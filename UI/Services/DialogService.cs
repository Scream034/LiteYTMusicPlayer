using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using LMP.Core.Youtube.Exceptions;
using LMP.Core.Youtube.Videos;
using LMP.Core.Youtube.Search;
using LMP.UI.Dialogs;
using LMP.UI.Dialogs.Content;
using LMP.UI.Features.Shell;

namespace LMP.UI.Services;

/// <summary>
/// Централизованный сервис для показа диалоговых окон.
/// 
/// <para><b>Архитектура:</b></para>
/// <list type="bullet">
///   <item><b>Overlay-диалоги</b> — рендерятся через DialogHost поверх контента,
///     но ПОД TopBar и PlayerBar. TopBar, уведомления и кнопки окна остаются активными.
///     Навигация блокируется автоматически.</item>
///   <item><b>Модальные окна</b> — BotDetection и StreamUnavailable.
///     Блокируют ВСЁ приложение. Используются только для критичных ошибок.</item>
/// </list>
/// 
/// <para><b>Паттерн для overlay-диалогов:</b></para>
/// <code>
/// 1. Создать Content/ViewModel с callback'ом (OnResult / OnClose)
/// 2. Установить callback → tcs.TrySetResult() + dialogHost.CloseDialog()
/// 3. Вызвать dialogHost.ShowAsync() (fire-and-forget)
/// 4. await tcs.Task для получения результата
/// </code>
/// </summary>
public sealed class DialogService
{
    private static LocalizationService L => LocalizationService.Instance;

    private readonly CookieAuthService _authService;
    private readonly YoutubeUserDataService _youtubeUserDataService;
    private readonly LocalAuthServer _localAuthServer;
    private readonly Func<DialogHostViewModel> _getDialogHost;

    /// <param name="authService">Сервис авторизации — для проверки состояния входа в YouTube.</param>
    /// <param name="getDialogHost">
    /// Lazy accessor для DialogHostViewModel.
    /// Используется <c>Func</c> вместо прямой инъекции из-за циклической зависимости:
    /// DialogService создаётся раньше MainWindowViewModel.
    /// </param>
    public DialogService(
        CookieAuthService authService,
        YoutubeUserDataService youtubeUserDataService,
        LocalAuthServer localAuthServer,
        Func<DialogHostViewModel> getDialogHost)
    {
        _authService = authService;
        _youtubeUserDataService = youtubeUserDataService;
        _localAuthServer = localAuthServer;
        _getDialogHost = getDialogHost;
    }

    #region Helpers

