using System.ComponentModel;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using YMusic.Native;

internal static partial class Program
{
    private const string PaCover = "https://avatars.yandex.net/get-music-content/123/restore-%%";
    private const string PaOtherCover = "https://avatars.mds.yandex.net/get-music-content/456/other-%%?1730453322475";

    private static async Task CheckPlaybackArtworkRestore()
    {
        await Run("PlaybackArtwork: safe canonical artwork roundtrips every occurrence without audio locations", PaRoundtrip);
        await Run("PlaybackArtwork: shared track metadata retains playback covers and forbids them in download snapshots", PaDownloadContract);
        await Run("PlaybackArtwork: legacy version one and validated offline artwork fallback remain compatible", PaLegacyOffline);
        await Run("PlaybackArtwork: restored offline occurrence keeps its bound edition and never streams missing audio", PaOfflineCheckpointProvenance);
        await Run("PlaybackArtwork: saved locally selected qualities remain distinct and unbound rows can gain a copy", PaOfflineCheckpointQualities);
        await Run("PlaybackArtwork: exact bytes distinguish known and legacy copies with the same nominal quality", PaOfflineCheckpointMeasuredVariants);
        await Run("PlaybackArtwork: opening the unique legacy copy upgrades its exact audio proof", PaLegacyExactCopyProof);
        await Run("PlaybackArtwork: a restored legacy path keeps its exact proof when another copy arrives before play", PaLegacyCopyBeforePlay);
        await Run("PlaybackArtwork: close and apply keep the expected legacy copy before its first play", PaLegacyExpectedCopyBeforePlay);
        await Run("PlaybackArtwork: a verified legacy opening keeps its SHA when the index changes before commit", PaLateLegacyExactCopyProof);
        await Run("PlaybackArtwork: prepared promotion keeps the verified legacy candidate proof", PaPreparedLegacyExactCopyProof);
        await Run("PlaybackArtwork: the already open queue can explicitly play a returning exact copy", PaReturningExactQueueCopy);
        await Run("PlaybackArtwork: opening a late local copy binds its edition to the next checkpoint", PaLateOfflineBinding);
        await Run("PlaybackArtwork: offline audio from another edition leaves the exact cover for hydration", PaDifferentOfflineAlbum);
        await Run("PlaybackArtwork: optional covers cannot crowd out a valid queue or resume position", PaArtworkBudget);
        await Run("PlaybackArtwork: untrusted persisted artwork is rejected and capture never saves signed locations", PaUnsafe);
        await Run("PlaybackArtwork: missing-only artwork updates notify existing row bindings once", PaNotifications);
        await Run("PlaybackArtwork: cold new windows render saved player and queue covers while paused", PaReopen);
        await Run("PlaybackArtwork: held legacy hydration batches exact composite IDs current first and preserves row selection", PaHydrate);
        await Run("PlaybackArtwork: live occurrences added during hydration receive only their exact edition cover", PaHydrateQueuedOccurrence);
        await Run("PlaybackArtwork: undo during a later page hydrates the skipped exact occurrence", PaHydrateUndoSkippedOccurrence);
        await Run("PlaybackArtwork: undo between track attempts keeps the exact album fallback", PaHydrateUndoBetweenAttempts);
        await Run("PlaybackArtwork: undo during an album request uses its exact confirmed membership", PaHydrateUndoDuringAlbum);
        await Run("PlaybackArtwork: undo reopens only a skipped exact album after the first album pass", PaHydrateUndoSkippedAlbum);
        await Run("PlaybackArtwork: undo resumes an album with only its remaining attempt", PaHydrateUndoAlbumAfterAttempt);
        await Run("PlaybackArtwork: undo retains a skipped track retry and admits a first track request after albums", PaHydrateUndoLateTracks);
        await Run("PlaybackArtwork: undo after hydration retains the returned occurrence's exact proof", PaHydrateLateUndo);
        await Run("PlaybackArtwork: undo while a snapshot is pending saves the already resolved cover", PaHydrateUndoDuringSave);
        await Run("PlaybackArtwork: temporary page failure retries exact live references and leaves other pages independent", PaHydratePageRetry);
        await Run("PlaybackArtwork: an exact album supplies missing covers across an exhausted track page", PaHydrateAlbumAfterFailedPage);
        await Run("PlaybackArtwork: an exhausted track page with one missing cover can use the exact album", PaHydrateOnlyFailedPage);
        await Run("PlaybackArtwork: Retry-After paces later pages and cancels on logout", PaHydrateRetryAfter);
        await Run("PlaybackArtwork: confirmed parent artwork requires exact album membership and yields to direct covers", PaConfirmedParentAlbum);
        await Run("PlaybackArtwork: exact member artwork wins with or without a parent cover", PaExactAlbumMemberArtwork);
        await Run("PlaybackArtwork: restored cover appears in open lyrics without loading text again", PaHydrateLyrics);
        await Run("PlaybackArtwork: metadata with wrong album or unsafe cover cannot fill restored artwork", PaMetadataGuards);
        await Run("PlaybackArtwork: bare legacy IDs use confirmed saved album hints without changing queue identity", PaLegacyAlbumHints);
        await Run("PlaybackArtwork: requested album alone supplies fallback artwork and direct track cover keeps precedence", PaPreferredAlbumCovers);
        await Run("PlaybackArtwork: repeated track IDs in distinct response album contexts retain exact composite covers", PaCompositeResponses);
        await Run("PlaybackArtwork: logout account change cancellation and replaced queue reject held metadata", PaStale);
        await Run("PlaybackArtwork: closing during media open and held artwork retains the restored position", PaClosed);
        await Run("PlaybackArtwork: an unreadable resume snapshot survives a later writable close", PaUnreadableOnRestore);
        await Run("PlaybackArtwork: temporary offline restore failures can retry without replacing the saved queue", PaOfflineRestoreRetry);
        await Run("PlaybackArtwork: close and apply checkpoint an edited queue after temporary read failure", PaEditedQueueCheckpointRetry);
        await Run("PlaybackArtwork: close and apply retain a temporarily unreadable queue after a mode change", PaModeCheckpointRetry);
        await Run("PlaybackArtwork: a queue first restored for exit hydrates only after its owner resumes", PaDeferredArtworkAfterExit);
        await Run("PlaybackArtwork: a paused metadata page renews access after canceled close", PaArtworkRefreshAfterCanceledClose);
        await Run("PlaybackArtwork: a failed uninstall handoff resumes only its own artwork", PaArtworkAfterUninstallHandoff);
        await Run("PlaybackArtwork: the last cover accepted during Close is saved after cancellation", PaArtworkFinalPageAfterCanceledClose);
        await Run("PlaybackArtwork: a completed journal retries the untouched queue after canceled close", PaJournalRestoreAfterCanceledClose);
        await Run("PlaybackArtwork: an idle verified account preserves the previous owner's queue on close and apply", PaForeignCheckpoint);
        await Run("PlaybackArtwork: a blocked logout clears only its own snapshot on retry", PaLogoutClearRetry);
        await Run("PlaybackArtwork: a blocked profile folder retains a verified snapshot identity for retry", PaLogoutAncestorRetry);
        await Run("PlaybackArtwork: only a fresh verified logout resolves a clear without an observed snapshot", PaUnobservedLogoutRetry);
        await Run("PlaybackArtwork: an unprepared close flushes the previously pending session write", PaUnpreparedCloseFlushesSession);
        await Run("PlaybackArtwork: playback resumes from a saved seek even when metadata duration is unknown", PaUnknownDuration);
        await Run("PlaybackArtwork: optional metadata failure preserves restored queue position and paused state", PaFailure);
    }

    private static Task PaRoundtrip()
    {
        var directory = PaDirectory("roundtrip");
        var duplicate = PaRow("101", cover: "//avatars.yandex.net/get-music-content/123/restore-%%");
        var queue = PaQueue([duplicate, PaRow("102", cover: PaOtherCover), duplicate], 2);
        var store = new PlaybackStateStore(directory);
        var snapshot = PlaybackStateStore.Capture("12345", queue, 77.25);
        duplicate.DownloadedPath = Path.Combine(directory, "audio-location-must-not-be-saved.mp3");
        store.Save(snapshot);
        var text = File.ReadAllText(Path.Combine(directory, PlaybackStateStore.FileName));
        Assert(!text.Contains("audio-location-must-not-be-saved") && !text.Contains("fake-test-token")
            && !text.Contains("downloadedPath", StringComparison.OrdinalIgnoreCase) && !text.Contains("localPath", StringComparison.OrdinalIgnoreCase),
            "artwork snapshot included an audio location or credential");
        var saved = store.Load("12345")!;
        using var offline = new OfflineLibrary(Path.Combine(directory, "offline"));
        var restored = PlaybackStateStore.RestoreQueue(saved, offline);
        Assert(saved.Version == 1 && saved.Tracks.Select(row => row.CoverUri).SequenceEqual(new[] { PaCover, PaOtherCover, PaCover }),
            "safe artwork was not canonicalized or the numeric rendition version was lost");
        Assert(restored.Tracks.Select(row => row.CoverUri).SequenceEqual(new[] { PaCover, PaOtherCover, PaCover })
            && !ReferenceEquals(restored.Tracks[0], restored.Tracks[2]) && restored.CurrentIndex == 2 && restored.PositionSeconds == 77.25,
            "artwork restore lost duplicate occurrence identity or position");
        Assert(saved.Shuffle && saved.Repeat == RepeatMode.One && restored.Tracks.All(row => !row.IsPlaying && row.LocalPath is null && row.DownloadedPath is null),
            "metadata roundtrip changed playback modes or opened a media location");
        return Task.CompletedTask;
    }

    private static Task PaDownloadContract()
    {
        var directory = PaDirectory("download-contract");
        const string unsafeCover = "https://cdn.invalid/fixture-artwork?token=fixture-private";
        var rows = new[] { PaRow("9601", cover: PaCover), PaRow("9602", cover: unsafeCover) };
        var playbackStore = new PlaybackStateStore(directory);
        playbackStore.Save(PlaybackStateStore.Capture("12345", PaQueue(rows, 0), 77.25));
        var playbackJson = File.ReadAllText(Path.Combine(directory, PlaybackStateStore.FileName));
        Assert(playbackStore.Load("12345")!.Tracks[0].CoverUri == PaCover
            && playbackJson.Contains("\"coverUri\"", StringComparison.Ordinal) && playbackJson.Contains(PaCover, StringComparison.Ordinal),
            "the shared metadata contract dropped valid artwork from playback persistence");

        var downloads = new DownloadQueueState(); downloads.Replace(rows, "Shared metadata fixture");
        var snapshot = DownloadQueueStore.Capture("12345", downloads, AudioQuality.Optimal, false, null);
        var store = new DownloadQueueStore(directory); store.Save(snapshot);
        var path = Path.Combine(directory, DownloadQueueStore.FileName); var original = File.ReadAllBytes(path);
        using (var json = JsonDocument.Parse(original))
            Assert(json.RootElement.GetProperty("entries").EnumerateArray().All(entry => !entry.GetProperty("track").TryGetProperty("coverUri", out _)),
                "a null shared artwork property entered the download metadata schema");
        var text = File.ReadAllText(path);
        Assert(!text.Contains("http", StringComparison.OrdinalIgnoreCase) && !text.Contains("fixture-private", StringComparison.Ordinal)
            && snapshot.Entries.All(entry => entry.Track.CoverUri is null), "download capture retained an artwork URL or signed location");
        var loaded = store.Load("12345")!;
        Assert(loaded.Entries.Count == 2 && loaded.Entries.All(entry => entry.Track.CoverUri is null && entry.Track.AlbumId == "91"
            && entry.Track.Artists.Single().Id == "301"), "download artwork exclusion damaged ordinary shared track metadata");

        foreach (var cover in new[] { PaCover, unsafeCover })
        {
            var injected = JsonNode.Parse(original)!; injected["entries"]![0]!["track"]!["coverUri"] = cover;
            File.WriteAllText(path, injected.ToJsonString()); var injectedBytes = File.ReadAllBytes(path);
            Assert(store.Load("12345") is null && store.LastReadStatus == DownloadQueueReadStatus.Invalid
                && injectedBytes.SequenceEqual(File.ReadAllBytes(path)), "download load accepted artwork or overwrote invalid snapshot evidence");
            File.WriteAllBytes(path, original);
            var entries = snapshot.Entries.Select((entry, index) => index == 0
                ? entry with { Track = entry.Track with { CoverUri = cover } } : entry).ToArray();
            try { store.Save(snapshot with { Entries = Array.AsReadOnly(entries) }); throw new InvalidOperationException("download save accepted nonnull shared artwork"); }
            catch (JsonException) { }
            Assert(original.SequenceEqual(File.ReadAllBytes(path)), "rejected shared artwork changed the last good download snapshot");
        }
        return Task.CompletedTask;
    }

    private static Task PaLegacyOffline()
    {
        var directory = PaDirectory("legacy-offline");
        var store = new PlaybackStateStore(directory);
        store.Save(PlaybackStateStore.Capture("12345", PaQueue([PaRow("101"), PaRow("101"), PaRow("102"), PaRow("103", cover: PaCover)], 1), 77.25));
        var path = Path.Combine(directory, PlaybackStateStore.FileName);
        var legacy = JsonNode.Parse(File.ReadAllText(path))!;
        foreach (var row in legacy["tracks"]!.AsArray().Take(3)) row!.AsObject().Remove("coverUri");
        File.WriteAllText(path, legacy.ToJsonString());
        var saved = store.Load("12345")!;
        Assert(store.LastReadStatus == PlaybackStateReadStatus.Loaded && saved.Version == 1 && saved.Tracks[0].CoverUri is null,
            "optional artwork made a version-one legacy snapshot unreadable");
        var music = Path.Combine(directory, "offline"); Directory.CreateDirectory(music);
        var items = new[] { PaOffline("101", "first.mp3", "//avatars.mds.yandex.net/get-music-content/456/other-%%?1730453322475"),
            PaOffline("102", "unsafe.mp3", "https://avatars.yandex.net/get-music-content/unsafe?token=fixture-private"),
            PaOffline("103", "third.mp3", PaOtherCover) };
        foreach (var item in items) File.WriteAllBytes(Path.Combine(music, item.FileName), [1, 2, 3, 4, 5]);
        File.WriteAllText(Path.Combine(music, "library.index.json"), JsonSerializer.Serialize(items));
        using var offline = new OfflineLibrary(music);
        var restored = PlaybackStateStore.RestoreQueue(saved, offline);
        Assert(restored.Tracks[0].CoverUri == PaOtherCover && restored.Tracks[1].CoverUri == PaOtherCover
            && restored.Tracks[2].CoverUri is null && restored.Tracks[3].CoverUri == PaCover,
            "validated offline fallback lost duplicate artwork, trusted an unsafe URL, or overwrote saved artwork");
        Assert(restored.CurrentIndex == 1 && restored.PositionSeconds == 77.25 && restored.Tracks.All(row => row.DownloadedPath is not null),
            "artwork fallback changed indexed media or current occurrence");
        File.WriteAllBytes(Path.Combine(music, "first.mp3"), [1]);
        var invalidMedia = PlaybackStateStore.RestoreQueue(saved, offline);
        Assert(invalidMedia.Tracks[0].CoverUri is null && invalidMedia.Tracks[1].CoverUri is null
            && invalidMedia.Tracks[0].DownloadedPath is null, "invalid indexed media supplied fallback artwork");
        return Task.CompletedTask;
    }

    private static async Task PaDifferentOfflineAlbum()
    {
        var directory = PaDirectory("cross-album-offline");
        new PlaybackStateStore(directory).Save(PlaybackStateStore.Capture("12345", PaQueue([PaRow("101")], 0), 77.25));
        var music = Path.Combine(directory, "offline"); Directory.CreateDirectory(music);
        var alternate = PaOffline("101", "alternate.mp3", PaOtherCover) with { RequestId = "101:92", AlbumId = "92" };
        var media = Path.Combine(music, alternate.FileName);
        File.WriteAllBytes(media, [1, 2, 3, 4, 5]);
        File.WriteAllText(Path.Combine(music, "library.index.json"), JsonSerializer.Serialize(new[] { alternate }));

        using var fixture = new PaFixture(directory, "window", async (request, token) =>
        {
            var ids = await PaRequestedIds(request, token);
            Assert(ids.SequenceEqual(["101:91"]), "hydration requested the cached audio edition");
            return PaMetadata(ids);
        });
        await fixture.OpenAndRestore(); await fixture.WaitArtwork();
        Assert(fixture.Queue.Current is { RequestId: "101:91", AlbumId: "91", CoverUri: PaCover } row && row.DownloadedPath == media,
            "a shared audio file selected the wrong edition artwork or lost the download");
        PaAssertPaused(fixture, 1, 0);
    }

    private static async Task PaArtworkBudget()
    {
        var directory = PaDirectory("fallback-budget"); var store = new PlaybackStateStore(directory);
        var snapshot = PlaybackStateStore.Capture("12345", PaQueue([PaRow("102")], 0), 2);
        var row = snapshot.Tracks[0] with { Title = new string('x', 4096) };
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        var rowBytes = JsonSerializer.SerializeToUtf8Bytes(row, options).Length + 1;
        var count = (PlaybackStateStore.MaximumBytes - 512) / rowBytes;
        var legacy = snapshot with { Tracks = Enumerable.Repeat(row, count).ToArray(), CurrentIndex = count - 1 };
        store.Save(legacy);
        var updated = legacy with
        {
            Tracks = Enumerable.Repeat(row with { CoverUri = PaCover }, count).ToArray(),
            PositionSeconds = 77.25
        };
        Assert(JsonSerializer.SerializeToUtf8Bytes(updated, options).Length > PlaybackStateStore.MaximumBytes,
            "fixture artwork did not exceed the serialization limit");
        await store.SaveInBackgroundAsync(updated);
        var saved = store.Load("12345");
        Assert(saved is { PositionSeconds: 77.25 } && saved.CurrentIndex == legacy.CurrentIndex
            && saved.Tracks.Count == count && saved.Tracks.All(track => track.RequestId == row.RequestId && track.CoverUri is null),
            "dropping excess optional artwork lost the saved queue or position");
    }

    private static Task PaUnsafe()
    {
        var directory = PaDirectory("unsafe"); var store = new PlaybackStateStore(directory);
        var good = PlaybackStateStore.Capture("12345", PaQueue([PaRow("101", cover: PaCover)], 0), 77.25);
        store.Save(good);
        var path = Path.Combine(directory, PlaybackStateStore.FileName); var original = File.ReadAllBytes(path);
        foreach (var uri in new[] { "https://cdn.invalid/fixture-private", "https://avatars.yandex.net/cover?token=fixture-private",
            "https://avatars.yandex.net/cover#fixture-private", "http://avatars.yandex.net/cover", "file:///C:/fixture-private.png",
            "C:\\fixture-private.png", "https://user:fixture-private@avatars.yandex.net/cover", "https://avatars.yandex.net:444/cover" })
        {
            var snapshot = PlaybackStateStore.Capture("12345", PaQueue([PaRow("101", cover: uri)], 0), 77.25);
            Assert(snapshot.Tracks.Single().CoverUri is null && !JsonSerializer.Serialize(snapshot).Contains("fixture-private"),
                "capture persisted an untrusted artwork or signed audio location");
            try { store.Save(good with { Tracks = [good.Tracks[0] with { CoverUri = uri }] }); throw new InvalidOperationException("unsafe snapshot save succeeded"); }
            catch (JsonException) { }
            Assert(original.SequenceEqual(File.ReadAllBytes(path)), "unsafe save changed the last good snapshot");
            var bad = JsonNode.Parse(original)!; bad["tracks"]![0]!["coverUri"] = uri; var text = bad.ToJsonString(); File.WriteAllText(path, text);
            Assert(store.Load("12345") is null && store.LastReadStatus == PlaybackStateReadStatus.Invalid
                && File.ReadAllText(path) == text, "untrusted persisted artwork was accepted or overwritten during read");
            File.WriteAllBytes(path, original);
        }
        return Task.CompletedTask;
    }

    private static Task PaNotifications()
    {
        var row = PaRow("101"); var changes = new List<string?>(); row.PropertyChanged += (_, args) => changes.Add(args.PropertyName);
        Assert(!row.SetMissingArtwork("https://avatars.yandex.net/cover?token=fixture-private") && row.CoverUri is null && changes.Count == 0,
            "unsafe metadata mutated a row or raised an artwork notification");
        Assert(row.SetMissingArtwork("//avatars.yandex.net/get-music-content/123/restore-%%") && row.CoverUri == PaCover
            && changes.SequenceEqual(new[] { nameof(TrackRow.CoverUri) }), "missing artwork did not notify its existing binding once");
        Assert(!row.SetMissingArtwork(PaOtherCover) && !row.SetMissingArtwork(PaCover) && row.CoverUri == PaCover && changes.Count == 1,
            "late artwork replaced an existing cover or raised duplicate notifications");
        return Task.CompletedTask;
    }

