using System.Text.Json;
using System.Text.Json.Serialization;
using LMP.Core.Audio.Interfaces;
using LMP.Core.Models.Json;
using LMP.Core.Youtube.Utils;
using LMP.Core.Youtube.Videos.Streams;

namespace LMP.Core.Audio.Http;

// Public Models

/// <summary>
/// Один вариант аудиопотока внутри закэшированного манифеста.
/// </summary>
public sealed class VariantEntry
{
    /// <summary>YouTube itag потока.</summary>
    public int Itag { get; set; }

    /// <summary>Полный videoplayback URL.</summary>
    public required string Url { get; set; }

    /// <summary>Контейнер (webm, mp4).</summary>
    public string Container { get; set; } = "";

    /// <summary>Кодек (opus, aac).</summary>
    public string Codec { get; set; } = "";

    /// <summary>Битрейт в bps.</summary>
    public int Bitrate { get; set; }

    /// <summary>Размер контента в байтах.</summary>
    public long Clen { get; set; }

    /// <summary>
    /// Типизированный формат контейнера.
    /// Не сериализуется и вычисляется из <see cref="Container"/>.
    /// </summary>
    [JsonIgnore]
    public AudioFormat Format => YoutubeIdHelper.MapContainerToFormat(Container);

    /// <summary>
    /// Типизированный аудиокодек.
    /// Не сериализуется и вычисляется из <see cref="Codec"/>.
    /// </summary>
    [JsonIgnore]
    public AudioCodec CodecType => Codec.ToAudioCodec(Format);

    /// <summary>Код языка аудиодорожки.</summary>
    public string? LanguageCode { get; set; }

    /// <summary>Язык по умолчанию.</summary>
    public bool IsDefaultLanguage { get; set; }
}

/// <summary>
/// Закэшированный полный манифест для одного трека.
/// Содержит все доступные аудио-варианты с живыми URL.
/// </summary>
public sealed class TrackManifestEntry
{
    /// <summary>Идентификатор трека (с префиксом yt_).</summary>
    public required string TrackId { get; set; }

    /// <summary>CDN hostname первого варианта.</summary>
    public string CdnHost { get; set; } = "";

    /// <summary>
    /// Время истечения URL (из &amp;expire= первого варианта, UTC).
    /// Используется как hint для probe-bypass, но НЕ как жёсткий TTL.
    /// Реальная инвалидация — по HTTP 403/410 при probe.
    /// </summary>
    public DateTime ExpireUtc { get; set; }

    /// <summary>
    /// Track-level integrated loudness в LUFS из YouTube <c>perceptualLoudnessDb</c>.
    /// </summary>
    [JsonConverter(typeof(NaNFloatJsonConverter))]
    public float IntegratedLufs { get; set; } = float.NaN;

    /// <summary>Все доступные аудио-варианты.</summary>
    public List<VariantEntry> Variants { get; set; } = [];

    /// <summary>Время записи в кэш (UTC).</summary>
    public DateTime SavedAtUtc { get; set; }
}

/// <summary>
/// Конверт JSON-файла session-кэша.
/// </summary>
public sealed class SessionCacheEnvelope
{
    /// <summary>Закэшированные манифесты (LRU, max записей).</summary>
    public List<TrackManifestEntry> Manifests { get; set; } = [];

    /// <summary>Время последней очистки (UTC).</summary>
    public DateTime LastCleanupUtc { get; set; }
}

// Store

/// <summary>
/// Персистентный дисковый кэш полных YouTube аудио-манифестов.
/// <para>
/// <b>Архитектура «храним пока работает»:</b>
/// </para>
/// <list type="number">
///   <item>
///     После каждого успешного YouTube API resolve вызывается <see cref="RecordManifest"/>
///     для сохранения <b>всех</b> аудио-вариантов (URL, itag, codec, bitrate, loudness).
///   </item>
///   <item>
///     При следующем воспроизведении <see cref="TryGetManifestAndProbeAsync"/>
///     проверяет наличие записи и делает HTTP HEAD к одному из URL.
///     Если CDN отвечает 200/206 — YouTube API call пропускается полностью.
///   </item>
///   <item>
///     При HTTP 403/410 запись инвалидируется целиком — все варианты
///     получены в одном API call и протухают одновременно.
///   </item>
/// </list>
/// <para>
/// <b>Экономия сети:</b> Один manifest fetch обслуживает playback, quality switch
/// И UI quality menu без дополнительных запросов — как в RAM, так и после рестарта приложения.
/// </para>
/// </summary>
internal static class SessionCacheStore
{
    private const int MaxManifests = 50;
    private const int TtlSafetyMarginMinutes = 30;
    private const int MinProbeTimeoutMs = 1000;
    private const int MaxProbeTimeoutMs = 3000;
    private const int DefaultProbeTimeoutMs = 2000;
    private const int TtlBypassProbeMinutes = 10;
    private const double ProbeTtfbMultiplier = 4.0;

