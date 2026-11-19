using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace YMusic.Native;

public partial class MainWindow
{
    private long _currentTrackRevealRequest;
    private ListBox? _currentTrackRevealOwner;
    private bool _trackRevealInputAttached;

    // Explicit navigation only. Resolve the index again after layout so a Next/
    // Previous commit between the click and the dispatcher uses the live cursor.
    private void RequestCurrentTrackReveal(ListBox list, Func<int> currentIndex, Func<bool> valid, Action? revealed = null)
    {
        if (ReferenceEquals(list, TracksList)) ObserveTrackRevealInput(list);
        var request = ++_currentTrackRevealRequest;
        _currentTrackRevealOwner = list;
        var account = _accountGeneration; var uid = _account?.Uid;
        void Apply(bool final)
        {
            if (_closed || request != _currentTrackRevealRequest || account != _accountGeneration
                || uid != _account?.Uid || !valid()) return;
            var index = currentIndex();
            if (index < 0 || index >= list.Items.Count) return;
            list.UpdateLayout();
            if (list.ActualWidth <= 0 || list.ActualHeight <= 0) return;
            var viewer = FindViewer(list);
            if (viewer is not null) SmoothScroll.CancelAnimation(viewer);
            // ScrollIntoView(item) finds the first equal item. The items-host
            // index preserves a repeated reference and realizes only its area.
            if (CurrentTrackItemsHost(list) is { } host) host.BringIndexIntoViewPublic(index);
            list.UpdateLayout();
            if (list.ItemContainerGenerator.ContainerFromIndex(index) is not FrameworkElement row) return;
            row.BringIntoView();
            list.UpdateLayout();
            if (VirtualizingPanel.GetScrollUnit(list) == ScrollUnit.Pixel
                && viewer?.Template?.FindName("PART_ScrollContentPresenter", viewer) is ScrollContentPresenter viewport
                && row.IsDescendantOf(viewport))
            {
                var bounds = row.TransformToAncestor(viewport).TransformBounds(new Rect(row.RenderSize));
                viewer.ScrollToVerticalOffset(viewer.VerticalOffset + bounds.Top - (viewport.ActualHeight - bounds.Height) / 2);
                list.UpdateLayout();
            }
            if (final && request == _currentTrackRevealRequest)
            {
                _currentTrackRevealOwner = null;
                revealed?.Invoke();
            }
        }
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => Apply(false)));
        // Runs after Browse's stored-offset callback and WPF's deferred measure.
        // This bounded second pass also handles a cursor changed during layout.
        Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() => Apply(true)));
    }

    private void ObserveTrackRevealInput(ListBox list)
    {
        if (_trackRevealInputAttached) return;
        _trackRevealInputAttached = true;
        list.AddHandler(Mouse.PreviewMouseWheelEvent, new MouseWheelEventHandler((_, _) => Cancel()), true);
        list.AddHandler(Mouse.PreviewMouseDownEvent, new MouseButtonEventHandler((_, _) => Cancel()), true);
        list.AddHandler(Keyboard.PreviewKeyDownEvent, new KeyEventHandler((_, args) =>
        {
            if (IsTrackRevealNavigationKey(args.Key)) Cancel();
        }), true);

        void Cancel()
        {
            if (!ReferenceEquals(_currentTrackRevealOwner, list)) return;
            _currentTrackRevealOwner = null;
            _currentTrackRevealRequest++;
        }
    }

    private static bool IsTrackRevealNavigationKey(Key key) => key is Key.Up or Key.Down or Key.Left or Key.Right
        or Key.PageUp or Key.PageDown or Key.Home or Key.End;

    private static VirtualizingPanel? CurrentTrackItemsHost(DependencyObject root)
    {
        if (root is VirtualizingPanel { IsItemsHost: true } panel) return panel;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            if (CurrentTrackItemsHost(VisualTreeHelper.GetChild(root, i)) is { } found) return found;
        return null;
    }
}
