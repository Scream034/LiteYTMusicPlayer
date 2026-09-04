using System.Net;
using LMP.Core.Audio.Cache;
using LMP.Core.Audio.Http;
using LMP.Core.Audio.Interfaces;
using LMP.Core.Audio.Sources;
using static LMP.Core.Audio.AudioConstants;

namespace LMP.Core.Audio;

/// <summary>
/// Фабрика аудио источников. Определяет формат, проверяет кэш,
/// создаёт подходящий <see cref="IAudioSource"/>.
/// </summary>
public static class AudioSourceFactory
{
    private static AudioCacheManager? _globalCacheManager;
    private static volatile StreamingConfig _currentConfig = StreamingProfiles.Medium;

    /// <summary>
    /// Runtime blacklist CDN-хостов, заблокированных для media-трафика ТСПУ.
    /// Singleton — живёт весь жизненный цикл приложения.
    /// </summary>
    internal static readonly CdnBlacklist CdnBlacklist = new(ttl: TimeSpan.FromMinutes(5));

    /// <summary>
    /// Инициализирует глобальный кэш-менеджер.
    /// </summary>
    public static void InitializeGlobalCache(AudioCacheManager cacheManager)
    {
        _globalCacheManager = cacheManager ?? throw new ArgumentNullException(nameof(cacheManager));
        Log.Info("[AudioSourceFactory] Global cache initialized");
    }

    /// <summary>Глобальный кэш-менеджер.</summary>
    public static AudioCacheManager? GlobalCache => _globalCacheManager;

    /// <summary>
    /// Обновляет конфигурацию стриминга.
    /// </summary>
    public static void SetStreamingConfig(StreamingConfig config)
    {
        _currentConfig = config ?? throw new ArgumentNullException(nameof(config));
        Log.Info($"[AudioSourceFactory] Config updated: " +
                 $"align={config.RequestAlignmentBytes / 1024}KB, " +
                 $"request={config.MinRequestSizeBytes / 1024}-{config.MaxRequestSizeBytes / 1024}KB, " +
                 $"targetBuffer={config.TargetBufferMs}ms, " +
                 $"concurrent={config.MaxConcurrentDownloads}");
    }

    /// <summary>
    /// Обновляет конфигурацию из профиля интернета.
    /// </summary>
    public static void ApplyInternetProfile(InternetProfile profile)
    {
        SetStreamingConfig(StreamingProfiles.GetConfig(profile));
    }

    /// <summary>
    /// Строит уникальный ключ кэша: trackId + формат + **нормализованный** битрейт.
    /// </summary>
    public static string BuildCacheKey(string trackId, AudioFormat format, int bitrate)
    {
        int normalizedBitrate = NormalizeBitrate(bitrate);
        return $"{trackId}_{format}_{normalizedBitrate}";
    }

    /// <summary>
    /// Ищет полностью закэшированный трек любого формата/битрейта.
    /// </summary>
    public static (string Path, AudioCacheEntry Entry)? FindAnyCachedTrack(string trackId)
    {
        if (_globalCacheManager == null) return null;

        var entry = _globalCacheManager.FindBestCache(trackId);
        if (entry == null) return null;

        var path = _globalCacheManager.GetCachePath(entry.CacheKey);
        if (!File.Exists(path)) return null;

        return (path, entry);
    }