    private static readonly Lock _lock = new();
    private static readonly Lock _saveIoLock = new();
    private static SessionCacheEnvelope _data = new();
    private static bool _dirty;

    // Load / Save

    /// <summary>
    /// Загружает session-кэш с диска. Вызывается однократно при старте.
    /// </summary>
    public static void Load()
    {
        try
        {
            var path = G.FilePath.SessionCache;
            var json = AtomicFile.ReadTextWithFallback(path, out bool recovered);
            if (string.IsNullOrWhiteSpace(json))
                return;

            if (recovered)
                Log.Warn("[SessionCache] Recovered session cache from backup (.bak)");

            var envelope = JsonSerializer.Deserialize(
                json,
                AppJsonContext.DefaultCompact.SessionCacheEnvelope);

            if (envelope is null)
                return;

            lock (_lock)
            {
                _data = envelope;
                EvictExpiredMustHoldLock();
            }

            int count, variants;
            lock (_lock)
            {
                count = _data.Manifests.Count;
                variants = CountTotalVariants();
            }

            Log.Debug($"[SessionCache] Loaded {count} manifest(s), {variants} variant(s)");
        }
        catch (Exception ex)
        {
            Log.Warn($"[SessionCache] Load failed (starting fresh): {ex.Message}");
            lock (_lock) { _data = new SessionCacheEnvelope(); }
        }
    }

    /// <summary>
    /// Сохраняет session-кэш на диск.
    /// </summary>
    public static void Save()
    {
        SessionCacheEnvelope snapshot;

        lock (_lock)
        {
            if (!_dirty)
                return;

            EvictExpiredMustHoldLock();
            snapshot = _data;
            _dirty = false;
        }

        try
        {
            string json = JsonSerializer.Serialize(
                snapshot,
                AppJsonContext.DefaultCompact.SessionCacheEnvelope);

            lock (_saveIoLock)
            {
                AtomicFile.WriteText(G.FilePath.SessionCache, json, createBackup: true);
            }

            int variants = 0;
            for (int i = 0; i < snapshot.Manifests.Count; i++)
                variants += snapshot.Manifests[i].Variants.Count;

            Log.Debug($"[SessionCache] Saved {snapshot.Manifests.Count} manifest(s), {variants} variant(s)");
        }
        catch (Exception ex)
        {
            Log.Warn($"[SessionCache] Save failed: {ex.Message}");
            lock (_lock) { _dirty = true; }
        }
    }

    // Record

    /// <summary>
    /// Сохраняет полный манифест после успешного YouTube API resolve.
    /// Заменяет предыдущую запись для этого трека целиком.
    /// </summary>
    /// <param name="trackId">ID трека (с префиксом yt_).</param>
    /// <param name="streams">Все аудио-варианты из манифеста.</param>
    public static void RecordManifest(
         string trackId,
         IReadOnlyList<AudioOnlyStreamInfo> streams,
         float perceptualLufs = float.NaN)
    {
        if (string.IsNullOrEmpty(trackId) || streams.Count == 0)
            return;

        string firstUrl = streams[0].Url;

        if (!TryExtractCdnHost(firstUrl, out var cdnHost))
            cdnHost = "";

        var expireUtc = ParseExpireFromUrl(firstUrl);

        var variants = new List<VariantEntry>(streams.Count);
        for (int i = 0; i < streams.Count; i++)
        {
            var s = streams[i];
            var format = YoutubeIdHelper.MapContainerToFormat(s.Container.Name);

            variants.Add(new VariantEntry
            {
                Itag = s.Itag,
                Url = s.Url,
                Container = format.ToContainerName(),
                Codec = s.AudioCodec.ToAudioCodec(format).ToDisplayName(),
                Bitrate = (int)s.Bitrate.BitsPerSecond,
                Clen = s.Size.Bytes,
                LanguageCode = s.AudioLanguage?.Code,
                IsDefaultLanguage = s.IsAudioLanguageDefault ?? false
            });
        }

        var entry = new TrackManifestEntry
        {
            TrackId = trackId,
            CdnHost = cdnHost,
            IntegratedLufs = perceptualLufs,
            ExpireUtc = expireUtc,
            Variants = variants,
            SavedAtUtc = DateTime.UtcNow
        };

        lock (_lock)
        {
            for (int i = _data.Manifests.Count - 1; i >= 0; i--)
            {
                if (string.Equals(_data.Manifests[i].TrackId, trackId, StringComparison.Ordinal))
                {
                    _data.Manifests.RemoveAt(i);
                    break;
                }
            }

            while (_data.Manifests.Count >= MaxManifests)
                EvictLeastRecentlyUsedMustHoldLock();

            _data.Manifests.Add(entry);
            _dirty = true;
        }

        Log.Info($"[SessionCache] RecordManifest: track={trackId}, variants={variants.Count}, cdn={cdnHost}, expire={expireUtc:O}");
        _ = Task.Run(Save);
    }

