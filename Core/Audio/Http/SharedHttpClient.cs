using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using LMP.Core.Youtube.Utils;

namespace LMP.Core.Audio.Http;

/// <summary>
/// Глобальный HTTP-клиент для CDN-запросов аудио.
/// Поддерживает горячую замену при смене сетевого интерфейса или настроек прокси.
/// </summary>
public static class SharedHttpClient
{
    private static volatile HttpClient _instance = CreateClient(null);
    private static readonly Lock _rebuildLock = new();

    /// <summary>Текущий активный экземпляр клиента.</summary>
    public static HttpClient Instance => _instance;

    /// <summary>
    /// Пересоздаёт HTTP-клиент с новым пулом соединений.
    /// Старый клиент утилизируется через 30 секунд — чтобы не обрывать активные range-запросы.
    /// </summary>
    /// <param name="proxy">Настройки прокси. <c>null</c> или <c>Enabled = false</c> — прокси не используется.</param>
    public static void Rebuild(ProxySettings? proxy = null)
    {
        HttpClient newClient;
        HttpClient? oldClient;

        lock (_rebuildLock)
        {
            newClient = CreateClient(proxy);
            oldClient = Interlocked.Exchange(ref _instance, newClient);
        }

        if (oldClient is not null)
        {
            _ = Task.Delay(TimeSpan.FromSeconds(30))
                    .ContinueWith(_ => oldClient.Dispose(), TaskScheduler.Default);
        }

        Log.Debug("[SharedHttpClient] Rebuilt. " +
                  $"Proxy: {(proxy?.Enabled == true ? $"{proxy.Host}:{proxy.Port}" : "none")}");
    }

    private static HttpClient CreateClient(ProxySettings? proxy)
    {
        var handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(15),

            PooledConnectionIdleTimeout = TimeSpan.FromSeconds(15),

            MaxConnectionsPerServer = 16,
            AutomaticDecompression = DecompressionMethods.All,
            UseCookies = false,

            // В связке с HTTP/1.1 гарантирует аппаратный разрыв при скраббинге
            ResponseDrainTimeout = TimeSpan.Zero,
            KeepAlivePingPolicy = HttpKeepAlivePingPolicy.WithActiveRequests,
            ConnectTimeout = TimeSpan.FromSeconds(15)
        };

        ApplyProxy(handler, proxy);

        var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(20),
            // HTTP/1.1 защищает от пенальти YouTube CDN на мультиплексированных соединениях
            DefaultRequestVersion = HttpVersion.Version11,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact,
        };

        client.DefaultRequestHeaders.Add("Accept", "*/*");

        Log.Debug($"[SharedHttpClient] Created: HTTP/{client.DefaultRequestVersion}, " +
                  $"policy={client.DefaultVersionPolicy}");

        return client;
    }

    /// <summary>
    /// Применяет настройки прокси к обработчику.
    /// Игнорирует системный прокси Windows, чтобы избежать падений из-за залипших 
    /// программ отладки трафика (например, закрытого Fiddler на порту 8888).
    /// </summary>
    private static void ApplyProxy(SocketsHttpHandler handler, ProxySettings? proxy)
    {
        if (proxy?.Enabled == true && !string.IsNullOrWhiteSpace(proxy.Host))
        {
            var webProxy = new WebProxy($"http://{proxy.Host}:{proxy.Port}");

            if (proxy.UseAuth && !string.IsNullOrWhiteSpace(proxy.Username))
                webProxy.Credentials = new NetworkCredential(proxy.Username, proxy.Password);

            handler.Proxy = webProxy;
            handler.UseProxy = true;
        }
    }

    /// <summary>
    /// Создаёт Range-запрос с User-Agent, соответствующим клиенту из параметра <c>c=</c> в URL.
    /// </summary>
    public static HttpRequestMessage CreateRangeRequest(string url, long start, long end)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Range = new RangeHeaderValue(start, end);
        request.Version = HttpVersion.Version11;
        ApplyUserAgentFromUrl(request, url);
        return request;
    }

    /// <summary>
    /// Устанавливает заголовок User-Agent по параметру <c>c=</c> в URL.
    /// Если параметр отсутствует — используется WebRemix UA как безопасный дефолт.
    /// </summary>
    public static void ApplyUserAgentFromUrl(HttpRequestMessage request, string url)
    {
        var clientParam = UrlEx.TryGetQueryParameterValue(url, "c");
        var ua = clientParam is not null
            ? YoutubeClientUtils.GetUserAgentForClient(clientParam)
            : YoutubeClientUtils.UaWebRemix;

        request.Headers.TryAddWithoutValidation("User-Agent", ua);
    }

    /// <summary>Возвращает длину контента по URL через HEAD-подобный GET-запрос.</summary>
    public static async Task<long> GetContentLengthAsync(string url, CancellationToken ct = default)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Version = HttpVersion.Version11;
            ApplyUserAgentFromUrl(request, url);

            using var response = await Instance
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);

            return response.Content.Headers.ContentLength ?? -1;
        }
        catch { return -1; }
    }

    /// <summary>Возвращает MIME-тип контента по URL.</summary>
    public static async Task<string?> GetContentTypeAsync(string url, CancellationToken ct = default)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Version = HttpVersion.Version11;
            ApplyUserAgentFromUrl(request, url);

            using var response = await Instance
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);

            return response.Content.Headers.ContentType?.MediaType;
        }
        catch { return null; }
    }
}