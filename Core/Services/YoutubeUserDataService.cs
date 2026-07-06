using LMP.Core.Youtube.Playlists;
using LMP.Core.Helpers.Extensions;

namespace LMP.Core.Services;

/// <summary>
/// Сервис для работы с пользовательскими данными на YouTube Music.
/// Координирует сетевые запросы к InnerTube API и управляет локальным кэшированием сессий.
/// </summary>
public partial class YoutubeUserDataService
{
    private readonly Lazy<YoutubeProvider> _youtubeLazy;
    private readonly CookieAuthService _auth;

    private YoutubeProvider Provider => _youtubeLazy.Value;

    /// <summary>
    /// Инициализирует новый экземпляр службы с ленивым разрешением зависимостей для предотвращения циклических связей при DI.
    /// </summary>
    /// <param name="youtubeLazy">Ленивый инициализатор провайдера YouTube.</param>
    /// <param name="auth">Служба управления аутентификацией и куками.</param>
    public YoutubeUserDataService(
        Lazy<YoutubeProvider> youtubeLazy,
        CookieAuthService auth)
    {
        _youtubeLazy = youtubeLazy;
        _auth = auth;
    }

    #region Лайки YouTube

    /// <summary>
    /// Загружает список понравившихся треков пользователя в зависимости от текущего режима синхронизации библиотеки.
    /// </summary>
    /// <remarks>
    /// <para><b>Режим MusicOnly:</b> Выполняет один точечный запрос VLLM через WEB_REMIX API YouTube Music.
    /// Ранее выполнялась дополнительная избыточная выгрузка всего плейлиста "LL" (все лайки YouTube),
    /// генерировавшая более 10 сетевых запросов. Это поведение убрано — API YouTube Music является 
    /// самодостаточным источником музыкального контента.</para>
    /// <para><b>Режим AllVideos:</b> Выкачивает полный плейлист "LL" со всеми видео, включая немузыкальный контент.</para>
    /// </remarks>
    /// <param name="mode">Режим синхронизации лайков (только музыка или все видео).</param>
    /// <returns>Список моделей треков <see cref="TrackInfo"/>, отмеченных лайком на YouTube.</returns>
    public async Task<List<TrackInfo>> GetLikedTracksAsync(
        LikeSyncMode mode = LikeSyncMode.MusicOnly)
    {
        if (!_auth.IsAuthenticated) return [];

        try
        {
            List<TrackInfo> likedTracks;

            switch (mode)
            {
                case LikeSyncMode.MusicOnly:
                    Log.Info("[Sync] Fetching Music Likes (LM) from YouTube Music...");
                    try
                    {
                        likedTracks = await Provider.GetClient().Music.GetLikedTracksAsync();
                        Log.Info($"[Sync] Got {likedTracks.Count} music likes from LM.");
                    }
                    catch (Exception ex)
                    {
                        Log.Warn($"[Sync] Music API failed, falling back to LL: {ex.Message}");
                        var allLikes = await GetAllLikedVideosAsync();
                        likedTracks = allLikes.FindAll(t => t.IsMusic);
                    }
                    break;

                case LikeSyncMode.AllVideos:
                    Log.Info("[Sync] Fetching ALL Liked Videos (LL)...");
                    likedTracks = await GetAllLikedVideosAsync();
                    break;

                case LikeSyncMode.LocalOnly:
                    Log.Info("[Sync] LocalOnly mode - no cloud sync.");
                    return [];

                default:
                    return [];
            }

            for (int i = 0; i < likedTracks.Count; i++)
            {
                likedTracks[i].IsLiked = true;
            }

            Log.Info($"[Sync] Total liked tracks: {likedTracks.Count}");
            return likedTracks;
        }
        catch (Exception ex)
        {
            Log.Error($"[Sync] Failed to fetch liked tracks: {ex.Message}");
            return [];
        }
    }

    /// <summary>
    /// Выполняет постраничную выгрузку всех понравившихся видео (плейлист "LL") до лимита в 1000 элементов.
    /// </summary>
    private async Task<List<TrackInfo>> GetAllLikedVideosAsync()
    {
        return await Provider.GetClient().Playlists
            .GetVideosAsync(new PlaylistId("LL"))
            .TakeAsync(1000)
            .ToListAsync();
    }