    /// <summary>
    /// Создаёт экземпляр аудиоисточника на основе дескриптора потока.
    /// </summary>
    /// <param name="descriptor">Дескриптор разрешенного потока.</param>
    /// <param name="httpClient">HTTP-клиент для загрузки сетевых сегментов.</param>
    /// <param name="urlAcquirer">Callback первичного получения URL продолжения.</param>
    /// <param name="urlRefresher">Callback обнуления и повторного получения URL при 403.</param>
    /// <param name="config">Конфигурация параметров стриминга.</param>
    /// <param name="ct">Токен отмены операции.</param>
    /// <returns>Инициализированный экземпляр аудиоисточника.</returns>
    /// <exception cref="InvalidOperationException">Выбрасывается, если глобальный кэш не был инициализирован.</exception>
    /// <exception cref="ArgumentException">Выбрасывается при отсутствии URL и локального кэша.</exception>
    public static Task<IAudioSource> CreateAsync(
        ResolvedStreamDescriptor descriptor,
        HttpClient httpClient,
        Func<CancellationToken, Task<string?>>? urlAcquirer = null,
        Func<CancellationToken, Task<string?>>? urlRefresher = null,
        StreamingConfig? config = null,
        CancellationToken ct = default)
    {
        if (_globalCacheManager == null)
        {
            throw new InvalidOperationException(
                "AudioSourceFactory.InitializeGlobalCache() must be called before creating sources");
        }

        Log.Info($"[AudioSourceFactory] CreateAsync -> {descriptor}");

        config ??= _currentConfig;
        string trackId = descriptor.TrackId;
        string url = descriptor.Url;
        var format = descriptor.Format;
        var codec = descriptor.Codec;
        int bitrateKbps = descriptor.BitrateKbps;
        long contentLength = descriptor.ContentLengthBytes;

        if (!string.IsNullOrEmpty(url) && File.Exists(url))
        {
            Log.Info($"[AudioSourceFactory] Source decision: LocalFileSource, track={trackId}, reason=local-download");
            return Task.FromResult<IAudioSource>(new LocalFileSource(
                url,
                contentLength,
                trackId,
                null,
                null));
        }

        // Вычисляем канонический ключ кэша для данного трека и формата
        string resolvedCacheKey = format != AudioFormat.Unknown && bitrateKbps > 0
            ? BuildCacheKey(trackId, format, bitrateKbps)
            : string.Empty;

        // Cache-only or empty URL path
        if (string.IsNullOrEmpty(url))
        {
            // При явном Partial Cache запрещаем откат на LocalFileSource: запускаем CachingStreamSource
            if (descriptor.Origin == StreamSource.DiskCachePartial)
            {
                int bootstrapBytes = Math.Min(config.InitialPrebufferBytes, int.MaxValue);
                var startupEntry = _globalCacheManager.FindBestStartupCache(trackId, bootstrapBytes)
                                ?? (!string.IsNullOrEmpty(resolvedCacheKey) ? _globalCacheManager.GetCacheInfo(resolvedCacheKey) : null);

                long effectiveLength = contentLength > 0
                    ? contentLength
                    : (startupEntry?.TotalSize ?? 0);

                string partialCacheKey = !string.IsNullOrEmpty(resolvedCacheKey)
                    ? resolvedCacheKey
                    : (startupEntry?.CacheKey ?? BuildCacheKey(trackId, format, bitrateKbps));

                Log.Info($"[AudioSourceFactory] Source decision: CachingStreamSource, track={trackId}, reason=partial-cache-bootstrap, cacheKey={partialCacheKey}");
                return Task.FromResult<IAudioSource>(new CachingStreamSource(
                    partialCacheKey,
                    trackId,
                    url: string.Empty,
                    contentLength: effectiveLength,
                    format: format != AudioFormat.Unknown ? format : (startupEntry?.Format ?? AudioFormat.WebM),
                    codec: codec != AudioCodec.Unknown ? codec : (startupEntry?.Codec ?? AudioCodec.Opus),
                    bitrate: bitrateKbps > 0 ? bitrateKbps : (startupEntry?.Bitrate ?? 128),
                    cacheManager: _globalCacheManager,
                    config: config,
                    urlAcquirer: urlAcquirer,
                    urlRefresher: urlRefresher));
            }

            if (!string.IsNullOrEmpty(resolvedCacheKey))
            {
                if (_globalCacheManager.IsFullyCached(resolvedCacheKey))
                {
                    var exactEntry = _globalCacheManager.GetCacheInfo(resolvedCacheKey);
                    string exactPath = _globalCacheManager.GetCachePath(resolvedCacheKey);
                    if (exactEntry != null && File.Exists(exactPath))
                    {
                        Log.Info($"[AudioSourceFactory] Source decision: LocalFileSource, track={trackId}, reason=full-cache");
                        return Task.FromResult<IAudioSource>(new LocalFileSource(
                            exactPath,
                            exactEntry.TotalSize,
                            trackId,
                            _globalCacheManager,
                            resolvedCacheKey));
                    }
                }
            }

            var cached = FindAnyCachedTrack(trackId);
            if (cached != null)
            {
                Log.Info($"[AudioSourceFactory] Source decision: LocalFileSource, track={trackId}, reason=full-cache");
                return Task.FromResult<IAudioSource>(new LocalFileSource(
                    cached.Value.Path,
                    cached.Value.Entry.TotalSize,
                    trackId,
                    _globalCacheManager,
                    cached.Value.Entry.CacheKey));
            }

            int defaultBootstrapBytes = Math.Min(config.InitialPrebufferBytes, int.MaxValue);
            var fallbackStartupEntry = _globalCacheManager.FindBestStartupCache(trackId, defaultBootstrapBytes);
            if (fallbackStartupEntry != null)
            {
                Log.Info($"[AudioSourceFactory] Source decision: CachingStreamSource, track={trackId}, reason=partial-cache-bootstrap, cacheKey={fallbackStartupEntry.CacheKey}");
                return Task.FromResult<IAudioSource>(new CachingStreamSource(
                    fallbackStartupEntry.CacheKey,
                    trackId,
                    url: string.Empty,
                    contentLength: fallbackStartupEntry.TotalSize,
                    format: fallbackStartupEntry.Format,
                    codec: fallbackStartupEntry.Codec,
                    bitrate: fallbackStartupEntry.Bitrate > 0 ? fallbackStartupEntry.Bitrate : bitrateKbps,
                    cacheManager: _globalCacheManager,
                    config: config,
                    urlAcquirer: urlAcquirer,
                    urlRefresher: urlRefresher));
            }

            throw new ArgumentException("No URL provided and no cache available", nameof(descriptor));
        }

        // CDN Connection Pre-Warming
        CdnConnectionPreWarmer.PreWarmHost(httpClient, url, ct);

        // Format and codec come from descriptor — no HTTP detect needed
        if (format == AudioFormat.Unknown)
        {
            format = DetectFormat(url);
            if (format == AudioFormat.Unknown)
                throw new NotSupportedException($"Could not detect audio format for descriptor: {trackId}");
        }

        if (codec == AudioCodec.Unknown)
            codec = GetCodecForFormat(format);

        if (contentLength <= 0)
        {
            contentLength = ExtractContentLengthFromUrl(url);
            if (contentLength <= 0)
                contentLength = 100 * 1024 * 1024;
        }

        string finalCacheKey = !string.IsNullOrEmpty(resolvedCacheKey)
            ? resolvedCacheKey
            : BuildCacheKey(trackId, format, bitrateKbps);

        if (descriptor.Origin != StreamSource.DiskCachePartial && _globalCacheManager.IsFullyCached(finalCacheKey))
        {
            var cachePath = _globalCacheManager.GetCachePath(finalCacheKey);
            if (File.Exists(cachePath))
            {
                var exactEntry = _globalCacheManager.GetCacheInfo(finalCacheKey);
                Log.Info($"[AudioSourceFactory] Source decision: LocalFileSource, track={trackId}, reason=full-cache");
                return Task.FromResult<IAudioSource>(new LocalFileSource(
                    cachePath,
                    exactEntry?.TotalSize ?? 0,
                    trackId,
                    _globalCacheManager,
                    finalCacheKey));
            }
        }

        Log.Info($"[AudioSourceFactory] Source decision: CachingStreamSource, track={trackId}, reason=live-stream, cacheKey={finalCacheKey}, hasLiveUrl={descriptor.HasLiveUrl}");

        return Task.FromResult<IAudioSource>(new CachingStreamSource(
            finalCacheKey,
            trackId,
            url,
            contentLength,
            format,
            codec,
            bitrateKbps,
            _globalCacheManager,
            config,
            urlAcquirer,
            urlRefresher));
    }

