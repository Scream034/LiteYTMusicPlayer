using System.Runtime.CompilerServices;
using System.Threading.Channels;
using LMP.Core.Audio.Helpers;
using LMP.Core.Audio.Interfaces;
using LMP.Core.Exceptions;

namespace LMP.Core.Audio;

/// <summary>
/// Аудио плеер с акторной моделью обработки команд.
/// </summary>
/// <remarks>
/// <para><b>Декомпозиция (partial classes):</b></para>
/// <list type="bullet">
///   <item><c>AudioPlayer.cs</c> — ядро: command loop, state machine, play/stop/resume.</item>
///   <item><c>AudioPlayer.Seek.cs</c> — двухфазный seek.</item>
///   <item><c>AudioPlayer.DeviceRecovery.cs</c> — восстановление после потери устройства.</item>
///   <item><c>AudioPlayer.Timers.cs</c> — управление таймерами UI-обновлений.</item>
/// </list>
/// </remarks>
public sealed partial class AudioPlayer : IAsyncDisposable, IDisposable
{
    #region Constants

    private const int CommandChannelCapacity = 32;
    private const int StopTaskTimeoutSec = 2;
    private const int DisposeTaskTimeoutSec = 2;
    private const int ResumeMinBufferMs = 100;
    private const int ResumeWarmupTimeoutMs = 500;
    /// <summary>Таймаут остановки decoder при fast-rewind (мс).</summary>
    private const int RewindDecoderStopTimeoutMs = 300;
    private const int BitsPerByte = 8;

    #endregion

    #region Nested Types

    /// <summary>
    /// План прогрева перед открытием playback gate.
    /// </summary>
    /// <param name="PcmThresholdSamples">Минимальный объём PCM в ring buffer.</param>
    /// <param name="SourceAheadMs">Минимальный непрерывный запас данных вперёд в source.</param>
    /// <param name="WarmupTimeoutMs">Таймаут активного ожидания прогрева.</param>
    private readonly record struct PlaybackWarmupPlan(
        int PcmThresholdSamples,
        int SourceAheadMs,
        int WarmupTimeoutMs);

    /// <summary>
    /// Пользовательское намерение относительно playback lifecycle.
    /// Отделено от operational state machine.
    /// </summary>
    private enum PlaybackIntent
    {
        Stop = 0,
        Pause = 1,
        Play = 2
    }

    private sealed record PlayAsyncContext(
        AudioPlayer Player,
        Action<PlaybackState> OnState,
        Action<AudioPlayerError> OnError,
        CancellationTokenRegistration Reg);

    #endregion

    #region Fields

    private readonly AudioPlayerOptions _options;
    private readonly AudioPlayerEvents _events = new();
    private readonly Channel<IAudioCommand> _commandChannel;
    private readonly CancellationTokenSource _lifetimeCts = new();
    private readonly Task _commandProcessorTask;
    private readonly IPlaybackBackend _sharedBackend;

    private volatile AudioPipeline? _activePipeline;
    private volatile PlayerState _state = PlayerState.Idle;
    private volatile bool _disposed;

    private int _playbackIntent = (int)PlaybackIntent.Stop;
    private int _seekGeneration;

    /// <summary>
    /// Task dispose'а предыдущего pipeline.
    /// </summary>
    private Task? _oldPipelineDisposeTask;

    private SessionGuard _session;
    private string? _currentTrackId;
    private Func<CancellationToken, Task<string?>>? _cachedUrlRefresher;
    private string? _cachedUrlRefresherTrackId;
    private Func<CancellationToken, Task<string?>>? _cachedUrlAcquirer;
    private string? _cachedUrlAcquirerTrackId;

    private readonly SharedPlaybackState _sharedState = new();

    /// <summary>
    /// Последнее сырое значение played samples из драйвера.
    /// </summary>
    private long _lastRawPlayedSamples = -1;

    #endregion

    #region Properties

    /// <summary>События плеера.</summary>
    public AudioPlayerEvents Events => _events;

    /// <summary>Текущее детальное состояние.</summary>
    public PlayerState DetailedState => _state;

    /// <summary>Текущая позиция воспроизведения.</summary>
    public TimeSpan Position
    {
        get
        {
            var pipeline = _activePipeline;
            if (pipeline == null) return TimeSpan.Zero;

            int bufferedSamples = pipeline.BackendBufferedSamples;
            long played = Math.Max(0, pipeline.PlayedSamples - bufferedSamples);

            if (played != _lastRawPlayedSamples)
            {
                _lastRawPlayedSamples = played;
                _sharedState.Update(
                    played,
                    bufferedSamples,
                    pipeline.SampleRate,
                    pipeline.Channels,
                    _state == PlayerState.Playing,
                    (long)Duration.TotalMilliseconds);
            }

            return _sharedState.GetCurrentPosition();
        }
    }

    /// <summary>Длительность текущего трека.</summary>
    public TimeSpan Duration => _activePipeline != null
        ? TimeSpan.FromMilliseconds(_activePipeline.StreamInfo.DurationMs)
        : TimeSpan.Zero;

    /// <summary>Текущее публичное состояние playback.</summary>
    public PlaybackState State => MapState(_state);

    /// <summary>Текущая информация о потоке.</summary>
    public AudioStreamInfo StreamInfo => _activePipeline?.StreamInfo ?? AudioStreamInfo.Empty;

