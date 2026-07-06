using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Security.Authentication;

namespace LMP.Core.Youtube.Exceptions;

/// <summary>
/// Тип сетевой ошибки при взаимодействии с YouTube API.
/// </summary>
public enum NetworkErrorType
{
    /// <summary>Неклассифицированная сетевая ошибка.</summary>
    Unknown = 0,

    /// <summary>Истечение таймаута HTTP-подключения или ответа.</summary>
    Timeout,

    /// <summary>Ошибка SSL/TLS handshake (часто вызвана DPI-блокировкой).</summary>
    SslHandshakeFailure,

    /// <summary>DNS-резолюция не удалась или соединение отклонено.</summary>
    ConnectionFailed
}

/// <summary>
/// Выбрасывается при сетевых ошибках взаимодействия с серверами YouTube.
/// <para>
/// Отделяет сетевые сбои (таймауты, SSL/TLS, DNS) от контентных ошибок
/// (видео недоступно, возрастные ограничения) для корректного отображения
/// пользователю рекомендаций: "проверьте VPN/интернет" вместо "видео недоступно".
/// </para>
/// </summary>
public sealed class YoutubeNetworkException : YoutubeExplodeException
{
    /// <summary>
    /// Тип сетевой ошибки.
    /// </summary>
    public NetworkErrorType ErrorType { get; }

    /// <summary>
    /// <c>true</c> если ошибка вызвана таймаутом HTTP-запроса.
    /// </summary>
    public bool IsTimeout => ErrorType == NetworkErrorType.Timeout;

    /// <summary>
    /// <c>true</c> если ошибка вызвана провалом SSL/TLS handshake.
    /// Типичная причина — DPI-блокировка или нерабочий прокси.
    /// </summary>
    public bool IsSslFailure => ErrorType == NetworkErrorType.SslHandshakeFailure;

    /// <summary>
    /// <c>true</c> если не удалось установить TCP-соединение (DNS, connection refused, socket error).
    /// </summary>
    public bool IsConnectionFailed => ErrorType == NetworkErrorType.ConnectionFailed;

    /// <summary>
    /// Ключ локализации для данного типа сетевой ошибки.
    /// </summary>
    public string GetLocalizationKey() => ErrorType switch
    {
        NetworkErrorType.Timeout => "Error_Network_Timeout",
        NetworkErrorType.SslHandshakeFailure => "Error_SslHandshake_Failed",
        NetworkErrorType.ConnectionFailed => "Error_Network_ConnectionFailed",
        _ => "Error_Network_Generic"
    };

    /// <summary>
    /// Ключ рекомендации для пользователя.
    /// </summary>
    public string GetRecommendationKey() => ErrorType switch
    {
        NetworkErrorType.SslHandshakeFailure => "Recommendation_DpiBlocked",
        _ => "Recommendation_CheckNetwork"
    };

    /// <summary>
    /// Создаёт экземпляр сетевой ошибки YouTube.
    /// </summary>
    /// <param name="message">Человекочитаемое описание ошибки.</param>
    /// <param name="errorType">Классификация сетевой ошибки.</param>
    /// <param name="innerException">Исходное исключение, вызвавшее сбой.</param>
    public YoutubeNetworkException(string message, NetworkErrorType errorType, Exception? innerException = null)
        : base(message, innerException)
    {
        ErrorType = errorType;
    }

    /// <summary>
    /// Пытается классифицировать произвольное исключение как сетевую ошибку YouTube.
    /// </summary>
    /// <param name="exception">Исходное исключение.</param>
    /// <param name="cancellationToken">
    /// Токен отмены вызывающего кода. Используется для различения user-cancel от HTTP timeout.
    /// </param>
    /// <returns>
    /// <see cref="YoutubeNetworkException"/> с корректным <see cref="ErrorType"/>,
    /// или <c>null</c> если исключение не является сетевой ошибкой.
    /// </returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static YoutubeNetworkException? TryClassify(Exception exception, CancellationToken cancellationToken)
    {
        // 1. TaskCanceledException от HttpClient.Timeout (не user-cancel)
        if (exception is TaskCanceledException tce && !cancellationToken.IsCancellationRequested)
        {
            return new YoutubeNetworkException(
                $"Connection timed out: {tce.Message}",
                NetworkErrorType.Timeout,
                tce);
        }

        // 2. OperationCanceledException от внутреннего таймаута (не user-cancel)
        if (exception is OperationCanceledException oce
            && !cancellationToken.IsCancellationRequested
            && !oce.CancellationToken.IsCancellationRequested)
        {
            return new YoutubeNetworkException(
                $"Operation timed out: {oce.Message}",
                NetworkErrorType.Timeout,
                oce);
        }

        // 3. HttpRequestException — анализ inner chain
        if (exception is HttpRequestException httpEx)
        {
            if (ContainsInChain<AuthenticationException>(httpEx))
            {
                return new YoutubeNetworkException(
                    $"SSL/TLS handshake failed: {httpEx.Message}",
                    NetworkErrorType.SslHandshakeFailure,
                    httpEx);
            }

            if (ContainsInChain<SocketException>(httpEx))
            {
                return new YoutubeNetworkException(
                    $"Connection failed: {httpEx.Message}",
                    NetworkErrorType.ConnectionFailed,
                    httpEx);
            }

            // Generic HttpRequestException без специфичного inner — connection failed
            return new YoutubeNetworkException(
                $"Network error: {httpEx.Message}",
                NetworkErrorType.ConnectionFailed,
                httpEx);
        }

        // 4. IOException (broken pipe, connection reset)
        if (exception is IOException ioEx)
        {
            if (ContainsInChain<AuthenticationException>(ioEx))
            {
                return new YoutubeNetworkException(
                    $"SSL/TLS error: {ioEx.Message}",
                    NetworkErrorType.SslHandshakeFailure,
                    ioEx);
            }

            return new YoutubeNetworkException(
                $"I/O error: {ioEx.Message}",
                NetworkErrorType.ConnectionFailed,
                ioEx);
        }

        return null;
    }

    /// <summary>
    /// Проверяет, содержит ли цепочка <see cref="Exception.InnerException"/> исключение указанного типа.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool ContainsInChain<T>(Exception root) where T : Exception
    {
        var current = root.InnerException;
        int depth = 0;
        while (current is not null && depth < 10)
        {
            if (current is T)
                return true;
            current = current.InnerException;
            depth++;
        }
        return false;
    }
}