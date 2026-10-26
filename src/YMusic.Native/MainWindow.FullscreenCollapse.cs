using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace YMusic.Native;

public partial class MainWindow
{
    private ExpandedCollapse? _expandedCollapse;

    private void RequestExpandedPlayerClose()
    {
        if (_expandedCollapse is not null || !IsExpandedPlayerOpen) return;
        if (_expandedFullscreenHost is not { } host || !IsVisible || !host.Window.IsVisible
            || WindowState == WindowState.Minimized || PresentationSource.FromVisual(host.Window) is null)
        { CloseExpandedPlayer(); return; }
        // Capture the destination now: the owner may have moved since opening.
        BeginExpandedCollapse(CaptureExpandedRevealOrigin());
    }

    // Same animation and cleanup as production, with a resolved local target
    // for never-shown fixtures; this path does not acquire or activate an HWND.
    private void BeginExpandedCollapseFromLocalCover(Rect bounds, ImageSource? image) =>
        BeginExpandedCollapse(image is null ? null : new(bounds, image, IsScreenCoordinates: false));

    private void BeginExpandedCollapse(ExpandedRevealOrigin? destination)
    {
        if (_expandedCollapse is not null || !IsExpandedPlayerOpen) return;
        if (!Motion.CanAnimate || _closed || _current is null || !ReferenceEquals(_expandedPlayerTrack, _current) || _expandedAccountGeneration != _accountGeneration
            || _expandedRevealStage is not { } stage || _expandedPlayerCoverButton is not { } cover
            || stage.ActualWidth <= 0 || stage.ActualHeight <= 0)
        { CloseExpandedPlayer(); return; }

        // Reversal may start halfway through opening. Preserve the composed
        // frame before removing its clocks, rather than flashing a fully open UI.
        var fromOpacity = stage.Opacity;
        var fromScaleX = _expandedReveal?.StageScale.ScaleX ?? 1;
        var fromScaleY = _expandedReveal?.StageScale.ScaleY ?? 1;
        var start = cover.TransformToAncestor(stage).TransformBounds(new Rect(cover.RenderSize));
        var image = _expandedPlayerCover?.Source ?? destination?.Image;
        if (_expandedReveal is { } reveal && reveal.Overlay.Children.Count > 0 && reveal.Overlay.Children[0] is Image openingGhost)
        {
            // Layout rounding can offset the arranged Canvas child from its
            // requested Left/Top. Preserve the actual rendered rectangle.
            start = openingGhost.TransformToAncestor(stage).TransformBounds(new Rect(openingGhost.RenderSize));
            image = openingGhost.Source;
        }
        CancelExpandedReveal();

        var target = destination?.ScreenBounds;
        if (destination is { IsScreenCoordinates: true })
        {
            if (PresentationSource.FromVisual(stage) is null) { CloseExpandedPlayer(); return; }
            target = new Rect(stage.PointFromScreen(target!.Value.TopLeft), stage.PointFromScreen(target.Value.BottomRight));
        }
        if (target is { } targetBounds && !ValidExpandedCollapseBounds(targetBounds)) { CloseExpandedPlayer(); return; }
        if (!ValidExpandedCollapseBounds(start)) { CloseExpandedPlayer(); return; }

        var collapse = _expandedCollapse = new ExpandedCollapse(stage, cover, _current, _accountGeneration);
        _fullscreenDisplayRequest?.SetRequired(false);
        collapse.StageScale.CenterX = stage.ActualWidth / 2; collapse.StageScale.CenterY = stage.ActualHeight / 2;
        collapse.StageScale.ScaleX = fromScaleX; collapse.StageScale.ScaleY = fromScaleY;
        stage.SetCurrentValue(RenderTransformProperty, collapse.Wrapper);
        // Keep interactions frozen while the closing frame is in flight.
        // Escape still routes at the host and repeated requests are idempotent.
        stage.SetCurrentValue(IsHitTestVisibleProperty, false);
        var duration = TimeSpan.FromMilliseconds(380);
        var ease = new CubicEase { EasingMode = EasingMode.EaseInOut };
        var timeline = new ParallelTimeline { Duration = duration, FillBehavior = FillBehavior.HoldEnd };
        timeline.Children.Add(RevealAnimation(fromOpacity, 0, duration, ease));
        timeline.Children.Add(RevealAnimation(fromScaleX, .985, duration, ease));
        timeline.Children.Add(RevealAnimation(fromScaleY, .985, duration, ease));
        if (target is { } end && image is not null)
        {
            // The whole stage shrinks slightly too. Resolve the cover endpoint
            // through that final scale so it lands on the owner's actual cover.
            end = new ScaleTransform(.985, .985, collapse.StageScale.CenterX, collapse.StageScale.CenterY).Inverse!.TransformBounds(end);
            var ghost = new Image { Name = "ExpandedCoverCollapse", Source = image, Width = start.Width, Height = start.Height,
                Stretch = Stretch.UniformToFill, IsHitTestVisible = false,
                Clip = new RectangleGeometry(new Rect(0, 0, start.Width, start.Height), 20, 20) };
            var transforms = new TransformGroup(); transforms.Children.Add(collapse.CoverScale); transforms.Children.Add(collapse.CoverTranslation);
            ghost.RenderTransform = transforms;
            Canvas.SetLeft(ghost, start.Left); Canvas.SetTop(ghost, start.Top); collapse.Overlay.Children.Add(ghost);
            Grid.SetRowSpan(collapse.Overlay, 3); stage.Children.Add(collapse.Overlay);
            cover.SetCurrentValue(OpacityProperty, 0d);
            timeline.Children.Add(RevealAnimation(1, end.Width / start.Width, duration, ease));
            timeline.Children.Add(RevealAnimation(1, end.Height / start.Height, duration, ease));
            timeline.Children.Add(RevealAnimation(0, end.Left - start.Left, duration, ease));
            timeline.Children.Add(RevealAnimation(0, end.Top - start.Top, duration, ease));
        }
        var clock = collapse.Completion = (ClockGroup)timeline.CreateClock(true);
        clock.Controller!.Stop();
        stage.ApplyAnimationClock(OpacityProperty, (AnimationClock)clock.Children[0]);
        collapse.StageScale.ApplyAnimationClock(ScaleTransform.ScaleXProperty, (AnimationClock)clock.Children[1]);
        collapse.StageScale.ApplyAnimationClock(ScaleTransform.ScaleYProperty, (AnimationClock)clock.Children[2]);
        if (clock.Children.Count > 3)
        {
            collapse.CoverScale.ApplyAnimationClock(ScaleTransform.ScaleXProperty, (AnimationClock)clock.Children[3]);
            collapse.CoverScale.ApplyAnimationClock(ScaleTransform.ScaleYProperty, (AnimationClock)clock.Children[4]);
            collapse.CoverTranslation.ApplyAnimationClock(TranslateTransform.XProperty, (AnimationClock)clock.Children[5]);
            collapse.CoverTranslation.ApplyAnimationClock(TranslateTransform.YProperty, (AnimationClock)clock.Children[6]);
        }
        clock.Completed += ExpandedCollapseCompleted;
        clock.Controller.Begin();
        clock.Controller.SeekAlignedToLastTick(TimeSpan.Zero, TimeSeekOrigin.BeginTime);
    }

