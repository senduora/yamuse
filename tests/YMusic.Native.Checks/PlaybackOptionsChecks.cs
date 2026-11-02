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
            PlaybackOptionsField<Dictionary<TrackRow, (string SessionId, string BatchId, WaveMode Mode, string? TrackSeedId, string? AlbumSeedId)>>(f.Window, "_waveSources")
                [next] = ("qa-wave-session", "qa-wave-batch", WaveMode.Personal, null, null);
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
            Assert(f.Player.IsPlaying && f.Player.SourceLocalPath == next.LocalPath && File.Exists(f.First.LocalPath!)
                && ReferenceEquals(PlaybackOptionsField<object>(f.Window, "_waveListening"), listening) && listeningClock.IsRunning
                && Control<TextBlock>(f.Window, "QualityText").Text == "Офлайн · QA prepared PCM B",
                "new explicit resume lost B metadata/lifecycle or an offline file");
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
                PlaybackOptionsField<Dictionary<TrackRow, (string SessionId, string BatchId, WaveMode Mode, string? TrackSeedId, string? AlbumSeedId)>>(f.Window, "_waveSources")
                    [next] = ("qa-wave-session", "qa-wave-batch", WaveMode.Personal, null, null);
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
                Assert(f.Player.IsPlaying && clock.IsRunning && f.Player.SourceLocalPath == next.LocalPath
                    && ReferenceEquals(PlaybackOptionsField<object>(f.Window, "_waveListening"), listening),
                    name + ": explicit resume changed or failed to restore B lifecycle");
            }

            using var preview = new PlaybackOptionsFixture("qa-promoted-preview");
            var fragment = new TrackRow { Id = "qa-fragment", RequestId = "qa-fragment", Title = "Invalid short fixture", Available = true,
                DurationMs = 120000, LocalPath = PlaybackOptionsSilentWave("qa-fragment", 30), LocalQuality = "Must not commit" };
            var previewQueue = PlaybackOptionsField<PlaybackQueue>(preview.Window, "_playbackQueue");
            previewQueue.SetItems([preview.First, fragment], preview.First);
            await preview.Play(); await PreloadAwaitPrepared(preview.Window);
            PlaybackOptionsField<Dictionary<TrackRow, (string SessionId, string BatchId, WaveMode Mode, string? TrackSeedId, string? AlbumSeedId)>>(preview.Window, "_waveSources")
                [fragment] = ("qa-wave-session", "qa-wave-batch", WaveMode.Personal, null, null);
            await Call(preview.Window, "AdvanceAsync", 1, false);
            Assert(!preview.Player.IsPlaying && !preview.Player.HasSource && !fragment.IsPlaying
                && PlaybackOptionsField<object?>(preview.Window, "_waveListening") is null
                && Control<TextBlock>(preview.Window, "QualityText").Text == ""
                && PlaybackOptionsField<Diagnostics>(preview.Window, "_diagnostics").Stage == "playback_error",
                "preview-duration rejection happened after promoted metadata/listening commit");
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
    private static void PlaybackOptionsSet(object value, string name, object? next)
        => value.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(value, next);

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
        internal TaskCompletionSource<IAudioOutputRoute>? HeldRoute { get; set; }
        public void Watch() { }
        public void Dispose() { }
        public Task<IReadOnlyList<AudioOutputDevice>> ListAsync()
            => Task.FromResult<IReadOnlyList<AudioOutputDevice>>(Available ? [new("fake-A", "Fixture headphones")] : []);
        public Task<IAudioOutputRoute> ResolveAsync(string id)
        {
            if (id.Length > 0 && !Available) throw new MusicException("Fixture device unavailable");
            return HeldRoute?.Task ?? Task.FromResult<IAudioOutputRoute>(new PlaybackOptionsFakeRoute(id));
        }
    }
    private sealed class PlaybackOptionsFixture : IDisposable
    {
        internal readonly PlaybackOptionsFakeOutputs Outputs = new();
        internal readonly NativePlayer Player;
        internal readonly MainWindow Window;
        internal readonly TrackRow First;
        internal readonly TaskCompletionSource Rendered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly PreferencesStore _preferences;
        internal PlaybackOptionsFixture(string name)
        {
            var directory = Path.Combine(_directory, "playback-options-" + name);
            _preferences = new PreferencesStore(directory); _preferences.Save(new() { Volume = 0 });
            Player = new NativePlayer(new AudioBuffer(Path.Combine(directory, "buffer"),
                new FakeHandler((_, _) => throw new InvalidOperationException("fixture forbids audio network"))));
            Window = new MainWindow(restore: false, api: new MusicApi(new FakeHandler((_, _) => throw new InvalidOperationException("fixture forbids network"))),
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
            await (Task)PlaybackOptionsInvoke(Window, "EnsureAudioOutputReadyAsync")!;
            await Window.PlayTrackAsync(First);
        }
        public void Dispose() => Window.Close();
    }
}
