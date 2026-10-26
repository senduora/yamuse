using System.ComponentModel;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using YMusic.Native;

internal static partial class Program
{
    private static async Task CheckIdlePerformance017()
    {
        await Run("Idle017: shown wave without a source has no decorative clocks or playback timers", Idle017NoSource);
        await Run("Idle017: paused expanded view freezes atmosphere and resumes its phase without touching playback", Idle017PauseResume);
        await Run("Idle017: paused atmosphere still follows finite colour feedback without starting perpetual motion", Idle017ColourFeedback);
    }

    private static async Task Idle017NoSource()
    {
        using var fixture = new Idle017Fixture("no-source"); var window = fixture.Window;
        EpInvoke(window, "SelectWaveView", WaveMode.Personal, null); window.UpdateLayout();
        await Dispatcher.Yield(DispatcherPriority.Loaded);
        var wave = EpField<AmbientBackdrop>(window, "_waveAmbient");
        Assert(window.IsVisible && wave.IsVisible && !fixture.Player.HasSource && !wave.IsMotionEnabled
            && !Ambient015Field<bool>(wave, "_begun") && !Ambient015Field<bool>(wave, "_running"), "shown idle wave started perpetual clocks without a source");
        var movement = await Idle017Movement(wave, 220);
        Assert(movement == 0 && !EpField<DispatcherTimer>(window, "_progressTimer").IsEnabled
            && !EpField<DispatcherTimer>(window, "_lyricsTimer").IsEnabled, "idle wave changes its transforms or polls playback");
        window.Close();
        Assert(Ambient015Field<bool>(wave, "_closed") && Ambient015Field<Window?>(wave, "_owner") is null, "idle close retained atmosphere owner");
        Assert(fixture.Handler.Seen.Count == 0, "no-source fixture contacted external transport");
    }

