using System.Text.Json;

namespace YMusic.Native;

public partial class MainWindow
{
    private PlaybackStateStore? _playbackStateStore;
    private string? _playbackStateAccountUid;
    private long? _restoredPlaybackOccurrence;
    private double? _restoredPlaybackSeconds;
    private DateTimeOffset _lastPlaybackStateSave;
    private bool _playbackStateInvalidated;
    private bool _playbackStateWriteBlocked;
    private bool _playbackStateRestoreUnavailable;
    private Task? _playbackStateWriteTask;

    private bool PlaybackPersistenceEnabled => _restore && !_demo;

    private void InitializePlaybackPersistence()
    {
        if (PlaybackPersistenceEnabled) _playbackStateStore = new PlaybackStateStore(_diagnosticsDirectory);
    }

    private void RestorePlaybackState(string accountUid)
    {
        if (!PlaybackPersistenceEnabled || _playbackStateStore is null || _closed) return;
        // Restoring an already activated account must not rewind a user's queue
        // when the service profile/library is refreshed in the same window.
        var hasLivePlayback = _player.HasSource || _loadingStream || _queue.Count > 0;
        if (_playbackStateAccountUid == accountUid && !_playbackStateInvalidated
            && (!_playbackStateRestoreUnavailable || hasLivePlayback)) return;
        _playbackStateAccountUid = accountUid;
        _playbackStateInvalidated = false;
        _playbackStateWriteBlocked = false;
        _playbackStateRestoreUnavailable = false;
        if (hasLivePlayback) return;
        var snapshot = _playbackStateStore.Load(accountUid);
        if (snapshot is null)
        {
            if (_playbackStateStore.LastReadStatus is PlaybackStateReadStatus.Invalid or PlaybackStateReadStatus.Unavailable)
            {
                _playbackStateWriteBlocked = true;
                _playbackStateRestoreUnavailable = _playbackStateStore.LastReadStatus == PlaybackStateReadStatus.Unavailable;
                _diagnostics.LastErrorType = "PlaybackRestore:" + _playbackStateStore.LastReadStatus;
            }
            return;
        }
        try
        {
            var restored = PlaybackStateStore.RestoreQueue(snapshot, _offline);
            _playbackQueue.RestoreItems(restored.Tracks, restored.CurrentIndex, snapshot.Shuffle, snapshot.Repeat);
            _restoredSourceAlbum = snapshot.SourceAlbum is { } album
                ? (album, _playbackQueue.ReplacementVersion, _accountGeneration, accountUid) : null;
            _current = _playbackQueue.Current;
            _restoredPlaybackOccurrence = _playbackQueue.CurrentOccurrenceToken;
            _restoredPlaybackSeconds = _current is null ? null : restored.PositionSeconds;
            _progressTimer.Stop();
            if (_current is { } current)
            {
                current.IsPlaying = false;
                TrackTitle.Text = current.Title; TrackArtist.Text = current.Artist;
                QualityText.Text = "";
                SetPlayerArtwork(current);
                _diagnostics.Stage = "restored_paused";
                SetStatus("Очередь восстановлена · нажмите «Воспроизвести»");
            }
            RefreshPlaybackView();
            UpdateControls(); UpdateProgress();
            if (_session is not null && restored.Tracks.Any(row => row.CoverUri is null))
                _restoredArtworkTask = RestorePlaybackArtworkAsync(restored.Tracks, accountUid, _accountCts.Token);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or MusicException or JsonException)
        {
            // A corrupt offline index must not turn its unchecked paths into
            // playable files, nor cause the stored queue to be overwritten.
            _playbackStateWriteBlocked = true;
            _diagnostics.LastErrorType = "PlaybackRestore:" + ex.GetType().Name;
        }
    }

    private bool TrySavePlaybackStateForExit()
    {
        if (_playbackStateRestoreUnavailable && _account is { } account) RestorePlaybackState(account.Uid);
        return SavePlaybackState();
    }

    private bool SavePlaybackState()
    {
        if (!PlaybackPersistenceEnabled || _account is null) return true;
        if (_playbackStateStore is null || _playbackStateInvalidated || _playbackStateWriteBlocked
            || _playbackStateAccountUid != _account.Uid) return false;
        try
        {
            _playbackStateStore.Save(CapturePlaybackState(_account.Uid));
            _lastPlaybackStateSave = DateTimeOffset.UtcNow;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _diagnostics.LastErrorType = "PlaybackSave:" + ex.GetType().Name;
            return false;
        }
    }

