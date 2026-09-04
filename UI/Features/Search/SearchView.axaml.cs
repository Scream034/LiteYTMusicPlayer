using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using ReactiveUI;

namespace LMP.UI.Features.Search;

/// <summary>
/// Представление экрана поиска треков.
/// Координирует адаптивную маску краев ленты, туннелирование клавиш автодополнения и управление скроллом.
/// </summary>
public partial class SearchView : UserControl
{
    private static readonly IBrush RightFadeMask = new LinearGradientBrush
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
        EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
        GradientStops =
        [
            new GradientStop(Color.FromRgb(255, 255, 255), 0.0),
            new GradientStop(Color.FromRgb(255, 255, 255), 0.92),
            new GradientStop(Color.FromArgb(0, 255, 255, 255), 1.0)
        ]
    };

    private static readonly IBrush LeftFadeMask = new LinearGradientBrush
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
        EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
        GradientStops =
        [
            new GradientStop(Color.FromArgb(0, 255, 255, 255), 0.0),
            new GradientStop(Color.FromRgb(255, 255, 255), 0.08),
            new GradientStop(Color.FromRgb(255, 255, 255), 1.0)
        ]
    };

    private static readonly IBrush BothFadeMask = new LinearGradientBrush
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
        EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
        GradientStops =
        [
            new GradientStop(Color.FromArgb(0, 255, 255, 255), 0.0),
            new GradientStop(Color.FromRgb(255, 255, 255), 0.06),
            new GradientStop(Color.FromRgb(255, 255, 255), 0.94),
            new GradientStop(Color.FromArgb(0, 255, 255, 255), 1.0)
        ]
    };

    private CompositeDisposable? _cleanup;

    public SearchView()
    {
        InitializeComponent();

        var searchBox = this.FindControl<TextBox>("SearchBox");
        if (searchBox != null)
        {
            searchBox.GotFocus += (sender, e) =>
            {
                if (DataContext is SearchViewModel vm && string.IsNullOrWhiteSpace(vm.SearchQuery))
                {
                    vm.OpenHistoryIfAvailable();
                }
            };

            // Перехватываем Tab и Right на стадии Tunneling, исключая сброс каретки и потерю фокуса
            searchBox.AddHandler(InputElement.KeyDownEvent, OnSearchBoxKeyDown, RoutingStrategies.Tunnel);
        }
    }

    private void OnSearchBoxKeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is not TextBox textBox || DataContext is not SearchViewModel vm)
            return;

        if (e.Key == Key.Tab && vm.HasGhostText)
        {
            e.Handled = true;
            vm.CompleteGhostText();
            textBox.CaretIndex = textBox.Text?.Length ?? 0;
            return;
        }

        // Автодополнение по стрелке Вправо разрешено ТОЛЬКО если курсор находится в самом конце текста
        if (e.Key == Key.Right && vm.HasGhostText && textBox.CaretIndex == (textBox.Text?.Length ?? 0))
        {
            e.Handled = true;
            vm.CompleteGhostText();
            textBox.CaretIndex = textBox.Text?.Length ?? 0;
            return;
        }
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);

        _cleanup?.Dispose();
        _cleanup = [];

        if (DataContext is SearchViewModel vm)
        {
            // Сбрасываем скролл наверх в момент старта нового поиска
            vm.WhenAnyValue(x => x.IsLoading)
                .Where(loading => loading)
                .ObserveOn(RxSchedulers.MainThreadScheduler)
                .Subscribe(_ =>
                {
                    var sv = this.FindControl<ScrollViewer>("ResultsScrollViewer");
                    if (sv != null)
                    {
                        sv.Offset = new Vector(0, 0);
                    }
                })
                .DisposeWith(_cleanup);

            // Адаптивное управление маской краев ленты подсказок
            var ribbonSv = this.FindControl<ScrollViewer>("RibbonScrollViewer");
            if (ribbonSv != null)
            {
                ribbonSv.GetObservable(ScrollViewer.OffsetProperty)
                    .Subscribe(_ => UpdateRibbonMask(ribbonSv))
                    .DisposeWith(_cleanup);

                ribbonSv.GetObservable(ScrollViewer.ExtentProperty)
                    .Subscribe(_ => UpdateRibbonMask(ribbonSv))
                    .DisposeWith(_cleanup);

                ribbonSv.GetObservable(ScrollViewer.ViewportProperty)
                    .Subscribe(_ => UpdateRibbonMask(ribbonSv))
                    .DisposeWith(_cleanup);
            }
        }
    }

    /// <summary>
    /// Динамически вычисляет маску затухания краев ленты:
    /// маски появляются строго при наличии доступной области прокрутки.
    /// </summary>
    private static void UpdateRibbonMask(ScrollViewer sv)
    {
        double extent = sv.Extent.Width;
        double viewport = sv.Viewport.Width;
        double offset = sv.Offset.X;

        if (extent <= viewport || viewport <= 0)
        {
            sv.OpacityMask = null;
            return;
        }

        bool hasLeft = offset > 4.0;
        bool hasRight = (offset + viewport) < (extent - 4.0);

        if (hasLeft && hasRight)
            sv.OpacityMask = BothFadeMask;
        else if (hasLeft)
            sv.OpacityMask = LeftFadeMask;
        else if (hasRight)
            sv.OpacityMask = RightFadeMask;
        else
            sv.OpacityMask = null;
    }

    /// <summary>
    /// <summary>
    /// Обрабатывает клики по чипам: ЛКМ — моментальный поиск, ПКМ — удаление элемента.
    /// </summary>
    private void OnSuggestionPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control { DataContext: SearchSuggestionItem item })
            return;

        var point = e.GetCurrentPoint(this);

        if (point.Properties.IsLeftButtonPressed)
        {
            e.Handled = true;
            ((ICommand)item.Owner.SuggestionClickCommand).Execute(item.Text);
        }
        else if (point.Properties.IsRightButtonPressed)
        {
            e.Handled = true;
            ((ICommand)item.Owner.RemoveSuggestionCommand).Execute(item.Text);
        }
    }

    /// <summary>
    /// Гарантированный перехват контекстного меню чипа для удаления запроса из истории.
    /// </summary>
    private void OnSuggestionContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (sender is Control { DataContext: SearchSuggestionItem item })
        {
            e.Handled = true;
            ((ICommand)item.Owner.RemoveSuggestionCommand).Execute(item.Text);
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _cleanup?.Dispose();
        _cleanup = null;
        base.OnDetachedFromVisualTree(e);
    }
}