using System.Reactive;
using System.Reactive.Linq;
using ReactiveUI;
using Avalonia.Media;

namespace LMP.UI.Features.Library;

/// <summary>
/// ViewModel карточки плейлиста в библиотеке.
/// </summary>
public sealed partial class PlaylistCardViewModel : ViewModelBase
{
    #region Fields

    private readonly CookieAuthService _auth;
    private readonly PlayerControlService _playerControl;
    private readonly LibraryService _library;
    private readonly AudioEngine _audio;

    private readonly Func<string, Task>? _onDelete;
    private readonly Func<Core.Models.Playlist, Task>? _onEdit;
    private readonly Func<Core.Models.Playlist, Task> _addToQueueAction;
    private readonly Func<Core.Models.Playlist, Task> _playAction;
    private readonly Action<string> _onOpen;

    private readonly EventHandler<string> _languageChangedHandler;
    private bool _isDisposed;

    #endregion

    #region Properties — Identity

    /// <summary>
    /// Исходный объект плейлиста.
    /// Мутабельный — обновляется через <see cref="UpdateFrom"/>.
    /// </summary>
    public Core.Models.Playlist Playlist { get; }

    /// <summary>
    /// Уникальный идентификатор плейлиста.
    /// </summary>
    public string Id => Playlist.Id;

    /// <summary>
    /// Это системный плейлист «Понравившиеся» (специальный UI: обложка-сердце, без удаления).
    /// </summary>
    public bool IsLikedPlaylist => Playlist.Id == LibraryService.LikedPlaylistId;

    #endregion

    #region Properties — Display

    /// <summary>
    /// Название плейлиста. Реактивное — обновляется при переименовании.
    /// </summary>
    [Reactive] public partial string Name { get; private set; }

    /// <summary>
    /// URL обложки с upscale для YouTube-превью. Реактивное — обновляется при смене обложки.
    /// </summary>
    [Reactive] public partial string? ThumbnailUrl { get; private set; }

    /// <summary>
    /// Количество треков в плейлисте. Реактивное — обновляется при add/remove треков.
    /// </summary>
    [Reactive] public partial int TrackCount { get; set; }

    /// <summary>
    /// Видимость карточки. Управляется stagger-анимацией в <see cref="LibraryViewModel"/>.
    /// </summary>
    [Reactive] public partial bool IsVisible { get; set; }

    /// <summary>
    /// Форматированное количество треков с правильным склонением.
    /// Пересчитывается через PropertyChanged при изменении TrackCount.
    /// </summary>
    public string FormattedTrackCount => FormatTrackCount(TrackCount);

    /// <summary>
    /// Есть обложка для отображения (не пустая и не Liked).
    /// Пересчитывается через PropertyChanged при изменении ThumbnailUrl.
    /// </summary>
    public bool HasThumbnail => !string.IsNullOrEmpty(ThumbnailUrl) && !IsLikedPlaylist;

    /// <summary>
    /// Показывать placeholder вместо обложки.
    /// Пересчитывается через PropertyChanged при изменении ThumbnailUrl.
    /// </summary>
    public bool ShowPlaceholder => string.IsNullOrEmpty(ThumbnailUrl) && !IsLikedPlaylist;

    /// <summary>
    /// YouTube URL плейлиста для CopyLinkButton.
    /// Null если плейлист не привязан к YouTube.
    /// </summary>
    public string? YoutubeUrl
    {
        get
        {
            if (IsLikedPlaylist && _auth.IsAuthenticated)
                return "https://www.youtube.com/playlist?list=LM";
            return string.IsNullOrEmpty(Playlist.YoutubeId)
                ? null
                : $"https://www.youtube.com/playlist?list={Playlist.YoutubeId}";
        }
    }

    /// <summary>
    /// Можно ли скопировать ссылку на YouTube-плейлист.
    /// </summary>
    public bool CanCopyLink => !string.IsNullOrEmpty(YoutubeUrl);

