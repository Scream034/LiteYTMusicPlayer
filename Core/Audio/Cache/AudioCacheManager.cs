using System.Buffers;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32.SafeHandles;
using LMP.Core.Audio.Interfaces;
using static LMP.Core.Audio.AudioConstants;
using LMP.Core.Audio.Normalization;

namespace LMP.Core.Audio.Cache;

public sealed class AudioCacheManager : IAsyncDisposable, IDisposable
{
    /// <summary>
    /// Текущая версия схемы metadata кэша.
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    ///   <item><b>1</b> — legacy chunk-based: <c>ChunkMaskData</c>, <c>TotalChunks</c>, <c>ChunkSize</c>.</item>
    ///   <item><b>2</b> — range-based: <c>DownloadedRangesData</c>, <c>AlignmentBytes</c>.</item>
    ///   <item><b>3</b> — lufs нормализация без сохранения gain.</item>
    /// </list>
    /// </remarks>
    private const int CurrentSchemaVersion = 3;

    /// <summary>
    /// Обёртка индекса кэша с версионированием схемы.
    /// Используется для сериализации/десериализации JSON-файла metadata.
    /// </summary>
    public sealed class AudioCacheIndexEnvelope
    {
        /// <summary>Версия схемы metadata.</summary>
        public int SchemaVersion { get; set; }

        /// <summary>Записи кэша.</summary>
        public List<AudioCacheEntry> Entries { get; set; } = [];
    }

    private readonly string _cacheDirectory;
    private readonly long _maxCacheSize;
    private readonly ConcurrentDictionary<string, AudioCacheEntry> _entries = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> _trackIndex = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _saveLock = new(1, 1);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _fileLocks = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, CacheFileHandle> _fileHandles = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _timerCts = new();
    private readonly Task _autoSaveTask;
    private volatile bool _disposed;

    public event Action<string, AudioFormat, int, bool>? OnFormatCached;
    public event Action? OnCacheCleared;

    public AudioCacheManager(string? cacheDirectory = null, long maxCacheSizeMb = 2048)
    {
        _cacheDirectory = cacheDirectory ?? G.Folder.AudioCache;
        _maxCacheSize = maxCacheSizeMb * 1024 * 1024;
        Directory.CreateDirectory(_cacheDirectory);
        LoadIndex();
        _autoSaveTask = AutoSaveLoopAsync(_timerCts.Token);
        Log.Info($"[AudioCache] Initialized: {_cacheDirectory}, max={maxCacheSizeMb}MB, entries={_entries.Count}");
    }

    #region Public API

    public bool IsTrackFullyCached(string trackId) => FindBestCache(trackId) != null;

    public AudioCacheEntry? FindBestCacheByTrackId(string trackId) => FindBestCache(trackId);

    public void HydrateCacheStatus(IEnumerable<TrackInfo> tracks)
    {
        var trackMap = new Dictionary<string, List<TrackInfo>>(StringComparer.Ordinal);

        foreach (var track in tracks)
        {
            if (track.IsDownloaded || track.IsCached || string.IsNullOrEmpty(track.Id))
                continue;

            if (!trackMap.TryGetValue(track.Id, out var list))
            {
                list = new List<TrackInfo>(1);
                trackMap[track.Id] = list;
            }

            list.Add(track);
        }

        if (trackMap.Count == 0) return;

        foreach (var (trackId, tracksList) in trackMap)
        {
            if (!_trackIndex.TryGetValue(trackId, out var keys)) continue;

            AudioCacheEntry? bestEntry = null;

            foreach (var key in keys.Keys)
            {
                if (_entries.TryGetValue(key, out var entry)
                    && entry.IsComplete
                    && EnsureCacheFileIntegrity(entry)
                    && (bestEntry == null || entry.Bitrate > bestEntry.Bitrate))
                {
                    bestEntry = entry;
                }
            }

            if (bestEntry != null)
            {
                foreach (var track in tracksList)
                    track.MarkAsCached(bestEntry.Format, bestEntry.Bitrate);
            }
        }
    }

    public bool IsFullyCached(string cacheKey) =>
        _entries.TryGetValue(cacheKey, out var entry)
        && entry.IsComplete
        && EnsureCacheFileIntegrity(entry);

    /// <summary>
    /// Возвращает лучший локальный кэш для быстрого старта partially cached трека.
    /// </summary>
    /// <param name="trackId">Идентификатор трека.</param>
    /// <param name="minContiguousBytes">
    /// Минимальный непрерывный префикс от начала файла, достаточный для fast-start.
    /// </param>
    /// <returns>
    /// Лучшую запись кэша, если найден локальный contiguous prefix достаточной длины;
    /// иначе <c>null</c>.
    /// </returns>
    public AudioCacheEntry? FindBestStartupCache(string trackId, int minContiguousBytes)
    {
        var entry = FindBestStartupCacheCore(trackId, minContiguousBytes);
        if (entry != null)
            return entry;

        var rawTrackId = TryGetRawTrackId(trackId);
        if (!string.IsNullOrEmpty(rawTrackId))
        {
            entry = FindBestStartupCacheCore(rawTrackId, minContiguousBytes);
            if (entry != null)
                return entry;
        }

        if (!IsPrefixedTrackId(trackId))
            return FindBestStartupCacheCore(string.Concat("yt_", trackId), minContiguousBytes);

        return null;
    }

    public AudioCacheEntry? FindBestCache(string trackId)
    {
        var entry = FindBestCacheCore(trackId);
        if (entry != null)
            return entry;

        var rawTrackId = TryGetRawTrackId(trackId);
        if (!string.IsNullOrEmpty(rawTrackId))
        {
            entry = FindBestCacheCore(rawTrackId);
            if (entry != null)
                return entry;
        }

        if (!IsPrefixedTrackId(trackId))
            return FindBestCacheCore(string.Concat("yt_", trackId));

        return null;
    }

    /// <summary>
    /// Обновляет integrated loudness во ВСЕХ cache entries данного трека.
    /// </summary>
    /// <remarks>
    /// Обновляет все entries, а не только «лучшую», потому что при повторном play
    /// может быть выбрана другая entry (другой format/bitrate bucket).
    /// Без обновления всех entries YouTube LUFS терялся бы при смене active entry.
    ///
    /// Приоритет источника определяется числовым порядком <see cref="LoudnessSource"/>:
    /// <see cref="LoudnessSource.YoutubePerceptual"/> (2) всегда перезаписывает
    /// <see cref="LoudnessSource.EbuMeasured"/> (1).
    /// </remarks>
    /// <param name="trackId">Идентификатор трека.</param>
    /// <param name="integratedLufs">Integrated loudness в LUFS.</param>
    /// <param name="source">Источник значения.</param>
    /// <returns><c>true</c>, если хотя бы одна entry была обновлена.</returns>
    public bool TryUpdateIntegratedLufs(string trackId, float integratedLufs, LoudnessSource source)
    {
        if (string.IsNullOrEmpty(trackId) || !float.IsFinite(integratedLufs))
            return false;

        var keys = CollectAllCacheKeysForTrack(trackId);
        if (keys.Count == 0)
            return false;

        bool anyUpdated = false;

        foreach (var cacheKey in keys)
        {
            if (!_entries.TryGetValue(cacheKey, out var entry))
                continue;

            var existingSource = (LoudnessSource)entry.IntegratedLufsSource;

            // Числовой порядок enum = приоритет: YoutubePerceptual(2) > EbuMeasured(1) > Unknown(0)
            if (existingSource > source)
                continue;

            // Skip если значение идентично — не дёргаем SaveIndexAsync без причины
            if (entry.IntegratedLufs is float existing
                && MathF.Abs(existing - integratedLufs) < 0.01f
                && existingSource == source)
            {
                continue;
            }

            entry.IntegratedLufs = integratedLufs;
            entry.IntegratedLufsSource = (int)source;
            entry.LastAccessedAt = DateTime.UtcNow;
            anyUpdated = true;
        }

        if (anyUpdated)
            _ = SaveIndexAsync();

        return anyUpdated;
    }

