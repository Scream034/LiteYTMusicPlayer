using System.Collections.ObjectModel;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using DynamicData;
using Microsoft.Extensions.DependencyInjection;
using ReactiveUI;

using LMP.UI.Features.Shell; // Добавлено

namespace LMP.UI.ViewModels;

public abstract partial class PaginatedViewModel<TSource, TViewModel> : ViewModelBase, IFilterable, ISmoothTransitionViewModel
    where TViewModel : IDisposable
    where TSource : notnull
{
    #region Fields

    protected readonly LibraryService LibService;

    private readonly SourceList<TSource> _sourceList = new();
    private readonly ReadOnlyObservableCollection<TViewModel> _items;
    private readonly CompositeDisposable _dynamicDataSubscriptions = [];
    private readonly HashSet<string> _loadedIds = new(StringComparer.Ordinal);

    private int _consecutiveEmptyLoads;
    private const int MaxConsecutiveEmptyLoads = 5;

    private CancellationTokenSource? _loadCts;
    private bool _canFetchMore;
    private bool _isDisposed;

    // Поля автоматического перехватчика переходов
    private bool _isDataLoading = true;
    private bool _isTransitioning;

    #endregion

    #region Properties

    protected virtual int BatchSize => LibService.Settings.LoadBatchSize > 0
        ? LibService.Settings.LoadBatchSize
        : 20;
    protected virtual int PrefetchThreshold => 10;

    /// <summary>
    /// Управляет видимостью списка. Вычисляет итоговое состояние:
    /// скелетон показывается если грузятся данные ИЛИ идет анимация перехода.
    /// </summary>
    public bool IsLoading
    {
        get => _isDataLoading || _isTransitioning;
        protected set
        {
            if (_isDataLoading == value) return;
            _isDataLoading = value;
            this.RaisePropertyChanged(nameof(IsLoading));
        }
    }

    [Reactive] public partial bool IsLoadingMore { get; protected set; }
    [Reactive] public partial bool IsFetchingFromNetwork { get; protected set; }
    [Reactive] public partial bool HasMoreItems { get; protected set; }
    [Reactive] public partial bool ReachedEnd { get; protected set; }

    public string FilterQuery
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = string.Empty;

    public ReadOnlyObservableCollection<TViewModel> Items => _items;
    protected int TotalCount { get; private set; }

    public ReactiveCommand<Unit, Unit> LoadMoreCommand { get; }

    #endregion

    #region Constructor

    protected PaginatedViewModel()
    {
        LibService = AppEntry.Services.GetRequiredService<LibraryService>();

        var filterPredicate = this.WhenAnyValue(x => x.FilterQuery)
            .Throttle(TimeSpan.FromMilliseconds(200))
            .ObserveOn(RxSchedulers.TaskpoolScheduler)
            .Select(BuildFilterPredicate)
            .StartWith(BuildFilterPredicate(FilterQuery));

        _sourceList.Connect()
            .Filter(filterPredicate)
            .Transform(CreateItemViewModel)
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Bind(out _items)
            .Subscribe()
            .DisposeWith(_dynamicDataSubscriptions);

        var canLoadMore = this.WhenAnyValue(
            x => x.IsLoadingMore,
            x => x.IsLoading,
            x => x.IsFetchingFromNetwork,
            x => x.HasMoreItems,
            static (more, init, net, hasMore) => !more && !init && !net && hasMore);

        LoadMoreCommand = CreateCommand(ReactiveCommand.CreateFromTask(LoadNextBatchAsync, canLoadMore));

        this.WhenAnyValue(x => x.FilterQuery)
            .Subscribe(_ => _consecutiveEmptyLoads = 0)
            .DisposeWith(_dynamicDataSubscriptions);
    }

    #endregion

    #region ISmoothTransitionViewModel

    /// <inheritdoc />
    public virtual void PrepareForTransition()
    {
        _isTransitioning = true;
        this.RaisePropertyChanged(nameof(IsLoading));
    }

    #endregion

    #region Navigation Lifecycle

    public override async Task OnNavigatedToAsync()
    {
        _isTransitioning = false;
        this.RaisePropertyChanged(nameof(IsLoading));

        await base.OnNavigatedToAsync();
    }

    #endregion

    #region Abstract Methods

    protected abstract TViewModel CreateItemViewModel(TSource item);
    protected abstract bool FilterItem(TSource item, string query);
    protected virtual string GetItemId(TSource item) => item?.GetHashCode().ToString() ?? "";

    protected virtual Task<List<TSource>> FetchMoreFromNetworkAsync(CancellationToken ct)
        => Task.FromResult(new List<TSource>());

    #endregion

    #region Private Helpers

    private Func<TSource, bool> BuildFilterPredicate(string query)
    {
        return item => FilterItem(item, query);
    }

    #endregion

    #region Public Methods

    /// <summary>
    /// Переносит элементы в источник данных и сбрасывает состояние пагинации.
    /// </summary>
    protected virtual void MoveSourceItem(int oldIndex, int newIndex)
    {
        _sourceList.Edit(list =>
        {
            if (oldIndex < 0 || oldIndex >= list.Count ||
                newIndex < 0 || newIndex >= list.Count ||
                oldIndex == newIndex)
                return;

            var item = list[oldIndex];
            list.RemoveAt(oldIndex);
            list.Insert(newIndex, item);
        });
    }

    /// <summary>
    /// Инициализирует модель новыми элементами.
    /// </summary>
    protected virtual async Task InitializeItemsAsync(IEnumerable<TSource> items, bool canFetchMore = true)
    {
        if (_isDisposed) return;

        CancelLoading();
        _loadCts = new CancellationTokenSource();
        _canFetchMore = canFetchMore;
        _consecutiveEmptyLoads = 0;

        var itemsList = items as List<TSource> ?? items?.ToList() ?? [];

        _loadedIds.Clear();
        for (int i = 0; i < itemsList.Count; i++)
        {
            var id = GetItemId(itemsList[i]);
            if (!string.IsNullOrEmpty(id))
                _loadedIds.Add(id);
        }

        _sourceList.Edit(innerList =>
        {
            innerList.Clear();
            innerList.AddRange(itemsList);
        });

        TotalCount = _sourceList.Count;
        UpdateState();
    }

    /// <summary>
    /// Полностью очищает список элементов.
    /// </summary>
    protected virtual void ClearItems()
    {
        _sourceList.Clear();
        _loadedIds.Clear();
        TotalCount = 0;
        _canFetchMore = false;
        UpdateState();
    }

    protected List<TSource> GetItemsSnapshot() => [.. _sourceList.Items];
    protected List<string> GetLoadedItemsIds() => [.. _sourceList.Items.Select(GetItemId)];

    #endregion

    #region Loading Logic

    protected void CancelLoading()
    {
        _loadCts?.Cancel();
        _loadCts?.Dispose();
        _loadCts = null;
        IsLoading = false;
        IsLoadingMore = false;
        IsFetchingFromNetwork = false;
    }

    private void UpdateState()
    {
        HasMoreItems = _canFetchMore;
        ReachedEnd = !_canFetchMore && TotalCount > 0;
    }

    protected void SetCanFetchMore(bool value)
    {
        _canFetchMore = value;
        UpdateState();
    }

    private async Task LoadNextBatchAsync()
    {
        if (_isDisposed || IsLoadingMore || IsFetchingFromNetwork || !_canFetchMore) return;

        IsLoadingMore = true;
        IsFetchingFromNetwork = true;

        int countBefore = TotalCount;

        try
        {
            var token = _loadCts?.Token ?? CancellationToken.None;
            var newItems = await FetchMoreFromNetworkAsync(token);

            if (token.IsCancellationRequested || _isDisposed) return;

            if (newItems is { Count: > 0 })
            {
                _sourceList.Edit(list =>
                {
                    for (int i = 0; i < newItems.Count; i++)
                    {
                        var item = newItems[i];
                        var id = GetItemId(item);
                        if (!string.IsNullOrEmpty(id) && _loadedIds.Add(id))
                        {
                            list.Add(item);
                            TotalCount++;
                        }
                    }
                });

                if (TotalCount == countBefore)
                    _consecutiveEmptyLoads++;
                else
                    _consecutiveEmptyLoads = 0;

                if (_consecutiveEmptyLoads >= MaxConsecutiveEmptyLoads)
                    _canFetchMore = false;
            }
            else
            {
                _canFetchMore = false;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Error($"[Paginated] Batch load failure: {ex.Message}");
            _canFetchMore = false;
        }
        finally
        {
            IsLoadingMore = false;
            IsFetchingFromNetwork = false;
            UpdateState();
        }
    }

    #endregion

    #region IDisposable

    protected override void Dispose(bool disposing)
    {
        if (_isDisposed) return;

        if (disposing)
        {
            Log.Debug($"[PaginatedVM] Disposing");

            CancelLoading();
            _dynamicDataSubscriptions.Dispose();

            foreach (var item in _items)
            {
                if (item is IDisposable d)
                    d.Dispose();
            }

            _sourceList.Dispose();
        }

        base.Dispose(disposing);
        _isDisposed = true;
    }

    #endregion
}
