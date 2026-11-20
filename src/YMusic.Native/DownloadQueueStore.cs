using System.Collections.Concurrent;
using System.ComponentModel;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32.SafeHandles;
using System.Text.Json.Serialization.Metadata;

namespace YMusic.Native;

// Metadata only. No local paths or session/audio data enter this schema.
// Playlist occurrences may carry validated public artwork for exact editions.
internal sealed record DownloadEntrySnapshot(
    [property: JsonRequired] PlaybackTrackState Track, [property: JsonRequired] DownloadEntryState State,
    [property: JsonRequired] int Attempts, [property: JsonRequired] string Error,
    [property: JsonRequired] bool RemovedFromCollection, [property: JsonRequired] bool SkipFavoritesDownload,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] AudioQuality? AutomaticFavoriteQuality = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DeferredFavoriteState? DeferredFavorite = null);
internal sealed record DownloadPlaylistSnapshot(
    [property: JsonRequired] string Owner, [property: JsonRequired] string Kind,
    [property: JsonRequired] string Title, [property: JsonRequired] int Revision,
    [property: JsonRequired] IReadOnlyList<string> TrackIds,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool HistoryOnly = false,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<DownloadPlaylistOccurrence>? Occurrences = null);
internal sealed record DownloadPlaylistOccurrence([property: JsonRequired] int Position, [property: JsonRequired] string RequestId,
    [property: JsonRequired] string? AlbumId, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? CoverUri,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DownloadPlaylistOccurrenceMetadata? Metadata = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool MetadataRecorded = false);
internal sealed record DownloadPlaylistOccurrenceMetadata(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Title,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Artist,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Album,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? DurationMs,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? Available,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<ArtistReference>? Artists,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? PrimaryArtistName);
internal sealed record DownloadQueueSnapshot(
    [property: JsonRequired] int Version, [property: JsonRequired] string AccountUid,
    [property: JsonRequired] DateTimeOffset SavedUtc, [property: JsonRequired] string Label,
    [property: JsonRequired] AudioQuality Quality, [property: JsonRequired] bool Automatic,
    [property: JsonRequired] bool FavoritesBatch, [property: JsonRequired] IReadOnlyList<DownloadEntrySnapshot> Entries,
    DownloadPlaylistSnapshot? Playlist = null);
internal sealed record RestoredDownloadEntry(TrackRow Track, DownloadEntryState State, long Bytes, int Attempts,
    string Error, bool RemovedFromCollection, bool SkipFavoritesDownload, AudioQuality? AutomaticFavoriteQuality = null,
    DeferredFavoriteState? DeferredFavorite = null, bool ResumeAutomaticallyOnStartup = false);
internal sealed record RestoredDownloadQueue(string Label, AudioQuality Quality, bool Automatic, bool FavoritesBatch,
    IReadOnlyList<RestoredDownloadEntry> Entries, PlaylistDetails? Playlist, bool PlaylistHistoryOnly = false, bool PlaylistOccurrencesRecorded = false,
    DownloadPlaylistPersistence? PlaylistPersistence = null);
internal sealed record DownloadPlaylistPersistence(PlaylistDetails Playlist, int Version, IReadOnlySet<TrackRow> MarkedV2Rows);
internal enum DownloadQueueReadStatus { Missing, Disabled, Loaded, OtherAccount, Invalid, Unavailable, AccessDenied, UnsafePath }
internal sealed record DownloadQueueReadResult(DownloadQueueSnapshot? Snapshot, DownloadQueueReadStatus Status)
{
    internal DownloadQueueStore.ProtectedPublication? Replacement { get; init; }
}

internal sealed class DownloadQueueStore
{
    internal const string FileName = "download-queue.json";
    internal const int CurrentVersion = 3, MaximumEntries = 10_000, MaximumBytes = 8 * 1024 * 1024;
    internal const int MaximumAttempts = 1_000_000;
    private const int LegacyVersion = 1, PriorCompactVersion = 2;
    private static ReadOnlySpan<byte> ClearedQueue => "{\"version\":0}"u8;
    private static readonly ConcurrentDictionary<string, StoreState> States = new(StringComparer.OrdinalIgnoreCase);
    private static readonly JsonSerializerOptions LegacyJson = new()
    { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, MaxDepth = 16 };
    private static readonly JsonSerializerOptions CompactJson = new(LegacyJson)
    { TypeInfoResolver = new DefaultJsonTypeInfoResolver { Modifiers = { ConfigureCompactProperties } } };
    private readonly string _path;
    private readonly StoreState _state;
    public DownloadQueueReadStatus LastReadStatus { get; private set; }

