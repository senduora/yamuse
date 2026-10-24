using System.Windows;
using System.IO;

namespace YMusic.Native;

public partial class MainWindow
{
    private IDisposable? _uninstallListener;
    private bool _fullUninstallPending;

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
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        { }
    }

    internal async Task ConfirmFullUninstallAsync()
    {
        if (_closed || _demo || !_restore || _fullUninstallPending) return;
        _fullUninstallPending = true;
        string? nonce = null;
        UninstallPaths? paths = null;
        var oldShutdown = Application.Current.ShutdownMode;
        var oldTray = _minimizeToTray;
        try
        {
            paths = UninstallSupport.CurrentInstalledPaths();
            if (!IsVisible) Show();
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            Activate();
            if (MessageBox.Show(this, UninstallSupport.ConfirmationText(paths), "Полное удаление Yamuse", MessageBoxButton.YesNo,
                MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
            // No files are deleted in this process. The clean second process
            // cannot run until this one exits, including all shutdown writes.
            nonce = UninstallSupport.CreateReceipt(paths);
            SuspendDownloadQueueForAccountChange();
            IsEnabled = false;
            _offlineMaintenance = true;
            _pendingAutoFavorites = null;
            _downloadCts?.Cancel();
            _playCts?.Cancel();
            CancelNextPreparation();
            _player.Stop();
            if (_downloadTask is { } downloads)
                try { await downloads; } catch (OperationCanceledException) { }
            UninstallSupport.StartAfterExit(paths, Environment.ProcessId, paths.Executable, [UninstallSupport.CompleteArgument, nonce]);
            Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            if (_shell is not null) _shell.MinimizeToTray = false;
            Close();
            if (!_closed) throw new IOException("Не удалось закрыть окно Yamuse. Удаление остановлено.");
            Application.Current.Shutdown();
        }
        catch (Exception error)
        {
            if (nonce is not null && paths is not null)
            {
                try { UninstallSupport.DeleteReceipt(paths, nonce); }
                catch (Exception cleanupError) when (cleanupError is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception) { }
            }
            Application.Current.ShutdownMode = oldShutdown;
            if (!_closed)
            {
                IsEnabled = true;
                _offlineMaintenance = false;
                if (_shell is not null) _shell.MinimizeToTray = oldTray;
                SetStatus("Удаление не начато. Повторите действие. Музыку можно включить снова.", true);
            }
            MessageBox.Show("Удаление не завершено.\n\n" + error.Message, "Удаление Yamuse", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally { _fullUninstallPending = false; }
    }
}
