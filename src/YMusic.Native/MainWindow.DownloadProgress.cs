using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;

namespace YMusic.Native;

public partial class MainWindow
{
    private ProgressBar? _queueOverallProgress;
    private TextBlock? _queueProgressDetails;
    private FrameworkElement? _queueProgressPanel;
    private CompactDownloadOperation? _compactDownloadOperation;
    private StackPanel? _compactDownloadContent;
    private CircularDownloadProgress? _compactDownloadRing;
    private TextBlock? _compactDownloadCaption;
    private DataTemplate? _downloadActionTemplate;
    private object? _downloadIdleContent;

    private static ControlTemplate SoftProgressTemplate()
    {
        var track = new FrameworkElementFactory(typeof(Grid), "PART_Track");
        track.SetValue(FrameworkElement.ClipToBoundsProperty, true);
        var rail = new FrameworkElementFactory(typeof(Border));
        rail.SetValue(Border.CornerRadiusProperty, new CornerRadius(3));
        rail.SetValue(Border.BackgroundProperty, ThemeManager.Brush("#34363E"));
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
        _queueOverallProgress.SetResourceReference(Control.ForegroundProperty, "DownloadBrush");
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
        // Playlist progress lives in its action button; this legacy row reserves no space.
        if (PlaylistTransferPanel is { } playlistPanel) playlistPanel.Visibility = Visibility.Collapsed;
        RefreshCompactDownloadAction();
        if (_queueOverallProgress is null) return;
        var progress = PlaylistTransferProgress.Read(_downloadQueue.Entries);
        _queueProgressPanel!.Visibility = progress.Total > 0 ? Visibility.Visible : Visibility.Collapsed;
        _queueOverallProgress.Value = progress.Percent;
        _queueProgressDetails!.Text = $"Обработано {progress.Finished:N0} из {progress.Total:N0} · {progress.Percent:0}% · " + progress.Details;
        _queueOverallProgress.ToolTip = "Общий прогресс обработки треков. Сохранённые, ошибки и недоступные показаны отдельно.";
        PlaylistTransferBar.Value = progress.Percent;
        PlaylistTransferTitle.Text = _downloadQueue.Running ? $"Скачивание · {progress.Percent:0}%"
            : _downloadQueue.HasPaused ? "Скачивание на паузе" : _downloadQueue.Suspended ? "Скачивание остановлено"
            : progress.Failed > 0 || progress.Unavailable > 0 ? "Не скачана часть треков" : "Плейлист скачан";
        PlaylistTransferDetails.Text = $"Скачано {progress.Completed:N0} из {progress.Total:N0}" +
            (progress.Failed > 0 ? $" · ошибок {progress.Failed}" : "") +
            (progress.Unavailable > 0 ? $" · недоступно {progress.Unavailable}" : "");
    }

    private bool IsCurrentCompactDownload()
    {
        if (_closed || _session is null || _compactDownloadOperation is not { Playlist: { } playlist } operation
            || operation.AccountGeneration != _accountGeneration) return false;
        return _section == "favorites" ? playlist.Owner == LocalFavoritesOwner && playlist.Kind == LocalFavoritesKind
            : _section == "playlist-detail" && _activePlaylist is { } current && current.Owner == playlist.Owner && current.Kind == playlist.Kind;
    }
    private bool RefreshCompactDownloadAction()
    {
        if (DownloadPlaylistButton is null) return false;
        var operation = _compactDownloadOperation;
        if (!IsCurrentCompactDownload() || operation is null || operation.Total == 0 || operation.Complete && !_downloadQueue.Running)
        { RestoreDownloadActionContent(); return false; }
        if (_compactDownloadContent is null)
        {
            _compactDownloadRing = new CircularDownloadProgress { Name = "CompactDownloadRing", Margin = new(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
            _compactDownloadRing.SetResourceReference(Control.ForegroundProperty, "DownloadBrush");
            _compactDownloadCaption = new TextBlock { Name = "CompactDownloadCaption", VerticalAlignment = VerticalAlignment.Center };
            _compactDownloadContent = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            _compactDownloadContent.Children.Add(_compactDownloadRing); _compactDownloadContent.Children.Add(_compactDownloadCaption);
        }
        if (!ReferenceEquals(DownloadPlaylistButton.Content, _compactDownloadContent))
        {
            _downloadIdleContent = DownloadPlaylistButton.Content;
            _downloadActionTemplate = DownloadPlaylistButton.ContentTemplate;
            DownloadPlaylistButton.ContentTemplate = null;
            DownloadPlaylistButton.Content = _compactDownloadContent;
        }
        var caption = _downloadQueue.Suspended || operation.Suspended > 0 ? "Остановлено"
            : _downloadCts?.IsCancellationRequested == true || operation.Paused > 0 || !_downloadQueue.Running && _downloadQueue.HasPaused ? "На паузе"
            : _downloadQueue.Running ? "Скачивается" : "Не скачано";
        _compactDownloadRing!.Value = operation.Percent;
        _compactDownloadCaption!.Text = $"{caption} · {operation.Completed}/{operation.Total}";
        var details = $"Скачано {operation.Completed} из {operation.Total} треков текущей загрузки" +
            (operation.Failed > 0 ? $" · ошибок {operation.Failed}" : "") +
            (operation.Unavailable > 0 ? $" · недоступно {operation.Unavailable}" : "");
        DownloadPlaylistButton.IsEnabled = !_offlineMaintenance;
        DownloadPlaylistButton.ToolTip = details + ". Открыть «Загрузки»: пауза, продолжение и повтор.";
        AutomationProperties.SetName(DownloadPlaylistButton, _compactDownloadCaption.Text + ". Открыть загрузки");
        AutomationProperties.SetHelpText(DownloadPlaylistButton, details);
        return true;
    }
    private void RestoreDownloadActionContent()
    {
        if (_compactDownloadContent is null || !ReferenceEquals(DownloadPlaylistButton.Content, _compactDownloadContent)) return;
        DownloadPlaylistButton.ContentTemplate = _downloadActionTemplate;
        DownloadPlaylistButton.Content = _downloadIdleContent;
        DownloadPlaylistButton.ClearValue(AutomationProperties.NameProperty);
        DownloadPlaylistButton.ClearValue(AutomationProperties.HelpTextProperty);
        DownloadPlaylistButton.ClearValue(ToolTipProperty);
    }
    private void RefreshDownloadActivity()
    {
        if (_section == "download-center") RefreshDownloadCenter();
        else RefreshCompactDownloadAction();
    }
}
