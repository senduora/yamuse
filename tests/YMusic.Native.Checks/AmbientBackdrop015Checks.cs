using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
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
    private static async Task CheckAmbientBackdrop015()
    {
        await Run("Ambient015: soft native gradient clouds follow live accent colours without rebuilding brushes", Ambient015Colours);
        await Run("Ambient015: native motion advances and pauses for hidden, unloaded, minimized and closed surfaces", Ambient015Lifecycle);
        var previous = typeof(Artwork).GetField("_cache", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null);
        var requests = 0; var cover = Ambient015Artwork();
        using var cache = new ArtworkCache(System.IO.Path.Combine(_directory, "ambient015-artwork"), new FakeHandler((request, _) =>
        {
            Assert(ArtworkUri.Valid(request.RequestUri!) && request.Headers.Authorization is null && !request.Headers.Contains("Cookie"), "ambient artwork escaped anonymous local fixture");
            Interlocked.Increment(ref requests);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(cover) });
        }));
        Artwork.Cache = cache;
        try { await Run("Ambient015: wave and centered or lyrics expanded players retain layout, contrast and input at both sizes", Ambient015Integrated); }
        finally { typeof(Artwork).GetField("_cache", BindingFlags.NonPublic | BindingFlags.Static)!.SetValue(null, previous); }
        File.WriteAllText(System.IO.Path.Combine(_directory, "ambient015-scope.json"), JsonSerializer.Serialize(new
        {
            realAccountLoaded = false, realMediaRead = false, externalNetworkRequests = 0, globalInputUsed = false,
            fixtureArtworkRequests = requests, fixtureWavMuted = true, nativeMotionEnabled = Motion.CanAnimate,
            renderTier = RenderCapability.Tier >> 16, windowsReducedAnimation = !SystemParameters.ClientAreaAnimation,
            backgroundMechanism = "Four radial gradient clouds, WPF transform clocks, no timer, frame event, blur or artwork decoding",
            motionClockMethod = "SeekAlignedToLastTick deterministically advances native WPF clocks; wholly offscreen windows may throttle real-time rendering",
            checkedAssemblySha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(MainWindow).Assembly.Location)))
        }, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static async Task Ambient015Colours()
    {
        var accent = new SolidColorBrush(Color.FromRgb(36, 170, 183));
        var ambient = new AmbientBackdrop { AccentBrush = accent, Width = 660, Height = 390, CornerRadius = 24 };
        var host = Ambient015Window(ambient);
        try
        {
            host.Show(); host.UpdateLayout(); await Dispatcher.Yield(DispatcherPriority.Render);
            var clouds = EpVisuals<Ellipse>(ambient).ToArray();
            Assert(clouds.Length == 4 && clouds.All(cloud => cloud.Stroke is null && cloud.Effect is null && cloud.Fill is RadialGradientBrush), "ambient is not four native soft radial clouds");
            Assert(!EpVisuals<System.Windows.Shapes.Path>(ambient).Any() && !EpVisuals<UIElement>(ambient).Any(element => element.Effect is not null), "ambient retained a sharp path or live blur effect");
            var brushes = clouds.Select(cloud => (RadialGradientBrush)cloud.Fill).ToArray();
            var stops = brushes.SelectMany(brush => brush.GradientStops).ToArray();
            Assert(brushes.All(brush => brush.GradientStops.Count == 4 && brush.GradientStops[0].Color.A > 200 && brush.GradientStops[^1].Color.A == 0
                && brush.GradientStops[2].Offset > .6 && brush.GradientStops[2].Color.A < 60), "cloud edges lack a long transparent falloff");
            var motion = Ambient015Field<Storyboard>(ambient, "_motion");
            Assert(motion.Children.Count == 16 && motion.Children.Cast<DoubleAnimation>().All(animation => animation.AutoReverse
                && animation.RepeatBehavior == RepeatBehavior.Forever && animation.EasingFunction is SineEase { EasingMode: EasingMode.EaseInOut }
                && animation.Duration.TimeSpan.TotalSeconds * 2 is >= 18 and <= 25 && Timeline.GetDesiredFrameRate(animation) is null), "ambient motion is not native slow eased auto-reverse motion");
            Assert(!typeof(AmbientBackdrop).GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).Any(field => field.FieldType == typeof(DispatcherTimer)), "ambient owns an additional UI timer");
            var first = brushes[0].GradientStops[0];
            Assert(first.Color.R == accent.Color.R && first.Color.G == accent.Color.G && first.Color.B == accent.Color.B, "gradient did not bind the current accent colour");
            var animation = new ColorAnimation(accent.Color, Color.FromRgb(105, 73, 214), TimeSpan.FromSeconds(5));
            var clock = animation.CreateClock(); accent.ApplyAnimationClock(SolidColorBrush.ColorProperty, clock);
            clock.Controller!.SeekAlignedToLastTick(TimeSpan.FromSeconds(2.5), TimeSeekOrigin.BeginTime);
            await Dispatcher.Yield(DispatcherPriority.DataBind);
            Assert(first.Color.R == accent.Color.R && first.Color.G == accent.Color.G && first.Color.B == accent.Color.B
                && first.Color.G < 150 && first.Color.B > 190, "live five-second brush colour did not reach the native gradient");
            var intermediate = accent.Color;
            var retarget = new ColorAnimation(intermediate, Color.FromRgb(64, 185, 100), TimeSpan.FromSeconds(5)).CreateClock();
            accent.ApplyAnimationClock(SolidColorBrush.ColorProperty, retarget, HandoffBehavior.SnapshotAndReplace);
            retarget.Controller!.SeekAlignedToLastTick(TimeSpan.FromSeconds(5), TimeSeekOrigin.BeginTime);
            await Dispatcher.Yield(DispatcherPriority.DataBind);
            Assert(first.Color.R == 64 && first.Color.G == 185 && first.Color.B == 100 && brushes.SequenceEqual(clouds.Select(cloud => cloud.Fill))
                && stops.SequenceEqual(brushes.SelectMany(brush => brush.GradientStops)), "retarget left a fixed orange layer or rebuilt the cloud brushes");
            var bitmap = Ambient015Bitmap(ambient); var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4]; bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
            var samples = new List<Color>();
            for (var y = 32; y < bitmap.PixelHeight - 32; y += 40) for (var x = 32; x < bitmap.PixelWidth - 32; x += 40) samples.Add(Ambient015Pixel(bitmap, pixels, x, y));
            Assert(samples.Max(color => color.G) - samples.Min(color => color.G) >= 20 && samples.Any(color => color.G > color.R * 1.4 && color.G > color.B * 1.15), "rendered atmosphere is empty, flat or unrelated to its green accent");
            var corner = Ambient015Pixel(bitmap, pixels, 0, 0); Assert(corner.A == 0 && Ambient015Pixel(bitmap, pixels, 25, 25).A > 240, "rounded backdrop leaks outside its wave card");
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var file = File.Create(System.IO.Path.Combine(_directory, "ambient015-soft-gradient.png")); encoder.Save(file);
        }
        finally { host.Close(); }
    }

    private static async Task Ambient015Lifecycle()
    {
        var ambient = new AmbientBackdrop { AccentBrush = new SolidColorBrush(Color.FromRgb(45, 144, 185)) };
        var content = new Grid(); content.Children.Add(ambient); var host = Ambient015Window(content);
        try
        {
            host.Show(); host.UpdateLayout(); await Dispatcher.Yield(DispatcherPriority.Loaded);
            var motion = Ambient015Field<Storyboard>(ambient, "_motion");
            Assert(ReferenceEquals(Ambient015Field<Window?>(ambient, "_owner"), host), "ambient did not attach the current owner");
            if (Motion.CanAnimate)
            {
                await Dispatcher.Yield(DispatcherPriority.Render); var time = motion.GetCurrentTime(ambient) ?? TimeSpan.Zero;
                var cloud = EpVisuals<Ellipse>(ambient).First(); var translation = ((TransformGroup)cloud.RenderTransform).Children.OfType<TranslateTransform>().Single(); var startX = translation.X;
                motion.SeekAlignedToLastTick(ambient, time + TimeSpan.FromSeconds(3), TimeSeekOrigin.BeginTime);
                Assert(motion.GetCurrentTime(ambient) >= time + TimeSpan.FromSeconds(3) && Math.Abs(translation.X - startX) > 2 && translation.HasAnimatedProperties,
                    "native animation clock did not advance an actual cloud transform: before=" + time + ", after=" + motion.GetCurrentTime(ambient) + ", x=" + startX + " -> " + translation.X);
                ambient.Visibility = Visibility.Collapsed; await Task.Delay(35); var paused = motion.GetCurrentTime(ambient);
                await Task.Delay(100); Assert(motion.GetIsPaused(ambient) && motion.GetCurrentTime(ambient) == paused && !Ambient015Field<bool>(ambient, "_running"), "hidden backdrop still animates");
                ambient.Visibility = Visibility.Visible; await Dispatcher.Yield(DispatcherPriority.Render);
                Assert(!motion.GetIsPaused(ambient) && Ambient015Field<bool>(ambient, "_running") && motion.GetCurrentTime(ambient) >= paused, "visible backdrop did not resume its existing clock");
                host.WindowState = WindowState.Minimized; await Task.Delay(40); paused = motion.GetCurrentTime(ambient);
                await Task.Delay(100); Assert(!Ambient015Field<bool>(ambient, "_running") && motion.GetCurrentTime(ambient) == paused, "minimized owner still animates");
                host.WindowState = WindowState.Normal; await Task.Delay(100); Assert(Ambient015Field<bool>(ambient, "_running"), "restored owner did not resume");
            }
            else Assert(!Ambient015Field<bool>(ambient, "_begun"), "software or reduced-motion fallback unexpectedly created a clock");
            content.Children.Remove(ambient); await Dispatcher.Yield(DispatcherPriority.Loaded);
            Assert(!Ambient015Field<bool>(ambient, "_running") && !Ambient015Field<bool>(ambient, "_begun") && Ambient015Field<Window?>(ambient, "_owner") is null, "unloaded backdrop retains owner listeners or active clocks");
            content.Children.Add(ambient); host.UpdateLayout(); await Dispatcher.Yield(DispatcherPriority.Loaded);
            Assert(ReferenceEquals(Ambient015Field<Storyboard>(ambient, "_motion"), motion) && ReferenceEquals(Ambient015Field<Window?>(ambient, "_owner"), host)
                && Ambient015Field<bool>(ambient, "_running") == Motion.CanAnimate, "reloaded backdrop created another storyboard or failed to resume");
        }
        finally { host.Close(); }
        Assert(Ambient015Field<bool>(ambient, "_closed") && !Ambient015Field<bool>(ambient, "_begun") && Ambient015Field<Window?>(ambient, "_owner") is null, "closed owner retained native clocks or listeners");
    }

    private static async Task Ambient015Integrated()
    {
        using var fixture = new ExpandedFixture("ambient015"); await fixture.Play(); var window = fixture.Window;
        var queueVersion = fixture.Queue.Version; var token = fixture.Queue.CurrentOccurrenceToken; var player = fixture.Player;
        var waveSources = EpField<Dictionary<TrackRow, (string SessionId, string BatchId, WaveMode Mode, string? TrackSeedId)>>(window, "_waveSources");
        waveSources[fixture.First] = ("ambient-session", "ambient-batch", WaveMode.Personal, null);
        EpField<List<TrackRow>>(window, "_waveTracks").Add(fixture.First);
        EpSet(window, "_waveBatch", new RadioBatch("ambient-session", "ambient-batch", [fixture.First]));
        EpInvoke(window, "SelectWaveView", WaveMode.Personal, null); EpInvoke(window, "UpdateWavePresentation");
        await Until(() => Control<Image>(window, "WaveCover").Source is BitmapSource && Control<Image>(window, "PlayerCover").Source is BitmapSource);
        var accent = EpField<CoverAccentController>(window, "_coverAccent"); await accent.PendingAnalysis;
        foreach (var clock in accent.Clocks.Values) clock.Controller!.SeekAlignedToLastTick(TimeSpan.FromSeconds(5), TimeSeekOrigin.BeginTime);
        await Dispatcher.Yield(DispatcherPriority.DataBind);
        var wave = EpField<AmbientBackdrop>(window, "_waveAmbient"); var waveSurface = Control<Border>(window, "WaveSurface");
        var content = (Grid)waveSurface.Child;
        Assert(content.Children[0] == wave && !content.Children.OfType<Shape>().Any() && wave.CornerRadius == 23 && !wave.IsHitTestVisible,
            "wave ornament replacement changed the content root or retained sharp strokes");
        foreach (var (width, height) in new[] { (1050, 680), (1280, 860) })
        {
            window.Width = width; window.Height = height; window.UpdateLayout(); await Task.Delay(160);
            Assert(wave.ActualWidth > 600 && wave.ActualHeight > 300 && ReferenceEquals(wave.AccentBrush, window.FindResource("AccentBrush")), "wave gradient is missing or lost the shared accent");
            Ambient015Hit(window, Control<Button>(window, "WavePlayButton")); Ambient015Hit(window, Control<Button>(window, "WaveNextButton"));
            Ambient015Contrast(Control<TextBlock>(window, "WaveTrackTitle"), wave); Ambient015Contrast(Control<TextBlock>(window, "WaveTrackArtist"), wave);
            App.Capture(window, System.IO.Path.Combine(_directory, $"ambient015-wave-{width}x{height}.png"));
            EpOpen(window); window.UpdateLayout(); await Task.Delay(130);
            var expandedHost = Control<ContentControl>(window, "ExpandedPlayerHost"); var expanded = EpField<AmbientBackdrop>(window, "_expandedAmbient");
            Assert(((Grid)expandedHost.Content).Children[0] == expanded && Grid.GetRowSpan(expanded) == 3 && expanded.ActualWidth == expandedHost.ActualWidth
                && expanded.ActualHeight == expandedHost.ActualHeight && !wave.IsVisible && !Ambient015Field<bool>(wave, "_running"), "expanded backdrop does not fill the surface or hidden wave keeps animating");
            foreach (var name in new[] { "ExpandedPlayerClose", "ExpandedPlayerPlayPause", "ExpandedPlayerPrevious", "ExpandedPlayerNext", "ExpandedPlayerLike", "ExpandedPlayerLyrics", "ExpandedPlayerSeek", "ExpandedPlayerVolume" })
            {
                var element = EpVisuals<FrameworkElement>(expandedHost).Single(control => control.Name == name);
                var bounds = element.TransformToAncestor(expandedHost).TransformBounds(new Rect(element.RenderSize));
                Assert(element.IsVisible && bounds.Left >= 0 && bounds.Top >= 0 && bounds.Right <= expandedHost.ActualWidth + 1 && bounds.Bottom <= expandedHost.ActualHeight + 1, name + " is clipped by the backdrop integration");
                if (element is Button button && button.IsEnabled) Ambient015Hit(window, button);
            }
            var cover = EpElement<Button>(window, "ExpandedPlayerCover");
            var center = cover.TranslatePoint(new Point(cover.ActualWidth / 2, 0), expandedHost).X;
            Assert(Math.Abs(center - expandedHost.ActualWidth / 2) < 2, "centered expanded player layout shifted");
            Ambient015Contrast(EpField<TextBlock>(window, "_expandedPlayerTitle"), expanded);
            App.Capture(window, System.IO.Path.Combine(_directory, $"ambient015-expanded-{width}x{height}.png"));
            EpInvoke(window, "SetExpandedLyricsVisible", true, false); window.UpdateLayout();
            await Until(() => EpField<ListBox>(window, "_expandedLyricsList").Items.Count == 50);
            window.UpdateLayout(); var lyrics = EpField<ListBox>(window, "_expandedLyricsList");
            Assert(lyrics.IsVisible && lyrics.ActualWidth > 350 && cover.TranslatePoint(new Point(cover.ActualWidth / 2, 0), expandedHost).X < center - 100
                && ReferenceEquals(expanded, EpField<AmbientBackdrop>(window, "_expandedAmbient")), "opening lyrics replaced the background or changed inline layout");
            var lyricText = EpVisuals<TextBlock>(lyrics).First(text => text.IsVisible && text.Text.StartsWith("Строка ", StringComparison.Ordinal));
            Ambient015Contrast(lyricText, expanded);
            App.Capture(window, System.IO.Path.Combine(_directory, $"ambient015-lyrics-{width}x{height}.png"));
            EpInvoke(window, "SetExpandedPlayerVisible", false, false); window.UpdateLayout();
            Assert(!Ambient015Field<bool>(expanded, "_running") && wave.IsVisible && Ambient015Field<bool>(wave, "_running") == Motion.CanAnimate, "closing expanded view did not resume the wave background alone");
        }
        Assert(fixture.Queue.Version == queueVersion && fixture.Queue.CurrentOccurrenceToken == token && ReferenceEquals(player, EpField<NativePlayer>(window, "_player")) && fixture.MediaCalls == 0,
            "background rendering mutated playback, queue identity or contacted media network");
        var expandedBackdrop = EpField<AmbientBackdrop>(window, "_expandedAmbient"); window.Close();
        Assert(EpField<object?>(window, "_ambientExpandedContent") is null && Ambient015Field<bool>(wave, "_closed") && Ambient015Field<bool>(expandedBackdrop, "_closed"), "main close retained content observer or backdrop clocks");
    }

    private static T Ambient015Field<T>(AmbientBackdrop backdrop, string name) => (T)typeof(AmbientBackdrop).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(backdrop)!;
    private static Window Ambient015Window(FrameworkElement content) => new() { Content = content, Width = 680, Height = 430, Left = -16000, Top = -16000,
        ShowActivated = false, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize };
    private static void Ambient015Hit(MainWindow window, Button button)
    {
        var hit = window.InputHitTest(button.TranslatePoint(new Point(button.ActualWidth / 2, button.ActualHeight / 2), window)) as DependencyObject;
        Assert(Ui015HasAncestor(hit, button), button.Name + " lost its original hit owner beneath the decorative backdrop");
    }
    private static RenderTargetBitmap Ambient015Bitmap(FrameworkElement element)
    {
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth), (int)Math.Ceiling(element.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen()) drawing.DrawRectangle(new VisualBrush(element) { Stretch = Stretch.None,
            AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top }, null, new Rect(0, 0, element.ActualWidth, element.ActualHeight));
        bitmap.Render(visual); return bitmap;
    }
    private static Color Ambient015Pixel(BitmapSource bitmap, byte[] pixels, int x, int y)
    {
        var offset = (Math.Clamp(y, 0, bitmap.PixelHeight - 1) * bitmap.PixelWidth + Math.Clamp(x, 0, bitmap.PixelWidth - 1)) * 4;
        return Color.FromArgb(pixels[offset + 3], pixels[offset + 2], pixels[offset + 1], pixels[offset]);
    }
    private static void Ambient015Contrast(TextBlock text, AmbientBackdrop backdrop)
    {
        var center = text.TranslatePoint(new Point(text.ActualWidth / 2, text.ActualHeight / 2), backdrop);
        Assert(center.X >= 0 && center.Y >= 0 && center.X < backdrop.ActualWidth && center.Y < backdrop.ActualHeight, "text escaped its ambient surface");
        var bitmap = Ambient015Bitmap(backdrop); var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4]; bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
        var background = Ambient015Pixel(bitmap, pixels, (int)center.X, (int)center.Y);
        static double Luminance(Color color)
        {
            static double Channel(byte value) { var x = value / 255d; return x <= .04045 ? x / 12.92 : Math.Pow((x + .055) / 1.055, 2.4); }
            return .2126 * Channel(color.R) + .7152 * Channel(color.G) + .0722 * Channel(color.B);
        }
        var foreground = ((SolidColorBrush)text.Foreground).Color;
        var ratio = (Luminance(foreground) + .05) / (Luminance(background) + .05);
        Assert(ratio >= 4.5, "ambient makes text unreadable: " + text.Text + ", contrast=" + ratio.ToString("F2", System.Globalization.CultureInfo.InvariantCulture));
    }
    private static byte[] Ambient015Artwork()
    {
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            drawing.DrawRectangle(new SolidColorBrush(Color.FromRgb(36, 155, 173)), null, new Rect(0, 0, 1024, 1024));
            drawing.DrawEllipse(new SolidColorBrush(Color.FromRgb(27, 84, 102)), null, new Point(510, 510), 280, 280);
            drawing.DrawEllipse(new SolidColorBrush(Color.FromRgb(195, 231, 226)), null, new Point(510, 510), 95, 95);
        }
        var bitmap = new RenderTargetBitmap(1024, 1024, 96, 96, PixelFormats.Pbgra32); bitmap.Render(visual);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var stream = new MemoryStream(); encoder.Save(stream); return stream.ToArray();
    }
}
