using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Nodes;
using Windows.Media.MediaProperties;
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
        await Run("SecurityOffline0317: interrupted live rollback on a filesystem without stable IDs preserves the recovered row", async () =>
        {
            using var f = new SecurityOfflineFixture("live-rollback-without-id", count: 1);
            var original = Path.Combine(f.Music, "track-0.mp3");
            var digest = SecurityOfflineHash(original);
            await SecurityOfflineCrash(f, "live-rollback-after-index", unsupportedIdentity: true);
            Assert(!File.Exists(original) && File.Exists(f.CommittedJournal), "fixture did not interrupt the committed live rollback");
            var repaired = JsonSerializer.Deserialize<OfflineItem[]>(File.ReadAllText(Path.Combine(f.Music, "library.index.json")))!.Single();
            Assert(repaired.FileName.StartsWith(".ym-recovered-", StringComparison.Ordinal), "live rollback did not record the authenticated alternate name");
            var recovered = Path.Combine(f.Music, repaired.FileName);
            using var reopened = f.Open(); await reopened.RecoverPendingAsync();
            Assert(reopened.Tracks().Single().LocalPath == recovered && SecurityOfflineHash(recovered) == digest
                && !f.HasRemovalJournal && reopened.GetPlaylists().Count == 0, "replay lost the audio or reversed a committed playlist clear");
        });
        await Run("SecurityOffline0317: committed legacy rollback retains only already indexed verified audio at the original name", async () =>
        {
            foreach (var condition in new[] { "verified", "missing-row", "changed-audio" })
            {
                using var f = new SecurityOfflineFixture("old-no-id-" + condition, count: 1);
                var original = Path.Combine(f.Music, "track-0.mp3");
                var index = Path.Combine(f.Music, "library.index.json");
                var indexed = File.ReadAllText(index);
                var digest = SecurityOfflineHash(original);
                await SecurityOfflineCrash(f, "after-library-commit", unsupportedIdentity: true);
                var quarantine = Directory.GetFiles(f.Music, ".ym-delete-*.pending").Single(path => path != f.ForeignPending);
                File.Move(quarantine, original); // The 0.7.0 live rollback used the original name.
                if (condition != "missing-row") File.WriteAllText(index, indexed);
                if (condition == "changed-audio") File.WriteAllBytes(original, Enumerable.Repeat((byte)0x61, 4096).ToArray());
                var expected = SecurityOfflineHash(original);

                using var reopened = f.Open(); await reopened.RecoverPendingAsync();
                var tracks = reopened.Tracks();
                Assert(tracks.Count == (condition == "verified" ? 1 : 0) && !f.HasRemovalJournal
                    && reopened.GetPlaylists().Count == 0 && SecurityOfflineHash(original) == expected,
                    condition + " changed the bytes or trusted an unproven row");
                if (condition == "verified") Assert(tracks.Single().LocalPath == original && expected == digest, "verified historical row was not kept");
            }
        });
        await Run("SecurityOffline0317: single and bulk removal preserve indexed audio with named streams", async () =>
        {
            foreach (var clear in new[] { false, true })
            {
                using var f = new SecurityOfflineFixture("named-stream-indexed-" + clear, count: 2);
                var original = Path.Combine(f.Music, "track-0.mp3");
                var digest = SecurityOfflineHash(original);
                WriteSecurityOfflineNamedStream(original);
                var removed = clear ? await f.Library.ClearAsync(default) : await f.Library.RemoveTrackAsync("r3-0", default);
                Assert(removed.Removed == (clear ? 1 : 0) && removed.Preserved == 1 && removed.Failed == 0
                    && f.Library.Read().Count == (clear ? 0 : 1) && SecurityOfflineHash(original) == digest
                    && File.ReadAllText(original + SecurityOfflineNamedStream) == "personal fixture metadata", "removal lost indexed audio or its named stream");
            }
        });
        await Run("SecurityOffline0317: a named stream added after staging survives live cleanup and replay", async () =>
        {
            foreach (var replay in new[] { false, true })
            {
                using var f = new SecurityOfflineFixture("named-stream-staged-" + replay, count: 1);
                var original = Path.Combine(f.Music, "track-0.mp3");
                var digest = SecurityOfflineHash(original);
                string Quarantine() => Directory.GetFiles(f.Music, ".ym-delete-*.pending").Single(path => path != f.ForeignPending);
                if (replay)
                {
                    await SecurityOfflineCrash(f, "after-library-commit");
                    WriteSecurityOfflineNamedStream(Quarantine());
                    await f.Library.RecoverPendingAsync();
                }
                else
                {
                    f.Library.RemovalCheckpoint = point =>
                    {
                        if (point == "after-library-commit") WriteSecurityOfflineNamedStream(Quarantine());
                    };
                    var result = await f.Library.ClearAsync(default);
                    Assert(result.Preserved == 1 && result.Removed == 0, "live cleanup did not preserve the newly modified audio");
                }
                Assert(f.Library.Read().Count == 0 && File.ReadAllText(original + SecurityOfflineNamedStream) == "personal fixture metadata"
                    && SecurityOfflineHash(original) == digest && !f.HasRemovalJournal, "staged audio lost a stream or was taken back into the library");
            }
        });
        await Run("SecurityOffline0317: recovery preserves a named stream added to an unindexed saved file", async () =>
        {
            using var f = new SecurityOfflineFixture("named-stream-save-recovery", count: 0);
            await SecurityOfflineCrash(f, "save-after-audio-publish", save: true);
            var published = Directory.GetFiles(f.Music, "*.mp3").Single(path => path != f.Foreign);
            var digest = SecurityOfflineHash(published);
            WriteSecurityOfflineNamedStream(published);
            using var reopened = f.Open(); await reopened.RecoverPendingAsync();
            Assert(reopened.Read().Count == 0 && !File.Exists(f.SaveJournal) && SecurityOfflineHash(published) == digest
                && File.ReadAllText(published + SecurityOfflineNamedStream) == "personal fixture metadata", "rollback deleted a user's named stream or took over the file");
        });
        await Run("SecurityOffline0317: both index replacements preserve DACL and compression and refuse named streams", async () =>
        {
            using var f = new SecurityOfflineFixture("index-metadata", count: 0);
            using var library = f.Open(SecurityOfflineSaveMedia());
            var paths = new[] { Path.Combine(f.Music, "library.index.json"), Path.Combine(f.Music, "playlists.index.json") };
            var expectedDacls = new Dictionary<string, string>();
            var drive = Path.GetPathRoot(f.Music)!;
            var supportsCompression = new DriveInfo(drive).DriveFormat == "NTFS"
                && U317GetVolumeInformation(drive, IntPtr.Zero, 0, out _, out _, out var flags, IntPtr.Zero, 0) && (flags & 0x10) != 0;
            foreach (var path in paths)
            {
                var info = new FileInfo(path);
                var security = info.GetAccessControl(AccessControlSections.Access);
                security.SetAccessRuleProtection(isProtected: true, preserveInheritance: true);
                security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier("S-1-5-21-111111111-222222222-333333333-4444"),
                    FileSystemRights.ReadData, AccessControlType.Deny));
                info.SetAccessControl(security);
                expectedDacls.Add(path, Dacl(path));
                if (supportsCompression)
                {
                    var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "compact.exe"))
                    { UseShellExecute = false, CreateNoWindow = true };
                    start.ArgumentList.Add("/C"); start.ArgumentList.Add("/Q"); start.ArgumentList.Add(path);
                    using var process = Process.Start(start) ?? throw new IOException("Could not start fixture compression.");
                    Assert(process.WaitForExit(5000) && process.ExitCode == 0
                        && File.GetAttributes(path).HasFlag(FileAttributes.Compressed), "could not compress fixture index");
                }
            }
            var track = SecurityOfflineSaveTrack();
            var snapshot = new PlaylistDetails("fixture", "1", "Protected index fixture", "", 2, [track]);
            await library.RememberPlaylistAsync(snapshot, AudioQuality.Excellent, default);
            await library.SaveAsync(track, SecurityOfflineSaveAudio(), default);
            Assert(paths.All(path => Dacl(path) == expectedDacls[path]
                && (!supportsCompression || File.GetAttributes(path).HasFlag(FileAttributes.Compressed))), "replacement lost DACL or compression");

            var audio = Path.Combine(f.Music, library.Read().Single().FileName);
            foreach (var path in paths)
            {
                var before = File.ReadAllText(path);
                if (path == paths[0])
                {
                    library.TemporaryPublicationCheckpoint = point =>
                    {
                        if (point == "index-before-publish") WriteSecurityOfflineNamedStream(path);
                    };
                    try
                    {
                        await library.SaveAsync(new TrackRow { Id = "new", RequestId = "new", Available = true }, SecurityOfflineSaveAudio(), default);
                        throw new InvalidOperationException("Index replacement accepted a late named stream.");
                    }
                    catch (Exception error) when (error is IOException or MusicException) { }
                    finally { library.TemporaryPublicationCheckpoint = null; }
                }
                else
                {
                    WriteSecurityOfflineNamedStream(path);
                    await Throws<IOException>(() => library.RememberPlaylistAsync(snapshot with { Revision = 3 }, AudioQuality.Excellent, default));
                }
                Assert(File.ReadAllText(path) == before && File.ReadAllText(path + SecurityOfflineNamedStream) == "personal fixture metadata"
                    && File.Exists(audio) && !File.Exists(f.SaveJournal), "protected index or an existing audio file was modified");
            }

            static string Dacl(string path) => new FileInfo(path).GetAccessControl(AccessControlSections.Access)
                .GetSecurityDescriptorSddlForm(AccessControlSections.Access);
        });
        await Run("SecurityOffline0317: offline playlist completion accepts a local track marked unavailable by the service", async () =>
        {
            using var f = new SecurityOfflineFixture("offline-unavailable-metadata", count: 0);
            using var library = f.Open(SecurityOfflineSaveMedia());
            var track = SecurityOfflineSaveTrack();
            await library.SaveAsync(track, SecurityOfflineSaveAudio(), default);
            var unavailable = new TrackRow { Id = track.Id, RequestId = track.RequestId, Title = track.Title, Available = false };
            await library.RememberPlaylistAsync(new PlaylistDetails("fixture", "1", "Local availability", "", 2, [unavailable]), AudioQuality.Excellent, default);
            var remembered = library.GetPlaylistStatus("fixture", "1");
            var inspected = library.GetPlaylistStatus("fixture", "1", [unavailable]);
            Assert(remembered is { Complete: true, Downloaded: 1 } && inspected == remembered
                && library.GetPlaylistTracks("fixture", "1").Single().CanPlay, "offline playlist counted catalog availability instead of its local file");
        });
        await Run("SecurityOffline0317: implicit local playback uses service quality tiers inside the exact request group", async () =>
        {
            using var f = new SecurityOfflineFixture("local-quality-tiers", count: 0);
            var m4aBytes = await EncodeAudioFixtureAsync("m4a", MediaEncodingProfile.CreateM4a(AudioEncodingQuality.High));
            var flacBytes = await EncodeAudioFixtureAsync("flac", MediaEncodingProfile.CreateFlac(AudioEncodingQuality.High));
            using var library = f.Open(SecurityOfflineSaveMedia(m4aBytes, flacBytes));
            TrackRow Edition(string album, string? chosenPath = null) => new()
            { Id = "tier", RequestId = "tier:" + album, AlbumId = album, Title = "Fixture", Available = true, LocalPath = chosenPath };
            var audio = SecurityOfflineSaveAudio();
            var edition = Edition("first");
            var mp3 = await library.SaveAsync(edition, audio with { Uri = new Uri("https://s1.vlx.yandex.net/160.mp3"), Bitrate = 160 }, default);
            var aac = await library.SaveAsync(edition, audio with { Uri = new Uri("https://s1.vlx.yandex.net/save-fixture.m4a"), Codec = "aac-mp4", Bitrate = 256 }, default);
            var flac = await library.SaveAsync(Edition("second"), audio with { Uri = new Uri("https://s1.vlx.yandex.net/save-fixture.flac"), Codec = "flac", Bitrate = 0 }, default);
            var mp3Path = library.ResolvePath(mp3.FileName);
            Assert(library.Find(edition)?.FileName == aac.FileName && library.Find(Edition("unknown"))?.FileName == flac.FileName
                && library.ResolvePlaybackSource(Edition("first", mp3Path))?.Path == mp3Path,
                "tier ordering displaced the exact edition or an explicitly selected file");
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
                    if (change == "external-index")
                    {
                        var written = File.GetLastWriteTimeUtc(index);
                        File.WriteAllText(index, "[]");
                        File.SetLastWriteTimeUtc(index, written);
                    }
                    if (change == "foreign-audio") File.WriteAllBytes(audio, Enumerable.Repeat((byte)0x77, 4096).ToArray());
                }
                using var reopened = f.Open(); await reopened.RecoverPendingAsync();
                Assert(reopened.Read().Count == (change == "live-rollback" ? 1 : 0) && !f.HasRemovalJournal,
                    change + " restored metadata without evidence or lost it on an uncommitted own write");
                if (change == "external-index") Assert(File.ReadAllText(index) == "[]", "external index revision was overwritten");
                if (change == "foreign-audio") Assert(File.ReadAllBytes(audio).All(value => value == 0x77), "foreign file was changed");
            }
        });
        await Run("SecurityOffline0317: an independent empty audio index restores verified audio without reviving missing metadata", async () =>
        {
            foreach (var live in new[] { true, false })
            {
                using var f = new SecurityOfflineFixture("unstaged-external-empty-" + live, count: 2);
                var missing = Path.Combine(f.Music, "track-0.mp3");
                var proved = Path.Combine(f.Music, "track-1.mp3");
                var digest = SecurityOfflineHash(proved);
                var index = Path.Combine(f.Music, "library.index.json");
                File.Delete(missing);
                if (live)
                {
                    f.Library.RemovalCheckpoint = point =>
                    {
                        if (point == "after-playlist-commit") File.WriteAllText(index, "[]");
                    };
                    await Throws<MusicException>(() => f.Library.ClearAsync(default));
                }
                else
                {
                    await SecurityOfflineCrash(f, "after-playlist-commit");
                    File.WriteAllText(index, "[]");
                }
                await f.Library.RecoverPendingAsync();
                Assert(f.Library.Read().Single().FileName == "track-1.mp3" && SecurityOfflineHash(proved) == digest
                    && !File.Exists(missing) && !f.HasRemovalJournal && f.Library.GetPlaylists().Count == 1,
                    "rollback revived absent metadata from an independent empty revision or lost verified audio");
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
        await Run("SecurityOffline0317: startup stays available when probing recovery is temporarily denied", async () =>
        {
            using var f = new SecurityOfflineFixture("probe-denied", count: 1);
            await SecurityOfflineCrash(f, "after-stage");
            var deny = true;
            using var reopened = new OfflineLibrary(f.Music, new AudioBuffer(Path.Combine(f.Root, "buffer")),
                beforeRecoveryProbeForChecks: () => { if (deny) throw new UnauthorizedAccessException("Fixture probe denied."); });
            Assert(reopened.IsAvailable && File.Exists(f.Journal), "construction lost the pending recovery");
            var guarded = await Throws<MusicException>(() => Task.FromResult(reopened.Read()));
            Assert(guarded.OfflineStorageRetryable, "read bypassed a failed probe or lost its retry state");
            await Throws<IOException>(() => reopened.RecoverPendingAsync());
            deny = false;
            await reopened.RecoverPendingAsync();
            Assert(reopened.Tracks().Single().Available && !f.HasRemovalJournal, "reopening did not retry the failed probe");
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
                            Control<System.Windows.Controls.ComboBox>(window, "QualityCombo").SelectedIndex = 2;
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
            var bytes = SilentMp3Bytes(4096);
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
        await Run("SecurityOffline0317: save reuse pins verified audio and rechecks the exact index row", async () =>
        {
            using var f = new SecurityOfflineFixture("verified-reuse", count: 0);
            var media = SecurityOfflineSaveMedia();
            using var library = f.Open(media);
            var track = SecurityOfflineSaveTrack();
            var audio = SecurityOfflineSaveAudio();
            var saved = await library.SaveAsync(track, audio, default);
            var row = saved with { Sha256 = saved.Sha256.ToUpperInvariant() };
            var path = Path.Combine(f.Music, saved.FileName);
            var index = Path.Combine(f.Music, "library.index.json");
            File.WriteAllText(index, JsonSerializer.Serialize(new[] { row }));
            var writeBlocked = false;
            library.SaveCheckpoint = point =>
            {
                if (point != "save-existing-verified") return;
                try { File.WriteAllBytes(path, Enumerable.Repeat((byte)0x71, checked((int)saved.Bytes)).ToArray()); }
                catch (IOException) { writeBlocked = true; }
            };
            var reused = await library.SaveAsync(track, audio, default);
            Assert(writeBlocked && media.Seen.Count == 1 && reused.FileName == saved.FileName
                && SecurityOfflineHash(path) == row.Sha256, "reuse lost the held file or did not accept uppercase digest");

            var external = JsonSerializer.Serialize(new[] { row with { Title = "Revised fixture" } });
            library.SaveCheckpoint = point =>
            {
                if (point == "save-existing-verified") File.WriteAllText(index, external);
            };
            await Throws<MusicException>(() => library.SaveAsync(track, audio, default));
            Assert(File.ReadAllText(index) == external && SecurityOfflineHash(path) == row.Sha256, "reuse accepted or overwrote a different index revision");
        });
        await Run("SecurityOffline0317: verified local lookup checks contents before applying the accepted quality", async () =>
        {
            using var f = new SecurityOfflineFixture("verified-lookup", count: 0);
            using var library = f.Open(SecurityOfflineSaveMedia());
            var track = SecurityOfflineSaveTrack();
            var audio = SecurityOfflineSaveAudio();
            var preferred = await library.SaveAsync(track, audio, default);
            var fallback = await library.SaveAsync(track,
                audio with { Uri = new Uri("https://s1.vlx.yandex.net/128.mp3"), Bitrate = 128 }, default);
            var path = library.ResolvePath(preferred.FileName);
            var changed = Enumerable.Repeat((byte)0x61, checked((int)preferred.Bytes)).ToArray();
            File.WriteAllBytes(path, changed);
            Assert(library.Find(track)?.FileName == preferred.FileName, "metadata-only fixture did not retain the matching length");
            using (File.Open(path, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                Assert((await library.FindVerifiedAsync(track, default, failIfUnavailable: true))?.FileName == fallback.FileName,
                    "temporary lock hid another verified quality");
                await Throws<IOException>(() => library.FindVerifiedAsync(track, default,
                    item => AudioQualityPolicy.IsSufficient(item.Codec, item.QualityBitrate, AudioQuality.Excellent), failIfUnavailable: true));
            }
            Assert((await library.FindVerifiedAsync(track, default))?.FileName == fallback.FileName
                && await library.FindVerifiedAsync(track, default, item => AudioQualityPolicy.IsSufficient(item.Codec, item.QualityBitrate, AudioQuality.Excellent)) is null
                && File.ReadAllBytes(path).SequenceEqual(changed), "verified lookup accepted changed audio or ignored the quality predicate");
        });
        await Run("SecurityOffline0317: save does not redownload while the existing exact file is busy", async () =>
        {
            using var f = new SecurityOfflineFixture("save-existing-busy", count: 0);
            var media = SecurityOfflineSaveMedia();
            using var library = f.Open(media);
            var track = SecurityOfflineSaveTrack();
            var descriptor = SecurityOfflineSaveAudio();
            var saved = await library.SaveAsync(track, descriptor, default);
            var path = library.ResolvePath(saved.FileName);
            var index = Path.Combine(f.Music, "library.index.json");
            var bytes = File.ReadAllText(index);
            using (File.Open(path, FileMode.Open, FileAccess.Read, FileShare.None))
                await Throws<IOException>(() => library.SaveAsync(track, descriptor, default));
            var retried = await library.SaveAsync(track, descriptor, default);
            Assert(media.Seen.Count == 1 && retried.FileName == saved.FileName && File.ReadAllText(index) == bytes
                && SecurityOfflineHash(path).Equals(saved.Sha256, StringComparison.OrdinalIgnoreCase), "busy audio was replaced or redownloaded");
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
        await Run("SecurityOffline0317: an interrupted index publication recovers before the audio receipt", async () =>
        {
            foreach (var point in new[] { "index-after-publication-receipt", "index-after-park", "index-after-rename" })
            {
                using var f = new SecurityOfflineFixture("index-replay-" + point, count: 0);
                var index = Path.Combine(f.Music, "library.index.json");
                var receipt = Path.Combine(f.Music, ".ym-index.recovery");
                var previous = File.ReadAllText(index);
                await SecurityOfflineCrash(f, point, save: true);
                Assert(File.Exists(receipt) && (File.Exists(index) == (point != "index-after-park")),
                    point + " did not reach the expected publication phase");
                await f.Library.RecoverPendingAsync();
                var committed = point == "index-after-rename";
                var restored = f.Library.Read();
                Assert(restored.Count == (committed ? 1 : 0) && (committed || File.ReadAllText(index) == previous)
                    && Directory.GetFiles(f.Music, "*.mp3").Length == (committed ? 2 : 1)
                    && !File.Exists(receipt) && !File.Exists(f.SaveJournal) && Directory.GetFiles(f.Music, "*.previous-*").Length == 0
                    && Directory.GetFiles(f.Music, "*.tmp-*").Length == 0 && File.Exists(f.Foreign),
                    point + " lost an index, its recorded audio, or left recovery incomplete");
                if (committed) Assert(SecurityOfflineHash(f.Library.ResolvePath(restored.Single().FileName))
                    .Equals(restored.Single().Sha256, StringComparison.OrdinalIgnoreCase), "replay lost the new index's audio");
            }
        });
        await Run("SecurityOffline0317: an arriving index is preserved with the intact previous copy", async () =>
        {
            using var f = new SecurityOfflineFixture("index-publish-external", count: 1);
            using var writer = f.Open(SecurityOfflineSaveMedia());
            var index = Path.Combine(f.Music, "library.index.json");
            var previous = File.ReadAllText(index);
            var item = JsonSerializer.Deserialize<OfflineItem[]>(previous)!.Single();
            var outside = JsonSerializer.Serialize(new[] { item with { Title = "Externally edited title" } });
            writer.TemporaryPublicationCheckpoint = point =>
            {
                if (point == "index-after-park")
                {
                    Assert(!File.Exists(index), "publication did not park its exact previous copy");
                    using var destination = new FileStream(index, FileMode.CreateNew, FileAccess.Write);
                    using var text = new StreamWriter(destination);
                    text.Write(outside);
                }
            };
            await Throws<IOException>(() => writer.SaveAsync(SecurityOfflineSaveTrack(), SecurityOfflineSaveAudio(), default));
            await f.Library.RecoverPendingAsync();
            Assert(f.Library.Read().Single().Title == "Externally edited title" && File.ReadAllText(index) == outside
                && File.ReadAllText(Directory.GetFiles(f.Music, "*.previous-*").Single()) == previous
                && Directory.GetFiles(f.Music, ".ym-index.recovery.preserved-*").Length == 1 && !File.Exists(f.SaveJournal)
                && File.Exists(f.Foreign), "publication overwrote the arriving index or lost the previous copy");
        });
        await Run("SecurityOffline0317: a held previous index blocks only when the authoritative name is missing", async () =>
        {
            foreach (var mode in new[] { "published", "independent", "missing" })
            {
                using var f = new SecurityOfflineFixture("busy-index-previous-" + mode, count: 1);
                var index = Path.Combine(f.Music, "library.index.json");
                var original = File.ReadAllText(index);
                var receipt = Path.Combine(f.Music, ".ym-index.recovery");
                await SecurityOfflineCrash(f, mode == "published" ? "index-after-rename" : "index-after-park", save: true);
                var backup = Directory.GetFiles(f.Music, "library.index.json.previous-*").Single();
                if (mode == "independent")
                {
                    var row = JsonSerializer.Deserialize<OfflineItem[]>(original)!.Single();
                    File.WriteAllText(index, JsonSerializer.Serialize(new[] { row with { Title = "Independent original entry" } }));
                }
                using (var held = new FileStream(backup, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    if (mode != "missing")
                    {
                        await f.Library.RecoverPendingAsync();
                        var rows = f.Library.Read();
                        Assert((mode == "published" ? rows.Count == 2 : rows.Single().Title == "Independent original entry")
                            && rows.All(row => SecurityOfflineHash(f.Library.ResolvePath(row.FileName))
                                .Equals(row.Sha256, StringComparison.OrdinalIgnoreCase)) && !File.Exists(receipt) && !File.Exists(f.SaveJournal)
                            && Directory.GetFiles(f.Music, ".ym-index.recovery.preserved-*").Length == 1 && File.ReadAllText(backup) == original,
                            "the verified new index was blocked or the busy backup lost its exact receipt");
                    }
                    else
                    {
                        await Throws<IOException>(() => f.Library.RecoverPendingAsync());
                        Assert(!File.Exists(index) && File.Exists(receipt) && File.Exists(f.SaveJournal) && File.ReadAllText(backup) == original,
                            "missing authoritative index completed before it could restore the held backup");
                    }
                }
                if (mode == "missing")
                {
                    await f.Library.RecoverPendingAsync();
                    Assert(f.Library.Read().Count == 1 && File.ReadAllText(index) == original && !File.Exists(backup)
                        && !File.Exists(receipt) && !File.Exists(f.SaveJournal), "retry did not restore the released backup");
                }
            }
        });
        await Run("SecurityOffline0317: interrupted rollback keeps exact removal proof through a second index parking", async () =>
        {
            foreach (var playlists in new[] { true, false })
            foreach (var externalRewrite in new[] { false, true })
            {
                using var f = new SecurityOfflineFixture($"second-park-{playlists}-{externalRewrite}", count: playlists ? 1 : 2);
                var playlistPath = Path.Combine(f.Music, "playlists.index.json");
                var index = playlists ? playlistPath : Path.Combine(f.Music, "library.index.json");
                if (!playlists)
                {
                    File.WriteAllText(playlistPath, "{\"schemaVersion\":1,\"playlists\":[]}");
                    File.Delete(Path.Combine(f.Music, "track-0.mp3"));
                }
                await SecurityOfflineCrash(f, "before-removal-commit-marker");
                var empty = File.ReadAllText(index);
                var digest = SecurityOfflineHash(index);
                var time = File.GetLastWriteTimeUtc(index);
                var before = ReadRevision(index);
                await SecurityOfflineCrash(f, "index-after-park", recover: true);
                var parked = Directory.GetFiles(f.Music, Path.GetFileName(index) + ".previous-*").Single();
                var moved = ReadRevision(parked);
                Assert(!File.Exists(index) && f.HasRemovalJournal && File.Exists(Path.Combine(f.Music, ".ym-index.recovery"))
                    && SecurityOfflineHash(parked) == digest && File.GetLastWriteTimeUtc(parked) == time
                    && moved.Low == before.Low && moved.High == before.High && moved.Volume == before.Volume && moved.Changed == before.Changed,
                    "the held rollback object did not retain the exact signed revision while parked");
                if (externalRewrite)
                {
                    File.WriteAllText(parked, empty);
                    File.SetLastWriteTimeUtc(parked, time);
                    Assert(SecurityOfflineHash(parked) == digest && File.GetLastWriteTimeUtc(parked) == time && ReadRevision(parked).Changed != before.Changed,
                        "fixture did not produce an independently rewritten revision with the same bytes and write time");
                }
                await f.Library.RecoverPendingAsync();
                Assert(!f.HasRemovalJournal && !File.Exists(Path.Combine(f.Music, ".ym-index.recovery")) && File.Exists(f.Foreign)
                    && f.Library.Read().Count == (playlists || externalRewrite ? 1 : 2)
                    && (playlists ? f.Library.GetPlaylists().Count == (externalRewrite ? 0 : 1) : f.Library.GetPlaylists().Count == 0),
                    "the second restart lost verified rollback evidence or accepted a foreign revision");
            }

            static FileSecurityPreservation.FileRevision ReadRevision(string path)
            {
                using var held = UninstallPathGuard.OpenOwnedFile(path, read: true, deleteAccess: false);
                return FileSecurityPreservation.GetPersistentRevision(held) ?? throw new IOException("Fixture needs a stable file revision.");
            }
        });
        await Run("SecurityOffline0317: live and interrupted index rollback restore the original archive bit", async () =>
        {
            foreach (var playlists in new[] { true, false })
            foreach (var archive in new[] { true, false })
            foreach (var interrupted in new[] { true, false })
            {
                using var f = new SecurityOfflineFixture($"rollback-archive-{playlists}-{archive}-{interrupted}", count: 1);
                var index = Path.Combine(f.Music, playlists ? "playlists.index.json" : "library.index.json");
                File.SetAttributes(index, archive ? FileAttributes.Archive : FileAttributes.Normal);
                var hash = SecurityOfflineHash(index);
                var time = File.GetLastWriteTimeUtc(index);
                if (interrupted) await SecurityOfflineCrash(f, "index-after-park", save: !playlists);
                else
                {
                    f.Library.TemporaryPublicationCheckpoint = point =>
                    {
                        if (point == "index-after-park") throw new IOException("Leave the old revision in place after rollback.");
                    };
                    await Throws<IOException>(() => playlists ? f.Library.ClearAsync(default) : f.Library.RemoveTrackAsync("r3-0", default));
                }
                await f.Library.RecoverPendingAsync();
                Assert(SecurityOfflineHash(index) == hash && File.GetLastWriteTimeUtc(index) == time && f.Library.GetPlaylists().Count == 1
                    && ((File.GetAttributes(index) & FileAttributes.Archive) != 0) == archive && f.Library.Tracks().Single().Available
                    && !f.HasRemovalJournal && Directory.GetFiles(f.Music, "*.previous-*").Length == 0,
                    "returning the prior index changed its archive bit, bytes, or write time");
            }
        });
        await Run("SecurityOffline0317: an unavailable primary file ID still allows archive recovery on the exact NTFS object", async () =>
        {
            foreach (var replace in new[] { false, true })
            {
                using var f = new SecurityOfflineFixture("archive-no-primary-id-" + replace, count: 1);
                var index = Path.Combine(f.Music, "library.index.json");
                File.SetAttributes(index, FileAttributes.Normal);
                var original = File.ReadAllBytes(index);
                var written = File.GetLastWriteTimeUtc(index);
                (uint Volume, ulong FileReference)? identity;
                using (var held = UninstallPathGuard.OpenOwnedFile(index, read: true, deleteAccess: false))
                    identity = FileSecurityPreservation.TryGetNtfsFileReference(held);
                Assert(identity is not null, "fixture requires local NTFS with stable file references");
                await SecurityOfflineCrash(f, "index-after-park", save: true, unsupportedIdentity: true);
                await SecurityOfflineCrash(f, "index-after-restore-before-archive", recover: true, unsupportedIdentity: true);
                var receipt = Path.Combine(f.Music, ".ym-index.recovery");
                Assert(File.Exists(receipt) && File.ReadAllBytes(index).SequenceEqual(original) && File.GetLastWriteTimeUtc(index) == written
                    && (File.GetAttributes(index) & FileAttributes.Archive) != 0, "fixture did not interrupt the returned file before restoring Archive");
                using (var held = UninstallPathGuard.OpenOwnedFile(index, read: true, deleteAccess: false))
                    Assert(FileSecurityPreservation.TryGetNtfsFileReference(held) == identity, "parked source did not return as the same file");
                if (replace)
                {
                    File.Move(index, Path.Combine(f.Root, "prior-index.json"));
                    File.WriteAllBytes(index, original);
                    File.SetLastWriteTimeUtc(index, written);
                    File.SetAttributes(index, FileAttributes.Archive);
                    using var held = UninstallPathGuard.OpenOwnedFile(index, read: true, deleteAccess: false);
                    Assert(FileSecurityPreservation.TryGetNtfsFileReference(held) != identity, "fixture did not substitute an independent file");
                }
                f.Library.FileIdentityErrorForChecks = 50;
                await f.Library.RecoverPendingAsync();
                Assert(((File.GetAttributes(index) & FileAttributes.Archive) != 0) == replace && File.GetLastWriteTimeUtc(index) == written
                    && File.ReadAllBytes(index).SequenceEqual(original) && f.Library.Read().Count == 1 && !File.Exists(receipt),
                    "recovery changed an independent Archive attribute or missed the verified original repair");
            }
        });
        await Run("SecurityOffline0317: index publication supports a filesystem without stable file identity", async () =>
        {
            using var f = new SecurityOfflineFixture("index-replay-without-id", count: 0);
            var index = Path.Combine(f.Music, "library.index.json");
            var original = File.ReadAllText(index);
            await SecurityOfflineCrash(f, "index-after-park", save: true, unsupportedIdentity: true);
            Assert(!File.Exists(index), "fixture did not reach the parked phase");
            f.Library.FileIdentityErrorForChecks = 50;
            await f.Library.RecoverPendingAsync();
            Assert(File.ReadAllText(index) == original && !File.Exists(Path.Combine(f.Music, ".ym-index.recovery"))
                && !File.Exists(f.SaveJournal) && File.Exists(f.Foreign), "replay did not restore the exact parked index");
            using var writer = f.Open(SecurityOfflineSaveMedia());
            writer.FileIdentityErrorForChecks = 50;
            var saved = await writer.SaveAsync(SecurityOfflineSaveTrack(), SecurityOfflineSaveAudio(), default);
            Assert(f.Library.Read().Single().FileName == saved.FileName && Directory.GetFiles(f.Music, "*.previous-*").Length == 0,
                "live replacement requires a persistent identity despite holding the verified original");
        });
        await Run("SecurityOffline0317: a concurrent reader sees the published index once its writer releases the handle", async () =>
        {
            using var f = new SecurityOfflineFixture("index-publication-read", count: 0);
            using var writer = f.Open(SecurityOfflineSaveMedia());
            using var reader = f.Open();
            Assert(reader.Read().Count == 0, "fixture did not prime the previous index snapshot");
            using var release = new ManualResetEventSlim(false);
            var published = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            writer.TemporaryPublicationCheckpoint = point =>
            {
                if (point != "index-before-retiring-backup") return;
                published.TrySetResult();
                if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("fixture did not release publication");
            };
            var save = Task.Run(() => writer.SaveAsync(SecurityOfflineSaveTrack(), SecurityOfflineSaveAudio(), default));
            Task<IReadOnlyList<OfflineItem>>? read = null;
            try
            {
                await published.Task.WaitAsync(TimeSpan.FromSeconds(5));
                var index = Path.Combine(f.Music, "library.index.json");
                var busy = await Throws<System.ComponentModel.Win32Exception>(() =>
                {
                    using var direct = UninstallPathGuard.OpenOwnedFile(index, read: true, deleteAccess: true);
                    return Task.CompletedTask;
                });
                Assert(busy.NativeErrorCode == 32 && Directory.GetFiles(f.Music, "*.previous-*").Length == 1
                    && File.Exists(Path.Combine(f.Music, ".ym-index.recovery")), "the published index was not held while its backup could still be retired");
                var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                read = Task.Factory.StartNew(() =>
                {
                    started.TrySetResult();
                    return reader.Read();
                }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
                await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert(await Task.WhenAny(read, Task.Delay(100)) != read, "reader used stale data or failed during publication");
            }
            finally { release.Set(); }
            var item = await save.WaitAsync(TimeSpan.FromSeconds(5));
            Assert((await read!.WaitAsync(TimeSpan.FromSeconds(5))).Single().FileName == item.FileName,
                "reader did not see the newly verified record after publication");
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
                if (name == "playlists.index.json")
                    await Throws<OfflinePlaylistIndexFormatException>(() => f.Library.ClearAsync(default));
                else
                    await Throws<IOException>(() => f.Library.ClearAsync(default));
                Assert(new FileInfo(path).Length == OfflineLibrary.MaximumIndexBytes + 1L && SecurityOfflineHash(audio) == hash
                    && !f.HasRemovalJournal, "oversized index was interpreted as empty or overwritten");
            }
        });
        await Run("SecurityOffline0317: invalid save metadata is rejected before download; zero bitrate FLAC is valid", async () =>
        {
            var root = Path.Combine(_directory, "security-offline-0317", "save-validation-" + Guid.NewGuid().ToString("N"));
            var music = Path.Combine(root, "music");
            var media = SecurityOfflineSaveMedia(flac: await EncodeAudioFixtureAsync("flac", MediaEncodingProfile.CreateFlac(AudioEncodingQuality.High)));
            using var library = new OfflineLibrary(music, new AudioBuffer(Path.Combine(root, "buffer"), media));
            await Throws<MusicException>(() => library.SaveAsync(new TrackRow { Id = "fixture", RequestId = "fixture", DurationMs = -1 }, SecurityOfflineSaveAudio(), default));
            await Throws<MusicException>(() => library.SaveAsync(SecurityOfflineSaveTrack(), SecurityOfflineSaveAudio() with { Bitrate = -1 }, default));
            await Throws<MusicException>(() => library.SaveAsync(new TrackRow { Id = "fixture", RequestId = "" }, SecurityOfflineSaveAudio(), default));
            Assert(!Directory.Exists(root) && media.Seen.Count == 0, "invalid metadata touched the offline store or downloaded audio");
            var flac = await library.SaveAsync(SecurityOfflineSaveTrack(), SecurityOfflineSaveAudio() with
                { Uri = new Uri("https://s1.vlx.yandex.net/save-fixture.flac"), Codec = "flac", Bitrate = 0 }, default);
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
        await Run("SecurityOffline0317: only the exact playlist clear revision authorizes live rollback or replay", async () =>
        {
            var empty = JsonSerializer.Serialize(new OfflinePlaylistIndex(1, []), new JsonSerializerOptions
                { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true });
            foreach (var step in new[] { "live-own", "live-before", "live-after", "replay-before", "replay-after", "replay-rewrite" })
            {
                using var f = new SecurityOfflineFixture("playlist-clear-proof-" + step, count: 1);
                var path = Path.Combine(f.Music, "playlists.index.json");
                var before = step.EndsWith("-before", StringComparison.Ordinal);
                var checkpoint = before ? "after-stage" : "after-playlist-commit";
                if (step.StartsWith("live-", StringComparison.Ordinal))
                {
                    f.Library.RemovalCheckpoint = point =>
                    {
                        if (point != checkpoint) return;
                        if (step == "live-own") throw new IOException("Leave the clear uncommitted.");
                        PutExternalEmpty();
                        if (step == "live-after") throw new IOException("Leave the clear uncommitted.");
                    };
                    if (before) await Throws<MusicException>(() => f.Library.ClearAsync(default));
                    else await Throws<IOException>(() => f.Library.ClearAsync(default));
                }
                else
                {
                    await SecurityOfflineCrash(f, checkpoint);
                    PutExternalEmpty();
                }
                await f.Library.RecoverPendingAsync();
                var ownsRevision = step == "live-own";
                Assert(f.Library.GetPlaylists().Count == (ownsRevision ? 1 : 0) && (ownsRevision || File.ReadAllText(path) == empty)
                    && f.Library.Tracks().Single().Available && !f.HasRemovalJournal
                    && Directory.GetFiles(f.Music, ".ym-playlist-clear-*.proof").Length == 0,
                    step + " restored an unrelated playlist or lost independently recoverable audio");

                void PutExternalEmpty()
                {
                    if (before || step == "replay-rewrite")
                    {
                        var written = File.GetLastWriteTimeUtc(path);
                        File.WriteAllText(path, empty);
                        File.SetLastWriteTimeUtc(path, written);
                        Assert(File.GetLastWriteTimeUtc(path) == written, "fixture did not retain the previous last-write timestamp");
                    }
                    else
                    {
                        Assert(File.ReadAllText(path) == empty, "fixture did not match the application's exact clear bytes");
                        File.Move(path, Path.Combine(f.Root, "retained-clear.json"));
                        File.WriteAllText(path, empty);
                    }
                }
            }
        });
        await Run("SecurityOffline0317: interrupted clear restores every alias to the same verified alternate file", async () =>
        {
            foreach (var point in new[] { "after-stage", "before-removal-commit-marker", "after-library-commit" })
            {
                using var f = new SecurityOfflineFixture("restored-alias-" + point, count: 1);
                var index = Path.Combine(f.Music, "library.index.json");
                var first = f.Library.Read().Single();
                var alias = first with { Id = "alias", RequestId = "alias:second", Title = "Independent alias title" };
                File.WriteAllText(index, JsonSerializer.Serialize(new[] { first, alias }));
                await SecurityOfflineCrash(f, point);
                var oldName = f.Library.ResolvePath(first.FileName);
                File.WriteAllBytes(oldName, Enumerable.Repeat((byte)0x72, (int)first.Bytes).ToArray());
                var foreignHash = SecurityOfflineHash(oldName);
                var foreign = first with { Id = "foreign-claim", RequestId = "foreign-claim", Sha256 = foreignHash };
                if (point == "after-stage") File.WriteAllText(index, JsonSerializer.Serialize(new[] { first, alias, foreign }));
                await SecurityOfflineCrash(f, "recovery-after-index", recover: true);
                await f.Library.RecoverPendingAsync();
                var rows = f.Library.Read();
                var restored = rows.Where(row => row.Sha256 == first.Sha256).ToArray();
                Assert(restored.Length == 2 && restored.Single(row => row.Id == alias.Id).Title == alias.Title
                    && restored.Single(row => row.Id == first.Id).FileName == restored[0].FileName
                    && restored[0].FileName != first.FileName && SecurityOfflineHash(f.Library.ResolvePath(restored[0].FileName)) == first.Sha256
                    && SecurityOfflineHash(oldName) == foreignHash && !f.HasRemovalJournal && File.Exists(f.Foreign)
                    && (point != "after-stage" || rows.Single(row => row.Id == foreign.Id).FileName == first.FileName),
                    point + " left an alias at the foreign path or lost its title, identifier, or bytes");
            }
        });
        await Run("SecurityOffline0317: a busy or read-only audio file retains all matching aliases until physical deletion", async () =>
        {
            foreach (var readOnly in new[] { false, true })
            {
                using var f = new SecurityOfflineFixture("retained-aliases-" + readOnly, count: 1);
                var index = Path.Combine(f.Music, "library.index.json");
                var first = f.Library.Read().Single();
                var second = first with { Id = "other-id", RequestId = "another-edition", Title = "Another edition" };
                var source = f.Library.ResolvePath(first.FileName);
                File.WriteAllText(index, JsonSerializer.Serialize(new[] { first, second }));
                if (readOnly) File.SetAttributes(source, FileAttributes.ReadOnly);
                try
                {
                    using var reader = readOnly ? null : new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
                    var partial = await f.Library.ClearAsync(default);
                    Assert(partial.Removed == 0 && partial.Failed == 1 && partial.Forgotten == 0 && f.Library.Read().Count == 2
                        && f.Library.Read().Single(row => row.Id == second.Id).Title == second.Title && SecurityOfflineHash(source) == first.Sha256,
                        "refused physical deletion dropped indexed aliases");
                }
                finally { if (readOnly) File.SetAttributes(source, FileAttributes.Normal); }
                var completed = await f.Library.ClearAsync(default);
                Assert(completed.Removed == 1 && completed.Forgotten == 2 && f.Library.Read().Count == 0
                    && !File.Exists(source) && File.Exists(f.Foreign) && !f.HasRemovalJournal,
                    "successful physical deletion left an alias or changed a foreign file");
            }
        });
        await Run("SecurityOffline0317: an attribute access error retains an indexed variant for the next removal", async () =>
        {
            foreach (var clear in new[] { false, true })
            {
                using var f = new SecurityOfflineFixture("attributes-unavailable-" + clear, count: 2);
                var index = Path.Combine(f.Music, "library.index.json");
                var rows = f.Library.Read().ToArray();
                rows[1] = rows[1] with { Id = rows[0].Id, RequestId = rows[0].RequestId, Bitrate = 192 };
                File.WriteAllText(index, JsonSerializer.Serialize(rows));
                var heldPath = f.Library.ResolvePath(rows[0].FileName);
                var removedPath = f.Library.ResolvePath(rows[1].FileName);
                var heldDigest = SecurityOfflineHash(heldPath);
                f.Library.BeforeRemovalAttributesForChecks = path =>
                {
                    if (path == heldPath) throw new UnauthorizedAccessException("Fixture metadata access temporarily unavailable.");
                };
                var partial = await Remove();
                Assert(partial.Removed == 1 && partial.Failed == 1 && partial.HasRetainedIndexEntries && partial.Forgotten == 1
                    && f.Library.Read().Single().FileName == rows[0].FileName && SecurityOfflineHash(heldPath) == heldDigest
                    && !File.Exists(removedPath) && f.Library.GetPlaylists().Count == (clear ? 0 : 1), "attribute denial removed or hid the still-indexed file");
                f.Library.BeforeRemovalAttributesForChecks = null;
                var retried = await Remove();
                Assert(retried.Removed == 1 && !retried.HasRetainedIndexEntries && f.Library.Read().Count == 0
                    && !File.Exists(heldPath) && File.Exists(f.Foreign) && !f.HasRemovalJournal, "authorized retry could not remove the preserved variant");

                Task<OfflineRemovalResult> Remove() => clear ? f.Library.ClearAsync(default) : f.Library.RemoveTrackAsync(rows[0].Id, default);
            }
        });
        await Run("SecurityOffline0317: removal selects the actual SHA across every row sharing a file name", async () =>
        {
            foreach (var mode in new[] { "verified", "attributes-denied", "sharing", "unrelated", "interrupted" })
            {
                using var f = new SecurityOfflineFixture("shared-name-sha-" + mode, count: 1);
                var index = Path.Combine(f.Music, "library.index.json");
                var current = f.Library.Read().Single();
                var media = f.Library.ResolvePath(current.FileName);
                var stale = current with { Id = "old", RequestId = "old", Sha256 = new string('F', 64) };
                File.WriteAllText(index, JsonSerializer.Serialize(new[] { stale, current }));
                var original = File.ReadAllBytes(media);
                if (mode == "unrelated") File.WriteAllBytes(media, Enumerable.Repeat((byte)0x41, (int)current.Bytes).ToArray());
                if (mode == "interrupted")
                {
                    await SecurityOfflineCrash(f, "after-library-commit");
                    await f.Library.RecoverPendingAsync();
                    Assert(f.Library.Read().Single().Id == current.Id && File.ReadAllBytes(media).SequenceEqual(original),
                        "the removal journal did not carry the later matching row");
                }
                if (mode == "attributes-denied" || mode == "sharing")
                {
                    using var busy = mode == "sharing" ? new FileStream(media, FileMode.Open, FileAccess.Read, FileShare.Read) : null;
                    if (mode == "attributes-denied") f.Library.BeforeRemovalAttributesForChecks = _ => throw new UnauthorizedAccessException();
                    var partial = await f.Library.ClearAsync(default);
                    Assert(partial.Failed == 1 && partial.Forgotten == 0 && partial.HasRetainedIndexEntries && f.Library.Read().Count == 2,
                        mode + " dropped the later matching row before checking the physical file");
                    f.Library.BeforeRemovalAttributesForChecks = null;
                }
                var result = await f.Library.ClearAsync(default);
                Assert(f.Library.Read().Count == 0 && File.Exists(f.Foreign) && !f.HasRemovalJournal
                    && (mode == "unrelated" ? result.Removed == 0 && File.ReadAllBytes(media).All(value => value == 0x41)
                        : result.Removed == 1 && !File.Exists(media)), mode + " retained the proven file or removed unrelated bytes");
            }
        });
        await Run("SecurityOffline0317: single removal only shares the exact verified audio signature", async () =>
        {
            foreach (var mode in new[] { "other-sha", "same-sha", "verification-busy" })
            {
                using var f = new SecurityOfflineFixture("single-shared-name-" + mode, count: 1);
                var matching = f.Library.Read().Single();
                var other = matching with { Id = "other", RequestId = "other", Sha256 = mode == "same-sha" ? matching.Sha256 : new string('F', 64) };
                var index = Path.Combine(f.Music, "library.index.json");
                var audio = f.Library.ResolvePath(matching.FileName);
                File.WriteAllText(index, JsonSerializer.Serialize(new[] { other, matching }));
                if (mode == "verification-busy")
                {
                    using var reader = new FileStream(audio, FileMode.Open, FileAccess.Read, FileShare.Read);
                    var blocked = await f.Library.RemoveTrackAsync(matching.Id, default);
                    Assert(blocked.Failed == 1 && blocked.HasRetainedIndexEntries && f.Library.Read().Count == 2
                        && SecurityOfflineHash(audio) == matching.Sha256, "unverified sharing forgot an indexed candidate");
                }
                var removal = await f.Library.RemoveTrackAsync(matching.Id, default);
                var shared = mode == "same-sha";
                Assert(removal.Removed == (shared ? 0 : 1) && removal.Forgotten == 1 && !removal.HasRetainedIndexEntries
                    && f.Library.Read().Single().Id == other.Id && !f.HasRemovalJournal && File.Exists(f.Foreign)
                    && (shared ? SecurityOfflineHash(audio) == matching.Sha256 : !File.Exists(audio)),
                    "single removal treated an unrelated digest as ownership or removed a truly shared file");
            }
        });
        await Run("SecurityOffline0317: late single rollback recognizes the exact index it wrote before the marker", async () =>
        {
            foreach (var mode in new[] { "live", "replay", "independent" })
            {
                using var f = new SecurityOfflineFixture("late-single-rollback-index-" + mode, count: 1);
                var expected = f.Library.Read().Single();
                var stale = expected with { Id = "older", RequestId = "older", Sha256 = new string('F', 64) };
                var index = Path.Combine(f.Music, "library.index.json");
                File.WriteAllText(index, JsonSerializer.Serialize(new[] { stale, expected }));
                if (mode == "live")
                {
                    f.Library.RemovalCheckpoint = point =>
                    {
                        if (point == "before-removal-commit-marker") throw new IOException("Fixture marker failure.");
                    };
                    var error = await Throws<IOException>(() => f.Library.RemoveTrackAsync(expected.Id, default));
                    Assert(error.Message == "Fixture marker failure.", "rollback replaced the original marker error");
                }
                else
                {
                    await SecurityOfflineCrash(f, "before-removal-commit-marker", removeSingle: true);
                    if (mode == "independent")
                    {
                        var previous = Path.Combine(f.Root, "saved-planned-index.json");
                        File.Move(index, previous);
                        File.Copy(previous, index);
                    }
                    else await SecurityOfflineCrash(f, "index-after-park", recover: true);
                }
                await f.Library.RecoverPendingAsync();
                var restored = f.Library.Read().Single(row => row.Id == expected.Id);
                Assert((restored.FileName == expected.FileName) == (mode != "independent")
                    && SecurityOfflineHash(f.Library.ResolvePath(restored.FileName)) == expected.Sha256
                    && f.Library.Read().Single(row => row.Id == stale.Id).FileName == stale.FileName && !f.HasRemovalJournal,
                    "rollback missed its own signed revision or accepted an independent identical index");
            }
        });
        await Run("SecurityOffline0317: replay keeps distinct metadata with shared IDs and prefers an existing edited alias", async () =>
        {
            foreach (var mode in new[] { "original", "edit", "different-album" })
            {
                using var f = new SecurityOfflineFixture("shared-alias-metadata-" + mode, count: 1);
                var first = f.Library.Read().Single() with { AlbumId = "1", Title = "First edition" };
                var second = first with { AlbumId = "2", Title = "Second edition" };
                var index = Path.Combine(f.Music, "library.index.json");
                File.WriteAllText(index, JsonSerializer.Serialize(new[] { first, second }));
                await SecurityOfflineCrash(f, "after-library-commit");
                if (mode == "edit") File.WriteAllText(index, JsonSerializer.Serialize(new[] { second with { Title = "Edited second edition" } }));
                if (mode == "different-album") File.WriteAllText(index, JsonSerializer.Serialize(new[] { second with { AlbumId = "3", Title = "Third edition" } }));
                await SecurityOfflineCrash(f, "recovery-after-index", recover: true);
                await f.Library.RecoverPendingAsync();
                var rows = f.Library.Read();
                Assert(rows.Count == (mode == "different-album" ? 3 : 2)
                    && rows.All(row => row.Id == first.Id && row.RequestId == first.RequestId && row.FileName == first.FileName)
                    && rows.Single(row => row.AlbumId == "1").Title == first.Title
                    && rows.Single(row => row.AlbumId == "2").Title == (mode == "edit" ? "Edited second edition" : second.Title)
                    && (mode != "different-album" || rows.Single(row => row.AlbumId == "3").Title == "Third edition")
                    && SecurityOfflineHash(f.Library.ResolvePath(first.FileName)) == first.Sha256 && !f.HasRemovalJournal,
                    "replay collapsed distinct metadata or replaced the external edit");
            }
        });
        await Run("SecurityOffline0317: repeated rollback recognizes its installed repaired audio index", async () =>
        {
            foreach (var mode in new[] { "own", "independent-copy", "occupied" })
            {
                using var f = new SecurityOfflineFixture("repair-publication-" + mode, count: 1);
                var matching = f.Library.Read().Single();
                var other = matching with { Id = "old-digest", RequestId = "old-digest", Sha256 = new string('C', 64) };
                var index = Path.Combine(f.Music, "library.index.json");
                var original = f.Library.ResolvePath(matching.FileName);
                File.WriteAllText(index, JsonSerializer.Serialize(new[] { matching, other }));
                var originalHash = SecurityOfflineHash(index);
                await SecurityOfflineCrash(f, "before-removal-commit-marker", removeSingle: true);
                var clearedHash = SecurityOfflineHash(index);
                if (mode == "occupied") File.WriteAllText(original, "Independent ordinary file");
                await SecurityOfflineCrash(f, "recovery-after-index", recover: true);
                var repairedHash = SecurityOfflineHash(index);
                Assert(repairedHash != originalHash && repairedHash != clearedHash && f.HasRemovalJournal,
                    "fixture did not reach a different, already installed repair revision");
                if (mode == "independent-copy")
                {
                    var copy = Path.Combine(f.Root, "retained-repair-index.json");
                    File.Move(index, copy);
                    File.Copy(copy, index);
                    Assert(SecurityOfflineHash(index) == repairedHash, "fixture did not retain exact repaired bytes");
                }
                await f.Library.RecoverPendingAsync();
                var rows = f.Library.Read();
                var restored = rows.Single(row => row.Id == matching.Id);
                Assert((restored.FileName == matching.FileName) == (mode == "own") && rows.Single(row => row.Id == other.Id).FileName == other.FileName
                    && SecurityOfflineHash(f.Library.ResolvePath(restored.FileName)) == matching.Sha256 && !f.HasRemovalJournal
                    && (mode != "occupied" || File.ReadAllText(original) == "Independent ordinary file")
                    && Directory.GetFiles(f.Music, ".ym-library-repair-*.proof").Length == 0,
                    "replay forgot its repaired revision or trusted an occupied name or an independent identical index");
            }
        });
        await Run("SecurityOffline0317: cleanup retires the removal journal before dropping repaired index proofs", async () =>
        {
            foreach (var checkpoint in new[] { "removal-after-journal-retired", "removal-after-proofs-retired", "independent-copy" })
            {
                using var f = new SecurityOfflineFixture("repair-cleanup-order-" + checkpoint, count: 1);
                var matching = f.Library.Read().Single();
                var stale = matching with { Id = "stale", RequestId = "stale", Sha256 = new string('B', 64) };
                var original = f.Library.ResolvePath(matching.FileName);
                var index = Path.Combine(f.Music, "library.index.json");
                File.WriteAllText(index, JsonSerializer.Serialize(new[] { matching, stale }));
                await SecurityOfflineCrash(f, "before-removal-commit-marker", removeSingle: true);
                if (checkpoint == "independent-copy")
                {
                    await SecurityOfflineCrash(f, "recovery-after-index", recover: true);
                    var retained = Path.Combine(f.Root, "retained-repaired-index.json");
                    var before = SecurityOfflineHash(index);
                    File.Move(index, retained);
                    File.Copy(retained, index);
                    Assert(SecurityOfflineHash(index) == before, "fixture did not preserve the repaired index bytes");
                }
                await SecurityOfflineCrash(f, checkpoint == "independent-copy" ? "removal-after-proofs-retired" : checkpoint, recover: true);
                var proofs = Directory.GetFiles(f.Music, ".ym-library-repair-*.proof");
                Assert(!f.HasRemovalJournal && (proofs.Length > 0) == (checkpoint == "removal-after-journal-retired"),
                    "companion cleanup completed while the repair could still replay");
                var repairedHash = SecurityOfflineHash(index);
                await f.Library.RecoverPendingAsync();
                var rows = f.Library.Read();
                var restored = rows.Single(row => row.Id == matching.Id);
                Assert(rows.Count == 2 && rows.Single(row => row.Id == stale.Id).FileName == stale.FileName
                    && (restored.FileName == matching.FileName) == (checkpoint != "independent-copy")
                    && SecurityOfflineHash(f.Library.ResolvePath(restored.FileName)) == matching.Sha256
                    && (File.Exists(original) == (checkpoint != "independent-copy"))
                    && SecurityOfflineHash(index) == repairedHash && !f.HasRemovalJournal && File.Exists(f.Foreign),
                    "a cleanup interruption moved verified audio, changed the repaired snapshot or trusted an independent copy");
            }
        });
        await Run("SecurityOffline0317: verifying shared paths retains every rejected digest for the checked revision", async () =>
        {
            using var f = new SecurityOfflineFixture("same-path-rejection-set", count: 1);
            var original = f.Library.Read().Single();
            var media = f.Library.ResolvePath(original.FileName);
            var replacement = Enumerable.Repeat((byte)0x52, checked((int)original.Bytes)).ToArray();
            var acceptedHash = Convert.ToHexString(SHA256.HashData(replacement)).ToLowerInvariant();
            var alternate = original with { Bitrate = 192, Sha256 = acceptedHash };
            var index = Path.Combine(f.Music, "library.index.json");
            var sourceIndex = JsonSerializer.Serialize(new[] { original, alternate });
            File.WriteAllText(index, sourceIndex);
            File.WriteAllBytes(media, Enumerable.Repeat((byte)0x5C, replacement.Length).ToArray());
            var request = new OfflineLibrary.DownloadCacheRequest(new TrackRow { Id = original.Id, RequestId = original.RequestId });
            var before = f.Library.DownloadVerificationRejectionVersion;
            using (var checkedCopies = await f.Library.HoldDownloadCacheBatchAsync([request], default, verifyAllVariants: true))
                Assert(checkedCopies[0].Item is null && checkedCopies[0].Unavailable is null, "fixture did not compare both digests against one corrupt revision");
            Assert(f.Library.DownloadVerificationRejectionVersion == before + 2 && f.Library.Tracks().All(row => !row.Available)
                && !f.Library.GetPlaylistTracks("fixture", "1").Single().Available && !f.Library.GetPlaylistStatus("fixture", "1").Complete,
                "one proved digest rejection disappeared while reopening the ordinary playlist projection");
            File.Move(media, Path.Combine(f.Root, "previous-fixture.mp3"));
            File.WriteAllBytes(media, replacement);
            using (var checkedCopies = await f.Library.HoldDownloadCacheBatchAsync([request], default, verifyAllVariants: true))
                Assert(checkedCopies[0].Item?.Sha256 == acceptedHash && checkedCopies[0].Unavailable is null, "a different file revision did not permit the matching digest");
            Assert(f.Library.Tracks().Where(row => row.Available).Single().LocalSourceSha256 == acceptedHash
                && f.Library.GetPlaylistTracks("fixture", "1").Single().Available && f.Library.GetPlaylistStatus("fixture", "1").Complete
                && File.ReadAllText(index) == sourceIndex && File.Exists(f.Foreign), "the new revision restored a wrong variant or changed persisted metadata");
            var catalog = request.Track;
            var marked = f.Library.FindCatalogPlaybackMark(catalog);
            var source = f.Library.ResolvePlaybackSource(catalog);
            var wrong = f.Library.Tracks().Single(row => row.LocalSourceSha256 == original.Sha256);
            Assert(marked is { Variant.Bitrate: 192 } && marked.Path == media && marked.Digest == acceptedHash
                && source is { Variant.Bitrate: 192 } && source.Path == media && source.Digest == acceptedHash
                && f.Library.ResolvePlaybackSource(wrong, requireExactOfflineVariant: true) is null,
                "the ordinary resolver hid a matching digest on a shared path or reused it for the explicitly wrong copy");
            using var verified = await source!.HoldAsync(default);
            Assert(verified.VerifiedDigest?.Equals(acceptedHash, StringComparison.OrdinalIgnoreCase) == true,
                "the selected alternate did not open using its own expected digest");
        });
        await Run("SecurityOffline0317: removal and save replay preserve audio with extended attributes", async () =>
        {
            foreach (var mode in new[] { "single", "clear", "save-replay", "removal-replay" })
            {
                using var f = new SecurityOfflineFixture("audio-ea-" + mode, count: mode == "save-replay" ? 0 : 1);
                var original = Path.Combine(f.Music, "track-0.mp3");
                var source = original;
                if (mode == "save-replay")
                {
                    await SecurityOfflineCrash(f, "save-after-audio-publish", save: true);
                    source = Directory.GetFiles(f.Music, "*.mp3").Single(path => path != f.Foreign);
                    original = source;
                }
                else if (mode == "removal-replay")
                {
                    await SecurityOfflineCrash(f, "after-library-commit");
                    source = Directory.GetFiles(f.Music, ".ym-delete-*.pending").Single(path => path != f.ForeignPending);
                }
                var hash = SecurityOfflineHash(source);
                Cleanup015AddExtendedAttribute(source);
                if (mode == "single") await f.Library.RemoveTrackAsync("r3-0", default);
                else if (mode == "clear") await f.Library.ClearAsync(default);
                else await f.Library.RecoverPendingAsync();
                using var file = UninstallPathGuard.OpenOwnedFile(original, read: true, deleteAccess: false);
                Assert(FileSecurityPreservation.HasNamedStreamsOrExtendedAttributes(file) && !FileSecurityPreservation.HasNamedDataStreams(file)
                    && SecurityOfflineHash(original) == hash && f.Library.Read().Count == 0 && !f.HasRemovalJournal && !File.Exists(f.SaveJournal),
                    mode + " deleted extended attributes or re-indexed a protected user file");
            }
        });
        await Run("SecurityOffline0317: a recovery journal with extended attributes is preserved", async () =>
        {
            using var f = new SecurityOfflineFixture("journal-ea-preserved", count: 1);
            await SecurityOfflineCrash(f, "after-stage");
            var staged = Directory.GetFiles(f.Music, ".ym-delete-*.pending").Single(path => path != f.ForeignPending);
            var digest = SecurityOfflineHash(staged);
            var receipt = SecurityOfflineHash(f.Journal);
            Cleanup015AddExtendedAttribute(f.Journal);
            var error = await Throws<MusicException>(() => f.Library.RecoverPendingAsync());
            using var held = UninstallPathGuard.OpenOwnedFile(f.Journal, read: true, deleteAccess: false);
            Assert(error.Message.Contains("расширенные атрибуты", StringComparison.Ordinal) && f.HasRemovalJournal
                && FileSecurityPreservation.HasNamedStreamsOrExtendedAttributes(held) && SecurityOfflineHash(f.Journal) == receipt
                && SecurityOfflineHash(staged) == digest && File.Exists(f.Foreign), "recovery removed its protected receipt or changed staged data");
        });
        await Run("SecurityOffline0317: early rollback keeps the original verified path despite a pre-existing different digest", async () =>
        {
            foreach (var live in new[] { true, false })
            {
                using var f = new SecurityOfflineFixture("early-index-conflict-" + live, count: 1);
                var item = f.Library.Read().Single();
                var other = item with { Id = "older", RequestId = "older", Sha256 = new string('E', 64) };
                var original = f.Library.ResolvePath(item.FileName);
                var index = Path.Combine(f.Music, "library.index.json");
                var before = JsonSerializer.Serialize(new[] { other, item });
                File.WriteAllText(index, before);
                if (live)
                {
                    f.Library.RemovalCheckpoint = point =>
                    {
                        if (point == "after-stage") throw new IOException("Requested early fixture rollback.");
                    };
                    var error = await Throws<IOException>(() => f.Library.ClearAsync(default));
                    Assert(error.Message == "Requested early fixture rollback.", "rollback replaced the original controlled failure");
                }
                else
                {
                    await SecurityOfflineCrash(f, "after-stage");
                    await SecurityOfflineCrash(f, "recovery-after-restore", recover: true);
                }
                await f.Library.RecoverPendingAsync();
                Assert(File.ReadAllText(index) == before && SecurityOfflineHash(original) == item.Sha256 && !f.HasRemovalJournal
                    && Directory.GetFiles(f.Music, ".ym-recovered-*").Length == 0 && f.Library.GetPlaylists().Count == 1,
                    "rollback moved the verified file or rewrote an unchanged ambiguous index");
            }
        });
        await Run("SecurityOffline0317: owned rollback restores all rows on one absent path and verifies an arriving file once", async () =>
        {
            foreach (var arrival in new[] { "absent", "matching", "unrelated" })
            {
                using var f = new SecurityOfflineFixture("unstaged-shared-name-" + arrival, count: 1);
                var current = f.Library.Read().Single();
                var wrong = current with { Id = "old-digest", RequestId = "old-digest", Sha256 = new string('A', 64) };
                var alias = current with { Id = "matching-alias", RequestId = "second-album" };
                var original = f.Library.ResolvePath(current.FileName);
                var bytes = File.ReadAllBytes(original);
                File.WriteAllText(Path.Combine(f.Music, "library.index.json"), JsonSerializer.Serialize(new[] { wrong, current, alias }));
                File.Delete(original);
                await SecurityOfflineCrash(f, "before-removal-commit-marker");
                if (arrival == "matching") File.WriteAllBytes(original, bytes);
                else if (arrival == "unrelated") File.WriteAllBytes(original, Enumerable.Repeat((byte)0x7B, bytes.Length).ToArray());
                await SecurityOfflineCrash(f, "recovery-after-index", recover: true);
                await f.Library.RecoverPendingAsync();
                var expected = arrival == "absent" ? new[] { wrong.Id, current.Id, alias.Id }
                    : arrival == "matching" ? new[] { current.Id, alias.Id } : [];
                Assert(f.Library.Read().Select(row => row.Id).SequenceEqual(expected) && !f.HasRemovalJournal && File.Exists(f.Foreign)
                    && (arrival switch { "matching" => File.ReadAllBytes(original).SequenceEqual(bytes),
                        "unrelated" => File.ReadAllBytes(original).All(value => value == 0x7B), _ => !File.Exists(original) }),
                    "rollback lost authentic metadata by row order or adopted unrelated media");
            }
        });
        await Run("SecurityOffline0317: a denied or shared read does not signal confirmed download absence", async () =>
        {
            using var f = new SecurityOfflineFixture("download-uncertain-file", count: 1);
            var track = new TrackRow { Id = "r3-0", RequestId = "r3-0" };
            var media = f.Library.ResolvePath("track-0.mp3");
            var revision = f.Library.DownloadVerificationRejectionVersion;
            f.Library.BeforeVerifySavedAudioForChecks = _ => throw new System.ComponentModel.Win32Exception(5);
            await Throws<IOException>(() => f.Library.FindVerifiedAsync(track, default, failIfUnavailable: true));
            f.Library.BeforeVerifySavedAudioForChecks = null;
            using (var busy = new FileStream(media, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
                await Throws<IOException>(() => f.Library.FindVerifiedAsync(track, default, failIfUnavailable: true));
            Assert(f.Library.DownloadVerificationRejectionVersion == revision && await f.Library.FindVerifiedAsync(track, default) is not null,
                "a temporary read failure was reported as proven absence or removed a valid indexed file");
        });
        await Run("SecurityOffline0317: a busy playlist clear proof keeps rollback available for retry", async () =>
        {
            foreach (var mode in new[] { "live-held", "replay-held", "replay-released-at-fault" })
            {
                using var f = new SecurityOfflineFixture("busy-playlist-clear-proof-" + mode, count: 1);
                var playlists = Path.Combine(f.Music, "playlists.index.json");
                var previous = f.Library.GetPlaylists().Single();
                string? proof = null;
                string? cleared = null;
                FileStream? busy = null;
                try
                {
                    if (mode == "live-held")
                    {
                        f.Library.RemovalCheckpoint = point =>
                        {
                            if (point != "before-removal-commit-marker") return;
                            HoldProof();
                            throw new IOException("Leave the clear uncommitted.");
                        };
                        var liveFault = await Throws<IOException>(() => f.Library.ClearAsync(default));
                        Assert(liveFault.InnerException is System.ComponentModel.Win32Exception { NativeErrorCode: 32 }, "live rollback did not encounter the held proof");
                    }
                    else
                    {
                        await SecurityOfflineCrash(f, "before-removal-commit-marker");
                        HoldProof();
                        if (mode == "replay-released-at-fault") f.Library.RemovalCheckpoint = point =>
                        {
                            if (point == "removal-proof-unavailable") { busy!.Dispose(); busy = null; }
                        };
                    }
                    var fault = await Throws<IOException>(() => f.Library.RecoverPendingAsync());
                    var unavailable = await Throws<MusicException>(() => Task.FromResult(f.Library.GetPlaylists()));
                    Assert(fault.InnerException is System.ComponentModel.Win32Exception { NativeErrorCode: 32 }
                        && unavailable.OfflineStorageRetryable && f.HasRemovalJournal && File.Exists(proof) && File.ReadAllText(playlists) == cleared
                        && (mode != "replay-released-at-fault" || busy is null), "a transient proof failure retired the rollback evidence");
                }
                finally { busy?.Dispose(); f.Library.RemovalCheckpoint = null; }
                await f.Library.RecoverPendingAsync();
                var restored = f.Library.GetPlaylists().Single();
                Assert(restored.Owner == previous.Owner && restored.Kind == previous.Kind && restored.Tracks.Count == previous.Tracks.Count
                    && f.Library.Tracks().Single().Available && !f.HasRemovalJournal && !File.Exists(proof),
                    "retry did not restore the original playlist and audio after the proof was released");

                void HoldProof()
                {
                    cleared = File.ReadAllText(playlists);
                    proof = Directory.GetFiles(f.Music, ".ym-playlist-clear-*.proof").Single();
                    busy = new FileStream(proof, FileMode.Open, FileAccess.Read, FileShare.Read);
                }
            }
        });
        await Run("SecurityOffline0317: a foreign playlist clear proof cannot take ownership of an empty snapshot", async () =>
        {
            using var f = new SecurityOfflineFixture("foreign-playlist-clear-proof", count: 1);
            await SecurityOfflineCrash(f, "after-playlist-commit");
            var playlists = Path.Combine(f.Music, "playlists.index.json");
            var cleared = File.ReadAllText(playlists);
            var proof = Directory.GetFiles(f.Music, ".ym-playlist-clear-*.proof").Single();
            File.WriteAllText(proof, "unrelated fixture bytes");
            await f.Library.RecoverPendingAsync();
            Assert(f.Library.GetPlaylists().Count == 0 && File.ReadAllText(playlists) == cleared && f.Library.Tracks().Single().Available
                && File.ReadAllText(proof) == "unrelated fixture bytes" && !f.HasRemovalJournal,
                "recovery replaced an unproven playlist snapshot or removed a foreign proof");
        });
        await Run("SecurityOffline0317: committed removal reports success while obsolete proofs stay busy", async () =>
        {
            foreach (var live in new[] { true, false })
            {
                using var f = new SecurityOfflineFixture("busy-obsolete-removal-proof-" + live, count: 2);
                File.Delete(f.Library.ResolvePath("track-0.mp3"));
                var audio = f.Library.ResolvePath("track-1.mp3");
                var held = new List<FileStream>();
                try
                {
                    if (live)
                    {
                        f.Library.RemovalCheckpoint = point => { if (point == "after-library-commit") HoldProofs(); };
                        var result = await f.Library.ClearAsync(default);
                        Assert(result.Removed == 1 && result.Failed == 0, "busy proof reported failure after deleting the committed track");
                    }
                    else
                    {
                        await SecurityOfflineCrash(f, "after-delete");
                        HoldProofs();
                    }
                    await f.Library.RecoverPendingAsync();
                    Assert(held.Count == 2 && !f.HasRemovalJournal && !File.Exists(audio) && f.Library.GetPlaylists().Count == 0
                        && f.Library.Read().Count == 0 && File.Exists(f.Foreign), "unused secondary evidence held the committed indexes unavailable");
                }
                finally { foreach (var file in held) file.Dispose(); }

                void HoldProofs()
                {
                    foreach (var path in Directory.GetFiles(f.Music, ".ym-*.proof"))
                        held.Add(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read));
                }
            }
        });
        await Run("SecurityOffline0317: repaired pending rollback can retire while secondary proofs are busy", async () =>
        {
            foreach (var live in new[] { true, false })
            {
                using var f = new SecurityOfflineFixture("busy-repaired-rollback-" + live, count: 2);
                File.Delete(f.Library.ResolvePath("track-0.mp3"));
                var held = new List<FileStream>();
                try
                {
                    if (live)
                    {
                        f.Library.RemovalCheckpoint = point =>
                        {
                            if (point == "before-removal-commit-marker") throw new IOException("Requested fixture rollback.");
                            if (point == "live-rollback-after-index") HoldProofs();
                        };
                        var failure = await Throws<IOException>(() => f.Library.ClearAsync(default));
                        Assert(failure.Message == "Requested fixture rollback.", "secondary cleanup replaced the live rollback result");
                    }
                    else
                    {
                        await SecurityOfflineCrash(f, "before-removal-commit-marker");
                        await SecurityOfflineCrash(f, "recovery-after-index", recover: true);
                        HoldProofs();
                    }
                    await f.Library.RecoverPendingAsync();
                    Assert(held.Count == 2 && !f.HasRemovalJournal && f.Library.Read().Count == 2
                        && f.Library.Tracks().Single(row => row.Id == "r3-1").Available && f.Library.GetPlaylists().Count == 1,
                        "busy obsolete proof kept the repaired pending indexes unavailable");
                }
                finally { foreach (var receipt in held) receipt.Dispose(); }

                void HoldProofs()
                {
                    foreach (var path in Directory.GetFiles(f.Music, ".ym-*.proof"))
                        held.Add(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read));
                }
            }
        });
        await Run("SecurityOffline0317: playlist clear needs an attestable staged revision only when it changes saved snapshots", async () =>
        {
            foreach (var mode in new[] { "unsupported-volume", "unsupported-stage", "already-empty", "missing-index" })
            {
                using var f = new SecurityOfflineFixture("playlist-clear-no-id-" + mode, count: 1);
                var playlists = Path.Combine(f.Music, "playlists.index.json");
                var index = Path.Combine(f.Music, "library.index.json");
                var audio = Path.Combine(f.Music, "track-0.mp3");
                var digest = SecurityOfflineHash(audio);
                if (mode == "already-empty") File.WriteAllText(playlists, "{\"schemaVersion\":1,\"playlists\":[]}");
                if (mode == "missing-index") File.Delete(playlists);
                var previous = File.Exists(playlists) ? File.ReadAllText(playlists) : null;
                var audioIndex = File.ReadAllText(index);
                var attemptedStage = false;
                if (mode == "unsupported-stage")
                {
                    f.Library.TemporaryPublicationCheckpoint = point =>
                    {
                        if (point != "index-before-publish") return;
                        attemptedStage = true;
                        f.Library.FileIdentityErrorForChecks = 50;
                    };
                }
                else f.Library.FileIdentityErrorForChecks = 50;

                if (mode.StartsWith("unsupported-", StringComparison.Ordinal))
                {
                    await Throws<IOException>(() => f.Library.ClearAsync(default));
                    Assert(attemptedStage == (mode == "unsupported-stage") && SecurityOfflineHash(audio) == digest
                        && File.ReadAllText(index) == audioIndex && f.Library.GetPlaylists().Count == 1,
                        mode + " changed a nonempty snapshot or its local audio without a replay proof");
                }
                else Assert((await f.Library.ClearAsync(default)).Removed == 1 && !File.Exists(audio)
                    && f.Library.GetPlaylists().Count == 0, mode + " prevented removal when no snapshot needed a rewrite");

                Assert((File.Exists(playlists) ? File.ReadAllText(playlists) : null) == previous && !f.HasRemovalJournal
                    && Directory.GetFiles(f.Music, ".ym-playlist-clear-*.proof").Length == 0
                    && Directory.GetFiles(f.Music, "*.tmp-*").Length == 0 && File.Exists(f.Foreign), mode + " left a new unprovable snapshot or receipt");
            }
        });
        await Run("SecurityOffline0317: unreadable playlist JSON does not block repair of independent audio index", async () =>
        {
            using var f = new SecurityOfflineFixture("malformed-external-playlists");
            await SecurityOfflineCrash(f, "before-removal-commit-marker");
            var playlists = Path.Combine(f.Music, "playlists.index.json");
            const string outside = "{\"schemaVersion\":1,\"playlists\":[";
            File.WriteAllText(playlists, outside);
            for (var attempt = 0; attempt < 2; attempt++)
            {
                using var reopened = f.Open(); await reopened.RecoverPendingAsync();
                Assert(!f.HasRemovalJournal && File.ReadAllText(playlists) == outside
                    && reopened.Read().Count == 2 && reopened.Read().All(item => SecurityOfflineHash(reopened.ResolvePath(item.FileName)) == item.Sha256),
                    "recovery overwrote unrelated playlist bytes or failed to restore indexed audio");
                await Throws<OfflinePlaylistIndexFormatException>(() => Task.FromResult(reopened.GetPlaylists()));
            }
        });
        await Run("SecurityOffline0317: oversized external playlist index stays untouched during audio repair", async () =>
        {
            using var f = new SecurityOfflineFixture("oversized-external-playlists", count: 1);
            await SecurityOfflineCrash(f, "before-removal-commit-marker");
            var playlists = Path.Combine(f.Music, "playlists.index.json");
            const long length = OfflineLibrary.MaximumIndexBytes + 1L;
            using (var outside = File.Open(playlists, FileMode.Create, FileAccess.Write))
            { outside.WriteByte(0x7B); outside.SetLength(length); }
            using var reopened = f.Open(); await reopened.RecoverPendingAsync();
            Assert(reopened.Tracks().Single().Available && !f.HasRemovalJournal && new FileInfo(playlists).Length == length,
                "an oversized unrelated playlist prevented restoration or was overwritten");
            using (var outside = File.OpenRead(playlists)) Assert(outside.ReadByte() == 0x7B, "external index bytes changed");
            await Throws<OfflinePlaylistIndexFormatException>(() => Task.FromResult(reopened.GetPlaylists()));
        });
        await Run("SecurityOffline0317: save replay honors a row written while it verified the audio", async () =>
        {
            using var f = new SecurityOfflineFixture("save-replay-index-changed", count: 0);
            await SecurityOfflineCrash(f, "save-after-audio-publish", save: true);
            var saved = Directory.GetFiles(f.Music, "*.mp3").Single(path => path != f.Foreign);
            var digest = SecurityOfflineHash(saved);
            var row = new OfflineItem("retained", "retained", "External snapshot", "", "", 0, Path.GetFileName(saved),
                "mp3", 320, new FileInfo(saved).Length, digest, DateTimeOffset.UtcNow);
            var updated = false;
            f.Library.SaveCheckpoint = point =>
            {
                if (point != "save-recovery-after-hash") return;
                updated = true;
                File.WriteAllText(Path.Combine(f.Music, "library.index.json"), JsonSerializer.Serialize(new[] { row }));
            };
            await f.Library.RecoverPendingAsync();
            Assert(updated && f.Library.Read().Single().Title == row.Title && SecurityOfflineHash(saved) == digest
                && !File.Exists(f.SaveJournal), "replay unlinked the file from a fresher index");
        });
        await Run("SecurityOffline0317: first save replay reserves the missing index without leaving a placeholder", async () =>
        {
            using var f = new SecurityOfflineFixture("first-save-no-index", count: 0);
            var index = Path.Combine(f.Music, "library.index.json");
            File.Delete(index);
            await SecurityOfflineCrash(f, "save-after-audio-publish", save: true);
            var published = Directory.GetFiles(f.Music, "*.mp3").Single(path => path != f.Foreign);
            await f.Library.RecoverPendingAsync();
            Assert(!File.Exists(index) && !File.Exists(published) && !File.Exists(f.SaveJournal)
                && File.Exists(f.Foreign) && f.Library.Read().Count == 0, "first-save rollback left a placeholder or failed to remove its verified orphan");
        });
        await Run("SecurityOffline0317: live rollback preserves edited metadata for the same recovered file", async () =>
        {
            foreach (var collision in new[] { false, true })
            {
                using var f = new SecurityOfflineFixture("rollback-metadata-" + collision, count: 2);
                var index = Path.Combine(f.Music, "library.index.json");
                var original = Path.Combine(f.Music, "track-0.mp3");
                var digest = SecurityOfflineHash(original);
                var metadata = JsonSerializer.Deserialize<OfflineItem[]>(File.ReadAllText(index))!;
                metadata[0] = metadata[0] with { Title = "Edited external metadata", Artist = "Other text" };
                f.Library.RemovalCheckpoint = point =>
                {
                    if (point != "after-stage") return;
                    File.WriteAllText(index, JsonSerializer.Serialize(metadata));
                    if (collision) File.WriteAllText(original, "personal replacement");
                };
                await Throws<MusicException>(() => f.Library.RemoveTrackAsync("r3-0", default));
                var rows = f.Library.Read(); var kept = rows.Single(row => row.Id == "r3-0");
                Assert(rows.Count == 2 && kept.Title == metadata[0].Title && kept.Artist == metadata[0].Artist
                    && SecurityOfflineHash(Path.Combine(f.Music, kept.FileName)) == digest && !f.HasRemovalJournal,
                    "rollback duplicated or reverted externally edited metadata");
                if (collision) Assert(File.ReadAllText(original) == "personal replacement", "rollback touched the collision");
            }
        });
        await Run("SecurityOffline0317: a different digest at the old index name keeps both records distinct", async () =>
        {
            using var f = new SecurityOfflineFixture("rollback-different-digest", count: 1);
            var index = Path.Combine(f.Music, "library.index.json");
            var original = Path.Combine(f.Music, "track-0.mp3");
            var before = f.Library.Read().Single();
            var outside = before with { Title = "Catalog edit for another file revision", Sha256 = new string('1', 64) };
            f.Library.RemovalCheckpoint = point =>
            {
                if (point == "after-stage") File.WriteAllText(index, JsonSerializer.Serialize(new[] { outside }));
            };
            await Throws<MusicException>(() => f.Library.RemoveTrackAsync(before.Id, default));
            await f.Library.RecoverPendingAsync();
            var rows = f.Library.Read();
            var retained = rows.Single(row => row.Sha256 == before.Sha256);
            Assert(rows.Count == 2 && rows.Single(row => row.Sha256 == outside.Sha256).Title == outside.Title
                && retained.FileName != outside.FileName && SecurityOfflineHash(f.Library.ResolvePath(retained.FileName)) == before.Sha256
                && !File.Exists(original) && !f.HasRemovalJournal && File.Exists(f.Foreign),
                "repair replaced the external claim or recorded two different files at a single name");
        });
        await Run("SecurityOffline0317: same-size index edits with restored mtime block stale writes", async () =>
        {
            foreach (var playlist in new[] { false, true })
            {
                using var f = new SecurityOfflineFixture("edited-revision-" + playlist, count: 2);
                var index = Path.Combine(f.Music, playlist ? "playlists.index.json" : "library.index.json");
                var time = File.GetLastWriteTimeUtc(index);
                var before = File.ReadAllText(index);
                var outside = playlist ? before.Replace("\"title\":\"fixture\"", "\"title\":\"outside\"", StringComparison.Ordinal)
                    : before.Replace("\"r3-1\",\"RequestId\":\"r3-1\",\"Title\":\"fixture\"", "\"r3-1\",\"RequestId\":\"r3-1\",\"Title\":\"outside\"", StringComparison.Ordinal);
                Assert(outside != before && outside.Length == before.Length, "fixture must preserve index size");
                var edited = false;
                f.Library.RemovalCheckpoint = point =>
                {
                    if (point != "after-stage" || edited) return;
                    edited = true;
                    File.WriteAllText(index, outside);
                    File.SetLastWriteTimeUtc(index, time);
                    Assert(File.GetLastWriteTimeUtc(index) == time, "fixture did not restore mtime");
                    if (playlist) Assert(f.Library.GetPlaylists().Single().Title == "outside", "playlist cache did not see the same-size edit");
                    else Assert(f.Library.Read().Single(row => row.Id == "r3-1").Title == "outside", "audio cache did not see the same-size edit");
                };
                await Throws<MusicException>(() => playlist ? f.Library.ClearAsync(default) : f.Library.RemoveTrackAsync("r3-0", default));
                Assert(edited && !f.HasRemovalJournal && File.ReadAllText(index) == outside
                    && f.Library.Tracks().Count == 2 && f.Library.Tracks().All(row => row.Available), "stale index was published or a staged track was lost");
                f.Library.RemovalCheckpoint = null;
                Assert((await f.Library.RemoveTrackAsync("r3-0", default)).Removed == 1, "fresh revision could not be written");
            }
        });
        await Run("SecurityOffline0317: recovery protects early and late named streams on its own journal", async () =>
        {
            using var f = new SecurityOfflineFixture("receipt-with-stream", count: 1);
            await SecurityOfflineCrash(f, "after-stage");
            var journalBytes = File.ReadAllBytes(f.Journal);
            WriteSecurityOfflineNamedStream(f.Journal);
            var refusal = await Throws<MusicException>(() => f.Library.RecoverPendingAsync());
            Assert(refusal.Message.Contains("именованные потоки", StringComparison.Ordinal)
                && File.ReadAllBytes(f.Journal).SequenceEqual(journalBytes)
                && File.ReadAllText(f.Journal + SecurityOfflineNamedStream) == "personal fixture metadata"
                && !File.Exists(Path.Combine(f.Music, "track-0.mp3")), "protected journal was replayed or lost a stream");
            File.Delete(f.Journal + SecurityOfflineNamedStream);
            f.Library.RemovalCheckpoint = point =>
            {
                if (point == "recovery-after-index") WriteSecurityOfflineNamedStream(f.Journal);
            };
            await f.Library.RecoverPendingAsync();
            var archived = Directory.GetFiles(f.Music, ".ym-removal.recovery.preserved-*").Single();
            await f.Library.RecoverPendingAsync();
            Assert(!f.HasRemovalJournal && f.Library.Tracks().Single().Available && File.ReadAllBytes(archived).SequenceEqual(journalBytes)
                && File.ReadAllText(archived + SecurityOfflineNamedStream) == "personal fixture metadata", "retirement lost a late stream or scheduled a second replay");
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
                new PreferencesStore(paths.Data).Save(new PlayerPreferences { Volume = 0 });
                var foreignProfileFile = Path.Combine(paths.Data, "fixture.txt");
                File.WriteAllText(foreignProfileFile, "personal fixture data");
                await SecurityOfflineCrash(f, "after-stage");
                if (invalid) { var bytes = File.ReadAllBytes(f.Journal); bytes[^8] ^= 0x40; File.WriteAllBytes(f.Journal, bytes); }
                var before = Directory.GetFiles(f.Music).ToDictionary(p => p, File.ReadAllBytes);
                var result = await Task.Run(() => UninstallCleanup.Execute(paths));
                Assert(!File.Exists(Path.Combine(paths.Data, "settings.json"))
                    && File.ReadAllText(foreignProfileFile) == "personal fixture data" && result.PreservedForeignFiles == 1,
                    "uninstall left owned profile settings or changed the unrelated data file");
                if (invalid) Assert(!result.Complete && result.Issues.Count > 0 && before.All(pair => File.ReadAllBytes(pair.Key).SequenceEqual(pair.Value)), "bad journal authorized uninstall music deletion");
                else Assert(result.Complete && !Directory.Exists(f.Music), "authentic stale transaction survived owned-data uninstall");
            }
        });
    }

    // Root calls this before WPF initialization when the first argument is
    // --security-offline-child. Arguments: fixtureRoot, checkpoint, [recover|recover-unsupported|save|save-unsupported|remove-unsupported|remove-single].
    private static int SecurityOffline0317Child(string[] args)
    {
        if (args.Length is < 2 or > 3) return 2;
        var root = Path.GetFullPath(args[0]);
        if (!root.Contains(Path.DirectorySeparatorChar + "security-offline-0317" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || File.ReadAllText(Path.Combine(root, ".security-fixture")) != "Yamuse.SecurityOffline0317.Fixture") return 2;
        var recovering = args.Length == 3 && args[2] is "recover" or "recover-unsupported";
        var saving = args.Length == 3 && args[2] is "save" or "save-unsupported";
        var music = Path.Combine(root, "documents", "Yamuse", "Music");
        var journals = new[] { SecurityOfflineJournal, SecurityOfflineCommittedJournal, SecurityOfflineSaveJournal, ".ym-index.recovery" }
            .Select(name => (Active: Path.Combine(music, name), Paused: Path.Combine(root, "child-paused-" + name)))
            .Where(path => recovering && File.Exists(path.Active)).ToArray();
        // Install the fixture hook before scheduling any recovery worker. Only
        // this isolated test process temporarily renames its own known journal.
        foreach (var path in journals) File.Move(path.Active, path.Paused);
        using var library = new OfflineLibrary(music, new AudioBuffer(Path.Combine(root, "buffer"), saving ? SecurityOfflineSaveMedia() : null));
        library.RemovalCheckpoint = point =>
        {
            if (args[1] == "live-rollback-after-index" && point == "after-library-commit") throw new IOException("Injected live rollback.");
            if (point == args[1]) Environment.Exit(73);
        };
        library.SaveCheckpoint = library.RemovalCheckpoint;
        library.TemporaryPublicationCheckpoint = library.RemovalCheckpoint;
        if (args.Length == 3 && args[2] is "save-unsupported" or "recover-unsupported") library.FileIdentityErrorForChecks = 50;
        if (args.Length == 3 && args[2] == "remove-unsupported") library.RemovalAudioIdentityUnavailableForChecks = true;
        if (recovering)
        {
            foreach (var path in journals) File.Move(path.Paused, path.Active);
            library.RecoverPendingAsync().GetAwaiter().GetResult();
        }
        else if (saving) library.SaveAsync(SecurityOfflineSaveTrack(), SecurityOfflineSaveAudio(), default).GetAwaiter().GetResult();
        else if (args.Length == 3 && args[2] == "remove-single") library.RemoveTrackAsync("r3-0", default).GetAwaiter().GetResult();
        else library.ClearAsync(default, new SecurityOfflineObserver(p => { if (args[1] == "after-delete" && p.Phase == "delete") Environment.Exit(73); })).GetAwaiter().GetResult();
        return 3; // Expected checkpoint was not reached.
    }

    private static async Task SecurityOfflineCrash(SecurityOfflineFixture fixture, string point, bool recover = false, bool save = false,
        bool unsupportedIdentity = false, bool removeSingle = false)
    {
        var processPath = Environment.ProcessPath!;
        var start = new ProcessStartInfo(processPath) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = fixture.Root };
        if (Path.GetFileNameWithoutExtension(processPath).Equals("dotnet", StringComparison.OrdinalIgnoreCase)) start.ArgumentList.Add(typeof(Program).Assembly.Location);
        start.ArgumentList.Add("--security-offline-child"); start.ArgumentList.Add(fixture.Root); start.ArgumentList.Add(point);
        if (recover) start.ArgumentList.Add(unsupportedIdentity ? "recover-unsupported" : "recover");
        else if (save) start.ArgumentList.Add(unsupportedIdentity ? "save-unsupported" : "save");
        else if (removeSingle) start.ArgumentList.Add("remove-single");
        else if (unsupportedIdentity) start.ArgumentList.Add("remove-unsupported");
        using var child = Process.Start(start) ?? throw new IOException("Failed to start isolated recovery child.");
        await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
        Assert(child.ExitCode == 73, "isolated crash checkpoint failed: " + point + ", exit=" + child.ExitCode);
    }

    private static string SecurityOfflineHash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    private const string SecurityOfflineNamedStream = ":yamuse-fixture-note";
    private static void WriteSecurityOfflineNamedStream(string path)
    {
        using var stream = new FileStream(path + SecurityOfflineNamedStream, FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        using var writer = new StreamWriter(stream);
        writer.Write("personal fixture metadata");
    }
    private static TrackRow SecurityOfflineSaveTrack() => new() { Id = "save-fixture", RequestId = "save-fixture", Title = "Saved fixture", Available = true };
    private static AudioDescriptor SecurityOfflineSaveAudio() => new(new Uri("https://s1.vlx.yandex.net/save-fixture.mp3"), "mp3", 320);
    private static FakeHandler SecurityOfflineSaveMedia(byte[]? m4a = null, byte[]? flac = null)
    {
        var mp3 = SilentMp3Bytes(4096);
        return new((request, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(Path.GetExtension(request.RequestUri!.AbsolutePath) switch
            {
                ".m4a" => m4a ?? throw new InvalidOperationException("Missing AAC-MP4 fixture"),
                ".flac" => flac ?? throw new InvalidOperationException("Missing FLAC fixture"),
                _ => int.TryParse(Path.GetFileNameWithoutExtension(request.RequestUri.AbsolutePath), out var bitrate)
                    ? SilentMp3Bytes(4096, bitrate) : mp3
            })
        }));
    }
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
