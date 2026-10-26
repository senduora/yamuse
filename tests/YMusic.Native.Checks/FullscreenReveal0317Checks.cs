using System.ComponentModel;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using YMusic.Native;

internal static partial class Program
{
    private static async Task CheckFullscreenReveal0317()
    {
        await Run("FullscreenReveal0317: background ambient controls and cover share one finite reveal", Fr0317Frames);
        await Run("FullscreenReveal0317: first frame is prepared before placement and maximized entry has no normal bounce", Fr0317Placement);
        await Run("FullscreenReveal0317: rapid close track panes account lifetime and unload cancel all owned clocks", Fr0317Cancellation);
        await Run("FullscreenReveal0317: reduced motion and zero size retain fully restored content", Fr0317Fallbacks);
        await Run("FullscreenReveal0317: never-shown four pane states keep queue left player center and lyrics right", Fr0317PaneLayout);
        File.WriteAllText(Path.Combine(_directory, "fullscreen-reveal-fix-0317-scope.json"), JsonSerializer.Serialize(new
        {
            fixtureWindowsShown = false, nativeHandlesCreated = false, desktopInputUsed = false, desktopScreenshots = false,
            userAccountLoaded = false, audioPlayed = false, externalNetworkRequests = 0, nativeFullscreenGeometryExercised = false,
            frameTimesMilliseconds = new[] { 0, 80, 190, 380 }, placementUsesNeverShownWindows = true,
            checkedAssemblySha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(MainWindow).Assembly.Location)))
        }, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static async Task Fr0317Frames()
    {
        using var f = new Fr0317Fixture("frames"); f.Open();
        var stage = Fr0317Get<Grid>(f.Window, "_expandedRevealStage");
        var surface = Fr0317Get<Grid>(f.Window, "_expandedPlayerSurface");
        var cover = Fr0317Get<Button>(f.Window, "_expandedPlayerCoverButton");
        var originalTransform = stage.RenderTransform; var originalOpacity = stage.Opacity; var originalCover = cover.Opacity;
        var baseChildren = stage.Children.Count;
        Fr0317Start(f); await Dispatcher.Yield(DispatcherPriority.Loaded);
        if (!Motion.CanAnimate)
        {
            Assert(Fr0317Get<object?>(f.Window, "_expandedReveal") is null && stage.Opacity == originalOpacity, "unavailable motion left prepared content hidden");
            return;
        }
        var reveal = Fr0317Get<object>(f.Window, "_expandedReveal");
        var clock = (Clock)reveal.GetType().GetProperty("Completion")!.GetValue(reveal)!;
        Assert(clock.Controller is not null, "finite reveal has no bounded clock controller"); clock.Controller!.Pause();
        var samples = new List<object>(); var opacity = new List<(double Stage, double Controls, double Ambient)>();
        foreach (var milliseconds in new[] { 0, 80, 190, 380 })
        {
            clock.Controller.SeekAlignedToLastTick(TimeSpan.FromMilliseconds(milliseconds), TimeSeekOrigin.BeginTime);
            f.Layout();
            var ambient = Fr0317Get<AmbientBackdrop>(f.Window, "_expandedAmbient");
            var ghost = Fr0317Visuals<Image>(stage).SingleOrDefault(image => image.Name == "ExpandedCoverTransition");
            var ghostRect = ghost?.TransformToAncestor(stage).TransformBounds(new Rect(ghost.RenderSize));
            opacity.Add((stage.Opacity, stage.Opacity * surface.Opacity, stage.Opacity * ambient.Opacity));
            samples.Add(new { milliseconds, stageOpacity = stage.Opacity, surfaceOpacity = surface.Opacity, ambientOpacity = ambient.Opacity,
                effectiveAmbientOpacity = stage.Opacity * ambient.Opacity, effectiveControlsOpacity = stage.Opacity * surface.Opacity,
                ghostBounds = ghostRect?.ToString(), imageSourceShared = ghost is not null && ReferenceEquals(ghost.Source, f.Artwork),
                neverShown = !f.Window.IsVisible && PresentationSource.FromVisual(f.Window) is null });
            Fr0317Render(f.Root, Path.Combine(_directory, $"fullscreen-reveal-fix-0317-{milliseconds:000}.png"));
        }
        File.WriteAllText(Path.Combine(_directory, "fullscreen-reveal-fix-0317-frames.json"), JsonSerializer.Serialize(samples, new JsonSerializerOptions { WriteIndented = true }));
        Assert(opacity[0].Stage < .001, "opaque fullscreen Stage/background was visible at the first prepared frame");
        Assert(opacity[1].Stage is > 0 and < 1 && opacity[2].Stage > opacity[1].Stage && opacity[2].Stage < 1, "whole Stage did not fade through shared intermediate frames");
        Assert(opacity.All(frame => Math.Abs(frame.Stage - frame.Controls) < .001 && Math.Abs(frame.Stage - frame.Ambient) < .001), "ambient/background and controls use different reveal phases");
        await Dispatcher.Yield(DispatcherPriority.Background);
        Assert(stage.Opacity == originalOpacity && ReferenceEquals(stage.RenderTransform, originalTransform) && cover.Opacity == originalCover
            && stage.Children.Count == baseChildren && !stage.HasAnimatedProperties && Fr0317Get<object?>(f.Window, "_expandedReveal") is null
            && Fr0317Get<object?>(f.Window, "_expandedRevealPending") is null, "finite reveal retained overlay clocks opacity or transforms");
    }

