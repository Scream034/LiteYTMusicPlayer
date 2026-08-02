using System.Net;
using System.Text.RegularExpressions;

namespace LMP.Core.Youtube.Videos;

/// <summary>
/// Represents a syntactically valid YouTube video ID.
/// </summary>
public readonly partial struct VideoId(string value)
{
    /// <summary>
    /// Raw ID value.
    /// </summary>
    public string Value { get; } = value;

    /// <inheritdoc />
    public override string ToString() => Value;
}

public partial struct VideoId
{
    private static bool IsValid(string videoId)
    {
        if (videoId.Length != 11) return false;
        foreach (var c in videoId.AsSpan())
            if (!char.IsLetterOrDigit(c) && c is not ('_' or '-')) return false;
        return true;
    }


    private static string? TryNormalize(string? videoIdOrUrl)
    {
        if (string.IsNullOrWhiteSpace(videoIdOrUrl)) return null;

        if (IsValid(videoIdOrUrl)) return videoIdOrUrl;

        {
            var raw = MyRegex().Match(videoIdOrUrl).Groups[1].Value;
            var id = WebUtility.UrlDecode(raw);
            if (!string.IsNullOrWhiteSpace(id) && IsValid(id)) return id;
        }
        {
            var raw = MyRegex1().Match(videoIdOrUrl).Groups[1].Value;
            var id = WebUtility.UrlDecode(raw);
            if (!string.IsNullOrWhiteSpace(id) && IsValid(id)) return id;
        }
        {
            var raw = MyRegex2().Match(videoIdOrUrl).Groups[1].Value;
            var id = WebUtility.UrlDecode(raw);
            if (!string.IsNullOrWhiteSpace(id) && IsValid(id)) return id;
        }
        {
            var raw = MyRegex3().Match(videoIdOrUrl).Groups[1].Value;
            var id = WebUtility.UrlDecode(raw);
            if (!string.IsNullOrWhiteSpace(id) && IsValid(id)) return id;
        }
        {
            var raw = MyRegex4().Match(videoIdOrUrl).Groups[1].Value;
            var id = WebUtility.UrlDecode(raw);
            if (!string.IsNullOrWhiteSpace(id) && IsValid(id)) return id;
        }
        {
            var raw = MyRegex5.Match(videoIdOrUrl).Groups[1].Value;
            var id = WebUtility.UrlDecode(raw);
            if (!string.IsNullOrWhiteSpace(id) && IsValid(id)) return id;
        }

        return null;
    }

    /// <summary>
    /// Attempts to parse the specified string as a video ID or URL.
    /// Returns null in case of failure.
    /// </summary>
    public static VideoId? TryParse(string? videoIdOrUrl) =>
        TryNormalize(videoIdOrUrl) is { } id ? new VideoId(id) : default;

    /// <summary>
    /// Parses the specified string as a YouTube video ID or URL.
    /// Throws an exception in case of failure.
    /// </summary>
    public static VideoId Parse(string videoIdOrUrl) =>
        TryParse(videoIdOrUrl)
        ?? throw new ArgumentException($"Invalid YouTube video ID or URL '{videoIdOrUrl}'.");

    /// <summary>
    /// Converts string to ID.
    /// </summary>
    public static implicit operator VideoId(string videoIdOrUrl) => Parse(videoIdOrUrl);

    /// <summary>
    /// Converts ID to string.
    /// </summary>
    public static implicit operator string(VideoId videoId) => videoId.ToString();

    [GeneratedRegex(@"youtube\..+?/watch.*?v=(.*?)(?:&|/|$)")]
    private static partial Regex MyRegex();
    [GeneratedRegex(@"youtu\.be/watch.*?v=(.*?)(?:\?|&|/|$)")]
    private static partial Regex MyRegex1();
    [GeneratedRegex(@"youtu\.be/(.*?)(?:\?|&|/|$)")]
    private static partial Regex MyRegex2();
    [GeneratedRegex(@"youtube\..+?/embed/(.*?)(?:\?|&|/|$)")]
    private static partial Regex MyRegex3();
    [GeneratedRegex(@"youtube\..+?/shorts/(.*?)(?:\?|&|/|$)")]
    private static partial Regex MyRegex4();

    [GeneratedRegex(@"youtube\..+?/live/(.*?)(?:\?|&|/|$)")]
    private static partial Regex MyRegex5 { get; }
}

public partial struct VideoId : IEquatable<VideoId>
{
    /// <inheritdoc />
    public bool Equals(VideoId other) =>
        string.Equals(Value, other.Value, StringComparison.Ordinal);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is VideoId other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => Value.GetHashCode(StringComparison.Ordinal);

    /// <summary>
    /// Equality check.
    /// </summary>
    public static bool operator ==(VideoId left, VideoId right) => left.Equals(right);

    /// <summary>
    /// Equality check.
    /// </summary>
    public static bool operator !=(VideoId left, VideoId right) => !(left == right);
}