    /// <summary>
    /// Собирает все cacheKey для трека, включая варианты ID с/без доменного префикса.
    /// </summary>
    /// <remarks>
    /// Трек может иметь несколько entries под разными cacheKey
    /// (разные format/bitrate: WebM/160, Mp4/128 и т.д.).
    /// Также ID может встречаться как с префиксом <c>yt_</c>, так и без.
    /// </remarks>
    private List<string> CollectAllCacheKeysForTrack(string trackId)
    {
        // Начальная ёмкость 4: типичный трек имеет 1-2 entries,
        // редко больше (разные форматы одного трека).
        var keys = new List<string>(4);

        AppendKeysFromIndex(trackId, keys);

        var rawId = TryGetRawTrackId(trackId);
        if (!string.IsNullOrEmpty(rawId))
            AppendKeysFromIndex(rawId, keys);

        if (!IsPrefixedTrackId(trackId))
            AppendKeysFromIndex(string.Concat("yt_", trackId), keys);

        return keys;
    }

    /// <summary>
    /// Добавляет cacheKey из trackIndex в список, если они там есть.
    /// </summary>
    private void AppendKeysFromIndex(string trackId, List<string> target)
    {
        if (_trackIndex.TryGetValue(trackId, out var index))
        {
            foreach (var key in index.Keys)
                target.Add(key);
        }
    }

    private AudioCacheEntry? FindBestStartupCacheCore(string trackId, int minContiguousBytes)
    {
        if (string.IsNullOrEmpty(trackId)) return null;
        if (!_trackIndex.TryGetValue(trackId, out var keys)) return null;

        AudioCacheEntry? best = null;
        long bestContiguous = long.MinValue;

        foreach (var key in keys.Keys)
        {
            if (!_entries.TryGetValue(key, out var entry))
                continue;

            string path = GetCachePath(entry.CacheKey);
            if (!File.Exists(path))
                continue;

            if (entry.IsComplete && !EnsureCacheFileIntegrity(entry))
                continue;

            long contiguous = entry.IsComplete
                ? entry.TotalSize
                : entry.GetContiguousDownloadedBytesFrom(0);

            if (contiguous < minContiguousBytes)
                continue;

            if (best == null
                || contiguous > bestContiguous
                || (contiguous == bestContiguous && entry.Bitrate > best.Bitrate))
            {
                best = entry;
                bestContiguous = contiguous;
            }
        }

        return best;
    }

    private AudioCacheEntry? FindBestCacheCore(string trackId)
    {
        if (string.IsNullOrEmpty(trackId)) return null;
        if (!_trackIndex.TryGetValue(trackId, out var keys)) return null;

        AudioCacheEntry? best = null;

        foreach (var key in keys.Keys)
        {
            if (_entries.TryGetValue(key, out var entry)
                && entry.IsComplete
                && EnsureCacheFileIntegrity(entry)
                && (best == null || entry.Bitrate > best.Bitrate))
            {
                best = entry;
            }
        }

        return best;
    }

    /// <summary>
    /// Определяет, содержит ли идентификатор доменный префикс трека.
    /// </summary>
    /// <param name="trackId">Идентификатор трека.</param>
    private static bool IsPrefixedTrackId(string trackId)
    {
        if (string.IsNullOrEmpty(trackId))
            return false;

        var span = trackId.AsSpan();
        return span.StartsWith("yt_".AsSpan()) || span.StartsWith("yt_pl_".AsSpan());
    }

    /// <summary>
    /// Возвращает raw YouTube ID без доменного префикса, если он присутствует.
    /// </summary>
    /// <param name="trackId">Идентификатор трека.</param>
    private static string? TryGetRawTrackId(string trackId)
    {
        if (string.IsNullOrEmpty(trackId))
            return null;

        var span = trackId.AsSpan();

        if (span.StartsWith("yt_pl_".AsSpan()))
            return span[6..].ToString();

        if (span.StartsWith("yt_".AsSpan()))
            return span[3..].ToString();

        return null;
    }

    public bool HasPartialCache(string cacheKey) =>
        _entries.TryGetValue(cacheKey, out var entry) && entry.DownloadedBytes > 0;

    public AudioCacheEntry? GetCacheInfo(string cacheKey) =>
        _entries.TryGetValue(cacheKey, out var entry) ? entry : null;

