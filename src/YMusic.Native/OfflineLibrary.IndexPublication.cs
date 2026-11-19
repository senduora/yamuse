using System.ComponentModel;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace YMusic.Native;

internal sealed partial class OfflineLibrary
{
    private const string IndexPublicationRecoveryFileName = ".ym-index.recovery";
    private string IndexPublicationRecoveryPath => Path.Combine(DirectoryPath, IndexPublicationRecoveryFileName);

    private void ReplaceIndexUnderPublicationLock(string path, string temporary, SafeFileHandle stage,
        IndexStamp expected, IndexStamp written, FileSecurityPreservation.FileRevision originalRevision, bool preservePreviousRevision,
        ref bool published)
    {
        var restoreArchive = (originalRevision.Attributes & FileAttributes.Archive) == 0;
        using var original = OpenIndex(path, deleteAccess: true, writeAttributes: preservePreviousRevision || restoreArchive) ?? throw IndexChanged();
        CheckIndexStamp(ReadIndexSnapshot(original).Stamp, expected);
        if (FileSecurityPreservation.GetRevision(original) != originalRevision) throw IndexChanged();
        var backup = path + ".previous-" + Guid.NewGuid().ToString("N");
        var identity = GetStorageFileIdentity(original);
        var archiveReference = restoreArchive && identity is null ? GetArchiveReference(original) : null;
        var pinPreviousTimes = preservePreviousRevision || restoreArchive && (expected.Revision is not null || archiveReference is not null);
        var pending = new IndexPublicationJournal(3, UninstallPathGuard.Canonical(DirectoryPath), Path.GetFileName(path),
            Path.GetFileName(temporary), Path.GetFileName(backup),
            new(expected.Length, expected.Hash!, identity),
            new(written.Length, written.Hash!, GetStorageFileIdentity(stage)), originalRevision.Attributes,
            pinPreviousTimes ? originalRevision.Written : null, pinPreviousTimes ? originalRevision.Changed : null, archiveReference);
        ValidateIndexPublicationJournal(pending);
        using var receipt = PublishJournal(IndexPublicationRecoveryPath, pending, MaximumSaveRecoveryBytes);
        var parked = false;
        try
        {
            TemporaryPublicationCheckpoint?.Invoke("index-after-publication-receipt");
            FileSecurityPreservation.CheckReplacementMetadata(original);
            if (FileSecurityPreservation.GetRevision(original) != originalRevision) throw IndexChanged();
            // An outer removal WAL may have authorized its indexed revision by
            // these times. Moving the exact file must not invalidate that proof.
            if (pinPreviousTimes) FileSecurityPreservation.PinWriteAndChangeTimes(original);
            ManagedCacheCleanup.RenameByHandle(original, backup);
            parked = true;
            TemporaryPublicationCheckpoint?.Invoke("index-after-park");
            PublishStagedIndex(stage, path, ref published);
        }
        catch
        {
            // A failed no-replace operation leaves an arriving index untouched.
            // The live handle can restore the old file if its name is still free.
            if (!published)
                TryRetireIndexPublication(() =>
                {
                    using var restored = parked ? RestoreParkedIndex(original, path, pending) : null;
                    RetireJournal(receipt, IndexPublicationRecoveryPath);
                });
            throw;
        }

        // The original is still the exact checked object. A failure here leaves
        // the durable receipt so replay can retire the old copy independently.
        TryRetireIndexPublication(() =>
        {
            TemporaryPublicationCheckpoint?.Invoke("index-before-retiring-backup");
            var removed = TryDiscardPublishedIndexCopy(original);
            RetireJournal(receipt, IndexPublicationRecoveryPath, preserve: !removed);
        });
    }

    private void PublishStagedIndex(SafeFileHandle stage, string path, ref bool published)
    {
        ManagedCacheCleanup.RenameByHandle(stage, path, releaseDeleteDisposition: true);
        published = true;
        TemporaryPublicationCheckpoint?.Invoke("index-after-rename");
    }