    internal DownloadQueueStore(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        _path = Path.Combine(Path.GetFullPath(directory), FileName);
        _state = States.GetOrAdd(_path, _ => new());
    }
    internal DownloadQueueSnapshot? Load(string? accountUid)
    {
        var result = Read(accountUid);
        LastReadStatus = result.Status;
        return result.Snapshot;
    }
    internal DownloadQueueReadResult Read(string? accountUid)
    {
        if (!ValidId(accountUid)) return new(null, DownloadQueueReadStatus.Disabled);
        lock (_state)
        {
            try
            {
                using var parents = UninstallPathGuard.HoldAncestors(_path);
                using var handle = parents.Complete ? GuardedStoreFile.OpenForRead(_path) : null;
                if (handle is null) return new(null, DownloadQueueReadStatus.Missing);
                using var stream = new FileStream(handle, FileAccess.Read);
                var length = stream.Length;
                if (length > MaximumBytes) throw new JsonException();
                var bytes = new byte[(int)length]; stream.ReadExactly(bytes);
                if (bytes.AsSpan().SequenceEqual(ClearedQueue)) return new(null, DownloadQueueReadStatus.Missing);
                var snapshot = ReadSnapshot(bytes);
                Validate(snapshot);
                if (snapshot.AccountUid != accountUid)
                    return new(null, DownloadQueueReadStatus.OtherAccount)
                    { Replacement = new() { AuthorizedAccountUid = accountUid, ObservedForeign = ObserveCheckpoint(snapshot.AccountUid, handle, bytes) } };
                RememberCheckpoint(snapshot.AccountUid, handle, bytes);
                return new(Copy(snapshot), DownloadQueueReadStatus.Loaded);
            }
            catch (JsonException) { return new(null, DownloadQueueReadStatus.Invalid); }
            catch (UninstallPathGuard.UnsafeStoragePathException) { return new(null, DownloadQueueReadStatus.UnsafePath); }
            catch (Exception error) when (IsAccessDenied(error))
            { return new(null, DownloadQueueReadStatus.AccessDenied); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or Win32Exception)
            { return new(null, DownloadQueueReadStatus.Unavailable); }
        }
    }
    internal static bool IsAccessDenied(Exception error) => error is UnauthorizedAccessException or Win32Exception { NativeErrorCode: 5 or 1314 }
        || error is IOException { InnerException: Win32Exception { NativeErrorCode: 5 or 1314 } };
    internal sealed class ProtectedPublication
    {
        internal string? AuthorizedAccountUid { get; init; }
        internal CheckpointWitness? ObservedForeign { get; init; }
        internal CheckpointWitness? Published { get; set; }
        internal volatile bool Blocked;
    }

    internal sealed record CheckpointWitness(string AccountUid, FileSecurityPreservation.FileRevision File, string Hash);

    internal bool Save(DownloadQueueSnapshot snapshot, ProtectedPublication? protection = null, bool explicitBatch = false)
    {
        var frozen = Freeze(snapshot); long revision;
        lock (_state) revision = ++_state.Revision;
        return SaveCore(frozen, revision, protection, explicitBatch);
    }
    internal Task<bool> SaveInBackgroundAsync(DownloadQueueSnapshot snapshot, ProtectedPublication? protection = null, bool explicitBatch = false)
    {
        var frozen = Freeze(snapshot); long revision;
        // Reserve at admission, not when a worker happens to run. A later save,
        // clear or close always invalidates every older outstanding write.
        lock (_state) revision = ++_state.Revision;
        return Task.Run(() => SaveCore(frozen, revision, protection, explicitBatch));
    }
    private bool SaveCore(DownloadQueueSnapshot snapshot, long revision, ProtectedPublication? protection, bool explicitBatch)
    {
        using var stream = new BoundedJsonStream();
        var version = snapshot.Version == LegacyVersion ? CurrentVersion : snapshot.Version;
        JsonSerializer.Serialize(stream, snapshot with { Version = version }, CompactJson);
        var bytes = stream.ToArray();
        lock (_state)
        {
            if (revision != _state.Revision) return false;
            using var storage = GuardedStoreFile.BeginWrite(_path);
            if (storage.Exists)
            {
                var existing = storage.ReadExisting(MaximumBytes);
                if (existing is null || !existing.AsSpan().SequenceEqual(ClearedQueue))
                {
                    string? allowedForeignOwner = null;
                    if (protection is not null)
                    {
                        // An explicit batch may replace the exact checkpoint observed at
                        // account activation, never an intervening publication.
                        var expected = protection.Published;
                        if (expected is not null && expected.AccountUid != snapshot.AccountUid) return false;
                        if (expected is null && protection.ObservedForeign is { } foreign)
                        {
                            if (!explicitBatch || snapshot.Entries.Count == 0 || protection.AuthorizedAccountUid != snapshot.AccountUid)
                                return false;
                            expected = foreign;
                        }
                        if (existing is null || expected is null || SnapshotHash(existing) != expected.Hash)
                        { protection.Blocked = true; return false; }
                        using var current = storage.OpenExisting();
                        if (current is null || !SameFile(FileSecurityPreservation.GetRevision(current), expected.File))
                        { protection.Blocked = true; return false; }
                        allowedForeignOwner = expected.AccountUid;
                    }
                    if (existing is not null)
                    {
                        try
                        {
                            var existingOwner = ReadSnapshot(existing).AccountUid;
                            if (existingOwner != snapshot.AccountUid && existingOwner != allowedForeignOwner) return false;
                        }
                        catch (JsonException) { } // A damaged queue remains subject to the window's admission decision.
                    }
                }
            }
            using var staged = storage.Stage(bytes);
            var published = false;
            try
            {
                storage.Commit(staged);
                published = true;
                _state.PublishedRevision = revision;
                RememberCheckpoint(snapshot.AccountUid, staged, bytes);
                if (protection is not null) protection.Published = _knownCheckpoint;
            }
            finally { if (!published) UninstallPathGuard.DiscardStagedFile(staged); }
            return true;
        }
    }

