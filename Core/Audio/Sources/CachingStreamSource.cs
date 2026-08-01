using LMP.Core.Audio.Cache;
using LMP.Core.Audio.Interfaces;
using LMP.Core.Audio.Parsers;
using LMP.Core.Exceptions;

namespace LMP.Core.Audio.Sources;

/// <summary>
/// Источник аудио с range-based кэшированием и HTTP Range-request загрузкой.
///
/// <para><b>Архитектура:</b></para>
/// <list type="bullet">
///   <item>Данные загружаются произвольными диапазонами с адаптивным размером (MAPO)</item>
///   <item>Диапазоны кэшируются в RAM (<see cref="SlidingRamCache"/>) и на диск (<see cref="AudioCacheManager"/>)</item>
///   <item>Фоновый preload loop удерживает буфер вокруг текущей позиции</item>
///   <item>Seek реализован через epoch-based cancellation</item>
///   <item>Suspend/Resume приостанавливает фоновую загрузку при сворачивании окна</item>
/// </list>
///
/// <para><b>Partial class structure:</b></para>
/// <list type="bullet">
///   <item><c>CachingStreamSource.cs</c> — ядро: поля, init, read frames, dispose, network assessment</item>
///   <item><c>CachingStreamSource.Chunks.cs</c> — загрузка диапазонов, MAPO planner, RAM cache, dedup</item>
///   <item><c>CachingStreamSource.Seeking.cs</c> — seek с epoch-based cancellation</item>
///   <item><c>CachingStreamSource.Preload.cs</c> — фоновая загрузка и буферизация</item>
///   <item><c>CachingStreamSource.ReadStream.cs</c> — Stream-обёртка для парсеров</item>
/// </list>
/// </summary>
public sealed partial class CachingStreamSource : IAudioSource
{
    #region Constants

    /// <summary>Короткая задержка перед окончательной утилизацией ресурсов (мс).</summary>
    private const int DisposalDelayMs = 32;

    /// <summary>Таймаут ожидания открытия playback gate (мс).</summary>
    private const int PlaybackGateTimeoutMs = 128;

    /// <summary>Критический таймаут suspend-mode подзагрузки (мс).</summary>
    private const int PlaybackGateCriticalTimeoutMs = 512;

    /// <summary>Количество повторов чтения при смене эпохи.</summary>
    private const int ReadAtMaxEpochRetries = 3;

    /// <summary>Задержка между retry чтения при смене эпохи (мс).</summary>
    private const int ReadAtEpochRetryDelayMs = 30;

    /// <summary>Количество последовательных 403 refresh-failures перед открытием breaker.</summary>
    private const int MaxRefreshFailuresBeforeCircuitBreak = 2;

    /// <summary>Минимальная граница seek clamp.</summary>
    private const long SeekLowerBound = 0;

    /// <summary>Смещение для последнего байта контента.</summary>
    private const long SeekEndOffset = 1;

    /// <summary>Задержка перед утилизацией CTS предыдущей эпохи (мс).</summary>
    private const int DeferredEpochDisposeDelayMs = 2000;

    /// <summary>Таймаут ожидания завершения preload-task при DisposeAsync (мс).</summary>
    private const int PreloadTaskDisposeWaitTimeoutMs = 1000;

    /// <summary>Гистерезис буфера для предотвращения дрожания вокруг цели (мс).</summary>
    private const int TargetBufferHysteresisMs = 2500;

    /// <summary>Порог критического refill-режима (мс). Ниже — агрессивный prefetch.</summary>
    private const int CriticalRefillBufferMs = 4000;

    /// <summary>Аварийный порог буфера (мс). Ниже — максимально агрессивная загрузка.</summary>
    private const int EmergencyRefillBufferMs = 1500;

    #endregion

    #region Enums

    /// <summary>
    /// Уровень деградации текущего сетевого соединения.
    /// Влияет на параллелизм, размер запросов и агрессивность preload.
    /// </summary>
    private enum NetworkDegradationLevel
    {
        /// <summary>Сеть в норме. Стандартные параметры.</summary>
        Normal,

        /// <summary>Повышенная задержка или узкий канал. Ограниченный параллелизм.</summary>
        Degraded,

        /// <summary>Экстремальная задержка или минимальный канал. Один поток, маленькие запросы.</summary>
        Critical
    }

    #endregion

    #region Fields

    //  Configuration 
    private readonly StreamingConfig _config;

    //  Identity 
    private readonly string _cacheKey;
    private readonly string _trackId;
    private readonly long _contentLength;
    private readonly AudioFormat _format;
    private readonly int _bitrate;

    /// <summary>
    /// Флаг, указывающий, что в данный момент выполняется критическая сетевая
    /// операция для Seek. Preload loop должен приостановиться, чтобы не создавать
    /// конкуренцию за сеть.
    /// </summary>
    private volatile bool _seekInProgress;

    //  Transport alignment 
    private int _requestAlignmentBytes;

    //  Dependencies 
    private readonly HttpClient _httpClient;
    private readonly AudioCacheManager _cacheManager;

    /// <summary>
    /// Callback для первичного continuation acquire.
    /// Используется в <see cref="EnsureUrlAvailableAsync"/> при первом network miss.
    /// </summary>
    private readonly Func<CancellationToken, Task<string?>>? _urlAcquirer;

    /// <summary>
    /// Callback для forced URL refresh.
    /// Используется только при 403/expired URL.
    /// </summary>
    private readonly Func<CancellationToken, Task<string?>>? _urlRefresher;

    //  Parsing 

