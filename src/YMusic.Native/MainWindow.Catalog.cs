using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace YMusic.Native;

public partial class MainWindow
{
    private readonly ObservableCollection<CatalogItem> _catalogItems = new();
    private bool _catalogShowing;
    private bool _catalogWriting;
    private object? _playlistChooserRequest;
    private int _searchPage;
    private string _searchText = "";
    private string _searchType = "all";
    private PlaylistDetails? _activePlaylist;
    private sealed record PlaylistCatalogState(PlaylistDetails Header, long Mutation = 0,
        bool AwaitingCollection = false, bool Deleted = false, bool TitleConfirmed = false);
    private sealed record PlaylistTrackRemoval(PlaylistDetails Before, TrackRow Track);
    // Keep revision floors and card metadata for this account. Full track lists belong to visible or saved views.
    private readonly Dictionary<(string Owner, string Kind), PlaylistCatalogState> _playlistCatalogState = new();
    private string? _playlistCatalogAccount;
    private long _playlistMutationRevision;
    private CatalogItem? _openedCatalogItem;
    private int _artistPage;

    private void SearchNav_Click(object sender, RoutedEventArgs e)
    {
        SetSection("search", "Поиск");
        CatalogSearchPanel.Visibility = Visibility.Visible;
        ResetSearchResults();
        ShowCatalogItems([]);
        UpdateSearchCategories();
        CatalogQuery.Focus(); SetStatus("Введите запрос и нажмите Enter или «Найти».");
    }
    private void ResetSearchResults()
    {
        _searchText = ""; _searchPage = 0;
        _searchType = _collectionsSearch ? "collections" : (CatalogType.SelectedItem as ComboBoxItem)?.Tag as string ?? "all";
        MoreCatalogButton.IsEnabled = false;
    }
    private async void PlaylistsNav_Click(object sender, RoutedEventArgs e) => await LoadCollectionAsync("playlists");
    private async void AlbumsNav_Click(object sender, RoutedEventArgs e) => await LoadCollectionAsync("albums");
    private async void ArtistsNav_Click(object sender, RoutedEventArgs e) => await LoadCollectionAsync("artists");
    private async void CatalogSearch_Click(object sender, RoutedEventArgs e) => await SearchCatalogViewAsync(false);
    private async void MoreCatalog_Click(object sender, RoutedEventArgs e) => await SearchCatalogViewAsync(true);
    private async void CatalogQuery_KeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) { e.Handled = true; await SearchCatalogViewAsync(false); } }
    private void Catalog_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateControls();
    private async void Catalog_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (FromButton(e.OriginalSource as DependencyObject)) return;
        if (ItemsControl.ContainerFromElement(CatalogList, e.OriginalSource as DependencyObject) is ListBoxItem)
            await OpenSelectedCatalogAsync();
    }
    private void ShowCatalogItems(IEnumerable<CatalogItem> items, bool append = false)
    {
        _catalogShowing = true; _tracks.Clear();
        TracksList.Visibility = Visibility.Collapsed; CatalogList.Visibility = Visibility.Visible;
        CatalogList.ItemsSource = _catalogItems;
        if (!append) _catalogItems.Clear();
        var snapshot = CurrentCatalogItems(items, includeCreatedPlaylists: _section == "playlists");
        ApplyOfflineStates(snapshot.Select(item => item.Track).OfType<TrackRow>());
        foreach (var item in snapshot) _catalogItems.Add(item);
        GroupCatalogResults();
        FilterBox.IsEnabled = false; FilterHint.Text = "Дважды нажмите на результат, чтобы открыть";
        PlaySelectionButton.Content = "Открыть";
        UpdateCount();
        if (!append) Motion.Enter(CatalogList);
    }
    private void HideCatalog()
    {
        _catalogShowing = false;
        CatalogList.Visibility = Visibility.Collapsed; TracksList.Visibility = Visibility.Visible;
        CatalogSearchPanel.Visibility = Visibility.Collapsed; PlaylistActions.Visibility = Visibility.Collapsed;
        CreatePlaylistButton.Visibility = RenamePlaylistButton.Visibility = DeletePlaylistButton.Visibility = RemovePlaylistTrackButton.Visibility = Visibility.Collapsed;
        MoreArtistButton.Visibility = Visibility.Collapsed;
        PlaySelectionButton.Content = "▶  Слушать"; _activePlaylist = null;
    }
    private void CollectionOptions_Click(object sender, RoutedEventArgs e)
        => CollectionOptionsPopup.IsOpen = !CollectionOptionsPopup.IsOpen;
    private async Task SearchCatalogViewAsync(bool more)
    {
        if (_session is null) { SetStatus("Войдите в Яндекс для поиска по каталогу."); return; }
        var query = CatalogQuery.Text.Trim();
        if (query.Length == 0)
        {
            if (!more && _section == "search")
            {
                _featureCts?.Cancel();
                ResetSearchResults();
                ShowCatalogItems([]);
            }
            SetStatus("Введите название трека, альбома или исполнителя.");
            return;
        }
        if (more && _featureCts is { IsCancellationRequested: false }) return;
        var type = _collectionsSearch ? "collections" : (CatalogType.SelectedItem as ComboBoxItem)?.Tag as string ?? "all";
        var page = more && query == _searchText && type == _searchType ? _searchPage + 1 : 0;
        if (_section != "search") SetSection("search", "Поиск");
        CatalogSearchPanel.Visibility = Visibility.Visible;
        if (page == 0)
        {
            ResetSearchResults();
            ShowCatalogItems([]);
        }
        await CatalogReadAsync(async (session, token, isCurrent) =>
        {
            var result = type == "collections" ? await _api.SearchCollectionsAsync(session, query, page, token)
                : await _api.SearchCatalogAsync(session, query, type, page, token);
            if (!isCurrent()) return;
            _searchPage = result.Page; _searchText = query; _searchType = type;
            ShowCatalogItems(result.Items, page > 0); MoreCatalogButton.IsEnabled = result.HasMore;
            SetStatus(type == "collections" ? $"Сборники и редакционные плейлисты · {_catalogItems.Count:N0}" : $"Результаты поиска · {_catalogItems.Count:N0}");
        });
    }
    private Task LoadCollectionAsync(string kind) => LoadCollectionCoreAsync(kind, preserveSettings: false);

    private async Task LoadCollectionCoreAsync(string kind, bool preserveSettings)
    {
        if (_account is null) { SetStatus("Войдите в Яндекс, чтобы открыть коллекцию."); return; }
        var title = kind == "playlists" ? "Плейлисты" : kind == "albums" ? "Альбомы" : "Исполнители";
        if (preserveSettings)
        {
            _openedCatalogItem = null;
            ApplySection(kind, title, closeSettings: false);
        }
        else SetSection(kind, title);
        ShowCatalogItems([]);
        await CatalogReadAsync(async (session, token, isCurrent) =>
        {
            var uid = _account?.Uid ?? throw new OperationCanceledException(token);
            if (kind == "artists")
            {
                await ReadLikedArtistsCollectionAsync(session, uid, token, isCurrent, ShowCollectionItems);
                return;
            }
            var items = kind == "playlists" ? await _api.GetPlaylistsAsync(session, uid, token)
                : await _api.GetLikedAlbumsAsync(session, uid, token);
            if (!isCurrent()) return;
            if (kind == "playlists") ObservePlaylistCollection(items);
            ShowCollectionItems(items);
            if (kind == "playlists") ShowPlaylistCatalogActions();
        });

        void ShowCollectionItems(IReadOnlyList<CatalogItem> items)
        {
            ShowCatalogItems(items);
            SetStatus($"{title} · {_catalogItems.Count:N0}");
        }
    }
    private void ShowPlaylistCatalogActions()
    {
        PlaylistActions.Visibility = CreatePlaylistButton.Visibility = Visibility.Visible;
    }

    private void EnsurePlaylistCatalogAccount()
    {
        var uid = _account?.Uid;
        if (_playlistCatalogAccount == uid) return;
        _playlistCatalogState.Clear();
        _playlistMutationRevision = 0;
        _playlistCatalogAccount = uid;
    }

    private static CatalogItem PlaylistCatalogItem(PlaylistDetails playlist)
        => new(playlist.Kind, "playlist", playlist.Title, playlist.Description, playlist.Owner, playlist.Kind,
            CoverUri: playlist.CoverUri, OwnerName: playlist.OwnerName);

    private CatalogItem? CurrentPlaylistCatalogItem(CatalogItem item)
    {
        EnsurePlaylistCatalogAccount();
        // Search and list cards carry no revision. Reconcile them with confirmed writes before showing them.
        if (item is not { Kind: "playlist", Owner: { } owner }
            || !_playlistCatalogState.TryGetValue((owner, item.PlaylistKind ?? item.Id), out var state) || state.Mutation == 0)
            return item;
        return state.Deleted ? null : WithPlaylistMetadata(item, state.Header);
    }

    private List<CatalogItem> CurrentCatalogItems(IEnumerable<CatalogItem> items, bool includeCreatedPlaylists)
    {
        EnsurePlaylistCatalogAccount();
        var snapshot = items.Select(CurrentPlaylistCatalogItem).OfType<CatalogItem>().Select(PresentCatalogAuthor).ToList();
        if (includeCreatedPlaylists)
        {
            foreach (var state in _playlistCatalogState.Values)
                if (state is { AwaitingCollection: true, Deleted: false } && state.Header.Owner == _account?.Uid
                    && !snapshot.Any(item => MatchesPlaylistItem(item, state.Header)))
                    snapshot.Add(PlaylistCatalogItem(state.Header));
        }
        return snapshot;
    }

    private void ObservePlaylistCollection(IReadOnlyList<CatalogItem> items)
    {
        foreach (var item in items)
        {
            if (item.Owner is not { } owner) continue;
            var identity = (owner, item.PlaylistKind ?? item.Id);
            if (_playlistCatalogState.GetValueOrDefault(identity) is { AwaitingCollection: true } state)
                _playlistCatalogState[identity] = state with { AwaitingCollection = false };
        }
    }

    private async Task OpenSelectedCatalogAsync()
    {
        if (CatalogList.SelectedItem is not CatalogItem item) return;
        await OpenCatalogAsync(item);
    }
    private async Task OpenCatalogAsync(CatalogItem item, bool remember = true)
    {
        if (item.Kind == "offline-playlist") { await ShowOfflinePlaylist(item); return; }
        if (_session is null) return;
        if (CurrentPlaylistCatalogItem(item) is not { } current)
        {
            SetStatus("Плейлист удалён.");
            return;
        }
        item = current;
        if (item.Kind == "artist") { await OpenArtistAsync(item.Id, item.Title, remember); return; }
        if (remember) RememberBrowseView();
        _openedCatalogItem = item; _artistPage = 0;
        NavigateSection(item.Kind + "-detail", item.Title, remember: false);
        ShowRows([]);
        if (item.Kind == "track")
        {
            await LoadCatalogTrackAsync(item, play: true);
            return;
        }
        await CatalogReadAsync(async (session, token, isCurrent) =>
        {
            if (item.Kind == "playlist")
            {
                var readRevision = _playlistMutationRevision;
                var details = await _api.GetPlaylistAsync(session, item.Owner ?? throw new MusicException("У плейлиста не указан владелец."), item.PlaylistKind ?? item.Id, token);
                if (isCurrent()) ApplyPlaylistRead(details, readRevision);
                return;
            }
            if (item.Kind == "album")
            {
                var album = await _api.GetAlbumDetailsAsync(session, item.Id, token);
                if (!isCurrent()) return;
                ShowRows(album.Tracks); ShowCollectionHero(album, item.Title);
                SetStatus($"Загружено {album.Tracks.Count:N0} треков"); return;
            }
            throw new MusicException("Этот тип результата пока не поддерживается.");
        });
    }
    private async Task LoadCatalogTrackAsync(CatalogItem item, bool play)
    {
        var playback = CurrentCatalogPlaybackIntent();
        await CatalogReadAsync(async (session, token, isCurrent) =>
        {
            var rows = await _api.GetTracksAsync(session, [new LikedId(item.Id, item.AlbumId)], 0, token);
            if (!isCurrent()) return;
            ShowRows(rows);
            var playable = rows.FirstOrDefault(row => row.CanPlay);
            SetStatus(playable is null ? "Этот трек недоступен для воспроизведения." : $"Загружено {rows.Count:N0} треков", playable is null);
            if (play && playable is not null && playback == CurrentCatalogPlaybackIntent()) await PlayTrackAsync(playable);
        });
    }

    private (int Playback, long Queue, long Advance, long Pause, bool Playing) CurrentCatalogPlaybackIntent()
        => (_playbackGeneration, _playbackQueue.Version, _advanceOperation, _wavePauseRevision, _player.IsPlaying);

    private async void MoreArtist_Click(object sender, RoutedEventArgs e)
    {
        if (_openedCatalogItem is not { Kind: "artist" } artist || _section != "artist-tracks"
            || MoreArtistButton.Visibility != Visibility.Visible || _featureCts is { IsCancellationRequested: false }) return;
        var page = _artistPage + 1;
        await CatalogReadAsync(async (session, token, isCurrent) =>
        {
            var rows = await _api.GetArtistTracksAsync(session, artist.Id, page, token);
            if (!isCurrent()) return;
            ApplyOfflineStates(rows);
            foreach (var row in rows)
            {
                row.IsPlaying = _player.HasSource && _current?.RequestId == row.RequestId;
                _tracks.Add(row);
            }
            _artistPage = page;
            ShowArtistPagination(rows.Count == 100);
            UpdateCount(); SetStatus($"Треки исполнителя · загружено {_tracks.Count:N0}");
        });
    }
    private void ApplyPlaylistRead(PlaylistDetails details, long readRevision)
    {
        EnsurePlaylistCatalogAccount();
        var identity = (details.Owner, details.Kind);
        var known = _playlistCatalogState.GetValueOrDefault(identity);
        if (known?.Deleted == true)
        {
            SetStatus("Плейлист удалён.");
            return;
        }
        if (known is not null && (details.Revision < known.Header.Revision
            || details.Revision == known.Header.Revision && (readRevision < known.Mutation
                || known.TitleConfirmed && details.Title != known.Header.Title)))
        {
            if (FindPlaylistSnapshot(known.Header) is { } snapshot)
            {
                if (!ReferenceEquals(snapshot, _activePlaylist)) ShowPlaylist(snapshot);
                else SetStatus("Показана последняя известная версия плейлиста.");
            }
            else SetStatus("Сервис вернул более старую версию плейлиста. Обновите раздел.", true);
            return;
        }
        details = PresentPlaylistAuthor(details);
        if (_account is not null)
        {
            var header = details with { Tracks = [] };
            _playlistCatalogState[identity] = known is null ? new(header) : known with { Header = header };
            if (known is null || details.Revision > known.Header.Revision || details.Title != known.Header.Title)
                UpdateBrowsePlaylist(details, deleted: false);
        }
        ShowPlaylist(details);
    }

    private void ShowPlaylist(PlaylistDetails details) => ShowPlaylistWithSelection(details, removal: null);

    private void ShowPlaylistWithSelection(PlaylistDetails details, PlaylistTrackRemoval? removal)
    {
        details = PresentPlaylistAuthor(details);
        TrackRow? selected = null;
        var restoreSelection = false;
        // A response fills the underlying view while an open Settings draft stays intact.
        if (_section == "playlist-detail" && _activePlaylist is { } current
            && current.Owner == details.Owner && current.Kind == details.Kind)
        {
            if (details.Revision < current.Revision)
            {
                SetStatus("Сервис вернул более старую ревизию плейлиста.", true);
                return;
            }
            // A refresh of this playlist must not expire a mutation in the same view.
            _featureCts?.Cancel();
            if (TracksList.SelectedItem is TrackRow row)
            {
                restoreSelection = true;
                selected = RemapOnlinePlaylistSelection(current, details, row, removal);
            }
        }
        else ApplySection("playlist-detail", details.Title, closeSettings: false);
        CatalogSearchPanel.Visibility = Visibility.Collapsed;
        CreatePlaylistButton.Visibility = Visibility.Collapsed;
        _activePlaylist = details; CollectionTitle.Text = details.Title;
        ShowRows(details.Tracks);
        if (restoreSelection) TracksList.SelectedItem = selected is not null && TracksList.Items.Contains(selected) ? selected : null;
        ShowCollectionHero(details);
        var writable = _account?.Uid == details.Owner && !details.IsGenerated;
        PlaylistActions.Visibility = Visibility.Visible;
        RenamePlaylistButton.Visibility = DeletePlaylistButton.Visibility = RemovePlaylistTrackButton.Visibility = writable ? Visibility.Visible : Visibility.Collapsed;
        SetStatus($"Плейлист · {details.Tracks.Count:N0} треков" + (writable ? " · ваша подборка" : ""));
        UpdatePlaylistDownloadedStatus();
    }
    private Task CatalogReadAsync(Func<Session, CancellationToken, Func<bool>, Task> operation)
        => ReadFeatureViewAsync(async (token, isCurrent) =>
        {
            var session = await EnsureSessionAsync(token);
            if (isCurrent()) await operation(session, token, isCurrent);
        });

    private async Task ReadFeatureViewAsync(Func<CancellationToken, Func<bool>, Task> operation)
    {
        _featureCts?.Cancel();
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, _accountCts.Token);
        _featureCts = cts;
        var account = _accountGeneration;
        var uid = _account?.Uid;
        var view = _viewGeneration;
        bool Current() => !_closed && !cts.IsCancellationRequested && ReferenceEquals(_featureCts, cts)
            && account == _accountGeneration && uid == _account?.Uid && view == _viewGeneration;
        Busy(true); SetStatus("Загрузка…");
        try { if (Current()) await operation(cts.Token, Current); }
        catch (Exception ex) { if (Current()) ShowError(ex); }
        finally { if (ReferenceEquals(_featureCts, cts)) _featureCts = null; cts.Dispose(); Busy(false); }
    }
    private async Task<bool> RefreshCatalogViewAsync()
    {
        if (_section == "search") { await SearchCatalogViewAsync(false); return true; }
        if (_section == "track-detail" && _openedCatalogItem is { Kind: "track" } track)
        { await LoadCatalogTrackAsync(track, play: false); return true; }
        if (_section == "offline-playlists") { await ShowOfflinePlaylistsAsync(); return true; }
        if (_section == "offline-playlist-detail")
        {
            if (_viewedOfflinePlaylist is { } local)
                await ShowOfflinePlaylist(new(local.Kind, "offline-playlist", CollectionTitle.Text, "", local.Owner, local.Kind));
            return true;
        }
        if (_section == "artist-tracks") { await LoadArtistTracksAsync(remember: false); return true; }
        if (_section is "playlists" or "albums" or "artists") { await LoadCollectionAsync(_section); return true; }
        if (_activePlaylist is not null)
        {
            var current = _activePlaylist;
            await CatalogReadAsync(async (session, token, isCurrent) =>
            {
                var readRevision = _playlistMutationRevision;
                var details = await _api.GetPlaylistAsync(session, current.Owner, current.Kind, token);
                if (isCurrent()) ApplyPlaylistRead(details, readRevision);
            });
            return true;
        }
        if (_section.EndsWith("-detail", StringComparison.Ordinal) && _openedCatalogItem is not null)
        {
            await OpenCatalogAsync(_openedCatalogItem, remember: false); return true;
        }
        return false;
    }
    private void Track_RightClick(object sender, MouseButtonEventArgs e)
    {
        if (ItemsControl.ContainerFromElement(TracksList, e.OriginalSource as DependencyObject) is ListViewItem item) item.IsSelected = true;
    }
    private static LikedId TrackReference(TrackRow row)
    {
        var parts = row.RequestId.Split(':', 2);
        return new(parts[0], parts.Length == 2 ? parts[1] : null);
    }
    private async void LikeTrack_Click(object sender, RoutedEventArgs e) => await SetLikeAsync(true);
    private async void UnlikeTrack_Click(object sender, RoutedEventArgs e) => await SetLikeAsync(false);
    private async Task SetLikeAsync(bool liked)
    {
        if (TracksList.SelectedItem is TrackRow row) await SetTrackLikeAsync(row, liked);
    }
    private async void PlayerLike_Click(object sender, RoutedEventArgs e)
        => await ToggleCurrentLikeAsync();
    private async Task ToggleCurrentLikeAsync()
    {
        if (_current is not { } row || !_favoriteIdsKnown || !PlayerLikeButton.IsEnabled) return;
        await SetTrackLikeAsync(row, !_favoriteTrackIds.Contains(row.Id));
    }
    private void UpdatePlayerLikeState()
    {
        if (PlayerLikeButton is null) return;
        ApplyTrackLikeStates(KnownTrackRows());
        var liked = _current is { } row && _favoriteTrackIds.Contains(row.Id);
        PlayerLikeButton.Content = liked ? "\uEB52" : "\uEB51";
        PlayerLikeButton.Foreground = liked ? (System.Windows.Media.Brush)FindResource("AccentTextBrush") : (System.Windows.Media.Brush)FindResource("MutedBrush");
        PlayerLikeButton.IsEnabled = _current is not null && _session is not null && _favoriteIdsKnown && !_catalogWriting;
        var label = liked ? "Убрать из Мне нравится" : "Добавить в Мне нравится";
        PlayerLikeButton.ToolTip = _session is not null && !_favoriteIdsKnown ? "Загружаем отметки Мне нравится…" : label;
        System.Windows.Automation.AutomationProperties.SetName(PlayerLikeButton, label);
        RefreshDownloadUnlikeAction();
        UpdateShellState();
    }
    private async Task SetTrackLikeAsync(TrackRow row, bool liked)
    {
        if (_account is null || _demo || row.Id.Length == 0) return;
        var uid = _account.Uid;
        await CatalogMutationAsync((session, token) => _api.SetTrackLikeAsync(session, uid, TrackReference(row), liked, token),
            result => ApplyConfirmedTrackLikeResult(row, liked, result, showStatus: true), requireSameView: false);
    }
    private void ApplyConfirmedTrackLike(TrackRow row, bool liked, bool showStatus = true)
        => ApplyConfirmedTrackLikeResult(row, liked, null, showStatus);

    private bool ApplyConfirmedTrackLikeResult(TrackRow row, bool liked, IReadOnlyList<LikedId>? readback,
        bool showStatus, bool refreshIncomplete = true)
    {
        _favoritesLikeRevision++;
        if (readback is not null)
        {
            _favoritesAccountUid ??= _account?.Uid;
            // A refresh already in flight can contain an older order and other
            // devices' old likes as well as the track this action changes.
            _libraryCts?.Cancel();
        }
        if (liked) _favoriteTrackIds.Add(row.Id);
        else
        {
            _pendingConfirmedFavorites.Remove(row.Id);
            var wasLiked = _favoriteTrackIds.Remove(row.Id);
            _libraryRemovedLikes?.Add(row.Id);
            var removed = _favoritesCache.RemoveAll(t => t.Id == row.Id);
            _diagnostics.TotalTracks = Math.Max(0, _diagnostics.TotalTracks - (wasLiked ? Math.Max(1, removed) : removed));
            _diagnostics.LoadedTracks = _favoritesCache.Count;
            if (_pendingAutoFavorites is { } pending)
            { var remaining = pending.Tracks.Where(t => t.Id != row.Id).ToArray(); _pendingAutoFavorites = remaining.Length == 0 ? null : pending with { Tracks = remaining }; }
            if (_section == "favorites")
            { for (var i = _tracks.Count - 1; i >= 0; i--) if (_tracks[i].Id == row.Id) _tracks.RemoveAt(i); UpdateCount(); }
        }
        var complete = false;
        if (readback is not null)
        {
            ApplyVerifiedFavoriteIds(readback);
            var online = liked && row.Available && row.HasTrackMetadata && row.LocalPath is null && row.LocalQuality is null;
            complete = TryApplyConfirmedFavoriteSnapshot(online ? row : null, readback);
            if (complete)
            {
                if (_pendingAutoFavorites is not null) _pendingAutoFavorites = FavoritesDownloadSnapshot();
                PersistFavoritesCache();
                if (_section == "favorites") UpdateCount();
            }
            else _pendingAutoFavorites = null;
        }
        else
        {
            _favoriteIdsKnown = true;
            UpdatePlayerLikeState();
        }
        if (_account?.Uid == _downloadQueueAccountUid && _accountGeneration == _downloadQueueAccountGeneration)
        { _downloadQueue.SetCollectionLike(row.Id, liked); RefreshDownloadCenter(); }
        if (showStatus) SetStatus(liked ? "Трек добавлен в избранное" : "Трек убран из избранного");
        if (liked || readback is not null && !complete && refreshIncomplete) _ = RefreshLibraryAsync();
        if (liked) ScheduleConfirmedFavoriteDownload(row);
        return complete;
    }
    private async void CreatePlaylist_Click(object sender, RoutedEventArgs e)
    {
        if (_session is null || _account is not { } account || _catalogWriting) return;
        var generation = _accountGeneration;
        var view = _viewGeneration;
        var dialog = new TextDialog(this, "Новый плейлист", privacy: true);
        if (dialog.ShowDialog() != true || generation != _accountGeneration || view != _viewGeneration || account.Uid != _account?.Uid) return;
        await CreatePlaylistFromCatalogAsync(dialog.Value, dialog.VisibilityValue);
    }
    private Task CreatePlaylistFromCatalogAsync(string title, string visibility)
        => CatalogMutationAsync((session, token) => _api.CreatePlaylistAsync(session, title, visibility, token), ShowCreatedPlaylist,
            reconcile: playlist => ApplyConfirmedPlaylistChange(playlist, created: true));

    private void ShowCreatedPlaylist(PlaylistDetails playlist)
    {
        if (_playlistCatalogState.TryGetValue((playlist.Owner, playlist.Kind), out var known) && known.Header.Revision > playlist.Revision)
        {
            if (FindPlaylistSnapshot(known.Header) is not { } latest) return;
            playlist = latest;
        }
        if (_section == "playlists")
        {
            PushBrowse(_browseHistory, CaptureBrowseView());
            _browseForward.Clear();
        }
        _openedCatalogItem = PlaylistCatalogItem(playlist);
        ShowPlaylist(playlist);
    }
    private async void RenamePlaylist_Click(object sender, RoutedEventArgs e)
    {
        if (_activePlaylist is not { } playlist || _catalogWriting) return;
        var dialog = new TextDialog(this, "Название плейлиста", playlist.Title);
        if (dialog.ShowDialog() != true) return;
        await RenamePlaylistFromCatalogAsync(playlist, dialog.Value);
    }
    private Task RenamePlaylistFromCatalogAsync(PlaylistDetails playlist, string title)
        => CatalogMutationAsync((session, token) => _api.RenamePlaylistAsync(session, playlist, title, token), null,
            reconcile: current => ApplyConfirmedPlaylistChange(current, created: false, titleConfirmed: true));
    private async void DeletePlaylist_Click(object sender, RoutedEventArgs e)
    {
        if (_activePlaylist is not { } playlist || _catalogWriting) return;
        if (MessageBox.Show(this, $"Удалить плейлист «{playlist.Title}» из аккаунта?", "Удаление плейлиста", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        await DeletePlaylistFromCatalogAsync(playlist);
    }
    private Task DeletePlaylistFromCatalogAsync(PlaylistDetails playlist)
        => CatalogMutationAsync(async (session, token) => { await _api.DeletePlaylistAsync(session, playlist, token); return playlist; },
            null, reconcile: ApplyConfirmedPlaylistDeletion);

    private async void RemovePlaylistTrack_Click(object sender, RoutedEventArgs e)
    {
        if (_activePlaylist is not { } playlist || TracksList.SelectedItem is not TrackRow row || _catalogWriting) return;
        var index = Array.FindIndex(playlist.Tracks.ToArray(), t => ReferenceEquals(t, row));
        if (index < 0) return;
        var removal = new PlaylistTrackRemoval(playlist, row);
        await CatalogMutationAsync((session, token) => _api.RemovePlaylistTrackAsync(session, playlist, index, row.RequestId, token), null,
            reconcile: details => ApplyConfirmedPlaylistChange(details, created: false, removal: removal));
    }
    private async void AddToPlaylist_Click(object sender, RoutedEventArgs e)
    {
        if (TracksList.SelectedItem is TrackRow row) await AddTrackToPlaylistAsync(row);
    }
    private async Task AddTrackToPlaylistAsync(TrackRow row)
    {
        if (_closed || _account is not { } account || row.LocalPath is not null) return;
        if (_catalogWriting) { SetStatus(CatalogWriteBusyMessage, true); return; }
        if (_playlistChooserRequest is not null) { SetStatus("Выбор плейлиста уже выполняется.", true); return; }
        var reference = TrackReference(row);
        if (reference.AlbumId is null) { SetStatus("У трека не указан albumId, добавление в плейлист недоступно.", true); return; }
        var generation = _accountGeneration;
        var view = _viewGeneration;
        var token = _accountCts.Token;
        var request = new object();
        _playlistChooserRequest = request;
        bool Current() => !_closed && !token.IsCancellationRequested && generation == _accountGeneration
            && _account?.Uid == account.Uid && view == _viewGeneration && ReferenceEquals(_playlistChooserRequest, request);
        try
        {
            var session = await EnsureSessionAsync(token);
            if (!Current()) return;
            var playlists = await _api.GetPlaylistsAsync(session, account.Uid, token);
            if (!Current()) return;
            if (_catalogWriting) { SetStatus(CatalogWriteBusyMessage, true); return; }
            var dialog = new PlaylistDialog(this, CurrentCatalogItems(playlists, includeCreatedPlaylists: true));
            if (dialog.ShowDialog() != true || dialog.Selected is not { } selected) return;
            if (!Current()) return;
            await InsertTrackIntoPlaylistAsync(selected, reference);
        }
        catch (Exception ex) { if (Current()) ShowError(ex); }
        finally { if (ReferenceEquals(_playlistChooserRequest, request)) _playlistChooserRequest = null; }
    }
    private Task InsertTrackIntoPlaylistAsync(CatalogItem selected, LikedId track)
    {
        var generation = _accountGeneration;
        var view = _viewGeneration;
        var uid = _account?.Uid;
        bool Current() => !_closed && generation == _accountGeneration && view == _viewGeneration && uid == _account?.Uid;
        return CatalogMutationAsync(async (session, token) =>
        {
            if (selected.Owner is null || selected.Owner != uid || CurrentPlaylistCatalogItem(selected) is null)
                throw new MusicException("Выбранный плейлист больше недоступен для изменения.");
            var kind = selected.PlaylistKind ?? selected.Id;
            var playlist = await _api.GetPlaylistAsync(session, selected.Owner, kind, token);
            if (!Current()) throw new OperationCanceledException(token);
            session = await EnsureSessionAsync(token);
            token.ThrowIfCancellationRequested();
            if (!Current()) throw new OperationCanceledException(token);
            return await _api.InsertPlaylistTracksAsync(session, playlist, [track], playlist.Tracks.Count, token);
        }, _ => SetStatus("Трек добавлен в выбранный плейлист"), reconcile: ApplyConfirmedPlaylistChange, reportBusy: true);
    }

    private bool IsShowingPlaylist(PlaylistDetails playlist) => _section == "playlist-detail"
        && (_activePlaylist is { } active ? SamePlaylist(active, playlist) : MatchesPlaylistItem(_openedCatalogItem, playlist));

    private void ApplyConfirmedPlaylistChange(PlaylistDetails playlist) => ApplyConfirmedPlaylistChange(playlist, created: false);

    private void ApplyConfirmedPlaylistChange(PlaylistDetails playlist, bool created, bool titleConfirmed = false, PlaylistTrackRemoval? removal = null)
    {
        EnsurePlaylistCatalogAccount();
        var identity = (playlist.Owner, playlist.Kind);
        var known = _playlistCatalogState.GetValueOrDefault(identity);
        playlist = PresentPlaylistAuthor(playlist);
        var header = known is { Deleted: false } && known.Header.Revision > playlist.Revision ? known.Header : playlist with { Tracks = [] };
        _playlistCatalogState[identity] = new(header, ++_playlistMutationRevision, created || known?.AwaitingCollection == true,
            TitleConfirmed: created || titleConfirmed || known?.TitleConfirmed == true);
        if (header.Revision > playlist.Revision)
        {
            if (FindPlaylistSnapshot(header) is not { } latest) return;
            playlist = latest;
        }
        UpdateBrowsePlaylist(playlist, deleted: false, created: created, removal: removal);
        if (created && _catalogShowing && _section == "playlists" && !_catalogItems.Any(item => MatchesPlaylistItem(item, playlist)))
        {
            _catalogItems.Add(PlaylistCatalogItem(playlist));
            UpdateCount();
        }
        if (IsShowingPlaylist(playlist) && (_activePlaylist is null || playlist.Revision >= _activePlaylist.Revision))
            ShowPlaylistWithSelection(playlist, removal);
    }

    private void ApplyConfirmedPlaylistDeletion(PlaylistDetails playlist)
    {
        EnsurePlaylistCatalogAccount();
        _playlistCatalogState[(playlist.Owner, playlist.Kind)] = new(playlist with { Tracks = [] }, ++_playlistMutationRevision, Deleted: true);
        UpdateBrowsePlaylist(playlist, deleted: true);
        if (IsShowingPlaylist(playlist)) _ = LoadCollectionCoreAsync("playlists", preserveSettings: true);
    }

    private const string CatalogWriteBusyMessage = "Дождитесь завершения другого изменения коллекции.";

    private async Task CatalogMutationAsync<T>(Func<Session, CancellationToken, Task<T>> operation, Action<T>? success,
        bool requireSameView = true, Action<T>? reconcile = null, bool reportBusy = false)
    {
        if (_closed || _session is null) return;
        if (_catalogWriting)
        {
            if (reportBusy) SetStatus(CatalogWriteBusyMessage, true);
            return;
        }
        _catalogWriting = true; UpdatePlayerLikeState(); UpdateArtistLike(); Busy(true);
        var generation = _accountGeneration; var viewGeneration = _viewGeneration;
        var uid = _account?.Uid;
        var token = _accountCts.Token;
        bool CurrentAccount() => !_closed && !token.IsCancellationRequested && generation == _accountGeneration && uid == _account?.Uid;
        try
        {
            var session = await EnsureSessionAsync(token);
            if (!CurrentAccount() || requireSameView && viewGeneration != _viewGeneration) return;
            var result = await operation(session, token);
            if (!CurrentAccount()) return;
            var sameView = viewGeneration == _viewGeneration;
            reconcile?.Invoke(result);
            if (requireSameView && !sameView) { SetStatus("Изменение сохранено в аккаунте."); return; }
            success?.Invoke(result);
        }
        catch (OperationCanceledException) when (!CurrentAccount() || requireSameView && viewGeneration != _viewGeneration) { }
        catch (Exception ex)
        {
            if (CurrentAccount() && (!requireSameView || reconcile is not null || viewGeneration == _viewGeneration)) ShowError(ex);
        }
        finally
        {
            _catalogWriting = false;
            if (!_closed) { UpdatePlayerLikeState(); UpdateArtistLike(); }
            Busy(false);
        }
    }
    private void UpdateCatalogCount()
    {
        if (!_catalogShowing) return;
        CountText.Text = $"{_catalogItems.Count:N0} результатов";
        EmptyState.Visibility = _catalogItems.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
        var searchCompleted = _section == "search" && _searchText.Length > 0;
        EmptyTitle.Text = searchCompleted ? "Ничего не найдено" : "Пока нет результатов";
        EmptyDescription.Text = _section != "search" ? "Дождитесь загрузки или обновите раздел"
            : searchCompleted ? "Попробуйте изменить запрос" : "Введите запрос для поиска в каталоге Яндекса";
    }
}
