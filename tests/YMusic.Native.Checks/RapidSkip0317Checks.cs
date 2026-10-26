using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows.Controls;
using YMusic.Native;

internal static partial class Program
{
    private static async Task CheckRapidSkip0317()
    {
        await Run("RapidSkip0317: manual local bursts preserve every queue step history and occurrence", Rs0317Bursts);
        await Run("RapidSkip0317: sufficient local and prepared promotion avoid network opening and redundant current decode", Rs0317LocalAndPrepared);
        await Run("RapidSkip0317: a new manual intent cancels the old optional quality probe without waiting for its budget", Rs0317QualityCancellation);
        await Run("RapidSkip0317: stale completion cannot override direct selection stop signout or close", Rs0317StaleCompletion);
        await Run("RapidSkip0317: automatic and wave guards serialize while stale finally retains the new owner", Rs0317Ownership);
    }

    private static async Task Rs0317Bursts()
    {
        var measurements = new List<object>();
        foreach (var scenario in new[]
        {
            (Name: "five-next", Start: 0, Directions: new[] { 1, 1, 1, 1, 1 }, Repeated: false, Shuffle: false, Repeat: RepeatMode.Off),
            (Name: "three-previous", Start: 6, Directions: new[] { -1, -1, -1 }, Repeated: false, Shuffle: false, Repeat: RepeatMode.Off),
            (Name: "mixed-history", Start: 1, Directions: new[] { 1, 1, -1, 1 }, Repeated: false, Shuffle: false, Repeat: RepeatMode.Off),
            (Name: "repeated-occurrences", Start: 0, Directions: new[] { 1, 1, 1, -1, 1 }, Repeated: true, Shuffle: false, Repeat: RepeatMode.Off),
            (Name: "shuffle", Start: 0, Directions: new[] { 1, 1, -1, 1, 1 }, Repeated: false, Shuffle: true, Repeat: RepeatMode.Off),
            (Name: "repeat-boundary", Start: 5, Directions: new[] { 1, 1, 1 }, Repeated: false, Shuffle: false, Repeat: RepeatMode.All),
            (Name: "manual-repeat-one", Start: 0, Directions: new[] { 1, 1, -1, 1 }, Repeated: false, Shuffle: false, Repeat: RepeatMode.One)
        })
        {
            using var fixture = new Rs0317Fixture(scenario.Name, repeated: scenario.Repeated);
            var expected = fixture.ConfigureQueue(scenario.Start, scenario.Shuffle, scenario.Repeat);
            foreach (var direction in scenario.Directions) { if (direction > 0) expected.Next(false); else expected.Previous(); }
            using var process = Process.GetCurrentProcess(); var cpu = process.TotalProcessorTime; var clock = Stopwatch.StartNew();
            var slots = new HashSet<object>(); var tasks = new List<Task>();
            foreach (var direction in scenario.Directions)
            {
                tasks.Add(Rs0317Call(fixture.Window, "AdvanceAsync", direction, false));
                if (Rs0317Field<object?>(fixture.Player, "_current") is { } slot) slots.Add(slot);
            }
            await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(5));
            if (Rs0317Field<object?>(fixture.Player, "_current") is { } currentSlot) slots.Add(currentSlot);
            Assert(fixture.Queue.CurrentIndex == expected.CurrentIndex && fixture.Queue.CurrentOccurrenceToken == expected.CurrentOccurrenceToken
                && fixture.Queue.Version == expected.Version && fixture.Queue.History.SequenceEqual(expected.History), "manual burst lost a queue step, history or occurrence");
            Assert(ReferenceEquals(fixture.Queue.Current, Rs0317Field<TrackRow>(fixture.Window, "_current"))
                && fixture.Player.SourceLocalPath == expected.Current!.LocalPath && expected.Current.IsPlaying
                && !Rs0317Field<bool>(fixture.Window, "_loadingStream") && !Rs0317Field<bool>(fixture.Window, "_advancingPlayback")
                && fixture.Handler.Requests == 0, "stale open owns the native source or UI state after the burst");
            measurements.Add(new { scenario.Name, commands = scenario.Directions, finalIndex = fixture.Queue.CurrentIndex,
                occurrence = fixture.Queue.CurrentOccurrenceToken, elapsedMs = clock.Elapsed.TotalMilliseconds,
                processCpuMs = (process.TotalProcessorTime - cpu).TotalMilliseconds, observedCurrentSlots = slots.Count, externalRequests = fixture.Handler.Requests });
        }
        File.WriteAllText(Path.Combine(_directory, "rapid-skip-0317-bursts.json"), JsonSerializer.Serialize(measurements, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static async Task Rs0317LocalAndPrepared()
    {
        using var fixture = new Rs0317Fixture("local-prepared", bitrate: _ => 320);
        fixture.ConfigureQueue();
        var opening = Rs0317Call(fixture.Window, "AdvanceAsync", 1, false);
        Assert(fixture.Status.Text == "Открываем локальный трек…" && fixture.Handler.Requests == 0, "sufficient local file is presented or resolved as a network stream");
        await opening.WaitAsync(TimeSpan.FromSeconds(5));
        if (Rs0317Field<Task?>(fixture.Window, "_preloadTask") is { } preload) await preload.WaitAsync(TimeSpan.FromSeconds(5));
        Assert(fixture.Player.HasPrepared, "local next slot was not prepared");
        var prepared = Rs0317Field<object>(fixture.Player, "_prepared"); var queueVersion = fixture.Queue.Version;
        var promotion = Rs0317Call(fixture.Window, "AdvanceAsync", 1, false);
        Assert(promotion.IsCompleted, "ready local promotion introduced an asynchronous current open");
        await promotion;
        Assert(ReferenceEquals(prepared, Rs0317Field<object>(fixture.Player, "_current")) && fixture.Queue.CurrentIndex == 2
            && fixture.Queue.Version == queueVersion + 1 && !fixture.Status.Text.Contains("аудиопоток", StringComparison.OrdinalIgnoreCase)
            && fixture.Player.CrossfadeSeconds == 3 && !fixture.Player.NormalizeVolume && fixture.Handler.Requests == 0,
            "prepared promotion reopened the current decoder, changed settings or reported a network opening");
    }

    private static async Task Rs0317QualityCancellation()
    {
        using var fixture = new Rs0317Fixture("quality-cancel", bitrate: i => i == 1 ? 128 : 320);
        fixture.ConfigureQueue();
        var first = Rs0317Call(fixture.Window, "AdvanceAsync", 1, false);
        await fixture.Handler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var clock = Stopwatch.StartNew();
        var second = Rs0317Call(fixture.Window, "AdvanceAsync", 1, false);
        Assert(fixture.Handler.Canceled.Task.IsCompleted, "new manual intent waited for the old 1500ms quality budget instead of canceling it");
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5));
        Assert(fixture.Queue.CurrentIndex == 2 && fixture.Player.SourceLocalPath == fixture.Rows[2].LocalPath && fixture.Handler.Requests == 1
            && ((ComboBox)fixture.Window.FindName("QualityCombo")).SelectedIndex == 0,
            "quality cancellation changed selected quality or resolved a sufficient local target");
        File.WriteAllText(Path.Combine(_directory, "rapid-skip-0317-quality-cancel.json"), JsonSerializer.Serialize(new
        { requests = fixture.Handler.Requests, cancellationObservedOnNewIntent = true, finalIndex = 2, elapsedMs = clock.Elapsed.TotalMilliseconds,
            policyBudgetMs = AudioQualityPolicy.OfflineUpgradeTimeoutMilliseconds, qualityIndex = 0 }, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static async Task Rs0317StaleCompletion()
    {
        foreach (var action in new[] { "direct-selection", "cancel-stop", "signout", "close" })
        {
            using var fixture = new Rs0317Fixture("late-" + action, bitrate: i => i == 1 ? 128 : 320);
            fixture.Handler.IgnoreCancellation = true; fixture.ConfigureQueue();
            var first = Rs0317Call(fixture.Window, "AdvanceAsync", 1, false);
            await fixture.Handler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            switch (action)
            {
                case "direct-selection": await fixture.Window.PlayTrackAsync(fixture.Rows[4]).WaitAsync(TimeSpan.FromSeconds(5)); break;
                // This is the shared cancellation/NativePlayer.Stop pair used by the
                // Windows Stop route; no real SMTC session is created by the fixture.
                case "cancel-stop": Rs0317Field<CancellationTokenSource>(fixture.Window, "_playCts").Cancel(); fixture.Player.Stop(); break;
                case "signout": fixture.Window.SignOut(); break;
                case "close": fixture.Window.Close(); break;
            }
            fixture.Handler.Release.TrySetResult(); await first.WaitAsync(TimeSpan.FromSeconds(5));
            if (action == "direct-selection")
                Assert(fixture.Queue.CurrentIndex == 4 && fixture.Player.SourceLocalPath == fixture.Rows[4].LocalPath
                    && ReferenceEquals(Rs0317Field<TrackRow>(fixture.Window, "_current"), fixture.Rows[4]), "late superseded completion replaced a direct selection");
            else Assert(!fixture.Player.HasSource && !fixture.Player.IsPlaying, "late completion revived canceled, signed-out or closed playback");
            Assert(fixture.Handler.Requests == 1 && !Rs0317Field<bool>(fixture.Window, "_advancingPlayback"), "late completion retried a request or retained navigation ownership");
        }
    }

    private static async Task Rs0317Ownership()
    {
        using (var fixture = new Rs0317Fixture("owner", bitrate: i => i is 1 or 2 ? 128 : 320))
        {
            fixture.ConfigureQueue();
            var automatic = Rs0317Call(fixture.Window, "AdvanceAsync", 1, true);
            await fixture.Handler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await Rs0317Call(fixture.Window, "AdvanceAsync", 1, true);
            Assert(fixture.Queue.CurrentIndex == 1 && fixture.Handler.Requests == 1, "duplicate automatic end advanced twice");
            var manual = Rs0317Call(fixture.Window, "AdvanceAsync", 1, false);
            await automatic.WaitAsync(TimeSpan.FromSeconds(2));
            Assert(!manual.IsCompleted && fixture.Queue.CurrentIndex == 2 && Rs0317Field<bool>(fixture.Window, "_advancingPlayback"),
                "stale automatic finally released a newer manual owner");
            fixture.Window.SignOut(); await manual.WaitAsync(TimeSpan.FromSeconds(5));
            Assert(!fixture.Player.HasSource && !Rs0317Field<bool>(fixture.Window, "_advancingPlayback"), "canceling the current owner retained playback or navigation state");
        }
        using (var fixture = new Rs0317Fixture("wave-guard"))
        {
            fixture.ConfigureQueue(); var version = fixture.Queue.Version;
            Rs0317Set(fixture.Window, "_advancingPlayback", true); Rs0317Set(fixture.Window, "_advancingWave", true);
            await Rs0317Call(fixture.Window, "AdvanceAsync", 1, false);
            await Rs0317Call(fixture.Window, "AdvanceAsync", 1, true);
            Assert(fixture.Queue.Version == version && Rs0317Field<bool>(fixture.Window, "_advancingPlayback") && fixture.Handler.Requests == 0,
                "manual/automatic advance entered an already owned wave extension");
            Rs0317Set(fixture.Window, "_advancingPlayback", false); Rs0317Set(fixture.Window, "_advancingWave", false);
        }
    }

    private static Task Rs0317Call(object target, string method, params object[] args) => (Task)target.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, args)!;
    private static T Rs0317Field<T>(object target, string field) => (T)target.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;
    private static void Rs0317Set(object target, string field, object value) => target.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);

    private sealed class Rs0317Fixture : IDisposable
    {
        private const int Seed = 117;
        internal MainWindow Window { get; }
        internal NativePlayer Player { get; }
        internal TrackRow[] Rows { get; }
        internal Rs0317Handler Handler { get; } = new();
        internal PlaybackQueue Queue => Rs0317Field<PlaybackQueue>(Window, "_playbackQueue");
        internal TextBlock Status => (TextBlock)Window.FindName("StatusText");
        internal Rs0317Fixture(string name, Func<int, int>? bitrate = null, bool repeated = false)
        {
            var directory = Path.Combine(_directory, "rapid-skip-0317-" + name); var music = Path.Combine(directory, "music"); Directory.CreateDirectory(music);
            Rows = Enumerable.Range(0, 7).Select(i =>
            {
                var path = Path.Combine(music, "synthetic-" + i + ".wav"); Rs0317Wave(path);
                return new TrackRow { Id = "synthetic-" + i, RequestId = "synthetic-" + i, Title = "Synthetic " + i, Artist = "Fixture",
                    Available = true, DurationMs = 2000, LocalPath = path, LocalQuality = "Generated local WAV" };
            }).ToArray();
            if (repeated) Rows[2] = Rows[0];
            if (bitrate is not null)
            {
                // Logical service-quality metadata drives policy independently of
                // the owned generated WAV container used by the native decoder.
                var items = Rows.Select((row, i) => new OfflineItem(row.Id, row.RequestId, row.Title, row.Artist, "Fixture", 2000,
                    Path.GetFileName(row.LocalPath!), "mp3", bitrate(i), new FileInfo(row.LocalPath!).Length,
                    Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(row.LocalPath!))), DateTimeOffset.UtcNow)).ToArray();
                File.WriteAllText(Path.Combine(music, "library.index.json"), JsonSerializer.Serialize(items));
            }
            Player = new NativePlayer(new AudioBuffer(Path.Combine(directory, "audio"), Handler));
            Window = new MainWindow(api: new MusicApi(Handler), store: new SessionStore(Path.Combine(directory, "session")), restore: false,
                diagnosticsDirectory: Path.Combine(directory, "diagnostics"),
                offline: new OfflineLibrary(music, new AudioBuffer(Path.Combine(directory, "downloads"), Handler)), player: Player,
                preferences: new PreferencesStore(Path.Combine(directory, "preferences")));
            ((Slider)Window.FindName("VolumeSlider")).Value = 0; Player.SetVolume(0);
            if (bitrate is not null)
            { Rs0317Set(Window, "_session", new Session("synthetic", null, DateTimeOffset.UtcNow.AddHours(1), "synthetic")); Rs0317Set(Window, "_account", new Account("fixture", "Synthetic", true)); }
            Rs0317Set(Window, "_playbackQueue", new PlaybackQueue(new Random(Seed)));
            // The native MainWindow is never shown: all stores are injected and
            // no shell, SMTC, real profile, login, updater or external media is used.
        }
        internal PlaybackQueue ConfigureQueue(int start = 0, bool shuffle = false, RepeatMode repeat = RepeatMode.Off)
        {
            var expected = new PlaybackQueue(new Random(Seed));
            foreach (var queue in new[] { Queue, expected })
            {
                queue.SetItems(Rows, Rows[0]); queue.Shuffle = shuffle; queue.Repeat = repeat;
                for (var i = 1; i <= start; i++) queue.SelectAt(i);
            }
            Rs0317Set(Window, "_current", Rows[start]); return expected;
        }
        public void Dispose() { Handler.Release.TrySetResult(); Window.Close(); Player.Dispose(); }
    }
    private sealed class Rs0317Handler : HttpMessageHandler
    {
        internal int Requests;
        internal bool IgnoreCancellation;
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Canceled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Interlocked.Increment(ref Requests); Entered.TrySetResult();
            using var registered = token.Register(() => Canceled.TrySetResult());
            if (IgnoreCancellation) await Release.Task;
            else await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("Synthetic released request") };
        }
    }
    private static void Rs0317Wave(string path)
    {
        const int rate = 48000, frames = rate * 2;
        using var output = new BinaryWriter(File.Create(path));
        output.Write(System.Text.Encoding.ASCII.GetBytes("RIFF")); output.Write(36 + frames * 2); output.Write(System.Text.Encoding.ASCII.GetBytes("WAVEfmt "));
        output.Write(16); output.Write((short)1); output.Write((short)1); output.Write(rate); output.Write(rate * 2); output.Write((short)2); output.Write((short)16);
        output.Write(System.Text.Encoding.ASCII.GetBytes("data")); output.Write(frames * 2);
        for (var i = 0; i < frames; i++) output.Write((short)(Math.Sin(i * 2 * Math.PI * 997 / rate) * 10000));
    }
}