    private static async Task PaReopen()
    {
        var directory = PaDirectory("reopen"); var store = new PlaybackStateStore(directory);
        store.Save(PlaybackStateStore.Capture("12345", PaQueue([PaRow("101", cover: PaCover), PaRow("102", cover: PaOtherCover), PaRow("101", cover: PaCover)], 2), 77.25));
        var readback = new List<object>();
        for (var pass = 0; pass < 2; pass++)
        {
            using var fixture = new PaFixture(directory, "cold-" + pass);
            await fixture.OpenAndRestore(session: false);
            PaInvoke(fixture.Window, "ShowQueue");
            await PaImagesReady(fixture, expectedCount: 3);
            PaAssertPaused(fixture, 3, 2);
            Assert(fixture.ApiHandler.Seen.Count == 0 && PaField<Task?>(fixture.Window, "_restoredArtworkTask") is null,
                "saved artwork without a session requested metadata or audio");
            Assert(fixture.ArtworkRequests >= 2, "fresh artwork cache did not render the saved cover from the anonymous fixture");
            App.Capture(fixture.Window, Path.Combine(directory, "restored-cold-" + pass + ".png"));
            readback.Add(new { pass, queueImages = 3, playerImageReady = true, position = Control<TextBlock>(fixture.Window, "PositionText").Text,
                fixture.ArtworkRequests, metadataRequests = fixture.ApiHandler.Seen.Count, fixture.MediaRequests, coldCache = true });
        }
        File.WriteAllText(Path.Combine(directory, "readback.json"), JsonSerializer.Serialize(readback, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static async Task PaHydrate()
    {
        var directory = PaDirectory("hydrate"); var store = new PlaybackStateStore(directory);
        var rows = Enumerable.Range(0, 103).Select(i => PaRow((7000 + i).ToString())).ToList(); rows.Add(rows[101]);
        store.Save(PlaybackStateStore.Capture("12345", PaQueue(rows, 103), 77.25));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var batches = new List<string[]>();
        using var fixture = new PaFixture(directory, "window", async (request, token) =>
        {
            var ids = await PaRequestedIds(request, token); batches.Add(ids);
            if (batches.Count == 1) { entered.TrySetResult(); await release.Task; }
            return PaMetadata(ids);
        });
        try
        {
            await fixture.OpenAndRestore(); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var queue = fixture.Queue; var originalItems = queue.Items; var original = queue.Items.ToArray();
            var occurrence = queue.CurrentOccurrenceToken;
            PaInvoke(fixture.Window, "ShowQueueCore", false); var list = Control<ListView>(fixture.Window, "TracksList"); list.SelectedIndex = 5;
            list.ScrollIntoView(list.Items[0]); fixture.Window.UpdateLayout();
            var selected = list.SelectedItem;
            var changes = new int[original.Length];
            for (var i = 0; i < original.Length; i++) { var index = i; original[i].PropertyChanged += (_, args) => { if (args.PropertyName == nameof(TrackRow.CoverUri)) changes[index]++; }; }
            Assert(Control<Image>(fixture.Window, "PlayerCover").Source is null && original.All(row => row.CoverUri is null),
                "held legacy fixture unexpectedly started with artwork");
            release.TrySetResult(); await fixture.WaitArtwork();
            await Until(() => Artwork.GetLoadStatus(Control<Image>(fixture.Window, "PlayerCover")) == ArtworkLoadStatus.Ready
                && PaQueueImages(fixture.Window).Any(image => Artwork.GetLoadStatus(image) == ArtworkLoadStatus.Ready));
            Assert(batches.Count == 2 && batches.All(batch => batch.Length <= 100) && batches[0][0] == "7101:91"
                && batches.SelectMany(batch => batch).Distinct(StringComparer.Ordinal).Count() == 103
                && batches.SelectMany(batch => batch).Order().SequenceEqual(rows.Select(row => row.RequestId).Distinct(StringComparer.Ordinal).Order()),
                "legacy artwork did not use bounded exact composite IDs with current track first");
            Assert(ReferenceEquals(originalItems, queue.Items) && queue.Items.Zip(original).All(pair => ReferenceEquals(pair.First, pair.Second))
                && queue.CurrentOccurrenceToken == occurrence && ReferenceEquals(list.SelectedItem, selected) && list.SelectedIndex == 5,
                "hydration replaced rows, selection, or current occurrence");
            Assert(changes.All(count => count == 1) && original.All(row => row.CoverUri == PaCover)
                && !ReferenceEquals(original[101], original[103]), "duplicate artwork did not update each existing row binding exactly once");
            PaAssertPaused(fixture, 104, 103);
            await fixture.WaitSave();
            Assert(store.Load("12345")!.Tracks.All(row => row.CoverUri == PaCover), "hydrated artwork was not saved for the next restart");
            File.WriteAllText(Path.Combine(directory, "readback.json"), JsonSerializer.Serialize(new
            { batchSizes = batches.Select(batch => batch.Length), firstRequest = batches[0][0], exactUniqueIds = 103, occurrences = 104,
                existingInstances = true, selectedIndex = list.SelectedIndex, currentIndex = queue.CurrentIndex, position = "1:17", fixture.MediaRequests }, new JsonSerializerOptions { WriteIndented = true }));
        }
        finally { release.TrySetResult(); }
    }

    private static async Task PaHydrateQueuedOccurrence()
    {
        foreach (var removeOriginal in new[] { false, true })
        {
            var directory = PaDirectory("queued-occurrence-" + removeOriginal);
            var store = new PlaybackStateStore(directory);
            var rows = new[] { PaRow("9860", PaOtherCover), PaRow("9861"), PaRow("9861", PaOtherCover, albumId: "92") };
            store.Save(PlaybackStateStore.Capture("12345", PaQueue(rows, 0), 77.25));
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var parents = 0;
            using var fixture = new PaFixture(directory, "window", async (request, token) =>
            {
                if (request.Method == HttpMethod.Get)
                {
                    Assert(removeOriginal && request.RequestUri?.AbsolutePath == "/albums/91/with-tracks", "new occurrence requested unadmitted parent artwork");
                    parents++;
                    return Json("""
                        {"result":{"id":"91","title":"Fixture parent","volumes":[[{"id":"9861","title":"Member","coverUri":"avatars.yandex.net/get-music-content/123/restore-%%"}]]}}
                        """);
                }
                var ids = await PaRequestedIds(request, token);
                Assert(ids.SequenceEqual(new[] { "9861:91" }), "hydration did not request just the exact uncovered edition");
                entered.SetResult();
                await release.Task.WaitAsync(token);
                return PaMetadata(removeOriginal ? [] : ids);
            });
            try
            {
                await fixture.OpenAndRestore(); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                var queue = fixture.Queue;
                var current = queue.CurrentOccurrenceToken;
                var replacementVersion = queue.ReplacementVersion;
                var original = queue.Items[1];
                var otherEdition = queue.Items[2];
                PaInvoke(fixture.Window, "ShowQueueCore", false);
                var list = Control<ListView>(fixture.Window, "TracksList");
                list.SelectedItem = original;
                PaInvoke(fixture.Window, "AddSelectedToQueue", true);
                var inserted = queue.Items[1];
                Assert(!ReferenceEquals(original, inserted) && inserted.RequestId == original.RequestId && inserted.CoverUri is null,
                    "normal play-next admission did not copy the empty artwork reference");
                if (removeOriginal)
                {
                    list.SelectedItem = original;
                    await (Task)PaInvoke(fixture.Window, "RemoveSelectedQueueAsync")!;
                    Assert(!queue.Items.Contains(original), "fixture could not remove the original non-current occurrence");
                }
                var admitted = queue.Items.ToArray();
                var tokens = admitted.Select((_, index) => queue.OccurrenceTokenAt(index)).ToArray();
                release.SetResult();
                await fixture.WaitArtwork();
                Assert(inserted.CoverUri == PaCover && original.CoverUri == (removeOriginal ? null : PaCover) && otherEdition.CoverUri == PaOtherCover
                    && parents == (removeOriginal ? 1 : 0), "the live exact occurrence lost its cover or repainted a detached row or wrong edition");
                Assert(queue.CurrentOccurrenceToken == current && queue.ReplacementVersion == replacementVersion && queue.Items.SequenceEqual(admitted)
                    && tokens.SequenceEqual(queue.Items.Select((_, index) => queue.OccurrenceTokenAt(index))), "artwork changed queue identity or order");
                PaAssertPaused(fixture, admitted.Length, 0);
                await fixture.WaitSave();
                Assert(store.Load("12345")!.Tracks.Select(row => (row.RequestId, row.CoverUri))
                    .SequenceEqual(admitted.Select(row => (row.RequestId, row.CoverUri))), "admitted artwork was not saved with the current edited queue");
            }
            finally { release.TrySetResult(); }
        }
    }

    private static async Task PaHydrateUndoSkippedOccurrence()
    {
        foreach (var exhaustFirstPage in new[] { false, true })
        {
            var directory = PaDirectory("undo-skipped-occurrence-" + exhaustFirstPage);
            var store = new PlaybackStateStore(directory);
            var rows = Enumerable.Range(10000, 102).Select(id => PaRow(id.ToString()))
                .Append(PaRow("10100", PaOtherCover, albumId: "92")).ToArray();
            store.Save(PlaybackStateStore.Capture("12345", PaQueue(rows, 0), 77.25));
            var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var secondEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseSecond = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var batches = new List<string[]>();
            var parentRequests = 0;
            using var fixture = new PaFixture(directory, "window", async (request, token) =>
            {
                if (request.Method == HttpMethod.Get)
                {
                    Assert(exhaustFirstPage && request.RequestUri?.AbsolutePath == "/albums/91/with-tracks",
                        "the follow-up requested an unrelated album");
                    parentRequests++;
                    return Json("""{"result":{"id":"91","title":"Fixture album","volumes":[]}}""");
                }
                var ids = await PaRequestedIds(request, token);
                batches.Add(ids);
                if (batches.Count == 1)
                {
                    firstEntered.SetResult();
                    await releaseFirst.Task.WaitAsync(token);
                }
                if (ids.Length == 100 && exhaustFirstPage) return Json("", HttpStatusCode.ServiceUnavailable);
                if (ids.SequenceEqual(["10101:91"]))
                {
                    secondEntered.SetResult();
                    await releaseSecond.Task.WaitAsync(token);
                }
                return PaMetadata(ids);
            });
            try
            {
                await fixture.OpenAndRestore(); await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                var queue = fixture.Queue;
                var original = queue.Items.ToArray();
                var tokens = original.Select((_, index) => queue.OccurrenceTokenAt(index)).ToArray();
                var replacementVersion = queue.ReplacementVersion;
                var removed = original[100];
                PaInvoke(fixture.Window, "ShowQueueCore", false);
                Control<ListView>(fixture.Window, "TracksList").SelectedItem = removed;
                await (Task)PaInvoke(fixture.Window, "RemoveSelectedQueueAsync")!;
                Assert(queue.CanUndoRemoval && !queue.Items.Contains(removed), "normal queue action did not detach the exact occurrence");
                releaseFirst.SetResult();

                await secondEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                PaInvoke(fixture.Window, "UndoQueueRemoval_Click", new Button(), new RoutedEventArgs(Button.ClickEvent));
                Assert(ReferenceEquals(queue.Items[100], removed) && removed.CoverUri is null && !queue.CanUndoRemoval,
                    "Undo did not return the same still-uncovered occurrence while the second request was held");
                releaseSecond.SetResult();
                await fixture.WaitArtwork();
                int[] expectedBatchSizes = exhaustFirstPage ? [100, 100, 1, 1] : [100, 1, 1];
                Assert(batches.Select(batch => batch.Length).SequenceEqual(expectedBatchSizes)
                    && parentRequests == (exhaustFirstPage ? 1 : 0)
                    && batches[^1].SequenceEqual(["10100:91"]), "the follow-up skipped the restored edition or reopened an exhausted request");
                Assert(removed.CoverUri == PaCover && original[102].CoverUri == PaOtherCover,
                    "the exact returned occurrence lost its artwork or changed an existing cover from another edition");
                Assert(queue.Items.SequenceEqual(original) && queue.CurrentOccurrenceToken == tokens[0]
                    && queue.ReplacementVersion == replacementVersion && tokens.SequenceEqual(queue.Items.Select((_, index) => queue.OccurrenceTokenAt(index))),
                    "artwork changed queue order or identity after Undo");
                PaAssertPaused(fixture, rows.Length, 0);
                await fixture.WaitSave();
                var saved = store.Load("12345")!;
                Assert(saved.Tracks.Select(row => (row.RequestId, row.CoverUri)).SequenceEqual(original.Select(row => (row.RequestId, row.CoverUri)))
                    && saved.PositionSeconds == 77.25, "post-Undo snapshot failed to retain exact artwork, order or position");
            }
            finally { releaseFirst.TrySetResult(); releaseSecond.TrySetResult(); }
        }
    }

    private static async Task PaHydrateUndoBetweenAttempts()
    {
        var directory = PaDirectory("undo-between-artwork-attempts");
        var store = new PlaybackStateStore(directory);
        var initial = new[] { PaRow("10600"), PaRow("10601", albumId: "92") };
        store.Save(PlaybackStateStore.Capture("12345", PaQueue(initial, 0), 77.25));
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var retryEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRetry = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = new List<string[]>();
        var albums = new List<string>();
        using var fixture = new PaFixture(directory, "window", async (request, token) =>
        {
            if (request.Method == HttpMethod.Get)
            {
                albums.Add(request.RequestUri!.AbsolutePath);
                return Json(JsonSerializer.Serialize(new { result = new { id = "92", title = "Fixture album", coverUri = PaOtherCover,
                    volumes = new[] { new[] { new { id = "10601", title = "Exact member" } } } } }));
            }
            var ids = await PaRequestedIds(request, token);
            attempts.Add(ids);
            if (attempts.Count == 1)
            {
                firstEntered.SetResult();
                await releaseFirst.Task.WaitAsync(token);
                return Json("", HttpStatusCode.ServiceUnavailable);
            }
            retryEntered.TrySetResult();
            await releaseRetry.Task.WaitAsync(token);
            return PaMetadata(ids);
        });
        try
        {
            await fixture.OpenAndRestore();
            await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var restored = fixture.Queue.Items.ToArray();
            var tokens = restored.Select((_, index) => fixture.Queue.OccurrenceTokenAt(index)).ToArray();
            PaInvoke(fixture.Window, "ShowQueueCore", false);
            Control<ListView>(fixture.Window, "TracksList").SelectedItem = restored[1];
            await (Task)PaInvoke(fixture.Window, "RemoveSelectedQueueAsync")!;
            Assert(fixture.Queue.CanUndoRemoval && fixture.Queue.Items.Count == 1, "fixture retained the row in the retry");
            releaseFirst.SetResult();
            await retryEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            PaInvoke(fixture.Window, "UndoQueueRemoval_Click", new Button(), new RoutedEventArgs(Button.ClickEvent));
            Assert(ReferenceEquals(fixture.Queue.Items[1], restored[1]) && restored[1].CoverUri is null,
                "fixture did not return the still uncovered row before Retry completed");
            releaseRetry.SetResult();
            await fixture.WaitArtwork();
            await fixture.WaitSave();
            var saved = store.Load("12345")!;
            Assert(attempts.Count == 2 && attempts[0].SequenceEqual(["10600:91", "10601:92"])
                && attempts[1].SequenceEqual(["10600:91"]) && albums.SequenceEqual(["/albums/92/with-tracks"]),
                "restore did not keep the original track and parent request budgets");
            Assert(fixture.Queue.Items.SequenceEqual(restored) && tokens.SequenceEqual(restored.Select((_, index) => fixture.Queue.OccurrenceTokenAt(index)))
                && restored[0].CoverUri == PaCover && restored[1].CoverUri == PaOtherCover
                && saved.Tracks[1].CoverUri == PaOtherCover && saved.PositionSeconds == 77.25,
                "confirmed member artwork did not reach the exact surviving occurrence and snapshot");
        }
        finally { releaseFirst.TrySetResult(); releaseRetry.TrySetResult(); }
    }

    private static async Task PaHydrateUndoDuringAlbum()
    {
        var directory = PaDirectory("undo-during-artwork-album");
        var store = new PlaybackStateStore(directory);
        var rows = Enumerable.Range(10200, 102).Select(id => PaRow(id.ToString()))
            .Append(PaRow("10300", PaOtherCover, albumId: "92")).ToArray();
        store.Save(PlaybackStateStore.Capture("12345", PaQueue(rows, 0), 77.25));
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var albumEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseAlbum = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var batches = new List<string[]>();
        using var fixture = new PaFixture(directory, "window", async (request, token) =>
        {
            if (request.Method == HttpMethod.Get)
            {
                Assert(request.RequestUri?.AbsolutePath == "/albums/91/with-tracks", "artwork requested the wrong album edition");
                albumEntered.SetResult();
                await releaseAlbum.Task.WaitAsync(token);
                return Json(JsonSerializer.Serialize(new { result = new { id = "91", title = "Fixture parent", coverUri = PaOtherCover,
                    volumes = new[] { new[] { new { id = "10301", title = "First member", coverUri = PaOtherCover },
                        new { id = "10300", title = "Returned member", coverUri = PaCover } } } } }));
            }
            var ids = await PaRequestedIds(request, token);
            batches.Add(ids);
            if (batches.Count != 1) return PaMetadata([]);
            firstEntered.SetResult();
            await releaseFirst.Task.WaitAsync(token);
            return PaMetadata(ids);
        });
        try
        {
            await fixture.OpenAndRestore(); await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var queue = fixture.Queue;
            var original = queue.Items.ToArray();
            var tokens = original.Select((_, index) => queue.OccurrenceTokenAt(index)).ToArray();
            var removed = original[100];
            PaInvoke(fixture.Window, "ShowQueueCore", false);
            Control<ListView>(fixture.Window, "TracksList").SelectedItem = removed;
            await (Task)PaInvoke(fixture.Window, "RemoveSelectedQueueAsync")!;
            Assert(queue.CanUndoRemoval && !queue.Items.Contains(removed), "fixture did not remove the late exact occurrence");
            releaseFirst.SetResult();
            await albumEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert(batches.Count == 2 && batches[1].SequenceEqual(["10301:91"]), "the removed row was sent during the track passes");
            PaInvoke(fixture.Window, "UndoQueueRemoval_Click", new Button(), new RoutedEventArgs(Button.ClickEvent));
            Assert(ReferenceEquals(queue.Items[100], removed) && removed.CoverUri is null, "Undo did not return the exact still-uncovered row");
            releaseAlbum.SetResult();
            await fixture.WaitArtwork();
            await fixture.WaitSave();
            var saved = store.Load("12345")!;
            Assert(batches.Count == 2 && removed.CoverUri == PaCover && original[101].CoverUri == PaOtherCover && original[102].CoverUri == PaOtherCover
                && saved.Tracks[100] is { RequestId: "10300:91", CoverUri: PaCover } && saved.Tracks[102] is { RequestId: "10300:92", CoverUri: PaOtherCover },
                "the confirmed album response did not fill and persist the returned exact member without another request");
            Assert(queue.Items.SequenceEqual(original) && tokens.SequenceEqual(queue.Items.Select((_, index) => queue.OccurrenceTokenAt(index)))
                && saved is { CurrentIndex: 0, PositionSeconds: 77.25 }, "album artwork changed queue order, tokens or position");
            PaAssertPaused(fixture, original.Length, 0);
        }
        finally { releaseFirst.TrySetResult(); releaseAlbum.TrySetResult(); }
    }

    private static async Task PaHydrateUndoSkippedAlbum()
    {
        var directory = PaDirectory("undo-skipped-artwork-album");
        var store = new PlaybackStateStore(directory);
        var rows = new[] { PaRow("10400", PaOtherCover), PaRow("10401"), PaRow("10402", albumId: "92"), PaRow("10403", albumId: "93") };
        store.Save(PlaybackStateStore.Capture("12345", PaQueue(rows, 0), 77.25));
        var tracksEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseTracks = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var albumEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseAlbum = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var albums = new List<string>();
        var tracks = new List<string[]>();
        using var fixture = new PaFixture(directory, "window", async (request, token) =>
        {
            if (request.Method == HttpMethod.Post)
            {
                tracks.Add(await PaRequestedIds(request, token));
                tracksEntered.TrySetResult();
                await releaseTracks.Task.WaitAsync(token);
                return PaMetadata([]);
            }
            var path = request.RequestUri!.AbsolutePath;
            albums.Add(path);
            if (path == "/albums/93/with-tracks") return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            if (path == "/albums/92/with-tracks")
            {
                albumEntered.TrySetResult();
                await releaseAlbum.Task.WaitAsync(token);
                return Album("92", "10402", PaOtherCover);
            }
            Assert(path == "/albums/91/with-tracks", "the retry requested a different edition");
            return Album("91", "10401", PaCover);
        });
        try
        {
            await fixture.OpenAndRestore(); await tracksEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var original = fixture.Queue.Items.ToArray();
            var tokens = original.Select((_, index) => fixture.Queue.OccurrenceTokenAt(index)).ToArray();
            PaInvoke(fixture.Window, "ShowQueueCore", false);
            Control<ListView>(fixture.Window, "TracksList").SelectedItem = original[1];
            await (Task)PaInvoke(fixture.Window, "RemoveSelectedQueueAsync")!;
            Assert(fixture.Queue.CanUndoRemoval && !fixture.Queue.Items.Contains(original[1]), "fixture could not remove the first album's only occurrence");
            releaseTracks.SetResult();
            await albumEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            PaInvoke(fixture.Window, "UndoQueueRemoval_Click", new Button(), new RoutedEventArgs(Button.ClickEvent));
            Assert(ReferenceEquals(fixture.Queue.Items[1], original[1]) && original[1].CoverUri is null, "Undo did not restore the same uncovered occurrence");
            releaseAlbum.SetResult();
            await fixture.WaitArtwork(); await fixture.WaitSave();
            var saved = store.Load("12345");
            Assert(tracks.Count == 1 && tracks[0].SequenceEqual(["10401:91", "10402:92", "10403:93"])
                && albums.SequenceEqual(["/albums/92/with-tracks", "/albums/93/with-tracks", "/albums/93/with-tracks", "/albums/91/with-tracks"]),
                "the limited album retry skipped the returned group or reopened a group already sent");
            Assert(original[1].CoverUri == PaCover && original[2].CoverUri == PaOtherCover && original[3].CoverUri is null
                && saved is { PositionSeconds: 77.25, CurrentIndex: 0 } && saved.Tracks[1] is { RequestId: "10401:91", CoverUri: PaCover }
                && fixture.Queue.Items.SequenceEqual(original) && tokens.SequenceEqual(original.Select((_, index) => fixture.Queue.OccurrenceTokenAt(index))),
                "the retry lost exact membership, the saved cover, or the restored occurrence");
            PaAssertPaused(fixture, original.Length, 0);
        }
        finally { releaseTracks.TrySetResult(); releaseAlbum.TrySetResult(); }

        static HttpResponseMessage Album(string id, string member, string cover) => Json(JsonSerializer.Serialize(new
        { result = new { id, title = "Fixture parent", volumes = new[] { new[] { new { id = member, title = "Fixture member", coverUri = cover } } } } }));
    }

    private static async Task PaHydrateUndoAlbumAfterAttempt()
    {
        foreach (var failAfterUndo in new[] { false, true })
        {
            var directory = PaDirectory("undo-half-used-album-" + failAfterUndo);
            var store = new PlaybackStateStore(directory);
            var rows = new[] { PaRow("10410", PaCover), PaRow("10411"), PaRow("10412", albumId: "92") };
            store.Save(PlaybackStateStore.Capture("12345", PaQueue(rows, 0), 77.25));
            var clock = new PaArtworkClock();
            var albums = new List<string>();
            var albumEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseAlbum = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var fixture = new PaFixture(directory, "window", async (request, token) =>
            {
                if (request.Method == HttpMethod.Post)
                {
                    Assert((await PaRequestedIds(request, token)).SequenceEqual(["10411:91", "10412:92"]), "wrong track batch for the two original album groups");
                    return PaMetadata([]);
                }
                var path = request.RequestUri!.AbsolutePath;
                albums.Add(path);
                if (path == "/albums/92/with-tracks")
                {
                    albumEntered.TrySetResult();
                    await releaseAlbum.Task.WaitAsync(token);
                    return Album("92", "10412", PaOtherCover);
                }
                Assert(path == "/albums/91/with-tracks", "Undo requested a different album");
                if (albums.Count == 1 || failAfterUndo)
                {
                    var failure = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
                    failure.Headers.RetryAfter = new(TimeSpan.FromSeconds(1));
                    return failure;
                }
                return Album("91", "10411", PaCover);
            });
            PaSet(fixture.Window, "_restoredArtworkClock", clock);
            try
            {
                await fixture.OpenAndRestore();
                await Until(() => clock.IsWaiting);
                var original = fixture.Queue.Items.ToArray();
                var tokens = original.Select((_, index) => fixture.Queue.OccurrenceTokenAt(index)).ToArray();
                PaInvoke(fixture.Window, "ShowQueueCore", false);
                Control<ListView>(fixture.Window, "TracksList").SelectedItem = original[1];
                await (Task)PaInvoke(fixture.Window, "RemoveSelectedQueueAsync")!;
                Assert(fixture.Queue.CanUndoRemoval && !fixture.Queue.Items.Contains(original[1]), "fixture did not remove A during its retry delay");
                clock.Advance(TimeSpan.FromMilliseconds(999));
                Assert(clock.IsWaiting && albums.Count == 1, "the album route ignored Retry-After before checking for remaining live rows");
                clock.Advance(TimeSpan.FromMilliseconds(1));
                await albumEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                PaInvoke(fixture.Window, "UndoQueueRemoval_Click", new Button(), new RoutedEventArgs(Button.ClickEvent));
                releaseAlbum.SetResult();
                await fixture.WaitArtwork(); await fixture.WaitSave();
                var saved = store.Load("12345");
                var expected = failAfterUndo ? null : PaCover;
                Assert(albums.SequenceEqual(["/albums/91/with-tracks", "/albums/92/with-tracks", "/albums/91/with-tracks"]) && !clock.IsWaiting,
                    "Undo lost the second attempt or reopened an already exhausted album");
                Assert(ReferenceEquals(fixture.Queue.Items[1], original[1]) && original[1].CoverUri == expected && original[2].CoverUri == PaOtherCover
                    && saved is { CurrentIndex: 0, PositionSeconds: 77.25 } && saved.Tracks[1].CoverUri == expected
                    && tokens.SequenceEqual(original.Select((_, index) => fixture.Queue.OccurrenceTokenAt(index))),
                    "album recovery changed exact membership, occurrence, position or the saved result");
                PaAssertPaused(fixture, 3, 0);
            }
            finally { releaseAlbum.TrySetResult(); }
        }

        static HttpResponseMessage Album(string id, string member, string cover) => Json(JsonSerializer.Serialize(new
        { result = new { id, title = "Fixture parent", volumes = new[] { new[] { new { id = member, title = "Fixture member", coverUri = cover } } } } }));
    }

    private static async Task PaHydrateUndoLateTracks()
    {
        foreach (var duringAlbum in new[] { false, true })
        {
            var directory = PaDirectory("undo-late-tracks-" + duringAlbum);
            var store = new PlaybackStateStore(directory);
            var rows = new[] { PaRow("10800", PaCover) }.Concat(Enumerable.Range(10801, 99).Select(id => PaRow(id.ToString())))
                .Append(PaRow("10900", albumId: "92")).Append(PaRow("10901", albumId: "93")).ToArray();
            store.Save(PlaybackStateStore.Capture("12345", PaQueue(rows, 0), 77.25));
            var clock = new PaArtworkClock();
            var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var laterEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseLater = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var batches = new List<string[]>();
            var albums = 0;
            using var fixture = new PaFixture(directory, "window", async (request, token) =>
            {
                if (request.Method == HttpMethod.Get)
                {
                    Assert(duringAlbum && request.RequestUri?.AbsolutePath == "/albums/92/with-tracks", "the held track retry bypassed the allowed parent route");
                    albums++;
                    laterEntered.SetResult();
                    await releaseLater.Task.WaitAsync(token);
                    return Json(JsonSerializer.Serialize(new { result = new { id = "92", title = "Parent fixture", coverUri = PaOtherCover,
                        volumes = new[] { new[] { new { id = "10900", title = "Exact member" } } } } }));
                }
                var ids = await PaRequestedIds(request, token);
                batches.Add(ids);
                if (batches.Count == 1)
                {
                    firstEntered.SetResult();
                    await releaseFirst.Task.WaitAsync(token);
                    if (duringAlbum) return PaMetadata(ids[..^1]);
                    var failure = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
                    failure.Headers.RetryAfter = new(TimeSpan.FromSeconds(1));
                    return failure;
                }
                if (!duringAlbum && batches.Count == 2)
                {
                    laterEntered.SetResult();
                    await releaseLater.Task.WaitAsync(token);
                }
                return PaMetadata(ids);
            });
            PaSet(fixture.Window, "_restoredArtworkClock", clock);
            try
            {
                await fixture.OpenAndRestore(); await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                var original = fixture.Queue.Items.ToArray();
                var tokens = original.Select((_, index) => fixture.Queue.OccurrenceTokenAt(index)).ToArray();
                PaInvoke(fixture.Window, "ShowQueueCore", false);
                var list = Control<ListView>(fixture.Window, "TracksList");
                list.SelectedItems.Clear();
                foreach (var row in duringAlbum ? original[^1..] : original[1..^1]) list.SelectedItems.Add(row);
                await (Task)PaInvoke(fixture.Window, "RemoveSelectedQueueAsync")!;
                Assert(fixture.Queue.CanUndoRemoval && fixture.Queue.Items.Count == (duringAlbum ? 101 : 2), "fixture could not detach the intended exact track page");
                releaseFirst.SetResult();
                if (!duringAlbum)
                {
                    await Until(() => clock.IsWaiting);
                    clock.Advance(TimeSpan.FromMilliseconds(999));
                    Assert(clock.IsWaiting && batches.Count == 1, "the following page escaped the track route Retry-After");
                    clock.Advance(TimeSpan.FromMilliseconds(1));
                }
                await laterEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                PaInvoke(fixture.Window, "UndoQueueRemoval_Click", new Button(), new RoutedEventArgs(Button.ClickEvent));
                releaseLater.SetResult();
                await fixture.WaitArtwork(); await fixture.WaitSave();
                Assert(albums == (duringAlbum ? 1 : 0) && batches.Select(batch => batch.Length).SequenceEqual(duringAlbum ? [100, 1] : [100, 1, 100])
                    && batches[1].SequenceEqual(["10901:93"]) && (duringAlbum || batches[0].SequenceEqual(batches[2])),
                    "the late track pass lost an admissible pair or reset a used request budget");
                var saved = store.Load("12345");
                Assert(original.All(row => row.CoverUri is not null) && original[^2].CoverUri == (duringAlbum ? PaOtherCover : PaCover)
                    && original[^1].CoverUri == PaCover && saved is { CurrentIndex: 0, PositionSeconds: 77.25 }
                    && saved.Tracks.Select(row => row.CoverUri).SequenceEqual(original.Select(row => row.CoverUri))
                    && fixture.Queue.Items.SequenceEqual(original) && tokens.SequenceEqual(original.Select((_, i) => fixture.Queue.OccurrenceTokenAt(i))),
                    "late hydration changed queue occurrences or lost the verified covers in the snapshot");
                PaAssertPaused(fixture, 102, 0);
            }
            finally { releaseFirst.TrySetResult(); releaseLater.TrySetResult(); }
        }
    }

    private static async Task PaHydrateLateUndo()
    {
        var directory = PaDirectory("late-undo-after-artwork");
        var store = new PlaybackStateStore(directory);
        var original = new[] { PaRow("10400", PaOtherCover), PaRow("10401") };
        store.Save(PlaybackStateStore.Capture("12345", PaQueue(original, 0), 23));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var requests = 0;
        using var fixture = new PaFixture(directory, "window", async (request, token) =>
        {
            var ids = await PaRequestedIds(request, token);
            Assert(ids.SequenceEqual(["10401:91"]), "fixture requested an unrelated edition");
            requests++;
            entered.TrySetResult();
            await release.Task.WaitAsync(token);
            return PaMetadata(ids);
        });
        try
        {
            await fixture.OpenAndRestore();
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var removed = fixture.Queue.Items[1];
            var token = fixture.Queue.OccurrenceTokenAt(1);
            PaInvoke(fixture.Window, "ShowQueueCore", false);
            Control<ListView>(fixture.Window, "TracksList").SelectedItem = removed;
            await (Task)PaInvoke(fixture.Window, "RemoveSelectedQueueAsync")!;
            Assert(fixture.Queue.CanUndoRemoval && fixture.Queue.Items.Count == 1, "fixture did not park the exact occurrence");
            release.TrySetResult();
            await fixture.WaitArtwork();
            Assert(fixture.Queue.Items.Count == 1, "hydration independently changed queue membership");
            PaInvoke(fixture.Window, "UndoQueueRemoval_Click", new Button(), new RoutedEventArgs(Button.ClickEvent));
            await fixture.WaitSave();
            var saved = store.Load("12345")!;
            Assert(ReferenceEquals(fixture.Queue.Items[1], removed) && fixture.Queue.OccurrenceTokenAt(1) == token
                && removed.CoverUri == PaCover && saved.Tracks[1].CoverUri == PaCover && saved.PositionSeconds == 23 && requests == 1,
                "late undo lost its already confirmed artwork, position, or occurrence identity");
        }
        finally { release.TrySetResult(); }
    }

    private static async Task PaHydrateUndoDuringSave()
    {
        var directory = PaDirectory("undo-during-artwork-snapshot");
        var store = new PlaybackStateStore(directory);
        store.Save(PlaybackStateStore.Capture("12345", PaQueue([PaRow("10200"), PaRow("10201")], 0), 77.25));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var olderWrite = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var fixture = new PaFixture(directory, "window", async (request, token) =>
        {
            var ids = await PaRequestedIds(request, token);
            Assert(ids.SequenceEqual(["10200:91", "10201:91"]), "unexpected artwork request during snapshot handoff");
            entered.SetResult();
            await release.Task.WaitAsync(token);
            return PaMetadata(ids);
        });
        try
        {
            await fixture.OpenAndRestore(); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var queue = fixture.Queue;
            var removed = queue.Items[1];
            var token = queue.OccurrenceTokenAt(1);
            PaInvoke(fixture.Window, "ShowQueueCore", false);
            Control<ListView>(fixture.Window, "TracksList").SelectedItem = removed;
            await (Task)PaInvoke(fixture.Window, "RemoveSelectedQueueAsync")!;
            PaSet(fixture.Window, "_playbackStateWriteTask", olderWrite.Task);
            release.SetResult();
            await Until(() => queue.Current?.CoverUri == PaCover);
            Assert(removed.CoverUri is null && !PaField<Task>(fixture.Window, "_restoredArtworkTask").IsCompleted,
                "fixture did not hold completion behind the older snapshot");

            PaInvoke(fixture.Window, "UndoQueueRemoval_Click", new Button(), new RoutedEventArgs(Button.ClickEvent));
            Assert(ReferenceEquals(queue.Items[1], removed) && removed.CoverUri is null, "Undo did not return the row before the older save completed");
            olderWrite.SetResult();
            await fixture.WaitArtwork();
            await fixture.WaitSave();
            Assert(removed.CoverUri == PaCover && queue.OccurrenceTokenAt(1) == token
                && store.Load("12345")!.Tracks[1].CoverUri == PaCover, "snapshot handoff missed an already resolved cover for a restored occurrence");
            PaAssertPaused(fixture, 2, 0);
        }
        finally { release.TrySetResult(); olderWrite.TrySetResult(); }
    }

    private static async Task PaHydrateAlbumAfterFailedPage()
    {
        var directory = PaDirectory("album-after-failed-page");
        var rows = Enumerable.Range(9800, 103).Select(id => PaRow(id.ToString())).ToArray();
        var store = new PlaybackStateStore(directory);
        store.Save(PlaybackStateStore.Capture("12345", PaQueue(rows, 0), 77.25));
        var batches = new List<string[]>();
        var albumRequests = 0;
        using var fixture = new PaFixture(directory, "window", async (request, token) =>
        {
            if (request.Method == HttpMethod.Get)
            {
                Assert(request.RequestUri?.AbsolutePath == "/albums/91/with-tracks", "fixture got an unrelated album request");
                albumRequests++;
                return Json(JsonSerializer.Serialize(new { result = new { id = "91", title = "Fixture album", coverUri = PaOtherCover,
                    volumes = new[] { new[] { new { id = "9800", title = "First page member" },
                        new { id = "9900", title = "Second page member" }, new { id = "9901", title = "Existing direct cover" } } } } }));
            }
            var ids = await PaRequestedIds(request, token);
            batches.Add(ids);
            return batches.Count <= 2 ? Json("", HttpStatusCode.ServiceUnavailable) : PaMetadata(ids.Skip(1).ToArray());
        });
        await fixture.OpenAndRestore();
        await fixture.WaitArtwork();
        await fixture.WaitSave();
        string? Expected(string id) => id switch { "9800" or "9900" => PaOtherCover, "9901" or "9902" => PaCover, _ => null };
        var saved = store.Load("12345")!;
        Assert(batches.Select(batch => batch.Length).SequenceEqual(new[] { 100, 100, 3 }) && batches[0].SequenceEqual(batches[1])
            && albumRequests == 1, "obtaining missing artwork required another metadata request");
        Assert(fixture.Queue.Items.All(row => row.CoverUri == Expected(row.Id)) && saved.Tracks.All(row => row.CoverUri == Expected(row.Id)),
            "album proof omitted a failed page member, overwrote direct artwork, or covered a nonmember");
        PaAssertPaused(fixture, rows.Length, 0);
    }

    private static async Task PaHydrateOnlyFailedPage()
    {
        var directory = PaDirectory("album-after-only-track-page");
        var store = new PlaybackStateStore(directory);
        store.Save(PlaybackStateStore.Capture("12345", PaQueue([PaRow("10501")], 0), 77.25));
        var requests = new List<string>();
        using var fixture = new PaFixture(directory, "window", async (request, token) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            requests.Add(path);
            if (request.Method == HttpMethod.Post)
            {
                Assert((await PaRequestedIds(request, token)).SequenceEqual(["10501:91"]), "fixture requested a different track edition");
                return Json("", HttpStatusCode.ServiceUnavailable);
            }
            Assert(path == "/albums/91/with-tracks", "fallback requested a different album edition");
            return Json(JsonSerializer.Serialize(new { result = new { id = "91", title = "Fixture album", coverUri = PaOtherCover,
                volumes = new[] { new[] { new { id = "10501", title = "Exact member" } } } } }));
        });
        await fixture.OpenAndRestore();
        await fixture.WaitArtwork();
        await fixture.WaitSave();
        Assert(requests.SequenceEqual(["/tracks", "/tracks", "/albums/91/with-tracks"])
            && fixture.Queue.Current?.CoverUri == PaOtherCover && store.Load("12345")!.Tracks[0].CoverUri == PaOtherCover,
            "the exhausted track page did not obtain and persist its exact album cover within the retry budget");
        PaAssertPaused(fixture, 1, 0);
    }

    private static async Task PaHydratePageRetry()
    {
        foreach (var mode in new[] { "recovers", "persistent", "logout" })
        {
            var directory = PaDirectory("page-retry-" + mode);
            var rows = Enumerable.Range(0, 103).Select(index => PaRow((9700 + index).ToString())).ToArray();
            var store = new PlaybackStateStore(directory);
            store.Save(PlaybackStateStore.Capture("12345", PaQueue(rows, 102), 77.25));
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var retryEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var retryCanceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var batches = new List<string[]>();
            var parentRequests = 0;
            using var fixture = new PaFixture(directory, "window", async (request, token) =>
            {
                if (request.Method == HttpMethod.Get)
                {
                    Assert(mode == "persistent" && request.RequestUri?.AbsolutePath == "/albums/91/with-tracks",
                        "a successful or cancelled page requested an album");
                    parentRequests++;
                    return Json("""{"result":{"id":"91","title":"Fixture album","volumes":[]}}""");
                }
                var ids = await PaRequestedIds(request, token);
                batches.Add(ids);
                if (batches.Count == 1)
                {
                    entered.SetResult();
                    await release.Task.WaitAsync(token);
                    return Json("", HttpStatusCode.ServiceUnavailable);
                }
                if (batches.Count == 2 && mode == "logout")
                {
                    retryEntered.SetResult();
                    try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
                    catch (OperationCanceledException) { retryCanceled.SetResult(); throw; }
                }
                return batches.Count == 2 && mode == "persistent"
                    ? Json("", HttpStatusCode.ServiceUnavailable) : PaMetadata(ids);
            });
            try
            {
                await fixture.OpenAndRestore(); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                var original = fixture.Queue.Items.ToArray();
                var occurrence = fixture.Queue.CurrentOccurrenceToken;
                release.SetResult();
                if (mode == "logout")
                {
                    await retryEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    fixture.Window.SignOut();
                    await retryCanceled.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    await fixture.WaitArtwork();
                    Assert(batches.Count == 2 && original.All(row => row.CoverUri is null) && fixture.ArtworkRequests == 0,
                        "logging out during a page retry continued hydration");
                    continue;
                }
                await fixture.WaitArtwork();
                Assert(batches.Select(batch => batch.Length).SequenceEqual(new[] { 100, 100, 3 })
                    && batches[0][0] == rows[102].RequestId && batches[0].SequenceEqual(batches[1])
                    && parentRequests == (mode == "persistent" ? 1 : 0)
                    && batches[0].Concat(batches[2]).Distinct(StringComparer.Ordinal).Count() == rows.Length,
                    "the current-first page retry repeated the wrong references or skipped the next page");
                PaAssertPaused(fixture, rows.Length, 102);
                Assert(fixture.Queue.CurrentOccurrenceToken == occurrence && fixture.Queue.Items.SequenceEqual(original),
                    "artwork retries replaced the restored queue or current occurrence");
                var requestedLater = batches[2].ToHashSet(StringComparer.Ordinal);
                string? Expected(string id) => mode == "recovers" || requestedLater.Contains(id) ? PaCover : null;
                Assert(original.All(row => row.CoverUri == Expected(row.RequestId)), "an independent artwork page did not publish its result");
                await fixture.WaitSave();
                Assert(store.Load("12345")!.Tracks.All(row => row.CoverUri == Expected(row.RequestId)),
                    "successful pages did not persist their restored artwork");
            }
            finally { release.TrySetResult(); }
        }
    }

    private static async Task PaHydrateRetryAfter()
    {
        foreach (var mode in new[] { "continue", "logout", "over-budget" })
        {
            var directory = PaDirectory("retry-after-" + mode);
            var overBudget = mode == "over-budget";
            var rows = Enumerable.Range(0, overBudget ? 201 : 101).Select(index => PaRow((10100 + index).ToString())).ToArray();
            var store = new PlaybackStateStore(directory);
            store.Save(PlaybackStateStore.Capture("12345", PaQueue(rows, 0), 77.25));
            var clock = new PaArtworkClock();
            var batches = new List<string[]>();
            var parentRequests = 0;
            using var fixture = new PaFixture(directory, "window", async (request, token) =>
            {
                if (request.Method == HttpMethod.Get)
                {
                    Assert(overBudget && request.RequestUri?.AbsolutePath == "/albums/91/with-tracks", "track backoff changed the parent route");
                    Interlocked.Increment(ref parentRequests);
                    return Json(JsonSerializer.Serialize(new { result = new { id = "91", title = "Parent fixture", coverUri = PaCover,
                        volumes = new[] { new[] { new { id = rows[0].Id, title = "Member fixture" } } } } }));
                }
                var ids = await PaRequestedIds(request, token);
                batches.Add(ids);
                if (overBudget && batches.Count == 1) return PaMetadata(ids[1..]);
                if (batches.Count == 3) return PaMetadata(ids);
                var response = Json("", HttpStatusCode.TooManyRequests);
                response.Headers.RetryAfter = new(TimeSpan.FromSeconds(overBudget ? 3 : batches.Count));
                return response;
            });
            PaSet(fixture.Window, "_restoredArtworkClock", clock);
            await fixture.OpenAndRestore();
            if (overBudget)
            {
                await fixture.WaitArtwork();
                PaAssertPaused(fixture, rows.Length, 0);
                Assert(batches.Select(batch => batch.Length).SequenceEqual(new[] { 100, 100 }) && !clock.IsWaiting && parentRequests == 1,
                    "a track delay above the budget did not stop that route or incorrectly delayed the parent");
                Assert(fixture.Queue.Items.Take(100).All(row => row.CoverUri == PaCover)
                    && fixture.Queue.Items.Skip(100).All(row => row.CoverUri is null), "partial artwork was lost or a blocked page was hydrated");
                await fixture.WaitSave();
                Assert(store.Load("12345")!.Tracks.Take(100).All(row => row.CoverUri == PaCover), "partial artwork did not persist");
                continue;
            }

            await Until(() => clock.IsWaiting);
            clock.Advance(TimeSpan.FromMilliseconds(999));
            Assert(clock.IsWaiting && batches.Count == 1, "the page retry ignored the first Retry-After");
            clock.Advance(TimeSpan.FromMilliseconds(1));
            await Until(() => batches.Count >= 2 && clock.IsWaiting);
            clock.Advance(TimeSpan.FromMilliseconds(1999));
            Assert(clock.IsWaiting && batches.Count == 2 && batches[0].SequenceEqual(batches[1]),
                "the next page ignored Retry-After from the exhausted page");
            if (mode == "logout") fixture.Window.SignOut();
            else clock.Advance(TimeSpan.FromMilliseconds(1));
            await fixture.WaitArtwork();
            Assert(!clock.IsWaiting && parentRequests == 0, "artwork hydration retained a wait or fetched another route");
            if (mode == "logout")
            {
                clock.Advance(TimeSpan.FromSeconds(10));
                Assert(batches.Count == 2 && fixture.ArtworkRequests == 0, "logging out did not cancel deferred hydration");
            }
            else
            {
                PaAssertPaused(fixture, rows.Length, 0);
                Assert(batches.Select(batch => batch.Length).SequenceEqual(new[] { 100, 100, 1 })
                    && fixture.Queue.Items.Skip(100).Single().CoverUri == PaCover,
                    "the independently successful page did not run after Retry-After expired");
            }
        }
    }

    private sealed class PaArtworkClock : TimeProvider
    {
        private long _timestamp;
        private PaArtworkTimer? _pending;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Read(ref _timestamp);
        internal bool IsWaiting => Volatile.Read(ref _pending) is not null;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            Assert(dueTime > TimeSpan.Zero && period == Timeout.InfiniteTimeSpan, "artwork must use a one-time endpoint delay");
            var timer = new PaArtworkTimer(this, GetTimestamp() + dueTime.Ticks, callback, state);
            Assert(Interlocked.CompareExchange(ref _pending, timer, null) is null, "overlapping delays in sequential artwork hydration");
            return timer;
        }

        internal void Advance(TimeSpan duration)
        {
            var now = Interlocked.Add(ref _timestamp, duration.Ticks);
            if (Volatile.Read(ref _pending) is { } timer && timer.Due <= now && Remove(timer)) timer.Fire();
        }

        private bool Remove(PaArtworkTimer timer) => ReferenceEquals(Interlocked.CompareExchange(ref _pending, null, timer), timer);

        private sealed class PaArtworkTimer(PaArtworkClock owner, long due, TimerCallback callback, object? state) : ITimer
        {
            internal long Due => due;
            internal void Fire() => callback(state);
            public bool Change(TimeSpan dueTime, TimeSpan period) => throw new NotSupportedException();
            public void Dispose() => owner.Remove(this);
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }

    private static async Task PaConfirmedParentAlbum()
    {
        var directory = PaDirectory("confirmed-parent");
        var store = new PlaybackStateStore(directory);
        var rows = new[] { PaRow("9801"), PaRow("9801"), PaRow("9802"), PaRow("9803"),
            PaRow("9804", albumId: "92"), PaRow("9805", PaOtherCover), PaRow("9806") };
        store.Save(PlaybackStateStore.Capture("12345", PaQueue(rows, 0), 77.25));
        var albums = new List<string>();
        using var fixture = new PaFixture(directory, "window", async (request, token) =>
        {
            if (request.Method == HttpMethod.Post)
            {
                var ids = await PaRequestedIds(request, token);
                Assert(ids.SequenceEqual(new[] { "9801:91", "9802:91", "9803:91", "9804:92", "9806:91" }),
                    "album hydration sent duplicate or already populated track references");
                (string Id, string? Cover, string Album)[] metadata =
                [
                    ("9801", null, "91"), ("9802", PaOtherCover, "91"), ("9803", PaOtherCover, "92"),
                    ("9804", null, "92"), ("9806", PaOtherCover, "92")
                ];
                return Json(JsonSerializer.Serialize(new { result = metadata.Select(row => new
                {
                    id = row.Id, coverUri = row.Cover, albums = new[] { new { id = row.Album } }
                }) }));
            }
            Assert(request.Method == HttpMethod.Get && request.RequestUri?.Host == "api.music.yandex.net"
                && request.Headers.Authorization?.ToString() == "OAuth " + FakeSession.AccessToken,
                "parent artwork hydration did not use the same checked API session");
            var path = request.RequestUri!.AbsolutePath;
            albums.Add(path);
            var exact = path == "/albums/91/with-tracks";
            Assert(exact || path == "/albums/92/with-tracks", "cover fallback requested an unrelated album edition");
            var members = exact ? new[] { "9801", "9802", "9805", "9806" } : new[] { "9804" };
            return Json(JsonSerializer.Serialize(new { result = new { id = exact ? "91" : "93", title = "Fixture parent", coverUri = PaCover,
                volumes = new[] { members.Select(id => new { id, title = "Catalog track" }).ToArray() } } }));
        });
        await fixture.OpenAndRestore();
        var original = fixture.Queue.Items.ToArray();
        var occurrence = fixture.Queue.CurrentOccurrenceToken;
        await fixture.WaitArtwork();
        PaInvoke(fixture.Window, "ShowQueueCore", false);
        await Until(() => Artwork.GetLoadStatus(Control<Image>(fixture.Window, "PlayerCover")) == ArtworkLoadStatus.Ready
            && PaQueueImages(fixture.Window).Any(image => image.DataContext is TrackRow { Id: "9801" }
                && Artwork.GetLoadStatus(image) == ArtworkLoadStatus.Ready));
        string?[] expected = [PaCover, PaCover, PaOtherCover, null, null, PaOtherCover, PaCover];
        Assert(albums.SequenceEqual(new[] { "/albums/91/with-tracks", "/albums/92/with-tracks" })
            && fixture.ApiHandler.Seen.Count == 3 && original.Select(row => row.CoverUri).SequenceEqual(expected),
            "parent artwork ignored strict membership/coalescing or displaced a direct cover");
        Assert(fixture.Queue.Items.SequenceEqual(original) && fixture.Queue.CurrentOccurrenceToken == occurrence
            && original.Select(row => row.RequestId).SequenceEqual(rows.Select(row => row.RequestId)),
            "parent artwork changed the saved edition, rows or occurrence");
        PaAssertPaused(fixture, rows.Length, 0);
        await fixture.WaitSave();
        Assert(store.Load("12345")!.Tracks.Select(row => row.CoverUri).SequenceEqual(expected), "confirmed parent artwork was not retained");
    }

    private static async Task PaExactAlbumMemberArtwork()
    {
        foreach (var parentHasCover in new[] { true, false })
        {
            var directory = PaDirectory("exact-member-" + parentHasCover);
            var rows = new[] { PaRow("9821"), PaRow("9821"), PaRow("9822"), PaRow("9823"), PaRow("9824", PaCover) };
            var store = new PlaybackStateStore(directory);
            store.Save(PlaybackStateStore.Capture("12345", PaQueue(rows, 0), 77.25));
            using var fixture = new PaFixture(directory, "window", async (request, token) =>
            {
                if (request.Method == HttpMethod.Post)
                {
                    var ids = await PaRequestedIds(request, token);
                    Assert(ids.SequenceEqual(new[] { "9821:91", "9822:91", "9823:91" }), "hydration duplicated or requested an already covered row");
                    // /tracks omits other exact references. Its confirmed direct
                    // image for this row must win over a different album member image.
                    return PaMetadata(["9822:91"]);
                }
                Assert(request.Method == HttpMethod.Get && request.RequestUri?.AbsolutePath == "/albums/91/with-tracks", "wrong parent album requested");
                var members = new[] { "9821", "9822", "9823" }.Select(id => new
                {
                    id, title = "Fixture member", coverUri = id == "9823" ? "https://invalid.test/cover" : PaOtherCover
                }).ToArray();
                return Json(JsonSerializer.Serialize(new { result = new { id = "91", title = "Parent fixture", coverUri = parentHasCover ? PaCover : null,
                    volumes = new[] { members } } }));
            });
            await fixture.OpenAndRestore();
            var original = fixture.Queue.Items.ToArray();
            var occurrence = fixture.Queue.CurrentOccurrenceToken;
            await fixture.WaitArtwork();
            await Until(() => Artwork.GetLoadStatus(Control<Image>(fixture.Window, "PlayerCover")) == ArtworkLoadStatus.Ready);
            string?[] expected = [PaOtherCover, PaOtherCover, PaCover, parentHasCover ? PaCover : null, PaCover];
            PaAssertPaused(fixture, rows.Length, 0);
            Assert(fixture.ApiHandler.Seen.Count == 2 && fixture.Queue.Items.SequenceEqual(original)
                && fixture.Queue.CurrentOccurrenceToken == occurrence && original.Select(row => row.CoverUri).SequenceEqual(expected),
                "exact member or parent artwork displaced a more specific cover or changed the queue");
        }
    }

    private static async Task PaHydrateLyrics()
    {
        var directory = PaDirectory("hydrate-lyrics");
        new PlaybackStateStore(directory).Save(PlaybackStateStore.Capture("12345", PaQueue([PaRow("8181")], 0), 77.25));
        var metadataEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseMetadata = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var lyricsRequests = 0;
        Task<HttpResponseMessage> NoLyrics(HttpRequestMessage _, CancellationToken __)
        {
            Interlocked.Increment(ref lyricsRequests);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
        using var lyricsHttp = new HttpClient(new FakeHandler(NoLyrics));
        using var fixture = new PaFixture(directory, "window", async (request, token) =>
        {
            var ids = await PaRequestedIds(request, token);
            metadataEntered.SetResult();
            await releaseMetadata.Task;
            return PaMetadata(ids);
        });
        try
        {
            PaSet(fixture.Window, "_geniusLyrics", new GeniusLyricsClient(new FakeHandler(NoLyrics)));
            PaField<LrclibLyricsClient?>(fixture.Window, "_lrclibLyrics")?.Dispose();
            PaSet(fixture.Window, "_lrclibLyrics", new LrclibLyricsClient(lyricsHttp));
            await fixture.OpenAndRestore();
            await metadataEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            PaInvoke(fixture.Window, "SetLyricsVisible", true);
            var lyrics = Control<ListBox>(fixture.Window, "LyricsList");
            var cover = Control<Image>(fixture.Window, "LyricsCover");
            var lines = new[] { new LyricLine(5, "Displayed fixture line") };
            lyrics.ItemsSource = lines;
            Assert(cover.IsVisible && cover.Source is null, "lyrics did not start visible and without restored artwork");

            releaseMetadata.SetResult();
            await fixture.WaitArtwork();
            await Until(() => Artwork.GetLoadStatus(cover) == ArtworkLoadStatus.Ready);
            Assert(Artwork.GetSourceUri(cover) == PaCover && ReferenceEquals(lyrics.ItemsSource, lines)
                && fixture.ApiHandler.Seen.Count == 1 && lyricsRequests == 0,
                "hydration changed the displayed lyrics or requested their providers again");
        }
        finally { releaseMetadata.TrySetResult(); }
    }

    private static async Task PaMetadataGuards()
    {
        var directory = PaDirectory("metadata-guards");
        new PlaybackStateStore(directory).Save(PlaybackStateStore.Capture("12345", PaQueue([PaRow("8001"), PaRow("8002"), PaRow("8003"), PaRow("8004")], 0), 77.25));
        using var fixture = new PaFixture(directory, "window", (request, _) => Task.FromResult(request.Method == HttpMethod.Get
            ? Json("", HttpStatusCode.NotFound) : Json("""
            {"result":[
              {"id":"8001","coverUri":"avatars.yandex.net/get-music-content/123/restore-%%","albums":[{"id":"92","title":"Wrong context"}]},
              {"id":"8002","coverUri":"avatars.yandex.net/get-music-content/123/restore-%%","albums":[{"id":"91","title":"Confirmed"}]},
              {"id":"8003","coverUri":"https://avatars.yandex.net/cover?token=fixture-private","albums":[{"id":"91","title":"Unsafe"}]}]}
            """)));
        await fixture.OpenAndRestore(); await fixture.WaitArtwork();
        Assert(fixture.Queue.Items.Select(row => row.CoverUri).SequenceEqual(new string?[] { null, PaCover, null, null }),
            "mismatched album, unsafe URL, or missing API metadata filled a restored cover");
        Assert(fixture.Queue.Items.All(row => row.Title.StartsWith("Saved ", StringComparison.Ordinal))
            && Control<Image>(fixture.Window, "PlayerCover").Source is null, "artwork-only hydration overwrote queue metadata or painted an unconfirmed current cover");
        PaAssertPaused(fixture, 4, 0);
    }

    private static async Task PaLegacyAlbumHints()
    {
        foreach (var mode in new[] { "bare-correct", "bare-wrong", "suffix-priority" })
        {
            var directory = PaDirectory("album-hint-" + mode);
            var suffix = mode == "suffix-priority";
            var row = new TrackRow
            {
                Id = "9301", RequestId = suffix ? "9301:92" : "9301", AlbumId = "91", Title = "Saved bare legacy track",
                Artist = "Saved artist", Album = "Saved album", Available = true, DurationMs = 180000
            };
            // The bare and composite queue occurrences describe one confirmed
            // metadata query; hydration must retain both original RequestIds.
            var rows = suffix ? new[] { row } : new[] { row, PaRow("9301") };
            var requestIds = rows.Select(track => track.RequestId).ToArray();
            var store = new PlaybackStateStore(directory);
            store.Save(PlaybackStateStore.Capture("12345", PaQueue(rows, 0), 77.25));
            var queries = new List<string[]>();
            var parentQueries = 0;
            using var fixture = new PaFixture(directory, "window", async (request, token) =>
            {
                if (request.Method == HttpMethod.Get)
                {
                    Assert(mode == "bare-wrong" && request.RequestUri?.AbsolutePath == "/albums/91/with-tracks", "legacy fallback requested an unrelated parent");
                    parentQueries++;
                    return Json("", HttpStatusCode.NotFound);
                }
                var ids = await PaRequestedIds(request, token); queries.Add(ids);
                if (mode != "bare-wrong") return PaMetadata(ids);
                return Json("""{"result":[{"id":"9301","coverUri":"avatars.yandex.net/get-music-content/123/restore-%%","albums":[{"id":"92","title":"Wrong album"}]}]}""");
            });
            await fixture.OpenAndRestore(); await fixture.WaitArtwork();
            var expectedQuery = suffix ? "9301:92" : "9301:91";
            var expectedCover = mode == "bare-wrong" ? null : PaCover;
            Assert(queries.Count == 1 && queries[0].SequenceEqual(new[] { expectedQuery }) && parentQueries == (mode == "bare-wrong" ? 1 : 0),
                mode + ": saved album hint was ignored, suffix lost priority, or equivalent composite queries were duplicated");
            Assert(fixture.Queue.Items.Select(track => track.RequestId).SequenceEqual(requestIds)
                && fixture.Queue.Items.All(track => track.AlbumId == "91" && track.CoverUri == expectedCover),
                mode + ": artwork query rewrote original queue identity or accepted the wrong confirmed album");
            PaAssertPaused(fixture, rows.Length, 0);
            await fixture.WaitSave();
            Assert(store.Load("12345")!.Tracks.Select(track => track.RequestId).SequenceEqual(requestIds),
                mode + ": artwork hydration persisted its query ID over the original legacy RequestId");
        }
    }

    private static async Task PaPreferredAlbumCovers()
    {
        foreach (var mode in new[] { "matching", "unsafe", "missing-cover", "missing-album", "direct-precedence" })
        {
            var albums = new List<Dictionary<string, object?>>
            {
                new() { ["id"] = "92", ["title"] = "Wrong first album", ["coverUri"] = PaOtherCover }
            };
            if (mode != "missing-album")
            {
                var matching = new Dictionary<string, object?> { ["id"] = "91", ["title"] = "Requested album" };
                if (mode == "matching") matching["coverUri"] = PaCover;
                else if (mode is "unsafe" or "direct-precedence") matching["coverUri"] = "https://avatars.yandex.net/cover?token=fixture-private";
                albums.Add(matching);
            }
            var track = new Dictionary<string, object?> { ["id"] = "9401", ["title"] = "Fixture", ["albums"] = albums };
            if (mode == "direct-precedence") track["coverUri"] = PaCover;
            var handler = new FakeHandler((_, _) => Task.FromResult(Json(JsonSerializer.Serialize(new { result = new[] { track } }))));
            using var api = new MusicApi(handler);
            var row = (await api.GetTracksAsync(FakeSession, [new("9401", "91")], 0, CancellationToken.None)).Single();
            var expected = mode is "matching" or "direct-precedence" ? PaCover : null;
            Assert(row.RequestId == "9401:91" && row.CoverUri == expected,
                mode + ": requested album artwork fell back to another album or displaced a direct track cover");
            Assert(row.AlbumId == (mode == "missing-album" ? "92" : "91")
                && handler.Seen.Count == 1 && Uri.UnescapeDataString(handler.Seen[0].Body).Contains("track-ids=9401:91", StringComparison.Ordinal),
                mode + ": metadata request or selected album identity changed during artwork selection");
        }
    }

    private static async Task PaCompositeResponses()
    {
        foreach (var reversed in new[] { false, true })
        {
            var first = new { id = "9501", title = "Album 91 fixture", coverUri = PaCover, albums = new[] { new { id = "91", title = "Album 91" } } };
            var second = new { id = "9501", title = "Album 92 fixture", coverUri = PaOtherCover, albums = new[] { new { id = "92", title = "Album 92" } } };
            var payload = reversed ? new[] { second, first } : new[] { first, second };
            var handler = new FakeHandler((_, _) => Task.FromResult(Json(JsonSerializer.Serialize(new { result = payload }))));
            using var api = new MusicApi(handler);
            var requested = new LikedId[] { new("9501", "92"), new("9501", "91"), new("9501", "92") };
            var rows = await api.GetTracksAsync(FakeSession, requested, 10, CancellationToken.None);
            Assert(rows.Select(row => row.RequestId).SequenceEqual(new[] { "9501:92", "9501:91", "9501:92" })
                && rows.Select(row => row.AlbumId).SequenceEqual(new[] { "92", "91", "92" })
                && rows.Select(row => row.CoverUri).SequenceEqual(new[] { PaOtherCover, PaCover, PaOtherCover }),
                "same-ID response entries collapsed distinct requested album contexts; reversed=" + reversed);
            Assert(rows.Select(row => row.Number).SequenceEqual(new[] { 11, 12, 13 })
                && rows.Select(row => row.Title).SequenceEqual(new[] { "Album 92 fixture", "Album 91 fixture", "Album 92 fixture" })
                && !ReferenceEquals(rows[0], rows[2]) && handler.Seen.Count == 1,
                "composite response mapping changed request order, duplicate occurrence, or metadata context");
        }
    }

    private static async Task PaStale()
    {
        foreach (var mode in new[] { "logout", "account", "cancel", "replacement" })
        {
            var directory = PaDirectory("stale-" + mode);
            new PlaybackStateStore(directory).Save(PlaybackStateStore.Capture("12345", PaQueue([PaRow("9001"), PaRow("9002")], 1), 77.25));
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var fixture = new PaFixture(directory, "window", async (request, _) =>
            { var ids = await PaRequestedIds(request, CancellationToken.None); entered.TrySetResult(); await release.Task; return PaMetadata(ids); });
            try
            {
                await fixture.OpenAndRestore(); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                var original = fixture.Queue.Items.ToArray(); var pending = PaField<Task?>(fixture.Window, "_restoredArtworkTask")!;
                if (mode == "logout") fixture.Window.SignOut();
                else if (mode == "account")
                {
                    PaSet(fixture.Window, "_account", new Account("67890", "Other fixture", true));
                    PaSet(fixture.Window, "_session", FakeSession with { DeviceId = "another-fixture-device" });
                }
                else if (mode == "cancel") PaField<CancellationTokenSource>(fixture.Window, "_accountCts").Cancel();
                else
                {
                    var replacement = PaRow("9002"); fixture.Queue.SetItems([replacement]);
                    PaSet(fixture.Window, "_current", replacement);
                }
                release.TrySetResult(); await pending.WaitAsync(TimeSpan.FromSeconds(5));
                Assert(original.All(row => row.CoverUri is null) && fixture.Queue.Items.All(row => row.CoverUri is null)
                    && Artwork.GetSourceUri(Control<Image>(fixture.Window, "PlayerCover")) is null && fixture.ArtworkRequests == 0,
                    mode + ": stale metadata republished into a detached or different account queue");
                Assert(fixture.MediaRequests == 0 && !fixture.Player.HasSource && !fixture.Player.IsPlaying, mode + ": stale artwork opened audio");
                if (mode == "logout") Assert(fixture.Queue.Items.Count == 0 && !File.Exists(Path.Combine(directory, PlaybackStateStore.FileName)), "logout state was resurrected");
                else if (mode != "replacement") PaAssertPaused(fixture, 2, 1);
            }
            finally { release.TrySetResult(); }
        }
    }

    private static async Task PaClosed()
    {
        var directory = PaDirectory("closed"); var store = new PlaybackStateStore(directory);
        store.Save(PlaybackStateStore.Capture("12345", PaQueue([PaRow("9101"), PaRow("9101")], 1), 77.25));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var fixture = new PaFixture(directory, "window", async (request, _) =>
        { var ids = await PaRequestedIds(request, CancellationToken.None); entered.TrySetResult(); await release.Task; return PaMetadata(ids); });
        try
        {
            await fixture.OpenAndRestore(); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var original = fixture.Queue.Current!; var pending = PaField<Task?>(fixture.Window, "_restoredArtworkTask")!;
            await fixture.Player.SetOutputDeviceAsync(new PlaybackOptionsFakeRoute("fixture-resume"));
            var opening = fixture.Player.OpenPausedAsync(new Uri(PlaybackOptionsSilentWave("close-during-resume", 4)), default);
            // MediaOpened posts to this dispatcher. Close in the same dispatcher turn as source assignment.
            Assert(fixture.Player.HasSource && !opening.IsCompleted, "decoder did not begin opening synchronously");
            fixture.Close(); var afterClose = File.ReadAllBytes(Path.Combine(directory, PlaybackStateStore.FileName));
            release.TrySetResult();
            try { await opening.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (OperationCanceledException) { }
            await pending.WaitAsync(TimeSpan.FromSeconds(5));
            Assert(original.CoverUri is null && fixture.ArtworkRequests == 0
                && store.Load("12345") is { CurrentIndex: 1, PositionSeconds: 77.25 }
                && afterClose.SequenceEqual(File.ReadAllBytes(Path.Combine(directory, PlaybackStateStore.FileName))),
                "closing during open lost the saved duplicate position or late artwork changed the snapshot");
        }
        finally { release.TrySetResult(); }
    }

    private static async Task PaUnreadableOnRestore()
    {
        var directory = PaDirectory("unreadable-on-restore"); var store = new PlaybackStateStore(directory);
        store.Save(PlaybackStateStore.Capture("12345", PaQueue([PaRow("9102", PaCover)], 0), 77.25));
        var path = Path.Combine(directory, PlaybackStateStore.FileName);
        var original = File.ReadAllBytes(path);
        using (var fixture = new PaFixture(directory, "blocked"))
        {
            using (var held = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.None))
                await fixture.OpenAndRestore(session: false);
            Assert(fixture.Queue.Items.Count == 0, "the held snapshot unexpectedly loaded");
            fixture.Close();
        }
        Assert(original.SequenceEqual(File.ReadAllBytes(path)), "close overwrote a temporarily unavailable valid snapshot");
        using var reopened = new PaFixture(directory, "reopened");
        await reopened.OpenAndRestore(session: false);
        PaAssertPaused(reopened, 1, 0);
    }

    private static async Task PaOfflineRestoreRetry()
    {
        foreach (var failure in new[] { "locked-index", "pending-journal", "invalid-index" })
        {
            var directory = PaDirectory("offline-restore-" + failure); var store = new PlaybackStateStore(directory);
            store.Save(PlaybackStateStore.Capture("12345", PaQueue([PaRow("9102", PaCover), PaRow("9102", PaCover)], 1), 77.25));
            var music = Path.Combine(directory, "offline"); Directory.CreateDirectory(music);
            var index = Path.Combine(music, "library.index.json");
            File.WriteAllText(index, failure == "invalid-index" ? "{" : "[]");
            var savedPath = Path.Combine(directory, PlaybackStateStore.FileName);
            var original = File.ReadAllBytes(savedPath);
            // The download center reads storage during construction. Hold the
            // sharing lock first so this case exercises a cold index lookup.
            using var locked = failure == "locked-index" ? File.Open(index, FileMode.Open, FileAccess.Read, FileShare.None) : null;
            using var fixture = new PaFixture(directory, "window");
            var recovery = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            if (failure == "pending-journal")
            {
                var state = typeof(OfflineLibrary).GetField("_directoryState", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .GetValue(PaField<OfflineLibrary>(fixture.Window, "_offline"))!;
                state.GetType().GetField("RecoveryTask", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.SetValue(state, recovery.Task);
            }
            await fixture.OpenAndRestore(session: false);
            Assert(fixture.Queue.Items.Count == 0, $"{failure}: the blocked restore published a queue");
            Assert(PaField<bool>(fixture.Window, "_playbackStateRestoreUnavailable") == (failure != "invalid-index"),
                $"{failure}: wrong restore retry policy");
            Assert(PaInvoke(fixture.Window, "TrySavePlaybackStateForExit") is false, $"{failure}: close accepted a blocked checkpoint");
            Assert(original.SequenceEqual(File.ReadAllBytes(savedPath)), $"{failure}: failed restore replaced a valid checkpoint");
            locked?.Dispose();
            if (failure == "invalid-index") continue;
            if (failure == "pending-journal")
            {
                var retry = (Task)PaInvoke(fixture.Window, "RetryPlaybackRestoreAfterRecoveryAsync")!;
                Assert(!retry.IsCompleted, "playback restore did not wait for recovery");
                recovery.SetResult();
                await retry.WaitAsync(TimeSpan.FromSeconds(5));
            }
            Assert(PaInvoke(fixture.Window, "TrySavePlaybackStateForExit") is true
                && store.Load("12345") is { CurrentIndex: 1, PositionSeconds: 77.25 }, "healthy recovery did not allow checkpoint retry");
            PaAssertPaused(fixture, 2, 1);
        }
    }

    private static async Task PaLogoutClearRetry()
    {
        foreach (var mode in new[] { "original", "replacement-after", "edit-after", "replacement-before" })
        {
            var directory = PaDirectory("logout-clear-retry-" + mode); var store = new PlaybackStateStore(directory);
            store.Save(PlaybackStateStore.Capture("12345", PaQueue([PaRow("9102", PaCover)], 0), 77.25));
            var path = Path.Combine(directory, PlaybackStateStore.FileName);
            using var fixture = new PaFixture(directory, "window");
            await fixture.OpenAndRestore(session: false);
            var changedAfterLogout = mode is "replacement-after" or "edit-after";
            if (mode == "replacement-before") PublishReplacement();
            using (var held = File.Open(path, FileMode.Open, FileAccess.Read, changedAfterLogout ? FileShare.None : FileShare.Read))
            {
                fixture.Window.SignOut();
                Assert(PaField<PlaybackStateStore.ClearRetry?>(fixture.Window, "_pendingPlaybackStateClear") is { File: not null, Hash: not null }
                    && Control<TextBlock>(fixture.Window, "StatusText").Text.Contains("пока не удалена", StringComparison.Ordinal),
                    "logout did not retain and report the pending snapshot identity");
                PaSet(fixture.Window, "_account", new Account("12345", "Fixture Account", true));
                PaInvoke(fixture.Window, "RestorePlaybackState", "12345");
                Assert(fixture.Queue.Items.Count == 0 && PaInvoke(fixture.Window, "TrySavePlaybackStateForExit") is false,
                    "same-account return restored the signed-out snapshot or bypassed its pending deletion");
            }
            if (changedAfterLogout) PublishReplacement();
            PaInvoke(fixture.Window, "RestorePlaybackState", "12345");
            Assert(PaField<PlaybackStateStore.ClearRetry?>(fixture.Window, "_pendingPlaybackStateClear") is null,
                "the unlocked deletion stayed permanently pending");
            Assert(changedAfterLogout ? fixture.Queue.Current?.Id == "9104" && store.Load("12345") is { PositionSeconds: 33 }
                : fixture.Queue.Items.Count == 0 && !File.Exists(path), "logout retry erased a newer revision or restored the retired queue");

            void PublishReplacement()
            {
                // Simulate another writer's on-disk publication without advancing
                // this process's in-memory playback revision counter.
                var next = PlaybackStateStore.Capture("12345", PaQueue([PaRow("9104", PaCover)], 0), 33);
                var bytes = JsonSerializer.SerializeToUtf8Bytes(next,
                    new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
                if (mode == "edit-after") File.WriteAllBytes(path, bytes);
                else GuardedStoreFile.Write(path, bytes);
            }
        }
    }

    private static Task PaLogoutAncestorRetry()
    {
        foreach (var replace in new[] { false, true })
        {
            var directory = PaDirectory("logout-ancestor-" + replace);
            var writer = new PlaybackStateStore(directory);
            writer.Save(PlaybackStateStore.Capture("12345", PaQueue([PaRow("9102", PaCover)], 0), 77));
            var store = replace ? new PlaybackStateStore(directory) : writer;
            if (replace) Assert(store.Load("12345") is not null, "fixture did not load the checked snapshot");
            PlaybackStateStore.ClearRetry? retry = null;
            using (UninstallPathGuard.HoldDirectory(directory, deleteAccess: true))
            {
                try { store.Clear("12345", out retry); throw new InvalidOperationException("fixture failed to block opening the profile folder"); }
                catch (IOException) { }
            }
            Assert(retry is { File: not null, Hash: not null }, "profile-folder failure lost the last checked file or its digest");

            var path = Path.Combine(directory, PlaybackStateStore.FileName);
            if (replace)
            {
                var newer = PlaybackStateStore.Capture("12345", PaQueue([PaRow("9104", PaCover)], 0), 33);
                GuardedStoreFile.Write(path, JsonSerializer.SerializeToUtf8Bytes(newer,
                    new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
            }
            Assert(store.RetryClear(retry!.Value), "a released profile folder remained blocked");
            Assert(replace ? store.Load("12345") is { PositionSeconds: 33 } : !File.Exists(path),
                "retry erased a newer snapshot or failed to remove the checked one");
        }
        return Task.CompletedTask;
    }

    private static async Task PaEditedQueueCheckpointRetry()
    {
        foreach (var apply in new[] { false, true })
        {
            var directory = PaDirectory(apply ? "edited-queue-apply" : "edited-queue-close");
            var store = new PlaybackStateStore(directory);
            var path = Path.Combine(directory, PlaybackStateStore.FileName);
            store.Save(PlaybackStateStore.Capture("12345", PaQueue([PaRow("9210", PaCover)], 0), 77.25));
            var original = File.ReadAllBytes(path);
            var music = Path.Combine(directory, "offline"); Directory.CreateDirectory(music);
            var index = Path.Combine(music, "library.index.json");
            File.WriteAllText(index, "[]");
            // Hold a different dependency on each path. Close exercises a cold
            // offline-index read and Apply exercises the snapshot file itself.
            using var held = File.Open(apply ? path : index, FileMode.Open, FileAccess.Read, FileShare.None);
            using var fixture = new PaFixture(directory, "window");
            await fixture.OpenAndRestore(session: false);
            Assert(PaField<bool>(fixture.Window, "_playbackStateRestoreUnavailable") && fixture.Queue.Items.Count == 0,
                "fixture did not start with a retryable checkpoint failure");
            var row = PaRow("9211", PaCover);
            fixture.Queue.Enqueue(row);
            if (apply) fixture.Queue.RemoveMany([0]); // A user edit may leave an intentionally empty queue.
            var editedVersion = fixture.Queue.Version;

            if (apply)
            {
                var feed = Path.Combine(directory, "feed.json");
                File.WriteAllText(feed, "{\"githubRepository\":\"https://github.com/senduora/yamuse\"}");
                var backend = new Qf061A10UpdateBackend();
                var updates = new UpdateSupport(feed, true, _ => backend);
                await updates.CheckAsync();
                Task<bool> Apply() => (Task<bool>)PaInvoke(fixture.Window, "ApplyAppUpdateAsync", updates)!;
                Assert(!await Apply() && backend.Applies == 0, "apply bypassed the locked playback checkpoint");
                held.Dispose();
                await (Task)PaInvoke(fixture.Window, "RetryPlaybackRestoreAfterRecoveryAsync")!;
                Assert(fixture.Queue.Items.Count == 0 && fixture.Queue.Version == editedVersion
                    && original.SequenceEqual(File.ReadAllBytes(path)), "passive retry revived a queue the user emptied");
                File.WriteAllText(path, "{");
                Assert(!await Apply() && backend.Applies == 0 && File.ReadAllText(path) == "{",
                    "verified apply replaced a malformed snapshot");
                File.WriteAllBytes(path, original);
                Assert(await Apply() && backend.Applies == 1 && store.Load("12345") is { Tracks.Count: 0, CurrentIndex: -1 },
                    "apply did not persist the edited empty queue once the checkpoint was readable");
            }
            else
            {
                var prompts = 0;
                fixture.Window.CloseSaveFailurePrompt = failure =>
                {
                    prompts++;
                    Assert(prompts == 1 && failure.Contains("очередь воспроизведения", StringComparison.Ordinal)
                        && original.SequenceEqual(File.ReadAllBytes(path)), "close replaced the checkpoint while offline verification was blocked");
                    held.Dispose();
                    PaInvoke(fixture.Window, "RestorePlaybackState", "12345");
                    Assert(fixture.Queue.Version == editedVersion && ReferenceEquals(fixture.Queue.Current, row),
                        "passive retry replaced the user's live queue");
                    return MessageBoxResult.Yes;
                };
                fixture.Window.Close();
                await Until(() => PaField<bool>(fixture.Window, "_closed"));
                var written = store.Load("12345");
                Assert(prompts == 1 && written is { PositionSeconds: 0, CurrentIndex: 0 }
                    && written.Tracks.Single().Id == row.Id, "close retry did not save the edited queue");
            }

            Assert(fixture.Queue.Version == editedVersion && !fixture.Player.HasSource && fixture.MediaRequests == 0 && fixture.ApiHandler.Seen.Count == 0,
                "checkpoint recovery replayed, rebuilt or requested the old queue");
        }
    }

    private static Task PaOfflineCheckpointQualities()
    {
        var directory = PaDirectory("offline-checkpoint-qualities");
        var music = Path.Combine(directory, "offline");
        Directory.CreateDirectory(music);
        var low = PaOffline("9241", "low.mp3", PaCover);
        var high = low with { FileName = "high.mp3", Bitrate = 320 };
        var unrelatedEdition = high with { RequestId = "9241:92", AlbumId = "92", FileName = "another-edition.mp3" };
        var later = PaOffline("9242", "new-edition.mp3", PaCover);
        var index = Path.Combine(music, "library.index.json");
        foreach (var item in new[] { low, high, unrelatedEdition, later }) File.WriteAllBytes(Path.Combine(music, item.FileName), [1, 2, 3, 4, 5]);
        File.WriteAllText(index, JsonSerializer.Serialize(new[] { low, high, unrelatedEdition }));
        using var offline = new OfflineLibrary(music);
        var rows = offline.Tracks();
        var initial = new[]
        {
            rows.Single(row => Path.GetFileName(row.LocalPath) == low.FileName),
            rows.Single(row => Path.GetFileName(row.LocalPath) == high.FileName),
            new TrackRow { Id = "9242", RequestId = "9242:93", AlbumId = "93", Title = "Waiting", IsOfflineLibraryRow = true }
        };
        var store = new PlaybackStateStore(directory);
        store.Save(PlaybackStateStore.Capture("12345", PaQueue(initial, 0), 1.5));
        var snapshot = store.Load("12345")!;
        var restored = PlaybackStateStore.RestoreQueue(snapshot, offline);
        Assert(restored.Tracks.Select(row => Path.GetFileName(row.LocalPath)).SequenceEqual(new string?[] { low.FileName, high.FileName, null })
            && snapshot.Tracks[2].LocalSourceRequestId is null && restored.Tracks[2].IsOfflineLibraryRow,
            "checkpoint recovery merged two selected qualities or created a pin for an unavailable occurrence");
        File.WriteAllText(index, JsonSerializer.Serialize(new[] { low, high, unrelatedEdition, later }));
        Assert(offline.ResolvePlaybackSource(restored.Tracks[2], requireExactOfflineVariant: true)?.Path == Path.Combine(music, later.FileName),
            "an unbound offline occurrence refused the compatible saved copy added later");

        var originalPath = Path.Combine(music, low.FileName);
        var fallbackPath = Path.Combine(music, high.FileName);
        File.Delete(originalPath);
        var waiting = PlaybackStateStore.RestoreQueue(snapshot, offline).Tracks[0];
        Assert(waiting.LocalPath is null && !waiting.IsDownloaded && waiting.CanPlayInQueue
            && offline.ResolvePlaybackSource(waiting, requireExactOfflineVariant: true) is null
            && offline.ResolvePlaybackSource(waiting)?.Path == fallbackPath
            && offline.ResolvePlaybackSource(initial[0])?.Path == fallbackPath,
            "a missing exact bitrate was marked as downloaded or lost its same-edition fallback");
        File.Delete(fallbackPath);
        Assert(offline.ResolvePlaybackSource(waiting) is null && offline.ResolvePlaybackSource(initial[0]) is null,
            "removing the two copies of a bound edition selected a different album");
        File.WriteAllBytes(originalPath, [1, 2, 3, 4, 5]);
        Assert(offline.ResolvePlaybackSource(waiting, requireExactOfflineVariant: true)?.Path == originalPath,
            "the returning exact quality was not eligible for explicit playback");
        return Task.CompletedTask;
    }

    private static Task PaOfflineCheckpointMeasuredVariants()
    {
        foreach (int? confirmed in new int?[] { 192, null })
        {
            var directory = PaDirectory("confirmed-offline-variants-" + (confirmed.HasValue ? "known" : "legacy"));
            var music = Directory.CreateDirectory(Path.Combine(directory, "offline")).FullName;
            var first = SaveFixtureCopy(music, "first.mp3", 192, confirmed);
            var second = SaveFixtureCopy(music, "second.mp3", 320, 320);
            var index = Path.Combine(music, "library.index.json");
            File.WriteAllText(index, JsonSerializer.Serialize(new[] { second, first }));
            using var offline = new OfflineLibrary(music);
            var firstPath = offline.ResolvePath(first.FileName);
            var selected = offline.Tracks().Single(row => row.LocalPath == firstPath);
            var store = new PlaybackStateStore(directory);
            store.Save(PlaybackStateStore.Capture("12345", PaQueue([selected], 0), 1));
            var saved = store.Load("12345")!;
            var restored = PlaybackStateStore.RestoreQueue(saved, offline).Tracks.Single();
            Assert(saved.Tracks[0].LocalSourceConfirmedBitrate == confirmed && saved.Tracks[0].LocalSourceSha256 == first.Sha256
                && restored.LocalPath == firstPath && offline.ResolvePlaybackSource(restored, requireExactOfflineVariant: true)?.Path == firstPath,
                "restoring the selected copy substituted the stronger nominal alias");
            if (!confirmed.HasValue)
            {
                var older = saved with { Tracks = [saved.Tracks[0] with { LocalSourceSha256 = null }] };
                var ambiguous = PlaybackStateStore.RestoreQueue(older, offline).Tracks.Single();
                Assert(!ambiguous.IsDownloaded && ambiguous.CanPlayInQueue
                    && offline.ResolvePlaybackSource(ambiguous, requireExactOfflineVariant: true) is null
                    && offline.ResolvePlaybackSource(ambiguous) is { AdmittedExactLegacySelection: false } fallback
                    && fallback.Path == offline.ResolvePath(second.FileName),
                    "an older ambiguous binding treated a compatible alternate as exact");
                File.WriteAllText(index, JsonSerializer.Serialize(new[] { first }));
                var bound = PlaybackStateStore.RestoreQueue(older, offline).Tracks.Single().CopyForQueueOccurrence();
                File.WriteAllText(index, JsonSerializer.Serialize(new[] { second }));
                Assert(bound.LocalPath == firstPath && bound.RestoredLocalSha256 == first.Sha256
                    && offline.ResolvePlaybackSource(bound, requireExactOfflineVariant: true) is null
                    && offline.ResolvePlaybackSource(bound) is { AdmittedExactLegacySelection: false } compatible
                    && compatible.Path == offline.ResolvePath(second.FileName),
                    "an occupied legacy path was treated as missing when its indexed entry was removed");
                File.WriteAllText(index, JsonSerializer.Serialize(new[] { second, first with { ConfirmedBitrate = 192 } }));
                Assert(offline.ResolvePlaybackSource(restored, requireExactOfflineVariant: true)?.Path == firstPath,
                    "confirming the same bytes changed their exact binding");
            }
            File.Delete(firstPath);
            Assert(offline.ResolvePlaybackSource(restored, requireExactOfflineVariant: true) is null
                && offline.ResolvePlaybackSource(restored)?.Path == offline.ResolvePath(second.FileName),
                "implicit resumption did not keep the alternate separate from an explicit exact launch");
        }
        return Task.CompletedTask;

        static OfflineItem SaveFixtureCopy(string music, string fileName, int actualBitrate, int? knownBitrate)
        {
            var bytes = SilentMp3Seconds(4, actualBitrate);
            File.WriteAllBytes(Path.Combine(music, fileName), bytes);
            return PaOffline("9246", fileName, null) with
            { Bytes = bytes.Length, Sha256 = Convert.ToHexString(SHA256.HashData(bytes)), DurationMs = 4000, ConfirmedBitrate = knownBitrate };
        }
    }

    private static async Task PaLegacyExactCopyProof()
    {
        foreach (var presentAtRestore in new[] { true, false })
        {
            var directory = PaDirectory("legacy-exact-copy-proof-" + presentAtRestore);
            var music = Directory.CreateDirectory(Path.Combine(directory, "offline")).FullName;
            var bytes = SilentMp3Seconds(4, 192);
            var legacy = PaOffline("9247", "preferred.mp3", null) with
            { Bytes = bytes.Length, Sha256 = Convert.ToHexString(SHA256.HashData(bytes)), DurationMs = 4000 };
            var path = Path.Combine(music, legacy.FileName);
            var index = Path.Combine(music, "library.index.json");
            if (presentAtRestore) File.WriteAllBytes(path, bytes);
            File.WriteAllText(index, JsonSerializer.Serialize(new[] { legacy }));
            var row = new TrackRow { Id = legacy.Id, RequestId = legacy.RequestId, AlbumId = legacy.AlbumId, Title = "Legacy proven copy",
                DurationMs = 4000, IsOfflineLibraryRow = true, LocalSourceRequestId = legacy.RequestId,
                LocalSourceCodec = legacy.Codec, LocalSourceBitrate = legacy.Bitrate };
            var store = new PlaybackStateStore(directory);
            store.Save(PlaybackStateStore.Capture("12345", PaQueue([row], 0), 0));
            using (var fixture = new PaFixture(directory, "initial"))
            {
                await fixture.OpenAndRestore(session: false);
                Assert(fixture.Queue.Current?.LocalPath == (presentAtRestore ? path : null)
                    && store.Load("12345")!.Tracks.Single().LocalSourceSha256 is null,
                    "fixture did not restore the expected legacy checkpoint");
                if (!presentAtRestore) File.WriteAllBytes(path, bytes);
                await (Task)PaInvoke(fixture.Window, "TogglePlaybackAsync")!;
                await fixture.WaitSave();
                Assert(fixture.Player.SourceLocalPath == path && store.Load("12345")!.Tracks.Single().LocalSourceSha256 == legacy.Sha256,
                    "opening legacy audio omitted the validated content identity from the checkpoint");
            }
            var higher = SilentMp3Seconds(4, 320);
            var improved = legacy with { FileName = "improved.mp3", ConfirmedBitrate = 320, Bytes = higher.Length,
                Sha256 = Convert.ToHexString(SHA256.HashData(higher)) };
            File.WriteAllBytes(Path.Combine(music, improved.FileName), higher);
            File.WriteAllText(index, JsonSerializer.Serialize(new[] { improved, legacy }));
            using var reopened = new PaFixture(directory, "reopened");
            await reopened.OpenAndRestore(session: false);
            Assert(reopened.Queue.Current?.LocalPath == path && reopened.MediaRequests == 0,
                "later ambiguity displaced the previously proven legacy copy");
        }
    }

    private static async Task PaLegacyExpectedCopyBeforePlay()
    {
        foreach (var apply in new[] { false, true })
        {
            var directory = PaDirectory("legacy-expected-copy-before-play-" + apply);
            var music = Directory.CreateDirectory(Path.Combine(directory, "offline")).FullName;
            var bytes = SilentMp3Seconds(8, 192);
            var original = PaOffline("9248", "expected.mp3", null) with
            { Bytes = bytes.Length, Sha256 = Convert.ToHexString(SHA256.HashData(bytes)), DurationMs = 8000 };
            var originalPath = Path.Combine(music, original.FileName);
            var index = Path.Combine(music, "library.index.json");
            File.WriteAllBytes(originalPath, bytes);
            File.WriteAllText(index, JsonSerializer.Serialize(new[] { original }));
            var betterBytes = SilentMp3Seconds(8, 320);
            var better = original with { FileName = "better.mp3", ConfirmedBitrate = 320, Bytes = betterBytes.Length,
                Sha256 = Convert.ToHexString(SHA256.HashData(betterBytes)) };
            TrackRow Occurrence(string title) => new()
            {
                Id = original.Id, RequestId = original.RequestId, AlbumId = original.AlbumId, Title = title, DurationMs = 8000,
                IsOfflineLibraryRow = true, LocalSourceRequestId = original.RequestId, LocalSourceCodec = original.Codec,
                LocalSourceBitrate = original.Bitrate
            };
            var store = new PlaybackStateStore(directory);
            store.Save(PlaybackStateStore.Capture("12345", PaQueue([Occurrence("Earlier"), Occurrence("Selected")], 1), 1.75));
            using (var fixture = new PaFixture(directory, "initial"))
            {
                await fixture.OpenAndRestore(session: false);
                Assert(!fixture.Player.HasSource && fixture.Queue.CurrentIndex == 1
                    && fixture.Queue.Items.All(row => row.LocalPath == originalPath && row.RestoredLocalSha256 == original.Sha256
                        && row.OpenedLocalVariant is null), "initial restore did not select the unique legacy copy");
                File.WriteAllBytes(Path.Combine(music, better.FileName), betterBytes);
                File.WriteAllText(index, JsonSerializer.Serialize(new[] { better, original }));
                if (apply)
                {
                    var feed = Path.Combine(directory, "feed.json");
                    File.WriteAllText(feed, "{\"githubRepository\":\"https://github.com/senduora/yamuse\"}");
                    var backend = new Qf061A10UpdateBackend();
                    var updates = new UpdateSupport(feed, true, _ => backend);
                    await updates.CheckAsync();
                    Assert(await (Task<bool>)PaInvoke(fixture.Window, "ApplyAppUpdateAsync", updates)! && backend.Applies == 1,
                        "fixture did not finish apply before opening audio");
                }
                else
                {
                    fixture.Window.CloseSaveFailurePrompt = failure => throw new InvalidOperationException(failure);
                    fixture.Close();
                    await Until(() => PaField<bool>(fixture.Window, "_closed"));
                }
            }
            var saved = store.Load("12345")!;
            Assert(saved.CurrentIndex == 1 && saved.PositionSeconds == 1.75 && saved.Tracks.Count == 2
                && saved.Tracks.All(track => track.ExpectedLocalSourceSha256 == original.Sha256 && track.LocalSourceSha256 is null),
                "exit lost an unopened expectation or recorded it as a verified opening");
            if (apply)
            {
                using var offline = new OfflineLibrary(music);
                var parked = Path.Combine(directory, "parked.mp3");
                File.Move(originalPath, parked);
                var missing = PlaybackStateStore.RestoreQueue(saved, offline);
                var waiting = missing.Tracks[1];
                Assert(missing.CurrentIndex == 1 && missing.PositionSeconds == 1.75 && waiting.LocalPath is null
                    && waiting.RestoredLocalSha256 == original.Sha256
                    && offline.ResolvePlaybackSource(waiting, requireExactOfflineVariant: true) is null
                    && offline.ResolvePlaybackSource(waiting) is { AdmittedExactLegacySelection: false } fallback && fallback.Digest == better.Sha256,
                    "a missing expectation admitted a substitute as exact");
                store.Save(PlaybackStateStore.Capture("12345", PaQueue(missing.Tracks, 1), missing.PositionSeconds));
                Assert(store.Load("12345")!.Tracks.All(track => track.ExpectedLocalSourceSha256 == original.Sha256),
                    "a second checkpoint forgot the unavailable expectation");
                File.Move(parked, originalPath);
                using (var busy = new FileStream(originalPath, FileMode.Open, FileAccess.Write, FileShare.Read))
                {
                    var held = PlaybackStateStore.RestoreQueue(saved, offline).Tracks[1];
                    var candidate = offline.ResolvePlaybackSource(held, requireExactOfflineVariant: true);
                    Assert(candidate?.Digest == original.Sha256 && held.OpenedLocalVariant is null,
                        "a busy expected source switched to another copy");
                    try { using var lease = await candidate!.HoldAsync(CancellationToken.None); throw new InvalidOperationException("busy source opened"); }
                    catch (LocalAudioAccessException) { }
                }
            }
            using var reopened = new PaFixture(directory, "reopened");
            await reopened.OpenAndRestore(session: false);
            Assert(reopened.Queue.CurrentIndex == 1 && reopened.Queue.Current?.LocalPath == originalPath && !reopened.Player.HasSource
                && PaField<double?>(reopened.Window, "_restoredPlaybackSeconds") == 1.75, "the expected copy did not restore at its paused position");
            await (Task)PaInvoke(reopened.Window, "PlayTrackCoreAsync", reopened.Queue.Current!, null, false, null, false, true, null)!;
            await reopened.WaitSave();
            var opened = store.Load("12345")!;
            Assert(reopened.Player.SourceLocalPath == originalPath && reopened.Player.SourceVerifiedLocalDigest == original.Sha256
                && reopened.Queue.CurrentIndex == 1 && reopened.MediaRequests == 0
                && opened.Tracks[0].ExpectedLocalSourceSha256 == original.Sha256 && opened.Tracks[0].LocalSourceSha256 is null
                && opened.Tracks[1].ExpectedLocalSourceSha256 is null && opened.Tracks[1].LocalSourceSha256 == original.Sha256,
                "playback did not upgrade only the occurrence whose bytes it verified");
        }
    }

    private static async Task PaLegacyCopyBeforePlay()
    {
        foreach (var replace in new[] { false, true })
        {
            var directory = PaDirectory("legacy-copy-before-play-" + replace);
            var music = Directory.CreateDirectory(Path.Combine(directory, "offline")).FullName;
            var bytes = SilentMp3Seconds(8, 192);
            var original = PaOffline("9248", "restored.mp3", null) with
            { Bytes = bytes.Length, Sha256 = Convert.ToHexString(SHA256.HashData(bytes)), DurationMs = 8000 };
            var originalPath = Path.Combine(music, original.FileName);
            var index = Path.Combine(music, "library.index.json");
            File.WriteAllBytes(originalPath, bytes);
            File.WriteAllText(index, JsonSerializer.Serialize(new[] { original }));
            var replacementBytes = SilentMp3Seconds(8, 320);
            var replacement = original with { FileName = "new-copy.mp3", ConfirmedBitrate = 320, Bytes = replacementBytes.Length,
                Sha256 = Convert.ToHexString(SHA256.HashData(replacementBytes)) };
            var replacementPath = Path.Combine(music, replacement.FileName);
            var admitted = replace ? replacement : original;
            var admittedPath = replace ? replacementPath : originalPath;
            TrackRow Occurrence(string title) => new()
            {
                Id = original.Id, RequestId = original.RequestId, AlbumId = original.AlbumId, Title = title, DurationMs = 8000,
                IsOfflineLibraryRow = true, LocalSourceRequestId = original.RequestId, LocalSourceCodec = original.Codec,
                LocalSourceBitrate = original.Bitrate
            };
            var store = new PlaybackStateStore(directory);
            store.Save(PlaybackStateStore.Capture("12345", PaQueue([Occurrence("Earlier occurrence"), Occurrence("Selected occurrence")], 1), 1.75));
            using (var fixture = new PaFixture(directory, "initial"))
            {
                await fixture.OpenAndRestore(session: false);
                var selected = fixture.Queue.Current!;
                var occurrence = fixture.Queue.CurrentOccurrenceToken;
                Assert(selected.LocalPath == originalPath && selected.RestoredLocalSha256 == original.Sha256
                    && !fixture.Player.HasSource && store.Load("12345")!.Tracks[1].LocalSourceSha256 is null,
                    "restoration must remember the selected expectation before a verified open");
                if (replace) File.Move(originalPath, Path.Combine(directory, "parked.mp3"));
                File.WriteAllBytes(replacementPath, replacementBytes);
                File.WriteAllText(index, JsonSerializer.Serialize(new[] { replacement, original }));
                await (Task)PaInvoke(fixture.Window, "PlayTrackCoreAsync", selected, null, false, null, false, true, null)!;
                Assert(fixture.Player.SourceLocalPath == admittedPath && fixture.Player.SourceVerifiedLocalDigest == admitted.Sha256
                    && selected.OpenedLocalVariant?.Sha256 == admitted.Sha256
                    && fixture.Queue.CurrentOccurrenceToken == occurrence && fixture.MediaRequests == 0,
                    "the explicitly opened legacy occurrence did not checkpoint its verified copy");
                await Until(() => fixture.Player.Position.TotalSeconds >= 1.25);
                await (Task)PaInvoke(fixture.Window, "TogglePlaybackAsync")!;
                await fixture.WaitSave();
                if (replace)
                {
                    var feed = Path.Combine(directory, "feed.json");
                    File.WriteAllText(feed, "{\"githubRepository\":\"https://github.com/senduora/yamuse\"}");
                    var backend = new Qf061A10UpdateBackend();
                    var updates = new UpdateSupport(feed, true, _ => backend);
                    await updates.CheckAsync();
                    Assert(await (Task<bool>)PaInvoke(fixture.Window, "ApplyAppUpdateAsync", updates)! && backend.Applies == 1,
                        "fixture apply did not persist the admitted occurrence");
                    File.Move(Path.Combine(directory, "parked.mp3"), originalPath);
                }
                else
                {
                    fixture.Window.CloseSaveFailurePrompt = failure => throw new InvalidOperationException(failure);
                    fixture.Close();
                    await Until(() => PaField<bool>(fixture.Window, "_closed"));
                }
            }
            var saved = store.Load("12345")!;
            Assert(saved.CurrentIndex == 1 && saved.PositionSeconds >= 1.25 && saved.Tracks.Count == 2
                && saved.Tracks[0].LocalSourceSha256 is null && saved.Tracks[1].LocalSourceSha256 == admitted.Sha256,
                "final persistence lost the duplicate occurrence, position or the verified copy");
            using var reopened = new PaFixture(directory, "reopened");
            await reopened.OpenAndRestore(session: false);
            Assert(reopened.Queue.CurrentIndex == 1 && reopened.Queue.Current?.LocalPath == admittedPath && !reopened.Player.HasSource
                && PaField<double?>(reopened.Window, "_restoredPlaybackSeconds") == saved.PositionSeconds,
                "the proved occurrence did not reopen paused at its selected file");
            await (Task)PaInvoke(reopened.Window, "PlayTrackCoreAsync", reopened.Queue.Current!, null, false, null, false, true, null)!;
            Assert(reopened.Player.SourceLocalPath == admittedPath && reopened.Player.SourceVerifiedLocalDigest == admitted.Sha256
                && reopened.Queue.CurrentIndex == 1 && reopened.MediaRequests == 0, "reopening did not reverify the stored exact copy");
        }
    }

    private static async Task PaLateLegacyExactCopyProof()
    {
        foreach (var mode in new[] { "competing-copy", "bitrate-refinement" })
        foreach (var apply in new[] { false, true })
        {
            var directory = PaDirectory($"legacy-proof-during-open-{mode}-{apply}");
            var music = Directory.CreateDirectory(Path.Combine(directory, "offline")).FullName;
            var bytes = SilentMp3Seconds(8, 192);
            var admitted = PaOffline("9248", "preferred.mp3", null) with
            { Bytes = bytes.Length, Sha256 = Convert.ToHexString(SHA256.HashData(bytes)), DurationMs = 8000 };
            var preferredPath = Path.Combine(music, admitted.FileName);
            var index = Path.Combine(music, "library.index.json");
            File.WriteAllText(index, JsonSerializer.Serialize(new[] { admitted }));
            var improvedBytes = SilentMp3Seconds(8, 320);
            var improved = admitted with { FileName = "later-copy.mp3", ConfirmedBitrate = 320, Bytes = improvedBytes.Length,
                Sha256 = Convert.ToHexString(SHA256.HashData(improvedBytes)) };
            var refined = admitted with { ConfirmedBitrate = 192 };
            TrackRow Occurrence(string title) => new()
            {
                Id = admitted.Id, RequestId = admitted.RequestId, AlbumId = admitted.AlbumId, Title = title, DurationMs = 8000,
                IsOfflineLibraryRow = true, LocalSourceRequestId = admitted.RequestId, LocalSourceCodec = admitted.Codec,
                LocalSourceBitrate = admitted.Bitrate
            };
            var store = new PlaybackStateStore(directory);
            store.Save(PlaybackStateStore.Capture("12345", PaQueue([Occurrence("Earlier occurrence"), Occurrence("Selected occurrence")], 1), 1.75));
            using (var fixture = new PaFixture(directory, "initial"))
            {
                await fixture.OpenAndRestore(session: false);
                var selected = fixture.Queue.Current!;
                var occurrence = fixture.Queue.CurrentOccurrenceToken;
                Assert(fixture.Queue.CurrentIndex == 1 && selected.LocalPath is null && !fixture.Player.HasSource,
                    "fixture did not restore the paused later occurrence without its file");
                File.WriteAllBytes(preferredPath, bytes);
                var changedWhileOpening = false;
                var offline = PaField<OfflineLibrary>(fixture.Window, "_offline");
                offline.BeforeOpenedCheckpointProofForChecks = () =>
                {
                    Assert(fixture.Player.SourceLocalPath == preferredPath && fixture.Player.SourceVerifiedLocalDigest == admitted.Sha256
                        && !changedWhileOpening, "the fixture did not hold the actual verified opening");
                    changedWhileOpening = true;
                    if (mode == "competing-copy")
                    {
                        File.WriteAllBytes(Path.Combine(music, improved.FileName), improvedBytes);
                        File.WriteAllText(index, JsonSerializer.Serialize(new[] { improved, admitted }));
                    }
                    else File.WriteAllText(index, JsonSerializer.Serialize(new[] { refined }));
                };
                await (Task)PaInvoke(fixture.Window, "TogglePlaybackAsync")!;
                offline.BeforeOpenedCheckpointProofForChecks = null;
                Assert(changedWhileOpening && fixture.Player.IsPlaying && fixture.Queue.CurrentOccurrenceToken == occurrence
                    && selected.OpenedLocalVariant?.Sha256 == admitted.Sha256 && fixture.MediaRequests == 0,
                    "the opening lost its own verified digest after the index update");
                await Until(() => fixture.Player.Position.TotalSeconds >= 1.25);
                await (Task)PaInvoke(fixture.Window, "TogglePlaybackAsync")!;
                await fixture.WaitSave();

                if (apply)
                {
                    var feed = Path.Combine(directory, "feed.json");
                    File.WriteAllText(feed, "{\"githubRepository\":\"https://github.com/senduora/yamuse\"}");
                    var backend = new Qf061A10UpdateBackend();
                    var updates = new UpdateSupport(feed, true, _ => backend);
                    await updates.CheckAsync();
                    Assert(await (Task<bool>)PaInvoke(fixture.Window, "ApplyAppUpdateAsync", updates)! && backend.Applies == 1,
                        "fixture apply did not persist the admitted occurrence");
                }
                else
                {
                    fixture.Window.CloseSaveFailurePrompt = failure => throw new InvalidOperationException(failure);
                    fixture.Close();
                    await Until(() => PaField<bool>(fixture.Window, "_closed"));
                }
            }
            if (mode == "bitrate-refinement")
            {
                File.WriteAllBytes(Path.Combine(music, improved.FileName), improvedBytes);
                File.WriteAllText(index, JsonSerializer.Serialize(new[] { improved, refined }));
            }
            var saved = store.Load("12345")!;
            Assert(saved.CurrentIndex == 1 && saved.PositionSeconds >= 1.25 && saved.Tracks.Count == 2
                && saved.Tracks[0].LocalSourceSha256 is null && saved.Tracks[1].LocalSourceSha256 == admitted.Sha256,
                "final persistence lost the selected duplicate occurrence, position or verified digest");
            using var reopened = new PaFixture(directory, "reopened");
            await reopened.OpenAndRestore(session: false);
            Assert(reopened.Queue.CurrentIndex == 1 && reopened.Queue.Current?.LocalPath == preferredPath && !reopened.Player.HasSource
                && PaField<double?>(reopened.Window, "_restoredPlaybackSeconds") == saved.PositionSeconds, "the stored occurrence did not reopen paused at its exact copy");
            await (Task)PaInvoke(reopened.Window, "PlayTrackCoreAsync", reopened.Queue.Current!, null, false, null, false, true, null)!;
            Assert(reopened.Player.SourceLocalPath == preferredPath && reopened.Player.SourceVerifiedLocalDigest == admitted.Sha256
                && reopened.Queue.CurrentIndex == 1 && reopened.MediaRequests == 0, "explicit reopening did not verify the same admitted candidate");
        }
    }

    private static async Task PaPreparedLegacyExactCopyProof()
    {
        var directory = PaDirectory("prepared-legacy-exact-copy-proof");
        var music = Directory.CreateDirectory(Path.Combine(directory, "offline")).FullName;
        var firstPath = Path.Combine(directory, "outgoing-fixture.mp3");
        File.WriteAllBytes(firstPath, SilentMp3Seconds(12, 192));
        var bytes = SilentMp3Seconds(12, 192);
        var exact = PaOffline("9249", "prepared-fixture.mp3", null) with
        { Bytes = bytes.Length, Sha256 = Convert.ToHexString(SHA256.HashData(bytes)), DurationMs = 12000 };
        var path = Path.Combine(music, exact.FileName);
        var index = Path.Combine(music, "library.index.json");
        File.WriteAllBytes(path, bytes);
        File.WriteAllText(index, JsonSerializer.Serialize(new[] { exact }));
        var first = new TrackRow { Id = "9001", RequestId = "9001", Title = "Outgoing fixture", LocalPath = firstPath, DurationMs = 12000, Available = true };
        var legacy = new TrackRow { Id = exact.Id, RequestId = exact.RequestId, AlbumId = exact.AlbumId, Title = "Prepared legacy fixture",
            DurationMs = 12000, IsOfflineLibraryRow = true, LocalSourceRequestId = exact.RequestId, LocalSourceCodec = exact.Codec,
            LocalSourceBitrate = exact.Bitrate };
        using var fixture = new PaFixture(directory, "window");
        await fixture.OpenAndRestore(session: false);
        var offline = PaField<OfflineLibrary>(fixture.Window, "_offline");
        var snapshot = PlaybackStateStore.Capture("12345", PaQueue([legacy], 0), 0);
        var next = PlaybackStateStore.RestoreQueue(snapshot, offline).Tracks.Single();
        fixture.Queue.RestoreItems([first, next], 0, false, RepeatMode.Off);
        var betterBytes = SilentMp3Seconds(12, 320);
        var better = exact with { FileName = "late-before-prepare.mp3", ConfirmedBitrate = 320, Bytes = betterBytes.Length,
            Sha256 = Convert.ToHexString(SHA256.HashData(betterBytes)) };
        File.WriteAllBytes(Path.Combine(music, better.FileName), betterBytes);
        File.WriteAllText(index, JsonSerializer.Serialize(new[] { better, exact }));
        var admitted = (LocalAudioSource?)PaInvoke(fixture.Window, "RefreshLocalPlaybackSource", next, false);
        Assert(admitted?.Path == path && next.CanPlayInQueue && ReferenceEquals(fixture.Queue.PeekNext(naturalEnd: true), next)
            && next.RestoredLocalSha256 == exact.Sha256 && next.OpenedLocalVariant is null,
            "the restored legacy occurrence did not retain its exact expectation for preparation");
        await fixture.Window.PlayTrackAsync(first);
        PaInvoke(fixture.Window, "RefreshNextPreparation");
        await Until(() => fixture.Player.HasPrepared);
        var prepared = PaInvoke(fixture.Window, "ReadyNextPlayback", true);
        Assert(prepared is not null && fixture.Player.PreparedLocalPath == path && next.OpenedLocalVariant is null,
            "the fixture did not prepare the selected candidate or it persisted proof before opening the occurrence");
        var changed = false;
        offline.BeforeOpenedCheckpointProofForChecks = () =>
        {
            Assert(fixture.Player.SourceLocalPath == path && fixture.Player.SourceVerifiedLocalDigest == exact.Sha256
                && fixture.Player.FadingLocalPath == firstPath, "the fixture did not promote the checked player slot");
            changed = true;
            File.WriteAllText(index, JsonSerializer.Serialize(new[] { better, exact with { ConfirmedBitrate = 192 } }));
        };
        await (Task)PaInvoke(fixture.Window, "AdvanceCoreAsync", 1, true, prepared)!;
        offline.BeforeOpenedCheckpointProofForChecks = null;
        await fixture.WaitSave();
        var saved = new PlaybackStateStore(directory).Load("12345")!;
        Assert(changed && fixture.Queue.CurrentIndex == 1 && ReferenceEquals(fixture.Queue.Current, next)
            && saved.Tracks[1].LocalSourceSha256 == exact.Sha256 && fixture.MediaRequests == 0,
            "prepared promotion failed to persist the proof from its actual open");
    }

    private static async Task PaReturningExactQueueCopy()
    {
        foreach (var mode in new[] { "listen", "cover-during-maintenance", "directory", "directory-saved-cover", "directory-other-edition",
            "rename-existing", "rename-root-existing", "replace-root-existing" })
        {
            var directory = PaDirectory("returning-exact-queue-copy-" + mode);
            var music = Directory.CreateDirectory(Path.Combine(directory, "offline")).FullName;
            var parked = Path.Combine(directory, "parked-offline");
            var replacement = Path.Combine(directory, "replacement-offline");
            var useCover = mode == "cover-during-maintenance";
            var entireDirectory = mode.StartsWith("directory", StringComparison.Ordinal);
            var otherEdition = mode == "directory-other-edition";
            var savedCover = mode == "directory-saved-cover" ? PaCover.Replace("restore", "already-saved") : null;
            var renameExisting = mode == "rename-existing";
            var renameRoot = mode == "rename-root-existing";
            var replaceRoot = mode == "replace-root-existing";
            var data = SilentMp3Seconds(4, 192);
            var alternateData = SilentMp3Seconds(4, 320);
            var exact = PaOffline("9245", "exact.mp3", PaCover) with
            { Bytes = data.Length, Sha256 = Convert.ToHexString(SHA256.HashData(data)), DurationMs = 4000 };
            var alternate = exact with { FileName = "alternate.mp3", Bitrate = 320, Bytes = alternateData.Length,
                Sha256 = Convert.ToHexString(SHA256.HashData(alternateData)) };
            var path = Path.Combine(music, exact.FileName);
            var parkedFile = Path.Combine(music, "parked-exact.mp3");
            if (renameExisting || renameRoot || replaceRoot) File.WriteAllBytes(path, data);
            File.WriteAllBytes(Path.Combine(music, alternate.FileName), alternateData);
            var index = JsonSerializer.Serialize(new[] { exact, alternate });
            File.WriteAllText(Path.Combine(music, "library.index.json"), index);
            if (replaceRoot)
            {
                Directory.CreateDirectory(replacement);
                File.WriteAllText(Path.Combine(replacement, "library.index.json"), index);
                File.WriteAllBytes(Path.Combine(replacement, alternate.FileName), alternateData);
            }
            var row = new TrackRow { Id = exact.Id, RequestId = otherEdition ? exact.Id + ":92" : exact.RequestId,
                AlbumId = otherEdition ? "92" : exact.AlbumId, CoverUri = savedCover,
                Title = "Synthetic exact queue row", DurationMs = 4000, IsOfflineLibraryRow = true,
                LocalSourceRequestId = exact.RequestId, LocalSourceCodec = exact.Codec, LocalSourceBitrate = exact.Bitrate,
                LocalSourceSha256 = exact.Sha256 };
            new PlaybackStateStore(directory).Save(PlaybackStateStore.Capture("12345", PaQueue([row], 0), 0));
            if (entireDirectory) Directory.Move(music, parked);
            using var fixture = new PaFixture(directory, "window");
            var canReadRootIdentity = !replaceRoot;
            if (replaceRoot) fixture.Window.QueueOfflineRootIdentityUnavailableForChecks = () => !canReadRootIdentity;
            await fixture.OpenAndRestore(session: false);
            PaInvoke(fixture.Window, "ShowQueueCore", false);
            var initialMarks = PaField<Task?>(fixture.Window, "_offlineMarksTask");
            if (initialMarks is { } marks) await marks.WaitAsync(TimeSpan.FromSeconds(5));
            var list = Control<ListView>(fixture.Window, "TracksList");
            var listen = Control<Button>(fixture.Window, "PlaySelectionButton");
            var restored = fixture.Queue.Current!;
            var occurrence = fixture.Queue.CurrentOccurrenceToken;
            list.ScrollIntoView(restored);
            fixture.Window.UpdateLayout();
            var cover = SaVisuals<Button>(list).Single(button => button.Name == "TrackCoverPlayButton" && ReferenceEquals(button.DataContext, restored));
            if (renameRoot || replaceRoot)
            {
                await Until(() => PaField<FileSystemWatcher?>(fixture.Window, "_queueOfflineWatcher") is not null
                    && PaField<Task?>(fixture.Window, "_offlineMarksTask") is { IsCompleted: true } current
                    && !ReferenceEquals(current, initialMarks));
                Assert(restored.IsDownloaded && listen.IsEnabled && cover.IsEnabled, "fixture did not finish the initial queue check");
                if (replaceRoot)
                {
                    var unanchoredWatcher = PaField<FileSystemWatcher?>(fixture.Window, "_queueOfflineWatcher");
                    Assert(PaField<UninstallFileIdentity?>(fixture.Window, "_queueOfflineRootIdentity") is null,
                        "fixture did not model a first watcher without a root identifier");
                    canReadRootIdentity = true;
                    await Until(() => PaField<UninstallFileIdentity?>(fixture.Window, "_queueOfflineRootIdentity") is not null
                        && PaField<FileSystemWatcher?>(fixture.Window, "_queueOfflineWatcher") is { } anchored
                        && !ReferenceEquals(anchored, unanchoredWatcher));
                    var oldWatcher = PaField<FileSystemWatcher?>(fixture.Window, "_queueOfflineWatcher");
                    await PaReplaceWatchedOfflineRootAsync(fixture.Window.Dispatcher, music, parked, replacement);
                    await Until(() => !restored.IsDownloaded && !listen.IsEnabled && !cover.IsEnabled
                        && PaField<FileSystemWatcher?>(fixture.Window, "_queueOfflineWatcher") is { } watcher && !ReferenceEquals(watcher, oldWatcher));
                }
                else
                {
                    Directory.Move(music, parked);
                    await Until(() => !restored.IsDownloaded && !listen.IsEnabled && !cover.IsEnabled
                        && PaField<FileSystemWatcher?>(fixture.Window, "_queueOfflineWatcher") is null);
                }
            }
            else if (renameExisting)
            {
                Assert(restored.IsDownloaded && listen.IsEnabled && cover.IsEnabled, "fixture did not display the playable exact copy");
                File.Move(path, parkedFile);
                await Until(() => !restored.IsDownloaded && !listen.IsEnabled && !cover.IsEnabled);
            }
            Assert(ReferenceEquals(list.Items[0], restored) && !restored.CanPlay && restored.CanPlayInQueue == !(entireDirectory || renameRoot)
                && !listen.IsEnabled && !cover.IsEnabled, "fixture did not keep absent exact playback disabled");
            if (useCover) PaInvoke(fixture.Window, "BeginOfflineMaintenance");
            if (entireDirectory)
            {
                File.WriteAllBytes(Path.Combine(parked, exact.FileName), data);
                Directory.Move(parked, music);
            }
            else if (renameRoot) Directory.Move(parked, music);
            else if (renameExisting) File.Move(parkedFile, path);
            else File.WriteAllBytes(path, data);
            if (useCover)
            {
                await Task.Delay(380);
                Assert(!listen.IsEnabled && !cover.IsEnabled, "fixture did not hold the offline refresh during maintenance");
                PaInvoke(fixture.Window, "EndOfflineMaintenance");
            }
            await Until(() => listen.IsEnabled && cover.IsEnabled && restored.DownloadedPath == path);
            if (entireDirectory) Assert(restored.CoverUri == savedCover, "the file mark set artwork before a verified opening");
            if (renameRoot || replaceRoot)
            {
                Assert(PaField<FileSystemWatcher?>(fixture.Window, "_queueOfflineWatcher") is not null, "returning root did not rearm the file watcher");
                File.Move(path, parkedFile);
                await Until(() => !restored.IsDownloaded && !listen.IsEnabled && !cover.IsEnabled);
                File.Move(parkedFile, path);
                await Until(() => listen.IsEnabled && cover.IsEnabled && restored.DownloadedPath == path);
            }
            SaClick(useCover ? cover : listen);
            await Until(() => fixture.Player.IsPlaying);
            Assert(fixture.Player.SourceLocalPath == path && fixture.Queue.CurrentOccurrenceToken == occurrence && fixture.MediaRequests == 0,
                "the recovered control did not start the exact copy in the existing queue");
            if (entireDirectory)
            {
                await fixture.WaitSave();
                var expectedCover = otherEdition ? null : savedCover ?? PaCover;
                Assert(fixture.Player.SourceVerifiedLocalDigest == exact.Sha256
                    && restored.CoverUri == expectedCover && Artwork.GetSourceUri(Control<Image>(fixture.Window, "PlayerCover")) == expectedCover
                    && new PlaybackStateStore(directory).Load("12345")!.Tracks.Single().CoverUri == expectedCover,
                    "the verified opening did not preserve the correct edition's artwork in the player and checkpoint");
            }
            fixture.Close();
            Assert(PaField<FileSystemWatcher?>(fixture.Window, "_queueOfflineWatcher") is null
                && PaField<System.Windows.Threading.DispatcherTimer?>(fixture.Window, "_queueOfflineRootTimer") is null,
                "closing the queue retained its directory watcher or root poll");
        }
    }

    private static async Task PaReplaceWatchedOfflineRootAsync(Dispatcher dispatcher, string music, string parked, string replacement)
    {
        const int attempts = 40;
        for (var attempt = 1; ; attempt++)
        {
            var renaming = false;
            var operation = $"open '{replacement}' for rename";
            try
            {
                using var staged = UninstallPathGuard.HoldDirectory(replacement, deleteAccess: true);
                operation = $"open '{music}' for rename";
                using var original = UninstallPathGuard.HoldDirectory(music, deleteAccess: true);

                // The watcher must observe the new directory identity without a tick on the absent name.
                using var disabled = dispatcher.DisableProcessing();
                renaming = true;
                operation = $"rename '{music}' to '{parked}'";
                ManagedCacheCleanup.RenameByHandle(original, parked);
                operation = $"publish '{replacement}' as '{music}'";
                ManagedCacheCleanup.RenameByHandle(staged, music);
                return;
            }
            catch (Win32Exception error) when (!renaming && attempt < attempts && error.NativeErrorCode is 5 or 32 or 33)
            { await Task.Delay(50); }
            catch (Win32Exception error)
            { throw new IOException($"Offline root fixture could not {operation} (Win32 {error.NativeErrorCode}, attempt {attempt}).", error); }
        }
    }

    private static async Task PaLateOfflineBinding()
    {
        var directory = PaDirectory("late-offline-binding");
        var music = Directory.CreateDirectory(Path.Combine(directory, "offline")).FullName;
        var data = SilentMp3Seconds(4, 192);
        var digest = Convert.ToHexString(SHA256.HashData(data));
        var first = PaOffline("9244", "late.mp3", PaCover) with
        { Bytes = data.Length, Sha256 = digest, DurationMs = 4000, ConfirmedBitrate = 192 };
        var rejectedBytes = SilentMp3Seconds(4, 320);
        var rejected = first with { FileName = "unverified.mp3", Bitrate = 320, ConfirmedBitrate = 320,
            Bytes = rejectedBytes.Length, Sha256 = Convert.ToHexString(SHA256.HashData(rejectedBytes)) };
        var requested = rejected with { RequestId = "9244:93", AlbumId = "93", FileName = "requested.mp3" };
        var row = new TrackRow { Id = first.Id, RequestId = requested.RequestId, AlbumId = requested.AlbumId,
            Title = "Synthetic saved row", DurationMs = 4000, IsOfflineLibraryRow = true };
        var store = new PlaybackStateStore(directory);
        store.Save(PlaybackStateStore.Capture("12345", PaQueue([row], 0), 1.5));
        var index = Path.Combine(music, "library.index.json");
        var openedPath = Path.Combine(music, first.FileName);
        using (var fixture = new PaFixture(directory, "first-window"))
        {
            await fixture.OpenAndRestore(session: false);
            var restored = fixture.Queue.Current!;
            File.WriteAllBytes(openedPath, data);
            var altered = rejectedBytes.ToArray();
            altered[^1] ^= 1;
            var rejectedPath = Path.Combine(music, rejected.FileName);
            File.WriteAllBytes(rejectedPath, altered);
            File.WriteAllText(index, JsonSerializer.Serialize(new[] { first, rejected }));
            var candidate = (LocalAudioSource?)PaInvoke(fixture.Window, "RefreshLocalPlaybackSource", restored, true);
            Assert(candidate?.Path == rejectedPath && restored.IsDownloaded && restored.PreferredLocalVariant.RequestId is null,
                "a visibility check bound an unopened local variant");
            await (Task)PaInvoke(fixture.Window, "TogglePlaybackAsync")!;
            await fixture.WaitSave();
            var saved = store.Load("12345")!.Tracks.Single();
            Assert(fixture.Player.SourceLocalPath == openedPath && fixture.MediaRequests == 0
                && saved.LocalSourceRequestId == first.RequestId && saved.LocalSourceCodec == first.Codec
                && saved.LocalSourceBitrate == first.Bitrate && saved.LocalSourceConfirmedBitrate == first.ConfirmedBitrate,
                "opening a local variant omitted its exact binding from the checkpoint");
        }
        File.WriteAllBytes(Path.Combine(music, requested.FileName), rejectedBytes);
        File.WriteAllText(index, JsonSerializer.Serialize(new[] { first, requested }));
        using var reopened = new PaFixture(directory, "next-window");
        await reopened.OpenAndRestore(session: false);
        await (Task)PaInvoke(reopened.Window, "TogglePlaybackAsync")!;
        Assert(reopened.Player.SourceLocalPath == openedPath && reopened.MediaRequests == 0,
            "resuming preferred the newly cached requested edition over the previously opened copy");
    }

    private static async Task PaOfflineCheckpointProvenance()
    {
        foreach (var state in new[] { "bound", "missing", "rejected" })
        {
            var directory = PaDirectory("offline-checkpoint-" + state);
            var music = Path.Combine(directory, "offline");
            Directory.CreateDirectory(music);
            var wav = File.ReadAllBytes(PlaybackOptionsSilentWave("restored-offline-edition", 6));
            var digest = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(wav));
            var bound = PaOffline("9240", "bound.mp3", PaCover) with { Bytes = wav.Length, Sha256 = digest, DurationMs = 6000 };
            var other = bound with { RequestId = "9240:92", AlbumId = "92", FileName = "other.mp3", Bitrate = 320 };
            File.WriteAllBytes(Path.Combine(music, other.FileName), wav);
            var localPath = Path.Combine(music, bound.FileName);
            if (state != "missing") File.WriteAllBytes(localPath, wav);
            var items = new[] { other, bound };
            File.WriteAllText(Path.Combine(music, "library.index.json"), JsonSerializer.Serialize(items));
            var row = new TrackRow
            {
                Id = "9240", RequestId = "9240:92", AlbumId = "92", Title = "Saved occurrence", CoverUri = PaCover,
                Available = true, DurationMs = 6000, IsOfflineLibraryRow = true, LocalSourceRequestId = "9240:91",
                LocalPath = localPath, DownloadedPath = localPath
            };
            var store = new PlaybackStateStore(directory);
            store.Save(PlaybackStateStore.Capture("12345", PaQueue([row], 0), 1.5));
            var checkpoint = File.ReadAllText(Path.Combine(directory, PlaybackStateStore.FileName));
            Assert(checkpoint.Contains("9240:91", StringComparison.Ordinal) && !checkpoint.Contains("bound.mp3", StringComparison.Ordinal),
                "the checkpoint did not keep the edition key without a file path");
            using var fixture = new PaFixture(directory, "window");
            await fixture.OpenAndRestore();
            var restored = fixture.Queue.Current!;
            var occurrence = fixture.Queue.CurrentOccurrenceToken;
            Assert(restored.IsOfflineLibraryRow && restored.LocalSourceRequestId == "9240:91"
                && restored.LocalPath == (state == "missing" ? null : localPath), "restoring selected another saved edition");
            if (state == "rejected")
            {
                wav[^1] ^= 1;
                File.WriteAllBytes(localPath, wav);
            }
            await (Task)PaInvoke(fixture.Window, "TogglePlaybackAsync")!;
            var ownsOccurrence = fixture.Queue.CurrentOccurrenceToken == occurrence;
            var openedBoundPath = string.Equals(fixture.Player.SourceLocalPath, localPath, StringComparison.OrdinalIgnoreCase);
            var requestedAudio = fixture.ApiHandler.Seen.Any(request => !request.Uri.AbsolutePath.EndsWith("/lyrics", StringComparison.Ordinal));
            Assert(ownsOccurrence && !requestedAudio && fixture.MediaRequests == 0
                && (state == "bound" ? openedBoundPath : !fixture.Player.HasSource),
                $"offline checkpoint {state}: token={ownsOccurrence}, bound={openedBoundPath}, hasSource={fixture.Player.HasSource}, " +
                $"api={fixture.ApiHandler.Seen.Count}, media={fixture.MediaRequests}, status={Control<TextBlock>(fixture.Window, "StatusText").Text}");
        }
    }

    private static async Task PaForeignCheckpoint()
    {
        foreach (var apply in new[] { false, true })
        {
            var directory = PaDirectory("foreign-idle-checkpoint-" + apply);
            var store = new PlaybackStateStore(directory);
            var path = Path.Combine(directory, PlaybackStateStore.FileName);
            store.Save(PlaybackStateStore.Capture("67890", PaQueue([PaRow("9250", PaCover)], 0), 57));
            var original = File.ReadAllBytes(path);
            using var fixture = new PaFixture(directory, "window");
            await fixture.OpenAndRestore(session: false);
            Assert(fixture.Queue.Items.Count == 0, "the other account's queue was restored");
            PaInvoke(fixture.Window, "Repeat_Click", fixture.Window, new RoutedEventArgs());
            if (apply)
            {
                var feed = Path.Combine(directory, "feed.json");
                File.WriteAllText(feed, "{\"githubRepository\":\"https://github.com/senduora/yamuse\"}");
                var backend = new Qf061A10UpdateBackend();
                var updates = new UpdateSupport(feed, true, _ => backend);
                await updates.CheckAsync();
                Assert(await (Task<bool>)PaInvoke(fixture.Window, "ApplyAppUpdateAsync", updates)! && backend.Applies == 1,
                    "the other account's checkpoint prevented applying a fixture update");
            }
            else
            {
                fixture.Window.CloseSaveFailurePrompt = failure => throw new InvalidOperationException(failure);
                fixture.Window.Close();
                await Until(() => PaField<bool>(fixture.Window, "_closed"));
            }
            Assert(original.SequenceEqual(File.ReadAllBytes(path)) && store.Load("67890") is { PositionSeconds: 57 },
                "an idle account replaced the previous owner's checkpoint");
        }
    }

    private static async Task PaModeCheckpointRetry()
    {
        foreach (var apply in new[] { false, true })
        {
            var directory = PaDirectory("mode-checkpoint-" + apply);
            var store = new PlaybackStateStore(directory);
            var path = Path.Combine(directory, PlaybackStateStore.FileName);
            var rows = new[] { PaRow("9220", PaCover), PaRow("9221", PaCover) };
            store.Save(PlaybackStateStore.Capture("12345", PaQueue(rows, 0), 77.25) with
            {
                Shuffle = apply, Repeat = RepeatMode.One, PlayNextIndices = apply ? [1] : null
            });
            var original = File.ReadAllBytes(path);
            using var fixture = new PaFixture(directory, "window");
            using var held = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.None);
            await fixture.OpenAndRestore(session: false);
            Assert(PaField<bool>(fixture.Window, "_playbackStateRestoreUnavailable") && fixture.Queue.Items.Count == 0,
                "fixture did not hold the saved queue during restore");
            PaInvoke(fixture.Window, apply ? "Repeat_Click" : "Shuffle_Click", fixture.Window, new RoutedEventArgs());

            if (apply)
            {
                var feed = Path.Combine(directory, "feed.json");
                File.WriteAllText(feed, "{\"githubRepository\":\"https://github.com/senduora/yamuse\"}");
                var backend = new Qf061A10UpdateBackend();
                var updates = new UpdateSupport(feed, true, _ => backend);
                await updates.CheckAsync();
                Task<bool> Apply() => (Task<bool>)PaInvoke(fixture.Window, "ApplyAppUpdateAsync", updates)!;
                Assert(!await Apply() && backend.Applies == 0, "apply did not wait for the original checkpoint");
                held.Dispose();
                Assert(original.SequenceEqual(File.ReadAllBytes(path)) && await Apply() && backend.Applies == 1,
                    "apply did not commit the recovered queue with the new repeat mode");
            }
            else
            {
                var prompts = 0;
                fixture.Window.CloseSaveFailurePrompt = _ =>
                {
                    Assert(++prompts == 1, "close still failed after the saved queue was released");
                    held.Dispose();
                    Assert(original.SequenceEqual(File.ReadAllBytes(path)), "close had already overwritten the held checkpoint");
                    return MessageBoxResult.Yes;
                };
                fixture.Window.Close();
                await Until(() => PaField<bool>(fixture.Window, "_closed"));
                Assert(prompts == 1, "close did not wait for the original checkpoint");
            }
            var saved = store.Load("12345");
            Assert(saved is { PositionSeconds: 77.25, CurrentIndex: 0, Shuffle: true }
                && saved.Repeat == (apply ? RepeatMode.All : RepeatMode.One)
                && saved.Tracks.Select(x => x.Id).SequenceEqual(rows.Select(x => x.Id))
                && (apply ? saved.PlayNextIndices?.SequenceEqual(new[] { 1 }) == true : saved.PlayNextIndices is null)
                && fixture.MediaRequests == 0, "changing a mode lost the saved queue, position, or untouched mode");
        }
    }

    private static async Task PaDeferredArtworkAfterExit()
    {
        foreach (var scenario in new[] { "close-cancel", "close-replaced", "close-account", "close-canceled-account", "close-success",
            "apply-error", "apply-deferred", "apply-success" })
        {
            var directory = PaDirectory("deferred-exit-artwork-" + scenario);
            var store = new PlaybackStateStore(directory);
            var path = Path.Combine(directory, PlaybackStateStore.FileName);
            var rows = new[] { PaRow("9250"), PaRow("9251") };
            store.Save(PlaybackStateStore.Capture("12345", PaQueue(rows, 0), 77.25) with { PlayNextIndices = [1] });
            var feed = Path.Combine(directory, "feed.json");
            File.WriteAllText(feed, "{\"githubRepository\":\"https://github.com/senduora/yamuse\"}");
            var backend = new Qf061A10UpdateBackend { FailApply = scenario == "apply-error" };
            var updates = new UpdateSupport(feed, true, _ => backend);
            using var fixture = new PaFixture(directory, "window", async (request, token) => PaMetadata(await PaRequestedIds(request, token)));
            PaSet(fixture.Window, "_appUpdatesOverride", updates);
            using (var held = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                await fixture.OpenAndRestore();
                Assert(PaField<bool>(fixture.Window, "_playbackStateRestoreUnavailable") && fixture.Queue.Items.Count == 0,
                    "fixture did not make the startup checkpoint temporarily unreadable");
            }

            var closing = scenario.StartsWith("close-", StringComparison.Ordinal);
            var realExit = scenario.EndsWith("-success", StringComparison.Ordinal);
            var blockSort = closing && !realExit || scenario == "apply-deferred";
            var sortDirectory = Path.Combine(directory, "locked-sort");
            var sorts = new ListSortStore(sortDirectory);
            var changes = PaField<Dictionary<string, ListSortRule>>(fixture.Window, "_unsavedListSorts");
            if (blockSort)
            {
                sorts.SaveChanges(new Dictionary<string, ListSortRule> { ["tracks:queue"] = new(ListSortField.Title) });
                PaSet(fixture.Window, "_listSortStore", sorts);
                changes["tracks:queue"] = new(ListSortField.Artist);
            }
            using (var sortLock = blockSort ? File.Open(Path.Combine(sortDirectory, "list-sorting.json"), FileMode.Open, FileAccess.Read, FileShare.None) : null)
            {
                if (closing)
                {
                    var prompted = false;
                    fixture.Window.CloseSaveFailurePrompt = failure =>
                    {
                        Assert(!realExit && !prompted && failure.Contains("сортировку", StringComparison.Ordinal)
                            && fixture.Queue.Items.Count == 2 && fixture.ApiHandler.Seen.Count == 0,
                            "exit preparation did not restore the queue silently before asking about the saved sort");
                        prompted = true;
                        if (scenario == "close-replaced") fixture.Queue.RestoreItems([PaRow("9260")], 0, true, RepeatMode.One);
                        if (scenario == "close-account")
                        {
                            PaSet(fixture.Window, "_account", new Account("67890", "Different fixture account", true));
                            PaSet(fixture.Window, "_accountGeneration", PaField<int>(fixture.Window, "_accountGeneration") + 1);
                        }
                        if (scenario == "close-canceled-account") PaField<CancellationTokenSource>(fixture.Window, "_accountCts").Cancel();
                        return MessageBoxResult.Cancel;
                    };
                    fixture.Window.Close();
                    if (realExit) await Until(() => PaField<bool>(fixture.Window, "_closed"));
                    else await Until(() => prompted && !PaField<bool>(fixture.Window, "_closeCommitInProgress"));
                    fixture.Window.CloseSaveFailurePrompt = null;
                }
                else
                {
                    await updates.CheckAsync();
                    Task<bool> Apply() => (Task<bool>)PaInvoke(fixture.Window, "ApplyAppUpdateAsync", updates)!;
                    if (scenario == "apply-error") await Throws<InvalidOperationException>(() => Apply());
                    else Assert(await Apply() == realExit, "fake Apply did not take the expected completion or deferral branch");
                    Assert(backend.Applies == (scenario == "apply-deferred" ? 0 : 1), "the update handoff ran before the required checkpoint");
                }
            }
            changes.Clear();

            var resumes = scenario is "close-cancel" or "apply-error" or "apply-deferred";
            if (!resumes)
            {
                Assert(fixture.ApiHandler.Seen.Count == 0 && PaField<Task?>(fixture.Window, "_restoredArtworkTask") is null
                    && PaField<object?>(fixture.Window, "_deferredRestoredArtwork") is null && fixture.MediaRequests == 0,
                    "artwork started or remained pending after exit or loss of its owner");
                continue;
            }

            var current = fixture.Queue.Current;
            var occurrence = fixture.Queue.CurrentOccurrenceToken;
            var task = PaField<Task?>(fixture.Window, "_restoredArtworkTask");
            PaInvoke(fixture.Window, "ResumeDeferredRestoredArtwork");
            Assert(task is not null && ReferenceEquals(task, PaField<Task?>(fixture.Window, "_restoredArtworkTask")),
                "releasing the exit admission did not start hydration exactly once");
            await fixture.WaitArtwork(); await fixture.WaitSave();
            var saved = store.Load("12345");
            Assert(fixture.ApiHandler.Seen.Count == 1 && ReferenceEquals(fixture.Queue.Current, current) && fixture.Queue.CurrentOccurrenceToken == occurrence
                && fixture.Queue.Items.All(row => row.CoverUri == PaCover)
                && saved is { CurrentIndex: 0, PositionSeconds: 77.25, Shuffle: true, Repeat: RepeatMode.One }
                && saved.PlayNextIndices?.SequenceEqual([1]) == true && saved.Tracks.All(row => row.CoverUri == PaCover),
                "resumed artwork changed the restored occurrence, playback modes, or saved position");
            PaAssertPaused(fixture, 2, 0);
        }
    }

    private static async Task PaArtworkRefreshAfterCanceledClose()
    {
        var directory = PaDirectory("paused-artwork-refresh-on-close");
        var store = new PlaybackStateStore(directory);
        var rows = Enumerable.Range(0, 101).Select(index => PaRow((10700 + index).ToString())).ToArray();
        store.Save(PlaybackStateStore.Capture("12345", PaQueue(rows, 0), 77.25));
        var preferences = new PreferencesStore(Path.Combine(directory, "window", "preferences"));
        preferences.Save(new PlayerPreferences());
        var firstPage = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releasePage = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var latest = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var batches = new List<string[]>();
        var rotations = 0;
        using var fixture = new PaFixture(directory, "window", async (request, token) =>
        {
            if (request.RequestUri?.Host == "oauth.yandex.ru")
            {
                Assert(request.RequestUri.AbsolutePath == "/token", "artwork used a different OAuth route");
                Interlocked.Increment(ref rotations);
                return Json(JsonSerializer.Serialize(new { access_token = FakeSession.AccessToken, refresh_token = "fixture-paused-artwork-refresh", expires_in = 3600 }));
            }
            var ids = await PaRequestedIds(request, token);
            batches.Add(ids);
            if (batches.Count == 1)
            {
                firstPage.TrySetResult();
                await releasePage.Task.WaitAsync(token);
            }
            return PaMetadata(ids);
        });
        PaSet(fixture.Window, "_appUpdatesOverride", new UpdateSupport(Path.Combine(directory, "no-feed.json"), true, _ => new Qf061A10UpdateBackend()));
        try
        {
            await fixture.OpenAndRestore(); await firstPage.Task.WaitAsync(TimeSpan.FromSeconds(5));
            PaSet(fixture.Window, "_session", FakeSession with { ExpiresAtUtc = DateTimeOffset.UtcNow.AddSeconds(15) });
            PaSet(fixture.Window, "_updateCheckCompletion", latest);
            PaSet(fixture.Window, "_updateCheckExitTimeout", TimeSpan.FromSeconds(10));
            var originalTask = PaField<Task>(fixture.Window, "_restoredArtworkTask");
            var occurrence = fixture.Queue.CurrentOccurrenceToken;
            var prompts = 0;
            fixture.Window.CloseSaveFailurePrompt = _ => { prompts++; return MessageBoxResult.Cancel; };
            using (var held = File.Open(preferences.FilePath, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                fixture.Window.Close();
                await Until(() => PaField<bool>(fixture.Window, "_closeCommitInProgress"));
                releasePage.SetResult();
                await Until(() => PaField<TaskCompletionSource?>(fixture.Window, "_playbackRestoreAdmission") is not null || originalTask.IsCompleted);
                Assert(!originalTask.IsCompleted && rotations == 0 && batches.Count == 1
                    && fixture.Queue.Items.Take(100).All(row => row.CoverUri == PaCover) && fixture.Queue.Items[100].CoverUri is null,
                    "closed admission consumed the late page or sent a refresh instead of preserving accepted covers");
                latest.SetResult();
                await Until(() => prompts != 0 && !PaField<bool>(fixture.Window, "_closeCommitInProgress"));
            }
            fixture.Window.CloseSaveFailurePrompt = null;
            await fixture.WaitArtwork(); await fixture.WaitSave();
            Assert(prompts == 1 && rotations == 1 && batches.Select(batch => batch.Length).SequenceEqual([100, 1])
                && ReferenceEquals(originalTask, PaField<Task>(fixture.Window, "_restoredArtworkTask"))
                && fixture.Queue.CurrentOccurrenceToken == occurrence && fixture.Queue.Items.All(row => row.CoverUri == PaCover)
                && store.Load("12345") is { CurrentIndex: 0, PositionSeconds: 77.25 } saved && saved.Tracks.All(row => row.CoverUri == PaCover),
                "canceled close did not resume exactly the remaining metadata page and persist its cover");
            PaAssertPaused(fixture, 101, 0);
        }
        finally
        {
            releasePage.TrySetResult(); latest.TrySetResult();
            fixture.Window.CloseSaveFailurePrompt = null;
        }
    }

    private static async Task PaArtworkAfterUninstallHandoff()
    {
        foreach (var mode in new[] { "paused-failure", "new-failure", "closed" })
        {
            var newRestore = mode == "new-failure";
            var closes = mode == "closed";
            var directory = PaDirectory("artwork-after-fake-uninstall-" + mode);
            var store = new PlaybackStateStore(directory);
            var rows = Enumerable.Range(0, newRestore ? 1 : 101).Select(index => PaRow((11000 + index).ToString())).ToArray();
            store.Save(PlaybackStateStore.Capture("12345", PaQueue(rows, 0), 77.25));
            var pageEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var pageRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var admittedSave = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var batches = new List<int>();
            using var fixture = new PaFixture(directory, "window", async (request, token) =>
            {
                var ids = await PaRequestedIds(request, token);
                batches.Add(ids.Length);
                if (!newRestore && batches.Count == 1) { pageEntered.SetResult(); await pageRelease.Task.WaitAsync(token); }
                return PaMetadata(ids);
            });
            PaSet(fixture.Window, "_appUpdatesOverride", new UpdateSupport(Path.Combine(directory, "no-feed.json"), true, _ => new Qf061A10UpdateBackend()));
            try
            {
                using (newRestore ? File.Open(Path.Combine(directory, PlaybackStateStore.FileName), FileMode.Open, FileAccess.Read, FileShare.None) : null)
                    await fixture.OpenAndRestore();
                if (!newRestore) await pageEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                var original = PaField<Task?>(fixture.Window, "_restoredArtworkTask");
                PaSet(fixture.Window, "_fullUninstallCommitting", true);
                PaSet(fixture.Window, "_favoritesCacheSaveTask", admittedSave.Task);
                var handoff = fixture.Window.StartFullUninstallHandoffAsync(() =>
                {
                    if (newRestore)
                        Assert(fixture.Queue.Items.Count == 1 && PaField<object?>(fixture.Window, "_deferredRestoredArtwork") is not null
                            && batches.Count == 0, "fake handoff did not defer artwork for the first recovered queue");
                    if (!closes) throw new IOException("fixture-only handoff refusal");
                });
                if (!newRestore)
                {
                    pageRelease.SetResult();
                    await Until(() => PaField<TaskCompletionSource?>(fixture.Window, "_playbackRestoreAdmission") is not null || original!.IsCompleted);
                    Assert(!handoff.IsCompleted && !original!.IsCompleted && batches.SequenceEqual([100]),
                        "handoff did not hold the already active artwork and admitted write");
                }
                admittedSave.SetResult();
                if (closes)
                {
                    await handoff.WaitAsync(TimeSpan.FromSeconds(5));
                    PaSet(fixture.Window, "_fullUninstallCanClose", true);
                    fixture.Close();
                    Assert(PaField<bool>(fixture.Window, "_closed"), "successful fake handoff did not close the fixture");
                }
                else await Throws<IOException>(() => handoff.WaitAsync(TimeSpan.FromSeconds(5)));
                PaInvoke(fixture.Window, "FinishFullUninstallCommit");
                if (closes)
                {
                    await original!.WaitAsync(TimeSpan.FromSeconds(5));
                    Assert(batches.SequenceEqual([100]), "successful handoff requested another artwork page");
                    continue;
                }
                var occurrence = fixture.Queue.CurrentOccurrenceToken;
                await fixture.WaitArtwork(); await fixture.WaitSave();
                Assert((newRestore || ReferenceEquals(original, PaField<Task>(fixture.Window, "_restoredArtworkTask")))
                    && batches.SequenceEqual(newRestore ? [1] : [100, 1]) && occurrence == fixture.Queue.CurrentOccurrenceToken
                    && store.Load("12345") is { PositionSeconds: 77.25 } saved && saved.Tracks.All(row => row.CoverUri == PaCover),
                    "refused handoff did not resume and persist the exact pending artwork once");
                PaAssertPaused(fixture, rows.Length, 0);
            }
            finally
            {
                pageRelease.TrySetResult(); admittedSave.TrySetResult();
                PaInvoke(fixture.Window, "FinishFullUninstallCommit");
            }
        }
    }

    private static async Task PaArtworkFinalPageAfterCanceledClose()
    {
        var directory = PaDirectory("artwork-final-page-during-close");
        var store = new PlaybackStateStore(directory);
        store.Save(PlaybackStateStore.Capture("12345", PaQueue([PaRow("11150")], 0), 77.25));
        var preferences = new PreferencesStore(Path.Combine(directory, "window", "preferences"));
        preferences.Save(new PlayerPreferences());
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var fixture = new PaFixture(directory, "window", async (request, token) =>
        {
            var ids = await PaRequestedIds(request, token);
            entered.SetResult(); await release.Task.WaitAsync(token);
            return PaMetadata(ids);
        });
        PaSet(fixture.Window, "_appUpdatesOverride", new UpdateSupport(Path.Combine(directory, "no-feed.json"), true, _ => new Qf061A10UpdateBackend()));
        try
        {
            await fixture.OpenAndRestore(); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var task = PaField<Task>(fixture.Window, "_restoredArtworkTask");
            var occurrence = fixture.Queue.CurrentOccurrenceToken;
            var prompted = false;
            fixture.Window.CloseSaveFailurePrompt = _ =>
            {
                Assert(store.Load("12345")?.Tracks.Single().CoverUri is null, "close checkpoint already contained the held cover");
                prompted = true;
                var frame = new DispatcherFrame();
                Exception? error = null;
                fixture.Window.Dispatcher.BeginInvoke(new Action(async () =>
                {
                    try
                    {
                        release.SetResult();
                        await Until(() => PaField<TaskCompletionSource?>(fixture.Window, "_playbackRestoreAdmission") is not null || task.IsCompleted);
                        Assert(!task.IsCompleted && fixture.Queue.Current?.CoverUri == PaCover
                            && store.Load("12345")?.Tracks.Single().CoverUri is null, "last page did not wait to persist the accepted cover");
                    }
                    catch (Exception failure) { error = failure; }
                    finally { frame.Continue = false; }
                }));
                Dispatcher.PushFrame(frame);
                if (error is not null) throw error;
                return MessageBoxResult.Cancel;
            };
            using (File.Open(preferences.FilePath, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                fixture.Window.Close();
                await Until(() => prompted && !PaField<bool>(fixture.Window, "_closeCommitInProgress"));
            }
            await fixture.WaitArtwork(); await fixture.WaitSave();
            Assert(fixture.ApiHandler.Seen.Count == 1 && fixture.Queue.CurrentOccurrenceToken == occurrence
                && store.Load("12345") is { PositionSeconds: 77.25 } saved && saved.Tracks.Single().CoverUri == PaCover,
                "canceling Close lost the accepted final cover or issued another metadata request");
            PaAssertPaused(fixture, 1, 0);
        }
        finally { release.TrySetResult(); fixture.Window.CloseSaveFailurePrompt = null; }
    }

    private static async Task PaJournalRestoreAfterCanceledClose()
    {
        foreach (var mode in new[] { "unchanged", "edited-during-close", "edited-before-retry" })
        {
            var directory = PaDirectory("journal-restored-after-cancel-" + mode);
            var store = new PlaybackStateStore(directory);
            store.Save(PlaybackStateStore.Capture("12345", PaQueue([PaRow("10730"), PaRow("10731")], 1), 77.25));
            var music = Directory.CreateDirectory(Path.Combine(directory, "offline")).FullName;
            File.WriteAllText(Path.Combine(music, "library.index.json"), "[]");
            using var fixture = new PaFixture(directory, "window", async (request, token) => PaMetadata(await PaRequestedIds(request, token)));
            PaSet(fixture.Window, "_appUpdatesOverride", new UpdateSupport(Path.Combine(directory, "no-feed.json"), true, _ => new Qf061A10UpdateBackend()));
            var journal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var offline = PaField<OfflineLibrary>(fixture.Window, "_offline");
            var state = typeof(OfflineLibrary).GetField("_directoryState", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(offline)!;
            state.GetType().GetField("RecoveryTask", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.SetValue(state, journal.Task);
            await fixture.OpenAndRestore();
            Assert(PaField<bool>(fixture.Window, "_playbackStateRestoreUnavailable") && fixture.Queue.Items.Count == 0,
                "fixture did not leave a cold queue waiting for the offline journal");
            if (mode == "edited-before-retry") fixture.Queue.RestoreItems([PaRow("10732")], 0, true, RepeatMode.One);
            var recovery = (Task)PaInvoke(fixture.Window, "RetryPlaybackRestoreAfterRecoveryAsync")!;
            Assert(!recovery.IsCompleted, "fixture did not enter the asynchronous journal retry");
            if (mode == "edited-before-retry")
            {
                journal.SetResult();
                await recovery.WaitAsync(TimeSpan.FromSeconds(5));
                Assert(fixture.Queue.Items.Single().Id == "10732" && fixture.ApiHandler.Seen.Count == 0,
                    "finishing the journal replaced a queue edited before the retry took ownership");
                continue;
            }
            var prompted = false;
            fixture.Window.CloseSaveFailurePrompt = failure =>
            {
                Assert(!prompted && failure.Contains("очередь воспроизведения", StringComparison.Ordinal) && fixture.Queue.Items.Count == 0,
                    "close did not observe the still-blocked queue before offering cancellation");
                prompted = true;
                var frame = new DispatcherFrame();
                Exception? waiterFailure = null;
                fixture.Window.Dispatcher.BeginInvoke(new Action(async () =>
                {
                    try
                    {
                        journal.SetResult();
                        await Until(() => PaField<TaskCompletionSource?>(fixture.Window, "_playbackRestoreAdmission") is not null || recovery.IsCompleted);
                        Assert(!recovery.IsCompleted && fixture.Queue.Items.Count == 0 && fixture.ApiHandler.Seen.Count == 0,
                            "a completed journal lost its retry or changed the queue before readmission");
                        if (mode == "edited-during-close") fixture.Queue.RestoreItems([PaRow("10732")], 0, true, RepeatMode.One);
                    }
                    catch (Exception error) { waiterFailure = error; }
                    finally { frame.Continue = false; }
                }));
                Dispatcher.PushFrame(frame);
                if (waiterFailure is not null) throw waiterFailure;
                return MessageBoxResult.Cancel;
            };
            try
            {
                fixture.Window.Close();
                await recovery.WaitAsync(TimeSpan.FromSeconds(8));
                Assert(prompted && !PaField<bool>(fixture.Window, "_closeCommitInProgress"), "journal retry resumed before close released admission");
                if (mode == "edited-during-close")
                {
                    Assert(fixture.Queue.Items.Single().Id == "10732" && fixture.ApiHandler.Seen.Count == 0,
                        "late journal retry replaced the user's newer queue");
                    continue;
                }
                await fixture.WaitArtwork(); await fixture.WaitSave();
                PaAssertPaused(fixture, 2, 1);
                Assert(fixture.ApiHandler.Seen.Count == 1 && fixture.Queue.Items.All(row => row.CoverUri == PaCover)
                    && store.Load("12345") is { CurrentIndex: 1, PositionSeconds: 77.25 } saved && saved.Tracks.All(row => row.CoverUri == PaCover),
                    "close cancellation did not finish the paused journal retry, position, and cover hydration");
            }
            finally { journal.TrySetResult(); fixture.Window.CloseSaveFailurePrompt = null; }
        }
    }

    private static async Task PaUnobservedLogoutRetry()
    {
        foreach (var owner in new[] { "12345", "67890" })
        {
            var directory = PaDirectory("unobserved-logout-" + owner);
            var store = new PlaybackStateStore(directory);
            var path = Path.Combine(directory, PlaybackStateStore.FileName);
            store.Save(PlaybackStateStore.Capture(owner, PaQueue([PaRow("9230", PaCover)], 0), 38));
            var original = File.ReadAllBytes(path);
            using var fixture = new PaFixture(directory, "window");
            using (UninstallPathGuard.HoldDirectory(directory, deleteAccess: true))
            {
                await fixture.OpenAndRestore(session: false);
                Assert(PaField<bool>(fixture.Window, "_playbackStateRestoreUnavailable"), "fixture did not block the first read");
                fixture.Window.SignOut();
                Assert(PaField<PlaybackStateStore.ClearRetry?>(fixture.Window, "_pendingPlaybackStateClear") is { File: null },
                    "fixture did not create a pending clear without a checked file");
            }
            var automaticallyCleared = (bool)PaInvoke(fixture.Window, "TryFlushPendingPlaybackStateClear")!;
            Assert(automaticallyCleared == (owner != "12345") && original.SequenceEqual(File.ReadAllBytes(path)),
                "the automatic retry touched an unobserved checkpoint or failed to recognize another owner");
            PaSet(fixture.Window, "_account", new Account("12345", "Fixture Account", true));
            fixture.Window.SignOut();
            Assert(PaField<PlaybackStateStore.ClearRetry?>(fixture.Window, "_pendingPlaybackStateClear") is null
                && (owner == "12345" ? !File.Exists(path) : original.SequenceEqual(File.ReadAllBytes(path))),
                "the fresh verified logout did not resolve its own checkpoint or preserve another owner's file");
        }
    }

    private static async Task PaUnpreparedCloseFlushesSession()
    {
        var directory = PaDirectory("unprepared-close-session");
        var sessionDirectory = Path.Combine(directory, "window", "session");
        var sessions = new SessionStore(sessionDirectory);
        sessions.Save(FakeSession);
        using var fixture = new PaFixture(directory, "window");
        await fixture.OpenAndRestore();
        var observed = sessions.Load() ?? throw new InvalidOperationException("fixture has no saved session");
        Assert(observed == FakeSession, "fixture did not establish the original stored login");
        PaSet(fixture.Window, "_ownedStoredSession", observed);
        var renewed = observed with { AccessToken = "fixture-only-renewed-session" };
        PaSet(fixture.Window, "_session", renewed);
        using (var locked = File.Open(Path.Combine(sessionDirectory, "session.dpapi"), FileMode.Open, FileAccess.Read, FileShare.Read))
            Assert(PaInvoke(fixture.Window, "TrySaveSession", renewed, observed) is false, "fixture did not create a pending session write");
        Assert(sessions.Load() == observed, "the blocked session write changed the original stored login");
        // This fixture has no close coordinator. Its Closed callback matches an
        // unprepared system exit without invoking a recovery dialog or network.
        fixture.Close();
        Assert(PaField<bool>(fixture.Window, "_closed") && sessions.Load() == renewed && fixture.ApiHandler.Seen.Count == 0,
            "system close lost the pending session or requested network work");
    }

    private static async Task PaUnknownDuration()
    {
        var directory = PaDirectory("unknown-duration"); var store = new PlaybackStateStore(directory);
        var music = Path.Combine(directory, "offline"); Directory.CreateDirectory(music);
        var file = Path.Combine(music, "unknown.mp3");
        File.Copy(PlaybackOptionsSilentWave("resume-unknown-duration", 6), file);
        var bytes = File.ReadAllBytes(file);
        var item = PaOffline("9103", "unknown.mp3", PaCover) with
        {
            DurationMs = 0, Bytes = bytes.Length, Sha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes))
        };
        File.WriteAllText(Path.Combine(music, "library.index.json"), JsonSerializer.Serialize(new[] { item }));
        store.Save(PlaybackStateStore.Capture("12345", PaQueue([PaRow("9103", PaCover, durationMs: 0)], 0), 0));
        using (var fixture = new PaFixture(directory, "before"))
        {
            await fixture.OpenAndRestore(session: false);
            await fixture.Window.PlayTrackAsync(fixture.Queue.Current!);
            fixture.Player.Pause();
            PaInvoke(fixture.Window, "CommitSeek", 2.25d);
            fixture.Close();
        }
        Assert(store.Load("12345") is { PositionSeconds: > 2 and < 3 }, "metadata duration zero erased the native seek position");
        using var reopened = new PaFixture(directory, "after");
        await reopened.OpenAndRestore(session: false);
        Assert(!reopened.Player.HasSource && Control<TextBlock>(reopened.Window, "PositionText").Text == "0:02",
            "unknown-duration track did not restore its paused position");
        await reopened.Window.PlayTrackAsync(reopened.Queue.Current!);
        await Until(() => reopened.Player.Position.TotalSeconds > 2);
        Assert(reopened.Player.IsPlaying && reopened.MediaRequests == 0, "explicit resume did not seek the indexed audio");
    }

    private static async Task PaFailure()
    {
        var directory = PaDirectory("optional-error");
        new PlaybackStateStore(directory).Save(PlaybackStateStore.Capture("12345", PaQueue([PaRow("9201"), PaRow("9201")], 1), 77.25));
        using var fixture = new PaFixture(directory, "window", (_, _) => Task.FromResult(Json("fixture service unavailable", HttpStatusCode.ServiceUnavailable)));
        await fixture.OpenAndRestore(); var original = fixture.Queue.Items.ToArray(); await fixture.WaitArtwork();
        PaAssertPaused(fixture, 2, 1);
        Assert(fixture.Queue.Items.Zip(original).All(pair => ReferenceEquals(pair.First, pair.Second)) && original.All(row => row.CoverUri is null)
            && PaField<Diagnostics>(fixture.Window, "_diagnostics").LastErrorType?.Contains("Artwork", StringComparison.OrdinalIgnoreCase) == true,
            "optional artwork failure lost the restored queue or was not diagnosed");
        Assert(fixture.ApiHandler.Seen.Select(request => request.Uri.AbsolutePath)
            .SequenceEqual(["/tracks", "/tracks", "/albums/91/with-tracks", "/albums/91/with-tracks"]) && fixture.ArtworkRequests == 0,
            "the track and exact album routes did not each respect the one-retry budget");
    }

    private static string PaDirectory(string name)
    { var path = Path.Combine(_directory, "playback-artwork", name); Directory.CreateDirectory(path); return path; }
    private static TrackRow PaRow(string id, string? cover = null, long durationMs = 180000, string albumId = "91") => new()
    {
        Id = id, RequestId = id + ":" + albumId, AlbumId = albumId, Title = "Saved " + id, Artist = "Saved artist", Album = "Saved album",
        Artists = [new("301", "Saved artist")], Available = true, DurationMs = durationMs, CoverUri = cover
    };
    private static PlaybackQueue PaQueue(IEnumerable<TrackRow> rows, int current)
    { var queue = new PlaybackQueue(); queue.RestoreItems(rows.ToArray(), current, true, RepeatMode.One); return queue; }
    private static OfflineItem PaOffline(string id, string filename, string? cover) => new(id, id + ":91", "Indexed " + id, "Indexed artist", "Indexed album", 180000,
        filename, "mp3", 192, 5, new string('a', 64), DateTimeOffset.UtcNow, cover, [new("301", "Indexed artist")], "91");
    private static T PaField<T>(MainWindow window, string name) => (T)typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
    private static void PaSet(MainWindow window, string name, object? value) => typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, value);
    private static object? PaInvoke(MainWindow window, string name, params object?[] args) => typeof(MainWindow).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, args);
    private static IEnumerable<T> PaVisuals<T>(DependencyObject root) where T : DependencyObject
    { if (root is T typed) yield return typed; for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) foreach (var child in PaVisuals<T>(VisualTreeHelper.GetChild(root, i))) yield return child; }
    private static Image[] PaQueueImages(MainWindow window) => PaVisuals<Image>(Control<ListView>(window, "TracksList")).Where(image => image.DataContext is TrackRow && image.Width == 34).ToArray();
    private static async Task PaImagesReady(PaFixture fixture, int expectedCount)
    {
        await Until(() => Artwork.GetLoadStatus(Control<Image>(fixture.Window, "PlayerCover")) == ArtworkLoadStatus.Ready
            && PaQueueImages(fixture.Window).Length == expectedCount && PaQueueImages(fixture.Window).All(image => Artwork.GetLoadStatus(image) == ArtworkLoadStatus.Ready));
        Assert(Control<Image>(fixture.Window, "PlayerCover").Source is BitmapSource { IsFrozen: true }
            && PaQueueImages(fixture.Window).All(image => image.Source is BitmapSource { IsFrozen: true }), "restored XAML images are not decoded frozen sources");
    }
    private static void PaAssertPaused(PaFixture fixture, int count, int current)
    {
        Assert(fixture.Queue.Items.Count == count && fixture.Queue.CurrentIndex == current && fixture.Queue.Shuffle && fixture.Queue.Repeat == RepeatMode.One
            && ReferenceEquals(PaField<TrackRow?>(fixture.Window, "_current"), fixture.Queue.Current), "artwork changed the queue, occurrence or modes");
        Assert(!fixture.Player.HasSource && !fixture.Player.IsPlaying && fixture.MediaRequests == 0 && fixture.Queue.Items.All(row => !row.IsPlaying)
            && Control<TextBlock>(fixture.Window, "PositionText").Text == "1:17", "artwork restore opened audio, autoplayed, or changed saved position");
    }
    private static async Task<string[]> PaRequestedIds(HttpRequestMessage request, CancellationToken token)
    {
        Assert(request.RequestUri?.Host == "api.music.yandex.net" && request.RequestUri.AbsolutePath == "/tracks" && request.Method == HttpMethod.Post,
            "artwork hydration requested a route other than track metadata");
        Assert(request.Headers.Authorization?.ToString() == "OAuth " + FakeSession.AccessToken && !request.Headers.Contains("Cookie"), "metadata request lost the fake music grant contract");
        var fields = (await request.Content!.ReadAsStringAsync(token)).Split('&').Select(field => field.Split('=', 2)).ToDictionary(pair => pair[0], pair => Uri.UnescapeDataString(pair[1]));
        return fields["track-ids"].Split(',');
    }
    private static HttpResponseMessage PaMetadata(string[] ids) => Json(JsonSerializer.Serialize(new
    { result = ids.Reverse().Select(request => new { id = request.Split(':')[0], title = "Remote replacement must not be used", coverUri = PaCover,
        durationMs = 999, available = true, albums = new[] { new { id = request.Split(':')[1], title = "Remote album" } } }) }));

    private sealed class PaFixture : IDisposable
    {
        internal MainWindow Window { get; }
        internal NativePlayer Player { get; }
        internal PlaybackQueue Queue => PaField<PlaybackQueue>(Window, "_playbackQueue");
        internal FakeHandler ApiHandler { get; }
        internal int MediaRequests, ArtworkRequests;
        private readonly object? _oldArtwork;
        private readonly ArtworkCache _artwork;
        private bool _closed;
        internal PaFixture(string directory, string instance, Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? metadata = null)
        {
            var local = Path.Combine(directory, instance); Directory.CreateDirectory(local);
            _oldArtwork = typeof(Artwork).GetField("_cache", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null);
            var png = MediaCoverPng();
            _artwork = new ArtworkCache(Path.Combine(local, "artwork"), new FakeHandler((request, _) =>
            {
                Assert(ArtworkUri.Valid(request.RequestUri!) && request.Headers.Authorization is null && !request.Headers.Contains("Cookie"), "restored artwork left the anonymous CDN contract");
                Interlocked.Increment(ref ArtworkRequests);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(png) });
            }));
            Artwork.Cache = _artwork;
            ApiHandler = new FakeHandler(metadata ?? ((_, _) => throw new InvalidOperationException("saved artwork unexpectedly requested metadata")));
            var noMedia = new FakeHandler((_, _) => { Interlocked.Increment(ref MediaRequests); throw new InvalidOperationException("artwork restore requested audio"); });
            Player = new NativePlayer(new AudioBuffer(Path.Combine(local, "audio"), noMedia));
            var offline = new OfflineLibrary(Path.Combine(directory, "offline"), new AudioBuffer(Path.Combine(local, "downloads"), noMedia));
            Window = new MainWindow(api: new MusicApi(ApiHandler), store: new SessionStore(Path.Combine(local, "session")), restore: false,
                diagnosticsDirectory: directory, offline: offline, player: Player, preferences: new PreferencesStore(Path.Combine(local, "preferences")))
            { Width = 1280, Height = 860, Left = -16000, Top = 0, ShowActivated = false, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual };
        }
        internal async Task OpenAndRestore(bool session = true)
        {
            var rendered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Window.ContentRendered += (_, _) => rendered.TrySetResult();
            Window.Show(); await rendered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Control<Slider>(Window, "VolumeSlider").Value = 0;
            // OnReady has completed with restore:false: do not start the product
            // updater or read the real profile merely to exercise persistence.
            PaSet(Window, "_restore", true); PaSet(Window, "_account", new Account("12345", "Fixture Account", true));
            PaSet(Window, "_session", session ? FakeSession : null);
            PaInvoke(Window, "InitializePlaybackPersistence"); PaInvoke(Window, "RestorePlaybackState", "12345");
        }
        internal async Task WaitArtwork()
        {
            var pending = PaField<Task?>(Window, "_restoredArtworkTask"); Assert(pending is not null, "legacy artwork hydration task was not retained for completion");
            await pending!.WaitAsync(TimeSpan.FromSeconds(10));
            await Window.Dispatcher.InvokeAsync(() => Window.UpdateLayout(), DispatcherPriority.Background);
        }
        internal async Task WaitSave()
        { if (PaField<Task?>(Window, "_playbackStateWriteTask") is { } pending) await pending.WaitAsync(TimeSpan.FromSeconds(5)); }
        internal void Close() { if (_closed) return; _closed = true; Window.Close(); }
        public void Dispose()
        {
            Close(); _artwork.Dispose();
            typeof(Artwork).GetField("_cache", BindingFlags.NonPublic | BindingFlags.Static)!.SetValue(null, _oldArtwork);
        }
    }
}
