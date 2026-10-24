using System.Windows;
using System.Windows.Controls;

namespace YMusic.Native;

public partial class MainWindow
{
    private event Action? AppUpdateUiChanged;

    private async Task CheckForAppUpdatesAsync()
    {
        try
        {
            var updates = UpdateSupport.Current;
            if (!updates.State.FeedConfigured || !updates.State.IsInstalled) return;
            await updates.CheckAsync(_lifetime.Token);
            if (!_closed) RefreshAppUpdateUi();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _diagnostics.LastErrorType = "Updates:" + ex.GetType().Name; }
    }

    private void RefreshAppUpdateUi()
    {
        var state = UpdateSupport.Current.State;
        var available = state.Status is AppUpdateStatus.Available or AppUpdateStatus.Downloaded;
        SettingsButton.Content = available ? "Настройки · •" : "Настройки";
        SettingsButton.ToolTip = available ? "Доступно обновление " + state.AvailableVersion : "Настройки";
        AppUpdateUiChanged?.Invoke();
    }

    private FrameworkElement CreateUpdateSettingsPanel()
        => CreateUpdateSettingsPanelCore(UpdateSupport.Current);

    private FrameworkElement CreateUpdateSettingsPanelCore(UpdateSupport updates)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 4, 0, 20) };
        panel.Children.Add(new TextBlock { Text = "Обновления", FontSize = 17, FontWeight = FontWeights.SemiBold });
        var text = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 10) };
        var button = new Button { HorizontalAlignment = HorizontalAlignment.Left, Name = "AppUpdateButton" };
        panel.Children.Add(text); panel.Children.Add(button);
        void Refresh()
        {
            var state = updates.State;
            text.Text = "Версия " + state.CurrentVersion + "\n" + state.Message;
            button.Content = state.Status switch
            {
                AppUpdateStatus.Available => "Скачать обновление",
                AppUpdateStatus.Downloaded => "Обновить и перезапустить",
                AppUpdateStatus.Checking => "Проверяем…",
                AppUpdateStatus.Downloading => "Скачиваем…",
                _ => "Проверить обновления"
            };
            button.IsEnabled = state.FeedConfigured && state.IsInstalled
                && state.Status is not AppUpdateStatus.Checking and not AppUpdateStatus.Downloading;
        }
        panel.Loaded += (_, _) => { AppUpdateUiChanged += Refresh; Refresh(); };
        panel.Unloaded += (_, _) => AppUpdateUiChanged -= Refresh;
        button.Click += async (_, _) =>
        {
            button.IsEnabled = false;
            string? failure = null;
            try
            {
                if (updates.State.Status == AppUpdateStatus.Downloaded)
                {
                    SavePreferences();
                    SavePlaybackState();
                    SuspendDownloadQueueForAccountChange();
                    // Let an in-progress atomic offline write finish before the
                    // updater exits this process. Preserve the user's stored opt-in.
                    var autoBefore = _autoFavoritesEnabled;
                    try
                    {
                        _autoFavoritesEnabled = false;
                        var pending = _downloadTask;
                        _downloadCts?.Cancel();
                        if (pending is not null)
                            try { await pending; } catch (OperationCanceledException) { }
                        updates.ApplyAndRestart();
                    }
                    finally { _autoFavoritesEnabled = autoBefore; }
                }
                else if (updates.State.Status == AppUpdateStatus.Available)
                {
                    text.Text = "Скачиваем обновление…";
                    await updates.DownloadAsync(new Progress<int>(percent =>
                    { if (panel.IsLoaded) text.Text = $"Скачиваем обновление… {percent}%"; }), _lifetime.Token);
                }
                else
                {
                    text.Text = "Проверяем обновления…";
                    await updates.CheckAsync(_lifetime.Token);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            { _diagnostics.LastErrorType = "Updates:" + ex.GetType().Name; failure = "Не удалось обновить приложение. Повторите позже."; }
            finally
            {
                if (!_closed) RefreshAppUpdateUi();
                if (failure is not null && panel.IsLoaded) text.Text = failure;
            }
        };
        Refresh();
        return panel;
    }
}
