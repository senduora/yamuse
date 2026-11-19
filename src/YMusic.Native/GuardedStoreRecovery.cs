using System.Buffers;
using System.ComponentModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace YMusic.Native;

internal enum GuardedStoreKind { Regular, Artwork, Loudness }

// One authenticated recovery slot per destination. Its bytes are committed
// before parking the previous file. Recovery never replaces an occupied name.
internal sealed class GuardedStoreRecovery
{
    internal sealed class RecoveryException(string previous, Exception? cause)
        : IOException($"Не удалось завершить замену файла. Предыдущие данные сохранены, если есть файл {Path.GetFileName(previous)}.", cause);
    internal sealed class ReplacementCapabilityException()
        : IOException("Том не предоставляет устойчивые идентификаторы файлов для безопасной замены.");

    private const int MaximumReceiptBytes = 4096;
    private const int MaximumProvenFileBytes = 32 * 1024 * 1024;
    private const string ArtworkSlotPrefix = ".yamuse-artwork-";
    private readonly string _path;
    private readonly string _receipt;
    private readonly string _protection;
    private readonly byte[] _entropy;
    private readonly bool _cache;
    internal string Previous { get; }

    internal GuardedStoreRecovery(string path, GuardedStoreKind kind = GuardedStoreKind.Regular)
    {
        _path = path;
        _cache = kind != GuardedStoreKind.Regular;
        var artwork = kind == GuardedStoreKind.Artwork;
        _entropy = SHA256.HashData(Encoding.UTF8.GetBytes("Yamuse.StoreRecovery.1\0" + path.ToUpperInvariant()));
        var name = Path.GetFileName(path);
        if (artwork && ArtworkImageName(name) != name) throw new IOException("Некорректное имя файла обложки.");
        var slot = Path.Combine(Path.GetDirectoryName(path)!, artwork ? ArtworkSlotPrefix + name[..64] : ".yamuse-store-" + Convert.ToHexString(_entropy));
        _receipt = slot + ".receipt";
        _protection = slot + ".protected";
        Previous = slot + ".previous";
    }

    // Only the artwork store can enumerate its exact destination names before
    // opening and authenticating a pending slot under the destination's gate.
    internal static string? ArtworkImageName(string entryName)
    {
        if (entryName.Length == 68 && entryName.EndsWith(".img", StringComparison.Ordinal)
            && IsArtworkHash(entryName.AsSpan(0, 64))) return entryName;
        if (!entryName.StartsWith(ArtworkSlotPrefix, StringComparison.Ordinal)) return null;
        var slot = entryName.AsSpan(ArtworkSlotPrefix.Length);
        if (slot.Length < 64 || !IsArtworkHash(slot[..64])) return null;
        return slot[64..] is ".receipt" or ".previous" or ".protected" ? slot[..64].ToString() + ".img" : null;
    }

    private static bool IsArtworkHash(ReadOnlySpan<char> hash) => hash.IndexOfAnyExcept("0123456789abcdef") < 0;

    private bool HasProtection => _cache && UninstallPathGuard.EntryExists(_protection);
    internal bool HasParkedPrevious => UninstallPathGuard.EntryExists(Previous);
    internal bool Pending => UninstallPathGuard.EntryExists(_receipt) || HasParkedPrevious || HasProtection;
    internal bool MayNeedRecoveryRead => HasProtection || UninstallPathGuard.EntryExists(_receipt) && !HasParkedPrevious;

    internal SafeFileHandle Record(SafeFileHandle original, SafeFileHandle staged)
        => Record(original, staged, out _);

    internal SafeFileHandle Record(SafeFileHandle original, SafeFileHandle staged, out Receipt receipt)
    {
        var previous = FileSecurityPreservation.GetPersistentRevision(original) ?? throw new ReplacementCapabilityException();
        var replacement = FileSecurityPreservation.GetPersistentRevision(staged) ?? throw new ReplacementCapabilityException();
        receipt = new(2, previous, replacement, CaptureProof(original, previous), CaptureProof(staged, replacement), Guid.NewGuid());
        return WriteRecord(_receipt, receipt, _entropy);
    }

