using LMP.Core.Audio.Interfaces;
using LMP.Core.Audio.Sources;
using LMP.Core.Exceptions;
using static LMP.Core.Audio.AudioConstants;

namespace LMP.Core.Audio;

public sealed partial class AudioPlayer
{

    #region Seek Constants
    private const int SeekLoopMaxIterations = 50;
    private const int ReSeekDecoderStopTimeoutMs = 200;
    private const int SourceSeekTimeoutMs = 500;
    private const int DeferredSeekResumeTimeoutMs = 30_000;

    #endregion

    #region Seek State

    /// <summary>CTS текущего seek.</summary>
    private CancellationTokenSource? _activeSeekCts;

    /// <summary>CTS текущего deferred-resume после seek.</summary>
    private CancellationTokenSource? _deferredResumeCts;

    /// <summary>Pending seek position для latest-wins coalescing.</summary>
    private long _pendingSeekMs = -1;

    /// <summary>Признак активного seek.</summary>
    private volatile bool _backgroundSeekActive;

    /// <summary>Pipeline, связанный с текущим seek.</summary>
    private AudioPipeline? _backgroundSeekPipeline;

#if DEBUG
    private int _seekRestartCount;
    private int _decoderRestartCount;
#endif

    #endregion

    #region Public Seek API

    /// <summary>
    /// Инициирует seek с latest-wins coalescing.
    /// </summary>
    public ValueTask SeekAsync(TimeSpan position, CancellationToken ct = default)
    {
        if (_disposed || _state is not (PlayerState.Playing or PlayerState.Paused or PlayerState.Buffering))
            return ValueTask.CompletedTask;

        var pipeline = _activePipeline;
        if (pipeline == null || !pipeline.Source.CanSeek)
            return ValueTask.CompletedTask;

        pipeline.Source.SetPlaybackActive(false);

        long targetMs = (long)position.TotalMilliseconds;

        if (_backgroundSeekActive || Interlocked.Read(ref _pendingSeekMs) >= 0)
        {
            Volatile.Write(ref _pendingSeekMs, targetMs);
            return ValueTask.CompletedTask;
        }

        CancelActiveSeek();

        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        ct.Register(() => tcs.TrySetCanceled(ct));

        int seekGeneration = Interlocked.Increment(ref _seekGeneration);

        Volatile.Write(ref _pendingSeekMs, targetMs);
        _commandChannel.Writer.TryWrite(new SeekCommand(position, _session.Current, seekGeneration, tcs));

        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Асинхронно отменяет текущий seek и deferred-resume.
    /// </summary>
    private void CancelActiveSeek()
    {
        ResetSeekState();

        var deferredResumeCts = Interlocked.Exchange(ref _deferredResumeCts, null);
        CancelCtsAsync(deferredResumeCts);

        var cts = Interlocked.Exchange(ref _activeSeekCts, null);
        if (cts == null) return;

#if DEBUG
        int restartCount = Interlocked.Increment(ref _seekRestartCount);
        if (restartCount % 5 == 0)
            Log.Warn($"[AudioPlayer] Seek restart storm: {restartCount} cancellations on current track");
#endif

        CancelCtsAsync(cts);
    }

    private static void CancelCtsAsync(CancellationTokenSource? cts)
    {
        if (cts == null) return;

        ThreadPool.UnsafeQueueUserWorkItem(static state =>
        {
            try { ((CancellationTokenSource)state!).Cancel(); }
            catch (ObjectDisposedException) { }
        }, cts);
    }

    /// <summary>Сбрасывает seek observability counters.</summary>
    [System.Diagnostics.Conditional("DEBUG")]
    internal void ResetPerTrackCounters()
    {
#if DEBUG
        Volatile.Write(ref _seekRestartCount, 0);
        Volatile.Write(ref _decoderRestartCount, 0);
#endif
    }

    #endregion

    #region Seek Command Handler

    /// <summary>
    /// Обрабатывает seek-команду.
    /// </summary>
    private async Task HandleSeekAsync(SeekCommand cmd)
    {
        var pipeline = _activePipeline;
        if (pipeline == null || !pipeline.Source.CanSeek)
        {
            cmd.Completion?.TrySetResult(false);
            return;
        }

        long latestTargetMs = DrainPendingSeekMs();
        long posMs = latestTargetMs >= 0 ? latestTargetMs : (long)cmd.Position.TotalMilliseconds;

        bool wasPlaying = _state is PlayerState.Playing or PlayerState.Buffering;
        SetState(PlayerState.Seeking);
        StopPositionTimer();
        _lastRawPlayedSamples = -1;

        try
        {
            if (pipeline.Source is CachingStreamSource cachingSource)
                _ = cachingSource.TryPrefetchChunkForSeekAsync(posMs, _lifetimeCts.Token);

            await pipeline.StopDecodingAsync(
                TimeSpan.FromMilliseconds(DecoderStopTimeoutSeekMs)).ConfigureAwait(false);

            if (_session.IsStale(cmd.SessionId))
            {
                cmd.Completion?.TrySetCanceled();
                StartPositionTimerDelayed();
                return;
            }

            pipeline.Stop();
            pipeline.Flush();
            pipeline.PrepareForSeek(posMs);

            Volatile.Write(ref _backgroundSeekPipeline, pipeline);
            _backgroundSeekActive = true;

            var seekCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token);
            var oldCts = Interlocked.Exchange(ref _activeSeekCts, seekCts);
            if (oldCts != null)
            {
                try { oldCts.Cancel(); }
                catch (ObjectDisposedException) { }
            }

            await CompleteSeekWithCoalescingAsync(
                pipeline, cmd, posMs, wasPlaying, cmd.SessionId, seekCts)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            cmd.Completion?.TrySetCanceled();
            StartPositionTimerDelayed();
        }
        catch (AudioDeviceException ex)
        {
            cmd.Completion?.TrySetException(ex);
            HandleError(ex);
            StartPositionTimerDelayed();
        }
        catch (Exception ex)
        {
            cmd.Completion?.TrySetException(ex);
            SetState(wasPlaying ? PlayerState.Playing : PlayerState.Paused);

            if (wasPlaying)
            {
                try { pipeline.Start(); }
                catch (AudioDeviceException devEx) { HandleError(devEx); return; }
            }

            StartPositionTimerDelayed();
        }
    }

