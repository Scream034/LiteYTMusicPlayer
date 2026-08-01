using System.Collections.ObjectModel;
using System.Net;
using System.Reactive;
using System.Reactive.Linq;
using Avalonia;
using Avalonia.Media;
using Avalonia.Threading;
using LMP.Core.Audio.Cache;
using LMP.Core.Audio.Normalization;
using LMP.Core.Youtube.Exceptions;
using LMP.Core.Youtube.Utils;
using LMP.UI.Dialogs;
using LMP.UI.Features.Shell;
using Microsoft.Extensions.DependencyInjection;
using ReactiveUI;


namespace LMP.UI.Features.Settings;

/// <summary>
/// Обёртка над произвольным значением с именем для отображения в ComboBox.
/// <para>
/// ToString() возвращает Name — ComboBox вызывает его напрямую,
/// без создания DataTemplate-контейнеров. Это устраняет утечку памяти
/// от DisplayMemberBinding → ControlTemplate binding → PointerDeferredContent.
/// </para>
/// </summary>
public sealed class LocalizedItem<T>(T value, string name)
{
    public T Value { get; } = value;
    public string Name { get; } = name;

    public override string ToString() => Name;
}

/// <summary>Пресеты количества bitmap-объектов в RAM-кэше изображений.</summary>
public enum ImageCachePreset { Custom, Low, Medium, High }

/// <summary>
/// ViewModel страницы настроек.
/// <para>
/// Архитектура: sidebar (ListBox с 9 items) + ContentControl справа.
/// В каждый момент в visual tree живёт одна страница (~10–15 контролов).
/// Все страницы получают DataContext = этот VM напрямую (без Owner.*).
/// </para>
/// </summary>
public sealed partial class SettingsViewModel : ViewModelBase, IDisposable, ISmoothTransitionViewModel
{
    /// <inheritdoc />
    protected override bool HandlesAccountChanges => true;

    private const int NavigationDebounceMs = 128;

    private readonly LibraryService _library;
    private readonly TrackRegistry _registry;
    private readonly SearchCacheService _searchCache;
    private readonly ImageCacheService _imageCache;
    private readonly ThemeManagerService _themeManager;
    private readonly CookieAuthService _auth;
    private readonly LocalAuthServer _localServer;
    private readonly DialogService _dialog;
    private readonly AudioEngine _audio;
    private readonly YoutubeProvider _youtube;
    private readonly YoutubeUserDataService _userData;
    private readonly NotificationService _notifications;

    private bool _isLoadingTheme;
    private bool _isUpdatingPreset;
    private bool _isLoadingSettings;
    private bool _isDisposed;
    /// <summary>
    /// Локальный признак наличия данных в памяти
    /// </summary>
    private bool _isDataLoaded;

    /// <summary>
    /// Признак готовности контента.
    /// <para>
    /// Пока <c>false</c> — показывается skeleton-заглушка.
    /// После <c>true</c> — sidebar + страница контента.
    /// </para>
    /// </summary>
    [Reactive] public partial bool IsContentReady { get; private set; }

    #region Sidebar

    /// <summary>
    /// Элементы sidebar — по одному на секцию настроек.
    /// <para>
    /// ListBox отображает их иконкой + названием через DataTemplate по DataType.
    /// ContentControl справа подбирает страницу по типу выбранного элемента.
    /// В каждый момент в visual tree — одна страница (~10–15 контролов).
    /// </para>
    /// </summary>
    public ObservableCollection<SettingsSidebarItemBase> SidebarItems { get; } = [];

    /// <summary>Текущий выбранный элемент sidebar — определяет какая страница отображается.</summary>
    [Reactive] public partial SettingsSidebarItemBase? SelectedSidebarItem { get; set; }

    /// <summary>
    /// Управляет видимостью текстовых лейблов в sidebar.
    /// <para>
    /// <c>true</c>  — sidebar достаточно широкий, показываем иконку + текст.<br/>
    /// <c>false</c> — sidebar узкий, показываем только иконки (tooltip всегда виден).
    /// Значение устанавливается из code-behind при изменении ширины колонки GridSplitter-ом.
    /// </para>
    /// </summary>
    [Reactive] public partial bool IsSidebarExpanded { get; set; } = true;

    #endregion

    #region Account

    /// <summary>Признак авторизации пользователя через cookies.</summary>
    [Reactive] public partial bool IsAuthenticated { get; private set; }

    /// <summary>Имя пользователя или локализованная строка "не авторизован".</summary>
    public string AccountName => IsAuthenticated ? _auth.State.UserName : SL["Auth_NotSignedIn"];

    /// <summary>URL аватара или <c>null</c> если не авторизован — управляет видимостью Image/Icon.</summary>
    public string? AccountAvatarUrl => IsAuthenticated ? _auth.State.AvatarUrl : null;

    /// <summary>Email или локализованная строка "гость".</summary>
    public string AccountSubtitle => IsAuthenticated ? _auth.State.UserEmail : SL["Auth_Guest"];

    /// <summary>
    /// Указывает, выполняется ли в данный момент сетевая транзакция с аккаунтом (вход, смена канала, выход).
    /// </summary>
    [Reactive] public partial bool IsAccountLoading { get; private set; }

    #endregion

    #region Network

    /// <summary>Состояние последней проверки подключения.</summary>
    public enum NetworkStatusKind
    {
        /// <summary>Проверка ещё не выполнялась.</summary>
        Unknown,
        /// <summary>Проверка выполняется в данный момент.</summary>
        Checking,
        /// <summary>YouTube доступен напрямую.</summary>
        Ok,
        /// <summary>Нет подключения к интернету.</summary>
        NoInternet,
        /// <summary>Подключение идёт через VPN-интерфейс.</summary>
        VpnDetected,
        /// <summary>Подключение идёт через явно заданный прокси.</summary>
        ProxyActive,
        /// <summary>YouTube недоступен — ошибка сети или блокировка.</summary>
        Error
    }

    [Reactive]
    public partial NetworkStatusKind NetworkStatus { get; private set; }
    = NetworkStatusKind.Unknown;

    /// <summary>
    /// Цвет индикатора статуса сети.
    /// Читается из активной темы приложения — не хардкодим цвета.
    /// </summary>
    public Color NetworkStatusColor => NetworkStatus switch
    {
        NetworkStatusKind.Ok => ThemeManagerService.GetThemeColor("Accent"),
        NetworkStatusKind.VpnDetected => ThemeManagerService.GetThemeColor("SystemInfoBlue"),
        NetworkStatusKind.ProxyActive => ThemeManagerService.GetThemeColor("Accent"),
        NetworkStatusKind.Checking => ThemeManagerService.GetThemeColor("SystemWarnOrange"),
        NetworkStatusKind.NoInternet => ThemeManagerService.GetThemeColor("SystemError"),
        NetworkStatusKind.Error => ThemeManagerService.GetThemeColor("SystemError"),
        _ => ThemeManagerService.GetThemeColor("TextSecondary"),
    };

    [Reactive] public partial string NetworkStatusText { get; private set; } = "";

    /// <summary>Задержка последнего успешного запроса к YouTube в миллисекундах.</summary>
    [Reactive] public partial int NetworkLatencyMs { get; private set; }

    /// <summary>
    /// Возвращает <c>true</c> если задержка измерена и ненулевая.
    /// Управляет видимостью метки латентности без конвертеров на стороне XAML.
    /// </summary>
    public bool HasLatency => NetworkLatencyMs > 0;

    /// <summary>Флаг активной проверки подключения — блокирует повторный запуск.</summary>
    [Reactive] public partial bool IsNetworkTesting { get; private set; }

    /// <summary>Доступные профили скорости интернета для ComboBox.</summary>
    public ObservableCollection<LocalizedItem<InternetProfile>> InternetProfileOptions { get; } = [];

    /// <summary>Выбранный профиль скорости; синхронизируется с настройками через подписку.</summary>
    [Reactive] public partial LocalizedItem<InternetProfile>? SelectedInternetProfile { get; set; }

    [Reactive] public partial bool ProxyEnabled { get; set; }
    [Reactive] public partial string ProxyHost { get; set; } = "";
    [Reactive] public partial int ProxyPort { get; set; } = 8080;
    [Reactive] public partial bool ProxyAuth { get; set; }
    [Reactive] public partial string ProxyUser { get; set; } = "";
    [Reactive] public partial string ProxyPass { get; set; } = "";

    /// <summary>
    /// Признак того, что изменения сети требуют перезапуска.
    /// <para>Устанавливается при смене профиля, прокси или клиента.</para>
    /// </summary>
    [Reactive] public partial bool NetworkRestartRequired { get; set; }

    #endregion

    #region Storage

    /// <summary>Текущий путь к папке загрузок.</summary>
    [Reactive] public partial string DownloadPath { get; set; } = string.Empty;

    /// <summary>Пресеты количества bitmap-объектов в RAM для ComboBox.</summary>
    public ObservableCollection<LocalizedItem<ImageCachePreset>> ImageCachePresets { get; } = [];

    /// <summary>Выбранный пресет; <c>null</c> означает Custom (произвольное значение слайдера).</summary>
    [Reactive] public partial LocalizedItem<ImageCachePreset>? SelectedImageCachePreset { get; set; }

    /// <summary>Максимальное количество bitmap-объектов в RAM-кэше.</summary>
    [Reactive] public partial int MaxBitmapCacheItems { get; set; }

    [Reactive] public partial int ImageCacheLimitMb { get; set; }
    [Reactive] public partial int AudioCacheLimitMb { get; set; }
    [Reactive] public partial int DownloadedTracksLimitMb { get; set; }

    /// <summary>Статистика кэша изображений в формате "X MB / Y MB (N files, RAM: M)".</summary>
    [Reactive] public partial string ImageCacheStats { get; private set; } = "...";

