using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace YMusic.Native;

public partial class MainWindow
{
    private readonly ObservableCollection<CatalogItem> _catalogItems = new();
    private bool _catalogShowing;
    private bool _catalogWriting;
    private CancellationTokenSource? _playlistChooserRequest;
    private int _searchPage;
    private string _searchText = "";
    private string _searchType = "all";
    private PlaylistDetails? _activePlaylist;
    private readonly Dictionary<TrackRow, PlaylistSelection> _playlistSelections = new(ReferenceEqualityComparer.Instance);
    private int _playlistSelectionOrdinal;
    private bool _applyingPlaylistSelection;
    private bool _playlistSelectionReconciled;
    private long? _playlistSelectionPlaybackToken;
    private EarlyPlaylistSelection? _earlyPlaylistSelection;
    private PlaylistTrackEdit? _pendingPlaylistTrackEdit;
    private DeferredPlaylistSelection[] _deferredPlaylistSelections = [];
    private sealed record PlaylistCatalogState(PlaylistDetails Header, long Mutation = 0,
        bool AwaitingCollection = false, bool Deleted = false, bool TitleConfirmed = false, bool HasDetails = false);
    private sealed record PlaylistTrackEdit(PlaylistDetails Before, int? Index = null, LikedId? Inserted = null, int? ConfirmedRevision = null)
    {
        internal object SelectionKey { get; } = new();

