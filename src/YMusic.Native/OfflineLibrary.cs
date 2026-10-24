using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Collections.Concurrent;

namespace YMusic.Native;

internal sealed record OfflineItem([property: JsonRequired] string Id, [property: JsonRequired] string RequestId,
    [property: JsonRequired] string Title, [property: JsonRequired] string Artist, [property: JsonRequired] string Album,
    [property: JsonRequired] long DurationMs, [property: JsonRequired] string FileName, [property: JsonRequired] string Codec,
    [property: JsonRequired] int Bitrate, [property: JsonRequired] long Bytes, [property: JsonRequired] string Sha256,
    [property: JsonRequired] DateTimeOffset SavedUtc, string? CoverUri = null, IReadOnlyList<ArtistReference>? Artists = null, string? AlbumId = null);

internal sealed record OfflineRemovalResult(int Removed, int Preserved, int Failed, int Forgotten);

internal sealed class OfflineLibrary : IDisposable
{
    public string DirectoryPath { get; }
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
    private readonly string _indexPath;
    private readonly string _playlistIndexPath;
    private readonly object _cacheLock = new();
    private LibraryCache? _libraryCache;
    private PlaylistCache? _playlistCache;
    private static readonly JsonSerializerOptions LibraryJson = new()
    { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, MaxDepth = 48, WriteIndented = true };
    private static readonly JsonSerializerOptions PlaylistJson = new()
    { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, MaxDepth = 48, WriteIndented = true };
    internal OfflineLibrary(string? directory = null, AudioBuffer? downloader = null)
    {
        DirectoryPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory ?? BrandPaths.Current.MusicDirectory));
        _indexPath = Path.Combine(DirectoryPath, "library.index.json");
        _playlistIndexPath = Path.Combine(DirectoryPath, "playlists.index.json");
        _directoryState = DirectoryLocks.GetOrAdd(DirectoryPath, _ => new DirectoryState());
        _writeLock = _directoryState.Gate;
        _downloader = downloader ?? new AudioBuffer();
    }
    public IReadOnlyList<OfflineItem> Read() => LoadLibrary().Items;

    // No API requests and no media hashing: first load parses the index, subsequent
    // lookups validate only index timestamp/length and candidate media existence/length.
    public OfflineItem? Find(TrackRow track)
    {
        ArgumentNullException.ThrowIfNull(track);
        return Find(track, LoadLibrary());
    }
    public IReadOnlyList<TrackRow> Tracks()
        => Read().Select((item, i) =>
        {
            var valid = ValidFile(item);
            return new TrackRow
            {
            Number = i + 1, Id = item.Id, RequestId = item.RequestId, Title = item.Title, Artist = item.Artist, Artists = CopyArtists(item.Artists),
            Album = item.Album, AlbumId = item.AlbumId, DurationMs = item.DurationMs, CoverUri = item.CoverUri, Available = valid,
            LocalPath = valid ? ResolvePath(item.FileName) : null, LocalQuality = QualityLabel(item)
            };
        }).ToArray();

    public IReadOnlyList<OfflinePlaylist> GetPlaylists() => LoadPlaylists().Items;

    public async Task<OfflinePlaylist> RememberPlaylistAsync(PlaylistDetails playlist, AudioQuality quality, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(playlist);
        if (!Enum.IsDefined(quality)) throw new ArgumentOutOfRangeException(nameof(quality));
        var tracks = playlist.Tracks.Select(TrackMetadata).ToArray();
        var snapshot = new OfflinePlaylist(playlist.Owner, playlist.Kind, playlist.Title, playlist.Revision,
            quality, DateTimeOffset.UtcNow, Array.AsReadOnly(tracks));
        ValidatePlaylist(snapshot);
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var previous = LoadPlaylists(force: true); // Corrupt data is a blocker, never an empty fallback.
            var items = previous.Items.Where(p => p.Owner != snapshot.Owner || p.Kind != snapshot.Kind).ToList();
            items.Insert(0, snapshot);
            await WriteIndexAsync(_playlistIndexPath, new OfflinePlaylistIndex(1, items), PlaylistJson, previous.Stamp, cancellationToken).ConfigureAwait(false);
            lock (_cacheLock) _playlistCache = null;
            return snapshot;
        }
        finally { _writeLock.Release(); }
    }

    public OfflinePlaylistStatus GetPlaylistStatus(string owner, string kind, IReadOnlyList<TrackRow>? currentTracks = null)
    {
        var snapshot = GetPlaylists().FirstOrDefault(p => p.Owner == owner && p.Kind == kind);
        if (snapshot is null) return new(currentTracks?.Count ?? 0, 0, false, false, false);
        var tracks = currentTracks is null ? snapshot.Tracks : currentTracks.Select(TrackMetadata).ToArray();
        var changed = tracks.Count != snapshot.Tracks.Count
            || !tracks.Zip(snapshot.Tracks).All(pair => SameTrackMetadata(pair.First, pair.Second));
        var library = LoadLibrary();
        var downloaded = tracks.Count(t => t.Available && Find(ToRow(t), library) is not null);
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
            return new TrackRow
            {
                Number = index + 1, Id = track.Id, RequestId = track.RequestId, Title = track.Title, Artist = track.Artist, Artists = CopyArtists(track.Artists),
                Album = track.Album, AlbumId = track.AlbumId, DurationMs = track.DurationMs, CoverUri = track.CoverUri, Available = local is not null,
                LocalPath = local is null ? null : ResolvePath(local.FileName), LocalQuality = local is null ? null : QualityLabel(local)
            };
        }).ToArray();
    }

    public async Task<OfflineItem> SaveAsync(TrackRow track, AudioDescriptor audio, CancellationToken cancellationToken, IProgress<AudioProgress>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(track); ArgumentNullException.ThrowIfNull(audio);
        ValidateAlbumId(track.AlbumId);
        // A key gate coalesces identical requests across instances without serializing
        // unrelated network downloads. Entries are removed when their last waiter leaves.
        var key = track.Id + "\0" + audio.Codec + "\0" + audio.Bitrate;
        using var saveGate = await _directoryState.EnterSaveAsync(key, cancellationToken).ConfigureAwait(false);
        string? buffered = null, partial = null, published = null;
        var committed = false;
        try
        {
            long generation;
            OfflineItem? existing;
            await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                EnsureSafeDirectory();
                generation = _directoryState.Generation;
                existing = LoadLibrary(force: true).Items.FirstOrDefault(item => item.Id == track.Id && item.Codec == audio.Codec && item.Bitrate == audio.Bitrate && ValidFile(item));
            }
            finally { _writeLock.Release(); }
            if (existing is not null && await HasExpectedHashAsync(existing, cancellationToken).ConfigureAwait(false))
            {
                await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    if (_directoryState.Generation != generation) throw DownloadInvalidated();
                    if (LoadLibrary(force: true).Items.Any(item => SameOfflineItem(item, existing)) && ValidFile(existing)) return existing;
                }
                finally { _writeLock.Release(); }
            }
            using (await _directoryState.EnterDownloadAsync(cancellationToken).ConfigureAwait(false))
                buffered = await _downloader.DownloadAsync(audio, cancellationToken, progress).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            EnsureSafeDirectory(create: true);
            // Stage under a unique name before taking the short index commit gate.
            partial = ResolvePath(".ym-audio-" + Guid.NewGuid().ToString("N") + ".partial");
            await using (var input = File.OpenRead(buffered))
            await using (var output = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan))
                await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
            string originalHash, persistedHash;
            await using (var source = File.OpenRead(buffered)) originalHash = Convert.ToHexString(await SHA256.HashDataAsync(source, cancellationToken).ConfigureAwait(false)).ToLowerInvariant();
            await using (var readback = File.OpenRead(partial)) persistedHash = Convert.ToHexString(await SHA256.HashDataAsync(readback, cancellationToken).ConfigureAwait(false)).ToLowerInvariant();
            if (originalHash != persistedHash) throw new MusicException("Не совпала контрольная сумма сохранённого аудио. Файл не добавлен в библиотеку.");
            await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                cancellationToken.ThrowIfCancellationRequested(); EnsureSafeDirectory();
                if (_directoryState.Generation != generation) throw DownloadInvalidated();
                var latest = LoadLibrary(force: true); // Never overwrite corrupt or externally changed data.
                var title = SafeName(track.Artist + " — " + track.Title);
                var id = SafeName(track.Id);
                var filename = $"{title} [{id}-{audio.Codec}-{audio.Bitrate}]{audio.Extension}";
                var destination = ResolvePath(filename);
                if (File.Exists(destination)) { filename = $"{title} [{id}-{Guid.NewGuid():N}]{audio.Extension}"; destination = ResolvePath(filename); }
                File.Move(partial, destination); partial = null; published = destination;
                var item = new OfflineItem(track.Id, track.RequestId, track.Title, track.Artist, track.Album, track.DurationMs, filename,
                    audio.Codec, audio.Bitrate, new FileInfo(destination).Length, persistedHash, DateTimeOffset.UtcNow, track.CoverUri, CopyArtists(track.Artists), track.AlbumId);
                // Preserve a modified media file; only its stale metadata is replaced.
                var items = latest.Items.Where(i => existing is null || i.FileName != existing.FileName || i.Sha256 != existing.Sha256).ToList();
                items.Insert(0, item);
                await WriteIndexAsync(_indexPath, items, LibraryJson, latest.Stamp, cancellationToken).ConfigureAwait(false);
                committed = true;
                lock (_cacheLock) _libraryCache = null;
                return item;
            }
            finally { _writeLock.Release(); }
        }
        finally
        {
            AudioBuffer.Delete(buffered); SafeDeleteStaged(partial);
            if (!committed) SafeDeleteStaged(published);
        }
    }

    public Task<OfflineRemovalResult> RemoveTrackAsync(string trackId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(trackId);
        return RemoveAsync(trackId, cancellationToken);
    }
    public Task<OfflineRemovalResult> ClearAsync(CancellationToken cancellationToken) => RemoveAsync(null, cancellationToken);

    private async Task<OfflineRemovalResult> RemoveAsync(string? trackId, CancellationToken cancellationToken)
    {
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        var staged = new List<(OfflineItem Item, string Original, string Quarantine)>();
        var removed = 0; var preserved = 0; var failed = 0; var committed = false;
        var retained = new List<OfflineItem>();
        try
        {
            EnsureSafeDirectory();
            var library = LoadLibrary(force: true);
            // Both indexes are preflighted before a single file is moved.
            var playlists = LoadPlaylists(force: true);
            var selected = library.Items.Where(i => trackId is null || i.Id == trackId).ToArray();
            var remaining = library.Items.Where(i => trackId is not null && i.Id != trackId).ToList();
            _directoryState.Generation++; // An already running Save cannot republish after removal.
            foreach (var item in selected.DistinctBy(i => i.FileName, StringComparer.OrdinalIgnoreCase))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var path = ResolvePath(item.FileName);
                if (!File.Exists(path)) { preserved++; continue; }
                if (remaining.Any(i => i.FileName.Equals(item.FileName, StringComparison.OrdinalIgnoreCase))) { preserved++; continue; }
                try
                {
                    if (!ValidFile(item)) { preserved++; continue; }
                    if ((File.GetAttributes(path) & FileAttributes.ReadOnly) != 0) { failed++; retained.Add(item); continue; }
                    var quarantine = ResolvePath(".ym-delete-" + Guid.NewGuid().ToString("N") + ".pending");
                    // Deny writers during verification and rename. Delete sharing allows
                    // our rename while holding the handle to the checked bytes.
                    await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
                    if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0 || file.Length != item.Bytes
                        || Convert.ToHexString(await SHA256.HashDataAsync(file, cancellationToken).ConfigureAwait(false)).ToLowerInvariant() != item.Sha256)
                    { preserved++; continue; }
                    EnsureSafeDirectory();
                    File.Move(path, quarantine);
                    staged.Add((item, path, quarantine));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { failed++; retained.Add(item); }
            }
            remaining.AddRange(retained);
            cancellationToken.ThrowIfCancellationRequested();
            // Clear resets snapshots too; deleting one track retains snapshot metadata
            // and its completion is recomputed from actual remaining managed files.
            IndexStamp? snapshotWritten = null;
            try
            {
                if (trackId is null)
                {
                    snapshotWritten = await WriteIndexAsync(_playlistIndexPath, new OfflinePlaylistIndex(1, []), PlaylistJson, playlists.Stamp, cancellationToken).ConfigureAwait(false);
                }
                await WriteIndexAsync(_indexPath, remaining, LibraryJson, library.Stamp, cancellationToken).ConfigureAwait(false);
                committed = true;
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
            foreach (var entry in staged)
            {
                try
                {
                    EnsureSafeDirectory();
                    await using var file = new FileStream(entry.Quarantine, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
                    if ((File.GetAttributes(entry.Quarantine) & FileAttributes.ReparsePoint) != 0 || file.Length != entry.Item.Bytes
                        || Convert.ToHexString(await SHA256.HashDataAsync(file, CancellationToken.None).ConfigureAwait(false)).ToLowerInvariant() != entry.Item.Sha256)
                    { RestoreStaged(entry.Original, entry.Quarantine); preserved++; continue; }
                    File.Delete(entry.Quarantine); removed++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    var restoredPath = RestoreStaged(entry.Original, entry.Quarantine); failed++;
                    if (restoredPath is not null)
                    {
                        var restored = entry.Item with { FileName = Path.GetFileName(restoredPath) };
                        if (await HasExpectedHashAsync(restored, CancellationToken.None).ConfigureAwait(false)) retained.Add(restored);
                    }
                }
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
            return new(removed, preserved, failed, selected.Length - retained.Count);
        }
        finally
        {
            if (!committed) foreach (var entry in staged) RestoreStaged(entry.Original, entry.Quarantine);
            lock (_cacheLock) { _libraryCache = null; _playlistCache = null; }
            _writeLock.Release();
        }
    }

    private async Task<bool> HasExpectedHashAsync(OfflineItem item, CancellationToken cancellationToken)
    {
        if (!ValidFile(item)) return false;
        try
        {
            await using var file = new FileStream(ResolvePath(item.FileName), FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
            return file.Length == item.Bytes && Convert.ToHexString(await SHA256.HashDataAsync(file, cancellationToken).ConfigureAwait(false)).ToLowerInvariant() == item.Sha256;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }
    private string? RestoreStaged(string original, string quarantine)
    {
        try
        {
            EnsureSafeDirectory();
            if (!File.Exists(quarantine)) return File.Exists(original) ? original : null;
            var recovered = File.Exists(original) ? ResolvePath(".ym-recovered-" + Guid.NewGuid().ToString("N") + Path.GetExtension(original)) : original;
            File.Move(quarantine, recovered); return recovered;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or MusicException) { return null; /* Keep the uniquely named file for recovery. */ }
    }
    private void SafeDeleteStaged(string? path)
    {
        if (path is null) return;
        try { EnsureSafeDirectory(); if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0) AudioBuffer.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or MusicException) { }
    }
    private void EnsureSafeDirectory(bool create = false)
    {
        for (var part = new DirectoryInfo(DirectoryPath); part is not null; part = part.Parent)
            if (part.Exists && (part.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new MusicException("Папка офлайн-музыки содержит ссылку или перенаправление. Операция остановлена, файлы сохранены.");
        if (create) Directory.CreateDirectory(DirectoryPath);
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

    private LibraryCache LoadLibrary(bool force = false)
    {
        lock (_cacheLock)
        {
            EnsureSafeDirectory();
            var stamp = Stamp(_indexPath);
            if (!force && _libraryCache is { } cached && cached.Stamp == stamp) return cached;
            try
            {
                var items = stamp.Exists ? JsonSerializer.Deserialize<List<OfflineItem>>(File.ReadAllText(_indexPath), LibraryJson) ?? throw new JsonException() : [];
                foreach (var item in items)
                {
                    if (item is null || string.IsNullOrWhiteSpace(item.Id) || string.IsNullOrWhiteSpace(item.RequestId)
                        || item.Title is null || item.Artist is null || item.Album is null || string.IsNullOrWhiteSpace(item.Codec)
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

    private PlaylistCache LoadPlaylists(bool force = false)
    {
        lock (_cacheLock)
        {
            EnsureSafeDirectory();
            var stamp = Stamp(_playlistIndexPath);
            if (!force && _playlistCache is { } cached && cached.Stamp == stamp) return cached;
            try
            {
                var data = stamp.Exists ? JsonSerializer.Deserialize<OfflinePlaylistIndex>(File.ReadAllText(_playlistIndexPath), PlaylistJson) ?? throw new JsonException() : new(1, []);
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

    private async Task<IndexStamp> WriteIndexAsync<T>(string path, T data, JsonSerializerOptions options, IndexStamp expected, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureSafeDirectory(create: true);
        var temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            var serialized = JsonSerializer.Serialize(data, options);
            await File.WriteAllTextAsync(temp, serialized, cancellationToken).ConfigureAwait(false);
            if (await File.ReadAllTextAsync(temp, cancellationToken).ConfigureAwait(false) != serialized) throw new MusicException("Не прошла проверка сохранённого индекса офлайн-библиотеки.");
            cancellationToken.ThrowIfCancellationRequested();
            EnsureSafeDirectory();
            if (Stamp(path) != expected) throw IndexChanged();
            var writtenStamp = Stamp(temp);
            File.Move(temp, path, true);
            return writtenStamp;
        }
        finally { SafeDeleteStaged(temp); }
    }

    private static OfflinePlaylistTrack TrackMetadata(TrackRow track)
    {
        ArgumentNullException.ThrowIfNull(track);
        return new(track.Id, track.RequestId, track.Title, track.Artist, track.Album, track.DurationMs, track.Available, track.CoverUri, CopyArtists(track.Artists), track.AlbumId);
    }
    private static TrackRow ToRow(OfflinePlaylistTrack track) => new()
    { Id = track.Id, RequestId = track.RequestId, Title = track.Title, Artist = track.Artist, Artists = CopyArtists(track.Artists), Album = track.Album, AlbumId = track.AlbumId, DurationMs = track.DurationMs, Available = track.Available, CoverUri = track.CoverUri };
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
        => left with { Artists = null, AlbumId = null } == right with { Artists = null, AlbumId = null };
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
                || track.Title is null || track.Artist is null || track.Album is null || track.DurationMs < 0) throw new JsonException();
            ValidateArtists(track.Artists);
            ValidateAlbumId(track.AlbumId);
        }
    }
    private static string QualityLabel(OfflineItem item)
        => item.Codec.StartsWith("flac", StringComparison.OrdinalIgnoreCase) ? "Lossless · FLAC" : item.Codec.ToUpperInvariant() + (item.Bitrate > 0 ? $" · {item.Bitrate} кбит/с" : "");
    private static IndexStamp Stamp(string path)
    {
        var file = new FileInfo(path);
        if (file.Exists && (file.Attributes & FileAttributes.ReparsePoint) != 0) throw new MusicException("Индекс офлайн-библиотеки является ссылкой. Операция остановлена.");
        return file.Exists ? new(true, file.Length, file.LastWriteTimeUtc.Ticks) : new(false, 0, 0);
    }
    private static MusicException IndexChanged() => new("Индекс офлайн-библиотеки изменился во время записи. Ничего не перезаписывалось; перечитайте библиотеку перед повтором.");
    private sealed class DirectoryState
    {
        public SemaphoreSlim Gate { get; } = new(1);
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
