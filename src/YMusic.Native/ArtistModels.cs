namespace YMusic.Native;

internal sealed record ArtistDetails(string Id, string Name, string? CoverUri, long? MonthlyListeners,
    IReadOnlyList<TrackRow> TopTracks, IReadOnlyList<CatalogItem> Albums,
    IReadOnlyList<CatalogItem> SimilarArtists, string Description);