    /// <summary>
    /// Создаёт экземпляр аудиоисточника по прямому URL (устаревшая перегрузка).
    /// </summary>
    /// <param name="url">Прямой URL потока или путь к локальному файлу.</param>
    /// <param name="httpClient">HTTP-клиент для сетевых запросов.</param>
    /// <param name="urlAcquirer">Callback первичного получения URL продолжения.</param>
    /// <param name="urlRefresher">Callback обновления URL при 403.</param>
    /// <param name="trackId">Идентификатор трека.</param>
    /// <param name="bitrateHint">Предполагаемый битрейт в kbps.</param>
    /// <param name="config">Конфигурация параметров стриминга.</param>
    /// <param name="ct">Токен отмены операции.</param>
    /// <returns>Инициализированный экземпляр аудиоисточника.</returns>
    /// <exception cref="InvalidOperationException">Выбрасывается, если глобальный кэш не был инициализирован.</exception>
    /// <exception cref="ArgumentException">Выбрасывается при пустом URL и отсутствии кэша.</exception>
    public static async Task<IAudioSource> CreateAsync(
          string url,
          HttpClient httpClient,
          Func<CancellationToken, Task<string?>>? urlAcquirer = null,
          Func<CancellationToken, Task<string?>>? urlRefresher = null,
          string? trackId = null,
          int bitrateHint = 0,
          StreamingConfig? config = null,
          CancellationToken ct = default)
    {
        if (_globalCacheManager == null)
        {
            throw new InvalidOperationException(
                "AudioSourceFactory.InitializeGlobalCache() must be called before creating sources");
        }

        config ??= _currentConfig;
        trackId ??= GenerateTrackIdFromUrl(url);

        if (!string.IsNullOrEmpty(url) && File.Exists(url))
        {
            Log.Info($"[AudioSourceFactory] Source decision: LocalFileSource, reason=local-download");
            return new LocalFileSource(url, 0, trackId, null, null);
        }

        if (string.IsNullOrEmpty(url))
        {
            var cached = FindAnyCachedTrack(trackId);
            if (cached != null)
            {
                Log.Info($"[AudioSourceFactory] Using cached: {trackId} " +
                         $"({cached.Value.Entry.Format}/{cached.Value.Entry.Bitrate}kbps)");
                return new LocalFileSource(
                    cached.Value.Path,
                    cached.Value.Entry.TotalSize,
                    trackId,
                    _globalCacheManager,
                    cached.Value.Entry.CacheKey);
            }

            int bootstrapBytes = Math.Min(config.InitialPrebufferBytes, int.MaxValue);
            var startupEntry = _globalCacheManager.FindBestStartupCache(trackId, bootstrapBytes);
            if (startupEntry != null)
            {
                Log.Info($"[AudioSourceFactory] Partial-cache bootstrap: {trackId} " +
                         $"({startupEntry.Format}/{startupEntry.Bitrate}kbps, " +
                         $"prefix={startupEntry.GetContiguousDownloadedBytesFrom(0) / 1024}KB)");

                return new CachingStreamSource(
                    startupEntry.CacheKey,
                    trackId,
                    url: string.Empty,
                    contentLength: startupEntry.TotalSize,
                    format: startupEntry.Format,
                    codec: startupEntry.Codec,
                    bitrate: startupEntry.Bitrate > 0 ? startupEntry.Bitrate : bitrateHint,
                    cacheManager: _globalCacheManager,
                    config: config,
                    urlAcquirer: urlAcquirer,
                    urlRefresher: urlRefresher);
            }

            throw new ArgumentException("No URL provided and no cache available", nameof(url));
        }

        CdnConnectionPreWarmer.PreWarmHost(httpClient, url, ct);

        var format = await DetectFormatAsync(url, httpClient, ct).ConfigureAwait(false);
        if (format == AudioFormat.Unknown)
            throw new NotSupportedException($"Could not detect audio format for: {url}");

        var (contentLength, codec, detectedBitrate) =
            await GetStreamInfoAsync(url, format, httpClient, ct).ConfigureAwait(false);

        int finalBitrate = bitrateHint > 0 ? bitrateHint : detectedBitrate;
        string cacheKey = BuildCacheKey(trackId, format, finalBitrate);

        if (_globalCacheManager.IsFullyCached(cacheKey))
        {
            var cachePath = _globalCacheManager.GetCachePath(cacheKey);
            if (File.Exists(cachePath))
            {
                var exactEntry = _globalCacheManager.GetCacheInfo(cacheKey);
                Log.Info($"[AudioSourceFactory] Exact cache hit: {cacheKey}");
                return new LocalFileSource(
                    cachePath,
                    exactEntry?.TotalSize ?? 0,
                    trackId,
                    _globalCacheManager,
                    cacheKey);
            }
        }

        Log.Info($"[AudioSourceFactory] Streaming {format}/{codec}/{finalBitrate}kbps: {cacheKey}");

        return new CachingStreamSource(
            cacheKey,
            trackId,
            url,
            contentLength,
            format,
            codec,
            finalBitrate,
            _globalCacheManager,
            config,
            urlAcquirer,
            urlRefresher);
    }

