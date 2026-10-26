using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;

namespace YMusic.Native;

public partial class MainWindow
{
    private ListSourceOrder? _trackOrder, _catalogOrder, _downloadOrder;
    private ListSortStore? _listSortStore;
    private Dictionary<string, ListSortRule> _listSorts = [];
    private bool _sortingReady, _syncSortUi;
    private bool _downloadedSortRefreshPending;
    private DateTime _lastDownloadSortRefresh;
    private string SortContext => _section == "download-center" ? "downloads" : _catalogShowing
        ? _section == "search" ? "catalog:search:" + (_collectionsSearch ? "collections" : _searchType) : "catalog:" + _section
        : "tracks:" + _section;
    private ListSortRule CurrentListSort => _listSorts.GetValueOrDefault(SortContext) ?? new();
    private static readonly ListSortOption[] TrackSortOptions = [new(ListSortField.Original, "Исходный порядок"), new(ListSortField.Title, "По названию"),
        new(ListSortField.Artist, "По исполнителю"), new(ListSortField.Album, "По альбому"), new(ListSortField.Duration, "По длительности"), new(ListSortField.Downloaded, "По скачиванию")];
    private static readonly ListSortOption[] NameSortOptions = [TrackSortOptions[0], TrackSortOptions[1]];
    private static readonly ListSortOption[] AlbumSortOptions = [TrackSortOptions[0], TrackSortOptions[1], TrackSortOptions[2]];
    private static readonly ListSortOption[] DownloadSortOptions = [TrackSortOptions[0], TrackSortOptions[1], TrackSortOptions[2],
        new(ListSortField.State, "По состоянию"), new(ListSortField.Progress, "По прогрессу"), new(ListSortField.Size, "По размеру"), new(ListSortField.Error, "По причине ошибки")];
    private ListSortOption[] CurrentSortOptions => _section == "download-center" ? DownloadSortOptions : !_catalogShowing ? TrackSortOptions
        : _section == "albums" || _section == "search" && _searchType == "album" && !_collectionsSearch ? AlbumSortOptions
        : _section == "search" && !_collectionsSearch && _searchType is "all" or "track" ? TrackSortOptions : NameSortOptions;

