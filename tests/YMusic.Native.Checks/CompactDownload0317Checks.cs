using System.Collections.Concurrent;
using System.ComponentModel;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using YMusic.Native;

internal static partial class Program
{
    private static async Task CheckCompactDownload0317()
    {
        await Run("CompactDownload0317: one run counts deduplicated new work and releases subscriptions", Compact0317Model);
        await Run("CompactDownload0317: parallel reports coalesce without lost wakeups or post-dispose paints", Compact0317Dispatch);
        await Run("CompactDownload0317: 3900 cached plus two new downloads show zero half full in an 18px button", Compact0317TopUp);
        await Run("CompactDownload0317: all cached completes without showing a ring or transfer row", Compact0317AllCached);
        await Run("CompactDownload0317: a cached file disappearing after preflight joins only the actual new work", Compact0317MissingAfterPreflight);
        await Run("CompactDownload0317: pause resume and failed retry retain truthful counts and available actions", Compact0317PauseRetry);
        await Run("CompactDownload0317: navigation account change and close cannot paint another toolbar", Compact0317Isolation);
    }

    private static Task Compact0317Model()
    {
        var queue = new DownloadQueueState();
        queue.Replace(Enumerable.Range(0, 3900).Select(i => DcTrack("cached-" + i))
            .Concat([DcTrack("new-a"), DcTrack("new-a"), DcTrack("new-b")]), "large");
        var selected = queue.BeginRun(false);
        var work = selected.Where(entry => entry.Track.Id.StartsWith("new-", StringComparison.Ordinal)).ToArray();
        var notifications = 0;
        var operation = new CompactDownloadOperation(work, null, 7, () => notifications++);
        Assert(selected.Length == 3902 && operation.Total == 2 && operation.Percent == 0, "cached or duplicate ids entered the current work denominator");
        foreach (var entry in selected.Take(3900)) entry.Complete(100);
        Assert(notifications == 0 && operation.Completed == 0, "old cached entries changed the new-download indicator");
        work[0].Begin(); work[0].Progress(new(100, 100));
        Assert(operation.Percent is > 0 and < 50, "network EOF claimed a committed file");
        work[0].Complete(100); work[1].Begin(); work[1].Progress(new(8192, null));
        Assert(operation.Completed == 1 && operation.Percent == 50, "mixed or unknown media sizes fabricated a total byte percentage");
        work[1].SetState(DownloadEntryState.Failed);
        Assert(operation.Failed == 1 && operation.Percent == 50 && !operation.Complete, "a failed item earned successful progress");
        work[1].SetState(DownloadEntryState.Unavailable);
        Assert(operation.Unavailable == 1 && operation.Failed == 0 && operation.Percent == 50, "unavailable work became 100 percent success");
        work[1].Begin(); work[1].Complete(10000);
        Assert(operation.Percent == 100 && operation.Complete, "two committed saves did not complete the ring");
        operation.Dispose(); var finalNotifications = notifications;
        work[1].SetState(DownloadEntryState.Paused);
        Assert(operation.Disposed && operation.Completed == 2 && notifications == finalNotifications, "disposed run retained entry subscriptions or a mutable final snapshot");
        using var retry = new CompactDownloadOperation([work[1]], null, 7, () => { });
        Assert(retry.Total == 1 && retry.Completed == 0 && retry.Percent == 0, "retry reused the old completed cohort");
        retry.Exclude(work[1]); retry.Include(work[0]);
        Assert(retry.Total == 1 && retry.Completed == 1, "a file changing after preflight cannot update the cohort");
        return Task.CompletedTask;
    }

