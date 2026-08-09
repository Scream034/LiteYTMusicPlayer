namespace LMP.Core.Exceptions;

/// <summary>
/// Неподдерживаемый формат аудио.
/// </summary>
public class UnsupportedFormatException(string format)
    : AudioException($"Unsupported audio format: {format}")
{
    public string Format { get; } = format;
}