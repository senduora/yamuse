using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;

internal static partial class Program
{
    private static async Task CheckControlChrome()
    {
        await Run("Control chrome: symmetric insets rounded surfaces and bounded content use actual global templates", () =>
        {
            var fixture = ChromeFixture();
            ChromeLayout(fixture.Root);
            foreach (var button in new[] { fixture.Button, fixture.Quiet })
            {
                Assert(button.Padding.Left == button.Padding.Right && button.Padding.Top == button.Padding.Bottom,
                    "button padding is asymmetric");
                var chrome = ChromePart<Border>(button, "ButtonChrome");
                var content = ChromePart<ContentPresenter>(button, "ButtonContent");
                var hover = ChromePart<Border>(button, "ButtonHover");
                Assert(chrome.CornerRadius == new CornerRadius(9) && hover.CornerRadius == new CornerRadius(8)
                    && hover.Margin == new Thickness(1), "button layers do not share rounded insets");
                Assert(ReferenceEquals(VisualTreeHelper.GetParent(content), chrome), "button content ignores the actual border thickness");
                var position = content.TranslatePoint(new Point(), button);
                Assert(position.X >= button.Padding.Left + button.BorderThickness.Left - .1
                    && position.Y >= button.Padding.Top + button.BorderThickness.Top - .1
                    && position.X + content.ActualWidth <= button.ActualWidth - button.Padding.Right - button.BorderThickness.Right + .1
                    && position.Y + content.ActualHeight <= button.ActualHeight - button.Padding.Bottom - button.BorderThickness.Bottom + .1,
                    "button text intersects its chrome or padding");
                Assert(button.FocusVisualStyle is null && !button.IsMouseOver && !button.IsKeyboardFocused,
                    "disconnected fixture is unexpectedly receiving input or native focus chrome");
            }
            Assert(fixture.Link.Padding.Left == fixture.Link.Padding.Right && fixture.Link.Padding.Top == fixture.Link.Padding.Bottom
                && fixture.Link.Template.FindName("LinkUnderline", fixture.Link) is null
                && ChromePart<Border>(fixture.Link, "LinkChrome").CornerRadius == new CornerRadius(6),
                "text link retains an underline or asymmetric hit area");
            var toggle = ChromePart<ToggleButton>(fixture.Combo, "DropDownToggle");
            Assert(toggle.Padding == fixture.Combo.Padding && !toggle.Focusable && !toggle.IsTabStop
                && toggle.FocusVisualStyle is null && fixture.Combo.FocusVisualStyle is null,
                "combo toggle ignores padding or adds a second focus target");
            Assert(ChromePart<Border>(fixture.Combo, "ComboChrome").CornerRadius == new CornerRadius(9), "combo corners differ from buttons");
            ChromeCapture(fixture.Root, "control-chrome-normal-selected-disabled.png");
            return Task.CompletedTask;
        });
        await Run("Control chrome: native dotted focus is replaced by inset accent rings and explicit keyboard triggers", () =>
        {
            var fixture = ChromeFixture(); ChromeLayout(fixture.Root);
            foreach (var (control, part, radius, margin, property) in new (Control, string, double, double, DependencyProperty)[]
            {
                (fixture.Button, "ButtonKeyboardFocus", 7, 2, YMusic.Native.InputFeedback.KeyboardFocusVisibleProperty),
                (fixture.Quiet, "ButtonKeyboardFocus", 7, 2, YMusic.Native.InputFeedback.KeyboardFocusVisibleProperty),
                (fixture.Link, "LinkKeyboardFocus", 4, 1, YMusic.Native.InputFeedback.KeyboardFocusVisibleProperty),
                (fixture.Combo, "ComboKeyboardFocus", 7, 2, YMusic.Native.InputFeedback.KeyboardFocusVisibleProperty),
                (fixture.SelectedItem, "ItemKeyboardFocus", 5, 2, YMusic.Native.InputFeedback.KeyboardFocusVisibleProperty)
            })
            {
                var ring = ChromePart<Border>(control, part);
                Assert(control.FocusVisualStyle is null && ring.Margin == new Thickness(margin)
                    && ring.CornerRadius == new CornerRadius(radius) && ring.BorderThickness == new Thickness(1)
                    && ring.Opacity == 0 && !ring.IsHitTestVisible && ReferenceEquals(ring.BorderBrush, Application.Current.Resources["AccentBrush"]),
                    "control has native/outer/interactive focus chrome instead of a bounded accent ring");
                Assert(ChromeTrigger(control, property, part, UIElement.OpacityProperty, 1d), "keyboard focus trigger does not reveal the internal ring");
            }
            var buttonFocus = new Control { Style = (Style)Application.Current.Resources["KeyboardButtonFocus"], Width = 200, Height = 40 };
            var linkFocus = new Control { Style = (Style)Application.Current.Resources["KeyboardLinkFocus"], Width = 200, Height = 36 };
            var previews = new StackPanel { Margin = new Thickness(24) };
            previews.Children.Add(ChromeLabel("Шаблоны индикации фокуса · статический просмотр, без ввода", 17));
            previews.Children.Add(new TextBlock { Text = "Оранжевая рамка находится внутри границ. Реальный keyboard focus здесь не вызывается.",
                Foreground = Brushes.LightGray, Margin = new Thickness(0, 8, 0, 20), TextWrapping = TextWrapping.Wrap });
            previews.Children.Add(ChromeFocusPreview(new Button { Content = "Обновить текст", Width = 200, Height = 40 }, buttonFocus));
            previews.Children.Add(ChromeFocusPreview(new Button { Content = "Название альбома", Style = (Style)Application.Current.Resources["TextLink"], Width = 200, Height = 36 }, linkFocus));
            var root = ChromeRoot(previews, 700, 240); ChromeLayout(root);
            foreach (var focus in new[] { buttonFocus, linkFocus })
            {
                var ring = (Border)VisualTreeHelper.GetChild(focus, 0);
                Assert(ring.Margin.Left >= 1 && ring.Margin.Top >= 1 && ring.BorderThickness == new Thickness(1)
                    && ring.CornerRadius.TopLeft >= 4 && !ring.IsHitTestVisible, "focus template uses an underline or out-of-bounds ring");
            }
            ChromeCapture(root, "control-chrome-focus-template-preview.png");
            return Task.CompletedTask;
        });
        await Run("Control chrome: selected disabled and hover rules retain branded colors without synthetic read-only input states", () =>
        {
            var fixture = ChromeFixture(); ChromeLayout(fixture.Root);
            var selected = ChromePart<Border>(fixture.SelectedItem, "Item");
            Assert(fixture.SelectedItem.IsSelected && ReferenceEquals(selected.Background, Application.Current.Resources["AccentSelectionBrush"])
                && ReferenceEquals(selected.BorderBrush, Application.Current.Resources["AccentHoverBrush"]) && ReferenceEquals(fixture.SelectedItem.Foreground, Application.Current.Resources["AccentTextBrush"]),
                "actual selected-item state lost the dynamic accent selection surface");
            Assert(ChromePart<Grid>(fixture.Disabled, "ButtonSurface").Opacity <= .351
                && !fixture.Disabled.IsEnabled, "actual disabled state is not visible");
            Assert(YMusic.Native.CoverPalette.Contrast(((SolidColorBrush)Application.Current.Resources["AccentBrush"]).Color, YMusic.Native.CoverPalette.Ink) >= 4.5, "active accent lost readable ink contrast");
            Assert(ChromeResourceTrigger(fixture.Link, UIElement.IsMouseOverProperty, null, System.Windows.Controls.Control.ForegroundProperty, "AccentTextBrush"),
                "text link hover does not resolve the dynamic accent hover resource");
            Assert(ChromeTrigger(fixture.Button, UIElement.IsMouseOverProperty, "ButtonHover", YMusic.Native.Motion.OpacityProperty, .065d)
                && ChromeResourceTrigger(fixture.Combo, ComboBox.IsDropDownOpenProperty, "ComboChrome", Border.BorderBrushProperty, "AccentHoverBrush"),
                "button or opened-combo state lost the bounded highlight");
            Assert(fixture.SelectedItem.FocusVisualStyle is null && fixture.NormalItem.FocusVisualStyle is null,
                "combo items inherit native dotted keyboard focus");
            File.WriteAllText(Path.Combine(_directory, "control-chrome-readback.json"), JsonSerializer.Serialize(new
            {
                normalSelectedAndDisabledRenderedFromActualTemplates = true,
                realWritableSelectedAndEnabledProperties = true,
                focusIndicatorPreviewIsNotActualKeyboardFocus = true,
                hoverAndKeyboardFocusTriggersInspected = true,
                actualHoverAndKeyboardFocusExercised = false,
                readOnlyInputDependencyPropertiesModified = false,
                nativeWindowCreated = false,
                userCursorMoved = false,
                keyboardOrMouseInputSent = false,
                accountOrNetworkAccess = false,
                accent = ChromeColor((Brush)Application.Current.Resources["AccentBrush"]), textHover = "AccentTextBrush", surfaceHover = "AccentHoverBrush", dynamicResourceKeysInspected = true
            }, new JsonSerializerOptions { WriteIndented = true }));
            return Task.CompletedTask;
        });
    }

