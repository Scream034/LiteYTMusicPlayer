using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;

namespace LMP.Core.Services;

/// <summary>
/// Единый координатор управления воспроизведением.
/// Предоставляет реактивные свойства и команды, синхронизированные между всеми UI компонентами.
///
/// <para><b>Архитектура:</b></para>
/// <list type="bullet">
///   <item>Является единственным подписчиком на события AudioEngine для state tracking</item>
///   <item>Предоставляет BehaviorSubject-based IObservable для PlayerBar, TrayIcon, MediaKeys</item>
///   <item>Работает независимо от suspend/resume состояния окна</item>
///   <item>Является единственной точкой управления громкостью — UI не обращается к AudioEngine напрямую</item>
/// </list>
///
/// <para><b>ForceSync:</b></para>
/// <para>Не публикует повторные значения в CurrentTrack если объект тот же (по Id).
/// Это предотвращает ложный TrackReset при восстановлении из трея.</para>
///
/// <para><b>Shuffle:</b></para>
/// <para>Все изменения ShuffleEnabled ДОЛЖНЫ идти через этот сервис (SetShuffleEnabled / ToggleAutoShuffle),
/// чтобы BehaviorSubject всегда был синхронизирован с AudioEngine.</para>
///
/// <para><b>N-Token Warning:</b></para>
/// <para>Предупреждение о сложной расшифровке публикуется через <see cref="NTokenWarningObservable"/>
/// и одновременно показывается через <see cref="NotificationService"/> (если доступен).</para>
/// </summary>
public sealed class PlayerControlService : IDisposable
{
    private readonly AudioEngine _audio;
    private readonly LibraryService _library;
    private readonly NotificationService? _notificationService;

    private readonly BehaviorSubject<bool> _isPlayingSubject;
    private readonly BehaviorSubject<bool> _isPausedSubject;
    private readonly BehaviorSubject<bool> _isLoadingSubject;
    private readonly BehaviorSubject<TrackInfo?> _currentTrackSubject;
    private readonly BehaviorSubject<RepeatMode> _repeatModeSubject;
    private readonly BehaviorSubject<bool> _shuffleEnabledSubject;
    private readonly BehaviorSubject<int> _queueCountSubject;

    /// <summary>
    /// ID плейлиста, из которого была запущена текущая очередь.
    /// Null = источник не плейлист (Home, Search, одиночный трек).
    ///
    /// <para>Сбрасывается автоматически при полной остановке (IsPlaying=false, IsPaused=false).
    /// Устанавливается только через <see cref="SetActivePlaylistId"/>.</para>
    /// </summary>
    private readonly BehaviorSubject<string?> _activePlaylistIdSubject = new(null);

    /// <summary>
    /// Реактивный поток текущей громкости (0–MaxVolume).
    /// Публикуется при любом изменении: скролл трея, слайдер, программное.
    /// </summary>
    private readonly BehaviorSubject<int> _volumeSubject;

    /// <summary>
    /// Сигнал принудительной синхронизации. Подписчики должны обновить
    /// все свои состояния без полного TrackReset.
    /// </summary>
    private readonly Subject<Unit> _forceSyncSubject = new();

    /// <summary>
    /// Сигнал запроса Resume из любого компонента.
    /// MainWindow подписывается и вызывает RestoreFromTray / BroadcastResume.
    /// </summary>
    private readonly Subject<Unit> _resumeRequestSubject = new();

    /// <summary>
    /// Сигнал предупреждения о сложной расшифровке n-токена.
    /// </summary>
    private readonly Subject<AudioEngine.NTokenWarningInfo> _nTokenWarningSubject = new();

    /// <summary>
    /// Кэш ссылки на текущий трек для корректного dedupe в <see cref="OnTrackChanged"/>.
    /// Позволяет не переиздавать событие если Id трека не изменился.
    /// </summary>
    private TrackInfo? _currentTrack;

    private bool _disposed;

    #region Constructors

    public PlayerControlService(AudioEngine audio, LibraryService library)
        : this(audio, library, null)
    {
    }

