using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using YMusic.Native;

internal static partial class Program
{
    private static async Task CheckFavoritesCacheStore()
    {
        await Run("FavoritesCacheStore: exact ordered metadata roundtrip excludes media and UI state", async () =>
        {
            var store = new FavoritesCacheStore(FcDirectory("roundtrip"));
            var row = FcTrack("101", "101:201");
            row.DownloadedPath = @"C:\fixture\fake-test-token-downloaded.mp3";
            row.DownloadedQuality = "fake-quality";
            row.IsLiked = row.LikeStateKnown = row.CanToggleLike = row.IsPlaying = true;
            var snapshot = FavoritesCacheStore.Capture("fixture-account-A", [row, FcTrack("101", "101:202"), row], DateTimeOffset.UtcNow);
            await store.SaveInBackgroundAsync(snapshot);
            var loaded = new FavoritesCacheStore(FcDirectory("roundtrip")).Load("fixture-account-A")!;
            var restored = FavoritesCacheStore.Restore(loaded);
            Assert(restored.Select(track => track.RequestId).SequenceEqual(new[] { "101:201", "101:202", "101:201" })
                && restored.Select(track => track.Number).SequenceEqual(new[] { 1, 2, 3 }), "composite identities, duplicate occurrence or ordering changed");
            Assert(restored[0].Artists.SequenceEqual(row.Artists) && restored[0].AlbumId == "201"
                && restored[0].CoverUri == "https://avatars.yandex.net/get-music/fixture/%%?12345"
                && restored[0].Title == row.Title && restored[0].DurationMs == row.DurationMs && restored[0].Available,
                "descriptive metadata did not roundtrip exactly");
            Assert(!ReferenceEquals(restored[0], restored[2]) && !ReferenceEquals(restored[0].Artists, restored[2].Artists)
                && restored.All(track => track.LocalPath is null && track.LocalQuality is null && track.DownloadedPath is null
                    && track.DownloadedQuality is null && !track.IsLiked && !track.LikeStateKnown && !track.CanToggleLike && !track.IsPlaying),
                "cache restored media paths or authoritative UI/playback state");
            var json = File.ReadAllText(store.FilePath("fixture-account-A"));
            foreach (var forbidden in new[] { "fixture-account-A", "localPath", "localQuality", "downloadedPath", "downloadedQuality",
                "accessToken", "refreshToken", "signedUrl", "audioUrl", "deviceUid", "isLiked", "isPlaying", "fake-test-token" })
                Assert(!json.Contains(forbidden, StringComparison.OrdinalIgnoreCase), "cache persisted an excluded field or credential");
            try { ((IList<FavoritesTrackMetadata>)loaded.Tracks)[0] = loaded.Tracks[1]; throw new InvalidOperationException("mutable tracks"); }
            catch (NotSupportedException) { }
            try { ((IList<ArtistReference>)loaded.Tracks[0].Artists)[0] = new("999", "changed"); throw new InvalidOperationException("mutable artists"); }
            catch (NotSupportedException) { }
        });

        await Run("FavoritesCacheStore: account hash isolates owners and rejects unknown identity", async () =>
        {
            var directory = FcDirectory("accounts"); var store = new FavoritesCacheStore(directory);
            Assert(store.Load("fixture-account-A") is null && !Directory.Exists(Path.Combine(directory, "favorites-cache")), "missing load created cache directories");
            var first = FcSnapshot("fixture-account-A", "101"); var second = FcSnapshot("fixture-account-B", "202");
            await store.SaveInBackgroundAsync(first); await store.SaveInBackgroundAsync(second);
            Assert(store.FilePath("fixture-account-A") != store.FilePath("fixture-account-B")
                && Path.GetFileName(store.FilePath("fixture-account-A")) == first.AccountHash + ".json"
                && first.AccountHash.Length == 64 && !first.AccountHash.Contains("fixture")
                && store.Load("fixture-account-A")!.Tracks[0].Id == "101" && store.Load("fixture-account-B")!.Tracks[0].Id == "202",
                "different accounts shared cache metadata or raw owner filename");
            File.Copy(store.FilePath("fixture-account-A"), store.FilePath("fixture-account-B"), overwrite: true);
            var transplanted = File.ReadAllBytes(store.FilePath("fixture-account-B"));
            Assert(store.Load("fixture-account-B") is null && transplanted.SequenceEqual(File.ReadAllBytes(store.FilePath("fixture-account-B"))),
                "foreign metadata was exposed or overwritten during recovery");
            foreach (var invalid in new[] { "", " ", "fake\nuid", new string('u', 513) })
            {
                Assert(store.Load(invalid) is null, "invalid owner loaded metadata");
                await Throws<ArgumentException>(() => { FavoritesCacheStore.AccountHash(invalid); return Task.CompletedTask; });
            }
            Assert(FavoritesCacheStore.AccountHash("uid/with:punctuation") != FavoritesCacheStore.AccountHash("uid_with_punctuation"),
                "owner identity was silently normalized");
        });

        await Run("FavoritesCacheStore: malformed unknown and unsafe metadata is a read-only miss", async () =>
        {
            var store = new FavoritesCacheStore(FcDirectory("invalid"));
            await store.SaveInBackgroundAsync(FcSnapshot());
            var path = store.FilePath("fixture-account-A"); var good = File.ReadAllText(path);
            var bad = new List<string> { "{", "null", "{}" };
            foreach (var field in new[] { "version", "accountHash", "savedUtc", "tracks" })
            { var node = JsonNode.Parse(good)!; node.AsObject().Remove(field); bad.Add(node.ToJsonString()); }
            foreach (var field in new[] { "id", "requestId", "title", "artist", "album", "durationMs", "available", "artists" })
            { var node = JsonNode.Parse(good)!; node["tracks"]![0]!.AsObject().Remove(field); bad.Add(node.ToJsonString()); }
            var changed = JsonNode.Parse(good)!; changed["version"] = 99; bad.Add(changed.ToJsonString());
            changed = JsonNode.Parse(good)!; changed["signedUrl"] = "https://audio.invalid/?token=fake"; bad.Add(changed.ToJsonString());
            foreach (var (field, value) in new (string, JsonNode?)[] { ("requestId", JsonValue.Create("101:wrong:album")),
                ("id", JsonValue.Create("../101")), ("albumId", JsonValue.Create("../../file")),
                ("title", JsonValue.Create("bad\u0001title")), ("durationMs", JsonValue.Create(-1L)),
                ("coverUri", JsonValue.Create("https://avatars.yandex.net/get-music/fixture/%%?token=fake")),
                ("coverUri", JsonValue.Create("https://audio.invalid/signed.mp3")), ("artists", null),
                ("localPath", JsonValue.Create(@"C:\fixture\outside.mp3")) })
            { changed = JsonNode.Parse(good)!; changed["tracks"]![0]![field] = value; bad.Add(changed.ToJsonString()); }
            changed = JsonNode.Parse(good)!; changed["tracks"]![0]!["artists"]![0]!["id"] = "https://artist.invalid"; bad.Add(changed.ToJsonString());
            foreach (var text in bad)
            {
                File.WriteAllText(path, text);
                Assert(store.Load("fixture-account-A") is null && File.ReadAllText(path) == text, "invalid cache was accepted or mutated during read");
            }
            using (var oversized = new FileStream(path, FileMode.Create, FileAccess.Write)) oversized.SetLength(FavoritesCacheStore.MaximumBytes + 1L);
            Assert(store.Load("fixture-account-A") is null && new FileInfo(path).Length == FavoritesCacheStore.MaximumBytes + 1L,
                "oversized cache bypassed the read bound or was overwritten");
            await store.SaveInBackgroundAsync(FcSnapshot());
            Assert(store.Load("fixture-account-A")!.Tracks.Count == 1, "valid replacement did not recover malformed cache");
        });

        await Run("FavoritesCacheStore: limits preserve the previous complete snapshot", async () =>
        {
            var store = new FavoritesCacheStore(FcDirectory("bounds")); var snapshot = FcSnapshot();
            await store.SaveInBackgroundAsync(snapshot);
            var before = File.ReadAllBytes(store.FilePath("fixture-account-A"));
            var maximum = Enumerable.Repeat(FcTrack("101"), FavoritesCacheStore.MaximumTracks).ToArray();
            Assert(FavoritesCacheStore.Capture("fixture-account-A", maximum, DateTimeOffset.UtcNow).Tracks.Count == FavoritesCacheStore.MaximumTracks,
                "maximum valid collection was truncated");
            await Throws<JsonException>(() => { FavoritesCacheStore.Capture("fixture-account-A", maximum.Append(maximum[0]).ToArray(), DateTimeOffset.UtcNow); return Task.CompletedTask; });
            var wide = snapshot.Tracks[0] with { Title = new string('X', 4096), Artist = new string('Y', 4096), Album = new string('Z', 4096) };
            await Throws<JsonException>(() => store.SaveInBackgroundAsync(snapshot with { Tracks = Array.AsReadOnly(Enumerable.Repeat(wide, 3000).ToArray()) }));
            await Throws<JsonException>(() => store.SaveInBackgroundAsync(snapshot with { Tracks = [wide with { Title = new string('X', 4097) }] }));
            await Throws<JsonException>(() => store.SaveInBackgroundAsync(snapshot with { Tracks = [wide with { Artists = Enumerable.Repeat(new ArtistReference("301", "Artist"), 101).ToArray() }] }));
            Assert(before.SequenceEqual(File.ReadAllBytes(store.FilePath("fixture-account-A")))
                && !Directory.EnumerateFiles(Path.GetDirectoryName(store.FilePath("fixture-account-A"))!, ".favorites-cache-*.tmp").Any(),
                "failed bounded write changed the previous cache or leaked a temporary file");
        });

        await Run("FavoritesCacheStore: failed atomic rename retains previous data and exact temp cleanup", async () =>
        {
            var store = new FavoritesCacheStore(FcDirectory("atomic")); await store.SaveInBackgroundAsync(FcSnapshot());
            var path = store.FilePath("fixture-account-A"); var before = File.ReadAllBytes(path);
            var unrelated = Path.Combine(Path.GetDirectoryName(path)!, ".favorites-cache-unrelated.tmp"); File.WriteAllText(unrelated, "keep");
            var rejected = false;
            using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                try { await store.SaveInBackgroundAsync(FcSnapshot(id: "202")); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { rejected = true; }
            }
            Assert(rejected, "locked destination did not reject atomic replacement");
            Assert(before.SequenceEqual(File.ReadAllBytes(path)) && File.ReadAllText(unrelated) == "keep"
                && Directory.EnumerateFiles(Path.GetDirectoryName(path)!, ".favorites-cache-*.tmp").Single() == unrelated,
                "failed rename damaged cache, leaked own temporary or removed another file");
            await store.SaveInBackgroundAsync(FcSnapshot(id: "202"));
            Assert(store.Load("fixture-account-A")!.Tracks[0].Id == "202", "writer did not recover after a locked destination");
        });

        await Run("FavoritesCacheStore: latest admission wins with reversed queued workers and frozen caller data", async () =>
        {
            var pending = new FcPendingWriters(); var directory = FcDirectory("writers");
            var store = new FavoritesCacheStore(directory, pending.Schedule);
            var first = store.SaveInBackgroundAsync(FcSnapshot(id: "101"));
            var latest = new FavoritesCacheStore(directory, pending.Schedule).SaveInBackgroundAsync(FcSnapshot(id: "202"));
            pending.Execute(1); pending.Execute(0); await Task.WhenAll(first, latest);
            Assert(store.Load("fixture-account-A")!.Tracks[0].Id == "202", "stale queued writer replaced newer metadata");
            var artists = new List<ArtistReference> { new("301", "Original artist") };
            var tracks = new List<FavoritesTrackMetadata> { FcSnapshot().Tracks[0] with { Artists = artists, Title = "Original title" } };
            var frozen = store.SaveInBackgroundAsync(FcSnapshot() with { Tracks = tracks });
            artists[0] = new("999", "mutated"); tracks[0] = tracks[0] with { Title = "mutated" }; tracks.Clear();
            pending.Execute(2); await frozen;
            var loaded = store.Load("fixture-account-A")!;
            Assert(loaded.Tracks[0].Title == "Original title" && loaded.Tracks[0].Artists[0] == new ArtistReference("301", "Original artist"),
                "caller mutation changed an admitted snapshot");
        });

        await Run("FavoritesCacheStore: redirected directories files and hard links never expose outside metadata", async () =>
        {
            var directory = FcDirectory("safe-io"); var outside = Path.Combine(directory, "outside"); Directory.CreateDirectory(outside);
            var marker = Path.Combine(outside, "keep"); File.WriteAllText(marker, "outside");
            var linked = Path.Combine(directory, "linked-root"); CreateUninstallJunction(linked, outside);
            try
            {
                var unsafeStore = new FavoritesCacheStore(linked);
                Assert(unsafeStore.Load("fixture-account-A") is null, "load followed a root junction");
                await Throws<IOException>(() => unsafeStore.SaveInBackgroundAsync(FcSnapshot()));
                Assert(Directory.EnumerateFileSystemEntries(outside).Single() == marker, "save followed a root junction");
            }
            finally { Directory.Delete(linked, recursive: false); }
            var root = Path.Combine(directory, "cache-link"); Directory.CreateDirectory(root);
            var cacheLink = Path.Combine(root, "favorites-cache"); CreateUninstallJunction(cacheLink, outside);
            try
            {
                var unsafeStore = new FavoritesCacheStore(root);
                await Throws<IOException>(() => unsafeStore.SaveInBackgroundAsync(FcSnapshot()));
                Assert(unsafeStore.Load("fixture-account-A") is null && Directory.EnumerateFileSystemEntries(outside).Single() == marker,
                    "cache child junction was followed");
            }
            finally { Directory.Delete(cacheLink, recursive: false); }
            var store = new FavoritesCacheStore(Path.Combine(directory, "files")); await store.SaveInBackgroundAsync(FcSnapshot());
            var cache = store.FilePath("fixture-account-A"); var external = Path.Combine(outside, "metadata.json");
            File.Copy(cache, external); var original = File.ReadAllBytes(external); File.Delete(cache); File.CreateSymbolicLink(cache, external);
            try
            {
                Assert(store.Load("fixture-account-A") is null, "load read metadata through a file symlink");
                await Throws<IOException>(() => store.SaveInBackgroundAsync(FcSnapshot(id: "202")));
                Assert(original.SequenceEqual(File.ReadAllBytes(external)), "save modified a symlink target");
            }
            finally { File.Delete(cache); }
            Assert(FcCreateHardLink(cache, external, IntPtr.Zero), "could not create fixture hard link");
            try
            {
                Assert(store.Load("fixture-account-A") is null, "load read external hard-linked metadata");
                await Throws<IOException>(() => store.SaveInBackgroundAsync(FcSnapshot(id: "202")));
                Assert(original.SequenceEqual(File.ReadAllBytes(external)), "save modified an external hard link");
            }
            finally { File.Delete(cache); }
            var unsafeArtwork = new TrackRow { Id = "101", RequestId = "101", CoverUri = "https://avatars.yandex.net/get-music/fixture/%%?token=fake" };
            Assert(FavoritesCacheStore.Capture("fixture-account-A", [unsafeArtwork], DateTimeOffset.UtcNow).Tracks[0].CoverUri is null,
                "capture retained a token-bearing artwork address");
        });
    }