    private sealed record ChromeControls(Border Root, Button Button, Button Quiet, Button Link, ComboBox Combo,
        ComboBoxItem NormalItem, ComboBoxItem SelectedItem, Button Disabled);

    private static ChromeControls ChromeFixture()
    {
        var normal = new Button { Content = "Обновить текст", Width = 200, Height = 40 };
        var quiet = new Button { Content = "Новая волна", Style = (Style)Application.Current.Resources["QuietButton"], Width = 200, Height = 40 };
        var link = new Button { Content = "Название альбома", Style = (Style)Application.Current.Resources["TextLink"], Width = 200, Height = 36 };
        var disabled = new Button { Content = "Недоступное действие", Width = 200, Height = 40, IsEnabled = false };
        var combo = new ComboBox { ItemsSource = new[] { "Название", "Время", "Исполнитель" }, SelectedIndex = 1, Width = 200, Height = 38 };
        var normalItem = new ComboBoxItem { Content = "Исполнитель", Width = 200 };
        var selected = new ComboBoxItem { Content = "Время · выбрано", Width = 200, IsSelected = true };
        var items = new StackPanel { Margin = new Thickness(24) };
        items.Children.Add(ChromeLabel("Ровные контуры и внутренние отступы", 20));
        items.Children.Add(new TextBlock { Text = "Реальные состояния шаблонов: обычное, выбранное, отключённое. Без имитации мыши и клавиатуры.",
            Foreground = Brushes.LightGray, Margin = new Thickness(0, 8, 0, 18), TextWrapping = TextWrapping.Wrap });
        items.Children.Add(ChromeRow("Обычная кнопка", normal));
        items.Children.Add(ChromeRow("Тихая кнопка", quiet));
        items.Children.Add(ChromeRow("Текстовая ссылка", link));
        items.Children.Add(ChromeRow("Отключённая кнопка", disabled));
        items.Children.Add(ChromeRow("Закрытый список", combo));
        items.Children.Add(ChromeRow("Пункт списка", normalItem));
        items.Children.Add(ChromeRow("Выбранный пункт", selected));
        return new(ChromeRoot(items, 760, 550), normal, quiet, link, combo, normalItem, selected, disabled);
    }