    public string GetCachePath(string cacheKey)
    {
        var safeId = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(cacheKey)))[..16];
        return Path.Combine(_cacheDirectory, safeId + CacheFileExtension);
    }

    public void Touch(string cacheKey)
    {
        if (_entries.TryGetValue(cacheKey, out var entry))
            entry.LastAccessedAt = DateTime.UtcNow;
    }

    /// <summary>
    /// Создаёт или обновляет metadata-запись range-based кэша.
    /// </summary>
    public AudioCacheEntry CreateOrUpdate(
        string cacheKey,
        string trackId,
        string url,
        long totalSize,
        AudioFormat format,
        AudioCodec codec,
        int bitrate = 0,
        long durationMs = -1,
        int alignmentBytes = ChunkSize)
    {
        var entry = _entries.GetOrAdd(cacheKey, _ => new AudioCacheEntry
        {
            CacheKey = cacheKey,
            TrackId = trackId,
            OriginalUrl = url,
            TotalSize = totalSize,
            Format = format,
            Codec = codec,
            Bitrate = bitrate,
            DurationMs = durationMs,
            AlignmentBytes = alignmentBytes,
            CreatedAt = DateTime.UtcNow,
            LastAccessedAt = DateTime.UtcNow
        });

        entry.OriginalUrl = url;
        entry.LastAccessedAt = DateTime.UtcNow;

        if (bitrate > 0)
            entry.Bitrate = bitrate;

        if (durationMs > 0)
            entry.DurationMs = durationMs;

        if (entry.DownloadedBytes == 0 && alignmentBytes > 0)
            entry.AlignmentBytes = alignmentBytes;

        AddToTrackIndex(trackId, cacheKey);
        return entry;
    }

    public void MarkComplete(string cacheKey, long? durationMs = null, int? bitrate = null)
    {
        if (!_entries.TryGetValue(cacheKey, out var entry)) return;

        entry.MarkFullyDownloaded();
        entry.IsComplete = true;
        entry.CompletedAt = DateTime.UtcNow;
        entry.LastAccessedAt = DateTime.UtcNow;

        if (durationMs.HasValue) entry.DurationMs = durationMs.Value;
        if (bitrate.HasValue) entry.Bitrate = bitrate.Value;

        UpdateFileSizeCache(entry);
        // Handle закроется автоматически при release lease — не нужен ForceClose
        Log.Info($"[AudioCache] Track fully cached: {cacheKey}");
        _ = SaveIndexAsync();
        RaiseFormatCached(entry);
    }

    public void RemoveCache(string cacheKey)
    {
        if (!_entries.TryRemove(cacheKey, out var entry)) return;

        RemoveFromTrackIndex(entry.TrackId, cacheKey);
        ForceCloseHandle(cacheKey);

        var filePath = GetCachePath(cacheKey);
        try { if (File.Exists(filePath)) File.Delete(filePath); } catch { }

        _ = SaveIndexAsync();
    }

    private void InvalidateCompleteEntry(AudioCacheEntry entry)
    {
        entry.IsComplete = false;
        entry.CompletedAt = null;
        entry.ResetDownloadedRanges();
        entry.ActualFileSize = 0;
        ForceCloseHandle(entry.CacheKey);
        _ = SaveIndexAsync();
    }

    /// <summary>
    /// Записывает произвольный диапазон байт в файл кэша.
    /// I/O отслеживается через lease-модель для безопасного закрытия дескриптора.
    /// </summary>
    public async ValueTask WriteRangeAsync(
        string cacheKey,
        long offset,
        ReadOnlyMemory<byte> data,
        CancellationToken ct = default)
    {
        if (!_entries.TryGetValue(cacheKey, out var entry)) return;
        if (data.IsEmpty) return;
        if (offset < 0 || offset >= entry.TotalSize) return;

        long remaining = entry.TotalSize - offset;
        if (remaining <= 0) return;

        if (data.Length > remaining)
            data = data[..(int)remaining];

        var fileLock = _fileLocks.GetOrAdd(cacheKey, static _ => new SemaphoreSlim(1, 1));
        await fileLock.WaitAsync(ct).ConfigureAwait(false);

        var fileHandle = GetFileHandle(cacheKey);
        fileHandle.BeginIo();

        try
        {
            var handle = fileHandle.GetOrOpen();
            await RandomAccess.WriteAsync(handle, data, offset, ct).ConfigureAwait(false);

            entry.MarkRangeDownloaded(offset, data.Length);
            entry.LastAccessedAt = DateTime.UtcNow;

            long writtenEnd = offset + data.Length;
            if (writtenEnd > entry.ActualFileSize)
                entry.ActualFileSize = writtenEnd;

            if (!entry.IsComplete && entry.DownloadedBytes >= entry.TotalSize)
            {
                entry.IsComplete = true;
                entry.CompletedAt = DateTime.UtcNow;
                entry.ActualFileSize = Math.Max(entry.ActualFileSize, entry.TotalSize);
                Log.Info($"[AudioCache] Track fully cached: {cacheKey}");
                RaiseFormatCached(entry);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { Log.Warn($"[AudioCache] Write range failed: {ex.Message}"); }
        finally
        {
            fileHandle.EndIo();
            fileLock.Release();
        }
    }

    /// <summary>
    /// Читает произвольный диапазон байт из файла кэша.
    /// I/O отслеживается через lease-модель для безопасного закрытия дескриптора.
    /// </summary>
    public async ValueTask<(IMemoryOwner<byte> Owner, int Length)?> ReadRangeAsync(
        string cacheKey,
        long offset,
        int length,
        CancellationToken ct = default)
    {
        if (!_entries.TryGetValue(cacheKey, out var entry)) return null;
        if (length <= 0) return null;
        if (!entry.IsRangeDownloaded(offset, length)) return null;

        long remaining = entry.TotalSize - offset;
        if (offset < 0 || remaining <= 0) return null;

        int expectedLength = length;
        if (expectedLength > remaining)
            expectedLength = (int)remaining;
        if (expectedLength <= 0) return null;

        var fileHandle = TryGetFileHandle(cacheKey);
        if (fileHandle == null) return null;

        fileHandle.BeginIo();
        var memoryOwner = MemoryPool<byte>.Shared.Rent(expectedLength);

        try
        {
            var handle = fileHandle.GetOrOpen(FileMode.Open);
            int totalRead = 0;
            var buffer = memoryOwner.Memory[..expectedLength];

            while (totalRead < expectedLength)
            {
                int read = await RandomAccess.ReadAsync(
                    handle, buffer[totalRead..expectedLength],
                    offset + totalRead, ct).ConfigureAwait(false);
                if (read == 0) break;
                totalRead += read;
            }

            if (totalRead != expectedLength)
            {
                memoryOwner.Dispose();
                entry.InvalidateRange(offset, expectedLength);
                Log.Warn($"[AudioCache] Short read range of {cacheKey}: " +
                         $"offset={offset}, expected={expectedLength}, got={totalRead}. Range invalidated.");
                return null;
            }

            entry.LastAccessedAt = DateTime.UtcNow;
            return (memoryOwner, expectedLength);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            memoryOwner.Dispose();
            throw;
        }
        catch
        {
            memoryOwner.Dispose();
            return null;
        }
        finally
        {
            fileHandle.EndIo();
        }
    }

    public Stream? OpenCachedStream(string cacheKey)
    {
        if (!_entries.TryGetValue(cacheKey, out var entry)
            || !entry.IsComplete
            || !EnsureCacheFileIntegrity(entry))
        {
            return null;
        }

        Touch(cacheKey);

        return new FileStream(
            GetCachePath(cacheKey),
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: CacheFileBufferSize,
            useAsync: false);
    }

    public async Task CleanupAsync(CancellationToken ct = default)
    {
        var stats = GetStats();
        if (stats.TotalSizeBytes <= _maxCacheSize) return;

        Log.Info($"[AudioCache] Cleanup needed: {stats.TotalSizeBytes / 1024 / 1024}MB > {_maxCacheSize / 1024 / 1024}MB");

        long totalSize = stats.TotalSizeBytes;

        var entries = _entries.Values
            .OrderBy(e => e.LastAccessedAt)
            .ToList();

        foreach (var entry in entries)
        {
            ct.ThrowIfCancellationRequested();

            if (totalSize <= _maxCacheSize * CacheCleanupThreshold)
                break;

            totalSize -= entry.ActualFileSize;
            RemoveCache(entry.CacheKey);
        }

        Log.Info($"[AudioCache] Cleanup complete, new size: {totalSize / 1024 / 1024}MB");
    }

    #endregion

    #region Lease API

    /// <summary>
    /// Регистрирует lifetime lease на cache-файл.
    /// Source вызывает при инициализации, чтобы гарантировать жизнь дескриптора
    /// до явного <see cref="ReleaseLease"/>.
    /// </summary>
    /// <param name="cacheKey">Уникальный ключ кэша.</param>
    public void AcquireLease(string cacheKey)
    {
        var entry = _fileHandles.GetOrAdd(cacheKey,
            key => new CacheFileHandle(GetCachePath(key)));
        entry.AddLease();
    }

    /// <summary>
    /// Освобождает lifetime lease на cache-файл.
    /// Если после release не осталось lease'ов и in-flight I/O,
    /// дескриптор закрывается автоматически.
    /// </summary>
    /// <param name="cacheKey">Уникальный ключ кэша.</param>
    public void ReleaseLease(string cacheKey)
    {
        if (_fileHandles.TryGetValue(cacheKey, out var entry))
            entry.RemoveLease();
    }

    #endregion

    #region Statistics

    /// <summary>
    /// Перечисляет все complete и физически валидные записи кэша.
    /// </summary>
    internal IEnumerable<AudioCacheEntry> GetAllCompleteEntries()
    {
        foreach (var entry in _entries.Values)
        {
            if (entry.IsComplete && EnsureCacheFileIntegrity(entry))
                yield return entry;
        }
    }

    /// <summary>
    /// Возвращает компактную статистику кэша.
    /// </summary>
    public (int FileCount, int SizeMb) GetStatsCompact()
    {
        var stats = GetStats();
        int totalFiles = stats.CompleteEntries + stats.PartialEntries;
        return (totalFiles, (int)(stats.TotalSizeBytes / 1024 / 1024));
    }

    /// <summary>
    /// Собирает полную статистику использования дискового пространства кэша.
    /// </summary>
    public CacheStats GetStats()
    {
        long totalSize = 0;
        int completeCount = 0;
        int partialCount = 0;
        int totalCount = 0;

        foreach (var entry in _entries.Values)
        {
            UpdateFileSizeCache(entry);

            if (entry.ActualFileSize > 0)
            {
                totalCount++;
                if (entry.IsComplete) completeCount++;
                else if (entry.DownloadedBytes > 0) partialCount++;
                totalSize += entry.ActualFileSize;
            }
        }

        return new CacheStats
        {
            TotalEntries = totalCount,
            CompleteEntries = completeCount,
            PartialEntries = partialCount,
            TotalSizeBytes = totalSize,
            MaxSizeBytes = _maxCacheSize
        };
    }

    public static (int FileCount, int SizeMb) GetDownloadsStats()
    {
        try
        {
            var dir = new DirectoryInfo(G.Folder.Downloads);
            if (!dir.Exists) return (0, 0);

            var files = dir.GetFiles("*.*", SearchOption.TopDirectoryOnly);
            long totalBytes = 0;

            for (int i = 0; i < files.Length; i++)
                totalBytes += files[i].Length;

            return (files.Length, (int)(totalBytes / 1024 / 1024));
        }
        catch (Exception ex)
        {
            Log.Warn($"[AudioCache] GetDownloadsStats error: {ex.Message}");
            return (0, 0);
        }
    }

    public List<(AudioFormat Format, int Bitrate)> GetCachedFormats(string trackId)
    {
        var result = new List<(AudioFormat, int)>();
        if (!_trackIndex.TryGetValue(trackId, out var keys)) return result;

        foreach (var key in keys.Keys)
        {
            if (_entries.TryGetValue(key, out var entry)
                && entry.IsComplete
                && EnsureCacheFileIntegrity(entry))
            {
                result.Add((entry.Format, entry.Bitrate));
            }
        }

        return result;
    }

    public bool IsFormatCached(string trackId, AudioFormat format, int bitrate)
    {
        return IsFullyCached(AudioSourceFactory.BuildCacheKey(trackId, format, bitrate));
    }

    #endregion

    #region Export to Downloads

    public async Task<bool> ExportTrackToDownloadsAsync(
        string trackId,
        Func<string, Task<TrackInfo?>> getTrackFunc,
        Func<TrackInfo, Task> updateTrackFunc,
        CancellationToken ct = default)
    {
        var entry = FindBestCache(trackId);
        if (entry == null)
        {
            Log.Warn($"[AudioCache] Track {trackId} not fully cached, cannot export");
            return false;
        }

        return await PromoteCacheToDownloadsAsync(entry, getTrackFunc, updateTrackFunc, ct).ConfigureAwait(false);
    }

    private async Task<bool> PromoteCacheToDownloadsAsync(
        AudioCacheEntry entry,
        Func<string, Task<TrackInfo?>> getTrackFunc,
        Func<TrackInfo, Task> updateTrackFunc,
        CancellationToken ct)
    {
        if (!await _saveLock.WaitAsync(1000, ct).ConfigureAwait(false))
            return false;

        try
        {
            var track = await getTrackFunc(entry.TrackId).ConfigureAwait(false);
            if (track == null)
            {
                Log.Warn($"[AudioCache] Track not found: {entry.TrackId}");
                return false;
            }

            if (track.IsDownloaded && !string.IsNullOrEmpty(track.LocalPath) && File.Exists(track.LocalPath))
            {
                Log.Debug($"[AudioCache] Already downloaded: {track.Title}");
                return true;
            }

            var cachePath = GetCachePath(entry.CacheKey);
            if (!File.Exists(cachePath))
            {
                Log.Warn($"[AudioCache] Cache file not found: {cachePath}");
                return false;
            }

            if (!entry.IsComplete || !EnsureCacheFileIntegrity(entry))
            {
                Log.Warn($"[AudioCache] Incomplete cache entry: {entry.CacheKey}");
                return false;
            }

            string ext = entry.Format switch
            {
                AudioFormat.WebM => "webm",
                AudioFormat.Mp4 => "m4a",
                AudioFormat.Ogg => "ogg",
                _ => "audio"
            };

            string safeName = SanitizeFileName($"{track.Author} - {track.Title}.{ext}");
            string destPath = Path.Combine(G.Folder.Downloads, safeName);

            if (File.Exists(destPath))
            {
                var existing = new FileInfo(destPath);
                if (existing.Length == entry.TotalSize)
                {
                    track.MarkAsDownloaded(destPath, entry.Format, entry.Bitrate);
                    await updateTrackFunc(track).ConfigureAwait(false);
                    return true;
                }

                var baseName = Path.GetFileNameWithoutExtension(safeName);
                destPath = Path.Combine(G.Folder.Downloads, $"{baseName}_{entry.Bitrate}kbps.{ext}");
            }

            Log.Info($"[AudioCache] Exporting to Downloads: {Path.GetFileName(destPath)}");
            File.Copy(cachePath, destPath, overwrite: true);

            track.MarkAsDownloaded(destPath, entry.Format, entry.Bitrate);
            await updateTrackFunc(track).ConfigureAwait(false);
            OnFormatCached?.Invoke(entry.TrackId, entry.Format, entry.Bitrate, true);
            return true;
        }
        catch (Exception ex)
        {
            Log.Error($"[AudioCache] Export failed: {ex.Message}");
            return false;
        }
        finally
        {
            _saveLock.Release();
        }
    }

    private static string SanitizeFileName(string fileName)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sanitized = string.Join("_", fileName.Split(invalid, StringSplitOptions.RemoveEmptyEntries));
        return sanitized.Length > 200 ? sanitized[..200] : sanitized;
    }

    #endregion

    #region Clear & Maintenance

    public async Task ClearAllAsync(CancellationToken ct = default)
    {
        if (!await _saveLock.WaitAsync(5000, ct).ConfigureAwait(false))
        {
            Log.Warn("[AudioCache] ClearAllAsync: couldn't acquire lock");
            return;
        }

        try
        {
            Log.Info("[AudioCache] Clearing all cache...");
            _entries.Clear();
            _trackIndex.Clear();
            DisposeAllHandles();

            var dir = new DirectoryInfo(_cacheDirectory);
            if (dir.Exists)
            {
                foreach (var file in dir.GetFiles())
                {
                    try { file.Delete(); }
                    catch (Exception ex) { Log.Warn($"[AudioCache] Failed to delete {file.Name}: {ex.Message}"); }
                }
            }

            Log.Info("[AudioCache] Cache cleared");
        }
        finally
        {
            _saveLock.Release();
        }

        try { OnCacheCleared?.Invoke(); }
        catch (Exception ex) { Log.Warn($"[AudioCache] OnCacheCleared handler error: {ex.Message}"); }
    }

    public static async Task ClearDownloadsAsync(CancellationToken ct = default)
    {
        await Task.Run(() =>
        {
            try
            {
                var dir = new DirectoryInfo(G.Folder.Downloads);
                if (!dir.Exists) return;

                Log.Info("[AudioCache] Clearing downloads folder...");
                foreach (var file in dir.GetFiles())
                {
                    try { file.Delete(); }
                    catch (Exception ex) { Log.Warn($"[AudioCache] Failed to delete {file.Name}: {ex.Message}"); }
                }
                Log.Info("[AudioCache] Downloads cleared");
            }
            catch (Exception ex)
            {
                Log.Error($"[AudioCache] ClearDownloadsAsync error: {ex.Message}");
            }
        }, ct).ConfigureAwait(false);
    }

    public void RemoveTrackCache(string trackId)
    {
        if (!_trackIndex.TryGetValue(trackId, out var keys)) return;

        var keysToRemove = keys.Keys.ToArray();
        for (int i = 0; i < keysToRemove.Length; i++)
            RemoveCache(keysToRemove[i]);

        Log.Debug($"[AudioCache] Removed {keysToRemove.Length} cache entries for track {trackId}");
    }

    /// <summary>
    /// Проверяет целостность complete-кэша.
    /// </summary>
    /// <remarks>
    /// <para>Если файл физически валиден (<c>Length &gt;= TotalSize</c>), но runtime range-state
    /// отсутствует (например, после миграции схемы или сбоя), выполняет self-heal
    /// вместо деструктивного удаления.</para>
    /// <para>Удаление выполняется только при реальном усечении файла на диске.</para>
    /// </remarks>
    private bool EnsureCacheFileIntegrity(AudioCacheEntry entry)
    {
        if (!entry.IsComplete) return false;

        var filePath = GetCachePath(entry.CacheKey);

        try
        {
            var fi = new FileInfo(filePath);

            if (!fi.Exists)
            {
                InvalidateCompleteEntry(entry);
                return false;
            }

            if (fi.Length >= entry.TotalSize)
            {
                if (entry.DownloadedBytes < entry.TotalSize && entry.TotalSize > 0)
                {
                    entry.MarkFullyDownloaded();
                    entry.ActualFileSize = fi.Length;
                    Log.Info($"[AudioCache] Self-healed integrity: {entry.CacheKey}");
                    _ = SaveIndexAsync();
                }

                return true;
            }

            Log.Warn($"[AudioCache] ⚠ Truncated cache file: {entry.CacheKey} " +
                     $"(disk={fi.Length / 1024}KB, expected={entry.TotalSize / 1024}KB)");

            InvalidateCompleteEntry(entry);

            try { fi.Delete(); }
            catch (Exception ex) { Log.Warn($"[AudioCache] Failed to delete truncated cache file: {ex.Message}"); }

            return false;
        }
        catch (Exception ex)
        {
            Log.Warn($"[AudioCache] Integrity check I/O error for {entry.CacheKey}: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Инвалидирует диапазон байт в кэше.
    /// </summary>
    public void InvalidateRange(string cacheKey, long offset, int length)
    {
        if (string.IsNullOrEmpty(cacheKey) || length <= 0) return;

        if (_entries.TryGetValue(cacheKey, out var entry))
        {
            entry.IsComplete = false;
            entry.CompletedAt = null;
            entry.InvalidateRange(offset, length);
            UpdateFileSizeCache(entry);
            _ = SaveIndexAsync();

            Log.Info($"[AudioCache] Surgical invalidation: {cacheKey}, range={offset}-{offset + length - 1}");
        }
    }

    /// <summary>
    /// Инвалидирует диапазон байт для лучшего доступного кэша указанного трека.
    /// </summary>
    /// <param name="trackId">Идентификатор трека.</param>
    /// <param name="offset">Абсолютное смещение диапазона в байтах.</param>
    /// <param name="length">Длина диапазона в байтах.</param>
    public void InvalidateRangeByTrackId(string trackId, long offset, int length)
    {
        if (string.IsNullOrEmpty(trackId) || length <= 0) return;

        var entry = FindBestCache(trackId);
        if (entry == null) return;

        InvalidateRange(entry.CacheKey, offset, length);
    }

    #endregion

    #region Private Helpers

    private void AddToTrackIndex(string trackId, string cacheKey)
    {
        var keys = _trackIndex.GetOrAdd(trackId, static _ => new ConcurrentDictionary<string, byte>(StringComparer.Ordinal));
        keys.TryAdd(cacheKey, 1);
    }

    private void RemoveFromTrackIndex(string trackId, string cacheKey)
    {
        if (!_trackIndex.TryGetValue(trackId, out var keys)) return;

        keys.TryRemove(cacheKey, out _);
        if (keys.IsEmpty)
            _trackIndex.TryRemove(trackId, out _);
    }

    /// <summary>Возвращает или создаёт <see cref="CacheFileHandle"/> для cacheKey.</summary>
    private CacheFileHandle GetFileHandle(string cacheKey) =>
        _fileHandles.GetOrAdd(cacheKey, key => new CacheFileHandle(GetCachePath(key)));

    /// <summary>Возвращает <see cref="CacheFileHandle"/> если существует.</summary>
    private CacheFileHandle? TryGetFileHandle(string cacheKey) =>
        _fileHandles.TryGetValue(cacheKey, out var entry) ? entry : null;

    /// <summary>Принудительно закрывает дескриптор cache-файла.</summary>
    private void ForceCloseHandle(string cacheKey)
    {
        if (_fileHandles.TryGetValue(cacheKey, out var entry))
            entry.ForceClose();
    }

    /// <summary>Принудительно закрывает все дескрипторы.</summary>
    private void ForceCloseAllHandles()
    {
        foreach (var entry in _fileHandles.Values)
            entry.ForceClose();
    }

    private void DisposeHandle(string cacheKey)
    {
        if (_fileHandles.TryRemove(cacheKey, out var handle))
        {
            try { handle.Dispose(); }
            catch { }
        }
    }

    private void DisposeAllHandles()
    {
        foreach (var key in _fileHandles.Keys)
            DisposeHandle(key);
    }

    private void UpdateFileSizeCache(AudioCacheEntry entry)
    {
        try
        {
            var filePath = GetCachePath(entry.CacheKey);
            entry.ActualFileSize = File.Exists(filePath)
                ? new FileInfo(filePath).Length
                : 0;
        }
        catch
        {
        }
    }

    private void RaiseFormatCached(AudioCacheEntry entry)
    {
        try
        {
            OnFormatCached?.Invoke(entry.TrackId, entry.Format, entry.Bitrate, false);
        }
        catch (Exception ex)
        {
            Log.Warn($"[AudioCache] OnFormatCached handler error: {ex.Message}");
        }
    }

    private void LoadIndex()
    {
        var indexPath = Path.Combine(_cacheDirectory, CacheMetadataFileName);
        if (!File.Exists(indexPath)) return;

        try
        {
            string json = File.ReadAllText(indexPath);
            if (string.IsNullOrWhiteSpace(json)) return;

            int loadedSchemaVersion;
            List<AudioCacheEntry>? entries;

            var trimmed = json.AsSpan().TrimStart();
            if (trimmed.Length > 0 && trimmed[0] == '{')
            {
                var envelope = JsonSerializer.Deserialize(json, AppJsonContext.Default.AudioCacheIndexEnvelope);
                loadedSchemaVersion = envelope?.SchemaVersion ?? 0;
                entries = envelope?.Entries;
            }
            else
            {
                loadedSchemaVersion = 1;
                entries = JsonSerializer.Deserialize(json, AppJsonContext.Default.ListAudioCacheEntry);
            }

            if (entries == null) return;

            bool needsMigration = loadedSchemaVersion < CurrentSchemaVersion;
            bool needsLufsMigration = loadedSchemaVersion < 3;
            int migratedComplete = 0;
            int droppedPartial = 0;

            for (int i = 0; i < entries.Count; i++)
            {
                var entry = entries[i];
                if (string.IsNullOrEmpty(entry.CacheKey)) continue;

                string filePath = GetCachePath(entry.CacheKey);
                if (!File.Exists(filePath)) continue;

                if (needsMigration)
                    MigrateEntry(entry, filePath, ref migratedComplete, ref droppedPartial);

                // Инвалидация legacy normalization metadata ДО RestoreAfterLoad/добавления в _entries
                if (needsLufsMigration)
                {
                    entry.IntegratedLufs = null;
                    entry.IntegratedLufsSource = 0;
                }

                entry.RestoreAfterLoad();

                if (entry.IsComplete && entry.DownloadedBytes < entry.TotalSize && entry.TotalSize > 0)
                {
                    try
                    {
                        var fi = new FileInfo(filePath);
                        if (fi.Exists && fi.Length >= entry.TotalSize)
                        {
                            entry.MarkFullyDownloaded();
                            entry.ActualFileSize = fi.Length;
                            migratedComplete++;
                            Log.Debug($"[AudioCache] Self-healed complete entry: {entry.CacheKey}");
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Warn($"[AudioCache] Self-heal I/O error for {entry.CacheKey}: {ex.Message}");
                    }
                }

                UpdateFileSizeCache(entry);
                _entries.TryAdd(entry.CacheKey, entry);
                AddToTrackIndex(entry.TrackId, entry.CacheKey);
            }

            if (needsMigration || needsLufsMigration || migratedComplete > 0)
            {
                Log.Info($"[AudioCache] Schema migration v{loadedSchemaVersion}→v{CurrentSchemaVersion}: " +
                         $"{migratedComplete} complete restored, {droppedPartial} partial reset" +
                         (needsLufsMigration ? $", {entries.Count} normalization metadata reset (LUFS model)" : ""));
                _ = SaveIndexAsync();
            }

            Log.Debug($"[AudioCache] Loaded {_entries.Count} entries");
        }
        catch (Exception ex)
        {
            Log.Warn($"[AudioCache] Failed to load index: {ex.Message}");
        }
    }

    /// <summary>
    /// Мигрирует запись со старой chunk-based схемы на текущую range-based.
    /// </summary>
    /// <remarks>
    /// <para><b>Complete без range-state:</b> восстанавливается по физическому файлу.
    /// Если файл на диске <c>&gt;= TotalSize</c>, range-state генерируется как <c>[0..TotalSize)</c>.</para>
    /// <para><b>Partial без range-state:</b> coverage сбрасывается. Файл сохраняется на диске
    /// для возможной перезаписи при повторном кэшировании.</para>
    /// </remarks>
    private static void MigrateEntry(
        AudioCacheEntry entry,
        string filePath,
        ref int migratedComplete,
        ref int droppedPartial)
    {
        if (entry.AlignmentBytes <= 0)
            entry.AlignmentBytes = ChunkSize;

        bool hasRangeState = entry.DownloadedRangesData is { Count: > 0 };

        if (entry.IsComplete && !hasRangeState)
        {
            try
            {
                var fi = new FileInfo(filePath);
                if (fi.Exists && fi.Length >= entry.TotalSize && entry.TotalSize > 0)
                {
                    entry.MarkFullyDownloaded();
                    entry.ActualFileSize = fi.Length;
                    migratedComplete++;
                }
                else
                {
                    entry.IsComplete = false;
                    entry.CompletedAt = null;
                    droppedPartial++;
                }
            }
            catch
            {
                entry.IsComplete = false;
                entry.CompletedAt = null;
                droppedPartial++;
            }
        }
        else if (!entry.IsComplete && !hasRangeState)
        {
            entry.ResetDownloadedRanges();
            droppedPartial++;
        }
    }

    private async Task SaveIndexAsync()
    {
        if (_disposed) return;
        if (!await _saveLock.WaitAsync(CacheSaveLockTimeoutMs).ConfigureAwait(false)) return;

        try
        {
            var indexPath = Path.Combine(_cacheDirectory, CacheMetadataFileName);
            var entries = _entries.Values.ToList();

            for (int i = 0; i < entries.Count; i++)
                entries[i].PrepareForSave();

            var envelope = new AudioCacheIndexEnvelope
            {
                SchemaVersion = CurrentSchemaVersion,
                Entries = entries
            };

            // Используем тот же source-generated контекст (camelCase),
            // что и LoadIndex, чтобы JSON корректно десериализовался при следующем старте.
            string json = JsonSerializer.Serialize(
                envelope, AppJsonContext.Default.AudioCacheIndexEnvelope);

            await File.WriteAllTextAsync(indexPath, json).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Warn($"[AudioCache] Failed to save index: {ex.Message}");
        }
        finally
        {
            _saveLock.Release();
        }
    }

    /// <summary>
    /// Синхронно сохраняет индекс кэша.
    /// Используется только в shutdown-path, чтобы не терять metadata/gain
    /// при sync dispose приложения.
    /// </summary>
    private void SaveIndexSync()
    {
        if (_disposed) return;
        if (!_saveLock.Wait(CacheSaveLockTimeoutMs)) return;

        try
        {
            var indexPath = Path.Combine(_cacheDirectory, CacheMetadataFileName);
            var entries = _entries.Values.ToList();

            for (int i = 0; i < entries.Count; i++)
                entries[i].PrepareForSave();

            var envelope = new AudioCacheIndexEnvelope
            {
                SchemaVersion = CurrentSchemaVersion,
                Entries = entries
            };

            // Тот же source-generated контекст (camelCase), что и LoadIndex.
            string json = JsonSerializer.Serialize(
                envelope, AppJsonContext.Default.AudioCacheIndexEnvelope);

            File.WriteAllText(indexPath, json);
        }
        catch (Exception ex)
        {
            Log.Warn($"[AudioCache] Failed to sync save index: {ex.Message}");
        }
        finally
        {
            _saveLock.Release();
        }
    }

    private async Task AutoSaveLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(CacheAutoSaveIntervalMs, ct).ConfigureAwait(false);
                await SaveIndexAsync().ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Log.Warn($"[AudioCache] Auto-save error: {ex.Message}");
            }
        }
    }

    #endregion

    #region Dispose

    public void Dispose()
    {
        if (_disposed) return;

        SaveIndexSync();

        _disposed = true;
        _timerCts.Cancel();
        ForceCloseAllHandles();

        foreach (var fileLock in _fileLocks.Values)
        {
            try { fileLock.Dispose(); } catch { }
        }

        _timerCts.Dispose();
        _saveLock.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;

        _timerCts.Cancel();

        try { await _autoSaveTask.ConfigureAwait(false); } catch { }

        await SaveIndexAsync().ConfigureAwait(false);

        _disposed = true;
        ForceCloseAllHandles();

        foreach (var fileLock in _fileLocks.Values)
        {
            try { fileLock.Dispose(); } catch { }
        }

        _timerCts.Dispose();
        _saveLock.Dispose();
    }

    #endregion

    /// <summary>
    /// Владеющая запись файлового дескриптора cache-файла.
    /// Обеспечивает lease-based ownership и in-flight I/O tracking.
    /// </summary>
    /// <remarks>
    /// <para><b>Инварианты:</b></para>
    /// <list type="bullet">
    ///   <item>Handle открывается лениво при первом I/O запросе через <see cref="GetOrOpen"/>.</item>
    ///   <item>Handle закрывается автоматически при quiescence:
    ///         <c>LeaseCount == 0 &amp;&amp; ActiveIoCount == 0</c>.</item>
    ///   <item>Все мутации состояния потокобезопасны через <see cref="_lock"/>.</item>
    /// </list>
    /// </remarks>
    private sealed class CacheFileHandle : IDisposable
    {
        private readonly string _filePath;
        private readonly Lock _lock = new();
        private SafeFileHandle? _handle;
        private int _leaseCount;
        private int _activeIoCount;
        private TaskCompletionSource? _quiescenceWaiter;

        public CacheFileHandle(string filePath) => _filePath = filePath;

        /// <summary>Количество активных lease.</summary>
        public int LeaseCount => Volatile.Read(ref _leaseCount);

        /// <summary>Количество in-flight I/O операций.</summary>
        public int ActiveIoCount => Volatile.Read(ref _activeIoCount);

        /// <summary>Handle закрыт или не открывался.</summary>
        public bool IsClosed
        {
            get { lock (_lock) return _handle is null or { IsClosed: true }; }
        }

        /// <summary>Увеличивает lease counter.</summary>
        public void AddLease()
        {
            lock (_lock) _leaseCount++;
        }

        /// <summary>Уменьшает lease counter. Закрывает handle при quiescence.</summary>
        public void RemoveLease()
        {
            lock (_lock)
            {
                if (_leaseCount > 0) _leaseCount--;
                TryCloseIfQuiescent();
            }
        }

        /// <summary>Регистрирует начало I/O операции.</summary>
        public void BeginIo() => Interlocked.Increment(ref _activeIoCount);

        /// <summary>Регистрирует завершение I/O. Закрывает handle при quiescence.</summary>
        public void EndIo()
        {
            if (Interlocked.Decrement(ref _activeIoCount) <= 0
                && Volatile.Read(ref _leaseCount) <= 0)
            {
                lock (_lock) TryCloseIfQuiescent();
            }
        }

        /// <summary>Возвращает OS handle, лениво открывая файл при необходимости.</summary>
        public SafeFileHandle GetOrOpen(FileMode mode = FileMode.OpenOrCreate)
        {
            lock (_lock)
            {
                if (_handle is { IsClosed: false })
                    return _handle;

                _handle = File.OpenHandle(
                    _filePath, mode,
                    FileAccess.ReadWrite,
                    FileShare.ReadWrite | FileShare.Delete,
                    FileOptions.Asynchronous | FileOptions.RandomAccess);

                return _handle;
            }
        }

        /// <summary>
        /// Принудительно закрывает handle, независимо от lease/I/O counters.
        /// Используется при удалении файла или dispose менеджера.
        /// </summary>
        public void ForceClose()
        {
            TaskCompletionSource? waiter;
            lock (_lock)
            {
                waiter = _quiescenceWaiter;
                _quiescenceWaiter = null;

                if (_handle is { IsClosed: false })
                {
                    try { _handle.Dispose(); } catch { }
                }
                _handle = null;
            }
            waiter?.TrySetResult();
        }

        /// <summary>
        /// Ожидает завершения всех I/O операций. Используется на dispose path.
        /// </summary>
        public Task WaitForQuiescenceAsync(int timeoutMs)
        {
            lock (_lock)
            {
                if (_leaseCount <= 0 && _activeIoCount <= 0)
                    return Task.CompletedTask;

                _quiescenceWaiter ??= new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously);
            }
            return _quiescenceWaiter.Task.WaitAsync(TimeSpan.FromMilliseconds(timeoutMs));
        }

        /// <summary>Проверяет quiescence и закрывает handle. Вызывается под lock.</summary>
        private void TryCloseIfQuiescent()
        {
            if (_leaseCount > 0 || _activeIoCount > 0) return;

            var waiter = _quiescenceWaiter;
            _quiescenceWaiter = null;

            if (_handle is { IsClosed: false })
            {
                try { _handle.Dispose(); } catch { }
                _handle = null;
            }

            waiter?.TrySetResult();
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            ForceClose();
        }
    }
}

