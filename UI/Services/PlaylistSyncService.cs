using LMP.Core.Youtube.Exceptions;
using LMP.Core.Youtube.Music;

namespace LMP.UI.Services;

/// <summary>
/// Централизованный сервис синхронизации одного плейлиста с YouTube.
///
/// <para><b>Зачем нужен:</b></para>
/// <para>
/// Ранее синхронизация была размазана по <c>MusicLibraryManager</c> (массовая),
/// <c>PlaylistEditService</c> (привязка/отвязка) и <c>PlaylistViewModel</c> (кнопка Refresh).
/// Этот сервис — единый источник истины для синхронизации конкретного плейлиста.
/// </para>
///
/// <para><b>Архитектура:</b></para>
/// <list type="number">
///   <item><see cref="BuildPreviewAsync"/> — загрузить diff между локальным и облачным состоянием</item>
///   <item>ShowSyncPlaylistDialog — показать пользователю что изменилось</item>
///   <item><see cref="ApplyAsync"/> — применить выбранную стратегию</item>
/// </list>
///
/// <para><b>Важно про источники данных треков:</b></para>
/// <para>
/// Для diff используется <c>GetVideosAsync</c> (только воспроизводимые треки, без удалённых).
/// Для sync-операций используется <c>GetPlaylistItemsWithSetVideoIdAsync</c> (нужен SetVideoId).
/// Это намеренное разделение: удалённые видео должны игнорироваться в diff,
/// иначе они всегда будут показываться как «только в YouTube» после каждого merge.
/// </para>
/// </summary>
public sealed class PlaylistSyncService
{
    private readonly LibraryService _library;
    private readonly YoutubeProvider _youtube;
    private readonly CookieAuthService _auth;
    private readonly DialogService _dialog;

    /// <summary>
    /// Кэшированный снимок плейлиста из BuildPreviewAsync.
    /// Переиспользуется в ApplyAsync чтобы не делать повторный запрос.
    /// </summary>
    private FullPlaylistSyncData? _cachedSyncData;

    private static LocalizationService SL => LocalizationService.Instance;

    public PlaylistSyncService(
        LibraryService library,
        YoutubeProvider youtube,
        CookieAuthService auth,
        DialogService dialog)
    {
        _library = library;
        _youtube = youtube;
        _auth = auth;
        _dialog = dialog;
    }

    #region Public API

    /// <summary>
    /// Полный цикл синхронизации: preview → диалог → применение.
    /// Снимок плейлиста загружается ОДИН раз и переиспользуется в Apply.
    /// </summary>
    public async Task<PlaylistSyncResult?> SyncWithDialogAsync(
        string playlistId,
        CancellationToken ct = default)
    {
        var playlist = await _library.GetPlaylistAsync(playlistId, ct);
        if (playlist == null)
            return PlaylistSyncResult.Fail("Playlist not found");

        if (playlist.SyncMode != PlaylistSyncMode.TwoWaySync ||
            string.IsNullOrEmpty(playlist.YoutubeId))
        {
            return PlaylistSyncResult.Fail(
                SL["Playlist_SyncNotLinked"] ?? "Playlist is not linked to YouTube");
        }

        if (!_auth.IsAuthenticated)
            return PlaylistSyncResult.Fail(
                SL["Playlist_SyncNotAuth"] ?? "Not authenticated");

        _cachedSyncData = null;

        var preview = await BuildPreviewAsync(playlist, ct);
        if (preview == null)
            return PlaylistSyncResult.Fail(
                SL["Playlist_SyncFetchFailed"] ?? "Failed to fetch playlist data from YouTube");

        if (!preview.HasAnyDifference)
        {
            await _dialog.ShowInfoAsync(
                SL["Playlist_SyncWithCloud"] ?? "Sync",
                SL["Playlist_SyncNoChanges"] ?? "Playlist is already in sync");
            return PlaylistSyncResult.NoChanges();
        }

        var options = await _dialog.ShowPlaylistSyncDialogAsync(preview);
        if (options == null)
            return null;

        return await ApplyAsync(playlist, preview, options, ct);
    }

