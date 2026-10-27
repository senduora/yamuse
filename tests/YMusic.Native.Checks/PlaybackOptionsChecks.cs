using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using YMusic.Native;

internal static partial class Program
{
    private static async Task CheckPlaybackOptions()
    {
        await Run("PlaybackOptions: 15 30 60 minute deadlines fade only in the final five seconds", () =>
        {
            var timer = new SleepTimerState();
            foreach (var (mode, minutes) in new[] { (SleepTimerMode.Minutes15, 15), (SleepTimerMode.Minutes30, 30), (SleepTimerMode.Minutes60, 60) })
            {
                timer.Start(mode);
                var duration = TimeSpan.FromMinutes(minutes);
                Assert(timer.Tick(duration - TimeSpan.FromSeconds(6), null) is { Completed: false, Gain: 1 }, "timer faded early");
                Assert(timer.Tick(duration - TimeSpan.FromSeconds(2.5), null) is { Completed: false, Gain: .5 }, "half fade differs");
                Assert(timer.Tick(duration, null) is { Completed: true, Gain: 0 }, "deadline did not expire");
                Assert(timer.Tick(duration + TimeSpan.FromHours(4), null).Completed, "suspend/resume missed elapsed deadline");
            }
            timer.Cancel(); Assert(!timer.Active && timer.Tick(TimeSpan.MaxValue, null).Gain == 1, "cancel retained attenuation");
            return Task.CompletedTask;
        });
        await Run("PlaybackOptions: after-track waits for known duration and respects remaining playback time", () =>
        {
            var timer = new SleepTimerState(); timer.Start(SleepTimerMode.AfterCurrentTrack);
            Assert(!timer.Tick(TimeSpan.FromDays(1), null).Completed, "unknown duration stopped current track");
            Assert(timer.Tick(TimeSpan.Zero, TimeSpan.FromSeconds(2.5)).Gain == .5, "after-track fade differs");
            Assert(timer.Tick(TimeSpan.Zero, TimeSpan.Zero).Completed, "after-track boundary did not stop");
            return Task.CompletedTask;
        });
        await Run("PlaybackOptions: device preference defaults migrates validates and roundtrips without timer persistence", () =>
        {
            var store = new PreferencesStore(Path.Combine(_directory, "playback-options-preferences"));
            Directory.CreateDirectory(Path.GetDirectoryName(store.FilePath)!);
            File.WriteAllText(store.FilePath, "{\"volume\":0.42}");
            Assert(store.Load().OutputDeviceId == "", "legacy settings did not retain system default");
            store.Save(store.Load() with { OutputDeviceId = "fake-output-A" });
            Assert(store.Load().OutputDeviceId == "fake-output-A" && store.Load().Volume == .42, "device save damaged other preferences");
            Assert(PreferencesStore.Normalize(new() { OutputDeviceId = "bad\nendpoint" }).OutputDeviceId == ""
                && PreferencesStore.Normalize(new() { OutputDeviceId = new string('x', 2049) }).OutputDeviceId == "", "invalid device ID accepted");
            Assert(!File.ReadAllText(store.FilePath).Contains("sleep", StringComparison.OrdinalIgnoreCase), "sleep timer was persisted across restart");
            return Task.CompletedTask;
        });
        await Run("PlaybackOptions: sleep gain pauses both crossfade slots restores user volume and preserves local files", async () =>
        {
            var first = PlaybackOptionsSilentWave("sleep-a", 15); var next = PlaybackOptionsSilentWave("sleep-b", 15);
            using var player = new NativePlayer(); player.Volume = .42;
            await player.OpenAsync(new Uri(first), CancellationToken.None);
            await player.PrepareNextAsync(new Uri(next), CancellationToken.None);
            Assert(player.TryPlayPrepared(new Uri(next), TimeSpan.FromSeconds(5)), "crossfade did not start");
            player.SetSleepGain(.3);
            Assert(player.Volume == .42 && player.SleepGain == .3 && player.FadingLocalPath == first, "transient sleep attenuation changed slider volume");
            var position = player.Position; player.Pause(); player.SetSleepGain(1);
            await Task.Delay(170);
            Assert(!player.IsPlaying && Math.Abs((player.Position - position).TotalSeconds) < .15 && player.Volume == .42 && player.SleepGain == 1,
                "sleep pause lost position or failed to restore user volume");
            player.Toggle(); Assert(player.IsPlaying, "manual resume failed after sleep pause");
            player.Stop(); Assert(File.Exists(first) && File.Exists(next), "sleep/crossfade deleted a local source");
        });
        await Run("PlaybackOptions: fake endpoint route covers current prepared fading and preserves position without selecting hardware", async () =>
        {
            var first = PlaybackOptionsSilentWave("route-a", 20); var next = PlaybackOptionsSilentWave("route-b", 20);
            using var player = new NativePlayer(); player.Volume = 0;
            await player.OpenAsync(new Uri(first), CancellationToken.None); player.Seek(TimeSpan.FromSeconds(4));
            await player.PrepareNextAsync(new Uri(next), CancellationToken.None);
            var a = new PlaybackOptionsFakeRoute("fake-A"); await player.SetOutputDeviceAsync(a);
            Assert(a.Players.Count == 2 && player.OutputDeviceId == "fake-A" && player.IsPlaying && player.HasPrepared,
                "current/prepared were not routed together");
            Assert(Math.Abs(player.Position.TotalSeconds - 4) < 1 && player.SourceLocalPath == first && player.PreparedLocalPath == next, "route lost position/source ownership");
            Assert(player.TryPlayPrepared(new Uri(next), TimeSpan.FromSeconds(5)), "routed prepared slot failed promotion");
            await player.PrepareNextAsync(new Uri(first), CancellationToken.None);
            var b = new PlaybackOptionsFakeRoute("fake-B"); await player.SetOutputDeviceAsync(b);
            Assert(b.Players.Count == 3 && player.FadingLocalPath == first && player.HasPrepared, "endpoint route omitted a crossfade participant");
            player.Pause(); var paused = player.Position;
            await player.SetOutputDeviceAsync(new PlaybackOptionsFakeRoute("fake-C"));
            await Task.Delay(100);
            Assert(!player.IsPlaying && Math.Abs((player.Position - paused).TotalSeconds) < .15, "changing endpoint resumed paused music");
            player.Stop(); Assert(File.Exists(first) && File.Exists(next), "routing took ownership of offline files");
        });
        await Run("PlaybackOptions: failed endpoint switch pauses playback and rolls the route back", async () =>
        {
            using var player = new NativePlayer(); player.Volume = 0;
            await player.OpenAsync(new Uri(PlaybackOptionsSilentWave("route-failure", 12)), CancellationToken.None);
            var a = new PlaybackOptionsFakeRoute("fake-working"); await player.SetOutputDeviceAsync(a);
            await Throws<MusicException>(() => player.SetOutputDeviceAsync(new PlaybackOptionsFakeRoute("fake-failed", fail: true)));
            Assert(!player.IsPlaying && player.OutputDeviceId == "fake-working", "failed route left playback audible or changed preference");
        });
        await Run("PlaybackOptions: UI after-track blocks next while minute timer fades pauses and restores position", async () =>
        {
            using var f = new PlaybackOptionsFixture("timer-ui");
            await f.Play();
            f.Window.SetSleepTimer(SleepTimerMode.AfterCurrentTrack);
            Assert(!(bool)PlaybackOptionsInvoke(f.Window, "CanAutoAdvancePlaybackOptions")! && !f.Player.HasPrepared, "after-track permitted crossfade preloading");
            Assert((bool)PlaybackOptionsInvoke(f.Window, "HandlePlaybackOptionsTrackEnded")!, "natural end bypassed sleep timer");
            Assert(!f.Player.IsPlaying && ReferenceEquals(PlaybackOptionsField<TrackRow>(f.Window, "_current"), f.First), "after-track advanced queue or continued playback");
            await Call(f.Window, "TogglePlaybackAsync"); f.Player.Seek(TimeSpan.FromSeconds(3));
            f.Window.SetSleepTimer(SleepTimerMode.Minutes15);
            PlaybackOptionsInvoke(f.Window, "ApplySleepTimerElapsed", TimeSpan.FromMinutes(15) - TimeSpan.FromSeconds(2.5));
            Assert(f.Player.SleepGain == .5 && f.Player.Volume == 0, "timer fade changed persisted volume");
            var position = f.Player.Position;
            PlaybackOptionsInvoke(f.Window, "ApplySleepTimerElapsed", TimeSpan.FromMinutes(15));
            Assert(!f.Player.IsPlaying && f.Player.SleepGain == 1 && Math.Abs((f.Player.Position - position).TotalSeconds) < .15,
                "timer completion failed pause/position/gain restore");
            Assert((bool)PlaybackOptionsInvoke(f.Window, "CanAutoAdvancePlaybackOptions")!, "completed timer still blocked queue");
        });
        await Run("PlaybackOptions: deadline cancels an in-flight open so a late completion cannot restart music", async () =>
        {
            using var f = new PlaybackOptionsFixture("pending-open");
            await (Task)PlaybackOptionsInvoke(f.Window, "EnsureAudioOutputReadyAsync")!;
            f.Window.SetSleepTimer(SleepTimerMode.Minutes15);
            var play = f.Window.PlayTrackAsync(f.First);
            Assert(PlaybackOptionsField<bool>(f.Window, "_loadingStream"), "fixture did not suspend in native open");
            PlaybackOptionsInvoke(f.Window, "ApplySleepTimerElapsed", TimeSpan.FromMinutes(15));
            await play;
            Assert(!f.Player.IsPlaying && !f.Player.HasSource && f.Player.SleepGain == 1,
                "pending open played after timer deadline or retained an owned source");
        });
        await Run("PlaybackOptions: fake device removal pauses and reconnection never resumes music", async () =>
        {
            using var f = new PlaybackOptionsFixture("device-ui"); await f.Play();
            await f.Outputs.ListAsync();
            await (Task)PlaybackOptionsInvoke(f.Window, "ChangeAudioOutputAsync", "fake-A", false)!;
            Assert(f.Player.OutputDeviceId == "fake-A" && f.Player.IsPlaying, "fake route was not selected");
            f.Outputs.Available = false;
            await (Task)PlaybackOptionsInvoke(f.Window, "RefreshAudioOutputsAsync")!;
            Assert(!f.Player.IsPlaying && f.Player.OutputDeviceId == "fake-A", "device removal fell back audibly to speakers");
            await Throws<MusicException>(() => (Task)PlaybackOptionsInvoke(f.Window, "EnsureAudioOutputReadyAsync")!);
            f.Outputs.Available = true;
            await (Task)PlaybackOptionsInvoke(f.Window, "RefreshAudioOutputsAsync")!;
            await (Task)PlaybackOptionsInvoke(f.Window, "EnsureAudioOutputReadyAsync")!;
            Assert(!f.Player.IsPlaying, "device reconnection resumed music automatically");
        });
        await Run("PlaybackOptions: settings render sleep and fake system output choices in Light Dark minimum and normal layouts", async () =>
        {
            using var f = new PlaybackOptionsFixture("settings-visual"); await f.Play();
            var previousTheme = ThemeManager.Mode;
            try
            {
                PlaybackOptionsInvoke(f.Window, "Settings_Click", f.Window, new RoutedEventArgs());
                var host = Control<ContentControl>(f.Window, "PlaybackOptionsHost");
                var sleep = PlaybackOptionsField<ComboBox>(f.Window, "_sleepTimerCombo");
                var output = PlaybackOptionsField<ComboBox>(f.Window, "_audioOutputCombo");
                var scroll = Control<ScrollViewer>(f.Window, "SettingsScroll");
                await (Task)PlaybackOptionsInvoke(f.Window, "RefreshAudioOutputsAsync")!;
                Assert(sleep.Items.Count == 5 && output.Items.Cast<AudioOutputDevice>().Select(d => d.Id).SequenceEqual(new[] { "", "fake-A" }),
                    "settings omitted sleep options or fake/system output choices");
                var captures = new List<object>();
                foreach (var (theme, width, height, route, mode) in new[]
                {
                    ("Light", 1050, 680, "", SleepTimerMode.Minutes30),
                    ("Light", 1280, 860, "fake-A", SleepTimerMode.AfterCurrentTrack),
                    ("Dark", 1050, 680, "fake-A", SleepTimerMode.AfterCurrentTrack),
                    ("Dark", 1280, 860, "", SleepTimerMode.Minutes30)
                })
                {
                    ThemeManager.SetMode(theme);
                    f.Window.Width = width; f.Window.Height = height; f.Window.UpdateLayout();
                    await (Task)PlaybackOptionsInvoke(f.Window, "ChangeAudioOutputAsync", route, false)!;
                    await (Task)PlaybackOptionsInvoke(f.Window, "RefreshAudioOutputsAsync")!;
                    f.Window.SetSleepTimer(mode);
                    host.BringIntoView(); f.Window.UpdateLayout(); await Task.Delay(80);
                    var top = host.TranslatePoint(new Point(), scroll).Y;
                    Assert(host.ActualWidth > 300 && sleep.ActualWidth > 200 && output.ActualWidth > 150
                        && top >= -1 && top + host.ActualHeight <= scroll.ActualHeight + 1,
                        "sleep/device host is clipped outside the settings viewport");
                    var choice = route.Length == 0 ? "system" : "fake";
                    // The existing helper draws window.Background plus a VisualBrush
                    // of the client tree, including its scrolled settings content.
                    await HomeWaveUi041Capture(f.Window, "playback-options-" + choice, theme, width, height);
                    captures.Add(new { theme, width, height, output = choice, timer = mode.ToString(),
                        file = $"home-wave041-playback-options-{choice}-{theme}-{width}x{height}.png" });
                }
                File.WriteAllText(Path.Combine(_directory, "playback-options-visual.json"), JsonSerializer.Serialize(new
                {
                    screenshots = captures, scrolledTo = "PlaybackOptionsHost.BringIntoView",
                    renderer = "window background plus client VisualBrush",
                    liveAccountUsed = false, physicalEndpointSelection = false
                }, new JsonSerializerOptions { WriteIndented = true }));
            }
            finally { ThemeManager.SetMode(previousTheme); }
        });
        File.WriteAllText(Path.Combine(_directory, "playback-options-scope.json"), JsonSerializer.Serialize(new
        {
            liveAccountUsed = false, externalNetworkUsed = false, physicalEndpointSelection = false,
            endpointSeam = "fake IAudioOutputRoute records native players without writing AudioDevice",
            audio = "private generated silent PCM WAV", timerDeadlines = "deterministic elapsed-time state and UI callback",
            manualHardwareQa = "not performed; physical device removal and driver-dependent switch timing remain unverified"
        }, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static string PlaybackOptionsSilentWave(string name, double seconds)
    {
        var path = WriteWave("playback-options-" + name + ".wav", seconds);
        var bytes = File.ReadAllBytes(path); Array.Clear(bytes, 44, bytes.Length - 44); File.WriteAllBytes(path, bytes); return path;
    }
    private static object? PlaybackOptionsInvoke(object value, string name, params object?[] args)
        => value.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(value, args);
    private static T PlaybackOptionsField<T>(object value, string name)
        => (T)value.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(value)!;

    private sealed class PlaybackOptionsFakeRoute(string id, bool fail = false) : IAudioOutputRoute
    {
        public string Id => id;
        internal HashSet<Windows.Media.Playback.MediaPlayer> Players { get; } = [];
        public void Apply(Windows.Media.Playback.MediaPlayer player)
        {
            if (fail) throw new InvalidOperationException("fake route failure");
            Players.Add(player); // Deliberately does not access AudioDevice or a real endpoint.
        }
    }
    private sealed class PlaybackOptionsFakeOutputs : IAudioOutputDevices
    {
        public event Action? Changed { add { } remove { } }
        internal bool Available { get; set; } = true;
        public void Watch() { }
        public void Dispose() { }
        public Task<IReadOnlyList<AudioOutputDevice>> ListAsync()
            => Task.FromResult<IReadOnlyList<AudioOutputDevice>>(Available ? [new("fake-A", "Fixture headphones")] : []);
        public Task<IAudioOutputRoute> ResolveAsync(string id)
        {
            if (id.Length > 0 && !Available) throw new MusicException("Fixture device unavailable");
            return Task.FromResult<IAudioOutputRoute>(new PlaybackOptionsFakeRoute(id));
        }
    }
    private sealed class PlaybackOptionsFixture : IDisposable
    {
        internal readonly PlaybackOptionsFakeOutputs Outputs = new();
        internal readonly NativePlayer Player = new();
        internal readonly MainWindow Window;
        internal readonly TrackRow First;
        private readonly PreferencesStore _preferences;
        internal PlaybackOptionsFixture(string name)
        {
            var directory = Path.Combine(_directory, "playback-options-" + name);
            _preferences = new PreferencesStore(directory); _preferences.Save(new() { Volume = 0 });
            Window = new MainWindow(restore: false, api: new MusicApi(new FakeHandler((_, _) => throw new InvalidOperationException("fixture forbids network"))),
                store: new SessionStore(directory), diagnosticsDirectory: directory, offline: new OfflineLibrary(Path.Combine(directory, "offline")),
                player: Player, preferences: _preferences, audioOutputDevices: Outputs);
            First = new TrackRow { Id = "playback-options-first", RequestId = "playback-options-first", Title = "Silent fixture", Artist = "Fixture", Available = true,
                DurationMs = 30000, LocalPath = PlaybackOptionsSilentWave(name, 30) };
            Window.Show();
        }
        internal async Task Play()
        {
            await (Task)PlaybackOptionsInvoke(Window, "EnsureAudioOutputReadyAsync")!;
            await Window.PlayTrackAsync(First);
        }
        public void Dispose() => Window.Close();
    }
}
