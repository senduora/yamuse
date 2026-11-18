using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;

namespace YMusic.Native;

public partial class MainWindow
{
    private event Action? AppUpdateUiChanged;
    // A per-window seam keeps fixture updates away from the installed updater.
    private UpdateSupport? _appUpdatesOverride = null;
    private UpdateSupport AppUpdates => _appUpdatesOverride ?? UpdateSupport.Current;
    private bool _startupUpdateCheckStarted, _startupUpdateDismissed, _appUpdateActionInProgress, _startupUpdateRestartRequested;
    private bool _appUpdatePreparingRestart;
    private Border? _startupUpdatePrompt;
    private TextBlock? _startupUpdateText;
    private Button? _startupUpdateButton, _startupUpdateLater;
    private string? _appUpdateFailure;
    private bool AppUpdateMaintenanceActive => _offlineMaintenance || _cacheMaintenance;
    private const string AppUpdateMaintenanceMessage = "Дождитесь завершения очистки музыки или кэша и повторите обновление.";
    private const string AppUpdateRestoreMessage = "Обновление отложено до восстановления очереди загрузок. После восстановления повторите попытку.";
    private const string AppUpdateDownloadSaveMessage = "Не удалось сохранить очередь загрузок. Обновление отложено.";
    private const string AppUpdateRetryMessage = "Не удалось обновить приложение. Повторите позже.";

    private async Task CheckForAppUpdatesAsync()
    {
        if (_closed || _closeCommitInProgress || _startupUpdateCheckStarted) return;
        _startupUpdateCheckStarted = true;
        InitializeUpdateSchedule();
        RefreshAppUpdateUi();
        try
        {
            var updates = AppUpdates;
            if (!updates.State.FeedConfigured || !updates.State.IsInstalled && !_restore && !updates.HasPortableCheckOverride) return;
            if (_updateCheckFrequency != UpdateCheckFrequency.Startup)
            { await RunScheduledUpdateCheckAsync(); return; }
            await CheckAppUpdateNowAsync(updates);
            if (_closed || _lifetime.IsCancellationRequested || _closeCommitInProgress || _updateCheckFrequency != UpdateCheckFrequency.Startup) return;
            // Offline startup stays quiet. Only a confirmed available package
            // creates the invitation; navigation and settings refresh cannot.
            if (updates.State.IsInstalled && updates.State.Status is AppUpdateStatus.Available or AppUpdateStatus.Downloaded)
                CreateStartupUpdatePrompt();
            RefreshAppUpdateUi();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _diagnostics.LastErrorType = "Updates:" + ex.GetType().Name; }
    }

    private void RefreshAppUpdateUi()
    {
        if (_closed) return;
        var state = AppUpdates.State;
        var available = state.Status is AppUpdateStatus.Available or AppUpdateStatus.Downloaded;
        SettingsButton.Content = available ? "Настройки · •" : "Настройки";
        SettingsButton.ToolTip = available ? "Доступно обновление " + state.AvailableVersion : "Настройки";
        RefreshFooterUpdateUi(state);
        AppUpdateUiChanged?.Invoke();
        RefreshStartupUpdatePrompt();
    }

    private void RefreshFooterUpdateUi(AppUpdateState state)
    {
        if (FooterUpdateButton is null) return;
        var available = state.Status is AppUpdateStatus.Available or AppUpdateStatus.Downloaded;
        FooterUpdateButton.Content = state.Status switch
        {
            AppUpdateStatus.Checking => "…", AppUpdateStatus.Downloading => "…",
            AppUpdateStatus.Available or AppUpdateStatus.Downloaded => "↑",
            AppUpdateStatus.NoUpdate => "✓", AppUpdateStatus.Error => "!", _ => "↻"
        };
        FooterUpdateButton.SetResourceReference(Control.ForegroundProperty, available ? "AccentTextBrush" : "MutedBrush");
        FooterUpdateButton.ToolTip = state.Status switch
        {
            AppUpdateStatus.Available or AppUpdateStatus.Downloaded => "Доступно обновление " + state.AvailableVersion + (state.IsInstalled ? " — открыть настройки" : " — открыть официальный выпуск"),
            AppUpdateStatus.Checking => "Проверяем обновления…", AppUpdateStatus.Downloading => "Скачиваем обновление…",
            AppUpdateStatus.NoUpdate => "Последняя доступная версия. Нажмите, чтобы проверить снова.",
            AppUpdateStatus.Error => "Не удалось проверить обновления. Нажмите, чтобы повторить.",
            _ => "Проверить обновления"
        };
        AutomationProperties.SetName(FooterUpdateButton, FooterUpdateButton.ToolTip.ToString());
        FooterUpdateButton.IsEnabled = state.FeedConfigured && !_closeCommitInProgress && !_appUpdateActionInProgress && !_updateCheckInProgress && !_startupUpdateRestartRequested
            && state.Status is not AppUpdateStatus.Checking and not AppUpdateStatus.Downloading;
    }

