using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using LMP.UI.Features.Shell;
using AsyncImageLoader;
using LMP.Core.Audio.Cache;
using System.Diagnostics;
using LMP.Core.Audio.Http;


namespace LMP;

public partial class App : Application
{
    private SplashWindow? _splash;
    private readonly CancellationTokenSource _appLifetimeCts = new();

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        Log.Info("Framework initialization completed.");

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Тема
            var themeManager = AppEntry.Services.GetRequiredService<ThemeManagerService>();
            themeManager.LoadAndApplyThemeOnStartup();

            // Локализация
            var bootstrap = AppEntry.Services.GetRequiredService<BootstrapSettings>();
            LocalizationService.Instance.Initialize(bootstrap.LanguageCode);
            Log.Info($"Localization: {bootstrap.LanguageCode}");

            // Splash Screen
            _splash = new SplashWindow();
            desktop.MainWindow = _splash;
            _splash.Show();

            // Даём UI-потоку отрисовать splash
            Dispatcher.UIThread.Post(() =>
            {
                _ = InitializeAppAsync(desktop);
            }, DispatcherPriority.Background);
        }

#if !WINDOWS
        // Этот код вообще не попадет в сборку под Windows
        InitializeTrayIcon();
#endif

        base.OnFrameworkInitializationCompleted();
    }

#if !WINDOWS
    private void InitializeTrayIcon()
    {
        var trayIcon = new TrayIcon
        {
            // В Avalonia 11+ иконки загружаются через AssetLoader
            Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://LMP/Assets/app.ico"))),
            ToolTipText = LocalizationService.Instance["Common_AppName"],
            IsVisible = false
        };

        var trayIcons = new TrayIcons { trayIcon };
        TrayIcon.SetIcons(this, trayIcons);
    }
