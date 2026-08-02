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
}