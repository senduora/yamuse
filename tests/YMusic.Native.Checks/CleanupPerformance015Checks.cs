using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows.Media;
using YMusic.Native;

internal static partial class Program
{
    private static async Task CheckCleanupPerformance015()
    {
        await Run("Cleanup015: one protected object survives verification, staging and commit without a second media read", Cleanup015Protected);
        await Run("Cleanup015: 1024 protected staged files retain bounded managed memory before commit", Cleanup015RetainedMemory);
        await Run("Cleanup015: modified same-size, foreign, missing and read-only files survive partial bulk removal", Cleanup015Partial);
        await Run("Cleanup015: cancellation after staging restores every file and both indexes", Cleanup015Cancel);
        await Run("Cleanup015: failed library commit restores staged audio and playlist index", Cleanup015Rollback);
        await Run("Cleanup015: external index revision survives a rejected commit and audio rollback", Cleanup015ExternalIndex);
        await Run("Cleanup015: post-commit deletion failure restores a discoverable managed file for retry", Cleanup015DeleteFailure);
        await Run("Cleanup015: clear invalidates an already running Save across library instances", Cleanup015SaveGeneration);
        await Run("Cleanup015: redirected root and ancestor preserve external data", Cleanup015Reparse);
        await Run("Cleanup015: artwork clears disk and memory while preserving foreign, invalid, linked and read-only entries", Cleanup015Artwork);
        await Run("Cleanup015: artwork clear cancels pending publishers and accepts fresh requests afterward", Cleanup015ArtworkPending);
        await Run("Cleanup015: loudness clears shared memory and validates the entire fixed-file schema", Cleanup015Loudness);
        await Run("Cleanup015: loudness clear invalidates an in-flight analysis without touching its audio", Cleanup015LoudnessGeneration);
    }

    private static async Task Cleanup015Protected()
    {
        using var fixture = new Cleanup015Fixture("protected", 8, 1024 * 1024);
        var commitSeen = false;
        var progress = new Cleanup015Observer(p =>
        {
            if (p.Phase != "commit") return;
            commitSeen = true;
            var staged = Directory.GetFiles(fixture.Path, ".ym-delete-*.pending");
            Assert(staged.Length == 8 && p.Verified == 8 && p.VerifiedBytes == 8L * 1024 * 1024, "verification did not stage the full managed set");
            foreach (var path in staged)
            {
                Assert(Cleanup015Denied(() => { using var write = File.OpenWrite(path); }), "a verified file accepted a writer before deletion");
                Assert(Cleanup015Denied(() => File.Move(path, path + ".foreign")), "a verified pathname could be replaced");
            }
            Assert(Cleanup015Denied(() => Directory.Move(fixture.Path, fixture.Path + "-moved")), "a checked ancestor could be replaced");
        });
        var result = await fixture.Library.ClearAsync(default, progress);
        Assert(commitSeen && result == new OfflineRemovalResult(8, 0, 0, 8) && fixture.Library.Read().Count == 0, "protected deletion did not finish");
        Assert(!Directory.EnumerateFiles(fixture.Path, ".ym-delete-*.pending").Any(), "staging residue after successful clear");
    }

    private static async Task Cleanup015Partial()
    {
        using var f = new Cleanup015Fixture("partial", 6);
        var modified = f.File(1); var bytes = File.ReadAllBytes(modified); bytes[16] ^= 0x40; File.WriteAllBytes(modified, bytes);
        var foreign = System.IO.Path.Combine(f.Path, "personal.mp3"); File.WriteAllText(foreign, "untouched fixture");
        File.Delete(f.File(2)); File.SetAttributes(f.File(3), FileAttributes.ReadOnly);
        var outside = System.IO.Path.Combine(_directory, "cleanup015-file-link-target.mp3"); File.Copy(f.File(5), outside);
        File.Delete(f.File(5)); File.CreateSymbolicLink(f.File(5), outside);
        using (var locked = new FileStream(f.File(4), FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete))
        {
            var result = await f.Library.ClearAsync(default);
            Assert(result == new OfflineRemovalResult(1, 3, 2, 4), "partial results lost preserved/failed counts");
            Assert(f.Library.Read().Select(i => i.Id).Order().SequenceEqual(new[] { "3", "4" }), "failed files were forgotten or preserved foreign data became indexed");
        }
        Assert(File.ReadAllBytes(modified).SequenceEqual(bytes) && File.ReadAllText(foreign) == "untouched fixture"
            && File.Exists(f.File(5)) && File.Exists(outside), "changed, linked or foreign bytes were removed");
        File.SetAttributes(f.File(3), FileAttributes.Normal);
        var retry = await f.Library.ClearAsync(default);
        Assert(retry.Removed == 2 && retry.Failed == 0 && File.Exists(modified) && File.Exists(foreign), "retry failed to remove only retained managed audio");
    }

