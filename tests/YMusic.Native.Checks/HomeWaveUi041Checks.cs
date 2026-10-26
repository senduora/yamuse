using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using YMusic.Native;
using ShapePath = System.Windows.Shapes.Path;

internal static partial class Program
{
    private static async Task CheckHomeWaveUi041()
    {
        await Run("HomeWaveUi041: wave actions share a visible vector and collection keeps a heart in every entry point", HomeWaveUi041Icons);
        await Run("HomeWaveUi041: all server shelves and both hero illustrations render at minimum and normal sizes in both themes", HomeWaveUi041Home);
        await Run("HomeWaveUi041: technical download center has distinct navigation and preserves queue when opening ready music", HomeWaveUi041Downloads);
    }

    private static async Task HomeWaveUi041Icons()
    {
        using var f = new SaFixture("wave-ui041-icons"); await f.Activate();
        var window = f.Window;
        SaInvoke(window, "SetSection", "favorites", "Мне нравится"); window.UpdateLayout(); await Task.Delay(350);
        var wave = (Geometry)window.FindResource("MenuWaveGeometry");
        var collection = (GeometryGroup)window.FindResource("CollectionWaveGeometry");
        var like = (Geometry)window.FindResource("MenuLikeGeometry");
        Assert(collection.Children.OfType<GeometryGroup>().Any(group => group.Children.Contains(wave))
            && collection.Children.OfType<GeometryGroup>().Any(group => group.Children.Contains(like)),
            "Collection wave lost the same wave stroke or the heart");
        ShapePath Icon(string name) => SaVisuals<ShapePath>(Control<Button>(window, name)).Single();
        Assert(ReferenceEquals(Icon("WaveNav").Data, wave) && ReferenceEquals(Icon("CollectionWaveNav").Data, collection)
            && ReferenceEquals(Icon("FavoritesWaveButton").Data, collection), "Wave entry points use unrelated glyphs");
        var menu = Control<ListView>(window, "TracksList").ContextMenu!;
        var action = menu.Items.OfType<MenuItem>().Single(item => item.Header as string == "Волна по этому треку");
        Assert(action.Icon is ShapePath menuIcon && ReferenceEquals(menuIcon.Data, wave), "Track menu differs from the common wave icon");
        foreach (var name in new[] { "WaveNav", "CollectionWaveNav", "FavoritesWaveButton" }) {
            var path = Icon(name);
            Assert(path.Stroke is SolidColorBrush && HomeWaveUi041Ink(path) > 10, "Wave action rendered no visible vector: " + name);
        }
        await HomeWaveUi041Capture(window, "favorites", "Dark", 1280, 860);
        var previousMode = ThemeManager.Mode;
        try {
            foreach (var theme in new[] { "Dark", "Light" }) {
                ThemeManager.SetMode(theme);
                foreach (var mode in new[] { WaveMode.Personal, WaveMode.Collection }) {
                    SaInvoke(window, "SelectWaveView", mode, null!); SaInvoke(window, "UpdateWavePresentation"); window.UpdateLayout();
                    var fallback = Control<ShapePath>(window, "WaveFallbackGlyph");
                    Assert(ReferenceEquals(fallback.Data, mode == WaveMode.Collection ? collection : wave)
                        && HomeWaveUi041Ink(fallback) > 100, "Wave without a cover has no matching visible fallback");
                    var background = (SolidColorBrush)HomeWaveUi041Parent<Border>(fallback).Background;
                    Assert(fallback.Stroke is SolidColorBrush stroke && Theme040Contrast(stroke.Color, background.Color) >= 3,
                        "Wave fallback lost contrast on its actual background in " + theme);
                    await HomeWaveUi041Capture(window, "empty-wave-" + mode, theme, 1280, 860);
                    File.WriteAllText(Path.Combine(_directory, $"home-wave041-fallback-state-{theme}-{mode}.json"), JsonSerializer.Serialize(new {
                        mode = mode.ToString(), theme, fallback.IsVisible, fallback.Opacity,
                        fallback.ActualWidth, fallback.ActualHeight, ink = HomeWaveUi041Ink(fallback),
                        stroke = fallback.Stroke?.ToString(), geometry = fallback.Data?.ToString(),
                        coverSource = Control<Image>(window, "WaveCover").Source?.GetType().FullName
                    }, new JsonSerializerOptions { WriteIndented = true }));
                }
            }
        } finally { ThemeManager.SetMode(previousMode); }
    }

