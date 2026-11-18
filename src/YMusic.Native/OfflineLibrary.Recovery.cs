using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace YMusic.Native;

internal sealed partial class OfflineLibrary
{
    private const string RecoveryFileName = ".ym-removal.recovery";
    private const string CommittedRemovalFileName = ".ym-removal.committed";
    private const string SaveRecoveryFileName = ".ym-save.recovery";
    private const int MaximumRecoveryBytes = 64 * 1024 * 1024;
    private const int MaximumSaveRecoveryBytes = 64 * 1024;
    private const int MaximumRecoveryEntries = 100_000;
    internal const string RecoveryAuthenticationFailure = "Не удалось подтвердить журнал восстановления. Если «Документы» были перенесены, верните папку в прежнее расположение вместе с Yamuse\\Music и повторите. Файлы и журнал сохранены.";
    private string RecoveryPath => Path.Combine(DirectoryPath, RecoveryFileName);
    private string CommittedRemovalPath => Path.Combine(DirectoryPath, CommittedRemovalFileName);
    private string SaveRecoveryPath => Path.Combine(DirectoryPath, SaveRecoveryFileName);

    // A stale transaction is recovered off the UI thread. Reads never acquire
    // Gate: an active operation owns its journal; stale recovery reports a
    // temporary unavailable state until its shared task completes.
    internal async Task RecoverPendingAsync(CancellationToken token = default)
    {
        try { await ScheduleRecoveryIfNeeded().WaitAsync(token).ConfigureAwait(false); }
        catch (Win32Exception error) { throw new IOException("Не удалось восстановить прерванную операцию с офлайн-библиотекой.", error); }
    }

