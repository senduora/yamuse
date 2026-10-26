using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
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
    private static async Task CheckCoverAccent016()
    {
        await Run("CoverAccent016: delayed forward and cached back keep the last palette and reject late artwork", Accent016Back);
        await Run("CoverAccent016: confirmed missing artwork fades to static over five seconds and recovers", Accent016Missing);
        await Run("CoverAccent016: actual main mini and expanded play surfaces follow live colour without hovering", Accent016Surfaces);
        await Run("CoverAccent016: distinct soft clouds retain native motion and show colour changes", Accent016Clouds);
    }

    private sealed class Accent016Fixture : IDisposable
    {
        private readonly object? _previous = typeof(Artwork).GetField("_cache", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null);
        private readonly ArtworkCache _cache;
        internal readonly SaFixture Fixture;
        internal MainWindow Window => Fixture.Window;
        internal Image Image => Control<Image>(Window, "PlayerCover");
        internal CoverAccentController Controller => SaField<CoverAccentController>(Window, "_coverAccent");
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int HoldBlue, RedRequests;
        internal Accent016Fixture(string name)
        {
            _cache = new ArtworkCache(System.IO.Path.Combine(_directory, name, "covers"), new FakeHandler(async (request, _) =>
            {
                Assert(ArtworkUri.Valid(request.RequestUri!) && request.Headers.Authorization is null, "palette fixture sent credentials or left the artwork host");
                var path = request.RequestUri!.AbsolutePath;
                if (path.Contains("blue") && Volatile.Read(ref HoldBlue) == 1) { Entered.TrySetResult(); await Release.Task; }
                if (path.Contains("missing")) return new HttpResponseMessage(HttpStatusCode.NotFound);
                if (path.Contains("red")) Interlocked.Increment(ref RedRequests);
                var color = path.Contains("blue") ? Colors.Blue : Colors.Red;
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(Accent015Bitmap(32, 32, (_, _) => color)));
                using var stream = new MemoryStream(); encoder.Save(stream);
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(stream.ToArray()) };
            }));
            Artwork.Cache = _cache; Fixture = new SaFixture(name);
        }
        internal void Select(string name)
        {
            typeof(MainWindow).GetField("_current", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(Window,
                new TrackRow { Id = "accent016-" + name, RequestId = "accent016-" + name, Title = name, Available = true });
            Artwork.SetSourceUri(Image, "https://avatars.yandex.net/get-music-content/1/accent016-" + name + "/%%");
        }
        internal async Task Ready()
        {
            await Until(() => Artwork.GetLoadStatus(Image) == ArtworkLoadStatus.Ready);
            await Controller.PendingAnalysis;
        }
        public void Dispose()
        {
            Release.TrySetResult(); Fixture.Dispose(); _cache.Dispose();
            typeof(Artwork).GetField("_cache", BindingFlags.Static | BindingFlags.NonPublic)!.SetValue(null, _previous);
        }
    }

    private static async Task Accent016Back()
    {
        using var f = new Accent016Fixture("accent016-back"); f.Select("red"); await f.Ready();
        Accent015Seek(f.Controller, 5.01); var red = f.Controller.TargetColor; var visible = f.Controller.Brushes["AccentBrush"].Color;
        Volatile.Write(ref f.HoldBlue, 1); f.Select("blue"); await f.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert(Artwork.GetLoadStatus(f.Image) == ArtworkLoadStatus.Loading && f.Image.Source is null
            && f.Controller.TargetColor == red && f.Controller.Brushes["AccentBrush"].Color == visible, "loading the next image introduced a static-orange detour");
        f.Select("red"); await f.Ready();
        Assert(f.RedRequests == 1 && f.Controller.TargetColor == red, "cached previous artwork re-fetched or reset the accent");
        f.Release.TrySetResult(); await Task.Delay(150);
        Assert(f.Controller.TargetColor == red && Artwork.GetLoadStatus(f.Image) == ArtworkLoadStatus.Ready,
            "late next artwork changed the previous track's palette or loading state");
        Volatile.Write(ref f.HoldBlue, 0); f.Select("blue"); await f.Ready();
        Assert(f.Controller.TargetColor == CoverPalette.Readable(Colors.Blue), "forward selection after Back did not recover");
        Accent015Seek(f.Controller, 0); Assert(f.Controller.Brushes["AccentBrush"].Color == visible, "new cover did not start from the displayed old colour");
        Accent015Seek(f.Controller, 2.5); var middle = f.Controller.Brushes["AccentBrush"].Color;
        Assert(middle != visible && middle != f.Controller.TargetColor, "new cover has no intermediate colour");
        Accent015Seek(f.Controller, 5.01);
        File.WriteAllText(System.IO.Path.Combine(_directory, "accent016-back.json"), JsonSerializer.Serialize(new
        { cachedRedRequests = f.RedRequests, loadingPreservesPalette = true, staleResultRejected = true, transitionSeconds = 5,
            previous = visible.ToString(), midpoint = middle.ToString(), next = f.Controller.Brushes["AccentBrush"].Color.ToString(), realAccountAccessed = false }));
    }

    private static async Task Accent016Missing()
    {
        using var f = new Accent016Fixture("accent016-missing"); f.Select("blue"); await f.Ready(); Accent015Seek(f.Controller, 5.01);
        var before = f.Controller.Brushes["AccentBrush"].Color;
        f.Select("missing"); await Until(() => Artwork.GetLoadStatus(f.Image) == ArtworkLoadStatus.Unavailable);
        Assert(f.Controller.TargetColor == CoverPalette.Fallback, "a confirmed unavailable cover never used the configured fallback");
        if (SystemParameters.ClientAreaAnimation)
        {
            Assert(f.Controller.Clocks.Count == 7 && f.Controller.Clocks.Values.All(c => c.Timeline.Duration.TimeSpan == TimeSpan.FromSeconds(5)), "missing cover bypassed the five-second fade");
            Accent015Seek(f.Controller, 0); Assert(f.Controller.Brushes["AccentBrush"].Color == before, "missing cover jumped to orange");
            Accent015Seek(f.Controller, 2.5); var midway = f.Controller.Brushes["AccentBrush"].Color;
            Assert(midway != before && midway != CoverPalette.Fallback, "fallback lacks intermediate colours");
        }
        f.Select("red"); await f.Ready(); Accent015Seek(f.Controller, 5.01);
        Assert(f.Controller.TargetColor == CoverPalette.Readable(Colors.Red), "artwork failure prevented the next cover recovering");
        f.Window.Hide();
        Assert(Artwork.GetLoadStatus(f.Image) == ArtworkLoadStatus.Loading && f.Controller.TargetColor == CoverPalette.Readable(Colors.Red), "hiding the player reset its palette");
        f.Window.Show(); await f.Ready();
        Assert(f.Controller.TargetColor == CoverPalette.Readable(Colors.Red), "showing the same cached artwork reset its palette");
    }

    private static Brush Accent016Surface(Button button)
        => button.Template.FindName("Disc", button) is Shape disc ? disc.Fill
            : button.Template.FindName("ButtonChrome", button) is Border chrome ? chrome.Background
            : SaVisuals<Border>(button).First(border => border.Background is not null).Background;

    private static async Task Accent016Surfaces()
    {
        using var f = new SaFixture("accent016-surfaces"); var w = f.Window;
        var current = new TrackRow { Id = "accent016-surface", RequestId = "accent016-surface", Title = "Живой акцент", Artist = "Тест", Available = true, DurationMs = 180000 };
        typeof(MainWindow).GetField("_current", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(w, current);
        SaInvoke(w, "UpdateControls");
        var controller = SaField<CoverAccentController>(w, "_coverAccent");
        var main = Control<Button>(w, "PlayPauseButton");
        var mini = new MiniPlayerWindow { Left = -16000, Top = 0, ShowActivated = false, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual };
        try
        {
            mini.Update(current.Title, current.Artist, false, true, true); mini.Show(); mini.UpdateLayout();
            SaInvoke(w, "SetExpandedPlayerVisible", true, false); w.UpdateLayout();
            var expanded = SaVisuals<Button>(Control<ContentControl>(w, "ExpandedPlayerHost")).Single(b => b.Name == "ExpandedPlayerPlayPause");
            // Exercise the same brush reference changes that hover/leave causes,
            // then leave all three controls untouched through the colour fade.
            main.SetResourceReference(System.Windows.Controls.Control.BackgroundProperty, "AccentHoverBrush"); w.UpdateLayout();
            main.SetResourceReference(System.Windows.Controls.Control.BackgroundProperty, "AccentBrush"); w.UpdateLayout();
            var accent = controller.Brushes["AccentBrush"];
            Assert(new[] { main, mini.PlayButton, expanded }.All(b => !b.IsMouseOver), "offscreen surfaces unexpectedly hovered");
            foreach (var target in new[] { Colors.Blue, Colors.Red, Colors.Cyan })
            {
                controller.Retarget(target);
                foreach (var time in new[] { 0d, .3, 2.5, 5.01 })
                {
                    Accent015Seek(controller, time); await Dispatcher.Yield(DispatcherPriority.Render); w.UpdateLayout(); mini.UpdateLayout();
                    foreach (var button in new[] { main, mini.PlayButton, expanded })
                    {
                        var surface = Accent016Surface(button);
                        Assert(ReferenceEquals(surface, accent) && ((SolidColorBrush)surface).Color == accent.Color,
                            "actual play surface detached until hover: " + button.Name + " at " + time);
                    }
                }
            }
            var thumb = SaVisuals<Ellipse>(Control<Slider>(w, "VolumeSlider")).Single(e => e.Name == "Handle");
            Assert(ReferenceEquals(thumb.Fill, accent), "slider handle retained a private stale accent");
            App.Capture(w, System.IO.Path.Combine(_directory, "accent016-expanded-live.png"));
            App.Capture(mini, System.IO.Path.Combine(_directory, "accent016-mini-live.png"));
        }
        finally { mini.Close(); }
    }

    private static async Task Accent016Clouds()
    {
        var accent = new SolidColorBrush(Color.FromRgb(225, 61, 54));
        var ambient = new AmbientBackdrop { AccentBrush = accent, Width = 1000, Height = 700 };
        var host = Ambient015Window(ambient);
        try
        {
            host.Show(); host.UpdateLayout(); await Dispatcher.Yield(DispatcherPriority.Render);
            var clouds = EpVisuals<Ellipse>(ambient).ToArray();
            Assert(clouds.Length == 4 && clouds.All(c => c.Width <= 900 && c.Height <= 800 && c.Stroke is null && c.Effect is null),
                "background returned to oversized flat haze or acquired sharp contours");
            var motion = Ambient015Field<Storyboard>(ambient, "_motion");
            if (Motion.CanAnimate) { motion.Pause(ambient); motion.SeekAlignedToLastTick(ambient, TimeSpan.Zero, TimeSeekOrigin.BeginTime); }
            App.Capture(host, System.IO.Path.Combine(_directory, "accent016-background-red.png"));
            var old = clouds[0].RenderTransform.Value;
            if (Motion.CanAnimate)
            {
                motion.SeekAlignedToLastTick(ambient, TimeSpan.FromSeconds(5), TimeSeekOrigin.BeginTime);
                Assert(clouds[0].RenderTransform.Value != old, "defined clouds no longer move");
            }
            accent.Color = Color.FromRgb(35, 171, 194); await Dispatcher.Yield(DispatcherPriority.Render);
            App.Capture(host, System.IO.Path.Combine(_directory, "accent016-background-teal.png"));
        }
        finally { host.Close(); }
    }
}
