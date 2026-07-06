using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace LMP.UI.Controls;

/// <summary>
/// Высокопроизводительный прогресс-бар с поддержкой отображения сегментов буферизации,
/// двухцветной проигранной частью (кэшированная vs потоковая) и плавной LERP-интерполяцией буфера.
/// </summary>
public sealed class BufferedProgressBar : Control
{
    private static class ProgressConstants
    {
        public const double LerpFactor = 0.12;
        public const double SnapThreshold = 0.0005;
        public const double TranslucentOpacity = 0.25;
        public const double UncachedOpacity = 0.30;
        public const double MinWidthHeight = 0.001;
    }

    #region Styled Properties

    public static readonly StyledProperty<double> ValueProperty =
        AvaloniaProperty.Register<BufferedProgressBar, double>(nameof(Value));

    public static readonly StyledProperty<double> MaximumProperty =
        AvaloniaProperty.Register<BufferedProgressBar, double>(nameof(Maximum), 1.0);

    public static readonly StyledProperty<IReadOnlyList<(double Start, double End)>> BufferedRangesProperty =
        AvaloniaProperty.Register<BufferedProgressBar, IReadOnlyList<(double Start, double End)>>(nameof(BufferedRanges));

    public static readonly StyledProperty<bool> IsFullyBufferedProperty =
        AvaloniaProperty.Register<BufferedProgressBar, bool>(nameof(IsFullyBuffered));

    public static readonly StyledProperty<IBrush> ForegroundBrushProperty =
        AvaloniaProperty.Register<BufferedProgressBar, IBrush>(nameof(ForegroundBrush));

    public static readonly StyledProperty<IBrush> BackgroundBrushProperty =
        AvaloniaProperty.Register<BufferedProgressBar, IBrush>(nameof(BackgroundBrush));

    public static readonly StyledProperty<IBrush> BufferBrushProperty =
        AvaloniaProperty.Register<BufferedProgressBar, IBrush>(nameof(BufferBrush));

    #endregion

    #region Properties

