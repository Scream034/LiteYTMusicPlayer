using System.ComponentModel;
using System.Windows.Input;
using Avalonia.Media;
using ReactiveUI;

namespace LMP.UI.Features.Shared;

public sealed partial class TrackItemViewModel : ViewModelBase
{
    #region Weak Event Subscription

    private sealed class WeakPropertyChangedSubscription
    {
        private readonly WeakReference<TrackItemViewModel> _weak;
        private readonly INotifyPropertyChanged _source;

        internal WeakPropertyChangedSubscription(TrackItemViewModel vm, INotifyPropertyChanged source)
        {
            _weak = new WeakReference<TrackItemViewModel>(vm);
            _source = source;
            source.PropertyChanged += Handle;
        }

        private void Handle(object? sender, PropertyChangedEventArgs e)
        {
            if (_weak.TryGetTarget(out var vm))
                vm.OnTrackPropertyChanged(sender, e);
            else
                _source.PropertyChanged -= Handle;
        }

        internal void Unsubscribe() => _source.PropertyChanged -= Handle;
    }

    private readonly WeakPropertyChangedSubscription _trackSubscription;

    #endregion

    #region Static Geometries Cache

    private static StreamGeometry? _checkCircleGeometry;
    private static StreamGeometry? _cloudCheckGeometry;

    private static StreamGeometry? CheckCircleGeometry =>
        _checkCircleGeometry ??= ResolveStaticGeometry("Icon.CheckCircle");

    private static StreamGeometry? CloudCheckGeometry =>
        _cloudCheckGeometry ??= ResolveStaticGeometry("Icon.CloudCheck");

    private static StreamGeometry? ResolveStaticGeometry(string key) =>
        Avalonia.Application.Current?.Resources.TryGetResource(key, null, out var res) == true
            ? res as StreamGeometry
            : null;

    #endregion

    private readonly AudioEngine _audio;
    private readonly MusicLibraryManager _manager;
    private readonly DownloadService _downloads;
    private readonly DialogService _dialog;
    private readonly LibraryService _library;

    private Action<TrackInfo>? _onPlay;

    // Ленивые поля для команд контекстного меню (минимизация аллокаций при загрузке списков)
    private ICommand? _addToQueueCommand;
    private ICommand? _startRadioCommand;
    private ICommand? _saveToDownloadsCommand;
    private ICommand? _removeFromPlaylistCommand;
    private ICommand? _removeFromQueueCommand;
    private ICommand? _addToPlaylistCommand;
    private ICommand? _copyLinkCommand;

    public TrackInfo Track { get; }
    public bool IsDisposed { get; private set; }

    public string Id => Track.Id;
    public string Title => Track.Title;
    public string Author => Track.Author;
    public TimeSpan Duration => Track.Duration;
    public string ThumbnailUrl => Track.ThumbnailUrl;

    public bool IsLiked => Track.IsLiked;
    public bool IsDownloaded => Track.IsDownloaded;

    public string FormattedDuration => Duration.TotalHours >= 1
        ? Duration.ToString(@"h\:mm\:ss")
        : Duration.ToString(@"m\:ss");

    [Reactive] public partial bool IsActive { get; private set; }
    [Reactive] public partial bool IsPlaying { get; private set; }
    [Reactive] public partial bool IsDownloading { get; private set; }
    [Reactive] public partial float DownloadProgress { get; private set; }
    [Reactive] public partial bool IsMenuOpen { get; set; }
    [Reactive] public partial bool IsSelected { get; set; }
    [Reactive] public partial bool IsPlaylistContext { get; set; }
    [Reactive] public partial bool IsQueueContext { get; set; }

    public bool ShowAddToQueue => !IsQueueContext;

    /// <summary>
    /// Флаг отображения иконки состояния кэша.
    /// </summary>
    public bool HasCacheIcon => !IsDownloading && (Track.IsDownloaded || Track.IsCached);

    /// <summary>
    /// Геометрия иконки кэша из статического кэша.
    /// </summary>
    public StreamGeometry? CacheIconGeometry => Track.IsDownloaded
        ? CheckCircleGeometry
        : (Track.IsCached ? CloudCheckGeometry : null);

    /// <summary>
    /// Подсказка для иконки кэша.
    /// </summary>
    public string? CacheIconTooltip => Track.IsDownloaded
        ? L["Track_Downloaded"]
        : (Track.IsCached ? L["Track_Cached"] : null);

    public string DownloadStatusText
    {
        get
        {
            if (Track.IsDownloaded) return L["Track_Downloaded"] ?? "Downloaded";
            if (Track.IsCached) return L["Track_SaveToFolder"] ?? "Save to folder";
            return L["Track_Download"] ?? "Download";
        }
    }

    public Action<TrackInfo>? StartRadioAction { get; set; }
    public Action<TrackInfo>? RemoveFromPlaylistAction { get; set; }
    public string? SourceContextId { get; set; }

    public ICommand PlayCommand { get; }
    public ICommand ToggleLikeCommand { get; }

    public ICommand AddToQueueCommand =>
        _addToQueueCommand ??= new TrackSyncCommand(OnAddToQueue);

    public ICommand StartRadioCommand =>
        _startRadioCommand ??= new TrackSyncCommand(OnStartRadio);

    public ICommand SaveToDownloadsCommand =>
        _saveToDownloadsCommand ??= new TrackAsyncCommand(SaveToDownloadsAsync);

    public ICommand AddToPlaylistCommand =>
        _addToPlaylistCommand ??= new TrackAsyncCommand(AddToPlaylistAsync);

    public ICommand CopyLinkCommand =>
        _copyLinkCommand ??= new TrackAsyncCommand(CopyLinkAsync);

