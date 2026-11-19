using System.Windows;
using System.IO;
using System.Windows.Threading;

namespace YMusic.Native;

public partial class MainWindow
{
    private IDisposable? _uninstallListener;
    private bool _fullUninstallPending, _fullUninstallCommitting, _fullUninstallCanClose;
    private bool _windowsUninstallRegistered;
    private enum UninstallListenerStatus { Inapplicable, Retryable, Ready }
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
    internal void InitializeFullUninstall(UninstallSupport.WindowsUninstallRegistration registration)
        => _ = InitializeFullUninstall(registration, UninstallSupport.ValidateCurrentInstallationImage,
            () => UninstallSupport.Listen(this, reply => ForwardFullUninstall(reply)), UninstallSupport.CheckWindowsUninstallRegistration);

    internal Task InitializeFullUninstall(UninstallSupport.WindowsUninstallRegistration registration, Action verifyInstalled,
        Func<IDisposable> listen, Func<UninstallSupport.WindowsUninstallRegistration> register, Func<TimeSpan, CancellationToken, Task>? wait = null)
    {
        _windowsUninstallRegistered = registration == UninstallSupport.WindowsUninstallRegistration.Registered;
        var listener = TryInitializeFullUninstall(verifyInstalled, listen);
        return RetryWindowsUninstallAfterStartupAsync(listener, registration, verifyInstalled, listen, register, wait);
    }

    private UninstallSupport.ForwardedUninstall GetForwardedUninstallAdmission()
    {
        if (_closed || _demo || !_restore || ExitBlocksFullUninstall || Dispatcher.HasShutdownStarted)
            return UninstallSupport.ForwardedUninstall.Closing;
        if (CleanupBlocksFullUninstall) return UninstallSupport.ForwardedUninstall.Maintenance;
        return _fullUninstallPending ? UninstallSupport.ForwardedUninstall.Busy : UninstallSupport.ForwardedUninstall.Accepted;
    }

    internal void ForwardFullUninstall(Action<UninstallSupport.ForwardedUninstall> reply, Func<Task>? confirmation = null)
    {
        var admission = GetForwardedUninstallAdmission();
        if (admission != UninstallSupport.ForwardedUninstall.Accepted) { reply(admission); return; }

        // Reserve the one window request before acknowledging it. If the client
        // has left, release the reservation without showing a new prompt.
        _fullUninstallPending = true;
        try { reply(admission); }
        catch { _fullUninstallPending = false; throw; }
        _ = RunAdmittedFullUninstallAsync(defer: true, confirmation);
    }

    internal void InitializeFullUninstall(Action verifyInstalled, Func<IDisposable> listen)
        => _ = TryInitializeFullUninstall(verifyInstalled, listen);

