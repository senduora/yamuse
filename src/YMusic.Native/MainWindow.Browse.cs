using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace YMusic.Native;

public partial class MainWindow
{
    private readonly Stack<BrowseSnapshot> _browseHistory = new();
    private sealed record BrowseSnapshot(string Section, string Title, TrackRow[] Tracks, CatalogItem[] Catalog,
        bool IsCatalog, string Filter, PlaylistDetails? Playlist, CatalogItem? Opened, int ArtistPage,
        ArtistDetails? Artist, bool? ArtistLiked, bool SearchVisible, double Offset, int SelectedIndex);

    private void RememberBrowseView()
    {
        // A player link can be used while Settings covers Home or an artist page.
        // Capture the underlying music view's offset, not the Settings scroll position.
        SetSettingsVisible(false);
        if (_browseHistory.Count >= 24) _browseHistory.Clear();
        var list = _catalogShowing ? (ItemsControl)CatalogList : TracksList;
        _browseHistory.Push(new(_section, CollectionTitle.Text, _tracks.ToArray(), _catalogItems.ToArray(),
            _catalogShowing, FilterBox.Text, _activePlaylist, _openedCatalogItem, _artistPage, _artistDetails, _artistLiked,
            CatalogSearchPanel.Visibility == Visibility.Visible, FindViewer(_section is "home" or "artist-detail" ? CurrentContent() : list)?.VerticalOffset ?? 0,
            _catalogShowing ? CatalogList.SelectedIndex : TracksList.SelectedIndex));
    }

    private void BrowseBack_Click(object sender, RoutedEventArgs e)
    {
        if (!_browseHistory.TryPop(out var previous)) return;
        SetSection(previous.Section, previous.Title);
        _openedCatalogItem = previous.Opened; _artistPage = previous.ArtistPage;
        if (previous.Section == "home") ShowHomeSections(_homeSections);
        else if (previous.Section == "artist-detail" && previous.Artist is { } artist)
        { _artistLiked = previous.ArtistLiked; ShowArtistDetails(artist); }
        else if (previous.Playlist is { } playlist) ShowPlaylist(playlist);
        else if (previous.IsCatalog) ShowCatalogItems(previous.Catalog);
        else ShowRows(previous.Tracks);
        CatalogSearchPanel.Visibility = previous.SearchVisible ? Visibility.Visible : Visibility.Collapsed;
        FilterBox.Text = previous.Filter;
        var list = previous.IsCatalog ? (ListBox)CatalogList : TracksList;
        if (previous.SelectedIndex >= 0 && previous.SelectedIndex < list.Items.Count) list.SelectedIndex = previous.SelectedIndex;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            if (!_closed && _section == previous.Section)
                FindViewer(previous.Section is "home" or "artist-detail" ? CurrentContent() : list)?.ScrollToVerticalOffset(previous.Offset);
        }));
        UpdateBrowseButtons();
        SetStatus("Вернулись к " + previous.Title);
    }

    private void UpdateBrowseButtons()
    {
        if (BrowseBackButton is null) return;
        BrowseBackButton.Visibility = _browseHistory.Count > 0 && (_section.EndsWith("-detail", StringComparison.Ordinal) || _section is "home-shelf" or "artist-tracks")
            ? Visibility.Visible : Visibility.Collapsed;
        ArtistBackButton.IsEnabled = _browseHistory.Count > 0;
    }

    private void ResetBrowseState()
    {
        _browseHistory.Clear(); _resolvedArtists.Clear(); _resolvedArtistGeneration = -1;
        _artistDetails = null; _artistLiked = null; _homeSections = [];
        _catalogItems.Clear(); _openedCatalogItem = null;
        HomeShelves.ItemsSource = null;
        ArtistTopTracks.ItemsSource = ArtistAlbums.ItemsSource = ArtistSimilar.ItemsSource = null;
        ArtistName.Text = "Исполнитель"; ArtistMonthlyText.Text = ArtistDescription.Text = "";
        HomeHeading.Text = "Ваша музыка";
        Artwork.SetSourceUri(ArtistPortrait, null);
    }

    private static ScrollViewer? FindViewer(DependencyObject root)
    {
        if (root is ScrollViewer viewer) return viewer;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            if (FindViewer(VisualTreeHelper.GetChild(root, i)) is { } found) return found;
        return null;
    }

    private FrameworkElement CurrentContent() => _settingsOpen ? SettingsPanel : _lyricsOpen ? LyricsPanel : _section switch
    {
        "wave" => WavePanel, "home" => HomePanel, "artist-detail" => ArtistPanel, _ => ContentGrid
    };
}