    private Task ScheduleRecoveryIfNeeded()
    {
        lock (_directoryState.RecoverySync)
        {
            if (_directoryState.StorageMutationActive) return Task.CompletedTask;
            if (_directoryState.RecoveryTask is { IsCompleted: false } pending) return pending;
            if (!HasPendingRecovery())
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

    private bool HasPendingRecovery() => UninstallPathGuard.EntryExists(RecoveryPath)
        || UninstallPathGuard.EntryExists(CommittedRemovalPath) || UninstallPathGuard.EntryExists(SaveRecoveryPath);

    private async Task RecoverIfPendingUnderGateAsync()
    {
        if (HasPendingRecovery()) await RecoverUnderGateAsync().ConfigureAwait(false);
    }

    private void SetStorageMutationActive(bool active)
    {
        lock (_directoryState.RecoverySync) _directoryState.StorageMutationActive = active;
    }

    private void EnsureRecoveryReadReady()
    {
        Task? task;
        lock (_directoryState.RecoverySync)
        {
            if (_directoryState.StorageMutationActive) return;
            task = _directoryState.RecoveryTask;
        }
        if (task is { IsCompleted: false })
            throw new MusicException("Восстанавливаем прерванную операцию с офлайн-библиотекой. Повторите открытие после завершения восстановления.");
        if (task is { Exception.InnerException: MusicException { Message: RecoveryAuthenticationFailure } })
            throw new MusicException(RecoveryAuthenticationFailure);
        if (task is { IsCompletedSuccessfully: false })
            throw new MusicException("Не удалось восстановить прерванную операцию с офлайн-библиотекой. Файлы и журнал сохранены; проверьте доступ и повторите действие.");
    }

    private RecoveryJournalLease CreateRecoveryJournal(IReadOnlyList<StagedRemoval> entries, LibraryCache library,
        PlaylistCache playlists, IReadOnlyList<OfflineItem> remaining, bool clearPlaylists)
    {
        if (Stamp(_indexPath) != library.Stamp || Stamp(_playlistIndexPath) != playlists.Stamp) throw IndexChanged();
        var tracked = remaining.Concat(entries.Select(entry => entry.Item)).ToHashSet(ReferenceEqualityComparer.Instance);
        var unstaged = library.Items.Where(item => !tracked.Contains(item)).ToArray();
        var data = new RecoveryJournal(4, UninstallPathGuard.Canonical(DirectoryPath),
            entries.Select(e => new RecoveryEntry(e.Item, Path.GetFileName(e.Quarantine), Path.GetFileName(e.Recovery), e.Identity)).ToArray(),
            Fingerprint(_indexPath), Digest(JsonSerializer.SerializeToUtf8Bytes(remaining, LibraryJson)),
            clearPlaylists ? new OfflinePlaylistIndex(1, playlists.Items) : null,
            clearPlaylists ? Digest(JsonSerializer.SerializeToUtf8Bytes(new OfflinePlaylistIndex(1, []), PlaylistJson)) : null,
            unstaged);
        ValidateJournal(data);
        using (PublishJournal(RecoveryPath, data, MaximumRecoveryBytes, () =>
        {
            if (Stamp(_indexPath) != library.Stamp || Stamp(_playlistIndexPath) != playlists.Stamp) throw IndexChanged();
        })) { }
        return ReadRecoveryJournal(RecoveryPath, committed: false);
    }

    private FileStream PublishJournal<T>(string path, T data, int maximumBytes, Action? beforePublication = null)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(data, LibraryJson);
        byte[] encrypted;
        try
        {
            if (bytes.Length > maximumBytes) throw new MusicException("Журнал офлайн-библиотеки слишком большой. Операция остановлена.");
            encrypted = TransformRecovery(bytes, protect: true);
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        var handle = UninstallPathGuard.CreateStagedFile(temporary, writeThrough: true);
        FileStream? output = null;
        try
        {
            UninstallPathGuard.SetDeleteDisposition(handle, delete: true);
            output = new FileStream(handle, FileAccess.Write);
            output.Write(encrypted); output.Flush(flushToDisk: true);
            beforePublication?.Invoke();
            TemporaryPublicationCheckpoint?.Invoke("journal-before-publish");
            ManagedCacheCleanup.RenameByHandle(handle, path, releaseDeleteDisposition: true); // Never replace another request or a link.
            return output;
        }
        catch
        {
            UninstallPathGuard.DiscardStagedFile(handle);
            if (output is null) handle.Dispose(); else output.Dispose();
            throw;
        }
    }

    private RecoveryJournalLease ReadRecoveryJournal(string path, bool committed)
    {
        var (data, file) = ReadProtectedJournal<RecoveryJournal>(path, MaximumRecoveryBytes);
        try { ValidateJournal(data); return new(data, file) { Committed = committed }; }
        catch { file.Dispose(); throw; }
    }

    private (T Data, FileStream File) ReadProtectedJournal<T>(string path, int maximumBytes) where T : class
    {
        var handle = UninstallPathGuard.OpenOwnedFile(path, read: true);
        FileStream? file = null;
        try
        {
            file = new FileStream(handle, FileAccess.Read, 4096, isAsync: false);
            if (file.Length <= 0 || file.Length > maximumBytes + 64 * 1024) throw new JsonException();
            var encrypted = new byte[checked((int)file.Length)]; file.ReadExactly(encrypted);
            byte[] plain;
            try { plain = TransformRecovery(encrypted, protect: false); }
            catch (CryptographicException) { throw new MusicException(RecoveryAuthenticationFailure); }
            try
            {
                if (plain.Length > maximumBytes) throw new JsonException();
                var data = JsonSerializer.Deserialize<T>(plain, LibraryJson) ?? throw new JsonException();
                return (data, file);
            }
            finally { CryptographicOperations.ZeroMemory(plain); }
        }
        catch { if (file is null) handle.Dispose(); else file.Dispose(); throw; }
    }

    private FileStream CreateSaveJournal(string partial, OfflineItem item, StorageFileIdentity? identity, IndexStamp expected)
    {
        var data = new SaveJournal(2, UninstallPathGuard.Canonical(DirectoryPath), Path.GetFileName(partial), item.FileName,
            item.Bytes, item.Sha256, identity);
        ValidateSaveJournal(data);
        return PublishJournal(SaveRecoveryPath, data, MaximumSaveRecoveryBytes, () =>
        {
            if (Stamp(_indexPath) != expected) throw IndexChanged();
        });
    }

    private void ValidateSaveJournal(SaveJournal journal)
    {
        if (journal.Version is not (1 or 2) || !string.Equals(journal.Directory, UninstallPathGuard.Canonical(DirectoryPath), StringComparison.OrdinalIgnoreCase)
            || journal.Partial is null || !GeneratedName(journal.Partial, ".ym-audio-", ".partial")
            || Path.GetExtension(journal.FileName) is not (".mp3" or ".m4a" or ".flac")
            || journal.Bytes <= 0 || journal.Bytes > AudioBuffer.MaximumBytes || !IsHash(journal.Sha256)
            || journal.Version == 1 && journal.Identity is null
            || journal.Identity is { } identity && (identity.Low | identity.High) == 0) throw new JsonException();
        _ = ResolvePath(journal.Partial);
        _ = ResolvePath(journal.FileName);
    }

    private async Task RecoverSaveUnderGateAsync()
    {
        if (!UninstallPathGuard.EntryExists(SaveRecoveryPath)) return;
        var (pending, file) = ReadProtectedJournal<SaveJournal>(SaveRecoveryPath, MaximumSaveRecoveryBytes);
        using var journal = file;
        ValidateSaveJournal(pending);
        if (pending.Identity is not { } expectedIdentity)
        {
            // This filesystem could not prove identity. Leave both pathnames
            // untouched and retire only the authenticated transaction record.
            UninstallPathGuard.DeleteByHandle(journal.SafeFileHandle);
            return;
        }
        var latest = LoadLibrary(force: true, recovering: true);
        foreach (var name in new[] { pending.Partial, pending.FileName })
        {
            var path = ResolvePath(name);
            if (!UninstallPathGuard.EntryExists(path)) continue;
            using var handle = UninstallPathGuard.OpenOwnedFile(path, read: true);
            if (GetStorageFileIdentity(handle) != expectedIdentity) continue;
            using var source = new FileStream(handle, FileAccess.Read, bufferSize: 1, isAsync: false);
            if (source.Length != pending.Bytes || !await RemovalHashMatchesAsync(source, pending.Sha256, CancellationToken.None).ConfigureAwait(false)) continue;
            var references = latest.Items.Where(item => item.FileName.Equals(name, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (references.Length > 0)
            {
                if (name == pending.FileName && references.Any(item => item.Bytes == pending.Bytes
                    && item.Sha256.Equals(pending.Sha256, StringComparison.OrdinalIgnoreCase))) continue;
                throw new IOException("Индекс изменился после сохранения трека. Файл и журнал сохранены.");
            }
            // Roll back the exact unindexed object. A foreign replacement,
            // even with identical bytes, has a different file identity.
            UninstallPathGuard.DeleteByHandle(handle);
        }
        UninstallPathGuard.DeleteByHandle(journal.SafeFileHandle);
    }

    private StorageFileIdentity? GetStorageFileIdentity(SafeFileHandle file)
    {
        const int FileIdInfo = 18;
        const int NotSupported = 50, InvalidParameter = 87;
        int error;
        if (FileIdentityErrorForChecks is { } injected) error = injected;
        else if (GetFileInformationByHandleEx(file, FileIdInfo, out var identity, (uint)Marshal.SizeOf<NativeStorageFileIdentity>()))
            return (identity.Low | identity.High) == 0 ? null : new(identity.Volume, identity.Low, identity.High);
        else error = Marshal.GetLastWin32Error();

        // FileIdInfo can return either error on a filesystem without 128-bit
        // identifiers. A legacy ID may change across rename, especially on FAT.
        return error is NotSupported or InvalidParameter ? null : throw new Win32Exception(error);
    }

    private void ValidateJournal(RecoveryJournal journal)
    {
        if (journal.Version is not (1 or 2 or 3 or 4) || !string.Equals(journal.Directory, UninstallPathGuard.Canonical(DirectoryPath), StringComparison.OrdinalIgnoreCase)
            || journal.Entries is null || journal.Entries.Count > MaximumRecoveryEntries
            || (journal.Version >= 3) != (journal.Unstaged is not null)
            || (journal.Unstaged?.Count ?? 0) > MaximumRecoveryEntries - journal.Entries.Count
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
            if (entry?.Item is not { } item) throw new JsonException();
            ValidateRecoveryItem(item);
            _ = ResolvePath(entry.Quarantine);
            _ = ResolvePath(entry.Recovery);
            if (!GeneratedName(entry.Quarantine, ".ym-delete-", ".pending")
                || !GeneratedName(entry.Recovery, ".ym-recovered-", Path.GetExtension(item.FileName))
                || entry.Identity is { } identity && (journal.Version < 4 || (identity.Low | identity.High) == 0)
                || !names.Add(item.FileName) || !names.Add(entry.Quarantine) || !names.Add(entry.Recovery)) throw new JsonException();
        }
        if (journal.Unstaged is { } unstaged)
            foreach (var item in unstaged) ValidateRecoveryItem(item);
    }

    private void ValidateRecoveryItem(OfflineItem? item)
    {
        if (item is null || string.IsNullOrWhiteSpace(item.Id) || string.IsNullOrWhiteSpace(item.RequestId)
            || item.Title is null || item.Artist is null || !TrackRow.ValidPrimaryArtistName(item.PrimaryArtistName)
            || item.Album is null || string.IsNullOrWhiteSpace(item.Codec)
            || item.DurationMs < 0 || item.Bitrate < 0 || item.Bytes <= 0 || item.SavedUtc == default || !IsHash(item.Sha256)) throw new JsonException();
        ValidateArtists(item.Artists); ValidateAlbumId(item.AlbumId);
        _ = ResolvePath(item.FileName);
    }

    private async Task RecoverUnderGateAsync()
    {
        using var ancestors = HoldStorageParents(RecoveryPath);
        if (!ancestors.Complete) return;
        var pending = UninstallPathGuard.EntryExists(RecoveryPath);
        var committed = UninstallPathGuard.EntryExists(CommittedRemovalPath);
        if (pending && committed) throw new IOException("Обнаружены два журнала очистки. Файлы и журналы сохранены.");
        await RecoverSaveUnderGateAsync().ConfigureAwait(false);
        if (!pending && !committed) return;
        using var journal = ReadRecoveryJournal(committed ? CommittedRemovalPath : RecoveryPath, committed);
        // V1 only recorded byte hashes. Preserve the old conservative rollback
        // when the hashes are indistinguishable. Fix the keep decision before
        // index repair can bring back the original bytes on another restart.
        if (!committed && journal.Data.Version == 1 && Fingerprint(_indexPath) != journal.Data.OriginalLibraryHash)
            CommitRecoveryJournal(journal);
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
                if (entry.Identity is null && path == original)
                {
                    // Without a file ID the original name is ambiguous after
                    // a committed delete. An unfinished operation can retain
                    // existing metadata, but must not re-adopt this pathname.
                    if (journal.Committed) opened.Add((entry, original, null));
                    continue;
                }
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
                    if (entry.Identity is { } expectedIdentity && GetStorageFileIdentity(handle) != expectedIdentity
                        || stream.Length != entry.Item.Bytes || !await RemovalHashMatchesAsync(stream, entry.Item.Sha256, CancellationToken.None).ConfigureAwait(false))
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
                if (candidate.File is null) { restored.Add(new(candidate.Entry.Item, candidate.Entry.Recovery, null)); continue; }
                var original = ResolvePath(candidate.Entry.Item.FileName);
                var target = candidate.Path;
                if (target != original)
                {
                    // Keep weakly identified bytes under their authenticated
                    // alternate name so another interrupted replay can find them.
                    var destination = candidate.Entry.Identity is null || UninstallPathGuard.EntryExists(original)
                        ? ResolvePath(candidate.Entry.Recovery) : original;
                    if (!destination.Equals(target, StringComparison.OrdinalIgnoreCase))
                    {
                        ManagedCacheCleanup.RenameByHandle(candidate.File.SafeFileHandle, destination); target = destination;
                        RemovalCheckpoint?.Invoke("recovery-after-restore");
                    }
                }
                restored.Add(new(candidate.Entry.Item, candidate.Entry.Recovery, target));
            }
            await RestoreInterruptedPlaylistsAsync(journal).ConfigureAwait(false);
            await RepairRecoveredIndexAsync(restored, journal).ConfigureAwait(false);
            RemovalCheckpoint?.Invoke("recovery-after-index");
            DeleteRecoveryJournal(journal);
        }
        finally
        {
            foreach (var candidate in opened) candidate.File?.Dispose();
            lock (_cacheLock) { _libraryCache = null; _playlistCache = null; }
        }
    }

    private async Task RestoreInterruptedPlaylistsAsync(RecoveryJournalLease journal)
    {
        if (journal.Committed || journal.Data.PreviousPlaylists is not { } previous) return;
        var current = LoadPlaylists(force: true, recovering: true);
        if (Fingerprint(_playlistIndexPath) != journal.Data.CommittedPlaylistHash) return; // Preserve external/unrelated changes.
        await WriteIndexAsync(_playlistIndexPath, previous, PlaylistJson, current.Stamp, CancellationToken.None).ConfigureAwait(false);
    }

    private async Task RepairRecoveredIndexAsync(IReadOnlyList<RecoveredAudio> restored, RecoveryJournalLease journal)
    {
        var latest = LoadLibrary(force: true, recovering: true);
        var repaired = latest.Items.ToList();
        var changed = false;
        foreach (var audio in restored)
        {
            // An interrupted repair can already have committed the alternate
            // journal name. Both names refer to the same authenticated item.
            var original = audio.Item;
            var recovery = original with { FileName = audio.RecoveryName };
            bool Matches(OfflineItem row) => row.FileName == original.FileName ? SameOfflineItem(row, original)
                : row.FileName == recovery.FileName && SameOfflineItem(row, recovery);
            if (audio.Path is null)
            {
                changed |= repaired.RemoveAll(Matches) > 0;
                continue;
            }
            var item = original with { FileName = Path.GetFileName(audio.Path) };
            var existing = repaired.FindIndex(Matches);
            if (existing < 0)
            {
                repaired.Add(item); changed = true;
                continue;
            }
            if (!SameOfflineItem(repaired[existing], item)) { repaired[existing] = item; changed = true; }
            for (var i = repaired.Count - 1; i > existing; i--)
            {
                if (Matches(repaired[i])) { repaired.RemoveAt(i); changed = true; }
            }
        }
        if (!journal.Committed && journal.Data.Unstaged is { Count: > 0 } unstaged
            && Fingerprint(_indexPath) == journal.Data.CommittedLibraryHash)
        {
            await RepairUnstagedMetadataAsync(repaired, restored, unstaged, journal.Data.CommittedLibraryHash, latest.Stamp, changed).ConfigureAwait(false);
        }
        else if (changed) await WriteIndexAsync(_indexPath, repaired, LibraryJson, latest.Stamp, CancellationToken.None).ConfigureAwait(false);
        lock (_cacheLock) _libraryCache = null;
    }

    private async Task RepairUnstagedMetadataAsync(List<OfflineItem> repaired, IReadOnlyList<RecoveredAudio> restored,
        IReadOnlyList<OfflineItem> unstaged, string writtenHash, IndexStamp expected, bool stagedChanged)
    {
        var verified = restored.Where(audio => audio.Path is not null)
            .ToDictionary(audio => audio.Path!, audio => audio.Item, StringComparer.OrdinalIgnoreCase);
        var held = new List<FileStream>();
        var absentPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var metadataChanged = false;
        try
        {
            foreach (var item in unstaged)
            {
                var sameName = repaired.Where(row => row.FileName.Equals(item.FileName, StringComparison.OrdinalIgnoreCase));
                if (sameName.Any(row => SameOfflineItem(row, item) || !SameBytes(row, item))) continue;
                var path = ResolvePath(item.FileName);
                if (verified.TryGetValue(path, out var previous))
                {
                    if (!SameBytes(previous, item)) continue;
                }
                else if (UninstallPathGuard.EntryExists(path))
                {
                    if ((File.GetAttributes(path) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0) continue;
                    var lease = await HoldVerifiedUnstagedAudioAsync(path, item).ConfigureAwait(false);
                    if (lease is null) continue;
                    held.Add(lease);
                    verified.Add(path, item);
                }
                else absentPaths.Add(path);
                repaired.Add(item);
                metadataChanged = true;
            }
            if (stagedChanged || metadataChanged)
                await WriteIndexAsync(_indexPath, repaired, LibraryJson, expected, CancellationToken.None,
                    metadataChanged ? CheckUnstagedRollback : null).ConfigureAwait(false);
        }
        finally { foreach (var lease in held) lease.Dispose(); }

        void CheckUnstagedRollback()
        {
            if (Fingerprint(_indexPath) != writtenHash || absentPaths.Any(UninstallPathGuard.EntryExists)) throw IndexChanged();
        }

        static bool SameBytes(OfflineItem left, OfflineItem right)
            => left.Bytes == right.Bytes && left.Sha256.Equals(right.Sha256, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<FileStream?> HoldVerifiedUnstagedAudioAsync(string path, OfflineItem item)
    {
        var handle = UninstallPathGuard.OpenOwnedFile(path, read: true, deleteAccess: false);
        FileStream? stream = null;
        var keep = false;
        try
        {
            stream = new FileStream(handle, FileAccess.Read, bufferSize: 1, isAsync: false);
            keep = stream.Length == item.Bytes && await RemovalHashMatchesAsync(stream, item.Sha256, CancellationToken.None).ConfigureAwait(false);
            return keep ? stream : null;
        }
        finally { if (!keep) { if (stream is null) handle.Dispose(); else stream.Dispose(); } }
    }

    private void CommitRecoveryJournal(RecoveryJournalLease journal)
    {
        ManagedCacheCleanup.RenameByHandle(journal.File.SafeFileHandle, CommittedRemovalPath);
        journal.Committed = true;
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
        if (file.Length > MaximumIndexBytes) throw new IOException("Индекс слишком большой для безопасного восстановления очистки.");
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

    private sealed record RecoveryEntry(OfflineItem Item, string Quarantine, string Recovery, StorageFileIdentity? Identity = null);
    private sealed record RecoveryJournal(int Version, string Directory, IReadOnlyList<RecoveryEntry> Entries,
        string? OriginalLibraryHash, string CommittedLibraryHash, OfflinePlaylistIndex? PreviousPlaylists, string? CommittedPlaylistHash,
        IReadOnlyList<OfflineItem>? Unstaged = null);
    private sealed record SaveJournal(int Version, string Directory, string Partial, string FileName, long Bytes, string Sha256, StorageFileIdentity? Identity);
    private sealed record StorageFileIdentity(ulong Volume, ulong Low, ulong High);
    private sealed record RecoveredAudio(OfflineItem Item, string RecoveryName, string? Path);
    private sealed class RecoveryJournalLease(RecoveryJournal data, FileStream file) : IDisposable
    {
        internal RecoveryJournal Data => data;
        internal FileStream File => file;
        internal bool Committed { get; set; }
        public void Dispose() => file.Dispose();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeStorageFileIdentity { internal ulong Volume, Low, High; }
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle file, int kind, out NativeStorageFileIdentity information, uint size);

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