    #endregion

    #region Constructor

    /// <summary>Создаёт плеер.</summary>
    public AudioPlayer(AudioPlayerOptions? options = null)
    {
        _options = options ?? new AudioPlayerOptions();

        _commandChannel = Channel.CreateBounded<IAudioCommand>(new BoundedChannelOptions(CommandChannelCapacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropOldest
        });

        _sharedBackend = CreateSharedBackend(_options);
        _commandProcessorTask = Task.Run(ProcessCommandsAsync);
    }

    private static IPlaybackBackend CreateSharedBackend(AudioPlayerOptions options)
    {
        if (options.UseNullBackend) return new Backends.NullAudioBackend();

        try
        {
            return new Backends.NAudioBackend();
        }
        catch (Exception ex)
        {
            Log.Warn($"[AudioPlayer] NAudio failed: {ex.Message}, using NullBackend");
            return new Backends.NullAudioBackend();
        }
    }

    #endregion

    #region Public API

    /// <summary>
    /// Инициирует запуск воспроизведения.
    /// </summary>
    private void Play(
        ResolvedStreamDescriptor descriptor,
        TimeSpan? seekPosition = null,
        CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        SetPlaybackIntent(PlaybackIntent.Play);
        CancelActiveSeek();

        int session = _session.BeginNew();
        _currentTrackId = descriptor.TrackId;
        _lastRawPlayedSamples = -1;

        _commandChannel.Writer.TryWrite(
            new PlayCommand(descriptor, session, seekPosition, ct));
    }

    /// <summary>
    /// Запускает воспроизведение и ожидает Playing.
    /// </summary>
    public Task PlayAsync(
        ResolvedStreamDescriptor descriptor,
        CancellationToken ct = default,
        TimeSpan? seekPosition = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (ct.IsCancellationRequested) return Task.FromCanceled(ct);

        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        void OnState(PlaybackState s)
        {
            if (s == PlaybackState.Playing)
                tcs.TrySetResult(true);
        }

        void OnError(AudioPlayerError e)
        {
            tcs.TrySetException(e.Exception ?? new Exception(e.Message));
        }

        _events.StateChanged += OnState;
        _events.ErrorOccurred += OnError;

        var reg = ct.UnsafeRegister(static state =>
            ((TaskCompletionSource<bool>)state!).TrySetCanceled(), tcs);

        tcs.Task.ContinueWith((_, state) =>
        {
            var ctx = (PlayAsyncContext)state!;
            ctx.Player._events.StateChanged -= ctx.OnState;
            ctx.Player._events.ErrorOccurred -= ctx.OnError;
            ctx.Reg.Dispose();
        },
        new PlayAsyncContext(this, OnState, OnError, reg),
        CancellationToken.None,
        TaskContinuationOptions.ExecuteSynchronously,
        TaskScheduler.Default);

        Play(descriptor, seekPosition, ct);
        return tcs.Task;
    }

    /// <summary>Ставит воспроизведение на паузу.</summary>
    public void Pause()
    {
        if (_disposed) return;

        SetPlaybackIntent(PlaybackIntent.Pause);
        CancelActiveSeek();
        _commandChannel.Writer.TryWrite(new PauseCommand(_session.Current));
    }

    /// <summary>Возобновляет воспроизведение.</summary>
    public void Resume()
    {
        if (_disposed) return;

        SetPlaybackIntent(PlaybackIntent.Play);
        _commandChannel.Writer.TryWrite(new ResumeCommand(_session.Current));
    }

    /// <summary>Останавливает воспроизведение.</summary>
    public void Stop()
    {
        if (_state is PlayerState.Idle or PlayerState.Disposed) return;

        SetPlaybackIntent(PlaybackIntent.Stop);
        CancelActiveSeek();
        _lastRawPlayedSamples = -1;

        _commandChannel.Writer.TryWrite(new StopCommand(_session.BeginNew()));
    }

