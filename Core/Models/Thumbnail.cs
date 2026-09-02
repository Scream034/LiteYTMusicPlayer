using System.Diagnostics.CodeAnalysis;
using LMP.Core.Youtube.Videos;

namespace LMP.Core.Models;

/// <summary>
/// Thumbnail image.
/// </summary>
public partial class Thumbnail(string url, Resolution resolution)
{
    /// <summary>
    /// Thumbnail URL.
    /// </summary>
    public string Url { get; } = url;

    /// <summary>
    /// Thumbnail resolution.
    /// </summary>
    public Resolution Resolution { get; } = resolution;

    /// <inheritdoc />
    [ExcludeFromCodeCoverage]
    public override string ToString() => $"Thumbnail ({Resolution})";
}

public partial class Thumbnail
{
    internal static IReadOnlyList<Thumbnail> GetDefaultSet(VideoId videoId) =>
        [
            new($"https://img.youtube.com/vi/{videoId}/default.jpg", new Resolution(120, 90)),
            new($"https://img.youtube.com/vi/{videoId}/mqdefault.jpg", new Resolution(320, 180)),
            new($"https://img.youtube.com/vi/{videoId}/hqdefault.jpg", new Resolution(480, 360)),
        ];
}