using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;
using Windows.Storage;
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
        await Run("PlaybackOptions: partial routing holds both audible crossfade slots together and pauses on failure", async () =>
        {
            foreach (var fail in new[] { false, true })
            {
                using var player = new NativePlayer { Volume = .5 };
                var outgoing = PlaybackOptionsSilentWave("held-crossfade-a-" + fail, 30);
                var incoming = PlaybackOptionsSilentWave("held-crossfade-b-" + fail, 30);
                var system = new PlaybackOptionsFakeRoute("");
                await player.SetOutputDeviceAsync(system);
                await player.OpenAsync(new Uri(outgoing), CancellationToken.None);
                await player.PrepareNextAsync(new Uri(incoming), CancellationToken.None);
                Assert(player.TryPlayPrepared(new Uri(incoming), TimeSpan.FromSeconds(5)), "fixture did not start WPF crossfade");
                var current = PlaybackOptionsField<object>(player, "_current");
                var fading = PlaybackOptionsField<object>(player, "_fading");
                var currentWpf = (System.Windows.Media.MediaPlayer)current.GetType().GetProperty("Player")!.GetValue(current)!;
                var fadingWpf = (System.Windows.Media.MediaPlayer)fading.GetType().GetProperty("Player")!.GetValue(fading)!;
                await Until(() => currentWpf.Volume > 0 && fadingWpf.Volume > 0);

                var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var openings = 0;
                player.BeforeRouteDecoderInstallForChecks = async token =>
                {
                    if (++openings != 1) return;
                    held.TrySetResult();
                    await release.Task.WaitAsync(token);
                    if (fail) throw new MusicException("Synthetic route replacement failed before installation");
                };
                var route = new PlaybackOptionsFakeRoute("fake-held-crossfade");
                var position = player.Position;
                var fadingPosition = fadingWpf.Position;
                var switching = player.SetOutputDeviceAsync(route);
                try
                {
                    await held.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    await Until(() => (Network(current) is null) != (Network(fading) is null));
                    var installed = Network(current) ?? Network(fading)!;
                    var remaining = Network(current) is null ? currentWpf : fadingWpf;
                    player.SetVolume(.65);
                    player.SetSleepGain(.7);
                    await Task.Delay(270);
                    Assert(!switching.IsCompleted && player.IsPlaying && installed.Volume == 0 && remaining.Volume == 0
                        && !player.IsCurrentAudioAdvancing && !player.IsFadingAudioAdvancing
                        && Math.Abs((player.Position - position).TotalSeconds) < .23,
                        "one route advanced or became audible before both replacements were ready");
                    release.TrySetResult();
                    if (fail)
                        await Throws<MusicException>(() => switching.WaitAsync(TimeSpan.FromSeconds(5)));
                    else
                        await switching.WaitAsync(TimeSpan.FromSeconds(5));
                    Assert(player.IsPlaying != fail && player.OutputRouteIsValid && player.OutputDeviceId == (fail ? system.Id : route.Id)
                        && player.SourceLocalPath == incoming && player.FadingLocalPath == outgoing
                        && ReferenceEquals(PlaybackOptionsField<object>(player, "_current"), current)
                        && ReferenceEquals(PlaybackOptionsField<object>(player, "_fading"), fading)
                        && Math.Abs((player.Position - position).TotalSeconds) < .4,
                        "route completion discarded the source, lost position or changed pause incorrectly");
                    var actualFade = Network(fading)?.PlaybackSession.Position ?? fadingWpf.Position;
                    Assert(Math.Abs((actualFade - fadingPosition).TotalSeconds) < .4
                        && (fail ? system.Players.Contains(installed) : route.Players.SetEquals([Network(current)!, Network(fading)!])),
                        "both participants did not return to the same accepted route and positions");
                    if (!fail) Assert(Network(current)!.Volume > 0 && Network(fading)!.Volume > 0,
                        "the successful complete route did not restore both crossfade gains");
                }
                finally { release.TrySetResult(); }
            }

            static Windows.Media.Playback.MediaPlayer? Network(object slot)
                => PlaybackOptionsField<Windows.Media.Playback.MediaPlayer?>(slot, "_network");
        });
        await Run("PlaybackOptions: decoder error after route open leaves the original decoder available for rollback", async () =>
        {
            foreach (var late in new[] { false, true }) await CheckFailure(late);

            static async Task CheckFailure(bool late)
            {
                using var player = new NativePlayer { Volume = 0 };
                var source = PlaybackInput.FileUri(PlaybackOptionsSilentWave("candidate-route-post-open-" + late, 30));
                await player.OpenAsync(source, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(8));
                player.Seek(TimeSpan.FromSeconds(8));
                await Until(() => player.Position.TotalSeconds > 7);
                var slot = PlaybackOptionsField<object>(player, "_current");
                var route = new PlaybackOptionsFakeRoute("fake-post-open");
                var reports = 0;
                Action<Exception>? reportLate = null;
                player.RouteDecoderOpenedForChecks = fail =>
                {
                    if (late) reportLate = fail;
                    else Report(fail);
                };
                player.ReadWpfPositionForChecks = decoder =>
                {
                    if (reportLate is { } fail) { reportLate = null; Report(fail); }
                    return decoder.Position;
                };
                await Throws<MusicException>(() => player.SetOutputDeviceAsync(route).WaitAsync(TimeSpan.FromSeconds(8)));
                player.ReadWpfPositionForChecks = null;
                Assert(reports == 1 && route.Players.Count == 1 && player.OutputRouteIsValid && player.OutputDeviceId.Length == 0
                    && ReferenceEquals(slot, PlaybackOptionsField<object>(player, "_current"))
                    && PlaybackOptionsField<Windows.Media.Playback.MediaPlayer?>(slot, "_network") is null
                    && player.HasSource && !player.IsPlaying && player.Position.TotalSeconds > 7,
                    "a failed candidate displaced the working WPF decoder or position");
                player.Toggle();
                Assert(player.IsPlaying, "the rolled-back original decoder could not play again");

                void Report(Action<Exception> fail)
                {
                    reports++;
                    fail(new COMException("Opened synthetic candidate failed before handoff"));
                }
            }
        });

        await Run("PlaybackOptions: failure resuming an acknowledged route keeps the previous saved output", async () =>
        {
            using var f = new PlaybackOptionsFixture("route-resume-commit");
            var w = f.Window;
            await f.Play();
            var next = PlaybackInput.FileUri(PlaybackOptionsSilentWave("route-resume-commit-next", 30));
            await f.Player.PrepareNextAsync(next, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(8));
            Assert(f.Player.TryPlayPrepared(next, TimeSpan.FromSeconds(10)), "fixture did not begin its crossfade");
            var store = PlaybackOptionsField<PreferencesStore>(w, "_preferenceStore");
            var resumes = 0;
            f.Player.BeforeRouteMixResumeForChecks = () =>
            {
                resumes++;
                throw new COMException("Synthetic mix resume rejection");
            };
            await ((Task)PlaybackOptionsInvoke(w, "ChangeAudioOutputAsync", "fake-A", false)!).WaitAsync(TimeSpan.FromSeconds(8));
            Assert(resumes == 1 && f.Outputs.ListRequests > 0 && f.Player.OutputRouteIsValid && !f.Player.IsPlaying
                && f.Player.OutputDeviceId.Length == 0 && PlaybackOptionsField<string>(w, "_preferredOutputDeviceId").Length == 0
                && store.Load().OutputDeviceId.Length == 0,
                "failed native mix resume committed the acknowledged output to preferences");
            f.Player.BeforeRouteMixResumeForChecks = null;
            await ((Task)PlaybackOptionsInvoke(w, "ChangeAudioOutputAsync", "fake-A", false)!).WaitAsync(TimeSpan.FromSeconds(8));
            Assert(f.Player.OutputDeviceId == "fake-A" && store.Load().OutputDeviceId == "fake-A",
                "a later successful switch did not persist its accepted output");
        });

        await Run("PlaybackOptions: crossfade retirement errors preserve the live gain and exact end events", async () =>
        {
            var media = await PlaybackOptionsMp3Async();
            foreach (var mode in new[] { "fade-ended", "tick", "current-ended" })
            {
                var ranges = new ConcurrentBag<long>();
                using var player = new NativePlayer(new AudioBuffer(Path.Combine(_directory, "fade-retire-" + mode),
                    new FakeHandler((request, _) => Task.FromResult(ProgressiveResponse(request, media, ranges))))) { Volume = .35 };
                var a = new Uri("https://s1.vlx.yandex.net/fade-retirement.mp3");
                var b = PlaybackInput.FileUri(PlaybackOptionsSilentWave("fade-retirement-" + mode, 30));
                await player.OpenAsync(new AudioDescriptor(a, "mp3", 192), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(8));
                var fading = PlaybackOptionsField<object>(player, "_current");
                var input = (PlaybackInput)fading.GetType().GetProperty("Input", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(fading)!;
                await player.PrepareNextAsync(b, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(8));
                Assert(player.TryPlayPrepared(b, TimeSpan.FromSeconds(10)) && File.Exists(input.Path), "fixture did not own an outgoing buffer");
                var current = PlaybackOptionsField<object>(player, "_current");
                var decoder = (System.Windows.Media.MediaPlayer)current.GetType().GetProperty("Player")!.GetValue(current)!;
                var transitions = new List<bool>();
                var ends = 0;
                var faults = 0;
                player.TransitionFinished += transitions.Add;
                player.Ended += (_, _) => ends++;
                player.AfterDecoderCloseForChecks = uri =>
                {
                    if (uri != a) return;
                    faults++;
                    throw new COMException("Synthetic old decoder close error");
                };
                if (mode == "tick")
                {
                    PlaybackOptionsSet(player, "_fadeElapsed", TimeSpan.FromSeconds(10));
                    PlaybackOptionsInvoke(player, "UpdateFade");
                }
                else PlaybackOptionsInvoke(player, "OnMediaEnded", mode == "fade-ended" ? fading : current);
                PlaybackOptionsInvoke(player, "OnMediaEnded", fading);
                Assert(faults == 1 && transitions.SequenceEqual([mode != "current-ended"]) && ends == (mode == "current-ended" ? 1 : 0)
                    && player.IsPlaying == (mode != "current-ended") && player.SourceLocalPath == b.LocalPath
                    && Math.Abs(decoder.Volume - player.Volume) < .01 && player.LastNativeError?.StartsWith("COMException", StringComparison.Ordinal) == true,
                    $"{mode}: retirement failure lost transition feedback, current end, gain, or diagnostics");
                await Until(() => !File.Exists(input.Path));
            }
        });

        await Run("PlaybackOptions: route rollback survives a second Play failure and an independent Pause failure", async () =>
        {
            using var f = new PlaybackOptionsFixture("partial-route-resume-pause-failure");
            var w = f.Window;
            var previous = new PlaybackOptionsFakeRoute("");
            await f.Player.SetOutputDeviceAsync(previous);
            var (a, b) = await BeginTransportCrossfade(f, "partial-route-resume-next");
            var queue = PlaybackOptionsField<PlaybackQueue>(w, "_playbackQueue");
            var target = queue.CurrentOccurrenceToken;
            const string uid = "fixture-output-clock";
            var checkpoint = new PlaybackStateStore(Path.Combine(_directory, "held-output-clock-checkpoint"));
            PlaybackOptionsSet(w, "_restore", true);
            PlaybackOptionsSet(w, "_account", new Account(uid, "Fixture", false));
            PlaybackOptionsSet(w, "_playbackStateAccountUid", uid);
            PlaybackOptionsSet(w, "_playbackStateStore", checkpoint);
            var restoring = false;
            var operations = new List<(Uri Source, bool Play)>();
            f.Player.BeforeRouteMixResumeForChecks = () => restoring = true;
            f.Player.BeforeDecoderTransportForChecks = (uri, play) =>
            {
                if (!restoring) return;
                operations.Add((uri, play));
                if (uri == a && play || uri == b && !play) throw new COMException("Synthetic partial resume and pause rejection");
            };
            await ((Task)PlaybackOptionsInvoke(w, "ChangeAudioOutputAsync", "fake-A", false)!).WaitAsync(TimeSpan.FromSeconds(8));
            var store = PlaybackOptionsField<PreferencesStore>(w, "_preferenceStore");
            var current = PlaybackOptionsField<object>(f.Player, "_current");
            var fading = PlaybackOptionsField<object>(f.Player, "_fading");
            var currentDecoder = PlaybackOptionsField<Windows.Media.Playback.MediaPlayer>(current, "_network");
            var fadingDecoder = PlaybackOptionsField<Windows.Media.Playback.MediaPlayer>(fading, "_network");
            Assert(operations.SequenceEqual([(b, true), (a, true), (b, false), (a, false)])
                && !f.Player.IsPlaying && !f.Player.OutputRouteIsValid && f.Player.OutputRouteChanging && f.Player.OutputDeviceId.Length == 0
                && !f.Player.IsCurrentAudioAdvancing && !f.Player.IsFadingAudioAdvancing && store.Load().OutputDeviceId.Length == 0
                && previous.Players.SetEquals([currentDecoder, fadingDecoder]) && currentDecoder.Volume == 0 && fadingDecoder.Volume == 0
                && PlaybackOptionsField<Diagnostics>(w, "_diagnostics").Stage == "paused",
                "rollback lost an independent pause, unmuted a live slot, or saved the failed route");
            var frozen = f.Player.Position.TotalSeconds;
            currentDecoder.PlaybackSession.Position = TimeSpan.FromSeconds(frozen + 7);
            await Until(() => currentDecoder.PlaybackSession.Position.TotalSeconds >= frozen + 6);
            PlaybackOptionsInvoke(f.Player, "OnMediaEnded", current);
            PlaybackOptionsInvoke(w, "UpdateProgress");
            PlaybackOptionsInvoke(w, "SavePlaybackState");
            Assert(Math.Abs(f.Player.Position.TotalSeconds - frozen) < .02
                && Math.Abs(Control<Slider>(w, "SeekSlider").Value - frozen) < .02
                && checkpoint.Load(uid) is { CurrentIndex: 1 } saved && Math.Abs(saved.PositionSeconds - frozen) < .02,
                "a mute-only decoder changed the confirmed UI position or current checkpoint");
            f.Player.BeforeDecoderTransportForChecks = null;
            f.Player.BeforeRouteMixResumeForChecks = null;
            await ((Task)PlaybackOptionsInvoke(w, "ChangeAudioOutputAsync", "fake-A", false)!).WaitAsync(TimeSpan.FromSeconds(8));
            Assert(f.Player.OutputRouteIsValid && !f.Player.OutputRouteChanging && !f.Player.IsPlaying
                && f.Player.OutputDeviceId == "fake-A" && store.Load().OutputDeviceId == "fake-A"
                && Math.Abs(f.Player.Position.TotalSeconds - frozen) < .02,
                "fresh device selection did not recover the paused clock");
            await Call(w, "TogglePlaybackAsync").WaitAsync(TimeSpan.FromSeconds(8));
            await Until(() => currentDecoder.PlaybackSession.Position.TotalSeconds < frozen + 1);
            Assert(f.Player.IsPlaying && queue.CurrentOccurrenceToken == target
                && Math.Abs(f.Player.Position.TotalSeconds - frozen) < 1,
                "explicit Play did not resume from the exact frozen position");
        });

        await Run("PlaybackOptions: ordinary Play and Pause contain partial crossfade transport failures", async () =>
        {
            foreach (var play in new[] { true, false })
            {
                using var f = new PlaybackOptionsFixture("ordinary-transport-crossfade-fault-" + play);
                var w = f.Window;
                var (a, b) = await BeginTransportCrossfade(f, "ordinary-transport-crossfade-next-" + play);
                Assert(a != b, "fixture did not distinguish the first and second decoder commands");
                if (play) await Call(w, "TogglePlaybackAsync");
                var operations = new List<(Uri Source, bool Play)>();
                f.Player.BeforeDecoderTransportForChecks = (uri, starting) =>
                {
                    operations.Add((uri, starting));
                    if (uri == a && starting == play) throw new COMException("Synthetic second slot transport refusal");
                };
                await Call(w, "TogglePlaybackAsync").WaitAsync(TimeSpan.FromSeconds(8));
                (Uri, bool)[] expected = play ? [(b, true), (a, true), (b, false), (a, false)] : [(b, false), (a, false)];
                Assert(operations.SequenceEqual(expected) && !f.Player.IsPlaying && f.Player.OutputRouteIsValid == play
                    && !f.Player.IsCurrentAudioAdvancing && !f.Player.IsFadingAudioAdvancing
                    && !PlaybackOptionsField<DispatcherTimer>(w, "_progressTimer").IsEnabled
                    && Control<TextBlock>(w, "StatusText").Text.Contains("не удалось изменить состояние", StringComparison.Ordinal),
                    "a partial media action escaped, left an active clock or showed a successful command");
            }
        });

        await Run("PlaybackOptions: Seek survives fade retirement failure and rejects an unwritten target", async () =>
        {
            using var f = new PlaybackOptionsFixture("seek-retired-decoder-fault");
            var w = f.Window;
            var (a, _) = await BeginTransportCrossfade(f, "seek-retired-decoder-next");
            var finishes = 0;
            f.Player.TransitionFinished += _ => finishes++;
            f.Player.AfterDecoderCloseForChecks = uri =>
            {
                if (uri == a) throw new COMException("Synthetic fading decoder close error");
            };
            var q = PlaybackOptionsField<PlaybackQueue>(w, "_playbackQueue");
            var occurrence = q.CurrentOccurrenceToken;
            PlaybackOptionsInvoke(w, "CommitSeek", 8d);
            await Until(() => f.Player.Position.TotalSeconds > 7);
            Assert(finishes == 1 && f.Player.FadingLocalPath is null && q.CurrentOccurrenceToken == occurrence
                && f.Player.LastNativeError?.StartsWith("COMException", StringComparison.Ordinal) == true,
                "a retirement failure dropped the seek or transition notification");
            f.Player.Pause();
            var confirmed = f.Player.Position.TotalSeconds;
            f.Player.BeforeDecoderSeekForChecks = () => throw new COMException("Synthetic position setter rejection");
            PlaybackOptionsInvoke(w, "CommitSeek", 18d);
            Assert(PlaybackOptionsField<double?>(w, "_pendingSeek") is null && q.CurrentOccurrenceToken == occurrence
                && Math.Abs(Control<Slider>(w, "SeekSlider").Value - confirmed) < .5
                && Control<TextBlock>(w, "StatusText").Text.Contains("не удалось перемотать", StringComparison.Ordinal),
                "a failed position write retained an unconfirmed target");
        });

        await Run("PlaybackOptions: all timelines hold the confirmed position during route changes", async () =>
        {
            foreach (var success in new[] { true, false })
            {
                using var f = new PlaybackOptionsFixture("held-output-timelines-" + success);
                var w = f.Window;
                await f.Play();
                await f.Rendered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                var next = PlaybackInput.FileUri(PlaybackOptionsSilentWave("held-timelines-next-" + success, 30));
                await f.Player.PrepareNextAsync(next, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(8));
                Assert(f.Player.TryPlayPrepared(next, TimeSpan.FromSeconds(10)), "fixture did not make a live crossfade");
                await Until(() => f.Player.IsCurrentAudioAdvancing);
                var oldShell = PlaybackOptionsField<DesktopShell?>(w, "_shell");
                var fakeShell = (DesktopShell)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(DesktopShell));
                var mini = new MiniPlayerWindow { ShowActivated = false, ShowInTaskbar = false, Left = -16000, Top = -4000 };
                PlaybackOptionsSet(fakeShell, "_owner", w);
                PlaybackOptionsSet(fakeShell, "_mini", mini);
                PlaybackOptionsSet(w, "_shell", fakeShell);
                var release = new TaskCompletionSource<IReadOnlyList<AudioOutputDevice>>(TaskCreationOptions.RunContinuationsAsynchronously);
                try
                {
                    mini.Show();
                    EpOpen(w);
                    w.UpdateLayout(); mini.UpdateLayout();
                    var sliders = new[] { Control<Slider>(w, "SeekSlider"), PlaybackOptionsField<Slider>(w, "_expandedPlayerSeek"), mini.SeekSlider };
                    PlaybackOptionsInvoke(w, "UpdateProgress");
                    await Until(() => sliders.All(slider => slider.IsVisible && Pc016Animated(slider)));
                    var calls = f.Outputs.ListRequests;
                    f.Outputs.HeldList = release;
                    var switching = (Task)PlaybackOptionsInvoke(w, "ChangeAudioOutputAsync", "fake-A", false)!;
                    await Until(() => f.Outputs.ListRequests > calls);
                    Assert(!switching.IsCompleted && f.Player.IsPlaying && !f.Player.IsCurrentAudioAdvancing
                        && System.Windows.Automation.AutomationProperties.GetName(Control<Button>(w, "PlayPauseButton")) == "Пауза",
                        "holding a route took away the Pause intent or did not suspend its clock");
                    var held = f.Player.Position.TotalSeconds;
                    for (var sample = 0; sample < 3; sample++)
                    {
                        await Task.Delay(220);
                        PlaybackOptionsInvoke(w, "UpdateProgress");
                        Assert(sliders.All(slider => !Pc016Animated(slider) && Math.Abs(slider.Value - held) < .3),
                            "a visible timeline extrapolated while the native position was suspended");
                    }
                    release.TrySetResult(success ? [new("fake-A", "Fixture headphones")] : []);
                    await switching.WaitAsync(TimeSpan.FromSeconds(8));
                    if (success) await Until(() => f.Player.IsCurrentAudioAdvancing);
                    PlaybackOptionsInvoke(w, "UpdateProgress");
                    await Until(() => sliders.All(slider => Pc016Animated(slider) == success));
                    Assert(f.Player.IsPlaying == success, "route completion changed the expected playback intent");
                }
                finally { release.TrySetResult([]); PlaybackOptionsSet(w, "_shell", oldShell); mini.Close(); }
            }
        });

        await Run("PlaybackOptions: prepared promotion commits despite retiring decoder errors", async () =>
        {
            foreach (var overlap in new[] { false, true })
            {
                using var f = new PlaybackOptionsFixture("prepared-retirement-error-" + overlap);
                var w = f.Window;
                var q = PlaybackOptionsField<PlaybackQueue>(w, "_playbackQueue");
                var b = new TrackRow { Id = "retirement-b", RequestId = "retirement-b", Title = "Prepared B", Available = true,
                    DurationMs = 30000, LocalPath = PlaybackOptionsSilentWave("prepared-retirement-b-" + overlap, 30) };
                var c = new TrackRow { Id = "retirement-c", RequestId = "retirement-c", Title = "Prepared C", Available = true,
                    DurationMs = 30000, LocalPath = PlaybackOptionsSilentWave("prepared-retirement-c-" + overlap, 30) };
                q.SetItems([f.First, b, c], f.First);
                await f.Play();
                await PreloadAwaitPrepared(w);
                if (overlap)
                {
                    f.Player.SetCrossfade(10);
                    await Call(w, "AdvanceAsync", 1, true).WaitAsync(TimeSpan.FromSeconds(8));
                    Assert(ReferenceEquals(q.Current, b) && f.Player.FadingLocalPath == f.First.LocalPath,
                        "fixture did not leave A fading during the second promotion");
                    await PreloadAwaitPrepared(w);
                }
                f.Player.SetCrossfade(0);
                var faults = 0;
                var retired = PlaybackInput.FileUri(f.First.LocalPath!);
                f.Player.AfterDecoderCloseForChecks = path =>
                {
                    if (path != retired) return;
                    faults++;
                    throw new COMException("Synthetic retiring decoder failed after close");
                };
                var expected = overlap ? c : b;
                var token = q.PeekNextOccurrenceToken();
                await Call(w, "AdvanceAsync", 1, false).WaitAsync(TimeSpan.FromSeconds(8));
                Assert(faults == 1 && f.Player.IsPlaying && f.Player.FadingLocalPath is null && f.Player.SourceLocalPath == expected.LocalPath
                    && q.CurrentOccurrenceToken == token && ReferenceEquals(q.Current, expected)
                    && ReferenceEquals(PlaybackOptionsField<TrackRow?>(w, "_current"), expected) && f.Player.Position.TotalSeconds < 3
                    && !string.IsNullOrWhiteSpace(Control<TextBlock>(w, "QualityText").Text),
                    "a retiring decoder exception left successful Play outside its queue and UI lifecycle");
            }
        });

        await Run("PlaybackOptions: failed endpoint switch pauses playback and rolls the route back", async () =>
        {
            using var player = new NativePlayer(); player.Volume = 0;
            await player.OpenAsync(new Uri(PlaybackOptionsSilentWave("route-failure", 12)), CancellationToken.None);
            var a = new PlaybackOptionsFakeRoute("fake-working"); await player.SetOutputDeviceAsync(a);
            await Throws<MusicException>(() => player.SetOutputDeviceAsync(new PlaybackOptionsFakeRoute("fake-failed", fail: true)));
            Assert(!player.IsPlaying && player.OutputDeviceId == "fake-working" && player.OutputRouteIsValid,
                "ordinary rollback did not restore a valid paused route");
        });
        await Run("PlaybackOptions: failed route rollback keeps default WPF and restores only the routed WinRT slot", async () =>
        {
            using var player = new NativePlayer { Volume = 0 };
            var currentPath = PlaybackOptionsSilentWave("rollback-default-current", 30);
            var preparedPath = PlaybackOptionsSilentWave("rollback-default-prepared", 30);
            var systemRoute = new PlaybackOptionsFakeRoute("");
            await player.SetOutputDeviceAsync(systemRoute);
            await player.OpenAsync(new Uri(currentPath), CancellationToken.None);
            await player.PrepareNextAsync(new Uri(preparedPath), CancellationToken.None);
            player.Seek(TimeSpan.FromSeconds(7));
            await Until(() => player.Position.TotalSeconds >= 6.5);
            player.Pause();
            var position = player.Position;
            var current = PlaybackOptionsField<object>(player, "_current");
            var prepared = PlaybackOptionsField<object>(player, "_prepared");
            Assert(PlaybackOptionsField<object?>(current, "_network") is null
                && PlaybackOptionsField<object?>(prepared, "_network") is null, "fixture did not start with two WPF slots");
            var attempts = 0;
            var rejectedRoute = new PlaybackOptionsFakeRoute("fake-partial") { Reject = _ => ++attempts == 1 };
            await Throws<MusicException>(() => player.SetOutputDeviceAsync(rejectedRoute));
            var routedPrepared = PlaybackOptionsField<Windows.Media.Playback.MediaPlayer>(prepared, "_network");
            Assert(attempts == 2 && rejectedRoute.Players.SetEquals([routedPrepared]) && systemRoute.Players.SetEquals([routedPrepared]),
                "rollback did not restore just the WinRT slot that had switched");
            Assert(player.OutputRouteIsValid && player.OutputDeviceId.Length == 0 && !player.IsPlaying && player.HasPrepared
                && ReferenceEquals(PlaybackOptionsField<object>(player, "_current"), current)
                && ReferenceEquals(PlaybackOptionsField<object>(player, "_prepared"), prepared)
                && PlaybackOptionsField<object?>(current, "_network") is null
                && Math.Abs((player.Position - position).TotalSeconds) < .2
                && player.SourceLocalPath == currentPath && player.PreparedLocalPath == preparedPath,
                "rollback reopened the unchanged WPF decoder, moved the paused position or lost the prepared track");
        });
        await Run("PlaybackOptions: WPF decoder replacement applies speed changed while the native source opens", async () =>
        {
            using var player = new NativePlayer { Volume = 0 };
            await player.OpenAsync(new Uri(PlaybackOptionsSilentWave("route-speed", 30)), CancellationToken.None);
            var route = new PlaybackOptionsFakeRoute("fake-speed");
            var change = player.SetOutputDeviceAsync(route);
            Assert(!change.IsCompleted && route.Players.Count == 1, "replacement did not wait for its native MediaOpened event");
            player.SetSpeed(1.5);
            await change.WaitAsync(TimeSpan.FromSeconds(5));
            Assert(player.IsPlaying && Math.Abs(route.Players.Single().PlaybackSession.PlaybackRate - 1.5) < 0.001,
                "replacement decoder captured stale speed during its open");
        });
        await Run("PlaybackOptions: decoder replacement carries the latest pending or acknowledged seek for that slot", async () =>
        {
            var source = new Uri(PlaybackOptionsSilentWave("seek-route-source", 30));
            var next = new Uri(PlaybackOptionsSilentWave("seek-route-next", 30));
            foreach (var scenario in new[] { "pending", "acknowledged", "rollback", "new-source" })
            {
                using var player = new NativePlayer { Volume = 0 };
                var system = new PlaybackOptionsFakeRoute("");
                await player.SetOutputDeviceAsync(system);
                await player.OpenPausedAsync(source, CancellationToken.None);
                var originalSlot = PlaybackOptionsField<object>(player, "_current");
                var originalDecoder = originalSlot.GetType().GetProperty("Player")!.GetValue(originalSlot);
                var clock = TimeSpan.FromSeconds(2);
                player.ReadWpfPositionForChecks = decoder => ReferenceEquals(decoder, originalDecoder) ? clock : decoder.Position;
                var route = new PlaybackOptionsFakeRoute("fake-route-seek");
                var change = player.SetOutputDeviceAsync(route, scenario == "rollback"
                    ? () => Task.FromException(new MusicException("Synthetic route confirmation failed"))
                    : null);
                Assert(!change.IsCompleted, "fixture did not keep the replacement behind native MediaOpened");
                player.Seek(TimeSpan.FromSeconds(7));
                player.Seek(TimeSpan.FromSeconds(19));
                Assert(player.Position == clock, "old native clock unexpectedly acknowledged the requested positions");
                var target = TimeSpan.FromSeconds(19);
                if (scenario == "acknowledged")
                {
                    clock = target;
                    Assert(player.Position == target, "fixture did not acknowledge the latest seek");
                    target = clock = TimeSpan.FromSeconds(22);
                }
                if (scenario == "new-source")
                {
                    await Task.WhenAll(change, player.OpenPausedAsync(next, CancellationToken.None)).WaitAsync(TimeSpan.FromSeconds(8));
                    Assert(!ReferenceEquals(PlaybackOptionsField<object>(player, "_current"), originalSlot)
                        && player.SourceLocalPath == next.LocalPath && player.Position.TotalSeconds < .5 && !player.IsPlaying,
                        "a former slot's pending seek affected the new source");
                    continue;
                }
                if (scenario == "rollback")
                    await Throws<MusicException>(() => change.WaitAsync(TimeSpan.FromSeconds(8)));
                else await change.WaitAsync(TimeSpan.FromSeconds(8));
                await Until(() => Math.Abs((player.Position - target).TotalSeconds) < .25);
                var installed = PlaybackOptionsField<Windows.Media.Playback.MediaPlayer>(originalSlot, "_network");
                Assert(ReferenceEquals(PlaybackOptionsField<object>(player, "_current"), originalSlot) && !player.IsPlaying
                    && player.OutputRouteIsValid && (scenario == "rollback"
                        ? player.OutputDeviceId == "" && system.Players.Contains(installed) : route.Players.Contains(installed)),
                    "decoder swap or rollback did not preserve ownership, pause or the effective route");
            }
        });
        await Run("PlaybackOptions: WPF end and failure cannot affect its WinRT replacement", async () =>
        {
            using var player = new NativePlayer { Volume = 0 };
            var source = new Uri(PlaybackOptionsSilentWave("stale-wpf-events", 30));
            await player.OpenAsync(source, CancellationToken.None);
            var slot = PlaybackOptionsField<object>(player, "_current");
            var oldWpf = slot.GetType().GetProperty("Player")!.GetValue(slot);
            await player.SetOutputDeviceAsync(new PlaybackOptionsFakeRoute("fake-wpf-retired"));
            var ended = 0; var failed = 0;
            player.Ended += (_, _) => ended++;
            player.Failed += _ => failed++;
            var failure = new IOException("Synthetic decoder failure");
            var args = typeof(System.Windows.Media.ExceptionEventArgs).GetConstructor(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, [typeof(Exception)], null)!.Invoke([failure]);
            PlaybackOptionsInvoke(slot, "MediaEnded", oldWpf, EventArgs.Empty);
            PlaybackOptionsInvoke(slot, "MediaFailed", oldWpf, args);
            Assert(ended == 0 && failed == 0 && player.IsPlaying && player.SourceLocalPath == source.LocalPath,
                "a retired WPF callback changed the installed decoder");
            PlaybackOptionsInvoke(player, "OnMediaEnded", slot);
            Assert(ended == 1 && !player.IsPlaying, "active decoder end did not reach playback");
            PlaybackOptionsInvoke(player, "OnMediaFailed", slot, failure);
            Assert(failed == 1 && !player.HasSource, "active decoder failure did not release playback");
        });
        await Run("PlaybackOptions: partial route and failed rollback block Play until all slots accept an output", async () =>
        {
            using var f = new PlaybackOptionsFixture("route-rollback");
            await f.Play();
            var previous = new PlaybackOptionsFakeRoute("fake-A");
            await SelectRoute(previous);
            var current = previous.Players.Single();
            var next = new TrackRow { Id = "rollback-next", RequestId = "rollback-next", Title = "Silent prepared track",
                Available = true, DurationMs = 30000, LocalPath = PlaybackOptionsSilentWave("rollback-next", 30) };
            var queue = PlaybackOptionsField<PlaybackQueue>(f.Window, "_playbackQueue");
            queue.SetItems([f.First, next], f.First);
            PlaybackOptionsInvoke(f.Window, "RefreshNextPreparation");
            await PreloadAwaitPrepared(f.Window);

            var requested = new PlaybackOptionsFakeRoute("fake-B") { Reject = player => !ReferenceEquals(player, current) };
            previous.Reject = player => ReferenceEquals(player, current);
            f.Player.Seek(TimeSpan.FromSeconds(5));
            await SelectRoute(requested);
            var position = f.Player.Position;
            Assert(requested.Players.SetEquals([current]) && !f.Player.OutputRouteIsValid && f.Player.OutputDeviceId == previous.Id,
                "partial switch and failed rollback were treated as a valid route");
            Assert(!f.Player.IsPlaying && f.Player.HasSource && !f.Player.HasPrepared && position.TotalSeconds >= 4,
                "failed rollback lost the current position or left a playable prepared slot");
            await f.Window.HandleDesktopMediaCommandAsync(DesktopMediaAction.Play);
            await Throws<MusicException>(() => ReadyPlaybackOutput(f.Window));
            Assert(!f.Player.IsPlaying && Math.Abs((f.Player.Position - position).TotalSeconds) < .2, "Play bypassed the invalid route");

            f.Outputs.HeldRoute = null;
            await (Task)PlaybackOptionsInvoke(f.Window, "RefreshAudioOutputsAsync")!;
            Assert(f.Player.OutputRouteIsValid && f.Player.OutputDeviceId == previous.Id && !f.Player.IsPlaying,
                "successful reapplication did not restore the route in pause");
            await f.Window.HandleDesktopMediaCommandAsync(DesktopMediaAction.Play);
            Assert(f.Player.IsPlaying && f.Player.Position.TotalSeconds >= position.TotalSeconds - .2,
                "explicit Play did not resume on the recovered route");

            Task SelectRoute(PlaybackOptionsFakeRoute route)
            {
                var resolved = new TaskCompletionSource<IAudioOutputRoute>(TaskCreationOptions.RunContinuationsAsynchronously);
                resolved.SetResult(route);
                f.Outputs.HeldRoute = resolved;
                return (Task)PlaybackOptionsInvoke(f.Window, "ChangeAudioOutputAsync", route.Id, false)!;
            }
        });
        await Run("PlaybackOptions: DeviceWatcher COM error cannot interrupt window cleanup", async () =>
        {
            using var f = new PlaybackOptionsFixture("watcher-disposal-error");
            await f.Play();
            var lifetime = PlaybackOptionsField<CancellationTokenSource>(f.Window, "_lifetime");
            f.Outputs.DisposeFailure = new COMException("Synthetic watcher failure", unchecked((int)0x80004005));
            f.Window.Close();
            Assert(f.Outputs.DisposeCalls == 1 && PlaybackOptionsField<IAudioOutputDevices?>(f.Window, "_audioOutputDevices") is null,
                "closed window retained its watcher");
            Assert(PlaybackOptionsField<bool>(f.Window, "_closed") && lifetime.IsCancellationRequested && !f.Player.HasSource,
                "watcher error interrupted window cleanup");
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
        await Run("PlaybackOptions: expired minute sleep stops early crossfade before the next occurrence commits", async () =>
        {
            using var f = new PlaybackOptionsFixture("expired-crossfade");
            var next = new TrackRow { Id = "sleep-crossfade-next", RequestId = "sleep-crossfade-next", Title = "Prepared next",
                Available = true, DurationMs = 30000, LocalPath = PlaybackOptionsSilentWave("expired-crossfade-next", 30) };
            var queue = PlaybackOptionsField<PlaybackQueue>(f.Window, "_playbackQueue");
            queue.SetItemsAt([f.First, next], 0);
            await f.Play();
            await PreloadAwaitPrepared(f.Window);
            PlaybackOptionsField<DispatcherTimer>(f.Window, "_progressTimer").Stop();
            f.Player.SetCrossfade(6);
            f.Player.Seek(TimeSpan.FromSeconds(27));
            await Until(() => f.Player.Position.TotalSeconds >= 26.5);
            var occurrence = queue.CurrentOccurrenceToken;
            var outgoing = PlaybackOptionsField<object>(f.Player, "_current");
            f.Window.SetSleepTimer(SleepTimerMode.Minutes15);
            var reads = 0;
            f.Window.SleepElapsedForChecks = () => { reads++; return TimeSpan.FromMinutes(15); };

            // No dispatcher turn between the deadline and the crossfade attempt.
            PlaybackOptionsInvoke(f.Window, "TryAutomaticTransition");

            Assert(reads == 1 && !f.Player.IsPlaying && queue.CurrentOccurrenceToken == occurrence
                && ReferenceEquals(queue.Current, f.First) && ReferenceEquals(PlaybackOptionsField<TrackRow>(f.Window, "_current"), f.First)
                && ReferenceEquals(PlaybackOptionsField<object>(f.Player, "_current"), outgoing)
                && f.Player.FadingLocalPath is null && !next.IsPlaying
                && !PlaybackOptionsField<SleepTimerState>(f.Window, "_sleepTimer").Active,
                "an expired sleep deadline started the prepared track before pausing");
        });
        await Run("PlaybackOptions: expired minute between Ended and prepared commit leaves the old occurrence", async () =>
        {
            foreach (var expired in new[] { false, true })
            {
                using var f = new PlaybackOptionsFixture("completed-prepared-deadline-" + expired);
                var next = new TrackRow { Id = "completed-prepared-next", RequestId = "completed-prepared-next", Title = "Prepared next",
                    Available = true, DurationMs = 30000, LocalPath = PlaybackOptionsSilentWave("completed-prepared-next-" + expired, 30) };
                var queue = PlaybackOptionsField<PlaybackQueue>(f.Window, "_playbackQueue");
                queue.SetItems([f.First, next]);
                await f.Play(); await PreloadAwaitPrepared(f.Window);
                PlaybackOptionsField<DispatcherTimer>(f.Window, "_progressTimer").Stop();
                SaInvoke(f.Window, "RememberWaveSource", next, new RadioBatch("timer-session", "timer-batch", [next]));
                var original = queue.CurrentOccurrenceToken; var prepared = PlaybackOptionsField<object>(f.Player, "_prepared");
                var outgoing = PlaybackOptionsField<object>(f.Player, "_current");
                f.Window.SetSleepTimer(SleepTimerMode.Minutes15);
                PlaybackOptionsField<DispatcherTimer>(f.Window, "_sleepTick").Stop();
                var reads = 0;
                f.Window.SleepElapsedForChecks = () => ++reads > 1 && expired ? TimeSpan.FromMinutes(15) : TimeSpan.Zero;
                PlaybackOptionsInvoke(f.Player, "OnMediaEnded", outgoing);
                await Until(() => !PlaybackOptionsField<bool>(f.Window, "_advancingPlayback"));
                if (expired)
                    Assert(reads > 1 && !f.Player.IsPlaying && queue.CurrentOccurrenceToken == original && queue.History.Count == 0
                        && ReferenceEquals(PlaybackOptionsField<object>(f.Player, "_current"), outgoing)
                        && PlaybackOptionsField<object?>(f.Window, "_waveListening") is null,
                        "prepared automatic playback committed after the deadline");
                else
                    Assert(f.Player.IsPlaying && ReferenceEquals(queue.Current, next)
                        && ReferenceEquals(PlaybackOptionsField<object>(f.Player, "_current"), prepared),
                        "timely natural end did not use the prepared decoder");
            }
        });
        await Run("PlaybackOptions: deadline cancels an in-flight open so a late completion cannot restart music", async () =>
        {
            using var f = new PlaybackOptionsFixture("pending-open");
            await ReadyPlaybackOutput(f.Window);
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
            await Throws<MusicException>(() => ReadyPlaybackOutput(f.Window));
            f.Outputs.Available = true;
            await (Task)PlaybackOptionsInvoke(f.Window, "RefreshAudioOutputsAsync")!;
            await ReadyPlaybackOutput(f.Window);
            Assert(!f.Player.IsPlaying, "device reconnection resumed music automatically");
        });
        await Run("PlaybackOptions: a held reconnect cannot revive a removed endpoint or replace the newer list", async () =>
        {
            foreach (var superseded in new[] { false, true })
            {
                using var f = new PlaybackOptionsFixture(superseded ? "stale-reconnect" : "fresh-reconnect");
                await f.Play();
                await (Task)PlaybackOptionsInvoke(f.Window, "ChangeAudioOutputAsync", "fake-A", false)!;
                f.Outputs.Available = false;
                await (Task)PlaybackOptionsInvoke(f.Window, "RefreshAudioOutputsAsync")!;
                Assert(!f.Player.IsPlaying && PlaybackOptionsField<bool>(f.Window, "_audioOutputUnavailable"),
                    "fixture did not pause the missing endpoint");

                f.Outputs.Available = true;
                f.Outputs.HeldRoute = new(TaskCreationOptions.RunContinuationsAsynchronously);
                var reconnect = (Task)PlaybackOptionsInvoke(f.Window, "RefreshAudioOutputsAsync")!;
                Assert(!reconnect.IsCompleted, "fixture did not suspend route resolution");
                f.Outputs.Available = false;
                if (superseded) await (Task)PlaybackOptionsInvoke(f.Window, "RefreshAudioOutputsAsync")!;
                var route = new PlaybackOptionsFakeRoute("fake-A");
                f.Outputs.HeldRoute.SetResult(route);
                await reconnect;
                Assert(route.Players.Count == (superseded ? 0 : 1), "reconnect did not respect the latest refresh revision");
                await Throws<MusicException>(() => ReadyPlaybackOutput(f.Window));
                await f.Window.HandleDesktopMediaCommandAsync(DesktopMediaAction.Play);
                var choices = PlaybackOptionsField<ComboBox>(f.Window, "_audioOutputCombo").Items.Cast<AudioOutputDevice>();
                Assert(!f.Player.IsPlaying && choices.Single(device => device.Id == "fake-A").Name == "Выбранное устройство недоступно",
                    "late reconnect revived an unavailable endpoint or replaced the latest list");
            }
        });
        await Run("PlaybackOptions: manual refresh restarts failed and aborted audio watchers without repeated attempts", async () =>
        {
            var outputs = new PlaybackOptionsFakeOutputs { WatchFailures = 1 };
            using var f = new PlaybackOptionsFixture("watcher-recovery", outputs);
            await f.Play();
            Assert(outputs.WatchAttempts == 1 && !outputs.Watching, "initial watch failure was lost or retried automatically");
            var refresh = PlaybackOptionsField<Button>(f.Window, "_audioOutputRefresh");
            refresh.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert(outputs.WatchAttempts == 2 && outputs.Watching, "manual refresh did not renew a failed watch");
            await (Task)PlaybackOptionsInvoke(f.Window, "ChangeAudioOutputAsync", "fake-A", false)!;
            outputs.Watching = false; // Simulates a previously registered watcher entering Aborted.
            refresh.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert(outputs.WatchAttempts == 3 && outputs.Watching, "manual refresh did not renew an aborted watch");
            var listCount = outputs.ListRequests;
            outputs.Available = false;
            outputs.RaiseChanged();
            outputs.RaiseChanged();
            outputs.RaiseChanged();
            await Until(() => !f.Player.IsPlaying && PlaybackOptionsField<bool>(f.Window, "_audioOutputUnavailable"));
            Assert(outputs.WatchAttempts == 3 && outputs.ListRequests == listCount + 1,
                "watcher burst did not coalesce or caused automatic registration retries");
        });
        await Run("PlaybackOptions: a stale explicit route leaves the previous preference and native slots paused", async () =>
        {
            foreach (var scenario in new[] { "before-apply", "fresh-confirmation", "superseded-confirmation" })
            {
                using var f = new PlaybackOptionsFixture("manual-route-" + scenario);
                await f.Play();
                await (Task)PlaybackOptionsInvoke(f.Window, "ChangeAudioOutputAsync", "fake-A", false)!;
                f.Outputs.SecondAvailable = true;
                await (Task)PlaybackOptionsInvoke(f.Window, "RefreshAudioOutputsAsync")!;
                var candidate = new PlaybackOptionsFakeRoute("fake-B");
                f.Outputs.HeldRoute = new(TaskCreationOptions.RunContinuationsAsynchronously);
                TaskCompletionSource<IReadOnlyList<AudioOutputDevice>>? heldConfirmation = null;
                if (scenario == "superseded-confirmation")
                {
                    heldConfirmation = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    f.Outputs.HeldList = heldConfirmation;
                    f.Outputs.HeldRoute.SetResult(candidate);
                }
                var selection = (Task)PlaybackOptionsInvoke(f.Window, "ChangeAudioOutputAsync", "fake-B", false)!;
                Assert(!selection.IsCompleted, "fixture did not hold the requested endpoint or its confirmation");
                if (heldConfirmation is not null) await Until(() => f.Outputs.HeldList is null);
                f.Outputs.SecondAvailable = false;
                if (scenario != "fresh-confirmation") await (Task)PlaybackOptionsInvoke(f.Window, "RefreshAudioOutputsAsync")!;
                if (heldConfirmation is null) f.Outputs.HeldRoute.SetResult(candidate);
                else heldConfirmation.SetResult([new("fake-A", "Fixture headphones"), new("fake-B", "Fixture speakers")]);
                await selection;
                var combo = PlaybackOptionsField<ComboBox>(f.Window, "_audioOutputCombo");
                Assert(candidate.Players.Count == (scenario == "before-apply" ? 0 : 1) && f.Player.OutputDeviceId == "fake-A"
                    && f.Player.OutputRouteIsValid && !f.Player.IsPlaying && f.Player.HasSource,
                    "a removed manual endpoint changed the live route or resumed playback");
                Assert(Equals(combo.SelectedValue, "fake-A") && !combo.Items.Cast<AudioOutputDevice>().Any(device => device.Id == "fake-B")
                    && PlaybackOptionsField<PreferencesStore>(f, "_preferences").Load().OutputDeviceId == "fake-A",
                    "a removed manual endpoint was persisted or survived in the list");
                f.Outputs.HeldRoute = null;
                await f.Window.HandleDesktopMediaCommandAsync(DesktopMediaAction.Play);
                Assert(f.Player.IsPlaying && f.Player.OutputDeviceId == "fake-A", "the previous working route could not be resumed explicitly");
            }
        });
        await Run("PlaybackOptions: newer available lists share a held reconnect and leave Play usable", async () =>
        {
            using var f = new PlaybackOptionsFixture("reconnect-newer-available");
            await f.Play();
            await (Task)PlaybackOptionsInvoke(f.Window, "ChangeAudioOutputAsync", "fake-A", false)!;
            f.Outputs.Available = false;
            await (Task)PlaybackOptionsInvoke(f.Window, "RefreshAudioOutputsAsync")!;
            f.Outputs.Available = true;
            f.Outputs.HeldRoute = new(TaskCreationOptions.RunContinuationsAsynchronously);
            var resolutions = f.Outputs.ResolveRequests;
            var reconnect = (Task)PlaybackOptionsInvoke(f.Window, "RefreshAudioOutputsAsync")!;
            await (Task)PlaybackOptionsInvoke(f.Window, "RefreshAudioOutputsAsync")!;
            await (Task)PlaybackOptionsInvoke(f.Window, "RefreshAudioOutputsAsync")!;
            Assert(!reconnect.IsCompleted && f.Outputs.ResolveRequests == resolutions + 1, "refresh started overlapping reconnects");
            f.Outputs.HeldRoute.SetResult(new PlaybackOptionsFakeRoute("fake-A"));
            await reconnect;
            await ReadyPlaybackOutput(f.Window);
            Assert(!f.Player.IsPlaying && f.Outputs.ResolveRequests == resolutions + 1, "reconnect revived Play or started a redundant route resolution");
            await f.Window.HandleDesktopMediaCommandAsync(DesktopMediaAction.Play);
            Assert(f.Player.IsPlaying && f.Player.OutputDeviceId == "fake-A", "surviving endpoint stayed blocked after reconnect");
        });
        await Run("QaFixes061: unavailable output blocks resume while normal pause and reconnect remain explicit", async () =>
        {
            using var f = new PlaybackOptionsFixture("qa-unavailable-resume"); await f.Play();
            await Call(f.Window, "TogglePlaybackAsync");
            Assert(!f.Player.IsPlaying, "normal Pause was blocked");
            await Call(f.Window, "TogglePlaybackAsync");
            Assert(f.Player.IsPlaying, "normal explicit resume was blocked");
            await (Task)PlaybackOptionsInvoke(f.Window, "ChangeAudioOutputAsync", "fake-A", false)!;
            f.Outputs.Available = false;
            await (Task)PlaybackOptionsInvoke(f.Window, "RefreshAudioOutputsAsync")!;
            Assert(!f.Player.IsPlaying && PlaybackOptionsField<bool>(f.Window, "_audioOutputUnavailable"), "fixture did not remove the selected output");
            await Call(f.Window, "TogglePlaybackAsync");
            Assert(!f.Player.IsPlaying && f.Player.OutputDeviceId == "fake-A"
                && ReferenceEquals(PlaybackOptionsField<TrackRow>(f.Window, "_current"), f.First)
                && Equals(Control<Button>(f.Window, "PlayPauseButton").Content, "\uE768")
                && Control<TextBlock>(f.Window, "StatusText").Text.Contains("недоступно", StringComparison.Ordinal),
                "Play resumed an unavailable route or changed the current source instead of explaining the block");
            f.Outputs.Available = true;
            await (Task)PlaybackOptionsInvoke(f.Window, "RefreshAudioOutputsAsync")!;
            Assert(!f.Player.IsPlaying, "reconnection replayed the blocked resume intent");
            await Call(f.Window, "TogglePlaybackAsync");
            Assert(f.Player.IsPlaying && f.Player.Volume == 0 && File.Exists(f.First.LocalPath!), "available output did not accept a new explicit resume");
        });
        await Run("QaFixes061: pending output switch permits pause but drops resume intent and late completion after close", async () =>
        {
            using var f = new PlaybackOptionsFixture("qa-switch-resume"); await f.Play();
            await (Task)PlaybackOptionsInvoke(f.Window, "ChangeAudioOutputAsync", "fake-A", false)!;
            var output = PlaybackOptionsField<ComboBox>(f.Window, "_audioOutputCombo");
            f.Outputs.HeldRoute = new(TaskCreationOptions.RunContinuationsAsynchronously);
            output.SelectedValue = "";
            var switching = PlaybackOptionsField<Task>(f.Window, "_audioOutputReady");
            Assert(!switching.IsCompleted && PlaybackOptionsField<bool>(f.Window, "_audioOutputSwitching"), "fixture did not hold the output switch");
            await Call(f.Window, "TogglePlaybackAsync");
            Assert(!f.Player.IsPlaying, "pending output switch blocked Pause");
            var resume = Call(f.Window, "TogglePlaybackAsync");
            Assert(resume.IsCompleted, "resume waited for a route and retained an obsolete intent");
            await resume;
            Assert(!f.Player.IsPlaying && ReferenceEquals(PlaybackOptionsField<TrackRow>(f.Window, "_current"), f.First)
                && Control<TextBlock>(f.Window, "StatusText").Text.Contains("переключения", StringComparison.Ordinal), "pending switch accepted resume");
            f.Outputs.HeldRoute.SetResult(new PlaybackOptionsFakeRoute(""));
            await switching;
            Assert(!f.Player.IsPlaying && f.Player.OutputDeviceId == "", "route completion resumed the blocked intent");
            await Call(f.Window, "TogglePlaybackAsync");
            Assert(f.Player.IsPlaying, "completed output switch blocked a new explicit resume");

            f.Outputs.HeldRoute = new(TaskCreationOptions.RunContinuationsAsynchronously);
            output.SelectedValue = "fake-A";
            var closingSwitch = PlaybackOptionsField<Task>(f.Window, "_audioOutputReady");
            Assert(!closingSwitch.IsCompleted && PlaybackOptionsField<bool>(f.Window, "_audioOutputSwitching"), "fixture did not hold the closing switch");
            await Call(f.Window, "TogglePlaybackAsync"); // Pause remains usable before close.
            await Call(f.Window, "TogglePlaybackAsync"); // Rejected resume must have no continuation.
            f.Window.Close();
            f.Outputs.HeldRoute.SetResult(new PlaybackOptionsFakeRoute("fake-A"));
            await closingSwitch;
            await Call(f.Window, "TogglePlaybackAsync");
            Assert(PlaybackOptionsField<bool>(f.Window, "_closed") && !f.Player.IsPlaying && !f.Player.HasSource,
                "late route completion or a closed-window callback revived playback");
        });
        await Run("PlaybackOptions: paused prepared Next waits for the new route and retains the duplicate occurrence", async () =>
        {
            using var f = new PlaybackOptionsFixture("paused-prepared-route");
            var next = new TrackRow { Id = "route-paused", RequestId = "route-paused", Title = "Prepared route fixture",
                Available = true, DurationMs = 30000, LocalPath = PlaybackOptionsSilentWave("route-paused-next", 30) };
            var queue = PlaybackOptionsField<PlaybackQueue>(f.Window, "_playbackQueue");
            queue.SetItemsAt([f.First, next, next], 0);
            await f.Play();
            await PreloadAwaitPrepared(f.Window);
            await Call(f.Window, "TogglePlaybackAsync");
            var occurrence = queue.PeekNextOccurrenceToken(false);
            var route = new PlaybackOptionsFakeRoute("fake-A");
            f.Outputs.HeldRoute = new(TaskCreationOptions.RunContinuationsAsynchronously);
            PlaybackOptionsField<ComboBox>(f.Window, "_audioOutputCombo").SelectedValue = route.Id;
            var change = PlaybackOptionsField<Task>(f.Window, "_audioOutputReady");
            var nextCommand = Call(f.Window, "AdvanceAsync", 1, false);
            Assert(!change.IsCompleted && !nextCommand.IsCompleted && !f.Player.IsPlaying && queue.CurrentOccurrenceToken == occurrence,
                "a paused source promoted its prepared successor before routing");
            f.Outputs.HeldRoute.SetResult(route);
            await Task.WhenAll(change, nextCommand).WaitAsync(TimeSpan.FromSeconds(8));
            var slot = PlaybackOptionsField<object>(f.Player, "_current");
            Assert(f.Player.IsPlaying && f.Player.SourceLocalPath == next.LocalPath && queue.CurrentOccurrenceToken == occurrence
                && queue.CurrentIndex == 1 && route.Players.Contains(PlaybackOptionsField<Windows.Media.Playback.MediaPlayer>(slot, "_network")),
                "Next started a different duplicate or opened a decoder on the previous output");
        });
        await Run("PlaybackOptions: held stream waits for a new output while Sleep and Next supersede the open", async () =>
        {
            var media = await PlaybackOptionsMp3Async();
            foreach (var action in new[] { "play", "sleep", "next" })
            {
                var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                using var f = new PlaybackOptionsFixture("held-stream-" + action,
                    apiHandler: new FakeHandler((_, _) => Task.FromResult(PlaybackOptionsAudioInfo(1))),
                    bufferHandler: new FakeHandler(async (_, token) =>
                    {
                        entered.TrySetResult();
                        await release.Task.WaitAsync(token);
                        return new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new ByteArrayContent(media) };
                    }));
                PlaybackOptionsSet(f.Window, "_session", FakeSession);
                var online = new TrackRow { Id = "8801", RequestId = "8801:91", Title = "Held audio",
                    DurationMs = 60000, Available = true };
                var queue = PlaybackOptionsField<PlaybackQueue>(f.Window, "_playbackQueue");
                queue.SetItemsAt([online, f.First], 0);
                var occurrence = queue.CurrentOccurrenceToken;
                var following = queue.PeekNextOccurrenceToken(false);
                var opening = f.Window.PlayTrackAsync(online);
                try
                {
                    await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    var route = new PlaybackOptionsFakeRoute("fake-A");
                    f.Outputs.HeldRoute = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    PlaybackOptionsField<ComboBox>(f.Window, "_audioOutputCombo").SelectedValue = route.Id;
                    var change = PlaybackOptionsField<Task>(f.Window, "_audioOutputReady");
                    release.TrySetResult();
                    await Until(() => f.Player.HasSource && f.Player.Duration.TotalSeconds > 30);
                    await f.Window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                    Assert(!change.IsCompleted && !opening.IsCompleted && !f.Player.IsPlaying && queue.CurrentOccurrenceToken == occurrence,
                        "new media became audible before the endpoint change completed");
                    Task? advance = null;
                    if (action == "sleep")
                    {
                        f.Window.SetSleepTimer(SleepTimerMode.Minutes15);
                        PlaybackOptionsInvoke(f.Window, "ApplySleepTimerElapsed", TimeSpan.FromMinutes(16));
                        await opening.WaitAsync(TimeSpan.FromSeconds(5));
                        Assert(!f.Player.HasSource, "Sleep left the canceled cold decoder attached");
                    }
                    else if (action == "next")
                    {
                        advance = Call(f.Window, "AdvanceAsync", 1, false);
                        Assert(!f.Player.IsPlaying && queue.CurrentOccurrenceToken == following, "Next did not supersede the held occurrence");
                    }
                    f.Outputs.HeldRoute.SetResult(route);
                    await Task.WhenAll(change, opening, advance ?? Task.CompletedTask).WaitAsync(TimeSpan.FromSeconds(8));
                    Assert(f.Player.IsPlaying == (action != "sleep") && queue.CurrentOccurrenceToken == (action == "next" ? following : occurrence),
                        "route completion ignored Sleep or the superseding occurrence");
                    if (action != "sleep")
                    {
                        var slot = PlaybackOptionsField<object>(f.Player, "_current");
                        Assert(route.Players.Contains(PlaybackOptionsField<Windows.Media.Playback.MediaPlayer>(slot, "_network")),
                            "audio started through a decoder that was never routed to the selected endpoint");
                    }
                }
                finally { release.TrySetResult(); }
            }
        });
        await Run("PlaybackOptions: automatic startup remains cancellable before both opening and final routing", async () =>
        {
            var media = await PlaybackOptionsMp3Async();
            foreach (var (late, action) in new[] { (false, "pause"), (true, "pause"), (false, "toggle"),
                (true, "toggle"), (false, "after-track"), (true, "after-track"), (true, "play"), (true, "seek"), (true, "seek-stop") })
            {
                var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var routing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                using var f = new PlaybackOptionsFixture($"natural-ready-{late}-{action}",
                    apiHandler: new FakeHandler((request, _) => Task.FromResult(request.RequestUri!.AbsolutePath switch
                    {
                        "/rotor/session/new" => Json(TwBatch("fixture-auto-ready", "start", 8803)),
                        "/get-file-info" => PlaybackOptionsAudioInfo(1),
                        _ => Json("{}")
                    })),
                    bufferHandler: new FakeHandler(async (_, token) =>
                    {
                        entered.TrySetResult();
                        await release.Task.WaitAsync(token);
                        return new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new ByteArrayContent(media) };
                    }));
                var w = f.Window;
                var q = PlaybackOptionsField<PlaybackQueue>(w, "_playbackQueue");
                var seek = action is "seek" or "seek-stop";
                var timecode = action == "seek" ? 42d : 12d;
                PlaybackOptionsSet(w, "_session", FakeSession);
                var api = PlaybackOptionsField<MusicApi>(w, "_api");
                var station = await api.StartWaveAsync(FakeSession, CancellationToken.None);
                var next = station.Tracks[0];
                SaInvoke(w, "RememberWaveSource", next, station);
                q.SetItems([f.First]);
                await f.Play();
                await f.Rendered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                var (windows, Dispatch) = PlaybackOptionsMediaGate(w);
                const string checkpointUid = "fixture-cold-seek";
                PlaybackStateStore? checkpoint = null;
                if (seek)
                {
                    checkpoint = new PlaybackStateStore(Path.Combine(_directory, "cold-position-checkpoint-" + action));
                    PlaybackOptionsSet(w, "_restore", true);
                    PlaybackOptionsSet(w, "_account", new Account(checkpointUid, "Fixture", false));
                    PlaybackOptionsSet(w, "_playbackStateAccountUid", checkpointUid);
                    PlaybackOptionsSet(w, "_playbackStateStore", checkpoint);
                }
                q.SetItems([f.First, next]);
                var occurrence = q.OccurrenceTokenAt(1);
                PlaybackOptionsInvoke(w, "CancelNextPreparation", true);
                if (!late) PlaybackOptionsSet(w, "_audioOutputReady", routing.Task);
                PlaybackOptionsInvoke(f.Player, "OnMediaEnded", PlaybackOptionsField<object>(f.Player, "_current"));
                try
                {
                    if (late)
                    {
                        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                        PlaybackOptionsSet(w, "_audioOutputReady", routing.Task);
                        release.TrySetResult();
                        await Until(() => f.Player.HasSource && f.Player.Duration.TotalSeconds > 30);
                        await w.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                    }
                    Assert(q.CurrentOccurrenceToken == occurrence && !f.Player.IsPlaying && f.Player.HasSource == late
                        && System.Windows.Automation.AutomationProperties.GetName(Control<Button>(w, "PlayPauseButton")) == "Пауза",
                        "the committed cold occurrence lost the pending transport action");
                    Assert(PlaybackOptionsField<object?>(w, "_waveListening") is null, "the decoder started listening before routing");
                    if (action == "pause")
                    {
                        Assert(windows.HandlesMediaKeys, "pending startup did not reach the media gateway");
                        await Dispatch(Windows.Media.SystemMediaTransportControlsButton.Pause);
                        await Dispatch(Windows.Media.SystemMediaTransportControlsButton.Pause);
                    }
                    else if (seek)
                    {
                        Assert(f.Player.Duration.TotalSeconds > timecode && (action != "seek" || next.DurationMs < timecode * 1000),
                            "fixture did not provide native duration past the accepted catalogue timecode");
                        PlaybackOptionsSet(w, "_lyricsOpen", true);
                        PlaybackOptionsSet(w, "_lyricLines", new[] { new LyricLine(timecode, "Selected timecode") });
                        if (action == "seek")
                        {
                            var slot = PlaybackOptionsField<object>(f.Player, "_current");
                            var decoder = PlaybackOptionsField<Windows.Media.Playback.MediaPlayer?>(slot, "_network");
                            var starting = PlaybackOptionsField<object>(w, "_automaticTrackStart");
                            using var unopened = new Windows.Media.Playback.MediaPlayer { AutoPlay = false, Volume = 0 };
                            PlaybackOptionsSet(slot, "_network", unopened);
                            try
                            {
                                Assert(f.Player.HasSource && f.Player.Duration == TimeSpan.Zero
                                    && PlaybackOptionsInvoke(w, "TrySeekLyricLine", 0, false) is false
                                    && ReferenceEquals(PlaybackOptionsField<object?>(w, "_automaticTrackStart"), starting),
                                    "an unknown native duration accepted a zero seek or canceled the pending open");
                            }
                            finally { PlaybackOptionsSet(slot, "_network", decoder); }
                        }
                        Assert(PlaybackOptionsInvoke(w, "TrySeekLyricLine", 0, false) is true,
                            "fixture did not send a valid synchronized-lyrics seek");
                    }
                    else if (action == "toggle") await Call(w, "TogglePlaybackAsync");
                    else if (action == "after-track") w.SetSleepTimer(SleepTimerMode.AfterCurrentTrack);
                    else
                    {
                        await w.HandleDesktopMediaCommandAsync(DesktopMediaAction.Play);
                        Assert(!f.Player.IsPlaying && q.CurrentOccurrenceToken == occurrence,
                            "explicit Play restarted an already active pending transition");
                    }
                    routing.TrySetResult();
                    release.TrySetResult();
                    await Until(() => !PlaybackOptionsField<bool>(w, "_loadingStream"));
                    var starts = action == "play";
                    Assert(q.CurrentOccurrenceToken == occurrence && f.Player.IsPlaying == starts
                        && (PlaybackOptionsField<object?>(w, "_waveListening") is not null) == starts,
                        "readiness changed the selected occurrence, ignored Pause or committed an unused listening lifecycle");
                    if (action == "after-track")
                        Assert(!PlaybackOptionsField<SleepTimerState>(w, "_sleepTimer").Active, "late after-track sleep stayed armed on the unplayed occurrence");
                    if (action == "seek-stop")
                    {
                        var stopStore = checkpoint ?? throw new InvalidOperationException("missing Stop checkpoint fixture");
                        Assert(windows.HandlesMediaKeys && stopStore.Load(checkpointUid)?.PositionSeconds == timecode,
                            "fixture did not preserve the timecode and the pre-Stop session");
                        await Dispatch(Windows.Media.SystemMediaTransportControlsButton.Stop);
                        Assert(!windows.HandlesMediaKeys && !f.Player.HasSource && q.CurrentOccurrenceToken == occurrence
                            && stopStore.Load(checkpointUid)?.PositionSeconds == 0
                            && Control<TextBlock>(w, "PositionText").Text == "0:00"
                            && PlaybackOptionsField<double?>(w, "_restoredPlaybackSeconds") is null,
                            "Stop retained the paused position or discarded the exact occurrence");
                        await Dispatch(Windows.Media.SystemMediaTransportControlsButton.Play);
                        Assert(!f.Player.IsPlaying, "stopped SMTC still accepted Play");
                        await Call(w, "TogglePlaybackAsync").WaitAsync(TimeSpan.FromSeconds(8));
                        Assert(f.Player.IsPlaying && q.CurrentOccurrenceToken == occurrence && f.Player.Position.TotalSeconds < 2,
                            "local Play did not restart the stopped occurrence from zero");
                    }
                    else if (action == "pause" || seek)
                    {
                        Assert(windows.HandlesMediaKeys && !f.Player.HasSource, "paused exact occurrence vanished from the media gateway");
                        if (seek)
                        {
                            var saved = checkpoint?.Load(checkpointUid);
                            Assert(PlaybackOptionsField<double?>(w, "_restoredPlaybackSeconds") == timecode
                                && saved is { CurrentIndex: 1 } && saved.PositionSeconds == timecode
                                && saved.Tracks[1].RequestId == next.RequestId && saved.Tracks[1].DurationMs == next.DurationMs
                                && saved.PositionDurationMs > next.DurationMs,
                                "the accepted native timecode was not persisted for the exact occurrence");
                        }
                        await Dispatch(Windows.Media.SystemMediaTransportControlsButton.Play).WaitAsync(TimeSpan.FromSeconds(8));
                        Assert(f.Player.IsPlaying && q.CurrentOccurrenceToken == occurrence
                            && PlaybackOptionsField<object?>(w, "_waveListening") is not null,
                            "gateway Play did not start the paused exact occurrence");
                        if (seek)
                        {
                            await Until(() => f.Player.Position.TotalSeconds >= timecode - .5);
                            f.Player.Pause();
                            var store = checkpoint ?? throw new InvalidOperationException("missing seek checkpoint fixture");
                            Assert(PlaybackOptionsField<double?>(w, "_restoredPlaybackSeconds") is null
                                && PlaybackOptionsInvoke(w, "SavePlaybackState") is true
                                && store.Load(checkpointUid)?.PositionSeconds >= timecode - .5,
                                "a save after applying the seek lost its observed duration");

                            using var reopened = new PlaybackOptionsFixture("reopened-native-seek",
                                apiHandler: new FakeHandler((request, _) => Task.FromResult(request.RequestUri!.AbsolutePath == "/get-file-info"
                                    ? PlaybackOptionsAudioInfo(2) : Json("{}"))),
                                bufferHandler: new FakeHandler((_, _) => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                                { Content = new ByteArrayContent(media) })));
                            await reopened.Rendered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                            PlaybackOptionsSet(reopened.Window, "_restore", true);
                            PlaybackOptionsSet(reopened.Window, "_account", new Account(checkpointUid, "Fixture", false));
                            PlaybackOptionsSet(reopened.Window, "_session", FakeSession);
                            PlaybackOptionsSet(reopened.Window, "_playbackStateStore", store);
                            PlaybackOptionsInvoke(reopened.Window, "RestorePlaybackState", checkpointUid);
                            var restoredQueue = PlaybackOptionsField<PlaybackQueue>(reopened.Window, "_playbackQueue");
                            Assert(!reopened.Player.HasSource && restoredQueue.CurrentIndex == 1
                                && restoredQueue.Current?.RequestId == next.RequestId
                                && PlaybackOptionsField<double?>(reopened.Window, "_restoredPlaybackSeconds") >= timecode - .5,
                                "reopening clamped the native timecode or selected another occurrence");
                            var restoredOccurrence = restoredQueue.CurrentOccurrenceToken;
                            await Call(reopened.Window, "TogglePlaybackAsync").WaitAsync(TimeSpan.FromSeconds(8));
                            await Until(() => reopened.Player.IsPlaying && reopened.Player.Position.TotalSeconds >= timecode - .5);
                            Assert(restoredQueue.CurrentOccurrenceToken == restoredOccurrence,
                                "explicit Play after reopening changed the restored occurrence");
                        }
                        await Dispatch(Windows.Media.SystemMediaTransportControlsButton.Stop);
                        Assert(!windows.HandlesMediaKeys && !f.Player.HasSource, "explicit Stop retained the media gateway");
                    }
                }
                finally { routing.TrySetResult(); release.TrySetResult(); }

            }
        });
        await Run("PlaybackOptions: late native failure releases the loopback and Play resolves a new descriptor at the same occurrence", async () =>
        {
            var media = await PlaybackOptionsMp3Async();
            var ranges = new ConcurrentBag<long>();
            var descriptors = 0;
            using var f = new PlaybackOptionsFixture("late-native-failure",
                apiHandler: new FakeHandler(RespondAsync),
                bufferHandler: new FakeHandler((request, _) => Task.FromResult(ProgressiveResponse(request, media, ranges))));
            await f.Rendered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            const string accountUid = "fixture-native-failure";
            var checkpoint = new PlaybackStateStore(Path.Combine(_directory, "native-failure-resume-checkpoint"));
            PlaybackOptionsSet(f.Window, "_session", FakeSession);
            PlaybackOptionsSet(f.Window, "_restore", true);
            PlaybackOptionsSet(f.Window, "_account", new Account(accountUid, "Fixture", false));
            PlaybackOptionsSet(f.Window, "_playbackStateAccountUid", accountUid);
            PlaybackOptionsSet(f.Window, "_playbackStateStore", checkpoint);
            const int catalogueDurationMs = 10000;
            var online = new TrackRow { Id = "8802", RequestId = "8802:91", Title = "Retry audio",
                DurationMs = catalogueDurationMs, Available = true };
            var queue = PlaybackOptionsField<PlaybackQueue>(f.Window, "_playbackQueue");
            queue.SetItemsAt([online, online], 1);
            var occurrence = queue.CurrentOccurrenceToken;
            await f.Window.PlayTrackAsync(online).WaitAsync(TimeSpan.FromSeconds(10));
            Assert(f.Player.IsPlaying && descriptors == 1, "fixture did not start the exact current stream");
            var failing = PlaybackOptionsField<object>(f.Player, "_current");
            var input = (PlaybackInput)failing.GetType().GetProperty("Input", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(failing)!;
            Assert(input.Progressive && input.Uri.IsLoopback, "fixture did not use a loopback source");
            PlaybackOptionsInvoke(f.Window, "CommitSeek", 20d);
            await Until(() => f.Player.Position.TotalSeconds >= 19.5);
            PlaybackOptionsInvoke(f.Window, "UpdateProgress");
            Assert(PlaybackOptionsField<double?>(f.Window, "_pendingSeek") is null,
                "fixture did not advance the native clock past the catalogue duration");
            var savedPosition = f.Player.Position.TotalSeconds;
            var nativeDuration = f.Player.Duration;
            PlaybackOptionsInvoke(f.Player, "OnMediaFailed", failing, new COMException("Inert decoder failure", unchecked((int)0x80004005)));
            Assert(!f.Player.HasSource && !f.Player.IsPlaying && queue.CurrentOccurrenceToken == occurrence
                && PlaybackOptionsField<long?>(f.Window, "_restoredPlaybackOccurrence") == occurrence
                && PlaybackOptionsField<double?>(f.Window, "_restoredPlaybackSeconds") >= savedPosition - .5
                && PlaybackOptionsField<TimeSpan?>(f.Window, "_restoredPlaybackNativeDuration") == nativeDuration && descriptors == 1,
                "failure left the dead input attached or lost the native position of the exact occurrence");
            var saved = checkpoint.Load(accountUid);
            Assert(saved is { CurrentIndex: 1 } && saved.PositionSeconds >= savedPosition - .5
                && saved.PositionDurationMs >= nativeDuration.TotalMilliseconds - 1
                && saved.Tracks.All(row => row.DurationMs == online.DurationMs),
                "the failure checkpoint clamped the position or changed the original track durations");
            await Until(() => !File.Exists(input.Path));
            await Call(f.Window, "TogglePlaybackAsync").WaitAsync(TimeSpan.FromSeconds(10));
            await Until(() => f.Player.IsPlaying && f.Player.Position.TotalSeconds >= savedPosition - 1);
            var retried = PlaybackOptionsField<object>(f.Player, "_current");
            Assert(descriptors == 2 && !ReferenceEquals(retried, failing) && queue.CurrentIndex == 1
                && queue.CurrentOccurrenceToken == occurrence && ReferenceEquals(queue.Current, online),
                "explicit Play reused the dead loopback or changed the selected duplicate");
            f.Player.Pause();

            using var reopened = new PlaybackOptionsFixture("reopened-native-failure",
                apiHandler: new FakeHandler(RespondAsync),
                bufferHandler: new FakeHandler((request, _) => Task.FromResult(ProgressiveResponse(request, media, new ConcurrentBag<long>()))));
            await reopened.Rendered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            PlaybackOptionsSet(reopened.Window, "_restore", true);
            PlaybackOptionsSet(reopened.Window, "_account", new Account(accountUid, "Fixture", false));
            PlaybackOptionsSet(reopened.Window, "_session", FakeSession);
            PlaybackOptionsSet(reopened.Window, "_playbackStateStore", checkpoint);
            PlaybackOptionsInvoke(reopened.Window, "RestorePlaybackState", accountUid);
            var restoredQueue = PlaybackOptionsField<PlaybackQueue>(reopened.Window, "_playbackQueue");
            Assert(restoredQueue.CurrentIndex == 1 && !reopened.Player.HasSource && restoredQueue.Current?.DurationMs == catalogueDurationMs
                && PlaybackOptionsField<double?>(reopened.Window, "_restoredPlaybackSeconds") >= savedPosition - 1,
                "reopening discarded the native bound or resumed before Play");
            var restoredOccurrence = restoredQueue.CurrentOccurrenceToken;
            await Call(reopened.Window, "TogglePlaybackAsync").WaitAsync(TimeSpan.FromSeconds(10));
            await Until(() => reopened.Player.IsPlaying && reopened.Player.Position.TotalSeconds >= savedPosition - 1);
            Assert(restoredQueue.CurrentOccurrenceToken == restoredOccurrence && descriptors == 3,
                "reopening changed the exact duplicate or did not resolve one new descriptor on Play");

            PlaybackOptionsSet(f.Window, "_observedPlaybackDuration", null);
            PlaybackOptionsInvoke(f.Player, "OnMediaFailed", retried, new COMException("Inert decoder failure without trusted duration"));
            Assert(queue.CurrentOccurrenceToken == occurrence
                && PlaybackOptionsField<double?>(f.Window, "_restoredPlaybackSeconds") == catalogueDurationMs / 1000d
                && PlaybackOptionsField<TimeSpan?>(f.Window, "_restoredPlaybackNativeDuration") is null,
                "failure without a confirmed duration bypassed the catalogue fallback");

            Task<HttpResponseMessage> RespondAsync(HttpRequestMessage request, CancellationToken _)
            {
                var path = request.RequestUri!.AbsolutePath;
                return Task.FromResult(path switch
                {
                    "/get-file-info" => PlaybackOptionsAudioInfo(Interlocked.Increment(ref descriptors)),
                    "/tracks/8802/lyrics" or "/tracks/8802/supplement" => LyricsUiYandex(request),
                    _ => throw new InvalidOperationException("unexpected API in native failure fixture: " + path)
                });
            }
        });
        await Run("PlaybackOptions: a minute deadline after adopting a Wave fallback preserves the first Play lifecycle", async () =>
        {
            var audio = await PlaybackOptionsMp3Async();
            foreach (var stop in new[] { false, true })
            {
                using var f = new TwFixture("adopted-fallback-timer-" + stop) { LongStartTrack = 801, LongStartAudio = audio };
                var w = f.Window;
                await w.PlayTrackAsync(TwSeed("1234", "Текущий трек", true));
                f.IndexAudio(["801"]);
                var library = PlaybackOptionsField<OfflineLibrary>(w, "_offline");
                var saved = library.Read().Single();
                ReplaceWithSameLengthSilentWave(library.ResolvePath(saved.FileName), saved);
                var q = PlaybackOptionsField<PlaybackQueue>(w, "_playbackQueue");
                var (windows, Dispatch) = PlaybackOptionsMediaGate(w);
                var expiredAfterAdoption = false;
                w.SleepElapsedForChecks = () =>
                {
                    if (q.Current?.Id != "801" || PlaybackOptionsField<TrackRow?>(w, "_current")?.Id != "801"
                        || !f.Player.HasSource || f.Player.IsPlaying) return TimeSpan.Zero;
                    expiredAfterAdoption = true;
                    return TimeSpan.FromMinutes(15);
                };
                w.SetSleepTimer(SleepTimerMode.Minutes15);
                await Call(w, "StartWaveViewAsync", true).WaitAsync(TimeSpan.FromSeconds(8));
                await PlaybackOptionsField<Task>(w, "_feedbackTail");
                var target = q.CurrentOccurrenceToken;
                Assert(expiredAfterAdoption && target is not null && q.Current?.Id == "801"
                    && !f.Player.HasSource && !f.Player.IsPlaying && windows.HandlesMediaKeys
                    && f.Feedback.All(e => e.Type != "trackStarted"),
                    "the sleep boundary left an uncommitted decoder or lost the paused Wave occurrence in SMTC");

                if (stop)
                {
                    await w.HandleDesktopMediaCommandAsync(DesktopMediaAction.Stop);
                    await Dispatch(Windows.Media.SystemMediaTransportControlsButton.Play);
                    Assert(!f.Player.HasSource && !windows.HandlesMediaKeys && q.CurrentOccurrenceToken == target,
                        "Stop left a decoder or gave Windows an obsolete Play intent");
                    continue;
                }
                await Dispatch(Windows.Media.SystemMediaTransportControlsButton.Play).WaitAsync(TimeSpan.FromSeconds(8));
                await PlaybackOptionsField<Task>(w, "_feedbackTail");
                var observed = PlaybackOptionsField<(long Occurrence, long Queue, int Playback, int Account, TimeSpan Duration)?>(w, "_observedPlaybackDuration");
                Assert(f.Player.IsPlaying && q.CurrentOccurrenceToken == target
                    && observed is { } measurement && measurement.Occurrence == target && measurement.Duration == f.Player.Duration
                    && !string.IsNullOrWhiteSpace(Control<TextBlock>(w, "QualityText").Text)
                    && f.Feedback.Count(e => e == ("trackStarted", "801")) == 1,
                    "explicit Play bypassed the first decoder commit, duration observation, or Wave start");
            }
        });

        await Run("PlaybackOptions: explicit Wave deadline before URL resolution retains system Play", async () =>
        {
            var audio = await PlaybackOptionsMp3Async();
            foreach (var action in new[] { "play", "stop", "replace", "logout" })
            {
                using var f = new TwFixture("ordinary-wave-sleep-" + action) { LongStartTrack = 801, LongStartAudio = audio };
                var w = f.Window;
                var old = TwSeed("1234", "Прежний трек", true);
                await w.PlayTrackAsync(old);
                var queue = PlaybackOptionsField<PlaybackQueue>(w, "_playbackQueue");
                var previous = queue.CurrentOccurrenceToken;
                var (windows, Dispatch) = PlaybackOptionsMediaGate(w);
                w.SetSleepTimer(SleepTimerMode.Minutes15);
                f.HoldLongStart = true;
                var start = Call(w, "StartWaveViewAsync", true);
                try
                {
                    await f.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    var target = queue.CurrentOccurrenceToken;
                    Assert(target is not null && target != previous && queue.Current?.Id == "801"
                        && !f.Player.HasSource && f.Feedback.All(e => e.Type != "trackStarted"),
                        "fixture did not hold an exact accepted Wave before creating the slot");
                    PlaybackOptionsInvoke(w, "ApplySleepTimerElapsed", TimeSpan.FromMinutes(15));
                    f.Release.TrySetResult();
                    await start.WaitAsync(TimeSpan.FromSeconds(8));
                    Assert(windows.HandlesMediaKeys && queue.CurrentOccurrenceToken == target && !f.Player.HasSource && !f.Player.IsPlaying,
                        "sleep before URL resolution lost the exact Windows Play cue");
                    if (action == "stop") await Dispatch(Windows.Media.SystemMediaTransportControlsButton.Stop);
                    if (action == "replace")
                    {
                        queue.SetItems([old], old);
                        PlaybackOptionsInvoke(w, "UpdateControls");
                    }
                    if (action == "logout") w.SignOut();
                    await Dispatch(Windows.Media.SystemMediaTransportControlsButton.Play).WaitAsync(TimeSpan.FromSeconds(8));
                    await PlaybackOptionsField<Task>(w, "_feedbackTail");
                    if (action == "play")
                        Assert(f.Player.IsPlaying && queue.CurrentOccurrenceToken == target && f.Player.Duration.TotalSeconds > 55
                            && !string.IsNullOrEmpty(Control<TextBlock>(w, "QualityText").Text)
                            && f.Feedback.Count(e => e == ("trackStarted", "801")) == 1,
                            "system Play did not perform one real first start at the paused Wave occurrence");
                    else
                        Assert(!windows.HandlesMediaKeys && !f.Player.HasSource
                            && f.Feedback.All(e => e != ("trackStarted", "801")), $"{action}: obsolete system Play revived the Wave");
                }
                finally { f.Release.TrySetResult(); }
            }
        });

        await Run("PlaybackOptions: ordinary Wave cleanup failures leave the exact occurrence retryable", async () =>
        {
            foreach (var (wave, prepared) in new[] { (true, false), (true, true), (false, false) })
            {
                using var f = new TwFixture($"ordinary-start-retirement-{wave}-{prepared}")
                {
                    StartAvailability = prepared ? id => id != 802 : null
                };
                var w = f.Window;
                await Call(w, "OpenTrackWaveAsync", TwSeed("1234", "Прежняя волна", true));
                await f.Ready("1234");
                SaInvoke(w, "CancelNextPreparation", true);
                var queue = PlaybackOptionsField<PlaybackQueue>(w, "_playbackQueue");
                var old = queue.Current!;
                var retired = new Uri("https://s1.vlx.yandex.net/track-wave-fixture.mp3");
                string? expectedLocal = null;
                if (prepared)
                {
                    var c = new TrackRow { Id = "5543", RequestId = "5543", Title = "Предыдущая предзагрузка", Available = true,
                        DurationMs = 30000, LocalPath = PlaybackOptionsSilentWave("ordinary-wave-prepared-retirement", 30) };
                    queue.SetItems([old, c, TwSeed("5544", "Хвост прежней очереди", true)], old);
                    SaInvoke(w, "RefreshNextPreparation");
                    await PreloadAwaitPrepared(w);
                    retired = PlaybackInput.FileUri(c.LocalPath!);
                    f.IndexAudio(["802"]);
                    var library = PlaybackOptionsField<OfflineLibrary>(w, "_offline");
                    expectedLocal = library.ResolvePath(library.Read().Single().FileName);
                }
                var faults = 0;
                f.Player.AfterDecoderCloseForChecks = source =>
                {
                    if (source != retired) return;
                    faults++;
                    throw new COMException("Synthetic cleanup failure replacing a normal source");
                };
                try
                {
                    var next = wave ? Call(w, "StartWaveViewAsync", true) : w.PlayTrackAsync(TwSeed("9999", "Обычный трек", true));
                    await next.WaitAsync(TimeSpan.FromSeconds(8));
                    await PlaybackOptionsField<Task>(w, "_feedbackTail");
                    var token = queue.CurrentOccurrenceToken;
                    var id = wave ? "802" : "9999";
                    var diag = PlaybackOptionsField<Diagnostics>(w, "_diagnostics");
                    Assert(faults == 1 && token is not null && queue.Current?.Id == id
                        && ReferenceEquals(queue.Current, PlaybackOptionsField<TrackRow?>(w, "_current"))
                        && !f.Player.HasSource && !f.Player.IsPlaying && !f.Player.HasPrepared
                        && !PlaybackOptionsField<bool>(w, "_loadingStream") && PlaybackOptionsField<object?>(w, "_playCts") is null
                        && diag.Stage == "playback_error" && diag.LastErrorType == nameof(COMException)
                        && f.Feedback.Count(item => item == ("skip", old.Id)) == 1 && f.Feedback.All(item => item != ("trackStarted", id)),
                        "ordinary startup did not retire its old slots, report the error, or complete the failed cue");
                    f.Player.AfterDecoderCloseForChecks = null;
                    await Call(w, "TogglePlaybackAsync").WaitAsync(TimeSpan.FromSeconds(8));
                    await PlaybackOptionsField<Task>(w, "_feedbackTail");
                    Assert(f.Player.IsPlaying && queue.CurrentOccurrenceToken == token
                        && (expectedLocal is null || f.Player.SourceLocalPath == expectedLocal)
                        && f.Feedback.Count(item => item == ("trackStarted", id)) == (wave ? 1 : 0),
                        "explicit retry lost the exact ordinary occurrence or duplicated Wave feedback");
                }
                finally { f.Player.AfterDecoderCloseForChecks = null; }
            }
        });

        await Run("PlaybackOptions: ordinary Wave first Play failure releases the provisional decoder", async () =>
        {
            foreach (var local in new[] { false, true })
            {
                using var f = new TwFixture("ordinary-first-play-failure-" + local) { StartAvailability = local ? id => id != 801 : null };
                var w = f.Window;
                await w.PlayTrackAsync(TwSeed("1234", "Прежняя очередь", true));
                if (local) f.IndexAudio(["801"]);
                var (windows, Dispatch) = PlaybackOptionsMediaGate(w);
                var faults = 0;
                f.Player.BeforeDecoderTransportForChecks = (_, play) =>
                {
                    if (play && ++faults == 1) throw new COMException("Synthetic first Play refusal");
                };
                try
                {
                    await Call(w, "StartWaveViewAsync", true).WaitAsync(TimeSpan.FromSeconds(8));
                    var q = PlaybackOptionsField<PlaybackQueue>(w, "_playbackQueue");
                    var target = q.CurrentOccurrenceToken;
                    Assert(faults == 1 && q.Current?.Id == "801" && !f.Player.HasSource && !f.Player.IsPlaying && windows.HandlesMediaKeys
                        && f.Feedback.All(item => item != ("trackStarted", "801")), "a failed first Play kept a decoder outside its startup lifecycle");
                    await Dispatch(Windows.Media.SystemMediaTransportControlsButton.Play).WaitAsync(TimeSpan.FromSeconds(8));
                    await PlaybackOptionsField<Task>(w, "_feedbackTail");
                    var observed = PlaybackOptionsField<(long Occurrence, long Queue, int Playback, int Account, TimeSpan Duration)?>(w, "_observedPlaybackDuration");
                    Assert(faults == 2 && f.Player.IsPlaying && q.CurrentOccurrenceToken == target
                        && observed is { } measurement && measurement.Occurrence == target && measurement.Duration == f.Player.Duration
                        && !string.IsNullOrEmpty(Control<TextBlock>(w, "QualityText").Text)
                        && f.Feedback.Count(item => item == ("trackStarted", "801")) == 1,
                        "explicit retry bypassed the normal first commit, duration observation, or Wave feedback");
                }
                finally { f.Player.BeforeDecoderTransportForChecks = null; }
            }
        });

        await Run("PlaybackOptions: system Play replaces a paused opener while the admitted account refresh drains", async () =>
        {
            var media = await PlaybackOptionsMp3Async();
            foreach (var action in new[] { "pause", "sleep", "stop", "replace", "occurrence", "logout" })
            {
                var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var rotations = 0;
                var descriptors = 0;
                var starts = 0;
                using var f = new PlaybackOptionsFixture("paused-opener-refresh-" + action,
                    apiHandler: new FakeHandler(RespondAsync),
                    bufferHandler: new FakeHandler((_, _) => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                        { Content = new ByteArrayContent(media) })));
                var w = f.Window;
                var expired = FakeSession with { ExpiresAtUtc = DateTimeOffset.UtcNow.AddSeconds(15) };
                var store = PlaybackOptionsField<SessionStore>(w, "_store");
                store.Save(expired);
                var stored = store.Load() ?? throw new InvalidOperationException("fixture did not store the original login");
                PlaybackOptionsSet(w, "_session", stored);
                PlaybackOptionsSet(w, "_ownedStoredSession", stored);
                PlaybackOptionsSet(w, "_account", new Account("12345", "Fixture", true));
                await f.Play();
                var api = PlaybackOptionsField<MusicApi>(w, "_api");
                var cached = await api.StartWaveAsync(stored, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
                var b = cached.Tracks.Single();
                if (action == "occurrence") cached = cached with { Tracks = [b, b] };
                SaInvoke(w, "AddWaveBatch", cached);
                var (windows, Dispatch) = PlaybackOptionsMediaGate(w);
                if (action == "sleep") w.SetSleepTimer(SleepTimerMode.Minutes15);
                var opening = Call(w, "StartWavePlaybackAsync", cached);
                try
                {
                    await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    var q = PlaybackOptionsField<PlaybackQueue>(w, "_playbackQueue");
                    var target = q.CurrentOccurrenceToken;
                    var old = PlaybackOptionsField<CancellationTokenSource>(w, "_playCts");
                    Assert(q.Current?.Id == b.Id && !f.Player.HasSource && descriptors == 0, "fixture did not hold URL resolution before the first Play");
                    if (action == "sleep") PlaybackOptionsInvoke(w, "ApplySleepTimerElapsed", TimeSpan.FromMinutes(15));
                    else await Dispatch(Windows.Media.SystemMediaTransportControlsButton.Pause);
                    Assert(old.IsCancellationRequested && windows.HandlesMediaKeys && PlaybackOptionsField<bool>(w, "_loadingStream"),
                        "paused fixture did not retain the exact cue while the account refresh drained");
                    var retry = Dispatch(Windows.Media.SystemMediaTransportControlsButton.Play);
                    Assert(!retry.IsCompleted && !ReferenceEquals(old, PlaybackOptionsField<CancellationTokenSource>(w, "_playCts"))
                        && q.CurrentOccurrenceToken == target && descriptors == 0, "early system Play did not replace the canceled cold open");
                    if (action == "stop") await Dispatch(Windows.Media.SystemMediaTransportControlsButton.Stop);
                    if (action == "replace") q.SetItems([f.First], f.First);
                    if (action == "occurrence") Assert(q.SelectAt(1), "fixture did not change to the other exact duplicate");
                    if (action == "logout") w.SignOut();
                    release.TrySetResult();
                    await Task.WhenAll(opening, retry).WaitAsync(TimeSpan.FromSeconds(8));
                    await PlaybackOptionsField<Task>(w, "_feedbackTail");
                    var shouldPlay = action is "pause" or "sleep";
                    Assert(rotations == 1 && descriptors == (shouldPlay ? 1 : 0) && starts == (shouldPlay ? 1 : 0)
                        && f.Player.IsPlaying == shouldPlay && (!shouldPlay || q.CurrentOccurrenceToken == target)
                        && (action == "logout" ? store.Load() is null : store.Load()?.AccessToken == "fixture-new-access"),
                        $"{action}: obsolete playback or rotation owner won, or the exact queued Play failed");
                }
                finally { release.TrySetResult(); }

                async Task<HttpResponseMessage> RespondAsync(HttpRequestMessage request, CancellationToken _)
                {
                    switch (request.RequestUri!.AbsolutePath)
                    {
                        case "/token":
                            rotations++;
                            entered.TrySetResult();
                            await release.Task;
                            return Json(OAuthRotatedTokens);
                        case "/get-file-info":
                            descriptors++;
                            Assert(request.Headers.Authorization?.Parameter == "fixture-new-access", "fixture resolved audio with the obsolete token");
                            return PlaybackOptionsAudioInfo(1);
                        case "/rotor/session/new":
                            return Json(TwBatch("cached-fixture", "first", 8980, count: 1));
                        case "/rotor/session/cached-fixture/tracks":
                            return Json(TwBatch("cached-fixture", "empty-tail", 8981, count: 1, available: static _ => false));
                        case "/rotor/session/cached-fixture/feedback/":
                            using (var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync()))
                                if (body.RootElement.GetProperty("event").GetProperty("type").GetString() == "trackStarted") starts++;
                            return Json("""{"result":{}}""");
                        default: throw new InvalidOperationException("unexpected fixture route");
                    }
                }
            }
        });

        await Run("PlaybackOptions: failed Wave fallback adoption reports the error for the exact cued occurrence", async () =>
        {
            foreach (var prepared in new[] { false, true }) await CheckAdoptionFailure(prepared);

            static async Task CheckAdoptionFailure(bool prepared)
            {
                var audio = await PlaybackOptionsMp3Async();
                using var f = new TwFixture("failed-adopted-wave-cue-" + prepared) { LongStartTrack = 802, LongStartAudio = audio };
                var w = f.Window;
                await Call(w, "OpenTrackWaveAsync", TwSeed("1234", "Прежняя волна", true));
                await f.Ready("1234");
                SaInvoke(w, "CancelNextPreparation", true);
                var retired = new Uri("https://s1.vlx.yandex.net/track-wave-fixture.mp3");
                if (prepared)
                {
                    var oldQueue = PlaybackOptionsField<PlaybackQueue>(w, "_playbackQueue");
                    var old = oldQueue.Current!;
                    var c = new TrackRow { Id = "5555", RequestId = "5555", Title = "Прежняя предзагрузка",
                        Available = true, DurationMs = 30000, LocalPath = PlaybackOptionsSilentWave("old-wave-prepared-retirement", 30) };
                    oldQueue.SetItems([old, c, TwSeed("5556", "Хвост прежней очереди", true)], old);
                    SaInvoke(w, "RefreshNextPreparation");
                    await PreloadAwaitPrepared(w);
                    retired = PlaybackInput.FileUri(c.LocalPath!);
                }
                f.HoldLongStart = true;
                f.IndexAudio(["802"]);
                var library = PlaybackOptionsField<OfflineLibrary>(w, "_offline");
                var saved = library.Read().Single();
                ReplaceWithSameLengthSilentWave(library.ResolvePath(saved.FileName), saved);
                var starting = Call(w, "StartWaveViewAsync", true);
                try
                {
                    await f.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    Assert(f.Player.HasPrepared == prepared && f.Player.IsPlaying, "fixture did not preserve the indicated prior native slot");
                    var failures = 0;
                    f.Player.AfterDecoderCloseForChecks = source =>
                    {
                        if (source != retired) return;
                        failures++;
                        throw new COMException("Failed to retire fixture's previous Wave source");
                    };
                    f.Release.TrySetResult();
                    await starting.WaitAsync(TimeSpan.FromSeconds(8));
                    await PlaybackOptionsField<Task>(w, "_feedbackTail");
                    var queue = PlaybackOptionsField<PlaybackQueue>(w, "_playbackQueue");
                    var token = queue.CurrentOccurrenceToken;
                    var failed = PlaybackOptionsField<Diagnostics>(w, "_diagnostics");
                    Assert(failures == 1 && !f.Player.HasSource && !f.Player.IsPlaying && queue.Current?.Id == "802"
                        && ReferenceEquals(queue.Current, PlaybackOptionsField<TrackRow?>(w, "_current"))
                        && PlaybackOptionsField<RadioBatch>(w, "_waveBatch").SessionId == "tw-2"
                        && failed.Stage == "playback_error" && failed.LastErrorType == nameof(COMException)
                        && f.Feedback.Count(item => item == ("skip", "801")) == 1
                        && f.Feedback.All(item => item != ("trackStarted", "802")), "failed adoption published mismatched owners or lost its error");
                    f.Player.AfterDecoderCloseForChecks = null;
                    await Call(w, "TogglePlaybackAsync").WaitAsync(TimeSpan.FromSeconds(8));
                    await Until(() => f.Feedback.Contains(("trackStarted", "802")));
                    Assert(f.Player.IsPlaying && queue.CurrentOccurrenceToken == token
                        && f.Feedback.Count(item => item == ("trackStarted", "802")) == 1,
                        "failed adoption did not leave its exact cue available for explicit Play");
                }
                finally { f.Release.TrySetResult(); f.Player.AfterDecoderCloseForChecks = null; }
            }
        });

        await Run("PlaybackOptions: a failed Wave fallback adoption releases the staged input", async () =>
        {
            var media = await PlaybackOptionsMp3Async();
            var ranges = new ConcurrentBag<long>();
            using var player = new NativePlayer(new AudioBuffer(Path.Combine(_directory, "failed-wave-adoption"),
                new FakeHandler((request, _) => Task.FromResult(ProgressiveResponse(request, media, ranges)))));
            player.Volume = 0;
            var first = PlaybackInput.FileUri(PlaybackOptionsSilentWave("adoption-fault-first", 30));
            var next = PlaybackInput.FileUri(PlaybackOptionsSilentWave("adoption-fault-next", 30));
            await player.OpenAsync(first, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(8));
            await player.PrepareNextAsync(next, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(8));
            Assert(player.TryPlayPrepared(next, TimeSpan.FromSeconds(10)) && player.FadingLocalPath is not null,
                "fixture did not open the previous transition");
            var descriptor = new AudioDescriptor(new Uri("https://s1.vlx.yandex.net/adoption-fault.mp3"), "mp3", 192);
            using var admission = await player.PrepareWaveFallbackAsync(descriptor, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(8));
            var slot = PlaybackOptionsField<object>(player, "_waveFallback");
            var input = (PlaybackInput)slot.GetType().GetProperty("Input", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(slot)!;
            Assert(admission.IsReady && input.Progressive && File.Exists(input.Path), "fixture did not own a staged loopback input");
            var fault = new COMException("Fixture transition cleanup failure");
            var thrown = 0;
            void FailCleanup(bool _) { thrown++; throw fault; }
            player.TransitionFinished += FailCleanup;
            try
            {
                try { admission.TryAdopt(); throw new InvalidOperationException("transition failed without reporting a cleanup exception"); }
                catch (COMException error) when (ReferenceEquals(error, fault)) { }
                Assert(thrown == 1 && !admission.IsReady && !player.HasSource
                    && PlaybackOptionsField<object?>(player, "_waveFallback") is null, "the failed adoption leaked its slot or replaced the source");
                await Until(() => !File.Exists(input.Path));
            }
            finally { player.TransitionFinished -= FailCleanup; }
        });

        await Run("PlaybackOptions: a minute deadline reached during cold routing blocks Play and trackStarted at the boundary", async () =>
        {
            var media = await PlaybackOptionsMp3Async();
            foreach (var (automatic, expired) in new[] { (true, true), (false, true), (true, false) })
            {
                var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var starts = 0;
                using var f = new PlaybackOptionsFixture($"cold-sleep-{automatic}-{expired}",
                    apiHandler: new FakeHandler(async (request, token) =>
                    {
                        switch (request.RequestUri!.AbsolutePath)
                        {
                            case "/rotor/session/new": return Json(TwBatch("fixture-deadline-station", "first", 8803));
                            case "/get-file-info": return PlaybackOptionsAudioInfo(1);
                            case "/rotor/session/fixture-deadline-station/feedback/":
                                using (var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token)))
                                    if (body.RootElement.GetProperty("event").GetProperty("type").GetString() == "trackStarted")
                                        Interlocked.Increment(ref starts);
                                return Json("{}");
                            default: throw new InvalidOperationException("unexpected API in cold deadline fixture");
                        }
                    }),
                    bufferHandler: new FakeHandler(async (_, token) =>
                    {
                        entered.TrySetResult();
                        await release.Task.WaitAsync(token);
                        return new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new ByteArrayContent(media) };
                    }));
                PlaybackOptionsSet(f.Window, "_session", FakeSession);
                var api = PlaybackOptionsField<MusicApi>(f.Window, "_api");
                var station = await api.StartWaveAsync(FakeSession, CancellationToken.None);
                var online = station.Tracks[0];
                SaInvoke(f.Window, "RememberWaveSource", online, station);
                var queue = PlaybackOptionsField<PlaybackQueue>(f.Window, "_playbackQueue");
                queue.SetItemsAt([f.First, online], 0);
                var occurrence = queue.PeekNextOccurrenceToken(automatic);
                var elapsed = TimeSpan.FromMinutes(14);
                f.Window.SleepElapsedForChecks = () => elapsed;
                f.Window.SetSleepTimer(SleepTimerMode.Minutes15);
                PlaybackOptionsField<DispatcherTimer>(f.Window, "_sleepTick").Stop();
                var native = Call(f.Window, "AdvanceAsync", 1, automatic);
                try
                {
                    await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    var output = new PlaybackOptionsFakeRoute("fake-A");
                    f.Outputs.HeldRoute = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    PlaybackOptionsField<ComboBox>(f.Window, "_audioOutputCombo").SelectedValue = output.Id;
                    var change = PlaybackOptionsField<Task>(f.Window, "_audioOutputReady");
                    release.TrySetResult();
                    await Until(() => f.Player.HasSource && f.Player.Duration.TotalSeconds > 30);
                    await f.Window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                    Assert(!native.IsCompleted && !f.Player.IsPlaying && !online.IsPlaying && queue.CurrentOccurrenceToken == occurrence,
                        "fixture did not hold the selected cold decoder before Play");

                    if (expired) elapsed = TimeSpan.FromMinutes(15);
                    f.Outputs.HeldRoute.SetResult(output);
                    await Task.WhenAll(change, native).WaitAsync(TimeSpan.FromSeconds(8));
                    await PlaybackOptionsField<Task>(f.Window, "_feedbackTail").WaitAsync(TimeSpan.FromSeconds(5));
                    Assert(queue.CurrentOccurrenceToken == occurrence && ReferenceEquals(queue.Current, online)
                        && ReferenceEquals(PlaybackOptionsField<TrackRow>(f.Window, "_current"), online),
                        "deadline changed the selected occurrence");
                    Assert(f.Player.IsPlaying == !expired && f.Player.HasSource == !expired && online.IsPlaying == !expired
                        && starts == (expired ? 0 : 1) && PlaybackOptionsField<SleepTimerState>(f.Window, "_sleepTimer").Active == !expired,
                        "cold open played, retained its decoder or published trackStarted despite the elapsed deadline");
                    if (expired) Assert(Control<TextBlock>(f.Window, "StatusText").Text.Contains("Таймер сна", StringComparison.Ordinal),
                        "cold deadline did not use the sleep completion path");
                }
                finally { release.TrySetResult(); }
            }
        });
        await Run("PlaybackOptions: a prepared target under applied routing starts its lifecycle only on real Play", async () =>
        {
            foreach (var action in new[] { "ready", "pause", "failure" })
            {
                using var f = new PlaybackOptionsFixture("prepared-applied-route-" + action);
                var w = f.Window;
                var q = PlaybackOptionsField<PlaybackQueue>(w, "_playbackQueue");
                var next = new TrackRow { Id = "applied-route-next", RequestId = "applied-route-next", Title = "Prepared successor",
                    Available = true, DurationMs = 30000, LocalPath = PlaybackOptionsSilentWave("applied-route-next-" + action, 30) };
                q.SetItems([f.First, next]);
                await f.Play();
                await f.Rendered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await PreloadAwaitPrepared(w);
                SaInvoke(w, "RememberWaveSource", next, new RadioBatch("prepared-applied", "one", [next]));
                var (windows, Dispatch) = PlaybackOptionsMediaGate(w);
                var target = q.PeekNextOccurrenceToken();
                var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var releaseDecoder = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var releaseList = new TaskCompletionSource<IReadOnlyList<AudioOutputDevice>>(TaskCreationOptions.RunContinuationsAsynchronously);
                f.Outputs.HeldList = releaseList;
                f.Player.BeforeRouteDecoderInstallForChecks = token =>
                {
                    entered.TrySetResult();
                    return releaseDecoder.Task.WaitAsync(token);
                };
                var routing = (Task)PlaybackOptionsInvoke(w, "ChangeAudioOutputAsync", "fake-A", false)!;
                try
                {
                    await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    Assert(f.Player.OutputRouteChanging && f.Player.IsPlaying && f.Player.HasPrepared,
                        "fixture held route resolution instead of decoder installation");
                    Assert(!f.Player.TryPlayPrepared(new Uri(next.LocalPath!), TimeSpan.FromMilliseconds(200)),
                        "native promotion reported success without a Play during routing");
                    var command = Call(w, "AdvanceAsync", 1, false);
                    Assert(q.CurrentOccurrenceToken == target && !f.Player.IsPlaying && !routing.IsCompleted
                        && PlaybackOptionsField<object?>(w, "_waveListening") is null
                        && System.Windows.Automation.AutomationProperties.GetName(Control<Button>(w, "PlayPauseButton")) == "Пауза",
                        "manual Next lost its exact target or published should-be-unplayed lifecycle");
                    if (action == "pause") await w.HandleDesktopMediaCommandAsync(DesktopMediaAction.Pause);
                    releaseDecoder.TrySetResult();
                    releaseList.TrySetResult(action == "failure" ? [] : [new("fake-A", "Fixture headphones")]);
                    await Task.WhenAll(command, routing).WaitAsync(TimeSpan.FromSeconds(8));
                    Assert(ReferenceEquals(q.Current, next) && q.CurrentOccurrenceToken == target
                        && f.Player.IsPlaying == (action == "ready")
                        && (PlaybackOptionsField<object?>(w, "_waveListening") is not null) == (action == "ready"),
                        "route readiness published a false start or displaced the manually accepted occurrence");
                    if (action == "failure")
                    {
                        Assert(windows.HandlesMediaKeys && !f.Player.HasSource && f.Player.OutputRouteIsValid,
                            "rollback cleared the exact paused SMTC occurrence");
                        var retryRoute = new TaskCompletionSource<IAudioOutputRoute>(TaskCreationOptions.RunContinuationsAsynchronously);
                        f.Outputs.HeldRoute = retryRoute;
                        var retryList = new TaskCompletionSource<IReadOnlyList<AudioOutputDevice>>(TaskCreationOptions.RunContinuationsAsynchronously);
                        f.Outputs.HeldList = retryList;
                        var retry = Dispatch(Windows.Media.SystemMediaTransportControlsButton.Play);
                        try
                        {
                            Assert(!retry.IsCompleted && !f.Player.IsPlaying && q.CurrentOccurrenceToken == target,
                                "the SMTC retry did not enter a new cold open on the same occurrence");
                            var retrySwitch = (Task)PlaybackOptionsInvoke(w, "ChangeAudioOutputAsync", "fake-A", false)!;
                            retryRoute.TrySetResult(new PlaybackOptionsFakeRoute("fake-A"));
                            retryList.TrySetResult([]);
                            await Task.WhenAll(retry, retrySwitch).WaitAsync(TimeSpan.FromSeconds(8));
                            Assert(windows.HandlesMediaKeys && !f.Player.HasSource && q.CurrentOccurrenceToken == target
                                && PlaybackOptionsField<object?>(w, "_waveListening") is null,
                                "a second route failure discarded the same resumable occurrence or started its lifecycle");
                        }
                        finally { retryRoute.TrySetResult(new PlaybackOptionsFakeRoute("fake-A")); retryList.TrySetResult([]); }
                        await Dispatch(Windows.Media.SystemMediaTransportControlsButton.Play).WaitAsync(TimeSpan.FromSeconds(8));
                        Assert(f.Player.IsPlaying && q.CurrentOccurrenceToken == target
                            && PlaybackOptionsField<object?>(w, "_waveListening") is not null, "SMTC Play did not resume on the recovered route");
                    }
                }
                finally { releaseDecoder.TrySetResult(); releaseList.TrySetResult([]); }
            }
        });

        await Run("PlaybackOptions: Previous and a failed SMTC retry retain the exact cold occurrence after output loss", async () =>
        {
            var media = await PlaybackOptionsMp3Async();
            var failing = true;
            var modernRequests = 0;
            var legacyRequests = 0;
            using var f = new PlaybackOptionsFixture("previous-output-loss-retry",
                apiHandler: new FakeHandler((request, _) => Task.FromResult(Respond(request))),
                bufferHandler: new FakeHandler((_, _) => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                    { Content = new ByteArrayContent(media) })));
            var w = f.Window;
            await f.Rendered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            PlaybackOptionsSet(w, "_session", FakeSession);
            var library = PlaybackOptionsField<OfflineLibrary>(w, "_offline");
            Directory.CreateDirectory(library.DirectoryPath);
            var path = Path.Combine(library.DirectoryPath, "previous.mp3");
            File.Copy(PlaybackOptionsSilentWave("previous-output-admission", 30), path);
            var previous = new TrackRow { Id = "8801", RequestId = "8801:91", Title = "Indexed predecessor",
                DurationMs = 30000, LocalPath = path };
            WriteFixtureAudioIndex(library.DirectoryPath, [previous]);
            var next = new TrackRow { Id = "8802", RequestId = "8802", Title = "Unplayed occurrence",
                DurationMs = 30000, Available = true };
            await ReadyPlaybackOutput(w);
            await w.PlayTrackAsync(previous).WaitAsync(TimeSpan.FromSeconds(8));
            await (Task)PlaybackOptionsInvoke(w, "ChangeAudioOutputAsync", "fake-A", false)!;
            Assert(f.Player.IsPlaying && f.Player.OutputDeviceId == "fake-A", "fixture did not start on the preferred output");

            var (windows, Dispatch) = PlaybackOptionsMediaGate(w);
            var queue = PlaybackOptionsField<PlaybackQueue>(w, "_playbackQueue");
            queue.SetItems([previous, next], previous);
            var first = queue.CurrentOccurrenceToken;
            var target = queue.OccurrenceTokenAt(1);
            var routing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var entering = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            PlaybackOptionsInvoke(w, "CancelNextPreparation", true);
            PlaybackOptionsSet(w, "_audioOutputReady", routing.Task);
            w.QueuePlaybackBeforeVerificationForChecks = token =>
            {
                entering.TrySetResult();
                return release.Task.WaitAsync(token);
            };
            try
            {
                PlaybackOptionsInvoke(f.Player, "OnMediaEnded", PlaybackOptionsField<object>(f.Player, "_current"));
                Assert(queue.CurrentOccurrenceToken == target && !f.Player.HasSource, "fixture did not hold the successor before open");
                var goingBack = Dispatch(Windows.Media.SystemMediaTransportControlsButton.Previous);
                await entering.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert(queue.CurrentOccurrenceToken == target && queue.PeekPreviousOccurrenceToken() == first
                    && windows.HandlesMediaKeys && !f.Player.IsPlaying, "Previous consumed the exact predecessor or lost the pending transport");

                f.Outputs.Available = false;
                await (Task)PlaybackOptionsInvoke(w, "RefreshAudioOutputsAsync")!;
                await goingBack.WaitAsync(TimeSpan.FromSeconds(5));
                routing.TrySetResult();
                release.TrySetResult();
                Assert(queue.CurrentOccurrenceToken == target && queue.PeekPreviousOccurrenceToken() == first
                    && windows.HandlesMediaKeys && !f.Player.HasSource, "output loss displaced the exact unplayed occurrence");
                f.Outputs.Available = true;
                await (Task)PlaybackOptionsInvoke(w, "RefreshAudioOutputsAsync")!;
                PlaybackOptionsSet(w, "_restoredPlaybackOccurrence", target);
                PlaybackOptionsSet(w, "_restoredPlaybackSeconds", 7.5d);

                await Dispatch(Windows.Media.SystemMediaTransportControlsButton.Play).WaitAsync(TimeSpan.FromSeconds(8));
                Assert(modernRequests == 1 && legacyRequests == 1 && queue.CurrentOccurrenceToken == target
                    && windows.HandlesMediaKeys && !f.Player.HasSource
                    && PlaybackOptionsField<double?>(w, "_restoredPlaybackSeconds") == 7.5d,
                    "descriptor failure discarded the SMTC card, exact occurrence, or saved position");

                failing = false;
                await Dispatch(Windows.Media.SystemMediaTransportControlsButton.Play).WaitAsync(TimeSpan.FromSeconds(8));
                await Until(() => f.Player.IsPlaying && f.Player.Position.TotalSeconds >= 7);
                Assert(queue.CurrentOccurrenceToken == target && queue.PeekPreviousOccurrenceToken() == first
                    && modernRequests == 2 && legacyRequests == 1, "the final Play did not open the same occurrence exactly once");
                await Dispatch(Windows.Media.SystemMediaTransportControlsButton.Stop);
                Assert(!windows.HandlesMediaKeys && !f.Player.HasSource && queue.CurrentOccurrenceToken == target
                    && PlaybackOptionsField<double?>(w, "_restoredPlaybackSeconds") is null, "Stop did not withdraw the retained cue");
            }
            finally { routing.TrySetResult(); release.TrySetResult(); }

            HttpResponseMessage Respond(HttpRequestMessage request)
            {
                switch (request.RequestUri!.AbsolutePath)
                {
                    case "/get-file-info":
                        modernRequests++;
                        return failing ? Json("{}", System.Net.HttpStatusCode.ServiceUnavailable) : PlaybackOptionsAudioInfo(1);
                    case "/tracks/8802/download-info":
                        legacyRequests++;
                        return Json("{}", System.Net.HttpStatusCode.ServiceUnavailable);
                    default: throw new InvalidOperationException("unexpected API in route retry fixture");
                }
            }
        });

        await Run("PlaybackOptions: a lost preferred output keeps the unplayed occurrence in SMTC until reconnect", async () =>
        {
            using var f = new PlaybackOptionsFixture("cold-output-loss-resume");
            var w = f.Window;
            var q = PlaybackOptionsField<PlaybackQueue>(w, "_playbackQueue");
            var next = new TrackRow { Id = "lost-out-next", RequestId = "lost-out-next", Title = "Waiting successor",
                Available = true, DurationMs = 30000, LocalPath = PlaybackOptionsSilentWave("lost-out-next", 30) };
            await f.Play();
            await f.Rendered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await (Task)PlaybackOptionsInvoke(w, "ChangeAudioOutputAsync", "fake-A", false)!;
            Assert(f.Player.OutputDeviceId == "fake-A", "fixture did not activate the fake endpoint");
            var (windows, Dispatch) = PlaybackOptionsMediaGate(w);
            SaInvoke(w, "RememberWaveSource", next, new RadioBatch("loss-resume", "first", [next]));
            q.SetItems([f.First, next]);
            var target = q.OccurrenceTokenAt(1);
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            PlaybackOptionsInvoke(w, "CancelNextPreparation", true);
            PlaybackOptionsSet(w, "_audioOutputReady", ready.Task);
            try
            {
                PlaybackOptionsInvoke(f.Player, "OnMediaEnded", PlaybackOptionsField<object>(f.Player, "_current"));
                Assert(q.CurrentOccurrenceToken == target && !f.Player.HasSource && windows.HandlesMediaKeys,
                    "fixture did not hold the accepted successor before opening");
                f.Outputs.Available = false;
                await (Task)PlaybackOptionsInvoke(w, "RefreshAudioOutputsAsync")!;
                await Until(() => !PlaybackOptionsField<bool>(w, "_loadingStream"));
                Assert(windows.HandlesMediaKeys && q.CurrentOccurrenceToken == target && !f.Player.IsPlaying
                    && PlaybackOptionsField<object?>(w, "_waveListening") is null, "device loss cleared or started the pending successor");
                ready.TrySetResult();
                await Dispatch(Windows.Media.SystemMediaTransportControlsButton.Play);
                Assert(!f.Player.HasSource && !f.Player.IsPlaying, "SMTC Play bypassed the missing route");
                f.Outputs.Available = true;
                await (Task)PlaybackOptionsInvoke(w, "RefreshAudioOutputsAsync")!;
                Assert(windows.HandlesMediaKeys && !f.Player.IsPlaying, "reconnection resumed music or removed SMTC Play");
                await Dispatch(Windows.Media.SystemMediaTransportControlsButton.Play).WaitAsync(TimeSpan.FromSeconds(8));
                Assert(f.Player.IsPlaying && ReferenceEquals(q.Current, next) && q.CurrentOccurrenceToken == target
                    && PlaybackOptionsField<object?>(w, "_waveListening") is not null, "reconnected SMTC Play changed the selected occurrence");
            }
            finally { ready.TrySetResult(); }
        });

        await Run("QaFixes061: prepared Next pauses during pending output readiness and route release cannot revive playback", async () =>
        {
            using var f = new PlaybackOptionsFixture("qa-promoted-pause");
            var next = new TrackRow { Id = "qa-promoted-next", RequestId = "qa-promoted-next", Title = "Silent prepared next", Available = true,
                DurationMs = 30000, LocalPath = PlaybackOptionsSilentWave("qa-promoted-next", 30), LocalQuality = "QA prepared PCM B" };
            var queue = PlaybackOptionsField<PlaybackQueue>(f.Window, "_playbackQueue");
            queue.SetItems([f.First, next], f.First);
            var output = PlaybackOptionsField<ComboBox>(f.Window, "_audioOutputCombo");
            f.Outputs.HeldRoute = new(TaskCreationOptions.RunContinuationsAsynchronously);
            output.SelectedValue = "fake-A";
            var initialRoute = PlaybackOptionsField<Task>(f.Window, "_audioOutputReady");
            var initialPlay = f.Window.PlayTrackAsync(f.First);
            Assert(PlaybackOptionsField<bool>(f.Window, "_loadingStream") && !f.Player.IsPlaying && !f.Player.HasSource,
                "fixture did not hold an unopened source behind output readiness");
            await Call(f.Window, "TogglePlaybackAsync");
            Assert(!f.Player.IsPlaying && !f.Player.HasSource, "loading Toggle started an unopened source before output readiness");
            f.Outputs.HeldRoute.SetResult(new PlaybackOptionsFakeRoute("fake-A"));
            await initialRoute; await initialPlay; await PreloadAwaitPrepared(f.Window);
            await f.Rendered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            // OnReady has finished with restore:false. Enable persistence only
            // against this explicit fixture store; no updater/login startup runs.
            var state = new PlaybackStateStore(Path.Combine(_directory, "playback-options-qa-promoted-state"));
            PlaybackOptionsSet(f.Window, "_restore", true);
            PlaybackOptionsSet(f.Window, "_account", new Account("qa-promoted-account", "Fixture", false));
            PlaybackOptionsSet(f.Window, "_playbackStateAccountUid", "qa-promoted-account");
            PlaybackOptionsSet(f.Window, "_playbackStateStore", state);
            SaInvoke(f.Window, "RememberWaveSource", next, new RadioBatch("qa-wave-session", "qa-wave-batch", [next]));
            var prepared = PlaybackOptionsField<object>(f.Player, "_prepared");
            var occurrence = queue.PeekNextOccurrenceToken(true);
            Assert(f.Player.IsPlaying && f.Player.HasPrepared, "fixture did not prepare a playing queue");

            f.Outputs.HeldRoute = new(TaskCreationOptions.RunContinuationsAsynchronously);
            output.SelectedValue = "";
            var switching = PlaybackOptionsField<Task>(f.Window, "_audioOutputReady");
            var advance = Call(f.Window, "AdvanceAsync", 1, false);
            Assert(!advance.IsCompleted && !switching.IsCompleted && PlaybackOptionsField<bool>(f.Window, "_loadingStream")
                && f.Player.IsPlaying && ReferenceEquals(PlaybackOptionsField<object>(f.Player, "_current"), prepared)
                && Control<Button>(f.Window, "PlayPauseButton").IsEnabled
                && ReferenceEquals(queue.Current, next) && queue.CurrentOccurrenceToken == occurrence,
                "fixture did not promote the exact prepared occurrence before held readiness");
            var committedListening = PlaybackOptionsField<object?>(f.Window, "_waveListening");
            Assert(committedListening is not null && Control<TextBlock>(f.Window, "QualityText").Text == "Офлайн · QA prepared PCM B"
                && state.Load("qa-promoted-account")?.CurrentIndex == 1,
                "live promotion did not commit B metadata/lifecycle before routing");
            f.Window.SetSleepTimer(SleepTimerMode.AfterCurrentTrack);
            await Call(f.Window, "TogglePlaybackAsync");
            Assert(!f.Player.IsPlaying, "Pause ignored the already playing promoted source during readiness");
            Assert(!switching.IsCompleted && f.Player.SourceLocalPath == next.LocalPath
                && PlaybackOptionsField<Diagnostics>(f.Window, "_diagnostics").Stage == "paused",
                "Pause changed source ownership or failed to report the paused state");
            f.Outputs.HeldRoute.SetResult(new PlaybackOptionsFakeRoute(""));
            await switching; await advance.WaitAsync(TimeSpan.FromSeconds(5));
            var listening = PlaybackOptionsField<object?>(f.Window, "_waveListening");
            Assert(listening is not null, "paused finalization skipped B listening lifecycle");
            var listeningTrack = (TrackRow)listening!.GetType().GetProperty("Track")!.GetValue(listening)!;
            var listeningClock = (System.Diagnostics.Stopwatch)listening.GetType().GetProperty("Clock")!.GetValue(listening)!;
            var saved = state.Load("qa-promoted-account");
            Assert(!f.Player.IsPlaying && ReferenceEquals(queue.Current, next) && queue.CurrentOccurrenceToken == occurrence
                && PlaybackOptionsField<Diagnostics>(f.Window, "_diagnostics").Stage == "paused"
                && !PlaybackOptionsField<System.Windows.Threading.DispatcherTimer>(f.Window, "_progressTimer").IsEnabled
                && Equals(Control<Button>(f.Window, "PlayPauseButton").Content, "\uE768"),
                "late readiness resumed playback or overwrote paused UI/queue ownership");
            Assert(Control<TextBlock>(f.Window, "QualityText").Text == "Офлайн · QA prepared PCM B" && next.IsPlaying
                && ReferenceEquals(listening, committedListening) && ReferenceEquals(listeningTrack, next) && !listeningClock.IsRunning
                && PlaybackOptionsField<SleepTimerState>(f.Window, "_sleepTimer").AfterTrack
                && ReferenceEquals(PlaybackOptionsField<TrackRow>(f.Window, "_sleepTrack"), next)
                && PlaybackOptionsField<long?>(f.Window, "_sleepTrackOccurrence") == occurrence
                && saved is not null && saved.CurrentIndex == 1 && saved.Tracks[saved.CurrentIndex].Id == next.Id,
                "paused finalization skipped B quality, listening, sleep context or saved occurrence");
            await Call(f.Window, "TogglePlaybackAsync");
            await Until(() => listeningClock.IsRunning);
            Assert(f.Player.IsPlaying && f.Player.SourceLocalPath == next.LocalPath && File.Exists(f.First.LocalPath!)
                && ReferenceEquals(PlaybackOptionsField<object>(f.Window, "_waveListening"), listening) && listeningClock.IsRunning
                && Control<TextBlock>(f.Window, "QualityText").Text == "Офлайн · QA prepared PCM B",
                "new explicit resume lost B metadata/lifecycle or an offline file");
        });
        await Run("QaFixes061: promoted short track completion owns the next occurrence before held output readiness", async () =>
        {
            using var f = new PlaybackOptionsFixture("promoted-short-ended");
            var shortTrack = new TrackRow { Id = "promoted-short-b", RequestId = "promoted-short-b", Title = "Prepared short track",
                Available = true, DurationMs = 1000, LocalPath = PlaybackOptionsSilentWave("promoted-short-b", 1) };
            var last = new TrackRow { Id = "promoted-short-c", RequestId = "promoted-short-c", Title = "Following track",
                Available = true, DurationMs = 30000, LocalPath = PlaybackOptionsSilentWave("promoted-short-c", 30) };
            var queue = PlaybackOptionsField<PlaybackQueue>(f.Window, "_playbackQueue");
            queue.SetItems([f.First, shortTrack, last], f.First);
            await f.Play();
            await PreloadAwaitPrepared(f.Window);
            await f.Rendered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var shortOccurrence = queue.PeekNextOccurrenceToken(true);
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            PlaybackOptionsSet(f.Window, "_audioOutputReady", gate.Task);
            var promotion = Call(f.Window, "AdvanceAsync", 1, false);
            Assert(!promotion.IsCompleted && !gate.Task.IsCompleted && f.Player.IsPlaying
                && queue.CurrentOccurrenceToken == shortOccurrence && f.Player.SourceLocalPath == shortTrack.LocalPath,
                "fixture did not promote the short occurrence while output readiness was held");
            var lastOccurrence = queue.PeekNextOccurrenceToken(true);

            await Until(() => ReferenceEquals(queue.Current, last));
            Assert(!gate.Task.IsCompleted && queue.CurrentOccurrenceToken == lastOccurrence && !f.Player.HasSource
                && queue.History.SequenceEqual([f.First, shortTrack]),
                "the short track completion was dropped or the following occurrence opened before readiness");
            gate.TrySetResult();
            await promotion.WaitAsync(TimeSpan.FromSeconds(5));
            await Until(() => f.Player.IsPlaying && f.Player.SourceLocalPath == last.LocalPath
                && !PlaybackOptionsField<bool>(f.Window, "_loadingStream"));
            Assert(queue.CurrentOccurrenceToken == lastOccurrence && ReferenceEquals(queue.Current, last),
                "the old promotion changed the occurrence after output became ready");
        });

        await Run("QaFixes061: sleep and output pauses preserve promoted lifecycle while unopened and preview sources stay aborted", async () =>
        {
            foreach (var cause in new[] { "sleep", "removal", "failure", "expired-before-next" })
                foreach (var promoted in new[] { true, false }) await CheckPause(cause, promoted);

            async Task CheckPause(string cause, bool promoted)
            {
                var name = "qa-pause-" + cause + "-" + promoted;
                using var f = new PlaybackOptionsFixture(name);
                var next = new TrackRow { Id = name, RequestId = name, Title = "Silent matrix B", Available = true,
                    DurationMs = 30000, LocalPath = PlaybackOptionsSilentWave(name, 30), LocalQuality = "QA matrix PCM B" };
                var queue = PlaybackOptionsField<PlaybackQueue>(f.Window, "_playbackQueue");
                queue.SetItems([f.First, next], f.First);
                await f.Play(); await PreloadAwaitPrepared(f.Window); await f.Rendered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await (Task)PlaybackOptionsInvoke(f.Window, "ChangeAudioOutputAsync", "fake-A", false)!;
                var state = new PlaybackStateStore(Path.Combine(_directory, name + "-state"));
                PlaybackOptionsSet(f.Window, "_restore", true);
                PlaybackOptionsSet(f.Window, "_account", new Account("qa-pause-account", "Fixture", false));
                PlaybackOptionsSet(f.Window, "_playbackStateAccountUid", "qa-pause-account");
                PlaybackOptionsSet(f.Window, "_playbackStateStore", state);
                SaInvoke(f.Window, "RememberWaveSource", next, new RadioBatch("qa-wave-session", "qa-wave-batch", [next]));
                var occurrence = queue.PeekNextOccurrenceToken(true);
                f.Outputs.HeldRoute = new(TaskCreationOptions.RunContinuationsAsynchronously);
                PlaybackOptionsField<ComboBox>(f.Window, "_audioOutputCombo").SelectedValue = "";
                var routing = PlaybackOptionsField<Task>(f.Window, "_audioOutputReady");
                var expiredRead = false;
                var lifecycleAtExpiry = false;
                if (cause == "expired-before-next")
                {
                    PlaybackOptionsSet(f.Window, "_section", "queue");
                    PlaybackOptionsInvoke(f.Window, "RefreshPlaybackView");
                    f.Window.SetSleepTimer(SleepTimerMode.Minutes15);
                    // No await until Next: the first expired Tick must come from
                    // synchronous selection/UpdateControls, not a dispatcher tick.
                    f.Window.SleepElapsedForChecks = () =>
                    {
                        expiredRead = true;
                        lifecycleAtExpiry = ReferenceEquals(PlaybackOptionsField<TrackRow>(f.Window, "_current"), next)
                            && (promoted
                                ? Control<TextBlock>(f.Window, "QualityText").Text == "Офлайн · QA matrix PCM B"
                                    && PlaybackOptionsField<object?>(f.Window, "_waveListening") is not null
                                : !f.Player.HasSource && !next.IsPlaying);
                        return TimeSpan.FromMinutes(15);
                    };
                }
                var pending = promoted ? Call(f.Window, "AdvanceAsync", 1, false) : f.Window.PlayTrackAsync(next);
                if (cause == "expired-before-next")
                    Assert(expiredRead && lifecycleAtExpiry && !f.Player.IsPlaying && !routing.IsCompleted,
                        name + ": synchronous expired Tick ran before owned lifecycle or did not pause");
                else
                    Assert(!pending.IsCompleted && !routing.IsCompleted && PlaybackOptionsField<bool>(f.Window, "_loadingStream")
                        && f.Player.IsPlaying == promoted && f.Player.HasSource == promoted,
                        name + ": fixture did not hold the intended live/unopened source");
                var listening = PlaybackOptionsField<object?>(f.Window, "_waveListening");
                if (cause == "sleep")
                {
                    f.Window.SetSleepTimer(SleepTimerMode.Minutes15);
                    PlaybackOptionsInvoke(f.Window, "ApplySleepTimerElapsed", TimeSpan.FromMinutes(15));
                }
                else if (cause == "removal")
                {
                    f.Outputs.Available = false;
                    await (Task)PlaybackOptionsInvoke(f.Window, "RefreshAudioOutputsAsync")!;
                }
                else if (cause == "failure")
                {
                    f.Outputs.HeldRoute.SetException(new MusicException("Fixture held route failure"));
                    await routing;
                }
                await pending.WaitAsync(TimeSpan.FromSeconds(5));
                if (cause != "failure")
                {
                    f.Outputs.HeldRoute.SetResult(new PlaybackOptionsFakeRoute(""));
                    await routing;
                }
                Assert(!f.Player.IsPlaying && !PlaybackOptionsField<bool>(f.Window, "_loadingStream")
                    && ReferenceEquals(queue.Current, next) && queue.CurrentOccurrenceToken == occurrence
                    && PlaybackOptionsField<Diagnostics>(f.Window, "_diagnostics").Stage == "paused"
                    && !PlaybackOptionsField<System.Windows.Threading.DispatcherTimer>(f.Window, "_progressTimer").IsEnabled,
                    name + ": pause/release changed occurrence or returned to playing");
                if (!promoted)
                {
                    Assert(!f.Player.HasSource && !next.IsPlaying && PlaybackOptionsField<object?>(f.Window, "_waveListening") is null
                        && Control<TextBlock>(f.Window, "QualityText").Text == "", name + ": aborted unopened source acquired a lifecycle or started late");
                    return;
                }
                Assert(listening is not null && ReferenceEquals(PlaybackOptionsField<object?>(f.Window, "_waveListening"), listening)
                    && Control<TextBlock>(f.Window, "QualityText").Text == "Офлайн · QA matrix PCM B"
                    && state.Load("qa-pause-account") is { CurrentIndex: 1 } saved && saved.Tracks[1].Id == next.Id,
                    name + ": pause lost B metadata/listening/state");
                var clock = (System.Diagnostics.Stopwatch)listening!.GetType().GetProperty("Clock")!.GetValue(listening)!;
                Assert(!clock.IsRunning, name + ": paused listening clock still runs");
                await Call(f.Window, "TogglePlaybackAsync");
                await Until(() => clock.IsRunning);
                Assert(f.Player.IsPlaying && clock.IsRunning && f.Player.SourceLocalPath == next.LocalPath
                    && ReferenceEquals(PlaybackOptionsField<object>(f.Window, "_waveListening"), listening),
                    name + ": explicit resume changed or failed to restore B lifecycle");
            }

            foreach (var natural in new[] { false, true })
            {
                using var preview = new PlaybackOptionsFixture("qa-promoted-preview-" + natural);
                var fragment = new TrackRow { Id = "qa-fragment", RequestId = "qa-fragment", Title = "Invalid short fixture", Available = true,
                    DurationMs = 120000, LocalPath = PlaybackOptionsSilentWave("qa-fragment-" + natural, 30), LocalQuality = "Must not commit" };
                var previewQueue = PlaybackOptionsField<PlaybackQueue>(preview.Window, "_playbackQueue");
                previewQueue.SetItems([preview.First, fragment], preview.First);
                await preview.Play(); await PreloadAwaitPrepared(preview.Window);
                await preview.Rendered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                SaInvoke(preview.Window, "RememberWaveSource", fragment, new RadioBatch("qa-wave-session", "qa-wave-batch", [fragment]));
                var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                PlaybackOptionsSet(preview.Window, "_audioOutputReady", gate.Task);
                if (natural) preview.Player.Pause(); // Native Ended reaches Advance with IsPlaying false.
                var advance = Call(preview.Window, "AdvanceAsync", 1, natural);
                Assert(!advance.IsCompleted && !preview.Player.HasSource && !fragment.IsPlaying
                    && PlaybackOptionsField<object?>(preview.Window, "_waveListening") is null,
                    "preview was promoted instead of waiting for readiness on the ordinary open path");
                gate.TrySetResult();
                await advance.WaitAsync(TimeSpan.FromSeconds(5));
                Assert(!preview.Player.IsPlaying && !preview.Player.HasSource && !fragment.IsPlaying
                    && PlaybackOptionsField<object?>(preview.Window, "_waveListening") is null
                    && Control<TextBlock>(preview.Window, "QualityText").Text == ""
                    && PlaybackOptionsField<Diagnostics>(preview.Window, "_diagnostics").Stage == "playback_error",
                    "preview-duration rejection happened after promoted metadata/listening commit");
            }
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
    private static Task<byte[]>? _playbackOptionsMp3;
    private static Task<byte[]> PlaybackOptionsMp3Async() => _playbackOptionsMp3 ??= GenerateAsync();

    private static async Task<byte[]> GenerateAsync()
    {
        var wave = PlaybackOptionsSilentWave("route-stream-60", 60);
        var folder = await StorageFolder.GetFolderFromPathAsync(_directory);
        var file = await folder.CreateFileAsync("playback-options-silent.mp3", CreationCollisionOption.FailIfExists);
        var preparation = await new MediaTranscoder { HardwareAccelerationEnabled = false }.PrepareFileTranscodeAsync(
            await StorageFile.GetFileFromPathAsync(wave), file, MediaEncodingProfile.CreateMp3(AudioEncodingQuality.High));
        Assert(preparation.CanTranscode, "could not generate the native route playback fixture");
        await preparation.TranscodeAsync();
        return await File.ReadAllBytesAsync(file.Path);
    }

    private static HttpResponseMessage PlaybackOptionsAudioInfo(int serial)
        => Json(JsonSerializer.Serialize(new { result = new { downloadInfo = new
        {
            codec = "mp3", quality = "nq", transport = "raw", bitrate = 192,
            url = $"https://s1.vlx.yandex.net/playback-options-{serial}.mp3"
        } } }));
    private static object? PlaybackOptionsInvoke(object value, string name, params object?[] args)
        => value.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(value, args);
    private static T PlaybackOptionsField<T>(object value, string name)
        => (T)value.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(value)!;
    private static void PlaybackOptionsSet(object value, string name, object? next)
        => value.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(value, next);
    private static (WindowsIntegration Gate, Func<Windows.Media.SystemMediaTransportControlsButton, Task> Dispatch) PlaybackOptionsMediaGate(MainWindow window)
    {
        var gate = (WindowsIntegration)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(WindowsIntegration));
        PlaybackOptionsSet(gate, "_window", window);
        PlaybackOptionsSet(gate, "<IsAvailable>k__BackingField", true);
        PlaybackOptionsSet(gate, "<WindowHandle>k__BackingField", new System.Windows.Interop.WindowInteropHelper(window).Handle);
        PlaybackOptionsSet(window, "_windows", gate);
        Task delivered = Task.CompletedTask;
        gate.Command += action => delivered = window.HandleDesktopMediaCommandAsync(action);
        return (gate, Dispatch);

        Task Dispatch(Windows.Media.SystemMediaTransportControlsButton button)
        {
            delivered = Task.CompletedTask;
            PlaybackOptionsInvoke(gate, "DispatchButton", button);
            return delivered;
        }
    }

    private static async Task<(Uri Outgoing, Uri Incoming)> BeginTransportCrossfade(PlaybackOptionsFixture fixture, string name)
    {
        var queue = PlaybackOptionsField<PlaybackQueue>(fixture.Window, "_playbackQueue");
        var incoming = new TrackRow { Id = name, RequestId = name, Title = "Fixture successor", Available = true,
            DurationMs = 30000, LocalPath = PlaybackOptionsSilentWave(name, 30) };
        queue.SetItems([fixture.First, incoming], fixture.First);
        await fixture.Play();
        await PreloadAwaitPrepared(fixture.Window);
        fixture.Player.SetCrossfade(10);
        await Call(fixture.Window, "AdvanceAsync", 1, true);
        Assert(ReferenceEquals(queue.Current, incoming) && fixture.Player.FadingLocalPath == fixture.First.LocalPath,
            "fixture did not begin an exact managed crossfade");
        return (PlaybackInput.FileUri(fixture.First.LocalPath!), PlaybackInput.FileUri(incoming.LocalPath!));
    }

    private static Task ReadyPlaybackOutput(MainWindow window)
        => (Task)PlaybackOptionsInvoke(window, "EnsureAudioOutputReadyAsync", CancellationToken.None)!;

    private sealed class PlaybackOptionsFakeRoute(string id, bool fail = false) : IAudioOutputRoute
    {
        public string Id => id;
        internal HashSet<Windows.Media.Playback.MediaPlayer> Players { get; } = [];
        internal Func<Windows.Media.Playback.MediaPlayer, bool>? Reject { get; set; }
        public void Apply(Windows.Media.Playback.MediaPlayer player)
        {
            if (fail || Reject?.Invoke(player) == true) throw new InvalidOperationException("fake route failure");
            Players.Add(player); // Deliberately does not access AudioDevice or a real endpoint.
        }
    }
    private sealed class PlaybackOptionsFakeOutputs : IAudioOutputDevices
    {
        public event Action? Changed;
        internal bool Available { get; set; } = true;
        internal bool SecondAvailable { get; set; }
        internal TaskCompletionSource<IAudioOutputRoute>? HeldRoute { get; set; }
        internal TaskCompletionSource<IReadOnlyList<AudioOutputDevice>>? HeldList { get; set; }
        internal Exception? DisposeFailure { get; set; }
        internal int DisposeCalls { get; private set; }
        internal int WatchFailures { get; set; }
        internal int WatchAttempts { get; private set; }
        internal int ListRequests { get; private set; }
        internal int ResolveRequests { get; private set; }
        internal bool Watching { get; set; }
        public void Watch()
        {
            if (Watching) return;
            WatchAttempts++;
            if (WatchFailures > 0)
            {
                WatchFailures--;
                throw new COMException("Synthetic watcher start error", unchecked((int)0x80004005));
            }
            Watching = true;
        }
        internal void RaiseChanged()
        {
            if (Watching) Changed?.Invoke();
        }
        public void Dispose()
        {
            DisposeCalls++;
            if (DisposeFailure is { } error) throw error;
        }
        public Task<IReadOnlyList<AudioOutputDevice>> ListAsync()
        {
            ListRequests++;
            if (HeldList is { } held)
            {
                HeldList = null;
                return held.Task;
            }
            var devices = new List<AudioOutputDevice>();
            if (Available) devices.Add(new("fake-A", "Fixture headphones"));
            if (SecondAvailable) devices.Add(new("fake-B", "Fixture speakers"));
            return Task.FromResult<IReadOnlyList<AudioOutputDevice>>(devices);
        }
        public Task<IAudioOutputRoute> ResolveAsync(string id)
        {
            ResolveRequests++;
            if (id.Length > 0 && !Available) throw new MusicException("Fixture device unavailable");
            return HeldRoute?.Task ?? Task.FromResult<IAudioOutputRoute>(new PlaybackOptionsFakeRoute(id));
        }
    }
    private sealed class PlaybackOptionsFixture : IDisposable
    {
        internal readonly PlaybackOptionsFakeOutputs Outputs;
        internal readonly NativePlayer Player;
        internal readonly MainWindow Window;
        internal readonly TrackRow First;
        internal readonly TaskCompletionSource Rendered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly PreferencesStore _preferences;
        internal PlaybackOptionsFixture(string name, PlaybackOptionsFakeOutputs? outputs = null,
            HttpMessageHandler? apiHandler = null, HttpMessageHandler? bufferHandler = null)
        {
            Outputs = outputs ?? new();
            var directory = Path.Combine(_directory, "playback-options-" + name);
            _preferences = new PreferencesStore(directory);
            _preferences.Save(new() { Volume = 0, Quality = apiHandler is null ? AudioQuality.Excellent : AudioQuality.Optimal });
            Player = new NativePlayer(new AudioBuffer(Path.Combine(directory, "buffer"),
                bufferHandler ?? new FakeHandler((_, _) => throw new InvalidOperationException("fixture forbids audio network"))));
            Window = new MainWindow(restore: false, api: new MusicApi(apiHandler ?? new FakeHandler((_, _) => throw new InvalidOperationException("fixture forbids network"))),
                store: new SessionStore(directory), diagnosticsDirectory: directory, offline: new OfflineLibrary(Path.Combine(directory, "offline"),
                    new AudioBuffer(Path.Combine(directory, "offline-buffer"), new FakeHandler((_, _) => throw new InvalidOperationException("fixture forbids download network")))),
                player: Player, preferences: _preferences, audioOutputDevices: Outputs);
            First = new TrackRow { Id = "playback-options-first", RequestId = "playback-options-first", Title = "Silent fixture", Artist = "Fixture", Available = true,
                DurationMs = 30000, LocalPath = PlaybackOptionsSilentWave(name, 30) };
            Window.ContentRendered += (_, _) => Rendered.TrySetResult();
            Window.Show();
        }
        internal async Task Play()
        {
            await ReadyPlaybackOutput(Window);
            await Window.PlayTrackAsync(First);
        }
        public void Dispose() => Window.Close();
    }
}
