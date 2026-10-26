using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace YMusic.Native;

internal sealed partial class OfflineLibrary
{
    private const string RecoveryFileName = ".ym-removal.recovery";
    private const int MaximumRecoveryBytes = 64 * 1024 * 1024;
    private const int MaximumRecoveryEntries = 100_000;
    private string RecoveryPath => Path.Combine(DirectoryPath, RecoveryFileName);

    // A stale transaction is recovered off the UI thread. Reads never acquire
    // Gate: an active operation owns its journal; stale recovery reports a
    // temporary unavailable state until its shared task completes.
    internal async Task RecoverPendingAsync(CancellationToken token = default)
        => await ScheduleRecoveryIfNeeded().WaitAsync(token).ConfigureAwait(false);

    private Task ScheduleRecoveryIfNeeded()
    {
        lock (_directoryState.RecoverySync)
        {
            if (_directoryState.RemovalActive) return Task.CompletedTask;
            if (_directoryState.RecoveryTask is { IsCompleted: false } pending) return pending;
            if (!UninstallPathGuard.EntryExists(RecoveryPath))
            {
                _directoryState.RecoveryTask = null;
                return Task.CompletedTask;
            }
            var task = Task.Run(async () =>
            {
                await _writeLock.WaitAsync().ConfigureAwait(false);
                try { await RecoverUnderGateAsync().ConfigureAwait(false); }
                finally { _writeLock.Release(); }
            });
            _directoryState.RecoveryTask = task;
            // Retain the fault for a prompt read error, and observe it even if
            // no caller awaits the constructor's background recovery.
            _ = task.ContinueWith(t => { _ = t.Exception; }, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            return task;
        }
    }

    private void SetRemovalActive(bool active)
    {
        lock (_directoryState.RecoverySync) _directoryState.RemovalActive = active;
    }

    private void EnsureRecoveryReadReady()
    {
        Task? task;
        lock (_directoryState.RecoverySync)
        {
            if (_directoryState.RemovalActive) return;
            task = _directoryState.RecoveryTask;
        }
        if (task is { IsCompleted: false })
            throw new MusicException("Восстанавливаем прерванную очистку музыки. Повторите открытие библиотеки после завершения восстановления.");
        if (task is { IsCompletedSuccessfully: false })
            throw new MusicException("Не удалось восстановить прерванную очистку музыки. Файлы и журнал сохранены; проверьте доступ и повторите действие.");
    }

    private RecoveryJournalLease CreateRecoveryJournal(IReadOnlyList<StagedRemoval> entries, LibraryCache library,
        PlaylistCache playlists, IReadOnlyList<OfflineItem> remaining, bool clearPlaylists)
    {
        if (Stamp(_indexPath) != library.Stamp || Stamp(_playlistIndexPath) != playlists.Stamp) throw IndexChanged();
        var data = new RecoveryJournal(1, UninstallPathGuard.Canonical(DirectoryPath),
            entries.Select(e => new RecoveryEntry(e.Item, Path.GetFileName(e.Quarantine), Path.GetFileName(e.Recovery))).ToArray(),
            Fingerprint(_indexPath), Digest(JsonSerializer.SerializeToUtf8Bytes(remaining, LibraryJson)),
            clearPlaylists ? new OfflinePlaylistIndex(1, playlists.Items) : null,
            clearPlaylists ? Digest(JsonSerializer.SerializeToUtf8Bytes(new OfflinePlaylistIndex(1, []), PlaylistJson)) : null);
        ValidateJournal(data);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(data, LibraryJson);
        byte[] encrypted;
        try
        {
            if (bytes.Length > MaximumRecoveryBytes) throw new MusicException("Журнал очистки слишком большой. Библиотека не изменена.");
            encrypted = TransformRecovery(bytes, protect: true);
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
        var temporary = RecoveryPath + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            { output.Write(encrypted); output.Flush(flushToDisk: true); }
            if (Stamp(_indexPath) != library.Stamp || Stamp(_playlistIndexPath) != playlists.Stamp) throw IndexChanged();
            File.Move(temporary, RecoveryPath); // Never replace another request or a link.
            return ReadRecoveryJournal();
        }
        finally { SafeDeleteStaged(temporary); }
    }

    private RecoveryJournalLease ReadRecoveryJournal()
    {
        var handle = UninstallPathGuard.OpenOwnedFile(RecoveryPath, read: true);
        FileStream? file = null;
        try
        {
            file = new FileStream(handle, FileAccess.Read, 4096, isAsync: false);
            if (file.Length is <= 0 or > MaximumRecoveryBytes + 64 * 1024) throw new JsonException();
            var encrypted = new byte[checked((int)file.Length)]; file.ReadExactly(encrypted);
            var plain = TransformRecovery(encrypted, protect: false);
            try
            {
                if (plain.Length > MaximumRecoveryBytes) throw new JsonException();
                var data = JsonSerializer.Deserialize<RecoveryJournal>(plain, LibraryJson) ?? throw new JsonException();
                ValidateJournal(data);
                return new(data, file);
            }
            finally { CryptographicOperations.ZeroMemory(plain); }
        }
        catch { if (file is null) handle.Dispose(); else file.Dispose(); throw; }
    }

    private void ValidateJournal(RecoveryJournal journal)
    {
        if (journal.Version != 1 || !string.Equals(journal.Directory, UninstallPathGuard.Canonical(DirectoryPath), StringComparison.OrdinalIgnoreCase)
            || journal.Entries is null || journal.Entries.Count > MaximumRecoveryEntries
            || !HashOrNull(journal.OriginalLibraryHash) || !IsHash(journal.CommittedLibraryHash)
            || !HashOrNull(journal.CommittedPlaylistHash)
            || (journal.PreviousPlaylists is null) != (journal.CommittedPlaylistHash is null)) throw new JsonException();
        if (journal.PreviousPlaylists is { } playlists)
        {
            if (playlists.SchemaVersion != 1 || playlists.Playlists is null) throw new JsonException();
            foreach (var playlist in playlists.Playlists) ValidatePlaylist(playlist);
        }
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in journal.Entries)
        {
            if (entry is null || entry.Item is not { } item || string.IsNullOrWhiteSpace(item.Id) || string.IsNullOrWhiteSpace(item.RequestId)
                || item.Title is null || item.Artist is null || item.Album is null || string.IsNullOrWhiteSpace(item.Codec)
                || item.DurationMs < 0 || item.Bitrate < 0 || item.Bytes <= 0 || item.SavedUtc == default || !IsHash(item.Sha256)) throw new JsonException();
            ValidateArtists(item.Artists); ValidateAlbumId(item.AlbumId);
            _ = ResolvePath(item.FileName);
            _ = ResolvePath(entry.Quarantine);
            _ = ResolvePath(entry.Recovery);
            if (!GeneratedName(entry.Quarantine, ".ym-delete-", ".pending")
                || !GeneratedName(entry.Recovery, ".ym-recovered-", Path.GetExtension(item.FileName))
                || !names.Add(item.FileName) || !names.Add(entry.Quarantine) || !names.Add(entry.Recovery)) throw new JsonException();
        }
    }