    #endregion

    #region Properties — Author & Ownership

    /// <summary>
    /// Имя автора/владельца плейлиста. Реактивное.
    /// </summary>
    [Reactive] public partial string? Author { get; private set; }

    /// <summary>
    /// Показывать строку автора на карточке.
    /// True только для чужих плейлистов с известным автором.
    /// Реактивное — обновляется при смене ownership.
    /// </summary>
    [Reactive] public partial bool ShowAuthor { get; private set; }

    /// <summary>
    /// Отображаемая строка автора с предлогом «от X».
    /// Локализованная, реактивная.
    /// </summary>
    [Reactive] public partial string? AuthorDisplayText { get; private set; }

    /// <summary>
    /// Tooltip для строки автора: «Плейлист от X».
    /// Локализованный, реактивный.
    /// </summary>
    [Reactive] public partial string? AuthorTooltip { get; private set; }

    /// <summary>
    /// Плейлист приватный (🔒). Реактивное.
    /// </summary>
    [Reactive] public partial bool IsPrivate { get; private set; }

    /// <summary>
    /// Плейлист по ссылке (🔗). Реактивное.
    /// </summary>
    [Reactive] public partial bool IsUnlisted { get; private set; }

    /// <summary>
    /// Плейлист хранится локально (LocalOnly или TwoWaySync).
    /// </summary>
    public bool IsLocal => Playlist.IsLocal;

    /// <summary>
    /// Плейлист только для чтения (Foreign или CloudPublic).
    /// </summary>
    public bool IsReadOnly => !Playlist.IsEditable;

    /// <summary>
    /// Можно ли удалить плейлист (всё кроме Liked).
    /// </summary>
    public bool CanDelete => !IsLikedPlaylist;

    /// <summary>
    /// Можно ли открыть диалог редактирования плейлиста.
    /// </summary>
    public bool CanEdit => Playlist.IsEditable;

    #endregion

    #region Properties — Cloud & Sync

    /// <summary>
    /// Плейлист связан с YouTube: есть YoutubeId и он доступен,
    /// или это Liked при аутентифицированном пользователе.
    /// Используется для отображения иконки облака (☁).
    /// Реактивное.
    /// </summary>
    [Reactive] public partial bool HasCloudSource { get; private set; }

    /// <summary>
    /// Двусторонняя синхронизация активна (TwoWaySync или Liked + auth).
    /// Реактивное.
    /// </summary>
    [Reactive] public partial bool IsTwoWaySynced { get; private set; }

    #endregion

    #region Properties — Playback State

    /// <summary>
    /// Данный плейлист активен в плеере и очередь «чистая».
    /// Используется для анимации пульсации рамки карточки.
    /// </summary>
    [Reactive] public partial bool IsActive { get; private set; }

    /// <summary>
    /// Очередь полностью совпадает с треками этого плейлиста.
    /// </summary>
    [Reactive] public partial bool IsQueuePure { get; private set; }

    /// <summary>
    /// Очередь чистая и прямо сейчас активно играет.
    /// Используется для иконки Play/Pause в контекстном меню.
    /// </summary>
    [Reactive] public partial bool IsPlayingPure { get; private set; }

    /// <summary>
    /// Текст для пункта контекстного меню «Воспроизвести / Пауза».
    /// Пересчитывается через PropertyChanged при изменении IsPlayingPure.
    /// </summary>
    public string PlayMenuHeader => IsPlayingPure
        ? (LocalizationService.Instance["Player_Pause"] ?? "Pause")
        : (LocalizationService.Instance["Player_Play"] ?? "Play");

    /// <summary>
    /// Геометрия иконки Play/Pause для контекстного меню.
    /// Пересчитывается через PropertyChanged при изменении IsPlayingPure.
    /// </summary>
    public Geometry? PlayMenuIcon
    {
        get
        {
            var key = IsPlayingPure ? "Icon.Pause" : "Icon.Play";
            if (Avalonia.Application.Current?.TryGetResource(key, null, out var res) == true
                && res is Geometry geo)
                return geo;
            return null;
        }
    }