/// <summary>
/// Метаданные range-based кэша одного аудиопотока.
/// </summary>
public sealed class AudioCacheEntry
{
    /// <summary>Уникальный ключ кэша.</summary>
    public string CacheKey { get; init; } = "";

    /// <summary>Идентификатор трека.</summary>
    public string TrackId { get; init; } = "";

    /// <summary>Исходный URL.</summary>
    public string OriginalUrl { get; set; } = "";

    /// <summary>Полный размер контента в байтах.</summary>
    public long TotalSize { get; set; }

    /// <summary>Формат аудио-контейнера.</summary>
    public AudioFormat Format { get; init; }

    /// <summary>Аудио-кодек.</summary>
    public AudioCodec Codec { get; set; }

    /// <summary>Реальный битрейт в kbps.</summary>
    public int Bitrate { get; set; }

    /// <summary>Длительность трека в миллисекундах.</summary>
    public long DurationMs { get; set; } = -1;

    /// <summary>
    /// Выравнивание диапазонов, использованное этим кэшем.
    /// </summary>
    public int AlignmentBytes { get; set; }

    /// <summary>Дата и время создания записи.</summary>
    public DateTime CreatedAt { get; init; }

    /// <summary>Дата и время последнего обращения.</summary>
    public DateTime LastAccessedAt { get; set; }

