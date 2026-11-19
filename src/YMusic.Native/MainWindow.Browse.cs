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
        internal long? PlaylistSelectionPlaybackToken { get; init; }
        internal EarlyPlaylistSelection? EarlyPlaylistSelection { get; init; }
        internal PlaylistSelection[] PlaylistSelections { get; init; } = [];
        internal DeferredPlaylistSelection[] DeferredPlaylistSelections { get; init; } = [];
        internal bool ArtistTracksNeedRead { get; init; }
        internal InitialCatalogRead? PendingCatalogRead { get; init; }
        internal (string Owner, string Kind)? OfflinePlaylist { get; init; }
        internal int AccountGeneration { get; init; }
        internal string? AccountUid { get; init; }
    }

    private void RememberBrowseView()
    {
        // A player link can be used while Settings covers Home or an artist page.
        // Capture the underlying music view's offset, not the Settings scroll position.
        SetSettingsVisible(false);
        if (CanRememberBrowseView()) PushBrowse(_browseHistory, CaptureBrowseView());
        _browseForward.Clear();
    }
    private bool CanRememberBrowseView()
    {
        if (_section != "playlist-detail") return true;
        if (_activePlaylist is { } playlist)
            return _playlistCatalogState.GetValueOrDefault((playlist.Owner, playlist.Kind))?.Deleted != true;
        return _openedCatalogItem is not { Kind: "playlist", Owner: { } owner } item
            || _playlistCatalogState.GetValueOrDefault((owner, item.PlaylistKind ?? item.Id))?.Deleted != true;
    }
    private BrowseSnapshot CaptureBrowseView()
    {
        var list = _catalogShowing ? (ListBox)CatalogList : TracksList;
        var queue = _section == "queue";
        return new(_section, CollectionTitle.Text, _section is "queue" or "queue-history" ? [] : _tracks.ToArray(),
            _catalogShowing ? _catalogItems.ToArray() : [],
            _catalogShowing, FilterBox.Text, _activePlaylist, _openedCatalogItem, _artistPage,
            _section == "artist-tracks" && MoreArtistButton.Visibility == Visibility.Visible, _artistDetails, _artistLiked,
            CatalogSearchPanel.Visibility == Visibility.Visible, FindViewer(BrowseScrollContent(list, _section))?.VerticalOffset ?? 0,
            list.SelectedIndex, list.SelectedItem,
            _activeAlbumDetails, _searchText, _searchType, _searchPage, _collectionsSearch,
            (CatalogType.SelectedItem as ComboBoxItem)?.Tag as string ?? "all", MoreCatalogButton.IsEnabled,
            CatalogQuery.Text, _waveViewMode, _waveSeedTrack, _waveSeedAlbum)
        {
            QueueSelection = queue ? CaptureQueueSelection() : [],
            PlaylistSelectionReconciled = _activePlaylist is not null && _playlistSelectionReconciled,
            PlaylistSelectionPlaybackToken = _activePlaylist is not null && IsPlaybackContextView()
                ? CapturedPlaylistSelectionPlaybackToken()
                : null,
            PlaylistSelections = _activePlaylist is { } playlist ? CapturePlaylistSelection(playlist) : [],
            DeferredPlaylistSelections = _activePlaylist is not null ? _deferredPlaylistSelections : [],
            EarlyPlaylistSelection = _activePlaylist is not null ? _earlyPlaylistSelection : null,
            ArtistTracksNeedRead = _section == "artist-tracks" && !_artistTracksLoaded,
            PendingCatalogRead = _catalogShowing ? _initialCatalogRead : null,
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
        => MatchesPlaylistItem(item, playlist.Owner, playlist.Kind);

    private static bool MatchesPlaylistItem(CatalogItem? item, string owner, string kind)
        => item is { Kind: "playlist" } && item.Owner == owner && (item.PlaylistKind ?? item.Id) == kind;

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

    private static (string Id, string RequestId) PlaylistRowReference(TrackRow row) => (row.Id, row.RequestId);

    private static bool SamePlaylistTrackOrder(PlaylistDetails left, PlaylistDetails right)
        => SamePlaylist(left, right)
            && left.Tracks.Select(PlaylistRowReference).SequenceEqual(right.Tracks.Select(PlaylistRowReference));

    private static bool SamePlaylistOccurrences(PlaylistDetails left, PlaylistDetails right)
        => left.Revision == right.Revision && SamePlaylistTrackOrder(left, right);

    private static bool MatchesConfirmedPlaylistEdit(PlaylistTrackEdit edit, PlaylistDetails next)
        => next.Revision == edit.ConfirmedRevision && MatchesPlaylistEditOrder(edit, next);

    private static bool ConfirmsEarlyPlaylistSelection(EarlyPlaylistSelection? early, PlaylistTrackEdit? edit, PlaylistDetails next)
        => early is not null && edit is not null && ReferenceEquals(early.EditKey, edit.SelectionKey)
            && early.Revision == next.Revision && MatchesConfirmedPlaylistEdit(edit, next);

    private static bool MatchesPlaylistEditOrder(PlaylistTrackEdit edit, PlaylistDetails next)
    {
        if (!SamePlaylist(next, edit.Before)) return false;
        var expected = edit.Before.Tracks.Select(PlaylistRowReference).ToList();
        if (edit.Index is int index)
        {
            if (edit.Inserted is { } inserted) expected.Insert(index, (inserted.Id, inserted.RequestId));
            else expected.RemoveAt(index);
        }
        return expected.SequenceEqual(next.Tracks.Select(PlaylistRowReference));
    }

    private static (Dictionary<TrackRow, (int Index, int Ordinal)> Positions, Dictionary<string, List<TrackRow>> Requests)
        IndexPlaylistSelection(IReadOnlyList<TrackRow> rows)
    {
        var positions = new Dictionary<TrackRow, (int, int)>(ReferenceEqualityComparer.Instance);
        var requests = new Dictionary<string, List<TrackRow>>(StringComparer.Ordinal);
        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            if (!requests.TryGetValue(row.RequestId, out var group)) requests[row.RequestId] = group = [];
            if (!positions.TryAdd(row, (i, group.Count))) positions[row] = (-1, -1);
            group.Add(row);
        }
        return (positions, requests);
    }

    private (PlaylistSelection[] Selected, DeferredPlaylistSelection[] Deferred, bool UsedConfirmedPositions) ResolvePlaylistSelection(
        PlaylistDetails previous, PlaylistDetails next, PlaylistSelection[] selected, DeferredPlaylistSelection[] deferred,
        PlaylistTrackEdit? edit, long? playbackToken)
    {
        if (selected.Length == 0 && deferred.Length == 0) return ([], [], false);
        var confirmed = edit is not null && MatchesConfirmedPlaylistEdit(edit, next);
        var sameOrder = SamePlaylistTrackOrder(previous, next);
        var result = new List<PlaylistSelection>();
        var waiting = new List<DeferredPlaylistSelection>();
        var pending = _pendingPlaylistTrackEdit;
        var usedConfirmedPositions = false;
        foreach (var choice in deferred)
        {
            if (!ReferenceEquals(choice.EditKey, pending?.SelectionKey) || next.Revision != choice.SuccessorRevision
                || previous.Revision != next.Revision || !sameOrder) continue;
            if (edit is null) waiting.Add(choice);
            else if (confirmed && ReferenceEquals(choice.EditKey, edit.SelectionKey))
            {
                usedConfirmedPositions = true;
                var target = edit.MapIndex(choice.SourceIndex);
                if (target >= 0) result.Add(new(next.Tracks[target], choice.OriginRevision, choice.Ordinal));
            }
        }
        if (selected.Length > 0)
        {
            var (previousPositions, beforeRequests) = IndexPlaylistSelection(previous.Tracks);
            var (nextPositions, afterRequests) = IndexPlaylistSelection(next.Tracks);
            var exactEdit = confirmed && edit is not null && SamePlaylistOccurrences(previous, edit.Before) ? edit : null;
            var unprovenDuplicates = !confirmed && edit is not null && next.Revision > edit.Before.Revision
                ? edit.Before.Tracks.GroupBy(row => row.RequestId, StringComparer.Ordinal).Where(group => group.Skip(1).Any())
                    .Select(group => group.Key).ToHashSet(StringComparer.Ordinal) : null;
            var canDefer = edit is null && pending is { Index: not null } && next.Revision > previous.Revision
                && SamePlaylistOccurrences(previous, pending.Before) && MatchesPlaylistEditOrder(pending, next);
            foreach (var choice in selected)
            {
                if (!previousPositions.TryGetValue(choice.Row, out var position) || position.Index < 0) continue;
                if (exactEdit is not null)
                {
                    usedConfirmedPositions = true;
                    var target = exactEdit.MapIndex(position.Index);
                    if (target >= 0) result.Add(choice with { Row = next.Tracks[target] });
                    continue;
                }
                var request = choice.Row.RequestId;
                if (unprovenDuplicates?.Contains(request) == true && choice.OriginRevision <= edit!.Before.Revision) continue;
                if (nextPositions.TryGetValue(choice.Row, out var exact))
                {
                    if (exact.Index >= 0) result.Add(choice);
                    continue;
                }
                var count = beforeRequests[request].Count;
                if (request.Length > 0 && (count == 1 || sameOrder) && afterRequests.TryGetValue(request, out var nextGroup) && nextGroup.Count == count)
                    result.Add(choice with { Row = nextGroup[position.Ordinal] });
                // Keep the intent until positions are confirmed, including a selection that the edit would remove.
                else if (canDefer && pending is not null)
                    waiting.Add(new(pending.SelectionKey, position.Index, choice.OriginRevision, choice.Ordinal, next.Revision, playbackToken));
            }
        }
        return (result.OrderBy(choice => choice.Ordinal).DistinctBy(choice => choice.Row, ReferenceEqualityComparer.Instance).ToArray(),
            waiting.ToArray(), usedConfirmedPositions);
    }

    private PlaylistDetails? FindPlaylistSnapshot(PlaylistDetails header)
    {
        bool Matches(PlaylistDetails details) => SamePlaylist(details, header)
            && (details.Revision > header.Revision || details.Revision == header.Revision && details.Title == header.Title);

        if (_activePlaylist is { } active && Matches(active)) return active;
        return _browseHistory.Concat(_browseForward).Select(view => view.Playlist).Append(_playbackContext?.Playlist)
            .OfType<PlaylistDetails>().Where(Matches).MaxBy(details => details.Revision);
    }

    private IEnumerable<PlaylistDetails> ExactKnownPlaylistSnapshots(PlaylistDetails playlist)
        => _browseHistory.Concat(_browseForward).Where(OwnsBrowseView).Select(view => view.Playlist)
            .Prepend(_activePlaylist).Append(HasPlaybackContext ? _playbackContext?.Playlist : null).OfType<PlaylistDetails>()
            .Where(snapshot => !ReferenceEquals(snapshot, playlist) && SamePlaylistOccurrences(snapshot, playlist));

    private PlaylistDetails MergeKnownPlaylistRows(PlaylistDetails playlist, bool preferPublished = false)
    {
        var missing = playlist.Tracks.Count(row => !row.HasTrackMetadata);
        var inherited = playlist.InheritedMetadataPositions;
        if (preferPublished) missing += inherited?.Count ?? 0;
        if (missing == 0) return playlist;
        TrackRow[]? tracks = null;
        HashSet<int>? revisedInheritance = null;
        foreach (var snapshot in ExactKnownPlaylistSnapshots(playlist))
        {
            for (var i = 0; i < playlist.Tracks.Count; i++)
            {
                if (!snapshot.Tracks[i].HasTrackMetadata) continue;
                var donorInherited = snapshot.InheritedMetadataPositions?.Contains(i) == true;
                var previouslyInherited = (revisedInheritance ?? inherited)?.Contains(i) == true;
                if ((tracks?[i] ?? playlist.Tracks[i]).HasTrackMetadata && !(preferPublished && previouslyInherited && !donorInherited)) continue;
                (tracks ??= playlist.Tracks.ToArray())[i] = snapshot.Tracks[i];
                if (donorInherited != previouslyInherited)
                {
                    revisedInheritance ??= inherited?.ToHashSet() ?? [];
                    if (donorInherited) revisedInheritance.Add(i);
                    else revisedInheritance.Remove(i);
                }
                if (!preferPublished || !donorInherited) missing--;
            }
            if (missing == 0) break;
        }
        return tracks is null ? playlist : playlist with { Tracks = tracks, InheritedMetadataPositions = revisedInheritance ?? inherited };
    }

    private PlaylistDetails MergeConfirmedPlaylistRows(PlaylistDetails playlist, PlaylistTrackEdit? edit)
    {
        var verified = edit is not null && MatchesConfirmedPlaylistEdit(edit, playlist);
        playlist = MergeKnownPlaylistRows(playlist, preferPublished: verified);
        if (!verified || edit is null || !playlist.Tracks.Any(row => !row.HasTrackMetadata)) return playlist;
        // An ordinary read may have filled gaps in the exact revision while the write was in flight.
        var previous = MergeKnownPlaylistRows(edit.Before);
        TrackRow[]? filled = null;
        HashSet<int>? inherited = null;
        for (var i = 0; i < previous.Tracks.Count; i++)
        {
            var row = previous.Tracks[i];
            var index = edit.MapIndex(i);
            if (index < 0 || playlist.Tracks[index].HasTrackMetadata || !row.HasTrackMetadata) continue;
            (filled ??= playlist.Tracks.ToArray())[index] = row.Number == index + 1 ? row : MusicApi.CopyPlaylistTrack(row, index + 1);
            (inherited ??= playlist.InheritedMetadataPositions?.ToHashSet() ?? []).Add(index);
        }
        return filled is null ? playlist : playlist with { Tracks = filled, InheritedMetadataPositions = inherited };
    }

    private static CatalogItem WithPlaylistMetadata(CatalogItem item, PlaylistDetails playlist)
        => item with { Title = playlist.Title, Subtitle = playlist.Description,
            CoverUri = playlist.CoverUri ?? item.CoverUri, OwnerName = playlist.OwnerName ?? item.OwnerName, PlaylistRevision = playlist.Revision };

    private void UpdateBrowsePlaylist(PlaylistDetails playlist, bool deleted, bool created = false, PlaylistTrackEdit? edit = null,
        bool exactRead = false, bool updateCards = true)
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
                if (!updateCards && view.Playlist is null) return view;
                if (view.Playlist is { } previous && previous.Revision > playlist.Revision) return view;
                if (exactRead && (view.Playlist is null || !SamePlaylistOccurrences(view.Playlist, playlist))) return view;
                var (selection, deferred, _) = view.Playlist is { } previousPlaylist
                    ? ResolvePlaylistSelection(previousPlaylist, playlist, view.PlaylistSelections, view.DeferredPlaylistSelections,
                        edit, view.PlaylistSelectionPlaybackToken)
                    : (Array.Empty<PlaylistSelection>(), Array.Empty<DeferredPlaylistSelection>(), false);
                var confirmsEarly = ConfirmsEarlyPlaylistSelection(view.EarlyPlaylistSelection, edit, playlist);
                return view with
                {
                    Title = playlist.Title, Tracks = updatedTracks ??= playlist.Tracks.ToArray(), Playlist = playlist,
                    Opened = view.Opened is { } opened ? UpdatedItem(opened) : null,
                    Selection = selection.FirstOrDefault()?.Row,
                    SelectedIndex = -1,
                    PlaylistSelectionReconciled = view.PlaylistSelectionReconciled || view.PlaylistSelections.Length > 0
                        || view.DeferredPlaylistSelections.Length > 0 || confirmsEarly,
                    PlaylistSelectionPlaybackToken = confirmsEarly ? view.EarlyPlaylistSelection!.PlaybackToken : view.PlaylistSelectionPlaybackToken,
                    EarlyPlaylistSelection = edit is null && view.EarlyPlaylistSelection?.Revision == playlist.Revision
                        ? view.EarlyPlaylistSelection : null,
                    PlaylistSelections = selection,
                    DeferredPlaylistSelections = deferred
                };
            }

            if (!updateCards || exactRead || !view.IsCatalog) return view;
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
        if (!exactRead && _playbackContext is { Playlist: { } source } context && SamePlaylist(source, playlist))
        {
            if (deleted) ClearPlaybackContext();
            else
            {
                var updated = Reconcile(context);
                if (updated?.Playlist is { } next)
                {
                    if (edit is not null) RebasePlaybackContextOccurrences(source, next, edit);
                    ObservePlaybackContextPlaylist(next);
                }
                _playbackContext = updated;
            }
        }
        if (updateCards && !exactRead && _catalogShowing)
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
            await ReadOfflineViewAsync((_, checks) => _offline.Tracks(checks), rows => Restore(previous with { Tracks = rows.ToArray() }));
        else if (previous.Section == "offline-playlists")
            await ReadOfflineViewAsync(ReadOfflinePlaylists, items => Restore(previous with { Catalog = items }));
        else if (previous.Section == "offline-playlist-detail" && previous.OfflinePlaylist is { } playlist)
            await ReadOfflineViewAsync((token, checks) => ReadOfflinePlaylist(playlist.Owner, playlist.Kind, previous.Title, token, checks), view =>
            {
                var selection = previous.Selection is TrackRow row ? RemapPlaylistSelection(previous.Tracks, view.Rows, row) : null;
                if (Restore(previous with { Title = view.Title, Tracks = view.Rows.ToArray(), Selection = selection, SelectedIndex = -1 }))
                    SetOfflinePlaylistStatus(view.Status);
            });
        else if (Restore(previous)) await ReadIncompleteBrowseDetailAsync(previous);

        bool Restore(BrowseSnapshot refreshed)
        {
            if (!source.TryPeek(out var current) || !ReferenceEquals(current, previous) || !OwnsBrowseView(current)) return false;
            source.Pop();
            if (CanRememberBrowseView()) PushBrowse(destination, CaptureBrowseView());
            RestoreBrowseView(refreshed, useLiveFavorites: true);
            RefreshFavoritesOnOpen();
            return true;
        }
    }

    private bool OwnsBrowseView(BrowseSnapshot snapshot)
        => !_closed && snapshot.AccountGeneration == _accountGeneration && snapshot.AccountUid == _account?.Uid;

    private Task ReadIncompleteBrowseDetailAsync(BrowseSnapshot snapshot)
    {
        if (snapshot.PendingCatalogRead is { } initial && initial.Section == snapshot.Section && _session is not null)
        {
            if (initial is { Section: "search", Query: { } query, SearchType: { } type })
                return ReadSearchPageAsync(query, type, page: 0);
            if (initial.Section is "playlists" or "albums" or "artists" && _account is not null)
                return ReadCollectionItemsAsync(initial.Section, snapshot.Title);
        }
        if (_session is null) return Task.CompletedTask;
        if (RequiredPlaylistDetailRead(snapshot) is { } newer) return ReadCatalogDetailAsync(newer);
        if (snapshot.Opened is not { } opened) return Task.CompletedTask;
        switch (snapshot.Section, opened.Kind)
        {
            case ("artist-detail", "artist") when snapshot.Artist is null:
                ClearArtistDetails(opened.Title, "Загружаем музыку исполнителя…");
                return ReadArtistDetailAsync(opened.Id);
            case ("artist-tracks", "artist") when snapshot.ArtistTracksNeedRead:
                return ReadInitialArtistTracksAsync(opened.Id);
            case ("album-detail", "album") when snapshot.Album is null:
            case ("playlist-detail", "playlist") when snapshot.Playlist is null:
            case ("track-detail", "track") when snapshot.Tracks.Length == 0:
                return ReadCatalogDetailAsync(opened);
            default:
                return Task.CompletedTask;
        }
    }

    private CatalogItem? RequiredPlaylistDetailRead(BrowseSnapshot snapshot)
        => snapshot is { Section: "playlist-detail", Playlist: { } playlist }
            && _playlistCatalogState.GetValueOrDefault((playlist.Owner, playlist.Kind)) is { Deleted: false } state
            && playlist.Revision < state.Header.Revision ? PlaylistCatalogItem(state.Header) : null;

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
            _artistTracksLoaded = previous.Section == "artist-tracks" && !previous.ArtistTracksNeedRead;
            _initialCatalogRead = previous.PendingCatalogRead;
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
            var early = previous.EarlyPlaylistSelection;
            if (early is not null && !ReferenceEquals(early.EditKey, _pendingPlaylistTrackEdit?.SelectionKey)) early = null;
            var restorePlaylistSelection = previous.Section == "playlist-detail"
                && (previous.PlaylistSelectionReconciled || previous.PlaylistSelections.Length > 0
                    || previous.DeferredPlaylistSelections.Length > 0 || early is not null);
            if (previous.Section == "queue") RestoreQueueSelection(previous.QueueSelection);
            else if (previous.Section == "queue-history")
                list.SelectedItem = previous.Selection is TrackRow historyRow && list.Items.Contains(historyRow) ? historyRow : null;
            else if (restorePlaylistSelection)
            {
                RestorePlaylistSelection(previous.PlaylistSelections, previous.DeferredPlaylistSelections
                    .Where(choice => ReferenceEquals(choice.EditKey, _pendingPlaylistTrackEdit?.SelectionKey)).ToArray());
                _earlyPlaylistSelection = early;
                if (previous.PlaylistSelectionReconciled)
                    ProtectReconciledPlaylistSelection(previous.PlaylistSelectionPlaybackToken);
            }
            else if (previous.IsCatalog && previous.Selection is CatalogItem { Kind: "playlist" } card)
                list.SelectedItem = card.Owner is { } owner
                    ? list.Items.OfType<CatalogItem>().FirstOrDefault(item => MatchesPlaylistItem(item, owner, card.PlaylistKind ?? card.Id))
                    : list.Items.Contains(card) ? card : null;
            else if (previous.Selection is not null && list.Items.Contains(previous.Selection)) list.SelectedItem = previous.Selection;
            else if (previous.Section == "offline" && previous.Selection is TrackRow offline)
                list.SelectedItem = list.Items.OfType<TrackRow>().FirstOrDefault(row => SameSavedTrack(row, offline));
            else if (previous.Section == "favorites" && previous.Selection is TrackRow selected)
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
            var playbackMoved = _playbackQueue.CurrentOccurrenceToken is long current
                && previous.PlaylistSelectionPlaybackToken != current && IsPlaybackContextView();
            var playbackOwnedChoice = previous.PlaylistSelectionReconciled || early is not null || _deferredPlaylistSelections.Length > 0;
            if (playbackOwnedChoice ? playbackMoved : !restorePlaylistSelection || TracksList.SelectedItems.Count <= 1)
                SyncPlaybackContextSelection(preserveSelectionIfUnavailable: restorePlaylistSelection);
        }
        finally { _restoringBrowse = false; }
    }
    private void Window_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton is MouseButton.Left or MouseButton.Right && _activePlaylist is { } playlist && e.OriginalSource is DependencyObject source
            && !FromButton(source))
        {
            var rowIndex = TrackRowIndex(source);
            if (rowIndex >= 0 || ReferenceEquals(source, TracksList)
                || source is ScrollContentPresenter content && content.IsDescendantOf(TracksList)) _deferredPlaylistSelections = [];
            if (e.ChangedButton == MouseButton.Left && rowIndex >= 0 && TracksList.Items[rowIndex] is TrackRow row
                && _playlistSelections.TryGetValue(row, out var choice))
                _playlistSelections[row] = choice with { OriginRevision = playlist.Revision };
        }
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
        _pendingPlaylistTrackEdit = null; _deferredPlaylistSelections = [];
        _playlistCatalogState.Clear(); _playlistCatalogAccount = null; _playlistMutationRevision = 0;
        CancelPlaylistChooser();
        CancelTrackLinkLookup();
        _offlineDownloadRefreshDeferred = false;
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
