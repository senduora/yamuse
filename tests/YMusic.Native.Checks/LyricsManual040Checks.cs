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
        await Run("LyricsManual040: scrollbar page buttons suspend follow during repeat and capture in both views", async () =>
        { await LyricsManual040Page(false); await LyricsManual040Page(true); });
        await Run("LyricsManual040: Enter seeks the focused timed line and leaves untimed text alone in both views", async () =>
        { await LyricsManual040KeyboardSeek(false); await LyricsManual040KeyboardSeek(true); });
        await Run("LyricsManual040: paused native selection is separate from the current timed line in both views", async () =>
        { await LyricsManual040PausedSelection(false); await LyricsManual040PausedSelection(true); });
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
        Assert(LyricsActiveIndex(f.Window) == 70, "holding the scrollbar stopped the current lyric highlight");
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

    private static async Task LyricsManual040Page(bool expanded)
    {
        using var f = new LyricsManual040Fixture(expanded, "page-scroll");
        await Dispatcher.Yield(DispatcherPriority.ContextIdle); f.Window.UpdateLayout();
        var viewer = LyricsUiVisuals<ScrollViewer>(f.List).First();
        var scrollbar = LyricsUiVisuals<ScrollBar>(viewer).First(bar => bar.Orientation == Orientation.Vertical);
        scrollbar.ApplyTemplate();
        var deadline = expanded ? "_expandedLyricsFollowUntil" : "_lyricsFollowUntil";
        var manual = expanded ? "_expandedLyricsManualDragging" : "_lyricsManualDragging";
        foreach (var name in new[] { "PageBefore", "PageAfter" })
        {
            var page = (RepeatButton)scrollbar.Template.FindName(name, scrollbar);
            LyricsUiSet(f.Window, deadline, DateTimeOffset.MinValue);
            page.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Assert(Toast040Field<DateTimeOffset>(f.Window, deadline) > DateTimeOffset.UtcNow.AddSeconds(2.9),
                "a routed page repeat did not postpone lyrics follow: " + name);
            page.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount) { RoutedEvent = Mouse.GotMouseCaptureEvent });
            LyricsUiSet(f.Window, deadline, DateTimeOffset.MinValue);
            Assert(Toast040Field<bool>(f.Window, manual), "holding a lyrics page button did not take manual control: " + name);
            page.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount) { RoutedEvent = Mouse.LostMouseCaptureEvent });
            Assert(!Toast040Field<bool>(f.Window, manual) && Toast040Field<DateTimeOffset>(f.Window, deadline) > DateTimeOffset.UtcNow.AddSeconds(2.9),
                "page release did not start the manual idle interval: " + name);
        }
        Assert(f.Requests == 0, "scrollbar page buttons requested a provider");
    }

    private static async Task LyricsManual040KeyboardSeek(bool expanded)
    {
        using var f = new LyricsManual040Fixture(expanded, "keyboard-seek");
        var player = LyricsUiField<NativePlayer>(f.Window, "_player")!;
        var media = new Uri(WriteWave("lyrics-keyboard-seek-" + expanded + ".wav", 30));
        f.List.ScrollIntoView(f.List.Items[12]); f.Window.UpdateLayout();
        await Dispatcher.Yield(DispatcherPriority.ContextIdle);
        var row = (ListBoxItem)f.List.ItemContainerGenerator.ContainerFromIndex(12);
        Assert(LyricsActiveIndex(f.Window) != 12, "fixture did not distinguish a focused line from the playback highlight");
        var previousFocus = Keyboard.FocusedElement as DependencyObject;
        try
        {
            Pk040Focus(row);
            var opening = player.OpenPausedAsync(media, CancellationToken.None);
            Assert(player.HasSource && !opening.IsCompleted && player.Duration == TimeSpan.Zero,
                "the pending decoder did not present a source without a seek range");
            Assert(!PressEnter().Handled && Toast040Field<double?>(f.Window, "_pendingSeek") is null,
                "Enter before the source reported a duration scheduled a seek");
            await opening;
            Assert(row.IsKeyboardFocused && PressEnter().Handled && Toast040Field<double?>(f.Window, "_pendingSeek") == 12,
                "Enter did not seek to the focused timed line");
            await Until(() => Math.Abs(player.Position.TotalSeconds - 12) < .4);
            LyricsUiInvoke(f.Window, "UpdateProgress");
            Assert(PressEnter(repeat: true).Handled && Toast040Field<double?>(f.Window, "_pendingSeek") is null,
                "holding Enter sought to the same lyric after the first seek completed");
            LyricsUiInvoke(f.Window, "SetLyricsRows", Array.Empty<LyricLine>(), LyricsUiLines(f.Window));
            f.List.ScrollIntoView(f.List.Items[12]);
            (System.Windows.Window.GetWindow(f.List) ?? f.Window).UpdateLayout();
            row = (ListBoxItem)f.List.ItemContainerGenerator.ContainerFromIndex(12);
            Pk040Focus(row);
            LyricsUiInvoke(f.Window, "UpdateProgress");
            Assert(!PressEnter().Handled && LyricsActiveIndex(f.Window) == -1 && !LyricsRowIsCurrent(f.List.Items[12])
                && Toast040Field<double?>(f.Window, "_pendingSeek") is null && !player.IsPlaying,
                "Enter on untimed lyrics moved or resumed playback");
        }
        finally { Pk040Focus(previousFocus); }

        KeyEventArgs PressEnter(bool repeat = false)
        {
            var args = new KeyEventArgs(new Pk040Keyboard(ModifierKeys.None), new Pk040Source(row), Environment.TickCount, Key.Enter)
            { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            if (repeat)
                typeof(KeyEventArgs).GetMethod("SetRepeat", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(args, [true]);
            row.RaiseEvent(args);
            return args;
        }
    }

    private static async Task LyricsManual040PausedSelection(bool expanded)
    {
        using var f = new LyricsManual040Fixture(expanded, "paused-selection", firstSecond: 1);
        var w = f.Window;
        var player = LyricsUiField<NativePlayer>(w, "_player")!;
        await player.OpenPausedAsync(new Uri(WriteWave("lyrics-paused-selection-" + expanded + ".wav", 30)), CancellationToken.None);
        LyricsUiInvoke(w, "UpdateProgress");
        var timer = LyricsUiField<DispatcherTimer>(w, "_lyricsTimer")!;
        var nativeKeyboard = typeof(ListBox).GetMethod("MakeKeyboardSelection", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var nativeMouse = typeof(ListBox).GetMethod("NotifyListItemClicked", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var previousFocus = Keyboard.FocusedElement as DependencyObject;
        try
        {
            Navigate(0, 1);
            Assert(f.List.SelectedIndex == 1 && !timer.IsEnabled && Math.Abs(player.Position.TotalSeconds) < .1
                && Toast040Field<double?>(w, "_pendingSeek") is null, "the paused arrow did not exercise native selection alone");
            await AssertAccent(-1, 1);

            var canceled = RowAt(2);
            LyricsUiMouse(canceled, down: true);
            nativeMouse.Invoke(f.List, [canceled, MouseButton.Left]);
            f.List.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount) { RoutedEvent = Mouse.MouseLeaveEvent });
            LyricsUiMouse(canceled, down: false);
            Assert(f.List.SelectedIndex == 2 && Toast040Field<double?>(w, "_pendingSeek") is null
                && Math.Abs(player.Position.TotalSeconds) < .1, "an abandoned click sought before the first line");
            await AssertAccent(-1, 2);

            LyricsUiInvoke(w, "CommitSeek", 5d);
            await Until(() => Math.Abs(player.Position.TotalSeconds - 5) < .35);
            LyricsUiInvoke(w, "UpdateProgress");
            Navigate(4, 5);
            Assert(f.List.SelectedIndex == 5 && Math.Abs(player.Position.TotalSeconds - 5) < .35,
                "native navigation did not move the paused seek candidate");
            await AssertAccent(4, 5);

            var candidate = RowAt(5);
            Assert(Preview(candidate, Key.Enter).Handled && Toast040Field<double?>(w, "_pendingSeek") == 6,
                "Enter did not commit the focused lyric timecode");
            await AssertAccent(5);
            await Until(() => Math.Abs(player.Position.TotalSeconds - 6) < .35);

            var clicked = RowAt(8);
            LyricsUiMouse(clicked, down: true);
            nativeMouse.Invoke(f.List, [clicked, MouseButton.Left]);
            LyricsUiMouse(clicked, down: false);
            Assert(Toast040Field<double?>(w, "_pendingSeek") == 9, "an accepted click did not commit its exact timestamp");
            await AssertAccent(8);
            await Until(() => Math.Abs(player.Position.TotalSeconds - 9) < .35);
            Assert(!player.IsPlaying && !timer.IsEnabled && f.Requests == 0, "line navigation resumed playback or called a provider");
        }
        finally { Pk040Focus(previousFocus); }

        ListBoxItem RowAt(int index)
        {
            f.List.ScrollIntoView(f.List.Items[index]);
            (System.Windows.Window.GetWindow(f.List) ?? w).UpdateLayout();
            return (ListBoxItem)f.List.ItemContainerGenerator.ContainerFromIndex(index);
        }
        void Navigate(int from, int to)
        {
            var source = RowAt(from);
            var target = RowAt(to);
            Pk040Focus(source);
            Assert(!Preview(source, Key.Down).Handled, "lyrics consumed the native arrow navigation");
            // Use WPF's own post-navigation selection step on the inactive test desktop.
            Pk040Focus(target);
            nativeKeyboard.Invoke(f.List, [target]);
        }
        static KeyEventArgs Preview(ListBoxItem row, Key key)
        {
            var args = new KeyEventArgs(new Pk040Keyboard(ModifierKeys.None), new Pk040Source(row), Environment.TickCount, key)
            { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            row.RaiseEvent(args);
            return args;
        }
        async Task AssertAccent(int expected, int? candidate = null)
        {
            await Dispatcher.Yield(DispatcherPriority.DataBind);
            Assert(LyricsActiveIndex(w) == expected && Enumerable.Range(0, f.List.Items.Count)
                .Where(index => LyricsRowIsCurrent(f.List.Items[index])).SequenceEqual(expected < 0 ? [] : new[] { expected }),
                "navigation changed the timecode accent without an accepted seek");
            if (expected >= 0) Assert(RowAt(expected).FontWeight == FontWeights.SemiBold, "the timed row lost its accent");
            if (candidate is int candidateIndex)
                Assert(RowAt(candidateIndex).FontWeight != FontWeights.SemiBold, "native selection borrowed the timed-line accent");
        }
    }

    private sealed class LyricsManual040Fixture : IDisposable
    {
        internal MainWindow Window { get; }
        internal ListBox List { get; }
        internal int Requests { get; private set; }
        private readonly MusicApi _api;
        private readonly NativePlayer _player;
        private readonly CancellationTokenSource _lyrics = new();
        internal LyricsManual040Fixture(bool expanded, string name, double firstSecond = 0)
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
            var lines = Enumerable.Range(0, 120).Select(i => new LyricLine(firstSecond + i, "Private lyric line " + i)).ToArray();
            LyricsUiInvoke(Window, "SetLyricsRows", lines, lines.Select(l => l.Text)); LyricsUiAssignPending(Window, _lyrics);
            var ordinary = Control<ListBox>(Window, "LyricsList");
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
