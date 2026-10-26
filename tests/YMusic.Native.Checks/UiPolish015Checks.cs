using System.Globalization;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using YMusic.Native;
using Path = System.IO.Path;

internal static partial class Program
{
    private static async Task CheckUiPolish015()
    {
        await Run("UI polish015: production list player catalog and expanded text links have bounded hits and retain trimming", Ui015Links);
        await Run("UI polish015: real WPF focus and local pointer/key routes distinguish keyboard from mouse without sticky chrome", Ui015Focus);
        await Run("UI polish015: repeat off all one uses separated vectors and exact accessible state without changing queue identity", Ui015Repeat);
        await Run("UI polish015: hero Back has symmetric content insets and restores its exact original placement", Ui015HeroBack);
        await Run("UI polish015: playlist author and empty subtitle preserve card title and cover alignment", Ui015Cards);
    }

    private static TrackRow[] Ui015Rows() =>
    [
        new() { Id = "ui-015-short", RequestId = "ui-015-short:1", Title = "Короткий трек", Album = "Альбом", AlbumId = "1", Artist = "Первый, Второй",
            Artists = [new("artist-42", "Первый"), new("artist-99", "Второй")], Available = true, DurationMs = 180000 },
        new() { Id = "ui-015-long", RequestId = "ui-015-long:1", Title = string.Concat(Enumerable.Repeat("Длинное название ", 15)), Album = string.Concat(Enumerable.Repeat("Длинный альбом ", 10)), AlbumId = "1",
            Artist = "А, Очень длинное имя второго исполнителя, которое обрезается по колонке", Artists = [new("artist-42", "А"), new("artist-99", "Очень длинное имя второго исполнителя, которое обрезается по колонке")], Available = true }
    ];

