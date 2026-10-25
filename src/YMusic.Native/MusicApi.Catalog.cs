using System.Globalization;
using System.Net.Http;
using System.Text.Json;

namespace YMusic.Native;

// Protocol source: jrfrigat/YandexMusic, commit 8c929094386137669766632f9cc2c97d3b05f670
// SearchClient, LibraryClient, AlbumsClient, ArtistsClient, PlaylistsClient (MIT).
internal sealed partial class MusicApi
{
    private readonly SemaphoreSlim _catalogMutationGate = new(1, 1);
    private const int CatalogTrackBatchSize = 100;

    public async Task<CatalogPage> SearchCatalogAsync(Session session, string query, string type,
        int page, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        CatalogPageIndex(page);
        string[] types = ["all", "track", "album", "artist", "playlist"];
        if (!types.Contains(type, StringComparer.Ordinal))
            throw new ArgumentException("Поддерживаются типы all, track, album, artist и playlist.", nameof(type));
        using var json = await ApiAsync(HttpMethod.Get,
            $"/search?text={Uri.EscapeDataString(query)}&type={type}&page={page.ToString(CultureInfo.InvariantCulture)}&nocorrect=false",
            session, null, cancellationToken).ConfigureAwait(false);
        var result = CatalogResult(json);
        CatalogObject(result, "поиска");
        (string Section, string Kind)[] sections = [("tracks", "track"), ("albums", "album"), ("artists", "artist"), ("playlists", "playlist")];
        var items = new List<CatalogItem>();
        var hasMore = false;
        foreach (var entry in sections.Where(s => type == "all" || s.Kind == type)
                     .OrderBy(s => result.TryGetProperty(s.Section, out var section) ? Int(section, "order", int.MaxValue) : int.MaxValue))
        {
            if (!result.TryGetProperty(entry.Section, out var section) || section.ValueKind == JsonValueKind.Null) continue;
            var rows = CatalogArray(section, "results", "результатов поиска");
            var total = CatalogNonNegativeInt(section, "total", "поиска");
            var perPage = Int(section, "perPage");
            if (total > 0 && perPage <= 0) throw new MusicException("В ответе поиска нет размера страницы.");
            hasMore |= (long)(page + 1) * perPage < total;
            foreach (var row in rows.EnumerateArray()) items.Add(CatalogEntity(row, entry.Kind));
        }
        return new(items, page, hasMore);
    }

    public async Task<IReadOnlyList<CatalogItem>> GetLikedAlbumsAsync(Session session, string uid, CancellationToken cancellationToken)
    {
        using var json = await ApiAsync(HttpMethod.Get, $"/users/{CatalogId(uid)}/likes/albums?rich=True", session, null, cancellationToken).ConfigureAwait(false);
        var rows = CatalogResult(json);
        CatalogArrayValue(rows, "любимых альбомов");
        return rows.EnumerateArray().Select(row => CatalogEntity(CatalogUnwrapEntity(row, "album"), "album")).ToArray();
    }

    public async Task<IReadOnlyList<CatalogItem>> GetLikedArtistsAsync(Session session, string uid, CancellationToken cancellationToken)
    {
        using var json = await ApiAsync(HttpMethod.Get, $"/users/{CatalogId(uid)}/likes/artists?with-timestamps=True", session, null, cancellationToken).ConfigureAwait(false);
        var rows = CatalogResult(json);
        CatalogArrayValue(rows, "любимых исполнителей");
        return rows.EnumerateArray().Select(row => CatalogEntity(CatalogUnwrapEntity(row, "artist"), "artist")).ToArray();
    }

    public async Task<IReadOnlyList<CatalogItem>> GetPlaylistsAsync(Session session, string uid, CancellationToken cancellationToken)
    {
        using var json = await ApiAsync(HttpMethod.Get, $"/users/{CatalogId(uid)}/playlists/list", session, null, cancellationToken).ConfigureAwait(false);
        var rows = CatalogResult(json);
        CatalogArrayValue(rows, "плейлистов");
        return rows.EnumerateArray().Select(row => CatalogEntity(row, "playlist", uid)).ToArray();
    }

