using System.Windows;
using System.Windows.Automation;

namespace YMusic.Native;

public partial class MainWindow
{
    // Reuse the existing navigation snapshot. The arrays below belong to this
    // capture; TrackRow metadata is immutable, while its live marks stay shared.
    private BrowseSnapshot? _playbackContext;
    private long _playbackContextQueueVersion;
    private int _playbackContextAccount;
    private string? _playbackContextUid;
    private ListSortRule? _playbackContextSort;
    private Dictionary<string, List<TrackRow>> _playbackContextRows = new(StringComparer.Ordinal);
    private Dictionary<long, PlaybackContextOccurrence> _playbackContextOccurrences = [];
    // Keep the occurrence map's original rows and the latest revision observed with that exact order.
    private PlaylistDetails? _playbackContextOccurrencePlaylist;
    private int _playbackContextSameOrderRevision;
    private readonly Dictionary<long, int> _ambiguousPlaylistOccurrences = [];
    private bool _playbackRowsMatchOccurrenceOrder;
    private readonly record struct PlaybackContextOccurrence(TrackRow Row, int RequestOrdinal, int RequestCount, int ReferenceOrdinal, int ReferenceCount);

    private bool OwnsPlaybackContext => !_closed && _playbackContext is not null
        && _playbackContextQueueVersion == _playbackQueue.ReplacementVersion
        && _playbackContextAccount == _accountGeneration && _playbackContextUid == _account?.Uid;
    private bool HasPlaybackContext => OwnsPlaybackContext && _queue.Count > 0;

    private void CapturePlaybackContext()
    {
        ClearPlaybackContext();
        if (_catalogShowing || _section is not ("playlist-detail" or "favorites" or "album-detail")) return;
        var snapshot = CaptureBrowseView();
        if (snapshot.Section == "playlist-detail" && snapshot.Playlist is null
            || snapshot.Section == "album-detail" && snapshot.Album is null && snapshot.Opened?.Kind != "album") return;
        // Playlist/album containers can come from mutable service caches. Keep
        // this launch's full source list even when its queue is filtered/sorted.
        if (snapshot.Playlist is { } playlist)
            snapshot = snapshot with { Playlist = playlist with { Tracks = Array.AsReadOnly(snapshot.Tracks) } };
        if (snapshot.Album is { } album)
            snapshot = snapshot with { Album = album with { Tracks = Array.AsReadOnly(snapshot.Tracks) } };
        _playbackContext = snapshot;
        _playbackContextSort = CurrentListSort;
        _playbackContextQueueVersion = _playbackQueue.ReplacementVersion;
        _playbackContextOccurrencePlaylist = snapshot.Playlist;
        _playbackContextSameOrderRevision = snapshot.Playlist?.Revision ?? 0;
        _playbackRowsMatchOccurrenceOrder = snapshot.Playlist is not null;
        CapturePlaybackContextOccurrences(snapshot.Tracks);
        _playbackContextAccount = _accountGeneration;
        _playbackContextUid = _account?.Uid;
        UpdatePlaybackContextButton();
    }

    private void ClearPlaybackContext(bool clearView = false)
    {
        _restoredSourceAlbum = null;
        _playbackContext = null; _playbackContextUid = null; _playbackContextSort = null;
        _playbackContextOccurrences = [];
        _playbackContextOccurrencePlaylist = null;
        _playbackContextSameOrderRevision = 0;
        _ambiguousPlaylistOccurrences.Clear();
        _playbackRowsMatchOccurrenceOrder = false;
        if (clearView) _playbackContextRows = new(StringComparer.Ordinal);
        UpdatePlaybackContextButton();
    }

    private void ValidatePlaybackContext()
    {
        if (_playbackContext is not null && !OwnsPlaybackContext) ClearPlaybackContext();
    }

