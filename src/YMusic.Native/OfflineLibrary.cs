using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Collections.Concurrent;
using System.Buffers;
using System.ComponentModel;
using Microsoft.Win32.SafeHandles;

namespace YMusic.Native;

internal sealed record OfflineItem([property: JsonRequired] string Id, [property: JsonRequired] string RequestId,
    [property: JsonRequired] string Title, [property: JsonRequired] string Artist, [property: JsonRequired] string Album,
    [property: JsonRequired] long DurationMs, [property: JsonRequired] string FileName, [property: JsonRequired] string Codec,
    [property: JsonRequired] int Bitrate, [property: JsonRequired] long Bytes, [property: JsonRequired] string Sha256,
    [property: JsonRequired] DateTimeOffset SavedUtc, string? CoverUri = null, IReadOnlyList<ArtistReference>? Artists = null, string? AlbumId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? PrimaryArtistName = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? ConfirmedBitrate = null)
{
    // Bitrate is the service's rendition identifier, including for legacy rows.
    internal int QualityBitrate => ConfirmedBitrate.GetValueOrDefault();
}

internal sealed record OfflineRemovalResult(int Removed, int Preserved, int Failed, int Forgotten)
{
    internal bool HasRetainedIndexEntries => Failed > 0;
}

internal sealed class OfflinePlaylistIndexFormatException()
    : MusicException("Не удалось прочитать индекс офлайн-плейлистов. Аудиофайлы и повреждённый индекс сохранены.");

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
    internal bool RemovalAudioIdentityUnavailableForChecks { get; set; }
    internal Action<string>? BeforeRemovalAttributesForChecks { get; set; }
    internal Action<string>? BeforeVerifySavedAudioForChecks { get; set; }
    private readonly AudioBuffer _downloader;
    private readonly Action? _beforeRecoveryProbeForChecks;
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
    { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, AllowDuplicateProperties = false, MaxDepth = 48, WriteIndented = true };
    private static readonly JsonSerializerOptions PlaylistJson = new()
    { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, AllowDuplicateProperties = false, MaxDepth = 48, WriteIndented = true };
    internal OfflineLibrary(string? directory = null, AudioBuffer? downloader = null, BrandPaths? knownPaths = null,
        Action? beforeRecoveryProbeForChecks = null)
    {
        var root = directory is null ? (knownPaths ?? BrandPaths.Current).AvailableMusicDirectory
            : Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        if (root is not null) _storage = new(root);
        _directoryState = root is not null ? DirectoryLocks.GetOrAdd(root, _ => new DirectoryState()) : new DirectoryState();
        _writeLock = _directoryState.Gate;
        _downloader = downloader ?? new AudioBuffer();
        _beforeRecoveryProbeForChecks = beforeRecoveryProbeForChecks;
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

    // A single reviewed metadata snapshot for a batch forecast. Each
    // decision to reuse the actual audio still needs FindVerifiedAsync.
    internal Func<TrackRow, OfflineItem?> CaptureTrackLookup()
    {
        if (!IsAvailable) return static _ => null;
        var snapshot = LoadLibrary();
        return track => Find(track, snapshot);
    }

    internal async Task<OfflineItem?> FindVerifiedAsync(TrackRow track, CancellationToken cancellationToken,
        Func<OfflineItem, bool>? accept = null, bool failIfUnavailable = false)
    {
        ArgumentNullException.ThrowIfNull(track);
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsAvailable) return null;
        var snapshot = LoadLibrary();
        IOException? unavailable = null;
        foreach (var candidate in VerifiedCandidates(track, snapshot))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (accept is not null && !accept(candidate)) continue;
            VerifiedSavedFile? verifiedFile;
            try { verifiedFile = await HoldExpectedHashAsync(candidate, cancellationToken, failIfUnavailable).ConfigureAwait(false); }
            catch (IOException error) when (failIfUnavailable) { unavailable ??= error; continue; }
            using var verified = verifiedFile;
            if (verified is null) continue;
            cancellationToken.ThrowIfCancellationRequested();
            if (LoadLibrary().ById.TryGetValue(candidate.Id, out var current)
                && current.Any(item => SameOfflineItem(item, candidate))) return candidate;
        }
        if (unavailable is not null) throw unavailable;
        return null;
    }

    private static IEnumerable<OfflineItem> VerifiedCandidates(TrackRow track, LibraryCache snapshot)
    {
        var exact = string.IsNullOrEmpty(track.RequestId) ? null : snapshot.ByRequest.GetValueOrDefault(track.RequestId);
        var variants = string.IsNullOrEmpty(track.Id) ? null : snapshot.ById.GetValueOrDefault(track.Id);
        return (exact ?? []).Concat((variants ?? []).Where(item => item.RequestId != track.RequestId || exact is null));
    }

    internal const int DownloadVerificationBatchSize = 128;
    internal sealed record DownloadCacheRequest(TrackRow Track, Func<OfflineItem, bool>? Accept = null);
    internal readonly record struct DownloadCacheResult(OfflineItem? Item, IOException? Unavailable,
        IReadOnlyList<OfflineItem>? VerifiedCopies = null, IReadOnlyList<LocalAudioSource>? RefusedCopies = null);
    // Only a batch check can describe every variant. A failed guarded open
    // records a refusal for its exact source without classifying other copies.
    internal sealed record OfflineCopyCheck(TrackRow Track, IReadOnlyList<OfflineItem>? VerifiedCopies, bool RetryOnNavigation = false,
        IReadOnlyList<LocalAudioSource>? RefusedCopies = null);

    internal sealed class OfflineCopyChecks : System.Collections.ObjectModel.ReadOnlyDictionary<string, OfflineCopyCheck>
    {
        private readonly HashSet<(string Path, long Bytes, string Digest)> _refused;

        internal OfflineCopyChecks(Dictionary<string, OfflineCopyCheck> checks) : base(checks)
        {
            _refused = checks.Values.SelectMany(check => check.RefusedCopies ?? [])
                .Where(source => source.Bytes is not null && source.Digest is not null)
                .Select(source => PhysicalKey(source.Path, source.Bytes!.Value, source.Digest!)).ToHashSet();
        }

        internal bool HasRefusals => _refused.Count > 0;
        internal bool Refuses(string path, long bytes, string digest) => _refused.Count > 0 && _refused.Contains(PhysicalKey(path, bytes, digest));
        internal bool Refuses(LocalAudioSource source) => source.Bytes is { } bytes && source.Digest is { } digest && Refuses(source.Path, bytes, digest);

        private static (string Path, long Bytes, string Digest) PhysicalKey(string path, long bytes, string digest)
            => (path.ToUpperInvariant(), bytes, digest.ToUpperInvariant());
    }

    // The checked index and ready audio remain pinned until the dispatcher applies
    // these results. Dispose the batch before a worker can write new audio.
    internal sealed class VerifiedDownloadBatch(DownloadCacheResult[] results, Action release) : IDisposable
    {
        private Action? _release = release;
        internal DownloadCacheResult this[int position] => results[position];
        public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
    }

    internal async Task<VerifiedDownloadBatch> HoldDownloadCacheBatchAsync(IReadOnlyList<DownloadCacheRequest> requests, CancellationToken token,
        bool verifyAllVariants = false)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(requests.Count, DownloadVerificationBatchSize);
        EnsureRecoveryReadReady();
        await _writeLock.WaitAsync(token).ConfigureAwait(false);
        UninstallPathGuard.AncestorLease? parents = null;
        SafeFileHandle? index = null;
        var files = new Dictionary<(string Path, long Bytes, string Sha256), VerifiedSavedFile>();
        try
        {
            parents = HoldStorageParents(_indexPath);
            lock (_directoryState.IndexPublicationSync) index = parents.Complete ? OpenIndex(_indexPath) : null;
            LibraryCache snapshot;
            lock (_cacheLock) snapshot = LoadLibrarySnapshot(ReadIndexSnapshot(index), force: false);
            var results = new DownloadCacheResult[requests.Count];
            for (var i = 0; i < requests.Count; i++)
            {
                token.ThrowIfCancellationRequested();
                var request = requests[i];
                IOException? unavailable = null;
                List<OfflineItem>? verifiedCopies = verifyAllVariants ? [] : null;
                List<LocalAudioSource>? refusedCopies = null;
                foreach (var candidate in VerifiedCandidates(request.Track, snapshot))
                {
                    if (request.Accept is not null && !request.Accept(candidate)) continue;
                    var key = (candidate.FileName, candidate.Bytes, candidate.Sha256);
                    if (!files.ContainsKey(key))
                    {
                        VerifiedSavedFile? file;
                        try { file = await HoldExpectedHashAsync(candidate, token, failIfUnavailable: true, parentsAlreadyHeld: true).ConfigureAwait(false); }
                        catch (IOException error)
                        {
                            unavailable ??= error;
                            if (verifyAllVariants) (refusedCopies ??= []).Add(IndexedPlaybackSource(candidate, selected: false));
                            continue;
                        }
                        if (file is null) continue;
                        files.Add(key, file);
                    }
                    if (results[i].Item is null) results[i] = new(candidate, null);
                    verifiedCopies?.Add(candidate);
                    if (!verifyAllVariants) break;
                }
                if (results[i].Item is null) results[i] = new(null, unavailable);
                if (verifiedCopies is not null)
                    results[i] = results[i] with { VerifiedCopies = verifiedCopies, RefusedCopies = refusedCopies };
            }
            return new(results, Release);
        }
        catch { Release(); throw; }

        void Release()
        {
            try { foreach (var file in files.Values) file.Dispose(); }
            finally
            {
                index?.Dispose(); parents?.Dispose();
                _writeLock.Release();
            }
        }
    }
    public IReadOnlyList<TrackRow> Tracks(OfflineCopyChecks? verifiedCopies = null)
        => Read().Select((item, i) =>
        {
            var path = ValidFile(item) ? ResolvePath(item.FileName) : null;
            var valid = path is not null && AllowsVerifiedProjection(verifiedCopies, item)
                && !IsRejectedPlaybackSource(path, item.Sha256, item.Bytes);
            var quality = QualityLabel(item);
            return new TrackRow
            {
            Number = i + 1, Id = item.Id, RequestId = item.RequestId, Title = item.Title, Artist = item.Artist, Artists = CopyArtists(item.Artists),
            PrimaryArtistName = item.PrimaryArtistName,
            Album = item.Album, AlbumId = item.AlbumId, DurationMs = item.DurationMs, CoverUri = item.CoverUri, Available = valid,
            IsOfflineLibraryRow = true, LocalPath = path, LocalQuality = quality, LocalSourceRequestId = item.RequestId,
            LocalSourceCodec = item.Codec, LocalSourceBitrate = item.Bitrate,
            LocalSourceConfirmedBitrate = item.ConfirmedBitrate,
            LocalSourceSha256 = item.Sha256,
            DownloadedPath = valid ? path : null, DownloadedQuality = valid ? quality : null
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

    internal OfflinePlaylistStatus InspectPlaylistStatus(string owner, string kind, IReadOnlyList<TrackRow>? currentTracks, CancellationToken token,
        OfflineCopyChecks? verifiedCopies = null)
    {
        token.ThrowIfCancellationRequested();
        var snapshot = GetPlaylists().FirstOrDefault(p => p.Owner == owner && p.Kind == kind);
        if (snapshot is null) return new(currentTracks?.Count ?? 0, 0, false, false, false);
        var tracks = currentTracks is null ? snapshot.Tracks : currentTracks.Select(TrackMetadata).ToArray();
        var changed = tracks.Count != snapshot.Tracks.Count
            || !tracks.Zip(snapshot.Tracks).All(pair => SameTrackMetadata(pair.First, pair.Second));
        var library = LoadLibrary();
        var downloaded = tracks.Count(t =>
        {
            token.ThrowIfCancellationRequested();
            return FindProjectedPlaylistItem(ToRow(t), library, verifiedCopies) is not null;
        });
        // Quality is retained as requested metadata. Completion uses actual valid files,
        // independently of the UI's currently selected quality or codec fallback.
        return new(tracks.Count, downloaded, tracks.Count > 0 && !changed && downloaded == tracks.Count, changed, true);
    }

    private bool AllowsVerifiedProjection(OfflineCopyChecks? checks, OfflineItem item)
        => checks is null || !(checks.HasRefusals && checks.Refuses(ResolvePath(item.FileName), item.Bytes, item.Sha256))
            && (checks.GetValueOrDefault(item.Id)?.VerifiedCopies is not { } verified || verified.Any(candidate => SameOfflineItem(candidate, item)));

    internal bool AllowsVerifiedCopy(OfflineCopyChecks checks, string trackId, LocalAudioSource source)
        => !checks.Refuses(source)
            && (checks.GetValueOrDefault(trackId)?.VerifiedCopies is not { } verified || verified.Any(candidate => MatchesOfflineCopy(source, candidate)));

    internal bool MatchesOfflineCopy(LocalAudioSource source, OfflineItem copy)
        => MatchesPhysicalCopy(source, copy)
            && source.Variant is { } variant && variant.RequestId == copy.RequestId && variant.Codec == copy.Codec
            && variant.Bitrate == copy.Bitrate && variant.ConfirmedBitrate == copy.ConfirmedBitrate;

    internal bool MatchesPhysicalCopy(LocalAudioSource source, OfflineItem copy)
        => source.Bytes == copy.Bytes && StringComparer.OrdinalIgnoreCase.Equals(source.Digest, copy.Sha256)
            && StringComparer.OrdinalIgnoreCase.Equals(source.Path, ResolvePath(copy.FileName));

    internal static bool SamePhysicalCopy(LocalAudioSource left, LocalAudioSource right)
        => left.Bytes == right.Bytes && StringComparer.OrdinalIgnoreCase.Equals(left.Digest, right.Digest)
            && StringComparer.OrdinalIgnoreCase.Equals(left.Path, right.Path);

    internal static bool SameOfflineCopy(LocalAudioSource left, LocalAudioSource right)
        => SamePhysicalCopy(left, right) && left.Variant is { } chosen && right.Variant is { } other
            && chosen.RequestId == other.RequestId && chosen.Codec == other.Codec && chosen.Bitrate == other.Bitrate
            && chosen.ConfirmedBitrate == other.ConfirmedBitrate;

    private OfflineItem? FindProjectedPlaylistItem(TrackRow track, LibraryCache library, OfflineCopyChecks? checks)
        => checks is not null && (checks.HasRefusals || checks.ContainsKey(track.Id))
            ? VerifiedCandidates(track, library).FirstOrDefault(item => AllowsVerifiedProjection(checks, item) && ValidFile(item)
                && !IsRejectedPlaybackSource(ResolvePath(item.FileName), item.Sha256, item.Bytes))
            : FindPlaylistPlaybackItem(track, library);

    public IReadOnlyList<TrackRow> GetPlaylistTracks(string owner, string kind, OfflineCopyChecks? verifiedCopies = null)
    {
        var snapshot = GetPlaylists().FirstOrDefault(p => p.Owner == owner && p.Kind == kind);
        if (snapshot is null) return [];
        var library = LoadLibrary();
        return snapshot.Tracks.Select((track, index) =>
        {
            var local = FindProjectedPlaylistItem(ToRow(track), library, verifiedCopies);
            var path = local is null ? null : ResolvePath(local.FileName);
            var quality = local is null ? null : QualityLabel(local);
            return new TrackRow
            {
                Number = index + 1, Id = track.Id, RequestId = track.RequestId, Title = track.Title, Artist = track.Artist, Artists = CopyArtists(track.Artists),
                PrimaryArtistName = track.PrimaryArtistName,
                Album = track.Album, AlbumId = track.AlbumId, DurationMs = track.DurationMs, CoverUri = track.CoverUri, Available = local is not null,
                IsOfflineLibraryRow = true, LocalPath = path, LocalQuality = quality, LocalSourceRequestId = local?.RequestId,
                LocalSourceCodec = local?.Codec, LocalSourceBitrate = local?.Bitrate,
                LocalSourceConfirmedBitrate = local?.ConfirmedBitrate,
                LocalSourceSha256 = local?.Sha256,
                DownloadedPath = path, DownloadedQuality = quality
            };
        }).ToArray();
    }

    public async Task<OfflineItem> SaveAsync(TrackRow track, AudioDescriptor audio, CancellationToken cancellationToken,
        IProgress<AudioProgress>? progress = null, bool recheckQuality = false)
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
            async Task<bool> IsStillIndexedAsync(OfflineItem item)
            {
                await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    await RecoverIfPendingUnderGateAsync().ConfigureAwait(false);
                    if (_directoryState.Generation != generation) throw DownloadInvalidated();
                    return LoadLibrary(force: true, recovering: true).Items.Any(current => SameOfflineItem(current, item));
                }
                finally { _writeLock.Release(); }
            }
            IOException? unavailable = null;
            var retainedVariants = new List<OfflineItem>();
            foreach (var existing in savedVariants)
            {
                VerifiedSavedFile? verifiedFile;
                try { verifiedFile = await HoldExpectedHashAsync(existing, cancellationToken, failIfUnavailable: true).ConfigureAwait(false); }
                catch (IOException error) { unavailable ??= error; continue; }
                using var verified = verifiedFile;
                if (verified is null) continue;
                if (recheckQuality && AudioQualityPolicy.IsBetter(audio.Codec, audio.Bitrate, existing.Codec, existing.QualityBitrate))
                {
                    retainedVariants.Add(existing);
                    continue;
                }
                SaveCheckpoint?.Invoke("save-existing-verified");
                if (await IsStillIndexedAsync(existing).ConfigureAwait(false)) return existing;
            }
            if (unavailable is not null) throw unavailable;
            int? confirmedBitrate;
            using (await _directoryState.EnterDownloadAsync(cancellationToken).ConfigureAwait(false))
            {
                var candidate = await _downloader.DownloadAsync(audio, cancellationToken, progress).ConfigureAwait(false);
                (buffered, confirmedBitrate) = await OfflineAudioProbe.VerifyAsync(candidate, audio, track.DurationMs, cancellationToken,
                    SaveCheckpoint is { } checkpoint ? () => checkpoint("save-before-audio-probe") : null).ConfigureAwait(false);
            }
            cancellationToken.ThrowIfCancellationRequested();
            var previous = retainedVariants.OrderByDescending(item => item.QualityBitrate).FirstOrDefault();
            if (previous is not null && previous.QualityBitrate >= confirmedBitrate.GetValueOrDefault())
            {
                using var retained = await HoldExpectedHashAsync(previous, cancellationToken, failIfUnavailable: true).ConfigureAwait(false);
                if (retained is not null && await IsStillIndexedAsync(previous).ConfigureAwait(false)) return previous;
            }
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
                var sameBytes = retainedVariants.FirstOrDefault(item => item.Bytes == output.Length
                    && item.Sha256.Equals(persistedHash, StringComparison.OrdinalIgnoreCase));
                using var sameFile = sameBytes is null ? null
                    : await HoldExpectedHashAsync(sameBytes, cancellationToken, failIfUnavailable: true).ConfigureAwait(false);
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
                if (sameBytes is not null && sameFile is not null)
                {
                    var observed = sameBytes with { ConfirmedBitrate = confirmedBitrate };
                    var updated = latest.Items.Select(item => SameOfflineItem(item, sameBytes) ? observed : item).ToList();
                    await WriteIndexAsync(_indexPath, updated, LibraryJson, latest.Stamp, cancellationToken).ConfigureAwait(false);
                    committed = true;
                    lock (_cacheLock) _libraryCache = null;
                    SaveCheckpoint?.Invoke("save-after-index-commit");
                    return observed;
                }
                var filename = preferredName;
                var destination = preferredPath;
                if (UninstallPathGuard.EntryExists(destination) || latest.Items.Any(i => i.FileName.Equals(filename, StringComparison.OrdinalIgnoreCase)))
                { filename = $"{title} [{id}-{Guid.NewGuid():N}]{audio.Extension}"; destination = ResolvePath(filename); }
                var item = new OfflineItem(track.Id, track.RequestId, track.Title, track.Artist, track.Album, track.DurationMs, filename,
                    audio.Codec, audio.Bitrate, output.Length, persistedHash, DateTimeOffset.UtcNow, track.CoverUri, artists, track.AlbumId,
                    track.PrimaryArtistNameToPersist, confirmedBitrate);
                SaveCheckpoint?.Invoke("save-before-receipt");
                var identity = GetStorageFileIdentity(stage);
                journal = CreateSaveJournal(partial, item, identity, latest.Stamp);
                if (identity is null) unidentifiedDestination = item.FileName;
                SaveCheckpoint?.Invoke("save-before-audio-publish");
                ManagedCacheCleanup.RenameByHandle(stage, destination, releaseDeleteDisposition: true);
                SaveCheckpoint?.Invoke("save-after-audio-publish");
                // Keep verified earlier realizations indexed even when the service
                // reported the same nominal rate for a different, improved file.
                var items = latest.Items.Where(i => !IsSameVariant(i)
                    || retainedVariants.Any(previousItem => SameOfflineItem(i, previousItem))).ToList();
                items.Insert(0, item);
                await WriteIndexAsync(_indexPath, items, LibraryJson, latest.Stamp, cancellationToken).ConfigureAwait(false);
                committed = true;
                lock (_cacheLock) _libraryCache = null;
                SaveCheckpoint?.Invoke("save-after-index-commit");
                RetireJournal(journal, SaveRecoveryPath);
                return item;
            }
            finally
            {
                journal?.Dispose();
                if (!committed && journal is not null)
                {
                    try
                    {
                        RecoverIndexPublicationUnderGate();
                        // A live rollback can still use its exact handle on a
                        // filesystem that cannot provide a persistent file ID.
                        if (unidentifiedDestination is not null) _ = RollbackUnindexedSaveAudio(stage, unidentifiedDestination);
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
            var clearPlaylists = trackId is null && playlists.Items.Count > 0;
            if (clearPlaylists)
            {
                using var current = OpenIndex(_playlistIndexPath) ?? throw IndexChanged();
                _ = RequireRemovalIndexIdentity(current);
            }
            var selected = library.Items.Where(i => trackId is null || i.Id == trackId).ToArray();
            var remaining = library.Items.Where(i => trackId is not null && i.Id != trackId).ToList();
            var candidates = selected.GroupBy(i => i.FileName, StringComparer.OrdinalIgnoreCase).ToArray();
            var referenced = remaining.ToLookup(i => i.FileName, StringComparer.OrdinalIgnoreCase);
            var verified = 0; long verifiedBytes = 0;
            var totalBytes = candidates.Sum(group => group.Max(row => row.Bytes));
            var resultGate = new object();
            void Report(string phase)
                => progress?.Report(new(verified, candidates.Length, removed, preserved, failed, verifiedBytes, totalBytes, phase));
            void RetainCandidate(IGrouping<string, OfflineItem> group, OfflineItem? verified)
            {
                lock (resultGate)
                {
                    failed++;
                    retained.AddRange(verified is null ? group : group.Where(row => SameRecoveredAudio(row, verified)));
                }
            }
            Report("verify");
            _directoryState.Generation++; // An already running Save cannot republish after removal.
            // At most two simultaneous media reads. A single seek-limited disk
            // may not benefit from concurrency; avoiding a second full read is
            // the main saving on both HDD and SSD.
            await Parallel.ForEachAsync(candidates, new ParallelOptions { MaxDegreeOfParallelism = 2, CancellationToken = cancellationToken }, async (group, token) =>
            {
                token.ThrowIfCancellationRequested();
                var path = ResolvePath(group.Key);
                FileStream? file = null;
                OfflineItem? verifiedItem = null;
                try
                {
                    BeforeRemovalAttributesForChecks?.Invoke(path);
                    var attributes = File.GetAttributes(path);
                    if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
                    { lock (resultGate) preserved++; return; }
                    var quarantine = ResolvePath(".ym-delete-" + Guid.NewGuid().ToString("N") + ".pending");
                    var recovery = ResolvePath(".ym-recovered-" + Guid.NewGuid().ToString("N") + Path.GetExtension(path));
                    // READ|DELETE, FILE_SHARE_READ, OPEN_REPARSE_POINT. The
                    // native guard validates the opened object itself. Keep
                    // this exact handle alive through commit and deletion so
                    // nobody can mutate or replace the verified bytes.
                    var handle = UninstallPathGuard.OpenOwnedFile(path, read: true);
                    try { file = new FileStream(handle, FileAccess.Read, bufferSize: 1, isAsync: false); }
                    catch { handle.Dispose(); throw; }
                    var length = file.Length;
                    if (FileSecurityPreservation.HasNamedStreamsOrExtendedAttributes(handle) || !group.Any(row => row.Bytes == length))
                    { lock (resultGate) preserved++; return; }
                    var digest = await HashRemovalAudioAsync(file, token).ConfigureAwait(false);
                    var match = group.FirstOrDefault(row => row.Bytes == length && row.Sha256.Equals(digest, StringComparison.OrdinalIgnoreCase));
                    if (match is null)
                    { lock (resultGate) preserved++; return; }
                    verifiedItem = match;
                    if (referenced[group.Key].Any(row => SameRecoveredAudio(row, match)))
                    { lock (resultGate) preserved++; return; }
                    if ((attributes & FileAttributes.ReadOnly) != 0)
                    { RetainCandidate(group, verifiedItem); return; }
                    token.ThrowIfCancellationRequested();
                    var identity = RemovalAudioIdentityUnavailableForChecks ? null : GetStorageFileIdentity(handle);
                    staged.Add(new(verifiedItem, path, quarantine, recovery, file, identity));
                    file = null; // Ownership transfers to staged until final commit/rollback.
                    lock (resultGate) verifiedBytes += length;
                }
                catch (Exception ex) when (file is null && ex is (FileNotFoundException or DirectoryNotFoundException
                    or UninstallPathGuard.UnsafeStoragePathException or Win32Exception { NativeErrorCode: 2 or 3 }))
                { lock (resultGate) preserved++; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception)
                { RetainCandidate(group, verifiedItem); }
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
            journal = CreateRecoveryJournal(staged.ToArray(), library, playlists, remaining, clearPlaylists);
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
            if (journal.Data.PlaylistClearOperation is { } playlistOperation)
            {
                await WriteIndexAsync(_playlistIndexPath, new OfflinePlaylistIndex(1, []), PlaylistJson, playlists.Stamp,
                    cancellationToken, attestStage: (stage, _) => PublishRemovalIndexProof(playlistOperation, stage, playlists: true)).ConfigureAwait(false);
                RemovalCheckpoint?.Invoke("after-playlist-commit");
            }
            var attestLibrary = journal.Data.LibraryRemovalOperation is { } libraryOperation
                ? (Action<SafeFileHandle, IndexStamp>)((stage, _) => PublishRemovalIndexProof(libraryOperation, stage, playlists: false,
                    required: journal.Data.Unstaged is { Count: > 0 })) : null;
            await WriteIndexAsync(_indexPath, remaining, LibraryJson, library.Stamp, cancellationToken, attestStage: attestLibrary).ConfigureAwait(false);
            RemovalCheckpoint?.Invoke("before-removal-commit-marker");
            CommitRecoveryJournal(journal);
            RemovalCheckpoint?.Invoke("after-library-commit");
            var failedAudio = new List<RecoveredAudio>();
            foreach (var entry in staged)
            {
                var preserveMetadata = false;
                try
                {
                    EnsureSafeDirectory();
                    // Index commit is the cancellation boundary. After it, the
                    // small delete/restore phase completes with no audio reads.
                    var handle = entry.File.SafeFileHandle;
                    preserveMetadata = FileSecurityPreservation.HasNamedStreamsOrExtendedAttributes(handle);
                    if (!preserveMetadata)
                    {
                        UninstallPathGuard.DeleteByHandle(handle);
                        entry.Deleted = true;
                        entry.File.Dispose(); removed++;
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception)
                {
                    var restoredPath = RestoreStaged(entry); failed++;
                    // The same protected handle still proves the digest.
                    failedAudio.Add(new(entry.Item, Path.GetFileName(entry.Recovery), restoredPath));
                }
                if (preserveMetadata) { RestoreStaged(entry); preserved++; }
                Report("delete");
            }
            // Failed managed files remain discoverable for a later retry. The
            // second commit merges fresh external/unrelated entries instead of
            // hiding a restored audio file behind an empty index.
            if (failedAudio.Count > 0)
            {
                await RepairRecoveredIndexAsync(failedAudio, journal).ConfigureAwait(false);
                var failedNames = failedAudio.ToDictionary(audio => audio.Item.FileName, StringComparer.OrdinalIgnoreCase);
                retained.AddRange(selected.Where(row => failedNames.TryGetValue(row.FileName, out var saved) && SameRecoveredAudio(row, saved.Item)));
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
                    RecoverIndexPublicationUnderGate();
                    var restored = new List<RecoveredAudio>();
                    foreach (var entry in staged)
                    {
                        if (entry.Deleted) continue;
                        var target = RestoreStaged(entry);
                        var preserveMetadata = FileSecurityPreservation.HasNamedStreamsOrExtendedAttributes(entry.File.SafeFileHandle);
                        restored.Add(new(entry.Item, Path.GetFileName(entry.Recovery), preserveMetadata ? null : target));
                    }
                    // A collision may change the pathname even before commit.
                    // Repair that exact mapping rather than indexing a foreign replacement.
                    if (journal is not null)
                    {
                        await RestoreInterruptedPlaylistsAsync(journal).ConfigureAwait(false);
                        if (restored.Count > 0 || !journal.Committed && journal.Data.Unstaged is { Count: > 0 })
                            await RepairRecoveredIndexAsync(restored, journal).ConfigureAwait(false);
                        RemovalCheckpoint?.Invoke("live-rollback-after-index");
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
        => (await HashRemovalAudioAsync(file, token).ConfigureAwait(false)).Equals(expected, StringComparison.OrdinalIgnoreCase);

    private static async Task<string> HashRemovalAudioAsync(FileStream file, CancellationToken token)
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
            return Convert.ToHexString(digest.GetHashAndReset());
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }

    private long _downloadVerificationRejectionVersion;
    internal long DownloadVerificationRejectionVersion => Interlocked.Read(ref _downloadVerificationRejectionVersion);

    private async Task<VerifiedSavedFile?> HoldExpectedHashAsync(OfflineItem item, CancellationToken cancellationToken,
        bool failIfUnavailable = false, bool parentsAlreadyHeld = false)
    {
        var path = ResolvePath(item.FileName);
        UninstallPathGuard.AncestorLease? parents = null;
        FileStream? file = null;
        try
        {
            if (!parentsAlreadyHeld) parents = HoldStorageParents(path);
            if (parents is { Complete: false })
            {
                Interlocked.Increment(ref _downloadVerificationRejectionVersion);
                return null;
            }
            BeforeVerifySavedAudioForChecks?.Invoke(path);
            var handle = UninstallPathGuard.OpenOwnedFile(path, read: true, deleteAccess: false);
            try { file = new FileStream(handle, FileAccess.Read, bufferSize: 1, isAsync: false); }
            catch { handle.Dispose(); throw; }
            if (file.Length != item.Bytes)
            {
                Interlocked.Increment(ref _downloadVerificationRejectionVersion);
                return null;
            }
            var revision = LocalAudioSource.TryReadRevision(file.SafeFileHandle);
            if (!await RemovalHashMatchesAsync(file, item.Sha256, cancellationToken).ConfigureAwait(false))
            {
                if (revision is not null && revision == LocalAudioSource.TryReadRevision(file.SafeFileHandle)
                    && RememberRejectedPlaybackSource(IndexedPlaybackSource(item, selected: false), new LocalAudioIntegrityException(revision)))
                    Interlocked.Increment(ref _downloadVerificationRejectionVersion);
                return null;
            }
            var verified = new VerifiedSavedFile(parents, file);
            parents = null;
            file = null;
            return verified;
        }
        catch (Exception error) when (file is null && error is (FileNotFoundException or DirectoryNotFoundException
            or Win32Exception { NativeErrorCode: 2 or 3 }))
        {
            Interlocked.Increment(ref _downloadVerificationRejectionVersion);
            return null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or Win32Exception)
        {
            if (!failIfUnavailable) return null;
            if (error is IOException) throw;
            throw new IOException("Не удалось проверить сохранённый аудиофайл. Файл сохранён.", error);
        }
        finally { file?.Dispose(); parents?.Dispose(); }
    }

    private sealed class VerifiedSavedFile(UninstallPathGuard.AncestorLease? parents, FileStream file) : IDisposable
    {
        public void Dispose() { file.Dispose(); parents?.Dispose(); }
    }

    private static string RestoreDestination(StorageFileIdentity? identity, string original, string recovery)
        => identity is null || UninstallPathGuard.EntryExists(original) ? recovery : original;

    private string RestoreStaged(StagedRemoval entry)
    {
        if (entry.RestoredPath is { } restored) return restored;
        if (!entry.Moved) return entry.Original;
        try
        {
            var recovered = RestoreDestination(entry.Identity, entry.Original, entry.Recovery);
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
            return LoadLibrarySnapshot(ReadIndexSnapshot(_indexPath), force);
        }
    }

    private LibraryCache LoadLibrarySnapshot((IndexStamp Stamp, byte[]? Bytes) snapshot, bool force)
    {
        try
        {
            var (stamp, bytes) = snapshot;
            if (!force && _libraryCache is { } cached && cached.Stamp == stamp) return cached;
            var items = stamp.Exists ? ParseLibraryIndex(bytes!) : [];
            var ordered = items.Select(item => (Item: item, Rank: AudioQualityPolicy.Rank(item.Codec, item.QualityBitrate)))
                .OrderByDescending(entry => entry.Rank)
                .ThenByDescending(entry => entry.Rank == 3 ? 0 : entry.Item.QualityBitrate)
                .ThenByDescending(entry => entry.Item.SavedUtc)
                .ThenBy(entry => entry.Item.FileName, StringComparer.Ordinal)
                .Select(entry => entry.Item).ToArray();
            _libraryCache = new(stamp, Array.AsReadOnly(items.ToArray()),
                ordered.GroupBy(i => i.RequestId, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.Ordinal),
                ordered.GroupBy(i => i.Id, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.Ordinal));
            return _libraryCache;
        }
        catch (JsonException) { throw new MusicException("Не удалось прочитать индекс офлайн-библиотеки. Аудиофайлы сохранены; index не перезаписывался."); }
    }

    internal IReadOnlyList<OfflineItem> ReadAudioIndex(SafeFileHandle handle)
        => ParseLibraryIndex(ReadIndexSnapshot(handle).Bytes!);

    private List<OfflineItem> ParseLibraryIndex(byte[] bytes)
    {
        var items = JsonSerializer.Deserialize<List<OfflineItem>>(DecodeIndex(bytes), LibraryJson) ?? throw new JsonException();
        foreach (var item in items)
        {
            if (item is null || string.IsNullOrWhiteSpace(item.Id) || string.IsNullOrWhiteSpace(item.RequestId)
                || item.Title is null || item.Artist is null || !TrackRow.ValidPrimaryArtistName(item.PrimaryArtistName)
                || item.Album is null || string.IsNullOrWhiteSpace(item.Codec)
                || item.DurationMs < 0 || item.Bitrate < 0 || item.ConfirmedBitrate is <= 0 || item.Bytes <= 0 || item.SavedUtc == default
                || item.Sha256 is null || item.Sha256.Length != 64 || !item.Sha256.All(Uri.IsHexDigit)) throw new JsonException();
            ValidateArtists(item.Artists);
            ValidateAlbumId(item.AlbumId);
            _ = ResolvePath(item.FileName);
        }
        return items;
    }

    private PlaylistCache LoadPlaylists(bool force = false, bool recovering = false)
    {
        if (!recovering) EnsureRecoveryReadReady();
        lock (_cacheLock)
        {
            EnsureSafeDirectory();
            try
            {
                var (stamp, bytes) = ReadIndexSnapshot(_playlistIndexPath);
                if (!force && _playlistCache is { } cached && cached.Stamp == stamp) return cached;
                var data = stamp.Exists ? ParsePlaylistIndex(bytes!) : new(1, []);
                _playlistCache = new(stamp, Array.AsReadOnly(data.Playlists.Select(p => p with { Tracks = Array.AsReadOnly(p.Tracks.ToArray()) }).ToArray()));
                return _playlistCache;
            }
            catch (Exception error) when (error is JsonException or OfflineIndexSizeException)
            { throw new OfflinePlaylistIndexFormatException(); }
        }
    }

    internal static bool IsSupportedPlaylistIndex(SafeFileHandle handle)
    {
        try
        {
            _ = ParsePlaylistIndex(ReadIndexSnapshot(handle).Bytes!);
            return true;
        }
        catch (Exception error) when (error is JsonException or OfflineIndexSizeException) { return false; }
    }

    private static OfflinePlaylistIndex ParsePlaylistIndex(byte[] bytes)
    {
        var data = JsonSerializer.Deserialize<OfflinePlaylistIndex>(DecodeIndex(bytes), PlaylistJson) ?? throw new JsonException();
        if (data.SchemaVersion != 1 || data.Playlists is null) throw new JsonException();
        foreach (var playlist in data.Playlists) ValidatePlaylist(playlist);
        if (data.Playlists.GroupBy(p => (p.Owner, p.Kind)).Any(g => g.Count() > 1)) throw new JsonException();
        return data;
    }

    private static SafeFileHandle? OpenIndex(string path, bool deleteAccess = false, bool writeAttributes = false)
    {
        try { return UninstallPathGuard.OpenOwnedFile(path, read: true, deleteAccess: deleteAccess, writeAttributes: writeAttributes); }
        catch (Win32Exception error) when (error.NativeErrorCode is 2 or 3) { return null; }
        catch (Win32Exception error) { throw new IOException("Не удалось прочитать индекс офлайн-библиотеки.", error); }
    }

    private (IndexStamp Stamp, byte[]? Bytes) ReadIndexSnapshot(string path)
    {
        using var parents = HoldStorageParents(path);
        lock (_directoryState.IndexPublicationSync)
        {
            using var handle = parents.Complete ? OpenIndex(path) : null;
            return ReadIndexSnapshot(handle);
        }
    }

    private static (IndexStamp Stamp, byte[]? Bytes) ReadIndexSnapshot(SafeFileHandle? handle)
    {
        if (handle is null) return (new(0, null, null), null);
        var revision = IndexRevision(handle);
        var length = RandomAccess.GetLength(handle);
        if (length > MaximumIndexBytes) throw new OfflineIndexSizeException();
        byte[] bytes = length == 0 ? [] : GuardedStoreFile.ReadFromHandle(handle, MaximumIndexBytes) ?? throw IndexChanged();
        if (IndexRevision(handle) != revision || bytes.LongLength != length) throw IndexChanged();
        return (new(length, revision, Digest(bytes)), bytes);
    }

    private sealed class OfflineIndexSizeException() : IOException("Индекс офлайн-библиотеки слишком большой. Файлы сохранены.");

    private static FileSecurityPreservation.FileRevision? IndexRevision(SafeFileHandle handle)
        => FileSecurityPreservation.GetPersistentRevision(handle) is { Changed: > 0 } revision ? revision : null;

    private static readonly Encoding StrictUtf8Index = new UTF8Encoding(false, true);
    private static readonly Encoding StrictUtf16LeIndex = new UnicodeEncoding(false, false, true);
    private static readonly Encoding StrictUtf16BeIndex = new UnicodeEncoding(true, false, true);
    private static readonly Encoding StrictUtf32LeIndex = new UTF32Encoding(false, false, true);
    private static readonly Encoding StrictUtf32BeIndex = new UTF32Encoding(true, false, true);

    private static string DecodeIndex(byte[] bytes)
    {
        // Keep StreamReader's BOM formats without its lossy decoder fallback.
        var (encoding, preamble) = bytes switch
        {
            [0xff, 0xfe, 0x00, 0x00, ..] => (StrictUtf32LeIndex, 4),
            [0x00, 0x00, 0xfe, 0xff, ..] => (StrictUtf32BeIndex, 4),
            [0xff, 0xfe, ..] => (StrictUtf16LeIndex, 2),
            [0xfe, 0xff, ..] => (StrictUtf16BeIndex, 2),
            [0xef, 0xbb, 0xbf, ..] => (StrictUtf8Index, 3),
            _ => (StrictUtf8Index, 0)
        };
        try { return encoding.GetString(bytes.AsSpan(preamble)); }
        catch (DecoderFallbackException error)
        { throw new JsonException("Индекс офлайн-библиотеки содержит повреждённую кодировку.", error); }
    }

    private static void CheckIndexStamp(IndexStamp current, IndexStamp expected)
    {
        if (current.Hash != expected.Hash || expected.Revision is not null && current.Revision != expected.Revision) throw IndexChanged();
    }

    private async Task WriteIndexAsync<T>(string path, T data, JsonSerializerOptions options, IndexStamp expected,
        CancellationToken cancellationToken, Action? beforePublication = null, Action<SafeFileHandle, IndexStamp>? attestStage = null,
        StorageFileIdentity? requireIdentity = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RecoverIndexPublicationUnderGate();
        var serialized = JsonSerializer.Serialize(data, options);
        if (expected.Length > MaximumIndexBytes || System.Text.Encoding.UTF8.GetByteCount(serialized) > MaximumIndexBytes)
            throw new IOException("Индекс офлайн-библиотеки слишком большой. Файлы сохранены.");
        using var parents = HoldStorageParents(path, create: true);
        var temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using var original = OpenIndex(path);
            if (requireIdentity is not null && (original is null || GetStorageFileIdentity(original) != requireIdentity)) throw IndexChanged();
            CheckIndexStamp(ReadIndexSnapshot(original).Stamp, expected);
            FileSecurityPreservation.FileRevision? originalRevision = null;
            if (original is not null)
            {
                FileSecurityPreservation.CheckReplacementMetadata(original);
                originalRevision = FileSecurityPreservation.GetRevision(original);
            }
            using var handle = UninstallPathGuard.CreateStagedFile(temp, asynchronous: true, writeThrough: true, writeDacl: original is not null);
            UninstallPathGuard.SetDeleteDisposition(handle, delete: true);
            await using var output = new FileStream(handle, FileAccess.ReadWrite, 4096, isAsync: true);
            var published = false;
            try
            {
                if (original is not null) FileSecurityPreservation.PrepareReplacement(original, handle);
                else FileSecurityPreservation.CheckReplacementMetadata(handle);
                var bytes = System.Text.Encoding.UTF8.GetBytes(serialized);
                await output.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                output.Flush(flushToDisk: true);
                output.Position = 0;
                using var readback = new StreamReader(output, leaveOpen: true);
                if (await readback.ReadToEndAsync(cancellationToken).ConfigureAwait(false) != serialized) throw new MusicException("Не прошла проверка сохранённого индекса офлайн-библиотеки.");
                cancellationToken.ThrowIfCancellationRequested();
                if (original is not null) FileSecurityPreservation.PreserveBasicInformation(original, handle);
                File.SetLastWriteTimeUtc(handle, DateTime.UtcNow);
                output.Flush(flushToDisk: true);
                var writtenStamp = new IndexStamp(output.Length, null, Digest(bytes));
                beforePublication?.Invoke();
                TemporaryPublicationCheckpoint?.Invoke("index-before-publish");
                attestStage?.Invoke(handle, writtenStamp);
                lock (_directoryState.IndexPublicationSync)
                {
                    try
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (original is not null)
                        {
                            FileSecurityPreservation.CheckReplacementMetadata(original);
                            if (FileSecurityPreservation.GetRevision(original) != originalRevision) throw IndexChanged();
                            original.Dispose(); // Recheck the revision after reopening with delete access.
                            ReplaceIndexUnderPublicationLock(path, temp, handle, expected, writtenStamp, originalRevision!.Value,
                                preservePreviousRevision: requireIdentity is not null, ref published);
                        }
                        else PublishStagedIndex(handle, path, ref published);
                    }
                    finally
                    {
                        // Keep the new name pinned through backup and receipt retirement.
                        // The staging readback and flush left no pending writes.
                        if (published) handle.Dispose();
                    }
                }
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
    {
        if (left.CoverUri != right.CoverUri && ArtworkUri.Metadata(left.CoverUri) != ArtworkUri.Metadata(right.CoverUri)) return false;
        // Navigation metadata and online availability do not change the
        // playlist's saved track order or the validity of its local files.
        return left with { Artists = null, AlbumId = null, PrimaryArtistName = null, CoverUri = null, Available = false }
            == right with { Artists = null, AlbumId = null, PrimaryArtistName = null, CoverUri = null, Available = false };
    }
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
    internal static string QualityLabel(OfflineItem item)
        => item.Codec.StartsWith("flac", StringComparison.OrdinalIgnoreCase) ? "Lossless · FLAC" : item.Codec.ToUpperInvariant()
            + (item.ConfirmedBitrate is { } bitrate ? $" · {bitrate} кбит/с" : "");
    private static MusicException IndexChanged() => new("Индекс офлайн-библиотеки изменился во время записи. Ничего не перезаписывалось; перечитайте библиотеку перед повтором.")
        { OfflineStorageRetryable = true };
    private sealed class DirectoryState
    {
        public SemaphoreSlim Gate { get; } = new(1);
        internal object RecoverySync { get; } = new();
        // Readers deny replacement while their verified index handle is open.
        // Serialize only this handle's lifetime against final publication.
        internal object IndexPublicationSync { get; } = new();
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
    private sealed record IndexStamp(long Length, FileSecurityPreservation.FileRevision? Revision, string? Hash)
    {
        internal bool Exists => Hash is not null;
    }
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