    public async Task<IReadOnlyList<TrackRow>> GetAlbumTracksAsync(Session session, string albumId, CancellationToken cancellationToken)
        => (await GetAlbumDetailsAsync(session, albumId, cancellationToken).ConfigureAwait(false)).Tracks;

    public async Task<AlbumDetails> GetAlbumDetailsAsync(Session session, string albumId, CancellationToken cancellationToken)
    {
        using var json = await ApiAsync(HttpMethod.Get, $"/albums/{CatalogId(albumId)}/with-tracks", session, null, cancellationToken).ConfigureAwait(false);
        var album = CatalogResult(json);
        if (Text(album, "id") != albumId) throw new MusicException("Яндекс вернул другой альбом.");
        var volumes = CatalogArray(album, "volumes", "дисков альбома");
        var rows = new List<TrackRow>();
        foreach (var volume in volumes.EnumerateArray())
        {
            CatalogArrayValue(volume, "трека альбома");
            foreach (var track in volume.EnumerateArray())
                rows.Add(CatalogTrack(track, rows.Count + 1, albumId));
        }
        return new(albumId, CatalogMetadataText(album, "title"), CatalogArtistNames(album), rows,
            EntityArtwork(album, "album"), CatalogOptionalYear(album), CatalogOptionalDate(album, "releaseDate"));
    }

    public async Task<IReadOnlyList<TrackRow>> GetArtistTracksAsync(Session session, string artistId, int page, CancellationToken cancellationToken)
    {
        CatalogPageIndex(page);
        using var json = await ApiAsync(HttpMethod.Get,
            $"/artists/{CatalogId(artistId)}/tracks?page={page.ToString(CultureInfo.InvariantCulture)}&page-size={CatalogTrackBatchSize}",
            session, null, cancellationToken).ConfigureAwait(false);
        var rows = CatalogArray(CatalogResult(json), "tracks", "треков исполнителя");
        return rows.EnumerateArray().Select((row, index) => CatalogTrack(row, page * CatalogTrackBatchSize + index + 1)).ToArray();
    }

    public async Task<PlaylistDetails> GetPlaylistAsync(Session session, string owner, string kind, CancellationToken cancellationToken)
    {
        using var json = await ApiAsync(HttpMethod.Get, $"/users/{CatalogId(owner)}/playlists/{CatalogId(kind)}", session, null, cancellationToken).ConfigureAwait(false);
        var result = CatalogResult(json);
        var info = CatalogPlaylistHeader(result, owner, kind) with
        {
            CoverUri = EntityArtwork(result, "playlist"), OwnerName = CatalogPlaylistOwnerName(result),
            Created = CatalogOptionalDate(result, "created"), Modified = CatalogOptionalDate(result, "modified")
        };
        var entries = CatalogArray(result, "tracks", "треков плейлиста");
        if (CatalogNonNegativeInt(result, "trackCount", "плейлиста") != entries.GetArrayLength())
            throw new MusicException("Получен неполный список плейлиста. Изменения не отправлены.");
        var references = new List<LikedId>();
        foreach (var entry in entries.EnumerateArray())
        {
            var id = CatalogRequiredText(entry, "id", "трека плейлиста");
            var albumId = Text(entry, "albumId");
            references.Add(new(id, albumId.Length == 0 ? null : albumId));
        }
        var rows = new List<TrackRow>(references.Count);
        for (var offset = 0; offset < references.Count; offset += CatalogTrackBatchSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // GetTracksAsync retains request IDs and leaves missing entries unavailable in place.
            rows.AddRange(await GetTracksAsync(session, references.Skip(offset).Take(CatalogTrackBatchSize).ToArray(), offset, cancellationToken).ConfigureAwait(false));
        }
        return info with { Tracks = rows };
    }

