using System.Net.Http;
using System.Text.Json;
using System.Windows.Threading;

namespace YMusic.Native;

public partial class MainWindow
{
    private Task? _restoredArtworkTask;

    private async Task RestorePlaybackArtworkAsync(IReadOnlyList<TrackRow> restored, string accountUid,
        CancellationToken cancellationToken)
    {
        bool OwnsAccount() => !_closed && !cancellationToken.IsCancellationRequested
            && !_playbackStateInvalidated && _account?.Uid == accountUid
            && _playbackStateAccountUid == accountUid && _session is not null;

        if (!OwnsAccount()) return;
        var groups = restored.Where(row => row.CoverUri is null)
            .Select(row => new
            {
                Row = row,
                Reference = new LikedId(row.Id, row.RequestId == row.Id
                    ? row.AlbumId : row.RequestId[(row.Id.Length + 1)..])
            })
            .GroupBy(item => item.Reference.RequestId, StringComparer.Ordinal)
            .OrderByDescending(group => group.Any(item => ReferenceEquals(item.Row, _current)))
            .Select(group => new
            {
                Rows = group.Select(item => item.Row).ToArray(),
                Reference = group.First().Reference
            }).ToArray();
        try
        {
            foreach (var page in groups.Chunk(100))
            {
                if (!OwnsAccount()) return;
                var liveRows = new HashSet<TrackRow>(_playbackQueue.Items, ReferenceEqualityComparer.Instance);
                var pending = page.Where(group => group.Rows.Any(row => liveRows.Contains(row) && row.CoverUri is null)).ToArray();
                if (pending.Length == 0) continue;
                var session = await EnsureSessionAsync(cancellationToken);
                var metadata = await _api.GetTracksAsync(session, pending.Select(group => group.Reference).ToArray(), 0, cancellationToken);
                if (!OwnsAccount()) return;
                // Queue replacement can happen while either network operation is in flight.
                liveRows = new(_playbackQueue.Items, ReferenceEqualityComparer.Instance);
                var byRequest = metadata.GroupBy(row => row.RequestId, StringComparer.Ordinal)
                    .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
                var currentChanged = false;
                var visited = 0;
                foreach (var group in pending)
                {
                    if (!byRequest.TryGetValue(group.Reference.RequestId, out var source)
                        || group.Reference.AlbumId is not null && source.AlbumId != group.Reference.AlbumId) continue;
                    foreach (var row in group.Rows)
                    {
                        if (!OwnsAccount()) return;
                        if (liveRows.Contains(row) && row.SetMissingArtwork(source.CoverUri)
                            && ReferenceEquals(row, _current)) currentChanged = true;
                        if (++visited % 16 != 0) continue;
                        if (currentChanged) { ApplyRestoredPlaybackArtwork(); currentChanged = false; }
                        await Dispatcher.Yield(DispatcherPriority.Background);
                        if (!OwnsAccount()) return;
                        liveRows = new(_playbackQueue.Items, ReferenceEqualityComparer.Instance);
                    }
                }
                if (currentChanged) ApplyRestoredPlaybackArtwork();
            }
            if (OwnsAccount()) PersistPlaybackStateIfDue();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested || _closed) { }
        catch (Exception error) when (error is MusicException or HttpRequestException or JsonException
            or IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            // Artwork is optional; retain the restored queue, pause, position and
            // retryable snapshot if its metadata is temporarily unavailable.
            if (OwnsAccount()) _diagnostics.LastErrorType = "PlaybackArtwork:" + error.GetType().Name;
        }
    }

    private void ApplyRestoredPlaybackArtwork()
    {
        if (_current is not { } current || _closed) return;
        Artwork.SetSourceUri(PlayerCover, current.CoverUri);
        // Refresh the expanded player without rebuilding the queue or changing
        // its selected occurrence. Queue images observe CoverUri notifications.
        UpdateControls();
    }
}