    private static async Task Compact0317Dispatch()
    {
        var callbacks = new ConcurrentQueue<Action>(); var values = new List<long>();
        DownloadProgressSink? sink = null;
        using var dispatch = new DownloadProgressDispatch(callbacks.Enqueue, () =>
        {
            if (sink!.Take() is { } value) values.Add(value.Bytes);
            if (values.Count == 1) sink.Report(new(20001, null)); // Arrives during the drain.
        });
        sink = new DownloadProgressSink(dispatch.Request);
        await Task.WhenAll(Enumerable.Range(0, 8).Select(worker => Task.Run(() =>
        {
            for (var n = 0; n < 10000; n++) sink.Report(new(worker * 10000 + n, null));
        })));
        Assert(callbacks.Count == 1, "parallel reports scheduled more than one pending dispatcher callback");
        Assert(callbacks.TryDequeue(out var first), "reports never scheduled a dispatcher callback"); first!();
        Assert(callbacks.Count == 1, "a report during the drain lost its next wakeup");
        Assert(callbacks.TryDequeue(out var second), "second drain was not queued"); second!();
        Assert(values.Count == 2 && values[1] == 20001 && sink.Take() is null && callbacks.Count == 0,
            "the coalesced sink retained a backlog or lost the latest report");
        sink.Report(new(30000, null)); dispatch.Dispose();
        Assert(callbacks.TryDequeue(out var stale), "dispose fixture did not queue a pending callback"); stale!();
        await Task.Run(() => sink.Report(new(40000, null)));
        Assert(values.Count == 2 && callbacks.Count == 0, "a disposed batch painted or queued more work");
    }

    private static async Task Compact0317TopUp()
    {
        using var fixture = new Compact0317Fixture("top-up", 3900);
        var playlist = fixture.Playlist([DcTrack("new-a"), DcTrack("new-b"), DcTrack("new-a")]);
        fixture.Show(playlist);
        var task = Call(fixture.Window, "DownloadPlaylistAsync");
        await Until(() => fixture.Requests.ContainsKey("new-a"));
        var operation = Compact0317Operation(fixture.Window);
        var button = Control<Button>(fixture.Window, "DownloadPlaylistButton");
        var ring = SaField<CircularDownloadProgress>(fixture.Window, "_compactDownloadRing");
        var percentages = new List<double> { ring.Value };
        var descriptor = DependencyPropertyDescriptor.FromProperty(RangeBase.ValueProperty, typeof(CircularDownloadProgress));
        EventHandler changed = (_, _) => percentages.Add(ring.Value); descriptor.AddValueChanged(ring, changed);
        try
        {
            Assert(operation.Total == 2 && operation.Completed == 0 && ring.Value == 0
                && SaField<TextBlock>(fixture.Window, "_compactDownloadCaption").Text == "Скачивается · 0/2", "top-up began with the whole playlist's 3902 tracks");
            fixture.Window.Width = 1050; fixture.Window.Height = 680; fixture.Window.UpdateLayout();
            Assert(ring.Width == 18 && ring.Height == 18 && ring.ActualWidth is >= 16 and <= 20 && ring.ActualHeight is >= 16 and <= 20
                && !ring.IsIndeterminate && button.IsEnabled && button.ActualWidth < 230 && button.ActualHeight <= 44,
                $"compact progress is not a finite button-sized accessible action at minimum window size: ring {ring.Width}x{ring.Height}, actual {ring.ActualWidth}x{ring.ActualHeight}; button {button.ActualWidth}x{button.ActualHeight}, enabled {button.IsEnabled}, visible {button.IsVisible}");
            Assert(((SolidColorBrush)ring.Foreground).Color == ((SolidColorBrush)Application.Current.FindResource("DownloadBrush")).Color,
                "ring colour depends on artwork instead of the fixed green DownloadBrush");
            Assert(AutomationProperties.GetName(button).Contains("0/2") && AutomationProperties.GetHelpText(button).Contains("текущей загрузки"),
                "assistive UI lost the finite current-operation state");
            Assert(Control<Border>(fixture.Window, "PlaylistTransferPanel").Visibility == Visibility.Collapsed,
                "a large transfer row is still visible during a top-up");
            var tracks = Control<ListView>(fixture.Window, "TracksList");
            Assert(VirtualizingPanel.GetIsVirtualizing(tracks) && VirtualizingPanel.GetVirtualizationMode(tracks) == VirtualizationMode.Recycling
                && SaVisuals<ListViewItem>(tracks).Count() is > 0 and < 100, "large-playlist progress broke row virtualization");
            App.Capture(fixture.Window, Path.Combine(fixture.DirectoryPath, "top-up-0-of-2-minimum.png"));
            fixture.Release("new-a");
            await Until(() => fixture.Requests.ContainsKey("new-b"));
            Assert(operation.Completed == 1 && operation.Total == 2 && ring.Value == 50
                && SaField<TextBlock>(fixture.Window, "_compactDownloadCaption").Text == "Скачивается · 1/2", "first actual save did not show one of two and half a circle");
            App.Capture(fixture.Window, Path.Combine(fixture.DirectoryPath, "top-up-1-of-2-minimum.png"));
            fixture.Release("new-b"); await task.WaitAsync(TimeSpan.FromSeconds(10));
            await Until(() => Control<Button>(fixture.Window, "DownloadPlaylistButton").Content is string text && text.Contains("Скачан"));
            Assert(operation.Disposed && operation.Complete && operation.Total == 2 && operation.Completed == 2
                && percentages.Contains(0) && percentages.Contains(50) && percentages.Contains(100), "ring did not reach verified zero/half/full states");
            Assert(fixture.Requests.Values.Sum() == 2 && fixture.Requests.Count == 2 && fixture.Library.Read().Count == 3902,
                "presentation changed actual fetching, duplicated a track or lost cached audio");
            Assert(Control<Border>(fixture.Window, "PlaylistTransferPanel").Visibility == Visibility.Collapsed
                && SaVisuals<CircularDownloadProgress>(button).Count() == 0, "completed top-up retained its ring or old transfer row");
            File.WriteAllText(Path.Combine(fixture.DirectoryPath, "top-up-evidence.json"), JsonSerializer.Serialize(new
            { cached = 3900, operation.Total, operation.Completed, operation.Percent, percentages, requests = fixture.Requests, indexItems = fixture.Library.Read().Count,
                ring = new { ring.Width, ring.Height, ring.IsIndeterminate }, minWindow = new { fixture.Window.Width, fixture.Window.Height } }, new JsonSerializerOptions { WriteIndented = true }));
        }
        finally { descriptor.RemoveValueChanged(ring, changed); }
    }

