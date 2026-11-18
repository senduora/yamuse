using System.ComponentModel;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace YMusic.Native;

// Fixed, small local stores use one checked handle for both the read bound
// and the contents. A write owns its staged entry through publish or cleanup.
internal static class GuardedStoreFile
{
    internal static byte[]? Read(string path, int maximumBytes)
    {
        try
        {
            using var parents = UninstallPathGuard.HoldAncestors(path);
            if (!parents.Complete) return null;
            using var handle = OpenExisting(path, read: true);
            if (handle is null) return null;
            return ReadFromHandle(handle, maximumBytes);
        }
        catch (Win32Exception error) { throw new IOException("Не удалось прочитать локальные данные.", error); }
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

    internal static WriteTransaction BeginWrite(string path, bool createParents = true, bool deleteAccess = false)
    {
        path = UninstallPathGuard.Canonical(path);
        var writer = HoldWriteGate(path);
        try
        {
            var parents = UninstallPathGuard.HoldAncestors(path, createParents);
            try
            {
                var original = parents.Complete ? OpenExisting(path, read: true, deleteAccess) : null;
                return new(path, writer, parents, original, deleteAccess);
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

    internal static WriteGate HoldWriteGate(string path)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var sid = identity.User!;
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sid.Value + "\0" +
            UninstallPathGuard.Canonical(path).ToUpperInvariant())));
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
                if (!mutex.WaitOne(TimeSpan.FromSeconds(1))) throw new IOException("Файл занят другим экземпляром Yamuse.");
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
        SafeFileHandle? original, bool deleteAccess) : IDisposable
    {
        private FileSecurityPreservation.FileRevision? _originalRevision;
        internal bool Exists => original is not null;

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
            temporary = UninstallPathGuard.Canonical(temporary);
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
                var stage = UninstallPathGuard.CreateStagedFile(temporary, writeThrough: true, writeDacl: Exists);
                try
                {
                    if (original is not null) FileSecurityPreservation.PrepareReplacement(original, stage);
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

        internal void Commit(SafeFileHandle staged)
        {
            try
            {
                if (original is not null)
                {
                    using var current = OpenCheckedOriginal();
                    FileSecurityPreservation.CheckReplacementMetadata(current);
                    FileSecurityPreservation.PreserveBasicInformation(current, staged);
                    RandomAccess.FlushToDisk(staged);
                    VerifyOriginalRevision(current);
                }
                // Windows has no compare-and-replace primitive for directory entries.
                // Other Yamuse writers hold the same cross-session mutex. Checked
                // originals deny data writes/deletion until just before the handle rename.
                original?.Dispose();
                ManagedCacheCleanup.RenameByHandle(staged, path, replace: Exists);
            }
            catch (Win32Exception error) { throw SaveError(error); }
        }

        internal void Write(ReadOnlySpan<byte> bytes, bool clear = false, bool retainClearMarkerOnDeleteFailure = false)
        {
            if (clear && !Exists) return;
            using var staged = Stage(bytes);
            var published = false;
            try
            {
                Commit(staged);
                published = true;
                if (clear)
                {
                    try { UninstallPathGuard.DeleteByHandle(staged); }
                    catch (Win32Exception) when (retainClearMarkerOnDeleteFailure) { }
                }
            }
            catch (Win32Exception error) { throw SaveError(error); }
            finally { if (!published) UninstallPathGuard.DiscardStagedFile(staged); }
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

    private static SafeFileHandle? OpenExisting(string path, bool read, bool deleteAccess = false)
    {
        try { return UninstallPathGuard.OpenOwnedFile(path, read, deleteAccess); }
        catch (Win32Exception error) when (error.NativeErrorCode == 2) { return null; }
    }

    private static IOException SaveError(Exception error) => new("Не удалось сохранить локальные данные.", error);
}
