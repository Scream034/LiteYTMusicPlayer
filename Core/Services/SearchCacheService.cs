using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LMP.Core.Services;

public sealed class CachedSearchResult
{
    public string Query { get; set; } = "";
    public string Source { get; set; } = "";
    public DateTime CachedAt { get; set; }
    public List<TrackInfo> Tracks { get; set; } = [];
}

public sealed class SearchCacheService
{
    private readonly LibraryService _libraryService;
    private readonly int _maxCacheFiles = 50;
    private readonly SemaphoreSlim _lock = new(1, 1);

    // In-memory LRU cache
    private readonly Dictionary<string, CachedSearchResult> _memoryCache = [];
    private readonly LinkedList<string> _lruOrder = new();
    private const int MaxMemoryCacheItems = 15;


    private TimeSpan CacheTtl => TimeSpan.FromMinutes(
        _libraryService.Settings.SearchCacheTtlMinutes > 0
            ? _libraryService.Settings.SearchCacheTtlMinutes
            : 60);

    public SearchCacheService(LibraryService libraryService)
    {
        _libraryService = libraryService;
        _ = Task.Run(CleanupOldCacheAsync);
    }

    /// <summary>
    /// Получить из кэша.
    /// </summary>
    public async Task<List<TrackInfo>?> GetAsync(string query, SearchSource source, int minCount = 10)
    {
        if (string.IsNullOrWhiteSpace(query)) return null;

        var key = GetCacheKey(query, source);
        var ttl = CacheTtl;

        // Memory Check
        lock (_memoryCache)
        {
            if (_memoryCache.TryGetValue(key, out var memResult))
            {
                var age = DateTime.UtcNow - memResult.CachedAt;
                if (age < ttl && memResult.Tracks.Count >= minCount)
                {
                    TouchLruUnsafe(key);
                    Log.Debug($"[SearchCache] Memory hit: '{query}' ({source}), {memResult.Tracks.Count} items");
                    return [.. memResult.Tracks];
                }
                if (age >= ttl)
                {
                    RemoveFromMemoryUnsafe(key);
                }
            }
        }

        // Disk Check
        await _lock.WaitAsync();
        try
        {
            var filePath = GetFilePath(key);
            if (!File.Exists(filePath)) return null;

            var (json, _) = await AtomicFile.ReadTextWithFallbackAsync(filePath).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(json)) return null;

            var cached = JsonSerializer.Deserialize(json, AppJsonContext.Default.CachedSearchResult);

            if (cached == null) return null;

            var age = DateTime.UtcNow - cached.CachedAt;
            if (age > ttl)
            {
                TryDeleteFile(filePath);
                return null;
            }

            if (cached.Tracks.Count < minCount) return null;

            AddToMemoryCache(key, cached);
            Log.Debug($"[SearchCache] Disk hit: '{query}' ({source}), {cached.Tracks.Count} items");
            return [.. cached.Tracks];
        }
        catch (Exception ex)
        {
            Log.Error($"[SearchCache] Read error: {ex.Message}");
            return null;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Сохранить в кэш.
    /// </summary>
    public async Task SetAsync(string query, SearchSource source, List<TrackInfo> tracks)
    {
        if (string.IsNullOrWhiteSpace(query) || tracks.Count == 0) return;

        var key = GetCacheKey(query, source);
        var cached = new CachedSearchResult
        {
            Query = query,
            Source = source.ToCacheKey(),
            CachedAt = DateTime.UtcNow,
            Tracks = [.. tracks]
        };

        AddToMemoryCache(key, cached);

        await _lock.WaitAsync();
        try
        {
            var filePath = GetFilePath(key);
            var json = JsonSerializer.Serialize(cached, AppJsonContext.Default.CachedSearchResult);
            await AtomicFile.WriteTextAsync(filePath, json, createBackup: false).ConfigureAwait(false);
            Log.Debug($"[SearchCache] Stored: '{query}' ({source}), {tracks.Count} items");
        }
        catch (Exception ex)
        {
            Log.Warn($"[SearchCache] Write error: {ex.Message}");
        }
        finally
        {
            _lock.Release();
        }
    }

    public void InvalidateQuery(string query, SearchSource source)
    {
        if (string.IsNullOrWhiteSpace(query)) return;

        var key = GetCacheKey(query, source);
        RemoveFromMemory(key);

        try
        {
            var filePath = GetFilePath(key);
            TryDeleteFile(filePath);
            Log.Debug($"[SearchCache] Invalidated: '{query}' ({source})");
        }
        catch { }
    }

    private void AddToMemoryCache(string key, CachedSearchResult result)
    {
        lock (_memoryCache)
        {
            if (_memoryCache.TryGetValue(key, out _))
            {
                _memoryCache[key] = result;
                TouchLruUnsafe(key);
            }
            else
            {
                while (_memoryCache.Count >= MaxMemoryCacheItems && _lruOrder.Count > 0)
                {
                    var oldest = _lruOrder.Last!.Value;
                    _lruOrder.RemoveLast();
                    _memoryCache.Remove(oldest);
                }

                _memoryCache[key] = result;
                _lruOrder.AddFirst(key);
            }
        }
    }

    private void TouchLruUnsafe(string key)
    {
        _lruOrder.Remove(key);
        _lruOrder.AddFirst(key);
    }

    private void RemoveFromMemory(string key)
    {
        lock (_memoryCache)
        {
            RemoveFromMemoryUnsafe(key);
        }
    }

    private void RemoveFromMemoryUnsafe(string key)
    {
        _memoryCache.Remove(key);
        _lruOrder.Remove(key);
    }

    private async Task CleanupOldCacheAsync()
    {
        try
        {
            EnsureCacheDirectoryExists();

            var ttl = CacheTtl;
            var files = Directory.GetFiles(G.Folder.SearchCache, "*.json")
                .Select(static f => new FileInfo(f))
                .OrderByDescending(static f => f.LastWriteTimeUtc)
                .ToList();

            int deletedCount = 0;

            foreach (var file in files.Skip(_maxCacheFiles))
            {
                TryDeleteFile(file.FullName);
                deletedCount++;
            }

            foreach (var file in files.Take(_maxCacheFiles))
            {
                if (DateTime.UtcNow - file.LastWriteTimeUtc > ttl)
                {
                    TryDeleteFile(file.FullName);
                    deletedCount++;
                }
            }

            if (deletedCount > 0)
            {
                Log.Info($"[SearchCache] Cleanup: deleted {deletedCount} files");
            }
        }
        catch (Exception ex)
        {
            Log.Error($"[SearchCache] Cleanup error: {ex.Message}");
        }

        await Task.CompletedTask;
    }

    /// <summary>
    /// Принудительная очистка просроченных записей.
    /// Вызывается при изменении TTL в настройках.
    /// </summary>
    public async Task CleanupExpiredAsync()
    {
        await CleanupOldCacheAsync();

        // Также чистим память
        var ttl = CacheTtl;
        var now = DateTime.UtcNow;

        lock (_memoryCache)
        {
            var expiredKeys = _memoryCache
                .Where(kv => now - kv.Value.CachedAt > ttl)
                .Select(kv => kv.Key)
                .ToList();

            foreach (var key in expiredKeys)
            {
                RemoveFromMemoryUnsafe(key);
            }

            if (expiredKeys.Count > 0)
            {
                Log.Info($"[SearchCache] Cleaned {expiredKeys.Count} expired memory entries");
            }
        }
    }

    private static string GetCacheKey(string query, SearchSource source)
    {
        var normalizedQuery = query.ToLowerInvariant().Trim();
        var sourceKey = source.ToCacheKey();
        var rawKey = $"{normalizedQuery}|{sourceKey}";
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(rawKey));
        return Convert.ToHexString(bytes)[..16];
    }

