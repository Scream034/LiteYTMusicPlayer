using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Reactive;
using System.Reactive.Linq;
using Avalonia.Threading;
using LMP.Core.Audio.Http;
using LMP.Core.Helpers.Extensions;
using LMP.Core.Models;
using LMP.Core.Services;
using LMP.Core.Youtube.Search;
using LMP.UI.Features.Shared;
using LMP.UI.ViewModels;
using ReactiveUI;

namespace LMP.UI.Features.Search;

/// <summary>
/// Элемент подсказки поисковой строки (история либо сетевое предложение YouTube).
/// </summary>
public sealed record SearchSuggestionItem(string Text, bool IsFromHistory, SearchViewModel Owner);

/// <summary>
/// ViewModel экрана поиска треков с поддержкой Ghost Text, in-place обновлением подсказок и приоритизацией прямых URL.
/// </summary>
public sealed partial class SearchViewModel : TrackListPaginatedViewModel
{
    #region Constants

    /// <inheritdoc />
    protected override bool HandlesAccountChanges => true;

    private const int DebounceMs = 300;
    private const int MaxResults = 300;

    #endregion

    #region Fields

    private readonly YoutubeProvider _youtube;
    private readonly SearchCacheService _searchCache;
    private readonly ImageCacheService _imageCache;
    private readonly HashSet<string> _dismissedSuggestions = new(StringComparer.OrdinalIgnoreCase);

    private string _currentQuery = string.Empty;
    private CancellationTokenSource? _searchCts;
    private CancellationTokenSource? _suggestCts;
    private YoutubeProvider.SearchSession? _searchSession;
    private DateTime _lastSearchTime = DateTime.MinValue;

    private bool _isDisposed;
    private int _displayTrackCount;

    #endregion

    #region Properties

    private int InitialBatchSize => LibService.Settings.LoadBatchSize > 0
        ? LibService.Settings.LoadBatchSize
        : 25;

    private int ScrollBatchSize => LibService.Settings.SearchBatchSize > 0
        ? LibService.Settings.SearchBatchSize
        : 25;

    [Reactive] public partial string SearchQuery { get; set; } = string.Empty;

    /// <summary>
    /// Полный текст автодополнения (Ghost Text), применяемый при нажатии Tab / стрелки вправо.
    /// </summary>
    [Reactive] public partial string GhostText { get; private set; } = string.Empty;

    /// <summary>
    /// Суффикс автодополнения (хвост подсказки, отображаемый следом за введенным текстом).
    /// </summary>
    [Reactive] public partial string GhostTextSuffix { get; private set; } = string.Empty;

    /// <summary>
    /// Флаг наличия доступного суффикса Ghost Text для отображения полупрозрачного слоя.
    /// </summary>
    public bool HasGhostText => !string.IsNullOrEmpty(GhostTextSuffix);

    /// <summary>
    /// Источник поиска: YouTube Music, YouTube, Local.
    /// </summary>
    [Reactive] public partial ContentSource Source { get; set; } = ContentSource.YouTubeMusic;

    [Reactive] public partial bool HasResults { get; private set; }
    [Reactive] public partial string? ErrorMessage { get; private set; }
    [Reactive] public partial bool IsFromCache { get; private set; }
    [Reactive] public partial bool IsOfflineMode { get; private set; }

    /// <summary>
    /// Точное число отображаемых треков (реактивно слушает UI-коллекцию Items).
    /// </summary>
    public int DisplayTrackCount
    {
        get => _displayTrackCount;
        private set => this.RaiseAndSetIfChanged(ref _displayTrackCount, value);
    }

    /// <summary>
    /// Значение счетчика слева от строки поиска.
    /// Отображает число найденных треков, при их отсутствии — число доступных подсказок либо 0.
    /// </summary>
    public int BadgeCount => DisplayTrackCount > 0 ? DisplayTrackCount : Suggestions.Count;

    /// <summary>
    /// Индикатор видимости счетчика. Зафиксирован в true для предотвращения горизонтальных сдвигов интерфейса.
    /// </summary>
    public bool IsBadgeVisible => true;

    /// <summary>
    /// Всплывающая подсказка для счетчика треков/подсказок.
    /// </summary>
    public string BadgeTooltip => DisplayTrackCount > 0
        ? string.Format(LocalizationService.Instance["Search_BadgeTooltip_Tracks"] ?? "Найдено треков: {0}", DisplayTrackCount)
        : Suggestions.Count > 0
            ? string.Format(LocalizationService.Instance["Search_BadgeTooltip_Suggestions"] ?? "Подсказок: {0}", Suggestions.Count)
            : (LocalizationService.Instance["Search_BadgeTooltip_Empty"] ?? "Нет элементов");

