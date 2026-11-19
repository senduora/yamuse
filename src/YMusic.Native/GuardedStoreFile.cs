using System.ComponentModel;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace YMusic.Native;

// Fixed, small local stores use one checked handle for both the read bound
// and the contents. A write owns its staged entry through publish or cleanup.
internal static class GuardedStoreFile
{
    internal static byte[]? Read(string path, int maximumBytes, GuardedStoreKind kind = GuardedStoreKind.Regular)
    {
        try
        {
            using var parents = UninstallPathGuard.HoldAncestors(path);
            if (!parents.Complete) return null;
            using var handle = OpenForRead(path, kind: kind);
            if (handle is null) return null;
            return ReadFromHandle(handle, maximumBytes);
        }
        catch (Win32Exception error) { throw new IOException("Не удалось прочитать локальные данные.", error); }
    }

    // The caller keeps checked ancestors open throughout the read. The ordinary
    // fast path still permits concurrent readers without taking the writer gate.
    internal static SafeFileHandle? OpenForRead(string path, bool read = true, bool deleteAccess = false,
        GuardedStoreKind kind = GuardedStoreKind.Regular)
    {
        path = UninstallPathGuard.Canonical(path);
        var recovery = new GuardedStoreRecovery(path, kind);
        var handle = OpenExisting(path, read, deleteAccess);
        if (kind != GuardedStoreKind.Regular) return OpenCacheForRead(path, read, deleteAccess, recovery, handle);
        if (handle is not null)
        {
            if (!deleteAccess) RepairIfIdle(path, recovery);
            return handle;
        }
        if (!recovery.Pending) return OpenExisting(path, read, deleteAccess);
        using var writer = HoldWriteGate(path);
        Recover(recovery);
        return OpenExisting(path, read, deleteAccess);
    }

    private static SafeFileHandle? OpenCacheForRead(string path, bool read, bool deleteAccess,
        GuardedStoreRecovery recovery, SafeFileHandle? handle)
    {
        try
        {
            if (!recovery.Pending)
            {
                if (handle is not null) return handle;
                handle = OpenExisting(path, read, deleteAccess);
                if (handle is null || !recovery.Pending) return handle;
            }

            using var gate = TryHoldCanonicalWriteGate(path, TimeSpan.Zero)
                ?? throw new IOException("Файл занят другим экземпляром Yamuse.");
            handle ??= OpenExisting(path, read, deleteAccess);
            if (recovery.HasParkedPrevious)
            {
                if (handle is null && read) return recovery.RestoreParkedForRead(deleteAccess);
                recovery.VerifyPublishedRead(handle);
                return handle;
            }
            if (!recovery.MayNeedRecoveryRead) return handle;

            if (recovery.RecoverCacheForRead(handle)) return handle;
            handle?.Dispose();
            return null;
        }
        catch { handle?.Dispose(); throw; }
    }

    private static void RepairIfIdle(string path, GuardedStoreRecovery recovery)
    {
        try
        {
            if (!recovery.MayNeedRecoveryRead) return;
            using var writer = TryHoldCanonicalWriteGate(path, TimeSpan.Zero);
            if (writer is not null && recovery.MayNeedRecoveryRead) Recover(recovery);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or Win32Exception)
        { /* A busy or unverifiable receipt remains for repair under a later writer. */ }
    }

    // Clear callers must recover even when the primary name exists. Both the
    // write gate and ancestor lease belong to the caller for this operation.
    internal static GuardedStoreRecovery.Outcome RecoverPendingUnderWriteGate(string path, GuardedStoreKind kind = GuardedStoreKind.Regular)
    {
        var recovery = new GuardedStoreRecovery(UninstallPathGuard.Canonical(path), kind);
        return recovery.Pending ? Recover(recovery) : default;
    }

    internal static SafeFileHandle? OpenCacheForCleanupUnderWriteGate(string path, GuardedStoreKind kind, out GuardedStoreRecovery.Outcome outcome)
    {
        var recovery = new GuardedStoreRecovery(UninstallPathGuard.Canonical(path), kind);
        try { return recovery.OpenForCacheCleanup(out outcome); }
        catch (Exception error) when (error is Win32Exception or CryptographicException or JsonException)
        { throw recovery.CannotRecover(error); }
    }

