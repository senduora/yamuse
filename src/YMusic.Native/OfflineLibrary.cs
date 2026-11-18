using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Collections.Concurrent;
using System.Buffers;
using System.ComponentModel;

namespace YMusic.Native;

internal sealed record OfflineItem([property: JsonRequired] string Id, [property: JsonRequired] string RequestId,
    [property: JsonRequired] string Title, [property: JsonRequired] string Artist, [property: JsonRequired] string Album,
    [property: JsonRequired] long DurationMs, [property: JsonRequired] string FileName, [property: JsonRequired] string Codec,
    [property: JsonRequired] int Bitrate, [property: JsonRequired] long Bytes, [property: JsonRequired] string Sha256,
    [property: JsonRequired] DateTimeOffset SavedUtc, string? CoverUri = null, IReadOnlyList<ArtistReference>? Artists = null, string? AlbumId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? PrimaryArtistName = null);

internal sealed record OfflineRemovalResult(int Removed, int Preserved, int Failed, int Forgotten);

internal sealed partial class OfflineLibrary : IDisposable
{
    internal const int MaximumIndexBytes = 64 * 1024 * 1024;
    private readonly StoragePaths? _storage;
    private StoragePaths Storage => _storage ?? throw new MusicException(BrandPaths.OfflineUnavailableMessage);
    internal bool IsAvailable => _storage is not null;
    public string DirectoryPath => Storage.Directory;
    internal Action<string>? RemovalCheckpoint { get; set; }
    internal Action<string>? SaveCheckpoint { get; set; }
    internal Action<string>? TemporaryPublicationCheckpoint { get; set; }
    internal int? FileIdentityErrorForChecks { get; set; }
    private readonly AudioBuffer _downloader;
    // Coordinate multiple library instances in this process, including playlist snapshots.
    private static readonly ConcurrentDictionary<string, DirectoryState> DirectoryLocks = new(StringComparer.OrdinalIgnoreCase);
    private readonly DirectoryState _directoryState;
    // Shared by every library instance targeting this directory. Existing requests
    // finish when lowered; new requests wait until the new limit has room.
    public int ConcurrentDownloads
    {
        get => _directoryState.DownloadLimit;
        set => _directoryState.DownloadLimit = Math.Clamp(value, 1, 10);
    }
    private readonly SemaphoreSlim _writeLock;
    private string _indexPath => Storage.LibraryIndex;
    private string _playlistIndexPath => Storage.PlaylistIndex;
    private readonly object _cacheLock = new();
    private LibraryCache? _libraryCache;
    private PlaylistCache? _playlistCache;
    private static readonly JsonSerializerOptions LibraryJson = new()
    { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, MaxDepth = 48, WriteIndented = true };
    private static readonly JsonSerializerOptions PlaylistJson = new()
    { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, MaxDepth = 48, WriteIndented = true };
    internal OfflineLibrary(string? directory = null, AudioBuffer? downloader = null, BrandPaths? knownPaths = null)
    {
        var root = directory is null ? (knownPaths ?? BrandPaths.Current).AvailableMusicDirectory
            : Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        if (root is not null) _storage = new(root);
        _directoryState = root is not null ? DirectoryLocks.GetOrAdd(root, _ => new DirectoryState()) : new DirectoryState();
        _writeLock = _directoryState.Gate;
        _downloader = downloader ?? new AudioBuffer();
        if (IsAvailable) _ = ScheduleRecoveryIfNeeded();
    }
    private sealed class StoragePaths(string directory)
    {
        internal string Directory { get; } = directory;
        internal string LibraryIndex { get; } = Path.Combine(directory, "library.index.json");
        internal string PlaylistIndex { get; } = Path.Combine(directory, "playlists.index.json");
    }
    public IReadOnlyList<OfflineItem> Read() => IsAvailable ? LoadLibrary().Items : [];

    // No API requests and no media hashing: first load parses the index, subsequent
    // lookups validate only index timestamp/length and candidate media existence/length.
    public OfflineItem? Find(TrackRow track)
    {
        ArgumentNullException.ThrowIfNull(track);
        return IsAvailable ? Find(track, LoadLibrary()) : null;
    }
    public IReadOnlyList<TrackRow> Tracks()
        => Read().Select((item, i) =>
        {
            var valid = ValidFile(item);
            var path = valid ? ResolvePath(item.FileName) : null;
            var quality = QualityLabel(item);
            return new TrackRow
            {
            Number = i + 1, Id = item.Id, RequestId = item.RequestId, Title = item.Title, Artist = item.Artist, Artists = CopyArtists(item.Artists),
            PrimaryArtistName = item.PrimaryArtistName,
            Album = item.Album, AlbumId = item.AlbumId, DurationMs = item.DurationMs, CoverUri = item.CoverUri, Available = valid,
            IsOfflineLibraryRow = true, LocalPath = path, LocalQuality = quality, DownloadedPath = path, DownloadedQuality = valid ? quality : null
            };
        }).ToArray();

    public IReadOnlyList<OfflinePlaylist> GetPlaylists() => IsAvailable ? LoadPlaylists().Items : [];