    /// <summary>
    /// Синхронизация без диалога — используется при первичной привязке плейлиста к YouTube.
    /// </summary>
    public async Task<PlaylistSyncResult> SyncDirectAsync(
        string playlistId,
        PlaylistSyncOptions options,
        CancellationToken ct = default)
    {
        var playlist = await _library.GetPlaylistAsync(playlistId, ct);
        if (playlist == null)
            return PlaylistSyncResult.Fail("Playlist not found");

        if (string.IsNullOrEmpty(playlist.YoutubeId))
            return PlaylistSyncResult.Fail("No YouTube ID");

        _cachedSyncData = null;

        var preview = await BuildPreviewAsync(playlist, ct);
        if (preview == null)
            return PlaylistSyncResult.Fail("Failed to fetch YouTube data");

        return await ApplyAsync(playlist, preview, options, ct);
    }

    #endregion

    #region Preview (Diff)

    /// <summary>
    /// Строит снимок различий между локальным и облачным состоянием плейлиста.
    /// При недоступности облака помечает плейлист как <see cref="Playlist.IsCloudUnavailable"/>.
    /// </summary>
    private async Task<PlaylistSyncPreview?> BuildPreviewAsync(
        Playlist playlist,
        CancellationToken ct)
    {
        try
        {
            YoutubeProvider.ThrowIfInCooldown();

            var fullDataTask = GetOrFetchFullDataAsync(playlist.YoutubeId!, ct);
            var localTrackIdsTask = _library.GetPlaylistTrackIdsAsync(playlist.Id, ct);

            await Task.WhenAll(fullDataTask, localTrackIdsTask);

            var fullData = await fullDataTask;
            var localTrackIds = await localTrackIdsTask;

            if (fullData == null)
            {
                playlist.IsCloudUnavailable = true;
                await _library.AddOrUpdatePlaylistAsync(playlist, ct);
                return null;
            }

            _cachedSyncData = fullData;

            // Успешный доступ — облако доступно
            if (playlist.IsCloudUnavailable)
            {
                playlist.IsCloudUnavailable = false;
                await _library.AddOrUpdatePlaylistAsync(playlist, ct);
            }

            var cloudVideoIds = new HashSet<string>(fullData.Tracks.Count, StringComparer.Ordinal);
            for (int i = 0; i < fullData.Tracks.Count; i++)
                cloudVideoIds.Add("yt_" + fullData.Tracks[i].VideoId);

            var localIdSet = new HashSet<string>(localTrackIds, StringComparer.Ordinal);

            int commonCount = 0;
            int cloudOnlyCount = 0;

            foreach (var cloudId in cloudVideoIds)
            {
                if (localIdSet.Contains(cloudId)) commonCount++;
                else cloudOnlyCount++;
            }

            int localOnlyCount = 0;
            foreach (var localId in localIdSet)
            {
                if (!cloudVideoIds.Contains(localId))
                    localOnlyCount++;
            }

            Log.Debug($"[PlaylistSync] Diff: common={commonCount}, " +
                      $"cloudOnly={cloudOnlyCount}, localOnly={localOnlyCount}");

            return new PlaylistSyncPreview
            {
                LocalName = playlist.Name,
                CloudName = fullData.Title ?? playlist.Name,
                LocalDescription = playlist.Description,
                CloudDescription = fullData.Description,
                LocalThumbnailUrl = playlist.ThumbnailUrl,
                CloudThumbnailUrl = fullData.ThumbnailUrl,
                LocalOnlyTrackCount = localOnlyCount,
                CloudOnlyTrackCount = cloudOnlyCount,
                CommonTrackCount = commonCount
            };
        }
        catch (Exception ex)
        {
            Log.Error($"[PlaylistSync] Preview failed: {ex.Message}");

            // Помечаем как недоступный при сетевых/API ошибках
            if (ex is PlaylistUnavailableException or HttpRequestException)
            {
                playlist.IsCloudUnavailable = true;
                try { await _library.AddOrUpdatePlaylistAsync(playlist, ct); } catch { }
            }

            return null;
        }
    }