    public ICommand RemoveFromPlaylistCommand =>
        _removeFromPlaylistCommand ??= new TrackSyncCommand(OnRemoveFromPlaylist);

    public ICommand RemoveFromQueueCommand =>
        _removeFromQueueCommand ??= new TrackSyncCommand(OnRemoveFromQueue);

    public TrackItemViewModel(
        TrackInfo track,
        AudioEngine audio,
        DownloadService downloads,
        MusicLibraryManager manager,
        DialogService dialog,
        LibraryService library,
        Action<TrackInfo>? onPlay = null)
    {
        Track = track;
        _audio = audio;
        _manager = manager;
        _downloads = downloads;
        _dialog = dialog;
        _library = library;
        _onPlay = onPlay;

        PlayCommand = new TrackAsyncCommand(PlayAsync);
        ToggleLikeCommand = new TrackAsyncCommand(ToggleLikeAsync);

        _trackSubscription = new WeakPropertyChangedSubscription(this, track);
    }

    private void OnTrackPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(Track.IsLiked):
                this.RaisePropertyChanged(nameof(IsLiked));
                break;

            case nameof(Track.IsDownloaded):
                if (Track.IsDownloaded && IsDownloading)
                {
                    IsDownloading = false;
                    DownloadProgress = 0f;
                }
                this.RaisePropertyChanged(nameof(IsDownloaded));
                this.RaisePropertyChanged(nameof(DownloadStatusText));
                this.RaisePropertyChanged(nameof(HasCacheIcon));
                this.RaisePropertyChanged(nameof(CacheIconGeometry));
                this.RaisePropertyChanged(nameof(CacheIconTooltip));
                break;

            case nameof(Track.IsCached):
                this.RaisePropertyChanged(nameof(DownloadStatusText));
                this.RaisePropertyChanged(nameof(HasCacheIcon));
                this.RaisePropertyChanged(nameof(CacheIconGeometry));
                this.RaisePropertyChanged(nameof(CacheIconTooltip));
                break;
        }
    }

    private Task ToggleLikeAsync() => _manager.ToggleLikeAsync(Track);

    private void OnAddToQueue() => _audio.Enqueue(Track);

    private void OnStartRadio() => StartRadioAction?.Invoke(Track);

    private void OnRemoveFromPlaylist()
    {
        if (IsPlaylistContext)
            RemoveFromPlaylistAction?.Invoke(Track);
    }

    private void OnRemoveFromQueue()
    {
        if (IsQueueContext)
            _audio.RemoveFromQueue(Track);
    }

    private async Task PlayAsync()
    {
        if (_audio.CurrentTrack?.Id == Id)
            await _audio.SetPlaybackStateAsync(!_audio.IsPlaying).ConfigureAwait(false);
        else
            _onPlay?.Invoke(Track);
    }

    private async Task SaveToDownloadsAsync()
    {
        if (Track.IsDownloaded) return;

        if (Track.IsCached)
        {
            var cache = AudioSourceFactory.GlobalCache;
            if (cache == null) return;

            bool success = await cache.ExportTrackToDownloadsAsync(
                Track.Id,
                async id => await _library.GetTrackAsync(id).ConfigureAwait(false),
                async t => await _library.AddOrUpdateTrackAsync(t).ConfigureAwait(false)).ConfigureAwait(false);

            if (success) Track.IsDownloaded = true;
        }
        else
        {
            _downloads.StartDownload(Track);
        }
    }

    public void SetActive(bool isActive, bool isPlaying)
    {
        IsActive = isActive;
        IsPlaying = isActive && isPlaying;
    }

    public void SetDownloadState(bool isDownloading, float progress)
    {
        if (Track.IsDownloaded)
            isDownloading = false;

        IsDownloading = isDownloading;
        DownloadProgress = isDownloading ? progress : 0f;

        this.RaisePropertyChanged(nameof(HasCacheIcon));
        this.RaisePropertyChanged(nameof(CacheIconGeometry));
        this.RaisePropertyChanged(nameof(CacheIconTooltip));

        if (!isDownloading)
        {
            this.RaisePropertyChanged(nameof(DownloadStatusText));
        }
    }

    public void UpdatePlayAction(Action<TrackInfo>? onPlay) => _onPlay = onPlay;

    private async Task AddToPlaylistAsync()
    {
        var selectedIds = await _dialog.ShowAddToPlaylistDialogAsync(Track).ConfigureAwait(false);
        if (selectedIds.Count == 0) return;

        foreach (var playlistId in selectedIds)
            await _manager.AddTrackToPlaylistAsync(playlistId, Track).ConfigureAwait(false);
    }

    private async Task CopyLinkAsync()
    {
        if (IsDisposed) return;

        var url = Track.Url;
        if (string.IsNullOrEmpty(url))
            url = $"https://www.youtube.com/watch?v={Track.GetRawId()}";

        if (string.IsNullOrEmpty(url))
        {
            CopyHintService.Instance.Show(
                L["Track_CopyLink_NoUrl"] ?? "No link available",
                CopyHintKind.Warning,
                null);
            return;
        }

        await Clipboard.SetTextAsync(url).ConfigureAwait(false);

        CopyHintService.Instance.Show(
            L["Track_Copied"] ?? "Copied!",
            CopyHintKind.Success,
            null);
    }

    protected override void Dispose(bool disposing)
    {
        if (IsDisposed) return;
        if (disposing)
        {
            _trackSubscription.Unsubscribe();
            _onPlay = null;
            StartRadioAction = null;
            RemoveFromPlaylistAction = null;
        }
        base.Dispose(disposing);
        IsDisposed = true;
    }
}