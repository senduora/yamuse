using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using YMusic.Native;

internal static partial class Program
{
    private static async Task CheckLyricsManual040()
    {
        await Run("LyricsManual040: inline wheel bursts retain every notch and interrupt only automatic following", () => LyricsManual040Wheel(false, -120));
        await Run("LyricsManual040: expanded wheel bursts retain every notch and interrupt only automatic following", () => LyricsManual040Wheel(true, -120));
        await Run("LyricsManual040: precision wheel bursts retain their distance in both lyrics views", async () =>
        { await LyricsManual040Wheel(false, -30); await LyricsManual040Wheel(true, -30); });
        await Run("LyricsManual040: inline scrollbar owns the viewport until drag completes even after the idle deadline", () => LyricsManual040Drag(false));
        await Run("LyricsManual040: expanded scrollbar owns the viewport until drag completes even after the idle deadline", () => LyricsManual040Drag(true));
    }

    private static async Task LyricsManual040Wheel(bool expanded, int delta)
    {
        using var f = new LyricsManual040Fixture(expanded, "wheel-" + delta);
        await Dispatcher.Yield(DispatcherPriority.ContextIdle); f.Window.UpdateLayout();
        var viewer = LyricsUiVisuals<ScrollViewer>(f.List).First();
        Assert(viewer.IsLoaded && viewer.IsVisible && viewer.ScrollableHeight > 1200 && SmoothScroll.GetEnabled(viewer),
            "lyrics wheel fixture did not realize the actual scroll owner");
        if (!Motion.CanAnimate || SystemParameters.WheelScrollLines == 0) return;
        var step = -delta / 120d * (SystemParameters.WheelScrollLines < 0 ? viewer.ViewportHeight : SystemParameters.WheelScrollLines * 16d);
        foreach (var (followWasActive, pendingAutomatic) in new[] { (false, false), (true, false), (true, true) })
        {
            SmoothScroll.CancelAnimation(viewer); viewer.ScrollToVerticalOffset(400); f.Window.UpdateLayout();
            await Dispatcher.Yield(DispatcherPriority.ContextIdle);
            var start = viewer.VerticalOffset;
            if (followWasActive) SmoothScroll.ScrollTo(viewer, Math.Min(viewer.ScrollableHeight, start + 1000));
            if (pendingAutomatic)
            {
                var pendingState = Smooth016State(viewer);
                pendingState.GetType().GetField("_started", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .SetValue(pendingState, System.Diagnostics.Stopwatch.GetTimestamp() - System.Diagnostics.Stopwatch.Frequency / 20);
                pendingState.GetType().GetMethod("OnRendering", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(pendingState, [null, EventArgs.Empty]);
                Assert((bool)pendingState.GetType().GetField("_scrollPending", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(pendingState)!,
                    "automatic frame was not pending before manual takeover");
            }
            var expected = Math.Clamp(start + 8 * step, 0, viewer.ScrollableHeight);
            for (var packet = 0; packet < 8; packet++)
                Assert(ScrollWheel(viewer, delta).Handled, "lyrics lost a routed manual wheel packet");
            var state = Smooth016State(viewer);
            var target = (double)state.GetType().GetProperty("TargetOffset", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(state)!;
            var pausedUntil = Toast040Field<DateTimeOffset>(f.Window, expanded ? "_expandedLyricsFollowUntil" : "_lyricsFollowUntil");
            File.WriteAllText(Path.Combine(_directory, $"lyrics-manual040-burst-{expanded}-{delta}-{followWasActive}-{pendingAutomatic}.json"),
                JsonSerializer.Serialize(new { expanded, delta, followWasActive, pendingAutomatic, packets = 8, step, start, target, expected, viewer.VerticalOffset }));
            Assert(Math.Abs(target - expected) < .15,
                $"manual burst lost distance: expanded={expanded}, delta={delta}, automatic={followWasActive}, target={target:F2}, expected={expected:F2}");
            Assert(pausedUntil > DateTimeOffset.UtcNow.AddSeconds(2.9), "wheel no longer suspends automatic following");
            await Until(() => !SmoothScroll.IsAnimating(viewer));
            await Dispatcher.Yield(DispatcherPriority.ContextIdle); f.Window.UpdateLayout();
            Assert(Math.Abs(viewer.VerticalOffset - expected) <= 1, "manual burst did not arrive at its accumulated target");
            Assert(f.Requests == 0, "manual scroll requested lyrics or audio from the network");
        }
    }

    private static async Task LyricsManual040Drag(bool expanded)
    {
        using var f = new LyricsManual040Fixture(expanded, "drag");
        await Dispatcher.Yield(DispatcherPriority.ContextIdle); f.Window.UpdateLayout();
        var viewer = LyricsUiVisuals<ScrollViewer>(f.List).First();
        var scrollbar = LyricsUiVisuals<ScrollBar>(viewer).First(bar => bar.Orientation == Orientation.Vertical);
        scrollbar.ApplyTemplate();
        var thumb = LyricsUiVisuals<Thumb>(scrollbar).First();
        thumb.RaiseEvent(new DragStartedEventArgs(0, 0) { RoutedEvent = Thumb.DragStartedEvent });
        viewer.ScrollToVerticalOffset(600); f.Window.UpdateLayout(); await Dispatcher.Yield(DispatcherPriority.ContextIdle);
        var offset = viewer.VerticalOffset;
        // A stationary held thumb may outlast 3.5 seconds. Expire the same idle
        // checkpoint without waiting; the active drag must still own the view.
        LyricsUiSet(f.Window, expanded ? "_expandedLyricsFollowUntil" : "_lyricsFollowUntil", DateTimeOffset.MinValue);
        LyricsUiInvoke(f.Window, "UpdateLyrics", 70d);
        await Task.Delay(120); f.Window.UpdateLayout();
        Assert(f.List.SelectedIndex == 70, "holding the scrollbar stopped the current lyric highlight");
        Assert(Math.Abs(viewer.VerticalOffset - offset) <= 1 && !SmoothScroll.IsAnimating(viewer),
            "automatic following took the viewport back while the scrollbar was held");
        thumb.RaiseEvent(new DragCompletedEventArgs(0, 0, false) { RoutedEvent = Thumb.DragCompletedEvent });
        var deadline = Toast040Field<DateTimeOffset>(f.Window, expanded ? "_expandedLyricsFollowUntil" : "_lyricsFollowUntil");
        Assert(deadline > DateTimeOffset.UtcNow.AddSeconds(2.9), "releasing the thumb did not start a fresh manual idle interval");
        LyricsUiSet(f.Window, expanded ? "_expandedLyricsFollowUntil" : "_lyricsFollowUntil", DateTimeOffset.MinValue);
        LyricsUiInvoke(f.Window, "UpdateLyrics", 71d);
        await Until(() => Math.Abs(viewer.VerticalOffset - offset) > 3);
        Assert(f.Requests == 0, "scrollbar following requested a provider");
    }

    private sealed class LyricsManual040Fixture : IDisposable
    {
        internal MainWindow Window { get; }
        internal ListBox List { get; }
        internal int Requests { get; private set; }
        private readonly MusicApi _api;
        private readonly NativePlayer _player;
        private readonly CancellationTokenSource _lyrics = new();
        internal LyricsManual040Fixture(bool expanded, string name)
        {
            var directory = Path.Combine(_directory, "lyrics-manual040-" + expanded + "-" + name);
            var http = new FakeHandler((_, _) => { Requests++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)); });
            _api = new MusicApi(http); _player = new NativePlayer(new AudioBuffer(Path.Combine(directory, "buffer"), http));
            var preferences = new PreferencesStore(Path.Combine(directory, "preferences"));
            preferences.Save(new PlayerPreferences { Volume = 0, UpdateCheckFrequency = UpdateCheckFrequency.Off });
            Window = new MainWindow(api: _api, store: new SessionStore(Path.Combine(directory, "session")), restore: false,
                diagnosticsDirectory: Path.Combine(directory, "diagnostics"),
                offline: new OfflineLibrary(Path.Combine(directory, "offline"), new AudioBuffer(Path.Combine(directory, "downloads"), http)),
                player: _player, preferences: preferences)
            { ShowActivated = false, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -18000, Top = 0 };
            Window.Show();
            LyricsUiSet(Window, "_current", new TrackRow { Id = "manual-private", Title = "Private lyrics fixture", Artist = "Fixture", Available = true });
            var lines = Enumerable.Range(0, 120).Select(i => new LyricLine(i, "Private lyric line " + i)).ToArray();
            LyricsUiSet(Window, "_lyricLines", lines); LyricsUiSet(Window, "_lyricsCts", _lyrics);
            var ordinary = Control<ListBox>(Window, "LyricsList"); ordinary.ItemsSource = lines.Select(l => l.Text).ToArray();
            if (expanded)
            {
                LyricsUiInvoke(Window, "SetExpandedPlayerVisible", true, false);
                LyricsUiInvoke(Window, "SetExpandedLyricsVisible", true, false);
                List = LyricsUiField<ListBox>(Window, "_expandedLyricsList")!;
            }
            else { LyricsUiInvoke(Window, "SetLyricsVisible", true); List = ordinary; }
            Window.UpdateLayout();
        }
        public void Dispose() { Window.Close(); _lyrics.Dispose(); _player.Dispose(); _api.Dispose(); }
    }
}
