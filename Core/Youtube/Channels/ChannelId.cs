using System.Net;
using System.Text.RegularExpressions;

namespace LMP.Core.Youtube.Channels;

/// <summary>
/// Represents a syntactically valid YouTube channel ID.
/// </summary>
public readonly partial struct ChannelId(string value)
{
    /// <summary>
    /// Raw ID value.
    /// </summary>
    public string Value { get; } = value;

    /// <inheritdoc />
    public override string ToString() => Value;
}

public partial struct ChannelId
{
    private static bool IsValid(string channelId)
    {
        if (!channelId.StartsWith("UC", StringComparison.Ordinal) || channelId.Length != 24)
            return false;
        foreach (var c in channelId.AsSpan())
            if (!char.IsLetterOrDigit(c) && c is not ('_' or '-')) return false;
        return true;
    }

    private static string? TryNormalize(string? channelIdOrUrl)
    {
        if (string.IsNullOrWhiteSpace(channelIdOrUrl)) return null;

        if (IsValid(channelIdOrUrl)) return channelIdOrUrl;

        var raw = MyRegex().Match(channelIdOrUrl).Groups[1].Value;
        var id = WebUtility.UrlDecode(raw);
        return !string.IsNullOrWhiteSpace(id) && IsValid(id) ? id : null;
    }

    /// <summary>
    /// Attempts to parse the specified string as a YouTube channel ID or URL.
    /// Returns null in case of failure.
    /// </summary>
    public static ChannelId? TryParse(string? channelIdOrUrl) =>
        TryNormalize(channelIdOrUrl) is { } id ? new ChannelId(id) : default;

    /// <summary>
    /// Parses the specified string as a YouTube channel ID or URL.
    /// </summary>
    public static ChannelId Parse(string channelIdOrUrl) =>
        TryParse(channelIdOrUrl)
        ?? throw new ArgumentException($"Invalid YouTube channel ID or URL '{channelIdOrUrl}'.");

    /// <summary>
    /// Converts string to ID.
    /// </summary>
    public static implicit operator ChannelId(string channelIdOrUrl) => Parse(channelIdOrUrl);

    /// <summary>
    /// Converts ID to string.
    /// </summary>
    public static implicit operator string(ChannelId channelId) => channelId.ToString();

    [GeneratedRegex(@"youtube\..+?/channel/(.*?)(?:\?|&|/|$)")]
    private static partial Regex MyRegex();
}

public partial struct ChannelId : IEquatable<ChannelId>
{
    /// <inheritdoc />
    public bool Equals(ChannelId other) =>
        string.Equals(Value, other.Value, StringComparison.Ordinal);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is ChannelId other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => Value.GetHashCode(StringComparison.Ordinal);

    /// <summary>
    /// Equality check.
    /// </summary>
    public static bool operator ==(ChannelId left, ChannelId right) => left.Equals(right);

    /// <summary>
    /// Equality check.
    /// </summary>
    public static bool operator !=(ChannelId left, ChannelId right) => !(left == right);
}
