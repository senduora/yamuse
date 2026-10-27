using System.Net.Http;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;

namespace YMusic.Native;

public partial class MainWindow
{
    private readonly DownloadQueueState _downloadQueue = new();
    private readonly Dictionary<DownloadEntry, DownloadProgressSink> _downloadProgress = [];
    private Grid? _downloadCenterView;
    private TextBlock? _downloadCenterSummary, _downloadCenterNotice;
    private Button? _downloadPauseButton, _downloadResumeButton, _downloadRetryButton, _downloadStorageButton;
    private Button? _downloadUnlikeButton, _downloadSelectUnavailableButton;
    private TextBlock? _downloadSelectionText;
    private int _downloadUnlikeTotal, _downloadUnlikeCompleted;
    private MenuItem? _downloadUnlikeMenu;
    private BatchSelectionListView? _downloadEntriesList;
    private string? _downloadQueueAccountUid;
    private int _downloadQueueAccountGeneration;
    private bool _downloadsAutoSuspended;
    private PlaylistDetails? _downloadQueuePlaylist;
    private AudioQuality _downloadQueueQuality;
    private bool _downloadQueueAutomatic;
    internal FrameworkElement DownloadCenterView { get { InitializeDownloadCenter(); return _downloadCenterView!; } }

    private void InitializeDownloadCenter()
    {
        if (_downloadCenterView is not null) return;
        var grid = _downloadCenterView = new Grid();
        grid.RowDefinitions.Add(new() { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new() { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new() { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        _downloadCenterSummary = new TextBlock { Name = "DownloadCenterSummary", TextWrapping = TextWrapping.Wrap, FontSize = 14, Margin = new(0, 0, 0, 12) };
        var overview = new StackPanel();
        var intro = new TextBlock { Name = "DownloadCenterIntro", Text = "Очередь, прогресс и ошибки скачивания. Готовая музыка — в разделе «Скачанное».",
            TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new(0, 0, 0, 12) };
        intro.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        overview.Children.Add(intro); overview.Children.Add(_downloadCenterSummary);
        overview.Children.Add(InitializeDownloadProgress());
        var overviewCard = new Border { Child = overview, CornerRadius = new(12), Padding = new(16), Margin = new(0, 0, 0, 14), BorderThickness = new(1) };
        overviewCard.SetResourceReference(Border.BackgroundProperty, "ControlBrush");
        overviewCard.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
        grid.Children.Add(overviewCard);
        var actions = new WrapPanel { Margin = new(0, 0, 0, 8) }; Grid.SetRow(actions, 1); grid.Children.Add(actions);
        _downloadPauseButton = AddButton("Пауза", "DownloadPauseButton", (_, _) => PauseDownloadQueue());
        _downloadResumeButton = AddButton("Продолжить", "DownloadResumeButton", async (_, _) => await ResumeDownloadQueue());
        _downloadRetryButton = AddButton("Повторить", "DownloadRetryButton", async (_, _) => await RetryFailedDownloads());
        _downloadRetryButton.ToolTip = "Повторить только неудачные загрузки";
        var libraryButton = AddButton("Открыть скачанное →", "DownloadLibraryButton", Offline_Click);
        libraryButton.ToolTip = "Перейти к готовой офлайн-музыке";
        _downloadSelectUnavailableButton = AddButton("Недоступные", "DownloadSelectUnavailableButton", (_, _) => SelectUnavailableDownloads());
        _downloadSelectUnavailableButton.ToolTip = "Выделить недоступные треки из «Мне нравится». Для своего выбора используйте Ctrl или Shift.";
        _downloadUnlikeButton = AddButton("Убрать лайк", "DownloadUnlikeButton", async (_, _) => await UnlikeSelectedDownloadAsync());
        _downloadStorageButton = AddButton("Занято: …", "DownloadStorageButton", (_, _) => RefreshDownloadStorage());
        _downloadStorageButton.ToolTip = "Размер файлов из индекса приложения. Посторонние файлы не учитываются. Нажмите, чтобы обновить.";
        _downloadSelectionText = new TextBlock { Name = "DownloadSelectionText", FontSize = 11, Foreground = (Brush)FindResource("MutedBrush"), VerticalAlignment = VerticalAlignment.Center, Margin = new(4, 0, 0, 4) };
        actions.Children.Add(_downloadSelectionText);
        _downloadCenterNotice = new TextBlock { Name = "DownloadCenterNotice", TextWrapping = TextWrapping.Wrap, FontSize = 12, Foreground = (Brush)FindResource("MutedBrush"), Margin = new(0, 0, 0, 14) };
        Grid.SetRow(_downloadCenterNotice, 2); grid.Children.Add(_downloadCenterNotice);
        var list = new BatchSelectionListView { Name = "DownloadEntriesList", ItemsSource = _downloadQueue.Entries, HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Background = Brushes.Transparent, Foreground = Foreground, BorderThickness = new(0), ItemContainerStyle = DownloadRowStyle() };
        list.SetResourceReference(StyleProperty, typeof(ListView));
        _downloadEntriesList = list; list.SelectionMode = SelectionMode.Extended;
        list.SelectionChanged += (_, _) => RefreshDownloadUnlikeAction();
        list.PreviewMouseRightButtonDown += (_, e) => { SelectDownloadContextRow(e.OriginalSource as DependencyObject); e.Handled = true; };
        _downloadUnlikeMenu = new MenuItem { Header = "Убрать из «Мне нравится»" };
        _downloadUnlikeMenu.Click += async (_, _) => await UnlikeSelectedDownloadAsync();
        list.ContextMenu = new ContextMenu(); list.ContextMenu.Items.Add(_downloadUnlikeMenu);
        list.ContextMenu.Opened += (_, _) => RefreshDownloadUnlikeAction();
        AutomationProperties.SetName(list, "Треки в очереди загрузки");
        ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Auto);
        VirtualizingPanel.SetIsVirtualizing(list, true); VirtualizingPanel.SetVirtualizationMode(list, VirtualizationMode.Recycling);
        var columns = new GridView { AllowsColumnReorder = false, ColumnHeaderContainerStyle = DownloadHeaderStyle() };
        columns.Columns.Add(new() { Header = DownloadSortHeader("Трек / исполнитель", ListSortField.Title), Width = 230, CellTemplate = TrackTemplate() });
        columns.Columns.Add(new() { Header = DownloadSortHeader("Состояние", ListSortField.State), Width = 130, DisplayMemberBinding = new Binding(nameof(DownloadEntry.StatusText)) });
        columns.Columns.Add(new() { Header = DownloadSortHeader("Прогресс", ListSortField.Progress), Width = 112, CellTemplate = ProgressTemplate() });
        columns.Columns.Add(new() { Header = DownloadSortHeader("Получено / размер", ListSortField.Size), Width = 172, DisplayMemberBinding = new Binding(nameof(DownloadEntry.SizeText)) });
        columns.Columns.Add(new() { Header = DownloadSortHeader("Причина ошибки", ListSortField.Error), Width = 290, CellTemplate = TextTemplate(nameof(DownloadEntry.Error), wrap: true) });
        list.View = columns; Grid.SetRow(list, 3); grid.Children.Add(list);
        RefreshDownloadCenter(); RefreshDownloadStorage();

        Button AddButton(string text, string name, RoutedEventHandler click)
        {
            var button = new Button { Name = name, Content = text, Margin = new(0, 0, 6, 4), Padding = new(10, 0, 10, 0), Height = 32, FontSize = 12 };
            AutomationProperties.SetName(button, text); button.Click += click; actions.Children.Add(button); return button;
        }
    }
    private Style DownloadHeaderStyle()
    {
        var style = new Style(typeof(GridViewColumnHeader));
        style.Setters.Add(new Setter(Control.ForegroundProperty, FindResource("MutedBrush")));
        style.Setters.Add(new Setter(Control.FontSizeProperty, 11.0));
        var border = new FrameworkElementFactory(typeof(Border)); border.SetValue(Border.PaddingProperty, new Thickness(8, 9, 8, 9));
        var content = new FrameworkElementFactory(typeof(ContentPresenter)); content.SetBinding(ContentPresenter.ContentProperty, new Binding("Content") { RelativeSource = RelativeSource.TemplatedParent }); border.AppendChild(content);
        style.Setters.Add(new Setter(Control.TemplateProperty, new ControlTemplate(typeof(GridViewColumnHeader)) { VisualTree = border })); return style;
    }
    private static Style DownloadRowStyle()
    {
        var style = new Style(typeof(ListViewItem));
        style.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.Transparent));
        style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(8, 9, 8, 9)));
        style.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
        var border = new FrameworkElementFactory(typeof(Border)); border.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));
        border.SetBinding(Border.BackgroundProperty, new Binding("Background") { RelativeSource = RelativeSource.TemplatedParent });
        var row = new FrameworkElementFactory(typeof(GridViewRowPresenter));
        row.SetBinding(GridViewRowPresenter.ContentProperty, new Binding("Content") { RelativeSource = RelativeSource.TemplatedParent });
        row.SetBinding(GridViewRowPresenter.ColumnsProperty, new Binding("View.Columns") { RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(ListView), 1) });
        row.SetBinding(MarginProperty, new Binding("Padding") { RelativeSource = RelativeSource.TemplatedParent }); border.AppendChild(row);
        style.Setters.Add(new Setter(Control.TemplateProperty, new ControlTemplate(typeof(ListViewItem)) { VisualTree = border }));
        var hover = new Trigger { Property = IsMouseOverProperty, Value = true }; hover.Setters.Add(new Setter(Control.BackgroundProperty, ThemeManager.Brush("#202227"))); style.Triggers.Add(hover);
        var selected = new Trigger { Property = ListViewItem.IsSelectedProperty, Value = true }; selected.Setters.Add(new Setter(Control.BackgroundProperty, ThemeManager.Brush("#2B2D34"))); style.Triggers.Add(selected);
        return style;
    }
    private static DataTemplate TrackTemplate()
    {
        var panel = new FrameworkElementFactory(typeof(StackPanel));
        var title = new FrameworkElementFactory(typeof(TextBlock)); title.SetBinding(TextBlock.TextProperty, new Binding(nameof(DownloadEntry.Title))); title.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
        title.SetBinding(ToolTipProperty, new Binding(nameof(DownloadEntry.Title))); panel.AppendChild(title);
        var artist = new FrameworkElementFactory(typeof(TextBlock)); artist.SetBinding(TextBlock.TextProperty, new Binding(nameof(DownloadEntry.Artist))); artist.SetValue(TextBlock.FontSizeProperty, 11.0); artist.SetValue(TextBlock.ForegroundProperty, ThemeManager.Brush("#ADB0BA")); artist.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis); panel.AppendChild(artist);
        var collection = new FrameworkElementFactory(typeof(TextBlock)); collection.SetBinding(TextBlock.TextProperty, new Binding(nameof(DownloadEntry.CollectionText)));
        collection.SetValue(TextBlock.FontSizeProperty, 11.0); collection.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 4, 0, 0));
        collection.SetValue(TextBlock.ForegroundProperty, ThemeManager.Brush("#BEB5B5"));
        var style = new Style(typeof(TextBlock)); var empty = new DataTrigger { Binding = new Binding(nameof(DownloadEntry.CollectionText)), Value = "" };
        empty.Setters.Add(new Setter(UIElement.VisibilityProperty, Visibility.Collapsed)); style.Triggers.Add(empty);
        collection.SetValue(FrameworkElement.StyleProperty, style); panel.AppendChild(collection);
        return new() { VisualTree = panel };
    }
    private static DataTemplate ProgressTemplate()
    {
        var bar = new FrameworkElementFactory(typeof(ProgressBar));
        bar.SetValue(Control.TemplateProperty, SoftProgressTemplate());
        bar.SetResourceReference(Control.ForegroundProperty, "DownloadBrush");
        bar.SetValue(FrameworkElement.WidthProperty, 92.0); bar.SetValue(FrameworkElement.HeightProperty, 5.0); bar.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        bar.SetValue(System.Windows.Controls.Primitives.RangeBase.MaximumProperty, 100.0);
        bar.SetBinding(System.Windows.Controls.Primitives.RangeBase.ValueProperty, new Binding(nameof(DownloadEntry.Percent)) { Mode = BindingMode.OneWay });
        bar.SetBinding(ProgressBar.IsIndeterminateProperty, new Binding(nameof(DownloadEntry.Indeterminate)));
        bar.SetBinding(ToolTipProperty, new Binding(nameof(DownloadEntry.SizeText)));
        return new() { VisualTree = bar };
    }
    private static DataTemplate TextTemplate(string property, bool wrap)
    {
        var text = new FrameworkElementFactory(typeof(TextBlock)); text.SetBinding(TextBlock.TextProperty, new Binding(property));
        text.SetValue(TextBlock.TextWrappingProperty, wrap ? TextWrapping.Wrap : TextWrapping.NoWrap);
        return new() { VisualTree = text };
    }
    private void RefreshDownloadCenter()
    {
        RefreshTransferProgress();
        if (_downloadCenterSummary is null) return;
        _downloadCenterSummary.Text = _downloadQueue.Summary;
        _downloadCenterNotice!.Text = _downloadQueue.Notice;
        RefreshDownloadUnlikeAction();
        RefreshDownloadSort(force: !_downloadQueue.Running);
        _downloadPauseButton!.IsEnabled = _downloadCts is not null && !_downloadCts.IsCancellationRequested;
    }
    private void RefreshDownloadStorage()
    {
        if (_downloadStorageButton is null) return;
        try
        {
            // Inspect only indexed paths when explicitly refreshed or a batch ends.
            // No directory enumeration or per-progress-tick filesystem work.
            long bytes = 0;
            foreach (var item in _offline.Read().DistinctBy(item => item.FileName, StringComparer.OrdinalIgnoreCase))
            {
                var file = new FileInfo(_offline.ResolvePath(item.FileName));
                if (file.Exists && (file.Attributes & FileAttributes.ReparsePoint) == 0 && file.Length == item.Bytes) bytes += item.Bytes;
            }
            _downloadStorageButton.Content = $"{DownloadEntry.FormatBytes(bytes)} ↻";
        }
        catch (Exception error) when (error is MusicException or IOException or UnauthorizedAccessException)
        { _downloadStorageButton.Content = "Размер недоступен ↻"; }
    }
    private void FlushDownloadProgress()
    {
        if (_downloadCts is null || _downloadCts.IsCancellationRequested) return;
        foreach (var (entry, sink) in _downloadProgress)
            if (sink.Take() is { } value) entry.Progress(value);
        RefreshDownloadActivity();
    }
    private void PauseDownloadQueue()
    {
        if (_downloadCts is null) return;
        _downloadsAutoSuspended = true; _pendingAutoFavorites = null;
        _downloadQueue.Pause(); _downloadCts.Cancel(); RefreshDownloadCenter();
        _ = PersistDownloadQueueAsync(force: true);
    }
    private Task ResumeDownloadQueue() => ContinueDownloadQueue(retryFailed: false);
    private Task RetryFailedDownloads() => ContinueDownloadQueue(retryFailed: true);
    private Task ContinueDownloadQueue(bool retryFailed)
    {
        if (_downloadCts is not null || _offlineMaintenance || _session is null || _closed || _catalogWriting || _downloadQueue.Suspended) return Task.CompletedTask;
        if (_downloadQueueAccountGeneration != _accountGeneration) { SuspendDownloadQueueForAccountChange(); return Task.CompletedTask; }
        return RunDownloadQueueAsync(retryFailed);
    }
    private void SuspendDownloadQueueForAccountChange()
    {
        ClearPersistedDownloadQueue();
        _compactDownloadOperation?.Dispose();
        _downloadsAutoSuspended = true; _pendingAutoFavorites = null;
        _downloadQueue.Suspend(); _downloadCts?.Cancel(); RefreshDownloadCenter();
    }
    private void DownloadCenter_Click(object sender, RoutedEventArgs e)
    { SetSection("download-center", "Центр загрузок"); DownloadCenterNav.BringIntoView(); RefreshDownloadCenter(); RefreshDownloadStorage(); }

    private bool CanUnlikeDownload(DownloadEntry? entry) => entry is not null && !entry.Active && !_closed && !_demo && !_catalogWriting
        && _session is not null && _account is not null && _downloadQueueAccountUid == _account.Uid
        && _downloadQueueAccountGeneration == _accountGeneration && _favoriteIdsKnown && _favoriteTrackIds.Contains(entry.Track.Id)
        && entry.Track.Id.Length > 0 && entry.Track.RequestId.Split(':', 2)[0] == entry.Track.Id && _downloadQueue.Entries.Contains(entry);
    private void RefreshDownloadUnlikeAction()
    {
        if (_downloadUnlikeButton is null || _downloadEntriesList is null) return;
        var selected = _downloadEntriesList.SelectedItems.OfType<DownloadEntry>().ToArray();
        var count = selected.Where(CanUnlikeDownload).DistinctBy(entry => entry.Track.Id).Count();
        var enabled = count > 0;
        var hint = enabled ? $"Убрать из «Мне нравится» выбранные треки: {count}. Скачанные файлы останутся на диске."
            : _downloadUnlikeTotal > 0 ? "Снимаем лайки с выбранных треков. Повторный запрос недоступен до завершения."
            : selected.Length > 0 && selected.All(entry => entry.RemovedFromCollection) ? "Лайки уже сняты. Треки убраны из «Мне нравится»."
            : "Выберите треки из «Мне нравится» с Ctrl или Shift. Отметки коллекции должны быть загружены.";
        _downloadUnlikeButton.Content = _downloadUnlikeTotal > 0 ? $"Снято {_downloadUnlikeCompleted}/{_downloadUnlikeTotal}" : count > 1 ? $"Убрать лайки · {count}" : "Убрать лайк";
        AutomationProperties.SetName(_downloadUnlikeButton, _downloadUnlikeButton.Content.ToString());
        _downloadUnlikeButton.IsEnabled = enabled; _downloadUnlikeButton.ToolTip = hint;
        if (_downloadUnlikeMenu is not null) { _downloadUnlikeMenu.Header = count > 1 ? $"Убрать из «Мне нравится» · {count}" : "Убрать из «Мне нравится»"; _downloadUnlikeMenu.IsEnabled = enabled; _downloadUnlikeMenu.ToolTip = hint; }
        _downloadSelectUnavailableButton!.IsEnabled = _downloadQueue.Entries.Any(entry => IsUnavailableDownload(entry) && CanUnlikeDownload(entry));
        _downloadSelectionText!.Text = selected.Length == 0 ? "Ctrl / Shift — выбор" : $"Выбрано: {selected.Length}";
        var sameAccount = _session is not null && _downloadQueueAccountGeneration == _accountGeneration;
        _downloadResumeButton!.IsEnabled = _downloadQueue.CanResume && sameAccount && !_offlineMaintenance && !_closed && !_catalogWriting;
        _downloadRetryButton!.IsEnabled = _downloadQueue.CanRetry && sameAccount && !_offlineMaintenance && !_closed && !_catalogWriting;
    }
    private void SelectDownloadContextRow(DependencyObject? origin)
    {
        if (_downloadEntriesList is null) return;
        if (origin is not null && ItemsControl.ContainerFromElement(_downloadEntriesList, origin) is ListViewItem item)
        { if (!item.IsSelected) { _downloadEntriesList.SelectedItems.Clear(); item.IsSelected = true; } }
        else _downloadEntriesList.SelectedItems.Clear();
        RefreshDownloadUnlikeAction();
    }
    private static bool IsUnavailableDownload(DownloadEntry entry) => !entry.Track.Available || entry.State == DownloadEntryState.Unavailable;
    private void SelectUnavailableDownloads()
    {
        if (_downloadEntriesList is null || _catalogWriting) return;
        var entries = _downloadEntriesList.Items.OfType<DownloadEntry>().Where(entry => IsUnavailableDownload(entry) && CanUnlikeDownload(entry)).ToArray();
        _downloadEntriesList.ReplaceSelection(entries);
        RefreshDownloadUnlikeAction();
    }
    private async Task UnlikeSelectedDownloadAsync()
    {
        if (_downloadEntriesList is null || _catalogWriting || _downloadUnlikeTotal > 0) return;
        var entries = _downloadEntriesList.SelectedItems.OfType<DownloadEntry>().Where(CanUnlikeDownload).DistinctBy(entry => entry.Track.Id).ToArray();
        if (entries.Length == 0 || _account is null) return;
        var uid = _account.Uid; var generation = _accountGeneration;
        _downloadUnlikeTotal = entries.Length; _downloadUnlikeCompleted = 0;
        try
        {
            await CatalogMutationAsync(async (session, token) =>
            {
                foreach (var entry in entries)
                {
                    token.ThrowIfCancellationRequested();
                    if (_closed || generation != _accountGeneration || _account?.Uid != uid) throw new OperationCanceledException(token);
                    try { await _api.SetTrackLikeAsync(session, uid, TrackReference(entry.Track), false, token); }
                    catch (Exception error) when (error is MusicException or HttpRequestException)
                    { throw new MusicException($"Снято лайков: {_downloadUnlikeCompleted} из {entries.Length}. Обработка остановлена. {error.Message}"); }
                    token.ThrowIfCancellationRequested();
                    if (_closed || generation != _accountGeneration || _account?.Uid != uid) throw new OperationCanceledException(token);
                    ApplyConfirmedTrackLike(entry.Track, false, showStatus: false);
                    _downloadUnlikeCompleted++;
                    RefreshDownloadUnlikeAction();
                }
                return _downloadUnlikeCompleted;
            }, count => SetStatus(count == 1 ? "Трек убран из избранного" : $"Из избранного убраны треки: {count}"), requireSameView: false);
        }
        finally { _downloadUnlikeTotal = _downloadUnlikeCompleted = 0; RefreshDownloadUnlikeAction(); }
    }
}