#endif

    private async Task InitializeAppAsync(IClassicDesktopStyleApplicationLifetime desktop)
    {
        var stopwatch = Stopwatch.StartNew();
        var L = LocalizationService.Instance;

        try
        {
            // Отдаем приоритет UI, чтобы Splash мгновенно отрисовался
            await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Background);

            _splash?.SetProgress(5);
            _splash?.UpdateStatus(L["Splash_Initializing"]);

            // Auth init первым: LibraryService.InitializeAsync зависит от CurrentOwnerId
            var auth = AppEntry.Services.GetRequiredService<CookieAuthService>();
            await auth.InitializeAsync().ConfigureAwait(false);
            _splash?.SetProgress(10);

            // Audio Cache
            _splash?.UpdateStatus(L["Splash_InitAudioCache"]);
            var audioCacheTask = Task.Run(() =>
                AppEntry.Services.GetRequiredService<AudioCacheManager>());

            var statsLoadTask = Task.Run(() =>
            {
                CdnHostStatsStore.Load();
                SessionCacheStore.Load();
            });

            await Task.WhenAll(audioCacheTask, statsLoadTask).ConfigureAwait(false);

            var audioCacheManager = audioCacheTask.Result;
            AudioSourceFactory.InitializeGlobalCache(audioCacheManager);

            // Fire-and-forget: прогрев top CDN-кластеров пока UI продолжает инициализацию
            _ = CdnHostStatsStore.PreWarmTopClustersAsync(
                    SharedHttpClient.Instance,
                    _appLifetimeCts.Token);

            _splash?.SetProgress(20);

            _ = Task.Run(async () =>
            {
                try
                {
                    using var timer = new PeriodicTimer(TimeSpan.FromMinutes(5));
                    while (await timer.WaitForNextTickAsync(_appLifetimeCts.Token).ConfigureAwait(false))
                    {
                        CdnHostStatsStore.Save();
                        SessionCacheStore.Save();
                    }
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    Log.Warn($"[App] Periodic stats save failed: {ex.Message}");
                }
            });

            // Memory Monitor
            MemoryCleanupHelper.StartAutoCleanup();
            Log.Info("[Startup] Memory auto-cleanup scheduled");
            _splash?.SetProgress(25);

            // Library Service
            _splash?.UpdateStatus(L["Splash_LoadingLibrary"]);
            var library = AppEntry.Services.GetRequiredService<LibraryService>();
            await Task.Run(async () => await library.InitializeAsync());
            _splash?.SetProgress(45);

            // Применяем загруженные из БД настройки к аудио-движку
            var audioEngine = AppEntry.Services.GetRequiredService<AudioEngine>();
            audioEngine.InitializeVolumeFromSettings();

            // СИНХРОНИЗАЦИЯ ЯЗЫКА
            var savedLang = library.Settings.LanguageCode;
            var currentLang = L.CurrentLanguageCode;

            if (!string.IsNullOrEmpty(savedLang) && savedLang != currentLang)
            {
                if (savedLang == "en" && currentLang != "en")
                {
                    Log.Info($"First run: saving auto-detected '{currentLang}' to DB (was default 'en')");
                    library.Settings.LanguageCode = currentLang;
                }
                else if (savedLang != "en")
                {
                    Log.Info($"Applying saved language from DB: {savedLang}");
                    L.CurrentLanguage = savedLang;
                }
            }
            _splash?.SetProgress(50);

            // КРИТИЧНО: NotificationService после LibraryService
            var notifications = AppEntry.Services.GetRequiredService<NotificationService>();
            await notifications.InitializeAsync();
            Log.Info("[Startup] NotificationService history loaded");
            _splash?.SetProgress(55);

            // PlaybackErrorOrchestrator после NotificationService
            var orchestrator = AppEntry.Services.GetRequiredService<PlaybackErrorOrchestrator>();
            Log.Info("[Startup] PlaybackErrorOrchestrator ready");
            _splash?.SetProgress(60);

            // Image Cache
            _splash?.UpdateStatus(L["Splash_PreparingImages"]);
            var imageCache = await Task.Run(() =>
                AppEntry.Services.GetRequiredService<ImageCacheService>());

            var oldLoader = ImageLoader.AsyncImageLoader;
            ImageLoader.AsyncImageLoader = new CachedImageLoader(imageCache);
            if (oldLoader is IDisposable disposableLoader)
                disposableLoader.Dispose();

            _splash?.SetProgress(70);

            // YouTube Provider
            _splash?.UpdateStatus(L["Splash_ConnectingYouTube"]);
            var youtube = AppEntry.Services.GetRequiredService<Lazy<YoutubeProvider>>();

            _ = Task.Run(async () =>
            {
                try
                {
                    await youtube.Value.InitializeAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Log.Warn($"[YouTube] Background init failed: {ex.Message}");
                }
            });

            _splash?.SetProgress(80);

            // Create Main Window
            _splash?.UpdateStatus(L["Splash_BuildingInterface"]);

            MainWindow? mainWindow = null;
            MainWindowViewModel? mainWindowVM = null;

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                mainWindowVM = AppEntry.Services.GetRequiredService<MainWindowViewModel>();
                mainWindow = new MainWindow { DataContext = mainWindowVM };
            });
            _splash?.SetProgress(93);

            // Ready!
            _splash?.UpdateStatus(L["Splash_Ready"]);
            _splash?.SetProgress(100);

            // Switch Windows - Без искусственной паузы MinSplashTimeMs!
            await Dispatcher.UIThread.InvokeAsync(async () =>
            {
                desktop.MainWindow = mainWindow;
                mainWindow?.Show();
                mainWindowVM?.NotifyWindowShown();
                _splash?.Close();
                _splash = null;

                // Если произошла миграция со старой версии (v1/v2) на v3
                if (AppEntry.WasMigratedFromLegacy)
                {
                    try
                    {
                        var notifications = AppEntry.Services.GetRequiredService<NotificationService>();
                        var dialogService = AppEntry.Services.GetRequiredService<DialogService>();

                        // Отправляем предупреждающий Toast
                        await notifications.ShowToastAsync(
                            "Dialog_Warning_Title",
                            "Library_LegacyPlaylists_Message",
                            NotificationSeverity.Warning,
                            durationMs: 12000);

                        // Блокируем интерфейс модальным оверлеем
                        await dialogService.ShowLegacyMigrationAlertAsync(30);
                    }
                    catch (Exception ex)
                    {
                        Log.Error($"[Startup] Failed to show legacy warning: {ex.Message}");
                    }
                }
            });

            Log.Info($"Main window ready. Total splash time: {stopwatch.ElapsedMilliseconds}ms");

            // Post-Startup GC Compaction
            _ = Task.Run(async () =>
            {
                await Task.Delay(2500);
                GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
                GC.WaitForPendingFinalizers();
                Log.Info("[Memory] Post-startup GC compaction complete. Cold memory reclaimed.");
            });

            // Shutdown Handler (исправленный детерминированный асинхронный цикл)
            bool isCleaningUp = false;
            desktop.ShutdownRequested += (sender, e) =>
            {
                if (isCleaningUp) return;

                // Отменяем немедленное синхронное завершение процесса ОС
                e.Cancel = true;
                isCleaningUp = true;

                // Перенаправляем выполнение очистки в очередь UI-потока с высоким приоритетом
                Dispatcher.UIThread.Post(async () =>
                {
                    try
                    {
                        _appLifetimeCts.Cancel();

                        // Сохраняем статистику до освобождения ресурсов менеджера кэша
                        CdnHostStatsStore.Save();
                        SessionCacheStore.Save();

                        LocalAuthServer.DisposeIfCreated();
                        MemoryCleanupHelper.Dispose();

                        // Асинхронно высвобождаем аудиодвижок, кэш и гарантированно сбрасываем настройки в SQLite
                        await audioEngine.DisposeAsync().ConfigureAwait(false);
                        await audioCacheManager.DisposeAsync().ConfigureAwait(false);
                        await library.DisposeAsync().ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        Log.Error($"Shutdown error during async cleanup: {ex.Message}");
                    }
                    finally
                    {
                        _appLifetimeCts.Dispose();

                        // Завершаем работу приложения (force shutdown в обход повторного вызова событий)
                        desktop.Shutdown();
                    }
                }, DispatcherPriority.Normal);
            };

#if DEBUG
            this.AttachDeveloperTools();

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                mainWindow?.KeyDown += (s, e) =>
                    {
                        if (e.Key == Avalonia.Input.Key.F9)
                            new UI.Features.Debug.DebugWindow().Show();
                    };
            });
#endif
        }
        catch (Exception ex)
        {
            Log.Fatal($"Initialization failed: {ex.Message}\n{ex.StackTrace}");

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                _splash?.UpdateStatus(string.Format(L["Splash_Error"], ex.Message));
            });

            await Task.Delay(3000);
        }
    }
}