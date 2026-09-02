using System.Diagnostics;
using LMP.Core.Audio.Cache;
using LMP.Core.Audio.Interfaces;
using LMP.Core.Audio.Sources;
using LMP.Core.Youtube.Videos;
using LMP.Core.Helpers.Extensions;
using LMP.Tests.Framework;
using Microsoft.Extensions.DependencyInjection;

namespace LMP.Tests.Unit;

/// <summary>
/// Unit и integration тесты для подсистемы валидации времени жизни URL (expire TTL)
/// и упреждающего обновления протухших потоков.
/// </summary>
public static class StreamFreshnessTests
{
    // 
    // UNIT TESTS (Offline, deterministic)
    // 

    /// <summary>
    /// Проверяет точность парсинга параметра &amp;expire= и определение срока годности через <see cref="UrlEx"/>.
    /// </summary>
    [TestMethod(TestCategory.Unit, "StreamFreshness: UrlEx TTL Calculations",
        Group = TestGroups.Pipeline, Order = 1)]
    public static Task TestUrlExExpireParsingAsync()
    {
        var now = DateTimeOffset.UtcNow;
        var nowUnix = now.ToUnixTimeSeconds();

        // 1. Уже протухший URL (expire = now - 120s)
        var expiredUrl = $"https://rr1---sn-4g5lznsz.googlevideo.com/videoplayback?expire={nowUnix - 120}&n=testToken123&itag=251";
        Assert(UrlEx.TryGetExpireUtc(expiredUrl, out var exp1), "Failed to parse expire timestamp");
        Assert(exp1 < DateTime.UtcNow, "Parsed expire should be in the past");
        Assert(UrlEx.IsUrlExpiredOrExpiringSoon(expiredUrl, TimeSpan.FromMinutes(5)), "Url should be marked as expired");
        Log.Info("  ✅ Expired URL correctly detected");

        // 2. Истекает через 2 минуты (порог 5 минут -> должен считаться expiring soon)
        var expiringSoonUrl = $"https://rr1---sn-4g5lznsz.googlevideo.com/videoplayback?expire={nowUnix + 120}&n=testToken123&itag=251";
        Assert(UrlEx.IsUrlExpiredOrExpiringSoon(expiringSoonUrl, TimeSpan.FromMinutes(5)), "Url expiring in 2m should trigger expiring soon (<5m)");
        Assert(!UrlEx.IsUrlExpiredOrExpiringSoon(expiringSoonUrl, TimeSpan.FromMinutes(1)), "Url expiring in 2m should not trigger if margin is 1m");
        Log.Info("  ✅ Expiring soon (<5m) correctly detected");

        // 3. Свежий URL на 6 часов вперёд
        var freshUrl = $"https://rr1---sn-4g5lznsz.googlevideo.com/videoplayback?expire={nowUnix + 21600}&n=testToken123&itag=251";
        Assert(UrlEx.TryGetExpireUtc(freshUrl, out var exp3), "Failed to parse fresh expire timestamp");
        Assert(exp3 > DateTime.UtcNow.AddHours(5), "Fresh expire should be > 5h in future");
        Assert(!UrlEx.IsUrlExpiredOrExpiringSoon(freshUrl, TimeSpan.FromMinutes(5)), "Fresh URL should not be marked as expiring");
        Log.Info("  ✅ Fresh URL (>5h) correctly validated");

        // 4. URL без параметра expire (локальный файл / generic CDN)
        var noExpireUrl = "https://example.com/audio/track123.opus";
        Assert(!UrlEx.TryGetExpireUtc(noExpireUrl, out _), "Should return false for URL without expire param");
        Assert(!UrlEx.IsUrlExpiredOrExpiringSoon(noExpireUrl), "URL without expire should not be considered expiring");
        Log.Info("  ✅ URL without expire handled gracefully");

        // 5. Пустая строка / null
        Assert(UrlEx.IsUrlExpiredOrExpiringSoon(""), "Empty URL must be considered expired to force initial acquire");
        Log.Info("  ✅ Empty URL handled safely");

        return Task.CompletedTask;
    }

    /// <summary>
    /// Верифицирует, что при совпадении n-token, но свежем &amp;expire=,
    /// refresher признаёт URL валидным (Success) и не выбрасывает фатальный StaleToken.
    /// </summary>
    [TestMethod(TestCategory.Unit, "StreamFreshness: Refresh with unchanged n-token",
        Group = TestGroups.Pipeline, Order = 2)]
    public static async Task TestRefreshWithUnchangedNTokenAndValidExpireAsync()
    {
        var nowUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        const string sharedNToken = "SameStaticNToken123";

        // Старый протухший URL
        var oldUrl = $"https://rr1---sn-4g5lznsz.googlevideo.com/videoplayback?expire={nowUnix - 3600}&n={sharedNToken}&sig=SIG1&itag=251";

        // Новый URL: тот же n-token, но expire на 6 часов вперёд и новая подпись
        var newFreshUrl = $"https://rr1---sn-4g5lznsz.googlevideo.com/videoplayback?expire={nowUnix + 21600}&n={sharedNToken}&sig=SIG2&itag=251";

        int refreshCallCount = 0;
        ValueTask<string?> MockRefresher(CancellationToken ct)
        {
            Interlocked.Increment(ref refreshCallCount);
            return ValueTask.FromResult<string?>(newFreshUrl);
        }

        using var tempCache = new TempCacheManager();

        var source = new CachingStreamSource(
            cacheKey: "test_freshness_track",
            trackId: "yt_test_track",
            url: oldUrl,
            contentLength: 10_000_000,
            format: AudioFormat.WebM,
            codec: AudioCodec.Opus,
            bitrate: 160,
            cacheManager: tempCache.Manager,
            config: new StreamingConfig { MinRequestSizeBytes = 4096, MaxRequestSizeBytes = 65536 },
            urlRefresher: ct => MockRefresher(ct).AsTask());

        // Запускаем проверку свежести
        await source.EnsureStreamFreshnessAsync(CancellationToken.None);

        Assert(refreshCallCount == 1, $"Expected 1 refresh call, got {refreshCallCount}");
        Assert(!UrlEx.IsUrlExpiredOrExpiringSoon(source.CacheKey), "Source URL should be fresh after refresh");

        Log.Info($"[Test] Refresh with identical n-token + new expire succeeded without StaleToken crash.");
    }

