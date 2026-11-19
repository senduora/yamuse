using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using YMusic.Native;

internal static partial class Program
{
    private static async Task CheckDownloadRestore()
    {
        await Run("DownloadRestore: metadata-only immutable account-bound snapshot and playlist membership roundtrip", DrMetadata);
        await Run("DownloadRestore: recalled playlist artwork survives resume without entering the queue checkpoint", DrPlaylistArtwork);
        await Run("DownloadRestore: repeated editions survive continuation and a busy playlist index", DrExactPlaylistOccurrences);
        await Run("DownloadRestore: exact playlist editions stay independent from available audio jobs", DrPlaylistEditionSelection);
        await Run("DownloadRestore: authoritative sparse metadata keeps equal fields and legacy occurrences can recall them", DrPlaylistMetadataProvenance);
        await Run("DownloadRestore: deferred automatic edition retains separate manual history across reopen", DrDeferredFavoriteEdition);
        await Run("DownloadRestore: crash reconciliation pauses unfinished work and validates ready files", DrCrash);
        await Run("DownloadRestore: close reopen and explicit resume retry preserve files and playlist context", DrWindowLifecycle);
        await Run("DownloadRestore: logout and account generation reject stale restored work and writes", DrAccount);
        await Run("DownloadRestore: locked logout retry retires only the previous checkpoint before re-login or exit", DrRetryLogoutCheckpoint);
        await Run("DownloadRestore: unproved logout receipt protects its owner and allows unrelated in-memory work", DrUnknownLogoutCheckpoint);
        await Run("DownloadRestore: resolving a foreign logout saves the live queue only when it can claim the checkpoint", DrRetryForeignLogoutForCurrentQueue);
        await Run("DownloadRestore: account with no work rechecks the checkpoint after a foreign logout resolves", DrRecheckEmptyAfterForeignLogout);
        await Run("DownloadRestore: explicit logout observes the current readable revision before retrying locked deletion", DrLogoutObservesCurrentRevision);
        await Run("DownloadRestore: account switch hides old rows and queue text while the next restore waits", DrAccountVisibility);
        await Run("DownloadRestore: restore and automatic batches wait for the shared offline recovery task", DrPendingOfflineRecovery);
        await Run("DownloadRestore: opening Download Center retries a transient offline recovery failure", DrRetryOfflineRecovery);
        await Run("DownloadRestore: unreadable and temporarily unreconciled checkpoints are protected until Download Center can retry", DrRetryUnreadableCheckpoint);
        await Run("DownloadRestore: single deletion preserves a protected checkpoint and an explicit pause across close", DrDeletionKeepsCheckpoint);
        await Run("DownloadRestore: read status stays with its result across another completed read", DrReadStatusOwnership);
        await Run("DownloadRestore: inaccessible checkpoint allows an explicit in-memory batch without replacing later readable data", DrInMemoryWhenCheckpointIsUnsafe);
        await Run("DownloadRestore: stale load or preparation cannot replace running or completed manual work", DrManualAdmission);
        await Run("DownloadRestore: rejected count and byte admissions keep the old queue and checkpoint writable", DrCheckpointAdmissionCapacity);
        await Run("DownloadRestore: a near-limit published v1 queue can continue retry and roundtrip in the current format", DrNearLimitLegacyCheckpoint);
        await Run("DownloadRestore: near-limit v2 and v3 queues retain sparse editions and local artwork", async () =>
        { await DrNearLimitCompactCheckpoint(2); await DrNearLimitCompactCheckpoint(DownloadQueueStore.CurrentVersion); });
        await Run("DownloadRestore: admission before pending-worker wait rejects obsolete restoration", DrPendingAdmission);
        await Run("DownloadRestore: clear close and stale preparation failures invalidate held restoration", DrRestoreInvalidation);
        await Run("DownloadRestore: invalid oversized and unknown data remain untouched", DrInvalid);
        await Run("DownloadRestore: corrupt offline index blocks restoration without overwriting evidence", DrCorruptIndex);
        await Run("DownloadRestore: corrupt playlist index preserves its checkpoint while separate audio work remains available", DrCorruptPlaylistIndex);
        await Run("DownloadRestore: a confirmed different account cannot replace an untouched checkpoint on exit", DrPreserveForeignCheckpoint);
        await Run("DownloadRestore: an explicit batch can save over an observed previous owner but preserves a later revision", DrDifferentOwnerExplicitBatch);
        await Run("DownloadRestore: oversized playlist index permits independent audio while retaining the index and checkpoint", DrOversizedPlaylistIndex);
        await Run("DownloadRestore: repairing a playlist index keeps the current manual pause and retry", DrKeepManualQueueAfterPlaylistRepair);
        await Run("DownloadRestore: single removal verifies a preserved checkpoint before modifying the offline file", DrDeleteWhenCheckpointReadRefused);
        await Run("DownloadRestore: close waits for committed single removal before saving the paused queue", DrCloseWaitsForOfflineRemoval);
        await Run("DownloadRestore: newest atomic write and clear defeat outstanding background saves", DrWrites);
        await Run("DownloadRestore: a logout retry clears only the observed checkpoint revision", DrRetryClearIdentity);
        await Run("DownloadRestore: a busy checkpoint keeps the previous snapshot and retries when writable", DrRetryCheckpoint);
        await Run("DownloadRestore: full uninstall removes the owned queue snapshot", DrUninstall);
    }
    private static string DrDirectory(string name)
    { var path = Path.Combine(_directory, "download-restore-" + name); Directory.CreateDirectory(path); return path; }

    private static async Task DrNearLimitLegacyCheckpoint()
    {
        using var fixture = new DownloadCenterFixture("restore-large-legacy", 1);
        DrEnable(fixture);
        var window = fixture.Window;
        var title = new string('я', 3400);
        var rows = Enumerable.Range(1, 400).Select(id => new TrackRow
        {
            Id = id.ToString(), RequestId = id + ":91", Title = title, Artist = "Автор", AlbumId = "91", Available = id <= 2
        }).ToArray();
        var queue = new DownloadQueueState();
        queue.Replace(rows, "Ранее сохранённая очередь");
        queue.Entries[0].SetState(DownloadEntryState.Paused);
        var failure = DownloadFailure.Describe(new MusicException("повреждён индекс"));
        queue.Entries[1].Begin(); queue.Entries[1].SetState(DownloadEntryState.Failed, failure);
        foreach (var entry in queue.Entries.Skip(2)) entry.SetState(DownloadEntryState.Unavailable);
        var playlist = new PlaylistDetails("fixture-owner", "91", "Ранее сохранённый плейлист", "", 7, [rows[1], rows[0], rows[1]]);
        var legacy = DownloadQueueStore.Capture("fixture", queue, AudioQuality.Optimal, false, playlist,
            recordPlaylistOccurrences: false) with { Version = 1 };
        var formerJson = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        var legacyBytes = JsonSerializer.SerializeToUtf8Bytes(legacy, formerJson);
        var largest = legacy with
        {
            SavedUtc = DateTimeOffset.UnixEpoch.AddTicks(1),
            Playlist = legacy.Playlist! with { HistoryOnly = true },
            Entries = legacy.Entries.Select(entry => entry with
            {
                State = DownloadEntryState.Failed, Attempts = DownloadQueueStore.MaximumAttempts, Error = failure
            }).ToArray()
        };
        Assert(legacyBytes.Length < DownloadQueueStore.MaximumBytes && legacyBytes.Length > DownloadQueueStore.MaximumBytes - 200_000
            && JsonSerializer.SerializeToUtf8Bytes(largest, formerJson).Length > DownloadQueueStore.MaximumBytes,
            "fixture does not fit the published writer while overflowing the old reserve");

        var path = Path.Combine(fixture.DirectoryPath, DownloadQueueStore.FileName);
        File.WriteAllBytes(path, legacyBytes);
        await Call(window, "RestoreDownloadQueueAsync", "fixture");
        Assert(fixture.Queue.CanResume && fixture.Queue.CanRetry && fixture.Queue.Entries.Count == rows.Length && fixture.Requests.Count == 0,
            "legacy restore did not retain the paused and failed work without starting it");
        await Call(window, "ResumeDownloadQueue");
        Assert(fixture.Requests.Count == 1 && fixture.Requests.GetValueOrDefault("1") == 1
            && fixture.Queue.Entries[1].State == DownloadEntryState.Failed, "Continue refused or crossed the legacy retry boundary");
        await Call(window, "RetryFailedDownloads");
        Assert(fixture.Requests.Count == 2 && fixture.Requests.GetValueOrDefault("2") == 1,
            "Retry refused or downloaded unrelated legacy rows");
        var store = new DownloadQueueStore(fixture.DirectoryPath);
        var saved = store.Load("fixture")!;
        Assert(saved.Version == DownloadQueueStore.CurrentVersion && new FileInfo(path).Length <= DownloadQueueStore.MaximumBytes
            && saved.Entries.Count == rows.Length && saved.Entries[0] is { State: DownloadEntryState.Completed, Attempts: 1 }
            && saved.Entries[1] is { State: DownloadEntryState.Completed, Attempts: 2 }
            && saved.Entries.Skip(2).All(entry => entry.State == DownloadEntryState.Unavailable)
            && saved.Entries.Select(entry => entry.Track.RequestId).SequenceEqual(rows.Select(row => row.RequestId))
            && saved.Entries.All(entry => entry.Track.Title == title && entry.Track.AlbumId == "91")
            && saved.Playlist?.TrackIds.SequenceEqual(new[] { "2", "1", "2" }) == true,
            "migration lost metadata, ordering or progress or crossed the hard size limit");

        var worstStore = new DownloadQueueStore(DrDirectory("legacy-v2-capacity"));
        worstStore.Save(largest);
        var worst = worstStore.Load("fixture")!;
        Assert(worst.Version == DownloadQueueStore.CurrentVersion && worst.Playlist?.HistoryOnly == true
            && worst.Entries.Count == rows.Length && worst.Entries.All(entry => entry.Attempts == DownloadQueueStore.MaximumAttempts
                && entry.Error == failure && entry.Track.Title == title), "v2 did not preserve the reserved worst-case legacy metadata");
    }

