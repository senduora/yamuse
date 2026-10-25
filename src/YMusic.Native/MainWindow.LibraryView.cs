using System.Windows;

namespace YMusic.Native;

public partial class MainWindow
{
    private const int BackgroundOfflineThreshold = 256;
    private CancellationTokenSource? _offlineMarksCts, _playlistStatusCts;
    private Task? _offlineMarksTask, _playlistStatusTask;
    private int _offlineViewEpoch;
    private PlaylistStatusSnapshot? _playlistStatusCache, _playlistStatusPending;
    private sealed record OfflineMark(TrackRow Track, string? Path, string? Quality);
    private sealed record PlaylistStatusSnapshot(string Owner, string Kind, int Revision, TrackRow[] Tracks, int Epoch, OfflinePlaylistStatus? Status);

    private void InvalidateOfflineViewState()
    {
        _offlineViewEpoch++;
        _offlineMarksCts?.Cancel(); _playlistStatusCts?.Cancel();
        _playlistStatusCache = _playlistStatusPending = null;
    }

    private void InvalidatePlaylistStatus()
    {
        _playlistStatusCts?.Cancel();
        _playlistStatusCache = _playlistStatusPending = null;
    }

    private OfflineMark[] ReadOfflineMarks(IReadOnlyList<TrackRow> tracks, CancellationToken token)
    {
        var marks = new OfflineMark[tracks.Count];
        for (var i = 0; i < tracks.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            var track = tracks[i];
            var saved = _offline.Find(track);
            marks[i] = new(track, saved is null ? null : _offline.ResolvePath(saved.FileName), saved is null ? null : OfflineQuality(saved));
        }
        return marks;
    }

    private void ApplyOfflineMarks(IEnumerable<OfflineMark> marks)
    {
        foreach (var mark in marks)
        {
            var wasDownloaded = mark.Track.IsDownloaded;
            mark.Track.DownloadedPath = mark.Path;
            mark.Track.DownloadedQuality = mark.Quality;
            if (wasDownloaded != mark.Track.IsDownloaded) QueueDownloadedSortRefresh();
        }
    }

    private void QueueOfflineMarks(TrackRow[] tracks)
    {
        // Entering a large view paints cached row marks immediately, then
        // revalidates disk state off the dispatcher even after an external edit.
        _playlistStatusCache = null; _playlistStatusCts?.Cancel(); _playlistStatusPending = null;
        _offlineMarksCts?.Cancel();
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, _accountCts.Token);
        _offlineMarksCts = cts;
        var epoch = _offlineViewEpoch; var account = _accountGeneration;
        _offlineMarksTask = RunAsync();
        async Task RunAsync()
        {
            try
            {
                var marks = await Task.Run(() => ReadOfflineMarks(tracks, cts.Token), cts.Token);
                if (_closed || cts.IsCancellationRequested || epoch != _offlineViewEpoch || account != _accountGeneration || _offlineMaintenance) return;
                ApplyOfflineMarks(marks);
                UpdateControls();
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested) { }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or MusicException)
            {
                if (!_closed && !cts.IsCancellationRequested && epoch == _offlineViewEpoch && account == _accountGeneration)
                    _diagnostics.LastErrorType = "OfflineMarks:" + error.GetType().Name;
            }
            finally { if (ReferenceEquals(_offlineMarksCts, cts)) _offlineMarksCts = null; cts.Dispose(); }
        }
    }

    private static bool SamePlaylistStatus(PlaylistStatusSnapshot? snapshot, PlaylistDetails playlist, int epoch)
        => snapshot is not null && snapshot.Epoch == epoch && snapshot.Owner == playlist.Owner && snapshot.Kind == playlist.Kind
            && snapshot.Revision == playlist.Revision && snapshot.Tracks.Length == playlist.Tracks.Count
            && snapshot.Tracks.Zip(playlist.Tracks).All(pair => ReferenceEquals(pair.First, pair.Second));

    private OfflinePlaylistStatus? LargePlaylistStatus(PlaylistDetails playlist)
    {
        if (SamePlaylistStatus(_playlistStatusCache, playlist, _offlineViewEpoch)) return _playlistStatusCache!.Status;
        if (SamePlaylistStatus(_playlistStatusPending, playlist, _offlineViewEpoch)) return null;
        _playlistStatusCts?.Cancel();
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, _accountCts.Token);
        _playlistStatusCts = cts;
        var snapshot = new PlaylistStatusSnapshot(playlist.Owner, playlist.Kind, playlist.Revision, playlist.Tracks.ToArray(), _offlineViewEpoch, null);
        _playlistStatusPending = snapshot;
        var account = _accountGeneration;
        _playlistStatusTask = RunAsync();
        return null;
        async Task RunAsync()
        {
            try
            {
                var state = await Task.Run(() => _offline.InspectPlaylistStatus(snapshot.Owner, snapshot.Kind, snapshot.Tracks, cts.Token), cts.Token);
                if (_closed || cts.IsCancellationRequested || account != _accountGeneration || snapshot.Epoch != _offlineViewEpoch
                    || !ReferenceEquals(_playlistStatusPending, snapshot)) return;
                _playlistStatusCache = snapshot with { Status = state };
                _playlistStatusPending = null;
                UpdatePlaylistDownloadedStatus();
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested) { }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or MusicException)
            {
                if (!_closed && !cts.IsCancellationRequested && account == _accountGeneration && snapshot.Epoch == _offlineViewEpoch
                    && ReferenceEquals(_playlistStatusPending, snapshot))
                { _diagnostics.LastErrorType = "PlaylistStatus:" + error.GetType().Name; PlaylistDownloadText.Text = "Не удалось прочитать статус скачивания"; }
            }
            finally
            {
                if (ReferenceEquals(_playlistStatusPending, snapshot)) _playlistStatusPending = null;
                if (ReferenceEquals(_playlistStatusCts, cts)) _playlistStatusCts = null;
                cts.Dispose();
            }
        }
    }
}
