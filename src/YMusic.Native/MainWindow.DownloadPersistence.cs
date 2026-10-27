using System.Text.Json;
using System.Windows.Threading;

namespace YMusic.Native;

public partial class MainWindow
{
    private DownloadQueueStore? _downloadQueueStore;
    private string? _downloadPersistenceAccountUid;
    private bool _downloadPersistenceDirty, _downloadPersistenceInvalidated, _downloadPersistenceClosing;
    private bool _downloadPersistenceWriteBlocked;
    private Task? _downloadPersistenceWriteTask;
    // New work, clear and close supersede an in-flight load/reconciliation.
    private long _downloadQueueAdmissionRevision;
    // Fixture seams hold either asynchronous stage without touching a live profile.
    internal Func<string, Task<DownloadQueueSnapshot?>>? DownloadQueueRestoreReadAsync { get; set; }
    internal Func<DownloadQueueSnapshot, Task<RestoredDownloadQueue>>? DownloadQueueRestorePrepareAsync { get; set; }
    private readonly DispatcherTimer _downloadPersistenceTimer = new() { Interval = TimeSpan.FromSeconds(2) };

    private void InitializeDownloadPersistence()
    {
        if (!_restore || _demo || _downloadQueueStore is not null) return;
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
        _downloadPersistenceInvalidated = false; _downloadPersistenceWriteBlocked = false; _downloadPersistenceDirty = true;
    }
    private async Task RestoreDownloadQueueAsync(string accountUid)
    {
        var admissionRevision = _downloadQueueAdmissionRevision;
        if (_downloadQueueStore is not { } store || _closed || _downloadPersistenceClosing) return;
        if (_downloadPersistenceAccountUid == accountUid && !_downloadPersistenceInvalidated) return;
        var generation = _accountGeneration;
        bool IsCurrentAdmission() => !_closed && !_downloadPersistenceClosing && _account?.Uid == accountUid
            && generation == _accountGeneration && admissionRevision == _downloadQueueAdmissionRevision;
        // A canceled prior account's workers must finish before this shared
        // queue is replaced; their finally blocks still own the old entries.
        if (_downloadTask is { } pending) await pending;
        if (!IsCurrentAdmission()) return;
        _downloadPersistenceAccountUid = accountUid;
        _downloadPersistenceInvalidated = false; _downloadPersistenceWriteBlocked = false;
        try
        {
            var snapshot = await (DownloadQueueRestoreReadAsync?.Invoke(accountUid) ?? Task.Run(() => store.Load(accountUid)));
            if (!IsCurrentAdmission() || _downloadPersistenceInvalidated) return;
            if (snapshot is null)
            {
                if (store.LastReadStatus is DownloadQueueReadStatus.Invalid or DownloadQueueReadStatus.Unavailable)
                    _downloadPersistenceWriteBlocked = true;
                if (_downloadQueueAccountUid != accountUid)
                { _downloadQueue.Replace([], "Очередь пуста"); _downloadQueuePlaylist = null; }
                _downloadQueueAccountUid = accountUid; _downloadQueueAccountGeneration = generation;
                _downloadsAutoSuspended = _downloadPersistenceWriteBlocked;
                _downloadPersistenceDirty = false;
                RefreshDownloadCenter(); UpdatePlaylistDownloadedStatus();
                return;
            }
            var restored = await (DownloadQueueRestorePrepareAsync?.Invoke(snapshot)
                ?? Task.Run(() => DownloadQueueStore.PrepareRestore(snapshot, _offline)));
            if (!IsCurrentAdmission() || _downloadPersistenceInvalidated) return;
            _downloadQueue.Restore(restored);
            _downloadQueuePlaylist = restored.Playlist; _downloadQueueQuality = restored.Quality;
            _downloadQueueAutomatic = restored.Automatic;
            _downloadQueueAccountUid = accountUid; _downloadQueueAccountGeneration = generation;
            // This flag also suppresses automatic favorites replacement for a
            // restored failed-only queue. Only an explicit download can lift it.
            _downloadsAutoSuspended = true; _pendingAutoFavorites = null;
            _downloadPersistenceDirty = true;
            RefreshDownloadCenter(); UpdatePlaylistDownloadedStatus();
            if (_downloadQueue.Entries.Count > 0) SetStatus("Очередь загрузок восстановлена на паузе. Продолжите её в «Центре загрузок».");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or MusicException or JsonException)
        {
            if (!IsCurrentAdmission() || _downloadPersistenceInvalidated) return;
            _downloadPersistenceWriteBlocked = true; _downloadsAutoSuspended = true; _pendingAutoFavorites = null;
            _diagnostics.LastErrorType = "DownloadRestore:" + error.GetType().Name;
        }
    }
    private bool CanPersistDownloadQueue => _downloadQueueStore is not null && !_downloadPersistenceClosing
        && !_downloadPersistenceInvalidated && !_downloadPersistenceWriteBlocked && !_downloadQueue.Suspended
        && _account is not null && _downloadPersistenceAccountUid == _account.Uid
        && _downloadQueueAccountUid == _account.Uid && _downloadQueueAccountGeneration == _accountGeneration;

    private async Task PersistDownloadQueueAsync(bool force = false)
    {
        if (!CanPersistDownloadQueue || !force && (!_downloadPersistenceDirty || _downloadPersistenceWriteTask is { IsCompleted: false })) return;
        _downloadPersistenceDirty = false;
        try
        {
            var snapshot = DownloadQueueStore.Capture(_account!.Uid, _downloadQueue, _downloadQueueQuality, _downloadQueueAutomatic, _downloadQueuePlaylist);
            var task = _downloadQueueStore!.SaveInBackgroundAsync(snapshot);
            _downloadPersistenceWriteTask = task;
            await task;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        { _diagnostics.LastErrorType = "DownloadSave:" + error.GetType().Name; }
    }
    private void SaveDownloadQueueForClose()
    {
        _downloadPersistenceTimer.Stop();
        if (_downloadPersistenceClosing) return;
        ++_downloadQueueAdmissionRevision;
        try
        {
            if (CanPersistDownloadQueue)
            {
                // Active/queued states are restored paused by the store. Save
                // synchronously once so process exit cannot abandon the write.
                _downloadQueueStore!.Save(DownloadQueueStore.Capture(_account!.Uid, _downloadQueue,
                    _downloadQueueQuality, _downloadQueueAutomatic, _downloadQueuePlaylist));
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        { _diagnostics.LastErrorType = "DownloadSave:" + error.GetType().Name; }
        finally { _downloadPersistenceClosing = true; }
    }
    private bool ClearPersistedDownloadQueue()
    {
        ++_downloadQueueAdmissionRevision;
        if (_downloadPersistenceClosing) return true; // Closing saved its paused snapshot first.
        _downloadPersistenceInvalidated = true; _downloadPersistenceDirty = false;
        _downloadPersistenceAccountUid = null; _downloadPersistenceWriteBlocked = false;
        if (_downloadQueueStore is null) return true;
        try { _downloadQueueStore.Clear(); return true; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            _diagnostics.LastErrorType = "DownloadClear:" + error.GetType().Name;
            SetStatus("Не удалось удалить сохранённую очередь загрузок. Проверьте доступ к папке данных.", true);
            return false;
        }
    }
}