    internal static byte[]? ReadFromHandle(SafeFileHandle handle, int maximumBytes)
    {
        var length = RandomAccess.GetLength(handle);
        if (length <= 0 || length > maximumBytes) return null;
        var bytes = new byte[(int)length];
        var offset = 0;
        while (offset < bytes.Length)
        {
            var read = RandomAccess.Read(handle, bytes.AsSpan(offset), offset);
            if (read == 0) throw new EndOfStreamException();
            offset += read;
        }
        return bytes;
    }

    internal static void Write(string path, ReadOnlySpan<byte> bytes)
    {
        using var storage = BeginWrite(path);
        storage.Write(bytes);
    }

    internal static WriteTransaction BeginWrite(string path, bool createParents = true, bool deleteAccess = false,
        bool allowNetworkShare = false, GuardedStoreKind kind = GuardedStoreKind.Regular)
    {
        path = UninstallPathGuard.Canonical(path, allowNetworkShare: allowNetworkShare);
        var writer = HoldCanonicalWriteGate(path);
        try
        {
            var parents = UninstallPathGuard.HoldAncestors(path, createParents, allowNetworkShare);
            try
            {
                var recovery = new GuardedStoreRecovery(path, kind);
                if (parents.Complete && recovery.Pending && Recover(recovery).PreserveCurrent)
                    throw recovery.CannotRecover();
                var original = parents.Complete ? OpenExisting(path, read: true, deleteAccess) : null;
                return new(path, writer, parents, original, deleteAccess, recovery, allowNetworkShare);
            }
            catch { parents.Dispose(); throw; }
        }
        catch (Exception error)
        {
            writer.Dispose();
            if (error is Win32Exception) throw SaveError(error);
            throw;
        }
    }

    private static GuardedStoreRecovery.Outcome Recover(GuardedStoreRecovery recovery)
    {
        try { return recovery.Recover(); }
        catch (Exception error) when (error is Win32Exception or CryptographicException or JsonException)
        { throw recovery.CannotRecover(error); }
    }

    internal static WriteGate HoldWriteGate(string path) => HoldCanonicalWriteGate(UninstallPathGuard.Canonical(path));

    private static WriteGate HoldCanonicalWriteGate(string path)
        => TryHoldCanonicalWriteGate(path, TimeSpan.FromSeconds(1)) ?? throw new IOException("Файл занят другим экземпляром Yamuse.");

