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
    private DownloadEntry[]? _downloadUnlikeMenuEntries;
    private int _downloadUnlikeMenuGeneration;
    private BatchSelectionListView? _downloadEntriesList;
    private string? _downloadQueueAccountUid;
    private int _downloadQueueAccountGeneration;
    private bool DownloadQueueBelongsToActiveAccount => _session is not null && _account is { } account
        && _downloadQueueAccountUid == account.Uid && _downloadQueueAccountGeneration == _accountGeneration;
    private bool HasActiveOwnedDownload => DownloadQueueBelongsToActiveAccount && _downloadQueue.Running
        && _downloadCts is { IsCancellationRequested: false };
    private bool CanPauseActiveDownload => !_closed && !_offlineMaintenance && !_closeCommitInProgress
        && !_appUpdatePreparingRestart && HasActiveOwnedDownload;
    private bool _downloadsAutoSuspended;
    private PlaylistDetails? _downloadQueuePlaylist;
    private DownloadPlaylistPersistence? _downloadQueuePlaylistPersistence;
    private bool _downloadQueuePlaylistHistoryOnly;
    private bool _downloadQueueRecordPlaylistOccurrences = true;
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
        var list = new BatchSelectionListView { Name = "DownloadEntriesList", HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Background = Brushes.Transparent, Foreground = Foreground, BorderThickness = new(0), ItemContainerStyle = DownloadRowStyle() };
        list.SetResourceReference(StyleProperty, typeof(ListView));
        _downloadEntriesList = list; list.SelectionMode = SelectionMode.Extended;
        list.SelectionChanged += (_, _) => RefreshDownloadUnlikeAction();
        list.PreviewMouseRightButtonDown += (_, e) => e.Handled = true;
        list.ContextMenuOpening += (_, e) =>
        {
            var keyboard = e.CursorLeft == -1 && e.CursorTop == -1;
            if (keyboard && ReferenceEquals(e.OriginalSource, list)) return;
            var row = SelectDownloadContextRow(e.OriginalSource as DependencyObject);
            if (!keyboard && row is not null) MoveContextRangeAnchor(row);
        };
        _downloadUnlikeMenu = new MenuItem { Header = "Убрать из «Мне нравится»" };
        _downloadUnlikeMenu.Click += async (_, _) =>
        {
            if (_downloadUnlikeMenuGeneration == _accountGeneration && _downloadUnlikeMenuEntries is { } entries)
                await UnlikeDownloadsAsync(entries);
        };
        list.ContextMenu = new ContextMenu(); list.ContextMenu.Items.Add(_downloadUnlikeMenu);
        list.ContextMenu.Opened += (_, _) =>
        {
            _downloadUnlikeMenuEntries = list.SelectedItems.OfType<DownloadEntry>().ToArray();
            _downloadUnlikeMenuGeneration = _accountGeneration;
            RefreshDownloadUnlikeAction();
        };
        list.ContextMenu.Closed += (_, _) => { _downloadUnlikeMenuEntries = null; RefreshDownloadUnlikeAction(); };
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
        RefreshTrackDownloadButton();
        RefreshTransferProgress();
        if (_downloadCenterSummary is null) return;
        var visible = DownloadQueueBelongsToActiveAccount;
        var entries = visible ? _downloadQueue.Entries : null;
        if (!ReferenceEquals(_downloadEntriesList!.ItemsSource, entries)) _downloadEntriesList.ItemsSource = entries;
        _downloadCenterSummary.Text = visible ? _downloadQueue.Summary : DownloadQueueState.EmptyLabel;
        _downloadCenterNotice!.Text = DownloadQueueUsesMemoryOnly ? DownloadQueueMemoryOnlyNotice
            : visible ? _downloadQueue.Notice : DownloadQueueState.EmptyNotice;
        RefreshDownloadUnlikeAction();
        RefreshDownloadSort(force: !_downloadQueue.Running);
        _downloadPauseButton!.IsEnabled = CanPauseActiveDownload;
    }
    private void RefreshTrackDownloadButton()
    {
        if (DownloadButton is null) return;
        DownloadButton.Content = _downloadCts is not null ? "✕" : "↓";
        if (!_offline.IsAvailable)
        {
            DownloadButton.ToolTip = BrandPaths.OfflineUnavailableMessage;
            DownloadButton.IsEnabled = false;
            return;
        }
        DownloadButton.ToolTip = _downloadCts is not null ? "Отменить загрузку" : "Скачать выбранные треки";
        DownloadButton.IsEnabled = _downloadCts is not null ? CanPauseActiveDownload
            : !_offlineMaintenance && !_catalogShowing && _session is not null && !DownloadQueueRestoreCanRetry
                && TracksList.SelectedItems.Cast<TrackRow>().Any(track => track is { Available: true, LocalPath: null });
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
        if (CanPauseActiveDownload) PauseDownloadQueueCore();
    }
    private void PauseDownloadQueueCore()
    {
        ++_downloadQueueAdmissionRevision;
        _downloadsAutoSuspended = true; ClearPendingAutoFavorites();
        _downloadQueue.Pause(); _downloadCts!.Cancel();
        RemoveUnstartedAutomaticFavorites();
        RefreshDownloadCenter();
        _ = PersistDownloadQueueAsync(force: true);
    }
    private Task ResumeDownloadQueue() => ContinueDownloadQueue(retryFailed: false);
    private Task RetryFailedDownloads() => ContinueDownloadQueue(retryFailed: true);
    private long PauseDownloadQueueForAppUpdate()
    {
        // Even an idle queue can have a single-removal restart waiting for refresh.
        var admissionRevision = ++_downloadQueueAdmissionRevision;
        if (_autoFavoritesEnabled && DownloadQueueBelongsToActiveAccount && _pendingConfirmedFavorites.Count > 0
            && _downloadQueue.AppendAutomaticFavorites(_pendingConfirmedFavorites.Values, SelectedQuality,
                admit: CanAdmitCurrentDownloadQueue, requeueExisting: true)
                == DownloadQueueAppendResult.Rejected)
            SetStatus(AutomaticDownloadAdmissionMessage, true);
        if (_downloadQueue.Running || _downloadQueue.CanResume || _downloadCts is not null)
        {
            _downloadsAutoSuspended = true;
            ClearPendingAutoFavorites();
            _downloadQueue.Pause(); _downloadCts?.Cancel(); RefreshDownloadCenter();
        }
        return admissionRevision;
    }
    private Task ContinueDownloadQueue(bool retryFailed)
    {
        if (_downloadCts is not null || _offlineMaintenance || _session is null || _closed || _closeCommitInProgress || DownloadQueueRestorePending
            || DownloadQueueRestoreCanRetry
            || _catalogWriting || _downloadQueue.Suspended) return Task.CompletedTask;
        if (!DownloadQueueBelongsToActiveAccount) return Task.CompletedTask;
        if (!CanAdmitCurrentDownloadQueue(_downloadQueue.Entries))
        {
            SetStatus("Очередь загрузок не помещается в файл восстановления. Продолжение отложено.", true);
            return Task.CompletedTask;
        }
        if (!retryFailed && _offline.IsAvailable && _downloadQueue.CanResume)
        {
            _downloadsAutoSuspended = false;
            _autoFavoritesRemovalRestart = null;
        }
        return RunDownloadQueueAsync(retryFailed);
    }
    private bool SuspendDownloadQueueForAccountChange(bool keepQueueVisible = false, bool explicitLogout = false)
    {
        var checkpointCleared = ClearPersistedDownloadQueue(invalidateOnFailure: !keepQueueVisible, explicitLogout);
        if (!checkpointCleared && keepQueueVisible) return false;
        _compactDownloadOperation?.Dispose();
        if (!keepQueueVisible)
        {
            _downloadQueueAccountUid = null;
            _deletedUntilFavoritesRefresh.Clear(); _deletedUntilConfirmedLike.Clear();
        }
        _downloadsAutoSuspended = true; ClearPendingAutoFavorites();
        _downloadQueue.Suspend(); _downloadCts?.Cancel(); RefreshDownloadCenter();
        return checkpointCleared;
    }
    private void DiscardUnstartedAutomaticFavorites()
    {
        if (!RemoveUnstartedAutomaticFavorites()) return;
        RefreshDownloadCenter();
        _ = PersistDownloadQueueAsync(force: true);
    }
    private bool RemoveUnstartedAutomaticFavorites()
    {
        if (!_downloadQueue.DiscardUnstartedAutomaticFavorites()) return false;
        if (_downloadQueuePlaylist is { } playlist)
        {
            var retainedIds = _downloadQueue.Entries.Select(entry => entry.Track.Id).ToHashSet(StringComparer.Ordinal);
            var retainedTracks = playlist.Tracks.Where(track => retainedIds.Contains(track.Id)).ToArray();
            if (retainedTracks.Length != playlist.Tracks.Count)
            {
                _downloadQueuePlaylist = playlist with { Tracks = retainedTracks };
                if (_downloadQueuePlaylistPersistence is { } persistence)
                    _downloadQueuePlaylistPersistence = persistence with
                    { Playlist = persistence.Playlist with { Tracks = persistence.Playlist.Tracks.Where(track => retainedIds.Contains(track.Id)).ToArray() } };
                _downloadQueuePlaylistHistoryOnly = true;
            }
        }
        return true;
    }
    private void ReconcileDownloadQueueFavorites(string uid, IReadOnlySet<string> confirmedIds)
    {
        if (uid != _downloadQueueAccountUid || !DownloadQueueBelongsToActiveAccount || _downloadQueue.Suspended) return;
        if (_downloadQueue.ReconcileFavorites(confirmedIds, ClearOfflineDeletionSuppression)) RefreshDownloadCenter();
    }
    private async void DownloadCenter_Click(object sender, RoutedEventArgs e)
    {
        if (!_offline.IsAvailable) { SetStatus(BrandPaths.OfflineUnavailableMessage, true); return; }
        SetSection("download-center", "Центр загрузок"); DownloadCenterNav.BringIntoView(); RefreshDownloadCenter(); RefreshDownloadStorage();
        await RetryDownloadQueueRestoreAsync();
    }

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
        if (_downloadUnlikeMenu is not null)
        {
            var menuCount = _downloadUnlikeMenuEntries is null ? count : _downloadUnlikeMenuGeneration == _accountGeneration
                ? _downloadUnlikeMenuEntries.Where(CanUnlikeDownload).DistinctBy(entry => entry.Track.Id).Count() : 0;
            _downloadUnlikeMenu.Header = menuCount > 1 ? $"Убрать из «Мне нравится» · {menuCount}" : "Убрать из «Мне нравится»";
            _downloadUnlikeMenu.IsEnabled = menuCount > 0;
            _downloadUnlikeMenu.ToolTip = menuCount > 0
                ? $"Убрать из «Мне нравится» выбранные треки: {menuCount}. Скачанные файлы останутся на диске." : hint;
        }
        _downloadSelectUnavailableButton!.IsEnabled = _downloadQueue.Entries.Any(entry => IsUnavailableDownload(entry) && CanUnlikeDownload(entry));
        _downloadSelectionText!.Text = selected.Length == 0 ? "Ctrl / Shift — выбор" : $"Выбрано: {selected.Length}";
        var sameAccount = DownloadQueueBelongsToActiveAccount && _downloadCts is null;
        _downloadResumeButton!.IsEnabled = _downloadQueue.CanResume && sameAccount && !_offlineMaintenance && !_closed && !_catalogWriting;
        _downloadRetryButton!.IsEnabled = _downloadQueue.CanRetry && sameAccount && !_offlineMaintenance && !_closed && !_catalogWriting;
    }
    private ListViewItem? SelectDownloadContextRow(DependencyObject? origin)
    {
        if (_downloadEntriesList is null) return null;
        ListViewItem? selected = null;
        if (origin is not null && ItemsControl.ContainerFromElement(_downloadEntriesList, origin) is ListViewItem item)
        {
            if (!item.IsSelected) { _downloadEntriesList.SelectedItems.Clear(); item.IsSelected = true; }
            selected = item;
        }
        else _downloadEntriesList.SelectedItems.Clear();
        RefreshDownloadUnlikeAction();
        return selected;
    }
    private static bool IsUnavailableDownload(DownloadEntry entry) => !entry.Track.Available || entry.State == DownloadEntryState.Unavailable;
    private void SelectUnavailableDownloads()
    {
        if (_downloadEntriesList is null || _catalogWriting) return;
        var entries = _downloadEntriesList.Items.OfType<DownloadEntry>().Where(entry => IsUnavailableDownload(entry) && CanUnlikeDownload(entry)).ToArray();
        _downloadEntriesList.ReplaceSelection(entries);
        RefreshDownloadUnlikeAction();
    }
    private Task UnlikeSelectedDownloadAsync() => UnlikeDownloadsAsync(_downloadEntriesList?.SelectedItems.OfType<DownloadEntry>() ?? []);
    private async Task UnlikeDownloadsAsync(IEnumerable<DownloadEntry> selection)
    {
        if (_downloadEntriesList is null || _catalogWriting || _downloadUnlikeTotal > 0) return;
        var entries = selection.Where(CanUnlikeDownload).DistinctBy(entry => entry.Track.Id).ToArray();
        if (entries.Length == 0 || _account is null) return;
        var uid = _account.Uid; var generation = _accountGeneration;
        _downloadUnlikeTotal = entries.Length; _downloadUnlikeCompleted = 0;
        var needsRefresh = false;
        try
        {
            await CatalogMutationAsync(async (session, token) =>
            {
                foreach (var entry in entries)
                {
                    token.ThrowIfCancellationRequested();
                    if (_closed || generation != _accountGeneration || _account?.Uid != uid) throw new OperationCanceledException(token);
                    IReadOnlyList<LikedId> readback;
                    try { readback = await _api.SetTrackLikeAsync(session, uid, TrackReference(entry.Track), false, token); }
                    catch (Exception error) when (error is MusicException or HttpRequestException)
                    { throw new MusicException($"Снято лайков: {_downloadUnlikeCompleted} из {entries.Length}. Обработка остановлена. {error.Message}"); }
                    token.ThrowIfCancellationRequested();
                    if (_closed || generation != _accountGeneration || _account?.Uid != uid) throw new OperationCanceledException(token);
                    needsRefresh = !ApplyConfirmedTrackLikeResult(entry.Track, false, readback, showStatus: false, deferFollowUp: true);
                    _downloadUnlikeCompleted++;
                    RefreshDownloadUnlikeAction();
                }
                return _downloadUnlikeCompleted;
            }, count => SetStatus(count == 1 ? "Трек убран из избранного" : $"Из избранного убраны треки: {count}"), requireSameView: false);
        }
        finally
        {
            _downloadUnlikeTotal = _downloadUnlikeCompleted = 0;
            RefreshDownloadUnlikeAction();
            if (!_closed && generation == _accountGeneration && _account?.Uid == uid)
            {
                if (needsRefresh) _ = RefreshLibraryAsync();
                TryStartPendingAutoFavorites();
            }
        }
    }
}