    private static async Task Cleanup015RetainedMemory()
    {
        using var f = new Cleanup015Fixture("retained-memory", 1024, 16 * 1024);
        var before = GC.GetTotalMemory(forceFullCollection: true); long? retainedBytes = null;
        var result = await f.Library.ClearAsync(default, new Cleanup015Observer(p =>
        {
            if (p.Phase != "commit") return;
            retainedBytes = GC.GetTotalMemory(forceFullCollection: true) - before;
            Assert(p.Verified == 1024 && p.VerifiedBytes == 16L * 1024 * 1024
                && Directory.GetFiles(f.Path, ".ym-delete-*.pending").Length == 1024, "memory fixture did not hold the complete verified set at commit");
            Assert(retainedBytes < 32L * 1024 * 1024, "per-file staging retained excessive managed buffers: " + retainedBytes);
        }));
        Assert(retainedBytes.HasValue && result == new OfflineRemovalResult(1024, 0, 0, 1024) && f.Library.Read().Count == 0
            && Directory.GetFiles(f.Path, "*.mp3").Length == 0, "bounded-memory clear did not complete every managed deletion");
        File.WriteAllText(Path.Combine(f.Path, "memory-facts.json"), JsonSerializer.Serialize(new
        {
            files = 1024, fixtureBytes = 16L * 1024 * 1024, managedBytesBefore = before,
            retainedManagedDeltaAtCommit = retainedBytes, maximumRetainedDelta = 32L * 1024 * 1024, result.Removed
        }, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static async Task Cleanup015Cancel()
    {
        using var f = new Cleanup015Fixture("cancel", 8, 512 * 1024);
        await f.Snapshot(); var before = f.Indexes();
        using var cancel = new CancellationTokenSource();
        await Throws<OperationCanceledException>(() => f.Library.ClearAsync(cancel.Token, new Cleanup015Observer(p => { if (p.Phase == "verify" && p.Verified > 0) cancel.Cancel(); })));
        Assert(f.Indexes() == before && f.Items.All(i => File.Exists(System.IO.Path.Combine(f.Path, i.FileName))), "cancel changed an index or lost staged media");
        Assert(!Directory.GetFiles(f.Path, ".ym-delete-*.pending").Any(), "cancel left quarantined files");
        Assert((await f.Library.RemoveTrackAsync("0", default)).Removed == 1, "cancellation left a stale gate or handle");
        Assert(f.Library.GetPlaylists().Count == 1 && f.Library.GetPlaylistStatus("fixture", "1").Downloaded == 7, "single-track removal changed playlist snapshots or completion semantics");
    }

    private static async Task Cleanup015Rollback()
    {
        using var f = new Cleanup015Fixture("rollback", 3);
        await f.Snapshot(); var before = f.Indexes();
        using (var blocked = new FileStream(System.IO.Path.Combine(f.Path, "library.index.json"), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var error = await Throws<Exception>(() => f.Library.ClearAsync(default));
            Assert(error is IOException or UnauthorizedAccessException, "unexpected index-write failure");
        }
        Assert(f.Indexes() == before && f.Items.All(i => File.Exists(System.IO.Path.Combine(f.Path, i.FileName))), "index write failure lost audio or a playlist snapshot");
        Assert(!Directory.GetFiles(f.Path, ".ym-delete-*.pending").Any() && f.Library.Read().Count == 3, "rollback did not release protected objects");
    }

    private static async Task Cleanup015SaveGeneration()
    {
        using var f = new Cleanup015Fixture("save-generation", 1);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var saving = new OfflineLibrary(f.Path, new AudioBuffer(System.IO.Path.Combine(f.Path, "buffer-fixture"), new FakeHandler(async (_, token) =>
        {
            entered.TrySetResult(); await release.Task.WaitAsync(token);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[4096]) };
        })));
        var save = saving.SaveAsync(new TrackRow { Id = "new", RequestId = "new", Title = "fixture", Artist = "fixture", Album = "", Available = true },
            new AudioDescriptor(new Uri("https://s1.vlx.yandex.net/fixture.mp3"), "mp3", 320), default);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await f.Library.ClearAsync(default); release.TrySetResult();
        await Throws<MusicException>(async () => await save);
        Assert(f.Library.Read().Count == 0 && !Directory.GetFiles(f.Path, "*.mp3").Any(), "in-flight save republished after clear");
    }

    private static async Task Cleanup015ExternalIndex()
    {
        using var f = new Cleanup015Fixture("external-index", 3); await f.Snapshot();
        var before = f.Indexes(); var changed = before.Item1 + " ";
        await Throws<MusicException>(() => f.Library.ClearAsync(default, new Cleanup015Observer(p =>
        {
            if (p.Phase == "commit") File.WriteAllText(System.IO.Path.Combine(f.Path, "library.index.json"), changed);
        })));
        Assert(f.Indexes() == (changed, before.Item2) && f.Items.All(i => File.Exists(System.IO.Path.Combine(f.Path, i.FileName))), "external index revision or audio was overwritten");
    }

    private static async Task Cleanup015DeleteFailure()
    {
        using var f = new Cleanup015Fixture("delete-failure", 3);
        var result = await f.Library.ClearAsync(default, new Cleanup015Observer(p =>
        {
            if (p.Phase == "commit") File.SetAttributes(Directory.GetFiles(f.Path, ".ym-delete-*.pending")[0], FileAttributes.ReadOnly);
        }));
        var retained = f.Library.Read();
        Assert(result.Removed == 2 && result.Failed == 1 && result.Forgotten == 2 && retained.Count == 1, "post-commit failure lost retained metadata");
        var restored = f.Library.ResolvePath(retained[0].FileName);
        Assert(File.Exists(restored) && Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(restored))).Equals(retained[0].Sha256, StringComparison.OrdinalIgnoreCase), "failed deletion did not restore the checked bytes");
        File.SetAttributes(restored, FileAttributes.Normal);
        Assert((await f.Library.ClearAsync(default)).Removed == 1 && f.Library.Read().Count == 0, "restored managed file cannot be retried");
    }

