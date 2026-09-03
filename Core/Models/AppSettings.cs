using LMP.Core.Audio.Normalization;

namespace LMP.Core.Models;

public enum YoutubeClientProfile
{
    WebRemix,    // n + sig (Работают все ролики)
    AndroidVR,   // большинство роликов работают
    Web,         // n + sig
    TV,          // не работает
    Ios,         // HLS в основном
    AndroidMusic // требует доп. действий
}

public enum InternetProfile
{
    Low,    // Экономия трафика / Медленный интернет
    Medium, // Баланс (по умолчанию)
    High,   // Высокое качество / Быстрый интернет
    Ultra   // Максимальное кэширование / Локальная сеть
}

/// <summary>
/// Тип кривой интерполяции громкости.
/// </summary>
public enum VolumeCurveType
{
    /// <summary>Линейная: volume = t</summary>
    Linear,

    /// <summary>Квадратичная: volume = t² (по умолчанию, перцептивно линейная)</summary>
    Quadratic,

    /// <summary>Логарифмическая: volume = log2(1 + t) / log2(2)</summary>
    Logarithmic,

    /// <summary>Кубическая: volume = t³</summary>
    Cubic,

    /// <summary>
    /// "Скорость света": экспоненциальный рост в конце.
    /// Формула: volume = (e^(t*2) - 1) / (e² - 1)
    /// </summary>
    SpeedOfLight
}
/// <summary>
/// Стратегия обработки критических ошибок воспроизведения.
/// </summary>
/// <remarks>
/// <para>Определяет, требуется ли ручное действие пользователя или можно восстановиться автоматически.</para>
/// <para>Если выбран автоматический режим, конкретное действие с очередью определяется <see cref="PlaybackFailureBehavior"/>.</para>
/// <para>Используется в <see cref="UI.Services.PlaybackErrorOrchestrator"/>.</para>
/// </remarks>
public enum PlaybackErrorBehavior
{
    /// <summary>
    /// Остановить автоматическое восстановление и ждать явного действия пользователя.
    /// Если в очереди есть следующий трек — воспроизведение приостанавливается на текущем состоянии.
    /// Если трек последний — активный трек завершается полной остановкой.
    /// </summary>
    Dialog,

    /// <summary>
    /// Восстановиться автоматически и показать уведомление об ошибке.
    /// Конкретное действие после ошибки определяется <see cref="PlaybackFailureBehavior"/>.
    /// </summary>
    ToastAndSkip,

    /// <summary>
    /// Восстановиться автоматически без показа уведомления.
    /// Конкретное действие после ошибки определяется <see cref="PlaybackFailureBehavior"/>.
    /// </summary>
    Ignore
}

/// <summary>
/// Режим отображения уведомлений при сложной дешифровке n-токена.
/// </summary>
public enum NTokenNotificationMode
{
    /// <summary>
    /// Не создавать уведомление вовсе.
    /// </summary>
    Disabled = 0,

    /// <summary>
    /// Показывать всплывающее toast-уведомление и сохранять запись в центре уведомлений.
    /// </summary>
    Toast = 1,

    /// <summary>
    /// Сохранять запись только в центре уведомлений без toast.
    /// </summary>
    PanelOnly = 2
}

/// <summary>
/// Поведение автоматического восстановления после критической ошибки воспроизведения.
/// </summary>
/// <remarks>
/// Применяется только если <see cref="AudioSettings.CriticalErrorBehavior"/> допускает автоматическое восстановление.
/// </remarks>
public enum PlaybackFailureBehavior
{
    /// <summary>
    /// Автоматически переключиться на следующий трек и продолжить воспроизведение.
    /// </summary>
    SkipAndPlay,

    /// <summary>
    /// Автоматически переключиться на следующий трек и приостановить воспроизведение.
    /// </summary>
    SkipAndPause,

    /// <summary>
    /// Выйти из текущего трека и полностью остановить воспроизведение без перехода к следующему.
    /// </summary>
    Stop
}

public enum RepeatMode
{
    None,
    One,
    All
}

public enum AudioQualityPreference
{
    BestAvailable,
    Standard
}

public sealed class ProxySettings
{
    public bool Enabled { get; set; } = false;
    public string Host { get; set; } = "";
    public int Port { get; set; } = 8080;
    public bool UseAuth { get; set; } = false;
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
}

/// <summary>
/// Настройки хранения данных.
/// </summary>
public sealed class StorageSettings
{
    /// <summary>
    /// Лимит кэша изображений в МБ.
    /// </summary>
    public int ImageCacheLimitMb { get; set; } = 500;