    private static async Task HomeWaveUi041Home()
    {
        var previousMode = ThemeManager.Mode;
        using var f = new SaFixture("wave-ui041-home"); await f.Activate();
        var window = f.Window;
        using var api = new MusicApi(new FakeHandler((_, _) => Task.FromResult(Json(JsonSerializer.Serialize(new {
            result = new { blocks = Enumerable.Range(1, 7).Select(index => new {
                title = "Тестовая полка " + index,
                entities = Enumerable.Range(1, 7).Select(item => new { type = "album", data = new {
                    id = $"ui-fixture-{index}-{item}", title = "Тестовый альбом " + item
                } }).ToArray()
            }).ToArray() }
        })))));
        var sections = await api.GetHomeSectionsAsync(FakeSession, default);
        try {
            SaInvoke(window, "SetSection", "home", "Главная"); SaInvoke(window, "ShowHomeSections", new object[] { sections });
            foreach (var theme in new[] { "Dark", "Light" }) {
                ThemeManager.SetMode(theme);
                foreach (var (width, height) in new[] { (1280, 860), (1050, 680) }) {
                    window.Width = width; window.Height = height; window.UpdateLayout(); await Task.Delay(350);
                    var shelves = Control<ItemsControl>(window, "HomeShelves");
                    Assert(shelves.Items.Count == 7 && SaVisuals<Button>(shelves).Count(button => button.Tag is CatalogItem) == 42,
                        "Home dropped a server shelf or changed its six-card preview");
                    foreach (var name in new[] { "HomePersonalWaveArtwork", "HomeCollectionWaveArtwork" }) {
                        var path = Control<ShapePath>(window, name);
                        var ink = path.ActualWidth > 0 && path.ActualHeight > 0 ? HomeWaveUi041Ink(path) : 0;
                        // Uniform stretch preserves the wave's 12:9 geometry;
                        // a correctly rendered 70px-wide wave is about 54px tall.
                        var minimumHeight = 60 * path.Data.Bounds.Height / path.Data.Bounds.Width;
                        if (!path.IsVisible || path.ActualWidth < 60 || path.ActualHeight < minimumHeight || ink <= 100) {
                            File.WriteAllText(Path.Combine(_directory, "home-wave041-hero-diagnostic.json"), JsonSerializer.Serialize(new {
                                name, theme, width, path.IsVisible, path.ActualWidth, path.ActualHeight, ink, path.Opacity,
                                section = SaField<string>(window, "_section"), homeVisibility = Control<Grid>(window, "HomePanel").Visibility.ToString(),
                                homeOpacity = Control<Grid>(window, "HomePanel").Opacity, geometry = path.Data?.ToString(), stroke = path.Stroke?.ToString()
                            }, new JsonSerializerOptions { WriteIndented = true }));
                            App.Capture(window, Path.Combine(_directory, "home-wave041-hero-diagnostic.png"));
                        }
                        Assert(path.IsVisible && path.ActualWidth >= 60 && path.ActualHeight >= minimumHeight && ink > 100,
                            $"Hero illustration at {theme}/{width}: visible={path.IsVisible}, size={path.ActualWidth}x{path.ActualHeight}, ink={ink}");
                        var hero = HomeWaveUi041Parent<Button>(path);
                        var title = SaVisuals<TextBlock>(hero).First(text => text.FontWeight == FontWeights.Bold);
                        var titleBounds = title.TransformToAncestor(hero).TransformBounds(new Rect(title.RenderSize));
                        var artBounds = path.TransformToAncestor(hero).TransformBounds(new Rect(path.RenderSize));
                        Assert(titleBounds.Right <= artBounds.Left && title.DesiredSize.Height <= title.ActualHeight + 1,
                            "Hero art overlaps or clips its title at " + theme + "/" + width);
                    }
                    await HomeWaveUi041Capture(window, "home", theme, width, height);
                }
            }
        } finally { ThemeManager.SetMode(previousMode); }
    }

