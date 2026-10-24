using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using YMusic.Native;

internal static partial class Program
{
    private static async Task CheckScrollPerformance()
    {
        await Run("Scroll0311: actual 3920-row covered collection frame layout allocation measurement", ScrollCollectionMeasurement);
        await Run("Scroll0311: retargeted wheel keeps velocity and monotonic exact endpoints", ScrollTrajectoryContinuity);
        await Run("Scroll0311: precision bursts native input nested boundaries and high-DPI endpoints", ScrollRoutingRegression);
    }

    private static Task ScrollTrajectoryContinuity()
    {
        foreach (var sign in new[] { -1, 1 })
        {
            var previous = SmoothScroll.Interpolate(0, sign * 48, sign * 144d / 280, 20d / 280);
            var target = sign * 96d;
            var retargeted = SmoothScroll.Interpolate(previous.Offset, target, previous.Velocity, 0);
            Assert(Math.Abs(previous.Velocity - retargeted.Velocity) < 1e-10,
                "the next wheel packet introduced an instantaneous velocity jump");
            var position = previous.Offset;
            for (var step = 0; step <= 280; step++)
            {
                var sample = SmoothScroll.Interpolate(previous.Offset, target, previous.Velocity, step / 280d);
                Assert(sign * (sample.Offset - position) >= -1e-9 && sign * (target - sample.Offset) >= -1e-9,
                    "retargeting overshot or reversed inside a same-direction gesture");
                position = sample.Offset;
            }
            var end = SmoothScroll.Interpolate(previous.Offset, target, previous.Velocity, 1);
            Assert(end.Offset == target && Math.Abs(end.Velocity) < 1e-12, "trajectory did not stop at its exact endpoint");
        }
        Assert(!Motion.AnimationAllowed(false, 2, RenderMode.Default)
            && !Motion.AnimationAllowed(true, 0, RenderMode.Default)
            && !Motion.AnimationAllowed(true, 2, RenderMode.SoftwareOnly), "reduced-motion policy changed");
        return Task.CompletedTask;
    }