    private static DownloadQueueSnapshot ReadSnapshot(ReadOnlySpan<byte> bytes)
    {
        var reader = new Utf8JsonReader(bytes, new JsonReaderOptions { MaxDepth = LegacyJson.MaxDepth });
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) throw new JsonException();
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            var isVersion = reader.ValueTextEquals("version"u8);
            if (!reader.Read()) throw new JsonException();
            if (!isVersion) { reader.Skip(); continue; }
            if (reader.TokenType != JsonTokenType.Number || !reader.TryGetInt32(out var version)) throw new JsonException();
            var json = version switch
            {
                LegacyVersion => LegacyJson,
                PriorCompactVersion or CurrentVersion => CompactJson,
                _ => throw new JsonException()
            };
            var snapshot = JsonSerializer.Deserialize<DownloadQueueSnapshot>(bytes, json);
            if (snapshot?.Version != version) throw new JsonException();
            return snapshot;
        }
        throw new JsonException();
    }

    private static void ConfigureCompactProperties(JsonTypeInfo type)
    {
        if (type.Type == typeof(DownloadEntrySnapshot))
        {
            // The shortened keys make room for seven attempt digits and both
            // longer boolean values, even if v1 held only an empty error string.
            foreach (var property in type.Properties)
            {
                if (property.Name == "error") property.CustomConverter = new DownloadReasonCodeConverter();
                property.Name = property.Name switch
                {
                    "state" => "s", "attempts" => "a", "error" => "e",
                    "removedFromCollection" => "r", "skipFavoritesDownload" => "x", _ => property.Name
                };
            }
        }
        else if (type.Type == typeof(DownloadQueueSnapshot))
        {
            // The header also covers a longer UTC timestamp and a newly set
            // history flag, without taking space from the v1 track metadata.
            foreach (var property in type.Properties)
                property.Name = property.Name switch { "savedUtc" => "t", "favoritesBatch" => "f", _ => property.Name };
        }
        else if (type.Type == typeof(DownloadPlaylistSnapshot))
        {
            foreach (var property in type.Properties)
                if (property.Name == "historyOnly") property.Name = "h";
        }
    }

    private sealed class DownloadReasonCodeConverter : JsonConverter<string>
    {
        public override string Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
            => reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out var code)
                ? DownloadFailure.CheckpointReasonText(code) : throw new JsonException();

        public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options)
            => writer.WriteNumberValue(DownloadFailure.CheckpointReasonCode(value));
    }
    internal void Clear()
    {
        lock (_state)
        {
            ++_state.Revision;
            // A published tombstone prevents restoration even if deleting it fails.
            using var storage = GuardedStoreFile.BeginWrite(_path, createParents: false);
            storage.Write(ClearedQueue, clear: true, retainClearMarkerOnDeleteFailure: true);
            _knownCheckpoint = null;
        }
    }

    internal readonly record struct ClearRetry(string Path, string AccountUid, long Revision,
        FileSecurityPreservation.FileRevision? File, string? Hash = null);

    private CheckpointWitness? _knownCheckpoint;

    private void RememberCheckpoint(string accountUid, SafeFileHandle handle, ReadOnlySpan<byte> bytes)
        => _knownCheckpoint = ObserveCheckpoint(accountUid, handle, bytes);

    private static CheckpointWitness? ObserveCheckpoint(string accountUid, SafeFileHandle handle, ReadOnlySpan<byte> bytes)
    {
        try { return new(accountUid, FileSecurityPreservation.GetRevision(handle), SnapshotHash(bytes)); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or Win32Exception)
        { return null; }
    }

    internal void Clear(string accountUid, out ClearRetry? pending, bool authorizeCurrent = false)
    {
        if (!ValidId(accountUid)) throw new ArgumentException("Не указан владелец очереди загрузок.", nameof(accountUid));
        lock (_state)
        {
            var known = _knownCheckpoint is { } observed && observed.AccountUid == accountUid ? _knownCheckpoint : null;
            pending = new(_path, accountUid, ++_state.Revision, known?.File, known?.Hash);
            try
            {
                using var writer = GuardedStoreFile.HoldWriteGate(_path);
                using var parents = UninstallPathGuard.HoldAncestors(_path);
                if (parents.Complete)
                {
                    GuardedStoreFile.RecoverPendingUnderWriteGate(_path);
                    if (known is null && !authorizeCurrent)
                    {
                        using var metadata = TryOpenQueueFile(read: false, deleteAccess: false);
                        if (metadata is not null) pending = pending.Value with { File = FileSecurityPreservation.GetRevision(metadata) };
                    }
                    var receipt = pending.Value;
                    try
                    {
                        if (!TryClearCurrentFile(ref receipt, authorizeCurrent)) throw new IOException("Не удалось подтвердить прежнюю очередь загрузок.");
                    }
                    catch (Win32Exception) when (authorizeCurrent)
                    {
                        // The existing reader may allow an exact evidence read but
                        // refuse DELETE. A new proof takes precedence, but a read
                        // failure leaves the last exact proof available for retry.
                        if (ObserveExplicitClear(ref receipt)) throw;
                    }
                    finally { pending = receipt; }
                }
                _knownCheckpoint = null;
                pending = null;
            }
            catch (Win32Exception error) { throw new IOException("Не удалось удалить очередь загрузок.", error); }
        }
    }

    private bool ObserveExplicitClear(ref ClearRetry receipt)
    {
        using var current = TryOpenQueueFile(read: true, deleteAccess: false);
        if (current is null) return false;
        var bytes = GuardedStoreFile.ReadFromHandle(current, MaximumBytes);
        if (bytes is null || bytes.AsSpan().SequenceEqual(ClearedQueue)) return false;
        try
        {
            var snapshot = ReadSnapshot(bytes);
            Validate(snapshot);
            if (snapshot.AccountUid != receipt.AccountUid) return false;
        }
        catch (JsonException) { return false; }
        receipt = receipt with { File = FileSecurityPreservation.GetRevision(current), Hash = SnapshotHash(bytes) };
        return true;
    }

    internal bool RetryClear(ClearRetry pending)
    {
        if (!string.Equals(pending.Path, _path, StringComparison.OrdinalIgnoreCase)) return false;
        lock (_state)
        {
            if (_state.PublishedRevision > pending.Revision) return true;
            try
            {
                using var writer = GuardedStoreFile.HoldWriteGate(_path);
                using var parents = UninstallPathGuard.HoldAncestors(_path);
                if (parents.Complete)
                {
                    GuardedStoreFile.RecoverPendingUnderWriteGate(_path);
                    if (!TryClearCurrentFile(ref pending)) return false;
                }
                if (_knownCheckpoint is { } known && known.AccountUid == pending.AccountUid && known.File == pending.File)
                    _knownCheckpoint = null;
                return true;
            }
            catch (Win32Exception error) { throw new IOException("Не удалось удалить очередь загрузок.", error); }
        }
    }

    private bool TryClearCurrentFile(ref ClearRetry pending, bool authorizeCurrent = false)
    {
        using var file = TryOpenQueueFile(read: true, deleteAccess: true);
        if (file is null) return true;
        var current = FileSecurityPreservation.GetRevision(file);
        if (!authorizeCurrent && pending.File is { } expected && !SameFile(current, expected)) return true;
        var bytes = GuardedStoreFile.ReadFromHandle(file, MaximumBytes);
        if (bytes is null || bytes.AsSpan().SequenceEqual(ClearedQueue)) return true;
        DownloadQueueSnapshot snapshot;
        try
        {
            snapshot = ReadSnapshot(bytes);
            Validate(snapshot);
        }
        catch (JsonException) { return true; }
        if (snapshot.AccountUid != pending.AccountUid) return true;
        // A rename can change the filesystem's ChangeTime without changing the
        // object or its contents. Only matching bytes may authenticate that case.
        var hash = SnapshotHash(bytes);
        if (!authorizeCurrent)
        {
            if (pending.File is null) return false;
            if (pending.Hash is { } expectedHash && hash != expectedHash) return true;
            if (pending.Hash is null && current != pending.File) return false;
        }
        pending = pending with { File = current, Hash = hash };
        FileSecurityPreservation.CheckReplacementMetadata(file);
        UninstallPathGuard.DeleteByHandle(file);
        return true;
    }

    internal static bool IsOwnedFile(SafeFileHandle file)
    {
        if (GuardedStoreFile.ReadFromHandle(file, MaximumBytes) is not { } bytes) return false;
        if (bytes.AsSpan().SequenceEqual(ClearedQueue)) return true;
        try
        {
            using var json = JsonDocument.Parse(bytes, new JsonDocumentOptions { AllowDuplicateProperties = false, MaxDepth = 16 });
            Validate(ReadSnapshot(bytes));
            return true;
        }
        catch (JsonException) { return false; }
    }

    private static bool SameFile(FileSecurityPreservation.FileRevision left, FileSecurityPreservation.FileRevision right)
        => (left.Volume, left.Low, left.High) == (right.Volume, right.Low, right.High);

    private static string SnapshotHash(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    private SafeFileHandle? TryOpenQueueFile(bool read, bool deleteAccess)
    {
        try { return UninstallPathGuard.OpenOwnedFile(_path, read, deleteAccess); }
        catch (Win32Exception error) when (error.NativeErrorCode == 2) { return null; }
    }
    internal static DownloadQueueSnapshot Capture(string accountUid, DownloadQueueState queue, AudioQuality quality,
        bool automatic, PlaylistDetails? playlist, bool playlistHistoryOnly = false, bool recordPlaylistOccurrences = true,
        DownloadPlaylistPersistence? playlistPersistence = null)
    {
        queue.VerifyAccess();
        return Capture(accountUid, queue.Entries, queue.Label, quality, automatic, queue.FavoritesBatch, playlist, playlistHistoryOnly,
            recordPlaylistOccurrences, playlistPersistence);
    }

    internal static bool CanAdmit(string accountUid, IEnumerable<DownloadEntry> proposed, string label, AudioQuality quality,
        bool automatic, bool favoritesBatch, PlaylistDetails? playlist, bool recordPlaylistOccurrences = true,
        DownloadPlaylistPersistence? playlistPersistence = null)
    {
        if (playlist?.Tracks.Count > MaximumEntries) return false;
        try
        {
            var entries = proposed.Take(MaximumEntries + 1).ToArray();
            if (entries.Length > MaximumEntries) return false;
            var snapshot = Capture(accountUid, entries, label, quality, automatic, favoritesBatch, playlist,
                playlistHistoryOnly: true, recordPlaylistOccurrences, playlistPersistence);
            // Reserve the longest serialized values an admitted entry can reach
            // during processing or retry, before such a state needs a checkpoint.
            var largest = snapshot with
            {
                SavedUtc = DateTimeOffset.UnixEpoch.AddTicks(1),
                Entries = snapshot.Entries.Select(entry => entry with
                {
                    Attempts = MaximumAttempts,
                    Error = DownloadFailure.HighestCheckpointReason,
                    RemovedFromCollection = false,
                    SkipFavoritesDownload = false
                }).ToArray()
            };
            using var stream = new BoundedJsonStream(measureOnly: true);
            JsonSerializer.Serialize(stream, largest, CompactJson);
            return true;
        }
        catch (JsonException) { return false; }
    }

    private static DownloadQueueSnapshot Capture(string accountUid, IEnumerable<DownloadEntry> queueEntries, string label,
        AudioQuality quality, bool automatic, bool favoritesBatch, PlaylistDetails? playlist, bool playlistHistoryOnly,
        bool recordPlaylistOccurrences, DownloadPlaylistPersistence? playlistPersistence)
    {
        // Shutdown pauses the live workers. Save their automatic work as queued,
        // so the checkpoint still distinguishes an interruption from a user pause.
        var entries = queueEntries.Select(entry => new DownloadEntrySnapshot(ToSavedTrack(entry.Track),
            entry.ResumeAutomaticallyOnStartup ? DownloadEntryState.Queued : entry.State,
            entry.Attempts, DownloadFailure.RestorableReason(entry.Error), entry.RemovedFromCollection,
                entry.SkipFavoritesDownload, entry.AutomaticFavoriteQuality, entry.DeferredFavorite)).ToArray();
        playlist = playlistPersistence?.Playlist ?? playlist;
        var context = playlist is null ? null : new DownloadPlaylistSnapshot(playlist.Owner, playlist.Kind, playlist.Title,
            playlist.Revision, Array.AsReadOnly(playlist.Tracks.Select(track => track.Id).ToArray()), playlistHistoryOnly,
            recordPlaylistOccurrences ? CapturePlaylistOccurrences(playlist, entries, playlistPersistence?.MarkedV2Rows) : null);
        var snapshot = new DownloadQueueSnapshot(playlistPersistence?.Version ?? CurrentVersion, accountUid, DateTimeOffset.UtcNow, label,
            quality, automatic, favoritesBatch, Array.AsReadOnly(entries), context);
        Validate(snapshot); return snapshot;
    }
    internal static PlaybackTrackState ToSavedTrack(TrackRow track) => new(track.Id, track.RequestId, track.Title,
        track.Artist, track.Album, track.DurationMs, track.Available, CopyArtists(track.Artists), track.AlbumId,
        PrimaryArtistName: track.PrimaryArtistNameToPersist);

    internal static TrackRow ToTrackRow(PlaybackTrackState track, int number, string? cover = null) => new()
    {
        Number = number, Id = track.Id, RequestId = track.RequestId, Title = track.Title, Artist = track.Artist,
        Artists = CopyArtists(track.Artists), PrimaryArtistName = track.PrimaryArtistName, Album = track.Album,
        AlbumId = track.AlbumId, DurationMs = track.DurationMs, Available = track.Available, CoverUri = cover
    };
    private static IReadOnlyList<DownloadPlaylistOccurrence> CapturePlaylistOccurrences(PlaylistDetails playlist, DownloadEntrySnapshot[] entries,
        IReadOnlySet<TrackRow>? markedV2Rows)
    {
        var byId = new Dictionary<string, PlaybackTrackState>(StringComparer.Ordinal);
        foreach (var entry in entries)
            if (!byId.TryAdd(entry.Track.Id, entry.Track)) throw new JsonException();
        var repeated = playlist.Tracks.GroupBy(track => track.Id, StringComparer.Ordinal)
            .Where(group => group.Skip(1).Any()).Select(group => group.Key).ToHashSet(StringComparer.Ordinal);
        var occurrences = new List<DownloadPlaylistOccurrence>();
        for (var i = 0; i < playlist.Tracks.Count; i++)
        {
            var track = playlist.Tracks[i];
            if (!byId.TryGetValue(track.Id, out var primary)) throw new JsonException();
            var changedEdition = track.RequestId != primary.RequestId || track.AlbumId != primary.AlbumId;
            var cover = changedEdition || repeated.Contains(track.Id) ? ArtworkUri.Metadata(track.CoverUri) : null;
            DownloadPlaylistOccurrenceMetadata? metadata = new(
                track.Title == primary.Title ? null : track.Title, track.Artist == primary.Artist ? null : track.Artist,
                track.Album == primary.Album ? null : track.Album, track.DurationMs == primary.DurationMs ? null : track.DurationMs,
                track.Available == primary.Available ? null : track.Available,
                track.Artists.SequenceEqual(primary.Artists) ? null : CopyArtists(track.Artists),
                track.PrimaryArtistNameToPersist == primary.PrimaryArtistName ? null : track.PrimaryArtistNameToPersist ?? "");
            if (metadata is { Title: null, Artist: null, Album: null, DurationMs: null, Available: null, Artists: null, PrimaryArtistName: null })
                metadata = null;
            if (changedEdition || cover is not null || metadata is not null)
                occurrences.Add(new(i, track.RequestId, track.AlbumId, cover, metadata, markedV2Rows?.Contains(track) == true));
        }
        return Array.AsReadOnly(occurrences.ToArray());
    }
    internal static RestoredDownloadQueue PrepareRestore(DownloadQueueSnapshot snapshot, OfflineLibrary offline)
        => PrepareRestoreAsync(snapshot, offline, CancellationToken.None).GetAwaiter().GetResult();

    internal static async Task<RestoredDownloadQueue> PrepareRestoreAsync(DownloadQueueSnapshot snapshot, OfflineLibrary offline,
        CancellationToken cancellationToken)
    {
        Validate(snapshot); ArgumentNullException.ThrowIfNull(offline);
        var remembered = snapshot.Playlist is { HistoryOnly: false } context
            ? offline.GetPlaylists().FirstOrDefault(item => item.Owner == context.Owner && item.Kind == context.Kind) : null;
        var covers = RememberedPlaylistCovers(remembered, snapshot.Playlist);
        var entries = new List<RestoredDownloadEntry>(snapshot.Entries.Count);
        foreach (var batch in snapshot.Entries.Chunk(OfflineLibrary.DownloadVerificationBatchSize))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var firstNumber = entries.Count + 1;
            var rows = batch.Select((saved, i) =>
            {
                var t = saved.Track;
                return ToTrackRow(t, firstNumber + i, covers.GetValueOrDefault((t.Id, t.RequestId, t.AlbumId)));
            }).ToArray();
            var requests = batch.Select((saved, i) => (Saved: saved, Row: rows[i]))
                .Where(entry => entry.Saved.State != DownloadEntryState.Removed)
                .Select(entry => new OfflineLibrary.DownloadCacheRequest(entry.Row, item =>
                    entry.Saved.State == DownloadEntryState.Completed || snapshot.Automatic
                    || AudioQualityPolicy.IsSufficient(item.Codec, item.QualityBitrate, entry.Saved.AutomaticFavoriteQuality ?? snapshot.Quality)
                    || entry.Saved.AutomaticFavoriteQuality is not null)).ToArray();
            using var verified = requests.Length > 0 && offline.IsAvailable
                ? await offline.HoldDownloadCacheBatchAsync(requests, cancellationToken).ConfigureAwait(false) : null;
            var nextProof = 0;
            for (var i = 0; i < batch.Length; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var saved = batch[i]; var row = rows[i];
                var proof = verified is not null && saved.State != DownloadEntryState.Removed ? verified[nextProof++] : default;
                if (proof.Unavailable is not null) throw proof.Unavailable;
                // Verified audio committed before a crash remains usable, even
                // if the last checkpoint still shows work in progress.
                var local = proof.Item;
                if (local is not null)
                {
                    row.DownloadedPath = offline.ResolvePath(local.FileName);
                    row.DownloadedQuality = OfflineLibrary.QualityLabel(local).Replace("-MP4", "");
                }
                var state = saved.State is DownloadEntryState.Failed or DownloadEntryState.Unavailable or DownloadEntryState.Removed
                    ? saved.State : DownloadEntryState.Paused;
                if (local is not null) state = DownloadEntryState.Completed;
                var resumeAutomatically = state == DownloadEntryState.Paused
                    && saved.State is (DownloadEntryState.Queued or DownloadEntryState.Resolving or DownloadEntryState.Downloading)
                    && (snapshot.Automatic && snapshot.FavoritesBatch || saved.AutomaticFavoriteQuality is not null);
                // Partial bytes do not survive a process restart.
                entries.Add(new(row, state, local?.Bytes ?? 0, saved.Attempts,
                    state is DownloadEntryState.Failed or DownloadEntryState.Unavailable ? saved.Error : "",
                    saved.RemovedFromCollection, saved.SkipFavoritesDownload, saved.AutomaticFavoriteQuality, saved.DeferredFavorite,
                    resumeAutomatically));
            }
        }
        var byId = entries.ToDictionary(entry => entry.Track.Id, entry => entry.Track, StringComparer.Ordinal);
        var p = snapshot.Playlist;
        PlaylistDetails? playlist = null;
        DownloadPlaylistPersistence? playlistPersistence = null;
        var historyOnly = p?.HistoryOnly ?? false;
        var occurrencesRecorded = false;
        if (p is not null)
        {
            var rememberedExact = remembered is not null && p.Revision == remembered.Revision
                && p.TrackIds.SequenceEqual(remembered.Tracks.Select(track => track.Id), StringComparer.Ordinal);
            var firstPositions = p.TrackIds.Select((id, index) => (id, index)).DistinctBy(item => item.id, StringComparer.Ordinal)
                .ToDictionary(item => item.id, item => item.index, StringComparer.Ordinal);
            var legacyRepeated = p.Occurrences is null && firstPositions.Count != p.TrackIds.Count;
            var canRecallLegacyRepeats = legacyRepeated && rememberedExact && firstPositions.All(item =>
                SamePlaylistEdition(byId[item.Key], remembered!.Tracks[item.Value]));
            historyOnly |= remembered is not null && remembered.Revision > p.Revision || legacyRepeated && !canRecallLegacyRepeats;
            occurrencesRecorded = p.Occurrences is not null || snapshot.Version >= PriorCompactVersion && !legacyRepeated;
            var overrides = p.Occurrences?.ToDictionary(occurrence => occurrence.Position);
            var tracks = p.TrackIds.Select((id, position) => BuildOccurrence(id, position, hydrate: true)).ToArray();
            playlist = new PlaylistDetails(p.Owner, p.Kind, p.Title, "", p.Revision, Array.AsReadOnly(tracks));
            if (remembered is not null)
            {
                var checkpointTracks = p.TrackIds.Select((id, position) => BuildOccurrence(id, position, hydrate: false)).ToArray();
                // Recalled artwork and descriptive metadata can help the local card
                // without increasing the serialized checkpoint's admission cost.
                var preserveV2 = snapshot.Version == PriorCompactVersion && p.Occurrences?.Any(occurrence =>
                    occurrence is { MetadataRecorded: false, Metadata: null }) == true;
                var markedRows = preserveV2 ? p.Occurrences!.Where(occurrence => occurrence.MetadataRecorded)
                    .Select(occurrence => checkpointTracks[occurrence.Position]).ToHashSet() : [];
                playlistPersistence = new(playlist with { Tracks = Array.AsReadOnly(checkpointTracks) },
                    preserveV2 ? PriorCompactVersion : CurrentVersion, markedRows);
            }
            TrackRow BuildOccurrence(string id, int position, bool hydrate)
            {
                var primary = byId[id];
                if (hydrate && canRecallLegacyRepeats && firstPositions[id] != position)
                    return RestorePlaylistRow(remembered!.Tracks[position], position + 1);
                var edition = overrides?.GetValueOrDefault(position);
                var requestId = edition?.RequestId ?? primary.RequestId;
                var albumId = edition is not null ? edition.AlbumId : primary.AlbumId;
                var previous = hydrate && rememberedExact ? remembered!.Tracks[position] : null;
                var matchingPrevious = previous is not null && previous.RequestId == requestId && previous.AlbumId == albumId
                    && (edition?.CoverUri is not { } cover || cover == ArtworkUri.Metadata(previous.CoverUri)) ? previous : null;
                var metadata = edition?.Metadata;
                // Older occurrences carried edition keys and artwork alone.
                // Recall missing metadata only for the exact positional edition.
                // V3 identifies authoritative empty deltas in its header at the
                // same byte cost as V2. Some V2 records used a per-row marker.
                var previousMetadata = snapshot.Version < CurrentVersion && edition is { MetadataRecorded: false, Metadata: null }
                    && (requestId != primary.RequestId || albumId != primary.AlbumId) ? matchingPrevious : null;
                return new TrackRow
                {
                    Number = position + 1, Id = id, RequestId = requestId, AlbumId = albumId,
                    Title = metadata?.Title ?? previousMetadata?.Title ?? primary.Title,
                    Artist = metadata?.Artist ?? previousMetadata?.Artist ?? primary.Artist,
                    Artists = metadata?.Artists ?? previousMetadata?.Artists ?? primary.Artists,
                    PrimaryArtistName = metadata?.PrimaryArtistName is { } name ? name.Length == 0 ? null : name
                        : previousMetadata is null ? primary.PrimaryArtistNameToPersist : previousMetadata.PrimaryArtistName,
                    Album = metadata?.Album ?? previousMetadata?.Album ?? primary.Album,
                    DurationMs = metadata?.DurationMs ?? previousMetadata?.DurationMs ?? primary.DurationMs,
                    Available = metadata?.Available ?? previousMetadata?.Available ?? primary.Available,
                    CoverUri = edition?.CoverUri ?? ArtworkUri.Metadata(matchingPrevious?.CoverUri)
                        ?? (hydrate ? covers.GetValueOrDefault((id, requestId, albumId)) : null)
                };
            }
        }
        return new(snapshot.Label, snapshot.Quality, snapshot.Automatic, snapshot.FavoritesBatch, Array.AsReadOnly(entries.ToArray()),
            playlist, historyOnly, occurrencesRecorded, playlistPersistence);
    }

    private static bool SamePlaylistEdition(TrackRow expected, OfflinePlaylistTrack remembered) =>
        expected.RequestId == remembered.RequestId && expected.AlbumId == remembered.AlbumId;

    private static TrackRow RestorePlaylistRow(OfflinePlaylistTrack track, int number) => new()
    {
        Number = number, Id = track.Id, RequestId = track.RequestId, AlbumId = track.AlbumId, Title = track.Title,
        Artist = track.Artist, Artists = CopyArtists(track.Artists ?? []), PrimaryArtistName = track.PrimaryArtistName,
        Album = track.Album, DurationMs = track.DurationMs, Available = track.Available, CoverUri = ArtworkUri.Metadata(track.CoverUri)
    };
    private static Dictionary<(string Id, string RequestId, string? AlbumId), string> RememberedPlaylistCovers(
        OfflinePlaylist? previous, DownloadPlaylistSnapshot? playlist)
    {
        if (previous is null || playlist is null) return [];
        var members = playlist.TrackIds.ToHashSet(StringComparer.Ordinal);
        return previous.Tracks.Where(track => members.Contains(track.Id))
            .Select(track => (Key: (track.Id, track.RequestId, track.AlbumId), Cover: ArtworkUri.Metadata(track.CoverUri)))
            .Where(track => track.Cover is not null).GroupBy(track => track.Key)
            .Where(rows => rows.Select(track => track.Cover).Distinct(StringComparer.Ordinal).Count() == 1)
            .ToDictionary(rows => rows.Key, rows => rows.First().Cover!);
    }

    private static DownloadQueueSnapshot Freeze(DownloadQueueSnapshot snapshot)
    { ArgumentNullException.ThrowIfNull(snapshot); Validate(snapshot); var copy = Copy(snapshot); Validate(copy); return copy; }
    private static DownloadQueueSnapshot Copy(DownloadQueueSnapshot snapshot) => snapshot with
    {
        Entries = Array.AsReadOnly(snapshot.Entries.Select(entry => entry with
        {
            Track = entry.Track with { Artists = CopyArtists(entry.Track.Artists) },
            DeferredFavorite = entry.DeferredFavorite is { PreviousTrack: { } previous } deferred
                ? deferred with { PreviousTrack = previous with { Artists = CopyArtists(previous.Artists) } } : entry.DeferredFavorite
        }).ToArray()),
        Playlist = snapshot.Playlist is { } p ? p with
        {
            TrackIds = Array.AsReadOnly(p.TrackIds.ToArray()),
            Occurrences = p.Occurrences is { } occurrences ? Array.AsReadOnly(occurrences.Select(occurrence =>
                occurrence.Metadata is { Artists: { } artists } metadata
                    ? occurrence with { Metadata = metadata with { Artists = CopyArtists(artists) } } : occurrence).ToArray()) : null
        } : null
    };
    private static IReadOnlyList<ArtistReference> CopyArtists(IReadOnlyList<ArtistReference>? artists)
    {
        if (artists is null || artists.Any(artist => artist is null)) throw new JsonException();
        return Array.AsReadOnly(artists.Select(artist => new ArtistReference(artist.Id, artist.Name)).ToArray());
    }
    private static void Validate(DownloadQueueSnapshot snapshot)
    {
        if (snapshot.Version is not (LegacyVersion or PriorCompactVersion or CurrentVersion) || !ValidId(snapshot.AccountUid) || snapshot.SavedUtc == default
            || snapshot.SavedUtc.Offset != TimeSpan.Zero || !ValidText(snapshot.Label) || !Enum.IsDefined(snapshot.Quality)
            || snapshot.Entries is null || snapshot.Entries.Count > MaximumEntries) throw new JsonException();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in snapshot.Entries)
        {
            if (entry is null || entry.Track is not { } track || !ValidSavedTrack(track) || !ids.Add(track.Id)
                || !Enum.IsDefined(entry.State)
                || entry.Attempts < 0 || entry.Attempts > MaximumAttempts || entry.Error is null || entry.Error != DownloadFailure.RestorableReason(entry.Error)
                || entry.AutomaticFavoriteQuality is { } automaticQuality && !Enum.IsDefined(automaticQuality)
                || entry.DeferredFavorite is { } deferred && (snapshot.Version == LegacyVersion || entry.AutomaticFavoriteQuality is null
                    || entry.State is not (DownloadEntryState.Queued or DownloadEntryState.Paused or DownloadEntryState.Suspended)
                    || deferred.PreviousState is not (DownloadEntryState.Unavailable or DownloadEntryState.Removed)
                    || deferred.PreviousQuality is { } previousQuality && !Enum.IsDefined(previousQuality)
                    || deferred.PreviousTrack is { } previous && (previous.Id != track.Id || !ValidSavedTrack(previous)))
                || entry.SkipFavoritesDownload && (!(snapshot.FavoritesBatch || entry.AutomaticFavoriteQuality is not null)
                    || !entry.RemovedFromCollection)) throw new JsonException();
        }
        if (snapshot.Playlist is { } p && (!ValidKey(p.Owner) || !ValidKey(p.Kind) || !ValidText(p.Title) || p.Revision < 0
            || p.TrackIds is null || p.TrackIds.Count > MaximumEntries || p.TrackIds.Any(id => id is null || !ids.Contains(id)))) throw new JsonException();
        if (snapshot.Playlist is { Occurrences: { } occurrences } context)
        {
            if (snapshot.Version == LegacyVersion || occurrences.Count > context.TrackIds.Count) throw new JsonException();
            var positions = new HashSet<int>();
            foreach (var occurrence in occurrences)
                if (occurrence is null || occurrence.Position < 0 || occurrence.Position >= context.TrackIds.Count || !positions.Add(occurrence.Position)
                    || !ValidRequestId(occurrence.RequestId, context.TrackIds[occurrence.Position])
                    || occurrence.AlbumId is not null && !ValidId(occurrence.AlbumId)
                    || occurrence.CoverUri is { } cover && cover != ArtworkUri.Metadata(cover)
                    || occurrence.Metadata is { } metadata && !ValidOccurrenceMetadata(metadata)) throw new JsonException();
        }
        // A confirmed-like batch has favorites removal semantics without claiming
        // that its few tracks form the complete offline collection snapshot.
        if (snapshot.FavoritesBatch && snapshot.Playlist is not { Owner: "local:favorites", Kind: "liked" }
            && !(snapshot.Automatic && snapshot.Playlist is null)) throw new JsonException();
    }
    private static bool ValidSavedTrack(PlaybackTrackState track) => ValidId(track.Id)
        && ValidRequestId(track.RequestId, track.Id) && (track.AlbumId is null || ValidId(track.AlbumId))
        && track.CoverUri is null && !track.OfflineOnly && track.LocalSourceRequestId is null
        && track.LocalSourceCodec is null && track.LocalSourceBitrate is null && track.LocalSourceConfirmedBitrate is null && track.LocalSourceSha256 is null
        && ValidText(track.Title) && ValidText(track.Artist) && ValidText(track.Album)
        && TrackRow.ValidPrimaryArtistName(track.PrimaryArtistName) && track.DurationMs is >= 0 and <= PlaybackStateStore.MaximumDurationMs
        && ValidArtists(track.Artists);
    private static bool ValidOccurrenceMetadata(DownloadPlaylistOccurrenceMetadata metadata) =>
        (metadata.Title is null || ValidText(metadata.Title)) && (metadata.Artist is null || ValidText(metadata.Artist))
        && (metadata.Album is null || ValidText(metadata.Album))
        && (metadata.DurationMs is null or >= 0 and <= PlaybackStateStore.MaximumDurationMs)
        && (metadata.PrimaryArtistName == "" || TrackRow.ValidPrimaryArtistName(metadata.PrimaryArtistName))
        && (metadata.Artists is null || ValidArtists(metadata.Artists));
    private static bool ValidArtists(IReadOnlyList<ArtistReference>? artists) => artists is { Count: <= 100 }
        && artists.All(artist => artist is not null && ValidId(artist.Id) && ValidText(artist.Name));
    private static bool ValidId(string? value) => value is { Length: > 0 and <= 512 }
        && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
    private static bool ValidKey(string? value) => value is { Length: > 0 and <= 512 }
        && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or ':' or '.');
    private static bool ValidRequestId(string? value, string id) => value == id || value is { Length: <= 1025 }
        && value.StartsWith(id + ":", StringComparison.Ordinal) && ValidId(value[(id.Length + 1)..]);
    private static bool ValidText(string? value) => value is { Length: <= 4096 } && !value.Any(char.IsControl);
    private sealed class StoreState { internal long Revision, PublishedRevision; }
    private sealed class BoundedJsonStream(bool measureOnly = false) : MemoryStream
    {
        private int _measuredBytes;
        public override void Write(byte[] buffer, int offset, int count) { if (ShouldWrite(count)) base.Write(buffer, offset, count); }
        public override void Write(ReadOnlySpan<byte> buffer) { if (ShouldWrite(buffer.Length)) base.Write(buffer); }
        public override void WriteByte(byte value) { if (ShouldWrite(1)) base.WriteByte(value); }
        private bool ShouldWrite(int count)
        {
            if ((measureOnly ? _measuredBytes : Length) + count > MaximumBytes)
                throw new JsonException("Очередь слишком велика для сохранения.");
            if (measureOnly) _measuredBytes += count;
            return !measureOnly;
        }
    }
}