    /// <summary>
    /// Метаданные кэша текущего трека. Гарантированно не null после успешного
    /// <see cref="InitializeAsync"/>; обращение снаружи init — программная ошибка.
    /// </summary>
    private AudioCacheEntry? _cacheEntry;
    private IContainerParser? _parser;
    private AsyncCachingReadStream? _readStream;

    //  RAM cache & active downloads 

    /// <summary>
    /// RAM-кэш диапазонов байт, оптимизированный для малого количества активных блоков
    /// в скользящем окне вокруг текущей позиции воспроизведения.
    /// </summary>
    private readonly SlidingRamCache _ramCache = new();

    /// <summary>
    /// Реестр активных HTTP-загрузок для дедупликации.
    /// Ключ = начало выровненного диапазона. Строгая overlap-защита.
    /// </summary>
    private readonly Lock _activeDownloadsLock = new();
    private readonly Dictionary<long, ActiveRangeDownload> _activeDownloads = new(8);

    /// <summary>Семафор параллельных загрузок.</summary>
    private readonly SemaphoreSlim _downloadSlots;

    //  Epoch-based cancellation 
    private long _downloadEpoch;
    private CancellationTokenSource? _downloadCts;
    private readonly Lock _epochLock = new();

    /// <summary>
    /// Легковесный затвор для управления фоновым циклом предзагрузки.
    /// Set = воспроизведение активно, Reset = плеер на паузе.
    /// </summary>
    private readonly ManualResetEventSlim _playbackGate = new(initialState: true);

    /// <summary>
    /// ManualResetEventSlim для блокировки preload loop при suspend.
    /// Set = работаем, Reset = приостановлены.
    /// </summary>
    private readonly ManualResetEventSlim _suspendGate = new(initialState: true);

    //  Lifecycle 
    private CancellationTokenSource? _lifetimeCts;
    private Task? _preloadTask;

    /// <summary>Счётчик незавершённых фоновых disk-write операций.</summary>
    private int _pendingDiskWrites;

    /// <summary>Флаг того, что lease был успешно взят.</summary>
    private volatile bool _leaseAcquired;

    /// <summary>
    /// Single-flight promise для первичного получения continuation URL.
    /// Гарантирует, что одновременно выполняется только один URL resolution.
    /// Не используется для 403 refresh — тот идёт через <see cref="CoordinatedRefreshAsync"/>.
    /// </summary>
    private TaskCompletionSource<string?>? _continuationUrlTcs;
    private readonly Lock _continuationLock = new();

    //  Position tracking 
    private long _currentReadOffset;
    private long _positionMs;
    private string _currentUrl;

    //  Refresh / retry state 
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private DateTime _lastRefreshTime = DateTime.MinValue;
    private int _consecutive403Count;
    private int _requestSequenceNumber;
    private int _consecutiveRefreshFailures;
    private Exception? _lastDownloadException;

    //  Latency Tracking & Adaptive Transport 
    private readonly object _latencyLock = new();
    private double _latency0;
    private double _latency1;
    private double _latency2;
    private double _estimatedBandwidthBytesPerSec;

    /// <summary>
    /// Счётчик накопленных замеров bandwidth.
    /// Используется для определения bootstrap-фазы EMA:
    /// первые <see cref="StreamingConfig.BandwidthBootstrapSampleCount"/> замеров
    /// получают повышенный вес для быстрой начальной сходимости.
    /// </summary>
    private int _bandwidthSampleCount;

    //  State flags 
    private volatile bool _initialized;
    private volatile bool _disposed;

    #endregion

    #region Properties

    /// <summary>Текущая расчетная скорость загрузки данных из сети (байт/сек).</summary>
    public double EstimatedSpeedBytesPerSec
    {
        get { lock (_latencyLock) return _estimatedBandwidthBytesPerSec; }
    }

    /// <summary>Текущая средняя задержка сети (мс).</summary>
    public double AveragePingMs
    {
        get { lock (_latencyLock) return GetAverageLatencyInternal(); }
    }

    /// <inheritdoc/>
    public long DurationMs => _parser?.DurationMs ?? _cacheEntry?.DurationMs ?? -1;

    /// <inheritdoc/>
    public long PositionMs => Volatile.Read(ref _positionMs);

    /// <inheritdoc/>
    public bool CanSeek => true;

    /// <inheritdoc/>
    public AudioCodec Codec { get; private set; }

    /// <inheritdoc/>
    public byte[]? DecoderConfig => _parser?.DecoderConfig;

    /// <inheritdoc/>
    public int SampleRate => _parser?.SampleRate ?? 0;

    /// <inheritdoc/>
    public int Channels => _parser?.Channels ?? 0;

    /// <summary>Прогресс буферизации (0–100%).</summary>
    public double BufferProgress => _cacheEntry?.DownloadProgress ?? 0;

    /// <summary>
    /// Оценка непрерывного буфера вперёд от текущей позиции чтения в миллисекундах.
    /// </summary>
    /// <remarks>
    /// Использует локально доступные и in-flight диапазоны.
    /// Нужен для latency-aware решения о безопасном открытии playback gate.
    /// </remarks>
    public int BufferedAheadMs
    {
        get
        {
            if (!_initialized) return 0;

            long position = Volatile.Read(ref _currentReadOffset);
            if (position < 0 || position >= _contentLength) return 0;

            long bufferedAhead = GetBufferedBytesAheadIncludingInflight(position);
            return ConvertBufferedBytesToMs(bufferedAhead);
        }
    }

    /// <summary>Полностью ли загружен трек на диск.</summary>
    public bool IsFullyBuffered => _cacheEntry?.IsComplete ?? false;

    /// <summary>Объём скачанных данных в байтах.</summary>
    public long DownloadedBytes => _cacheEntry?.DownloadedBytes ?? 0;