    /// <summary>
    /// Текст-заглушка ленты подсказок, когда подсказки отсутствуют.
    /// Сохраняет высоту строки и исключает вертикальные сдвиги макета.
    /// </summary>
    public string RibbonPlaceholderText
    {
        get
        {
            var trimmed = SearchQuery.Trim();
            if (!string.IsNullOrEmpty(trimmed) && YoutubeProvider.DetectQueryType(trimmed) != QueryType.Search)
                return LocalizationService.Instance["Search_DirectUrlHint"] ?? "Прямая ссылка на трек или плейлист";

            return string.IsNullOrWhiteSpace(trimmed)
                ? (LocalizationService.Instance["Search_NoHistoryPlaceholder"] ?? "История поиска и популярные запросы")
                : (LocalizationService.Instance["Search_NoSuggestionsPlaceholder"] ?? "Нет подсказок для данного запроса");
        }
    }

    /// <summary>
    /// Кнопка принудительного обновления: видна только при наличии кэшированных результатов.
    /// </summary>
    public bool ShowForceSearchButton =>
        LibService.Settings.EnableSearchCache && IsFromCache && !IsLoading;

    /// <summary>
    /// Локально сохраненная история поиска.
    /// </summary>
    public ObservableCollection<string> RecentSearches { get; } = [];

    /// <summary>
    /// Флаг наличия сохранённых запросов в локальной истории. Управляет видимостью кнопки-метлы.
    /// </summary>
    public bool HasRecentSearches => RecentSearches.Count > 0;

    /// <summary>
    /// Комбинированный список подсказок (История + YouTube Suggest).
    /// </summary>
    public ObservableCollection<SearchSuggestionItem> Suggestions { get; } = [];

    /// <summary>
    /// Флаг наличия подсказок для отображения чипов в горизонтальной ленте.
    /// </summary>
    public bool HasSuggestions => Suggestions.Count > 0;

    /// <summary>
    /// Флаг наличия истории в подсказках (zero-alloc проход без LINQ).
    /// </summary>
    public bool HasHistoryInSuggestions
    {
        get
        {
            for (int i = 0; i < Suggestions.Count; i++)
            {
                if (Suggestions[i].IsFromHistory)
                    return true;
            }
            return false;
        }
    }

    #endregion

    #region Commands

    public ReactiveCommand<Unit, Unit> SearchCommand { get; }
    public ReactiveCommand<Unit, Unit> ForceSearchCommand { get; }
    public ReactiveCommand<string, Unit> SuggestionClickCommand { get; }
    public ReactiveCommand<string, Unit> RemoveSuggestionCommand { get; }
    public ReactiveCommand<string, Unit> RemoveHistoryCommand => RemoveSuggestionCommand;
    public ReactiveCommand<Unit, Unit> ClearHistoryCommand { get; }
    public ReactiveCommand<Unit, Unit> ClearQueryCommand { get; }
    public ReactiveCommand<Unit, Unit> CompleteGhostTextCommand { get; }
    public ReactiveCommand<string, Unit> SetSourceCommand { get; }

    #endregion

    #region UI Presentation State

    /// <summary>
    /// Флаг активности сетевой операции (первичная загрузка или постраничная докачка).
    /// </summary>
    public bool IsBusy => IsLoading || IsFetchingFromNetwork;

    public bool IsSourceYtm => Source == ContentSource.YouTubeMusic;
    public bool IsSourceYt => Source == ContentSource.YouTube;
    public bool IsSourceLocal => Source == ContentSource.Local;

    /// <summary>
    /// Определяет необходимость показа заглушки «Ничего не найдено».
    /// </summary>
    public bool ShowEmptyState => !IsLoading && !HasResults && !string.IsNullOrWhiteSpace(_currentQuery);

    public void ClearQuery()
    {
        SearchQuery = string.Empty;
        UpdateLocalSuggestionsAndGhostText(string.Empty);
    }

    public void OpenHistoryIfAvailable()
    {
        UpdateLocalSuggestionsAndGhostText(SearchQuery);
    }

    /// <summary>
    /// Дополняет поле ввода текстом найденного Ghost Text.
    /// </summary>
    public void CompleteGhostText()
    {
        if (_isDisposed || string.IsNullOrEmpty(GhostText)) return;

        if (GhostText.StartsWith(SearchQuery, StringComparison.OrdinalIgnoreCase) &&
            GhostText.Length > SearchQuery.Length)
        {
            SearchQuery = GhostText;
        }
    }

    #endregion

    #region Constructor

