using System.Windows;
using System.Windows.Controls;

namespace YMusic.Native;

public partial class MainWindow
{
    private readonly Dictionary<string, CatalogItem> _resolvedTrackAlbums = new(StringComparer.Ordinal);
    private int _resolvedAlbumGeneration = -1;
    private long _trackLinkLookup;
    private CancellationTokenSource? _trackLinkLookupCts;
    private bool HasPendingTrackLinkNavigation => _trackLinkLookupCts is { IsCancellationRequested: false };

    private void CancelTrackLinkLookup()
    {
        var lookup = _trackLinkLookupCts;
        _trackLinkLookupCts = null;
        lookup?.Cancel();
    }

    private (CancellationToken Token, Func<bool> IsCurrent, Action Finish) BeginTrackLinkLookup()
    {
        var previous = _trackLinkLookupCts;
        _trackLinkLookup++;
        var generation = _accountGeneration;
        var uid = _account?.Uid;
        var view = _viewGeneration;
        var lookup = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, _accountCts.Token);
        _trackLinkLookupCts = lookup;
        previous?.Cancel();
        CancelPlaylistChooser();
        if (HasPendingOfflineNavigation) _offlineNavigation?.Cancel();
        var token = lookup.Token;
        bool IsCurrent() => !_closed && !token.IsCancellationRequested && ReferenceEquals(_trackLinkLookupCts, lookup)
            && generation == _accountGeneration && uid == _account?.Uid && view == _viewGeneration;
        return (token, IsCurrent, () =>
        {
            var current = ReferenceEquals(_trackLinkLookupCts, lookup);
            if (current) _trackLinkLookupCts = null;
            lookup.Dispose();
            if (current) ResumeDeletedPlaylistFallback();
        });
    }

    private void UpdatePlayerArtistLinks()
    {
        if (PlayerArtistPanel is null) return;
        if (PlayerMoreButton is { } more) more.IsEnabled = _current is not null && !_closed;
        PlayerCoverButton.Tag = PlayerTitleButton.Tag = _current;
        PlayerCoverButton.IsEnabled = PlayerTitleButton.IsEnabled = _current is not null && _session is not null;
        var visible = _current is { } track && (track.Artists.Count > 0 || !string.IsNullOrWhiteSpace(track.Artist));
        if (!ReferenceEquals(PlayerArtistLinks.Content, _current))
        {
            ClearTrackArtistsMenu(PlayerArtistPanel);
            PlayerArtistLinks.Content = _current;
            if (_current is { Artists.Count: > 0 } current)
                PlayerArtistPanel.ContextMenu = CreateTrackArtistsMenu(PlayerArtistPanel, current.Artists, () => ReferenceEquals(_current, current));
        }
        PlayerArtistPanel.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        PlayerArtistPanel.IsEnabled = _session is not null;
        TrackArtist.Visibility = visible ? Visibility.Collapsed : Visibility.Visible;
    }

    private void ShowTrackArtistsMenu(Button button, TrackRow track, IReadOnlyList<ArtistReference> artists)
    {
        var menu = CreateTrackArtistsMenu(button, artists, () => ReferenceEquals(button.Tag, track));
        button.ContextMenu = menu;
        menu.IsOpen = true;
    }

    private ContextMenu CreateTrackArtistsMenu(FrameworkElement anchor, IReadOnlyList<ArtistReference> artists, Func<bool> ownsTrack)
    {
        var generation = _accountGeneration;
        var menu = new ContextMenu { PlacementTarget = anchor };
        foreach (var artist in artists)
        {
            var item = new MenuItem { Header = artist.Name };
            item.Click += async (_, _) =>
            {
                if (generation != _accountGeneration || _closed || _session is null || !ownsTrack()) return;
                if (IsExpandedPlayerOpen && _expandedPlayerArtistLinks is not null
                    && (ReferenceEquals(anchor, _expandedPlayerArtistLinks) || anchor.IsDescendantOf(_expandedPlayerArtistLinks))) CloseExpandedPlayer();
                await OpenArtistAsync(artist.Id, artist.Name);
            };
            menu.Items.Add(item);
        }
        return menu;
    }

    private void TrackArtists_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is FrameworkElement anchor) ClearTrackArtistsMenu(anchor);
    }

    private static void ClearTrackArtistsMenu(FrameworkElement anchor)
    {
        if (anchor.ContextMenu is not { } old) return;
        old.IsOpen = false;
        anchor.ContextMenu = null;
    }

    private void TrackArtists_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (_session is null || sender is not FrameworkElement { DataContext: TrackRow { Artists.Count: > 0 } track } anchor) return;
        e.Handled = true;
        var menu = CreateTrackArtistsMenu(anchor, track.Artists, () => ReferenceEquals(anchor.DataContext, track));
        anchor.ContextMenu = menu;
        menu.IsOpen = true;
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
        var lookup = BeginTrackLinkLookup();
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
                    var session = await EnsureSessionAsync(lookup.Token);
                    if (!lookup.IsCurrent()) return;
                    var rows = await _api.GetTracksAsync(session, [TrackReference(track)], 0, lookup.Token);
                    if (!lookup.IsCurrent()) return;
                    var exact = rows.Single();
                    if (exact.AlbumId is not { Length: > 0 } resolved)
                    { SetStatus("У этого трека Яндекс не указал альбом."); return; }
                    cached = new(resolved, "album", exact.Album, exact.Artist, CoverUri: exact.CoverUri);
                    if (_resolvedTrackAlbums.Count >= 256) _resolvedTrackAlbums.Remove(_resolvedTrackAlbums.Keys.First());
                    _resolvedTrackAlbums[track.RequestId] = cached;
                }
                albumId = cached.Id;
                title = cached.Title;
            }
            if (!lookup.IsCurrent()) return;
            await OpenCatalogAsync(new(albumId, "album", string.IsNullOrWhiteSpace(title) ? "Альбом" : title, track.Artist, CoverUri: track.CoverUri));
        }
        catch (Exception ex) { if (lookup.IsCurrent()) ShowError(ex); }
        finally { lookup.Finish(); }
    }
}
