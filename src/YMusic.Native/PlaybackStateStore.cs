using System.Collections.Concurrent;
using System.ComponentModel;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32.SafeHandles;

namespace YMusic.Native;

// Only descriptive metadata is saved. Audio locations, credentials,
// download descriptors, and the native player's temporary files are never saved.
internal sealed record PlaybackTrackState(
    [property: JsonRequired] string Id, [property: JsonRequired] string RequestId,
    [property: JsonRequired] string Title, [property: JsonRequired] string Artist,
    [property: JsonRequired] string Album, [property: JsonRequired] long DurationMs,
    [property: JsonRequired] bool Available, [property: JsonRequired] IReadOnlyList<ArtistReference> Artists,
    string? AlbumId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? CoverUri = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? PrimaryArtistName = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool OfflineOnly = false,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? LocalSourceRequestId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? LocalSourceCodec = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? LocalSourceBitrate = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? LocalSourceConfirmedBitrate = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? LocalSourceSha256 = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ExpectedLocalSourceSha256 = null);

internal sealed record PlaybackAlbumState(string Id, string Title, string Artist);

internal sealed record PlaybackSnapshot(
    [property: JsonRequired] int Version, [property: JsonRequired] string AccountUid,
    [property: JsonRequired] DateTimeOffset SavedUtc,
    [property: JsonRequired] IReadOnlyList<PlaybackTrackState> Tracks,
    [property: JsonRequired] int CurrentIndex, [property: JsonRequired] double PositionSeconds,
    [property: JsonRequired] bool Shuffle, [property: JsonRequired] RepeatMode Repeat,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] PlaybackAlbumState? SourceAlbum = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<int>? PlayNextIndices = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? PositionDurationMs = null);

internal sealed record RestoredPlaybackQueue(IReadOnlyList<TrackRow> Tracks, int CurrentIndex, double PositionSeconds,
    IReadOnlyList<int>? PlayNextIndices = null, long? PositionDurationMs = null);
internal enum PlaybackStateReadStatus { Missing, Disabled, Loaded, OtherAccount, Invalid, Unavailable }

