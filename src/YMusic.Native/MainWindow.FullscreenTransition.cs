using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace YMusic.Native;

public partial class MainWindow
{
    private Grid? _expandedRevealStage;
    private ExpandedReveal? _expandedReveal;
    private DispatcherOperation? _expandedRevealPending;
    private long _expandedRevealGeneration;
    private sealed record ExpandedRevealOrigin(Rect ScreenBounds, ImageSource Image, bool IsScreenCoordinates = true);

    private ExpandedRevealOrigin? CaptureExpandedRevealOrigin()
    {
        if (!Motion.CanAnimate || WindowState == WindowState.Minimized || PlayerCover.Source is not { } image
            || !PlayerCover.IsVisible || PresentationSource.FromVisual(PlayerCover) is null || PlayerCover.ActualWidth <= 0 || PlayerCover.ActualHeight <= 0) return null;
        return new(new Rect(PlayerCover.PointToScreen(new Point()), PlayerCover.PointToScreen(new Point(PlayerCover.ActualWidth, PlayerCover.ActualHeight))), image);
    }

    private void BeginExpandedReveal(ExpandedRevealOrigin? origin)
    {
        PrepareExpandedReveal(origin);
        BeginPreparedExpandedReveal();
    }

    // An embedded player can supply an already-resolved local rectangle without
    // an HWND. The normal account/track/lifetime guards remain in the common path.
    private void BeginExpandedRevealFromLocalCover(Rect sourceBounds, ImageSource image) =>
        BeginExpandedReveal(new(sourceBounds, image, IsScreenCoordinates: false));

    private void PrepareExpandedReveal(ExpandedRevealOrigin? origin)
    {
        CancelExpandedReveal();
        if (!Motion.CanAnimate || _closed || _current is null || _expandedPlayerSurface is null || _expandedRevealStage is null || _expandedPlayerCoverButton is null) return;
        var reveal = _expandedReveal = new ExpandedReveal(_expandedRevealStage, _expandedPlayerCoverButton, origin, _current, _accountGeneration, _expandedRevealGeneration);
        // Prepare while collapsed, before changing native state/style. Ambient,
        // opaque background and controls enter as one composited layer.
        reveal.Stage.SetCurrentValue(OpacityProperty, 0d);
        reveal.Stage.SetCurrentValue(RenderTransformProperty, reveal.Wrapper);
        if (origin is not null) reveal.Cover.SetCurrentValue(OpacityProperty, 0d);
    }

