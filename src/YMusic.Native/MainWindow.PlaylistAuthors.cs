namespace YMusic.Native;

public partial class MainWindow
{
    private CatalogItem PresentCatalogAuthor(CatalogItem item)
        => item.Kind is "playlist" or "offline-playlist" && string.IsNullOrWhiteSpace(item.OwnerName)
            && _account is { } account && item.Owner == account.Uid
            ? item with { OwnerName = account.DisplayName } : item;

    private PlaylistDetails PresentPlaylistAuthor(PlaylistDetails playlist)
        => string.IsNullOrWhiteSpace(playlist.OwnerName) && _account is { } account && playlist.Owner == account.Uid
            ? playlist with { OwnerName = account.DisplayName } : playlist;
}
