using System.Windows;
using System.Windows.Controls;

namespace YMusic.Native;

public partial class MainWindow
{
    private readonly Dictionary<string, CatalogItem> _resolvedTrackAlbums = new(StringComparer.Ordinal);
    private int _resolvedAlbumGeneration = -1;

    private void UpdatePlayerArtistLinks()
    {
        if (PlayerArtistPanel is null) return;
        PlayerCoverButton.Tag = PlayerTitleButton.Tag = _current;
        PlayerCoverButton.IsEnabled = PlayerTitleButton.IsEnabled = _current is not null && _session is not null;
        var visible = _current is { } track && (track.Artists.Count > 0 || !string.IsNullOrWhiteSpace(track.Artist));
        if (!ReferenceEquals(PlayerArtistLinks.Content, _current))
        {
            PlayerArtistLinks.Content = _current;
            PlayerArtistPanel.ContextMenu = _current is { Artists.Count: > 0 } current ? CreateTrackArtistsMenu(PlayerArtistPanel, current.Artists) : null;
        }
        PlayerArtistPanel.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        PlayerArtistPanel.IsEnabled = _session is not null;
        TrackArtist.Visibility = visible ? Visibility.Collapsed : Visibility.Visible;
    }

    private void ShowTrackArtistsMenu(Button button, IReadOnlyList<ArtistReference> artists)
    {
        var menu = CreateTrackArtistsMenu(button, artists);
        button.ContextMenu = menu;
        menu.IsOpen = true;
    }

    private ContextMenu CreateTrackArtistsMenu(FrameworkElement anchor, IReadOnlyList<ArtistReference> artists)
    {
        var generation = _accountGeneration;
        var menu = new ContextMenu { PlacementTarget = anchor };
        foreach (var artist in artists)
        {
            var item = new MenuItem { Header = artist.Name };
            item.Click += async (_, _) =>
            {
                if (generation == _accountGeneration && !_closed) await OpenArtistAsync(artist.Id, artist.Name);
            };
            menu.Items.Add(item);
        }
        return menu;
    }

    private async void TrackAlbum_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is not Button { Tag: TrackRow track }) return;
        await OpenTrackAlbumAsync(track);
    }
    private async void CatalogTitle_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is not Button { Tag: CatalogItem item }) return;
        if (item.Kind == "track")
        {
            var track = item.Track ?? new TrackRow { Id = item.Id, RequestId = new LikedId(item.Id, item.AlbumId).RequestId, Title = item.Title, AlbumId = item.AlbumId };
            await OpenTrackAlbumAsync(track);
        }
        else await OpenCatalogAsync(item);
    }
    private async Task OpenTrackAlbumAsync(TrackRow track)
    {
        if (_session is null) { SetStatus("Для открытия альбома подключитесь к Яндексу."); return; }
        var generation = _accountGeneration;
        var view = _viewGeneration;
        try
        {
            var albumId = track.AlbumId;
            var title = track.Album;
            if (string.IsNullOrWhiteSpace(albumId))
            {
                if (_resolvedAlbumGeneration != generation)
                { _resolvedTrackAlbums.Clear(); _resolvedAlbumGeneration = generation; }
                if (!_resolvedTrackAlbums.TryGetValue(track.RequestId, out var cached))
                {
                    var token = _accountCts.Token;
                    var session = await EnsureSessionAsync(token);
                    var rows = await _api.GetTracksAsync(session, [TrackReference(track)], 0, token);
                    token.ThrowIfCancellationRequested();
                    if (generation != _accountGeneration || view != _viewGeneration || _closed) return;
                    var exact = rows.FirstOrDefault(row => row.Id == track.Id);
                    if (exact?.AlbumId is not { Length: > 0 } resolved)
                    { SetStatus("У этого трека Яндекс не указал альбом."); return; }
                    cached = new(resolved, "album", exact.Album, exact.Artist, CoverUri: exact.CoverUri);
                    if (_resolvedTrackAlbums.Count >= 256) _resolvedTrackAlbums.Remove(_resolvedTrackAlbums.Keys.First());
                    _resolvedTrackAlbums[track.RequestId] = cached;
                }
                albumId = cached.Id;
                title = cached.Title;
            }
            if (generation != _accountGeneration || view != _viewGeneration || _closed) return;
            await OpenCatalogAsync(new(albumId, "album", string.IsNullOrWhiteSpace(title) ? "Альбом" : title, track.Artist, CoverUri: track.CoverUri));
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { ShowError(ex); }
    }
}