    /// <summary>
    /// Получает главное окно если оно загружено и видимо.
    /// Используется только для модальных диалогов (BotDetection, StreamUnavailable).
    /// </summary>
    private static Window? GetMainWindow()
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var window = desktop.MainWindow;
            if (window is { IsLoaded: true, IsVisible: true })
                return window;
        }
        return null;
    }

    /// <summary>
    /// Безопасный показ модального Window-based диалога.
    /// Включает Task.Yield() для корректной работы с Avalonia event loop.
    /// </summary>
    private static async Task<T?> ShowModalSafeAsync<T>(Window dialog, Window owner)
    {
        try
        {
            await Task.Yield();
            return await dialog.ShowDialog<T?>(owner);
        }
        catch (Exception ex)
        {
            Log.Error($"[Dialog] Modal dialog error: {ex.Message}");
            return default;
        }
    }

    #endregion

    #region Generic Choice Dialog (Overlay)

    /// <summary>
    /// Универсальный диалог выбора с произвольным набором кнопок и опциональным чекбоксом.
    /// </summary>
    public async Task<ChoiceResult<T>?> ShowChoiceAsync<T>(
        string title,
        string? message,
        IReadOnlyList<ChoiceOption<T>> options,
        string? cancelText = null,
        string? checkBoxText = null)
    {
        var host = _getDialogHost();
        var tcs = new TaskCompletionSource<ChoiceResult<T>?>();

        var vm = ChoiceDialogViewModel.Create(
            title: title,
            message: message,
            options: options,
            onResult: (value, isChecked) =>
            {
                // Сначала инициируем закрытие диалога, предотвращая наложение на новые TCS сессии
                host.CloseDialog(value);
                tcs.TrySetResult(new ChoiceResult<T>(value, isChecked));
            },
            onCancel: () =>
            {
                host.CloseDialog();
                tcs.TrySetResult(null);
            },
            cancelText: cancelText,
            checkBoxText: checkBoxText);

        _ = host.ShowAsync<object>(vm);
        return await tcs.Task;
    }

    #endregion

    #region Basic Dialogs (Overlay)

    /// <summary>
    /// Диалог выбора YouTube аккаунта (основной/бренд).
    /// </summary>
    public async Task<YoutubeAccountItem?> ShowAccountSelectionDialogAsync(IEnumerable<YoutubeAccountItem> accounts)
    {
        var host = _getDialogHost();
        var tcs = new TaskCompletionSource<YoutubeAccountItem?>();

        var vm = new AccountSelectionDialogViewModel(accounts, _authService.State.AuthUser)
        {
            OnResult = result =>
            {
                host.CloseDialog(result);
                tcs.TrySetResult(result);
            }
        };

        _ = host.ShowAsync<object>(vm);
        return await tcs.Task;
    }

    /// <summary>
    /// Показывает диалог автоматического скачивания, распаковки и ручной активации расширения LMP Auth.
    /// </summary>
    public async Task<bool> ShowExtensionInstallDialogAsync()
    {
        var host = _getDialogHost();
        var tcs = new TaskCompletionSource<bool>();

        var vm = new AuthDialogViewModel(_authService, _youtubeUserDataService, _localAuthServer)
        {
            OnResult = result =>
            {
                host.CloseDialog(result);
                tcs.TrySetResult(result);
            }
        };

        _ = host.ShowAsync<object>(vm);
        return await tcs.Task;
    }

    /// <summary>
    /// Показывает диалог подтверждения (Yes/No, OK/Cancel).
    /// </summary>
    public async Task<bool> ConfirmAsync(
        string title, string message,
        string confirmText, string cancelText)
    {
        var host = _getDialogHost();
        var tcs = new TaskCompletionSource<bool>();

        var content = new ConfirmDialogContent(
            title, message, confirmText, cancelText,
            onResult: result =>
            {
                host.CloseDialog(result);
                tcs.TrySetResult(result);
            });

        _ = host.ShowAsync<object>(content);
        return await tcs.Task;
    }

    /// <summary>
    /// Показывает диалог подтверждения со стандартными кнопками OK / Cancel.
    /// </summary>
    public Task<bool> ConfirmAsync(string title, string message) =>
        ConfirmAsync(title, message, L["Common_OK"], L["Common_Cancel"]);

    /// <summary>
    /// Показывает информационный диалог с одной кнопкой.
    /// </summary>
    public async Task ShowInfoAsync(string title, string message, string buttonText)
    {
        var host = _getDialogHost();
        var tcs = new TaskCompletionSource<object?>();

        var content = new InfoDialogContent(
            title, message, buttonText,
            onClose: () =>
            {
                host.CloseDialog();
                tcs.TrySetResult(null);
            });

        _ = host.ShowAsync<object>(content);
        await tcs.Task;
    }

    /// <summary>
    /// Показывает информационный диалог со стандартной кнопкой OK.
    /// </summary>
    public Task ShowInfoAsync(string title, string message) =>
        ShowInfoAsync(title, message, L["Common_OK"]);

    /// <summary>
    /// Показывает диалог ввода текста.
    /// </summary>
    public async Task<string?> ShowInputAsync(
        string title, string prompt, string? watermark = null)
    {
        var host = _getDialogHost();
        var tcs = new TaskCompletionSource<string?>();

        var content = new InputDialogContent(
            title, prompt,
            watermark ?? L["Input_Watermark"],
            onResult: result =>
            {
                host.CloseDialog(result);
                tcs.TrySetResult(result);
            });

        _ = host.ShowAsync<object>(content);
        return await tcs.Task;
    }

    /// <summary>
    /// Показывает системный диалог выбора папки.
    /// Это единственный диалог использующий нативный OS picker.
    /// </summary>
    public static async Task<string?> SelectFolderAsync(string? startPath = null)
    {
        return await Dispatcher.UIThread.InvokeAsync(async () =>
        {
            var window = GetMainWindow();
            if (window == null) return null;

            var storage = window.StorageProvider;
            IStorageFolder? suggested = null;

            if (!string.IsNullOrEmpty(startPath) && Directory.Exists(startPath))
                suggested = await storage.TryGetFolderFromPathAsync(startPath);

            var result = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = L["Dialog_SelectDownloadFolder_Title"],
                SuggestedStartLocation = suggested,
                AllowMultiple = false
            });

            return result.Count > 0 ? result[0].Path.LocalPath : null;
        });
    }

    #endregion

    #region Close Action Dialog

    /// <summary>
    /// Показывает диалог "Что сделать при закрытии?" через универсальный ChoiceDialog.
    /// </summary>
    /// <returns>Результат выбора или null если отменено.</returns>
    public async Task<ChoiceResult<CloseAction>?> ShowCloseActionDialogAsync()
    {
        return await ShowChoiceAsync(
            title: L["Dialog_CloseAction_Title"] ?? "Close application?",
            message: L["Dialog_CloseAction_Message"] ?? "What would you like to do?",
            options:
            [
                new ChoiceOption<CloseAction>
                {
                    Text = L["Dialog_CloseAction_Tray"] ?? "Minimize to tray",
                    Value = CloseAction.MinimizeToTray,
                    IsPrimary = true
                },
                new ChoiceOption<CloseAction>
                {
                    Text = L["Dialog_CloseAction_Exit"] ?? "Exit",
                    Value = CloseAction.Exit
                }
            ],
            cancelText: L["Common_Cancel"] ?? "Cancel",
            checkBoxText: L["Dialog_CloseAction_Remember"] ?? "Remember my choice");
    }

    #endregion

    #region Playlist Dialogs (Overlay)

    /// <summary>
    /// Диалог создания нового плейлиста.
    /// </summary>
    public async Task<CreatePlaylistResult?> ShowCreatePlaylistDialogAsync()
    {
        var host = _getDialogHost();
        var tcs = new TaskCompletionSource<CreatePlaylistResult?>();

        var vm = new CreatePlaylistDialogViewModel(_authService.IsAuthenticated)
        {
            OnResult = result =>
            {
                host.CloseDialog(result);
                tcs.TrySetResult(result);
            }
        };

        _ = host.ShowAsync<object>(vm);
        return await tcs.Task;
    }

    /// <summary>
    /// Диалог удаления плейлиста.
    /// </summary>
    public async Task<DeletePlaylistResult?> ShowDeletePlaylistDialogAsync(
        Playlist playlist, bool isAuthenticated)
    {
        var host = _getDialogHost();
        var tcs = new TaskCompletionSource<DeletePlaylistResult?>();

        var vm = new DeletePlaylistDialogViewModel(playlist, isAuthenticated)
        {
            OnResult = result =>
            {
                host.CloseDialog(result);
                tcs.TrySetResult(result);
            }
        };

        _ = host.ShowAsync<object>(vm);
        return await tcs.Task;
    }

    /// <summary>
    /// Диалог «Добавить трек в плейлист» — список плейлистов с чекбоксами.
    /// </summary>
    public async Task<List<string>> ShowAddToPlaylistDialogAsync(TrackInfo track)
    {
        var host = _getDialogHost();
        var tcs = new TaskCompletionSource<List<string>>();

        var library = Microsoft.Extensions.DependencyInjection
            .ServiceProviderServiceExtensions
            .GetRequiredService<LibraryService>(AppEntry.Services);
        var playlists = await library.GetAllPlaylistsAsync();

        var vm = new AddToPlaylistDialogViewModel(track, playlists)
        {
            OnResult = result =>
            {
                host.CloseDialog(result);
                tcs.TrySetResult(result);
            }
        };

        _ = host.ShowAsync<object>(vm);
        return await tcs.Task;
    }

    /// <summary>
    /// Диалог редактирования плейлиста.
    /// </summary>
    public async Task<EditPlaylistResult?> ShowEditPlaylistDialogAsync(Playlist playlist)
    {
        var host = _getDialogHost();
        var tcs = new TaskCompletionSource<EditPlaylistResult?>();

        IReadOnlyList<TrackInfo>? tracks = null;
        try
        {
            var library = Microsoft.Extensions.DependencyInjection
                .ServiceProviderServiceExtensions
                .GetRequiredService<LibraryService>(AppEntry.Services);
            var loaded = await library.GetPlaylistTracksAsync(playlist.Id, limit: 200);
            if (loaded.Count > 0) tracks = loaded;
        }
        catch (Exception ex)
        {
            Log.Warn($"[DialogService] CoverPicker tracks load failed: {ex.Message}");
        }

        var vm = new EditPlaylistDialogViewModel(
            playlist, _authService.IsAuthenticated, tracks)
        {
            OnResult = result =>
            {
                host.CloseDialog(result);
                tcs.TrySetResult(result);
            }
        };

        _ = host.ShowAsync<object>(vm);
        return await tcs.Task;
    }

    #endregion

    #region Sync Dialogs (Overlay)

    /// <summary>
    /// Диалог массовой синхронизации.
    /// </summary>
    public async Task<List<SyncDecision>> ShowSyncSelectionAsync(
        IEnumerable<PlaylistSearchResult> items,
        ISet<string> existingLocalNames,
        IReadOnlyDictionary<string, int>? trackCounts = null)
    {
        var host = _getDialogHost();
        var tcs = new TaskCompletionSource<List<SyncDecision>>();

        var vm = new SyncSelectionViewModel(
            items, existingLocalNames, trackCounts)
        {
            OnResult = result =>
            {
                host.CloseDialog(result);
                tcs.TrySetResult(result);
            }
        };

        _ = host.ShowAsync<object>(vm);
        return await tcs.Task;
    }

    /// <summary>
    /// Диалог синхронизации одного плейлиста.
    /// </summary>
    public async Task<PlaylistSyncOptions?> ShowPlaylistSyncDialogAsync(
        PlaylistSyncPreview preview)
    {
        var host = _getDialogHost();
        var tcs = new TaskCompletionSource<PlaylistSyncOptions?>();

        var vm = new SyncPlaylistDialogViewModel(preview)
        {
            OnResult = result =>
            {
                host.CloseDialog(result);
                tcs.TrySetResult(result);
            }
        };

        _ = host.ShowAsync<object>(vm);
        return await tcs.Task;
    }

    #endregion

    #region Critical Dialogs (Modal Windows)

    /// <summary>
    /// Диалог ошибки недоступности стрима.
    /// <para><b>МОДАЛЬНЫЙ:</b> блокирует ВСЁ окно.</para>
    /// </summary>
    public static async Task ShowStreamUnavailableAsync(StreamUnavailableException exception)
    {
        await Dispatcher.UIThread.InvokeAsync(async () =>
        {
            var window = GetMainWindow();
            if (window == null) return;

            var dialog = new StreamUnavailableDialog();
            dialog.ConfigureForException(exception);
            await ShowModalSafeAsync<object>(dialog, window);
        });
    }

    /// <summary>
    /// Диалог общей ошибки воспроизведения.
    /// <para><b>МОДАЛЬНЫЙ:</b> блокирует ВСЁ окно.</para>
    /// </summary>
    public static async Task ShowPlaybackErrorAsync(
        string videoId, string errorMessage, Exception? exception = null)
    {
        await Dispatcher.UIThread.InvokeAsync(async () =>
        {
            var window = GetMainWindow();
            if (window == null) return;

            var dialog = new StreamUnavailableDialog();
            dialog.ConfigureForError(videoId, errorMessage, exception);
            await ShowModalSafeAsync<object>(dialog, window);
        });
    }

    /// <summary>
    /// Диалог требования авторизации.
    /// </summary>
    public Task ShowLoginRequiredAsync(LoginRequiredException exception) =>
        ShowInfoAsync(
            L["Dialog_Login_Title"],
            L[exception.GetLocalizationKey()],
            L["Common_OK"]);

    /// <summary>
    /// Показывает модальное оверлей-предупреждение о необходимости повторной синхронизации плейлистов после миграции БД.
    /// Полностью блокирует интерфейс на указанное время (обратный отсчет) с помощью DialogHost.
    /// </summary>
    public async Task ShowLegacyMigrationAlertAsync(int countdownSeconds)
    {
        var host = _getDialogHost();
        var tcs = new TaskCompletionSource<object?>();

        var vm = new MigrationWarningDialogViewModel(
            title: L["Dialog_Warning_Title"] ?? "Warning",
            message: L["Library_LegacyPlaylists_Message"] ?? "Please re-sync your playlists.",
            countdownSeconds: countdownSeconds,
            onClose: () =>
            {
                host.CloseDialog();
                tcs.TrySetResult(null);
            });

        _ = host.ShowAsync<object>(vm);
        await tcs.Task;
    }

    #endregion
}

/// <summary>
/// Результат диалога выбора: выбранное значение + состояние чекбокса.
/// </summary>
/// <typeparam name="T">Тип выбранного значения.</typeparam>
public sealed record ChoiceResult<T>(T Value, bool IsChecked)
{
    public override string ToString()
    {
        return $"Value: {Value}, IsChecked: {IsChecked}";
    }
}