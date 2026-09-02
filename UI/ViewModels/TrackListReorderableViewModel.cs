using System.Reactive.Linq;
using LMP.UI.Features.Shared;
using ReactiveUI;

namespace LMP.UI.ViewModels;

/// <summary>
/// Абстрактный базовый класс для экранов с переупорядочиваемым списком треков (Playlist и т.п.).
/// Фиксирует generic-параметры ReorderableViewModel на (TrackInfo, TrackItemViewModel)
/// и добавляет тот же Smart Parent паттерн что и <see cref="TrackListPaginatedViewModel"/>.
/// </summary>
public abstract class TrackListReorderableViewModel
    : ReorderableViewModel<TrackInfo, TrackItemViewModel>
{
    #region Fields

    protected readonly AudioEngine Audio;
    protected readonly DownloadService Downloads;
    protected readonly TrackViewModelFactory VmFactory;

    protected TrackItemViewModel? CurrentActiveVm;

    #endregion

    #region Constructor

    protected TrackListReorderableViewModel(
        AudioEngine audio,
        DownloadService downloads,
        TrackViewModelFactory vmFactory)
    {
        Audio = audio;
        Downloads = downloads;
        VmFactory = vmFactory;

        SubscribeToAudioEngine();
        SubscribeToDownloadService();
        SubscribeToCacheManager();
    }

    #endregion

    #region Source Normalization

    /// <summary>
    /// Канонизирует входящий экземпляр <see cref="TrackInfo"/> перед сохранением в _sources.
    /// После этого source-слой и VM смотрят на один и тот же объект.
    /// </summary>
    /// <param name="item">Свежий экземпляр трека.</param>
    /// <returns>Канонический экземпляр из <see cref="TrackRegistry"/>.</returns>
    protected override TrackInfo NormalizeSourceItem(TrackInfo item) =>
        VmFactory.GetCanonicalTrack(item);

    /// <summary>
    /// Сливает свежие данные трека в существующий экземпляр без замены ссылки.
    /// Сохраняет привязки UI и обновляет только реально изменившиеся свойства.
    /// </summary>
    /// <param name="current">Текущий канонический экземпляр.</param>
    /// <param name="fresh">Свежий нормализованный экземпляр.</param>
    protected override void MergeSourceItem(TrackInfo current, TrackInfo fresh)
    {
        if (ReferenceEquals(current, fresh)) return;

        current.UpdateMetadata(fresh);

        if (fresh.IsDownloaded && !current.IsDownloaded)
        {
            if (!string.IsNullOrEmpty(fresh.LocalPath))
            {
                current.MarkAsDownloaded(
                    fresh.LocalPath,
                    fresh.PreferredFormat,
                    fresh.PreferredBitrate);
            }
            else
            {
                current.IsDownloaded = true;
                current.IsCached = true;
            }
        }
        else if (fresh.IsCached && !current.IsCached)
        {
            current.MarkAsCached(fresh.PreferredFormat, fresh.PreferredBitrate);
        }

        if (!string.IsNullOrEmpty(fresh.LocalPath) && fresh.LocalPath != current.LocalPath)
            current.LocalPath = fresh.LocalPath;

        if (fresh.PreferredFormat.HasValue &&
            fresh.PreferredFormat != current.PreferredFormat)
        {
            current.PreferredFormat = fresh.PreferredFormat;
        }

        if (fresh.PreferredBitrate > 0 && fresh.PreferredBitrate != current.PreferredBitrate)
            current.PreferredBitrate = fresh.PreferredBitrate;
    }

    #endregion

    #region Smart Parent — Audio

    private void SubscribeToAudioEngine()
    {
        Observable.FromEvent<Action<TrackInfo?>, TrackInfo?>(
                h => Audio.OnTrackChanged += h,
                h => Audio.OnTrackChanged -= h)
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(track => UpdatePlaybackState(track, Audio.IsPlaying))
            .DisposeWith(Disposables);

        Observable.FromEvent<Action<bool, bool>, (bool isPlaying, bool isPaused)>(
                h => (a, b) => h((a, b)),
                h => Audio.OnPlaybackStateChanged += h,
                h => Audio.OnPlaybackStateChanged -= h)
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(x => UpdatePlaybackState(Audio.CurrentTrack, x.isPlaying))
            .DisposeWith(Disposables);
    }

    /// <summary>
    /// O(1): lookup через GetCachedVm — обратный индекс в ReorderableViewModel._vmCache.
    /// Не использует TrackViewModelFactory.TryGet намеренно: ReorderableViewModel
    /// создаёт VM через CreateViewModel (не factory-кэш), поэтому ищем в своём кэше.
    /// </summary>
    private void UpdatePlaybackState(TrackInfo? currentTrack, bool isPlaying)
    {
        if (CurrentActiveVm != null && CurrentActiveVm.Id != currentTrack?.Id)
        {
            CurrentActiveVm.SetActive(false, false);
            CurrentActiveVm = null;
        }

        if (currentTrack is null) return;

        CurrentActiveVm ??= GetCachedVm(currentTrack.Id);
        CurrentActiveVm?.SetActive(true, isPlaying);
    }

    #endregion

    #region Smart Parent — Downloads

    private void SubscribeToDownloadService()
    {
        Observable.FromEvent<Action<string, float>, (string id, float progress)>(
                h => (id, p) => h((id, p)),
                h => Downloads.OnProgress += h,
                h => Downloads.OnProgress -= h)
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(x => GetCachedVm(x.id)?.SetDownloadState(true, x.progress))
            .DisposeWith(Disposables);

        Observable.FromEvent<Action<string, bool, string?>, (string id, bool ok, string? path)>(
                h => (id, ok, path) => h((id, ok, path)),
                h => Downloads.OnCompleted += h,
                h => Downloads.OnCompleted -= h)
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(x => GetCachedVm(x.id)?.SetDownloadState(false, 0f))
            .DisposeWith(Disposables);
    }

    #endregion

    #region Smart Parent — Cache

    /// <summary>
    /// Подписывается на событие завершения кэширования и обновляет канонический
    /// <see cref="TrackInfo"/> без пересоздания ViewModel.
    /// </summary>
    private void SubscribeToCacheManager()
    {
        var cache = AudioSourceFactory.GlobalCache;
        if (cache is null) return;

        Observable.FromEvent<Action<string, AudioFormat, int, bool>, (string trackId, AudioFormat format, int bitrate, bool isExport)>(
                h => (id, format, bitrate, isExport) => h((id, format, bitrate, isExport)),
                h => cache.OnFormatCached += h,
                h => cache.OnFormatCached -= h)
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(x =>
            {
                if (!_sources.TryGetValue(x.trackId, out var track)) return;
                if (!track.IsCached)
                    track.MarkAsCached(x.format, x.bitrate);
            })
            .DisposeWith(Disposables);
    }

    /// <summary>
    /// Гидрирует cache-status для всех текущих source-элементов.
    /// Поскольку _sources хранит канонические экземпляры, обновления автоматически
    /// доезжают до UI через <see cref="TrackItemViewModel"/> без reset списка.
    /// </summary>
    /// <param name="ct">Токен отмены.</param>
    protected async Task HydrateCacheStatusAsync(CancellationToken ct = default)
    {
        var cache = AudioSourceFactory.GlobalCache;
        if (cache is null || _sources.Count == 0) return;

        try
        {
            var tracks = new List<TrackInfo>(_sources.Count);
            foreach (var track in _sources.Values)
                tracks.Add(track);

            await Task.Run(() => cache.HydrateCacheStatus(tracks), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Warn($"[TrackListReorderable] Cache hydration error: {ex.Message}");
        }
    }

    #endregion

    #region ReorderableViewModel Overrides

    protected sealed override string GetItemId(TrackInfo item) => item.Id;

    protected sealed override bool MatchesFilter(TrackInfo item, string query) =>
        TrackFilters.MatchesTitleOrAuthor(item, query);

    /// <summary>
    /// Создаёт VM через factory с привязкой <see cref="OnPlay"/> и начальным активным состоянием.
    /// Не sealed: наследники могут переопределить для дополнительной настройки VM.
    /// </summary>
    protected override TrackItemViewModel CreateViewModel(TrackInfo track)
    {
        var vm = VmFactory.GetOrCreate(track, OnPlay);

        if (Audio.CurrentTrack?.Id == track.Id)
        {
            vm.SetActive(true, Audio.IsPlaying);
            CurrentActiveVm = vm;
        }

        return vm;
    }

    protected sealed override Task<List<TrackInfo>> LoadItemsByIdsAsync(
        IEnumerable<string> ids, CancellationToken ct) =>
        LoadTracksAsync(ids, ct);

    #endregion

    #region Abstract

    /// <summary>Вызывается при нажатии Play на треке.</summary>
    protected abstract void OnPlay(TrackInfo track);

    /// <summary>
    /// Загружает треки по списку ID.
    /// Используется ReorderableViewModel при инициализации по ID-списку.
    /// </summary>
    protected abstract Task<List<TrackInfo>> LoadTracksAsync(
        IEnumerable<string> ids, CancellationToken ct);

    #endregion
}