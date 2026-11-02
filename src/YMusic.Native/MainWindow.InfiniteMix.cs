namespace YMusic.Native;

public partial class MainWindow
{
    private CancellationTokenSource? _automaticWaveCts;
    private PlaybackAlbumState? _restoredSourceAlbum;
    private IReadOnlyList<TrackRow>? _restoredSourceAlbumQueue;

    private PlaybackAlbumState? PlaybackSourceAlbum()
    {
        if (HasPlaybackContext && _playbackContext!.Section == "album-detail")
        {
            var source = _playbackContext;
            var id = source.Album?.Id ?? (source.Opened?.Kind == "album" ? source.Opened.Id : null);
            if (MusicApi.IsAlbumWaveSeed(id))
                return new(id!, source.Album?.Title ?? source.Title, source.Album?.Artists ?? source.Opened?.Subtitle ?? "");
        }
        return ReferenceEquals(_restoredSourceAlbumQueue, _playbackQueue.Items) ? _restoredSourceAlbum : null;
    }

    private void CancelAutomaticWave()
    {
        var cts = _automaticWaveCts; _automaticWaveCts = null;
        cts?.Cancel();
    }

    // Only a natural end of an exhausted ordinary queue may start a new station.
    // Browsing remains independent; the seed belongs to the playback source.
    private async Task<bool> TryStartAutomaticWaveAsync()
    {
        if (_session is null || _current is not { } last || !ReferenceEquals(_playbackQueue.Current, last)
            || _playbackQueue.Repeat != RepeatMode.Off || !CanAutoAdvancePlaybackOptions() || _waveStarting
            || _waveSources.ContainsKey(last) || _audioOutputUnavailable) return false;
        var album = PlaybackSourceAlbum();
        if (album is null && !MusicApi.IsTrackWaveSeed(last.Id)) return false;
        var mode = album is null ? WaveMode.Track : WaveMode.Album;
        var account = _accountGeneration; var playback = _playbackGeneration;
        var version = _playbackQueue.Version; var occurrence = _playbackQueue.CurrentOccurrenceToken;
        var operation = _advanceOperation; var request = ++_waveRequestGeneration;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, _accountCts.Token);
        _automaticWaveCts = cts;
        bool CurrentRequest() => !cts.IsCancellationRequested && !_closed && _session is not null
            && account == _accountGeneration && playback == _playbackGeneration && request == _waveRequestGeneration
            && operation == _advanceOperation && version == _playbackQueue.Version
            && occurrence == _playbackQueue.CurrentOccurrenceToken && ReferenceEquals(_current, last)
            && ReferenceEquals(_playbackQueue.Current, last) && _playbackQueue.Repeat == RepeatMode.Off
            && CanAutoAdvancePlaybackOptions() && !_audioOutputUnavailable;
        SetStatus(album is null ? "Продолжаем волну по треку…" : "Продолжаем волну по альбому…");
        UpdateControls();
        try
        {
            var session = await EnsureSessionAsync(cts.Token);
            var batch = await _api.StartWaveAsync(session, cts.Token, mode, album is null ? last.Id : null, album?.Id);
            if (!CurrentRequest()) return true;
            if (!batch.Tracks.Any(track => track.CanPlay)) throw new MusicException("Волна не вернула доступных треков.");
            _waveTracks.Clear();
            AddWaveBatch(batch);
            QueueWaveFeedback(batch.SessionId, batch.BatchId, "radioStarted", null, 0);
            _playbackQueue.SetItems(batch.Tracks);
            // Update an already visible wave without changing the user's page or Settings.
            if (_section == "wave")
            {
                _waveViewMode = mode; _waveSeedTrack = album is null ? last : null;
                _waveSeedAlbum = album is null ? null : new(album.Id, "album", album.Title, album.Artist);
                ShowRows(_waveTracks);
            }
            _automaticWaveCts = null;
            if (_playbackQueue.Current is { } first) await PlayTrackAsync(first);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (CurrentRequest()) { StopCompletedQueue(); ShowError(ex); }
        }
        finally
        {
            if (ReferenceEquals(_automaticWaveCts, cts)) _automaticWaveCts = null;
            UpdateControls();
        }
        return true;
    }

    private void StopCompletedQueue()
    {
        _player.Stop(); _progressTimer.Stop(); if (_current is not null) _current.IsPlaying = false;
        CancelNextPreparation(); ReportWaveEnd(true); HighlightTrack(null);
        _diagnostics.Stage = "library_ready"; SetStatus("Очередь завершена"); UpdateControls(); UpdateProgress();
    }
}
