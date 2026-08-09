using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using LMP.Core.Audio.Http;
using LMP.Core.Youtube.Videos;
using LMP.Tests.Framework;
using Microsoft.Extensions.DependencyInjection;

namespace LMP.Tests.Integration;

/// <summary>
/// Диагностические тесты сетевой доступности YouTube CDN.
/// Позволяют точно определить, на каком уровне происходит блокировка:
/// TCP, TLS, HTTP или конкретный URL-путь.
/// </summary>
public static class CdnDiagnosticTests
{
    /// <summary>Снимок состояния CDN для baseline/diff.</summary>
    private readonly record struct CdnHostSnapshot(
        string Host,
        string Ip,
        bool HealthOk,
        int HealthMs,
        bool MediaOk,
        int MediaMs,
        string? MediaError);

    /// <summary>Baseline для сравнения при смене сети. Null = ещё не собран.</summary>
    private static List<CdnHostSnapshot>? _networkBaseline;
    private static string? _baselineOutboundIp;

    // --- Section: Known CDN IPs ---

    /// <summary>
    /// Известные IP-адреса Google CDN из предыдущих сессий.
    /// Позволяют тестировать конкретные адреса без предварительного DNS-запроса.
    /// </summary>
    private static readonly (string Host, string Ip)[] KnownCdnEndpoints =
    [
        ("rr3---sn-4g5lznsz.googlevideo.com", "173.194.19.40"),    // Working in prev session
        ("rr3---sn-4g5ednss.googlevideo.com", "173.194.188.104"),  // Partially working
        ("rr5---sn-4g5ednsy.googlevideo.com", "74.125.173.138"),   // Blocked
        ("rr3---sn-4g5edn6k.googlevideo.com", "74.125.111.136"),   // Blocked
        ("rr7---sn-gvnuxaxjvh-n8ml.googlevideo.com", "37.79.210.18"), // Warmup only
        ("rr6---sn-axq7sn76.googlevideo.com", "172.217.130.150"),  // Warmup only
        ("rr3---sn-axq7sn7e.googlevideo.com", "74.125.163.21"),    // Warmup only
        ("rr4---sn-4g5lznlz.googlevideo.com", "74.125.104.73"),    // Blocked
    ];

    // --- Section: TCP Connectivity ---

    /// <summary>
    /// Тестирует raw TCP-подключение к порту 443 каждого известного CDN IP.
    /// Проходит до TLS — позволяет отделить сетевую блокировку от TLS/HTTP.
    /// </summary>
    [TestMethod(TestCategory.Integration, "CDN: TCP Connectivity per IP",
        Order = 10, Group = "CdnDiagnostic", RequiresNetwork = true, TimeoutSeconds = 60)]
    public static async Task TestTcpConnectivityAsync(IServiceProvider _)
    {
        int reachable = 0;
        int blocked = 0;

        Log.Info("═══════════════════════════════════════════════════════════════");
        Log.Info("  CDN TCP CONNECTIVITY (raw TCP, no TLS, no HTTP)");
        Log.Info("═══════════════════════════════════════════════════════════════\n");

        foreach (var (host, ip) in KnownCdnEndpoints)
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var sw = Stopwatch.StartNew();

            try
            {
                using var socket = new Socket(
                    AddressFamily.InterNetwork,
                    SocketType.Stream,
                    ProtocolType.Tcp);

                await socket.ConnectAsync(
                    new IPEndPoint(IPAddress.Parse(ip), 443),
                    cts.Token);

                sw.Stop();
                reachable++;
                Log.Info($"  ✅ TCP OK   {ip,-20} {host[..Math.Min(host.Length, 38)],-38} {sw.ElapsedMilliseconds}ms");
            }
            catch (OperationCanceledException)
            {
                sw.Stop();
                blocked++;
                Log.Warn($"  ❌ TIMEOUT  {ip,-20} {host[..Math.Min(host.Length, 38)],-38} (>{sw.ElapsedMilliseconds}ms)");
            }
            catch (Exception ex)
            {
                sw.Stop();
                blocked++;
                Log.Warn($"  ❌ ERROR    {ip,-20} {host[..Math.Min(host.Length, 38)],-38} {ex.Message}");
            }
        }

        Log.Info($"\n  Result: {reachable} reachable, {blocked} blocked/timeout");
        Log.Info("═══════════════════════════════════════════════════════════════");

