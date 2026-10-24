using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace YMusic.Native;

/// <summary>Event-driven pixel wheel scrolling; native selection, keys and scrollbars keep control.</summary>
public static class SmoothScroll
{
    private const double DurationMilliseconds = 280;
    private const double Epsilon = 0.1;
    private const double OffsetTolerance = 1;
    private static readonly ConditionalWeakTable<ScrollViewer, ScrollState> States = new();

    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached(
        "Enabled", typeof(bool), typeof(SmoothScroll),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.Inherits, OnEnabledChanged));

    public static void SetEnabled(DependencyObject target, bool value) => target.SetValue(EnabledProperty, value);
    public static bool GetEnabled(DependencyObject target) => (bool)target.GetValue(EnabledProperty);

    internal static bool IsAnimating(ScrollViewer viewer) =>
        States.TryGetValue(viewer, out var state) && state.IsAnimating;

    internal static void ScrollTo(ScrollViewer viewer, double offset)
    {
        if (!double.IsFinite(offset)) return;
        offset = Math.Clamp(offset, 0, viewer.ScrollableHeight);
        if (GetEnabled(viewer) && Motion.CanAnimate && viewer.IsLoaded && viewer.IsVisible && viewer.IsEnabled
            && PixelScrolling(viewer) && States.TryGetValue(viewer, out var state)) state.AnimateTo(offset);
        else viewer.ScrollToVerticalOffset(offset);
    }

    internal static void CancelAnimation(ScrollViewer viewer)
    {
        if (States.TryGetValue(viewer, out var state)) state.Stop();
    }

    private static void OnEnabledChanged(DependencyObject target, DependencyPropertyChangedEventArgs args)
    {
        if (target is not ScrollViewer viewer) return;
        if (States.TryGetValue(viewer, out var previous))
        {
            previous.Dispose();
            States.Remove(viewer);
        }
        if ((bool)args.NewValue) States.Add(viewer, new ScrollState(viewer));
    }

    private static DependencyObject? Parent(DependencyObject value)
    {
        if (value is Visual or Visual3D)
        {
            var visualParent = VisualTreeHelper.GetParent(value);
            if (visualParent is not null) return visualParent;
        }
        return value is FrameworkContentElement content ? content.Parent
            : value is FrameworkElement element ? element.Parent ?? element.TemplatedParent
            : value is ContentElement contentElement ? ContentOperations.GetParent(contentElement) : null;
    }

    private static bool NativeInputOwner(DependencyObject source)
    {
        for (DependencyObject? current = source; current is not null; current = Parent(current))
        {
            if (current is TextBoxBase or PasswordBox or ComboBox) return true;
            if (current is FrameworkElement element &&
                (element.Name == "PART_ContentHost" || element.TemplatedParent is TextBoxBase or PasswordBox or ComboBox))
                return true;
        }
        return false;
    }

    // An outer viewer receives PreviewMouseWheel first. Pick the deepest viewer that
    // can still move in this direction, so nested boundaries continue to the parent.
    private static ScrollViewer? WheelOwner(DependencyObject source, int delta)
    {
        for (DependencyObject? current = source; current is not null; current = Parent(current))
        {
            if (current is not ScrollViewer viewer || viewer.ScrollableHeight <= Epsilon) continue;
            var offset = States.TryGetValue(viewer, out var state) && state.IsAnimating
                ? state.TargetOffset : viewer.VerticalOffset;
            if (delta > 0 ? offset > Epsilon : offset < viewer.ScrollableHeight - Epsilon) return viewer;
        }
        return null;
    }

    private static bool PixelScrolling(ScrollViewer viewer)
    {
        if (!viewer.CanContentScroll) return true;
        var info = ScrollInfo(viewer.Content as DependencyObject);
        // With no delegated IScrollInfo the ScrollContentPresenter uses DIP offsets.
        if (info is null || info is ScrollContentPresenter) return true;
        if (info is not VirtualizingStackPanel panel) return false;
        var owner = ItemsControl.GetItemsOwner(panel);
        return owner is not null && VirtualizingPanel.GetScrollUnit(owner) == ScrollUnit.Pixel;
    }

    private static IScrollInfo? ScrollInfo(DependencyObject? value)
    {
        if (value is null || value is ScrollViewer) return null;
        if (value is IScrollInfo info) return info;
        if (value is not Visual and not Visual3D) return null;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(value); index++)
            if (ScrollInfo(VisualTreeHelper.GetChild(value, index)) is { } child) return child;
        return null;
    }

    private sealed class ScrollState : IDisposable
    {
        private readonly ScrollViewer _viewer;
        private readonly MouseWheelEventHandler _wheelHandler;
        private readonly MouseButtonEventHandler _mouseHandler;
        private readonly KeyEventHandler _keyHandler;
        private readonly DragStartedEventHandler _dragHandler;
        private readonly ScrollEventHandler _scrollHandler;
        private double _startOffset;
        private double _requestedOffset;
        private bool _scrollPending;
        private long _started;
        private TimeSpan? _renderStarted;
        private TimeSpan? _lastRenderingTime;
        internal bool IsAnimating { get; private set; }
        internal double TargetOffset { get; private set; }

        internal ScrollState(ScrollViewer viewer)
        {
            _viewer = viewer;
            _wheelHandler = OnWheel;
            _mouseHandler = (_, _) => Cancel();
            _keyHandler = (_, _) => Cancel();
            _dragHandler = (_, _) => Cancel();
            _scrollHandler = (_, _) => Cancel();
            viewer.AddHandler(Mouse.PreviewMouseWheelEvent, _wheelHandler);
            viewer.AddHandler(Mouse.PreviewMouseDownEvent, _mouseHandler, true);
            viewer.AddHandler(Keyboard.PreviewKeyDownEvent, _keyHandler, true);
            viewer.AddHandler(Thumb.DragStartedEvent, _dragHandler, true);
            viewer.AddHandler(ScrollBar.ScrollEvent, _scrollHandler, true);
            viewer.ScrollChanged += OnScrollChanged;
            viewer.Unloaded += OnUnloaded;
            viewer.IsVisibleChanged += OnVisibilityChanged;
            viewer.IsEnabledChanged += OnIsEnabledChanged;
        }

        private void OnWheel(object sender, MouseWheelEventArgs args)
        {
            if (args.Handled || args.Delta == 0 || Keyboard.Modifiers != ModifierKeys.None ||
                !_viewer.IsLoaded || !_viewer.IsVisible || !_viewer.IsEnabled || !Motion.CanAnimate) return;
            var source = args.OriginalSource as DependencyObject ?? _viewer;
            if (NativeInputOwner(source) || !ReferenceEquals(WheelOwner(source, args.Delta), _viewer) ||
                !PixelScrolling(_viewer)) return;
            var lines = SystemParameters.WheelScrollLines;
            if (lines == 0) return;
            var distance = -args.Delta / 120d * (lines < 0 ? _viewer.ViewportHeight : lines * 16d);
            var current = _viewer.VerticalOffset;
            // Reverse immediately from the visible position instead of first paying
            // off an accumulated target in the old direction.
            var basis = IsAnimating && Math.Sign(distance) == Math.Sign(TargetOffset - current)
                ? TargetOffset : current;
            var target = Math.Clamp(basis + distance, 0, _viewer.ScrollableHeight);
            if (Math.Abs(target - current) <= Epsilon) return;
            _startOffset = current;
            if (!IsAnimating) _requestedOffset = current;
            TargetOffset = target;
            _started = Stopwatch.GetTimestamp();
            _renderStarted = null;
            if (!IsAnimating)
            {
                IsAnimating = true;
                CompositionTarget.Rendering += OnRendering;
            }
            args.Handled = true;
        }

        internal void AnimateTo(double target)
        {
            var current = _viewer.VerticalOffset;
            if (Math.Abs(target - current) <= Epsilon) return;
            _startOffset = current;
            if (!IsAnimating) _requestedOffset = current;
            TargetOffset = target;
            _started = Stopwatch.GetTimestamp();
            _renderStarted = null;
            if (!IsAnimating) { IsAnimating = true; CompositionTarget.Rendering += OnRendering; }
        }

        internal void Stop() => Cancel();

        private void OnRendering(object? sender, EventArgs args)
        {
            if (!Motion.CanAnimate || !_viewer.IsVisible || !_viewer.IsLoaded || !_viewer.IsEnabled)
            {
                Cancel();
                return;
            }
            // Layout caused by ScrollTo can raise Rendering again for the same
            // composition frame. Do not advance or lay out the list twice in it.
            var elapsedMilliseconds = Stopwatch.GetElapsedTime(_started).TotalMilliseconds;
            if (args is RenderingEventArgs rendering)
            {
                if (_lastRenderingTime == rendering.RenderingTime) return;
                _lastRenderingTime = rendering.RenderingTime;
                _renderStarted ??= rendering.RenderingTime - TimeSpan.FromMilliseconds(elapsedMilliseconds);
                elapsedMilliseconds = (rendering.RenderingTime - _renderStarted.Value).TotalMilliseconds;
            }
            // WPF applies ScrollToVerticalOffset through its dispatcher queue. Keep
            // one command in flight, so an older ScrollChanged cannot be compared
            // against a newer frame's request and mistaken for external scrolling.
            if (_scrollPending) return;
            var elapsed = elapsedMilliseconds / DurationMilliseconds;
            var progress = Math.Clamp(elapsed, 0, 1);
            var eased = 1 - Math.Pow(1 - progress, 3);
            _requestedOffset = _startOffset + (TargetOffset - _startOffset) * eased;
            // Match layout's physical-pixel rounding before deciding whether a
            // command can change the viewport. A fixed 1-DIP cutoff suppresses
            // visible one-pixel frames on high-DPI displays.
            if (_viewer.UseLayoutRounding)
            {
                var scale = VisualTreeHelper.GetDpi(_viewer).DpiScaleY;
                _requestedOffset = Math.Clamp(Math.Round(_requestedOffset * scale) / scale, 0, _viewer.ScrollableHeight);
            }
            // An unchanged pixel produces no ScrollChanged acknowledgement.
            if (Math.Abs(_requestedOffset - _viewer.VerticalOffset) <= Epsilon)
            {
                if (progress >= 1)
                {
                    _viewer.ScrollToVerticalOffset(TargetOffset);
                    Cancel();
                }
                return;
            }
            _scrollPending = true;
            _viewer.ScrollToVerticalOffset(_requestedOffset);
        }

        private void OnScrollChanged(object sender, ScrollChangedEventArgs args)
        {
            if (!IsAnimating || !ReferenceEquals(args.OriginalSource, _viewer)) return;
            if (args.ExtentHeightChange != 0 || args.ViewportHeightChange != 0)
            {
                Cancel();
                return;
            }
            if (args.VerticalChange == 0) return;
            if (_scrollPending && Math.Abs(_viewer.VerticalOffset - _requestedOffset) <= OffsetTolerance)
                _scrollPending = false;
            else Cancel(); // A native key, scrollbar or external ScrollTo owns this move.
        }

        private void OnUnloaded(object sender, RoutedEventArgs args) => Cancel();
        private void OnVisibilityChanged(object sender, DependencyPropertyChangedEventArgs args)
        {
            if (!(bool)args.NewValue) Cancel();
        }
        private void OnIsEnabledChanged(object sender, DependencyPropertyChangedEventArgs args)
        {
            if (!(bool)args.NewValue) Cancel();
        }

        private void Cancel()
        {
            if (!IsAnimating) return;
            IsAnimating = false;
            _scrollPending = false;
            CompositionTarget.Rendering -= OnRendering;
        }

        public void Dispose()
        {
            Cancel();
            _viewer.RemoveHandler(Mouse.PreviewMouseWheelEvent, _wheelHandler);
            _viewer.RemoveHandler(Mouse.PreviewMouseDownEvent, _mouseHandler);
            _viewer.RemoveHandler(Keyboard.PreviewKeyDownEvent, _keyHandler);
            _viewer.RemoveHandler(Thumb.DragStartedEvent, _dragHandler);
            _viewer.RemoveHandler(ScrollBar.ScrollEvent, _scrollHandler);
            _viewer.ScrollChanged -= OnScrollChanged;
            _viewer.Unloaded -= OnUnloaded;
            _viewer.IsVisibleChanged -= OnVisibilityChanged;
            _viewer.IsEnabledChanged -= OnIsEnabledChanged;
        }
    }
}