    /// <summary>Статистика аудиокэша в формате "X MB / Y MB (N files)".</summary>
    [Reactive] public partial string AudioCacheStats { get; private set; } = "...";

    /// <summary>Доля занятого места в кэше изображений [0..1] для ProgressBar.</summary>
    [Reactive] public partial double ImageCacheUsagePercent { get; private set; }

    /// <summary>Доля занятого места в аудиокэше [0..1] для ProgressBar.</summary>
    [Reactive] public partial double AudioCacheUsagePercent { get; private set; }

    /// <summary>Статистика загрузок в формате "X MB / Y MB (N files)".</summary>
    [Reactive] public partial string DownloadsStats { get; private set; } = "...";

    /// <summary>Доля занятого места загрузками [0..1] для ProgressBar.</summary>
    [Reactive] public partial double DownloadsUsagePercent { get; private set; }

    /// <summary>Автоматически сохранять загрузки в папку Downloads.</summary>
    [Reactive] public partial bool AutoSaveToDownloads { get; set; }

    #endregion

    #region Theme

    /// <summary>Встроенные и пользовательские пресеты тем для ComboBox.</summary>
    public ObservableCollection<ThemeSettings> ThemePresets { get; } = [];

    /// <summary>Выбранный пресет темы; при смене — цвета применяются к color picker'ам.</summary>
    [Reactive] public partial ThemeSettings? SelectedPreset { get; set; }

    [Reactive] public partial Color AccentColor { get; set; }
    [Reactive] public partial Color BgPrimaryColor { get; set; }
    [Reactive] public partial Color BgSecondaryColor { get; set; }
    [Reactive] public partial Color BgElevatedColor { get; set; }
    [Reactive] public partial Color TextPrimaryColor { get; set; }
    [Reactive] public partial Color TextSecondaryColor { get; set; }

    /// <summary>
    /// Признак несохранённых изменений темы.
    /// <para>Управляет видимостью кнопок Apply / Reset.</para>
    /// </summary>
    [Reactive] public partial bool HasUnsavedThemeChanges { get; set; }

    #endregion

    #region Audio

    /// <summary>
    /// Обёрнутые значения AudioQualityPreference — ComboBox использует ToString()
    /// от LocalizedItem, DataTemplate и конвертер AudioQualityToString не нужны.
    /// </summary>
    public List<LocalizedItem<AudioQualityPreference>> QualityOptions { get; private set; } = [];

    /// <summary>Выбранный элемент качества; синхронизируется с настройками через подписку.</summary>
    [Reactive] public partial LocalizedItem<AudioQualityPreference>? SelectedQualityItem { get; set; }

    [Reactive] public partial int MaxVolumeLimit { get; set; }
    [Reactive] public partial float TargetGainDb { get; set; }
    [Reactive] public partial bool RememberTrackFormat { get; set; }
    [Reactive] public partial bool VolumeBoostEnabled { get; set; }
    [Reactive] public partial bool AudioNormalizationEnabled { get; set; }
    [Reactive] public partial float NormalizationTargetLufs { get; set; }
    [Reactive] public partial float NormalizationMaxGain { get; set; }

    /// <summary>Варианты кривой громкости для ComboBox.</summary>
    public ObservableCollection<LocalizedItem<VolumeCurveType>> VolumeCurveOptions { get; } = [];

    /// <summary>Выбранная кривая громкости; синхронизируется с настройками через подписку.</summary>
    [Reactive] public partial LocalizedItem<VolumeCurveType>? SelectedVolumeCurve { get; set; }

    /// <summary>Варианты поведения при ошибке воспроизведения для ComboBox.</summary>
    public ObservableCollection<LocalizedItem<PlaybackErrorBehavior>> ErrorBehaviorOptions { get; } = [];

    /// <summary>Выбранное поведение при ошибке; синхронизируется с настройками через подписку.</summary>
    [Reactive] public partial LocalizedItem<PlaybackErrorBehavior>? SelectedErrorBehavior { get; set; }

    [Reactive] public partial bool PlayErrorSound { get; set; }
    [Reactive] public partial bool SkipNTokenTracks { get; set; }

    /// <summary>Варианты режима нормализации для ComboBox.</summary>
    public ObservableCollection<LocalizedItem<NormalizationMode>> NormalizationModeOptions { get; } = [];

    /// <summary>Выбранный режим нормализации; синхронизируется с настройками через подписку.</summary>
    [Reactive] public partial LocalizedItem<NormalizationMode>? SelectedNormalizationMode { get; set; }

    /// <summary>
    /// Флаг программного отката при отмене выключения нормализации.
    /// <para>
    /// Без него отмена диалога приводит к повторному срабатыванию подписки
    /// на AudioNormalizationEnabled и бесконечному циклу подтверждений.
    /// </para>
    /// </summary>
    private bool _isRevertingNormalization;

    #endregion

    #region Playback & Failure Settings

    /// <summary>Варианты уведомлений n-токена для ComboBox.</summary>
    public ObservableCollection<LocalizedItem<NTokenNotificationMode>> NTokenNotificationOptions { get; } = [];

    /// <summary>Выбранный режим предупреждений расшифровки n-токена.</summary>
    [Reactive] public partial LocalizedItem<NTokenNotificationMode>? SelectedNTokenNotification { get; set; }

    /// <summary>Варианты поведения при сбое воспроизведения для ComboBox.</summary>
    public ObservableCollection<LocalizedItem<PlaybackFailureBehavior>> PlaybackFailureOptions { get; } = [];

    /// <summary>Выбранный режим поведения при фатальном сбое трека.</summary>
    [Reactive] public partial LocalizedItem<PlaybackFailureBehavior>? SelectedPlaybackFailure { get; set; }

    [Reactive] public partial bool IsPlaybackFailureActionEnabled { get; private set; } = true;

    #endregion

    #region UI & Behavior

    [Reactive] public partial bool DiscordRpcEnabled { get; set; }
    [Reactive] public partial bool AutoPlayOnPaste { get; set; }
    [Reactive] public partial int SearchBatchSize { get; set; }
    [Reactive] public partial bool EnableSearchCache { get; set; }
    [Reactive] public partial int SearchCacheTtlMinutes { get; set; }

    /// <summary>Список доступных языков — статический, берётся из LocalizationService.</summary>
    public static List<LanguageItem> Languages => LocalizationService.Instance.AvailableLanguages;

    /// <summary>Выбранный язык; при смене применяется немедленно.</summary>
    [Reactive] public partial LanguageItem? SelectedLanguage { get; set; }

    /// <summary>Варианты действия при закрытии окна для ComboBox.</summary>
    public ObservableCollection<LocalizedItem<CloseAction>> CloseActionOptions { get; } = [];

    /// <summary>Выбранное действие при закрытии; синхронизируется с настройками через подписку.</summary>
    [Reactive] public partial LocalizedItem<CloseAction>? SelectedCloseAction { get; set; }

    [Reactive] public partial bool MinimizeToTray { get; set; }

    /// <summary>Флаг однократной инициализации подписок.</summary>
    private bool _subscriptionsSetup;

    #endregion

    #region Memory

    /// <summary>
    /// Элемент пресета GPU-кэша.
    /// <para>ToString() → Name: ComboBox не создаёт DataTemplate-контейнеры.</para>
    /// </summary>
    public sealed record GpuCachePresetItem(long Mb, string Name)
    {
        public override string ToString() => Name;
    }

    /// <summary>Пресеты размера GPU-кэша текстур для ComboBox.</summary>
    public ObservableCollection<GpuCachePresetItem> GpuCachePresets { get; } = [];

    /// <summary>Выбранный пресет GPU-кэша; при смене требует перезапуска.</summary>
    [Reactive] public partial GpuCachePresetItem? SelectedGpuCachePreset { get; set; }

    /// <summary>Признак того, что изменение GPU-кэша требует перезапуска приложения.</summary>
    [Reactive] public partial bool GpuCacheRestartRequired { get; private set; }

    [Reactive] public partial bool AutoMemoryCleanupEnabled { get; set; }
    [Reactive] public partial int MemoryCleanupIntervalMinutes { get; set; }
    [Reactive] public partial int MemoryPressureThresholdMb { get; set; }

    /// <summary>Принудительная очистка памяти прямо сейчас (aggressive GC).</summary>
    public ReactiveCommand<Unit, Unit> CleanupMemoryNowCommand { get; }

    #endregion

    #region Commands

    public ReactiveCommand<Unit, Unit> BrowseDownloadPathCommand { get; }
    public ReactiveCommand<Unit, Unit> ClearHistoryCommand { get; }
    public ReactiveCommand<Unit, Unit> ResetLibraryCommand { get; }
    public ReactiveCommand<Unit, Unit> LoginCommand { get; }
    public ReactiveCommand<Unit, Unit> LogoutCommand { get; }
    public ReactiveCommand<Unit, Unit> SwitchAccountCommand { get; }
    public ReactiveCommand<Unit, Unit> ClearImageCacheCommand { get; }
    public ReactiveCommand<Unit, Unit> ClearAudioCacheCommand { get; }
    public ReactiveCommand<Unit, Unit> ApplyThemeCommand { get; }
    public ReactiveCommand<Unit, Unit> ResetThemeCommand { get; }
    public ReactiveCommand<Unit, Unit> ClearDownloadsCommand { get; }
    public ReactiveCommand<Unit, Unit> ShowNormalizationInfoCommand { get; }
    public ReactiveCommand<Unit, Unit> RefreshProfileCommand { get; }
    public ReactiveCommand<Unit, Unit> TestNetworkCommand { get; }

    #endregion