    // Probe

    /// <summary>
    /// Пытается получить и проверить закэшированный манифест для трека.
    /// <para>
    /// Алгоритм:
    /// </para>
    /// <list type="number">
    ///   <item>Поиск записи по <paramref name="trackId"/>.</item>
    ///   <item>Проверка отметки <see cref="TrackManifestEntry.ExpireUtc"/> (мгновенный сброс без сетевого вызова).</item>
    ///   <item>Если запись свежая (saved &lt; 10 мин назад) — bypass без probe.</item>
    ///   <item>HTTP HEAD к первому URL с адаптивным timeout.</item>
    ///   <item>200/206 → возвращает entry. 403/404/410 → дропает запись.</item>
    /// </list>
    /// </summary>
    /// <param name="trackId">ID трека.</param>
    /// <param name="httpClient">HTTP-клиент.</param>
    /// <param name="ct">Токен отмены.</param>
    /// <returns>Валидная запись или null.</returns>
    public static async ValueTask<TrackManifestEntry?> TryGetManifestAndProbeAsync(
        string trackId,
        HttpClient httpClient,
        CancellationToken ct)
    {
        TrackManifestEntry? entry;

        lock (_lock)
        {
            entry = FindMustHoldLock(trackId);
            if (entry is null || entry.Variants.Count == 0)
                return null;

            // Если CDN-хост манифеста уже заблокирован ТСПУ (в CdnBlacklist),
            // немедленно сбрасываем кэш и не делаем bypass — нужен свежий API resolve с другим хостом
            if (!string.IsNullOrEmpty(entry.CdnHost) && AudioSourceFactory.CdnBlacklist.IsBlocked(entry.CdnHost))
            {
                Log.Warn($"[SessionCache] Manifest CDN host '{entry.CdnHost}' is blacklisted. Dropping cached manifest for {trackId}.");
                DropMustHoldLock(trackId);
                return null;
            }

            // Мгновенная инвалидация без выполнения сетевого зонда, если срок жизни ссылки истек
            if (entry.ExpireUtc <= DateTime.UtcNow)
            {
                Log.Debug($"[SessionCache] ExpireUtc passed for {trackId}, dropping manifest without probe");
                DropMustHoldLock(trackId);
                return null;
            }
            else if (DateTime.UtcNow - entry.SavedAtUtc < TimeSpan.FromMinutes(TtlBypassProbeMinutes))
            {
                Log.Debug($"[SessionCache] Probe bypass: track={trackId}, age={(DateTime.UtcNow - entry.SavedAtUtc).TotalSeconds:F0}s, variants={entry.Variants.Count}");
                return entry;
            }
        }

        var probeUrl = entry.Variants[0].Url;
        var probeTimeoutMs = ComputeProbeTimeout(entry.CdnHost);

        try
        {
            using var probeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            probeCts.CancelAfter(probeTimeoutMs);

            using var request = new HttpRequestMessage(HttpMethod.Head, probeUrl);
            request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 0);
            request.Version = System.Net.HttpVersion.Version11;
            SharedHttpClient.ApplyUserAgentFromUrl(request, probeUrl);

            using var response = await httpClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, probeCts.Token)
                .ConfigureAwait(false);

            var status = (int)response.StatusCode;

            if (status is 200 or 206)
            {
                Log.Info($"[SessionCache] Probe OK ({status}): track={trackId}, variants={entry.Variants.Count}");
                return entry;
            }

            if (status is 401 or 403 or 404 or 410)
            {
                Log.Debug($"[SessionCache] Probe rejected (HTTP {status}): track={trackId}, dropping manifest");
                lock (_lock) { DropMustHoldLock(trackId); }
                return null;
            }

