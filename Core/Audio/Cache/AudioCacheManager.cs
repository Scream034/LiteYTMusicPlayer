using System.Buffers;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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
    private const int CurrentSchemaVersion = 4;

    /// <summary>
    /// Известные стандартные комбинации форматов и битрейтов YouTube для восстановления файлов-сирот.
    /// </summary>
    private static readonly (AudioFormat Format, int Bitrate, AudioCodec Codec)[] KnownFormatProfiles =
    [
        (AudioFormat.WebM, 160, AudioCodec.Opus),
        (AudioFormat.WebM, 70, AudioCodec.Opus),
        (AudioFormat.WebM, 50, AudioCodec.Opus),
        (AudioFormat.Mp4, 140, AudioCodec.Aac),
        (AudioFormat.Mp4, 128, AudioCodec.Aac),
        (AudioFormat.Ogg, 160, AudioCodec.Opus)
    ];

    /// <summary>
    /// Обёртка индекса кэша с версионированием схемы.
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

        // Self-Healing: Проверяем наличие файлов-сирот на диске для переданных треков
        bool orphansRecovered = RecoverOrphansForTracks(trackMap.Keys);

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

        if (orphansRecovered)
            _ = SaveIndexAsync();
    }

    /// <summary>
    /// Сканирует диск на наличие файлов-сирот для переданных trackId и восстанавливает их в индекс.
    /// </summary>
    /// <param name="trackIds">Список идентификаторов треков.</param>
    /// <returns><c>true</c>, если была восстановлена хотя бы одна запись.</returns>
    public bool RecoverOrphansForTracks(IEnumerable<string> trackIds)
    {
        bool anyRecovered = false;

        foreach (var rawTrackId in trackIds)
        {
            if (string.IsNullOrEmpty(rawTrackId)) continue;

            string trackId = rawTrackId.StartsWith("yt_", StringComparison.Ordinal)
                ? rawTrackId
                : string.Concat("yt_", rawTrackId);

            foreach (var (format, bitrate, codec) in KnownFormatProfiles)
            {
                string cacheKey = AudioSourceFactory.BuildCacheKey(trackId, format, bitrate);

                // Если запись уже есть в памяти и валидна — пропускаем
                if (_entries.TryGetValue(cacheKey, out var existing) && existing.IsComplete)
                    continue;

                string filePath = GetCachePath(cacheKey);

                if (!File.Exists(filePath))
                    continue;

                try
                {
                    var fi = new FileInfo(filePath);
                    // Минимальный порог размера файла (16 KB) для отсечения повреждённых огрызков
                    if (fi.Length > 16384)
                    {
                        var entry = _entries.GetOrAdd(cacheKey, _ => new AudioCacheEntry
                        {
                            CacheKey = cacheKey,
                            TrackId = trackId,
                            OriginalUrl = string.Empty,
                            TotalSize = fi.Length,
                            Format = format,
                            Codec = codec,
                            Bitrate = bitrate,
                            AlignmentBytes = ChunkSize,
                            CreatedAt = fi.CreationTimeUtc,
                            LastAccessedAt = DateTime.UtcNow,
                            CompletedAt = fi.LastWriteTimeUtc,
                            IsComplete = true,
                            ActualFileSize = fi.Length
                        });

                        entry.TotalSize = fi.Length;
                        entry.ActualFileSize = fi.Length;
                        entry.IsComplete = true;
                        entry.CompletedAt = fi.LastWriteTimeUtc;
                        entry.LastAccessedAt = DateTime.UtcNow;
                        entry.MarkFullyDownloaded();

                        AddToTrackIndex(trackId, cacheKey);
                        anyRecovered = true;

                        Log.Info($"[AudioCache] Orphaned cache file recovered: {cacheKey} ({fi.Length / 1024} KB)");
                    }
                }
                catch (Exception ex)
                {
                    Log.Warn($"[AudioCache] Failed to probe orphan file {filePath}: {ex.Message}");
                }
            }
        }

        return anyRecovered;
    }

    public bool IsFullyCached(string cacheKey) =>
        _entries.TryGetValue(cacheKey, out var entry)
        && entry.IsComplete
        && EnsureCacheFileIntegrity(entry);

    /// <summary>
    /// Возвращает лучший локальный кэш для быстрого старта partially cached трека.
    /// </summary>
    public AudioCacheEntry? FindBestStartupCache(string trackId, int minContiguousBytes) =>
        TryWithIdVariants(trackId, id => FindBestStartupCacheCore(id, minContiguousBytes));

    /// <summary>
    /// Возвращает лучший полностью закэшированный вариант трека.
    /// </summary>
    public AudioCacheEntry? FindBestCache(string trackId) =>
        TryWithIdVariants(trackId, FindBestCacheCore);

    /// <summary>
    /// Обновляет integrated loudness во ВСЕХ cache entries данного трека.
    /// </summary>
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

            if (existingSource > source)
                continue;

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
    private List<string> CollectAllCacheKeysForTrack(string trackId)
    {
        var keys = new List<string>(4);

        TryWithIdVariants<object>(trackId, id =>
        {
            AppendKeysFromIndex(id, keys);
            return null;
        });

        return keys;
    }

    /// <summary>
    /// Добавляет cacheKey из trackIndex в список.
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
    /// Применяет поиск <paramref name="find"/> к прямому trackId, raw ID и yt_-prefixed ID
    /// и возвращает первый не-null результат.
    /// </summary>
    /// <remarks>
    /// Устраняет дублирование паттерна «direct → raw → yt_ prefix» из
    /// <see cref="FindBestCache"/>, <see cref="FindBestStartupCache"/>
    /// и <see cref="CollectAllCacheKeysForTrack"/>.
    /// </remarks>
    /// <typeparam name="T">Тип результата.</typeparam>
    /// <param name="trackId">Исходный идентификатор трека.</param>
    /// <param name="find">Функция поиска по конкретному ID.</param>
    /// <returns>Первый не-null результат или <c>null</c>.</returns>
    private static T? TryWithIdVariants<T>(string trackId, Func<string, T?> find) where T : class
    {
        var result = find(trackId);
        if (result != null) return result;

        var rawId = TryGetRawTrackId(trackId);
        if (!string.IsNullOrEmpty(rawId))
        {
            result = find(rawId);
            if (result != null) return result;
        }

        if (!IsPrefixedTrackId(trackId))
            return find(string.Concat("yt_", trackId));

        return null;
    }

    /// <summary>
    /// Определяет, содержит ли идентификатор доменный префикс трека.
    /// </summary>
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
    /// </summary>
    public void AcquireLease(string cacheKey)
    {
        var entry = _fileHandles.GetOrAdd(cacheKey,
            key => new CacheFileHandle(GetCachePath(key)));
        entry.AddLease();
    }

    /// <summary>
    /// Освобождает lifetime lease на cache-файл.
    /// </summary>
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

    public bool IsFormatCached(string trackId, AudioFormat format, int bitrate) =>
        IsFullyCached(AudioSourceFactory.BuildCacheKey(trackId, format, bitrate));

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

            DeleteFilesInDirectory(new DirectoryInfo(_cacheDirectory), "AudioCache");

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
                DeleteFilesInDirectory(dir, "AudioCache");
                Log.Info("[AudioCache] Downloads cleared");
            }
            catch (Exception ex)
            {
                Log.Error($"[AudioCache] ClearDownloadsAsync error: {ex.Message}");
            }
        }, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Удаляет все файлы в директории с логированием ошибок.
    /// </summary>
    /// <param name="dir">Директория.</param>
    /// <param name="logTag">Тег для лога ошибок.</param>
    private static void DeleteFilesInDirectory(DirectoryInfo dir, string logTag)
    {
        if (!dir.Exists) return;

        foreach (var file in dir.GetFiles())
        {
            try { file.Delete(); }
            catch (Exception ex) { Log.Warn($"[{logTag}] Failed to delete {file.Name}: {ex.Message}"); }
        }
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
    private bool EnsureCacheFileIntegrity(AudioCacheEntry entry)
    {
        if (!entry.IsComplete) return false;

        var filePath = GetCachePath(entry.CacheKey);

        try
        {
            if (!IsFilePhysicallyComplete(filePath, entry.TotalSize, out long actualLength))
            {
                if (actualLength == 0)
                {
                    // Файл не существует
                    InvalidateCompleteEntry(entry);
                    return false;
                }

                // Файл усечён
                Log.Warn($"[AudioCache] ⚠ Truncated cache file: {entry.CacheKey} " +
                         $"(disk={actualLength / 1024}KB, expected={entry.TotalSize / 1024}KB)");
                InvalidateCompleteEntry(entry);

                try { File.Delete(filePath); }
                catch (Exception ex) { Log.Warn($"[AudioCache] Failed to delete truncated cache file: {ex.Message}"); }

                return false;
            }

            // Файл физически валиден — self-heal range-state при необходимости
            if (entry.DownloadedBytes < entry.TotalSize && entry.TotalSize > 0)
            {
                entry.MarkFullyDownloaded();
                entry.ActualFileSize = actualLength;
                Log.Info($"[AudioCache] Self-healed integrity: {entry.CacheKey}");
                _ = SaveIndexAsync();
            }

            return true;
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

    /// <summary>
    /// Проверяет, что файл существует и его размер не меньше ожидаемого.
    /// </summary>
    /// <param name="filePath">Путь к файлу.</param>
    /// <param name="minSize">Минимально ожидаемый размер в байтах.</param>
    /// <param name="fileLength">Фактическая длина файла на диске; 0 если не существует.</param>
    /// <returns><c>true</c> если файл существует и <c>Length &gt;= minSize</c>.</returns>
    private static bool IsFilePhysicallyComplete(string filePath, long minSize, out long fileLength)
    {
        try
        {
            var fi = new FileInfo(filePath);
            fileLength = fi.Exists ? fi.Length : 0;
            return fi.Exists && minSize > 0 && fi.Length >= minSize;
        }
        catch
        {
            fileLength = 0;
            return false;
        }
    }

    private void LoadIndex()
    {
        var indexPath = Path.Combine(_cacheDirectory, CacheMetadataFileName);
        var json = AtomicFile.ReadTextWithFallback(indexPath, out bool loadedFromBackup);

        if (string.IsNullOrWhiteSpace(json))
        {
            Log.Info("[AudioCache] Starting with fresh index (no valid index or backup found)");
            return;
        }

        try
        {
            int loadedSchemaVersion;
            List<AudioCacheEntry>? entries = null;

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
            bool needsBitrateMigration = loadedSchemaVersion < 4;
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

                if (needsLufsMigration)
                {
                    entry.IntegratedLufs = null;
                    entry.IntegratedLufsSource = 0;
                }

                if (needsBitrateMigration && !string.IsNullOrEmpty(entry.CacheKey) && entry.Bitrate > 0)
                {
                    string expectedKey = AudioSourceFactory.BuildCacheKey(entry.TrackId, entry.Format, entry.Bitrate);
                    if (!string.Equals(entry.CacheKey, expectedKey, StringComparison.Ordinal))
                    {
                        string oldPath = GetCachePath(entry.CacheKey);
                        string newPath = GetCachePath(expectedKey);

                        if (File.Exists(oldPath) && !File.Exists(newPath))
                        {
                            try { File.Move(oldPath, newPath); }
                            catch (Exception ex) { Log.Warn($"[AudioCache] Bitrate bucket migration move failed: {ex.Message}"); }
                        }

                        // Переиндексация: удаляем старый ключ, entry перезагрузится с новым
                        RemoveFromTrackIndex(entry.TrackId, entry.CacheKey);
                        entry = new AudioCacheEntry
                        {
                            CacheKey = expectedKey,
                            TrackId = entry.TrackId,
                            OriginalUrl = entry.OriginalUrl,
                            TotalSize = entry.TotalSize,
                            Format = entry.Format,
                            Codec = entry.Codec,
                            Bitrate = entry.Bitrate,
                            DurationMs = entry.DurationMs,
                            AlignmentBytes = entry.AlignmentBytes,
                            CreatedAt = entry.CreatedAt,
                            LastAccessedAt = entry.LastAccessedAt,
                            CompletedAt = entry.CompletedAt,
                            IsComplete = entry.IsComplete,
                            ActualFileSize = entry.ActualFileSize,
                            IntegratedLufs = entry.IntegratedLufs,
                            IntegratedLufsSource = entry.IntegratedLufsSource,
                            DownloadedRangesData = entry.DownloadedRangesData
                        };
                        entries[i] = entry;
                    }
                }

                entry.RestoreAfterLoad();

                if (entry.IsComplete && entry.DownloadedBytes < entry.TotalSize && entry.TotalSize > 0)
                {
                    if (IsFilePhysicallyComplete(filePath, entry.TotalSize, out long len))
                    {
                        entry.MarkFullyDownloaded();
                        entry.ActualFileSize = len;
                        migratedComplete++;
                        Log.Debug($"[AudioCache] Self-healed complete entry: {entry.CacheKey}");
                    }
                    else if (len == 0)
                    {
                        Log.Warn($"[AudioCache] Self-heal I/O error or missing file for {entry.CacheKey}");
                    }
                }

                UpdateFileSizeCache(entry);
                _entries.TryAdd(entry.CacheKey, entry);
                AddToTrackIndex(entry.TrackId, entry.CacheKey);
            }

            if (needsMigration || needsLufsMigration || needsBitrateMigration || migratedComplete > 0 || loadedFromBackup)
            {
                Log.Info($"[AudioCache] Index loaded successfully (Schema v{loadedSchemaVersion}→v{CurrentSchemaVersion}): " +
                         $"{_entries.Count} entries restored");
                _ = SaveIndexAsync();
            }

            Log.Debug($"[AudioCache] Loaded {_entries.Count} entries");
        }
        catch (Exception ex)
        {
            Log.Error($"[AudioCache] Failed to parse index: {ex.Message}");
        }
    }

    /// <summary>
    /// Атомарно сохраняет индекс кэша на диск с созданием резервной копии (.bak).
    /// </summary>
    private async Task SaveIndexAsync()
    {
        if (_disposed) return;
        if (!await _saveLock.WaitAsync(CacheSaveLockTimeoutMs).ConfigureAwait(false)) return;

        try
        {
            var json = BuildIndexJson();
            var indexPath = Path.Combine(_cacheDirectory, CacheMetadataFileName);
            await AtomicFile.WriteTextAsync(indexPath, json, createBackup: true).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Warn($"[AudioCache] Failed to atomic save index: {ex.Message}");
        }
        finally
        {
            _saveLock.Release();
        }
    }

    /// <summary>
    /// Синхронное атомарное сохранение индекса кэша для shutdown-path.
    /// </summary>
    private void SaveIndexSync()
    {
        if (_disposed) return;
        if (!_saveLock.Wait(CacheSaveLockTimeoutMs)) return;

        try
        {
            var json = BuildIndexJson();
            var indexPath = Path.Combine(_cacheDirectory, CacheMetadataFileName);
            AtomicFile.WriteText(indexPath, json, createBackup: true);
        }
        catch (Exception ex)
        {
            Log.Warn($"[AudioCache] Failed to sync atomic save index: {ex.Message}");
        }
        finally
        {
            _saveLock.Release();
        }
    }

    /// <summary>
    /// Мигрирует запись со старой chunk-based схемы на текущую range-based.
    /// </summary>
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
            if (IsFilePhysicallyComplete(filePath, entry.TotalSize, out long len))
            {
                entry.MarkFullyDownloaded();
                entry.ActualFileSize = len;
                migratedComplete++;
            }
            else
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

    /// <summary>
    /// Строит JSON индекса кэша из текущего состояния _entries.
    /// </summary>
    /// <remarks>
    /// Устраняет дублирование между <see cref="SaveIndexAsync"/> и <see cref="SaveIndexSync"/>.
    /// </remarks>
    private string BuildIndexJson()
    {
        var entries = _entries.Values.ToList();

        for (int i = 0; i < entries.Count; i++)
            entries[i].PrepareForSave();

        var envelope = new AudioCacheIndexEnvelope
        {
            SchemaVersion = CurrentSchemaVersion,
            Entries = entries
        };

        return JsonSerializer.Serialize(envelope, AppJsonContext.Default.AudioCacheIndexEnvelope);
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
    private sealed class CacheFileHandle : IDisposable
    {
        private readonly string _filePath;
        private readonly Lock _lock = new();
        private SafeFileHandle? _handle;
        private int _leaseCount;
        private int _activeIoCount;
        private TaskCompletionSource? _quiescenceWaiter;

        public CacheFileHandle(string filePath) => _filePath = filePath;

        public int LeaseCount => Volatile.Read(ref _leaseCount);
        public int ActiveIoCount => Volatile.Read(ref _activeIoCount);

        public bool IsClosed
        {
            get { lock (_lock) return _handle is null or { IsClosed: true }; }
        }

        public void AddLease()
        {
            lock (_lock) _leaseCount++;
        }

        public void RemoveLease()
        {
            lock (_lock)
            {
                if (_leaseCount > 0) _leaseCount--;
                TryCloseIfQuiescent();
            }
        }

        public void BeginIo() => Interlocked.Increment(ref _activeIoCount);

        public void EndIo()
        {
            if (Interlocked.Decrement(ref _activeIoCount) <= 0
                && Volatile.Read(ref _leaseCount) <= 0)
            {
                lock (_lock) TryCloseIfQuiescent();
            }
        }

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

        public void Dispose() => ForceClose();
    }
}