    private static async Task ScrollRoutingRegression()
    {
        var outerBody = new StackPanel();
        var inner = new ListBox { Height = 150, ItemsSource = Enumerable.Range(0, 200).Select(i => "Row " + i).ToArray() };
        ScrollViewer.SetCanContentScroll(inner, true);
        VirtualizingPanel.SetScrollUnit(inner, ScrollUnit.Pixel);
        var text = new TextBox { Text = "Native text wheel owner", Height = 35 };
        outerBody.Children.Add(text); outerBody.Children.Add(inner); outerBody.Children.Add(new Border { Height = 2000 });
        var outer = new ScrollViewer { Content = outerBody, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        SmoothScroll.SetEnabled(outer, true);
        var window = new Window { Content = outer, Width = 500, Height = 400, Left = -16000, Top = 0,
            ShowActivated = false, ShowInTaskbar = false, UseLayoutRounding = true, WindowStartupLocation = WindowStartupLocation.Manual };
        window.Show();
        try
        {
            await Task.Delay(120); window.UpdateLayout();
            var viewer = SaVisuals<ScrollViewer>(inner).First();
            var source = SaVisuals<ListBoxItem>(inner).First();
            if (Motion.CanAnimate)
            {
                for (var i = 0; i < 8; i++) Assert(ScrollWheel(source, -15).Handled, "precision burst escaped its pixel owner");
                Assert(!SmoothScroll.IsAnimating(viewer), "precision packets left an animation tail");
                await Task.Delay(100);
                var lineDistance = SystemParameters.WheelScrollLines < 0 ? viewer.ViewportHeight : SystemParameters.WheelScrollLines * 16d;
                Assert(Math.Abs(viewer.VerticalOffset - lineDistance) <= 1, "packets arriving before layout lost precision distance");
                viewer.ScrollToTop(); await Task.Delay(60);
                for (var i = 0; i < 24; i++) { ScrollWheel(source, -1); await Task.Delay(8); }
                Assert(Math.Abs(viewer.VerticalOffset - lineDistance * 24 / 120) <= 1,
                    "high-DPI per-packet rounding lost the fractional precision remainder");
                viewer.ScrollToTop(); await Task.Delay(60);
                ScrollWheel(source, -30); ScrollWheel(source, -120);
                await Until(() => !SmoothScroll.IsAnimating(viewer));
                Assert(Math.Abs(viewer.VerticalOffset - lineDistance * 1.25) <= 1, "precision-to-notch handoff lost queued distance");

                for (var i = 0; i < 5; i++) ScrollWheel(source, -120);
                await Task.Delay(50); var beforeReverse = viewer.VerticalOffset;
                ScrollWheel(source, 120); await Until(() => !SmoothScroll.IsAnimating(viewer));
                Assert(viewer.VerticalOffset <= beforeReverse + 1, "reverse first paid the accumulated forward target");
                Assert(outer.VerticalOffset == 0, "nested pixel wheel moved the outer viewport");

                ScrollWheel(source, -120);
                var key = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window), Environment.TickCount, Key.Down)
                { RoutedEvent = Keyboard.PreviewKeyDownEvent };
                source.RaiseEvent(key);
                Assert(!SmoothScroll.IsAnimating(viewer) && !key.Handled, "native key did not cancel motion or was swallowed");
                ScrollWheel(source, -120);
                viewer.RaiseEvent(new DragStartedEventArgs(0, 0) { RoutedEvent = Thumb.DragStartedEvent });
                Assert(!SmoothScroll.IsAnimating(viewer), "native scrollbar drag failed to cancel motion");
                ScrollWheel(source, -120); viewer.ScrollToVerticalOffset(800); await Task.Delay(80);
                Assert(!SmoothScroll.IsAnimating(viewer) && Math.Abs(viewer.VerticalOffset - 800) <= 1, "external ScrollTo lost control");
                ScrollWheel(source, -120); viewer.IsEnabled = false;
                Assert(!SmoothScroll.IsAnimating(viewer), "disabled viewer retained its frame handler"); viewer.IsEnabled = true;

                SmoothScroll.ScrollTo(viewer, viewer.ScrollableHeight - .8);
                await Until(() => !SmoothScroll.IsAnimating(viewer));
                Assert(Math.Abs(viewer.VerticalOffset - (viewer.ScrollableHeight - .8)) <= 1,
                    "high-DPI animation suppressed its final physical pixel");
                SmoothScroll.ScrollTo(viewer, viewer.ScrollableHeight);
                await Until(() => !SmoothScroll.IsAnimating(viewer));
                Assert(Math.Abs(viewer.VerticalOffset - viewer.ScrollableHeight) <= .1, "last pixel did not reach the exact extent");
                source = SaVisuals<ListBoxItem>(inner).First();
                var boundary = ScrollWheel(source, -120);
                Assert(boundary.Handled && SmoothScroll.IsAnimating(outer) && !SmoothScroll.IsAnimating(viewer),
                    "nested boundary failed to forward wheel to the outer viewer");
                SmoothScroll.CancelAnimation(outer); outer.ScrollToTop(); await Task.Delay(80);
                Assert(!ScrollWheel(text, -120).Handled && !SmoothScroll.IsAnimating(outer), "text editor wheel was intercepted");

                viewer.ScrollToTop(); await Task.Delay(60);
                VirtualizingPanel.SetScrollUnit(inner, ScrollUnit.Item); window.UpdateLayout(); await Task.Delay(60);
                Assert(!ScrollWheel(SaVisuals<ListBoxItem>(inner).First(), -120).Handled && !SmoothScroll.IsAnimating(viewer),
                    "logical item scrolling was treated as pixels");
                VirtualizingPanel.SetScrollUnit(inner, ScrollUnit.Pixel); window.UpdateLayout();
            }
            var mode = RenderOptions.ProcessRenderMode;
            try
            {
                RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;
                Assert(!ScrollWheel(SaVisuals<ListBoxItem>(inner).First(), -120).Handled && !SmoothScroll.IsAnimating(viewer),
                    "software rendering ignored reduced-motion fallback");
                SmoothScroll.ScrollTo(viewer, 40); await Task.Delay(80);
                Assert(!SmoothScroll.IsAnimating(viewer) && Math.Abs(viewer.VerticalOffset - 40) <= 1, "reduced-motion ScrollTo did not apply directly");
                Assert(ScrollWheel(SaVisuals<ListBoxItem>(inner).First(), -30).Handled && !SmoothScroll.IsAnimating(viewer),
                    "reduced-motion precision input lost its direct pixel route");
            }
            finally { RenderOptions.ProcessRenderMode = mode; }
            File.WriteAllText(Path.Combine(_directory, "scroll0311-routing.json"), JsonSerializer.Serialize(new
            { precisionBurstPackets = 8, precisionToNotchHandoff = true, nestedBoundary = true, nativeTextKeyScrollbarAndExternalScroll = true,
                logicalUnitsPreserved = true, reducedMotion = true, dpiScaleY = VisualTreeHelper.GetDpi(window).DpiScaleY,
                animationAssertionsRun = Motion.CanAnimate, physicalTrackpadNotTested = true }));
        }
        finally { window.Close(); }
        Assert(!SmoothScroll.IsAnimating(outer), "unloaded viewer retained animation");
    }

    private static async Task ScrollCollectionMeasurement()
    {
        var directory = Path.Combine(_directory, "scroll0311"); Directory.CreateDirectory(directory);
        var cacheField = typeof(Artwork).GetField("_cache", BindingFlags.NonPublic | BindingFlags.Static)!;
        var previousCache = cacheField.GetValue(null);
        var coverBytes = ScrollCover(); var coverRequests = 0;
        using var covers = new ArtworkCache(Path.Combine(directory, "covers"), new FakeHandler((_, _) =>
        {
            Interlocked.Increment(ref coverRequests);
            return Task.FromResult(HomeArtworkResponse(coverBytes));
        }));
        Artwork.Cache = covers;
        try
        {
            using var fixture = new SaFixture("scroll0311");
            await fixture.Activate();
            var window = fixture.Window;
            var rows = Enumerable.Range(0, 3920).Select(i => new TrackRow
            {
                Id = "scroll-" + i, RequestId = "scroll-" + i + ":1", Number = i + 1,
                Title = "Трек большой коллекции " + i, Artist = "Исполнитель " + i % 8,
                Artists = [new("artist-" + i % 8, "Исполнитель " + i % 8)], Album = "Альбом " + i % 32,
                Available = true, DurationMs = 180000,
                CoverUri = "https://avatars.yandex.net/scroll-fixture/" + i % 96 + "/%%"
            }).ToArray();
            SaInvoke(window, "ShowRows", (object)rows);
            window.Width = 1280; window.Height = 860;
            await Task.Delay(600); window.UpdateLayout();
            var list = Control<ListView>(window, "TracksList");
            var viewer = SaVisuals<ScrollViewer>(list).First();
            Assert(list.Items.Count == 3920 && viewer.ScrollableHeight > 100000, "measurement did not use the full pixel collection");
            Assert(VirtualizingPanel.GetIsVirtualizing(list) && VirtualizingPanel.GetScrollUnit(list) == ScrollUnit.Pixel,
                "measurement lost the shipped virtualized pixel list");
            await Until(() => SaVisuals<Image>(list).Any(image => image.Source is BitmapSource));
            var facts = new List<object>();
            foreach (var (name, count, interval, delta) in new[]
            {
                ("continuous-wheel", 96, 20, -120),
                ("precision-wheel", 96, 12, -30),
                ("fast-wheel", 64, 20, -360)
            })
            {
                SmoothScroll.CancelAnimation(viewer); viewer.ScrollToTop();
                await Task.Delay(180);
                using var measurement = new ScrollFrameProbe(window, viewer);
                var maxRealized = 0;
                for (var n = 0; n < count; n++)
                {
                    measurement.Input();
                    var source = SaVisuals<ListViewItem>(list).First(item => item.IsVisible);
                    var wheel = ScrollWheel(source, delta);
                    Assert(!Motion.CanAnimate || wheel.Handled, "animated wheel did not route to its pixel viewer");
                    if (n % 16 == 0) maxRealized = Math.Max(maxRealized, SaVisuals<ListViewItem>(list).Count());
                    await Task.Delay(interval);
                }
                await Until(() => !SmoothScroll.IsAnimating(viewer));
                await Dispatcher.Yield(DispatcherPriority.ContextIdle);
                measurement.Stop();
                Assert(maxRealized is > 0 and < 100, "scrolling realized the whole collection: " + maxRealized);
                Assert(viewer.VerticalOffset > 100 && viewer.HorizontalOffset == 0, "scripted wheel did not move the list");
                var frameFile = Path.Combine(directory, name + "-frames.json");
                File.WriteAllText(frameFile, JsonSerializer.Serialize(measurement.Frames, new JsonSerializerOptions { WriteIndented = true }));
                facts.Add(new
                {
                    scenario = name, inputs = count, requestedIntervalMs = interval, delta,
                    measurement = measurement.Summary, maxRealized,
                    coversVisible = SaVisuals<Image>(list).Count(image => image.Source is BitmapSource),
                    finalOffset = viewer.VerticalOffset, rawFrames = Path.GetFileName(frameFile)
                });
            }
            App.Capture(window, Path.Combine(directory, "collection-3920-covered.png"));
            File.WriteAllText(Path.Combine(directory, "collection-measurement.json"), JsonSerializer.Serialize(new
            {
                schema = 1, utc = DateTimeOffset.UtcNow, rows = rows.Length,
                viewport = new { width = list.ActualWidth, height = list.ActualHeight, dpi = VisualTreeHelper.GetDpi(window).DpiScaleY },
                environment = new
                {
                    renderTier = RenderCapability.Tier >> 16,
                    processRenderMode = RenderOptions.ProcessRenderMode.ToString(),
                    clientAreaAnimation = SystemParameters.ClientAreaAnimation,
                    remoteSession = SystemParameters.IsRemoteSession,
                    windowIsOffscreen = window.Left < SystemParameters.VirtualScreenLeft || window.Top < SystemParameters.VirtualScreenTop,
                    runtime = Environment.Version.ToString(), os = Environment.OSVersion.Version.ToString()
                },
                coverRequests, decodedCoverSize = 64, scenarios = facts,
                method = "Fixed packet count/delta, requested async interval; actual intervals recorded. Input latency means oldest unacknowledged packet to next changed offset. Bounded probe subscribes only for the scripted scenario and detaches at Stop.",
                scope = "Actual MainWindow/TracksList/templates; synthetic account, anonymous in-process cover handler; composition callback cadence is not display-present FPS. Offscreen fixture cannot certify 144 Hz or physical presentation."
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        finally { cacheField.SetValue(null, previousCache); }
    }

    private static MouseWheelEventArgs ScrollWheel(UIElement source, int delta)
    {
        var wheel = new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, delta)
        { RoutedEvent = Mouse.PreviewMouseWheelEvent };
        source.RaiseEvent(wheel); return wheel;
    }

    private static byte[] ScrollCover()
    {
        var pixels = new byte[64 * 64 * 4];
        for (var i = 0; i < pixels.Length; i += 4)
        { pixels[i] = (byte)((i / 4) % 64 * 4); pixels[i + 1] = (byte)((i / 256) * 4); pixels[i + 2] = 180; pixels[i + 3] = 255; }
        var bitmap = BitmapSource.Create(64, 64, 96, 96, PixelFormats.Bgra32, null, pixels, 64 * 4);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream(); encoder.Save(stream); return stream.ToArray();
    }

    private sealed class ScrollFrameProbe : IDisposable
    {
        private readonly FrameworkElement _root;
        private readonly ScrollViewer _viewer;
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly Process _process = Process.GetCurrentProcess();
        private readonly double _cpu;
        private readonly long _allocated, _uiAllocated;
        private readonly int[] _collections = Enumerable.Range(0, 3).Select(GC.CollectionCount).ToArray();
        private readonly List<ScrollFrame> _frames = new(2048);
        private readonly List<double> _acknowledgements = new(2048);
        private readonly List<double> _inputs = new(256);
        private TimeSpan? _lastRendering;
        private double? _inputTime;
        private int _layouts, _scrolls, _duplicateCallbacks;
        private bool _stopped;
        private object? _summary;

        internal ScrollFrameProbe(FrameworkElement root, ScrollViewer viewer)
        {
            _root = root; _viewer = viewer;
            _cpu = _process.TotalProcessorTime.TotalMilliseconds;
            _allocated = GC.GetTotalAllocatedBytes(false); _uiAllocated = GC.GetAllocatedBytesForCurrentThread();
            root.LayoutUpdated += Layout; viewer.ScrollChanged += Scroll;
            CompositionTarget.Rendering += Render;
        }
        internal void Input()
        {
            var now = _clock.Elapsed.TotalMilliseconds;
            _inputTime ??= now;
            if (_inputs.Count < 2048) _inputs.Add(now);
        }
        private void Layout(object? sender, EventArgs args) => _layouts++;
        private void Scroll(object sender, ScrollChangedEventArgs args)
        {
            if (!ReferenceEquals(args.OriginalSource, _viewer) || args.VerticalChange == 0) return;
            _scrolls++;
            if (_inputTime is { } input && _acknowledgements.Count < 2048)
            { _acknowledgements.Add(_clock.Elapsed.TotalMilliseconds - input); _inputTime = null; }
        }
        private void Render(object? sender, EventArgs args)
        {
            if (args is not RenderingEventArgs rendering || _frames.Count >= 2048) return;
            if (_lastRendering == rendering.RenderingTime) { _duplicateCallbacks++; return; }
            _lastRendering = rendering.RenderingTime;
            _frames.Add(new(_clock.Elapsed.TotalMilliseconds, rendering.RenderingTime.TotalMilliseconds,
                _viewer.VerticalOffset, SmoothScroll.IsAnimating(_viewer), _layouts, _scrolls));
        }
        internal IReadOnlyList<ScrollFrame> Frames => _frames;
        internal object Summary => _summary ?? throw new InvalidOperationException("Stop measurement before reading it");
        internal void Stop()
        {
            if (_stopped) return; _stopped = true;
            CompositionTarget.Rendering -= Render; _root.LayoutUpdated -= Layout; _viewer.ScrollChanged -= Scroll;
            _clock.Stop();
            var gaps = _frames.Zip(_frames.Skip(1), (a, b) => b.CompositionMs - a.CompositionMs).ToArray();
            var wallGaps = _frames.Zip(_frames.Skip(1), (a, b) => b.WallMs - a.WallMs).ToArray();
            var movedGaps = _frames.Zip(_frames.Skip(1), (a, b) => new { offset = b.Offset - a.Offset, gap = b.CompositionMs - a.CompositionMs })
                .Where(x => Math.Abs(x.offset) > .1).Select(x => x.gap).ToArray();
            _summary = new
            {
                elapsedMs = _clock.Elapsed.TotalMilliseconds, uniqueCompositionFrames = _frames.Count,
                duplicateRenderingCallbacks = _duplicateCallbacks, layoutUpdates = _layouts, scrollChanges = _scrolls,
                compositionGapMs = ScrollDistribution(gaps), callbackWallGapMs = ScrollDistribution(wallGaps),
                changingOffsetFrameGapMs = ScrollDistribution(movedGaps), inputToOffsetMs = ScrollDistribution(_acknowledgements.ToArray()),
                actualInputIntervalMs = ScrollDistribution(_inputs.Zip(_inputs.Skip(1), (a, b) => b - a).ToArray()),
                stationaryDuringAnimation = _frames.Zip(_frames.Skip(1), (a, b) => a.Animating && b.Animating && Math.Abs(b.Offset - a.Offset) < .1).Count(x => x),
                cpuMs = _process.TotalProcessorTime.TotalMilliseconds - _cpu,
                totalAllocatedBytes = GC.GetTotalAllocatedBytes(false) - _allocated,
                uiAllocatedBytes = GC.GetAllocatedBytesForCurrentThread() - _uiAllocated,
                gcCollections = Enumerable.Range(0, 3).Select(i => GC.CollectionCount(i) - _collections[i]).ToArray()
            };
        }
        public void Dispose() { Stop(); _process.Dispose(); }
    }
    private sealed record ScrollFrame(double WallMs, double CompositionMs, double Offset, bool Animating, int LayoutUpdates, int ScrollChanges);
    private static object ScrollDistribution(double[] values)
    {
        Array.Sort(values);
        double Pick(double percentile) => values.Length == 0 ? 0 : values[(int)Math.Ceiling((values.Length - 1) * percentile)];
        return new { count = values.Length, p50 = Pick(.5), p95 = Pick(.95), p99 = Pick(.99), max = values.Length == 0 ? 0 : values[^1] };
    }
}
