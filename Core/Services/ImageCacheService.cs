using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Avalonia.Media.Imaging;

namespace LMP.Core.Services;

public enum ImageQuality
{
    Low = 120,
    Medium = 200,
    High = 400,
    Ultra = 800
}

/// <summary>
/// Ультра-оптимизированный сервис кэширования изображений.
/// Direct-to-Disk + атомарные операции + Lock-Free LRU.
///
/// <para><b>Архитектура памяти:</b></para>
/// <list type="bullet">
///   <item>Memory cache: Dictionary{ulong} (под lock) + LinkedList для O(1) LRU.
///     Ключ — ulong FNV-1a hash: zero-alloc lookup, 8 байт vs 40+ байт string.</item>
///   <item>ConcurrentDictionary убран: все обращения к нему шли под _lruLock
///     (двойная синхронизация = чистые потери).</item>
///   <item>Disk cache: прямая запись через FileStream без MemoryStream.</item>
///   <item>Deduplication: Lazy&lt;Task&gt; с фиксом AddRef bug.</item>
/// </list>
/// </summary>
public sealed class ImageCacheService : IDisposable
{
    private readonly HttpClient _http;
    private readonly LibraryService _library;
    private readonly SemaphoreSlim _downloadSemaphore = new(6);

    /// <summary>
    /// Memory cache. Ключ — FNV-1a hash (ulong), не строка.
    /// Zero-alloc lookup на hot path: hash вычисляется арифметически,
    /// Dictionary{ulong} использует GetHashCode() = (int)key, без boxing.
    /// Все операции — под _lruLock.
    /// </summary>
    private readonly Dictionary<ulong, RefCountedBitmap> _memoryCache = [];
    private readonly LinkedList<ulong> _lruOrder = new();
    private readonly Dictionary<ulong, LinkedListNode<ulong>> _lruIndex = [];
    private readonly Lock _lruLock = new();

    /// <summary>
    /// Дедупликация параллельных загрузок одного URL.
    /// Lazy гарантирует, что фабрика вызовется ровно один раз при конкурентном доступе.
    /// Ключ — ulong hash.
    /// </summary>
    private readonly ConcurrentDictionary<ulong, Lazy<Task<Bitmap?>>> _pendingLoads = [];

    private long _currentDiskCacheBytes;
    private long _currentMemoryCacheBytes;
    private bool _isDisposed;
    private int _loadCounter;
    private const int CleanupInterval = 50;

    /// <summary>
    /// Минимальный размер кэша покрывает два экрана треков (~20 видимых × 2) + буфер.
    /// При Low quality (120px): 80 × 57KB ≈ 4.5MB — разумный дефолт.
    /// Настраивается через Settings.Storage.MaxBitmapCacheItems.
    /// </summary>
    private int MaxMemoryItems => _library.Settings.Storage.MaxBitmapCacheItems > 0
        ? _library.Settings.Storage.MaxBitmapCacheItems
        : 80;

    // Реальный лимит в байтах:
    // Low (120px):  120×120×4 = 57.6KB × 25 items ≈ 1.4MB — правильно
    // High (400px): 400×400×4 = 640KB × 25 items ≈ 16MB — нужно учитывать
    private long MaxMemoryBytes => MaxMemoryItems * 400L * 400 * 4; // запас для High качества

    public ImageCacheService(LibraryService library)
    {
        _library = library;
        _http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(8),
            DefaultRequestHeaders = { { "User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) LMP/1.0" } }
        };

        if (!Directory.Exists(G.Folder.ImageCache))
            Directory.CreateDirectory(G.Folder.ImageCache);