    /// <summary>
    /// Нормализует URL обложки для стабильного сравнения.
    /// Убирает query string (sqp=..., v=...) — YouTube меняет их при каждом запросе.
    /// </summary>
    private static string? NormalizeThumbnailUrl(string? url)
    {
        if (string.IsNullOrEmpty(url)) return url;

        if (url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            var queryIndex = url.IndexOf('?');
            return queryIndex >= 0 ? url[..queryIndex] : url;
        }

        return url;
    }

    #endregion

    #region Apply Strategy

    /// <summary>
    /// Применяет выбранную стратегию синхронизации.
    /// После успешного завершения обновляет <see cref="Playlist.LastSyncedAtUtc"/>
    /// и сбрасывает <see cref="Playlist.IsCloudUnavailable"/>.
    /// </summary>
    private async Task<PlaylistSyncResult> ApplyAsync(
        Playlist playlist,
        PlaylistSyncPreview preview,
        PlaylistSyncOptions options,
        CancellationToken ct)
    {
        try
        {
            bool metadataChanged = false;
            int tracksAddedLocally = 0;
            int tracksAddedToCloud = 0;
            int tracksRemovedLocally = 0;
            int tracksRemovedFromCloud = 0;

            metadataChanged = await SyncMetadataAsync(playlist, preview, options, ct);

            if (options.SyncTracks)
            {
                (tracksAddedLocally, tracksAddedToCloud, tracksRemovedLocally, tracksRemovedFromCloud)
                    = options.Strategy switch
                    {
                        PlaylistSyncStrategy.ReplaceLocal =>
                            await ReplaceLocalTracksAsync(playlist, ct),
                        PlaylistSyncStrategy.ReplaceCloud =>
                            await ReplaceCloudTracksAsync(playlist, ct),
                        PlaylistSyncStrategy.Merge =>
                            await MergeTracksAsync(playlist, ct),
                        _ => (0, 0, 0, 0)
                    };
            }

            // Захватываем кэш до его обнуления (исправление бага с пустыми датами/просмотрами при merge)
            var cloudData = _cachedSyncData;
            _cachedSyncData = null;

            // Cloud-only stats (views, date) — pull-only из YouTube
            if (cloudData != null)
            {
                if (cloudData.ViewCount.HasValue && playlist.ViewCount != cloudData.ViewCount)
                    playlist.ViewCount = cloudData.ViewCount;

                if (playlist.ReleaseDate != cloudData.ReleaseDate)
                    playlist.ReleaseDate = cloudData.ReleaseDate;
            }

            // Sync state: всегда обновляем после успешного завершения
            playlist.LastSyncedAtUtc = DateTime.UtcNow;
            playlist.IsCloudUnavailable = false;
            playlist.UpdatedAt = DateTime.Now;
            await _library.AddOrUpdatePlaylistAsync(playlist, ct);

            var result = new PlaylistSyncResult
            {
                Success = true,
                MetadataChanged = metadataChanged,
                TracksAddedLocally = tracksAddedLocally,
                TracksAddedToCloud = tracksAddedToCloud,
                TracksRemovedLocally = tracksRemovedLocally,
                TracksRemovedFromCloud = tracksRemovedFromCloud
            };

            Log.Info($"[PlaylistSync] Completed: {result.Summary}");
            return result;
        }
        catch (Exception ex)
        {
            _cachedSyncData = null;
            Log.Error($"[PlaylistSync] Apply failed: {ex.Message}");
            return PlaylistSyncResult.Fail(ex.Message);
        }
    }