    public SearchViewModel(
        AudioEngine audio,
        DownloadService downloads,
        TrackViewModelFactory vmFactory,
        YoutubeProvider youtube,
        SearchCacheService searchCache,
        ImageCacheService imageCache)
        : base(audio, downloads, vmFactory)
    {
        _youtube = youtube;
        _searchCache = searchCache;
        _imageCache = imageCache;

        var canSearch = this.WhenAnyValue(
            x => x.SearchQuery, x => x.IsLoading,
            static (q, loading) => !string.IsNullOrWhiteSpace(q) && !loading);

        SearchCommand = CreateCommand(ReactiveCommand.CreateFromTask(
            () => ExecuteSearchAsync(forceNetwork: false, bypassDebounce: true),
            canSearch));

        var canForceSearch = this.WhenAnyValue(
            x => x.IsFromCache, x => x.IsLoading,
            static (cache, loading) => cache && !loading);

        ForceSearchCommand = CreateCommand(ReactiveCommand.CreateFromTask(
            () => ExecuteSearchAsync(forceNetwork: true, bypassDebounce: true),
            canForceSearch));

        SuggestionClickCommand = CreateCommand(ReactiveCommand.CreateFromTask<string>(async q =>
        {
            if (_isDisposed || string.IsNullOrEmpty(q)) return;
            SearchQuery = q;
            await ExecuteSearchAsync(forceNetwork: false, bypassDebounce: true);
        }));

        CompleteGhostTextCommand = CreateCommand(ReactiveCommand.Create(CompleteGhostText));

        RemoveSuggestionCommand = CreateCommand(ReactiveCommand.Create<string>(q =>
        {
            if (_isDisposed || string.IsNullOrEmpty(q)) return;

            _dismissedSuggestions.Add(q);

            for (int i = RecentSearches.Count - 1; i >= 0; i--)
            {
                if (string.Equals(RecentSearches[i], q, StringComparison.OrdinalIgnoreCase))
                    RecentSearches.RemoveAt(i);
            }

            UpdateHistoryStorage();
            this.RaisePropertyChanged(nameof(HasRecentSearches));

            for (int i = Suggestions.Count - 1; i >= 0; i--)
            {
                if (string.Equals(Suggestions[i].Text, q, StringComparison.OrdinalIgnoreCase))
                {
                    Suggestions.RemoveAt(i);
                    break;
                }
            }

            CalculateGhostText(SearchQuery, Suggestions);
            NotifySuggestionsChanged();
        }));

        ClearHistoryCommand = CreateCommand(ReactiveCommand.Create(() =>
        {
            if (_isDisposed) return;
            RecentSearches.Clear();
            _dismissedSuggestions.Clear();
            UpdateHistoryStorage();
            this.RaisePropertyChanged(nameof(HasRecentSearches));
            UpdateLocalSuggestionsAndGhostText(SearchQuery);
        }));

        ClearQueryCommand = CreateCommand(ReactiveCommand.Create(ClearQuery));

        SetSourceCommand = CreateCommand(ReactiveCommand.Create<string>(sourceStr =>
        {
            if (_isDisposed) return;
            if (Enum.TryParse<ContentSource>(sourceStr, true, out var result))
                Source = result;
        }));

        // 1. Мгновенная синхронная реакция на ввод: история и Ghost Text вычисляются на 0-м кадре без ожидания таймеров
        this.WhenAnyValue(x => x.SearchQuery)
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(query =>
            {
                if (_isDisposed) return;
                UpdateLocalSuggestionsAndGhostText(query);
            })
            .DisposeWith(Disposables);

        // 2. Дебаунс 200 мс ИСКЛЮЧИТЕЛЬНО для сетевого InnerTube/Suggest API
        this.WhenAnyValue(x => x.SearchQuery)
            .Throttle(TimeSpan.FromMilliseconds(200))
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(query =>
            {
                if (_isDisposed) return;
                FetchRemoteSuggestionsThrottled(query);
            })
            .DisposeWith(Disposables);

        // Синхронизация счетчика отображаемых треков на главном потоке
        Observable.FromEventPattern<NotifyCollectionChangedEventHandler, NotifyCollectionChangedEventArgs>(
                h => ((INotifyCollectionChanged)Items).CollectionChanged += h,
                h => ((INotifyCollectionChanged)Items).CollectionChanged -= h)
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(_ =>
            {
                DisplayTrackCount = Items.Count;
                NotifyBadgeChanged();
            })
            .DisposeWith(Disposables);

        this.WhenAnyValue(x => x.IsFromCache, x => x.IsLoading)
            .Subscribe(_ => this.RaisePropertyChanged(nameof(ShowForceSearchButton)))
            .DisposeWith(Disposables);

        // Единая подписка на визуальное переключение источника
        this.WhenAnyValue(x => x.Source)
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(_ =>
            {
                this.RaisePropertyChanged(nameof(IsSourceYtm));
                this.RaisePropertyChanged(nameof(IsSourceYt));
                this.RaisePropertyChanged(nameof(IsSourceLocal));
                IsOfflineMode = Source == ContentSource.Local;
            })
            .DisposeWith(Disposables);

        // Переключение источника обходит debounce (действие пользователя намеренное)
        this.WhenAnyValue(x => x.Source)
            .Skip(1)
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(async _ =>
            {
                if (_isDisposed) return;
                if (!string.IsNullOrWhiteSpace(SearchQuery))
                    await ExecuteSearchAsync(forceNetwork: false, bypassDebounce: true);
            })
            .DisposeWith(Disposables);

        this.WhenAnyValue(x => x.IsLoading, x => x.IsFetchingFromNetwork, static (l, f) => l || f)
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(_ => this.RaisePropertyChanged(nameof(IsBusy)))
            .DisposeWith(Disposables);

        this.WhenAnyValue(x => x.IsLoading, x => x.HasResults, static (l, r) => !l && !r)
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(_ => this.RaisePropertyChanged(nameof(ShowEmptyState)))
            .DisposeWith(Disposables);

        IsLoading = false;
    }