    private static async Task Compact0317AllCached()
    {
        using var fixture = new Compact0317Fixture("all-cached", 3900);
        var playlist = fixture.Playlist([]);
        await fixture.Library.RememberPlaylistAsync(playlist, AudioQuality.Excellent, default);
        fixture.Show(playlist);
        await Until(() => Control<Button>(fixture.Window, "DownloadPlaylistButton").Content is string text && text.Contains("Скачан"));
        var task = Call(fixture.Window, "DownloadPlaylistAsync");
        Assert(Control<Button>(fixture.Window, "DownloadPlaylistButton").Content is string initial && initial.Contains("Скачан"),
            "a fully cached playlist discarded its immediately available downloaded state");
        await task.WaitAsync(TimeSpan.FromSeconds(10));
        await Until(() => Control<Button>(fixture.Window, "DownloadPlaylistButton").Content is string text && text.Contains("Скачан"));
        Assert(Compact0317Operation(fixture.Window) is { Total: 0, Disposed: true } && fixture.Requests.Count == 0
            && SaField<CircularDownloadProgress?>(fixture.Window, "_compactDownloadRing") is null
            && Control<Border>(fixture.Window, "PlaylistTransferPanel").Visibility == Visibility.Collapsed,
            "all-cached completion showed download progress or fetched audio");
        Assert(typeof(MainWindow).GetField("_downloadProgressTimer", BindingFlags.Instance | BindingFlags.NonPublic) is null,
            "download progress still installs a polling timer");
    }

