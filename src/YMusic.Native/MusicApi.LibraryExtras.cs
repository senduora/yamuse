using System.Net.Http;
using System.Text.Json;

namespace YMusic.Native;

// Protocol: jrfrigat/YandexMusic, MIT, commit 8c929094386137669766632f9cc2c97d3b05f670,
// Endpoints/LibraryClient.cs, Endpoints/ILibraryClient.cs and Models/Library/*.cs.
internal sealed partial class MusicApi
{
    public async Task<IReadOnlyList<CatalogItem>> GetLikedPlaylistsAsync(Session session, string uid, CancellationToken cancellationToken)
    {
        LibraryId(uid);
        using var json = await ApiAsync(HttpMethod.Get, $"/users/{CatalogId(uid)}/likes/playlists", session, null, cancellationToken).ConfigureAwait(false);
        var rows = CatalogResult(json);
        CatalogArrayValue(rows, "любимых плейлистов");
        var playlists = new List<CatalogItem>();
        foreach (var row in rows.EnumerateArray())
        {
            CatalogObject(row, "отметки плейлиста");
            if (Text(row, "type") is { Length: > 0 } type && type != "playlist")
                throw new MusicException("Список любимых плейлистов содержит другой тип объекта.");
            if (!row.TryGetProperty("playlist", out var playlist) || playlist.ValueKind != JsonValueKind.Object)
                throw new MusicException("В отметке нет данных плейлиста. Список не был загружен полностью.");
            var item = CatalogEntity(playlist, "playlist");
            _ = LibraryEntityReference(item);
            // A kind alone cannot identify a playlist. Do not borrow the account uid as owner.
            var reference = Text(row, "id");
            if (reference.Contains(':') && reference != item.Owner + ":" + item.PlaylistKind)
                throw new MusicException("Идентификатор отметки относится к другому плейлисту.");
            playlists.Add(item);
        }
        return playlists;
    }

