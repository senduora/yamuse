using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using YMusic.Native;

internal static partial class Program
{
    private const string SecurityOfflineJournal = ".ym-removal.recovery";

    private static async Task CheckSecurityOffline0317()
    {
        await Run("SecurityOffline0317: crash staging and both index boundaries recover audio and snapshots idempotently", async () =>
        {
            foreach (var point in new[] { "after-stage", "after-playlist-commit", "after-library-commit", "after-delete" })
            {
                using var f = new SecurityOfflineFixture(point);
                await SecurityOfflineCrash(f, point);
                using var reopened = f.Open(); await reopened.RecoverPendingAsync();
                var expected = point == "after-delete" ? 1 : 2;
                Assert(reopened.Read().Count == expected && reopened.Tracks().Count(t => t.Available) == expected, point + " lost discoverable audio");
                Assert(reopened.GetPlaylists().Count == (point is "after-stage" or "after-playlist-commit" ? 1 : 0), point + " lost precommit snapshot or reverted a committed clear");
                foreach (var item in reopened.Read()) Assert(SecurityOfflineHash(Path.Combine(f.Music, item.FileName)) == item.Sha256, "recovered index points at unverified bytes");
                await reopened.RecoverPendingAsync();
                Assert(!File.Exists(f.Journal) && reopened.Read().Count == expected && !Directory.GetFiles(f.Music, ".ym-delete-*.pending").Except(new[] { f.ForeignPending }).Any(), "recovery is not idempotent");
                Assert(File.ReadAllText(f.ForeignPending) == "unowned pending" && File.ReadAllText(f.Foreign) == "foreign file", "recovery removed an unowned file");
                Assert((await reopened.ClearAsync(default)).Removed == expected && File.Exists(f.ForeignPending) && File.Exists(f.Foreign), "retry did not remove exactly recovered managed audio");
            }
        });
        await Run("SecurityOffline0317: cancelled rollback collision indexes original verified bytes and active reads never wait for Gate", async () =>
        {
            using var f = new SecurityOfflineFixture("collision", count: 1);
            using var cancel = new CancellationTokenSource(); var readFinished = false;
            f.Library.RemovalCheckpoint = point =>
            {
                if (point != "after-stage") return;
                var read = Task.Run(() => f.Library.Read().Count);
                readFinished = read.Wait(TimeSpan.FromSeconds(2));
                Assert(readFinished && read.Result == 1, "active journal read blocked on the writer/cache gate");
                File.WriteAllBytes(Path.Combine(f.Music, "track-0.mp3"), Enumerable.Repeat((byte)0x77, 4096).ToArray()); cancel.Cancel();
            };
            await Throws<OperationCanceledException>(() => f.Library.ClearAsync(cancel.Token));
            var item = f.Library.Read().Single();
            Assert(readFinished && item.FileName.StartsWith(".ym-recovered-", StringComparison.Ordinal)
                && SecurityOfflineHash(Path.Combine(f.Music, item.FileName)) == item.Sha256 && f.Library.Tracks().Single().Available, "rollback indexed the replacement instead of verified audio");
            Assert(File.ReadAllBytes(Path.Combine(f.Music, "track-0.mp3"))[0] == 0x77 && !File.Exists(f.Journal), "foreign replacement was overwritten or repaired journal remained");
        });
        await Run("SecurityOffline0317: stale recovery returns a prompt guarded read and invalidates the same instance cache", async () =>
        {
            using var f = new SecurityOfflineFixture("read-protocol");
            _ = f.Library.Read(); await SecurityOfflineCrash(f, "after-stage");
            var gate = (SemaphoreSlim)typeof(OfflineLibrary).GetField("_writeLock", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(f.Library)!;
            await gate.WaitAsync(); OfflineLibrary? reopening = null;
            try
            {
                reopening = f.Open(); var clock = Stopwatch.StartNew();
                try { _ = reopening.Read(); throw new InvalidOperationException("pending recovery read unexpectedly succeeded"); }
                catch (MusicException) { }
                Assert(clock.Elapsed < TimeSpan.FromSeconds(1), "pending recovery blocked normal read");
            }
            finally { gate.Release(); }
            using (reopening)
            {
                await reopening!.RecoverPendingAsync();
                Assert(reopening.Read().Count == 2 && reopening.Tracks().All(t => t.Available) && f.Library.Tracks().All(t => t.Available), "completed recovery did not refresh the existing library cache");
            }
        });
        await Run("SecurityOffline0317: recovery interrupted after restore and a foreign collision resumes by its durable mapping", async () =>
        {
            using var f = new SecurityOfflineFixture("recovery-crash"); await SecurityOfflineCrash(f, "after-stage");
            var staged = Directory.GetFiles(f.Music, ".ym-delete-*.pending").Single(path => path != f.ForeignPending);
            var originalIndex = JsonNode.Parse(File.ReadAllText(Path.Combine(f.Music, "library.index.json")))!.AsArray();
            var stagedHash = SecurityOfflineHash(staged);
            var originalName = originalIndex.Single(row => row!["Sha256"]!.GetValue<string>() == stagedHash)!["FileName"]!.GetValue<string>();
            File.WriteAllBytes(Path.Combine(f.Music, originalName), Enumerable.Repeat((byte)0x77, 4096).ToArray());
            await SecurityOfflineCrash(f, "recovery-after-restore", recover: true);
            using var reopened = f.Open(); await reopened.RecoverPendingAsync();
            Assert(reopened.Read().Count == 2 && reopened.Tracks().All(t => t.Available) && !File.Exists(f.Journal), "second recovery could not find its already-restored mapping");
            Assert(File.ReadAllBytes(Path.Combine(f.Music, originalName))[0] == 0x77, "recovery overwrote the collision file");
            foreach (var item in reopened.Read()) Assert(SecurityOfflineHash(Path.Combine(f.Music, item.FileName)) == item.Sha256, "second recovery points to replacement bytes");
        });
        await Run("SecurityOffline0317: crash after recovery index repair replays entries already deleted after commit", async () =>
        {
            using var f = new SecurityOfflineFixture("repair-replay"); await SecurityOfflineCrash(f, "after-delete");
            await SecurityOfflineCrash(f, "recovery-after-index", recover: true);
            using var reopened = f.Open(); await reopened.RecoverPendingAsync();
            Assert(reopened.Read().Count == 1 && reopened.Tracks().All(t => t.Available) && !File.Exists(f.Journal), "already-deleted entry blocked replay of a repaired index");
        });
        await Run("SecurityOffline0317: malformed tampered transplanted oversized and traversing journals preserve all files", async () =>
        {
            foreach (var kind in new[] { "plaintext", "tampered", "transplanted", "oversized", "traversal", "unknown-schema" })
            {
                using var f = new SecurityOfflineFixture(kind); await SecurityOfflineCrash(f, "after-stage");
                var originalJournal = File.ReadAllBytes(f.Journal);
                var protectedBefore = Directory.GetFiles(f.Music).Where(p => p != f.Journal).ToDictionary(p => p, File.ReadAllBytes);
                if (kind == "plaintext") File.WriteAllText(f.Journal, "{\"Version\":1}");
                else if (kind == "tampered") { var bytes = originalJournal.ToArray(); bytes[^8] ^= 0x40; File.WriteAllBytes(f.Journal, bytes); }
                else if (kind == "oversized") { using var file = new FileStream(f.Journal, FileMode.Create); file.SetLength(64L * 1024 * 1024 + 64 * 1024 + 1); }
                else if (kind == "transplanted")
                {
                    using var other = new SecurityOfflineFixture("other-path");
                    await SecurityOfflineCrash(other, "after-stage"); File.WriteAllBytes(f.Journal, File.ReadAllBytes(other.Journal));
                }
                else
                {
                    var transform = typeof(OfflineLibrary).GetMethod("TransformRecovery", BindingFlags.NonPublic | BindingFlags.Instance)!;
                    var plain = (byte[])transform.Invoke(f.Library, [originalJournal, false])!;
                    var json = JsonNode.Parse(plain)!;
                    if (kind == "traversal") json["Entries"]![0]!["Quarantine"] = "../outside.mp3";
                    else json["Version"] = 99;
                    var malformed = JsonSerializer.SerializeToUtf8Bytes(json); File.WriteAllBytes(f.Journal, (byte[])transform.Invoke(f.Library, [malformed, true])!);
                }
                try { await f.Library.RecoverPendingAsync(); throw new InvalidOperationException("unsafe journal accepted: " + kind); }
                catch (Exception error) when (error is CryptographicException or JsonException or MusicException) { }
                Assert(File.Exists(f.Journal) && protectedBefore.All(pair => File.ReadAllBytes(pair.Key).SequenceEqual(pair.Value)), kind + " changed an indexed/foreign file");
                File.WriteAllBytes(f.Journal, originalJournal); await f.Library.RecoverPendingAsync();
                Assert(f.Library.Tracks().All(t => t.Available), kind + " did not recover after restoring the authentic journal");
            }
        });
        await Run("SecurityOffline0317: journal and audio symlinks never authorize following their targets", async () =>
        {
            foreach (var kind in new[] { "journal", "quarantine" })
            {
                using var f = new SecurityOfflineFixture("link-" + kind); await SecurityOfflineCrash(f, "after-stage");
                var link = kind == "journal" ? f.Journal : Directory.GetFiles(f.Music, ".ym-delete-*.pending").Single(path => path != f.ForeignPending);
                var original = File.ReadAllBytes(link); var outside = Path.Combine(f.Root, "outside-keep.bin"); File.WriteAllBytes(outside, original);
                File.Delete(link); File.CreateSymbolicLink(link, outside);
                try { await f.Library.RecoverPendingAsync(); throw new InvalidOperationException("reparse journal/audio accepted"); }
                catch (Exception error) when (error is IOException or MusicException) { }
                Assert(File.Exists(link) && File.ReadAllBytes(outside).SequenceEqual(original), "recovery followed or removed the reparse target");
            }
        });
        await Run("SecurityOffline0317: interrupted snapshot restoration preserves an external index revision", async () =>
        {
            using var f = new SecurityOfflineFixture("external-snapshot"); await SecurityOfflineCrash(f, "after-playlist-commit");
            var path = Path.Combine(f.Music, "playlists.index.json"); var external = File.ReadAllText(path).Replace("[]", "[ ]", StringComparison.Ordinal) + " ";
            File.WriteAllText(path, external); await f.Library.RecoverPendingAsync();
            Assert(File.ReadAllText(path) == external && f.Library.Tracks().All(t => t.Available), "recovery overwrote an external playlist revision");
        });
        await Run("SecurityOffline0317: ordinary reads and recovery never create an absent unmanaged library", async () =>
        {
            var root = Path.Combine(_directory, "security-offline-0317", "absent-" + Guid.NewGuid().ToString("N"));
            using var library = new OfflineLibrary(Path.Combine(root, "music"), new AudioBuffer(Path.Combine(root, "buffer")));
            await library.RecoverPendingAsync(); Assert(library.Read().Count == 0 && !Directory.Exists(root), "read/recovery created an absent folder");
        });
        await Run("SecurityOffline0317: uninstall recovers authentic provenance before removal and a bad journal preserves music", async () =>
        {
            foreach (var invalid in new[] { false, true })
            {
                using var f = new SecurityOfflineFixture("uninstall-" + invalid);
                File.Delete(f.ForeignPending); File.Delete(f.Foreign);
                var paths = UninstallPaths.FromKnownFolders(Path.Combine(f.Root, "local"), Path.Combine(f.Root, "documents"));
                Directory.CreateDirectory(paths.Data); File.WriteAllText(Path.Combine(paths.Data, "fixture.txt"), "owned app data");
                await SecurityOfflineCrash(f, "after-stage");
                if (invalid) { var bytes = File.ReadAllBytes(f.Journal); bytes[^8] ^= 0x40; File.WriteAllBytes(f.Journal, bytes); }
                var before = Directory.GetFiles(f.Music).ToDictionary(p => p, File.ReadAllBytes);
                var result = await Task.Run(() => UninstallCleanup.Execute(paths));
                if (invalid) Assert(!result.Complete && result.Issues.Count > 0 && before.All(pair => File.ReadAllBytes(pair.Key).SequenceEqual(pair.Value)), "bad journal authorized uninstall music deletion");
                else Assert(result.Complete && !Directory.Exists(f.Music) && !Directory.Exists(paths.Data), "authentic stale transaction survived owned-data uninstall");
            }
        });
    }

    // Root calls this before WPF initialization when the first argument is
    // --security-offline-child. Arguments: fixtureRoot, checkpoint, [recover].
    private static int SecurityOffline0317Child(string[] args)
    {
        if (args.Length is < 2 or > 3) return 2;
        var root = Path.GetFullPath(args[0]);
        if (!root.Contains(Path.DirectorySeparatorChar + "security-offline-0317" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || File.ReadAllText(Path.Combine(root, ".security-fixture")) != "Yamuse.SecurityOffline0317.Fixture") return 2;
        var recovering = args.Length == 3 && args[2] == "recover";
        var music = Path.Combine(root, "documents", "Yamuse", "Music");
        var journal = Path.Combine(music, SecurityOfflineJournal);
        var pausedJournal = Path.Combine(root, "child-fixture-journal");
        // Install the fixture hook before scheduling any recovery worker. Only
        // this isolated test process temporarily renames its own known journal.
        if (recovering) File.Move(journal, pausedJournal);
        using var library = new OfflineLibrary(music, new AudioBuffer(Path.Combine(root, "buffer")));
        library.RemovalCheckpoint = point => { if (point == args[1]) Environment.Exit(73); };
        if (recovering) { File.Move(pausedJournal, journal); library.RecoverPendingAsync().GetAwaiter().GetResult(); }
        else library.ClearAsync(default, new SecurityOfflineObserver(p => { if (args[1] == "after-delete" && p.Phase == "delete") Environment.Exit(73); })).GetAwaiter().GetResult();
        return 3; // Expected checkpoint was not reached.
    }

    private static async Task SecurityOfflineCrash(SecurityOfflineFixture fixture, string point, bool recover = false)
    {
        var processPath = Environment.ProcessPath!;
        var start = new ProcessStartInfo(processPath) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = fixture.Root };
        if (Path.GetFileNameWithoutExtension(processPath).Equals("dotnet", StringComparison.OrdinalIgnoreCase)) start.ArgumentList.Add(typeof(Program).Assembly.Location);
        start.ArgumentList.Add("--security-offline-child"); start.ArgumentList.Add(fixture.Root); start.ArgumentList.Add(point);
        if (recover) start.ArgumentList.Add("recover");
        using var child = Process.Start(start) ?? throw new IOException("Failed to start isolated recovery child.");
        await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
        Assert(child.ExitCode == 73, "isolated crash checkpoint failed: " + point + ", exit=" + child.ExitCode);
    }

    private static string SecurityOfflineHash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    private sealed class SecurityOfflineObserver(Action<OfflineCleanupProgress> action) : IProgress<OfflineCleanupProgress>
    { public void Report(OfflineCleanupProgress value) => action(value); }

    private sealed class SecurityOfflineFixture : IDisposable
    {
        internal string Root { get; }
        internal string Music => Path.Combine(Root, "documents", "Yamuse", "Music");
        internal string Journal => Path.Combine(Music, SecurityOfflineJournal);
        internal string ForeignPending { get; }
        internal string Foreign => Path.Combine(Music, "personal.mp3");
        internal OfflineLibrary Library { get; }
        internal SecurityOfflineFixture(string name, int count = 2)
        {
            Root = Path.GetFullPath(Path.Combine(_directory, "security-offline-0317", name + "-" + Guid.NewGuid().ToString("N")));
            if (!Root.StartsWith(Path.GetFullPath(_directory) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new IOException("Fixture escaped results directory.");
            Directory.CreateDirectory(Music); File.WriteAllText(Path.Combine(Root, ".security-fixture"), "Yamuse.SecurityOffline0317.Fixture");
            var rows = Enumerable.Range(0, count).Select(i =>
            {
                var bytes = Enumerable.Repeat((byte)(0x31 + i), 4096).ToArray(); var name = "track-" + i + ".mp3";
                File.WriteAllBytes(Path.Combine(Music, name), bytes);
                return new OfflineItem("r3-" + i, "r3-" + i, "fixture", "fixture", "", 1000, name, "mp3", 320, bytes.Length, Convert.ToHexString(SHA256.HashData(bytes)), DateTimeOffset.UtcNow);
            }).ToArray();
            File.WriteAllText(Path.Combine(Music, "library.index.json"), JsonSerializer.Serialize(rows));
            var tracks = rows.Select(i => new OfflinePlaylistTrack(i.Id, i.RequestId, i.Title, i.Artist, i.Album, i.DurationMs, true)).ToArray();
            var snapshot = new OfflinePlaylistIndex(1, [new("fixture", "1", "fixture", 1, AudioQuality.Excellent, DateTimeOffset.UtcNow, tracks)]);
            File.WriteAllText(Path.Combine(Music, "playlists.index.json"), JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
            ForeignPending = Path.Combine(Music, ".ym-delete-" + Guid.NewGuid().ToString("N") + ".pending"); File.WriteAllText(ForeignPending, "unowned pending");
            File.WriteAllText(Foreign, "foreign file"); Library = Open();
        }
        internal OfflineLibrary Open() => new(Music, new AudioBuffer(Path.Combine(Root, "buffer")));
        public void Dispose() => Library.Dispose(); // Preserve bounded evidence; never recursively delete a fixture with links.
    }
}
