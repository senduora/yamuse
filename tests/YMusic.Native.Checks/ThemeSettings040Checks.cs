using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using YMusic.Native;

internal static partial class Program
{
    private static async Task CheckThemeSettings040()
    {
        await Run("ThemeSettings040: absent legacy malformed and roundtrip preferences preserve System and the gold default", Ts040Preferences);
        await Run("ThemeSettings040: theme changes reuse the cover palette and keep five second readable accent transitions", Ts040Palette);
        await Run("ThemeSettings040: settings apply and persist theme immediately without showing a window or resetting other preferences", Ts040Settings);
    }

    private static Task Ts040Preferences()
    {
        var directory = Path.Combine(_directory, "theme040-preferences");
        var store = new PreferencesStore(directory);
        var initial = store.Load();
        Assert(initial.Theme == "System" && initial.StaticAccentColor == "#E7BB59" && initial.AccentFromCover
            && !File.Exists(store.FilePath), "absent preferences lost the System/gold defaults or created a file");
        Directory.CreateDirectory(directory);
        foreach (var jsonTheme in new[] { "\"unknown\"", "\"\"", "null", "17", "[]", "{\"mode\":\"Light\"}" })
        {
            File.WriteAllText(store.FilePath, $$"""{"theme":{{jsonTheme}},"volume":0.31,"crossfadeSeconds":6.5,"staticAccentColor":"#ea571c","accentFromCover":false,"concurrentDownloads":7}""");
            var value = store.Load();
            Assert(value.Theme == "System" && !store.LastLoadRecovered && value.Volume == .31 && value.CrossfadeSeconds == 6.5
                && value.StaticAccentColor == "#EA571C" && !value.AccentFromCover && value.ConcurrentDownloads == 7,
                "malformed theme reset an unrelated saved preference: " + jsonTheme);
        }
        File.WriteAllText(store.FilePath, """{"volume":0.31,"crossfadeSeconds":6.5,"concurrentDownloads":7}""");
        var legacy = store.Load();
        Assert(legacy.Theme == "System" && legacy.StaticAccentColor == "#E7BB59" && legacy.AccentFromCover,
            "legacy schema did not acquire System/gold defaults");
        foreach (var mode in new[] { "System", "Light", "Dark" })
        {
            store.Save(legacy with { Theme = mode, StaticAccentColor = "#6eadfa" });
            var saved = store.Load();
            Assert(saved.Theme == mode && saved.StaticAccentColor == "#6EADFA" && saved.AccentFromCover
                && saved.Volume == .31 && saved.CrossfadeSeconds == 6.5 && saved.ConcurrentDownloads == 7, "theme roundtrip changed another preference");
            using var json = JsonDocument.Parse(File.ReadAllBytes(store.FilePath));
            Assert(json.RootElement.GetProperty("theme").GetString() == mode, "theme was persisted as its resolved system colour");
        }
        store.Save(legacy with { Theme = "invalid", StaticAccentColor = "invalid" });
        Assert(store.Load().Theme == "System" && store.Load().StaticAccentColor == "#E7BB59", "invalid saved values lost safe defaults");
        Assert(CoverPalette.Fallback == Color.FromRgb(231, 187, 89), "palette fallback is not the new gold");
        return Task.CompletedTask;
    }

