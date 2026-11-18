using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
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
        await Run("PlaybackArtwork: offline audio from another edition leaves the exact cover for hydration", PaDifferentOfflineAlbum);
        await Run("PlaybackArtwork: optional covers cannot crowd out a valid queue or resume position", PaArtworkBudget);
        await Run("PlaybackArtwork: untrusted persisted artwork is rejected and capture never saves signed locations", PaUnsafe);
        await Run("PlaybackArtwork: missing-only artwork updates notify existing row bindings once", PaNotifications);
        await Run("PlaybackArtwork: cold new windows render saved player and queue covers while paused", PaReopen);
        await Run("PlaybackArtwork: held legacy hydration batches exact composite IDs current first and preserves row selection", PaHydrate);
        await Run("PlaybackArtwork: restored cover appears in open lyrics without loading text again", PaHydrateLyrics);
        await Run("PlaybackArtwork: metadata with wrong album or unsafe cover cannot fill restored artwork", PaMetadataGuards);
        await Run("PlaybackArtwork: bare legacy IDs use confirmed saved album hints without changing queue identity", PaLegacyAlbumHints);
        await Run("PlaybackArtwork: requested album alone supplies fallback artwork and direct track cover keeps precedence", PaPreferredAlbumCovers);
        await Run("PlaybackArtwork: repeated track IDs in distinct response album contexts retain exact composite covers", PaCompositeResponses);
        await Run("PlaybackArtwork: logout account change cancellation and replaced queue reject held metadata", PaStale);
        await Run("PlaybackArtwork: closing during media open and held artwork retains the restored position", PaClosed);
        await Run("PlaybackArtwork: an unreadable resume snapshot survives a later writable close", PaUnreadableOnRestore);
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
        using var fixture = new PaFixture(directory, "window", (_, _) => Task.FromResult(Json("""
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
            using var fixture = new PaFixture(directory, "window", async (request, token) =>
            {
                var ids = await PaRequestedIds(request, token); queries.Add(ids);
                if (mode != "bare-wrong") return PaMetadata(ids);
                return Json("""{"result":[{"id":"9301","coverUri":"avatars.yandex.net/get-music-content/123/restore-%%","albums":[{"id":"92","title":"Wrong album"}]}]}""");
            });
            await fixture.OpenAndRestore(); await fixture.WaitArtwork();
            var expectedQuery = suffix ? "9301:92" : "9301:91";
            var expectedCover = mode == "bare-wrong" ? null : PaCover;
            Assert(queries.Count == 1 && queries[0].SequenceEqual(new[] { expectedQuery }),
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
        Assert(fixture.ApiHandler.Seen.Count == 1 && fixture.ArtworkRequests == 0, "optional artwork error retried or requested another network route");
    }

    private static string PaDirectory(string name)
    { var path = Path.Combine(_directory, "playback-artwork", name); Directory.CreateDirectory(path); return path; }
    private static TrackRow PaRow(string id, string? cover = null, long durationMs = 180000) => new()
    {
        Id = id, RequestId = id + ":91", AlbumId = "91", Title = "Saved " + id, Artist = "Saved artist", Album = "Saved album",
        Artists = [new("301", "Saved artist")], Available = true, DurationMs = durationMs, CoverUri = cover
    };
    private static PlaybackQueue PaQueue(IEnumerable<TrackRow> rows, int current)
    { var queue = new PlaybackQueue(); queue.RestoreItems(rows.ToArray(), current, true, RepeatMode.One); return queue; }
    private static OfflineItem PaOffline(string id, string filename, string? cover) => new(id, id + ":91", "Indexed " + id, "Indexed artist", "Indexed album", 180000,
        filename, "mp3", 192, 5, new string('a', 64), DateTimeOffset.UtcNow, cover, [new("301", "Indexed artist")], "91");
    private static T PaField<T>(MainWindow window, string name) => (T)typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
    private static void PaSet(MainWindow window, string name, object? value) => typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, value);
    private static void PaInvoke(MainWindow window, string name, params object?[] args) => typeof(MainWindow).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, args);
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
