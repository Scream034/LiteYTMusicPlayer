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
    private static long _connectionSequence;

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

    /// <summary>
    /// Создаёт новый экземпляр <see cref="HttpClient"/> для CDN-запросов аудио.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Клиент конфигурируется для работы с YouTube CDN (<c>*.googlevideo.com</c>):
    /// <list type="bullet">
    ///   <item>
    ///     <description>
    ///       <see cref="SocketsHttpHandler.ConnectCallback"/> — кастомный обработчик TCP-подключений
    ///       с явным выбором <c>IPv4</c> при <see cref="System.Net.Sockets.AddressFamily.Unspecified"/>.
    ///       Гарантирует прохождение трафика через инструменты обхода DPI (zapret, GoodbyeDPI),
    ///       которые перехватывают только IPv4.
    ///     </description>
    ///   </item>
    ///   <item>
    ///     <description>
    ///       <see cref="SocketsHttpHandler.PooledConnectionLifetime"/> = 90 секунд —
    ///       предотвращает накопление «зомби»-соединений при смене VPN-туннеля.
    ///     </description>
    ///   </item>
    ///   <item>
    ///     <description>
    ///       <see cref="SocketsHttpHandler.PooledConnectionIdleTimeout"/> = 45 секунд —
    ///       даёт запас между прогревом CDN (<see cref="CdnConnectionPreWarmer"/>) и первым
    ///       Range GET из <c>CachingStreamSource</c>.
    ///     </description>
    ///   </item>
    ///   <item>
    ///     <description>
    ///       <see cref="SocketsHttpHandler.UseProxy"/> = <see langword="false"/> —
    ///       явно отключает системный proxy Windows, исключая захват трафика
    ///       отладочными инструментами (Fiddler, Charles) в нейтральных запусках.
    ///     </description>
    ///   </item>
    /// </list>
    /// </para>
    /// <para>
    /// TCP keepalive намеренно <b>не используется</b>: при ТСПУ silent-drop keepalive-зонды
    /// уходят в никуда, и ОС закрывает сокет через <c>KeepaliveTime + Interval × RetryCount</c>
    /// секунд с <c>WSAECONNRESET</c>, маскируя истинную причину отказа.
    /// Вместо этого таймаут регулируется через <see cref="HttpClient.Timeout"/>.
    /// </para>
    /// </remarks>
    /// <param name="proxy">
    /// Пользовательские настройки HTTP-прокси. Передайте <see langword="null"/> для прямого подключения.
    /// </param>
    /// <returns>Новый настроенный экземпляр <see cref="HttpClient"/>.</returns>
    private static HttpClient CreateClient(ProxySettings? proxy)
    {
        var handler = new SocketsHttpHandler
        {
            ConnectCallback = ConnectWithKeepAliveAsync,
            PooledConnectionLifetime = TimeSpan.FromSeconds(90),
            PooledConnectionIdleTimeout = TimeSpan.FromSeconds(45),
            MaxConnectionsPerServer = 6,
            AutomaticDecompression = DecompressionMethods.All,
            UseCookies = false,
            ResponseDrainTimeout = TimeSpan.FromSeconds(1),
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
                  $"poolLifetime=90s, maxConn=6, idleTimeout=45s, proxy={(proxy?.Enabled == true ? $"{proxy.Host}:{proxy.Port}" : "none")}");

        return client;
    }

    /// <summary>
    /// Callback установки TCP-соединения для <see cref="SocketsHttpHandler"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Реализует два поведения, недоступных через стандартные свойства <see cref="SocketsHttpHandler"/>:
    /// </para>
    /// <para>
    /// <b>1. Принудительный IPv4 при неопределённом AddressFamily.</b><br/>
    /// При <see cref="System.Net.Sockets.AddressFamily.Unspecified"/> DNS может вернуть AAAA-записи,
    /// и <see cref="System.Net.Sockets.Socket"/> по умолчанию создаётся как dual-stack IPv6.
    /// Трафик по IPv6 обходит WinDivert-based инструменты обхода DPI (zapret, GoodbyeDPI),
    /// которые перехватывают только IPv4-пакеты.
    /// Метод фильтрует адреса до IPv4 (<see cref="System.Net.Sockets.AddressFamily.InterNetwork"/>),
    /// сохраняя fallback на полный список при отсутствии A-записей.
    /// </para>
    /// <para>
    /// <b>2. Явный <see cref="System.Net.Sockets.AddressFamily"/> в конструкторе Socket.</b><br/>
    /// Создание <c>new Socket(SocketType.Stream, ProtocolType.Tcp)</c> без <c>AddressFamily</c>
    /// может завершиться <see cref="System.Net.Sockets.SocketException"/>
    /// (<c>WSAEAFNOSUPPORT</c>) при несоответствии семейства адреса и сокета.
    /// Конструктор с явным <c>addresses[0].AddressFamily</c> исключает эту ситуацию.
    /// </para>
    /// <para>
    /// TCP keepalive намеренно <b>отсутствует</b>. При ТСПУ silent-drop keepalive-зонды
    /// уходят в никуда; ОС считает N потерянных зондов отказом сети и закрывает сокет
    /// с <c>ConnectionReset (10054)</c>, скрывая истинную причину. Таймаут регулируется
    /// через <see cref="HttpClient.Timeout"/> на уровне HTTP-запроса.
    /// </para>
    /// </remarks>
    /// <param name="ctx">
    /// Контекст подключения: DNS-endpoint, AddressFamily-подсказка и начальный HTTP-запрос,
    /// инициировавший открытие нового соединения.
    /// </param>
    /// <param name="ct">Токен отмены.</param>
    /// <returns>
    /// Открытый <see cref="NetworkStream"/> поверх установленного TCP-сокета.
    /// </returns>
    /// <exception cref="System.Net.Sockets.SocketException">
    /// Выбрасывается, если DNS не вернул ни одного адреса или TCP Connect завершился ошибкой.
    /// </exception>
    internal static async ValueTask<Stream> ConnectWithKeepAliveAsync(
        SocketsHttpConnectionContext ctx,
        CancellationToken ct)
    {
        var allAddresses = await Dns.GetHostAddressesAsync(
            ctx.DnsEndPoint.Host,
            ctx.DnsEndPoint.AddressFamily,
            ct).ConfigureAwait(false);

        // При AddressFamily.Unspecified DNS возвращает A и AAAA.
        // Оставляем только IPv4: WinDivert-based bypass работает только на IPv4.
        IPAddress[] addresses;
        if (ctx.DnsEndPoint.AddressFamily == System.Net.Sockets.AddressFamily.Unspecified)
        {
            var ipv4Only = Array.FindAll(
                allAddresses,
                static a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);

            addresses = ipv4Only.Length > 0 ? ipv4Only : allAddresses;
        }
        else
        {
            addresses = allAddresses;
        }

        if (addresses.Length == 0)
            throw new System.Net.Sockets.SocketException(
                (int)System.Net.Sockets.SocketError.HostNotFound);

        long connectionId = Interlocked.Increment(ref _connectionSequence);
        var initialUri = ctx.InitialRequestMessage.RequestUri;
        var initialRange = ctx.InitialRequestMessage.Headers.Range?.ToString() ?? "-";

        Log.Debug(
            $"[SharedHttpClient] Connect#{connectionId} opening: " +
            $"endpoint={ctx.DnsEndPoint.Host}:{ctx.DnsEndPoint.Port}, " +
            $"initial={initialUri?.Host ?? "(none)"}{initialUri?.AbsolutePath ?? string.Empty}, " +
            $"range={initialRange}, " +
            $"resolved={addresses.Length}, first={addresses[0]}");

        // Явный AddressFamily из первого адреса: гарантирует отсутствие WSAEAFNOSUPPORT
        // при ConnectAsync с mismatched address family.
        var socket = new Socket(
            addresses[0].AddressFamily,
            SocketType.Stream,
            ProtocolType.Tcp)
        {
            // Отключаем алгоритм Нейгла: для Range-запросов нет смысла буферизовать
            // маленькие сегменты — важна минимальная задержка первого байта.
            NoDelay = true,
        };

        try
        {
            await socket.ConnectAsync(addresses, ctx.DnsEndPoint.Port, ct).ConfigureAwait(false);

            Log.Debug(
                $"[SharedHttpClient] Connect#{connectionId} connected: " +
                $"local={socket.LocalEndPoint}, remote={socket.RemoteEndPoint}");

            // ownsSocket: true — NetworkStream берёт ownership и dispose сокета при своём dispose.
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            // При любой ошибке соединения немедленный dispose сокета обязателен,
            // иначе утечка дескриптора в состоянии FIN_WAIT.
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
            return;
        }

        handler.Proxy = null;
        handler.UseProxy = false;
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