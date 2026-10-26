using System.Windows;
using System.Windows.Controls;

namespace YMusic.Native;

public partial class MainWindow
{
    // Rows are independent view instances. Their marks are keyed by the service
    // track identity, never by playlist position or a particular album context.
    private IEnumerable<TrackRow> KnownTrackRows() => _tracks.Concat(_favoritesCache).Concat(_waveTracks).Concat(_queue)
        .Concat(_catalogItems.Select(item => item.Track).OfType<TrackRow>())
        .Concat(_homeSections.SelectMany(section => section.Items).Select(item => item.Track).OfType<TrackRow>())
        .Concat(_artistDetails?.TopTracks ?? [])
        .Concat(_current is { } current ? [current] : Array.Empty<TrackRow>()).Distinct();

    private void ApplyTrackLikeStates(IEnumerable<TrackRow> rows)
    {
        var canToggle = _favoriteIdsKnown && _session is not null && _account is not null && !_catalogWriting && !_demo;
        foreach (var row in rows)
        {
            row.IsLiked = _favoriteIdsKnown && _favoriteTrackIds.Contains(row.Id);
            row.LikeStateKnown = _favoriteIdsKnown;
            row.CanToggleLike = canToggle && row.Id.Length > 0;
        }
    }

    private async void TrackLike_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is not Button { Tag: TrackRow row } || !row.CanToggleLike || !_favoriteIdsKnown || _catalogWriting) return;
        await SetTrackLikeAsync(row, !_favoriteTrackIds.Contains(row.Id));
    }

    private async void TrackCoverPlay_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (_closed || sender is not Button { DataContext: TrackRow row } button || !row.CanPlay) return;
        if (ItemsControl.ContainerFromElement(TracksList, button) is ListViewItem item)
        {
            // Use the actual row container, including repeated occurrences in
            // the queue, and the same filtered/sorted queue as double-click.
            var index = TracksList.ItemContainerGenerator.IndexFromContainer(item);
            if (index < 0 || !ReferenceEquals(TracksList.Items[index], row)) return;
            TracksList.SelectedIndex = index;
            await PlaySelectedAsync();
            return;
        }
        if (ItemsControl.ContainerFromElement(ArtistTopTracks, button) is ListBoxItem)
        {
            await PlayArtistTopAsync(row);
            return;
        }
        if (!_catalogShowing || ItemsControl.ContainerFromElement(CatalogList, button) is not ListBoxItem) return;
        var rows = CatalogList.Items.OfType<CatalogItem>().Where(result => result.Kind == "track")
            .Select(result => result.Track).OfType<TrackRow>().ToArray();
        if (!rows.Any(track => ReferenceEquals(track, row))) return;
        _playbackQueue.SetItems(rows, row);
        await PlayTrackAsync(row);
    }
}