    #endregion

    #region Commands

    /// <summary>Открыть плейлист (навигация).</summary>
    public ReactiveCommand<Unit, Unit> OpenCommand { get; }

    /// <summary>Удалить плейлист.</summary>
    public ReactiveCommand<Unit, Unit> DeleteCommand { get; }

    /// <summary>Добавить все треки в очередь.</summary>
    public ReactiveCommand<Unit, Unit> AddToQueueCommand { get; }

    /// <summary>Воспроизвести плейлист (smart play/pause).</summary>
    public ReactiveCommand<Unit, Unit> PlayCommand { get; }

    /// <summary>Открыть диалог редактирования.</summary>
    public ReactiveCommand<Unit, Unit> EditCommand { get; }

    /// <summary>Скопировать ссылку на YouTube-плейлист.</summary>
    public ReactiveCommand<Unit, Unit> CopyLinkCommand { get; }

    #endregion

    #region Constructor

    public PlaylistCardViewModel(
        CookieAuthService auth,
        PlayerControlService playerControl,
        LibraryService library,
        AudioEngine audio,
        Core.Models.Playlist playlist,
        int trackCount,
        Action<string> onOpen,
        Func<Core.Models.Playlist, Task> addToQueueAction,
        Func<Core.Models.Playlist, Task> playAction,
        Func<string, Task>? onDelete = null,
        Func<Core.Models.Playlist, Task>? onEdit = null)
    {
        Playlist = playlist;
        _auth = auth;
        _playerControl = playerControl;
        _library = library;
        _audio = audio;
        _onOpen = onOpen;
        _addToQueueAction = addToQueueAction;
        _playAction = playAction;
        _onDelete = onDelete;
        _onEdit = onEdit;

        // ═══ Initial state from model ═══
        Name = playlist.Name;
        TrackCount = trackCount;
        ThumbnailUrl = UpscaleThumbnailUrl(playlist.ThumbnailUrl);
        Author = playlist.Author;

        ApplyOwnershipState(playlist);
        ApplyCloudState(playlist);

        // ═══ Commands ═══
        OpenCommand = CreateCommand(ReactiveCommand.Create(() =>
        {
            if (!_isDisposed) _onOpen(Playlist.Id);
        }));

        DeleteCommand = CreateCommand(ReactiveCommand.CreateFromTask(
            async () =>
            {
                if (!_isDisposed && _onDelete != null)
                    await _onDelete(Playlist.Id);
            },
            this.WhenAnyValue(x => x.CanDelete)
                .ObserveOn(RxSchedulers.MainThreadScheduler)));

        AddToQueueCommand = CreateCommand(ReactiveCommand.CreateFromTask(async () =>
        {
            if (!_isDisposed) await _addToQueueAction(Playlist);
        }));

        PlayCommand = CreateCommand(ReactiveCommand.CreateFromTask(async () =>
        {
            if (!_isDisposed) await _playAction(Playlist);
        }));

        EditCommand = CreateCommand(ReactiveCommand.CreateFromTask(async () =>
        {
            if (!_isDisposed && _onEdit != null) await _onEdit(Playlist);
        }));

        CopyLinkCommand = CreateCommand(ReactiveCommand.CreateFromTask(
            async () =>
            {
                if (_isDisposed || string.IsNullOrEmpty(YoutubeUrl)) return;
                await Clipboard.SetTextAsync(YoutubeUrl);
                CopyHintService.Instance.Show(
                    LocalizationService.Instance["Playlist_LinkCopied"] ?? "Copied!",
                    CopyHintKind.Success);
            },
            this.WhenAnyValue(x => x.YoutubeUrl, url => !string.IsNullOrEmpty(url))
                .ObserveOn(RxSchedulers.MainThreadScheduler)));

        // ═══ Playback state tracking ═══
        Observable.CombineLatest(
                _playerControl.ActivePlaylistIdObservable,
                _playerControl.PlaybackStateObservable,
                _playerControl.QueueCountObservable,
                (activeId, state, qCount) => new { activeId, state.IsPlaying, qCount })
            .Throttle(TimeSpan.FromMilliseconds(50))
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(async data =>
            {
                if (_isDisposed) return;

                if (data.activeId != Id || data.qCount != TrackCount)
                {
                    IsActive = false;
                    IsQueuePure = false;
                    IsPlayingPure = false;
                    return;
                }

                try
                {
                    var trackIds = await _library.GetPlaylistTrackIdsAsync(Id);
                    var trackIdSet = new HashSet<string>(trackIds, StringComparer.Ordinal);
                    var queue = _audio.Queue;

                    bool isPure = queue.Count == trackIds.Count;
                    if (isPure)
                    {
                        for (int i = 0; i < queue.Count; i++)
                        {
                            if (!trackIdSet.Contains(queue[i].Id))
                            {
                                isPure = false;
                                break;
                            }
                        }
                    }

                    IsActive = isPure;
                    IsQueuePure = isPure;
                    IsPlayingPure = isPure && data.IsPlaying;
                }
                catch (Exception ex)
                {
                    Log.Error($"[PlaylistCard] Queue purity check error: {ex.Message}");
                    IsActive = false;
                    IsQueuePure = false;
                    IsPlayingPure = false;
                }
            })
            .DisposeWith(Disposables);

        // ═══ Derived property invalidation ═══
        this.WhenAnyValue(x => x.IsPlayingPure)
            .Subscribe(_ =>
            {
                this.RaisePropertyChanged(nameof(PlayMenuHeader));
                this.RaisePropertyChanged(nameof(PlayMenuIcon));
            })
            .DisposeWith(Disposables);

        this.WhenAnyValue(x => x.TrackCount)
            .Subscribe(_ => this.RaisePropertyChanged(nameof(FormattedTrackCount)))
            .DisposeWith(Disposables);

        this.WhenAnyValue(x => x.ThumbnailUrl)
            .Subscribe(_ =>
            {
                this.RaisePropertyChanged(nameof(HasThumbnail));
                this.RaisePropertyChanged(nameof(ShowPlaceholder));
            })
            .DisposeWith(Disposables);

        _languageChangedHandler = (_, _) =>
        {
            this.RaisePropertyChanged(nameof(FormattedTrackCount));
            this.RaisePropertyChanged(nameof(PlayMenuHeader));
            RefreshAuthorTexts();
        };
        LocalizationService.Instance.LanguageChanged += _languageChangedHandler;
    }

