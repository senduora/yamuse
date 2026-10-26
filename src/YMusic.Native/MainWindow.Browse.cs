using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace YMusic.Native;

public partial class MainWindow
{
    private readonly Stack<BrowseSnapshot> _browseHistory = new();
    private readonly Stack<BrowseSnapshot> _browseForward = new();
    private bool _restoringBrowse;
    private sealed record BrowseSnapshot(string Section, string Title, TrackRow[] Tracks, CatalogItem[] Catalog,
        bool IsCatalog, string Filter, PlaylistDetails? Playlist, CatalogItem? Opened, int ArtistPage,
        ArtistDetails? Artist, bool? ArtistLiked, bool SearchVisible, double Offset, int SelectedIndex,
        object? Selection, AlbumDetails? Album, string SearchText, string SearchType, int SearchPage, bool CollectionsSearch,
        string SearchCategory, bool SearchHasMore, string Query, WaveMode WaveMode, TrackRow? WaveSeedTrack);

    private void RememberBrowseView()
    {
        // A player link can be used while Settings covers Home or an artist page.
        // Capture the underlying music view's offset, not the Settings scroll position.
        SetSettingsVisible(false);
        PushBrowse(_browseHistory, CaptureBrowseView());
        _browseForward.Clear();
    }
    private BrowseSnapshot CaptureBrowseView()
    {
        var list = _catalogShowing ? (ItemsControl)CatalogList : TracksList;
        return new(_section, CollectionTitle.Text, _tracks.ToArray(), _catalogItems.ToArray(),
            _catalogShowing, FilterBox.Text, _activePlaylist, _openedCatalogItem, _artistPage, _artistDetails, _artistLiked,
            CatalogSearchPanel.Visibility == Visibility.Visible, FindViewer(_section is "home" or "artist-detail" ? CurrentContent() : list)?.VerticalOffset ?? 0,
            _catalogShowing ? CatalogList.SelectedIndex : TracksList.SelectedIndex, _catalogShowing ? CatalogList.SelectedItem : TracksList.SelectedItem,
            _activeAlbumDetails, _searchText, _searchType, _searchPage, _collectionsSearch,
            (CatalogType.SelectedItem as ComboBoxItem)?.Tag as string ?? "all", MoreCatalogButton.IsEnabled,
            CatalogQuery.Text, _waveViewMode, _waveSeedTrack);
    }
    private static void PushBrowse(Stack<BrowseSnapshot> stack, BrowseSnapshot snapshot)
    {
        if (stack.Count >= 24)
        {
            var retained = stack.Take(23).Reverse().ToArray(); stack.Clear();
            foreach (var item in retained) stack.Push(item);
        }
        stack.Push(snapshot);
    }

    private void BrowseBack_Click(object sender, RoutedEventArgs e)
    {
        if (!_browseHistory.TryPop(out var previous)) return;
        PushBrowse(_browseForward, CaptureBrowseView()); RestoreBrowseView(previous);
    }
    private void BrowseForward()
    {
        if (!_browseForward.TryPop(out var next)) return;
        PushBrowse(_browseHistory, CaptureBrowseView()); RestoreBrowseView(next);
    }
    private void RestoreBrowseView(BrowseSnapshot previous)
    {
        _restoringBrowse = true;
        try
        {
        _waveViewMode = previous.WaveMode; _waveSeedTrack = previous.WaveSeedTrack;
        NavigateSection(previous.Section, previous.Title, remember: false);
        _openedCatalogItem = previous.Opened; _artistPage = previous.ArtistPage;
        _searchText = previous.SearchText; _searchType = previous.SearchType; _searchPage = previous.SearchPage;
        _collectionsSearch = previous.CollectionsSearch; CatalogQuery.Text = previous.Query; _waveViewMode = previous.WaveMode;
        CatalogType.SelectedItem = CatalogType.Items.OfType<ComboBoxItem>().First(item => (string)item.Tag == previous.SearchCategory);
        MoreCatalogButton.IsEnabled = previous.Section == "search" && previous.IsCatalog && previous.SearchHasMore;
        if (previous.Section == "home") ShowHomeSections(_homeSections);
        else if (previous.Section == "artist-detail" && previous.Artist is { } artist)
        { _artistLiked = previous.ArtistLiked; ShowArtistDetails(artist); }
        else if (previous.Playlist is { } playlist) ShowPlaylist(playlist);
        else if (previous.IsCatalog) ShowCatalogItems(previous.Catalog);
        else { ShowRows(previous.Tracks); if (previous.Album is { } album) ShowCollectionHero(album, previous.Title); }
        CatalogSearchPanel.Visibility = previous.SearchVisible ? Visibility.Visible : Visibility.Collapsed;
        FilterBox.Text = previous.Filter;
        var list = previous.IsCatalog ? (ListBox)CatalogList : TracksList;
        if (previous.Selection is not null && list.Items.Contains(previous.Selection)) list.SelectedItem = previous.Selection;
        else if (previous.SelectedIndex >= 0 && previous.SelectedIndex < list.Items.Count) list.SelectedIndex = previous.SelectedIndex;
        var viewGeneration = _viewGeneration; var accountGeneration = _accountGeneration;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            if (!_closed && _viewGeneration == viewGeneration && _accountGeneration == accountGeneration)
                FindViewer(previous.Section is "home" or "artist-detail" ? CurrentContent() : list)?.ScrollToVerticalOffset(previous.Offset);
        }));
        UpdateBrowseButtons();
        UpdateWavePresentation();
        SetStatus("Вернулись к " + previous.Title);
        SyncPlaybackContextSelection();
        }
        finally { _restoringBrowse = false; }
    }
    private void Window_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton is not (MouseButton.XButton1 or MouseButton.XButton2)) return;
        e.Handled = true;
        if (e.ChangedButton == MouseButton.XButton1)
        {
            if (IsExpandedPlayerOpen) CloseExpandedPlayer();
            else if (_settingsOpen) SettingsClose_Click(sender, e);
            else if (_lyricsOpen) SetLyricsVisible(false);
            else BrowseBack_Click(sender, e);
        }
        else if (!IsExpandedPlayerOpen && !_settingsOpen && !_lyricsOpen) BrowseForward();
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
        _browseHistory.Clear(); _browseForward.Clear(); _resolvedArtists.Clear(); _resolvedArtistGeneration = -1;
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
        "wave" => WavePanel, "home" => HomePanel, "artist-detail" => ArtistPanel, "download-center" => DownloadCenterPanel, _ => ContentGrid
    };
}