    private static bool ValidExpandedCollapseBounds(Rect bounds) => !bounds.IsEmpty && bounds.Width > 0 && bounds.Height > 0
        && double.IsFinite(bounds.X) && double.IsFinite(bounds.Y) && double.IsFinite(bounds.Width) && double.IsFinite(bounds.Height);

    private void ExpandedCollapseCompleted(object? sender, EventArgs args)
    {
        if (_expandedCollapse is not { } collapse || !ReferenceEquals(sender, collapse.Completion)) return;
        CloseExpandedPlayer();
    }

    private void CancelExpandedCollapse()
    {
        if (_expandedCollapse is not { } collapse) return;
        _expandedCollapse = null;
        if (collapse.Completion is { } clock) { clock.Completed -= ExpandedCollapseCompleted; clock.Controller?.Stop(); }
        collapse.Stage.ApplyAnimationClock(OpacityProperty, null);
        collapse.StageScale.BeginAnimation(ScaleTransform.ScaleXProperty, null); collapse.StageScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        collapse.CoverScale.BeginAnimation(ScaleTransform.ScaleXProperty, null); collapse.CoverScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        collapse.CoverTranslation.BeginAnimation(TranslateTransform.XProperty, null); collapse.CoverTranslation.BeginAnimation(TranslateTransform.YProperty, null);
        if (ReferenceEquals(collapse.Stage.RenderTransform, collapse.Wrapper)) collapse.Stage.SetCurrentValue(RenderTransformProperty, collapse.OriginalTransform);
        collapse.Stage.SetCurrentValue(OpacityProperty, collapse.OriginalOpacity);
        collapse.Stage.SetCurrentValue(IsHitTestVisibleProperty, collapse.OriginalHitTestVisible);
        collapse.Cover.SetCurrentValue(OpacityProperty, collapse.OriginalCoverOpacity);
        foreach (Image ghost in collapse.Overlay.Children) ghost.Source = null;
        collapse.Overlay.Children.Clear(); collapse.Stage.Children.Remove(collapse.Overlay);
    }

    private sealed class ExpandedCollapse(Grid stage, Button cover, TrackRow track, int account)
    {
        public Grid Stage { get; } = stage;
        public Button Cover { get; } = cover;
        public TrackRow Track { get; } = track;
        public int Account { get; } = account;
        public Transform OriginalTransform { get; } = stage.RenderTransform;
        public double OriginalOpacity { get; } = stage.Opacity;
        public double OriginalCoverOpacity { get; } = cover.Opacity;
        public bool OriginalHitTestVisible { get; } = stage.IsHitTestVisible;
        public ScaleTransform StageScale { get; } = new();
        public ScaleTransform CoverScale { get; } = new();
        public TranslateTransform CoverTranslation { get; } = new();
        // Animated coordinates must not snap again when the opening ghost is
        // replaced; otherwise a fractional arranged origin visibly jumps.
        public Canvas Overlay { get; } = new() { IsHitTestVisible = false, ClipToBounds = true, UseLayoutRounding = false };
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