    /// <summary>
    /// Запускает спекулятивный прогрев TCP+TLS-соединений к последним известным CDN-хостам.
    /// </summary>
    public static void PreWarmCdnConnections(HttpClient httpClient, CancellationToken ct)
    {
        CdnConnectionPreWarmer.PreWarmRecentHosts(httpClient, ct);
    }

    /// <summary>
    /// Возвращает информацию о кэше для трека.
    /// </summary>
    public static AudioCacheEntry? GetCacheInfo(string trackId) =>
        _globalCacheManager?.FindBestCache(trackId);

    #region Format Detection

    /// <summary>
    /// Определяет формат по URL (mime-параметры, расширение).
    /// </summary>
    public static AudioFormat DetectFormat(string url)
    {
        if (string.IsNullOrEmpty(url))
            return AudioFormat.Unknown;

        if (url.Contains(".m3u8", StringComparison.OrdinalIgnoreCase) ||
            url.Contains("/hls/", StringComparison.OrdinalIgnoreCase))
            return AudioFormat.Hls;

        if (url.Contains("mime=audio%2Fwebm", StringComparison.OrdinalIgnoreCase) ||
            url.Contains("mime=audio/webm", StringComparison.OrdinalIgnoreCase))
            return AudioFormat.WebM;

        if (url.Contains("mime=audio%2Fmp4", StringComparison.OrdinalIgnoreCase) ||
            url.Contains("mime=audio/mp4", StringComparison.OrdinalIgnoreCase))
            return AudioFormat.Mp4;

        if (url.Contains("mime=audio%2Fogg", StringComparison.OrdinalIgnoreCase) ||
            url.Contains("mime=audio/ogg", StringComparison.OrdinalIgnoreCase))
            return AudioFormat.Ogg;

        if (url.Contains(".webm", StringComparison.OrdinalIgnoreCase))
            return AudioFormat.WebM;

        if (url.Contains(".mp4", StringComparison.OrdinalIgnoreCase) ||
            url.Contains(".m4a", StringComparison.OrdinalIgnoreCase))
            return AudioFormat.Mp4;

        if (url.Contains(".ogg", StringComparison.OrdinalIgnoreCase) ||
            url.Contains(".opus", StringComparison.OrdinalIgnoreCase))
            return AudioFormat.Ogg;

        return AudioFormat.Unknown;
    }

