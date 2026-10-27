using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace YMusic.Native;

// Metadata only. Neither TrackRow paths/artwork nor Session/AudioDescriptor can
// enter this schema. Playlist membership refers to these exact queue track IDs.
internal sealed record DownloadEntrySnapshot(
    [property: JsonRequired] PlaybackTrackState Track, [property: JsonRequired] DownloadEntryState State,
    [property: JsonRequired] int Attempts, [property: JsonRequired] string Error,
    [property: JsonRequired] bool RemovedFromCollection, [property: JsonRequired] bool SkipFavoritesDownload);
internal sealed record DownloadPlaylistSnapshot(
    [property: JsonRequired] string Owner, [property: JsonRequired] string Kind,
    [property: JsonRequired] string Title, [property: JsonRequired] int Revision,
    [property: JsonRequired] IReadOnlyList<string> TrackIds);
internal sealed record DownloadQueueSnapshot(
    [property: JsonRequired] int Version, [property: JsonRequired] string AccountUid,
    [property: JsonRequired] DateTimeOffset SavedUtc, [property: JsonRequired] string Label,
    [property: JsonRequired] AudioQuality Quality, [property: JsonRequired] bool Automatic,
    [property: JsonRequired] bool FavoritesBatch, [property: JsonRequired] IReadOnlyList<DownloadEntrySnapshot> Entries,
    DownloadPlaylistSnapshot? Playlist = null);
internal sealed record RestoredDownloadEntry(TrackRow Track, DownloadEntryState State, long Bytes, int Attempts,
    string Error, bool RemovedFromCollection, bool SkipFavoritesDownload);
internal sealed record RestoredDownloadQueue(string Label, AudioQuality Quality, bool Automatic, bool FavoritesBatch,
    IReadOnlyList<RestoredDownloadEntry> Entries, PlaylistDetails? Playlist);
internal enum DownloadQueueReadStatus { Missing, Disabled, Loaded, OtherAccount, Invalid, Unavailable }

internal sealed class DownloadQueueStore
{
    internal const string FileName = "download-queue.json";
    internal const int CurrentVersion = 1, MaximumEntries = 10_000, MaximumBytes = 8 * 1024 * 1024;
    private static readonly ConcurrentDictionary<string, StoreState> States = new(StringComparer.OrdinalIgnoreCase);
    private static readonly JsonSerializerOptions Json = new()
    { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, MaxDepth = 16 };
    private readonly string _directory, _path;
    private readonly StoreState _state;
    public DownloadQueueReadStatus LastReadStatus { get; private set; }

