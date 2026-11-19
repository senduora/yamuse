using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32.SafeHandles;

namespace YMusic.Native;

// Descriptive metadata only: no credentials, audio sources or local media paths.
internal sealed record FavoritesTrackMetadata(
    [property: JsonRequired] string Id, [property: JsonRequired] string RequestId,
    [property: JsonRequired] string Title, [property: JsonRequired] string Artist,
    [property: JsonRequired] string Album, [property: JsonRequired] long DurationMs,
    [property: JsonRequired] bool Available, [property: JsonRequired] IReadOnlyList<ArtistReference> Artists,
    string? AlbumId = null, string? CoverUri = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool MissingMetadata = false,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? PrimaryArtistName = null);

internal sealed record FavoritesCacheSnapshot(
    [property: JsonRequired] int Version, [property: JsonRequired] string AccountHash,
    [property: JsonRequired] DateTimeOffset SavedUtc,
    [property: JsonRequired] IReadOnlyList<FavoritesTrackMetadata> Tracks);

internal enum FavoritesCacheReadStatus { Missing, Loaded, Invalid, Unavailable }

internal sealed class FavoritesCacheStore
{
    internal const int CurrentVersion = 1, MaximumTracks = 50_000, MaximumBytes = 32 * 1024 * 1024;
    private static readonly ConcurrentDictionary<string, StoreState> States = new(StringComparer.OrdinalIgnoreCase);
    private static readonly JsonSerializerOptions Json = new()
    { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, MaxDepth = 16 };
    private readonly string _directory;
    private readonly Func<Action, Task> _schedule;
    private readonly Action? _beforeCommit;

    internal FavoritesCacheStore(string directory) : this(directory, action => Task.Run(action)) { }

    // Fixtures can reorder workers or hold a flushed write before replacement.
    internal FavoritesCacheStore(string directory, Func<Action, Task> schedule, Action? beforeCommit = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentNullException.ThrowIfNull(schedule);
        _directory = Path.Combine(Path.GetFullPath(directory), "favorites-cache");
        _schedule = schedule;
        _beforeCommit = beforeCommit;
    }

    internal string FilePath(string accountUid) => Path.Combine(_directory, AccountHash(accountUid) + ".json");

    internal FavoritesCacheSnapshot? Load(string accountUid)
        => Read(accountUid).Snapshot;

    internal (FavoritesCacheReadStatus Status, FavoritesCacheSnapshot? Snapshot) Read(string accountUid)
    {
        if (!ValidAccountUid(accountUid)) return (FavoritesCacheReadStatus.Invalid, null);
        var hash = AccountHash(accountUid);
        var path = Path.Combine(_directory, hash + ".json");
        lock (States.GetOrAdd(path, _ => new()))
        {
            try
            {
                using var location = SafeDirectoryScope.Acquire(_directory, create: false);
                if (location is null) return (FavoritesCacheReadStatus.Missing, null);
                using var handle = GuardedStoreFile.OpenForRead(path);
                if (handle is null) return (FavoritesCacheReadStatus.Missing, null);
                ValidateChecked(handle, path, directory: false);
                if (GuardedStoreFile.ReadFromHandle(handle, MaximumBytes) is not { } bytes)
                    return (FavoritesCacheReadStatus.Invalid, null);
                var snapshot = ReadSnapshot(bytes, Json);
                return snapshot.AccountHash == hash ? (FavoritesCacheReadStatus.Loaded, Copy(snapshot))
                    : (FavoritesCacheReadStatus.Invalid, null);
            }
            catch (JsonException) { return (FavoritesCacheReadStatus.Invalid, null); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or Win32Exception)
            { return (FavoritesCacheReadStatus.Unavailable, null); }
        }
    }

    private static FavoritesCacheSnapshot ReadSnapshot(byte[] bytes, JsonSerializerOptions options)
    {
        var snapshot = JsonSerializer.Deserialize<FavoritesCacheSnapshot>(bytes, options) ?? throw new JsonException();
        Validate(snapshot);
        return snapshot;
    }

    internal static bool IsOwnedFile(string name, SafeFileHandle file)
    {
        if (GuardedStoreFile.ReadFromHandle(file, MaximumBytes) is not { } bytes) return false;
        try
        {
            var options = new JsonSerializerOptions(Json) { AllowDuplicateProperties = false };
            return ReadSnapshot(bytes, options).AccountHash == name[..64];
        }
        catch (JsonException) { return false; }
    }

    internal Task SaveInBackgroundAsync(FavoritesCacheSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        Validate(snapshot);
        var frozen = Copy(snapshot);
        Validate(frozen);
        var path = Path.Combine(_directory, frozen.AccountHash + ".json");
        var state = States.GetOrAdd(path, _ => new());
        // Admission and publication share only the final rename lock. Serialization
        // and flushing must not hold up the caller's dispatcher.
        long revision;
        lock (state.RenameGate) revision = Interlocked.Increment(ref state.Revision);
        return _schedule(() =>
        {
            if (revision != Volatile.Read(ref state.Revision)) return;
            using var serialized = new BoundedJsonStream();
            JsonSerializer.Serialize(serialized, frozen, Json);
            var bytes = serialized.ToArray();
            lock (state)
            {
                if (revision != Volatile.Read(ref state.Revision)) return;
                WriteAtomic(path, bytes, state, revision);
            }
        });
    }