    private static async Task Cleanup015Reparse()
    {
        using var f = new Cleanup015Fixture("redirect-target", 1);
        var before = f.Indexes();
        var link = System.IO.Path.Combine(_directory, "cleanup015-redirect-root"); CreateUninstallJunction(link, f.Path);
        using var redirected = new OfflineLibrary(link);
        await Throws<MusicException>(() => redirected.ClearAsync(default));
        var parent = System.IO.Path.Combine(_directory, "cleanup015-redirect-ancestor"); CreateUninstallJunction(parent, System.IO.Path.GetDirectoryName(f.Path)!);
        using var nested = new OfflineLibrary(System.IO.Path.Combine(parent, System.IO.Path.GetFileName(f.Path)));
        await Throws<MusicException>(() => nested.ClearAsync(default));
        Assert(f.Indexes() == before && File.Exists(f.File(0)), "redirected cleanup touched external fixture data");
    }

    private static async Task Cleanup015Artwork()
    {
        var dir = System.IO.Path.Combine(_directory, "cleanup015-artwork"); var requests = 0;
        using var cache = new ArtworkCache(dir, new FakeHandler((_, _) => { Interlocked.Increment(ref requests); return Task.FromResult(HomeArtworkResponse(HomeArtworkPng(Colors.Blue))); }));
        const string source = "https://avatars.yandex.net/cleanup-fixture/%%";
        var original = await cache.GetAsync(source, 256, default); Assert(original is not null, "fixture cache did not populate");
        var valid = Directory.GetFiles(dir, "*.img").Single();
        var invalid = System.IO.Path.Combine(dir, new string('b', 64) + ".img"); File.WriteAllText(invalid, "foreign invalid image");
        var readonlyPath = System.IO.Path.Combine(dir, new string('c', 64) + ".img"); File.WriteAllBytes(readonlyPath, HomeArtworkPng(Colors.Red)); File.SetAttributes(readonlyPath, FileAttributes.ReadOnly);
        var foreign = System.IO.Path.Combine(dir, "photo.png"); File.WriteAllBytes(foreign, HomeArtworkPng(Colors.Red));
        var junction = System.IO.Path.Combine(dir, new string('d', 64) + ".img"); var outside = System.IO.Path.Combine(_directory, "cleanup015-artwork-outside"); Directory.CreateDirectory(outside); File.WriteAllText(System.IO.Path.Combine(outside, "keep"), "outside"); CreateUninstallJunction(junction, outside);
        var result = await cache.ClearAsync(default);
        Assert(result.Removed == 1 && result.Preserved == 3 && result.Failed == 1 && !File.Exists(valid) && File.Exists(invalid) && File.Exists(foreign) && File.Exists(System.IO.Path.Combine(outside, "keep")), "cache ownership/reparse/readonly boundary failed");
        Assert(await cache.GetAsync(source, 256, default) is not null && requests == 2, "memory cache survived clear");
        File.SetAttributes(readonlyPath, FileAttributes.Normal);
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        await Throws<OperationCanceledException>(() => cache.ClearAsync(canceled.Token));
        Assert(Directory.GetFiles(dir, "*.img").Length >= 2, "pre-canceled cache clear removed files");
    }

