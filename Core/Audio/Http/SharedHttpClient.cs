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
            // --- LAYER 1: TCP Keepalive через ConnectCallback ---
            // Единственный официально задокументированный способ установить SO_KEEPALIVE
            // на уровне сокета в SocketsHttpHandler (.NET 5+).
            // Паттерн взят из официальной документации Microsoft:
            // https://learn.microsoft.com/en-us/dotnet/api/system.net.http.socketshttphandler.connectcallback
            //
            // Зачем: при работе через TUN VPN (Xray, sing-box, WireGuard) TCP-соединения
            // могут тихо умереть когда туннель переподключается или ротирует маршрут,
            // при этом SocketsHttpHandler не знает об этом и держит зомби-сокеты в пуле.
            // Следующий range-запрос берёт мёртвый сокет → IOException шторм → starvation.
            //
            // С keepalive: ОС детектирует мёртвый туннель за 5 + 3×3 = 14с и закрывает сокет.
            // SocketsHttpHandler при извлечении из пула видит закрытый сокет и создаёт новый
            // автоматически — без IOException шторма и без ручного rebuild.
            ConnectCallback = ConnectWithKeepAliveAsync,

            // --- LAYER 2: Ротация пула ---
            // 90с вместо 15мин: принудительное пересоздание соединений до того
            // как туннель успевает протухнуть. При активном VPN TUN туннели живут
            // в среднем 60–120с при переподключении. 15мин гарантировало накопление зомби.
            PooledConnectionLifetime = TimeSpan.FromSeconds(90),

            // 10с вместо 15с: idle-соединения дропаются быстрее.
            // Keepalive покрывает активные, idle не нужны долго.
            PooledConnectionIdleTimeout = TimeSpan.FromSeconds(10),

            // 6 вместо 16: при 16 параллельных соединениях IOException шторм был
            // максимальным (16 мёртвых сокетов одновременно). 6 достаточно для
            // range-запросов + прогрева CDN.
            MaxConnectionsPerServer = 6,

            AutomaticDecompression = DecompressionMethods.All,
            UseCookies = false,

            ResponseDrainTimeout = TimeSpan.Zero,

            // KeepAlivePingPolicy работает только для HTTP/2.
            // При HTTP/1.1 (наш случай) это no-op — убираем ложное ощущение защиты.
            // Реальный keepalive теперь на уровне TCP через ConnectCallback выше.
            // KeepAlivePingPolicy = HttpKeepAlivePingPolicy.WithActiveRequests, // HTTP/2 only

            // 8с вместо 15с: с TCP keepalive мёртвый туннель детектируется за ~14с ОС,
            // поэтому ConnectTimeout 15с был бесполезным ожиданием поверх.
            ConnectTimeout = TimeSpan.FromSeconds(8),
        };

        ApplyProxy(handler, proxy);

        var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(20),
            DefaultRequestVersion = HttpVersion.Version11,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact,
        };

        client.DefaultRequestHeaders.Add("Accept", "*/*");

        Log.Debug($"[SharedHttpClient] Created: HTTP/{client.DefaultRequestVersion}, " +
                  $"policy={client.DefaultVersionPolicy}, " +
                  $"poolLifetime=90s, maxConn=6, keepAlive=TCP(5s/3s/3)");

        return client;
    }

    /// <summary>
    /// ConnectCallback с TCP keepalive.
    /// Вызывается SocketsHttpHandler при создании каждого нового соединения.
    ///
    /// Параметры агрессивные для TUN VPN:
    ///   TcpKeepAliveTime     = 5с  — первый зонд через 5с idle
    ///   TcpKeepAliveInterval = 3с  — повторные зонды каждые 3с
    ///   TcpKeepAliveRetryCount = 3 — 3 нет ответа → ОС закрывает сокет
    ///
    /// Итог: мёртвый туннель детектируется за 5 + 3×3 = 14с вместо ConnectTimeout.
    ///
    /// DNS resolution выполняется внутри callback через Dns.GetHostAddressesAsync,
    /// что позволяет использовать ctx.DnsEndPoint.AddressFamily для IPv4/IPv6 hint.
    /// </summary>
    private static async ValueTask<Stream> ConnectWithKeepAliveAsync(
        SocketsHttpConnectionContext ctx,
        CancellationToken ct)
    {
        // Резолвим DNS с учётом предпочтительного AddressFamily из контекста.
        // Это важно: при наличии только IPv4 или только IPv6 маршрута
        // ConnectAsync к неправильному семейству упадёт с SocketException.
        var addresses = await Dns.GetHostAddressesAsync(
            ctx.DnsEndPoint.Host,
            ctx.DnsEndPoint.AddressFamily,
            ct).ConfigureAwait(false);

        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp)
        {
            // Отключаем алгоритм Нейгла: для range-запросов нет смысла буферизовать
            // маленькие TCP-сегменты, важна минимальная задержка первого байта.
            NoDelay = true,
        };

        try
        {
            // SO_KEEPALIVE — включаем TCP keepalive на уровне ОС.
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);

            // TCP_KEEPIDLE (Linux) / TCP_KEEPALIVE (macOS) / TcpKeepAliveTime (Windows):
            // время idle в секундах до отправки первого keepalive-зонда.
            socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveTime, 5);

            // TCP_KEEPINTVL: интервал между повторными зондами при отсутствии ответа.
            socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveInterval, 3);

            // TCP_KEEPCNT: число неотвеченных зондов до закрытия соединения.
            // На Windows этот параметр игнорируется (фиксировано 10), на Linux/macOS работает.
            socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveRetryCount, 3);

            await socket.ConnectAsync(addresses, ctx.DnsEndPoint.Port, ct).ConfigureAwait(false);

            // ownsSocket: true — NetworkStream берёт ownership и dispose сокета при своём dispose.
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            // При любой ошибке connection disposal обязателен немедленно,
            // иначе socket leak в FIN_WAIT состоянии.
            socket.Dispose();
            throw;
        }
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