    private void RecoverIndexPublicationUnderGate()
    {
        if (!UninstallPathGuard.EntryExists(IndexPublicationRecoveryPath)) return;
        using var parents = HoldStorageParents(IndexPublicationRecoveryPath);
        if (!parents.Complete) return;
        var (pending, file) = ReadProtectedJournal<IndexPublicationJournal>(IndexPublicationRecoveryPath, MaximumSaveRecoveryBytes);
        using var receipt = file;
        var path = ValidateIndexPublicationJournal(pending);
        lock (_directoryState.IndexPublicationSync)
        {
            using var current = OpenIndex(path);
            var published = current is not null && MatchesPublicationCopy(current, pending.Written, requireIdentity: true);
            var preserve = false;
            using var backup = OpenCleanupCopy(pending.Backup, pending.PreviousChanged is not null || ArchiveWasClear(pending));
            // Verify the complete parked file before restoring it. Without a
            // stable ID the dedicated name and digest can only authorize moving it back.
            if (current is null && (backup is null || !MatchesParkedIndex(backup, pending)))
                throw new IOException("Не удалось подтвердить прежнюю копию индекса. Файлы и журнал сохранены.");
            using var restored = current is null ? RestoreParkedIndex(backup!, path, pending) : null;
            if (current is not null && backup is not null)
            {
                // A current index from another writer is authoritative. Keep
                // the prior copy intact if publication or backup ownership is uncertain.
                preserve |= !published
                    || !MatchesPublicationCopy(backup, pending.Previous, requireIdentity: true)
                    || !TryDiscardPublishedIndexCopy(backup);
            }
            else if (!published && !preserve && current is not null && ArchiveWasClear(pending)
                && (FileSecurityPreservation.GetRevision(current).Attributes & FileAttributes.Archive) != 0
                && PreviousTimesMatch(current, pending))
            {
                if (pending.PreviousChanged is not null && MatchesReturnedArchiveSource(current, pending))
                {
                    // The previous rename may have completed before the final
                    // attribute fix. Its identity still has to match the receipt.
                    using var metadata = OpenIndex(path, writeAttributes: true) ?? throw IndexChanged();
                    FileSecurityPreservation.PinWriteAndChangeTimes(metadata);
                    FileSecurityPreservation.RestoreArchiveAttribute(metadata, pending.PreviousAttributes!.Value);
                }
                else if (pending.Previous.Identity is null && pending.PreviousArchiveReference is null)
                {
                    // A pathname and an equal digest do not prove which object returned.
                    preserve = MatchesPublicationCopy(current, pending.Previous, requireIdentity: false);
                }
            }

            using var stage = OpenCleanupCopy(pending.Stage, writeAttributes: false);
            if (stage is not null)
                preserve |= !MatchesPublicationCopy(stage, pending.Written, requireIdentity: true)
                    || !TryDiscardPublishedIndexCopy(stage);
            RetireJournal(receipt, IndexPublicationRecoveryPath, preserve);

            SafeFileHandle? OpenCleanupCopy(string name, bool writeAttributes)
            {
                try { return OpenIndex(Path.Combine(DirectoryPath, name), deleteAccess: true, writeAttributes: writeAttributes); }
                catch (Exception error) when (current is not null && error is (IOException or UnauthorizedAccessException or Win32Exception))
                {
                    // An accessible authoritative index stays pinned. Preserve
                    // the busy copy and receipt without blocking reads.
                    preserve = true;
                    return null;
                }
            }
        }
    }

    private SafeFileHandle? RestoreParkedIndex(SafeFileHandle previous, string path, IndexPublicationJournal pending)
    {
        var preserveTimes = pending.PreviousChanged is not null;
        var restoreArchive = ArchiveWasClear(pending) && PreviousTimesMatch(previous, pending);
        if (preserveTimes) FileSecurityPreservation.PinWriteAndChangeTimes(previous);
        ManagedCacheCleanup.RenameByHandle(previous, path);
        if (!restoreArchive) return null; // The rename handle still pins the restored name.
        TemporaryPublicationCheckpoint?.Invoke("index-after-restore-before-archive");
        FileSecurityPreservation.RestoreArchiveAfterRename(previous, pending.PreviousAttributes!.Value, preserveTimes);
        // Archive can be set while the rename handle closes. Reopen and verify
        // the authoritative path after the attribute fix, then hold it through retirement.
        var restored = OpenIndex(path) ?? throw IndexChanged();
        try
        {
            if (!MatchesParkedIndex(restored, pending)) throw IndexChanged();
            return restored;
        }
        catch { restored.Dispose(); throw; }
    }

    private static bool ArchiveWasClear(IndexPublicationJournal pending)
        => pending.PreviousAttributes is { } attributes && (attributes & FileAttributes.Archive) == 0;

