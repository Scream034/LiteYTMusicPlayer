using System.ComponentModel;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Media;
using ReactiveUI;

namespace LMP.UI.Features.Player;

/// <summary>
/// Code-behind нижней панели управления плеером (Player Bar).
/// </summary>
public partial class PlayerBarView : UserControl
{
    private static class LayoutConstants
    {
        /// <summary>
        /// Половина ширины прямоугольного бегунка таймлайна (3px / 2 = 1.5px).
        /// Исключает размытие границ и субпиксельное смещение при отрисовке.
        /// </summary>
        public const double SeekThumbHalfWidth = 1.5;
        public const double SeekCursorHalfWidth = 1.0;
        public const double SeekPreviewCursorHalfWidth = 1.5;

        public const int SeekHintAutoHideMs = 1250;

        public const double RenderTransformResetValue = 0.0;
        public const double DefaultTooltipWidth = 46.0;
        public const double DefaultHintWidth = 180.0;
    }

    private static class GlideConstants
    {
        public const double DurationSec = 0.175;
        public const double TriggerThresholdSec = 0.8;
        public const double RetargetThresholdSec = 0.1;
        public const double MaxJumpSec = 0.25;
    }

    #region State

    private bool _isDraggingSeek;
    private bool _isSuspended;

    private double _seekDragRatio;
    private double _cachedSeekWidth;

    /// <summary>
    /// Сохраняет координату предпросмотра (0.0 - 1.0) для анимации
    /// интерактивного соединительного моста (Span Selection Bridge).
    /// </summary>
    private double? _currentPreviewRatio;

    private readonly SerialDisposable _seekHintDisposable = new();

    private FlyoutBase? _formatFlyout;
    private PlayerBarViewModel? _currentViewModel;

    private bool _isRafRunning;
    private TopLevel? _topLevel;

    private TranslateTransform? _seekThumbTranslate;
    private TranslateTransform? _seekCursorTranslate;
    private TranslateTransform? _seekTooltipTranslate;

    /// <summary>
    /// Трансформация смещения для подсказки отмены перемещения.
    /// Позволяет перемещать подсказку вслед за курсором мыши на аппаратном уровне.
    /// </summary>
    private TranslateTransform? _seekHintTranslate;

    private double _lastEnginePosition = -1.0;
    private double _anchorPosition;
    private TimeSpan _anchorFrameTime;

    private double _displayPosition;
    private double _glideStart;
    private double _glideTarget;
    private TimeSpan _glideStartTime;
    private bool _isGliding;

    #endregion

    #region Initialization

    /// <summary>
    /// Инициализирует новый экземпляр класса <see cref="PlayerBarView"/>.
    /// Загружает XAML-разметку, инициализирует аппаратные трансформации и регистрирует обработчики событий.
    /// </summary>
    public PlayerBarView()
    {
        InitializeComponent();
        CacheRenderTransforms();
        SetupEventHandlers();
    }

    private void CacheRenderTransforms()
    {
        _seekThumbTranslate = (TranslateTransform)SeekThumb.RenderTransform!;
        _seekCursorTranslate = (TranslateTransform)SeekCursor.RenderTransform!;

        _seekTooltipTranslate = new TranslateTransform();
        SeekTooltip.RenderTransform = _seekTooltipTranslate;

        _seekHintTranslate = new TranslateTransform();
        SeekHint.RenderTransform = _seekHintTranslate;

        ApplySliderReset();
    }

    private void SetupEventHandlers()
    {
        SeekContainer.PropertyChanged += OnSeekContainerPropertyChanged;

        _formatFlyout = FormatButton.Flyout;
        if (_formatFlyout != null)
        {
            _formatFlyout.Opened += (_, _) => FormatButton.Classes.Add("popup-open");
            _formatFlyout.Closed += (_, _) => FormatButton.Classes.Remove("popup-open");
        }

        KeyDown += OnKeyDown;
    }

    #endregion

    #region Lifecycle

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _topLevel = TopLevel.GetTopLevel(this);

        if (VisualRoot is Window window)
        {
            window.Activated += OnWindowActivated;
            window.Deactivated += OnWindowDeactivated;
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);

        StopRaf();
        _topLevel = null;

        if (VisualRoot is Window window)
        {
            window.Activated -= OnWindowActivated;
            window.Deactivated -= OnWindowDeactivated;
        }

        CloseAllPopups();
        CancelSeekDrag();
        UnsubscribeFromViewModel();

