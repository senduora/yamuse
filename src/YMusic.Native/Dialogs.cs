using System.Windows;
using System.Windows.Controls;

namespace YMusic.Native;

internal sealed class TextDialog : Window
{
    private readonly TextBox _text = new() { MinWidth = 340, Margin = new Thickness(0, 12, 0, 14) };
    private readonly ComboBox _visibility = new() { ItemsSource = new[] { "Приватный", "Публичный" }, SelectedIndex = 0, Margin = new Thickness(0, 0, 0, 14), Padding = new Thickness(8) };
    public string Value => _text.Text.Trim();
    public string VisibilityValue => _visibility.SelectedIndex == 1 ? "public" : "private";
    public TextDialog(Window owner, string title, string initial = "", bool privacy = false)
    {
        Owner = owner; Title = title; WindowStartupLocation = WindowStartupLocation.CenterOwner; SizeToContent = SizeToContent.WidthAndHeight;
        ResizeMode = ResizeMode.NoResize; ShowInTaskbar = false; Style = (Style)Application.Current.FindResource(typeof(Window));
        WindowAppearance.Apply(this);
        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(new TextBlock { Text = title, FontSize = 20 }); _text.Text = initial; panel.Children.Add(_text);
        if (privacy) panel.Children.Add(_visibility);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = "Отмена", IsCancel = true, Margin = new Thickness(0, 0, 8, 0) };
        var save = new Button { Content = "Сохранить", IsDefault = true, Style = (Style)Application.Current.FindResource("AccentButton"), IsEnabled = initial.Trim().Length > 0 };
        save.Click += (_, _) => { if (Value.Length > 0) DialogResult = true; };
        _text.TextChanged += (_, _) => save.IsEnabled = Value.Length is > 0 and <= 200;
        buttons.Children.Add(cancel); buttons.Children.Add(save); panel.Children.Add(buttons); Content = panel;
        Loaded += (_, _) => { _text.Focus(); _text.SelectAll(); };
    }
}

internal sealed class PlaylistDialog : Window
{
    private readonly ListBox _items;
    public CatalogItem? Selected => _items.SelectedItem as CatalogItem;
    public PlaylistDialog(Window owner, IReadOnlyList<CatalogItem> playlists)
    {
        Owner = owner; Title = "Выберите плейлист"; Width = 480; Height = 430; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false; Style = (Style)Application.Current.FindResource(typeof(Window));
        WindowAppearance.Apply(this);
        var grid = new Grid { Margin = new Thickness(20) };
        grid.RowDefinitions.Add(new RowDefinition()); grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _items = new ListBox { ItemsSource = playlists, DisplayMemberPath = "Title" }; grid.Children.Add(_items);
        var choose = new Button { Content = "Добавить", IsEnabled = false, Margin = new Thickness(0, 12, 0, 0), Style = (Style)Application.Current.FindResource("AccentButton") };
        _items.SelectionChanged += (_, _) => choose.IsEnabled = Selected is not null;
        choose.Click += (_, _) => DialogResult = true;
        Grid.SetRow(choose, 1); grid.Children.Add(choose); Content = grid;
    }
}