    private void UpdatePlaybackContextButton()
    {
        if (PlaybackContextButton is null) return;
        var known = HasPlaybackContext;
        PlaybackContextButton.Visibility = known ? Visibility.Visible : Visibility.Collapsed;
        PlaybackContextButton.IsEnabled = known;
        var label = known ? "К списку воспроизведения · " + _playbackContext!.Title : "Источник очереди неизвестен";
        PlaybackContextButton.ToolTip = label;
        AutomationProperties.SetName(PlaybackContextButton, label);
    }

    private bool IsPlaybackContextView()
    {
        if (!HasPlaybackContext || _catalogShowing || _playbackContext!.Section != _section) return false;
        return _section switch
        {
            "favorites" => true,
            "playlist-detail" => _playbackContext.Playlist is { } source && _activePlaylist is { } shown
                && source.Owner == shown.Owner && source.Kind == shown.Kind,
            "album-detail" => (_playbackContext.Album?.Id ?? _playbackContext.Opened?.Id) is { } id
                && id == (_activeAlbumDetails?.Id ?? (_openedCatalogItem?.Kind == "album" ? _openedCatalogItem.Id : null)),
            _ => false
        };
    }

    // Called once when a list is populated, never by the progress timer.
    private void PlaybackContextViewShown()
    {
        if (IsPlaybackContextView() && _section == "playlist-detail" && _activePlaylist is { } latest)
        {
            ObservePlaybackContextPlaylist(latest);
            _playbackRowsMatchOccurrenceOrder = _playbackContextOccurrencePlaylist is { } anchor && SamePlaylistTrackOrder(anchor, latest);
            var source = _playbackContext!;
            // A refreshed/mutated known playlist is newer than its launch copy.
            // Browsing a different owner/kind never changes this source.
            var tracks = _tracks.ToArray();
            _playbackContext = source with { Title = latest.Title, Tracks = tracks,
                Playlist = latest with { Tracks = Array.AsReadOnly(tracks) } };
        }
        _playbackContextRows = new(StringComparer.Ordinal);
        foreach (var row in _tracks)
        {
            if (!_playbackContextRows.TryGetValue(row.RequestId, out var rows))
                _playbackContextRows[row.RequestId] = rows = [];
            rows.Add(row);
        }
        UpdatePlaybackContextButton();
        SyncPlaybackContextSelection();
    }

    private static PlaybackContextOccurrence[] DescribePlaybackContextOccurrences(IReadOnlyList<TrackRow> source)
    {
        var requestCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var referenceCounts = new Dictionary<TrackRow, int>(ReferenceEqualityComparer.Instance);
        var described = new PlaybackContextOccurrence[source.Count];
        for (var i = 0; i < source.Count; i++)
        {
            var row = source[i];
            var requestOrdinal = requestCounts[row.RequestId] = requestCounts.GetValueOrDefault(row.RequestId) + 1;
            var referenceOrdinal = referenceCounts[row] = referenceCounts.GetValueOrDefault(row) + 1;
            described[i] = new(row, requestOrdinal, 0, referenceOrdinal, 0);
        }
        for (var i = 0; i < described.Length; i++)
        {
            var occurrence = described[i];
            described[i] = occurrence with { RequestCount = requestCounts[occurrence.Row.RequestId], ReferenceCount = referenceCounts[occurrence.Row] };
        }
        return described;
    }

    private void CapturePlaybackContextOccurrences(IReadOnlyList<TrackRow> source)
    {
        var positions = new Dictionary<TrackRow, Queue<PlaybackContextOccurrence>>(ReferenceEqualityComparer.Instance);
        foreach (var occurrence in DescribePlaybackContextOccurrences(source))
        {
            if (!positions.TryGetValue(occurrence.Row, out var matches)) positions[occurrence.Row] = matches = new();
            matches.Enqueue(occurrence);
        }
        _playbackContextOccurrences = new(_queue.Count);
        for (var i = 0; i < _queue.Count; i++)
        {
            var row = _queue[i];
            if (positions.TryGetValue(row, out var occurrences) && occurrences.TryDequeue(out var position))
                _playbackContextOccurrences[_playbackQueue.OccurrenceTokenAt(i)] = position;
        }
    }

