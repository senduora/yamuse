using System.Globalization;
using System.Net;
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
        return rows.EnumerateArray().Select(row => CatalogEntity(row, "playlist", uid) with
        {
            PlaylistRevision = int.TryParse(Text(row, "revision"), NumberStyles.None, CultureInfo.InvariantCulture, out var revision)
                && revision > 0 ? revision : null
        }).ToArray();
    }

    public async Task<IReadOnlyList<TrackRow>> GetAlbumTracksAsync(Session session, string albumId, CancellationToken cancellationToken)
        => (await GetAlbumDetailsAsync(session, albumId, cancellationToken).ConfigureAwait(false)).Tracks;

    public async Task<AlbumDetails> GetAlbumDetailsAsync(Session session, string albumId, CancellationToken cancellationToken)
    {
        if (!IsTrackReferencePart(albumId)) throw new MusicException("Нужен отдельный точный ID альбома.");
        using var json = await ApiAsync(HttpMethod.Get, $"/albums/{CatalogId(albumId)}/with-tracks", session, null, cancellationToken).ConfigureAwait(false);
        var album = CatalogResult(json);
        if (LibraryTrackId(album, "id") != albumId) throw new MusicException("Яндекс вернул другой альбом.");
        var cover = EntityArtwork(album, "album");
        var title = CatalogMetadataText(album, "title");
        var context = (albumId, title, cover);
        var volumes = CatalogArray(album, "volumes", "дисков альбома");
        var rows = new List<TrackRow>();
        foreach (var volume in volumes.EnumerateArray())
        {
            CatalogArrayValue(volume, "трека альбома");
            foreach (var track in volume.EnumerateArray())
                rows.Add(CatalogTrack(track, rows.Count + 1, context));
        }
        return new(albumId, title, CatalogArtistNames(album), rows,
            cover, CatalogOptionalYear(album), CatalogOptionalDate(album, "releaseDate"),
            CatalogAlbumArtistCredits(album));
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
        var (playlist, references) = await CatalogReadPlaylistAsync(session, owner, kind, cancellationToken).ConfigureAwait(false);
        return await CatalogEnrichPlaylistAsync(session, playlist, references, cancellationToken).ConfigureAwait(false);
    }

    internal async Task<PlaylistDetails> GetPlaylistForEditingAsync(Session session, string owner, string kind, CancellationToken cancellationToken)
    {
        var (playlist, references) = await CatalogReadPlaylistAsync(session, owner, kind, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return playlist with { Tracks = references.Select(CatalogPlaylistPlaceholder).ToArray() };
    }

    private static TrackRow CatalogPlaylistPlaceholder(LikedId reference, int index)
        => new() { Number = index + 1, Id = reference.Id, RequestId = reference.RequestId, HasTrackMetadata = false };

    private static TrackRow? CatalogPlaylistKnownTrack(LikedId reference, int index, IReadOnlyList<TrackRow>? knownTracks)
    {
        var known = knownTracks is not null && index < knownTracks.Count ? knownTracks[index] : null;
        return known is { HasTrackMetadata: true } && known.Id == reference.Id && known.RequestId == reference.RequestId ? known : null;
    }

    internal static TrackRow CopyPlaylistTrack(TrackRow known, int number)
        => new()
        {
            Number = number, Id = known.Id, RequestId = known.RequestId, Title = known.Title, Artist = known.Artist,
            PrimaryArtistName = known.PrimaryArtistName, Artists = known.Artists, Album = known.Album, AlbumId = known.AlbumId,
            CoverUri = known.CoverUri, DurationMs = known.DurationMs, Available = known.Available,
            LocalPath = known.LocalPath, LocalQuality = known.LocalQuality, IsOfflineLibraryRow = known.IsOfflineLibraryRow,
            DownloadedPath = known.DownloadedPath, DownloadedQuality = known.DownloadedQuality, HasOfflineQueueVariant = known.HasOfflineQueueVariant,
            IsLiked = known.IsLiked, LikeStateKnown = known.LikeStateKnown, CanToggleLike = known.CanToggleLike
        };

    private async Task<(PlaylistDetails Playlist, LikedId[] References)> CatalogReadPlaylistAsync(
        Session session, string owner, string kind, CancellationToken cancellationToken)
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
            throw new MusicException("Сервис вернул неполный список плейлиста.");
        var references = entries.EnumerateArray().Select(entry =>
        {
            var id = LibraryTrackId(entry, "id");
            return new LikedId(id, CatalogPlaylistAlbumId(entry, id));
        }).ToArray();
        return (info, references);
    }

    private async Task<PlaylistDetails> CatalogEnrichPlaylistAsync(Session session, PlaylistDetails playlist, LikedId[] references,
        CancellationToken cancellationToken, bool allowPartialMetadata = false, IReadOnlyList<TrackRow>? knownTracks = null)
    {
        var rows = new List<TrackRow>(references.Length);
        HashSet<int>? inherited = null;
        PlaylistMetadataIssue? issue = null;
        for (var offset = 0; offset < references.Length; offset += CatalogTrackBatchSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var batch = references.Skip(offset).Take(CatalogTrackBatchSize).ToArray();
                var metadata = await GetTracksAsync(session, batch, offset, cancellationToken, allowPartialMetadata).ConfigureAwait(false);
                for (var index = 0; index < metadata.Count; index++)
                {
                    var row = metadata[index];
                    rows.Add(row.HasTrackMetadata ? row : Fallback(offset + index, row));
                }
            }
            catch (Exception error) when (allowPartialMetadata && error is (MusicException or HttpRequestException or IOException or OperationCanceledException))
            {
                cancellationToken.ThrowIfCancellationRequested();
                // The playlist response already proved these references and their order.
                issue = error is MusicException { Authentication: true } ? PlaylistMetadataIssue.Authentication : PlaylistMetadataIssue.Unavailable;
                for (var index = offset; index < references.Length; index++)
                    rows.Add(Fallback(index));
                break;
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        return playlist with { Tracks = rows, InheritedMetadataPositions = inherited, MetadataIssue = issue };

        TrackRow Fallback(int index, TrackRow? placeholder = null)
        {
            var reference = references[index];
            if (CatalogPlaylistKnownTrack(reference, index, knownTracks) is not { } known)
                return placeholder ?? CatalogPlaylistPlaceholder(reference, index);
            (inherited ??= []).Add(index);
            return known.Number == index + 1 ? known : CopyPlaylistTrack(known, index + 1);
        }
    }

    private static Task<JsonDocument> ReadOptionalPlaylistMetadataAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.StatusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests || (int)response.StatusCode is >= 500 and <= 599)
            throw new HttpRequestException("Сведения о треках временно недоступны.", null, response.StatusCode);
        return ReadApiAsync(response, cancellationToken);
    }

    public async Task<IReadOnlyList<LikedId>> SetTrackLikeAsync(Session session, string uid, LikedId track, bool liked, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(track);
        CatalogTrackReference(track, requireAlbum: false);
        await _catalogMutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await CatalogVerifyOwnerAsync(session, uid, cancellationToken).ConfigureAwait(false);
            var result = await LibraryWriteResultAsync(
                $"/users/{CatalogId(uid)}/likes/tracks/{(liked ? "add-multiple" : "remove")}", session,
                Form(("track-ids", track.RequestId)), cancellationToken).ConfigureAwait(false);
            LibraryRequireRevision(result);
            try
            {
                var current = await GetLikedIdsAsync(session, uid, cancellationToken).ConfigureAwait(false);
                if (current.Any(t => t.Id == track.Id) != liked)
                    throw new MusicException("Состояние лайка не совпало с запросом.");
                return current;
            }
            catch (Exception ex) when (CatalogReadbackUncertain(ex))
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
            var result = await CatalogWriteResultAsync($"/users/{CatalogId(account.Uid)}/playlists/create", session,
                Form(("title", title), ("visibility", visibility)), cancellationToken).ConfigureAwait(false);
            if (result is not { } acknowledgement) throw CatalogUnconfirmedWrite();
            var kind = Text(acknowledgement, "kind");
            var created = CatalogMutationHeader(acknowledgement, account.Uid, kind);
            if (created.Title != title || created.Visibility != visibility || created.IsGenerated)
                throw CatalogUnconfirmedWrite();
            var (current, references) = await CatalogReadAfterWriteAsync(session, created, cancellationToken).ConfigureAwait(false);
            if (current.Revision < created.Revision || current.Title != title || current.Visibility != visibility || current.IsGenerated)
                throw CatalogUnconfirmedWrite();
            return await CatalogEnrichPlaylistAsync(session, current, references, cancellationToken, allowPartialMetadata: true).ConfigureAwait(false);
        }
        finally { _catalogMutationGate.Release(); }
    }

    public Task<PlaylistDetails> CreatePlaylistAsync(Session session, string title, CancellationToken cancellationToken)
        => CreatePlaylistAsync(session, title, "private", cancellationToken);

    internal delegate Task<JsonElement?> PlaylistWriteAdmission(Func<Task<JsonElement?>> send);

    public Task<PlaylistDetails> RenamePlaylistAsync(Session session, PlaylistDetails expected, string title, CancellationToken cancellationToken)
        => RenamePlaylistAsync(session, expected, title, cancellationToken, null);

    internal async Task<PlaylistDetails> RenamePlaylistAsync(Session session, PlaylistDetails expected, string title,
        CancellationToken cancellationToken, PlaylistWriteAdmission? admission)
    {
        CatalogTitle(title);
        expected = CatalogSnapshot(expected);
        await _catalogMutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var (fresh, previous) = await CatalogWritableAsync(session, expected, cancellationToken).ConfigureAwait(false);
            var result = await CatalogWriteResultAsync(CatalogPlaylistPath(fresh) + "/name", session,
                Form(("value", title)), cancellationToken, admission).ConfigureAwait(false);
            var reported = result is { } acknowledgement ? CatalogMutationRevision(acknowledgement, fresh, title) : null;
            var minimumRevision = reported ?? fresh.Revision;
            var acknowledgedTitle = result is { } receipt && receipt.TryGetProperty("title", out _);
            if (minimumRevision < fresh.Revision || minimumRevision == fresh.Revision && fresh.Title == title && !acknowledgedTitle)
                throw CatalogUnconfirmedWrite();
            var (current, references) = await CatalogReadAfterWriteAsync(session, fresh, cancellationToken).ConfigureAwait(false);
            var sameOrder = references.SequenceEqual(previous);
            var confirmedAfterOrderChange = reported is int acknowledged && acknowledged > fresh.Revision
                && (current.Revision > acknowledged || current.Revision == acknowledged && acknowledgedTitle
                    && (long)acknowledged > (long)fresh.Revision + 1);
            if (current.Revision < minimumRevision || current.Title != title || current.IsGenerated
                || !sameOrder && !confirmedAfterOrderChange) throw CatalogUnconfirmedWrite();
            // Rename has no revision precondition. A jump can include another client's positional changes.
            var verified = sameOrder && current.Revision == reported && (long)current.Revision <= (long)fresh.Revision + 1;
            return await CatalogEnrichPlaylistAsync(session, current with { EditPositionsVerified = verified }, references,
                cancellationToken, allowPartialMetadata: true, knownTracks: verified ? expected.Tracks : null).ConfigureAwait(false);
        }
        finally { _catalogMutationGate.Release(); }
    }

    public Task DeletePlaylistAsync(Session session, PlaylistDetails expected, CancellationToken cancellationToken)
        => DeletePlaylistAsync(session, expected, cancellationToken, null);

    internal async Task DeletePlaylistAsync(Session session, PlaylistDetails expected, CancellationToken cancellationToken, PlaylistWriteAdmission? admission)
    {
        expected = CatalogSnapshot(expected);
        await _catalogMutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var (fresh, _) = await CatalogWritableAsync(session, expected, cancellationToken).ConfigureAwait(false);
            var result = await CatalogWriteResultAsync(CatalogPlaylistPath(fresh) + "/delete", session, null, cancellationToken, admission).ConfigureAwait(false);
            var acknowledgement = result is { } response ? JsonString(response).Trim() : "";
            var acknowledged = string.Equals(acknowledgement, "ok", StringComparison.OrdinalIgnoreCase);
            if (result is { ValueKind: JsonValueKind.False }
                || acknowledgement.Equals("error", StringComparison.OrdinalIgnoreCase)
                || acknowledgement.Equals("failed", StringComparison.OrdinalIgnoreCase))
                throw new MusicException("Яндекс отклонил действие каталога. Перечитайте данные перед повтором.");
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!acknowledged && !await CatalogPlaylistAbsentAsync(session, fresh, cancellationToken).ConfigureAwait(false))
                    throw CatalogUnconfirmedWrite();
                var current = await GetPlaylistsAsync(session, fresh.Owner, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                if (current.Any(p => p.Owner == fresh.Owner && p.PlaylistKind == fresh.Kind)) throw CatalogUnconfirmedWrite();
            }
            catch (Exception ex) when (CatalogReadbackUncertain(ex))
            { throw CatalogUnconfirmedWrite(); }
        }
        finally { _catalogMutationGate.Release(); }
    }

    private Task<bool> CatalogPlaylistAbsentAsync(Session session, PlaylistDetails playlist, CancellationToken cancellationToken)
        => SendApiRequestAsync(HttpMethod.Get, CatalogPlaylistPath(playlist), session, null, cancellationToken,
            static (response, token) =>
            {
                token.ThrowIfCancellationRequested();
                if (response.StatusCode == HttpStatusCode.NotFound) return Task.FromResult(true);
                RequireApiSuccess(response);
                return Task.FromResult(false);
            });

    public async Task<PlaylistDetails> InsertPlaylistTracksAsync(Session session, PlaylistDetails expected,
        IReadOnlyList<LikedId> tracks, int at, CancellationToken cancellationToken, IReadOnlyList<TrackRow>? knownInsertedTracks = null)
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
        var diff = JsonSerializer.Serialize(new[] { new { op = "insert", at, tracks = tracks.Select(t => new { id = t.Id, albumId = t.AlbumId }).ToArray() } });
        var desired = expected.Tracks.ToList();
        if (knownInsertedTracks?.Count != tracks.Count) knownInsertedTracks = null;
        desired.InsertRange(at, tracks.Select((reference, index) =>
        {
            var known = CatalogPlaylistKnownTrack(reference, index, knownInsertedTracks);
            var position = at + index;
            // Inserting creates a new occurrence even when the source already has this number.
            return known is null ? CatalogPlaylistPlaceholder(reference, position) : CopyPlaylistTrack(known, position + 1);
        }));
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
        var desired = expected.Tracks.Where((_, i) => i != index).ToArray();
        return CatalogChangeAsync(session, expected, diff, desired, cancellationToken);
    }

    private async Task<PlaylistDetails> CatalogChangeAsync(Session session, PlaylistDetails expected, string diff,
        IReadOnlyList<TrackRow> desired, CancellationToken cancellationToken)
    {
        await _catalogMutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var (fresh, _) = await CatalogWritableAsync(session, expected, cancellationToken).ConfigureAwait(false);
            // The form carries a JSON diff: albumId is camel case, and delete.to is exclusive.
            var result = await CatalogWriteResultAsync(CatalogPlaylistPath(fresh) + "/change", session,
                Form(("kind", fresh.Kind), ("revision", fresh.Revision.ToString(CultureInfo.InvariantCulture)), ("diff", diff)), cancellationToken).ConfigureAwait(false);
            var reported = result is { } acknowledgement ? CatalogMutationRevision(acknowledgement, fresh) : null;
            if (reported is { } reportedRevision && reportedRevision <= fresh.Revision) throw CatalogUnconfirmedWrite();
            var minimumRevision = reported ?? fresh.Revision;
            var (current, references) = await CatalogReadAfterWriteAsync(session, fresh, cancellationToken).ConfigureAwait(false);
            if (current.Revision <= fresh.Revision || current.Revision < minimumRevision || current.IsGenerated
                || !references.Select(reference => (reference.Id, reference.RequestId)).SequenceEqual(desired.Select(row => (row.Id, row.RequestId))))
                throw CatalogUnconfirmedWrite();
            var verified = current.Revision == reported;
            // A newer service revision can prove the final list without proving which duplicates survived.
            return await CatalogEnrichPlaylistAsync(session, current with { EditPositionsVerified = verified }, references,
                cancellationToken, allowPartialMetadata: true, knownTracks: verified ? desired : null).ConfigureAwait(false);
        }
        finally { _catalogMutationGate.Release(); }
    }

    private async Task CatalogVerifyOwnerAsync(Session session, string owner, CancellationToken cancellationToken)
    {
        _ = CatalogId(owner);
        var account = await GetAccountAsync(session, cancellationToken, includeAvatar: false).ConfigureAwait(false);
        if (account.Uid != owner) throw new MusicException("Изменять можно только коллекцию текущего аккаунта.");
    }

    private async Task<(PlaylistDetails Playlist, LikedId[] References)> CatalogWritableAsync(
        Session session, PlaylistDetails expected, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(expected);
        if (expected.Revision <= 0 || expected.IsGenerated) throw new MusicException("Этот плейлист нельзя изменять. Нужна актуальная пользовательская подборка.");
        await CatalogVerifyOwnerAsync(session, expected.Owner, cancellationToken).ConfigureAwait(false);
        var owned = await GetPlaylistsAsync(session, expected.Owner, cancellationToken).ConfigureAwait(false);
        var matching = owned.Where(p => p.Owner == expected.Owner && p.PlaylistKind == expected.Kind).ToArray();
        if (matching.Length == 0)
            throw new MusicException("Плейлист отсутствует среди собственных подборок текущего аккаунта.");
        var (fresh, references) = await CatalogReadPlaylistAsync(session, expected.Owner, expected.Kind, cancellationToken).ConfigureAwait(false);
        if (fresh.IsGenerated || fresh.Revision != expected.Revision || matching.Any(item => item.PlaylistRevision > fresh.Revision)
            || !references.Select(reference => (reference.Id, reference.RequestId)).SequenceEqual(expected.Tracks.Select(track => (track.Id, track.RequestId))))
            throw new MusicException("Плейлист изменился на сервере. Перечитайте его перед изменением; запись не отправлена.");
        // rename/delete have no documented atomic revision parameter. This preflight detects stale UI;
        // change uses the revision in the server request and is the optimistic-concurrency operation.
        return (fresh, references);
    }

    private async Task<(PlaylistDetails Playlist, LikedId[] References)> CatalogReadAfterWriteAsync(
        Session session, PlaylistDetails playlist, CancellationToken cancellationToken)
    {
        try { return await CatalogReadPlaylistAsync(session, playlist.Owner, playlist.Kind, cancellationToken).ConfigureAwait(false); }
        catch (Exception ex) when (CatalogReadbackUncertain(ex))
        { throw CatalogUnconfirmedWrite(); }
    }

    private static bool CatalogReadbackUncertain(Exception error) =>
        error is MusicException { Authentication: false } or HttpRequestException or IOException or OperationCanceledException;

    private async Task<JsonElement?> CatalogWriteResultAsync(string path, Session session, HttpContent? content,
        CancellationToken cancellationToken, PlaylistWriteAdmission? admission = null)
    {
        using (content)
            return await (admission is null ? Send() : admission(Send)).ConfigureAwait(false);

        async Task<JsonElement?> Send()
        {
            cancellationToken.ThrowIfCancellationRequested();
            try { return await SendApiRequestAsync(HttpMethod.Post, path, session, content, cancellationToken, ReadCatalogWriteAcknowledgementAsync).ConfigureAwait(false); }
            // The request may have committed before the network failed.
            catch (Exception error) when (error is HttpRequestException or IOException or OperationCanceledException) { return null; }
        }
    }

    private static async Task<JsonElement?> ReadCatalogWriteAcknowledgementAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.StatusCode == HttpStatusCode.Conflict)
            throw new MusicException("Яндекс отклонил изменение из-за конфликта. Перечитайте данные перед повтором.");
        if (response.StatusCode == HttpStatusCode.RequestTimeout || (int)response.StatusCode >= 500) return null;
        RequireApiSuccess(response);
        JsonDocument json;
        try { json = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false); }
        // A successful HTTP status with a malformed body cannot confirm the mutation.
        catch (MusicException) { return null; }
        using (json)
        {
            if (json.RootElement.ValueKind != JsonValueKind.Object) return null;
            if (json.RootElement.TryGetProperty("error", out _))
                throw new MusicException("Яндекс отклонил изменение. Перечитайте данные перед повтором.");
            if (!json.RootElement.TryGetProperty("result", out var result) || result.ValueKind == JsonValueKind.Null)
                return null;
            return CatalogResult(json).Clone();
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

    private static int? CatalogMutationRevision(JsonElement result, PlaylistDetails playlist, string? expectedTitle = null)
    {
        // The mutation path already fixes the identity. Fields present in a partial
        // acknowledgement must agree with that identity before readback can confirm it.
        if (result.ValueKind != JsonValueKind.Object
            || result.TryGetProperty("kind", out _) && Text(result, "kind") != playlist.Kind
            || result.TryGetProperty("uid", out _) && Text(result, "uid") != playlist.Owner
            || result.TryGetProperty("owner", out var owner) && Text(owner, "uid") != playlist.Owner
            || Text(result, "generatedPlaylistType").Length > 0 || Text(result, "artistPlaylistType").Length > 0)
            throw CatalogUnconfirmedWrite();
        if (result.TryGetProperty("title", out var title)
            && (string.IsNullOrWhiteSpace(JsonString(title)) || expectedTitle is not null && JsonString(title) != expectedTitle))
            throw CatalogUnconfirmedWrite();
        if (!result.TryGetProperty("revision", out _)) return null;
        try { return CatalogNonNegativeInt(result, "revision", "изменения плейлиста"); }
        catch (MusicException) { throw CatalogUnconfirmedWrite(); }
    }

    private static PlaylistDetails CatalogSnapshot(PlaylistDetails playlist)
    {
        ArgumentNullException.ThrowIfNull(playlist);
        ArgumentNullException.ThrowIfNull(playlist.Tracks);
        var tracks = playlist.Tracks.ToArray();
        foreach (var track in tracks)
        {
            if (!IsTrackReferencePart(track.Id) || track.RequestId != track.Id
                && (!track.RequestId.StartsWith(track.Id + ":", StringComparison.Ordinal) || !IsTrackReferencePart(track.RequestId[(track.Id.Length + 1)..])))
                throw new MusicException("В снимке плейлиста некорректный ID трека или альбома. Перечитайте плейлист.");
        }
        return playlist with { Tracks = tracks };
    }

    private static string CatalogPlaylistPath(PlaylistDetails playlist)
        => $"/users/{CatalogId(playlist.Owner)}/playlists/{CatalogId(playlist.Kind)}";

    private static string? CatalogPlaylistAlbumId(JsonElement entry, string trackId)
    {
        // New playlist responses can embed a track instead of the old albumId.
        // Use only an unambiguous reference in this exact playlist response;
        // later bulk metadata must never invent or override its album context.
        if (entry.TryGetProperty("albumId", out var direct)) return direct.ValueKind == JsonValueKind.Null ? null : LibraryTrackId(entry, "albumId");
        if (!entry.TryGetProperty("track", out var track) || track.ValueKind != JsonValueKind.Object
            || Text(track, "id") != trackId || !track.TryGetProperty("albums", out var albums)
            || albums.ValueKind != JsonValueKind.Array || albums.GetArrayLength() != 1) return null;
        var album = albums[0];
        return album.ValueKind == JsonValueKind.Object ? OptionalTrackMetadataId(album, "id") : throw TrackMetadataError();
    }

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
        var title = CatalogRequiredText(row, kind == "artist" ? "name" : "title", "каталога");
        if (kind == "track")
        {
            var track = CatalogTrack(row, 0);
            return new(track.Id, kind, title, track.Artist, AlbumId: track.AlbumId, CoverUri: track.CoverUri, Track: track);
        }
        var id = kind == "album" ? LibraryTrackId(row, "id") : CatalogRequiredText(row, kind == "playlist" ? "kind" : "id", "каталога");
        var subtitle = kind == "artist" ? "Исполнитель" : CatalogArtistNames(row);
        if (kind != "playlist") return new(id, kind, title, subtitle, CoverUri: EntityArtwork(row, kind));
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

    private static TrackRow CatalogTrack(JsonElement track, int number, (string Id, string Title, string? CoverUri)? albumContext = null)
    {
        var id = LibraryTrackId(track, "id");
        var (albumId, albumTitle) = albumContext is { } context ? (context.Id, context.Title) : TrackAlbum(track);
        if (albumContext is { } parent && albumTitle.Length == 0)
        {
            var embedded = TrackAlbum(track, parent.Id);
            if (embedded.Id == parent.Id) albumTitle = embedded.Title;
        }
        var title = TrackText(JsonString(track, "title"));
        return new()
        {
            Number = number, Id = id, RequestId = new LikedId(id, albumId).RequestId,
            Title = TrackTitle(track), Artist = TrackText(CatalogArtistNames(track)), Artists = ArtistReferences(track),
            PrimaryArtistName = PrimaryArtistName(track),
            Album = TrackText(albumTitle), AlbumId = albumId, DurationMs = TrackDuration(track),
            CoverUri = TrackArtwork(track, albumId) ?? albumContext?.CoverUri,
            Available = !string.IsNullOrWhiteSpace(title) && (Bool(track, "available") || Bool(track, "availableForPremiumUsers") || Bool(track, "availableFullWithoutPermission"))
        };
    }

    private static string CatalogArtistNames(JsonElement row)
        => row.TryGetProperty("artists", out var artists) && artists.ValueKind == JsonValueKind.Array
            ? string.Join(", ", artists.EnumerateArray().Select(a => Text(a, "name")).Where(n => n.Length > 0)) : "";

    private static IReadOnlyList<ArtistReference> CatalogAlbumArtistCredits(JsonElement album)
    {
        if (!album.TryGetProperty("artists", out var artists) || artists.ValueKind != JsonValueKind.Array) return [];
        var credits = new List<ArtistReference>();
        foreach (var artist in artists.EnumerateArray())
        {
            var name = Text(artist, "name");
            if (string.IsNullOrWhiteSpace(name)) continue;
            var id = Text(artist, "id");
            // Preserve name-only credits in place without guessing a navigation ID.
            if (string.IsNullOrWhiteSpace(id) || id.Length > 512 || id.Any(char.IsControl)) id = "";
            credits.Add(new(id, name));
        }
        return Array.AsReadOnly(credits.ToArray());
    }

    // Optional presentation metadata cannot make a previously readable collection fail.
    // Accept only the documented field types and ISO dates; never infer a release from tracks or titles.
    private static string CatalogMetadataText(JsonElement row, string property)
        => JsonString(row, property).Trim();

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
        if (id.Length > 512 || id is "." or ".." || id.Any(char.IsControl)) throw new ArgumentException("Некорректный идентификатор.", nameof(id));
        return Uri.EscapeDataString(id);
    }
    private static void CatalogTrackReference(LikedId track, bool requireAlbum)
    {
        ArgumentNullException.ThrowIfNull(track);
        // Comma separates multiple likes; colon separates a track from its album. A component
        // must never expand one UI selection into several IDs or silently change its context.
        if (!IsTrackReferencePart(track.Id)) throw new MusicException("Нужен отдельный точный ID трека.");
        if (track.AlbumId is null)
        {
            if (requireAlbum) throw new MusicException("Для добавления нужен точный albumId трека. Альбом не подбирается по названию.");
            return;
        }
        if (!IsTrackReferencePart(track.AlbumId)) throw new MusicException("Некорректный контекст альбома трека.");
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
