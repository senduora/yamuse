using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using YMusic.Native;

internal static partial class Program
{
    private static async Task CheckPlaybackControls016()
    {
        await Run("Playback016: native WAV playheads advance between 500-ms samples in all views and freeze on pause", Pc016Smooth);
        await Run("Playback016: held pointer and keyboard previews survive progress and commit or cancel safely", Pc016Gestures);
        await Run("Playback016: rail point replaces animation before commit and old gestures cannot seek a new track", Pc016RailAndReplacement);
        await Run("Playback016: all volume gestures update native gain immediately and persist only after release", Pc016Volume);
    }

    private static async Task Pc016Smooth()
    {
        using var f = new Pc016Fixture("smooth"); await f.Play(); f.Views();
        f.Player.SetSpeed(1.5); SaInvoke(f.Window, "UpdateProgress");
        var sliders = f.Seeks(); var samples = new List<object>(); var series = sliders.Select(_ => new List<double>()).ToArray();
        var nativeStart = f.Player.Position.TotalSeconds;
        for (var i = 0; i < 12; i++)
        {
            await Task.Delay(35);
            for (var j = 0; j < sliders.Length; j++) series[j].Add(sliders[j].Value);
            samples.Add(new { native = f.Player.Position.TotalSeconds, main = sliders[0].Value, expanded = sliders[1].Value, mini = sliders[2].Value });
        }
        for (var j = 0; j < sliders.Length; j++)
        {
            Assert(series[j].Select(v => Math.Round(v, 3)).Distinct().Count() >= 7, "playhead still advances only on polling: view " + j);
            Assert(series[j][^1] - series[j][0] > .2 && Math.Abs(sliders[j].Value - f.Player.Position.TotalSeconds) < .8,
                "display interpolation diverged from native playback: view " + j);
        }
        Assert(f.Player.Position.TotalSeconds - nativeStart > .25 && MediaField<DispatcherTimer>(f.Window, "_progressTimer").Interval == TimeSpan.FromMilliseconds(500),
            "native decoder did not advance or global progress timer was accelerated");
        await Call(f.Window, "TogglePlaybackAsync"); var frozen = sliders.Select(s => s.Value).ToArray();
        await Task.Delay(300);
        Assert(!f.Player.IsPlaying && sliders.Select((s, i) => Math.Abs(s.Value - frozen[i]) < .015 && !Pc016Animated(s)).All(v => v),
            "pause left an advancing playhead or live value animation");
        f.Player.Toggle(); SaInvoke(f.Window, "UpdateControls"); await Task.Delay(90);
        f.Player.Toggle(); SaInvoke(f.Window, "UpdateControls");
        Assert(sliders.All(s => !Pc016Animated(s)), "UpdateControls left live clocks after a native pause");
        SaInvoke(f.Window, "CommitSeek", 12.25d);
        Assert(sliders.All(s => Math.Abs(s.Value - 12.25) < .02), "seek target did not snap all views before decoder acknowledgement");
        await Until(() => Math.Abs(f.Player.Position.TotalSeconds - 12.25) < .4);
        await Call(f.Window, "TogglePlaybackAsync"); await Task.Delay(180);
        Assert(sliders.All(s => s.Value > 12.3), "resume did not restart smooth display clocks");
        typeof(MainWindow).GetField("_pendingSeek", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(f.Window, 25d);
        MediaField<System.Diagnostics.Stopwatch>(f.Window, "_seekClock").Restart();
        for (var i = 0; i < 4; i++)
        {
            SaInvoke(f.Window, "UpdateProgress"); await Task.Delay(90);
            Assert(sliders.All(s => Math.Abs(s.Value - 25) < .001 && !Pc016Animated(s)),
                "unacknowledged seek target repeatedly extrapolated and snapped back");
        }
        Assert(Math.Abs(f.Player.Position.TotalSeconds - 25) > 2, "pending fixture accidentally acknowledged before the hold check");
        Pc016Trace(f, "ack-before-native-seek");
        f.Player.Seek(TimeSpan.FromSeconds(25)); await Until(() => Math.Abs(f.Player.Position.TotalSeconds - 25) < .4);
        Pc016Trace(f, "ack-native-position-accepted");
        SaInvoke(f.Window, "UpdateProgress"); Pc016Trace(f, "ack-after-progress"); await Task.Delay(130);
        Pc016Trace(f, "ack-after-130ms-before-assert");
        Assert(sliders.All(s => s.Value > 25) && MediaField<double?>(f.Window, "_pendingSeek") is null,
            "acknowledged seek did not restart interpolation from native playback");
        f.Player.Stop(); SaInvoke(f.Window, "UpdateControls");
        Assert(sliders.All(s => !Pc016Animated(s) && s.Value == 0), "stop left a live playhead or previous-source position");
        Pc016Readback("smooth", new { samples, frozen, nativeSpeed = f.Player.SpeedRatio, progressTimerMilliseconds = 500 });
    }

    private static async Task Pc016Gestures()
    {
        using var f = new Pc016Fixture("gestures"); await f.Play(); f.Views(); await Call(f.Window, "TogglePlaybackAsync");
        var readback = new List<object>(); var seeks = f.Seeks();
        for (var i = 0; i < seeks.Length; i++)
        {
            var slider = seeks[i]; var before = f.Player.Position.TotalSeconds; var target = 8d + i * 3;
            Pc016Drag(slider, true); slider.Value = target;
            for (var tick = 0; tick < 3; tick++) { SaInvoke(f.Window, "UpdateProgress"); await Task.Delay(90); }
            Assert(Math.Abs(slider.Value - target) < .001 && Math.Abs(f.Player.Position.TotalSeconds - before) < .4,
                "progress fought a held preview or preview sought native audio: view " + i);
            Pc016Drag(slider, false); await Until(() => Math.Abs(f.Player.Position.TotalSeconds - target) < .4);
            Pc016Drag(slider, true); slider.Value = 24;
            slider.RaiseEvent(new DragCompletedEventArgs(0, 0, true) { RoutedEvent = Thumb.DragCompletedEvent });
            Assert(Math.Abs(f.Player.Position.TotalSeconds - target) < .4 && Math.Abs(slider.Value - target) < .4,
                "canceled drag committed or retained abandoned UI state: view " + i);
            Pc016Drag(slider, true); slider.Value = 22;
            slider.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount) { RoutedEvent = Mouse.LostMouseCaptureEvent, Handled = true });
            Assert(Math.Abs(f.Player.Position.TotalSeconds - target) < .4 && Math.Abs(slider.Value - target) < .4,
                "capture loss committed an abandoned seek: view " + i);
            readback.Add(new { view = i, committed = target, native = f.Player.Position.TotalSeconds, canceledTargets = new[] { 24, 22 } });
        }
        var main = seeks[0]; var source = PresentationSource.FromVisual(main)!;
        main.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, Key.Right) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
        main.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, Key.Right) { RoutedEvent = Keyboard.KeyDownEvent });
        var keyTarget = main.Value; SaInvoke(f.Window, "UpdateProgress");
        Assert(main.Value == keyTarget && MediaField<bool>(f.Window, "_seekDragging"), "progress overwrote a held keyboard preview");
        main.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, Key.Right) { RoutedEvent = Keyboard.PreviewKeyUpEvent });
        await Until(() => Math.Abs(f.Player.Position.TotalSeconds - keyTarget) < .4);
        Assert(!MediaField<bool>(f.Window, "_seekDragging") && !f.Player.IsPlaying, "keyboard completion resumed audio or retained its gesture");
        Pc016Readback("gestures", new { readback, keyTarget });
    }

    private static async Task Pc016RailAndReplacement()
    {
        using var f = new Pc016Fixture("rail"); await f.Play(); f.Views();
        var main = f.Seeks()[0]; SaInvoke(f.Window, "UpdateProgress");
        await Until(() => Pc016Animated(main));
        Assert(Pc016Animated(main), "fixture has no running animation to reproduce the rail conflict");
        // Reproduce Slider's move-to-point class handler: it writes the new base
        // value before the registered handled-events-too preview route executes.
        main.SetCurrentValue(RangeBase.ValueProperty, 18d);
        MediaHandledDown(main);
        Assert(!Pc016Animated(main) && Math.Abs(main.Value - 18) < .001, "old animation masked the clicked rail target");
        var gesture = MediaField<SliderGesture>(f.Window, "_seekGesture");
        main.ApplyTemplate(); f.Window.UpdateLayout(); var track = (Track)main.Template.FindName("PART_Track", main);
        var usable = track.ActualWidth - track.Thumb.ActualWidth;
        gesture.Point(track.TranslatePoint(new Point(track.Thumb.ActualWidth / 2 + usable * .7, track.ActualHeight / 2), main));
        Assert(Math.Abs(main.Value - main.Maximum * .7) < .1, "rail position ignored the native thumb/track geometry");
        var target = main.Value; SaInvoke(f.Window, "UpdateProgress"); Assert(main.Value == target, "held rail target was overwritten");
        MediaHandledUp(main); await Until(() => Math.Abs(f.Player.Position.TotalSeconds - target) < .4);
        Pc016Drag(main, true); main.Value = 28;
        Pc016Trace(f, "replacement-before-play");
        await f.Play("replacement"); Pc016Trace(f, "replacement-after-play-before-release"); Pc016Drag(main, false);
        Pc016Trace(f, "replacement-after-late-release-before-assert");
        Assert(f.Player.Position.TotalSeconds < 1 && main.Value < 1 && !MediaField<bool>(f.Window, "_seekDragging"),
            "an old pointer gesture sought the replacement source");
        var expanded = f.Seeks()[1]; Pc016Drag(expanded, true); expanded.Value = 27;
        await f.Play("replacement-two"); Pc016Drag(expanded, false);
        Assert(f.Player.Position.TotalSeconds < 1 && expanded.Value < 1 && !MediaField<bool>(f.Window, "_expandedSeekDragging"),
            "an old expanded gesture sought the replacement source");
        var mini = f.Mini!; Pc016Drag(mini.SeekSlider, true); mini.SeekSlider.Value = 26;
        await f.Play("replacement-three"); Pc016Drag(mini.SeekSlider, false);
        Assert(!mini.IsSeeking && f.Player.Position.TotalSeconds < 1 && mini.SeekSlider.Value < 1,
            "an old mini gesture sought the replacement occurrence");
        foreach (var slider in new[] { main, expanded })
        {
            var source = PresentationSource.FromVisual(slider)!;
            slider.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, Key.Right) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
            slider.Value = 24;
            await f.Play("keyboard-replacement-" + slider.Name);
            slider.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, Key.Right) { RoutedEvent = Keyboard.PreviewKeyUpEvent });
            Assert(MediaField<double?>(f.Window, "_pendingSeek") is null && f.Player.Position.TotalSeconds < 1,
                "a canceled keyboard release sought a replacement track: " + slider.Name);
        }
        Pc016Readback("rail", new { target, native = f.Player.Position.TotalSeconds, latestTrack = MediaField<TrackRow>(f.Window, "_current").Id });
    }

    private static async Task Pc016Volume()
    {
        using var f = new Pc016Fixture("volume"); await f.Play(); f.Views(); await Call(f.Window, "TogglePlaybackAsync");
        var idleFinishes = 0; var idle = new SliderGesture(new Slider(), () => { }, _ => idleFinishes++);
        idle.Cancel(); idle.Complete(true); idle.Cancel();
        Assert(idleFinishes == 0, "idle completion emitted a gesture callback");
        await Task.Delay(25); var unchangedWriteTime = File.GetLastWriteTimeUtc(f.Preferences.FilePath);
        MediaField<SliderGesture>(f.Window, "_volumeGesture").Cancel();
        Assert(File.GetLastWriteTimeUtc(f.Preferences.FilePath) == unchangedWriteTime
            && !MediaField<DispatcherTimer>(f.Window, "_volumeSaveTimer").IsEnabled, "idle cancellation saved preferences or started a persistence timer");
        var volumes = new[] { Control<Slider>(f.Window, "VolumeSlider"), EpElement<Slider>(f.Window, "ExpandedPlayerVolume"), f.Mini!.VolumeSlider };
        var readback = new List<object>();
        foreach (var (slider, index) in volumes.Select((s, i) => (s, i)))
        {
            Control<Slider>(f.Window, "VolumeSlider").Value = 0;
            var initialHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f.Preferences.FilePath)));
            Pc016Drag(slider, true);
            foreach (var value in new[] { 12d, 28, 45, 67, 37 })
            {
                slider.Value = value; SaInvoke(f.Window, "UpdateShellState");
                Assert(Math.Abs(f.Player.Volume - value / 100) < .001 && slider.Value == value, "native gain or held volume preview lagged: view " + index);
                Assert(f.Preferences.Load().Volume == 0 && initialHash == Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f.Preferences.FilePath))),
                    "pointer movement synchronously rewrote preferences: view " + index);
            }
            await Task.Delay(320);
            Assert(f.Preferences.Load().Volume == 0, "a held volume gesture persisted before release");
            Pc016Drag(slider, false); await Until(() => Math.Abs(f.Preferences.Load().Volume - .37) < .001);
            Assert(volumes.All(s => Math.Abs(s.Value - 37) < .001), "volume release did not synchronize the other views");
            Pc016Drag(slider, true); slider.Value = 65;
            slider.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount) { RoutedEvent = Mouse.LostMouseCaptureEvent });
            await Until(() => Math.Abs(f.Preferences.Load().Volume - .65) < .001);
            Assert(Math.Abs(f.Player.Volume - .65) < .001, "volume capture loss reverted audible gain");
            readback.Add(new { view = index, nativeVolume = f.Player.Volume, storedVolume = f.Preferences.Load().Volume, savedDuringPointerMovement = false });
        }
        f.Window.Close();
        Assert(!SliderGesture.HasActiveGesture && !MediaField<DispatcherTimer>(f.Window, "_volumeSaveTimer").IsEnabled,
            "closed views retained gesture state or a persistence timer");
        Pc016Readback("volume", new { readback, allAudioPausedBeforeVolumeChecks = true });
    }

    private static bool Pc016Animated(Slider slider) => DependencyPropertyHelper.GetValueSource(slider, RangeBase.ValueProperty).IsAnimated;
    private static string? _pc016AssemblyHash;
    private static void Pc016Trace(Pc016Fixture f, string stage)
    {
        var w = f.Window;
        object SliderState(Slider slider) => new
        {
            slider.Name, slider.Value, baseValue = slider.GetAnimationBaseValue(RangeBase.ValueProperty), animated = Pc016Animated(slider),
            slider.HasAnimatedProperties, slider.IsVisible, slider.IsEnabled, slider.Maximum, slider.ActualWidth,
            slider.IsMeasureValid, slider.IsArrangeValid, hasPresentationSource = PresentationSource.FromVisual(slider) is not null,
            thumbDragging = (slider.Template.FindName("PART_Track", slider) as Track)?.Thumb?.IsDragging
        };
        var trace = new
        {
            stage, utc = DateTimeOffset.UtcNow, nativePosition = f.Player.Position.TotalSeconds, nativeDuration = f.Player.Duration.TotalSeconds,
            f.Player.IsPlaying, f.Player.HasSource, nativeFile = Path.GetFileName(f.Player.SourceLocalPath),
            current = MediaField<TrackRow?>(w, "_current")?.Id, generation = MediaField<int>(w, "_playbackGeneration"),
            pending = MediaField<double?>(w, "_pendingSeek"), pendingMilliseconds = MediaField<System.Diagnostics.Stopwatch>(w, "_seekClock").ElapsedMilliseconds,
            loading = MediaField<bool>(w, "_loadingStream"), committing = MediaField<bool>(w, "_committingSeek"),
            mainDragging = MediaField<bool>(w, "_seekDragging"), expandedDragging = MediaField<bool>(w, "_expandedSeekDragging"),
            mainGesture = MediaField<SliderGesture>(w, "_seekGesture").IsActive,
            expandedGesture = MediaField<SliderGesture>(w, "_expandedSeekGesture").IsActive,
            miniSeeking = f.Mini?.IsSeeking, capturedType = Mouse.Captured?.GetType().FullName,
            views = f.Seeks().Select(SliderState).ToArray(),
            checkedAssemblySha256 = _pc016AssemblyHash ??= Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(MainWindow).Assembly.Location)))
        };
        File.AppendAllText(Path.Combine(_directory, "playback016-diagnostic-trace.jsonl"), JsonSerializer.Serialize(trace) + Environment.NewLine);
    }
    private static void Pc016Drag(Slider slider, bool start) => slider.RaiseEvent(start
        ? new DragStartedEventArgs(0, 0) { RoutedEvent = Thumb.DragStartedEvent }
        : new DragCompletedEventArgs(0, 0, false) { RoutedEvent = Thumb.DragCompletedEvent });
    private static void Pc016Readback(string suffix, object results) => File.WriteAllText(Path.Combine(_directory, "playback016-" + suffix + ".json"), JsonSerializer.Serialize(new
    { results, nativeFixture = "private generated 30-second WAV", realAccountUsed = false, profileRead = false, userMusicRead = false,
        networkRequests = 0, globalInputUsed = false, offscreenWindows = true,
        checkedAssemblySha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(MainWindow).Assembly.Location))) }, new JsonSerializerOptions { WriteIndented = true }));

    private sealed class Pc016Fixture : IDisposable
    {
        internal MainWindow Window { get; }
        internal NativePlayer Player { get; }
        internal PreferencesStore Preferences { get; }
        internal MiniPlayerWindow? Mini { get; private set; }
        private readonly FakeHandler _network;
        private readonly string _wav;
        private DesktopShell? _shell;
        internal Pc016Fixture(string suffix)
        {
            var directory = Path.Combine(_directory, "playback016-" + suffix); Directory.CreateDirectory(directory);
            _network = new FakeHandler((_, _) => throw new InvalidOperationException("private playback control fixture attempted network"));
            Preferences = new PreferencesStore(directory); Preferences.Save(new PlayerPreferences { Volume = 0, CrossfadeSeconds = 0 });
            Player = new NativePlayer(new AudioBuffer(Path.Combine(directory, "buffer"), _network));
            var offline = new OfflineLibrary(Path.Combine(directory, "offline"), new AudioBuffer(Path.Combine(directory, "downloads"), _network));
            Window = new(api: new MusicApi(_network), store: new SessionStore(Path.Combine(directory, "session")), restore: false,
                diagnosticsDirectory: directory, offline: offline, player: Player, preferences: Preferences)
            { Left = -16000, Top = 0, Width = 1050, Height = 680, ShowActivated = false, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual };
            Window.Show(); _wav = WriteWave("playback016-" + suffix + ".wav", 30);
        }
        internal Task Play(string id = "initial") => Window.PlayTrackAsync(new TrackRow { Id = "playback016-" + id, RequestId = "playback016-" + id,
            Title = "Локальный контроль " + id, Artist = "Fixture", DurationMs = 30000, Available = true, LocalPath = _wav });
        internal void Views()
        {
            SaInvoke(Window, "SetExpandedPlayerVisible", true, false); Window.UpdateLayout();
            Mini = MiniFixtureWindow(); _shell = MediaField<DesktopShell>(Window, "_shell");
            // Bind an already-offscreen mini to the real owner without invoking
            // ShowMiniPlayer, which deliberately opens a normal visible window.
            typeof(DesktopShell).GetField("_mini", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(_shell, Mini);
            typeof(DesktopShell).GetMethod("UpdateMiniPlayer", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(_shell, null);
            Mini.SeekRequested += seconds => SaInvoke(Window, "CommitSeek", seconds);
            Mini.VolumeRequested += percent => Control<Slider>(Window, "VolumeSlider").Value = percent;
            SaInvoke(Window, "UpdateShellState");
        }
        internal Slider[] Seeks() => [Control<Slider>(Window, "SeekSlider"), EpElement<Slider>(Window, "ExpandedPlayerSeek"), Mini!.SeekSlider];
        public void Dispose()
        {
            if (_shell is not null) typeof(DesktopShell).GetField("_mini", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(_shell, null);
            Mini?.Close(); if (Window.IsLoaded) Window.Close(); Player.Dispose();
            Assert(_network.Seen.Count == 0, "private local playback control checks issued network"); _network.Dispose();
        }
    }
}