    private void RebasePlaybackContextOccurrences(PlaylistDetails shown, PlaylistDetails next, PlaylistTrackEdit edit)
    {
        if (!HasPlaybackContext || _playbackContextOccurrencePlaylist is not { } previous || !SamePlaylist(previous, edit.Before)) return;
        if (!MatchesConfirmedPlaylistEdit(edit, next))
        {
            // The edit result proves the final list, but its intervening history may have replaced a duplicate.
            if (previous.Revision <= edit.Before.Revision && next.Revision > edit.Before.Revision)
                foreach (var (token, occurrence) in _playbackContextOccurrences)
                    if (occurrence.RequestCount > 1) _ambiguousPlaylistOccurrences.TryAdd(token, next.Revision);
            return;
        }
        if (edit.Before.Revision != previous.Revision && edit.Before.Revision != _playbackContextSameOrderRevision
            || !SamePlaylistTrackOrder(previous, edit.Before)
            || !SamePlaylistOccurrences(shown, edit.Before) && !SamePlaylistOccurrences(shown, next)) return;

        var nextPositions = DescribePlaybackContextOccurrences(next.Tracks);
        var previousPositions = DescribePlaybackContextOccurrences(previous.Tracks)
            .Select((occurrence, index) => (occurrence, index)).ToDictionary(item => item.occurrence, item => item.index);
        var rebased = new Dictionary<long, PlaybackContextOccurrence>(_playbackContextOccurrences.Count);
        foreach (var (token, occurrence) in _playbackContextOccurrences)
        {
            // An exact late result can resolve doubt caused by its own successor arriving first.
            if (_ambiguousPlaylistOccurrences.TryGetValue(token, out var lostInRevision)
                && (lostInRevision <= edit.Before.Revision || lostInRevision != next.Revision)) continue;
            if (!previousPositions.TryGetValue(occurrence, out var index)) continue;
            var mapped = edit.MapIndex(index);
            if (mapped >= 0) rebased.Add(token, nextPositions[mapped]);
        }
        _playbackContextOccurrences = rebased;
        _playbackContextOccurrencePlaylist = next;
        _playbackContextSameOrderRevision = next.Revision;
        _ambiguousPlaylistOccurrences.Clear();
    }

    private void ObservePlaybackContextPlaylist(PlaylistDetails displayed)
    {
        if (!HasPlaybackContext || _playbackContextOccurrencePlaylist is not { } anchor
            || displayed.Revision < anchor.Revision || !SamePlaylist(displayed, anchor)) return;
        if (SamePlaylistTrackOrder(anchor, displayed))
        {
            _playbackContextSameOrderRevision = Math.Max(_playbackContextSameOrderRevision, displayed.Revision);
            return;
        }
        var requestCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var referenceCounts = new Dictionary<TrackRow, int>(ReferenceEqualityComparer.Instance);
        foreach (var row in displayed.Tracks)
        {
            requestCounts[row.RequestId] = requestCounts.GetValueOrDefault(row.RequestId) + 1;
            referenceCounts[row] = referenceCounts.GetValueOrDefault(row) + 1;
        }
        foreach (var (token, occurrence) in _playbackContextOccurrences)
        {
            var count = requestCounts.GetValueOrDefault(occurrence.Row.RequestId);
            if ((count > 1 || occurrence.RequestCount > 1)
                && referenceCounts.GetValueOrDefault(occurrence.Row) != occurrence.ReferenceCount)
                _ambiguousPlaylistOccurrences.TryAdd(token, displayed.Revision);
        }
    }

