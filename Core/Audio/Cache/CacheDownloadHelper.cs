using System.Buffers;
using System.Net.Http.Headers;
using LMP.Core.Audio.Http;
using LMP.Core.Audio.Interfaces;

namespace LMP.Core.Audio.Cache;

/// <summary>
/// Lightweight gap-fill downloader для <see cref="AudioCacheManager"/>.
/// Скачивает только недостающие диапазоны (gaps) без создания <see cref="Sources.CachingStreamSource"/>,
/// парсеров контейнеров и ring buffers.
/// </summary>
/// <remarks>
/// <para>Thread-safe: использует <see cref="AudioCacheManager.WriteRangeAsync"/>
/// с внутренним file-lock и I/O tracking.</para>
/// <para>Idempotent: повторная запись уже скачанного диапазона — safe no-op
/// (overlap обрабатывается <see cref="AudioCacheEntry.MarkRangeDownloaded"/>).</para>
/// <para>Safe для параллельной работы с активным <see cref="Sources.CachingStreamSource"/>:
/// оба writer'а сериализуются через <c>_fileLocks</c> semaphore,
/// overlap-writes не ломают данные (тот же itag = те же байты).</para>
/// </remarks>
public static class CacheDownloadHelper
{
    /// <summary>
    /// Размер chunk для HTTP range requests.
    /// Баланс между overhead (много requests) и loss при обрыве (большой chunk).
    /// </summary>
    private const int ChunkSize = 512 * 1024;

