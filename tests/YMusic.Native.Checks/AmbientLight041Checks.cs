using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using YMusic.Native;

internal static partial class Program
{
    private static async Task CheckAmbientLight041()
    {
        var previousMode = ThemeManager.Mode;
        try
        {
            await Run("AmbientLight041: olive black-band and saturated covers produce measurable soft colour with readable light text", AmbientLight041Colour);
            await Run("AmbientLight041: monochrome and near-neutral covers remain neutral in rendered light backgrounds", AmbientLight041Neutral);
            await Run("AmbientLight041: light presentation follows interrupted cover fades while dark pixels and idle motion stay unchanged", AmbientLight041Transition);
        }
        finally { ThemeManager.SetMode(previousMode); }
    }

    private static async Task AmbientLight041Colour()
    {
        ThemeManager.SetMode("Light");
        var results = new List<object>();
        foreach (var (name, color) in new[] { ("olive-black-bands", Color.FromRgb(66, 75, 24)), ("saturated-teal", Color.FromRgb(20, 125, 175)), ("saturated-magenta", Color.FromRgb(170, 25, 150)) })
        {
            var cover = AmbientLight041Cover(color);
            var palette = CoverPalette.Extract(cover)!.Value;
            var accent = new SolidColorBrush(palette);
            var ambient = new AmbientBackdrop { AccentBrush = accent, IsMotionEnabled = false };
            try
            {
                var scene = AmbientLight041Scene(ambient, cover, accent, name);
                await AmbientLight041Layout(scene);
                var metrics = AmbientLight041Metrics(ambient);
                Assert(metrics.PeakChroma >= 15 && metrics.MeanChroma >= 5,
                    "Light atmosphere washed the cover into a neutral field: " + name + ", " + metrics);
                Assert(metrics.MinimumTextContrast >= 4.5 && metrics.MinimumMutedContrast >= 4.5
                    && CoverPalette.Contrast(palette, CoverPalette.Ink) >= 4.5,
                    "Light cover atmosphere reduced text or play-control readability: " + name + ", " + metrics);
                Assert(accent.Color == palette, "Decorative Light mapping changed the shared accent");
                AmbientLight041Save(scene, "ambient-light-041-" + name + ".png");
                results.Add(new { name, extractedPalette = palette.ToString(), metrics });
            }
            finally { ambient.Release(); }
        }
        File.WriteAllText(System.IO.Path.Combine(_directory, "ambient-light-041-colour.json"), JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static async Task AmbientLight041Neutral()
    {
        ThemeManager.SetMode("Light");
        foreach (var (name, color) in new[] { ("monochrome", Color.FromRgb(100, 100, 100)), ("near-neutral", Color.FromRgb(95, 97, 96)) })
        {
            var cover = AmbientLight041Cover(color);
            var palette = CoverPalette.Extract(cover)!.Value;
            var ambient = new AmbientBackdrop { AccentBrush = new SolidColorBrush(palette), IsMotionEnabled = false };
            try
            {
                var scene = AmbientLight041Scene(ambient, cover, ambient.AccentBrush!, name);
                await AmbientLight041Layout(scene);
                var metrics = AmbientLight041Metrics(ambient);
                Assert(AmbientLight041Chroma(palette) <= 2 && metrics.PeakChroma <= 4,
                    "A neutral cover acquired an invented saturated tint: " + name + ", " + metrics);
                Assert(metrics.MinimumTextContrast >= 4.5 && metrics.MinimumMutedContrast >= 4.5,
                    "Neutral Light atmosphere lost text readability");
                AmbientLight041Save(scene, "ambient-light-041-" + name + ".png");
            }
            finally { ambient.Release(); }
        }
    }

    private static async Task AmbientLight041Transition()
    {
        ThemeManager.SetMode("Dark");
        var cover = AmbientLight041Cover(Color.FromRgb(66, 75, 24));
        var image = new Image { Source = cover };
        using var controller = new CoverAccentController(image, new ResourceDictionary(), () => cover);
        await controller.PendingAnalysis;
        await AmbientLight041Frame(controller, 5.01);
        var primary = controller.Brushes["AccentBrush"];
        var originalColor = primary.Color;
        var ambient = new AmbientBackdrop { AccentBrush = primary, IsMotionEnabled = false };
        try
        {
            var scene = AmbientLight041Scene(ambient, cover, primary, "Переход обложки");
            await AmbientLight041Layout(scene);
            var dark = Ambient015Bitmap(ambient);
            var darkPixels = AmbientLight041Pixels(dark);
            var brushes = EpVisuals<Ellipse>(ambient).Select(cloud => (RadialGradientBrush)cloud.Fill).ToArray();
            var stops = brushes.SelectMany(brush => brush.GradientStops).ToArray();
            var first = brushes[0].GradientStops[0].Color;
            var veil = (LinearGradientBrush)ambient.Children.OfType<Border>().Single().Background;
            Assert(first.R == originalColor.R && first.G == originalColor.G && first.B == originalColor.B
                && veil.GradientStops.Select(stop => stop.Color.A).SequenceEqual(new byte[] { 90, 135, 211 })
                && veil.GradientStops.All(stop => stop.Color.R == 10 && stop.Color.G == 14 && stop.Color.B == 22),
                "Dark atmosphere no longer has its original cover tint and neutral veil");
            var motion = Ambient015Field<Storyboard>(ambient, "_motion");
            var transform = EpVisuals<Ellipse>(ambient).First().RenderTransform.Value;
            controller.Retarget(CoverPalette.Extract(AmbientLight041Cover(Color.FromRgb(20, 125, 175)))!.Value);
            var target = controller.TargetColor;
            if (SystemParameters.ClientAreaAnimation)
            {
                Assert(controller.Clocks.Values.All(clock => clock.Timeline.Duration.TimeSpan == TimeSpan.FromSeconds(5)), "Cover fade duration changed");
                await AmbientLight041Frame(controller, 2.5);
            }
            var shown = primary.Color;
            var primaryClock = controller.Clocks.GetValueOrDefault("AccentBrush");
            var shownTime = primaryClock?.CurrentTime;
            var shownPaused = primaryClock?.IsPaused;
            ThemeManager.SetMode("Light");
            await AmbientLight041Layout(scene);
            Assert(primary.Color == shown && controller.TargetColor == target
                && ReferenceEquals(primaryClock, controller.Clocks.GetValueOrDefault("AccentBrush"))
                && (!SystemParameters.ClientAreaAnimation || primaryClock!.IsPaused && primaryClock.CurrentTime == shownTime),
                $"Changing the decorative Light presentation restarted or jumped the frozen cover frame: shown={shown}, after={primary.Color}, targetBefore={target}, targetAfter={controller.TargetColor}, sameClock={ReferenceEquals(primaryClock, controller.Clocks.GetValueOrDefault("AccentBrush"))}, timeBefore={shownTime}, timeAfter={primaryClock?.CurrentTime}, pausedBefore={shownPaused}, pausedAfter={primaryClock?.IsPaused}");
            if (SystemParameters.ClientAreaAnimation)
                Assert(primaryClock!.Timeline.Duration.TimeSpan == TimeSpan.FromSeconds(5)
                    && new[] { "AccentTextBrush", "AccentSoftBrush", "AccentSelectionBrush" }.All(key => controller.Clocks[key].Timeline.Duration.TimeSpan == TimeSpan.FromSeconds(2.5)),
                    "Theme change restarted the five-second fade or changed its remaining deadline");
            Assert(brushes.SequenceEqual(EpVisuals<Ellipse>(ambient).Select(cloud => cloud.Fill))
                && stops.SequenceEqual(brushes.SelectMany(brush => brush.GradientStops)), "Theme change rebuilt the live gradient bindings");
            var metrics = AmbientLight041Metrics(ambient);
            Assert(metrics.PeakChroma >= 15 && metrics.MinimumMutedContrast >= 4.5, "Mid-transition Light atmosphere lost colour or readability");
            AmbientLight041Save(scene, "ambient-light-041-transition.png");
            controller.Retarget(originalColor);
            if (SystemParameters.ClientAreaAnimation)
            {
                await AmbientLight041Frame(controller, 0);
                Assert(primary.Color == shown, "Interrupted cover fade jumped instead of starting from its displayed colour");
            }
            await AmbientLight041Frame(controller, 5.01);
            ThemeManager.SetMode("Dark");
            await AmbientLight041Layout(scene);
            Assert(primary.Color == originalColor && AmbientLight041Pixels(Ambient015Bitmap(ambient)).SequenceEqual(darkPixels),
                "Light-to-Dark roundtrip changed the settled Dark background pixels");
            Assert(ReferenceEquals(motion, Ambient015Field<Storyboard>(ambient, "_motion"))
                && !ambient.IsMotionEnabled && !Ambient015Field<bool>(ambient, "_begun") && !Ambient015Field<bool>(ambient, "_running")
                && EpVisuals<Ellipse>(ambient).First().RenderTransform.Value == transform,
                "Finite theme/colour feedback started idle atmosphere motion");
            AmbientLight041Save(scene, "ambient-light-041-dark-roundtrip.png");
        }
        finally { ambient.Release(); }
    }

    private static async Task AmbientLight041Frame(CoverAccentController controller, double seconds)
    {
        // A new WPF clock must enter Active before a seek can replace the
        // previous held brush snapshot. Pause also commits on a timing tick;
        // settle it before recording a frame that must survive a render yield.
        Ts040Seek(controller, seconds);
        await Dispatcher.Yield(DispatcherPriority.Render);
        var clocks = controller.Clocks.Values.ToArray();
        if (seconds >= CoverAccentController.TransitionDuration.TotalSeconds || clocks.Length == 0) return;
        try { await Until(() => clocks.All(clock => clock.IsPaused)); }
        catch (OperationCanceledException)
        {
            throw new InvalidOperationException("Test cover clocks did not pause: "
                + string.Join("; ", clocks.Select(clock => $"state={clock.CurrentState}, paused={clock.IsPaused}, time={clock.CurrentTime}")));
        }
        var frame = TimeSpan.FromSeconds(seconds);
        foreach (var clock in clocks) clock.Controller!.SeekAlignedToLastTick(frame, TimeSeekOrigin.BeginTime);
        await Dispatcher.Yield(DispatcherPriority.DataBind);
        Assert(clocks.All(clock => clock.IsPaused && clock.CurrentTime == frame),
            "Test cover clocks did not retain the exact paused frame: " + seconds);
    }

    private static BitmapSource AmbientLight041Cover(Color color)
        => Accent015Bitmap(64, 64, (_, y) => y < 12 || y >= 52 ? Colors.Black : color);

    private static Grid AmbientLight041Scene(AmbientBackdrop ambient, BitmapSource cover, SolidColorBrush accent, string title)
    {
        var scene = new Grid { Width = 1000, Height = 700 };
        scene.Children.Add(ambient);
        scene.Children.Add(new Image { Source = cover, Width = 160, Height = 160, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(32) });
        var content = new StackPanel { HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(40, 0, 0, 36) };
        content.Children.Add(new TextBlock { Text = title, FontSize = 30, FontWeight = FontWeights.SemiBold, Foreground = (Brush)Application.Current.Resources["ForegroundBrush"] });
        content.Children.Add(new TextBlock { Text = "Синтетическая обложка · Light backdrop fixture", FontSize = 16, Margin = new Thickness(0, 8, 0, 16), Foreground = (Brush)Application.Current.Resources["MutedBrush"] });
        content.Children.Add(new Button { Content = "Воспроизвести", Width = 180, Height = 44, HorizontalAlignment = HorizontalAlignment.Left, Background = accent, Foreground = new SolidColorBrush(CoverPalette.Ink) });
        scene.Children.Add(content);
        return scene;
    }

    private static async Task AmbientLight041Layout(FrameworkElement scene)
    {
        var size = new Size(1000, 700);
        scene.Measure(size); scene.Arrange(new Rect(size)); scene.UpdateLayout();
        await Dispatcher.Yield(DispatcherPriority.DataBind);
        await Dispatcher.Yield(DispatcherPriority.Render);
    }

    private sealed record AmbientLight041Measurement(double MeanChroma, int PeakChroma, double MinimumTextContrast, double MinimumMutedContrast);
    private static AmbientLight041Measurement AmbientLight041Metrics(AmbientBackdrop ambient)
    {
        var bitmap = Ambient015Bitmap(ambient);
        var pixels = AmbientLight041Pixels(bitmap);
        var foreground = ((SolidColorBrush)Application.Current.Resources["ForegroundBrush"]).Color;
        var muted = ((SolidColorBrush)Application.Current.Resources["MutedBrush"]).Color;
        var samples = new List<Color>();
        for (var y = 32; y < bitmap.PixelHeight - 32; y += 32)
            for (var x = 32; x < bitmap.PixelWidth - 32; x += 32) samples.Add(Ambient015Pixel(bitmap, pixels, x, y));
        Assert(samples.Count > 100 && samples.All(color => color.A == 255), "Light fixture did not render a complete opaque backdrop");
        return new(samples.Average(color => AmbientLight041Chroma(color)), samples.Max(color => AmbientLight041Chroma(color)),
            samples.Min(color => CoverPalette.Contrast(color, foreground)), samples.Min(color => CoverPalette.Contrast(color, muted)));
    }
    private static int AmbientLight041Chroma(Color color) => Math.Max(color.R, Math.Max(color.G, color.B)) - Math.Min(color.R, Math.Min(color.G, color.B));
    private static byte[] AmbientLight041Pixels(BitmapSource bitmap)
    {
        var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
        bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0); return pixels;
    }
    private static void AmbientLight041Save(FrameworkElement scene, string fileName)
    {
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(Ambient015Bitmap(scene)));
        using var file = File.Create(System.IO.Path.Combine(_directory, fileName)); encoder.Save(file);
    }
}