    public PlayerControlService(AudioEngine audio, LibraryService library, NotificationService? notificationService)
    {
        _audio = audio;
        _library = library;
        _notificationService = notificationService;

        _currentTrack = _audio.CurrentTrack;

        _isPlayingSubject = new BehaviorSubject<bool>(_audio.IsPlaying);
        _isPausedSubject = new BehaviorSubject<bool>(_audio.IsPaused);
        _isLoadingSubject = new BehaviorSubject<bool>(_audio.IsLoading);
        _currentTrackSubject = new BehaviorSubject<TrackInfo?>(_currentTrack);
        _repeatModeSubject = new BehaviorSubject<RepeatMode>(_audio.RepeatMode);
        _shuffleEnabledSubject = new BehaviorSubject<bool>(_audio.ShuffleEnabled);
        _queueCountSubject = new BehaviorSubject<int>(_audio.Queue.Count);
        _volumeSubject = new BehaviorSubject<int>((int)Math.Round(_audio.GetVolume()));

        _audio.OnPlaybackStateChanged += OnPlaybackStateChanged;
        _audio.OnTrackChanged += OnTrackChanged;
        _audio.OnQueueChanged += OnQueueChanged;
        _audio.OnLoadingStateChanged += OnLoadingStateChanged;
        _audio.OnNTokenDecryptionWarning += OnNTokenDecryptionWarning;

        // Если БД уже загружена — синхронизируем UI-Subjects, если нет — ждем OnInitialized
        if (_library.IsInitialized)
        {
            _audio.InitializeVolumeFromSettings();
            ForceSync();
        }
        else
        {
            _library.OnInitialized += () =>
            {
                _audio.InitializeVolumeFromSettings();
                ForceSync();
            };
        }

        Log.Debug("[PlayerControl] Service initialized");
    }

    #endregion

    #region Properties

    public bool IsPlaying => _isPlayingSubject.Value;
    public bool IsPaused => _isPausedSubject.Value;
    public bool IsLoading => _isLoadingSubject.Value;

    /// <summary>
    /// Текущий трек. Обновляется только при реальной смене Id.
    /// </summary>
    public TrackInfo? CurrentTrack => _currentTrack;

    public RepeatMode RepeatMode => _repeatModeSubject.Value;
    public bool ShuffleEnabled => _shuffleEnabledSubject.Value;
    public bool HasTrack => _currentTrack != null;
    public int QueueCount => _queueCountSubject.Value;

    /// <summary>
    /// Текущая громкость (0–MaxVolume).
    /// Обновляется реактивно через <see cref="VolumeObservable"/>.
    /// </summary>
    public int CurrentVolume => _volumeSubject.Value;

    /// <summary>
    /// Текущий ID плейлиста-источника или null.
    /// </summary>
    public string? ActivePlaylistId => _activePlaylistIdSubject.Value;

    #endregion

    #region Observables

    public IObservable<bool> IsPlayingObservable => _isPlayingSubject.AsObservable();
    public IObservable<bool> IsPausedObservable => _isPausedSubject.AsObservable();
    public IObservable<bool> IsLoadingObservable => _isLoadingSubject.AsObservable();

    /// <summary>
    /// Поток изменений текущего трека.
    /// Публикуется ТОЛЬКО при реальной смене трека (другой Id).
    /// При ForceSync не переиздаётся — используйте ForceSyncObservable.
    /// </summary>
    public IObservable<TrackInfo?> CurrentTrackObservable => _currentTrackSubject.AsObservable();

    public IObservable<RepeatMode> RepeatModeObservable => _repeatModeSubject.AsObservable();
    public IObservable<bool> ShuffleEnabledObservable => _shuffleEnabledSubject.AsObservable();
    public IObservable<int> QueueCountObservable => _queueCountSubject.AsObservable();

    /// <summary>
    /// Реактивный поток изменения громкости.
    /// Используется TrayManager, PlayerBar и другими подписчиками.
    /// </summary>
    public IObservable<int> VolumeObservable => _volumeSubject.AsObservable();

    public IObservable<(bool IsPlaying, bool IsPaused)> PlaybackStateObservable =>
        _isPlayingSubject.CombineLatest(_isPausedSubject, (p, u) => (p, u));

    /// <summary>
    /// Сигнал для подписчиков: "обнови все состояния без TrackReset".
    /// Вызывается при восстановлении из трея / minimize.
    /// </summary>
    public IObservable<Unit> ForceSyncObservable => _forceSyncSubject.AsObservable();

    /// <summary>
    /// Сигнал запроса Resume. MainWindow подписывается и вызывает RestoreFromTray.
    /// </summary>
    public IObservable<Unit> ResumeRequestObservable => _resumeRequestSubject.AsObservable();

