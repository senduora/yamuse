using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;

namespace YMusic.Native;

public partial class MainWindow
{
    private ProgressBar? _queueOverallProgress;
    private TextBlock? _queueProgressDetails;
    private FrameworkElement? _queueProgressPanel;

    private static ControlTemplate SoftProgressTemplate()
    {
        var track = new FrameworkElementFactory(typeof(Grid), "PART_Track");
        track.SetValue(FrameworkElement.ClipToBoundsProperty, true);
        var rail = new FrameworkElementFactory(typeof(Border));
        rail.SetValue(Border.CornerRadiusProperty, new CornerRadius(3));
        rail.SetValue(Border.BackgroundProperty, new SolidColorBrush(Color.FromRgb(52, 54, 62)));
        track.AppendChild(rail);
        var fill = new FrameworkElementFactory(typeof(Border), "PART_Indicator");
        fill.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Left);
        fill.SetValue(Border.CornerRadiusProperty, new CornerRadius(3));
        fill.SetBinding(Border.BackgroundProperty, new Binding("Foreground") { RelativeSource = RelativeSource.TemplatedParent });
        track.AppendChild(fill);
        return new ControlTemplate(typeof(ProgressBar)) { VisualTree = track };
    }

    private FrameworkElement InitializeDownloadProgress()
    {
        var panel = new StackPanel { Margin = new(0, 0, 0, 12) };
        _queueProgressPanel = panel;
        _queueOverallProgress = new ProgressBar { Name = "DownloadOverallProgress", Minimum = 0, Maximum = 100,
            Height = 6, Template = SoftProgressTemplate() };
        _queueOverallProgress.SetResourceReference(Control.ForegroundProperty, "AccentBrush");
        panel.Children.Add(_queueOverallProgress);
        _queueProgressDetails = new TextBlock { Name = "DownloadProgressDetails", FontSize = 12, Foreground = (Brush)FindResource("MutedBrush"),
            TextWrapping = TextWrapping.Wrap, Margin = new(0, 7, 0, 0) };
        panel.Children.Add(_queueProgressDetails);
        PlaylistTransferBar.Template = SoftProgressTemplate();
        MaintenanceProgress.Template = SoftProgressTemplate();
        return panel;
    }

    private void RefreshTransferProgress()
    {
        if (_queueOverallProgress is null) return;
        var progress = PlaylistTransferProgress.Read(_downloadQueue.Entries);
        _queueProgressPanel!.Visibility = progress.Total > 0 ? Visibility.Visible : Visibility.Collapsed;
        _queueOverallProgress.Value = progress.Percent;
        _queueProgressDetails!.Text = $"Обработано {progress.Finished:N0} из {progress.Total:N0} · {progress.Percent:0}% · " + progress.Details;
        _queueOverallProgress.ToolTip = "Общий прогресс обработки треков. Сохранённые, ошибки и недоступные показаны отдельно.";
        var matching = _downloadQueuePlaylist is { } queuePlaylist && (_section == "favorites"
            ? queuePlaylist.Owner == LocalFavoritesOwner && queuePlaylist.Kind == LocalFavoritesKind
            : _activePlaylist is { } current && current.Owner == queuePlaylist.Owner && current.Kind == queuePlaylist.Kind);
        var complete = progress.Total > 0 && progress.Completed == progress.Total;
        PlaylistTransferPanel.Visibility = progress.Total > 0 && !complete && matching && _section is "favorites" or "playlist-detail"
            ? Visibility.Visible : Visibility.Collapsed;
        PlaylistTransferBar.Value = progress.Percent;
        PlaylistTransferTitle.Text = _downloadQueue.Running ? $"Скачивание · {progress.Percent:0}%"
            : _downloadQueue.HasPaused ? "Скачивание на паузе" : _downloadQueue.Suspended ? "Скачивание остановлено"
            : progress.Failed > 0 || progress.Unavailable > 0 ? "Не скачана часть треков" : "Плейлист скачан";
        PlaylistTransferDetails.Text = $"Скачано {progress.Completed:N0} из {progress.Total:N0}" +
            (progress.Failed > 0 ? $" · ошибок {progress.Failed}" : "") +
            (progress.Unavailable > 0 ? $" · недоступно {progress.Unavailable}" : "");
    }
}