            Log.Warn($"[SessionCache] Probe returned HTTP {status} for {trackId}, keeping for safety");
            return null;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            Log.Debug($"[SessionCache] Probe timed out ({probeTimeoutMs}ms) for {trackId}");
            return null;
        }
        catch (HttpRequestException ex)
        {
            Log.Debug($"[SessionCache] Probe network error for {trackId}: {ex.Message}");
            return null;
        }
        catch (Exception ex)
        {
            Log.Debug($"[SessionCache] Probe failed for {trackId}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Возвращает закэшированную запись манифеста без probe (для локального чтения).
    /// </summary>
    public static TrackManifestEntry? GetManifest(string trackId)
    {
        lock (_lock)
        {
            return FindMustHoldLock(trackId);
        }
    }

    /// <summary>
    /// Возвращает снимок всех trackId из текущего кэша.
    /// Используется диагностическими тестами для probe всех известных стримов.
    /// </summary>
    internal static IReadOnlyList<string> GetAllCachedTrackIds()
    {
        lock (_lock)
        {
            if (_data.Manifests.Count == 0)
                return [];

            var result = new string[_data.Manifests.Count];
            for (int i = 0; i < _data.Manifests.Count; i++)
                result[i] = _data.Manifests[i].TrackId;

            return result;
        }
    }

    /// <summary>
    /// Инвалидирует запись для трека (после фатальной 403 или force refresh).
    /// </summary>
    public static void Invalidate(string trackId)
    {
        lock (_lock)
        {
            DropMustHoldLock(trackId);
        }
        Log.Debug($"[SessionCache] Invalidated manifest for {trackId}");
    }

    // Private Helpers

    /// <summary>
    /// Возвращает индекс записи по trackId. Вызывается под lock.
    /// </summary>
    /// <returns>Индекс или -1 если не найдено.</returns>
    private static int FindIndexMustHoldLock(string trackId)
    {
        for (int i = 0; i < _data.Manifests.Count; i++)
        {
            if (string.Equals(_data.Manifests[i].TrackId, trackId, StringComparison.Ordinal))
                return i;
        }
        return -1;
    }

    private static TrackManifestEntry? FindMustHoldLock(string trackId)
    {
        int idx = FindIndexMustHoldLock(trackId);
        return idx >= 0 ? _data.Manifests[idx] : null;
    }

    private static void DropMustHoldLock(string trackId)
    {
        int idx = FindIndexMustHoldLock(trackId);
        if (idx < 0) return;

        _data.Manifests.RemoveAt(idx);
        _dirty = true;
    }

    /// <summary>
    /// Суммирует количество вариантов по всем манифестам. Вызывается под lock.
    /// </summary>
    private static int CountTotalVariants()
    {
        int total = 0;
        for (int i = 0; i < _data.Manifests.Count; i++)
            total += _data.Manifests[i].Variants.Count;
        return total;
    }

    private static int ComputeProbeTimeout(string cdnHost)
    {
        var avgTtfb = CdnHostStatsStore.GetAvgTtfbMs(cdnHost);
        if (double.IsNaN(avgTtfb))
            return DefaultProbeTimeoutMs;

        return Math.Clamp((int)(avgTtfb * ProbeTtfbMultiplier), MinProbeTimeoutMs, MaxProbeTimeoutMs);
    }

    private static void EvictLeastRecentlyUsedMustHoldLock()
    {
        if (_data.Manifests.Count == 0) return;

        int oldestIdx = 0;
        for (int i = 1; i < _data.Manifests.Count; i++)
        {
            if (_data.Manifests[i].SavedAtUtc < _data.Manifests[oldestIdx].SavedAtUtc)
                oldestIdx = i;
        }

        _data.Manifests.RemoveAt(oldestIdx);
    }

    private static void EvictExpiredMustHoldLock()
    {
        var threshold = DateTime.UtcNow.AddMinutes(-TtlSafetyMarginMinutes);
        for (int i = _data.Manifests.Count - 1; i >= 0; i--)
        {
            if (_data.Manifests[i].ExpireUtc <= threshold)
                _data.Manifests.RemoveAt(i);
        }
        _data.LastCleanupUtc = DateTime.UtcNow;
    }

    private static bool TryExtractCdnHost(string url, out string host)
    {
        host = "";
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return false;

        if (!uri.Host.EndsWith(".googlevideo.com", StringComparison.OrdinalIgnoreCase))
            return false;

        host = uri.Host;
        return true;
    }

    private static DateTime ParseExpireFromUrl(string url)
    {
        var expireStr = UrlEx.TryGetQueryParameterValue(url, "expire");
        if (expireStr is null || !long.TryParse(expireStr, out var unixSeconds))
            return DateTime.UtcNow.AddHours(6);

        return DateTimeOffset.FromUnixTimeSeconds(unixSeconds).UtcDateTime;
    }
}