    private static async Task Compact0317PauseRetry()
    {
        using (var fixture = new Compact0317Fixture("pause", 0))
        {
            var playlist = fixture.Playlist([DcTrack("new-a"), DcTrack("new-b")]); fixture.Show(playlist);
            var task = Call(fixture.Window, "DownloadPlaylistAsync"); await Until(() => fixture.Requests.ContainsKey("new-a"));
            fixture.Release("new-a"); await Until(() => fixture.Requests.ContainsKey("new-b"));
            var old = Compact0317Operation(fixture.Window);
            SaInvoke(fixture.Window, "PauseDownloadQueue"); await task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert(old.Disposed && old.Completed == 1 && old.Percent == 50 && SaField<TextBlock>(fixture.Window, "_compactDownloadCaption").Text.StartsWith("На паузе"),
                "pause discarded saved work or claimed completion");
            SaInvoke(fixture.Window, "DownloadPlaylist_Click", fixture.Window, new RoutedEventArgs());
            Assert(SaVisuals<Button>(fixture.Window.DownloadCenterView).Single(b => b.Name == "DownloadResumeButton").IsEnabled,
                "compact button does not reach an enabled resume action");
            fixture.Show(playlist); fixture.Release("new-b");
            await Call(fixture.Window, "ResumeDownloadQueue").WaitAsync(TimeSpan.FromSeconds(5));
            Assert(Compact0317Operation(fixture.Window) is { Total: 1, Completed: 1, Disposed: true }
                && fixture.Requests["new-a"] == 1 && fixture.Requests["new-b"] == 2, "resume reused the old cohort or fetched a completed track");
        }
        using (var fixture = new Compact0317Fixture("retry", 0) { FailTrack = "new-b", Hold = false })
        {
            fixture.Show(fixture.Playlist([DcTrack("new-a"), DcTrack("new-b"), DcTrack("unavailable", false)]));
            await Call(fixture.Window, "DownloadPlaylistAsync");
            var failed = Compact0317Operation(fixture.Window);
            Assert(failed is { Total: 3, Completed: 1, Failed: 1, Unavailable: 1, Disposed: true } && failed.Percent < 100
                && SaField<TextBlock>(fixture.Window, "_compactDownloadCaption").Text.StartsWith("Не скачано"), "failed or unavailable downloads became full green success");
            Assert(Control<Button>(fixture.Window, "DownloadPlaylistButton").ToolTip.ToString()!.Contains("ошибок 1"), "compact terminal result hid the error count");
            fixture.FailTrack = null; await Call(fixture.Window, "RetryFailedDownloads");
            Assert(Compact0317Operation(fixture.Window) is { Total: 1, Completed: 1, Disposed: true }
                && fixture.Requests["new-a"] == 1 && fixture.Requests["new-b"] == 2 && !fixture.Requests.ContainsKey("unavailable"),
                "retry changed backend admission or retained the previous failed cohort");
            Assert(Control<Button>(fixture.Window, "DownloadPlaylistButton").Content is string text && !text.Contains("✓ Скачан"),
                "a successful retry claimed that the still unavailable playlist is entirely downloaded");
        }
    }

    private static async Task Compact0317MissingAfterPreflight()
    {
        using var fixture = new Compact0317Fixture("missing-after-preflight", 1);
        var source = fixture.Playlist([]);
        fixture.Show(source with { Tracks = new[] { DcTrack("new-a") }.Concat(source.Tracks).ToArray() });
        var task = Call(fixture.Window, "DownloadPlaylistAsync");
        await Until(() => fixture.Requests.ContainsKey("new-a"));
        var operation = Compact0317Operation(fixture.Window);
        Assert(operation.Total == 1 && operation.Completed == 0, "existing audio was counted before it disappeared");
        File.Delete(Path.Combine(fixture.Library.DirectoryPath, "cached.flac"));
        fixture.Release("new-a"); await Until(() => fixture.Requests.ContainsKey("cached-0"));
        Assert(operation.Total == 2 && operation.Completed == 1 && operation.Percent == 50,
            "the actual worker's missing-file recheck did not expand the current work cohort");
        fixture.Release("cached-0"); await task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert(operation is { Total: 2, Completed: 2, Disposed: true } && fixture.Requests.Values.Sum() == 2
            && fixture.Library.Read().Select(item => item.Id).Distinct(StringComparer.Ordinal).Count() == 2
            && fixture.Queue.Entries.All(entry => entry.State == DownloadEntryState.Completed && fixture.Library.Find(entry.Track) is not null),
            "a disappearing cached file changed download admission or lost committed music");
    }