    private void NotifyBadgeChanged()
    {
        this.RaisePropertyChanged(nameof(BadgeCount));
        this.RaisePropertyChanged(nameof(IsBadgeVisible));
        this.RaisePropertyChanged(nameof(BadgeTooltip));
    }

    private void NotifySuggestionsChanged()
    {
        NotifyBadgeChanged();
        this.RaisePropertyChanged(nameof(HasSuggestions));
        this.RaisePropertyChanged(nameof(HasHistoryInSuggestions));
        this.RaisePropertyChanged(nameof(RibbonPlaceholderText));
        this.RaisePropertyChanged(nameof(HasGhostText));
    }

    #endregion

    #region Navigation

    public override async Task OnNavigatedToAsync()
    {
        if (_isDisposed) return;
        await LoadHistoryAsync();
        await base.OnNavigatedToAsync();
    }

    #endregion

    #region TrackListPaginatedViewModel Implementation

    protected override void OnPlay(TrackInfo track)
    {
        if (_isDisposed) return;
        _ = Audio.StartQueueAsync([track], track);
        _ = LibService.AddToRecentlyPlayedAsync(track);
    }

    protected override async Task<List<TrackInfo>> FetchMoreFromNetworkAsync(CancellationToken ct)
    {
        if (_isDisposed || Source == ContentSource.Local || TotalCount >= MaxResults)
            return [];

        if (_searchSession == null && !string.IsNullOrEmpty(_currentQuery))
        {
            var existingIds = GetLoadedItemsIds();
            _searchSession = _youtube.CreateSearchSession(
                _currentQuery, MaxResults, GetSearchFilter(), existingIds);
            Log.Info($"[Search] Continuation session created from cache ({TotalCount} existing items)");
        }

        if (_searchSession == null || !_searchSession.HasMore)
        {
            Log.Debug("[Search] FetchMore skipped: no active session or reached end.");
            SetCanFetchMore(false);
            return [];
        }

        var sw = Stopwatch.StartNew();
        Log.Info($"[Search] FetchMore started: current items={TotalCount}, requesting batch size={ScrollBatchSize}...");

        try
        {
            var newTracks = await _searchSession.FetchNextBatchAsync(ScrollBatchSize, ct);
            if (ct.IsCancellationRequested || _isDisposed) return [];

            if (Source == ContentSource.YouTubeMusic)
            {
                for (int i = 0; i < newTracks.Count; i++)
                    newTracks[i].IsMusic = true;
            }

            if (newTracks.Count > 0)
            {
                AudioSourceFactory.GlobalCache?.HydrateCacheStatus(newTracks);

                if (LibService.Settings.EnableSearchCache)
                {
                    var snapshot = GetItemsSnapshot();
                    var all = new List<TrackInfo>(snapshot.Count + newTracks.Count);
                    all.AddRange(snapshot);
                    all.AddRange(newTracks);
                    _ = _searchCache.SetAsync(_currentQuery, SourceToSearchSource(), all);

                    var imageUrls = newTracks.Take(10)
                        .Select(static t => t.ThumbnailUrl)
                        .Where(static u => !string.IsNullOrEmpty(u));
                    _ = _imageCache.PrefetchAsync(imageUrls!, ct);
                }
            }

            sw.Stop();
            Log.Info($"[Search] FetchMore finished: received {newTracks.Count} tracks in {sw.ElapsedMilliseconds}ms, session.HasMore={_searchSession.HasMore}");

            if (newTracks.Count == 0 || !_searchSession.HasMore)
                SetCanFetchMore(false);

            return newTracks;
        }
        catch (OperationCanceledException)
        {
            Log.Debug("[Search] FetchMore was canceled.");
            return [];
        }
        catch (HttpRequestException ex)
        {
            Log.Error($"[Search] Network error during FetchMore: {ex.Message}");
            ErrorMessage = SL["Search_NetworkError"];
            return [];
        }
        catch (Exception ex)
        {
            Log.Error($"[Search] FetchMore unexpected failure: {ex.Message}");
            return [];
        }
    }

