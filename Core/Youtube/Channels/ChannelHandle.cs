using System.Net;
using System.Text.RegularExpressions;

namespace LMP.Core.Youtube.Channels;

/// <summary>
/// Represents a syntactically valid YouTube channel handle.
/// </summary>
public readonly partial struct ChannelHandle(string value)
{
    /// <summary>
    /// Raw handle value.
    /// </summary>
    public string Value { get; } = value;

    /// <inheritdoc />
    public override string ToString() => Value;
}

public readonly partial struct ChannelHandle
{
    private static bool IsValid(string channelHandle)
    {
        foreach (var c in channelHandle.AsSpan())
            if (!char.IsLetterOrDigit(c) && c is not ('_' or '-' or '.')) return false;
        return true;
    }

    private static string? TryNormalize(string? channelHandleOrUrl)
    {
        if (string.IsNullOrWhiteSpace(channelHandleOrUrl)) return null;

        if (IsValid(channelHandleOrUrl)) return channelHandleOrUrl;

        var raw = MyRegex().Match(channelHandleOrUrl).Groups[1].Value;
        var handle = WebUtility.UrlDecode(raw);
        return !string.IsNullOrWhiteSpace(handle) && IsValid(handle) ? handle : null;
    }

    /// <summary>
    /// Attempts to parse the specified string as a YouTube channel handle or custom URL.
    /// Returns null in case of failure.
    /// </summary>
    public static ChannelHandle? TryParse(string? channelHandleOrUrl) =>
        TryNormalize(channelHandleOrUrl) is { } handle ? new ChannelHandle(handle) : default;

    /// <summary>
    /// Parses the specified string as a YouTube channel handle or custom URL.
    /// </summary>
    public static ChannelHandle Parse(string channelHandleOrUrl) =>
        TryParse(channelHandleOrUrl)
        ?? throw new ArgumentException(
            $"Invalid YouTube channel handle or custom URL '{channelHandleOrUrl}'."
        );

    /// <summary>
    /// Converts string to channel handle.
    /// </summary>
    public static implicit operator ChannelHandle(string channelHandleOrUrl) =>
        Parse(channelHandleOrUrl);

    /// <summary>
    /// Converts channel handle to string.
    /// </summary>
    public static implicit operator string(ChannelHandle channelHandle) => channelHandle.ToString();

    [GeneratedRegex(@"youtube\..+?/@(.*?)(?:\?|&|/|$)")]
    private static partial Regex MyRegex();
}