namespace YMusic.Native;

public partial class MainWindow
{
    private CancellationTokenSource? _automaticWaveCts;
    private (PlaybackAlbumState Album, long QueueVersion, int AccountGeneration, string AccountUid)? _restoredSourceAlbum;

    private PlaybackAlbumState? PlaybackSourceAlbum()
    {
        if (HasPlaybackContext && _playbackContext!.Section == "album-detail")
        {
            var source = _playbackContext;
            var id = source.Album?.Id ?? (source.Opened?.Kind == "album" ? source.Opened.Id : null);
            if (MusicApi.IsAlbumWaveSeed(id))
                return new(id!, source.Album?.Title ?? source.Title, source.Album?.Artists ?? source.Opened?.Subtitle ?? "");
        }
        return !_closed && _queue.Count > 0 && _restoredSourceAlbum is { } restored
            && restored.QueueVersion == _playbackQueue.ReplacementVersion
            && restored.AccountGeneration == _accountGeneration && restored.AccountUid == _account?.Uid
            ? restored.Album : null;
    }

    private void CancelPendingWavePlayback(bool preserveQueuedNavigation = false)
    {
        _deferredQueueTrackEnd = null;
        var starting = _automaticTrackStart;
        _automaticTrackStart = null;
        starting?.Request.Cancel();
        if (!preserveQueuedNavigation)
        {
            _manualQueueAdmissionRevision++;
            _manualQueueAdmissionTail = Task.CompletedTask;
        }
        _queueAdmissionCts?.Cancel();
        CancelExplicitWaveStart();
        _waveAdvanceWait?.Cancellation.Cancel();
        var cts = _automaticWaveCts; _automaticWaveCts = null;
        cts?.Cancel();
    }

    // Only a natural end of an exhausted ordinary queue may start a new station.
    // Browsing remains independent; the seed belongs to the playback source.
    private async Task<bool> TryStartAutomaticWaveAsync(HashSet<long> excluded)
    {
        if (_session is null || _current is not { } last || !ReferenceEquals(_playbackQueue.Current, last)
            || _playbackQueue.Repeat != RepeatMode.Off || !CanAutoAdvancePlaybackOptions() || _waveStarting
            || _waveSources.TryGetValue(last, out _) || IsAudioOutputUnavailable) return false;
        var album = PlaybackSourceAlbum();
        if (album is null && !MusicApi.IsTrackWaveSeed(last.Id)) return false;
        var mode = album is null ? WaveMode.Track : WaveMode.Album;
        var account = _accountGeneration; var playback = _playbackGeneration;
        var queue = _playbackQueue.ReplacementVersion; var occurrence = _playbackQueue.CurrentOccurrenceToken;
        var operation = _advanceOperation; var request = ++_waveRequestGeneration;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, _accountCts.Token);
        _automaticWaveCts = cts;
        bool CurrentRequest() => !cts.IsCancellationRequested && !_closed && !_player.IsPlaying && _session is not null
            && ReferenceEquals(_automaticWaveCts, cts)
            && account == _accountGeneration && playback == _playbackGeneration && request == _waveRequestGeneration
            && operation == _advanceOperation && queue == _playbackQueue.ReplacementVersion
            && occurrence == _playbackQueue.CurrentOccurrenceToken && ReferenceEquals(_current, last)
            && ReferenceEquals(_playbackQueue.Current, last) && _playbackQueue.Repeat == RepeatMode.Off
            && CanAutoAdvancePlaybackOptions() && !IsAudioOutputUnavailable;
        bool MayContinue()
        {
            if (!CurrentRequest()) return false;
            if (!HasMinuteSleepExpired()) return true;
            FinishSleepTimer();
            return false;
        }
        async Task<bool> OldQueueIsExhaustedAsync()
        {
            if (!MayContinue()) return false;
            // A manual Next can be verifying an occurrence that appeared while the request was pending.
            await _manualQueueAdmissionTail.WaitAsync(cts.Token);
            if (!MayContinue()) return false;
            // Offline marks can make a row playable without changing the queue version.
            return await ContinueExistingQueueAsync(operation, excluded) == QueueAdvanceResult.Exhausted && MayContinue();
        }
        SetStatus(album is null ? "Продолжаем волну по треку…" : "Продолжаем волну по альбому…");
        UpdateControls();
        WaveStartupAdmission? admission = null;
        try
        {
            MusicApi.WaveCandidate? candidate = null;
            Exception? requestError = null;
            try
            {
                var session = await EnsureSessionAsync(cts.Token);
                candidate = await _api.RequestWaveAsync(session, cts.Token, mode, album is null ? last.Id : null, album?.Id);
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested) { return true; }
            catch (Exception ex) { requestError = ex; }

            if (!await OldQueueIsExhaustedAsync()) return true;
            if (requestError is not null)
            {
                StopCompletedQueue();
                ShowError(requestError);
                return true;
            }
            var playable = WavePlayableTracks(candidate!.Batch.Tracks);
            TrackRow? first = null;
            foreach (var track in playable)
            {
                if (!MayContinue()) return true;
                admission = await AdmitWaveStartupAsync(track, cts);
                if (admission is null) continue;
                if (!await OldQueueIsExhaustedAsync()) return true;
                // The old tail check can outlive permission to open the held station file.
                if (!CanUseWaveStartupAdmission(track, admission))
                {
                    admission.Dispose();
                    admission = null;
                    continue;
                }
                first = track;
                break;
            }
            if (first is null || admission is null)
            {
                if (!await OldQueueIsExhaustedAsync()) return true;
                throw new MusicException("Волна не вернула доступных треков.");
            }
            var batch = _api.AcceptWave(candidate, cts.Token);
            _waveTracks.Clear();
            AddWaveBatch(batch);
            QueueWaveFeedback(batch.SessionId, batch.BatchId, "radioStarted", null, 0);
            _playbackQueue.SetItems(playable, first);
            // Update an already visible wave without changing the user's page or Settings.
            if (_section == "wave")
            {
                _waveViewMode = mode; _waveSeedTrack = album is null ? last : null;
                _waveSeedAlbum = album is null ? null : new(album.Id, "album", album.Title, album.Artist);
            }
            _automaticWaveCts = null;
            await PlayTrackWithSourceAsync(first, null, automatic: true, localAdmission: admission.Local,
                streamOnly: admission.Online is not null, waveFallback: admission.Online);
            if (!_closed && _section == "wave" && IsCurrentWaveView && ReferenceEquals(batch.Station, _waveBatch?.Station))
                ShowRows(_waveTracks);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (MayContinue()) { StopCompletedQueue(); ShowError(ex); }
        }
        finally
        {
            admission?.Dispose();
            if (ReferenceEquals(_automaticWaveCts, cts)) _automaticWaveCts = null;
            UpdateControls();
        }
        return true;
    }

    private void StopCompletedQueue(string status = "Очередь завершена")
    {
        _deferredQueueTrackEnd = null;
        _player.Stop(); _progressTimer.Stop(); if (_current is not null) _current.IsPlaying = false;
        CancelNextPreparation(); ReportWaveEnd(true); HighlightTrack(null);
        _diagnostics.Stage = "library_ready"; SetStatus(status); UpdateControls(); UpdateProgress();
    }
}