    private async Task RecoverUnderGateAsync()
    {
        if (!UninstallPathGuard.EntryExists(RecoveryPath)) return;
        EnsureSafeDirectory();
        using var ancestors = UninstallPathGuard.HoldExistingAncestors(RecoveryPath);
        using var journal = ReadRecoveryJournal();
        var currentHash = Fingerprint(_indexPath);
        var opened = new List<(RecoveryEntry Entry, string Path, FileStream? File)>();
        try
        {
            // Preflight every referenced object before the first recovery rename.
            // Only authenticated exact mappings can nominate a quarantine file;
            // every surviving file must also match its protected SHA-256 handle.
            foreach (var entry in journal.Data.Entries)
            {
                var quarantine = ResolvePath(entry.Quarantine);
                var recovery = ResolvePath(entry.Recovery);
                var original = ResolvePath(entry.Item.FileName);
                var path = UninstallPathGuard.EntryExists(quarantine) ? quarantine
                    : UninstallPathGuard.EntryExists(recovery) ? recovery : original;
                if (!UninstallPathGuard.EntryExists(path))
                {
                    opened.Add((entry, original, null)); continue; // Already deleted after commit.
                }
                var handle = UninstallPathGuard.OpenOwnedFile(path, read: true);
                FileStream? stream = null;
                var transferred = false;
                try
                {
                    stream = new FileStream(handle, FileAccess.Read, 1, isAsync: false);
                    if (stream.Length != entry.Item.Bytes || !await RemovalHashMatchesAsync(stream, entry.Item.Sha256, CancellationToken.None).ConfigureAwait(false))
                    {
                        // A replacement at the original name is foreign. It is
                        // preserved, never adopted under the old item's metadata.
                        if (path == original) { stream.Dispose(); stream = null; opened.Add((entry, original, null)); continue; }
                        throw new IOException("Файл журнала очистки изменён. Файлы и журнал сохранены.");
                    }
                    if (path == quarantine && UninstallPathGuard.EntryExists(original) && UninstallPathGuard.EntryExists(recovery))
                        throw new IOException("Имя восстановления занято. Файлы и журнал сохранены.");
                    opened.Add((entry, path, stream)); transferred = true;
                }
                finally { if (!transferred) { if (stream is null) handle.Dispose(); else stream.Dispose(); } }
            }
            var restored = new List<RecoveredAudio>();
            foreach (var candidate in opened)
            {
                if (candidate.File is null) { restored.Add(new(candidate.Entry.Item, null)); continue; }
                var original = ResolvePath(candidate.Entry.Item.FileName);
                var target = candidate.Path;
                if (target != original)
                {
                    var destination = UninstallPathGuard.EntryExists(original) ? ResolvePath(candidate.Entry.Recovery) : original;
                    if (!destination.Equals(target, StringComparison.OrdinalIgnoreCase))
                    {
                        ManagedCacheCleanup.RenameByHandle(candidate.File.SafeFileHandle, destination); target = destination;
                        RemovalCheckpoint?.Invoke("recovery-after-restore");
                    }
                }
                restored.Add(new(candidate.Entry.Item, target));
            }
            await RestoreInterruptedPlaylistsAsync(journal.Data, currentHash).ConfigureAwait(false);
            await RepairRecoveredIndexAsync(restored).ConfigureAwait(false);
            RemovalCheckpoint?.Invoke("recovery-after-index");
            DeleteRecoveryJournal(journal);
        }
        finally
        {
            foreach (var candidate in opened) candidate.File?.Dispose();
            lock (_cacheLock) { _libraryCache = null; _playlistCache = null; }
        }
    }

