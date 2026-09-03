using Avalonia.Controls;
using Avalonia.Media;
using LMP.UI.Features.Shell;
using LMP.UI.Features.Shared;
using ReactiveUI;

using System.Reactive;
using System.Reactive.Linq;
using LMP.UI.Dialogs;
using Microsoft.Extensions.DependencyInjection;
using Avalonia.Threading;
using System.Globalization;

namespace LMP.UI.Features.Playlist;

/// <summary>
/// ViewModel экрана плейлиста.
/// Управляет метаданными, отображением ownership/visibility,
/// воспроизведением и двусторонней синхронизацией с YouTube.
/// </summary>
public sealed partial class PlaylistViewModel : TrackListReorderableViewModel, ISmoothTransitionViewModel
{
    #region Fields

    private readonly MusicLibraryManager _manager;
    private readonly DialogService _dialog;
    private readonly DominantColorService _dominantColor;
    private readonly MainWindowViewModel _mainWindow;
    private readonly PlaylistEditService _editService;
    private readonly PlaylistSyncService _syncService;
    private readonly PlayerControlService _playerControl;
    private readonly CookieAuthService _auth;

    private readonly EventHandler<string> _languageChangedHandler;
    private readonly IDisposable? _librarySubscription;

    private CancellationTokenSource? _playlistLoadCts;
    private string _currentPlaylistId = "";
    private Core.Models.Playlist? _currentPlaylist;

    private DateTime _lastLocalMutationTime = DateTime.MinValue;
    private const int LocalMutationDebounceMs = 1500;

    private List<TrackInfo>? _allTracksCache;
    private bool _allTracksCacheValid;

    private volatile bool _isSuspended;
    private int _syncInProgressGate;
    private HashSet<string>? _playlistTrackIds;

    #endregion

    #region Properties — Metadata

    [Reactive] public partial string PlaylistName { get; private set; } = string.Empty;
    [Reactive] public partial string? ThumbnailUrl { get; private set; }
    [Reactive] public partial string? Description { get; private set; }
    [Reactive] public partial int TrackCount { get; private set; }
    [Reactive] public partial TimeSpan TotalDuration { get; private set; }
    [Reactive] public partial string FormattedDuration { get; set; } = "";
    [Reactive] public partial IBrush? HeaderBackground { get; private set; }
    [Reactive] public partial bool IsLikedPlaylist { get; private set; }

    /// <summary>Отформатированное количество просмотров (компактный вид: 1.2K, 3.5M).</summary>
    [Reactive] public partial string? FormattedViewCount { get; private set; }

    /// <summary>Отформатированная дата обновления плейлиста.</summary>
    [Reactive] public partial string? FormattedReleaseDate { get; private set; }

    public string FormattedTrackCount =>
        LocalizationService.Instance.GetPlural("Playlist_TracksCount", TrackCount);

    public string? PlaylistYoutubeUrl =>
        IsLikedPlaylist && _auth.IsAuthenticated
            ? "https://www.youtube.com/playlist?list=LL"
            : _currentPlaylist?.YoutubeId is { Length: > 0 } id
                ? $"https://www.youtube.com/playlist?list={id}"
                : null;

    [Reactive] public partial bool HasYoutubeLink { get; private set; }

    #endregion

    #region Properties — Author & Ownership

    /// <summary>Имя автора/владельца плейлиста.</summary>
    [Reactive] public partial string? AuthorName { get; private set; }

    /// <summary>Показывать строку автора (любой плейлист с известным автором).</summary>
    [Reactive] public partial bool ShowAuthor { get; private set; }

    /// <summary>Плейлист доступен только для прослушивания (Foreign / CloudPublic).</summary>
    [Reactive] public partial bool IsReadOnly { get; private set; }

    /// <summary>Плейлист можно редактировать (не read-only, не system).</summary>
    [Reactive] public partial bool CanEdit { get; private set; }

    /// <summary>Плейлист приватный (🔒).</summary>
    [Reactive] public partial bool IsPrivate { get; private set; }

    /// <summary>Плейлист доступен по ссылке (🔗).</summary>
    [Reactive] public partial bool IsUnlisted { get; private set; }

    #endregion

    #region Properties — Cloud & Sync

    /// <summary>Плейлист связан с YouTube (есть YoutubeId и он доступен).</summary>
    [Reactive] public partial bool HasCloudSource { get; private set; }

    /// <summary>Можно запустить refresh/sync из облака (TwoWaySync или Liked).</summary>
    [Reactive] public partial bool CanRefreshFromCloud { get; private set; }

    /// <summary>Двусторонняя синхронизация активна.</summary>
    [Reactive] public partial bool IsTwoWaySynced { get; private set; }

