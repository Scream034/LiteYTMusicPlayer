namespace LMP.Core.Exceptions;

/// <summary>
/// Бросается когда файл кэша удалён, усечён или содержит повреждённые чанки.
/// </summary>
/// <remarks>
/// <para><b>Recoverable vs Fatal:</b></para>
/// <para>Если <see cref="IsRecoverable"/> = true, <c>AudioEngine</c> выполняет
/// автоматический retry (не более <c>MaxAutoRetries</c>) с сохранением позиции.
/// При <c>IsRecoverable</c> = false или исчерпании retry — ошибка поднимается в UI.</para>
/// </remarks>
public sealed class CacheInvalidatedException : Exception
{
    /// <summary>ID трека, для которого произошла ошибка.</summary>
    public string? TrackId { get; init; }

    /// <summary>
    /// Индекс повреждённого чанка. Null если ошибка затрагивает весь файл
    /// (например <see cref="CacheInvalidationKind.FileDeleted"/>).
    /// </summary>
    public int? ChunkIndex { get; init; }

    /// <summary>Можно ли восстановиться без участия пользователя.</summary>
    public bool IsRecoverable { get; init; }

    /// <summary>Классификация причины инвалидации.</summary>
    public CacheInvalidationKind Kind { get; init; }

    /// <summary>
    /// Количество уже выполненных попыток auto-retry для этого трека.
    /// Инкрементируется в <c>AudioEngine</c> перед каждым повторным запуском.
    /// </summary>
    public int RetryCount { get; init; }

    public CacheInvalidatedException(string message) : base(message) { }

    public CacheInvalidatedException(string message, Exception? inner) : base(message, inner) { }

    public CacheInvalidatedException(
        string message,
        CacheInvalidationKind kind,
        bool isRecoverable,
        string? trackId = null,
        int? chunkIndex = null,
        int retryCount = 0,
        Exception? inner = null)
        : base(message, inner)
    {
        Kind = kind;
        IsRecoverable = isRecoverable;
        TrackId = trackId;
        ChunkIndex = chunkIndex;
        RetryCount = retryCount;
    }
}

/// <summary>Классификация причины инвалидации кэша.</summary>
public enum CacheInvalidationKind
{
    /// <summary>Файл удалён с диска во время воспроизведения.</summary>
    FileDeleted,

    /// <summary>Файл усечён: объявленная длина больше реального размера.</summary>
    Truncated,

    /// <summary>Short read одного чанка: данные на диске неполные.</summary>
    ShortRead,

    /// <summary>Парсер контейнера выполнял resync и достиг EOF раньше времени.</summary>
    ParserResync,

    /// <summary>Неизвестная причина.</summary>
    Unknown
}