    #endregion

    #region Metadata Sync

    /// <summary>
    /// Синхронизирует метаданные по выбранным полям.
    ///
    /// <para><b>Направление:</b></para>
    /// <list type="bullet">
    ///   <item>ReplaceLocal / Merge → YouTube → Local</item>
    ///   <item>ReplaceCloud → Local → YouTube</item>
    /// </list>
    /// </summary>
    private async Task<bool> SyncMetadataAsync(
        Playlist playlist,
        PlaylistSyncPreview preview,
        PlaylistSyncOptions options,
        CancellationToken ct)
    {
        bool changed = false;
        bool isCloudSource = options.Strategy != PlaylistSyncStrategy.ReplaceCloud;

        if (options.SyncName && preview.NameDiffers)
        {
            if (isCloudSource)
            {
                playlist.Name = preview.CloudName;
                changed = true;
                Log.Info($"[PlaylistSync] Name updated locally: '{preview.CloudName}'");
            }
            else
            {
                try
                {
                    await _youtube.RenamePlaylistAsync(playlist.YoutubeId!, playlist.Name);
                    Log.Info($"[PlaylistSync] Name updated in YouTube: '{playlist.Name}'");
                }
                catch (Exception ex)
                {
                    Log.Error($"[PlaylistSync] Rename in YouTube failed: {ex.Message}");
                }
            }
        }

        if (options.SyncDescription && preview.DescriptionDiffers)
        {
            if (isCloudSource)
            {
                playlist.Description = preview.CloudDescription;
                changed = true;
                Log.Info("[PlaylistSync] Description updated locally");
            }
            else
            {
                try
                {
                    var client = _youtube.GetClient();
                    await client.Mutations.SetPlaylistDescriptionAsync(
                        playlist.YoutubeId!, playlist.Description ?? "", ct);
                    Log.Info("[PlaylistSync] Description updated in YouTube");
                }
                catch (Exception ex)
                {
                    Log.Error($"[PlaylistSync] Description update in YouTube failed: {ex.Message}");
                }
            }
        }

        if (options.SyncThumbnail)
        {
            if (isCloudSource)
            {
                // Сравниваем нормализованные URL для определения реального различия,
                // но сохраняем оригинальный URL из YouTube
                var cloudNorm = NormalizeThumbnailUrl(preview.CloudThumbnailUrl);
                var localNorm = NormalizeThumbnailUrl(playlist.ThumbnailUrl);

                if (!string.IsNullOrEmpty(preview.CloudThumbnailUrl) &&
                    !string.Equals(cloudNorm, localNorm, StringComparison.OrdinalIgnoreCase))
                {
                    playlist.ThumbnailUrl = preview.CloudThumbnailUrl;
                    playlist.ComputedColor = null;
                    changed = true;
                    Log.Info("[PlaylistSync] Thumbnail updated locally from YouTube");
                }
            }
            else
            {
                if (!string.IsNullOrEmpty(playlist.ThumbnailUrl))
                {
                    try
                    {
                        var success = await UploadThumbnailToYoutubeAsync(
                            playlist.YoutubeId!, playlist.ThumbnailUrl, ct);

                        if (success)
                            Log.Info("[PlaylistSync] Thumbnail uploaded to YouTube");
                        else
                            Log.Warn("[PlaylistSync] Thumbnail upload to YouTube skipped or failed");
                    }
                    catch (Exception ex)
                    {
                        Log.Error($"[PlaylistSync] Thumbnail upload failed: {ex.Message}");
                    }
                }
            }
        }

        return changed;
    }

    #endregion

    #region Track Sync Strategies

