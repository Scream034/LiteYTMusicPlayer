namespace LMP.Core.Exceptions;

/// <summary>
/// Ошибка декодирования аудио.
/// </summary>
public class AudioDecoderException(string message, int errorCode = 0) : AudioException(message)
{
    public int ErrorCode { get; } = errorCode;
}