    /// <summary>Битрейт (kbps).</summary>
    public int Bitrate => _cacheEntry?.Bitrate ?? _bitrate;

    /// <summary>Парсер контейнера. Доступен для диагностики.</summary>
    internal IContainerParser? Parser => _parser;

    /// <summary>Уникальный ключ записи в дисковом кэше.</summary>
    public string CacheKey => _cacheKey;

    /// <summary>Количество непрерывных байт от начала файла (contiguous prefix).</summary>
    public long ContiguousPrefixBytes => _cacheEntry?.GetContiguousDownloadedBytesFrom(0) ?? 0;

    /// <summary>
    /// Глобальное событие о non-fatal сетевых проблемах источника.
    /// </summary>
    public static event Action<string, Exception>? OnSourceWarning;

    #endregion

    #region Constructor

    /// <summary>
    /// Создаёт источник с range-based HTTP-стримингом.
    /// </summary>
    /// <param name="cacheKey">Уникальный ключ кэша (trackId + format + normalized_bitrate).</param>
    /// <param name="trackId">Идентификатор трека.</param>
    /// <param name="url">Исходный URL потока.</param>
    /// <param name="contentLength">Полный размер контента в байтах.</param>
    /// <param name="format">Аудио-формат контейнера.</param>
    /// <param name="codec">Аудио-кодек.</param>
    /// <param name="bitrate">Битрейт в kbps.</param>
    /// <param name="httpClient">HTTP-клиент для загрузки.</param>
    /// <param name="cacheManager">Менеджер дискового кэша.</param>
    /// <param name="config">Конфигурация стриминга.</param>
    /// <param name="urlRefresher">Делегат обновления URL при 403.</param>
    public CachingStreamSource(
        string cacheKey,
        string trackId,
        string url,
        long contentLength,
        AudioFormat format,
        AudioCodec codec,
        int bitrate,
        HttpClient httpClient,
        AudioCacheManager cacheManager,
        StreamingConfig config,
        Func<CancellationToken, Task<string?>>? urlAcquirer = null,
        Func<CancellationToken, Task<string?>>? urlRefresher = null)
    {
        _config = config;
        _cacheKey = cacheKey;
        _trackId = trackId;
        _currentUrl = url;
        _contentLength = contentLength;
        _format = format;
        _bitrate = bitrate;
        _httpClient = httpClient;
        _cacheManager = cacheManager;
        _urlAcquirer = urlAcquirer;
        _urlRefresher = urlRefresher;
        Codec = codec;

        _requestAlignmentBytes = Math.Max(4096, config.RequestAlignmentBytes);
        _downloadSlots = new SemaphoreSlim(config.MaxConcurrentDownloads);
    }

    #endregion

    #region Network Assessment

    /// <summary>
    /// Оценивает деградацию сети по совокупности трёх независимых сигналов:
    /// <list type="number">
    ///   <item>
    ///     <b>Абсолютный bandwidth</b> — сравнение с профильными порогами
    ///     (<see cref="StreamingConfig.AbsoluteCriticalBandwidthBytesPerSec"/>,
    ///     <see cref="StreamingConfig.AbsoluteDegradedBandwidthBytesPerSec"/>).
    ///     Защищает от ложного <c>Normal</c> на узком канале с низкобитрейтным аудио,
    ///     где relative ratio может быть велик (10 Мбит/с + 128 kbps → ratio ≈ 78).
    ///   </item>
    ///   <item>
    ///     <b>Жизнеспособность канала</b> — bandwidth должен превышать битрейт аудио
    ///     как минимум в <see cref="StreamingConfig.MinViableBandwidthMultiplier"/>×,
    ///     иначе буферизация невозможна в принципе.
    ///   </item>
    ///   <item>
    ///     <b>Relative ratio и RTT</b> — классическая оценка по отношению
    ///     bandwidth/bitrate и средней задержке.
    ///   </item>
    /// </list>
    /// Сигналы проверяются в порядке убывания жёсткости: первый сработавший
    /// определяет итоговый уровень.
    /// </summary>
    private NetworkDegradationLevel GetNetworkDegradationLevel()
    {
        double avgLatencyMs;
        double bw;

        lock (_latencyLock)
        {
            avgLatencyMs = GetAverageLatencyInternal();
            bw = _estimatedBandwidthBytesPerSec;
        }

        // Сигнал 1: абсолютный потолок канала
        // Relative ratio вводит в заблуждение на узких каналах с низким битрейтом.
        // Проверяем абсолютные пороги первыми, чтобы не пропустить критическое состояние.
        if (bw > 0)
        {
            if (bw < _config.AbsoluteCriticalBandwidthBytesPerSec)
                return NetworkDegradationLevel.Critical;

            if (bw < _config.AbsoluteDegradedBandwidthBytesPerSec)
                return NetworkDegradationLevel.Degraded;
        }

        double bitrateBps = Math.Max(1, _bitrate) * 1000.0 / 8.0;

        // Сигнал 2: жизнеспособность канала
        // Если bandwidth не обеспечивает даже MinViableBandwidthMultiplier×bitrate,
        // буфер будет истощаться быстрее, чем пополняться.
        if (bw > 0 && bw < bitrateBps * _config.MinViableBandwidthMultiplier)
            return NetworkDegradationLevel.Critical;

        // Сигнал 3: relative ratio и RTT
        double widthRatio = bw > 0 ? bw / bitrateBps : double.PositiveInfinity;

        if (avgLatencyMs > 2500 || widthRatio < 3.0)
            return NetworkDegradationLevel.Critical;

        if (avgLatencyMs > 800 || widthRatio < 6.0)
            return NetworkDegradationLevel.Degraded;

        return NetworkDegradationLevel.Normal;
    }

