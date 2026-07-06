namespace LMP.Core.Youtube.Exceptions;

/// <summary>
/// Exception thrown within <see cref="Youtube" />.
/// </summary>
public class YoutubeExplodeException : Exception
{
    public YoutubeExplodeException(string message) : base(message) { }

    public YoutubeExplodeException(string message, Exception? innerException)
        : base(message, innerException) { }
}