using System.Globalization;
using System.Net.Http;
using System.Text.Json;

namespace YMusic.Native;

internal sealed partial class MusicApi
{
    // The public search contract has album/playlist types, no free-text "selection" type.
    // Only explicit compilation albums and positively identified editorial owners qualify.
    public async Task<CatalogPage> SearchCollectionsAsync(Session session, string query, int page, CancellationToken token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        CatalogPageIndex(page);
        var albums = ReadCollectionSearchAsync(session, query, "album", page, token);
        var playlists = ReadCollectionSearchAsync(session, query, "playlist", page, token);
        var parts = await Task.WhenAll(albums, playlists).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        return new(parts.SelectMany(part => part.Items).ToArray(), page, parts.Any(part => part.HasMore));
    }

    private async Task<CatalogPage> ReadCollectionSearchAsync(Session session, string query, string kind, int page, CancellationToken token)
    {
        using var json = await ApiAsync(HttpMethod.Get,
            $"/search?text={Uri.EscapeDataString(query)}&type={kind}&page={page.ToString(CultureInfo.InvariantCulture)}&nocorrect=false",
            session, null, token).ConfigureAwait(false);
        var result = CatalogResult(json);
        CatalogObject(result, "поиска подборок");
        if (!result.TryGetProperty(kind + "s", out var section) || section.ValueKind == JsonValueKind.Null) return new([], page, false);
        var rows = CatalogArray(section, "results", "подборок");
        var total = CatalogNonNegativeInt(section, "total", "поиска подборок");
        var perPage = Int(section, "perPage");
        if (total > 0 && perPage <= 0) throw new MusicException("В ответе поиска нет размера страницы.");
        var items = rows.EnumerateArray().Where(row => kind == "album"
            ? Text(row, "type") == "compilation" : IsEditorialPlaylist(row)).Select(row => CatalogEntity(row, kind)).ToArray();
        return new(items, page, (long)(page + 1) * perPage < total);
    }

    private static bool IsEditorialPlaylist(JsonElement row)
    {
        if (!row.TryGetProperty("owner", out var owner) || owner.ValueKind != JsonValueKind.Object
            || !owner.TryGetProperty("verified", out var verified) || verified.ValueKind != JsonValueKind.True
            || Text(owner, "name") != "Яндекс Музыка") return false;
        // Exact public owner pairs verified from Yandex search responses on 2026-10-24.
        return (Text(owner, "uid"), Text(owner, "login")) is
            ("103372440", "music-blog") or ("414787002", "yamusic-top") or ("826551232", "yearbyyear");
    }
}
