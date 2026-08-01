using System.Collections.ObjectModel;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using Avalonia;
using Avalonia.Media;
using LMP.Core.Youtube.Exceptions;
using ReactiveUI;


namespace LMP.UI.Features.Player;

/// <summary>
/// ViewModel для нижней панели управления плеером (Player Bar).
/// </summary>
/// 
public sealed partial class PlayerBarViewModel : ViewModelBase
{
    #region Constants - UI & UX

    private const int NavigationDebounceMs = 300;
    private const int HintDisplayDurationMs = 1500;
    private const int PositionUpdateThrottleMs = 50;
    private const int FallbackPositionIntervalMs = 500;
    private const int ShuffleAnimationDurationMs = 500;

    /// <summary>
    /// Максимальная допустимая разница битрейтов (kbps) для сопоставления
    /// скачанного формата с записью в кэше.
    /// Исключает ложные совпадения в пределах одного нормализованного bucket'а
    /// (например, 55 и 69 kbps → оба попадают в bucket 64, delta = 14 > 10).
    /// </summary>
    private const int BitrateMatchEpsilonKbps = 10;

    /// <summary>Таймаут, после которого состояние Reset считается зависшим (сек).</summary>
    private const int StaleResetTimeoutSec = 3;

    /// <summary>Порог времени воспроизведения для определения активности аудио (сек).</summary>
    private const double AudioIsPlayingThresholdSec = 0.5;

    #endregion

    #region Constants - Audio & Volume

    private const int DefaultVolume = 50;
    private const int DefaultMaxVolume = 100;
    private const int VolumeLowThresholdPercent = 33;
    private const int VolumeMediumThresholdPercent = 66;
    private const double VolumeBoostDivisor = 2.0;
    private const float MaxGainClamp = 4.0f;
    private const int VolumeScrollStepDivisor = 200;
    private const string OrangeWarnHex = "#FFB86C";

    #endregion

    #region Constants - Network & Buffer

    private const int BufferStateThrottleMs = 100;

    #endregion

    #region Fields

    private readonly AudioEngine _audio = null!;
    private readonly LibraryService _library = null!;
    private readonly YoutubeProvider _youtube = null!;
    private readonly MusicLibraryManager _musicManager = null!;
    private readonly PlayerControlService _playerControl = null!;
    private readonly NotificationService _notificationService = null!;

    private readonly Subject<Unit> _nextSubject = new();
    private readonly Subject<Unit> _prevSubject = new();

    /// <summary>
    /// Кэш треков, для которых получение форматов невозможно из-за контентных ограничений.
    /// Ключ = trackId, значение = причина ограничения.
    /// Не включает сетевые ошибки — они транзиентны и не кэшируются.
    /// </summary>
    private readonly Dictionary<string, LoginRequiredReason> _restrictedTracks = [];
    private CancellationTokenSource? _formatsCts;

    private CompositeDisposable? _heavySubscriptions;

    private bool _isSeeking;
    private bool _isInitialized;

    private int _lastVolumeBeforeMute = DefaultVolume;

    private DateTime _trackResetStartTime;
    private string? _pendingStreamInfoTrackId;
    private string? _lastHandledTrackId;
    private string _lastValidStreamInfo = "";
    private int _cachedEffectivePercent;

    private CancellationTokenSource? _activeHintCts;

    #endregion

    #region Properties - Playback State

    [Reactive] public partial TrackInfo? CurrentTrack { get; private set; }
    [Reactive] public partial bool IsLoading { get; private set; }
    [Reactive] public partial bool IsPlaying { get; private set; }
    [Reactive] public partial bool IsPaused { get; private set; }
    [Reactive] public partial bool HasTrack { get; private set; }
    [Reactive] public partial bool IsLiked { get; private set; }
    [Reactive] public partial bool IsNavigating { get; private set; }
    [Reactive] public partial bool IsTrackResetting { get; private set; }
    [Reactive] public partial bool IsFormatsLoading { get; private set; }

    public string? CurrentTrackUrl => CurrentTrack?.Url;

    public string SafeTitle => CurrentTrack?.Title ?? SL["Player_NotPlaying"];
    public string SafeAuthor => CurrentTrack?.Author ?? "";
    public string? SafeThumbnail => CurrentTrack?.ThumbnailUrl;

    #endregion

    #region Properties - Queue Info

    [Reactive] public partial int CurrentTrackIndex { get; private set; }
    [Reactive] public partial int TotalTracksInQueue { get; private set; }
    [Reactive] public partial bool HasQueueToShuffle { get; private set; }

    public string CurrentTrackIndexDisplay => (CurrentTrackIndex + 1).ToString();

    #endregion

    #region Properties - Seek & Duration

    [Reactive] public partial TimeSpan Position { get; set; }
    [Reactive] public partial TimeSpan Duration { get; private set; }
    [Reactive] public partial double PositionSeconds { get; set; }
    [Reactive] public partial double DurationSeconds { get; private set; }
    [Reactive] public partial bool IsSeekBusy { get; private set; }
    [Reactive] public partial bool IsSeekPreviewVisible { get; set; }

    #endregion

    #region Properties - Buffer Progress

    [Reactive] public partial double BufferProgressPercent { get; private set; }
    [Reactive] public partial IReadOnlyList<(double Start, double End)> BufferedRanges { get; private set; } = [];
    public bool UseSegmentedBuffer => BufferedRanges.Count > 1;
    [Reactive] public partial bool IsFullyBuffered { get; private set; }

    #endregion

    #region Properties - Volume

    [Reactive] public partial int Volume { get; set; }
    [Reactive] public partial int MaxVolume { get; private set; } = DefaultMaxVolume;
    [Reactive] public partial bool IsVolumePopupOpen { get; set; }
    [Reactive] public partial bool IsVolumePreviewVisible { get; set; }

    public float RealGain
    {
        get
        {
            float vol = _audio.GetVolume();
            return vol > 0 ? Math.Clamp(vol / 100f, 0f, MaxGainClamp) : 0f;
        }
    }

    public bool IsReallyBoosted
    {
        get
        {
            var settings = _library.Settings.Audio;
            if (!settings.VolumeBoostEnabled) return false;
            return Volume > AudioEngine.VolumeNormalRange;
        }
    }

    public bool IsMuted => Volume < 1;
    public bool IsVolumeLow => Volume >= 1 && !IsReallyBoosted && _cachedEffectivePercent <= VolumeLowThresholdPercent;
    public bool IsVolumeMedium => !IsMuted && !IsReallyBoosted && _cachedEffectivePercent > VolumeLowThresholdPercent && _cachedEffectivePercent <= VolumeMediumThresholdPercent;
    public bool IsVolumeHigh => !IsMuted && !IsReallyBoosted && _cachedEffectivePercent > VolumeMediumThresholdPercent;
    public bool IsVolumeBoosted => IsReallyBoosted;

