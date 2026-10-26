using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using YMusic.Native;

internal static partial class Program
{
    private static async Task CheckCoverAccent015()
    {
        await Run("CoverAccent015: dominant coloured area and meaningful details retain readable monochrome fallbacks", Accent015Dominant);
        await Run("CoverAccent015: transparent pixels are ignored and decoded sampling stays bounded", Accent015Sampling);
        await Run("CoverAccent015: palette and complete interrupted colour paths retain readable contrast", Accent015Contrast);
        await Run("CoverAccent015: actual WPF five-second clocks have start midpoint end and continuous retarget", Accent015Clocks);
        await Run("CoverAccent015: source identity generation missing cover mode cancellation and dispose reject late palettes", Accent015Stale);
        await Run("CoverAccent015: old valid invalid and unknown preference fields preserve the supported schema", Accent015Preferences);
        await Run("CoverAccent015: Settings apply mode and static colour immediately while invalid input preserves the last valid color", Accent015Settings);
        await Run("CoverAccent015: real main mini expanded slider hover focus selection resources remain shared and mutable", Accent015Views);
    }

    private static BitmapSource Accent015Bitmap(int width, int height, Func<int, int, Color> pixel)
    {
        var data = new byte[width * height * 4];
        for (var y = 0; y < height; y++) for (var x = 0; x < width; x++)
        { var c = pixel(x, y); var i = (y * width + x) * 4; data[i] = c.B; data[i + 1] = c.G; data[i + 2] = c.R; data[i + 3] = c.A; }
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, data, width * 4); bitmap.Freeze(); return bitmap;
    }

    private static Task Accent015Dominant()
    {
        foreach (var color in new[] { Colors.Red, Colors.Blue, Colors.Gray, Colors.Black, Colors.White })
        {
            var result = CoverPalette.Extract(Accent015Bitmap(32, 32, (_, _) => color));
            Assert(result == CoverPalette.Readable(color), "solid cover hue changed: " + color);
            if (color.R == color.G && color.G == color.B) Assert(result!.Value.R == result.Value.G && result.Value.G == result.Value.B, "neutral cover fell back to orange");
        }
        foreach (var majority in new[] { Colors.Gray, Colors.Black, Color.FromRgb(74, 102, 85), Colors.Blue })
        {
            var result = CoverPalette.Extract(Accent015Bitmap(32, 32, (x, _) => x < 24 ? majority : Colors.Red));
            var expected = majority.R == majority.G && majority.G == majority.B ? Colors.Red : majority;
            Assert(result == CoverPalette.Readable(expected), "colour-family area or neutral-background priority changed: " + majority);
        }
        return Task.CompletedTask;
    }

    private static Task Accent015Sampling()
    {
        Assert(CoverPalette.Extract(Accent015Bitmap(32, 32, (_, _) => Colors.Transparent)) is null, "fully transparent cover provided a colour");
        Assert(CoverPalette.Extract(Accent015Bitmap(32, 32, (x, _) => x < 30 ? Color.FromArgb(0, 255, 0, 0) : Colors.Blue)) == CoverPalette.Readable(Colors.Blue), "hidden RGB defeated visible cover");
        var large = Accent015Bitmap(1024, 1024, (_, _) => Colors.Blue);
        _ = CoverPalette.Extract(Accent015Bitmap(32, 32, (_, _) => Colors.Blue));
        var before = GC.GetAllocatedBytesForCurrentThread(); var result = CoverPalette.Extract(large);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert(result == CoverPalette.Readable(Colors.Blue) && allocated < 2 * 1024 * 1024, "large cover read a full managed pixel buffer");
        Assert(CoverPalette.SampleEdge == 32, "palette sample edge changed");
        File.WriteAllText(Path.Combine(_directory, "accent015-sampling.json"), JsonSerializer.Serialize(new { sourceWidth = 1024, sourceHeight = 1024, sampleEdge = CoverPalette.SampleEdge, maximumSamplePixels = 1024, managedAllocatedBytes = allocated }));
        return Task.CompletedTask;
    }

    private static Task Accent015Contrast()
    {
        var colors = new[] { CoverPalette.Fallback, Colors.Red, Colors.Blue, Colors.Green, Colors.Black, Colors.Gray, Colors.Purple, Colors.Cyan, Colors.Yellow, Colors.White }.Select(CoverPalette.Readable).ToArray();
        var minimum = double.MaxValue;
        foreach (var from in colors) foreach (var to in colors) for (var i = 0; i <= 100; i++)
        {
            // Check the stricter byte/sRGB path as well as actual WPF clocks below.
            var current = CoverPalette.Mix(from, to, i / 100d);
            var ink = CoverPalette.Contrast(current, CoverPalette.Ink);
            var panel = CoverPalette.Contrast(current, Color.FromRgb(23, 25, 29));
            minimum = Math.Min(minimum, Math.Min(ink, panel));
            Assert(ink >= 4.5 && panel >= 4.5, "intermediate accent is unreadable: " + current);
        }
        File.WriteAllText(Path.Combine(_directory, "accent015-contrast.json"), JsonSerializer.Serialize(new { checkedPairs = colors.Length * colors.Length, checkedStepsPerPair = 101, minimumRatio = minimum, foreground = CoverPalette.Ink.ToString() }));
        return Task.CompletedTask;
    }

    private static async Task Accent015Clocks()
    {
        using var controller = new CoverAccentController(new Image(), new ResourceDictionary(), () => null);
        var brush = controller.Brushes["AccentBrush"]; var from = brush.Color;
        controller.Retarget(Colors.Blue);
        if (!SystemParameters.ClientAreaAnimation)
        { Assert(brush.Color == controller.TargetColor && controller.Clocks.Count == 0, "Windows reduced-animation preference ignored"); return; }
        Assert(controller.Clocks.Count == 7 && controller.Clocks.Values.All(c => c.Timeline.Duration.TimeSpan == TimeSpan.FromSeconds(5)), "requested five-second duration changed");
        Accent015Seek(controller, 0); Assert(brush.Color == from, "fade jumps at start");
        Accent015Seek(controller, 2.5); var middle = brush.Color;
        Assert(middle != from && middle != controller.TargetColor && CoverPalette.Contrast(middle, CoverPalette.Ink) >= 4.5, "midpoint jumps or loses contrast");
        controller.Retarget(Colors.Red); Accent015Seek(controller, 0);
        Assert(brush.Color == middle, "rapid change did not continue from the animated colour");
        Accent015Seek(controller, 2.5); var interrupted = brush.Color;
        Assert(interrupted != middle && interrupted != controller.TargetColor, "replacement fade has no intermediate colours");
        Accent015Seek(controller, 5.01); Assert(brush.Color == controller.TargetColor, "fade fails to finish at five seconds");
        foreach (var value in controller.Brushes.Values) Assert(!value.IsFrozen && !value.CanFreeze, "completed brush becomes freezable");
        File.WriteAllText(Path.Combine(_directory, "accent015-clock.json"), JsonSerializer.Serialize(new { durationSeconds = 5, start = from.ToString(), midpoint = middle.ToString(), interruptedMidpoint = interrupted.ToString(), final = brush.Color.ToString(), actualWpfAnimationClockSeek = true, softwareRenderingDisablesColourFade = false }));
        await Task.CompletedTask;
    }

    private static void Accent015Seek(CoverAccentController controller, double seconds)
    {
        foreach (var clock in controller.Clocks.Values)
        { clock.Controller!.Pause(); clock.Controller.SeekAlignedToLastTick(TimeSpan.FromSeconds(seconds), TimeSeekOrigin.BeginTime); }
    }

    private static async Task Accent015Stale()
    {
        var image = new Image(); object identity = new(); var pending = new List<(TaskCompletionSource<Color?> Result, CancellationToken Token)>();
        var controller = new CoverAccentController(image, new ResourceDictionary(), () => identity, (_, token) =>
        { var result = new TaskCompletionSource<Color?>(TaskCreationOptions.RunContinuationsAsynchronously); pending.Add((result, token)); return result.Task; });
        var red = Accent015Bitmap(32, 32, (_, _) => Colors.Red); var blue = Accent015Bitmap(32, 32, (_, _) => Colors.Blue);
        try
        {
            image.Source = red; var oldTask = controller.PendingAnalysis;
            identity = new(); image.Source = blue; var newTask = controller.PendingAnalysis;
            Assert(pending.Count == 2 && pending[0].Token.IsCancellationRequested, "source change did not cancel old palette");
            pending[0].Result.SetResult(Colors.Red); await oldTask;
            Assert(controller.TargetColor == CoverPalette.Fallback, "late old cover recoloured the new track");
            pending[1].Result.SetResult(Colors.Blue); await newTask;
            Assert(controller.TargetColor == CoverPalette.Readable(Colors.Blue), "current cover result rejected");
            image.Source = red; var identityTask = controller.PendingAnalysis; identity = new();
            pending[2].Result.SetResult(Colors.Red); await identityTask;
            Assert(controller.TargetColor == CoverPalette.Readable(Colors.Blue), "same-source changed track identity accepted stale palette");
            controller.Configure(false, "#51BDAD"); Assert(controller.TargetColor == CoverPalette.Readable(Color.FromRgb(81, 189, 173)), "static mode was not applied");
            controller.Configure(true, "#51BDAD"); var modeTask = controller.PendingAnalysis;
            controller.Configure(false, "#E7BB59"); pending[3].Result.SetResult(Colors.Blue); await modeTask;
            Assert(controller.TargetColor == CoverPalette.Readable(Color.FromRgb(231, 187, 89)), "late auto palette overrode static mode");
            controller.Configure(true, "#51BDAD"); var missingTask = controller.PendingAnalysis; image.Source = null;
            pending[4].Result.SetResult(Colors.Red); await missingTask;
            Assert(controller.TargetColor == CoverPalette.Readable(Color.FromRgb(81, 189, 173)), "missing cover kept stale palette instead of static fallback");
            image.Source = blue; var disposeTask = controller.PendingAnalysis; controller.Dispose();
            var target = controller.TargetColor; pending[5].Result.SetResult(Colors.Blue); await disposeTask;
            image.Source = red;
            Assert(pending.Count == 6 && pending[5].Token.IsCancellationRequested && controller.TargetColor == target && controller.Clocks.Count == 0, "disposed controller accepted or started work");
        }
        finally { controller.Dispose(); foreach (var item in pending) item.Result.TrySetResult(null); }
    }

    private static Task Accent015Preferences()
    {
        var directory = Path.Combine(_directory, "accent015-preferences"); Directory.CreateDirectory(directory); var store = new PreferencesStore(directory);
        Assert(store.Load().AccentFromCover && store.Load().StaticAccentColor == "#E7BB59" && store.Load().Theme == "System", "new defaults changed");
        File.WriteAllText(store.FilePath, "{\"volume\":0.31,\"concurrentDownloads\":7,\"unknownExperimentalTheme\":true}");
        var old = store.Load(); Assert(old.AccentFromCover && old.StaticAccentColor == "#E7BB59" && old.Theme == "System" && old.Volume == .31 && old.ConcurrentDownloads == 7, "old preferences lost defaults or unrelated choices");
        store.Save(old with { AccentFromCover = false, StaticAccentColor = "#6eadfa" }); var saved = store.Load();
        Assert(!saved.AccentFromCover && saved.StaticAccentColor == "#6EADFA" && saved.Volume == .31 && saved.ConcurrentDownloads == 7, "static preference roundtrip changed state");
        store.Save(saved with { StaticAccentColor = "#ea571c" }); var savedOrange = store.Load();
        Assert(savedOrange.StaticAccentColor == "#EA571C" && !savedOrange.AccentFromCover && savedOrange.Theme == "System"
            && savedOrange.Volume == .31 && savedOrange.ConcurrentDownloads == 7, "existing explicit orange choice was replaced by the new default");
        Assert(!File.ReadAllText(store.FilePath).Contains("unknownExperimentalTheme"), "unknown experimental schema was persisted");
        foreach (var invalid in new[] { "red", "#123", "#GG0000", "#12345678", "#12345 ", "#000000\n", "" })
        { store.Save(saved with { StaticAccentColor = invalid }); Assert(store.Load().StaticAccentColor == "#E7BB59" && !store.Load().AccentFromCover, "invalid colour was accepted or changed mode: " + invalid); }
        File.WriteAllText(store.FilePath, "{\"accentFromCover\":false,\"staticAccentColor\":null,\"volume\":0.31}");
        Assert(store.Load().StaticAccentColor == "#E7BB59" && !store.Load().AccentFromCover && store.Load().Volume == .31, "null colour corrupted unrelated settings");
        return Task.CompletedTask;
    }

    private static async Task Accent015Settings()
    {
        using var f = new SaFixture("accent015-settings"); var w = f.Window;
        var store = new PreferencesStore(Path.Combine(_directory, "accent015-settings-store"));
        typeof(MainWindow).GetField("_preferenceStore", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(w, store);
        var controller = SaField<CoverAccentController>(w, "_coverAccent");
        Assert(ReferenceEquals(Control<Button>(w, "FavoritesNav").Foreground, controller.Brushes["AccentTextBrush"])
            && ReferenceEquals(Control<Button>(w, "LoginButton").Foreground, controller.Brushes["AccentTextBrush"]), "Main XAML retained cached application resources from before controller initialization");
        SaClick(Control<Button>(w, "SettingsButton"));
        Assert(Control<CheckBox>(w, "SettingsAccentFromCover").IsChecked == true && Control<TextBox>(w, "SettingsAccentHex").Text == "#E7BB59", "Settings lost current values");
        Control<CheckBox>(w, "SettingsAccentFromCover").IsChecked = false; Control<TextBox>(w, "SettingsAccentHex").Text = "#6EADFA";
        Assert(controller.TargetColor == CoverPalette.Readable(Color.FromRgb(110, 173, 250)) && store.Load().StaticAccentColor == "#6EADFA", "live color did not apply or persist");
        SaClick(Control<Button>(w, "SettingsBackButton")); SaClick(Control<Button>(w, "SettingsButton"));
        Assert(Control<CheckBox>(w, "SettingsAccentFromCover").IsChecked == false && Control<TextBox>(w, "SettingsAccentHex").Text == "#6EADFA", "Back rolled back live color");
        var validBefore = File.ReadAllBytes(store.FilePath);
        Control<CheckBox>(w, "SettingsAccentFromCover").IsChecked = false; Control<TextBox>(w, "SettingsAccentHex").Text = "#XXYYZZ";
        Assert(SaField<bool>(w, "_settingsOpen") && File.ReadAllBytes(store.FilePath).SequenceEqual(validBefore)
            && controller.TargetColor == CoverPalette.Readable(Color.FromRgb(110, 173, 250)) && Control<TextBox>(w, "SettingsAccentHex").Text == "#XXYYZZ", "invalid input changed theme or preferences or lost typed text");
        Control<TextBox>(w, "SettingsAccentHex").Text = "#6eadfa"; SaClick(Control<Button>(w, "SettingsBackButton"));
        Assert(!SaField<bool>(w, "_settingsOpen") && !store.Load().AccentFromCover && store.Load().StaticAccentColor == "#6EADFA" && controller.TargetColor == CoverPalette.Readable(Color.FromRgb(110, 173, 250)), "Save did not apply and persist static mode atomically");
        SaInvoke(w, "LoadPreferences");
        Assert(!SaField<bool>(w, "_accentFromCover") && SaField<string>(w, "_staticAccentColor") == "#6EADFA" && controller.TargetColor == CoverPalette.Readable(Color.FromRgb(110, 173, 250)), "loading saved preferences did not configure the live accent");
        var persisted = File.ReadAllBytes(store.FilePath);
        SaClick(Control<Button>(w, "SettingsButton"));
        var swatches = Control<WrapPanel>(w, "SettingsAccentSwatches"); Assert(swatches.Children.Count == 8
            && swatches.Children.OfType<Button>().Count(b => ReferenceEquals(b.BorderBrush, w.FindResource("ForegroundBrush"))) == 1, "palette lost selection");
        Control<CheckBox>(w, "SettingsAccentFromCover").IsChecked = true; SaClick(swatches.Children.OfType<Button>().First());
        Assert(Control<TextBox>(w, "SettingsAccentHex").Text == "#E7BB59" && controller.TargetColor == CoverPalette.Fallback
            && store.Load().AccentFromCover && store.Load().StaticAccentColor == "#E7BB59", "live swatch did not apply/persist its fallback");
        foreach (var (width, height) in new[] { (1050, 680), (1280, 860) })
        { w.Width = width; w.Height = height; w.UpdateLayout(); await Task.Delay(340); App.Capture(w, Path.Combine(_directory, $"accent015-settings-{width}x{height}.png")); }
        SaClick(Control<Button>(w, "SettingsBackButton")); Assert(store.Load().AccentFromCover && store.Load().StaticAccentColor == "#E7BB59", "auto setting did not persist selected static fallback");
        var beforeAnalysis = File.ReadAllBytes(store.FilePath);
        Control<Image>(w, "PlayerCover").Source = Accent015Bitmap(32, 32, (_, _) => Colors.Blue); await controller.PendingAnalysis;
        Assert(controller.TargetColor == CoverPalette.Readable(Colors.Blue) && File.ReadAllBytes(store.FilePath).SequenceEqual(beforeAnalysis), "cover analysis saved preferences or ignored enabled auto mode");
    }

    private static async Task Accent015Views()
    {
        using var f = new SaFixture("accent015-views"); var w = f.Window;
        var current = new TrackRow { Id = "accent-fixture", RequestId = "accent-fixture", Title = "Доминирующий синий", Artist = "Собственная обложка", Available = true, DurationMs = 180000 };
        typeof(MainWindow).GetField("_current", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(w, current);
        Control<TextBlock>(w, "TrackTitle").Text = current.Title;
        SaInvoke(w, "UpdateControls");
        var cover = Accent015Bitmap(128, 128, (x, _) => x < 96 ? Colors.Blue : Colors.Red);
        Control<Image>(w, "PlayerCover").Source = cover;
        var controller = SaField<CoverAccentController>(w, "_coverAccent"); await controller.PendingAnalysis; Accent015Seek(controller, 5.01); w.UpdateLayout();
        var accent = controller.Brushes["AccentBrush"];
        var accentText = controller.Brushes["AccentTextBrush"];
        Assert(controller.TargetColor == CoverPalette.Readable(Colors.Blue) && ReferenceEquals(Control<Button>(w, "PlayPauseButton").Background, accent), "main play disc lost dynamic resource");
        Assert(ReferenceEquals(Control<Button>(w, "FavoritesNav").Foreground, accentText) && ReferenceEquals(Control<Button>(w, "LoginButton").Foreground, accentText)
            && ReferenceEquals(((Border)Control<Button>(w, "PlayerCoverButton").Content).Background, controller.Brushes["AccentSoftBrush"]), "main XAML text retained a stale palette");
        foreach (var pair in controller.Brushes) Assert(ReferenceEquals(w.FindResource(pair.Key), pair.Value) && !pair.Value.IsFrozen, "live resource frozen or replaced: " + pair.Key);
        var mini = new MiniPlayerWindow { Left = -16000, Top = -16000, ShowActivated = false, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual };
        try
        {
            mini.Update(current.Title, current.Artist, false, true, true); mini.UpdateDetails(cover, 0, 180, true, true, true, 0, current.RequestId); mini.Show(); mini.UpdateLayout();
            Assert(ReferenceEquals(mini.PlayButton.Background, accent) && ReferenceEquals(mini.LikeButton.Foreground, accentText), "mini play or like kept a stale accent");
            Assert(SaVisuals<System.Windows.Shapes.Ellipse>(mini.SeekSlider).Any(e => ReferenceEquals(e.Fill, accent)), "mini seek handle kept hardcoded orange");
            SaInvoke(w, "SetExpandedPlayerVisible", true, false); w.UpdateLayout();
            var expanded = Control<ContentControl>(w, "ExpandedPlayerHost");
            var play = SaVisuals<Button>(expanded).Single(b => b.Name == "ExpandedPlayerPlayPause");
            Assert(ReferenceEquals(play.Background, accent) && ((SolidColorBrush)play.Foreground).Color == CoverPalette.Ink, "expanded play colour is not dynamic or readable");
            var link = new Button { Content = "Ссылка", Style = (Style)w.FindResource("TextLink") };
            var selection = new ListBox { Items = { "Выбранная строка" }, ItemContainerStyle = (Style)w.FindResource("FlatListItem"), SelectedIndex = 0 };
            var input = new TextBox { Text = "Выделение текста" };
            var controls = new Window { Content = new StackPanel { Children = { link, selection, input } }, Left = -16000, Top = -16000,
                Width = 400, Height = 240, ShowActivated = false, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual };
            try
            {
                controls.Show(); controls.UpdateLayout(); Ui015Key(link, System.Windows.Input.Key.Tab); link.Focus();
                Assert(ReferenceEquals(link.Foreground, accentText) && ReferenceEquals(ChromePart<Border>(link, "LinkKeyboardFocus").BorderBrush, accent), "keyboard link focus kept an old accent");
                var item = (ListBoxItem)selection.ItemContainerGenerator.ContainerFromIndex(0);
                Assert(ReferenceEquals(ChromePart<Border>(item, "ItemSurface").Background, controller.Brushes["AccentSelectionBrush"])
                    && ReferenceEquals(input.SelectionBrush, controller.Brushes["AccentTextSelectionBrush"]), "selection surfaces lost dynamic palette");
            }
            finally { controls.Close(); }
            controller.Retarget(Colors.Red); Accent015Seek(controller, 2.5); w.UpdateLayout(); mini.UpdateLayout();
            Assert(ReferenceEquals(mini.PlayButton.Background, accent) && ReferenceEquals(play.Background, accent), "existing players detached during animation");
            SaInvoke(w, "SetExpandedPlayerVisible", false, false); Accent015Seek(controller, 5.01); w.UpdateLayout(); await Task.Delay(340);
            App.Capture(w, Path.Combine(_directory, "accent015-main-red.png")); App.Capture(mini, Path.Combine(_directory, "accent015-mini-red.png"));
            controller.Retarget(Colors.Blue); Accent015Seek(controller, 5.01); w.UpdateLayout(); await Task.Delay(200);
            Assert(((SolidColorBrush)ChromePart<System.Windows.Shapes.Ellipse>(Control<Button>(w, "PlayPauseButton"), "Disc").Fill).Color == accent.Color, "actual main play disc detached from its animated palette");
            App.Capture(w, Path.Combine(_directory, "accent015-main-blue.png"));
            Assert(!f.Player.HasSource && f.Handler.Seen.Count == 0, "cover colours opened media or account network");
        }
        finally { mini.Close(); }
    }
}
