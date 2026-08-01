using System.Collections.Concurrent;
using System.Threading.Channels;
using LMP.Core.Audio.Cache;
using LMP.Core.Audio.Helpers;
using LMP.Core.Audio.Http;
using LMP.Core.Audio.Interfaces;
using LMP.Core.Audio.Normalization;
using LMP.Core.Exceptions;
using LMP.Core.Youtube.Utils;
using ReactiveUI;


namespace LMP.Core.Services;

/// <summary>
/// Центральный движок аудио воспроизведения.
/// Координирует AudioPlayer, очередь треков, громкость и UI события.
/// </summary>
/// <remarks>
/// <para>Освобожден от наследования UI-класса ViewModelBase для строгого разделения слоев Core и UI </para>
/// </remarks>
public sealed partial class AudioEngine : ReactiveObject, ISuspendable, IDisposable, IAsyncDisposable
{
    #region Engine Command Types

    /// <summary>Маркерный интерфейс для typed commands AudioEngine.</summary>
    private interface IEngineCommand { }

    /// <summary>Воспроизвести конкретный трек с опциональной позиции (для бесшовного восстановления).</summary>
    /// <param name="Track">Информация о треке.</param>
    /// <param name="Session">ID сессии для отмены устаревших команд.</param>
    /// <param name="SeekPosition">Позиция для старта воспроизведения.</param>
    /// <param name="IsRetry">Флаг автоматического перезапуска при ошибке кэша.</param>
    private sealed record PlayTrackCommand(
        TrackInfo Track,
        int Session,
        TimeSpan? SeekPosition = null,
        bool IsRetry = false) : IEngineCommand;

    /// <summary>Запустить очередь с указанного трека.</summary>
    private sealed record StartQueueCommand(IEnumerable<TrackInfo> Tracks, TrackInfo StartTrack, int Session) : IEngineCommand;

    /// <summary>Воспроизвести текущий индекс очереди.</summary>
    private sealed record PlayCurrentIndexCommand(int Session) : IEngineCommand;

    /// <summary>Навигация вперёд/назад.</summary>
    /// <param name="Forward">Направление движения.</param>
    /// <param name="UserInitiated">Инициировано ли пользователем.</param>
    /// <param name="StartPlaying">Запускать ли воспроизведение на целевом треке.</param>
    private sealed record NavigateCommand(bool Forward, bool UserInitiated, bool StartPlaying = true) : IEngineCommand;

    /// <summary>Смена формата/качества активного трека (встраивается в очередь для избежания гонки состояний).</summary>
    private sealed record SwitchQualityCommand(
        TrackInfo Track,
        TimeSpan Position,
        AudioFormat Format,
        int Bitrate,
        int Session) : IEngineCommand;

    #endregion

    #region Constants

    private const int CommandQueueCapacity = 32;

    /// <summary>Базовый диапазон громкости (0-200 = 0-100% без boost).</summary>
    public const int VolumeNormalRange = 200;

    /// <summary>Максимальный gain (защита от перегрузки).</summary>
    public const float MaxGain = 4.0f;

    private const int QualitySwitchCooldownMs = 2000;

    /// <summary>
    /// Целевой объём локального contiguous префикса для Partial Cache Fast Start.
    /// </summary>
    private const int PartialCacheBootstrapTargetMs = 12_000;

    /// <summary>
    /// Нижняя граница contiguous bootstrap-префикса в байтах.
    /// </summary>
    private const int PartialCacheBootstrapMinBytes = 96 * 1024;

    /// <summary>
    /// Верхняя граница contiguous bootstrap-префикса в байтах.
    /// </summary>
    private const int PartialCacheBootstrapMaxBytes = 384 * 1024;

    /// <summary>
    /// Максимальное число автоматических попыток восстановления после
    /// recoverable <see cref="CacheInvalidatedException"/> для одного трека.
    /// </summary>
    private const int MaxCacheAutoRetries = 2;

    /// <summary>
    /// Окно EBU R128 pre-scan для full-cache источника (LocalFileSource).
    /// Файл полностью доступен локально — можно позволить более глубокий анализ.
    /// </summary>
    private const int PreScanDurationFullCacheMs = 80_000;

    /// <summary>
    /// Окно EBU R128 pre-scan для streaming и partial-cache источников.
    /// Ограничено для минимизации задержки первого аудио-фрейма.
    /// </summary>
    private const int PreScanDurationStreamingMs = 45_000;

    #endregion

    #region Dependencies

    private readonly YoutubeProvider _youtube;
    private readonly LibraryService _library;
    private readonly AudioPlayer _player;
    private readonly TrackRegistry _trackRegistry; // Добавлено внедрение зависимости L1-кэша

    #endregion

    #region Synchronization

    private readonly Channel<IEngineCommand> _commandQueue;
    private readonly CancellationTokenSource _lifetimeCts = new();
    private readonly Lock _queueLock = new();

    private SessionGuard _session;
    private CancellationTokenSource? _sessionCts;
    private readonly Lock _sessionLock = new();

    private CancellationTokenSource? _networkRebuildCts;
    private readonly Lock _networkRebuildLock = new();
    private string? _lastOutboundIp;
    private Task? _networkWatchdogTask;

    /// <summary>
    /// Single-flight задачи первичного получения continuation URL по trackId.
    /// Разделяются между background priming и source-level lazy acquire.
    /// </summary>
    private readonly ConcurrentDictionary<string, Task<ContinuationUrlResult?>> _pendingUrlAcquisitions
        = new(StringComparer.Ordinal);

    /// <summary>
    /// Результат получения continuation URL с сопутствующей metadata stream variant.
    /// </summary>
    private readonly record struct ContinuationUrlResult(
        string Url,
        long Size,
        int Bitrate,
        AudioFormat Format,
        AudioCodec Codec,
        float IntegratedLufs = float.NaN);

    /// <summary>
    /// Task цикла обработки команд (<see cref="ProcessCommandsAsync"/>).
    /// Сохраняется для детерминированного ожидания при dispose:
    /// без ожидания возможна гонка — команда уже извлечена из канала,
    /// но handler ещё не завершил мутацию состояния плеера.
    /// </summary>
    private Task? _commandProcessorTask;

    /// <summary>
    /// Task цикла сохранения громкости (<see cref="VolumeSaveLoopAsync"/>).
    /// Ожидается при dispose чтобы гарантировать flush последнего pending-write
    /// в персистентное хранилище до закрытия БД.
    /// </summary>
    private Task? _volumeSaveTask;

    /// <summary>
    /// Единственная активная задача подготовки воспроизведения.
    /// Гарантирует single-flight: при поступлении нового PlayTrackCoreAsync
    /// предыдущая задача отменяется через session CTS, и новая ожидается actor'ом.
    /// Хранится для детерминированного ожидания — исключает overlap
    /// нескольких параллельных ResolveStreamAsync / _player.PlayAsync.
    /// </summary>
    private Task? _activePlayTask;

    #endregion

    #region Playback State

    private volatile bool _isSuspended;
    private DateTime _lastQualitySwitchTime = DateTime.MinValue;
    private string? _nTokenActiveTrackId;
    private string? _nTokenWarnedTrackId;
    private string? _sealedFailedTrackId;
    private volatile bool _isManualLoading;

    /// <summary>
    /// Очередь отложенных записей integrated loudness в БД.
    /// Используется для новой LUFS-модели.
    /// </summary>
    private readonly ConcurrentQueue<(string TrackId, float IntegratedLufs, LoudnessSource Source)> _pendingNormalizationWrites = new();

    /// <summary>
    /// Флаг завершённого dispose. Предотвращает double-dispose
    /// при вызове обоих путей (<see cref="DisposeAsync"/> и <see cref="Dispose(bool)"/>).
    /// </summary>
    private volatile bool _disposed;

    /// <summary>
    /// Счётчик автоматических retry для текущего трека при recoverable cache ошибках.
    /// Сбрасывается при смене трека в <see cref="HandlePlayTrackAsync"/>
    /// и <see cref="HandleStartQueueAsync"/>.
    /// </summary>
    private int _cacheRetryCount;

    /// <summary>
    /// Признак того, что активный <see cref="Audio.Sources.CachingStreamSource"/>
    /// был реально приостановлен lifecycle-политикой движка.
    /// </summary>
    /// <remarks>
    /// Нужен для симметричного Resume: если suspend был пропущен из-за активного playback,
    /// нельзя безусловно дергать <c>Resume()</c> при возврате окна в активное состояние.
    /// </remarks>
    private int _sourceLifecycleSuspended;

    #endregion

    #region Observable Properties

    [Reactive] public partial TrackInfo? CurrentTrack { get; private set; }
    [Reactive] public partial AudioStreamInfo StreamInfo { get; private set; } = AudioStreamInfo.Empty;

    public bool IsPlaying => _player.State == PlaybackState.Playing;
    public bool IsPaused => _player.State == PlaybackState.Paused;

    /// <summary>
    /// Возвращает true, если плеер выполняет буферизацию, загрузку или находится в процессе перемещения.
    /// </summary>
    public bool IsLoading => _isManualLoading || _player.State is PlaybackState.Loading or PlaybackState.Buffering || _player.DetailedState == PlayerState.Seeking;

    public int CurrentQueueIndex => Volatile.Read(ref _currentIndex);
    public bool ShuffleEnabled { get; set; }
    public RepeatMode RepeatMode { get; set; }

    public TimeSpan CurrentPosition => _player.Position;
    public TimeSpan TotalDuration => _player.Duration;
    public double BufferProgress => _player.BufferProgress;
    public bool IsFullyBuffered => _player.IsFullyBuffered;

    /// <summary>Текущий gain после volume curve, boost и dB-коррекции.</summary>
    public float CurrentGain => _currentGain;

    #endregion

    #region Events

    public event Action<TrackInfo?>? OnTrackChanged;
    public event Action<TimeSpan>? OnPositionChanged;
    public event Action<TimeSpan>? OnSeekCompleted;
    public event Action<bool, bool>? OnPlaybackStateChanged;
    public event Action? OnQueueChanged;
    public event Action<bool>? OnLoadingStateChanged;
    public event Action<int>? OnMaxVolumeChanged;
    public event Action<AudioStreamInfo>? OnStreamInfoChanged;
    public event Action<BufferState>? OnBufferStateChanged;
    public event Action? OnDeviceLost;
    public event Action? OnDeviceRestored;
    public event Action<Exception>? OnErrorOccurred;
    public event Action<NTokenWarningInfo>? OnNTokenDecryptionWarning;

    /// <summary>Контекст предупреждения о n-токене.</summary>
    public readonly record struct NTokenWarningInfo(TrackInfo? Track, bool WasSkipped);

    private readonly Action<TimeSpan> _positionChangedHandler;
    private readonly Action _raisePositionChangedOnUIDelegate;
    private long _currentPositionTicks;

    private readonly Action<BufferState> _bufferStateChangedHandler;
    private readonly Action _raiseBufferStateOnUIDelegate;
    private BufferState _currentBufferState;
    private readonly Lock _bufferStateLock = new();

    private readonly Action<TimeSpan> _seekCompletedHandler;
    private readonly Action _raiseSeekCompletedOnUIDelegate;
    private long _seekCompletedTicks;

    private readonly Action _deviceLostHandler;
    private readonly Action _raiseDeviceLostOnUIDelegate;

    private readonly Action _deviceRestoredHandler;
    private readonly Action _raiseDeviceRestoredOnUIDelegate;

    #endregion

    #region Constructor

