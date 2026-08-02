using System.Runtime.CompilerServices;
using LMP.Core.Youtube.Bridge;
using LMP.Core.Youtube.Exceptions;

namespace LMP.Core.Youtube.Playlists;

public sealed class PlaylistClient(HttpClient http)
{
    private readonly PlaylistController _controller = new(http);

    private static readonly string[] MusicKeywords =
    [
        "official video", "official music", "official audio", "music video",
    "lyrics", "lyric video", "(audio)", "[audio]", "ft.", "feat.",
    "official mv", "m/v", "visualizer", "acoustic", "remix", "cover",
    "live performance", "official lyric", "audio only"
    ];

    private static readonly string[] NonMusicKeywords =
    [
        "tutorial", "how to", "review", "unboxing", "gameplay", "walkthrough",
    "podcast", "interview", "news", "trailer", "teaser", "behind the scenes",
    "making of", "reaction", "compilation", "best of", "highlights",
    "episode", "ep.", "part ", "chapter", "lecture", "course"
    ];

    /// <summary>
    /// Возвращает метаданные плейлиста.
    /// </summary>
    public async ValueTask<Playlist> GetAsync(
        PlaylistId playlistId,
        CancellationToken cancellationToken = default)
    {
        var response = await _controller.GetPlaylistResponseAsync(playlistId, cancellationToken);

        var title = response.Title
            ?? throw new YoutubeExplodeException("Failed to extract playlist title.");

        return new Playlist
        {
            Id = $"yt_{playlistId.Value}",
            YoutubeId = playlistId.Value,
            StoredName = title,
            Author = response.Author,
            Description = response.Description,
            ThumbnailUrl = ThumbnailUtils.GetBestUrl(response.Thumbnails),
            SyncMode = PlaylistSyncMode.CloudPublic
        };
    }

    /// <summary>
    /// Единый импорт плейлиста: метаданные и полный список треков за один начальный HTTP-запрос.
    /// Устраняет дублирующий browse, который возникал при раздельных вызовах
    /// <see cref="GetAsync"/> и <see cref="GetVideosAsync"/>.
    /// При пустом browse-ответе автоматически применяет fallback на Next response.
    /// Поддерживает пагинацию через continuation token.
    /// </summary>
    /// <param name="playlistId">Идентификатор плейлиста.</param>
    /// <param name="cancellationToken">Токен отмены операции.</param>
    /// <returns>Метаданные плейлиста и полный упорядоченный список треков.</returns>
    public async Task<PlaylistImportResult> ImportAsync(
        PlaylistId playlistId,
        CancellationToken cancellationToken = default)
    {
        PlaylistBrowseResponse? browseResponse;
        try
        {
            browseResponse = await _controller.GetPlaylistBrowseResponseAsync(
                playlistId, cancellationToken).ConfigureAwait(false);
        }
        catch (PlaylistUnavailableException)
        {
            // Browse недоступен — полный fallback на Next для метаданных и треков
            var nextResponse = await _controller.GetPlaylistNextResponseAsync(
                playlistId, null, 0, null, cancellationToken).ConfigureAwait(false);

            return new PlaylistImportResult(
                BuildPlaylistFromData(playlistId, nextResponse),
                CollectTracks(nextResponse.Videos, new HashSet<string>(StringComparer.Ordinal)));
        }

        var playlist = BuildPlaylistFromData(playlistId, browseResponse);

        var encounteredIds = new HashSet<string>(
            browseResponse.Count ?? 64, StringComparer.Ordinal);

        IReadOnlyList<PlaylistVideoData> firstPage = browseResponse.Videos;
        string? continuationToken = browseResponse.ContinuationToken;
        string? visitorData = browseResponse.VisitorData;

        // Browse вернул 0 видео — fallback на Next только для треков
        if (firstPage.Count == 0)
        {
            Log.Info($"[PlaylistClient] Browse returned 0 videos for '{playlistId}', attempting Next fallback");
            try
            {
                var nextFallback = await _controller.GetPlaylistNextResponseAsync(
                    playlistId, null, 0, visitorData, cancellationToken).ConfigureAwait(false);

                firstPage = nextFallback.Videos;
                visitorData ??= nextFallback.VisitorData;
                continuationToken = null;

                Log.Info($"[PlaylistClient] Next fallback: {firstPage.Count} videos");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log.Warn($"[PlaylistClient] Next fallback failed for '{playlistId}': {ex.Message}");
            }
        }

        Log.Debug($"[PlaylistClient] Import initial page: {firstPage.Count} videos, " +
                  $"has continuation: {continuationToken != null}");

        var allTracks = CollectTracks(firstPage, encounteredIds);

        // Пагинация через continuation token
        while (!string.IsNullOrEmpty(continuationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var contResp = await _controller.GetPlaylistContinuationAsync(
                    continuationToken, visitorData, cancellationToken).ConfigureAwait(false);

                var contVideos = contResp.Videos;
                Log.Debug($"[PlaylistClient] Import continuation: {contVideos.Count} videos, " +
                          $"has more: {contResp.ContinuationToken != null}");

                if (contVideos.Count == 0) break;

                AppendTracks(contVideos, encounteredIds, allTracks);
                visitorData ??= contResp.VisitorData;
                continuationToken = contResp.ContinuationToken;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Log.Warn($"[PlaylistClient] Import pagination error for '{playlistId}': {ex.Message}");
                break;
            }
        }

        Log.Info($"[PlaylistClient] Import complete: '{playlistId}' → {allTracks.Count} tracks");
        return new PlaylistImportResult(playlist, allTracks);
    }

