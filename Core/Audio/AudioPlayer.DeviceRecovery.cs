using LMP.Core.Audio.Interfaces;
using LMP.Core.Exceptions;

namespace LMP.Core.Audio;

/// <summary>
/// Восстановление playback после потери аудиоустройства.
/// </summary>
public sealed partial class AudioPlayer
{
    private const int PostDeviceRecoveryStabilizationMs = 100;

    /// <summary>
    /// Callback потери устройства от pipeline.
    /// </summary>
    private void OnPipelineDeviceLost(AudioPipeline pipeline, int sessionId)
    {
        if (_disposed || _activePipeline != pipeline) return;
        if (_session.IsStale(sessionId)) return;

        CancelActiveSeek();
        _commandChannel.Writer.TryWrite(new DeviceLostCommand(sessionId, pipeline));
    }

    /// <summary>
    /// Callback появления устройства от pipeline.
    /// </summary>
    private void OnPipelineDeviceAvailable(AudioPipeline pipeline, int sessionId)
    {
        if (_disposed || _activePipeline != pipeline) return;
        if (_session.IsStale(sessionId)) return;

        _commandChannel.Writer.TryWrite(new DeviceAvailableCommand(sessionId, pipeline));
    }

    /// <summary>
    /// Обрабатывает потерю устройства в actor loop.
    /// </summary>
    private Task HandleDeviceLostAsync(DeviceLostCommand cmd)
    {
        if (IsPipelineCommandStale(cmd.Pipeline, cmd.SessionId))
            return Task.CompletedTask;

        CancelActiveSeek();
        StopTimers();

        Log.Warn("[AudioPlayer] Device lost — actor paused playback");
        SetState(PlayerState.Paused);
        _events.RaiseDeviceLost();

        return Task.CompletedTask;
    }

    /// <summary>
    /// Обрабатывает появление устройства в actor loop.
    /// </summary>
    private async Task HandleDeviceAvailableAsync(DeviceAvailableCommand cmd)
    {
        if (IsPipelineCommandStale(cmd.Pipeline, cmd.SessionId))
            return;

        if (CurrentPlaybackIntent != PlaybackIntent.Play)
        {
            Log.Debug($"[AudioPlayer] Device available ignored due to intent={CurrentPlaybackIntent}");
            return;
        }

        if (_state is PlayerState.Idle or PlayerState.Disposed)
            return;

        Log.Info("[AudioPlayer] Device available — initiating actor recovery");
        SetState(PlayerState.Buffering);

        await HandleDeviceRecoveryAsync(new DeviceRecoveryCommand(cmd.SessionId)).ConfigureAwait(false);
    }

    /// <summary>
    /// Восстанавливает playback после потери устройства.
    /// </summary>
    private async Task HandleDeviceRecoveryAsync(DeviceRecoveryCommand cmd)
    {
        var pipeline = _activePipeline;
        if (pipeline == null || !pipeline.IsDeviceLost)
        {
            if (_state == PlayerState.Buffering && CurrentPlaybackIntent != PlaybackIntent.Play)
                SetState(PlayerState.Paused);

            return;
        }

        try
        {
            await pipeline.RecoverFromDeviceLossAsync(
                CreateUrlRefresher(),
                _options,
                CreateTrackEndedCallback(cmd.SessionId),
                CreateErrorCallback(cmd.SessionId, pipeline),
                _lifetimeCts.Token).ConfigureAwait(false);

            if (_session.IsStale(cmd.SessionId) || _activePipeline != pipeline)
                return;

            _lifetimeCts.Token.ThrowIfCancellationRequested();

            if (CurrentPlaybackIntent != PlaybackIntent.Play)
            {
                SetState(PlayerState.Paused);
                return;
            }

            await Task.Delay(PostDeviceRecoveryStabilizationMs, _lifetimeCts.Token).ConfigureAwait(false);

            if (_activePipeline != pipeline || _lifetimeCts.Token.IsCancellationRequested)
                return;

            var warmupPlan = ComputePlaybackWarmupPlan(pipeline, isSeek: false);

            bool pcmReady = await pipeline.WaitForBufferAsync(
                warmupPlan.PcmThresholdSamples,
                warmupPlan.WarmupTimeoutMs,
                _lifetimeCts.Token).ConfigureAwait(false);

            bool sourceReady = IsSourceReadyForResume(pipeline, warmupPlan.SourceAheadMs);

            if (_activePipeline != pipeline || _lifetimeCts.Token.IsCancellationRequested)
                return;

            if (CurrentPlaybackIntent != PlaybackIntent.Play)
            {
                SetState(PlayerState.Paused);
                return;
            }

            SetState(PlayerState.Buffering);
            ResumeOrDefer(pipeline, pcmReady, sourceReady, warmupPlan, cmd.SessionId,
                startTimers: true, configurePipeline: true,
                trackId: _currentTrackId, logContext: "Device recovery warmup");

            Log.Info("[AudioPlayer] Device recovery complete");
            _events.RaiseDeviceRestored();
        }
        catch (AudioDeviceException ex)
        {
            SetState(PlayerState.Error);
            _events.RaiseError(new AudioPlayerError(GetDeviceErrorMessage(), ex));
        }
        catch (OperationCanceledException)
        {
            if (_state == PlayerState.Buffering && CurrentPlaybackIntent != PlaybackIntent.Play)
                SetState(PlayerState.Paused);
        }
        catch (Exception ex)
        {
            SetState(PlayerState.Error);
            _events.RaiseError(new AudioPlayerError(ex.Message, ex));
        }
    }
}