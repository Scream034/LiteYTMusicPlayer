using LMP.UI.Features.Shell;
using ReactiveUI;

using System.Collections.ObjectModel;
using System.Reactive;
using System.Reactive.Linq;
using LMP.Core.Youtube.Search;
using LMP.UI.Dialogs;
using Avalonia.Threading;
using LMP.Core.Helpers.Extensions;
using System.Collections.Specialized;

namespace LMP.UI.Features.Library;

/// <summary>
/// ViewModel страницы «Библиотека» — управление плейлистами пользователя.
/// 
/// <para><b>Оптимизации:</b></para>
/// <list type="bullet">
///   <item>UI-Yielding через Dispatcher.InvokeAsync для плавного рендера</item>
///   <item>Батчинг карточек: первые 12 мгновенно, остальные по 4 с задержкой</item>
///   <item>Подписки на события только в конструкторе (без дублирования)</item>
///   <item>O(1) запрос длительности вместо N+1</item>
/// </list>
/// </summary>
public sealed partial class LibraryViewModel : ViewModelBase, ISmoothTransitionViewModel
{
    #region Зависимости

    private readonly AudioEngine _audio;
    private readonly LibraryService _library;
    private readonly YoutubeProvider _youtube;
    private readonly CookieAuthService _auth;
    private readonly DialogService _dialog;
    private readonly MainWindowViewModel _mainWindow;
    private readonly MusicLibraryManager _manager;
    private readonly PlaylistEditService _editService;
    private readonly NotificationService _notifications;
    private readonly PlayerControlService _playerControl;

    #endregion

    #region Внутреннее состояние

    private CancellationTokenSource? _syncCts;
    private CancellationTokenSource? _staggerCts;
    private CancellationTokenSource? _statsAnimCts;
    private bool _isDisposed;
    private int _prevPlaylistCount;
    private int _prevTrackCount;

    /// <summary>
    /// Локальный признак наличия данных в памяти.
    /// </summary>
    private bool _isDataLoaded;

    /// <summary>
    /// Идентификатор владельца, для которого последний раз была загружена страница.
    /// Защищает от повторного использования stale-кэша после смены аккаунта,
    /// даже если broadсast был пропущен или страница уже находилась в кеше навигации.
    /// </summary>
    private string _loadedOwnerId = string.Empty;

    #endregion

    #region Reactive-свойства

    [Reactive] public partial bool IsContentReady { get; private set; }
    [Reactive] public partial bool IsLoading { get; private set; }
    [Reactive] public partial bool IsSyncing { get; private set; }
    [Reactive] public partial double SyncProgress { get; private set; }
    [Reactive] public partial string SyncStatus { get; private set; } = "";
    [Reactive] public partial bool IsAuthenticated { get; private set; }
    [Reactive] public partial bool HasPlaylists { get; private set; }

    #endregion

    #region Статистика

    [Reactive] public partial bool IsStatsVisible { get; private set; }
    [Reactive] public partial string PlaylistCountText { get; private set; } = "";
    [Reactive] public partial string TotalTracksText { get; private set; } = "";
    [Reactive] public partial string TotalDurationText { get; private set; } = "";
    [Reactive] public partial string AvgTrackDurationText { get; private set; } = "";
    [Reactive] public partial string AvgPlaylistDurationText { get; private set; } = "";

    #endregion

    #region Коллекция и команды

    public ObservableCollection<PlaylistCardViewModel> Playlists { get; } = [];

    public ReactiveCommand<Unit, Unit> RefreshCommand { get; }
    public ReactiveCommand<Unit, Unit> OpenCreateCommand { get; }
    public ReactiveCommand<Unit, Unit> SyncAccountPlaylistsCommand { get; }
    public ReactiveCommand<Unit, Unit> CancelSyncCommand { get; }

    #endregion

    #region Конструктор

    /// <inheritdoc />
    protected override bool HandlesAccountChanges => true;

