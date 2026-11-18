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
        bool IsCatalog, string Filter, PlaylistDetails? Playlist, CatalogItem? Opened, int ArtistPage, bool ArtistHasMore,
        ArtistDetails? Artist, bool? ArtistLiked, bool SearchVisible, double Offset, int SelectedIndex,
        object? Selection, AlbumDetails? Album, string SearchText, string SearchType, int SearchPage, bool CollectionsSearch,
        string SearchCategory, bool SearchHasMore, string Query, WaveMode WaveMode, TrackRow? WaveSeedTrack, CatalogItem? WaveSeedAlbum)
    {
        internal long[] QueueSelection { get; init; } = [];
        internal bool PlaylistSelectionReconciled { get; init; }
        internal (string Owner, string Kind)? OfflinePlaylist { get; init; }
        internal int AccountGeneration { get; init; }
        internal string? AccountUid { get; init; }
    }

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
        var list = _catalogShowing ? (ListBox)CatalogList : TracksList;
        var queue = _section == "queue";
        return new(_section, CollectionTitle.Text, _section is "queue" or "queue-history" ? [] : _tracks.ToArray(), _catalogItems.ToArray(),
            _catalogShowing, FilterBox.Text, _activePlaylist, _openedCatalogItem, _artistPage,
            _section == "artist-tracks" && MoreArtistButton.Visibility == Visibility.Visible, _artistDetails, _artistLiked,
            CatalogSearchPanel.Visibility == Visibility.Visible, FindViewer(BrowseScrollContent(list, _section))?.VerticalOffset ?? 0,
            list.SelectedIndex, list.SelectedItem,
            _activeAlbumDetails, _searchText, _searchType, _searchPage, _collectionsSearch,
            (CatalogType.SelectedItem as ComboBoxItem)?.Tag as string ?? "all", MoreCatalogButton.IsEnabled,
            CatalogQuery.Text, _waveViewMode, _waveSeedTrack, _waveSeedAlbum)
        {
            QueueSelection = queue ? CaptureQueueSelection() : [],
            OfflinePlaylist = _section == "offline-playlist-detail" ? _viewedOfflinePlaylist : null,
            AccountGeneration = _accountGeneration,
            AccountUid = _account?.Uid
        };
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

    private void UpdateBrowseArtistMembership(string id, bool liked)
    {
        // Reconcile only existing, bounded page history; membership is not a
        // historical property of an artist and must survive Back/Forward.
        foreach (var stack in new[] { _browseHistory, _browseForward })
        {
            if (!stack.Any(view => view.Section == "artist-detail" && view.Artist?.Id == id && view.ArtistLiked != liked)) continue;
            var views = stack.Reverse().ToArray();
            stack.Clear();
            foreach (var view in views)
                stack.Push(view.Section == "artist-detail" && view.Artist?.Id == id ? view with { ArtistLiked = liked } : view);
        }
    }

    private void UpdateBrowseArtistCollection(ArtistCollectionChange change)
    {
        foreach (var stack in new[] { _browseHistory, _browseForward })
        {
            if (!stack.Any(view => view.Section == "artists" && view.IsCatalog)) continue;
            var views = stack.Reverse().ToArray();
            stack.Clear();
            foreach (var view in views)
            {
                if (view.Section != "artists" || !view.IsCatalog) { stack.Push(view); continue; }
                var catalog = ReconcileLikedArtists(view.Catalog, change);
                if (ReferenceEquals(catalog, view.Catalog)) { stack.Push(view); continue; }
                var removedSelection = !change.Liked && view.Selection is CatalogItem { Kind: "artist" } selected
                    && selected.Id == change.Artist.Id;
                stack.Push(view with
                {
                    Catalog = catalog.ToArray(), Selection = removedSelection ? null : view.Selection,
                    SelectedIndex = removedSelection ? -1 : view.SelectedIndex
                });
            }
        }
    }

    private static bool SamePlaylist(PlaylistDetails left, PlaylistDetails right)
        => left.Owner == right.Owner && left.Kind == right.Kind;

    private static bool MatchesPlaylistItem(CatalogItem? item, PlaylistDetails playlist)
        => item is { Kind: "playlist" } && item.Owner == playlist.Owner && (item.PlaylistKind ?? item.Id) == playlist.Kind;

    private static TrackRow? RemapPlaylistSelection(IReadOnlyList<TrackRow> previous, IEnumerable<TrackRow> next, TrackRow selected)
    {
        var previousCount = 0;
        var selectedOrdinal = 0;
        foreach (var row in previous)
        {
            if (row.RequestId != selected.RequestId) continue;
            previousCount++;
            if (!ReferenceEquals(row, selected)) continue;
            if (selectedOrdinal > 0) return null;
            selectedOrdinal = previousCount;
        }
        if (selectedOrdinal == 0) return null;

        var nextCount = 0;
        TrackRow? exact = null;
        TrackRow? byOrdinal = null;
        foreach (var row in next)
        {
            if (row.RequestId != selected.RequestId) continue;
            if (++nextCount == selectedOrdinal) byOrdinal = row;
            if (!ReferenceEquals(row, selected)) continue;
            if (exact is not null) return null;
            exact = row;
        }
        // With reconstructed rows, repeated IDs only identify occurrences by source order.
        // A different multiplicity cannot distinguish which occurrence survived.
        return exact ?? (nextCount == previousCount && selected.RequestId.Length > 0 ? byOrdinal : null);
    }

    private static TrackRow? RemapOnlinePlaylistSelection(PlaylistDetails previous, PlaylistDetails next, TrackRow selected, PlaylistTrackRemoval? removal)
    {
        if (removal is not null && previous.Revision <= removal.Before.Revision && selected.RequestId == removal.Track.RequestId)
        {
            var beforeWrite = previous.Revision == removal.Before.Revision
                ? RemapPlaylistSelection(previous.Tracks, removal.Before.Tracks, selected) : null;
            if (beforeWrite is null || ReferenceEquals(beforeWrite, removal.Track)) return null;
        }
        return RemapPlaylistSelection(previous.Tracks, next.Tracks, selected);
    }

    private PlaylistDetails? FindPlaylistSnapshot(PlaylistDetails header)
    {
        bool Matches(PlaylistDetails details) => SamePlaylist(details, header)
            && (details.Revision > header.Revision || details.Revision == header.Revision && details.Title == header.Title);

        if (_activePlaylist is { } active && Matches(active)) return active;
        return _browseHistory.Concat(_browseForward).Select(view => view.Playlist).Append(_playbackContext?.Playlist)
            .OfType<PlaylistDetails>().Where(Matches).MaxBy(details => details.Revision);
    }

    private static CatalogItem WithPlaylistMetadata(CatalogItem item, PlaylistDetails playlist)
        => item with { Title = playlist.Title, Subtitle = playlist.Description,
            CoverUri = playlist.CoverUri ?? item.CoverUri, OwnerName = playlist.OwnerName ?? item.OwnerName };

    private void UpdateBrowsePlaylist(PlaylistDetails playlist, bool deleted, bool created = false, PlaylistTrackRemoval? removal = null)
    {
        playlist = PresentPlaylistAuthor(playlist);
        TrackRow[]? updatedTracks = null;
        CatalogItem UpdatedItem(CatalogItem item) => WithPlaylistMetadata(item, playlist);

        BrowseSnapshot? Reconcile(BrowseSnapshot view)
        {
            if (view.Section == "playlist-detail" && (view.Playlist is { } shown
                ? SamePlaylist(shown, playlist) : MatchesPlaylistItem(view.Opened, playlist)))
            {
                if (deleted) return null;
                if (view.Playlist is { } previous && previous.Revision > playlist.Revision) return view;
                var selection = view.Playlist is { } previousPlaylist && view.Selection is TrackRow row
                    ? RemapOnlinePlaylistSelection(previousPlaylist, playlist, row, removal) : null;
                return view with
                {
                    Title = playlist.Title, Tracks = updatedTracks ??= playlist.Tracks.ToArray(), Playlist = playlist,
                    Opened = view.Opened is { } opened ? UpdatedItem(opened) : null,
                    Selection = selection,
                    SelectedIndex = -1,
                    PlaylistSelectionReconciled = view.PlaylistSelectionReconciled || view.Selection is TrackRow
                };
            }

            var index = Array.FindIndex(view.Catalog, item => MatchesPlaylistItem(item, playlist));
            if (index < 0)
                return created && view is { Section: "playlists", IsCatalog: true }
                    ? view with { Catalog = [.. view.Catalog, PlaylistCatalogItem(playlist)] }
                    : view;
            var catalog = view.Catalog.ToList();
            var selected = ReferenceEquals(view.Selection, catalog[index]);
            if (deleted) catalog.RemoveAt(index);
            else catalog[index] = UpdatedItem(catalog[index]);
            return view with
            {
                Catalog = catalog.ToArray(),
                Selection = selected ? deleted ? null : catalog[index] : view.Selection,
                SelectedIndex = selected && deleted ? -1 : view.SelectedIndex
            };
        }

        foreach (var stack in new[] { _browseHistory, _browseForward })
        {
            var views = stack.Reverse().ToArray();
            stack.Clear();
            foreach (var view in views)
                if (Reconcile(view) is { } current) stack.Push(current);
        }
        if (_playbackContext is { Playlist: { } source } context && SamePlaylist(source, playlist))
        {
            if (deleted) ClearPlaybackContext();
            else _playbackContext = Reconcile(context);
        }
        if (_catalogShowing)
        {
            for (var index = _catalogItems.Count - 1; index >= 0; index--)
            {
                if (!MatchesPlaylistItem(_catalogItems[index], playlist)) continue;
                if (deleted) _catalogItems.RemoveAt(index);
                else _catalogItems[index] = UpdatedItem(_catalogItems[index]);
            }
            UpdateCount();
        }
        UpdateBrowseButtons();
    }

    private async void BrowseBack_Click(object sender, RoutedEventArgs e) => await BrowseStepAsync(_browseHistory, _browseForward);
    private async void BrowseForward() => await BrowseStepAsync(_browseForward, _browseHistory);

    private async Task BrowseStepAsync(Stack<BrowseSnapshot> source, Stack<BrowseSnapshot> destination)
    {
        while (source.TryPeek(out var obsolete) && !OwnsBrowseView(obsolete)) source.Pop();
        if (!source.TryPeek(out var previous)) { UpdateBrowseButtons(); return; }
        if (previous.Section == "offline")
            await ReadOfflineViewAsync(_ => _offline.Tracks(), rows => Restore(previous with { Tracks = rows.ToArray() }));
        else if (previous.Section == "offline-playlists")
            await ReadOfflineViewAsync(ReadOfflinePlaylists, items => Restore(previous with { Catalog = items }));
        else if (previous.Section == "offline-playlist-detail" && previous.OfflinePlaylist is { } playlist)
            await ReadOfflineViewAsync(token => ReadOfflinePlaylist(playlist.Owner, playlist.Kind, previous.Title, token), view =>
            {
                var selection = previous.Selection is TrackRow row ? RemapPlaylistSelection(previous.Tracks, view.Rows, row) : null;
                if (Restore(previous with { Title = view.Title, Tracks = view.Rows.ToArray(), Selection = selection, SelectedIndex = -1 }))
                    SetOfflinePlaylistStatus(view.Status);
            });
        else Restore(previous);

        bool Restore(BrowseSnapshot refreshed)
        {
            if (!source.TryPeek(out var current) || !ReferenceEquals(current, previous) || !OwnsBrowseView(current)) return false;
            source.Pop();
            PushBrowse(destination, CaptureBrowseView());
            RestoreBrowseView(refreshed, useLiveFavorites: true);
            RefreshFavoritesOnOpen();
            return true;
        }
    }

    private bool OwnsBrowseView(BrowseSnapshot snapshot)
        => !_closed && snapshot.AccountGeneration == _accountGeneration && snapshot.AccountUid == _account?.Uid;

    private void RestoreBrowseView(BrowseSnapshot previous, bool useLiveFavorites = false)
    {
        if (!OwnsBrowseView(previous)) return;
        _restoringBrowse = true;
        try
        {
            _waveViewMode = previous.WaveMode; _waveSeedTrack = previous.WaveSeedTrack; _waveSeedAlbum = previous.WaveSeedAlbum;
            NavigateSection(previous.Section, previous.Title, remember: false);
            if (previous.Section == "offline-playlist-detail") _viewedOfflinePlaylist = previous.OfflinePlaylist;
            _openedCatalogItem = previous.Opened; _artistPage = previous.ArtistPage;
            _searchText = previous.SearchText; _searchType = previous.SearchType; _searchPage = previous.SearchPage;
            _collectionsSearch = previous.CollectionsSearch; CatalogQuery.Text = previous.Query;
            CatalogType.SelectedItem = CatalogType.Items.OfType<ComboBoxItem>().First(item => (string)item.Tag == previous.SearchCategory);
            MoreCatalogButton.IsEnabled = previous.Section == "search" && previous.IsCatalog && previous.SearchHasMore;
            if (previous.Section == "home") ShowHomeSections(_homeSections);
            else if (previous.Section == "artist-detail")
            {
                if (previous.Artist is { } artist)
                {
                    _artistLiked = previous.ArtistLiked;
                    ShowArtistDetails(artist);
                    if (!_artistLiked.HasValue) _ = RefreshArtistMembershipAsync(artist.Id);
                }
                else ClearArtistDetails(previous.Title, "Обновите раздел, чтобы загрузить музыку исполнителя.");
            }
            else if (previous.Playlist is { } playlist) ShowPlaylist(playlist);
            else if (previous.IsCatalog)
            {
                ShowCatalogItems(previous.Catalog);
                if (previous.Section == "playlists") ShowPlaylistCatalogActions();
            }
            else if (previous.Section is "queue" or "queue-history")
            {
                FilterBox.Text = previous.Filter;
                _filterTimer.Stop();
                _query = previous.Filter.Trim();
                if (previous.Section == "queue") ShowQueueRows();
                else ShowQueueHistoryRows();
            }
            else
            {
                ShowRows(useLiveFavorites && previous.Section == "favorites" ? _favoritesCache : previous.Tracks);
                if (previous.Album is { } album) ShowCollectionHero(album, previous.Title);
                if (previous.Section == "artist-tracks") ShowArtistPagination(previous.ArtistHasMore);
            }
            CatalogSearchPanel.Visibility = previous.SearchVisible ? Visibility.Visible : Visibility.Collapsed;
            if (previous.Section is not ("queue" or "queue-history")) FilterBox.Text = previous.Filter;
            var list = previous.IsCatalog ? (ListBox)CatalogList : TracksList;
            var restorePlaylistSelection = previous.Section == "playlist-detail"
                && (previous.PlaylistSelectionReconciled || previous.Selection is TrackRow);
            if (previous.Section == "queue") RestoreQueueSelection(previous.QueueSelection);
            else if (previous.Section == "queue-history")
                list.SelectedItem = previous.Selection is TrackRow historyRow && list.Items.Contains(historyRow) ? historyRow : null;
            else if (restorePlaylistSelection)
                list.SelectedItem = previous.Selection is TrackRow playlistRow && list.Items.Contains(playlistRow) ? playlistRow : null;
            else if (previous.Selection is not null && list.Items.Contains(previous.Selection)) list.SelectedItem = previous.Selection;
            else if (previous.Section is "favorites" or "offline" && previous.Selection is TrackRow selected)
                list.SelectedItem = list.Items.OfType<TrackRow>().FirstOrDefault(row => row.RequestId == selected.RequestId);
            else if (previous.Section == "offline-playlists" && previous.Selection is CatalogItem local)
                list.SelectedItem = list.Items.OfType<CatalogItem>().FirstOrDefault(item => item.Owner == local.Owner && item.PlaylistKind == local.PlaylistKind);
            else if (previous.SelectedIndex >= 0 && previous.SelectedIndex < list.Items.Count) list.SelectedIndex = previous.SelectedIndex;
            var viewGeneration = _viewGeneration; var accountGeneration = _accountGeneration;
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
            {
                if (!_closed && _viewGeneration == viewGeneration && _accountGeneration == accountGeneration)
                    FindViewer(BrowseScrollContent(list, previous.Section))?.ScrollToVerticalOffset(previous.Offset);
            }));
            UpdateBrowseButtons();
            UpdateWavePresentation();
            SetStatus("Вернулись к " + previous.Title);
            SyncPlaybackContextSelection(preserveSelectionIfUnavailable: restorePlaylistSelection);
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
        _playlistCatalogState.Clear(); _playlistCatalogAccount = null; _playlistMutationRevision = 0;
        _playlistChooserRequest = null;
        _resolvedTrackAlbums.Clear(); _resolvedAlbumGeneration = -1;
        CancelArtistMembershipRead();
        _likedArtistsReadChanges = null;
        _artistDetails = null; _artistLiked = null; _homeSections = [];
        _catalogItems.Clear(); _openedCatalogItem = null;
        CatalogQuery.Clear();
        CatalogType.SelectedIndex = 0;
        _collectionsSearch = false;
        ResetSearchResults();
        UpdateSearchCategories();
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

    private FrameworkElement BrowseScrollContent(ListBox list, string section) => section switch
    {
        "home" => HomePanel, "artist-detail" => ArtistPanel, _ => list
    };

    private FrameworkElement CurrentContent() => _settingsOpen ? SettingsPanel : _lyricsOpen ? LyricsPanel : _section switch
    {
        "wave" => WavePanel, "home" => HomePanel, "artist-detail" => ArtistPanel, "download-center" => DownloadCenterPanel, _ => ContentGrid
    };
}
