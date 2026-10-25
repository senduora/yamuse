using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using YMusic.Native;

internal static partial class Program
{
    private static async Task CheckSmoothScroll016()
    {
        await Run("SmoothScroll016: actual navigation keeps clamped edge packets smooth at both ends", Smooth016NavigationEdges);
        await Run("SmoothScroll016: edge reversal manual scrollbar and changed extent preserve native control", Smooth016Interruptions);
        await Run("SmoothScroll016: partial last row reaches exact edge without losing long-list virtualization", Smooth016VirtualizedEdge);
    }

    private static async Task Smooth016NavigationEdges()
    {
        using var f = new DownloadCenterFixture("smooth016-navigation", 1);
        var w = f.Window; w.Height = w.MinHeight; w.UpdateLayout();
        var viewer = Control<ScrollViewer>(w, "NavigationScroll");
        await Task.Delay(100); w.UpdateLayout();
        Assert(viewer.ScrollableHeight > 2 && SmoothScroll.GetEnabled(viewer), "navigation fixture did not use the shipped scroll owner");
        var facts = new List<object>();
        if (Motion.CanAnimate && SystemParameters.WheelScrollLines != 0)
        {
            foreach (var bottom in new[] { true, false })
                facts.Add(await Smooth016Edge(viewer, viewer.Content as UIElement ?? viewer, bottom));
        }
        File.WriteAllText(Path.Combine(_directory, "smooth-scroll016-navigation.json"), JsonSerializer.Serialize(new
        { animationAssertionsRun = Motion.CanAnimate, viewer.ScrollableHeight, dpiScaleY = VisualTreeHelper.GetDpi(viewer).DpiScaleY, edges = facts }));
    }

    private static async Task<object> Smooth016Edge(ScrollViewer viewer, UIElement source, bool bottom)
    {
        var line = SystemParameters.WheelScrollLines < 0 ? viewer.ViewportHeight : SystemParameters.WheelScrollLines * 16d;
        var remaining = Math.Min(15.75, Math.Min(line / 2, viewer.ScrollableHeight / 3));
        var target = bottom ? viewer.ScrollableHeight : 0;
        await Smooth016Place(viewer, bottom ? target - remaining : remaining);
        var start = viewer.VerticalOffset;
        var offsets = new List<double> { start };
        var changes = new List<object>();
        ScrollChangedEventHandler changed = (_, args) =>
        {
            if (ReferenceEquals(args.OriginalSource, viewer) && args.VerticalChange != 0 && offsets.Count < 128)
            {
                offsets.Add(viewer.VerticalOffset);
                changes.Add(new { viewer.VerticalOffset, args.VerticalChange, args.ExtentHeightChange, args.ViewportHeightChange, viewer.ScrollableHeight });
            }
        };
        viewer.ScrollChanged += changed;
        try
        {
            Assert(ScrollWheel(source, bottom ? -120 : 120).Handled && SmoothScroll.IsAnimating(viewer), "edge wheel did not start animated scrolling");
            var started = Smooth016Started(viewer);
            // All arrive before layout acknowledges the clamped target. The old
            // owner check leaked the second packet into immediate native scrolling.
            for (var packet = 0; packet < 5; packet++)
                Assert(ScrollWheel(source, bottom ? -120 : 120).Handled, "clamped pending edge leaked a wheel packet to native scrolling");
            Assert(Smooth016Started(viewer) == started, "extra clamped packets restarted the animation tail");
            await Until(() => !SmoothScroll.IsAnimating(viewer));
            await Task.Delay(30);
            File.WriteAllText(Path.Combine(_directory, $"smooth-scroll016-edge-{viewer.Name}-{bottom}.json"), JsonSerializer.Serialize(new
            { bottom, start, target, final = viewer.VerticalOffset, finalExtent = viewer.ScrollableHeight, offsets, changes }));
            Assert(Math.Abs(viewer.VerticalOffset - target) <= .1, "edge animation missed its exact final clamp");
            var sign = bottom ? 1 : -1;
            var previous = start; var maximumStep = 0d;
            foreach (var offset in offsets)
            {
                Assert(sign * (offset - previous) >= -.01 && sign * (target - offset) >= -.1, "short edge tail reversed or overshot");
                maximumStep = Math.Max(maximumStep, Math.Abs(offset - previous)); previous = offset;
            }
            Assert(offsets.Count >= 3 && maximumStep < Math.Abs(target - start) * .9, "short edge tail collapsed into one abrupt viewport jump");
            return new { bottom, start, target, final = viewer.VerticalOffset, maximumStep, offsets, repeatedClampedPackets = 5 };
        }
        finally { viewer.ScrollChanged -= changed; SmoothScroll.CancelAnimation(viewer); }
    }