    private static async Task Cleanup015ArtworkPending()
    {
        var dir = System.IO.Path.Combine(_directory, "cleanup015-artwork-pending"); var calls = 0;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cache = new ArtworkCache(dir, new FakeHandler(async (_, token) =>
        {
            if (Interlocked.Increment(ref calls) == 1) { entered.TrySetResult(); await Task.Delay(Timeout.Infinite, token); }
            return HomeArtworkResponse(HomeArtworkPng(Colors.Green));
        }));
        const string source = "https://avatars.yandex.net/cleanup-pending/%%";
        var pending = cache.GetAsync(source, 256, default); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await cache.ClearAsync(default); await Throws<OperationCanceledException>(async () => await pending);
        Assert(!Directory.Exists(dir) || !Directory.GetFiles(dir, "*.img").Any(), "canceled load republished disk cache");
        Assert(await cache.GetAsync(source, 256, default) is not null && calls == 2, "fresh request did not recover after cleanup");
        var redirected = System.IO.Path.Combine(_directory, "cleanup015-artwork-link"); CreateUninstallJunction(redirected, dir);
        using var linkCache = new ArtworkCache(redirected);
        await Throws<IOException>(() => linkCache.ClearAsync(default));
        Assert(Directory.GetFiles(dir, "*.img").Length == 1, "redirected cache clear removed target files");
    }