    /// <summary>Дата и время полного завершения кэширования.</summary>
    public DateTime? CompletedAt { get; set; }

    /// <summary>Флаг полной готовности локального кэша.</summary>
    public bool IsComplete { get; set; }

    /// <summary>Физический размер файла кэша на диске.</summary>
    public long ActualFileSize { get; set; }

    /// <summary>
    /// Новое canonical-поле integrated loudness трека в LUFS.
    /// </summary>
    public float? IntegratedLufs { get; set; }

    /// <summary>
    /// Источник значения <see cref="IntegratedLufs"/>.
    /// </summary>
    public int IntegratedLufsSource { get; set; }

    /// <summary>
    /// Сериализуемые диапазоны локально скачанных данных.
    /// </summary>
    public List<SerializedDownloadedRange>? DownloadedRangesData { get; set; }

    private long _downloadedBytes;

    [JsonIgnore]
    private List<CacheByteRange>? _downloadedRanges;

    [JsonIgnore]
    private readonly Lock _rangesLock = new();

    [JsonIgnore]
    private ConcurrentDictionary<long, byte>? _corruptedOfflineRanges;

    /// <summary>Точное количество байт, доступных локально.</summary>
    [JsonIgnore]
    public long DownloadedBytes => Volatile.Read(ref _downloadedBytes);