    private PlaybackSnapshot CapturePlaybackState(string accountUid)
    {
        var seconds = ReferenceEquals(_current, _playbackQueue.Current)
            ? RestoredPlaybackPosition?.TotalSeconds ?? DisplayPosition(_player.Position).TotalSeconds : 0;
        return PlaybackStateStore.Capture(accountUid, _playbackQueue, seconds, PlaybackSourceAlbum());
    }

    // Called by the normal progress timer; close/pause/seek save immediately.
    private void PersistPlaybackStateIfDue()
    {
        if (_closeCommitInProgress || DateTimeOffset.UtcNow - _lastPlaybackStateSave < TimeSpan.FromSeconds(5) || _playbackStateWriteTask is { IsCompleted: false }
            || !PlaybackPersistenceEnabled || _playbackStateStore is null || _playbackStateInvalidated || _playbackStateWriteBlocked
            || _account is null || _playbackStateAccountUid != _account.Uid) return;
        // Validation can fail synchronously before the background write starts.
        // Throttle that failure too; keep the last good snapshot and live queue.
        _lastPlaybackStateSave = DateTimeOffset.UtcNow;
        try
        {
            _playbackStateWriteTask = SaveAsync(CapturePlaybackState(_account.Uid));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        { _diagnostics.LastErrorType = "PlaybackSave:" + ex.GetType().Name; }
        async Task SaveAsync(PlaybackSnapshot snapshot)
        {
            try { await _playbackStateStore.SaveInBackgroundAsync(snapshot); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            { _diagnostics.LastErrorType = "PlaybackSave:" + ex.GetType().Name; }
        }
    }

    private bool ClearPlaybackState()
    {
        _playbackStateInvalidated = true;
        _playbackStateWriteBlocked = false;
        _playbackStateRestoreUnavailable = false;
        _playbackStateAccountUid = null;
        ClearRestoredPosition();
        if (!PlaybackPersistenceEnabled || _playbackStateStore is null) return true;
        try { _playbackStateStore.Clear(); return true; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _diagnostics.LastErrorType = "PlaybackClear:" + ex.GetType().Name;
            SetStatus("Не удалось удалить сохранённую очередь из папки данных.", true);
            return false;
        }
    }

    private bool TryGetRestoredPosition(TrackRow track, out double seconds)
    {
        seconds = 0;
        if (!ReferenceEquals(track, _playbackQueue.Current) || _restoredPlaybackSeconds is not { } saved
            || _restoredPlaybackOccurrence != _playbackQueue.CurrentOccurrenceToken) return false;
        seconds = PlaybackStateStore.ClampPosition(saved, track.DurationMs);
        return true;
    }

    // Open a restored source paused, then seek, then explicitly start it. Restore
    // itself never opens a source, resolves audio, or asks the native player to play.
    private void ApplyRestoredPositionAfterOpen(TrackRow track)
    {
        if (!_player.HasSource || !TryGetRestoredPosition(track, out var seconds)) return;
        var target = Math.Clamp(seconds, 0, Math.Max(0, _player.Duration.TotalSeconds));
        _player.Seek(TimeSpan.FromSeconds(target));
        _pendingSeek = target; _seekClock.Restart();
        ClearRestoredPosition();
    }

    private void ForgetRestoredPositionFor(TrackRow track, bool restart = false)
    {
        _playbackStateWriteBlocked = false; // An explicit new playback can replace an unrestorable queue.
        _playbackStateRestoreUnavailable = false;
        if (!restart && TryGetRestoredPosition(track, out _)) return;
        ClearRestoredPosition();
    }

    private void ClearRestoredPosition()
    {
        _restoredPlaybackOccurrence = null;
        _restoredPlaybackSeconds = null;
    }

    // Native source assignment can precede media readiness and the resume seek.
    private TimeSpan? RestoredPlaybackPosition => _current is { } track && TryGetRestoredPosition(track, out var seconds)
        ? TimeSpan.FromSeconds(seconds) : null;
    private TimeSpan? RestoredPlaybackDuration => RestoredPlaybackPosition.HasValue && _current is { } track
        ? TimeSpan.FromMilliseconds(track.DurationMs) : null;
}