    /// <summary>
    /// Проверяет, что <see cref="CachingStreamSource.EnsureStreamFreshnessAsync"/>
    /// срабатывает превентивно, если URL истекает в ближайшие 5 минут.
    /// </summary>
    [TestMethod(TestCategory.Unit, "StreamFreshness: Proactive check on resume",
        Group = TestGroups.Pipeline, Order = 3)]
    public static async Task TestProactiveStreamFreshnessOnResumeAsync()
    {
        var nowUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        // URL истекает через 2 минуты
        var expiringUrl = $"https://rr1---sn-4g5lznsz.googlevideo.com/videoplayback?expire={nowUnix + 120}&n=token1&itag=251";
        var freshUrl = $"https://rr1---sn-4g5lznsz.googlevideo.com/videoplayback?expire={nowUnix + 21600}&n=token2&itag=251";

        int refreshCalls = 0;
        using var tempCache = new TempCacheManager();

        var source = new CachingStreamSource(
            cacheKey: "test_proactive_track",
            trackId: "yt_proactive_track",
            url: expiringUrl,
            contentLength: 5_000_000,
            format: AudioFormat.WebM,
            codec: AudioCodec.Opus,
            bitrate: 160,
            cacheManager: tempCache.Manager,
            config: new StreamingConfig(),
            urlRefresher: _ =>
            {
                Interlocked.Increment(ref refreshCalls);
                return Task.FromResult<string?>(freshUrl);
            });

        // Эмулируем вызов из AudioPlayer.HandleResumeAsync
        await source.EnsureStreamFreshnessAsync(CancellationToken.None);

        Assert(refreshCalls == 1, $"Proactive refresh should have been invoked 1 time, got {refreshCalls}");
        Log.Info("  ✅ Proactive freshness check on resume verified");
    }

    // 
    // INTEGRATION TESTS (Network required)
    // 

    /// <summary>
    /// Интеграционный тест: запрашивает реальный манифест с YouTube и проверяет,
    /// что полученный URL имеет валидный параметр &amp;expire= с запасом времени не менее 5 часов.
    /// </summary>
    [TestMethod(TestCategory.Integration, "StreamFreshness: Live Manifest Expire TTL Check",
        Group = TestGroups.Pipeline, Order = 4, RequiresNetwork = true, TimeoutSeconds = 60)]
    public static async Task TestLiveStreamUrlExpireTtlAsync(IServiceProvider services)
    {
        var youtube = services.GetRequiredService<Lazy<YoutubeProvider>>().Value.GetClient();
        var videoId = TestConfig.Get().Pipeline.DebugVideoId;

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var sw = Stopwatch.StartNew();
        var manifest = await youtube.Videos.Streams.GetManifestAsync(
            VideoId.Parse(videoId), cts.Token);
        sw.Stop();

        var audioStream = manifest.GetAudioOnlyStreams().GetWithHighestBitrate();
        Assert(audioStream is not null, "No audio stream found in live manifest");

        var url = audioStream!.Url;
        Assert(UrlEx.TryGetExpireUtc(url, out var expireUtc), "Live stream URL is missing valid &expire= parameter");

        var ttlRemaining = expireUtc - DateTime.UtcNow;
        Log.Info($"[StreamFreshness] Live stream URL resolved in {sw.ElapsedMilliseconds}ms:");
        Log.Info($"  Expire UTC   : {expireUtc:O}");
        Log.Info($"  TTL Remaining: {ttlRemaining.TotalHours:F2} hours ({ttlRemaining.TotalMinutes:F0} minutes)");

        // Стандартный TTL YouTube составляет ~6 часов (21600 секунд)
        Assert(ttlRemaining.TotalHours >= 4.5, $"TTL remaining is unexpectedly low: {ttlRemaining.TotalHours:F2}h (expected ~6h)");
        Assert(!UrlEx.IsUrlExpiredOrExpiringSoon(url, TimeSpan.FromMinutes(5)), "Fresh live stream URL should not be expiring soon");

        Log.Info("  ✅ Live stream URL TTL successfully parsed and verified");
    }

    // 
    // HELPERS
    // 

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new Exception($"[Assertion Failed] {message}");
    }

    private sealed class TempCacheManager : IDisposable
    {
        private readonly string _tempDir;
        public AudioCacheManager Manager { get; }

        public TempCacheManager()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), $"lmp_cache_test_{Guid.NewGuid():N}");
            Directory.CreateDirectory(_tempDir);
            Manager = new AudioCacheManager(_tempDir, maxCacheSizeMb: 50 * 1024 * 1024);
        }

        public void Dispose()
        {
            try
            {
                Manager.Dispose();
                if (Directory.Exists(_tempDir))
                    Directory.Delete(_tempDir, recursive: true);
            }
            catch { }
        }
    }
}