    /// <summary>
    /// Реактивный поток ID плейлиста-источника.
    /// Публикует null при остановке или запуске вне плейлиста.
    /// Используется PlaylistViewModel для IsPlayingThisPlaylist.
    /// </summary>
    public IObservable<string?> ActivePlaylistIdObservable =>
        _activePlaylistIdSubject.AsObservable();

    /// <summary>
    /// Сигнал предупреждения о сложной расшифровке n-токена для текущего трека.
    /// Содержит контекст трека и флаг автоматического пропуска.
    /// </summary>
    public IObservable<AudioEngine.NTokenWarningInfo> NTokenWarningObservable => _nTokenWarningSubject.AsObservable();

    #endregion

    #region Commands

    public async Task PlayPauseAsync()
    {
        try
        {
            await _audio.SetPlaybackStateAsync(!_audio.IsPlaying);
        }
        catch (Exception ex)
        {
            Log.Error($"[PlayerControl] PlayPause error: {ex.Message}");
        }
    }

    public async Task NextAsync()
    {
        try
        {
            await _audio.PlayNextAsync();
        }
        catch (Exception ex)
        {
            Log.Error($"[PlayerControl] Next error: {ex.Message}");
        }
    }

    public async Task PreviousAsync()
    {
        try
        {
            await _audio.PlayPreviousAsync();
        }
        catch (Exception ex)
        {
            Log.Error($"[PlayerControl] Previous error: {ex.Message}");
        }
    }

    public void ToggleRepeat()
    {
        var newMode = _audio.RepeatMode switch
        {
            RepeatMode.None => RepeatMode.All,
            RepeatMode.All => RepeatMode.One,
            RepeatMode.One => RepeatMode.None,
            _ => RepeatMode.None
        };

        _audio.RepeatMode = newMode;
        _library.UpdateSettings(s => s.RepeatMode = newMode);
        _repeatModeSubject.OnNext(newMode);

        Log.Debug($"[PlayerControl] RepeatMode changed to {newMode}");
    }

    public void ShuffleQueue()
    {
        _audio.ShuffleQueue();
        Log.Debug("[PlayerControl] Queue shuffled");
    }

    /// <summary>
    /// Переключает авто-перемешивание (toggle).
    /// Синхронизирует AudioEngine, сохраняет в настройки, публикует в Subject.
    /// </summary>
    public void ToggleAutoShuffle()
    {
        bool newState = !_audio.ShuffleEnabled;
        _audio.ShuffleEnabled = newState;

        // При включении shuffle — немедленно перемешать текущую очередь.
        // Пользователь сразу видит случайный порядок в QueueView.
        // При выключении — порядок остаётся как есть (уже перемешан).
        if (newState)
            _audio.ShuffleQueue();

        _library.UpdateSettings(s => s.ShuffleEnabled = newState);
        _shuffleEnabledSubject.OnNext(newState);

        Log.Debug($"[PlayerControl] AutoShuffle changed to {newState}");
    }

    /// <summary>
    /// Устанавливает состояние авто-перемешивания напрямую.
    /// Используется из PlaylistViewModel и других мест, которые хотят
    /// явно установить shuffle = false перед стартом очереди.
    ///
    /// <para><b>ВАЖНО:</b> Все изменения ShuffleEnabled должны идти через этот метод
    /// или ToggleAutoShuffle(), чтобы BehaviorSubject оставался синхронизированным.</para>
    /// </summary>
    /// <param name="enabled">Новое состояние авто-перемешивания.</param>
    public void SetShuffleEnabled(bool enabled)
    {
        if (_audio.ShuffleEnabled == enabled)
            return;

        _audio.ShuffleEnabled = enabled;
        _library.UpdateSettings(s => s.ShuffleEnabled = enabled);
        _shuffleEnabledSubject.OnNext(enabled);

        Log.Debug($"[PlayerControl] ShuffleEnabled set to {enabled}");
    }

    /// <summary>
    /// Устанавливает ID плейлиста-источника текущей очереди.
    /// Вызывается из PlaylistViewModel перед StartQueueAsync.
    /// Null = очередь запущена не из плейлиста.
    /// </summary>
    public void SetActivePlaylistId(string? playlistId)
    {
        if (_activePlaylistIdSubject.Value == playlistId) return;
        _activePlaylistIdSubject.OnNext(playlistId);
        Log.Debug($"[PlayerControl] ActivePlaylistId = {playlistId ?? "null"}");
    }

