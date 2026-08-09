namespace LMP.Core.Exceptions;

/// <summary>
/// Выбрасывается когда CDN-хост недоступен для медиа-трафика
/// (ТСПУ silent drop на /videoplayback).
/// </summary>
public sealed class CdnUnavailableException(string host, string url)
    : Exception($"CDN host '{host}' is unavailable for media path (TSPU block?)")
{
    /// <summary>CDN hostname.</summary>
    public string Host { get; } = host;

    /// <summary>Оригинальный media URL.</summary>
    public string MediaUrl { get; } = url;
}