    public double Value
    {
        get => GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public double Maximum
    {
        get => GetValue(MaximumProperty);
        set => SetValue(MaximumProperty, value);
    }

    public IReadOnlyList<(double Start, double End)> BufferedRanges
    {
        get => GetValue(BufferedRangesProperty);
        set => SetValue(BufferedRangesProperty, value);
    }

    public bool IsFullyBuffered
    {
        get => GetValue(IsFullyBufferedProperty);
        set => SetValue(IsFullyBufferedProperty, value);
    }

    public IBrush ForegroundBrush
    {
        get => GetValue(ForegroundBrushProperty);
        set => SetValue(ForegroundBrushProperty, value);
    }

    public IBrush BackgroundBrush
    {
        get => GetValue(BackgroundBrushProperty);
        set => SetValue(BackgroundBrushProperty, value);
    }

    public IBrush BufferBrush
    {
        get => GetValue(BufferBrushProperty);
        set => SetValue(BufferBrushProperty, value);
    }

    #endregion

    #region Fields

    private readonly List<(double Start, double End)> _smoothRanges = new();
    private bool _isInterpolating;
    private bool _rafActive;

    private IBrush? _cachedUncachedBrush;
    private Color _lastForegroundColor;
    private IBrush? _cachedBufferBrush;
    private Color _lastBufferColor;

    private static readonly IReadOnlyList<(double Start, double End)> FullyBufferedTarget = new[] { (0.0, 1.0) };

    #endregion

    static BufferedProgressBar()
    {
        AffectsRender<BufferedProgressBar>(
            ValueProperty,
            MaximumProperty,
            BufferedRangesProperty,
            IsFullyBufferedProperty,
            ForegroundBrushProperty,
            BackgroundBrushProperty,
            BufferBrushProperty);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (_isInterpolating)
        {
            StartAnimationLoop();
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _rafActive = false;
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == BufferedRangesProperty || change.Property == IsFullyBufferedProperty)
        {
            _isInterpolating = true;
            StartAnimationLoop();
        }
    }

    private void StartAnimationLoop()
    {
        if (_rafActive) return;
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;

        _rafActive = true;
        topLevel.RequestAnimationFrame(OnAnimationFrame);
    }

    private void OnAnimationFrame(TimeSpan time)
    {
        if (!_rafActive) return;

        UpdateSmoothRanges();
        InvalidateVisual();

        if (_isInterpolating)
        {
            TopLevel.GetTopLevel(this)?.RequestAnimationFrame(OnAnimationFrame);
        }
        else
        {
            _rafActive = false;
        }
    }

    private void UpdateSmoothRanges()
    {
        var targets = IsFullyBuffered ? FullyBufferedTarget : BufferedRanges;

        if (targets == null || targets.Count == 0)
        {
            _smoothRanges.Clear();
            _isInterpolating = false;
            return;
        }

        while (_smoothRanges.Count < targets.Count)
            _smoothRanges.Add(targets[_smoothRanges.Count]);
        while (_smoothRanges.Count > targets.Count)
            _smoothRanges.RemoveAt(_smoothRanges.Count - 1);

        bool needsMoreInterpolation = false;

        for (int i = 0; i < targets.Count; i++)
        {
            var (TStart, TEnd) = targets[i];
            var (SStart, SEnd) = _smoothRanges[i];

            double newStart;
            if (Math.Abs(TStart - SStart) > ProgressConstants.SnapThreshold)
            {
                newStart = SStart + (TStart - SStart) * ProgressConstants.LerpFactor;
                needsMoreInterpolation = true;
            }
            else
            {
                newStart = TStart;
            }

            double newEnd;
            if (Math.Abs(TEnd - SEnd) > ProgressConstants.SnapThreshold)
            {
                newEnd = SEnd + (TEnd - SEnd) * ProgressConstants.LerpFactor;
                needsMoreInterpolation = true;
            }
            else
            {
                newEnd = TEnd;
            }

            _smoothRanges[i] = (newStart, newEnd);
        }

        _isInterpolating = needsMoreInterpolation;
    }

    /// <inheritdoc />
    public override void Render(DrawingContext context)
    {
        var bounds = Bounds;
        double width = bounds.Width;
        double height = bounds.Height;

        if (width <= 0 || height <= 0) return;

        double max = Math.Max(ProgressConstants.MinWidthHeight, Maximum);
        double value = Math.Clamp(Value, 0.0, max);
        double playedRatio = value / max;
        double playedWidth = playedRatio * width;

        var bgBrush = BackgroundBrush ?? Brushes.Gray;
        double cornerRadius = height / 2.0;
        context.DrawRectangle(bgBrush, null, new Rect(0, 0, width, height), cornerRadius, cornerRadius);

        var bufBrush = BufferBrush ?? Brushes.LightGray;
        var ranges = _smoothRanges;

        if (ranges.Count > 0)
        {
            var brush = GetTranslucentBrush(bufBrush, ProgressConstants.TranslucentOpacity);
            for (int i = 0; i < ranges.Count; i++)
            {
                var (start, end) = ranges[i];
                start = Math.Clamp(double.IsFinite(start) ? start : 0.0, 0.0, 1.0);
                end = Math.Clamp(double.IsFinite(end) ? end : start, start, 1.0);

                double left = start * width;
                double w = (end - start) * width;

                if (w > 0)
                {
                    context.DrawRectangle(brush, null, new Rect(left, 0, w, height), cornerRadius, cornerRadius);
                }
            }
        }

        var fgBrush = ForegroundBrush ?? Brushes.Blue;

        if (playedWidth > 0)
        {
            var uncachedBrush = GetUncachedForegroundBrush(fgBrush);
            context.DrawRectangle(uncachedBrush, null, new Rect(0, 0, playedWidth, height), cornerRadius, cornerRadius);

            if (ranges.Count > 0)
            {
                for (int i = 0; i < ranges.Count; i++)
                {
                    var (start, end) = ranges[i];
                    start = Math.Clamp(double.IsFinite(start) ? start : 0.0, 0.0, 1.0);
                    end = Math.Clamp(double.IsFinite(end) ? end : start, start, 1.0);

                    double intersectStart = Math.Max(start, 0.0);
                    double intersectEnd = Math.Min(end, playedRatio);

                    if (intersectEnd > intersectStart)
                    {
                        double left = intersectStart * width;
                        double w = (intersectEnd - intersectStart) * width;
                        if (w > 0)
                        {
                            context.DrawRectangle(fgBrush, null, new Rect(left, 0, w, height), cornerRadius, cornerRadius);
                        }
                    }
                }
            }
        }
    }

    private IBrush GetUncachedForegroundBrush(IBrush foreground)
    {
        if (foreground is ISolidColorBrush solid)
        {
            if (solid.Color != _lastForegroundColor)
            {
                _lastForegroundColor = solid.Color;
                _cachedUncachedBrush = new SolidColorBrush(solid.Color, ProgressConstants.UncachedOpacity);
            }
            return _cachedUncachedBrush ?? foreground;
        }
        return foreground;
    }

    private IBrush GetTranslucentBrush(IBrush brush, double opacity)
    {
        if (brush is ISolidColorBrush solid)
        {
            if (solid.Color != _lastBufferColor)
            {
                _lastBufferColor = solid.Color;
                _cachedBufferBrush = new SolidColorBrush(solid.Color, opacity);
            }
            return _cachedBufferBrush ?? brush;
        }
        return brush;
    }
}