    public async Task<OfflinePlaylist> RememberPlaylistAsync(PlaylistDetails playlist, AudioQuality quality, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(playlist);
        if (!Enum.IsDefined(quality)) throw new ArgumentOutOfRangeException(nameof(quality));
        var tracks = playlist.Tracks.Select(TrackMetadata).ToArray();
        var snapshot = new OfflinePlaylist(playlist.Owner, playlist.Kind, playlist.Title, playlist.Revision,
            quality, DateTimeOffset.UtcNow, Array.AsReadOnly(tracks));
        ValidatePlaylist(snapshot);
        await RecoverPendingAsync(cancellationToken).ConfigureAwait(false);
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await RecoverIfPendingUnderGateAsync().ConfigureAwait(false);
            var previous = LoadPlaylists(force: true, recovering: true); // Corrupt data is a blocker, never an empty fallback.
            var items = previous.Items.Where(p => p.Owner != snapshot.Owner || p.Kind != snapshot.Kind).ToList();
            items.Insert(0, snapshot);
            await WriteIndexAsync(_playlistIndexPath, new OfflinePlaylistIndex(1, items), PlaylistJson, previous.Stamp, cancellationToken).ConfigureAwait(false);
            lock (_cacheLock) _playlistCache = null;
            return snapshot;
        }
        finally { _writeLock.Release(); }
    }

    public OfflinePlaylistStatus GetPlaylistStatus(string owner, string kind, IReadOnlyList<TrackRow>? currentTracks = null)
        => InspectPlaylistStatus(owner, kind, currentTracks, CancellationToken.None);

    internal OfflinePlaylistStatus InspectPlaylistStatus(string owner, string kind, IReadOnlyList<TrackRow>? currentTracks, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var snapshot = GetPlaylists().FirstOrDefault(p => p.Owner == owner && p.Kind == kind);
        if (snapshot is null) return new(currentTracks?.Count ?? 0, 0, false, false, false);
        var tracks = currentTracks is null ? snapshot.Tracks : currentTracks.Select(TrackMetadata).ToArray();
        var changed = tracks.Count != snapshot.Tracks.Count
            || !tracks.Zip(snapshot.Tracks).All(pair => SameTrackMetadata(pair.First, pair.Second));
        var library = LoadLibrary();
        var downloaded = tracks.Count(t => { token.ThrowIfCancellationRequested(); return t.Available && Find(ToRow(t), library) is not null; });
        // Quality is retained as requested metadata. Completion uses actual valid files,
        // independently of the UI's currently selected quality or codec fallback.
        return new(tracks.Count, downloaded, tracks.Count > 0 && !changed && downloaded == tracks.Count, changed, true);
    }

    public IReadOnlyList<TrackRow> GetPlaylistTracks(string owner, string kind)
    {
        var snapshot = GetPlaylists().FirstOrDefault(p => p.Owner == owner && p.Kind == kind);
        if (snapshot is null) return [];
        var library = LoadLibrary();
        return snapshot.Tracks.Select((track, index) =>
        {
            var local = Find(ToRow(track), library);
            var path = local is null ? null : ResolvePath(local.FileName);
            var quality = local is null ? null : QualityLabel(local);
            return new TrackRow
            {
                Number = index + 1, Id = track.Id, RequestId = track.RequestId, Title = track.Title, Artist = track.Artist, Artists = CopyArtists(track.Artists),
                PrimaryArtistName = track.PrimaryArtistName,
                Album = track.Album, AlbumId = track.AlbumId, DurationMs = track.DurationMs, CoverUri = track.CoverUri, Available = local is not null,
                IsOfflineLibraryRow = true, LocalPath = path, LocalQuality = quality, DownloadedPath = path, DownloadedQuality = quality
            };
        }).ToArray();
    }

    public async Task<OfflineItem> SaveAsync(TrackRow track, AudioDescriptor audio, CancellationToken cancellationToken, IProgress<AudioProgress>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(track); ArgumentNullException.ThrowIfNull(audio);
        if (string.IsNullOrWhiteSpace(track.Id) || string.IsNullOrWhiteSpace(track.RequestId)
            || track.Title is null || track.Artist is null || !TrackRow.ValidPrimaryArtistName(track.PrimaryArtistName)
            || track.Album is null || track.DurationMs < 0
            || string.IsNullOrWhiteSpace(audio.Codec) || audio.Bitrate < 0)
            throw new MusicException("Некорректные данные трека. Офлайн-библиотека не изменена.");
        ValidateAlbumId(track.AlbumId);
        var artists = CopyArtists(track.Artists);
        var title = SafeName(track.Artist + " — " + track.Title);
        var id = SafeName(track.Id);
        var preferredName = $"{title} [{id}-{audio.Codec}-{audio.Bitrate}]{audio.Extension}";
        var preferredPath = ResolvePath(preferredName);
        await RecoverPendingAsync(cancellationToken).ConfigureAwait(false);
        // A key gate coalesces identical requests across instances without serializing
        // unrelated network downloads. Entries are removed when their last waiter leaves.
        var key = track.Id + "\0" + audio.Codec + "\0" + audio.Bitrate;
        using var saveGate = await _directoryState.EnterSaveAsync(key, cancellationToken).ConfigureAwait(false);
        bool IsSameVariant(OfflineItem item) => item.Id == track.Id && item.Codec == audio.Codec && item.Bitrate == audio.Bitrate;
        BufferedAudioFile? buffered = null;
        var holdsWriteLock = false;
        try
        {
            long generation;
            OfflineItem[] savedVariants;
            await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                EnsureSafeDirectory();
                await RecoverIfPendingUnderGateAsync().ConfigureAwait(false);
                generation = _directoryState.Generation;
                savedVariants = LoadLibrary(force: true, recovering: true).Items.Where(IsSameVariant).ToArray();
            }
            finally { _writeLock.Release(); }
            foreach (var existing in savedVariants)
            {
                if (!await HasExpectedHashAsync(existing, cancellationToken).ConfigureAwait(false)) continue;
                await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    await RecoverIfPendingUnderGateAsync().ConfigureAwait(false);
                    if (_directoryState.Generation != generation) throw DownloadInvalidated();
                    if (LoadLibrary(force: true, recovering: true).Items.Any(item => SameOfflineItem(item, existing)) && ValidFile(existing)) return existing;
                }
                finally { _writeLock.Release(); }
            }
            using (await _directoryState.EnterDownloadAsync(cancellationToken).ConfigureAwait(false))
                buffered = await _downloader.DownloadAsync(audio, cancellationToken, progress).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            using var parents = HoldStorageParents(_indexPath, create: true);
            // Stage under a unique name before taking the short index commit gate.
            var partial = ResolvePath(".ym-audio-" + Guid.NewGuid().ToString("N") + ".partial");
            using var stage = UninstallPathGuard.CreateStagedFile(partial, asynchronous: true);
            // Keep the partial file delete-pending until its durable receipt
            // exists. A process exit during a long copy then closes it safely.
            UninstallPathGuard.SetDeleteDisposition(stage, delete: true);
            await using var output = new FileStream(stage, FileAccess.ReadWrite, 65536, isAsync: true);
            var committed = false;
            FileStream? journal = null;
            string? unidentifiedDestination = null;
            try
            {
                string originalHash;
                await using (var input = File.OpenRead(buffered.Path))
                {
                    await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
                    input.Position = 0;
                    originalHash = Convert.ToHexString(await SHA256.HashDataAsync(input, cancellationToken).ConfigureAwait(false)).ToLowerInvariant();
                }
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                output.Flush(flushToDisk: true);
                output.Position = 0;
                var persistedHash = Convert.ToHexString(await SHA256.HashDataAsync(output, cancellationToken).ConfigureAwait(false)).ToLowerInvariant();
                if (originalHash != persistedHash) throw new MusicException("Не совпала контрольная сумма сохранённого аудио. Файл не добавлен в библиотеку.");
                await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
                holdsWriteLock = true;
                SetStorageMutationActive(true);
                await RecoverIfPendingUnderGateAsync().ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                if (_directoryState.Generation != generation) throw DownloadInvalidated();
                var latest = LoadLibrary(force: true); // Never overwrite corrupt or externally changed data.
                var currentVariants = latest.Items.Where(IsSameVariant).ToArray();
                if (currentVariants.Length != savedVariants.Length
                    || !currentVariants.Zip(savedVariants).All(pair => SameOfflineItem(pair.First, pair.Second))) throw IndexChanged();
                var filename = preferredName;
                var destination = preferredPath;
                if (UninstallPathGuard.EntryExists(destination) || latest.Items.Any(i => i.FileName.Equals(filename, StringComparison.OrdinalIgnoreCase)))
                { filename = $"{title} [{id}-{Guid.NewGuid():N}]{audio.Extension}"; destination = ResolvePath(filename); }
                var item = new OfflineItem(track.Id, track.RequestId, track.Title, track.Artist, track.Album, track.DurationMs, filename,
                    audio.Codec, audio.Bitrate, output.Length, persistedHash, DateTimeOffset.UtcNow, track.CoverUri, artists, track.AlbumId,
                    track.PrimaryArtistNameToPersist);
                SaveCheckpoint?.Invoke("save-before-receipt");
                var identity = GetStorageFileIdentity(stage);
                journal = CreateSaveJournal(partial, item, identity, latest.Stamp);
                if (identity is null) unidentifiedDestination = item.FileName;
                SaveCheckpoint?.Invoke("save-before-audio-publish");
                ManagedCacheCleanup.RenameByHandle(stage, destination, releaseDeleteDisposition: true);
                SaveCheckpoint?.Invoke("save-after-audio-publish");
                // Replace prior metadata for this quality only. A missing or
                // modified old file is never a target of this save operation.
                var items = latest.Items.Where(i => !IsSameVariant(i)).ToList();
                items.Insert(0, item);
                await WriteIndexAsync(_indexPath, items, LibraryJson, latest.Stamp, cancellationToken).ConfigureAwait(false);
                committed = true;
                lock (_cacheLock) _libraryCache = null;
                SaveCheckpoint?.Invoke("save-after-index-commit");
                UninstallPathGuard.DeleteByHandle(journal.SafeFileHandle);
                return item;
            }
            finally
            {
                journal?.Dispose();
                if (!committed && journal is not null)
                {
                    try
                    {
                        // A live rollback can still use its exact handle on a
                        // filesystem that cannot provide a persistent file ID.
                        if (unidentifiedDestination is not null && !LoadLibrary(force: true, recovering: true).Items
                            .Any(row => row.FileName.Equals(unidentifiedDestination, StringComparison.OrdinalIgnoreCase)))
                            UninstallPathGuard.DeleteByHandle(stage);
                        await output.DisposeAsync().ConfigureAwait(false);
                        await RecoverSaveUnderGateAsync().ConfigureAwait(false);
                    }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException or MusicException or Win32Exception or JsonException or CryptographicException)
                    { /* A failed rollback leaves the durable receipt for the next recovery. */ }
                }
            }
        }
        catch (Win32Exception error) { throw new IOException("Не удалось сохранить аудиофайл.", error); }
        finally
        {
            if (holdsWriteLock)
            {
                SetStorageMutationActive(false);
                _writeLock.Release();
                _ = ScheduleRecoveryIfNeeded();
            }
            buffered?.Dispose();
        }
    }

    public Task<OfflineRemovalResult> RemoveTrackAsync(string trackId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(trackId);
        return Task.Run(() => RemoveAsync(trackId, cancellationToken, null), cancellationToken);
    }
    public Task<OfflineRemovalResult> ClearAsync(CancellationToken cancellationToken) => ClearAsync(cancellationToken, null);
    public Task<OfflineRemovalResult> ClearAsync(CancellationToken cancellationToken, IProgress<OfflineCleanupProgress>? progress)
        => Task.Run(() => RemoveAsync(null, cancellationToken, progress), cancellationToken);

    private async Task<OfflineRemovalResult> RemoveAsync(string? trackId, CancellationToken cancellationToken, IProgress<OfflineCleanupProgress>? progress)
    {
        await RecoverPendingAsync(cancellationToken).ConfigureAwait(false);
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        SetStorageMutationActive(true);
        var staged = new ConcurrentBag<StagedRemoval>();
        var removed = 0; var preserved = 0; var failed = 0;
        var completed = false;
        RecoveryJournalLease? journal = null;
        var retained = new List<OfflineItem>();
        UninstallPathGuard.AncestorLease? ancestors = null;
        try
        {
            EnsureSafeDirectory();
            // Keep parents pinned from index preflight through rollback.
            _ = UninstallPathGuard.Canonical(DirectoryPath); // A drive root is never a cleanup target.
            ancestors = HoldStorageParents(_indexPath, create: true);
            await RecoverIfPendingUnderGateAsync().ConfigureAwait(false);
            var library = LoadLibrary(force: true);
            // Both indexes are preflighted before a single file is moved.
            var playlists = LoadPlaylists(force: true);
            var selected = library.Items.Where(i => trackId is null || i.Id == trackId).ToArray();
            var remaining = library.Items.Where(i => trackId is not null && i.Id != trackId).ToList();
            var candidates = selected.DistinctBy(i => i.FileName, StringComparer.OrdinalIgnoreCase).ToArray();
            var referenced = remaining.Select(i => i.FileName).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var verified = 0; long verifiedBytes = 0;
            var totalBytes = candidates.Sum(i => i.Bytes);
            var resultGate = new object();
            void Report(string phase)
                => progress?.Report(new(verified, candidates.Length, removed, preserved, failed, verifiedBytes, totalBytes, phase));
            Report("verify");
            _directoryState.Generation++; // An already running Save cannot republish after removal.
            // At most two simultaneous media reads. A single seek-limited disk
            // may not benefit from concurrency; avoiding a second full read is
            // the main saving on both HDD and SSD.
            await Parallel.ForEachAsync(candidates, new ParallelOptions { MaxDegreeOfParallelism = 2, CancellationToken = cancellationToken }, async (item, token) =>
            {
                token.ThrowIfCancellationRequested();
                var path = ResolvePath(item.FileName);
                FileStream? file = null;
                try
                {
                    if (!File.Exists(path) || referenced.Contains(item.FileName) || !ValidFile(item))
                    { lock (resultGate) preserved++; return; }
                    if ((File.GetAttributes(path) & FileAttributes.ReadOnly) != 0)
                    { lock (resultGate) { failed++; retained.Add(item); } return; }
                    var quarantine = ResolvePath(".ym-delete-" + Guid.NewGuid().ToString("N") + ".pending");
                    var recovery = ResolvePath(".ym-recovered-" + Guid.NewGuid().ToString("N") + Path.GetExtension(path));
                    // READ|DELETE, FILE_SHARE_READ, OPEN_REPARSE_POINT. The
                    // native guard validates the opened object itself. Keep
                    // this exact handle alive through commit and deletion so
                    // nobody can mutate or replace the verified bytes.
                    var handle = UninstallPathGuard.OpenOwnedFile(path, read: true);
                    try { file = new FileStream(handle, FileAccess.Read, bufferSize: 1, isAsync: false); }
                    catch { handle.Dispose(); throw; }
                    if (file.Length != item.Bytes
                        || !await RemovalHashMatchesAsync(file, item.Sha256, token).ConfigureAwait(false))
                    { lock (resultGate) preserved++; return; }
                    token.ThrowIfCancellationRequested();
                    staged.Add(new(item, path, quarantine, recovery, file, GetStorageFileIdentity(handle)));
                    file = null; // Ownership transfers to staged until final commit/rollback.
                    lock (resultGate) verifiedBytes += item.Bytes;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception)
                { lock (resultGate) { failed++; retained.Add(item); } }
                finally
                {
                    file?.Dispose();
                    lock (resultGate) { verified++; Report("verify"); }
                }
            }).ConfigureAwait(false);
            remaining.AddRange(retained);
            cancellationToken.ThrowIfCancellationRequested();
            // The complete immutable mapping is durable before the first rename.
            // Verification retains the exact opened handles without moving files.
            journal = CreateRecoveryJournal(staged.ToArray(), library, playlists, remaining, trackId is null);
            foreach (var entry in staged)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ManagedCacheCleanup.RenameByHandle(entry.File.SafeFileHandle, entry.Quarantine);
                entry.Moved = true;
                RemovalCheckpoint?.Invoke("after-stage");
            }
            Report("commit");
            // Clear resets snapshots too; deleting one track retains snapshot metadata
            // and its completion is recomputed from actual remaining managed files.
            IndexStamp? snapshotWritten = null;
            try
            {
                if (trackId is null)
                {
                    snapshotWritten = await WriteIndexAsync(_playlistIndexPath, new OfflinePlaylistIndex(1, []), PlaylistJson, playlists.Stamp, cancellationToken).ConfigureAwait(false);
                    RemovalCheckpoint?.Invoke("after-playlist-commit");
                }
                await WriteIndexAsync(_indexPath, remaining, LibraryJson, library.Stamp, cancellationToken).ConfigureAwait(false);
                RemovalCheckpoint?.Invoke("before-removal-commit-marker");
                CommitRecoveryJournal(journal);
            }
            catch
            {
                if (snapshotWritten is not null)
                {
                    // Roll back our snapshot change only if no other writer replaced it.
                    await WriteIndexAsync(_playlistIndexPath, new OfflinePlaylistIndex(1, playlists.Items), PlaylistJson, snapshotWritten, CancellationToken.None).ConfigureAwait(false);
                }
                throw;
            }
            RemovalCheckpoint?.Invoke("after-library-commit");
            foreach (var entry in staged)
            {
                try
                {
                    EnsureSafeDirectory();
                    // Index commit is the cancellation boundary. After it, the
                    // small delete/restore phase completes with no audio reads.
                    UninstallPathGuard.DeleteByHandle(entry.File.SafeFileHandle);
                    entry.Deleted = true;
                    entry.File.Dispose(); removed++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception)
                {
                    var restoredPath = RestoreStaged(entry); failed++;
                    // The same protected handle still proves the digest.
                    retained.Add(entry.Item with { FileName = Path.GetFileName(restoredPath) });
                }
                finally { Report("delete"); }
            }
            // Failed managed files remain discoverable for a later retry. The
            // second commit merges fresh external/unrelated entries instead of
            // hiding a restored audio file behind an empty index.
            if (retained.Count > 0)
            {
                var latest = LoadLibrary(force: true);
                var repaired = latest.Items.ToList();
                foreach (var item in retained)
                    if (!repaired.Any(i => i.FileName.Equals(item.FileName, StringComparison.OrdinalIgnoreCase))) repaired.Add(item);
                if (repaired.Count != latest.Items.Count)
                    await WriteIndexAsync(_indexPath, repaired, LibraryJson, latest.Stamp, CancellationToken.None).ConfigureAwait(false);
            }
            DeleteRecoveryJournal(journal);
            completed = true;
            Report("complete");
            return new(removed, preserved, failed, selected.Length - retained.Count);
        }
        finally
        {
            try
            {
                if (!completed)
                {
                    var restored = new List<RecoveredAudio>();
                    foreach (var entry in staged)
                        if (!entry.Deleted) restored.Add(new(entry.Item, Path.GetFileName(entry.Recovery), RestoreStaged(entry)));
                    // A collision may change the pathname even before commit.
                    // Repair that exact mapping rather than indexing a foreign replacement.
                    if (journal is not null)
                    {
                        await RestoreInterruptedPlaylistsAsync(journal).ConfigureAwait(false);
                        if (restored.Count > 0 || !journal.Committed && journal.Data.Unstaged is { Count: > 0 })
                            await RepairRecoveredIndexAsync(restored, journal).ConfigureAwait(false);
                    }
                    if (journal is not null) DeleteRecoveryJournal(journal);
                }
            }
            finally
            {
                foreach (var entry in staged) entry.File.Dispose();
                journal?.Dispose();
                ancestors?.Dispose();
                lock (_cacheLock) { _libraryCache = null; _playlistCache = null; }
                SetStorageMutationActive(false);
                _writeLock.Release();
                // Only failures which could not repair their mapping leave a journal.
                // Its fault is observed; ordinary reads fail promptly until recovery succeeds.
                _ = ScheduleRecoveryIfNeeded();
            }
        }
    }

    private static async Task<bool> RemovalHashMatchesAsync(FileStream file, string expected, CancellationToken token)
    {
        // Staged streams retain only their protected handle. A read buffer belongs
        // to one of the two active workers, never to every retained media file.
        var buffer = ArrayPool<byte>.Shared.Rent(256 * 1024);
        try
        {
            using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            int read;
            while ((read = await file.ReadAsync(buffer.AsMemory(0, 256 * 1024), token).ConfigureAwait(false)) != 0)
                digest.AppendData(buffer, 0, read);
            return Convert.ToHexString(digest.GetHashAndReset()).Equals(expected, StringComparison.OrdinalIgnoreCase);
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }

    private async Task<bool> HasExpectedHashAsync(OfflineItem item, CancellationToken cancellationToken)
    {
        var path = ResolvePath(item.FileName);
        try
        {
            using var parents = HoldStorageParents(path);
            if (!parents.Complete || !ValidFile(item)) return false;
            using var handle = UninstallPathGuard.OpenOwnedFile(path, read: true, deleteAccess: false);
            using var file = new FileStream(handle, FileAccess.Read, 65536, isAsync: false);
            return file.Length == item.Bytes && Convert.ToHexString(await SHA256.HashDataAsync(file, cancellationToken).ConfigureAwait(false)).ToLowerInvariant() == item.Sha256;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception) { return false; }
    }
    private string RestoreStaged(StagedRemoval entry)
    {
        if (entry.RestoredPath is { } restored) return restored;
        if (!entry.Moved) return entry.Original;
        try
        {
            var recovered = UninstallPathGuard.EntryExists(entry.Original) ? entry.Recovery : entry.Original;
            ManagedCacheCleanup.RenameByHandle(entry.File.SafeFileHandle, recovered); return entry.RestoredPath = recovered;
        }
        catch (Win32Exception error)
        {
            // Without a successful rename the journal must survive. A retry
            // still needs its authenticated mapping for the quarantine file.
            throw new IOException("Не удалось вернуть файл из карантина. Файл и журнал очистки сохранены.", error);
        }
    }
    private sealed record StagedRemoval(OfflineItem Item, string Original, string Quarantine, string Recovery, FileStream File, StorageFileIdentity? Identity)
    {
        internal bool Moved { get; set; }
        internal bool Deleted { get; set; }
        internal string? RestoredPath { get; set; }
    }
    private void EnsureSafeDirectory()
    {
        for (var part = new DirectoryInfo(DirectoryPath); part is not null; part = part.Parent)
            if (part.Exists && (part.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new MusicException("Папка офлайн-музыки содержит ссылку или перенаправление. Операция остановлена, файлы сохранены.");
    }
    private static UninstallPathGuard.AncestorLease HoldStorageParents(string path, bool create = false)
    {
        try { return UninstallPathGuard.HoldAncestors(path, create); }
        catch (Win32Exception error) { throw new IOException("Не удалось открыть папку офлайн-библиотеки.", error); }
    }
    private static MusicException DownloadInvalidated() => new("Состав офлайн-библиотеки изменился во время загрузки. Загрузка остановлена; повторите её при необходимости.");
    internal string ResolvePath(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName) || fileName != Path.GetFileName(fileName) || fileName.Contains(':')
            || fileName.Any(c => char.IsControl(c) || Path.GetInvalidFileNameChars().Contains(c))
            || fileName.EndsWith('.') || fileName.EndsWith(' ')
            || fileName.Equals("library.index.json", StringComparison.OrdinalIgnoreCase) || fileName.Equals("playlists.index.json", StringComparison.OrdinalIgnoreCase))
            throw new MusicException("Некорректный путь в индексе офлайн-библиотеки.");
        var result = Path.GetFullPath(Path.Combine(DirectoryPath, fileName));
        if (!result.StartsWith(DirectoryPath.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new MusicException("Путь вышел за пределы папки музыки.");
        return result;
    }

    private OfflineItem? Find(TrackRow track, LibraryCache cache)
    {
        if (!string.IsNullOrEmpty(track.RequestId) && cache.ByRequest.TryGetValue(track.RequestId, out var exact))
        {
            var match = exact.FirstOrDefault(ValidFile);
            if (match is not null) return match;
        }
        // Same exact server track ID is valid across album contexts. Never compare names.
        return !string.IsNullOrEmpty(track.Id) && cache.ById.TryGetValue(track.Id, out var variants) ? variants.FirstOrDefault(ValidFile) : null;
    }

    private bool ValidFile(OfflineItem item)
    {
        var path = ResolvePath(item.FileName);
        try
        {
            var file = new FileInfo(path);
            return file.Exists && file.Length == item.Bytes && item.Bytes > 0 && (file.Attributes & FileAttributes.ReparsePoint) == 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    private LibraryCache LoadLibrary(bool force = false, bool recovering = false)
    {
        if (!recovering) EnsureRecoveryReadReady();
        lock (_cacheLock)
        {
            EnsureSafeDirectory();
            var stamp = Stamp(_indexPath);
            if (!force && _libraryCache is { } cached && cached.Stamp == stamp) return cached;
            try
            {
                using var parents = HoldStorageParents(_indexPath);
                stamp = parents.Complete ? Stamp(_indexPath) : new(false, 0, 0);
                var items = stamp.Exists ? JsonSerializer.Deserialize<List<OfflineItem>>(ReadIndexText(_indexPath), LibraryJson) ?? throw new JsonException() : [];
                foreach (var item in items)
                {
                    if (item is null || string.IsNullOrWhiteSpace(item.Id) || string.IsNullOrWhiteSpace(item.RequestId)
                        || item.Title is null || item.Artist is null || !TrackRow.ValidPrimaryArtistName(item.PrimaryArtistName)
                        || item.Album is null || string.IsNullOrWhiteSpace(item.Codec)
                        || item.DurationMs < 0 || item.Bitrate < 0 || item.Bytes <= 0 || item.SavedUtc == default
                        || item.Sha256 is null || item.Sha256.Length != 64 || !item.Sha256.All(Uri.IsHexDigit)) throw new JsonException();
                    ValidateArtists(item.Artists);
                    ValidateAlbumId(item.AlbumId);
                    _ = ResolvePath(item.FileName);
                }
                if (Stamp(_indexPath) != stamp) throw IndexChanged();
                var ordered = items.OrderByDescending(i => i.Codec.StartsWith("flac", StringComparison.OrdinalIgnoreCase))
                    .ThenByDescending(i => i.Codec.StartsWith("flac", StringComparison.OrdinalIgnoreCase) ? 0 : i.Bitrate)
                    .ThenByDescending(i => i.SavedUtc).ToArray();
                _libraryCache = new(stamp, Array.AsReadOnly(items.ToArray()),
                    ordered.GroupBy(i => i.RequestId, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.Ordinal),
                    ordered.GroupBy(i => i.Id, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.Ordinal));
                return _libraryCache;
            }
            catch (JsonException) { throw new MusicException("Не удалось прочитать индекс офлайн-библиотеки. Аудиофайлы сохранены; index не перезаписывался."); }
        }
    }

    private PlaylistCache LoadPlaylists(bool force = false, bool recovering = false)
    {
        if (!recovering) EnsureRecoveryReadReady();
        lock (_cacheLock)
        {
            EnsureSafeDirectory();
            var stamp = Stamp(_playlistIndexPath);
            if (!force && _playlistCache is { } cached && cached.Stamp == stamp) return cached;
            try
            {
                using var parents = HoldStorageParents(_playlistIndexPath);
                stamp = parents.Complete ? Stamp(_playlistIndexPath) : new(false, 0, 0);
                var data = stamp.Exists ? JsonSerializer.Deserialize<OfflinePlaylistIndex>(ReadIndexText(_playlistIndexPath), PlaylistJson) ?? throw new JsonException() : new(1, []);
                if (data.SchemaVersion != 1 || data.Playlists is null) throw new JsonException();
                foreach (var playlist in data.Playlists) ValidatePlaylist(playlist);
                if (data.Playlists.GroupBy(p => (p.Owner, p.Kind)).Any(g => g.Count() > 1)) throw new JsonException();
                if (Stamp(_playlistIndexPath) != stamp) throw IndexChanged();
                _playlistCache = new(stamp, Array.AsReadOnly(data.Playlists.Select(p => p with { Tracks = Array.AsReadOnly(p.Tracks.ToArray()) }).ToArray()));
                return _playlistCache;
            }
            catch (JsonException) { throw new MusicException("Не удалось прочитать индекс офлайн-плейлистов. Аудиофайлы и повреждённый index сохранены; перезапись не выполнялась."); }
        }
    }

    private static string ReadIndexText(string path)
    {
        try
        {
            using var handle = UninstallPathGuard.OpenOwnedFile(path, read: true, deleteAccess: false);
            using var stream = new FileStream(handle, FileAccess.Read);
            if (stream.Length > MaximumIndexBytes) throw new IOException("Индекс офлайн-библиотеки слишком большой. Файлы сохранены.");
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
        catch (Win32Exception error) { throw new IOException("Не удалось прочитать индекс офлайн-библиотеки.", error); }
    }

    private async Task<IndexStamp> WriteIndexAsync<T>(string path, T data, JsonSerializerOptions options, IndexStamp expected,
        CancellationToken cancellationToken, Action? beforePublication = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var serialized = JsonSerializer.Serialize(data, options);
        if (expected.Length > MaximumIndexBytes || System.Text.Encoding.UTF8.GetByteCount(serialized) > MaximumIndexBytes)
            throw new IOException("Индекс офлайн-библиотеки слишком большой. Файлы сохранены.");
        using var parents = HoldStorageParents(path, create: true);
        var temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using var handle = UninstallPathGuard.CreateStagedFile(temp, asynchronous: true, writeThrough: true);
            UninstallPathGuard.SetDeleteDisposition(handle, delete: true);
            await using var output = new FileStream(handle, FileAccess.ReadWrite, 4096, isAsync: true);
            var published = false;
            try
            {
                var bytes = System.Text.Encoding.UTF8.GetBytes(serialized);
                await output.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                output.Flush(flushToDisk: true);
                output.Position = 0;
                using var readback = new StreamReader(output, leaveOpen: true);
                if (await readback.ReadToEndAsync(cancellationToken).ConfigureAwait(false) != serialized) throw new MusicException("Не прошла проверка сохранённого индекса офлайн-библиотеки.");
                cancellationToken.ThrowIfCancellationRequested();
                if (Stamp(path) != expected) throw IndexChanged();
                File.SetLastWriteTimeUtc(handle, DateTime.UtcNow);
                var writtenStamp = new IndexStamp(true, output.Length, File.GetLastWriteTimeUtc(handle).Ticks);
                beforePublication?.Invoke();
                TemporaryPublicationCheckpoint?.Invoke("index-before-publish");
                ManagedCacheCleanup.RenameByHandle(handle, path, replace: expected.Exists, releaseDeleteDisposition: true);
                published = true;
                return writtenStamp;
            }
            finally { if (!published) UninstallPathGuard.DiscardStagedFile(handle); }
        }
        catch (Win32Exception error) { throw new IOException("Не удалось сохранить индекс офлайн-библиотеки.", error); }
    }

    private static OfflinePlaylistTrack TrackMetadata(TrackRow track)
    {
        ArgumentNullException.ThrowIfNull(track);
        return new(track.Id, track.RequestId, track.Title, track.Artist, track.Album, track.DurationMs, track.Available,
            track.CoverUri, CopyArtists(track.Artists), track.AlbumId, track.PrimaryArtistNameToPersist);
    }
    private static TrackRow ToRow(OfflinePlaylistTrack track) => new()
    {
        Id = track.Id, RequestId = track.RequestId, Title = track.Title, Artist = track.Artist, Artists = CopyArtists(track.Artists),
        PrimaryArtistName = track.PrimaryArtistName, Album = track.Album, AlbumId = track.AlbumId, DurationMs = track.DurationMs,
        Available = track.Available, CoverUri = track.CoverUri
    };
    private static IReadOnlyList<ArtistReference> CopyArtists(IReadOnlyList<ArtistReference>? artists)
    {
        ValidateArtists(artists);
        return artists is { Count: > 0 } ? Array.AsReadOnly(artists.ToArray()) : [];
    }
    private static void ValidateArtists(IReadOnlyList<ArtistReference>? artists)
    {
        if (artists is null) return; // Indexes written before artist navigation have no field.
        foreach (var artist in artists)
            if (artist is null || string.IsNullOrWhiteSpace(artist.Id) || artist.Id.Length > 512 || artist.Id.Any(char.IsControl)
                || string.IsNullOrWhiteSpace(artist.Name)) throw new JsonException();
    }
    private static bool SameTrackMetadata(OfflinePlaylistTrack left, OfflinePlaylistTrack right)
        // Adding navigation metadata to a legacy snapshot does not mean that
        // the playlist's content changed or its downloaded files are stale.
        => left with { Artists = null, AlbumId = null, PrimaryArtistName = null }
            == right with { Artists = null, AlbumId = null, PrimaryArtistName = null };
    private static bool SameOfflineItem(OfflineItem left, OfflineItem right)
        // Persisted identity still includes AlbumId: an external metadata write
        // must invalidate a pending deduplication readback just like other fields.
        => left with { Artists = null } == right with { Artists = null }
            && (left.Artists ?? []).SequenceEqual(right.Artists ?? []);
    private static void ValidateAlbumId(string? albumId)
    {
        if (albumId is not null && (string.IsNullOrWhiteSpace(albumId) || albumId.Length > 512 || albumId.Any(char.IsControl))) throw new JsonException();
    }
    private static void ValidatePlaylist(OfflinePlaylist playlist)
    {
        if (playlist is null || string.IsNullOrWhiteSpace(playlist.Owner) || string.IsNullOrWhiteSpace(playlist.Kind)
            || playlist.Title is null || playlist.Revision < 0 || !Enum.IsDefined(playlist.RequestedAudioQuality)
            || playlist.UpdatedUtc == default || playlist.UpdatedUtc.Offset != TimeSpan.Zero || playlist.Tracks is null) throw new JsonException();
        foreach (var track in playlist.Tracks)
        {
            if (track is null || string.IsNullOrWhiteSpace(track.Id) || string.IsNullOrWhiteSpace(track.RequestId)
                || track.Title is null || track.Artist is null || !TrackRow.ValidPrimaryArtistName(track.PrimaryArtistName)
                || track.Album is null || track.DurationMs < 0) throw new JsonException();
            ValidateArtists(track.Artists);
            ValidateAlbumId(track.AlbumId);
        }
    }
    private static string QualityLabel(OfflineItem item)
        => item.Codec.StartsWith("flac", StringComparison.OrdinalIgnoreCase) ? "Lossless · FLAC" : item.Codec.ToUpperInvariant() + (item.Bitrate > 0 ? $" · {item.Bitrate} кбит/с" : "");
    private static IndexStamp Stamp(string path)
    {
        try
        {
            var file = new FileInfo(path);
            var attributes = file.Exists ? file.Attributes : File.GetAttributes(path);
            if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
                throw new MusicException("Индекс офлайн-библиотеки является ссылкой или папкой. Операция остановлена.");
            return new(true, file.Length, file.LastWriteTimeUtc.Ticks);
        }
        catch (FileNotFoundException) { return new(false, 0, 0); }
        catch (DirectoryNotFoundException) { return new(false, 0, 0); }
    }
    private static MusicException IndexChanged() => new("Индекс офлайн-библиотеки изменился во время записи. Ничего не перезаписывалось; перечитайте библиотеку перед повтором.");
    private sealed class DirectoryState
    {
        public SemaphoreSlim Gate { get; } = new(1);
        internal object RecoverySync { get; } = new();
        internal Task? RecoveryTask;
        internal bool StorageMutationActive;
        public long Generation;
        private readonly object _gate = new();
        private int _downloadLimit = 3, _activeDownloads;
        private TaskCompletionSource _downloadChanged = NewDownloadSignal();
        public int DownloadLimit
        {
            get { lock (_gate) return _downloadLimit; }
            set
            {
                lock (_gate)
                {
                    if (_downloadLimit == value) return;
                    _downloadLimit = value;
                    SignalDownloadChange();
                }
            }
        }
        public async Task<IDisposable> EnterDownloadAsync(CancellationToken cancellationToken)
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Task changed;
                lock (_gate)
                {
                    if (_activeDownloads < _downloadLimit)
                    {
                        _activeDownloads++;
                        return new SaveLease(ReleaseDownload);
                    }
                    changed = _downloadChanged.Task;
                }
                await changed.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        private void ReleaseDownload()
        {
            lock (_gate) { _activeDownloads--; SignalDownloadChange(); }
        }
        private static TaskCompletionSource NewDownloadSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
        private void SignalDownloadChange()
        {
            var previous = _downloadChanged;
            _downloadChanged = NewDownloadSignal();
            previous.TrySetResult();
        }
        private readonly Dictionary<string, SaveEntry> _saves = new(StringComparer.Ordinal);
        public async Task<IDisposable> EnterSaveAsync(string key, CancellationToken cancellationToken)
        {
            SaveEntry entry;
            lock (_gate)
            {
                if (!_saves.TryGetValue(key, out entry!)) _saves[key] = entry = new();
                entry.References++;
            }
            try { await entry.Semaphore.WaitAsync(cancellationToken).ConfigureAwait(false); }
            catch { Drop(key, entry, release: false); throw; }
            return new SaveLease(() => Drop(key, entry, release: true));
        }
        private void Drop(string key, SaveEntry entry, bool release)
        {
            lock (_gate)
            {
                if (release) entry.Semaphore.Release();
                if (--entry.References == 0) { _saves.Remove(key); entry.Semaphore.Dispose(); }
            }
        }
        private sealed class SaveEntry { public readonly SemaphoreSlim Semaphore = new(1); public int References; }
        private sealed class SaveLease(Action release) : IDisposable
        {
            private Action? _release = release;
            public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
        }
    }
    private sealed record IndexStamp(bool Exists, long Length, long LastWriteTicks);
    private sealed record LibraryCache(IndexStamp Stamp, IReadOnlyList<OfflineItem> Items,
        Dictionary<string, OfflineItem[]> ByRequest, Dictionary<string, OfflineItem[]> ById);
    private sealed record PlaylistCache(IndexStamp Stamp, IReadOnlyList<OfflinePlaylist> Items);
    private static string SafeName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var result = new string(value.Where(c => !invalid.Contains(c) && !char.IsControl(c)).ToArray()).Trim().TrimEnd('.');
        if (result.Length > 90) result = result[..90].TrimEnd();
        return result.Length > 0 ? result : "Трек";
    }
    public void Dispose() => _downloader.Dispose();
}