    #endregion

    #region Update

    /// <summary>
    /// Обновляет карточку из свежих данных плейлиста.
    /// Вызывается при инкрементальном обновлении библиотеки.
    /// </summary>
    public void UpdateFrom(Core.Models.Playlist playlist, int trackCount)
    {
        if (_isDisposed) return;

        // ═══ Display ═══
        if (Name != playlist.Name)
        {
            Playlist.StoredName = playlist.StoredName;
            Name = playlist.Name;
        }

        if (TrackCount != trackCount)
            TrackCount = trackCount;

        var newUrl = UpscaleThumbnailUrl(playlist.ThumbnailUrl);
        if (ThumbnailUrl != newUrl)
        {
            Playlist.ThumbnailUrl = playlist.ThumbnailUrl;
            ThumbnailUrl = newUrl;
        }

        // ═══ Author ═══
        if (!string.Equals(Author, playlist.Author, StringComparison.Ordinal))
        {
            Playlist.Author = playlist.Author;
            Author = playlist.Author;
        }

        // ═══ Ownership & Visibility ═══
        bool ownershipChanged = Playlist.Ownership != playlist.Ownership
                                || Playlist.Visibility != playlist.Visibility;
        if (ownershipChanged)
        {
            Playlist.Ownership = playlist.Ownership;
            Playlist.Visibility = playlist.Visibility;
            ApplyOwnershipState(playlist);
        }

        // ═══ Cloud & Sync ═══
        bool cloudChanged = Playlist.SyncMode != playlist.SyncMode
                            || !string.Equals(Playlist.YoutubeId, playlist.YoutubeId, StringComparison.Ordinal)
                            || Playlist.IsCloudUnavailable != playlist.IsCloudUnavailable;
        if (cloudChanged)
        {
            Playlist.SyncMode = playlist.SyncMode;
            Playlist.YoutubeId = playlist.YoutubeId;
            Playlist.IsCloudUnavailable = playlist.IsCloudUnavailable;
            ApplyCloudState(playlist);
        }
    }