    public async Task SetEntityLikeAsync(Session session, string uid, CatalogItem item, bool liked, CancellationToken cancellationToken)
    {
        LibraryId(uid);
        var (plural, bodyKey, reference) = LibraryEntityReference(item);
        await _catalogMutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await CatalogVerifyOwnerAsync(session, uid, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var json = await CatalogWriteAsync($"/users/{CatalogId(uid)}/likes/{plural}/{(liked ? "add-multiple" : "remove")}",
                    session, Form((bodyKey, reference)), cancellationToken).ConfigureAwait(false);
                LibraryRequireOk(CatalogResult(json));
                IReadOnlyList<CatalogItem> current = item.Kind switch
                {
                    "album" => await GetLikedAlbumsAsync(session, uid, cancellationToken).ConfigureAwait(false),
                    "artist" => await GetLikedArtistsAsync(session, uid, cancellationToken).ConfigureAwait(false),
                    _ => await GetLikedPlaylistsAsync(session, uid, cancellationToken).ConfigureAwait(false)
                };
                var present = current.Any(candidate => LibraryEntityReference(candidate).Reference == reference);
                if (present != liked) throw CatalogUnconfirmedWrite();
            }
            catch (Exception ex) when (LibraryUncertain(ex)) { throw CatalogUnconfirmedWrite(); }
        }
        finally { _catalogMutationGate.Release(); }
    }

    public async Task<IReadOnlyList<TrackRow>> GetDislikedTracksAsync(Session session, string uid, CancellationToken cancellationToken)
    {
        var ids = await LibraryDislikedIdsAsync(session, uid, cancellationToken).ConfigureAwait(false);
        var tracks = new List<TrackRow>(ids.Count);
        for (var offset = 0; offset < ids.Count; offset += CatalogTrackBatchSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Preserve every reference, including unavailable tracks, in server order.
            tracks.AddRange(await GetTracksAsync(session, ids.Skip(offset).Take(CatalogTrackBatchSize).ToArray(), offset, cancellationToken).ConfigureAwait(false));
        }
        return tracks;
    }

    public async Task<IReadOnlyList<CatalogItem>> GetDislikedArtistsAsync(Session session, string uid, CancellationToken cancellationToken)
    {
        LibraryId(uid);
        using var json = await ApiAsync(HttpMethod.Get, $"/users/{CatalogId(uid)}/dislikes/artists", session, null, cancellationToken).ConfigureAwait(false);
        var rows = CatalogResult(json);
        CatalogArrayValue(rows, "нежелательных исполнителей");
        return rows.EnumerateArray().Select(row =>
        {
            var item = CatalogEntity(CatalogUnwrapEntity(row, "artist"), "artist");
            LibraryId(item.Id);
            return item;
        }).ToArray();
    }

    public async Task SetDislikeAsync(Session session, string uid, string kind, LikedId reference, bool disliked, CancellationToken cancellationToken)
    {
        LibraryId(uid);
        ArgumentNullException.ThrowIfNull(reference);
        LibraryId(reference.Id);
        string plural, bodyKey, requestId;
        switch (kind)
        {
            case "track":
                if (reference.AlbumId is not null) LibraryId(reference.AlbumId);
                CatalogTrackReference(reference, requireAlbum: false);
                plural = "tracks"; bodyKey = "track-ids"; requestId = reference.RequestId;
                break;
            case "artist":
                if (reference.AlbumId is not null) throw new MusicException("У отметки исполнителя не может быть albumId.");
                plural = "artists"; bodyKey = "artist-ids"; requestId = reference.Id;
                break;
            default: throw new MusicException("Отметка «не нравится» поддерживается только для треков и исполнителей.");
        }
        await _catalogMutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await CatalogVerifyOwnerAsync(session, uid, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var json = await CatalogWriteAsync($"/users/{CatalogId(uid)}/dislikes/{plural}/{(disliked ? "add-multiple" : "remove")}",
                    session, Form((bodyKey, requestId)), cancellationToken).ConfigureAwait(false);
                var result = CatalogResult(json);
                if (kind == "track") _ = CatalogNonNegativeInt(result, "revision", "изменения отметки");
                else LibraryRequireOk(result);
                // A track's library membership is keyed by track id; albumId is request context.
                var present = kind == "track"
                    ? (await LibraryDislikedIdsAsync(session, uid, cancellationToken).ConfigureAwait(false)).Any(t => t.Id == reference.Id)
                    : (await GetDislikedArtistsAsync(session, uid, cancellationToken).ConfigureAwait(false)).Any(a => a.Id == reference.Id);
                if (present != disliked) throw CatalogUnconfirmedWrite();
            }
            catch (Exception ex) when (LibraryUncertain(ex)) { throw CatalogUnconfirmedWrite(); }
        }
        finally { _catalogMutationGate.Release(); }
    }

    private async Task<IReadOnlyList<LikedId>> LibraryDislikedIdsAsync(Session session, string uid, CancellationToken cancellationToken)
    {
        LibraryId(uid);
        // Deliberately underscore here: upstream documents a different key from likes/tracks.
        using var json = await ApiAsync(HttpMethod.Get, $"/users/{CatalogId(uid)}/dislikes/tracks?if_modified_since_revision=0",
            session, null, cancellationToken).ConfigureAwait(false);
        var result = CatalogResult(json);
        CatalogObject(result, "нежелательных треков");
        if (!result.TryGetProperty("library", out var library)) throw new MusicException("В ответе нет списка нежелательных треков.");
        CatalogObject(library, "библиотеки");
        if (library.TryGetProperty("uid", out var owner) && owner.ValueKind != JsonValueKind.Null)
        {
            LibraryId(Text(library, "uid"));
            if (Text(library, "uid") != uid) throw new MusicException("Ответ относится к библиотеке другого аккаунта.");
        }
        var rows = CatalogArray(library, "tracks", "нежелательных треков");
        var ids = new List<LikedId>(rows.GetArrayLength());
        foreach (var row in rows.EnumerateArray())
        {
            var id = CatalogRequiredText(row, "id", "нежелательного трека");
            LibraryId(id);
            string? albumId = null;
            if (row.TryGetProperty("albumId", out var album) && album.ValueKind != JsonValueKind.Null)
            {
                albumId = Text(row, "albumId");
                LibraryId(albumId);
            }
            ids.Add(new(id, albumId));
        }
        return ids;
    }

    private static (string Plural, string BodyKey, string Reference) LibraryEntityReference(CatalogItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        LibraryId(item.Id);
        switch (item.Kind)
        {
            case "album": return ("albums", "album-ids", item.Id);
            case "artist": return ("artists", "artist-ids", item.Id);
            case "playlist":
                LibraryId(item.Owner);
                LibraryId(item.PlaylistKind);
                if (item.Id != item.PlaylistKind) throw new MusicException("ID плейлиста не совпадает с его kind.");
                return ("playlists", "playlist-ids", item.Owner + ":" + item.PlaylistKind);
            default: throw new MusicException("Здесь поддерживаются только альбомы, исполнители и плейлисты с точными серверными ID.");
        }
    }

    private static void LibraryId(string? id)
    {
        // The supported music entities have decimal server IDs. Never turn a local/generated
        // placeholder, delimiter or display name into a library mutation or a multi-ID body.
        if (string.IsNullOrEmpty(id) || id.Length > 32 || id.Any(c => c < '0' || c > '9') || id.All(c => c == '0'))
            throw new MusicException("Нужен точный положительный числовой ID сервера; объект без него пока не поддерживается.");
    }

    private static void LibraryRequireOk(JsonElement result)
    {
        if (result.ValueKind != JsonValueKind.String || !string.Equals(result.GetString(), "ok", StringComparison.OrdinalIgnoreCase))
            throw CatalogUnconfirmedWrite();
    }

    private static bool LibraryUncertain(Exception ex)
        => ex is MusicException or HttpRequestException or IOException or OperationCanceledException or JsonException or InvalidOperationException;
}
