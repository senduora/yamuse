using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using YMusic.Native;

internal static partial class Program
{
    private static async Task CheckPlaybackKeys040()
    {
        await Run("PlaybackKeys040: routed F11 toggles the separate fullscreen host and Escape closes it", Pk040Fullscreen);
        await Run("PlaybackKeys040: cached fullscreen keeps one media subscription until final close", Pk040FullscreenMediaRegistration);
        await Run("PlaybackKeys040: raw and routed Previous remain available during fullscreen collapse", Pk040MediaDuringCollapse);
        await Run("PlaybackKeys040: text password rich text editable combo and modifier keys retain input", Pk040Input);
        await Run("PlaybackKeys040: retired local search shortcut and smart navigation stay absent", Pk040RetiredLocalSearch);
        await Run("PlaybackKeys040: main and numpad volume clamp persist and mute restores the last nonzero level", Pk040Volume);
        await Run("PlaybackKeys040: routed P Space brackets and media keys use shared asynchronous playback", Pk040Transport);
        await Run("PlaybackKeys040: Previous uses accepted pending and restored positions", Pk040AcceptedPrevious);
        await Run("PlaybackKeys040: focused buttons keep native Space and Enter activation", Pk040ButtonActivation);
        await Run("PlaybackKeys040: autorepeat does not toggle playback mute or fullscreen", Pk040Repeat);
        await Run("PlaybackKeys040: Enter and double click activate the exact available row while Listen can fall back", Pk040TrackActivation);
        await Run("PlaybackKeys040: catalog Enter opens the focused card and ignores repeat", Pk040CatalogEnter);
        await Run("PlaybackKeys040: queue undo leaves the hidden queue alone under overlays", Pk040QueueUndoOverlays);
        await Run("PlaybackKeys040: SMTC Play and Pause are idempotent and its inactive session allows local media keys", Pk040WindowsMediaTransport);
        await Run("PlaybackKeys040: application media commands reach focused main fullscreen and mini controls once", Pk040ApplicationMediaCommands);
        await Run("PlaybackKeys040: L captures current track and reuses confirmed account mutation guards", Pk040Like);
        File.WriteAllText(Path.Combine(_directory, "playback-keys040-scope.json"), JsonSerializer.Serialize(new
        {
            fixtureWindowsShown = false, nativeHandlesCreated = false, desktopInputUsed = false,
            realAccountUsed = false, externalNetworkRequests = 0, keyboardLayoutChanged = false,
            keyEvents = "WPF routed PreviewKeyDown and MediaCommands targeted at fixture controls",
            audio = "private generated WAV at zero volume for transport checks",
            fullscreenClose = "production request route; never-shown surface uses its immediate fallback"
        }, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static Task Pk040RetiredLocalSearch()
    {
        using var f = new Pk040Fixture("retired-local-search");
        f.Account();
        var navigation = (StackPanel)((ScrollViewer)f.Window.FindName("NavigationScroll")).Content;
        Assert(navigation.Children.OfType<Button>().All(button => button.Name is not ("QuickAccessNav" or "SmartPlaylistsNav")),
            "retired feature remained in navigation");
        Assert(f.Window.FindName("SearchNav") is Button, "normal service search was removed with local search");
        var windows = Application.Current.Windows.Count;
        var requests = f.Http.Requests;
        Assert(!f.Key(Key.K, modifiers: ModifierKeys.Control).Handled, "retired Ctrl K was still consumed");
        var text = new TextBox { Text = "Поиск" };
        Assert(!f.Key(Key.K, text, ModifierKeys.Control).Handled && text.Text == "Поиск", "Ctrl K intercepted text input");
        Assert(Application.Current.Windows.Count == windows && f.Http.Requests == requests && !f.Player.HasSource,
            "retired shortcut opened a dialog or started network/audio");
        return Task.CompletedTask;
    }

    private static Task Pk040Fullscreen()
    {
        using var f = new Pk040Fixture("fullscreen"); f.SetCurrent(f.Rows[0]);
        var before = (f.Window.WindowState, f.Window.WindowStyle, f.Window.Left, f.Window.Top, f.Window.Width, f.Window.Height);
        Assert(f.Key(Key.F11).Handled && Pk040Field<object?>(f.Window, "_expandedFullscreenHost") is not null, "F11 did not open fullscreen");
        var host = f.Fullscreen;
        Assert(host != f.Window && !host.IsVisible && new WindowInteropHelper(host).Handle == 0, "F11 created a visible or native fixture window");
        var expandedVolume = f.ExpandedVolume;
        Assert(f.Key(Key.Add, expandedVolume).Handled && f.Volume.Value == 10, "fullscreen did not forward numpad volume");
        Assert(f.Key(Key.F11, host).Handled && Pk040Field<object?>(f.Window, "_expandedFullscreenHost") is null, "fullscreen F11 did not close");
        Assert(f.Key(Key.F11).Handled && f.Key(Key.Escape, f.Fullscreen).Handled
            && Pk040Field<object?>(f.Window, "_expandedFullscreenHost") is null, "fullscreen Escape did not close");
        Assert(before == (f.Window.WindowState, f.Window.WindowStyle, f.Window.Left, f.Window.Top, f.Window.Width, f.Window.Height), "shortcuts changed main placement");
        f.SetCurrent(null); f.Key(Key.F11);
        Assert(Pk040Field<object?>(f.Window, "_expandedFullscreenHost") is null, "F11 opened without a current track");
        return Task.CompletedTask;
    }

    private static Task Pk040Input()
    {
        using var f = new Pk040Fixture("input"); f.SetCurrent(f.Rows[0]);
        var root = (Grid)f.Window.Content;
        var password = new PasswordBox(); var rich = new RichTextBox();
        var run = new Run("private input"); rich.Document.Blocks.Add(new Paragraph(run));
        var combo = new ComboBox { IsEditable = true, Width = 200 };
        var chooser = new ComboBox { IsEditable = false, Width = 200 };
        var choice = new ComboBoxItem { Content = "Private font choice" }; chooser.Items.Add(choice);
        root.Children.Add(password); root.Children.Add(rich); root.Children.Add(combo); root.Children.Add(chooser); f.Layout();
        var filter = Control<TextBox>(f.Window, "FilterBox"); filter.IsEnabled = true;
        var catalog = Control<TextBox>(f.Window, "CatalogQuery");
        var inputs = new List<UIElement> { filter, catalog, password, rich, combo, chooser, choice };
        combo.ApplyTemplate();
        if (combo.Template.FindName("PART_EditableTextBox", combo) is TextBox editor) inputs.Add(editor);
        foreach (var input in inputs)
            foreach (var key in new[] { Key.P, Key.M, Key.L, Key.OemOpenBrackets, Key.OemCloseBrackets, Key.OemPlus, Key.OemMinus, Key.Add, Key.Subtract, Key.Space })
                Assert(!f.Key(key, input).Handled, input.GetType().Name + " lost " + key + " to playback");
        Assert((bool)typeof(MainWindow).GetMethod("IsPlaybackTextInput", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [run])!, "rich text content ancestor lost its editor guard");
        var win = new Pk040Keyboard(ModifierKeys.Windows);
        Assert(win.IsKeyDown(Key.LWin) && win.IsKeyDown(Key.RWin), "isolated keyboard did not expose the Windows key state");
        File.WriteAllText(Path.Combine(_directory, "playback-keys040-win-modifier.json"), JsonSerializer.Serialize(new
        { reportedModifiers = win.Modifiers.ToString(), leftWindowsDown = win.IsKeyDown(Key.LWin), rightWindowsDown = win.IsKeyDown(Key.RWin) }));
        foreach (var modifiers in new[] { ModifierKeys.Control, ModifierKeys.Alt, ModifierKeys.Windows, ModifierKeys.Control | ModifierKeys.Shift })
            foreach (var key in new[] { Key.P, Key.M, Key.L, Key.OemOpenBrackets, Key.OemCloseBrackets, Key.Add, Key.Subtract, Key.Space, Key.F11, Key.Escape, Key.F })
                Assert(!f.Key(key, modifiers: modifiers).Handled, modifiers + " combination was consumed: " + key);
        foreach (var key in new[] { Key.Left, Key.Right, Key.Up, Key.Down, Key.Home, Key.End, Key.PageUp, Key.PageDown, Key.Tab })
            Assert(!f.Key(key).Handled, "existing navigation was consumed: " + key);
        Assert(!f.Key(Key.Space, f.Volume).Handled && !f.Key(Key.Right, f.Volume).Handled, "slider lost Space or arrow behavior");
        Assert(f.Volume.Value == 0 && !f.Player.HasSource && f.Http.Writes.Count == 0
            && Pk040Field<PlaybackQueue>(f.Window, "_playbackQueue").CurrentIndex == 0, "guarded input changed music state");
        // Preserve the existing Escape order even while settings/lyrics are open.
        Pk040Set(f.Window, "_settingsOpen", true); f.Key(Key.Escape);
        Assert(!Pk040Field<bool>(f.Window, "_settingsOpen"), "Escape no longer closes settings");
        Pk040Set(f.Window, "_lyricsOpen", true); f.Key(Key.Escape);
        Assert(!Pk040Field<bool>(f.Window, "_lyricsOpen"), "Escape no longer closes lyrics");
        return Task.CompletedTask;
    }

    private static Task Pk040Volume()
    {
        using var f = new Pk040Fixture("volume"); f.SetCurrent(f.Rows[0]); f.Key(Key.F11);
        var expanded = f.ExpandedVolume;
        void Level(double expected)
        {
            Assert(Math.Abs(f.Volume.Value - expected) < .001 && Math.Abs(expanded.Value - expected) < .001
                && Math.Abs(f.Player.Volume - expected / 100) < .001 && Math.Abs(f.Preferences.Load().Volume - expected / 100) < .001,
                "main expanded native and persisted volume diverged at " + expected);
        }
        f.Volume.Value = 37; Level(37);
        Assert(f.Key(Key.M, expanded).Handled, "focused fullscreen volume suppressed mute"); Level(0);
        f.Key(Key.M, f.Fullscreen); Level(37);
        f.Key(Key.OemPlus, modifiers: ModifierKeys.Shift); Level(47);
        f.Key(Key.OemMinus); Level(37);
        f.Key(Key.Add, expanded); Level(47); f.Key(Key.Subtract, expanded); Level(37);
        for (var index = 0; index < 12; index++) f.Key(Key.Add); Level(100);
        f.Key(Key.M); Level(0); f.Key(Key.M); Level(100);
        f.Volume.Value = 63; f.Volume.Value = 0; f.Key(Key.M); Level(63);
        for (var index = 0; index < 12; index++) f.Key(Key.Subtract); Level(0);
        f.Key(Key.M); Level(3); // The last nonzero step before reaching zero was 3%.
        Pk040Set(f.Window, "_settingsOpen", true);
        f.Key(Key.Add); Level(13); f.Key(Key.M); Level(0); f.Key(Key.M); Level(13);
        Pk040Set(f.Window, "_settingsOpen", false);
        using var initialZero = new Pk040Fixture("initial-zero"); initialZero.Key(Key.M);
        Assert(initialZero.Volume.Value == 50 && initialZero.Player.Volume == .5 && initialZero.Preferences.Load().Volume == .5, "initial zero volume has no useful mute restore fallback");
        return Task.CompletedTask;
    }

    private static async Task Pk040Transport()
    {
        using var f = new Pk040Fixture("transport"); f.SetCurrent(f.Rows[1]);
        Pk040Set(f.Window, "_loadingStream", true); Assert(f.Key(Key.P).Handled && !f.Player.HasSource, "P bypassed stream-loading guard");
        Pk040Set(f.Window, "_loadingStream", false);
        Assert(f.Key(Key.P).Handled, "P was not routed");
        await Until(() => f.Player.IsPlaying && !Pk040Field<bool>(f.Window, "_loadingStream"));
        Assert(ReferenceEquals(Pk040Field<TrackRow?>(f.Window, "_current"), f.Rows[1]) && f.Player.Volume == 0, "P played a different current or audible fixture");
        f.Key(Key.Space); Assert(!f.Player.IsPlaying, "existing Space did not pause");
        f.Key(Key.F11); Assert(f.Key(Key.P, f.Fullscreen).Handled && f.Player.IsPlaying, "fullscreen P did not resume");
        foreach (var target in new UIElement[] { f.Window, f.Fullscreen })
        {
            f.Player.Seek(TimeSpan.FromSeconds(8));
            Assert(f.Key(Key.OemOpenBrackets, target).Handled, "[ was not handled");
            await Until(() => !Pk040Field<bool>(f.Window, "_loadingStream") && ReferenceEquals(Pk040Field<TrackRow?>(f.Window, "_current"), f.Rows[0]));
            Assert(Pk040Field<PlaybackQueue>(f.Window, "_playbackQueue").CurrentIndex == 0 && f.Player.Position.TotalSeconds < 3, "[ did not mean previous track");
            Assert(f.Key(Key.OemCloseBrackets, target).Handled, "] was not handled");
            await Until(() => !Pk040Field<bool>(f.Window, "_loadingStream") && ReferenceEquals(Pk040Field<TrackRow?>(f.Window, "_current"), f.Rows[1]));
            Assert(Pk040Field<PlaybackQueue>(f.Window, "_playbackQueue").CurrentIndex == 1, "] did not mean next track");
        }
        Assert(f.Key(Key.MediaNextTrack, f.Fullscreen).Handled, "media next was not handled");
        await Until(() => !Pk040Field<bool>(f.Window, "_loadingStream") && ReferenceEquals(Pk040Field<TrackRow?>(f.Window, "_current"), f.Rows[2]));
        Assert(Pk040Field<PlaybackQueue>(f.Window, "_playbackQueue").CurrentIndex == 2, "media next changed direction");
        var q = Pk040Field<PlaybackQueue>(f.Window, "_playbackQueue");
        await CheckMediaPrevious(f.ExpandedVolume);
        Assert(f.Key(Key.Escape, f.Fullscreen).Handled, "fullscreen did not close before main-window media checks");
        await CheckMediaPrevious(f.Volume);
        Assert(f.Key(Key.MediaPreviousTrack).Handled, "media previous was not handled at the beginning");
        await Until(() => q.CurrentIndex == 1 && !Pk040Field<bool>(f.Window, "_loadingStream"));
        f.Key(Key.P); Assert(!f.Player.IsPlaying && f.Http.Requests == 0, "P pause performed network or failed");

        async Task CheckMediaPrevious(UIElement focus)
        {
            foreach (var raw in new[] { false, true })
            {
                var token = q.CurrentOccurrenceToken;
                f.Player.Seek(TimeSpan.FromSeconds(8));
                await Until(() => f.Player.Position.TotalSeconds > 6);
                Previous();
                Assert(q.CurrentOccurrenceToken == token, "media previous moved off the current occurrence past the threshold");
                await Until(() => f.Player.Position.TotalSeconds < 2);
                Previous();
                await Until(() => q.CurrentIndex == 1 && f.Player.IsPlaying && !Pk040Field<bool>(f.Window, "_loadingStream"));
                Assert(f.Key(Key.MediaNextTrack, focus).Handled, "media next did not restore the successor for the other route");
                await Until(() => q.CurrentOccurrenceToken == token && f.Player.IsPlaying && !Pk040Field<bool>(f.Window, "_loadingStream"));

                void Previous()
                {
                    if (raw) Assert(f.Key(Key.MediaPreviousTrack, focus).Handled, "raw Previous was ignored");
                    else
                    {
                        Assert(MediaCommands.PreviousTrack.CanExecute(null, focus), "routed Previous was ignored");
                        MediaCommands.PreviousTrack.Execute(null, focus);
                    }
                }
            }
        }
    }

    private static async Task Pk040AcceptedPrevious()
    {
        using (var f = new Pk040Fixture("accepted-previous-pending"))
        {
            var w = f.Window;
            var q = Pk040Field<PlaybackQueue>(w, "_playbackQueue");
            await w.PlayTrackAsync(f.Rows[0]);
            await Call(w, "AdvanceAsync", 1, false);
            var target = q.CurrentOccurrenceToken;
            var sample = TimeSpan.FromSeconds(1);
            f.Player.ReadWpfPositionForChecks = _ => sample;
            Pk040Call(w, "CommitSeek", 8d);
            Assert(Pk040Field<double?>(w, "_pendingSeek") == 8d && f.Player.Position == sample,
                "fixture did not hold a pending seek ahead of the native sample");
            Assert(f.Key(Key.MediaPreviousTrack).Handled && q.CurrentOccurrenceToken == target
                && Pk040Field<double?>(w, "_pendingSeek") == 0d,
                "raw Previous abandoned the accepted seek instead of rewinding the same occurrence");
            sample = TimeSpan.FromSeconds(8);
            Assert(MediaCommands.PreviousTrack.CanExecute(null, f.Volume), "routed Previous is unavailable");
            MediaCommands.PreviousTrack.Execute(null, f.Volume);
            await Until(() => ReferenceEquals(q.Current, f.Rows[0]) && f.Player.IsPlaying);
            Assert(q.CurrentOccurrenceToken != target, "second Previous used stale native position instead of the accepted rewind");
        }

        using (var f = new Pk040Fixture("accepted-previous-restored"))
        {
            var w = f.Window;
            f.SetCurrent(f.Rows[1]);
            var q = Pk040Field<PlaybackQueue>(w, "_playbackQueue");
            var target = q.CurrentOccurrenceToken;
            Pk040Set(w, "_restoredPlaybackOccurrence", target);
            Pk040Set(w, "_restoredPlaybackSeconds", 17d);
            Pk040Call(w, "UpdateControls");
            Assert(Control<TextBlock>(w, "PositionText").Text == "0:17" && !f.Player.HasSource, "fixture did not restore the paused offset");
            await Call(w, "HandleShellCommandAsync", ShellAction.Previous);
            Assert(q.CurrentOccurrenceToken == target && Pk040Field<double?>(w, "_restoredPlaybackSeconds") == 0d
                && Control<TextBlock>(w, "PositionText").Text == "0:00" && !f.Player.HasSource && !f.Player.IsPlaying,
                "Previous opened or changed the exact source-free occurrence instead of rewinding it");
        }
    }

    private static async Task Pk040WindowsMediaTransport()
    {
        using var f = new Pk040Fixture("windows-media");
        var media = (WindowsIntegration)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(WindowsIntegration));
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        typeof(WindowsIntegration).GetField("_window", flags)!.SetValue(media, f.Window);
        typeof(WindowsIntegration).GetField("<IsAvailable>k__BackingField", flags)!.SetValue(media, true);
        Pk040Set(f.Window, "_windows", media);
        f.SetCurrent(f.Rows[1]);
        var text = new TextBox { Text = "Поиск" };
        var combo = new ComboBox { ItemsSource = new[] { "Треки", "Подборки" }, SelectedIndex = 0 };
        var root = (Grid)f.Window.Content;
        root.Children.Add(text); root.Children.Add(combo); f.Layout();
        Assert(!media.HandlesMediaKeys && f.Key(Key.MediaPlayPause, text).Handled, "inactive SMTC lost Play on a text editor");
        await Until(() => f.Player.IsPlaying && !Pk040Field<bool>(f.Window, "_loadingStream"));
        Assert(f.Key(Key.MediaNextTrack, combo).Handled, "inactive SMTC lost Next on a combo box");
        await Until(() => !Pk040Field<bool>(f.Window, "_loadingStream") && ReferenceEquals(Pk040Field<TrackRow?>(f.Window, "_current"), f.Rows[2]));
        Assert(f.Key(Key.MediaPreviousTrack, f.Volume).Handled, "inactive SMTC lost Previous on a slider");
        await Until(() => !Pk040Field<bool>(f.Window, "_loadingStream") && ReferenceEquals(Pk040Field<TrackRow?>(f.Window, "_current"), f.Rows[1]));
        Assert(text.Text == "Поиск" && combo.SelectedIndex == 0, "a media key changed text or combo selection");

        Assert(!media.HandlesMediaKeys, "a disconnected SMTC reserved local keys");
        // Simulate a bridge binding without creating a window handle or registering with Windows.
        typeof(WindowsIntegration).GetField("<WindowHandle>k__BackingField", flags)!.SetValue(media, (nint)1);
        Assert(media.HandlesMediaKeys && !f.Key(Key.MediaPlayPause, text).Handled && !f.Key(Key.MediaNextTrack, combo).Handled
            && !f.Key(Key.MediaPreviousTrack, f.Volume).Handled && f.Player.IsPlaying, "active SMTC allowed a duplicate local media action");
        var commands = new List<DesktopMediaAction>();
        media.Command += commands.Add;
        var dispatch = typeof(WindowsIntegration).GetMethod("DispatchButton", flags)!;
        dispatch.Invoke(media, [Windows.Media.SystemMediaTransportControlsButton.Play]);
        dispatch.Invoke(media, [Windows.Media.SystemMediaTransportControlsButton.Pause]);
        Assert(commands.SequenceEqual([DesktopMediaAction.Play, DesktopMediaAction.Pause]), "SMTC lost distinct Play and Pause intentions");

        await f.Window.HandleDesktopMediaCommandAsync(DesktopMediaAction.Play);
        Assert(f.Player.IsPlaying, "duplicate SMTC Play paused playback");
        f.Player.Seek(TimeSpan.FromSeconds(5));
        await f.Window.HandleDesktopMediaCommandAsync(DesktopMediaAction.Pause);
        var pausedAt = f.Player.Position;
        await f.Window.HandleDesktopMediaCommandAsync(DesktopMediaAction.Pause);
        Assert(!f.Player.IsPlaying && f.Player.HasSource && pausedAt.TotalSeconds >= 4
            && Math.Abs((f.Player.Position - pausedAt).TotalSeconds) < .2,
            "duplicate SMTC Pause resumed playback or discarded its position");
        f.SetCurrent(f.Rows[1]); // Refresh controls with the paused source before the explicit Stop.
        await f.Window.HandleDesktopMediaCommandAsync(DesktopMediaAction.Stop);
        Assert(!media.HandlesMediaKeys && !f.Player.HasSource && !f.Rows[1].IsPlaying &&
            ReferenceEquals(Pk040Field<PlaybackQueue>(f.Window, "_playbackQueue").Current, f.Rows[1]),
            "SMTC Stop retained the active source or discarded the queue selection");
        dispatch.Invoke(media, [Windows.Media.SystemMediaTransportControlsButton.Play]);
        Assert(commands.Count == 2 && f.Key(Key.MediaPlayPause).Handled, "inactive SMTC handled Play or prevented the local key");
        await Until(() => f.Player.IsPlaying && !Pk040Field<bool>(f.Window, "_loadingStream"));
        Assert(f.Player.Position.TotalSeconds < 3, "Play after Stop resumed from the old paused position");
    }

    private static async Task Pk040MediaDuringCollapse()
    {
        if (!Motion.CanAnimate) return;
        foreach (var raw in new[] { false, true })
        {
            using var f = new Fr0317Fixture("collapse-media-previous-" + raw);
            var w = f.Window;
            var first = new TrackRow { Id = "collapse-media-a", RequestId = "collapse-media-a", Title = "Fixture A",
                Available = true, DurationMs = 30000, LocalPath = PlaybackOptionsSilentWave("collapse-media-a-" + raw, 30) };
            var second = new TrackRow { Id = "collapse-media-b", RequestId = "collapse-media-b", Title = "Fixture B",
                Available = true, DurationMs = 30000, LocalPath = PlaybackOptionsSilentWave("collapse-media-b-" + raw, 30) };
            var queue = Pk040Field<PlaybackQueue>(w, "_playbackQueue");
            var player = Pk040Field<NativePlayer>(w, "_player");
            player.SetVolume(0);
            queue.SetItems([first, second], first);
            await w.PlayTrackAsync(first);
            await Call(w, "AdvanceAsync", 1, false);
            player.Seek(TimeSpan.FromSeconds(8));
            await Until(() => player.Position.TotalSeconds > 6);
            Pk040Call(w, "SetExpandedPlayerVisible", true, true);
            Pk040Field<Image>(w, "_expandedPlayerCover").Source = f.Artwork;
            var host = Pk040Field<object>(w, "_expandedFullscreenHost");
            var fullscreen = (Window)host.GetType().GetProperty("Window", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(host)!;
            Fc040Begin(f, f.SourceBounds);
            Fc040Clock(Pk040Field<object>(w, "_expandedCollapse")).Controller!.Pause();
            var stage = Pk040Field<Grid>(w, "_expandedRevealStage");
            Assert(!stage.IsEnabled && !stage.IsHitTestVisible, "fixture did not freeze the closing fullscreen contents");
            var token = queue.CurrentOccurrenceToken;
            Previous();
            Assert(queue.CurrentOccurrenceToken == token && !stage.IsHitTestVisible,
                "Previous past the threshold changed the closing occurrence or restored pointer input");
            await Until(() => player.Position.TotalSeconds < 2);
            Previous();
            await Until(() => ReferenceEquals(queue.Current, first) && player.IsPlaying);
            Assert(queue.CurrentOccurrenceToken != token, "Previous before the threshold did not move to the prior occurrence");

            void Previous()
            {
                if (!raw)
                {
                    Assert(MediaCommands.PreviousTrack.CanExecute(null, fullscreen), "routed Previous was rejected during collapse");
                    MediaCommands.PreviousTrack.Execute(null, fullscreen);
                    return;
                }
                var press = new KeyEventArgs(new Pk040Keyboard(ModifierKeys.None), new Pk040Source(fullscreen), Environment.TickCount, Key.MediaPreviousTrack)
                    { RoutedEvent = Keyboard.PreviewKeyDownEvent };
                fullscreen.RaiseEvent(press);
                Assert(press.Handled, "raw Previous missed the closing fullscreen host");
            }
        }
    }

    private static Task Pk040FullscreenMediaRegistration()
    {
        using var f = new Pk040Fixture("cached-fullscreen-media-registration");
        f.SetCurrent(f.Rows[0]);
        Window? cached = null;
        for (var opening = 0; opening < 4; opening++)
        {
            Assert(f.Key(Key.F11).Handled, "F11 did not open the cached fullscreen fixture");
            var window = f.Fullscreen;
            cached ??= window;
            Assert(ReferenceEquals(window, cached)
                && HandlerCount(window, CommandManager.PreviewCanExecuteEvent) == 1
                && HandlerCount(window, CommandManager.PreviewExecutedEvent) == 1,
                $"fullscreen opening {opening + 1} changed its window or media subscription count");
            Assert(f.Key(Key.Escape, window).Handled && Pk040Field<object?>(f.Window, "_expandedFullscreenHost") is null,
                "Escape did not release the temporary fullscreen host");
        }

        var closed = cached ?? throw new InvalidOperationException("fixture never created a fullscreen window");
        Pk040Call(f.Window, "DestroyExpandedFullscreenWindow", f.Window, EventArgs.Empty);
        Assert(Pk040Field<object?>(f.Window, "_expandedFullscreenWindow") is null
            && HandlerCount(closed, CommandManager.PreviewCanExecuteEvent) == 0
            && HandlerCount(closed, CommandManager.PreviewExecutedEvent) == 0,
            "destroying the cached window retained media subscriptions");
        return Task.CompletedTask;

        static int HandlerCount(Window window, RoutedEvent routedEvent)
        {
            var store = typeof(UIElement).GetProperty("EventHandlersStore", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window);
            if (store is null) return 0;
            var registrations = store.GetType().GetMethod("GetRoutedEventHandlers")!.Invoke(store, [routedEvent]);
            return (registrations as Array)?.Length ?? 0;
        }
    }

    private static async Task Pk040ApplicationMediaCommands()
    {
        using var f = new Pk040Fixture("app-media-routed");
        var w = f.Window;
        var queue = Pk040Field<PlaybackQueue>(w, "_playbackQueue");
        f.SetCurrent(f.Rows[0]);
        var editor = new TextBox { Text = "Поиск" };
        ((Grid)w.Content).Children.Add(editor); f.Layout();

        Execute(MediaCommands.Play, editor);
        await Until(() => f.Player.IsPlaying && !Pk040Field<bool>(w, "_loadingStream"));
        var first = queue.CurrentOccurrenceToken;
        Execute(MediaCommands.Play, editor);
        Assert(f.Player.IsPlaying && queue.CurrentOccurrenceToken == first, "explicit routed Play toggled or advanced the active source");
        Execute(MediaCommands.Pause, editor);
        Execute(MediaCommands.Pause, editor);
        Assert(!f.Player.IsPlaying && f.Player.HasSource && editor.Text == "Поиск", "explicit routed Pause was not idempotent");
        var version = queue.Version;
        Execute(MediaCommands.NextTrack, editor);
        await Until(() => queue.CurrentIndex == 1 && f.Player.IsPlaying && !Pk040Field<bool>(w, "_loadingStream"));
        Assert(queue.Version == version + 1, "the preview and bubble routes both advanced the queue");

        Assert(f.Key(Key.F11).Handled, "fixture could not open the fullscreen host");
        Execute(MediaCommands.PreviousTrack, f.ExpandedVolume);
        await Until(() => queue.CurrentOccurrenceToken == first && f.Player.IsPlaying);
        Execute(MediaCommands.Stop, f.ExpandedVolume);
        Assert(!f.Player.HasSource && queue.CurrentOccurrenceToken == first, "routed Stop discarded the exact occurrence or left it active");

        var mini = new MiniPlayerWindow();
        mini.Update("Fixture", "", false, true, true, canPlay: true);
        w.AttachMediaCommands(mini);
        try
        {
            Execute(MediaCommands.TogglePlayPause, mini.PlayButton);
            await Until(() => f.Player.IsPlaying && !Pk040Field<bool>(w, "_loadingStream"));
            Assert(queue.CurrentOccurrenceToken == first, "mini routed Play changed the selected occurrence");
            Execute(MediaCommands.Pause, mini.PlayButton);
            Assert(!f.Player.IsPlaying, "mini did not route explicit Pause");
            Execute(MediaCommands.Stop, mini.PlayButton);
            Assert(!f.Player.HasSource && !f.Key(Key.Space, mini.PlayButton).Handled
                && f.Key(Key.MediaPlayPause, mini.PlayButton).Handled, "mini did not preserve ordinary keys or route raw Play after Stop");
            await Until(() => f.Player.IsPlaying && !Pk040Field<bool>(w, "_loadingStream"));
            foreach (var raw in new[] { false, true })
            {
                Assert(f.Key(Key.MediaNextTrack, mini.PlayButton).Handled, "mini did not take the raw Next key");
                await Until(() => queue.CurrentIndex == 1 && f.Player.IsPlaying && !Pk040Field<bool>(w, "_loadingStream"));
                var second = queue.CurrentOccurrenceToken;
                f.Player.Seek(TimeSpan.FromSeconds(6));
                await Until(() => f.Player.Position.TotalSeconds > 4);
                Previous();
                Assert(queue.CurrentOccurrenceToken == second, "mini Previous past the threshold moved to another occurrence");
                await Until(() => f.Player.Position.TotalSeconds < 2);
                Previous();
                await Until(() => queue.CurrentOccurrenceToken == first && f.Player.IsPlaying && !Pk040Field<bool>(w, "_loadingStream"));

                void Previous()
                {
                    if (raw) Assert(f.Key(Key.MediaPreviousTrack, mini.PlayButton).Handled, "mini did not take the raw Previous key");
                    else Execute(MediaCommands.PreviousTrack, mini.PlayButton);
                }
            }

            var gate = (WindowsIntegration)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(WindowsIntegration));
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            typeof(WindowsIntegration).GetField("_window", flags)!.SetValue(gate, w);
            typeof(WindowsIntegration).GetField("<IsAvailable>k__BackingField", flags)!.SetValue(gate, true);
            typeof(WindowsIntegration).GetField("<WindowHandle>k__BackingField", flags)!.SetValue(gate, (nint)1);
            Pk040Set(w, "_windows", gate);
            f.SetCurrent(f.Rows[0]);
            Assert(gate.HandlesMediaKeys && !MediaCommands.NextTrack.CanExecute(null, f.ExpandedVolume)
                && !MediaCommands.NextTrack.CanExecute(null, mini.PlayButton)
                && !f.Key(Key.MediaPlayPause, mini.PlayButton).Handled && !f.Key(Key.MediaNextTrack, mini.PlayButton).Handled
                && !f.Key(Key.MediaPreviousTrack, mini.PlayButton).Handled,
                "a local routed command or raw mini key still claims an active SMTC action");
            MediaCommands.NextTrack.Execute(null, mini.PlayButton);
            Assert(queue.CurrentOccurrenceToken == first && f.Player.IsPlaying, "the local route duplicated active SMTC");

            Task observed = Task.CompletedTask;
            gate.Command += action => observed = w.HandleDesktopMediaCommandAsync(action);
            typeof(WindowsIntegration).GetMethod("DispatchButton", flags)!.Invoke(gate, [Windows.Media.SystemMediaTransportControlsButton.Next]);
            await observed.WaitAsync(TimeSpan.FromSeconds(5));
            Assert(queue.CurrentIndex == 1 && f.Player.IsPlaying, "a single SMTC Next did not own the transition");
        }
        finally { mini.Close(); }

        static void Execute(RoutedCommand command, IInputElement focus)
        {
            Assert(command.CanExecute(null, focus), $"local route rejected {command.Name}");
            command.Execute(null, focus);
        }
    }

    private static Task Pk040ButtonActivation()
    {
        using var f = new Pk040Fixture("button-activation");
        f.SetCurrent(f.Rows[0]);
        var w = f.Window;
        var list = Control<ListView>(w, "TracksList");
        list.SelectedIndex = 0;
        f.Layout();
        var row = (ListViewItem)list.ItemContainerGenerator.ContainerFromIndex(0);
        var title = Pk040Visuals<Button>(row).First(button => ReferenceEquals(button.Tag, f.Rows[0]) && Equals(button.ToolTip, "Открыть альбом"));
        var previousFocus = Keyboard.FocusedElement as DependencyObject;
        try
        {
            Activate(title, Key.Enter);
            Assert(Control<TextBlock>(w, "StatusText").Text == "Для открытия альбома подключитесь к Яндексу." && !f.Player.HasSource,
                "Enter played the selected row instead of opening its focused title link");
            Activate(Control<Button>(w, "QueueNav"), Key.Space);
            Assert(Pk040Field<string>(w, "_section") == "queue" && !f.Player.HasSource,
                "Space sent a focused navigation button to the global transport");
        }
        finally { Pk040Focus(previousFocus); }
        return Task.CompletedTask;

        void Activate(Button button, Key key)
        {
            Pk040Focus(button);
            Assert(button.IsKeyboardFocused && !f.Key(key, button).Handled, "the focused button lost its preview key: " + key);
            var keyboard = new Pk040Keyboard(ModifierKeys.None);
            var source = new Pk040Source(button);
            button.RaiseEvent(new KeyEventArgs(keyboard, source, Environment.TickCount, key) { RoutedEvent = Keyboard.KeyDownEvent });
            if (key == Key.Space)
                button.RaiseEvent(new KeyEventArgs(keyboard, source, Environment.TickCount, key) { RoutedEvent = Keyboard.KeyUpEvent });
        }
    }

    private static async Task Pk040Repeat()
    {
        using var f = new Pk040Fixture("key-autorepeat"); f.SetCurrent(f.Rows[0]);
        Assert(f.Key(Key.P).Handled, "a new P press was ignored");
        await Until(() => f.Player.IsPlaying && !Pk040Field<bool>(f.Window, "_loadingStream"));
        Assert(f.Key(Key.P, repeat: true).Handled && f.Player.IsPlaying, "held P toggled playback a second time");
        Assert(f.Key(Key.Space).Handled && !f.Player.IsPlaying, "a new global Space press did not toggle playback");
        Assert(f.Key(Key.Space, repeat: true).Handled && !f.Player.IsPlaying, "held Space resumed playback");
        Assert(f.Key(Key.Space).Handled && f.Player.IsPlaying, "a second independent Space press did not resume playback");

        f.Volume.Value = 37;
        Assert(f.Key(Key.M).Handled && f.Volume.Value == 0, "a new M press did not mute");
        Assert(f.Key(Key.M, repeat: true).Handled && f.Volume.Value == 0, "held M restored volume");
        Assert(f.Key(Key.M).Handled && f.Volume.Value == 37, "a second independent M press did not restore volume");

        Assert(f.Key(Key.F11).Handled, "a new F11 press did not open fullscreen");
        var fullscreen = f.Fullscreen;
        var mainPlay = Control<Button>(f.Window, "PlayPauseButton");
        var fullPlay = Pk040Visuals<Button>(Pk040Field<Grid>(f.Window, "_expandedPlayerSurface"))
            .Single(button => button.Name == "ExpandedPlayerPlayPause");
        Assert(AutomationProperties.GetName(mainPlay) == "Пауза" && AutomationProperties.GetName(fullPlay) == "Пауза",
            "active transport did not expose the Pause action in both views");
        Assert(f.Key(Key.P, fullscreen).Handled && AutomationProperties.GetName(mainPlay) == "Воспроизвести"
            && AutomationProperties.GetName(fullPlay) == "Воспроизвести", "paused transport did not expose the Play action in both views");
        Assert(f.Key(Key.F11, fullscreen, repeat: true).Handled && ReferenceEquals(fullscreen, f.Fullscreen), "held F11 closed fullscreen");
        Assert(f.Key(Key.F11, fullscreen).Handled && Pk040Field<object?>(f.Window, "_expandedFullscreenHost") is null,
            "a second independent F11 press did not close fullscreen");
    }

    private static async Task Pk040TrackActivation()
    {
        using var f = new Pk040Fixture("exact-track-row");
        var w = f.Window;
        var unavailable = new TrackRow { Id = "not-playable", RequestId = "not-playable", Title = "Недоступный" };
        Pk040Call(w, "ShowRows", (object)new[] { f.Rows[0], unavailable, f.Rows[2], f.Rows[2] });
        var list = Control<ListView>(w, "TracksList");
        list.SelectedIndex = 1; f.Layout();
        var unavailableRow = (ListViewItem)list.ItemContainerGenerator.ContainerFromIndex(1);
        var previousFocus = Keyboard.FocusedElement as DependencyObject;
        try
        {
            Pk040Focus(unavailableRow);
            Assert(unavailableRow.IsKeyboardFocused && f.Key(Key.Enter, unavailableRow).Handled, "fixture missed the unavailable row's Enter route");
            var click = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
            { RoutedEvent = System.Windows.Controls.Control.MouseDoubleClickEvent, Source = unavailableRow };
            Pk040Call(w, "Tracks_DoubleClick", list, click);
            Assert(click.Handled && !f.Player.HasSource && !Pk040Field<bool>(w, "_loadingStream"),
                "activating an unavailable row tried to play a different track");

            var duplicateRow = (ListViewItem)list.ItemContainerGenerator.ContainerFromIndex(3);
            Pk040Focus(duplicateRow);
            Assert(f.Key(Key.Enter, duplicateRow, repeat: true).Handled && !f.Player.HasSource && !Pk040Field<bool>(w, "_loadingStream"),
                "held Enter started the newly focused track");
            Assert(f.Key(Key.Enter, duplicateRow).Handled, "fixture missed its focused duplicate row");
            var queue = Pk040Field<PlaybackQueue>(w, "_playbackQueue");
            await Until(() => f.Player.HasSource && !Pk040Field<bool>(w, "_loadingStream"));
            Assert(queue.CurrentIndex == 3, "Enter played the first duplicate instead of the focused occurrence");

            list.UnselectAll();
            Control<Button>(w, "PlaySelectionButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => ReferenceEquals(Pk040Field<TrackRow?>(w, "_current"), f.Rows[0]) && !Pk040Field<bool>(w, "_loadingStream"));
            Assert(queue.CurrentIndex == 0, "Listen lost its no-selection fallback");
        }
        finally { Pk040Focus(previousFocus); }
    }

    private static Task Pk040QueueUndoOverlays()
    {
        using var f = new Pk040Fixture("queue-undo-overlays"); f.SetCurrent(f.Rows[0]);
        var w = f.Window;
        var queue = Pk040Field<PlaybackQueue>(w, "_playbackQueue");
        queue.RemoveMany([1]);
        Pk040Call(w, "ShowQueue");
        Pk040Call(w, "SetSettingsVisible", true);
        Assert(!f.Key(Key.Z, modifiers: ModifierKeys.Control).Handled && queue.CanUndoRemoval,
            "Ctrl+Z in Settings changed the hidden queue");
        Pk040Call(w, "SetSettingsVisible", false);
        Pk040Call(w, "SetLyricsVisible", true);
        Assert(!f.Key(Key.Z, modifiers: ModifierKeys.Control).Handled && queue.CanUndoRemoval,
            "Ctrl+Z in Lyrics changed the hidden queue");
        Pk040Call(w, "SetLyricsVisible", false);
        f.Key(Key.F11);
        Assert(!f.Key(Key.Z, f.Fullscreen, ModifierKeys.Control).Handled && queue.CanUndoRemoval,
            "Ctrl+Z in Fullscreen changed the hidden queue");
        f.Key(Key.Escape, f.Fullscreen);
        Assert(f.Key(Key.Z, modifiers: ModifierKeys.Control).Handled && !queue.CanUndoRemoval && queue.Items.Count == 3,
            "Ctrl+Z no longer restores the removed row in Queue");
        return Task.CompletedTask;
    }

    private static async Task Pk040CatalogEnter()
    {
        using var f = new Pk040Fixture("catalog-enter");
        f.Account();
        var displayed = new[] { new CatalogItem("91", "album", "Selected album", "Fixture"),
            new CatalogItem("92", "album", "Focused album", "Fixture") };
        Pk040Call(f.Window, "ShowCatalogItems", displayed, false);
        var catalog = Control<ListBox>(f.Window, "CatalogList");
        catalog.SelectedIndex = 0;
        f.Layout();
        var row = (ListBoxItem)catalog.ItemContainerGenerator.ContainerFromIndex(1);
        var previousFocus = Keyboard.FocusedElement as DependencyObject;
        try
        {
            Pk040Focus(row);
            var requests = f.Http.Requests;
            Assert(f.Key(Key.Enter, row, repeat: true).Handled && f.Http.Requests == requests,
                "held Enter opened the focused catalog card");
            Assert(f.Key(Key.Enter, row).Handled, "catalog Enter missed its focused container");
            await Until(() => f.Http.Requests > requests);
            Assert(Pk040Field<CatalogItem?>(f.Window, "_openedCatalogItem")?.Id == "92", "catalog Enter opened the old selection");
        }
        finally { Pk040Focus(previousFocus); }
    }

    private static void Pk040Focus(DependencyObject? target)
    {
        var change = typeof(KeyboardDevice).GetMethod("ChangeFocus", BindingFlags.Instance | BindingFlags.NonPublic,
            null, [typeof(DependencyObject), typeof(int)], null) ?? throw new InvalidOperationException("WPF local keyboard focus hook unavailable");
        change.Invoke(Keyboard.PrimaryDevice, [target, Environment.TickCount]);
    }

    private static async Task Pk040Like()
    {
        using var f = new Pk040Fixture("like"); f.Account(); f.SetCurrent(f.Rows[0]);
        var favorites = Pk040Field<HashSet<string>>(f.Window, "_favoriteTrackIds");
        favorites.UnionWith(f.Http.Likes); Pk040Set(f.Window, "_favoriteIdsKnown", true); Pk040Call(f.Window, "UpdateControls");
        Control<ListView>(f.Window, "TracksList").SelectedItem = f.Rows[1];
        f.Http.Hold = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Assert(f.Key(Key.L).Handled, "L was not handled");
        await Until(() => f.Http.Writes.Count == 1);
        Assert(f.Http.Writes[0] == ("remove", "keys-0:91") && favorites.Contains("keys-0"), "L targeted selection or applied before server confirmation");
        f.SetCurrent(f.Rows[1]); f.Key(Key.L);
        Assert(f.Http.Writes.Count == 1, "repeated L bypassed the mutation guard");
        f.Http.Hold.SetResult(); await Until(() => !Pk040Field<bool>(f.Window, "_catalogWriting")); f.Http.Hold = null;
        Assert(!favorites.Contains("keys-0") && favorites.Contains("keys-1") && !f.Rows[0].IsLiked && f.Rows[1].IsLiked, "async L confirmation was redirected to the newer current track");
        f.SetCurrent(f.Rows[0]);
        Assert(f.Key(Key.L, repeat: true).Handled && f.Http.Writes.Count == 1, "held L toggled the like after the request finished");
        f.Key(Key.F11); Assert(f.Key(Key.L, f.Fullscreen).Handled, "fullscreen L was not handled");
        await Until(() => f.Http.Writes.Count == 2 && !Pk040Field<bool>(f.Window, "_catalogWriting") && favorites.Contains("keys-0"));
        Assert(f.Http.Writes[1] == ("add-multiple", "keys-0:91") && f.Http.Likes.Contains("keys-0") && f.Rows[0].IsLiked, "fullscreen L did not persist the exact track like through the shared action");
        Pk040Set(f.Window, "_favoriteIdsKnown", false); f.Key(Key.L);
        Assert(f.Http.Writes.Count == 2, "L guessed an unknown like state");
    }

    private static T Pk040Field<T>(MainWindow window, string name) => (T)typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
    private static void Pk040Set(MainWindow window, string name, object? value) => typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, value);
    private static void Pk040Call(MainWindow window, string name, params object?[] args) => typeof(MainWindow).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, args);
    private static IEnumerable<T> Pk040Visuals<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T value) yield return value;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            foreach (var child in Pk040Visuals<T>(VisualTreeHelper.GetChild(root, index))) yield return child;
    }

    private sealed class Pk040Keyboard(ModifierKeys modifiers) : KeyboardDevice(InputManager.Current)
    {
        protected override KeyStates GetKeyStatesFromSystem(Key key) => key switch
        {
            Key.LeftCtrl or Key.RightCtrl when modifiers.HasFlag(ModifierKeys.Control) => KeyStates.Down,
            Key.LeftAlt or Key.RightAlt when modifiers.HasFlag(ModifierKeys.Alt) => KeyStates.Down,
            Key.LWin or Key.RWin when modifiers.HasFlag(ModifierKeys.Windows) => KeyStates.Down,
            Key.LeftShift or Key.RightShift when modifiers.HasFlag(ModifierKeys.Shift) => KeyStates.Down,
            _ => KeyStates.None
        };
    }
    private sealed class Pk040Source(Visual root) : PresentationSource
    {
        public override Visual RootVisual { get; set; } = root;
        public override bool IsDisposed => false;
        protected override CompositionTarget GetCompositionTargetCore() => null!;
    }

    private sealed class Pk040Fixture : IDisposable
    {
        internal MainWindow Window { get; }
        internal NativePlayer Player { get; }
        internal PreferencesStore Preferences { get; }
        internal Pk040Http Http { get; } = new();
        internal TrackRow[] Rows { get; }
        internal Slider Volume => Control<Slider>(Window, "VolumeSlider");
        internal Slider ExpandedVolume => Pk040Visuals<Slider>(Pk040Field<Grid>(Window, "_expandedPlayerSurface")).Single(slider => slider.Name == "ExpandedPlayerVolume");
        internal Window Fullscreen => (Window)Pk040Field<object>(Window, "_expandedFullscreenHost").GetType().GetProperty("Window", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Pk040Field<object>(Window, "_expandedFullscreenHost"))!;
        private readonly OfflineLibrary _offline;
        internal Pk040Fixture(string name)
        {
            var directory = Path.Combine(_directory, "playback-keys040-" + name); Directory.CreateDirectory(directory);
            Preferences = new PreferencesStore(Path.Combine(directory, "preferences")); Preferences.Save(new PlayerPreferences { Volume = 0, CrossfadeSeconds = 0, Theme = "Dark" });
            Player = new NativePlayer(new AudioBuffer(Path.Combine(directory, "audio"), new FakeHandler((_, _) => throw new InvalidOperationException("keyboard fixture attempted CDN"))));
            _offline = new OfflineLibrary(Path.Combine(directory, "offline"));
            Window = new MainWindow(api: new MusicApi(Http), store: new SessionStore(Path.Combine(directory, "session")), restore: false,
                diagnosticsDirectory: directory, offline: _offline, player: Player, preferences: Preferences)
            { ShowActivated = false, ShowInTaskbar = false, Width = 1280, Height = 860,
                WindowStartupLocation = WindowStartupLocation.Manual, Left = -16000, Top = -4000 };
            var wav = WriteWave("playback-keys040-" + name + ".wav", 30);
            Rows = Enumerable.Range(0, 3).Select(index => new TrackRow { Id = "keys-" + index, RequestId = "keys-" + index + ":91", AlbumId = "91",
                Title = "Private keyboard " + index, Artist = "Fixture", Available = true, DurationMs = 30000, LocalPath = wav }).ToArray();
            Pk040Call(Window, "ShowRows", (object)Rows); Pk040Field<PlaybackQueue>(Window, "_playbackQueue").SetItems(Rows, Rows[0]);
            Layout();
            Assert(!Window.IsVisible && PresentationSource.FromVisual(Window) is null && new WindowInteropHelper(Window).Handle == 0, "keyboard fixture created an HWND");
        }
        internal void Account() { Pk040Set(Window, "_session", FakeSession); Pk040Set(Window, "_account", new Account("12345", "Fixture", true)); }
        internal void SetCurrent(TrackRow? track)
        {
            Pk040Set(Window, "_current", track); if (track is not null) Pk040Field<PlaybackQueue>(Window, "_playbackQueue").Select(track);
            Pk040Call(Window, "UpdateControls"); Layout();
        }
        internal void Layout()
        { var root = (FrameworkElement)Window.Content; root.Measure(new Size(1280, 860)); root.Arrange(new Rect(0, 0, 1280, 860)); root.UpdateLayout(); }
        internal KeyEventArgs Key(Key key, UIElement? target = null, ModifierKeys modifiers = ModifierKeys.None, bool repeat = false)
        {
            target ??= Window;
            var args = new KeyEventArgs(new Pk040Keyboard(modifiers), new Pk040Source(target), Environment.TickCount, key) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            if (repeat)
            {
                var markRepeat = typeof(KeyEventArgs).GetMethod("SetRepeat", BindingFlags.Instance | BindingFlags.NonPublic)
                    ?? throw new InvalidOperationException("WPF key repeat hook unavailable");
                markRepeat.Invoke(args, [true]);
            }
            target.RaiseEvent(args); return args;
        }
        public void Dispose() { Http.Hold?.TrySetCanceled(); Window.Close(); _offline.Dispose(); Http.Dispose(); }
    }

    private sealed class Pk040Http : HttpMessageHandler
    {
        internal HashSet<string> Likes { get; } = new(StringComparer.Ordinal) { "keys-0", "keys-1" };
        internal List<(string Action, string Id)> Writes { get; } = [];
        internal TaskCompletionSource? Hold;
        internal int Requests;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Requests++; Assert(request.RequestUri!.Host == "api.music.yandex.net", "keyboard fixture left its fake API");
            var path = request.RequestUri.AbsolutePath;
            if (path == "/account/status") return Json(Profile);
            if (path == "/albums/92/with-tracks") return Json("{\"result\":{\"id\":92,\"title\":\"Focused album\",\"volumes\":[]}}");
            if (path == "/users/12345/likes/tracks") return Json(JsonSerializer.Serialize(new { result = new { library = new { tracks = Likes.Select(id => new { id, albumId = "91" }).ToArray() } } }));
            if (path.StartsWith("/users/12345/likes/tracks/", StringComparison.Ordinal))
            {
                var body = Uri.UnescapeDataString(await request.Content!.ReadAsStringAsync(token));
                var id = body.Split('&').Single(part => part.StartsWith("track-ids=", StringComparison.Ordinal))[10..];
                var action = path.Split('/')[^1]; Writes.Add((action, id));
                if (Hold is { } hold) await hold.Task.WaitAsync(token);
                if (action == "remove") Likes.Remove(id.Split(':')[0]); else Likes.Add(id.Split(':')[0]);
                return Json("{\"result\":{\"revision\":1}}");
            }
            if (path == "/tracks") return Json(JsonSerializer.Serialize(new { result = Likes.Select(id => new { id, title = id, available = true, durationMs = 30000,
                artists = new[] { new { id = "fixture", name = "Fixture" } }, albums = new[] { new { id = "91", title = "Fixture" } } }).ToArray() }));
            throw new InvalidOperationException("Unexpected keyboard fixture route " + path);
        }
    }
}
