namespace LMP.Core.Exceptions;

/// <summary>
/// Базовое исключение для аудио операций.
/// </summary>
public class AudioException : Exception
{
    public AudioException(string message) : base(message) { }
    public AudioException(string message, Exception? inner) : base(message, inner) { }
}