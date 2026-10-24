using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using YMusic.Native;

internal static partial class Program
{
    private static async Task CheckPlaybackPreload()
    {
        await Run("Playback preload: downloaded next auto-promotes its prepared decoder without HTTP", async () =>
        {
            using var fixture = new PreloadIntegrationFixture("offline", signedIn: false);
            var first = fixture.Local("first"); var next = fixture.Row("downloaded");
            var saved = await fixture.Save(next);
            await fixture.Play(first, next); await PreloadAwaitPrepared(fixture.Window);
            var decoder = PreloadSlot(fixture.Player, "_prepared");
            Assert(next.IsDownloaded && fixture.Player.PreparedLocalPath == fixture.Library.ResolvePath(saved.FileName), "downloaded next not prepared locally");
            Assert(fixture.ResolveCalls == 0 && fixture.MediaCalls == 0 && decoder!.Position == TimeSpan.Zero, "offline prefetch used network or started playback");
            PreloadInvoke(fixture.Window, "CommitSeek", 10.8);
            await Until(() => fixture.Queue.CurrentOccurrenceToken != fixture.FirstOccurrence);
            Assert(ReferenceEquals(fixture.Queue.Current, next) && ReferenceEquals(decoder, PreloadSlot(fixture.Player, "_current")), "progress threshold did not reuse prepared decoder");
            Assert(fixture.ResolveCalls == 0 && fixture.MediaCalls == 0 && fixture.Player.IsPlaying, "offline promotion reopened online");
            Assert(Control<TextBlock>(fixture.Window, "StatusText").Text.Contains("Офлайн"), "offline promotion status missing");
            fixture.Player.Stop();
            Assert(File.Exists(fixture.Library.ResolvePath(saved.FileName)) && File.Exists(first.LocalPath), "player released a user/offline file by deleting it");
        });

        await Run("Playback preload: online next resolves and buffers once then promotes without reopen and cleans its temp", async () =>
        {
            using var fixture = new PreloadIntegrationFixture("online");
            var first = fixture.Local("first"); var next = fixture.Row("online");
            await fixture.Play(first, next); await PreloadAwaitPrepared(fixture.Window);
            var decoder = PreloadSlot(fixture.Player, "_prepared"); var path = fixture.Player.PreparedLocalPath;
            Assert(fixture.ResolveCalls == 1 && fixture.MediaCalls == 1 && path is not null && File.Exists(path), "online next not fetched exactly once");
            await Call(fixture.Window, "AdvanceAsync", 1, false);
            Assert(ReferenceEquals(decoder, PreloadSlot(fixture.Player, "_current")) && fixture.Player.SourceLocalPath == path, "promotion opened a different decoder/source");
            Assert(fixture.ResolveCalls == 1 && fixture.MediaCalls == 1 && !fixture.Player.HasPrepared, "promotion made another resolve/buffer request");
            await Until(() => fixture.Player.FadingLocalPath is null);
            PreloadInvoke(fixture.Window, "CancelNextPreparation", true); fixture.Player.Stop();
            await Until(() => fixture.BufferEmpty);
            Assert(File.Exists(first.LocalPath), "online buffer cleanup deleted local current");
        });

        await Run("Playback preload: queue shuffle repeat quality current and account changes reject stale exact occurrences", async () =>
        {
            using var fixture = new PreloadIntegrationFixture("identity");
            var a = fixture.Local("a"); var b = fixture.Local("b"); var c = fixture.Local("c");
            await fixture.Play(a, b, c); await PreloadAwaitPrepared(fixture.Window);
            var oldDecoder = PreloadSlot(fixture.Player, "_prepared")!;
            fixture.Queue.Move(1, 2); PreloadInvoke(fixture.Window, "UpdateControls"); await PreloadAwaitPrepared(fixture.Window);
            Assert(ReferenceEquals(PreloadPreparedTrack(fixture.Window), c) && oldDecoder.Source is null, "queue move kept stale next");
            oldDecoder = PreloadSlot(fixture.Player, "_prepared")!;
            Control<Button>(fixture.Window, "ShuffleButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await PreloadAwaitPrepared(fixture.Window);
            Assert(ReferenceEquals(PreloadPreparedTrack(fixture.Window), fixture.Queue.PeekNext(true)) && oldDecoder.Source is null, "shuffle did not replace old preparation");
            fixture.Queue.Repeat = RepeatMode.One; PreloadInvoke(fixture.Window, "UpdateControls"); await PreloadAwaitPrepared(fixture.Window);
            Assert(ReferenceEquals(PreloadPreparedTrack(fixture.Window), a) && PreloadInvoke(fixture.Window, "ReadyNextPlayback", false) is null, "repeat-one natural preparation leaked into manual Next");

            fixture.Queue.Shuffle = false; fixture.Queue.Repeat = RepeatMode.All;
            await fixture.Play(a, b, b); await PreloadAwaitPrepared(fixture.Window);
            var firstDuplicate = fixture.Queue.PeekNextOccurrenceToken(true);
            await Call(fixture.Window, "AdvanceAsync", 1, false); await PreloadAwaitPrepared(fixture.Window);
            Assert(fixture.Queue.CurrentIndex == 1 && fixture.Queue.CurrentOccurrenceToken == firstDuplicate
                && fixture.Queue.PeekNextOccurrenceToken(true) != firstDuplicate && ReferenceEquals(PreloadPreparedTrack(fixture.Window), b), "same TrackRow duplicate occurrence was deduplicated");
            fixture.Queue.RemoveAt(2);
            Assert(PreloadInvoke(fixture.Window, "ReadyNextPlayback", true) is null, "mutated queue accepted a stale duplicate token");
            PreloadInvoke(fixture.Window, "UpdateControls"); await PreloadAwaitPrepared(fixture.Window);
            oldDecoder = PreloadSlot(fixture.Player, "_prepared")!;
            await fixture.Window.PlayTrackAsync(c);
            Assert(oldDecoder.Source is null && !fixture.Player.HasPrepared && fixture.Player.SourceLocalPath == c.LocalPath, "current replacement kept stale preparation");

            fixture.Queue.Repeat = RepeatMode.Off; fixture.BlockResolve();
            await fixture.Play(a, fixture.Row("quality-pending")); await fixture.ResolveEntered.Task;
            var obsolete = PreloadGet<Task>(fixture.Window, "_preloadTask"); var resolves = fixture.ResolveCalls;
            Control<ComboBox>(fixture.Window, "QualityCombo").SelectedIndex = 1;
            await Until(() => fixture.ResolveCalls > resolves); await obsolete.WaitAsync(TimeSpan.FromSeconds(5));
            fixture.ReleaseResolve(); await PreloadAwaitPrepared(fixture.Window);
            var prepared = PreloadGet<object>(fixture.Window, "_preparedPlayback");
            var identity = prepared.GetType().GetProperty("Identity")!.GetValue(prepared)!;
            Assert((AudioQuality)identity.GetType().GetProperty("Quality")!.GetValue(identity)! == AudioQuality.Optimal, "quality change retained obsolete preparation");
            fixture.BlockResolve(); fixture.Queue.SetItems([a, fixture.Row("account-pending")], a); PreloadInvoke(fixture.Window, "UpdateControls");
            await fixture.ResolveEntered.Task; var pending = PreloadGet<Task>(fixture.Window, "_preloadTask");
            fixture.Window.SignOut(); fixture.ReleaseResolve(); await pending.WaitAsync(TimeSpan.FromSeconds(5));
            Assert(!fixture.Player.HasPrepared && !fixture.Player.HasSource && fixture.Queue.Current is null && fixture.BufferEmpty, "account cancellation resurrected prepared/current audio");
        });

        await Run("Playback preload: paused next stays silent until promotion and UI pause seek control an active overlap", async () =>
        {
            using var fixture = new PreloadIntegrationFixture("paused", signedIn: false);
            var a = fixture.Local("a"); var b = fixture.Local("b"); var c = fixture.Local("c");
            await fixture.Play(a, b, c); await PreloadAwaitPrepared(fixture.Window);
            await Call(fixture.Window, "TogglePlaybackAsync"); var next = PreloadSlot(fixture.Player, "_prepared")!;
            await Task.Delay(250);
            Assert(!fixture.Player.IsPlaying && next.Position == TimeSpan.Zero && next.Volume == 0 && fixture.Player.SourceLocalPath == a.LocalPath, "pause started prepared next");
            await Call(fixture.Window, "AdvanceAsync", 1, false);
            Assert(fixture.Player.IsPlaying && fixture.Player.SourceLocalPath == b.LocalPath && fixture.Player.FadingLocalPath is null, "manual Next from pause did not promote immediately");
            await fixture.Play(a, b, c); await PreloadAwaitPrepared(fixture.Window);
            await Call(fixture.Window, "AdvanceAsync", 1, true);
            Assert(fixture.Player.FadingLocalPath == a.LocalPath, "automatic promotion did not overlap");
            await Call(fixture.Window, "TogglePlaybackAsync"); await Task.Delay(80);
            var current = PreloadSlot(fixture.Player, "_current")!; var outgoing = PreloadSlot(fixture.Player, "_fading")!;
            var currentPosition = current.Position; var oldPosition = outgoing.Position;
            await Task.Delay(300);
            Assert((current.Position - currentPosition).Duration().TotalMilliseconds < 80 && (outgoing.Position - oldPosition).Duration().TotalMilliseconds < 80, "UI pause left one overlapping player running");
            PreloadInvoke(fixture.Window, "CommitSeek", 2.5);
            Assert(fixture.Player.FadingLocalPath is null && outgoing.Source is null, "UI seek did not release outgoing");
            await Call(fixture.Window, "TogglePlaybackAsync"); await Task.Delay(100);
            Assert(fixture.Player.IsPlaying && fixture.Player.Position.TotalSeconds >= 2.4 && fixture.ResolveCalls == 0 && fixture.MediaCalls == 0, "UI seek/resume wiring or local network contract failed");
        });

        await Run("Playback preload: offline delete releases exact prepared and fading indexed files preserving unrelated current", async () =>
        {
            using var fixture = new PreloadIntegrationFixture("deletion", signedIn: false);
            var a = fixture.Row("delete-outgoing", available: false); var b = fixture.Row("delete-prepared", available: false); var c = fixture.Row("keep-current", available: false);
            var ia = await fixture.Save(a); var ib = await fixture.Save(b); var ic = await fixture.Save(c);
            PreloadInvoke(fixture.Window, "ApplyOfflineStates", (object)new[] { a, b, c });
            await fixture.Play(a, b, c); await PreloadAwaitPrepared(fixture.Window);
            var foreign = Path.Combine(fixture.Library.DirectoryPath, "foreign-user-file.txt"); File.WriteAllText(foreign, "preserve");
            await PreloadDelete(fixture.Window, b, [a, b, c]);
            Assert(!File.Exists(fixture.Library.ResolvePath(ib.FileName)) && fixture.Player.SourceLocalPath == fixture.Library.ResolvePath(ia.FileName) && fixture.Player.IsPlaying, "prepared deletion stopped unrelated current or left locked file");
            await PreloadAwaitPrepared(fixture.Window); await Call(fixture.Window, "AdvanceAsync", 1, true);
            Assert(fixture.Player.FadingLocalPath == fixture.Library.ResolvePath(ia.FileName) && fixture.Player.SourceLocalPath == fixture.Library.ResolvePath(ic.FileName), "expected managed outgoing/current not active");
            await PreloadDelete(fixture.Window, a, [a, b, c]);
            Assert(!File.Exists(fixture.Library.ResolvePath(ia.FileName)) && fixture.Player.FadingLocalPath is null
                && fixture.Player.SourceLocalPath == fixture.Library.ResolvePath(ic.FileName) && fixture.Player.IsPlaying, "fading deletion stopped unrelated current or left locked file");
            Assert(fixture.Library.Read().Single().Id == c.Id && File.ReadAllText(foreign) == "preserve" && fixture.ResolveCalls == 0 && fixture.MediaCalls == 0, "managed deletion changed unrelated library/file/network");
        });

        await Run("Playback preload: Wave prefetch emits no listening feedback and overlap finish excludes paused wall time", async () =>
        {
            using var fixture = new PreloadIntegrationFixture("wave");
            await Call(fixture.Window, "StartWaveViewAsync", false); await PreloadAwaitPrepared(fixture.Window); await PreloadFeedbackIdle(fixture.Window);
            Assert(fixture.Feedback.Select(e => e.Type).SequenceEqual(new[] { "radioStarted", "trackStarted" }) && fixture.Feedback[^1].TrackId == "901", "prefetch emitted listening events for an unplayed track");
            var oldListening = PreloadGet<object>(fixture.Window, "_waveListening");
            var clock = (Stopwatch)oldListening.GetType().GetProperty("Clock")!.GetValue(oldListening)!;
            var listeningBeforeWall = clock.Elapsed.TotalSeconds; var wall = Stopwatch.StartNew();
            await Call(fixture.Window, "TogglePlaybackAsync"); var beforePause = clock.Elapsed; await Task.Delay(250);
            Assert((clock.Elapsed - beforePause).TotalMilliseconds < 30, "initial pause counted as listening");
            await Call(fixture.Window, "TogglePlaybackAsync"); await Task.Delay(120);
            await Call(fixture.Window, "AdvanceAsync", 1, true); await PreloadFeedbackIdle(fixture.Window);
            Assert(ReferenceEquals(PreloadGet<object>(fixture.Window, "_fadingWaveListening"), oldListening)
                && fixture.Feedback.Count(e => e.TrackId == "901" && e.Type is "trackFinished" or "skip") == 0, "outgoing feedback finished at promotion rather than actual overlap end");
            await Call(fixture.Window, "TogglePlaybackAsync"); beforePause = clock.Elapsed; await Task.Delay(400);
            Assert((clock.Elapsed - beforePause).TotalMilliseconds < 30 && fixture.Player.FadingLocalPath is not null, "overlap pause did not pause its listening clock");
            await Call(fixture.Window, "TogglePlaybackAsync"); await Until(() => fixture.Player.FadingLocalPath is null); await PreloadFeedbackIdle(fixture.Window);
            var completed = fixture.Feedback.Where(e => e.TrackId == "901" && e.Type is "trackFinished" or "skip").ToArray();
            Assert(completed.Length == 1 && completed[0].Type == "trackFinished" && Math.Abs(completed[0].Seconds - clock.Elapsed.TotalSeconds) < .08, "natural overlap did not finish outgoing once with actual played clock");
            Assert(wall.Elapsed.TotalSeconds - (completed[0].Seconds - listeningBeforeWall) > .50 && fixture.Feedback.Count(e => e.Type == "trackStarted" && e.TrackId == "902") == 1
                && !fixture.Feedback.Any(e => e.TrackId == "903"), "paused wall time counted or merely prepared track reported");
            var finishedAt = clock.Elapsed; await Task.Delay(100); Assert(clock.Elapsed == finishedAt, "finished outgoing listening clock kept running");
        });
    }

    private static T PreloadGet<T>(object target, string field) => (T)target.GetType().GetField(field, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(target)!;
    private static object? PreloadInvoke(MainWindow window, string method, params object?[] args)
        => typeof(MainWindow).GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, args);
    private static MediaPlayer? PreloadSlot(NativePlayer native, string field)
    {
        var slot = PreloadGet<object?>(native, field);
        return (MediaPlayer?)slot?.GetType().GetProperty("Player")!.GetValue(slot);
    }
    private static TrackRow? PreloadPreparedTrack(MainWindow window)
    {
        var prepared = PreloadGet<object?>(window, "_preparedPlayback");
        return (TrackRow?)prepared?.GetType().GetProperty("Track")!.GetValue(prepared);
    }
    private static async Task PreloadAwaitPrepared(MainWindow window)
    {
        if (PreloadGet<Task?>(window, "_preloadTask") is { } task) await task.WaitAsync(TimeSpan.FromSeconds(8));
        await Until(() => PreloadGet<NativePlayer>(window, "_player").HasPrepared);
    }
    private static Task PreloadFeedbackIdle(MainWindow window) => PreloadGet<Task>(window, "_feedbackTail").WaitAsync(TimeSpan.FromSeconds(5));
    private static async Task PreloadDelete(MainWindow window, TrackRow track, TrackRow[] rows)
    {
        PreloadInvoke(window, "ShowRows", (object)rows);
        Control<ListView>(window, "TracksList").SelectedItem = track;
        PreloadInvoke(window, "DeleteOfflineTrack_Click", window, new RoutedEventArgs());
        await Until(() => !PreloadGet<bool>(window, "_offlineMaintenance"));
    }
    private sealed record PreloadFeedback(string Type, string? TrackId, double Seconds);
    private sealed class PreloadIntegrationFixture : IDisposable
    {
        private readonly byte[] _media;
        private readonly object _sync = new();
        private readonly List<PreloadFeedback> _feedback = [];
        private readonly string _prefix;
        private int _resolveCalls, _mediaCalls;
        private bool _blockResolve;
        private TaskCompletionSource _resolveRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ResolveEntered { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public MainWindow Window { get; }
        public NativePlayer Player { get; }
        public OfflineLibrary Library { get; }
        public PlaybackQueue Queue => PreloadGet<PlaybackQueue>(Window, "_playbackQueue");
        public long? FirstOccurrence { get; private set; }
        public string DirectoryPath { get; }
        public int ResolveCalls => Volatile.Read(ref _resolveCalls);
        public int MediaCalls => Volatile.Read(ref _mediaCalls);
        public PreloadFeedback[] Feedback { get { lock (_sync) return _feedback.ToArray(); } }
        public bool BufferEmpty => !Directory.Exists(Path.Combine(DirectoryPath, "player-buffer")) || !Directory.EnumerateFiles(Path.Combine(DirectoryPath, "player-buffer")).Any();
        public PreloadIntegrationFixture(string name, bool signedIn = true)
        {
            _prefix = "preload-" + name + "-" + Guid.NewGuid().ToString("N");
            DirectoryPath = Path.Combine(_directory, _prefix); Directory.CreateDirectory(DirectoryPath);
            _media = File.ReadAllBytes(WriteWave(_prefix + ".wav", 12));
            var handler = new FakeHandler(Respond);
            var mediaHandler = new FakeHandler((request, _) =>
            {
                Assert(request.Headers.Authorization is null && !request.Headers.Contains("Cookie") && request.RequestUri!.Host == "s1.vlx.yandex.net", "media left anonymous fixture CDN");
                Interlocked.Increment(ref _mediaCalls);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(_media) });
            });
            var libraryHandler = new FakeHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(_media) }));
            Library = new OfflineLibrary(Path.Combine(DirectoryPath, "Music"), new AudioBuffer(Path.Combine(DirectoryPath, "library-buffer"), libraryHandler));
            Player = new NativePlayer(new AudioBuffer(Path.Combine(DirectoryPath, "player-buffer"), mediaHandler)); Player.SetVolume(0);
            Window = new MainWindow(api: new MusicApi(handler), store: new SessionStore(Path.Combine(DirectoryPath, "store")), restore: false, diagnosticsDirectory: DirectoryPath, offline: Library, player: Player)
            { ShowActivated = false, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -5000, Top = -5000 };
            if (signedIn) typeof(MainWindow).GetField("_session", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(Window, FakeSession);
            Window.Show(); Control<Slider>(Window, "VolumeSlider").Value = 0; Player.SetVolume(0);
        }
        public TrackRow Row(string id, bool available = true) => new() { Id = _prefix + "-" + id, RequestId = _prefix + "-" + id + ":1", Title = id, DurationMs = 12000, Available = available };
        public TrackRow Local(string id)
        {
            var path = Path.Combine(DirectoryPath, id + ".wav"); File.WriteAllBytes(path, _media);
            return new() { Id = _prefix + "-" + id, RequestId = _prefix + "-" + id + ":1", Title = id, DurationMs = 12000, Available = true, LocalPath = path, LocalQuality = "Fixture WAV" };
        }
        public Task<OfflineItem> Save(TrackRow row) => Library.SaveAsync(row, new AudioDescriptor(new Uri("https://s1.vlx.yandex.net/integration-library.mp3"), "mp3", 320), default);
        public async Task Play(params TrackRow[] rows)
        {
            Queue.SetItems(rows, rows[0]); FirstOccurrence = Queue.CurrentOccurrenceToken;
            await Window.PlayTrackAsync(rows[0]);
        }
        public void BlockResolve()
        { _blockResolve = true; _resolveRelease = new(TaskCreationOptions.RunContinuationsAsynchronously); ResolveEntered = new(TaskCreationOptions.RunContinuationsAsynchronously); }
        public void ReleaseResolve() { _blockResolve = false; _resolveRelease.TrySetResult(); }
        private async Task<HttpResponseMessage> Respond(HttpRequestMessage request, CancellationToken token)
        {
            var path = request.RequestUri!.AbsolutePath.TrimEnd('/');
            if (path == "/get-file-info")
            {
                var serial = Interlocked.Increment(ref _resolveCalls); ResolveEntered.TrySetResult();
                if (_blockResolve) await _resolveRelease.Task.WaitAsync(token);
                return Json(JsonSerializer.Serialize(new { result = new { downloadInfo = new { quality = "lossless", codec = "mp3", bitrate = 320, transport = "raw", url = "https://s1.vlx.yandex.net/integration-" + serial + ".mp3" } } }));
            }
            if (path == "/rotor/session/new")
                return Json(JsonSerializer.Serialize(new { result = new { radioSessionId = "preload-wave", batchId = "batch-one", sequence = Enumerable.Range(901, 4).Select(id => new { track = new { id = id.ToString(), title = "Preload Wave " + id, available = true, durationMs = 12000 } }) } }));
            if (path == "/rotor/session/preload-wave/feedback")
            {
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token)); var e = body.RootElement.GetProperty("event");
                var type = e.GetProperty("type").GetString()!;
                lock (_sync) _feedback.Add(new(type, e.TryGetProperty("trackId", out var id) ? id.GetString() : null, e.TryGetProperty("totalPlayedSeconds", out var seconds) ? seconds.GetDouble() : 0));
                return Json("{\"result\":{}}");
            }
            throw new InvalidOperationException("Unexpected preload fixture route: " + path);
        }
        public void Dispose() { ReleaseResolve(); Window.Close(); }
    }
}
