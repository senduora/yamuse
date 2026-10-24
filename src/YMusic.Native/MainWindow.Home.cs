using System.Windows;

namespace YMusic.Native;

public partial class MainWindow
{
    private IReadOnlyList<HomeSection> _homeSections = [];

    private sealed record HomeShelf(HomeSection Source, IReadOnlyList<CatalogItem> Items)
    {
        public string Title => Source.Title;
        public string? Note => Source.Note;
    }

    private void ShowHomeSections(IReadOnlyList<HomeSection> sections)
    {
        _homeSections = sections.ToArray();
        var shelves = _homeSections.Where(section => section.Items.Count > 0)
            .Select(section => new HomeShelf(section, section.Items.Take(6).ToArray())).ToArray();
        HomeShelves.ItemsSource = shelves;
        var name = _account?.DisplayName.Trim();
        HomeHeading.Text = string.IsNullOrWhiteSpace(name) ? "Ваша музыка" : "Для вас, " + name;
        var notes = _homeSections.Select(section => section.Note).Where(note => !string.IsNullOrWhiteSpace(note)).Distinct().ToArray();
        HomeStatus.Text = notes.Length == 0 ? "Подборки, новые открытия и музыка под ваше настроение" : string.Join(" · ", notes);
        HomeEmpty.Text = "Яндекс пока не предложил подборки.\nОткройте волну или найдите музыку через поиск.";
        HomeEmpty.Visibility = shelves.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        HomeScroll.ScrollToTop();
        Motion.Enter(HomeShelves, 5);
    }

    private async void HomeItem_Click(object sender, RoutedEventArgs e)
    {
        if (_closed || sender is not FrameworkElement element) return;
        var item = element.Tag as CatalogItem ?? element.DataContext as CatalogItem;
        if (item is null) return;
        if (_session is null) { SetStatus("Войдите в Яндекс, чтобы открыть музыку."); return; }
        await OpenCatalogAsync(item);
    }

    private void HomeShelf_Click(object sender, RoutedEventArgs e)
    {
        if (_closed || sender is not FrameworkElement element) return;
        var section = element.Tag as HomeSection ?? (element.DataContext as HomeShelf)?.Source;
        if (section is null) return;
        RememberBrowseView();
        SetSection("home-shelf", section.Title);
        ShowCatalogItems(section.Items);
        SetStatus($"{section.Title} · {section.Items.Count:N0}");
    }
}
