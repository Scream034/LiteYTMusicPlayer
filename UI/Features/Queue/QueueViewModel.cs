using Avalonia.Collections;
using Avalonia.Threading;
using LMP.UI.Features.Shared;
using ReactiveUI;

using System.Reactive;
using System.Reactive.Linq;

namespace LMP.UI.Features.Queue;

/// <summary>
/// ViewModel панели очереди воспроизведения.
/// Наследует TrackListReorderableViewModel для полного устранения дублирования кода.
/// Использует изолированный кэш базового класса и инкрементальные обновления.
/// </summary>
public sealed partial class QueueViewModel : TrackListReorderableViewModel
{
    #region Fields

    private readonly DownloadService _downloads;
    private readonly DialogService _dialog;
    private readonly MusicLibraryManager _manager;
    private readonly LibraryService _library;

    private bool _isMovingInternally;
    private volatile bool _isSuspended;
    private bool _isDisposed;

    #endregion

    #region Properties

    /// <summary>True когда очередь пуста (нет треков вообще).</summary>
    [Reactive] public partial bool IsEmpty { get; private set; } = true;

    /// <summary>True когда очередь не пуста, но фильтр не нашёл совпадений.</summary>
    [Reactive] public partial bool IsFilterEmpty { get; private set; }

    [Reactive] public partial bool CanReorderItems { get; private set; } = true;

    /// <summary>
    /// Псевдоним для Items, сохраняющий совместимость с биндингом в QueueView.axaml.
    /// </summary>
    public AvaloniaList<TrackItemViewModel> QueueItems => Items;

    #endregion

    #region Commands

    public ReactiveCommand<Unit, Unit> ClearQueueCommand { get; }
    public ReactiveCommand<Unit, Unit> ShuffleQueueCommand { get; }
    public ReactiveCommand<Unit, Unit> DownloadAllCommand { get; }
    public ReactiveCommand<TrackItemViewModel, Unit> RemoveTrackCommand { get; }
    public ReactiveCommand<(int oldIndex, int newIndex), Unit> MoveItemCommand { get; }
    public ReactiveCommand<Unit, Unit> SaveQueueToPlaylistCommand { get; }

    #endregion

    #region Constructor

    public QueueViewModel(
        AudioEngine audio,
        DownloadService downloads,
        DialogService dialog,
        MusicLibraryManager manager,
        LibraryService library,
        TrackViewModelFactory vmFactory)
        : base(audio, downloads, vmFactory)
    {
        _downloads = downloads;
        _dialog = dialog;
        _manager = manager;
        _library = library;

        ClearQueueCommand = CreateCommand(
            ReactiveCommand.Create(() => Audio.ClearQueue()));

        ShuffleQueueCommand = CreateCommand(
            ReactiveCommand.Create(() => Audio.ShuffleQueue()));

        DownloadAllCommand = CreateCommand(ReactiveCommand.Create(OnDownloadAll));

        RemoveTrackCommand = CreateCommand(
            ReactiveCommand.Create<TrackItemViewModel>(item => Audio.RemoveFromQueue(item.Track)));

        MoveItemCommand = CreateCommand(
            ReactiveCommand.CreateFromTask<(int oldIndex, int newIndex)>(
                t => CanReorderItems ? MoveItemAsync(t.oldIndex, t.newIndex) : Task.CompletedTask));

        SaveQueueToPlaylistCommand = CreateCommand(ReactiveCommand.CreateFromTask(
            SaveQueueToPlaylistAsync,
            this.WhenAnyValue(x => x.IsEmpty, static empty => !empty)));

        // Подписка на изменение состава очереди в AudioEngine
        Observable.FromEvent(
                h => Audio.OnQueueChanged += h,
                h => Audio.OnQueueChanged -= h)
            .Throttle(TimeSpan.FromMilliseconds(80))
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(_ =>
            {
                if (!_isMovingInternally && !_isSuspended)
                    RefreshFromAudioEngine();
            })
            .DisposeWith(Disposables);

        RefreshFromAudioEngine();
    }

    #endregion

    #region Overrides

    /// <summary>
    /// Перестраивает видимый список и динамически обновляет статусы пустоты и фильтрации.
    /// </summary>
    protected override void RebuildVisibleItems()
    {
        CanReorderItems = string.IsNullOrWhiteSpace(FilterQuery);
        base.RebuildVisibleItems();

        IsEmpty = TotalCount == 0;
        IsFilterEmpty = !IsEmpty && !string.IsNullOrWhiteSpace(FilterQuery) && Items.Count == 0;
    }