    private void RecalcEffectivePercent()
    {
        var settings = _library.Settings.Audio;

        if (settings.VolumeBoostEnabled)
        {
            _cachedEffectivePercent = Volume <= AudioEngine.VolumeNormalRange
                ? (int)(Volume / VolumeBoostDivisor)
                : 100;
        }
        else
        {
            int maxVol = MaxVolume > 0 ? MaxVolume : DefaultMaxVolume;
            _cachedEffectivePercent = (int)((double)Volume / maxVol * 100);
        }
    }

    public IBrush VolumePercentBrush
    {
        get
        {
            var app = Application.Current;
            if (app == null) return Brushes.White;

            string resourceKey = IsReallyBoosted ? "SystemWarnOrangeBrush" : "TextPrimaryBrush";
            if (app.Resources.TryGetResource(resourceKey, app.ActualThemeVariant, out var brush) && brush is IBrush b)
                return b;

            return IsReallyBoosted
                ? new SolidColorBrush(Color.Parse(OrangeWarnHex))
                : Brushes.White;
        }
    }

    #endregion

    #region Properties - Repeat & Shuffle

    [Reactive] public partial bool IsShuffleAnimating { get; private set; }
    [Reactive] public partial bool AutoShuffleEnabled { get; private set; }
    [Reactive] public partial RepeatMode RepeatMode { get; set; }

    public bool IsRepeatNone => RepeatMode == RepeatMode.None;
    public bool IsRepeatOne => RepeatMode == RepeatMode.One;
    public bool IsRepeatAll => RepeatMode == RepeatMode.All;

    #endregion

    #region Properties - Hints

    [Reactive] public partial bool IsRepeatHintVisible { get; private set; }
    [Reactive] public partial string RepeatHintText { get; private set; } = "";
    [Reactive] public partial bool IsLikeHintVisible { get; private set; }
    [Reactive] public partial string LikeHintText { get; private set; } = "";

    #endregion

    #region Properties - Stream Info

    [Reactive] public partial string StreamInfo { get; private set; } = "";
    [Reactive] public partial bool ShowStreamInfo { get; private set; }
    [Reactive] public partial string NetworkSpeedText { get; private set; } = "";
    [Reactive] public partial string PingText { get; private set; } = "";
    [Reactive] public partial IBrush PingBrush { get; private set; } = Brushes.White;
    [Reactive] public partial FontWeight PingWeight { get; private set; } = FontWeight.SemiBold;
    [Reactive] public partial bool ShowNetworkStats { get; private set; }

    public ObservableCollection<StreamOption> AvailableFormats { get; } = [];

    #endregion

    #region Properties - Tooltips

    public string ShuffleTooltip => AutoShuffleEnabled
        ? SL["Player_Shuffle_AutoEnabled"]
        : SL["Player_Shuffle_AutoDisabled"];

    public static string PreviousTooltip => SL["Player_Previous"];
    public static string NextTooltip => SL["Player_Next"];

    public string PlayPauseTooltip => IsPlaying
        ? SL["Player_Pause"]
        : SL["Player_Play"];

    public string RepeatTooltip => RepeatMode switch
    {
        RepeatMode.None => SL["Player_Repeat_Off"],
        RepeatMode.All => SL["Player_Repeat_All"],
        RepeatMode.One => SL["Player_Repeat_One"],
        _ => ""
    };

    public string LikeTooltip => IsLiked
        ? SL["Track_Unlike"]
        : SL["Track_Like"];

    public string MuteTooltip => IsMuted
        ? SL["Player_Unmute"]
        : SL["Player_Mute"];

    public string TrackNumberTooltip => string.Format(
        SL["Player_TrackNumber"],
        CurrentTrackIndex + 1,
        TotalTracksInQueue);

    public string DurationTooltip
    {
        get
        {
            if (IsLoading || DurationSeconds <= 0)
                return SL["Player_Loading_Duration"];

            return string.Format(
                SL["Player_Duration"],
                FormatTime(Position),
                FormatTime(Duration));
        }
    }

    #endregion

    #region Commands

