namespace YMusic.Native;

// Id is the entity ID. Track album IDs are carried separately, never inferred from a name.
internal sealed record CatalogItem(string Id, string Kind, string Title, string Subtitle,
    string? Owner = null, string? PlaylistKind = null, string? AlbumId = null, string? CoverUri = null, TrackRow? Track = null);

internal sealed record CatalogPage(IReadOnlyList<CatalogItem> Items, int Page, bool HasMore);

internal sealed record PlaylistDetails(string Owner, string Kind, string Title, string Description,
    int Revision, IReadOnlyList<TrackRow> Tracks, bool IsGenerated = false, string Visibility = "");