    #endregion

    #region Seek Coalescing Loop

    /// <summary>
    /// Выполняет background-фазу seek с coalescing.
    /// </summary>
    private async Task CompleteSeekWithCoalescingAsync(
        AudioPipeline pipeline,
        SeekCommand cmd,
        long initialPosMs,
        bool wasPlaying,
        int sessionAtStart,
        CancellationTokenSource seekCts)
    {
        var seekCt = seekCts.Token;
        bool decoderStarted = false;
        var totalSw = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            long currentTargetMs = initialPosMs;
            int iteration = 0;

            while (iteration++ < SeekLoopMaxIterations)
            {
                var phaseSw = System.Diagnostics.Stopwatch.StartNew();

                try
                {
                    var sourceSeekTask = pipeline.Source
                        .SeekAsync(currentTargetMs, seekCt).AsTask();

                    var completed = await Task.WhenAny(
                        sourceSeekTask,
                        Task.Delay(SourceSeekTimeoutMs, seekCt)).ConfigureAwait(false);

                    if (completed == sourceSeekTask)
                    {
                        await sourceSeekTask.ConfigureAwait(false);
                    }
                    else
                    {
                        long drained = DrainPendingSeekMs();
                        if (drained >= 0)
                        {
                            currentTargetMs = drained;
                            pipeline.Flush();
                            pipeline.PrepareForSeek(currentTargetMs);
                            Log.Debug($"[SeekTelemetry] Phase A timed out, coalescing to {drained}ms");
                            continue;
                        }

                        await sourceSeekTask.ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException)
                {
                    Log.Debug($"[SeekTelemetry] Phase A cancelled after {phaseSw.ElapsedMilliseconds}ms");
                    throw;
                }

                long phaseAMs = phaseSw.ElapsedMilliseconds;
                phaseSw.Restart();

                if (!ValidateSeekState(seekCt, pipeline, sessionAtStart))
                {
                    cmd.Completion?.TrySetCanceled();
                    return;
                }

                UpdateDecodedPosition(pipeline, currentTargetMs);

                {
                    long drained = DrainPendingSeekMs();
                    if (drained >= 0)
                    {
                        currentTargetMs = drained;
                        pipeline.Flush();
                        pipeline.PrepareForSeek(currentTargetMs);
                        Log.Debug($"[SeekTelemetry] Phase B: Coalesced to {drained}ms");
                        continue;
                    }
                }

                if (!ValidateSeekState(seekCt, pipeline, sessionAtStart))
                {
                    cmd.Completion?.TrySetCanceled();
                    return;
                }

                pipeline.StartDecoding(
                    CreateUrlRefresher(),
                    _options,
                    CreateTrackEndedCallback(cmd.SessionId),
                    CreateErrorCallback(cmd.SessionId, pipeline));
                decoderStarted = true;

                long phaseDMs = phaseSw.ElapsedMilliseconds;
                phaseSw.Restart();

                {
                    long drained = DrainPendingSeekMs();
                    if (drained >= 0)
                    {
                        currentTargetMs = drained;
                        await StopDecoderForReseekAsync(pipeline, seekCt).ConfigureAwait(false);
                        decoderStarted = false;
                        pipeline.PrepareForSeek(currentTargetMs);
                        Log.Debug($"[SeekTelemetry] Phase D interrupted, re-seeking to {drained}ms");
                        continue;
                    }
                }

                long phaseEMs = 0;

                if (wasPlaying && _state != PlayerState.Paused)
                {
                    var (seekThreshold, sourceAheadMs, warmupTimeout) =
                        ComputeSeekWarmupParams(pipeline);

                    bool warmupReady = seekThreshold <= 0;

                    if (seekThreshold > 0 && warmupTimeout > 0)
                    {
                        try
                        {
                            warmupReady = await pipeline.WaitForBufferAsync(
                                seekThreshold, warmupTimeout, seekCt).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) { throw; }
                        catch
                        {
                        }
                    }

                    phaseEMs = phaseSw.ElapsedMilliseconds;
                    phaseSw.Restart();

                    {
                        long drained = DrainPendingSeekMs();
                        if (drained >= 0)
                        {
                            currentTargetMs = drained;
                            await StopDecoderForReseekAsync(pipeline, seekCt).ConfigureAwait(false);
                            decoderStarted = false;
                            pipeline.PrepareForSeek(currentTargetMs);
                            Log.Debug($"[SeekTelemetry] Phase E interrupted, re-seeking to {drained}ms");
                            continue;
                        }
                    }

                    if (!ValidateSeekState(seekCt, pipeline, sessionAtStart))
                    {
                        cmd.Completion?.TrySetCanceled();
                        return;
                    }

                    bool pcmReady = seekThreshold <= 0
                        || warmupReady
                        || pipeline.BufferedSamples >= seekThreshold;

                    bool sourceReady = IsSourceReadyForResume(pipeline, sourceAheadMs);

                    var warmupPlan = new PlaybackWarmupPlan(seekThreshold, sourceAheadMs, warmupTimeout);

                    if (pcmReady && sourceReady || pipeline.Source.IsFullyBuffered)
                    {
                        ResumePlaybackSequence(
                            pipeline,
                            startTimers: false,
                            configurePipeline: true,
                            trackId: _currentTrackId);
                    }
                    else
                    {
                        Log.Warn($"[SeekTelemetry] Buffer warmup incomplete " +
                                 $"(ring={pipeline.BufferedSamples}/{seekThreshold}, " +
                                 $"ahead={GetSourceBufferedAheadMs(pipeline)}ms/{sourceAheadMs}ms). " +
                                 "Entering Buffering state. Playback will auto-resume.");

                        _options.OnPipelineConfiguring?.Invoke(pipeline, _currentTrackId);
                        SetState(PlayerState.Buffering);
                        LaunchDeferredResume(pipeline, warmupPlan, sessionAtStart);
                    }
                }
                else
                {
                    SetState(PlayerState.Paused);
                }

                Log.Info($"[SeekTelemetry] Seek to {currentTargetMs}ms COMPLETED. " +
                         $"Total: {totalSw.ElapsedMilliseconds}ms | " +
                         $"A: {phaseAMs}ms | D: {phaseDMs}ms | E: {phaseEMs}ms");

                StartPositionTimerDelayed();
                _events.RaiseSeekCompleted(TimeSpan.FromMilliseconds(currentTargetMs));
                cmd.Completion?.TrySetResult(true);
                return;
            }

            Log.Warn($"[SeekTelemetry] Seek coalescing loop exhausted ({SeekLoopMaxIterations} iterations)");

            if (decoderStarted && wasPlaying && _state != PlayerState.Paused)
            {
                ResumePlaybackSequence(
                    pipeline,
                    startTimers: false,
                    configurePipeline: true,
                    trackId: _currentTrackId);
            }

            StartPositionTimerDelayed();
            _events.RaiseSeekCompleted(TimeSpan.FromMilliseconds(initialPosMs));
            cmd.Completion?.TrySetResult(true);
        }
        catch (OperationCanceledException)
        {
            Log.Debug($"[SeekTelemetry] Seek to {initialPosMs}ms cancelled. " +
                      $"Elapsed: {totalSw.ElapsedMilliseconds}ms");
            cmd.Completion?.TrySetCanceled();
            StartPositionTimerDelayed();
        }
        catch (AudioDeviceException ex)
        {
            cmd.Completion?.TrySetException(ex);
            HandleError(ex);
            StartPositionTimerDelayed();
        }
        catch (Exception ex)
        {
            cmd.Completion?.TrySetException(ex);
            SetState(wasPlaying ? PlayerState.Playing : PlayerState.Paused);

            if (wasPlaying && _activePipeline == pipeline)
            {
                try { pipeline.Start(); }
                catch (AudioDeviceException devEx) { HandleError(devEx); return; }
            }

            StartPositionTimerDelayed();
        }
        finally
        {
            if (ReferenceEquals(Volatile.Read(ref _backgroundSeekPipeline), pipeline))
                ResetSeekState();

            Interlocked.CompareExchange(ref _activeSeekCts, null, seekCts);

            try { seekCts.Dispose(); }
            catch (ObjectDisposedException) { }
        }
    }

