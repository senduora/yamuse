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
    private int _searchPage;
    private string _searchText = "";
    private string _searchType = "all";
    private PlaylistDetails? _activePlaylist;
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
        var snapshot = items.Select(PresentCatalogAuthor).ToArray();
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
        if (query.Length == 0) { SetStatus("Введите название трека, альбома или исполнителя."); return; }
        var type = _collectionsSearch ? "collections" : (CatalogType.SelectedItem as ComboBoxItem)?.Tag as string ?? "all";
        var page = more && query == _searchText && type == _searchType ? _searchPage + 1 : 0;
        SetSection("search", "Поиск"); CatalogSearchPanel.Visibility = Visibility.Visible;
        await CatalogReadAsync(async (session, token) =>
        {
            var result = type == "collections" ? await _api.SearchCollectionsAsync(session, query, page, token)
                : await _api.SearchCatalogAsync(session, query, type, page, token);
            token.ThrowIfCancellationRequested();
            _searchPage = result.Page; _searchText = query; _searchType = type;
            ShowCatalogItems(result.Items, page > 0); MoreCatalogButton.IsEnabled = result.HasMore;
            SetStatus(type == "collections" ? $"Сборники и редакционные плейлисты · {_catalogItems.Count:N0}" : $"Результаты поиска · {_catalogItems.Count:N0}");
        });
    }
    private async Task LoadCollectionAsync(string kind)
    {
        if (_account is null) { SetStatus("Войдите в Яндекс, чтобы открыть коллекцию."); return; }
        var title = kind == "playlists" ? "Плейлисты" : kind == "albums" ? "Альбомы" : "Исполнители";
        SetSection(kind, title);
        await CatalogReadAsync(async (session, token) =>
        {
            var uid = _account?.Uid ?? throw new OperationCanceledException(token);
            var items = kind == "playlists" ? await _api.GetPlaylistsAsync(session, uid, token)
                : kind == "albums" ? await _api.GetLikedAlbumsAsync(session, uid, token) : await _api.GetLikedArtistsAsync(session, uid, token);
            token.ThrowIfCancellationRequested(); ShowCatalogItems(items);
            if (kind == "playlists") { PlaylistActions.Visibility = Visibility.Visible; CreatePlaylistButton.Visibility = Visibility.Visible; }
            SetStatus($"{title} · {items.Count:N0}");
        });
    }
    private async Task OpenSelectedCatalogAsync()
    {
        if (CatalogList.SelectedItem is not CatalogItem item) return;
        if (item.Kind == "offline-playlist") { ShowOfflinePlaylist(item); return; }
        if (_session is null) return;
        await OpenCatalogAsync(item);
    }
    private async Task OpenCatalogAsync(CatalogItem item, bool remember = true)
    {
        if (item.Kind == "artist") { await OpenArtistAsync(item.Id, item.Title, remember); return; }
        if (remember) RememberBrowseView();
        _openedCatalogItem = item; _artistPage = 0;
        NavigateSection(item.Kind + "-detail", item.Title, remember: false);
        await CatalogReadAsync(async (session, token) =>
        {
            if (item.Kind == "playlist")
            {
                var details = await _api.GetPlaylistAsync(session, item.Owner ?? throw new MusicException("У плейлиста не указан владелец."), item.PlaylistKind ?? item.Id, token);
                token.ThrowIfCancellationRequested(); ShowPlaylist(details);
                return;
            }
            if (item.Kind == "album")
            {
                var album = await _api.GetAlbumDetailsAsync(session, item.Id, token);
                token.ThrowIfCancellationRequested(); ShowRows(album.Tracks); ShowCollectionHero(album, item.Title);
                SetStatus($"Загружено {album.Tracks.Count:N0} треков"); return;
            }
            IReadOnlyList<TrackRow> rows = item.Kind switch
            {
                "artist" => await _api.GetArtistTracksAsync(session, item.Id, 0, token),
                "track" => await _api.GetTracksAsync(session, [new LikedId(item.Id, item.AlbumId)], 0, token),
                _ => throw new MusicException("Этот тип результата пока не поддерживается.")
            };
            token.ThrowIfCancellationRequested(); ShowRows(rows);
            if (item.Kind == "artist" && rows.Count == 100) { PlaylistActions.Visibility = Visibility.Visible; MoreArtistButton.Visibility = Visibility.Visible; }
            SetStatus(item.Kind == "artist" ? "Треки исполнителя · первая страница" : $"Загружено {rows.Count:N0} треков");
            if (item.Kind == "track" && rows.Count > 0)
            {
                _playbackQueue.SetItems(rows);
                if (_playbackQueue.Current is { } first) await PlayTrackAsync(first);
            }
        });
    }
    private async void MoreArtist_Click(object sender, RoutedEventArgs e)
    {
        if (_openedCatalogItem is not { Kind: "artist" } artist || _section != "artist-tracks") return;
        var page = _artistPage + 1;
        await CatalogReadAsync(async (session, token) =>
        {
            var rows = await _api.GetArtistTracksAsync(session, artist.Id, page, token);
            token.ThrowIfCancellationRequested();
            foreach (var row in rows) _tracks.Add(row);
            _artistPage = page; MoreArtistButton.Visibility = rows.Count == 100 ? Visibility.Visible : Visibility.Collapsed;
            UpdateCount(); SetStatus($"Треки исполнителя · загружено {_tracks.Count:N0}");
        });
    }
    private void ShowPlaylist(PlaylistDetails details)
    {
        details = PresentPlaylistAuthor(details);
        // A response fills the underlying view while an open Settings draft stays intact.
        ApplySection("playlist-detail", details.Title, closeSettings: false);
        CatalogSearchPanel.Visibility = Visibility.Collapsed;
        CreatePlaylistButton.Visibility = Visibility.Collapsed;
        _activePlaylist = details; CollectionTitle.Text = details.Title;
        ShowRows(details.Tracks);
        ShowCollectionHero(details);
        var writable = _account?.Uid == details.Owner && !details.IsGenerated;
        PlaylistActions.Visibility = Visibility.Visible;
        RenamePlaylistButton.Visibility = DeletePlaylistButton.Visibility = RemovePlaylistTrackButton.Visibility = writable ? Visibility.Visible : Visibility.Collapsed;
        SetStatus($"Плейлист · {details.Tracks.Count:N0} треков" + (writable ? " · ваша подборка" : ""));
        UpdatePlaylistDownloadedStatus();
    }
    private async Task CatalogReadAsync(Func<Session, CancellationToken, Task> operation)
    {
        _featureCts?.Cancel();
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, _accountCts.Token); _featureCts = cts;
        Busy(true); SetStatus("Загрузка…");
        try { var session = await EnsureSessionAsync(cts.Token); await operation(session, cts.Token); }
        catch (OperationCanceledException) when (cts.IsCancellationRequested) { }
        catch (Exception ex) { ShowError(ex); }
        finally { if (ReferenceEquals(_featureCts, cts)) _featureCts = null; cts.Dispose(); Busy(false); }
    }
    private async Task<bool> RefreshCatalogViewAsync()
    {
        if (_section == "search") { await SearchCatalogViewAsync(false); return true; }
        if (_section == "artist-tracks") { await LoadArtistTracksAsync(remember: false); return true; }
        if (_section is "playlists" or "albums" or "artists") { await LoadCollectionAsync(_section); return true; }
        if (_activePlaylist is not null)
        {
            var current = _activePlaylist;
            await CatalogReadAsync(async (session, token) => { var details = await _api.GetPlaylistAsync(session, current.Owner, current.Kind, token); token.ThrowIfCancellationRequested(); ShowPlaylist(details); });
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
        await CatalogMutationAsync(async (session, token) => { await _api.SetTrackLikeAsync(session, uid, TrackReference(row), liked, token); return true; },
            result => ApplyConfirmedTrackLike(row, liked), requireSameView: false);
    }
    private void ApplyConfirmedTrackLike(TrackRow row, bool liked, bool showStatus = true)
    {
        _favoritesLikeRevision++;
        if (liked) _favoriteTrackIds.Add(row.Id);
        else
        {
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
        _favoriteIdsKnown = true; UpdatePlayerLikeState();
        if (_account?.Uid == _downloadQueueAccountUid && _accountGeneration == _downloadQueueAccountGeneration)
        { _downloadQueue.SetCollectionLike(row.Id, liked); RefreshDownloadCenter(); }
        if (showStatus) SetStatus(liked ? "Трек добавлен в избранное" : "Трек убран из избранного");
        PersistFavoritesCache();
        if (liked) _ = RefreshLibraryAsync();
    }
    private async void CreatePlaylist_Click(object sender, RoutedEventArgs e)
    {
        if (_session is null || _catalogWriting) return;
        var dialog = new TextDialog(this, "Новый плейлист", privacy: true);
        if (dialog.ShowDialog() != true) return;
        await CatalogMutationAsync((session, token) => _api.CreatePlaylistAsync(session, dialog.Value, dialog.VisibilityValue, token), ShowPlaylist);
    }
    private async void RenamePlaylist_Click(object sender, RoutedEventArgs e)
    {
        if (_activePlaylist is not { } playlist || _catalogWriting) return;
        var dialog = new TextDialog(this, "Название плейлиста", playlist.Title);
        if (dialog.ShowDialog() != true) return;
        await CatalogMutationAsync((session, token) => _api.RenamePlaylistAsync(session, playlist, dialog.Value, token), ShowPlaylist);
    }
    private async void DeletePlaylist_Click(object sender, RoutedEventArgs e)
    {
        if (_activePlaylist is not { } playlist || _catalogWriting) return;
        if (MessageBox.Show(this, $"Удалить плейлист «{playlist.Title}» из аккаунта?", "Удаление плейлиста", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        await CatalogMutationAsync(async (session, token) => { await _api.DeletePlaylistAsync(session, playlist, token); return true; },
            result => { _activePlaylist = null; _ = LoadCollectionAsync("playlists"); });
    }
    private async void RemovePlaylistTrack_Click(object sender, RoutedEventArgs e)
    {
        if (_activePlaylist is not { } playlist || TracksList.SelectedItem is not TrackRow row || _catalogWriting) return;
        var index = Array.FindIndex(playlist.Tracks.ToArray(), t => ReferenceEquals(t, row));
        if (index < 0) return;
        await CatalogMutationAsync((session, token) => _api.RemovePlaylistTrackAsync(session, playlist, index, row.RequestId, token), ShowPlaylist);
    }
    private async void AddToPlaylist_Click(object sender, RoutedEventArgs e)
    {
        if (TracksList.SelectedItem is TrackRow row) await AddTrackToPlaylistAsync(row);
    }
    private async Task AddTrackToPlaylistAsync(TrackRow row)
    {
        if (_closed || _account is not { } account || _catalogWriting || row.LocalPath is not null) return;
        var reference = TrackReference(row);
        if (reference.AlbumId is null) { SetStatus("У трека не указан albumId, добавление в плейлист недоступно.", true); return; }
        var generation = _accountGeneration;
        try
        {
            var token = _accountCts.Token;
            var session = await EnsureSessionAsync(token);
            token.ThrowIfCancellationRequested();
            if (generation != _accountGeneration || _closed || _account?.Uid != account.Uid) return;
            var playlists = await _api.GetPlaylistsAsync(session, account.Uid, token);
            if (generation != _accountGeneration || _closed) return;
            var dialog = new PlaylistDialog(this, playlists);
            if (dialog.ShowDialog() != true || dialog.Selected is not { } selected) return;
            if (generation != _accountGeneration || _closed) return;
            var details = await _api.GetPlaylistAsync(session, selected.Owner!, selected.PlaylistKind!, token);
            if (generation != _accountGeneration || _closed) return;
            await CatalogMutationAsync((validSession, token) => _api.InsertPlaylistTracksAsync(validSession, details, [reference], details.Tracks.Count, token),
                _ => SetStatus("Трек добавлен в выбранный плейлист"));
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (generation == _accountGeneration && !_closed) ShowError(ex); }
    }
    private async Task CatalogMutationAsync<T>(Func<Session, CancellationToken, Task<T>> operation, Action<T> success, bool requireSameView = true)
    {
        if (_catalogWriting || _session is null) return;
        _catalogWriting = true; UpdatePlayerLikeState(); Busy(true); var generation = _accountGeneration; var viewGeneration = _viewGeneration;
        var token = _accountCts.Token;
        try
        {
            var session = await EnsureSessionAsync(token);
            token.ThrowIfCancellationRequested();
            if (_closed || generation != _accountGeneration) return;
            var result = await operation(session, token);
            if (_closed || generation != _accountGeneration) return;
            if (requireSameView && viewGeneration != _viewGeneration) { SetStatus("Изменение сохранено в аккаунте."); return; }
            success(result);
        }
        catch (Exception ex) { if (generation == _accountGeneration) ShowError(ex); }
        finally { _catalogWriting = false; UpdatePlayerLikeState(); Busy(false); }
    }
    private void UpdateCatalogCount()
    {
        if (!_catalogShowing) return;
        CountText.Text = $"{_catalogItems.Count:N0} результатов";
        EmptyState.Visibility = _catalogItems.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
        EmptyTitle.Text = "Пока нет результатов";
        EmptyDescription.Text = _section == "search" ? "Введите запрос для поиска в каталоге Яндекса" : "Дождитесь загрузки или обновите раздел";
    }
}
