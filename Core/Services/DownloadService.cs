namespace LMP.Core.Services;

public sealed class DownloadService
{
    private readonly YoutubeProvider _youtube;
    private readonly LibraryService _library;

    private readonly Dictionary<string, DownloadTask> _activeTasks = [];
    private readonly Lock _lock = new();
    private readonly SemaphoreSlim _downloadSemaphore = new(3);

    public event Action<string, float>? OnProgress;
    public event Action<string, bool, string?>? OnCompleted;

    public DownloadService(YoutubeProvider youtube, LibraryService library)
    {
        _youtube = youtube;
        _library = library;
    }

    public bool IsDownloading(string trackId)
    {
        lock (_lock)
        {
            return _activeTasks.ContainsKey(trackId);
        }
    }

    public float GetProgress(string trackId)
    {
        lock (_lock)
        {
            return _activeTasks.TryGetValue(trackId, out var task) ? task.Progress : 0f;
        }
    }

    public int ActiveDownloadsCount
    {
        get
        {
            lock (_lock)
            {
                return _activeTasks.Count;
            }
        }
    }

    /// <summary>
    /// Запускает процесс загрузки трека.
    /// <list type="bullet">
    ///   <item>Если трек полностью закэширован — мгновенный экспорт без сети.</item>
    ///   <item>Если трек частично закэширован — докачивает только недостающие gaps.</item>
    ///   <item>Если кэша нет — скачивает целиком через кэш-механизм и экспортирует.</item>
    /// </list>
    /// </summary>
    /// <param name="track">Объект информации о треке.</param>
    public void StartDownload(TrackInfo track)
    {
        lock (_lock)
        {
            if (_activeTasks.ContainsKey(track.Id) || track.IsDownloaded)
                return;
        }

        var cache = Audio.AudioSourceFactory.GlobalCache;
        if (cache == null) return;

        // Path 1: Already fully cached → instant export (0 network bytes)
        if (cache.IsTrackFullyCached(track.Id))
        {
            lock (_lock)
            {
                _activeTasks[track.Id] = new DownloadTask { Progress = 0f };
            }

            _ = RunInstantExportAsync(track, cache);
            return;
        }

        // Path 2: Partial or no cache → gap-fill + export
        EnqueueGapFillDownload(track);
    }