    private static async Task Compact0317Isolation()
    {
        using var fixture = new Compact0317Fixture("isolation", 0);
        var playlist = fixture.Playlist([DcTrack("new-a"), DcTrack("new-b")]); fixture.Show(playlist);
        var task = Call(fixture.Window, "DownloadPlaylistAsync"); await Until(() => fixture.Requests.ContainsKey("new-a"));
        var operation = Compact0317Operation(fixture.Window);
        var oldSink = SaField<Dictionary<DownloadEntry, DownloadProgressSink>>(fixture.Window, "_downloadProgress").Values.Single();
        var other = playlist with { Kind = "other", Title = "Другой плейлист", Tracks = [DcTrack("other")] }; fixture.Show(other);
        oldSink.Report(new(100, 200)); await Task.Delay(60);
        Assert(Control<Button>(fixture.Window, "DownloadPlaylistButton").Content is string otherText && !otherText.Contains("Скачивается"),
            "an old playlist callback painted a different playlist's button");
        fixture.Show(playlist); fixture.Window.SignOut(); await task.WaitAsync(TimeSpan.FromSeconds(5));
        var caption = SaField<TextBlock>(fixture.Window, "_compactDownloadCaption").Text;
        oldSink.Report(new(200, 200)); await Task.Delay(60);
        Assert(operation.Disposed && fixture.Queue.Suspended && SaField<TextBlock>(fixture.Window, "_compactDownloadCaption").Text == caption
            && Control<Button>(fixture.Window, "DownloadPlaylistButton").Content is string,
            "an old account callback repainted or retained the previous ring");
        typeof(MainWindow).GetField("_session", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(fixture.Window, FakeSession);
        typeof(MainWindow).GetField("_accountCts", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(fixture.Window,
            CancellationTokenSource.CreateLinkedTokenSource(SaField<CancellationTokenSource>(fixture.Window, "_lifetime").Token));
        fixture.Show(other);
        var next = Call(fixture.Window, "DownloadPlaylistAsync"); await Until(() => fixture.Requests.ContainsKey("other"));
        var nextOperation = Compact0317Operation(fixture.Window); var nextCaption = SaField<TextBlock>(fixture.Window, "_compactDownloadCaption").Text;
        oldSink.Report(new(99999, 99999)); await Task.Delay(60);
        Assert(nextOperation.AccountGeneration != operation.AccountGeneration && nextOperation.Completed == 0
            && SaField<TextBlock>(fixture.Window, "_compactDownloadCaption").Text == nextCaption, "the previous account's sink changed a new run");
        fixture.Window.Close(); await next.WaitAsync(TimeSpan.FromSeconds(5));
        Assert(nextOperation.Disposed && fixture.Active == 0, "close retained subscriptions or an active fake request");
    }

    private static CompactDownloadOperation Compact0317Operation(MainWindow window) => SaField<CompactDownloadOperation>(window, "_compactDownloadOperation");

    private sealed class Compact0317Fixture : IDisposable
    {
        internal string DirectoryPath { get; }
        internal MainWindow Window { get; }
        internal OfflineLibrary Library { get; }
        internal DownloadQueueState Queue => SaField<DownloadQueueState>(Window, "_downloadQueue");
        internal ConcurrentDictionary<string, int> Requests { get; } = new(StringComparer.Ordinal);
        internal bool Hold = true;
        internal string? FailTrack;
        private readonly TrackRow[] _cached;
        private readonly ConcurrentDictionary<string, TaskCompletionSource> _released = new(StringComparer.Ordinal);
        private int _active;
        internal int Active => Volatile.Read(ref _active);
        internal Compact0317Fixture(string name, int cached)
        {
            DirectoryPath = Path.Combine(_directory, "compact-download0317-" + name); Directory.CreateDirectory(DirectoryPath);
            var music = Path.Combine(DirectoryPath, "Music"); Directory.CreateDirectory(music);
            _cached = Enumerable.Range(0, cached).Select(i => DcTrack("cached-" + i)).ToArray();
            if (cached > 0)
            {
                var media = new byte[32]; File.WriteAllBytes(Path.Combine(music, "cached.flac"), media);
                var hash = Convert.ToHexString(SHA256.HashData(media));
                var items = _cached.Select(t => new OfflineItem(t.Id, t.RequestId, t.Title, t.Artist, t.Album, t.DurationMs,
                    "cached.flac", "flac", 0, media.Length, hash, DateTimeOffset.UtcNow));
                File.WriteAllText(Path.Combine(music, "library.index.json"), JsonSerializer.Serialize(items));
            }
            var api = new FakeHandler((request, _) =>
            {
                Assert(request.RequestUri!.AbsolutePath == "/get-file-info", "unexpected compact-download API route");
                var id = Uri.UnescapeDataString(request.RequestUri.Query.TrimStart('?').Split('&').Select(p => p.Split('=', 2)).Single(p => p[0] == "trackId")[1]);
                return Task.FromResult(Json("{\"result\":{\"downloadInfo\":{\"quality\":\"lossless\",\"codec\":\"mp3\",\"bitrate\":320,\"transport\":\"raw\",\"url\":\"https://s1.vlx.yandex.net/" + id + ".mp3\"}}}"));
            });
            var downloads = new FakeHandler((request, _) =>
            {
                var id = Uri.UnescapeDataString(Path.GetFileNameWithoutExtension(request.RequestUri!.AbsolutePath));
                Requests.AddOrUpdate(id, 1, (_, count) => count + 1);
                if (id == FailTrack) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
                Interlocked.Increment(ref _active);
                var bytes = new byte[id == "new-a" ? 1024 : 262144];
                var release = Hold ? _released.GetOrAdd(id, _ => new(TaskCreationOptions.RunContinuationsAsynchronously)).Task : Task.CompletedTask;
                var content = new StreamContent(new Compact0317Stream(bytes, release, () => Interlocked.Decrement(ref _active)));
                if (id != "new-b") content.Headers.ContentLength = bytes.Length;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
            });
            Library = new OfflineLibrary(music, new AudioBuffer(Path.Combine(DirectoryPath, "buffer"), downloads));
            var player = new NativePlayer(new AudioBuffer(Path.Combine(DirectoryPath, "player-buffer"),
                new FakeHandler((_, _) => throw new InvalidOperationException("compact download cannot start playback"))));
            Window = new MainWindow(api: new MusicApi(api), store: new SessionStore(Path.Combine(DirectoryPath, "session")), restore: false,
                diagnosticsDirectory: DirectoryPath, offline: Library, player: player);
            Window.Show(); Control<FrameworkElement>(Window, "LoginPanel").Visibility = Visibility.Collapsed;
            typeof(MainWindow).GetField("_session", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(Window, FakeSession);
            typeof(MainWindow).GetField("_account", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(Window, new Account("12345", "Fixture", true));
            typeof(MainWindow).GetField("_concurrentDownloads", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(Window, 1);
        }
        internal PlaylistDetails Playlist(TrackRow[] added) => new("12345", "compact0317", "Текущая докачка", "", 0, _cached.Concat(added).ToArray());
        internal void Show(PlaylistDetails playlist) => SaInvoke(Window, "ShowPlaylist", playlist);
        internal void Release(string id) => _released.GetOrAdd(id, _ => new(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();
        public void Dispose() { if (!SaField<bool>(Window, "_closed")) Window.Close(); }
    }
    private sealed class Compact0317Stream(byte[] bytes, Task release, Action disposed) : Stream
    {
        private int _position, _disposed;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _position; set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await release.WaitAsync(cancellationToken);
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