    private static async Task Cleanup015Loudness()
    {
        var dir = System.IO.Path.Combine(_directory, "cleanup015-loudness"); Directory.CreateDirectory(dir);
        var path = System.IO.Path.Combine(dir, "loudness-v1.json"); var key = new string('A', 64);
        var stored = new Dictionary<string, LoudnessResult> { [key] = new(-14, .7) }; File.WriteAllText(path, JsonSerializer.Serialize(stored));
        var cache = new LoudnessCache(dir); typeof(LoudnessCache).GetMethod("Load", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(cache, null);
        var foreign = System.IO.Path.Combine(dir, "notes.txt"); File.WriteAllText(foreign, "keep");
        Assert((await LoudnessCache.ClearDirectoryAsync(dir, default)).Removed == 1 && !File.Exists(path) && File.Exists(foreign), "fixed loudness clear failed");
        var values = typeof(LoudnessCache).GetProperty("_values", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(cache) as Dictionary<string, LoudnessResult>;
        Assert(values is { Count: 0 }, "an existing cache instance retained memory after static clear");
        foreach (var invalid in new[] { "{\"personal\":42}", "{\"" + key + "\":{\"Peak\":0.7}}", "{\"" + key + "\":{\"IntegratedLufs\":\"wrong\",\"Peak\":0.7}}" })
        {
            File.WriteAllText(path, invalid); var result = await cache.ClearAsync(default);
            Assert(result.Preserved == 1 && File.ReadAllText(path) == invalid, "invalid/foreign loudness file was deleted");
        }
        File.WriteAllText(path, JsonSerializer.Serialize(stored)); File.SetAttributes(path, FileAttributes.ReadOnly);
        Assert((await cache.ClearAsync(default)).Failed == 1 && File.Exists(path), "readonly loudness data was deleted"); File.SetAttributes(path, FileAttributes.Normal);
        var link = System.IO.Path.Combine(_directory, "cleanup015-loudness-link"); CreateUninstallJunction(link, dir);
        Assert((await LoudnessCache.ClearDirectoryAsync(link, default)).Failed == 1 && File.Exists(path), "linked loudness clear touched target data");
    }

    private static async Task Cleanup015LoudnessGeneration()
    {
        var dir = System.IO.Path.Combine(_directory, "cleanup015-loudness-generation"); Directory.CreateDirectory(dir);
        var audio = LoudnessWave(dir, "analysis-fixture", 2, 48000, (.4, 12d)); var before = SHA256.HashData(File.ReadAllBytes(audio));
        var analysisGate = (SemaphoreSlim)typeof(LoudnessAnalysis).GetField("Gate", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        await analysisGate.WaitAsync(); var cache = new LoudnessCache(System.IO.Path.Combine(dir, "cache"));
        Task<LoudnessResult?> pending;
        try
        {
            pending = cache.MeasureAsync(audio, default);
            await Task.Delay(150); Assert(!pending.IsCompleted, "analysis did not wait on the fixture gate");
            await cache.ClearAsync(default);
        }
        finally { analysisGate.Release(); }
        Assert(await pending is not null, "clearing derived cache interrupted a usable analysis");
        Assert(!File.Exists(System.IO.Path.Combine(dir, "cache", "loudness-v1.json")) && SHA256.HashData(File.ReadAllBytes(audio)).SequenceEqual(before), "old-generation analysis republished cache or changed audio");
    }

    private static bool Cleanup015Denied(Action operation)
    {
        try { operation(); return false; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return true; }
    }
    private sealed class Cleanup015Observer(Action<OfflineCleanupProgress> report) : IProgress<OfflineCleanupProgress>
    { public void Report(OfflineCleanupProgress value) => report(value); }

    private sealed class Cleanup015Fixture : IDisposable
    {
        internal string Path { get; }
        internal OfflineLibrary Library { get; }
        internal OfflineItem[] Items { get; }
        internal Cleanup015Fixture(string name, int count, int length = 16384)
        {
            Path = System.IO.Path.Combine(_directory, "cleanup015-" + name); Directory.CreateDirectory(Path);
            var data = new byte[length]; new Random(15).NextBytes(data); var hash = Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
            Items = Enumerable.Range(0, count).Select(i => new OfflineItem(i.ToString(), i.ToString(), "Fixture", "Artist", "Album", 1000, i + ".mp3", "mp3", 320, length, hash, DateTimeOffset.UtcNow)).ToArray();
            foreach (var item in Items) System.IO.File.WriteAllBytes(System.IO.Path.Combine(Path, item.FileName), data);
            System.IO.File.WriteAllText(System.IO.Path.Combine(Path, "library.index.json"), JsonSerializer.Serialize(Items));
            Library = new OfflineLibrary(Path);
        }
        internal string File(int index) => System.IO.Path.Combine(Path, Items[index].FileName);
        internal Task<OfflinePlaylist> Snapshot() => Library.RememberPlaylistAsync(new PlaylistDetails("fixture", "1", "Fixture", "", 1,
            Items.Select(i => new TrackRow { Id = i.Id, RequestId = i.RequestId, Title = i.Title, Artist = i.Artist, Album = i.Album, Available = true }).ToArray()), AudioQuality.Excellent, default);
        internal (string, string) Indexes() => (System.IO.File.ReadAllText(System.IO.Path.Combine(Path, "library.index.json")),
            System.IO.File.Exists(System.IO.Path.Combine(Path, "playlists.index.json")) ? System.IO.File.ReadAllText(System.IO.Path.Combine(Path, "playlists.index.json")) : "<absent>");
        public void Dispose() => Library.Dispose();
    }
}