    /// <summary>Асинхронный stop.</summary>
    public async Task StopAsync()
    {
        if (_state is PlayerState.Idle or PlayerState.Disposed) return;

        SetPlaybackIntent(PlaybackIntent.Stop);

        int session = _session.BeginNew();
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        void OnState(PlaybackState s)
        {
            if (s != PlaybackState.Stopped) return;
            _events.StateChanged -= OnState;
            tcs.TrySetResult(true);
        }

        _events.StateChanged += OnState;
        await _commandChannel.Writer.WriteAsync(new StopCommand(session)).ConfigureAwait(false);

        try
        {
            await tcs.Task.WaitAsync(TimeSpan.FromSeconds(StopTaskTimeoutSec)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            _events.StateChanged -= OnState;
        }
    }

    /// <summary>Немедленно применяет volume gain.</summary>
    public void SetVolumeGain(float gain) => _sharedBackend.SetVolumeGain(gain);

    #endregion

    #region Command Processing

    /// <summary>
    /// Основной actor loop.
    /// </summary>
    private async Task ProcessCommandsAsync()
    {
        try
        {
            await foreach (var command in _commandChannel.Reader
                .ReadAllAsync(_lifetimeCts.Token)
                .ConfigureAwait(false))
            {
                if (_session.IsStale(command.SessionId) && command is not DisposeCommand)
                {
                    if (command is SeekCommand { Completion: { } tcs })
                        tcs.TrySetCanceled();

                    continue;
                }

                if (!PlayerStateTransitions.CanAcceptCommand(_state, command))
                {
                    Log.Debug($"[AudioPlayer] Command {command.GetType().Name} rejected in state {_state}");

                    if (command is SeekCommand { Completion: { } failTcs })
                        failTcs.TrySetResult(false);

                    continue;
                }

                try
                {
                    switch (command)
                    {
                        case PlayCommand play:
                            await HandlePlayAsync(play).ConfigureAwait(false);
                            break;

                        case StopCommand:
                            await HandleStopAsync().ConfigureAwait(false);
                            break;

                        case PauseCommand:
                            await HandlePauseAsync().ConfigureAwait(false);
                            break;

                        case ResumeCommand resume:
                            await HandleResumeAsync(resume).ConfigureAwait(false);
                            break;

                        case SeekCommand seek:
                            await HandleSeekAsync(seek).ConfigureAwait(false);
                            break;

                        case DeferredResumeCommand deferredResume:
                            await HandleDeferredResumeAsync(deferredResume).ConfigureAwait(false);
                            break;

                        case StarvationCommand starvation:
                            await HandleStarvationAsync(starvation).ConfigureAwait(false);
                            break;

                        case TrackEndedCommand trackEnded:
                            await HandleTrackEndedAsync(trackEnded).ConfigureAwait(false);
                            break;

                        case DeviceLostCommand deviceLost:
                            await HandleDeviceLostAsync(deviceLost).ConfigureAwait(false);
                            break;

                        case DeviceAvailableCommand deviceAvailable:
                            await HandleDeviceAvailableAsync(deviceAvailable).ConfigureAwait(false);
                            break;

                        case DeviceRecoveryCommand recovery:
                            await HandleDeviceRecoveryAsync(recovery).ConfigureAwait(false);
                            break;

                        case PlayerErrorCommand error:
                            await HandlePlayerErrorAsync(error).ConfigureAwait(false);
                            break;

                        case DisposeCommand:
                            await HandleDisposeAsync().ConfigureAwait(false);
                            break;
                    }
                }
                catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    if (CancellationHelper.IsCancellationLike(ex) || _session.IsStale(command.SessionId))
                        continue;

                    Log.Error($"[AudioPlayer] Command error: {ex.Message}", ex);
                    HandleError(ex);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private Task HandlePauseAsync()
    {
        SetPlaybackIntent(PlaybackIntent.Pause);
        StopTimers();

        var pipeline = _activePipeline;
        pipeline?.Stop();

        if (_state is not (PlayerState.Idle or PlayerState.Disposed))
            SetState(PlayerState.Paused);

        return Task.CompletedTask;
    }

    private async Task HandleResumeAsync(ResumeCommand cmd)
    {
        SetPlaybackIntent(PlaybackIntent.Play);

        var pipeline = _activePipeline;
        if (pipeline == null) return;

        if (pipeline.IsDeviceLost)
        {
            SetState(PlayerState.Buffering);
            await HandleDeviceRecoveryAsync(new DeviceRecoveryCommand(cmd.SessionId)).ConfigureAwait(false);
            return;
        }

        // Проактивная проверка: если URL протух во время длительной паузы/сна,
        // обновляем его ДО открытия шлюзов и прогрева буфера
        if (pipeline.Source is Sources.CachingStreamSource cachingSource)
        {
            await cachingSource.EnsureStreamFreshnessAsync(_lifetimeCts.Token).ConfigureAwait(false);
        }

        int minBytes = pipeline.SampleRate * pipeline.Channels * sizeof(float) * ResumeMinBufferMs / 1000;
        if (_sharedBackend.BufferedBytes < minBytes)
        {
            pipeline.ActivateFillLoop();
            await pipeline.WaitForBackendWarmupAsync(ResumeWarmupTimeoutMs, _lifetimeCts.Token).ConfigureAwait(false);
        }

        ResumePlaybackSequence(pipeline, startTimers: true, configurePipeline: false, trackId: _currentTrackId);
    }

    private async Task HandleStopAsync()
    {
        SetPlaybackIntent(PlaybackIntent.Stop);
        CancelActiveSeek();
        StopTimers();

        var pipeline = Interlocked.Exchange(ref _activePipeline, null);
        if (pipeline != null)
            await pipeline.DisposeAsync().ConfigureAwait(false);

        _sharedBackend.Flush();
        _currentTrackId = null;
        SetState(PlayerState.Idle);
    }

    /// <summary>
    /// Обрабатывает Dispose-команду.
    /// </summary>
    private async Task HandleDisposeAsync()
    {
        SetPlaybackIntent(PlaybackIntent.Stop);
        await HandleStopAsync().ConfigureAwait(false);

        var oldDisposeTask = Volatile.Read(ref _oldPipelineDisposeTask);
        if (oldDisposeTask != null && !oldDisposeTask.IsCompleted)
        {
            try
            {
                await oldDisposeTask.WaitAsync(TimeSpan.FromMilliseconds(500)).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                Log.Warn("[AudioPlayer] Old pipeline dispose did not complete within HandleDisposeAsync timeout");
            }
            catch
            {
            }
        }

        DisposeTimers();
        SetState(PlayerState.Disposed);
    }

    /// <summary>
    /// Единая последовательность возобновления playback.
    /// </summary>
    private void ResumePlaybackSequence(
        AudioPipeline pipeline,
        bool startTimers,
        bool configurePipeline,
        string? trackId = null)
    {
        if (configurePipeline)
            _options.OnPipelineConfiguring?.Invoke(pipeline, trackId);

        pipeline.ActivateFillLoop();
        pipeline.Start();

        if (startTimers)
            StartTimers();

        SetState(PlayerState.Playing);
    }

    /// <summary>
    /// Строит adaptive план прогрева playback gate.
    /// </summary>
    private static PlaybackWarmupPlan ComputePlaybackWarmupPlan(AudioPipeline pipeline, bool isSeek)
    {
        if (pipeline.Source.IsFullyBuffered || pipeline.Source is Sources.LocalFileSource)
            return new PlaybackWarmupPlan(0, 0, 0);

        double avgPingMs = pipeline.Source is Sources.CachingStreamSource cs
            ? cs.AveragePingMs
            : 0;

        int sourceAheadMs = avgPingMs switch
        {
            <= 0 => isSeek ? 500 : 750,
            <= 150 => isSeek ? 600 : 800,
            <= 400 => isSeek ? 800 : 1200,
            <= 900 => isSeek ? 1200 : 2000,
            <= 2500 => isSeek ? 2000 : 3500,
            _ => isSeek ? 3000 : 5000
        };

        int pcmThresholdMs = avgPingMs switch
        {
            <= 0 => isSeek ? 60 : 80,
            <= 150 => isSeek ? 80 : 100,
            <= 400 => isSeek ? 100 : 120,
            <= 900 => isSeek ? 150 : 180,
            <= 2500 => isSeek ? 250 : 300,
            _ => isSeek ? 400 : 450
        };

        int warmupTimeoutMs = avgPingMs switch
        {
            <= 0 => isSeek ? 200 : 600,
            <= 150 => isSeek ? 300 : 800,
            <= 400 => isSeek ? 500 : 1500,
            <= 900 => isSeek ? 1000 : 3000,
            <= 2500 => isSeek ? 2000 : 7000,
            _ => isSeek ? 4000 : 10000
        };

        // NToken / network-contention guard
        if (!isSeek
            && pipeline.Source is Sources.CachingStreamSource csForBandwidth
            && csForBandwidth.EstimatedSpeedBytesPerSec <= 0)
        {
            sourceAheadMs = Math.Max(sourceAheadMs, 3000);
            warmupTimeoutMs = Math.Max(warmupTimeoutMs, 8000);
        }

        int samples = pipeline.SampleRate * pipeline.Channels * pcmThresholdMs / 1000;
        return new PlaybackWarmupPlan(Math.Max(samples, pipeline.Channels), sourceAheadMs, warmupTimeoutMs);
    }

    /// <summary>
    /// Возвращает непрерывный запас source-ahead в миллисекундах.
    /// </summary>
    private static int GetSourceBufferedAheadMs(AudioPipeline pipeline)
    {
        if (pipeline.Source.IsFullyBuffered || pipeline.Source is Sources.LocalFileSource)
            return int.MaxValue;

        if (pipeline.Source is Sources.CachingStreamSource cs)
            return cs.BufferedAheadMs;

        return 0;
    }

    /// <summary>
    /// Проверяет, достаточен ли запас source-ahead для открытия gate.
    /// </summary>
    private static bool IsSourceReadyForResume(AudioPipeline pipeline, int requiredAheadMs)
    {
        if (requiredAheadMs <= 0) return true;
        return GetSourceBufferedAheadMs(pipeline) >= requiredAheadMs;
    }

    /// <summary>
    /// Подготавливает pipeline и запускает decoder.
    /// </summary>
    private async Task PrepareAndStartDecodingAsync(
        AudioPipeline pipeline,
        PlayCommand cmd,
        CancellationToken ct)
    {
        await pipeline.PreScanNormalizationAsync(ct).ConfigureAwait(false);

        if (cmd.SeekPosition is { TotalMilliseconds: > 0 } seekPos)
        {
            long seekMs = (long)seekPos.TotalMilliseconds;
            pipeline.PrepareForSeek(seekMs);

            if (await pipeline.Source.SeekAsync(seekMs, ct).ConfigureAwait(false))
            {
                pipeline.SetDecodedSamplesPosition(
                    (long)(seekMs / 1000.0 * pipeline.SampleRate * pipeline.Channels));
            }
        }

        pipeline.StartDecoding(
            CreateUrlRefresher(),
            _options,
            CreateTrackEndedCallback(cmd.SessionId),
            CreateErrorCallback(cmd.SessionId, pipeline));
    }

    /// <summary>
    /// Выполняет воспроизведение нового дескриптора потока в контексте сессии.
    /// </summary>
    private async Task HandlePlayAsync(PlayCommand cmd)
    {
        Log.Info($"[AudioPlayer] HandlePlayAsync -> {cmd.Descriptor}, seek={cmd.SeekPosition?.TotalMilliseconds ?? 0}ms");

        SetPlaybackIntent(PlaybackIntent.Play);
        SetState(PlayerState.Loading);
        CancelActiveSeek();

        ResetPerTrackCounters();

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            _lifetimeCts.Token, cmd.ExternalCancellationToken);
        var ct = linkedCts.Token;

        float previousGain = _activePipeline?.GetLockedNormalizationGain() ?? 1.0f;

        try
        {
            ct.ThrowIfCancellationRequested();

            var pipeline = await AudioPipeline.CreateAsync(
                cmd.Descriptor,
                CreateUrlAcquirer(),
                CreateUrlRefresher(),
                _options,
                _sharedBackend,
                ct).ConfigureAwait(false);

            if (_session.IsStale(cmd.SessionId))
            {
                await pipeline.DisposeAsync().ConfigureAwait(false);
                return;
            }

            ct.ThrowIfCancellationRequested();

            StopTimers();

            var oldPipeline = Interlocked.Exchange(ref _activePipeline, pipeline);
            if (oldPipeline != null)
                TrackAndFirePipelineDispose(oldPipeline);

            _sharedBackend.Flush();
            _lastRawPlayedSamples = -1;

            _events.RaiseStreamInfo(pipeline.StreamInfo);

            int capturedSession = cmd.SessionId;
            pipeline.SetDeviceLostHandler(() => OnPipelineDeviceLost(pipeline, capturedSession));
            pipeline.SetDeviceAvailableHandler(() => OnPipelineDeviceAvailable(pipeline, capturedSession));
            pipeline.SetStarvationHandler(() => OnPipelineStarvation(pipeline, capturedSession));

            pipeline.SetInitialNormalizationGain(previousGain);
            _options.OnPipelineConfiguring?.Invoke(pipeline, cmd.Descriptor.TrackId);

            var lockedTrackId = cmd.Descriptor.TrackId;
            if (lockedTrackId != null && _options.OnIntegratedLufsResolved != null)
            {
                var lufsCb = _options.OnIntegratedLufsResolved;
                pipeline.Analyzer.SetIntegratedLufsCallback(
                    lufs => lufsCb(lockedTrackId, lufs, Normalization.LoudnessSource.EbuMeasured));
            }

            if (pipeline.IsDeviceLost)
            {
                await PrepareAndStartDecodingAsync(pipeline, cmd, ct).ConfigureAwait(false);
                SetState(PlayerState.Paused);
                _events.RaiseDeviceLost();
                return;
            }

            await PrepareAndStartDecodingAsync(pipeline, cmd, ct).ConfigureAwait(false);
            SetState(PlayerState.Buffering);

            var warmupPlan = ComputePlaybackWarmupPlan(pipeline, isSeek: false);

            bool pcmReady = await pipeline.WaitForBufferAsync(
                warmupPlan.PcmThresholdSamples,
                warmupPlan.WarmupTimeoutMs,
                ct).ConfigureAwait(false);

            bool sourceReady = IsSourceReadyForResume(pipeline, warmupPlan.SourceAheadMs);

            if (_session.IsStale(cmd.SessionId))
            {
                var stale = Interlocked.Exchange(ref _activePipeline, null);
                if (stale != null)
                    await stale.DisposeAsync().ConfigureAwait(false);
                return;
            }

            ct.ThrowIfCancellationRequested();

            if (CurrentPlaybackIntent != PlaybackIntent.Play)
            {
                pipeline.Stop();
                SetState(PlayerState.Paused);
                return;
            }

            ResumeOrDefer(pipeline, pcmReady, sourceReady, warmupPlan, cmd.SessionId,
                startTimers: true, configurePipeline: false, logContext: "Initial warmup");

            _ = WatchPipelineLifetimeAsync(pipeline, cmd.SessionId);
        }
        catch (OperationCanceledException)
        {
            if (_state is PlayerState.Loading or PlayerState.Buffering)
                SetState(PlayerState.Idle);
        }
        catch (NAudio.MmException ex)
        {
            SetState(PlayerState.Error);
            _events.RaiseError(new AudioPlayerError(GetDeviceErrorMessage(), ex));
        }
        catch (Exception ex) when (
            cmd.ExternalCancellationToken.IsCancellationRequested ||
            _session.IsStale(cmd.SessionId) ||
            CancellationHelper.IsCancellationLike(ex))
        {
            if (_state is PlayerState.Loading or PlayerState.Buffering)
                SetState(PlayerState.Idle);
        }
        catch (Exception ex)
        {
            SetState(PlayerState.Error);
            _events.RaiseError(new AudioPlayerError(ex.Message, ex));
        }
    }

    private async Task WatchPipelineLifetimeAsync(AudioPipeline pipeline, int sessionId)
    {
        try
        {
            await Task.Delay(Timeout.Infinite, pipeline.LifetimeToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            if (_disposed || _session.IsStale(sessionId) || _activePipeline != pipeline)
                return;

            try
            {
                _commandChannel.Writer.TryWrite(new PlayerErrorCommand(
                    sessionId,
                    pipeline,
                    new AudioDeviceException(GetDeviceErrorMessage())));

                _commandChannel.Writer.TryWrite(new StopCommand(sessionId));
            }
            catch (ChannelClosedException)
            {
            }
        }
    }

    #endregion

    #region Helpers

    /// <summary>
    /// Переводит pipeline в режим буферизации и запускает фоновое ожидание
    /// перед открытием playback gate.
    /// </summary>
    private void LaunchDeferredResume(
        AudioPipeline pipeline,
        PlaybackWarmupPlan warmupPlan,
        int sessionId)
    {
        pipeline.ActivateBufferingMode();

        int seekGeneration = Volatile.Read(ref _seekGeneration);
        var deferredResumeCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token);
        var previousCts = Interlocked.Exchange(ref _deferredResumeCts, deferredResumeCts);
        CancelCtsAsync(previousCts);

        _ = AwaitDeferredSeekBufferAndResumeAsync(
            pipeline,
            warmupPlan.PcmThresholdSamples,
            warmupPlan.SourceAheadMs,
            sessionId,
            seekGeneration,
            deferredResumeCts);
    }

    /// <summary>
    /// Возобновляет playback немедленно, если буферы готовы,
    /// или делегирует в <see cref="LaunchDeferredResume"/>.
    /// </summary>
    private void ResumeOrDefer(
        AudioPipeline pipeline,
        bool pcmReady,
        bool sourceReady,
        PlaybackWarmupPlan warmupPlan,
        int sessionId,
        bool startTimers,
        bool configurePipeline,
        string? trackId = null,
        string? logContext = null)
    {
        if (pcmReady && sourceReady)
        {
            ResumePlaybackSequence(pipeline, startTimers, configurePipeline, trackId);
            return;
        }

        Log.Warn($"[AudioPlayer] {logContext ?? "Warmup"} incomplete " +
                 $"(ring={pipeline.BufferedSamples}/{warmupPlan.PcmThresholdSamples}, " +
                 $"ahead={GetSourceBufferedAheadMs(pipeline)}ms/{warmupPlan.SourceAheadMs}ms). " +
                 "Deferred resume.");

        LaunchDeferredResume(pipeline, warmupPlan, sessionId);
    }

    /// <summary>
    /// Возвращает <c>true</c>, если pipeline-команда устарела и должна быть отброшена.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool IsPipelineCommandStale(AudioPipeline pipeline, int sessionId) =>
        _disposed || _activePipeline != pipeline || _session.IsStale(sessionId);

    /// <summary>
    /// Запускает dispose старого pipeline в фоне.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void TrackAndFirePipelineDispose(AudioPipeline pipeline)
    {
        var disposeTask = Task.Run(async () =>
        {
            try
            {
                await pipeline.DisposeAsync().ConfigureAwait(false);
            }
            catch
            {
            }
        });

        Volatile.Write(ref _oldPipelineDisposeTask, disposeTask);
    }

    /// <summary>
    /// Обрабатывает естественное завершение трека внутри actor loop.
    /// </summary>
    private async Task HandleTrackEndedAsync(TrackEndedCommand cmd)
    {
        if (_state is not (PlayerState.Buffering or PlayerState.Playing or PlayerState.Paused or PlayerState.Seeking))
            return;

        if (_activePipeline == null)
            return;

        if (_options.ShouldFastReplay?.Invoke() == true)
        {
            var pipeline = _activePipeline;
            if (pipeline is { IsDisposed: false, IsDeviceLost: false }
                && pipeline.Source.CanSeek)
            {
                if (await FastRewindInternalAsync(pipeline, cmd.SessionId).ConfigureAwait(false))
                    return;
            }
        }

        SetPlaybackIntent(PlaybackIntent.Stop);
        StopTimers();
        _sharedBackend.Flush();

        _events.RaiseTrackEnded();

        _currentTrackId = null;

        var old = Interlocked.Exchange(ref _activePipeline, null);
        if (old != null)
            TrackAndFirePipelineDispose(old);

        SetState(PlayerState.Idle);
    }

    /// <summary>
    /// Выполняет fast-rewind текущего pipeline в начало трека
    /// без пересоздания source, decoder и файловых дескрипторов.
    /// </summary>
    private async Task<bool> FastRewindInternalAsync(AudioPipeline pipeline, int sessionId)
    {
        try
        {
            SetState(PlayerState.Buffering);
            StopTimers();
            _lastRawPlayedSamples = -1;

            await pipeline.StopDecodingAsync(
                TimeSpan.FromMilliseconds(RewindDecoderStopTimeoutMs)).ConfigureAwait(false);

            pipeline.Stop();
            pipeline.Flush();
            pipeline.PrepareForSeek(0);

            bool seeked = await pipeline.Source
                .SeekAsync(0, _lifetimeCts.Token).ConfigureAwait(false);

            if (!seeked)
            {
                Log.Warn("[AudioPlayer] Fast rewind: seek(0) failed");
                return false;
            }

            pipeline.SetDecodedSamplesPosition(0);

            if (_disposed || _session.IsStale(sessionId))
                return false;

            pipeline.StartDecoding(
                CreateUrlRefresher(),
                _options,
                CreateTrackEndedCallback(sessionId),
                CreateErrorCallback(sessionId, pipeline));

            var warmupPlan = ComputePlaybackWarmupPlan(pipeline, isSeek: false);

            bool pcmReady = warmupPlan.PcmThresholdSamples <= 0;

            if (!pcmReady && warmupPlan.WarmupTimeoutMs > 0)
            {
                pcmReady = await pipeline.WaitForBufferAsync(
                    warmupPlan.PcmThresholdSamples,
                    warmupPlan.WarmupTimeoutMs,
                    _lifetimeCts.Token).ConfigureAwait(false);
            }

            bool sourceReady = IsSourceReadyForResume(pipeline, warmupPlan.SourceAheadMs);

            if (_disposed || _session.IsStale(sessionId))
                return false;

            if (CurrentPlaybackIntent != PlaybackIntent.Play)
            {
                pipeline.Stop();
                SetState(PlayerState.Paused);
                return true;
            }

            if (pipeline.Source.IsFullyBuffered)
                ResumePlaybackSequence(pipeline, startTimers: true, configurePipeline: false);
            else
                ResumeOrDefer(pipeline, pcmReady, sourceReady, warmupPlan, sessionId,
                    startTimers: true, configurePipeline: false, logContext: "Fast rewind warmup");

            Log.Info("[AudioPlayer] Fast rewind completed (repeat-one)");
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex)
        {
            Log.Warn($"[AudioPlayer] Fast rewind failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Обрабатывает starvation и переводит playback в adaptive rebuffer.
    /// </summary>
    private Task HandleStarvationAsync(StarvationCommand cmd)
    {
        if (IsPipelineCommandStale(cmd.Pipeline, cmd.SessionId))
            return Task.CompletedTask;

        // Принимаем starvation из Playing и Buffering:
        // из Playing — нормальный путь (ring опустел при воспроизведении);
        // из Buffering — сеть упала во время post-seek rebuffer (deferred resume уже активен,
        // но его CTS будет заменён новым в LaunchDeferredResume).
        if (_state is not (PlayerState.Playing or PlayerState.Buffering))
            return Task.CompletedTask;

        if (CurrentPlaybackIntent != PlaybackIntent.Play)
            return Task.CompletedTask;

        Log.Warn($"[AudioPlayer] Starvation in state={_state} — closing gate for adaptive rebuffer");

        var pipeline = cmd.Pipeline;

        // Stop только если играем: при Buffering backend уже остановлен.
        if (_state == PlayerState.Playing)
        {
            pipeline.Stop();
            StopTimers();
        }

        SetState(PlayerState.Buffering);
        _options.OnStarvationDetected?.Invoke();

        var recoveryPlan = ComputeStarvationRecoveryPlan(pipeline);
        LaunchDeferredResume(pipeline, recoveryPlan, cmd.SessionId);

        return Task.CompletedTask;
    }

    /// <summary>
    /// Вычисляет план восстановления после starvation с повышенными порогами.
    /// </summary>
    private static PlaybackWarmupPlan ComputeStarvationRecoveryPlan(AudioPipeline pipeline)
    {
        var basePlan = ComputePlaybackWarmupPlan(pipeline, isSeek: false);

        int recoverySourceAheadMs = basePlan.SourceAheadMs > 0
            ? Math.Min(basePlan.SourceAheadMs * 2, 8000)
            : 3000;

        // basePlan.PcmThresholdSamples = 0 при IsFullyBuffered=true.
        // Нулевой threshold даёт pcmReady=true немедленно при ring=0 → resume без данных.
        // Минимум 100ms samples гарантирует что ring содержит реальные декодированные данные.
        int recoveryPcmSamples = basePlan.PcmThresholdSamples > 0
            ? (int)(basePlan.PcmThresholdSamples * 1.5)
            : pipeline.SampleRate * pipeline.Channels * 100 / 1000;

        return new PlaybackWarmupPlan(
            Math.Max(recoveryPcmSamples, pipeline.Channels * 2),
            recoverySourceAheadMs,
            basePlan.WarmupTimeoutMs);
    }

    /// <summary>
    /// Обработчик starvation от pipeline.
    /// </summary>
    private void OnPipelineStarvation(AudioPipeline pipeline, int sessionId)
    {
        if (_disposed || _activePipeline != pipeline) return;
        if (_session.IsStale(sessionId)) return;

        _commandChannel.Writer.TryWrite(new StarvationCommand(sessionId, pipeline));
    }

    /// <summary>
    /// Обрабатывает фоновую ошибку внутри actor loop.
    /// </summary>
    private Task HandlePlayerErrorAsync(PlayerErrorCommand cmd)
    {
        if (IsPipelineCommandStale(cmd.Pipeline, cmd.SessionId))
            return Task.CompletedTask;

        if (CancellationHelper.IsCancellationLike(cmd.Error))
            return Task.CompletedTask;

        HandleError(cmd.Error);
        return Task.CompletedTask;
    }

    private void HandleError(Exception ex)
    {
        SetState(PlayerState.Error);

        string message = ex switch
        {
            AudioDeviceException or NAudio.MmException => GetDeviceErrorMessage(),
            CacheInvalidatedException => LocalizationService.Instance.Get(
                "Error_CacheInvalidated", "Track cache was deleted. Playback stopped."),
            _ => ex.Message
        };

        _events.RaiseError(new AudioPlayerError(message, ex));
    }

    private static string GetDeviceErrorMessage() =>
        LocalizationService.Instance.Get(
            "Error_NoAudioDevice",
            "Audio output device is not available. Please connect headphones or speakers.");

    /// <summary>
    /// Выполняет переход state machine.
    /// </summary>
    private bool SetState(PlayerState newState, [CallerMemberName] string? caller = null)
    {
        var oldState = _state;
        if (oldState == newState) return true;

        if (!PlayerStateTransitions.CanTransition(oldState, newState))
        {
            Log.Error($"[AudioPlayer] Invalid state transition: {oldState} -> {newState}. " +
                      $"Caller={caller}, Session={_session.Current}, SeekGeneration={Volatile.Read(ref _seekGeneration)}, " +
                      $"Intent={CurrentPlaybackIntent}");
            return false;
        }

        _state = newState;
        _events.RaiseStateChanged(MapState(newState));
        return true;
    }

    private static PlaybackState MapState(PlayerState state) => state switch
    {
        PlayerState.Idle => PlaybackState.Stopped,
        PlayerState.Loading => PlaybackState.Loading,
        PlayerState.Buffering => PlaybackState.Buffering,
        PlayerState.Playing => PlaybackState.Playing,
        PlayerState.Paused => PlaybackState.Paused,
        PlayerState.Seeking => PlaybackState.Playing,
        PlayerState.Error => PlaybackState.Error,
        PlayerState.Disposed => PlaybackState.Stopped,
        _ => PlaybackState.Stopped
    };

    private void SetPlaybackIntent(PlaybackIntent intent) =>
        Volatile.Write(ref _playbackIntent, (int)intent);

    private PlaybackIntent CurrentPlaybackIntent =>
        (PlaybackIntent)Volatile.Read(ref _playbackIntent);

    private Func<CancellationToken, Task<string?>>? CreateUrlAcquirer() =>
        GetOrCreateUrlCallback(
            _options.UrlAcquireCallback,
            ref _cachedUrlAcquirer,
            ref _cachedUrlAcquirerTrackId);

    private Func<CancellationToken, Task<string?>>? CreateUrlRefresher() =>
        GetOrCreateUrlCallback(
            _options.UrlRefreshCallback,
            ref _cachedUrlRefresher,
            ref _cachedUrlRefresherTrackId);

    /// <summary>
    /// Создаёт session-bound callback завершения трека.
    /// </summary>
    private Action CreateTrackEndedCallback(int sessionId)
    {
        return () =>
        {
            if (_disposed) return;
            _commandChannel.Writer.TryWrite(new TrackEndedCommand(sessionId));
        };
    }

    /// <summary>
    /// Создаёт session-bound callback фоновой ошибки.
    /// </summary>
    private Action<Exception> CreateErrorCallback(int sessionId, AudioPipeline pipeline)
    {
        return ex =>
        {
            if (_disposed) return;
            _commandChannel.Writer.TryWrite(new PlayerErrorCommand(sessionId, pipeline, ex));
        };
    }

    /// <summary>
    /// Возвращает кэшированный URL callback для текущего трека или создаёт новый.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private Func<CancellationToken, Task<string?>>? GetOrCreateUrlCallback(
        Func<string, CancellationToken, ValueTask<string?>>? optionCallback,
        ref Func<CancellationToken, Task<string?>>? cachedCallback,
        ref string? cachedTrackId)
    {
        if (optionCallback == null || string.IsNullOrEmpty(_currentTrackId))
            return null;

        string trackId = _currentTrackId;

        if (cachedCallback != null && cachedTrackId == trackId)
            return cachedCallback;

        cachedTrackId = trackId;
        cachedCallback = ct => optionCallback(trackId, ct).AsTask();
        return cachedCallback;
    }

    #endregion

    #region Statistics

    internal AudioPipeline? GetActivePipeline() => _activePipeline;

    public double BufferProgress => _activePipeline?.Source.BufferProgress ?? 0;

    public bool IsFullyBuffered => _activePipeline?.Source.IsFullyBuffered ?? false;

    public long GetDownloadedBytes()
    {
        var pipeline = _activePipeline;
        if (pipeline == null) return 0;

        return pipeline.Source switch
        {
            Sources.CachingStreamSource caching => caching.DownloadedBytes,
            Sources.LocalFileSource => pipeline.Source.IsFullyBuffered
                ? pipeline.StreamInfo.DurationMs * pipeline.StreamInfo.Bitrate / BitsPerByte
                : 0,
            _ => (long)(pipeline.Source.BufferProgress / 100.0
                * pipeline.StreamInfo.DurationMs * pipeline.StreamInfo.Bitrate / BitsPerByte)
        };
    }

    public IReadOnlyList<(double Start, double End)> GetBufferedRanges() =>
        _activePipeline?.Source.GetBufferedRanges() ?? [];

    #endregion

    #region Dispose

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        CancelActiveSeek();

        _commandChannel.Writer.TryWrite(new DisposeCommand(int.MaxValue));
        _lifetimeCts.Cancel();

        try
        {
            _commandProcessorTask.Wait(TimeSpan.FromSeconds(DisposeTaskTimeoutSec));
        }
        catch
        {
        }

        _sharedBackend.Dispose();
        _lifetimeCts.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        CancelActiveSeek();

        await _commandChannel.Writer.WriteAsync(new DisposeCommand(int.MaxValue))
            .ConfigureAwait(false);

        _commandChannel.Writer.TryComplete();

        try
        {
            await _commandProcessorTask
                .WaitAsync(TimeSpan.FromSeconds(DisposeTaskTimeoutSec))
                .ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            Log.Warn("[AudioPlayer] Command processor did not finish within dispose timeout");
        }
        catch
        {
        }

        _lifetimeCts.Cancel();

        _sharedBackend.Dispose();
        _lifetimeCts.Dispose();

        GC.SuppressFinalize(this);
    }

    #endregion
}