    /// <summary>
    /// Определяет формат: сначала по URL, потом по magic bytes.
    /// </summary>
    internal static async Task<AudioFormat> DetectFormatAsync(
        string url, HttpClient httpClient, CancellationToken ct = default)
    {
        var urlFormat = DetectFormat(url);
        if (urlFormat != AudioFormat.Unknown)
            return urlFormat;

        try
        {
            using var request = SharedHttpClient.CreateRangeRequest(url, 0, FormatDetectionHeaderSize - 1);
            using var response = await httpClient.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, ct);

            if (response.StatusCode == HttpStatusCode.Forbidden)
                return AudioFormat.Unknown;

            response.EnsureSuccessStatusCode();

            var header = await response.Content.ReadAsByteArrayAsync(ct);
            return DetectFormatByMagic(header);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Log.Warn($"[AudioSourceFactory] Format detection failed: {ex.Message}");
            return AudioFormat.Unknown;
        }
    }

    /// <summary>
    /// Определяет формат по magic bytes заголовка.
    /// </summary>
    internal static AudioFormat DetectFormatByMagic(ReadOnlySpan<byte> header)
    {
        if (header.Length < 4) return AudioFormat.Unknown;

        if (header[..4].SequenceEqual(WebMMagic))
            return AudioFormat.WebM;

        if (header.Length >= 8 && header.Slice(4, 4).SequenceEqual(Mp4FtypMagic))
            return AudioFormat.Mp4;

        if (header[..4].SequenceEqual(OggMagic))
            return AudioFormat.Ogg;

        return AudioFormat.Unknown;
    }

    /// <summary>
    /// Определяет кодек по формату контейнера.
    /// </summary>
    public static AudioCodec GetCodecForFormat(AudioFormat format) => format switch
    {
        AudioFormat.WebM => AudioCodec.Opus,
        AudioFormat.Ogg => AudioCodec.Opus,
        AudioFormat.Mp4 => AudioCodec.Aac,
        AudioFormat.Hls => AudioCodec.Aac,
        _ => AudioCodec.Unknown
    };

    #endregion

    #region Private Helpers

    private static string GenerateTrackIdFromUrl(string url)
    {
        var hash = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(url));
        return "cache_" + Convert.ToHexString(hash)[..16];
    }

    internal static async Task<(long ContentLength, AudioCodec Codec, int Bitrate)> GetStreamInfoAsync(
     string url, AudioFormat format, HttpClient httpClient, CancellationToken ct)
    {
        long contentLength = 0;

        if (url.Contains("googlevideo.com/videoplayback"))
        {
            contentLength = ExtractContentLengthFromUrl(url);
        }
        else
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Head, url);
                using var response = await httpClient.SendAsync(request, ct);
                if (response.IsSuccessStatusCode)
                    contentLength = response.Content.Headers.ContentLength ?? 0;
            }
            catch (OperationCanceledException) { throw; }
            catch { }
        }

        if (contentLength <= 0)
            contentLength = 100 * 1024 * 1024;

        var codec = GetCodecForFormat(format);
        int bitrate = ExtractBitrateFromUrl(url);
        if (bitrate == 0)
            bitrate = codec == AudioCodec.Opus ? 128 : 96;

        return (contentLength, codec, bitrate);
    }

    private static long ExtractContentLengthFromUrl(string url)
    {
        try
        {
            var query = System.Web.HttpUtility.ParseQueryString(new Uri(url).Query);
            return long.TryParse(query["clen"], out var clen) ? clen : 0;
        }
        catch { return 0; }
    }

    private static int ExtractBitrateFromUrl(string url)
    {
        try
        {
            var query = System.Web.HttpUtility.ParseQueryString(new Uri(url).Query);
            return int.TryParse(query["bitrate"], out var br) ? br / 1000 : 0;
        }
        catch { return 0; }
    }

    #endregion
}

public enum AudioFormat
{
    Unknown = 0,
    WebM = 1,
    Mp4 = 2,
    Ogg = 3,
    Hls = 4
}