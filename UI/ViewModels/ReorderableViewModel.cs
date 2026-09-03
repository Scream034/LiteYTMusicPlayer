using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using ReactiveUI;
using System.Reactive.Linq;
using LMP.UI.Features.Shell;
using Avalonia.Collections;

namespace LMP.UI.ViewModels;

/// <summary>
/// Базовый ViewModel для списков треков с виртуализацией, фильтрацией и перетаскиванием.
/// </summary>
public abstract class ReorderableViewModel<TSource, TViewModel> : ViewModelBase, IFilterable, ISmoothTransitionViewModel
    where TViewModel : class, IDisposable
    where TSource : notnull
{
    #region Fields

    /// <inheritdoc />
    protected override bool HandlesAccountChanges => true;

    protected readonly LibraryService LibService;

    private List<string> _masterIds = [];

    /// <summary>
    /// Канонические source-экземпляры, привязанные к текущему списку.
    /// Производные классы могут использовать словарь для zero-alloc access
    /// без повторной материализации snapshot-коллекций.
    /// </summary>
    protected readonly Dictionary<string, TSource> _sources = [];

    protected readonly Dictionary<string, TViewModel> _vmCache = [];

    private List<TViewModel> _rebuildBuffer = [];

    private CancellationTokenSource? _loadCts;
    private bool _isDisposed;

    private bool _isDataLoading;
    private bool _isTransitioning;
    private TaskCompletionSource? _transitionTcs;

    #endregion

    #region Properties

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

    public string FilterQuery
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = string.Empty;

    public AvaloniaList<TViewModel> Items { get; } = [];

    protected int TotalCount => _masterIds.Count;

    public bool CanReorder => string.IsNullOrWhiteSpace(FilterQuery);

    #endregion

    #region Constructor

    protected ReorderableViewModel()
    {
        LibService = AppEntry.Services.GetRequiredService<LibraryService>();

        this.WhenAnyValue(static x => x.FilterQuery)
            .Skip(1)
            .Throttle(TimeSpan.FromMilliseconds(150))
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(_ => RebuildVisibleItems())
            .DisposeWith(Disposables);
    }

    #endregion

    #region ISmoothTransitionViewModel

    /// <inheritdoc />
    public virtual void PrepareForTransition()
    {
        _isTransitioning = true;
        _transitionTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        this.RaisePropertyChanged(nameof(IsLoading));
    }

    #endregion

    #region Lifecycle

    /// <inheritdoc />
    public override async Task OnNavigatedToAsync()
    {
        _isTransitioning = false;
        _transitionTcs?.TrySetResult();
        this.RaisePropertyChanged(nameof(IsLoading));

        await base.OnNavigatedToAsync().ConfigureAwait(false);
    }

    /// <inheritdoc />
    protected override void OnAccountChanged()
    {
        base.OnAccountChanged();

        CancelLoading();
        Items.Clear();
        _masterIds.Clear();
        _sources.Clear();
        DisposeAndClearVmCache();

        _isDataLoading = true;
    }

    #endregion

    #region Abstract Methods

    protected abstract string GetItemId(TSource item);
    protected abstract TViewModel CreateViewModel(TSource item);
    protected abstract bool MatchesFilter(TSource item, string query);
    protected abstract Task<List<TSource>> LoadItemsByIdsAsync(IEnumerable<string> ids, CancellationToken ct);
    protected virtual Task SaveMoveAsync(int fromIndex, int toIndex, CancellationToken ct) => Task.CompletedTask;

    /// <summary>
    /// Извлекает строковый идентификатор напрямую из экземпляра ViewModel.
    /// Устраняет необходимость хранения обратного словаря _vmToId.
    /// </summary>
    /// <param name="vm">Экземпляр модели представления.</param>
    /// <returns>Уникальный строковый идентификатор элемента.</returns>
    protected abstract string GetViewModelId(TViewModel vm);

    /// <summary>
    /// Нормализует свежезагруженный source-элемент перед помещением в master-слой.
    /// Позволяет производным VM канонизировать объекты и тем самым исключить
    /// рассинхронизацию между _sources и TrackItemViewModel.Track.
    /// </summary>
    /// <param name="item">Свежий source-элемент.</param>
    /// <returns>Нормализованный экземпляр, который должен попасть в _sources.</returns>
    protected virtual TSource NormalizeSourceItem(TSource item) => item;

    /// <summary>
    /// Сливает свежие данные в уже существующий source-экземпляр без замены ссылки.
    /// Это сохраняет identity VM и предотвращает лишние пересоздания UI.
    /// </summary>
    /// <param name="current">Текущий экземпляр из _sources.</param>
    /// <param name="fresh">Свежий нормализованный экземпляр.</param>
    protected virtual void MergeSourceItem(TSource current, TSource fresh) { }

    #endregion

    #region Transition Barrier

    /// <summary>
    /// Асинхронно ожидает завершения анимации перехода страницы, если она активна.
    /// Позволяет подготовить DTO в пуле потоков и не забивать UI-поток до окончания рендеринга.
    /// </summary>
    /// <param name="ct">Токен отмены операции.</param>
    /// <returns>Задача, представляющая ожидание завершения визуального перехода.</returns>
    protected async Task WaitForTransitionAsync(CancellationToken ct)
    {
        var tcs = _transitionTcs;
        if (!_isTransitioning || tcs == null)
            return;

        try
        {
            await tcs.Task.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Корректная отмена при быстрой смене страниц
        }
    }

    /// <summary>
    /// Инициализирует модель предварительно загруженными данными без повторного обращения к хранилищу.
    /// </summary>
    /// <param name="allIds">Целевой master-порядок идентификаторов.</param>
    /// <param name="items">Свежие предварительно загруженные source-элементы.</param>
    protected void InitializeWithPreloadedData(List<string> allIds, List<TSource> items)
    {
        if (_isDisposed) return;

        CancelLoading();
        UpdateMasterData(items, allIds);
    }

    #endregion

    #region Initialization

    /// <summary>
    /// Инициализирует список по master-порядку идентификаторов без разрушения
    /// существующего VM-кэша. На успешной загрузке изменения применяются инкрементально.
    /// При сбое нового контекста текущий список очищается, чтобы не показывать
    /// данные от предыдущего экрана под новой шапкой.
    /// </summary>
    /// <param name="allIds">Целевой master-порядок идентификаторов.</param>
    /// <param name="ct">Токен отмены.</param>
    protected async Task InitializeAsync(List<string> allIds, CancellationToken ct = default)
    {
        if (_isDisposed) return;

        CancelLoading();
        _loadCts = new CancellationTokenSource();
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(_loadCts.Token, ct);
        var linkedCt = linkedCts.Token;

        bool targetOrderChanged = !HasSameIdOrder(_masterIds, allIds);

        try
        {
            if (allIds.Count == 0)
            {
                UpdateMasterData([], allIds);
                return;
            }

            var loaded = await LoadItemsByIdsAsync(allIds, linkedCt);
            if (linkedCt.IsCancellationRequested) return;

            UpdateMasterData(loaded, allIds);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Error($"[ReorderableVM] Load error: {ex.Message}");

            if (targetOrderChanged && !linkedCt.IsCancellationRequested)
                UpdateMasterData([], allIds);
        }
    }

    /// <summary>
    /// Инициализирует модель готовыми данными.
    /// Переписан на инкрементальное обновление master-слоя без потери VM-кэша.
    /// </summary>
    protected void InitializeWithData(IEnumerable<TSource> items)
    {
        UpdateMasterData(items);
    }

    /// <summary>
    /// Инкрементально обновляет master-данные без полной инвалидации кэша ViewModel.
    /// Сохраняет identity существующих source-экземпляров и VM, обновляя только
    /// порядок, состав и metadata реально изменившихся элементов.
    /// </summary>
    /// <param name="items">Свежие source-элементы.</param>
    /// <param name="explicitOrder">
    /// Необязательный master-порядок идентификаторов. Если задан, используется как
    /// источник истинного порядка даже при частичной загрузке source-элементов.
    /// </param>
    protected void UpdateMasterData(IEnumerable<TSource> items, IReadOnlyList<string>? explicitOrder = null)
    {
        if (_isDisposed) return;

        CancelLoading();

        List<string> newMasterIds;
        HashSet<string> retainedIds;

        if (explicitOrder is not null)
        {
            int count = explicitOrder.Count;
            newMasterIds = new List<string>(count);
            retainedIds = new HashSet<string>(count, StringComparer.Ordinal);
            _sources.EnsureCapacity(_sources.Count + count);
            _vmCache.EnsureCapacity(_vmCache.Count + count);

            for (int i = 0; i < count; i++)
            {
                var id = explicitOrder[i];
                newMasterIds.Add(id);
                retainedIds.Add(id);
            }
        }
        else
        {
            newMasterIds = [];
            retainedIds = new HashSet<string>(StringComparer.Ordinal);
        }

        foreach (var rawItem in items)
        {
            var normalized = NormalizeSourceItem(rawItem);
            var id = GetItemId(normalized);
            if (string.IsNullOrEmpty(id)) continue;

            if (explicitOrder is null)
            {
                newMasterIds.Add(id);
                retainedIds.Add(id);
            }
            else if (!retainedIds.Contains(id))
            {
                continue;
            }

            if (_sources.TryGetValue(id, out var current))
            {
                if (!ReferenceEquals(current, normalized))
                    MergeSourceItem(current, normalized);
            }
            else
            {
                _sources[id] = normalized;
            }
        }

        RemoveUnusedSources(retainedIds);

        _masterIds = newMasterIds;

        RebuildVisibleItems();
        DisposeRemovedViewModels(retainedIds);
    }

    #endregion

    #region Filtering

    /// <summary>
    /// Перестраивает видимый список на основе master-порядка и текущего фильтра.
    /// При пустом <see cref="FilterQuery"/> фильтрация пропускается полностью,
    /// гарантируя отображение всех элементов независимо от реализации <see cref="MatchesFilter"/>.
    /// </summary>
    protected virtual void RebuildVisibleItems()
    {
        var query = FilterQuery;
        bool hasFilter = !string.IsNullOrWhiteSpace(query);

        _rebuildBuffer.Clear();
        if (_rebuildBuffer.Capacity < _masterIds.Count)
            _rebuildBuffer.Capacity = _masterIds.Count;

        for (int i = 0; i < _masterIds.Count; i++)
        {
            var id = _masterIds[i];
            if (!_sources.TryGetValue(id, out var source)) continue;
            if (hasFilter && !MatchesFilter(source, query)) continue;

            if (!_vmCache.TryGetValue(id, out var vm))
            {
                vm = CreateViewModel(source);
                _vmCache[id] = vm;
            }

            _rebuildBuffer.Add(vm);
        }

        SyncVisibleItems(_rebuildBuffer);
    }

    /// <summary>
    /// Синхронизирует видимую коллекцию ViewModel.
    /// <para>
    /// Для больших скачков размера списка (фильтрация, сброс поиска) использует атомарную замену,
    /// исключая лавину одиночных событий Add/Remove, которая ломает виртуализатор ItemsRepeater.
    /// Поштучный путь сохраняется только для единичных перестановок и мутаций.
    /// </para>
    /// </summary>
    /// <param name="newItems">Целевой видимый порядок VM.</param>
    private void SyncVisibleItems(List<TViewModel> newItems)
    {
        if (Items.Count == newItems.Count)
        {
            bool same = true;
            for (int i = 0; i < newItems.Count; i++)
            {
                if (!ReferenceEquals(Items[i], newItems[i]))
                {
                    same = false;
                    break;
                }
            }

            if (same) return;
        }

        if (Items.Count == 0)
        {
            if (newItems.Count > 0)
                Items.AddRange(newItems);
            return;
        }

        if (newItems.Count == 0)
        {
            Items.Clear();
            return;
        }

        if (Items.Count <= 3 || Math.Abs(Items.Count - newItems.Count) > 10 || !HasVisibleOverlap(newItems))
        {
            Items.Clear();
            Items.AddRange(newItems);
            return;
        }

        for (int i = Items.Count - 1; i >= 0; i--)
        {
            if (!ContainsReference(newItems, Items[i]))
                Items.RemoveAt(i);
        }

        for (int targetIndex = 0; targetIndex < newItems.Count; targetIndex++)
        {
            var desired = newItems[targetIndex];

            if (targetIndex < Items.Count && ReferenceEquals(Items[targetIndex], desired))
                continue;

            int existingIndex = FindVisibleItemIndex(desired, targetIndex);
            if (existingIndex >= 0)
            {
                if (existingIndex != targetIndex)
                    Items.Move(existingIndex, targetIndex);
            }
            else
            {
                Items.Insert(targetIndex, desired);
            }
        }

        for (int i = Items.Count - 1; i >= newItems.Count; i--)
            Items.RemoveAt(i);
    }

    #endregion

    #region Reordering

    public void MoveItem(int oldIndex, int newIndex)
    {
        if (!ValidateMove(oldIndex, newIndex)) return;

        var movingId = GetVmId(Items[oldIndex]);
        if (string.IsNullOrEmpty(movingId)) return;

        int masterOld = _masterIds.IndexOf(movingId);
        int masterNew = GetMasterIndexForVisualTarget(newIndex);

        if (masterOld >= 0 && masterNew >= 0 && masterOld != masterNew)
        {
            _masterIds.RemoveAt(masterOld);
            _masterIds.Insert(masterNew, movingId);
        }

        Items.Move(oldIndex, newIndex);
    }

    public async Task MoveItemAsync(int oldIndex, int newIndex)
    {
        if (!ValidateMove(oldIndex, newIndex)) return;

        var movingId = GetVmId(Items[oldIndex]);
        if (string.IsNullOrEmpty(movingId)) return;

        int masterOld = _masterIds.IndexOf(movingId);
        int masterNew = GetMasterIndexForVisualTarget(newIndex);

        if (masterOld >= 0 && masterNew >= 0 && masterOld != masterNew)
        {
            _masterIds.RemoveAt(masterOld);
            _masterIds.Insert(masterNew, movingId);
            Items.Move(oldIndex, newIndex);

            try
            {
                await SaveMoveAsync(masterOld, masterNew, CancellationToken.None).ConfigureAwait(false);
                Log.Info("[Reorderable] Move saved to DB");
            }
            catch (Exception ex)
            {
                Log.Error($"[Reorderable] Save move failed: {ex.Message}");

                _masterIds.RemoveAt(masterNew);
                _masterIds.Insert(masterOld, movingId);
                Items.Move(newIndex, oldIndex);
            }
        }
    }

    private bool ValidateMove(int oldIndex, int newIndex)
    {
        if (!CanReorder)
        {
            Log.Warn("[Reorderable] Cannot reorder with active filter");
            return false;
        }

        return oldIndex != newIndex
            && oldIndex >= 0 && oldIndex < Items.Count
            && newIndex >= 0 && newIndex < Items.Count;
    }

    private int GetMasterIndexForVisualTarget(int visualTarget)
    {
        if (visualTarget >= Items.Count) return _masterIds.Count - 1;
        var targetId = GetVmId(Items[visualTarget]);
        return string.IsNullOrEmpty(targetId) ? visualTarget : _masterIds.IndexOf(targetId);
    }

    #endregion

    #region Helpers

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool HasVisibleOverlap(List<TViewModel> newItems)
    {
        for (int i = 0; i < Items.Count; i++)
        {
            var current = Items[i];
            for (int j = 0; j < newItems.Count; j++)
            {
                if (ReferenceEquals(current, newItems[j]))
                    return true;
            }
        }

        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool ContainsReference(List<TViewModel> items, TViewModel item)
    {
        for (int i = 0; i < items.Count; i++)
        {
            if (ReferenceEquals(items[i], item))
                return true;
        }

        return false;
    }

    protected TViewModel? GetCachedVm(string id) => _vmCache.GetValueOrDefault(id);

    protected List<TSource> GetLoadedItemsSnapshot()
    {
        var result = new List<TSource>(_masterIds.Count);

        for (int i = 0; i < _masterIds.Count; i++)
        {
            var id = _masterIds[i];
            if (_sources.TryGetValue(id, out var source))
                result.Add(source);
        }

        return result;
    }

    protected List<string> GetAllIds()
    {
        var result = new List<string>(_masterIds.Count);
        for (int i = 0; i < _masterIds.Count; i++)
            result.Add(_masterIds[i]);

        return result;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private string GetVmId(TViewModel vm) => GetViewModelId(vm);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int FindVisibleItemIndex(TViewModel item, int startIndex)
    {
        for (int i = startIndex; i < Items.Count; i++)
        {
            if (ReferenceEquals(Items[i], item))
                return i;
        }

        return -1;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool HasSameIdOrder(List<string> currentIds, IReadOnlyList<string> newIds)
    {
        if (currentIds.Count != newIds.Count) return false;

        for (int i = 0; i < currentIds.Count; i++)
        {
            if (!string.Equals(currentIds[i], newIds[i], StringComparison.Ordinal))
                return false;
        }

        return true;
    }

    private void RemoveUnusedSources(HashSet<string> retainedIds)
    {
        if (retainedIds.Count == _sources.Count) return;

        List<string>? toRemove = null;

        foreach (var id in _sources.Keys)
        {
            if (retainedIds.Contains(id)) continue;

            toRemove ??= new List<string>();
            toRemove.Add(id);
        }

        if (toRemove is null) return;

        for (int i = 0; i < toRemove.Count; i++)
            _sources.Remove(toRemove[i]);
    }

    private void DisposeRemovedViewModels(HashSet<string> retainedIds)
    {
        if (retainedIds.Count == _vmCache.Count) return;

        List<string>? toRemove = null;

        foreach (var id in _vmCache.Keys)
        {
            if (retainedIds.Contains(id)) continue;

            toRemove ??= new List<string>();
            toRemove.Add(id);
        }

        if (toRemove is null) return;

        for (int i = 0; i < toRemove.Count; i++)
        {
            var id = toRemove[i];
            if (!_vmCache.Remove(id, out var vm)) continue;

            vm.Dispose();
        }
    }

    protected void CancelLoading()
    {
        _loadCts?.Cancel();
        _loadCts?.Dispose();
        _loadCts = null;
    }

    private void DisposeAndClearVmCache()
    {
        foreach (var vm in _vmCache.Values)
            vm.Dispose();

        _vmCache.Clear();
    }

    #endregion

    #region Local Mutations

    protected bool RemoveItemLocally(string id)
    {
        if (string.IsNullOrEmpty(id)) return false;

        bool removed = _masterIds.Remove(id);
        _sources.Remove(id);

        if (_vmCache.Remove(id, out var vm))
        {
            Items.Remove(vm);
            vm.Dispose();
        }

        return removed;
    }

    #endregion

    #region IDisposable

    protected override void Dispose(bool disposing)
    {
        if (_isDisposed) return;

        if (disposing)
        {
            Log.Debug($"[ReorderableVM] Disposing {_vmCache.Count} cached VMs");

            CancelLoading();
            DisposeAndClearVmCache();

            Items.Clear();
            _sources.Clear();
            _masterIds.Clear();
            _rebuildBuffer = null!;
        }

        base.Dispose(disposing);
        _isDisposed = true;
    }

    #endregion
}