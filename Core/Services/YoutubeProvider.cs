using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using LMP.Core.Youtube;
using LMP.Core.Youtube.Channels;
using LMP.Core.Youtube.Music;
using LMP.Core.Youtube.Playlists;
using LMP.Core.Youtube.Search;
using LMP.Core.Youtube.Videos;
using LMP.Core.Youtube.Videos.Streams;
using LMP.Core.Youtube.Utils;
using LMP.Core.Helpers.Extensions;
using ReactiveUI;
using LMP.Core.Youtube.Exceptions;
using LMP.Core.Youtube.Bridge.NToken;
using LMP.Core.Youtube.Bridge.SigCipher;
using LMP.Core.Youtube.Bridge.PoToken;
using LMP.Core.Audio.Http;
using LMP.Core.Audio.Interfaces;
using LMP.Core.Audio.Cache;
using LMP.Core.Youtube.Videos.ClosedCaptions;
using System.Text.Json;

namespace LMP.Core.Services;

/// <summary>
/// Центральный провайдер интеграции с YouTube и YouTube Music.
/// Координирует работу с API, расшифровку токенов, поиск, плейлисты и управление сессионным кэшем потоков.
/// </summary>
public partial class YoutubeProvider : IDisposable
{
    #region Fields & Dependencies

    private readonly NTokenDecryptor _nTokenDecryptor;
    private readonly SigCipherDecryptor _sigCipherDecryptor;
    private readonly TrackRegistry _trackRegistry;
    private readonly LibraryService? _libraryService;
    private readonly YoutubeUserDataService _userDataService;
    private PoTokenProvider? _poTokenProvider;

    /// <summary>
    /// Глобальный сервис авторизации через сессионные куки.
    /// </summary>
    public readonly CookieAuthService AuthService = null!;

    /// <summary>
    /// RAM-кэш полных манифестов. Ключ = trackId (с yt_ префиксом).
    /// Дублирует дисковый SessionCacheStore для instant-access в текущей сессии.
    /// </summary>
    private readonly ConcurrentDictionary<string, StreamManifest> _manifestRamCache = new(StringComparer.Ordinal);

    private YoutubeClient _youtube = null!;
    private SocketsHttpHandler? _currentHandler;
    private HttpClient? _currentHttpClient;
    private volatile bool _disposed;

    private Task? _initTask;
    private readonly Lock _initLock = new();

    private static readonly Regex YoutubeVideoRegex = _YoutubeVideoRegex();
    private static readonly Regex YoutubePlaylistRegex = _YoutubePlaylistRegex();
    private static readonly Regex ValidYoutubeId = _ValidYoutubeId();

    #endregion

    #region Properties & Events

    /// <summary>
    /// Указывает, завершена ли фоновая инициализация провайдера.
    /// </summary>
    public bool IsReady { get; private set; }

    /// <summary>
    /// Событие, вызываемое при старте ресурсоёмкой расшифровки n-токена.
    /// Передаёт идентификатор видео в UI для отображения анимации прогресса.
    /// </summary>
    public event Action<string>? OnNTokenDecryptionStarted;

    #endregion

    #region Constructor

    /// <summary>
    /// Создаёт экземпляр провайдера YouTube.
    /// </summary>
    public YoutubeProvider(
        TrackRegistry trackRegistry,
        LibraryService? libraryService,
        CookieAuthService cookieAuth,
        NTokenDecryptor nTokenDecryptor,
        SigCipherDecryptor sigCipherDecryptor,
        YoutubeUserDataService userDataService)
    {
        _trackRegistry = trackRegistry;
        _libraryService = libraryService;
        AuthService = cookieAuth;
        _nTokenDecryptor = nTokenDecryptor;
        _sigCipherDecryptor = sigCipherDecryptor;
        _userDataService = userDataService;
        _poTokenProvider = new PoTokenProvider(SharedHttpClient.Instance);

        // Связываем централизованный утилитный класс с куками сессии
        YoutubeClientUtils.Initialize(cookieAuth);

        _nTokenDecryptor.OnComplexDecryptionStarted += HandleNTokenDecryptionStarted;

        if (AuthService != null)
        {
            ReloadClient();
            AuthService.OnAuthStateChanged += ReloadClient;
        }
    }

    private void HandleNTokenDecryptionStarted(string? rawVideoId)
    {
        if (string.IsNullOrWhiteSpace(rawVideoId))
            return;

        OnNTokenDecryptionStarted?.Invoke(rawVideoId);
    }

    #endregion

    #region Bot Detection — Stateless Helpers

    /// <summary>
    /// Проверяет, доступен ли трек для оффлайн-воспроизведения без обращения к сети.
    /// </summary>
    public static bool CanPlayOffline(TrackInfo track)
    {
        if (string.IsNullOrEmpty(track.Id))
            return false;

        var rawId = track.GetRawIdSpan().ToString();
        if (string.IsNullOrEmpty(rawId))
            return false;

        var cached = AudioSourceFactory.FindAnyCachedTrack(rawId);
        return cached != null;
    }

    /// <summary>
    /// Проверяет, можно ли выполнять сетевые операции на YouTube без риска блокировки.
    /// </summary>
    public static bool CanPerformNetworkOperation() => !VideoController.IsInCooldown;

    /// <summary>
    /// Выбрасывает исключение, если система находится в режиме ожидания из-за детекции автоматизированных запросов.
    /// </summary>
    public static void ThrowIfInCooldown() => VideoController.ThrowIfInCooldown();

    #endregion

    #region Client Initialization

    /// <summary>
    /// Пересоздаёт HTTP-клиент и внутренний <see cref="YoutubeClient"/> с учётом
    /// текущих кук авторизации и настроек прокси из <see cref="LibraryService"/>.
    /// Вызывается при смене аккаунта, смене сетевого интерфейса (VPN) и изменении прокси.
    /// </summary>
    public void ReloadClient()
    {
        DisposeCurrentClient();

        // VisitorData привязан к сессии — при смене клиента он невалиден.
        _poTokenProvider?.Invalidate();

        var proxy = _libraryService?.Settings.Proxy;

        _currentHandler = new SocketsHttpHandler
        {
            UseCookies = false,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
            AllowAutoRedirect = false,

            // HTTP/2 ping (KeepAlivePingPolicy) работает на application level,
            // но TCP keepalive детектирует мёртвый туннель на уровне ОС —
            // независимо от версии HTTP протокола.
            ConnectCallback = SharedHttpClient.ConnectWithKeepAliveAsync,

            // 2 минуты вместо 5: VPN туннели живут 60-120с при переподключении.
            // 5 минут гарантировало накопление зомби-соединений.
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),

            PooledConnectionIdleTimeout = TimeSpan.FromSeconds(15),
            MaxConnectionsPerServer = 20,
            EnableMultipleHttp2Connections = true,

            // HTTP/2 application-level ping — дополнительный слой поверх TCP keepalive
            KeepAlivePingPolicy = HttpKeepAlivePingPolicy.Always,
            KeepAlivePingDelay = TimeSpan.FromSeconds(15),
            KeepAlivePingTimeout = TimeSpan.FromSeconds(5),
            ConnectTimeout = TimeSpan.FromSeconds(10),
        };

        if (proxy?.Enabled == true && !string.IsNullOrWhiteSpace(proxy.Host))
        {
            var webProxy = new WebProxy($"http://{proxy.Host}:{proxy.Port}");

            if (proxy.UseAuth && !string.IsNullOrWhiteSpace(proxy.Username))
                webProxy.Credentials = new NetworkCredential(proxy.Username, proxy.Password);

            _currentHandler.Proxy = webProxy;
            _currentHandler.UseProxy = true;
        }

        var baseHttpClient = new HttpClient(_currentHandler, disposeHandler: false)
        {
            Timeout = TimeSpan.FromSeconds(30)
        };

        var youtubeHandler = new YoutubeHttpHandler(baseHttpClient, AuthService, disposeClient: true);

        _currentHttpClient = new HttpClient(youtubeHandler, disposeHandler: true)
        {
            Timeout = TimeSpan.FromSeconds(30)
        };

        _youtube = new YoutubeClient(
            _currentHttpClient,
            _nTokenDecryptor,
            _sigCipherDecryptor,
            isAuthenticatedCheck: () => AuthService?.IsAuthenticated ?? false,
            ownsHttpClient: false,
            poTokenProvider: _poTokenProvider);