    private static async Task DrNearLimitCompactCheckpoint(int version)
    {
        using var f = new DownloadCenterFixture("restore-large-compact-" + version, 1);
        DrEnable(f);
        var ready = DcTrack("1"); var failed = DcTrack("2");
        var queue = new DownloadQueueState(); queue.Replace([ready, failed], "Compact V2"); queue.Pause();
        queue.Entries[1].SetState(DownloadEntryState.Failed);
        PlaylistDetails Context(int width, bool remembered = false)
        {
            var title = new string('я', width);
            var editions = Enumerable.Range(100, 400).Select(album => new TrackRow
            { Id = "1", AlbumId = album.ToString(), RequestId = $"1:{album}", Title = title, Available = true,
                CoverUri = remembered ? $"https://avatars.yandex.net/get-music-content/edition-{album}/%%" : null });
            return new("fixture-owner", "91", "Compact V2", "", 1, new[] { ready, failed }.Concat(editions).ToArray());
        }
        var low = 3300; var high = 3600;
        while (high - low > 1)
        {
            var middle = (low + high) / 2;
            if (DownloadQueueStore.CanAdmit("fixture", queue.Entries, queue.Label, AudioQuality.Optimal, false, false, Context(middle))) low = middle;
            else high = middle;
        }
        var playlist = Context(low);
        var path = Path.Combine(f.DirectoryPath, DownloadQueueStore.FileName);
        var store = new DownloadQueueStore(f.DirectoryPath);
        var snapshot = DownloadQueueStore.Capture("fixture", queue, AudioQuality.Optimal, false, playlist);
        store.Save(snapshot with { Version = version });
        await f.Library.RememberPlaylistAsync(Context(low, remembered: true), AudioQuality.Optimal, default);
        Assert(store.Load("fixture")?.Version == version && new FileInfo(path).Length > DownloadQueueStore.MaximumBytes - 5000,
            "fixture did not create a compact checkpoint near its size limit");

        await Call(f.Window, "RestoreDownloadQueueAsync", "fixture");
        Assert(f.Queue.CanResume && f.Queue.CanRetry, "v2 did not restore its paused and failed rows");
        await Call(f.Window, "ResumeDownloadQueue");
        await Call(f.Window, "RetryFailedDownloads");
        var saved = store.Load("fixture")!;
        var restored = await DownloadQueueStore.PrepareRestoreAsync(saved, f.Library, default);
        var restoredPlaylist = restored.Playlist!;
        Assert(f.Requests.Count == 2 && f.Requests.Values.All(count => count == 1)
            && saved.Version == DownloadQueueStore.CurrentVersion && new FileInfo(path).Length <= DownloadQueueStore.MaximumBytes
            && saved.Playlist!.Occurrences!.All(occurrence => occurrence.CoverUri is null)
            && restoredPlaylist.Tracks.Select(track => (track.RequestId, track.Title)).SequenceEqual(playlist.Tracks.Select(track => (track.RequestId, track.Title)))
            && restoredPlaylist.Tracks.Skip(2).All(track => track.CoverUri is not null),
            "near-limit queue could not continue or preserve its sparse playlist and recalled artwork on re-save");
    }

