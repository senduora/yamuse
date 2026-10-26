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
    private Border? _startupUpdatePrompt;
    private TextBlock? _startupUpdateText;
    private Button? _startupUpdateButton, _startupUpdateLater;
    private string? _startupUpdateFailure;
    private bool AppUpdateMaintenanceActive => _offlineMaintenance || _cacheMaintenance;
    private const string AppUpdateMaintenanceMessage = "Дождитесь завершения очистки музыки или кэша и повторите обновление.";

    private async Task CheckForAppUpdatesAsync()
    {
        if (_closed || _startupUpdateCheckStarted) return;
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
            if (_closed || _lifetime.IsCancellationRequested) return;
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
        FooterUpdateButton.IsEnabled = state.FeedConfigured && !_appUpdateActionInProgress && !_updateCheckInProgress
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
        _startupUpdateText!.Text = AppUpdateMaintenanceActive ? AppUpdateMaintenanceMessage : _startupUpdateFailure ?? (state.Status switch
        {
            AppUpdateStatus.Downloading => $"Скачиваем обновление… {state.ProgressPercent ?? 0}%",
            AppUpdateStatus.Checking => "Проверяем обновление…",
            _ when _startupUpdateRestartRequested => "Перезапускаем приложение…",
            _ when _appUpdateActionInProgress => "Готовим перезапуск…",
            AppUpdateStatus.Downloaded => $"Версия {state.AvailableVersion} загружена и готова к установке.",
            _ => $"Версия {state.AvailableVersion}. Приложение сохранит состояние и перезапустится."
        });
        _startupUpdateButton!.IsEnabled = !AppUpdateMaintenanceActive && !_appUpdateActionInProgress && !_startupUpdateRestartRequested
            && state.Status is not AppUpdateStatus.Checking and not AppUpdateStatus.Downloading;
        _startupUpdateLater!.IsEnabled = !_appUpdateActionInProgress && !_startupUpdateRestartRequested;
    }

    private async Task InstallStartupUpdateAsync()
    {
        if (_closed || _startupUpdateDismissed || _appUpdateActionInProgress || _startupUpdateRestartRequested) return;
        if (AppUpdateMaintenanceActive) { RefreshAppUpdateUi(); return; }
        var updates = AppUpdates;
        _appUpdateActionInProgress = true;
        _startupUpdateFailure = null;
        RefreshAppUpdateUi();
        try
        {
            // A failed download/apply is retriable. Rechecking also discovers a
            // package that was downloaded successfully before a restart failed.
            if (updates.State.Status is not AppUpdateStatus.Available and not AppUpdateStatus.Downloaded)
                await updates.CheckAsync(_lifetime.Token);
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
                _startupUpdateRestartRequested = await ApplyAppUpdateAsync(updates);
            else
                _startupUpdateFailure = updates.State.Status == AppUpdateStatus.Error
                    ? "Не удалось обновить приложение. Проверьте соединение и повторите."
                    : "Обновление сейчас недоступно. Можно проверить его позже в настройках.";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _diagnostics.LastErrorType = "Updates:" + ex.GetType().Name;
            _startupUpdateFailure = "Не удалось обновить приложение. Повторите позже.";
        }
        finally
        {
            _appUpdateActionInProgress = false;
            if (!_closed) RefreshAppUpdateUi();
        }
    }

    private async Task<bool> ApplyAppUpdateAsync(UpdateSupport updates)
    {
        if (_closed || _lifetime.IsCancellationRequested) return false;
        if (AppUpdateMaintenanceActive) { RefreshAppUpdateUi(); return false; }
        SuspendDownloadQueueForAccountChange();
        // Preserve committed preferences, and finish an atomic offline write
        // before the SDK exits the process. Closing the window cancels this wait.
        // Queue suspension already blocks automatic downloads. The preference
        // remains the user's choice and can change while this wait yields.
        var pending = _downloadTask;
        _downloadCts?.Cancel();
        if (pending is not null)
            try { await pending.WaitAsync(_lifetime.Token); } catch (OperationCanceledException) { }
        if (_closed || _lifetime.IsCancellationRequested) return false;
        // Cleanup can start while the pending download yields. Recheck on
        // the dispatcher immediately before saving state and exiting.
        if (AppUpdateMaintenanceActive) { RefreshAppUpdateUi(); return false; }
        SavePreferences(); SavePlaybackState();
        updates.ApplyAndRestart();
        return true;
    }

    private FrameworkElement CreateUpdateSettingsPanel()
        => CreateUpdateSettingsPanelCore(AppUpdates);

    private FrameworkElement CreateUpdateSettingsPanelCore(UpdateSupport updates)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 4, 0, 20) };
        panel.Children.Add(new TextBlock { Text = "Обновления", FontSize = 17, FontWeight = FontWeights.SemiBold });
        var text = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 10) };
        var button = new Button { HorizontalAlignment = HorizontalAlignment.Left, Name = "AppUpdateButton" };
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
        void Refresh()
        {
            var state = updates.State;
            text.Text = "Версия " + state.CurrentVersion + "\n" + (AppUpdateMaintenanceActive ? AppUpdateMaintenanceMessage : state.Message);
            button.Content = state.Status switch
            {
                AppUpdateStatus.Available => state.IsInstalled ? "Скачать обновление" : "Открыть официальный выпуск",
                AppUpdateStatus.Downloaded => "Обновить и перезапустить",
                AppUpdateStatus.Checking => "Проверяем…",
                AppUpdateStatus.Downloading => "Скачиваем…",
                _ => "Проверить обновления"
            };
            button.IsEnabled = state.FeedConfigured
                && !AppUpdateMaintenanceActive && !_appUpdateActionInProgress && !_updateCheckInProgress && state.Status is not AppUpdateStatus.Checking and not AppUpdateStatus.Downloading;
        }
        panel.Loaded += (_, _) => { AppUpdateUiChanged += Refresh; Refresh(); };
        panel.Unloaded += (_, _) => AppUpdateUiChanged -= Refresh;
        button.Click += async (_, _) =>
        {
            if (_closed || _appUpdateActionInProgress || _updateCheckInProgress) return;
            if (AppUpdateMaintenanceActive) { Refresh(); return; }
            _appUpdateActionInProgress = true;
            button.IsEnabled = false;
            RefreshAppUpdateUi();
            string? failure = null;
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
                    { if (panel.IsLoaded) text.Text = $"Скачиваем обновление… {percent}%"; }), _lifetime.Token);
                }
                else
                {
                    text.Text = "Проверяем обновления…";
                    await CheckAppUpdateNowAsync(updates);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            { _diagnostics.LastErrorType = "Updates:" + ex.GetType().Name; failure = "Не удалось обновить приложение. Повторите позже."; }
            finally
            {
                _appUpdateActionInProgress = false;
                if (!_closed) { RefreshAppUpdateUi(); ArmUpdateSchedule(); }
                if (failure is not null && panel.IsLoaded) text.Text = failure;
            }
        };
        Refresh();
        return panel;
    }
}
