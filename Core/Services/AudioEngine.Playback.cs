using LMP.Core.Audio.Helpers;
using LMP.Core.Audio.Http;
using LMP.Core.Audio.Normalization;
using LMP.Core.Exceptions;
using ReactiveUI;

namespace LMP.Core.Services;

public sealed partial class AudioEngine
{
    #region Command Loop

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

    #region Navigation & Queue Handlers

    private async Task HandlePlayTrackAsync(PlayTrackCommand cmd)
    {
        if (_session.IsStale(cmd.Session)) return;

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

    #region Playback Core

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

            if (!startPlaying)
            {
                SetManualLoading(false);
                return;
            }

            ct.ThrowIfCancellationRequested();
            Volatile.Write(ref _nTokenActiveTrackId, track.Id);
            Volatile.Write(ref _nTokenWarnedTrackId, null);

            AudioSourceFactory.PreWarmCdnConnections(
                SharedHttpClient.Instance, _lifetimeCts.Token);

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

                    PreWarmNextTracksInQueue(CurrentQueueIndex, SharedHttpClient.Instance, _lifetimeCts.Token);
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

    #endregion

    #region Player Event Handlers

    /// <summary>
    /// Подписывается на события низкоуровневого плеера (<see cref="AudioPlayer"/>).
    /// </summary>
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
}