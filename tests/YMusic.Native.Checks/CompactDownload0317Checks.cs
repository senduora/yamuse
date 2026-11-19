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
        await Run("CompactDownload0317: cached batch holds exact index and audio proofs until applied", Compact0317HeldCacheProofs);
        await Run("CompactDownload0317: confirmed download size or checksum failure refreshes the selected saved-playlist card", async () =>
        { await Compact0317RejectedCacheRefresh(changeLength: false); await Compact0317RejectedCacheRefresh(changeLength: true); });
        await Run("CompactDownload0317: confirmed disappearance refreshes the selected playlist when both downloads fail", Compact0317MissingCacheRefresh);
        await Run("CompactDownload0317: failed quality upgrade refreshes the selected card for a lower-quality saved variant", Compact0317UpgradeFailureRefresh);
        await Run("CompactDownload0317: failed upgrade verifies a saved copy across repeated offline navigation and details", Compact0317VerifyAcrossOfflineNavigation);
        await Run("CompactDownload0317: busy copies keep exact offline rows and details consistent until reopened", Compact0317BusyCopyProjection);
        await Run("CompactDownload0317: cached work and single removal preserve a busy copy on marks and activation", Compact0317BusyCopyMarksAndActivation);
        await Run("CompactDownload0317: a guarded open denial removes the exact offline badge until rechecked",
            () => Compact0317GuardedOpenDenial(hasAlternate: false));
        await Run("CompactDownload0317: denying one exact copy leaves the other downloadable row playable",
            () => Compact0317GuardedOpenDenial(hasAlternate: true));
        await Run("CompactDownload0317: alias rows share the refusal and recovery of their physical copy",
            () => Compact0317GuardedOpenDenial(hasAlternate: true, hasAlias: true));
        await Run("CompactDownload0317: cache-only Retry updates marks of the open short queue", Compact0317QueueUsesLatestCopyCheck);
        await Run("CompactDownload0317: a late batch refusal cannot hide a subsequently verified exact copy", Compact0317StaleBatchCopyCheck);
        await Run("CompactDownload0317: reopening cards inherits a completed busy-copy check before its first paint", Compact0317BusyCopyDuringCardReopen);
        await Run("CompactDownload0317: successful retry refreshes open details after a busy saved copy",
            () => Compact0317RetryAfterBusyCopy(pauseOtherTrack: false));
        await Run("CompactDownload0317: pausing another retry keeps its completed upgrade visible in open details",
            () => Compact0317RetryAfterBusyCopy(pauseOtherTrack: true));
        await Run("CompactDownload0317: an open online list paints temporary refusal and the completed retry", async () =>
        {
            await Compact0317RetryAfterBusyCopy(pauseOtherTrack: false, keepOnlineList: true);
            await Compact0317RetryAfterBusyCopy(pauseOtherTrack: true, keepOnlineList: true);
            await Compact0317RetryAfterBusyCopy(pauseOtherTrack: false, keepOnlineList: true, largeOnlineList: true);
        });
        await Run("CompactDownload0317: a cached file disappearing after preflight joins only the actual new work", Compact0317MissingAfterPreflight);
        await Run("CompactDownload0317: a busy cached group preserves later downloads and can be retried", Compact0317CacheGroupRefusal);
        await Run("CompactDownload0317: pause resume and failed retry retain truthful counts and available actions", Compact0317PauseRetry);
        await Run("CompactDownload0317: a late storage failure keeps a cancelled entry paused", Compact0317PauseBeatsLateFailure);
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
        var snapshot = DownloadQueueStore.Capture("12345", fixture.Queue, AudioQuality.Excellent, false, playlist);
        var restored = await Task.Run(() => DownloadQueueStore.PrepareRestoreAsync(snapshot, fixture.Library, default))
            .WaitAsync(TimeSpan.FromSeconds(10));
        Assert(restored.Entries.Count == 3900 && restored.Entries.All(entry => entry is { State: DownloadEntryState.Completed, Track.IsDownloaded: true })
            && fixture.Requests.Count == 0, "large checkpoint restore lost verified cached audio or fetched media");
    }

    private static async Task Compact0317HeldCacheProofs()
    {
        using var fixture = new Compact0317Fixture("held-cache-proofs", 1);
        var index = Path.Combine(fixture.Library.DirectoryPath, "library.index.json");
        var audio = Path.Combine(fixture.Library.DirectoryPath, "cached.flac");
        var item = fixture.Library.Read().Single();
        var wrong = item with { Id = "different-id", RequestId = "different-id", Sha256 = new string('0', 64) };
        File.WriteAllText(index, JsonSerializer.Serialize(new[] { item, wrong }));
        using (var verified = await fixture.Library.HoldDownloadCacheBatchAsync(
            [new(DcTrack("cached-0")), new(DcTrack("different-id")), new(DcTrack("cached-0"), _ => false)], default))
        {
            Assert(verified[0].Item?.Id == "cached-0" && verified[1] is { Item: null, Unavailable: null }
                && verified[2] is { Item: null, Unavailable: null }, "batch reused proof for a different hash or declined quality");
            foreach (var path in new[] { index, audio })
                await Throws<IOException>(() =>
                {
                    using var writer = File.Open(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
                    return Task.CompletedTask;
                });
        }
        Assert(fixture.Library.FindCatalogPlaybackMark(DcTrack("cached-0")) is not null
            && fixture.Library.FindCatalogPlaybackMark(DcTrack("different-id")) is null,
            "a held checksum mismatch did not clear the exact damaged edition's offline badge");
        using var indexWriter = File.Open(index, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
        using var audioWriter = File.Open(audio, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
    }

    private static async Task Compact0317RejectedCacheRefresh(bool changeLength)
    {
        using var fixture = new Compact0317Fixture("cache-rejection-updates-card-" + changeLength, 1) { FailTrack = "cached-0" };
        var playlist = fixture.Playlist([]);
        await fixture.Library.RememberPlaylistAsync(playlist, AudioQuality.Excellent, default);
        await Call(fixture.Window, "ShowOfflinePlaylistsAsync");
        var cards = Control<ListBox>(fixture.Window, "CatalogList");
        var original = cards.Items.OfType<CatalogItem>().Single(); cards.SelectedItem = original;
        Assert(original.Subtitle.StartsWith("✓", StringComparison.Ordinal), "fixture card was not initially ready");
        var path = Path.Combine(fixture.Library.DirectoryPath, "cached.flac");
        var changed = File.ReadAllBytes(path);
        if (changeLength) changed = [.. changed, 1];
        else changed[^1] ^= 1;
        File.WriteAllBytes(path, changed);
        await Call(fixture.Window, "StartDownloadBatchAsync", playlist.Tracks, "Fixture", null!, false);
        await Until(() => cards.SelectedItem is CatalogItem selected && selected.Subtitle == "Скачано 0 из 1");
        Assert(cards.SelectedItem is CatalogItem updated && updated.Owner == original.Owner && updated.PlaylistKind == original.PlaylistKind
            && SaField<string>(fixture.Window, "_section") == "offline-playlists" && fixture.Queue.CanRetry
            && fixture.Requests.GetValueOrDefault("cached-0") == 1 && File.ReadAllBytes(path).SequenceEqual(changed),
            "failed replacement did not keep the selected updated card or changed the rejected original bytes");
    }

    private static async Task Compact0317MissingCacheRefresh()
    {
        using var fixture = new Compact0317Fixture("missing-cache-refresh", 1) { FailTrack = "cached-0", HeldFailure = "new-a" };
        var playlist = fixture.Playlist([]);
        await fixture.Library.RememberPlaylistAsync(playlist, AudioQuality.Excellent, default);
        var batch = Call(fixture.Window, "StartDownloadBatchAsync", new[] { DcTrack("new-a"), playlist.Tracks.Single() }, "Fixture", null!, false);
        try
        {
            await Until(() => fixture.Requests.ContainsKey("new-a"));
            await Call(fixture.Window, "ShowOfflinePlaylistsAsync");
            var cards = Control<ListBox>(fixture.Window, "CatalogList");
            var before = cards.Items.OfType<CatalogItem>().Single(); cards.SelectedItem = before;
            Assert(before.Subtitle.StartsWith("✓", StringComparison.Ordinal), "fixture card was not initially ready");
            var oldPath = Path.Combine(fixture.Library.DirectoryPath, "cached.flac");
            var movedPath = Path.Combine(fixture.DirectoryPath, "moved-fixture.flac");
            File.Move(oldPath, movedPath);
            fixture.Release("new-a");
            await batch;
            await Until(() => cards.SelectedItem is CatalogItem selected && selected.Subtitle == "Скачано 0 из 1");
            Assert(cards.SelectedItem is CatalogItem updated && updated.Owner == before.Owner && updated.PlaylistKind == before.PlaylistKind
                && fixture.Queue.CanRetry && fixture.Requests.GetValueOrDefault("cached-0") == 1 && !File.Exists(oldPath)
                && File.Exists(movedPath) && fixture.Library.Read().Count == 1, "the failed batch left a stale card or lost its indexed row");
        }
        finally { fixture.Release("new-a"); }
    }

    private static async Task Compact0317UpgradeFailureRefresh()
    {
        foreach (var state in new[] { "healthy", "missing", "corrupt", "busy" })
        {
            using var fixture = new Compact0317Fixture("upgrade-failure-refresh-" + state, 1) { HeldMetadataFailure = "cached-0" };
            var item = fixture.Library.Read().Single() with { Codec = "mp3", Bitrate = 128 };
            File.WriteAllText(Path.Combine(fixture.Library.DirectoryPath, "library.index.json"), JsonSerializer.Serialize(new[] { item }));
            Control<ComboBox>(fixture.Window, "QualityCombo").SelectedIndex = 0;
            var playlist = fixture.Playlist([]);
            await fixture.Library.RememberPlaylistAsync(playlist, AudioQuality.Excellent, default);
            var originalVersion = fixture.Library.DownloadVerificationRejectionVersion;
            var batch = Call(fixture.Window, "StartDownloadBatchAsync", playlist.Tracks, "Fixture", null!, false);
            try
            {
                await fixture.MetadataFailureStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await Call(fixture.Window, "ShowOfflinePlaylistsAsync");
                var cards = Control<ListBox>(fixture.Window, "CatalogList");
                var before = cards.Items.OfType<CatalogItem>().Single(); cards.SelectedItem = before;
                Assert(before.Subtitle.StartsWith("✓", StringComparison.Ordinal), "the lower-quality variant was not initially available");
                var originalPath = fixture.Library.ResolvePath(item.FileName);
                if (state == "missing") File.Move(originalPath, Path.Combine(fixture.DirectoryPath, "moved-lower-variant.flac"));
                if (state == "corrupt")
                {
                    var bytes = File.ReadAllBytes(originalPath);
                    bytes[^1] ^= 1;
                    File.WriteAllBytes(originalPath, bytes);
                }
                using (var busy = state == "busy" ? new FileStream(originalPath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite) : null)
                {
                    fixture.Release("cached-0");
                    await batch;
                    await Until(() => cards.SelectedItem is CatalogItem selected && !ReferenceEquals(selected, before));
                    Assert(cards.SelectedItem is CatalogItem updated && updated.Owner == before.Owner && updated.PlaylistKind == before.PlaylistKind
                        && (state == "healthy" ? updated.Subtitle.StartsWith("✓", StringComparison.Ordinal) : updated.Subtitle == "Скачано 0 из 1")
                        && fixture.Queue.CanRetry && fixture.Requests.IsEmpty && fixture.Library.Read().Single().Sha256 == item.Sha256
                        && (fixture.Library.DownloadVerificationRejectionVersion > originalVersion) == (state is "missing" or "corrupt"),
                        "the failed upgrade did not preserve the selection or confirm the lower-quality variant correctly");
                }
                if (state == "busy") Assert(fixture.Library.GetPlaylistStatus(playlist.Owner, playlist.Kind).Complete,
                    "temporary sharing was retained as a content rejection after release");
            }
            finally { fixture.Release("cached-0"); }
        }
    }

    private static async Task Compact0317VerifyAcrossOfflineNavigation()
    {
        foreach (var view in new[] { "repeat-cards", "corrupt-detail", "healthy-detail" })
        {
            using var fixture = new Compact0317Fixture("upgrade-navigate-" + view, 1) { HeldMetadataFailure = "cached-0" };
            var item = fixture.Library.Read().Single() with { Codec = "mp3", Bitrate = 128 };
            File.WriteAllText(Path.Combine(fixture.Library.DirectoryPath, "library.index.json"), JsonSerializer.Serialize(new[] { item }));
            Control<ComboBox>(fixture.Window, "QualityCombo").SelectedIndex = 0;
            var playlist = fixture.Playlist([]);
            await fixture.Library.RememberPlaylistAsync(playlist, AudioQuality.Excellent, default);
            var batch = Call(fixture.Window, "StartDownloadBatchAsync", playlist.Tracks, "Fixture", null!, false);
            using var releaseVerification = new ManualResetEventSlim(false);
            try
            {
                await fixture.MetadataFailureStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await Call(fixture.Window, "ShowOfflinePlaylistsAsync");
                var cards = Control<ListBox>(fixture.Window, "CatalogList");
                var initialCard = cards.Items.OfType<CatalogItem>().Single();
                Assert(initialCard.Subtitle.StartsWith("✓", StringComparison.Ordinal), "the fixture did not start with ready audio");
                var media = fixture.Library.ResolvePath(item.FileName);
                var healthy = view == "healthy-detail";
                if (!healthy)
                {
                    var bytes = File.ReadAllBytes(media);
                    bytes[^1] ^= 1;
                    File.WriteAllBytes(media, bytes);
                }
                if (view == "repeat-cards")
                {
                    var verifying = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    fixture.Library.BeforeVerifySavedAudioForChecks = path =>
                    {
                        if (path != media) return;
                        verifying.TrySetResult();
                        if (!releaseVerification.Wait(TimeSpan.FromSeconds(10))) throw new IOException("Fixture checksum pause expired.");
                    };
                    fixture.Release("cached-0");
                    await verifying.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    await Call(fixture.Window, "ShowOfflinePlaylistsAsync");
                    var selected = cards.Items.OfType<CatalogItem>().Single();
                    Assert(selected.Subtitle.StartsWith("✓", StringComparison.Ordinal), "the repeat read happened after checksum confirmation");
                    cards.SelectedItem = selected;
                    releaseVerification.Set();
                    await batch;
                    await Until(() => cards.SelectedItem is CatalogItem card && card.Subtitle == "Скачано 0 из 1");
                    Assert(cards.SelectedItem is CatalogItem current && current.Owner == selected.Owner && current.PlaylistKind == selected.PlaylistKind,
                        "checksum confirmation painted an old navigation or lost the current selection");
                }
                else
                {
                    await Call(fixture.Window, "ShowOfflinePlaylist", initialCard);
                    var list = Control<ListBox>(fixture.Window, "TracksList");
                    var selected = list.Items.OfType<TrackRow>().Single();
                    list.SelectedItem = selected;
                    fixture.Release("cached-0");
                    await batch;
                    await Until(() => list.SelectedItem is TrackRow row && !ReferenceEquals(row, selected) && row.Available == healthy);
                    Assert(list.SelectedItem is TrackRow current && current.Id == selected.Id && current.RequestId == selected.RequestId,
                        "the detail refresh lost the selected playlist occurrence");
                    await Call(fixture.Window, "ShowOfflinePlaylistsAsync");
                    var card = cards.Items.OfType<CatalogItem>().Single();
                    Assert(healthy ? card.Subtitle.StartsWith("✓", StringComparison.Ordinal) : card.Subtitle == "Скачано 0 из 1",
                        "later card navigation ignored the established checksum result");
                }
            }
            finally { releaseVerification.Set(); fixture.Release("cached-0"); }
        }
    }

    private static async Task Compact0317BusyCopyProjection()
    {
        foreach (var hasAlternate in new[] { false, true })
        {
            using var fixture = new Compact0317Fixture("busy-offline-details-" + hasAlternate, 1) { HeldMetadataFailure = "cached-0" };
            var primary = fixture.Library.Read().Single() with { Codec = "mp3", Bitrate = 192 };
            var alternate = primary with { RequestId = primary.Id + ":92", AlbumId = "92", FileName = "alternate.mp3", Bitrate = 128 };
            var busyPath = fixture.Library.ResolvePath(primary.FileName);
            var fallbackPath = fixture.Library.ResolvePath(alternate.FileName);
            if (hasAlternate) File.Copy(busyPath, fallbackPath);
            File.WriteAllText(Path.Combine(fixture.Library.DirectoryPath, "library.index.json"),
                JsonSerializer.Serialize(hasAlternate ? new[] { primary, alternate } : [primary]));
            Control<ComboBox>(fixture.Window, "QualityCombo").SelectedIndex = 0;
            var playlist = fixture.Playlist([]);
            await fixture.Library.RememberPlaylistAsync(playlist, AudioQuality.Excellent, default);
            var rejection = fixture.Library.DownloadVerificationRejectionVersion;
            var batch = Call(fixture.Window, "StartDownloadBatchAsync", playlist.Tracks, "Fixture", null!, false);
            try
            {
                await fixture.MetadataFailureStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await Call(fixture.Window, "ShowOfflinePlaylistsAsync");
                var card = Control<ListBox>(fixture.Window, "CatalogList").Items.OfType<CatalogItem>().Single();
                await Call(fixture.Window, "ShowOfflinePlaylist", card);
                var tracks = Control<ListBox>(fixture.Window, "TracksList");
                var previous = tracks.Items.OfType<TrackRow>().Single();
                tracks.SelectedItem = previous;
                Assert(previous.DownloadedPath == busyPath, "fixture did not initially resolve the primary copy");
                using (var held = new FileStream(busyPath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
                {
                    fixture.Release("cached-0");
                    await batch;
                    await Until(() => tracks.SelectedItem is TrackRow selected && !ReferenceEquals(previous, selected));
                    var detail = (TrackRow)tracks.SelectedItem;
                    Assert(detail.Available == hasAlternate && detail.CanPlay == hasAlternate && detail.IsDownloaded == hasAlternate
                        && detail.DownloadedPath == (hasAlternate ? fallbackPath : null) && detail.LocalPath == detail.DownloadedPath
                        && detail.LocalSourceRequestId == (hasAlternate ? alternate.RequestId : null)
                        && Control<TextBlock>(fixture.Window, "StatusText").Text.Contains($"доступно {(hasAlternate ? 1 : 0)} из 1", StringComparison.Ordinal),
                        "detail rows or counters lost the exact temporary copy check");
                    await Call(fixture.Window, "ShowOffline");
                    var exact = tracks.Items.OfType<TrackRow>().Single(row => row.LocalPath == busyPath);
                    Assert(!exact.Available && !exact.CanPlay && exact.DownloadedPath is null
                        && (!hasAlternate || tracks.Items.OfType<TrackRow>().Single(row => row.LocalPath == fallbackPath).CanPlay),
                        "the saved-file view substituted the other copy or repainted the temporarily busy file");
                }
                await Call(fixture.Window, "ShowOffline");
                Assert(tracks.Items.OfType<TrackRow>().Single(row => row.LocalPath == busyPath).CanPlay
                    && fixture.Library.DownloadVerificationRejectionVersion == rejection && fixture.Requests.IsEmpty,
                    "reopening after release did not recover the busy file or treated sharing as corruption");
            }
            finally { fixture.Release("cached-0"); }
        }
    }

    private static async Task Compact0317BusyCopyMarksAndActivation()
    {
        foreach (var removeOtherTrack in new[] { true, false })
        {
            using var fixture = new Compact0317Fixture("busy-mark-after-other-work-" + removeOtherTrack, 1) { HeldMetadataFailure = "cached-0" };
            var primary = fixture.Library.Read().Single() with { Codec = "mp3", Bitrate = 192, ConfirmedBitrate = 192 };
            var other = DcTrack("cached-1");
            var otherCopy = primary with { Id = other.Id, RequestId = other.RequestId, FileName = "other.mp3", Bitrate = 320, ConfirmedBitrate = 320 };
            var alternate = primary with { FileName = "reserve.mp3", Bitrate = 128, ConfirmedBitrate = 128 };
            var primaryPath = fixture.Library.ResolvePath(primary.FileName);
            var alternatePath = fixture.Library.ResolvePath(alternate.FileName);
            File.Copy(primaryPath, fixture.Library.ResolvePath(otherCopy.FileName));
            if (!removeOtherTrack) File.Copy(primaryPath, alternatePath);
            File.WriteAllText(Path.Combine(fixture.Library.DirectoryPath, "library.index.json"), JsonSerializer.Serialize(
                removeOtherTrack ? new[] { primary, otherCopy } : [primary, otherCopy, alternate]));
            Control<ComboBox>(fixture.Window, "QualityCombo").SelectedIndex = 0;
            var playlist = fixture.Playlist([other]);
            await fixture.Library.RememberPlaylistAsync(playlist, AudioQuality.Excellent, default);
            var online = playlist.Tracks.First();
            var batch = Call(fixture.Window, "StartDownloadBatchAsync", new[] { online }, "Fixture", null!, false);
            try
            {
                await fixture.MetadataFailureStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await Call(fixture.Window, "ShowOfflinePlaylistsAsync");
                var card = Control<ListBox>(fixture.Window, "CatalogList").Items.OfType<CatalogItem>().Single();
                await Call(fixture.Window, "ShowOfflinePlaylist", card);
                var list = Control<ListBox>(fixture.Window, "TracksList");
                var oldRow = list.Items.OfType<TrackRow>().Single(row => row.Id == primary.Id);
                using (File.Open(primaryPath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
                {
                    fixture.Release(primary.Id);
                    await batch;
                    await Until(() => list.Items.OfType<TrackRow>().Any(row => row.Id == primary.Id && !ReferenceEquals(row, oldRow)));
                    if (removeOtherTrack)
                    {
                        await Call(fixture.Window, "DeleteOfflineTrackAsync", list.Items.OfType<TrackRow>().Single(row => row.Id == other.Id));
                        Assert(!list.Items.OfType<TrackRow>().Single(row => row.Id == primary.Id).CanPlay,
                            "deleting a separate track repainted the earlier busy detail");
                        await Call(fixture.Window, "ShowOfflinePlaylist", card);
                        Assert(Control<TextBlock>(fixture.Window, "StatusText").Text.Contains("доступно 0 из 2", StringComparison.Ordinal),
                            "opening details while busy disagreed with the refreshed rows");
                    }
                    await Call(fixture.Window, "ShowOffline");
                    if (!removeOtherTrack)
                    {
                        await Call(fixture.Window, "StartDownloadBatchAsync", new[] { other }, "Fixture cache", null!, false);
                        SaInvoke(fixture.Window, "ApplyOfflineStates", (object)new[] { online });
                        Assert(online.DownloadedPath == alternatePath && fixture.Requests.IsEmpty,
                            "a cache-only batch revived the busy source or lost the verified alternative");
                    }
                    var exact = list.Items.OfType<TrackRow>().Single(row => row.LocalPath == primaryPath);
                    await Call(fixture.Window, "ActivateTrackRowAsync", list.Items.IndexOf(exact));
                    Assert(!exact.CanPlay && !exact.IsDownloaded && SaField<TrackRow?>(fixture.Window, "_current") is null,
                        "activating the exact busy row repainted or substituted it");
                }
                await Call(fixture.Window, "ShowOffline");
                Assert(list.Items.OfType<TrackRow>().Single(row => row.LocalPath == primaryPath).CanPlay,
                    "reopening after release did not confirm the exact copy");
            }
            finally { fixture.Release(primary.Id); }
        }
    }

    private static async Task Compact0317GuardedOpenDenial(bool hasAlternate, bool hasAlias = false)
    {
        using var fixture = new Compact0317Fixture($"guarded-open-denial-{hasAlternate}-{hasAlias}", 1);
        var item = fixture.Library.Read().Single();
        var alias = item with { Id = "alias-1", RequestId = "alias-1:91" };
        var path = fixture.Library.ResolvePath(item.FileName);
        var alternatePath = fixture.Library.ResolvePath("alternate.mp3");
        if (hasAlternate)
        {
            var audio = SilentMp3Seconds(3);
            File.WriteAllBytes(alternatePath, audio);
            var alternate = item with { FileName = "alternate.mp3", Codec = "mp3", Bitrate = 320, ConfirmedBitrate = 320,
                Bytes = audio.Length, Sha256 = Convert.ToHexString(SHA256.HashData(audio)) };
            File.WriteAllText(Path.Combine(fixture.Library.DirectoryPath, "library.index.json"),
                JsonSerializer.Serialize(hasAlias ? new[] { item, alternate, alias } : new[] { item, alternate }));
        }
        await Call(fixture.Window, "ShowOffline");
        var list = Control<ListBox>(fixture.Window, "TracksList");
        TrackRow ExactRow() => list.Items.OfType<TrackRow>().Single(candidate => candidate.LocalPath == path && candidate.Id == item.Id);
        TrackRow AliasRow() => list.Items.OfType<TrackRow>().Single(candidate => candidate.Id == alias.Id);
        var row = ExactRow();
        Assert(row.CanPlay, "fixture did not start with an indexed copy and no refusal");
        var oldProjectionRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseOldProjection = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reads = 0;
        fixture.Window.OfflineViewAfterRead = token =>
        {
            if (Interlocked.Increment(ref reads) != 1) return Task.CompletedTask;
            oldProjectionRead.TrySetResult();
            return releaseOldProjection.Task.WaitAsync(token);
        };
        try
        {
            using (File.Open(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
            {
                await Call(fixture.Window, "ActivateTrackRowAsync", list.Items.IndexOf(row));
                await oldProjectionRead.Task.WaitAsync(TimeSpan.FromSeconds(5));
                SaInvoke(fixture.Window, "RefreshDownloadedMarks");
                Assert(!row.CanPlay && !row.IsDownloaded && !ExactRow().CanPlay,
                    "guarded access refused the copy but left its playable badge");
                if (hasAlias)
                {
                    Assert(!AliasRow().CanPlay, "the shared busy copy kept a mark under its other identifier");
                    await Call(fixture.Window, "ShowOffline");
                    Assert(!ExactRow().CanPlay && !AliasRow().CanPlay, "reloading reapplied the busy physical file to its alias");
                }
                if (hasAlternate)
                {
                    var alternate = list.Items.OfType<TrackRow>().Single(candidate => candidate.LocalPath == alternatePath);
                    Assert(alternate.CanPlay && alternate.DownloadedPath == alternatePath, "denying one copy hid the unaffected alternate row");
                    await Call(fixture.Window, "ActivateTrackRowAsync", list.Items.IndexOf(alternate));
                    var player = SaField<NativePlayer>(fixture.Window, "_player");
                    Assert(player.IsPlaying && player.SourceLocalPath == alternatePath && !ExactRow().CanPlay,
                        "activating the unaffected alternate substituted another source or repainted the busy copy");
                }
            }
            await Call(fixture.Window, "ShowOffline");
            Assert(ExactRow().CanPlay && fixture.Requests.IsEmpty,
                "reopening after the release inherited the older sharing refusal");
            if (hasAlias) Assert(AliasRow().DownloadedPath == path, "the alias did not recover with its shared verified file");
            releaseOldProjection.TrySetResult();
            await Until(() => SaField<CancellationTokenSource?>(fixture.Window, "_offlineDownloadRefresh") is null);
            Assert(ExactRow().CanPlay, "the superseded projection hid the rechecked copy");
        }
        finally { releaseOldProjection.TrySetResult(); }
    }

    private static async Task Compact0317QueueUsesLatestCopyCheck()
    {
        using var fixture = new Compact0317Fixture("copy-check-in-queue", 1);
        var item = fixture.Library.Read().Single();
        var path = fixture.Library.ResolvePath(item.FileName);
        var exact = fixture.Library.Tracks().Single();
        SaField<PlaybackQueue>(fixture.Window, "_playbackQueue").SetItems([exact], exact);
        SaInvoke(fixture.Window, "ShowQueueCore", false);
        var list = Control<ListBox>(fixture.Window, "TracksList");
        list.SelectedItem = exact;
        fixture.Window.UpdateLayout();
        var cover = SaVisuals<Button>(list).Single(button => button.Name == "TrackCoverPlayButton" && ReferenceEquals(button.DataContext, exact));
        var listen = Control<Button>(fixture.Window, "PlaySelectionButton");
        using (File.Open(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            await Call(fixture.Window, "StartDownloadBatchAsync", fixture.Playlist([]).Tracks, "Fixture", null!, false);
            Assert(fixture.Queue.CanRetry && !exact.IsDownloaded && ReferenceEquals(list.SelectedItem, exact) && !cover.IsEnabled && !listen.IsEnabled,
                "the short queue was marked before the negative copy check arrived");
        }
        await Call(fixture.Window, "RetryFailedDownloads");
        Assert(!fixture.Queue.CanRetry && exact.DownloadedPath == path && ReferenceEquals(list.SelectedItem, exact)
            && cover.IsEnabled && listen.IsEnabled && fixture.Requests.IsEmpty, "the cache-only retry did not update both controls for the same row");
    }

    private static async Task Compact0317StaleBatchCopyCheck()
    {
        foreach (var otherRefusal in new[] { false, true })
        {
            using var fixture = new Compact0317Fixture("stale-batch-copy-check-" + otherRefusal, otherRefusal ? 2 : 1);
            var original = fixture.Library.Read().OrderBy(item => item.Id).ToArray();
            var bytes = SilentMp3Seconds(3);
            var sha = Convert.ToHexString(SHA256.HashData(bytes));
            var copies = original.Select(item => item with { FileName = item.Id + ".mp3", Codec = "mp3", Bitrate = 320,
                ConfirmedBitrate = 320, Bytes = bytes.Length, Sha256 = sha }).ToArray();
            foreach (var copy in copies) File.WriteAllBytes(fixture.Library.ResolvePath(copy.FileName), bytes);
            File.WriteAllText(Path.Combine(fixture.Library.DirectoryPath, "library.index.json"), JsonSerializer.Serialize(copies));
            var first = copies[0];
            var path = fixture.Library.ResolvePath(first.FileName);
            await Call(fixture.Window, "ShowOffline");
            var list = Control<ListBox>(fixture.Window, "TracksList");
            TrackRow FirstRow() => list.Items.OfType<TrackRow>().Single(row => row.Id == first.Id);
            using var other = otherRefusal ? File.Open(fixture.Library.ResolvePath(copies[1].FileName), FileMode.Open, FileAccess.Read, FileShare.None) : null;
            if (otherRefusal)
            {
                var second = list.Items.OfType<TrackRow>().Single(row => row.Id == copies[1].Id);
                await Call(fixture.Window, "ActivateTrackRowAsync", list.Items.IndexOf(second));
            }
            var verified = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var publish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            fixture.Window.OfflineCopyChecksBeforePublishForChecks = token => { verified.TrySetResult(); return publish.Task.WaitAsync(token); };
            Task? batch = null;
            try
            {
                using (File.Open(path, FileMode.Open, FileAccess.Read, FileShare.None))
                {
                    await Call(fixture.Window, "ActivateTrackRowAsync", list.Items.IndexOf(FirstRow()));
                    Assert(!FirstRow().CanPlay, "fixture did not start with a temporary refusal for the exact copy");
                    batch = Call(fixture.Window, "StartDownloadBatchAsync", new[] { fixture.Playlist([]).Tracks[0] }, "Fixture", null!, false);
                    await verified.Task.WaitAsync(TimeSpan.FromSeconds(5));
                }
                await Call(fixture.Window, "ActivateTrackRowAsync", list.Items.IndexOf(FirstRow()));
                Assert(SaField<NativePlayer>(fixture.Window, "_player").SourceLocalPath == path && FirstRow().CanPlay,
                    "the newer guarded exact open did not clear its refusal");
                publish.TrySetResult();
                await batch.WaitAsync(TimeSpan.FromSeconds(5));
                Assert(FirstRow().CanPlay && FirstRow().DownloadedPath == path
                    && (!otherRefusal || !list.Items.OfType<TrackRow>().Single(row => row.Id == copies[1].Id).CanPlay),
                    "late verification restored an obsolete refusal or disturbed the independent refusal");
            }
            finally { publish.TrySetResult(); if (batch is not null) await batch.WaitAsync(TimeSpan.FromSeconds(5)); }
        }
    }

    private static async Task Compact0317BusyCopyDuringCardReopen()
    {
        using var fixture = new Compact0317Fixture("busy-reopen-before-paint", 1) { HeldMetadataFailure = "cached-0" };
        var item = fixture.Library.Read().Single() with { Codec = "mp3", Bitrate = 128 };
        File.WriteAllText(Path.Combine(fixture.Library.DirectoryPath, "library.index.json"), JsonSerializer.Serialize(new[] { item }));
        Control<ComboBox>(fixture.Window, "QualityCombo").SelectedIndex = 0;
        var playlist = fixture.Playlist([]);
        await fixture.Library.RememberPlaylistAsync(playlist, AudioQuality.Excellent, default);
        var batch = Call(fixture.Window, "StartDownloadBatchAsync", playlist.Tracks, "Fixture", null!, false);
        var oldReadCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var permitOldPaint = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            await fixture.MetadataFailureStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Call(fixture.Window, "ShowOfflinePlaylistsAsync");
            using var held = new FileStream(fixture.Library.ResolvePath(item.FileName), FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
            var reads = 0;
            fixture.Window.OfflineViewAfterRead = token =>
            {
                if (Interlocked.Increment(ref reads) != 1) return Task.CompletedTask;
                oldReadCompleted.TrySetResult();
                return permitOldPaint.Task.WaitAsync(token);
            };
            fixture.Release("cached-0");
            await batch;
            await oldReadCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Call(fixture.Window, "ShowOfflinePlaylistsAsync");
            var cards = Control<ListBox>(fixture.Window, "CatalogList");
            Assert(cards.Items.OfType<CatalogItem>().Single().Subtitle == "Скачано 0 из 1",
                "reopening cards read only file attributes after the temporary verification had finished");
            permitOldPaint.TrySetResult();
            await Until(() => SaField<CancellationTokenSource?>(fixture.Window, "_offlineDownloadRefresh") is null);
            Assert(cards.Items.OfType<CatalogItem>().Single().Subtitle == "Скачано 0 из 1",
                "the superseded offline read repainted the current cards");
        }
        finally { permitOldPaint.TrySetResult(); fixture.Release("cached-0"); }
    }

    private static async Task Compact0317RetryAfterBusyCopy(bool pauseOtherTrack, bool keepOnlineList = false, bool largeOnlineList = false)
    {
        using var fixture = new Compact0317Fixture($"retry-after-busy-copy-{pauseOtherTrack}-{keepOnlineList}-{largeOnlineList}", 1) { HeldMetadataFailure = "cached-0" };
        var prior = fixture.Library.Read().Single() with { Codec = "mp3", Bitrate = 128 };
        File.WriteAllText(Path.Combine(fixture.Library.DirectoryPath, "library.index.json"), JsonSerializer.Serialize(new[] { prior }));
        Control<ComboBox>(fixture.Window, "QualityCombo").SelectedIndex = 0;
        var other = DcTrack("new-b");
        if (pauseOtherTrack) fixture.FailTrack = other.Id;
        var playlist = fixture.Playlist(pauseOtherTrack ? [other] : []);
        if (largeOnlineList) playlist = playlist with { Tracks = Enumerable.Repeat(playlist.Tracks[0], 257).ToArray() };
        await fixture.Library.RememberPlaylistAsync(playlist, AudioQuality.Excellent, default);
        var batch = Call(fixture.Window, "StartDownloadBatchAsync", playlist.Tracks, "Fixture", null!, false);
        try
        {
            await fixture.MetadataFailureStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (keepOnlineList) fixture.Show(playlist);
            else
            {
                await Call(fixture.Window, "ShowOfflinePlaylistsAsync");
                await Call(fixture.Window, "ShowOfflinePlaylist", Control<ListBox>(fixture.Window, "CatalogList").Items.OfType<CatalogItem>().Single());
            }
            var tracks = Control<ListBox>(fixture.Window, "TracksList");
            tracks.SelectedItem = tracks.Items.OfType<TrackRow>().First(row => row.Id == prior.Id);
            using (File.Open(fixture.Library.ResolvePath(prior.FileName), FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
            {
                fixture.Release(prior.Id);
                await batch;
                if (keepOnlineList)
                    await Until(() => tracks.SelectedItem is TrackRow { Available: true, IsDownloaded: false }
                        && Control<TextBlock>(fixture.Window, "PlaylistDownloadText").Text == $"Скачано 0 из {playlist.Tracks.Count}");
                else await Until(() => tracks.SelectedItem is TrackRow { Available: false, CanPlay: false });
                Assert(fixture.Queue.CanRetry, "the busy lower copy did not leave an upgrade to retry");
            }
            var unavailable = (TrackRow)tracks.SelectedItem;
            fixture.HeldMetadataFailure = null;
            fixture.FailTrack = null;
            var retry = Call(fixture.Window, "RetryFailedDownloads");
            if (pauseOtherTrack)
            {
                await Until(() => fixture.Requests.GetValueOrDefault(other.Id) == 2);
                Assert(fixture.Queue.Entries.Single(entry => entry.Track.Id == prior.Id).State == DownloadEntryState.Completed,
                    "the prior upgrade did not commit before the second retry started");
                await Call(fixture.Window, "DownloadSelectedAsync");
            }
            await retry.WaitAsync(TimeSpan.FromSeconds(10));
            var upgraded = fixture.Library.Read().Single(item => item.Sha256 != prior.Sha256);
            var upgradedPath = fixture.Library.ResolvePath(upgraded.FileName);
            if (keepOnlineList)
            {
                await Until(() => ReferenceEquals(tracks.SelectedItem, unavailable) && unavailable.DownloadedPath == upgradedPath
                    && Control<TextBlock>(fixture.Window, "PlaylistDownloadText").Text == (pauseOtherTrack ? "Скачано 1 из 2" : "Все треки доступны офлайн"));
                Assert(SaField<string>(fixture.Window, "_section") == "playlist-detail", "refreshing the heading changed the online view");
            }
            else await Until(() => tracks.SelectedItem is TrackRow row && !ReferenceEquals(row, unavailable) && row.LocalPath == upgradedPath);
            Assert(tracks.SelectedItem is TrackRow { Available: true, CanPlay: true } current && current.DownloadedPath == upgradedPath
                && (keepOnlineList || SaField<string>(fixture.Window, "_section") == "offline-playlist-detail"
                    && Control<TextBlock>(fixture.Window, "StatusText").Text.Contains($"доступно 1 из {playlist.Tracks.Count}", StringComparison.Ordinal))
                && !fixture.Queue.CanRetry && fixture.Requests.GetValueOrDefault(prior.Id) == 1,
                "retry committed new audio but left the open offline detail on its earlier sharing refusal");
            if (pauseOtherTrack)
                Assert(fixture.Queue.CanResume && fixture.Queue.Entries.Single(entry => entry.Track.Id == other.Id).State == DownloadEntryState.Paused
                    && fixture.Library.Find(other) is null, "rechecking the saved upgrade resumed the paused retry");
        }
        finally { fixture.Release(prior.Id); fixture.Release(other.Id); }
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

    private static async Task Compact0317PauseBeatsLateFailure()
    {
        foreach (var pause in new[] { true, false })
        {
            using var fixture = new Compact0317Fixture("late-storage-failure-" + pause, 0);
            var track = DcTrack("new-a");
            fixture.Library.SaveCheckpoint = stage =>
            {
                if (stage != "save-before-receipt") return;
                if (pause) fixture.Window.Dispatcher.Invoke(() => SaInvoke(fixture.Window, "PauseDownloadQueue"));
                throw new IOException("fixture late storage failure");
            };
            var work = Call(fixture.Window, "StartDownloadBatchAsync", new[] { track }, "Fixture", null!, false);
            await Until(() => fixture.Requests.ContainsKey(track.Id));
            fixture.Release(track.Id);
            await work.WaitAsync(TimeSpan.FromSeconds(5));
            Assert(fixture.Queue.CanResume == pause && fixture.Queue.CanRetry != pause
                && fixture.Library.Read().Count == 0 && fixture.Requests.GetValueOrDefault(track.Id) == 1,
                "late storage failure overrode cancellation or was not reported without a pause");
            fixture.Library.SaveCheckpoint = null;
            await Call(fixture.Window, pause ? "ResumeDownloadQueue" : "RetryFailedDownloads");
            Assert(fixture.Queue.Entries.Single().State == DownloadEntryState.Completed && fixture.Requests.GetValueOrDefault(track.Id) == 2,
                "the appropriate continuation did not restart the uncommitted audio");
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

    private static async Task Compact0317CacheGroupRefusal()
    {
        using var fixture = new Compact0317Fixture("cache-group-refusal", 1);
        var source = fixture.Playlist([]);
        fixture.Show(source with { Tracks = [DcTrack("new-a"), source.Tracks[0], DcTrack("new-b")] });
        var task = Call(fixture.Window, "DownloadPlaylistAsync");
        await Until(() => fixture.Requests.ContainsKey("new-a"));
        FileStream? held = null;
        var first = fixture.Queue.Entries[0]; var cached = fixture.Queue.Entries[1];
        first.PropertyChanged += (_, _) =>
        {
            if (first.State == DownloadEntryState.Completed && held is null)
                held = File.Open(Path.Combine(fixture.Library.DirectoryPath, "library.index.json"), FileMode.Open, FileAccess.Read, FileShare.None);
        };
        cached.PropertyChanged += (_, _) => { if (cached.State == DownloadEntryState.Failed) held?.Dispose(); };
        try
        {
            fixture.Release("new-a");
            await Until(() => fixture.Requests.ContainsKey("new-b"));
            fixture.Release("new-b");
            await task;
            Assert(held is not null && cached.State == DownloadEntryState.Failed && fixture.Queue.CanRetry
                && fixture.Library.Read().Any(item => item.Id == "new-b"), "the cached group aborted an unrelated later download");
            await Call(fixture.Window, "RetryFailedDownloads");
            Assert(cached.State == DownloadEntryState.Completed && fixture.Requests.Count == 2 && !fixture.Requests.ContainsKey("cached-0"),
                "cache retry ignored the verified file or downloaded it again");
        }
        finally { held?.Dispose(); }
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
        internal string? HeldFailure;
        internal string? HeldMetadataFailure;
        internal TaskCompletionSource MetadataFailureStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
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
            var api = new FakeHandler((request, token) =>
            {
                if (HeldMetadataFailure is { } failed && request.RequestUri!.AbsolutePath == "/tracks/" + failed + "/download-info")
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
                Assert(request.RequestUri!.AbsolutePath == "/get-file-info", "unexpected compact-download API route");
                var id = Uri.UnescapeDataString(request.RequestUri.Query.TrimStart('?').Split('&').Select(p => p.Split('=', 2)).Single(p => p[0] == "trackId")[1]);
                if (id == HeldMetadataFailure)
                {
                    MetadataFailureStarted.TrySetResult();
                    return HoldFailureAsync(id, token);
                }
                return Task.FromResult(Json("{\"result\":{\"downloadInfo\":{\"quality\":\"lossless\",\"codec\":\"mp3\",\"bitrate\":320,\"transport\":\"raw\",\"url\":\"https://s1.vlx.yandex.net/" + id + ".mp3\"}}}"));
            });
            var downloads = new FakeHandler((request, token) =>
            {
                var id = Uri.UnescapeDataString(Path.GetFileNameWithoutExtension(request.RequestUri!.AbsolutePath));
                Requests.AddOrUpdate(id, 1, (_, count) => count + 1);
                if (id == HeldFailure) return HoldFailureAsync(id, token);
                if (id == FailTrack) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
                Interlocked.Increment(ref _active);
                var bytes = id == "new-a" ? SilentMp3Seconds(3) : SilentMp3Bytes(262144);
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
        private async Task<HttpResponseMessage> HoldFailureAsync(string id, CancellationToken token)
        {
            await _released.GetOrAdd(id, _ => new(TaskCreationOptions.RunContinuationsAsynchronously)).Task.WaitAsync(token);
            return new(HttpStatusCode.ServiceUnavailable);
        }
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