    private void BeginPreparedExpandedReveal()
    {
        if (_expandedReveal is not { } reveal) return;
        _expandedRevealPending = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            _expandedRevealPending = null;
            if (!ExpandedRevealStillCurrent(reveal)) { if (ReferenceEquals(_expandedReveal, reveal)) CancelExpandedReveal(); return; }
            reveal.Stage.UpdateLayout();
            if (!ExpandedRevealStillCurrent(reveal) || reveal.Stage.ActualWidth <= 0 || reveal.Stage.ActualHeight <= 0) { CancelExpandedReveal(); return; }
            var destination = reveal.Cover.TransformToAncestor(reveal.Stage).TransformBounds(new Rect(reveal.Cover.RenderSize));
            if (destination.Width <= 0 || destination.Height <= 0) { CancelExpandedReveal(); return; }
            reveal.StageScale.CenterX = reveal.Stage.ActualWidth / 2; reveal.StageScale.CenterY = reveal.Stage.ActualHeight / 2;

            var duration = TimeSpan.FromMilliseconds(380);
            var ease = new CubicEase { EasingMode = EasingMode.EaseInOut };
            var timeline = new ParallelTimeline { Duration = duration, FillBehavior = FillBehavior.HoldEnd };
            timeline.Children.Add(RevealAnimation(0, reveal.OriginalOpacity, duration, ease));
            timeline.Children.Add(RevealAnimation(.985, 1, duration, ease));
            timeline.Children.Add(RevealAnimation(.985, 1, duration, ease));
            if (ExpandedCoverStart(reveal) is { } start)
            {
                var ghost = new Image { Name = "ExpandedCoverTransition", Source = reveal.Origin!.Image, Width = destination.Width, Height = destination.Height,
                    Stretch = Stretch.UniformToFill, IsHitTestVisible = false, Clip = new RectangleGeometry(new Rect(0, 0, destination.Width, destination.Height), 20, 20) };
                var transforms = new TransformGroup(); transforms.Children.Add(reveal.CoverScale); transforms.Children.Add(reveal.CoverTranslation); ghost.RenderTransform = transforms;
                Canvas.SetLeft(ghost, destination.Left); Canvas.SetTop(ghost, destination.Top); reveal.Overlay.Children.Add(ghost);
                Grid.SetRowSpan(reveal.Overlay, 3); reveal.Stage.Children.Add(reveal.Overlay);
                timeline.Children.Add(RevealAnimation(start.Width / destination.Width, 1, duration, ease));
                timeline.Children.Add(RevealAnimation(start.Height / destination.Height, 1, duration, ease));
                timeline.Children.Add(RevealAnimation(start.Left - destination.Left, 0, duration, ease));
                timeline.Children.Add(RevealAnimation(start.Top - destination.Top, 0, duration, ease));
            }
            else reveal.Cover.SetCurrentValue(OpacityProperty, reveal.OriginalCoverOpacity);

            // One clock group starts background, controls and shared-image cover
            // together. There is no per-frame layout or native-window callback.
            var clock = reveal.Completion = (ClockGroup)timeline.CreateClock(true);
            clock.Controller!.Stop();
            reveal.Stage.SetCurrentValue(OpacityProperty, reveal.OriginalOpacity);
            reveal.StageScale.ScaleX = reveal.StageScale.ScaleY = 1;
            reveal.Stage.ApplyAnimationClock(OpacityProperty, (AnimationClock)clock.Children[0]);
            reveal.StageScale.ApplyAnimationClock(ScaleTransform.ScaleXProperty, (AnimationClock)clock.Children[1]);
            reveal.StageScale.ApplyAnimationClock(ScaleTransform.ScaleYProperty, (AnimationClock)clock.Children[2]);
            if (clock.Children.Count > 3)
            {
                reveal.CoverScale.ApplyAnimationClock(ScaleTransform.ScaleXProperty, (AnimationClock)clock.Children[3]);
                reveal.CoverScale.ApplyAnimationClock(ScaleTransform.ScaleYProperty, (AnimationClock)clock.Children[4]);
                reveal.CoverTranslation.ApplyAnimationClock(TranslateTransform.XProperty, (AnimationClock)clock.Children[5]);
                reveal.CoverTranslation.ApplyAnimationClock(TranslateTransform.YProperty, (AnimationClock)clock.Children[6]);
            }
            clock.Completed += ExpandedRevealCompleted;
            clock.Controller.Begin();
            clock.Controller.SeekAlignedToLastTick(TimeSpan.Zero, TimeSeekOrigin.BeginTime);
        }));
    }

    private bool ExpandedRevealStillCurrent(ExpandedReveal reveal) => !_closed && IsExpandedPlayerOpen
        && reveal.Generation == _expandedRevealGeneration && reveal.Account == _accountGeneration && ReferenceEquals(_expandedReveal, reveal) && ReferenceEquals(_current, reveal.Track);

    private static DoubleAnimation RevealAnimation(double from, double to, TimeSpan duration, IEasingFunction ease) =>
        new(from, to, duration) { EasingFunction = ease, FillBehavior = FillBehavior.HoldEnd };

    private static Rect? ExpandedCoverStart(ExpandedReveal reveal)
    {
        if (reveal.Origin is not { } origin) return null;
        var bounds = origin.ScreenBounds;
        if (origin.IsScreenCoordinates)
        {
            if (PresentationSource.FromVisual(reveal.Stage) is null) return null;
            // Physical coordinates include negative monitor origins; WPF uses
            // the destination HWND's current DPI for this one conversion.
            bounds = new Rect(reveal.Stage.PointFromScreen(bounds.TopLeft), reveal.Stage.PointFromScreen(bounds.BottomRight));
        }
        return bounds.Width > 0 && bounds.Height > 0 && double.IsFinite(bounds.X) && double.IsFinite(bounds.Y)
            && double.IsFinite(bounds.Width) && double.IsFinite(bounds.Height) ? bounds : null;
    }

    private void ExpandedRevealCompleted(object? sender, EventArgs args) => CancelExpandedReveal();

    private void ExpandedPlayerClosed(object? sender, EventArgs args)
    {
        Closed -= ExpandedPlayerClosed;
        CancelExpandedCollapse(); CancelExpandedReveal(); CloseExpandedFullscreenHost(); ClearExpandedQueue();
        ExpandedPlayerHost.Visibility = Visibility.Collapsed;
        _expandedQueueOpen = false; _expandedWindowPlacement = null; _expandedPreviousFocus = null;
        foreach (var panel in new[] { _expandedLyricsPanel, _expandedQueuePanel })
        { panel?.BeginAnimation(WidthProperty, null); panel?.BeginAnimation(OpacityProperty, null); }
    }

    private void CancelExpandedReveal()
    {
        _expandedRevealGeneration++;
        if (_expandedRevealPending is { Status: DispatcherOperationStatus.Pending } pending) pending.Abort();
        _expandedRevealPending = null;
        if (_expandedReveal is not { } reveal) return;
        _expandedReveal = null;
        if (reveal.Completion is not null) { reveal.Completion.Completed -= ExpandedRevealCompleted; reveal.Completion.Controller?.Stop(); }
        reveal.Stage.ApplyAnimationClock(OpacityProperty, null);
        reveal.StageScale.BeginAnimation(ScaleTransform.ScaleXProperty, null); reveal.StageScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        reveal.CoverScale.BeginAnimation(ScaleTransform.ScaleXProperty, null); reveal.CoverScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        reveal.CoverTranslation.BeginAnimation(TranslateTransform.XProperty, null); reveal.CoverTranslation.BeginAnimation(TranslateTransform.YProperty, null);
        if (ReferenceEquals(reveal.Stage.RenderTransform, reveal.Wrapper)) reveal.Stage.SetCurrentValue(RenderTransformProperty, reveal.OriginalTransform);
        reveal.Stage.SetCurrentValue(OpacityProperty, reveal.OriginalOpacity); reveal.Cover.SetCurrentValue(OpacityProperty, reveal.OriginalCoverOpacity);
        foreach (Image image in reveal.Overlay.Children) image.Source = null;
        reveal.Overlay.Children.Clear(); reveal.Stage.Children.Remove(reveal.Overlay);
    }

    private sealed class ExpandedReveal(Grid stage, Button cover, ExpandedRevealOrigin? origin, TrackRow track, int account, long generation)
    {
        public Grid Stage { get; } = stage;
        public Button Cover { get; } = cover;
        public ExpandedRevealOrigin? Origin { get; } = origin;
        public TrackRow Track { get; } = track;
        public int Account { get; } = account;
        public long Generation { get; } = generation;
        public Transform OriginalTransform { get; } = stage.RenderTransform;
        public double OriginalOpacity { get; } = stage.Opacity;
        public double OriginalCoverOpacity { get; } = cover.Opacity;
        public ScaleTransform StageScale { get; } = new(.985, .985);
        public ScaleTransform CoverScale { get; } = new();
        public TranslateTransform CoverTranslation { get; } = new();
        public Canvas Overlay { get; } = new() { IsHitTestVisible = false, ClipToBounds = true };
        public ClockGroup? Completion { get; set; }
        private TransformGroup? _wrapper;
        public TransformGroup Wrapper
        {
            get
            {
                if (_wrapper is not null) return _wrapper;
                _wrapper = new(); if (OriginalTransform is not null) _wrapper.Children.Add(OriginalTransform); _wrapper.Children.Add(StageScale); return _wrapper;
            }
        }
    }
}