    internal static FavoritesCacheSnapshot Capture(string accountUid, IReadOnlyList<TrackRow> rows, DateTimeOffset savedUtc)
    {
        ArgumentNullException.ThrowIfNull(rows);
        if (rows.Count > MaximumTracks) throw new JsonException("Коллекция слишком велика для кэша.");
        var tracks = rows.Select(row => new FavoritesTrackMetadata(row.Id, row.RequestId, row.Title, row.Artist,
            row.Album, row.DurationMs, row.Available, CopyArtists(row.Artists), row.AlbumId, ArtworkUri.Metadata(row.CoverUri),
            MissingMetadata: !row.HasTrackMetadata, PrimaryArtistName: row.PrimaryArtistNameToPersist)).ToArray();
        var snapshot = new FavoritesCacheSnapshot(CurrentVersion, AccountHash(accountUid), savedUtc.ToUniversalTime(), Array.AsReadOnly(tracks));
        Validate(snapshot);
        return snapshot;
    }

    internal static TrackRow[] Restore(FavoritesCacheSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        Validate(snapshot);
        return snapshot.Tracks.Select((track, index) => new TrackRow
        {
            Number = index + 1, Id = track.Id, RequestId = track.RequestId, Title = track.Title, Artist = track.Artist,
            Album = track.Album, DurationMs = track.DurationMs, Available = track.Available,
            Artists = CopyArtists(track.Artists), PrimaryArtistName = track.PrimaryArtistName,
            AlbumId = track.AlbumId, CoverUri = track.CoverUri,
            HasTrackMetadata = !track.MissingMetadata && !IsLegacyPlaceholder(track)
        }).ToArray();
    }

    // Older snapshots stored an omitted track as the default unavailable row.
    private static bool IsLegacyPlaceholder(FavoritesTrackMetadata track) => !track.Available
        && track.Title == "Недоступный трек" && track.Artist.Length == 0 && track.Album.Length == 0
        && track.DurationMs == 0 && track.Artists.Count == 0 && track.PrimaryArtistName is null && track.AlbumId is null && track.CoverUri is null;

