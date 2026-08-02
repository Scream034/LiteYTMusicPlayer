using System.Net;
using System.Text.RegularExpressions;

namespace LMP.Core.Youtube.Channels;

/// <summary>
/// Represents a syntactically valid YouTube user name.
/// </summary>
public readonly partial struct UserName(string value)
{
    /// <summary>
    /// Raw user name value.
    /// </summary>
    public string Value { get; } = value;

    /// <inheritdoc />
    public override string ToString() => Value;
}

public partial struct UserName
{
    private static bool IsValid(string userName)
    {
        if (userName.Length > 20) return false;
        foreach (var c in userName.AsSpan())
            if (!char.IsLetterOrDigit(c)) return false;
        return true;
    }

    private static string? TryNormalize(string? userNameOrUrl)
    {
        if (string.IsNullOrWhiteSpace(userNameOrUrl)) return null;

        if (IsValid(userNameOrUrl)) return userNameOrUrl;

        var raw = MyRegex().Match(userNameOrUrl).Groups[1].Value;
        var userName = WebUtility.UrlDecode(raw);
        return !string.IsNullOrWhiteSpace(userName) && IsValid(userName) ? userName : null;
    }

    /// <summary>
    /// Attempts to parse the specified string as a YouTube user name or profile URL.
    /// Returns null in case of failure.
    /// </summary>
    public static UserName? TryParse(string? userNameOrUrl) =>
        TryNormalize(userNameOrUrl) is { } name ? new UserName(name) : default;

    /// <summary>
    /// Parses the specified string as a YouTube user name or profile URL.
    /// </summary>
    public static UserName Parse(string userNameOrUrl) =>
        TryParse(userNameOrUrl)
        ?? throw new ArgumentException(
            $"Invalid YouTube user name or profile URL '{userNameOrUrl}'."
        );

    /// <summary>
    /// Converts string to user name.
    /// </summary>
    public static implicit operator UserName(string userNameOrUrl) => Parse(userNameOrUrl);

    /// <summary>
    /// Converts user name to string.
    /// </summary>
    public static implicit operator string(UserName userName) => userName.ToString();

    [GeneratedRegex(@"youtube\..+?/user/(.*?)(?:\?|&|/|$)")]
    private static partial Regex MyRegex();
}

public partial struct UserName : IEquatable<UserName>
{
    /// <inheritdoc />
    public bool Equals(UserName other) =>
        string.Equals(Value, other.Value, StringComparison.Ordinal);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is UserName other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => Value.GetHashCode(StringComparison.Ordinal);

    /// <summary>
    /// Equality check.
    /// </summary>
    public static bool operator ==(UserName left, UserName right) => left.Equals(right);

    /// <summary>
    /// Equality check.
    /// </summary>
    public static bool operator !=(UserName left, UserName right) => !(left == right);
}
