using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Windows;
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
        await Run("PlaybackKeys040: text password rich text editable combo and modifier keys retain input", Pk040Input);
        await Run("PlaybackKeys040: retired local search shortcut and smart navigation stay absent", Pk040RetiredLocalSearch);
        await Run("PlaybackKeys040: main and numpad volume clamp persist and mute restores the last nonzero level", Pk040Volume);
        await Run("PlaybackKeys040: routed P Space brackets and media keys use shared asynchronous playback", Pk040Transport);
        await Run("PlaybackKeys040: L captures current track and reuses confirmed account mutation guards", Pk040Like);
        File.WriteAllText(Path.Combine(_directory, "playback-keys040-scope.json"), JsonSerializer.Serialize(new
        {
            fixtureWindowsShown = false, nativeHandlesCreated = false, desktopInputUsed = false,
            realAccountUsed = false, externalNetworkRequests = 0, keyboardLayoutChanged = false,
            keyEvents = "WPF routed PreviewKeyDown with an isolated KeyboardDevice",
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
        f.Player.Seek(TimeSpan.FromSeconds(8));
        Assert(f.Key(Key.MediaPreviousTrack).Handled, "media previous was not handled");
        await Until(() => !Pk040Field<bool>(f.Window, "_loadingStream") && ReferenceEquals(Pk040Field<TrackRow?>(f.Window, "_current"), f.Rows[1]));
        Assert(Pk040Field<PlaybackQueue>(f.Window, "_playbackQueue").CurrentIndex == 1 && f.Player.Position.TotalSeconds < 3, "media previous changed direction");
        f.Key(Key.P); Assert(!f.Player.IsPlaying && f.Http.Requests == 0, "P pause performed network or failed");
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
        f.SetCurrent(f.Rows[0]); f.Key(Key.F11); Assert(f.Key(Key.L, f.Fullscreen).Handled, "fullscreen L was not handled");
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
            // Keep lyric discovery inside this never-shown keyboard fixture.
            Pk040Set(Window, "_lyricsCts", new CancellationTokenSource()); Layout();
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
        internal KeyEventArgs Key(Key key, UIElement? target = null, ModifierKeys modifiers = ModifierKeys.None)
        {
            target ??= Window;
            var args = new KeyEventArgs(new Pk040Keyboard(modifiers), new Pk040Source(target), Environment.TickCount, key) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
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
