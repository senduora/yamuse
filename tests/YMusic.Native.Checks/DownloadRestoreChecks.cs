using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Controls;
using YMusic.Native;

internal static partial class Program
{
    private static async Task CheckDownloadRestore()
    {
        await Run("DownloadRestore: metadata-only immutable account-bound snapshot and playlist membership roundtrip", DrMetadata);
        await Run("DownloadRestore: crash reconciliation pauses unfinished work and validates ready files", DrCrash);
        await Run("DownloadRestore: close reopen and explicit resume retry preserve files and playlist context", DrWindowLifecycle);
        await Run("DownloadRestore: logout and account generation reject stale restored work and writes", DrAccount);
        await Run("DownloadRestore: stale load or preparation cannot replace running or completed manual work", DrManualAdmission);
        await Run("DownloadRestore: admission before pending-worker wait rejects obsolete restoration", DrPendingAdmission);
        await Run("DownloadRestore: clear close and stale preparation failures invalidate held restoration", DrRestoreInvalidation);
        await Run("DownloadRestore: invalid oversized and unknown data remain untouched", DrInvalid);
        await Run("DownloadRestore: corrupt offline index blocks restoration without overwriting evidence", DrCorruptIndex);
        await Run("DownloadRestore: newest atomic write and clear defeat outstanding background saves", DrWrites);
        await Run("DownloadRestore: full uninstall removes the owned queue snapshot", DrUninstall);
    }
    private static string DrDirectory(string name)
    { var path = Path.Combine(_directory, "download-restore-" + name); Directory.CreateDirectory(path); return path; }
    private static DownloadQueueSnapshot DrSnapshot(string uid = "fixture", string label = "Очередь")
    {
        var queue = new DownloadQueueState(); queue.Replace([DcTrack("1"), DcTrack("2"), DcTrack("3", false)], label);
        queue.Entries[0].Complete(DownloadCenterFixture.Media.Length);
        queue.Entries[1].Begin(); queue.Entries[1].Progress(new(8192, 16384));
        queue.Entries[2].SetState(DownloadEntryState.Failed, DownloadFailure.Describe(new HttpRequestException("token=SECRET")));
        return DownloadQueueStore.Capture(uid, queue, AudioQuality.Optimal, false,
            new PlaylistDetails("owner-1", "123", "Fixture playlist", "", 7, [queue.Entries[2].Track, queue.Entries[0].Track, queue.Entries[0].Track, queue.Entries[1].Track]));
    }
    private static Task DrMetadata()
    {
        var directory = DrDirectory("metadata"); var store = new DownloadQueueStore(directory);
        var track = new TrackRow { Id = "1", RequestId = "1:2", Title = "Fixture", Artist = "Artist", AlbumId = "2", Available = true,
            DurationMs = 1000, Artists = [new("55", "Artist")], CoverUri = "https://cdn.invalid/?token=SECRET", LocalPath = "C:\\private\\secret.mp3", DownloadedPath = "C:\\private\\secret.mp3" };
        var queue = new DownloadQueueState(); queue.Replace([track], "Fixture");
        queue.Entries[0].SetState(DownloadEntryState.Failed, "https://signed.invalid/?token=SECRET C:\\private");
        var snapshot = DownloadQueueStore.Capture("fixture", queue, AudioQuality.Excellent, false, null);
        store.Save(snapshot);
        var json = File.ReadAllText(Path.Combine(directory, DownloadQueueStore.FileName));
        Assert(!json.Contains("SECRET") && !json.Contains("private") && !json.Contains("http") && !json.Contains("Path") && !json.Contains("coverUri"), "locations or raw exception data entered the queue file");
        var saved = store.Load("fixture")!;
        Assert(saved.Entries[0].Track.AlbumId == "2" && saved.Entries[0].Track.Artists.Single().Id == "55"
            && saved.Entries[0].Error == DownloadFailure.RestorableReason("unsafe"), "metadata or safe error did not roundtrip");
        Assert(store.Load(null) is null && store.LastReadStatus == DownloadQueueReadStatus.Disabled
            && store.Load("other") is null && store.LastReadStatus == DownloadQueueReadStatus.OtherAccount, "queue ownership was inferred from an absent or different UID");
        try { ((IList<DownloadEntrySnapshot>)saved.Entries)[0] = saved.Entries[0]; throw new InvalidOperationException("mutable entries"); } catch (NotSupportedException) { }
        try { ((IList<ArtistReference>)saved.Entries[0].Track.Artists)[0] = new("99", "changed"); throw new InvalidOperationException("mutable artists"); } catch (NotSupportedException) { }
        var playlist = DrSnapshot(); store.Save(playlist);
        Assert(store.Load("fixture")!.Playlist!.TrackIds.SequenceEqual(new[] { "3", "1", "1", "2" }), "playlist order or duplicate occurrences were lost");
        return Task.CompletedTask;
    }
    private static async Task DrCrash()
    {
        var directory = DrDirectory("crash");
        using var offline = new OfflineLibrary(Path.Combine(directory, "Music"), new AudioBuffer(Path.Combine(directory, "buffer"),
            new FakeHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(DownloadCenterFixture.Media) }))));
        var snapshot = DrSnapshot();
        var saved = await offline.SaveAsync(DcTrack("1"), new(new Uri("https://s1.vlx.yandex.net/1.mp3"), "mp3", 320), CancellationToken.None);
        var restored = DownloadQueueStore.PrepareRestore(snapshot, offline);
        var queue = new DownloadQueueState(); queue.Restore(restored);
        Assert(!queue.Running && !queue.Suspended && queue.CanResume && queue.CanRetry, "restored queue started automatically or lost explicit actions");
        Assert(queue.Entries.Select(e => e.State).SequenceEqual([DownloadEntryState.Completed, DownloadEntryState.Paused, DownloadEntryState.Failed])
            && queue.Entries[1].Bytes == 0 && queue.Entries[1].TotalBytes is null && queue.Entries[1].Attempts == 1, "active crash state or partial-byte claim survived restart");
        Assert(queue.Entries[0].Track.IsDownloaded && queue.Entries[0].Track.DownloadedPath == offline.ResolvePath(saved.FileName), "ready mark was not restored from the validated index");
        Assert(restored.Playlist!.Tracks.Select(t => t.Id).SequenceEqual(["3", "1", "1", "2"]), "restored playlist context changed");
        File.Delete(offline.ResolvePath(saved.FileName));
        Assert(DownloadQueueStore.PrepareRestore(snapshot, offline).Entries[0] is { State: DownloadEntryState.Paused, Bytes: 0, Track.IsDownloaded: false }, "missing ready file was trusted from queue JSON");
        var favorites = new DownloadQueueState(); favorites.Replace([DcTrack("1"), DcTrack("2")], "Мне нравится", true);
        favorites.SetCollectionLike("2", false);
        var favoriteSnapshot = DownloadQueueStore.Capture("fixture", favorites, AudioQuality.Optimal, true,
            new("local:favorites", "liked", "Мне нравится", "", 0, favorites.Entries.Select(e => e.Track).ToArray()));
        var favoriteRestored = new DownloadQueueState(); favoriteRestored.Restore(DownloadQueueStore.PrepareRestore(favoriteSnapshot, offline));
        Assert(favoriteRestored.FavoritesBatch && favoriteRestored.Entries[1].SkipFavoritesDownload
            && favoriteRestored.Entries[1].RemovedFromCollection && favoriteRestored.BeginRun(false).Length == 1, "favorites exclusion was not retained");
    }
    private static void DrEnable(DownloadCenterFixture fixture)
    {
        typeof(MainWindow).GetField("_restore", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(fixture.Window, true);
        typeof(MainWindow).GetField("_account", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(fixture.Window, new Account("fixture", "Fixture", false));
        SaInvoke(fixture.Window, "InitializeDownloadPersistence");
    }
    private static async Task DrWindowLifecycle()
    {
        byte[] readyHash; string readyPath; string directory;
        using (var first = new DownloadCenterFixture("restore-lifecycle", 2) { HoldFirstAttempts = true, FailTrack = "4" })
        {
            DrEnable(first); directory = first.DirectoryPath;
            var rows = new[] { DcTrack("1"), DcTrack("2"), DcTrack("3", false), DcTrack("4") };
            var playlist = new PlaylistDetails("fixture-owner", "345", "Saved playlist", "", 12, rows);
            var run = Call(first.Window, "StartDownloadBatchAsync", rows, "Восстановление", playlist, false);
            await Until(() => first.Queue.Entries[0].State == DownloadEntryState.Completed && first.Queue.Entries[3].State == DownloadEntryState.Failed && first.Active == 1);
            await Call(first.Window, "PersistDownloadQueueAsync", true);
            var onDisk = new DownloadQueueStore(directory).Load("fixture")!;
            Assert(onDisk.Entries[1].State is DownloadEntryState.Downloading or DownloadEntryState.Resolving, "running queue was not checkpointed for crash recovery");
            SaInvoke(first.Window, "PauseDownloadQueue"); await run.WaitAsync(TimeSpan.FromSeconds(5));
            readyPath = first.Library.ResolvePath(first.Library.Find(rows[0])!.FileName); readyHash = SHA256.HashData(File.ReadAllBytes(readyPath));
            // The normal window close hook saves before account suspension.
        }
        using var second = new DownloadCenterFixture("restore-lifecycle", 2);
        DrEnable(second);
        await Call(second.Window, "RestoreDownloadQueueAsync", "fixture");
        Assert(second.Requests.Count == 0 && second.Active == 0 && second.Queue.CanResume && second.Queue.CanRetry
            && !second.Queue.Running && SaField<bool>(second.Window, "_downloadsAutoSuspended"), "reopening started requests or lost user-gated continuation");
        Assert(second.Queue.Entries.Select(e => e.State).SequenceEqual([DownloadEntryState.Completed, DownloadEntryState.Paused, DownloadEntryState.Unavailable, DownloadEntryState.Failed])
            && second.Queue.Entries[3].Error.Contains("Аудиосервер"), "terminal states or failure reason changed across close");
        var resume = SaVisuals<Button>(second.Window.DownloadCenterView).Single(b => b.Name == "DownloadResumeButton");
        Assert(resume.IsEnabled, "restored queue has no enabled Continue button");
        await Call(second.Window, "ResumeDownloadQueue");
        Assert(second.Requests.GetValueOrDefault("1") == 0 && second.Requests.GetValueOrDefault("2") == 1
            && second.Requests.GetValueOrDefault("3") == 0 && second.Requests.GetValueOrDefault("4") == 0, "resume repeated completed/unavailable/failed tasks");
        await Call(second.Window, "RetryFailedDownloads");
        Assert(second.Requests.GetValueOrDefault("4") == 1 && second.Requests.GetValueOrDefault("1") == 0
            && readyHash.SequenceEqual(SHA256.HashData(File.ReadAllBytes(readyPath))), "retry changed a ready file or skipped a restored error");
        Assert(second.Library.GetPlaylists().Single(p => p.Owner == "fixture-owner" && p.Kind == "345") is { Revision: 12, Tracks.Count: 4 }, "resume forgot the original offline playlist snapshot");
        var reopened = new DownloadQueueStore(directory).Load("fixture")!;
        Assert(reopened.Entries.Count(e => e.State == DownloadEntryState.Completed) == 3, "terminal completion was not checkpointed");
    }
    private static async Task DrAccount()
    {
        using var fixture = new DownloadCenterFixture("restore-account", 1);
        DrEnable(fixture);
        var store = new DownloadQueueStore(fixture.DirectoryPath); store.Save(DrSnapshot());
        var restore = Call(fixture.Window, "RestoreDownloadQueueAsync", "fixture");
        fixture.Window.SignOut();
        await restore;
        Assert(!File.Exists(Path.Combine(fixture.DirectoryPath, DownloadQueueStore.FileName)) && !fixture.Queue.CanResume && fixture.Queue.Suspended, "logout restored stale work or retained its file");
        typeof(MainWindow).GetField("_session", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(fixture.Window, FakeSession);
        typeof(MainWindow).GetField("_account", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(fixture.Window, new Account("other", "Other", false));
        await Call(fixture.Window, "RestoreDownloadQueueAsync", "other");
        await Call(fixture.Window, "ResumeDownloadQueue"); await Call(fixture.Window, "RetryFailedDownloads");
        Assert(fixture.Requests.Count == 0 && fixture.Queue.Entries.Count == 0, "different account inherited previous tasks");
    }
    private static async Task DrManualAdmission()
    {
        foreach (var preparation in new[] { false, true })
        foreach (var complete in new[] { false, true })
        {
            using var fixture = new DownloadCenterFixture($"restore-admission-{preparation}-{complete}", 1)
            { HoldFirstAttempts = !complete };
            DrEnable(fixture);
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var oldSnapshot = DrSnapshot(label: "Obsolete snapshot");
            fixture.Window.DownloadQueueRestoreReadAsync = async _ =>
            {
                if (!preparation) { entered.SetResult(); await release.Task; }
                return oldSnapshot;
            };
            if (preparation) fixture.Window.DownloadQueueRestorePrepareAsync = async snapshot =>
            {
                entered.SetResult(); await release.Task;
                return DownloadQueueStore.PrepareRestore(snapshot, fixture.Library);
            };
            var restore = Call(fixture.Window, "RestoreDownloadQueueAsync", "fixture");
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var run = fixture.Start([DcTrack("4")]);
            var manualEntry = fixture.Queue.Entries.Single();
            if (complete) await run.WaitAsync(TimeSpan.FromSeconds(5));
            else await Until(() => fixture.Active == 1 && fixture.Queue.Running);
            release.SetResult();
            await restore.WaitAsync(TimeSpan.FromSeconds(5));
            Assert(ReferenceEquals(fixture.Queue.Entries.Single(), manualEntry) && fixture.Queue.Label == "Тестовая очередь"
                && fixture.Queue.Running == !complete && !SaField<bool>(fixture.Window, "_downloadPersistenceWriteBlocked"),
                "obsolete restore replaced or blocked manual work after " + (preparation ? "preparation" : "load"));
            if (!complete) SaInvoke(fixture.Window, "PauseDownloadQueue");
            await run.WaitAsync(TimeSpan.FromSeconds(5));
            DrAssertManualSettled(fixture, manualEntry, complete);
            var saved = new DownloadQueueStore(fixture.DirectoryPath).Load("fixture")!;
            Assert(saved.Label == "Тестовая очередь" && saved.Entries.Single().Track.Id == "4",
                "obsolete restoration replaced the manual queue checkpoint");
        }
    }
    private static async Task DrPendingAdmission()
    {
        using var fixture = new DownloadCenterFixture("restore-pending-admission", 1);
        DrEnable(fixture);
        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        typeof(MainWindow).GetField("_downloadTask", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(fixture.Window, pending.Task);
        var reads = 0;
        fixture.Window.DownloadQueueRestoreReadAsync = _ => { ++reads; return Task.FromResult<DownloadQueueSnapshot?>(DrSnapshot()); };
        var restore = Call(fixture.Window, "RestoreDownloadQueueAsync", "fixture");
        Assert(!restore.IsCompleted && reads == 0, "restore did not wait for the prior worker");
        var run = fixture.Start([DcTrack("4")]); var manualEntry = fixture.Queue.Entries.Single();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
        pending.SetResult(); await restore.WaitAsync(TimeSpan.FromSeconds(5));
        Assert(reads == 0 && ReferenceEquals(fixture.Queue.Entries.Single(), manualEntry),
            "restore captured its admission only after waiting for the prior worker");
        DrAssertManualSettled(fixture, manualEntry, complete: true);
    }
    private static void DrAssertManualSettled(DownloadCenterFixture fixture, DownloadEntry manualEntry, bool complete)
    {
        Assert(fixture.Active == 0 && !fixture.Queue.Running && SaField<Task?>(fixture.Window, "_downloadTask") is null
            && SaField<CancellationTokenSource?>(fixture.Window, "_downloadCts") is null
            && ReferenceEquals(fixture.Queue.Entries.Single(), manualEntry)
            && manualEntry.State == (complete ? DownloadEntryState.Completed : DownloadEntryState.Paused),
            "manual worker failed to finish or changed its queue after obsolete restoration");
        var player = SaField<NativePlayer>(fixture.Window, "_player");
        Assert(fixture.Requests.Count == 1 && fixture.Requests.GetValueOrDefault("4") == 1 && !player.IsPlaying && !player.HasSource,
            "restoration started saved requests or playback");
    }
    private static async Task DrRestoreInvalidation()
    {
        foreach (var close in new[] { false, true })
        {
            using var fixture = new DownloadCenterFixture("restore-invalidation-" + close, 1);
            DrEnable(fixture);
            var held = new TaskCompletionSource<DownloadQueueSnapshot?>(TaskCreationOptions.RunContinuationsAsynchronously);
            fixture.Window.DownloadQueueRestoreReadAsync = _ => held.Task;
            var restore = Call(fixture.Window, "RestoreDownloadQueueAsync", "fixture");
            SaInvoke(fixture.Window, close ? "SaveDownloadQueueForClose" : "ClearPersistedDownloadQueue");
            held.SetResult(DrSnapshot()); await restore.WaitAsync(TimeSpan.FromSeconds(5));
            Assert(fixture.Queue.Entries.Count == 0 && fixture.Requests.Count == 0 && !fixture.Queue.Running
                && !File.Exists(Path.Combine(fixture.DirectoryPath, DownloadQueueStore.FileName)),
                "held restoration survived " + (close ? "close" : "clear"));
        }
        using var errorFixture = new DownloadCenterFixture("restore-stale-error", 1);
        DrEnable(errorFixture);
        var preparing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var failure = new TaskCompletionSource<RestoredDownloadQueue>(TaskCreationOptions.RunContinuationsAsynchronously);
        errorFixture.Window.DownloadQueueRestoreReadAsync = _ => Task.FromResult<DownloadQueueSnapshot?>(DrSnapshot());
        errorFixture.Window.DownloadQueueRestorePrepareAsync = _ => { preparing.SetResult(); return failure.Task; };
        var stale = Call(errorFixture.Window, "RestoreDownloadQueueAsync", "fixture");
        await preparing.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var manual = errorFixture.Start([DcTrack("4")]); var entry = errorFixture.Queue.Entries.Single();
        await manual.WaitAsync(TimeSpan.FromSeconds(5));
        failure.SetException(new IOException("Fixture stale restore failure")); await stale.WaitAsync(TimeSpan.FromSeconds(5));
        Assert(!SaField<bool>(errorFixture.Window, "_downloadPersistenceWriteBlocked")
            && !SaField<bool>(errorFixture.Window, "_downloadsAutoSuspended"), "stale restore error changed manual persistence or pause state");
        DrAssertManualSettled(errorFixture, entry, complete: true);
    }
    private static async Task DrInvalid()
    {
        var directory = DrDirectory("invalid"); var store = new DownloadQueueStore(directory); store.Save(DrSnapshot());
        var path = Path.Combine(directory, DownloadQueueStore.FileName); var good = File.ReadAllText(path);
        var bad = new List<string> { "{", "null", "{}" };
        foreach (var field in new[] { "version", "accountUid", "quality", "entries" })
        { var node = JsonNode.Parse(good)!; node.AsObject().Remove(field); bad.Add(node.ToJsonString()); }
        var extra = JsonNode.Parse(good)!; extra["signedUrl"] = "https://signed.invalid/?token=SECRET"; bad.Add(extra.ToJsonString());
        var invalid = JsonNode.Parse(good)!; invalid["entries"]![0]!["track"]!["requestId"] = "https://audio.invalid"; bad.Add(invalid.ToJsonString());
        invalid = JsonNode.Parse(good)!; invalid["entries"]![0]!["error"] = "token=SECRET"; bad.Add(invalid.ToJsonString());
        invalid = JsonNode.Parse(good)!; invalid["playlist"]!["trackIds"]![0] = "999"; bad.Add(invalid.ToJsonString());
        invalid = JsonNode.Parse(good)!; invalid["entries"]![0]!["attempts"] = -1; bad.Add(invalid.ToJsonString());
        foreach (var text in bad)
        {
            File.WriteAllText(path, text);
            Assert(store.Load("fixture") is null && store.LastReadStatus == DownloadQueueReadStatus.Invalid && File.ReadAllText(path) == text, "bad snapshot was accepted or overwritten during read");
        }
        using (var huge = new FileStream(path, FileMode.Create)) huge.SetLength(DownloadQueueStore.MaximumBytes + 1L);
        Assert(store.Load("fixture") is null && store.LastReadStatus == DownloadQueueReadStatus.Invalid, "oversized input bypassed the read bound");
        store.Save(DrSnapshot()); var before = File.ReadAllBytes(path);
        var queue = new DownloadQueueState(); queue.Replace(Enumerable.Range(1, DownloadQueueStore.MaximumEntries + 1).Select(i => DcTrack(i.ToString())), "Too many");
        await Throws<JsonException>(() => { DownloadQueueStore.Capture("fixture", queue, AudioQuality.Optimal, false, null); return Task.CompletedTask; });
        Assert(before.SequenceEqual(File.ReadAllBytes(path)), "invalid capture replaced the last good queue");
        queue.Replace(Enumerable.Range(1, 10_000).Select(i => DcTrack(i.ToString())), "Maximum count");
        store.Save(DownloadQueueStore.Capture("fixture", queue, AudioQuality.Optimal, false, null));
        Assert(store.Load("fixture")!.Entries.Count == 10_000, "maximum allowed count was truncated");
        before = File.ReadAllBytes(path);
        var wide = new string('X', 4096);
        queue.Replace(Enumerable.Range(1, 750).Select(i => new TrackRow { Id = i.ToString(), RequestId = i.ToString(), Title = wide, Artist = wide, Album = wide }), "Maximum bytes");
        var tooWide = DownloadQueueStore.Capture("fixture", queue, AudioQuality.Optimal, false, null);
        await Throws<JsonException>(() => { store.Save(tooWide); return Task.CompletedTask; });
        Assert(before.SequenceEqual(File.ReadAllBytes(path)), "bounded writer replaced the last good snapshot on overflow");
        var target = Path.Combine(directory, "outside"); Directory.CreateDirectory(target);
        var linked = Path.Combine(directory, "linked"); CreateUninstallJunction(linked, target);
        try
        {
            var unsafeStore = new DownloadQueueStore(linked);
            await Throws<IOException>(() => { unsafeStore.Save(DrSnapshot()); return Task.CompletedTask; });
            Assert(unsafeStore.Load("fixture") is null && unsafeStore.LastReadStatus == DownloadQueueReadStatus.Unavailable
                && !Directory.EnumerateFileSystemEntries(target).Any(), "queue storage followed a reparse directory");
        }
        finally { Directory.Delete(linked, recursive: false); }
    }
    private static async Task DrCorruptIndex()
    {
        string path; byte[] before;
        using (var fixture = new DownloadCenterFixture("restore-corrupt-index", 1))
        {
            DrEnable(fixture);
            var store = new DownloadQueueStore(fixture.DirectoryPath); store.Save(DrSnapshot());
            path = Path.Combine(fixture.DirectoryPath, DownloadQueueStore.FileName); before = File.ReadAllBytes(path);
            Directory.CreateDirectory(fixture.Library.DirectoryPath);
            File.WriteAllText(Path.Combine(fixture.Library.DirectoryPath, "library.index.json"), "{");
            await Call(fixture.Window, "RestoreDownloadQueueAsync", "fixture");
            await Call(fixture.Window, "PersistDownloadQueueAsync", true);
            Assert(fixture.Requests.Count == 0 && fixture.Queue.Entries.Count == 0 && SaField<bool>(fixture.Window, "_downloadPersistenceWriteBlocked")
                && before.SequenceEqual(File.ReadAllBytes(path)), "corrupt offline index admitted work or overwrote the stored queue");
        }
        Assert(before.SequenceEqual(File.ReadAllBytes(path)), "closing after a restore failure overwrote the evidence");
    }
    private static async Task DrWrites()
    {
        var directory = DrDirectory("writes"); var store = new DownloadQueueStore(directory);
        var writes = Enumerable.Range(0, 40).Select(i => store.SaveInBackgroundAsync(DrSnapshot(label: "old-" + i))).ToArray();
        store.Save(DrSnapshot(label: "latest")); await Task.WhenAll(writes);
        Assert(store.Load("fixture")!.Label == "latest", "older background write won after synchronous close save");
        writes = Enumerable.Range(0, 40).Select(i => store.SaveInBackgroundAsync(DrSnapshot(label: "stale-" + i))).ToArray();
        new DownloadQueueStore(directory).Clear(); await Task.WhenAll(writes);
        Assert(!File.Exists(Path.Combine(directory, DownloadQueueStore.FileName)) && !Directory.EnumerateFiles(directory, "*.tmp").Any(), "clear resurrected a stale queue or leaked an atomic temporary file");
    }
    private static Task DrUninstall()
    {
        var root = DrDirectory("uninstall"); var paths = UninstallPaths.FromKnownFolders(Path.Combine(root, "Local"), Path.Combine(root, "Documents"));
        new DownloadQueueStore(paths.Data).Save(DrSnapshot());
        var result = UninstallCleanup.Execute(paths);
        Assert(result.Complete && !Directory.Exists(paths.Data), "full uninstall left the owned queue file behind");
        return Task.CompletedTask;
    }
}
