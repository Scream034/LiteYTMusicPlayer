using LMP.Core.Audio.Http;
using LMP.Core.Exceptions;

namespace LMP.Core.Services;

public sealed partial class AudioEngine
{
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
    public void OnResume(SuspendLevel previousLevel)
    {
        _isSuspended = false;
        ApplyLifecycleSourceSuspendPolicy();

        AudioSourceFactory.PreWarmCdnConnections(
            Audio.Http.SharedHttpClient.Instance, _lifetimeCts.Token);
    }

    #endregion

    #region Source Lifecycle Policy

    private bool ShouldKeepSourceActiveWhileSuspended()
    {
        var playbackState = _player.State;
        var detailedState = _player.DetailedState;

        return playbackState is PlaybackState.Loading
            or PlaybackState.Buffering
            or PlaybackState.Playing
            || detailedState == PlayerState.Seeking;
    }

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

    #region Error Handling

    private void HandleCacheInvalidated(CacheInvalidatedException cacheEx)
    {
        var trackId = cacheEx.TrackId ?? CurrentTrack?.Id;

        if (cacheEx.IsRecoverable && _cacheRetryCount < MaxCacheAutoRetries)
        {
            int retryNumber = Interlocked.Increment(ref _cacheRetryCount);
            var resumePosition = CurrentPosition;
            var track = CurrentTrack;

            Log.Info($"[AudioEngine] Cache auto-retry #{retryNumber}/{MaxCacheAutoRetries}: track={trackId}, kind={cacheEx.Kind}, pos={resumePosition}");

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
                Log.Info($"[AudioEngine] Surgical patch in progress. Preserving existing cache file for: {trackId}");
            }

            if (track != null)
            {
                ResetSealedFailedTrack();
                int session = BeginNewSession();
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

    #region IDisposable & IAsyncDisposable Implementation

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

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        _youtube.OnNTokenDecryptionStarted -= HandleNTokenDecryptionStarted;
        CdnConnectionPreWarmer.OnTunnelDeadDetected -= HandleCdnTunnelDead;
        lock (_sessionLock) { _sessionCts?.Cancel(); _sessionCts?.Dispose(); }

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

        _commandQueue.Writer.TryComplete();
        _lifetimeCts.Cancel();

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

        await _player.DisposeAsync().ConfigureAwait(false);
        _lifetimeCts.Dispose();

        GC.SuppressFinalize(this);
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    #endregion
}