    /// <summary>
    /// YouTube → Local: полностью заменить локальные треки облачными.
    ///
    /// <para><b>Алгоритм:</b></para>
    /// <list type="number">
    ///   <item>Загружает полный снимок плейлиста из WEB_REMIX</item>
    ///   <item>Удаляет все локальные треки из плейлиста</item>
    ///   <item>Добавляет облачные треки и сохраняет setVideoId батчем</item>
    /// </list>
    /// </summary>
    private async Task<(int AddedLocally, int AddedToCloud, int RemovedLocally, int RemovedFromCloud)>
        ReplaceLocalTracksAsync(Playlist playlist, CancellationToken ct)
    {
        var youtubeId = playlist.YoutubeId!;

        var fullDataTask = GetOrFetchFullDataAsync(youtubeId, ct);
        var localTrackIdsTask = _library.GetPlaylistTrackIdsAsync(playlist.Id, ct);

        await Task.WhenAll(fullDataTask, localTrackIdsTask);

        var fullData = await fullDataTask;
        var localTrackIds = await localTrackIdsTask;

        if (fullData == null)
        {
            Log.Error("[PlaylistSync] ReplaceLocal: failed to fetch cloud data");
            return (0, 0, 0, 0);
        }

        int removedLocally = 0;
        for (int i = 0; i < localTrackIds.Count; i++)
        {
            await _library.RemoveTrackFromPlaylistAsync(localTrackIds[i], playlist.Id, ct);
            removedLocally++;
        }

        int addedLocally = 0;
        var mappings = new List<(string TrackId, string SetVideoId)>(fullData.Tracks.Count);

        for (int i = 0; i < fullData.Tracks.Count; i++)
        {
            var remote = fullData.Tracks[i];
            var track = CreateTrackInfo(remote);

            await _library.AddOrUpdateTrackAsync(track, ct);
            await _library.AddTrackToPlaylistAsync(track, playlist.Id, ct);
            addedLocally++;

            if (!string.IsNullOrEmpty(remote.SetVideoId))
                mappings.Add(("yt_" + remote.VideoId, remote.SetVideoId));
        }

        if (mappings.Count > 0)
            await _library.UpdateSetVideoIdsAsync(playlist.Id, mappings, ct);

        Log.Info($"[PlaylistSync] ReplaceLocal: removed={removedLocally}, added={addedLocally}");
        return (addedLocally, 0, removedLocally, 0);
    }

    /// <summary>
    /// Local → YouTube: полностью заменить облачные треки локальными.
    /// </summary>
    private async Task<(int AddedLocally, int AddedToCloud, int RemovedLocally, int RemovedFromCloud)>
        ReplaceCloudTracksAsync(Playlist playlist, CancellationToken ct)
    {
        var youtubeId = playlist.YoutubeId!;

        var fullDataTask = GetOrFetchFullDataAsync(youtubeId, ct);
        var localTrackIdsTask = _library.GetPlaylistTrackIdsAsync(playlist.Id, ct);

        await Task.WhenAll(fullDataTask, localTrackIdsTask);

        var fullData = await fullDataTask;
        var localTrackIds = await localTrackIdsTask;

        if (fullData == null)
        {
            Log.Error("[PlaylistSync] ReplaceCloud: failed to fetch cloud data");
            return (0, 0, 0, 0);
        }

        int removedFromCloud = 0;
        if (fullData.Tracks.Count > 0)
        {
            var setVideoIds = new List<string>(fullData.Tracks.Count);
            for (int i = 0; i < fullData.Tracks.Count; i++)
            {
                if (!string.IsNullOrEmpty(fullData.Tracks[i].SetVideoId))
                    setVideoIds.Add(fullData.Tracks[i].SetVideoId);
            }

            if (setVideoIds.Count > 0)
            {
                await _youtube.RemoveTracksFromPlaylistAsync(youtubeId, setVideoIds);
                removedFromCloud = setVideoIds.Count;
            }
        }

        var ytTrackIds = new List<string>(localTrackIds.Count);
        for (int i = 0; i < localTrackIds.Count; i++)
        {
            if (localTrackIds[i].StartsWith("yt_", StringComparison.Ordinal))
                ytTrackIds.Add(localTrackIds[i]);
        }

        int addedToCloud = 0;
        if (ytTrackIds.Count > 0)
        {
            var newSetVideoIds = await _youtube.AddTracksToPlaylistAsync(youtubeId, ytTrackIds);
            addedToCloud = ytTrackIds.Count;

            var mappings = new List<(string TrackId, string SetVideoId)>(newSetVideoIds.Count);
            for (int i = 0; i < newSetVideoIds.Count && i < ytTrackIds.Count; i++)
            {
                if (!string.IsNullOrEmpty(newSetVideoIds[i]))
                    mappings.Add((ytTrackIds[i], newSetVideoIds[i]!));
            }

            if (mappings.Count > 0)
                await _library.UpdateSetVideoIdsAsync(playlist.Id, mappings, ct);
        }

        Log.Info($"[PlaylistSync] ReplaceCloud: removed={removedFromCloud}, added={addedToCloud}");
        return (0, addedToCloud, 0, removedFromCloud);
    }