    /// <summary>
    /// Асинхронно получает видеоролики плейлиста батчами.
    /// При пустом browse-результате выполняет fallback на Next response.
    /// </summary>
    public async IAsyncEnumerable<Batch<TrackInfo>> GetVideoBatchesAsync(
        PlaylistId playlistId,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var encounteredIds = new HashSet<string>(StringComparer.Ordinal);
        string? continuationToken = null;
        string? visitorData = null;
        bool isFirstRequest = true;

        do
        {
            IReadOnlyList<PlaylistVideoData> videos;

            if (isFirstRequest)
            {
                var browseResponse = await _controller.GetPlaylistBrowseResponseAsync(
                    playlistId, cancellationToken);

                videos = browseResponse.Videos;
                continuationToken = browseResponse.ContinuationToken;
                visitorData = browseResponse.VisitorData;
                isFirstRequest = false;

                Log.Debug($"[PlaylistClient] Initial browse: {videos.Count} videos, " +
                          $"has continuation: {continuationToken != null}");

                // Fallback: browse вернул 0 видео — пробуем Next response
                if (videos.Count == 0)
                {
                    Log.Info($"[PlaylistClient] Browse returned 0 videos for '{playlistId}', " +
                             $"attempting Next fallback");
                    try
                    {
                        var nextResponse = await _controller.GetPlaylistNextResponseAsync(
                            playlistId, null, 0, visitorData, cancellationToken);

                        if (nextResponse.IsAvailable)
                        {
                            videos = nextResponse.Videos;
                            visitorData ??= nextResponse.VisitorData;
                            continuationToken = null;

                            Log.Info($"[PlaylistClient] Next fallback: {videos.Count} videos");
                        }
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        Log.Warn($"[PlaylistClient] Next fallback failed for '{playlistId}': {ex.Message}");
                    }
                }
            }
            else if (!string.IsNullOrEmpty(continuationToken))
            {
                var contResponse = await _controller.GetPlaylistContinuationAsync(
                    continuationToken, visitorData, cancellationToken);

                videos = contResponse.Videos;
                continuationToken = contResponse.ContinuationToken;
                visitorData ??= contResponse.VisitorData;

                Log.Debug($"[PlaylistClient] Continuation: {videos.Count} videos, " +
                          $"has more: {continuationToken != null}");
            }
            else
            {
                break;
            }

            var batch = new List<TrackInfo>(videos.Count);
            AppendTracks(videos, encounteredIds, batch);

            if (batch.Count > 0)
                yield return Batch.Create(batch);

            if (string.IsNullOrEmpty(continuationToken) && batch.Count == 0)
                break;

        } while (!string.IsNullOrEmpty(continuationToken));
    }

