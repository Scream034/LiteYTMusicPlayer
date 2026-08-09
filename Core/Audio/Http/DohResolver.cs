using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace LMP.Core.Audio.Http;

/// <summary>
/// DNS-over-HTTPS fallback resolver для YouTube-доменов,
/// заблокированных провайдером или Zapret на уровне UDP DNS.
/// </summary>
/// <remarks>
/// <para>
/// Используется из <see cref="SharedHttpClient.ConnectWithKeepAliveAsync"/>
/// при <see cref="SocketError.HostNotFound"/> для известных YouTube-доменов.
/// </para>
/// <para>
/// DoH идёт по HTTPS/443 — провайдер не может заблокировать его без блокировки
/// всего Google, а Zapret не перехватывает HTTPS-трафик.
/// </para>
/// <para>
/// Кэширование: положительные записи — 5 минут, отрицательные — 30 секунд.
/// Thread-safe через <see cref="ConcurrentDictionary{TKey,TValue}"/>.
/// </para>
/// </remarks>
internal static class DohResolver
{
    /// <summary>Домены, для которых применяется DoH fallback.</summary>
    private static readonly string[] FallbackDomains =
    [
        "youtube.com",
        "music.youtube.com",
        "www.youtube.com",
        "googleapis.com",
        "jnn-pa.googleapis.com",
    ];

    private const long PositiveTtlTicks = 5 * 60 * TimeSpan.TicksPerSecond;  // 5 минут
    private const long NegativeTtlTicks = 30 * TimeSpan.TicksPerSecond;       // 30 секунд
    private const int DohTimeoutMs = 4000;

    /// <summary>
    /// Fallback DoH endpoints при недоступности Google DoH.
    /// </summary>
    private static readonly string[] FallbackEndpoints =
    [
        "https://dns.google/resolve",
        "https://cloudflare-dns.com/dns-query",
    ];

    private static readonly ConcurrentDictionary<string, CacheEntry> _cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Выделенный HttpClient для DoH-запросов.
    /// Не использует <see cref="SharedHttpClient"/> — иначе рекурсия в ConnectCallback.
    /// Не использует custom ConnectCallback — стандартный DNS-резолв для dns.google работает.
    /// </summary>
    private static readonly HttpClient _dohClient = CreateDohClient();

    private readonly record struct CacheEntry(IPAddress[] Addresses, long ExpiresAtTicks);

    /// <summary>
    /// Проверяет, является ли домен кандидатом для DoH fallback.
    /// </summary>
    internal static bool IsFallbackDomain(string host)
    {
        for (int i = 0; i < FallbackDomains.Length; i++)
        {
            if (host.EndsWith(FallbackDomains[i], StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Резолвит домен через DNS-over-HTTPS с кэшированием.
    /// Возвращает <c>null</c> при неудаче.
    /// </summary>
    internal static async ValueTask<IPAddress[]?> ResolveAsync(
        string host,
        CancellationToken ct = default)
    {
        // Проверяем кэш
        if (_cache.TryGetValue(host, out var cached))
        {
            if (DateTime.UtcNow.Ticks < cached.ExpiresAtTicks)
            {
                return cached.Addresses.Length > 0 ? cached.Addresses : null;
            }

            _cache.TryRemove(host, out _);
        }

        // DoH запрос
        for (int i = 0; i < FallbackEndpoints.Length; i++)
        {
            var addresses = await TryDohQueryAsync(
                FallbackEndpoints[i], host, ct).ConfigureAwait(false);

            if (addresses is { Length: > 0 })
            {
                _cache[host] = new CacheEntry(addresses,
                    DateTime.UtcNow.Ticks + PositiveTtlTicks);

                Log.Info($"[DoH] Resolved {host} → {addresses[0]} " +
                         $"({addresses.Length} address(es)) via {GetEndpointName(FallbackEndpoints[i])}");
                return addresses;
            }
        }

        // Все DoH endpoints не помогли — кэшируем негативный результат
        _cache[host] = new CacheEntry([], DateTime.UtcNow.Ticks + NegativeTtlTicks);
        Log.Warn($"[DoH] Failed to resolve {host} via all DoH endpoints");
        return null;
    }

    /// <summary>
    /// Инвалидирует кэш. Вызывается при смене сети.
    /// </summary>
    internal static void InvalidateCache()
    {
        int count = _cache.Count;
        _cache.Clear();
        if (count > 0)
            Log.Debug($"[DoH] Cache cleared ({count} entries)");
    }

    private static async Task<IPAddress[]?> TryDohQueryAsync(
        string endpoint,
        string host,
        CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(DohTimeoutMs);

            // Google DNS: JSON API — проще парсить, следует CNAME автоматически
            // Cloudflare: тот же формат при Accept: application/dns-json
            string url = endpoint.Contains("cloudflare")
                ? $"{endpoint}?name={Uri.EscapeDataString(host)}&type=A"
                : $"{endpoint}?name={Uri.EscapeDataString(host)}&type=A";

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Accept.Add(
                new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/dns-json"));

            using var response = await _dohClient
                .SendAsync(request, HttpCompletionOption.ResponseContentRead, cts.Token)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
                return null;

            using var stream = await response.Content.ReadAsStreamAsync(cts.Token)
                .ConfigureAwait(false);

            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cts.Token)
                .ConfigureAwait(false);

            var root = doc.RootElement;

            // Проверяем Status (0 = NOERROR)
            if (root.TryGetProperty("Status", out var status) && status.GetInt32() != 0)
                return null;

            if (!root.TryGetProperty("Answer", out var answers))
                return null;

            var result = new List<IPAddress>(4);

            foreach (var answer in answers.EnumerateArray())
            {
                // type=1 → A record (IPv4)
                if (!answer.TryGetProperty("type", out var type) || type.GetInt32() != 1)
                    continue;

                if (!answer.TryGetProperty("data", out var data))
                    continue;

                if (IPAddress.TryParse(data.GetString(), out var addr))
                    result.Add(addr);
            }

            return result.Count > 0 ? result.ToArray() : null;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            Log.Debug($"[DoH] Timeout querying {GetEndpointName(endpoint)} for {host}");
            return null;
        }
        catch (Exception ex)
        {
            Log.Debug($"[DoH] Error querying {GetEndpointName(endpoint)} for {host}: {ex.Message}");
            return null;
        }
    }

    private static HttpClient CreateDohClient()
    {
        // Нельзя использовать SharedHttpClient.ConnectWithKeepAliveAsync — рекурсия.
        // Стандартный SocketsHttpHandler с системным DNS.
        // dns.google (8.8.8.8) и cloudflare-dns.com (1.1.1.1) резолвятся
        // через DNS-записи, а не YouTube — провайдер их не блокирует.
        var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
            ConnectTimeout = TimeSpan.FromSeconds(4),
        };

        return new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(5),
        };
    }

    private static string GetEndpointName(string endpoint) => endpoint switch
    {
        _ when endpoint.Contains("google") => "Google",
        _ when endpoint.Contains("cloudflare") => "Cloudflare",
        _ => endpoint
    };
}