    public LibraryViewModel(
        LibraryService library,
        YoutubeProvider youtube,
        CookieAuthService auth,
        MainWindowViewModel mainWindow,
        DialogService dialog,
        MusicLibraryManager manager,
        AudioEngine audio,
        NotificationService notifications,
        PlaylistEditService editService,
        PlayerControlService playerControl)
    {
        _audio = audio;
        _library = library;
        _youtube = youtube;
        _auth = auth;
        _dialog = dialog;
        _mainWindow = mainWindow;
        _manager = manager;
        _notifications = notifications;
        _editService = editService;
        _playerControl = playerControl;

        IsAuthenticated = _auth.IsAuthenticated;
        _auth.OnAuthStateChanged += OnAuthChanged;

        OpenCreateCommand = CreateCommand(ReactiveCommand.CreateFromTask(OpenCreateDialogAsync));

        var canSync = this.WhenAnyValue(x => x.IsSyncing, syncing => !syncing);
        SyncAccountPlaylistsCommand = CreateCommand(
            ReactiveCommand.CreateFromTask(SyncAccountPlaylistsAsync, canSync));

        CancelSyncCommand = CreateCommand(ReactiveCommand.Create(() =>
        {
            _syncCts?.Cancel();
            SyncStatus = SL["Sync_Cancelling"];
        }));

        RefreshCommand = CreateCommand(ReactiveCommand.CreateFromTask(LoadPlaylistsAsync));

        SubscribeToLibraryEvents();
        Playlists.CollectionChanged += OnPlaylistsCollectionChanged;
        HasPlaylists = Playlists.Count > 0;
    }

    #endregion

    #region ISmoothTransitionViewModel

    /// <inheritdoc />
    public void PrepareForTransition()
    {
        // Если данные уже в памяти, НЕ сбрасываем контент в скелетон — сохраняем плавность
        if (!_isDataLoaded)
            IsContentReady = false;
    }

    #endregion

    #region Навигация

