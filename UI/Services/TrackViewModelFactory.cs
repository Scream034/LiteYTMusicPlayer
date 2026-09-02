using LMP.UI.Features.Shared;

namespace LMP.UI.Services;

/// <summary>
/// Фабрика для создания изолированных <see cref="TrackItemViewModel"/>.
/// Обеспечивает разделение канонических данных через реестр <see cref="TrackRegistry"/>,
/// изолируя презентационное состояние каждого экрана во избежание утечек контекста и ошибок деструкции.
/// </summary>
/// <remarks>
/// <para>Ранее использовала глобальный кэш на слабых ссылках, что приводило к преждевременному Dispose общих моделей
/// при закрытии отдельных экранов. Текущая реализация полностью изолирует жизненный цикл VM каждого экрана.</para>
/// </remarks>
public sealed class TrackViewModelFactory
{
    private readonly LibraryService _library;
    private readonly DialogService _dialog;
    private readonly AudioEngine _audio;
    private readonly DownloadService _downloads;
    private readonly MusicLibraryManager _manager;
    private readonly TrackRegistry _registry;

    public TrackViewModelFactory(
        LibraryService library,
        DialogService dialog,
        AudioEngine audio,
        DownloadService downloads,
        MusicLibraryManager manager,
        TrackRegistry registry)
    {
        _library = library;
        _dialog = dialog;
        _audio = audio;
        _downloads = downloads;
        _manager = manager;
        _registry = registry;
    }

    /// <summary>
    /// Возвращает канонический экземпляр <see cref="TrackInfo"/> из реестра.
    /// </summary>
    /// <param name="track">Свежий экземпляр трека.</param>
    /// <returns>Канонический экземпляр, используемый всеми экранами и сервисами.</returns>
    public TrackInfo GetCanonicalTrack(TrackInfo track) =>
        _registry.RegisterOrUpdate(track);

    /// <summary>
    /// Возвращает новую изолированную ViewModel для трека.
    /// </summary>
    public TrackItemViewModel GetOrCreate(TrackInfo track, Action<TrackInfo>? playAction = null)
    {
        var canonical = _registry.RegisterOrUpdate(track);
        var vm = CreateVmInstance(canonical, playAction);

        return vm;
    }

    /// <summary>
    /// Создаёт изолированную ViewModel для списка очереди.
    /// </summary>
    public TrackItemViewModel CreateForQueue(TrackInfo track, Action<TrackInfo>? playAction = null)
    {
        var canonical = _registry.RegisterOrUpdate(track);
        var vm = CreateVmInstance(canonical, playAction);

        vm.IsQueueContext = true;

        return vm;
    }

    private TrackItemViewModel CreateVmInstance(TrackInfo track, Action<TrackInfo>? playAction)
    {
        return new TrackItemViewModel(
            track,
            _audio,
            _downloads,
            _manager,
            _dialog,
            _library,
            playAction);
    }
}