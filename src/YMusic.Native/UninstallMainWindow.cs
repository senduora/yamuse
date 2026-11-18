using System.Windows;
using System.IO;

namespace YMusic.Native;

public partial class MainWindow
{
    private IDisposable? _uninstallListener;
    private bool _fullUninstallPending, _fullUninstallCommitting, _fullUninstallCanClose;
    private bool CleanupBlocksFullUninstall => _cacheMaintenance || _offlineMaintenance || _clearOfflineConfirmationPending;
    private bool ExitBlocksFullUninstall => _closeCommitInProgress || _appUpdatePreparingRestart || _startupUpdateRestartRequested;
    private const string UninstallCleanupBusy = "Дождитесь завершения очистки музыки или кэша и повторите удаление.";
    private const string UninstallExitBusy = "Дождитесь закрытия или перезапуска Yamuse и повторите удаление.";

    private void RefreshFullUninstallAction()
    {
        SettingsUninstall.IsEnabled = _uninstallListener is not null && !_fullUninstallPending && !CleanupBlocksFullUninstall && !ExitBlocksFullUninstall;
        SettingsUninstall.ToolTip = _uninstallListener is null ? "Полное удаление доступно в установленной версии Yamuse"
            : CleanupBlocksFullUninstall ? UninstallCleanupBusy
            : ExitBlocksFullUninstall ? UninstallExitBusy
            : "Удалить приложение, сохранённый вход, настройки, кэш и скачанную музыку";
    }

    // App calls this only for a real, installed process after assigning its
    // MainWindow. No demo/test/portable window registers an uninstall listener.
    internal void InitializeFullUninstall()
    {
        if (_demo || !_restore || _closed || _uninstallListener is not null) return;
        try
        {
            _ = UninstallSupport.CurrentInstalledPaths();
            _uninstallListener = UninstallSupport.Listen(this, ConfirmFullUninstallAsync);
            Closed += (_, _) => { _uninstallListener?.Dispose(); _uninstallListener = null; };
            RefreshFullUninstallAction();
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        { }
    }

    private bool CanProceedWithFullUninstall()
    {
        if (_closed) return false;
        if (!CleanupBlocksFullUninstall && !ExitBlocksFullUninstall) return true;
        SetStatus(CleanupBlocksFullUninstall ? UninstallCleanupBusy : UninstallExitBusy, true);
        return false;
    }

    // A modal dialog pumps the dispatcher, so maintenance can start before it returns.
    internal bool ConfirmBeforeFullUninstall(Func<bool> confirm)
        => CanProceedWithFullUninstall() && confirm() && CanProceedWithFullUninstall();

    internal async Task ConfirmFullUninstallAsync()
    {
        if (_demo || !_restore || _fullUninstallPending || !CanProceedWithFullUninstall()) return;
        _fullUninstallPending = true;
        RefreshFullUninstallAction();
        (UninstallPaths Paths, string Nonce)? request = null;
        var ownsOfflineMaintenance = false;
        var oldShutdown = Application.Current.ShutdownMode;
        var oldTray = _minimizeToTray;
        try
        {
            var paths = UninstallSupport.CurrentInstalledPaths();
            if (!IsVisible) Show();
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            Activate();
            if (!ConfirmBeforeFullUninstall(() => MessageBox.Show(this, UninstallSupport.ConfirmationText(paths), "Полное удаление Yamuse",
                MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes)) return;
            // No files are deleted in this process. The clean second process
            // cannot run until this one exits, including all shutdown writes.
            var nonce = UninstallSupport.CreateReceipt(paths);
            request = (paths, nonce);
            _fullUninstallCommitting = true;
            _offlineMaintenance = true;
            ownsOfflineMaintenance = true;
            IsEnabled = false;
            await StartFullUninstallHandoffAsync(() =>
                UninstallSupport.StartAfterExit(paths, Environment.ProcessId, paths.Executable, [UninstallSupport.CompleteArgument, nonce]));
            _fullUninstallCanClose = true;
            Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            if (_shell is not null) _shell.MinimizeToTray = false;
            Close();
            if (!_closed) throw new IOException("Не удалось закрыть окно Yamuse. Удаление остановлено.");
            Application.Current.Shutdown();
        }
        catch (Exception error)
        {
            if (request is { } pending)
            {
                try { UninstallSupport.DeleteReceipt(pending.Paths, pending.Nonce); }
                catch (Exception cleanupError) when (cleanupError is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception) { }
            }
            Application.Current.ShutdownMode = oldShutdown;
            if (!_closed)
            {
                if (ownsOfflineMaintenance)
                {
                    _offlineMaintenance = false;
                    IsEnabled = true;
                    RefreshDownloadCenter();
                    UpdatePlaylistDownloadedStatus();
                }
                if (_shell is not null) _shell.MinimizeToTray = oldTray;
                SetStatus("Удаление не начато. Можно повторить действие.", true);
            }
            MessageBox.Show("Удаление не завершено.\n\n" + error.Message, "Удаление Yamuse", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _fullUninstallCanClose = false;
            _fullUninstallCommitting = false;
            _fullUninstallPending = false;
            if (!_closed) RefreshFullUninstallAction();
        }
    }

    internal async Task StartFullUninstallHandoffAsync(Action startWaiter)
    {
        // Preserve paused work, including admitted automatic downloads. A
        // restore still in progress retains its previous on-disk checkpoint.
        if (!DownloadQueueRestorePending) PauseDownloadQueueForAppUpdate();
        var writes = DrainExitWritesAsync();
        if (_downloadTask is { } downloads)
            try { await downloads.WaitAsync(_lifetime.Token); }
            catch (OperationCanceledException) when (!_lifetime.IsCancellationRequested) { }
        await writes.WaitAsync(_lifetime.Token);
        _lifetime.Token.ThrowIfCancellationRequested();
        // The snapshot restores paused. Leave the source and position intact
        // if starting the waiter fails. OnClosed disposes playback on success.
        _ = SaveStateBeforeClosing();
        startWaiter();
    }
}