    public ReactiveCommand<Unit, Unit> PlayPauseCommand { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> PreviousCommand { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> NextCommand { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> ShuffleQueueCommand { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> ToggleAutoShuffleCommand { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> ToggleRepeatCommand { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> ToggleLikeCommand { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> ToggleMuteCommand { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> CopyLinkCommand { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> LoadFormatsCommand { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> ForceLoadFormatsCommand { get; private set; } = null!;
    public ReactiveCommand<StreamOption, Unit> SwitchFormatCommand { get; private set; } = null!;

    #endregion

    #region Events for View

    public event Action? SuspendRequested;
    public event Action? ResumeRequested;

    #endregion

    #region Constructor

    public PlayerBarViewModel(
        AudioEngine audio,
        LibraryService library,
        YoutubeProvider youtube,
        MusicLibraryManager musicManager,
        PlayerControlService playerControl,
        NotificationService notificationService)
    {
        _audio = audio;
        _library = library;
        _youtube = youtube;
        _musicManager = musicManager;
        _playerControl = playerControl;
        _notificationService = notificationService;

        Log.Debug("[PlayerBar] Created, initializing...");

        LocalizationService.Instance.LanguageChanged += OnLanguageChanged;
        _youtube.AuthService.OnAuthStateChanged += ClearRestrictedTracksCache;

        InitializeFromSettings();
        SetupCommands();
        SubscribeLightweight();
        SubscribeHeavy();

        Log.Info("[PlayerBar] Initialization complete");
    }

    #endregion

    #region Setup

    private void InitializeFromSettings()
    {
        var settings = _library.Settings;

        int newMax = Math.Max(settings.MaxVolumeLimit, DefaultMaxVolume);
        MaxVolume = newMax;

        int savedVolume = settings.LastVolume;
        if (savedVolume > 0 && savedVolume <= MaxVolume)
        {
            Volume = savedVolume;
            _lastVolumeBeforeMute = savedVolume;
        }
        else if (savedVolume > MaxVolume)
        {
            Volume = MaxVolume;
            _lastVolumeBeforeMute = MaxVolume;
        }
        else
        {
            Volume = DefaultVolume;
            _lastVolumeBeforeMute = DefaultVolume;
        }

        AutoShuffleEnabled = _playerControl.ShuffleEnabled;
        RepeatMode = _playerControl.RepeatMode;

        // Синхронизируем начальное значение с координатором, минуя лишний дисковый I/O
        _playerControl.SetVolumeFast(Volume);
        RecalcEffectivePercent();

        _isInitialized = true;
        RaiseVolumePropertiesChanged();
        UpdateQueueState();

        Log.Info($"[PlayerBar] Initialized: Vol={Volume}, MaxVol={MaxVolume}, AutoShuffle={AutoShuffleEnabled}, Repeat={RepeatMode}");
    }

    private void SetupCommands()
    {
        var hasTrackObs = this.WhenAnyValue(x => x.HasTrack);
        var canShuffle = this.WhenAnyValue(x => x.HasQueueToShuffle);

        PlayPauseCommand = CreateCommand(ReactiveCommand.CreateFromTask(
            _playerControl.PlayPauseAsync, hasTrackObs));

        NextCommand = CreateCommand(ReactiveCommand.Create(() =>
        {
            IsNavigating = true;
            _nextSubject.OnNext(Unit.Default);
        }, hasTrackObs));

        PreviousCommand = CreateCommand(ReactiveCommand.Create(() =>
        {
            IsNavigating = true;
            _prevSubject.OnNext(Unit.Default);
        }, hasTrackObs));

        ShuffleQueueCommand = CreateCommand(ReactiveCommand.Create(() =>
        {
            _playerControl.ShuffleQueue();
            IsShuffleAnimating = true;
            Observable.Timer(TimeSpan.FromMilliseconds(ShuffleAnimationDurationMs))
                .ObserveOn(RxSchedulers.MainThreadScheduler)
                .Subscribe(_ => IsShuffleAnimating = false)
                .DisposeWith(Disposables);
        }, canShuffle));

        ToggleAutoShuffleCommand = CreateCommand(ReactiveCommand.Create(() =>
        {
            _playerControl.ToggleAutoShuffle();
            this.RaisePropertyChanged(nameof(ShuffleTooltip));
        }));

        ToggleRepeatCommand = CreateCommand(ReactiveCommand.Create(() =>
        {
            _playerControl.ToggleRepeat();
            ShowHint(
                v => IsRepeatHintVisible = v,
                () => RepeatHintText = RepeatMode switch
                {
                    RepeatMode.None => SL["Player_Repeat_Off"],
                    RepeatMode.All => SL["Player_Repeat_All"],
                    RepeatMode.One => SL["Player_Repeat_One"],
                    _ => ""
                });
        }));

        ToggleMuteCommand = CreateCommand(ReactiveCommand.Create(() =>
        {
            if (IsMuted)
            {
                int restoreVolume = Math.Min(
                    _lastVolumeBeforeMute > 0 ? _lastVolumeBeforeMute : DefaultVolume,
                    MaxVolume);
                Volume = restoreVolume;
            }
            else
            {
                _lastVolumeBeforeMute = Volume;
                _library.UpdateSettings(s => s.LastVolume = Volume);
                Volume = 0;
            }
            OnVolumeChangeComplete();
        }));

        ToggleLikeCommand = CreateCommand(ReactiveCommand.CreateFromTask(async () =>
        {
            if (CurrentTrack != null)
            {
                await _musicManager.ToggleLikeAsync(CurrentTrack);
                ShowHint(
                    v => IsLikeHintVisible = v,
                    () => LikeHintText = IsLiked ? SL["Track_Added"] : SL["Track_Removed"]);
            }
        }, hasTrackObs));

        LoadFormatsCommand = CreateCommand(ReactiveCommand.CreateFromTask(() => LoadFormatsAsync(forceRefresh: false)));
        ForceLoadFormatsCommand = CreateCommand(ReactiveCommand.CreateFromTask(() => LoadFormatsAsync(forceRefresh: true)));

        SwitchFormatCommand = CreateCommand(ReactiveCommand.CreateFromTask<StreamOption>(async option =>
        {
            if (option == null) return;
            BeginTrackReset();
            await _audio.SwitchQualityAsync(option.Format, (int)option.Bitrate);
        }));
    }

    private void SubscribeLightweight()
    {
        _playerControl.PlaybackStateObservable
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(state =>
            {
                IsPlaying = state.IsPlaying;
                IsPaused = state.IsPaused;
                this.RaisePropertyChanged(nameof(PlayPauseTooltip));

                if (state.IsPlaying && IsTrackResetting)
                {
                    EndTrackReset();
                }
            })
            .DisposeWith(Disposables);

        _playerControl.CurrentTrackObservable
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(HandleTrackChanged)
            .DisposeWith(Disposables);

        _playerControl.RepeatModeObservable
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(mode =>
            {
                RepeatMode = mode;
                this.RaisePropertyChanged(nameof(RepeatTooltip));
                this.RaisePropertyChanged(nameof(IsRepeatNone));
                this.RaisePropertyChanged(nameof(IsRepeatOne));
                this.RaisePropertyChanged(nameof(IsRepeatAll));
            })
            .DisposeWith(Disposables);

        _playerControl.ShuffleEnabledObservable
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(enabled =>
            {
                AutoShuffleEnabled = enabled;
                this.RaisePropertyChanged(nameof(ShuffleTooltip));
            })
            .DisposeWith(Disposables);

        _playerControl.IsLoadingObservable
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(loading =>
            {
                IsLoading = loading;
            })
            .DisposeWith(Disposables);

        _playerControl.QueueCountObservable
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(_ => UpdateQueueState())
            .DisposeWith(Disposables);

        _playerControl.ForceSyncObservable
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(_ => HandleForceSync())
            .DisposeWith(Disposables);

        // Реактивная синхронизация: получение изменений громкости от координатора (трея, горячих клавиш)
        _playerControl.VolumeObservable
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(v =>
            {
                if (Volume != v)
                {
                    Volume = v;
                }
            })
            .DisposeWith(Disposables);

        // Передача изменений громкости от слайдера плеербара к координатору
        this.WhenAnyValue(x => x.Volume)
            .Subscribe(v =>
            {
                if (_playerControl.CurrentVolume != v)
                {
                    _playerControl.SetVolumeFast(v);
                }

                RecalcEffectivePercent();
                RaiseVolumePropertiesChanged();
            })
            .DisposeWith(Disposables);

        Observable.FromEvent<Action<AudioStreamInfo>, AudioStreamInfo>(
                h => _audio.OnStreamInfoChanged += h,
                h => _audio.OnStreamInfoChanged -= h)
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(UpdateStreamInfo)
            .DisposeWith(Disposables);

        Observable.FromEvent<Action<int>, int>(
                h => _audio.OnMaxVolumeChanged += h,
                h => _audio.OnMaxVolumeChanged -= h)
            .DistinctUntilChanged()
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(HandleMaxVolumeChanged)
            .DisposeWith(Disposables);

        Observable.FromEvent<Action<TrackInfo>, TrackInfo>(
                h => _library.OnTrackUpdated += h,
                h => _library.OnTrackUpdated -= h)
            .Where(t => CurrentTrack != null && t.Id == CurrentTrack.Id)
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(t =>
            {
                IsLiked = t.IsLiked;
                CurrentTrack?.IsLiked = t.IsLiked;
                this.RaisePropertyChanged(nameof(LikeTooltip));
            })
            .DisposeWith(Disposables);

        var cacheManager = AudioSourceFactory.GlobalCache
            ?? throw new NullReferenceException("AudioSourceFactory.GlobalCache is not initialized");

        Observable.FromEvent<Action<string, AudioFormat, int, bool>, (string TrackId, AudioFormat Format, int Bitrate, bool Downloaded)>(
                h => (t, f, b, d) => h((t, f, b, d)),
                h => cacheManager.OnFormatCached += h,
                h => cacheManager.OnFormatCached -= h)
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(x => OnFormatCached(x.TrackId, x.Format, x.Bitrate, x.Downloaded))
            .DisposeWith(Disposables);

        Observable.FromEvent(
                h => cacheManager.OnCacheCleared += h,
                h => cacheManager.OnCacheCleared -= h)
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(_ => SyncBufferState())
            .DisposeWith(Disposables);

        this.WhenAnyValue(x => x.CurrentTrackIndex, x => x.TotalTracksInQueue)
            .Subscribe(_ => this.RaisePropertyChanged(nameof(TrackNumberTooltip)))
            .DisposeWith(Disposables);

        _nextSubject
            .Throttle(TimeSpan.FromMilliseconds(NavigationDebounceMs))
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(async _ =>
            {
                try { await _playerControl.NextAsync(); }
                finally { IsNavigating = false; }
            })
            .DisposeWith(Disposables);

        _prevSubject
            .Throttle(TimeSpan.FromMilliseconds(NavigationDebounceMs))
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(async _ =>
            {
                try { await _playerControl.PreviousAsync(); }
                finally { IsNavigating = false; }
            })
            .DisposeWith(Disposables);
    }

    private void SubscribeHeavy()
    {
        _heavySubscriptions?.Dispose();
        _heavySubscriptions = [];

        Observable.FromEvent<Action<TimeSpan>, TimeSpan>(
                h => _audio.OnPositionChanged += h,
                h => _audio.OnPositionChanged -= h)
            .Where(_ => !IsTrackResetting)
            .Throttle(TimeSpan.FromMilliseconds(PositionUpdateThrottleMs))
            .DistinctUntilChanged(pos => (long)pos.TotalSeconds)
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(pos =>
            {
                if (IsTrackResetting) return;
                Position = pos;
                PositionSeconds = pos.TotalSeconds;
                this.RaisePropertyChanged(nameof(DurationTooltip));
            })
            .DisposeWith(_heavySubscriptions);

        Observable.FromEvent<Action<TimeSpan>, TimeSpan>(
                h => _audio.OnSeekCompleted += h,
                h => _audio.OnSeekCompleted -= h)
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(pos =>
            {
                PositionSeconds = pos.TotalSeconds;
                Position = pos;
                SyncBufferState();

                // Детерминированный сброс состояния занятости по факту физического завершения
                IsSeekBusy = false;
            })
            .DisposeWith(_heavySubscriptions);

        Observable.FromEvent<Action<BufferState>, BufferState>(
                h => _audio.OnBufferStateChanged += h,
                h => _audio.OnBufferStateChanged -= h)
            .Throttle(TimeSpan.FromMilliseconds(BufferStateThrottleMs))
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(state => SyncBufferState(state))
            .DisposeWith(_heavySubscriptions);

        Observable.Interval(TimeSpan.FromMilliseconds(FallbackPositionIntervalMs))
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(_ => FallbackPositionUpdate())
            .DisposeWith(_heavySubscriptions);
    }

    #endregion

    #region Unified Hint System

    private async void ShowHint(Action<bool> setVisible, Action setText, int durationMs = HintDisplayDurationMs)
    {
        _activeHintCts?.Cancel();
        _activeHintCts?.Dispose();
        var cts = new CancellationTokenSource();
        _activeHintCts = cts;

        try
        {
            setText();
            setVisible(true);
            await Task.Delay(durationMs, cts.Token);
            setVisible(false);
        }
        catch (OperationCanceledException) { }
    }

    #endregion

    #region Buffer Progress

    private void SyncBufferState(BufferState? externalState = null)
    {
        if (!HasTrack || CurrentTrack == null || IsTrackResetting)
            return;

        bool isPlayingLocalFile = _audio.StreamInfo.IsValid && _audio.StreamInfo.IsFromCache;

        if (isPlayingLocalFile)
        {
            SetFullyBuffered();
            UpdateNetworkStats(0, 0); // Отключаем стату сети только если АКТИВНЫЙ поток действительно локальный
            return;
        }

        double currentRatio = DurationSeconds > 0 ? PositionSeconds / DurationSeconds : 0.0;

        IReadOnlyList<(double Start, double End)> rawRanges;
        if (externalState.HasValue)
        {
            var state = externalState.Value;
            BufferProgressPercent = state.Progress;
            IsFullyBuffered = state.IsFullyBuffered;
            rawRanges = state.Ranges;

            // Обновляем UI телеметрию из движка
            UpdateNetworkStats(state.SpeedBytesPerSec, state.AveragePingMs);
        }
        else
        {
            BufferProgressPercent = _audio.BufferProgress;
            IsFullyBuffered = _audio.IsFullyBuffered;
            rawRanges = _audio.GetBufferedRanges();
        }

        if (IsFullyBuffered)
        {
            SetFullyBuffered();
            UpdateNetworkStats(0, 0);
            return;
        }

        bool isPcmDataGuaranteed = !IsLoading && !IsSeekBusy;

        if (isPcmDataGuaranteed && rawRanges.Count > 0 && currentRatio > 0.0)
        {
            var adjustedRanges = new List<(double Start, double End)>(rawRanges.Count);

            int closestIndex = -1;
            double minDistance = double.MaxValue;

            for (int i = 0; i < rawRanges.Count; i++)
            {
                var r = rawRanges[i];
                double dist = 0;
                if (currentRatio < r.Start) dist = r.Start - currentRatio;
                else if (currentRatio > r.End) dist = currentRatio - r.End;

                if (dist < minDistance)
                {
                    minDistance = dist;
                    closestIndex = i;
                }
            }

            for (int i = 0; i < rawRanges.Count; i++)
            {
                double start = rawRanges[i].Start;
                double end = rawRanges[i].End;

                if (i == closestIndex && minDistance < 0.15)
                {
                    if (start > currentRatio) start = Math.Max(0.0, currentRatio - 0.005);
                    if (end < currentRatio) end = Math.Min(1.0, currentRatio + 0.005);
                }

                adjustedRanges.Add((start, end));
            }
            BufferedRanges = adjustedRanges;
        }
        else
        {
            BufferedRanges = rawRanges;
        }

        this.RaisePropertyChanged(nameof(UseSegmentedBuffer));
    }

    private void UpdateNetworkStats(double speedBytesPerSec, double pingMs)
    {
        if (IsFullyBuffered || (speedBytesPerSec <= 0 && pingMs <= 0))
        {
            ShowNetworkStats = false;
            return;
        }

        ShowNetworkStats = true;

        // Локализованное форматирование скорости
        if (speedBytesPerSec > 1024 * 1024)
        {
            double speedMb = speedBytesPerSec / (1024.0 * 1024.0);
            NetworkSpeedText = string.Format(LocalizationService.Instance.Get("Stream_Speed_Mb", "{0:F1} MB/s"), speedMb);
        }
        else if (speedBytesPerSec > 0)
        {
            double speedKb = speedBytesPerSec / 1024.0;
            NetworkSpeedText = string.Format(LocalizationService.Instance.Get("Stream_Speed_Kb", "{0:F0} KB/s"), speedKb);
        }
        else
        {
            NetworkSpeedText = string.Format(LocalizationService.Instance.Get("Stream_Speed_Kb", "0 KB/s"), 0);
        }

        // Локализованный пинг
        PingText = string.Format(LocalizationService.Instance.Get("Stream_Ping_Ms", "{0:F0} ms"), pingMs);

        // Динамическая адаптивная стилизация задержки сети
        var app = Application.Current;
        if (pingMs < 200)
        {
            // Стабильный пинг: обычный цвет как у формата (TextMutedBrush), начертание по умолчанию
            if (app?.Resources.TryGetResource("TextMutedBrush", app.ActualThemeVariant, out var b) == true && b is IBrush brush)
                PingBrush = brush;
            else
                PingBrush = Brushes.Gray;

            PingWeight = FontWeight.SemiBold;
        }
        else if (pingMs < 500)
        {
            // Менее стабильный: акцентный цвет плеера, начертание по умолчанию
            if (app?.Resources.TryGetResource("AccentBrush", app.ActualThemeVariant, out var b) == true && b is IBrush brush)
                PingBrush = brush;
            else
                PingBrush = Brushes.DodgerBlue;

            PingWeight = FontWeight.SemiBold;
        }
        else
        {
            // Критическая задержка: системный красный цвет и сверхжирное начертание (Heavy)
            if (app?.Resources.TryGetResource("AccentBrush", app.ActualThemeVariant, out var b) == true && b is IBrush brush)
                PingBrush = brush;
            else
                PingBrush = Brushes.Red;

            PingWeight = FontWeight.Heavy;
        }
    }

    private void SetFullyBuffered()
    {
        if (IsFullyBuffered && BufferProgressPercent >= 100) return;

        BufferProgressPercent = 100;
        BufferedRanges = [(0.0, 1.0)];
        IsFullyBuffered = true;
        this.RaisePropertyChanged(nameof(UseSegmentedBuffer));
    }

    private void ResetBufferState()
    {
        BufferProgressPercent = 0;
        BufferedRanges = [];
        IsFullyBuffered = false;
        this.RaisePropertyChanged(nameof(UseSegmentedBuffer));
    }

    #endregion

    #region Track Reset Visual

    private void BeginTrackReset()
    {
        _trackResetStartTime = DateTime.UtcNow;
        IsTrackResetting = true;
        Position = TimeSpan.Zero;
        PositionSeconds = 0;
        ResetBufferState();
    }

    private void EndTrackReset()
    {
        if (!IsTrackResetting) return;
        IsTrackResetting = false;
    }

    #endregion

    #region Private Handlers

    private void HandleMaxVolumeChanged(int newMax)
    {
        if (MaxVolume == newMax) return;

        int oldMax = MaxVolume;
        MaxVolume = newMax;

        if (Volume > MaxVolume)
            Volume = MaxVolume;

        if (_lastVolumeBeforeMute > MaxVolume)
            _lastVolumeBeforeMute = MaxVolume;

        RecalcEffectivePercent();
        RaiseVolumePropertiesChanged();
        Log.Info($"[PlayerBar] MaxVolume changed: {oldMax} -> {newMax}");
    }

    private void HandleTrackChanged(TrackInfo? track)
    {
        string? newTrackId = track?.Id;
        bool isNewTrack = newTrackId != _lastHandledTrackId;
        _lastHandledTrackId = newTrackId;

        CurrentTrack = track;
        HasTrack = track != null;

        RaiseTrackInfoChanged();

        if (track != null)
        {
            if (isNewTrack)
            {
                _lastValidStreamInfo = "";
                AvailableFormats.Clear();
                _pendingStreamInfoTrackId = track.Id;

                CancelFormatsLoading();
                BeginTrackReset();

                Duration = track.Duration;
                DurationSeconds = Duration.TotalSeconds > 0 ? Duration.TotalSeconds : 1;

                ShowStreamInfo = true;
                StreamInfo = SL["Player_StreamInfo_Loading"];

                Log.Debug($"[PlayerBar] New track: {track.Id}");
            }

            var storedTrack = _library.GetTrack(track.Id);
            IsLiked = storedTrack?.IsLiked ?? track.IsLiked;
        }
        else
        {
            ResetToNoTrack();
        }

        this.RaisePropertyChanged(nameof(DurationTooltip));
        UpdateQueueState();
    }

    private void ResetToNoTrack()
    {
        CancelFormatsLoading();
        _pendingStreamInfoTrackId = null;
        _lastValidStreamInfo = "";
        AvailableFormats.Clear();
        Duration = TimeSpan.Zero;
        DurationSeconds = 1;
        ShowStreamInfo = false;
        StreamInfo = "";
        ShowNetworkStats = false;
        IsLiked = false;
        IsTrackResetting = false;
        Position = TimeSpan.Zero;
        PositionSeconds = 0;
        ResetBufferState();
    }

    /// <summary>
    /// Принудительно синхронизирует состояние отображения панели с низкоуровневым движком.
    /// </summary>
    private void HandleForceSync()
    {
        if (!HasTrack || CurrentTrack == null)
            return;

        if (IsTrackResetting)
        {
            IsTrackResetting = false;
        }

        if (IsSeekBusy)
        {
            IsSeekBusy = false;
        }

        SyncPositionFromEngine();

        var dur = _audio.TotalDuration;
        if (dur.TotalSeconds > 0)
        {
            Duration = dur;
            DurationSeconds = dur.TotalSeconds;
        }

        SyncBufferState();

        if (!string.IsNullOrEmpty(_lastValidStreamInfo))
        {
            StreamInfo = _lastValidStreamInfo;
            ShowStreamInfo = true;
        }

        var storedTrack = _library.GetTrack(CurrentTrack.Id);
        if (storedTrack != null)
        {
            IsLiked = storedTrack.IsLiked;
            CurrentTrack.IsLiked = storedTrack.IsLiked;
        }

        NetworkSpeedText = "";
        PingText = "";
        ShowNetworkStats = false;

        RaiseTrackInfoChanged();
        this.RaisePropertyChanged(nameof(DurationTooltip));
        this.RaisePropertyChanged(nameof(LikeTooltip));
        this.RaisePropertyChanged(nameof(PlayPauseTooltip));

        UpdateQueueState();
    }

    private void UpdateQueueState()
    {
        var queue = _audio.Queue;
        TotalTracksInQueue = queue.Count;
        HasQueueToShuffle = queue.Count > 1;

        if (CurrentTrack != null)
        {
            CurrentTrackIndex = 0;
            for (int i = 0; i < queue.Count; i++)
            {
                if (queue[i].Id == CurrentTrack.Id)
                {
                    CurrentTrackIndex = i;
                    break;
                }
            }
        }
        else
        {
            CurrentTrackIndex = 0;
        }

        this.RaisePropertyChanged(nameof(CurrentTrackIndexDisplay));
        this.RaisePropertyChanged(nameof(TrackNumberTooltip));
    }

    /// <summary>
    /// Обновляет текстовую плашку информации о потоке и синхронизирует состояние кнопок качества.
    /// </summary>
    private void UpdateStreamInfo(AudioStreamInfo info)
    {
        Log.Debug($"[PlayerBar] StreamInfo UI update: track={CurrentTrack?.Id}, valid={info.IsValid}, " +
                  $"display='{info.FormatDisplay}', duration={info.DurationMs}ms");

        if (CurrentTrack == null)
        {
            ShowStreamInfo = false;
            StreamInfo = "";
            return;
        }

        if (info.IsValid)
        {
            _lastValidStreamInfo = info.FormatDisplay;
            StreamInfo = info.FormatDisplay;

            Duration = TimeSpan.FromMilliseconds(info.DurationMs);
            DurationSeconds = Duration.TotalSeconds > 0 ? Duration.TotalSeconds : 1;
            this.RaisePropertyChanged(nameof(DurationTooltip));

            UpdateActiveFormat(info.Format, info.Bitrate);

            SyncFormatDownloadStatus();

            if (IsTrackResetting)
            {
                bool isForCurrentTrack =
                    (!string.IsNullOrEmpty(info.TrackId) && CurrentTrack.Id == info.TrackId)
                    || (string.IsNullOrEmpty(info.TrackId)
                        && _pendingStreamInfoTrackId == CurrentTrack.Id);

                if (isForCurrentTrack)
                {
                    _pendingStreamInfoTrackId = null;
                    EndTrackReset();
                }
            }

            SyncBufferState();
        }
        else
        {
            StreamInfo = SL["Player_StreamInfo_Loading"];
        }

        ShowStreamInfo = true;
    }

    private void FallbackPositionUpdate()
    {
        if (!HasTrack || IsTrackResetting)
        {
            if (IsTrackResetting && HasTrack)
            {
                var elapsed = DateTime.UtcNow - _trackResetStartTime;
                bool audioIsPlaying = IsPlaying || _audio.CurrentPosition.TotalSeconds > AudioIsPlayingThresholdSec;

                if (audioIsPlaying && elapsed > TimeSpan.FromSeconds(StaleResetTimeoutSec))
                {
                    Log.Warn($"[PlayerBar] TrackReset stuck for {elapsed.TotalSeconds:F1}s while audio is playing — force clearing");
                    IsTrackResetting = false;
                    SyncPositionFromEngine();
                    SyncBufferState();

                    if (!string.IsNullOrEmpty(_lastValidStreamInfo))
                    {
                        StreamInfo = _lastValidStreamInfo;
                        ShowStreamInfo = true;
                    }
                }
            }
            return;
        }

        if (IsSeekBusy)
        {
            return;
        }

        if (_isSeeking) return;

        if (IsPlaying)
        {
            var pos = _audio.CurrentPosition;

            if ((long)pos.TotalSeconds != (long)Position.TotalSeconds)
            {
                Position = pos;
                PositionSeconds = pos.TotalSeconds;
            }
        }
    }

    #endregion

    #region Public Interaction

    public void StartSeek()
    {
        _isSeeking = true;
    }

    public void UpdateSeekPosition(double seconds)
    {
        if (!_isSeeking) return;

        seconds = Math.Clamp(seconds, 0, DurationSeconds);
        PositionSeconds = seconds;
        Position = TimeSpan.FromSeconds(seconds);
        this.RaisePropertyChanged(nameof(DurationTooltip));
    }

    /// <summary>
    /// Инициирует перемотку. Состояние IsSeekBusy сбрасывается по событию OnSeekCompleted.
    /// </summary>
    public async void EndSeek()
    {
        if (!HasTrack)
        {
            _isSeeking = false;
            IsSeekBusy = false;
            return;
        }

        double target = PositionSeconds;
        _isSeeking = false;

        PositionSeconds = target;
        Position = TimeSpan.FromSeconds(target);
        this.RaisePropertyChanged(nameof(DurationTooltip));

        IsSeekBusy = true;

        try
        {
            await _audio.SeekAsync(TimeSpan.FromSeconds(target));
        }
        catch (Exception ex)
        {
            Log.Warn($"[PlayerBar] Seek failed: {ex.Message}");
            IsSeekBusy = false;
            SyncPositionFromEngine();
        }
    }

    public double ReadCurrentPositionSeconds() => _audio.CurrentPosition.TotalSeconds;

    public void CancelSeek()
    {
        _isSeeking = false;
        IsSeekBusy = false;
        SyncPositionFromEngine();
    }

    /// <summary>
    /// Фиксирует измененную громкость в конфигурационном файле приложения.
    /// </summary>
    public void OnVolumeChangeComplete()
    {
        _playerControl.CommitVolume();
    }

    public int GetVolumeScrollStep()
    {
        if (MaxVolume <= DefaultMaxVolume) return 1;
        return Math.Max(1, MaxVolume / VolumeScrollStepDivisor);
    }

    public void RequestResumeIfSuspended()
    {
        if (IsSuspended)
        {
            Log.Info($"[PlayerBar] Requesting resume (level={CurrentSuspendLevel})");
            _playerControl.RequestResume();
        }
    }

    #endregion

    #region Private Helpers

    /// <summary>
    /// Устанавливает <see cref="StreamOption.IsActive"/> ровно у одного формата —
    /// ближайшего по битрейту среди совпадающих по контейнеру.
    /// Решает проблему когда NormalizeBitrate(70) == NormalizeBitrate(55) == 64
    /// и оба формата ложно получают IsActive = true.
    /// </summary>
    private void UpdateActiveFormat(AudioFormat format, int infoBitrate)
    {
        // Сброс всех флагов
        foreach (var f in AvailableFormats)
            f.IsActive = false;

        if (AvailableFormats.Count == 0) return;

        // Ищем единственный ближайший по raw-битрейту формат того же контейнера
        StreamOption? bestMatch = null;
        int bestDelta = int.MaxValue;

        foreach (var f in AvailableFormats)
        {
            if (f.Format != format) continue;
            int delta = Math.Abs((int)f.Bitrate - infoBitrate);
            if (delta < bestDelta)
            {
                bestDelta = delta;
                bestMatch = f;
            }
        }

        bestMatch?.IsActive = true;
    }

    private void SyncPositionFromEngine()
    {
        var realPos = _audio.CurrentPosition;
        Position = realPos;
        PositionSeconds = realPos.TotalSeconds;
    }

    private void RaiseVolumePropertiesChanged()
    {
        this.RaisePropertyChanged(nameof(IsMuted));
        this.RaisePropertyChanged(nameof(IsVolumeLow));
        this.RaisePropertyChanged(nameof(IsVolumeMedium));
        this.RaisePropertyChanged(nameof(IsVolumeHigh));
        this.RaisePropertyChanged(nameof(IsVolumeBoosted));
        this.RaisePropertyChanged(nameof(IsReallyBoosted));
        this.RaisePropertyChanged(nameof(VolumePercentBrush));
        this.RaisePropertyChanged(nameof(MuteTooltip));
    }

    private void RaiseTrackInfoChanged()
    {
        this.RaisePropertyChanged(nameof(SafeTitle));
        this.RaisePropertyChanged(nameof(SafeAuthor));
        this.RaisePropertyChanged(nameof(SafeThumbnail));
        this.RaisePropertyChanged(nameof(PlayPauseTooltip));
        this.RaisePropertyChanged(nameof(CurrentTrackUrl));
    }

    private static string FormatTime(TimeSpan time) =>
        time.TotalHours >= 1
            ? time.ToString(@"h\:mm\:ss")
            : time.ToString(@"m\:ss");

    #endregion

    #region Format Loading

    /// <summary>
    /// Синхронизирует <see cref="StreamOption.IsDownloaded"/> для каждого элемента
    /// <see cref="AvailableFormats"/> на основе текущего состояния кэша и явного скачивания.
    /// Использует аддитивную логику: только устанавливает <c>true</c>, никогда не сбрасывает.
    /// Полный сброс происходит только при пересоздании списка в <see cref="LoadFormatsAsync"/>.
    /// </summary>
    private void SyncFormatDownloadStatus()
    {
        if (CurrentTrack == null || AvailableFormats.Count == 0)
            return;

        var cache = AudioSourceFactory.GlobalCache;
        var cachedFormats = cache?.GetCachedFormats(CurrentTrack.Id) ?? [];

        Log.Debug($"[PlayerBar] SyncDownloadStatus: track={CurrentTrack.Id}, " +
                  $"cachedFormats=[{string.Join(", ", cachedFormats.Select(f => $"{f.Format}/{f.Bitrate}"))}], " +
                  $"isDownloaded={CurrentTrack.IsDownloaded}, localPath={CurrentTrack.LocalPath ?? "null"}, " +
                  $"preferredBitrate={CurrentTrack.PreferredBitrate}, " +
                  $"formats=[{string.Join(", ", AvailableFormats.Select(f => $"{f.Format}/{f.Bitrate:F0}"))}]");

        // Phase 1: Hidden cache — epsilon-based bitrate matching
        foreach (var f in AvailableFormats)
        {
            if (f.IsDownloaded) continue;

            foreach (var (cachedFormat, cachedBitrate) in cachedFormats)
            {
                if (f.Format == cachedFormat
                    && Math.Abs((int)f.Bitrate - cachedBitrate) <= BitrateMatchEpsilonKbps)
                {
                    f.IsDownloaded = true;
                    break;
                }
            }
        }

        // Phase 2: Explicit download — closest match по контейнеру
        bool isExplicitDownload = CurrentTrack.IsDownloaded
            && !string.IsNullOrEmpty(CurrentTrack.LocalPath)
            && File.Exists(CurrentTrack.LocalPath);

        if (isExplicitDownload)
        {
            AudioFormat explicitFormat = AudioSourceFactory.DetectFormat(CurrentTrack.LocalPath!);
            if (explicitFormat == AudioFormat.Unknown)
                explicitFormat = AudioFormat.WebM;

            int referenceBitrate = EstimateRawBitrateFromFile(CurrentTrack);

            StreamOption? bestMatch = null;
            int bestDelta = int.MaxValue;

            foreach (var f in AvailableFormats)
            {
                if (f.Format != explicitFormat) continue;
                int delta = Math.Abs((int)f.Bitrate - referenceBitrate);
                if (delta < bestDelta)
                {
                    bestDelta = delta;
                    bestMatch = f;
                }
            }

            bestMatch?.IsDownloaded = true;
        }
    }

    /// <summary>
    /// Оценивает сырой (ненормализованный) битрейт скачанного файла.
    /// Используется для fuzzy-matching при неизвестном <c>PreferredBitrate</c>.
    /// </summary>
    private static int EstimateRawBitrateFromFile(TrackInfo track)
    {
        try
        {
            if (string.IsNullOrEmpty(track.LocalPath) || track.Duration.TotalSeconds <= 0)
                return 128;

            long fileSize = new FileInfo(track.LocalPath).Length;
            return Math.Max((int)(fileSize * 8 / track.Duration.TotalSeconds / 1000), 32);
        }
        catch
        {
            return 128;
        }
    }

    private async Task LoadFormatsAsync(bool forceRefresh = false)
    {
        if (CurrentTrack == null) return;

        string videoId = CurrentTrack.Id.Replace("yt_", "");

        if (!forceRefresh && _restrictedTracks.TryGetValue(CurrentTrack.Id, out var cachedReason))
        {
            await HandleMissingFormatsNotificationAsync(
                new LoginRequiredException(
                    "Cached authorization restriction",
                    videoId,
                    cachedReason),
                "Cached authorization requirement");
            AvailableFormats.Clear();
            return;
        }

        CancelFormatsLoading();
        _formatsCts = new CancellationTokenSource();
        var token = _formatsCts.Token;

        IsFormatsLoading = true;
        List<StreamOption> formats = [];
        bool hasError = false;
        string? errorMessage = null;
        Exception? caughtException = null;

        try
        {
            formats = await _youtube.GetStreamOptionsAsync(videoId, token, forceRefresh);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (YoutubeNetworkException ex)
        {
            // Сетевые ошибки НЕ кэшируются — трек может быть доступен при
            // восстановлении соединения
            Log.Warn($"LoadFormatsAsync network error: {ex.ErrorType} — {ex.Message}");
            caughtException = ex;
            hasError = true;
            errorMessage = ex.Message;
            formats = [];
        }
        catch (LoginRequiredException ex)
        {
            Log.Error($"LoadFormatsAsync login required: {ex.Reason} — {ex.Message}");
            caughtException = ex;
            hasError = true;
            errorMessage = ex.Message;

            // Кэшируем ТОЛЬКО контентные ограничения, НЕ бот-детекцию (она транзиентна)
            if (ex.Reason != LoginRequiredReason.BotDetection)
            {
                _restrictedTracks[CurrentTrack.Id] = ex.Reason;
            }

            formats = [];
        }
        catch (Exception ex)
        {
            Log.Error($"LoadFormatsAsync error: {ex.Message}");
            caughtException = ex;
            hasError = true;
            errorMessage = ex.Message;

            if (!_youtube.AuthService.IsAuthenticated &&
                (ex.Message.Contains("LoginRequired", StringComparison.OrdinalIgnoreCase) ||
                 ex.Message.Contains("403")))
            {
                _restrictedTracks[CurrentTrack.Id] = LoginRequiredReason.Unknown;
            }

            formats = [];
        }
        finally
        {
            if (!token.IsCancellationRequested)
            {
                IsFormatsLoading = false;
            }
        }

        if (token.IsCancellationRequested) return;

        var (currentFormat, currentBitrate, _) = _audio.GetCurrentStreamInfo();

        AvailableFormats.Clear();

        foreach (var f in formats)
        {
            AvailableFormats.Add(f);
        }

        // Единый источник правды для IsActive
        UpdateActiveFormat(currentFormat, currentBitrate);

        // Аддитивно устанавливает IsDownloaded=true для кэшированных
        SyncFormatDownloadStatus();

        // Проверка наличия физических форматов (без LINQ)
        bool hasValidPhysicalFormats = false;
        foreach (var f in formats)
        {
            if (f.SizeMb > 0 && f.Format != AudioFormat.Hls)
            {
                hasValidPhysicalFormats = true;
                break;
            }
        }

        if (hasError || !hasValidPhysicalFormats)
        {
            if (!_youtube.AuthService.IsAuthenticated && caughtException is not YoutubeNetworkException)
            {
                _restrictedTracks.TryAdd(CurrentTrack.Id, LoginRequiredReason.Unknown);
            }
            await HandleMissingFormatsNotificationAsync(caughtException, errorMessage);
        }

        Log.Debug($"Loaded {AvailableFormats.Count} formats");
    }

    private async Task HandleMissingFormatsNotificationAsync(Exception? exception, string? errorMessage)
    {
        const string titleKey = "Error_StreamUnavailable_Title";

        string messageKey = exception switch
        {
            YoutubeNetworkException netEx => netEx.GetLocalizationKey(),
            LoginRequiredException lre => lre.GetLocalizationKey(),
            BotDetectionException => "Error_Login_BotDetection",
            VideoUnplayableException => "Error_Video_Unavailable",
            _ => "Error_Stream_Generic"
        };

        string? recommendationKey = exception switch
        {
            YoutubeNetworkException netEx => netEx.GetRecommendationKey(),
            LoginRequiredException lre => lre.Reason switch
            {
                LoginRequiredReason.AgeRestricted => "Recommendation_Login_AgeRestricted",
                LoginRequiredReason.Private => "Recommendation_Private",
                LoginRequiredReason.MembersOnly => "Recommendation_MembersOnly",
                LoginRequiredReason.BotDetection => "Recommendation_BotDetection",
                _ => "Recommendation_Login"
            },
            BotDetectionException => "Recommendation_BotDetection",
            VideoUnplayableException => "Recommendation_VideoUnavailable",
            _ => null
        };

        if (recommendationKey == null)
        {
            if (errorMessage != null &&
                (errorMessage.Contains("AgeRestricted", StringComparison.OrdinalIgnoreCase) ||
                 errorMessage.Contains("Age restricted", StringComparison.OrdinalIgnoreCase)))
            {
                messageKey = "Error_Login_AgeRestricted";
                recommendationKey = "Recommendation_Login_AgeRestricted";
            }
            else if (!_youtube.AuthService.IsAuthenticated)
            {
                messageKey = "Error_Login_Required";
                recommendationKey = "Recommendation_Login";
            }
            else
            {
                recommendationKey = "Recommendation_DpiBlocked";
            }
        }

        try
        {
            await _notificationService.ShowToastAsync(
                titleKey: titleKey,
                messageKey: messageKey,
                severity: NotificationSeverity.Warning,
                trackId: CurrentTrack?.Id,
                trackTitle: CurrentTrack?.Title,
                exceptionDetails: exception?.ToString() ?? errorMessage,
                recommendationKey: recommendationKey);
        }
        catch (Exception ex)
        {
            Log.Error($"[PlayerBar] Failed to show formats notification: {ex.Message}");
        }
    }

    private void CancelFormatsLoading()
    {
        _formatsCts?.Cancel();
        _formatsCts?.Dispose();
        _formatsCts = null;
        IsFormatsLoading = false;
    }

    private void ClearRestrictedTracksCache()
    {
        _restrictedTracks.Clear();
        Log.Debug("[PlayerBar] Restricted tracks cache cleared due to AuthState change.");
    }

    private void OnFormatCached(string trackId, AudioFormat format, int bitrate, bool isDownloaded)
    {
        if (CurrentTrack == null || CurrentTrack.Id != trackId) return;

        if (!isDownloaded) return;

        bool found = false;

        foreach (var streamFormat in AvailableFormats)
        {
            if (streamFormat.Format == format
                && Math.Abs((int)streamFormat.Bitrate - bitrate) <= BitrateMatchEpsilonKbps)
            {
                streamFormat.IsDownloaded = true;
                found = true;
                break;
            }
        }

        if (!found && AvailableFormats.Count > 0)
            SyncFormatDownloadStatus();
    }

    #endregion

    #region Language

    private void OnLanguageChanged(object? sender, string newLang)
    {
        RaiseTrackInfoChanged();
        RaiseVolumePropertiesChanged();

        this.RaisePropertyChanged(nameof(ShuffleTooltip));
        this.RaisePropertyChanged(nameof(PreviousTooltip));
        this.RaisePropertyChanged(nameof(NextTooltip));
        this.RaisePropertyChanged(nameof(RepeatTooltip));
        this.RaisePropertyChanged(nameof(LikeTooltip));
        this.RaisePropertyChanged(nameof(TrackNumberTooltip));
        this.RaisePropertyChanged(nameof(DurationTooltip));
        this.RaisePropertyChanged(nameof(L));
        this.RaisePropertyChanged(nameof(IsRepeatNone));
        this.RaisePropertyChanged(nameof(IsRepeatOne));
        this.RaisePropertyChanged(nameof(IsRepeatAll));
    }

    #endregion

    #region LifeCycle

    /// <summary>
    /// Вызывается при сворачивании или приостановке работы интерфейса.
    /// </summary>
    protected override void OnSuspend(SuspendLevel level)
    {
        _heavySubscriptions?.Dispose();
        _heavySubscriptions = null;

        CancelFormatsLoading();

        NetworkSpeedText = "";
        PingText = "";
        ShowNetworkStats = false;

        SuspendRequested?.Invoke();

        Log.Debug($"[PlayerBar] Suspended (level={level}): heavy subscriptions disposed");
    }

    protected override void OnResume(SuspendLevel previousLevel)
    {
        Log.Debug($"[PlayerBar] OnResume called (previousLevel={previousLevel})");

        if (IsTrackResetting)
        {
            if (IsPlaying || _audio.CurrentPosition.TotalSeconds > AudioIsPlayingThresholdSec)
            {
                IsTrackResetting = false;
                SyncPositionFromEngine();
                SyncBufferState();
            }
        }

        IsSeekBusy = false;
        IsFormatsLoading = false;

        SubscribeHeavy();

        ResumeRequested?.Invoke();

        Log.Debug("[PlayerBar] Resumed: heavy subscriptions recreated");
    }

    /// <summary>
    /// Освобождает ресурсы, используемые ViewModel, и сохраняет текущую громкость.
    /// </summary>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            LocalizationService.Instance.LanguageChanged -= OnLanguageChanged;
            _youtube.AuthService.OnAuthStateChanged -= ClearRestrictedTracksCache;

            _activeHintCts?.Cancel();
            _activeHintCts?.Dispose();

            CancelFormatsLoading();

            _heavySubscriptions?.Dispose();

            if (_isInitialized && Volume > 0)
            {
                _playerControl.CommitVolume();
            }
        }
        base.Dispose(disposing);
    }

    #endregion
}