    private async Task RestoreInterruptedPlaylistsAsync(RecoveryJournal journal, string? libraryHash = null)
    {
        if (journal.PreviousPlaylists is null || (libraryHash ?? Fingerprint(_indexPath)) != journal.OriginalLibraryHash) return;
        var current = LoadPlaylists(force: true, recovering: true);
        if (Fingerprint(_playlistIndexPath) != journal.CommittedPlaylistHash) return; // Preserve external/unrelated changes.
        await WriteIndexAsync(_playlistIndexPath, journal.PreviousPlaylists, PlaylistJson, current.Stamp, CancellationToken.None).ConfigureAwait(false);
    }

    private async Task RepairRecoveredIndexAsync(IReadOnlyList<RecoveredAudio> restored)
    {
        var latest = LoadLibrary(force: true, recovering: true);
        var repaired = latest.Items.ToList();
        var changed = false;
        foreach (var audio in restored)
        {
            var existing = repaired.FindIndex(item => SameOfflineItem(item, audio.Item));
            if (audio.Path is null)
            {
                if (existing >= 0) { repaired.RemoveAt(existing); changed = true; }
                continue;
            }
            var item = audio.Item with { FileName = Path.GetFileName(audio.Path) };
            if (existing >= 0)
            {
                if (!SameOfflineItem(repaired[existing], item)) { repaired[existing] = item; changed = true; }
            }
            else if (!repaired.Any(row => SameOfflineItem(row, item))) { repaired.Add(item); changed = true; }
        }
        if (changed) await WriteIndexAsync(_indexPath, repaired, LibraryJson, latest.Stamp, CancellationToken.None).ConfigureAwait(false);
        lock (_cacheLock) _libraryCache = null;
    }