    private static WriteGate? TryHoldCanonicalWriteGate(string path, TimeSpan timeout)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var sid = identity.User!;
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sid.Value + "\0" +
            path.ToUpperInvariant())));
        try
        {
            var security = new MutexSecurity();
            security.SetOwner(sid);
            security.AddAccessRule(new MutexAccessRule(sid, MutexRights.FullControl, AccessControlType.Allow));
            var mutex = MutexAcl.Create(false, @"Global\Yamuse.Store." + key, out _, security);
            try
            {
                if (!sid.Equals(mutex.GetAccessControl().GetOwner(typeof(SecurityIdentifier))))
                    throw new IOException("Не удалось подтвердить владельца блокировки сохранения.");
                if (!mutex.WaitOne(timeout)) { mutex.Dispose(); return null; }
            }
            catch (AbandonedMutexException) { /* The previous process exited; the on-disk state is rechecked. */ }
            catch { mutex.Dispose(); throw; }
            return new(mutex);
        }
        catch (Exception error) when (error is WaitHandleCannotBeOpenedException or Win32Exception)
        { throw SaveError(error); }
    }

    // Transactions are synchronous because Windows mutex ownership belongs to the acquiring thread.
    internal sealed class WriteGate(Mutex mutex) : IDisposable
    {
        public void Dispose() { mutex.ReleaseMutex(); mutex.Dispose(); }
    }

    internal sealed class WriteTransaction(string path, WriteGate writer, UninstallPathGuard.AncestorLease parents,
        SafeFileHandle? original, bool deleteAccess, GuardedStoreRecovery recovery, bool allowNetworkShare) : IDisposable
    {
        private FileSecurityPreservation.FileRevision? _originalRevision;
        internal bool Exists => original is not null;
        internal Action? AfterParkForChecks { get; set; }

        internal SafeFileHandle? OpenExisting()
        {
            try
            {
                if (original is null) return null;
                FileSecurityPreservation.CheckReplacementMetadata(original);
                return OpenCheckedOriginal();
            }
            catch (Win32Exception error) { throw SaveError(error); }
        }

        internal byte[]? ReadExisting(int maximumBytes) => original is null ? null : ReadFromHandle(original, maximumBytes);

        internal SafeFileHandle CreateStage(string temporary)
        {
            temporary = UninstallPathGuard.Canonical(temporary, allowNetworkShare: allowNetworkShare);
            if (!parents.Complete || temporary.Equals(path, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(Path.GetDirectoryName(temporary), Path.GetDirectoryName(path), StringComparison.OrdinalIgnoreCase))
                throw new IOException("Временный файл должен находиться рядом с сохраняемым файлом.");
            try
            {
                if (original is not null)
                {
                    FileSecurityPreservation.CheckReplacementMetadata(original);
                    _originalRevision ??= FileSecurityPreservation.GetRevision(original);
                    VerifyOriginalRevision(original);
                    FileSecurityPreservation.ValidateReplacementAttributes(_originalRevision.Value.Attributes);
                }
                var stage = UninstallPathGuard.CreateStagedFile(temporary, writeThrough: true, writeDacl: Exists, deleteOnClose: true);
                try
                {
                    if (original is not null) FileSecurityPreservation.PrepareReplacement(original, stage);
                    else FileSecurityPreservation.CheckReplacementMetadata(stage);
                    return stage;
                }
                catch
                {
                    UninstallPathGuard.DiscardStagedFile(stage); stage.Dispose(); throw;
                }
            }
            catch (Win32Exception error) { throw SaveError(error); }
        }

        internal SafeFileHandle Stage(ReadOnlySpan<byte> bytes)
        {
            var staged = CreateStage($"{path}.{Guid.NewGuid():N}.tmp");
            try
            {
                RandomAccess.Write(staged, bytes, 0);
                RandomAccess.FlushToDisk(staged);
                return staged;
            }
            catch { UninstallPathGuard.DiscardStagedFile(staged); staged.Dispose(); throw; }
        }

        internal bool Commit(SafeFileHandle staged, Func<Action, bool>? publishIfCurrent = null)
        {
            SafeFileHandle? receipt = null;
            SafeFileHandle? previous = null;
            GuardedStoreRecovery.Receipt? recorded = null;
            var parked = false;
            try
            {
                if (original is not null)
                {
                    using var current = OpenCheckedOriginal();
                    FileSecurityPreservation.CheckReplacementMetadata(current);
                    FileSecurityPreservation.PreserveBasicInformation(current, staged);
                    // Record a stable write time before close and recovery.
                    // Windows can set Archive while finishing this new revision.
                    File.SetLastWriteTimeUtc(staged, DateTime.UtcNow);
                    RandomAccess.FlushToDisk(staged);
                    VerifyOriginalRevision(current);
                    receipt = recovery.Record(current, staged, out recorded);
                }
                if (publishIfCurrent is not null)
                {
                    if (!publishIfCurrent(Publish)) return false;
                }
                else Publish();
                // Revalidation is bounded but can read from disk; do not hold
                // the caller's short admission lock during eventual retirement.
                if (parked && previous is not null && recorded is not null)
                {
                    try
                    {
                        recovery.RetireOrArchive(previous, staged, recorded);
                        parked = false;
                    }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException or Win32Exception)
                    { /* The committed replacement and authenticated recovery record stay intact. */ }
                }
                return true;
            }
            catch (Win32Exception error) { throw SaveError(error); }
            catch (CryptographicException error) { throw recovery.CannotRecover(error); }
            finally
            {
                previous?.Dispose();
                if (receipt is not null)
                {
                    if (!parked) UninstallPathGuard.DiscardStagedFile(receipt);
                    receipt.Dispose();
                }
            }

            void Publish()
            {
                if (original is null)
                {
                    ManagedCacheCleanup.RenameByHandle(staged, path);
                    UninstallPathGuard.KeepFileOnClose(staged);
                    return;
                }

                VerifyOriginalRevision(original);
                var expected = FileSecurityPreservation.GetRevision(original);
                // Keep concurrent readers available through the preparation.
                // Reopen for rename and verify the exact file after closing the read lease.
                original.Dispose();
                previous = GuardedStoreFile.OpenExisting(path, read: true, deleteAccess: true,
                    writeAttributes: (expected.Attributes & FileAttributes.Archive) == 0) ?? throw Changed();
                if (FileSecurityPreservation.GetRevision(previous) != expected) throw Changed();
                ManagedCacheCleanup.RenameByHandle(previous, recovery.Previous);
                parked = true;
                var replacementLinked = false;
                try
                {
                    AfterParkForChecks?.Invoke();
                    ManagedCacheCleanup.RenameByHandle(staged, path);
                    replacementLinked = true;
                    UninstallPathGuard.KeepFileOnClose(staged);
                }
                catch (Exception error)
                {
                    try
                    {
                        if (replacementLinked) UninstallPathGuard.UnlinkByHandle(staged);
                        ManagedCacheCleanup.RenameByHandle(previous, path);
                        FileSecurityPreservation.RestoreArchiveAfterRename(previous, expected.Attributes);
                        parked = false;
                    }
                    catch (Exception restoreError) when (restoreError is Win32Exception or IOException)
                    { throw recovery.CannotRecover(parked ? error : restoreError); }
                    throw;
                }
            }
        }

        internal void Write(ReadOnlySpan<byte> bytes, bool clear = false, bool retainClearMarkerOnDeleteFailure = false,
            Action<SafeFileHandle>? onPublished = null)
        {
            if (clear && !Exists) return;
            using var staged = Stage(bytes);
            var published = false;
            try
            {
                Commit(staged);
                published = true;
                onPublished?.Invoke(staged);
                // If the previous file could not be retired, retain the marker.
                // A later recovery must never interpret this clear as a failed save.
                if (clear && !recovery.Pending)
                {
                    try { UninstallPathGuard.DeleteByHandle(staged); }
                    catch (Win32Exception) when (retainClearMarkerOnDeleteFailure) { }
                }
            }
            catch (Win32Exception error) { throw SaveError(error); }
            finally { if (!published && !staged.IsClosed) UninstallPathGuard.DiscardStagedFile(staged); }
        }

        internal void Delete()
        {
            if (original is null) return;
            try
            {
                if (!deleteAccess) throw new InvalidOperationException("Операция должна открыть файл с правом удаления.");
                UninstallPathGuard.DeleteByHandle(original);
            }
            catch (Win32Exception error) { throw SaveError(error); }
        }

        private SafeFileHandle OpenCheckedOriginal()
        {
            var handle = GuardedStoreFile.OpenExisting(path, read: true) ?? throw Changed();
            try
            {
                if (FileSecurityPreservation.GetRevision(handle) != FileSecurityPreservation.GetRevision(original!)) throw Changed();
                return handle;
            }
            catch { handle.Dispose(); throw; }
        }

        private void VerifyOriginalRevision(SafeFileHandle handle)
        {
            if (_originalRevision is { } expected && FileSecurityPreservation.GetRevision(handle) != expected) throw Changed();
        }

        public void Dispose()
        {
            original?.Dispose(); parents.Dispose(); writer.Dispose();
        }

        private static IOException Changed() => new("Файл изменился во время сохранения. Повторите операцию.");
    }

    private static SafeFileHandle? OpenExisting(string path, bool read, bool deleteAccess = false, bool writeAttributes = false)
    {
        try { return UninstallPathGuard.OpenOwnedFile(path, read, deleteAccess, writeAttributes); }
        catch (Win32Exception error) when (error.NativeErrorCode == 2) { return null; }
    }

    private static IOException SaveError(Exception error) => new("Не удалось сохранить локальные данные.", error);
}
