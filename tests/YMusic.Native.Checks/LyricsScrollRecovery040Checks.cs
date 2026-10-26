using System.Diagnostics;
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
    public static async Task CheckLyricsScrollRecovery040()
    {
        foreach (var (expanded, width, height) in new[] { (false, 1050d, 680d), (false, 1600d, 1000d), (true, 1600d, 1000d) })
        {
            var name = expanded ? "fullscreen" : "inline-" + width;
            await Run("LyricsRecovery040: variable wrapped virtual rows retain every wheel notch in " + name,
                () => Lr040Wheel(expanded, width, height, name));
        }
        foreach (var expanded in new[] { false, true })
        {
            var name = expanded ? "fullscreen" : "inline";
            await Run("LyricsRecovery040: precision coalesced wheel and reversal preserve visible motion in " + name,
                () => Lr040Packets(expanded, name));
            await Run("LyricsRecovery040: active playback pending following and a held scrollbar respect manual ownership in " + name,
                () => Lr040Ownership(expanded, name));
            await Run("LyricsRecovery040: an unrealized active row cannot steal immediate wheel takeover in " + name,
                () => Lr040UnrealizedFollow(expanded, name));
        }
    }

    private static async Task Lr040Wheel(bool expanded, double width, double height, string name)
    {
        using var f = new Lr040Fixture(expanded, width, height, name);
        await Task.Delay(150); f.Window.UpdateLayout();
        var viewer = LyricsUiVisuals<ScrollViewer>(f.List).First();
        Assert(viewer.IsLoaded && viewer.IsVisible && viewer.ScrollableHeight > 4000 && SmoothScroll.GetEnabled(viewer), "long lyrics did not realize a pixel scroll owner");
        Assert(Motion.CanAnimate && SystemParameters.WheelScrollLines > 0, "animation fixture requires the enabled system wheel animation setting");
        Assert(LyricsUiVisuals<ListBoxItem>(f.List).Count() is > 0 and < 90, "lyrics fixture lost virtualization");
        var samples = new List<object>();
        var extentChanges = 0;
        var anchors = Lr040Anchors(f.List, viewer);
        var travelled = 0d;
        var missingAnchors = 0;
        ScrollChangedEventHandler changed = (_, e) =>
        {
            if (!ReferenceEquals(e.OriginalSource, viewer)) return;
            if (e.ExtentHeightChange != 0) extentChanges++;
            var next = Lr040Anchors(f.List, viewer);
            var common = anchors.Keys.FirstOrDefault(next.ContainsKey, -1);
            if (common >= 0) travelled += anchors[common] - next[common];
            else missingAnchors++;
            anchors = next;
            var state = Smooth016State(viewer);
            samples.Add(new { t = Stopwatch.GetTimestamp(), viewer.VerticalOffset, viewer.ExtentHeight, e.VerticalChange, e.ExtentHeightChange, animating = SmoothScroll.IsAnimating(viewer), travelled,
                requested = state.GetType().GetField("_requestedOffset", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(state),
                pending = state.GetType().GetField("_scrollPending", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(state),
                target = state.GetType().GetProperty("TargetOffset", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(state) });
        };
        viewer.ScrollChanged += changed;
        try
        {
            var start = viewer.VerticalOffset;
            var step = SystemParameters.WheelScrollLines * 16d;
            for (var n = 0; n < 30; n++)
            {
                Assert(ScrollWheel(viewer, -120).Handled, "manual wheel packet was unhandled");
                await Task.Delay(35);
            }
            await Until(() => !SmoothScroll.IsAnimating(viewer));
            await Dispatcher.Yield(DispatcherPriority.ContextIdle); f.Window.UpdateLayout();
            var expected = start + 30 * step;
            File.WriteAllText(Path.Combine(_directory, "lyrics-recovery040-" + name + ".json"), JsonSerializer.Serialize(new
            {
                name, rows = f.List.Items.Count, font = f.List.FontFamily.Source, expanded, width, height,
                actualWidth = f.List.ActualWidth, actualHeight = f.List.ActualHeight,
                start, expected, actual = viewer.VerticalOffset, travelled, extentChanges, missingAnchors, samples,
                realized = LyricsUiVisuals<ListBoxItem>(f.List).Count(), networkRequests = f.Requests
            }, new JsonSerializerOptions { WriteIndented = true }));
            Assert(extentChanges > 0, "fixture did not exercise changing virtualized extent");
            Assert(missingAnchors == 0, "wheel fixture lost all common visible-row anchors");
            Assert(Math.Abs(travelled - 30 * step) <= 1.1,
                $"virtualized wheel lost visible distance: {name}, expected={30 * step:F2}, actual={travelled:F2}, offset={viewer.VerticalOffset:F2}, extentChanges={extentChanges}");
            Assert(f.Requests == 0, "manual scrolling contacted a provider");
        }
        finally { viewer.ScrollChanged -= changed; }
    }

    private static Dictionary<int, double> Lr040Anchors(ListBox list, ScrollViewer viewer) => LyricsUiVisuals<ListBoxItem>(list)
        .Select(item => (index: list.ItemContainerGenerator.IndexFromContainer(item), y: item.TranslatePoint(new Point(), viewer).Y))
        .Where(item => item.index >= 0).ToDictionary(item => item.index, item => item.y);

    private static async Task Lr040Packets(bool expanded, string name)
    {
        var records = new List<object>();
        foreach (var (delta, packets, batch) in new[] { (-15, 240, 8), (-1, 900, 3), (-360, 18, 1) })
        {
            // Each device cadence starts with unknown row heights. Reusing the
            // previous panel would hide extent refinement behind its size cache.
            using var f = new Lr040Fixture(expanded, 1050, 680, "packets-" + name + delta);
            await Task.Delay(100); f.Window.UpdateLayout();
            var viewer = LyricsUiVisuals<ScrollViewer>(f.List).First();
            using var trace = new Lr040Trace(f.List, viewer);
            for (var packet = 0; packet < packets; packet++)
            {
                Assert(ScrollWheel(viewer, delta).Handled, "coalesced or precision wheel packet was unhandled");
                if ((packet + 1) % batch == 0) await Task.Delay(delta == -1 ? 2 : 25);
            }
            await Until(() => !SmoothScroll.IsAnimating(viewer));
            await Task.Delay(40);
            var expected = -delta / 120d * SystemParameters.WheelScrollLines * 16d * packets;
            records.Add(new { delta, packets, batch, expected, trace.Travelled, trace.ExtentChanges, trace.MissingAnchors, viewer.VerticalOffset });
            File.WriteAllText(Path.Combine(_directory, "lyrics-recovery040-packets-" + name + ".json"), JsonSerializer.Serialize(records, new JsonSerializerOptions { WriteIndented = true }));
            Assert(trace.MissingAnchors == 0, "packet fixture lost all common visible-row anchors");
            Assert(Math.Abs(trace.Travelled - expected) <= 1.1, $"packet distance changed: {name}, delta={delta}, expected={expected:F2}, actual={trace.Travelled:F2}");
            Assert(delta >= -120 || trace.ExtentChanges > 0, "coalesced wheel did not exercise dynamic extent");
            if (delta == -360)
            {
                for (var packet = 0; packet < 6; packet++) ScrollWheel(viewer, -120);
                await Task.Delay(50);
                var before = trace.Travelled;
                Assert(ScrollWheel(viewer, 120).Handled, "reversal escaped the lyrics viewer");
                await Until(() => !SmoothScroll.IsAnimating(viewer)); await Task.Delay(30);
                Assert(Math.Abs((trace.Travelled - before) + SystemParameters.WheelScrollLines * 16d) <= 1.1,
                    "reversal paid off the pending forward distance or lost the backward notch");
            }
            Assert(f.Requests == 0, "packet fixture requested a provider");
        }
    }

    private static async Task Lr040Ownership(bool expanded, string name)
    {
        using var f = new Lr040Fixture(expanded, 1050, 680, "ownership-" + name);
        await f.StartPlayback();
        var viewer = LyricsUiVisuals<ScrollViewer>(f.List).First();
        LyricsUiInvoke(f.Window, expanded ? "PauseExpandedLyricsFollow" : "PauseLyricsFollow");
        viewer.ScrollToVerticalOffset(500); await Task.Delay(100);
        SmoothScroll.ScrollTo(viewer, viewer.VerticalOffset + 900);
        var state = Smooth016State(viewer);
        state.GetType().GetField("_started", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(state,
            Stopwatch.GetTimestamp() - Stopwatch.Frequency / 20);
        state.GetType().GetMethod("OnRendering", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(state, [null, EventArgs.Empty]);
        Assert((bool)state.GetType().GetField("_scrollPending", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(state)!, "automatic offset was not pending before wheel takeover");
        using (var trace = new Lr040Trace(f.List, viewer))
        {
            for (var packet = 0; packet < 8; packet++) Assert(ScrollWheel(viewer, -120).Handled, "active-playback manual wheel escaped");
            await Until(() => !SmoothScroll.IsAnimating(viewer)); await Task.Delay(40);
            Assert(Math.Abs(trace.Travelled - 8 * SystemParameters.WheelScrollLines * 16d) <= 1.1,
                "a queued automatic frame stole or canceled manual wheel distance");
        }
        var scrollbar = LyricsUiVisuals<ScrollBar>(viewer).First(bar => bar.Orientation == Orientation.Vertical);
        scrollbar.ApplyTemplate();
        var thumb = LyricsUiVisuals<Thumb>(scrollbar).First();
        thumb.RaiseEvent(new DragStartedEventArgs(0, 0) { RoutedEvent = Thumb.DragStartedEvent });
        viewer.ScrollToVerticalOffset(1100); await Task.Delay(100);
        using (var trace = new Lr040Trace(f.List, viewer))
        {
            LyricsUiSet(f.Window, expanded ? "_expandedLyricsFollowUntil" : "_lyricsFollowUntil", DateTimeOffset.MinValue);
            f.Player.Seek(TimeSpan.FromSeconds(60));
            LyricsUiInvoke(f.Window, "UpdateLyrics", 60d);
            await Task.Delay(250);
            Assert(f.Player.IsPlaying && f.List.SelectedIndex >= 240, "manual drag paused playback or the active lyric highlight");
            Assert(Math.Abs(trace.Travelled) <= 1.1 && !SmoothScroll.IsAnimating(viewer), "held scrollbar lost ownership after the follow deadline");
        }
        thumb.RaiseEvent(new DragCompletedEventArgs(0, 0, false) { RoutedEvent = Thumb.DragCompletedEvent });
        var deadline = Toast040Field<DateTimeOffset>(f.Window, expanded ? "_expandedLyricsFollowUntil" : "_lyricsFollowUntil");
        Assert(deadline > DateTimeOffset.UtcNow.AddSeconds(2.9), "drag release did not renew manual pause");
        var manualOffset = viewer.VerticalOffset;
        await Task.Delay(180);
        Assert(Math.Abs(viewer.VerticalOffset - manualOffset) <= 1.1, "a stale follow tick ran immediately after drag release");
        LyricsUiSet(f.Window, expanded ? "_expandedLyricsFollowUntil" : "_lyricsFollowUntil", DateTimeOffset.MinValue);
        await Until(() => Math.Abs(viewer.VerticalOffset - manualOffset) > 10);
        Assert(f.Player.IsPlaying && f.Requests == 0, "resuming lyric following stopped playback or contacted a provider");
        File.WriteAllText(Path.Combine(_directory, "lyrics-recovery040-ownership-" + name + ".json"), JsonSerializer.Serialize(new
        { activePlayback = true, queuedAutomaticOffset = true, heldPastDeadline = true, highlightDuringDrag = true, pauseAfterRelease = true,
            resumedFollowing = true, networkRequests = f.Requests, inputEventsWereRoutedInProcess = true, physicalInputTested = false }));
    }

    private static async Task Lr040UnrealizedFollow(bool expanded, string name)
    {
        using var f = new Lr040Fixture(expanded, 1050, 680, "unrealized-" + name);
        await f.StartPlayback();
        // The media clock remains active; hold only its timer dispatch so that
        // the same active-row transition can be placed immediately before input.
        LyricsUiField<DispatcherTimer>(f.Window, "_lyricsTimer")!.Stop();
        var viewer = LyricsUiVisuals<ScrollViewer>(f.List).First();
        SmoothScroll.CancelAnimation(viewer); viewer.ScrollToTop(); await Task.Delay(80);
        Assert(f.List.ItemContainerGenerator.ContainerFromIndex(300) is null, "follow target was already realized");
        LyricsUiSet(f.Window, expanded ? "_expandedLyricsFollowUntil" : "_lyricsFollowUntil", DateTimeOffset.MinValue);
        f.Player.Seek(TimeSpan.FromSeconds(75));
        LyricsUiInvoke(f.Window, "UpdateLyrics", 75d);
        using var trace = new Lr040Trace(f.List, viewer);
        for (var packet = 0; packet < 8; packet++) Assert(ScrollWheel(viewer, -120).Handled, "unrealized-row manual input escaped");
        await Until(() => !SmoothScroll.IsAnimating(viewer)); await Task.Delay(100);
        File.WriteAllText(Path.Combine(_directory, "lyrics-recovery040-unrealized-" + name + ".json"), JsonSerializer.Serialize(new
        { trace.Travelled, trace.MissingAnchors, trace.ExtentChanges, viewer.VerticalOffset, f.Player.IsPlaying }));
        Assert(trace.MissingAnchors == 0 && Math.Abs(trace.Travelled - 8 * SystemParameters.WheelScrollLines * 16d) <= 1.1,
            "a pending unrealized-row BringIntoView stole the manual viewport");
        Assert(f.Player.IsPlaying && f.Requests == 0, "unrealized-row following changed playback or contacted a provider");
    }

    private sealed class Lr040Trace : IDisposable
    {
        private readonly ListBox _list;
        private readonly ScrollViewer _viewer;
        private Dictionary<int, double> _anchors;
        internal double Travelled { get; private set; }
        internal int ExtentChanges { get; private set; }
        internal int MissingAnchors { get; private set; }
        internal Lr040Trace(ListBox list, ScrollViewer viewer)
        { _list = list; _viewer = viewer; _anchors = Lr040Anchors(list, viewer); viewer.ScrollChanged += Changed; }
        private void Changed(object sender, ScrollChangedEventArgs args)
        {
            if (!ReferenceEquals(args.OriginalSource, _viewer)) return;
            var next = Lr040Anchors(_list, _viewer);
            var common = _anchors.Keys.FirstOrDefault(next.ContainsKey, -1);
            if (common >= 0) Travelled += _anchors[common] - next[common];
            else MissingAnchors++;
            _anchors = next;
            if (args.ExtentHeightChange != 0) ExtentChanges++;
        }
        public void Dispose() => _viewer.ScrollChanged -= Changed;
    }

    private sealed class Lr040Fixture : IDisposable
    {
        internal MainWindow Window { get; }
        internal ListBox List { get; }
        internal int Requests { get; private set; }
        private readonly MusicApi _api;
        private readonly NativePlayer _player;
        internal NativePlayer Player => _player;
        private readonly string _name;
        private readonly CancellationTokenSource _lyrics = new();
        internal Lr040Fixture(bool expanded, double width, double height, string name)
        {
            _name = name;
            var directory = Path.Combine(_directory, "lyrics-recovery040-fixture-" + name);
            var http = new FakeHandler((_, _) => { Requests++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)); });
            _api = new MusicApi(http); _player = new NativePlayer(new AudioBuffer(Path.Combine(directory, "buffer"), http));
            var preferences = new PreferencesStore(Path.Combine(directory, "preferences"));
            preferences.Save(new PlayerPreferences { Volume = 0, UpdateCheckFrequency = UpdateCheckFrequency.Off });
            Window = new MainWindow(api: _api, store: new SessionStore(Path.Combine(directory, "session")), restore: false,
                diagnosticsDirectory: Path.Combine(directory, "diagnostics"),
                offline: new OfflineLibrary(Path.Combine(directory, "offline"), new AudioBuffer(Path.Combine(directory, "downloads"), http)),
                player: _player, preferences: preferences)
            { Width = width, Height = height, ShowActivated = false, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -18000, Top = 0 };
            Window.Show();
            LyricsUiSet(Window, "_current", new TrackRow { Id = "recovery-private", Title = "Локальный текст", Artist = "Fixture", Available = true });
            var sentences = new[]
            {
                "Тихий вечер за окном",
                "Город засыпает, и по мокрой мостовой неторопливо возвращаются последние прохожие",
                "",
                "Мы сохраним этот свет",
                "На другой стороне реки ещё горят огни: они рисуют на воде длинные дорожки и напоминают нам о доме, который ждёт за поворотом старой улицы",
                "Дождь",
                "Не спеши, посмотри на дорогу, где каждый знакомый фонарь освещает шаги и где ветер приносит далёкие звуки вечерних поездов; мы продолжим идти до рассвета"
            };
            var lines = Enumerable.Range(0, 420).Select(i => new LyricLine(i * .25, i < 16 ? "Вечер " + i : sentences[(i / 9 + i) % sentences.Length])).ToArray();
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
        internal async Task StartPlayback()
        {
            _player.Volume = 0;
            await _player.OpenAsync(new Uri(WriteWave("lyrics-recovery040-" + _name + ".wav", 110)), CancellationToken.None);
            LyricsUiInvoke(Window, "UpdateLyricsTimer");
            await Until(() => _player.IsPlaying && LyricsUiField<DispatcherTimer>(Window, "_lyricsTimer")!.IsEnabled);
        }
        public void Dispose() { Window.Close(); _lyrics.Dispose(); _player.Dispose(); _api.Dispose(); }
    }
}