    private static Border ChromeRoot(UIElement child, double width, double height)
    {
        var root = new Border { Width = width, Height = height, Background = new SolidColorBrush(Color.FromRgb(16, 17, 20)), Child = child,
            UseLayoutRounding = true, SnapsToDevicePixels = true };
        TextElement.SetFontFamily(root, new FontFamily("Segoe UI"));
        TextElement.SetFontSize(root, 14);
        TextElement.SetForeground(root, new SolidColorBrush(Color.FromRgb(243, 241, 240)));
        TextOptions.SetTextFormattingMode(root, TextFormattingMode.Ideal);
        TextOptions.SetTextRenderingMode(root, TextRenderingMode.Grayscale);
        return root;
    }

    private static TextBlock ChromeLabel(string text, double size) => new() { Text = text, FontSize = size, FontWeight = FontWeights.SemiBold };
    private static Grid ChromeRow(string label, FrameworkElement control)
    {
        var row = new Grid { Margin = new Thickness(0, 0, 0, 12) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(240) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(new TextBlock { Text = label, Foreground = Brushes.LightGray, VerticalAlignment = VerticalAlignment.Center });
        Grid.SetColumn(control, 1); row.Children.Add(control);
        return row;
    }

    private static Grid ChromeFocusPreview(FrameworkElement background, Control ring)
    {
        var grid = new Grid { Width = background.Width, Height = background.Height, HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 0, 0, 16) };
        grid.Children.Add(background); grid.Children.Add(ring); return grid;
    }

    private static void ChromeLayout(FrameworkElement root)
    {
        root.Measure(new Size(root.Width, root.Height)); root.Arrange(new Rect(0, 0, root.Width, root.Height)); root.UpdateLayout();
    }

    private static T ChromePart<T>(Control control, string name) where T : DependencyObject =>
        control.Template.FindName(name, control) as T ?? throw new InvalidOperationException("missing actual template part " + name);

    private static string? ChromeColor(Brush? brush) => (brush as SolidColorBrush)?.Color.ToString();

    private static bool ChromeTrigger(Control control, DependencyProperty condition, string? target, DependencyProperty property, object value) =>
        control.Template.Triggers.OfType<Trigger>().Where(trigger => trigger.Property == condition && Equals(trigger.Value, true))
            .SelectMany(trigger => trigger.Setters.OfType<Setter>()).Any(setter => setter.TargetName == target && setter.Property == property
                && (value is string color ? setter.Value is Brush brush && ChromeColor(brush) == color : Equals(setter.Value, value)));

    private static bool ChromeResourceTrigger(Control control, DependencyProperty condition, string? target, DependencyProperty property, string key) =>
        control.Template.Triggers.OfType<Trigger>().Where(trigger => trigger.Property == condition && Equals(trigger.Value, true))
            .SelectMany(trigger => trigger.Setters.OfType<Setter>()).Any(setter => setter.TargetName == target && setter.Property == property
                && setter.Value is DynamicResourceExtension resource && Equals(resource.ResourceKey, key));

    private static void ChromeCapture(FrameworkElement root, string file)
    {
        var bitmap = new RenderTargetBitmap((int)root.Width, (int)root.Height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(root);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = File.Create(Path.Combine(_directory, file)); encoder.Save(output);
    }
}