    /// <summary>
    /// Изменяет громкость на указанный шаг (положительный или отрицательный).
    /// Используется для колеса мыши в трее и горячих клавиш.
    /// </summary>
    /// <param name="delta">Величина изменения громкости.</param>
    /// <returns>Новое значение громкости.</returns>
    public int AdjustVolume(int delta)
    {
        int currentVolume = (int)Math.Round(_audio.GetVolume());
        int maxVolume = _library.Settings.MaxVolumeLimit;
        if (maxVolume <= 0) maxVolume = 100;

        int newVolume = Math.Clamp(currentVolume + delta, 0, maxVolume);
        SetVolume(newVolume);
        return newVolume;
    }

    /// <summary>
    /// Устанавливает громкость воспроизведения, применяет её к аудио-пайплайну и планирует фоновое сохранение.
    /// Является единственной точкой входа для изменения громкости во всём приложении.
    /// </summary>
    /// <param name="volume">Новое значение громкости (0–MaxVolume).</param>
    public void SetVolume(int volume)
    {
        int maxVolume = _library.Settings.MaxVolumeLimit;
        if (maxVolume <= 0) maxVolume = 100;

        int clamped = Math.Clamp(volume, 0, maxVolume);
        int current = (int)Math.Round(_audio.GetVolume());

        if (clamped != current)
        {
            _audio.SetVolumeInstant(clamped);
            _volumeSubject.OnNext(clamped);
        }

        // Обновляем настройки; дебаунсер в LibraryService сам запишет их на диск без фризов UI
        _library.UpdateSettings(s => s.Volume = clamped);
    }

    /// <summary>
    /// Возвращает текущую громкость из AudioEngine (округлённую до int).
    /// </summary>
    public int GetCurrentVolume() => (int)Math.Round(_audio.GetVolume());

    /// <summary>
    /// Возвращает максимальную громкость из настроек.
    /// </summary>
    public int GetMaxVolume()
    {
        int max = _library.Settings.MaxVolumeLimit;
        return max > 0 ? max : 100;
    }

    /// <summary>
    /// Запрашивает Resume у MainWindow (через ResumeRequestObservable).
    /// Вызывается когда пользователь взаимодействует с UI в suspend-режиме.
    /// </summary>
    public void RequestResume()
    {
        _resumeRequestSubject.OnNext(Unit.Default);
    }

    #endregion

    #region AudioEngine Event Handlers

    private void OnPlaybackStateChanged(bool isPlaying, bool isPaused)
    {
        _isPlayingSubject.OnNext(isPlaying);
        _isPausedSubject.OnNext(isPaused);
    }

    /// <summary>
    /// Обрабатывает смену трека из AudioEngine.
    /// Не переиздаёт событие если Id трека не изменился,
    /// чтобы предотвратить ложные TrackReset при восстановлении из трея.
    /// </summary>
    private void OnTrackChanged(TrackInfo? track)
    {
        var previous = _currentTrack;
        _currentTrack = track;

        if (previous?.Id == track?.Id)
            return;

        _currentTrackSubject.OnNext(track);

        // Сбрасываем источник только при реальной остановке (track → null).
        // При переходе между треками (prev != null → new != null) источник сохраняется —
        // это нормально: пользователь слушает тот же плейлист.
        if (track == null && _activePlaylistIdSubject.Value != null)
        {
            _activePlaylistIdSubject.OnNext(null);
            Log.Debug("[PlayerControl] ActivePlaylistId cleared (track → null)");
        }
    }

    private void OnQueueChanged()
    {
        _queueCountSubject.OnNext(_audio.Queue.Count);
    }

    private void OnLoadingStateChanged(bool isLoading)
    {
        _isLoadingSubject.OnNext(isLoading);
    }

    /// <summary>
    /// Публикует предупреждение о сложной расшифровке n-токена
    /// и, если это разрешено настройками, либо добавляет его в центр уведомлений,
    /// либо показывает toast.
    /// </summary>
    private void OnNTokenDecryptionWarning(AudioEngine.NTokenWarningInfo warning)
    {
        _nTokenWarningSubject.OnNext(warning);

        if (_notificationService == null)
            return;

        var mode = _library.Settings.Audio.NTokenNotificationMode;
        switch (mode)
        {
            case NTokenNotificationMode.Disabled:
                Log.Debug($"[PlayerControl] N-Token warning suppressed for track '{warning.Track?.Id}' as configured.");
                return;

            case NTokenNotificationMode.PanelOnly:
                _ = PublishNTokenWarningAsync(_notificationService, warning, showToast: false);
                return;

            default:
                _ = PublishNTokenWarningAsync(_notificationService, warning, showToast: true);
                return;
        }
    }

