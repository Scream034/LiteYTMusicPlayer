namespace LMP.Core.Exceptions;

/// <summary>
/// Бросается когда аудиоустройство вывода недоступно.
/// </summary>
public sealed class AudioDeviceException : Exception
{
    public AudioDeviceException(string message) : base(message) { }
    public AudioDeviceException(string message, Exception? inner) : base(message, inner) { }
}