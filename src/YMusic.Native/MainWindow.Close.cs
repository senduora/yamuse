using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;

namespace YMusic.Native;

public partial class MainWindow
{
    private readonly bool _persistBeforeClose;
    private bool _closeCommitInProgress, _closeCommitApproved;
    internal Func<string, MessageBoxResult>? CloseSaveFailurePrompt { get; set; }
    private readonly record struct CloseDownloads(long Revision, int AccountGeneration, bool AutoWasEnabled, bool AutoWasSuspended,
        PlaylistDetails? PendingAutoFavorites, HashSet<DownloadEntry> Entries);

    protected override void OnClosing(CancelEventArgs e)
    {
        if (_fullUninstallCommitting && !_fullUninstallCanClose) { e.Cancel = true; return; }
        base.OnClosing(e);
        if (e.Cancel || _closeCommitApproved || _fullUninstallCanClose) return;
        if (!_persistBeforeClose && CloseSaveFailurePrompt is null) return;
        e.Cancel = true;
        if (_closeCommitInProgress) return;
        _closeCommitInProgress = true;
        _ = CommitAndCloseAsync();
    }

    private Task DrainExitWritesAsync() => _restore && !_demo
        ? Task.WhenAll(DrainFavoritesCacheWritesAsync(), DrainSessionRefreshWritesAsync())
        : DrainSessionRefreshWritesAsync();

