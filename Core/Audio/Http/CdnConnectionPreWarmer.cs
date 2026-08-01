using System.Collections.Concurrent;
using System.Diagnostics;

namespace LMP.Core.Audio.Http;

/// <summary>
/// Спекулятивный прогрев TCP+TLS-соединений к YouTube CDN нодам.
/// </summary>
internal static class CdnConnectionPreWarmer
{
    private const string GoogleVideoCdnSuffix = ".googlevideo.com";
    private const string GenerateEndpoint = "/generate_204";
    private const int MaxTrackedHosts = 4;

    private static readonly TimeSpan WarmCooldown = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan WarmTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan SpeculativeThrottle = TimeSpan.FromSeconds(30);

    private static DateTime _lastSpeculativeWarmTime = DateTime.MinValue;

    private static readonly Lock _lock = new();
    private static readonly LinkedList<(string Host, DateTime WarmTime)> _recentHosts = new();

    /// <summary>
    /// Регистрирует CDN-хост после успешного HTTP-ответа.
    /// </summary>
    public static void RecordHost(string url)
    {
        if (!TryExtractHost(url, out var host))
            return;

        CdnHostStatsStore.RecordHit(host);

        lock (_lock)
        {
            var node = _recentHosts.First;
            while (node != null)
            {
                var next = node.Next;
                if (string.Equals(node.Value.Host, host, StringComparison.OrdinalIgnoreCase))
                    _recentHosts.Remove(node);
                node = next;
            }

            _recentHosts.AddFirst((host, DateTime.UtcNow));

            while (_recentHosts.Count > MaxTrackedHosts)
                _recentHosts.RemoveLast();
        }
    }

    /// <summary>
    /// Спекулятивный прогрев соединений к последним известным CDN-хостам.
    /// </summary>
    public static void PreWarmRecentHosts(HttpClient httpClient, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        string[] hostsToWarm;

        lock (_lock)
        {
            if (now - _lastSpeculativeWarmTime < SpeculativeThrottle)
                return;

            if (_recentHosts.Count == 0)
                return;

            _lastSpeculativeWarmTime = now;

            var buffer = new string[_recentHosts.Count];
            int count = 0;

            foreach (var (host, warmTime) in _recentHosts)
            {
                if (now - warmTime < WarmCooldown)
                    continue;

                buffer[count++] = host;
            }

            if (count == 0)
                return;

            hostsToWarm = buffer[..count];
        }

        for (int i = 0; i < hostsToWarm.Length; i++)
            _ = WarmHostCoreAsync(httpClient, hostsToWarm[i], ct);

        Log.Debug($"[CdnPreWarmer] Speculative warm-up fired for {hostsToWarm.Length} recent CDN host(s)");
    }

    /// <summary>
    /// Точечный прогрев соединения к конкретному CDN-хосту из URL.
    /// </summary>
    public static void PreWarmHost(HttpClient httpClient, string url, CancellationToken ct)
    {
        if (!TryExtractHost(url, out var host))
            return;

        lock (_lock)
        {
            foreach (var (trackedHost, warmTime) in _recentHosts)
            {
                if (string.Equals(trackedHost, host, StringComparison.OrdinalIgnoreCase)
                    && DateTime.UtcNow - warmTime < WarmCooldown)
                {
                    Log.Debug($"[CdnPreWarmer] Host {TruncateHost(host)}... still warm, skipping");
                    return;
                }
            }
        }

        _ = WarmHostCoreAsync(httpClient, host, ct);
        Log.Debug($"[CdnPreWarmer] Targeted warm-up fired for {TruncateHost(host)}...");
    }

    /// <summary>
    /// Выполняет TCP+TLS прогрев CDN-ноды через <c>GET /generate_204</c>.
    /// </summary>
    private static async Task WarmHostCoreAsync(HttpClient httpClient, string host, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(WarmTimeout);

            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                $"https://{host}{GenerateEndpoint}");

            request.Headers.ConnectionClose = false;
            SharedHttpClient.ApplyUserAgentFromUrl(request, $"https://{host}/");

            using var response = await httpClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token)
                .ConfigureAwait(false);

            sw.Stop();

            CdnHostStatsStore.RecordTtfb(host, sw.ElapsedMilliseconds);
            CdnHostStatsStore.FlushIfNeeded();

            Log.Debug($"[CdnPreWarmer] {TruncateHost(host)}... warm in {sw.ElapsedMilliseconds}ms (HTTP {(int)response.StatusCode})");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (OperationCanceledException)
        {
            sw.Stop();
            Log.Debug($"[CdnPreWarmer] {TruncateHost(host)}... timed out ({sw.ElapsedMilliseconds}ms)");
        }
        catch (Exception ex)
        {
            sw.Stop();
            Log.Debug($"[CdnPreWarmer] {TruncateHost(host)}... failed ({sw.ElapsedMilliseconds}ms): {ex.Message}");
        }
    }

    /// <summary>
    /// Прогревает один конкретный хост. Используется из <see cref="CdnHostStatsStore.PreWarmTopClustersAsync"/>.
    /// </summary>
    internal static Task WarmSingleHostAsync(HttpClient httpClient, string host, CancellationToken ct)
        => WarmHostCoreAsync(httpClient, host, ct);

    /// <summary>
    /// Извлекает hostname из URL, если это YouTube CDN.
    /// </summary>
    private static bool TryExtractHost(string url, out string host)
    {
        host = string.Empty;

        if (string.IsNullOrEmpty(url))
            return false;

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return false;

        if (!uri.Host.EndsWith(GoogleVideoCdnSuffix, StringComparison.OrdinalIgnoreCase))
            return false;

        host = uri.Host;
        return true;
    }

    /// <summary>
    /// Возвращает усечённый hostname для вывода в лог.
    /// </summary>
    /// <param name="host">CDN hostname.</param>
    /// <param name="maxLength">Максимальная длина вывода (по умолчанию 40).</param>
    /// <returns>Hostname, усечённый до <paramref name="maxLength"/> символов.</returns>
    private static string TruncateHost(string host, int maxLength = 40) =>
        host.Length <= maxLength ? host : host[..maxLength];
}