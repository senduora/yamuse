using System.Text.Json;
using System.Windows.Threading;

namespace YMusic.Native;

public partial class MainWindow
{
    private DownloadQueueStore? _downloadQueueStore;
    private string? _downloadPersistenceAccountUid;
    private bool _downloadPersistenceDirty, _downloadPersistenceInvalidated, _downloadPersistenceClosing;
    private bool _downloadPersistenceWriteBlocked;
    private bool _downloadRestoreNeedsRetry;
    private bool _preserveUnreadableDownloadCheckpoint;
    private bool _downloadPlaylistIndexDamaged;
    private DownloadQueueStore.ProtectedPublication? _downloadCheckpointProtection;
    private bool _downloadCheckpointExplicitBatch;
    private Dictionary<string, DownloadQueueStore.ClearRetry>? _pendingDownloadQueueClears;
    private Task? _downloadPersistenceWriteTask;
    private (string AccountUid, int Generation, long Revision)? _downloadQueueRestoreAdmission;
    // New work, clear and close supersede an in-flight load/reconciliation.
    private long _downloadQueueAdmissionRevision;
    // Fixture seams hold either asynchronous stage without touching a live profile.
    internal Func<string, Task<DownloadQueueSnapshot?>>? DownloadQueueRestoreReadAsync { get; set; }
    internal Func<string, Task<DownloadQueueReadResult>>? DownloadQueueRestoreReadResultAsync { get; set; }
    internal Func<DownloadQueueSnapshot, Task<RestoredDownloadQueue>>? DownloadQueueRestorePrepareAsync { get; set; }
    private readonly DispatcherTimer _downloadPersistenceTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private const string DownloadQueueRestoreRetryMessage = "Сохранённая очередь загрузок временно недоступна. Откройте «Центр загрузок», чтобы повторить восстановление.";
    private const string DownloadQueueInMemoryMessage = "Сохранённая очередь недоступна. Ручная очередь действует до закрытия приложения.";
    private const string DownloadQueuePlaylistIndexDamagedMessage = "Индекс офлайн-плейлистов повреждён. Отдельные треки можно скачать вручную. Временная очередь действует до закрытия приложения.";
    private string DownloadQueueMemoryOnlyNotice => _downloadPlaylistIndexDamaged ? DownloadQueuePlaylistIndexDamagedMessage : DownloadQueueInMemoryMessage;

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
        _downloadPersistenceInvalidated = false; _downloadPersistenceWriteBlocked = _preserveUnreadableDownloadCheckpoint;
        _downloadRestoreNeedsRetry = false;
        _downloadPersistenceDirty = !_downloadPersistenceWriteBlocked;
    }
    private bool DownloadQueueRestorePending => _downloadQueueRestoreAdmission is { } restore
        && restore.AccountUid == _account?.Uid && restore.Generation == _accountGeneration
        && restore.Revision == _downloadQueueAdmissionRevision;

    private bool AppUpdateMustWaitForDownloadRestore => DownloadQueueRestorePending || DownloadQueueRestoreCanRetry
        || (_downloadQueueStore is not null && _account is { } account
            && (_downloadQueueAccountUid != account.Uid || _downloadQueueAccountGeneration != _accountGeneration)
            && !_downloadPersistenceInvalidated && !_downloadPersistenceWriteBlocked);
    private bool DownloadQueueRestoreCanRetry => _downloadRestoreNeedsRetry && !_downloadPersistenceInvalidated
        && _account is { } account && _downloadPersistenceAccountUid == account.Uid;
    private bool HasUnfinishedDownloads => DownloadQueueBelongsToActiveAccount && !_downloadQueue.Suspended
        && _downloadQueue.Entries.Any(entry => !entry.SkipFavoritesDownload && entry.State is (DownloadEntryState.Queued
            or DownloadEntryState.Resolving or DownloadEntryState.Downloading or DownloadEntryState.Paused or DownloadEntryState.Failed));
    private bool HasUnfinishedMemoryOnlyDownloads => DownloadQueueUsesMemoryOnly && HasUnfinishedDownloads;
    private bool DownloadCheckpointNeedsRemovalVerification => DownloadQueueRestorePending || DownloadQueueRestoreCanRetry || DownloadQueueUsesMemoryOnly;
    private bool CanRecheckEmptyProtectedDownloadQueue => DownloadQueueBelongsToActiveAccount && _downloadQueue.Entries.Count == 0
        && _downloadCheckpointProtection is { ObservedForeign: null };
    private Task RetryDownloadQueueRestoreAsync()
    {
        if (_account is not { } account || _session is null || _closed || _offlineMaintenance
            || _closeCommitInProgress || _appUpdatePreparingRestart) return Task.CompletedTask;
        if (DownloadQueueRestoreCanRetry || _preserveUnreadableDownloadCheckpoint && _downloadCts is null && !HasUnfinishedMemoryOnlyDownloads)
            return RestoreDownloadQueueAsync(account.Uid);
        if (_downloadCheckpointProtection is not null && DownloadQueueBelongsToActiveAccount && TryFlushPendingDownloadQueueClears()
            && !HasPendingDownloadQueueClear)
        {
            if (CanRecheckEmptyProtectedDownloadQueue) return RestoreDownloadQueueAsync(account.Uid);
            if (_downloadQueue.Entries.Count > 0) return PersistDownloadQueueAsync(force: true);
        }
        return Task.CompletedTask;
    }

    private async Task RestoreDownloadQueueAsync(string accountUid)
    {
        var admissionRevision = _downloadQueueAdmissionRevision;
        if (_downloadQueueStore is not { } store || _closed || _downloadPersistenceClosing) return;
        var generation = _accountGeneration;
        if (_account?.Uid != accountUid || DownloadQueueRestorePending || _preserveUnreadableDownloadCheckpoint && HasUnfinishedMemoryOnlyDownloads
            || (!DownloadQueueRestoreCanRetry && !_preserveUnreadableDownloadCheckpoint && !CanRecheckEmptyProtectedDownloadQueue
                && _downloadPersistenceAccountUid == accountUid && !_downloadPersistenceInvalidated
                && (_downloadPersistenceWriteBlocked || _downloadQueueAccountUid == accountUid && _downloadQueueAccountGeneration == generation))) return;
        bool IsCurrentAdmission() => !_closed && !_downloadPersistenceClosing && _account?.Uid == accountUid
            && generation == _accountGeneration && admissionRevision == _downloadQueueAdmissionRevision;
        var restore = (AccountUid: accountUid, Generation: generation, Revision: admissionRevision);
        _downloadQueueRestoreAdmission = restore;
        var recoveryToken = _accountCts.Token;
        var hasValidCheckpoint = false;
        try
        {
            // The previous account's workers still own the shared queue while finishing.
            if (_downloadTask is { } pending) await pending;
            if (!IsCurrentAdmission()) return;
            _downloadPersistenceAccountUid = accountUid;
            _downloadPersistenceInvalidated = false;
            if (!TryFlushPendingDownloadQueueClears())
            {
                BlockDownloadQueueRestore(retryable: true);
                return;
            }
            // Another account's unresolved sign-out can protect the shared file
            // without requiring this account to wait before starting in memory.
            var protectedByOtherAccount = HasPendingDownloadQueueClear;
            var read = protectedByOtherAccount
                ? new DownloadQueueReadResult(null, DownloadQueueReadStatus.OtherAccount)
                : await ReadDownloadCheckpointForRestoreAsync(store, accountUid);
            var (snapshot, readStatus) = read;
            if (!IsCurrentAdmission() || _downloadPersistenceInvalidated || _preserveUnreadableDownloadCheckpoint && HasUnfinishedMemoryOnlyDownloads) return;
            if (snapshot is null)
            {
                if (readStatus == DownloadQueueReadStatus.Unavailable)
                {
                    BlockDownloadQueueRestore(retryable: true);
                    return;
                }
                if (readStatus is DownloadQueueReadStatus.AccessDenied or DownloadQueueReadStatus.UnsafePath)
                {
                    BlockDownloadQueueRestore(retryable: false, preserveUnreadable: true);
                    return;
                }
                _preserveUnreadableDownloadCheckpoint = false;
                _downloadPlaylistIndexDamaged = false;
                _downloadCheckpointProtection = readStatus == DownloadQueueReadStatus.OtherAccount ? read.Replacement ?? new() : null;
                _downloadCheckpointExplicitBatch = false;
                _downloadPersistenceWriteBlocked = readStatus == DownloadQueueReadStatus.Invalid;
                _downloadRestoreNeedsRetry = false;
                if (_downloadQueueAccountUid != accountUid || _downloadQueueAccountGeneration != generation)
                {
                    _downloadQueue.Replace([], DownloadQueueState.EmptyLabel);
                    _downloadQueuePlaylist = null; _downloadQueuePlaylistPersistence = null; _downloadQueuePlaylistHistoryOnly = false;
                }
                _downloadQueueAccountUid = accountUid; _downloadQueueAccountGeneration = generation;
                _downloadsAutoSuspended = _downloadPersistenceWriteBlocked;
                _downloadPersistenceDirty = false;
                RefreshDownloadCenter(); UpdatePlaylistDownloadedStatus();
                return;
            }
            hasValidCheckpoint = true;
            await _offline.RecoverPendingAsync(recoveryToken);
            if (!IsCurrentAdmission() || _downloadPersistenceInvalidated) return;
            var restored = await (DownloadQueueRestorePrepareAsync?.Invoke(snapshot)
                ?? Task.Run(() => DownloadQueueStore.PrepareRestoreAsync(snapshot, _offline, recoveryToken), recoveryToken));
            if (!IsCurrentAdmission() || _downloadPersistenceInvalidated || _preserveUnreadableDownloadCheckpoint && HasUnfinishedMemoryOnlyDownloads) return;
            _downloadPersistenceWriteBlocked = false; _downloadRestoreNeedsRetry = false;
            _preserveUnreadableDownloadCheckpoint = false;
            _downloadPlaylistIndexDamaged = false;
            _downloadCheckpointProtection = null;
            _downloadCheckpointExplicitBatch = false;
            _downloadQueue.Restore(restored);
            _downloadQueuePlaylist = restored.Playlist; _downloadQueueQuality = restored.Quality;
            _downloadQueuePlaylistPersistence = restored.PlaylistPersistence;
            _downloadQueuePlaylistHistoryOnly = restored.PlaylistHistoryOnly;
            _downloadQueueRecordPlaylistOccurrences = restored.PlaylistOccurrencesRecorded;
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
        catch (OfflinePlaylistIndexFormatException error) when (hasValidCheckpoint)
        {
            if (!IsCurrentAdmission() || _downloadPersistenceInvalidated) return;
            _downloadPlaylistIndexDamaged = true;
            BlockDownloadQueueRestore(retryable: false, preserveUnreadable: true);
            _diagnostics.LastErrorType = "DownloadRestore:" + error.GetType().Name;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or MusicException or JsonException or System.Security.Cryptography.CryptographicException)
        {
            if (!IsCurrentAdmission() || _downloadPersistenceInvalidated) return;
            var preserveUnreadable = !hasValidCheckpoint
                && (error is UninstallPathGuard.UnsafeStoragePathException || DownloadQueueStore.IsAccessDenied(error));
            BlockDownloadQueueRestore(retryable: hasValidCheckpoint || !preserveUnreadable && error is IOException,
                preserveUnreadable: preserveUnreadable);
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
    private async Task<DownloadQueueReadResult> ReadDownloadCheckpointForRestoreAsync(DownloadQueueStore store, string accountUid)
    {
        if (DownloadQueueRestoreReadResultAsync is { } readResult) return await readResult(accountUid);
        if (DownloadQueueRestoreReadAsync is not { } readSnapshot) return await Task.Run(() => store.Read(accountUid));
        var snapshot = await readSnapshot(accountUid);
        return new(snapshot, snapshot is null ? DownloadQueueReadStatus.Missing : DownloadQueueReadStatus.Loaded);
    }
    private void BlockDownloadQueueRestore(bool retryable, bool preserveUnreadable = false)
    {
        _downloadPersistenceWriteBlocked = true;
        _downloadPersistenceDirty = false;
        _downloadsAutoSuspended = true;
        _downloadRestoreNeedsRetry = retryable;
        _preserveUnreadableDownloadCheckpoint = preserveUnreadable;
        if (retryable)
        {
            RefreshDownloadCenter();
            SetStatus(DownloadQueueRestoreRetryMessage, true);
        }
        else
        {
            if (!preserveUnreadable) ClearPendingAutoFavorites();
            if (preserveUnreadable)
            {
                RefreshDownloadCenter();
                SetStatus(DownloadQueueMemoryOnlyNotice, true);
            }
        }
    }
    private bool CanPersistDownloadQueue => _downloadQueueStore is not null && !_downloadPersistenceClosing
        && !_downloadPersistenceInvalidated && !_downloadPersistenceWriteBlocked && !_downloadQueue.Suspended
        && !HasPendingDownloadQueueClear
        && _account is not null && _downloadPersistenceAccountUid == _account.Uid
        && _downloadQueueAccountUid == _account.Uid && _downloadQueueAccountGeneration == _accountGeneration;

    private bool DownloadCheckpointBlocksAutomaticWork => _preserveUnreadableDownloadCheckpoint
        || _downloadCheckpointProtection is { } protection && (protection.ObservedForeign is null || protection.Blocked);
    private bool DownloadQueueUsesMemoryOnly => DownloadCheckpointBlocksAutomaticWork
        || _downloadCheckpointProtection is { ObservedForeign: not null } && !_downloadCheckpointExplicitBatch;

    private async Task PersistDownloadQueueAsync(bool force = false)
    {
        if (!CanPersistDownloadQueue || !force && (_closeCommitInProgress || !_downloadPersistenceDirty
            || _downloadPersistenceWriteTask is { IsCompleted: false })) return;
        var admissionRevision = _downloadQueueAdmissionRevision;
        _downloadPersistenceDirty = false;
        try
        {
            var snapshot = CaptureDownloadQueue();
            var protection = _downloadCheckpointProtection;
            var task = _downloadQueueStore!.SaveInBackgroundAsync(snapshot, protection, _downloadCheckpointExplicitBatch);
            _downloadPersistenceWriteTask = task;
            if (await task && protection is not null && ReferenceEquals(protection, _downloadCheckpointProtection)
                && admissionRevision == _downloadQueueAdmissionRevision)
                FinishProtectedDownloadCheckpoint();
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
        if (!TryFlushPendingDownloadQueueClears() || HasPendingDownloadQueueClear) return false;
        if (!CanPersistDownloadQueue) return !HasUnfinishedMemoryOnlyDownloads;
        if (_downloadQueue.Entries.Count == 0) return true;
        try
        {
            // Active and queued entries are restored paused by the store.
            if (!_downloadQueueStore!.Save(CaptureDownloadQueue(), _downloadCheckpointProtection, _downloadCheckpointExplicitBatch))
                return !HasUnfinishedDownloads;
            if (_downloadCheckpointProtection is not null) FinishProtectedDownloadCheckpoint();
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
        _downloadQueueQuality, _downloadQueueAutomatic, _downloadQueuePlaylist, _downloadQueuePlaylistHistoryOnly,
        _downloadQueueRecordPlaylistOccurrences, _downloadQueuePlaylistPersistence);
    private void FinishProtectedDownloadCheckpoint()
    {
        _downloadCheckpointProtection = null;
        _downloadCheckpointExplicitBatch = false;
        if (!_closed)
        {
            RefreshDownloadCenter();
            TryStartPendingAutoFavorites();
        }
    }
    private bool ClearPersistedDownloadQueue(bool invalidateOnFailure, bool explicitLogout = false)
    {
        if (_downloadPersistenceClosing)
        {
            ++_downloadQueueAdmissionRevision;
            return true; // Closing saved its paused snapshot first.
        }
        var accountUid = invalidateOnFailure ? _account?.Uid : null;
        if (invalidateOnFailure && accountUid is null)
        {
            InvalidateDownloadQueueCheckpoint();
            return true;
        }
        DownloadQueueStore.ClearRetry? pending = null;
        try
        {
            if (_downloadQueueStore is { } store)
            {
                if (accountUid is null)
                {
                    store.Clear(); _pendingDownloadQueueClears?.Clear();
                }
                else if (_pendingDownloadQueueClears is { } clears && clears.TryGetValue(accountUid, out var existing))
                {
                    if (explicitLogout) store.Clear(accountUid, out pending, authorizeCurrent: true);
                    else if (!store.RetryClear(existing)) throw new IOException("Не удалось подтвердить прежнюю очередь загрузок.");
                    clears.Remove(accountUid);
                }
                else store.Clear(accountUid, out pending, authorizeCurrent: explicitLogout);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            if (accountUid is not null && pending is { } receipt)
                (_pendingDownloadQueueClears ??= new(StringComparer.Ordinal))[accountUid] = receipt;
            if (invalidateOnFailure) InvalidateDownloadQueueCheckpoint();
            _diagnostics.LastErrorType = "DownloadClear:" + error.GetType().Name;
            SetStatus("Не удалось удалить сохранённую очередь загрузок. Проверьте доступ к папке данных.", true);
            return false;
        }
        InvalidateDownloadQueueCheckpoint();
        return true;
    }
    private bool HasPendingDownloadQueueClear => _pendingDownloadQueueClears is { Count: > 0 };
    private bool TryFlushPendingDownloadQueueClears()
    {
        if (_downloadQueueStore is not { } store || _pendingDownloadQueueClears is not { Count: > 0 } clears) return true;
        foreach (var (accountUid, receipt) in clears.ToArray())
        {
            try
            {
                if (store.RetryClear(receipt)) clears.Remove(accountUid);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            { _diagnostics.LastErrorType = "DownloadClear:" + error.GetType().Name; }
        }
        return _account is { } active ? !clears.ContainsKey(active.Uid)
            : !clears.Values.Any(receipt => receipt.File is not null);
    }
    private void InvalidateDownloadQueueCheckpoint()
    {
        ++_downloadQueueAdmissionRevision;
        _downloadPersistenceInvalidated = true; _downloadPersistenceDirty = false;
        _downloadPersistenceAccountUid = null; _downloadPersistenceWriteBlocked = false; _downloadRestoreNeedsRetry = false;
        _preserveUnreadableDownloadCheckpoint = false;
        _downloadPlaylistIndexDamaged = false;
        _downloadCheckpointProtection = null;
        _downloadCheckpointExplicitBatch = false;
    }
}
