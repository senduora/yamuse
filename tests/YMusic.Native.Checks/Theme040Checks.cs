using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using YMusic.Native;

internal static partial class Program
{
    private static async Task CheckTheme040()
    {
        await Run("Theme040: System follows injected changes while explicit modes stay fixed and shared brushes remain mutable", () =>
        {
            var previousDetector = ThemeManager.SystemLightDetector; var previousMode = ThemeManager.Mode;
            var systemLight = false; var events = 0; EventHandler changed = (_, _) => events++;
            try
            {
                ThemeManager.SystemLightDetector = () => systemLight; ThemeManager.Initialize(Application.Current); ThemeManager.SetMode("System");
                Assert(ThemeManager.Mode == "System" && (!ThemeManager.IsLight || SystemParameters.HighContrast), "System did not use the injected detector");
                var keys = new[] { "BackgroundBrush", "PanelBrush", "ForegroundBrush", "MutedBrush", "TrackBrush", "DownloadBrush", "BorderBrush", "InputBrush", "ControlBrush", "HoverBrush", "ToastSurfaceBrush", "ToastBorderBrush", "CoverPlaceholderBrush", "SidebarBrush" };
                var resources = keys.ToDictionary(key => key, key => (SolidColorBrush)Application.Current.Resources[key]);
                var accent = Application.Current.Resources["AccentBrush"];
                var codeBrush = ThemeManager.Brush("#181A20"); var mutedCodeBrush = ThemeManager.Brush("#989BA7");
                ThemeManager.Changed += changed; ThemeManager.SetMode("Light");
                Assert(ThemeManager.IsLight || SystemParameters.HighContrast, "Light mode stayed dark");
                foreach (var key in keys) Assert(ReferenceEquals(resources[key], Application.Current.Resources[key]) && !resources[key].CanFreeze,
                    "theme switch replaced or froze a shared resource: " + key);
                Assert(ReferenceEquals(codeBrush, ThemeManager.Brush("#181A20")) && ReferenceEquals(mutedCodeBrush, ThemeManager.Brush("#989BA7"))
                    && !codeBrush.CanFreeze && !mutedCodeBrush.CanFreeze, "code-created views lost stable mutable brushes");
                Assert(ReferenceEquals(accent, Application.Current.Resources["AccentBrush"]), "neutral theme manager replaced the cover accent");
                if (!SystemParameters.HighContrast)
                {
                    Assert(Theme040Contrast(resources["ForegroundBrush"].Color, resources["BackgroundBrush"].Color) >= 7
                        && Theme040Contrast(resources["MutedBrush"].Color, resources["BackgroundBrush"].Color) >= 4.5
                        && Theme040Contrast(resources["DownloadBrush"].Color, resources["BackgroundBrush"].Color) >= 4.5,
                        "light text or download labels lack readable contrast");
                    ThemeManager.SetMode("Dark"); systemLight = true; ThemeManager.RefreshSystemTheme();
                    Assert(!ThemeManager.IsLight, "a system event overrode explicit Dark");
                    ThemeManager.SetMode("Light"); systemLight = false; ThemeManager.RefreshSystemTheme();
                    Assert(ThemeManager.IsLight, "a system event overrode explicit Light");
                    ThemeManager.SetMode("System"); Assert(!ThemeManager.IsLight, "returning to System retained explicit Light");
                    var before = events; systemLight = true; ThemeManager.RefreshSystemTheme();
                    Assert(ThemeManager.IsLight && events == before + 1, "injected system change did not apply exactly once");
                    before = events; ThemeManager.RefreshSystemTheme(); Assert(events == before, "an unchanged system theme generated another palette change");
                }
                ThemeManager.SetMode("unknown"); Assert(ThemeManager.Mode == "System", "unknown persisted mode did not fall back to System");
            }
            finally { ThemeManager.Changed -= changed; ThemeManager.SystemLightDetector = previousDetector; ThemeManager.SetMode(previousMode); ThemeManager.RefreshSystemTheme(); }
            return Task.CompletedTask;
        });
        await Run("Theme040: light accent links meet 4.5 contrast while dark keeps the selected cover colour", () =>
        {
            var previousMode = ThemeManager.Mode;
            try
            {
                foreach (var mode in new[] { "Light", "Dark" })
                {
                    ThemeManager.SetMode(mode);
                    foreach (var color in new[] { Colors.White, Colors.Yellow, Colors.Cyan, Colors.Magenta, Colors.Blue, Colors.Black, Color.FromRgb(231, 187, 89), Color.FromRgb(110, 173, 250) })
                    {
                        var readable = ThemeManager.ReadableAccent(color);
                        if (SystemParameters.HighContrast) continue;
                        if (mode == "Light")
                        {
                            foreach (var key in new[] { "BackgroundBrush", "PanelBrush", "SidebarBrush", "HoverBrush" })
                                Assert(Theme040Contrast(readable, ((SolidColorBrush)Application.Current.Resources[key]).Color) >= 4.5, "light cover accent link is unreadable on " + key + ": " + color);
                        }
                        else Assert(readable == color, "Dark changed a cover's selected accent colour");
                    }
                }
            }
            finally { ThemeManager.SetMode(previousMode); }
            return Task.CompletedTask;
        });
        await Run("Theme040: existing XAML controls and private menu redraw in both themes without replacing their brushes", () =>
        {
            var previousMode = ThemeManager.Mode; var observations = new List<object>();
            try
            {
                var root = new Border { Padding = new Thickness(24), CornerRadius = new CornerRadius(16) };
                root.SetResourceReference(Border.BackgroundProperty, "BackgroundBrush"); root.SetResourceReference(TextElement.ForegroundProperty, "ForegroundBrush");
                var stack = new StackPanel(); root.Child = stack;
                stack.Children.Add(new TextBlock { Text = "Yamuse · Тема оформления", FontSize = 24, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 18) });
                var input = new TextBox { Text = "Поиск по вашей музыке", Margin = new Thickness(0, 0, 0, 14) }; stack.Children.Add(input);
                var combo = new ComboBox { ItemsSource = new[] { "Как в системе", "Белая", "Тёмная" }, SelectedIndex = 0, Margin = new Thickness(0, 0, 0, 14) }; stack.Children.Add(combo);
                stack.Children.Add(new CheckBox { Content = "Показывать обложку в мини-плеере", IsChecked = true, Margin = new Thickness(0, 0, 0, 14) });
                var buttons = new StackPanel { Orientation = Orientation.Horizontal };
                buttons.Children.Add(new Button { Content = "Слушать", Style = (Style)Application.Current.FindResource("AccentButton"), Margin = new Thickness(0, 0, 12, 0) });
                buttons.Children.Add(new Button { Content = "Открыть плейлист" }); stack.Children.Add(buttons);
                var menu = new ContextMenu(); menu.Items.Add(new MenuItem { Header = "Воспроизвести следующим" }); menu.Items.Add(new Separator()); menu.Items.Add(new MenuItem { Header = "Добавить в плейлист" });
                var lastForeground = (SolidColorBrush)Application.Current.Resources["ForegroundBrush"];
                foreach (var mode in new[] { "Light", "Dark" })
                {
                    ThemeManager.SetMode(mode); Theme040Layout(root, new Size(620, 340));
                    Assert(ReferenceEquals(root.Background, Application.Current.Resources["BackgroundBrush"])
                        && ReferenceEquals(input.Foreground, lastForeground) && ReferenceEquals(combo.Foreground, lastForeground), "live styles retained another theme's resources");
                    Theme040Render(root, new Size(620, 340), "theme040-controls-" + mode.ToLowerInvariant() + ".png");
                    Theme040Render(menu, new Size(340, 106), "theme040-menu-" + mode.ToLowerInvariant() + ".png");
                    observations.Add(new { mode, background = ((SolidColorBrush)root.Background).Color.ToString(), foreground = lastForeground.Color.ToString(), input = ((SolidColorBrush)input.Background).Color.ToString() });
                }
            }
            finally { ThemeManager.SetMode(previousMode); }
            Theme040Evidence("controls", new { observations, windowsShown = false, nativePopupsOpened = false });
            return Task.CompletedTask;
        });
        await Run("Theme040: mini player keeps readable live colours and fully transparent rounded corners after resizing in both themes", () =>
        {
            var previousMode = ThemeManager.Mode; var observations = new List<object>();
            MiniPlayerWindow? mini = null;
            try
            {
                mini = new MiniPlayerWindow(); mini.Update("Трек для локальной проверки", "Исполнитель", true, true, true);
                Assert(mini.AllowsTransparency && mini.WindowStyle == WindowStyle.None && mini.ResizeMode == ResizeMode.CanResize
                    && mini.Background is SolidColorBrush { Color.A: 0 } && !mini.IsVisible,
                    "mini player has an opaque rectangular native backing or lost resizing");
                var frame = (Border)mini.Content; mini.Content = null;
                frame.SetResourceReference(TextElement.ForegroundProperty, "ForegroundBrush");
                var stableBackground = frame.Background; var stableInk = mini.Foreground;
                foreach (var mode in new[] { "Light", "Dark" })
                {
                    ThemeManager.SetMode(mode);
                    Assert(ReferenceEquals(frame.Background, stableBackground) && ReferenceEquals(mini.Foreground, stableInk), "existing mini views replaced their theme brushes");
                    Assert(Theme040Contrast(((SolidColorBrush)mini.Foreground).Color, ((SolidColorBrush)frame.Background).Color) >= 7,
                        "mini title lacks readable contrast in " + mode);
                    foreach (var size in new[] { new Size(320, 224), new Size(484, 300), new Size(760, 184), new Size(280, 184) })
                    {
                        var showArtwork = size.Width >= 320 && size.Height >= 224;
                        typeof(MiniPlayerWindow).GetMethod("SetArtworkVisible", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(mini, new object[] { showArtwork });
                        mini.Width = size.Width; mini.Height = size.Height;
                        typeof(MiniPlayerWindow).GetMethod("UpdateResponsiveLayout", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(mini, null);
                        Theme040Layout(frame, size);
                        Assert(frame.Clip is RectangleGeometry clip && clip.Rect.Size == size && clip.RadiusX == 14 && clip.RadiusY == 14, "mini content clipping did not follow resize");
                        foreach (var scale in new[] { 1d, 2d })
                        {
                            var bitmap = Theme040Bitmap(frame, size, scale); var width = bitmap.PixelWidth; var height = bitmap.PixelHeight;
                            var pixels = new byte[width * height * 4]; bitmap.CopyPixels(pixels, width * 4, 0);
                            foreach (var pixel in new[] { (0, 0), (width - 1, 0), (0, height - 1), (width - 1, height - 1), (2, 2), (width - 3, 2), (2, height - 3), (width - 3, height - 3) })
                                Assert(pixels[(pixel.Item2 * width + pixel.Item1) * 4 + 3] == 0, "mini paints a rectangular corner in " + mode + " at " + size + " scale " + scale);
                            Assert(pixels[((height / 2) * width + width / 2) * 4 + 3] == 255, "mini's central surface became transparent");
                            if (scale == 1 && size.Width is 320 or 760) Theme040Save(bitmap, "theme040-mini-" + mode.ToLowerInvariant() + "-" + (int)size.Width + ".png");
                        }
                        observations.Add(new { mode, size.Width, size.Height, showArtwork, mini.MinWidth, mini.MinHeight, cornerAlpha = 0, scales = new[] { 1, 2 } });
                    }
                }
                mini.Content = frame;
            }
            finally { mini?.Close(); ThemeManager.SetMode(previousMode); }
            Theme040Evidence("mini", new { observations, windowsShown = false, realAccountUsed = false, externalRequests = 0 });
            return Task.CompletedTask;
        });
        await Run("Theme040: switching completed hold clocks exposes exact new colours before yielding or notifying frame consumers", async () =>
        {
            var previousMode = ThemeManager.Mode; Window? window = null;
            var observations = new List<object>(); var notifications = new List<(bool Light, Color Background, Color Foreground)>();
            EventHandler changed = (_, _) => notifications.Add((ThemeManager.IsLight,
                ((SolidColorBrush)Application.Current.Resources["BackgroundBrush"]).Color,
                ((SolidColorBrush)Application.Current.Resources["ForegroundBrush"]).Color));
            try
            {
                ThemeManager.SetMode("Dark");
                var background = (SolidColorBrush)Application.Current.Resources["BackgroundBrush"];
                var foreground = (SolidColorBrush)Application.Current.Resources["ForegroundBrush"];
                var codeBrush = ThemeManager.Brush("#181A20");
                window = new Window { ShowActivated = false, ShowInTaskbar = false };
                window.SetResourceReference(System.Windows.Window.BackgroundProperty, "BackgroundBrush"); window.SetResourceReference(System.Windows.Window.ForegroundProperty, "ForegroundBrush");
                var surface = new Border { Background = background, Child = new TextBlock { Text = "Private held-clock seed", Foreground = foreground } };
                Theme040Render(surface, new Size(240, 40), "theme040-clock-seed.png");
                // Let the initial held clock reach its completed state once.
                // Every subsequent switch and assertion below is synchronous.
                await Task.Delay(40);
                ThemeManager.Changed += changed;
                foreach (var mode in new[] { "Light", "Dark", "Light", "Dark" })
                {
                    ThemeManager.SetMode(mode);
                    var light = mode == "Light";
                    var expectedBackground = (Color)ColorConverter.ConvertFromString(light ? "#F5F6F8" : "#101114");
                    var expectedForeground = (Color)ColorConverter.ConvertFromString(light ? "#23252B" : "#F3F1F0");
                    var expectedCode = (Color)ColorConverter.ConvertFromString(light ? "#FFFFFF" : "#181A20");
                    observations.Add(new { mode, ThemeManager.IsLight, background = background.Color.ToString(), foreground = foreground.Color.ToString(), code = codeBrush.Color.ToString(),
                        expectedBackground = expectedBackground.ToString(), expectedForeground = expectedForeground.ToString(), beforeDispatcherYield = true });
                    if (SystemParameters.HighContrast) continue;
                    Assert(background.Color == expectedBackground && foreground.Color == expectedForeground && codeBrush.Color == expectedCode,
                        "SetMode(" + mode + ") returned with stale held-clock colours: background=" + background.Color + ", foreground=" + foreground.Color + ", code=" + codeBrush.Color);
                    Assert(ReferenceEquals(window.Background, background) && ReferenceEquals(window.Foreground, foreground)
                        && WindowAppearance.ToColorRef(((SolidColorBrush)window.Background).Color) == WindowAppearance.ToColorRef(expectedBackground),
                        "a synchronous caption consumer observes another theme's background");
                    Assert(notifications[^1] == (light, expectedBackground, expectedForeground), "Changed notified frame consumers before the palette committed");
                    if (light) Assert(Theme040Contrast(ThemeManager.ReadableAccent(Colors.White), background.Color) >= 4.5,
                        "readable accent was computed for Light while the visible background retained Dark");
                }
            }
            finally
            {
                ThemeManager.Changed -= changed; window?.Close(); ThemeManager.SetMode(previousMode);
                Theme040Evidence("synchronous-switch", new { observations, notifications = notifications.Select(item => new { item.Light, background = item.Background.ToString(), foreground = item.Foreground.ToString() }), windowsShown = false });
            }
        });
    }

    private static void Theme040Layout(FrameworkElement root, Size size) { root.Measure(size); root.Arrange(new Rect(new Point(), size)); root.UpdateLayout(); }
    private static RenderTargetBitmap Theme040Bitmap(FrameworkElement root, Size size, double scale = 1)
    {
        Theme040Layout(root, size); var bitmap = new RenderTargetBitmap((int)Math.Round(size.Width * scale), (int)Math.Round(size.Height * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32); bitmap.Render(root); return bitmap;
    }
    private static void Theme040Save(BitmapSource bitmap, string name) { var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var output = File.Create(Path.Combine(_directory, name)); encoder.Save(output); }
    private static void Theme040Render(FrameworkElement root, Size size, string name) => Theme040Save(Theme040Bitmap(root, size), name);
    private static double Theme040Contrast(Color left, Color right)
    {
        static double Channel(byte value) { var s = value / 255d; return s <= .04045 ? s / 12.92 : Math.Pow((s + .055) / 1.055, 2.4); }
        static double Luma(Color value) => .2126 * Channel(value.R) + .7152 * Channel(value.G) + .0722 * Channel(value.B);
        var a = Luma(left); var b = Luma(right); return (Math.Max(a, b) + .05) / (Math.Min(a, b) + .05);
    }
    private static void Theme040Evidence(string name, object data) => File.WriteAllText(Path.Combine(_directory, "theme040-" + name + ".json"), JsonSerializer.Serialize(new
        { data, productSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(MainWindow).Assembly.Location))), runtime = Environment.Version.ToString() }, new JsonSerializerOptions { WriteIndented = true }));
}