    #endregion

    #region Helpers

    /// <summary>
    /// Выставляет все reactive-свойства ownership/visibility/author из модели.
    /// Вызывается из конструктора и <see cref="UpdateFrom"/>.
    /// </summary>
    private void ApplyOwnershipState(Core.Models.Playlist playlist)
    {
        ShowAuthor = !string.IsNullOrEmpty(playlist.Author);
        IsPrivate = playlist.Visibility == PlaylistVisibility.Private;
        IsUnlisted = playlist.Visibility == PlaylistVisibility.Unlisted;

        RefreshAuthorTexts();
    }

    /// <summary>
    /// Выставляет все reactive-свойства cloud/sync из модели.
    /// Вызывается из конструктора и <see cref="UpdateFrom"/>.
    /// </summary>
    private void ApplyCloudState(Core.Models.Playlist playlist)
    {
        HasCloudSource = playlist.HasCloudLink
                         || (IsLikedPlaylist && _auth.IsAuthenticated);

        IsTwoWaySynced = playlist.SyncMode == PlaylistSyncMode.TwoWaySync
                         || (IsLikedPlaylist && _auth.IsAuthenticated);
    }

    /// <summary>
    /// Пересчитывает локализованные строки автора.
    /// Вызывается при смене автора, ownership или языка.
    /// </summary>
    private void RefreshAuthorTexts()
    {
        var author = Author;
        bool hasAuthor = !string.IsNullOrEmpty(author);

        AuthorDisplayText = hasAuthor
            ? string.Format(
                LocalizationService.Instance["Playlist_ByAuthor"] ?? "от {0}",
                author)
            : null;

        AuthorTooltip = hasAuthor
            ? string.Format(
                LocalizationService.Instance["Playlist_Foreign_Tooltip"] ?? "Плейлист от {0}",
                author)
            : null;
    }

    /// <summary>
    /// Повышает разрешение YouTube-превью обложки.
    /// </summary>
    private static string? UpscaleThumbnailUrl(string? url)
    {
        if (string.IsNullOrEmpty(url)) return url;

        if (url.Contains("hqdefault.jpg"))
            return url.Replace("hqdefault.jpg", "maxresdefault.jpg");

        if (url.Contains("mqdefault.jpg"))
            return url.Replace("mqdefault.jpg", "maxresdefault.jpg");

        return url;
    }

    /// <summary>
    /// Форматирует количество треков с правильным склонением через локализацию.
    /// </summary>
    private static string FormatTrackCount(int count)
    {
        if (count == 0) return LocalizationService.Instance["Playlist_Empty"];
        return LocalizationService.Instance.GetPlural("Playlist_TracksCount", count);
    }

    /// <summary>
    /// Переводит карточку в видимое состояние (stagger-анимация появления).
    /// </summary>
    public void Show()
    {
        if (!_isDisposed) IsVisible = true;
    }

    #endregion

    #region Dispose

    protected override void Dispose(bool disposing)
    {
        if (_isDisposed) return;
        if (disposing)
        {
            _isDisposed = true;
            LocalizationService.Instance.LanguageChanged -= _languageChangedHandler;
        }
        base.Dispose(disposing);
    }

    #endregion
}