    /// <summary>
    /// Создаёт VM настроек и инициализирует команды и sidebar.
    /// <para>
    /// Тяжёлая инициализация (загрузка настроек, подписки) вынесена
    /// в <see cref="OnNavigatedToAsync"/> чтобы не блокировать UI при навигации.
    /// </para>
    /// </summary>
    public SettingsViewModel(
        LibraryService library,
        TrackRegistry registry,
        SearchCacheService searchCache,
        ImageCacheService imageCache,
        ThemeManagerService themeManager,
        CookieAuthService auth,
        DialogService dialog,
        AudioEngine audio,
        LocalAuthServer localServer,
        YoutubeProvider youtube,
        YoutubeUserDataService userData,
        NotificationService notifications)
    {
        _library = library;
        _registry = registry;
        _searchCache = searchCache;
        _imageCache = imageCache;
        _themeManager = themeManager;
        _auth = auth;
        _dialog = dialog;
        _audio = audio;
        _localServer = localServer;
        _youtube = youtube;
        _userData = userData;
        _notifications = notifications;

        LoginCommand = CreateCommand(ReactiveCommand.CreateFromTask(LoginAsync));
        SwitchAccountCommand = CreateCommand(ReactiveCommand.CreateFromTask(SwitchAccountAsync));
        LogoutCommand = CreateCommand(ReactiveCommand.CreateFromTask(LogoutAsync));
        BrowseDownloadPathCommand = CreateCommand(ReactiveCommand.CreateFromTask(BrowseDownloadPathAsync));
        ClearHistoryCommand = CreateCommand(ReactiveCommand.CreateFromTask(ClearHistoryAsync));
        ResetLibraryCommand = CreateCommand(ReactiveCommand.CreateFromTask(ResetLibraryAsync));
        ClearImageCacheCommand = CreateCommand(ReactiveCommand.CreateFromTask(ClearImageCacheAsync));
        ClearAudioCacheCommand = CreateCommand(ReactiveCommand.CreateFromTask(ClearAudioCacheAsync));
        ApplyThemeCommand = CreateCommand(ReactiveCommand.Create(ApplyTheme));
        ResetThemeCommand = CreateCommand(ReactiveCommand.Create(ResetTheme));
        ClearDownloadsCommand = CreateCommand(ReactiveCommand.CreateFromTask(ClearDownloadsAsync));
        CleanupMemoryNowCommand = CreateCommand(ReactiveCommand.Create(
            () => MemoryCleanupHelper.PerformCleanup(aggressive: true)));
        ShowNormalizationInfoCommand = CreateCommand(ReactiveCommand.CreateFromTask(ShowNormalizationInfoAsync));
        RefreshProfileCommand = CreateCommand(ReactiveCommand.CreateFromTask(RefreshProfileAsync));
        TestNetworkCommand = CreateCommand(ReactiveCommand.CreateFromTask(TestNetworkAsync));

        SidebarItems =
        [
            new AccountLanguageSidebarItem(this),
            new NetworkSidebarItem(this),
            new StorageCacheSidebarItem(this),
            new MemorySidebarItem(this),
            new AppearanceSidebarItem(this),
            new AudioSidebarItem(this),
            new PlaybackSidebarItem(this),
            new WindowBehaviorSidebarItem(this),
            new GeneralSidebarItem(this),
        ];

        // Подписка на изменение кэша для живого обновления статистики на странице настроек
        var cache = AudioSourceFactory.GlobalCache;
        if (cache != null)
        {
            Observable.FromEvent<Action<string, AudioFormat, int, bool>, Unit>(
                    h => (trackId, format, bitrate, isDownloaded) => h(Unit.Default),
                    h => cache.OnFormatCached += h,
                    h => cache.OnFormatCached -= h)
                .ObserveOn(RxSchedulers.MainThreadScheduler)
                .Subscribe(_ => UpdateCacheStats())
                .DisposeWith(Disposables);

            Observable.FromEvent(
                    h => cache.OnCacheCleared += h,
                    h => cache.OnCacheCleared -= h)
                .ObserveOn(RxSchedulers.MainThreadScheduler)
                .Subscribe(_ => UpdateCacheStats())
                .DisposeWith(Disposables);
        }

        this.WhenAnyValue(x => x.DownloadedTracksLimitMb)
            .Skip(1)
            .Where(_ => !_isLoadingSettings)
            .Throttle(TimeSpan.FromMilliseconds(500))
            .Subscribe(v =>
            {
                _library.UpdateSettings(s => s.Storage.DownloadedTracksLimitMb = v);
                UpdateCacheStats();
            })
            .DisposeWith(Disposables);

        this.WhenAnyValue(x => x.AutoSaveToDownloads)
            .Skip(1)
            .Where(_ => !_isLoadingSettings)
            .Subscribe(v => _library.UpdateSettings(s => s.Storage.AutoSaveToDownloads = v))
            .DisposeWith(Disposables);

        LocalizationService.Instance.LanguageChanged += OnLanguageChanged;
    }

    /// <inheritdoc />
    public void PrepareForTransition()
    {
        IsContentReady = false; // Скрываем тяжелый сайдбар и страницы настроек
    }