    /// <summary>
    /// Строит модель плейлиста из данных API-ответа.
    /// Заполняет owner metadata, cloud count и visibility для дальнейшего сохранения в БД.
    /// </summary>
    private static Playlist BuildPlaylistFromData(PlaylistId playlistId, IPlaylistData data)
    {
        string? bestThumb = null;

        if (data.Thumbnails.Count > 0)
        {
            var domainThumbs = new List<Thumbnail>(data.Thumbnails.Count);
            for (int i = 0; i < data.Thumbnails.Count; i++)
            {
                var t = data.Thumbnails[i];
                domainThumbs.Add(new Thumbnail(t.Url!, new Resolution(t.Width ?? 0, t.Height ?? 0)));
            }
            bestThumb = ThumbnailUtils.GetBestUrl(domainThumbs);
        }

        var playlist = new Playlist
        {
            Id = $"yt_{playlistId.Value}",
            YoutubeId = playlistId.Value,
            StoredName = data.Title ?? string.Empty,
            Author = data.Author,
            Description = data.Description,
            ThumbnailUrl = bestThumb,
            OwnerChannelId = data.ChannelId,
            CloudTrackCount = data.Count,
            SyncMode = PlaylistSyncMode.CloudPublic
        };

        if (data is PlaylistBrowseResponse browseResponse)
        {
            playlist.Visibility = browseResponse.Visibility;
            playlist.ViewCount = browseResponse.ViewCount;
            playlist.ReleaseDate = browseResponse.ReleaseDate;
        }

        return playlist;
    }

    /// <summary>
    /// Конвертирует страницу видео в новый список <see cref="TrackInfo"/>.
    /// </summary>
    private static List<TrackInfo> CollectTracks(
        IReadOnlyList<PlaylistVideoData> videos,
        HashSet<string> encounteredIds)
    {
        var result = new List<TrackInfo>(videos.Count);
        AppendTracks(videos, encounteredIds, result);
        return result;
    }

    /// <summary>
    /// Добавляет треки из <paramref name="videos"/> в <paramref name="target"/>
    /// с дедупликацией через <paramref name="encounteredIds"/>.
    /// </summary>
    private static void AppendTracks(
        IReadOnlyList<PlaylistVideoData> videos,
        HashSet<string> encounteredIds,
        List<TrackInfo> target)
    {
        for (int i = 0; i < videos.Count; i++)
        {
            var videoData = videos[i];
            var videoId = videoData.Id;
            if (string.IsNullOrEmpty(videoId) || !encounteredIds.Add(videoId))
                continue;

            var title = videoData.Title ?? string.Empty;
            var author = videoData.Author ?? LocalizationService.Instance["Track_UnknownAuthor"];

            target.Add(new TrackInfo
            {
                // Идентификатор передается в сыром виде. Сеттер TrackInfo.Id автоматически 
                // применит кэшированное префиксирование "yt_", исключая лишние аллокации строк.
                Id = videoId,
                Title = title,
                Author = author,
                ChannelId = videoData.ChannelId,
                Duration = videoData.Duration ?? TimeSpan.Zero,
                ThumbnailUrl = ThumbnailUtils.GetBestUrl(videoData.Thumbnails, videoId),
                Url = $"https://www.youtube.com/watch?v={videoId}",
                IsMusic = DetectIfMusic(title, author, videoData.Duration)
            });
        }
    }

    /// <summary>
    /// Heuristic-based music detection since YouTube API doesn't provide this info.
    /// </summary>
    private static bool DetectIfMusic(string title, string author, TimeSpan? duration)
    {
        if (author.EndsWith(" - Topic", StringComparison.OrdinalIgnoreCase) ||
            author.EndsWith("VEVO", StringComparison.OrdinalIgnoreCase))
            return true;

        if (duration.HasValue)
        {
            var mins = duration.Value.TotalMinutes;
            if (mins < 1 || mins > 15)
                return ContainsMusicKeywords(title);
            if (mins >= 2 && mins <= 6)
                return true;
        }

        if (ContainsMusicKeywords(title)) return true;
        if (ContainsNonMusicKeywords(title)) return false;

        return true;
    }

    private static bool ContainsMusicKeywords(string title)
    {
        for (int i = 0; i < MusicKeywords.Length; i++)
            if (title.Contains(MusicKeywords[i], StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    private static bool ContainsNonMusicKeywords(string title)
    {
        for (int i = 0; i < NonMusicKeywords.Length; i++)
            if (title.Contains(NonMusicKeywords[i], StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    /// <summary>
    /// Возвращает все видео плейлиста как плоский поток <see cref="TrackInfo"/>.
    /// </summary>
    public IAsyncEnumerable<TrackInfo> GetVideosAsync(
        PlaylistId playlistId,
        CancellationToken cancellationToken = default
    ) => GetVideoBatchesAsync(playlistId, cancellationToken).FlattenAsync();
}

/// <summary>
/// Результат единого импорта плейлиста: метаданные и полный список треков
/// без дублирующего browse-запроса.
/// </summary>
/// <param name="Playlist">Метаданные плейлиста, полученные из API.</param>
/// <param name="Tracks">Полный список треков со всеми страницами пагинации.</param>
public sealed record PlaylistImportResult(Playlist Playlist, List<TrackInfo> Tracks);