    /// <summary>
    /// Гарантирует полное кэширование трека, скачивая только недостающие диапазоны (gaps).
    /// </summary>
    /// <param name="descriptor">Дескриптор resolved потока с live URL.</param>
    /// <param name="httpClient">HTTP-клиент с общим connection pool.</param>
    /// <param name="cacheManager">Менеджер дискового кэша.</param>
    /// <param name="progress">
    /// Callback прогресса (0.0–1.0) относительно gap bytes.
    /// Если трек на 80% закэширован, progress отражает заполнение оставшихся 20%.
    /// </param>
    /// <param name="ct">Токен отмены.</param>
    /// <returns><c>true</c>, если трек полностью закэширован; иначе <c>false</c>.</returns>
    /// <exception cref="ArgumentException">Дескриптор не содержит live URL.</exception>
    public static async Task<bool> EnsureFullyCachedAsync(
        ResolvedStreamDescriptor descriptor,
        HttpClient httpClient,
        AudioCacheManager cacheManager,
        IProgress<float>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(cacheManager);

        if (!descriptor.HasLiveUrl)
            throw new ArgumentException("Descriptor must have a live URL for gap-fill.", nameof(descriptor));

        if (descriptor.ContentLengthBytes <= 0)
            throw new ArgumentException("Descriptor must have a positive content length.", nameof(descriptor));

        string cacheKey = AudioSourceFactory.BuildCacheKey(
            descriptor.TrackId, descriptor.Format, descriptor.BitrateKbps);

        return await EnsureFullyCachedCoreAsync(
            cacheKey,
            descriptor.TrackId,
            descriptor.Url,
            descriptor.ContentLengthBytes,
            descriptor.Format,
            descriptor.Codec,
            descriptor.BitrateKbps,
            httpClient,
            cacheManager,
            progress,
            ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Core: создаёт/обновляет entry, вычисляет gaps, скачивает через HTTP range, пишет в кэш.
    /// </summary>
    private static async Task<bool> EnsureFullyCachedCoreAsync(
        string cacheKey,
        string trackId,
        string url,
        long contentLength,
        AudioFormat format,
        AudioCodec codec,
        int bitrate,
        HttpClient httpClient,
        AudioCacheManager cacheManager,
        IProgress<float>? progress,
        CancellationToken ct)
    {
        var entry = cacheManager.CreateOrUpdate(
            cacheKey, trackId, url, contentLength, format, codec, bitrate);

        if (entry.IsComplete)
            return true;

        var ranges = entry.GetDownloadedRangesSnapshot();
        var gaps = ComputeGaps(ranges, contentLength);

        if (gaps.Count == 0)
        {
            if (entry.DownloadedBytes >= entry.TotalSize && !entry.IsComplete)
            {
                entry.MarkFullyDownloaded();
                entry.IsComplete = true;
                entry.CompletedAt = DateTime.UtcNow;
                entry.ActualFileSize = Math.Max(entry.ActualFileSize, entry.TotalSize);
            }

            return entry.IsComplete;
        }

        long totalGapBytes = 0;
        for (int i = 0; i < gaps.Count; i++)
            totalGapBytes += gaps[i].Length;

        long downloadedGapBytes = 0;
        byte[] buffer = ArrayPool<byte>.Shared.Rent(ChunkSize);

        try
        {
            for (int gapIdx = 0; gapIdx < gaps.Count; gapIdx++)
            {
                ct.ThrowIfCancellationRequested();

                long gapOffset = gaps[gapIdx].Start;
                long gapRemaining = gaps[gapIdx].Length;

                while (gapRemaining > 0)
                {
                    ct.ThrowIfCancellationRequested();

                    int requestSize = (int)Math.Min(ChunkSize, gapRemaining);
                    long requestEnd = gapOffset + requestSize - 1;

                    using var request = CreateRangeRequest(url, gapOffset, requestEnd);
                    using var response = await httpClient.SendAsync(
                        request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

                    if (response.StatusCode == System.Net.HttpStatusCode.Forbidden)
                    {
                        Log.Warn($"[CacheDownloadHelper] HTTP 403 for range {gapOffset}-{requestEnd}. URL expired.");
                        return false;
                    }

                    if (response.StatusCode == System.Net.HttpStatusCode.RequestedRangeNotSatisfiable)
                    {
                        Log.Warn($"[CacheDownloadHelper] HTTP 416 for range {gapOffset}-{requestEnd}. Content length mismatch.");
                        return false;
                    }

                    response.EnsureSuccessStatusCode();

                    int totalRead = await ReadStreamToBufferAsync(
                        response, buffer, requestSize, ct).ConfigureAwait(false);

                    if (totalRead == 0)
                    {
                        Log.Warn($"[CacheDownloadHelper] Empty response for range {gapOffset}-{requestEnd}");
                        return false;
                    }

                    await cacheManager.WriteRangeAsync(
                        cacheKey, gapOffset, buffer.AsMemory(0, totalRead), ct).ConfigureAwait(false);

                    downloadedGapBytes += totalRead;
                    gapOffset += totalRead;
                    gapRemaining -= totalRead;

                    float progressValue = totalGapBytes > 0
                        ? Math.Min((float)downloadedGapBytes / totalGapBytes, 1.0f)
                        : 1.0f;
                    progress?.Report(progressValue);
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        var finalEntry = cacheManager.GetCacheInfo(cacheKey);
        return finalEntry?.IsComplete ?? false;
    }

    /// <summary>
    /// Читает HTTP response stream в арендованный буфер.
    /// </summary>
    private static async ValueTask<int> ReadStreamToBufferAsync(
        HttpResponseMessage response,
        byte[] buffer,
        int expectedLength,
        CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);

        int totalRead = 0;
        while (totalRead < expectedLength)
        {
            int read = await stream.ReadAsync(
                buffer.AsMemory(totalRead, expectedLength - totalRead), ct).ConfigureAwait(false);
            if (read == 0) break;
            totalRead += read;
        }

        return totalRead;
    }

    /// <summary>
    /// Вычисляет незакэшированные диапазоны (gaps) на основе snapshot скачанных ranges.
    /// </summary>
    private static List<(long Start, long Length)> ComputeGaps(CacheByteRange[] ranges, long totalSize)
    {
        var gaps = new List<(long Start, long Length)>();
        long scanPos = 0;

        for (int i = 0; i < ranges.Length; i++)
        {
            if (ranges[i].Start > scanPos)
                gaps.Add((scanPos, ranges[i].Start - scanPos));

            if (ranges[i].EndExclusive > scanPos)
                scanPos = ranges[i].EndExclusive;
        }

        if (scanPos < totalSize)
            gaps.Add((scanPos, totalSize - scanPos));

        return gaps;
    }

    /// <summary>
    /// Создаёт HTTP range request с корректным User-Agent для YouTube CDN.
    /// </summary>
    private static HttpRequestMessage CreateRangeRequest(string url, long start, long end)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Range = new RangeHeaderValue(start, end);
        SharedHttpClient.ApplyUserAgentFromUrl(request, url);
        return request;
    }
}