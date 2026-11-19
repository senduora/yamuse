namespace YMusic.Native;

internal sealed partial class OfflineLibrary
{
    private static string? FindSelectedPlaybackArtwork(TrackRow track, OfflineItem selected)
    {
        if (track.Id != selected.Id || !PlaybackStateStore.ValidRequestId(track.RequestId, track.Id)
            || !PlaybackStateStore.ValidRequestId(selected.RequestId, selected.Id)) return null;
        var album = PlaybackStateStore.ArtworkAlbumId(track.Id, track.RequestId, track.AlbumId);
        var selectedAlbum = PlaybackStateStore.ArtworkAlbumId(selected.Id, selected.RequestId, selected.AlbumId);
        return album == selectedAlbum ? ArtworkUri.Metadata(selected.CoverUri) : null;
    }

    internal string? FindPlaybackArtwork(string trackId, string? albumId, OfflineItem? selectedAudio)
    {
        if (selectedAudio is not null && selectedAudio.Id == trackId && MatchesAlbum(selectedAudio)
            && ArtworkUri.Metadata(selectedAudio.CoverUri) is { } selectedCover) return selectedCover;
        if (!IsAvailable || albumId is null || !LoadLibrary().ById.TryGetValue(trackId, out var variants)) return null;

        foreach (var variant in variants)
        {
            if (MatchesAlbum(variant) && ArtworkUri.Metadata(variant.CoverUri) is { } cover && ValidFile(variant))
                return cover;
        }
        return null;

        bool MatchesAlbum(OfflineItem item) => PlaybackStateStore.ValidRequestId(item.RequestId, trackId)
            && PlaybackStateStore.ArtworkAlbumId(trackId, item.RequestId, item.AlbumId) == albumId;
    }
}