    private static bool PreviousTimesMatch(SafeFileHandle handle, IndexPublicationJournal pending)
    {
        if (pending.PreviousChanged is null) return true;
        var current = FileSecurityPreservation.GetRevision(handle);
        return current.Changed == pending.PreviousChanged && current.Written == pending.PreviousWritten;
    }

    private bool MatchesReturnedArchiveSource(SafeFileHandle current, IndexPublicationJournal pending)
        => (pending.PreviousArchiveReference is not null || pending.Previous.Identity is not null) && MatchesParkedIndex(current, pending);

    private bool MatchesParkedIndex(SafeFileHandle current, IndexPublicationJournal pending)
        => (pending.PreviousArchiveReference is not { } reference || GetArchiveReference(current) == reference)
            && MatchesPublicationCopy(current, pending.Previous, requireIdentity: false);

    private static IndexArchiveReference? GetArchiveReference(SafeFileHandle handle)
        => FileSecurityPreservation.TryGetNtfsFileReference(handle) is { } reference ? new(reference.Volume, reference.FileReference) : null;

    private bool MatchesPublicationCopy(SafeFileHandle handle, IndexPublicationCopy copy, bool requireIdentity)
    {
        if (copy.Identity is { } identity)
        {
            if (GetStorageFileIdentity(handle) != identity) return false;
        }
        else if (requireIdentity) return false;
        return RandomAccess.GetLength(handle) == copy.Length && ReadIndexSnapshot(handle).Stamp.Hash == copy.Hash;
    }

    private static bool TryDiscardPublishedIndexCopy(SafeFileHandle handle)
    {
        try
        {
            FileSecurityPreservation.CheckReplacementMetadata(handle);
            UninstallPathGuard.DeleteByHandle(handle);
            return true;
        }
        catch (Exception error) when (error is IOException or Win32Exception or UnauthorizedAccessException)
        { return false; }
    }

    private static void TryRetireIndexPublication(Action operation)
    {
        try { operation(); }
        catch (Exception error) when (error is IOException or Win32Exception or UnauthorizedAccessException)
        { /* Leave the durable receipt for recovery. */ }
    }

    private string ValidateIndexPublicationJournal(IndexPublicationJournal data)
    {
        var path = data.Target switch
        {
            "library.index.json" => _indexPath,
            "playlists.index.json" => _playlistIndexPath,
            _ => throw new JsonException()
        };
        if (data.Version is not (1 or 2 or 3) || !string.Equals(data.Directory, UninstallPathGuard.Canonical(DirectoryPath), StringComparison.OrdinalIgnoreCase)
            || data.Stage is null || !GeneratedName(data.Stage, data.Target + ".tmp-", "")
            || data.Backup is null || !GeneratedName(data.Backup, data.Target + ".previous-", "")
            || !ValidPublicationCopy(data.Previous) || !ValidPublicationCopy(data.Written)
            || (data.Version >= 2) != (data.PreviousAttributes is not null)
            || (data.PreviousChanged is null) != (data.PreviousWritten is null)
            || data.PreviousChanged is <= 0 || data.PreviousWritten is <= 0
            || data.Version == 1 && data.PreviousChanged is not null
            || data.PreviousArchiveReference is { } reference && (data.Version < 3 || reference.FileReference == 0
                || !ArchiveWasClear(data) || data.Previous.Identity is not null || data.PreviousChanged is null)) throw new JsonException();
        if (data.PreviousAttributes is { } attributes) FileSecurityPreservation.ValidateReplacementAttributes(attributes);
        return path;
    }

    private static bool ValidPublicationCopy(IndexPublicationCopy? copy)
        => copy is { Length: >= 0 and <= MaximumIndexBytes } && IsHash(copy.Hash)
            && (copy.Identity is not { } identity || (identity.Low | identity.High) != 0);

    private sealed record IndexPublicationJournal(int Version, string Directory, string Target, string Stage, string Backup,
        IndexPublicationCopy Previous, IndexPublicationCopy Written, FileAttributes? PreviousAttributes = null,
        long? PreviousWritten = null, long? PreviousChanged = null, IndexArchiveReference? PreviousArchiveReference = null);
    private sealed record IndexPublicationCopy(long Length, string Hash, StorageFileIdentity? Identity);
    private sealed record IndexArchiveReference(uint Volume, ulong FileReference);
}
