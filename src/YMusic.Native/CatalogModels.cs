namespace YMusic.Native;

// Id is the entity ID. Track album IDs are carried separately, never inferred from a name.
internal sealed record CatalogItem(string Id, string Kind, string Title, string Subtitle,
    string? Owner = null, string? PlaylistKind = null, string? AlbumId = null, string? CoverUri = null, TrackRow? Track = null,
    string? OwnerName = null)
{
    internal int? PlaylistRevision { get; init; }
    public string DisplaySubtitle => Kind == "playlist" && !string.IsNullOrWhiteSpace(OwnerName) ? OwnerName : Subtitle;
}

internal sealed record CatalogPage(IReadOnlyList<CatalogItem> Items, int Page, bool HasMore);

internal enum PlaylistMetadataIssue { Unavailable, Authentication }

internal sealed record PlaylistDetails(string Owner, string Kind, string Title, string Description,
    int Revision, IReadOnlyList<TrackRow> Tracks, bool IsGenerated = false, string Visibility = "",
    string? CoverUri = null, string? OwnerName = null, DateOnly? Created = null, DateOnly? Modified = null)
{
    internal bool EditPositionsVerified { get; init; }
    internal IReadOnlySet<int>? InheritedMetadataPositions { get; init; }
    internal PlaylistMetadataIssue? MetadataIssue { get; init; }
}

internal sealed record AlbumDetails(string Id, string Title, string Artists, IReadOnlyList<TrackRow> Tracks,
    string? CoverUri = null, int? Year = null, DateOnly? ReleaseDate = null,
    IReadOnlyList<ArtistReference>? ArtistCredits = null);