    public override async Task OnNavigatedToAsync()
    {
        if (_isDisposed) return;

        var currentOwnerId = _auth.State.DisplayId;
        if (_isDataLoaded && string.Equals(_loadedOwnerId, currentOwnerId, StringComparison.Ordinal))
        {
            IsContentReady = true;
            return;
        }

        // Загрузка в фоне без блокировки UI-потока
        await LoadPlaylistsAsync().ConfigureAwait(false);

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (_isDisposed) return;
            _isDataLoaded = true;
            _loadedOwnerId = _auth.State.DisplayId;
            IsContentReady = true;
        });
    }

    #endregion

    #region Подписки на события

    /// <summary>
    /// Подписывается на события LibraryService.
    /// Вызывается ТОЛЬКО из конструктора — предотвращает дублирование подписок.
    /// </summary>
    private void SubscribeToLibraryEvents()
    {
        // Инкрементальное обновление при изменении плейлиста
        Observable.FromEvent<Action<Core.Models.Playlist>, Core.Models.Playlist>(
                h => _library.OnPlaylistChanged += h,
                h => _library.OnPlaylistChanged -= h)
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Where(_ => !_isDisposed && !IsSyncing)
            .Subscribe(OnPlaylistChangedIncremental)
            .DisposeWith(Disposables);

        // Инкрементальное удаление плейлиста
        Observable.FromEvent<Action<string>, string>(
                h => _library.OnPlaylistRemoved += h,
                h => _library.OnPlaylistRemoved -= h)
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Where(_ => !_isDisposed && !IsSyncing)
            .Subscribe(OnPlaylistRemovedIncremental)
            .DisposeWith(Disposables);

        // Обновление статистики с дебаунсом
        Observable.FromEvent(
                h => _library.OnDataChanged += h,
                h => _library.OnDataChanged -= h)
            .Throttle(TimeSpan.FromMilliseconds(500))
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Where(__ => !_isDisposed && !IsSyncing)
            .Subscribe(__ => UpdateStatsInBackground())
            .DisposeWith(Disposables);
    }

    private async void UpdateStatsInBackground()
    {
        try
        {
            await UpdateStatsAnimatedAsync();
        }
        catch (Exception ex)
        {
            Log.Warn($"[Library] Ошибка обновления статистики: {ex.Message}");
        }
    }

    #endregion

    #region Инкрементальные обновления

    private async void OnPlaylistChangedIncremental(Core.Models.Playlist playlist)
    {
        if (_isDisposed) return;

        try
        {
            var result = await _library.GetPlaylistWithCountAsync(playlist.Id);
            if (result == null) return;

            var (freshPlaylist, trackCount) = result.Value;
            var existingVm = Playlists.FirstOrDefault(vm => vm.Id == playlist.Id);

            if (existingVm != null)
            {
                existingVm.UpdateFrom(freshPlaylist, trackCount);
            }
            else
            {
                var vm = CreatePlaylistCardVm(freshPlaylist, trackCount);
                int insertIndex = CalculateInsertIndex(freshPlaylist);

                if (insertIndex >= Playlists.Count)
                    Playlists.Add(vm);
                else
                    Playlists.Insert(insertIndex, vm);

                await Task.Delay(50);
                vm.Show();
            }
        }
        catch (Exception ex)
        {
            Log.Error($"[Library] Ошибка инкрементального обновления: {ex.Message}");
            await LoadPlaylistsAsync();
        }
    }

    private void OnPlaylistRemovedIncremental(string playlistId)
    {
        if (_isDisposed) return;

        var vm = Playlists.FirstOrDefault(x => x.Id == playlistId);
        if (vm != null)
        {
            vm.Dispose();
            Playlists.Remove(vm);
        }
    }

    private int CalculateInsertIndex(Core.Models.Playlist playlist)
    {
        if (playlist.Id == LibraryService.LikedPlaylistId) return 0;

        int index = 0;
        foreach (var vm in Playlists)
        {
            if (vm.IsLikedPlaylist) { index++; continue; }

            if (playlist.IsLocal && !vm.IsLocal) break;
            if (!playlist.IsLocal && vm.IsLocal) { index++; continue; }

            if (string.Compare(playlist.Name, vm.Name, StringComparison.Ordinal) < 0) break;
            index++;
        }

        return index;
    }

    #endregion

    #region Создание плейлиста

    private async Task OpenCreateDialogAsync()
    {
        if (_isDisposed) return;

        var result = await _dialog.ShowCreatePlaylistDialogAsync();
        if (result == null || string.IsNullOrWhiteSpace(result.Name)) return;

        var trimmedName = result.Name.Trim();
        bool wantsCloud = result.SyncToCloud && _auth.IsAuthenticated;
        string? youtubeId = null;

        if (wantsCloud)
        {
            _mainWindow.LockNavigation(SL["Playlist_CreatingCloud"] ?? "Создание в облаке...");
            try
            {
                youtubeId = await _youtube.CreatePlaylistAsync(trimmedName);

                if (string.IsNullOrEmpty(youtubeId))
                {
                    wantsCloud = false;
                    var createLocal = await OfferLocalFallbackAsync(
                        SL["Playlist_CloudCreateFailed_AskLocal"]
                            ?? "Не удалось создать плейлист в YouTube Music. Создать локально?");
                    if (!createLocal) return;
                }
            }
            catch (Exception ex)
            {
                Log.Error($"[Library] Ошибка создания облачного плейлиста: {ex.Message}");
                wantsCloud = false;

                var createLocal = await OfferLocalFallbackAsync(
                    string.Format(
                        SL["Playlist_CloudError_AskLocal"]
                            ?? "Ошибка YouTube API: {0}\n\nСоздать локально?",
                        ex.Message));
                if (!createLocal) return;
            }
            finally
            {
                _mainWindow.UnlockNavigation();
            }
        }

        var playlist = await _library.CreatePlaylistAsync(trimmedName);

        if (wantsCloud && !string.IsNullOrEmpty(youtubeId))
        {
            playlist.YoutubeId = youtubeId;
            playlist.SyncMode = PlaylistSyncMode.TwoWaySync;
        }
        else
        {
            playlist.SyncMode = PlaylistSyncMode.LocalOnly;
        }

        await _library.AddOrUpdatePlaylistAsync(playlist);

        Log.Info($"[Library] Создан плейлист '{trimmedName}' " +
                 $"(Sync={playlist.SyncMode}, YtId={playlist.YoutubeId ?? "none"})");
    }

    private async Task<bool> OfferLocalFallbackAsync(string message)
    {
        return await _dialog.ConfirmAsync(
            SL["Dialog_Warning_Title"] ?? "Предупреждение",
            message,
            SL["Playlist_CreateLocal"] ?? "Создать локально",
            SL["Button_Cancel"] ?? "Отмена");
    }

    #endregion

    #region Обработчики событий

    /// <summary>
    /// Вызывается из <see cref="CookieAuthService.OnAuthStateChanged"/>.
    /// Событие может стрелять с сетевого/таймерного потока — диспатчим на UI.
    /// </summary>
    private void OnAuthChanged()
    {
        if (_isDisposed) return;
        Dispatcher.UIThread.Post(
            () => IsAuthenticated = _auth.IsAuthenticated,
            DispatcherPriority.Background);
    }

    /// <inheritdoc />
    protected override void OnAccountChanged()
    {
        base.OnAccountChanged();

        bool wasActive = IsContentReady;

        _isDataLoaded = false;
        _loadedOwnerId = string.Empty;
        IsContentReady = false;
        Playlists.Clear();

        if (wasActive)
        {
            Log.Info("[Library] Account changed while page was visible. Re-rendering lists immediately.");
            _ = ReloadAfterAccountChangeAsync();
        }
    }

    /// <summary>
    /// Полная перезагрузка списка плейлистов после смены аккаунта.
    /// </summary>
    private async Task ReloadAfterAccountChangeAsync()
    {
        try
        {
            await LoadPlaylistsAsync();

            if (!_isDisposed)
            {
                _isDataLoaded = true;
                _loadedOwnerId = _auth.State.DisplayId;
                IsContentReady = true;
            }
        }
        catch (Exception ex)
        {
            Log.Error($"[Library] Failed to reload after account change: {ex.Message}");
        }
    }

    #endregion

    #region Синхронизация с YouTube

    /// <summary>
    /// Выполняет синхронизацию плейлистов с аккаунтом YouTube Music.
    /// Лайки синхронизируются отдельно и не запускаются скрыто в фоне.
    /// Все изменения bindable-состояния выполняются строго на UI-потоке.
    /// </summary>
    private async Task SyncAccountPlaylistsAsync()
    {
        if (_isDisposed) return;

        _syncCts?.Cancel();
        _syncCts = new CancellationTokenSource();
        var ct = _syncCts.Token;

        IsSyncing = true;
        IsStatsVisible = false;
        SyncProgress = 0;
        SyncStatus = SL["Sync_FetchingPlaylists"];

        _mainWindow.LockNavigation(SL["Sync_InProgress"]);

        try
        {
            List<PlaylistSearchResult> playlistsToImport = [];

            if (!_auth.IsAuthenticated)
            {
                await _dialog.ShowInfoAsync(SL["Library_SyncYoutube"], SL["Auth_NotSignedIn"]);
                return;
            }

            try
            {
                var ytPlaylists = await _youtube.GetUserPlaylistsByAuthAsync();
                ct.ThrowIfCancellationRequested();
                SyncProgress = 0.1;

                var filtered = ytPlaylists
                    .Where(p =>
                        !string.IsNullOrEmpty(p.YoutubeId) &&
                        p.YoutubeId != "LM" &&
                        p.YoutubeId != "VLLM" &&
                        !p.YoutubeId.StartsWith("RD"))
                    .ToList();

                playlistsToImport = [.. filtered.Select(p =>
            {
                var pid = new Core.Youtube.Playlists.PlaylistId(p.YoutubeId!);
                var thumbs = new List<Thumbnail>();
                if (!string.IsNullOrEmpty(p.ThumbnailUrl))
                    thumbs.Add(new Thumbnail(p.ThumbnailUrl, new Resolution(0, 0)));
                return new PlaylistSearchResult(pid, p.Name, null, thumbs);
            })];
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                if (!_isDisposed)
                {
                    await _dialog.ShowInfoAsync(
                        SL["Dialog_Error_Title"],
                        SL["Sync_Error_API"] + ": " + ex.Message);
                }
                return;
            }

            SyncProgress = 0.15;

            if (playlistsToImport.Count == 0)
            {
                var confirmSyncLikes = await _dialog.ConfirmAsync(
                    SL["Sync_ConfirmLikedOnly"] ?? "Плейлисты не найдены",
                    SL["Sync_NoPlaylistsFound_AskLiked"] ?? "Синхронизировать лайки?",
                    SL["Common_Yes"] ?? "Да",
                    SL["Common_No"] ?? "Нет");

                if (confirmSyncLikes)
                {
                    SyncStatus = SL["Sync_LikedSongs"];
                    await _manager.SyncLikedTracksAsync(ct);
                    await _dialog.ShowInfoAsync(
                        SL["Dialog_Done_Title"],
                        SL["Sync_Success_Msg_LikedOnly"] ?? "Понравившиеся песни синхронизированы.");
                }

                return;
            }

            ct.ThrowIfCancellationRequested();
            SyncStatus = SL["Sync_SelectPlaylists"];

            var allPlaylists = await _library.GetAllPlaylistsAsync(ct);
            var existingLocal = allPlaylists
                .Where(p => p.IsLocal)
                .GroupBy(p => p.Name, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
            var localNames = new HashSet<string>(existingLocal.Keys, StringComparer.Ordinal);

            var decisions = await _dialog.ShowSyncSelectionAsync(playlistsToImport, localNames);
            if (decisions.Count == 0 || _isDisposed) return;

            ct.ThrowIfCancellationRequested();
            SyncProgress = 0.2;
            SyncStatus = SL["Sync_ImportingPlaylists"];

            int importedCount = 0;
            int mergedCount = 0;
            int processed = 0;
            int totalToProcess = decisions.Count;

            foreach (var decision in decisions)
            {
                ct.ThrowIfCancellationRequested();
                if (_isDisposed) return;

                SyncStatus = string.Format(SL["Sync_ImportingPlaylist"], decision.Playlist.Title);

                var fullPlaylist = await _youtube.ImportPlaylistAsync(
                    decision.Playlist.Id.Value, _auth.IsAuthenticated, ct);

                if (fullPlaylist == null)
                {
                    processed++;
                    SyncProgress = 0.2 + (0.8 * processed / totalToProcess);
                    continue;
                }

                existingLocal.TryGetValue(decision.Playlist.Title, out var existing);

                if (decision.Action == MergeAction.Merge && existing != null)
                {
                    var existingTrackIds = await _library.GetPlaylistTrackIdsAsync(existing.Id, ct);
                    var existingTrackSet = new HashSet<string>(existingTrackIds, StringComparer.Ordinal);

                    bool tracksChanged = false;

                    // Cloud metadata / ownership / visibility / link-state
                    bool metadataChanged = ApplyMergedCloudMetadata(existing, fullPlaylist);

                    for (int i = 0; i < fullPlaylist.TrackIds.Count; i++)
                    {
                        var trackId = fullPlaylist.TrackIds[i];

                        if (existingTrackSet.Add(trackId))
                        {
                            existing.TrackIds.Add(trackId);
                            tracksChanged = true;
                        }

                        var t = await _library.GetTrackAsync(trackId, ct);
                        if (t != null && !t.InPlaylists.Contains(existing.Id))
                        {
                            t.InPlaylists.Add(existing.Id);
                            await _library.AddOrUpdateTrackAsync(t, ct);
                        }
                    }

                    // Важно: сохраняем не только при track delta, но и при metadata/link delta.
                    // Иначе новые поля модели так и не попадут в БД.
                    if (tracksChanged || metadataChanged)
                    {
                        await _library.AddOrUpdatePlaylistAsync(existing, ct);
                    }

                    mergedCount++;
                }
                else
                {
                    if (decision.Action == MergeAction.Duplicate && existing != null)
                        fullPlaylist.Name = $"{decision.Playlist.Title} ({SL["Sync_DuplicateName"]})";

                    if (_auth.IsAuthenticated)
                    {
                        fullPlaylist.SyncMode = PlaylistSyncMode.TwoWaySync;
                        fullPlaylist.YoutubeId = decision.Playlist.Id.Value;
                    }

                    await _library.AddOrUpdatePlaylistAsync(fullPlaylist, ct);
                    importedCount++;
                }

                processed++;
                SyncProgress = 0.2 + (0.8 * processed / totalToProcess);
            }

            SyncProgress = 1.0;
            SyncStatus = SL["Sync_Complete"];

            if (!_isDisposed)
            {
                await _notifications.ShowToastAsync(
                    "Sync_Complete_Title",
                    "Sync_Success_Msg",
                    NotificationSeverity.Success,
                    durationMs: 5000,
                    messageArgs: [importedCount, mergedCount]);
            }
        }
        catch (OperationCanceledException)
        {
            SyncStatus = SL["Sync_Cancelled"];
        }
        catch (Exception ex)
        {
            Log.Error($"[Library] Sync error: {ex.Message}");
            if (!_isDisposed)
            {
                await _notifications.ShowToastAsync(
                    "Dialog_Error_Title",
                    "Sync_Error_API",
                    NotificationSeverity.Error,
                    durationMs: 6000,
                    messageArgs: [ex.Message]);
            }
        }
        finally
        {
            try
            {
                await Task.Delay(300);
            }
            catch
            {
            }

            try
            {
                _mainWindow.UnlockNavigation();
            }
            catch (Exception ex)
            {
                Log.Warn($"[Library] UnlockNavigation error: {ex.Message}");
            }

            if (!_isDisposed)
            {
                IsSyncing = false;
                SyncProgress = 0;
                SyncStatus = string.Empty;

                try
                {
                    await LoadPlaylistsAsync();
                }
                catch (Exception ex)
                {
                    Log.Warn($"[Library] Post-sync reload error: {ex.Message}");
                }
            }
        }
    }

    /// <summary>
    /// Применяет к существующему локальному плейлисту облачные метаданные,
    /// полученные при импорте/синхронизации из YouTube.
    /// Возвращает <c>true</c>, если были изменены хотя бы какие-либо поля.
    /// </summary>
    /// <param name="existing">Локальный плейлист из базы данных.</param>
    /// <param name="incoming">Импортированный/облачный плейлист со свежими метаданными.</param>
    /// <returns><c>true</c>, если метаданные были обновлены; иначе <c>false</c>.</returns>
    private bool ApplyMergedCloudMetadata(
        Core.Models.Playlist existing,
        Core.Models.Playlist incoming)
    {
        bool changed = false;

        // 1. Автор плейлиста
        if (!string.Equals(existing.Author, incoming.Author, StringComparison.Ordinal))
        {
            existing.Author = incoming.Author;
            changed = true;
        }

        // 2. Ссылка на YouTube-канал владельца
        if (!string.Equals(existing.OwnerChannelId, incoming.OwnerChannelId, StringComparison.Ordinal))
        {
            existing.OwnerChannelId = incoming.OwnerChannelId;
            changed = true;
        }

        // 3. Статус владения (Mine, Foreign, System)
        if (existing.Ownership != incoming.Ownership)
        {
            existing.Ownership = incoming.Ownership;
            changed = true;
        }

        // 4. Статус приватности (Public, Private, Unlisted)
        if (existing.Visibility != incoming.Visibility)
        {
            existing.Visibility = incoming.Visibility;
            changed = true;
        }

        // 5. Ожидаемое количество треков в облаке
        if (existing.CloudTrackCount != incoming.CloudTrackCount)
        {
            existing.CloudTrackCount = incoming.CloudTrackCount;
            changed = true;
        }

        // 6. Количество просмотров на YouTube / YouTube Music
        if (existing.ViewCount != incoming.ViewCount)
        {
            existing.ViewCount = incoming.ViewCount;
            changed = true;
        }

        // 7. Дата последнего обновления плейлиста
        if (existing.ReleaseDate != incoming.ReleaseDate)
        {
            existing.ReleaseDate = incoming.ReleaseDate;
            changed = true;
        }

        // 8. Обложка плейлиста (YouTube меняет query-параметры обложек, но базовые URL совпадают)
        if (!string.Equals(existing.ThumbnailUrl, incoming.ThumbnailUrl, StringComparison.Ordinal))
        {
            existing.ThumbnailUrl = incoming.ThumbnailUrl;
            changed = true;
        }

        // 9. Описание плейлиста
        if (!string.Equals(existing.Description, incoming.Description, StringComparison.Ordinal))
        {
            existing.Description = incoming.Description;
            changed = true;
        }

        // 10. Облачная привязка и режим синхронизации при наличии авторизации
        if (_auth.IsAuthenticated)
        {
            if (!string.Equals(existing.YoutubeId, incoming.YoutubeId, StringComparison.Ordinal))
            {
                existing.YoutubeId = incoming.YoutubeId;
                changed = true;
            }

            if (existing.SyncMode != PlaylistSyncMode.TwoWaySync)
            {
                existing.SyncMode = PlaylistSyncMode.TwoWaySync;
                changed = true;
            }
        }

        // 11. Сброс флага недоступности облака
        if (existing.IsCloudUnavailable)
        {
            existing.IsCloudUnavailable = false;
            changed = true;
        }

        // Временные метки обновляются при каждом факте сверки/синхронизации
        existing.LastSyncedAtUtc = DateTime.UtcNow;
        existing.UpdatedAt = DateTime.Now;

        return changed;
    }

    #endregion

    #region Статистика

    /// <summary>
    /// Анимирует статистику от предыдущих значений к текущим с плавной интерполяцией.
    /// ИСПОЛЬЗУЕТ ОПТИМИЗИРОВАННЫЙ O(1) ЗАПРОС длительности!
    /// </summary>
    private async Task UpdateStatsAnimatedAsync()
    {
        if (_isDisposed) return;

        _statsAnimCts?.Cancel();
        _statsAnimCts?.Dispose();
        _statsAnimCts = new CancellationTokenSource();
        var ct = _statsAnimCts.Token;

        var targetPlaylists = Playlists.Count;
        var targetTracks = Playlists.Sum(p => p.TrackCount);

        // ═══ O(1) ЗАПРОС вместо N+1 ═══
        long totalTicks = 0;
        try
        {
            totalTicks = await _library.GetTotalLibraryDurationAsync(ct);
        }
        catch (Exception ex)
        {
            Log.Error($"[Library] Ошибка при расчете длительности: {ex.Message}");
        }

        if (ct.IsCancellationRequested || _isDisposed) return;
        var totalDuration = TimeSpan.FromTicks(totalTicks);

        var avgTrack = targetTracks > 0
            ? TimeSpan.FromTicks(totalDuration.Ticks / targetTracks)
            : TimeSpan.Zero;
        var avgPlaylist = targetPlaylists > 0
            ? TimeSpan.FromTicks(totalDuration.Ticks / targetPlaylists)
            : TimeSpan.Zero;

        IsStatsVisible = true;

        int startPlaylists = _prevPlaylistCount;
        int startTracks = _prevTrackCount;

        int diff = Math.Abs(targetPlaylists - startPlaylists)
                 + Math.Abs(targetTracks - startTracks);
        int steps = diff <= 3 ? 15 : 25;

        for (int i = 1; i <= steps; i++)
        {
            if (ct.IsCancellationRequested || _isDisposed) return;

            double t = (double)i / steps;
            double ease = 1 - Math.Pow(1 - t, 3);

            int currentPlaylists = startPlaylists
                + (int)Math.Round((targetPlaylists - startPlaylists) * ease);
            int currentTracks = startTracks
                + (int)Math.Round((targetTracks - startTracks) * ease);

            PlaylistCountText = SL.GetPlural("Library_PlaylistWord", currentPlaylists);
            TotalTracksText = SL.GetPlural("Library_TrackWord", currentTracks);

            TotalDurationText = FormatDurationLocalized(totalDuration);

            if (i == steps)
            {
                PlaylistCountText = SL.GetPlural("Library_PlaylistWord", targetPlaylists);
                TotalTracksText = SL.GetPlural("Library_TrackWord", targetTracks);
                AvgTrackDurationText = $"⌀ {SL["Library_AvgTrack"]}: {FormatDurationShort(avgTrack)}";
                AvgPlaylistDurationText = $"⌀ {SL["Library_AvgPlaylist"]}: {FormatDurationLocalized(avgPlaylist)}";
            }
            else
            {
                AvgTrackDurationText = "";
                AvgPlaylistDurationText = "";
            }

            try { await Task.Delay(16, ct); }
            catch (OperationCanceledException) { break; }
        }

        _prevPlaylistCount = targetPlaylists;
        _prevTrackCount = targetTracks;
    }

    private static string FormatDurationLocalized(TimeSpan ts)
    {
        var h = (int)ts.TotalHours;
        var m = ts.Minutes;
        var s = ts.Seconds;

        if (h > 0) return $"{h} {SL["Time_Hours_Short"]} {m} {SL["Time_Minutes_Short"]}";
        if (m > 0) return $"{m} {SL["Time_Minutes_Short"]}";
        return $"{s} {SL["Time_Seconds_Short"]}";
    }

    private static string FormatDurationShort(TimeSpan ts)
    {
        return ts.TotalHours >= 1 ? ts.ToString(@"h\:mm\:ss") : ts.ToString(@"m\:ss");
    }

    #endregion

    #region Полная загрузка

    /// <summary>
    /// Полная перезагрузка списка плейлистов с diff-алгоритмом и UI-Yielding.
    /// 
    /// <para><b>Оптимизация рендера:</b></para>
    /// <list type="bullet">
    ///   <item>Первые 12 карточек появляются мгновенно (покрывают viewport)</item>
    ///   <item>Остальные добавляются батчами по 4 с yield между ними</item>
    ///   <item>Dispatcher.InvokeAsync(Background) реально отдаёт UI-поток</item>
    /// </list>
    /// </summary>
    private async Task LoadPlaylistsAsync()
    {
        if (_isDisposed) return;

        _staggerCts?.Cancel();
        _staggerCts?.Dispose();
        _staggerCts = new CancellationTokenSource();
        var ct = _staggerCts.Token;

        IsStatsVisible = false;

        // Выборка из SQLite строго в пуле потоков
        var allPlaylistsWithCounts = await Task.Run(
            () => _library.GetAllPlaylistsWithCountsAsync(), ct).ConfigureAwait(false);

        if (_isDisposed || ct.IsCancellationRequested) return;

        var sorted = allPlaylistsWithCounts
            .OrderByDescending(x => x.Playlist.Id == LibraryService.LikedPlaylistId)
            .ThenByDescending(x => x.Playlist.IsLocal)
            .ThenBy(x => x.Playlist.Name)
            .ToList();

        // Переключение на UI-поток только для обновления ObservableCollection
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (ct.IsCancellationRequested || _isDisposed) return;

            var existingDict = Playlists.ToDictionary(vm => vm.Id);
            var newIdSet = new HashSet<string>(sorted.Select(x => x.Playlist.Id));

            for (int i = Playlists.Count - 1; i >= 0; i--)
            {
                if (!newIdSet.Contains(Playlists[i].Id))
                {
                    Playlists[i].Dispose();
                    Playlists.RemoveAt(i);
                }
            }

            for (int i = 0; i < sorted.Count; i++)
            {
                var (playlist, trackCount) = sorted[i];

                if (existingDict.TryGetValue(playlist.Id, out var existingVm))
                {
                    existingVm.UpdateFrom(playlist, trackCount);
                }
                else
                {
                    var vm = CreatePlaylistCardVm(playlist, trackCount);
                    vm.Show();

                    if (i >= Playlists.Count)
                        Playlists.Add(vm);
                    else
                        Playlists.Insert(i, vm);
                }
            }

            _loadedOwnerId = _auth.State.DisplayId;
            UpdateStatsInBackground();
        }, DispatcherPriority.Normal, ct);
    }

    #endregion

    private PlaylistCardViewModel CreatePlaylistCardVm(
     Core.Models.Playlist playlist, int trackCount)
    {
        return new PlaylistCardViewModel(
            _auth,
            _playerControl,
            _library,
            _audio,
            playlist,
            trackCount,
            onOpen: _mainWindow.NavigateToPlaylist,
            addToQueueAction: async (p) =>
            {
                var tracks = await _library.GetPlaylistTracksAsync(p.Id);
                _audio.EnqueuePlaylistWithNotification(tracks, p.Name);
            },
            playAction: async (p) =>
            {
                // Умная логика Play/Pause, если плейлист уже воспроизводится в чистом виде
                bool isActive = _playerControl.ActivePlaylistId == p.Id;
                if (isActive)
                {
                    var queue = _audio.Queue;
                    var trackIds = await _library.GetPlaylistTrackIdsAsync(p.Id);

                    bool isPure = queue.Count == trackIds.Count;
                    if (isPure)
                    {
                        var queueSet = new HashSet<string>(queue.Select(t => t.Id), StringComparer.Ordinal);
                        foreach (var id in trackIds)
                        {
                            if (!queueSet.Contains(id))
                            {
                                isPure = false;
                                break;
                            }
                        }
                    }

                    if (isPure)
                    {
                        await _playerControl.PlayPauseAsync();
                        return;
                    }
                }

                // Полная очистка очереди и запуск с фиксацией ID плейлиста в плеере
                var tracks = await _library.GetPlaylistTracksAsync(p.Id);
                if (tracks.Count > 0)
                {
                    _playerControl.SetShuffleEnabled(false);
                    _playerControl.SetActivePlaylistId(p.Id); // Фиксируем источник для иконок и анимации
                    await _audio.StartQueueAsync(tracks, tracks[0]);
                }
            },
            onDelete: DeletePlaylistAsync,
            onEdit: EditPlaylistFromCardAsync);
    }

    #region Редактирование и удаление

    private async Task EditPlaylistFromCardAsync(Core.Models.Playlist playlist)
    {
        if (_isDisposed) return;

        await _editService.EditPlaylistAsync(
            playlist.Id,
            _mainWindow.LockNavigation,
            _mainWindow.UnlockNavigation);
    }

    private async Task DeletePlaylistAsync(string playlistId)
    {
        if (_isDisposed) return;
        var playlist = await _library.GetPlaylistAsync(playlistId);
        if (playlist == null) return;

        if (playlistId == "liked")
        {
            await _dialog.ShowInfoAsync(SL["Dialog_Error_Title"], SL["Playlist_CannotDeleteLiked"]);
            return;
        }

        var confirmed = await _dialog.ConfirmAsync(
            SL["Dialog_Confirm_Title"],
            string.Format(SL["Playlist_DeleteConfirm"], playlist.Name),
            SL["Button_Delete"], SL["Button_Cancel"]);

        if (!confirmed) return;

        _mainWindow.LockNavigation(SL["Playlist_Deleting"]);
        try
        {
            await _manager.DeletePlaylistAsync(playlistId);
        }
        finally { _mainWindow.UnlockNavigation(); }
    }

    private void OnPlaylistsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_isDisposed) return;
        HasPlaylists = Playlists.Count > 0;
    }

    #endregion

    #region Dispose

    protected override void Dispose(bool disposing)
    {
        if (_isDisposed) return;
        if (disposing)
        {
            _isDisposed = true;

            Playlists.CollectionChanged -= OnPlaylistsCollectionChanged;

            _staggerCts?.Cancel();
            _staggerCts?.Dispose();

            _statsAnimCts?.Cancel();
            _statsAnimCts?.Dispose();

            foreach (var vm in Playlists)
                vm.Dispose();

            Playlists.Clear();

            _auth.OnAuthStateChanged -= OnAuthChanged;

            _syncCts?.Cancel();
            _syncCts?.Dispose();
        }
        base.Dispose(disposing);
    }

    #endregion
}