    private (TrackRow Row, int ReferenceOrdinal)? PlaybackContextCurrentOccurrence()
    {
        if (_playbackQueue.Current is not { } current || _playbackQueue.CurrentOccurrenceToken is not { } token
            || _ambiguousPlaylistOccurrences.ContainsKey(token)
            || !_playbackContextOccurrences.TryGetValue(token, out var occurrence)
            || !_playbackContextRows.TryGetValue(current.RequestId, out var rows)) return null;
        var referenceCount = rows.Count(row => ReferenceEquals(row, occurrence.Row));
        if (referenceCount == occurrence.ReferenceCount)
            return (occurrence.Row, occurrence.ReferenceOrdinal);
        // A normal refresh with the same full ordered references keeps the source ordinal.
        // An observed ambiguous change blocks that fallback even if the order later returns.
        if (occurrence.RequestCount > 1 && _playbackContextOccurrencePlaylist is not null && !_playbackRowsMatchOccurrenceOrder) return null;
        if (rows.Count != occurrence.RequestCount || occurrence.RequestOrdinal > rows.Count) return null;
        var target = rows[occurrence.RequestOrdinal - 1];
        return (target, rows.Take(occurrence.RequestOrdinal).Count(row => ReferenceEquals(row, target)));
    }

    private int SyncPlaybackContextSelection(bool reveal = false, bool preserveSelectionIfUnavailable = false)
    {
        if (!IsPlaybackContextView() || _playbackQueue.Current is null || !reveal && (_current is null || !_player.HasSource || _loadingStream
            || !ReferenceEquals(_playbackQueue.Current, _current))) return -1;
        var deferred = _deferredPlaylistSelections;
        if (!reveal && deferred.Any(choice => choice.PlaybackToken == _playbackQueue.CurrentOccurrenceToken)) return -1;
        if (!reveal && _playlistSelectionReconciled && _playlistSelectionPlaybackToken == _playbackQueue.CurrentOccurrenceToken) return -1;
        if (!reveal && _earlyPlaylistSelection is { } early && early.PlaybackToken == _playbackQueue.CurrentOccurrenceToken) return -1;
        var occurrence = PlaybackContextCurrentOccurrence();
        if (occurrence is not { } target) return Unavailable();
        var row = target.Row;
        if (FilterBox.Text.Trim() is { Length: > 0 } filter && !row.Matches(filter))
        {
            if (!reveal) return Unavailable();
            FilterBox.Clear(); _filterTimer.Stop(); _query = ""; _view.Refresh();
        }
        var visibleIndex = PlaybackContextVisibleIndex(target);
        if (visibleIndex < 0) return Unavailable();
        var replacesDeferredSelection = reveal && _deferredPlaylistSelections.Length > 0;
        if (reveal || _playlistSelectionReconciled || _earlyPlaylistSelection is not null || deferred.Length > 0) UsePlaybackContextSelection();
        if (replacesDeferredSelection || TracksList.SelectedIndex != visibleIndex || TracksList.SelectedItems.Count != 1)
            SelectIndex(visibleIndex, row);
        return visibleIndex;

        int Unavailable()
        {
            if (!preserveSelectionIfUnavailable && (reveal || deferred.Length == 0)) SelectIndex(-1, null);
            return -1;
        }

        void SelectIndex(int index, TrackRow? selected)
        {
            if (_activePlaylist is not { } playlist) { TracksList.SelectedIndex = index; return; }
            _applyingPlaylistSelection = true;
            try { TracksList.SelectedIndex = index; }
            finally { _applyingPlaylistSelection = false; }
            _playlistSelections.Clear();
            if (selected is not null)
                _playlistSelections[selected] = new(selected, _playbackContextOccurrencePlaylist?.Revision ?? playlist.Revision, ++_playlistSelectionOrdinal);
            UpdateControls();
        }
    }

    private void UsePlaybackContextSelection()
    {
        _playlistSelectionReconciled = false;
        _playlistSelectionPlaybackToken = null;
        _deferredPlaylistSelections = [];
        _earlyPlaylistSelection = null;
    }

    private void ProtectReconciledPlaylistSelection(long? playbackToken)
    {
        _playlistSelectionReconciled = true;
        _playlistSelectionPlaybackToken = playbackToken;
    }

