namespace LMP.UI.Helpers;

/// <summary>
/// Расширения для интеграции AudioEngine с UI-слоем уведомлений.
/// </summary>
public static class AudioEngineExtensions
{
    /// <summary>
    /// Безопасно добавляет список треков в очередь с фильтрацией дубликатов и выводом всплывающего хинта.
    /// </summary>
    /// <param name="audio">Экземпляр аудио-движка.</param>
    /// <param name="tracks">Список треков для добавления.</param>
    /// <param name="playlistName">Название источника (плейлиста) для логов.</param>
    public static void EnqueuePlaylistWithNotification(
        this AudioEngine audio,
        IEnumerable<TrackInfo> tracks,
        string playlistName)
    {
        var trackList = tracks as List<TrackInfo> ?? [.. tracks];
        if (trackList.Count == 0) return;

        int addedCount = audio.EnqueueRangeUnique(trackList);
        int skippedCount = trackList.Count - addedCount;

        var L = LocalizationService.Instance;

        if (addedCount > 0)
        {
            if (skippedCount > 0)
            {
                Log.Info($"[Queue] Added {addedCount} unique tracks from '{playlistName}', skipped {skippedCount} duplicates.");
                CopyHintService.Instance.Show(
                    string.Format(L["Playlist_AddedWithDuplicatesSkipped"], 
                        addedCount, skippedCount),
                    CopyHintKind.Success);
            }
            else
            {
                Log.Info($"[Queue] Added all {addedCount} tracks from '{playlistName}' to queue.");
                CopyHintService.Instance.Show(
                    string.Format(L["Playlist_AddedToQueue"], addedCount),
                    CopyHintKind.Success);
            }
        }
        else
        {
            Log.Debug($"[Queue] All tracks from '{playlistName}' are already present in the queue.");
            CopyHintService.Instance.Show(
                L["Playlist_AllAlreadyInQueue"],
                CopyHintKind.Warning);
        }
    }
}