        _ = Task.Run(InitializeDiskCacheAsync);
    }

    public Task<Bitmap?> GetImageAsync(string url, ImageQuality quality = ImageQuality.Low, CancellationToken ct = default)
        => GetImageAsync(url, (int)quality, ct);

    public async Task<Bitmap?> GetImageAsync(string url, int decodeWidth, CancellationToken ct = default)
    {
        if (_isDisposed || string.IsNullOrEmpty(url)) return null;

        var memKey = ComputeMemoryKeyHash(url, decodeWidth);

        // 1. Hot path: Memory cache
        lock (_lruLock)
        {
            if (_memoryCache.TryGetValue(memKey, out var cached))
            {
                TouchLruUnsafe(memKey);
                return cached.Bitmap; // ← просто возвращаем, без AddRef
            }
        }

        // 2. Cold path: дедупликация через Lazy<Task>
        var lazyTask = _pendingLoads.GetOrAdd(
            memKey,
            static (k, state) => new Lazy<Task<Bitmap?>>(() =>
                state.self.LoadImageInternalAsync(state.url, k, state.decodeWidth, state.ct)),
            (self: this, url, decodeWidth, ct));

        try
        {
            var bitmap = await lazyTask.Value.ConfigureAwait(false);
            // ← убрать блок AddRef для вызывающего — кэш держит единственный ref
            return bitmap;
        }
        catch { return null; }
        finally
        {
            _pendingLoads.TryRemove(memKey, out _);

            if (Interlocked.Increment(ref _loadCounter) % CleanupInterval == 0)
                _ = Task.Run(PerformMaintenanceAsync, CancellationToken.None);
        }
    }

    /// <summary>
    /// Предзагружает изображения на диск без декодирования в оперативную память.
    /// Полезно для предварительного кэширования обложек (например, топ-10 результатов поиска).
    /// </summary>
    public async Task PrefetchAsync(IEnumerable<string> urls, CancellationToken ct = default)
    {
        if (_isDisposed) return;

        // Фильтрация "уже есть на диске" происходит внутри EnsureDiskCachedAsync с double-check.
        var candidates = urls
            .Where(static u => !string.IsNullOrEmpty(u))
            .Distinct(StringComparer.Ordinal)
            .Take(8)
            .Select(u => EnsureDiskCachedAsync(u, ct));

        try { await Task.WhenAll(candidates).ConfigureAwait(false); }
        catch { /* Ошибки prefetch тихо игнорируются — это фоновая оптимизация */ }
    }

    /// <summary>
    /// Скачивает файл на диск, если его там ещё нет. В RAM ничего не задерживается.
    /// </summary>
    private async Task EnsureDiskCachedAsync(string url, CancellationToken ct)
    {
        var diskHash = ComputeDiskKeyHash(url);
        var diskPath = Path.Combine(G.Folder.ImageCache, diskHash.ToString("X16"));

        if (File.Exists(diskPath)) return;

        try
        {
            await _downloadSemaphore.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (!File.Exists(diskPath))
                    await DownloadDirectToDiskAsync(url, diskPath, ct);
            }
            finally { _downloadSemaphore.Release(); }
        }
        catch { }
    }

    private async Task<Bitmap?> LoadImageInternalAsync(string url, ulong memKey, int decodeWidth, CancellationToken ct)
    {
        // Строка для имени файла создаётся только здесь (cold path, cache miss)
        var diskHash = ComputeDiskKeyHash(url);
        var diskPath = Path.Combine(G.Folder.ImageCache, diskHash.ToString("X16"));

        if (!File.Exists(diskPath))
        {
            await _downloadSemaphore.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (!File.Exists(diskPath))
                    await DownloadDirectToDiskAsync(url, diskPath, ct).ConfigureAwait(false);
            }
            catch { return null; }
            finally { _downloadSemaphore.Release(); }
        }

        if (!File.Exists(diskPath)) return null;

        // LowQuality для thumbnail (44×44): визуально идентично MediumQuality,
        // но decoder пропускает bicubic filter pass — ~30% быстрее decode.
        var bitmap = await Task.Run(() =>
        {
            try
            {
                using var stream = File.OpenRead(diskPath);
                return decodeWidth > 0
                    ? Bitmap.DecodeToWidth(stream, decodeWidth, BitmapInterpolationMode.LowQuality)
                    : new Bitmap(stream);
            }
            catch (Exception ex)
            {
                Log.Debug($"[ImageCache] Decode failed: {ex.Message}");
                try { File.Delete(diskPath); } catch { }
                return null;
            }
        }, ct).ConfigureAwait(false);

        if (bitmap != null && !ct.IsCancellationRequested)
        {
            AddToMemoryCache(memKey, bitmap);
            return bitmap;
        }

        return null;
    }

    /// <summary>
    /// Скачивание напрямую в файл без MemoryStream.
    /// Атомарное переименование (.tmp → final) для потокобезопасности.
    /// </summary>
    private async Task DownloadDirectToDiskAsync(string url, string finalPath, CancellationToken ct)
    {
        var tmpPath = finalPath + ".tmp";

        try
        {
            using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return;

            await using (var fs = new FileStream(tmpPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
            await using (var net = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
            {
                await net.CopyToAsync(fs, ct).ConfigureAwait(false);
            }

            File.Move(tmpPath, finalPath, true);
            Interlocked.Add(ref _currentDiskCacheBytes, new FileInfo(finalPath).Length);
        }
        catch
        {
            try { if (File.Exists(tmpPath)) File.Delete(tmpPath); } catch { }
            throw;
        }
    }

    /// <summary>
    /// Добавляет bitmap в memory cache с LRU eviction.
    /// 
    /// <para><b>Memory pressure:</b> GC.AddMemoryPressure вызывается ПОСЛЕ
    /// успешной вставки. При дубликате — bitmap диспозится без pressure,
    /// т.к. оригинал уже учтён.</para>
    /// </summary>
    private void AddToMemoryCache(ulong key, Bitmap bitmap)
    {
        var entry = new RefCountedBitmap(bitmap);
        long estimatedBytes = entry.EstimatedBytes;

        lock (_lruLock)
        {
            while ((_memoryCache.Count >= MaxMemoryItems ||
                    _currentMemoryCacheBytes + estimatedBytes > MaxMemoryBytes)
                   && _lruOrder.Last != null)
            {
                EvictLastUnsafe();
            }

            if (!_memoryCache.ContainsKey(key))
            {
                _memoryCache[key] = entry;
                var node = _lruOrder.AddFirst(key);
                _lruIndex[key] = node;
                Interlocked.Add(ref _currentMemoryCacheBytes, estimatedBytes);
                GC.AddMemoryPressure(estimatedBytes);
            }
            else
            {
                entry.Dispose();
            }
        }
    }

    /// <summary>
    /// Вызывается с уже захваченным _lruLock.
    /// Перемещает ключ в начало LRU. O(1).
    /// </summary>
    private void TouchLruUnsafe(ulong key)
    {
        if (_lruIndex.TryGetValue(key, out var node))
        {
            _lruOrder.Remove(node);
            _lruOrder.AddFirst(node);
        }
    }

    /// <summary>
    /// Вызывается с уже захваченным _lruLock.
    /// Удаляет хвостовой элемент LRU. O(1).
    ///
    /// <para><b>ВАЖНО:</b> Bitmap НЕ диспозится намеренно.
    /// ImageCacheService возвращает Bitmap напрямую в Image.Source.
    /// Image не имеет механизма release при смене Source → кэш не знает
    /// когда bitmap перестал использоваться UI-слоем.
    /// Bitmap будет собран GC когда Image.Source сменится и
    /// последняя ссылка исчезнет.</para>
    ///
    /// <para>GC.RemoveMemoryPressure вызывается: кэш больше не отвечает
    /// за эту память, даже если Image.Source всё ещё держит bitmap.</para>
    /// </summary>
    private void EvictLastUnsafe()
    {
        var lastNode = _lruOrder.Last!;
        var key = lastNode.Value;
        _lruOrder.RemoveLast();
        _lruIndex.Remove(key);

        if (_memoryCache.Remove(key, out var removed))
        {
            var bytes = removed.EstimatedBytes;
            Interlocked.Add(ref _currentMemoryCacheBytes, -bytes);
            GC.RemoveMemoryPressure(bytes);
            // ← removed.Dispose() УБРАН: bitmap может быть в Image.Source
        }
    }

    private async Task PerformMaintenanceAsync()
    {
        var memInfo = GC.GetGCMemoryInfo();
        if (memInfo.MemoryLoadBytes > memInfo.HighMemoryLoadThresholdBytes * 0.85)
        {
            lock (_lruLock)
            {
                int toRemove = _memoryCache.Count / 2;
                for (int i = 0; i < toRemove && _lruOrder.Last != null; i++)
                    EvictLastUnsafe();
            }
        }

        long limitBytes = (long)_library.Settings.Storage.ImageCacheLimitMb * 1024 * 1024;
        if (_currentDiskCacheBytes > limitBytes)
            await CleanupDiskCacheAsync(limitBytes).ConfigureAwait(false);
    }

    /// <summary>
    /// Очистка дискового кэша с учётом времени последнего доступа.
    /// Файлы моложе 5 минут не удаляются (могут быть в memory cache).
    /// </summary>
    private async Task CleanupDiskCacheAsync(long limitBytes)
    {
        await Task.Run(() =>
        {
            try
            {
                var files = new DirectoryInfo(G.Folder.ImageCache)
                    .GetFiles()
                    .Where(static f => !f.Extension.EndsWith(".tmp"))
                    .OrderBy(static f => f.LastAccessTimeUtc)
                    .ToList();

                var cutoff = DateTime.UtcNow.AddMinutes(-5);
                long targetSize = (long)(limitBytes * 0.7);
                long deletedBytes = 0;

                foreach (var file in files)
                {
                    if (_currentDiskCacheBytes - deletedBytes <= targetSize) break;
                    if (file.LastAccessTimeUtc > cutoff) continue;

                    try
                    {
                        var size = file.Length;
                        file.Delete();
                        deletedBytes += size;
                    }
                    catch { }
                }

                if (deletedBytes > 0)
                    Interlocked.Add(ref _currentDiskCacheBytes, -deletedBytes);
            }
            catch { }
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// Полностью очищает memory cache.
    ///
    /// <para><b>ВАЖНО:</b> Bitmap-ы не диспозятся — они могут использоваться
    /// в Image.Source контролов в данный момент. Вызов Dispose() при живых
    /// Image-контролах вызывает ObjectDisposedException в layout pass.</para>
    /// </summary>
    public void ClearMemoryCache()
    {
        long totalBytes;

        lock (_lruLock)
        {
            totalBytes = _currentMemoryCacheBytes;

            _memoryCache.Clear();
            _lruOrder.Clear();
            _lruIndex.Clear();
            Volatile.Write(ref _currentMemoryCacheBytes, 0);
        }

        if (totalBytes > 0)
            GC.RemoveMemoryPressure(totalBytes);
    }

    public async Task ClearDiskCacheAsync()
    {
        ClearMemoryCache();
        await Task.Run(() =>
        {
            foreach (var f in Directory.GetFiles(G.Folder.ImageCache))
                try { File.Delete(f); } catch { }

            Volatile.Write(ref _currentDiskCacheBytes, 0);
        });
    }

    private async Task InitializeDiskCacheAsync()
    {
        try
        {
            long total = new DirectoryInfo(G.Folder.ImageCache)
                .EnumerateFiles()
                .Sum(static f => f.Length);
            Volatile.Write(ref _currentDiskCacheBytes, total);
        }
        catch { }
    }

    #region Cache Key Hashing (FNV-1a 64-bit → ulong)

    private const ulong FnvOffsetBasis = 14695981039346656037UL;
    private const ulong FnvPrime = 1099511628211UL;

    /// <summary>
    /// FNV-1a 64-bit хеш URL. Используется как ключ disk cache.
    /// Zero-alloc: возвращает ulong, строка создаётся только для имени файла на диске (cold path).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong ComputeDiskKeyHash(ReadOnlySpan<char> url)
    {
        ulong hash = FnvOffsetBasis;
        foreach (char c in url)
        {
            hash ^= (byte)c;
            hash *= FnvPrime;
            hash ^= (byte)(c >> 8);
            hash *= FnvPrime;
        }
        return hash;
    }

    /// <summary>
    /// FNV-1a 64-bit хеш URL + нормализованная ширина. Используется как ключ memory cache.
    /// Zero-alloc: Dictionary{ulong} lookup = чистая арифметика, без string аллокаций.
    /// Нормализация ширины (120/200/400/800) уменьшает количество уникальных ключей.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong ComputeMemoryKeyHash(ReadOnlySpan<char> url, int decodeWidth)
    {
        var normalizedWidth = decodeWidth switch
        {
            <= 120 => 120,
            <= 200 => 200,
            <= 400 => 400,
            _ => 800
        };

        ulong hash = FnvOffsetBasis;

        foreach (char c in url)
        {
            hash ^= (byte)c;
            hash *= FnvPrime;
            hash ^= (byte)(c >> 8);
            hash *= FnvPrime;
        }

        // Разделитель для разграничения URL и ширины в хеш-пространстве
        hash ^= (byte)'_';
        hash *= FnvPrime;

        // Ширина побайтово — дешевле чем TryFormat + посимвольный хеш
        hash ^= (byte)(normalizedWidth & 0xFF);
        hash *= FnvPrime;
        hash ^= (byte)((normalizedWidth >> 8) & 0xFF);
        hash *= FnvPrime;

        return hash;
    }

    #endregion

    public (int MemoryItems, long MemoryMb, int DiskFiles, long DiskMb) GetStats()
    {
        int memItems;
        lock (_lruLock) memItems = _memoryCache.Count;
        return (memItems, _currentMemoryCacheBytes / 1024 / 1024, 0, _currentDiskCacheBytes / 1024 / 1024);
    }

    public void EnforceLimits() => _ = PerformMaintenanceAsync();

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        ClearMemoryCache();
        _downloadSemaphore.Dispose();
        _http.Dispose();
    }
}