    #endregion

    #region Deferred Seek Resume

    /// <summary>
    /// Фоново ждёт готовности PCM и source-ahead после deferred seek/rebuffer.
    /// </summary>
    private async Task AwaitDeferredSeekBufferAndResumeAsync(
        AudioPipeline pipeline,
        int seekThreshold,
        int sourceAheadMs,
        int sessionId,
        int seekGeneration,
        CancellationTokenSource deferredResumeCts)
    {
        var ct = deferredResumeCts.Token;
        var waitLogSw = System.Diagnostics.Stopwatch.StartNew();
        long nextProgressLogMs = DeferredSeekResumeTimeoutMs;

        try
        {
            while (!ct.IsCancellationRequested && !_disposed)
            {
                bool pcmReady = seekThreshold <= 0 || pipeline.BufferedSamples >= seekThreshold;
                bool sourceReady = IsSourceReadyForResume(pipeline, sourceAheadMs);

                if (!pcmReady && seekThreshold > 0)
                {
                    int waitSliceMs = sourceAheadMs >= 6000 ? 1000 : 400;
                    bool signaled = await pipeline.WaitForBufferAsync(seekThreshold, waitSliceMs, ct)
                        .ConfigureAwait(false);

                    pcmReady = signaled || pipeline.BufferedSamples >= seekThreshold;
                    sourceReady = IsSourceReadyForResume(pipeline, sourceAheadMs);
                }

                if (pcmReady && sourceReady)
                {
                    int ringBufferSamples = pipeline.BufferedSamples;

                    if (!_commandChannel.Writer.TryWrite(new DeferredResumeCommand(
                            sessionId,
                            seekGeneration,
                            pipeline,
                            ThresholdReached: true,
                            BufferedSamples: ringBufferSamples)))
                    {
                        Log.Debug("[SeekTelemetry] Deferred resume command dropped: channel unavailable");
                    }

                    return;
                }

                if (waitLogSw.ElapsedMilliseconds >= nextProgressLogMs)
                {
                    Log.Warn($"[SeekTelemetry] Deferred warmup still waiting " +
                             $"(ring={pipeline.BufferedSamples}/{seekThreshold}, " +
                             $"ahead={GetSourceBufferedAheadMs(pipeline)}ms/{sourceAheadMs}ms, " +
                             $"elapsed={waitLogSw.ElapsedMilliseconds}ms)");

                    nextProgressLogMs += DeferredSeekResumeTimeoutMs;
                }

                await Task.Delay(100, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Log.Warn($"[SeekTelemetry] Deferred seek waiter error: {ex.Message}");
        }
        finally
        {
            Interlocked.CompareExchange(ref _deferredResumeCts, null, deferredResumeCts);
            try { deferredResumeCts.Dispose(); } catch (ObjectDisposedException) { }
        }
    }

    /// <summary>
    /// Возобновляет воспроизведение после deferred buffering внутри actor loop.
    /// </summary>
    private Task HandleDeferredResumeAsync(DeferredResumeCommand cmd)
    {
        int currentSeekGeneration = Volatile.Read(ref _seekGeneration);

        if (_disposed
            || _activePipeline != cmd.Pipeline
            || _session.IsStale(cmd.SessionId)
            || cmd.SeekGeneration != currentSeekGeneration)
        {
            Log.Debug($"[SeekTelemetry] Deferred resume dropped: " +
                      $"disposed={_disposed}, pipelineMatch={_activePipeline == cmd.Pipeline}, " +
                      $"stale={_session.IsStale(cmd.SessionId)}, " +
                      $"generationMatch={cmd.SeekGeneration == currentSeekGeneration}, " +
                      $"state={_state}");
            return Task.CompletedTask;
        }

        if (CurrentPlaybackIntent != PlaybackIntent.Play)
        {
            Log.Debug($"[SeekTelemetry] Deferred resume ignored due to intent={CurrentPlaybackIntent}");
            return Task.CompletedTask;
        }

        if (_state is PlayerState.Idle or PlayerState.Disposed or PlayerState.Error)
        {
            Log.Debug($"[SeekTelemetry] Deferred resume ignored in terminal state={_state}");
            return Task.CompletedTask;
        }

        if (!cmd.ThresholdReached)
        {
            Log.Warn("[SeekTelemetry] Deferred resume ignored because adaptive readiness threshold was not reached.");
            return Task.CompletedTask;
        }

        if (cmd.Pipeline.IsDeviceLost)
        {
            SetState(PlayerState.Buffering);
            _commandChannel.Writer.TryWrite(new DeviceRecoveryCommand(cmd.SessionId));
            return Task.CompletedTask;
        }

        Log.Debug($"[SeekTelemetry] Deferred warmup complete (ring={cmd.BufferedSamples}). Resuming playback.");

        ResumePlaybackSequence(
            cmd.Pipeline,
            startTimers: true,
            configurePipeline: false,
            trackId: _currentTrackId);

        Log.Info("[SeekTelemetry] Deferred seek buffer ready. Playback resumed automatically.");
        return Task.CompletedTask;
    }

    #endregion

    #region Seek Helpers

    /// <summary>
    /// Вычисляет adaptive параметры прогрева для seek.
    /// </summary>
    private static (int seekThreshold, int sourceAheadMs, int warmupTimeout) ComputeSeekWarmupParams(
        AudioPipeline pipeline)
    {
        var plan = ComputePlaybackWarmupPlan(pipeline, isSeek: true);
        return (plan.PcmThresholdSamples, plan.SourceAheadMs, plan.WarmupTimeoutMs);
    }

    /// <summary>Атомарно забирает pending seek позицию.</summary>
    private long DrainPendingSeekMs() =>
        Interlocked.Exchange(ref _pendingSeekMs, -1);

    /// <summary>
    /// Останавливает decoder и очищает буферы перед re-seek.
    /// </summary>
    private static async Task StopDecoderForReseekAsync(AudioPipeline pipeline, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        await pipeline.StopDecodingAsync(
            TimeSpan.FromMilliseconds(ReSeekDecoderStopTimeoutMs)).ConfigureAwait(false);

        pipeline.Stop();
        pipeline.Flush();
    }

    /// <summary>Проверяет валидность seek-контекста.</summary>
    private bool ValidateSeekState(CancellationToken ct, AudioPipeline pipeline, int sessionId)
    {
        return !ct.IsCancellationRequested
            && !_disposed
            && _activePipeline == pipeline
            && !_session.IsStale(sessionId);
    }

    /// <summary>Обновляет позицию decoded samples после seek.</summary>
    private static void UpdateDecodedPosition(AudioPipeline pipeline, long posMs)
    {
        long targetSamples = (long)(posMs / 1000.0 * pipeline.SampleRate * pipeline.Channels);
        pipeline.SetDecodedSamplesPosition(targetSamples);
    }

    /// <summary>Сбрасывает состояние coalescing seek.</summary>
    private void ResetSeekState()
    {
        Volatile.Write(ref _pendingSeekMs, -1);
        Volatile.Write(ref _backgroundSeekPipeline, null);
        _backgroundSeekActive = false;
    }

    #endregion
}