using System.Buffers.Binary;
using System.ComponentModel;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace YMusic.Native;

internal sealed class AudioBufferRecovery(string directory)
{
    internal const string LeaseDirectory = ".audio-leases-v1";
    private const string LeaseExtension = ".lease";
    private const int RecordSize = 36;
    private const int MaximumProtectedBytes = 4096;
    private const int RetryBatchSize = 16;
    private static readonly Dictionary<string, WeakReference<AudioBufferRecovery>> Shared = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _gate = new(1);
    private readonly Queue<string> _pending = new();
    private bool _scanned;

    internal static AudioBufferRecovery ForDirectory(string directory)
    {
        var root = UninstallPathGuard.Canonical(directory, allowVolumeRoot: true);
        lock (Shared)
        {
            if (Shared.TryGetValue(root, out var existing) && existing.TryGetTarget(out var live)) return live;
            foreach (var dead in Shared.Where(entry => !entry.Value.TryGetTarget(out _)).Select(entry => entry.Key).ToArray())
                Shared.Remove(dead);
            var recovery = new AudioBufferRecovery(root);
            Shared.Add(root, new(recovery));
            return recovery;
        }
    }

    internal static SafeFileHandle? Record(string path, SafeFileHandle audio)
    {
        if (FileSecurityPreservation.GetPersistentRevision(audio) is not { } revision) return null;
        var name = Path.GetFileName(path);
        if (!IsBufferName(name)) throw new IOException("Некорректное имя временного аудиофайла.");
        var bytes = new byte[RecordSize];
        "YAB2"u8.CopyTo(bytes);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(4), revision.Volume);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(12), revision.Low);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(20), revision.High);
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(28), revision.Created);
        byte[] protectedBytes;
        try { protectedBytes = ProtectedData.Protect(bytes, Entropy(path), DataProtectionScope.CurrentUser); }
        catch (CryptographicException error) { throw new IOException("Не удалось защитить запись очистки аудиобуфера.", error); }
        if (protectedBytes.Length > MaximumProtectedBytes) throw new IOException("Запись очистки аудиобуфера слишком велика.");
        var receipt = ReceiptPath(path);
        using var parents = UninstallPathGuard.HoldAncestors(receipt, create: true);
        var handle = UninstallPathGuard.CreateStagedFile(receipt);
        try
        {
            RandomAccess.Write(handle, protectedBytes, 0);
            RandomAccess.FlushToDisk(handle);
            return handle;
        }
        catch
        {
            try { RemoveReceipt(handle); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or Win32Exception) { }
            finally { handle.Dispose(); }
            throw;
        }
    }

    internal static int Recover(string directory) => new AudioBufferRecovery(directory).RecoverAsync(CancellationToken.None).GetAwaiter().GetResult();

    internal static (int Removed, int Failed) RecoverForUninstall(string directory)
    {
        var recovery = new AudioBufferRecovery(directory);
        var removed = recovery.RecoverAsync(CancellationToken.None, uninstall: true).GetAwaiter().GetResult();
        return (removed, recovery._pending.Count);
    }

    internal void Defer(string path)
    {
        _gate.Wait();
        try { _pending.Enqueue(ReceiptPath(path)); }
        finally { _gate.Release(); }
    }

    internal async Task<int> RecoverAsync(CancellationToken token, bool uninstall = false)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            token.ThrowIfCancellationRequested();
            if (_scanned && _pending.Count == 0) return 0;
            var markerDirectory = Path.Combine(directory, LeaseDirectory);
            var recovered = 0;
            try
            {
                using var parents = UninstallPathGuard.HoldAncestors(Path.Combine(markerDirectory, ".anchor"));
                if (!parents.Complete)
                {
                    _pending.Clear();
                    _scanned = true;
                    return 0;
                }
                if (_scanned)
                {
                    // Rotate temporarily held entries so they cannot hide the rest of the queue.
                    for (var remaining = Math.Min(_pending.Count, RetryBatchSize); remaining > 0; remaining--)
                    {
                        token.ThrowIfCancellationRequested();
                        recovered += RecoverReceipt(_pending.Dequeue(), uninstall);
                    }
                }
                else
                {
                    _pending.Clear();
                    foreach (var receipt in Directory.EnumerateFiles(markerDirectory, "*" + LeaseExtension))
                    {
                        token.ThrowIfCancellationRequested();
                        recovered += RecoverReceipt(receipt, uninstall);
                    }
                    _scanned = true;
                }
            }
            catch (Exception error) when (!uninstall && (error is IOException or UnauthorizedAccessException or Win32Exception)) { }
            return recovered;
        }
        finally { _gate.Release(); }
    }

    private int RecoverReceipt(string receipt, bool uninstall)
    {
        var name = Path.GetFileName(receipt);
        if (!name.EndsWith(LeaseExtension, StringComparison.Ordinal)) return 0;
        var audioName = name[..^LeaseExtension.Length];
        if (!IsBufferName(audioName)) return 0;
        var result = TryRecover(receipt, Path.Combine(directory, audioName), uninstall, out var removedFiles);
        if (result == RecoveryResult.TemporarilyHeld) _pending.Enqueue(receipt);
        return uninstall ? removedFiles : result == RecoveryResult.Removed ? 1 : 0;
    }

    private static RecoveryResult TryRecover(string receipt, string path, bool uninstall, out int removedFiles)
    {
        removedFiles = 0;
        try
        {
            // An active writer keeps its receipt open without sharing.
            using var marker = UninstallPathGuard.OpenOwnedFile(receipt, read: true);
            if (GuardedStoreFile.ReadFromHandle(marker, MaximumProtectedBytes) is not { } protectedBytes) return RecoveryResult.Preserved;
            var bytes = ProtectedData.Unprotect(protectedBytes, Entropy(path), DataProtectionScope.CurrentUser);
            if (bytes.Length != RecordSize || !bytes.AsSpan(0, 4).SequenceEqual("YAB2"u8)) return RecoveryResult.Preserved;
            try
            {
                using var audio = UninstallPathGuard.OpenOwnedFile(path, read: true);
                if (FileSecurityPreservation.GetPersistentRevision(audio) is not { } revision
                    || revision.Volume != BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(4))
                    || revision.Low != BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(12))
                    || revision.High != BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(20))
                    || revision.Created != BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(28))
                    || FileSecurityPreservation.HasNamedStreamsOrExtendedAttributes(audio)) return RecoveryResult.Preserved;
                UninstallPathGuard.UnlinkByHandle(audio);
                removedFiles++;
            }
            catch (Win32Exception error) when (error.NativeErrorCode is 2 or 3) { }
            if (RemoveReceipt(marker)) removedFiles++;
            return removedFiles != 0 ? RecoveryResult.Removed : RecoveryResult.Preserved;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or Win32Exception or CryptographicException)
        {
            return IsTemporarilyHeld(error) || uninstall && error is not UninstallPathGuard.UnsafeStoragePathException
                && (error is IOException or UnauthorizedAccessException or Win32Exception)
                ? RecoveryResult.TemporarilyHeld : RecoveryResult.Preserved;
        }
    }

    internal static bool RemoveReceipt(SafeFileHandle handle)
    {
        if (FileSecurityPreservation.HasNamedStreamsOrExtendedAttributes(handle)) return false;
        UninstallPathGuard.UnlinkByHandle(handle);
        return true;
    }

    internal static bool IsTemporarilyHeld(Exception error)
    {
        var native = error as Win32Exception ?? error.InnerException as Win32Exception;
        if (native is not null) return native.NativeErrorCode is 32 or 33 or 1224;
        if (error is not IOException io) return false;
        const uint Win32Failure = 0x80070000;
        var hresult = unchecked((uint)io.HResult);
        return (hresult & 0xffff0000) == Win32Failure && (hresult & 0xffff) is 32 or 33 or 1224;
    }

    private enum RecoveryResult { Removed, Preserved, TemporarilyHeld }

    private static string ReceiptPath(string path)
        => Path.Combine(Path.GetDirectoryName(path)!, LeaseDirectory, Path.GetFileName(path) + LeaseExtension);

    private static byte[] Entropy(string path)
        => SHA256.HashData(Encoding.UTF8.GetBytes("Yamuse.AudioBuffer.Recovery.2\0" + UninstallPathGuard.Canonical(path).ToUpperInvariant()));

    private static bool IsBufferName(string name)
    {
        const string prefix = "ym-audio-";
        var stem = Path.GetFileNameWithoutExtension(name);
        return Path.GetExtension(name) is ".mp3" or ".m4a" or ".flac" && stem.Length == prefix.Length + 32
            && stem.StartsWith(prefix, StringComparison.Ordinal)
            && Guid.TryParseExact(stem.AsSpan(prefix.Length), "N", out _);
    }
}
