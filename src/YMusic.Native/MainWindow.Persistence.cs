using System.ComponentModel;
using System.Security.Cryptography;
using System.Text.Json;

namespace YMusic.Native;

public partial class MainWindow
{
    private PlaybackStateStore? _playbackStateStore;
    private string? _playbackStateAccountUid;
    private long? _restoredPlaybackOccurrence;
    private double? _restoredPlaybackSeconds;
    private TimeSpan? _restoredPlaybackNativeDuration;
    private (long Occurrence, long Queue, int Playback, int Account, TimeSpan Duration)? _observedPlaybackDuration;
    private DateTimeOffset _lastPlaybackStateSave;
    private bool _playbackStateInvalidated;
    private bool _playbackStateWriteBlocked;
    private bool _playbackStateRestoreUnavailable;
    private bool _playbackStateOfflineRestoreUnavailable;
    private bool _untouchedForeignPlaybackState;
    private long _playbackStateRestoreItemsVersion;
    private bool? _pendingRestoredShuffle;
    private RepeatMode? _pendingRestoredRepeat;
    private PlaybackStateStore.ClearRetry? _pendingPlaybackStateClear;
    private Task? _playbackStateWriteTask;
    private TaskCompletionSource? _playbackRestoreAdmission;

    private bool PlaybackPersistenceEnabled => _restore && !_demo;
    private string PlaybackStateSaveFailure => _pendingPlaybackStateClear is null
        ? "Не удалось сохранить очередь воспроизведения и позицию."
        : "Не удалось удалить очередь предыдущего входа. Проверьте доступ к файлу и повторите попытку.";

    private void InitializePlaybackPersistence()
    {
        if (PlaybackPersistenceEnabled) _playbackStateStore = new PlaybackStateStore(_diagnosticsDirectory);
    }

    private void RestorePlaybackState(string accountUid)
    {
        if (!PlaybackPersistenceEnabled || _playbackStateStore is null || _closed) return;
        var sameAccount = _playbackStateAccountUid == accountUid && !_playbackStateInvalidated;
        if (!sameAccount)
        {
            _playbackStateRestoreItemsVersion = _playbackQueue.ItemsVersion;
            _pendingRestoredShuffle = null;
            _pendingRestoredRepeat = null;
            _playbackStateOfflineRestoreUnavailable = false;
            _untouchedForeignPlaybackState = false;
        }
        if (!TryFlushPendingPlaybackStateClear())
        {
            _playbackStateAccountUid = accountUid;
            _playbackStateInvalidated = false;
            _playbackStateWriteBlocked = true;
            _playbackStateRestoreUnavailable = true;
            return;
        }
        // Restoring an already activated account must not rewind a user's queue
        // when the service profile/library is refreshed in the same window.
        var hasLivePlayback = HasLiveQueueForRestore;
        if (sameAccount && (!_playbackStateRestoreUnavailable || hasLivePlayback)) return;
        _playbackStateAccountUid = accountUid;
        _playbackStateInvalidated = false;
        _playbackStateWriteBlocked = false;
        _playbackStateRestoreUnavailable = false;
        _playbackStateOfflineRestoreUnavailable = false;
        if (hasLivePlayback) return;
        var snapshot = _playbackStateStore.Load(accountUid);
        if (snapshot is null)
        {
            _untouchedForeignPlaybackState = _playbackStateStore.LastReadStatus == PlaybackStateReadStatus.OtherAccount;
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
            var shuffle = _pendingRestoredShuffle ?? snapshot.Shuffle;
            _playbackQueue.RestoreItems(restored.Tracks, restored.CurrentIndex, shuffle, _pendingRestoredRepeat ?? snapshot.Repeat,
                shuffle ? restored.PlayNextIndices : null);
            var artwork = new RestoredArtworkRequest(restored.Tracks, accountUid, _accountGeneration,
                _playbackQueue.ReplacementVersion, _accountCts.Token);
            _pendingRestoredShuffle = null;
            _pendingRestoredRepeat = null;
            _restoredSourceAlbum = snapshot.SourceAlbum is { } album
                ? (album, _playbackQueue.ReplacementVersion, _accountGeneration, accountUid) : null;
            _current = _playbackQueue.Current;
            _restoredPlaybackOccurrence = _playbackQueue.CurrentOccurrenceToken;
            _restoredPlaybackSeconds = _current is null ? null : restored.PositionSeconds;
            _restoredPlaybackNativeDuration = restored.PositionDurationMs is { } duration ? TimeSpan.FromMilliseconds(duration) : null;
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
            RequestRestoredPlaybackArtwork(artwork);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or MusicException or JsonException or Win32Exception)
        {
            // A corrupt offline index must not turn its unchecked paths into
            // playable files, nor cause the stored queue to be overwritten.
            _playbackStateWriteBlocked = true;
            _playbackStateRestoreUnavailable = ex is MusicException { OfflineStorageRetryable: true }
                or UnauthorizedAccessException or FileNotFoundException or DirectoryNotFoundException
                or Win32Exception { NativeErrorCode: 2 or 3 or 5 or 32 or 33 }
                or IOException { InnerException: Win32Exception { NativeErrorCode: 2 or 3 or 5 or 32 or 33 } }
                or IOException { HResult: unchecked((int)0x80070020) or unchecked((int)0x80070021) };
            _playbackStateOfflineRestoreUnavailable = _playbackStateRestoreUnavailable;
            _diagnostics.LastErrorType = "PlaybackRestore:" + ex.GetType().Name;
        }
    }

