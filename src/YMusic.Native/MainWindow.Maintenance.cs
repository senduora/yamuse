using System.Windows;

namespace YMusic.Native;

public partial class MainWindow
{
    private bool _cacheMaintenance;
    private int _maintenanceGeneration;
    private void ShowMaintenance(string text, double? percent)
    {
        MaintenancePanel.Visibility = Visibility.Visible;
        MaintenanceStatus.Text = text;
        MaintenanceProgress.Value = Math.Clamp(percent ?? 0, 0, 100);
        MaintenanceProgress.Visibility = percent is null ? Visibility.Collapsed : Visibility.Visible;
    }
    private void RefreshMaintenanceActions()
    {
        SettingsClearOffline.IsEnabled = SettingsClearCache.IsEnabled = !_offlineMaintenance && !_cacheMaintenance;
        SettingsUninstall.IsEnabled = _uninstallListener is not null && !_offlineMaintenance && !_cacheMaintenance;
        RefreshAppUpdateUi();
    }
    private void ReportOfflineCleanup(OfflineCleanupProgress progress)
    {
        if (_closed || !_offlineMaintenance) return;
        var percent = progress.Total == 0 ? 100 : 80.0 * progress.Verified / progress.Total;
        var text = progress.Phase switch
        {
            "verify" => $"Проверяем файлы · {progress.Verified:N0} из {progress.Total:N0}",
            "commit" => "Сохраняем изменения библиотеки…",
            "delete" => $"Удаляем проверенные файлы · {progress.Removed:N0} из {progress.Total:N0}",
            _ => "Очистка завершена"
        };
        if (progress.Phase == "commit") percent = 85;
        if (progress.Phase == "delete") percent = progress.Total == 0 ? 100 : 90.0 + 10.0 * progress.Removed / progress.Total;
        if (progress.Phase == "complete") percent = 100;
        ShowMaintenance(text, percent);
    }
    private async void SettingsClearCache_Click(object sender, RoutedEventArgs e) => await ClearCachesAsync();
    private async Task ClearCachesAsync()
    {
        if (_cacheMaintenance || _offlineMaintenance || _closed) return;
        _cacheMaintenance = true; _maintenanceGeneration++; RefreshMaintenanceActions();
        ShowMaintenance("Очищаем кэш…", null);
        try
        {
            var artwork = await Artwork.Cache.ClearAsync(_lifetime.Token);
            var analysis = await _player.ClearAnalysisCacheAsync(_lifetime.Token);
            if (_closed) return;
            var removed = artwork.Removed + analysis.Removed;
            var preserved = artwork.Preserved + analysis.Preserved;
            var failed = artwork.Failed + analysis.Failed;
            ShowMaintenance($"Кэш очищен · удалено {removed:N0} файлов · {DownloadEntry.FormatBytes(artwork.RemovedBytes + analysis.RemovedBytes)}"
                + (preserved > 0 ? $" · пропущено {preserved}" : "") + (failed > 0 ? $" · не удалось удалить {failed}" : ""), 100);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception error) { if (!_closed) { ShowMaintenance("Не удалось полностью очистить кэш. Можно повторить.", null); _diagnostics.LastErrorType = "CacheClear:" + error.GetType().Name; } }
        finally { _cacheMaintenance = false; if (!_closed) RefreshMaintenanceActions(); }
    }
}
