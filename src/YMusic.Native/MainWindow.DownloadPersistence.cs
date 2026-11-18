using System.Text.Json;
using System.Windows.Threading;

namespace YMusic.Native;

public partial class MainWindow
{
    private DownloadQueueStore? _downloadQueueStore;
    private string? _downloadPersistenceAccountUid;
    private bool _downloadPersistenceDirty, _downloadPersistenceInvalidated, _downloadPersistenceClosing;
    private bool _downloadPersistenceWriteBlocked;
    private bool _downloadRestoreRecoveryFailed;
    private Task? _downloadPersistenceWriteTask;
    private (string AccountUid, int Generation, long Revision)? _downloadQueueRestoreAdmission;
    // New work, clear and close supersede an in-flight load/reconciliation.
    private long _downloadQueueAdmissionRevision;
    // Fixture seams hold either asynchronous stage without touching a live profile.
    internal Func<string, Task<DownloadQueueSnapshot?>>? DownloadQueueRestoreReadAsync { get; set; }
    internal Func<DownloadQueueSnapshot, Task<RestoredDownloadQueue>>? DownloadQueueRestorePrepareAsync { get; set; }
    private readonly DispatcherTimer _downloadPersistenceTimer = new() { Interval = TimeSpan.FromSeconds(2) };

    private void InitializeDownloadPersistence()
    {
        if (!_restore || _demo || !_offline.IsAvailable || _downloadQueueStore is not null) return;
        _downloadQueueStore = new(_diagnosticsDirectory);
        _downloadQueue.StateChanged += MarkDownloadQueueDirty;
        _downloadPersistenceTimer.Tick += async (_, _) => await PersistDownloadQueueAsync();
        _downloadPersistenceTimer.Start();
    }
    private void MarkDownloadQueueDirty()
    {
        if (!_downloadPersistenceClosing && !_downloadPersistenceInvalidated) _downloadPersistenceDirty = true;
    }
    private void BeginDownloadQueuePersistence()
    {
        ++_downloadQueueAdmissionRevision;
        if (_downloadQueueStore is null || _downloadPersistenceClosing) return;
        _downloadPersistenceAccountUid = _account?.Uid;
        _downloadPersistenceInvalidated = false; _downloadPersistenceWriteBlocked = false; _downloadRestoreRecoveryFailed = false;
        _downloadPersistenceDirty = true;
    }
    private bool DownloadQueueRestorePending => _downloadQueueRestoreAdmission is { } restore
        && restore.AccountUid == _account?.Uid && restore.Generation == _accountGeneration
        && restore.Revision == _downloadQueueAdmissionRevision;

    private bool AppUpdateMustWaitForDownloadRestore => DownloadQueueRestorePending
        || (_downloadQueueStore is not null && _account is { } account
            && (_downloadQueueAccountUid != account.Uid || _downloadQueueAccountGeneration != _accountGeneration)
            && !_downloadPersistenceInvalidated && !_downloadPersistenceWriteBlocked);
    private bool DownloadQueueRestoreCanRetry => _downloadRestoreRecoveryFailed && !_downloadPersistenceInvalidated
        && _account is { } account && _downloadPersistenceAccountUid == account.Uid;
    private Task RetryDownloadQueueRestoreAsync() => DownloadQueueRestoreCanRetry && _account is { } account
        && _session is not null && !_closed && !_offlineMaintenance && !_closeCommitInProgress && !_appUpdatePreparingRestart
        ? RestoreDownloadQueueAsync(account.Uid) : Task.CompletedTask;

