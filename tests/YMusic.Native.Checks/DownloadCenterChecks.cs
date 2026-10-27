using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using YMusic.Native;

internal static partial class Program
{
    private static async Task CheckDownloadCenter()
    {
        await Run("Download center: dispatcher ownership, bounded progress sink and safe errors", DownloadCenterModelChecks);
        await Run("Download center: bounded workers, individual progress, pause and explicit resume preserve completed files", DownloadCenterPauseResume);
        await Run("Download center: limits one and ten bound active requests and cancellation admission", DownloadCenterLimits);
        await Run("Download center: retry only failed tracks and indexed storage excludes foreign files", DownloadCenterRetry);
        await Run("Download center: logout and clear never restart a suspended queue", DownloadCenterAccountAndClear);
    }
    private static async Task DownloadCenterModelChecks()
    {
        var queue = new DownloadQueueState();
        queue.Replace([DcTrack("1"), DcTrack("1"), DcTrack("2")], "Проверка");
        var entries = queue.BeginRun(false); Assert(entries.Length == 2, "queue did not deduplicate exact track ids");
        entries[0].Begin(); entries[0].Progress(new(4096, null));
        Assert(entries[0].Indeterminate && entries[0].Bytes == 4096 && entries[0].SizeText.Contains("неизвестен"), "unknown content length became a fake percentage");
        entries[0].Progress(new(8192, 16384)); Assert(entries[0].Percent == 50, "individual percentage is wrong");
        entries[0].Complete(16384); entries[1].Begin(); queue.Pause(); queue.EndRun(true);
        Assert(queue.CanResume && entries[0].State == DownloadEntryState.Completed && entries[1].State == DownloadEntryState.Paused, "pause reset a completed row");
        var resumed = queue.BeginRun(false); Assert(resumed.Length == 1 && resumed[0] == entries[1], "resume includes a completed track");
        resumed[0].SetState(DownloadEntryState.Failed, "Ошибка"); queue.EndRun(false);
        Assert(queue.BeginRun(true).SequenceEqual(resumed), "retry did not select only failures"); queue.EndRun(false); queue.Suspend();
        Assert(!queue.CanResume && !queue.CanRetry && queue.BeginRun(true).Length == 0, "suspended queue can restart");
        var ownerRejected = await Task.Run(() => { try { queue.Pause(); return false; } catch (InvalidOperationException) { return true; } });
        Assert(ownerRejected, "background queue mutation was accepted");
        var sink = new DownloadProgressSink();
        await Task.Run(() => { for (var n = 1; n <= 10000; n++) sink.Report(new(n, null)); });
        Assert(sink.Take() is { Bytes: 10000 } && sink.Take() is null, "progress reports accumulated instead of coalescing");
        var sensitive = "https://audio.invalid/?token=SECRET C:\\private\\session.dpapi";
        Exception[] errors = [new HttpRequestException(sensitive), new IOException(sensitive), new UnauthorizedAccessException(sensitive), new MusicException(sensitive), new Exception(sensitive), new OperationCanceledException(sensitive)];
        foreach (var error in errors)
        {
            var reason = DownloadFailure.Describe(error);
            Assert(!reason.Contains("SECRET") && !reason.Contains("https:") && !reason.Contains("private") && reason.Length > 20, "unsafe or empty error reason");
        }
        Assert(DownloadFailure.Describe(new IOException(sensitive, unchecked((int)0x80070070))).Contains("место"), "disk-full reason was lost");
    }
    private static async Task DownloadCenterPauseResume()
    {
        using var fixture = new DownloadCenterFixture("pause", 2) { HoldFirstAttempts = true };
        var task = fixture.Start([DcTrack("1"), DcTrack("2"), DcTrack("3"), DcTrack("4")]);
        SaInvoke(fixture.Window, "DownloadCenter_Click", fixture.Window, new RoutedEventArgs()); fixture.Window.UpdateLayout();
        var uiThread = Environment.CurrentManagedThreadId; var wrongThread = false;
        foreach (var entry in fixture.Queue.Entries) entry.PropertyChanged += (_, _) => wrongThread |= Environment.CurrentManagedThreadId != uiThread;
        await Until(() => fixture.Queue.Entries[0].State == DownloadEntryState.Completed && fixture.Queue.Entries.Count(e => e.Bytes > 0 && e.Active) == 2);
        Assert(fixture.Queue.Entries[1].TotalBytes == DownloadCenterFixture.Media.Length && fixture.Queue.Entries[1].Percent is > 0 and < 100, "known-size row lacks live progress");
        Assert(fixture.Queue.Entries[2].TotalBytes is null && fixture.Queue.Entries[2].SizeText.Contains("неизвестен"), "unknown-size row lost received bytes");
        Assert(Control<ContentControl>(fixture.Window, "DownloadCenterPanel").IsVisible && !Control<ListView>(fixture.Window, "TracksList").IsVisible, "Downloads navigation did not display the center alone");
        var bars = SaVisuals<ProgressBar>(fixture.Window.DownloadCenterView).ToArray();
        Assert(bars.Any(bar => bar.DataContext == fixture.Queue.Entries[1] && bar.Value > 0) && bars.Any(bar => bar.DataContext == fixture.Queue.Entries[2] && bar.IsIndeterminate), "live progress was not bound to rendered rows");
        App.Capture(fixture.Window, Path.Combine(fixture.DirectoryPath, "downloads-active.png"));
        Assert(fixture.Peak <= 2 && fixture.Peak == 2 && fixture.Queue.Entries[3].State == DownloadEntryState.Queued, "worker limit or pending state is wrong");
        Assert(SaField<int>(fixture.Window, "_busy") == 0, "downloads use the global busy gate");
        SaInvoke(fixture.Window, "PauseDownloadQueue"); await task.WaitAsync(TimeSpan.FromSeconds(5));
        var saved = fixture.Library.Read().Single(); var path = fixture.Library.ResolvePath(saved.FileName);
        var hash = System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(path));
        Assert(fixture.Queue.Entries.Count(e => e.State == DownloadEntryState.Completed) == 1 && fixture.Queue.Entries.Count(e => e.State == DownloadEntryState.Paused) == 3, "cancel discarded queued work or marked it failed");
        Assert(fixture.Requests.GetValueOrDefault("4") == 0 && fixture.Active == 0, "pause admitted another request or left a request active");
        Assert(SaVisuals<Button>(fixture.Window.DownloadCenterView).Single(button => button.Name == "DownloadResumeButton").IsEnabled, "paused queue has no enabled Resume action");
        await Task.Delay(200);
        App.Capture(fixture.Window, Path.Combine(fixture.DirectoryPath, "downloads-paused.png"));
        fixture.HoldFirstAttempts = false;
        await Call(fixture.Window, "ResumeDownloadQueue").WaitAsync(TimeSpan.FromSeconds(5));
        Assert(fixture.Library.Read().Count == 4 && fixture.Queue.Entries.All(e => e.State == DownloadEntryState.Completed), "resume did not finish the queue");
        Assert(fixture.Queue.Notice.Contains("Все треки") && !fixture.Queue.Notice.Contains("на паузе"), "completed queue still claims it is paused");
        Assert(fixture.Requests["1"] == 1 && fixture.Requests["2"] == 2 && fixture.Requests["3"] == 2 && fixture.Requests["4"] == 1, "resume downloaded completed files or skipped pending files");
        var resumedBytes = await File.ReadAllBytesAsync(path);
        Assert(hash.SequenceEqual(System.Security.Cryptography.SHA256.HashData(resumedBytes)) && !wrongThread, "completed file changed or UI model mutated off dispatcher");
        await Task.Delay(200);
        App.Capture(fixture.Window, Path.Combine(fixture.DirectoryPath, "downloads-completed.png"));
        Assert(!Directory.EnumerateFiles(fixture.BufferPath, "*", SearchOption.AllDirectories).Any(), "pause or resume leaked a streaming buffer");
        File.WriteAllText(Path.Combine(fixture.DirectoryPath, "facts.json"), JsonSerializer.Serialize(new { fixture.Peak, requests = fixture.Requests, completed = fixture.Library.Read().Count, dispatcherThread = uiThread, wrongThread }));
    }
    private static async Task DownloadCenterRetry()
    {
        using var fixture = new DownloadCenterFixture("retry", 3) { FailTrack = "2" };
        await fixture.Start([DcTrack("1"), DcTrack("2"), DcTrack("3", available: false)]);
        SaInvoke(fixture.Window, "DownloadCenter_Click", fixture.Window, new RoutedEventArgs()); fixture.Window.UpdateLayout();
        Assert(fixture.Queue.Entries.Select(e => e.State).SequenceEqual([DownloadEntryState.Completed, DownloadEntryState.Failed, DownloadEntryState.Unavailable]), "per-track terminal statuses are wrong");
        Assert(fixture.Queue.Entries[1].Error.Contains("Аудиосервер") && !fixture.Queue.Entries[1].Error.Contains("token"), "failure reason missing or unsafe");
        await Task.Delay(350); // Let the existing 260ms content entrance finish before the evidence image.
        App.Capture(fixture.Window, Path.Combine(fixture.DirectoryPath, "downloads-errors.png"));
        fixture.FailTrack = null;
        await Call(fixture.Window, "RetryFailedDownloads");
        Assert(fixture.Requests["1"] == 1 && fixture.Requests["2"] == 2 && fixture.Requests.GetValueOrDefault("3") == 0, "retry attempted completed or unavailable rows");
        Assert(fixture.Queue.Entries[1].State == DownloadEntryState.Completed && fixture.Queue.Entries[1].Error == "" && fixture.Queue.Entries[1].Attempts == 2, "successful retry retained stale error or attempts");
        File.WriteAllBytes(Path.Combine(fixture.Library.DirectoryPath, "foreign-file.mp3"), new byte[1024 * 1024]);
        SaInvoke(fixture.Window, "RefreshDownloadStorage");
        var button = SaVisuals<Button>(fixture.Window.DownloadCenterView).Single(b => b.Name == "DownloadStorageButton");
        Assert(button.Content.ToString()!.Contains(DownloadEntry.FormatBytes(2L * DownloadCenterFixture.Media.Length)), "storage counted foreign files instead of indexed audio");
        var view = fixture.Window.DownloadCenterView; view.Measure(new Size(1100, 500)); view.Arrange(new Rect(0, 0, 1100, 500)); view.UpdateLayout();
        Assert(SaVisuals<ListView>(view).Single().Items.Count == 3, "center does not expose every track");
    }
    private static async Task DownloadCenterLimits()
    {
        foreach (var limit in new[] { 1, 10 })
        {
            using var fixture = new DownloadCenterFixture("limit-" + limit, limit) { HoldFirstAttempts = true };
            var task = fixture.Start(Enumerable.Range(2, limit + 2).Select(id => DcTrack(id.ToString())).ToArray());
            await Until(() => fixture.Active == limit);
            SaInvoke(fixture.Window, "PauseDownloadQueue"); await task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert(fixture.Peak == limit && fixture.Requests.Values.Sum() == limit && fixture.Active == 0, "configured worker limit or cancellation admission failed for " + limit);
            Assert(fixture.Queue.Entries.All(e => e.State == DownloadEntryState.Paused), "limit fixture left active or failed rows");
        }
    }
    private static async Task DownloadCenterAccountAndClear()
    {
        using (var fixture = new DownloadCenterFixture("logout", 2) { HoldFirstAttempts = true })
        {
            var task = fixture.Start([DcTrack("2"), DcTrack("3"), DcTrack("4")]);
            await Until(() => fixture.Active == 2); fixture.Window.SignOut();
            await task.WaitAsync(TimeSpan.FromSeconds(5));
            var count = fixture.Requests.Values.Sum();
            Assert(fixture.Queue.Suspended && !fixture.Queue.CanResume, "logout left a resumable account-scoped queue");
            typeof(MainWindow).GetField("_session", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(fixture.Window, FakeSession);
            await Call(fixture.Window, "ResumeDownloadQueue"); await Call(fixture.Window, "RetryFailedDownloads");
            Assert(fixture.Requests.Values.Sum() == count, "logging back in restarted stale account work");
        }
        using (var fixture = new DownloadCenterFixture("clear", 2) { HoldFirstAttempts = true })
        {
            var task = fixture.Start([DcTrack("1"), DcTrack("2"), DcTrack("3")]);
            await Until(() => fixture.Queue.Entries[0].State == DownloadEntryState.Completed && fixture.Active == 2);
            typeof(MainWindow).GetField("_autoFavoritesEnabled", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(fixture.Window, true);
            typeof(MainWindow).GetField("_pendingAutoFavorites", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(fixture.Window, new PlaylistDetails("local:favorites", "liked", "Проверка", "", 0, [DcTrack("4")]));
            SaInvoke(fixture.Window, "PauseDownloadQueue"); await task.WaitAsync(TimeSpan.FromSeconds(5));
            SaInvoke(fixture.Window, "TryStartPendingAutoFavorites");
            var count = fixture.Requests.Values.Sum();
            Assert(SaField<CancellationTokenSource?>(fixture.Window, "_downloadCts") is null && fixture.Requests.GetValueOrDefault("4") == 0, "pause auto-started another snapshot");
            await Call(fixture.Window, "ClearOfflineAfterConfirmationAsync");
            await Call(fixture.Window, "ResumeDownloadQueue"); await Call(fixture.Window, "RetryFailedDownloads");
            Assert(fixture.Queue.Suspended && fixture.Library.Read().Count == 0 && fixture.Requests.Values.Sum() == count && !SaField<bool>(fixture.Window, "_autoFavoritesEnabled"), "clear restarted work, retained indexed audio or enabled auto-download");
            Assert(fixture.Queue.Entries.All(e => e.State == DownloadEntryState.Removed), "clear retained false completed marks");
        }
    }
    private static TrackRow DcTrack(string id, bool available = true) => new() { Id = id, RequestId = id + ":91", Title = "Проверка " + id, Artist = "Тестовый исполнитель", DurationMs = 3000, Available = available };
    private sealed class DownloadCenterFixture : IDisposable
    {
        internal static readonly byte[] Media = new byte[131072];
        internal string DirectoryPath { get; }
        internal string BufferPath { get; }
        internal MainWindow Window { get; }
        internal OfflineLibrary Library { get; }
        internal DownloadQueueState Queue => SaField<DownloadQueueState>(Window, "_downloadQueue");
        internal ConcurrentDictionary<string, int> Requests { get; } = new(StringComparer.Ordinal);
        internal bool HoldFirstAttempts { get; set; }
        internal string? FailTrack { get; set; }
        private int _active, _peak;
        internal int Active => Volatile.Read(ref _active);
        internal int Peak => Volatile.Read(ref _peak);
        internal DownloadCenterFixture(string name, int concurrency)
        {
            DirectoryPath = Path.Combine(_directory, "download-center-" + name); Directory.CreateDirectory(DirectoryPath);
            BufferPath = Path.Combine(DirectoryPath, "buffer");
            var api = new FakeHandler((request, _) =>
            {
                Assert(request.RequestUri!.AbsolutePath == "/get-file-info", "unexpected download API request");
                var id = request.RequestUri.Query.TrimStart('?').Split('&').Select(part => part.Split('=', 2)).Single(part => part[0] == "trackId")[1];
                return Task.FromResult(Json("{\"result\":{\"downloadInfo\":{\"quality\":\"lossless\",\"codec\":\"mp3\",\"bitrate\":320,\"transport\":\"raw\",\"url\":\"https://s1.vlx.yandex.net/" + id + ".mp3\"}}}"));
            });
            var media = new FakeHandler((request, _) =>
            {
                var id = Path.GetFileNameWithoutExtension(request.RequestUri!.AbsolutePath);
                var attempt = Requests.AddOrUpdate(id, 1, (_, value) => value + 1);
                if (id == FailTrack) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("token=SECRET") });
                var active = Interlocked.Increment(ref _active);
                int peak; while (active > (peak = Volatile.Read(ref _peak)) && Interlocked.CompareExchange(ref _peak, active, peak) != peak) { }
                var hold = HoldFirstAttempts && attempt == 1 && id != "1";
                var content = new StreamContent(new DownloadCenterStream(Media, hold, () => Interlocked.Decrement(ref _active)));
                if (id != "3") content.Headers.ContentLength = Media.Length;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
            });
            Library = new OfflineLibrary(Path.Combine(DirectoryPath, "Music"), new AudioBuffer(BufferPath, media));
            var player = new NativePlayer(new AudioBuffer(Path.Combine(DirectoryPath, "player-buffer"), new FakeHandler((_, _) => throw new InvalidOperationException("download center must not start playback"))));
            Window = new MainWindow(api: new MusicApi(api), store: new SessionStore(Path.Combine(DirectoryPath, "session")), restore: false, diagnosticsDirectory: DirectoryPath, offline: Library, player: player)
            { ShowActivated = false, ShowInTaskbar = false, Left = -16000, Top = 0, WindowStartupLocation = WindowStartupLocation.Manual };
            Window.Show(); _ = Window.DownloadCenterView;
            typeof(MainWindow).GetField("_session", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(Window, FakeSession);
            typeof(MainWindow).GetField("_concurrentDownloads", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(Window, concurrency);
        }
        internal Task Start(TrackRow[] tracks) => Call(Window, "StartDownloadBatchAsync", tracks, "Тестовая очередь", null!, false);
        public void Dispose() => Window.Close();
    }
    private sealed class DownloadCenterStream(byte[] bytes, bool hold, Action disposed) : Stream
    {
        private int _position, _disposed;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _position; set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (hold && _position > 0) await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            if (hold && _position == 0) await Task.Delay(300, cancellationToken);
            var count = Math.Min(buffer.Length, bytes.Length - _position);
            bytes.AsMemory(_position, count).CopyTo(buffer); _position += count; return count;
        }
        protected override void Dispose(bool disposing) { if (Interlocked.Exchange(ref _disposed, 1) == 0) disposed(); base.Dispose(disposing); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
