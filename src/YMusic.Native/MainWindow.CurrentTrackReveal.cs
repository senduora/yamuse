using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;

namespace YMusic.Native;

public partial class MainWindow
{
    private long _currentTrackRevealRequest;

    // Explicit navigation only. Resolve the index again after layout so a Next/
    // Previous commit between the click and the dispatcher uses the live cursor.
    private void RequestCurrentTrackReveal(ListBox list, Func<int> currentIndex, Func<bool> valid, Action? revealed = null)
    {
        var request = ++_currentTrackRevealRequest;
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
            if (final) revealed?.Invoke();
        }
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => Apply(false)));
        // Runs after Browse's stored-offset callback and WPF's deferred measure.
        // This bounded second pass also handles a cursor changed during layout.
        Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() => Apply(true)));
    }

    private static VirtualizingPanel? CurrentTrackItemsHost(DependencyObject root)
    {
        if (root is VirtualizingPanel { IsItemsHost: true } panel) return panel;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            if (CurrentTrackItemsHost(VisualTreeHelper.GetChild(root, i)) is { } found) return found;
        return null;
    }
}