    #endregion

    #region Оценки

    /// <summary>
    /// Устанавливает оценку видео на YouTube (лайк / дизлайк).
    /// </summary>
    /// <param name="videoId">Идентификатор видео на YouTube.</param>
    /// <param name="rating">Строковое представление оценки ("like" или "dislike").</param>
    public async Task RateVideoAsync(string videoId, string rating)
    {
        await Provider.LikeTrackAsync(videoId, rating == "like");
    }

    #endregion

    #region Операции с плейлистами

    /// <param name="title">Название плейлиста.</param>
    /// <returns>YouTube-идентификатор созданного плейлиста.</returns>
    /// <exception cref="InvalidOperationException">Выбрасывается, если YouTube API вернул пустой результат.</exception>
    public async Task<string> CreatePlaylistAsync(string title)
    {
        return await Provider.CreatePlaylistAsync(title)
            ?? throw new InvalidOperationException("YouTube API returned null playlist ID.");
    }

    /// <summary>
    /// Удаляет облачный плейлист с аккаунта YouTube Music.
    /// </summary>
    /// <param name="youtubePlaylistId">YouTube-идентификатор плейлиста.</param>
    public async Task DeletePlaylistAsync(string youtubePlaylistId)
    {
        await Provider.DeletePlaylistAsync(youtubePlaylistId);
    }

    /// <summary>
    /// Добавляет один трек в облачный плейлист на YouTube.
    /// </summary>
    /// <param name="youtubePlaylistId">YouTube-идентификатор целевого плейлиста.</param>
    /// <param name="videoId">YouTube-идентификатор трека.</param>
    public async Task AddTrackToPlaylistAsync(string youtubePlaylistId, string videoId)
    {
        await Provider.AddToPlaylistAsync(youtubePlaylistId, videoId);
    }

    #endregion

    #region Получение профилей и каналов

    /// <summary>
    /// Возвращает список всех каналов (бренд-аккаунтов), привязанных к текущим кукам.
    /// Использует данные, автоматически собранные при стартовой валидации сессии.
    /// </summary>
    public async Task<List<YoutubeAccountItem>> GetAvailableAccountsAsync()
    {
        if (!_auth.IsAuthenticated) return [];

        // Если кэш уже заполнен валидатором (обычно на старте) — возвращаем мгновенно
        if (_auth.State.CachedAccounts != null && _auth.State.CachedAccounts.Count > 0)
        {
            return _auth.State.CachedAccounts;
        }

        // Если кэш пуст, принудительно запрашиваем меню через валидатор
        var (isValid, error, _) = await _auth.ValidateSessionAsync();
        if (!isValid)
        {
            Log.Warn($"[UserDataService] Session validation failed during account fetch: {error}");
            return [];
        }

        return _auth.State.CachedAccounts ?? [];
    }

    /// <summary>
    /// Возвращает базовые метаданные текущего авторизованного пользователя (имя, почту и аватар).
    /// </summary>
    public async Task<(string Name, string Email, string AvatarUrl, string ActiveGaiaId)> GetAccountInfoAsync()
    {
        if (!_auth.IsAuthenticated) return ("Guest", "", "", "");

        // Принудительное обновление кэша профиля через валидацию сессии
        await _auth.ValidateSessionAsync();

        return (_auth.State.UserName, _auth.State.UserEmail, _auth.State.AvatarUrl, _auth.State.ActiveGaiaId);
    }

    #endregion

    #region Библиотека YouTube

    /// <summary>
    /// Возвращает список всех плейлистов в облачной библиотеке пользователя на YouTube Music.
    /// </summary>
    public async Task<List<Playlist>> GetMyPlaylistsAsync()
    {
        if (!_auth.IsAuthenticated) return [];

        try
        {
            return await Provider.GetClient().Music.GetLibraryPlaylistsAsync();
        }
        catch (Exception ex)
        {
            Log.Error($"[Sync] Failed to get user playlists: {ex.Message}");
            return [];
        }
    }

    #endregion
}