    private UninstallListenerStatus TryInitializeFullUninstall(Action verifyInstalled, Func<IDisposable> listen)
    {
        if (_demo || !_restore || _closed) return UninstallListenerStatus.Inapplicable;
        if (_uninstallListener is not null) return UninstallListenerStatus.Ready;
        try
        {
            verifyInstalled();
            _uninstallListener = listen();
            Closed += (_, _) => { _uninstallListener?.Dispose(); _uninstallListener = null; };
            RefreshFullUninstallAction();
            return UninstallListenerStatus.Ready;
        }
        catch (Exception error) when (UninstallSupport.IsUnavailableInstallEvidence(error))
        { return UninstallListenerStatus.Retryable; }
        catch (Exception error) when (error is IOException or InvalidOperationException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        { return UninstallListenerStatus.Inapplicable; }
    }

    internal void RetryWindowsUninstallRegistration(Func<bool>? register = null)
    {
        if (_restore && !_demo && !_windowsUninstallRegistered && _uninstallListener is not null)
            _windowsUninstallRegistered = (register ?? UninstallSupport.RegisterWindowsUninstall)();
    }

    private async Task RetryWindowsUninstallAfterStartupAsync(UninstallListenerStatus listener,
        UninstallSupport.WindowsUninstallRegistration registration, Action verifyInstalled, Func<IDisposable> listen,
        Func<UninstallSupport.WindowsUninstallRegistration> register, Func<TimeSpan, CancellationToken, Task>? wait)
    {
        bool PendingRegistration() => registration == UninstallSupport.WindowsUninstallRegistration.Retryable && !_windowsUninstallRegistered;
        bool CanRetry() => listener == UninstallListenerStatus.Retryable || listener == UninstallListenerStatus.Ready && PendingRegistration();
        if (!CanRetry()) return;
        wait ??= Task.Delay;
        var retryDelays = new[] { 1, 3, 10, 30, 60 };
        try
        {
            for (var delayIndex = 0; CanRetry(); delayIndex = Math.Min(delayIndex + 1, retryDelays.Length - 1))
            {
                if (_closed || _demo || !_restore) return;
                // If the installation is still temporarily unavailable, keep
                // checking once a minute after the initial shorter delays.
                await wait(TimeSpan.FromSeconds(retryDelays[delayIndex]), _lifetime.Token);
                if (_closed) return;
                if (ExitBlocksFullUninstall || _fullUninstallCommitting) continue;
                if (listener == UninstallListenerStatus.Retryable) listener = TryInitializeFullUninstall(verifyInstalled, listen);
                if (listener == UninstallListenerStatus.Ready && PendingRegistration())
                {
                    registration = register();
                    _windowsUninstallRegistered = registration == UninstallSupport.WindowsUninstallRegistration.Registered;
                }
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
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

    internal UninstallFileIdentity? ConfirmDocumentsForFullUninstall(UninstallPaths paths, Func<bool> confirm)
    {
        var documents = UninstallSupport.CaptureDocumentsIdentity(paths);
        return ConfirmBeforeFullUninstall(confirm) ? documents : null;
    }

    internal Task ConfirmFullUninstallAsync()
    {
        if (_demo || !_restore || _fullUninstallPending || !CanProceedWithFullUninstall()) return Task.CompletedTask;
        _fullUninstallPending = true;
        return RunAdmittedFullUninstallAsync(defer: false, confirmation: null);
    }

    private async Task RunAdmittedFullUninstallAsync(bool defer, Func<Task>? confirmation)
    {
        try
        {
            if (defer)
            {
                // End the pipe's dispatcher operation before entering any
                // modal UI, so another caller can receive its own Busy reply.
                await System.Windows.Threading.Dispatcher.Yield(DispatcherPriority.Normal);
                if (_closed) return;
                if (!CanProceedWithFullUninstall())
                {
                    var refusal = CleanupBlocksFullUninstall ? UninstallCleanupBusy : UninstallExitBusy;
                    MessageBox.Show(refusal, "Удаление Yamuse", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
            }
            RefreshFullUninstallAction();
            if (confirmation is null) await ConfirmAdmittedFullUninstallAsync();
            else await confirmation();
        }
        finally
        {
            _fullUninstallPending = false;
            if (!_closed) RefreshFullUninstallAction();
        }
    }

    private async Task ConfirmAdmittedFullUninstallAsync()
    {
        (UninstallPaths Paths, string Nonce)? request = null;
        var ownsOfflineMaintenance = false;
        var oldShutdown = Application.Current.ShutdownMode;
        var oldTray = _minimizeToTray;
        try
        {
            var paths = UninstallSupport.CurrentInstalledPaths();
            UninstallSupport.RequireUpdaterFinished(paths.Install);
            RetryWindowsUninstallRegistration();
            if (!IsVisible) Show();
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            Activate();
            var documents = ConfirmDocumentsForFullUninstall(paths, () => MessageBox.Show(this, UninstallSupport.ConfirmationText(paths), "Полное удаление Yamuse",
                MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes);
            if (documents is null) return;
            // No files are deleted in this process. The clean second process
            // cannot run until this one exits, including all shutdown writes.
            UninstallSupport.RequireUpdaterFinished(paths.Install);
            var nonce = UninstallSupport.CreateReceipt(paths, documents);
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
            FinishFullUninstallCommit();
        }
    }

    private void FinishFullUninstallCommit()
    {
        _fullUninstallCanClose = false;
        _fullUninstallCommitting = false;
        ResumeDeferredRestoredArtwork();
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
