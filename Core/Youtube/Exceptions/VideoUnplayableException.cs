namespace LMP.Core.Youtube.Exceptions;

/// <summary>
/// Exception thrown when the requested video is unplayable through any client.
/// </summary>
public class VideoUnplayableException : YoutubeExplodeException
{
    /// <summary>Идентификатор видео (raw, без <c>yt_</c> префикса).</summary>
    public string VideoId { get; }

    /// <param name="message">Описание ошибки.</param>
    /// <param name="videoId">Raw video ID.</param>
    public VideoUnplayableException(string message, string videoId)
        : base(message)
    {
        VideoId = videoId;
    }

    /// <summary>Backward-compatible конструктор для legacy throw-sites.</summary>
    public VideoUnplayableException(string message)
        : base(message)
    {
        VideoId = "";
    }
}