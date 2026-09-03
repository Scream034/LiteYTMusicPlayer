using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using LMP.UI.Controls;

namespace LMP.UI.Features.Queue;

public partial class QueueView : UserControl
{
    public QueueView()
    {
        InitializeComponent();

        AttachedToVisualTree += (_, _) =>
        {
            var trackList = this.FindControl<TrackListControl>("QueueTrackList");
            if (trackList == null) return;

            if (!trackList.IsLoading)
            {
                Dispatcher.UIThread.Post(ScrollToPlayingTrack, DispatcherPriority.Loaded);
            }
            else
            {
                IDisposable? sub = null;
                sub = trackList.GetObservable(TrackListControl.IsLoadingProperty)
                    .Subscribe(isLoading =>
                    {
                        if (!isLoading)
                        {
                            sub?.Dispose();
                            Dispatcher.UIThread.Post(ScrollToPlayingTrack, DispatcherPriority.Loaded);
                        }
                    });
            }
        };
    }

    private void ScrollToPlayingTrack()
    {
        if (DataContext is not QueueViewModel vm) return;

        var trackList = this.FindControl<TrackListControl>("QueueTrackList");
        if (trackList == null) return;

        var items = vm.QueueItems;
        for (int i = 0; i < items.Count; i++)
        {
            if (items[i].IsActive)
            {
                trackList.ScrollToTrackIndex(i, smooth: false);
                break;
            }
        }
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }
}