    /// <summary>
    /// Возвращает максимально допустимое число параллельных HTTP-загрузок
    /// с учётом текущего состояния сети.
    /// </summary>
    private int GetAdaptiveMaxConcurrentDownloads() => GetNetworkDegradationLevel() switch
    {
        NetworkDegradationLevel.Critical => 1,
        NetworkDegradationLevel.Degraded => Math.Min(2, _config.MaxConcurrentDownloads),
        _ => _config.MaxConcurrentDownloads
    };

    /// <summary>
    /// Возвращает адаптивный объём предварительной догрузки после seek.
    /// На узком канале seek-preload уменьшается.
    /// </summary>
    private int GetAdaptiveSeekPreloadBytes()
    {
        int preload = _config.SeekPreloadBytes;
        return GetNetworkDegradationLevel() switch
        {
            NetworkDegradationLevel.Critical =>
                Math.Max(_requestAlignmentBytes * 2, Math.Min(preload, _requestAlignmentBytes * 4)),
            NetworkDegradationLevel.Degraded =>
                Math.Max(_requestAlignmentBytes * 2, Math.Min(preload, _requestAlignmentBytes * 8)),
            _ => preload
        };
    }

    /// <summary>
    /// Переводит количество буферизованных байт аудио в миллисекунды воспроизведения.
    /// </summary>
    /// <param name="bytes">Количество байт аудиопотока.</param>
    /// <returns>Эквивалентная длительность в миллисекундах.</returns>
    private int ConvertBufferedBytesToMs(long bytes)
    {
        if (bytes <= 0) return 0;
        double bitrateBps = Math.Max(1, _bitrate) * 1000.0 / 8.0;
        return (int)(bytes / bitrateBps * 1000.0);
    }

    /// <summary>
    /// Вычисляет адаптивный целевой размер буфера на основе RTT и ширины канала.
    /// Высокий ping увеличивает цель, но узкий канал ограничивает бессмысленное раздувание.
    /// </summary>
    private int GetAdaptiveTargetBufferMs()
    {
        double avgLatencyMs;
        double bw;

        lock (_latencyLock)
        {
            avgLatencyMs = GetAverageLatencyInternal();
            bw = _estimatedBandwidthBytesPerSec;
        }

        int target = _config.TargetBufferMs;

        if (avgLatencyMs > 3000) target = Math.Min(target * 4, 45_000);
        else if (avgLatencyMs > 1500) target = Math.Min(target * 3, 32_000);
        else if (avgLatencyMs > 800) target = Math.Min(target * 2, 24_000);
        else if (avgLatencyMs > 300) target = Math.Min((target * 3) / 2, 18_000);

        if (bw > 0)
        {
            double bitrateBps = Math.Max(1, _bitrate) * 1000.0 / 8.0;
            double widthRatio = bw / bitrateBps;

            if (widthRatio < 2.0) target = Math.Min(target, 8_000);
            else if (widthRatio < 3.0) target = Math.Min(target, 12_000);
            else if (widthRatio < 5.0) target = Math.Min(target, 16_000);
        }

        return Math.Max(target, 4_000);
    }

    /// <summary>
    /// Возвращает минимальный непрерывный префикс данных (в байтах),
    /// необходимый для безопасного старта decoder после seek.
    /// </summary>
    /// <param name="position">Целевая позиция seek в байтах.</param>
    /// <returns>Требуемое количество contiguous-байт.</returns>
    private int GetMinimalSeekStartBytes(long position)
    {
        if (position >= _contentLength) return 0;

        var deg = GetNetworkDegradationLevel();

        // Снижены требования к startup prefix на слабых сетях.
        // Ожидание 8 секунд данных на канале 10 Мбит/с блокировало UI.
        // 2-3 секунды достаточно для запуска декодера, остальное дотянет фоновый поток.
        int desiredMs = deg switch
        {
            NetworkDegradationLevel.Critical => 2500,
            NetworkDegradationLevel.Degraded => 3000,
            _ => 3000
        };

        int minClamp = deg switch
        {
            NetworkDegradationLevel.Critical => 32 * 1024,
            NetworkDegradationLevel.Degraded => 48 * 1024,
            _ => 32 * 1024
        };

        int maxClamp = deg switch
        {
            NetworkDegradationLevel.Critical => 96 * 1024,
            NetworkDegradationLevel.Degraded => 128 * 1024,
            _ => 128 * 1024
        };

        double bitrateBps = Math.Max(1, _bitrate) * 1000.0 / 8.0;
        int byTime = (int)Math.Ceiling(bitrateBps * desiredMs / 1000.0);
        int required = Math.Max(_requestAlignmentBytes * 2, AlignUp(byTime, _requestAlignmentBytes));
        required = Math.Clamp(required, minClamp, maxClamp);

        long remaining = _contentLength - position;
        return (int)Math.Min(required, remaining);
    }

    /// <summary>
    /// Проверяет, достаточно ли локально доступных непрерывных данных
    /// для мгновенного старта decoder после seek.
    /// </summary>
    /// <param name="position">Целевая позиция seek в байтах.</param>
    /// <returns><c>true</c> если seek может стартовать без ожидания сети.</returns>
    private bool HasMinimalLocalSeekStartData(long position)
    {
        int required = GetMinimalSeekStartBytes(position);
        if (required <= 0) return true;

        long ramBytes = _ramCache.GetContiguousBytesFrom(position);
        long diskBytes = _cacheEntry?.GetContiguousDownloadedBytesFrom(position) ?? 0;
        long available = Math.Max(ramBytes, diskBytes);

        return available >= required;
    }