    /// <summary>Прогресс загрузки в процентах.</summary>
    [JsonIgnore]
    public double DownloadProgress =>
        TotalSize <= 0 ? 0 : Math.Min(100.0, (double)DownloadedBytes / TotalSize * 100.0);

    /// <summary>
    /// Проверяет, полностью ли покрыт диапазон <c>[offset, offset + length)</c>.
    /// </summary>
    public bool IsRangeDownloaded(long offset, long length)
    {
        if (length <= 0) return true;
        if (!NormalizeRange(offset, length, out long start, out long endExclusive))
            return false;

        lock (_rangesLock)
        {
            if (_downloadedRanges is not { Count: > 0 })
                return false;

            for (int i = 0; i < _downloadedRanges.Count; i++)
            {
                var current = _downloadedRanges[i];

                if (current.Start > start)
                    break;

                if (current.EndExclusive <= start)
                    continue;

                return current.Start <= start && current.EndExclusive >= endExclusive;
            }

            return false;
        }
    }

    /// <summary>
    /// Помечает диапазон байт загруженным.
    /// </summary>
    public void MarkRangeDownloaded(long offset, long length)
    {
        if (!NormalizeRange(offset, length, out long start, out long endExclusive))
            return;

        long addedBytes;

        lock (_rangesLock)
        {
            _downloadedRanges ??= new List<CacheByteRange>(4);

            int insertIndex = 0;
            while (insertIndex < _downloadedRanges.Count
                   && _downloadedRanges[insertIndex].EndExclusive < start)
            {
                insertIndex++;
            }

            long mergedStart = start;
            long mergedEnd = endExclusive;
            long overlapBytes = 0;
            int removeStart = insertIndex;

            while (insertIndex < _downloadedRanges.Count
                   && _downloadedRanges[insertIndex].Start <= mergedEnd)
            {
                var current = _downloadedRanges[insertIndex];

                long overlapStart = Math.Max(start, current.Start);
                long overlapEnd = Math.Min(endExclusive, current.EndExclusive);
                if (overlapStart < overlapEnd)
                    overlapBytes += overlapEnd - overlapStart;

                if (current.Start < mergedStart)
                    mergedStart = current.Start;

                if (current.EndExclusive > mergedEnd)
                    mergedEnd = current.EndExclusive;

                insertIndex++;
            }

            int removeCount = insertIndex - removeStart;
            if (removeCount > 0)
                _downloadedRanges.RemoveRange(removeStart, removeCount);

            _downloadedRanges.Insert(removeStart, new CacheByteRange(mergedStart, mergedEnd));
            addedBytes = endExclusive - start - overlapBytes;
        }

        if (addedBytes > 0)
            Interlocked.Add(ref _downloadedBytes, addedBytes);
    }