    private SafeFileHandle WriteRecord<T>(string path, T record, byte[] entropy)
    {
        var bytes = ProtectedData.Protect(JsonSerializer.SerializeToUtf8Bytes(record), entropy, DataProtectionScope.CurrentUser);
        if (bytes.Length > MaximumReceiptBytes) throw CannotRecover();

        var handle = UninstallPathGuard.CreateStagedFile($"{path}.{Guid.NewGuid():N}.tmp", writeThrough: true, deleteOnClose: true);
        try
        {
            FileSecurityPreservation.CheckReplacementMetadata(handle);
            RandomAccess.Write(handle, bytes, 0);
            RandomAccess.FlushToDisk(handle);
            ManagedCacheCleanup.RenameByHandle(handle, path);
            UninstallPathGuard.KeepFileOnClose(handle);
            return handle;
        }
        catch { handle.Dispose(); throw; }
    }

    internal Outcome Recover() => Recover(forCleanup: false, out _);

    internal SafeFileHandle? OpenForCacheCleanup(out Outcome outcome)
    {
        // Windows can deny a file-only open of a directory before handle validation.
        // Classify these entries as preserved. Deletion still requires the recovered file handle.
        try
        {
            if ((File.GetAttributes(_path) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            {
                outcome = new(false, true);
                return null;
            }
        }
        catch (FileNotFoundException) { /* A verified parked entry can still be restored. */ }

        outcome = Recover(forCleanup: true, out var lease);
        return lease;
    }

    private Outcome Recover(bool forCleanup, out SafeFileHandle? lease)
    {
        lease = null;
        SafeFileHandle? current = null;
        try
        {
            using var marker = Open(_receipt, deleteAccess: true);
            if (marker is null)
            {
                if (HasParkedPrevious) throw CannotRecover();
                if (!HasProtection && !forCleanup) return default;
                current = Open(_path, deleteAccess: forCleanup);
                var protectedCurrent = CheckProtectedCurrent(current);
                if (forCleanup && !protectedCurrent) { lease = current; current = null; }
                return new(false, protectedCurrent);
            }
            var receipt = ReadReceipt(marker);

            var outcome = default(Outcome);
            using (var previous = Open(Previous, deleteAccess: true,
                writeAttributes: (receipt.Previous.Attributes & FileAttributes.Archive) == 0))
            {
                if (previous is not null)
                {
                    if (FileSecurityPreservation.GetPersistentRevision(previous) is not { } actual || !SameIdentity(actual, receipt.Previous))
                        throw CannotRecover();
                    current = Open(_path, deleteAccess: forCleanup);
                    var preserveCurrent = CheckProtectedCurrent(current);
                    if (current is not null)
                    {
                        outcome = RetireOrArchive(previous, current, receipt, preserveCurrent);
                    }
                    else
                    {
                        var proof = CompareProof(previous, actual, receipt.Previous, receipt.PreviousProof, receipt.Version);
                        // A cache's active name can be cleaned later. Only exact proof permits restoration there.
                        if (proof == ProofStatus.Mismatch || _cache && proof == ProofStatus.Unknown)
                        {
                            ArchivePrevious(previous, receipt, restoreArchive: false);
                            outcome = new(true, false);
                        }
                        else if (_cache)
                        {
                            if (CheckProtectedCurrent(previous, retireStale: false)) throw CannotRecover();
                            current = RestoreVerifiedPrevious(previous, receipt, deleteAccess: forCleanup);
                            if (CheckProtectedCurrent(current)) throw CannotRecover();
                        }
                        else
                        {
                            if (proof == ProofStatus.Unknown) FileSecurityPreservation.CheckReplacementMetadata(previous);
                            ManagedCacheCleanup.RenameByHandle(previous, _path);
                            if (proof == ProofStatus.Match)
                                FileSecurityPreservation.RestoreArchiveAfterRename(previous, receipt.Previous.Attributes);
                        }
                    }
                }
                else
                {
                    if (forCleanup)
                    {
                        current = Open(_path, deleteAccess: true);
                        outcome = ResolveCacheLease(current, receipt, deleteAccess: true);
                        if (outcome.ArchivedPrevious) { current?.Dispose(); current = null; }
                    }
                    else if (_cache) outcome = RecoverReturnedCache(receipt);
                    if (!outcome.ArchivedPrevious && receipt is { Version: 2, PreviousProof: not null })
                    {
                        // A second interruption may have left Archive set on a returned or archived file.
                        if (!_cache) RepairArchive(_path, receipt);
                        RepairArchive(ConflictPath(receipt.Conflict), receipt);
                    }
                }
            }
            UninstallPathGuard.DeleteByHandle(marker);
            if (forCleanup && !outcome.PreserveCurrent) { lease = current; current = null; }
            return outcome;
        }
        finally { current?.Dispose(); }
    }

    // The full-uninstall caller holds the directory and this destination's
    // write gate. A parked file needs both its original proof and the store's
    // own payload validation before it can be removed without being restored.
    internal int RemoveOwnedForUninstall(Func<SafeFileHandle, bool> isOwnedFile)
    {
        try
        {
            using var marker = Open(_receipt, deleteAccess: true);
            if (marker is null) return 0;
            Receipt receipt;
            try { receipt = ReadReceipt(marker); }
            catch (Exception error) when (error is RecoveryException or CryptographicException or JsonException) { return 0; }

            var removed = 0;
            using (var previous = Open(Previous, deleteAccess: true))
            {
                if (previous is not null)
                {
                    if (FileSecurityPreservation.GetPersistentRevision(previous) is not { } actual
                        || CompareProof(previous, actual, receipt.Previous, receipt.PreviousProof, receipt.Version) != ProofStatus.Match
                        || !isOwnedFile(previous)) return 0;
                    FileSecurityPreservation.CheckReplacementMetadata(previous);
                    UninstallPathGuard.DeleteByHandle(previous);
                    removed++;
                }
            }

            FileSecurityPreservation.CheckReplacementMetadata(marker);
            UninstallPathGuard.DeleteByHandle(marker);
            return removed + 1;
        }
        catch (Exception error) when (error is UninstallPathGuard.UnsafeStoragePathException or FileSecurityPreservation.UnsupportedMetadataException)
        { return 0; }
    }

    // The read path keeps both names and the authenticated receipt in place.
    internal void VerifyPublishedRead(SafeFileHandle? current)
    {
        if (current is null) throw CannotRecover();
        try
        {
            using var marker = Open(_receipt, deleteAccess: false) ?? throw CannotRecover();
            var receipt = ReadReceipt(marker);
            if (CheckProtectedCurrent(current, retireStale: false)) throw CannotRecover();
            VerifyProof(current, receipt.Replacement, receipt.ReplacementProof, receipt.Version);
        }
        catch (Exception error) when (error is Win32Exception or CryptographicException or JsonException)
        { throw CannotRecover(error); }
    }

    // The caller holds the gate and observed a missing primary name. Restore
    // only the exact recorded object, without replacing an arriving occupant.
    internal SafeFileHandle RestoreParkedForRead(bool deleteAccess)
    {
        try
        {
            using var marker = Open(_receipt, deleteAccess: true) ?? throw CannotRecover();
            var receipt = ReadReceipt(marker);
            using var protection = Open(_protection, deleteAccess: true);
            var protectedIdentity = protection is null ? (FileSecurityPreservation.FileRevision?)null : ReadProtection(protection);
            using var previous = Open(Previous, deleteAccess: true,
                writeAttributes: (receipt.Previous.Attributes & FileAttributes.Archive) == 0) ?? throw CannotRecover();
            var verified = VerifyProof(previous, receipt.Previous, receipt.PreviousProof, receipt.Version);
            if (protectedIdentity is { } identity && SameFileId(verified, identity)) throw CannotRecover();
            var restored = RestoreVerifiedPrevious(previous, receipt, deleteAccess);
            try
            {
                if (protection is not null) UninstallPathGuard.DeleteByHandle(protection);
                UninstallPathGuard.DeleteByHandle(marker);
                return restored;
            }
            catch { restored.Dispose(); throw; }
        }
        catch (Exception error) when (error is Win32Exception or CryptographicException or JsonException)
        { throw CannotRecover(error); }
    }

    private SafeFileHandle RestoreVerifiedPrevious(SafeFileHandle previous, Receipt receipt, bool deleteAccess)
    {
        ManagedCacheCleanup.RenameByHandle(previous, _path);
        FileSecurityPreservation.RestoreArchiveAfterRename(previous, receipt.Previous.Attributes);
        previous.Dispose();
        var restored = Open(_path, deleteAccess) ?? throw CannotRecover();
        try
        {
            VerifyProof(restored, receipt.Previous, receipt.PreviousProof, receipt.Version);
            return restored;
        }
        catch { restored.Dispose(); throw; }
    }

    // The caller owns the primary lease. Accept it only while that same handle
    // still pins the verified object at the time the receipt is retired.
    internal bool RecoverCacheForRead(SafeFileHandle? current)
    {
        try
        {
            using var marker = Open(_receipt, deleteAccess: true);
            if (HasParkedPrevious || marker is null && !HasProtection) throw CannotRecover();
            var receipt = marker is null ? null : ReadReceipt(marker);
            if (marker is null || receipt is null) return !CheckProtectedCurrent(current) && current is not null;
            var outcome = ResolveCacheLease(current, receipt, deleteAccess: false);
            if (!outcome.ArchivedPrevious && receipt is { Version: 2, PreviousProof: not null })
                RepairArchive(ConflictPath(receipt.Conflict), receipt);
            UninstallPathGuard.DeleteByHandle(marker);
            return current is not null && !outcome.ArchivedPrevious && !outcome.PreserveCurrent;
        }
        catch (Exception error) when (error is Win32Exception or CryptographicException or JsonException)
        { throw CannotRecover(error); }
    }

    private Outcome ResolveCacheLease(SafeFileHandle? current, Receipt receipt, bool deleteAccess)
    {
        var protectedCurrent = CheckProtectedCurrent(current);
        if (current is null || protectedCurrent) return new(false, protectedCurrent);
        var actual = FileSecurityPreservation.GetPersistentRevision(current) ?? throw CannotRecover();
        if (!SameIdentity(actual, receipt.Previous))
        {
            if (CompareProof(current, actual, receipt.Replacement, receipt.ReplacementProof, receipt.Version) == ProofStatus.Match) return default;
            ProtectCurrent(actual);
            return new(false, true);
        }
        if (CompareProof(current, actual, receipt.Previous, receipt.PreviousProof, receipt.Version) != ProofStatus.Match)
        {
            if (deleteAccess) ArchivePrevious(current, receipt, restoreArchive: false);
            else
            {
                current.Dispose();
                if (!ArchiveChangedReturnedCache(receipt)) throw CannotRecover();
            }
            return new(true, false);
        }
        if ((actual.Attributes & FileAttributes.Archive) != (receipt.Previous.Attributes & FileAttributes.Archive))
        {
            // This lease already pins the returned object at its name.
            FileSecurityPreservation.RestoreArchiveAfterRename(current, receipt.Previous.Attributes, closeRenameHandle: false);
            VerifyProof(current, receipt.Previous, receipt.PreviousProof, receipt.Version);
        }
        return default;
    }

    private FileSecurityPreservation.FileRevision VerifyProof(SafeFileHandle handle, FileSecurityPreservation.FileRevision expected, Proof? proof, int version)
    {
        if (FileSecurityPreservation.GetPersistentRevision(handle) is not { } actual
            || CompareProof(handle, actual, expected, proof, version) != ProofStatus.Match)
            throw CannotRecover();
        return actual;
    }

    private Receipt ReadReceipt(SafeFileHandle marker)
    {
        FileSecurityPreservation.CheckReplacementMetadata(marker);
        var bytes = GuardedStoreFile.ReadFromHandle(marker, MaximumReceiptBytes) ?? throw CannotRecover();
        var receipt = JsonSerializer.Deserialize<Receipt>(ProtectedData.Unprotect(bytes, _entropy, DataProtectionScope.CurrentUser));
        if (receipt is not { Version: 1 or 2 } || !ValidIdentity(receipt.Previous) || !ValidIdentity(receipt.Replacement)
            || SameIdentity(receipt.Previous, receipt.Replacement) || receipt is { Version: 2, Conflict: var conflict } && conflict == Guid.Empty)
            throw CannotRecover();
        return receipt;
    }

    internal Outcome RetireOrArchive(SafeFileHandle previous, SafeFileHandle current, Receipt receipt, bool preserveCurrent = false)
    {
        if (FileSecurityPreservation.GetPersistentRevision(previous) is not { } actual || !SameIdentity(actual, receipt.Previous))
            throw CannotRecover();
        var currentRevision = FileSecurityPreservation.GetPersistentRevision(current);
        var committed = currentRevision is { } published
            && CompareProof(current, published, receipt.Replacement, receipt.ReplacementProof, receipt.Version) == ProofStatus.Match;
        if (_cache && !committed && !preserveCurrent)
        {
            ProtectCurrent(currentRevision ?? throw CannotRecover());
            preserveCurrent = true;
        }
        var unchanged = CompareProof(previous, actual, receipt.Previous, receipt.PreviousProof, receipt.Version) == ProofStatus.Match;
        if (committed && unchanged)
        {
            FileSecurityPreservation.CheckReplacementMetadata(previous);
            UninstallPathGuard.DeleteByHandle(previous);
            return new(false, preserveCurrent);
        }

        // A conflict, older receipt or missing proof cannot authorize deletion.
        ArchivePrevious(previous, receipt, restoreArchive: unchanged);
        return new(true, preserveCurrent);
    }

    private void ArchivePrevious(SafeFileHandle previous, Receipt receipt, bool restoreArchive)
    {
        ManagedCacheCleanup.RenameByHandle(previous, ConflictPath(receipt.Conflict == Guid.Empty ? Guid.NewGuid() : receipt.Conflict));
        if (restoreArchive) FileSecurityPreservation.RestoreArchiveAfterRename(previous, receipt.Previous.Attributes);
    }

    private Outcome RecoverReturnedCache(Receipt receipt)
    {
        using var returned = Open(_path, deleteAccess: false);
        var preserveCurrent = CheckProtectedCurrent(returned);
        if (returned is null) return default;
        var actual = FileSecurityPreservation.GetPersistentRevision(returned) ?? throw CannotRecover();
        if (preserveCurrent) return new(false, true);
        if (!SameIdentity(actual, receipt.Previous))
        {
            if (CompareProof(returned, actual, receipt.Replacement, receipt.ReplacementProof, receipt.Version) == ProofStatus.Match) return default;
            ProtectCurrent(actual);
            return new(false, true);
        }
        if (VerifyReturnedCache(returned, actual, receipt)) return default;

        // The ordinary read lease denies DELETE. Check the reopened object again
        // before moving it, since another process might have changed the name.
        returned.Dispose();
        return new(ArchiveChangedReturnedCache(receipt), false);
    }

    private bool ArchiveChangedReturnedCache(Receipt receipt)
    {
        using var movable = Open(_path, deleteAccess: true) ?? throw CannotRecover();
        var reopened = FileSecurityPreservation.GetPersistentRevision(movable) ?? throw CannotRecover();
        if (!SameIdentity(reopened, receipt.Previous)) throw CannotRecover();
        if (VerifyReturnedCache(movable, reopened, receipt)) return false;
        ArchivePrevious(movable, receipt, restoreArchive: false);
        return true;
    }

    private byte[] ProtectionEntropy() => SHA256.HashData(Encoding.UTF8.GetBytes("Yamuse.CacheProtection.1\0" + _path.ToUpperInvariant()));

    private void ProtectCurrent(FileSecurityPreservation.FileRevision actual)
    {
        // Leave the occupant in place. Publish its identity before retiring the
        // interrupted replacement so later cleanup still knows to preserve it.
        using var protection = WriteRecord(_protection, actual, ProtectionEntropy());
    }

    private bool CheckProtectedCurrent(SafeFileHandle? current, bool retireStale = true)
    {
        if (!_cache) return false;
        using var protection = Open(_protection, deleteAccess: retireStale);
        if (protection is null) return false;
        var identity = ReadProtection(protection);
        if (current is null) return false;
        if (SameFileId(FileSecurityPreservation.GetPersistentRevision(current) ?? throw CannotRecover(), identity))
            return true;
        if (retireStale) UninstallPathGuard.DeleteByHandle(protection);
        return false;
    }

    private FileSecurityPreservation.FileRevision ReadProtection(SafeFileHandle protection)
    {
        FileSecurityPreservation.CheckReplacementMetadata(protection);
        var bytes = GuardedStoreFile.ReadFromHandle(protection, MaximumReceiptBytes) ?? throw CannotRecover();
        var identity = JsonSerializer.Deserialize<FileSecurityPreservation.FileRevision>(
            ProtectedData.Unprotect(bytes, ProtectionEntropy(), DataProtectionScope.CurrentUser));
        if (!ValidIdentity(identity)) throw CannotRecover();
        return identity;
    }

    private static bool VerifyReturnedCache(SafeFileHandle handle, FileSecurityPreservation.FileRevision actual, Receipt receipt)
    {
        if (CompareProof(handle, actual, receipt.Previous, receipt.PreviousProof, receipt.Version) != ProofStatus.Match) return false;
        if ((actual.Attributes & FileAttributes.Archive) != (receipt.Previous.Attributes & FileAttributes.Archive))
            FileSecurityPreservation.RestoreArchiveAfterRename(handle, receipt.Previous.Attributes);
        return true;
    }

    private void RepairArchive(string path, Receipt receipt)
    {
        if ((receipt.Previous.Attributes & FileAttributes.Archive) != 0) return;
        using var returned = Open(path, deleteAccess: false);
        if (returned is null) return;
        var actual = FileSecurityPreservation.GetPersistentRevision(returned) ?? throw CannotRecover();
        if ((actual.Attributes & FileAttributes.Archive) == 0
            || CompareProof(returned, actual, receipt.Previous, receipt.PreviousProof, receipt.Version) != ProofStatus.Match) return;
        FileSecurityPreservation.RestoreArchiveAfterRename(returned, receipt.Previous.Attributes);
    }

    private string ConflictPath(Guid id) => $"{Previous}.conflict-{id:N}.bak";

    internal int CountConflictArchives()
    {
        var prefix = Path.GetFileName(Previous) + ".conflict-";
        return Directory.EnumerateFiles(Path.GetDirectoryName(Previous)!, prefix + "*.bak").Count(path =>
        {
            var name = Path.GetFileName(path);
            return name.Length == prefix.Length + 36 && name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                && name.EndsWith(".bak", StringComparison.OrdinalIgnoreCase)
                && Guid.TryParseExact(name.AsSpan(prefix.Length, 32), "N", out _);
        });
    }

    private enum ProofStatus { Unknown, Match, Mismatch }

    private static ProofStatus CompareProof(SafeFileHandle handle, FileSecurityPreservation.FileRevision actual,
        FileSecurityPreservation.FileRevision expected, Proof? proof, int version)
    {
        if (!MatchesParkedContent(actual, expected)) return ProofStatus.Mismatch;
        if (version != 2 || proof is null) return ProofStatus.Unknown;
        try { return CaptureProof(handle, actual) == proof ? ProofStatus.Match : ProofStatus.Mismatch; }
        catch (FileSecurityPreservation.UnsupportedMetadataException) { return ProofStatus.Mismatch; }
    }

    private static Proof? CaptureProof(SafeFileHandle handle, FileSecurityPreservation.FileRevision revision)
    {
        if (revision.Length > MaximumProvenFileBytes) return null;
        FileSecurityPreservation.CheckReplacementMetadata(handle);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            long offset = 0;
            while (offset < revision.Length)
            {
                var read = RandomAccess.Read(handle, buffer.AsSpan(0, (int)Math.Min(revision.Length - offset, 64 * 1024)), offset);
                if (read == 0) throw new EndOfStreamException();
                hash.AppendData(buffer, 0, read);
                offset += read;
            }
        }
        finally { ArrayPool<byte>.Shared.Return(buffer, clearArray: true); }
        var security = FileSecurityPreservation.CaptureReplacementSecurity(handle);
        if (FileSecurityPreservation.GetRevision(handle) != revision)
            throw new IOException("Файл изменился во время проверки восстановления.");
        return new(Convert.ToHexString(hash.GetHashAndReset()), security);
    }

    // A rename can change ChangeTime and set Archive, which displaces Normal.
    // Do not ignore a cleared Archive bit or any change to another attribute.
    private static bool MatchesParkedContent(FileSecurityPreservation.FileRevision actual, FileSecurityPreservation.FileRevision expected)
    {
        var attributes = actual.Attributes;
        if ((expected.Attributes & FileAttributes.Archive) == 0 && (attributes & FileAttributes.Archive) != 0)
        {
            attributes &= ~FileAttributes.Archive;
            if (attributes == 0) attributes = FileAttributes.Normal;
        }
        return actual with { Changed = expected.Changed, Attributes = attributes } == expected;
    }

    private static bool SameIdentity(FileSecurityPreservation.FileRevision left, FileSecurityPreservation.FileRevision right)
        => SameFileId(left, right) && left.Created == right.Created;

    private static bool SameFileId(FileSecurityPreservation.FileRevision left, FileSecurityPreservation.FileRevision right)
        => (left.Volume, left.Low, left.High) == (right.Volume, right.Low, right.High);

    private static bool ValidIdentity(FileSecurityPreservation.FileRevision revision)
        => (revision.Low | revision.High) != 0 && revision.Created >= 0 && revision.Length >= 0;

    private static SafeFileHandle? Open(string path, bool deleteAccess, bool writeAttributes = false)
    {
        try { return UninstallPathGuard.OpenOwnedFile(path, read: true, deleteAccess, writeAttributes); }
        catch (Win32Exception error) when (error.NativeErrorCode == 2) { return null; }
    }

    internal RecoveryException CannotRecover(Exception? cause = null) => new(Previous, cause);

    internal readonly record struct Outcome(bool ArchivedPrevious, bool PreserveCurrent);
    internal sealed record Proof(string Content, string Security);
    internal sealed record Receipt(int Version, FileSecurityPreservation.FileRevision Previous, FileSecurityPreservation.FileRevision Replacement,
        Proof? PreviousProof = null, Proof? ReplacementProof = null, Guid Conflict = default);
}