    private async Task RestoreDownloadQueueAsync(string accountUid)
    {
        var admissionRevision = _downloadQueueAdmissionRevision;
        if (_downloadQueueStore is not { } store || _closed || _downloadPersistenceClosing) return;
        var generation = _accountGeneration;
        if (_account?.Uid != accountUid || DownloadQueueRestorePending
            || (!DownloadQueueRestoreCanRetry && _downloadPersistenceAccountUid == accountUid && !_downloadPersistenceInvalidated
                && (_downloadPersistenceWriteBlocked || _downloadQueueAccountUid == accountUid && _downloadQueueAccountGeneration == generation))) return;
        bool IsCurrentAdmission() => !_closed && !_downloadPersistenceClosing && _account?.Uid == accountUid
            && generation == _accountGeneration && admissionRevision == _downloadQueueAdmissionRevision;
        var restore = (AccountUid: accountUid, Generation: generation, Revision: admissionRevision);
        _downloadQueueRestoreAdmission = restore;
        var recoveryToken = _accountCts.Token;
        var recoveringOffline = false;
        try
        {
            // The previous account's workers still own the shared queue while finishing.
            if (_downloadTask is { } pending) await pending;
            if (!IsCurrentAdmission()) return;
            _downloadPersistenceAccountUid = accountUid;
            _downloadPersistenceInvalidated = false; _downloadPersistenceWriteBlocked = false; _downloadRestoreRecoveryFailed = false;
            var snapshot = await (DownloadQueueRestoreReadAsync?.Invoke(accountUid) ?? Task.Run(() => store.Load(accountUid)));
            if (!IsCurrentAdmission() || _downloadPersistenceInvalidated) return;
            if (snapshot is null)
            {
                if (store.LastReadStatus is DownloadQueueReadStatus.Invalid or DownloadQueueReadStatus.Unavailable)
                    _downloadPersistenceWriteBlocked = true;
                if (_downloadQueueAccountUid != accountUid || _downloadQueueAccountGeneration != generation)
                {
                    _downloadQueue.Replace([], DownloadQueueState.EmptyLabel);
                    _downloadQueuePlaylist = null; _downloadQueuePlaylistHistoryOnly = false;
                }
                _downloadQueueAccountUid = accountUid; _downloadQueueAccountGeneration = generation;
                _downloadsAutoSuspended = _downloadPersistenceWriteBlocked;
                _downloadPersistenceDirty = false;
                RefreshDownloadCenter(); UpdatePlaylistDownloadedStatus();
                return;
            }
            recoveringOffline = true;
            await _offline.RecoverPendingAsync(recoveryToken);
            recoveringOffline = false;
            if (!IsCurrentAdmission() || _downloadPersistenceInvalidated) return;
            var restored = await (DownloadQueueRestorePrepareAsync?.Invoke(snapshot)
                ?? Task.Run(() => DownloadQueueStore.PrepareRestore(snapshot, _offline)));
            if (!IsCurrentAdmission() || _downloadPersistenceInvalidated) return;
            _downloadQueue.Restore(restored);
            _downloadQueuePlaylist = restored.Playlist; _downloadQueueQuality = restored.Quality;
            _downloadQueuePlaylistHistoryOnly = restored.PlaylistHistoryOnly;
            if (!_autoFavoritesEnabled) RemoveUnstartedAutomaticFavorites();
            _downloadQueueAutomatic = restored.Automatic;
            _downloadQueueAccountUid = accountUid; _downloadQueueAccountGeneration = generation;
            ReconcileDownloadQueueWithVerifiedFavorites();
            // Restored paused entries gate automatic work themselves. If a
            // verified refresh excludes them, the gate can disappear naturally.
            _downloadsAutoSuspended = false;
            _downloadPersistenceDirty = true;
            RefreshDownloadCenter(); UpdatePlaylistDownloadedStatus();
            if (_downloadQueue.Entries.Count > 0) SetStatus(_downloadQueue.CanResume
                ? "Очередь загрузок восстановлена на паузе. Продолжите её в «Центре загрузок»."
                : _downloadQueue.CanRetry ? "История загрузок восстановлена. Неудачные загрузки можно повторить в «Центре загрузок»."
                : "История загрузок восстановлена.");
        }
        catch (OperationCanceledException) when (recoveryToken.IsCancellationRequested || !IsCurrentAdmission()) { }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or MusicException or JsonException or System.Security.Cryptography.CryptographicException)
        {
            if (!IsCurrentAdmission() || _downloadPersistenceInvalidated) return;
            _downloadPersistenceWriteBlocked = true; _downloadsAutoSuspended = true;
            _downloadRestoreRecoveryFailed = recoveringOffline && error is IOException;
            if (!_downloadRestoreRecoveryFailed) ClearPendingAutoFavorites();
            _diagnostics.LastErrorType = "DownloadRestore:" + error.GetType().Name;
        }
        finally
        {
            if (_downloadQueueRestoreAdmission is { } current && current == restore)
            {
                _downloadQueueRestoreAdmission = null;
                if (IsCurrentAdmission() && !_downloadPersistenceInvalidated) TryStartPendingAutoFavorites();
            }
        }
    }
    private bool CanPersistDownloadQueue => _downloadQueueStore is not null && !_downloadPersistenceClosing
        && !_downloadPersistenceInvalidated && !_downloadPersistenceWriteBlocked && !_downloadQueue.Suspended
        && _account is not null && _downloadPersistenceAccountUid == _account.Uid
        && _downloadQueueAccountUid == _account.Uid && _downloadQueueAccountGeneration == _accountGeneration;

    private async Task PersistDownloadQueueAsync(bool force = false)
    {
        if (!CanPersistDownloadQueue || !force && (_closeCommitInProgress || !_downloadPersistenceDirty
            || _downloadPersistenceWriteTask is { IsCompleted: false })) return;
        var admissionRevision = _downloadQueueAdmissionRevision;
        _downloadPersistenceDirty = false;
        try
        {
            var snapshot = CaptureDownloadQueue();
            var task = _downloadQueueStore!.SaveInBackgroundAsync(snapshot);
            _downloadPersistenceWriteTask = task;
            await task;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            if (error is not JsonException && admissionRevision == _downloadQueueAdmissionRevision && CanPersistDownloadQueue)
                _downloadPersistenceDirty = true; // The next checkpoint can retry a transient disk failure.
            _diagnostics.LastErrorType = "DownloadSave:" + error.GetType().Name;
        }
    }
    private void SaveDownloadQueueForClose()
    {
        _downloadPersistenceTimer.Stop();
        if (_downloadPersistenceClosing) return;
        ++_downloadQueueAdmissionRevision;
        try { if (!_closeCommitApproved) TrySaveDownloadQueueCheckpoint(); }
        finally { _downloadPersistenceClosing = true; }
    }
    private bool TrySaveDownloadQueueCheckpoint()
    {
        if (!CanPersistDownloadQueue) return true;
        try
        {
            // Active and queued entries are restored paused by the store.
            _downloadQueueStore!.Save(CaptureDownloadQueue());
            _downloadPersistenceDirty = false;
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            _diagnostics.LastErrorType = "DownloadSave:" + error.GetType().Name;
            return false;
        }
    }
    private DownloadQueueSnapshot CaptureDownloadQueue() => DownloadQueueStore.Capture(_account!.Uid, _downloadQueue,
        _downloadQueueQuality, _downloadQueueAutomatic, _downloadQueuePlaylist, _downloadQueuePlaylistHistoryOnly);
    private bool ClearPersistedDownloadQueue(bool invalidateOnFailure)
    {
        if (_downloadPersistenceClosing)
        {
            ++_downloadQueueAdmissionRevision;
            return true; // Closing saved its paused snapshot first.
        }
        try { _downloadQueueStore?.Clear(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            if (invalidateOnFailure) InvalidateDownloadQueueCheckpoint();
            _diagnostics.LastErrorType = "DownloadClear:" + error.GetType().Name;
            SetStatus("Не удалось удалить сохранённую очередь загрузок. Проверьте доступ к папке данных.", true);
            return false;
        }
        InvalidateDownloadQueueCheckpoint();
        return true;
    }
    private void InvalidateDownloadQueueCheckpoint()
    {
        ++_downloadQueueAdmissionRevision;
        _downloadPersistenceInvalidated = true; _downloadPersistenceDirty = false;
        _downloadPersistenceAccountUid = null; _downloadPersistenceWriteBlocked = false; _downloadRestoreRecoveryFailed = false;
    }
}
