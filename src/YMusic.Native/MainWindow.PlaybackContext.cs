using System.Windows;
using System.Windows.Automation;

namespace YMusic.Native;

public partial class MainWindow
{
    // Reuse the existing navigation snapshot. The arrays below belong to this
    // capture; TrackRow metadata is immutable, while its live marks stay shared.
    private BrowseSnapshot? _playbackContext;
    private IReadOnlyList<TrackRow>? _playbackContextQueue;
    private int _playbackContextAccount;
    private string? _playbackContextUid;
    private ListSortRule? _playbackContextSort;
    private Dictionary<string, List<TrackRow>> _playbackContextRows = new(StringComparer.Ordinal);
    private PlaybackContextOccurrence[] _playbackContextOccurrences = [];
    private readonly record struct PlaybackContextOccurrence(int RequestOrdinal, int RequestCount, int ReferenceOrdinal, int ReferenceCount);

    private bool HasPlaybackContext => !_closed && _playbackContext is not null && _queue.Count > 0
        && ReferenceEquals(_playbackContextQueue, _playbackQueue.Items)
        && _playbackContextAccount == _accountGeneration && _playbackContextUid == _account?.Uid;

    private void CapturePlaybackContext()
    {
        ClearPlaybackContext();
        if (_catalogShowing || _section is not ("playlist-detail" or "favorites" or "album-detail" or "smart-playlist-detail" or "quick-access-detail")) return;
        var snapshot = CaptureBrowseView();
        if (snapshot.Section == "playlist-detail" && snapshot.Playlist is null
            || snapshot.Section == "album-detail" && snapshot.Album is null && snapshot.Opened?.Kind != "album") return;
        if (snapshot.Section == "smart-playlist-detail" && snapshot.Opened?.Kind != "smart"
            || snapshot.Section == "quick-access-detail" && snapshot.Opened?.Kind != "quick") return;
        // Playlist/album containers can come from mutable service caches. Keep
        // this launch's full source list even when its queue is filtered/sorted.
        if (snapshot.Playlist is { } playlist)
            snapshot = snapshot with { Playlist = playlist with { Tracks = Array.AsReadOnly(snapshot.Tracks) } };
        if (snapshot.Album is { } album)
            snapshot = snapshot with { Album = album with { Tracks = Array.AsReadOnly(snapshot.Tracks) } };
        _playbackContext = snapshot;
        _playbackContextSort = CurrentListSort;
        _playbackContextQueue = _playbackQueue.Items;
        CapturePlaybackContextOccurrences(snapshot.Tracks);
        _playbackContextAccount = _accountGeneration;
        _playbackContextUid = _account?.Uid;
        UpdatePlaybackContextButton();
    }

    private void ClearPlaybackContext(bool clearView = false)
    {
        _playbackContext = null; _playbackContextQueue = null; _playbackContextUid = null; _playbackContextSort = null;
        _playbackContextOccurrences = [];
        if (clearView) _playbackContextRows = new(StringComparer.Ordinal);
        UpdatePlaybackContextButton();
    }

