using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;

namespace LMP.UI.Controls;

public partial class VolumeControl : UserControl
{
    private static class VolumeConstants
    {
        public const double DefaultSliderHeight = 100.0;
        public const double MaxSliderHeightMultiplier = 2.5;
        public const double HeightGrowthFactor = 1.5;

        public const double ThumbRadius = 5.0;
        public const int DefaultMaxVolume = 100;

        public const double ActiveBorderThickness = 2.0;
        public const double InactiveBorderThickness = 1.0;
        public const double PopupCornerRadius = 10.0;
        public const double ButtonCornerRadius = 19.0;

        public const int PopupCloseDelayMs = 200;
    }

    #region Styled Properties 

    public static readonly StyledProperty<int> VolumeProperty =
        AvaloniaProperty.Register<VolumeControl, int>(nameof(Volume), defaultBindingMode: BindingMode.TwoWay);

    public static readonly StyledProperty<int> MaxVolumeProperty =
        AvaloniaProperty.Register<VolumeControl, int>(nameof(MaxVolume), VolumeConstants.DefaultMaxVolume);

    #endregion

    #region Direct Properties

    public static readonly DirectProperty<VolumeControl, bool> IsMutedProperty =
        AvaloniaProperty.RegisterDirect<VolumeControl, bool>(nameof(IsMuted), o => o.IsMuted);

    public static readonly DirectProperty<VolumeControl, bool> IsVolumeLowProperty =
        AvaloniaProperty.RegisterDirect<VolumeControl, bool>(nameof(IsVolumeLow), o => o.IsVolumeLow);

    public static readonly DirectProperty<VolumeControl, bool> IsVolumeMediumProperty =
        AvaloniaProperty.RegisterDirect<VolumeControl, bool>(nameof(IsVolumeMedium), o => o.IsVolumeMedium);

    public static readonly DirectProperty<VolumeControl, bool> IsVolumeHighProperty =
        AvaloniaProperty.RegisterDirect<VolumeControl, bool>(nameof(IsVolumeHigh), o => o.IsVolumeHigh);

    public static readonly DirectProperty<VolumeControl, bool> IsVolumeBoostedProperty =
        AvaloniaProperty.RegisterDirect<VolumeControl, bool>(nameof(IsVolumeBoosted), o => o.IsVolumeBoosted);

    public static readonly DirectProperty<VolumeControl, string> VolumePercentTextProperty =
        AvaloniaProperty.RegisterDirect<VolumeControl, string>(nameof(VolumePercentText), o => o.VolumePercentText);

    public static readonly DirectProperty<VolumeControl, double> VolumeSliderHeightProperty =
        AvaloniaProperty.RegisterDirect<VolumeControl, double>(nameof(VolumeSliderHeight), o => o.VolumeSliderHeight);

    #endregion

    #region Backing Fields 

    private bool _isMuted;
    private bool _isVolumeLow;
    private bool _isVolumeMedium;
    private bool _isVolumeHigh;
    private bool _isVolumeBoosted;
    private string _volumePercentText = "0";
    private double _volumeSliderHeight = VolumeConstants.DefaultSliderHeight;

    #endregion

    #region Properties Accessors

    public int Volume
    {
        get => GetValue(VolumeProperty);
        set => SetValue(VolumeProperty, value);
    }

    public int MaxVolume
    {
        get => GetValue(MaxVolumeProperty);
        set => SetValue(MaxVolumeProperty, value);
    }

    public bool IsMuted { get => _isMuted; private set => SetAndRaise(IsMutedProperty, ref _isMuted, value); }
    public bool IsVolumeLow { get => _isVolumeLow; private set => SetAndRaise(IsVolumeLowProperty, ref _isVolumeLow, value); }
    public bool IsVolumeMedium { get => _isVolumeMedium; private set => SetAndRaise(IsVolumeMediumProperty, ref _isVolumeMedium, value); }
    public bool IsVolumeHigh { get => _isVolumeHigh; private set => SetAndRaise(IsVolumeHighProperty, ref _isVolumeHigh, value); }
    public bool IsVolumeBoosted { get => _isVolumeBoosted; private set => SetAndRaise(IsVolumeBoostedProperty, ref _isVolumeBoosted, value); }
    public string VolumePercentText { get => _volumePercentText; private set => SetAndRaise(VolumePercentTextProperty, ref _volumePercentText, value); }
    public double VolumeSliderHeight { get => _volumeSliderHeight; private set => SetAndRaise(VolumeSliderHeightProperty, ref _volumeSliderHeight, value); }

    #endregion

    public event EventHandler? VolumeChangeCompleted;

    private bool _isDraggingVolume;
    private bool _isVolumePopupHovered;
    private bool _isVolumeButtonHovered;
    private int _lastVolumeBeforeMute = 50;
    private IDisposable? _closeTimer;

