using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using LMP.Core.Youtube.Bridge;

using LMP.Core.Youtube.Exceptions;
using LMP.Core.Youtube.Playlists;

namespace LMP.Core.Youtube.Channels;

public partial class ChannelClient(HttpClient http)
{
    private readonly ChannelController _controller = new(http);

    private static Channel Get(ChannelPage channelPage)
    {
        var channelId =
            channelPage.Id
            ?? throw new YoutubeExplodeException("Failed to extract the channel ID.");

        var title =
            channelPage.Title
            ?? throw new YoutubeExplodeException("Failed to extract the channel title.");

        var logoUrl =
            channelPage.LogoUrl
            ?? throw new YoutubeExplodeException("Failed to extract the channel logo URL.");

        var logoSize = 100;
        var matches = MyRegex().Matches(logoUrl);
        if (matches.Count > 0)
        {
            var raw = matches[^1].Groups[1].Value;
            if (!string.IsNullOrWhiteSpace(raw)
                && int.TryParse(raw, CultureInfo.InvariantCulture, out var parsed))
            {
                logoSize = parsed;
            }
        }

        return new Channel(
            new ChannelId(channelId),
            title,
            [new Thumbnail(logoUrl, new Resolution(logoSize, logoSize))]);
    }

    public async ValueTask<Channel> GetAsync(
        ChannelId channelId,
        CancellationToken cancellationToken = default
    )
    {
        if (channelId.Value == "UCuVPpxrm2VAgpH3Ktln4HXg")
        {
            return new Channel(
                new ChannelId("UCuVPpxrm2VAgpH3Ktln4HXg"),
                "Movies & TV",
                [
                    new Thumbnail(
                        "https://www.gstatic.com/youtube/img/tvfilm/clapperboard_profile.png",
                        new Resolution(1024, 1024)
                    ),
                ]
            );
        }

        return Get(await _controller.GetChannelPageAsync(channelId, cancellationToken));
    }

    public async ValueTask<Channel> GetByUserAsync(
        UserName userName,
        CancellationToken cancellationToken = default
    ) => Get(await _controller.GetChannelPageAsync(userName, cancellationToken));

    public async ValueTask<Channel> GetBySlugAsync(
        ChannelSlug channelSlug,
        CancellationToken cancellationToken = default
    ) => Get(await _controller.GetChannelPageAsync(channelSlug, cancellationToken));

    public async ValueTask<Channel> GetByHandleAsync(
        ChannelHandle channelHandle,
        CancellationToken cancellationToken = default
    ) => Get(await _controller.GetChannelPageAsync(channelHandle, cancellationToken));

    public IAsyncEnumerable<TrackInfo> GetUploadsAsync(
        ChannelId channelId,
        CancellationToken cancellationToken = default
    )
    {
        var playlistId = "UU" + channelId.Value[2..];
        return new PlaylistClient(http).GetVideosAsync(new PlaylistId(playlistId), cancellationToken);
    }

    public IAsyncEnumerable<Playlist> GetPlaylistsAsync(
        ChannelId channelId,
        CancellationToken cancellationToken = default
    ) => GetPlaylistBatchesAsync(channelId, cancellationToken).FlattenAsync();

    public async IAsyncEnumerable<Batch<Playlist>> GetPlaylistBatchesAsync(
        ChannelId channelId,
        [EnumeratorCancellation] CancellationToken cancellationToken = default
    )
    {
        var channelPage = await _controller.GetChannelPageAsync(channelId, cancellationToken);
        var channelTitle = channelPage.Title ?? "Unknown Channel";
        // Author больше не объект в модели Playlist, а строка
        var authorName = channelTitle;

        var encounteredIds = new HashSet<string>(StringComparer.Ordinal);
        var continuationToken = default(string?);

        do
        {
            var response = await _controller.GetChannelPlaylistsPageAsync(
                channelId,
                continuationToken,
                cancellationToken
            );

            var batch = new List<Playlist>();

            foreach (var item in response.Playlists)
            {
                // item.Id уже строка в модели Playlist
                if (!encounteredIds.Add(item.Id))
                    continue;

                // Обогащаем плейлист автором, так как в ответе списка плейлистов автора может не быть
                item.Author ??= authorName;
                batch.Add(item);
            }

            if (batch.Count == 0 && continuationToken == null && response.ContinuationToken == null)
                yield break;

            if (batch.Count > 0)
                yield return Batch.Create(batch);

            continuationToken = response.ContinuationToken;

        } while (!string.IsNullOrWhiteSpace(continuationToken));
    }

    [GeneratedRegex(@"\bs(\d+)\b")]
    private static partial Regex MyRegex();
}