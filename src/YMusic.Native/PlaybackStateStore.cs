using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace YMusic.Native;

// Only trusted anonymous artwork metadata is optional. Audio locations, credentials,
// download descriptors, and the native player's temporary files are never saved.
internal sealed record PlaybackTrackState(
    [property: JsonRequired] string Id, [property: JsonRequired] string RequestId,
    [property: JsonRequired] string Title, [property: JsonRequired] string Artist,
    [property: JsonRequired] string Album, [property: JsonRequired] long DurationMs,
    [property: JsonRequired] bool Available, [property: JsonRequired] IReadOnlyList<ArtistReference> Artists,
    string? AlbumId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? CoverUri = null);

internal sealed record PlaybackSnapshot(
    [property: JsonRequired] int Version, [property: JsonRequired] string AccountUid,
    [property: JsonRequired] DateTimeOffset SavedUtc,
    [property: JsonRequired] IReadOnlyList<PlaybackTrackState> Tracks,
    [property: JsonRequired] int CurrentIndex, [property: JsonRequired] double PositionSeconds,
    [property: JsonRequired] bool Shuffle, [property: JsonRequired] RepeatMode Repeat);

internal sealed record RestoredPlaybackQueue(IReadOnlyList<TrackRow> Tracks, int CurrentIndex, double PositionSeconds);
internal enum PlaybackStateReadStatus { Missing, Disabled, Loaded, OtherAccount, Invalid, Unavailable }

