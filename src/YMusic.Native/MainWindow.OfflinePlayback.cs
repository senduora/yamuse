namespace YMusic.Native;

public partial class MainWindow
{
    private (string TrackId, CancellationTokenSource Request)? _pendingOfflinePlayback;
    private string? _offlineRemovalTrackId;

    private LocalAudioSource? RefreshLocalPlaybackSource(TrackRow track)
    {
        var source = _offline.ResolvePlaybackSource(track);
        var indexed = source is { Bytes: not null } ? source : null;
        ApplyOfflineMarks([new(track, indexed?.Path, indexed?.Quality)]);
        return source;
    }

    private bool CanNavigateToTrack(TrackRow track)
    {
        if (!track.CanPlay) return false;
        if (track.Available && !track.IsOfflineLibraryRow) return true;
        return RefreshLocalPlaybackSource(track) is not null && track.CanPlay;
    }

    private TrackRow? PeekPlayableNext(bool naturalEnd)
    {
        // Updating a stale local mark makes the same queue search skip it
        // without consuming an occurrence or adding it to navigation history.
        while (_playbackQueue.PeekNext(naturalEnd) is { } next)
        {
            if (CanNavigateToTrack(next)) return next;
        }
        return null;
    }

    private bool BlocksOfflineOpen(string trackId) => _offlineMaintenance
        && (_offlineRemovalTrackId is null || _offlineRemovalTrackId == trackId);

    private static bool CanFallbackFromOnlineOpen(Exception error) => error is MusicException or IOException
        or UnauthorizedAccessException or InvalidOperationException or System.Net.Http.HttpRequestException or TimeoutException
        or System.Runtime.InteropServices.COMException or OperationCanceledException;

    private static bool LooksLikePreview(TrackRow track, TimeSpan duration)
        => track.DurationMs > 60_000 && duration.TotalSeconds is > 0 and <= 35;

    private static MusicException PreviewDurationError()
        => new("Длительность потока похожа на фрагмент. Проверьте подписку; воспроизведение остановлено.");
}