    /// <summary>
    /// Инициализирует центральный движок воспроизведения.
    /// </summary>
    public AudioEngine(YoutubeProvider youtube, LibraryService library, TrackRegistry trackRegistry)
    {
        _youtube = youtube;
        _library = library;
        _trackRegistry = trackRegistry;

        ApplyStreamingProfile();

        // Проверяем флаг готовности: если LibraryService уже инициализирован — применяем настройки,
        // если нет — подписываемся на событие завершения инициализации.
        if (_library.IsInitialized)
        {
            InitializeFromSettings();
        }
        else
        {
            _library.OnInitialized += InitializeFromSettings;
        }

        // Настройка делегатов один раз при создании класса. 
        // Исключает аллокацию замыканий в куче (Gen 0) во время проигрывания.
        _positionChangedHandler = HandlePositionChangedInternal;
        _raisePositionChangedOnUIDelegate = () => OnPositionChanged?.Invoke(TimeSpan.FromTicks(Volatile.Read(ref _currentPositionTicks)));

        _bufferStateChangedHandler = HandleBufferStateChangedInternal;
        _raiseBufferStateOnUIDelegate = () => OnBufferStateChanged?.Invoke(GetLatestBufferState());

        _seekCompletedHandler = HandleSeekCompletedInternal;
        _raiseSeekCompletedOnUIDelegate = () => OnSeekCompleted?.Invoke(TimeSpan.FromTicks(Volatile.Read(ref _seekCompletedTicks)));

        _deviceLostHandler = HandleDeviceLostInternal;
        _raiseDeviceLostOnUIDelegate = () => OnDeviceLost?.Invoke();

        _deviceRestoredHandler = HandleDeviceRestoredInternal;
        _raiseDeviceRestoredOnUIDelegate = () => OnDeviceRestored?.Invoke();

        _player = new AudioPlayer(new AudioPlayerOptions
        {
            UrlAcquireCallback = AcquireUrlCallbackAsync,
            UrlRefreshCallback = RefreshUrlCallbackAsync,
            PositionUpdateInterval = TimeSpan.FromMilliseconds(500),
            MaxRetryAttempts = 3,
            UseNullBackend = false,
            OnPipelineConfiguring = ConfigurePipelineBeforeStart,
            OnIntegratedLufsResolved = CommitIntegratedLufs,
            ShouldFastReplay = () => RepeatMode == RepeatMode.One,
            OnStarvationDetected = NotifyNetworkStarvation
        });

        SubscribeToPlayerEvents();
        _youtube.OnNTokenDecryptionStarted += HandleNTokenDecryptionStarted;
        CdnConnectionPreWarmer.OnTunnelDeadDetected += HandleCdnTunnelDead;
        InitializeFromSettings();

        _commandQueue = Channel.CreateBounded<IEngineCommand>(
            new BoundedChannelOptions(CommandQueueCapacity)
            {
                SingleReader = true,
                FullMode = BoundedChannelFullMode.DropOldest
            });

        _commandProcessorTask = Task.Run(ProcessCommandsAsync);
        _volumeSaveTask = Task.Run(VolumeSaveLoopAsync);

        // Внедряем регистрацию службы в реестре жизненного цикла
        LifecycleRegistry.Instance?.RegisterBackgroundSuspendable(this);

        _lastOutboundIp = GetOutboundIp();
        Log.Debug($"[AudioEngine] Initial outbound IP: {_lastOutboundIp ?? "(none)"}");

        System.Net.NetworkInformation.NetworkChange.NetworkAddressChanged
            += OnNetworkAddressChanged;

        // Watchdog — fallback для VPN без NetworkChange events
        _networkWatchdogTask = Task.Run(NetworkWatchdogAsync);
    }

    /// <summary>
    /// Конфигурирует pipeline перед открытием gate: громкость, нормализация и кроссфейдер.
    /// </summary>
    private void ConfigurePipelineBeforeStart(AudioPipeline pipeline, string? trackId)
    {
        trackId ??= pipeline.StreamInfo.TrackId;

        float volumeGain = ComputeFinalGain();
        _currentGain = volumeGain;
        _player.SetVolumeGain(volumeGain);

        var audioSettings = _library.Settings.Audio;

        // Pre-scan window:
        // Full-cache (LocalFileSource) — файл полностью доступен локально,
        // можно позволить более глубокий анализ без влияния на startup time.
        // Streaming/partial-cache — ограничиваем 30с, чтобы не задержать первый аудио-фрейм.
        bool isFullCache = pipeline.Source is Audio.Sources.LocalFileSource;
        int preScanDurationMs = isFullCache ? PreScanDurationFullCacheMs : PreScanDurationStreamingMs;

        var normConfig = new NormalizationConfig(
            audioSettings.NormalizationEnabled,
            audioSettings.NormalizationTargetLufs,
            audioSettings.NormalizationMaxGain,
            audioSettings.NormalizationMode,
            preScanDurationMs);

        pipeline.Analyzer.Configure(normConfig);

        Log.Debug($"[AudioEngine] Configuring pipeline for '{trackId}'. " +
                  $"Normalization: {normConfig.Enabled}, Mode: {normConfig.Mode}, " +
                  $"PreScan: {preScanDurationMs / 1000}s");

        if (normConfig.Enabled && !string.IsNullOrEmpty(trackId))
        {
            var registryTrack = _trackRegistry.TryGet(trackId) ?? _library.GetTrack(trackId);
            var currentTrack = CurrentTrack;
            var track = registryTrack ?? (currentTrack?.Id == trackId ? currentTrack : null);
            var cacheEntry = FindNormalizationCacheEntry(trackId);

            // HydrateNormalization делегирует приоритет в SetIntegratedLufs —
            // не перезапишет YoutubePerceptual значением EbuMeasured из кэша.
            if (track != null && cacheEntry != null)
                TrackNormalizationHydrator.HydrateNormalization(track, cacheEntry);

            float resolvedGain = track != null
                ? NormalizationGainResolver.Resolve(track, normConfig)
                : float.NaN;

            if (float.IsNaN(resolvedGain)
                && cacheEntry?.IntegratedLufs is float cacheIntegratedLufs
                && float.IsFinite(cacheIntegratedLufs))
            {
                resolvedGain = NormalizationGainResolver.ComputeGainFromIntegratedLufs(
                    cacheIntegratedLufs,
                    normConfig);
            }

#if DEBUG
            if (track != null)
            {
                Log.Debug($"[AudioEngine] Track resolved: ID={track.Id}, Title='{track.Title}' " +
                          $"| Source: {(registryTrack != null ? "Registry" : "CurrentTrackFallback")} " +
                          $"| Integrated LUFS: {(track.HasIntegratedLufs ? track.IntegratedLufs.ToString("F2") : "NaN")} " +
                          $"| LUFS Source: {track.IntegratedLufsSource} " +
                          $"| Cache LUFS: {(cacheEntry?.IntegratedLufs is float clufs ? clufs.ToString("F2") : "null")} " +
                          $"| Cache LUFS Source: {(cacheEntry != null ? ((LoudnessSource)cacheEntry.IntegratedLufsSource).ToString() : "null")} " +
                          $"| Resolved Gain: {(float.IsNaN(resolvedGain) ? "NaN" : resolvedGain.ToString("F4"))}");
            }
#endif

            if (!float.IsNaN(resolvedGain))
            {
                pipeline.Analyzer.LockResolvedGain(resolvedGain);
                Log.Info($"[AudioEngine] Normalization gain locked from LUFS metadata: {resolvedGain:F4}x for {trackId}");
            }
            else if (track != null && !(pipeline.Source is Audio.Sources.CachingStreamSource { IsFullyBuffered: false }))
            {
                Log.Warn($"[AudioEngine] Normalization resolver returned NaN for {trackId}. EBU R128 Pre-scan is REQUIRED.");
            }
        }

        pipeline.SnapCrossfaderToGain();
    }

    private void SubscribeToPlayerEvents()
    {
        _player.Events.PositionChanged += _positionChangedHandler;
        _player.Events.StateChanged += HandlePlayerStateChanged;
        _player.Events.TrackEnded += HandlePlayerTrackEnded;
        _player.Events.StreamInfoChanged += HandleStreamInfoChanged;
        _player.Events.BufferStateChanged += _bufferStateChangedHandler;
        _player.Events.SeekCompleted += _seekCompletedHandler;
        _player.Events.DeviceLost += _deviceLostHandler;
        _player.Events.DeviceRestored += _deviceRestoredHandler;

        _player.Events.ErrorOccurred += err =>
        {
            if (CancellationHelper.IsCancellationLike(err.Exception)) return;
            if (err.Exception is AudioSourceException && CancellationHelper.IsCancellationLike(err.Exception?.InnerException)) return;

            var ex = err.Exception;
            if (ex is AudioDeviceException)
            {
                RaiseError(new AudioDeviceException(err.Message, ex?.InnerException));
            }
            else if (ex is CacheInvalidatedException cacheEx)
            {
                HandleCacheInvalidated(cacheEx);
            }
            else
            {
                RaiseError(new AudioException(err.Message, ex));
            }
        };
    }

    /// <summary>
    /// Извлекает сохранённые параметры воспроизведения из настроек и применяет их к активному конвейеру.
    /// </summary>
    private void InitializeFromSettings()
    {
        var settings = _library.Settings;
        ShuffleEnabled = settings.ShuffleEnabled;
        RepeatMode = settings.RepeatMode;

        // Восстановление уровня громкости: отдаём приоритет LastVolume (int), 
        // при его отсутствии парсим Volume (float), предотвращая сброс в 0 при первом запуске (0.5f)
        int savedVolume = settings.LastVolume;
        if (savedVolume <= 0)
        {
            savedVolume = settings.Volume > 1.0f
                ? (int)settings.Volume
                : (int)Math.Round(settings.Volume * 100.0);
        }

        _volumePercent = savedVolume > 0 ? Math.Clamp(savedVolume, 0, VolumeNormalRange) : 50;
        ApplyGainToPipeline();
    }

    private void ApplyStreamingProfile()
    {
        AudioSourceFactory.ApplyInternetProfile(_library.Settings.InternetProfile);
    }

    #endregion

    #region Event Handlers

    private void HandlePositionChangedInternal(TimeSpan pos)
    {
        Volatile.Write(ref _currentPositionTicks, pos.Ticks);
        RaiseOnUI(_raisePositionChangedOnUIDelegate);
    }

    private void HandleBufferStateChangedInternal(BufferState state)
    {
        lock (_bufferStateLock)
        {
            _currentBufferState = state;
        }
        RaiseOnUI(_raiseBufferStateOnUIDelegate);
    }

    private BufferState GetLatestBufferState()
    {
        lock (_bufferStateLock)
        {
            return _currentBufferState;
        }
    }

    private void HandleSeekCompletedInternal(TimeSpan t)
    {
        Volatile.Write(ref _seekCompletedTicks, t.Ticks);
        RaiseOnUI(_raiseSeekCompletedOnUIDelegate);
    }

    private void HandleDeviceLostInternal()
    {
        RaiseOnUI(_raiseDeviceLostOnUIDelegate);
    }

    private void HandleDeviceRestoredInternal()
    {
        RaiseOnUI(_raiseDeviceRestoredOnUIDelegate);
    }

    #endregion

    #region Session Management