    /// <summary>
    /// Инвалидирует диапазон байт.
    /// </summary>
    public void InvalidateRange(long offset, long length)
    {
        if (!NormalizeRange(offset, length, out long start, out long endExclusive))
            return;

        long removedBytes;

        lock (_rangesLock)
        {
            if (_downloadedRanges is not { Count: > 0 })
                return;

            var updated = new List<CacheByteRange>(_downloadedRanges.Count + 1);
            removedBytes = 0;

            for (int i = 0; i < _downloadedRanges.Count; i++)
            {
                var current = _downloadedRanges[i];

                if (current.EndExclusive <= start || current.Start >= endExclusive)
                {
                    updated.Add(current);
                    continue;
                }

                long overlapStart = Math.Max(current.Start, start);
                long overlapEnd = Math.Min(current.EndExclusive, endExclusive);
                if (overlapStart < overlapEnd)
                    removedBytes += overlapEnd - overlapStart;

                if (current.Start < start)
                    updated.Add(new CacheByteRange(current.Start, start));

                if (current.EndExclusive > endExclusive)
                    updated.Add(new CacheByteRange(endExclusive, current.EndExclusive));
            }

            _downloadedRanges = updated.Count == 0 ? null : updated;
        }

        if (removedBytes > 0)
            Interlocked.Add(ref _downloadedBytes, -removedBytes);
    }

