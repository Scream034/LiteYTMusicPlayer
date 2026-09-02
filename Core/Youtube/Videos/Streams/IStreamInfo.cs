namespace LMP.Core.Youtube.Videos.Streams;

/// <summary>
/// Metadata associated with a media stream of a YouTube video.
/// </summary>
public interface IStreamInfo
{
    /// <summary>
    /// Stream Itag (Format ID)
    /// </summary>
    int Itag { get; }

    /// <summary>
    /// Stream URL. Fully decrypted and ready for playback
    /// </summary>
    string Url { get; }

    /// <summary>
    /// Stream container
    /// </summary>
    Container Container { get; }

    /// <summary>
    /// Stream size
    /// </summary>
    FileSize Size { get; }

    /// <summary>
    /// Stream bitrate
    /// </summary>
    Bitrate Bitrate { get; }
}