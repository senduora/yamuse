using System.Buffers.Binary;
using System.ComponentModel;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32.SafeHandles;
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
        await Run("Cleanup015: artwork and loudness cleanup preserve cache files with extended attributes", Cleanup015CacheExtendedAttributes);
        await Run("Cleanup015: loudness recovery archives changed snapshots across load and clear", Cleanup015LoudnessRecovery);
        await Run("Cleanup015: cold loudness load restores the verified parked snapshot after a temporary refusal", Cleanup015LoudnessColdReadRestoresParked);
        await Run("Cleanup015: cache reads retire receipts only through verified file leases", Cleanup015CacheReadBoundRecovery);
        await Run("Cleanup015: loudness preserves both copies after an interrupted cache conflict", Cleanup015LoudnessConflict);
        await Run("Cleanup015: prepared store stages reject named streams and retain overlapped data I/O", Cleanup015PreparedStage);
        await Run("Cleanup015: closing abandoned guarded stage removes only that stage", () =>
        {
            var directory = Path.Combine(_directory, "cleanup015-abandoned-stage");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "settings.json");
            var temporary = Path.Combine(directory, ".fixture-stage.tmp");
            File.WriteAllText(path, "authoritative settings");
            using (var transaction = GuardedStoreFile.BeginWrite(path))
            using (var abandoned = transaction.CreateStage(temporary))
                RandomAccess.Write(abandoned, "incomplete stage"u8, 0);
            Assert(!UninstallPathGuard.EntryExists(temporary) && File.ReadAllText(path) == "authoritative settings",
                "closing an unpublished handle retained staged data or changed the prior file");
            return Task.CompletedTask;
        });
        await Run("Cleanup015: guarded replacement keeps a competing file and recovers the previous snapshot", () =>
        {
            var directory = Path.Combine(_directory, "cleanup015-guarded-park");
            Directory.CreateDirectory(directory);
            if (new DriveInfo(Path.GetPathRoot(directory)!).DriveFormat != "NTFS") return Task.CompletedTask;
            var path = Path.Combine(directory, "fixture.json");
            File.WriteAllText(path, "previous snapshot");
            File.SetAttributes(path, FileAttributes.Hidden);
            var recovery = new GuardedStoreRecovery(path);
            using (var transaction = GuardedStoreFile.BeginWrite(path))
            using (var stage = transaction.Stage("requested snapshot"u8))
            {
                using (var concurrentReader = File.OpenRead(path))
                    Assert(concurrentReader.Length == "previous snapshot".Length, "staging blocked an ordinary reader");
                transaction.AfterParkForChecks = () => File.WriteAllText(path, "competing snapshot");
                Assert(Cleanup015Denied(() => transaction.Commit(stage)), "a competing publisher was overwritten");
            }
            Assert(File.ReadAllText(path) == "competing snapshot" && File.ReadAllText(recovery.Previous) == "previous snapshot"
                && recovery.Pending && !Directory.EnumerateFiles(directory, "*.tmp").Any(), "conflict lost a snapshot or left staged data");
            var competing = Path.Combine(directory, "competing.json");
            using (var retry = GuardedStoreFile.BeginWrite(path))
                Assert(retry.ReadExisting(256)!.AsSpan().SequenceEqual("competing snapshot"u8), "recovery changed the competing snapshot");
            var archive = Directory.EnumerateFiles(directory, "*.previous.conflict-*.bak").Single();
            Assert(File.ReadAllText(archive) == "previous snapshot" && File.ReadAllText(path) == "competing snapshot" && !recovery.Pending
                && (File.GetAttributes(archive) & (FileAttributes.Hidden | FileAttributes.Archive)) == FileAttributes.Hidden,
                "recovery did not safely archive the prior snapshot and unblock subsequent writes");
            File.SetAttributes(path, FileAttributes.Hidden);
            using (var transaction = GuardedStoreFile.BeginWrite(path))
            using (var stage = transaction.Stage("next requested snapshot"u8))
            {
                transaction.AfterParkForChecks = () => File.WriteAllText(path, "next competing snapshot");
                Assert(Cleanup015Denied(() => transaction.Commit(stage)), "second competing publisher was overwritten");
            }
            File.Move(path, competing);
            Assert(GuardedStoreFile.Read(path, 256)!.AsSpan().SequenceEqual("competing snapshot"u8)
                && File.ReadAllText(competing) == "next competing snapshot" && !recovery.Pending
                && (File.GetAttributes(path) & (FileAttributes.Hidden | FileAttributes.Archive)) == FileAttributes.Hidden,
                "read did not recover the previous snapshot after the collision was cleared");
            GuardedStoreFile.Write(path, "requested snapshot"u8);
            Assert(File.ReadAllText(path) == "requested snapshot" && File.ReadAllText(archive) == "previous snapshot" && !recovery.Pending
                && (File.GetAttributes(path) & (FileAttributes.Hidden | FileAttributes.Archive)) == (FileAttributes.Hidden | FileAttributes.Archive),
                "successful retry left an unfinished recovery slot or removed the archive");
            return Task.CompletedTask;
        });
        await Run("Cleanup015: loudness retries a busy first read and merges stored plus new measurements", Cleanup015LoudnessStorage);
        await Run("Cleanup015: recovery preserves edited previous bytes and finishes an interrupted Archive repair", Cleanup015GuardedRecoveryProof);
        await Run("Cleanup015: recovery preserves legacy and oversized snapshots without exact proofs", Cleanup015GuardedRecoveryLegacy);
        await Run("Cleanup015: recovery preserves detected extended attributes and completes the authenticated slot", Cleanup015GuardedRecoveryMetadata);
        await Run("Cleanup015: diagnostics legacy creation never replaces an occupied destination", Cleanup015DiagnosticCapabilities);
        await Run("Cleanup015: diagnostics reports why replacement would lose named streams", Cleanup015DiagnosticNamedStreams);
        await Run("Cleanup015: a busy loudness cache retains the latest 512 pending measurements", Cleanup015LoudnessOrder);
        await Run("Cleanup015: loudness clear invalidates an in-flight analysis without touching its audio", Cleanup015LoudnessGeneration);
    }

    private static Task Cleanup015GuardedRecoveryProof()
    {
        var directory = Path.Combine(_directory, "cleanup015-recovery-proof");
        Directory.CreateDirectory(directory);
        if (new DriveInfo(Path.GetPathRoot(directory)!).DriveFormat != "NTFS") return Task.CompletedTask;
        foreach (var scenario in new[] { "published", "missing-artwork", "unchanged-artwork", "returned", "archived", "returned-edited" })
        {
            var folder = Directory.CreateDirectory(Path.Combine(directory, scenario)).FullName;
            var artwork = scenario.EndsWith("-artwork", StringComparison.Ordinal);
            var path = Path.Combine(folder, artwork ? new string('a', 64) + ".img" : "fixture.json");
            File.WriteAllText(path, "previous");
            File.SetAttributes(path, FileAttributes.Hidden);
            var kind = artwork ? GuardedStoreKind.Artwork : GuardedStoreKind.Regular;
            var recovery = new GuardedStoreRecovery(path, kind);
            var receipt = Cleanup015ParkStoreReplacement(path, recovery, publishReplacement: scenario == "published");
            var archive = $"{recovery.Previous}.conflict-{receipt.Conflict:N}.bak";
            var returned = scenario switch { "returned" or "returned-edited" => path, "archived" => archive, _ => null };
            if (returned is not null)
            {
                using (var ancestors = UninstallPathGuard.HoldAncestors(path))
                using (var original = UninstallPathGuard.OpenOwnedFile(recovery.Previous, read: true))
                    ManagedCacheCleanup.RenameByHandle(original, returned);
                if (scenario == "returned-edited") EditBytes(returned);
                File.SetAttributes(returned, FileAttributes.Hidden | FileAttributes.Archive);

                using (var busy = new FileStream(returned, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    busy.Lock(0, 1);
                    try
                    {
                        Assert(Cleanup015Denied(() => { using var blocked = GuardedStoreFile.BeginWrite(path); })
                            && recovery.Pending && (File.GetAttributes(returned) & FileAttributes.Archive) != 0,
                            "a temporary proof read failure retired the Archive receipt");
                    }
                    finally { busy.Unlock(0, 1); }
                }

                var expected = scenario == "returned-edited" ? "edited!!" : "previous";
                var expectedAttributes = scenario == "returned-edited" ? FileAttributes.Hidden | FileAttributes.Archive : FileAttributes.Hidden;
                var data = GuardedStoreFile.Read(path, 1024);
                Assert(File.ReadAllText(returned) == expected && File.GetAttributes(returned) == expectedAttributes
                    && !recovery.Pending && (returned != path ? data is null : data is not null && Encoding.UTF8.GetString(data) == expected),
                    "retry did not finish Archive correction exclusively on the proven returned file");
                continue;
            }

            if (scenario == "unchanged-artwork")
            {
                using var restored = GuardedStoreFile.BeginWrite(path, kind: kind);
                Assert(restored.ReadExisting(1024).AsSpan().SequenceEqual("previous"u8) && !recovery.Pending && !File.Exists(archive),
                    "authenticated unchanged artwork did not recover at its active name");
                continue;
            }

            EditBytes(recovery.Previous);
            using var next = GuardedStoreFile.BeginWrite(path, kind: kind);
            var current = next.ReadExisting(1024);
            Assert(File.ReadAllText(archive) == "edited!!" && !recovery.Pending
                && (artwork ? !next.Exists : current.AsSpan().SequenceEqual("replacement"u8)),
                "matching size and write time allowed changed previous bytes at the active name");
        }
        return Task.CompletedTask;

        static void EditBytes(string path)
        {
            var written = File.GetLastWriteTimeUtc(path);
            using (var edited = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.Read)) edited.Write("edited!!"u8);
            File.SetLastWriteTimeUtc(path, written);
        }
    }

    private static Task Cleanup015GuardedRecoveryLegacy()
    {
        var directory = Directory.CreateDirectory(Path.Combine(_directory, "cleanup015-recovery-legacy")).FullName;
        if (new DriveInfo(Path.GetPathRoot(directory)!).DriveFormat != "NTFS") return Task.CompletedTask;
        const long oversized = 32L * 1024 * 1024 + 1;
        foreach (var scenario in new[] { "legacy-store", "legacy-artwork", "oversized-store" })
        {
            var folder = Directory.CreateDirectory(Path.Combine(directory, scenario)).FullName;
            var artwork = scenario == "legacy-artwork";
            var path = Path.Combine(folder, artwork ? new string('a', 64) + ".img" : "fixture.json");
            using (var source = File.Create(path))
            {
                source.Write("previous"u8);
                if (scenario == "oversized-store") source.SetLength(oversized);
            }
            var kind = artwork ? GuardedStoreKind.Artwork : GuardedStoreKind.Regular;
            var recovery = new GuardedStoreRecovery(path, kind);
            var receipt = Cleanup015ParkStoreReplacement(path, recovery);
            if (scenario == "oversized-store")
            {
                Assert(receipt is { Version: 2, PreviousProof: null }, "oversized previous snapshot was hashed past the cap");
            }
            else
            {
                var legacy = receipt with { Version = 1, PreviousProof = null, ReplacementProof = null, Conflict = Guid.Empty };
                var entropy = SHA256.HashData(Encoding.UTF8.GetBytes("Yamuse.StoreRecovery.1\0" + path.ToUpperInvariant()));
                var marker = Directory.EnumerateFiles(folder, "*.receipt").Single();
                File.WriteAllBytes(marker, ProtectedData.Protect(JsonSerializer.SerializeToUtf8Bytes(legacy), entropy, DataProtectionScope.CurrentUser));
            }

            using var next = GuardedStoreFile.BeginWrite(path, kind: kind);
            Assert(!recovery.Pending, "legacy or oversized recovery remained pending");
            if (artwork)
            {
                var archive = Directory.EnumerateFiles(folder, "*.previous.conflict-*.bak").Single();
                Assert(!next.Exists && File.ReadAllText(archive) == "previous", "legacy artwork entered the active cache without an exact proof");
            }
            else if (scenario == "oversized-store")
            {
                Assert(next.Exists && new FileInfo(path).Length == oversized, "an oversized snapshot was lost before publication");
            }
            else
            {
                Assert(next.ReadExisting(1024).AsSpan().SequenceEqual("previous"u8), "an unchanged legacy store snapshot did not recover");
            }
        }
        return Task.CompletedTask;
    }

    private static GuardedStoreRecovery.Receipt Cleanup015ParkStoreReplacement(string path, GuardedStoreRecovery recovery,
        bool publishReplacement = false, byte[]? replacement = null)
    {
        using var writer = GuardedStoreFile.HoldWriteGate(path);
        using var ancestors = UninstallPathGuard.HoldAncestors(path);
        using var original = UninstallPathGuard.OpenOwnedFile(path, read: true);
        using var stage = UninstallPathGuard.CreateStagedFile(path + ".tmp", writeDacl: true, deleteOnClose: true);
        FileSecurityPreservation.PrepareReplacement(original, stage);
        RandomAccess.Write(stage, replacement is null ? "replacement"u8 : replacement, 0);
        FileSecurityPreservation.PreserveBasicInformation(original, stage);
        File.SetLastWriteTimeUtc(stage, DateTime.UtcNow);
        RandomAccess.FlushToDisk(stage);
        using var receiptHandle = recovery.Record(original, stage, out var receipt);
        ManagedCacheCleanup.RenameByHandle(original, recovery.Previous);
        if (publishReplacement)
        {
            ManagedCacheCleanup.RenameByHandle(stage, path);
            UninstallPathGuard.KeepFileOnClose(stage);
        }
        return receipt;
    }

    private static async Task Cleanup015GuardedRecoveryMetadata()
    {
        var directory = Directory.CreateDirectory(Path.Combine(_directory, "cleanup015-recovery-metadata")).FullName;
        if (new DriveInfo(Path.GetPathRoot(directory)!).DriveFormat != "NTFS") return;
        foreach (var alreadyReturned in new[] { false, true })
        {
            var folder = Directory.CreateDirectory(Path.Combine(directory, alreadyReturned ? "returned" : "parked")).FullName;
            var path = Path.Combine(folder, "fixture.json");
            File.WriteAllText(path, "previous");
            File.SetAttributes(path, FileAttributes.Hidden);
            var recovery = new GuardedStoreRecovery(path);
            var receipt = Cleanup015ParkStoreReplacement(path, recovery);
            if (alreadyReturned)
            {
                using var ancestors = UninstallPathGuard.HoldAncestors(path);
                using var previous = UninstallPathGuard.OpenOwnedFile(recovery.Previous, read: true);
                ManagedCacheCleanup.RenameByHandle(previous, path);
            }

            var source = alreadyReturned ? path : recovery.Previous;
            Cleanup015AddExtendedAttribute(source);
            File.SetAttributes(source, FileAttributes.Hidden | FileAttributes.Archive);
            using (var before = UninstallPathGuard.OpenOwnedFile(source, read: true, deleteAccess: false))
            {
                var actual = FileSecurityPreservation.GetPersistentRevision(before)!.Value;
                Assert(receipt.PreviousProof is not null && (actual with { Changed = receipt.Previous.Changed, Attributes = FileAttributes.Hidden }) == receipt.Previous,
                    "extended attributes did not isolate the metadata check from the base revision check");
            }

            var archive = $"{recovery.Previous}.conflict-{receipt.Conflict:N}.bak";
            var preserved = alreadyReturned ? path : archive;
            using (var transaction = GuardedStoreFile.BeginWrite(path))
                Assert(!recovery.Pending && transaction.Exists == alreadyReturned && File.ReadAllText(preserved) == "previous"
                    && (File.GetAttributes(preserved) & FileAttributes.Archive) != 0,
                    "detected extended attributes stalled recovery, moved the returned generic store, or changed its metadata");
            using var handle = UninstallPathGuard.OpenOwnedFile(preserved, read: true, deleteAccess: false);
            var detected = await Throws<FileSecurityPreservation.UnsupportedMetadataException>(() =>
            {
                FileSecurityPreservation.CheckReplacementMetadata(handle);
                return Task.CompletedTask;
            });
            Assert(detected.Metadata == FileSecurityPreservation.ReplacementMetadata.ExtendedAttributes,
                "the preserved file lost its extended attribute");
        }
    }

    private static void Cleanup015AddExtendedAttribute(string path)
    {
        using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Write, FileShare.Read);
        Cleanup015AddExtendedAttribute(handle);
    }

    private static void Cleanup015AddExtendedAttribute(SafeFileHandle handle)
    {
        var name = "YamuseFixture"u8;
        var value = "fixture"u8;
        var entry = new byte[8 + name.Length + 1 + value.Length];
        entry[5] = (byte)name.Length;
        BinaryPrimitives.WriteUInt16LittleEndian(entry.AsSpan(6), (ushort)value.Length);
        name.CopyTo(entry.AsSpan(8));
        value.CopyTo(entry.AsSpan(8 + name.Length + 1));
        var status = NtSetEaFile(handle, out _, entry, (uint)entry.Length);
        Assert(status == 0, $"Could not attach the synthetic extended attribute (0x{status:X8}).");
    }

    [DllImport("ntdll.dll", ExactSpelling = true)]
    private static extern int NtSetEaFile(SafeFileHandle handle, out Cleanup015IoStatus status, byte[] entry, uint size);

    [StructLayout(LayoutKind.Sequential)]
    private struct Cleanup015IoStatus
    {
        internal IntPtr Status;
        internal UIntPtr Information;
    }

    private static async Task Cleanup015DiagnosticCapabilities()
    {
        var directory = Directory.CreateDirectory(Path.Combine(_directory, "cleanup015-diagnostic-capabilities")).FullName;
        using var diagnostics = new Diagnostics { CheckReportCapabilitiesForChecks = () => throw new Win32Exception(50) };
        var path = Path.Combine(directory, "report.json");
        Assert(diagnostics.Save(path), "legacy report creation did not finish");
        var known = File.ReadAllBytes(path);
        using var document = JsonDocument.Parse(known);
        Assert(document.RootElement.GetProperty("schema").GetInt32() == 1, "legacy fallback did not publish the requested report");
        var refused = await Throws<Diagnostics.ReplacementUnavailableException>(() => { diagnostics.Save(path, () => true); return Task.CompletedTask; });
        Assert(refused.Message.Contains("Выберите новое имя", StringComparison.Ordinal) && File.ReadAllBytes(path).SequenceEqual(known),
            "unsupported replacement did not preserve the report with an actionable error");
        using (var fixture = new SaFixture("diagnostics-capabilities-status"))
        {
            SaInvoke(fixture.Window, "ShowError", refused, "");
            Assert(Control<TextBlock>(fixture.Window, "StatusText").Text == refused.Message,
                "the status replaced the actionable diagnostic error with a generic retry");
        }
        var collision = Path.Combine(directory, "collision.json");
        diagnostics.BeforeReportPublicationForChecks = () => File.WriteAllText(collision, "unrelated report");
        await Throws<IOException>(() => { diagnostics.Save(collision); return Task.CompletedTask; });
        Assert(File.ReadAllText(collision) == "unrelated report" && !Directory.EnumerateFiles(directory, "*.tmp").Any(),
            "legacy report fallback replaced a concurrent file or retained its temporary stage");
    }

    private static async Task Cleanup015DiagnosticNamedStreams()
    {
        var directory = Directory.CreateDirectory(Path.Combine(_directory, "cleanup015-diagnostic-streams")).FullName;
        if (new DriveInfo(Path.GetPathRoot(directory)!).DriveFormat != "NTFS") return;
        var path = Path.Combine(directory, "report.json");
        var stream = path + ":fixture-note";
        File.WriteAllText(path, "saved report");
        File.WriteAllText(stream, "saved annotation");
        using var diagnostics = new Diagnostics();
        var refused = await Throws<Diagnostics.ReplacementUnavailableException>(() =>
        {
            diagnostics.Save(path, () => true);
            return Task.CompletedTask;
        });
        Assert(refused.Message.Contains("именованные потоки", StringComparison.Ordinal)
            && refused.Message.Contains("Выберите новое имя", StringComparison.Ordinal)
            && refused.Message.Contains("Прежний отчёт сохранён", StringComparison.Ordinal)
            && File.ReadAllText(path) == "saved report" && File.ReadAllText(stream) == "saved annotation"
            && !Directory.EnumerateFiles(directory, "*.tmp").Any(), "the ADS refusal lost data or omitted the available save option");
        using var fixture = new SaFixture("diagnostics-stream-status");
        SaInvoke(fixture.Window, "ShowError", refused, "");
        Assert(Control<TextBlock>(fixture.Window, "StatusText").Text == refused.Message, "the status hid the precise replacement refusal");
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
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(SilentMp3Bytes(4096)) };
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
        var values = typeof(LoudnessCache).GetProperty("_values", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(cache) as OrderedDictionary<string, LoudnessResult>;
        Assert(values is { Count: 0 }, "an existing cache instance retained memory after static clear");
        var directoryCache = Path.Combine(dir, "directory-at-cache-path");
        var fixedName = Path.Combine(directoryCache, "loudness-v1.json");
        Directory.CreateDirectory(fixedName);
        Assert((await LoudnessCache.ClearDirectoryAsync(directoryCache, default)).Preserved == 1 && Directory.Exists(fixedName),
            "a directory at the cache path was treated as an absent entry");
        foreach (var invalid in new[] { "{\"personal\":42}", "{\"\\uD800\":42}", "{\"" + key + "\":{\"Peak\":0.7}}", "{\"" + key + "\":{\"IntegratedLufs\":\"wrong\",\"Peak\":0.7}}" })
        {
            File.WriteAllText(path, invalid); var result = await cache.ClearAsync(default);
            Assert(result.Preserved == 1 && File.ReadAllText(path) == invalid, "invalid/foreign loudness file was deleted");
        }
        File.WriteAllText(path, JsonSerializer.Serialize(stored)); File.SetAttributes(path, FileAttributes.ReadOnly);
        Assert((await cache.ClearAsync(default)).Failed == 1 && File.Exists(path), "readonly loudness data was deleted"); File.SetAttributes(path, FileAttributes.Normal);
        var link = System.IO.Path.Combine(_directory, "cleanup015-loudness-link"); CreateUninstallJunction(link, dir);
        Assert((await LoudnessCache.ClearDirectoryAsync(link, default)).Failed == 1 && File.Exists(path), "linked loudness clear touched target data");
        if (new DriveInfo(Path.GetPathRoot(path)!).DriveFormat == "NTFS")
        {
            var contents = File.ReadAllText(path);
            var stream = path + ":yamuse-fixture";
            File.WriteAllText(stream, "benign metadata");
            var result = await cache.ClearAsync(default);
            Assert(result is { Removed: 0, Preserved: 1, Failed: 0, RemovedBytes: 0 }
                && File.ReadAllText(path) == contents && File.ReadAllText(stream) == "benign metadata",
                "clearing a valid cache lost its named stream or reported it removed");
        }
    }

    private static async Task Cleanup015CacheExtendedAttributes()
    {
        var root = Directory.CreateDirectory(Path.Combine(_directory, "cleanup015-cache-ea")).FullName;
        if (new DriveInfo(Path.GetPathRoot(root)!).DriveFormat != "NTFS") return;

        var artworkDirectory = Directory.CreateDirectory(Path.Combine(root, "artwork")).FullName;
        using var artwork = new ArtworkCache(artworkDirectory);
        var image = HomeArtworkPng(Colors.Red);
        var protectedImage = Path.Combine(artworkDirectory, new string('a', 64) + ".img");
        var ordinaryImage = Path.Combine(artworkDirectory, new string('b', 64) + ".img");
        File.WriteAllBytes(protectedImage, image);
        File.WriteAllBytes(ordinaryImage, image);
        Cleanup015AddExtendedAttribute(protectedImage);
        var clearedArtwork = await artwork.ClearAsync(default);
        Assert(clearedArtwork == new CacheCleanupResult(1, 1, 0, image.Length) && !File.Exists(ordinaryImage),
            "artwork clear did not count the EA file or failed to remove an ordinary image");
        AssertPreserved(protectedImage, image);

        var loudnessDirectory = Directory.CreateDirectory(Path.Combine(root, "loudness")).FullName;
        var path = Path.Combine(loudnessDirectory, "loudness-v1.json");
        var snapshot = JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, LoudnessResult> { [new string('A', 64)] = new(-14, .7) });
        File.WriteAllBytes(path, snapshot);
        Cleanup015AddExtendedAttribute(path);
        var clearedLoudness = await LoudnessCache.ClearDirectoryAsync(loudnessDirectory, default);
        Assert(clearedLoudness == new CacheCleanupResult(0, 1, 0, 0), "loudness clear did not count the EA file as preserved");
        AssertPreserved(path, snapshot);

        static void AssertPreserved(string path, byte[] expected)
        {
            using var handle = UninstallPathGuard.OpenOwnedFile(path, read: true, deleteAccess: false);
            Assert(File.ReadAllBytes(path).AsSpan().SequenceEqual(expected)
                && !FileSecurityPreservation.HasNamedDataStreams(handle)
                && FileSecurityPreservation.HasNamedStreamsOrExtendedAttributes(handle),
                "clear lost the original bytes or the extended attribute of the cache file");
        }
    }

    private static async Task Cleanup015LoudnessRecovery()
    {
        var root = Directory.CreateDirectory(Path.Combine(_directory, "cleanup015-loudness-recovery")).FullName;
        if (new DriveInfo(Path.GetPathRoot(root)!).DriveFormat != "NTFS") return;
        var key = new string('A', 64);
        var previous = Snapshot(-14);
        var changed = Snapshot(-12);
        var replacement = Snapshot(-16);
        Assert(previous.Length == changed.Length, "fixture should compare different data with the same length");
        var load = typeof(LoudnessCache).GetMethod("Load", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var values = typeof(LoudnessCache).GetProperty("_values", BindingFlags.NonPublic | BindingFlags.Instance)!;
        foreach (var scenario in new[] { "returned-direct", "returned-read", "unchanged-read", "parked", "published" })
        {
            var directory = Directory.CreateDirectory(Path.Combine(root, scenario)).FullName;
            var path = Path.Combine(directory, "loudness-v1.json");
            File.WriteAllBytes(path, previous);
            File.SetAttributes(path, scenario == "returned-direct" ? FileAttributes.Hidden | FileAttributes.Archive : FileAttributes.Hidden);
            var recovery = new GuardedStoreRecovery(path); // The pre-existing fixed-name receipt.
            var receipt = Cleanup015ParkStoreReplacement(path, recovery, publishReplacement: scenario == "published", replacement: replacement);
            var returned = scenario.EndsWith("-read", StringComparison.Ordinal) || scenario == "returned-direct";
            if (returned)
            {
                using var writer = GuardedStoreFile.HoldWriteGate(path);
                using var parents = UninstallPathGuard.HoldAncestors(path);
                using var parked = UninstallPathGuard.OpenOwnedFile(recovery.Previous, read: true);
                ManagedCacheCleanup.RenameByHandle(parked, path);
            }

            if (scenario != "unchanged-read")
            {
                var editPath = returned ? path : recovery.Previous;
                var written = File.GetLastWriteTimeUtc(editPath);
                using (var edit = new FileStream(editPath, FileMode.Open, FileAccess.Write, FileShare.Read)) edit.Write(changed);
                File.SetLastWriteTimeUtc(editPath, written);
            }

            var cache = new LoudnessCache(directory);
            if (scenario == "returned-read")
            {
                using var busy = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                busy.Lock(0, 1);
                try
                {
                    load.Invoke(cache, null);
                    Assert(values.GetValue(cache) is null && recovery.Pending && File.Exists(path),
                        "temporary proof failure became a successful empty cache or retired the receipt");
                }
                finally { busy.Unlock(0, 1); }
            }

            if (scenario.EndsWith("-read", StringComparison.Ordinal))
            {
                load.Invoke(cache, null);
                var loaded = (OrderedDictionary<string, LoudnessResult>)values.GetValue(cache)!;
                Assert(scenario == "unchanged-read" ? loaded.TryGetValue(key, out var measurement) && measurement.IntegratedLufs == -14
                    : loaded.Count == 0 && !File.Exists(path), "ordinary load did not distinguish unchanged and changed recovered snapshots");
                Assert(!recovery.Pending, "successful cache load left an unresolved receipt");
            }
            else if (scenario == "published")
            {
                using (var busy = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    busy.Lock(0, 1);
                    try
                    {
                        load.Invoke(cache, null);
                        Assert(values.GetValue(cache) is null && recovery.Pending && File.Exists(recovery.Previous),
                            "unavailable published proof prevented retry or altered the slot");
                    }
                    finally { busy.Unlock(0, 1); }
                }
                load.Invoke(cache, null);
                var loaded = (OrderedDictionary<string, LoudnessResult>)values.GetValue(cache)!;
                Assert(loaded.TryGetValue(key, out var measurement) && measurement.IntegratedLufs == -16
                    && recovery.Pending && File.ReadAllBytes(recovery.Previous).AsSpan().SequenceEqual(changed),
                    "verified published data could not be read without consuming the pending slot");
            }
            else if (scenario == "parked")
            {
                load.Invoke(cache, null);
                Assert(values.GetValue(cache) is null && recovery.Pending && !File.Exists(path)
                    && File.ReadAllBytes(recovery.Previous).AsSpan().SequenceEqual(changed), "cold load attempted recovery while publication was pending");
            }

            var expectedRemoved = scenario is "unchanged-read" or "published" ? 1 : 0;
            var expectedPreserved = scenario == "unchanged-read" ? 0 : 1;
            var expectedBytes = scenario == "unchanged-read" ? previous.Length : scenario == "published" ? replacement.Length : 0;
            var result = await cache.ClearAsync(default);
            Assert(result == new CacheCleanupResult(expectedRemoved, expectedPreserved, 0, expectedBytes) && !recovery.Pending && !File.Exists(path),
                $"loudness cleanup reported an incorrect result for {scenario}");
            var archive = $"{recovery.Previous}.conflict-{receipt.Conflict:N}.bak";
            Assert(scenario == "unchanged-read" ? !File.Exists(archive) : File.ReadAllBytes(archive).AsSpan().SequenceEqual(changed),
                $"loudness cleanup lost or archived the wrong snapshot for {scenario}");
        }

        byte[] Snapshot(double loudness) => JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, LoudnessResult> { [key] = new(loudness, .7) });
    }

    private static Task Cleanup015LoudnessColdReadRestoresParked()
    {
        var directory = Directory.CreateDirectory(Path.Combine(_directory, "cleanup015-loudness-parked-read-recovery")).FullName;
        if (new DriveInfo(Path.GetPathRoot(directory)!).DriveFormat != "NTFS") return Task.CompletedTask;
        var path = Path.Combine(directory, "loudness-v1.json");
        var key = new string('A', 64);
        var previous = JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, LoudnessResult> { [key] = new(-14, .7) });
        var replacement = JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, LoudnessResult> { [key] = new(-16, .7) });
        var expectedHash = SHA256.HashData(previous);
        File.WriteAllBytes(path, previous);
        var recovery = new GuardedStoreRecovery(path);
        Cleanup015ParkStoreReplacement(path, recovery, replacement: replacement);
        var marker = Directory.EnumerateFiles(directory, "*.receipt").Single();
        var markerBytes = File.ReadAllBytes(marker);
        Assert(!File.Exists(path) && recovery.Pending && File.Exists(recovery.Previous), "fixture did not leave a recoverable parked snapshot");

        var cache = new LoudnessCache(directory);
        var load = typeof(LoudnessCache).GetMethod("Load", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var values = typeof(LoudnessCache).GetProperty("_values", BindingFlags.NonPublic | BindingFlags.Instance)!;
        using (var busy = new FileStream(recovery.Previous, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
        {
            busy.Lock(0, 1);
            try
            {
                load.Invoke(cache, null);
                Assert(values.GetValue(cache) is null && recovery.Pending && !File.Exists(path)
                    && File.Exists(recovery.Previous) && File.ReadAllBytes(marker).SequenceEqual(markerBytes),
                    "a temporary refusal changed the parked journal or prevented retry");
            }
            finally { busy.Unlock(0, 1); }
        }

        load.Invoke(cache, null);
        Assert(values.GetValue(cache) is OrderedDictionary<string, LoudnessResult> loaded
            && loaded.TryGetValue(key, out var result) && result.IntegratedLufs == -14
            && !recovery.Pending && SHA256.HashData(File.ReadAllBytes(path)).SequenceEqual(expectedHash),
            "cold loudness load did not return the exact former snapshot");
        return Task.CompletedTask;
    }

    private static Task Cleanup015CacheReadBoundRecovery()
    {
        var root = Directory.CreateDirectory(Path.Combine(_directory, "cleanup015-cache-bound-read")).FullName;
        if (new DriveInfo(Path.GetPathRoot(root)!).DriveFormat != "NTFS") return Task.CompletedTask;
        foreach (var exact in new[] { false, true })
        {
            var directory = Directory.CreateDirectory(Path.Combine(root, "stale-protection-" + exact)).FullName;
            var path = Path.Combine(directory, "loudness-v1.json");
            File.WriteAllText(path, "previous");
            var slot = new GuardedStoreRecovery(path, GuardedStoreKind.Loudness);
            Cleanup015ParkStoreReplacement(path, slot);
            File.WriteAllText(path, "competing snapshot");
            using (var busy = new FileStream(slot.Previous, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
            {
                busy.Lock(0, 1);
                try { Assert(Cleanup015Denied(() => { using var write = GuardedStoreFile.BeginWrite(path, kind: GuardedStoreKind.Loudness); }), "fixture did not interrupt recovery"); }
                finally { busy.Unlock(0, 1); }
            }
            var protection = Directory.EnumerateFiles(directory, "*.protected").Single();
            var witness = File.ReadAllBytes(protection);
            var competitor = Path.Combine(directory, "preserved.json");
            File.Move(path, competitor);
            if (!exact)
            {
                var written = File.GetLastWriteTimeUtc(slot.Previous);
                using (var edit = new FileStream(slot.Previous, FileMode.Open, FileAccess.Write, FileShare.Read)) edit.Write("changed!"u8);
                File.SetLastWriteTimeUtc(slot.Previous, written);
                Assert(Cleanup015Denied(() => GuardedStoreFile.Read(path, 256, GuardedStoreKind.Loudness))
                    && slot.Pending && !File.Exists(path) && File.Exists(slot.Previous) && File.ReadAllBytes(protection).SequenceEqual(witness),
                    "unverified parked bytes retired their stale protection or changed the pending slot");
                continue;
            }
            Assert(GuardedStoreFile.Read(path, 256, GuardedStoreKind.Loudness).AsSpan().SequenceEqual("previous"u8)
                && !slot.Pending && !File.Exists(protection) && File.ReadAllText(competitor) == "competing snapshot",
                "stale protection blocked a verified parked file or altered the specifically protected bytes");
        }

        foreach (var (published, cleanup) in new[] { (false, false), (true, false), (false, true), (true, true) })
        {
            var directory = Directory.CreateDirectory(Path.Combine(root, $"active-receipt-{published}-{cleanup}")).FullName;
            var path = Path.Combine(directory, "loudness-v1.json");
            File.WriteAllText(path, "previous");
            File.SetAttributes(path, FileAttributes.Hidden);
            var slot = new GuardedStoreRecovery(path, GuardedStoreKind.Loudness);
            Cleanup015ParkStoreReplacement(path, slot, publishReplacement: published);
            using (var parents = UninstallPathGuard.HoldAncestors(path))
            using (var writer = GuardedStoreFile.HoldWriteGate(path))
            using (var parked = UninstallPathGuard.OpenOwnedFile(slot.Previous, read: true))
            {
                if (published) UninstallPathGuard.DeleteByHandle(parked);
                else ManagedCacheCleanup.RenameByHandle(parked, path);
            }
            var identity = FileIdentity(path);
            using var ancestors = UninstallPathGuard.HoldAncestors(path);
            using (var writer = GuardedStoreFile.HoldWriteGate(path))
            using (var lease = cleanup
                ? GuardedStoreFile.OpenCacheForCleanupUnderWriteGate(path, GuardedStoreKind.Loudness, out _)
                : GuardedStoreFile.OpenForRead(path, kind: GuardedStoreKind.Loudness))
            {
                Assert(lease is not null && UninstallPathGuard.GetStableFileIdentity(lease) == identity && !slot.Pending
                    && GuardedStoreFile.ReadFromHandle(lease, 256).AsSpan().SequenceEqual(published ? "replacement"u8 : "previous"u8)
                    && (published || File.GetAttributes(lease) == FileAttributes.Hidden),
                    "the proven current revision or its Archive repair was lost during receipt retirement");
                if (cleanup)
                {
                    Assert(Cleanup015Denied(() => File.Move(path, path + ".other")), "the cleanup lease did not keep its proven file in place");
                    UninstallPathGuard.DeleteByHandle(lease!);
                }
            }
            if (cleanup) Assert(!File.Exists(path), "exact cache cleanup did not remove the proved file");
        }
        return Task.CompletedTask;

        static UninstallFileIdentity FileIdentity(string path)
        {
            using var handle = UninstallPathGuard.OpenOwnedFile(path, read: true, deleteAccess: false);
            return UninstallPathGuard.GetStableFileIdentity(handle);
        }
    }

    private static async Task Cleanup015LoudnessConflict()
    {
        var root = Directory.CreateDirectory(Path.Combine(_directory, "cleanup015-loudness-conflict")).FullName;
        if (new DriveInfo(Path.GetPathRoot(root)!).DriveFormat != "NTFS") return;
        const string filename = "loudness-v1.json";
        var key = new string('A', 64);
        var previous = Snapshot(-14);
        var staged = Snapshot(-18);
        var occupant = Snapshot(-12);
        var load = typeof(LoudnessCache).GetMethod("Load", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var values = typeof(LoudnessCache).GetProperty("_values", BindingFlags.NonPublic | BindingFlags.Instance)!;

        foreach (var scenario in new[] { "occupied", "occupied-after-archive", "edited-published" })
        {
            var directory = Directory.CreateDirectory(Path.Combine(root, scenario)).FullName;
            var path = Path.Combine(directory, filename);
            File.WriteAllBytes(path, previous);
            var receiptSlot = new GuardedStoreRecovery(path);
            var receipt = Cleanup015ParkStoreReplacement(path, receiptSlot, publishReplacement: scenario == "edited-published", replacement: staged);
            var archive = $"{receiptSlot.Previous}.conflict-{receipt.Conflict:N}.bak";
            if (scenario == "occupied-after-archive")
            {
                using var writer = GuardedStoreFile.HoldWriteGate(path);
                using var parents = UninstallPathGuard.HoldAncestors(path);
                using var parked = UninstallPathGuard.OpenOwnedFile(receiptSlot.Previous, read: true);
                ManagedCacheCleanup.RenameByHandle(parked, archive);
            }
            if (scenario == "edited-published")
            {
                var written = File.GetLastWriteTimeUtc(path);
                using (var edit = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.Read)) edit.Write(occupant);
                File.SetLastWriteTimeUtc(path, written);
            }
            else File.WriteAllBytes(path, occupant);

            var cache = new LoudnessCache(directory);
            if (scenario == "occupied-after-archive")
            {
                load.Invoke(cache, null);
                Assert(values.GetValue(cache) is OrderedDictionary<string, LoudnessResult> { Count: 0 }
                    && !receiptSlot.Pending && File.Exists(path), "load did not preserve a witnessed incompatible occupant");
            }
            else if (scenario == "occupied")
            {
                var record = Directory.EnumerateFiles(directory, "*.receipt").Single();
                var recordBytes = File.ReadAllBytes(record);
                load.Invoke(cache, null);
                Assert(values.GetValue(cache) is null && File.ReadAllBytes(record).AsSpan().SequenceEqual(recordBytes)
                    && File.ReadAllBytes(receiptSlot.Previous).AsSpan().SequenceEqual(previous)
                    && File.ReadAllBytes(path).AsSpan().SequenceEqual(occupant) && !File.Exists(archive)
                    && !Directory.EnumerateFiles(directory, "*.protected").Any(),
                    "cold load admitted an unproven measurement or changed the pending slot");
            }
            else if (scenario == "edited-published")
            {
                using var busy = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                busy.Lock(0, 1);
                try
                {
                    Assert(await cache.ClearAsync(default) == new CacheCleanupResult(0, 0, 1, 0)
                        && receiptSlot.Pending && File.Exists(receiptSlot.Previous), "temporary proof refusal consumed the conflict receipt");
                }
                finally { busy.Unlock(0, 1); }
            }

            var cacheSlot = new GuardedStoreRecovery(path, GuardedStoreKind.Loudness);
            for (var pass = 0; pass < 2; pass++)
            {
                if (pass == 1)
                {
                    var created = File.GetCreationTimeUtc(path);
                    File.SetCreationTimeUtc(path, created.AddHours(-2));
                    Assert(File.GetCreationTimeUtc(path) != created, "fixture did not change the creation time of the protected snapshot");
                }
                Assert(await cache.ClearAsync(default) == new CacheCleanupResult(0, 2, 0, 0)
                    && File.ReadAllBytes(path).AsSpan().SequenceEqual(occupant) && File.ReadAllBytes(archive).AsSpan().SequenceEqual(previous)
                    && cacheSlot.Pending && !receiptSlot.Pending, $"clear did not preserve both conflict copies for {scenario}, pass {pass}");
            }
            Assert(Cleanup015Denied(() => { using var write = GuardedStoreFile.BeginWrite(path, kind: GuardedStoreKind.Loudness); }),
                "a later cache writer could overwrite an object preserved by recovery");

            var moved = Path.Combine(directory, "saved-copy.json");
            File.Move(path, moved);
            Assert(GuardedStoreFile.Read(path, 256, GuardedStoreKind.Loudness) is null && cacheSlot.Pending,
                "a read retired protection while its recorded file was temporarily away");
            Assert(await cache.ClearAsync(default) == new CacheCleanupResult(0, 1, 0, 0) && cacheSlot.Pending,
                "cleanup retired protection without a different active file identity");
            using (var write = GuardedStoreFile.BeginWrite(path, kind: GuardedStoreKind.Loudness))
                Assert(!write.Exists && cacheSlot.Pending, "an empty slot lost its dormant protection before publication");
            File.Move(moved, path);
            Assert(await cache.ClearAsync(default) == new CacheCleanupResult(0, 2, 0, 0) && cacheSlot.Pending
                && File.ReadAllBytes(path).AsSpan().SequenceEqual(occupant), "the returning exact protected file was removed");
            File.Move(path, moved);
            File.WriteAllBytes(path, staged);
            Assert(await cache.ClearAsync(default) == new CacheCleanupResult(1, 1, 0, staged.Length) && !cacheSlot.Pending
                && !File.Exists(path) && File.ReadAllBytes(moved).AsSpan().SequenceEqual(occupant),
                "the authenticated protection outlived the exact conflicting object at its name");
        }

        byte[] Snapshot(double loudness) => JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, LoudnessResult> { [key] = new(loudness, .7) });
    }

    private static async Task Cleanup015PreparedStage()
    {
        var directory = Path.Combine(_directory, "cleanup015-prepared-stage");
        Directory.CreateDirectory(directory);
        if (new DriveInfo(Path.GetPathRoot(directory)!).DriveFormat != "NTFS") return;
        var originalPath = Path.Combine(directory, "original.json");
        File.WriteAllText(originalPath, "{}");
        using var original = UninstallPathGuard.OpenOwnedFile(originalPath, read: true, deleteAccess: false);
        var goodPath = Path.Combine(directory, "good.tmp");
        using (var staged = UninstallPathGuard.CreateStagedFile(goodPath, asynchronous: true, writeDacl: true))
        {
            Assert(staged.IsAsync, "fixture stage must have an overlapped handle");
            await RandomAccess.WriteAsync(staged, "{}"u8.ToArray(), 0);
            FileSecurityPreservation.PrepareReplacement(original, staged);
            await RandomAccess.WriteAsync(staged, "[]"u8.ToArray(), 0);
        }
        Assert(File.ReadAllText(goodPath) == "[]", "metadata checks disrupted async writes on the same handle");

        var protectedPath = Path.Combine(directory, "protected.tmp");
        var stream = protectedPath + ":yamuse-fixture";
        File.WriteAllBytes(protectedPath, []);
        File.WriteAllText(stream, "benign metadata");
        var protectedFile = new FileInfo(protectedPath);
        using (var protectedStage = protectedFile.Create(FileMode.Open, FileSystemRights.FullControl, FileShare.Read, 4096,
            FileOptions.None, protectedFile.GetAccessControl(AccessControlSections.Access)))
        {
            var handle = protectedStage.SafeFileHandle;
            Assert(FileSecurityPreservation.HasNamedDataStreams(handle), "fixture stage lost its named stream before preparation");
            var error = await Throws<IOException>(() => { FileSecurityPreservation.PrepareReplacement(original, handle); return Task.CompletedTask; });
            Assert(error.Message.Contains("специальными свойствами", StringComparison.Ordinal), "preparation failed before reaching the metadata policy");
        }
        Assert(protectedFile.Length == 0 && File.ReadAllText(originalPath) == "{}" && File.ReadAllText(stream) == "benign metadata",
            "prepared staging check modified the original or named stream");
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

    private static Task Cleanup015LoudnessStorage()
    {
        var directory = Path.Combine(_directory, "cleanup015-loudness-storage", "cache");
        var path = Path.Combine(directory, "loudness-v1.json");
        var load = typeof(LoudnessCache).GetMethod("Load", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var save = typeof(LoudnessCache).GetMethod("Save", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var values = typeof(LoudnessCache).GetProperty("_values", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var cache = new LoudnessCache(directory);
        var state = typeof(LoudnessCache).GetField("_state", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(cache)!;
        var pending = (OrderedDictionary<string, LoudnessResult>)state.GetType().GetProperty("Pending", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(state)!;
        load.Invoke(cache, null);
        Assert(!Directory.Exists(directory), "cache read created missing parents");
        Directory.CreateDirectory(directory);
        var oldKey = new string('A', 64);
        var newKey = new string('F', 64);
        var oldMeasurement = new LoudnessResult(-16, .5);
        var newMeasurement = new LoudnessResult(-12, .6);
        File.WriteAllText(path, JsonSerializer.Serialize(new Dictionary<string, LoudnessResult> { [oldKey] = oldMeasurement }));
        var before = File.ReadAllBytes(path);
        values.SetValue(cache, null);
        using (File.Open(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            load.Invoke(cache, null);
            Assert(values.GetValue(cache) is null, "a sharing error was mistaken for an empty cache");
            pending[newKey] = newMeasurement;
            save.Invoke(cache, null);
            Assert(pending.Count == 1, "sharing error discarded a pending measurement");
        }
        using (File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            save.Invoke(cache, null);
            Assert(before.SequenceEqual(File.ReadAllBytes(path)) && !Directory.EnumerateFiles(directory, "*.tmp").Any(),
                "busy cache replacement changed the previous file or left staging data");
        }
        save.Invoke(cache, null);
        values.SetValue(cache, null);
        load.Invoke(cache, null);
        var saved = (OrderedDictionary<string, LoudnessResult>)values.GetValue(cache)!;
        Assert(saved.Count == 2 && saved[oldKey] == oldMeasurement && saved[newKey] == newMeasurement
            && pending.Count == 0, "retry did not merge the saved and pending measurements");
        File.WriteAllText(path, new string(' ', 256 * 1024) + JsonSerializer.Serialize(saved));
        values.SetValue(cache, null);
        load.Invoke(cache, null);
        Assert(((OrderedDictionary<string, LoudnessResult>)values.GetValue(cache)!).Count == 0, "oversized valid cache document reached normal deserialization");
        foreach (var contents in new[] { File.ReadAllText(path), "{\"personal\":42}", "{\"\\uD800\":42}", "" })
        {
            File.WriteAllText(path, contents);
            pending[newKey] = newMeasurement;
            save.Invoke(cache, null);
            Assert(File.ReadAllText(path) == contents && pending[newKey] == newMeasurement,
                "saving an analysis overwrote a file without a verified cache schema or lost the measurement");
        }
        return Task.CompletedTask;
    }

    private static Task Cleanup015LoudnessOrder()
    {
        var directory = Path.Combine(_directory, "cleanup015-loudness-order");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "loudness-v1.json");
        File.WriteAllText(path, "{}");
        var cache = new LoudnessCache(directory);
        var state = typeof(LoudnessCache).GetField("_state", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(cache)!;
        var pending = (OrderedDictionary<string, LoudnessResult>)state.GetType().GetProperty("Pending", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(state)!;
        var trim = typeof(LoudnessCache).GetMethod("Trim", BindingFlags.NonPublic | BindingFlags.Static)!;
        var save = typeof(LoudnessCache).GetMethod("Save", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var keys = Enumerable.Range(1, 514).Select(value => value.ToString("X64")).ToArray();
        using (File.Open(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            foreach (var key in keys)
            {
                pending.Add(key, new LoudnessResult(-14, .5));
                trim.Invoke(null, [pending]);
            }
            save.Invoke(cache, null);
            Assert(pending.Keys.SequenceEqual(keys.Skip(2)), "the last measurements did not survive a busy cache");
        }
        save.Invoke(cache, null);
        using var stored = JsonDocument.Parse(File.ReadAllBytes(path));
        Assert(stored.RootElement.EnumerateObject().Select(property => property.Name).SequenceEqual(keys.Skip(2))
            && pending.Count == 0, "the recovered publication did not retain the newest 512 measurements");
        return Task.CompletedTask;
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
