using System.Collections.Concurrent;
using System.Threading.Channels;
using LMP.Core.Audio.Helpers;
using LMP.Core.Audio.Http;
using LMP.Core.Audio.Interfaces;
using LMP.Core.Audio.Normalization;
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

    #region Record Structs

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

    /// <summary>Контекст предупреждения о n-токене.</summary>
    public readonly record struct NTokenWarningInfo(TrackInfo? Track, bool WasSkipped);

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
    /// recoverable <see cref="Exceptions.CacheInvalidatedException"/> для одного трека.
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
    private readonly TrackRegistry _trackRegistry;

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

    #region Constructor & Initialization

    /// <summary>
    /// Инициализирует центральный движок воспроизведения.
    /// </summary>
    public AudioEngine(YoutubeProvider youtube, LibraryService library, TrackRegistry trackRegistry)
    {
        _youtube = youtube;
        _library = library;
        _trackRegistry = trackRegistry;

        ApplyStreamingProfile();

        if (_library.IsInitialized)
        {
            InitializeFromSettings();
        }
        else
        {
            _library.OnInitialized += InitializeFromSettings;
        }

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
        Audio.Sources.CachingStreamSource.OnNetworkStalled += HandleSourceNetworkStalled;
        Audio.Sources.CachingStreamSource.OnNetworkRecovered += HandleSourceNetworkRecovered;
        InitializeFromSettings();

        _commandQueue = Channel.CreateBounded<IEngineCommand>(
            new BoundedChannelOptions(CommandQueueCapacity)
            {
                SingleReader = true,
                FullMode = BoundedChannelFullMode.DropOldest
            });

        _commandProcessorTask = Task.Run(ProcessCommandsAsync);
        _volumeSaveTask = Task.Run(VolumeSaveLoopAsync);

        LifecycleRegistry.Instance?.RegisterBackgroundSuspendable(this);

        _lastOutboundIp = GetOutboundIp();
        Log.Debug($"[AudioEngine] Initial outbound IP: {_lastOutboundIp ?? "(none)"}");

        System.Net.NetworkInformation.NetworkChange.NetworkAddressChanged
            += OnNetworkAddressChanged;

        _networkWatchdogTask = Task.Run(NetworkWatchdogAsync);
    }

    /// <summary>
    /// Извлекает сохранённые параметры воспроизведения из настроек и применяет их к активному конвейеру.
    /// </summary>
    private void InitializeFromSettings()
    {
        var settings = _library.Settings;
        ShuffleEnabled = settings.ShuffleEnabled;
        RepeatMode = settings.RepeatMode;

        InitializeVolumeFromSettings();
    }

    private void ApplyStreamingProfile()
    {
        AudioSourceFactory.ApplyInternetProfile(_library.Settings.InternetProfile);
    }

    #endregion

    #region Internal Event Delegates

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

    #region Playback Control API

    public Task PlayTrackAsync(TrackInfo track)
    {
        if (track == null) return Task.CompletedTask;
        ResetSealedFailedTrack();
        int session = BeginNewSession();
        EnqueueCommand(new PlayTrackCommand(track, session, null));
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

    #region Seek

    /// <summary>
    /// Выполняет seek немедленно.
    /// </summary>
    public ValueTask SeekAsync(TimeSpan position)
    {
        return _player.SeekAsync(position);
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

    private static void RaiseOnUI(Action action)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(action);
    }

    public static Task ReinitializeWithProfileAsync(InternetProfile profile)
    {
        AudioSourceFactory.ApplyInternetProfile(profile);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Извлекает <see cref="CdnUnavailableException"/> из любого уровня цепочки исключений.
    /// </summary>
    private static Exceptions.CdnUnavailableException? TryExtractCdnException(Exception ex)
    {
        var current = ex;
        while (current is not null)
        {
            if (current is Exceptions.CdnUnavailableException cdn)
                return cdn;
            current = current.InnerException;
        }
        return null;
    }

    #endregion
}