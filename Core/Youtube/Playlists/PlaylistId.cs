using System.Net;
using System.Text.RegularExpressions;

namespace LMP.Core.Youtube.Playlists;

/// <summary>
/// Represents a syntactically valid YouTube playlist ID.
/// </summary>
public readonly partial struct PlaylistId(string value)
{
    /// <summary>
    /// Raw ID value.
    /// </summary>
    public string Value { get; } = value;

    /// <inheritdoc />
    public override string ToString() => Value;
}

public partial struct PlaylistId
{
    private static bool IsValid(string playlistId)
    {
        if (playlistId.Length < 2) return false;
        foreach (var c in playlistId.AsSpan())
            if (!char.IsLetterOrDigit(c) && c is not ('_' or '-')) return false;
        return true;
    }

    private static string? TryNormalize(string? playlistIdOrUrl)
    {
        if (string.IsNullOrWhiteSpace(playlistIdOrUrl)) return null;

        if (IsValid(playlistIdOrUrl)) return playlistIdOrUrl;

        {
            var raw = MyRegex().Match(playlistIdOrUrl).Groups[1].Value;
            var id = WebUtility.UrlDecode(raw);
            if (!string.IsNullOrWhiteSpace(id) && IsValid(id)) return id;
        }
        {
            var raw = MyRegex1().Match(playlistIdOrUrl).Groups[1].Value;
            var id = WebUtility.UrlDecode(raw);
            if (!string.IsNullOrWhiteSpace(id) && IsValid(id)) return id;
        }
        {
            var raw = MyRegex2().Match(playlistIdOrUrl).Groups[1].Value;
            var id = WebUtility.UrlDecode(raw);
            if (!string.IsNullOrWhiteSpace(id) && IsValid(id)) return id;
        }
        {
            var raw = MyRegex3().Match(playlistIdOrUrl).Groups[1].Value;
            var id = WebUtility.UrlDecode(raw);
            if (!string.IsNullOrWhiteSpace(id) && IsValid(id)) return id;
        }

        return null;
    }

    /// <summary>
    /// Attempts to parse the specified string as a YouTube playlist ID or URL.
    /// Returns null in case of failure.
    /// </summary>
    public static PlaylistId? TryParse(string? playlistIdOrUrl) =>
        TryNormalize(playlistIdOrUrl) is { } id ? new PlaylistId(id) : default;

    /// <summary>
    /// Parses the specified string as a YouTube playlist ID or URL.
    /// </summary>
    public static PlaylistId Parse(string playlistIdOrUrl) =>
        TryParse(playlistIdOrUrl)
        ?? throw new ArgumentException($"Invalid YouTube playlist ID or URL '{playlistIdOrUrl}'.");

    /// <summary>
    /// Converts string to ID.
    /// </summary>
    public static implicit operator PlaylistId(string playlistIdOrUrl) => Parse(playlistIdOrUrl);

    /// <summary>
    /// Converts ID to string.
    /// </summary>
    public static implicit operator string(PlaylistId playlistId) => playlistId.ToString();

    [GeneratedRegex(@"youtube\..+?/playlist.*?list=(.*?)(?:&|/|$)")]
    private static partial Regex MyRegex();
    [GeneratedRegex(@"youtube\..+?/watch.*?list=(.*?)(?:&|/|$)")]
    private static partial Regex MyRegex1();
    [GeneratedRegex(@"youtu\.be/.*?/.*?list=(.*?)(?:&|/|$)")]
    private static partial Regex MyRegex2();
    [GeneratedRegex(@"youtube\..+?/embed/.*?/.*?list=(.*?)(?:&|/|$)")]
    private static partial Regex MyRegex3();
}

public partial struct PlaylistId : IEquatable<PlaylistId>
{
    /// <inheritdoc />
    public bool Equals(PlaylistId other) =>
        string.Equals(Value, other.Value, StringComparison.Ordinal);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is PlaylistId other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => Value.GetHashCode(StringComparison.Ordinal);

    /// <summary>
    /// Equality check.
    /// </summary>
    public static bool operator ==(PlaylistId left, PlaylistId right) => left.Equals(right);

    /// <summary>
    /// Equality check.
    /// </summary>
    public static bool operator !=(PlaylistId left, PlaylistId right) => !(left == right);
}