    public async Task SetTrackLikeAsync(Session session, string uid, LikedId track, bool liked, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(track);
        CatalogTrackReference(track, requireAlbum: false);
        await _catalogMutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await CatalogVerifyOwnerAsync(session, uid, cancellationToken).ConfigureAwait(false);
            using var json = await CatalogWriteAsync(
                $"/users/{CatalogId(uid)}/likes/tracks/{(liked ? "add-multiple" : "remove")}", session,
                Form(("track-ids", track.RequestId)), cancellationToken).ConfigureAwait(false);
            var result = CatalogResult(json);
            try { _ = CatalogNonNegativeInt(result, "revision", "изменения лайка"); }
            catch (MusicException) { throw CatalogUnconfirmedWrite(); }
            try
            {
                var current = await GetLikedIdsAsync(session, uid, cancellationToken).ConfigureAwait(false);
                if (current.Any(t => t.Id == track.Id) != liked)
                    throw new MusicException("Состояние лайка не совпало с запросом.");
            }
            catch (Exception ex) when (ex is MusicException or HttpRequestException or OperationCanceledException)
            { throw CatalogUnconfirmedWrite(); }
        }
        finally { _catalogMutationGate.Release(); }
    }

    public async Task<PlaylistDetails> CreatePlaylistAsync(Session session, string title, string visibility, CancellationToken cancellationToken)
    {
        CatalogTitle(title);
        if (visibility is not ("private" or "public")) throw new ArgumentException("Выберите private или public.", nameof(visibility));
        await _catalogMutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var account = await GetAccountAsync(session, cancellationToken, includeAvatar: false).ConfigureAwait(false);
            using var json = await CatalogWriteAsync($"/users/{CatalogId(account.Uid)}/playlists/create", session,
                Form(("title", title), ("visibility", visibility)), cancellationToken).ConfigureAwait(false);
            var result = CatalogResult(json);
            var kind = Text(result, "kind");
            var created = CatalogMutationHeader(result, account.Uid, kind);
            if (created.Title != title || created.Visibility != visibility || created.IsGenerated)
                throw CatalogUnconfirmedWrite();
            var current = await CatalogReadAfterWriteAsync(session, created, cancellationToken).ConfigureAwait(false);
            if (current.Title != title || current.Visibility != visibility) throw CatalogUnconfirmedWrite();
            return current;
        }
        finally { _catalogMutationGate.Release(); }
    }

    public Task<PlaylistDetails> CreatePlaylistAsync(Session session, string title, CancellationToken cancellationToken)
        => CreatePlaylistAsync(session, title, "private", cancellationToken);

    public async Task<PlaylistDetails> RenamePlaylistAsync(Session session, PlaylistDetails expected, string title, CancellationToken cancellationToken)
    {
        CatalogTitle(title);
        expected = CatalogSnapshot(expected);
        await _catalogMutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var fresh = await CatalogWritableAsync(session, expected, cancellationToken).ConfigureAwait(false);
            using var json = await CatalogWriteAsync(CatalogPlaylistPath(fresh) + "/name", session,
                Form(("value", title)), cancellationToken).ConfigureAwait(false);
            var response = CatalogMutationHeader(CatalogResult(json), fresh.Owner, fresh.Kind);
            if (response.Title != title || response.Revision < fresh.Revision) throw CatalogUnconfirmedWrite();
            var current = await CatalogReadAfterWriteAsync(session, fresh, cancellationToken).ConfigureAwait(false);
            if (current.Title != title || !CatalogSameTracks(current.Tracks, fresh.Tracks)) throw CatalogUnconfirmedWrite();
            return current;
        }
        finally { _catalogMutationGate.Release(); }
    }

    public async Task DeletePlaylistAsync(Session session, PlaylistDetails expected, CancellationToken cancellationToken)
    {
        expected = CatalogSnapshot(expected);
        await _catalogMutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var fresh = await CatalogWritableAsync(session, expected, cancellationToken).ConfigureAwait(false);
            using var json = await CatalogWriteAsync(CatalogPlaylistPath(fresh) + "/delete", session, null, cancellationToken).ConfigureAwait(false);
            var result = CatalogResult(json);
            if (result.ValueKind != JsonValueKind.String || !string.Equals(result.GetString(), "ok", StringComparison.OrdinalIgnoreCase))
                throw new MusicException("Яндекс не подтвердил удаление плейлиста. Перечитайте список перед повтором.");
            try
            {
                var current = await GetPlaylistsAsync(session, fresh.Owner, cancellationToken).ConfigureAwait(false);
                if (current.Any(p => p.Owner == fresh.Owner && p.PlaylistKind == fresh.Kind)) throw CatalogUnconfirmedWrite();
            }
            catch (Exception ex) when (ex is MusicException or HttpRequestException or OperationCanceledException)
            { throw CatalogUnconfirmedWrite(); }
        }
        finally { _catalogMutationGate.Release(); }
    }

    public async Task<PlaylistDetails> InsertPlaylistTracksAsync(Session session, PlaylistDetails expected,
        IReadOnlyList<LikedId> tracks, int at, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tracks);
        expected = CatalogSnapshot(expected);
        tracks = tracks.ToArray();
        if (tracks.Count == 0 || tracks.Count > 1000) throw new ArgumentException("Выберите от 1 до 1000 треков.", nameof(tracks));
        if (at < 0 || at > expected.Tracks.Count) throw new ArgumentOutOfRangeException(nameof(at));
        foreach (var track in tracks)
        {
            CatalogTrackReference(track, requireAlbum: true);
        }
        var diff = JsonSerializer.Serialize(new[] { new { op = "insert", at, tracks = tracks.Select(t => new { id = t.Id, album_id = t.AlbumId }).ToArray() } });
        var desired = expected.Tracks.Select(t => t.RequestId).ToList();
        desired.InsertRange(at, tracks.Select(t => t.RequestId));
        return await CatalogChangeAsync(session, expected, diff, desired, cancellationToken).ConfigureAwait(false);
    }

    public Task<PlaylistDetails> RemovePlaylistTrackAsync(Session session, PlaylistDetails expected,
        int index, string expectedRequestId, CancellationToken cancellationToken)
    {
        expected = CatalogSnapshot(expected);
        if (index < 0 || index >= expected.Tracks.Count) throw new ArgumentOutOfRangeException(nameof(index));
        if (expected.Tracks[index].RequestId != expectedRequestId)
            throw new MusicException("Выбранный трек изменился. Перечитайте плейлист.");
        var diff = JsonSerializer.Serialize(new[] { new { op = "delete", from = index, to = index + 1 } });
        var desired = expected.Tracks.Where((_, i) => i != index).Select(t => t.RequestId).ToArray();
        return CatalogChangeAsync(session, expected, diff, desired, cancellationToken);
    }

    private async Task<PlaylistDetails> CatalogChangeAsync(Session session, PlaylistDetails expected, string diff,
        IReadOnlyList<string> desired, CancellationToken cancellationToken)
    {
        await _catalogMutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var fresh = await CatalogWritableAsync(session, expected, cancellationToken).ConfigureAwait(false);
            // Upstream's wire contract is form fields; diff itself is serialized JSON (album_id, exclusive to).
            using var json = await CatalogWriteAsync(CatalogPlaylistPath(fresh) + "/change", session,
                Form(("kind", fresh.Kind), ("revision", fresh.Revision.ToString(CultureInfo.InvariantCulture)), ("diff", diff)), cancellationToken).ConfigureAwait(false);
            var response = CatalogMutationHeader(CatalogResult(json), fresh.Owner, fresh.Kind);
            if (response.Revision <= fresh.Revision) throw CatalogUnconfirmedWrite();
            var current = await CatalogReadAfterWriteAsync(session, response, cancellationToken).ConfigureAwait(false);
            if (current.Revision < response.Revision || !current.Tracks.Select(t => t.RequestId).SequenceEqual(desired, StringComparer.Ordinal))
                throw CatalogUnconfirmedWrite();
            return current;
        }
        finally { _catalogMutationGate.Release(); }
    }

    private async Task CatalogVerifyOwnerAsync(Session session, string owner, CancellationToken cancellationToken)
    {
        _ = CatalogId(owner);
        var account = await GetAccountAsync(session, cancellationToken, includeAvatar: false).ConfigureAwait(false);
        if (account.Uid != owner) throw new MusicException("Изменять можно только коллекцию текущего аккаунта.");
    }

    private async Task<PlaylistDetails> CatalogWritableAsync(Session session, PlaylistDetails expected, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(expected);
        if (expected.Revision <= 0 || expected.IsGenerated) throw new MusicException("Этот плейлист нельзя изменять. Нужна актуальная пользовательская подборка.");
        await CatalogVerifyOwnerAsync(session, expected.Owner, cancellationToken).ConfigureAwait(false);
        var owned = await GetPlaylistsAsync(session, expected.Owner, cancellationToken).ConfigureAwait(false);
        if (!owned.Any(p => p.Owner == expected.Owner && p.PlaylistKind == expected.Kind))
            throw new MusicException("Плейлист отсутствует среди собственных подборок текущего аккаунта.");
        var fresh = await GetPlaylistAsync(session, expected.Owner, expected.Kind, cancellationToken).ConfigureAwait(false);
        if (fresh.IsGenerated || fresh.Revision != expected.Revision || !CatalogSameTracks(fresh.Tracks, expected.Tracks))
            throw new MusicException("Плейлист изменился на сервере. Перечитайте его перед изменением; запись не отправлена.");
        // rename/delete have no documented atomic revision parameter. This preflight detects stale UI;
        // change uses the revision in the server request and is the optimistic-concurrency operation.
        return fresh;
    }

    private async Task<PlaylistDetails> CatalogReadAfterWriteAsync(Session session, PlaylistDetails playlist, CancellationToken cancellationToken)
    {
        try { return await GetPlaylistAsync(session, playlist.Owner, playlist.Kind, cancellationToken).ConfigureAwait(false); }
        catch (Exception ex) when (ex is MusicException or HttpRequestException or OperationCanceledException)
        { throw CatalogUnconfirmedWrite(); }
    }

    private async Task<JsonDocument> CatalogWriteAsync(string path, Session session, HttpContent? content, CancellationToken cancellationToken)
    {
        using (content)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try { return await ApiAsync(HttpMethod.Post, path, session, content, cancellationToken).ConfigureAwait(false); }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
            { throw CatalogUnconfirmedWrite(); }
        }
    }

    private static PlaylistDetails CatalogMutationHeader(JsonElement result, string owner, string kind)
    {
        try
        {
            if (kind.Length == 0) throw CatalogUnconfirmedWrite();
            return CatalogPlaylistHeader(result, owner, kind);
        }
        catch (MusicException) { throw CatalogUnconfirmedWrite(); }
    }

    private static bool CatalogSameTracks(IReadOnlyList<TrackRow> left, IReadOnlyList<TrackRow> right)
        => left.Select(t => t.RequestId).SequenceEqual(right.Select(t => t.RequestId), StringComparer.Ordinal);

    private static PlaylistDetails CatalogSnapshot(PlaylistDetails playlist)
    {
        ArgumentNullException.ThrowIfNull(playlist);
        ArgumentNullException.ThrowIfNull(playlist.Tracks);
        return playlist with { Tracks = playlist.Tracks.ToArray() };
    }

    private static string CatalogPlaylistPath(PlaylistDetails playlist)
        => $"/users/{CatalogId(playlist.Owner)}/playlists/{CatalogId(playlist.Kind)}";

    private static PlaylistDetails CatalogPlaylistHeader(JsonElement result, string owner, string kind)
    {
        CatalogObject(result, "плейлиста");
        if (CatalogOwner(result) != owner || Text(result, "kind") != kind)
            throw new MusicException("Ответ Яндекса относится к другому плейлисту.");
        var revision = CatalogNonNegativeInt(result, "revision", "плейлиста");
        return new(owner, kind, CatalogRequiredText(result, "title", "плейлиста"), Text(result, "description"),
            revision, [], Text(result, "generatedPlaylistType").Length > 0 || Text(result, "artistPlaylistType").Length > 0, Text(result, "visibility"));
    }

    private static CatalogItem CatalogEntity(JsonElement row, string kind, string? contextOwner = null)
    {
        CatalogObject(row, "каталога");
        var id = CatalogRequiredText(row, kind == "playlist" ? "kind" : "id", "каталога");
        var title = CatalogRequiredText(row, kind == "artist" ? "name" : "title", "каталога");
        var subtitle = kind == "artist" ? "Исполнитель" : CatalogArtistNames(row);
        string? albumId = null;
        if (kind == "track" && row.TryGetProperty("albums", out var albums) && albums.ValueKind == JsonValueKind.Array && albums.GetArrayLength() > 0)
            albumId = Text(albums[0], "id") is { Length: > 0 } value ? value : null;
        if (kind != "playlist") return new(id, kind, title, subtitle, AlbumId: albumId, CoverUri: EntityArtwork(row, kind),
            Track: kind == "track" ? CatalogTrack(row, 0) : null);
        var owner = CatalogOwner(row);
        if (owner.Length == 0) owner = contextOwner ?? "";
        if (owner.Length == 0) throw new MusicException("В данных плейлиста отсутствует владелец.");
        if (contextOwner is not null && owner != contextOwner) throw new MusicException("Список содержит плейлист другого владельца.");
        return new(id, kind, title, Text(row, "description"), owner, id, CoverUri: EntityArtwork(row, kind), OwnerName: CatalogPlaylistOwnerName(row));
    }

    private static JsonElement CatalogUnwrapEntity(JsonElement row, string property)
    {
        CatalogObject(row, "коллекции");
        return row.TryGetProperty(property, out var entity) && entity.ValueKind == JsonValueKind.Object ? entity : row;
    }

    private static TrackRow CatalogTrack(JsonElement track, int number, string? contextAlbumId = null)
    {
        var id = CatalogRequiredText(track, "id", "трека");
        var albumId = contextAlbumId;
        var navigationAlbum = TrackAlbum(track, contextAlbumId);
        if (track.TryGetProperty("albums", out var albums) && albums.ValueKind == JsonValueKind.Array)
        {
            foreach (var album in albums.EnumerateArray())
            {
                var candidate = Text(album, "id");
                if (albumId is null && candidate.Length > 0) albumId = candidate;
                if (candidate == albumId) break;
            }
        }
        var title = Text(track, "title");
        var version = Text(track, "version");
        return new()
        {
            Number = number, Id = id, RequestId = new LikedId(id, albumId).RequestId,
            Title = title.Length == 0 ? "Недоступный трек" : title + (version.Length == 0 ? "" : " · " + version),
            Artist = CatalogArtistNames(track), Artists = ArtistReferences(track), Album = navigationAlbum.Title, AlbumId = navigationAlbum.Id, DurationMs = Int(track, "durationMs"), CoverUri = TrackArtwork(track),
            Available = title.Length > 0 && (Bool(track, "available") || Bool(track, "availableForPremiumUsers") || Bool(track, "availableFullWithoutPermission"))
        };
    }

    private static string CatalogArtistNames(JsonElement row)
        => row.TryGetProperty("artists", out var artists) && artists.ValueKind == JsonValueKind.Array
            ? string.Join(", ", artists.EnumerateArray().Select(a => Text(a, "name")).Where(n => n.Length > 0)) : "";

    // Optional presentation metadata cannot make a previously readable collection fail.
    // Accept only the documented field types and ISO dates; never infer a release from tracks or titles.
    private static string CatalogMetadataText(JsonElement row, string property)
        => row.ValueKind == JsonValueKind.Object && row.TryGetProperty(property, out var value)
            && value.ValueKind == JsonValueKind.String ? value.GetString()?.Trim() ?? "" : "";

    private static int? CatalogOptionalYear(JsonElement row)
        => row.TryGetProperty("year", out var value) && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt32(out var year) && year is >= 1 and <= 9999 ? year : null;

    private static DateOnly? CatalogOptionalDate(JsonElement row, string property)
    {
        var value = CatalogMetadataText(row, property);
        if (DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)) return date;
        string[] formats = ["yyyy-MM-dd'T'HH:mm:sszzz", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFzzz",
            "yyyy-MM-dd'T'HH:mm:ss'Z'", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'"];
        return DateTimeOffset.TryParseExact(value, formats, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal, out var timestamp) ? DateOnly.FromDateTime(timestamp.DateTime) : null;
    }

    private static string? CatalogPlaylistOwnerName(JsonElement row)
    {
        if (!row.TryGetProperty("owner", out var owner) || owner.ValueKind != JsonValueKind.Object) return null;
        foreach (var property in new[] { "name", "displayName", "login" })
            if (CatalogMetadataText(owner, property) is { Length: > 0 } name) return name;
        return null;
    }

    private static string CatalogOwner(JsonElement row)
    {
        var uid = Text(row, "uid");
        var nested = row.TryGetProperty("owner", out var owner) ? Text(owner, "uid") : "";
        if (uid.Length > 0 && nested.Length > 0 && uid != nested) throw new MusicException("Владелец плейлиста противоречив в ответе сервиса.");
        return uid.Length > 0 ? uid : nested;
    }

    private static JsonElement CatalogResult(JsonDocument json)
    {
        var result = Result(json);
        if (result.ValueKind == JsonValueKind.Object &&
            (result.TryGetProperty("error", out var error) && error.ValueKind is not (JsonValueKind.Null or JsonValueKind.False)
             || result.TryGetProperty("success", out var success) && success.ValueKind == JsonValueKind.False
             || Text(result, "status").Length > 0 && Text(result, "status").ToLowerInvariant() is not ("ok" or "success")))
            throw new MusicException("Яндекс отклонил действие каталога. Перечитайте данные перед повтором.");
        return result;
    }

    private static JsonElement CatalogArray(JsonElement value, string property, string context)
    {
        CatalogObject(value, context);
        if (!value.TryGetProperty(property, out var rows)) throw new MusicException($"В ответе нет {context}.");
        CatalogArrayValue(rows, context);
        return rows;
    }
    private static void CatalogArrayValue(JsonElement value, string context)
    { if (value.ValueKind != JsonValueKind.Array) throw new MusicException($"Изменился формат {context}."); }
    private static void CatalogObject(JsonElement value, string context)
    { if (value.ValueKind != JsonValueKind.Object) throw new MusicException($"Изменился формат {context}."); }
    private static string CatalogRequiredText(JsonElement value, string name, string context)
    {
        var text = Text(value, name);
        if (string.IsNullOrWhiteSpace(text)) throw new MusicException($"В данных {context} нет поля {name}.");
        return text;
    }
    private static int CatalogNonNegativeInt(JsonElement value, string name, string context)
    {
        if (!int.TryParse(Text(value, name), NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number < 0)
            throw new MusicException($"В данных {context} нет корректного поля {name}.");
        return number;
    }
    private static string CatalogId(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        if (id.Length > 512 || id.Any(char.IsControl)) throw new ArgumentException("Некорректный идентификатор.", nameof(id));
        return Uri.EscapeDataString(id);
    }
    private static void CatalogTrackReference(LikedId track, bool requireAlbum)
    {
        ArgumentNullException.ThrowIfNull(track);
        _ = CatalogId(track.Id);
        // Comma separates multiple likes; colon separates a track from its album. A component
        // must never expand one UI selection into several IDs or silently change its context.
        if (track.Id.Contains(',') || track.Id.Contains(':')) throw new MusicException("Нужен отдельный точный ID трека.");
        if (string.IsNullOrWhiteSpace(track.AlbumId))
        {
            if (requireAlbum) throw new MusicException("Для добавления нужен точный albumId трека. Альбом не подбирается по названию.");
            return;
        }
        _ = CatalogId(track.AlbumId);
        if (track.AlbumId.Contains(',') || track.AlbumId.Contains(':')) throw new MusicException("Некорректный контекст альбома трека.");
    }
    private static void CatalogTitle(string title)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        if (title.Any(char.IsControl)) throw new ArgumentException("Название содержит управляющие символы.", nameof(title));
    }
    private static void CatalogPageIndex(int page)
    { if (page < 0 || page > 100000) throw new ArgumentOutOfRangeException(nameof(page)); }
    private static MusicException CatalogUnconfirmedWrite()
        => new("Запись отправлена, но итог не подтверждён чтением. Перечитайте коллекцию или плейлист перед повтором; автоматический повтор не выполняется.");
}