    private int BeginNewSession()
    {
        int session = _session.BeginNew();
        lock (_sessionLock)
        {
            _sessionCts?.Cancel();
            _sessionCts?.Dispose();
            _sessionCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token);
        }
        return session;
    }

    private CancellationToken GetSessionToken()
    {
        lock (_sessionLock) return _sessionCts?.Token ?? _lifetimeCts.Token;
    }

    #endregion

    #region Failure Barrier

    private bool IsSealedFailedTrack(string? trackId)
    {
        var sealed_ = Interlocked.CompareExchange(ref _sealedFailedTrackId, null, null);
        return !string.IsNullOrEmpty(trackId) && !string.IsNullOrEmpty(sealed_)
            && string.Equals(sealed_, trackId, StringComparison.Ordinal);
    }

    private void ResetSealedFailedTrack() => Volatile.Write(ref _sealedFailedTrackId, null);

    private void SealFailedTrack(string? trackId)
    {
        if (!string.IsNullOrEmpty(trackId))
            Volatile.Write(ref _sealedFailedTrackId, trackId);
    }

    private void AbortCurrentTrackPlaybackAfterFatalError(string? trackId)
    {
        if (string.IsNullOrEmpty(trackId)) return;
        SealFailedTrack(trackId);

        if (!string.Equals(CurrentTrack?.Id, trackId, StringComparison.Ordinal)) return;

        lock (_queueLock)
        {
            if (_queue.Count <= 1 && _currentIndex >= 0 && _currentIndex < _queue.Count
                && string.Equals(_queue[_currentIndex].Id, trackId, StringComparison.Ordinal))
                _currentIndex = -1;
        }

        BeginNewSession();
        _player.Stop();
    }

    /// <summary>
    /// Сбрасывает и останавливает воспроизведение при возникновении критической ошибки.
    /// </summary>
    public void StopAfterFatalPlaybackError()
    {
        AbortCurrentTrackPlaybackAfterFatalError(CurrentTrack?.Id);

        CurrentTrack = null;
        StreamInfo = AudioStreamInfo.Empty;

        RaiseOnUI(() =>
        {
            OnTrackChanged?.Invoke(null);
            OnPositionChanged?.Invoke(TimeSpan.Zero);
            OnPlaybackStateChanged?.Invoke(false, false);
            OnLoadingStateChanged?.Invoke(false);
        });
    }

    #endregion

    #region Command Processing

    /// <summary>
    /// Единый цикл обработки typed commands.
    /// </summary>
    private async Task ProcessCommandsAsync()
    {
        try
        {
            await foreach (var cmd in _commandQueue.Reader.ReadAllAsync(_lifetimeCts.Token).ConfigureAwait(false))
            {
                try
                {
                    switch (cmd)
                    {
                        case PlayTrackCommand play:
                            await HandlePlayTrackAsync(play).ConfigureAwait(false);
                            break;

                        case StartQueueCommand start:
                            await HandleStartQueueAsync(start).ConfigureAwait(false);
                            break;

                        case PlayCurrentIndexCommand pci:
                            await PlayCurrentIndexAsync(pci.Session).ConfigureAwait(false);
                            break;

                        case NavigateCommand nav:
                            await HandleNavigateAsync(nav).ConfigureAwait(false);
                            break;

                        case SwitchQualityCommand sq:
                            await HandleSwitchQualityAsync(sq).ConfigureAwait(false);
                            break;
                    }
                }
                catch (OperationCanceledException) { }
                catch (Exception ex) { Log.Warn($"[AudioEngine] Command error: {ex.Message}"); }
            }
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>Отправляет typed command в очередь.</summary>
    private void EnqueueCommand(IEngineCommand command)
    {
        _commandQueue.Writer.TryWrite(command);
    }

    #endregion

    #region Internal Playback

    /// <summary>
    /// Возвращает или запускает single-flight получение continuation URL для трека.
    /// Этот путь используется и background priming'ом, и source-level lazy acquire.
    /// </summary>
    /// <param name="track">Текущий трек.</param>
    private Task<ContinuationUrlResult?> GetOrStartContinuationUrlAcquisitionAsync(TrackInfo track)
    {
        if (_pendingUrlAcquisitions.TryGetValue(track.Id, out var existing))
            return existing;

        var sessionToken = GetSessionToken();
        var created = AcquireContinuationUrlCoreAsync(track, sessionToken);

        if (_pendingUrlAcquisitions.TryAdd(track.Id, created))
        {
            _ = RemovePendingContinuationAcquisitionAsync(track.Id, created);
            return created;
        }

        return _pendingUrlAcquisitions[track.Id];
    }

    /// <summary>
    /// Выполняет реальное получение continuation URL без force-refresh.
    /// </summary>
    private async Task<ContinuationUrlResult?> AcquireContinuationUrlCoreAsync(
      TrackInfo track,
      CancellationToken ct)
    {
        var requested = StreamSelectionHint.FromTrack(track, _library.Settings.RememberTrackFormat);

        var diskEntry = await SessionCacheStore
            .TryGetManifestAndProbeAsync(track.Id, Audio.Http.SharedHttpClient.Instance, ct)
            .ConfigureAwait(false);

        // Cache
        if (diskEntry != null)
        {
            var selectedVariant = SelectBestVariantFromEntry(diskEntry.Variants, requested.Format);
            if (selectedVariant != null)
            {
                return new ContinuationUrlResult(
                    selectedVariant.Url,
                    selectedVariant.Clen,
                    selectedVariant.Bitrate / 1000,
                    selectedVariant.Format,
                    selectedVariant.CodecType,
                    diskEntry.IntegratedLufs);
            }
        }

        var descriptor = await _youtube.RefreshStreamAsync(track, false, ct).ConfigureAwait(false);
        if (descriptor is null || !descriptor.Value.HasLiveUrl)
            return null;

        var d = descriptor.Value;

        // YouTube API path
        return new ContinuationUrlResult(
            d.Url,
            d.ContentLengthBytes,
            d.BitrateKbps,
            d.Format,
            d.Codec,
            d.IntegratedLufs);
    }

    /// <summary>
    /// Удаляет завершённую single-flight задачу continuation acquire,
    /// не затрагивая более новую задачу для того же trackId.
    /// </summary>
    private async Task RemovePendingContinuationAcquisitionAsync(
        string trackId,
        Task<ContinuationUrlResult?> task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch
        {
        }

        _pendingUrlAcquisitions.TryRemove(
            new KeyValuePair<string, Task<ContinuationUrlResult?>>(trackId, task));
    }

    /// <summary>
    /// Сохраняет resolved integrated loudness в runtime-модели, AudioCache и очередь DB persistence.
    /// </summary>
    /// <remarks>
    /// Единственный канал записи LUFS во все хранилища.
    /// Приоритет источника определяется числовым порядком <see cref="LoudnessSource"/> —
    /// отдельная функция ShouldOverwriteLufs не нужна и удалена.
    ///
    /// Обновляет три runtime-слоя:
    /// <list type="number">
    ///   <item>canonical — объект из LibraryService (persistent DB entity)</item>
    ///   <item>registryTrack — L1 in-memory registry (если отличается от canonical)</item>
    ///   <item>currentTrack — активный объект в UI (если отличается от обоих выше)</item>
    /// </list>
    /// Плюс cache entry (все format/bitrate buckets трека) и очередь DB writes.
    /// </remarks>
    private void CommitIntegratedLufs(string trackId, float integratedLufs, LoudnessSource source)
    {
        if (string.IsNullOrEmpty(trackId) || !float.IsFinite(integratedLufs))
            return;

        // Обновляем все три runtime-слоя.
        // SetIntegratedLufs сам проверяет приоритет через enum numeric order —
        // YoutubePerceptual(2) не будет перезаписан EbuMeasured(1).
        var canonical = _library.GetTrack(trackId);
        canonical?.SetIntegratedLufs(integratedLufs, source);

        var registryTrack = _trackRegistry.TryGet(trackId);
        if (registryTrack != null && !ReferenceEquals(registryTrack, canonical))
            registryTrack.SetIntegratedLufs(integratedLufs, source);

        var current = CurrentTrack;
        if (current != null
            && current.Id == trackId
            && !ReferenceEquals(current, canonical)
            && !ReferenceEquals(current, registryTrack))
        {
            current.SetIntegratedLufs(integratedLufs, source);
        }

        // Обновляем ВСЕ cache entries трека (все format/bitrate buckets).
        AudioSourceFactory.GlobalCache?.TryUpdateIntegratedLufs(trackId, integratedLufs, source);

        _pendingNormalizationWrites.Enqueue((trackId, integratedLufs, source));
    }

    /// <summary>
    /// Запускает воспроизведение трека по текущему индексу очереди с опциональной позиции и флагом автозапуска.
    /// </summary>
    private async Task PlayCurrentIndexAsync(int session, TimeSpan? seekPosition = null, bool startPlaying = true)
    {
        TrackInfo? track;
        lock (_queueLock)
        {
            if (_currentIndex < 0 || _currentIndex >= _queue.Count) return;
            track = _queue[_currentIndex];
        }

        if (track == null || IsSealedFailedTrack(track.Id)) return;

        var previousTask = Volatile.Read(ref _activePlayTask);
        if (previousTask is { IsCompleted: false })
        {
            try { await previousTask.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            catch (Exception) { }
        }

        if (_session.IsStale(session)) return;

        var playTask = PlayTrackCoreAsync(track, session, GetSessionToken(), seekPosition, startPlaying);
        Volatile.Write(ref _activePlayTask, playTask);

        try
        {
            await playTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (Exception) { }
    }

    /// <summary>
    /// Основной метод подготовки и запуска воспроизведения трека с поддержкой SeekPosition и ленивой загрузки.
    /// </summary>
    private async Task PlayTrackCoreAsync(TrackInfo track, int session, CancellationToken ct, TimeSpan? seekPosition = null, bool startPlaying = true)
    {
        if (_session.IsStale(session) || IsSealedFailedTrack(track.Id)) return;

        Log.Debug($"[AudioEngine] [PlayTrackCore] Initiating playback for track: {track.Id} | Session: {session} | StartPlaying: {startPlaying}");

        _player.Stop();
        if (_session.IsStale(session) || IsSealedFailedTrack(track.Id)) return;

        SetManualLoading(true);

        try
        {
            var canonical = await _library.GetTrackAsync(track.Id, ct).ConfigureAwait(false);
            if (canonical != null)
            {
                canonical.UpdateMetadata(track);
                track = canonical;
            }
            else
            {
                track = _trackRegistry.RegisterOrUpdate(track);
            }

            CurrentTrack = track;
            StreamInfo = AudioStreamInfo.Empty;

            RaiseOnUI(() =>
            {
                OnTrackChanged?.Invoke(track);
                OnPositionChanged?.Invoke(TimeSpan.Zero);
            });

            // Ленивый выход: прерываем подготовку потока при требовании тихой загрузки трека (SkipAndPause)
            if (!startPlaying)
            {
                SetManualLoading(false);
                return;
            }

            ct.ThrowIfCancellationRequested();
            Volatile.Write(ref _nTokenActiveTrackId, track.Id);
            Volatile.Write(ref _nTokenWarnedTrackId, null);

            AudioSourceFactory.PreWarmCdnConnections(
                Audio.Http.SharedHttpClient.Instance, _lifetimeCts.Token);

            const int maxStartupAttempts = 3;

            for (int attempt = 1; attempt <= maxStartupAttempts; attempt++)
            {
                try
                {
                    ct.ThrowIfCancellationRequested();

                    var descriptor = await Task.Run(
                        () => ResolveStreamAsync(track, ct, seekPosition), ct).ConfigureAwait(false);

                    if (descriptor.HasPerceptualLufs)
                    {
                        track.SetIntegratedLufs(
                            descriptor.IntegratedLufs,
                            LoudnessSource.YoutubePerceptual);

                        CommitIntegratedLufs(
                            track.Id,
                            descriptor.IntegratedLufs,
                            LoudnessSource.YoutubePerceptual);
                    }

                    if (_session.IsStaleOrCancelled(session, ct) || IsSealedFailedTrack(track.Id)) return;

                    Log.Info($"[AudioEngine] PlayTrackCore resolved -> {descriptor}");

                    await _player.PlayAsync(descriptor, ct, seekPosition: seekPosition).ConfigureAwait(false);

                    if (descriptor.HasPerceptualLufs)
                    {
                        AudioSourceFactory.GlobalCache?.TryUpdateIntegratedLufs(
                            track.Id,
                            descriptor.IntegratedLufs,
                            LoudnessSource.YoutubePerceptual);
                    }

                    PreWarmNextTracksInQueue(CurrentQueueIndex, Audio.Http.SharedHttpClient.Instance, _lifetimeCts.Token);
                    break;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (OperationCanceledException ex) when (attempt < maxStartupAttempts)
                {
                    Log.Warn($"[AudioEngine] Transient cancellation during track startup (attempt {attempt}/{maxStartupAttempts}): {ex.Message}");
                    _player.Stop();
                    await Task.Delay(150, ct).ConfigureAwait(false);
                }
            }

            ApplyGainToPipeline();
            ApplyLifecycleSourceSuspendPolicy();
        }
        catch (OperationCanceledException ex)
        {
            if (!ct.IsCancellationRequested)
            {
                Log.Warn($"[AudioEngine] Playback startup aborted: {ex.Message}");
                _player.Stop();
                RaiseError(ex);
            }
        }
        catch (Exception) when (_session.IsStaleOrCancelled(session, ct)) { }
        catch (Exception ex)
        {
            AbortCurrentTrackPlaybackAfterFatalError(track.Id);
            RaiseError(ex);
        }
        finally
        {
            SetManualLoading(false);
            Interlocked.CompareExchange(ref _nTokenActiveTrackId, null, track.Id);
        }
    }

    /// <summary>
    /// Callback для обновления URL при 403 Forbidden.
    /// </summary>
    private async ValueTask<string?> RefreshUrlCallbackAsync(string trackId, CancellationToken ct)
    {
        if (IsSealedFailedTrack(trackId)) return null;

        var track = (CurrentTrack?.Id == trackId ? CurrentTrack : null)
            ?? _trackRegistry.TryGet(trackId)
            ?? await _library.GetTrackAsync(trackId, ct).ConfigureAwait(false);

        if (track == null || IsSealedFailedTrack(trackId)) return null;

        var sessionToken = GetSessionToken();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, sessionToken);

        try
        {
            // Точечная инвалидация кэшей именно для этого трека
            Audio.Http.SessionCacheStore.Invalidate(trackId);
            _youtube.InvalidateMemoryCache(trackId);

            Log.Info($"[AudioEngine] 403 refresh: per-track caches invalidated for {trackId}");

            // Soft refresh (force=false)
            var descriptor = await Task.Run(
                () => _youtube.RefreshStreamAsync(track, false, linked.Token),
                linked.Token).ConfigureAwait(false);

            if (descriptor is { HasLiveUrl: true })
            {
                Log.Info($"[AudioEngine] Soft refresh returned new URL for {trackId}");
                return descriptor.Value.Url;
            }

            // Force refresh — последний шанс
            Log.Warn($"[AudioEngine] Soft refresh failed for {trackId}, " +
                    $"falling back to force refresh");

            descriptor = await Task.Run(
                () => _youtube.RefreshStreamAsync(track, true, linked.Token),
                linked.Token).ConfigureAwait(false);

            return descriptor is { HasLiveUrl: true } ? descriptor.Value.Url : null;
        }
        catch (Exception) when (linked.IsCancellationRequested
            || sessionToken.IsCancellationRequested
            || !string.Equals(CurrentTrack?.Id, trackId, StringComparison.Ordinal))
        {
            return null;
        }
        catch (Exception ex)
        {
            AbortCurrentTrackPlaybackAfterFatalError(trackId);
            RaiseError(ex);
            return null;
        }
    }

    /// <summary>
    /// Callback для первичного continuation acquire у partial-cache source.
    /// Не делает force-refresh и разделяет single-flight задачу с background priming.
    /// </summary>
    private async ValueTask<string?> AcquireUrlCallbackAsync(string trackId, CancellationToken ct)
    {
        if (IsSealedFailedTrack(trackId))
            return null;

        // Каскадный поиск трека: Активный -> Реестр L1 -> База данных
        var track = (CurrentTrack?.Id == trackId ? CurrentTrack : null)
            ?? _trackRegistry.TryGet(trackId)
            ?? await _library.GetTrackAsync(trackId, ct).ConfigureAwait(false);

        if (track == null || IsSealedFailedTrack(trackId))
            return null;

        try
        {
            var task = GetOrStartContinuationUrlAcquisitionAsync(track);
            var result = await task.WaitAsync(ct).ConfigureAwait(false);
            return result?.Url;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception) when (!string.Equals(CurrentTrack?.Id, trackId, StringComparison.Ordinal))
        {
            return null;
        }
        catch (Exception ex)
        {
            Log.Debug($"[AudioEngine] Acquire URL skipped for {trackId}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Вычисляет минимальный объём contiguous local prefix, достаточный
    /// для Partial Cache Fast Start.
    /// </summary>
    /// <param name="bitrateKbps">Битрейт потока в kbps.</param>
    private static int ComputePartialCacheBootstrapBytes(int bitrateKbps)
    {
        double bitrateBytesPerSec = Math.Max(1, bitrateKbps) * 1000.0 / 8.0;
        int bytes = (int)Math.Ceiling(bitrateBytesPerSec * PartialCacheBootstrapTargetMs / 1000.0);
        return Math.Clamp(bytes, PartialCacheBootstrapMinBytes, PartialCacheBootstrapMaxBytes);
    }

    /// <summary>
    /// Пытается подобрать лучший partial cache для fast-start с позиции начала трека.
    /// </summary>
    /// <param name="track">Трек.</param>
    /// <param name="seekPosition">
    /// Позиция seek-before-play. Если указана и не равна нулю, partial fast-start отключается,
    /// чтобы не стартовать с неподходящего локального префикса.
    /// </param>
    private AudioCacheEntry? TryGetPartialBootstrapCache(TrackInfo track, TimeSpan? seekPosition)
    {
        if (seekPosition is { TotalMilliseconds: > 0 })
            return null;

        var cacheManager = AudioSourceFactory.GlobalCache;
        if (cacheManager == null)
            return null;

        int bitrateHint = StreamSelectionHint.FromTrack(track, _library.Settings.RememberTrackFormat).BitrateKbps;
        if (bitrateHint <= 0)
            bitrateHint = 160;

        int requiredBytes = ComputePartialCacheBootstrapBytes(bitrateHint);
        return cacheManager.FindBestStartupCache(track.Id, requiredBytes);
    }

    /// <summary>
    /// Пытается прикрепить уже подготовленный continuation URL к активному source.
    /// </summary>
    /// <param name="track">Текущий трек.</param>
    /// <param name="url">Финальный stream URL.</param>
    /// <returns>
    /// <c>true</c>, если URL успешно передан в активный source;
    /// <c>false</c>, если пайплайн/source ещё не готовы или имеют неподходящий тип.
    /// </returns>
    private bool TryAttachPrimedContinuationUrlToActiveSource(TrackInfo track, string url)
    {
        if (_disposed || string.IsNullOrWhiteSpace(url))
            return false;

        var current = CurrentTrack;
        if (current == null || !string.Equals(current.Id, track.Id, StringComparison.Ordinal))
            return false;

        var pipeline = _player.GetActivePipeline();
        if (pipeline?.Source is not Audio.Sources.CachingStreamSource cachingSource)
            return false;

        if (cachingSource.TryAttachContinuationUrl(url))
        {
            Log.Debug($"[AudioEngine] Primed continuation URL attached to live source: {track.Id}");
            return true;
        }

        return false;
    }

    /// <summary>
    /// Подготавливает continuation URL для partial-cache source в фоне.
    /// Использует тот же single-flight acquire task, что и source-level lazy acquire.
    /// </summary>
    private async Task PrimeContinuationUrlAsync(
        TrackInfo track,
        AudioCacheEntry expectedEntry,
        CancellationToken ct)
    {
        try
        {
            if (ct.IsCancellationRequested || IsSealedFailedTrack(track.Id))
                return;

            if (TryGetCompatibleContinuationUrl(track, expectedEntry, out var existingUrl))
            {
                TryAttachPrimedContinuationUrlToActiveSource(track, existingUrl);
                return;
            }

            var result = await GetOrStartContinuationUrlAcquisitionAsync(track)
                .WaitAsync(ct)
                .ConfigureAwait(false);

            if (result == null || string.IsNullOrEmpty(result.Value.Url))
                return;

            if (!IsContinuationVariantCompatible(expectedEntry, result.Value.Format, result.Value.Bitrate))
            {
                Log.Warn($"[AudioEngine] Continuation priming variant mismatch for {track.Id}: " +
                         $"expected={expectedEntry.Format}/{expectedEntry.Bitrate}kbps, " +
                         $"actual={result.Value.Format}/{result.Value.Bitrate}kbps");
                return;
            }

            TryAttachPrimedContinuationUrlToActiveSource(track, result.Value.Url);

            if (float.IsFinite(result.Value.IntegratedLufs))
            {
                CommitIntegratedLufs(
                    track.Id,
                    result.Value.IntegratedLufs,
                    LoudnessSource.YoutubePerceptual);

                UpdateRunningPipelineGain(track.Id, result.Value.IntegratedLufs);
            }

            Log.Info($"[AudioEngine] Partial-cache continuation primed: {track.Id} " +
                     $"({result.Value.Codec.ToDisplayName()}/{result.Value.Bitrate}kbps)");
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Log.Debug($"[AudioEngine] Continuation priming skipped for {track.Id}: {ex.Message}");
        }
    }

    /// <summary>
    /// Разрешает источник аудиопотока для воспроизведения трека.
    /// </summary>
    /// <remarks>
    /// <para><b>Стратегия разрешения (порядок приоритета):</b></para>
    /// <list type="number">
    ///   <item>Скачанный локальный файл (папка Downloads) — мгновенный старт, формат определяется из физического файла.</item>
    ///   <item>Exact full disk cache — точное совпадение по format+bitrate bucket через <see cref="AudioSourceFactory.BuildCacheKey"/>.</item>
    ///   <item>Any full disk cache — fallback при отсутствии явного предпочтения формата.</item>
    ///   <item>Partial cache fast-start — локальный contiguous-префикс с ленивым continuation URL.</item>
    ///   <item>Session cache (disk manifest) — HEAD-probe к CDN, восстановление манифеста без YouTube API.</item>
    ///   <item>Provider memory cache (RAM manifest) — instant-access из текущей сессии.</item>
    ///   <item>YouTube API call — cold path, полный запрос манифеста.</item>
    /// </list>
    /// </remarks>
    /// <param name="track">Метаданные трека.</param>
    /// <param name="ct">Токен отмены асинхронной операции.</param>
    /// <param name="seekPosition">Начальная позиция воспроизведения при перемотке.</param>
    /// <returns>Дескриптор готового аудиопотока.</returns>
    /// <exception cref="InvalidOperationException">Выбрасывается при невозможности получить поток из всех источников.</exception>
    private async Task<ResolvedStreamDescriptor> ResolveStreamAsync(
        TrackInfo track,
        CancellationToken ct,
        TimeSpan? seekPosition = null)
    {
        var requested = StreamSelectionHint.FromTrack(track, _library.Settings.RememberTrackFormat);

        Log.Debug($"[AudioEngine] ResolveStreamAsync start: track={track.Id}, seek={seekPosition?.TotalMilliseconds ?? 0}ms, requestedFormat={requested.Format?.ToContainerName() ?? "-"}, requestedBitrate={requested.BitrateKbps}");

        var rawId = track.GetRawIdSpan().ToString();

        // --- Path 0: Downloaded local files (Downloads folder) ---
        // Формат определяется из физического файла на диске, а не из PreferredFormat,
        // который мутируется при SwitchQuality и больше не отражает формат скачанного файла.
        // Битрейт вычисляется из размера файла и длительности трека.
        // Проверяется только контейнер: скачанный файл один на контейнер,
        // битрейт не является дискриминантом для Downloads.
        if (track.IsDownloaded && !string.IsNullOrEmpty(track.LocalPath) && File.Exists(track.LocalPath))
        {
            var downloadedFormat = AudioSourceFactory.DetectFormat(track.LocalPath);
            if (downloadedFormat == AudioFormat.Unknown) downloadedFormat = AudioFormat.WebM;

            bool isUserOverrodeFormat = requested.HasFormat && requested.Format != downloadedFormat;

            if (!isUserOverrodeFormat)
            {
                var fileInfo = new FileInfo(track.LocalPath);
                var codec = AudioSourceFactory.GetCodecForFormat(downloadedFormat);
                int fileBitrateKbps = track.Duration.TotalSeconds > 0
                    ? Math.Max((int)(fileInfo.Length * 8 / track.Duration.TotalSeconds / 1000), 32)
                    : 128;

                var descriptor = new ResolvedStreamDescriptor
                {
                    TrackId = track.Id,
                    Url = track.LocalPath,
                    Format = downloadedFormat,
                    Codec = codec,
                    BitrateKbps = fileBitrateKbps,
                    ContentLengthBytes = fileInfo.Length,
                    Origin = StreamSource.DiskCacheFull
                };

                Log.Info($"[AudioEngine] ResolveStreamAsync LOCAL DOWNLOAD -> {descriptor}");
                TryEnrichIntegratedLufsFromLocalSources(track);
                return descriptor;
            }
        }

        // --- Path 1: Full disk cache (exact match by format+bitrate bucket) ---
        // При явном предпочтении формата и битрейта — точный lookup через BuildCacheKey.
        // Единый источник истины с AudioSourceFactory: тот же ключ, тот же bucket.
        if (requested.HasFormat && requested.HasBitrate)
        {
            string exactCacheKey = AudioSourceFactory.BuildCacheKey(track.Id, requested.Format!.Value, requested.BitrateKbps);
            if (AudioSourceFactory.GlobalCache is { } exactCache && exactCache.IsFullyCached(exactCacheKey))
            {
                var exactEntry = exactCache.GetCacheInfo(exactCacheKey);
                if (exactEntry != null)
                {
                    TrackNormalizationHydrator.HydrateNormalization(track, exactEntry);
                    TryEnrichIntegratedLufsFromLocalSources(track);

                    var descriptor = new ResolvedStreamDescriptor
                    {
                        TrackId = track.Id,
                        Url = "",
                        Format = exactEntry.Format,
                        Codec = exactEntry.Codec,
                        BitrateKbps = exactEntry.Bitrate,
                        ContentLengthBytes = exactEntry.TotalSize,
                        Origin = StreamSource.DiskCacheFull
                    };

                    Log.Info($"[AudioEngine] ResolveStreamAsync FULL CACHE (exact) -> {descriptor}");
                    return descriptor;
                }
            }
        }

        // --- Path 1b: Full disk cache (any format, no user preference) ---
        // FindAnyCachedTrack возвращает запись с максимальным битрейтом.
        // Используется только при первичном воспроизведении без явного предпочтения.
        if (!requested.HasFormat)
        {
            var fullCache = AudioSourceFactory.FindAnyCachedTrack(track.Id)
                         ?? (rawId != track.Id ? AudioSourceFactory.FindAnyCachedTrack(rawId) : null);

            if (fullCache != null)
            {
                var entry = fullCache.Value.Entry;
                TrackNormalizationHydrator.HydrateNormalization(track, entry);
                TryEnrichIntegratedLufsFromLocalSources(track);

                var descriptor = new ResolvedStreamDescriptor
                {
                    TrackId = track.Id,
                    Url = "",
                    Format = entry.Format,
                    Codec = entry.Codec,
                    BitrateKbps = entry.Bitrate,
                    ContentLengthBytes = entry.TotalSize,
                    Origin = StreamSource.DiskCacheFull
                };

                Log.Info($"[AudioEngine] ResolveStreamAsync FULL CACHE -> {descriptor}");
                return descriptor;
            }
        }

        // --- Path 2: Partial cache fast-start ---
        var bootstrapCache = TryGetPartialBootstrapCache(track, seekPosition);
        if (bootstrapCache != null)
        {
            TrackNormalizationHydrator.HydrateNormalization(track, bootstrapCache);
            TryEnrichIntegratedLufsFromLocalSources(track);

            if (TryGetCompatibleContinuationUrl(track, bootstrapCache, out var eagerUrl))
            {
                Log.Info($"[AudioEngine] Partial-cache fast start with eager continuation URL: {track.Id}");

                var descriptor = new ResolvedStreamDescriptor
                {
                    TrackId = track.Id,
                    Url = eagerUrl,
                    Format = bootstrapCache.Format,
                    Codec = bootstrapCache.Codec,
                    BitrateKbps = bootstrapCache.Bitrate,
                    ContentLengthBytes = bootstrapCache.TotalSize,
                    Origin = StreamSource.DiskCachePartial
                };

                Log.Info($"[AudioEngine] ResolveStreamAsync PARTIAL CACHE (eager URL) -> {descriptor}");
                return descriptor;
            }

            _ = PrimeContinuationUrlAsync(track, bootstrapCache, ct);

            Log.Info($"[AudioEngine] Partial-cache fast start: {track.Id}");

            var lazyDescriptor = new ResolvedStreamDescriptor
            {
                TrackId = track.Id,
                Url = "",
                Format = bootstrapCache.Format,
                Codec = bootstrapCache.Codec,
                BitrateKbps = bootstrapCache.Bitrate,
                ContentLengthBytes = bootstrapCache.TotalSize,
                Origin = StreamSource.DiskCachePartial
            };

            Log.Info($"[AudioEngine] ResolveStreamAsync PARTIAL CACHE (lazy URL) -> {lazyDescriptor}");
            return lazyDescriptor;
        }

        ct.ThrowIfCancellationRequested();

        // --- Path 3: Session cache (disk manifest with HEAD probe) ---
        var diskEntry = await SessionCacheStore
            .TryGetManifestAndProbeAsync(track.Id, Audio.Http.SharedHttpClient.Instance, ct)
            .ConfigureAwait(false);

        if (diskEntry is { Variants.Count: > 0 })
        {
            var cacheEntry = FindNormalizationCacheEntry(track.Id);
            if (cacheEntry != null)
                TrackNormalizationHydrator.HydrateNormalization(track, cacheEntry);

            var selectedVariant = SelectBestVariantFromEntry(diskEntry.Variants, requested.Format);
            if (selectedVariant != null)
            {
                var format = selectedVariant.Format;
                var codec = selectedVariant.CodecType != AudioCodec.Unknown
                    ? selectedVariant.CodecType
                    : AudioSourceFactory.GetCodecForFormat(format);

                var descriptor = new ResolvedStreamDescriptor
                {
                    TrackId = track.Id,
                    Itag = selectedVariant.Itag,
                    Format = format,
                    Codec = codec,
                    BitrateKbps = selectedVariant.Bitrate / 1000,
                    ContentLengthBytes = selectedVariant.Clen,
                    Url = selectedVariant.Url,
                    ExpireUtc = diskEntry.ExpireUtc,
                    CdnHost = diskEntry.CdnHost,
                    IntegratedLufs = diskEntry.IntegratedLufs,
                    LanguageCode = selectedVariant.LanguageCode,
                    IsDefaultLanguage = selectedVariant.IsDefaultLanguage,
                    Origin = StreamSource.SessionCache
                };

                Log.Info($"[AudioEngine] Session cache hit: {track.Id} -> {descriptor}");
                return descriptor;
            }
        }

        // --- Path 4: Provider memory cache (RAM manifest) ---
        var memDescriptor = _youtube.TryGetCachedStreamDescriptor(
            track.Id,
            requested.Format,
            requested.BitrateKbps);

        if (memDescriptor != null)
        {
            if (memDescriptor.Value.ExpireUtc == default || DateTime.UtcNow.AddMinutes(5) < memDescriptor.Value.ExpireUtc)
            {
                var cacheEntry = FindNormalizationCacheEntry(track.Id);
                if (cacheEntry != null)
                    TrackNormalizationHydrator.HydrateNormalization(track, cacheEntry);

                var descriptor = memDescriptor.Value with { TrackId = track.Id };
                Log.Info($"[AudioEngine] Provider memory cache hit: {track.Id} -> {descriptor}");
                return descriptor;
            }
            else
            {
                Log.Debug($"[AudioEngine] Provider memory cache hit ignored due to ExpireUtc passed: {track.Id}");
            }
        }

        // --- Path 5: YouTube API call (cold path) ---
        var freshDescriptor = await _youtube.RefreshStreamAsync(track, false, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Failed to resolve stream URL for {track.Id}");

        Log.Info($"[AudioEngine] ResolveStreamAsync YOUTUBE API -> {freshDescriptor}");
        return freshDescriptor;
    }

    /// <summary>
    /// Пытается обогатить трек track-level integrated LUFS из локальных источников без сети.
    /// Использует только <see cref="TrackManifestEntry.IntegratedLufs"/> из SessionCache.
    /// </summary>
    /// <param name="track">Текущий трек.</param>
    private static void TryEnrichIntegratedLufsFromLocalSources(TrackInfo track)
    {
        if (track.HasIntegratedLufs)
            return;

        var manifest = SessionCacheStore.GetManifest(track.Id);
        if (manifest is null)
            return;

        if (!float.IsFinite(manifest.IntegratedLufs))
            return;

        track.SetIntegratedLufs(
            manifest.IntegratedLufs,
            LoudnessSource.YoutubePerceptual);

        AudioSourceFactory.GlobalCache?.TryUpdateIntegratedLufs(
            track.Id,
            manifest.IntegratedLufs,
            LoudnessSource.YoutubePerceptual);

        Log.Debug($"[AudioEngine] Enriched LUFS from SessionCache: " +
                  $"{track.Id} → {manifest.IntegratedLufs:F2} LUFS");
    }

    #endregion

    #region Event Handlers

    private void HandlePlayerStateChanged(PlaybackState state)
    {
        ApplyLifecycleSourceSuspendPolicy();

        RaiseOnUI(() =>
        {
            this.RaisePropertyChanged(nameof(IsPlaying));
            this.RaisePropertyChanged(nameof(IsPaused));
            this.RaisePropertyChanged(nameof(IsLoading));
            this.RaisePropertyChanged(nameof(TotalDuration));
            OnPlaybackStateChanged?.Invoke(state == PlaybackState.Playing, state == PlaybackState.Paused);
            OnLoadingStateChanged?.Invoke(IsLoading);
        });
    }

    /// <summary>
    /// Обработчик естественного завершения трека.
    /// Маршрутизируется через typed command для соблюдения actor invariant.
    /// </summary>
    private void HandlePlayerTrackEnded()
    {
        if (_player.State is PlaybackState.Loading or PlaybackState.Buffering) return;
        EnqueueCommand(new NavigateCommand(Forward: true, UserInitiated: false));
    }

    private void HandleStreamInfoChanged(AudioStreamInfo info)
    {
        RaiseOnUI(() => { StreamInfo = info; OnStreamInfoChanged?.Invoke(info); });
    }

    #endregion

    #region Playback Control

    public Task PlayTrackAsync(TrackInfo track)
    {
        if (track == null) return Task.CompletedTask;
        ResetSealedFailedTrack();
        int session = BeginNewSession();
        EnqueueCommand(new PlayTrackCommand(track, session, null)); // Обычный запуск с начала
        return Task.CompletedTask;
    }

    public Task StartQueueAsync(IEnumerable<TrackInfo> tracks, TrackInfo startTrack)
    {
        ResetSealedFailedTrack();
        int session = BeginNewSession();
        EnqueueCommand(new StartQueueCommand(tracks, startTrack, session));
        return Task.CompletedTask;
    }

    public async Task SetPlaybackStateAsync(bool shouldPlay)
    {
        if (shouldPlay)
        {
            if (_player.State == PlaybackState.Paused) _player.Resume();
            else if (_player.State is PlaybackState.Stopped or PlaybackState.Error && CurrentTrack != null)
            {
                ResetSealedFailedTrack();
                int session = BeginNewSession();
                EnqueueCommand(new PlayCurrentIndexCommand(session));
            }
        }
        else _player.Pause();
    }

    /// <summary>
    /// Останавливает воспроизведение и очищает стейт трека.
    /// </summary>
    public void Stop()
    {
        BeginNewSession();
        _player.Stop();

        CurrentTrack = null;
        StreamInfo = AudioStreamInfo.Empty;

        RaiseOnUI(() =>
        {
            OnTrackChanged?.Invoke(null);
        });
    }

    public Task PlayNextAsync(bool startPlaying = true) { ResetSealedFailedTrack(); EnqueueCommand(new NavigateCommand(true, true, startPlaying)); return Task.CompletedTask; }
    public Task PlayPreviousAsync(bool startPlaying = true) { ResetSealedFailedTrack(); EnqueueCommand(new NavigateCommand(false, true, startPlaying)); return Task.CompletedTask; }

    #endregion

    #region Command Handlers

    private async Task HandlePlayTrackAsync(PlayTrackCommand cmd)
    {
        if (_session.IsStale(cmd.Session)) return;

        // Сбрасываем лимит авто-попыток только при обычном (неавтоматическом) запуске трека
        if (!cmd.IsRetry)
        {
            Volatile.Write(ref _cacheRetryCount, 0);
        }

        lock (_queueLock)
        {
            int idx = _queue.FindIndex(t => t.Id == cmd.Track.Id);
            if (idx >= 0) { _currentIndex = idx; _queue[idx] = cmd.Track; }
            else { _queue.Clear(); _queue.Add(cmd.Track); _currentIndex = 0; }
            InvalidateQueueSnapshot();
        }

        RaiseOnUI(() => OnQueueChanged?.Invoke());
        // Передаем SeekPosition дальше в проигрыватель
        await PlayCurrentIndexAsync(cmd.Session, cmd.SeekPosition).ConfigureAwait(false);
    }

    private async Task HandleStartQueueAsync(StartQueueCommand cmd)
    {
        if (_session.IsStale(cmd.Session)) return;

        Volatile.Write(ref _cacheRetryCount, 0);

        lock (_queueLock)
        {
            _queue.Clear();
            _queue.AddRange(cmd.Tracks);
            _currentIndex = _queue.FindIndex(t => t.Id == cmd.StartTrack.Id);
            if (_currentIndex == -1 && _queue.Count > 0) _currentIndex = 0;
            if (ShuffleEnabled && _queue.Count > 1) ApplyShuffleInPlace(preserveCurrentAtStart: true);
            InvalidateQueueSnapshot();
        }

        RaiseOnUI(() => OnQueueChanged?.Invoke());
        await PlayCurrentIndexAsync(cmd.Session).ConfigureAwait(false);
    }

    private async Task HandleNavigateAsync(NavigateCommand cmd)
    {
        int session = BeginNewSession();
        bool canMove;
        bool queueMutated;

        lock (_queueLock)
        {
            canMove = cmd.Forward ? TryMoveNext(cmd.UserInitiated) : TryMovePrevious();
            queueMutated = _queueMutatedByNavigation;
        }

        if (queueMutated) RaiseOnUI(() => OnQueueChanged?.Invoke());

        if (canMove)
            await PlayCurrentIndexAsync(session, startPlaying: cmd.StartPlaying).ConfigureAwait(false);
        else if (!cmd.Forward && _player.State != PlaybackState.Stopped)
            await _player.SeekAsync(TimeSpan.Zero).ConfigureAwait(false);
        else
            Stop();
    }

    #endregion

    #region Quality Switching

    public Task SwitchQualityAsync(AudioFormat format, int bitrate)
    {
        if (CurrentTrack == null) return Task.CompletedTask;

        ResetSealedFailedTrack();
        int session = BeginNewSession(); // Отменяет предыдущие сессии

        var track = CurrentTrack;
        track.TransientFormat = format;
        track.TransientBitrate = bitrate;

        if (_library.Settings.RememberTrackFormat)
        {
            track.PreferredFormat = format;
            track.PreferredBitrate = bitrate;
        }

        // Ставим в строгую очередь команд, чтобы исключить рассинхрон UI и Аудио
        EnqueueCommand(new SwitchQualityCommand(track, CurrentPosition, format, bitrate, session));
        return Task.CompletedTask;
    }

    /// <summary>
    /// Обработчик команды смены качества. Выполняется строго последовательно в actor-цикле.
    /// </summary>
    private async Task HandleSwitchQualityAsync(SwitchQualityCommand cmd)
    {
        var ct = GetSessionToken();
        if (_session.IsStale(cmd.Session)) return;

        try
        {
            var elapsed = (DateTime.UtcNow - _lastQualitySwitchTime).TotalMilliseconds;
            if (elapsed < QualitySwitchCooldownMs)
                await Task.Delay(QualitySwitchCooldownMs - (int)elapsed, ct).ConfigureAwait(false);

            _lastQualitySwitchTime = DateTime.UtcNow;

            Log.Info($"[AudioEngine] SwitchQuality start: track={cmd.Track.Id}, requestedFormat={cmd.Track.TransientFormat?.ToContainerName() ?? "-"}, requestedBitrate={cmd.Track.TransientBitrate}, resumePos={cmd.Position.TotalMilliseconds}ms");

            Volatile.Write(ref _nTokenActiveTrackId, cmd.Track.Id);
            ct.ThrowIfCancellationRequested();

            var descriptor = await Task.Run(async () =>
                await _youtube.RefreshStreamAsync(cmd.Track, false, ct).ConfigureAwait(false)
                ?? await _youtube.RefreshStreamAsync(cmd.Track, true, ct).ConfigureAwait(false),
                ct).ConfigureAwait(false);

            if (descriptor == null)
            {
                if (!_session.IsStale(cmd.Session))
                    RaiseError(new InvalidOperationException("No stream available"));
                return;
            }

            if (_session.IsStaleOrCancelled(cmd.Session, ct) || IsSealedFailedTrack(cmd.Track.Id)) return;

            var d = descriptor.Value;

            Log.Info($"[AudioEngine] SwitchQuality resolved -> {d}");

            var currentInfo = _player.StreamInfo;
            if (currentInfo.IsValid
                && string.Equals(currentInfo.TrackId, d.TrackId, StringComparison.Ordinal)
                && currentInfo.Format == d.Format
                && currentInfo.CodecType == d.Codec
                && currentInfo.Bitrate == d.BitrateKbps)
            {
                Log.Info($"[AudioEngine] SwitchQuality skipped: active pipeline already matches");
                return;
            }

            if (d.HasPerceptualLufs)
            {
                cmd.Track.SetIntegratedLufs(d.IntegratedLufs, LoudnessSource.YoutubePerceptual);
                CommitIntegratedLufs(cmd.Track.Id, d.IntegratedLufs, LoudnessSource.YoutubePerceptual);
            }

            // Дожидаемся предыдущей задачи (если она каким-то чудом осталась), 
            // чтобы не сломать инвариант плеера.
            var previousTask = Volatile.Read(ref _activePlayTask);
            if (previousTask is { IsCompleted: false })
            {
                try { await previousTask.ConfigureAwait(false); } catch { }
            }

            if (_session.IsStaleOrCancelled(cmd.Session, ct)) return;

            var playTask = _player.PlayAsync(
                d,
                ct,
                seekPosition: cmd.Position.TotalSeconds > 1 ? cmd.Position : null);

            Volatile.Write(ref _activePlayTask, playTask);
            await playTask.ConfigureAwait(false);

            if (d.HasPerceptualLufs)
            {
                AudioSourceFactory.GlobalCache?.TryUpdateIntegratedLufs(
                    cmd.Track.Id, d.IntegratedLufs, LoudnessSource.YoutubePerceptual);
            }

            ApplyGainToPipeline();
        }
        catch (Exception ex)
        {
            if (!_session.IsStaleOrCancelled(cmd.Session, ct) && !CancellationHelper.IsCancellationLike(ex))
            {
                AbortCurrentTrackPlaybackAfterFatalError(cmd.Track.Id);
                RaiseError(ex);
            }
        }
        finally
        {
            Interlocked.CompareExchange(ref _nTokenActiveTrackId, null, cmd.Track.Id);
        }
    }

    #endregion

    #region Seek

    /// <summary>
    /// Выполняет seek немедленно.
    /// </summary>
    /// <remarks>
    /// <para>Debounce-логика удалена: после переноса preview seek из UI
    /// в чисто визуальный режим реальный seek происходит только по финальному
    /// действию пользователя (release/click). Дополнительный debounce в движке
    /// больше не нужен и только создаёт лишнее состояние и гонки.</para>
    /// </remarks>
    public ValueTask SeekAsync(TimeSpan position)
    {
        return _player.SeekAsync(position);
    }

    #endregion

    #region Error Handling

    /// <summary>
    /// Проактивный триггер rebuild: PreWarmer обнаружил N последовательных таймаутов CDN.
    /// Срабатывает до starvation — до того как буфер декодера исчерпается.
    /// </summary>
    private void HandleCdnTunnelDead()
    {
        Log.Warn("[AudioEngine] CdnPreWarmer: tunnel dead detected — triggering proactive rebuild");
        NotifyNetworkStarvation();
    }

    /// <summary>
    /// Обрабатывает инвалидацию кэша: при сбое чтения выполняет 
    /// бесшовное переключение на стриминг, который хирургически пропатчит повреждённый чанк на диске.
    /// </summary>
    private void HandleCacheInvalidated(CacheInvalidatedException cacheEx)
    {
        var trackId = cacheEx.TrackId ?? CurrentTrack?.Id;

        if (cacheEx.IsRecoverable && _cacheRetryCount < MaxCacheAutoRetries)
        {
            int retryNumber = Interlocked.Increment(ref _cacheRetryCount);
            var resumePosition = CurrentPosition;
            var track = CurrentTrack;

            Log.Info($"[AudioEngine] Cache auto-retry #{retryNumber}/{MaxCacheAutoRetries}: track={trackId}, kind={cacheEx.Kind}, pos={resumePosition}");

            // Полное физическое стирание файла с диска выполняем ТОЛЬКО если файла действительно нет на месте (FileDeleted)
            if (cacheEx.Kind is CacheInvalidationKind.FileDeleted)
            {
                if (!string.IsNullOrEmpty(trackId))
                {
                    try
                    {
                        AudioSourceFactory.GlobalCache?.RemoveTrackCache(trackId);
                        Log.Info($"[AudioEngine] Removed missing cache registry for retry: {trackId}");
                    }
                    catch (Exception removeEx)
                    {
                        Log.Warn($"[AudioEngine] Failed to remove cache: {removeEx.Message}");
                    }
                }
            }
            else if (cacheEx.Kind is CacheInvalidationKind.ParserResync or CacheInvalidationKind.ShortRead)
            {
                // При повреждении файла мы сохраняем его на диске!
                // Метод LocalFileSource уже пометил повреждённый чанк неактивным.
                // Пересоздание конвейера создаст CachingStreamSource, который скачает из сети
                // исключительно недостающий чанк и запишет его прямо в тело существующего файла.
                Log.Info($"[AudioEngine] Surgical patch in progress. Preserving existing cache file for: {trackId}");
            }

            if (track != null)
            {
                ResetSealedFailedTrack();
                int session = BeginNewSession();
                // Указываем IsRetry: true для предотвращения сброса счетчика попыток
                EnqueueCommand(new PlayTrackCommand(track, session, resumePosition, IsRetry: true));
            }
            return;
        }

        Log.Warn($"[AudioEngine] Cache error non-recoverable or retry budget exhausted (retries={_cacheRetryCount}, kind={cacheEx.Kind}): {cacheEx.Message}");

        if (!string.IsNullOrEmpty(trackId))
        {
            try { AudioSourceFactory.GlobalCache?.RemoveTrackCache(trackId); }
            catch (Exception ex) { Log.Warn($"[AudioEngine] Failed to remove cache: {ex.Message}"); }
        }

        RaiseError(new CacheInvalidatedException(cacheEx.Message, cacheEx.InnerException));
    }

    private void RaiseError(Exception exception)
    {
        RaiseOnUI(() => OnErrorOccurred?.Invoke(exception));
    }

    #endregion

    #region ISuspendable Implementation

    /// <inheritdoc />
    public void OnSuspend(SuspendLevel level)
    {
        _isSuspended = true;

        if (ShouldKeepSourceActiveWhileSuspended())
        {
            Log.Debug("[AudioEngine] Suspend policy: source remains active due to active playback/buffering");
            return;
        }

        ApplyLifecycleSourceSuspendPolicy();
    }

    /// <inheritdoc />
    /// <remarks>
    /// При выходе из suspend вместо бесполезного HEAD к <c>redirector.googlevideo.com</c>
    /// прогреваем TCP+TLS к последним реально использованным CDN-нодам.
    /// Это обеспечивает мгновенное возобновление preload после разворачивания окна.
    /// </remarks>
    public void OnResume(SuspendLevel previousLevel)
    {
        _isSuspended = false;
        ApplyLifecycleSourceSuspendPolicy();

        // Прогрев реальных CDN-нод вместо redirector:
        // после suspend idle-соединения могут быть закрыты ОС/сервером.
        // Спекулятивный прогрев восстанавливает их до первого реального range-запроса.
        AudioSourceFactory.PreWarmCdnConnections(
            Audio.Http.SharedHttpClient.Instance, _lifetimeCts.Token);
    }

    #endregion

    #region Statistics

    internal AudioPipeline? GetActivePipeline() => _player.GetActivePipeline();
    public long GetDownloadedBytes() => _player.GetDownloadedBytes();
    public IReadOnlyList<(double Start, double End)> GetBufferedRanges() => _player.GetBufferedRanges();

    public (AudioFormat Format, int Bitrate, bool IsReady) GetCurrentStreamInfo()
    {
        var info = StreamInfo;
        return (info.Format, info.Bitrate, info.IsValid);
    }

    #endregion

    #region Helpers

    /// <summary>
    /// Определяет, стоит ли считать continuation URL из session cache актуальным.
    /// </summary>
    /// <param name="manifest">Manifest из session cache.</param>
    /// <param name="safetyBufferMinutes">Запас времени до expiry (по умолчанию 5 минут).</param>
    /// <returns>
    /// <c>true</c> — URL скорее всего ещё жив;<br/>
    /// <c>false</c> — URL протухает в ближайшие <paramref name="safetyBufferMinutes"/> минут
    /// или уже протух.
    /// </returns>
    private static bool IsContinuationUrlLikelyFresh(
        Audio.Http.TrackManifestEntry manifest,
        int safetyBufferMinutes = 5)
    {
        // Если expire не выставлен — не можем судить, считаем актуальным.
        if (manifest.ExpireUtc == default || manifest.ExpireUtc == DateTime.MinValue)
            return true;

        // URL считается протухшим если до expiry меньше safetyBufferMinutes.
        // Это покрывает случай когда трек лежит в очереди и начинает играть
        // позже чем был получен manifest.
        return DateTime.UtcNow.AddMinutes(safetyBufferMinutes) < manifest.ExpireUtc;
    }

    /// <summary>
    /// Возвращает IP исходящего интерфейса через routing table ОС.
    /// UDP Connect() не отправляет пакетов — только резолвит маршрут.
    /// </summary>
    private static string? GetOutboundIp()
    {
        try
        {
            using var socket = new System.Net.Sockets.Socket(
                System.Net.Sockets.AddressFamily.InterNetwork,
                System.Net.Sockets.SocketType.Dgram,
                System.Net.Sockets.ProtocolType.Udp);

            socket.Connect("8.8.8.8", 65530);
            var ip = (socket.LocalEndPoint as System.Net.IPEndPoint)?.Address.ToString();

            Log.Debug($"[AudioEngine] GetOutboundIp: {ip ?? "(none)"}" +
                      (ip != null && IsVpnTunAddress(ip) ? " [TUN/VPN static — diff bypass]" : ""));

            return ip;
        }
        catch (Exception ex)
        {
            Log.Debug($"[AudioEngine] GetOutboundIp failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Определяет, является ли IP-адрес статическим адресом TUN-адаптера VPN.
    ///
    /// Такие адреса никогда не меняются даже при переподключении туннеля,
    /// поэтому diff-фильтр (currentIp == lastIp) для них бессмысленен —
    /// rebuild нужно выполнять безусловно при NetworkAddressChanged.
    ///
    /// Диапазоны:
    ///   198.18.0.0/15   — RFC 2544 (Benchmarking), стандартный TUN у Xray/sing-box
    ///   198.51.100.0/24 — RFC 5737 TEST-NET-2
    ///   203.0.113.0/24  — RFC 5737 TEST-NET-3
    ///   100.64.0.0/10   — RFC 6598 Shared Address Space (некоторые WireGuard/Tailscale)
    /// </summary>
    private static bool IsVpnTunAddress(string ip)
    {
        if (!System.Net.IPAddress.TryParse(ip, out var addr))
            return false;

        var bytes = addr.GetAddressBytes();
        if (bytes.Length != 4) return false;

        // 198.18.0.0/15: bytes[0]==198, bytes[1] in [18,19]
        if (bytes[0] == 198 && bytes[1] is 18 or 19) return true;

        // 198.51.100.0/24
        if (bytes[0] == 198 && bytes[1] == 51 && bytes[2] == 100) return true;

        // 203.0.113.0/24
        if (bytes[0] == 203 && bytes[1] == 0 && bytes[2] == 113) return true;

        // 100.64.0.0/10: bytes[0]==100, bytes[1] in [64..127]
        if (bytes[0] == 100 && bytes[1] is >= 64 and <= 127) return true;

        return false;
    }

    /// <summary>
    /// Триггер А: смена IP (VPN вкл/выкл, смена интерфейса).
    /// Дебаунс 2с + diff фильтр — исключает шум NetworkAddressChanged.
    /// </summary>
    private void OnNetworkAddressChanged(object? sender, EventArgs e)
    {
        Log.Debug("[AudioEngine] NetworkAddressChanged event received");

        CancellationTokenSource newCts;
        lock (_networkRebuildLock)
        {
            _networkRebuildCts?.Cancel();
            _networkRebuildCts?.Dispose();
            _networkRebuildCts = newCts = CancellationTokenSource
                .CreateLinkedTokenSource(_lifetimeCts.Token);
        }

        _ = RebuildNetworkClientsAfterDelayAsync(newCts.Token);
    }

    /// <summary>
    /// Пересоздаёт HTTP-клиенты после стабилизации сетевого интерфейса.
    /// Дебаунс 2с + фильтрация по diff IP исключает шум:
    /// смену метрик, DHCP renewal, добавление IPv6 link-local.
    /// </summary>
    private async Task RebuildNetworkClientsAfterDelayAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);

            var currentIp = GetOutboundIp();
            if (currentIp == null)
            {
                Log.Debug("[AudioEngine] Network change ignored — no outbound route");
                return;
            }

            // При статическом TUN-адресе VPN diff всегда даёт false negative:
            // IP не меняется даже когда туннель умер и переподключился.
            // В этом случае bypass diff-фильтр и rebuild безусловно.
            bool isTunAddress = IsVpnTunAddress(currentIp);

            if (!isTunAddress &&
                string.Equals(currentIp, _lastOutboundIp, StringComparison.Ordinal))
            {
                Log.Debug($"[AudioEngine] Network change ignored — outbound IP unchanged ({currentIp})");
                return;
            }

            if (isTunAddress)
            {
                Log.Info($"[AudioEngine] TUN/VPN address detected ({currentIp}) — " +
                         "diff bypass, rebuilding unconditionally.");
            }
            else
            {
                Log.Info($"[AudioEngine] Outbound IP changed: " +
                         $"{_lastOutboundIp ?? "(none)"} → {currentIp}. Rebuilding.");
            }

            _lastOutboundIp = currentIp;
            await RebuildNetworkCoreAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Warn($"[AudioEngine] Network rebuild failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Триггер Б: мёртвый туннель с неизменным IP.
    /// Вызывается при starvation из AudioPlayer.
    /// Безусловный rebuild — diff не проверяем, туннель уже мёртв.
    /// </summary>
    internal void NotifyNetworkStarvation()
    {
        Log.Info("[AudioEngine] Network starvation detected — forcing HTTP client rebuild");

        CancellationTokenSource newCts;
        lock (_networkRebuildLock)
        {
            _networkRebuildCts?.Cancel();
            _networkRebuildCts?.Dispose();
            _networkRebuildCts = newCts = CancellationTokenSource
                .CreateLinkedTokenSource(_lifetimeCts.Token);
        }

        _ = ForceRebuildAfterStarvationAsync(newCts.Token);
    }

    private async Task ForceRebuildAfterStarvationAsync(CancellationToken ct)
    {
        try
        {
            // Короткий дебаунс — защита от шторма при серии underrun'ов
            await Task.Delay(TimeSpan.FromMilliseconds(500), ct).ConfigureAwait(false);

            // Обновляем snapshot — чтобы следующий NetworkAddressChanged
            // не сделал двойной rebuild если IP не изменился
            _lastOutboundIp = GetOutboundIp();

            Log.Info($"[AudioEngine] Force rebuild. Current outbound IP: {_lastOutboundIp ?? "(none)"}");

            await RebuildNetworkCoreAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Warn($"[AudioEngine] Force rebuild failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Единая точка пересборки HTTP-клиентов.
    /// Вызывается из обоих триггеров.
    /// </summary>
    private async Task RebuildNetworkCoreAsync()
    {
        Audio.Http.SharedHttpClient.Rebuild(_library.Settings.Proxy);
        _youtube.ReloadClient();

        Log.Info("[AudioEngine] HTTP clients rebuilt.");

        AudioSourceFactory.PreWarmCdnConnections(
            Audio.Http.SharedHttpClient.Instance, _lifetimeCts.Token);

        await Task.CompletedTask.ConfigureAwait(false);
    }

    /// <summary>
    /// Watchdog для VPN-клиентов без NetworkAddressChanged (WireGuard service на Windows).
    /// Интервал 3 минуты, 0 сетевых запросов — только routing table ОС.
    /// </summary>
    private async Task NetworkWatchdogAsync()
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(30), _lifetimeCts.Token).ConfigureAwait(false);

            while (!_lifetimeCts.Token.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromMinutes(3), _lifetimeCts.Token).ConfigureAwait(false);

                var currentIp = GetOutboundIp();
                if (currentIp != null
                    && _lastOutboundIp != null
                    && !string.Equals(currentIp, _lastOutboundIp, StringComparison.Ordinal))
                {
                    Log.Info($"[AudioEngine] Watchdog: IP change missed by NetworkChange event: " +
                            $"{_lastOutboundIp} → {currentIp}");
                    OnNetworkAddressChanged(this, EventArgs.Empty);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Warn($"[AudioEngine] Network watchdog error: {ex.Message}");
        }
    }

    /// <summary>
    /// Выбирает лучший вариант аудио-потока из списка по совпадению формата контейнера.
    /// </summary>
    private static VariantEntry? SelectBestVariantFromEntry(List<VariantEntry> variants, AudioFormat? preferredFormat)
    {
        if (variants.Count == 0) return null;

        if (preferredFormat is { } requestedFormat && requestedFormat != AudioFormat.Unknown)
        {
            for (int i = 0; i < variants.Count; i++)
            {
                if (variants[i].Format == requestedFormat)
                    return variants[i];
            }
        }

        return variants[0];
    }

    /// <summary>
    /// Проверяет, совместим ли continuation stream variant с уже выбранным partial cache.
    /// Это предотвращает прикрепление URL от другого контейнера/битрейта к существующему cache bucket.
    /// </summary>
    /// <param name="expectedEntry">Ожидаемая cache entry partial bootstrap.</param>
    /// <param name="format">Формат continuation stream.</param>
    /// <param name="bitrate">Битрейт continuation stream.</param>
    private static bool IsContinuationVariantCompatible(
         AudioCacheEntry expectedEntry,
         AudioFormat format,
         int bitrate)
    {
        if (format == AudioFormat.Unknown)
            return false;

        if (format != expectedEntry.Format)
            return false;

        if (bitrate <= 0 || expectedEntry.Bitrate <= 0)
            return true;

        string candidateKey = AudioSourceFactory.BuildCacheKey(
            expectedEntry.TrackId,
            format,
            bitrate);

        return string.Equals(candidateKey, expectedEntry.CacheKey, StringComparison.Ordinal);
    }

    /// <summary>
    /// Пытается извлечь уже известный continuation URL из session cache или provider memory cache,
    /// если он совместим с выбранным partial-cache variant и ссылка ещё жива.
    /// </summary>
    private bool TryGetCompatibleContinuationUrl(
     TrackInfo track,
     AudioCacheEntry expectedEntry,
     out string url)
    {
        url = string.Empty;

        var manifest = SessionCacheStore.GetManifest(track.Id);

        if (manifest != null && IsContinuationUrlLikelyFresh(manifest))
        {
            for (int i = 0; i < manifest.Variants.Count; i++)
            {
                var variant = manifest.Variants[i];
                if (IsContinuationVariantCompatible(expectedEntry, variant.Format, variant.Bitrate / 1000))
                {
                    url = variant.Url;
                    return true;
                }
            }
        }

        var rawId = track.GetRawIdSpan().ToString();
        var descriptor = _youtube.TryGetCachedStreamDescriptor(
            rawId,
            expectedEntry.Format,
            expectedEntry.Bitrate);

        if (descriptor is { HasLiveUrl: true } d &&
            IsContinuationVariantCompatible(expectedEntry, d.Format, d.BitrateKbps))
        {
            if (d.ExpireUtc == default || DateTime.UtcNow.AddMinutes(5) < d.ExpireUtc)
            {
                url = d.Url;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Возвращает наиболее релевантную metadata-запись кэша для нормализации.
    /// </summary>
    /// <param name="trackId">Идентификатор трека.</param>
    private static AudioCacheEntry? FindNormalizationCacheEntry(string trackId)
    {
        var cache = AudioSourceFactory.GlobalCache;
        if (cache == null || string.IsNullOrEmpty(trackId))
            return null;

        return cache.FindBestCacheByTrackId(trackId) ?? cache.FindBestStartupCache(trackId, 0);
    }

    /// <summary>
    /// Потокобезопасно обновляет статус ручной загрузки движка на UI-потоке.
    /// </summary>
    private void SetManualLoading(bool loading)
    {
        RaiseOnUI(() =>
        {
            if (_isManualLoading != loading)
            {
                _isManualLoading = loading;
                this.RaisePropertyChanged(nameof(IsLoading));
                OnLoadingStateChanged?.Invoke(IsLoading);
            }
        });
    }

    /// <summary>
    /// Определяет, должен ли сетевой audio source оставаться активным,
    /// даже если UI ушёл в background/suspend.
    /// </summary>
    /// <returns>
    /// <c>true</c>, если source нельзя suspend'ить;
    /// <c>false</c>, если suspend source допустим.
    /// </returns>
    /// <remarks>
    /// <para>Ключевой принцип: UI suspend ≠ audio/network suspend.</para>
    /// <para>
    /// Пока player находится в состояниях <see cref="PlaybackState.Loading"/>,
    /// <see cref="PlaybackState.Buffering"/>, <see cref="PlaybackState.Playing"/>
    /// или в детальном состоянии <see cref="PlayerState.Seeking"/>,
    /// source preload критически важен для стабильного playback/rebuffer.
    /// </para>
    /// </remarks>
    private bool ShouldKeepSourceActiveWhileSuspended()
    {
        var playbackState = _player.State;
        var detailedState = _player.DetailedState;

        return playbackState is PlaybackState.Loading
            or PlaybackState.Buffering
            or PlaybackState.Playing
            || detailedState == PlayerState.Seeking;
    }

    /// <summary>
    /// Применяет lifecycle-политику suspend/resume к активному сетевому audio source.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Если приложение suspended, но playback ещё активен, source НЕ приостанавливается:
    /// это предотвращает starvation на сетях с высоким RTT.
    /// </para>
    /// <para>
    /// Если playback не активен (paused/stopped/error) и приложение suspended —
    /// source можно safely suspend'ить для экономии ресурсов.
    /// </para>
    /// </remarks>
    private void ApplyLifecycleSourceSuspendPolicy()
    {
        if (_player.GetActivePipeline()?.Source is not Audio.Sources.CachingStreamSource cs)
        {
            Volatile.Write(ref _sourceLifecycleSuspended, 0);
            return;
        }

        if (!_isSuspended || ShouldKeepSourceActiveWhileSuspended())
        {
            if (Interlocked.Exchange(ref _sourceLifecycleSuspended, 0) != 0)
                cs.Resume();

            return;
        }

        if (Interlocked.Exchange(ref _sourceLifecycleSuspended, 1) == 0)
            cs.Suspend();
    }

    private static void RaiseOnUI(Action action)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(action);
    }

    public static Task ReinitializeWithProfileAsync(InternetProfile profile)
    {
        AudioSourceFactory.ApplyInternetProfile(profile);
        return Task.CompletedTask;
    }

    #endregion

    #region Dispose

    /// <summary>
    /// Синхронный dispose — FALLBACK shutdown path.
    /// </summary>
    /// <remarks>
    /// Best-effort cleanup без блокирующего ожидания async операций.
    /// Для минимизации потерь выполняет синхронный flush pending gain writes,
    /// но основной корректный shutdown-path остаётся за <see cref="DisposeAsync"/>.
    /// </remarks>
    private void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;

            _youtube.OnNTokenDecryptionStarted -= HandleNTokenDecryptionStarted;
            CdnConnectionPreWarmer.OnTunnelDeadDetected -= HandleCdnTunnelDead;
            lock (_sessionLock) { _sessionCts?.Cancel(); _sessionCts?.Dispose(); }

            try
            {
                FlushPendingNormalizationWritesSync();
            }
            catch (Exception ex)
            {
                Log.Warn($"[AudioEngine] Sync normalization flush on dispose failed: {ex.Message}");
            }

            _library.UpdateSettings(s =>
            {
                s.Volume = _volumePercent;
                s.RepeatMode = RepeatMode;
                s.ShuffleEnabled = ShuffleEnabled;
            });

            _commandQueue.Writer.TryComplete();
            _lifetimeCts.Cancel();

            try { _commandProcessorTask?.Wait(millisecondsTimeout: 500); } catch { }
            try { _volumeSaveTask?.Wait(millisecondsTimeout: 200); } catch { }
            try { _networkWatchdogTask?.Wait(millisecondsTimeout: 300); } catch { }

            System.Net.NetworkInformation.NetworkChange.NetworkAddressChanged
                -= OnNetworkAddressChanged;

            lock (_networkRebuildLock)
            {
                _networkRebuildCts?.Cancel();
                _networkRebuildCts?.Dispose();
                _networkRebuildCts = null;
            }

            _player.Dispose();
            _lifetimeCts.Dispose();
        }
    }

    /// <summary>
    /// Асинхронный dispose — PRIMARY shutdown path.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        // 1. Отписка + отмена active CTS
        _youtube.OnNTokenDecryptionStarted -= HandleNTokenDecryptionStarted;
        CdnConnectionPreWarmer.OnTunnelDeadDetected -= HandleCdnTunnelDead;
        lock (_sessionLock) { _sessionCts?.Cancel(); _sessionCts?.Dispose(); }

        // 2. Синхронное сохранение настроек (in-memory словарь, sub-μs)
        _library.UpdateSettings(s =>
        {
            s.Volume = _volumePercent;
            s.RepeatMode = RepeatMode;
            s.ShuffleEnabled = ShuffleEnabled;
        });

        using (var flushCts = new CancellationTokenSource(TimeSpan.FromSeconds(3)))
        {
            try
            {
                await FlushPendingNormalizationWritesAsync(flushCts.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Warn($"[AudioEngine] Normalization flush on async dispose: {ex.Message}");
            }
        }

        // 4. Complete writer — «новых команд не будет»; штатное завершение ReadAllAsync
        _commandQueue.Writer.TryComplete();

        // 5. Cancel lifetime — fallback-сигнал для loop'ов и VolumeSaveLoop
        _lifetimeCts.Cancel();

        // 6. Детерминированный drain: ждём завершения обоих loop'ов
        //  Таймауты выровнены с DisposeTaskTimeoutSec AudioPlayer'а
        const int loopDrainTimeoutMs = 2_000;
        if (_commandProcessorTask != null)
        {
            try
            {
                await _commandProcessorTask
                    .WaitAsync(TimeSpan.FromMilliseconds(loopDrainTimeoutMs))
                    .ConfigureAwait(false);
            }
            catch (TimeoutException)
            { Log.Warn("[AudioEngine] Command processor did not finish within dispose timeout"); }
            catch (Exception ex) when (ex is OperationCanceledException or AggregateException) { }
        }

        if (_volumeSaveTask != null)
        {
            try
            {
                await _volumeSaveTask
                    .WaitAsync(TimeSpan.FromMilliseconds(loopDrainTimeoutMs))
                    .ConfigureAwait(false);
            }
            catch (TimeoutException)
            { Log.Warn("[AudioEngine] Volume save loop did not finish within dispose timeout"); }
            catch (Exception ex) when (ex is OperationCanceledException or AggregateException) { }
        }

        if (_networkWatchdogTask != null)
        {
            try
            {
                await _networkWatchdogTask
                    .WaitAsync(TimeSpan.FromMilliseconds(500))
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is TimeoutException or OperationCanceledException or AggregateException) { }
        }

        System.Net.NetworkInformation.NetworkChange.NetworkAddressChanged
            -= OnNetworkAddressChanged;

        lock (_networkRebuildLock)
        {
            _networkRebuildCts?.Cancel();
            _networkRebuildCts?.Dispose();
            _networkRebuildCts = null;
        }

        // 7. Async dispose плеера (ожидает его внутренний command processor)
        await _player.DisposeAsync().ConfigureAwait(false);

        // 8. Dispose lifetime CTS
        _lifetimeCts.Dispose();

        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Выполняет синхронную утилизацию ресурсов аудио-движка.
    /// </summary>
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    #endregion
}