    /// <summary>
    /// Двусторонний merge: добавить отсутствующие треки в обе стороны без удаления.
    ///
    /// <para><b>Cloud set строится из полного снимка WEB_REMIX</b>, включая greyed-out треки.
    /// Это предотвращает повторную заливку уже существующих в облаке недоступных треков.</para>
    /// </summary>
    private async Task<(int AddedLocally, int AddedToCloud, int RemovedLocally, int RemovedFromCloud)>
        MergeTracksAsync(Playlist playlist, CancellationToken ct)
    {
        var youtubeId = playlist.YoutubeId!;

        var fullDataTask = GetOrFetchFullDataAsync(youtubeId, ct);
        var localTrackIdsTask = _library.GetPlaylistTrackIdsAsync(playlist.Id, ct);

        await Task.WhenAll(fullDataTask, localTrackIdsTask);

        var fullData = await fullDataTask;
        var localTrackIds = await localTrackIdsTask;

        if (fullData == null)
        {
            Log.Error("[PlaylistSync] Merge: failed to fetch cloud data");
            return (0, 0, 0, 0);
        }

        var localIdSet = new HashSet<string>(localTrackIds, StringComparer.Ordinal);

        // Cloud set строим из ПОЛНОГО снимка (включая greyed-out)
        var cloudIdSet = new HashSet<string>(fullData.Tracks.Count, StringComparer.Ordinal);
        for (int i = 0; i < fullData.Tracks.Count; i++)
            cloudIdSet.Add("yt_" + fullData.Tracks[i].VideoId);

        // Cloud-only → добавить локально
        int addedLocally = 0;
        var setVideoIdMappings = new List<(string TrackId, string SetVideoId)>(fullData.Tracks.Count);

        for (int i = 0; i < fullData.Tracks.Count; i++)
        {
            var remote = fullData.Tracks[i];
            var fullId = "yt_" + remote.VideoId;

            if (!string.IsNullOrEmpty(remote.SetVideoId))
                setVideoIdMappings.Add((fullId, remote.SetVideoId));

            if (!localIdSet.Contains(fullId))
            {
                var track = CreateTrackInfo(remote);
                await _library.AddOrUpdateTrackAsync(track, ct);
                await _library.AddTrackToPlaylistAsync(track, playlist.Id, ct);
                addedLocally++;
            }
        }

        // Local-only → добавить в YouTube
        var toUpload = new List<string>();
        for (int i = 0; i < localTrackIds.Count; i++)
        {
            var trackId = localTrackIds[i];
            if (trackId.StartsWith("yt_", StringComparison.Ordinal) &&
                !cloudIdSet.Contains(trackId))
            {
                toUpload.Add(trackId);
            }
        }

        int addedToCloud = 0;
        if (toUpload.Count > 0)
        {
            var newSetVideoIds = await _youtube.AddTracksToPlaylistAsync(youtubeId, toUpload);
            addedToCloud = toUpload.Count;

            for (int i = 0; i < newSetVideoIds.Count && i < toUpload.Count; i++)
            {
                if (!string.IsNullOrEmpty(newSetVideoIds[i]))
                    setVideoIdMappings.Add((toUpload[i], newSetVideoIds[i]!));
            }
        }

        if (setVideoIdMappings.Count > 0)
            await _library.UpdateSetVideoIdsAsync(playlist.Id, setVideoIdMappings, ct);

        Log.Info($"[PlaylistSync] Merge: +{addedLocally} local, +{addedToCloud} cloud");
        return (addedLocally, addedToCloud, 0, 0);
    }