    private bool HasLiveQueueForRestore => _player.HasSource || _loadingStream || _queue.Count > 0
        || _playbackStateRestoreUnavailable && _playbackStateRestoreItemsVersion != _playbackQueue.ItemsVersion;

    private bool PreserveForeignPlaybackCheckpoint => _untouchedForeignPlaybackState && !HasLiveQueueForRestore
        && _playbackStateRestoreItemsVersion == _playbackQueue.ItemsVersion;

    private bool PlaybackRestoreAdmissionPaused => _closeCommitInProgress || _appUpdatePreparingRestart || _fullUninstallCommitting;

    private async Task<bool> WaitForPlaybackRestoreAdmissionAsync(CancellationToken token)
    {
        while (PlaybackRestoreAdmissionPaused && !SessionIsExiting)
            await (_playbackRestoreAdmission ??= new(TaskCreationOptions.RunContinuationsAsynchronously)).Task.WaitAsync(token);
        return !SessionWriteAdmissionClosed;
    }

    private async Task RetryPlaybackRestoreAfterRecoveryAsync()
    {
        if (_account is not { } account) return;
        var generation = _accountGeneration;
        var token = _accountCts.Token;
        var queueGeneration = _playbackQueue.ReplacementVersion;
        var itemsVersion = _playbackQueue.ItemsVersion;
        bool OwnsRecovery() => !SessionIsExiting && !token.IsCancellationRequested && token == _accountCts.Token
            && generation == _accountGeneration && _account?.Uid == account.Uid && _playbackStateAccountUid == account.Uid
            && !_playbackStateInvalidated && _playbackStateRestoreUnavailable;
        bool OwnsRetry() => OwnsRecovery() && queueGeneration == _playbackQueue.ReplacementVersion
            && itemsVersion == _playbackQueue.ItemsVersion && !HasLiveQueueForRestore;
        if (!OwnsRecovery()) return;
        try
        {
            if (!await WaitForPlaybackRestoreAdmissionAsync(token) || !OwnsRecovery()) return;
            try { await _offline.RecoverPendingAsync(token); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or MusicException or CryptographicException or JsonException) { }
            if (OwnsRetry() && await WaitForPlaybackRestoreAdmissionAsync(token) && OwnsRetry())
                RestorePlaybackState(account.Uid);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
    }

    private bool TrySavePlaybackStateForExit() => SavePlaybackCheckpointOnExit(restoreUntouchedQueue: true);

    private bool SavePlaybackCheckpointOnExit(bool restoreUntouchedQueue)
    {
        if (_closed || _lifetime.IsCancellationRequested || !TryFlushPendingPlaybackStateClear()) return false;
        if (_playbackStateRestoreUnavailable && _account is { } account)
        {
            if (_accountCts.IsCancellationRequested || _playbackStateAccountUid != account.Uid) return false;
            if (HasLiveQueueForRestore) return SavePlaybackStateCore(verifyRestoreAccess: true);
            if (restoreUntouchedQueue) RestorePlaybackState(account.Uid);
        }
        return SavePlaybackState();
    }

    private bool SavePlaybackState() => SavePlaybackStateCore(verifyRestoreAccess: false);

    private bool SavePlaybackStateCore(bool verifyRestoreAccess)
    {
        if (!PlaybackPersistenceEnabled || _account is null) return true;
        if (_playbackStateStore is not { } store || _playbackStateInvalidated || _playbackStateWriteBlocked && !verifyRestoreAccess
            || _playbackStateAccountUid != _account.Uid) return false;
        if (PreserveForeignPlaybackCheckpoint) return true;
        try
        {
            var current = CapturePlaybackState(_account.Uid);
            if (verifyRestoreAccess)
            {
                // An edited queue owns the next checkpoint. Recheck the blocked
                // reads without applying the previously saved queue or position.
                if (_playbackStateOfflineRestoreUnavailable) _ = _offline.Read();
                store.SaveVerifiedCheckpoint(current);
                _playbackStateWriteBlocked = _playbackStateRestoreUnavailable = _playbackStateOfflineRestoreUnavailable = false;
            }
            else store.Save(current);
            _untouchedForeignPlaybackState = false;
            _pendingPlaybackStateClear = null;
            _lastPlaybackStateSave = DateTimeOffset.UtcNow;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or MusicException or Win32Exception)
        {
            _diagnostics.LastErrorType = "PlaybackSave:" + ex.GetType().Name;
            return false;
        }
    }

    private bool OwnsCheckpointOccurrence => _current is not null && ReferenceEquals(_current, _playbackQueue.Current);

    private void RememberPlaybackDuration()
    {
        if (!OwnsCheckpointOccurrence || !_player.HasSource || _playbackQueue.CurrentOccurrenceToken is not { } occurrence) return;
        var duration = _player.Duration;
        if (duration > TimeSpan.Zero)
            _observedPlaybackDuration = (occurrence, _playbackQueue.ReplacementVersion, _playbackGeneration, _accountGeneration, duration);
    }

    private TimeSpan? ObservedPlaybackDuration => _observedPlaybackDuration is { } measured
        && measured.Occurrence == _playbackQueue.CurrentOccurrenceToken && measured.Queue == _playbackQueue.ReplacementVersion
        && measured.Playback == _playbackGeneration && measured.Account == _accountGeneration
        ? measured.Duration : null;

    private PlaybackSnapshot CapturePlaybackState(string accountUid)
    {
        var ownsOccurrence = OwnsCheckpointOccurrence;
        var restored = ownsOccurrence ? RestoredPlaybackPosition : null;
        var seconds = ownsOccurrence ? restored?.TotalSeconds ?? DisplayPosition(_player.Position).TotalSeconds : 0;
        var duration = restored.HasValue ? _restoredPlaybackNativeDuration : ownsOccurrence ? ObservedPlaybackDuration : null;
        return PlaybackStateStore.Capture(accountUid, _playbackQueue, seconds, PlaybackSourceAlbum(),
            duration is { } native && _playbackQueue.CurrentOccurrenceToken is { } occurrence ? (occurrence, native) : null);
    }

    // Called by the normal progress timer; close/pause/seek save immediately.
    private void PersistPlaybackStateIfDue() => QueuePlaybackStateSave(force: false);

    private void QueuePlaybackStateSave(bool force)
    {
        if (_closeCommitInProgress || (!force && DateTimeOffset.UtcNow - _lastPlaybackStateSave < TimeSpan.FromSeconds(5))
            || _playbackStateWriteTask is { IsCompleted: false }
            || !PlaybackPersistenceEnabled || _playbackStateStore is null || _playbackStateInvalidated || _playbackStateWriteBlocked || _pendingPlaybackStateClear is not null
            || _account is null || _playbackStateAccountUid != _account.Uid || PreserveForeignPlaybackCheckpoint) return;
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

    private bool ClearPlaybackState(string? accountUid)
    {
        _playbackStateInvalidated = true;
        _playbackStateWriteBlocked = false;
        _playbackStateRestoreUnavailable = false;
        _playbackStateOfflineRestoreUnavailable = false;
        _untouchedForeignPlaybackState = false;
        _playbackStateAccountUid = null;
        _pendingRestoredShuffle = null;
        _pendingRestoredRepeat = null;
        ClearRestoredPosition();
        if (!PlaybackPersistenceEnabled || _playbackStateStore is null) return true;
        if (accountUid is null) return _pendingPlaybackStateClear is null;
        if (_pendingPlaybackStateClear is { } pending && pending.AccountUid != accountUid && !TryFlushPendingPlaybackStateClear())
            return false;
        try { _playbackStateStore.Clear(accountUid, out _pendingPlaybackStateClear); return true; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _diagnostics.LastErrorType = "PlaybackClear:" + ex.GetType().Name;
            return false;
        }
    }

    private bool TryFlushPendingPlaybackStateClear()
    {
        if (_pendingPlaybackStateClear is not { } pending || _playbackStateStore is not { } store) return true;
        try
        {
            if (!store.RetryClear(pending)) return false;
            _pendingPlaybackStateClear = null;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _diagnostics.LastErrorType = "PlaybackClear:" + ex.GetType().Name;
            return false;
        }
    }

    private bool TryGetRestoredPosition(TrackRow track, out double seconds)
    {
        seconds = 0;
        if (!ReferenceEquals(track, _playbackQueue.Current) || _restoredPlaybackSeconds is not { } saved
            || _restoredPlaybackOccurrence != _playbackQueue.CurrentOccurrenceToken) return false;
        seconds = _restoredPlaybackNativeDuration is { } native
            ? Math.Clamp(saved, 0, native.TotalSeconds) : PlaybackStateStore.ClampPosition(saved, track.DurationMs);
        return true;
    }

    // Open a restored source paused, then seek, then explicitly start it. Restore
    // itself never opens a source, resolves audio, or asks the native player to play.
    private void ApplyRestoredPositionAfterOpen(TrackRow track)
    {
        if (!_player.HasSource || !TryGetRestoredPosition(track, out var seconds)) return;
        var target = Math.Clamp(seconds, 0, Math.Max(0, _player.Duration.TotalSeconds));
        if (!_player.Seek(TimeSpan.FromSeconds(target)))
            throw new MusicException("Windows не удалось восстановить позицию трека. Попробуйте ещё раз.");
        _pendingSeek = target; _seekClock.Restart();
    }

    private void ForgetRestoredPositionFor(TrackRow track, bool restart = false)
    {
        if (!restart && TryGetRestoredPosition(track, out _)) return;
        ClearRestoredPosition();
    }

    private void ClearRestoredPosition()
    {
        _restoredPlaybackOccurrence = null;
        _restoredPlaybackSeconds = null;
        _restoredPlaybackNativeDuration = null;
    }

    // Native source assignment can precede media readiness and the resume seek.
    private TimeSpan? RestoredPlaybackPosition => _current is { } track && TryGetRestoredPosition(track, out var seconds)
        ? TimeSpan.FromSeconds(seconds) : null;
    private TimeSpan? RestoredPlaybackDuration => RestoredPlaybackPosition.HasValue && _current is { } track
        ? _restoredPlaybackNativeDuration ?? TimeSpan.FromMilliseconds(track.DurationMs) : null;
}