    /// <summary>Синхронизация в процессе прямо сейчас.</summary>
    [Reactive] public partial bool IsSyncing { get; private set; }

    /// <summary>Есть хотя бы один статусный чип для отображения рядом с action-кнопками.</summary>
    [Reactive] public partial bool HasStatusChips { get; private set; }

    /// <summary>Локализованная строка последней синхронизации (null если не синхронизировался).</summary>
    [Reactive] public partial string? LastSyncedText { get; private set; }

    #endregion

    #region Properties — Playback State

    [Reactive] public partial bool IsPlayingThisPlaylist { get; private set; }
    [Reactive] public partial bool IsShuffleActive { get; private set; }
    [Reactive] public partial bool IsDownloadingActive { get; private set; }
    [Reactive] public partial bool CanReorderItems { get; private set; }

    /// <summary>Очередь «чистая» — запущена из этого плейлиста без сторонних треков.</summary>
    [Reactive] public partial bool IsQueuePure { get; private set; }

    /// <summary>Очередь чистая и сейчас активно играет (для анимации эквалайзера).</summary>
    [Reactive] public partial bool IsPlayingPure { get; private set; }

    /// <summary>Динамическая подсказка для кнопки-трансформера Play/Pause/Replace.</summary>
    public string PlayButtonTooltip
    {
        get
        {
            if (IsQueuePure)
                return IsPlayingPure ? (SL["Player_Pause"] ?? "Pause") : (SL["Player_Play"] ?? "Play");
            return SL["Playlist_PlayAll"] ?? "Play (replace queue)";
        }
    }

    #endregion

    #region Properties — Header UI

    public GridLength HeaderHeight
    {
        get => _headerHeight;
        set
        {
            if (!value.IsAbsolute) return;
            var clamped = new GridLength(Math.Clamp(value.Value, HeaderHeightMin, HeaderHeightMax));
            this.RaiseAndSetIfChanged(ref _headerHeight, clamped);
            if (Math.Abs(LibService.Settings.PlaylistHeaderHeight - clamped.Value) > 1)
                LibService.UpdateSettings(s => s.PlaylistHeaderHeight = clamped.Value);
        }
    }

    private GridLength _headerHeight;
    private const double HeaderHeightMin = 215;
    private const double HeaderHeightMax = 270;

    #endregion

    #region Commands

    public ReactiveCommand<Unit, Unit> PlayAllCommand { get; }
    public ReactiveCommand<Unit, Unit> DeletePlaylistCommand { get; }
    public ReactiveCommand<Unit, Unit> UploadToCloudCommand { get; }
    public ReactiveCommand<Unit, Unit> UnlinkFromCloudCommand { get; }
    public ReactiveCommand<Unit, Unit> ShufflePlayCommand { get; }
    public ReactiveCommand<Unit, Unit> DownloadAllCommand { get; }
    public ReactiveCommand<Unit, Unit> MergePlaylistCommand { get; }
    public ReactiveCommand<Unit, Unit> RefreshPlaylistCommand { get; }
    public ReactiveCommand<Unit, Unit> AddToQueueCommand { get; }
    public ReactiveCommand<(int oldIndex, int newIndex), Unit> MoveItemCommand { get; }
    public ReactiveCommand<Unit, Unit> EditPlaylistCommand { get; }
    public ReactiveCommand<Unit, Unit> CopyPlaylistLinkCommand { get; }
    public ReactiveCommand<Unit, Unit> OpenAuthorCommand { get; }

    #endregion

    #region Constructor