    private static async Task HomeWaveUi041Downloads()
    {
        var previousMode = ThemeManager.Mode;
        using var f = new SaFixture("wave-ui041-downloads"); await f.Activate();
        var window = f.Window;
        var queue = SaField<DownloadQueueState>(window, "_downloadQueue");
        queue.Replace([DcTrack("ui-progress"), DcTrack("ui-error"), DcTrack("ui-complete")], "Тестовая очередь");
        var entries = queue.BeginRun(false);
        entries[0].Begin(); entries[0].Progress(new(512, 1024));
        entries[1].SetState(DownloadEntryState.Failed, "Тестовая ошибка соединения"); entries[2].Complete(4096);
        queue.EndRun(false);
        try {
            SaClick(Control<Button>(window, "DownloadCenterNav"));
            var view = window.DownloadCenterView;
            var nav = Control<Button>(window, "DownloadCenterNav");
            Assert(SaField<string>(window, "_section") == "download-center" && Control<TextBlock>(window, "CollectionTitle").Text == "Центр загрузок"
                && AutomationProperties.GetName(nav) == "Центр загрузок", "Technical download navigation retained the library label");
            var icon = SaVisuals<ShapePath>(nav).Single();
            Assert(ReferenceEquals(icon.Data, window.FindResource("DownloadCenterGeometry"))
                && !ReferenceEquals(icon.Data, window.FindResource("MenuDownloadGeometry")), "Technical center still looks like the downloaded mark");
            Assert(SaVisuals<TextBlock>(view).Single(text => text.Name == "DownloadCenterIntro").Text.Contains("Готовая музыка", StringComparison.Ordinal)
                && SaVisuals<ListView>(view).Single().Items.Count == 3, "Technical center lacks its explanation or download states");
            foreach (var theme in new[] { "Dark", "Light" }) {
                ThemeManager.SetMode(theme);
                foreach (var (width, height) in new[] { (1280, 860), (1050, 680) }) {
                    window.Width = width; window.Height = height; window.UpdateLayout();
                    await HomeWaveUi041Capture(window, "download-center", theme, width, height);
                }
            }
            var before = queue.Entries.Select(entry => (entry.Track.Id, entry.State, entry.Bytes)).ToArray();
            SaClick(SaVisuals<Button>(view).Single(button => button.Name == "DownloadLibraryButton"));
            Assert(SaField<string>(window, "_section") == "offline"
                && queue.Entries.Select(entry => (entry.Track.Id, entry.State, entry.Bytes)).SequenceEqual(before),
                "Ready-music shortcut cleared, restarted or reordered download tasks");
        } finally { ThemeManager.SetMode(previousMode); }
    }

    private static T HomeWaveUi041Parent<T>(DependencyObject child) where T : DependencyObject
    {
        for (var parent = VisualTreeHelper.GetParent(child); parent is not null; parent = VisualTreeHelper.GetParent(parent))
            if (parent is T match) return match;
        throw new InvalidOperationException("Expected visual ancestor is missing");
    }
    private static int HomeWaveUi041Ink(FrameworkElement element)
    {
        var width = (int)Math.Ceiling(element.ActualWidth); var height = (int)Math.Ceiling(element.ActualHeight);
        Assert(width > 0 && height > 0, "Vector has no arranged surface");
        // A child Visual retains its arranged offset. VisualBrush maps its own
        // bounds into the sample, rather than cropping a far-right hero to zero.
        var sample = new DrawingVisual();
        using (var drawing = sample.RenderOpen()) drawing.DrawRectangle(new VisualBrush(element), null, new Rect(0, 0, width, height));
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(sample);
        var pixels = new byte[width * height * 4]; bitmap.CopyPixels(pixels, width * 4, 0);
        var ink = 0; for (var offset = 3; offset < pixels.Length; offset += 4) if (pixels[offset] > 0) ink++;
        return ink;
    }
    private static async Task HomeWaveUi041Capture(MainWindow window, string name, string theme, int width, int height)
    {
        window.Width = width; window.Height = height; window.UpdateLayout(); await Task.Delay(350);
        // Capture the product client tree explicitly. Rendering the offscreen
        // HWND root can retain an empty/stale child drawing after a theme swap.
        var client = (FrameworkElement)window.Content;
        var size = new Size(Math.Ceiling(client.ActualWidth), Math.Ceiling(client.ActualHeight));
        var sample = new DrawingVisual();
        using (var drawing = sample.RenderOpen()) {
            drawing.DrawRectangle(window.Background, null, new Rect(size));
            drawing.DrawRectangle(new VisualBrush(client), null, new Rect(size));
        }
        var bitmap = new RenderTargetBitmap((int)size.Width, (int)size.Height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(sample);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(Path.Combine(_directory, $"home-wave041-{name}-{theme}-{width}x{height}.png")); encoder.Save(file);
    }
}
