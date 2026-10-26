using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using YMusic.Native;

internal static partial class Program
{
    private static async Task CheckFullscreenCollapse040()
    {
        await Run("FullscreenCollapse040: shared finite reverse frames and current cover endpoint", Fc040Frames);
        await Run("FullscreenCollapse040: reverse from an opening frame without a full-opacity flash", Fc040ReverseOpening);
        await Run("FullscreenCollapse040: repeated close reopen track account lifetime and power cleanup", Fc040Lifetime);
        await Run("FullscreenCollapse040: immediate API missing HWND reduced motion artwork and zero bounds", Fc040Fallbacks);
        File.WriteAllText(Path.Combine(_directory, "fullscreen-collapse040-scope.json"), JsonSerializer.Serialize(new
        {
            fixtureWindowsShown = false, nativeHandlesCreated = false, desktopInputUsed = false,
            accountLoaded = false, networkRequests = 0, audioPlayed = false,
            animationAssertionsRun = Motion.CanAnimate, durationMilliseconds = 380,
            screenTargetUsesProductionPointFromScreen = true, physicalMixedDpiMonitorExercised = false
        }, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static async Task Fc040Frames()
    {
        using var f = new Fr0317Fixture("collapse-frames"); f.Open();
        var w = f.Window; var stage = Fr0317Get<Grid>(w, "_expandedRevealStage");
        var surface = Fr0317Get<Grid>(w, "_expandedPlayerSurface");
        var cover = Fr0317Get<Button>(w, "_expandedPlayerCoverButton");
        var original = stage.RenderTransform; var before = Fh040Placement(w);
        // Deliberately differs from the opening origin, as after an owner move.
        var destination = new Rect(f.SourceBounds.X + 137, f.SourceBounds.Y - 51, 71, 71);
        Fc040Begin(f, destination);
        if (!Motion.CanAnimate) { Fc040Clean(f, original); return; }
        var collapse = Fr0317Get<object>(w, "_expandedCollapse"); var clock = Fc040Clock(collapse);
        clock.Controller!.Pause();
        Assert(clock.Children.Count == 7 && clock.Timeline.Duration.TimeSpan == TimeSpan.FromMilliseconds(380), "collapse does not use one finite shared clock");
        var samples = new List<object>(); var previous = 2d;
        foreach (var ms in new[] { 0, 80, 190, 379 })
        {
            clock.Controller.SeekAlignedToLastTick(TimeSpan.FromMilliseconds(ms), TimeSeekOrigin.BeginTime); f.Layout();
            var ghost = Fr0317Visuals<Image>(stage).Single(image => image.Name == "ExpandedCoverCollapse");
            var ambient = Fr0317Get<AmbientBackdrop>(w, "_expandedAmbient");
            Assert(stage.Opacity <= previous && stage.Opacity > 0 && cover.Opacity == 0 && !stage.IsHitTestVisible,
                "collapse faded in, exposed a duplicate cover or accepted a stale click at " + ms);
            Assert(Math.Abs(stage.Opacity * surface.Opacity - stage.Opacity * ambient.Opacity) < .001,
                "controls and ambient do not share the closing phase");
            Assert(ReferenceEquals(ghost.Source, f.Artwork) && Fh040Placement(w) == before, "closing cloned artwork or changed the owner");
            var rect = ghost.TransformToAncestor(stage).TransformBounds(new Rect(ghost.RenderSize));
            if (ms == 379)
            {
                var wrapper = (TransformGroup)stage.RenderTransform;
                var final = wrapper.TransformBounds(rect);
                Assert(Math.Abs(final.Left - destination.Left) < .01 && Math.Abs(final.Top - destination.Top) < .01
                    && Math.Abs(final.Width - destination.Width) < .01 && Math.Abs(final.Height - destination.Height) < .01,
                    "cover returns to the stale opening location or ignores whole-stage scale");
            }
            samples.Add(new { milliseconds = ms, opacity = stage.Opacity, ghostBounds = rect.ToString() });
            previous = stage.Opacity;
        }
        Assert(previous < .001, "closing surface does not become transparent");
        clock.Controller.SeekAlignedToLastTick(TimeSpan.FromMilliseconds(380), TimeSeekOrigin.BeginTime);
        await Dispatcher.Yield(DispatcherPriority.Background);
        Fc040Clean(f, original);
        Assert(Fh040Placement(w) == before, "completed collapse changed owner placement");
        File.WriteAllText(Path.Combine(_directory, "fullscreen-collapse040-frames.json"), JsonSerializer.Serialize(samples, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static async Task Fc040ReverseOpening()
    {
        if (!Motion.CanAnimate) return;
        using var f = new Fr0317Fixture("collapse-reverse-opening"); f.Open();
        var w = f.Window; var stage = Fr0317Get<Grid>(w, "_expandedRevealStage"); var original = stage.RenderTransform;
        Fr0317Start(f); await Dispatcher.Yield(DispatcherPriority.Loaded);
        var reveal = Fr0317Get<object>(w, "_expandedReveal");
        var opening = (ClockGroup)reveal.GetType().GetProperty("Completion")!.GetValue(reveal)!;
        opening.Controller!.Pause(); opening.Controller.SeekAlignedToLastTick(TimeSpan.FromMilliseconds(190), TimeSeekOrigin.BeginTime); f.Layout();
        var opacity = stage.Opacity;
        var oldGhost = Fr0317Visuals<Image>(stage).Single(image => image.Name == "ExpandedCoverTransition");
        var start = oldGhost.TransformToAncestor(stage).TransformBounds(new Rect(oldGhost.RenderSize));
        var oldScale = (ScaleTransform)reveal.GetType().GetProperty("StageScale")!.GetValue(reveal)!;
        var scale = oldScale.ScaleX;
        Fc040Begin(f, f.SourceBounds); f.Layout();
        var collapse = Fr0317Get<object>(w, "_expandedCollapse"); var clock = Fc040Clock(collapse); clock.Controller!.Pause();
        var ghost = Fr0317Visuals<Image>(stage).Single(image => image.Name == "ExpandedCoverCollapse");
        var actual = ghost.TransformToAncestor(stage).TransformBounds(new Rect(ghost.RenderSize));
        var currentScale = (ScaleTransform)collapse.GetType().GetProperty("StageScale")!.GetValue(collapse)!;
        Assert(Math.Abs(stage.Opacity - opacity) < .001 && Math.Abs(currentScale.ScaleX - scale) < .001
            && Math.Abs(actual.Left - start.Left) < .001 && Math.Abs(actual.Top - start.Top) < .001
            && Math.Abs(actual.Width - start.Width) < .001 && Math.Abs(actual.Height - start.Height) < .001,
            $"opening reversal changed the composed frame: opacity {opacity}->{stage.Opacity}, scale {scale}->{currentScale.ScaleX}, cover {start}->{actual}");
        Assert(Fr0317Get<object?>(w, "_expandedReveal") is null && Fr0317Get<object?>(w, "_expandedRevealPending") is null && oldGhost.Source is null,
            "opening clock survives reversal");
        Fr0317Call(w, "CloseExpandedPlayer"); Fc040Clean(f, original);
    }

    private static async Task Fc040Lifetime()
    {
        foreach (var action in new[] { "force", "track", "account", "closed", "unloaded", "owner-hide", "owner-minimize", "host-close", "reopen" })
        {
            using var f = new Fr0317Fixture("collapse-life-" + action); var w = f.Window;
            Fr0317Call(w, "SetExpandedPlayerVisible", true, true);
            Fr0317Call(w, "CancelExpandedReveal");
            var stage = Fr0317Get<Grid>(w, "_expandedRevealStage");
            Fr0317Get<Image>(w, "_expandedPlayerCover").Source = f.Artwork;
            var original = stage.RenderTransform; var before = Fh040Placement(w);
            var request = new FullscreenDisplayRequest(_ => FullscreenDisplayRequest.Continuous);
            Fr0317Get<FullscreenDisplayRequest>(w, "_fullscreenDisplayRequest").Dispose();
            Fr0317Set(w, "_fullscreenDisplayRequest", request); request.SetRequired(true);
            Fc040Begin(f, f.SourceBounds);
            Assert(!request.IsHeld, "collapse keeps a display-power request alive");
            if (Motion.CanAnimate)
            {
                var collapse = Fr0317Get<object>(w, "_expandedCollapse"); var clock = Fc040Clock(collapse); clock.Controller!.Pause();
                Fr0317Call(w, "RequestExpandedPlayerClose"); Fc040Begin(f, f.SourceBounds);
                Assert(ReferenceEquals(collapse, Fr0317Get<object>(w, "_expandedCollapse")), "repeated Esc restarted the close clock");
            }
            switch (action)
            {
                case "force": Fr0317Call(w, "SetExpandedPlayerVisible", false, false); break;
                case "track": Fr0317Set(w, "_current", f.Second); Fr0317Call(w, "RefreshExpandedPlayer"); break;
                case "account": Fr0317Set(w, "_accountGeneration", Fr0317Get<int>(w, "_accountGeneration") + 1); Fr0317Call(w, "RefreshExpandedPlayer"); break;
                case "closed": w.Close(); break;
                case "unloaded": Fr0317Call(w, "CloseExpandedPlayer"); Fr0317Get<Grid>(w, "_expandedPlayerSurface").RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent)); break;
                case "owner-hide": Fr0317Call(w, "FullscreenPowerVisibilityChanged", w, new DependencyPropertyChangedEventArgs()); break;
                case "owner-minimize": w.WindowState = WindowState.Minimized; Fr0317Call(w, "FullscreenPowerStateChanged", w, EventArgs.Empty); break;
                case "host-close":
                    if (Fr0317Get<object?>(w, "_expandedFullscreenHost") is not null) Fh040Window(w).Close();
                    // A user close now follows the same finite collapse route
                    // while the cached native source remains alive for reuse.
                    if (Fr0317Get<object?>(w, "_expandedCollapse") is { } pendingClose)
                    {
                        Fc040Clock(pendingClose).Controller!.SeekAlignedToLastTick(TimeSpan.FromMilliseconds(380), TimeSeekOrigin.BeginTime);
                        await Dispatcher.Yield(DispatcherPriority.Background);
                    }
                    break;
                case "reopen": Fr0317Call(w, "SetExpandedPlayerVisible", true, false); Fr0317Call(w, "CloseExpandedPlayer"); break;
            }
            Fc040Clean(f, original, action);
            Assert(Fr0317Get<object?>(w, "_expandedFullscreenHost") is null && !request.IsHeld, action + " retained host or power request");
            if (action != "owner-minimize") Assert(Fh040Placement(w) == before, action + " changed the owner window geometry or style");
        }
    }

    private static Task Fc040Fallbacks()
    {
        foreach (var kind in new[] { "no-hwnd", "immediate-api", "zero-target", "zero-stage", "reduced-motion", "no-artwork" })
        {
            var previousMode = RenderOptions.ProcessRenderMode;
            try
            {
                using var f = new Fr0317Fixture("collapse-fallback-" + kind); f.Open();
                var w = f.Window; var stage = Fr0317Get<Grid>(w, "_expandedRevealStage"); var original = stage.RenderTransform;
                if (kind == "reduced-motion") RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;
                if (kind == "zero-stage") { f.Root.Measure(new Size()); f.Root.Arrange(new Rect()); f.Root.UpdateLayout(); }
                if (kind == "no-hwnd") Fr0317Call(w, "RequestExpandedPlayerClose");
                else if (kind == "immediate-api") Fr0317Call(w, "SetExpandedPlayerVisible", false, false);
                else if (kind == "no-artwork")
                {
                    Fr0317Get<Image>(w, "_expandedPlayerCover").Source = null;
                    Fr0317Call(w, "BeginExpandedCollapseFromLocalCover", f.SourceBounds, null);
                    Assert(!Fr0317Visuals<Image>(stage).Any(image => image.Name == "ExpandedCoverCollapse"), "missing artwork created an invalid ghost");
                    Fr0317Call(w, "CloseExpandedPlayer");
                }
                else Fc040Begin(f, kind == "zero-target" ? new Rect() : f.SourceBounds);
                Fc040Clean(f, original);
                Assert(!w.IsVisible && PresentationSource.FromVisual(w) is null && new WindowInteropHelper(w).Handle == 0,
                    kind + " acquired native presentation");
            }
            finally { RenderOptions.ProcessRenderMode = previousMode; }
        }
        return Task.CompletedTask;
    }

    private static void Fc040Begin(Fr0317Fixture f, Rect target) => Fr0317Call(f.Window, "BeginExpandedCollapseFromLocalCover", target, f.Artwork);
    private static ClockGroup Fc040Clock(object collapse) => (ClockGroup)collapse.GetType().GetProperty("Completion")!.GetValue(collapse)!;
    private static void Fc040Clean(Fr0317Fixture f, Transform original, string context = "completed")
    {
        var w = f.Window; var stage = Fr0317Get<Grid>(w, "_expandedRevealStage"); var cover = Fr0317Get<Button>(w, "_expandedPlayerCoverButton");
        Assert(Fr0317Get<object?>(w, "_expandedCollapse") is null && Fr0317Get<object?>(w, "_expandedReveal") is null
            && Fr0317Get<object?>(w, "_expandedRevealPending") is null && stage.Opacity == 1 && cover.Opacity == 1
            && stage.IsHitTestVisible && !stage.HasAnimatedProperties && ReferenceEquals(stage.RenderTransform, original)
            && !Fr0317Visuals<Image>(stage).Any(image => image.Name is "ExpandedCoverCollapse" or "ExpandedCoverTransition")
            && ((ContentControl)w.FindName("ExpandedPlayerHost")).Visibility == Visibility.Collapsed,
            $"{context}: close retained clocks ghost transform opacity or expanded state; visibility={((ContentControl)w.FindName("ExpandedPlayerHost")).Visibility}, opacity={stage.Opacity}, cover={cover.Opacity}, hit={stage.IsHitTestVisible}, animated={stage.HasAnimatedProperties}, originalTransform={ReferenceEquals(stage.RenderTransform, original)}");
    }
}