    /// <summary>
    /// Лимит кэша аудио (StreamCache) в МБ.
    /// Старые файлы удаляются автоматически при превышении.
    /// </summary>
    public int AudioCacheLimitMb { get; set; } = 2048;

    public int DownloadedTracksLimitMb { get; set; } = 5000;

    /// <summary>
    /// Максимальное количество изображений в RAM-кэше.
    /// </summary>
    public int MaxBitmapCacheItems { get; set; } = 25;

    /// <summary>
    /// Автоматически сохранять полностью закэшированные треки в папку Downloads.
    /// По умолчанию выключено для экономии места (файл дублируется).
    /// </summary>
    public bool AutoSaveToDownloads { get; set; } = false;
}

/// <summary>
/// Настройки аудио системы.
/// </summary>
public sealed class AudioSettings
{
    /// <summary>
    /// Включить boost громкости выше 100%.
    /// Если false — MaxVolume просто увеличивает точность (больше шагов).
    /// </summary>
    public bool VolumeBoostEnabled { get; set; } = true;

    /// <summary>
    /// Кривая интерполяции громкости.
    /// </summary>
    public VolumeCurveType VolumeCurve { get; set; } = VolumeCurveType.Quadratic;

    /// <summary>
    /// Нормализация громкости (статическое выравнивание уровней, аналог Spotify/YouTube Music).
    /// </summary>
    public bool NormalizationEnabled { get; set; } = true;

    /// <summary>
    /// Целевой уровень нормализации в LUFS.
    /// Диапазон: -24 (тише) .. -6 (громче). По умолчанию -14 (стандарт Spotify/YouTube Music).
    /// </summary>
    public float NormalizationTargetLufs { get; set; } = -14f;

    /// <summary>
    /// Максимальный gain нормализации. Ограничивает усиление тихих треков.
    /// Диапазон: 1.0 (без усиления) .. 6.0. По умолчанию 3.0.
    /// </summary>
    public float NormalizationMaxGain { get; set; } = 3.0f;

    /// <summary>
    /// Режим нормализации громкости.
    /// <para><b>Bidirectional</b> (по умолчанию): усиливает тихие и понижает громкие треки.
    /// Обеспечивает одинаковую громкость при любом контенте.</para>
    /// <para><b>DownwardOnly</b>: только понижение громких треков, как на YouTube.
    /// Тихие треки воспроизводятся на исходном уровне.</para>
    /// </summary>
    public NormalizationMode NormalizationMode { get; set; } = NormalizationMode.Bidirectional;

    /// <summary>
    /// Стратегия обработки критических ошибок воспроизведения.
    /// </summary>
    /// <remarks>
    /// <para>Определяет, требуется ли ручное действие пользователя или можно восстановиться автоматически.</para>
    /// <para>Если выбран автоматический режим, конкретное действие с очередью задаётся свойством <see cref="PlaybackFailureBehavior"/>.</para>
    /// <para>НЕ применяется к:</para>
    /// <list type="bullet">
    ///   <item>BotDetectionException — всегда показывает диалог с таймером</item>
    ///   <item>LoginRequiredException — всегда показывает диалог/уведомление, требующее внимания пользователя</item>
    /// </list>
    /// </remarks>
    public PlaybackErrorBehavior CriticalErrorBehavior { get; set; } = PlaybackErrorBehavior.ToastAndSkip;

    /// <summary>
    /// Режим отображения уведомлений при сложной расшифровке n-токена.
    /// </summary>
    public NTokenNotificationMode NTokenNotificationMode { get; set; } = NTokenNotificationMode.Toast;

    /// <summary>
    /// Действие автоматического восстановления после критической ошибки воспроизведения.
    /// </summary>
    /// <remarks>
    /// Применяется только если <see cref="CriticalErrorBehavior"/> допускает автоматическое восстановление.
    /// </remarks>
    public PlaybackFailureBehavior PlaybackFailureBehavior { get; set; } = PlaybackFailureBehavior.SkipAndPause;

    /// <summary>
    /// Воспроизводить звук при ошибке воспроизведения.
    /// </summary>
    public bool PlayErrorSound { get; set; } = true;

    /// <summary>
    /// Пропускать треки, требующие сложной расшифровки n-токена.
    /// </summary>
    public bool SkipNTokenTracks { get; set; } = true;
}

/// <summary>
/// Настройки панели уведомлений: поведение, отображение и авто-очистка.
/// <para>
/// Единая точка конфигурации всего, что связано с уведомлениями.
/// <see cref="NotificationService"/> читает эти значения напрямую,
/// что устраняет жёсткие константы в коде сервиса.
/// </para>
/// </summary>
public sealed class NotificationSettings
{
    /// <summary>
    /// Ширина панели уведомлений в пикселях.
    /// </summary>
    public int PanelWidth { get; set; } = 360;