        _seekHintDisposable.Dispose();
    }

    private void OnWindowActivated(object? sender, EventArgs e)
    {
        if (!_isSuspended) RefreshAllVisuals();
    }

    private void OnWindowDeactivated(object? sender, EventArgs e)
    {
        CloseAllPopups();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        UnsubscribeFromViewModel();

        if (DataContext is PlayerBarViewModel vm)
        {
            _currentViewModel = vm;
            vm.PropertyChanged += OnViewModelPropertyChanged;
            vm.SuspendRequested += OnSuspend;
            vm.ResumeRequested += OnResume;

            if (!_isSuspended) RefreshAllVisuals();
        }
    }

    private void UnsubscribeFromViewModel()
    {
        if (_currentViewModel is null) return;
        _currentViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _currentViewModel.SuspendRequested -= OnSuspend;
        _currentViewModel.ResumeRequested -= OnResume;
        _currentViewModel = null;
    }

    private void OnViewModelPropertyChanged(
       object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not PlayerBarViewModel vm) return;

        switch (e.PropertyName)
        {
            case nameof(PlayerBarViewModel.IsLoading):
            case nameof(PlayerBarViewModel.IsTrackResetting):
                if (vm.IsTrackResetting) ApplySliderReset();
                else RemoveSliderReset();

                // Детерминированно показываем искру при любой загрузке, включая смену трека
                SparkContainer.IsVisible = vm.IsLoading;
                return;
        }

        if (_isSuspended) return;

        switch (e.PropertyName)
        {
            case nameof(PlayerBarViewModel.IsPlaying):
            case nameof(PlayerBarViewModel.PositionSeconds):
            case nameof(PlayerBarViewModel.DurationSeconds):
                EnsureRafRunning();
                break;
        }
    }

    private void ApplySliderReset()
    {
        StopRaf();

        _seekThumbTranslate!.X = LayoutConstants.RenderTransformResetValue;
        _seekCursorTranslate!.X = LayoutConstants.RenderTransformResetValue;

        CustomProgressBar.Value = 0;
        CustomProgressBar.IsVisible = true;

        _isGliding = false;
        _displayPosition = 0;
        _currentPreviewRatio = null;

        SeekRangeSpan.Opacity = 0;
        SeekTooltip.Opacity = 0;
        SeekHint.Opacity = 0;

        _seekHintTranslate?.X = LayoutConstants.RenderTransformResetValue;

        // Позволяем искре отображаться во время сброса, если в этот момент идет буферизация
        SparkContainer.IsVisible = _currentViewModel?.IsLoading ?? false;
    }

    private void RemoveSliderReset()
    {
        CustomProgressBar.IsVisible = true;
        bool isLoading = _currentViewModel?.IsLoading ?? false;
        SparkContainer.IsVisible = isLoading;

        if (!_isSuspended) RefreshAllVisuals();
    }

    public void OnSuspend()
    {
        _isSuspended = true;
        StopRaf();
        CloseAllPopups();
    }

    public void OnResume()
    {
        _isSuspended = false;
        _cachedSeekWidth = SeekContainer.Bounds.Width;
        RefreshAllVisuals();
    }

    #endregion

    #region Shuffle Button

    private void OnShuffleButtonEntered(object? sender, PointerEventArgs e)
        => ShufflePopup.IsOpen = true;

    private void OnShuffleButtonExited(object? sender, PointerEventArgs e)
        => ShufflePopup.IsOpen = false;

    private void OnShuffleButtonPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(sender as Control).Properties.IsRightButtonPressed)
        {
            e.Handled = true;
            _currentViewModel?.ToggleAutoShuffleCommand.Execute().Subscribe();
        }
    }

    #endregion

    #region Unified Visual Updates

    private void RefreshAllVisuals()
    {
        if (_currentViewModel is { } vm)
        {
            ApplySeekFromEngine(vm);
            EnsureRafRunning();
        }
    }

    private void ApplySeekFromEngine(PlayerBarViewModel vm)
    {
        if (vm.IsTrackResetting) return;

        double width = _cachedSeekWidth > 0 ? _cachedSeekWidth : SeekContainer.Bounds.Width;
        double duration = vm.DurationSeconds;
        if (width <= 0 || duration <= 0) return;

        double position = vm.ReadCurrentPositionSeconds();
        _anchorPosition = position;
        _anchorFrameTime = TimeSpan.Zero;
        _lastEnginePosition = -1.0;

        _isGliding = false;
        _displayPosition = position;

        double ratio = Math.Clamp(position / duration, 0.0, 1.0);
        ApplySeekVisual(ratio, width);

        CustomProgressBar.Value = position;
    }

    private void ApplySeekVisual(double ratio, double width)
    {
        double position = width * ratio;
        _seekThumbTranslate!.X = position - LayoutConstants.SeekThumbHalfWidth;
        _seekCursorTranslate!.X = position - LayoutConstants.SeekCursorHalfWidth;
    }

    #endregion

    #region Seek RAF Loop

    private void EnsureRafRunning()
    {
        if (_isSuspended) return;

        _topLevel ??= TopLevel.GetTopLevel(this);

        if (!_isRafRunning)
        {
            _isRafRunning = true;
            _topLevel?.RequestAnimationFrame(OnAnimationFrame);
        }
    }

    private void StopRaf()
    {
        _isRafRunning = false;
        _lastEnginePosition = -1.0;
    }

    private void OnAnimationFrame(TimeSpan frameTime)
    {
        if (!_isRafRunning) return;

        bool shouldContinue = ApplySeekFrame(frameTime);

        if (shouldContinue)
            _topLevel?.RequestAnimationFrame(OnAnimationFrame);
        else
            _isRafRunning = false;
    }

    private bool ApplySeekFrame(TimeSpan frameTime)
    {
        if (_currentViewModel is not { } vm) return false;
        if (vm.IsTrackResetting) return false;

        double width = _cachedSeekWidth > 0 ? _cachedSeekWidth : SeekContainer.Bounds.Width;
        double duration = vm.DurationSeconds;
        if (width <= 0 || duration <= 0) return false;

        // Во время перемотки блокируем перезапись положения, фиксируя ползунок на месте скраббинга
        if (vm.IsSeekBusy)
        {
            double displayPos = vm.PositionSeconds;
            double seekRatio = displayPos / duration;
            ApplySeekVisual(seekRatio, width);
            CustomProgressBar.Value = displayPos;
            return true;
        }

        double enginePosition = vm.ReadCurrentPositionSeconds();

        if (Math.Abs(enginePosition - _lastEnginePosition) > 0.001)
        {
            if (_lastEnginePosition >= 0.0 && Math.Abs(enginePosition - _lastEnginePosition) > GlideConstants.TriggerThresholdSec)
            {
                _isGliding = true;
                _glideStart = _displayPosition;
                _glideTarget = enginePosition;
                _glideStartTime = frameTime;
            }
            else if (_isGliding && Math.Abs(enginePosition - _glideTarget) > GlideConstants.RetargetThresholdSec)
            {
                _glideStart = _displayPosition;
                _glideTarget = enginePosition;
                _glideStartTime = frameTime;
            }

            _anchorPosition = enginePosition;
            _anchorFrameTime = frameTime;
            _lastEnginePosition = enginePosition;
        }

        double elapsed = (frameTime - _anchorFrameTime).TotalSeconds;
        if (elapsed > GlideConstants.MaxJumpSec) elapsed = GlideConstants.MaxJumpSec;

        double rate = vm.IsPlaying ? 1.0 : 0.0;
        double targetPosition = Math.Clamp(_anchorPosition + elapsed * rate, 0.0, duration);
        double displayPosition = targetPosition;

        if (_isGliding)
        {
            double glideElapsed = (frameTime - _glideStartTime).TotalSeconds;

            if (glideElapsed >= GlideConstants.DurationSec)
            {
                _isGliding = false;
                displayPosition = targetPosition;
            }
            else
            {
                double t = glideElapsed / GlideConstants.DurationSec;
                double easeOut = t * (2.0 - t);
                displayPosition = _glideStart + (_glideTarget - _glideStart) * easeOut;
            }
        }

        _displayPosition = displayPosition;
        double currentRatio = displayPosition / duration;
        double currentX = currentRatio * width;
        ApplySeekVisual(currentRatio, width);
        CustomProgressBar.Value = displayPosition;

        if (_currentPreviewRatio.HasValue && _isDraggingSeek)
        {
            double previewX = _currentPreviewRatio.Value * width;
            double left = Math.Min(currentX, previewX);
            double spanWidth = Math.Abs(currentX - previewX);

            Canvas.SetLeft(SeekRangeSpan, left);
            SeekRangeSpan.Width = spanWidth;
        }

        return vm.IsPlaying || _isGliding || (_isDraggingSeek && _currentPreviewRatio.HasValue);
    }

    #endregion

    #region Bounds Handlers

    private void OnSeekContainerPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property.Name != nameof(Bounds)) return;

        _cachedSeekWidth = SeekContainer.Bounds.Width;
        if (!_isSuspended) RefreshAllVisuals();
    }

    #endregion

    #region Seek Visual Helpers

    /// <summary>
    /// Обновляет положение и время тултипа предпросмотра с защитой от выхода за границы экрана.
    /// </summary>
    private void UpdateSeekTooltip(double x, double seconds)
    {
        double containerWidth = _cachedSeekWidth > 0.0 ? _cachedSeekWidth : SeekContainer.Bounds.Width;
        double width = SeekTooltip.Bounds.Width > 0.0 ? SeekTooltip.Bounds.Width : LayoutConstants.DefaultTooltipWidth;

        double targetX = x - width / 2.0;

        if (containerWidth > 0.0)
        {
            // Защитный отступ от краев таймлайна в пикселях
            const double safetyMargin = 6.0;
            double minX = safetyMargin;
            double maxX = Math.Max(safetyMargin, containerWidth - width - safetyMargin);

            _seekTooltipTranslate!.X = Math.Clamp(targetX, minX, maxX);
        }
        else
        {
            _seekTooltipTranslate!.X = targetX;
        }

        var time = TimeSpan.FromSeconds(Math.Max(0.0, seconds));
        HoverTimeText.Text = time.TotalHours >= 1.0
            ? time.ToString(@"h\:mm\:ss")
            : time.ToString(@"m\:ss");
    }

    /// <summary>
    /// Обновляет положение подсказки отмены с защитой от выхода за границы экрана.
    /// </summary>
    private void UpdateSeekHintPosition(double x)
    {
        if (_seekHintTranslate is null) return;

        double containerWidth = _cachedSeekWidth > 0.0 ? _cachedSeekWidth : SeekContainer.Bounds.Width;
        double width = SeekHint.Bounds.Width > 0.0 ? SeekHint.Bounds.Width : LayoutConstants.DefaultHintWidth;

        double targetX = x - width / 2.0;

        if (containerWidth > 0.0)
        {
            // Синхронный защитный отступ для подсказки отмены
            const double safetyMargin = 6.0;
            double minX = safetyMargin;
            double maxX = Math.Max(safetyMargin, containerWidth - width - safetyMargin);

            _seekHintTranslate.X = Math.Clamp(targetX, minX, maxX);
        }
        else
        {
            _seekHintTranslate.X = targetX;
        }
    }

    private void UpdateSeekPreview(double x)
        => Canvas.SetLeft(SeekPreviewCursor, x - LayoutConstants.SeekPreviewCursorHalfWidth);

    #endregion

    #region Seek Hint (Canvas-based)

    private void ShowSeekHint(string text, int? autoHideMs = null)
    {
        SeekHintText.Text = text;
        SeekHint.Opacity = 1; // Плавно отображаем через Opacity

        if (autoHideMs.HasValue)
        {
            _seekHintDisposable.Disposable = Observable
                .Timer(TimeSpan.FromMilliseconds(autoHideMs.Value))
                .ObserveOn(RxSchedulers.MainThreadScheduler)
                .Subscribe(_ => SeekHint.Opacity = 0);
        }
        else
        {
            _seekHintDisposable.Disposable = null;
        }
    }

    private void HideSeekHint()
    {
        SeekHint.Opacity = 0; // Плавно скрываем через Opacity
        _seekHintDisposable.Disposable = null;
    }

    #endregion

    #region Popup Helpers

    private void CloseAllPopups()
    {
        SeekTooltip.Opacity = 0;
        SeekRangeSpan.Opacity = 0;
        SeekHint.Opacity = 0; // Синхронно тушим подсказку
        _seekHintDisposable.Disposable = null;
    }

    private void ShowSeekPreview() => SeekPreviewCursor.Classes.Add("active");
    private void HideSeekPreview() => SeekPreviewCursor.Classes.Remove("active");

    #endregion

    #region Seek Slider

    private void OnSeekAreaMoved(object? sender, PointerEventArgs e)
    {
        if (_currentViewModel is not { DurationSeconds: > 0.0 } vm) return;

        var point = e.GetCurrentPoint(SeekContainer);

        if (point.Properties.IsRightButtonPressed)
        {
            CancelSeekDrag();
            return;
        }

        double width = _cachedSeekWidth > 0.0 ? _cachedSeekWidth : SeekContainer.Bounds.Width;
        if (width <= 0.0) return;

        double x = Math.Clamp(point.Position.X, 0.0, width);
        double ratio = x / width;
        double seconds = ratio * vm.DurationSeconds;

        UpdateSeekTooltip(x, seconds);
        UpdateSeekHintPosition(x);
        _currentPreviewRatio = ratio;

        if (_isDraggingSeek)
        {
            _seekDragRatio = ratio;
            ShowSeekPreview();
            UpdateSeekPreview(x);

            SeekTooltip.Opacity = 1;
            SeekRangeSpan.Opacity = 1;
        }
        else if (SeekHitBox.IsPointerOver)
        {
            ShowSeekPreview();
            UpdateSeekPreview(x);

            SeekTooltip.Opacity = 1;
            SeekRangeSpan.Opacity = 0;
        }
        else
        {
            SeekTooltip.Opacity = 0;
            SeekRangeSpan.Opacity = 0;
            HideSeekPreview();
            _currentPreviewRatio = null;
        }

        EnsureRafRunning();
    }

    private void OnSeekAreaPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_currentViewModel is not { HasTrack: true } vm) return;

        var point = e.GetCurrentPoint(SeekContainer);

        if (point.Properties.IsRightButtonPressed) { CancelSeekDrag(); return; }
        if (!point.Properties.IsLeftButtonPressed) return;

        _isDraggingSeek = true;
        SeekContainer.Classes.Add("dragging");
        e.Pointer.Capture(SeekHitBox);
        vm.StartSeek();

        double width = _cachedSeekWidth > 0.0 ? _cachedSeekWidth : SeekContainer.Bounds.Width;
        if (width <= 0.0) return;

        double x = Math.Clamp(point.Position.X, 0.0, width);
        _seekDragRatio = x / width;
        _currentPreviewRatio = _seekDragRatio;

        ShowSeekPreview();
        UpdateSeekPreview(x);
        UpdateSeekTooltip(x, _seekDragRatio * vm.DurationSeconds);
        UpdateSeekHintPosition(x);

        SeekTooltip.Opacity = 1;
        SeekRangeSpan.Opacity = 1;

        EnsureRafRunning();

        ShowSeekHint(
            vm.L.Get("Seek_CancelHint", "ESC or Right Click to cancel"));
    }

    private void OnSeekAreaReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_isDraggingSeek) return;

        if (_currentViewModel is { } vm)
        {
            double targetSeconds = _seekDragRatio * vm.DurationSeconds;
            vm.UpdateSeekPosition(targetSeconds);
            vm.EndSeek();
        }

        HideSeekHint();
        CompleteSeekDrag(e.Pointer);
    }

    private void OnSeekAreaExited(object? sender, PointerEventArgs e)
    {
        if (_isDraggingSeek) return;

        SeekTooltip.Opacity = 0;
        SeekRangeSpan.Opacity = 0;
        HideSeekPreview();
        _currentPreviewRatio = null;
    }

    private void OnSeekAreaCaptureLost(object? sender, PointerCaptureLostEventArgs e)
        => CancelSeekDrag();

    private void CompleteSeekDrag(IPointer pointer)
    {
        _isDraggingSeek = false;
        pointer.Capture(null);

        SeekContainer.Classes.Remove("dragging");

        SeekRangeSpan.Opacity = 0;

        if (!SeekHitBox.IsPointerOver)
        {
            SeekTooltip.Opacity = 0;
            HideSeekPreview();
            _currentPreviewRatio = null;
        }
    }

    /// <summary>
    /// Отменяет активную операцию перемотки (Seek) и сбрасывает визуальное состояние.
    /// </summary>
    private void CancelSeekDrag()
    {
        if (_isDraggingSeek)
        {
            _isDraggingSeek = false;
            SeekContainer.Classes.Remove("dragging");

            if (_currentViewModel is { } vm)
            {
                vm.CancelSeek();

                // Всегда скрываем подсказку отмены немедленно
                HideSeekHint();

                ApplySeekFromEngine(vm);
            }
        }

        // Скрываем соединительный мост-рельсы в любом случае
        SeekRangeSpan.Opacity = 0;

        // Если курсор всё ещё находится на таймлайне, НЕ скрываем текущее время (SeekTooltip),
        // а скрываем только подсказку (SeekHint). Если курсора нет — скрываем и обнуляем всё.
        if (!SeekHitBox.IsPointerOver)
        {
            SeekTooltip.Opacity = 0;
            HideSeekPreview();
            _currentPreviewRatio = null;
        }
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;

        bool hadSeek = _isDraggingSeek;
        CancelSeekDrag();

        if (hadSeek) e.Handled = true;
    }

    #endregion
}