    protected override void OnAccountChanged()
    {
        base.OnAccountChanged();

        SearchQuery = string.Empty;
        _currentQuery = string.Empty;
        GhostText = string.Empty;
        GhostTextSuffix = string.Empty;
        ErrorMessage = null;
        IsFromCache = false;
        HasResults = false;

        try { _searchSession?.Dispose(); } catch { }
        _searchSession = null;

        ClearItems();
        _ = Dispatcher.UIThread.InvokeAsync(LoadHistoryAsync, DispatcherPriority.Background);
    }

    #endregion

    #region Search Logic

    private SearchFilter GetSearchFilter() => Source switch
    {
        ContentSource.YouTubeMusic => SearchFilter.MusicSong,
        ContentSource.YouTube => SearchFilter.Video,
        ContentSource.Local => SearchFilter.None,
        _ => SearchFilter.MusicSong
    };

    private SearchSource SourceToSearchSource() => Source switch
    {
        ContentSource.YouTubeMusic => SearchSource.YouTubeMusic,
        ContentSource.YouTube => SearchSource.YouTube,
        _ => SearchSource.YouTube
    };

    private bool CanExecuteSearch()
    {
        var now = DateTime.UtcNow;
        if ((now - _lastSearchTime).TotalMilliseconds < DebounceMs) return false;
        _lastSearchTime = now;
        return true;
    }

    private async Task ExecuteSearchAsync(bool forceNetwork, bool bypassDebounce = false)
    {
        if (_isDisposed) return;
        if (!forceNetwork && !bypassDebounce && !CanExecuteSearch()) return;

        _lastSearchTime = DateTime.UtcNow;
        CancellationTokenSource? currentCts = null;

        try
        {
            _searchCts?.Cancel();
            _searchCts?.Dispose();
            _searchCts = new CancellationTokenSource();
            currentCts = _searchCts;
            var ct = currentCts.Token;

            try { _searchSession?.Dispose(); } catch { }
            _searchSession = null;

            CancelLoading();
            IsLoading = true;
            ErrorMessage = null;
            IsFromCache = false;
            HasResults = false;

            _currentQuery = SearchQuery.Trim();
            AddToHistory(_currentQuery);

            // 1. ПРИОРИТЕТНАЯ ПРОВЕРКА URL: исполняется всегда, независимо от выбранного источника
            var queryType = YoutubeProvider.DetectQueryType(_currentQuery);

            if (queryType == QueryType.DirectUrl)
            {
                await HandleDirectUrlAsync(ct);
                return;
            }

            if (queryType == QueryType.Playlist)
            {
                await HandlePlaylistAsync(ct);
                return;
            }

            // 2. Локальный поиск по SQLite
            if (Source == ContentSource.Local)
            {
                await HandleLocalSearchAsync(ct);
                return;
            }

            // 3. Сетевой поиск YouTube / YouTube Music
            await HandleSearchAsync(ct, forceNetwork);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (!_isDisposed)
            {
                ErrorMessage = ex.Message;
                Log.Error($"[Search] Error executing search: {ex}");
            }
        }
        finally
        {
            if (!_isDisposed
                && currentCts == _searchCts
                && currentCts != null
                && !currentCts.IsCancellationRequested)
            {
                IsLoading = false;
                IsFetchingFromNetwork = false;
            }
        }
    }

    private async Task HandleLocalSearchAsync(CancellationToken ct)
    {
        if (_isDisposed) return;

        var filtered = string.IsNullOrWhiteSpace(_currentQuery)
            ? await LibService.GetLocalTracksAsync(MaxResults, 0, ct)
            : await LibService.SearchLocalTracksAsync(_currentQuery, MaxResults, ct);

        ct.ThrowIfCancellationRequested();
        if (_isDisposed) return;

        await InitializeItemsAsync(filtered, canFetchMore: false);
        HasResults = filtered.Count > 0;

        if (!HasResults)
        {
            var localCount = await LibService.GetLocalTrackCountAsync(ct);
            ErrorMessage = localCount == 0 ? SL["Search_NoLocalFiles"] : SL["Search_NoResults"];
        }
    }