    /// <summary>
    /// Максимум уведомлений в памяти и в панели.
    /// При превышении вытесняются самые старые.
    /// </summary>
    public int MaxInPanelCount { get; set; } = 50;

    /// <summary>
    /// Включить авто-очистку уведомлений по возрасту.
    /// </summary>
    public bool AutoCleanupEnabled { get; set; } = true;

    /// <summary>
    /// Возраст уведомления в часах, после которого оно удаляется автоматически.
    /// По умолчанию 48 ч (2 дня).
    /// </summary>
    public int AutoCleanupAfterHours { get; set; } = 48;

    /// <summary>
    /// Интервал фоновой проверки авто-очистки в минутах.
    /// </summary>
    public int CleanupCheckIntervalMinutes { get; set; } = 30;
}

/// <summary>
/// Настройки автоматической очистки памяти.
/// </summary>
public sealed class MemorySettings
{
    /// <summary>
    /// Включить автоматическую очистку памяти по таймеру.
    /// </summary>
    public bool AutoCleanupEnabled { get; set; } = true;

    /// <summary>
    /// Интервал автоочистки в минутах.
    /// </summary>
    public int AutoCleanupIntervalMinutes { get; set; } = 30;

    /// <summary>
    /// Порог памяти (MB) при превышении которого запускается агрессивная очистка.
    /// 0 = отключено.
    /// </summary>
    public int PressureThresholdMb { get; set; } = 400;
}

/// <summary>
/// Application settings. Stored as JSON in Settings table.
/// </summary>
public sealed class AppSettings
{
    // === Audio ===

    /// <summary>
    /// Текущий уровень громкости плеера в процентах (0–100).
    /// </summary>
    public int Volume { get; set; } = 50;

    public bool ShuffleEnabled { get; set; }
    public RepeatMode RepeatMode { get; set; } = RepeatMode.None;
    public int MaxVolumeLimit { get; set; } = 100;
    public float TargetGainDb { get; set; } = 0f;
    public AudioQualityPreference QualityPreference { get; set; } = AudioQualityPreference.BestAvailable;
    public bool RememberTrackFormat { get; set; } = true;

    /// <summary>
    /// Расширенные настройки аудио.
    /// </summary>
    public AudioSettings Audio { get; set; } = new();

    // === Network ===
    public InternetProfile InternetProfile { get; set; } = InternetProfile.Medium;
    public ProxySettings Proxy { get; set; } = new();
    public StorageSettings Storage { get; set; } = new();

    // === UI ===
    public double PlaylistHeaderHeight { get; set; } = 300;
    public string LanguageCode { get; set; } = "en";
    public string DownloadPath { get; set; } = string.Empty;
    public bool DiscordRpcEnabled { get; set; } = true;
    public bool AutoPlayOnUrlPaste { get; set; } = true;
    public int LoadBatchSize { get; set; } = 20;
    public int SearchBatchSize { get; set; } = 30;
    public bool EnableSearchCache { get; set; } = true;
    public int SearchCacheTtlMinutes { get; set; } = 120;

    /// <summary>
    /// Оптимизировать UI при потере фокуса окна.
    /// 
    /// <para><b>true:</b> При сворачивании или потере фокуса приостанавливаются
    /// тяжёлые UI-обновления (позиция, буфер, анимации). Экономит CPU.</para>
    /// 
    /// <para><b>false:</b> UI работает штатно даже без фокуса.
    /// Полезно для пользователей со вторым монитором.</para>
    /// 
    /// <para>Настройка НЕ влияет на сворачивание в tray — там всегда оптимизация.</para>
    /// </summary>
    public bool OptimizeWhenInactive { get; set; } = true;

    /// <summary>
    /// Действие при нажатии кнопки закрытия окна.
    /// По умолчанию — спрашивать.
    /// </summary>
    public CloseAction CloseAction { get; set; } = CloseAction.Ask;

    /// <summary>
    /// Сворачивать в системный трей при нажатии кнопки минимизации.
    /// По умолчанию false — обычное сворачивание в панель задач.
    /// </summary>
    public bool MinimizeToTray { get; set; } = false;

    /// <summary>
    /// Настройки панели уведомлений и авто-очистки.
    /// </summary>
    public NotificationSettings Notifications { get; set; } = new();

    /// <summary>
    /// Режим синхронизации лайков с YouTube.
    /// </summary>
    public LikeSyncMode LikeSyncMode { get; set; } = LikeSyncMode.MusicOnly;

    /// <summary>
    /// Настройки управления памятью.
    /// </summary>
    public MemorySettings Memory { get; set; } = new();
}