    private static async Task Smooth016Interruptions()
    {
        var body = new Border { Height = 1500.5 };
        var viewer = new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, UseLayoutRounding = false };
        SmoothScroll.SetEnabled(viewer, true);
        var window = new Window { Content = viewer, Width = 350, Height = 400, Left = -16000, Top = 0,
            ShowActivated = false, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual };
        window.Show();
        try
        {
            await Task.Delay(100); window.UpdateLayout();
            if (Motion.CanAnimate && SystemParameters.WheelScrollLines != 0)
            {
                foreach (var bottom in new[] { false, true })
                {
                    var edge = bottom ? viewer.ScrollableHeight : 0;
                    await Smooth016Place(viewer, edge);
                    ScrollWheel(body, bottom ? 120 : -120);
                    Assert(ScrollWheel(body, bottom ? -120 : 120).Handled && !SmoothScroll.IsAnimating(viewer),
                        "immediate reversal at the visible edge retained the old inward animation");
                    await Task.Delay(40);
                    Assert(Math.Abs(viewer.VerticalOffset - edge) <= .1, "cancelled edge reversal applied its stale queued offset");
                }
                await Smooth016Place(viewer, viewer.ScrollableHeight - 15.75);
                ScrollWheel(body, -120); await Task.Delay(50);
                var beforeReverse = viewer.VerticalOffset;
                Assert(ScrollWheel(body, 120).Handled, "reverse wheel escaped the edge owner");
                await Until(() => !SmoothScroll.IsAnimating(viewer));
                Assert(viewer.VerticalOffset < beforeReverse && viewer.VerticalOffset < viewer.ScrollableHeight - 15,
                    "reverse input first paid the pending forward edge target");

                SmoothScroll.ScrollTo(viewer, viewer.ScrollableHeight);
                viewer.RaiseEvent(new DragStartedEventArgs(0, 0) { RoutedEvent = Thumb.DragStartedEvent });
                Assert(!SmoothScroll.IsAnimating(viewer), "thumb input retained the edge animation");
                viewer.ScrollToVerticalOffset(125.25); await Task.Delay(60);
                Assert(Math.Abs(viewer.VerticalOffset - 125.25) <= .1, "manual thumb destination lost native control");
                SmoothScroll.ScrollTo(viewer, 500);
                viewer.ScrollToVerticalOffset(200.25); await Task.Delay(60);
                Assert(!SmoothScroll.IsAnimating(viewer) && Math.Abs(viewer.VerticalOffset - 200.25) <= .1, "external scrolling failed to cancel an animated target");
                SmoothScroll.ScrollTo(viewer, viewer.ScrollableHeight);
                body.Height = 300.25; window.UpdateLayout(); await Task.Delay(60);
                Assert(!SmoothScroll.IsAnimating(viewer) && viewer.VerticalOffset >= 0 && viewer.VerticalOffset <= viewer.ScrollableHeight,
                    "changed extent retained a stale edge target or escaped its new bounds");
            }
            File.WriteAllText(Path.Combine(_directory, "smooth-scroll016-interruptions.json"), JsonSerializer.Serialize(new
            { animationAssertionsRun = Motion.CanAnimate, reversal = true, thumbAndExternalControl = true, changedExtent = true, viewer.VerticalOffset, viewer.ScrollableHeight }));
        }
        finally { window.Close(); }
        Assert(!SmoothScroll.IsAnimating(viewer), "closed edge fixture retained its rendering handler");
    }

    private static async Task Smooth016VirtualizedEdge()
    {
        using var f = new DownloadCenterFixture("smooth016-long-list", 1);
        var rows = Enumerable.Range(0, 2000).Select(index => DcTrack("smooth016-" + index)).ToArray();
        SaInvoke(f.Window, "ShowRows", (object)rows);
        var list = Control<ListView>(f.Window, "TracksList");
        await Task.Delay(150); f.Window.UpdateLayout();
        var viewer = SaVisuals<ScrollViewer>(list).First();
        Assert(list.Items.Count == 2000 && viewer.ScrollableHeight > 10000 && VirtualizingPanel.GetIsVirtualizing(list)
            && VirtualizingPanel.GetScrollUnit(list) == ScrollUnit.Pixel, "edge fixture lost the shipped virtualized pixel list");
        object? edge = null;
        if (Motion.CanAnimate && SystemParameters.WheelScrollLines != 0)
        {
            await Smooth016Place(viewer, viewer.ScrollableHeight - 15.75);
            var source = SaVisuals<ListViewItem>(list).First();
            edge = await Smooth016Edge(viewer, source, bottom: true);
            await Smooth016Place(viewer, viewer.ScrollableHeight - 32.15);
            var start = viewer.VerticalOffset;
            var partialTarget = viewer.ScrollableHeight - .35;
            var extent = viewer.ExtentHeight; var viewport = viewer.ViewportHeight;
            var physicalPixelDip = 1 / VisualTreeHelper.GetDpi(viewer).DpiScaleY;
            // Pixel-scrolling VSP can round the caller's fractional DIP target.
            // Measure that native endpoint on this same viewer, rather than
            // introducing a blanket tolerance that could hide animation drift.
            var nativeGeometryChanged = false;
            ScrollChangedEventHandler nativeGeometry = (_, args) =>
            {
                if (ReferenceEquals(args.OriginalSource, viewer)) nativeGeometryChanged |= args.ExtentHeightChange != 0 || args.ViewportHeightChange != 0;
            };
            var nativeEndpoint = double.NaN;
            viewer.ScrollChanged += nativeGeometry;
            try
            {
                await Smooth016Place(viewer, partialTarget);
                nativeEndpoint = viewer.VerticalOffset;
                Assert(!nativeGeometryChanged && viewer.ExtentHeight == extent && viewer.ViewportHeight == viewport,
                    "native fractional comparator changed extent or viewport");
                Assert(Math.Abs(nativeEndpoint - partialTarget) <= physicalPixelDip + .01,
                    "native fractional endpoint exceeded one physical pixel of representability");
                await Smooth016Place(viewer, start);
                Assert(!nativeGeometryChanged && Math.Abs(viewer.VerticalOffset - start) <= .01 && viewer.ExtentHeight == extent && viewer.ViewportHeight == viewport,
                    "fractional animation did not start from the native comparator's unchanged geometry");
            }
            finally { viewer.ScrollChanged -= nativeGeometry; }
            var offsets = new List<double> { start };
            var requests = new List<double> { start };
            var extentChanged = false;
            var state = Smooth016State(viewer);
            var requestedField = state.GetType().GetField("_requestedOffset", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var pendingField = state.GetType().GetField("_scrollPending", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var targetProperty = state.GetType().GetProperty("TargetOffset", BindingFlags.Instance | BindingFlags.NonPublic)!;
            void CaptureRequest()
            {
                if (requests.Count >= 128) return;
                // A rounded sample equal to the applied viewport sends no
                // command. Record pending frame commands and the final direct
                // TargetOffset command, rather than those discarded samples.
                if ((bool)pendingField.GetValue(state)!) requests.Add((double)requestedField.GetValue(state)!);
                else if (!SmoothScroll.IsAnimating(viewer)) requests.Add((double)targetProperty.GetValue(state)!);
            }
            EventHandler requestedFrame = (_, _) =>
            {
                CaptureRequest();
            };
            ScrollChangedEventHandler changed = (_, args) =>
            {
                if (!ReferenceEquals(args.OriginalSource, viewer)) return;
                extentChanged |= args.ExtentHeightChange != 0 || args.ViewportHeightChange != 0;
                if (args.VerticalChange != 0 && offsets.Count < 128) offsets.Add(viewer.VerticalOffset);
            };
            viewer.ScrollChanged += changed;
            try
            {
                SmoothScroll.ScrollTo(viewer, partialTarget);
                // Subscribe after the product handler, so each sample reads the
                // request just made for that composition frame.
                CompositionTarget.Rendering += requestedFrame;
                await Until(() => !SmoothScroll.IsAnimating(viewer)); await Task.Delay(30);
                CaptureRequest();
                File.WriteAllText(Path.Combine(_directory, "smooth-scroll016-fractional-target.json"), JsonSerializer.Serialize(new
                { start, partialTarget, nativeEndpoint, physicalPixelDip, final = viewer.VerticalOffset, extent, viewport, nativeGeometryChanged, extentChanged, requests, offsets }));
                Assert(!extentChanged && viewer.ExtentHeight == extent && viewer.ViewportHeight == viewport,
                    "fractional animation changed the native comparator's extent or viewport");
                var previous = start;
                foreach (var request in requests)
                {
                    Assert(request >= previous - .01 && request >= start - .01 && request <= partialTarget + .01,
                        "fractional requested path reversed or overshot the caller target");
                    previous = request;
                }
                Assert(Math.Abs(requests[^1] - partialTarget) <= .01, "fractional animation lost its exact final caller request");
                previous = start;
                foreach (var offset in offsets)
                {
                    Assert(offset >= previous - .01 && offset <= nativeEndpoint + .01, "fractional applied path reversed or overshot the measured native endpoint");
                    previous = offset;
                }
                Assert(Math.Abs(viewer.VerticalOffset - nativeEndpoint) <= .01, "fractional animation missed the measured native endpoint");
            }
            finally
            {
                CompositionTarget.Rendering -= requestedFrame;
                viewer.ScrollChanged -= changed; SmoothScroll.CancelAnimation(viewer);
            }
        }
        var realized = SaVisuals<ListViewItem>(list).Count();
        Assert(realized is > 0 and < 100, "near-edge scrolling realized the whole long list: " + realized);
        File.WriteAllText(Path.Combine(_directory, "smooth-scroll016-virtualized.json"), JsonSerializer.Serialize(new
        { animationAssertionsRun = Motion.CanAnimate, rows = rows.Length, realized, edge }));
    }

    private static async Task Smooth016Place(ScrollViewer viewer, double offset)
    {
        SmoothScroll.CancelAnimation(viewer); viewer.ScrollToVerticalOffset(offset);
        await Task.Delay(60);
    }

    private static long Smooth016Started(ScrollViewer viewer)
    {
        var state = Smooth016State(viewer);
        return (long)state.GetType().GetField("_started", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(state)!;
    }

    private static object Smooth016State(ScrollViewer viewer)
    {
        var table = typeof(SmoothScroll).GetField("States", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        var args = new object?[] { viewer, null };
        Assert((bool)table.GetType().GetMethod("TryGetValue")!.Invoke(table, args)!, "enabled scroll state was not registered");
        return args[1]!;
    }
}