internal sealed class PlaybackStateStore
{
    internal const int CurrentVersion = 1;
    internal const int MaximumTracks = 10_000;
    internal const int MaximumBytes = 8 * 1024 * 1024;
    internal const long MaximumDurationMs = 7L * 24 * 60 * 60 * 1000;
    internal const string FileName = "playback-state.json";
    private static readonly ConcurrentDictionary<string, object> Gates = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, long> Revisions = new(StringComparer.OrdinalIgnoreCase);
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 16
    };
    private readonly string _directory;
    private readonly string _path;
    private readonly object _gate;
    public PlaybackStateReadStatus LastReadStatus { get; private set; }

    public PlaybackStateStore(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        _directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        _path = Path.Combine(_directory, FileName);
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
                EnsureSafeLocation();
                if (!File.Exists(_path)) { LastReadStatus = PlaybackStateReadStatus.Missing; return null; }
                using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (stream.Length > MaximumBytes) throw new JsonException();
                // The opened handle bounds allocation and excludes concurrent writes;
                // never read an unbounded string from an untrusted snapshot.
                var bytes = new byte[checked((int)stream.Length)];
                stream.ReadExactly(bytes);
                var snapshot = JsonSerializer.Deserialize<PlaybackSnapshot>(bytes, Json) ?? throw new JsonException();
                Validate(snapshot);
                if (!string.Equals(snapshot.AccountUid, accountUid, StringComparison.Ordinal))
                { LastReadStatus = PlaybackStateReadStatus.OtherAccount; return null; }
                LastReadStatus = PlaybackStateReadStatus.Loaded;
                return Copy(snapshot);
            }
            catch (JsonException) { LastReadStatus = PlaybackStateReadStatus.Invalid; return null; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { LastReadStatus = PlaybackStateReadStatus.Unavailable; return null; }
        }
    }

    public void Save(PlaybackSnapshot snapshot)
        => SaveCore(snapshot, null);

    internal Task SaveInBackgroundAsync(PlaybackSnapshot snapshot)
    {
        var frozen = Copy(snapshot);
        long revision;
        lock (_gate) revision = Revisions.GetOrAdd(_path, 0);
        return Task.Run(() => SaveCore(frozen, revision));
    }

    private void SaveCore(PlaybackSnapshot snapshot, long? expectedRevision)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        Validate(snapshot);
        // Freeze caller-owned collections before serialization and validate that
        // exact copy. Nothing can mutate a persisted queue occurrence in flight.
        var frozen = Copy(snapshot);
        Validate(frozen);
        using var serialized = new BoundedJsonStream();
        JsonSerializer.Serialize(serialized, frozen, Json);
        var bytes = serialized.ToArray();
        lock (_gate)
        {
            var revision = Revisions.GetOrAdd(_path, 0);
            if (expectedRevision.HasValue && expectedRevision.Value != revision) return;
            WriteAtomic(bytes);
            Revisions[_path] = revision + 1;
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            Revisions.AddOrUpdate(_path, 1, (_, value) => value + 1);
            EnsureSafeLocation();
            if (!File.Exists(_path)) return;
            // If deletion fails after replacement, this invalid marker still
            // prevents restoration of the previous account's queue.
            WriteAtomic("{\"version\":0}"u8.ToArray());
            File.Delete(_path);
        }
    }

    private void WriteAtomic(byte[] bytes)
    {
        EnsureSafeLocation();
        Directory.CreateDirectory(_directory);
        EnsureSafeLocation();
        var temporary = Path.Combine(_directory, ".playback-state-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            { stream.Write(bytes); stream.Flush(flushToDisk: true); }
            // Replacement is on the same volume. A failed replacement leaves
            // the previous complete snapshot intact, never a truncated JSON.
            EnsureSafeLocation();
            if (File.Exists(_path)) File.Replace(temporary, _path, destinationBackupFileName: null);
            else File.Move(temporary, _path);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    public static PlaybackSnapshot Capture(string accountUid, PlaybackQueue queue, double positionSeconds)
    {
        ArgumentNullException.ThrowIfNull(queue);
        var tracks = queue.Items.Select(row => new PlaybackTrackState(row.Id, row.RequestId, row.Title, row.Artist,
            row.Album, row.DurationMs, row.Available, CopyArtists(row.Artists), row.AlbumId, ArtworkUri.Metadata(row.CoverUri))).ToArray();
        var index = queue.CurrentIndex;
        var snapshot = new PlaybackSnapshot(CurrentVersion, accountUid, DateTimeOffset.UtcNow, Array.AsReadOnly(tracks), index,
            ClampPosition(positionSeconds, index < 0 ? 0 : tracks[index].DurationMs), queue.Shuffle, queue.Repeat);
        Validate(snapshot);
        return snapshot;
    }

    public static RestoredPlaybackQueue RestoreQueue(PlaybackSnapshot snapshot, OfflineLibrary offline, bool localOnly = false)
    {
        ArgumentNullException.ThrowIfNull(snapshot); ArgumentNullException.ThrowIfNull(offline);
        Validate(snapshot);
        var rows = new List<TrackRow>(snapshot.Tracks.Count);
        var currentIndex = -1;
        for (var i = 0; i < snapshot.Tracks.Count; i++)
        {
            var metadata = snapshot.Tracks[i];
            var probe = ToRow(metadata, rows.Count + 1, available: metadata.Available);
            // Only the library's validated index can supply a local path. The
            // snapshot has no path field and cannot open an arbitrary file.
            var local = offline.Find(probe);
            if (localOnly && local is null) continue;
            var row = ToRow(metadata, rows.Count + 1, available: !localOnly && metadata.Available,
                local is null ? null : offline.ResolvePath(local.FileName),
                local is null ? null : OfflineQuality(local), local?.CoverUri);
            if (i == snapshot.CurrentIndex) currentIndex = rows.Count;
            rows.Add(row);
        }
        return new(Array.AsReadOnly(rows.ToArray()), currentIndex,
            currentIndex < 0 ? 0 : ClampPosition(snapshot.PositionSeconds, rows[currentIndex].DurationMs));
    }

    internal static double ClampPosition(double seconds, long durationMs)
        => !double.IsFinite(seconds) || durationMs <= 0 ? 0 : Math.Clamp(seconds, 0, durationMs / 1000.0);

    private static TrackRow ToRow(PlaybackTrackState track, int number, bool available, string? path = null, string? quality = null, string? offlineCover = null)
        => new()
        {
            Number = number, Id = track.Id, RequestId = track.RequestId, Title = track.Title, Artist = track.Artist,
            Artists = CopyArtists(track.Artists), Album = track.Album, AlbumId = track.AlbumId, DurationMs = track.DurationMs,
            CoverUri = ArtworkUri.Metadata(track.CoverUri) ?? ArtworkUri.Metadata(offlineCover),
            Available = available, DownloadedPath = path, DownloadedQuality = quality
        };
    private static string OfflineQuality(OfflineItem item) => item.Codec.StartsWith("flac", StringComparison.Ordinal)
        ? "Lossless · FLAC" : item.Codec.ToUpperInvariant().Replace("-MP4", "") + (item.Bitrate > 0 ? $" · {item.Bitrate} кбит/с" : "");
    private static IReadOnlyList<ArtistReference> CopyArtists(IReadOnlyList<ArtistReference> artists)
        => Array.AsReadOnly(artists.Select(artist => new ArtistReference(artist.Id, artist.Name)).ToArray());
    private static PlaybackSnapshot Copy(PlaybackSnapshot snapshot)
        => snapshot with
        {
            Tracks = Array.AsReadOnly(snapshot.Tracks.Select(track => track with { Artists = CopyArtists(track.Artists) }).ToArray()),
            PositionSeconds = ClampPosition(snapshot.PositionSeconds, snapshot.CurrentIndex < 0 ? 0 : snapshot.Tracks[snapshot.CurrentIndex].DurationMs)
        };

    private static void Validate(PlaybackSnapshot snapshot)
    {
        if (snapshot.Version != CurrentVersion || !ValidId(snapshot.AccountUid) || snapshot.SavedUtc == default
            || snapshot.Tracks is null || snapshot.Tracks.Count > MaximumTracks
            || snapshot.CurrentIndex < -1 || snapshot.CurrentIndex >= snapshot.Tracks.Count
            || !double.IsFinite(snapshot.PositionSeconds) || snapshot.PositionSeconds < 0 || !Enum.IsDefined(snapshot.Repeat))
            throw new JsonException("Некорректное сохранённое состояние воспроизведения.");
        foreach (var track in snapshot.Tracks)
        {
            if (track is null || !ValidId(track.Id) || !ValidRequestId(track.RequestId, track.Id)
                || track.AlbumId is not null && !ValidId(track.AlbumId)
                || track.CoverUri is not null && ArtworkUri.Metadata(track.CoverUri) is null
                || !ValidText(track.Title) || !ValidText(track.Artist) || !ValidText(track.Album)
                || track.DurationMs < 0 || track.DurationMs > MaximumDurationMs || track.Artists is null || track.Artists.Count > 100)
                throw new JsonException("Некорректные метаданные сохранённого трека.");
            foreach (var artist in track.Artists)
                if (artist is null || !ValidId(artist.Id) || !ValidText(artist.Name)) throw new JsonException();
        }
    }

    private static bool ValidId(string? id) => id is { Length: > 0 and <= 512 }
        && id.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');
    private static bool ValidRequestId(string? requestId, string id)
    {
        if (requestId == id) return true;
        if (requestId is null || requestId.Length > 1025 || !requestId.StartsWith(id + ":", StringComparison.Ordinal)) return false;
        return ValidId(requestId[(id.Length + 1)..]);
    }
    private static bool ValidText(string? value) => value is { Length: <= 4096 } && !value.Any(char.IsControl);

    private void EnsureSafeLocation()
    {
        for (DirectoryInfo? part = new(_directory); part is not null; part = part.Parent)
            if (part.Exists && (part.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Папка состояния содержит перенаправление.");
        if (File.Exists(_path) && (File.GetAttributes(_path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Файл состояния содержит перенаправление.");
    }

    private sealed class BoundedJsonStream : MemoryStream
    {
        public override void Write(byte[] buffer, int offset, int count)
        { CheckLength(count); base.Write(buffer, offset, count); }
        public override void Write(ReadOnlySpan<byte> buffer)
        { CheckLength(buffer.Length); base.Write(buffer); }
        public override void WriteByte(byte value)
        { CheckLength(1); base.WriteByte(value); }
        private void CheckLength(int count)
        {
            if (Length + count > MaximumBytes) throw new JsonException("Очередь слишком велика для сохранения.");
        }
    }
}