    private void InitializeSortingSources()
    {
        _trackOrder = new(_tracks); _catalogOrder = new(_catalogItems); _downloadOrder = new(_downloadQueue.Entries);
    }
    private void InitializeSorting()
    {
        if (_restore && !_demo) { _listSortStore = new(_diagnosticsDirectory); _listSorts = _listSortStore.Load(); }
        _sortingReady = true; ApplyListSorting(false);
    }
    private void ApplyListSorting(bool preserveSelection)
    {
        if (!_sortingReady) return;
        var options = CurrentSortOptions;
        var rule = CurrentListSort;
        if (!options.Any(o => o.Field == rule.Field)) rule = new();
        _syncSortUi = true;
        try
        {
            ListSortCombo.ItemsSource = options; ListSortCombo.SelectedValue = rule.Field;
            SortDirectionButton.IsEnabled = rule.Field != ListSortField.Original;
            SortDirectionButton.Content = rule.Descending ? "↓" : "↑";
            SortDirectionButton.ToolTip = rule.Descending ? "По убыванию · переключить по возрастанию" : "По возрастанию · переключить по убыванию";
            AutomationProperties.SetName(SortDirectionButton, (string)SortDirectionButton.ToolTip);
            ListSortControls.Visibility = ListSortStore.ValidContext(SortContext) ? Visibility.Visible : Visibility.Collapsed;
            ListSortControls.ToolTip = _section == "queue" ? "Сортировка списка сохраняет порядок воспроизведения. Его можно менять кнопками выше списка." : "Сортировка сохраняется для этого раздела. Исходный порядок возвращает порядок сервиса.";
        }
        finally { _syncSortUi = false; }
        if (_section == "download-center")
        {
            var view = (ListCollectionView)CollectionViewSource.GetDefaultView(_downloadQueue.Entries);
            view.CustomSort = rule.Field == ListSortField.Original ? null : new StableListComparer(rule, _downloadOrder!);
            UpdateDownloadSortHeaders(rule);
            return;
        }
        if (_catalogShowing)
        {
            var selected = preserveSelection ? CatalogList.SelectedItem : null;
            var view = (ListCollectionView)CollectionViewSource.GetDefaultView(_catalogItems);
            Dictionary<string, int>? groups = view.GroupDescriptions.Count == 0 ? null
                : _catalogItems.Select(item => item.Kind).Distinct().Select((kind, index) => (kind, index)).ToDictionary(p => p.kind, p => p.index);
            view.CustomSort = rule.Field == ListSortField.Original ? null : new StableListComparer(rule, _catalogOrder!, groups);
            if (selected is not null) CatalogList.SelectedItem = selected;
        }
        else
        {
            var queueIndex = preserveSelection && _section == "queue" ? SelectedQueueIndex() : -1;
            var selected = preserveSelection ? TracksList.SelectedItems.Cast<TrackRow>().ToArray() : [];
            ((ListCollectionView)_view).CustomSort = rule.Field == ListSortField.Original ? null : new StableListComparer(rule, _trackOrder!);
            if (queueIndex >= 0) SelectVisibleQueueOccurrence(queueIndex);
            else if (preserveSelection)
                foreach (var row in selected) if (_view.Contains(row) && !TracksList.SelectedItems.Contains(row)) TracksList.SelectedItems.Add(row);
        }
        UpdateSortHeaders(rule);
    }
    private void UpdateSortHeaders(ListSortRule rule)
    {
        if (TracksList.View is not GridView grid) return;
        foreach (var column in grid.Columns)
            if (column.Header is GridViewColumnHeader { Tag: string key } header && Enum.TryParse<ListSortField>(key, out var field))
            {
                var label = field switch { ListSortField.Title => "НАЗВАНИЕ", ListSortField.Artist => "ИСПОЛНИТЕЛЬ", ListSortField.Album => "АЛЬБОМ", ListSortField.Duration => "ВРЕМЯ", ListSortField.Downloaded => "↓", _ => "#" };
                header.Content = label + (rule.Field == field && field != ListSortField.Original ? rule.Descending ? " ↓" : " ↑" : "");
                header.ToolTip = field == ListSortField.Original ? "Вернуть исходный порядок" : field == ListSortField.Downloaded
                    ? "Сортировать по наличию скачанного файла" : "Сортировать: " + label.ToLower(System.Globalization.CultureInfo.CurrentCulture);
                AutomationProperties.SetName(header, (string)header.ToolTip);
            }
    }
    private void ChangeListSort(ListSortRule rule)
    {
        if (!_sortingReady || _syncSortUi || !ListSortStore.ValidContext(SortContext)) return;
        _listSorts[SortContext] = rule;
        ApplyListSorting(true);
        if (IsPlaybackContextView()) _playbackContextSort = rule;
        try { _listSortStore?.Save(_listSorts); }
        catch (Exception e) when (e is System.IO.IOException or UnauthorizedAccessException)
        { _diagnostics.LastErrorType = "ListSortSave:" + e.GetType().Name; SetStatus("Сортировка применена, но её не удалось сохранить на диске.", true); return; }
        if (_section == "queue") SetStatus("Список отсортирован · порядок воспроизведения сохранён");
    }
    private void ListSort_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_sortingReady || _syncSortUi || ListSortCombo.SelectedItem is not ListSortOption option) return;
        ChangeListSort(new(option.Field, option.Field == ListSortField.Downloaded));
    }
    private void SortDirection_Click(object sender, RoutedEventArgs e)
    { if (CurrentListSort.Field != ListSortField.Original) ChangeListSort(CurrentListSort with { Descending = !CurrentListSort.Descending }); }
    private void TrackSort_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not GridViewColumnHeader { Tag: string key } || !Enum.TryParse<ListSortField>(key, out var field)) return;
        e.Handled = true;
        var current = CurrentListSort;
        var firstDescending = field == ListSortField.Downloaded;
        ChangeListSort(field == ListSortField.Original ? new() : current.Field == field
            ? current.Descending != firstDescending ? new() : current with { Descending = !current.Descending } : new(field, firstDescending));
    }
    private void QueueDownloadedSortRefresh()
    {
        if (!_sortingReady || _downloadedSortRefreshPending || CurrentListSort.Field != ListSortField.Downloaded) return;
        _downloadedSortRefreshPending = true;
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, new Action(() =>
        {
            _downloadedSortRefreshPending = false;
            if (!_closed && CurrentListSort.Field == ListSortField.Downloaded) ApplyListSorting(true);
        }));
    }
    private void RefreshDownloadSort(bool force)
    {
        if (_section != "download-center" || CurrentListSort.Field is not (ListSortField.State or ListSortField.Progress or ListSortField.Size or ListSortField.Error)
            || !force && DateTime.UtcNow - _lastDownloadSortRefresh < TimeSpan.FromSeconds(1)) return;
        _lastDownloadSortRefresh = DateTime.UtcNow;
        CollectionViewSource.GetDefaultView(_downloadQueue.Entries).Refresh();
    }
    private GridViewColumnHeader DownloadSortHeader(string title, ListSortField field)
    {
        var header = new GridViewColumnHeader { Content = title, Tag = field, ToolTip = "Сортировать: " + title.ToLower(System.Globalization.CultureInfo.CurrentCulture), Cursor = System.Windows.Input.Cursors.Hand };
        header.Click += (_, e) =>
        {
            e.Handled = true;
            var rule = CurrentListSort;
            ChangeListSort(rule.Field == field ? rule.Descending ? new() : rule with { Descending = true } : new(field));
        };
        return header;
    }
    private void UpdateDownloadSortHeaders(ListSortRule rule)
    {
        if (_downloadCenterView?.Children.OfType<ListView>().SingleOrDefault()?.View is not GridView grid) return;
        foreach (var column in grid.Columns)
            if (column.Header is GridViewColumnHeader { Tag: ListSortField field } header)
            {
                var title = field switch { ListSortField.Title => "Трек / исполнитель", ListSortField.State => "Состояние", ListSortField.Progress => "Прогресс", ListSortField.Size => "Получено / размер", _ => "Причина ошибки" };
                header.Content = title + (rule.Field == field ? rule.Descending ? " ↓" : " ↑" : "");
            }
    }
    private void CloseSorting()
    { _trackOrder?.Dispose(); _catalogOrder?.Dispose(); _downloadOrder?.Dispose(); }
}