    /// <summary>
    /// Фабрикует изолированные VM специально для контекста очереди воспроизведения.
    /// </summary>
    protected override TrackItemViewModel CreateViewModel(TrackInfo track)
    {
        var vm = VmFactory.CreateForQueue(track, PlayFromQueue);

        if (Audio.CurrentTrack?.Id == track.Id)
        {
            vm.SetActive(true, Audio.IsPlaying);
            CurrentActiveVm = vm;
        }

        return vm;
    }

    /// <summary>
    /// Оповещает AudioEngine о внутреннем перетаскивании элемента.
    /// </summary>
    protected override Task SaveMoveAsync(int fromIndex, int toIndex, CancellationToken ct)
    {
        try
        {
            _isMovingInternally = true;
            Audio.MoveQueueItem(fromIndex, toIndex);
        }
        finally
        {
            _isMovingInternally = false;
        }
        return Task.CompletedTask;
    }

    protected override void OnPlay(TrackInfo track) => PlayFromQueue(track);

    protected override Task<List<TrackInfo>> LoadTracksAsync(IEnumerable<string> ids, CancellationToken ct)
    {
        // Очередь не поддерживает загрузку по ID, так как данные хранятся прямо в памяти AudioEngine.
        return Task.FromResult(Audio.Queue.ToList());
    }

    #endregion

    #region Queue Management

    /// <summary>
    /// Синхронизирует визуальное отображение очереди с актуальным состоянием AudioEngine.
    /// Выполняет защитную дедупликацию по ID, чтобы гарантировать стабильность 
    /// инкрементальных обновлений в UI.
    /// </summary>
    private void RefreshFromAudioEngine()
    {
        var queue = Audio.Queue;
        if (queue.Count == 0)
        {
            UpdateMasterData([]);
            return;
        }

        // Zero-alloc дедупликация: гарантируем UX-инвариант "один трек - одна строка"
        var seen = new HashSet<string>(queue.Count, StringComparer.Ordinal);
        var uniqueTracks = new List<TrackInfo>(queue.Count);

        for (int i = 0; i < queue.Count; i++)
        {
            var track = queue[i];
            if (track != null && seen.Add(track.Id))
            {
                uniqueTracks.Add(track);
            }
        }

        UpdateMasterData(uniqueTracks);
    }

    /// <summary>
    /// Запускает скачивание всех треков в очереди, которые еще не загружаются.
    /// </summary>
    private void OnDownloadAll()
    {
        var items = Items;
        for (int i = 0; i < items.Count; i++)
        {
            var item = items[i];
            if (!item.IsDownloading && !item.Track.IsDownloaded)
            {
                _downloads.StartDownload(item.Track);
            }
        }
    }

    private void PlayFromQueue(TrackInfo track) => _ = Audio.PlayTrackAsync(track);

    #endregion

    #region Lifecycle

    protected override void OnSuspend()
    {
        _isSuspended = true;
        Log.Debug("[QueueVM] Suspended");
    }

    protected override void OnResume()
    {
        _isSuspended = false;
        Log.Debug("[QueueVM] Resumed");
        RefreshFromAudioEngine();
    }

    /// <inheritdoc />
    public override async Task OnNavigatedToAsync()
    {
        await base.OnNavigatedToAsync().ConfigureAwait(false);
        RefreshFromAudioEngine();
    }

    /// <inheritdoc />
    protected override void OnAccountChanged()
    {
        base.OnAccountChanged();

        // Синхронизируем визуальное отображение очереди с актуальным стейтом AudioEngine для нового профиля
        RefreshFromAudioEngine();

        Log.Info("[Queue] Playback queue view synchronized with new account state.");
    }

    #endregion

    #region Commands Implementation

    private async Task SaveQueueToPlaylistAsync()
    {
        var tracks = GetLoadedItemsSnapshot();
        if (tracks.Count == 0) return;

        var result = await _dialog.ShowCreatePlaylistDialogAsync();
        if (result is null || string.IsNullOrWhiteSpace(result.Name)) return;

        var playlist = await _library.CreatePlaylistAsync(result.Name.Trim());

        foreach (var track in tracks)
            await _manager.AddTrackToPlaylistAsync(playlist.Id, track);

        Log.Info($"[Queue] Saved {tracks.Count} tracks to playlist '{result.Name}'");
    }

    #endregion

    #region IDisposable

    protected override void Dispose(bool disposing)
    {
        if (_isDisposed) return;

        if (disposing)
        {
            Log.Debug("[QueueVM] Disposing");
            // Базовый класс ReorderableViewModel автоматически очистит Items и утилизирует VM кэш.
        }

        base.Dispose(disposing);
        _isDisposed = true;
    }

    #endregion
}
