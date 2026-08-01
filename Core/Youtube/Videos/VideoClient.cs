using LMP.Core.Youtube.Bridge;
using LMP.Core.Youtube.Bridge.NToken;
using LMP.Core.Youtube.Bridge.PoToken;
using LMP.Core.Youtube.Bridge.SigCipher;
using LMP.Core.Youtube.Exceptions;
using LMP.Core.Youtube.Videos.Streams;

namespace LMP.Core.Youtube.Videos;

/// <summary>
/// Предоставляет методы для получения информации о видео YouTube.
/// </summary>
public sealed class VideoClient
{
    private readonly VideoController _controller;
    private readonly Func<bool>? _isAuthenticatedCheck;

    /// <summary>Клиент управления аудиопотоками.</summary>
    public StreamClient Streams { get; }

    /// <param name="http">HTTP-клиент.</param>
    /// <param name="nTokenDecryptor">Провайдер расшифровки N-Token.</param>
    /// <param name="sigCipherDecryptor">Провайдер расшифровки подписи.</param>
    /// <param name="isAuthenticatedCheck">Callback проверки авторизации.</param>
    /// <param name="poTokenProvider">Провайдер PoToken; <c>null</c> отключает pot.</param>
    public VideoClient(
        HttpClient http,
        NTokenDecryptor nTokenDecryptor,
        SigCipherDecryptor sigCipherDecryptor,
        Func<bool>? isAuthenticatedCheck = null,
        PoTokenProvider? poTokenProvider = null)
    {
        _controller = new VideoController(http, sigCipherDecryptor.PlayerManager);
        _isAuthenticatedCheck = isAuthenticatedCheck;
        Streams = new StreamClient(
            http, nTokenDecryptor, sigCipherDecryptor,
            isAuthenticatedCheck, poTokenProvider);
    }

    /// <summary>
    /// Возвращает метаданные трека по его идентификатору.
    /// </summary>
    /// <param name="videoId">Идентификатор видео YouTube.</param>
    /// <param name="cancellationToken">Токен отмены операции.</param>
    public async ValueTask<TrackInfo> GetAsync(
        VideoId videoId,
        CancellationToken cancellationToken = default)
    {
        var watchPage = await _controller
            .GetVideoWatchPageAsync(videoId, cancellationToken)
            .ConfigureAwait(false);

        var playerResponse = watchPage.PlayerResponse
                             ?? await GetPlayerResponseAsync(videoId, cancellationToken)
                                 .ConfigureAwait(false);

        var title = playerResponse.Title ?? "";
        var channelTitle = playerResponse.Author
                           ?? throw new YoutubeExplodeException("Failed to extract video author.");
        var channelId = playerResponse.ChannelId
                        ?? throw new YoutubeExplodeException("Failed to extract video channel ID.");

        var thumb = ThumbnailUtils.GetBestUrl(
            playerResponse.Thumbnails, videoId.Value);

        return new TrackInfo
        {
            Id = $"yt_{videoId.Value}",
            Title = title,
            Author = channelTitle,
            ChannelId = channelId,
            Duration = playerResponse.Duration ?? TimeSpan.Zero,
            ThumbnailUrl = thumb,
            Url = $"https://www.youtube.com/watch?v={videoId}",
            IsMusic = playerResponse.IsMusic,
        };
    }

    /// <summary>
    /// Возвращает PlayerResponse с полной fallback-цепочкой клиентов и корректным auth-контекстом.
    /// </summary>
    /// <param name="videoId">Идентификатор видео YouTube.</param>
    /// <param name="cancellationToken">Токен отмены операции.</param>
    internal async ValueTask<PlayerResponse> GetPlayerResponseAsync(
        VideoId videoId,
        CancellationToken cancellationToken = default)
    {
        bool isAuth = _isAuthenticatedCheck?.Invoke() ?? false;
        var (response, _) = await _controller.GetPlayerResponseWithFallbackAsync(
            videoId,
            cancellationToken,
            isAuthenticated: isAuth).ConfigureAwait(false);

        return response;
    }
}