    /// <summary>
    /// Вычисляет адаптивный таймаут ожидания critical-range при seek.
    /// </summary>
    /// <param name="expectedBytes">Ожидаемый объём критического диапазона.</param>
    /// <returns>
    /// Таймаут в миллисекундах, учитывающий RTT, пропускную способность
    /// и стоимость переподключения на высоколатентных сетях.
    /// </returns>
    /// <remarks>
    /// <para>
    /// Предыдущий верхний clamp 5000ms был слишком агрессивен для high-latency сетей:
    /// первый полезный contiguous startup prefix мог приезжать через 8–12 секунд,
    /// хотя сама пропускная способность оставалась достаточной для playback.
    /// </para>
    /// </remarks>
    private int ComputeAdaptiveSeekCriticalTimeoutMs(int expectedBytes)
    {
        double avgLatencyMs;
        double bw;

        lock (_latencyLock)
        {
            avgLatencyMs = GetAverageLatencyInternal();
            bw = _estimatedBandwidthBytesPerSec;
        }

        double bitrateBps = Math.Max(1, _bitrate) * 1000.0 / 8.0;
        double fallbackBps = Math.Max(16 * 1024, bitrateBps * 2.0);
        double effectiveBps = bw > 0 ? Math.Max(bw * 0.35, fallbackBps) : fallbackBps;

        double transferMs = expectedBytes / effectiveBps * 1000.0;

        // На high-latency сети учитываем не только RTT запроса,
        // но и стоимость установления/переподъёма полезной цепочки байт для parser.
        double latencyBudgetMs = avgLatencyMs > 0
            ? Math.Max(800.0, avgLatencyMs * 3.0)
            : 1500.0;

        int timeoutMs = (int)Math.Ceiling(latencyBudgetMs + transferMs + 1000.0);

        return Math.Clamp(timeoutMs, 2500, 15000);
    }

    #endregion

    #region Public Seek Helpers

    /// <summary>
    /// Проверяет, доступен ли диапазон для заданной позиции seek (для AudioPlayer).
    /// </summary>
    /// <param name="positionMs">Позиция в миллисекундах.</param>
    /// <returns><c>true</c> если данные доступны в RAM или на диске.</returns>
    public bool IsTargetChunkAvailable(long positionMs)
    {
        if (_parser == null || !_initialized) return false;

        var seekInfo = _parser.FindSeekPosition(positionMs);
        if (seekInfo == null) return false;

        long targetBytePos = Math.Min(seekInfo.Value.BytePosition, Math.Max(0, _contentLength - 1));
        int requiredLength = GetAlignedReadLength(targetBytePos, _config.MinRequestSizeBytes);
        return IsRangeLocallyAvailable(targetBytePos, requiredLength);
    }

    /// <summary>
    /// Проверяет, достаточно ли contiguous-данных для безопасного старта decoder
    /// после ранее выполненного seek (для AudioPlayer polling).
    /// </summary>
    /// <param name="positionMs">Позиция в миллисекундах, к которой был выполнен seek.</param>
    /// <returns><c>true</c> если минимальный startup prefix доступен.</returns>
    public bool IsSeekDataReady(long positionMs)
    {
        if (_parser == null || !_initialized) return false;

        var seekInfo = _parser.FindSeekPosition(positionMs);
        if (seekInfo == null) return false;

        long targetBytePos = Math.Min(seekInfo.Value.BytePosition, Math.Max(0, _contentLength - 1));
        return HasMinimalLocalSeekStartData(targetBytePos);
    }

    #endregion
    #region Warning Events

    #endregion

    #region Initialization

    /// <inheritdoc/>
    public async ValueTask<bool> InitializeAsync(CancellationToken ct = default)
    {
        if (_initialized) return true;

        try
        {
            Log.Info($"[CachingSource] Initialize: track={_trackId}, cacheKey={_cacheKey}, format={_format}, codec={Codec}, bitrate={_bitrate}kbps, contentLength={_contentLength}, hasInitialUrl={!string.IsNullOrWhiteSpace(_currentUrl)}");

            _cacheManager.AcquireLease(_cacheKey);
            _leaseAcquired = true;

            _cacheEntry = _cacheManager.CreateOrUpdate(
                _cacheKey, _trackId, _currentUrl, _contentLength, _format,
                AudioSourceFactory.GetCodecForFormat(_format), _bitrate,
                alignmentBytes: _requestAlignmentBytes);

            Log.Debug($"[CachingSource] Cache entry: downloaded={_cacheEntry.DownloadedBytes}, total={_cacheEntry.TotalSize}, complete={_cacheEntry.IsComplete}, alignment={_cacheEntry.AlignmentBytes}");

            if (_cacheEntry.DownloadedBytes > 0)
            {
                _requestAlignmentBytes = Math.Max(4096, _cacheEntry.AlignmentBytes);
                Log.Info($"[CachingSource] Resuming: {_cacheEntry.DownloadedBytes}/{_cacheEntry.TotalSize} bytes");
            }

            _lifetimeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            InitializeFirstEpoch();

            int initialBytes = Math.Min(
                _config.InitialPrebufferBytes,
                (int)Math.Min(_contentLength, int.MaxValue));

            bool hasLocalBootstrap = HasLocalInitialBootstrapData(initialBytes);

            if (!hasLocalBootstrap)
            {
                if (string.IsNullOrWhiteSpace(_currentUrl))
                {
                    bool urlReady = await EnsureUrlAvailableAsync(_lifetimeCts.Token).ConfigureAwait(false);
                    if (!urlReady)
                        throw new InvalidOperationException("Failed to acquire continuation URL for source initialization");
                }

                await EnsureRangeAsync(0, initialBytes, _lifetimeCts.Token, isCritical: true)
                    .ConfigureAwait(false);
            }
            else
            {
                Log.Debug($"[CachingSource] Local bootstrap prefix is sufficient: {initialBytes} bytes");
            }

            _readStream = new AsyncCachingReadStream(this);
            _parser = CreateParser(_readStream);

            if (!await _parser.ParseHeadersAsync(ct).ConfigureAwait(false))
                throw new InvalidOperationException("Failed to parse container headers");

            Codec = _parser.Codec;
            _cacheEntry.Codec = Codec;
            _cacheEntry.DurationMs = _parser.DurationMs;
            _cacheEntry.Bitrate = _bitrate;
            _initialized = true;

            Log.Info($"[CachingSource] Parser ready: track={_trackId}, codec={Codec}, sampleRate={SampleRate}, channels={Channels}, duration={DurationMs}ms");

            // Startup Prefetch: заливаем warmup-буфер параллельно с decoder init
            FireStartupPrefetchIfNeeded(initialBytes);

            _preloadTask = Task.Run(
                () => PreloadLoopAsync(_lifetimeCts.Token), _lifetimeCts.Token);

            Log.Info($"[CachingSource] Initialized: duration={DurationMs}ms, " +
                     $"cached={_cacheEntry.DownloadProgress:F0}%");
            return true;
        }
        catch (Exception ex)
        {
            Log.Error($"[CachingSource] Init failed: {ex.Message}", ex);
            return false;
        }
    }