    public VolumeControl()
    {
        InitializeComponent();

        VolumeButton.PointerEntered += OnVolumeButtonEntered;
        VolumeButton.PointerExited += OnVolumeButtonExited;

        VolumePopup.Opened += OnVolumePopupOpened;
        VolumePopup.Closed += OnVolumePopupClosed;

        UpdateDependentProperties();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == VolumeProperty || change.Property == MaxVolumeProperty)
        {
            UpdateDependentProperties();
            UpdateVolumeVisual();
        }
    }

    private void UpdateDependentProperties()
    {
        int maxVol = MaxVolume > 0 ? MaxVolume : VolumeConstants.DefaultMaxVolume;

        if (Volume > maxVol)
        {
            Volume = maxVol;
        }

        VolumeSliderHeight = ComputeVolumeSliderHeight(maxVol);

        int effectivePercent = (int)Math.Round((double)Volume / maxVol * 100);
        if (effectivePercent > 100) effectivePercent = 100;

        bool isMuted = Volume < 1;
        bool isVolumeLow = Volume >= 1 && effectivePercent <= 33;
        bool isVolumeMedium = !isMuted && effectivePercent > 33 && effectivePercent <= 66;
        bool isVolumeHigh = !isMuted && effectivePercent > 66;

        IsMuted = isMuted;
        IsVolumeLow = isVolumeLow;
        IsVolumeMedium = isVolumeMedium;
        IsVolumeHigh = isVolumeHigh;
        IsVolumeBoosted = Volume > VolumeConstants.DefaultMaxVolume;

        VolumePercentText = $"{Volume}";

        // Прямое и безопасное получение системных ресурсов темы
        var accentBrush = (IBrush)(Application.Current?.Resources["AccentBrush"] ?? Brushes.Purple);
        var textMutedBrush = (IBrush)(Application.Current?.Resources["TextMutedBrush"] ?? Brushes.Gray);
        var textSecondaryBrush = (IBrush)(Application.Current?.Resources["TextSecondaryBrush"] ?? Brushes.LightGray);
        var transparentBrush = (IBrush)(Application.Current?.Resources["AccentBrushTransparent"] ?? Brushes.Transparent);

        bool isPopupOpen = VolumePopup != null && VolumePopup.IsOpen;

        // ПРЯМОЕ УПРАВЛЕНИЕ СВОЙСТВАМИ КНОПКИ (100% надежность отрисовки без участия биндингов)
        if (isPopupOpen)
        {
            VolumeButton.BorderBrush = accentBrush;
            VolumeButton.Foreground = accentBrush;
            VolumeButton.BorderThickness = new Thickness(
                VolumeConstants.ActiveBorderThickness, 
                0, 
                VolumeConstants.ActiveBorderThickness, 
                VolumeConstants.ActiveBorderThickness);
            VolumeButton.CornerRadius = new CornerRadius(
                0, 
                0, 
                VolumeConstants.PopupCornerRadius, 
                VolumeConstants.PopupCornerRadius);
        }
        else
        {
            VolumeButton.BorderBrush = transparentBrush;
            VolumeButton.Foreground = isMuted ? textMutedBrush : textSecondaryBrush;
            VolumeButton.BorderThickness = new Thickness(VolumeConstants.InactiveBorderThickness);
            VolumeButton.CornerRadius = new CornerRadius(VolumeConstants.ButtonCornerRadius);
        }
    }

    private static double ComputeVolumeSliderHeight(int maxVolume)
    {
        double height = VolumeConstants.DefaultSliderHeight;
        if (maxVolume > VolumeConstants.DefaultMaxVolume)
        {
            height += (maxVolume - VolumeConstants.DefaultMaxVolume) * VolumeConstants.HeightGrowthFactor;
        }

        return Math.Clamp(height, VolumeConstants.DefaultSliderHeight, VolumeConstants.DefaultSliderHeight * VolumeConstants.MaxSliderHeightMultiplier);
    }

    private void UpdateVolumeVisual()
    {
        double height = VolumeSliderHeight;
        if (double.IsNaN(height) || height <= 0) height = VolumeConstants.DefaultSliderHeight;

        int max = MaxVolume > 0 ? MaxVolume : VolumeConstants.DefaultMaxVolume;
        double ratio = Math.Clamp((double)Volume / max, 0.0, 1.0);

        VolumeBar.Height = height * ratio;

        double thumbTop = height * (1.0 - ratio) - VolumeConstants.ThumbRadius;
        VolumeThumb.Margin = new Thickness(0, thumbTop, 0, 0);
    }

    #region Mouse & Touch Drag Interaction

    public void OnVolumeButtonClick(object? sender, RoutedEventArgs e)
    {
        if (Volume < 1)
        {
            Volume = Math.Min(_lastVolumeBeforeMute > 0 ? _lastVolumeBeforeMute : 50, MaxVolume);
        }
        else
        {
            _lastVolumeBeforeMute = Volume;
            Volume = 0;
        }
        VolumeChangeCompleted?.Invoke(this, EventArgs.Empty);
    }

    public void OnVolumeScroll(object? sender, PointerWheelEventArgs e)
    {
        int step = MaxVolume > 100 ? Math.Max(1, MaxVolume / 200) : 1;
        int delta = e.Delta.Y > 0 ? step : -step;
        Volume = Math.Clamp(Volume + delta, 0, MaxVolume);
        VolumeChangeCompleted?.Invoke(this, EventArgs.Empty);
        e.Handled = true;
    }

    public void OnVolumeAreaMoved(object? sender, PointerEventArgs e)
    {
        if (!_isDraggingVolume) return;

        var point = e.GetCurrentPoint(VolumeSliderPanel);
        if (point.Properties.IsRightButtonPressed) { CancelVolumeDrag(); return; }

        double height = VolumeSliderHeight;
        if (height <= 0) return;

        double y = Math.Clamp(point.Position.Y, 0, height);
        double ratio = 1.0 - y / height;
        Volume = Math.Clamp((int)(ratio * MaxVolume), 0, MaxVolume);
    }

    public void OnVolumeAreaPressed(object? sender, PointerPressedEventArgs e)
    {
        var point = e.GetCurrentPoint(VolumeSliderPanel);
        if (point.Properties.IsRightButtonPressed) { CancelVolumeDrag(); return; }
        if (!point.Properties.IsLeftButtonPressed) return;

        _isDraggingVolume = true;
        e.Pointer.Capture(VolumeHitBox);
        VolumeThumb.Classes.Add("dragging");
        VolumeBar.Classes.Add("dragging");

        double height = VolumeSliderHeight;
        if (height <= 0) return;

        double y = Math.Clamp(point.Position.Y, 0, height);
        double ratio = 1.0 - y / height;
        Volume = Math.Clamp((int)(ratio * MaxVolume), 0, MaxVolume);
    }

    public void OnVolumeAreaReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_isDraggingVolume) return;
        CompleteVolumeDrag(e.Pointer);
        VolumeChangeCompleted?.Invoke(this, EventArgs.Empty);
        TryScheduleVolumePopupClose();
    }

    public void OnVolumeAreaExited(object? sender, PointerEventArgs e) { }

    public void OnVolumeAreaCaptureLost(object? sender, PointerCaptureLostEventArgs e)
        => CancelVolumeDrag();

    private void CompleteVolumeDrag(IPointer pointer)
    {
        _isDraggingVolume = false;
        pointer.Capture(null);
        VolumeThumb.Classes.Remove("dragging");
        VolumeBar.Classes.Remove("dragging");
    }

    private void CancelVolumeDrag()
    {
        if (_isDraggingVolume)
        {
            _isDraggingVolume = false;
            VolumeThumb.Classes.Remove("dragging");
            VolumeBar.Classes.Remove("dragging");
        }
        UpdateVolumeVisual();
    }

    #endregion

    #region Auto Close Handlers

    public void OnVolumeButtonEntered(object? sender, PointerEventArgs e)
    {
        _isVolumeButtonHovered = true;
        _closeTimer?.Dispose();
        _closeTimer = null;
        VolumePopup.IsOpen = true;
        UpdateDependentProperties();
    }

    public void OnVolumeButtonExited(object? sender, PointerEventArgs e)
    {
        _isVolumeButtonHovered = false;
        TryScheduleVolumePopupClose();
        UpdateDependentProperties();
    }

    public void OnVolumePopupContentEntered(object? sender, PointerEventArgs e)
    {
        _isVolumePopupHovered = true;
        _closeTimer?.Dispose();
        _closeTimer = null;
        UpdateDependentProperties();
    }

    public void OnVolumePopupContentExited(object? sender, PointerEventArgs e)
    {
        _isVolumePopupHovered = false;
        if (!_isDraggingVolume) TryScheduleVolumePopupClose();
        UpdateDependentProperties();
    }

    private void TryScheduleVolumePopupClose()
    {
        if (_isDraggingVolume || _isVolumePopupHovered || _isVolumeButtonHovered) return;

        _closeTimer?.Dispose();
        _closeTimer = DispatcherTimer.RunOnce(() =>
        {
            if (!_isVolumePopupHovered && !_isVolumeButtonHovered && !_isDraggingVolume)
            {
                VolumePopup.IsOpen = false;
                UpdateDependentProperties();
            }
        }, TimeSpan.FromMilliseconds(VolumeConstants.PopupCloseDelayMs));
    }

    public void OnVolumePopupOpened(object? sender, EventArgs e)
    {
        UpdateDependentProperties();
        UpdateVolumeVisual();
    }

    public void OnVolumePopupClosed(object? sender, EventArgs e) => UpdateDependentProperties();

    #endregion
}