    private async Task CommitAndCloseAsync()
    {
        // Replay Close only after WPF returns from its first Closing event.
        await System.Windows.Threading.Dispatcher.Yield(DispatcherPriority.Normal);
        CloseDownloads? downloads = null;
        try
        {
            if (_closed) return;
            RefreshAppUpdateUi();
            ArmUpdateSchedule();
            if (_offlineMaintenanceCompletion is { } maintenance)
                await maintenance.Task.WaitAsync(_lifetime.Token);
            if (_closed) return;
            downloads = PauseDownloadsForClose();
            var writes = DrainExitWritesAsync();
            if (_downloadTask is { } pending)
                try { await pending.WaitAsync(_lifetime.Token); } catch (OperationCanceledException) { }
            await writes.WaitAsync(_lifetime.Token);
            while (!_closed && !_lifetime.IsCancellationRequested)
            {
                var updateCheckSettled = await SettleUpdateCheckAsync();
                if (_closed || _lifetime.IsCancellationRequested) return;
                var failure = SaveStateBeforeClosing();
                if (!updateCheckSettled && _updateCheckCompletion is not null)
                    failure = failure is null ? UpdateCheckStillRunning : failure + "\n" + UpdateCheckStillRunning;
                var choice = failure is null ? MessageBoxResult.No : AskHowToClose(failure);
                if (choice == MessageBoxResult.Yes) continue;
                if (choice != MessageBoxResult.No) return;
                _closeCommitApproved = true;
                if (_shell is { } shell) shell.RequestExit();
                else Close();
                return;
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception error)
        {
            _diagnostics.LastErrorType = "CloseSave:" + error.GetType().Name;
            if (!_closed) SetStatus("Не удалось подготовить выход. Повторите попытку.", true);
        }
        finally
        {
            _closeCommitApproved = false;
            _closeCommitInProgress = false;
            ResumeDeferredRestoredArtwork();
            if (!_closed)
            {
                RefreshAppUpdateUi();
                ArmUpdateSchedule();
                if (downloads is { } paused) ResumeDownloadsAfterCanceledClose(paused);
                TryStartPendingAutoFavorites();
            }
        }
    }

    private CloseDownloads? PauseDownloadsForClose()
    {
        if (DownloadQueueRestorePending) return null;
        var wasRunning = _downloadQueue.Running && _downloadCts is { IsCancellationRequested: false };
        var hasPendingFavorites = _autoFavoritesEnabled && DownloadQueueBelongsToActiveAccount
            && _pendingConfirmedFavorites.Count > 0;
        if (!wasRunning && !hasPendingFavorites) return null;

        var generation = _accountGeneration;
        var pending = _pendingAutoFavorites;
        var autoWasEnabled = _autoFavoritesEnabled;
        var autoWasSuspended = _downloadsAutoSuspended;
        var resumePendingFavorites = hasPendingFavorites && !autoWasSuspended && !_downloadQueue.HasPaused && !_downloadQueue.Suspended;
        HashSet<DownloadEntry> entries = wasRunning
            ? _downloadQueue.Entries.Where(entry => entry.Active || entry.State == DownloadEntryState.Queued).ToHashSet() : [];
        var pendingTerminalRows = resumePendingFavorites ? _downloadQueue.Entries.Where(entry =>
            entry.State is DownloadEntryState.Unavailable or DownloadEntryState.Removed
            && _pendingConfirmedFavorites.ContainsKey(entry.Track.Id)).ToArray() : [];
        var countBeforePause = _downloadQueue.Entries.Count;
        var revision = PauseDownloadQueueForAppUpdate();
        if (resumePendingFavorites)
        {
            entries.UnionWith(_downloadQueue.Entries.Skip(countBeforePause));
            entries.UnionWith(pendingTerminalRows.Where(entry => entry is { State: DownloadEntryState.Paused, DeferredFavorite: not null }));
        }
        return new(revision, generation, autoWasEnabled, autoWasSuspended, pending, entries);
    }

    private void ResumeDownloadsAfterCanceledClose(CloseDownloads paused)
    {
        if (_lifetime.IsCancellationRequested || _offlineMaintenance || _appUpdatePreparingRestart || _startupUpdateRestartRequested
            || _downloadCts is not null || _downloadQueue.Running || _downloadQueue.Suspended
            || !DownloadQueueBelongsToActiveAccount || paused.AccountGeneration != _accountGeneration
            || paused.Revision != _downloadQueueAdmissionRevision) return;

        _downloadsAutoSuspended = paused.AutoWasSuspended;
        if (_autoFavoritesEnabled) _pendingAutoFavorites ??= paused.PendingAutoFavorites;
        var automaticTurnedOff = paused.AutoWasEnabled && !_autoFavoritesEnabled;
        if (automaticTurnedOff)
        {
            if (_downloadQueueAutomatic) return;
            paused.Entries.RemoveWhere(entry => entry.AutomaticFavoriteQuality is not null);
        }
        if (paused.Entries.Count > 0) _ = RunDownloadQueueAsync(retryFailed: false, resumeEntries: paused.Entries);
    }

    private string? SaveStateBeforeClosing()
    {
        List<string>? failures = null;
        if (!TryFlushPendingSessionChange()) (failures ??= []).Add(PendingSessionSaveFailure);
        if (!TrySavePlaybackStateForExit()) (failures ??= []).Add(PlaybackStateSaveFailure);
        if (!TrySaveDownloadQueueCheckpoint()) (failures ??= []).Add(HasPendingDownloadQueueClear
            ? "Не удалось отменить сохранённую очередь после выхода из аккаунта. Она может восстановиться при следующем входе."
            : "Не удалось сохранить очередь загрузок.");
        if (!TrySavePreferencesForUpdate(out var failure)) (failures ??= []).Add(failure ?? "Не удалось сохранить настройки.");
        if (!TryFlushUnsavedListSorts()) (failures ??= []).Add("Не удалось сохранить сортировку списков.");
        if (!TryCommitUpdateSchedule()) (failures ??= []).Add(UpdateScheduleSaveFailure);
        return failures is null ? null : string.Join("\n", failures);
    }

    private string PendingSessionSaveFailure => _pendingSessionChange is { Session: null }
        ? "Не удалось удалить сохранённый вход. При следующем запуске Yamuse может снова открыть этот аккаунт."
        : "Не удалось сохранить обновлённый вход. При следующем запуске может потребоваться войти заново.";

    private MessageBoxResult AskHowToClose(string failure)
    {
        if (CloseSaveFailurePrompt is { } prompt) return prompt(failure);
        _shell?.ShowMainWindow();
        return MessageBox.Show(this, failure + "\n\n«Да» — повторить сохранение.\n«Нет» — выйти без сохранения последних изменений.\n«Отмена» — остаться в Yamuse.",
            "Выход из Yamuse", MessageBoxButton.YesNoCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel);
    }
}