    /// <summary>
    /// Запускает немедленный fire-and-forget prefetch после успешного parser init.
    /// <para>
    /// Перекрывает мёртвое время между завершением <see cref="InitializeAsync"/>
    /// и первой итерацией preload loop (<see cref="StreamingConfig.PreloadIntervalMs"/>).
    /// Без этого на канале с TTFB 1–2.5 с warmup ждёт 500 мс + N round-trips,
    /// задерживая playback на 1–1.5 с.
    /// </para>
    /// </summary>
    /// <param name="alreadyFetchedBytes">
    /// Объём данных, уже полученных initial fetch. Prefetch начинается встык после них.
    /// </param>
    private void FireStartupPrefetchIfNeeded(int alreadyFetchedBytes)
    {
        if (_cacheEntry is { IsComplete: true })
            return;

        long prefetchStart = alreadyFetchedBytes;
        long remaining = _contentLength - prefetchStart;
        if (remaining <= 0)
            return;

        int prefetchLength = (int)Math.Min(_config.StartupPrefetchBytes, remaining);
        if (prefetchLength <= 0)
            return;

        Log.Debug(
            $"[CachingSource] Startup prefetch: {prefetchLength / 1024}KB " +
            $"at offset {prefetchStart} (initial={alreadyFetchedBytes / 1024}KB)");

        _ = SafeStartupPrefetchAsync(prefetchStart, prefetchLength);
    }

    /// <summary>
    /// Best-effort фоновый prefetch для заполнения warmup-буфера.
    /// Ошибки не прерывают инициализацию — preload loop подхватит недокачанное.
    /// </summary>
    private async Task SafeStartupPrefetchAsync(long start, int length)
    {
        try
        {
            var token = CurrentDownloadToken;
            await EnsureRangeAsync(start, length, token, isCritical: false)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Debug($"[CachingSource] Startup prefetch failed (non-fatal): {ex.Message}");
        }
    }

    /// <summary>
    /// Проверяет, достаточно ли локально доступных данных от начала файла
    /// для безопасного bootstrap-старта parser/decoder без сетевого запроса.
    /// </summary>
    /// <param name="initialBytes">Минимальный стартовый префикс в байтах.</param>
    /// <returns>
    /// <c>true</c>, если contiguous local prefix от позиции 0 уже достаточен;
    /// иначе <c>false</c>.
    /// </returns>
    private bool HasLocalInitialBootstrapData(int initialBytes)
    {
        if (_cacheEntry == null || initialBytes <= 0)
            return false;

        long contiguous = _cacheEntry.GetContiguousDownloadedBytesFrom(0);
        return contiguous >= initialBytes;
    }

    /// <summary>Выбирает парсер контейнера на основе формата трека.</summary>
    private IContainerParser CreateParser(Stream stream) => _format switch
    {
        AudioFormat.WebM or AudioFormat.Ogg => new WebMContainerParser(stream),
        AudioFormat.Mp4 => new Mp4ContainerParser(stream),
        _ => throw new NotSupportedException($"Format not supported: {_format}")
    };