internal sealed class PlaybackStateStore
{
    internal const int CurrentVersion = 1;
    internal const int MaximumTracks = 10_000;
    internal const int MaximumBytes = 8 * 1024 * 1024;
    internal const long MaximumDurationMs = 7L * 24 * 60 * 60 * 1000;
    internal const string FileName = "playback-state.json";
    private static ReadOnlySpan<byte> ClearedMarker => "{\"version\":0}"u8;
    private static readonly ConcurrentDictionary<string, object> Gates = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, long> Revisions = new(StringComparer.OrdinalIgnoreCase);
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 16
    };
    private readonly string _path;
    private readonly object _gate;
    private (string AccountUid, FileSecurityPreservation.FileRevision File, string Hash)? _knownSnapshot;
    public PlaybackStateReadStatus LastReadStatus { get; private set; }

    public PlaybackStateStore(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        _path = Path.Combine(Path.GetFullPath(directory), FileName);
        _gate = Gates.GetOrAdd(_path, _ => new object());
    }

    // An absent/unknown UID is never proof that the stored queue belongs to the
    // current session. In particular, a device ID is reused across account logins.
    public PlaybackSnapshot? Load(string? accountUid, bool enabled = true)
    {
        if (!enabled || !ValidId(accountUid)) { LastReadStatus = PlaybackStateReadStatus.Disabled; return null; }
        lock (_gate)
        {
            try
            {
                using var parents = UninstallPathGuard.HoldAncestors(_path);
                using var handle = parents.Complete ? GuardedStoreFile.OpenForRead(_path) : null;
                if (handle is null) { LastReadStatus = PlaybackStateReadStatus.Missing; return null; }
                using var stream = new FileStream(handle, FileAccess.Read);
                if (stream.Length > MaximumBytes) throw new JsonException();
                // The opened handle bounds allocation and excludes concurrent writes;
                // never read an unbounded string from an untrusted snapshot.
                var bytes = new byte[checked((int)stream.Length)];
                stream.ReadExactly(bytes);
                var snapshot = ReadSnapshot(bytes);
                if (snapshot is null) { LastReadStatus = PlaybackStateReadStatus.Missing; return null; }
                if (!string.Equals(snapshot.AccountUid, accountUid, StringComparison.Ordinal))
                { LastReadStatus = PlaybackStateReadStatus.OtherAccount; return null; }
                RememberSnapshotFile(snapshot.AccountUid, handle, bytes);
                LastReadStatus = PlaybackStateReadStatus.Loaded;
                return Copy(snapshot);
            }
            catch (JsonException) { LastReadStatus = PlaybackStateReadStatus.Invalid; return null; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception)
            { LastReadStatus = PlaybackStateReadStatus.Unavailable; return null; }
        }
    }

    public void Save(PlaybackSnapshot snapshot)
        => SaveCore(snapshot, null);

    internal void SaveVerifiedCheckpoint(PlaybackSnapshot snapshot)
        => SaveCore(snapshot, null, verifyExisting: true);

    internal Task SaveInBackgroundAsync(PlaybackSnapshot snapshot)
    {
        var frozen = Copy(snapshot);
        long revision;
        lock (_gate) revision = Revisions.GetOrAdd(_path, 0);
        return Task.Run(() => SaveCore(frozen, revision));
    }

    private void SaveCore(PlaybackSnapshot snapshot, long? expectedRevision, bool verifyExisting = false)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        Validate(snapshot);
        // Freeze caller-owned collections before serialization and validate that
        // exact copy. Nothing can mutate a persisted queue occurrence in flight.
        var frozen = Copy(snapshot);
        Validate(frozen);
        using var serialized = new BoundedJsonStream();
        try { JsonSerializer.Serialize(serialized, frozen, Json); }
        catch (JsonException) when (serialized.LimitExceeded && frozen.Tracks.Any(track => track.CoverUri is not null))
        {
            // Optional artwork must fit around the queue and resume position.
            serialized.SetLength(0);
            JsonSerializer.Serialize(serialized, frozen with
            {
                Tracks = frozen.Tracks.Select(track => track with { CoverUri = null }).ToArray()
            }, Json);
        }
        var bytes = serialized.ToArray();
        lock (_gate)
        {
            var revision = Revisions.GetOrAdd(_path, 0);
            if (expectedRevision.HasValue && expectedRevision.Value != revision) return;
            try { WriteAtomic(frozen.AccountUid, bytes, verifyExisting); }
            catch (Win32Exception error) { throw new IOException("Не удалось сохранить состояние воспроизведения.", error); }
            Revisions[_path] = revision + 1;
        }
    }

    internal readonly record struct ClearRetry(long Revision, string AccountUid, FileSecurityPreservation.FileRevision? File,
        string? Hash = null);

    internal void Clear(string accountUid, out ClearRetry? pending)
    {
        if (!ValidId(accountUid)) { pending = null; return; }
        lock (_gate)
        {
            var revision = Revisions.AddOrUpdate(_path, 1, (_, value) => value + 1);
            var known = _knownSnapshot;
            if (known?.AccountUid != accountUid) known = null;
            pending = new(revision, accountUid, known?.File, known?.Hash);
            try
            {
                using var writer = GuardedStoreFile.HoldWriteGate(_path);
                using var parents = UninstallPathGuard.HoldAncestors(_path);
                if (parents.Complete)
                {
                    GuardedStoreFile.RecoverPendingUnderWriteGate(_path);
                    var receipt = pending.Value;
                    try
                    {
                        using var existing = TryOpenStateFile(deleteAccess: true);
                        if (existing is not null) ClearHeldSnapshot(existing, ref receipt, requireWitness: false);
                    }
                    catch (Win32Exception)
                    {
                        // DELETE may be denied while reading the revision that
                        // was actually present at the time of explicit logout.
                        using var readable = TryOpenStateFile();
                        if (readable is not null && ReadOwnedSnapshot(readable, accountUid) is { } bytes)
                        {
                            receipt = receipt with { File = FileSecurityPreservation.GetRevision(readable), Hash = SnapshotHash(bytes) };
                            throw;
                        }
                    }
                    finally { pending = receipt; }
                }
                pending = null;
                _knownSnapshot = null;
            }
            catch (Win32Exception error) { throw new IOException("Не удалось удалить состояние воспроизведения.", error); }
        }
    }

    internal bool RetryClear(ClearRetry pending)
    {
        lock (_gate)
        {
            if (Revisions.GetOrAdd(_path, 0) != pending.Revision) return true;
            try
            {
                using var writer = GuardedStoreFile.HoldWriteGate(_path);
                using var parents = UninstallPathGuard.HoldAncestors(_path);
                if (parents.Complete)
                {
                    GuardedStoreFile.RecoverPendingUnderWriteGate(_path);
                    using var existing = TryOpenStateFile(deleteAccess: true);
                    if (existing is not null && !ClearHeldSnapshot(existing, ref pending, requireWitness: true)) return false;
                }
                _knownSnapshot = null;
                return true;
            }
            catch (Win32Exception error) { throw new IOException("Не удалось удалить состояние воспроизведения.", error); }
        }
    }

    private static byte[]? ReadOwnedSnapshot(SafeFileHandle existing, string accountUid)
    {
        var bytes = GuardedStoreFile.ReadFromHandle(existing, MaximumBytes);
        if (bytes is null) return null;
        try { return ReadSnapshot(bytes)?.AccountUid == accountUid ? bytes : null; }
        catch (JsonException) { return null; }
    }

    private static bool ClearHeldSnapshot(SafeFileHandle existing, ref ClearRetry receipt, bool requireWitness)
    {
        var actual = FileSecurityPreservation.GetRevision(existing);
        if (requireWitness && receipt.File is { } expected && !SameFile(actual, expected)) return true;
        if (ReadOwnedSnapshot(existing, receipt.AccountUid) is not { } bytes) return true;
        var hash = SnapshotHash(bytes);
        if (requireWitness)
        {
            if (receipt.File is null) return false;
            // A newer save can update the same file object in place.
            if (receipt.Hash is { } expectedHash)
            {
                if (hash != expectedHash) return true;
            }
            else if (receipt.File != actual) return false;
        }
        receipt = receipt with { File = actual, Hash = hash };
        FileSecurityPreservation.CheckReplacementMetadata(existing);
        UninstallPathGuard.DeleteByHandle(existing);
        return true;
    }

    private void WriteAtomic(string accountUid, byte[] bytes, bool verifyExisting)
    {
        using var transaction = GuardedStoreFile.BeginWrite(_path);
        if (verifyExisting && transaction.Exists)
            _ = ReadSnapshot(transaction.ReadExisting(MaximumBytes) ?? throw new JsonException());
        transaction.Write(bytes, onPublished: handle => RememberSnapshotFile(accountUid, handle, bytes));
    }

    private void RememberSnapshotFile(string accountUid, SafeFileHandle handle, ReadOnlySpan<byte> bytes)
    {
        _knownSnapshot = null;
        try
        {
            if (FileSecurityPreservation.GetPersistentRevision(handle) is { } file)
                _knownSnapshot = (accountUid, file, SnapshotHash(bytes));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or Win32Exception) { }
    }

    private static bool SameFile(FileSecurityPreservation.FileRevision left, FileSecurityPreservation.FileRevision right)
        => (left.Volume, left.Low, left.High) == (right.Volume, right.Low, right.High);

    private static string SnapshotHash(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    private static PlaybackSnapshot? ReadSnapshot(ReadOnlySpan<byte> bytes)
    {
        if (bytes.SequenceEqual(ClearedMarker)) return null;
        var snapshot = JsonSerializer.Deserialize<PlaybackSnapshot>(bytes, Json) ?? throw new JsonException();
        Validate(snapshot);
        return snapshot;
    }

    internal static bool IsOwnedFile(SafeFileHandle file)
    {
        if (GuardedStoreFile.ReadFromHandle(file, MaximumBytes) is not { } bytes) return false;
        try
        {
            using var json = JsonDocument.Parse(bytes, new JsonDocumentOptions { AllowDuplicateProperties = false, MaxDepth = 16 });
            _ = ReadSnapshot(bytes);
            return true;
        }
        catch (JsonException) { return false; }
    }

    private SafeFileHandle? TryOpenStateFile(bool deleteAccess = false)
    {
        try { return UninstallPathGuard.OpenOwnedFile(_path, read: true, deleteAccess); }
        catch (Win32Exception error) when (error.NativeErrorCode == 2) { return null; }
    }

    public static PlaybackSnapshot Capture(string accountUid, PlaybackQueue queue, double positionSeconds, PlaybackAlbumState? sourceAlbum = null,
        (long Occurrence, TimeSpan Duration)? observedDuration = null)
    {
        ArgumentNullException.ThrowIfNull(queue);
        var tracks = queue.Items.Select(row =>
        {
            var variant = row.IsOfflineLibraryRow ? row.PreferredLocalVariant : default;
            return new PlaybackTrackState(row.Id, row.RequestId, row.Title, row.Artist, row.Album, row.DurationMs,
                row.Available, CopyArtists(row.Artists), row.AlbumId, ArtworkUri.Metadata(row.CoverUri),
                row.PrimaryArtistNameToPersist, row.IsOfflineLibraryRow, variant.RequestId, variant.Codec, variant.Bitrate,
                variant.ConfirmedBitrate, variant.Sha256,
                row.IsOfflineLibraryRow && variant.Sha256 is null ? row.RestoredLocalSha256 : null);
        }).ToArray();
        var index = queue.CurrentIndex;
        // The native bound applies to the position of this occurrence. Keep
        // catalogue duration available for preview detection after reopening.
        long? positionDurationMs = null;
        if (index >= 0 && observedDuration is { } native && native.Occurrence == queue.CurrentOccurrenceToken && native.Duration > TimeSpan.Zero)
            positionDurationMs = (long)Math.Ceiling(Math.Min(MaximumDurationMs, native.Duration.TotalMilliseconds));
        var priorities = queue.CapturePlayNextIndices();
        var snapshot = new PlaybackSnapshot(CurrentVersion, accountUid, DateTimeOffset.UtcNow, Array.AsReadOnly(tracks), index,
            index < 0 ? 0 : ClampPosition(positionSeconds, positionDurationMs ?? tracks[index].DurationMs), queue.Shuffle, queue.Repeat, sourceAlbum,
            priorities is null ? null : Array.AsReadOnly(priorities), positionDurationMs);
        Validate(snapshot);
        return snapshot;
    }

    public static RestoredPlaybackQueue RestoreQueue(PlaybackSnapshot snapshot, OfflineLibrary offline, bool localOnly = false)
    {
        ArgumentNullException.ThrowIfNull(snapshot); ArgumentNullException.ThrowIfNull(offline);
        Validate(snapshot);
        var rows = new List<TrackRow>(snapshot.Tracks.Count);
        var mappedIndices = snapshot.PlayNextIndices is { Count: > 0 }
            ? Enumerable.Repeat(-1, snapshot.Tracks.Count).ToArray() : null;
        var currentIndex = -1;
        for (var i = 0; i < snapshot.Tracks.Count; i++)
        {
            var metadata = snapshot.Tracks[i];
            var probe = ToRow(metadata, rows.Count + 1, available: metadata.Available);
            // Only the library's validated index can supply a local path. The
            // snapshot has no path field and cannot open an arbitrary file.
            var local = offline.FindCheckpointPlaybackItem(probe, out var exactQuality);
            if (localOnly && local is null) continue;
            // Editions may share audio but have different covers.
            var offlineCover = probe.CoverUri is null
                ? offline.FindPlaybackArtwork(metadata.Id, ArtworkAlbumId(metadata.Id, metadata.RequestId, metadata.AlbumId), local)
                : null;
            var selected = exactQuality ? local : null;
            var row = ToRow(metadata, rows.Count + 1, available: !localOnly && metadata.Available,
                selected is null ? null : offline.ResolvePath(selected.FileName),
                selected is null ? null : OfflineLibrary.QualityLabel(selected).Replace("-MP4", ""), offlineCover,
                metadata.OfflineOnly && metadata.LocalSourceRequestId is not null && metadata.LocalSourceCodec is not null
                    && metadata.LocalSourceBitrate is not null && metadata.LocalSourceSha256 is null ? selected?.Sha256 : null);
            row.HasOfflineQueueVariant = metadata.OfflineOnly && local is not null && selected is null;
            if (i == snapshot.CurrentIndex) currentIndex = rows.Count;
            if (mappedIndices is not null) mappedIndices[i] = rows.Count;
            rows.Add(row);
        }
        var priorities = mappedIndices is not null && snapshot.PlayNextIndices is { } indices
            ? Array.AsReadOnly(indices.Select(index => mappedIndices[index]).Where(index => index >= 0).ToArray()) : null;
        var positionDurationMs = currentIndex >= 0 ? snapshot.PositionDurationMs : null;
        return new(Array.AsReadOnly(rows.ToArray()), currentIndex,
            currentIndex < 0 ? 0 : ClampPosition(snapshot.PositionSeconds, positionDurationMs ?? rows[currentIndex].DurationMs),
            priorities, positionDurationMs);
    }

    internal static double ClampPosition(double seconds, long durationMs)
        => !double.IsFinite(seconds) ? 0 : Math.Clamp(seconds, 0, (durationMs > 0 ? durationMs : MaximumDurationMs) / 1000.0);

    // Callers validate that RequestId is the bare Id or Id:AlbumId.
    internal static string? ArtworkAlbumId(string id, string requestId, string? albumId)
        => requestId == id ? albumId : requestId[(id.Length + 1)..];

    private static TrackRow ToRow(PlaybackTrackState track, int number, bool available, string? path = null,
        string? quality = null, string? offlineCover = null, string? restoredLocalSha256 = null)
        => new()
        {
            Number = number, Id = track.Id, RequestId = track.RequestId, Title = track.Title, Artist = track.Artist,
            Artists = CopyArtists(track.Artists), PrimaryArtistName = track.PrimaryArtistName,
            Album = track.Album, AlbumId = track.AlbumId, DurationMs = track.DurationMs,
            CoverUri = ArtworkUri.Metadata(track.CoverUri) ?? ArtworkUri.Metadata(offlineCover),
            Available = track.OfflineOnly ? path is not null : available, DownloadedPath = path, DownloadedQuality = quality,
            IsOfflineLibraryRow = track.OfflineOnly,
            LocalSourceRequestId = track.OfflineOnly ? track.LocalSourceRequestId : null,
            LocalSourceCodec = track.OfflineOnly ? track.LocalSourceCodec : null,
            LocalSourceBitrate = track.OfflineOnly ? track.LocalSourceBitrate : null,
            LocalSourceConfirmedBitrate = track.OfflineOnly ? track.LocalSourceConfirmedBitrate : null,
            LocalSourceSha256 = track.OfflineOnly ? track.LocalSourceSha256 : null,
            RestoredLocalSha256 = track.ExpectedLocalSourceSha256 ?? restoredLocalSha256,
            LocalPath = track.OfflineOnly && track.LocalSourceRequestId is not null ? path : null,
            LocalQuality = track.OfflineOnly && track.LocalSourceRequestId is not null ? quality : null
        };
    private static IReadOnlyList<ArtistReference> CopyArtists(IReadOnlyList<ArtistReference> artists)
        => Array.AsReadOnly(artists.Select(artist => new ArtistReference(artist.Id, artist.Name)).ToArray());
    private static PlaybackSnapshot Copy(PlaybackSnapshot snapshot)
        => snapshot with
        {
            Tracks = Array.AsReadOnly(snapshot.Tracks.Select(track => track with { Artists = CopyArtists(track.Artists) }).ToArray()),
            PlayNextIndices = snapshot.PlayNextIndices is { } priorities ? Array.AsReadOnly(priorities.ToArray()) : null,
            PositionSeconds = snapshot.CurrentIndex < 0 ? 0
                : ClampPosition(snapshot.PositionSeconds, snapshot.PositionDurationMs ?? snapshot.Tracks[snapshot.CurrentIndex].DurationMs)
        };

    private static void Validate(PlaybackSnapshot snapshot)
    {
        if (snapshot.Version != CurrentVersion || !ValidId(snapshot.AccountUid) || snapshot.SavedUtc == default
            || snapshot.Tracks is null || snapshot.Tracks.Count > MaximumTracks
            || snapshot.CurrentIndex < -1 || snapshot.CurrentIndex >= snapshot.Tracks.Count
            || !double.IsFinite(snapshot.PositionSeconds) || snapshot.PositionSeconds < 0 || !Enum.IsDefined(snapshot.Repeat)
            || snapshot.PositionDurationMs is <= 0 or > MaximumDurationMs
            || snapshot.CurrentIndex < 0 && snapshot.PositionDurationMs is not null)
            throw new JsonException("Некорректное сохранённое состояние воспроизведения.");
        if (snapshot.SourceAlbum is { } album && (!MusicApi.IsAlbumWaveSeed(album.Id) || !ValidText(album.Title) || !ValidText(album.Artist)))
            throw new JsonException("Некорректный источник альбомной очереди.");
        if (snapshot.PlayNextIndices is { Count: > 0 } indices && (!snapshot.Shuffle || indices.Count > snapshot.Tracks.Count
            || indices.Any(index => index < 0 || index >= snapshot.Tracks.Count || index == snapshot.CurrentIndex)
            || indices.Distinct().Count() != indices.Count))
            throw new JsonException("Некорректная очерёдность следующих треков.");
        foreach (var track in snapshot.Tracks)
        {
            if (track is null || !ValidId(track.Id) || !ValidRequestId(track.RequestId, track.Id)
                || track.AlbumId is not null && !ValidId(track.AlbumId)
                || track.LocalSourceRequestId is { } saved && (!track.OfflineOnly || !ValidRequestId(saved, track.Id))
                || (track.LocalSourceCodec is null) != (track.LocalSourceBitrate is null)
                || track.LocalSourceCodec is { } codec && (track.LocalSourceRequestId is null || !ValidId(codec) || track.LocalSourceBitrate < 0)
                || track.LocalSourceConfirmedBitrate is { } confirmed && (track.LocalSourceBitrate is null || confirmed <= 0)
                || track.LocalSourceSha256 is { } sha256 && (track.LocalSourceBitrate is null || sha256.Length != 64 || !sha256.All(Uri.IsHexDigit))
                || track.ExpectedLocalSourceSha256 is { } expected && (track.LocalSourceBitrate is null || track.LocalSourceSha256 is not null
                    || expected.Length != 64 || !expected.All(Uri.IsHexDigit))
                || track.CoverUri is not null && ArtworkUri.Metadata(track.CoverUri) is null
                || !ValidText(track.Title) || !ValidText(track.Artist) || !ValidText(track.Album)
                || !TrackRow.ValidPrimaryArtistName(track.PrimaryArtistName)
                || track.DurationMs < 0 || track.DurationMs > MaximumDurationMs || track.Artists is null || track.Artists.Count > 100)
                throw new JsonException("Некорректные метаданные сохранённого трека.");
            foreach (var artist in track.Artists)
                if (artist is null || !ValidId(artist.Id) || !ValidText(artist.Name)) throw new JsonException();
        }
    }

    private static bool ValidId(string? id) => id is { Length: > 0 and <= 512 }
        && id.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');
    internal static bool ValidRequestId(string? requestId, string id)
    {
        if (requestId == id) return true;
        if (requestId is null || requestId.Length > 1025 || !requestId.StartsWith(id + ":", StringComparison.Ordinal)) return false;
        return ValidId(requestId[(id.Length + 1)..]);
    }
    private static bool ValidText(string? value) => value is { Length: <= 4096 } && !value.Any(char.IsControl);

    private sealed class BoundedJsonStream : MemoryStream
    {
        public bool LimitExceeded { get; private set; }
        public override void Write(byte[] buffer, int offset, int count)
        { CheckLength(count); base.Write(buffer, offset, count); }
        public override void Write(ReadOnlySpan<byte> buffer)
        { CheckLength(buffer.Length); base.Write(buffer); }
        public override void WriteByte(byte value)
        { CheckLength(1); base.WriteByte(value); }
        private void CheckLength(int count)
        {
            if (Length + count <= MaximumBytes) return;
            LimitExceeded = true;
            throw new JsonException("Очередь слишком велика для сохранения.");
        }
    }
}