        if (reachable == 0)
            throw new Exception("All CDN IPs are unreachable via TCP — network/firewall issue");
    }

    // --- Section: Path Discrimination ---

    /// <summary>
    /// Тестирует разницу в поведении ТСПУ между путями <c>/generate_204</c>
    /// и <c>/videoplayback</c> на одном и том же хосте и IP.
    /// Если 204 проходит, а videoplayback нет — блокировка по URL/payload.
    /// </summary>
    [TestMethod(TestCategory.Integration, "CDN: Path Discrimination (204 vs videoplayback)",
        Order = 20, Group = "CdnDiagnostic", RequiresNetwork = true, TimeoutSeconds = 120)]
    public static async Task TestPathDiscriminationAsync(IServiceProvider services)
    {
        Log.Info("═══════════════════════════════════════════════════════════════");
        Log.Info("  CDN PATH DISCRIMINATION: /generate_204 vs /videoplayback");
        Log.Info("═══════════════════════════════════════════════════════════════\n");

        var youtube = services.GetRequiredService<Lazy<YoutubeProvider>>().Value.GetClient();
        var videoId = TestConfig.Get().Pipeline.DebugVideoId;

        using var manifestCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var manifest = await youtube.Videos.Streams.GetManifestAsync(
            VideoId.Parse(videoId), manifestCts.Token);

        var audioStream = manifest.GetAudioOnlyStreams()
            .OrderByDescending(s => s.Bitrate.BitsPerSecond)
            .First();

        var streamUrl = audioStream.Url;
        var uri = new Uri(streamUrl);
        var host = uri.Host;

        Log.Info($"  Track:    {videoId}");
        Log.Info($"  CDN host: {host}");
        Log.Info($"  itag:     {audioStream.Itag}");
        Log.Info("");

        // --- Test A: /generate_204 ---
        Log.Info("  [A] GET /generate_204 ...");
        await RunPathTestAsync(
            $"https://{host}/generate_204",
            "generate_204",
            audioStream.Url);

        // --- Test B: /videoplayback Range 0-1023 ---
        Log.Info("\n  [B] GET /videoplayback Range: bytes=0-1023 ...");
        await RunPathTestAsync(
            streamUrl,
            "videoplayback bytes=0-1023",
            audioStream.Url,
            range: (0, 1023));

        // --- Test C: /videoplayback Range 65536-131071 (non-zero offset) ---
        Log.Info("\n  [C] GET /videoplayback Range: bytes=65536-131071 (non-zero) ...");
        await RunPathTestAsync(
            streamUrl,
            "videoplayback bytes=65536-131071",
            audioStream.Url,
            range: (65536, 131071));

        Log.Info("\n═══════════════════════════════════════════════════════════════");
        Log.Info("  INTERPRETATION:");
        Log.Info("  A=✅ B=❌ C=❌ → ТСПУ blocks /videoplayback path");
        Log.Info("  A=✅ B=❌ C=✅ → ТСПУ blocks only Range: bytes=0-N (start of stream)");
        Log.Info("  A=✅ B=✅ C=✅ → Zapret is working correctly");
        Log.Info("  A=❌ B=❌ C=❌ → Full block on this CDN host");
        Log.Info("═══════════════════════════════════════════════════════════════");
    }

    // --- Section: Zapret Coverage ---

    /// <summary>
    /// Проверяет покрытие Google CDN IP-диапазонов через запрос к каждому из них
    /// напрямую с явным SNI. Позволяет точно определить какие подсети проходят
    /// через zapret, а какие нет.
    /// </summary>
    [TestMethod(TestCategory.Integration, "CDN: Zapret IP Coverage Check",
        Order = 30, Group = "CdnDiagnostic", RequiresNetwork = true, TimeoutSeconds = 120)]
    public static async Task TestZapretCoverageAsync(IServiceProvider _)
    {
        Log.Info("═══════════════════════════════════════════════════════════════");
        Log.Info("  ZAPRET COVERAGE: /generate_204 per known CDN IP");
        Log.Info("═══════════════════════════════════════════════════════════════\n");

        Log.Info($"  {"IP",-20} {"Subnet",-18} {"204",-6} {"Latency",-10} Host");
        Log.Info($"  {new string('-', 80)}");

        foreach (var (host, ip) in KnownCdnEndpoints)
        {
            var subnet = GetSubnet(ip);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var sw = Stopwatch.StartNew();

            try
            {
                using var handler = new SocketsHttpHandler
                {
                    ConnectCallback = SharedHttpClient.ConnectWithKeepAliveAsync,
                    UseProxy = false,
                    ConnectTimeout = TimeSpan.FromSeconds(4),
                };

                using var client = new HttpClient(handler)
                {
                    Timeout = TimeSpan.FromSeconds(5),
                };

                using var request = new HttpRequestMessage(
                    HttpMethod.Get,
                    $"https://{host}/generate_204");
                SharedHttpClient.ApplyUserAgentFromUrl(request, $"https://{host}/");

                using var response = await client.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    cts.Token);

                sw.Stop();
                var status = (int)response.StatusCode;
                var icon = status is 200 or 204 ? "✅" : $"⚠{status}";

                Log.Info($"  {ip,-20} {subnet,-18} {icon,-6} {sw.ElapsedMilliseconds}ms{"",-5} {host}");
            }
            catch (OperationCanceledException)
            {
                sw.Stop();
                Log.Warn($"  {ip,-20} {subnet,-18} {"❌",-6} TIMEOUT       {host}");
            }
            catch (Exception ex)
            {
                sw.Stop();
                var reason = ex.InnerException?.Message ?? ex.Message;
                Log.Warn($"  {ip,-20} {subnet,-18} {"❌",-6} {reason[..Math.Min(reason.Length, 20)]}");
            }
        }

        Log.Info($"\n  Subnets legend:");
        Log.Info("  74.125.x.x   → Google CDN AS15169 (часто блокируется ТСПУ без zapret)");
        Log.Info("  173.194.x.x  → Google CDN AS15169");
        Log.Info("  172.217.x.x  → Google Fiber / YouTube AS15169");
        Log.Info("  37.79.x.x    → Google CDN Europe");
        Log.Info("═══════════════════════════════════════════════════════════════");
    }

    // --- Section: Live CDN from Fresh Manifest ---

    /// <summary>
    /// Получает свежий манифест, извлекает текущий CDN-хост и тестирует
    /// полную цепочку: DNS → TCP → TLS → 204 → videoplayback.
    /// Наиболее приближён к реальному воспроизведению.
    /// </summary>
    [TestMethod(TestCategory.Integration, "CDN: Full Chain Live Test",
        Order = 40, Group = "CdnDiagnostic", RequiresNetwork = true, TimeoutSeconds = 90)]
    public static async Task TestLiveFullChainAsync(IServiceProvider services)
    {
        Log.Info("═══════════════════════════════════════════════════════════════");
        Log.Info("  LIVE FULL CHAIN: DNS → TCP → TLS → 204 → videoplayback");
        Log.Info("═══════════════════════════════════════════════════════════════\n");

        var youtube = services.GetRequiredService<Lazy<YoutubeProvider>>().Value.GetClient();
        var videoId = TestConfig.Get().Pipeline.DebugVideoId;

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        // Step 1: DNS + Manifest
        Log.Info("[1/4] Resolving manifest...");
        var sw = Stopwatch.StartNew();
        var manifest = await youtube.Videos.Streams.GetManifestAsync(
            VideoId.Parse(videoId), cts.Token);
        Log.Info($"      ✅ {sw.ElapsedMilliseconds}ms — got {manifest.GetAudioOnlyStreams().Count()} audio streams");

        var stream = manifest.GetAudioOnlyStreams()
            .OrderByDescending(s => s.Bitrate.BitsPerSecond)
            .First();

        var uri = new Uri(stream.Url);
        var host = uri.Host;

        // Step 2: DNS resolution
        Log.Info($"\n[2/4] DNS resolution for {host}...");
        sw.Restart();
        var addresses = await Dns.GetHostAddressesAsync(host, AddressFamily.InterNetwork, cts.Token);
        Log.Info($"      ✅ {sw.ElapsedMilliseconds}ms — resolved {addresses.Length} IPv4 address(es):");
        foreach (var addr in addresses)
            Log.Info($"         {addr}");

        var targetIp = addresses[0];

        // Step 3: /generate_204
        Log.Info($"\n[3/4] GET /generate_204 → {targetIp}...");
        sw.Restart();
        try
        {
            using var handler = BuildHandler();
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
            using var req = new HttpRequestMessage(HttpMethod.Get, $"https://{host}/generate_204");
            SharedHttpClient.ApplyUserAgentFromUrl(req, stream.Url);
            using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            Log.Info($"      ✅ {sw.ElapsedMilliseconds}ms — HTTP {(int)resp.StatusCode}");
        }
        catch (Exception ex)
        {
            Log.Error($"      ❌ {sw.ElapsedMilliseconds}ms — {FormatException(ex)}");
        }

        // Step 4: /videoplayback bytes=0-1023
        Log.Info($"\n[4/4] GET /videoplayback Range: bytes=0-1023 → {targetIp}...");
        sw.Restart();
        try
        {
            using var handler = BuildHandler();
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
            using var req = new HttpRequestMessage(HttpMethod.Get, stream.Url);
            req.Headers.Range = new RangeHeaderValue(0, 1023);
            SharedHttpClient.ApplyUserAgentFromUrl(req, stream.Url);
            using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);

            if (resp.IsSuccessStatusCode || resp.StatusCode == HttpStatusCode.PartialContent)
            {
                var bytes = await resp.Content.ReadAsByteArrayAsync(cts.Token);
                Log.Info($"      ✅ {sw.ElapsedMilliseconds}ms — HTTP {(int)resp.StatusCode}, {bytes.Length} bytes");

                bool isWebM = bytes.Length >= 4 &&
                    bytes[0] == 0x1A && bytes[1] == 0x45 && bytes[2] == 0xDF && bytes[3] == 0xA3;
                Log.Info($"         Format: {(isWebM ? "WebM/Opus ✅" : $"Unknown — magic: {BitConverter.ToString(bytes, 0, Math.Min(8, bytes.Length))}")}");
            }
            else
            {
                var body = await resp.Content.ReadAsStringAsync(cts.Token);
                Log.Error($"      ❌ {sw.ElapsedMilliseconds}ms — HTTP {(int)resp.StatusCode}: {body[..Math.Min(body.Length, 200)]}");
            }
        }
        catch (Exception ex)
        {
            Log.Error($"      ❌ {sw.ElapsedMilliseconds}ms — {FormatException(ex)}");
        }

        Log.Info("\n═══════════════════════════════════════════════════════════════");
        Log.Info("  DIAGNOSIS:");
        Log.Info("  [3]=✅ [4]=❌ → ТСПУ drops /videoplayback. Fix: zapret winws.cmd rules");
        Log.Info("  [3]=❌ [4]=❌ → Full IP block. Fix: add to ipset-all.txt, restart zapret");
        Log.Info("  [3]=✅ [4]=✅ → CDN working correctly");
        Log.Info("═══════════════════════════════════════════════════════════════");
    }

    // --- Section: Zapret Config Check ---

    /// <summary>
    /// Читает файлы конфигурации zapret и проверяет наличие необходимых
    /// Google CDN диапазонов. Не требует сети.
    /// </summary>
    [TestMethod(TestCategory.Unit, "CDN: Zapret Config Audit",
        Order = 5, Group = "CdnDiagnostic", RequiresNetwork = false, TimeoutSeconds = 10)]
    public static Task TestZapretConfigAuditAsync(IServiceProvider _)
    {
        const string zapretBase = @"C:\Users\paralax\Desktop\zapret-windows\lists";

        var requiredSubnets = new[]
        {
            "74.125.0.0/16",
            "173.194.0.0/16",
            "142.251.0.0/16",
            "216.58.0.0/16",
            "172.217.0.0/16",
            "64.233.160.0/19",
        };

        Log.Info("═══════════════════════════════════════════════════════════════");
        Log.Info("  ZAPRET CONFIG AUDIT");
        Log.Info($"  Base path: {zapretBase}\n");

        var ipsetFile = Path.Combine(zapretBase, "ipset-all.txt");
        CheckFileForSubnets(ipsetFile, requiredSubnets, "ipset-all.txt");

        var googleFile = Path.Combine(zapretBase, "list-google.txt");
        CheckFileForDomains(googleFile, ["googlevideo.com", "youtube.com"], "list-google.txt");

        var generalFile = Path.Combine(zapretBase, "list-general.txt");
        CheckFileForDomains(generalFile, ["googlevideo.com"], "list-general.txt");

        Log.Info("\n  NEXT STEPS if subnets missing in ipset-all.txt:");
        Log.Info("  1. Add missing subnets to ipset-all.txt");
        Log.Info("  2. Restart zapret: run service_install_*.cmd as Administrator");
        Log.Info("  3. Verify winws.cmd has rules for *.googlevideo.com");
        Log.Info("═══════════════════════════════════════════════════════════════");

        return Task.CompletedTask;
    }

    [TestMethod(TestCategory.Integration, "CDN: Problem IP Direct Test",
        Order = 25, Group = "CdnDiagnostic", RequiresNetwork = true, TimeoutSeconds = 60)]
    public static async Task TestProblemIpDirectAsync(IServiceProvider services)
    {
        const string host = "rr4---sn-4g5lznlz.googlevideo.com";
        const string ip = "74.125.104.73";

        Log.Info($"═══ DIRECT TEST: {host} ({ip}) ═══\n");

        // Берём URL трека из кэша или свежий manifest
        var youtube = services.GetRequiredService<Lazy<YoutubeProvider>>().Value.GetClient();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var manifest = await youtube.Videos.Streams
            .GetManifestAsync(VideoId.Parse("fWzEbfbL11g"), cts.Token);

        var stream = manifest.GetAudioOnlyStreams()
            .OrderByDescending(s => s.Bitrate.BitsPerSecond).First();

        Log.Info($"  Stream URL host: {new Uri(stream.Url).Host}");
        Log.Info($"  itag={stream.Itag}\n");

        Log.Info("  [A] /generate_204 ...");
        await RunPathTestAsync($"https://{host}/generate_204", "generate_204", stream.Url);

        Log.Info("\n  [B] /videoplayback bytes=0-1023 ...");
        await RunPathTestAsync(stream.Url, "videoplayback 0-1023", stream.Url, range: (0, 1023));

        Log.Info("\n  [C] /videoplayback bytes=65536-131071 ...");
        await RunPathTestAsync(stream.Url, "videoplayback 65536-131071", stream.Url, range: (65536, 131071));

        Log.Info("\n═══════════════════════════════════════════════════════════════");
    }

    // --- Section: Multi-Manifest CDN Discovery ---

    /// <summary>
    /// Запрашивает манифест несколько раз (YouTube ротирует CDN-хосты между запросами),
    /// собирает все уникальные хосты и для каждого проверяет media path.
    /// <para>
    /// Ключевой тест: показывает полную картину доступности CDN в текущей сети.
    /// Если ни один хост не отдаёт media — zapret не работает для этого провайдера.
    /// Если часть хостов заблокирована — нужен CDN failover.
    /// </para>
    /// </summary>
    [TestMethod(TestCategory.Integration, "CDN: Multi-Manifest Discovery + Media Probe",
     Order = 50, Group = "CdnDiagnostic", RequiresNetwork = true, TimeoutSeconds = 180)]
    public static async Task TestMultiManifestMediaProbeAsync(IServiceProvider services)
    {
        const int interRequestDelayMs = 2000;

        Log.Info("═══════════════════════════════════════════════════════════════");
        Log.Info("  MULTI-MANIFEST CDN DISCOVERY + MEDIA PROBE");
        Log.Info("  Multiple videos for CDN diversity");
        Log.Info("═══════════════════════════════════════════════════════════════\n");

        var youtube = services.GetRequiredService<Lazy<YoutubeProvider>>().Value.GetClient();
        var config = TestConfig.Get();

        // Разные видео → YouTube чаще назначает разные CDN-ноды
        var videoIds = config.Pipeline.TestVideoIds;
        if (videoIds.Length == 0)
            videoIds = ["dQw4w9WgXcQ", "jNQXAC9IVRw", "kJQP7kiw5Fk"];

        var hostToUrl = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < videoIds.Length; i++)
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            Log.Info($"  [{i + 1}/{videoIds.Length}] Manifest for {videoIds[i]}...");

            try
            {
                var manifest = await youtube.Videos.Streams
                    .GetManifestAsync(VideoId.Parse(videoIds[i]), cts.Token);

                foreach (var stream in manifest.GetAudioOnlyStreams())
                {
                    var host = new Uri(stream.Url).Host;
                    // Сохраняем URL с наибольшим битрейтом для probe
                    if (!hostToUrl.ContainsKey(host))
                        hostToUrl[host] = stream.Url;
                }

                Log.Info($"           Unique hosts so far: {hostToUrl.Count}");
            }
            catch (Exception ex)
            {
                Log.Warn($"           Failed: {ex.Message}");
            }

            if (i < videoIds.Length - 1)
                await Task.Delay(interRequestDelayMs);
        }

        if (hostToUrl.Count == 0)
            throw new Exception("No CDN hosts discovered from any manifest request");

        Log.Info($"\n  Discovered {hostToUrl.Count} unique CDN host(s). Probing media path...\n");
        Log.Info($"  {"Host",-45} {"IP",-18} {"204",-8} {"Media",-8} {"Latency",-10} Diagnosis");
        Log.Info($"  {new string('─', 100)}");

        int available = 0;
        int tspuBlocked = 0;
        int fullBlocked = 0;

        foreach (var (host, url) in hostToUrl)
        {
            var ip = MediaPathProbe.ResolveToIp(host);
            var result = await MediaPathProbe.ProbeHostAsync(url, timeoutMs: 5000);

            var (diagnosis, classification) = ClassifyProbeResult(result);
            switch (classification)
            {
                case ProbeClassification.Available: available++; break;
                case ProbeClassification.TspuBlocked: tspuBlocked++; break;
                case ProbeClassification.FullBlocked: fullBlocked++; break;
            }

            string healthIcon = result.HealthCheckOk ? "✅" : "❌";
            string mediaIcon = result.MediaOk ? "✅" : "❌";
            string truncHost = host.Length > 43 ? host[..43] : host;

            Log.Info(
                $"  {truncHost,-45} {ip,-18} {healthIcon,-8} {mediaIcon,-8} " +
                $"{result.MediaMs + "ms",-10} {diagnosis}");
        }

        Log.Info($"\n  ═══ SUMMARY ═══");
        Log.Info($"  Available:    {available}/{hostToUrl.Count}");
        Log.Info($"  ТСПУ blocked: {tspuBlocked}/{hostToUrl.Count}");
        Log.Info($"  Full blocked: {fullBlocked}/{hostToUrl.Count}");

        Log.Info($"\n  INTERPRETATION:");
        if (available == 0)
            Log.Warn("  ❌ ALL hosts blocked — zapret is not covering this CDN/provider");
        else if (tspuBlocked > 0)
            Log.Warn($"  ⚠ {tspuBlocked} host(s) blocked — CDN failover needed");
        else
            Log.Info("  ✅ All hosts available — no blocking detected");

        Log.Info("═══════════════════════════════════════════════════════════════");

        if (available == 0)
            throw new Exception(
                $"All {hostToUrl.Count} CDN hosts are blocked for media path. " +
                "Zapret is not effective for this provider/network.");
    }

    // --- Section: Network Transition Comparison ---

    /// <summary>
    /// Двухфазный тест для сравнения CDN-доступности при смене сети.
    /// <para>
    /// <b>Фаза 1</b> (первый запуск): собирает baseline, сохраняет в памяти.
    /// <b>Фаза 2</b> (после переключения сети): собирает текущее состояние, показывает diff.
    /// </para>
    /// <para>
    /// Сценарий использования:
    /// 1. Запустить тест на VPN → baseline сохранён
    /// 2. Переключить на WiFi+Zapret
    /// 3. Запустить тест снова → diff показывает что сломалось
    /// </para>
    /// </summary>
    [TestMethod(TestCategory.Integration, "CDN: Network Transition Comparison (run twice)",
        Order = 60, Group = "CdnDiagnostic", RequiresNetwork = true, TimeoutSeconds = 180)]
    public static async Task TestNetworkTransitionAsync(IServiceProvider services)
    {
        bool isBaseline = _networkBaseline == null;
        string outboundIp = GetCurrentOutboundIp();

        Log.Info("═══════════════════════════════════════════════════════════════");
        Log.Info(isBaseline
            ? "  NETWORK BASELINE (Phase 1 — save current state)"
            : "  NETWORK COMPARISON (Phase 2 — diff with baseline)");
        Log.Info($"  Outbound IP: {outboundIp}");
        if (!isBaseline)
            Log.Info($"  Baseline IP: {_baselineOutboundIp}");
        Log.Info("═══════════════════════════════════════════════════════════════\n");

        if (!isBaseline && outboundIp == _baselineOutboundIp)
            Log.Warn("  ⚠ Outbound IP unchanged — did you switch networks?\n");

        var youtube = services.GetRequiredService<Lazy<YoutubeProvider>>().Value.GetClient();
        var videoId = TestConfig.Get().Pipeline.DebugVideoId;

        // Собираем хосты из свежего манифеста
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var manifest = await youtube.Videos.Streams
            .GetManifestAsync(VideoId.Parse(videoId), cts.Token);

        var hostToUrl = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var stream in manifest.GetAudioOnlyStreams())
        {
            var host = new Uri(stream.Url).Host;
            hostToUrl.TryAdd(host, stream.Url);
        }

        // Также проверяем хосты из baseline (если есть)
        if (_networkBaseline != null)
        {
            // Baseline хосты могли не попасть в новый манифест —
            // для них URL нет, но мы можем проверить 204
            foreach (var snap in _networkBaseline)
            {
                if (!hostToUrl.ContainsKey(snap.Host))
                    Log.Info($"  ℹ Baseline host {snap.Host} not in new manifest (CDN rotation)");
            }
        }

        // Probe текущих хостов
        var currentSnapshot = new List<CdnHostSnapshot>(hostToUrl.Count);

        Log.Info($"  {"Host",-45} {"IP",-18} {"204",-8} {"Media",-8} {"ms",-8}");
        Log.Info($"  {new string('─', 90)}");

        foreach (var (host, url) in hostToUrl)
        {
            var ip = MediaPathProbe.ResolveToIp(host);
            var result = await MediaPathProbe.ProbeHostAsync(url, timeoutMs: 5000);

            currentSnapshot.Add(new CdnHostSnapshot(
                host, ip,
                result.HealthCheckOk, result.HealthCheckMs,
                result.MediaOk, result.MediaMs,
                result.MediaError));

            string healthIcon = result.HealthCheckOk ? "✅" : "❌";
            string mediaIcon = result.MediaOk ? "✅" : "❌";
            string truncHost = host.Length > 43 ? host[..43] : host;

            Log.Info($"  {truncHost,-45} {ip,-18} {healthIcon,-8} {mediaIcon,-8} {result.MediaMs}ms");
        }

        // Diff
        if (!isBaseline && _networkBaseline != null)
        {
            Log.Info($"\n  ═══ DIFF (baseline → current) ═══");

            var baselineByHost = _networkBaseline.ToDictionary(
                s => s.Host, StringComparer.OrdinalIgnoreCase);

            bool anyRegression = false;

            foreach (var current in currentSnapshot)
            {
                if (!baselineByHost.TryGetValue(current.Host, out var baseline))
                {
                    Log.Info($"  🆕 {current.Host} — new host (not in baseline)");
                    continue;
                }

                if (baseline.MediaOk && !current.MediaOk)
                {
                    Log.Warn($"  🔴 {current.Host}: media WORKED → BLOCKED ({current.MediaError})");
                    anyRegression = true;
                }
                else if (!baseline.MediaOk && current.MediaOk)
                {
                    Log.Info($"  🟢 {current.Host}: media WAS BLOCKED → WORKS");
                }
                else if (current.MediaOk)
                {
                    Log.Info($"  ⚪ {current.Host}: media works in both");
                }
                else
                {
                    Log.Info($"  ⚫ {current.Host}: media blocked in both");
                }
            }

            // Хосты из baseline, которых нет в текущем манифесте
            foreach (var baseline in _networkBaseline)
            {
                if (!currentSnapshot.Any(c =>
                    string.Equals(c.Host, baseline.Host, StringComparison.OrdinalIgnoreCase)))
                {
                    Log.Info($"  ➖ {baseline.Host} — was in baseline, not in current manifest");
                }
            }

            if (anyRegression)
                Log.Warn("\n  ⚠ Regressions detected — CDN hosts lost media access");
            else
                Log.Info("\n  ✅ No regressions");
        }

        // Сохраняем как baseline для следующего запуска
        _networkBaseline = currentSnapshot;
        _baselineOutboundIp = outboundIp;

        if (isBaseline)
        {
            Log.Info("\n  📌 Baseline saved. Switch network and run this test again.");
            Log.Info("     Reset baseline: restart the app or run a different CDN test first.");
        }

        Log.Info("═══════════════════════════════════════════════════════════════");
    }

    // --- Section: Active Stream Probe ---

    /// <summary>
    /// Берёт URL текущего воспроизводимого трека из <see cref="SessionCacheStore"/>
    /// и проверяет, доступен ли его CDN-хост для media path прямо сейчас.
    /// <para>
    /// Отвечает на вопрос: "после переключения сети текущий стрим ещё работает?"
    /// </para>
    /// </summary>
    [TestMethod(TestCategory.Integration, "CDN: Probe Active/Recent Stream",
        Order = 55, Group = "CdnDiagnostic", RequiresNetwork = true, TimeoutSeconds = 30)]
    public static async Task TestActiveStreamProbeAsync()
    {
        Log.Info("═══════════════════════════════════════════════════════════════");
        Log.Info("  ACTIVE STREAM MEDIA PROBE");
        Log.Info("═══════════════════════════════════════════════════════════════\n");

        // Пытаемся получить URL из session cache (последний известный manifest)
        var allManifests = SessionCacheStore.GetAllCachedTrackIds();

        if (allManifests.Count == 0)
        {
            Log.Warn("  No cached manifests found. Play a track first, then re-run.");
            throw new Exception("No cached stream manifests available for probing");
        }

        Log.Info($"  Found {allManifests.Count} cached manifest(s). Probing each...\n");
        Log.Info($"  {"TrackId",-16} {"Host",-42} {"204",-8} {"Media",-8} {"ms",-8} Diagnosis");
        Log.Info($"  {new string('─', 95)}");

        int probed = 0;
        int available = 0;
        int blocked = 0;

        foreach (var trackId in allManifests.Take(5)) // ограничиваем чтобы тест не затягивался
        {
            var manifest = SessionCacheStore.GetManifest(trackId);
            if (manifest is not { Variants.Count: > 0 })
                continue;

            var variant = manifest.Variants[0];
            if (string.IsNullOrWhiteSpace(variant.Url))
                continue;

            var host = MediaPathProbe.ExtractHost(variant.Url);
            var result = await MediaPathProbe.ProbeHostAsync(variant.Url, timeoutMs: 5000);
            probed++;

            var (diagnosis, classification) = ClassifyProbeResult(result);
            switch (classification)
            {
                case ProbeClassification.Available: available++; break;
                case ProbeClassification.TspuBlocked: blocked++; break;
                case ProbeClassification.FullBlocked: blocked++; break;
            }

            string healthIcon = result.HealthCheckOk ? "✅" : "❌";
            string mediaIcon = result.MediaOk ? "✅" : "❌";
            string truncId = trackId.Length > 14 ? trackId[..14] : trackId;
            string truncHost = host.Length > 40 ? host[..40] : host;

            Log.Info($"  {truncId,-16} {truncHost,-42} {healthIcon,-8} {mediaIcon,-8} {result.MediaMs + "ms",-8} {diagnosis}");
        }

        Log.Info($"\n  Probed: {probed}, Available: {available}, Blocked: {blocked}");

        if (probed > 0 && available == 0)
        {
            Log.Warn("  ⚠ All recently-used CDN hosts are blocked for media.");
            Log.Warn("  → Current playback will fail. Network switch or zapret reconfiguration needed.");
        }

        Log.Info("═══════════════════════════════════════════════════════════════");

        if (probed == 0)
            throw new Exception("No valid stream URLs found in session cache");
    }

    // --- Section: Blacklist Unit Test ---

    /// <summary>
    /// Проверяет корректность <see cref="CdnBlacklist"/>: add, check, TTL expiry, URL filtering.
    /// </summary>
    [TestMethod(TestCategory.Unit, "CDN: Blacklist Logic",
        Order = 6, Group = "CdnDiagnostic", RequiresNetwork = false, TimeoutSeconds = 5)]
    public static async Task TestBlacklistLogicAsync(IServiceProvider _)
    {
        Log.Info("═══════════════════════════════════════════════════════════════");
        Log.Info("  CDN BLACKLIST UNIT TEST");
        Log.Info("═══════════════════════════════════════════════════════════════\n");

        // --- Add & check ---
        var blacklist = new CdnBlacklist(ttl: TimeSpan.FromMilliseconds(300));
        blacklist.MarkBlocked("rr1---sn-blocked.googlevideo.com");

        AssertTrue(blacklist.IsBlocked("rr1---sn-blocked.googlevideo.com"), "Blocked host should be blocked");
        AssertTrue(!blacklist.IsBlocked("rr2---sn-clean.googlevideo.com"), "Clean host should not be blocked");
        Log.Info("  ✅ Add & check");

        // --- Case-insensitive ---
        AssertTrue(blacklist.IsBlocked("RR1---SN-BLOCKED.GOOGLEVIDEO.COM"), "Should be case-insensitive");
        Log.Info("  ✅ Case-insensitive");

        // --- URL filtering ---
        AssertTrue(
            blacklist.IsBlockedUrl("https://rr1---sn-blocked.googlevideo.com/videoplayback?id=1"),
            "URL with blocked host should be blocked");
        AssertTrue(
            !blacklist.IsBlockedUrl("https://rr2---sn-clean.googlevideo.com/videoplayback?id=1"),
            "URL with clean host should not be blocked");
        AssertTrue(
            !blacklist.IsBlockedUrl(""),
            "Empty URL should not be blocked");
        Log.Info("  ✅ URL filtering");

        // --- TTL expiry ---
        await Task.Delay(400);
        AssertTrue(!blacklist.IsBlocked("rr1---sn-blocked.googlevideo.com"), "Should expire after TTL");
        AssertTrue(blacklist.Count == 0, "Expired entry should be removed lazily");
        Log.Info("  ✅ TTL expiry");

        // --- Unblock ---
        blacklist.MarkBlocked("rr3---sn-temp.googlevideo.com");
        blacklist.Unblock("rr3---sn-temp.googlevideo.com");
        AssertTrue(!blacklist.IsBlocked("rr3---sn-temp.googlevideo.com"), "Unblocked host should not be blocked");
        Log.Info("  ✅ Unblock");

        // --- GetBlockedHosts ---
        blacklist.MarkBlocked("host-a.googlevideo.com");
        blacklist.MarkBlocked("host-b.googlevideo.com");
        var hosts = blacklist.GetBlockedHosts();
        AssertTrue(hosts.Count == 2, $"Expected 2 blocked hosts, got {hosts.Count}");
        Log.Info("  ✅ GetBlockedHosts");

        // --- Clear ---
        blacklist.Clear();
        AssertTrue(blacklist.Count == 0, "Clear should remove all entries");
        Log.Info("  ✅ Clear");

        Log.Info("\n  All blacklist tests passed.");
        Log.Info("═══════════════════════════════════════════════════════════════");
    }

    // --- Section: DNS Resolution Diagnostic ---

    /// <summary>
    /// Тестирует DNS resolution для ключевых YouTube-доменов через системный DNS
    /// и через альтернативные DNS-серверы (Google, Cloudflare).
    /// <para>
    /// Выявляет провайдерскую DNS-блокировку YouTube — главную причину
    /// "не работает без VPN" при рабочем CDN.
    /// </para>
    /// </summary>
    [TestMethod(TestCategory.Integration, "CDN: DNS Resolution Diagnostic",
        Order = 3, Group = "CdnDiagnostic", RequiresNetwork = true, TimeoutSeconds = 30)]
    public static async Task TestDnsResolutionAsync()
    {
        Log.Info("═══════════════════════════════════════════════════════════════");
        Log.Info("  DNS RESOLUTION DIAGNOSTIC");
        Log.Info($"  Outbound IP: {GetCurrentOutboundIp()}");
        Log.Info("═══════════════════════════════════════════════════════════════\n");

        var domains = new[]
        {
        "www.youtube.com",
        "music.youtube.com",
        "jnn-pa.googleapis.com",     // BotGuard / PoToken
        "rr1---sn-4g5lznsz.googlevideo.com", // CDN (для сравнения)
    };

        Log.Info($"  {"Domain",-38} {"System DNS",-22} {"Latency",-10} Result");
        Log.Info($"  {new string('─', 85)}");

        int resolved = 0;
        int failed = 0;
        bool youtubeBlocked = false;
        bool cdnOk = false;

        foreach (var domain in domains)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                var addresses = await Dns.GetHostAddressesAsync(
                    domain, AddressFamily.InterNetwork);
                sw.Stop();

                if (addresses.Length > 0)
                {
                    resolved++;
                    string firstIp = addresses[0].ToString();
                    Log.Info($"  {domain,-38} ✅ {firstIp,-20} {sw.ElapsedMilliseconds}ms");

                    if (domain.EndsWith("googlevideo.com", StringComparison.OrdinalIgnoreCase))
                        cdnOk = true;
                }
                else
                {
                    failed++;
                    Log.Warn($"  {domain,-38} ❌ (0 addresses)       {sw.ElapsedMilliseconds}ms");
                }
            }
            catch (SocketException ex)
            {
                sw.Stop();
                failed++;
                Log.Warn($"  {domain,-38} ❌ {ex.SocketErrorCode,-20} {sw.ElapsedMilliseconds}ms");

                if (domain.Contains("youtube.com"))
                    youtubeBlocked = true;
            }
            catch (Exception ex)
            {
                sw.Stop();
                failed++;
                Log.Warn($"  {domain,-38} ❌ {ex.Message[..Math.Min(ex.Message.Length, 20)],-20} {sw.ElapsedMilliseconds}ms");

                if (domain.Contains("youtube.com"))
                    youtubeBlocked = true;
            }
        }

        // Попытка DNS через альтернативные серверы (UDP raw)
        Log.Info($"\n  Alternative DNS servers (UDP direct):");
        Log.Info($"  {"Domain",-38} {"DNS Server",-18} {"Result",-22} Latency");
        Log.Info($"  {new string('─', 85)}");

        var dnsServers = new (string Name, string Ip)[]
        {
        ("Google", "8.8.8.8"),
        ("Cloudflare", "1.1.1.1"),
        ("Yandex", "77.88.8.8"),
        };

        // Тестируем только youtube.com — он ключевой
        foreach (var (name, dnsIp) in dnsServers)
        {
            var result = await TryResolveDnsDirectAsync(
                "www.youtube.com", dnsIp);

            string icon = result.Success ? "✅" : "❌";
            string resolved_ip = result.Success
                ? result.Address ?? "(ok)"
                : result.Error ?? "failed";

            Log.Info(
                $"  {"www.youtube.com",-38} {name + " (" + dnsIp + ")",-18} " +
                $"{icon} {resolved_ip,-18} {result.Ms}ms");
        }

        // DoH (DNS-over-HTTPS) — используется как fallback в приложении
        Log.Info($"\n  DoH (DNS-over-HTTPS) resolution:");
        Log.Info($"  {"Domain",-38} {"Provider",-18} {"Result",-22} Latency");
        Log.Info($"  {new string('─', 85)}");

        var dohDomains = new[] { "www.youtube.com", "music.youtube.com" };
        foreach (var domain in dohDomains)
        {
            // Инвалидируем кэш чтобы проверить реальный DoH
            DohResolver.InvalidateCache();

            var sw = Stopwatch.StartNew();
            var result = await DohResolver.ResolveAsync(domain);
            sw.Stop();

            if (result is { Length: > 0 })
            {
                Log.Info($"  {domain,-38} {"Google DoH",-18} ✅ {result[0],-18} {sw.ElapsedMilliseconds}ms");
            }
            else
            {
                Log.Warn($"  {domain,-38} {"Google DoH",-18} ❌ {"Failed",-18} {sw.ElapsedMilliseconds}ms");
            }
        }

        // Диагноз
        Log.Info($"\n  ═══ DIAGNOSIS ═══");
        Log.Info($"  System DNS:  {resolved} resolved, {failed} failed");

        if (youtubeBlocked && cdnOk)
        {
            Log.Warn("  🔴 PROVIDER DNS BLOCKS youtube.com BUT NOT googlevideo.com CDN");
            Log.Warn("     → API requests fail, but CDN media path works");
            Log.Warn("     → Fix: configure DNS-over-HTTPS or use 8.8.8.8/1.1.1.1");
            Log.Warn("     → Zapret does NOT fix DNS-level blocking");
        }
        else if (youtubeBlocked && !cdnOk)
        {
            Log.Warn("  🔴 FULL DNS BLOCK — both youtube.com and CDN unresolvable");
            Log.Warn("     → Need DoH or VPN");
        }
        else if (!youtubeBlocked && failed > 0)
        {
            Log.Info("  ⚠ Partial DNS issues (some domains fail)");
        }
        else
        {
            Log.Info("  ✅ All domains resolve — DNS is not the problem");
        }

        Log.Info("═══════════════════════════════════════════════════════════════");

        if (youtubeBlocked)
            throw new Exception(
                "DNS resolution blocked for youtube.com — provider-level DNS blocking detected. " +
                "Configure DoH or alternative DNS server.");
    }

    /// <summary>
    /// Отправляет raw UDP DNS-запрос к указанному DNS-серверу,
    /// минуя системный resolver. Позволяет определить, блокирует ли
    /// провайдер DNS на уровне resolver или перехватывает UDP/53.
    /// </summary>
    private static async Task<(bool Success, string? Address, string? Error, int Ms)>
        TryResolveDnsDirectAsync(string domain, string dnsServerIp)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            // Собираем минимальный DNS query (type A)
            var query = BuildDnsQuery(domain);

            using var udp = new Socket(
                AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);

            var endpoint = new IPEndPoint(IPAddress.Parse(dnsServerIp), 53);

            using var cts = new CancellationTokenSource(3000);

            await udp.ConnectAsync(endpoint, cts.Token);
            await udp.SendAsync(query, SocketFlags.None, cts.Token);

            var buffer = new byte[512];
            int received = await udp.ReceiveAsync(
                buffer, SocketFlags.None, cts.Token);

            sw.Stop();

            if (received < 12)
                return (false, null, "Truncated response", (int)sw.ElapsedMilliseconds);

            // Парсим ответ: ищем первый A-record (type 1, class 1)
            var address = ParseDnsResponseFirstA(buffer, received);

            return address != null
                ? (true, address, null, (int)sw.ElapsedMilliseconds)
                : (false, null, "No A record in response", (int)sw.ElapsedMilliseconds);
        }
        catch (OperationCanceledException)
        {
            return (false, null, "Timeout", (int)sw.ElapsedMilliseconds);
        }
        catch (SocketException ex)
        {
            return (false, null, $"Socket: {ex.SocketErrorCode}", (int)sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            return (false, null, ex.Message, (int)sw.ElapsedMilliseconds);
        }
    }

    /// <summary>
    /// Собирает минимальный DNS query packet для A-записи.
    /// </summary>
    private static byte[] BuildDnsQuery(string domain)
    {
        // Header: ID=0x1234, flags=0x0100 (standard query, recursion desired)
        // QDCOUNT=1
        var parts = domain.Split('.');

        int nameLen = 0;
        foreach (var part in parts)
            nameLen += 1 + part.Length;
        nameLen += 1; // trailing zero

        var packet = new byte[12 + nameLen + 4];

        // Header
        packet[0] = 0x12; packet[1] = 0x34; // ID
        packet[2] = 0x01; packet[3] = 0x00; // Flags: RD=1
        packet[4] = 0x00; packet[5] = 0x01; // QDCOUNT=1

        // Question: domain name
        int offset = 12;
        foreach (var part in parts)
        {
            packet[offset++] = (byte)part.Length;
            foreach (char c in part)
                packet[offset++] = (byte)c;
        }
        packet[offset++] = 0; // name terminator

        // Type A (1)
        packet[offset++] = 0x00;
        packet[offset++] = 0x01;
        // Class IN (1)
        packet[offset++] = 0x00;
        packet[offset++] = 0x01;

        return packet;
    }

    /// <summary>
    /// Извлекает первый A-record (IPv4) из DNS-ответа.
    /// Обрабатывает CNAME-цепочки (youtube.com → youtube-ui.l.google.com → IP).
    /// </summary>
    private static string? ParseDnsResponseFirstA(byte[] buffer, int length)
    {
        if (length < 12) return null;

        int rcode = buffer[3] & 0x0F;
        if (rcode != 0) return null;

        int qdcount = (buffer[4] << 8) | buffer[5];
        int ancount = (buffer[6] << 8) | buffer[7];
        if (ancount == 0) return null;

        // Пропускаем question section
        int offset = 12;
        for (int q = 0; q < qdcount; q++)
        {
            offset = SkipDnsName(buffer, length, offset);
            if (offset < 0) return null;
            offset += 4; // QTYPE + QCLASS
        }

        // Проходим все answer records, ищем A (type=1)
        for (int i = 0; i < ancount && offset < length; i++)
        {
            offset = SkipDnsName(buffer, length, offset);
            if (offset < 0 || offset + 10 > length) return null;

            int type = (buffer[offset] << 8) | buffer[offset + 1];
            int rdlength = (buffer[offset + 8] << 8) | buffer[offset + 9];
            offset += 10;

            if (type == 1 && rdlength == 4 && offset + 4 <= length) // A record
                return $"{buffer[offset]}.{buffer[offset + 1]}.{buffer[offset + 2]}.{buffer[offset + 3]}";

            // type=5 (CNAME) — пропускаем, ищем A дальше в цепочке
            offset += rdlength;
        }

        return null;
    }

    /// <summary>
    /// Пропускает DNS name (с поддержкой compression pointers).
    /// </summary>
    private static int SkipDnsName(byte[] buffer, int length, int offset)
    {
        while (offset < length)
        {
            byte b = buffer[offset];
            if (b == 0) return offset + 1;                    // конец имени
            if ((b & 0xC0) == 0xC0) return offset + 2;        // pointer (2 байта)
            offset += b + 1;                                   // label
        }
        return -1; // overflow
    }

    // --- Section: Alternative API Endpoint ---

    /// <summary>
    /// Проверяет доступность альтернативного YouTube API endpoint через googleapis.com.
    /// ТСПУ блокирует SNI youtube.com, но не googleapis.com.
    /// </summary>
    [TestMethod(TestCategory.Integration, "CDN: Alternative API Endpoint (googleapis.com)",
        Order = 4, Group = "CdnDiagnostic", RequiresNetwork = true, TimeoutSeconds = 30)]
    public static async Task TestAlternativeApiEndpointAsync()
    {
        Log.Info("═══════════════════════════════════════════════════════════════");
        Log.Info("  ALTERNATIVE API ENDPOINT TEST");
        Log.Info($"  Outbound IP: {GetCurrentOutboundIp()}");
        Log.Info("═══════════════════════════════════════════════════════════════\n");

        var endpoints = new (string Host, string Path, string Label)[]
        {
        ("www.youtube.com", "/youtubei/v1/player", "Standard (blocked by TSPU)"),
        ("music.youtube.com", "/youtubei/v1/player", "Music (blocked by TSPU)"),
        ("youtubei.googleapis.com", "/youtubei/v1/player", "googleapis (may bypass TSPU)"),
        };

        Log.Info($"  {"Host",-35} {"DNS",-6} {"TCP",-6} {"TLS",-6} {"HTTP",-6} {"ms",-8} Label");
        Log.Info($"  {new string('─', 95)}");

        foreach (var (host, path, label) in endpoints)
        {
            bool dnsOk = false, tcpOk = false, tlsOk = false, httpOk = false;
            int totalMs = 0;
            var sw = Stopwatch.StartNew();

            // DNS
            IPAddress? ip = null;
            try
            {
                var addresses = await Dns.GetHostAddressesAsync(
                    host, AddressFamily.InterNetwork);
                if (addresses.Length > 0)
                {
                    dnsOk = true;
                    ip = addresses[0];
                }
            }
            catch (SocketException)
            {
                // Попробуем DoH
                var dohResult = await DohResolver.ResolveAsync(host);
                if (dohResult is { Length: > 0 })
                {
                    dnsOk = true;
                    ip = dohResult[0];
                }
            }

            if (!dnsOk || ip == null)
            {
                sw.Stop();
                Log.Warn($"  {host,-35} ❌     -      -      -      {sw.ElapsedMilliseconds}ms    {label}");
                continue;
            }

            // TCP
            try
            {
                using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                using var cts = new CancellationTokenSource(3000);
                await socket.ConnectAsync(ip, 443, cts.Token);
                tcpOk = true;
            }
            catch { }

            if (!tcpOk)
            {
                sw.Stop();
                Log.Warn($"  {host,-35} ✅     ❌     -      -      {sw.ElapsedMilliseconds}ms    {label}");
                continue;
            }

            // TLS + HTTP POST (InnerTube player request)
            try
            {
                using var handler = new SocketsHttpHandler
                {
                    ConnectTimeout = TimeSpan.FromSeconds(5),
                    UseProxy = false,
                };
                using var client = new HttpClient(handler)
                {
                    Timeout = TimeSpan.FromSeconds(8),
                };

                // Минимальный InnerTube player request (ANDROID client)
                var json = """
                {
                    "context": {
                        "client": {
                            "clientName": "ANDROID",
                            "clientVersion": "19.09.37",
                            "hl": "en",
                            "gl": "US"
                        }
                    },
                    "videoId": "dQw4w9WgXcQ"
                }
                """;

                using var request = new HttpRequestMessage(HttpMethod.Post,
                    $"https://{host}{path}?prettyPrint=false");
                request.Content = new StringContent(json,
                    System.Text.Encoding.UTF8, "application/json");
                request.Headers.TryAddWithoutValidation("User-Agent",
                    "com.google.android.youtube/19.09.37 (Linux; U; Android 12)");

                using var response = await client.SendAsync(
                    request, HttpCompletionOption.ResponseHeadersRead);

                tlsOk = true; // Если дошли сюда — TLS прошёл
                httpOk = (int)response.StatusCode is >= 200 and < 500;
                // 4xx тоже считаем "HTTP работает" — сервер ответил, не ТСПУ дропнул

                sw.Stop();
                totalMs = (int)sw.ElapsedMilliseconds;

                string dnsIcon = dnsOk ? "✅" : "❌";
                string tcpIcon = tcpOk ? "✅" : "❌";
                string tlsIcon = tlsOk ? "✅" : "❌";
                string httpIcon = httpOk ? $"✅{(int)response.StatusCode}" : "❌";

                Log.Info($"  {host,-35} {dnsIcon,-6} {tcpIcon,-6} {tlsIcon,-6} {httpIcon,-6} {totalMs}ms    {label}");
            }
            catch (HttpRequestException ex) when (
                ex.Message.Contains("canceled", StringComparison.OrdinalIgnoreCase) ||
                ex.Message.Contains("timed out", StringComparison.OrdinalIgnoreCase) ||
                ex.InnerException is OperationCanceledException)
            {
                sw.Stop();
                // TLS handshake timeout = ТСПУ silent drop
                Log.Warn(
                    $"  {host,-35} ✅     ✅     ❌ DPI -      {sw.ElapsedMilliseconds}ms    {label}");
            }
            catch (Exception ex)
            {
                sw.Stop();
                var inner = ex.InnerException?.Message ?? ex.Message;
                var truncated = inner.Length > 30 ? inner[..30] : inner;
                Log.Warn(
                    $"  {host,-35} ✅     ✅     ❌     -      {sw.ElapsedMilliseconds}ms    {label} ({truncated})");
            }
        }

        Log.Info($"\n  ═══ INTERPRETATION ═══");
        Log.Info("  www.youtube.com     TLS=❌ → ТСПУ blocks SNI 'youtube.com'");
        Log.Info("  googleapis.com      TLS=✅ → ТСПУ does NOT block this SNI");
        Log.Info("  → Solution: route InnerTube API through youtubei.googleapis.com");
        Log.Info("═══════════════════════════════════════════════════════════════");
    }

    // --- Section: Private Helpers ---

    private static async Task RunPathTestAsync(
     string url,
     string label,
     string referenceUrl,
     (long Start, long End)? range = null)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var sw = Stopwatch.StartNew();

        try
        {
            using var handler = BuildHandler();
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(8) };
            using var request = new HttpRequestMessage(HttpMethod.Get, url);

            if (range.HasValue)
                request.Headers.Range = new RangeHeaderValue(range.Value.Start, range.Value.End);

            SharedHttpClient.ApplyUserAgentFromUrl(request, referenceUrl);

            using var response = await client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cts.Token);

            sw.Stop();
            var code = (int)response.StatusCode;
            var icon = code is 200 or 204 or 206 ? "✅" : "⚠";

            Log.Info($"  {icon} HTTP {code} in {sw.ElapsedMilliseconds}ms — {label}");

            if (code is 200 or 206)
            {
                var bytes = await response.Content.ReadAsByteArrayAsync(cts.Token);
                Log.Info($"    Received: {bytes.Length} bytes");
            }
        }
        catch (OperationCanceledException)
        {
            sw.Stop();
            Log.Warn($"  ❌ TIMEOUT ({sw.ElapsedMilliseconds}ms) — {label}");
            Log.Warn("     → ТСПУ silent drop (no RST, no response)");
        }
        catch (Exception ex)
        {
            sw.Stop();
            Log.Error($"  ❌ ERROR ({sw.ElapsedMilliseconds}ms) — {label}: {FormatException(ex)}");
        }
    }

    private static SocketsHttpHandler BuildHandler() => new()
    {
        ConnectCallback = SharedHttpClient.ConnectWithKeepAliveAsync,
        UseProxy = false,
        PooledConnectionLifetime = TimeSpan.FromSeconds(30),
        ConnectTimeout = TimeSpan.FromSeconds(5),
    };

    private static string GetSubnet(string ip)
    {
        var parts = ip.Split('.');
        return parts.Length >= 2 ? $"{parts[0]}.{parts[1]}.0.0/16" : ip;
    }

    private static string FormatException(Exception ex)
    {
        var inner = ex;
        while (inner.InnerException is not null)
            inner = inner.InnerException;

        return inner switch
        {
            SocketException se =>
                $"socket/{se.SocketErrorCode} ({(int)se.SocketErrorCode}): {se.Message}",
            IOException ioe => $"io: {ioe.Message}",
            _ => $"{inner.GetType().Name}: {inner.Message}",
        };
    }

    private static void CheckFileForSubnets(string path, string[] subnets, string fileName)
    {
        Log.Info($"  [{fileName}]");

        if (!File.Exists(path))
        {
            Log.Warn($"    ❌ File not found: {path}");
            return;
        }

        var content = File.ReadAllText(path);
        foreach (var subnet in subnets)
        {
            var found = content.Contains(subnet, StringComparison.OrdinalIgnoreCase);
            Log.Info($"    {(found ? "✅" : "❌ MISSING")} {subnet}");
        }
    }

    private static void CheckFileForDomains(string path, string[] domains, string fileName)
    {
        Log.Info($"\n  [{fileName}]");

        if (!File.Exists(path))
        {
            Log.Warn($"    ❌ File not found: {path}");
            return;
        }

        var content = File.ReadAllText(path);
        foreach (var domain in domains)
        {
            var found = content.Contains(domain, StringComparison.OrdinalIgnoreCase);
            Log.Info($"    {(found ? "✅" : "❌ MISSING")} {domain}");
        }
    }

    /// <summary>
    /// Классифицирует результат probe для логирования.
    /// Media path — главный критерий. 204 может флапнуть.
    /// </summary>
    private static (string Diagnosis, ProbeClassification Classification)
        ClassifyProbeResult(MediaPathProbe.HostProbeResult result)
    {
        if (result.IsFullyAvailable)
            return ("✅ Available", ProbeClassification.Available);

        if (result.IsMediaAvailable)
            return ("✅ Available (204 transient)", ProbeClassification.Available);

        if (result.IsTspuPattern)
            return ("🔴 ТСПУ DPI block", ProbeClassification.TspuBlocked);

        if (result.IsFullBlock)
            return ("⚫ Full block", ProbeClassification.FullBlocked);

        return ($"⚠ Unknown ({result.MediaError})", ProbeClassification.TspuBlocked);
    }

    private static string GetCurrentOutboundIp()
    {
        try
        {
            using var socket = new Socket(
                AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.Connect("8.8.8.8", 53);
            return ((IPEndPoint)socket.LocalEndPoint!).Address.ToString();
        }
        catch
        {
            return "(unknown)";
        }
    }

    private static void AssertTrue(bool condition, string message)
    {
        if (!condition)
            throw new Exception($"Assertion failed: {message}");
    }

    private enum ProbeClassification
    {
        Available,
        TspuBlocked,
        FullBlocked
    }
}