    /// <summary>
    /// Публикует уведомление о сложной расшифровке n-токена
    /// либо как toast, либо только в центр уведомлений.
    /// </summary>
    private static async Task PublishNTokenWarningAsync(
        NotificationService notificationService,
        AudioEngine.NTokenWarningInfo warning,
        bool showToast)
    {
        try
        {
            var track = warning.Track;
            string trackDisplay = track?.Title ?? track?.Id ?? "Unknown";
            string? trackTitle = track?.Title ?? track?.Id;
            string messageKey = warning.WasSkipped
                ? "Notification_NToken_Skipped"
                : "Notification_NToken_Message";

            if (showToast)
            {
                await notificationService.ShowToastAsync(
                    titleKey: "Notification_NToken_Title",
                    messageKey: messageKey,
                    severity: NotificationSeverity.Warning,
                    messageArgs: [trackDisplay],
                    trackId: track?.Id,
                    trackTitle: trackTitle);
            }
            else
            {
                await notificationService.AddToPanelAsync(
                    titleKey: "Notification_NToken_Title",
                    messageKey: messageKey,
                    severity: NotificationSeverity.Warning,
                    messageArgs: [trackDisplay],
                    trackId: track?.Id,
                    trackTitle: trackTitle);
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"[PlayerControl] Failed to publish n-token warning: {ex.Message}");
        }
    }

    #endregion

    #region Sync

    /// <summary>
    /// Принудительная синхронизация всех состояний при восстановлении из трея.
    ///
    /// <para><b>ВАЖНО:</b> НЕ переиздаёт CurrentTrack если трек тот же самый (по Id).
    /// Это предотвращает ложный BeginTrackReset → замораживание UI.</para>
    ///
    /// <para>Вместо этого публикует ForceSyncObservable, на который PlayerBarViewModel
    /// подписывается для мягкого обновления (позиция, буфер, стрим-инфо).</para>
    ///
    /// <para>Если реальный трек в AudioEngine отличается от кэшированного (по Id),
    /// публикует новый трек через CurrentTrackObservable.</para>
    /// </summary>
    public void ForceSync()
    {
        _isPlayingSubject.OnNext(_audio.IsPlaying);
        _isPausedSubject.OnNext(_audio.IsPaused);
        _isLoadingSubject.OnNext(_audio.IsLoading);
        _repeatModeSubject.OnNext(_audio.RepeatMode);
        _shuffleEnabledSubject.OnNext(_audio.ShuffleEnabled);
        _queueCountSubject.OnNext(_audio.Queue.Count);
        _volumeSubject.OnNext((int)Math.Round(_audio.GetVolume()));

        var actualTrack = _audio.CurrentTrack;
        if (_currentTrack?.Id != actualTrack?.Id)
        {
            _currentTrack = actualTrack;
            _currentTrackSubject.OnNext(actualTrack);
        }
        else
        {
            // Обновляем ссылку без переиздания события
            _currentTrack = actualTrack;
        }

        _forceSyncSubject.OnNext(Unit.Default);

        Log.Debug("[PlayerControl] Forced sync completed (soft, no track reset)");
    }

    #endregion

    #region IDisposable

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _audio.OnPlaybackStateChanged -= OnPlaybackStateChanged;
        _audio.OnTrackChanged -= OnTrackChanged;
        _audio.OnQueueChanged -= OnQueueChanged;
        _audio.OnLoadingStateChanged -= OnLoadingStateChanged;
        _audio.OnNTokenDecryptionWarning -= OnNTokenDecryptionWarning;

        _isPlayingSubject.Dispose();
        _isPausedSubject.Dispose();
        _isLoadingSubject.Dispose();
        _currentTrackSubject.Dispose();
        _repeatModeSubject.Dispose();
        _shuffleEnabledSubject.Dispose();
        _queueCountSubject.Dispose();
        _volumeSubject.Dispose();
        _activePlaylistIdSubject.Dispose();
        _forceSyncSubject.Dispose();
        _resumeRequestSubject.Dispose();
        _nTokenWarningSubject.Dispose();

        Log.Debug("[PlayerControl] Service disposed");
    }

    #endregion
}