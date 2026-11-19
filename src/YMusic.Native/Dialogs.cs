using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;

namespace YMusic.Native;

internal sealed class TextDialog : Window
{
    private readonly TextBox _text = new() { MinWidth = 340, Margin = new Thickness(0, 12, 0, 14) };
    private readonly ComboBox _visibility = new() { ItemsSource = new[] { "Приватный", "Публичный" }, SelectedIndex = 0, Margin = new Thickness(0, 0, 0, 14), Padding = new Thickness(8) };
    public string Value => _text.Text.Trim();
    public string VisibilityValue => _visibility.SelectedIndex == 1 ? "public" : "private";
    private bool CanSave => Value is { Length: > 0 and <= 200 } value && !value.Any(char.IsControl);
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
        var save = new Button { Content = "Сохранить", IsDefault = true, Style = (Style)Application.Current.FindResource("AccentButton"), IsEnabled = CanSave };
        save.Click += (_, _) => { if (CanSave) DialogResult = true; };
        _text.TextChanged += (_, _) => save.IsEnabled = CanSave;
        buttons.Children.Add(cancel); buttons.Children.Add(save); panel.Children.Add(buttons); Content = panel;
        Loaded += (_, _) => { _text.Focus(); _text.SelectAll(); };
    }
}

internal sealed class PlaylistDialog : Window
{
    private sealed record PlaylistChoice(CatalogItem Playlist, string Label);
    private readonly ListBox _items;
    public CatalogItem? Selected => (_items.SelectedItem as PlaylistChoice)?.Playlist;
    public PlaylistDialog(Window owner, IReadOnlyList<CatalogItem> playlists)
    {
        Owner = owner; Title = "Выберите плейлист"; Width = 480; Height = 430; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false; Style = (Style)Application.Current.FindResource(typeof(Window));
        WindowAppearance.Apply(this);
        var grid = new Grid { Margin = new Thickness(20) };
        grid.RowDefinitions.Add(new RowDefinition()); grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var text = new FrameworkElementFactory(typeof(TextBlock));
        text.SetBinding(TextBlock.TextProperty, new Binding(nameof(PlaylistChoice.Label)));
        text.SetValue(TextBlock.TextWrappingProperty, TextWrapping.Wrap);
        var itemStyle = new Style(typeof(ListBoxItem));
        itemStyle.Setters.Add(new Setter(AutomationProperties.NameProperty, new Binding(nameof(PlaylistChoice.Label))));
        itemStyle.Setters.Add(new Setter(HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
        _items = new ListBox { ItemsSource = PlaylistChoices(playlists), ItemTemplate = new DataTemplate { VisualTree = text }, ItemContainerStyle = itemStyle };
        ScrollViewer.SetHorizontalScrollBarVisibility(_items, ScrollBarVisibility.Disabled);
        grid.Children.Add(_items);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        var cancel = new Button { Content = "Отмена", IsCancel = true, Margin = new Thickness(0, 0, 8, 0) };
        var choose = new Button { Content = "Добавить", IsDefault = true, IsEnabled = false, Style = (Style)Application.Current.FindResource("AccentButton") };
        _items.SelectionChanged += (_, _) => choose.IsEnabled = Selected is not null;
        choose.Click += (_, _) => { if (Selected is not null) DialogResult = true; };
        buttons.Children.Add(cancel); buttons.Children.Add(choose);
        Grid.SetRow(buttons, 1); grid.Children.Add(buttons); Content = grid;
    }

    private static PlaylistChoice[] PlaylistChoices(IReadOnlyList<CatalogItem> playlists)
    {
        var duplicateTitles = playlists.GroupBy(item => item.Title.Trim(), StringComparer.CurrentCultureIgnoreCase)
            .Where(names => names.Skip(1).Any()).Select(names => names.Key).ToHashSet(StringComparer.CurrentCultureIgnoreCase);
        var choices = playlists.Select(playlist => new PlaylistChoice(playlist, Label(playlist))).ToArray();
        if (choices.Select(choice => choice.Label.Trim()).Distinct(StringComparer.CurrentCultureIgnoreCase).Count() == choices.Length)
            return choices;
        // A literal title can imitate a formatted label. Put the exact kind before all free text in this case.
        return playlists.Select(playlist => new PlaylistChoice(playlist, WithDescription(playlist, $"№{Kind(playlist)} · {playlist.Title}"))).ToArray();

        string Label(CatalogItem playlist)
        {
            if (!duplicateTitles.Contains(playlist.Title.Trim())) return playlist.Title;
            return WithDescription(playlist, $"{playlist.Title} · №{Kind(playlist)}");
        }

        static string Kind(CatalogItem playlist) => Uri.EscapeDataString(playlist.PlaylistKind ?? playlist.Id);

        static string WithDescription(CatalogItem playlist, string label)
        {
            var description = playlist.Subtitle.Trim();
            return description.Length == 0 ? label : $"{label} · {description}";
        }
    }
}