    private static async Task DrCheckpointAdmissionCapacity()
    {
        using var fixture = new DownloadCenterFixture("restore-admission-capacity", 1) { FailTrack = "1" };
        DrEnable(fixture);
        var window = fixture.Window;
        await fixture.Start([DcTrack("1")]);
        await Call(window, "PersistDownloadQueueAsync", true);
        var entry = fixture.Queue.Entries.Single();
        var checkpoint = Path.Combine(fixture.DirectoryPath, DownloadQueueStore.FileName);
        var original = File.ReadAllBytes(checkpoint);
        var admission = SaField<long>(window, "_downloadQueueAdmissionRevision");
        var countOverflow = Enumerable.Range(2, DownloadQueueStore.MaximumEntries + 1).Select(id => DcTrack(id.ToString())).ToArray();
        var title = new string('я', 4096);
        var byteOverflow = Enumerable.Range(2, 400).Select(id => new TrackRow
        { Id = id.ToString(), RequestId = id.ToString(), Title = title, Available = true }).ToArray();

        foreach (var rows in new[] { countOverflow, byteOverflow })
        {
            var playlist = new PlaylistDetails("fixture-owner", "345", "Слишком большая очередь", "", 0, rows);
            var manual = Call(window, "StartDownloadBatchAsync", rows, playlist.Title, playlist, false);
            Assert(manual.IsCompletedSuccessfully && Control<TextBlock>(window, "StatusText").Text.Contains("меньше треков"),
                "the complete manual list was admitted beyond the count or byte limit");
            var automatic = Call(window, "StartDownloadBatchAsync", rows, "Автоскачивание", null!, true);
            Assert(automatic.IsCompletedSuccessfully && Control<TextBlock>(window, "StatusText").Text.Contains("Автоскачивание отложено"),
                "the automatic append was admitted or silently skipped beyond the count or byte limit");
            Assert(fixture.Queue.Entries.Single() == entry && entry.State == DownloadEntryState.Failed && fixture.Requests.Count == 1
                && SaField<long>(window, "_downloadQueueAdmissionRevision") == admission && File.ReadAllBytes(checkpoint).SequenceEqual(original),
                "a rejected package mutated the queue, started work or touched its checkpoint");
        }

        var pending = SaField<Dictionary<string, TrackRow>>(window, "_pendingConfirmedFavorites");
        foreach (var row in countOverflow) pending.Add(row.Id, row);
        typeof(MainWindow).GetField("_autoFavoritesEnabled", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(window, true);
        SaInvoke(window, "PauseDownloadQueueForAppUpdate");
        Assert(fixture.Queue.Entries.Single() == entry && pending.Count == countOverflow.Length,
            "preparing to exit or apply an update admitted an oversized pending append");
        var save = typeof(MainWindow).GetMethod("TrySaveDownloadQueueCheckpoint", BindingFlags.NonPublic | BindingFlags.Instance)!;
        Assert(save.Invoke(window, null) is true && new DownloadQueueStore(fixture.DirectoryPath).Load("fixture")?.Entries.Single().Track.Id == "1",
            "the previous queue could not be checkpointed for close or update after rejection");
    }
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
        var readyPath = offline.ResolvePath(saved.FileName);
        var changed = File.ReadAllBytes(readyPath); changed[^1] ^= 1; File.WriteAllBytes(readyPath, changed);
        Assert(DownloadQueueStore.PrepareRestore(snapshot, offline).Entries[0] is { State: DownloadEntryState.Paused, Bytes: 0, Track.IsDownloaded: false },
            "same-size damaged audio was restored as ready");
        File.Delete(readyPath);
        Assert(DownloadQueueStore.PrepareRestore(snapshot, offline).Entries[0] is { State: DownloadEntryState.Paused, Bytes: 0, Track.IsDownloaded: false }, "missing ready file was trusted from queue JSON");
        var favorites = new DownloadQueueState(); favorites.Replace([DcTrack("1"), DcTrack("2")], "Мне нравится", true);
        favorites.SetCollectionLike("2", false);
        var favoriteSnapshot = DownloadQueueStore.Capture("fixture", favorites, AudioQuality.Optimal, true,
            new("local:favorites", "liked", "Мне нравится", "", 0, favorites.Entries.Select(e => e.Track).ToArray()));
        var favoriteRestored = new DownloadQueueState(); favoriteRestored.Restore(DownloadQueueStore.PrepareRestore(favoriteSnapshot, offline));
        Assert(favoriteRestored.FavoritesBatch && favoriteRestored.Entries[1].SkipFavoritesDownload
            && favoriteRestored.Entries[1].RemovedFromCollection && favoriteRestored.BeginRun(false).Length == 1, "favorites exclusion was not retained");
    }
    private static async Task DrPlaylistArtwork()
    {
        var directory = DrDirectory("playlist-artwork");
        var media = new FakeHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new ByteArrayContent(DownloadCenterFixture.Media) }));
        using var offline = new OfflineLibrary(Path.Combine(directory, "Music"), new AudioBuffer(Path.Combine(directory, "buffer"), media));
        const string cover = "https://avatars.yandex.net/get-music-content/fixture/%%";
        TrackRow Track(string id, string album, string? artwork = null) => new()
        { Id = id, RequestId = $"{id}:{album}", AlbumId = album, Title = $"Fixture {id}", Available = true, CoverUri = artwork };
        var known = Track("1", "a", "//avatars.yandex.net/get-music-content/fixture/%%");
        var formerEdition = Track("2", "b", cover);
        var invalid = Track("3", "c", "https://untrusted.invalid/private?token=fixture");
        var nonMember = Track("4", "d", cover);
        var remembered = new PlaylistDetails("owner-art", "123", "Fixture artwork", "", 1, [known, formerEdition, invalid, nonMember]);
        await offline.RememberPlaylistAsync(remembered, AudioQuality.Optimal, default);

        var replacementEdition = Track("2", "new-edition");
        var queue = new DownloadQueueState(); queue.Replace([known, replacementEdition, invalid, nonMember], "Fixture artwork");
        var context = remembered with { Revision = 2, Tracks = [known, replacementEdition, invalid] };
        var store = new DownloadQueueStore(directory);
        store.Save(DownloadQueueStore.Capture("fixture", queue, AudioQuality.Optimal, false, context));
        var checkpoint = File.ReadAllText(Path.Combine(directory, DownloadQueueStore.FileName));
        Assert(!checkpoint.Contains("coverUri") && !checkpoint.Contains("https") && !checkpoint.Contains("avatars"), "queue obtained artwork addresses");
        var saved = store.Load("fixture")!;
        var restored = DownloadQueueStore.PrepareRestore(saved, offline);
        Assert(restored.Entries.Select(entry => entry.Track.CoverUri).SequenceEqual(new string?[] { cover, null, null, null })
            && restored.Playlist!.Tracks[0].CoverUri == cover, "restoration crossed an edition or membership boundary, or retained an unsafe cover");
        var anotherOwner = saved with { Playlist = saved.Playlist! with { Owner = "different-owner" } };
        Assert(DownloadQueueStore.PrepareRestore(anotherOwner, offline).Entries.All(entry => entry.Track.CoverUri is null), "artwork crossed playlist ownership");

        await offline.RememberPlaylistAsync(restored.Playlist!, restored.Quality, default);
        var item = await offline.SaveAsync(restored.Entries[0].Track, new(new Uri("https://s1.vlx.yandex.net/fixture.mp3"), "mp3", 320), default);
        using var reopened = new OfflineLibrary(offline.DirectoryPath);
        Assert(reopened.GetPlaylists().Single().Tracks[0].CoverUri == cover && item.CoverUri == cover
            && reopened.Tracks().Single().CoverUri == cover
            && !reopened.GetPlaylistStatus(context.Owner, context.Kind, context.Tracks).ChangedSinceSnapshot,
            "resumed playlist lost its cover or reported a change caused by artwork normalization");
    }
    private static void DrEnable(DownloadCenterFixture fixture)
    {
        typeof(MainWindow).GetField("_restore", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(fixture.Window, true);
        typeof(MainWindow).GetField("_account", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(fixture.Window, new Account("fixture", "Fixture", false));
        SaInvoke(fixture.Window, "InitializeDownloadPersistence");
    }
    private static async Task DrExactPlaylistOccurrences()
    {
        foreach (var mode in new[] { "new", "legacy", "busy-legacy", "busy-new", "newer" })
        {
            using var f = new DownloadCenterFixture("restore-playlist-editions-" + mode, 1);
            DrEnable(f);
            const string cover1 = "https://avatars.yandex.net/get-music-content/fixture-edition-1/%%";
            const string cover2 = "https://avatars.yandex.net/get-music-content/fixture-edition-2/%%";
            TrackRow Edition(string album, string cover) => new()
            { Id = "1", RequestId = "1:" + album, AlbumId = album, Album = "Album " + album,
                Title = "Fixture " + album, Available = true, CoverUri = cover };
            var tracks = new[] { Edition("91", cover1), Edition("92", cover2), DcTrack("2") };
            var playlist = new PlaylistDetails("owner-editions", "42", "Издания", "", 7, tracks);
            var queue = new DownloadQueueState(); queue.Replace(tracks, playlist.Title);
            var store = new DownloadQueueStore(f.DirectoryPath);
            var index = Path.Combine(f.Library.DirectoryPath, "playlists.index.json");
            byte[]? previous = null;
            if (mode != "new")
            {
                var previousPlaylist = mode switch
                {
                    "busy-new" => playlist with { Owner = "other-owner", Tracks = [DcTrack("3")] },
                    "newer" => playlist with { Revision = 8, Tracks = [DcTrack("3")] },
                    _ => playlist
                };
                await f.Library.RememberPlaylistAsync(previousPlaylist, AudioQuality.Optimal, default);
                previous = File.ReadAllBytes(index);
            }
            store.Save(DownloadQueueStore.Capture("fixture", queue, AudioQuality.Optimal, false, playlist,
                recordPlaylistOccurrences: mode is not ("legacy" or "busy-legacy")));
            var checkpointPath = Path.Combine(f.DirectoryPath, DownloadQueueStore.FileName);
            var checkpoint = File.ReadAllBytes(checkpointPath);
            using (var busy = mode.StartsWith("busy", StringComparison.Ordinal) ? File.Open(index, FileMode.Open, FileAccess.Read, FileShare.None) : null)
            {
                await Call(f.Window, "RestoreDownloadQueueAsync", "fixture");
                if (busy is not null)
                {
                    await Call(f.Window, "ResumeDownloadQueue");
                    Assert(!f.Queue.CanResume && f.Requests.Count == 0 && SaField<bool>(f.Window, "_downloadRestoreNeedsRetry")
                        && File.ReadAllBytes(checkpointPath).SequenceEqual(checkpoint), "busy playlist index released the protected queue");
                }
            }
            if (mode.StartsWith("busy", StringComparison.Ordinal))
            {
                Assert(File.ReadAllBytes(index).SequenceEqual(previous!), "the held playlist index was rewritten");
                SaInvoke(f.Window, "DownloadCenter_Click", null!, null!);
                await Until(() => f.Queue.CanResume);
            }
            Assert(f.Queue.CanResume && f.Queue.Entries.Count == 2 && f.Requests.Count == 0, "restore started or duplicated an audio job");
            await Call(f.Window, "ResumeDownloadQueue");
            if (mode == "newer")
            {
                Assert(f.Requests.Count == 2 && File.ReadAllBytes(index).SequenceEqual(previous!), "continuation overwrote the newer playlist revision");
                continue;
            }
            var actual = f.Library.GetPlaylists().Single(p => p.Owner == playlist.Owner).Tracks;
            Assert(f.Requests.Count == 2 && f.Requests.Values.All(count => count == 1)
                && actual.Select(track => (track.RequestId, track.AlbumId, track.Album, track.Title, track.CoverUri))
                    .SequenceEqual(tracks.Select(track => (track.RequestId, track.AlbumId, track.Album, track.Title, track.CoverUri)))
                && !f.Library.GetPlaylistStatus(playlist.Owner, playlist.Kind, tracks).ChangedSinceSnapshot,
                "continuation lost an edition or downloaded one ID twice");
            if (mode == "busy-new") Assert(f.Library.GetPlaylists().Any(p => p.Owner == "other-owner"), "continuation discarded another playlist");
        }
    }

    private static TrackRow DrEdition(string album, bool available = true) => new()
    { Id = "1", RequestId = "1:" + album, AlbumId = album, Album = "Album " + album, Title = "Edition " + album, Available = available };

    private static async Task DrPlaylistEditionSelection()
    {
        using var f = new DownloadCenterFixture("edition-selection", 1);
        DrEnable(f);
        var unavailable = DrEdition("91", false);
        var available = DrEdition("92");
        var earlier = new PlaylistDetails("fixture-owner", "91", "Different edition", "", 0, [DrEdition("91")]);
        await f.Library.RememberPlaylistAsync(earlier, AudioQuality.Optimal, default);
        var queue = new DownloadQueueState(); queue.Replace([available], earlier.Title);
        var store = new DownloadQueueStore(f.DirectoryPath);
        store.Save(DownloadQueueStore.Capture("fixture", queue, AudioQuality.Optimal, false, earlier with { Tracks = [available] }));
        await Call(f.Window, "RestoreDownloadQueueAsync", "fixture");
        var context = SaField<PlaylistDetails>(f.Window, "_downloadQueuePlaylist");
        Assert(context.Tracks.Single() is { RequestId: "1:92", AlbumId: "92", Album: "Album 92", CoverUri: null },
            "equal bare ID and revision substituted a different remembered edition");

        var playlist = earlier with { Revision = 1, Tracks = [unavailable, available] };
        await Call(f.Window, "StartDownloadBatchAsync", playlist.Tracks, playlist.Title, playlist, false);
        Assert(f.Queue.Entries.Single().Track.RequestId == "1:92" && f.Requests.Count == 1
            && f.Requests.GetValueOrDefault("1") == 1 && f.Library.Read().Single().RequestId == "1:92"
            && f.Library.GetPlaylists().Single().Tracks.Select(track => track.RequestId).SequenceEqual(["1:91", "1:92"]),
            "one unavailable edition suppressed another or changed the original occurrences");
        Assert(DownloadQueueState.SelectWorkTracks([DrEdition("91"), available]).Single().RequestId == "1:91"
            && DownloadQueueState.SelectWorkTracks([unavailable, DrEdition("92", false)]).Single().RequestId == "1:91",
            "worker selection changed the first-edition rule among equally available tracks");
    }

    private static Task DrDeferredFavoriteEdition()
    {
        using var f = new DownloadCenterFixture("deferred-edition-history", 1);
        var oldEdition = DrEdition("91", false);
        var newEdition = DrEdition("92");
        var failed = DcTrack("2");
        var context = new PlaylistDetails("fixture-owner", "91", "Manual editions", "", 5, [oldEdition, failed]);
        var queue = new DownloadQueueState(); queue.Replace(context.Tracks, context.Title);
        queue.Entries[0].SetState(DownloadEntryState.Unavailable, DownloadFailure.UnavailableReason);
        queue.Entries[1].SetState(DownloadEntryState.Failed);
        var store = new DownloadQueueStore(f.DirectoryPath);
        store.Save(DownloadQueueStore.Capture("fixture", queue, AudioQuality.Optimal, false, context,
            recordPlaylistOccurrences: false)); // A published-shaped v2 with no older optional occurrence field.
        var restored = DownloadQueueStore.PrepareRestore(store.Load("fixture")!, f.Library);
        queue.Restore(restored);
        Assert(restored.PlaylistOccurrencesRecorded && queue.CanRetry, "exact initial v2 context lost its provenance");
        queue.AppendAutomaticFavorites([newEdition], AudioQuality.Optimal, requeueExisting: true);
        queue.Pause();
        store.Save(DownloadQueueStore.Capture("fixture", queue, AudioQuality.Optimal, false, restored.Playlist,
            recordPlaylistOccurrences: restored.PlaylistOccurrencesRecorded));
        var reopened = DownloadQueueStore.PrepareRestore(store.Load("fixture")!, f.Library);
        Assert(reopened.Playlist!.Tracks[0] is { RequestId: "1:91", Album: "Album 91", Available: false }
            && reopened.Entries[0].Track is { RequestId: "1:92", Album: "Album 92", Available: true },
            "confirming a new favorite edition changed manual playlist membership");
        var second = new DownloadQueueState(); second.Restore(reopened);
        foreach (var candidate in new[] { queue, second })
        {
            candidate.DiscardUnstartedAutomaticFavorites();
            Assert(candidate.Entries[0] is { State: DownloadEntryState.Unavailable, Track.RequestId: "1:91", Track.Available: false }
                && candidate.CanRetry, "discarding deferred work left new metadata in the previous terminal row");
        }
        return Task.CompletedTask;
    }

    private static async Task DrPlaylistMetadataProvenance()
    {
        using var f = new DownloadCenterFixture("edition-metadata-provenance", 1);
        TrackRow Edition(string album, string title) => new()
        { Id = "1", RequestId = "1:" + album, AlbumId = album, Title = title, Album = title, Available = true };
        var context = new PlaylistDetails("fixture-owner", "91", "Fixture editions", "", 1,
            [Edition("91", "Current metadata"), Edition("92", "Current metadata")]);
        await f.Library.RememberPlaylistAsync(context with { Tracks = [context.Tracks[0], Edition("92", "Previously remembered")] },
            AudioQuality.Optimal, default);
        var queue = new DownloadQueueState(); queue.Replace(context.Tracks, context.Title);
        var store = new DownloadQueueStore(f.DirectoryPath);
        store.Save(DownloadQueueStore.Capture("fixture", queue, AudioQuality.Optimal, false, context));
        var current = store.Load("fixture")!;
        var occurrence = current.Playlist!.Occurrences!.Single();
        Assert(current.Version == DownloadQueueStore.CurrentVersion && !occurrence.MetadataRecorded && occurrence.Metadata is null,
            "fixture expected an authoritative empty delta without per-occurrence overhead");
        var currentTracks = DownloadQueueStore.PrepareRestore(current, f.Library).Playlist!.Tracks;
        var legacy = current with { Version = 2 };
        var legacyTracks = DownloadQueueStore.PrepareRestore(legacy, f.Library).Playlist!.Tracks;
        var markedV2 = legacy with { Playlist = legacy.Playlist! with { Occurrences = [occurrence with { MetadataRecorded = true }] } };
        Assert(currentTracks[1] is { RequestId: "1:92", Title: "Current metadata", Album: "Current metadata" }
            && legacyTracks[1] is { RequestId: "1:92", Title: "Previously remembered", Album: "Previously remembered" }
            && DownloadQueueStore.PrepareRestore(markedV2, f.Library).Playlist!.Tracks[1].Title == "Current metadata",
            "empty modern deltas were treated as missing historical data");
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
            && !second.Queue.Running && second.Queue.HasPaused, "reopening started requests or lost user-gated continuation");
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
    private static async Task DrRetryLogoutCheckpoint()
    {
        foreach (var mode in new[] { "relogin", "replacement", "exit", "exclusive-exit" })
        {
            using var f = new DownloadCenterFixture("retry-logout-checkpoint-" + mode, 1);
            DrEnable(f);
            var store = SaField<DownloadQueueStore>(f.Window, "_downloadQueueStore");
            store.Save(DrSnapshot(label: "Отменённая очередь"));
            await Call(f.Window, "RestoreDownloadQueueAsync", "fixture");
            Assert(f.Queue.CanResume, "fixture did not load the original queue");
            var path = Path.Combine(f.DirectoryPath, DownloadQueueStore.FileName);
            var exit = mode is "exit" or "exclusive-exit";
            using (File.Open(path, FileMode.Open, FileAccess.Read, mode == "exclusive-exit" ? FileShare.None : FileShare.Read))
            {
                f.Window.SignOut();
                Assert(SaField<Dictionary<string, DownloadQueueStore.ClearRetry>>(f.Window, "_pendingDownloadQueueClears")["fixture"]
                    is { File: not null, Hash: not null }, "logout discarded an exact proof of the previously read queue");
                if (!exit)
                {
                    UlSet(f.Window, "_session", FakeSession);
                    UlSet(f.Window, "_account", new Account("fixture", "Fixture", false));
                    await Call(f.Window, "RestoreDownloadQueueAsync", "fixture");
                    Assert(!f.Queue.CanResume && SaField<bool>(f.Window, "_downloadRestoreNeedsRetry"),
                        "re-login admitted retired work before the cleanup succeeded");
                }
                if (exit) Assert(SaInvokeResult(f.Window, "TrySaveDownloadQueueCheckpoint") is false,
                    "guest close reported success while an identified retired checkpoint was still locked");
                Assert(File.Exists(path) && f.Requests.Count == 0, "locked checkpoint was changed or produced a download");
            }

            if (exit)
            {
                SaInvoke(f.Window, "TrySaveDownloadQueueCheckpoint");
                Assert(!File.Exists(path), "the shared exit and update checkpoint path did not retry logout cleanup");
                continue;
            }
            if (mode == "replacement") new DownloadQueueStore(f.DirectoryPath).Save(DrSnapshot(label: "Новая очередь"));
            await Call(f.Window, "RestoreDownloadQueueAsync", "fixture");
            Assert(f.Requests.Count == 0 && (mode == "replacement" ? f.Queue.Label == "Новая очередь" && File.Exists(path)
                : f.Queue.Entries.Count == 0 && !File.Exists(path)), "cleanup restored the retired queue or deleted its replacement");
        }
    }
    private static async Task DrUnknownLogoutCheckpoint()
    {
        using var f = new DownloadCenterFixture("unknown-logout-checkpoint", 1);
        DrEnable(f);
        var path = Path.Combine(f.DirectoryPath, DownloadQueueStore.FileName);
        new DownloadQueueStore(f.DirectoryPath).Save(DrSnapshot(label: "Отменённая очередь"));
        var original = File.ReadAllBytes(path);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var holder = Task.Run(() =>
        {
            using var gate = GuardedStoreFile.HoldWriteGate(path);
            entered.SetResult();
            release.Wait(TimeSpan.FromSeconds(10));
        });
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            f.Window.SignOut();
        }
        finally { release.Set(); await holder; }
        var receipts = SaField<Dictionary<string, DownloadQueueStore.ClearRetry>>(f.Window, "_pendingDownloadQueueClears");
        Assert(receipts["fixture"].File is null, "test did not simulate a refusal before the first identity check");
        Assert(SaInvokeResult(f.Window, "TrySaveDownloadQueueCheckpoint") is false && File.ReadAllBytes(path).SequenceEqual(original),
            "guest exit reported successful cleanup without proof of the previous queue revision");

        UlSet(f.Window, "_session", FakeSession);
        UlSet(f.Window, "_account", new Account("other", "Other", false));
        await Call(f.Window, "RestoreDownloadQueueAsync", "other");
        await f.Start([DcTrack("9")]);
        Assert(f.Queue.Entries.Single().State == DownloadEntryState.Completed && f.Requests.GetValueOrDefault("9") == 1
            && SaInvokeResult(f.Window, "TrySaveDownloadQueueCheckpoint") is false && File.ReadAllBytes(path).SequenceEqual(original)
            && SaField<TextBlock>(f.Window, "_downloadCenterNotice").Text.Contains("до закрытия приложения", StringComparison.Ordinal),
            "the other account was blocked or wrote over the unproved checkpoint");

        var replacement = DrDirectory("unknown-logout-replacement");
        new DownloadQueueStore(replacement).Save(DrSnapshot(label: "Другая очередь прежнего владельца"));
        File.Move(Path.Combine(replacement, DownloadQueueStore.FileName), path, overwrite: true);
        var replacementBytes = File.ReadAllBytes(path);
        UlSet(f.Window, "_account", new Account("fixture", "Fixture", false));
        await Call(f.Window, "RestoreDownloadQueueAsync", "fixture");
        await f.Start([DcTrack("8")]);
        Assert(SaField<bool>(f.Window, "_downloadRestoreNeedsRetry") && !f.Queue.CanResume
            && !f.Requests.ContainsKey("8") && File.ReadAllBytes(path).SequenceEqual(replacementBytes),
            "unknown receipt deleted or admitted a same-UID file without prior revision proof");
        f.Window.SignOut();
        Assert(!File.Exists(path) && SaInvokeResult(f.Window, "TrySaveDownloadQueueCheckpoint") is true,
            "a fresh explicit logout of the verified file owner did not retire the held queue");
    }

    private static async Task DrRetryForeignLogoutForCurrentQueue()
    {
        foreach (var mode in new[] { "center", "close", "replacement", "replacement-paused" })
        {
            var pause = mode is "close" or "replacement-paused";
            using var f = new DownloadCenterFixture("retry-foreign-logout-" + mode, 1) { HoldFirstAttempts = pause };
            DrEnable(f);
            var store = SaField<DownloadQueueStore>(f.Window, "_downloadQueueStore");
            store.Save(DrSnapshot(label: "Retired queue"));
            var path = Path.Combine(f.DirectoryPath, DownloadQueueStore.FileName);
            var old = File.ReadAllBytes(path);
            var notice = SaField<TextBlock>(f.Window, "_downloadCenterNotice");
            var expectedState = pause ? DownloadEntryState.Paused : DownloadEntryState.Completed;
            using (File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                f.Window.SignOut();
                UlSet(f.Window, "_session", FakeSession);
                UlSet(f.Window, "_account", new Account("other", "Other", false));
                await Call(f.Window, "RestoreDownloadQueueAsync", "other");
                var work = f.Start([DcTrack("9")]);
                if (pause)
                {
                    await Until(() => f.Active == 1);
                    SaInvoke(f.Window, "PauseDownloadQueue");
                }
                await work;
                Assert(f.Queue.Entries.Single().State == expectedState && File.ReadAllBytes(path).SequenceEqual(old)
                    && notice.Text.Contains("до закрытия приложения", StringComparison.Ordinal), "current account did not start in protective mode");
                Assert(SaInvokeResult(f.Window, "TrySaveDownloadQueueCheckpoint") is false && File.ReadAllBytes(path).SequenceEqual(old),
                    "the active account suppressed an unresolved exact cleanup warning on exit");
            }

            if (mode.StartsWith("replacement", StringComparison.Ordinal))
            {
                var replacement = DrDirectory("retry-foreign-logout-replacement-source-" + mode);
                new DownloadQueueStore(replacement).Save(DrSnapshot("other", "Newer unrelated checkpoint"));
                File.Move(Path.Combine(replacement, DownloadQueueStore.FileName), path, overwrite: true);
                old = File.ReadAllBytes(path);
            }
            if (mode == "center") SaInvoke(f.Window, "DownloadCenter_Click", null!, null!);
            else Assert(Equals(SaInvokeResult(f.Window, "TrySaveDownloadQueueCheckpoint"), mode != "replacement-paused"),
                "close suppressed unfinished in-memory work or could not resolve the previous logout");

            if (mode.StartsWith("replacement", StringComparison.Ordinal))
                Assert(File.ReadAllBytes(path).SequenceEqual(old) && notice.Text.Contains("до закрытия приложения", StringComparison.Ordinal),
                    "a changed checkpoint was overwritten or presented as this queue's checkpoint");
            else
            {
                await Until(() => store.Load("other") is not null && !notice.Text.Contains("до закрытия приложения", StringComparison.Ordinal));
                Assert(store.Load("other")!.Entries.Single() is { Track.Id: "9" } persisted && persisted.State == expectedState
                    && f.Requests.GetValueOrDefault("9") == 1, "current in-memory work was lost, restored, or repeated during safe publication");
                if (mode == "close")
                {
                    f.Window.Close();
                    using var reopened = new DownloadCenterFixture("retry-foreign-logout-close", 1);
                    DrEnable(reopened);
                    UlSet(reopened.Window, "_account", new Account("other", "Other", false));
                    await Call(reopened.Window, "RestoreDownloadQueueAsync", "other");
                    Assert(reopened.Queue.CanResume && reopened.Queue.Entries.Single().Track.Id == "9"
                        && reopened.Requests.Count == 0, "the current account could not reopen its own paused queue");
                }
            }
        }
    }

    private static async Task DrDeletionKeepsCheckpoint()
    {
        string directory;
        using (var f = new DownloadCenterFixture("single-deletion-checkpoint", 1))
        {
            DrEnable(f);
            directory = f.DirectoryPath;
            var store = new DownloadQueueStore(directory);
            var queue = new DownloadQueueState(); queue.Replace([DcTrack("1"), DcTrack("2")], "Paused work"); queue.Pause();
            queue.Entries[0].Complete(DownloadCenterFixture.Media.Length);
            store.Save(DownloadQueueStore.Capture("fixture", queue, AudioQuality.Optimal, false, null));
            var path = Path.Combine(directory, DownloadQueueStore.FileName);
            var checkpoint = File.ReadAllBytes(path);
            async Task PrepareUnrelatedTrack(string id) => await f.Library.SaveAsync(DcTrack(id),
                new(new Uri($"https://s1.vlx.yandex.net/{id}.mp3"), "mp3", 320), default);

            await PrepareUnrelatedTrack("9");
            await PrepareUnrelatedTrack("1");
            f.Window.DownloadQueueRestorePrepareAsync = _ => Task.FromException<RestoredDownloadQueue>(new IOException("Fixture index is busy"));
            await Call(f.Window, "RestoreDownloadQueueAsync", "fixture");
            using (File.Open(path, FileMode.Open, FileAccess.Read, FileShare.None))
                await Call(f.Window, "DeleteOfflineTrackAsync", DcTrack("1"));
            await Call(f.Window, "DeleteOfflineTrackAsync", DcTrack("1"));
            Assert(f.Library.Find(DcTrack("1")) is not null && File.ReadAllBytes(path).SequenceEqual(checkpoint)
                && Control<TextBlock>(f.Window, "StatusText").Text.Contains("Сначала восстановите очередь", StringComparison.Ordinal),
                "deletion changed a track whose checkpoint could not yet be reconciled");
            await Call(f.Window, "DeleteOfflineTrackAsync", DcTrack("9"));
            Assert(f.Library.Find(DcTrack("9")) is null && File.ReadAllBytes(path).SequenceEqual(checkpoint)
                && SaField<bool>(f.Window, "_downloadRestoreNeedsRetry"), "unrelated removal discarded a protected queue or left the selected file");
            f.Window.DownloadQueueRestorePrepareAsync = null;
            SaInvoke(f.Window, "DownloadCenter_Click", null!, null!);
            await Until(() => f.Queue.CanResume);

            await PrepareUnrelatedTrack("8");
            var alternate = await f.Library.SaveAsync(DcTrack("1"),
                new(new Uri("https://s1.vlx.yandex.net/1-alternate.mp3"), "mp3", 128), default);
            var changedPath = f.Library.ResolvePath(alternate.FileName);
            var changed = File.ReadAllBytes(changedPath); changed[^1] ^= 1; File.WriteAllBytes(changedPath, changed);
            await Call(f.Window, "DeleteOfflineTrackAsync", DcTrack("1"));
            Assert(f.Library.Read().All(item => item.Id != "1") && File.ReadAllBytes(changedPath).SequenceEqual(changed),
                "deletion did not forget every managed row or modified the externally changed alternate");
            await Call(f.Window, "DeleteOfflineTrackAsync", DcTrack("8"));
            Assert(f.Library.Find(DcTrack("8")) is null && f.Queue.CanResume && SaInvokeResult(f.Window, "TrySaveDownloadQueueCheckpoint") is true,
                "unrelated removal suspended the remaining explicit pause or disabled saving it");
        }
        using var reopened = new DownloadCenterFixture("single-deletion-checkpoint", 1);
        DrEnable(reopened);
        Assert(reopened.DirectoryPath == directory, "fixture did not reopen the same checkpoint");
        await Call(reopened.Window, "RestoreDownloadQueueAsync", "fixture");
        Assert(reopened.Queue.CanResume && reopened.Requests.Count == 0
            && reopened.Queue.Entries.Select(entry => (entry.Track.Id, entry.State)).SequenceEqual(
                [("1", DownloadEntryState.Removed), ("2", DownloadEntryState.Paused)]), "reopen revived a removed track or lost explicitly paused work");
    }

    private static async Task DrRecheckEmptyAfterForeignLogout()
    {
        foreach (var replace in new[] { false, true })
        {
            using var f = new DownloadCenterFixture("recheck-empty-after-foreign-logout-" + replace, 1);
            DrEnable(f);
            var store = SaField<DownloadQueueStore>(f.Window, "_downloadQueueStore");
            store.Save(DrSnapshot());
            var path = Path.Combine(f.DirectoryPath, DownloadQueueStore.FileName);
            using (File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                f.Window.SignOut();
                UlSet(f.Window, "_session", FakeSession);
                UlSet(f.Window, "_account", new Account("other", "Other", false));
                await Call(f.Window, "RestoreDownloadQueueAsync", "other");
                Assert(f.Queue.Entries.Count == 0 && f.Requests.Count == 0, "another owner's retained checkpoint reached the empty queue");
                if (!replace)
                {
                    var favorite = DcTrack("9");
                    UlSet(f.Window, "_autoFavoritesEnabled", true);
                    SaField<HashSet<string>>(f.Window, "_favoriteTrackIds").Add(favorite.Id);
                    SaInvoke(f.Window, "ScheduleConfirmedFavoriteDownload", favorite);
                    Assert(f.Requests.Count == 0, "pending retirement started guarded automatic work prematurely");
                }
            }
            if (replace)
            {
                var queue = new DownloadQueueState(); queue.Replace([DcTrack("7")], "Paused B"); queue.Pause();
                var replacement = DrDirectory("recheck-empty-safe-b");
                new DownloadQueueStore(replacement).Save(DownloadQueueStore.Capture("other", queue, AudioQuality.Optimal, false, null));
                File.Move(Path.Combine(replacement, DownloadQueueStore.FileName), path, overwrite: true);
            }
            await Call(f.Window, "RetryDownloadQueueRestoreAsync");
            if (replace)
                Assert(f.Requests.Count == 0 && f.Queue.CanResume && f.Queue.Entries.Single().Track.Id == "7", "retry did not restore B's verified pause");
            else
            {
                if (SaField<Task?>(f.Window, "_downloadTask") is { } download) await download.WaitAsync(TimeSpan.FromSeconds(10));
                Assert(f.Requests.GetValueOrDefault("9") == 1 && store.Load("other")?.Entries.Single().State == DownloadEntryState.Completed,
                    "empty recovered slot did not admit pending verified work or its checkpoint");
            }
        }
    }

    private static Task DrLogoutObservesCurrentRevision()
    {
        foreach (var mode in new[] { "current", "later", "other" })
        {
            using var f = new DownloadCenterFixture("explicit-logout-observes-current-" + mode, 1);
            DrEnable(f);
            var store = SaField<DownloadQueueStore>(f.Window, "_downloadQueueStore");
            store.Save(DrSnapshot(label: "Previously observed queue"));
            var path = Path.Combine(f.DirectoryPath, DownloadQueueStore.FileName);
            byte[] Publish(string name, string uid)
            {
                var directory = DrDirectory("explicit-logout-physical-" + mode + "-" + name);
                new DownloadQueueStore(directory).Save(DrSnapshot(uid, name));
                File.Move(Path.Combine(directory, DownloadQueueStore.FileName), path, overwrite: true);
                return File.ReadAllBytes(path);
            }
            var current = Publish("Current queue", mode == "other" ? "other" : "fixture");
            using (File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                f.Window.SignOut();
                Assert(File.ReadAllBytes(path).SequenceEqual(current), "logout changed bytes while a reader denied deletion");
                if (mode != "other")
                {
                    var receipt = SaField<Dictionary<string, DownloadQueueStore.ClearRetry>>(f.Window, "_pendingDownloadQueueClears")["fixture"];
                    Assert(receipt is { File: not null, Hash: not null } && SaInvokeResult(f.Window, "TrySaveDownloadQueueCheckpoint") is false,
                        "logout did not record the readable current revision while deletion was denied");
                }
            }
            if (mode == "later") current = Publish("Later queue", "fixture");
            Assert(SaInvokeResult(f.Window, "TrySaveDownloadQueueCheckpoint") is true && (mode == "current"
                ? !File.Exists(path) : File.ReadAllBytes(path).SequenceEqual(current)),
                "automatic retry retained the observed logout target or cleared different evidence");
        }
        return Task.CompletedTask;
    }
    private static async Task DrAccountVisibility()
    {
        using var fixture = new DownloadCenterFixture("restore-account-visibility", 1) { HoldFirstAttempts = true };
        DrEnable(fixture);
        var window = fixture.Window;
        var work = fixture.Start([DcTrack("2")]);
        await Until(() => fixture.Active == 1);
        var list = SaField<BatchSelectionListView>(window, "_downloadEntriesList");
        var summary = SaField<TextBlock>(window, "_downloadCenterSummary");
        var notice = SaField<TextBlock>(window, "_downloadCenterNotice");
        var progress = SaField<FrameworkElement>(window, "_queueProgressPanel");
        var progressDetails = SaField<TextBlock>(window, "_queueProgressDetails");
        Assert(list.Items.Count == 1 && summary.Text.Contains("Тестовая очередь", StringComparison.Ordinal), "initial account queue was hidden");

        window.SignOut();
        AssertHidden();
        var revision = SaField<long>(window, "_downloadQueueAdmissionRevision");
        await Call(window, "DownloadSelectedAsync");
        Assert(SaField<long>(window, "_downloadQueueAdmissionRevision") == revision
            && !Control<Button>(window, "DownloadButton").IsEnabled, "signed-out cancel changed the previous worker's admission");
        await work;
        SaField<CancellationTokenSource>(window, "_accountCts").Dispose();
        UlSet(window, "_accountCts", new CancellationTokenSource());
        UlSet(window, "_session", FakeSession);
        UlSet(window, "_account", new Account("other", "Other", false));
        var read = new TaskCompletionSource<DownloadQueueSnapshot?>(TaskCreationOptions.RunContinuationsAsynchronously);
        window.DownloadQueueRestoreReadAsync = _ => read.Task;
        var restore = Call(window, "RestoreDownloadQueueAsync", "other");
        try
        {
            Assert(!restore.IsCompleted && fixture.Queue.Entries.Single().Track.Id == "2", "fixture did not hold the old model during restore");
            SaInvoke(window, "RefreshDownloadCenter");
            AssertHidden();
        }
        finally { read.TrySetResult(DrSnapshot("other", "Очередь другого аккаунта")); }
        await restore;
        Assert(list.Items.Count == 3 && summary.Text.Contains("Очередь другого аккаунта", StringComparison.Ordinal), "new account could not display its restored queue");

        void AssertHidden() => Assert(list.Items.Count == 0 && summary.Text == DownloadQueueState.EmptyLabel
            && notice.Text == DownloadQueueState.EmptyNotice && progress.Visibility == Visibility.Collapsed
            && progressDetails.Text.Length == 0, "old account rows, queue text, or progress remained visible");
    }
    private static async Task DrPendingOfflineRecovery()
    {
        foreach (var restoring in new[] { true, false })
        {
            using var fixture = new DownloadCenterFixture("pending-offline-recovery-" + restoring, 1);
            var state = typeof(OfflineLibrary).GetField("_directoryState", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(fixture.Library)!;
            var recovery = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            state.GetType().GetField("RecoveryTask", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(state, recovery.Task);
            Task work;
            if (restoring)
            {
                DrEnable(fixture);
                fixture.Window.DownloadQueueRestoreReadAsync = _ => Task.FromResult<DownloadQueueSnapshot?>(DrSnapshot());
                work = Call(fixture.Window, "RestoreDownloadQueueAsync", "fixture");
            }
            else
            {
                work = Call(fixture.Window, "StartDownloadBatchAsync", new[] { DcTrack("1") }, "Автоскачивание", null!, true);
            }
            try
            {
                await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);
                Assert(!work.IsCompleted && fixture.Requests.Count == 0, "a download operation read through pending recovery");
            }
            finally { recovery.TrySetResult(); }
            await work.WaitAsync(TimeSpan.FromSeconds(5));
            Assert(restoring ? fixture.Queue.CanResume && !SaField<bool>(fixture.Window, "_downloadPersistenceWriteBlocked")
                : fixture.Requests.GetValueOrDefault("1") == 1 && fixture.Queue.Entries.Single().State == DownloadEntryState.Completed,
                "a temporary offline recovery blocked later progress");
        }
    }
    private static async Task DrRetryOfflineRecovery()
    {
        using var fixture = new DownloadCenterFixture("retry-offline-recovery", 1);
        DrEnable(fixture);
        var store = new DownloadQueueStore(fixture.DirectoryPath);
        store.Save(DrSnapshot());
        var path = Path.Combine(fixture.DirectoryPath, DownloadQueueStore.FileName);
        var saved = File.ReadAllBytes(path);
        var state = typeof(OfflineLibrary).GetField("_directoryState", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(fixture.Library)!;
        var recovery = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        state.GetType().GetField("RecoveryTask", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(state, recovery.Task);
        var recoveryEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Window.DownloadQueueRestoreReadAsync = uid =>
        {
            recoveryEntered.TrySetResult();
            return Task.FromResult(store.Load(uid));
        };

        var restore = Call(fixture.Window, "RestoreDownloadQueueAsync", "fixture");
        await recoveryEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        recovery.TrySetException(new IOException("Fixture temporary file lock"));
        await restore.WaitAsync(TimeSpan.FromSeconds(5));
        Assert(SaField<bool>(fixture.Window, "_downloadRestoreNeedsRetry") && fixture.Queue.Entries.Count == 0
            && File.ReadAllBytes(path).SequenceEqual(saved), "a transient recovery failure discarded or overwrote the checkpoint");

        SaInvoke(fixture.Window, "DownloadCenter_Click", null!, null!);
        await Until(() => fixture.Queue.CanResume && !SaField<bool>(fixture.Window, "_downloadPersistenceWriteBlocked"));
        Assert(fixture.Queue.Entries.Count == 3 && fixture.Requests.Count == 0, "opening the center did not restore the paused checkpoint");
    }
    private static async Task DrRetryUnreadableCheckpoint()
    {
        foreach (var lockedFile in new[] { true, false })
        {
            using var fixture = new DownloadCenterFixture("retry-unreadable-checkpoint-" + lockedFile, 1);
            DrEnable(fixture);
            var store = new DownloadQueueStore(fixture.DirectoryPath);
            var queue = new DownloadQueueState(); queue.Replace([DcTrack("1"), DcTrack("2"), DcTrack("3")], "Исходная очередь");
            store.Save(DownloadQueueStore.Capture("fixture", queue, AudioQuality.Optimal, false, null));
            var path = Path.Combine(fixture.DirectoryPath, DownloadQueueStore.FileName);
            var bytes = File.ReadAllBytes(path);
            if (lockedFile)
            {
                using (File.Open(path, FileMode.Open, FileAccess.Read, FileShare.None))
                    await Call(fixture.Window, "RestoreDownloadQueueAsync", "fixture");
            }
            else
            {
                fixture.Window.DownloadQueueRestorePrepareAsync = _ => Task.FromException<RestoredDownloadQueue>(new IOException("Fixture index is busy"));
                await Call(fixture.Window, "RestoreDownloadQueueAsync", "fixture");
                fixture.Window.DownloadQueueRestorePrepareAsync = null;
            }
            await fixture.Start([DcTrack("9")]);
            Assert(SaField<bool>(fixture.Window, "_downloadRestoreNeedsRetry") && fixture.Queue.Entries.Count == 0
                && fixture.Requests.Count == 0 && File.ReadAllBytes(path).SequenceEqual(bytes), "a new batch overwrote unreadable work");

            var read = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            fixture.Window.DownloadQueueRestoreReadAsync = async uid => { await read.Task; return store.Load(uid); };
            SaInvoke(fixture.Window, "DownloadCenter_Click", null!, null!);
            try
            {
                await fixture.Start([DcTrack("9")]);
                Assert(fixture.Requests.Count == 0 && File.ReadAllBytes(path).SequenceEqual(bytes), "a new batch overwrote a held retry");
            }
            finally { read.TrySetResult(); }

            await Until(() => fixture.Queue.CanResume && !SaField<bool>(fixture.Window, "_downloadRestoreNeedsRetry"));
            Assert(fixture.Queue.Entries.Select(entry => entry.Track.Id).SequenceEqual(["1", "2", "3"])
                && fixture.Queue.Entries.All(entry => entry.State == DownloadEntryState.Paused) && fixture.Requests.Count == 0,
                "Download Center did not restore the original three paused tracks");
        }
    }
    private static async Task DrReadStatusOwnership()
    {
        using var f = new DownloadCenterFixture("restore-status-ownership", 1);
        DrEnable(f);
        var store = SaField<DownloadQueueStore>(f.Window, "_downloadQueueStore");
        store.Save(DrSnapshot());
        var path = Path.Combine(f.DirectoryPath, DownloadQueueStore.FileName);
        DownloadQueueReadResult blocked;
        using (File.Open(path, FileMode.Open, FileAccess.Read, FileShare.None)) blocked = store.Read("fixture");
        Assert(blocked.Status == DownloadQueueReadStatus.Unavailable && blocked.Snapshot is null, "fixture did not obtain its unavailable result");
        var held = new TaskCompletionSource<DownloadQueueReadResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Window.DownloadQueueRestoreReadResultAsync = _ => held.Task;
        var restore = Call(f.Window, "RestoreDownloadQueueAsync", "fixture");
        Assert(store.Load("other") is null && store.LastReadStatus == DownloadQueueReadStatus.OtherAccount,
            "fixture did not supersede the old mutable side channel");
        held.TrySetResult(blocked);
        await restore;
        Assert(SaField<bool>(f.Window, "_downloadRestoreNeedsRetry") && !f.Queue.CanResume,
            "another read changed this restore's status");
        f.Window.DownloadQueueRestoreReadResultAsync = null;
        await Call(f.Window, "RestoreDownloadQueueAsync", "fixture");
        Assert(f.Queue.CanResume && f.Requests.Count == 0, "the protected original snapshot could not be restored");
    }
    private static async Task DrInMemoryWhenCheckpointIsUnsafe()
    {
        using var fixture = new DownloadCenterFixture("restore-unsafe-checkpoint", 1);
        DrEnable(fixture);
        var path = Path.Combine(fixture.DirectoryPath, DownloadQueueStore.FileName);
        Directory.CreateDirectory(path);
        await Call(fixture.Window, "RestoreDownloadQueueAsync", "fixture");
        Assert(SaField<bool>(fixture.Window, "_preserveUnreadableDownloadCheckpoint")
            && !SaField<bool>(fixture.Window, "_downloadRestoreNeedsRetry") && Directory.Exists(path),
            "an unsafe object was treated as a temporary file lock or replaced");

        Directory.Delete(path, recursive: false);
        new DownloadQueueStore(fixture.DirectoryPath).Save(DrSnapshot());
        var saved = File.ReadAllBytes(path);
        await fixture.Start([DcTrack("9")]);
        SaInvoke(fixture.Window, "TrySaveDownloadQueueCheckpoint");
        Assert(fixture.Requests.GetValueOrDefault("9") == 1 && fixture.Queue.Entries.Single().State == DownloadEntryState.Completed
            && File.ReadAllBytes(path).SequenceEqual(saved)
            && SaField<TextBlock>(fixture.Window, "_downloadCenterNotice").Text.Contains("до закрытия приложения", StringComparison.Ordinal),
            "the explicit manual batch was blocked, overwritten, or presented as saved");
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
            if (close) SaInvoke(fixture.Window, "SaveDownloadQueueForClose");
            else SaInvoke(fixture.Window, "ClearPersistedDownloadQueue", true, false);
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
        invalid = JsonNode.Parse(good)!; invalid["entries"]![0]!["e"] = "token=SECRET"; bad.Add(invalid.ToJsonString());
        invalid = JsonNode.Parse(good)!; invalid["entries"]![0]!["e"] = 99; bad.Add(invalid.ToJsonString());
        invalid = JsonNode.Parse(good)!; invalid["playlist"]!["trackIds"]![0] = "999"; bad.Add(invalid.ToJsonString());
        invalid = JsonNode.Parse(good)!; invalid["entries"]![0]!["a"] = -1; bad.Add(invalid.ToJsonString());
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
            Assert(unsafeStore.Load("fixture") is null && unsafeStore.LastReadStatus == DownloadQueueReadStatus.UnsafePath
                && !Directory.EnumerateFileSystemEntries(target).Any(), "queue storage followed a reparse directory");
        }
        finally { Directory.Delete(linked, recursive: false); }
    }
    private static async Task DrCorruptIndex()
    {
        foreach (var repaired in new[] { false, true })
        {
            string path; byte[] before;
            using (var fixture = new DownloadCenterFixture("restore-corrupt-index-" + repaired, 1))
            {
                DrEnable(fixture);
                var store = new DownloadQueueStore(fixture.DirectoryPath); store.Save(DrSnapshot());
                path = Path.Combine(fixture.DirectoryPath, DownloadQueueStore.FileName); before = File.ReadAllBytes(path);
                Directory.CreateDirectory(fixture.Library.DirectoryPath);
                var index = Path.Combine(fixture.Library.DirectoryPath, "library.index.json");
                File.WriteAllText(index, "{");
                await Call(fixture.Window, "RestoreDownloadQueueAsync", "fixture");
                await fixture.Start([DcTrack("9")]);
                await Call(fixture.Window, "PersistDownloadQueueAsync", true);
                Assert(fixture.Requests.Count == 0 && fixture.Queue.Entries.Count == 0 && SaField<bool>(fixture.Window, "_downloadPersistenceWriteBlocked")
                    && before.SequenceEqual(File.ReadAllBytes(path)), "corrupt offline index admitted work or overwrote the stored queue");
                if (repaired)
                {
                    File.WriteAllText(index, "[]");
                    SaInvoke(fixture.Window, "DownloadCenter_Click", null!, null!);
                    await Until(() => fixture.Queue.CanResume);
                    Assert(fixture.Requests.Count == 0 && fixture.Queue.Entries.Select(entry => entry.Track.Id).SequenceEqual(["1", "2", "3"]),
                        "repair did not restore the former checkpoint in its existing order");
                }
            }
            if (!repaired) Assert(before.SequenceEqual(File.ReadAllBytes(path)), "closing after a restore failure overwrote the evidence");
        }
    }
    private static async Task DrCorruptPlaylistIndex()
    {
        using var f = new DownloadCenterFixture("restore-corrupt-playlist-index", 1);
        DrEnable(f);
        var file = Path.Combine(f.Library.DirectoryPath, "playlists.index.json");
        var queuePath = Path.Combine(f.DirectoryPath, DownloadQueueStore.FileName);
        var rows = new[] { DcTrack("1"), DcTrack("2") };
        var playlist = new PlaylistDetails("fixture-owner", "91", "Saved playlist", "", 1, rows);
        await f.Library.RememberPlaylistAsync(playlist, AudioQuality.Optimal, default);
        var validIndex = File.ReadAllBytes(file);
        var queue = new DownloadQueueState(); queue.Replace(rows, playlist.Title); queue.Pause();
        var store = new DownloadQueueStore(f.DirectoryPath);
        store.Save(DownloadQueueStore.Capture("fixture", queue, AudioQuality.Optimal, false, playlist));
        var checkpoint = File.ReadAllBytes(queuePath);
        File.WriteAllText(file, "{\"schemaVersion\":1,\"playlists\":[}");
        var malformed = File.ReadAllBytes(file);

        await Call(f.Window, "RestoreDownloadQueueAsync", "fixture");
        Assert(SaField<TextBlock>(f.Window, "_downloadCenterNotice").Text.Contains("Индекс офлайн-плейлистов повреждён", StringComparison.Ordinal),
            "permanent format failure was presented as a transient checkpoint error");
        await Call(f.Window, "StartDownloadBatchAsync", rows, playlist.Title, playlist, false);
        await f.Start([DcTrack("9")]);
        Assert(f.Requests.GetValueOrDefault("9") == 1 && f.Requests.Count == 1 && f.Library.Find(DcTrack("9")) is not null
            && File.ReadAllBytes(queuePath).SequenceEqual(checkpoint) && File.ReadAllBytes(file).SequenceEqual(malformed),
            "independent audio was refused or rewrote protected data");

        File.WriteAllBytes(file, validIndex);
        SaInvoke(f.Window, "DownloadCenter_Click", null!, null!);
        await Until(() => f.Queue.CanResume);
        Assert(f.Queue.Entries.Select(entry => entry.Track.Id).SequenceEqual(["1", "2"]), "repair did not recover the original queue");
        await Call(f.Window, "ResumeDownloadQueue");
        Assert(f.Requests.GetValueOrDefault("1") == 1 && f.Requests.GetValueOrDefault("2") == 1 && f.Requests.GetValueOrDefault("9") == 1
            && f.Library.GetPlaylists().Single().Tracks.Select(track => track.Id).SequenceEqual(["1", "2"]), "resume lost the repaired playlist or repeated independent audio");
    }

    private static async Task DrPreserveForeignCheckpoint()
    {
        using var f = new DownloadCenterFixture("verified-different-owner-preserves-checkpoint", 1);
        DrEnable(f);
        var store = new DownloadQueueStore(f.DirectoryPath);
        store.Save(DrSnapshot());
        var path = Path.Combine(f.DirectoryPath, DownloadQueueStore.FileName);
        var original = File.ReadAllBytes(path);
        UlSet(f.Window, "_account", new Account("other", "Other", false));
        await Call(f.Window, "RestoreDownloadQueueAsync", "other");
        Assert(f.Queue.Entries.Count == 0 && SaInvokeResult(f.Window, "TrySaveDownloadQueueCheckpoint") is true
            && File.ReadAllBytes(path).SequenceEqual(original), "an untouched different owner replaced the previous checkpoint with an empty one");
        Assert(!store.Save(DrSnapshot("other")) && File.ReadAllBytes(path).SequenceEqual(original),
            "the guarded transaction replaced a valid checkpoint belonging to another account");
        f.Window.SignOut();
        Assert(File.ReadAllBytes(path).SequenceEqual(original), "a fresh logout cleared another owner's verified checkpoint");
    }

    private static async Task DrDifferentOwnerExplicitBatch()
    {
        foreach (var replace in new[] { false, true })
        {
            using var f = new DownloadCenterFixture("verified-different-owner-explicit-batch-" + replace, 1) { HoldFirstAttempts = true };
            DrEnable(f);
            var store = new DownloadQueueStore(f.DirectoryPath);
            store.Save(DrSnapshot());
            var path = Path.Combine(f.DirectoryPath, DownloadQueueStore.FileName);
            UlSet(f.Window, "_account", new Account("other", "Other", false));
            await Call(f.Window, "RestoreDownloadQueueAsync", "other");
            if (replace) store.Save(DrSnapshot(label: "Newer old-owner checkpoint"));
            var expected = File.ReadAllBytes(path);
            var work = f.Start([DcTrack("9")]);
            await Until(() => f.Active == 1);
            SaInvoke(f.Window, "PauseDownloadQueue");
            await work;
            Assert(f.Queue.CanResume && Equals(SaInvokeResult(f.Window, "TrySaveDownloadQueueCheckpoint"), !replace),
                "closing failed to distinguish an authorized pause from one held behind a newer checkpoint");
            if (replace) Assert(File.ReadAllBytes(path).SequenceEqual(expected), "explicit work displaced a later publication");
            else Assert(store.Load("other")?.Entries.Single() is { Track.Id: "9", State: DownloadEntryState.Paused },
                "explicit work could not publish its own pause after confirming a new account");
        }
    }

    private static async Task DrOversizedPlaylistIndex()
    {
        using var f = new DownloadCenterFixture("restore-oversized-playlist-index", 1);
        DrEnable(f);
        var file = Path.Combine(f.Library.DirectoryPath, "playlists.index.json");
        var track = DcTrack("1");
        var playlist = new PlaylistDetails("fixture-owner", "91", "Saved playlist", "", 1, [track]);
        await f.Library.RememberPlaylistAsync(playlist, AudioQuality.Optimal, default);
        var validIndex = File.ReadAllBytes(file);
        var queue = new DownloadQueueState(); queue.Replace([track], playlist.Title); queue.Pause();
        var store = new DownloadQueueStore(f.DirectoryPath);
        store.Save(DownloadQueueStore.Capture("fixture", queue, AudioQuality.Optimal, false, playlist));
        var path = Path.Combine(f.DirectoryPath, DownloadQueueStore.FileName);
        var original = File.ReadAllBytes(path);
        using (var oversized = File.Open(file, FileMode.Open, FileAccess.Write)) oversized.SetLength(OfflineLibrary.MaximumIndexBytes + 1L);

        await Call(f.Window, "RestoreDownloadQueueAsync", "fixture");
        await f.Start([DcTrack("9")]);
        Assert(f.Requests.GetValueOrDefault("9") == 1 && f.Library.Find(DcTrack("9")) is not null
            && File.ReadAllBytes(path).SequenceEqual(original) && new FileInfo(file).Length == OfflineLibrary.MaximumIndexBytes + 1L,
            "oversized playlist index blocked separate audio or mutated protected data");
        File.WriteAllBytes(file, validIndex);
        await Call(f.Window, "RetryDownloadQueueRestoreAsync");
        Assert(f.Queue.CanResume && f.Queue.Entries.Single().Track.Id == "1", "restoring a bounded playlist index did not restore its checkpoint");
    }

    private static async Task DrKeepManualQueueAfterPlaylistRepair()
    {
        foreach (var pause in new[] { true, false })
        {
            using var f = new DownloadCenterFixture("preserve-manual-after-playlist-repair-" + pause, 1)
                { HoldFirstAttempts = pause, FailTrack = pause ? null : "9" };
            DrEnable(f);
            var file = Path.Combine(f.Library.DirectoryPath, "playlists.index.json");
            var track = DcTrack("1");
            var playlist = new PlaylistDetails("fixture-owner", "91", "Saved playlist", "", 1, [track]);
            await f.Library.RememberPlaylistAsync(playlist, AudioQuality.Optimal, default);
            var validIndex = File.ReadAllBytes(file);
            var queue = new DownloadQueueState(); queue.Replace([track], playlist.Title); queue.Pause();
            new DownloadQueueStore(f.DirectoryPath).Save(DownloadQueueStore.Capture("fixture", queue, AudioQuality.Optimal, false, playlist));
            var path = Path.Combine(f.DirectoryPath, DownloadQueueStore.FileName);
            var original = File.ReadAllBytes(path);
            File.WriteAllText(file, "{");
            await Call(f.Window, "RestoreDownloadQueueAsync", "fixture");
            var work = f.Start([DcTrack("9")]);
            if (pause)
            {
                await Until(() => f.Active == 1);
                SaInvoke(f.Window, "PauseDownloadQueue");
            }
            await work;
            File.WriteAllBytes(file, validIndex);
            await Call(f.Window, "RetryDownloadQueueRestoreAsync");
            Assert(f.Queue.Entries.Single().Track.Id == "9" && f.Queue.CanResume == pause && f.Queue.CanRetry != pause
                && SaInvokeResult(f.Window, "TrySaveDownloadQueueCheckpoint") is false && File.ReadAllBytes(path).SequenceEqual(original),
                "repair displaced independent unfinished work, overwrote the original checkpoint, or reported a safe exit");
            f.FailTrack = null;
            await Call(f.Window, pause ? "ResumeDownloadQueue" : "RetryFailedDownloads");
            await Call(f.Window, "RetryDownloadQueueRestoreAsync");
            Assert(f.Queue.CanResume && f.Queue.Entries.Single().Track.Id == "1" && f.Requests.GetValueOrDefault("9") == 2
                && f.Library.Find(DcTrack("9")) is not null, "finishing live work did not make the original checkpoint available");
        }
    }

    private static async Task DrDeleteWhenCheckpointReadRefused()
    {
        foreach (var malformed in new[] { false, true })
        {
            using var f = new DownloadCenterFixture("delete-preserved-checkpoint-" + malformed, 1);
            DrEnable(f);
            var tracks = new[] { DcTrack("1"), DcTrack("2") };
            var target = tracks[0]; var unrelated = DcTrack("9");
            foreach (var track in new[] { target, unrelated })
                await f.Library.SaveAsync(track, new(new Uri($"https://s1.vlx.yandex.net/{track.Id}.mp3"), "mp3", 320), default);
            var playlist = new PlaylistDetails("fixture-owner", "91", "Saved playlist", "", 1, tracks);
            await f.Library.RememberPlaylistAsync(playlist, AudioQuality.Optimal, default);
            var file = Path.Combine(f.Library.DirectoryPath, "playlists.index.json");
            var validIndex = File.ReadAllBytes(file);
            var queue = new DownloadQueueState(); queue.Replace(tracks, playlist.Title); queue.Pause();
            queue.Entries[0].Complete(DownloadCenterFixture.Media.Length);
            var path = Path.Combine(f.DirectoryPath, DownloadQueueStore.FileName);
            new DownloadQueueStore(f.DirectoryPath).Save(DownloadQueueStore.Capture("fixture", queue, AudioQuality.Optimal, false, playlist));
            var original = File.ReadAllBytes(path);
            if (malformed) File.WriteAllText(file, "{");
            else f.Window.DownloadQueueRestoreReadResultAsync = _ => Task.FromResult(new DownloadQueueReadResult(null, DownloadQueueReadStatus.AccessDenied));
            await Call(f.Window, "RestoreDownloadQueueAsync", "fixture");
            if (malformed) File.WriteAllBytes(file, validIndex);
            f.Window.DownloadQueueRestoreReadResultAsync = null;

            await Call(f.Window, "DeleteOfflineTrackAsync", target);
            await Call(f.Window, "DeleteOfflineTrackAsync", unrelated);
            Assert(f.Library.Find(target) is not null && f.Library.Find(unrelated) is null && File.ReadAllBytes(path).SequenceEqual(original),
                "single deletion bypassed the preserved checkpoint, or refused its unrelated sibling");
            await Call(f.Window, "RetryDownloadQueueRestoreAsync");
            await Call(f.Window, "DeleteOfflineTrackAsync", target);
            await Call(f.Window, "ResumeDownloadQueue");
            Assert(f.Requests.GetValueOrDefault("1") == 1 && f.Requests.GetValueOrDefault("2") == 1
                && f.Queue.Entries[0].State == DownloadEntryState.Removed, "restored queue fetched the explicitly removed track again");
        }
    }

    private static async Task DrCloseWaitsForOfflineRemoval()
    {
        const string name = "close-waits-for-offline-removal";
        using (var f = new DownloadCenterFixture(name, 1))
        {
            DrEnable(f);
            SaInvoke(f.Window, "InitializePlaybackPersistence");
            SaInvoke(f.Window, "RestorePlaybackState", "fixture");
            var target = DcTrack("1");
            await f.Library.SaveAsync(target, new(new Uri("https://s1.vlx.yandex.net/1.mp3"), "mp3", 320), default);
            var queue = new DownloadQueueState(); queue.Replace([target, DcTrack("2")], "Paused work"); queue.Pause();
            queue.Entries[0].Complete(DownloadCenterFixture.Media.Length);
            new DownloadQueueStore(f.DirectoryPath).Save(DownloadQueueStore.Capture("fixture", queue, AudioQuality.Optimal, false, null));
            await Call(f.Window, "RestoreDownloadQueueAsync", "fixture");
            var committed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var release = new ManualResetEventSlim();
            f.Library.RemovalCheckpoint = stage =>
            {
                if (stage != "after-library-commit") return;
                committed.TrySetResult();
                release.Wait(TimeSpan.FromSeconds(12));
            };
            string? prompt = null;
            f.Window.CloseSaveFailurePrompt = failure => { prompt = failure; return MessageBoxResult.No; };
            var removal = Call(f.Window, "DeleteOfflineTrackAsync", target);
            try
            {
                await committed.Task.WaitAsync(TimeSpan.FromSeconds(5));
                f.Window.Close();
                await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                Assert(!SaField<bool>(f.Window, "_closed") && SaField<bool>(f.Window, "_closeCommitInProgress"),
                    "close discarded a pending removal after its offline index commit");
            }
            finally { release.Set(); }
            await removal;
            await Until(() => SaField<bool>(f.Window, "_closed"));
            Assert(prompt is null, "a successfully finished deletion caused an unsaved-work prompt: " + prompt);
        }
        using var reopened = new DownloadCenterFixture(name, 1);
        DrEnable(reopened);
        await Call(reopened.Window, "RestoreDownloadQueueAsync", "fixture");
        Assert(reopened.Queue.Entries[0].State == DownloadEntryState.Removed && reopened.Queue.CanResume,
            "the final close checkpoint missed the already committed removal");
        await Call(reopened.Window, "ResumeDownloadQueue");
        Assert(reopened.Requests.Count == 1 && reopened.Requests.GetValueOrDefault("2") == 1,
            "reopening fetched the track removed before close");
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
    private static Task DrRetryClearIdentity()
    {
        foreach (var mode in new[] { "unchanged", "renamed-same-file", "same-owner-replacement", "other-owner-replacement", "same-file-new-revision" })
        {
            const string owner = "fixture";
            var directory = DrDirectory("retire-" + mode);
            var path = Path.Combine(directory, DownloadQueueStore.FileName);
            var store = new DownloadQueueStore(directory);
            store.Save(DrSnapshot(owner, "Старая очередь"));
            DownloadQueueStore.ClearRetry? pending = null;
            var failed = false;
            using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                try { store.Clear(owner, out pending); }
                catch (IOException) { failed = true; }
            }
            Assert(failed && pending is { File: not null, AccountUid: owner },
                "logout failed to retain the identity and revision of its blocked checkpoint");

            if (mode == "renamed-same-file")
            {
                File.Move(path, path + ".held");
                File.Move(path + ".held", path);
                File.SetLastWriteTimeUtc(path, File.GetLastWriteTimeUtc(path).AddSeconds(10));
            }
            if (mode is "unchanged" or "renamed-same-file")
            {
                Assert(store.RetryClear(pending!.Value) && store.Load(owner) is null && !File.Exists(path),
                    "retry left the exact retired checkpoint eligible for restoration");
                continue;
            }

            var replacementOwner = mode == "other-owner-replacement" ? "other-fixture" : owner;
            var replacement = DrDirectory("retire-source-" + mode);
            var replacementFile = Path.Combine(replacement, DownloadQueueStore.FileName);
            const string label = "Новое состояние загрузок с более длинным названием";
            new DownloadQueueStore(replacement).Save(DrSnapshot(replacementOwner, label));
            var expected = File.ReadAllBytes(replacementFile);
            if (mode == "same-file-new-revision") File.WriteAllBytes(path, expected);
            else File.Move(replacementFile, path, overwrite: true);
            Assert(store.RetryClear(pending!.Value) && File.ReadAllBytes(path).SequenceEqual(expected)
                && store.Load(replacementOwner)?.Label == label,
                "an old retry changed a replacement or a newer revision of the same file");
        }
        return Task.CompletedTask;
    }

    private static async Task DrRetryCheckpoint()
    {
        using var fixture = new DownloadCenterFixture("restore-busy-checkpoint", 1);
        DrEnable(fixture);
        var store = new DownloadQueueStore(fixture.DirectoryPath); store.Save(DrSnapshot());
        await Call(fixture.Window, "RestoreDownloadQueueAsync", "fixture");
        var path = Path.Combine(fixture.DirectoryPath, DownloadQueueStore.FileName);
        var before = File.ReadAllBytes(path);
        using (var busy = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            await Call(fixture.Window, "PersistDownloadQueueAsync", true);
            Assert(SaField<bool>(fixture.Window, "_downloadPersistenceDirty") && before.SequenceEqual(File.ReadAllBytes(path))
                && !Directory.EnumerateFiles(fixture.DirectoryPath, "*.tmp").Any(), "failed replacement discarded work or left its temporary file");
        }
        await Call(fixture.Window, "PersistDownloadQueueAsync", false);
        Assert(store.Load("fixture")?.Entries[1].State == DownloadEntryState.Paused
            && !SaField<bool>(fixture.Window, "_downloadPersistenceDirty"), "checkpoint did not retry after the previous file became writable");
    }
    private static Task DrUninstall()
    {
        var root = DrDirectory("uninstall"); var paths = UninstallPaths.FromKnownFolders(Path.Combine(root, "Local"), Path.Combine(root, "Documents"));
        Directory.CreateDirectory(paths.Documents);
        new DownloadQueueStore(paths.Data).Save(DrSnapshot());
        var result = UninstallCleanup.Execute(paths);
        Assert(result.Complete && !Directory.Exists(paths.Data), "full uninstall left the owned queue file behind");
        return Task.CompletedTask;
    }
}