    /// <summary>
    /// Создаёт TrackInfo из RemoteTrackInfo для сохранения в локальную библиотеку.
    /// </summary>
    private static TrackInfo CreateTrackInfo(RemoteTrackInfo remote) => new()
    {
        Id = "yt_" + remote.VideoId,
        Title = remote.Title,
        Author = remote.Author,
        Duration = TimeSpan.FromSeconds(remote.DurationSeconds),
        ThumbnailUrl = remote.ThumbnailUrl,
        IsMusic = true,
        Url = $"https://music.youtube.com/watch?v={remote.VideoId}"
    };

    #endregion

    #region Helpers

    /// <summary>
    /// Загружает локальную обложку в YouTube.
    /// Поддерживает HTTP URL (скачивает) и локальные файлы (читает напрямую).
    /// </summary>
    private async Task<bool> UploadThumbnailToYoutubeAsync(
        string youtubePlaylistId,
        string thumbnailUrl,
        CancellationToken ct)
    {
        byte[] imageData;

        if (thumbnailUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            using var httpClient = new HttpClient();
            httpClient.Timeout = TimeSpan.FromSeconds(15);

            var response = await httpClient.GetAsync(thumbnailUrl, ct);
            response.EnsureSuccessStatusCode();
            imageData = await response.Content.ReadAsByteArrayAsync(ct);
        }
        else if (thumbnailUrl.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
        {
            var uri = new Uri(thumbnailUrl);
            var localPath = uri.LocalPath;

            if (!File.Exists(localPath))
            {
                Log.Warn($"[PlaylistSync] Thumbnail file not found: {localPath}");
                return false;
            }

            imageData = await File.ReadAllBytesAsync(localPath, ct);
        }
        else if (Path.IsPathRooted(thumbnailUrl) && File.Exists(thumbnailUrl))
        {
            imageData = await File.ReadAllBytesAsync(thumbnailUrl, ct);
        }
        else
        {
            Log.Warn($"[PlaylistSync] Invalid thumbnail URL for upload: {thumbnailUrl}");
            return false;
        }

        if (imageData.Length == 0)
        {
            Log.Warn("[PlaylistSync] Thumbnail is empty, skipping upload");
            return false;
        }

        if (imageData.Length > 20 * 1024 * 1024)
        {
            Log.Warn($"[PlaylistSync] Thumbnail too large: {imageData.Length / 1024 / 1024}MB (max 20MB)");
            return false;
        }

        return await _youtube.UploadPlaylistThumbnailAsync(
            youtubePlaylistId, imageData);
    }

    /// <summary>
    /// Возвращает кэшированный снимок или делает свежий запрос.
    /// Кэш заполняется в BuildPreviewAsync и очищается после ApplyAsync.
    /// </summary>
    private async Task<FullPlaylistSyncData?> GetOrFetchFullDataAsync(
        string youtubeId, CancellationToken ct)
    {
        if (_cachedSyncData != null)
        {
            Log.Debug("[PlaylistSync] Using cached sync data (no extra request)");
            return _cachedSyncData;
        }

        return await _youtube.GetFullPlaylistDataAsync(youtubeId, ct);
    }

    #endregion
}