    /// <summary>
    /// Мгновенный экспорт полностью закэшированного трека в Downloads.
    /// </summary>
    private async Task RunInstantExportAsync(TrackInfo track, Audio.Cache.AudioCacheManager cache)
    {
        try
        {
            OnProgress?.Invoke(track.Id, 0f);

            bool success = await cache.ExportTrackToDownloadsAsync(
                track.Id,
                async id => await _library.GetTrackAsync(id).ConfigureAwait(false),
                async t => await _library.AddOrUpdateTrackAsync(t).ConfigureAwait(false),
                CancellationToken.None).ConfigureAwait(false);

            if (success)
            {
                var updatedTrack = await _library.GetTrackAsync(track.Id).ConfigureAwait(false);
                if (updatedTrack != null)
                {
                    track.IsDownloaded = true;
                    track.LocalPath = updatedTrack.LocalPath;
                    OnProgress?.Invoke(track.Id, 1.0f);
                    OnCompleted?.Invoke(track.Id, true, track.LocalPath);
                    return;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"[DownloadService] Instant export failed for {track.Id}: {ex.Message}. Falling back to gap-fill.");
        }
        finally
        {
            lock (_lock)
            {
                _activeTasks.Remove(track.Id);
            }
        }

        // Если мгновенный экспорт сорвался — fallback на gap-fill
        EnqueueGapFillDownload(track);
    }

    /// <summary>
    /// Помещает задачу загрузки в очередь с ограничением параллелизма.
    /// Использует gap-fill: скачивает только недостающие ranges через HTTP range requests.
    /// </summary>
    private void EnqueueGapFillDownload(TrackInfo track)
    {
        lock (_lock)
        {
            if (_activeTasks.ContainsKey(track.Id) || track.IsDownloaded)
                return;

            var cts = new CancellationTokenSource();
            _activeTasks[track.Id] = new DownloadTask { Progress = 0f, CancellationSource = cts };
        }

        _ = RunGapFillDownloadAsync(track);
    }

    /// <summary>
    /// Выполняет gap-fill загрузку: resolve URL → скачать gaps → экспорт в Downloads.
    /// </summary>
    private async Task RunGapFillDownloadAsync(TrackInfo track)
    {
        await _downloadSemaphore.WaitAsync().ConfigureAwait(false);

        try
        {
            CancellationToken ct;
            lock (_lock)
            {
                if (!_activeTasks.TryGetValue(track.Id, out var task))
                    return;
                ct = task.CancellationSource.Token;
            }

            // Step 1: Resolve stream URL (uses session cache → 0 API calls for recently played tracks)
            var descriptor = await _youtube.RefreshStreamAsync(track, false, ct).ConfigureAwait(false);
            if (descriptor == null || !descriptor.Value.HasLiveUrl)
            {
                Log.Warn($"[DownloadService] No live URL for {track.Id}");
                OnCompleted?.Invoke(track.Id, false, null);
                return;
            }

            var cache = Audio.AudioSourceFactory.GlobalCache;
            if (cache == null)
            {
                OnCompleted?.Invoke(track.Id, false, null);
                return;
            }

            // Step 2: Gap-fill download (only missing ranges)
            var progress = new Progress<float>(p =>
            {
                lock (_lock)
                {
                    if (_activeTasks.TryGetValue(track.Id, out var task))
                        task.Progress = p;
                }
                OnProgress?.Invoke(track.Id, p);
            });

            bool cached = await Audio.Cache.CacheDownloadHelper.EnsureFullyCachedAsync(
                descriptor.Value,
                Audio.Http.SharedHttpClient.Instance,
                cache,
                progress,
                ct).ConfigureAwait(false);

            if (!cached)
            {
                Log.Warn($"[DownloadService] Gap-fill incomplete for {track.Id}");
                OnCompleted?.Invoke(track.Id, false, null);
                return;
            }

            ct.ThrowIfCancellationRequested();

            // Step 3: Export completed cache to Downloads
            bool exported = await cache.ExportTrackToDownloadsAsync(
                track.Id,
                async id => await _library.GetTrackAsync(id).ConfigureAwait(false),
                async t => await _library.AddOrUpdateTrackAsync(t).ConfigureAwait(false),
                ct).ConfigureAwait(false);

            if (exported)
            {
                var updatedTrack = await _library.GetTrackAsync(track.Id).ConfigureAwait(false);
                if (updatedTrack != null)
                {
                    track.IsDownloaded = true;
                    track.LocalPath = updatedTrack.LocalPath;
                    OnCompleted?.Invoke(track.Id, true, track.LocalPath);
                    return;
                }
            }

            OnCompleted?.Invoke(track.Id, false, null);
        }
        catch (OperationCanceledException)
        {
            OnCompleted?.Invoke(track.Id, false, null);
        }
        catch (Exception ex)
        {
            Log.Error($"[DownloadService] Gap-fill download failed for {track.Id}: {ex.Message}");
            OnCompleted?.Invoke(track.Id, false, null);
        }
        finally
        {
            lock (_lock)
            {
                _activeTasks.Remove(track.Id);
            }
            _downloadSemaphore.Release();
        }
    }

    public void CancelDownload(string trackId)
    {
        lock (_lock)
        {
            if (_activeTasks.TryGetValue(trackId, out var task))
            {
                task.CancellationSource.Cancel();
            }
        }
    }

    public void CancelAllDownloads()
    {
        lock (_lock)
        {
            foreach (var task in _activeTasks.Values)
            {
                task.CancellationSource.Cancel();
            }
        }
    }

    private sealed class DownloadTask
    {
        public float Progress { get; set; }
        public CancellationTokenSource CancellationSource { get; set; } = new();
    }
}