using LMP.Core.Audio.Http;

namespace LMP.Core.Services;

public sealed partial class AudioEngine
{
    #region Queue State

    private readonly List<TrackInfo> _queue = new(64);
    private IReadOnlyList<TrackInfo>? _queueSnapshot;
    private int _currentIndex = -1;
    private bool _queueMutatedByNavigation;

    public IReadOnlyList<TrackInfo> Queue
    {
        get
        {
            lock (_queueLock)
            {
                _queueSnapshot ??= [.. _queue];
                return _queueSnapshot;
            }
        }
    }

    #endregion

    #region Public API

    /// <summary>
    /// Добавляет в очередь диапазон треков, отфильтровывая дубликаты.
    /// Если очередь была пуста, автоматически запускает проигрывание первого добавленного трека.
    /// </summary>
    /// <param name="tracks">Список добавляемых треков.</param>
    /// <returns>Количество фактически добавленных уникальных треков.</returns>
    public int EnqueueRangeUnique(IEnumerable<TrackInfo> tracks)
    {
        int addedCount = 0;
        TrackInfo? playbackTrack = null;
        bool shouldAutoplay = false;

        lock (_queueLock)
        {
            var existingIds = _queue.Select(static t => t.Id).ToHashSet(StringComparer.Ordinal);
            var unique = new List<TrackInfo>();

            foreach (var track in tracks)
            {
                if (existingIds.Add(track.Id))
                {
                    unique.Add(track);
                    addedCount++;
                }
            }

            if (unique.Count > 0)
            {
                _queue.AddRange(unique);
                InvalidateQueueSnapshot();

                if (CurrentTrack == null && !IsPlaying && !IsLoading)
                {
                    _currentIndex = _queue.Count - unique.Count; // Позиционируемся на первый из новых
                    playbackTrack = _queue[_currentIndex];
                    shouldAutoplay = true;
                }
            }
        }

        if (addedCount > 0)
        {
            RaiseOnUI(() => OnQueueChanged?.Invoke());

            if (shouldAutoplay && playbackTrack != null)
            {
                ResetSealedFailedTrack();
                int session = BeginNewSession();
                _ = PlayTrackCoreAsync(playbackTrack, session, GetSessionToken());
            }
        }

        return addedCount;
    }

    public void Enqueue(TrackInfo track)
    {
        TrackInfo? playbackTrack = null;
        bool shouldAutoplay = false;

        lock (_queueLock)
        {
            if (_queue.Any(t => t.Id == track.Id)) return;
            _queue.Add(track);
            InvalidateQueueSnapshot();

            if (CurrentTrack == null && !IsPlaying && !IsLoading)
            {
                _currentIndex = _queue.Count - 1;
                playbackTrack = _queue[_currentIndex];
                shouldAutoplay = true;
            }
        }

        RaiseOnUI(() => OnQueueChanged?.Invoke());

        if (shouldAutoplay && playbackTrack != null)
        {
            ResetSealedFailedTrack();
            int session = BeginNewSession();
            _ = PlayTrackCoreAsync(playbackTrack, session, GetSessionToken());
        }
    }

    public void EnqueueRange(IEnumerable<TrackInfo> tracks)
    {
        lock (_queueLock) { _queue.AddRange(tracks); InvalidateQueueSnapshot(); }
        RaiseOnUI(() => OnQueueChanged?.Invoke());
    }

    public void ShuffleQueue()
    {
        lock (_queueLock)
        {
            if (_queue.Count < 2) return;
            ApplyShuffleInPlace(preserveCurrentAtStart: true);
        }
        RaiseOnUI(() => OnQueueChanged?.Invoke());
    }

    public void ClearQueue()
    {
        lock (_queueLock)
        {
            var current = CurrentTrack;
            _queue.Clear();
            _currentIndex = -1;
            if (current != null) { _queue.Add(current); _currentIndex = 0; }
            InvalidateQueueSnapshot();
        }
        RaiseOnUI(() => OnQueueChanged?.Invoke());
    }

    public void RemoveFromQueue(TrackInfo track)
    {
        bool needStop = false;
        lock (_queueLock)
        {
            int idx = _queue.FindIndex(t => t.Id == track.Id);
            if (idx == -1) return;
            if (idx == _currentIndex) { needStop = _queue.Count == 1; if (idx == _queue.Count - 1) _currentIndex--; }
            else if (idx < _currentIndex) _currentIndex--;
            _queue.RemoveAt(idx);
            InvalidateQueueSnapshot();
        }
        RaiseOnUI(() => OnQueueChanged?.Invoke());
        if (needStop) Stop();
    }