    private async Task HandleDirectUrlAsync(CancellationToken ct)
    {
        if (_isDisposed) return;

        var track = await _youtube.GetTrackByUrlAsync(_currentQuery);
        ct.ThrowIfCancellationRequested();
        if (_isDisposed) return;

        var tracks = track != null ? [track] : new List<TrackInfo>();
        await InitializeItemsAsync(tracks, canFetchMore: false);

        if (track != null && LibService.Settings.AutoPlayOnUrlPaste)
            _ = Audio.PlayTrackAsync(track);

        HasResults = tracks.Count > 0;
        if (!HasResults) ErrorMessage = SL["Search_NoResults"];
    }

    private async Task HandlePlaylistAsync(CancellationToken ct)
    {
        if (_isDisposed) return;

        IsFetchingFromNetwork = true;
        var playlist = await _youtube.GetPlaylistAsync(_currentQuery);
        ct.ThrowIfCancellationRequested();
        if (_isDisposed) return;

        var tracks = playlist?.Tracks ?? [];
        IsFetchingFromNetwork = false;
        await InitializeItemsAsync(tracks, canFetchMore: false);

        if (tracks.Count > 0 && LibService.Settings.AutoPlayOnUrlPaste)
            _ = Audio.StartQueueAsync(tracks, tracks[0]);

        HasResults = tracks.Count > 0;
        if (!HasResults) ErrorMessage = SL["Search_NoResults"];
    }

    private async Task HandleSearchAsync(CancellationToken ct, bool forceNetwork)
    {
        if (_isDisposed) return;

        var sw = Stopwatch.StartNew();
        var cacheSource = SourceToSearchSource();
        bool useCache = !forceNetwork && LibService.Settings.EnableSearchCache;

        if (useCache)
        {
            var cached = await _searchCache.GetAsync(_currentQuery, cacheSource, minCount: 20);
            ct.ThrowIfCancellationRequested();
            if (_isDisposed) return;

            if (cached is { Count: >= 20 })
            {
                IsFromCache = true;
                _searchSession = null;

                await InitializeItemsAsync(cached, canFetchMore: cached.Count < MaxResults);
                HasResults = true;

                var urls = cached.Take(20).Select(static t => t.ThumbnailUrl);
                _ = _imageCache.PrefetchAsync(urls!, ct);

                Log.Debug($"[Search] Cache hit: {cached.Count} items in {sw.ElapsedMilliseconds}ms");
                return;
            }
        }

        IsFetchingFromNetwork = true;
        IsFromCache = false;

        if (forceNetwork)
            _searchCache.InvalidateQuery(_currentQuery, cacheSource);

        var (tracks, session) = await _youtube.SearchWithSessionAsync(
            _currentQuery, InitialBatchSize, MaxResults, GetSearchFilter(), ct);

        if (_isDisposed) return;

        _searchSession = session;

        if (Source == ContentSource.YouTubeMusic)
        {
            for (int i = 0; i < tracks.Count; i++)
                tracks[i].IsMusic = true;
        }

        ct.ThrowIfCancellationRequested();
        if (_isDisposed) return;

        IsFetchingFromNetwork = false;

        if (tracks.Count > 0 && LibService.Settings.EnableSearchCache)
        {
            _ = _searchCache.SetAsync(_currentQuery, cacheSource, tracks);
            var urls = tracks.Take(20).Select(static t => t.ThumbnailUrl);
            _ = _imageCache.PrefetchAsync(urls!, ct);
        }

        bool hasMore = tracks.Count > 0 && (session?.HasMore ?? false);
        await InitializeItemsAsync(tracks, canFetchMore: hasMore);

        HasResults = tracks.Count > 0;
        if (!HasResults) ErrorMessage = SL["Search_NoResults"];

        sw.Stop();
        Log.Info($"[Search] Search completed: {tracks.Count} items in {sw.ElapsedMilliseconds}ms, hasMore={hasMore}");
    }

    #endregion

    #region History, Suggestions & Ghost Text Engine