    private static string FcDirectory(string name)
    { var directory = Path.Combine(_directory, "favorites-cache-store", name); Directory.CreateDirectory(directory); return directory; }
    private static FavoritesCacheSnapshot FcSnapshot(string uid = "fixture-account-A", string id = "101")
        => FavoritesCacheStore.Capture(uid, [FcTrack(id)], DateTimeOffset.UtcNow);
    private static TrackRow FcTrack(string id, string? requestId = null) => new()
    {
        Id = id, RequestId = requestId ?? id, Title = "Fixture title", Artist = "Second artist, First artist", Album = "Fixture album",
        Artists = [new("302", "Second artist"), new("301", "First artist")], AlbumId = "201", DurationMs = 180000, Available = true,
        CoverUri = "avatars.yandex.net/get-music/fixture/%%?12345", LocalPath = @"C:\fixture\fake-test-token-local.mp3", LocalQuality = "fake-quality"
    };
    private sealed class FcPendingWriters
    {
        private readonly List<(Action Work, TaskCompletionSource Completion)> _pending = [];
        internal Task Schedule(Action work)
        { var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); _pending.Add((work, completion)); return completion.Task; }
        internal void Execute(int index)
        {
            var (work, completion) = _pending[index];
            try { work(); completion.SetResult(); } catch (Exception error) { completion.SetException(error); }
        }
    }
    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FcCreateHardLink(string path, string target, IntPtr securityAttributes);
}
