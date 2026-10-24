using System.Text.Json;

namespace YMusic.Native;

internal sealed partial class MusicApi
{
    // Artwork is taken from metadata already in the response; a row never makes an API request.
    internal static string? TrackArtwork(JsonElement track)
    {
        var direct = ArtworkUri.Metadata(Text(track, "coverUri"));
        if (direct is not null) return direct;
        if (track.ValueKind == JsonValueKind.Object && track.TryGetProperty("albums", out var albums)
            && albums.ValueKind == JsonValueKind.Array)
            foreach (var album in albums.EnumerateArray())
                if (ArtworkUri.Metadata(Text(album, "coverUri")) is { } cover) return cover;
        return null;
    }

    internal static string? EntityArtwork(JsonElement entity, string kind)
    {
        if (kind == "track") return TrackArtwork(entity);
        if (ArtworkUri.Metadata(Text(entity, "coverUri")) is { } direct) return direct;
        if (entity.ValueKind != JsonValueKind.Object || !entity.TryGetProperty("cover", out var cover)
            || cover.ValueKind != JsonValueKind.Object) return null;
        if (ArtworkUri.Metadata(Text(cover, "uri")) is { } uri) return uri;
        if (cover.TryGetProperty("itemsUri", out var items) && items.ValueKind == JsonValueKind.Array)
            foreach (var item in items.EnumerateArray())
                if (item.ValueKind == JsonValueKind.String && ArtworkUri.Metadata(item.GetString()) is { } itemUri) return itemUri;
        return null;
    }
}