    private long? CapturedPlaylistSelectionPlaybackToken() => _playlistSelectionReconciled ? _playlistSelectionPlaybackToken
        : _deferredPlaylistSelections.FirstOrDefault() is { } deferred ? deferred.PlaybackToken
        : _earlyPlaylistSelection is { } early ? early.PlaybackToken : _playbackQueue.CurrentOccurrenceToken;

    private (int View, long Replacement)? _sourcePlaylistRead;

    private void ProtectManualSourceSelection()
    {
        if (_sourcePlaylistRead != (_viewGeneration, _playbackQueue.ReplacementVersion) || !IsPlaybackContextView()) return;
        ProtectReconciledPlaylistSelection(_playbackQueue.CurrentOccurrenceToken);
        if (!ReferenceEquals(_currentTrackRevealOwner, TracksList)) return;
        _currentTrackRevealOwner = null;
        _currentTrackRevealRequest++;
    }

    private int PlaybackContextVisibleIndex((TrackRow Row, int ReferenceOrdinal) target)
    {
        var ordinal = target.ReferenceOrdinal;
        var index = 0;
        foreach (TrackRow row in _view)
        {
            if (ReferenceEquals(row, target.Row) && --ordinal == 0) return index;
            index++;
        }
        return -1;
    }

    private async void PlaybackContext_Click(object sender, RoutedEventArgs e)
    {
        if (!HasPlaybackContext) return;
        var source = _playbackContext!;
        var sourceSort = _playbackContextSort ?? new();
        // Existing navigation closes Settings through its live-save guard and
        // cancels pending page IO. Restoring this snapshot does not open audio.
        if (IsPlaybackContextView())
        {
            _featureCts?.Cancel();
            AdvanceViewGeneration();
            SetSettingsVisible(false);
            SetLyricsVisible(false);
        }
        else
        {
            RememberBrowseView();
            var shown = source;
            if (source.Section == "favorites" && _favoriteIdsKnown)
                shown = source with { Tracks = _favoritesCache.Where(row => _favoriteTrackIds.Contains(row.Id)).ToArray(), Selection = null, SelectedIndex = -1 };
            RestoreBrowseView(shown);
        }
        if (CurrentListSort != sourceSort) ChangeListSort(sourceSort);
        SyncPlaybackContextSelection(reveal: true);
        var view = _viewGeneration; var filter = FilterBox.Text;
        var replacement = _playbackQueue.ReplacementVersion;
        var newer = _session is null ? null : RequiredPlaylistDetailRead(source);
        var readingNewer = newer is not null;
        var waitingAfterReveal = false;
        RequestReveal(newer is null ? null : () =>
        {
            if (!readingNewer) return;
            waitingAfterReveal = true;
            _currentTrackRevealOwner = TracksList;
        });
        if (newer is null) return;
        var request = _currentTrackRevealRequest;
        var readOwner = (view, replacement);
        _sourcePlaylistRead = readOwner;
        try
        {
            await ReadCatalogDetailAsync(newer, afterPlaylistRead: () =>
            {
                if (request == _currentTrackRevealRequest && OwnsReveal()) RequestReveal(followReadback: true);
            });
        }
        finally
        {
            if (_sourcePlaylistRead == readOwner) _sourcePlaylistRead = null;
            readingNewer = false;
            if (waitingAfterReveal && request == _currentTrackRevealRequest) _currentTrackRevealOwner = null;
        }

        bool OwnsReveal() => view == _viewGeneration && FilterBox.Text == filter && replacement == _playbackQueue.ReplacementVersion
            && HasPlaybackContext && IsPlaybackContextView();
        void RequestReveal(Action? revealed = null, bool followReadback = false)
        {
            var context = _playbackContext;
            RequestCurrentTrackReveal(TracksList, () => SyncPlaybackContextSelection(reveal: true),
                () => OwnsReveal() && (followReadback || ReferenceEquals(_playbackContext, context)), revealed);
        }
    }
}