    private static string GetFilePath(string key) =>
        Path.Combine(G.Folder.SearchCache, $"{key}.json");

    private static void EnsureCacheDirectoryExists()
    {
        if (!Directory.Exists(G.Folder.SearchCache))
            Directory.CreateDirectory(G.Folder.SearchCache);
    }

    private static void TryDeleteFile(string filePath)
    {
        try { if (File.Exists(filePath)) File.Delete(filePath); }
        catch { }
    }

    public void ClearAll()
    {
        lock (_memoryCache)
        {
            _memoryCache.Clear();
            _lruOrder.Clear();
        }

        try
        {
            EnsureCacheDirectoryExists();
            foreach (var file in Directory.GetFiles(G.Folder.SearchCache, "*.json"))
                TryDeleteFile(file);

            Log.Info("[SearchCache] Cleared all cache");
        }
        catch (Exception ex)
        {
            Log.Error($"[SearchCache] Clear error: {ex.Message}");
        }
    }

    public (int MemoryItems, int DiskItems, long DiskSizeBytes, int TtlMinutes) GetStats()
    {
        int memCount;
        lock (_memoryCache) { memCount = _memoryCache.Count; }

        EnsureCacheDirectoryExists();
        var files = Directory.GetFiles(G.Folder.SearchCache, "*.json");
        long size = files.Sum(static f => new FileInfo(f).Length);
        int ttl = (int)CacheTtl.TotalMinutes;
        return (memCount, files.Length, size, ttl);
    }
}