    internal static string AccountHash(string uid)
    {
        if (!ValidAccountUid(uid)) throw new ArgumentException("Неизвестный владелец кэша.", nameof(uid));
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(uid)));
    }

    private void WriteAtomic(string path, byte[] bytes, StoreState state, long revision)
    {
        _ = UninstallPathGuard.Canonical(path);
        using var location = SafeDirectoryScope.Acquire(_directory, create: true)!;
        using var storage = GuardedStoreFile.BeginWrite(path);
        using (var existing = storage.OpenExisting())
            if (existing is not null) ValidateChecked(existing, path, directory: false);
        using var staged = storage.Stage(bytes);
        var published = false;
        try
        {
            _beforeCommit?.Invoke();
            if (revision != Volatile.Read(ref state.Revision)) return;
            published = storage.Commit(staged, publish =>
            {
                // Flush and metadata preparation stay outside the admission lock.
                lock (state.RenameGate)
                {
                    if (revision != state.Revision) return false;
                    publish();
                    return true;
                }
            });
        }
        finally { if (!published) UninstallPathGuard.DiscardStagedFile(staged); }
    }

    private static FavoritesCacheSnapshot Copy(FavoritesCacheSnapshot snapshot) => snapshot with
    { Tracks = Array.AsReadOnly(snapshot.Tracks.Select(track => track with { Artists = CopyArtists(track.Artists) }).ToArray()) };

    private static IReadOnlyList<ArtistReference> CopyArtists(IReadOnlyList<ArtistReference> artists)
    {
        if (artists is null || artists.Count > 100 || artists.Any(artist => artist is null)) throw new JsonException();
        return Array.AsReadOnly(artists.Select(artist => new ArtistReference(artist.Id, artist.Name)).ToArray());
    }

    private static void Validate(FavoritesCacheSnapshot snapshot)
    {
        if (snapshot.Version != CurrentVersion || snapshot.AccountHash is not { Length: 64 }
            || snapshot.AccountHash.Any(c => c is not (>= '0' and <= '9') and not (>= 'a' and <= 'f'))
            || snapshot.SavedUtc == default || snapshot.SavedUtc.Offset != TimeSpan.Zero
            || snapshot.Tracks is null || snapshot.Tracks.Count > MaximumTracks) throw new JsonException();
        foreach (var track in snapshot.Tracks)
        {
            if (track is null || !ValidId(track.Id) || !ValidRequestId(track.RequestId, track.Id)
                || track.AlbumId is not null && !ValidId(track.AlbumId)
                || !ValidText(track.Title) || !ValidText(track.Artist) || !ValidText(track.Album)
                || !TrackRow.ValidPrimaryArtistName(track.PrimaryArtistName)
                || track.DurationMs < 0 || track.DurationMs > PlaybackStateStore.MaximumDurationMs
                || track.Available && track.MissingMetadata
                || track.Artists is null || track.Artists.Count > 100
                || track.CoverUri is not null && track.CoverUri != ArtworkUri.Metadata(track.CoverUri)) throw new JsonException();
            foreach (var artist in track.Artists)
                if (artist is null || !ValidId(artist.Id) || !ValidText(artist.Name)) throw new JsonException();
        }
    }

    private static bool ValidAccountUid(string? uid) => !string.IsNullOrWhiteSpace(uid) && uid.Length <= 512 && !uid.Any(char.IsControl);
    private static bool ValidId(string? value) => value is { Length: > 0 and <= 512 }
        && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
    private static bool ValidRequestId(string? value, string id) => value == id || value is { Length: <= 1025 }
        && value.StartsWith(id + ":", StringComparison.Ordinal) && ValidId(value[(id.Length + 1)..]);
    private static bool ValidText(string? value) => value is { Length: <= 4096 } && !value.Any(char.IsControl);

    private sealed class StoreState
    {
        internal readonly object RenameGate = new();
        internal long Revision;
    }
    private sealed class BoundedJsonStream : MemoryStream
    {
        public override void Write(byte[] buffer, int offset, int count) { Bound(count); base.Write(buffer, offset, count); }
        public override void Write(ReadOnlySpan<byte> buffer) { Bound(buffer.Length); base.Write(buffer); }
        public override void WriteByte(byte value) { Bound(1); base.WriteByte(value); }
        private void Bound(int count) { if (Length + count > MaximumBytes) throw new JsonException("Коллекция слишком велика для кэша."); }
    }

    // Keep every ancestor open without FILE_SHARE_DELETE until IO completes.
    // This prevents a junction/rename race between validation and file creation.
    private sealed class SafeDirectoryScope : IDisposable
    {
        private readonly List<SafeFileHandle> _handles = [];
        internal static SafeDirectoryScope? Acquire(string directory, bool create)
        {
            var scope = new SafeDirectoryScope();
            try
            {
                var ancestors = new Stack<string>();
                for (DirectoryInfo? part = new(directory); part is not null; part = part.Parent) ancestors.Push(part.FullName);
                foreach (var part in ancestors)
                {
                    if (create) Directory.CreateDirectory(part);
                    var handle = OpenChecked(part, directory: true, missingAllowed: !create);
                    if (handle is null) { scope.Dispose(); return null; }
                    scope._handles.Add(handle);
                }
                return scope;
            }
            catch { scope.Dispose(); throw; }
        }
        public void Dispose()
        { for (var index = _handles.Count - 1; index >= 0; index--) _handles[index].Dispose(); _handles.Clear(); }
    }

    private static SafeFileHandle? OpenChecked(string path, bool directory, bool missingAllowed)
    {
        const uint openReparsePoint = 0x00200000, backupSemantics = 0x02000000;
        var handle = CreateFileW(path, directory ? 0x81u : 0x80000000u, directory ? 3u : 1u, IntPtr.Zero,
            3, openReparsePoint | (directory ? backupSemantics : 0), IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error(); handle.Dispose();
            if (missingAllowed && error is 2 or 3) return null;
            throw new IOException("Кэш недоступен.", new Win32Exception(error));
        }
        try
        {
            ValidateChecked(handle, path, directory);
            return handle;
        }
        catch { handle.Dispose(); throw; }
    }

    private static void ValidateChecked(SafeFileHandle handle, string path, bool directory)
    {
        if (!GetFileInformationByHandle(handle, out var info)) throw new IOException("Не удалось проверить файл кэша.", new Win32Exception(Marshal.GetLastWin32Error()));
        if ((info.Attributes & FileAttributes.ReparsePoint) != 0 || ((info.Attributes & FileAttributes.Directory) != 0) != directory
            || !directory && info.NumberOfLinks != 1) throw new IOException("Путь кэша содержит перенаправление.");
        var final = new StringBuilder(32768);
        var length = GetFinalPathNameByHandleW(handle, final, (uint)final.Capacity, 0);
        if (length == 0 || length >= final.Capacity || !string.Equals(NormalizePath(final.ToString()), NormalizePath(path), StringComparison.OrdinalIgnoreCase))
            throw new IOException("Путь кэша изменился.");
    }

    private static string NormalizePath(string path)
    {
        if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) path = @"\\" + path[8..];
        else if (path.StartsWith(@"\\?\", StringComparison.Ordinal)) path = path[4..];
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileInformation
    {
        internal FileAttributes Attributes;
        internal uint CreationTimeLow, CreationTimeHigh, LastAccessTimeLow, LastAccessTimeHigh, LastWriteTimeLow, LastWriteTimeHigh;
        internal uint VolumeSerialNumber, FileSizeHigh, FileSizeLow, NumberOfLinks, FileIndexHigh, FileIndexLow;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string fileName, uint desiredAccess, uint shareMode,
        IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInformation information);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle handle, StringBuilder path, uint length, uint flags);
}