    private async Task LoadHistoryAsync()
    {
        try
        {
            var history = await LibService.GetSearchHistoryAsync();
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (_isDisposed) return;
                RecentSearches.Clear();
                for (int i = 0; i < history.Count; i++)
                    RecentSearches.Add(history[i]);

                this.RaisePropertyChanged(nameof(HasRecentSearches));
                UpdateLocalSuggestionsAndGhostText(SearchQuery);
            });
        }
        catch (Exception ex)
        {
            Log.Error($"[Search] Failed to load search history: {ex.Message}");
        }
    }

    private void AddToHistory(string query)
    {
        if (_isDisposed || string.IsNullOrWhiteSpace(query)) return;

        if (YoutubeProvider.DetectQueryType(query) != QueryType.Search)
            return;

        for (int i = RecentSearches.Count - 1; i >= 0; i--)
        {
            if (string.Equals(RecentSearches[i], query, StringComparison.OrdinalIgnoreCase))
                RecentSearches.RemoveAt(i);
        }

        RecentSearches.Insert(0, query);

        while (RecentSearches.Count > 12)
            RecentSearches.RemoveAt(RecentSearches.Count - 1);

        UpdateHistoryStorage();
        this.RaisePropertyChanged(nameof(HasRecentSearches));
    }

    private void UpdateHistoryStorage()
    {
        if (_isDisposed) return;
        var historyStrings = RecentSearches.ToList();
        _ = LibService.SaveSearchHistoryAsync(historyStrings);
    }

    /// <summary>
    /// Мгновенно фильтрует текущие доступные подсказки (историю и уже полученные данные YouTube)
    /// и рассчитывает Ghost Text на 0-м кадре, сохраняя подсказки активными при вводе пробелов.
    /// </summary>
    private void UpdateLocalSuggestionsAndGhostText(string query)
    {
        var rawQuery = query ?? string.Empty;
        var trimmed = rawQuery.Trim();

        if (string.IsNullOrEmpty(trimmed) || YoutubeProvider.DetectQueryType(trimmed) != QueryType.Search)
        {
            GhostText = string.Empty;
            GhostTextSuffix = string.Empty;

            if (string.IsNullOrEmpty(trimmed))
            {
                int maxCount = LibService.Settings.MaxSuggestionsCount;
                var historyItems = new List<SearchSuggestionItem>(Math.Min(RecentSearches.Count, maxCount));
                for (int i = 0; i < RecentSearches.Count && historyItems.Count < maxCount; i++)
                {
                    var h = RecentSearches[i];
                    if (!_dismissedSuggestions.Contains(h))
                        historyItems.Add(new SearchSuggestionItem(h, true, this));
                }
                ApplySuggestions(historyItems);
            }
            else
            {
                ApplySuggestions([]);
            }
            return;
        }

        // 1. Ищем совпадения в локальной истории
        var matchedItems = new List<SearchSuggestionItem>(LibService.Settings.MaxSuggestionsCount);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < RecentSearches.Count; i++)
        {
            var h = RecentSearches[i];
            if (!_dismissedSuggestions.Contains(h) && h.Contains(trimmed, StringComparison.OrdinalIgnoreCase))
            {
                if (seen.Add(h))
                    matchedItems.Add(new SearchSuggestionItem(h, true, this));

                if (matchedItems.Count >= 4)
                    break;
            }
        }

        // 2. ВАЖНО: не затираем уже загруженные подсказки YouTube, если они все еще подходят под ввод!
        // Это обеспечивает непрерывную работу Ghost Text при нажатии пробела.
        for (int i = 0; i < Suggestions.Count; i++)
        {
            var item = Suggestions[i];
            if (!item.IsFromHistory && item.Text.StartsWith(rawQuery, StringComparison.OrdinalIgnoreCase))
            {
                if (seen.Add(item.Text))
                    matchedItems.Add(item);

                if (matchedItems.Count >= LibService.Settings.MaxSuggestionsCount)
                    break;
            }
        }

        ApplySuggestions(matchedItems);
        CalculateGhostText(rawQuery, matchedItems);
    }

    /// <summary>
    /// Вычисляет полный Ghost Text и изолированный суффикс без двоения букв и сбоев на пробелах.
    /// </summary>
    private void CalculateGhostText(string rawQuery, IReadOnlyList<SearchSuggestionItem> items)
    {
        if (string.IsNullOrEmpty(rawQuery) || items.Count == 0)
        {
            GhostText = string.Empty;
            GhostTextSuffix = string.Empty;
            return;
        }

        // Приоритет 1: точное совпадение с учетом регистра и пробелов
        for (int i = 0; i < items.Count; i++)
        {
            var candidate = items[i].Text;
            if (candidate.StartsWith(rawQuery, StringComparison.OrdinalIgnoreCase) && candidate.Length > rawQuery.Length)
            {
                // Префикс берем строго в пользовательском вводе, суффикс берем из подсказки
                GhostText = string.Concat(rawQuery, candidate.AsSpan(rawQuery.Length));
                GhostTextSuffix = candidate[rawQuery.Length..];
                return;
            }
        }

        // Приоритет 2: если пользователь поставил хвостовой пробел, ищем совпадение по первому слову
        var trimmed = rawQuery.TrimStart();
        if (trimmed.Length > 0)
        {
            for (int i = 0; i < items.Count; i++)
            {
                var candidate = items[i].Text;
                if (candidate.StartsWith(trimmed, StringComparison.OrdinalIgnoreCase) && candidate.Length > rawQuery.Length)
                {
                    GhostText = string.Concat(rawQuery, candidate.AsSpan(rawQuery.Length));
                    GhostTextSuffix = candidate[rawQuery.Length..];
                    return;
                }
            }
        }

        GhostText = string.Empty;
        GhostTextSuffix = string.Empty;
    }

    /// <summary>
    /// Выполняет фоновую подгрузку подсказок из сети после завершения паузы ввода пользователем.
    /// </summary>
    private void FetchRemoteSuggestionsThrottled(string query)
    {
        _suggestCts?.Cancel();
        _suggestCts?.Dispose();
        _suggestCts = new CancellationTokenSource();
        var ct = _suggestCts.Token;

        var trimmed = query?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(trimmed) || YoutubeProvider.DetectQueryType(trimmed) != QueryType.Search)
            return;

        var currentLocal = new List<SearchSuggestionItem>(Suggestions.Count);
        for (int i = 0; i < Suggestions.Count; i++)
        {
            if (Suggestions[i].IsFromHistory)
                currentLocal.Add(Suggestions[i]);
        }

        _ = FetchAndMergeRemoteSuggestionsAsync(trimmed, currentLocal, ct);
    }

    private async Task FetchAndMergeRemoteSuggestionsAsync(
        string query,
        List<SearchSuggestionItem> localItems,
        CancellationToken ct)
    {
        try
        {
            var ytSuggestions = await _youtube.GetSearchSuggestionsAsync(query, ct);
            if (ct.IsCancellationRequested || _isDisposed)
                return;

            var combined = new List<SearchSuggestionItem>(localItems.Count + ytSuggestions.Count);
            combined.AddRange(localItems);

            var seen = new HashSet<string>(localItems.Count + ytSuggestions.Count, StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < localItems.Count; i++)
                seen.Add(localItems[i].Text);

            int maxSuggestions = LibService.Settings.MaxSuggestionsCount;
            for (int i = 0; i < ytSuggestions.Count && combined.Count < maxSuggestions; i++)
            {
                var s = ytSuggestions[i];
                if (!_dismissedSuggestions.Contains(s) && seen.Add(s))
                    combined.Add(new SearchSuggestionItem(s, false, this));
            }

            if (ct.IsCancellationRequested || _isDisposed)
                return;

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (ct.IsCancellationRequested || _isDisposed)
                    return;

                ApplySuggestions(combined);
                CalculateGhostText(SearchQuery, combined);
            }, DispatcherPriority.Background, ct);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Debug($"[Search] Suggestion fetch error: {ex.Message}");
        }
    }

    /// <summary>
    /// Выполняет in-place синхронизацию подсказок без вызова Clear(),
    /// исключая пересборку визуального дерева и лаги UI.
    /// </summary>
    private void ApplySuggestions(List<SearchSuggestionItem> newItems)
    {
        if (Suggestions.Count == newItems.Count)
        {
            bool identical = true;
            for (int i = 0; i < newItems.Count; i++)
            {
                if (Suggestions[i].Text != newItems[i].Text || Suggestions[i].IsFromHistory != newItems[i].IsFromHistory)
                {
                    identical = false;
                    break;
                }
            }

            if (identical) return;
        }

        int commonCount = Math.Min(Suggestions.Count, newItems.Count);
        for (int i = 0; i < commonCount; i++)
        {
            if (Suggestions[i].Text != newItems[i].Text || Suggestions[i].IsFromHistory != newItems[i].IsFromHistory)
            {
                Suggestions[i] = newItems[i];
            }
        }

        if (newItems.Count > Suggestions.Count)
        {
            for (int i = Suggestions.Count; i < newItems.Count; i++)
            {
                Suggestions.Add(newItems[i]);
            }
        }
        else if (Suggestions.Count > newItems.Count)
        {
            for (int i = Suggestions.Count - 1; i >= newItems.Count; i--)
            {
                Suggestions.RemoveAt(i);
            }
        }

        NotifySuggestionsChanged();
    }

    #endregion

    #region IDisposable

    protected override void Dispose(bool disposing)
    {
        if (_isDisposed) return;

        if (disposing)
        {
            _isDisposed = true;
            _suggestCts?.Cancel();
            _suggestCts?.Dispose();
            _searchCts?.Cancel();
            _searchCts?.Dispose();
            _searchCts = null;

            try { _searchSession?.Dispose(); } catch { }
            _searchSession = null;
        }

        base.Dispose(disposing);
    }

    #endregion
}