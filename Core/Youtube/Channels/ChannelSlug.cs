using System.Net;
using System.Text.RegularExpressions;

namespace LMP.Core.Youtube.Channels;

/// <summary>
/// Represents a syntactically valid YouTube channel slug.
/// </summary>
public readonly partial struct ChannelSlug(string value)
{
    /// <summary>
    /// Raw slug value.
    /// </summary>
    public string Value { get; } = value;

    /// <inheritdoc />
    public override string ToString() => Value;
}

public readonly partial struct ChannelSlug
{
    private static bool IsValid(string channelSlug)
    {
        foreach (var c in channelSlug.AsSpan())
            if (!char.IsLetterOrDigit(c)) return false;
        return true;
    }


    private static string? TryNormalize(string? channelSlugOrUrl)
    {
        if (string.IsNullOrWhiteSpace(channelSlugOrUrl)) return null;

        if (IsValid(channelSlugOrUrl)) return channelSlugOrUrl;

        var raw = MyRegex().Match(channelSlugOrUrl).Groups[1].Value;
        var slug = WebUtility.UrlDecode(raw);
        return !string.IsNullOrWhiteSpace(slug) && IsValid(slug) ? slug : null;
    }

    /// <summary>
    /// Attempts to parse the specified string as a YouTube channel slug or legacy custom URL.
    /// Returns null in case of failure.
    /// </summary>
    public static ChannelSlug? TryParse(string? channelSlugOrUrl) =>
        TryNormalize(channelSlugOrUrl) is { } slug ? new ChannelSlug(slug) : default;

    /// <summary>
    /// Parses the specified string as a YouTube channel slug or legacy custom url.
    /// </summary>
    public static ChannelSlug Parse(string channelSlugOrUrl) =>
        TryParse(channelSlugOrUrl)
        ?? throw new ArgumentException(
            $"Invalid YouTube channel slug or legacy custom URL '{channelSlugOrUrl}'."
        );

    /// <summary>
    /// Converts string to channel slug.
    /// </summary>
    public static implicit operator ChannelSlug(string channelSlugOrUrl) => Parse(channelSlugOrUrl);

    /// <summary>
    /// Converts channel slug to string.
    /// </summary>
    public static implicit operator string(ChannelSlug channelSlug) => channelSlug.ToString();

    [GeneratedRegex(@"youtube\..+?/c/(.*?)(?:\?|&|/|$)")]
    private static partial Regex MyRegex();
}