    private static void DeleteRecoveryJournal(RecoveryJournalLease journal) => UninstallPathGuard.DeleteByHandle(journal.File.SafeFileHandle);
    private static bool IsHash(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);
    private static bool HashOrNull(string? value) => value is null || IsHash(value);
    private static bool GeneratedName(string name, string prefix, string suffix)
        => name.StartsWith(prefix, StringComparison.Ordinal) && name.EndsWith(suffix, StringComparison.Ordinal)
            && name.Length == prefix.Length + 32 + suffix.Length && Guid.TryParseExact(name.AsSpan(prefix.Length, 32), "N", out _);
    private static string Digest(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    private static string? Fingerprint(string path)
    {
        if (!UninstallPathGuard.EntryExists(path)) return null;
        using var handle = UninstallPathGuard.OpenOwnedFile(path, read: true, deleteAccess: false);
        using var file = new FileStream(handle, FileAccess.Read);
        if (file.Length > MaximumRecoveryBytes) throw new IOException("Индекс слишком большой для безопасного восстановления очистки.");
        return Convert.ToHexString(SHA256.HashData(file));
    }

    private byte[] TransformRecovery(byte[] input, bool protect)
    {
        var entropyBytes = SHA256.HashData(Encoding.UTF8.GetBytes("Yamuse.OfflineRemoval.1\0" + UninstallPathGuard.Canonical(DirectoryPath).ToUpperInvariant()));
        var blob = RecoveryBlob.Allocate(input);
        var entropy = RecoveryBlob.Allocate(entropyBytes);
        try
        {
            var success = protect ? CryptProtectData(ref blob, "Yamuse offline removal", ref entropy, IntPtr.Zero, IntPtr.Zero, 1, out var output)
                : CryptUnprotectData(ref blob, IntPtr.Zero, ref entropy, IntPtr.Zero, IntPtr.Zero, 1, out output);
            if (!success) throw new CryptographicException(Marshal.GetLastWin32Error());
            try
            {
                if (output.Length < 0 || output.Length > MaximumRecoveryBytes + 64 * 1024) throw new CryptographicException();
                var bytes = new byte[output.Length]; Marshal.Copy(output.Data, bytes, 0, bytes.Length); return bytes;
            }
            finally { ZeroRecoveryMemory(output.Data, output.Length); LocalFree(output.Data); }
        }
        finally { blob.Free(); entropy.Free(); }
    }

    private sealed record RecoveryEntry(OfflineItem Item, string Quarantine, string Recovery);
    private sealed record RecoveryJournal(int Version, string Directory, IReadOnlyList<RecoveryEntry> Entries,
        string? OriginalLibraryHash, string CommittedLibraryHash, OfflinePlaylistIndex? PreviousPlaylists, string? CommittedPlaylistHash);
    private sealed record RecoveredAudio(OfflineItem Item, string? Path);
    private sealed class RecoveryJournalLease(RecoveryJournal data, FileStream file) : IDisposable
    { internal RecoveryJournal Data => data; internal FileStream File => file; public void Dispose() => file.Dispose(); }

    [StructLayout(LayoutKind.Sequential)]
    private struct RecoveryBlob
    {
        internal int Length; internal IntPtr Data;
        internal static RecoveryBlob Allocate(byte[] bytes)
        { var data = Marshal.AllocHGlobal(bytes.Length); Marshal.Copy(bytes, 0, data, bytes.Length); return new() { Length = bytes.Length, Data = data }; }
        internal void Free() { ZeroRecoveryMemory(Data, Length); Marshal.FreeHGlobal(Data); }
    }
    private static void ZeroRecoveryMemory(IntPtr pointer, int length)
    { for (var i = 0; i < length; i++) Marshal.WriteByte(pointer, i, 0); }
    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref RecoveryBlob input, string description, ref RecoveryBlob entropy, IntPtr reserved, IntPtr prompt, int flags, out RecoveryBlob output);
    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref RecoveryBlob input, IntPtr description, ref RecoveryBlob entropy, IntPtr reserved, IntPtr prompt, int flags, out RecoveryBlob output);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr pointer);
}
