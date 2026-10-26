using System.Text.Json;

namespace YMusic.Native;

public partial class MainWindow
{
    private PlaybackStateStore? _playbackStateStore;
    private string? _playbackStateAccountUid;
    private TrackRow? _restoredPlaybackTrack;
    private double? _restoredPlaybackSeconds;
    private DateTimeOffset _lastPlaybackStateSave;
    private bool _playbackStateInvalidated;
    private bool _playbackStateWriteBlocked;
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
        if (_playbackStateAccountUid == accountUid && !_playbackStateInvalidated) return;
        _playbackStateAccountUid = accountUid;
        _playbackStateInvalidated = false;
        _playbackStateWriteBlocked = false;
        if (_player.HasSource || _loadingStream || _queue.Count > 0) return;
        var snapshot = _playbackStateStore.Load(accountUid);
        if (snapshot is null) return;
        try
        {
            var restored = PlaybackStateStore.RestoreQueue(snapshot, _offline);
            _playbackQueue.RestoreItems(restored.Tracks, restored.CurrentIndex, snapshot.Shuffle, snapshot.Repeat);
            _current = _playbackQueue.Current;
            _restoredPlaybackTrack = _current;
            _restoredPlaybackSeconds = _current is null ? null : restored.PositionSeconds;
            _progressTimer.Stop();
            if (_current is { } current)
            {
                current.IsPlaying = false;
                TrackTitle.Text = current.Title; TrackArtist.Text = current.Artist;
                QualityText.Text = "";
                Artwork.SetSourceUri(PlayerCover, null);
                _diagnostics.Stage = "restored_paused";
                SetStatus("Очередь восстановлена · нажмите «Воспроизвести»");
            }
            RefreshPlaybackView();
            UpdateControls(); UpdateProgress();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or MusicException or JsonException)
        {
            // A corrupt offline index must not turn its unchecked paths into
            // playable files, nor cause the stored queue to be overwritten.
            _playbackStateWriteBlocked = true;
            _diagnostics.LastErrorType = "PlaybackRestore:" + ex.GetType().Name;
        }
    }

    private void SavePlaybackState()
    {
        if (!PlaybackPersistenceEnabled || _playbackStateStore is null || _playbackStateInvalidated || _playbackStateWriteBlocked
            || _account is null || _playbackStateAccountUid != _account.Uid) return;
        try
        {
            var seconds = ReferenceEquals(_current, _playbackQueue.Current)
                ? RestoredPlaybackPosition?.TotalSeconds ?? DisplayPosition(_player.Position).TotalSeconds : 0;
            _playbackStateStore.Save(PlaybackStateStore.Capture(_account.Uid, _playbackQueue, seconds));
            _lastPlaybackStateSave = DateTimeOffset.UtcNow;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        { _diagnostics.LastErrorType = "PlaybackSave:" + ex.GetType().Name; }
    }

    // Called by the normal progress timer; close/pause/seek save immediately.
    private void PersistPlaybackStateIfDue()
    {
        if (DateTimeOffset.UtcNow - _lastPlaybackStateSave < TimeSpan.FromSeconds(5) || _playbackStateWriteTask is { IsCompleted: false }
            || !PlaybackPersistenceEnabled || _playbackStateStore is null || _playbackStateInvalidated || _playbackStateWriteBlocked
            || _account is null || _playbackStateAccountUid != _account.Uid) return;
        // Validation can fail synchronously before the background write starts.
        // Throttle that failure too; keep the last good snapshot and live queue.
        _lastPlaybackStateSave = DateTimeOffset.UtcNow;
        try
        {
            var seconds = RestoredPlaybackPosition?.TotalSeconds ?? DisplayPosition(_player.Position).TotalSeconds;
            var snapshot = PlaybackStateStore.Capture(_account.Uid, _playbackQueue, seconds);
            _playbackStateWriteTask = SaveAsync(snapshot);
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
        _playbackStateAccountUid = null;
        _restoredPlaybackTrack = null; _restoredPlaybackSeconds = null;
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
        if (!ReferenceEquals(track, _restoredPlaybackTrack) || _restoredPlaybackSeconds is not { } saved) return false;
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
        _restoredPlaybackTrack = null; _restoredPlaybackSeconds = null;
    }

    private void ForgetRestoredPositionFor(TrackRow track, bool restart = false)
    {
        _playbackStateWriteBlocked = false; // An explicit new playback can replace an unrestorable queue.
        if (!restart && ReferenceEquals(track, _restoredPlaybackTrack)) return;
        _restoredPlaybackTrack = null; _restoredPlaybackSeconds = null;
    }

    private TimeSpan? RestoredPlaybackPosition => !_player.HasSource && _current is { } track && TryGetRestoredPosition(track, out var seconds)
        ? TimeSpan.FromSeconds(seconds) : null;
    private TimeSpan? RestoredPlaybackDuration => RestoredPlaybackPosition.HasValue && _current is { } track
        ? TimeSpan.FromMilliseconds(track.DurationMs) : null;
}