        Log.Info($"[YouTube] Client reloaded. Auth: {AuthService?.IsAuthenticated ?? false}, " +
                $"Proxy: {(proxy?.Enabled == true ? $"{proxy.Host}:{proxy.Port}" : "none")}");
    }

    private void DisposeCurrentClient()
    {
        try
        {
            _currentHttpClient?.Dispose();
            _currentHttpClient = null;

            _youtube = null!;
        }
        catch (Exception ex)
        {
            Log.Warn($"[YouTube] Error disposing client: {ex.Message}");
        }
    }

    /// <summary>
    /// Выполняет асинхронную инициализацию провайдера. Потокобезопасно.
    /// </summary>
    public Task InitializeAsync()
    {
        lock (_initLock)
        {
            _initTask ??= InitializeInternalAsync();
            return _initTask;
        }
    }

    private async Task InitializeInternalAsync()
    {
        try
        {
            Log.Info("[YouTube] Initiating lazy background Visitor Data resolution...");

            // Получаем VisitorData, учитывая куки и защищая таймаутом
            _ = await YoutubeClientUtils.EnsureVisitorDataAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Warn($"[YouTube] Initialization non-fatal warning: {ex.Message}");
        }
        finally
        {
            IsReady = true;
            Log.Info("[YouTube] Initialized");
        }
    }

    /// <summary>
    /// Возвращает сконфигурированный экземпляр клиента YouTubeClient.
    /// </summary>
    public YoutubeClient GetClient() =>
        _youtube ?? throw new InvalidOperationException("YouTube client not initialized");

    #endregion

    #region Personalization

    /// <summary>
    /// Загружает персонализированную домашнюю страницу YouTube Music для авторизованного пользователя.
    /// </summary>
    public async Task<List<HomeSection>> GetPersonalizedHomeAsync(CancellationToken ct = default)
    {
        if (AuthService?.IsAuthenticated != true) return [];

        ThrowIfInCooldown();

        try
        {
            var shelves = await _youtube.Music.GetPersonalizedHomeAsync(ct);
            var sections = new List<HomeSection>(shelves.Count);

            foreach (var shelf in shelves)
            {
                if (VideoController.IsInCooldown)
                {
                    Log.Warn("[YouTube] Home loading interrupted by bot detection");
                    break;
                }

                var section = new HomeSection { Title = shelf.Title };

                for (int i = 0; i < shelf.Items.Count; i++)
                {
                    var item = shelf.Items[i];

                    string thumbUrl = ThumbnailUtils.GetBestUrlOrDefault(item.Thumbnails, $"https://i.ytimg.com/vi/{item.Id}/mqdefault.jpg");

                    bool isMusicContent = string.Equals(item.Type, "Song", StringComparison.OrdinalIgnoreCase);

                    var track = new TrackInfo
                    {
                        Id = item.Id,
                        Title = item.Title,
                        Author = item.Author ?? "Unknown",
                        ThumbnailUrl = thumbUrl,
                        Duration = item.Duration ?? TimeSpan.Zero,
                        IsMusic = isMusicContent,
                        Url = $"https://music.youtube.com/watch?v={item.Id}"
                    };

                    if (item.Type is "Playlist" or "Album")
                        track.Id = $"yt_pl_{item.Id}";
                    else
                        track = _trackRegistry.RegisterOrUpdate(track);

                    section.Tracks.Add(track);
                }

                if (section.Tracks.Count > 0)
                    sections.Add(section);
            }

            return sections;
        }
        catch (BotDetectionException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Error($"[Music] Failed to get home: {ex.Message}");
            throw;
        }
    }

    #endregion

    #region Edit YouTube Data

    /// <summary>
    /// Устанавливает статус "Мне нравится" (Like) для трека на YouTube Music.
    /// </summary>
    public async Task LikeTrackAsync(string trackId, bool like)
    {
        if (AuthService?.IsAuthenticated != true) return;
        try
        {
            var rawId = YoutubeIdHelper.ExtractRawId(trackId);
            await _youtube.Music.LikeTrackAsync(rawId, like);
            Log.Info($"[Music] Like={like} for {rawId}");
        }
        catch (Exception ex)
        {
            Log.Error($"[Music] Failed to like: {ex.Message}");
            throw;
        }
    }

    /// <summary>   
    /// Создаёт новый облачный плейлист в аккаунте YouTube.
    /// </summary>
    public async Task<string?> CreatePlaylistAsync(
        string title,
        IReadOnlyList<string>? videoIds = null)
    {
        if (AuthService?.IsAuthenticated != true)
            throw new InvalidOperationException("Cannot create cloud playlist: user is not authenticated.");

        ThrowIfInCooldown();

        try
        {
            var ytId = await _youtube.Mutations.CreatePlaylistAsync(title, videoIds);
            Log.Info($"[Music] Created playlist '{title}' → YT ID: {ytId}");
            return ytId;
        }
        catch (BotDetectionException) { throw; }
        catch (Exception ex)
        {
            Log.Error($"[Music] Failed to create playlist '{title}': {ex.Message}");
            throw;
        }
    }

    /// <summary>
    /// Добавляет один трек в существующий облачный плейлист.
    /// </summary>
    public async Task<string?> AddToPlaylistAsync(string playlistId, string trackId)
    {
        if (AuthService?.IsAuthenticated != true) return null;
        try
        {
            var rawId = YoutubeIdHelper.ExtractRawIdSpan(trackId).ToString();
            var setVideoIds = await _youtube.Mutations.AddTracksAsync(playlistId, [rawId]);
            var result = setVideoIds.Count > 0 ? setVideoIds[0] : null;
            if (!string.IsNullOrEmpty(result))
                Log.Debug($"[Music] Added {rawId} to {playlistId}, setVideoId={result}");
            return result;
        }
        catch (Exception ex)
        {
            Log.Error($"[Music] Failed to add to playlist: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Выполняет пакетное добавление треков в облачный плейлист.
    /// </summary>
    public async Task<List<string?>> AddTracksToPlaylistAsync(
        string playlistId, IReadOnlyList<string> trackIds)
    {
        if (AuthService?.IsAuthenticated != true) return [];

        ThrowIfInCooldown();

        try
        {
            var rawIds = new List<string>(trackIds.Count);
            for (int i = 0; i < trackIds.Count; i++)
                rawIds.Add(YoutubeIdHelper.ExtractRawId(trackIds[i]));

            var result = await _youtube.Mutations.AddTracksAsync(playlistId, rawIds);
            Log.Info($"[Music] Batch added {trackIds.Count} tracks to {playlistId}");
            return result;
        }
        catch (BotDetectionException) { throw; }
        catch (Exception ex)
        {
            Log.Error($"[Music] Failed to batch add to playlist: {ex.Message}");
            return [];
        }
    }

    /// <summary>
    /// Удаляет один элемент из облачного плейлиста по его внутреннему setVideoId.
    /// </summary>
    public async Task RemoveFromPlaylistAsync(string playlistId, string setVideoId)
    {
        if (AuthService?.IsAuthenticated != true) return;

        ThrowIfInCooldown();

        try
        {
            await _youtube.Mutations.RemoveTracksAsync(playlistId, [setVideoId]);
            Log.Info($"[Music] Removed item {setVideoId} from playlist {playlistId}");
        }
        catch (BotDetectionException) { throw; }
        catch (Exception ex)
        {
            Log.Error($"[Music] Failed to remove from playlist: {ex.Message}");
            throw;
        }
    }

    /// <summary>
    /// Выполняет пакетное удаление элементов из облачного плейлиста.
    /// </summary>
    public async Task RemoveTracksFromPlaylistAsync(
        string playlistId, IReadOnlyList<string> setVideoIds)
    {
        if (AuthService?.IsAuthenticated != true) return;

        ThrowIfInCooldown();

        try
        {
            await _youtube.Mutations.RemoveTracksAsync(playlistId, setVideoIds);
            Log.Info($"[Music] Batch removed {setVideoIds.Count} tracks from {playlistId}");
        }
        catch (BotDetectionException) { throw; }
        catch (Exception ex)
        {
            Log.Error($"[Music] Failed to batch remove from playlist: {ex.Message}");
            throw;
        }
    }

    /// <summary>
    /// Редактирует текстовое описание облачного плейлиста.
    /// </summary>
    public async Task EditPlaylistDescriptionAsync(string playlistId, string description)
    {
        if (string.IsNullOrWhiteSpace(playlistId))
            throw new ArgumentException("Playlist ID cannot be empty.", nameof(playlistId));

        if (AuthService?.IsAuthenticated != true)
            throw new InvalidOperationException("Cannot edit cloud playlist: user is not authenticated.");

        ThrowIfInCooldown();

        try
        {
            await _youtube.Mutations.SetPlaylistDescriptionAsync(playlistId, description);
            Log.Info($"[Music] Updated description for playlist {playlistId}");
        }
        catch (BotDetectionException) { throw; }
        catch (Exception ex)
        {
            Log.Error($"[Music] Failed to update playlist description {playlistId}: {ex.Message}");
            throw;
        }
    }

    /// <summary>
    /// Загружает пользовательскую обложку (изображение) для облачного плейлиста.
    /// </summary>
    public async Task<bool> UploadPlaylistThumbnailAsync(
        string playlistId,
        byte[] imageData)
    {
        if (string.IsNullOrWhiteSpace(playlistId))
            throw new ArgumentException("Playlist ID cannot be empty.", nameof(playlistId));

        if (AuthService?.IsAuthenticated != true)
            throw new InvalidOperationException("Cannot upload thumbnail: user is not authenticated.");

        ThrowIfInCooldown();

        try
        {
            var result = await _youtube.Mutations.UploadPlaylistThumbnailAsync(
                playlistId, imageData);

            if (result)
            {
                Log.Info($"[Music] Thumbnail uploaded for playlist {playlistId}");
            }

            return result;
        }
        catch (BotDetectionException) { throw; }
        catch (Exception ex)
        {
            Log.Error($"[Music] Failed to upload thumbnail for playlist {playlistId}: {ex.Message}");
            throw;
        }
    }

    /// <summary>
    /// Изменяет название облачного плейлиста.
    /// </summary>
    public async Task RenamePlaylistAsync(string playlistId, string newTitle)
    {
        if (string.IsNullOrWhiteSpace(playlistId))
            throw new ArgumentException("Playlist ID cannot be empty.", nameof(playlistId));

        if (AuthService?.IsAuthenticated != true)
            throw new InvalidOperationException("Cannot rename cloud playlist: user is not authenticated.");

        ThrowIfInCooldown();

        try
        {
            await _youtube.Mutations.RenamePlaylistAsync(playlistId, newTitle);
            Log.Info($"[Music] Renamed playlist {playlistId} → '{newTitle}'");
        }
        catch (BotDetectionException) { throw; }
        catch (Exception ex)
        {
            Log.Error($"[Music] Failed to rename playlist {playlistId}: {ex.Message}");
            throw;
        }
    }

    /// <summary>
    /// Удаляет облачный плейлист из аккаунта YouTube.
    /// </summary>
    public async Task DeletePlaylistAsync(string playlistId)
    {
        if (string.IsNullOrWhiteSpace(playlistId))
            throw new ArgumentException("Playlist ID cannot be empty.", nameof(playlistId));

        if (AuthService?.IsAuthenticated != true)
            throw new InvalidOperationException("Cannot delete cloud playlist: user is not authenticated.");

        ThrowIfInCooldown();

        try
        {
            await _youtube.Mutations.DeletePlaylistAsync(playlistId);
            Log.Info($"[Music] Deleted playlist {playlistId} from YouTube");
        }
        catch (BotDetectionException) { throw; }
        catch (Exception ex)
        {
            Log.Error($"[Music] Failed to delete playlist {playlistId}: {ex.Message}");
            throw;
        }
    }

    /// <summary>
    /// Извлекает полные синхронизационные данные плейлиста YouTube, включая треки, автора и приватность.
    /// </summary>
    public async Task<FullPlaylistSyncData?> GetFullPlaylistDataAsync(
        string youtubePlaylistId,
        CancellationToken ct = default)
    {
        if (AuthService?.IsAuthenticated != true) return null;

        ThrowIfInCooldown();

        try
        {
            var data = await _youtube.Sync.GetFullPlaylistDataAsync(youtubePlaylistId, ct);

            Log.Info($"[Music] Fetched full playlist data: " +
                     $"'{data.Title}', {data.Tracks.Count} tracks for {youtubePlaylistId}");

            return data;
        }
        catch (BotDetectionException) { throw; }
        catch (Exception ex)
        {
            Log.Error($"[Music] Failed to fetch full playlist data: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Выполняет аварийный сброс и очистку кэшей обходных движков при обнаружении фатальной ошибки 403 Forbidden.
    /// Предотвращает циклические блокировки при ротации шифров YouTube.
    /// </summary>
    [MethodImpl(MethodImplOptions.Synchronized)]
    public void HandlePlayback403Fatal()
    {
        Log.Warn("[YouTube] Fatal 403 Forbidden detected. Triggering self-healing bypass reset...");
        try
        {
            _nTokenDecryptor.InvalidateCache();
            _sigCipherDecryptor.InvalidateCache();
            _nTokenDecryptor.PlayerManager.InvalidateContext();
            _youtube.Videos.Streams.InvalidateCipherManifest();
            _poTokenProvider?.Invalidate();
            _ = YoutubeClientUtils.EnsureVisitorDataAsync(forceRefresh: true);
            ClearCache();
        }
        catch (Exception ex)
        {
            Log.Error($"[YouTube] Self-healing reset failed: {ex.Message}");
        }
    }

    #endregion

    #region Resolve Stream (3-Tier Cache Strategy)

    /// <summary>
    /// Разрешает stream URL для трека через YouTube API.
    /// <para><b>Высокоэффективный 3-уровневый алгоритм:</b></para>
    /// <list type="number">
    ///   <item>Exact full disk cache: Мгновенно возвращает локальный файл, если совпал запрошенный формат.</item>
    ///   <item>Any full disk cache: Возвращает любой локальный файл, если пользователь не запрашивал конкретный формат.</item>
    ///   <item>RAM manifest cache: Извлекает дескриптор из полной копии манифеста в оперативной памяти (0 сети).</item>
    ///   <item>Disk manifest cache: Делает адаптивный HEAD-запрос к CDN. При 200/206 — восстанавливает весь манифест без обращения к YouTube API.</item>
    ///   <item>YouTube API call: Запрашивает полный манифест со всеми медиа-вариантами, сохраняя их в RAM и на диск (1 API на все форматы).</item>
    /// </list>
    /// </summary>
    public async Task<ResolvedStreamDescriptor?> RefreshStreamAsync(
        TrackInfo track,
        bool forceRefresh = false,
        CancellationToken ct = default)
    {
        var rawVideoId = track.GetRawIdSpan().ToString();
        if (string.IsNullOrEmpty(rawVideoId)) return null;

        var requested = StreamSelectionHint.FromTrack(track, _libraryService?.Settings.RememberTrackFormat == true);

        Log.Info($"[YouTube] RefreshStreamAsync start: track={track.Id}, force={forceRefresh}, target={requested.Format?.ToContainerName() ?? "-"}/{requested.BitrateKbps}");

        if (forceRefresh)
        {
            PerformForceRefreshReset(rawVideoId, track.Id);
        }

        if (!forceRefresh && requested.HasFormat && requested.HasBitrate)
        {
            string cacheKey = AudioSourceFactory.BuildCacheKey(track.Id, requested.Format!.Value, requested.BitrateKbps);
            var cache = AudioSourceFactory.GlobalCache;
            if (cache != null && cache.IsFullyCached(cacheKey))
            {
                var entry = cache.GetCacheInfo(cacheKey);
                // Sanity check: отсекаем огрызки, размер которых не соответствует длительности
                if (entry != null && (track.Duration <= TimeSpan.FromSeconds(5) || entry.TotalSize >= (long)(track.Duration.TotalSeconds * entry.Bitrate * 1000.0 / 8.0 * 0.50)))
                {
                    var d = CreateDescriptorFromCacheEntry(track.Id, entry);
                    Log.Info($"[YouTube] RefreshStreamAsync FULL CACHE (exact) -> {d}");
                    return d;
                }
            }
        }

        if (!forceRefresh && !requested.HasFormat)
        {
            var anyCache = AudioSourceFactory.FindAnyCachedTrack(rawVideoId);
            if (anyCache != null && (track.Duration <= TimeSpan.FromSeconds(5) || anyCache.Value.Entry.TotalSize >= (long)(track.Duration.TotalSeconds * anyCache.Value.Entry.Bitrate * 1000.0 / 8.0 * 0.50)))
            {
                var d = CreateDescriptorFromCacheEntry(track.Id, anyCache.Value.Entry);
                Log.Info($"[YouTube] RefreshStreamAsync FULL CACHE (any) -> {d}");
                return d;
            }
        }

        if (!forceRefresh && _manifestRamCache.TryGetValue(track.Id, out var ramManifest))
        {
            var d = SelectFromManifest(ramManifest, track.Id, requested.Format, requested.BitrateKbps);
            if (d != null)
            {
                Log.Info($"[YouTube] RefreshStreamAsync RAM MANIFEST -> {d}");
                return d;
            }
        }

        if (!forceRefresh)
        {
            var diskEntry = await SessionCacheStore
                .TryGetManifestAndProbeAsync(track.Id, Audio.Http.SharedHttpClient.Instance, ct)
                .ConfigureAwait(false);

            if (diskEntry is { Variants.Count: > 0 })
            {
                var diskManifest = ReconstructManifest(diskEntry);
                _manifestRamCache[track.Id] = diskManifest;

                var d = SelectFromManifest(diskManifest, track.Id, requested.Format, requested.BitrateKbps);
                if (d != null)
                {
                    Log.Info($"[YouTube] RefreshStreamAsync DISK MANIFEST ({diskEntry.Variants.Count} variants) -> {d}");
                    return d;
                }
            }
        }

        ThrowIfInCooldown();

        try
        {
            var vId = VideoId.Parse(rawVideoId);
            var manifest = await _youtube.Videos.Streams
                .GetManifestAsync(vId, ct)
                .ConfigureAwait(false);

            var audioStreams = manifest.GetAudioOnlyStreams()
                .OrderByDescending(s => s.Bitrate)
                .ToList();

            if (audioStreams.Count == 0)
                throw new StreamUnavailableException($"No audio streams for {rawVideoId}", rawVideoId, StreamUnavailableReason.AllClientsFailed);

            _manifestRamCache[track.Id] = manifest;
            SessionCacheStore.RecordManifest(track.Id, audioStreams, manifest.IntegratedLufs);

            var descriptor = SelectFromManifest(manifest, track.Id, requested.Format, requested.BitrateKbps);
            if (descriptor == null)
                throw new StreamUnavailableException($"No matching stream for {rawVideoId}", rawVideoId, StreamUnavailableReason.AllClientsFailed);

            Log.Info($"[YouTube] RefreshStreamAsync YOUTUBE API ({audioStreams.Count} variants) -> {descriptor}");
            return descriptor;
        }
        catch (OperationCanceledException) { throw; }
        catch (StreamUnavailableException) { throw; }
        catch (BotDetectionException) { throw; }
        catch (LoginRequiredException) { throw; }
        catch (Exception ex)
        {
            Log.Error($"[YouTube] RefreshStreamAsync error: {ex.Message}");
            throw;
        }
    }

    #endregion

    #region Quality Options (Menu UI Support)

    /// <summary>
    /// Возвращает список доступных форматов стрима для указанного видео с поддержкой отмены операции и принудительного обновления.
    /// Предотвращает лишние сетевые вызовы за счёт чтения скачанных файлов, сохранённых ранее манифестов и дискового кэша.
    /// </summary>
    /// <param name="videoId">Идентификатор видео или трека.</param>
    /// <param name="ct">Токен отмены операции.</param>
    /// <param name="forceRefresh">Флаг принудительного игнорирования локального кэша и вызова YouTube API.</param>
    /// <returns>Список вариантов аудиопотока.</returns>
    public async Task<List<StreamOption>> GetStreamOptionsAsync(
        string videoId,
        CancellationToken ct = default,
        bool forceRefresh = false)
    {
        if (!IsReady || string.IsNullOrWhiteSpace(videoId)) return [];

        string trackId = videoId.StartsWith("yt_", StringComparison.Ordinal) ? videoId : $"yt_{videoId}";
        string rawId = videoId.StartsWith("yt_", StringComparison.Ordinal) ? videoId[3..] : videoId;

        var track = _trackRegistry.TryGet(trackId) ?? _libraryService?.GetTrack(trackId);
        bool isExplicitDownload = track != null && track.IsDownloaded && !string.IsNullOrEmpty(track.LocalPath) && File.Exists(track.LocalPath);

        StreamManifest? manifest = null;

        if (!forceRefresh)
        {
            // 1. СНАЧАЛА проверяем RAM Cache (0 мс, 0 сети). Если манифест уже загружен кнопкой "Обновить" — берём его!
            if (_manifestRamCache.TryGetValue(trackId, out var ramManifest))
            {
                manifest = ramManifest;
            }
            // 2. Затем Disk Session Cache (0 мс, 0 сети)
            else
            {
                var diskEntry = SessionCacheStore.GetManifest(trackId);
                if (diskEntry is { Variants.Count: > 0 })
                {
                    manifest = ReconstructManifest(diskEntry);
                    _manifestRamCache[trackId] = manifest;
                }
            }

            // Манифест найден в кэше — возвращаем ВСЕ форматы!
            if (manifest != null)
            {
                var options = MapManifestToOptions(manifest);
                Log.Debug($"[YouTube] StreamOptions CACHE hit: {trackId} ({options.Count} formats)");
                return options;
            }

            // 3. Если манифеста НЕТ, но файл скачан (папка Downloads) — возвращаем 1 формат, чтобы не лезть в сеть
            if (isExplicitDownload)
            {
                var format = track!.PreferredFormat ?? AudioSourceFactory.DetectFormat(track.LocalPath!);
                if (format == AudioFormat.Unknown) format = AudioFormat.WebM;

                var localOption = new StreamOption
                {
                    Format = format,
                    CodecType = AudioSourceFactory.GetCodecForFormat(format),
                    Bitrate = track.PreferredBitrate > 0 ? track.PreferredBitrate : 128,
                    SizeMb = new FileInfo(track.LocalPath!).Length / (1024.0 * 1024.0),
                    IsDownloaded = true,
                    IsActive = true
                };

                Log.Debug($"[YouTube] StreamOptions EXPLICIT DOWNLOAD hit: {trackId}");
                return [localOption];
            }

            // 4. То же самое для Local Audio Disk Cache (скрытый кэш)
            var globalCache = AudioSourceFactory.GlobalCache;
            if (globalCache != null)
            {
                var cachedTrack = globalCache.FindBestCacheByTrackId(trackId)
                    ?? globalCache.FindBestStartupCache(trackId, 0);

                if (cachedTrack != null)
                {
                    var localOption = new StreamOption
                    {
                        Format = cachedTrack.Format,
                        CodecType = cachedTrack.Codec,
                        Bitrate = cachedTrack.Bitrate,
                        SizeMb = cachedTrack.TotalSize / (1024.0 * 1024.0),
                        IsDownloaded = true,
                        IsActive = true
                    };

                    Log.Debug($"[YouTube] StreamOptions LOCAL DISK AUDIO cache hit: {trackId}");
                    return [localOption];
                }
            }
        }

        // 5. YouTube API Call — вызывается при принудительном обновлении или промахе локальных источников
        try
        {
            var vId = VideoId.Parse(rawId);
            manifest = await _youtube.Videos.Streams
                .GetManifestAsync(vId, ct)
                .ConfigureAwait(false);

            var audioStreams = manifest.GetAudioOnlyStreams()
                .OrderByDescending(s => s.Bitrate)
                .ToList();

            if (audioStreams.Count > 0)
            {
                _manifestRamCache[trackId] = manifest;
                SessionCacheStore.RecordManifest(trackId, audioStreams, manifest.IntegratedLufs);
            }

            return MapManifestToOptions(manifest);
        }
        catch (Exception ex)
        {
            Log.Error($"[YouTube] GetStreamOptions error: {ex.Message}");
            throw;
        }
    }

    /// <summary>
    /// Инвалидирует RAM-кэш манифеста для конкретного трека.
    /// Вызывается из <see cref="AudioEngine"/> при 403, чтобы следующий
    /// <c>RefreshStreamAsync(force=false)</c> обратился к YouTube API,
    /// а не вернул тот же мёртвый URL из оперативной памяти.
    /// </summary>
    /// <param name="trackId">Идентификатор трека (с yt_ префиксом или без).</param>
    public void InvalidateMemoryCache(string trackId)
    {
        if (string.IsNullOrEmpty(trackId))
            return;

        // Удаляем по точному ключу (обычно "yt_xxxxx")
        _manifestRamCache.TryRemove(trackId, out _);

        // Страховка: если передан rawId без префикса — пробуем оба варианта.
        var rawId = YoutubeIdHelper.ExtractRawId(trackId);
        if (!string.Equals(rawId, trackId, StringComparison.Ordinal))
        {
            _manifestRamCache.TryRemove($"yt_{rawId}", out _);
            _manifestRamCache.TryRemove(rawId, out _);
        }

        Log.Debug($"[YouTube] RAM manifest cache invalidated for {trackId}");
    }
    #endregion

    #region Support Helpers (Formatting & Fallback)

    private async Task<string?> GetHlsManifestAsync(string videoId, CancellationToken ct)
    {
        try
        {
            var vId = VideoId.Parse(videoId);
            var controller = new VideoController(_currentHttpClient!, _nTokenDecryptor.PlayerManager);
            return await controller.GetHlsManifestUrlAsync(vId, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (BotDetectionException) { throw; }
        catch (StreamUnavailableException) { throw; }
        catch (LoginRequiredException) { throw; }
        catch (Exception ex)
        {
            Log.Error($"[YouTube] [{videoId}] GetHlsManifest failed: {ex.Message}");
            return null;
        }
    }

    private AudioOnlyStreamInfo? SelectBestStream(
         List<AudioOnlyStreamInfo> streams,
         AudioFormat? preferredFormat,
         int preferredBitrate = 0)
    {
        if (streams.Count == 0) return null;

        var blacklist = AudioSourceFactory.CdnBlacklist;

        // Фильтруем заблокированные ТСПУ CDN-хосты, если есть альтернативы
        var candidateStreams = streams.Where(s => !blacklist.IsBlockedUrl(s.Url)).ToList();
        if (candidateStreams.Count == 0)
            candidateStreams = streams; // Fallback на исходные, если заблокированы вообще все

        if (preferredFormat is { } requestedFormat && requestedFormat != AudioFormat.Unknown)
        {
            AudioOnlyStreamInfo? bestMatch = null;
            double bestDelta = double.MaxValue;
            AudioOnlyStreamInfo? firstInFormat = null;

            for (int i = 0; i < candidateStreams.Count; i++)
            {
                var streamFormat = YoutubeIdHelper.MapContainerToFormat(candidateStreams[i].Container.Name);
                if (streamFormat != requestedFormat)
                    continue;

                firstInFormat ??= candidateStreams[i];

                if (preferredBitrate > 0)
                {
                    var delta = Math.Abs(candidateStreams[i].Bitrate.KiloBitsPerSecond - preferredBitrate);
                    if (delta < bestDelta)
                    {
                        bestDelta = delta;
                        bestMatch = candidateStreams[i];
                    }
                }
            }

            if (preferredBitrate > 0 && bestMatch != null) return bestMatch;
            if (firstInFormat != null) return firstInFormat;
        }

        var qualityPref = _libraryService?.Settings.QualityPreference ?? AudioQualityPreference.BestAvailable;

        if (qualityPref == AudioQualityPreference.Standard)
        {
            for (int i = 0; i < candidateStreams.Count; i++)
            {
                if (YoutubeIdHelper.MapContainerToFormat(candidateStreams[i].Container.Name) == AudioFormat.Mp4)
                    return candidateStreams[i];
            }
        }

        return candidateStreams.Count > 0 ? candidateStreams[0] : null;
    }

    private static AudioCodec DetermineCodec(AudioOnlyStreamInfo stream, AudioFormat format)
    {
        return stream.AudioCodec.ToAudioCodec(format);
    }

    #endregion

    #region Search Base

    /// <summary>
    /// Определяет тип переданного поискового запроса (ссылка, плейлист или обычный текст).
    /// </summary>
    public static QueryType DetectQueryType(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return QueryType.None;
        query = query.Trim();

        if (YoutubePlaylistRegex.IsMatch(query)) return QueryType.Playlist;
        if (YoutubeVideoRegex.IsMatch(query) ||
            query.StartsWith(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
            query.StartsWith(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return QueryType.DirectUrl;
        }

        return QueryType.Search;
    }

    /// <summary>
    /// Извлекает 11-значный YouTube ID видео из любого URL-адреса.
    /// </summary>
    public static string? ExtractVideoId(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        var match = YoutubeVideoRegex.Match(url);
        if (match.Success) return match.Groups[1].Value;
        try { return VideoId.TryParse(url)?.Value; } catch { return null; }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsValidYoutubeIdChars(ReadOnlySpan<char> id)
    {
        for (int i = 0; i < id.Length; i++)
        {
            var c = id[i];
            if (!char.IsLetterOrDigit(c) && c != '_' && c != '-')
                return false;
        }
        return true;
    }

    /// <summary>
    /// Загружает подробные метаданные трека по его прямой ссылке.
    /// </summary>
    public async Task<TrackInfo?> GetTrackByUrlAsync(string url)
    {
        if (!IsReady || string.IsNullOrWhiteSpace(url)) return null;
        try
        {
            var videoId = VideoId.TryParse(url) ?? VideoId.Parse(ExtractVideoId(url) ?? "");
            var rawTrack = await _youtube.Videos.GetAsync(videoId);
            return _trackRegistry.RegisterOrUpdate(rawTrack);
        }
        catch (Exception ex)
        {
            NotifyError($"[YouTube] GetTrackByUrlAsync error: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Выполняет стриминговый (потоковый) поиск музыкального контента на YouTube.
    /// </summary>
    public async IAsyncEnumerable<List<TrackInfo>> SearchStreamingAsync(
     string query,
     int maxResults = 300,
     SearchFilter filter = SearchFilter.MusicSong,
     [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (!IsReady || string.IsNullOrWhiteSpace(query))
            yield break;

        try
        {
            ThrowIfInCooldown();
        }
        catch (BotDetectionException ex)
        {
            NotifyError($"[YouTube] Search blocked: wait {ex.RemainingCooldown.TotalSeconds:F0}s");
            yield break;
        }

        var sw = Stopwatch.StartNew();
        int count = 0;
        bool isMusicFilter = filter.IsMusicContext();

        Log.Info($"[YouTube] Streaming search '{query}' (Filter: {filter})...");

        await foreach (var batch in _youtube.Search.GetResultBatchesAsync(query, filter, ct))
        {
            if (ct.IsCancellationRequested) yield break;

            if (VideoController.IsInCooldown)
            {
                Log.Warn("[YouTube] Search interrupted by bot detection cooldown");
                yield break;
            }

            var tracks = new List<TrackInfo>(batch.Items.Count);

            for (int i = 0; i < batch.Items.Count && count < maxResults; i++)
            {
                if (batch.Items[i] is not TrackInfo rawTrack) continue;

                if (isMusicFilter) rawTrack.IsMusic = true;

                var track = _trackRegistry.RegisterOrUpdate(rawTrack);
                tracks.Add(track);
                count++;
            }

            if (tracks.Count > 0)
            {
                Log.Info($"[YouTube] +{tracks.Count} (total: {count}) in {sw.ElapsedMilliseconds}ms");
                yield return tracks;
            }

            if (count >= maxResults) break;
        }

        sw.Stop();
        Log.Info($"[YouTube] Search done: {count} results in {sw.ElapsedMilliseconds}ms");
    }

    /// <summary>
    /// Облегчённый быстрый метод поиска без пошагового стриминга пакетов.
    /// </summary>
    public async Task<List<TrackInfo>> SearchFastAsync(
     string query,
     int maxResults = 100,
     SearchFilter filter = SearchFilter.MusicSong,
     CancellationToken ct = default)
    {
        try
        {
            ThrowIfInCooldown();
        }
        catch (BotDetectionException ex)
        {
            NotifyError($"[YouTube] Search blocked: wait {ex.RemainingCooldown.TotalSeconds:F0}s");
            return [];
        }

        var results = new List<TrackInfo>(maxResults);

        try
        {
            await foreach (var batch in SearchStreamingAsync(query, maxResults, filter, ct))
            {
                results.AddRange(batch);
                if (results.Count >= maxResults) break;
            }
        }
        catch (OperationCanceledException)
        {
            Log.Info($"[YouTube] Search cancelled after {results.Count} results");
        }
        catch (BotDetectionException) { }
        catch (Exception ex)
        {
            NotifyError($"[YouTube] SearchFastAsync error: {ex.Message}");
        }

        return results;
    }

    /// <summary>
    /// Обычный поиск YouTube.
    /// </summary>
    public Task<List<TrackInfo>> SearchAsync(
        string query,
        int maxResults = 100,
        SearchFilter filter = SearchFilter.MusicSong)
    {
        return SearchFastAsync(query, maxResults, filter);
    }

    #endregion

    #region Search Session Nested Type

    /// <summary>
    /// Итеративная сессия постраничного поиска с авто-дедупликацией и неблокирующим освобождением.
    /// Гарантирует отсутствие взаимных блокировок (deadlocks) в UI-потоке.
    /// </summary>
    public sealed class SearchSession : IAsyncDisposable, IDisposable
    {
        private readonly YoutubeClient _youtube;
        private readonly TrackRegistry _registry;
        private readonly string _query;
        private readonly int _maxResults;
        private readonly HashSet<string> _seenIds;
        private readonly CancellationTokenSource _sessionCts = new();

        private IAsyncEnumerator<Batch<ISearchResult>>? _enumerator;
        private bool _hasMore = true;
        private int _isDisposed;
        private int _isFetching;

        private readonly Queue<TrackInfo> _buffer = new();

        /// <summary>
        /// Указывает, есть ли ещё результаты для загрузки.
        /// </summary>
        public bool HasMore => (_hasMore || _buffer.Count > 0) && _isDisposed == 0 && _seenIds.Count < _maxResults;

        /// <summary>
        /// Количество загруженных уникальных треков в текущей сессии.
        /// </summary>
        public int LoadedCount => _seenIds.Count;

        /// <summary>
        /// Применяемый поисковый фильтр.
        /// </summary>
        public SearchFilter Filter { get; }

        internal SearchSession(
            YoutubeClient youtube,
            TrackRegistry registry,
            string query,
            int maxResults = 300,
            SearchFilter filter = SearchFilter.Video,
            IEnumerable<string>? skipTrackIds = null)
        {
            _youtube = youtube;
            _registry = registry;
            _query = query;
            _maxResults = maxResults;
            Filter = filter;
            _seenIds = new HashSet<string>(64, StringComparer.Ordinal);

            if (skipTrackIds != null)
            {
                foreach (var id in skipTrackIds)
                {
                    _seenIds.Add(YoutubeIdHelper.ExtractRawId(id));
                }
            }
        }

        /// <summary>
        /// Получает следующий пакет результатов поиска с контролем сетевой квоты.
        /// Гарантирует отдачу стабильного объема треков за минимальное число сетевых запросов.
        /// </summary>
        public async Task<List<TrackInfo>> FetchNextBatchAsync(int count = 25, CancellationToken ct = default)
        {
            if (_isDisposed != 0 || _seenIds.Count >= _maxResults || !HasMore)
                return [];

            if (Interlocked.CompareExchange(ref _isFetching, 1, 0) != 0)
            {
                Log.Warn("[SearchSession] FetchNextBatchAsync rejected: another fetch is currently active.");
                return [];
            }

            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, _sessionCts.Token);
            var token = linkedCts.Token;

            try
            {
                if (_isDisposed != 0 || _seenIds.Count >= _maxResults)
                    return [];

                var results = new List<TrackInfo>(Math.Min(count, 32));

                // 1. Отдаем остатки из буфера
                while (_buffer.Count > 0 && results.Count < count)
                {
                    results.Add(_buffer.Dequeue());
                }

                if (results.Count >= count || !_hasMore)
                    return results;

                // 2. Сетевая подгрузка: не более 2 сетевых запросов на одну фазу скролла
                int networkCalls = 0;
                const int maxNetworkCalls = 2;

                while (results.Count < count && _hasMore && _seenIds.Count < _maxResults && networkCalls < maxNetworkCalls)
                {
                    networkCalls++;

                    _enumerator ??= _youtube.Search
                        .GetResultBatchesAsync(_query, Filter, token)
                        .GetAsyncEnumerator(token);

                    if (!await _enumerator.MoveNextAsync().ConfigureAwait(false))
                    {
                        _hasMore = false;
                        break;
                    }

                    var batch = _enumerator.Current;
                    for (int i = 0; i < batch.Items.Count; i++)
                    {
                        if (_seenIds.Count >= _maxResults)
                        {
                            _hasMore = false;
                            break;
                        }

                        if (batch.Items[i] is not TrackInfo tInfo)
                            continue;

                        var rawId = tInfo.GetRawId();
                        if (!_seenIds.Add(rawId))
                            continue;

                        var canonicalTrack = _registry.RegisterOrUpdate(tInfo);

                        if (results.Count < count)
                            results.Add(canonicalTrack);
                        else
                            _buffer.Enqueue(canonicalTrack);
                    }
                }

                return results;
            }
            catch (OperationCanceledException) { return []; }
            catch (Exception ex)
            {
                Log.Error($"[SearchSession] Iteration failed: {ex.Message}");
                _hasMore = false;
                return [];
            }
            finally
            {
                Interlocked.Exchange(ref _isFetching, 0);
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _isDisposed, 1) != 0)
                return;

            _hasMore = false;
            _sessionCts.Cancel();

            _buffer.Clear();
            _seenIds.Clear();

            if (_enumerator != null)
            {
                try { await _enumerator.DisposeAsync().ConfigureAwait(false); }
                catch (NotSupportedException) { /* Игнорируем: энумератор InnerTube не требует явного Dispose */ }
                catch (Exception ex) { Log.Warn($"[SearchSession] Async dispose warning: {ex.Message}"); }
                _enumerator = null;
            }

            _sessionCts.Dispose();
            GC.SuppressFinalize(this);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _isDisposed, 1) != 0)
                return;

            _hasMore = false;
            _sessionCts.Cancel();

            _buffer.Clear();
            _seenIds.Clear();

            var enumerator = _enumerator;
            _enumerator = null;

            if (enumerator != null)
            {
                _ = Task.Run(async () =>
                {
                    try { await enumerator.DisposeAsync().ConfigureAwait(false); }
                    catch (NotSupportedException) { /* Игнорируем */ }
                    catch (Exception ex) { Log.Warn($"[SearchSession] Background dispose warning: {ex.Message}"); }
                });
            }

            _sessionCts.Dispose();
            GC.SuppressFinalize(this);
        }
    }

    private SearchSession? _currentSearchSession;
    private readonly ConcurrentDictionary<string, (DateTime ExpireAt, List<string> Items)> _suggestionsCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Создаёт новую сессию итеративного поиска.
    /// </summary>
    public SearchSession CreateSearchSession(
        string query,
        int maxResults = 300,
        SearchFilter filter = SearchFilter.MusicSong,
        IEnumerable<string>? skipTrackIds = null)
    {
        _currentSearchSession?.Dispose();
        _currentSearchSession = new SearchSession(_youtube, _trackRegistry, query, maxResults, filter, skipTrackIds);
        Log.Info($"[YouTube] Search session: '{query}' (max:{maxResults}, filter:{filter})");
        return _currentSearchSession;
    }

    /// <summary>
    /// Запускает поиск с возвратом как треков первого пакета, так и объекта активной сессии для дозагрузки.
    /// </summary>
    public async Task<(List<TrackInfo> Tracks, SearchSession Session)> SearchWithSessionAsync(
        string query,
        int initialCount = 25,
        int maxResults = 300,
        SearchFilter filter = SearchFilter.MusicSong,
        CancellationToken ct = default)
    {
        if (!IsReady || string.IsNullOrWhiteSpace(query))
            return ([], null!);

        var sw = Stopwatch.StartNew();
        var session = CreateSearchSession(query, maxResults, filter);
        var tracks = await session.FetchNextBatchAsync(initialCount, ct).ConfigureAwait(false);

        sw.Stop();
        Log.Info($"[YouTube] Initial '{query}': {tracks.Count} in {sw.ElapsedMilliseconds}ms (Filter: {filter})");

        return (tracks, session);
    }

    /// <summary>
    /// Получает поисковые подсказки от Google/YouTube Suggest API с fallback-доменами и локальным кэшированием.
    /// Предотвращает зависания UI-потока при блокировках сетевых узлов ТСПУ/DPI.
    /// </summary>
    /// <param name="query">Поисковый запрос.</param>
    /// <param name="ct">Токен отмены вызывающей стороны.</param>
    /// <returns>Список текстовых подсказок (до 10 элементов).</returns>
    public async Task<List<string>> GetSearchSuggestionsAsync(string query, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query))
            return [];

        var normalizedQuery = query.Trim();

        // Проверяем локальный RAM-кэш подсказок (TTL 5 минут) для исключения повторных запросов при Backspace
        if (_suggestionsCache.TryGetValue(normalizedQuery, out var cached) && cached.ExpireAt > DateTime.UtcNow)
        {
            return cached.Items;
        }

        // Ограничиваем таймаут подсказок 2.5 секундами — автодополнение не должно подвешивать ввод
        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(2500));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        var token = linkedCts.Token;

        var client = _currentHttpClient ?? SharedHttpClient.Instance;
        var encoded = Uri.EscapeDataString(normalizedQuery);

        // Zero-alloc fallback: генерируем строку по требованию без аллокации промежуточного массива и без ReadOnlySpan через await
        for (int i = 0; i < 2; i++)
        {
            var endpoint = i == 0
                ? $"https://suggestqueries.google.com/complete/search?client=firefox&ds=yt&q={encoded}&hl=ru"
                : $"https://suggestqueries-clients6.youtube.com/complete/search?client=firefox&ds=yt&q={encoded}&hl=ru";

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
                request.Headers.UserAgent.ParseAdd(YoutubeClientUtils.UaWeb);

                using var response = await client.SendAsync(
                    request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                    continue;

                await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
                using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: token).ConfigureAwait(false);

                if (doc.RootElement.ValueKind != JsonValueKind.Array || doc.RootElement.GetArrayLength() < 2)
                    continue;

                var arr = doc.RootElement[1];
                if (arr.ValueKind != JsonValueKind.Array)
                    continue;

                int maxSuggestions = _libraryService?.Settings.MaxSuggestionsCount ?? 8;
                int length = arr.GetArrayLength();
                var result = new List<string>(Math.Min(length, maxSuggestions));

                foreach (var item in arr.EnumerateArray())
                {
                    var text = item.GetString();
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        result.Add(text);
                        if (result.Count >= maxSuggestions)
                            break;
                    }
                }

                if (_suggestionsCache.Count > 100)
                    _suggestionsCache.Clear();

                _suggestionsCache[normalizedQuery] = (DateTime.UtcNow.AddMinutes(5), result);
                return result;
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                Log.Debug($"[YouTube] Suggestion timeout for '{normalizedQuery}' on endpoint #{i}");
            }
            catch (Exception ex)
            {
                Log.Debug($"[YouTube] Suggestion endpoint #{i} failed: {ex.Message}");
            }
        }

        return [];
    }

    #endregion

    #region Playlists, Channels, Radio & Downloads

    /// <summary>
    /// Получает название и список треков из внешнего плейлиста YouTube.
    /// </summary>
    public async Task<(string Name, List<TrackInfo> Tracks)?> GetPlaylistAsync(string url)
    {
        if (!IsReady) return null;

        try
        {
            ThrowIfInCooldown();
        }
        catch (BotDetectionException ex)
        {
            NotifyError($"[YouTube] Playlist blocked: wait {ex.RemainingCooldown.TotalSeconds:F0}s");
            return null;
        }

        try
        {
            var playlistId = PlaylistId.TryParse(url);
            if (playlistId == null) return null;

            var playlist = await _youtube.Playlists.GetAsync(playlistId.Value);
            var tracks = await _youtube.Playlists.GetVideosAsync(playlistId.Value).CollectAsync();

            for (int i = 0; i < tracks.Count; i++)
                _trackRegistry.RegisterOrUpdate(tracks[i]);

            Log.Info($"[YouTube] Playlist '{playlist.Name}': {tracks.Count} tracks");
            return (playlist.Name, tracks.ToList());
        }
        catch (BotDetectionException)
        {
            return null;
        }
        catch (Exception ex)
        {
            NotifyError($"[YouTube] GetPlaylistAsync error: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Загружает метаданные всех плейлистов канала для последующей синхронизации.
    /// </summary>
    public async Task<(string ChannelName, List<PlaylistSearchResult> Playlists)?> GetChannelPlaylistsForSyncAsync(
     string channelUrl, CancellationToken ct = default)
    {
        try
        {
            ThrowIfInCooldown();
        }
        catch (BotDetectionException ex)
        {
            NotifyError($"[YouTube] Sync blocked: wait {ex.RemainingCooldown.TotalSeconds:F0}s");
            return null;
        }

        var channel = await GetChannelFromUrlAsync(channelUrl, ct);
        if (channel is null) return null;

        Log.Info($"[YouTube] Fetching playlists from: {channel.Title}...");

        try
        {
            var results = new List<PlaylistSearchResult>();

            await foreach (var pl in _youtube.Channels.GetPlaylistsAsync(channel.Id, ct))
            {
                if (VideoController.IsInCooldown)
                {
                    Log.Warn("[YouTube] Channel sync interrupted by bot detection");
                    break;
                }

                if (pl.Name.Equals("Uploads", StringComparison.OrdinalIgnoreCase)) continue;

                var thumbs = new List<Thumbnail>();
                if (!string.IsNullOrEmpty(pl.ThumbnailUrl))
                    thumbs.Add(new Thumbnail(pl.ThumbnailUrl, new Resolution(0, 0)));

                var auth = pl.Author != null
                    ? new Author(new ChannelId(channel.Id.Value), pl.Author)
                    : null;

                results.Add(new PlaylistSearchResult(
                    new PlaylistId(pl.YoutubeId ?? ""),
                    pl.Name, auth, thumbs));
            }

            Log.Info($"[YouTube] Found {results.Count} playlists.");
            return (channel.Title, results);
        }
        catch (BotDetectionException)
        {
            return (channel.Title, []);
        }
        catch (Exception ex)
        {
            NotifyError($"[YouTube] Error parsing channel playlists: {ex.Message}");
            return (channel.Title, []);
        }
    }

    /// <summary>
    /// Получает список облачных плейлистов текущего авторизованного пользователя.
    /// </summary>
    public async Task<List<Playlist>> GetUserPlaylistsByAuthAsync()
    {
        return await _userDataService.GetMyPlaylistsAsync();
    }

    /// <summary>
    /// Импортирует плейлист: загружает метаданные и все треки единым запросом,
    /// определяет ownership и visibility, сохраняет треки в БД.
    /// </summary>
    public async Task<Playlist?> ImportPlaylistAsync(
        string playlistId, bool isAccountSync = false, CancellationToken ct = default)
    {
        try
        {
            ThrowIfInCooldown();
        }
        catch (BotDetectionException ex)
        {
            NotifyError($"[YouTube] Import blocked: wait {ex.RemainingCooldown.TotalSeconds:F0}s");
            return null;
        }

        try
        {
            var plId = new PlaylistId(playlistId);

            var result = await _youtube.Playlists.ImportAsync(plId, ct).ConfigureAwait(false);

            var playlist = result.Playlist;
            playlist.SyncMode = isAccountSync
                ? PlaylistSyncMode.TwoWaySync
                : PlaylistSyncMode.CloudPublic;

            // Ownership & Visibility
            ResolveOwnershipAndVisibility(playlist, isAccountSync);

            var tracks = result.Tracks;
            if (_libraryService != null)
            {
                for (int i = 0; i < tracks.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    await _libraryService.AddOrUpdateTrackAsync(tracks[i], ct).ConfigureAwait(false);
                    playlist.TrackIds.Add(tracks[i].Id);
                }
            }
            else
            {
                for (int i = 0; i < tracks.Count; i++)
                    playlist.TrackIds.Add(tracks[i].Id);
            }

            Log.Info($"[YouTube] ImportPlaylist '{playlistId}': {tracks.Count} tracks, " +
                     $"Ownership={playlist.Ownership}, Visibility={playlist.Visibility}");
            return playlist;
        }
        catch (BotDetectionException) { return null; }
        catch (OperationCanceledException) { throw; }
        catch (PlaylistUnavailableException ex)
        {
            Log.Warn($"[YouTube] Playlist '{playlistId}' unavailable: {ex.Message}");
            return null;
        }
        catch (Exception ex)
        {
            NotifyError($"[YouTube] Error importing playlist '{playlistId}': {ex.Message}");
            return null;
        }
    }

    private void ResolveOwnershipAndVisibility(Playlist playlist, bool isAccountSync)
    {
        if (!isAccountSync)
        {
            playlist.Ownership = PlaylistOwnership.Foreign;

            // URL-импорт: если privacy не удалось получить из API, считаем минимум Public
            if (playlist.Visibility == PlaylistVisibility.Unknown)
                playlist.Visibility = PlaylistVisibility.Public;

            return;
        }

        // Account sync: определяем ownership сравнением Author с UserName
        if (AuthService?.IsAuthenticated != true)
        {
            playlist.Ownership = PlaylistOwnership.Unknown;
            return;
        }

        var userName = AuthService.State.UserName;
        var playlistAuthor = playlist.Author;

        if (string.IsNullOrEmpty(userName) || string.IsNullOrEmpty(playlistAuthor))
        {
            playlist.Ownership = PlaylistOwnership.Unknown;
            return;
        }

        playlist.Ownership = playlistAuthor.Equals(userName, StringComparison.OrdinalIgnoreCase)
            ? PlaylistOwnership.Mine
            : PlaylistOwnership.Foreign;

        // Для чужих плейлистов из библиотеки fallback на Public только если privacy не распознали
        if (playlist.IsForeign && playlist.Visibility == PlaylistVisibility.Unknown)
            playlist.Visibility = PlaylistVisibility.Public;
    }

    /// <summary>
    /// Загружает информацию об авторе канала (название и ссылка на аватар максимального разрешения).
    /// </summary>
    public async Task<(string Name, string AvatarUrl)?> GetChannelInfoAsync(
        string url, CancellationToken ct = default)
    {
        var channel = await GetChannelFromUrlAsync(url, ct);
        if (channel == null) return null;

        string avatar = "";
        int maxWidth = 0;
        for (int i = 0; i < channel.Thumbnails.Count; i++)
        {
            if (channel.Thumbnails[i].Resolution.Width > maxWidth)
            {
                maxWidth = channel.Thumbnails[i].Resolution.Width;
                avatar = channel.Thumbnails[i].Url ?? "";
            }
        }

        return (channel.Title, avatar);
    }

    private async Task<Channel?> GetChannelFromUrlAsync(string url, CancellationToken ct = default)
    {
        try
        {
            var span = url.AsSpan();

            if (TryExtractSegment(span, "/channel/", out var channelId))
                return await _youtube.Channels.GetAsync(new ChannelId(channelId), ct);

            if (TryExtractSegment(span, "/@", out var handle))
                return await _youtube.Channels.GetByHandleAsync(new ChannelHandle(handle), ct);

            if (TryExtractSegment(span, "/c/", out var slug))
                return await _youtube.Channels.GetBySlugAsync(new ChannelSlug(slug), ct);

            if (TryExtractSegment(span, "/user/", out var user))
                return await _youtube.Channels.GetByUserAsync(new UserName(user), ct);

            NotifyError("[YouTube] Unrecognized channel URL format.");
            return null;
        }
        catch (Exception ex)
        {
            NotifyError($"[YouTube] Error getting channel info: {ex.Message}");
            return null;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool TryExtractSegment(ReadOnlySpan<char> url, ReadOnlySpan<char> pattern, out string result)
    {
        int idx = url.IndexOf(pattern);
        if (idx < 0)
        {
            result = "";
            return false;
        }

        var after = url[(idx + pattern.Length)..];

        int endSlash = after.IndexOf('/');
        int endQuery = after.IndexOf('?');

        int end = (endSlash, endQuery) switch
        {
            ( >= 0, >= 0) => Math.Min(endSlash, endQuery),
            ( >= 0, _) => endSlash,
            (_, >= 0) => endQuery,
            _ => after.Length
        };

        result = after[..end].ToString();
        return result.Length > 0;
    }

    /// <summary>
    /// Генерирует бесконечное радио-микс на основе переданного трека (RD-список).
    /// </summary>
    public async Task<List<TrackInfo>> GetRadioAsync(TrackInfo sourceTrack, int count = 25)
    {
        if (!IsReady || string.IsNullOrEmpty(sourceTrack.Url))
            return [];

        try
        {
            ThrowIfInCooldown();
        }
        catch (BotDetectionException ex)
        {
            NotifyError($"[YouTube] Radio blocked: wait {ex.RemainingCooldown.TotalSeconds:F0}s");
            return [];
        }

        try
        {
            var videoIdSpan = sourceTrack.GetRawIdSpan();
            if (videoIdSpan.IsEmpty) return [];

            var videoId = videoIdSpan.ToString();
            var mixUrl = $"https://www.youtube.com/watch?v={videoId}&list=RD{videoId}";

            var result = await GetPlaylistAsync(mixUrl);
            if (result == null) return [];

            var tracks = result.Value.Tracks;
            int take = Math.Min(count, tracks.Count);
            var output = new List<TrackInfo>(take);

            for (int i = 0; i < take; i++)
            {
                tracks[i].RadioSeedId = sourceTrack.Id;
                output.Add(tracks[i]);
            }

            return output;
        }
        catch (BotDetectionException)
        {
            return [];
        }
        catch
        {
            return [];
        }
    }

    /// <summary>
    /// Загружает список популярных треков (тренды) в текущем регионе.
    /// </summary>
    public async Task<List<TrackInfo>> GetTrendingAsync(int count = 20)
    {
        try
        {
            ThrowIfInCooldown();
        }
        catch (BotDetectionException ex)
        {
            NotifyError($"[YouTube] Trending blocked: wait {ex.RemainingCooldown.TotalSeconds:F0}s");
            return [];
        }

        try
        {
            var url = "https://music.youtube.com/playlist?list=RDCLAK5uy_kmPRjHDECIcuVwnKsx2Ng7fyNgFKWNJFs";
            var result = await GetPlaylistAsync(url);

            if (result != null)
            {
                var tracks = result.Value.Tracks;
                int take = Math.Min(count, tracks.Count);
                return tracks.GetRange(0, take);
            }

            return await SearchAsync("top music 2024", count);
        }
        catch (BotDetectionException)
        {
            return [];
        }
        catch
        {
            return await SearchAsync("top music 2024", count);
        }
    }

    /// <summary>
    /// Скачивает аудиодорожку трека на локальный диск в максимальном качестве.
    /// После успешной загрузки регистрирует файл в дисковом кэше LMP.
    /// </summary>
    public async Task<string?> DownloadTrackAsync(
      TrackInfo track,
      IProgress<float>? progress = null,
      CancellationToken ct = default)
    {
        if (!IsReady || string.IsNullOrEmpty(track.Url)) return null;

        try
        {
            ThrowIfInCooldown();
        }
        catch (BotDetectionException ex)
        {
            NotifyError($"[YouTube] Download blocked: wait {ex.RemainingCooldown.TotalSeconds:F0}s");
            return null;
        }

        try
        {
            var videoIdSpan = track.GetRawIdSpan();
            if (videoIdSpan.IsEmpty) return null;

            var vId = VideoId.Parse(videoIdSpan.ToString());
            var manifest = await _youtube.Videos.Streams.GetManifestAsync(vId, ct);
            var stream = manifest.GetAudioOnlyStreams().GetWithHighestBitrate();

            if (stream == null) return null;

            var fileName = SanitizeFileName($"{track.Author} - {track.Title}.{stream.Container.Name}");
            var filePath = Path.Combine(G.Folder.Downloads, fileName);

            var prog = progress != null
                ? new Progress<double>(p => progress.Report((float)p))
                : null;

            await _youtube.Videos.Streams.DownloadAsync(stream, filePath, progress: prog, cancellationToken: ct);
            Log.Info($"[YouTube] Downloaded: {fileName}");

            return filePath;
        }
        catch (BotDetectionException)
        {
            return null;
        }
        catch (Exception ex)
        {
            NotifyError($"[YouTube] Download error: {ex.Message}");
            return null;
        }
    }

    #endregion

    #region Manifest Mapping Helpers

    private ResolvedStreamDescriptor? SelectFromManifest(
        StreamManifest manifest,
        string trackId,
        AudioFormat? targetFormat,
        int targetBitrate)
    {
        var streams = manifest.GetAudioOnlyStreams()
            .OrderByDescending(s => s.Bitrate)
            .ToList();

        var selected = SelectBestStream(streams, targetFormat, targetBitrate);
        if (selected == null) return null;

        return CreateDescriptorFromStream(trackId, selected, manifest.IntegratedLufs);
    }

    private ResolvedStreamDescriptor CreateDescriptorFromStream(
         string trackId,
         AudioOnlyStreamInfo stream,
         float perceptualLufs)
    {
        var url = stream.Url;
        var format = YoutubeIdHelper.MapContainerToFormat(stream.Container.Name);
        var codec = DetermineCodec(stream, format);

        DateTime expireUtc = DateTime.MaxValue;
        var expireStr = UrlEx.TryGetQueryParameterValue(url, "expire");
        if (expireStr != null && long.TryParse(expireStr, out var unix))
            expireUtc = DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime;

        string cdnHost = "";
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
            cdnHost = uri.Host;

        return new ResolvedStreamDescriptor
        {
            TrackId = trackId,
            Itag = stream.Itag,
            Url = url,
            Format = format,
            Codec = codec,
            BitrateKbps = (int)Math.Round(stream.Bitrate.KiloBitsPerSecond),
            ContentLengthBytes = stream.Size.Bytes,
            CdnHost = cdnHost,
            ExpireUtc = expireUtc,
            IntegratedLufs = perceptualLufs,
            LanguageCode = stream.AudioLanguage?.Code,
            IsDefaultLanguage = stream.IsAudioLanguageDefault ?? false,
            Origin = StreamSource.YouTubeApi
        };
    }

    /// <summary>
    /// Создаёт дескриптор потока на основе полностью закэшированной записи.
    /// Битрейт берётся из реального значения записи кэша, а не из запроса пользователя,
    /// чтобы <see cref="AudioStreamInfo"/> корректно отражал воспроизводимый файл.
    /// </summary>
    private static ResolvedStreamDescriptor CreateDescriptorFromCacheEntry(
        string trackId,
        AudioCacheEntry entry)
    {
        return new ResolvedStreamDescriptor
        {
            TrackId = trackId,
            Url = "",
            Format = entry.Format,
            Codec = entry.Codec,
            BitrateKbps = entry.Bitrate,
            ContentLengthBytes = entry.TotalSize,
            Origin = StreamSource.DiskCacheFull
        };
    }

    /// <summary>
    /// Реконструирует <see cref="StreamManifest"/> из дисковой записи кэша манифеста.
    /// </summary>
    private static StreamManifest ReconstructManifest(TrackManifestEntry entry)
    {
        var streams = new List<IStreamInfo>(entry.Variants.Count);
        for (int i = 0; i < entry.Variants.Count; i++)
        {
            var v = entry.Variants[i];
            Language? lang = !string.IsNullOrEmpty(v.LanguageCode)
                ? new Language(v.LanguageCode, v.LanguageCode)
                : null;

            streams.Add(new AudioOnlyStreamInfo(
                v.Itag,
                v.Url,
                new Container(v.Container),
                new FileSize(v.Clen),
                new Bitrate(v.Bitrate),
                v.Codec,
                lang,
                v.IsDefaultLanguage,
                hasEncryptedNToken: false));
        }

        return new StreamManifest(streams, entry.IntegratedLufs);
    }

    /// <summary>
    /// Конвертирует манифест в список UI-опций с дедупликацией по нормализованному bucket'у.
    /// Предотвращает показ двух форматов с одинаковым физическим кэш-файлом.
    /// </summary>
    private static List<StreamOption> MapManifestToOptions(StreamManifest manifest)
    {
        var streams = manifest.GetAudioOnlyStreams()
            .OrderByDescending(s => s.Bitrate)
            .ToList();

        var seen = new HashSet<(AudioFormat, int)>();
        var result = new List<StreamOption>(streams.Count);

        for (int i = 0; i < streams.Count; i++)
        {
            var s = streams[i];
            var format = YoutubeIdHelper.MapContainerToFormat(s.Container.Name);
            int normalizedBitrate = AudioConstants.NormalizeBitrate((int)Math.Round(s.Bitrate.KiloBitsPerSecond));

            if (!seen.Add((format, normalizedBitrate)))
                continue;

            result.Add(new StreamOption
            {
                Format = format,
                CodecType = DetermineCodec(s, format),
                Bitrate = s.Bitrate.KiloBitsPerSecond,
                SizeMb = s.Size.MegaBytes
            });
        }

        return result;
    }

    private void PerformForceRefreshReset(string rawVideoId, string trackId)
    {
        Log.Warn($"[YouTube] [{rawVideoId}] Force refresh. Resetting caches...");
        try
        {
            _nTokenDecryptor.InvalidateCache();
            _sigCipherDecryptor.InvalidateCache();
            _nTokenDecryptor.PlayerManager.InvalidateContext();
            _youtube.Videos.Streams.InvalidateCipherManifest();
            SessionCacheStore.Invalidate(trackId);
            _manifestRamCache.TryRemove(trackId, out _);
            Log.Info($"[YouTube] [{rawVideoId}] Caches purged for force refresh.");
        }
        catch (Exception ex)
        {
            Log.Error($"[YouTube] [{rawVideoId}] Force refresh cache purge failed: {ex.Message}");
        }
    }

    #endregion

    #region Diagnostic Logging & Cache Clear

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();

        Span<char> buffer = name.Length <= 256
            ? stackalloc char[name.Length]
            : new char[name.Length];

        int pos = 0;
        for (int i = 0; i < name.Length && pos < 200; i++)
        {
            var c = name[i];
            if (Array.IndexOf(invalid, c) < 0)
                buffer[pos++] = c;
        }

        return new string(buffer[..pos]);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void NotifyStatus(string message) => Log.Info(message);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void NotifyError(string message) => Log.Error(message);

    /// <summary>
    /// Пытается быстро получить дескриптор потока из RAM-кэша манифестов без дисковых или сетевых запросов.
    /// Поддерживает как полный trackId (с yt_ префиксом), так и чистый 11-значный videoId.
    /// </summary>
    /// <param name="videoId">Идентификатор видео или трека.</param>
    /// <param name="format">Желаемый формат.</param>
    /// <param name="bitrate">Желаемый битрейт.</param>
    public ResolvedStreamDescriptor? TryGetCachedStreamDescriptor(string videoId, AudioFormat? format, int bitrate)
    {
        string trackId = videoId.StartsWith("yt_", StringComparison.Ordinal) ? videoId : $"yt_{videoId}";
        if (_manifestRamCache.TryGetValue(trackId, out var manifest))
        {
            return SelectFromManifest(manifest, trackId, format, bitrate);
        }
        return null;
    }

    /// <summary>
    /// Очищает оперативную память от кэша манифестов.
    /// </summary>
    public void ClearCache()
    {
        _manifestRamCache.Clear();
        Log.Info("[YouTube] RAM manifest cache cleared");
    }

    #endregion

    #region Dispose

    /// <summary>
    /// Освобождает все управляемые ресурсы провайдера YouTube.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        AuthService.OnAuthStateChanged -= ReloadClient;
        DisposeCurrentClient();

        _currentHandler?.Dispose();
        _currentHandler = null;

        _poTokenProvider?.Dispose();
        _poTokenProvider = null;

        _manifestRamCache.Clear();

        GC.SuppressFinalize(this);
        Log.Info("[YouTube] Provider disposed");
    }

    [GeneratedRegex(
        @"(?:youtube\.com\/(?:watch\?v=|embed\/|v\/|shorts\/)|youtu\.be\/)([a-zA-Z0-9_-]{11})",
        RegexOptions.IgnoreCase | RegexOptions.Compiled, "ru-RU")]
    private static partial Regex _YoutubeVideoRegex();

    [GeneratedRegex(
        @"(?:youtube\.com\/.*[?&]list=)([a-zA-Z0-9_-]+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled, "ru-RU")]
    private static partial Regex _YoutubePlaylistRegex();

    [GeneratedRegex(@"^[a-zA-Z0-9_-]{11}$", RegexOptions.Compiled)]
    private static partial Regex _ValidYoutubeId();

    #endregion
}

public sealed partial class StreamOption : ReactiveObject
{
    /// <summary>Типизированный формат контейнера.</summary>
    public AudioFormat Format { get; init; }

    /// <summary>Типизированный аудиокодек.</summary>
    public AudioCodec CodecType { get; init; }

    public double Bitrate { get; set; }
    public double SizeMb { get; set; }

    public string Container => Format.ToContainerName();
    public string Codec => CodecType.ToDisplayName();

    public string DisplayName => $"{Codec} {string.Format(LocalizationService.Instance.Get("Stream_Bitrate"), Bitrate)} ({Container})";

    public string SizeMbFormatted => string.Format(
        LocalizationService.Instance.Get("Stream_Format_Mb", "{0:F1} MB"),
        SizeMb);

    [Reactive] public partial bool IsDownloaded { get; set; }
    [Reactive] public partial bool IsActive { get; set; }
}

public sealed class HomeSection
{
    public string Title { get; set; } = "";
    public List<TrackInfo> Tracks { get; set; } = [];
}