    private void CreateStartupUpdatePrompt()
    {
        if (_startupUpdatePrompt is not null || _startupUpdateDismissed || _closed) return;
        var content = new StackPanel();
        content.Children.Add(new TextBlock { Text = "Доступно обновление Yamuse", FontWeight = FontWeights.SemiBold, FontSize = 14 });
        _startupUpdateText = new TextBlock { Name = "StartupUpdateText", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 5, 0, 9), FontSize = 12 };
        content.Children.Add(_startupUpdateText);
        var actions = new WrapPanel();
        _startupUpdateButton = new Button { Name = "StartupUpdateButton", Content = "Обновить и перезапустить", Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(0, 0, 8, 0), FontSize = 12 };
        _startupUpdateButton.SetResourceReference(StyleProperty, "AccentButton");
        _startupUpdateLater = new Button { Name = "StartupUpdateLater", Content = "Позже", Padding = new Thickness(12, 6, 12, 6), FontSize = 12 };
        _startupUpdateLater.SetResourceReference(StyleProperty, "QuietButton");
        actions.Children.Add(_startupUpdateButton); actions.Children.Add(_startupUpdateLater); content.Children.Add(actions);
        _startupUpdatePrompt = new Border { Name = "StartupUpdatePrompt", Child = content, CornerRadius = new CornerRadius(10),
            Padding = new Thickness(14, 11, 14, 11), Margin = new Thickness(22, 14, 22, 0), BorderThickness = new Thickness(1),
            Width = 410, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top };
        _startupUpdatePrompt.SetResourceReference(Border.BackgroundProperty, "PanelBrush");
        _startupUpdatePrompt.SetResourceReference(Border.BorderBrushProperty, "AccentSoftBrush");
        AutomationProperties.SetName(_startupUpdatePrompt, "Доступно обновление Yamuse");
        // A compact inline card belongs to the main column on every page.
        // It leaves the sidebar/player rows and expanded-player overlay intact.
        Grid.SetColumn(_startupUpdatePrompt, 1);
        Panel.SetZIndex(_startupUpdatePrompt, 150);
        LayoutRoot.Children.Add(_startupUpdatePrompt);
        _startupUpdateLater.Click += (_, _) =>
        {
            _startupUpdateDismissed = true;
            _startupUpdatePrompt.Visibility = Visibility.Collapsed;
        };
        _startupUpdateButton.Click += async (_, _) => await InstallStartupUpdateAsync();
    }

    private void RefreshStartupUpdatePrompt()
    {
        if (_startupUpdatePrompt is null || _startupUpdateDismissed || _closed) return;
        var state = AppUpdates.State;
        var show = state.Status is AppUpdateStatus.Available or AppUpdateStatus.Downloaded
            || _startupUpdatePrompt.Visibility == Visibility.Visible && (_startupUpdateRestartRequested || _appUpdateActionInProgress
                || _appUpdateFailure is not null || state.Status == AppUpdateStatus.Checking);
        _startupUpdatePrompt.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        _startupUpdateButton!.IsEnabled = show && !_closeCommitInProgress && !AppUpdateMaintenanceActive && !_appUpdateActionInProgress
            && !_updateCheckInProgress && !_startupUpdateRestartRequested
            && state.Status is not AppUpdateStatus.Checking and not AppUpdateStatus.Downloading;
        _startupUpdateLater!.IsEnabled = !_appUpdateActionInProgress && !_startupUpdateRestartRequested;
        if (!show) return;
        _startupUpdateText!.Text = AppUpdateMaintenanceActive ? AppUpdateMaintenanceMessage : _appUpdateFailure ?? (state.Status switch
        {
            AppUpdateStatus.Downloading => $"Скачиваем обновление… {state.ProgressPercent ?? 0}%",
            AppUpdateStatus.Checking => "Проверяем обновление…",
            _ when _startupUpdateRestartRequested => "Перезапускаем приложение…",
            _ when _appUpdateActionInProgress => "Готовим перезапуск…",
            AppUpdateStatus.Downloaded => $"Версия {state.AvailableVersion} загружена и готова к установке.",
            _ => $"Версия {state.AvailableVersion}. Приложение сохранит состояние и перезапустится."
        });
    }

    private async Task InstallStartupUpdateAsync()
    {
        if (_closed || _closeCommitInProgress || _startupUpdateDismissed || _appUpdateActionInProgress || _updateCheckInProgress || _startupUpdateRestartRequested) return;
        if (AppUpdateMaintenanceActive) { RefreshAppUpdateUi(); return; }
        var updates = AppUpdates;
        _appUpdateActionInProgress = true;
        _appUpdateFailure = null;
        RefreshAppUpdateUi();
        try
        {
            // A failed download/apply is retriable. Rechecking also discovers a
            // package that was downloaded successfully before a restart failed.
            if (updates.State.Status is not AppUpdateStatus.Available and not AppUpdateStatus.Downloaded)
                await CheckAppUpdateNowAsync(updates);
            if (_closed || _lifetime.IsCancellationRequested) return;
            if (updates.State.Status == AppUpdateStatus.Available)
                await updates.DownloadAsync(new Progress<int>(_ =>
                { if (!_closed && !_lifetime.IsCancellationRequested && _appUpdateActionInProgress) RefreshAppUpdateUi(); }), _lifetime.Token);
            if (_closed || _lifetime.IsCancellationRequested) return;
            // Progress<T> posts to the UI asynchronously. Its last callback may
            // run before DownloadAsync publishes Downloaded, so publish the
            // completed phase explicitly before waiting for an offline write.
            RefreshAppUpdateUi();
            if (updates.State.Status == AppUpdateStatus.Downloaded)
                await ApplyAppUpdateAsync(updates);
            else
                _appUpdateFailure = updates.State.Status == AppUpdateStatus.Error
                    ? "Не удалось обновить приложение. Проверьте соединение и повторите."
                    : "Обновление сейчас недоступно. Можно проверить его позже в настройках.";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _diagnostics.LastErrorType = "Updates:" + ex.GetType().Name;
            _appUpdateFailure = AppUpdateRetryMessage;
        }
        finally
        {
            _appUpdateActionInProgress = false;
            if (!_closed) { RefreshAppUpdateUi(); ArmUpdateSchedule(); }
        }
    }

    private async Task<bool> ApplyAppUpdateAsync(UpdateSupport updates)
    {
        if (_closed || _lifetime.IsCancellationRequested || _closeCommitInProgress || _appUpdatePreparingRestart || _startupUpdateRestartRequested) return false;
        _appUpdateFailure = null;
        if (AppUpdateMaintenanceActive) return false;
        if (AppUpdateMustWaitForDownloadRestore) return DeferAppUpdate(AppUpdateRestoreMessage);
        var accountUid = _account?.Uid;
        var accountGeneration = _accountGeneration;
        var autoWasSuspended = _downloadsAutoSuspended;
        var admissionRevision = _downloadQueueAdmissionRevision;
        bool IsCurrentAdmission() => accountUid == _account?.Uid && accountGeneration == _accountGeneration
            && admissionRevision == _downloadQueueAdmissionRevision;
        _appUpdatePreparingRestart = true;
        try
        {
            admissionRevision = PauseDownloadQueueForAppUpdate();
            if (!TrySaveDownloadQueueCheckpoint()) return DeferAppUpdate(AppUpdateDownloadSaveMessage);
            // Finish an atomic offline write before the updater exits the process.
            if (_downloadTask is { } pending)
                try { await pending.WaitAsync(_lifetime.Token); } catch (OperationCanceledException) { }
            await DrainExitWritesAsync().WaitAsync(_lifetime.Token);
            if (_closed || _lifetime.IsCancellationRequested || _closeCommitInProgress) return false;
            if (AppUpdateMaintenanceActive) return false;
            if (!IsCurrentAdmission() || _downloadCts is not null || _downloadQueue.Running)
                return DeferAppUpdate("Очередь загрузок или аккаунт изменились. Повторите обновление.");
            if (AppUpdateMustWaitForDownloadRestore) return DeferAppUpdate(AppUpdateRestoreMessage);
            if (!TryFlushPendingSessionChange()) return DeferAppUpdate(PendingSessionSaveFailure + " Обновление отложено.");
            if (!TrySaveDownloadQueueCheckpoint()) return DeferAppUpdate(AppUpdateDownloadSaveMessage);
            if (!TrySavePreferencesForUpdate(out var preferencesFailure))
                return DeferAppUpdate(preferencesFailure ?? "Не удалось сохранить настройки. Обновление отложено.");
            if (!TrySavePlaybackStateForExit())
                return DeferAppUpdate("Не удалось сохранить очередь воспроизведения и позицию. Обновление отложено.");
            updates.ApplyAndRestart();
            _startupUpdateRestartRequested = true;
            return true;
        }
        finally
        {
            _appUpdatePreparingRestart = false;
            if (!_closed && !_lifetime.IsCancellationRequested && !_startupUpdateRestartRequested)
            {
                if (IsCurrentAdmission() && !autoWasSuspended && _downloadCts is null && !_downloadQueue.HasPaused && !_downloadQueue.CanResume)
                    _downloadsAutoSuspended = false;
                TryStartPendingAutoFavorites();
            }
        }
    }

    private bool DeferAppUpdate(string reason)
    {
        _appUpdateFailure = reason;
        return false;
    }

    private FrameworkElement CreateUpdateSettingsPanel()
        => CreateUpdateSettingsPanelCore(AppUpdates);

    private FrameworkElement CreateUpdateSettingsPanelCore(UpdateSupport updates)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 4, 0, 20) };
        panel.Children.Add(new TextBlock { Text = "Обновления", FontSize = 17, FontWeight = FontWeights.SemiBold });
        var text = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 10) };
        var button = new Button { HorizontalAlignment = HorizontalAlignment.Left, Name = "AppUpdateButton" };
        var checkButton = new Button { HorizontalAlignment = HorizontalAlignment.Left, Name = "AppUpdateCheckButton",
            Content = "Проверить обновления", Margin = new Thickness(0, 6, 0, 0) };
        checkButton.SetResourceReference(StyleProperty, "QuietButton");
        var checkActions = new StackPanel { Visibility = Visibility.Collapsed };
        checkActions.Children.Add(checkButton);
        var frequencyLabel = new TextBlock { Text = "Проверять обновления", Margin = new Thickness(0, 2, 0, 5) };
        var frequency = new ComboBox { Name = "UpdateCheckFrequencyCombo", Width = 250, HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 0, 0, 6), DisplayMemberPath = "Label", SelectedValuePath = "Value",
            ItemsSource = new[]
            {
                new { Value = UpdateCheckFrequency.Startup, Label = "При запуске" },
                new { Value = UpdateCheckFrequency.Hourly, Label = "Раз в час" },
                new { Value = UpdateCheckFrequency.Daily, Label = "Раз в день" },
                new { Value = UpdateCheckFrequency.Weekly, Label = "Раз в неделю" },
                new { Value = UpdateCheckFrequency.Monthly, Label = "Раз в месяц" },
                new { Value = UpdateCheckFrequency.Off, Label = "Не проверять автоматически" }
            }, SelectedValue = _updateCheckFrequency };
        _updateFrequencyCombo = frequency;
        frequency.SelectionChanged += (_, _) =>
        {
            if (!CanApplySettingsControl || frequency.SelectedValue is not UpdateCheckFrequency selected || !Enum.IsDefined(selected)) return;
            _updateCheckFrequency = selected;
            PersistSettingsImmediately();
            ArmUpdateSchedule();
        };
        AutomationProperties.SetName(frequency, "Частота проверки обновлений");
        panel.Children.Add(frequencyLabel); panel.Children.Add(frequency);
        panel.Children.Add(new TextBlock { Text = "Только пока Yamuse открыт. Изменения применяются сразу.", FontSize = 12,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) });
        panel.Children.Add(text);
        panel.Children.Add(button);
        panel.Children.Add(checkActions);
        void Refresh()
        {
            var state = updates.State;
            var message = AppUpdateMaintenanceActive ? AppUpdateMaintenanceMessage
                : _startupUpdateRestartRequested ? "Перезапускаем приложение…" : _appUpdateFailure ?? state.Message;
            text.Text = "Версия " + state.CurrentVersion + "\n" + message;
            button.Content = state.Status switch
            {
                AppUpdateStatus.Available => state.IsInstalled ? "Скачать обновление" : "Открыть официальный выпуск",
                AppUpdateStatus.Downloaded => "Обновить и перезапустить",
                AppUpdateStatus.Checking => "Проверяем…",
                AppUpdateStatus.Downloading => "Скачиваем…",
                _ => "Проверить обновления"
            };
            button.IsEnabled = state.FeedConfigured
                && !_closeCommitInProgress && !AppUpdateMaintenanceActive && !_appUpdateActionInProgress && !_updateCheckInProgress && !_startupUpdateRestartRequested
                && state.Status is not AppUpdateStatus.Checking and not AppUpdateStatus.Downloading;
            checkActions.Visibility = state.IsInstalled && (state.Status is AppUpdateStatus.Available or AppUpdateStatus.Downloaded
                || state.Status == AppUpdateStatus.Checking && checkActions.Visibility == Visibility.Visible)
                ? Visibility.Visible : Visibility.Collapsed;
            checkButton.IsEnabled = button.IsEnabled;
        }
        panel.Loaded += (_, _) => { AppUpdateUiChanged += Refresh; Refresh(); };
        panel.Unloaded += (_, _) => AppUpdateUiChanged -= Refresh;
        checkButton.Click += async (_, _) =>
        {
            if (_closed || _closeCommitInProgress || _appUpdateActionInProgress || _updateCheckInProgress || _startupUpdateRestartRequested || AppUpdateMaintenanceActive) return;
            await CheckAppUpdateNowAsync(updates);
        };
        button.Click += async (_, _) =>
        {
            if (_closed || _closeCommitInProgress || _appUpdateActionInProgress || _updateCheckInProgress || _startupUpdateRestartRequested) return;
            if (AppUpdateMaintenanceActive) { Refresh(); return; }
            _appUpdateActionInProgress = true;
            _appUpdateFailure = null;
            button.IsEnabled = false;
            RefreshAppUpdateUi();
            try
            {
                if (updates.State.Status == AppUpdateStatus.Downloaded)
                {
                    await ApplyAppUpdateAsync(updates);
                }
                else if (updates.State.Status == AppUpdateStatus.Available)
                {
                    if (!updates.State.IsInstalled) { OpenUpdateLink(ReleasesUri); return; }
                    text.Text = "Скачиваем обновление…";
                    await updates.DownloadAsync(new Progress<int>(percent =>
                    { if (!_closed && panel.IsLoaded && updates.State.Status == AppUpdateStatus.Downloading) text.Text = $"Скачиваем обновление… {percent}%"; }), _lifetime.Token);
                }
                else
                {
                    text.Text = "Проверяем обновления…";
                    await CheckAppUpdateNowAsync(updates);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            { _diagnostics.LastErrorType = "Updates:" + ex.GetType().Name; _appUpdateFailure = AppUpdateRetryMessage; }
            finally
            {
                _appUpdateActionInProgress = false;
                if (!_closed) { RefreshAppUpdateUi(); ArmUpdateSchedule(); }
            }
        };
        Refresh();
        return panel;
    }
}
