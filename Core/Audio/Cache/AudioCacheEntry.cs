using System.Collections.Concurrent;
using System.Text.Json.Serialization;
using LMP.Core.Audio.Interfaces;

namespace LMP.Core.Audio.Cache;

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