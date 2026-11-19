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
    private const string ProtectedRecoveryJournal = "Журнал восстановления содержит именованные потоки или расширенные атрибуты. Аудиофайлы и журнал сохранены. Восстановление можно повторить после восстановления исходного журнала.";
    private string RecoveryPath => Path.Combine(DirectoryPath, RecoveryFileName);
    private string CommittedRemovalPath => Path.Combine(DirectoryPath, CommittedRemovalFileName);
    private string SaveRecoveryPath => Path.Combine(DirectoryPath, SaveRecoveryFileName);

    // A stale transaction is recovered off the UI thread. Reads never acquire
    // Gate: an active operation owns its journal; stale recovery reports a
    // temporary unavailable state until its shared task completes.
    internal async Task RecoverPendingAsync(CancellationToken token = default)
    {
        try { await ScheduleRecoveryIfNeeded().WaitAsync(token).ConfigureAwait(false); }
        catch (Exception error) when (error is Win32Exception or UnauthorizedAccessException)
        { throw new IOException("Не удалось восстановить прерванную операцию с офлайн-библиотекой.", error); }
    }

    private Task ScheduleRecoveryIfNeeded()
    {
        lock (_directoryState.RecoverySync)
        {
            if (_directoryState.StorageMutationActive) return Task.CompletedTask;
            if (_directoryState.RecoveryTask is { IsCompleted: false } pending) return pending;
            try
            {
                _beforeRecoveryProbeForChecks?.Invoke();
                if (!HasPendingRecovery())
                {
                    _directoryState.RecoveryTask = null;
                    return Task.CompletedTask;
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or Win32Exception)
            {
                // A startup probe can fail before there is any journal handle.
                // Keep storage closed to reads/writes until a later retry can check it.
                var unavailable = Task.FromException(error);
                _ = unavailable.Exception;
                return _directoryState.RecoveryTask = unavailable;
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
        || UninstallPathGuard.EntryExists(CommittedRemovalPath) || UninstallPathGuard.EntryExists(SaveRecoveryPath)
        || UninstallPathGuard.EntryExists(IndexPublicationRecoveryPath);

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
            throw new MusicException("Восстанавливаем прерванную операцию с офлайн-библиотекой. Повторите открытие после завершения восстановления.")
                { OfflineStorageRetryable = true };
        if (task is { Exception.InnerException: MusicException { Message: RecoveryAuthenticationFailure or ProtectedRecoveryJournal } error })
            throw new MusicException(error.Message);
        if (task is { IsCompletedSuccessfully: false })
        {
            const string message = "Не удалось восстановить прерванную операцию с офлайн-библиотекой. Файлы и журнал сохранены; проверьте доступ и повторите действие.";
            throw new MusicException(message)
                { OfflineStorageRetryable = task.Exception?.InnerException is IOException or UnauthorizedAccessException or Win32Exception };
        }
    }

    private RecoveryJournalLease CreateRecoveryJournal(IReadOnlyList<StagedRemoval> entries, LibraryCache library,
        PlaylistCache playlists, IReadOnlyList<OfflineItem> remaining, bool clearPlaylists)
    {
        void VerifyIndexes()
        {
            CheckIndexStamp(ReadIndexSnapshot(_indexPath).Stamp, library.Stamp);
            CheckIndexStamp(ReadIndexSnapshot(_playlistIndexPath).Stamp, playlists.Stamp);
        }
        VerifyIndexes();
        var tracked = remaining.ToHashSet(ReferenceEqualityComparer.Instance);
        var selectedByName = library.Items.Where(item => !tracked.Contains(item))
            .ToLookup(item => item.FileName, StringComparer.OrdinalIgnoreCase);
        var recorded = new List<RecoveryEntry>();
        foreach (var entry in entries)
        {
            var aliases = selectedByName[entry.Item.FileName]
                .Where(row => !ReferenceEquals(row, entry.Item) && SameRecoveredAudio(row, entry.Item)).ToArray();
            tracked.Add(entry.Item);
            tracked.UnionWith(aliases);
            recorded.Add(new(entry.Item, Path.GetFileName(entry.Quarantine), Path.GetFileName(entry.Recovery), entry.Identity,
                aliases.Length > 0 ? aliases : null));
        }
        var unstaged = library.Items.Where(item => !tracked.Contains(item)).ToArray();
        var remainingByName = remaining.ToLookup(item => item.FileName, StringComparer.OrdinalIgnoreCase);
        var attestLibrary = unstaged.Length > 0 || recorded.Any(entry => HasDifferentAudioClaim(remainingByName[entry.Item.FileName], entry.Item));
        var data = new RecoveryJournal(7, UninstallPathGuard.Canonical(DirectoryPath), recorded,
            library.Stamp.Hash, Digest(JsonSerializer.SerializeToUtf8Bytes(remaining, LibraryJson)),
            clearPlaylists ? new OfflinePlaylistIndex(1, playlists.Items) : null,
            clearPlaylists ? Digest(JsonSerializer.SerializeToUtf8Bytes(new OfflinePlaylistIndex(1, []), PlaylistJson)) : null,
            unstaged, clearPlaylists ? Guid.NewGuid() : null, attestLibrary ? Guid.NewGuid() : null);
        ValidateJournal(data);
        using (PublishJournal(RecoveryPath, data, MaximumRecoveryBytes, VerifyIndexes)) { }
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

    private (T Data, FileStream File) ReadProtectedJournal<T>(string path, int maximumBytes, bool allowProtectedMetadata = false) where T : class
    {
        var handle = UninstallPathGuard.OpenOwnedFile(path, read: true);
        FileStream? file = null;
        try
        {
            if (!allowProtectedMetadata && FileSecurityPreservation.HasNamedStreamsOrExtendedAttributes(handle)) throw new MusicException(ProtectedRecoveryJournal);
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
            CheckIndexStamp(ReadIndexSnapshot(_indexPath).Stamp, expected);
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
            RetireJournal(journal, SaveRecoveryPath);
            return;
        }
        foreach (var name in new[] { pending.Partial, pending.FileName })
        {
            var path = ResolvePath(name);
            if (!UninstallPathGuard.EntryExists(path)) continue;
            using var handle = UninstallPathGuard.OpenOwnedFile(path, read: true);
            if (GetStorageFileIdentity(handle) != expectedIdentity) continue;
            using var source = new FileStream(handle, FileAccess.Read, bufferSize: 1, isAsync: false);
            if (source.Length != pending.Bytes || !await RemovalHashMatchesAsync(source, pending.Sha256, CancellationToken.None).ConfigureAwait(false)) continue;
            SaveCheckpoint?.Invoke("save-recovery-after-hash");
            var references = RollbackUnindexedSaveAudio(handle, name);
            if (references.Length > 0 && (name != pending.FileName || !references.Any(item => item.Bytes == pending.Bytes
                && item.Sha256.Equals(pending.Sha256, StringComparison.OrdinalIgnoreCase))))
                throw new IOException("Индекс изменился после сохранения трека. Файл и журнал сохранены.");
        }
        RetireJournal(journal, SaveRecoveryPath);
    }

    private OfflineItem[] RollbackUnindexedSaveAudio(SafeFileHandle audio, string name)
    {
        // The index may have changed while the audio was being hashed. Hold
        // the current authoritative index through the unlink decision.
        using var openedIndex = OpenIndex(_indexPath);
        if (openedIndex is null && RecoverWithIndexNameReserved(audio)) return [];
        using var appearedIndex = openedIndex is null ? OpenIndex(_indexPath) ?? throw IndexChanged() : null;
        var currentIndex = openedIndex ?? appearedIndex!;
        var latest = LoadLibrary(force: true, recovering: true);
        CheckIndexStamp(ReadIndexSnapshot(currentIndex).Stamp, latest.Stamp);
        var references = latest.Items.Where(item => item.FileName.Equals(name, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (references.Length == 0 && !FileSecurityPreservation.HasNamedStreamsOrExtendedAttributes(audio)) UninstallPathGuard.DeleteByHandle(audio);
        return references;
    }

    private bool RecoverWithIndexNameReserved(SafeFileHandle audio)
    {
        lock (_directoryState.IndexPublicationSync)
        {
            SafeFileHandle reservation;
            try { reservation = UninstallPathGuard.CreateStagedFile(_indexPath, deleteOnClose: true); }
            catch (Win32Exception error) when (error.NativeErrorCode is 80 or 183) { return false; }
            using (reservation)
                if (!FileSecurityPreservation.HasNamedStreamsOrExtendedAttributes(audio)) UninstallPathGuard.DeleteByHandle(audio);
            return true;
        }
    }

    private StorageFileIdentity? GetStorageFileIdentity(SafeFileHandle file)
    {
        const int FileIdInfo = 18;
        const int InvalidFunction = 1, NotSupported = 50, InvalidParameter = 87, InvalidLevel = 124;
        int error;
        if (FileIdentityErrorForChecks is { } injected) error = injected;
        else if (GetFileInformationByHandleEx(file, FileIdInfo, out var identity, (uint)Marshal.SizeOf<NativeStorageFileIdentity>()))
            return (identity.Low | identity.High) == 0 ? null : new(identity.Volume, identity.Low, identity.High);
        else error = Marshal.GetLastWin32Error();

        // A legacy ID may change across rename, especially on FAT. Only a
        // filesystem or API capability failure permits recovery without an ID.
        return error is InvalidFunction or NotSupported or InvalidParameter or InvalidLevel ? null : throw new Win32Exception(error);
    }

    private void ValidateJournal(RecoveryJournal journal)
    {
        if (journal.Version is not (1 or 2 or 3 or 4 or 5 or 6 or 7) || !string.Equals(journal.Directory, UninstallPathGuard.Canonical(DirectoryPath), StringComparison.OrdinalIgnoreCase)
            || journal.Entries is null || journal.Entries.Count > MaximumRecoveryEntries
            || (journal.Version >= 3) != (journal.Unstaged is not null)
            || (journal.Unstaged?.Count ?? 0) > MaximumRecoveryEntries - journal.Entries.Count
            || !HashOrNull(journal.OriginalLibraryHash) || !IsHash(journal.CommittedLibraryHash)
            || !HashOrNull(journal.CommittedPlaylistHash)
            || (journal.PreviousPlaylists is null) != (journal.CommittedPlaylistHash is null)
            || journal.Version >= 5 && ((journal.PreviousPlaylists is null) != (journal.PlaylistClearOperation is null)
                || journal.PlaylistClearOperation == Guid.Empty)
            || journal.Version >= 6 && (journal.Unstaged is { Count: > 0 } && journal.LibraryRemovalOperation is null
                || journal.LibraryRemovalOperation == Guid.Empty)
            || journal.Version == 6 && journal.Unstaged is { Count: 0 } && journal.LibraryRemovalOperation is not null) throw new JsonException();
        if (journal.PreviousPlaylists is { } playlists)
        {
            if (playlists.SchemaVersion != 1 || playlists.Playlists is null) throw new JsonException();
            foreach (var playlist in playlists.Playlists) ValidatePlaylist(playlist);
        }
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var remainingEntries = MaximumRecoveryEntries - journal.Entries.Count - (journal.Unstaged?.Count ?? 0);
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
            if (entry.Aliases is not { } aliases) continue;
            if (journal.Version < 6 || aliases.Count > remainingEntries) throw new JsonException();
            remainingEntries -= aliases.Count;
            foreach (var alias in aliases)
            {
                ValidateRecoveryItem(alias);
                if (!alias.FileName.Equals(item.FileName, StringComparison.OrdinalIgnoreCase) || !SameRecoveredAudio(alias, item))
                    throw new JsonException();
            }
        }
        if (journal.Unstaged is { } unstaged)
            foreach (var item in unstaged) ValidateRecoveryItem(item);
    }

    private void ValidateRecoveryItem(OfflineItem? item)
    {
        if (item is null || string.IsNullOrWhiteSpace(item.Id) || string.IsNullOrWhiteSpace(item.RequestId)
            || item.Title is null || item.Artist is null || !TrackRow.ValidPrimaryArtistName(item.PrimaryArtistName)
            || item.Album is null || string.IsNullOrWhiteSpace(item.Codec)
            || item.DurationMs < 0 || item.Bitrate < 0 || item.ConfirmedBitrate is <= 0 || item.Bytes <= 0 || item.SavedUtc == default || !IsHash(item.Sha256)) throw new JsonException();
        ValidateArtists(item.Artists); ValidateAlbumId(item.AlbumId);
        _ = ResolvePath(item.FileName);
    }

    private async Task RecoverUnderGateAsync()
    {
        using var ancestors = HoldStorageParents(RecoveryPath);
        if (!ancestors.Complete) return;
        RecoverIndexPublicationUnderGate();
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
        LibraryCache? legacyIndex = null;
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
                var legacyOriginal = entry.Identity is null && path == original;
                if (legacyOriginal && !journal.Committed) continue;
                // 0.7.0 could finish an in-process rollback at the original
                // name. Verify and retain its exact already-indexed row only.
                if (!UninstallPathGuard.EntryExists(path) || legacyOriginal
                    && !(legacyIndex ??= LoadLibrary(force: true, recovering: true)).Items.Any(item => SameOfflineItem(item, entry.Item)))
                {
                    opened.Add((entry, original, null)); continue; // Already deleted after commit.
                }
                var handle = UninstallPathGuard.OpenOwnedFile(path, read: true, deleteAccess: !legacyOriginal);
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
                    opened.Add((entry, path, stream)); transferred = true;
                }
                finally { if (!transferred) { if (stream is null) handle.Dispose(); else stream.Dispose(); } }
            }
            var restored = new List<RecoveredAudio>();
            var planned = new List<(FileStream File, string Destination)>();
            var currentIndex = opened.Any(candidate => candidate.File is not null) ? LoadLibrary(force: true, recovering: true) : null;
            var currentRows = currentIndex?.Items.ToLookup(item => item.FileName, StringComparer.OrdinalIgnoreCase);
            var knownIndex = currentIndex is not null && IsKnownRemovalIndex(currentIndex, journal,
                () => opened.Any(candidate => candidate.File is not null && HasDifferentAudioClaim(currentRows![candidate.Entry.Item.FileName], candidate.Entry.Item)), out _);
            foreach (var candidate in opened)
            {
                if (candidate.File is null) { restored.Add(new(candidate.Entry.Item, candidate.Entry.Recovery, null)); continue; }
                var preserveMetadata = FileSecurityPreservation.HasNamedStreamsOrExtendedAttributes(candidate.File.SafeFileHandle);
                var original = ResolvePath(candidate.Entry.Item.FileName);
                var target = candidate.Path;
                if (candidate.Entry.Identity is null && target == original && !preserveMetadata)
                    continue; // Existing metadata stays in place, never recreated from the journal.
                var claims = currentRows!;
                var originalClaimed = HasConflictingRecoveryClaim(claims[candidate.Entry.Item.FileName], candidate.Entry.Item.FileName,
                    candidate.Entry.Item, knownIndex);
                if (target != original || originalClaimed && candidate.Entry.Identity is not null && !preserveMetadata)
                {
                    // Keep weakly identified bytes under their authenticated
                    // alternate name so another interrupted replay can find them.
                    var alternate = ResolvePath(candidate.Entry.Recovery);
                    var destination = originalClaimed ? alternate : RestoreDestination(candidate.Entry.Identity, original, alternate);
                    var name = Path.GetFileName(destination);
                    if (!preserveMetadata && HasConflictingRecoveryClaim(claims[name], name, candidate.Entry.Item, knownIndex))
                        throw IndexChanged();
                    if (!destination.Equals(target, StringComparison.OrdinalIgnoreCase))
                    {
                        if (UninstallPathGuard.EntryExists(destination))
                            throw new IOException("Имя восстановления занято. Файлы и журнал сохранены.");
                        planned.Add((candidate.File, destination)); target = destination;
                    }
                }
                restored.Add(new(candidate.Entry.Item, candidate.Entry.Recovery, preserveMetadata ? null : target));
            }
            foreach (var (file, destination) in planned)
            {
                ManagedCacheCleanup.RenameByHandle(file.SafeFileHandle, destination);
                RemovalCheckpoint?.Invoke("recovery-after-restore");
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
        if (journal.Committed || journal.Data.Version < 5 || journal.Data.PreviousPlaylists is not { } previous) return;
        IndexStamp current;
        // A size mismatch already proves these are unrelated bytes. The
        // transaction's empty playlist index has one exact serialized shape.
        var clearedLength = JsonSerializer.SerializeToUtf8Bytes(new OfflinePlaylistIndex(1, []), PlaylistJson).Length;
        using (var handle = OpenIndex(_playlistIndexPath))
        {
            if (handle is null || RandomAccess.GetLength(handle) != clearedLength) return;
            current = ReadIndexSnapshot(handle).Stamp;
            if (current.Hash != journal.Data.CommittedPlaylistHash) return;
        }
        // Legacy receipts only describe planned bytes. An independent empty
        // index can have the same bytes, so only an attested file authorizes rollback.
        var (proof, file) = TryOpenRemovalIndexProof(journal.Data.PlaylistClearOperation, playlists: true);
        using var receipt = file;
        if (proof?.Identity is not { } identity || !MatchesRemovalIndexProof(proof, current)) return;
        try
        {
            await WriteIndexAsync(_playlistIndexPath, previous, PlaylistJson, current, CancellationToken.None,
                requireIdentity: identity).ConfigureAwait(false);
        }
        catch (MusicException error) when (error.OfflineStorageRetryable && !UninstallPathGuard.EntryExists(IndexPublicationRecoveryPath))
        { /* The index changed again before rollback. The new version is authoritative. */ }
    }

    private string RemovalIndexProofPath(Guid operation, bool playlists, int? repairSlot = null)
        => Path.Combine(DirectoryPath, repairSlot is { } slot ? $".ym-library-repair-{operation:N}-{slot}.proof"
            : (playlists ? ".ym-playlist-clear-" : ".ym-library-removal-") + operation.ToString("N") + ".proof");

    private void PublishRemovalIndexProof(Guid operation, SafeFileHandle stage, bool playlists, bool required = true,
        int? repairSlot = null, string? repairedHash = null)
    {
        var identity = GetStorageFileIdentity(stage);
        if (identity is null)
        {
            if (required) throw RemovalIndexProofUnavailable();
            return;
        }
        FileSecurityPreservation.PinWriteAndChangeTimes(stage);
        var revision = FileSecurityPreservation.GetPersistentRevision(stage);
        if (revision is not { Written: > 0, Changed: > 0 }) throw RemovalIndexProofUnavailable();
        if (repairSlot is not null) RetireProof(operation, playlists, repairSlot);
        PublishJournal(RemovalIndexProofPath(operation, playlists, repairSlot), new RemovalIndexProof(operation, identity,
            revision.Value.Written, revision.Value.Changed, repairSlot, repairedHash), MaximumSaveRecoveryBytes).Dispose();
    }

    private StorageFileIdentity RequireRemovalIndexIdentity(SafeFileHandle file)
        => GetStorageFileIdentity(file) ?? throw RemovalIndexProofUnavailable();

    private static IOException RemovalIndexProofUnavailable()
        => new("Не удалось подтвердить файловую ревизию офлайн-индекса. Очистка остановлена, файлы и снимки сохранены.");

    private (RemovalIndexProof? Proof, FileStream? File) TryOpenRemovalIndexProof(Guid? operation, bool playlists, int? repairSlot = null)
    {
        if (operation is not { } id) return default;
        var path = RemovalIndexProofPath(id, playlists, repairSlot);
        try
        {
            var (proof, file) = ReadProtectedJournal<RemovalIndexProof>(path, MaximumSaveRecoveryBytes, allowProtectedMetadata: true);
            if (proof.Operation == id && proof.Identity is { } identity && (identity.Low | identity.High) != 0 && proof.Written > 0
                && proof.RepairSlot == repairSlot && (repairSlot is null ? proof.RepairedHash is null : IsHash(proof.RepairedHash)))
                return (proof, file);
            file.Dispose();
        }
        catch (Win32Exception error) when (error.NativeErrorCode is 2 or 3) { }
        catch (Win32Exception error)
        {
            RemovalCheckpoint?.Invoke("removal-proof-unavailable");
            throw new IOException("Не удалось прочитать подтверждение офлайн-очистки. Восстановление можно повторить.", error);
        }
        catch (Exception error) when (error is JsonException or UninstallPathGuard.UnsafeStoragePathException
            or MusicException { Message: RecoveryAuthenticationFailure })
        { /* A foreign record cannot authorize overwriting a snapshot. Leave it intact. */ }
        return default;
    }

    private static bool MatchesRemovalIndexProof(RemovalIndexProof proof, IndexStamp stamp)
        => proof.Changed > 0 && stamp.Revision is { } revision && proof.Written == revision.Written && proof.Changed == revision.Changed
            && proof.Identity == new StorageFileIdentity(revision.Volume, revision.Low, revision.High);

    private async Task RepairRecoveredIndexAsync(IReadOnlyList<RecoveredAudio> restored, RecoveryJournalLease journal)
    {
        var latest = LoadLibrary(force: true, recovering: true);
        var repaired = latest.Items.ToList();
        var entries = journal.Data.Entries.ToDictionary(entry => entry.Item.FileName, StringComparer.OrdinalIgnoreCase);
        var legacyAliases = journal.Data.Version < 6 ? journal.Data.Unstaged?.ToLookup(row => row.FileName, StringComparer.OrdinalIgnoreCase) : null;
        var knownIndex = IsKnownRemovalIndex(latest, journal, () => restored.Any(audio => audio.Path is { } path
            && Path.GetFileName(path).Equals(audio.Item.FileName, StringComparison.OrdinalIgnoreCase)
            && HasDifferentAudioClaim(latest.Items.Where(row => row.FileName.Equals(audio.Item.FileName, StringComparison.OrdinalIgnoreCase)), audio.Item)),
            out var attested);
        Action<SafeFileHandle, IndexStamp>? attestRepair = null;
        if (attested is { } trusted)
        {
            var operation = journal.Data.LibraryRemovalOperation!.Value;
            // Keep the proof of the currently installed revision until the
            // second slot's revision has actually reached the authoritative name.
            var nextSlot = trusted.RepairSlot == 0 ? 1 : 0;
            attestRepair = (stage, stamp) => PublishRemovalIndexProof(operation, stage, playlists: false,
                repairSlot: nextSlot, repairedHash: stamp.Hash);
        }
        var changed = false;
        foreach (var audio in restored)
        {
            // An interrupted repair can already have committed the alternate
            // journal name. Both names refer to the same authenticated item.
            var original = audio.Item;
            var recoveryName = audio.RecoveryName;
            var savedAliases = entries[original.FileName].Aliases
                ?? legacyAliases?[original.FileName].Where(row => SameRecoveredAudio(row, original)).ToArray() ?? [];
            var savedRows = savedAliases.Prepend(original);
            bool AliasName(string name) => name.Equals(original.FileName, StringComparison.OrdinalIgnoreCase)
                || name.Equals(recoveryName, StringComparison.OrdinalIgnoreCase);
            if (audio.Path is null)
            {
                changed |= repaired.RemoveAll(row => AliasName(row.FileName)
                    && savedRows.Any(saved => SameOfflineItem(row, saved with { FileName = row.FileName }))) > 0;
                continue;
            }
            var name = Path.GetFileName(audio.Path);
            bool Names(string left, string right) => left.Equals(right, StringComparison.OrdinalIgnoreCase);
            var claims = repaired.Where(row => Names(row.FileName, name));
            if (HasConflictingRecoveryClaim(claims, name, original, knownIndex)) throw IndexChanged();
            var aliases = claims.Where(row => SameRecoveredAudio(row, original)).ToList();
            for (var i = repaired.Count - 1; i >= 0; i--)
            {
                var row = repaired[i];
                if (Names(row.FileName, name) || !SameRecoveredAudio(row, original) || !AliasName(row.FileName)) continue;
                var moved = row with { FileName = name };
                if (aliases.Any(alias => SameOfflineItem(alias, moved with { FileName = alias.FileName }))) repaired.RemoveAt(i);
                else { repaired[i] = moved; aliases.Add(moved); }
                changed = true;
            }
            // Match exact aliases first. An independently edited row can then
            // stand in for one remaining saved row with the same identity.
            var unmatched = aliases.ToList();
            var missing = new List<OfflineItem>();
            foreach (var saved in savedRows.Select(item => item with { FileName = name }))
            {
                var exact = unmatched.FindIndex(row => SameOfflineItem(row, saved with { FileName = row.FileName }));
                if (exact >= 0) unmatched.RemoveAt(exact);
                else if (!aliases.Any(row => SameOfflineItem(row, saved with { FileName = row.FileName }))
                    && !missing.Any(row => SameOfflineItem(row, saved))) missing.Add(saved);
            }
            for (var i = missing.Count - 1; i >= 0; i--)
            {
                var saved = missing[i];
                var edited = unmatched.FindIndex(row => row.Id == saved.Id && row.RequestId == saved.RequestId && row.AlbumId == saved.AlbumId);
                if (edited < 0) continue;
                unmatched.RemoveAt(edited);
                missing.RemoveAt(i);
            }
            foreach (var saved in missing)
            {
                var edited = unmatched.FindIndex(row => row.Id == saved.Id && row.RequestId == saved.RequestId
                    && (row.AlbumId is null || saved.AlbumId is null));
                if (edited >= 0) unmatched.RemoveAt(edited);
                else { repaired.Add(saved); changed = true; }
            }
        }
        if (!journal.Committed && journal.Data.Unstaged is { Count: > 0 } unstaged
            && latest.Stamp.Hash == journal.Data.CommittedLibraryHash && journal.Data.Version >= 6)
        {
            var (proof, file) = TryOpenRemovalIndexProof(journal.Data.LibraryRemovalOperation, playlists: false);
            using var receipt = file;
            if (proof?.Identity is { } identity && MatchesRemovalIndexProof(proof, latest.Stamp))
            {
                await RepairUnstagedMetadataAsync(repaired, restored, unstaged, journal.Data.CommittedLibraryHash, latest.Stamp, changed, identity,
                    attestRepair).ConfigureAwait(false);
                changed = false;
            }
        }
        if (changed) await WriteIndexAsync(_indexPath, repaired, LibraryJson, latest.Stamp, CancellationToken.None,
            attestStage: attestRepair, requireIdentity: attested?.Identity).ConfigureAwait(false);
        lock (_cacheLock) _libraryCache = null;
    }

    private static bool SameRecoveredAudio(OfflineItem row, OfflineItem original)
        => row.Bytes == original.Bytes && row.Sha256.Equals(original.Sha256, StringComparison.OrdinalIgnoreCase);

    private bool IsKnownRemovalIndex(LibraryCache index, RecoveryJournalLease journal, Func<bool> hasConflictingClaims,
        out (StorageFileIdentity Identity, int? RepairSlot)? attested)
    {
        attested = null;
        if (index.Stamp.Hash is { } hash && hash == journal.Data.OriginalLibraryHash) return true;
        if (journal.Committed || journal.Data.LibraryRemovalOperation is not { } operation
            || !hasConflictingClaims()) return false;
        IOException? unavailable = null;
        for (var i = -1; i < 2; i++)
        {
            int? repairSlot = i < 0 ? null : i;
            if (repairSlot is null && index.Stamp.Hash != journal.Data.CommittedLibraryHash) continue;
            try
            {
                var (proof, file) = TryOpenRemovalIndexProof(operation, playlists: false, repairSlot);
                using var receipt = file;
                if (proof?.Identity is not { } identity || !MatchesRemovalIndexProof(proof, index.Stamp)
                    || repairSlot is not null && proof.RepairedHash != index.Stamp.Hash) continue;
                attested = (identity, repairSlot);
                return true;
            }
            catch (IOException error) { unavailable ??= error; }
        }
        if (unavailable is not null) throw unavailable;
        return false;
    }

    private static bool HasDifferentAudioClaim(IEnumerable<OfflineItem> claims, OfflineItem source)
        => claims.Any(row => !SameRecoveredAudio(row, source));

    private static bool HasConflictingRecoveryClaim(IEnumerable<OfflineItem> claims, string name, OfflineItem source, bool originalIndex)
        // The authenticated pre-removal index can already contain conflicting
        // rows. Only reuse this exception for the file's original name.
        => !(originalIndex && name.Equals(source.FileName, StringComparison.OrdinalIgnoreCase))
            && HasDifferentAudioClaim(claims, source);

    private async Task RepairUnstagedMetadataAsync(List<OfflineItem> repaired, IReadOnlyList<RecoveredAudio> restored,
        IReadOnlyList<OfflineItem> unstaged, string writtenHash, IndexStamp expected, bool stagedChanged, StorageFileIdentity identity,
        Action<SafeFileHandle, IndexStamp>? attestRepair)
    {
        var verified = restored.Where(audio => audio.Path is not null)
            .ToDictionary(audio => audio.Path!, audio => audio.Item, StringComparer.OrdinalIgnoreCase);
        var held = new List<FileStream>();
        var absentPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var metadataChanged = false;
        try
        {
            foreach (var group in unstaged.GroupBy(item => item.FileName, StringComparer.OrdinalIgnoreCase))
            {
                var path = ResolvePath(group.Key);
                if (!verified.TryGetValue(path, out var reference))
                {
                    if (UninstallPathGuard.EntryExists(path))
                    {
                        if ((File.GetAttributes(path) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0) continue;
                        var audio = await HoldVerifiedUnstagedAudioAsync(path, group).ConfigureAwait(false);
                        if (audio is not { } matched) continue;
                        held.Add(matched.File);
                        reference = matched.Item;
                    }
                    else absentPaths.Add(path);
                }
                foreach (var item in group)
                {
                    if (reference is not null && !SameRecoveredAudio(reference, item)) continue;
                    if (repaired.Any(row => row.FileName.Equals(group.Key, StringComparison.OrdinalIgnoreCase)
                        && SameOfflineItem(row, item with { FileName = row.FileName }))) continue;
                    repaired.Add(item);
                    metadataChanged = true;
                }
            }
            if (stagedChanged || metadataChanged)
                await WriteIndexAsync(_indexPath, repaired, LibraryJson, expected, CancellationToken.None,
                    metadataChanged ? CheckUnstagedRollback : null, attestStage: attestRepair, requireIdentity: identity).ConfigureAwait(false);
        }
        finally { foreach (var lease in held) lease.Dispose(); }

        void CheckUnstagedRollback()
        {
            if (Fingerprint(_indexPath) != writtenHash || absentPaths.Any(UninstallPathGuard.EntryExists)) throw IndexChanged();
        }

    }

    private static async Task<(FileStream File, OfflineItem Item)?> HoldVerifiedUnstagedAudioAsync(string path, IEnumerable<OfflineItem> items)
    {
        var handle = UninstallPathGuard.OpenOwnedFile(path, read: true, deleteAccess: false);
        FileStream? stream = null;
        var keep = false;
        try
        {
            stream = new FileStream(handle, FileAccess.Read, bufferSize: 1, isAsync: false);
            var length = stream.Length;
            if (!items.Any(item => item.Bytes == length) || FileSecurityPreservation.HasNamedStreamsOrExtendedAttributes(handle)) return null;
            var digest = await HashRemovalAudioAsync(stream, CancellationToken.None).ConfigureAwait(false);
            var reference = items.FirstOrDefault(item => item.Bytes == length && item.Sha256.Equals(digest, StringComparison.OrdinalIgnoreCase));
            if (reference is null) return null;
            keep = true;
            return (stream, reference);
        }
        finally { if (!keep) { if (stream is null) handle.Dispose(); else stream.Dispose(); } }
    }

    private void CommitRecoveryJournal(RecoveryJournalLease journal)
    {
        ManagedCacheCleanup.RenameByHandle(journal.File.SafeFileHandle, CommittedRemovalPath);
        journal.Committed = true;
    }

    private void DeleteRecoveryJournal(RecoveryJournalLease journal)
    {
        // Keep every proof while the main journal can still be replayed.
        // Closing the held file completes a delete-pending retirement.
        RetireJournal(journal.File, journal.Committed ? CommittedRemovalPath : RecoveryPath);
        journal.Dispose();
        RemovalCheckpoint?.Invoke("removal-after-journal-retired");
        // These exact, operation-specific proofs are inert once the main
        // journal is gone, including when a reader prevents their cleanup.
        if (journal.Data.Version >= 5)
            TryRetireIndexPublication(() => RetireProof(journal.Data.PlaylistClearOperation, playlists: true));
        if (journal.Data.Version >= 6)
            TryRetireIndexPublication(() => RetireProof(journal.Data.LibraryRemovalOperation, playlists: false));
        if (journal.Data.Version >= 6 && journal.Data.LibraryRemovalOperation is { } operation)
        {
            TryRetireIndexPublication(() => RetireProof(operation, playlists: false, repairSlot: 0));
            TryRetireIndexPublication(() => RetireProof(operation, playlists: false, repairSlot: 1));
        }
        RemovalCheckpoint?.Invoke("removal-after-proofs-retired");
    }

    private void RetireProof(Guid? operation, bool playlists, int? repairSlot = null)
    {
        if (operation is not { } id) return;
        using var file = TryOpenRemovalIndexProof(operation, playlists, repairSlot).File;
        if (file is not null) RetireJournal(file, RemovalIndexProofPath(id, playlists, repairSlot));
    }

    private static void RetireJournal(FileStream journal, string path, bool preserve = false)
    {
        var handle = journal.SafeFileHandle;
        if (preserve || FileSecurityPreservation.HasNamedStreamsOrExtendedAttributes(handle))
        {
            // Preserve metadata added while the operation ran. The retired
            // journal is inert and will not be replayed again.
            ManagedCacheCleanup.RenameByHandle(handle, path + ".preserved-" + Guid.NewGuid().ToString("N"));
        }
        else UninstallPathGuard.DeleteByHandle(handle);
    }
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

    private sealed record RecoveryEntry(OfflineItem Item, string Quarantine, string Recovery, StorageFileIdentity? Identity = null,
        IReadOnlyList<OfflineItem>? Aliases = null);
    private sealed record RecoveryJournal(int Version, string Directory, IReadOnlyList<RecoveryEntry> Entries,
        string? OriginalLibraryHash, string CommittedLibraryHash, OfflinePlaylistIndex? PreviousPlaylists, string? CommittedPlaylistHash,
        IReadOnlyList<OfflineItem>? Unstaged = null, Guid? PlaylistClearOperation = null, Guid? LibraryRemovalOperation = null);
    private sealed record RemovalIndexProof(Guid Operation, StorageFileIdentity? Identity, long Written, long Changed = 0,
        int? RepairSlot = null, string? RepairedHash = null);
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