    private static async Task Idle017PauseResume()
    {
        using var fixture = new Idle017Fixture("paused"); var window = fixture.Window; var player = fixture.Player;
        await window.PlayTrackAsync(fixture.Track); EpOpen(window); window.UpdateLayout(); await Task.Delay(100);
        var expanded = EpField<AmbientBackdrop>(window, "_expandedAmbient"); var wave = EpField<AmbientBackdrop>(window, "_waveAmbient");
        var queue = EpField<PlaybackQueue>(window, "_playbackQueue"); var queueVersion = queue.Version; var occurrence = queue.CurrentOccurrenceToken;
        Assert(expanded.IsMotionEnabled && Ambient015Field<bool>(expanded, "_running") == Motion.CanAnimate
            && !wave.IsVisible && !Ambient015Field<bool>(wave, "_running"), "playback did not animate the visible atmosphere alone");
        var motion = Ambient015Field<Storyboard>(expanded, "_motion");
        if (Motion.CanAnimate)
        {
            var from = Idle017Translation(expanded).X;
            motion.SeekAlignedToLastTick(expanded, TimeSpan.FromSeconds(3), TimeSeekOrigin.BeginTime);
            Assert(Math.Abs(Idle017Translation(expanded).X - from) > 1, "native playback atmosphere no longer advances smoothly");
        }
        await Idle017Toggle(window); await Task.Delay(100);
        var position = player.Position; var pausedPhase = Motion.CanAnimate ? motion.GetCurrentTime(expanded) : null;
        Assert(!player.IsPlaying && player.HasSource && !expanded.IsMotionEnabled && !Ambient015Field<bool>(expanded, "_running"), "pausing visible expanded player kept decorative motion running");
        var changes = await Idle017Movement(expanded, 260);
        Assert(changes == 0 && (!Motion.CanAnimate || motion.GetIsPaused(expanded) && motion.GetCurrentTime(expanded) == pausedPhase)
            && Math.Abs((player.Position - position).TotalSeconds) < .1, "paused player or decorative transforms kept advancing");
        Assert(!EpField<DispatcherTimer>(window, "_progressTimer").IsEnabled && !EpField<DispatcherTimer>(window, "_lyricsTimer").IsEnabled,
            "pausing left a playback display timer active");
        await Idle017Toggle(window); await Task.Delay(70);
        Assert(player.IsPlaying && expanded.IsMotionEnabled && Ambient015Field<bool>(expanded, "_running") == Motion.CanAnimate
            && (!Motion.CanAnimate || !motion.GetIsPaused(expanded) && motion.GetCurrentTime(expanded) >= pausedPhase), "resume failed to continue the same atmosphere phase");
        window.WindowState = WindowState.Minimized; await Task.Delay(80);
        Assert(!Ambient015Field<bool>(expanded, "_running") && await Idle017Movement(expanded, 100) == 0 && player.IsPlaying, "minimize kept visual clocks running or stopped audio");
        window.WindowState = WindowState.Normal; window.Hide(); await Task.Delay(80);
        Assert(!Ambient015Field<bool>(expanded, "_running") && await Idle017Movement(expanded, 100) == 0 && player.IsPlaying, "hidden view kept visual clocks running or stopped audio");
        window.Show(); await Task.Delay(80);
        Assert(Ambient015Field<bool>(expanded, "_running") == Motion.CanAnimate, "shown playback failed to resume atmosphere");
        await Idle017Toggle(window); player.Stop(); EpInvoke(window, "UpdateControls");
        Assert(!expanded.IsMotionEnabled && !Ambient015Field<bool>(expanded, "_running") && !player.HasSource,
            "stopping the native source retained expanded motion");
        Assert(queue.Version == queueVersion && queue.CurrentOccurrenceToken == occurrence && fixture.Handler.Seen.Count == 0,
            "atmosphere lifecycle changed queue identity or contacted a network");
        window.Close();
        Assert(Ambient015Field<bool>(expanded, "_closed") && !Ambient015Field<bool>(expanded, "_begun")
            && Ambient015Field<Window?>(expanded, "_owner") is null && !player.HasSource, "close retained active native clocks, owner or source");
        File.WriteAllText(System.IO.Path.Combine(_directory, "idle017-lifecycle.json"), JsonSerializer.Serialize(new
        {
            shownOffscreen = true, windowSize = "1280x860", realAccountLoaded = false, externalNetworkRequests = fixture.Handler.Seen.Count,
            mutedGeneratedLocalWav = true, idleTransformChanges = changes, resumePreservesPhase = true,
            canAnimate = Motion.CanAnimate, reducedMotionFallback = !Motion.CanAnimate, cpuMeasurementInSeparateProbe = true
        }, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static async Task Idle017ColourFeedback()
    {
        var accent = new SolidColorBrush(Color.FromRgb(36, 170, 183));
        var ambient = new AmbientBackdrop { AccentBrush = accent, IsMotionEnabled = false };
        var window = Ambient015Window(ambient);
        try
        {
            window.Show(); window.UpdateLayout(); await Dispatcher.Yield(DispatcherPriority.Loaded);
            var translation = Idle017Translation(ambient); var initial = translation.X;
            var animation = new ColorAnimation(accent.Color, Color.FromRgb(105, 73, 214), TimeSpan.FromSeconds(5)).CreateClock();
            accent.ApplyAnimationClock(SolidColorBrush.ColorProperty, animation);
            animation.Controller!.SeekAlignedToLastTick(TimeSpan.FromSeconds(2.5), TimeSeekOrigin.BeginTime);
            await Dispatcher.Yield(DispatcherPriority.DataBind);
            var stop = ((RadialGradientBrush)EpVisuals<Ellipse>(ambient).First().Fill).GradientStops[0];
            Assert(stop.Color.R == accent.Color.R && stop.Color.G == accent.Color.G && stop.Color.B == accent.Color.B
                && accent.Color.G < 150 && accent.Color.B > 190 && translation.X == initial
                && !Ambient015Field<bool>(ambient, "_begun"), "paused atmosphere lost finite cover feedback or restarted perpetual clocks");
            animation.Controller.SeekAlignedToLastTick(TimeSpan.FromSeconds(5), TimeSeekOrigin.BeginTime);
            await Dispatcher.Yield(DispatcherPriority.DataBind);
            Assert(stop.Color.R == 105 && stop.Color.G == 73 && stop.Color.B == 214 && !Ambient015Field<bool>(ambient, "_running"), "finite feedback failed to settle while paused");
            accent.ApplyAnimationClock(SolidColorBrush.ColorProperty, null);
        }
        finally { window.Close(); }
    }

    private static TranslateTransform Idle017Translation(AmbientBackdrop backdrop) => ((TransformGroup)EpVisuals<Ellipse>(backdrop).First().RenderTransform).Children.OfType<TranslateTransform>().Single();
    private static async Task<int> Idle017Movement(AmbientBackdrop backdrop, int milliseconds)
    {
        var transform = Idle017Translation(backdrop); var changes = 0;
        var descriptor = DependencyPropertyDescriptor.FromProperty(TranslateTransform.XProperty, typeof(TranslateTransform));
        EventHandler changed = (_, _) => changes++;
        descriptor.AddValueChanged(transform, changed);
        try { await Task.Delay(milliseconds); return changes; }
        finally { descriptor.RemoveValueChanged(transform, changed); }
    }
    private static Task Idle017Toggle(MainWindow window) => (Task)typeof(MainWindow).GetMethod("TogglePlaybackAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null)!;

    private sealed class Idle017Fixture : IDisposable
    {
        internal FakeHandler Handler { get; } = new((_, _) => throw new InvalidOperationException("Idle fixture unexpectedly contacted a network"));
        internal NativePlayer Player { get; }
        internal MainWindow Window { get; }
        internal TrackRow Track { get; }
        internal Idle017Fixture(string suffix)
        {
            var directory = System.IO.Path.Combine(_directory, "idle017-" + suffix); Directory.CreateDirectory(directory);
            Player = new NativePlayer(new AudioBuffer(System.IO.Path.Combine(directory, "audio"), Handler)); Player.Volume = 0;
            Track = new TrackRow { Id = "idle017", RequestId = "idle017", Title = "Isolated idle fixture", Artist = "Fixture", Available = true,
                DurationMs = 30000, LocalPath = WriteWave("idle017-" + suffix + ".wav", 30) };
            Window = new MainWindow(api: new MusicApi(Handler), store: new SessionStore(System.IO.Path.Combine(directory, "session")), restore: false,
                diagnosticsDirectory: directory, offline: new OfflineLibrary(System.IO.Path.Combine(directory, "offline")), player: Player,
                preferences: new PreferencesStore(System.IO.Path.Combine(directory, "preferences")))
                { Width = 1280, Height = 860, Left = -16000, Top = 0, ShowActivated = false, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual };
            Window.Show(); Control<Slider>(Window, "VolumeSlider").Value = 0; Player.Volume = 0;
        }
        public void Dispose() { Window.Close(); Player.Dispose(); Handler.Dispose(); }
    }
}