    /// <summary>
    /// Вызывается при переходе на страницу настроек.
    /// Перезагружает актуальные параметры из сервиса библиотеки для синхронизации с внешними изменениями.
    /// </summary>
    public override async Task OnNavigatedToAsync()
    {
        if (_isDisposed) return;

        if (_isDataLoaded)
        {
            // Синхронизируем свойства вью-модели со свежими настройками из сервиса (например, после диалога закрытия)
            LoadAllSettings();
            IsContentReady = true;
            return;
        }

        await Task.Delay(NavigationDebounceMs);
        if (_isDisposed) return;

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            InitializeLists();
            LoadAllSettings();
            UpdateCacheStats();
            SetupSubscriptions();

            SelectedSidebarItem ??= SidebarItems.FirstOrDefault();

            _isDataLoaded = true;
            IsContentReady = true;
        });

        MemoryCleanupHelper.PerformCleanup(aggressive: false);
    }

    /// <inheritdoc />
    protected override void OnAccountChanged()
    {
        base.OnAccountChanged();

        _isDataLoaded = false;

        if (IsContentReady)
        {
            Log.Info("[Settings] Account changed while settings page was open. Re-loading configuration in real-time.");
            IsContentReady = false;
            LoadAllSettings();
            UpdateCacheStats();
            IsContentReady = true;
        }
    }

    /// <summary>При смене языка — перестраиваем все локализованные списки ComboBox.</summary>
    private void OnLanguageChanged(object? sender, string e) => RefreshLocalizedLists();

    private void InitializeLists()
    {
        RefreshThemePresets();
        RefreshLocalizedLists();
        InitGpuCachePresets();
    }

    /// <summary>
    /// Инициализирует пресеты GPU-кэша.
    /// <para>
    /// Покрытие обложек 120px: 1 текстура ≈ 56KB.
    /// 32MB  → ~570  обложек  (минимум, слабые GPU/iGPU).
    /// 64MB  → ~1140 обложек  (рекомендуется, дефолт).
    /// 128MB → ~2280 обложек  (мощные GPU).
    /// 256MB → ~4560 обложек  (максимум).
    /// </para>
    /// </summary>
    private void InitGpuCachePresets()
    {
        GpuCachePresets.Clear();
        GpuCachePresets.Add(new GpuCachePresetItem(32, $"32 MB  ({SL["Cache_Low"]})"));
        GpuCachePresets.Add(new GpuCachePresetItem(64, $"64 MB  ({SL["Cache_Medium"]}) ✓"));
        GpuCachePresets.Add(new GpuCachePresetItem(128, $"128 MB ({SL["Cache_High"]})"));
        GpuCachePresets.Add(new GpuCachePresetItem(256, $"256 MB ({SL["Cache_Ultra"]})"));

        var currentMb = BootstrapSettings.Current.GpuTextureCacheMb;
        SelectedGpuCachePreset = GpuCachePresets.FirstOrDefault(x => x.Mb == currentMb)
                              ?? GpuCachePresets[1];
    }

    /// <summary>
    /// Перестраивает список встроенных и пользовательских пресетов тем.
    /// <para>
    /// Пользовательский пресет добавляется только если текущая тема
    /// не совпадает ни с одним встроенным по ключевым цветам.
    /// </para>
    /// </summary>
    private void RefreshThemePresets()
    {
        ThemePresets.Clear();

        foreach (var preset in ThemeManagerService.GetBuiltInPresets())
            ThemePresets.Add(preset);

        var saved = _themeManager.GetCurrentTheme();
        var isBuiltIn = ThemePresets.Any(p =>
            string.Equals(p.AccentColor, saved.AccentColor, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(p.BgPrimary, saved.BgPrimary, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(p.BgSecondary, saved.BgSecondary, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(p.AccentHover, saved.AccentHover, StringComparison.OrdinalIgnoreCase));

        if (!isBuiltIn && !saved.IsBuiltIn)
            ThemePresets.Add(saved);
    }

    /// <summary>
    /// Загружает цвета текущей темы в color picker'ы и выбирает соответствующий пресет.
    /// <para>
    /// Флаг <c>_isLoadingTheme</c> подавляет запись <see cref="HasUnsavedThemeChanges"/>
    /// во время программного изменения цветов.
    /// </para>
    /// </summary>
    private void LoadThemeColors()
    {
        _isLoadingTheme = true;
        try
        {
            var currentTheme = _themeManager.GetCurrentTheme();
            ApplyThemeToColorPickers(currentTheme);

            var matchingPreset = ThemePresets.FirstOrDefault(p =>
                string.Equals(p.AccentColor, currentTheme.AccentColor, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(p.BgPrimary, currentTheme.BgPrimary, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(p.BgSecondary, currentTheme.BgSecondary, StringComparison.OrdinalIgnoreCase));

            matchingPreset ??= ThemePresets.FirstOrDefault(p =>
                string.Equals(p.Name, currentTheme.Name, StringComparison.OrdinalIgnoreCase));

            SelectedPreset = matchingPreset ?? ThemePresets.FirstOrDefault();
            HasUnsavedThemeChanges = false;
        }
        finally
        {
            _isLoadingTheme = false;
        }
    }

    /// <summary>
    /// Применяет текущие цвета color picker'ов как новую тему и сохраняет её.
    /// <para>
    /// Производные цвета (BgHighlight, BgHover, BgOverlay и т.д.) вычисляются
    /// автоматически на основе BgSecondary через LightenColor/DarkenColor.
    /// Semantic-цвета (SystemError, SystemInfoBlue, SystemWarnOrange) наследуются
    /// от дефолтной темы — пользователь их не редактирует в color picker'ах.
    /// </para>
    /// </summary>
    private void ApplyTheme()
    {
        static string GetRgbHex(Color c) => $"{c.R:X2}{c.G:X2}{c.B:X2}";

        // Берём semantic-цвета из текущей темы — они не меняются через color picker'ы
        var current = _themeManager.GetCurrentTheme();

        var theme = new ThemeSettings
        {
            Name = SelectedPreset?.Name ?? SL["Theme_Custom"],

            // Backgrounds
            AccentColor = AccentColor.ToString(),
            AccentHover = SmartAccentHover(AccentColor).ToString(),
            BgPrimary = BgPrimaryColor.ToString(),
            BgSecondary = BgSecondaryColor.ToString(),
            BgElevated = BgElevatedColor.ToString(),
            BgHighlight = LightenColor(BgSecondaryColor, 0.1).ToString(),
            BgHover = LightenColor(BgSecondaryColor, 0.2).ToString(),
            BgSkeleton = LightenColor(BgSecondaryColor, 0.05).ToString(),
            BgSkeletonDeep = DarkenColor(BgSecondaryColor, 0.2).ToString(),
            BgOverlay = $"#CC{GetRgbHex(BgPrimaryColor)}",

            // Text
            TextPrimary = TextPrimaryColor.ToString(),
            TextSecondary = TextSecondaryColor.ToString(),
            TextMuted = DarkenColor(TextSecondaryColor, 0.3).ToString(),
            TextDark = BgPrimaryColor.ToString(),

            // Semantic — наследуем, пользователь их не трогает
            SystemError = current.SystemError,
            SystemErrorBg = current.SystemErrorBg,
            SystemInfoBlue = current.SystemInfoBlue,
            SystemWarnOrange = current.SystemWarnOrange,
        };

        _themeManager.SaveTheme(theme);
        _themeManager.ApplyTheme(theme);
        HasUnsavedThemeChanges = false;

        RefreshThemePresets();

        _isLoadingTheme = true;
        SelectedPreset = ThemePresets.FirstOrDefault(p =>
            string.Equals(p.Name, theme.Name, StringComparison.OrdinalIgnoreCase))
            ?? ThemePresets.FirstOrDefault();
        _isLoadingTheme = false;
    }

    /// <summary>
    /// Вычисляет hover-цвет акцента с учётом яркости.
    /// <para>
    /// Светлый акцент затемняется, тёмный — светлеет,
    /// чтобы hover всегда был визуально отличим.
    /// </para>
    /// </summary>
    private static Color SmartAccentHover(Color accent)
    {
        var brightness = (0.299 * accent.R + 0.587 * accent.G + 0.114 * accent.B) / 255.0;
        return brightness > 0.7
            ? DarkenColor(accent, 0.15)
            : LightenColor(accent, 0.15);
    }

    /// <summary>
    /// Перестраивает все локализованные списки ComboBox с сохранением текущего выбора.
    /// <para>
    /// Вызывается при инициализации и при смене языка. Текущий выбор
    /// сохраняется через Value, а не через индекс — безопасно при перестройке коллекции.
    /// </para>
    /// </summary>
    private void RefreshLocalizedLists()
    {
        var currentProfile = SelectedInternetProfile?.Value ?? _library.Settings.InternetProfile;
        InternetProfileOptions.Clear();
        foreach (var p in Enum.GetValues<InternetProfile>())
            InternetProfileOptions.Add(new LocalizedItem<InternetProfile>(p, SL[$"NetProfile_{p}"]));
        SelectedInternetProfile = InternetProfileOptions.FirstOrDefault(x => x.Value == currentProfile)
                               ?? InternetProfileOptions[1];

        var currentImgPreset = SelectedImageCachePreset?.Value ?? ImageCachePreset.Custom;
        ImageCachePresets.Clear();
        ImageCachePresets.Add(new LocalizedItem<ImageCachePreset>(ImageCachePreset.Low, $"{SL["Cache_Low"]} (20)"));
        ImageCachePresets.Add(new LocalizedItem<ImageCachePreset>(ImageCachePreset.Medium, $"{SL["Cache_Medium"]} (50)"));
        ImageCachePresets.Add(new LocalizedItem<ImageCachePreset>(ImageCachePreset.High, $"{SL["Cache_High"]} (100)"));
        if (currentImgPreset != ImageCachePreset.Custom)
            SelectedImageCachePreset = ImageCachePresets.FirstOrDefault(x => x.Value == currentImgPreset);

        var currentCurve = SelectedVolumeCurve?.Value ?? _library.Settings.Audio.VolumeCurve;
        VolumeCurveOptions.Clear();
        VolumeCurveOptions.Add(new(VolumeCurveType.Linear, SL["VolumeCurve_Linear"]));
        VolumeCurveOptions.Add(new(VolumeCurveType.Quadratic, SL["VolumeCurve_Quadratic"]));
        VolumeCurveOptions.Add(new(VolumeCurveType.Logarithmic, SL["VolumeCurve_Logarithmic"]));
        VolumeCurveOptions.Add(new(VolumeCurveType.Cubic, SL["VolumeCurve_Cubic"]));
        VolumeCurveOptions.Add(new(VolumeCurveType.SpeedOfLight, SL["VolumeCurve_SpeedOfLight"]));
        SelectedVolumeCurve = VolumeCurveOptions.FirstOrDefault(x => x.Value == currentCurve)
                           ?? VolumeCurveOptions[1];

        var currentErrorBehavior = SelectedErrorBehavior?.Value ?? _library.Settings.Audio.CriticalErrorBehavior;
        ErrorBehaviorOptions.Clear();
        ErrorBehaviorOptions.Add(new(PlaybackErrorBehavior.Dialog, SL["Settings_ErrorBehavior_Dialog"]));
        ErrorBehaviorOptions.Add(new(PlaybackErrorBehavior.ToastAndSkip, SL["Settings_ErrorBehavior_ToastAndSkip"]));
        ErrorBehaviorOptions.Add(new(PlaybackErrorBehavior.Ignore, SL["Settings_ErrorBehavior_Ignore"]));
        SelectedErrorBehavior = ErrorBehaviorOptions.FirstOrDefault(x => x.Value == currentErrorBehavior)
                             ?? ErrorBehaviorOptions.FirstOrDefault(x => x.Value == PlaybackErrorBehavior.ToastAndSkip)
                             ?? ErrorBehaviorOptions[0];

        var currentNTokenMode = SelectedNTokenNotification?.Value ?? _library.Settings.Audio.NTokenNotificationMode;
        NTokenNotificationOptions.Clear();
        NTokenNotificationOptions.Add(new(NTokenNotificationMode.Disabled, SL["Settings_NTokenMode_Disabled"]));
        NTokenNotificationOptions.Add(new(NTokenNotificationMode.PanelOnly, SL["Settings_NTokenMode_PanelOnly"]));
        NTokenNotificationOptions.Add(new(NTokenNotificationMode.Toast, SL["Settings_NTokenMode_Toast"]));
        SelectedNTokenNotification = NTokenNotificationOptions.FirstOrDefault(x => x.Value == currentNTokenMode)
                                  ?? NTokenNotificationOptions.FirstOrDefault(x => x.Value == NTokenNotificationMode.Toast)
                                  ?? NTokenNotificationOptions[^1];

        var currentFailureMode = SelectedPlaybackFailure?.Value ?? _library.Settings.Audio.PlaybackFailureBehavior;
        PlaybackFailureOptions.Clear();
        PlaybackFailureOptions.Add(new(PlaybackFailureBehavior.SkipAndPlay, SL["Settings_PlaybackFailure_SkipAndPlay"]));
        PlaybackFailureOptions.Add(new(PlaybackFailureBehavior.SkipAndPause, SL["Settings_PlaybackFailure_SkipAndPause"]));
        PlaybackFailureOptions.Add(new(PlaybackFailureBehavior.Stop, SL["Settings_PlaybackFailure_Stop"]));
        SelectedPlaybackFailure = PlaybackFailureOptions.FirstOrDefault(x => x.Value == currentFailureMode)
                               ?? PlaybackFailureOptions.FirstOrDefault(x => x.Value == PlaybackFailureBehavior.SkipAndPause)
                               ?? PlaybackFailureOptions[0];

        UpdatePlaybackFailureActionAvailability();

        var currentCloseAction = SelectedCloseAction?.Value ?? _library.Settings.CloseAction;
        CloseActionOptions.Clear();
        CloseActionOptions.Add(new(CloseAction.Exit, SL["CloseAction_Exit"]));
        CloseActionOptions.Add(new(CloseAction.MinimizeToTray, SL["CloseAction_MinimizeToTray"]));
        CloseActionOptions.Add(new(CloseAction.Ask, SL["CloseAction_Ask"]));
        SelectedCloseAction = CloseActionOptions.FirstOrDefault(x => x.Value == currentCloseAction)
                           ?? CloseActionOptions[2];

        var currentNormMode = SelectedNormalizationMode?.Value ?? _library.Settings.Audio.NormalizationMode;
        NormalizationModeOptions.Clear();
        NormalizationModeOptions.Add(new(NormalizationMode.Bidirectional, SL["NormMode_Bidirectional"]));
        NormalizationModeOptions.Add(new(NormalizationMode.DownwardOnly, SL["NormMode_DownwardOnly"]));
        SelectedNormalizationMode = NormalizationModeOptions.FirstOrDefault(x => x.Value == currentNormMode)
                                 ?? NormalizationModeOptions[0];

        var currentQuality = SelectedQualityItem?.Value ?? _library.Settings.QualityPreference;
        QualityOptions = Enum.GetValues<AudioQualityPreference>()
            .Select(q => new LocalizedItem<AudioQualityPreference>(
                q, SL[$"AudioQuality_{q}"] ?? q.ToString()))
            .ToList();
        SelectedQualityItem = QualityOptions.FirstOrDefault(x => x.Value == currentQuality)
                           ?? QualityOptions[0];
        this.RaisePropertyChanged(nameof(QualityOptions));
    }

    /// <summary>
    /// Регистрирует все Rx-подписки на изменения свойств → сохранение в настройки.
    /// <para>
    /// Вызывается однократно после <see cref="LoadAllSettings"/> чтобы подписки
    /// не срабатывали на программную установку начальных значений.
    /// Все подписки добавляются в <c>Disposables</c> и освобождаются при Dispose.
    /// </para>
    /// </summary>
    private void SetupSubscriptions()
    {
        if (_subscriptionsSetup) return;
        _subscriptionsSetup = true;

        this.WhenAnyValue(x => x.SelectedSidebarItem)
            .Skip(1).WhereNotNull()
            .Where(_ => !_isLoadingSettings)
            .Subscribe(item =>
            {
                if (item is NetworkSidebarItem && NetworkStatus == NetworkStatusKind.Unknown)
                    _ = TestNetworkAsync();
            })
            .DisposeWith(Disposables);

        this.WhenAnyValue(x => x.SelectedCloseAction)
            .Skip(1).WhereNotNull()
            .Where(_ => !_isLoadingSettings)
            .Subscribe(c => _library.UpdateSettings(s => s.CloseAction = c.Value))
            .DisposeWith(Disposables);

        this.WhenAnyValue(x => x.MinimizeToTray)
            .Skip(1)
            .Where(_ => !_isLoadingSettings)
            .Subscribe(v => _library.UpdateSettings(s => s.MinimizeToTray = v))
            .DisposeWith(Disposables);

        this.WhenAnyValue(x => x.SelectedLanguage)
            .Skip(1).WhereNotNull()
            .Where(_ => !_isLoadingSettings)
            .Subscribe(lang =>
            {
                LocalizationService.Instance.CurrentLanguage = lang.Code;
                _library.UpdateSettings(s => s.LanguageCode = lang.Code);
            })
            .DisposeWith(Disposables);

        this.WhenAnyValue(x => x.SelectedInternetProfile)
            .Skip(1).WhereNotNull()
            .Where(_ => !_isLoadingSettings)
            .Subscribe(async p =>
            {
                _library.UpdateSettings(s => s.InternetProfile = p.Value);

                // Применяется сразу — перезапуск не нужен.
                await AudioEngine.ReinitializeWithProfileAsync(p.Value);
            })
            .DisposeWith(Disposables);

        this.WhenAnyValue(
                x => x.ProxyEnabled, x => x.ProxyHost, x => x.ProxyPort,
                x => x.ProxyAuth, x => x.ProxyUser, x => x.ProxyPass)
            .Skip(1)
            .Where(_ => !_isLoadingSettings)
            .Subscribe(_ => SaveNetworkSettings())
            .DisposeWith(Disposables);

        this.WhenAnyValue(x => x.ImageCacheLimitMb, x => x.AudioCacheLimitMb)
            .Skip(1)
            .Where(_ => !_isLoadingSettings)
            .Throttle(TimeSpan.FromMilliseconds(500))
            .Subscribe(_ => SaveStorageSettings())
            .DisposeWith(Disposables);

        this.WhenAnyValue(x => x.SelectedGpuCachePreset)
            .Skip(1).WhereNotNull()
            .Where(_ => !_isLoadingSettings)
            .Subscribe(preset =>
            {
                if (BootstrapSettings.Current.GpuTextureCacheMb == preset.Mb) return;
                BootstrapSettings.Current.GpuTextureCacheMb = preset.Mb;
                BootstrapSettings.Current.Save();
                GpuCacheRestartRequired = true;
                Log.Info($"[Settings] GPU cache → {preset.Mb}MB (restart required)");
            })
            .DisposeWith(Disposables);

        this.WhenAnyValue(x => x.AutoMemoryCleanupEnabled)
            .Skip(1)
            .Where(_ => !_isLoadingSettings)
            .Subscribe(v =>
            {
                _library.UpdateSettings(s => s.Memory.AutoCleanupEnabled = v);
                MemoryCleanupHelper.RestartAutoCleanup();
            })
            .DisposeWith(Disposables);

        this.WhenAnyValue(x => x.MemoryCleanupIntervalMinutes)
            .Skip(1)
            .Where(_ => !_isLoadingSettings)
            .Throttle(TimeSpan.FromMilliseconds(500))
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(v =>
            {
                _library.UpdateSettings(s => s.Memory.AutoCleanupIntervalMinutes = v);
                MemoryCleanupHelper.RestartAutoCleanup();
            })
            .DisposeWith(Disposables);

        this.WhenAnyValue(x => x.MemoryPressureThresholdMb)
            .Skip(1)
            .Where(_ => !_isLoadingSettings)
            .Throttle(TimeSpan.FromMilliseconds(500))
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(v => _library.UpdateSettings(s => s.Memory.PressureThresholdMb = v))
            .DisposeWith(Disposables);

        this.WhenAnyValue(x => x.SelectedImageCachePreset)
            .Skip(1)
            .Where(p => !_isUpdatingPreset && !_isLoadingSettings && p is not null)
            .Subscribe(p =>
            {
                _isUpdatingPreset = true;
                MaxBitmapCacheItems = p!.Value switch
                {
                    ImageCachePreset.Low => 20,
                    ImageCachePreset.Medium => 50,
                    ImageCachePreset.High => 100,
                    _ => MaxBitmapCacheItems
                };
                _isUpdatingPreset = false;
            })
            .DisposeWith(Disposables);

        this.WhenAnyValue(x => x.MaxBitmapCacheItems)
            .Skip(1)
            .Where(_ => !_isLoadingSettings)
            .Subscribe(val =>
            {
                _library.UpdateSettings(s => s.Storage.MaxBitmapCacheItems = val);
                _imageCache.EnforceLimits();

                if (_isUpdatingPreset) return;

                _isUpdatingPreset = true;
                SelectedImageCachePreset = val switch
                {
                    20 => ImageCachePresets.FirstOrDefault(x => x.Value == ImageCachePreset.Low),
                    50 => ImageCachePresets.FirstOrDefault(x => x.Value == ImageCachePreset.Medium),
                    100 => ImageCachePresets.FirstOrDefault(x => x.Value == ImageCachePreset.High),
                    _ => null
                };
                _isUpdatingPreset = false;
            })
            .DisposeWith(Disposables);

        this.WhenAnyValue(
                x => x.AccentColor, x => x.BgPrimaryColor, x => x.BgSecondaryColor,
                x => x.BgElevatedColor, x => x.TextPrimaryColor, x => x.TextSecondaryColor)
            .Skip(1)
            .Where(_ => !_isLoadingSettings)
            .Subscribe(_ => { if (!_isLoadingTheme) HasUnsavedThemeChanges = true; })
            .DisposeWith(Disposables);

        this.WhenAnyValue(x => x.SelectedPreset)
            .Skip(1).WhereNotNull()
            .Where(_ => !_isLoadingSettings)
            .Subscribe(ApplyPresetToColorPickers)
            .DisposeWith(Disposables);

        this.WhenAnyValue(x => x.MaxVolumeLimit)
            .Skip(1)
            .Where(_ => !_isLoadingSettings)
            .Subscribe(v =>
            {
                _library.UpdateSettings(s => s.MaxVolumeLimit = v);
                _audio.OnMaxVolumeLimitChanged(v);
            })
            .DisposeWith(Disposables);

        this.WhenAnyValue(x => x.TargetGainDb)
            .Skip(1)
            .Where(_ => !_isLoadingSettings)
            .Subscribe(v =>
            {
                _library.UpdateSettings(s => s.TargetGainDb = v);
                _audio.UpdateAudioSettings();
            })
            .DisposeWith(Disposables);

        this.WhenAnyValue(x => x.SelectedQualityItem)
            .Skip(1).WhereNotNull()
            .Where(_ => !_isLoadingSettings)
            .Subscribe(item =>
            {
                _library.UpdateSettings(s => s.QualityPreference = item.Value);
                _youtube.ClearCache();
            })
            .DisposeWith(Disposables);

        this.WhenAnyValue(x => x.VolumeBoostEnabled)
            .Skip(1)
            .Where(_ => !_isLoadingSettings)
            .Subscribe(v =>
            {
                _library.UpdateSettings(s => s.Audio.VolumeBoostEnabled = v);
                _audio.UpdateAudioSettings();
            })
            .DisposeWith(Disposables);

        this.WhenAnyValue(x => x.AudioNormalizationEnabled)
            .Skip(1)
            .Where(_ => !_isLoadingSettings && !_isRevertingNormalization)
            .Subscribe(async v =>
            {
                if (!v)
                {
                    var confirmed = await _dialog.ConfirmAsync(
                        SL["Settings_NormalizationDisable_Title"],
                        SL["Settings_NormalizationDisable_Message"],
                        SL["Common_Disable"],
                        SL["Common_Cancel"]);

                    if (!confirmed)
                    {
                        _isRevertingNormalization = true;
                        AudioNormalizationEnabled = true;
                        _isRevertingNormalization = false;
                        return;
                    }
                }

                _library.UpdateSettings(s => s.Audio.NormalizationEnabled = v);
                _audio.UpdateAudioSettings();
            })
            .DisposeWith(Disposables);

        this.WhenAnyValue(x => x.SelectedNormalizationMode)
            .Skip(1).WhereNotNull()
            .Where(_ => !_isLoadingSettings)
            .Subscribe(m =>
            {
                _library.UpdateSettings(s => s.Audio.NormalizationMode = m.Value);
                _audio.UpdateAudioSettings();
            })
            .DisposeWith(Disposables);

        this.WhenAnyValue(x => x.NormalizationTargetLufs)
            .Skip(1)
            .Where(_ => !_isLoadingSettings)
            .Throttle(TimeSpan.FromMilliseconds(300))
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(v =>
            {
                _library.UpdateSettings(s => s.Audio.NormalizationTargetLufs = v);
                _audio.UpdateAudioSettings();
            })
            .DisposeWith(Disposables);

        this.WhenAnyValue(x => x.NormalizationMaxGain)
            .Skip(1)
            .Where(_ => !_isLoadingSettings)
            .Throttle(TimeSpan.FromMilliseconds(300))
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(v =>
            {
                _library.UpdateSettings(s => s.Audio.NormalizationMaxGain = v);
                _audio.UpdateAudioSettings();
            })
            .DisposeWith(Disposables);

        this.WhenAnyValue(x => x.SelectedVolumeCurve)
            .Skip(1).WhereNotNull()
            .Where(_ => !_isLoadingSettings)
            .Subscribe(c =>
            {
                _library.UpdateSettings(s => s.Audio.VolumeCurve = c.Value);
                _audio.UpdateAudioSettings();
            })
            .DisposeWith(Disposables);

        this.WhenAnyValue(x => x.SelectedErrorBehavior)
            .Skip(1).WhereNotNull()
            .Where(_ => !_isLoadingSettings)
            .Subscribe(b =>
            {
                _library.UpdateSettings(s => s.Audio.CriticalErrorBehavior = b.Value);
                UpdatePlaybackFailureActionAvailability();
            })
            .DisposeWith(Disposables);

        this.WhenAnyValue(x => x.PlayErrorSound)
            .Skip(1)
            .Where(_ => !_isLoadingSettings)
            .Subscribe(v => _library.UpdateSettings(s => s.Audio.PlayErrorSound = v))
            .DisposeWith(Disposables);

        this.WhenAnyValue(x => x.SkipNTokenTracks)
            .Skip(1)
            .Where(_ => !_isLoadingSettings)
            .Subscribe(v => _library.UpdateSettings(s => s.Audio.SkipNTokenTracks = v))
            .DisposeWith(Disposables);

        this.WhenAnyValue(x => x.SelectedNTokenNotification)
            .Skip(1).WhereNotNull()
            .Where(_ => !_isLoadingSettings)
            .Subscribe(m => _library.UpdateSettings(s => s.Audio.NTokenNotificationMode = m.Value))
            .DisposeWith(Disposables);

        this.WhenAnyValue(x => x.SelectedPlaybackFailure)
            .Skip(1).WhereNotNull()
            .Where(_ => !_isLoadingSettings)
            .Subscribe(m => _library.UpdateSettings(s => s.Audio.PlaybackFailureBehavior = m.Value))
            .DisposeWith(Disposables);

        this.WhenAnyValue(x => x.DiscordRpcEnabled)
            .Skip(1)
            .Where(_ => !_isLoadingSettings)
            .Subscribe(v => _library.UpdateSettings(s => s.DiscordRpcEnabled = v))
            .DisposeWith(Disposables);

        this.WhenAnyValue(x => x.AutoPlayOnPaste)
            .Skip(1)
            .Where(_ => !_isLoadingSettings)
            .Subscribe(v => _library.UpdateSettings(s => s.AutoPlayOnUrlPaste = v))
            .DisposeWith(Disposables);

        this.WhenAnyValue(x => x.RememberTrackFormat)
            .Skip(1)
            .Where(_ => !_isLoadingSettings)
            .Subscribe(v => _library.UpdateSettings(s => s.RememberTrackFormat = v))
            .DisposeWith(Disposables);

        this.WhenAnyValue(x => x.SearchBatchSize)
            .Skip(1)
            .Where(_ => !_isLoadingSettings)
            .Subscribe(v => _library.UpdateSettings(s => s.SearchBatchSize = v))
            .DisposeWith(Disposables);

        this.WhenAnyValue(x => x.EnableSearchCache)
            .Skip(1)
            .Where(_ => !_isLoadingSettings)
            .Subscribe(v => _library.UpdateSettings(s => s.EnableSearchCache = v))
            .DisposeWith(Disposables);

        this.WhenAnyValue(x => x.SearchCacheTtlMinutes)
            .Skip(1)
            .Where(_ => !_isLoadingSettings)
            .Subscribe(v =>
            {
                _library.UpdateSettings(s => s.SearchCacheTtlMinutes = v);
                _ = _searchCache.CleanupExpiredAsync();
            })
            .DisposeWith(Disposables);
    }

    /// <summary>
    /// Загружает все настройки из <see cref="LibraryService"/> в свойства VM.
    /// <para>
    /// Флаг <c>_isLoadingSettings</c> подавляет все Rx-подписки на время загрузки,
    /// чтобы программная установка значений не триггерила обратную запись в настройки.
    /// </para>
    /// </summary>
    private void LoadAllSettings()
    {
        _isLoadingSettings = true;
        try
        {
            var s = _library.Settings;

            DownloadPath = _library.DownloadPath;
            DiscordRpcEnabled = s.DiscordRpcEnabled;
            AutoPlayOnPaste = s.AutoPlayOnUrlPaste;
            SearchBatchSize = s.SearchBatchSize;
            MaxVolumeLimit = s.MaxVolumeLimit;
            TargetGainDb = s.TargetGainDb;
            RememberTrackFormat = s.RememberTrackFormat;
            EnableSearchCache = s.EnableSearchCache;
            SearchCacheTtlMinutes = s.SearchCacheTtlMinutes;
            SelectedLanguage = Languages.FirstOrDefault(x => x.Code == s.LanguageCode) ?? Languages[0];

            var mem = s.Memory;
            AutoMemoryCleanupEnabled = mem.AutoCleanupEnabled;
            MemoryCleanupIntervalMinutes = mem.AutoCleanupIntervalMinutes > 0 ? mem.AutoCleanupIntervalMinutes : 30;
            MemoryPressureThresholdMb = mem.PressureThresholdMb > 0 ? mem.PressureThresholdMb : 400;

            VolumeBoostEnabled = s.Audio.VolumeBoostEnabled;
            AudioNormalizationEnabled = s.Audio.NormalizationEnabled;
            NormalizationTargetLufs = s.Audio.NormalizationTargetLufs;
            NormalizationMaxGain = s.Audio.NormalizationMaxGain;
            SelectedNormalizationMode = NormalizationModeOptions.FirstOrDefault(x => x.Value == s.Audio.NormalizationMode)
                                     ?? NormalizationModeOptions[0];
            SelectedVolumeCurve = VolumeCurveOptions.FirstOrDefault(x => x.Value == s.Audio.VolumeCurve)
                                     ?? VolumeCurveOptions[1];
            PlayErrorSound = s.Audio.PlayErrorSound;
            SelectedErrorBehavior = ErrorBehaviorOptions.FirstOrDefault(x => x.Value == s.Audio.CriticalErrorBehavior)
                                 ?? ErrorBehaviorOptions.FirstOrDefault(x => x.Value == PlaybackErrorBehavior.ToastAndSkip)
                                 ?? ErrorBehaviorOptions[0];
            SkipNTokenTracks = s.Audio.SkipNTokenTracks;
            SelectedNTokenNotification = NTokenNotificationOptions.FirstOrDefault(x => x.Value == s.Audio.NTokenNotificationMode)
                                      ?? NTokenNotificationOptions.FirstOrDefault(x => x.Value == NTokenNotificationMode.Toast)
                                      ?? NTokenNotificationOptions[^1];
            SelectedPlaybackFailure = PlaybackFailureOptions.FirstOrDefault(x => x.Value == s.Audio.PlaybackFailureBehavior)
                                   ?? PlaybackFailureOptions.FirstOrDefault(x => x.Value == PlaybackFailureBehavior.SkipAndPause)
                                   ?? PlaybackFailureOptions[0];

            SelectedQualityItem = QualityOptions.FirstOrDefault(x => x.Value == s.QualityPreference)
                               ?? QualityOptions[0];

            IsAuthenticated = _auth.IsAuthenticated;
            RaiseAccountProperties();

            SelectedInternetProfile = InternetProfileOptions.FirstOrDefault(x => x.Value == s.InternetProfile)
                                   ?? InternetProfileOptions[1];

            ProxyEnabled = s.Proxy.Enabled;
            ProxyHost = s.Proxy.Host;
            ProxyPort = s.Proxy.Port;
            ProxyAuth = s.Proxy.UseAuth;
            ProxyUser = s.Proxy.Username;
            ProxyPass = s.Proxy.Password;

            ImageCacheLimitMb = s.Storage.ImageCacheLimitMb;
            AudioCacheLimitMb = s.Storage.AudioCacheLimitMb;
            DownloadedTracksLimitMb = s.Storage.DownloadedTracksLimitMb;
            AutoSaveToDownloads = s.Storage.AutoSaveToDownloads;

            MaxBitmapCacheItems = s.Storage.MaxBitmapCacheItems > 0 ? s.Storage.MaxBitmapCacheItems : 40;
            _isUpdatingPreset = true;
            SelectedImageCachePreset = MaxBitmapCacheItems switch
            {
                20 => ImageCachePresets.FirstOrDefault(x => x.Value == ImageCachePreset.Low),
                50 => ImageCachePresets.FirstOrDefault(x => x.Value == ImageCachePreset.Medium),
                100 => ImageCachePresets.FirstOrDefault(x => x.Value == ImageCachePreset.High),
                _ => null
            };
            _isUpdatingPreset = false;

            SelectedCloseAction = CloseActionOptions.FirstOrDefault(x => x.Value == s.CloseAction)
                               ?? CloseActionOptions.LastOrDefault();
            MinimizeToTray = s.MinimizeToTray;

            UpdatePlaybackFailureActionAvailability();
            LoadThemeColors();
        }
        finally
        {
            _isLoadingSettings = false;
        }
    }

    /// <summary>Применяет пресет темы к color picker'ам и выставляет флаг несохранённых изменений.</summary>
    private void ApplyPresetToColorPickers(ThemeSettings preset)
    {
        ApplyThemeToColorPickers(preset);
        HasUnsavedThemeChanges = true;
    }

    /// <summary>
    /// Распаковывает цвета темы в свойства color picker'ов.
    /// <para>
    /// Флаг <c>_isLoadingTheme</c> предотвращает срабатывание подписки
    /// на изменение цветов, которая выставляет <see cref="HasUnsavedThemeChanges"/>.
    /// </para>
    /// </summary>
    private void ApplyThemeToColorPickers(ThemeSettings theme)
    {
        _isLoadingTheme = true;
        try
        {
            AccentColor = ParseColorSafe(theme.AccentColor);
            BgPrimaryColor = ParseColorSafe(theme.BgPrimary);
            BgSecondaryColor = ParseColorSafe(theme.BgSecondary);
            BgElevatedColor = ParseColorSafe(theme.BgElevated);
            TextPrimaryColor = ParseColorSafe(theme.TextPrimary);
            TextSecondaryColor = ParseColorSafe(theme.TextSecondary);
        }
        finally
        {
            _isLoadingTheme = false;
        }
    }

    /// <summary>Сбрасывает тему к дефолтной и обновляет color picker'ы.</summary>
    private void ResetTheme()
    {
        _themeManager.ResetToDefault();
        LoadThemeColors();
        SelectedPreset = ThemePresets.FirstOrDefault();
    }

    /// <summary>
    /// Парсит hex-цвет без выброса исключения.
    /// <para>При некорректном значении возвращает Magenta как визуальный сигнал ошибки.</para>
    /// </summary>
    private static Color ParseColorSafe(string hex)
    {
        try { return Color.Parse(hex); }
        catch { return Colors.Magenta; }
    }

    /// <summary>Осветляет цвет на заданный фактор [0..1].</summary>
    private static Color LightenColor(Color c, double factor) =>
        Color.FromArgb(c.A,
            (byte)Math.Min(255, c.R + (255 - c.R) * factor),
            (byte)Math.Min(255, c.G + (255 - c.G) * factor),
            (byte)Math.Min(255, c.B + (255 - c.B) * factor));

    /// <summary>Затемняет цвет на заданный фактор [0..1].</summary>
    private static Color DarkenColor(Color c, double factor) =>
        Color.FromArgb(c.A,
            (byte)(c.R * (1 - factor)),
            (byte)(c.G * (1 - factor)),
            (byte)(c.B * (1 - factor)));

    /// <summary>
    /// Сохраняет сетевые настройки и немедленно применяет их к обоим HTTP-клиентам.
    /// Перезапуск приложения не требуется.
    /// </summary>
    private void SaveNetworkSettings()
    {
        var proxy = new ProxySettings
        {
            Enabled = ProxyEnabled,
            Host = ProxyHost,
            Port = ProxyPort,
            UseAuth = ProxyAuth,
            Username = ProxyUser,
            Password = ProxyPass,
        };

        _library.UpdateSettings(s => s.Proxy = proxy);

        Core.Audio.Http.SharedHttpClient.Rebuild(proxy);
        _youtube.ReloadClient();

        NetworkStatus = NetworkStatusKind.Unknown;
        NetworkStatusText = "";
        NetworkLatencyMs = 0;
        this.RaisePropertyChanged(nameof(NetworkStatusColor));
        this.RaisePropertyChanged(nameof(HasLatency));

        Log.Info("[Settings] Network settings applied immediately.");
    }

    /// <summary>Сохраняет лимиты дискового кэша и обновляет статистику.</summary>
    private void SaveStorageSettings()
    {
        _library.UpdateSettings(s =>
        {
            s.Storage.ImageCacheLimitMb = ImageCacheLimitMb;
            s.Storage.AudioCacheLimitMb = AudioCacheLimitMb;
        });
        UpdateCacheStats();
    }

    private async Task ClearImageCacheAsync()
    {
        await _imageCache.ClearDiskCacheAsync();
        UpdateCacheStats();
    }

    private async Task ClearAudioCacheAsync()
    {
        var cache = AudioSourceFactory.GlobalCache;
        if (cache is not null)
            await cache.ClearAllAsync();
        else
            Log.Warn("[AudioCache] AudioCacheManager not initialized, cannot clear cache.");

        UpdateCacheStats();
    }

    private void UpdatePlaybackFailureActionAvailability()
    {
        var behavior = SelectedErrorBehavior?.Value ?? _library.Settings.Audio.CriticalErrorBehavior;
        IsPlaybackFailureActionEnabled = PlaybackErrorBehaviorMatrix.UsesPlaybackFailureBehavior(behavior);
    }

    /// <summary>
    /// Обновляет статистику и проценты заполнения всех кэшей.
    /// <para>Вызывается после очистки кэша или изменения лимитов.</para>
    /// </summary>
    private void UpdateCacheStats()
    {
        var (memItems, _, imgCount, imgSizeMb) = _imageCache.GetStats();

        var cache = AudioSourceFactory.GlobalCache;
        var (audioFileCount, audioSizeMb) = cache?.GetStatsCompact() ?? (0, 0);
        var (downloadFileCount, downloadSizeMb) = AudioCacheManager.GetDownloadsStats();

        ImageCacheStats = $"{imgSizeMb} MB / {ImageCacheLimitMb} MB ({imgCount} {SL["Common_Files"]}, RAM: {memItems})";
        AudioCacheStats = $"{audioSizeMb} MB / {AudioCacheLimitMb} MB ({audioFileCount} {SL["Common_Files"]})";
        DownloadsStats = $"{downloadSizeMb} MB / {DownloadedTracksLimitMb} MB ({downloadFileCount} {SL["Common_Files"]})";

        ImageCacheUsagePercent = ImageCacheLimitMb > 0 ? Math.Clamp((double)imgSizeMb / ImageCacheLimitMb, 0, 1) : 0;
        AudioCacheUsagePercent = AudioCacheLimitMb > 0 ? Math.Clamp((double)audioSizeMb / AudioCacheLimitMb, 0, 1) : 0;
        DownloadsUsagePercent = DownloadedTracksLimitMb > 0 ? Math.Clamp((double)downloadSizeMb / DownloadedTracksLimitMb, 0, 1) : 0;
    }

    private async Task LoginAsync()
    {
        IsAccountLoading = true;
        try
        {
            var authVm = new AuthDialogViewModel(_auth, _userData, _localServer);
            var host = AppEntry.Services.GetRequiredService<DialogHostViewModel>();
            var tcs = new TaskCompletionSource<bool>();

            authVm.OnResult = result =>
            {
                host.CloseDialog(result);
                tcs.TrySetResult(result);
            };

            _ = host.ShowAsync<object>(authVm);
            var success = await tcs.Task;

            if (success)
            {
                IsAuthenticated = _auth.IsAuthenticated;
                RaiseAccountProperties();

                await _notifications.ShowToastAsync(
                    titleKey: SL["Dialog_Success"] ?? "Success",
                    messageKey: string.Format(SL["Auth_LoggedInAs"] ?? "Signed in: {0}", _auth.State.UserName),
                    severity: NotificationSeverity.Success,
                    durationMs: 4000);
            }
        }
        finally
        {
            IsAccountLoading = false;
        }
    }

    /// <summary>
    /// Вызывает диалог выбора канала/бренда и мгновенно переключает контекст авторизованного пользователя
    /// без дополнительных сетевых запросов, используя данные из кэша.
    /// </summary>
    private async Task SwitchAccountAsync()
    {
        if (!IsAuthenticated) return;

        IsAccountLoading = true;
        _isLoadingSettings = true;
        try
        {
            var accounts = _auth.State.CachedAccounts;
            if (accounts == null || accounts.Count == 0)
            {
                Log.Info("[Settings] No cached accounts found. Making fallback network request...");
                accounts = await _userData.GetAvailableAccountsAsync();
            }

            // Хардкод заменен на ключи локализации
            if (accounts.Count == 0)
            {
                await _dialog.ShowInfoAsync(SL["Dialog_Error_Title"] ?? "Error", SL["Auth_ProfileLoadError_Message"] ?? "Failed to load profile data.");
                return;
            }

            if (accounts.Count <= 1)
            {
                await _dialog.ShowInfoAsync(SL["Dialog_Info_Title"] ?? "Info", SL["Auth_NoMultipleAccounts"] ?? "There are no other channels available for this profile.");
                return;
            }

            var selectedAccount = await _dialog.ShowAccountSelectionDialogAsync(accounts);
            if (selectedAccount == null) return;

            _auth.SetAuthUser(selectedAccount.AuthUser);
            _auth.UpdateUserProfile(selectedAccount.Name, selectedAccount.Email, selectedAccount.AvatarUrl, selectedAccount.GaiaId);

            _youtube.ClearCache();
            RaiseAccountProperties();

            // Используем Toast вместо блокирующего диалога
            await _notifications.ShowToastAsync(
                titleKey: SL["Dialog_Success"] ?? "Success",
                messageKey: string.Format(SL["Auth_LoggedInAs"] ?? "Signed in: {0}", selectedAccount.Name),
                severity: NotificationSeverity.Success,
                durationMs: 4000);
        }
        catch (LoginRequiredException ex) when (ex.Reason == LoginRequiredReason.SessionExpired)
        {
            Log.Warn("[Settings] Switch account failed due to expired session. Prompting user to update cookies.");

            var title = SL["Auth_SessionExpired_SwitchAccount_Title"] ?? "Session Update Required";
            var msg = SL["Auth_SessionExpired_SwitchAccount_Message"] ?? "To switch accounts, you need to update your authorization because the current session has expired.\n\nDo you want to sign in again right now?";
            var loginText = SL["Auth_Login"] ?? "Sign In";
            var cancelText = SL["Common_Cancel"] ?? "Cancel";

            bool wantsToLogin = await _dialog.ConfirmAsync(title, msg, loginText, cancelText);
            if (wantsToLogin)
            {
                await LoginAsync();
            }
        }
        catch (Exception ex)
        {
            await _dialog.ShowInfoAsync(SL["Dialog_Error_Title"] ?? "Error", ex.Message);
        }
        finally
        {
            _isLoadingSettings = false;
            IsAccountLoading = false;
        }
    }

    /// <summary>
    /// Ручное принудительное обновление данных профиля пользователя по кнопке.
    /// Полезно, если аватарка "протухла" или пользователь сменил ник.
    /// </summary>
    private async Task RefreshProfileAsync()
    {
        if (!IsAuthenticated) return;

        IsAccountLoading = true;
        try
        {
            var (name, email, avatar, gaiaId) = await _userData.GetAccountInfoAsync();
            if (!string.IsNullOrEmpty(name))
            {
                _auth.UpdateUserProfile(name, email, avatar, gaiaId);
                RaiseAccountProperties();
            }

            // Запускаем фоновое обновление свитчера, чтобы обновить кэш каналов
            _ = _userData.GetAvailableAccountsAsync();
        }
        catch (Exception ex)
        {
            Log.Warn($"[Settings] Failed to refresh profile manually: {ex.Message}");
        }
        finally
        {
            IsAccountLoading = false;
        }
    }

    private async Task LogoutAsync()
    {
        if (!await _dialog.ConfirmAsync(SL["Auth_Logout"], SL["Dialog_LogoutMessage"])) return;
        IsAccountLoading = true;
        try
        {
            _auth.Logout();
            IsAuthenticated = _auth.IsAuthenticated;
            RaiseAccountProperties();
        }
        finally
        {
            IsAccountLoading = false;
        }
    }

    /// <summary>
    /// Поднимает PropertyChanged для всех вычисляемых свойств аккаунта.
    /// <para>
    /// Нужно при логине/логауте — свойства AccountName, AccountAvatarUrl, AccountSubtitle
    /// не являются [Reactive], их значения зависят от IsAuthenticated и _auth.State.
    /// </para>
    /// </summary>
    private void RaiseAccountProperties()
    {
        this.RaisePropertyChanged(nameof(AccountName));
        this.RaisePropertyChanged(nameof(AccountAvatarUrl));
        this.RaisePropertyChanged(nameof(AccountSubtitle));
    }

    private async Task BrowseDownloadPathAsync()
    {
        var newPath = await DialogService.SelectFolderAsync(DownloadPath);
        if (string.IsNullOrEmpty(newPath)) return;
        DownloadPath = newPath;
        _library.DownloadPath = newPath;
    }

    private async Task ClearHistoryAsync()
    {
        if (!await _dialog.ConfirmAsync(SL["Dialog_Confirm_Title"], SL["Dialog_ClearHistoryMessage"])) return;
        await _library.ClearHistoryAsync();
        await _dialog.ShowInfoAsync(SL["Dialog_Done_Title"], SL["Dialog_HistoryCleared"]);
    }

    private async Task ResetLibraryAsync()
    {
        if (!await _dialog.ConfirmAsync(SL["Dialog_Warning_Title"], SL["Dialog_ResetMessage"])) return;
        await _library.ResetAsync();
        LoadAllSettings();
        await _dialog.ShowInfoAsync(SL["Dialog_Done_Title"], SL["Dialog_ResetComplete"]);
    }

    private async Task ClearDownloadsAsync()
    {
        if (!await _dialog.ConfirmAsync(SL["Dialog_Confirm_Title"], SL["Settings_ClearDownloadsConfirm"])) return;

        await AudioCacheManager.ClearDownloadsAsync();

        foreach (var track in _registry.GetPinnedTracks().Where(t => t.IsDownloaded))
        {
            track.IsDownloaded = false;
            track.LocalPath = null;
        }

        UpdateCacheStats();
    }

    private async Task ShowNormalizationInfoAsync()
    {
        await _dialog.ShowInfoAsync(
            SL["Settings_NormalizationInfo_Title"],
            SL["Settings_NormalizationInfo_Body"],
            SL["Common_GotIt"]);
    }

    /// <summary>
    /// Выполняет проверку доступности YouTube и определяет активный профиль подключения.
    /// Детектирует VPN по типу и описанию сетевых интерфейсов Windows.
    /// </summary>
    private async Task TestNetworkAsync()
    {
        if (IsNetworkTesting) return;

        IsNetworkTesting = true;
        NetworkStatus = NetworkStatusKind.Checking;
        NetworkStatusText = SL["Network_StatusChecking"];
        NetworkLatencyMs = 0;
        this.RaisePropertyChanged(nameof(NetworkStatusColor));

        try
        {
            if (!System.Net.NetworkInformation.NetworkInterface.GetIsNetworkAvailable())
            {
                SetNetworkStatus(NetworkStatusKind.NoInternet, SL["Network_StatusNoInternet"]);
                return;
            }

            bool vpnDetected = DetectVpnInterface();

            var sw = System.Diagnostics.Stopwatch.StartNew();
            bool reachable = await ProbeYoutubeAsync().ConfigureAwait(false);
            sw.Stop();

            if (!reachable)
            {
                SetNetworkStatus(NetworkStatusKind.Error, SL["Network_StatusError"]);
                return;
            }

            NetworkLatencyMs = (int)sw.ElapsedMilliseconds;

            if (ProxyEnabled && !string.IsNullOrWhiteSpace(ProxyHost))
            {
                SetNetworkStatus(
                    NetworkStatusKind.ProxyActive,
                    string.Format(SL["Network_StatusProxy"], ProxyHost, ProxyPort));
            }
            else if (vpnDetected)
            {
                SetNetworkStatus(NetworkStatusKind.VpnDetected, SL["Network_StatusVpn"]);
            }
            else
            {
                SetNetworkStatus(NetworkStatusKind.Ok, SL["Network_StatusOk"]);
            }
        }
        catch (Exception ex)
        {
            SetNetworkStatus(NetworkStatusKind.Error, ex.Message);
            Log.Warn($"[Settings] Ошибка проверки сети: {ex.Message}");
        }
        finally
        {
            IsNetworkTesting = false;
        }
    }

    /// <summary>
    /// Выполняет один GET-запрос к <c>music.youtube.com/generate_204</c>.
    /// Возвращает <c>true</c> если сервер ответил любым кодом 2xx–3xx.
    /// </summary>
    private static async Task<bool> ProbeYoutubeAsync()
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            using var request = new HttpRequestMessage(
                HttpMethod.Get, "https://music.youtube.com/generate_204");

            request.Version = HttpVersion.Version11;
            request.Headers.TryAddWithoutValidation("User-Agent", YoutubeClientUtils.UaWebRemix);

            using var response = await Core.Audio.Http.SharedHttpClient.Instance
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token)
                .ConfigureAwait(false);

            return (int)response.StatusCode is >= 200 and <= 399;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Проверяет наличие активного VPN-интерфейса среди сетевых адаптеров Windows.
    /// Детектирует Tunnel-тип, а также TAP/TUN/WireGuard/OpenVPN/Cisco по имени и описанию.
    /// </summary>
    private static bool DetectVpnInterface()
    {
        try
        {
            foreach (var iface in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                if (iface.OperationalStatus !=
                    System.Net.NetworkInformation.OperationalStatus.Up)
                    continue;

                if (iface.NetworkInterfaceType ==
                    System.Net.NetworkInformation.NetworkInterfaceType.Tunnel)
                    return true;

                var name = iface.Name.ToLowerInvariant();
                var desc = iface.Description.ToLowerInvariant();

                if (name.Contains("vpn") || desc.Contains("vpn") ||
                    name.Contains("tap") || desc.Contains("tap") ||
                    name.Contains("tun") || desc.Contains("tun") ||
                    name.Contains("wg") || desc.Contains("wireguard") ||
                    desc.Contains("openvpn") || desc.Contains("cisco") ||
                    desc.Contains("nordvpn") || desc.Contains("expressvpn"))
                    return true;
            }
        }
        catch (Exception ex)
        {
            Log.Debug($"[Settings] Ошибка детектирования VPN: {ex.Message}");
        }

        return false;
    }

    /// <summary>
    /// Устанавливает статус сети и синхронно уведомляет XAML об изменении цвета индикатора.
    /// </summary>
    private void SetNetworkStatus(NetworkStatusKind kind, string text)
    {
        NetworkStatus = kind;
        NetworkStatusText = text;
        this.RaisePropertyChanged(nameof(NetworkStatusColor));
        this.RaisePropertyChanged(nameof(HasLatency));
    }

    /// <inheritdoc />
    protected override void OnResume()
    {
        base.OnResume();

        // Синхронизируем настройки при восстановлении/разворачивании окна из фонового режима
        LoadAllSettings();
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (_isDisposed) return;
        if (disposing)
        {
            _isDisposed = true;
            LocalizationService.Instance.LanguageChanged -= OnLanguageChanged;
        }
        base.Dispose(disposing);
    }
}