    private static async Task Ui015Links()
    {
        using var f = new SaFixture("ui015-links"); await f.Activate(); var w = f.Window; var rows = Ui015Rows();
        SaInvoke(w, "ShowPlaylist", new PlaylistDetails("owner", "fixture", "Ссылки по границам текста", "", 1, rows, OwnerName: "Автор плейлиста"));
        typeof(MainWindow).GetField("_current", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(w, rows[0]);
        Control<TextBlock>(w, "TrackTitle").Text = rows[0].Title;
        var queue = SaField<PlaybackQueue>(w, "_playbackQueue"); queue.SetItems(rows, rows[0]); SaInvoke(w, "UpdateControls");
        var token = queue.CurrentOccurrenceToken; var version = queue.Version;
        foreach (var (width, height) in new[] { (1050, 680), (1280, 860) })
        {
            w.Width = width; w.Height = height; w.UpdateLayout(); await Task.Delay(140);
            var list = Control<ListView>(w, "TracksList"); list.ScrollIntoView(rows[0]); w.UpdateLayout();
            var first = (ListViewItem)list.ItemContainerGenerator.ContainerFromItem(rows[0])!;
            var links = SaVisuals<Button>(first).Where(button => button.IsVisible && button.Content is TextBlock && (button.Tag is ArtistReference || ReferenceEquals(button.Tag, rows[0]))).ToArray();
            Assert(links.Length == 4, "list lost title/album/two exact artist links");
            foreach (var link in links) Ui015BoundedHit(w, link, true);
            Ui015BoundedHit(w, Control<Button>(w, "PlayerTitleButton"), true);
            var artistPanel = Control<DockPanel>(w, "PlayerArtistPanel");
            Assert(SaVisuals<TextBlock>(artistPanel).Count(text => text.Text == ", " && text.IsVisible) == 1, "artist separator changed");
            Assert(artistPanel.ContextMenu!.Items.Cast<MenuItem>().Select(item => item.Header.ToString()).SequenceEqual(new[] { "Первый", "Второй" }), "right-click artist menu is incomplete");
            list.ScrollIntoView(rows[1]); w.UpdateLayout();
            var second = (ListViewItem)list.ItemContainerGenerator.ContainerFromItem(rows[1])!;
            var longLinks = SaVisuals<Button>(second).Where(button => button.IsVisible && button.Content is TextBlock text && text.Text.Length > 30).ToArray();
            Assert(longLinks.Length >= 3, "long title/album/artist fixtures were not realized");
            foreach (var link in longLinks)
            {
                var text = (TextBlock)link.Content;
                Assert(text.TextTrimming == TextTrimming.CharacterEllipsis && text.ActualWidth > 0 && text.ActualWidth < Ui015NaturalWidth(text) - 2,
                    "long link is clipped without a finite ellipsis measure: " + text.Text);
                Ui015BoundedHit(w, link, false);
            }
            App.Capture(w, Path.Combine(_directory, $"ui015-links-list-{width}x{height}.png"));
            SaInvoke(w, "SetExpandedPlayerVisible", true, false); w.UpdateLayout(); await Task.Delay(150);
            var host = Control<ContentControl>(w, "ExpandedPlayerHost");
            var title = SaVisuals<Button>(host).Single(button => button.Name == "ExpandedPlayerTitle");
            Ui015BoundedHit(w, title, true);
            var expandedArtists = SaVisuals<ContentControl>(host).Single(control => control.Name == "ExpandedPlayerArtists");
            Assert(SaVisuals<Button>(expandedArtists).Count(button => button.Tag is ArtistReference) == 2
                && expandedArtists.ContextMenu!.Items.Count == 2, "expanded player lost individual artists or all-artists menu");
            App.Capture(w, Path.Combine(_directory, $"ui015-links-expanded-{width}x{height}.png"));
            SaInvoke(w, "SetExpandedPlayerVisible", false, false);
        }
        SaInvoke(w, "SetExpandedPlayerVisible", true, false); w.UpdateLayout();
        var expanded = Control<ContentControl>(w, "ExpandedPlayerHost");
        SaClick(SaVisuals<Button>(expanded).First(button => button.Tag is ArtistReference));
        await Until(() => SaField<string>(w, "_section") == "artist-detail" && SaField<CancellationTokenSource?>(w, "_featureCts") is null);
        Assert(expanded.Visibility == Visibility.Collapsed && f.Handler.Seen.Any(call => call.Uri.AbsolutePath == "/artists/artist-42/brief-info"), "expanded artist click failed to navigate by exact identity");
        SaClick(Control<Button>(w, "SearchNav")); Control<TextBox>(w, "CatalogQuery").Text = "fixture";
        await Call(w, "SearchCatalogViewAsync", false); w.UpdateLayout();
        var catalog = Control<ListBox>(w, "CatalogList"); var track = catalog.Items.Cast<CatalogItem>().First(item => item.Kind == "track");
        catalog.ScrollIntoView(track); w.UpdateLayout();
        var result = (ListBoxItem)catalog.ItemContainerGenerator.ContainerFromItem(track)!;
        Ui015BoundedHit(w, SaVisuals<Button>(result).Single(button => button.Content is TextBlock text && text.Text == track.Title), true);
        Assert(queue.Version == version && queue.CurrentOccurrenceToken == token && !f.Player.HasSource, "render/link measurement changed playback");
    }

    private static Task Ui015Focus()
    {
        var first = new Button { Content = "Действие", Margin = new Thickness(0, 0, 0, 10) };
        var link = new Button { Content = new TextBlock { Text = "Короткая ссылка", TextTrimming = TextTrimming.CharacterEllipsis }, Style = (Style)Application.Current.Resources["TextLink"] };
        var controls = new StackPanel { Margin = new Thickness(24) }; controls.Children.Add(first); controls.Children.Add(link);
        var host = new Window { Content = controls, Width = 480, Height = 240, Left = -16000, Top = -16000, ShowActivated = false, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual };
        try
        {
            host.Show(); host.UpdateLayout(); Ui015Key(first, Key.Tab); first.Focus();
            Assert(first.IsKeyboardFocused && InputFeedback.GetKeyboardFocusVisible(first) && ChromePart<Border>(first, "ButtonKeyboardFocus").Opacity == 1, "real WPF keyboard focus lacks button ring");
            Ui015Key(first, Key.Tab); first.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next)); host.UpdateLayout();
            Assert(link.IsKeyboardFocused && InputFeedback.GetKeyboardFocusVisible(link) && ChromePart<Border>(link, "LinkKeyboardFocus").Opacity == 1,
                "Tab traversal did not reveal link focus");
            var clicks = 0; link.Click += (_, _) => clicks++;
            link.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = Mouse.PreviewMouseDownEvent });
            link.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); host.UpdateLayout();
            Assert(link.IsKeyboardFocused && !InputFeedback.GetKeyboardFocusVisible(link) && ChromePart<Border>(link, "LinkKeyboardFocus").Opacity == 0
                && ChromeColor(link.Foreground) == ChromeColor((Brush)Application.Current.Resources["MutedBrush"]) && clicks == 1, "mouse click retained accent/focus or lost the logical clickable owner");
            link.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount) { RoutedEvent = Mouse.MouseLeaveEvent });
            Assert(!InputFeedback.GetKeyboardFocusVisible(link) && ChromeColor(link.Foreground) == ChromeColor((Brush)Application.Current.Resources["MutedBrush"]), "leaving mouse link retained highlight");
            Ui015Key(link, Key.Enter); host.UpdateLayout();
            Assert(InputFeedback.GetKeyboardFocusVisible(link) && ChromePart<Border>(link, "LinkKeyboardFocus").Opacity == 1, "keyboard use after mouse did not restore focus");
            first.Focus(); Assert(!InputFeedback.GetKeyboardFocusVisible(link), "lost focus retained link focus ring");
            first.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = Mouse.PreviewMouseDownEvent });
            Assert(!InputFeedback.GetKeyboardFocusVisible(first) && ChromePart<Border>(first, "ButtonKeyboardFocus").Opacity == 0, "ordinary button retains orange mouse outline");
            link.IsEnabled = false; Assert(!link.Focus() && ChromePart<Grid>(link, "LinkSurface").Opacity == .4, "disabled link accepts focus or loses its disabled state");
            File.WriteAllText(Path.Combine(_directory, "ui015-focus-readback.json"), JsonSerializer.Serialize(new { actualWpfKeyboardFocus = true, actualMoveFocusTabTraversal = true,
                localRoutedPointerAndKeyEvents = true, readOnlyInputPropertiesModified = false, nativeGlobalInputSent = false, userCursorMoved = false, activatedWindow = host.IsActive, privateFixtureOnly = true, clickCount = clicks }));
        }
        finally { host.Close(); }
        return Task.CompletedTask;
    }

    private static async Task Ui015Repeat()
    {
        using var f = new SaFixture("ui015-repeat"); var w = f.Window; var rows = Ui015Rows();
        var queue = SaField<PlaybackQueue>(w, "_playbackQueue"); queue.SetItems(rows, rows[0]); var token = queue.CurrentOccurrenceToken; var items = queue.Items;
        var repeat = Control<Button>(w, "RepeatButton"); var arrows = Control<System.Windows.Shapes.Path>(w, "RepeatArrows"); var one = Control<System.Windows.Shapes.Path>(w, "RepeatOneLabel");
        var arrowStroke = arrows.Data.GetWidenedPathGeometry(new Pen(Brushes.White, arrows.StrokeThickness + .5));
        var oneStroke = one.Data.GetWidenedPathGeometry(new Pen(Brushes.White, one.StrokeThickness + .5));
        Assert(Geometry.Combine(arrowStroke, oneStroke, GeometryCombineMode.Intersect, null).GetArea() < .001, "repeat-one vector intersects arrow strokes");
        foreach (var (state, label, visible) in new[] { (RepeatMode.Off, "Повтор выключен", false), (RepeatMode.All, "Повтор всей очереди", false), (RepeatMode.One, "Повтор трека", true), (RepeatMode.Off, "Повтор выключен", false) })
        {
            SaInvoke(w, "UpdateControls"); w.UpdateLayout(); await Task.Delay(220);
            Assert(queue.Repeat == state && Equals(repeat.ToolTip, label) && AutomationProperties.GetName(repeat) == label && (one.Visibility == Visibility.Visible) == visible,
                "repeat state and accessibility diverged");
            Assert(!SaVisuals<TextBlock>(repeat).Any() && ReferenceEquals(queue.Items, items) && queue.CurrentOccurrenceToken == token && !f.Player.HasSource,
                "repeat presentation changed queue identity or added text labels");
            if (state != RepeatMode.Off || !File.Exists(Path.Combine(_directory, "ui015-repeat-off.png")))
                Ui015CaptureElement(repeat, "ui015-repeat-" + state.ToString().ToLowerInvariant() + ".png");
            if (state == RepeatMode.Off && File.Exists(Path.Combine(_directory, "ui015-repeat-one.png"))) break;
            SaClick(repeat);
        }
    }

    private static async Task Ui015HeroBack()
    {
        using var f = new SaFixture("ui015-back"); await f.Activate(); var w = f.Window;
        var back = Control<Button>(w, "BrowseBackButton"); var originalParent = back.Parent; var originalPadding = back.Padding; var originalMargin = back.Margin;
        await Call(w, "OpenCatalogAsync", new CatalogItem("1", "album", "Исходный альбом", ""), true);
        SaInvoke(w, "ShowPlaylist", new PlaylistDetails("owner", "fixture", "Плейлист с ровной кнопкой", "", 1, Ui015Rows(), OwnerName: "Автор fixture"));
        foreach (var (width, height) in new[] { (1050, 680), (1280, 860) })
        {
            w.Width = width; w.Height = height; w.UpdateLayout(); await Task.Delay(100);
            Assert(back.Padding == new Thickness(12, 6, 12, 6) && back.ActualHeight >= 32 && back.ActualWidth > 75, "hero Back still presses its label against chrome");
            var content = ChromePart<ContentPresenter>(back, "ButtonContent"); var position = content.TranslatePoint(new Point(), back);
            Assert(position.X >= 12 && position.Y >= 6 && position.X + content.ActualWidth <= back.ActualWidth - 12 + .1
                && position.Y + content.ActualHeight <= back.ActualHeight - 6 + .1, "hero Back content escapes symmetric insets");
            App.Capture(w, Path.Combine(_directory, $"ui015-back-{width}x{height}.png"));
        }
        SaInvoke(w, "HideCollectionHero");
        Assert(ReferenceEquals(back.Parent, originalParent) && back.Padding == originalPadding && back.Margin == originalMargin, "closing hero failed to restore original Back layout");
    }

    private static Task Ui015Cards()
    {
        using var f = new SaFixture("ui015-cards"); var w = f.Window;
        var template = (DataTemplate)w.FindResource("MusicCard");
        var withAuthor = new ContentControl { Content = new CatalogItem("playlist-a", "playlist", "Плейлист", "", OwnerName: "Автор fixture"), ContentTemplate = template };
        var empty = new ContentControl { Content = new CatalogItem("playlist-b", "playlist", "Плейлист", ""), ContentTemplate = template };
        var root = ChromeRoot(new StackPanel { Orientation = Orientation.Horizontal, Children = { withAuthor, empty } }, 500, 300); ChromeLayout(root);
        var titleA = SaVisuals<TextBlock>(withAuthor).Single(text => text.Text == "Плейлист"); var titleB = SaVisuals<TextBlock>(empty).Single(text => text.Text == "Плейлист");
        Assert(Math.Abs(titleA.TranslatePoint(new Point(), root).Y - titleB.TranslatePoint(new Point(), root).Y) < .1 && withAuthor.DesiredSize.Height == empty.DesiredSize.Height,
            "missing subtitle vertically shifts title or card");
        Assert(SaVisuals<TextBlock>(withAuthor).Any(text => text.Text == "Автор fixture"), "playlist card did not bind owner DisplaySubtitle");
        ChromeCapture(root, "ui015-playlist-author-empty.png");
        return Task.CompletedTask;
    }

    private static void Ui015BoundedHit(Window root, Button link, bool shortText)
    {
        Assert(link.Content is TextBlock { IsHitTestVisible: true } && link.Padding.Left == 0 && link.Padding.Right == 0 && link.ActualWidth > 0
            && link.Template.FindName("LinkUnderline", link) is null, "link lacks a real text/button owner or retains underline/padding");
        var inside = root.InputHitTest(link.TranslatePoint(new Point(link.ActualWidth / 2, link.ActualHeight / 2), root)) as DependencyObject;
        Assert(Ui015HasAncestor(inside, link), "actual text does not hit its clickable button");
        if (shortText)
        {
            Assert(link.ActualWidth <= Ui015NaturalWidth((TextBlock)link.Content) + 2, "short link stretches beyond the measured text");
            var outside = root.InputHitTest(link.TranslatePoint(new Point(link.ActualWidth + 10, link.ActualHeight / 2), root)) as DependencyObject;
            Assert(!Ui015HasAncestor(outside, link), "blank area beyond text remains clickable/highlightable");
        }
    }
    private static bool Ui015HasAncestor(DependencyObject? hit, DependencyObject expected)
    {
        for (; hit is not null; hit = hit is Visual ? VisualTreeHelper.GetParent(hit) : (hit as FrameworkContentElement)?.Parent)
            if (ReferenceEquals(hit, expected)) return true;
        return false;
    }
    private static double Ui015NaturalWidth(TextBlock text) => new FormattedText(text.Text, CultureInfo.GetCultureInfo("ru-RU"), text.FlowDirection,
        new Typeface(text.FontFamily, text.FontStyle, text.FontWeight, text.FontStretch), text.FontSize, text.Foreground, VisualTreeHelper.GetDpi(text).PixelsPerDip).WidthIncludingTrailingWhitespace;
    private static void Ui015Key(UIElement target, Key key) => target.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(target), Environment.TickCount, key) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
    private static void Ui015CaptureElement(FrameworkElement element, string file)
    {
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth * 4), (int)Math.Ceiling(element.ActualHeight * 4), 384, 384, PixelFormats.Pbgra32);
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            var bounds = new Rect(0, 0, element.ActualWidth, element.ActualHeight);
            drawing.DrawRectangle(new SolidColorBrush(Color.FromRgb(23, 25, 29)), null, bounds);
            drawing.DrawRectangle(new VisualBrush(element) { Stretch = Stretch.Fill }, null, bounds);
        }
        bitmap.Render(visual); var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder(); encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        using var output = File.Create(Path.Combine(_directory, file)); encoder.Save(output);
    }
}
