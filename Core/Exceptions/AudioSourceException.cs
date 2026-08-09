namespace LMP.Core.Exceptions;

/// <summary>
/// Ошибка источника аудио.
/// </summary>
public class AudioSourceException : AudioException
{
    public AudioSourceException(string message) : base(message) { }
    public AudioSourceException(string message, Exception inner) : base(message, inner) { }
}