    public void MoveQueueItem(int from, int to)
    {
        lock (_queueLock)
        {
            if (from < 0 || from >= _queue.Count || to < 0 || to >= _queue.Count) return;
            var item = _queue[from];
            _queue.RemoveAt(from);
            _queue.Insert(to, item);
            if (_currentIndex == from) _currentIndex = to;
            else if (from < _currentIndex && to >= _currentIndex) _currentIndex--;
            else if (from > _currentIndex && to <= _currentIndex) _currentIndex++;
            InvalidateQueueSnapshot();
        }
        RaiseOnUI(() => OnQueueChanged?.Invoke());
    }

    #endregion

    #region Pre-warm

    /// <summary>
    /// Спекулятивно прогревает CDN-соединения для следующих треков в очереди.
    /// </summary>
    private void PreWarmNextTracksInQueue(int currentIndex, HttpClient httpClient, CancellationToken ct)
    {
        const int LookaheadCount = 2;

        var queue = Queue;

        for (int i = 1; i <= LookaheadCount; i++)
        {
            int idx = currentIndex + i;
            if (idx >= queue.Count)
                break;

            var next = queue[idx];

            if (AudioSourceFactory.FindAnyCachedTrack(next.Id) != null)
                continue;

            string? targetUrl = null;

            var manifest = SessionCacheStore.GetManifest(next.Id);
            if (manifest is { Variants.Count: > 0 })
            {
                targetUrl = manifest.Variants[0].Url;
            }
            else
            {
                string rawId = next.GetRawIdSpan().ToString();
                var hint = StreamSelectionHint.FromTrack(next, _library.Settings.RememberTrackFormat);
                var descriptor = _youtube.TryGetCachedStreamDescriptor(
                    rawId,
                    hint.Format,
                    hint.BitrateKbps);

                if (descriptor is { HasLiveUrl: true } d)
                    targetUrl = d.Url;
            }

            if (!string.IsNullOrEmpty(targetUrl))
            {
                CdnConnectionPreWarmer.PreWarmHost(httpClient, targetUrl, ct);
                Log.Debug($"[AudioEngine] Lookahead CDN prewarm queue[{idx}]: {next.Id}");
            }
        }
    }

    #endregion

    #region Navigation

    private bool TryMoveNext(bool userInitiated)
    {
        _queueMutatedByNavigation = false;
        if (_queue.Count == 0) return false;
        if (!userInitiated && RepeatMode == RepeatMode.One) return true;

        if (_currentIndex + 1 < _queue.Count) { _currentIndex++; return true; }

        if (RepeatMode == RepeatMode.All)
        {
            if (!userInitiated && _queue.Count == 1) return false;
            if (ShuffleEnabled && _queue.Count > 1)
            {
                ApplyShuffleInPlace(preserveCurrentAtStart: false);
                _queueMutatedByNavigation = true;
            }
            _currentIndex = 0;
            return true;
        }
        return false;
    }

    private bool TryMovePrevious()
    {
        if (_queue.Count == 0) return false;
        if (CurrentPosition.TotalSeconds > 3) return false;
        if (_currentIndex > 0) { _currentIndex--; return true; }
        if (RepeatMode == RepeatMode.All) { _currentIndex = _queue.Count - 1; return true; }
        return false;
    }

    private bool TryMoveNextSkippingTrack(string? skippedTrackId)
    {
        if (_queue.Count <= 1 || _currentIndex < 0 || _currentIndex >= _queue.Count) return false;
        for (int step = 1; step < _queue.Count; step++)
        {
            int idx = (_currentIndex + step) % _queue.Count;
            if (_queue[idx].Id != skippedTrackId) { _currentIndex = idx; return true; }
        }
        return false;
    }

    /// <summary>Fisher-Yates shuffle in-place.</summary>
    private void ApplyShuffleInPlace(bool preserveCurrentAtStart)
    {
        if (_queue.Count < 2) return;

        if (preserveCurrentAtStart && _currentIndex >= 0 && _currentIndex < _queue.Count)
        {
            if (_currentIndex != 0) (_queue[0], _queue[_currentIndex]) = (_queue[_currentIndex], _queue[0]);
            for (int n = _queue.Count - 1; n > 1; n--)
            {
                int k = 1 + Random.Shared.Next(n);
                (_queue[k], _queue[n]) = (_queue[n], _queue[k]);
            }
            _currentIndex = 0;
        }
        else
        {
            for (int n = _queue.Count - 1; n > 0; n--)
            {
                int k = Random.Shared.Next(n + 1);
                (_queue[k], _queue[n]) = (_queue[n], _queue[k]);
            }
        }

        InvalidateQueueSnapshot();
    }

    private void InvalidateQueueSnapshot() => _queueSnapshot = null;

    #endregion
}