    /// <summary>Инициализирует первую эпоху загрузки.</summary>
    private void InitializeFirstEpoch()
    {
        lock (_epochLock)
        {
            _downloadCts = _lifetimeCts != null
                ? CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token)
                : new CancellationTokenSource();
            _downloadEpoch = 1;
        }
    }

    #endregion

    #region Reading

    /// <inheritdoc/>
    /// <remarks>
    /// <para><b>Self-Healing:</b> При обнаружении коррупции парсером запускается
    /// точечное восстановление диапазона без прерывания воспроизведения.</para>
    /// </remarks>
    public async ValueTask<AudioFrame?> ReadFrameAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!_initialized || _parser == null)
            throw new InvalidOperationException("Source not initialized");

        const int maxHealingAttempts = 3;

        for (int attempt = 0; attempt < maxHealingAttempts; attempt++)
        {
            try
            {
                var frame = await _parser.ReadNextFrameAsync(ct).ConfigureAwait(false);
                if (frame != null)
                {
                    Volatile.Write(ref _positionMs, frame.Value.TimestampMs);
                    UpdateCurrentReadOffset();
                }
                return frame;
            }
            catch (ParserCorruptionException ex)
            {
                await HealCorruptionAsync(ex.AbsoluteBytePosition, ct).ConfigureAwait(false);
            }
        }

        throw new InvalidDataException("Unrecoverable container corruption after max healing attempts.");
    }

    /// <summary>
    /// Self-healing: инвалидирует повреждённый диапазон и перекачивает его из сети.
    /// Если сети нет — помечает диапазон как мёртвый и делает resync.
    /// </summary>
    private async Task HealCorruptionAsync(long absoluteBytePosition, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        long rangeStart = AlignDown(absoluteBytePosition, _requestAlignmentBytes);
        int rangeLength = GetAlignedReadLength(rangeStart, _config.MinRequestSizeBytes);
        if (rangeLength <= 0) return;

        Log.Warn($"[SelfHealing] Corruption detected at byte {absoluteBytePosition} " +
                 $"(Range {rangeStart}-{rangeStart + rangeLength - 1})");

        if (_cacheEntry == null || _readStream == null || _parser == null) return;

        _cacheManager.InvalidateRange(_cacheKey, rangeStart, rangeLength);

        if (_ramCache.TryRemoveContaining(absoluteBytePosition, out var badBlock) && badBlock is not null)
            badBlock.Dispose();

        var healResult = await EnsureRangeAsync(rangeStart, rangeLength, ct, isCritical: true)
            .ConfigureAwait(false);

        if (healResult == RangeDownloadResult.Success)
        {
            Log.Info($"[SelfHealing] Range {rangeStart}-{rangeStart + rangeLength - 1} healed from network.");
            _readStream.SeekAndCancelPendingReads(rangeStart);
            Volatile.Write(ref _currentReadOffset, rangeStart);
            _parser.RequireResync();
            return;
        }

        if (healResult == RangeDownloadResult.Cancelled && ct.IsCancellationRequested)
            ct.ThrowIfCancellationRequested();

        _cacheEntry.MarkRangeCorruptedOffline(rangeStart);
        long nextBoundary = Math.Min(rangeStart + rangeLength, _contentLength);

        Log.Warn($"[SelfHealing] Network unavailable. Marking range as dead for this session.");

        _readStream.SeekAndCancelPendingReads(nextBoundary);
        Volatile.Write(ref _currentReadOffset, nextBoundary);

        if (nextBoundary < _contentLength)
            _parser.RequireResync();
    }

    /// <summary>Обновляет текущую абсолютную позицию чтения для preload/eviction.</summary>
    private void UpdateCurrentReadOffset()
    {
        if (_readStream != null)
            Volatile.Write(ref _currentReadOffset, _readStream.Position);
    }

    #endregion

    #region Epoch-Based Cancellation

    /// <summary>
    /// Откладывает <see cref="IDisposable.Dispose"/> для CTS,
    /// предотвращая <see cref="ObjectDisposedException"/> в конкурентных путях.
    /// </summary>
    private static void DeferDisposeCancellationTokenSource(CancellationTokenSource? cts, int delayMs)
    {
        if (cts == null) return;

        ThreadPool.UnsafeQueueUserWorkItem(static async state =>
        {
            var (source, delay) = ((CancellationTokenSource Source, int DelayMs))state!;
            try { await Task.Delay(delay).ConfigureAwait(false); } catch { }
            try { source.Dispose(); } catch (ObjectDisposedException) { }
        }, (cts, delayMs));
    }

    /// <summary>Отменяет все загрузки текущей эпохи и создаёт новую.</summary>
    private CancellationToken ResetDownloadEpoch()
    {
        lock (_epochLock)
        {
            var oldCts = _downloadCts;

            _downloadCts = _lifetimeCts != null
                ? CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token)
                : new CancellationTokenSource();

            Interlocked.Increment(ref _downloadEpoch);

            if (oldCts != null)
            {
                ThreadPool.UnsafeQueueUserWorkItem(static state =>
                {
                    try { ((CancellationTokenSource)state!).Cancel(); }
                    catch (ObjectDisposedException) { }
                }, oldCts);

                DeferDisposeCancellationTokenSource(oldCts, DeferredEpochDisposeDelayMs);
            }

            return _downloadCts.Token;
        }
    }

    /// <summary>CancellationToken текущей эпохи загрузки. Потокобезопасно.</summary>
    private CancellationToken CurrentDownloadToken
    {
        get
        {
            lock (_epochLock)
                return _downloadCts?.Token ?? CancellationToken.None;
        }
    }

    /// <summary>Мгновенно отменяет активные чтения на потоке без уничтожения источника.</summary>
    public void CancelActiveReads() => _readStream?.CancelActiveReads();

    #endregion

    #region Public Buffer Management

    /// <inheritdoc/>
    public void ReleaseRamBuffers()
    {
        long currentOffset = Volatile.Read(ref _currentReadOffset);
        _ramCache.Trim(currentOffset, _config.RamEvictionWindowBytes, _config.MaxRamBytes);
    }

    /// <inheritdoc/>
    public void CancelPendingOperations() => _lifetimeCts?.Cancel();

    /// <inheritdoc/>
    public void SetPlaybackActive(bool active)
    {
        if (_disposed) return;

        if (active) _playbackGate.Set();
        else _playbackGate.Reset();

        Log.Debug($"[CachingSource] Playback active state updated: {active}");
    }

    /// <summary>
    /// Пытается прикрепить continuation URL, полученный out-of-band,
    /// к уже работающему source.
    /// </summary>
    /// <param name="url">Готовый финальный stream URL.</param>
    /// <returns>
    /// <c>true</c>, если URL был принят;
    /// <c>false</c>, если source уже имел URL, был disposed или входной URL невалиден.
    /// </returns>
    internal bool TryAttachContinuationUrl(string url)
    {
        if (_disposed) return false;
        if (string.IsNullOrWhiteSpace(url)) return false;

        TaskCompletionSource<string?>? pendingTcs;

        lock (_continuationLock)
        {
            if (!string.IsNullOrWhiteSpace(_currentUrl))
                return false;

            _currentUrl = url;
            pendingTcs = _continuationUrlTcs;
            _continuationUrlTcs = null;
        }

        _cacheEntry?.OriginalUrl = url;

        pendingTcs?.TrySetResult(url);

        var host = Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : "?";
        Log.Info($"[CachingSource] Continuation URL attached externally: track={_trackId}, host={host}");
        return true;
    }

    /// <summary>
    /// Атомарно обновляет stream URL после успешного refresh.
    /// Сбрасывает счётчик consecutive 403 и обновляет URL в cache entry.
    /// </summary>
    /// <param name="newUrl">Новый валидный stream URL.</param>
    internal void UpdateUrl(string newUrl)
    {
        if (_disposed || string.IsNullOrWhiteSpace(newUrl))
            return;

        _currentUrl = newUrl;
        Volatile.Write(ref _consecutive403Count, 0);

        _cacheEntry?.OriginalUrl = newUrl;

        string host = Uri.TryCreate(newUrl, UriKind.Absolute, out var uri) ? uri.Host : "?";
        Log.Warn($"[CachingSource] URL updated after refresh: track={_trackId}, host={host}, consecutive403Reset=true");
    }

    #endregion

    #region Warning Events

    /// <summary>
    /// Публикует non-fatal предупреждение источника для внешнего оркестратора UI.
    /// </summary>
    /// <param name="exception">Причина предупреждения.</param>
    private void PublishSourceWarning(Exception exception)
    {
        var handler = OnSourceWarning;
        if (handler is null) return;

        try
        {
            handler(_trackId, exception);
        }
        catch (Exception ex)
        {
            Log.Warn($"[CachingSource] Source warning callback failed: {ex.Message}");
        }
    }

    #endregion

    #region Dispose

    /// <summary>Общий эпилог dispose: освобождение всех ресурсов.</summary>
    private void DisposeSharedResources()
    {
        try { _lifetimeCts?.Dispose(); } catch (ObjectDisposedException) { }

        _readStream?.Dispose();
        DisposeAllRamChunks();

        lock (_continuationLock)
        {
            var tcs = _continuationUrlTcs;
            _continuationUrlTcs = null;
            tcs?.TrySetResult(null);
        }

        try { _refreshLock.Dispose(); } catch (ObjectDisposedException) { }
        try { _downloadSlots.Dispose(); } catch (ObjectDisposedException) { }

        _suspendGate.Dispose();
        _playbackGate.Dispose();

        if (_leaseAcquired)
            _cacheManager.ReleaseLease(_cacheKey);
    }

    /// <summary>Диспозит все блоки в RAM-кэше.</summary>
    private void DisposeAllRamChunks() => _ramCache.DisposeAll();

    /// <summary>Общая преамбула dispose: разблокировка gates, cancel epoch + lifetime.</summary>
    private void BeginDispose()
    {
        _suspendGate.Set();
        _playbackGate.Set();

        CancellationTokenSource? downloadCtsToDispose;

        lock (_epochLock)
        {
            downloadCtsToDispose = _downloadCts;
            _downloadCts = null;
        }

        if (downloadCtsToDispose != null)
        {
            try { downloadCtsToDispose.Cancel(); }
            catch (ObjectDisposedException) { }

            DeferDisposeCancellationTokenSource(downloadCtsToDispose, DeferredEpochDisposeDelayMs);
        }

        try { _lifetimeCts?.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        BeginDispose();
        _parser?.Dispose();
        DrainPendingDiskWritesSync();
        DisposeSharedResources();
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        BeginDispose();

        if (_preloadTask != null)
        {
            try
            {
                await _preloadTask
                    .WaitAsync(TimeSpan.FromMilliseconds(PreloadTaskDisposeWaitTimeoutMs))
                    .ConfigureAwait(false);
            }
            catch { }
        }

        await DrainPendingDiskWritesAsync().ConfigureAwait(false);
        await Task.Delay(DisposalDelayMs).ConfigureAwait(false);

        if (_parser != null)
            await _parser.DisposeAsync().ConfigureAwait(false);

        DisposeSharedResources();
    }

    /// <summary>
    /// Ожидает завершения всех фоновых disk-write операций перед освобождением lease.
    /// </summary>
    private async Task DrainPendingDiskWritesAsync()
    {
        const int maxWaitMs = 2000;
        const int pollIntervalMs = 25;
        int elapsed = 0;

        while (Volatile.Read(ref _pendingDiskWrites) > 0 && elapsed < maxWaitMs)
        {
            await Task.Delay(pollIntervalMs).ConfigureAwait(false);
            elapsed += pollIntervalMs;
        }

        int remaining = Volatile.Read(ref _pendingDiskWrites);
        if (remaining > 0)
            Log.Warn($"[CachingSource] {remaining} pending disk writes not drained within {maxWaitMs}ms");
    }

    /// <summary>Sync fallback для дренажа pending writes.</summary>
    private void DrainPendingDiskWritesSync()
    {
        const int maxWaitMs = 500;
        const int pollIntervalMs = 10;
        int elapsed = 0;

        while (Volatile.Read(ref _pendingDiskWrites) > 0 && elapsed < maxWaitMs)
        {
            Thread.Sleep(pollIntervalMs);
            elapsed += pollIntervalMs;
        }
    }

    #endregion
}