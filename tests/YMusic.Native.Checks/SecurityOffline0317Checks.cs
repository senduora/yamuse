using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using YMusic.Native;

internal static partial class Program
{
    private const string SecurityOfflineJournal = ".ym-removal.recovery";
    private const string SecurityOfflineCommittedJournal = ".ym-removal.committed";
    private const string SecurityOfflineSaveJournal = ".ym-save.recovery";

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
                Assert(!f.HasRemovalJournal && reopened.Read().Count == expected && !Directory.GetFiles(f.Music, ".ym-delete-*.pending").Except(new[] { f.ForeignPending }).Any(), "recovery is not idempotent");
                Assert(File.ReadAllText(f.ForeignPending) == "unowned pending" && File.ReadAllText(f.Foreign) == "foreign file", "recovery removed an unowned file");
                Assert((await reopened.ClearAsync(default)).Removed == expected && File.Exists(f.ForeignPending) && File.Exists(f.Foreign), "retry did not remove exactly recovered managed audio");
            }
        });
        await Run("SecurityOffline0317: equal library hashes do not decide the playlist clear boundary", async () =>
        {
            foreach (var point in new[] { "after-playlist-commit", "before-removal-commit-marker", "after-library-commit" })
            {
                using var f = new SecurityOfflineFixture("equal-index-" + point, count: 0);
                var index = Path.Combine(f.Music, "library.index.json");
                Assert(File.ReadAllText(index) == "[]", "fixture library is not an empty byte-identical index");
                await SecurityOfflineCrash(f, point);
                await SecurityOfflineCrash(f, "recovery-after-index", recover: true);
                using var reopened = f.Open(); await reopened.RecoverPendingAsync();
                Assert(reopened.GetPlaylists().Count == (point == "after-library-commit" ? 0 : 1)
                    && File.ReadAllText(index) == "[]" && !f.HasRemovalJournal, point + " lost or resurrected a playlist snapshot");
            }
        });
        await Run("SecurityOffline0317: legacy replay records the clear decision before restoring original index bytes", async () =>
        {
            using var f = new SecurityOfflineFixture("legacy-committed-replay", count: 1);
            var index = Path.Combine(f.Music, "library.index.json");
            var rows = JsonSerializer.Deserialize<OfflineItem[]>(File.ReadAllText(index))!;
            var original = JsonSerializer.Serialize(rows, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(index, original);
            await SecurityOfflineCrash(f, "after-library-commit");
            var transform = typeof(OfflineLibrary).GetMethod("TransformRecovery", BindingFlags.NonPublic | BindingFlags.Instance)!;
            var plain = (byte[])transform.Invoke(f.Library, [File.ReadAllBytes(f.CommittedJournal), false])!;
            var legacy = JsonNode.Parse(plain)!;
            legacy["Version"] = 1;
            legacy.AsObject().Remove("Unstaged");
            foreach (var entry in legacy["Entries"]!.AsArray()) entry!.AsObject().Remove("Identity");
            File.WriteAllBytes(f.Journal, (byte[])transform.Invoke(f.Library, [JsonSerializer.SerializeToUtf8Bytes(legacy), true])!);
            File.Delete(f.CommittedJournal);

            await SecurityOfflineCrash(f, "recovery-after-index", recover: true);
            Assert(File.Exists(f.CommittedJournal), "the legacy phase decision was not finalized before replay rewrote the index");
            File.WriteAllText(index, original); // Recreate the original index hash while the durable decision remains.
            using var reopened = f.Open(); await reopened.RecoverPendingAsync();
            Assert(reopened.Tracks().Single().Available && reopened.GetPlaylists().Count == 0 && !f.HasRemovalJournal,
                "legacy replay resurrected a snapshot after restoring the original audio index");
        });
        await Run("SecurityOffline0317: committed replay preserves a restored backup copy with identical bytes", async () =>
        {
            foreach (var unsupported in new[] { false, true })
            {
                using var f = new SecurityOfflineFixture("backup-copy-" + unsupported, count: 1);
                var original = Path.Combine(f.Music, "track-0.mp3");
                var backup = Path.Combine(f.Root, "backup-fixture.mp3");
                File.Copy(original, backup);
                var before = SecurityOfflineHash(backup);
                await SecurityOfflineCrash(f, "after-delete", unsupportedIdentity: unsupported);
                File.Move(backup, original);
                using var reopened = f.Open(); await reopened.RecoverPendingAsync();
                Assert(reopened.Read().Count == 0 && reopened.GetPlaylists().Count == 0 && !f.HasRemovalJournal,
                    "committed replay adopted the backup copy as managed audio");
                Assert((await reopened.ClearAsync(default)).Removed == 0 && SecurityOfflineHash(original) == before,
                    "cleanup removed or changed the restored backup copy");
            }
        });
        await Run("SecurityOffline0317: committed replay keeps genuine audio through two interrupted restores", async () =>
        {
            foreach (var unsupported in new[] { false, true })
            {
                using var f = new SecurityOfflineFixture("owned-double-replay-" + unsupported, count: 1);
                var original = Path.Combine(f.Music, "track-0.mp3");
                var before = SecurityOfflineHash(original);
                await SecurityOfflineCrash(f, "after-library-commit", unsupportedIdentity: unsupported);
                await SecurityOfflineCrash(f, "recovery-after-restore", recover: true);
                await SecurityOfflineCrash(f, "recovery-after-index", recover: true);
                using var reopened = f.Open(); await reopened.RecoverPendingAsync();
                var restored = reopened.Tracks().Single();
                Assert(restored.Available && SecurityOfflineHash(restored.LocalPath!) == before && !f.HasRemovalJournal
                    && reopened.GetPlaylists().Count == 0 && (unsupported
                        ? Path.GetFileName(restored.LocalPath!).StartsWith(".ym-recovered-", StringComparison.Ordinal)
                        : restored.LocalPath == original), "repeated replay lost verified bytes or revived the cleared playlist");
            }
        });
        await Run("SecurityOffline0317: pending removal restores missing original metadata through a second restart", async () =>
        {
            using var f = new SecurityOfflineFixture("missing-indexed-row", count: 2);
            File.Delete(Path.Combine(f.Music, "track-0.mp3"));
            await SecurityOfflineCrash(f, "before-removal-commit-marker");
            await SecurityOfflineCrash(f, "recovery-after-index", recover: true);
            using var reopened = f.Open(); await reopened.RecoverPendingAsync();
            var rows = reopened.Tracks();
            Assert(rows.Count == 2 && !rows.Single(row => row.Id == "r3-0").Available
                && rows.Single(row => row.Id == "r3-1").Available && reopened.GetPlaylists().Count == 1 && !f.HasRemovalJournal,
                "replay lost unavailable metadata, changed ownership or wrote duplicate rows");
        });
        await Run("SecurityOffline0317: pending missing metadata rollback preserves external changes", async () =>
        {
            foreach (var change in new[] { "external-index", "foreign-audio", "committed-removal", "live-rollback" })
            {
                using var f = new SecurityOfflineFixture("missing-row-" + change, count: 1);
                var audio = Path.Combine(f.Music, "track-0.mp3");
                var index = Path.Combine(f.Music, "library.index.json");
                File.Delete(audio);
                if (change == "live-rollback")
                {
                    f.Library.RemovalCheckpoint = point =>
                    {
                        if (point == "before-removal-commit-marker") throw new IOException("Injected before commit marker.");
                    };
                    await Throws<IOException>(() => f.Library.ClearAsync(default));
                }
                else
                {
                    await SecurityOfflineCrash(f, change == "committed-removal" ? "after-library-commit" : "before-removal-commit-marker");
                    if (change == "external-index") File.WriteAllText(index, " [] ");
                    if (change == "foreign-audio") File.WriteAllBytes(audio, Enumerable.Repeat((byte)0x77, 4096).ToArray());
                }
                using var reopened = f.Open(); await reopened.RecoverPendingAsync();
                Assert(reopened.Read().Count == (change == "live-rollback" ? 1 : 0) && !f.HasRemovalJournal,
                    change + " restored metadata without evidence or lost it on an uncommitted own write");
                if (change == "external-index") Assert(File.ReadAllText(index) == " [] ", "external index revision was overwritten");
                if (change == "foreign-audio") Assert(File.ReadAllBytes(audio).All(value => value == 0x77), "foreign file was changed");
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
        await Run("SecurityOffline0317: failed rollback retains the journal until a later restore can complete", async () =>
        {
            using var f = new SecurityOfflineFixture("blocked-rollback", count: 1);
            using var cancel = new CancellationTokenSource();
            var original = Path.Combine(f.Music, "track-0.mp3");
            var originalHash = SecurityOfflineHash(original);
            var recovery = "";
            f.Library.RemovalCheckpoint = point =>
            {
                if (point != "after-stage") return;
                var transform = typeof(OfflineLibrary).GetMethod("TransformRecovery", BindingFlags.NonPublic | BindingFlags.Instance)!;
                using var pending = new FileStream(f.Journal, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
                var encrypted = new byte[checked((int)pending.Length)]; pending.ReadExactly(encrypted);
                var journal = (byte[])transform.Invoke(f.Library, [encrypted, false])!;
                recovery = Path.Combine(f.Music, JsonNode.Parse(journal)!["Entries"]![0]!["Recovery"]!.GetValue<string>());
                File.WriteAllText(original, "original name occupied");
                File.WriteAllText(recovery, "recovery name occupied");
                cancel.Cancel();
            };
            await Throws<IOException>(() => f.Library.ClearAsync(cancel.Token));
            await Throws<IOException>(() => f.Library.RecoverPendingAsync());
            Assert(File.Exists(f.Journal) && File.ReadAllText(original) == "original name occupied"
                && File.ReadAllText(recovery) == "recovery name occupied", "rollback discarded the journal or replaced a collision file");
            File.Delete(recovery);
            await f.Library.RecoverPendingAsync();
            Assert(!File.Exists(f.Journal) && f.Library.Tracks().Single().LocalPath == recovery
                && SecurityOfflineHash(recovery) == originalHash, "retry did not recover the managed audio and index after the collision cleared");
        });
        await Run("SecurityOffline0317: manual app actions retry guarded recovery after a temporary lock", async () =>
        {
            foreach (var action in new[] { "delete", "clear", "download" })
            {
                using var f = new SecurityOfflineFixture("manual-retry-" + action, count: 1);
                await SecurityOfflineCrash(f, "after-stage");
                using (File.Open(f.Journal, FileMode.Open, FileAccess.Read, FileShare.Read))
                    await Throws<IOException>(() => f.Library.RecoverPendingAsync());

                await Throws<MusicException>(() => Task.FromResult(f.Library.Read()));
                var api = new FakeHandler((_, _) => throw new InvalidOperationException("The fixture must stay offline."));
                var window = new MainWindow(api: new MusicApi(api), restore: false,
                    diagnosticsDirectory: Path.Combine(f.Root, "diagnostics"), offline: f.Library,
                    preferences: new PreferencesStore(Path.Combine(f.Root, "preferences")));
                try
                {
                    window.Show();
                    var track = new TrackRow { Id = "r3-0", RequestId = "r3-0", Available = true };
                    switch (action)
                    {
                        case "delete": await Call(window, "DeleteOfflineTrackAsync", track); break;
                        case "clear": await Call(window, "ClearOfflineAfterConfirmationAsync"); break;
                        case "download":
                            typeof(MainWindow).GetField("_session", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, FakeSession);
                            await Call(window, "StartDownloadBatchAsync", new[] { track }, "Fixture", null!, false);
                            break;
                    }
                    var tracks = f.Library.Tracks();
                    Assert(!File.Exists(f.Journal) && tracks.Count == (action == "download" ? 1 : 0)
                        && tracks.All(row => row.Available) && File.Exists(f.Foreign) && api.Seen.Count == 0,
                        action + " did not finish after the guard released the pending recovery");
                }
                finally { window.Close(); }
            }
        });
        await Run("SecurityOffline0317: long local paths support save readback and guarded cleanup", async () =>
        {
            var root = Path.Combine(_directory, "security-offline-0317", "long-path-" + Guid.NewGuid().ToString("N"));
            var music = root;
            while (music.Length < 270) music = Path.Combine(music, new string('m', 72));
            var bytes = Enumerable.Repeat((byte)0x37, 4096).ToArray();
            var media = new FakeHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new ByteArrayContent(bytes) }));
            using var library = new OfflineLibrary(music, new AudioBuffer(Path.Combine(root, "buffer"), media));
            var track = new TrackRow { Id = "long-1", RequestId = "long-1", Title = "Fixture", Artist = "Artist", Available = true };
            await library.RememberPlaylistAsync(new PlaylistDetails("fixture", "1", "Fixture", "", 1, [track]), AudioQuality.Excellent, default);
            var saved = await library.SaveAsync(track, new AudioDescriptor(new Uri("https://s1.vlx.yandex.net/fixture.mp3"), "mp3", 320), default);
            var path = Path.Combine(music, saved.FileName);
            var foreign = Path.Combine(music, "personal.mp3");
            File.WriteAllText(foreign, "personal");

            using var reopening = new OfflineLibrary(music, new AudioBuffer(Path.Combine(root, "buffer")));
            Assert(File.ReadAllBytes(path).SequenceEqual(bytes) && reopening.Tracks().Single().LocalPath == path
                && reopening.GetPlaylists().Count == 1 && media.Seen.Count == 1, "long paths did not retain audio and indexes");
            var result = await reopening.ClearAsync(default);
            Assert(result.Removed == 1 && !File.Exists(path) && !File.Exists(Path.Combine(music, SecurityOfflineJournal))
                && reopening.Read().Count == 0 && reopening.GetPlaylists().Count == 0 && File.ReadAllText(foreign) == "personal",
                "guarded long-path cleanup did not preserve the unrelated file or complete its journal");
        });
        await Run("SecurityOffline0317: incomplete saves leave one indexed file after reopen and retry", async () =>
        {
            foreach (var point in new[] { "save-before-receipt", "save-before-audio-publish", "save-after-audio-publish", "save-after-index-commit" })
            {
                using var f = new SecurityOfflineFixture(point, count: 0);
                await SecurityOfflineCrash(f, point, save: true);
                var receipted = point != "save-before-receipt";
                Assert(File.Exists(f.SaveJournal) == receipted, point + " left the wrong recovery record state");
                if (!receipted) Assert(!Directory.GetFiles(f.Music, ".ym-audio-*.partial").Any(), "copied data survived without a receipt");
                var media = SecurityOfflineSaveMedia();
                using var reopened = f.Open(media); await reopened.RecoverPendingAsync();
                var committed = point == "save-after-index-commit";
                Assert(reopened.Read().Count == (committed ? 1 : 0) && !File.Exists(f.SaveJournal)
                    && !Directory.GetFiles(f.Music, ".ym-audio-*.partial").Any(), point + " recovered the wrong operation state");
                var saved = await reopened.SaveAsync(SecurityOfflineSaveTrack(), SecurityOfflineSaveAudio(), default);
                var savedPath = Path.Combine(f.Music, saved.FileName);
                Assert(reopened.Tracks().Single().LocalPath == savedPath && media.Seen.Count == (committed ? 0 : 1)
                    && Directory.GetFiles(f.Music, "*.mp3").ToHashSet().SetEquals(new[] { f.Foreign, savedPath }),
                    point + " left a duplicate or did not preserve the foreign file");
            }
        });
        await Run("SecurityOffline0317: redownload replaces stale rows of the selected audio quality", async () =>
        {
            foreach (var change in new[] { "missing", "size-changed", "legacy-duplicate" })
            {
                using var f = new SecurityOfflineFixture("stale-quality-" + change, count: 0);
                var media = SecurityOfflineSaveMedia();
                using var library = f.Open(media);
                var track = SecurityOfflineSaveTrack();
                var audio = SecurityOfflineSaveAudio();
                var original = await library.SaveAsync(track, audio, default);
                var alternate = await library.SaveAsync(track, audio with { Bitrate = 128 }, default);
                var originalPath = Path.Combine(f.Music, original.FileName);
                var alternatePath = Path.Combine(f.Music, alternate.FileName);
                var alternateHash = SecurityOfflineHash(alternatePath);
                var userBytes = Enumerable.Repeat((byte)0x67, 4097).ToArray();
                if (change == "size-changed") File.WriteAllBytes(originalPath, userBytes);
                else File.Delete(originalPath);
                if (change == "legacy-duplicate")
                {
                    var older = original with { Bytes = original.Bytes - 1, Sha256 = new string('a', 64) };
                    File.WriteAllText(Path.Combine(f.Music, "library.index.json"), JsonSerializer.Serialize(new[] { alternate, original, older }));
                }

                var fresh = await library.SaveAsync(track, audio, default);
                using var reopened = f.Open();
                var rows = reopened.Read();
                Assert(rows.Count == 2 && rows.Single(row => row.Bitrate == audio.Bitrate).FileName == fresh.FileName
                    && rows.Single(row => row.Bitrate == alternate.Bitrate).FileName == alternate.FileName
                    && reopened.Tracks().All(row => row.Available) && media.Seen.Count == 3,
                    change + " retained a stale row or removed an unrelated quality");
                Assert(SecurityOfflineHash(alternatePath) == alternateHash && File.Exists(f.Foreign), "redownload changed a preserved file");
                if (change == "size-changed") Assert(File.ReadAllBytes(originalPath).SequenceEqual(userBytes), "redownload overwrote the changed file");
            }
        });
        await Run("SecurityOffline0317: crash after staging index or journal leaves no owned temporary file", async () =>
        {
            foreach (var point in new[] { "journal-before-publish", "index-before-publish" })
            {
                var saving = point == "index-before-publish";
                using var f = new SecurityOfflineFixture("temporary-" + point, count: saving ? 0 : 1);
                var index = Path.Combine(f.Music, "library.index.json");
                var original = File.ReadAllText(index);
                var foreign = index + ".tmp-personal-copy";
                File.WriteAllText(foreign, "personal copy");
                await SecurityOfflineCrash(f, point, save: saving);
                Assert(Directory.GetFiles(f.Music, "*.tmp-*").Single() == foreign
                    && File.ReadAllText(foreign) == "personal copy" && File.ReadAllText(index) == original,
                    point + " left incomplete data or touched an external file");
                using var reopened = f.Open(); await reopened.RecoverPendingAsync();
                Assert(!File.Exists(f.SaveJournal) && !f.HasRemovalJournal && reopened.Read().Count == (saving ? 0 : 1),
                    point + " interrupted the previously committed index or left recovery incomplete");
            }
        });
        await Run("SecurityOffline0317: save recovery preserves a replacement even with matching audio bytes", async () =>
        {
            using var f = new SecurityOfflineFixture("save-replacement", count: 0);
            await SecurityOfflineCrash(f, "save-after-audio-publish", save: true);
            var published = Directory.GetFiles(f.Music, "*.mp3").Single(path => path != f.Foreign);
            var moved = Path.Combine(f.Root, "retained-fixture.mp3");
            File.Move(published, moved);
            File.Copy(moved, published);
            var media = SecurityOfflineSaveMedia();
            using var reopened = f.Open(media); await reopened.RecoverPendingAsync();
            Assert(reopened.Read().Count == 0 && File.Exists(published) && !File.Exists(f.SaveJournal), "recovery claimed a replacement file");
            var saved = await reopened.SaveAsync(SecurityOfflineSaveTrack(), SecurityOfflineSaveAudio(), default);
            Assert(reopened.Read().Count == 1 && Path.Combine(f.Music, saved.FileName) != published && media.Seen.Count == 1
                && SecurityOfflineHash(published) == SecurityOfflineHash(moved), "retry replaced unrelated audio");
        });
        await Run("SecurityOffline0317: unsupported persistent file identity keeps normal saves and preserves unproven files", async () =>
        {
            foreach (var code in new[] { 50, 87, 5, 1117 })
            {
                using var f = new SecurityOfflineFixture("save-file-id-" + code, count: 0);
                using var library = f.Open(SecurityOfflineSaveMedia());
                library.FileIdentityErrorForChecks = code;
                if (code is 50 or 87)
                {
                    var saved = await library.SaveAsync(SecurityOfflineSaveTrack(), SecurityOfflineSaveAudio(), default);
                    Assert(library.Tracks().Single().LocalPath == Path.Combine(f.Music, saved.FileName), "unsupported ID prevented normal saving");
                }
                else
                {
                    var error = await Throws<IOException>(() => library.SaveAsync(SecurityOfflineSaveTrack(), SecurityOfflineSaveAudio(), default));
                    Assert(error.InnerException is System.ComponentModel.Win32Exception native && native.NativeErrorCode == code
                        && library.Read().Count == 0 && Directory.GetFiles(f.Music, "*.mp3").Single() == f.Foreign,
                        "access or device error was treated as unsupported identity");
                }
                Assert(!File.Exists(f.SaveJournal) && !Directory.GetFiles(f.Music, ".ym-audio-*.partial").Any(), "save left a partial or journal");
            }

            using var interrupted = new SecurityOfflineFixture("save-without-id-crash", count: 0);
            await SecurityOfflineCrash(interrupted, "save-after-audio-publish", save: true, unsupportedIdentity: true);
            var unproven = Directory.GetFiles(interrupted.Music, "*.mp3").Single(path => path != interrupted.Foreign);
            var originalHash = SecurityOfflineHash(unproven);
            using var reopened = interrupted.Open(SecurityOfflineSaveMedia());
            await reopened.RecoverPendingAsync();
            Assert(reopened.Read().Count == 0 && SecurityOfflineHash(unproven) == originalHash && !File.Exists(interrupted.SaveJournal),
                "recovery altered or indexed a file without remembered identity");
            reopened.FileIdentityErrorForChecks = 50;
            var retried = await reopened.SaveAsync(SecurityOfflineSaveTrack(), SecurityOfflineSaveAudio(), default);
            Assert(reopened.Read().Count == 1 && Path.Combine(interrupted.Music, retried.FileName) != unproven
                && SecurityOfflineHash(unproven) == originalHash, "retry replaced the unproven file");
        });
        await Run("SecurityOffline0317: oversized indexes are refused and preserved", async () =>
        {
            foreach (var name in new[] { "library.index.json", "playlists.index.json" })
            {
                using var f = new SecurityOfflineFixture("index-bound-" + name, count: 1);
                var path = Path.Combine(f.Music, name);
                using (var stream = File.Open(path, FileMode.Open, FileAccess.Write)) stream.SetLength(OfflineLibrary.MaximumIndexBytes + 1L);
                var audio = Path.Combine(f.Music, "track-0.mp3");
                var hash = SecurityOfflineHash(audio);
                await Throws<IOException>(() => f.Library.ClearAsync(default));
                Assert(new FileInfo(path).Length == OfflineLibrary.MaximumIndexBytes + 1L && SecurityOfflineHash(audio) == hash
                    && !f.HasRemovalJournal, "oversized index was interpreted as empty or overwritten");
            }
        });
        await Run("SecurityOffline0317: invalid save metadata is rejected before download; zero bitrate FLAC is valid", async () =>
        {
            var root = Path.Combine(_directory, "security-offline-0317", "save-validation-" + Guid.NewGuid().ToString("N"));
            var music = Path.Combine(root, "music");
            var media = SecurityOfflineSaveMedia();
            using var library = new OfflineLibrary(music, new AudioBuffer(Path.Combine(root, "buffer"), media));
            await Throws<MusicException>(() => library.SaveAsync(new TrackRow { Id = "fixture", RequestId = "fixture", DurationMs = -1 }, SecurityOfflineSaveAudio(), default));
            await Throws<MusicException>(() => library.SaveAsync(SecurityOfflineSaveTrack(), SecurityOfflineSaveAudio() with { Bitrate = -1 }, default));
            await Throws<MusicException>(() => library.SaveAsync(new TrackRow { Id = "fixture", RequestId = "" }, SecurityOfflineSaveAudio(), default));
            Assert(!Directory.Exists(root) && media.Seen.Count == 0, "invalid metadata touched the offline store or downloaded audio");
            var flac = await library.SaveAsync(SecurityOfflineSaveTrack(), SecurityOfflineSaveAudio() with { Codec = "flac", Bitrate = 0 }, default);
            Assert(flac.Bitrate == 0 && flac.FileName.EndsWith(".flac", StringComparison.Ordinal)
                && library.Find(SecurityOfflineSaveTrack())?.FileName == flac.FileName,
                "valid lossless audio with service bitrate 0 was rejected or could not be read back");
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
        await Run("SecurityOffline0317: replay reconciles alternate names already committed to the index", async () =>
        {
            foreach (var change in new[] { "collision-cleared", "renamed-again", "audio-removed" })
            {
                using var f = new SecurityOfflineFixture("committed-alternate-" + change, count: 1);
                var original = Path.Combine(f.Music, "track-0.mp3");
                var originalHash = SecurityOfflineHash(original);
                await SecurityOfflineCrash(f, "after-stage");
                File.WriteAllText(original, "temporary collision");
                await SecurityOfflineCrash(f, "recovery-after-index", recover: true);
                var indexPath = Path.Combine(f.Music, "library.index.json");
                var indexed = JsonSerializer.Deserialize<OfflineItem[]>(File.ReadAllText(indexPath))!.Single();
                var alternate = Path.Combine(f.Music, indexed.FileName);
                Assert(indexed.FileName.StartsWith(".ym-recovered-", StringComparison.Ordinal)
                    && SecurityOfflineHash(alternate) == originalHash && File.Exists(f.Journal), "fixture did not commit the alternate name");

                if (change == "audio-removed") File.Delete(alternate);
                else File.Delete(original);
                if (change == "renamed-again") await SecurityOfflineCrash(f, "recovery-after-restore", recover: true);

                using var reopened = f.Open(); await reopened.RecoverPendingAsync();
                var tracks = reopened.Tracks();
                if (change == "audio-removed")
                    Assert(tracks.Count == 0 && File.ReadAllText(original) == "temporary collision", "replay retained an obsolete alternate or adopted unrelated audio");
                else
                    Assert(tracks.Single().LocalPath == original && SecurityOfflineHash(original) == originalHash,
                        "replay duplicated an index entry or retained the obsolete path");
                Assert(!File.Exists(alternate) && !File.Exists(f.Journal) && File.Exists(f.Foreign), "replay left recovery files or changed unrelated audio");
            }
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
        await Run("SecurityOffline0317: relocated recovery directory preserves bytes and explains the original location requirement", async () =>
        {
            using var f = new SecurityOfflineFixture("relocated-journal", count: 1);
            var initial = SecurityOfflineHash(Path.Combine(f.Music, "track-0.mp3"));
            await SecurityOfflineCrash(f, "after-stage");
            var relocated = Path.GetFullPath(Path.Combine(f.Root, "relocated-documents", "Music"));
            Assert(UninstallPathGuard.Contains(f.Root, Path.GetFullPath(f.Music))
                && UninstallPathGuard.Contains(f.Root, relocated), "relocation fixture escaped its root");
            Directory.CreateDirectory(Path.GetDirectoryName(relocated)!);
            Directory.Move(f.Music, relocated);
            try
            {
                var before = Directory.GetFiles(relocated).ToDictionary(path => path, SecurityOfflineHash);
                using var moved = new OfflineLibrary(relocated, new AudioBuffer(Path.Combine(f.Root, "relocation-buffer")));
                var error = await Throws<MusicException>(() => moved.RecoverPendingAsync());
                var passive = await Throws<MusicException>(() => Task.FromResult(moved.Read()));
                Assert(error.Message == OfflineLibrary.RecoveryAuthenticationFailure && passive.Message == error.Message
                    && Directory.GetFiles(relocated).Length == before.Count
                    && before.All(pair => SecurityOfflineHash(pair.Key) == pair.Value), "relocation changed files or hid the recovery instructions");
            }
            finally { Directory.Move(relocated, f.Music); }
            await f.Library.RecoverPendingAsync();
            Assert(f.Library.Tracks().Single().Available && SecurityOfflineHash(Path.Combine(f.Music, "track-0.mp3")) == initial
                && !f.HasRemovalJournal, "the original guarded location could not complete the recovery");
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
    // --security-offline-child. Arguments: fixtureRoot, checkpoint, [recover|save|save-unsupported|remove-unsupported].
    private static int SecurityOffline0317Child(string[] args)
    {
        if (args.Length is < 2 or > 3) return 2;
        var root = Path.GetFullPath(args[0]);
        if (!root.Contains(Path.DirectorySeparatorChar + "security-offline-0317" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || File.ReadAllText(Path.Combine(root, ".security-fixture")) != "Yamuse.SecurityOffline0317.Fixture") return 2;
        var recovering = args.Length == 3 && args[2] == "recover";
        var saving = args.Length == 3 && args[2] is "save" or "save-unsupported";
        var music = Path.Combine(root, "documents", "Yamuse", "Music");
        var journal = Path.Combine(music, SecurityOfflineJournal);
        if (recovering && !File.Exists(journal)) journal = Path.Combine(music, SecurityOfflineCommittedJournal);
        var pausedJournal = Path.Combine(root, "child-fixture-journal");
        // Install the fixture hook before scheduling any recovery worker. Only
        // this isolated test process temporarily renames its own known journal.
        if (recovering) File.Move(journal, pausedJournal);
        using var library = new OfflineLibrary(music, new AudioBuffer(Path.Combine(root, "buffer"), saving ? SecurityOfflineSaveMedia() : null));
        library.RemovalCheckpoint = point => { if (point == args[1]) Environment.Exit(73); };
        library.SaveCheckpoint = library.RemovalCheckpoint;
        library.TemporaryPublicationCheckpoint = library.RemovalCheckpoint;
        if (args.Length == 3 && args[2] is "save-unsupported" or "remove-unsupported") library.FileIdentityErrorForChecks = 50;
        if (recovering) { File.Move(pausedJournal, journal); library.RecoverPendingAsync().GetAwaiter().GetResult(); }
        else if (saving) library.SaveAsync(SecurityOfflineSaveTrack(), SecurityOfflineSaveAudio(), default).GetAwaiter().GetResult();
        else library.ClearAsync(default, new SecurityOfflineObserver(p => { if (args[1] == "after-delete" && p.Phase == "delete") Environment.Exit(73); })).GetAwaiter().GetResult();
        return 3; // Expected checkpoint was not reached.
    }

    private static async Task SecurityOfflineCrash(SecurityOfflineFixture fixture, string point, bool recover = false, bool save = false, bool unsupportedIdentity = false)
    {
        var processPath = Environment.ProcessPath!;
        var start = new ProcessStartInfo(processPath) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = fixture.Root };
        if (Path.GetFileNameWithoutExtension(processPath).Equals("dotnet", StringComparison.OrdinalIgnoreCase)) start.ArgumentList.Add(typeof(Program).Assembly.Location);
        start.ArgumentList.Add("--security-offline-child"); start.ArgumentList.Add(fixture.Root); start.ArgumentList.Add(point);
        if (recover) start.ArgumentList.Add("recover");
        else if (save) start.ArgumentList.Add(unsupportedIdentity ? "save-unsupported" : "save");
        else if (unsupportedIdentity) start.ArgumentList.Add("remove-unsupported");
        using var child = Process.Start(start) ?? throw new IOException("Failed to start isolated recovery child.");
        await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
        Assert(child.ExitCode == 73, "isolated crash checkpoint failed: " + point + ", exit=" + child.ExitCode);
    }

    private static string SecurityOfflineHash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    private static TrackRow SecurityOfflineSaveTrack() => new() { Id = "save-fixture", RequestId = "save-fixture", Title = "Saved fixture", Available = true };
    private static AudioDescriptor SecurityOfflineSaveAudio() => new(new Uri("https://s1.vlx.yandex.net/save-fixture.mp3"), "mp3", 320);
    private static FakeHandler SecurityOfflineSaveMedia() => new((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
    { Content = new ByteArrayContent(Enumerable.Repeat((byte)0x47, 4096).ToArray()) }));
    private sealed class SecurityOfflineObserver(Action<OfflineCleanupProgress> action) : IProgress<OfflineCleanupProgress>
    { public void Report(OfflineCleanupProgress value) => action(value); }

    private sealed class SecurityOfflineFixture : IDisposable
    {
        internal string Root { get; }
        internal string Music => Path.Combine(Root, "documents", "Yamuse", "Music");
        internal string Journal => Path.Combine(Music, SecurityOfflineJournal);
        internal string CommittedJournal => Path.Combine(Music, SecurityOfflineCommittedJournal);
        internal string SaveJournal => Path.Combine(Music, SecurityOfflineSaveJournal);
        internal bool HasRemovalJournal => File.Exists(Journal) || File.Exists(CommittedJournal);
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
        internal OfflineLibrary Open(HttpMessageHandler? handler = null) => new(Music, new AudioBuffer(Path.Combine(Root, "buffer"), handler));
        public void Dispose() => Library.Dispose(); // Preserve bounded evidence; never recursively delete a fixture with links.
    }
}