    /// <summary>
    /// Сбрасывает все локально скачанные диапазоны.
    /// </summary>
    public void ResetDownloadedRanges()
    {
        lock (_rangesLock)
            _downloadedRanges = null;

        DownloadedRangesData = null;
        Volatile.Write(ref _downloadedBytes, 0);
    }

    /// <summary>
    /// Помечает весь файл полностью скачанным.
    /// </summary>
    public void MarkFullyDownloaded()
    {
        lock (_rangesLock)
        {
            _downloadedRanges = TotalSize > 0
                ? new List<CacheByteRange>(1) { new CacheByteRange(0, TotalSize) }
                : null;
        }

        DownloadedRangesData = TotalSize > 0
            ? new List<SerializedDownloadedRange>(1) { new() { Start = 0, EndExclusive = TotalSize } }
            : null;

        Volatile.Write(ref _downloadedBytes, Math.Max(0, TotalSize));
    }

    /// <summary>
    /// Пытается вернуть непрерывный диапазон, содержащий указанную позицию.
    /// </summary>
    internal bool TryGetContainingRange(long offset, out long start, out long endExclusive)
    {
        if (offset < 0 || offset >= TotalSize)
        {
            start = 0;
            endExclusive = 0;
            return false;
        }

        lock (_rangesLock)
        {
            if (_downloadedRanges is not { Count: > 0 })
            {
                start = 0;
                endExclusive = 0;
                return false;
            }

            for (int i = 0; i < _downloadedRanges.Count; i++)
            {
                var current = _downloadedRanges[i];

                if (current.Start > offset)
                    break;

                if (current.EndExclusive <= offset)
                    continue;

                start = current.Start;
                endExclusive = current.EndExclusive;
                return true;
            }
        }

        start = 0;
        endExclusive = 0;
        return false;
    }

    /// <summary>
    /// Возвращает количество непрерывно доступных байт вперёд от позиции.
    /// </summary>
    public long GetContiguousDownloadedBytesFrom(long offset)
    {
        return TryGetContainingRange(offset, out _, out long endExclusive)
            ? endExclusive - offset
            : 0;
    }

    /// <summary>
    /// Возвращает snapshot скачанных диапазонов.
    /// </summary>
    internal CacheByteRange[] GetDownloadedRangesSnapshot()
    {
        lock (_rangesLock)
        {
            if (_downloadedRanges is not { Count: > 0 })
                return [];

            return [.. _downloadedRanges];
        }
    }

    /// <summary>
    /// Помечает выровненный диапазон как повреждённый в текущей оффлайн-сессии.
    /// </summary>
    public void MarkRangeCorruptedOffline(long alignedStart)
    {
        _corruptedOfflineRanges ??= new ConcurrentDictionary<long, byte>();
        _corruptedOfflineRanges.TryAdd(alignedStart, 1);
    }

    /// <summary>
    /// Проверяет, был ли выровненный диапазон помечен как повреждённый в оффлайне.
    /// </summary>
    public bool IsRangeCorruptedOffline(long alignedStart) =>
        _corruptedOfflineRanges != null && _corruptedOfflineRanges.ContainsKey(alignedStart);

    /// <summary>
    /// Подготавливает сериализуемое состояние перед сохранением индекса.
    /// </summary>
    public void PrepareForSave()
    {
        lock (_rangesLock)
        {
            if (_downloadedRanges is not { Count: > 0 })
            {
                DownloadedRangesData = null;
                return;
            }

            var data = new List<SerializedDownloadedRange>(_downloadedRanges.Count);
            for (int i = 0; i < _downloadedRanges.Count; i++)
            {
                data.Add(new SerializedDownloadedRange
                {
                    Start = _downloadedRanges[i].Start,
                    EndExclusive = _downloadedRanges[i].EndExclusive
                });
            }

            DownloadedRangesData = data;
        }
    }

    /// <summary>
    /// Восстанавливает runtime-состояние после загрузки из JSON.
    /// </summary>
    public void RestoreAfterLoad()
    {
        lock (_rangesLock)
            _downloadedRanges = null;

        Volatile.Write(ref _downloadedBytes, 0);

        if (DownloadedRangesData is not { Count: > 0 })
            return;

        for (int i = 0; i < DownloadedRangesData.Count; i++)
        {
            var range = DownloadedRangesData[i];
            MarkRangeDownloaded(range.Start, range.EndExclusive - range.Start);
        }
    }

    private bool NormalizeRange(long offset, long length, out long start, out long endExclusive)
    {
        if (length <= 0 || offset < 0 || offset >= TotalSize)
        {
            start = 0;
            endExclusive = 0;
            return false;
        }

        start = offset;
        endExclusive = offset + length;

        if (endExclusive <= start)
        {
            endExclusive = 0;
            start = 0;
            return false;
        }

        if (endExclusive > TotalSize)
            endExclusive = TotalSize;

        return endExclusive > start;
    }
}

/// <summary>
/// Сериализуемый диапазон локально скачанных данных.
/// </summary>
public sealed class SerializedDownloadedRange
{
    /// <summary>Начало диапазона включительно.</summary>
    public long Start { get; set; }

    /// <summary>Конец диапазона исключительно.</summary>
    public long EndExclusive { get; set; }
}

internal readonly record struct CacheByteRange(long Start, long EndExclusive);

public readonly struct CacheStats
{
    public int TotalEntries { get; init; }
    public int CompleteEntries { get; init; }
    public int PartialEntries { get; init; }
    public long TotalSizeBytes { get; init; }
    public long MaxSizeBytes { get; init; }

    public double UsagePercent =>
        MaxSizeBytes == 0 ? 0 : (double)TotalSizeBytes / MaxSizeBytes * 100;

    public string TotalSizeFormatted => FormatSize(TotalSizeBytes);
    public string MaxSizeFormatted => FormatSize(MaxSizeBytes);

    private static string FormatSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / 1024.0 / 1024.0:F1} MB";
        return $"{bytes / 1024.0 / 1024.0 / 1024.0:F2} GB";
    }
}