    private void ValidatePlaybackContext()
    {
        if (_playbackContext is not null && !HasPlaybackContext) ClearPlaybackContext();
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
            "smart-playlist-detail" => _playbackContext.Opened is { Kind: "smart" } smart && _openedCatalogItem is { Kind: "smart" } shown && smart.Id == shown.Id,
            "quick-access-detail" => _playbackContext.Opened is { Kind: "quick" } quick && _openedCatalogItem is { Kind: "quick" } shown && quick.Id == shown.Id,
            _ => false
        };
    }

    // Called once when a list is populated, never by the progress timer.
    private void PlaybackContextViewShown()
    {
        if (IsPlaybackContextView() && _section == "playlist-detail" && _activePlaylist is { } latest)
        {
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

    private void CapturePlaybackContextOccurrences(IReadOnlyList<TrackRow> source)
    {
        var requestCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var positions = new Dictionary<TrackRow, Queue<(int RequestOrdinal, int ReferenceOrdinal)>>(ReferenceEqualityComparer.Instance);
        foreach (var row in source)
        {
            requestCounts[row.RequestId] = requestCounts.GetValueOrDefault(row.RequestId) + 1;
            if (!positions.TryGetValue(row, out var occurrences)) positions[row] = occurrences = new();
            occurrences.Enqueue((requestCounts[row.RequestId], occurrences.Count + 1));
        }
        var referenceCounts = new Dictionary<TrackRow, int>(ReferenceEqualityComparer.Instance);
        foreach (var pair in positions) referenceCounts[pair.Key] = pair.Value.Count;
        _playbackContextOccurrences = new PlaybackContextOccurrence[_queue.Count];
        for (var i = 0; i < _queue.Count; i++)
        {
            var row = _queue[i];
            if (positions.TryGetValue(row, out var occurrences) && occurrences.TryDequeue(out var position))
                _playbackContextOccurrences[i] = new(position.RequestOrdinal, requestCounts[row.RequestId], position.ReferenceOrdinal, referenceCounts[row]);
        }
    }

    private (TrackRow Row, int ReferenceOrdinal)? PlaybackContextCurrentOccurrence()
    {
        var index = _playbackQueue.CurrentIndex;
        if (_playbackQueue.Current is not { } current || index < 0 || index >= _playbackContextOccurrences.Length
            || !_playbackContextRows.TryGetValue(current.RequestId, out var rows)) return null;
        var occurrence = _playbackContextOccurrences[index];
        if (occurrence.ReferenceOrdinal <= 0) return null;
        var referenceCount = rows.Count(row => ReferenceEquals(row, current));
        if (referenceCount == occurrence.ReferenceCount)
            return (current, occurrence.ReferenceOrdinal);
        // A service refresh can replace row instances. Keep the launch's source
        // ordinal, including when its queue was sorted or filtered. If duplicate
        // multiplicity changed, the removed occurrence is ambiguous: select none.
        if (rows.Count != occurrence.RequestCount || occurrence.RequestOrdinal > rows.Count) return null;
        var target = rows[occurrence.RequestOrdinal - 1];
        return (target, rows.Take(occurrence.RequestOrdinal).Count(row => ReferenceEquals(row, target)));
    }

    private int SyncPlaybackContextSelection(bool reveal = false)
    {
        if (!IsPlaybackContextView() || _playbackQueue.Current is null || !reveal && (_current is null || !_player.HasSource || _loadingStream
            || !ReferenceEquals(_playbackQueue.Current, _current))) return -1;
        var occurrence = PlaybackContextCurrentOccurrence();
        if (occurrence is not { } target) { TracksList.SelectedIndex = -1; return -1; }
        var row = target.Row;
        if (FilterBox.Text.Trim() is { Length: > 0 } filter && !row.Matches(filter))
        {
            if (!reveal) { TracksList.SelectedIndex = -1; return -1; }
            FilterBox.Clear(); _filterTimer.Stop(); _query = ""; _view.Refresh();
        }
        var ordinal = target.ReferenceOrdinal; var visibleIndex = 0;
        foreach (TrackRow visible in _view)
        {
            if (ReferenceEquals(visible, row) && --ordinal == 0)
            {
                if (TracksList.SelectedIndex != visibleIndex || TracksList.SelectedItems.Count != 1)
                    TracksList.SelectedIndex = visibleIndex;
                return visibleIndex;
            }
            visibleIndex++;
        }
        TracksList.SelectedIndex = -1;
        return -1;
    }

    private void PlaybackContext_Click(object sender, RoutedEventArgs e)
    {
        if (!HasPlaybackContext) return;
        var source = _playbackContext!;
        var sourceSort = _playbackContextSort ?? new();
        // Existing navigation closes Settings through its live-save guard and
        // cancels pending page IO. Restoring this snapshot does not open audio.
        if (IsPlaybackContextView())
        {
            _featureCts?.Cancel();
            _viewGeneration++;
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
        var context = _playbackContext;
        RequestCurrentTrackReveal(TracksList, () => SyncPlaybackContextSelection(reveal: true),
            () => view == _viewGeneration && FilterBox.Text == filter && ReferenceEquals(_playbackContext, context)
                && HasPlaybackContext && IsPlaybackContextView());
    }
}
