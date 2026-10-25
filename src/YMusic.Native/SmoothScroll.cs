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

    // Hermite interpolation preserves the visible velocity when another wheel
    // packet extends the target. Restarting ease-out with 3 * distance / duration
    // on every packet creates a new speed jump each time, even at a steady rate.
    internal static (double Offset, double Velocity) Interpolate(double start, double target,
        double initialVelocity, double progress)
    {
        var t = Math.Clamp(progress, 0, 1);
        var distance = target - start;
        var tangent = Math.Clamp(initialVelocity * DurationMilliseconds,
            Math.Min(0, 3 * distance), Math.Max(0, 3 * distance));
        var offset = start + distance * t * t * (3 - 2 * t) + tangent * t * (1 - t) * (1 - t);
        var velocity = (distance * 6 * t * (1 - t) + tangent * (1 - t) * (1 - 3 * t)) / DurationMilliseconds;
        return (offset, velocity);
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
            var offset = States.TryGetValue(viewer, out var state) && state.HasPendingOffset
                ? state.TargetOffset : viewer.VerticalOffset;
            // A clamped target can reach an edge before the visible viewport.
            // Keep owning wheel input until that last part of the motion arrives;
            // otherwise native scrolling consumes the next packet and snaps it.
            if (delta > 0 ? Math.Max(offset, viewer.VerticalOffset) > Epsilon
                : Math.Min(offset, viewer.VerticalOffset) < viewer.ScrollableHeight - Epsilon) return viewer;
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
        private bool _precisionPending;
        private double _precisionOrigin;
        private double _precisionRemainder;
        private long _started;
        private TimeSpan? _renderStarted;
        private TimeSpan? _lastRenderingTime;
        private double _initialVelocity;
        private double _velocity;
        internal bool IsAnimating { get; private set; }
        internal bool HasPendingOffset => IsAnimating || _precisionPending;
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
                !_viewer.IsLoaded || !_viewer.IsVisible || !_viewer.IsEnabled) return;
            var source = args.OriginalSource as DependencyObject ?? _viewer;
            if (NativeInputOwner(source) || !ReferenceEquals(WheelOwner(source, args.Delta), _viewer) ||
                !PixelScrolling(_viewer)) return;
            if (!Motion.CanAnimate && Math.Abs(args.Delta) >= 120) return;
            var lines = SystemParameters.WheelScrollLines;
            if (lines == 0) return;
            var distance = -args.Delta / 120d * (lines < 0 ? _viewer.ViewportHeight : lines * 16d);
            var current = _precisionPending ? TargetOffset : _viewer.VerticalOffset + _precisionRemainder;
            // Reverse immediately from the visible position instead of first paying
            // off an accumulated target in the old direction.
            var basis = IsAnimating && Math.Sign(distance) == Math.Sign(TargetOffset - current)
                ? TargetOffset : current;
            var target = Math.Clamp(basis + distance, 0, _viewer.ScrollableHeight);
            if (Math.Abs(target - current) <= Epsilon)
            {
                if (HasPendingOffset)
                {
                    // Reversal can return to the visible edge before the first
                    // frame has moved. Cancel the old inward target as well.
                    if (Math.Abs(target - TargetOffset) > Epsilon)
                    {
                        Cancel();
                        _viewer.ScrollToVerticalOffset(target);
                    }
                    args.Handled = true;
                }
                return;
            }
            // Extra packets towards an already clamped animated endpoint add no
            // distance. Consume them without restarting or speeding up the tail.
            if (IsAnimating && Math.Abs(target - TargetOffset) <= Epsilon &&
                Math.Sign(distance) == Math.Sign(TargetOffset - current))
            {
                args.Handled = true;
                return;
            }
            // High-resolution wheel packets already describe fine movement at
            // the device's cadence. Queue their exact pixel distance once instead
            // of turning each small packet into another 280 ms animation tail.
            if (Math.Abs(args.Delta) < 120)
            {
                var origin = _precisionPending ? _precisionOrigin : _viewer.VerticalOffset;
                Cancel();
                _precisionOrigin = origin;
                TargetOffset = Math.Clamp(current + distance, 0, _viewer.ScrollableHeight);
                _precisionPending = true;
                _viewer.ScrollToVerticalOffset(TargetOffset);
                args.Handled = true;
                return;
            }
            if (_precisionPending)
            {
                _requestedOffset = current;
                _scrollPending = true;
                _precisionPending = false;
            }
            var continuing = IsAnimating && Math.Sign(distance) == Math.Sign(TargetOffset - current);
            _initialVelocity = continuing ? _velocity : 3 * (target - current) / DurationMilliseconds;
            _startOffset = current;
            if (!IsAnimating && !_scrollPending) _requestedOffset = current;
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
            var current = _precisionPending ? TargetOffset : _viewer.VerticalOffset;
            if (Math.Abs(target - current) <= Epsilon) return;
            if (_precisionPending)
            {
                _requestedOffset = current;
                _scrollPending = true;
                _precisionPending = false;
            }
            _startOffset = current;
            _initialVelocity = IsAnimating && Math.Sign(target - current) == Math.Sign(TargetOffset - current)
                ? _velocity : 3 * (target - current) / DurationMilliseconds;
            if (!IsAnimating && !_scrollPending) _requestedOffset = current;
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
            (_requestedOffset, _velocity) = Interpolate(_startOffset, TargetOffset, _initialVelocity, progress);
            // Match layout's physical-pixel rounding before deciding whether a
            // command can change the viewport. A fixed 1-DIP cutoff suppresses
            // visible one-pixel frames on high-DPI displays.
            if (_viewer.UseLayoutRounding)
            {
                var scale = VisualTreeHelper.GetDpi(_viewer).DpiScaleY;
                _requestedOffset = Math.Clamp(Math.Round(_requestedOffset * scale) / scale, 0, _viewer.ScrollableHeight);
            }
            // The visible start or final partial row need not lie on a physical
            // pixel. Rounding must not move a first frame backwards, overshoot a
            // fractional target, or undo an already acknowledged queued frame.
            _requestedOffset = Math.Clamp(_requestedOffset,
                Math.Min(_viewer.VerticalOffset, TargetOffset), Math.Max(_viewer.VerticalOffset, TargetOffset));
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
            if (!ReferenceEquals(args.OriginalSource, _viewer)) return;
            if (!HasPendingOffset)
            {
                if (args.VerticalChange != 0) _precisionRemainder = 0;
                return;
            }
            if (args.ExtentHeightChange != 0 || args.ViewportHeightChange != 0)
            {
                Cancel();
                return;
            }
            if (args.VerticalChange == 0) return;
            if (_precisionPending)
            {
                if (Math.Abs(_viewer.VerticalOffset - TargetOffset) <= OffsetTolerance)
                {
                    // Carry the subpixel remainder across acknowledged packets;
                    // rounding each tiny delta independently doubles or loses
                    // distance on displays such as 125% scaling.
                    _precisionRemainder = TargetOffset - _viewer.VerticalOffset;
                    _precisionPending = false;
                }
                else if (_viewer.VerticalOffset < Math.Min(_precisionOrigin, TargetOffset) - OffsetTolerance ||
                    _viewer.VerticalOffset > Math.Max(_precisionOrigin, TargetOffset) + OffsetTolerance) Cancel();
                return;
            }
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
            _precisionPending = false;
            _precisionRemainder = 0;
            if (!IsAnimating) { _scrollPending = false; return; }
            IsAnimating = false;
            _scrollPending = false;
            _velocity = 0;
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