        internal int MapIndex(int previousIndex)
        {
            if (Index is not int index || previousIndex < index) return previousIndex;
            if (Inserted is not null) return previousIndex + 1;
            return previousIndex == index ? -1 : previousIndex - 1;
        }
    }
    private sealed record PlaylistSelection(TrackRow Row, int OriginRevision, int Ordinal);
    private sealed record DeferredPlaylistSelection(object EditKey, int SourceIndex, int OriginRevision, int Ordinal, int SuccessorRevision, long? PlaybackToken);
    private sealed record EarlyPlaylistSelection(object EditKey, int Revision, long? PlaybackToken);
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
        var source = e.OriginalSource as DependencyObject;
        if (e.ChangedButton != MouseButton.Left || source is null || !_catalogShowing || FromButton(source)
            || ItemsControl.ContainerFromElement(CatalogList, source) is not ListBoxItem row) return;
        var index = CatalogList.ItemContainerGenerator.IndexFromContainer(row);
        if (index < 0 || index >= CatalogList.Items.Count || CatalogList.Items[index] is not CatalogItem item) return;
        e.Handled = true;
        await OpenCatalogAsync(item);
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
        _shownQueue = null;
        _catalogShowing = false;
        CatalogList.Visibility = Visibility.Collapsed; TracksList.Visibility = Visibility.Visible;
        CatalogSearchPanel.Visibility = Visibility.Collapsed; PlaylistActions.Visibility = Visibility.Collapsed;
        CreatePlaylistButton.Visibility = RenamePlaylistButton.Visibility = DeletePlaylistButton.Visibility = RemovePlaylistTrackButton.Visibility = Visibility.Collapsed;
        MoreArtistButton.Visibility = Visibility.Collapsed;
        PlaySelectionButton.Content = "▶  Слушать"; _activePlaylist = null;
        _playlistSelections.Clear(); _playlistSelectionOrdinal = 0;
        UsePlaybackContextSelection();
    }
    private void CollectionOptions_Click(object sender, RoutedEventArgs e)
        => CollectionOptionsPopup.IsOpen = !CollectionOptionsPopup.IsOpen;
    private async Task SearchCatalogViewAsync(bool more)
    {
        if (_session is null) { SetStatus("Войдите в Яндекс для поиска по каталогу."); return; }
        if (more)
        {
            if (_section != "search" || _searchText.Length == 0 || !MoreCatalogButton.IsEnabled
                || _featureCts is { IsCancellationRequested: false }) return;
            AdvanceViewGeneration();
            await ReadSearchPageAsync(_searchText, _searchType, _searchPage + 1);
            return;
        }
        var query = CatalogQuery.Text.Trim();
        if (query.Length == 0)
        {
            if (_section == "search")
            {
                AdvanceViewGeneration();
                _featureCts?.Cancel();
                _initialCatalogRead = null;
                ResetSearchResults();
                ShowCatalogItems([]);
            }
            SetStatus("Введите название трека, альбома или исполнителя.");
            return;
        }
        var type = _collectionsSearch ? "collections" : (CatalogType.SelectedItem as ComboBoxItem)?.Tag as string ?? "all";
        if (_section != "search") SetSection("search", "Поиск");
        else AdvanceViewGeneration();
        CatalogSearchPanel.Visibility = Visibility.Visible;
        ResetSearchResults();
        ShowCatalogItems([]);
        await ReadSearchPageAsync(query, type, 0);
    }

    private sealed record InitialCatalogRead(string Section, string? Query = null, string? SearchType = null);
    private InitialCatalogRead? _initialCatalogRead;

    private Task ReadSearchPageAsync(string query, string type, int page)
    {
        if (page == 0) _initialCatalogRead = new("search", query, type);
        return CatalogReadAsync(async (session, token, isCurrent) =>
        {
            var result = type == "collections" ? await _api.SearchCollectionsAsync(session, query, page, token)
                : await _api.SearchCatalogAsync(session, query, type, page, token);
            if (!isCurrent()) return;
            _searchPage = result.Page; _searchText = query; _searchType = type;
            ShowCatalogItems(result.Items, page > 0); MoreCatalogButton.IsEnabled = result.HasMore;
            _initialCatalogRead = null;
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
        await ReadCollectionItemsAsync(kind, title);
    }

    private Task ReadCollectionItemsAsync(string kind, string title)
    {
        _initialCatalogRead = new(kind);
        return CatalogReadAsync(async (session, token, isCurrent) =>
        {
            var uid = _account?.Uid ?? throw new OperationCanceledException(token);
            if (kind == "artists")
            {
                await ReadLikedArtistsCollectionAsync(session, uid, token, isCurrent, ShowCollectionItems);
                return;
            }
            var readMutation = _playlistMutationRevision;
            var items = kind == "playlists" ? await _api.GetPlaylistsAsync(session, uid, token)
                : await _api.GetLikedAlbumsAsync(session, uid, token);
            if (!isCurrent()) return;
            if (kind == "playlists") ObservePlaylistCollection(items, readMutation);
            ShowCollectionItems(items);
            if (kind == "playlists") ShowPlaylistCatalogActions();
        });

        void ShowCollectionItems(IReadOnlyList<CatalogItem> items)
        {
            ShowCatalogItems(items);
            _initialCatalogRead = null;
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
            CoverUri: playlist.CoverUri, OwnerName: playlist.OwnerName) { PlaylistRevision = playlist.Revision };

    private static PlaylistDetails PlaylistCatalogHeader(PlaylistDetails playlist)
        => playlist with { Tracks = [], InheritedMetadataPositions = null, MetadataIssue = null, EditPositionsVerified = false };

    private void ObservePlaylistSelection(SelectionChangedEventArgs change)
    {
        if (_updatingTrackRows || _restoringBrowse || !ReferenceEquals(change.OriginalSource, TracksList) || _activePlaylist is not { } playlist) return;
        _deferredPlaylistSelections = [];
        ProtectManualSourceSelection();
        foreach (var row in change.RemovedItems.OfType<TrackRow>()) _playlistSelections.Remove(row);
        foreach (var row in change.AddedItems.OfType<TrackRow>()) _playlistSelections[row] = new(row, playlist.Revision, ++_playlistSelectionOrdinal);
    }

    private PlaylistSelection[] CapturePlaylistSelection(PlaylistDetails playlist)
    {
        return TracksList.SelectedItems.OfType<TrackRow>().Select(row =>
        {
            if (!_playlistSelections.TryGetValue(row, out var choice))
                _playlistSelections[row] = choice = new(row, playlist.Revision, ++_playlistSelectionOrdinal);
            return choice;
        }).OrderBy(choice => choice.Ordinal).ToArray();
    }

    private void RestorePlaylistSelection(PlaylistSelection[] selection, DeferredPlaylistSelection[] deferred)
    {
        _applyingPlaylistSelection = true;
        try
        {
            TracksList.UnselectAll();
            _playlistSelections.Clear();
            if (selection.Length > 0)
            {
                var visible = new HashSet<TrackRow>(TracksList.Items.OfType<TrackRow>(), ReferenceEqualityComparer.Instance);
                foreach (var choice in selection)
                {
                    if (!visible.Contains(choice.Row)) continue;
                    TracksList.SelectedItems.Add(choice.Row);
                    _playlistSelections[choice.Row] = choice;
                    _playlistSelectionOrdinal = Math.Max(_playlistSelectionOrdinal, choice.Ordinal);
                }
            }
            _deferredPlaylistSelections = deferred;
            foreach (var choice in deferred) _playlistSelectionOrdinal = Math.Max(_playlistSelectionOrdinal, choice.Ordinal);
        }
        finally { _applyingPlaylistSelection = false; }
        UpdateControls();
    }

    private CatalogItem? CurrentPlaylistCatalogItem(CatalogItem item)
    {
        EnsurePlaylistCatalogAccount();
        // Search and old list cards may carry no revision. Reconcile them with the last verified header.
        if (item is not { Kind: "playlist", Owner: { } owner }
            || !_playlistCatalogState.TryGetValue((owner, item.PlaylistKind ?? item.Id), out var state)
            || state.Mutation == 0 && !state.TitleConfirmed)
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

    private void ObservePlaylistCollection(IReadOnlyList<CatalogItem> items, long readMutation)
    {
        EnsurePlaylistCatalogAccount();
        foreach (var item in items)
        {
            if (item.Owner is not { } owner || owner != _account?.Uid) continue;
            var identity = (owner, item.PlaylistKind ?? item.Id);
            var state = _playlistCatalogState.GetValueOrDefault(identity);
            if (state is null)
            {
                if (item.PlaylistRevision is int firstRevision)
                    _playlistCatalogState[identity] = new(new(owner, identity.Item2, item.Title, item.Subtitle, firstRevision, [],
                        CoverUri: item.CoverUri, OwnerName: item.OwnerName), TitleConfirmed: true);
                continue;
            }
            if (state.Deleted) continue;
            // The response itself can prove a later revision even if the read began before a local write.
            if (item.PlaylistRevision is int revision && revision > state.Header.Revision)
            {
                var header = state.Header with { Title = item.Title, Description = item.Subtitle, Revision = revision,
                    CoverUri = item.CoverUri ?? state.Header.CoverUri, OwnerName = item.OwnerName ?? state.Header.OwnerName };
                state = state with { Header = header, TitleConfirmed = true, HasDetails = false, AwaitingCollection = false };
            }
            else if (readMutation >= state.Mutation) state = state with { AwaitingCollection = false };
            _playlistCatalogState[identity] = state;
        }
        if (_activePlaylist is { } active) UpdatePlaylistWriteAvailability(active);
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
        await ReadCatalogDetailAsync(item, playTrack: true);
    }

    private Task ReadCatalogDetailAsync(CatalogItem item, bool playTrack = false, Action? afterPlaylistRead = null)
        => item.Kind == "track" ? LoadCatalogTrackAsync(item, playTrack) : CatalogReadAsync(async (session, token, isCurrent) =>
        {
            if (item.Kind == "playlist")
            {
                var readRevision = _playlistMutationRevision;
                var details = await _api.GetPlaylistAsync(session, item.Owner ?? throw new MusicException("У плейлиста не указан владелец."), item.PlaylistKind ?? item.Id, token);
                if (isCurrent() && ApplyPlaylistRead(details, readRevision) && isCurrent()) afterPlaylistRead?.Invoke();
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
    private bool ApplyPlaylistRead(PlaylistDetails details, long readRevision)
    {
        EnsurePlaylistCatalogAccount();
        var identity = (details.Owner, details.Kind);
        var known = _playlistCatalogState.GetValueOrDefault(identity);
        if (known?.Deleted == true)
        {
            SetStatus("Плейлист удалён.");
            return false;
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
            return false;
        }
        details = PresentPlaylistAuthor(MergeKnownPlaylistRows(details));
        if (_account is not null)
        {
            var header = PlaylistCatalogHeader(details);
            _playlistCatalogState[identity] = known is null ? new(header, HasDetails: true) : known with { Header = header, HasDetails = true };
            var sameDetails = known is { HasDetails: true } && details.Revision == known.Header.Revision && details.Title == known.Header.Title;
            UpdateBrowsePlaylist(details, deleted: false, exactRead: sameDetails);
        }
        ShowPlaylist(details);
        return true;
    }

    private void ShowPlaylist(PlaylistDetails details) => ShowPlaylistWithSelection(details, edit: null);

    private void ShowPlaylistWithSelection(PlaylistDetails details, PlaylistTrackEdit? edit)
    {
        details = PresentPlaylistAuthor(details);
        PlaylistSelection[] selected = [];
        DeferredPlaylistSelection[] deferred = [];
        var restoreSelection = false;
        // A response fills the underlying view while an open Settings draft stays intact.
        if (IsShowingPlaylist(details))
        {
            var current = _activePlaylist;
            if (current is not null && details.Revision < current.Revision)
            {
                SetStatus("Сервис вернул более старую ревизию плейлиста.", true);
                return;
            }
            // The same view can have a detail read in flight while a write is published.
            // Let the read reconcile with the revision floor; navigation owns cancellation.
            if (current is not null && edit is null && _pendingPlaylistTrackEdit is { Index: not null, Inserted: null } pending
                && details.Revision > current.Revision && SamePlaylistOccurrences(current, pending.Before) && MatchesPlaylistEditOrder(pending, details))
                _earlyPlaylistSelection = new(pending.SelectionKey, details.Revision, _playbackQueue.CurrentOccurrenceToken);
            var early = _earlyPlaylistSelection;
            var confirmsEarly = ConfirmsEarlyPlaylistSelection(early, edit, details);
            if (current is not null && (_playlistSelectionReconciled || early?.Revision == details.Revision
                || TracksList.SelectedItems.Count > 0 || _deferredPlaylistSelections.Length > 0))
            {
                restoreSelection = true;
                var resolved = ResolvePlaylistSelection(current, details, CapturePlaylistSelection(current), _deferredPlaylistSelections,
                    edit, _playbackQueue.CurrentOccurrenceToken);
                (selected, deferred) = (resolved.Selected, resolved.Deferred);
                if (confirmsEarly) ProtectReconciledPlaylistSelection(early!.PlaybackToken);
                else if (edit is { Index: not null, Inserted: null } && resolved.UsedConfirmedPositions)
                    ProtectReconciledPlaylistSelection(_playbackQueue.CurrentOccurrenceToken);
            }
        }
        else ApplySection("playlist-detail", details.Title, closeSettings: false);
        CatalogSearchPanel.Visibility = Visibility.Collapsed;
        CreatePlaylistButton.Visibility = Visibility.Collapsed;
        _activePlaylist = details; CollectionTitle.Text = details.Title;
        ShowRows(details.Tracks);
        if (restoreSelection) RestorePlaylistSelection(selected, deferred);
        ShowCollectionHero(details);
        var writable = _account?.Uid == details.Owner && !details.IsGenerated;
        PlaylistActions.Visibility = Visibility.Visible;
        RenamePlaylistButton.Visibility = DeletePlaylistButton.Visibility = RemovePlaylistTrackButton.Visibility = writable ? Visibility.Visible : Visibility.Collapsed;
        UpdatePlaylistWriteAvailability(details);
        if (!HasPendingOfflineNavigation)
            SetStatus($"Плейлист · {details.Tracks.Count:N0} треков" + (writable ? " · ваша подборка" : ""));
        UpdatePlaylistDownloadedStatus();
    }
    private Task CatalogReadAsync(Func<Session, CancellationToken, Func<bool>, Task> operation)
        => ReadFeatureViewAsync(async (token, isCurrent) =>
        {
            var session = await EnsureSessionAsync(token);
            if (isCurrent()) await operation(session, token, isCurrent);
        });

    private CancellationTokenSource? _offlineNavigation;
    private bool HasPendingOfflineNavigation => _offlineNavigation is { IsCancellationRequested: false } request
        && ReferenceEquals(_featureCts, request);

    private async Task ReadFeatureViewAsync(Func<CancellationToken, Func<bool>, Task> operation, bool offlineNavigation = false)
    {
        _featureCts?.Cancel();
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, _accountCts.Token);
        _featureCts = cts;
        if (offlineNavigation)
        {
            _offlineNavigation = cts;
            AdvanceViewGeneration();
        }
        var account = _accountGeneration;
        var uid = _account?.Uid;
        var view = _viewGeneration;
        bool Current() => !_closed && !cts.IsCancellationRequested && ReferenceEquals(_featureCts, cts)
            && account == _accountGeneration && uid == _account?.Uid && view == _viewGeneration;
        Busy(true); SetStatus("Загрузка…");
        try { if (Current()) await operation(cts.Token, Current); }
        catch (Exception ex) { if (Current()) ShowError(ex); }
        finally
        {
            if (ReferenceEquals(_featureCts, cts)) _featureCts = null;
            if (ReferenceEquals(_offlineNavigation, cts)) _offlineNavigation = null;
            cts.Dispose();
            Busy(false);
        }
    }
    private async Task<bool> RefreshCatalogViewAsync()
    {
        if (HasPendingOfflineNavigation) return true;
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
        => e.Handled = true;
    private static LikedId TrackReference(TrackRow row)
        => TryTrackReference(row) ?? throw new MusicException("Ссылка на трек не совпадает с выбранной записью.");

    private static LikedId? TryTrackReference(TrackRow row)
    {
        var parts = row.RequestId.Split(':', 2);
        if (parts[0].Length == 0 || parts[0] != row.Id || parts.Length == 2 && (parts[1].Length == 0 || parts[1].Contains(':'))) return null;
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
        bool showStatus, bool deferFollowUp = false)
    {
        _favoritesLikeRevision++;
        var previousPendingAutoFavorites = _pendingAutoFavorites;
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
            var survivingPendingSnapshot = _pendingAutoFavorites is not null ? previousPendingAutoFavorites : null;
            ApplyVerifiedFavoriteIds(readback);
            var online = liked && row.Available && row.HasTrackMetadata && row.LocalPath is null && row.LocalQuality is null;
            complete = TryApplyConfirmedFavoriteSnapshot(online ? row : null, readback);
            if (complete)
            {
                UpdatePendingAutoFavoritesFromConfirmedReadback(survivingPendingSnapshot, allowNewRemovalSnapshot: !liked);
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
        if (liked || readback is not null && !complete && !deferFollowUp) _ = RefreshLibraryAsync();
        if (liked) ScheduleConfirmedFavoriteDownload(row);
        else if (complete && !deferFollowUp) TryStartPendingAutoFavorites();
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
        if (_activePlaylist is not { } playlist || _catalogWriting || IsStalePlaylistForWrite(playlist)) return;
        var dialog = new TextDialog(this, "Название плейлиста", playlist.Title);
        if (dialog.ShowDialog() != true) return;
        await RenamePlaylistFromCatalogAsync(playlist, dialog.Value);
    }
    private Task RenamePlaylistFromCatalogAsync(PlaylistDetails playlist, string title)
        => CatalogMutationAsync((session, token) =>
        {
            EnsureCurrentPlaylistForWrite(playlist);
            return _api.RenamePlaylistAsync(session, MergeKnownPlaylistRows(playlist), title, token, AdmitPlaylistWrite(playlist, token));
        }, null,
            reconcile: current => ApplyConfirmedPlaylistChange(current, created: false, titleConfirmed: true, edit: new(playlist)));
    private async void DeletePlaylist_Click(object sender, RoutedEventArgs e)
    {
        if (_activePlaylist is not { } playlist || _catalogWriting || IsStalePlaylistForWrite(playlist)) return;
        if (MessageBox.Show(this, $"Удалить плейлист «{playlist.Title}» из аккаунта?", "Удаление плейлиста", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        await DeletePlaylistFromCatalogAsync(playlist);
    }
    private Task DeletePlaylistFromCatalogAsync(PlaylistDetails playlist)
        => CatalogMutationAsync(async (session, token) =>
        {
            EnsureCurrentPlaylistForWrite(playlist);
            await _api.DeletePlaylistAsync(session, playlist, token, AdmitPlaylistWrite(playlist, token));
            return playlist;
        }, null, reconcile: ApplyConfirmedPlaylistDeletion);

    private void EnsureCurrentPlaylistForWrite(PlaylistDetails playlist)
    {
        EnsurePlaylistCatalogAccount();
        if (IsStalePlaylistForWrite(playlist))
            throw new MusicException("Плейлист изменился на сервере. Перечитайте его перед изменением; запись не отправлена.");
    }

    private MusicApi.PlaylistWriteAdmission AdmitPlaylistWrite(PlaylistDetails playlist, CancellationToken token)
    {
        var account = _accountGeneration;
        // Start the request in the same dispatcher turn as the final authority check.
        return send => Dispatcher.InvokeAsync(() =>
        {
            token.ThrowIfCancellationRequested();
            if (_closed || account != _accountGeneration || _account?.Uid != playlist.Owner)
                throw new OperationCanceledException(token);
            EnsureCurrentPlaylistForWrite(playlist);
            return send();
        }, DispatcherPriority.Normal, token).Task.Unwrap();
    }

    private bool IsStalePlaylistForWrite(PlaylistDetails playlist)
        => _playlistCatalogState.GetValueOrDefault((playlist.Owner, playlist.Kind)) is { } known
            && (known.Deleted || known.Header.Revision > playlist.Revision);

    private void UpdatePlaylistWriteAvailability(PlaylistDetails playlist)
        => RenamePlaylistButton.IsEnabled = DeletePlaylistButton.IsEnabled = RemovePlaylistTrackButton.IsEnabled = !IsStalePlaylistForWrite(playlist);

    private async void RemovePlaylistTrack_Click(object sender, RoutedEventArgs e)
    {
        if (_activePlaylist is not { } playlist || TracksList.SelectedItem is not TrackRow row || _catalogWriting) return;
        var before = playlist.Tracks.ToArray();
        var index = Array.FindIndex(before, track => ReferenceEquals(track, row));
        if (index < 0) return;
        var edit = new PlaylistTrackEdit(playlist with { Tracks = before }, index);
        try
        {
            await CatalogMutationAsync((session, token) =>
            {
                EnsureCurrentPlaylistForWrite(playlist);
                _pendingPlaylistTrackEdit = edit;
                return _api.RemovePlaylistTrackAsync(session, MergeKnownPlaylistRows(edit.Before), index, row.RequestId, token);
            }, null, reconcile: details => ApplyConfirmedPlaylistChange(details, created: false, edit: edit));
        }
        finally { CompletePendingPlaylistEdit(edit); }
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
        var reference = TryTrackReference(row);
        if (reference?.AlbumId is null) { SetStatus("Для добавления в плейлист нужен точный ID трека и альбома.", true); return; }
        var generation = _accountGeneration;
        var view = _viewGeneration;
        var lookup = _trackLinkLookup;
        using var request = CancellationTokenSource.CreateLinkedTokenSource(_accountCts.Token);
        var token = request.Token;
        _playlistChooserRequest = request;
        bool Current() => !_closed && !token.IsCancellationRequested && generation == _accountGeneration
            && _account?.Uid == account.Uid && view == _viewGeneration && lookup == _trackLinkLookup
            && ReferenceEquals(_playlistChooserRequest, request);
        try
        {
            var session = await EnsureSessionAsync(token);
            if (!Current()) return;
            var readMutation = _playlistMutationRevision;
            var playlists = await _api.GetPlaylistsAsync(session, account.Uid, token);
            if (!Current()) return;
            if (_catalogWriting) { SetStatus(CatalogWriteBusyMessage, true); return; }
            ObservePlaylistCollection(playlists, readMutation);
            var dialog = new PlaylistDialog(this, CurrentCatalogItems(playlists, includeCreatedPlaylists: true));
            if (dialog.ShowDialog() != true || dialog.Selected is not { } selected) return;
            if (!Current()) return;
            await InsertTrackIntoPlaylistCoreAsync(selected, reference, row);
        }
        catch (Exception ex) { if (Current()) ShowError(ex); }
        finally { if (ReferenceEquals(_playlistChooserRequest, request)) _playlistChooserRequest = null; }
    }

    private void CancelPlaylistChooser()
    {
        var request = _playlistChooserRequest;
        _playlistChooserRequest = null;
        request?.Cancel();
    }

    private Task InsertTrackIntoPlaylistAsync(CatalogItem selected, LikedId track)
        => InsertTrackIntoPlaylistCoreAsync(selected, track, sourceRow: null);

    private async Task InsertTrackIntoPlaylistCoreAsync(CatalogItem selected, LikedId track, TrackRow? sourceRow)
    {
        var generation = _accountGeneration;
        var view = _viewGeneration;
        var uid = _account?.Uid;
        PlaylistTrackEdit? edit = null;
        bool Current() => !_closed && generation == _accountGeneration && view == _viewGeneration && uid == _account?.Uid;
        try
        {
            await CatalogMutationAsync(async (session, token) =>
            {
                if (selected.Owner is null || selected.Owner != uid || CurrentPlaylistCatalogItem(selected) is null)
                    throw new MusicException("Выбранный плейлист больше недоступен для изменения.");
                var kind = selected.PlaylistKind ?? selected.Id;
                var playlist = await _api.GetPlaylistForEditingAsync(session, selected.Owner, kind, token);
                if (!Current()) throw new OperationCanceledException(token);
                EnsureCurrentPlaylistForWrite(playlist);
                _playlistCatalogState.TryAdd((playlist.Owner, playlist.Kind), new(PlaylistCatalogHeader(playlist)));
                playlist = MergeKnownPlaylistRows(playlist);
                session = await EnsureSessionAsync(token);
                token.ThrowIfCancellationRequested();
                if (!Current()) throw new OperationCanceledException(token);
                var index = playlist.Tracks.Count;
                edit = new(playlist, index, track);
                _pendingPlaylistTrackEdit = edit;
                return await _api.InsertPlaylistTracksAsync(session, playlist, [track], index, token,
                    knownInsertedTracks: sourceRow is null ? null : [sourceRow]);
            }, _ => SetStatus("Трек добавлен в выбранный плейлист"),
                reconcile: details => ApplyConfirmedPlaylistChange(details, created: false, edit: edit), reportBusy: true);
        }
        finally { CompletePendingPlaylistEdit(edit); }
    }

    private void CompletePendingPlaylistEdit(PlaylistTrackEdit? edit)
    {
        if (!ReferenceEquals(_pendingPlaylistTrackEdit, edit)) return;
        _pendingPlaylistTrackEdit = null;
        _deferredPlaylistSelections = [];
        _earlyPlaylistSelection = null;
    }

    private bool IsShowingPlaylist(PlaylistDetails playlist) => _section == "playlist-detail"
        && (_activePlaylist is { } active ? SamePlaylist(active, playlist) : MatchesPlaylistItem(_openedCatalogItem, playlist));

    private void ApplyConfirmedPlaylistChange(PlaylistDetails playlist, bool created, bool titleConfirmed = false, PlaylistTrackEdit? edit = null)
    {
        EnsurePlaylistCatalogAccount();
        var identity = (playlist.Owner, playlist.Kind);
        var known = _playlistCatalogState.GetValueOrDefault(identity);
        if (edit is not null && playlist.EditPositionsVerified)
            edit = edit with { ConfirmedRevision = playlist.Revision };
        playlist = PresentPlaylistAuthor(playlist);
        var newerHeader = known is { Deleted: false } && known.Header.Revision > playlist.Revision ? known : null;
        var header = newerHeader?.Header ?? PlaylistCatalogHeader(playlist);
        var mutation = ++_playlistMutationRevision;
        // An acknowledgement below an observed revision cannot invalidate a read of that newer header.
        _playlistCatalogState[identity] = newerHeader ?? new(header, mutation, created || known?.AwaitingCollection == true,
            TitleConfirmed: created || titleConfirmed || known?.TitleConfirmed == true, HasDetails: true);
        var intermediate = false;
        if (newerHeader is not null)
        {
            if (FindPlaylistSnapshot(header) is { } latest) playlist = latest;
            else
            {
                // Keep the newer card and its in-flight read. Older full views can use the confirmed intermediate composition.
                intermediate = true;
            }
        }
        playlist = MergeConfirmedPlaylistRows(playlist, edit);
        UpdateBrowsePlaylist(playlist, deleted: false, created: created, edit: edit, updateCards: !intermediate);
        if (created && _catalogShowing && _section == "playlists" && !_catalogItems.Any(item => MatchesPlaylistItem(item, playlist)))
        {
            _catalogItems.Add(PlaylistCatalogItem(playlist));
            UpdateCount();
        }
        if (IsShowingPlaylist(playlist) && (_activePlaylist is { } active ? playlist.Revision >= active.Revision : !intermediate))
        {
            ShowPlaylistWithSelection(playlist, edit);
            if (intermediate && _featureCts is not { IsCancellationRequested: false }
                && !HasPendingTrackLinkNavigation && !HasPendingOfflineNavigation)
                _ = ReadCatalogDetailAsync(PlaylistCatalogItem(header));
        }
    }

    private void ApplyConfirmedPlaylistDeletion(PlaylistDetails playlist)
    {
        EnsurePlaylistCatalogAccount();
        _playlistCatalogState[(playlist.Owner, playlist.Kind)] = new(PlaylistCatalogHeader(playlist), ++_playlistMutationRevision, Deleted: true);
        UpdateBrowsePlaylist(playlist, deleted: true);
        if (!IsShowingPlaylist(playlist)) return;
        if (!HasPendingOfflineNavigation && !HasPendingTrackLinkNavigation) _ = LoadCollectionCoreAsync("playlists", preserveSettings: true);
        else
        {
            _openedCatalogItem = PlaylistCatalogItem(playlist);
            _activePlaylist = null;
            HideCollectionHero();
            PlaylistActions.Visibility = Visibility.Collapsed;
            ShowRows([]);
        }
    }

    private void ResumeDeletedPlaylistFallback()
    {
        if (!_closed && _session is not null && _account is not null && !HasPendingOfflineNavigation
            && !HasPendingTrackLinkNavigation && !CanRememberBrowseView())
            _ = LoadCollectionCoreAsync("playlists", preserveSettings: true);
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
        var lookup = _trackLinkLookup;
        var uid = _account?.Uid;
        var token = _accountCts.Token;
        bool CurrentAccount() => !_closed && !token.IsCancellationRequested && generation == _accountGeneration && uid == _account?.Uid;
        try
        {
            var session = await EnsureSessionAsync(token);
            if (!CurrentAccount() || requireSameView && viewGeneration != _viewGeneration) return;
            var result = await operation(session, token);
            if (!CurrentAccount()) return;
            var sameView = viewGeneration == _viewGeneration && lookup == _trackLinkLookup && !HasPendingOfflineNavigation;
            reconcile?.Invoke(result);
            var metadataNotice = result is PlaylistDetails playlist ? playlist.MetadataIssue switch
            {
                PlaylistMetadataIssue.Authentication => "Изменение сохранено. Войдите в Яндекс, чтобы загрузить сведения о треках.",
                PlaylistMetadataIssue.Unavailable => "Изменение сохранено. Сведения о треках временно недоступны.",
                _ => null
            } : null;
            if (requireSameView && !sameView)
            {
                if (!HasPendingOfflineNavigation) SetStatus(metadataNotice ?? "Изменение сохранено в аккаунте.");
                return;
            }
            success?.Invoke(result);
            if (metadataNotice is not null) SetStatus(metadataNotice);
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