    public PlaylistViewModel(
        AudioEngine audio,
        DownloadService downloads,
        MusicLibraryManager manager,
        DialogService dialog,
        TrackViewModelFactory vmFactory,
        DominantColorService dominantColor,
        MainWindowViewModel mainWindow,
        PlaylistSyncService syncService,
        PlaylistEditService editService,
        PlayerControlService playerControl,
        CookieAuthService auth)
        : base(audio, downloads, vmFactory)
    {
        _manager = manager;
        _dialog = dialog;
        _dominantColor = dominantColor;
        _mainWindow = mainWindow;
        _syncService = syncService;
        _editService = editService;
        _playerControl = playerControl;
        _auth = auth;

        _languageChangedHandler = (_, _) =>
            this.RaisePropertyChanged(nameof(FormattedTrackCount));
        LocalizationService.Instance.LanguageChanged += _languageChangedHandler;

        _headerHeight = new GridLength(Math.Clamp(
            LibService.Settings.PlaylistHeaderHeight,
            HeaderHeightMin, HeaderHeightMax));

        // CanExecute observables
        var hasTracks = this.WhenAnyValue(x => x.TrackCount, static c => c > 0)
            .ObserveOn(RxSchedulers.MainThreadScheduler);

        var canEdit = this.WhenAnyValue(x => x.CanEdit)
            .ObserveOn(RxSchedulers.MainThreadScheduler);

        var canRefresh = this.WhenAnyValue(
                x => x.CanRefreshFromCloud, x => x.IsSyncing,
                static (canRefresh, isSyncing) => canRefresh && !isSyncing)
            .ObserveOn(RxSchedulers.MainThreadScheduler);

        // Commands
        PlayAllCommand = CreateCommand(
            ReactiveCommand.CreateFromTask(PlayAllAsync, hasTracks));

        DeletePlaylistCommand = CreateCommand(ReactiveCommand.CreateFromTask(async () =>
        {
            if (await _dialog.ConfirmAsync(
                SL["Dialog_Confirm_Title"],
                string.Format(SL["Playlist_DeleteConfirm"], PlaylistName)))
            {
                await _manager.DeletePlaylistAsync(_currentPlaylistId);
            }
        }));

        UploadToCloudCommand = CreateCommand(ReactiveCommand.CreateFromTask(async () =>
        {
            await _manager.UploadPlaylistToAccountAsync(_currentPlaylistId);
            await LoadPlaylistAsync(_currentPlaylistId);
        }));

        UnlinkFromCloudCommand = CreateCommand(ReactiveCommand.CreateFromTask(async () =>
        {
            await _manager.ConvertToLocalAsync(_currentPlaylistId);
            await LoadPlaylistAsync(_currentPlaylistId);
        }));

        RefreshPlaylistCommand = CreateCommand(
            ReactiveCommand.CreateFromTask(RefreshPlaylistAsync, canRefresh));

        ShufflePlayCommand = CreateCommand(
            ReactiveCommand.CreateFromTask(ShufflePlayAsync, hasTracks));

        DownloadAllCommand = CreateCommand(
            ReactiveCommand.CreateFromTask(DownloadAllAsync, hasTracks));

        MergePlaylistCommand = CreateCommand(
            ReactiveCommand.CreateFromTask(MergePlaylistAsync, canEdit));

        AddToQueueCommand = CreateCommand(
            ReactiveCommand.Create(EnqueueUniquePlaylistTracks, hasTracks));

        MoveItemCommand = CreateCommand(
            ReactiveCommand.CreateFromTask<(int oldIndex, int newIndex)>(async tuple =>
            {
                if (!CanReorderItems) return;
                _lastLocalMutationTime = DateTime.Now;
                InvalidateAllTracksCache();
                await MoveItemAsync(tuple.oldIndex, tuple.newIndex);
            }));

        EditPlaylistCommand = CreateCommand(
            ReactiveCommand.CreateFromTask(EditPlaylistAsync, canEdit));

        CopyPlaylistLinkCommand = CreateCommand(ReactiveCommand.CreateFromTask(
            CopyPlaylistLinkAsync,
            this.WhenAnyValue(x => x.HasYoutubeLink)
                .ObserveOn(RxSchedulers.MainThreadScheduler)));

        OpenAuthorCommand = CreateCommand(ReactiveCommand.Create(() =>
        {
            var url = _currentPlaylist?.AuthorUrl;
            if (string.IsNullOrEmpty(url)) return;

            try
            {
                System.Diagnostics.Process.Start(
                    new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = url,
                        UseShellExecute = true
                    });
            }
            catch (Exception ex)
            {
                Log.Warn($"[Playlist] Failed to open author URL: {ex.Message}");
            }
        }));

        // Reactive subscriptions
        this.WhenAnyValue(x => x.CanEdit, x => x.FilterQuery)
            .Subscribe(_ => CanReorderItems = CanEdit && CanReorder)
            .DisposeWith(Disposables);

        _librarySubscription = Observable.FromEvent(
                h => LibService.OnDataChanged += h,
                h => LibService.OnDataChanged -= h)
            .Throttle(TimeSpan.FromMilliseconds(600))
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(_ =>
            {
                if (_isSuspended) return;
                if ((DateTime.Now - _lastLocalMutationTime).TotalMilliseconds < LocalMutationDebounceMs)
                {
                    Log.Debug("[Playlist] Ignoring OnDataChanged (recent local mutation)");
                    return;
                }

                InvalidateAllTracksCache();
                if (string.IsNullOrEmpty(_currentPlaylistId)) return;

                Dispatcher.UIThread.InvokeAsync(
                    () => LoadPlaylistAsync(_currentPlaylistId, showLoader: false, CancellationToken.None),
                    DispatcherPriority.Background);
            });