    private static Task Fr0317Placement()
    {
        var rows = new List<object>();
        var allPrepared = true; var allFirstOpensRetained = true; var allStatePlansMinimal = true;
        foreach (var state in new[] { WindowState.Normal, WindowState.Maximized, WindowState.Minimized })
        {
            using var f = new Fr0317Fixture("placement-" + state); var w = f.Window;
            Fr0317Call(w, "BuildExpandedPlayer"); f.Layout();
            w.Left = -17320; w.Top = -260; w.Width = 1180; w.Height = 760; w.WindowState = state;
            var style = w.WindowStyle; var resize = w.ResizeMode; var restore = w.RestoreBounds;
            var observations = new List<object>(); var transitions = new List<WindowState>();
            var stateDescriptor = DependencyPropertyDescriptor.FromProperty(System.Windows.Window.WindowStateProperty, typeof(System.Windows.Window));
            var styleDescriptor = DependencyPropertyDescriptor.FromProperty(System.Windows.Window.WindowStyleProperty, typeof(System.Windows.Window));
            var resizeDescriptor = DependencyPropertyDescriptor.FromProperty(System.Windows.Window.ResizeModeProperty, typeof(System.Windows.Window));
            var firstFrames = new List<double>();
            EventHandler observe = (_, _) =>
            {
                var stage = Fr0317Get<Grid>(w, "_expandedRevealStage"); firstFrames.Add(stage.Opacity);
                observations.Add(new { state = w.WindowState.ToString(), style = w.WindowStyle.ToString(), stageOpacity = stage.Opacity,
                    hostVisible = ((ContentControl)w.FindName("ExpandedPlayerHost")).Visibility == Visibility.Visible });
            };
            EventHandler stateChanged = (_, _) => { transitions.Add(w.WindowState); observe(w, EventArgs.Empty); };
            stateDescriptor.AddValueChanged(w, stateChanged); styleDescriptor.AddValueChanged(w, observe); resizeDescriptor.AddValueChanged(w, observe);
            try { Fr0317Call(w, "SetExpandedPlayerVisible", true, true); }
            finally { stateDescriptor.RemoveValueChanged(w, stateChanged); styleDescriptor.RemoveValueChanged(w, observe); resizeDescriptor.RemoveValueChanged(w, observe); }
            var firstOpenRetained = !Motion.CanAnimate || Fr0317Get<object?>(w, "_expandedReveal") is not null
                && Fr0317Get<object?>(w, "_expandedRevealPending") is not null && Fr0317Get<Grid>(w, "_expandedRevealStage").Opacity == 0
                && ReferenceEquals(Fr0317Get<TrackRow?>(w, "_expandedPlayerTrack"), f.First);
            rows.Add(new { originalState = state.ToString(), transitions = transitions.Select(value => value.ToString()).ToArray(), firstOpenRetained, observations });
            File.WriteAllText(Path.Combine(_directory, "fullscreen-reveal-fix-0317-placement.json"), JsonSerializer.Serialize(rows, new JsonSerializerOptions { WriteIndented = true }));
            if (Motion.CanAnimate) allPrepared &= observations.Count == 0 && Fr0317Get<Grid>(w, "_expandedRevealStage").Opacity == 0;
            allFirstOpensRetained &= firstOpenRetained;
            allStatePlansMinimal &= transitions.Count == 0;
            Fr0317Call(w, "CloseExpandedPlayer");
            Assert(w.WindowState == state && w.WindowStyle == style && w.ResizeMode == resize && w.Left == -17320 && w.Top == -260 && w.Width == 1180 && w.Height == 760
                && w.RestoreBounds == restore && !w.IsVisible && PresentationSource.FromVisual(w) is null, "closing fullscreen lost never-shown placement or created a native handle");
        }
        Assert(allPrepared, "fullscreen changed the main placement or failed to prepare its separate surface");
        Assert(allFirstOpensRetained, "production first-open Refresh canceled the prepared reveal or left the initial track unbound");
        Assert(allStatePlansMinimal, "enter fullscreen performed an unnecessary Normal/Maximized state bounce");
        return Task.CompletedTask;
    }

