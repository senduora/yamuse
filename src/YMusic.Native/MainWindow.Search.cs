using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;

namespace YMusic.Native;

public partial class MainWindow
{
    private bool _collectionsSearch;

    private async void SearchCategory_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string type }) return;
        _collectionsSearch = type == "collections";
        if (!_collectionsSearch)
            CatalogType.SelectedItem = CatalogType.Items.OfType<ComboBoxItem>().First(item => (string)item.Tag == type);
        UpdateSearchCategories();
        if (CatalogQuery.Text.Trim().Length > 0) await SearchCatalogViewAsync(false);
        else
        {
            _featureCts?.Cancel(); ResetSearchResults();
            ShowCatalogItems([]); SetStatus("Введите название или имя исполнителя и нажмите Enter.");
        }
    }

    private void UpdateSearchCategories()
    {
        if (SearchCategories is null) return;
        var selected = _collectionsSearch ? "collections" : (CatalogType.SelectedItem as ComboBoxItem)?.Tag as string ?? "all";
        foreach (var button in SearchCategories.Children.OfType<Button>())
        {
            var active = (string)button.Tag == selected;
            button.Background = active ? (Brush)FindResource("AccentSoftBrush") : Brushes.Transparent;
            button.Foreground = (Brush)FindResource(active ? "AccentTextBrush" : "MutedBrush");
            System.Windows.Automation.AutomationProperties.SetHelpText(button, active ? "Выбрано" : "");
        }
    }

    private void GroupCatalogResults()
    {
        var view = (ListCollectionView)CollectionViewSource.GetDefaultView(_catalogItems);
        using (view.DeferRefresh())
        {
            view.GroupDescriptions.Clear();
            if (_section == "search" && !_collectionsSearch && _searchType == "all")
                view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(CatalogItem.Kind)));
        }
        ApplyListSorting(false);
        UpdateSearchCategories();
    }

    private async void CatalogOpen_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: CatalogItem item }) return;
        e.Handled = true;
        await OpenCatalogAsync(item);
    }

    private static bool FromButton(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is Button) return true;
            source = source is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(source) : LogicalTreeHelper.GetParent(source);
        }
        return false;
    }
}