        SubscribeToPlaybackSource();
    }

    #endregion

    #region ISmoothTransitionViewModel

    public override void PrepareForTransition()
    {
        base.PrepareForTransition();
        IsLoading = true;
    }

    #endregion

    #region Lifecycle

    protected override void OnSuspend() => _isSuspended = true;

    protected override void OnResume()
    {
        _isSuspended = false;
        InvalidateAllTracksCache();
        UpdatePlaybackState();
        this.RaisePropertyChanged(nameof(FormattedTrackCount));
    }

    #endregion

    #region TrackListReorderableViewModel

    protected override TrackItemViewModel CreateViewModel(TrackInfo item)
    {
        var vm = base.CreateViewModel(item);

        vm.SourceContextId = _currentPlaylistId;
        vm.IsPlaylistContext = CanEdit;

        vm.RemoveFromPlaylistAction = async t =>
        {
            if (!CanEdit) return;

            _lastLocalMutationTime = DateTime.Now;
            InvalidateAllTracksCache();
            _playlistTrackIds?.Remove(t.Id);
            RemoveItemLocally(t.Id);

            TrackCount = Math.Max(0, TrackCount - 1);
            this.RaisePropertyChanged(nameof(FormattedTrackCount));

            if (t.Duration > TimeSpan.Zero)
            {
                TotalDuration = TotalDuration > t.Duration
                    ? TotalDuration - t.Duration
                    : TimeSpan.Zero;
                FormatDuration();
            }

            await _manager.RemoveTrackFromPlaylistAsync(_currentPlaylistId, t.Id);
        };

        vm.StartRadioAction = t => Log.Info($"[Playlist] Start radio requested for {t.Title}");
        return vm;
    }

    protected override async Task<List<TrackInfo>> LoadTracksAsync(
        IEnumerable<string> ids, CancellationToken ct) =>
        await LibService.GetPlaylistTracksAsync(_currentPlaylistId, ct);

    protected override async Task SaveMoveAsync(
        int fromMasterIndex, int toMasterIndex, CancellationToken ct)
    {
        Log.Info($"[Playlist] Saving move {fromMasterIndex}→{toMasterIndex}");
        await _manager.MovePlaylistTrackAsync(_currentPlaylistId, fromMasterIndex, toMasterIndex, ct);
        Log.Info("[Playlist] Move saved");
    }

    protected override void OnPlay(TrackInfo track) =>
        _ = PlayFromPlaylistAsync(track);

    #endregion

    #region Public API

    /// <summary>
    /// Загружает плейлист с отображением loader/skeleton.
    /// </summary>
    public Task LoadPlaylistAsync(string playlistId) =>
        LoadPlaylistAsync(playlistId, showLoader: true, CancellationToken.None);

    #endregion

    #region Loading

    private CancellationTokenSource ReplacePlaylistLoadCts(CancellationToken externalCt)
    {
        _playlistLoadCts?.Cancel();
        _playlistLoadCts?.Dispose();
        _playlistLoadCts = externalCt.CanBeCanceled
            ? CancellationTokenSource.CreateLinkedTokenSource(externalCt)
            : new CancellationTokenSource();
        return _playlistLoadCts;
    }

    /// <summary>
    /// Загружает либо мягко обновляет плейлист.
    /// При <paramref name="showLoader"/> = false не поднимает loading-state.
    /// </summary>
    private async Task LoadPlaylistAsync(string playlistId, bool showLoader, CancellationToken ct)
    {
        var loadCts = ReplacePlaylistLoadCts(ct);
        var loadCt = loadCts.Token;

        _currentPlaylistId = playlistId;
        InvalidateAllTracksCache();
        IsLoading = showLoader;

        try
        {
            loadCt.ThrowIfCancellationRequested();

            var playlist = await LibService.GetPlaylistAsync(playlistId);
            loadCt.ThrowIfCancellationRequested();
            if (playlist is null) return;

            _currentPlaylist = playlist;

            // Metadata
            PlaylistName = playlist.Name;
            ThumbnailUrl = playlist.ThumbnailUrl;
            Description = playlist.Description;
            IsLikedPlaylist = playlistId == LibraryService.LikedPlaylistId;

            // Author & Ownership
            AuthorName = playlist.Author;
            ShowAuthor = !string.IsNullOrEmpty(playlist.Author);
            IsReadOnly = playlist.IsReadOnly;
            CanEdit = playlist.IsEditable;
            IsPrivate = playlist.Visibility == PlaylistVisibility.Private;
            IsUnlisted = playlist.Visibility == PlaylistVisibility.Unlisted;

            // Cloud & Sync
            IsTwoWaySynced = playlist.SyncMode == PlaylistSyncMode.TwoWaySync;
            HasCloudSource = playlist.HasCloudLink
                             || (IsLikedPlaylist && _auth.IsAuthenticated);
            CanRefreshFromCloud = IsTwoWaySynced
                                  || (IsLikedPlaylist && _auth.IsAuthenticated);
            HasStatusChips = IsReadOnly || IsPrivate || IsUnlisted || HasCloudSource;
            LastSyncedText = FormatRelativeTime(playlist.LastSyncedAtUtc);

            // Stats: views & date
            FormattedViewCount = FormatViewCount(playlist.ViewCount);
            FormattedReleaseDate = FormatReleaseDate(playlist.ReleaseDate);

            // Derived (одним блоком в конце)
            this.RaisePropertyChanged(nameof(PlaylistYoutubeUrl));
            HasYoutubeLink = PlaylistYoutubeUrl is not null;

            // Tracks
            var allIds = await LibService.GetPlaylistTrackIdsAsync(playlistId, ct);
            loadCt.ThrowIfCancellationRequested();

            _playlistTrackIds = new HashSet<string>(allIds, StringComparer.Ordinal);
            TrackCount = allIds.Count;
            this.RaisePropertyChanged(nameof(FormattedTrackCount));

            TotalDuration = await LibService.GetPlaylistTotalDurationAsync(playlistId, ct);
            loadCt.ThrowIfCancellationRequested();

            FormatDuration();

            await InitializeAsync(allIds, loadCt);
            loadCt.ThrowIfCancellationRequested();

            _ = LoadHeaderGradientAsync();
            _ = HydrateCacheStatusAsync(loadCt);

            UpdatePlaybackState();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Error($"[Playlist] Error loading playlist '{playlistId}': {ex.Message}");
        }
        finally
        {
            if (ReferenceEquals(_playlistLoadCts, loadCts))
                IsLoading = false;
        }
    }

    private async Task LoadHeaderGradientAsync()
    {
        try
        {
            if (_currentPlaylist?.EffectiveColor is { } colorStr)
            {
                try
                {
                    var color = Color.Parse(colorStr);
                    await Dispatcher.UIThread.InvokeAsync(() =>
                        HeaderBackground = DominantColorService.CreateHeaderGradient(color));
                    return;
                }
                catch (FormatException)
                {
                    Log.Warn($"[Playlist] Invalid EffectiveColor: {colorStr}");
                }
            }

            if (string.IsNullOrEmpty(ThumbnailUrl)
                || !PlaylistEditorViewModel.IsValidUri(ThumbnailUrl))
            {
                await SetHeaderBackgroundAsync(null);
                return;
            }

            var dominantColor = await _dominantColor.GetDominantColorAsync(ThumbnailUrl);
            if (dominantColor is not null)
            {
                _ = SaveComputedColorAsync(dominantColor.Value);
                await Dispatcher.UIThread.InvokeAsync(() =>
                    HeaderBackground = DominantColorService.CreateHeaderGradient(dominantColor.Value));
            }
            else
            {
                await SetHeaderBackgroundAsync(null);
            }
        }
        catch (HttpRequestException ex)
        {
            Log.Warn($"[Playlist] Thumbnail load failed (network): {ex.Message}");
            await SetHeaderBackgroundAsync(null);
            await _dialog.ShowInfoAsync(
                SL["Dialog_Warning_Title"] ?? "Warning",
                string.Format(SL["Playlist_ThumbnailLoadFailed"] ?? "Could not load cover: {0}", ex.Message));
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException)
        {
            Log.Warn($"[Playlist] Invalid image: {ex.Message}");
            await SetHeaderBackgroundAsync(null);
            await _dialog.ShowInfoAsync(
                SL["Dialog_Warning_Title"] ?? "Warning",
                SL["Playlist_ThumbnailInvalidFormat"] ?? "Invalid cover format.");
        }
        catch (Exception ex)
        {
            Log.Warn($"[Playlist] Gradient error: {ex.Message}");
            await SetHeaderBackgroundAsync(null);
        }
    }

    private async Task SaveComputedColorAsync(Color color)
    {
        if (_currentPlaylist is null) return;

        var colorHex = $"#{color.R:X2}{color.G:X2}{color.B:X2}";
        if (string.Equals(_currentPlaylist.ComputedColor, colorHex, StringComparison.OrdinalIgnoreCase))
            return;

        try
        {
            _currentPlaylist.ComputedColor = colorHex;
            await LibService.AddOrUpdatePlaylistAsync(_currentPlaylist);
            Log.Debug($"[Playlist] ComputedColor saved: {colorHex}");
        }
        catch (Exception ex)
        {
            Log.Warn($"[Playlist] Failed to save ComputedColor: {ex.Message}");
        }
    }

    private async Task SetHeaderBackgroundAsync(IBrush? brush) =>
        await Dispatcher.UIThread.InvokeAsync(() => HeaderBackground = brush);

    #endregion

    #region Playback

    private async Task PlayAllAsync()
    {
        if (TrackCount == 0) return;

        if (IsQueuePure)
        {
            await _playerControl.PlayPauseAsync();
            return;
        }

        var allTracks = await GetAllTracksAsync();
        if (allTracks.Count == 0) return;

        // Удален форсированный сброс Shuffle. Сохраняем глобальный выбор пользователя!
        _playerControl.SetActivePlaylistId(_currentPlaylistId);
        IsShuffleActive = false;
        await Audio.StartQueueAsync(allTracks, allTracks[0]);
    }

    private async Task ShufflePlayAsync()
    {
        if (TrackCount == 0) return;
        var allTracks = await GetAllTracksAsync();
        if (allTracks.Count == 0) return;

        // Выбираем случайную песню в качестве отправной точки для воспроизведения
        var startTrack = allTracks[Random.Shared.Next(allTracks.Count)];

        // Активируем Shuffle глобально. Теперь статус кнопки на PlayerBar визуально синхронизирован
        _playerControl.SetShuffleEnabled(true);
        _playerControl.SetActivePlaylistId(_currentPlaylistId);

        // Движок AudioEngine автоматически перемешает оставшуюся очередь, закрепив выбранный трек на первой позиции
        await Audio.StartQueueAsync(allTracks, startTrack);

        IsShuffleActive = true;
        Observable.Timer(TimeSpan.FromMilliseconds(800))
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(_ => IsShuffleActive = false)
            .DisposeWith(Disposables);
    }

    private async Task PlayFromPlaylistAsync(TrackInfo track)
    {
        try
        {
            // Удален форсированный сброс Shuffle. 
            // Если Shuffle включен, при клике на трек очередь автоматически перемешается, воспроизведя этот трек первым.
            _playerControl.SetActivePlaylistId(_currentPlaylistId);
            IsShuffleActive = false;
            var allTracks = await GetAllTracksAsync();
            await Audio.StartQueueAsync(allTracks, track);
            _ = LibService.AddToRecentlyPlayedAsync(track);
        }
        catch (Exception ex)
        {
            Log.Error($"[Playlist] PlayFromPlaylist error: {ex.Message}");
        }
    }

    private async Task DownloadAllAsync()
    {
        IsDownloadingActive = true;
        var allTracks = await GetAllTracksAsync();
        foreach (var track in allTracks.Where(static t => !t.IsDownloaded))
            Downloads.StartDownload(track);

        Observable.Timer(TimeSpan.FromSeconds(2))
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(_ => IsDownloadingActive = false)
            .DisposeWith(Disposables);
    }

    private void EnqueueUniquePlaylistTracks()
    {
        var tracks = GetLoadedItemsSnapshot();
        if (tracks.Count == 0) return;
        Audio.EnqueuePlaylistWithNotification(tracks, PlaylistName);
    }

    /// <summary>
    /// Подписка на аудио-движок для smart Play/Pause/Equalizer button.
    /// </summary>
    private void SubscribeToPlaybackSource()
    {
        Observable.CombineLatest(
                _playerControl.ActivePlaylistIdObservable,
                _playerControl.PlaybackStateObservable,
                _playerControl.QueueCountObservable,
                (activeId, state, qCount) => new { activeId, state.IsPlaying, qCount })
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(data =>
            {
                bool isActive = data.activeId == _currentPlaylistId;
                IsPlayingThisPlaylist = isActive;
                IsQueuePure = isActive && CheckQueuePurity();
                IsPlayingPure = IsQueuePure && data.IsPlaying;
                this.RaisePropertyChanged(nameof(PlayButtonTooltip));
            })
            .DisposeWith(Disposables);
    }

    private bool CheckQueuePurity()
    {
        if (_playlistTrackIds == null) return false;
        var queue = Audio.Queue;
        if (queue.Count != TrackCount) return false;

        for (int i = 0; i < queue.Count; i++)
        {
            if (!_playlistTrackIds.Contains(queue[i].Id))
                return false;
        }
        return true;
    }

    private void UpdatePlaybackState()
    {
        IsPlayingThisPlaylist = _playerControl.ActivePlaylistId == _currentPlaylistId;
        IsQueuePure = IsPlayingThisPlaylist && CheckQueuePurity();
        IsPlayingPure = IsQueuePure && _playerControl.IsPlaying;
        this.RaisePropertyChanged(nameof(PlayButtonTooltip));
    }

    #endregion

    #region Sync & Edit

    private async Task RefreshPlaylistAsync()
    {
        if (!CanRefreshFromCloud) return;
        if (Interlocked.Exchange(ref _syncInProgressGate, 1) != 0)
        {
            Log.Debug("[Playlist] Sync ignored: already in progress");
            return;
        }

        IsSyncing = true;
        _mainWindow.LockNavigation(SL["Playlist_SyncInProgress"] ?? "Syncing...");

        try
        {
            var notifications = AppEntry.Services.GetRequiredService<NotificationService>();

            if (IsLikedPlaylist)
            {
                await _manager.SyncLikedTracksAsync();
                InvalidateAllTracksCache();
                await LoadPlaylistAsync(_currentPlaylistId);

                await notifications.ShowToastAsync(
                    titleKey: "Playlist_SyncComplete_Toast_Title",
                    messageKey: "Sync_Success_Msg_LikedOnly",
                    severity: NotificationSeverity.Success,
                    durationMs: 4000);
                NotificationService.PlaySuccessSound();
            }
            else
            {
                var result = await _syncService.SyncWithDialogAsync(_currentPlaylistId);
                if (result is null) return;

                if (result.Success)
                {
                    InvalidateAllTracksCache();
                    await LoadPlaylistAsync(_currentPlaylistId);

                    if (result.TracksAddedLocally > 0 || result.TracksAddedToCloud > 0 ||
                        result.TracksRemovedLocally > 0 || result.TracksRemovedFromCloud > 0 ||
                        result.MetadataChanged)
                    {
                        await notifications.ShowToastAsync(
                            titleKey: "Playlist_SyncComplete_Toast_Title",
                            messageKey: "Playlist_SyncSuccess_Details",
                            messageArgs:
                            [
                                result.TracksAddedLocally, result.TracksAddedToCloud,
                                result.TracksRemovedLocally, result.TracksRemovedFromCloud
                            ],
                            severity: NotificationSeverity.Success,
                            durationMs: 4000);
                        NotificationService.PlaySuccessSound();
                    }
                }
                else
                {
                    await _dialog.ShowInfoAsync(
                        SL["Dialog_Error_Title"] ?? "Error",
                        result.ErrorMessage ?? SL["Playlist_SyncFailed"] ?? "Sync failed");
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error($"[Playlist] Sync error: {ex.Message}");
            await _dialog.ShowInfoAsync(SL["Dialog_Error_Title"] ?? "Error", ex.Message);
        }
        finally
        {
            IsSyncing = false;
            _mainWindow.UnlockNavigation();
            Volatile.Write(ref _syncInProgressGate, 0);
        }
    }

    private async Task EditPlaylistAsync()
    {
        var result = await _editService.EditPlaylistAsync(
            _currentPlaylistId,
            _mainWindow.LockNavigation,
            _mainWindow.UnlockNavigation);

        if (result is { Changed: true })
            await LoadPlaylistAsync(_currentPlaylistId);
    }

    private async Task CopyPlaylistLinkAsync()
    {
        if (_currentPlaylist?.YoutubeId is not { Length: > 0 } ytId)
        {
            CopyHintService.Instance.Show(
                SL["Playlist_CopyLink_NotLinked"] ?? "Not linked to YouTube",
                CopyHintKind.Warning);
            return;
        }

        await Clipboard.SetTextAsync($"https://www.youtube.com/playlist?list={ytId}");
        CopyHintService.Instance.Show(SL["Playlist_LinkCopied"] ?? "Copied!", CopyHintKind.Success);
    }

    private async Task MergePlaylistAsync()
    {
        var otherPlaylists = (await LibService.GetAllPlaylistsAsync())
            .Where(p => p.Id != _currentPlaylistId && p.IsLocal)
            .ToList();

        if (otherPlaylists.Count == 0)
        {
            await _dialog.ShowInfoAsync(
                SL["Dialog_Merge_NoTarget_Title"],
                SL["Dialog_Merge_NoTarget_Msg"]);
            return;
        }

        var targetId = otherPlaylists.First().Id;
        if (!string.IsNullOrEmpty(targetId))
        {
            bool ok = await _manager.MergePlaylistsAsync(_currentPlaylistId, targetId);
            await _dialog.ShowInfoAsync(
                ok ? SL["Dialog_Success"] : SL["Dialog_Error_Title"],
                ok ? SL["Merge_Success_Msg"] : SL["Merge_Error_Msg"]);
        }
    }

    #endregion

    #region Cache & Helpers

    private async Task<List<TrackInfo>> GetAllTracksAsync()
    {
        if (_allTracksCacheValid && _allTracksCache is not null)
            return _allTracksCache;

        _allTracksCache = await LibService.GetPlaylistTracksAsync(_currentPlaylistId);
        _allTracksCacheValid = true;
        return _allTracksCache;
    }

    private void InvalidateAllTracksCache()
    {
        _allTracksCacheValid = false;
        _allTracksCache = null;
    }

    #endregion

    #region Formatting

    /// <summary>
    /// Форматирует количество просмотров в компактный вид с правильным локализованным склонением.
    /// </summary>
    private static string? FormatViewCount(long? views)
    {
        if (views is null or <= 0) return null;

        long v = views.Value;

        // Для точных чисел меньше 10 000 используем полноценное языковое склонение
        if (v < 10_000)
        {
            return SL.GetPlural("Playlist_Views", (int)v);
        }

        // Для сокращенных единиц (K, M, B) форматируем число и подставляем форму множественного числа (other)
        string number = v switch
        {
            >= 1_000_000_000 => string.Create(CultureInfo.CurrentCulture, $"{v / 1_000_000_000.0:0.#}B"),
            >= 1_000_000 => string.Create(CultureInfo.CurrentCulture, $"{v / 1_000_000.0:0.#}M"),
            _ => string.Create(CultureInfo.CurrentCulture, $"{v / 1_000.0:0.#}K")
        };

        var pattern = SL.Get("Playlist_Views_other", "{0} views");
        return string.Format(CultureInfo.CurrentCulture, pattern, number);
    }

    /// <summary>
    /// Форматирует дату обновления плейлиста с учётом текущей локали.
    /// </summary>
    private static string? FormatReleaseDate(DateOnly? date)
    {
        if (date is null) return null;

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        int daysDiff = today.DayNumber - date.Value.DayNumber;

        if (daysDiff == 0) return SL["Playlist_Updated_JustNow"] ?? "обновлено сегодня";
        if (daysDiff == 1) return SL["Playlist_Updated_Yesterday"] ?? "обновлено вчера";
        if (daysDiff is > 1 and < 7)
            return string.Format(SL["Playlist_Updated_DaysAgo"] ?? "обновлено {0} дн. назад", daysDiff);

        return date.Value.ToString("d MMM yyyy", CultureInfo.CurrentCulture);
    }

    private void FormatDuration()
    {
        int totalHours = (int)TotalDuration.TotalHours;
        int minutes = TotalDuration.Minutes;
        int seconds = TotalDuration.Seconds;

        FormattedDuration = totalHours > 0
            ? $"{totalHours}{SL["Duration_Hours"]} {minutes}{SL["Duration_Minutes"]}"
            : minutes > 0
                ? $"{minutes}{SL["Duration_Minutes"]} {seconds}{SL["Duration_Seconds"]}"
                : $"{seconds}{SL["Duration_Seconds"]}";
    }

    /// <summary>
    /// Форматирует UTC-время в локализованную относительную строку.
    /// </summary>
    private static string? FormatRelativeTime(DateTime? utcTime)
    {
        if (utcTime is null) return null;

        var diff = DateTime.UtcNow - utcTime.Value;

        if (diff.TotalMinutes < 1) return SL["Playlist_Synced_JustNow"];
        if (diff.TotalHours < 1) return string.Format(SL["Playlist_Synced_MinutesAgo"], (int)diff.TotalMinutes);
        if (diff.TotalDays < 1) return string.Format(SL["Playlist_Synced_HoursAgo"], (int)diff.TotalHours);
        if (diff.TotalDays < 7) return string.Format(SL["Playlist_Synced_DaysAgo"], (int)diff.TotalDays);
        if (diff.TotalDays < 30) return string.Format(SL["Playlist_Synced_WeeksAgo"], (int)(diff.TotalDays / 7));
        return string.Format(SL["Playlist_Synced_MonthsAgo"], (int)(diff.TotalDays / 30));
    }

    #endregion

    #region Dispose

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Log.Debug($"[PlaylistVM] Disposing {_currentPlaylistId}");
            LocalizationService.Instance.LanguageChanged -= _languageChangedHandler;
            _librarySubscription?.Dispose();
            _playlistLoadCts?.Cancel();
            _playlistLoadCts?.Dispose();
            _playlistLoadCts = null;
            InvalidateAllTracksCache();
            _currentPlaylist = null;
        }
        base.Dispose(disposing);
    }

    #endregion
}