    private static async Task Fr0317Cancellation()
    {
        foreach (var action in new[] { "immediate-close", "started-close", "track", "queue", "lyrics", "account", "closed", "unloaded" })
        {
            using var f = new Fr0317Fixture("cancel-" + action); f.Open(); var w = f.Window;
            var stage = Fr0317Get<Grid>(w, "_expandedRevealStage"); var cover = Fr0317Get<Button>(w, "_expandedPlayerCoverButton");
            stage.RenderTransform = new TranslateTransform(3, -4); stage.Opacity = .73; cover.Opacity = .61;
            var before = stage.RenderTransform; var opacity = stage.Opacity; var coverOpacity = cover.Opacity;
            Fr0317Start(f); if (action != "immediate-close") await Dispatcher.Yield(DispatcherPriority.Loaded);
            switch (action)
            {
                case "immediate-close": case "started-close": Fr0317Call(w, "CloseExpandedPlayer"); break;
                case "track": Fr0317Set(w, "_current", f.Second); Fr0317Call(w, "RefreshExpandedPlayer"); break;
                case "queue": Fr0317Call(w, "SetExpandedQueueVisible", true, false); break;
                case "lyrics": Fr0317Call(w, "SetExpandedLyricsVisible", true, false); break;
                case "account": w.SignOut(); break;
                case "closed": w.Close(); break;
                case "unloaded": Fr0317Get<Grid>(w, "_expandedPlayerSurface").RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent)); break;
            }
            Assert(Fr0317Get<object?>(w, "_expandedReveal") is null && Fr0317Get<object?>(w, "_expandedRevealPending") is null
                && stage.Opacity == opacity && ReferenceEquals(stage.RenderTransform, before) && cover.Opacity == coverOpacity && !stage.HasAnimatedProperties
                && !Fr0317Visuals<Image>(stage).Any(image => image.Name == "ExpandedCoverTransition"), action + " retained transition work or hidden content");
            if (action is "immediate-close" or "started-close")
                for (var i = 0; i < 3; i++) { f.Open(); Fr0317Start(f); Fr0317Call(w, "CloseExpandedPlayer"); }
            Assert(!w.IsVisible && PresentationSource.FromVisual(w) is null, "cancellation fixture acquired a native surface");
        }
    }

    private static async Task Fr0317Fallbacks()
    {
        var previous = RenderOptions.ProcessRenderMode;
        try
        {
            RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;
            using var f = new Fr0317Fixture("reduced-motion"); f.Open(); Fr0317Start(f); await Dispatcher.Yield(DispatcherPriority.Loaded);
            Assert(!Motion.CanAnimate && Fr0317Get<Grid>(f.Window, "_expandedRevealStage").Opacity == 1 && Fr0317Get<object?>(f.Window, "_expandedReveal") is null, "disabled motion left hidden content or a clock");
        }
        finally { RenderOptions.ProcessRenderMode = previous; }
        using (var f = new Fr0317Fixture("zero-size"))
        {
            f.Open(); f.Root.Measure(new Size()); f.Root.Arrange(new Rect()); f.Root.UpdateLayout();
            Fr0317Start(f); await Dispatcher.Yield(DispatcherPriority.Loaded);
            Assert(Fr0317Get<Grid>(f.Window, "_expandedRevealStage").Opacity == 1 && Fr0317Get<object?>(f.Window, "_expandedReveal") is null
                && Fr0317Get<object?>(f.Window, "_expandedRevealPending") is null, "zero-size reveal retained a pending clock or invisible stage");
        }
    }

    private static Task Fr0317PaneLayout()
    {
        using var f = new Fr0317Fixture("pane-layout"); f.Open(); var w = f.Window;
        foreach (var size in new[] { new Size(1050, 680), new Size(1280, 860), new Size(1920, 1080) })
            foreach (var (lyrics, queue) in new[] { (false, false), (true, false), (true, true), (false, true) })
            {
                Fr0317Call(w, "SetExpandedLyricsVisible", lyrics, false); Fr0317Call(w, "SetExpandedQueueVisible", queue, false); f.Layout(size);
                var stage = Fr0317Get<Grid>(w, "_expandedRevealStage"); var cover = Fr0317Get<Button>(w, "_expandedPlayerCoverButton");
                var rect = cover.TransformToAncestor(stage).TransformBounds(new Rect(cover.RenderSize));
                var l = Fr0317Get<Grid>(w, "_expandedLyricsPanel"); var q = Fr0317Get<Grid>(w, "_expandedQueuePanel");
                Assert(Fr0317Get<bool>(w, "_expandedLyricsOpen") == lyrics && Fr0317Get<bool>(w, "_expandedQueueOpen") == queue, "pane states are no longer independent");
                if (queue) Assert(q.TransformToAncestor(stage).TransformBounds(new Rect(q.RenderSize)).Right <= rect.Left + 1, "queue is not left of the player");
                if (lyrics) Assert(l.TransformToAncestor(stage).TransformBounds(new Rect(l.RenderSize)).Left >= rect.Right - 1, "lyrics are not right of the player");
                foreach (var name in new[] { "ExpandedPlayerPrevious", "ExpandedPlayerPlayPause", "ExpandedPlayerNext", "ExpandedPlayerShuffle", "ExpandedPlayerRepeat", "ExpandedPlayerLike", "ExpandedPlayerLyrics", "ExpandedPlayerQueue", "ExpandedPlayerMore", "ExpandedPlayerSeek", "ExpandedPlayerVolume" })
                {
                    var control = Fr0317Visuals<FrameworkElement>(stage).Single(value => value.Name == name);
                    var bounds = control.TransformToAncestor(stage).TransformBounds(new Rect(control.RenderSize));
                    Assert(bounds.Width > 5 && bounds.Height > 5 && bounds.Left >= 0 && bounds.Top >= 0 && bounds.Right <= stage.ActualWidth + 1 && bounds.Bottom <= stage.ActualHeight + 1, name + " clipped after reveal integration");
                }
            }
        return Task.CompletedTask;
    }

    private static void Fr0317Start(Fr0317Fixture fixture)
    {
        var local = typeof(MainWindow).GetMethod("BeginExpandedRevealFromLocalCover", BindingFlags.Instance | BindingFlags.NonPublic);
        if (local is not null) local.Invoke(fixture.Window, [fixture.SourceBounds, fixture.Artwork]);
        else Fr0317Call(fixture.Window, "BeginExpandedReveal", new object?[] { null });
    }
    private static void Fr0317Call(MainWindow window, string name, params object?[] args) => typeof(MainWindow).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, args);
    private static T Fr0317Get<T>(MainWindow window, string name) => (T)typeof(MainWindow).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!;
    private static void Fr0317Set(MainWindow window, string name, object? value) => typeof(MainWindow).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(window, value);
    private static IEnumerable<T> Fr0317Visuals<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T value) yield return value;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++) foreach (var child in Fr0317Visuals<T>(VisualTreeHelper.GetChild(root, index))) yield return child;
    }
    private static void Fr0317Render(FrameworkElement element, string path)
    {
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth), (int)Math.Ceiling(element.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        // Window.Background normally supplies the opaque client backing. A
        // never-shown root-only export must supply that same backing explicitly.
        var backing = new DrawingVisual(); using (var drawing = backing.RenderOpen())
            drawing.DrawRectangle(System.Windows.Window.GetWindow(element)?.Background ?? new SolidColorBrush(Color.FromRgb(16, 17, 20)), null, new Rect(element.RenderSize));
        bitmap.Render(backing); bitmap.Render(element);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var output = File.Create(path); encoder.Save(output);
    }

    private sealed class Fr0317Fixture : IDisposable
    {
        public MainWindow Window { get; }
        public FrameworkElement Root => (FrameworkElement)Window.Content;
        public Size Size { get; private set; } = new(1280, 860);
        public TrackRow First { get; } = new() { Id = "fullscreen-private-1", RequestId = "fullscreen-private-1", Title = "Частный проверочный трек", Artist = "Fixture", Available = true, DurationMs = 50000 };
        public TrackRow Second { get; } = new() { Id = "fullscreen-private-2", RequestId = "fullscreen-private-2", Title = "Следующий проверочный трек", Artist = "Fixture", Available = true, DurationMs = 50000 };
        public BitmapSource Artwork { get; }
        public Rect SourceBounds { get; }
        private readonly Fr0317Http _http = new();
        public Fr0317Fixture(string name)
        {
            var directory = Path.Combine(_directory, "fullscreen-never-shown-" + name); Directory.CreateDirectory(directory);
            var visual = new DrawingVisual(); using (var drawing = visual.RenderOpen())
            { drawing.DrawRectangle(new LinearGradientBrush(Colors.DarkOrange, Colors.DarkSlateBlue, 60), null, new Rect(0, 0, 512, 512)); drawing.DrawEllipse(Brushes.Wheat, null, new Point(256, 256), 90, 90); }
            var image = new RenderTargetBitmap(512, 512, 96, 96, PixelFormats.Pbgra32); image.Render(visual); image.Freeze(); Artwork = image;
            Window = new MainWindow(api: new MusicApi(_http), store: new SessionStore(Path.Combine(directory, "session")), restore: false, diagnosticsDirectory: directory,
                offline: new OfflineLibrary(Path.Combine(directory, "offline")), player: new NativePlayer(new AudioBuffer(Path.Combine(directory, "audio"), new Fr0317Http())), preferences: new PreferencesStore(Path.Combine(directory, "preferences")))
            { ShowActivated = false, ShowInTaskbar = false, Width = 1280, Height = 860, WindowStartupLocation = WindowStartupLocation.Manual, Left = -17320, Top = -260 };
            Fr0317Set(Window, "_current", First); Fr0317Get<PlaybackQueue>(Window, "_playbackQueue").SetItems([First, Second], First);
            // A never-started private request suppresses network-only lyric discovery
            // while exercising its existing visibility/cancellation machinery.
            Fr0317Set(Window, "_lyricsCts", new CancellationTokenSource());
            var playerCover = (Image)Window.FindName("PlayerCover"); playerCover.Source = Artwork; Layout();
            SourceBounds = playerCover.TransformToAncestor(Root).TransformBounds(new Rect(playerCover.RenderSize));
            Assert(!Window.IsVisible && PresentationSource.FromVisual(Window) is null && new WindowInteropHelper(Window).Handle == IntPtr.Zero, "fixture constructor created a native window");
        }
        public void Open()
        {
            Fr0317Call(Window, "SetExpandedPlayerVisible", true, false);
            Fr0317Get<Image>(Window, "_expandedPlayerCover").Source = Artwork; Layout();
        }
        public void Layout(Size? size = null)
        { if (size is { } next) Size = next; Root.Measure(Size); Root.Arrange(new Rect(Size)); Root.UpdateLayout(); }
        public void Dispose() { Window.Close(); _http.Dispose(); }
    }
    private sealed class Fr0317Http : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Never-shown fullscreen fixture attempted HTTP: " + request.RequestUri?.Host);
    }
}
