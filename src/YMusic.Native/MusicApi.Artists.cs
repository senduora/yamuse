using System.Globalization;
using System.Net.Http;
using System.Text.Json;

namespace YMusic.Native;

internal sealed partial class MusicApi
{
    // Official desktop 5.124.1 public API client: getBriefInfo/getAboutArtist.
    // ArtistBriefInfo and Stats.lastMonthListeners are also described by
    // jrfrigat/YandexMusic 8c929094386137669766632f9cc2c97d3b05f670 (MIT).
    public async Task<ArtistDetails> GetArtistDetailsAsync(Session session, string artistId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var id = CatalogId(artistId);
        using var briefJson = await ApiAsync(HttpMethod.Get, $"/artists/{id}/brief-info", session, null, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        var brief = CatalogResult(briefJson);
        CatalogObject(brief, "исполнителя");
        if (!brief.TryGetProperty("artist", out var artist)) throw new MusicException("В ответе Яндекса нет исполнителя.");
        ArtistIdentity(artist, artistId, required: true);
        var name = CatalogRequiredText(artist, "name", "исполнителя");
        var top = ArtistOptionalArray(brief, "popularTracks")
            .Select((row, index) => CatalogTrack(row, index + 1)).ToArray();
        var albums = ArtistOptionalArray(brief, "albums").Select(row => CatalogEntity(row, "album")).ToArray();
        var similar = ArtistOptionalArray(brief, "similarArtists").Select(row => CatalogEntity(row, "artist")).ToArray();
        var description = ArtistDescription(artist);
        var listeners = brief.TryGetProperty("stats", out var briefStats) ? ArtistMonthlyListeners(briefStats) : null;

        // Ratings, likesCount and play counts describe other metrics. Only the
        // service's actual monthly listener statistic is shown as this audience.
        if (listeners is null)
        {
            using var aboutJson = await ApiAsync(HttpMethod.Get, $"/artists/{id}/about-artist", session, null, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            var about = ArtistAboutResult(aboutJson, artistId);
            if (description.Length == 0) description = ArtistDescription(about);
            listeners = about.TryGetProperty("stats", out var stats) ? ArtistMonthlyListeners(stats) : null;
        }
        return new(artistId, name, EntityArtwork(artist, "artist"), listeners,
            Array.AsReadOnly(top), Array.AsReadOnly(albums), Array.AsReadOnly(similar), description);
    }

    private static JsonElement ArtistAboutResult(JsonDocument json, string artistId)
    {
        var wrapped = json.RootElement.TryGetProperty("result", out _);
        // Current about-artist returns a direct object; brief-info and older
        // client fixtures use the result envelope. Direct data must identify
        // this exact artist before its statistics may be attached to the page.
        var about = wrapped ? CatalogResult(json) : json.RootElement;
        CatalogObject(about, "сведений об исполнителе");
        if (about.TryGetProperty("artist", out var artist) && artist.ValueKind != JsonValueKind.Null)
            ArtistIdentity(artist, artistId, required: !wrapped);
        else if (!wrapped) throw new MusicException("В ответе Яндекса нет исполнителя для статистики.");
        if (!wrapped && (about.TryGetProperty("success", out var success) && success.ValueKind == JsonValueKind.False
            || Text(about, "status").Length > 0 && Text(about, "status").ToLowerInvariant() is not ("ok" or "success")))
            throw new MusicException("Яндекс не подтвердил сведения об исполнителе.");
        return about;
    }

    private static void ArtistIdentity(JsonElement artist, string expected, bool required)
    {
        CatalogObject(artist, "исполнителя");
        var actual = Text(artist, "id");
        if ((required || actual.Length > 0) && actual != expected)
            throw new MusicException("Яндекс вернул другого исполнителя.");
    }

    private static JsonElement[] ArtistOptionalArray(JsonElement root, string property)
    {
        if (!root.TryGetProperty(property, out var rows) || rows.ValueKind == JsonValueKind.Null) return [];
        CatalogArrayValue(rows, "данных исполнителя");
        return rows.EnumerateArray().ToArray();
    }

    private static long? ArtistMonthlyListeners(JsonElement stats)
        => long.TryParse(Text(stats, "lastMonthListeners"), NumberStyles.None, CultureInfo.InvariantCulture, out var value) && value >= 0
            ? value : null;

    private static string ArtistDescription(JsonElement row)
    {
        if (!row.TryGetProperty("description", out var description)) return "";
        return description.ValueKind == JsonValueKind.String ? description.GetString() ?? ""
            : description.ValueKind == JsonValueKind.Object ? Text(description, "text") : "";
    }

    // Keep names for display separately; an artist is navigable only when the
    // exact response contains both its ID and its name. No name-based lookup.
    private static IReadOnlyList<ArtistReference> ArtistReferences(JsonElement track)
    {
        if (!track.TryGetProperty("artists", out var artists) || artists.ValueKind != JsonValueKind.Array) return [];
        var result = new List<ArtistReference>();
        foreach (var artist in artists.EnumerateArray())
        {
            var id = Text(artist, "id");
            var name = Text(artist, "name");
            if (string.IsNullOrWhiteSpace(id) || id.Length > 512 || id.Any(char.IsControl) || string.IsNullOrWhiteSpace(name)) continue;
            result.Add(new(id, name));
        }
        return Array.AsReadOnly(result.ToArray());
    }
}