    internal DownloadQueueStore(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        _directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        _path = Path.Combine(_directory, FileName);
        _state = States.GetOrAdd(_path, _ => new());
    }
    internal DownloadQueueSnapshot? Load(string? accountUid)
    {
        if (!ValidId(accountUid)) { LastReadStatus = DownloadQueueReadStatus.Disabled; return null; }
        lock (_state)
        {
            try
            {
                EnsureSafeLocation();
                if (!File.Exists(_path)) { LastReadStatus = DownloadQueueReadStatus.Missing; return null; }
                using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (stream.Length > MaximumBytes) throw new JsonException();
                var bytes = new byte[checked((int)stream.Length)]; stream.ReadExactly(bytes);
                var snapshot = JsonSerializer.Deserialize<DownloadQueueSnapshot>(bytes, Json) ?? throw new JsonException();
                Validate(snapshot);
                if (snapshot.AccountUid != accountUid) { LastReadStatus = DownloadQueueReadStatus.OtherAccount; return null; }
                LastReadStatus = DownloadQueueReadStatus.Loaded; return Copy(snapshot);
            }
            catch (JsonException) { LastReadStatus = DownloadQueueReadStatus.Invalid; return null; }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            { LastReadStatus = DownloadQueueReadStatus.Unavailable; return null; }
        }
    }
    internal void Save(DownloadQueueSnapshot snapshot)
    {
        var frozen = Freeze(snapshot); long revision;
        lock (_state) revision = ++_state.Revision;
        SaveCore(frozen, revision);
    }
    internal Task SaveInBackgroundAsync(DownloadQueueSnapshot snapshot)
    {
        var frozen = Freeze(snapshot); long revision;
        // Reserve at admission, not when a worker happens to run. A later save,
        // clear or close always invalidates every older outstanding write.
        lock (_state) revision = ++_state.Revision;
        return Task.Run(() => SaveCore(frozen, revision));
    }
    private void SaveCore(DownloadQueueSnapshot snapshot, long revision)
    {
        using var stream = new BoundedJsonStream(); JsonSerializer.Serialize(stream, snapshot, Json);
        var bytes = stream.ToArray();
        lock (_state)
        {
            if (revision != _state.Revision) return;
            WriteAtomic(bytes);
        }
    }
    internal void Clear()
    {
        lock (_state)
        {
            ++_state.Revision;
            EnsureSafeLocation();
            if (!File.Exists(_path)) return;
            // If deletion fails, no previous account queue remains restorable.
            WriteAtomic("{\"version\":0}"u8.ToArray()); File.Delete(_path);
        }
    }
    private void WriteAtomic(byte[] bytes)
    {
        EnsureSafeLocation(); Directory.CreateDirectory(_directory); EnsureSafeLocation();
        var temporary = Path.Combine(_directory, ".download-queue-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            { stream.Write(bytes); stream.Flush(flushToDisk: true); }
            EnsureSafeLocation();
            if (File.Exists(_path)) File.Replace(temporary, _path, null); else File.Move(temporary, _path);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }
    internal static DownloadQueueSnapshot Capture(string accountUid, DownloadQueueState queue, AudioQuality quality,
        bool automatic, PlaylistDetails? playlist)
    {
        queue.VerifyAccess();
        var entries = queue.Entries.Select(entry => new DownloadEntrySnapshot(
            new PlaybackTrackState(entry.Track.Id, entry.Track.RequestId, entry.Track.Title, entry.Track.Artist,
                entry.Track.Album, entry.Track.DurationMs, entry.Track.Available, CopyArtists(entry.Track.Artists), entry.Track.AlbumId),
            entry.State, entry.Attempts, DownloadFailure.RestorableReason(entry.Error), entry.RemovedFromCollection, entry.SkipFavoritesDownload)).ToArray();
        var context = playlist is null ? null : new DownloadPlaylistSnapshot(playlist.Owner, playlist.Kind, playlist.Title,
            playlist.Revision, Array.AsReadOnly(playlist.Tracks.Select(track => track.Id).ToArray()));
        var snapshot = new DownloadQueueSnapshot(CurrentVersion, accountUid, DateTimeOffset.UtcNow, queue.Label,
            quality, automatic, queue.FavoritesBatch, Array.AsReadOnly(entries), context);
        Validate(snapshot); return snapshot;
    }
    internal static RestoredDownloadQueue PrepareRestore(DownloadQueueSnapshot snapshot, OfflineLibrary offline)
    {
        Validate(snapshot); ArgumentNullException.ThrowIfNull(offline);
        var entries = new List<RestoredDownloadEntry>(snapshot.Entries.Count);
        foreach (var saved in snapshot.Entries)
        {
            var t = saved.Track;
            var row = new TrackRow { Number = entries.Count + 1, Id = t.Id, RequestId = t.RequestId, Title = t.Title,
                Artist = t.Artist, Artists = CopyArtists(t.Artists), Album = t.Album, AlbumId = t.AlbumId,
                DurationMs = t.DurationMs, Available = t.Available };
            // Only a validated offline-index entry can prove a file is ready.
            // A crash after audio commit but before queue save is covered too.
            var local = offline.Find(row);
            if (local is not null)
            {
                row.DownloadedPath = offline.ResolvePath(local.FileName);
                row.DownloadedQuality = local.Codec.StartsWith("flac", StringComparison.Ordinal) ? "Lossless · FLAC"
                    : local.Codec.ToUpperInvariant().Replace("-MP4", "") + (local.Bitrate > 0 ? $" · {local.Bitrate} кбит/с" : "");
            }
            var state = saved.State is DownloadEntryState.Failed or DownloadEntryState.Unavailable or DownloadEntryState.Removed
                ? saved.State : DownloadEntryState.Paused;
            if (local is not null && saved.State != DownloadEntryState.Removed && (saved.State == DownloadEntryState.Completed
                || snapshot.Automatic || AudioQualityPolicy.IsSufficient(local.Codec, local.Bitrate, snapshot.Quality))) state = DownloadEntryState.Completed;
            // No partial-file claim survives a process restart. The existing
            // downloader restarts that one track after explicit continuation.
            entries.Add(new(row, state, state == DownloadEntryState.Completed ? local!.Bytes : 0, saved.Attempts,
                state is DownloadEntryState.Failed or DownloadEntryState.Unavailable ? saved.Error : "",
                saved.RemovedFromCollection, saved.SkipFavoritesDownload));
        }
        var byId = entries.ToDictionary(entry => entry.Track.Id, entry => entry.Track, StringComparer.Ordinal);
        var playlist = snapshot.Playlist is { } p ? new PlaylistDetails(p.Owner, p.Kind, p.Title, "", p.Revision,
            Array.AsReadOnly(p.TrackIds.Select(id => byId[id]).ToArray())) : null;
        return new(snapshot.Label, snapshot.Quality, snapshot.Automatic, snapshot.FavoritesBatch, Array.AsReadOnly(entries.ToArray()), playlist);
    }
    private static DownloadQueueSnapshot Freeze(DownloadQueueSnapshot snapshot)
    { ArgumentNullException.ThrowIfNull(snapshot); Validate(snapshot); var copy = Copy(snapshot); Validate(copy); return copy; }
    private static DownloadQueueSnapshot Copy(DownloadQueueSnapshot snapshot) => snapshot with
    {
        Entries = Array.AsReadOnly(snapshot.Entries.Select(entry => entry with { Track = entry.Track with { Artists = CopyArtists(entry.Track.Artists) } }).ToArray()),
        Playlist = snapshot.Playlist is { } p ? p with { TrackIds = Array.AsReadOnly(p.TrackIds.ToArray()) } : null
    };
    private static IReadOnlyList<ArtistReference> CopyArtists(IReadOnlyList<ArtistReference>? artists)
    {
        if (artists is null || artists.Any(artist => artist is null)) throw new JsonException();
        return Array.AsReadOnly(artists.Select(artist => new ArtistReference(artist.Id, artist.Name)).ToArray());
    }
    private static void Validate(DownloadQueueSnapshot snapshot)
    {
        if (snapshot.Version != CurrentVersion || !ValidId(snapshot.AccountUid) || snapshot.SavedUtc == default
            || snapshot.SavedUtc.Offset != TimeSpan.Zero || !ValidText(snapshot.Label) || !Enum.IsDefined(snapshot.Quality)
            || snapshot.Entries is null || snapshot.Entries.Count > MaximumEntries) throw new JsonException();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in snapshot.Entries)
        {
            if (entry is null || entry.Track is not { } track || !ValidId(track.Id) || !ids.Add(track.Id)
                || !ValidRequestId(track.RequestId, track.Id) || track.AlbumId is not null && !ValidId(track.AlbumId)
                || !ValidText(track.Title) || !ValidText(track.Artist) || !ValidText(track.Album)
                || track.DurationMs < 0 || track.DurationMs > PlaybackStateStore.MaximumDurationMs
                || track.Artists is null || track.Artists.Count > 100 || !Enum.IsDefined(entry.State)
                || entry.Attempts < 0 || entry.Attempts > 1_000_000 || entry.Error is null || entry.Error != DownloadFailure.RestorableReason(entry.Error)
                || entry.SkipFavoritesDownload && (!snapshot.FavoritesBatch || !entry.RemovedFromCollection)) throw new JsonException();
            foreach (var artist in track.Artists)
                if (artist is null || !ValidId(artist.Id) || !ValidText(artist.Name)) throw new JsonException();
        }
        if (snapshot.Playlist is { } p && (!ValidKey(p.Owner) || !ValidKey(p.Kind) || !ValidText(p.Title) || p.Revision < 0
            || p.TrackIds is null || p.TrackIds.Count > MaximumEntries || p.TrackIds.Any(id => id is null || !ids.Contains(id)))) throw new JsonException();
        if (snapshot.FavoritesBatch && snapshot.Playlist is not { Owner: "local:favorites", Kind: "liked" }) throw new JsonException();
    }
    private static bool ValidId(string? value) => value is { Length: > 0 and <= 512 }
        && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
    private static bool ValidKey(string? value) => value is { Length: > 0 and <= 512 }
        && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or ':' or '.');
    private static bool ValidRequestId(string? value, string id) => value == id || value is { Length: <= 1025 }
        && value.StartsWith(id + ":", StringComparison.Ordinal) && ValidId(value[(id.Length + 1)..]);
    private static bool ValidText(string? value) => value is { Length: <= 4096 } && !value.Any(char.IsControl);
    private void EnsureSafeLocation()
    {
        for (DirectoryInfo? part = new(_directory); part is not null; part = part.Parent)
            if (part.Exists && (part.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Папка очереди содержит перенаправление.");
        if (File.Exists(_path) && (File.GetAttributes(_path) & FileAttributes.ReparsePoint) != 0) throw new IOException("Файл очереди содержит перенаправление.");
    }
    private sealed class StoreState { internal long Revision; }
    private sealed class BoundedJsonStream : MemoryStream
    {
        public override void Write(byte[] buffer, int offset, int count) { Bound(count); base.Write(buffer, offset, count); }
        public override void Write(ReadOnlySpan<byte> buffer) { Bound(buffer.Length); base.Write(buffer); }
        public override void WriteByte(byte value) { Bound(1); base.WriteByte(value); }
        private void Bound(int count) { if (Length + count > MaximumBytes) throw new JsonException("Очередь слишком велика для сохранения."); }
    }
}
