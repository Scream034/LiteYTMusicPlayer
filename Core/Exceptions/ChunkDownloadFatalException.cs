namespace LMP.Core.Exceptions;

/// <summary>
/// Фатальная ошибка загрузки чанков — стрим безвозвратно недоступен.
/// </summary>
public class ChunkDownloadFatalException : AudioSourceException
{
    /// <summary>
    /// Индекс чанка, на котором произошла ошибка.
    /// </summary>
    public int ChunkIndex { get; }

    /// <summary>
    /// Количество последовательных неудачных попыток.
    /// </summary>
    public int ConsecutiveFailures { get; }

    /// <summary>
    /// Причина ошибки.
    /// </summary>
    public ChunkDownloadFailureReason Reason { get; }

    /// <summary>
    /// ID трека (для диагностики).
    /// </summary>
    public string? TrackId { get; }

    /// <summary>
    /// HTTP статус код последнего неудачного запроса (если применимо).
    /// </summary>
    public int? HttpStatusCode { get; }

    public ChunkDownloadFatalException(
        string message,
        int chunkIndex,
        int consecutiveFailures,
        ChunkDownloadFailureReason reason,
        string? trackId = null,
        int? httpStatusCode = null)
        : base(message)
    {
        ChunkIndex = chunkIndex;
        ConsecutiveFailures = consecutiveFailures;
        Reason = reason;
        TrackId = trackId;
        HttpStatusCode = httpStatusCode;
    }

    public ChunkDownloadFatalException(
        string message,
        int chunkIndex,
        int consecutiveFailures,
        ChunkDownloadFailureReason reason,
        string? trackId,
        int? httpStatusCode,
        Exception innerException)
        : base(message, innerException)
    {
        ChunkIndex = chunkIndex;
        ConsecutiveFailures = consecutiveFailures;
        Reason = reason;
        TrackId = trackId;
        HttpStatusCode = httpStatusCode;
    }

    /// <summary>
    /// Возвращает ключ локализации для пользовательского сообщения.
    /// </summary>
    public string GetLocalizationKey() => Reason switch
    {
        ChunkDownloadFailureReason.Forbidden403 => "Error_Stream_Forbidden",
        ChunkDownloadFailureReason.UmpFormat => "Error_Stream_UmpFormat",
        ChunkDownloadFailureReason.MaxRetriesExceeded => "Error_Stream_MaxRetries",
        ChunkDownloadFailureReason.NetworkError => "Error_Stream_Network",
        _ => "Error_Stream_Unknown"
    };
}

/// <summary>
/// Причина фатальной ошибки загрузки чанков.
/// </summary>
public enum ChunkDownloadFailureReason
{
    /// <summary>Превышен лимит HTTP 403 Forbidden ответов.</summary>
    Forbidden403,

    /// <summary>YouTube вернул UMP (encrypted) формат вместо raw audio.</summary>
    UmpFormat,

    /// <summary>Превышен лимит retry-попыток.</summary>
    MaxRetriesExceeded,

    /// <summary>Сетевая ошибка (timeout, connection reset).</summary>
    NetworkError,

    /// <summary>Неизвестная ошибка.</summary>
    Unknown
}