    private static async Task Ts040Palette()
    {
        var previousMode = ThemeManager.Mode;
        var detector = ThemeManager.SystemLightDetector;
        ThemeManager.Initialize(Application.Current);
        ThemeManager.SystemLightDetector = () => false;
        ThemeManager.SetMode("Dark");
        var analyses = 0;
        var image = new Image();
        var controller = new CoverAccentController(image, new ResourceDictionary(), () => null,
            (_, _) => { analyses++; return Task.FromResult<Color?>(Colors.Blue); });
        try
        {
            var bitmap = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[] { 255, 0, 0, 255 }, 4);
            bitmap.Freeze(); image.Source = bitmap; await controller.PendingAnalysis;
            Ts040Seek(controller, 5.01);
            var target = controller.TargetColor;
            var refs = controller.Brushes.ToDictionary(pair => pair.Key, pair => pair.Value);
            Assert(refs.Count == 7 && analyses == 1, "cover analysis or shared palette shape drifted");
            foreach (var mode in new[] { "Light", "Dark", "Light" })
            {
                ThemeManager.SetMode(mode);
                Assert(controller.TargetColor == target && controller.Brushes["AccentBrush"].Color == target && analyses == 1
                    && controller.Brushes.All(pair => ReferenceEquals(refs[pair.Key], pair.Value) && !pair.Value.IsFrozen),
                    "theme change replaced the primary accent, shared brushes or reanalysed artwork");
                if (ThemeManager.IsLight)
                {
                    var text = controller.Brushes["AccentTextBrush"].Color;
                    foreach (var key in new[] { "BackgroundBrush", "PanelBrush", "SidebarBrush", "HoverBrush" })
                        Assert(CoverPalette.Contrast(text, ((SolidColorBrush)Application.Current.Resources[key]).Color) >= 4.5,
                            "light accent text fails contrast on " + key);
                    foreach (var key in new[] { "AccentSoftBrush", "AccentSelectionBrush" })
                    {
                        var surface = controller.Brushes[key].Color;
                        Assert(Math.Min(surface.R, Math.Min(surface.G, surface.B)) > 180 && CoverPalette.Contrast(text, surface) >= 4.5,
                            "light accent support surface became dark or lost text contrast: " + key);
                    }
                }
            }
            var from = controller.Brushes["AccentBrush"].Color;
            controller.Retarget(Colors.Cyan);
            if (SystemParameters.ClientAreaAnimation)
            {
                Assert(controller.Clocks.Count == 7 && controller.Clocks.Values.All(clock => clock.Timeline.Duration.TimeSpan == TimeSpan.FromSeconds(5)),
                    "ordinary colour change lost the five second clocks");
                foreach (var seconds in new[] { 0d, .25, 1, 2.5, 4.9, 5.01 })
                {
                    Ts040Seek(controller, seconds);
                    var current = controller.Brushes["AccentBrush"].Color;
                    if (seconds == 0) Assert(current == from, "ordinary transition jumped at its start");
                    if (seconds == 2.5) Assert(current != from && current != controller.TargetColor, "ordinary transition lost its midpoint");
                    Assert(CoverPalette.Contrast(controller.Brushes["AccentTextBrush"].Color,
                        ((SolidColorBrush)Application.Current.Resources["HoverBrush"]).Color) >= 4.5, "intermediate light accent text is unreadable");
                }
            }
            Assert(controller.Brushes["AccentBrush"].Color == controller.TargetColor, "ordinary transition did not finish");
            controller.Configure(false, "#E7BB59"); Ts040Seek(controller, 5.01);
            Assert(controller.TargetColor == CoverPalette.Fallback && analyses == 1, "static gold choice triggered artwork analysis");
            controller.Dispose();
            var disposed = controller.Brushes.ToDictionary(pair => pair.Key, pair => pair.Value.Color);
            ThemeManager.SetMode("Dark");
            Assert(controller.Clocks.Count == 0 && controller.Brushes.All(pair => pair.Value.Color == disposed[pair.Key]) && analyses == 1,
                "disposed palette retained a theme subscription or work");
        }
        finally { controller.Dispose(); ThemeManager.SystemLightDetector = detector; ThemeManager.SetMode(previousMode); }
    }

    private static void Ts040Seek(CoverAccentController controller, double seconds)
    {
        foreach (var clock in controller.Clocks.Values)
        {
            // Enter Active before seeking past the deadline: skipping the first
            // active tick can retain the previous held colour snapshot in WPF.
            clock.Controller!.Begin();
            clock.Controller.SeekAlignedToLastTick(TimeSpan.Zero, TimeSeekOrigin.BeginTime);
            clock.Controller.Pause();
            clock.Controller.SeekAlignedToLastTick(TimeSpan.FromSeconds(seconds), TimeSeekOrigin.BeginTime);
        }
    }

    private static Task Ts040Settings()
    {
        var previousMode = ThemeManager.Mode;
        var detector = ThemeManager.SystemLightDetector;
        ThemeManager.SystemLightDetector = () => false;
        var directory = Path.Combine(_directory, "theme040-settings");
        var store = new PreferencesStore(directory);
        store.Save(new() { Theme = "System", Volume = .31, CrossfadeSeconds = 6.5, PlaybackSpeed = 1.25,
            ConcurrentDownloads = 7, AccentFromCover = true, StaticAccentColor = "#EA571C", UpdateCheckFrequency = UpdateCheckFrequency.Off });
        var saved = File.ReadAllBytes(store.FilePath);
        var network = 0;
        HttpMessageHandler Handler() => new FakeHandler((_, _) => { network++; throw new InvalidOperationException("theme fixture attempted network access"); });
        var player = new NativePlayer(new AudioBuffer(Path.Combine(directory, "audio"), Handler()));
        var offline = new OfflineLibrary(Path.Combine(directory, "offline"), new AudioBuffer(Path.Combine(directory, "downloads"), Handler()));
        var window = new MainWindow(api: new MusicApi(Handler()), store: new SessionStore(Path.Combine(directory, "session")),
            restore: false, diagnosticsDirectory: directory, offline: offline, player: player, preferences: store);
        try
        {
            var settings = (Button)window.FindName("SettingsButton");
            typeof(MainWindow).GetMethod("Settings_Click", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, [settings, new RoutedEventArgs()]);
            var combo = (ComboBox)window.FindName("SettingsTheme");
            Assert(combo.SelectedValue is "System" && !ThemeManager.IsLight && File.ReadAllBytes(store.FilePath).SequenceEqual(saved),
                "opening settings changed the default mode or rewrote saved choices");
            Assert(combo.Items.Cast<ComboBoxItem>().Select(item => (string)item.Content).SequenceEqual(new[] { "Как в системе", "Белая", "Тёмная" }),
                "theme choices are missing or have the wrong labels");
            foreach (var mode in new[] { "Light", "Dark", "System" })
            {
                combo.SelectedValue = mode;
                var actual = store.Load();
                Assert(actual.Theme == mode && ThemeManager.Mode == mode && (SystemParameters.HighContrast || ThemeManager.IsLight == (mode == "Light"))
                    && actual.Volume == .31 && actual.PlaybackSpeed == 1.25 && actual.CrossfadeSeconds == 6.5
                    && actual.ConcurrentDownloads == 7 && actual.StaticAccentColor == "#EA571C" && actual.AccentFromCover
                    && actual.UpdateCheckFrequency == UpdateCheckFrequency.Off, "live theme change did not persist or reset another preference");
            }
            ThemeManager.SystemLightDetector = () => true; ThemeManager.RefreshSystemTheme();
            Assert((SystemParameters.HighContrast || ThemeManager.IsLight) && store.Load().Theme == "System" && combo.SelectedValue is "System", "system refresh persisted its resolved light mode");
            var toast = (Border)window.FindName("StatusNotification");
            Assert(ReferenceEquals(toast.Parent, window.FindName("FooterContent")) && Grid.GetColumn(toast) == 1 && Panel.GetZIndex(toast) == 300
                && toast.HorizontalAlignment == HorizontalAlignment.Right && toast.VerticalAlignment == VerticalAlignment.Bottom
                && toast.Background is null && ((TextBlock)window.FindName("StatusText")).TextWrapping == TextWrapping.NoWrap,
                "theme conversion changed the plain footer notification layout");
            foreach (var (name, key) in new[] { ("Sidebar", "SidebarBrush"), ("PlayerBar", "PanelBrush"), ("MetricsBar", "BackgroundBrush") })
                Assert(((SolidColorBrush)((Border)window.FindName(name)).Background).Color == ((SolidColorBrush)Application.Current.Resources[key]).Color,
                    "main neutral surface did not follow the live theme: " + name);
            Assert(window.FindName("SettingsApplyButton") is null && window.FindName("SettingsCancelButton") is null
                && !window.IsLoaded && new WindowInteropHelper(window).Handle == IntPtr.Zero && network == 0 && !player.HasSource,
                "theme settings created a native window, footer, network call or playback");
        }
        finally { window.Close(); ThemeManager.SystemLightDetector = detector; ThemeManager.SetMode(previousMode); }
        return Task.CompletedTask;
    }
}
