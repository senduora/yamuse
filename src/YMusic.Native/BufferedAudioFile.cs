using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace YMusic.Native;

// Keep the containing directories and the file name stable while handing a
// completed file to Windows. Cleanup reopens the original object by handle.
internal sealed class BufferedAudioFile : IDisposable
{
    private const uint ReadExtendedAttributes = 0x8, ReadAttributes = 0x80, DeleteAccess = 0x10000, GenericRead = 0x80000000;
    private const uint Overlapped = 0x40000000;
    private const FileShare IdentityShare = FileShare.ReadWrite | FileShare.Delete;
    private readonly object _gate = new();
    private readonly UninstallPathGuard.AncestorLease _ancestors;
    private readonly SafeFileHandle _identity;
    private SafeFileHandle? _receipt;
    private AudioBufferRecovery? _recovery;
    private FileStream? _stream;

    internal string Path { get; }
    internal FileStream Stream => Volatile.Read(ref _stream) ?? throw new ObjectDisposedException(nameof(BufferedAudioFile));

    internal BufferedAudioFile(string path, FileOptions options)
    {
        Path = UninstallPathGuard.Canonical(path);
        try
        {
            _ancestors = UninstallPathGuard.HoldAncestors(Path, create: true);
            try
            {
                var writer = new FileStream(Path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read, 1, FileOptions.Asynchronous | options);
                try { _identity = Reopen(writer.SafeFileHandle, ReadAttributes, IdentityShare); }
                catch { writer.Dispose(); throw; }
                _stream = writer;
            }
            catch { _ancestors.Dispose(); throw; }
        }
        catch (Win32Exception error) { throw new IOException("Не удалось открыть временный аудиофайл.", error); }
    }

    internal void Seal()
    {
        lock (_gate)
        {
            var writer = Stream;
            // Both handles forbid rename and deletion. The reader must share
            // the current writer until it has taken over the file.
            var bridge = ReopenReader(writer, FileShare.ReadWrite);
            _stream = bridge;
            writer.Dispose();
            _stream = ReopenReader(bridge, FileShare.Read);
            bridge.Dispose();
        }
    }

    internal void RecordForRecovery(AudioBufferRecovery recovery)
    {
        _receipt = AudioBufferRecovery.Record(Path, Stream.SafeFileHandle);
        if (_receipt is not null) _recovery = recovery;
    }

    private static FileStream ReopenReader(FileStream current, FileShare share)
    {
        SafeFileHandle handle;
        try { handle = Reopen(current.SafeFileHandle, GenericRead, share, Overlapped); }
        catch (Win32Exception error) { throw new IOException("Не удалось открыть временный аудиофайл для чтения.", error); }
        try { return new FileStream(handle, FileAccess.Read, 1, isAsync: true); }
        catch { handle.Dispose(); throw; }
    }

    public void Dispose()
    {
        FileStream? stream;
        lock (_gate)
        {
            stream = _stream;
            _stream = null;
        }
        if (stream is null) return;
        try { stream.Dispose(); }
        finally
        {
            var retry = false;
            try { retry = UnlinkRequiresRetry(); }
            finally
            {
                if (retry) _ = RetryUnlinkAsync();
                else ReleaseIdentity();
            }
        }
    }

    private bool UnlinkRequiresRetry()
    {
        try
        {
            using var file = Reopen(_identity, DeleteAccess | ReadAttributes | ReadExtendedAttributes, IdentityShare);
            if (FileSecurityPreservation.HasNamedStreamsOrExtendedAttributes(file)) return false;
            UninstallPathGuard.UnlinkByHandle(file);
            if (_receipt is not null) AudioBufferRecovery.RemoveReceipt(_receipt);
            return false;
        }
        catch (Exception error) when (error is Win32Exception or IOException)
        {
            return AudioBufferRecovery.IsTemporarilyHeld(error);
        }
    }

    private async Task RetryUnlinkAsync()
    {
        var stillHeld = false;
        try
        {
            // Native decoding and output routing can still be releasing a read handle.
            for (var delay = 50; delay <= 6400; delay *= 2)
            {
                await Task.Delay(delay).ConfigureAwait(false);
                stillHeld = UnlinkRequiresRetry();
                if (!stillHeld) break;
            }
        }
        finally
        {
            ReleaseIdentity();
            if (stillHeld) _recovery?.Defer(Path);
        }
    }

    private void ReleaseIdentity()
    {
        _receipt?.Dispose();
        _identity.Dispose();
        _ancestors.Dispose();
    }

    private static SafeFileHandle Reopen(SafeFileHandle original, uint access, FileShare share, uint flags = 0)
    {
        var handle = ReOpenFile(original, access, share, flags);
        if (!handle.IsInvalid) return handle;
        var error = Marshal.GetLastWin32Error();
        handle.Dispose();
        throw new Win32Exception(error);
    }

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern SafeFileHandle ReOpenFile(SafeFileHandle original, uint access, FileShare share, uint flags);
}
