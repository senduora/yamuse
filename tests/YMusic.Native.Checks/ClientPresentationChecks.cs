using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using YMusic.Native;

internal static partial class Program
{
    private static async Task CheckClientPresentation()
    {
        await Run("Presentation: Home cards stay inside their shelves through repeated hover and scrolling", PresentationCards);
        await Run("Presentation: inline live Settings retain changes navigate and scroll while the same player continues", PresentationSettings);
        await Run("Presentation: selection avoids playlist disk reads while explicit refresh observes changed data", PresentationSelection);
    }

    private static async Task PresentationCards()
    {
        using var fixture = new SaFixture("presentation-cards");
        var window = fixture.Window;
        await fixture.Activate();
        SaInvoke(window, "SetSection", "home", "Главная");
        var items = Enumerable.Range(0, 6).Select(i => new CatalogItem("presentation-" + i, "album",
            i == 0 ? "Премьера" : "Подборка для уютного вечера " + i,
            "Только новинки, любимые исполнители и новые открытия для вас")).ToArray();
        SaInvoke(window, "ShowHomeSections", new object[] { new[] { new HomeSection("Собираем для вас", items), new HomeSection("Новые релизы", items) } });
        var shelves = Control<ItemsControl>(window, "HomeShelves");
        var scroll = Control<ScrollViewer>(window, "HomeScroll");
        var hoverKey = (DependencyPropertyKey)typeof(UIElement).GetField("IsMouseOverPropertyKey", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        var facts = new List<object>();
        foreach (var (width, height) in new[] { (1050, 680), (1280, 860) })
        {
            await ClResize(window, width, height);
            Assert(TextOptions.GetTextRenderingMode(window) == TextRenderingMode.Grayscale && TextOptions.GetTextFormattingMode(window) == TextFormattingMode.Ideal,
                "main typography does not use the selected antialiased rendering");
            var cards = SaVisuals<Button>(shelves).Where(b => b.Tag is CatalogItem).ToArray();
            Assert(cards.Length == 12, "a shelf lost its cards");
            foreach (var card in cards)
            {
                var wrap = PresentationParent<WrapPanel>(card);
                var bounds = ClRect(card, wrap);
                Assert(bounds.Left >= 0 && bounds.Right <= wrap.ActualWidth + 1 && bounds.Top >= 0 && bounds.Bottom <= wrap.ActualHeight + 1,
                    "card background extends outside its shelf: " + bounds);
            }
            var first = cards[0];
            var parent = PresentationParent<WrapPanel>(first);
            var before = ClRect(first, parent);
            var cover = SaVisuals<Image>(first).Single();
            var coverBounds = ClRect(cover, first);
            Assert(coverBounds.Left >= 8 && coverBounds.Right <= first.ActualWidth - 8, "cover lost its safe edge padding");
            scroll.ScrollToVerticalOffset(ClRect(parent, (Visual)scroll.Content).Top - 45);
            await Task.Delay(160);
            var layer = (Border)first.Template.FindName("CardHover", first);
            for (var n = 0; n < 3; n++)
            {
                first.SetValue(hoverKey, true);
                await Task.Delay(200);
                Assert(layer.Opacity > .99 && ClRect(first, parent) == before && first.InputHitTest(new Point(4, first.ActualHeight / 2)) is not null,
                    "hover changed geometry, lost its edge hit area or failed to settle");
                first.SetValue(hoverKey, false);
                await Task.Delay(200);
                Assert(layer.Opacity < .01 && ClRect(first, parent) == before, "hover exit did not settle at the same geometry");
            }
            first.SetValue(hoverKey, true); await Task.Delay(200);
            App.Capture(window, Path.Combine(_directory, $"presentation-home-hover-{width}.png"));
            first.SetValue(hoverKey, false);
            scroll.ScrollToEnd(); await Task.Delay(160);
            Assert(scroll.HorizontalOffset == 0 && scroll.ScrollableWidth == 0, "Home introduced horizontal overflow");
            facts.Add(new { width, height, card = before, cover = coverBounds, dpi = VisualTreeHelper.GetDpi(window).PixelsPerDip, hoverCycles = 3 });
        }
        var homeOffset = scroll.VerticalOffset;
        SaClick(Control<Button>(window, "SettingsButton")); window.UpdateLayout();
        Control<ScrollViewer>(window, "SettingsScroll").ScrollToVerticalOffset(100);
        await Call(window, "OpenArtistAsync", "artist-42", "Исполнитель 42", true);
        Assert(Control<Grid>(window, "ArtistPanel").IsVisible && !Control<Grid>(window, "SettingsPanel").IsVisible,
            "a music link left Settings over the artist page");
        SaClick(Control<Button>(window, "ArtistBackButton")); await Task.Delay(150); window.UpdateLayout();
        Assert(Control<Grid>(window, "HomePanel").IsVisible && Math.Abs(scroll.VerticalOffset - homeOffset) <= 1,
            "Back restored the Settings offset instead of the underlying Home shelf");
        File.WriteAllText(Path.Combine(_directory, "presentation-cards.json"), JsonSerializer.Serialize(facts, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static async Task PresentationSettings()
    {
        var media = File.ReadAllBytes(WriteWave("presentation-settings.wav", 40));
        using var fixture = new PreloadIntegrationFixture("presentation-settings", signedIn: false, media: media);
        var window = fixture.Window;
        var store = new PreferencesStore(Path.Combine(fixture.DirectoryPath, "settings"));
        typeof(MainWindow).GetField("_preferenceStore", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(window, store);
        store.Save(new PlayerPreferences { Volume = 0 });
        var initialPreferences = File.ReadAllBytes(store.FilePath);
        var rows = Enumerable.Range(0, 90).Select(i => new TrackRow { Id = "presentation-row-" + i, RequestId = "presentation-row-" + i,
            Title = "Трек " + i, Available = true, DurationMs = 40000 }).ToArray();
        SaInvoke(window, "ShowRows", (object)rows);
        Control<TextBox>(window, "FilterBox").Text = "Трек"; await Task.Delay(300);
        var list = Control<ListView>(window, "TracksList"); list.SelectedIndex = 12;
        var listScroll = SaVisuals<ScrollViewer>(list).First();
        listScroll.ScrollToVerticalOffset(300); await Task.Delay(80);
        var offset = listScroll.VerticalOffset;
        var first = fixture.Local("playing"); var next = fixture.Local("prepared");
        await fixture.Play(first, next); await PreloadAwaitPrepared(window);
        var version = fixture.Queue.Version; var position = fixture.Player.Position;
        SaClick(Control<Button>(window, "SettingsButton")); window.UpdateLayout();
        Control<Slider>(window, "CrossfadeSetting").Value = 8.5;
        Control<ComboBox>(window, "SettingsSpeed").SelectedItem = 1.25;
        var space = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window), Environment.TickCount, Key.Space)
        { RoutedEvent = Keyboard.PreviewKeyDownEvent, Source = Control<CheckBox>(window, "SettingsTray") };
        SaInvoke(window, "Window_KeyDown", window, space);
        Assert(!space.Handled && fixture.Player.IsPlaying, "Settings stole a checkbox key for global playback");
        await Task.Delay(200);
        Assert(fixture.Player.IsPlaying && fixture.Player.Position > position && fixture.Queue.Version == version && window.OwnedWindows.Count == 0,
            "opening Settings interrupted playback or created another window");
        SaClick(Control<Button>(window, "SettingsBackButton")); window.UpdateLayout(); await Task.Delay(80);
        Assert(fixture.Player.CrossfadeSeconds == 8.5 && fixture.Player.SpeedRatio == 1.25 && store.Load().CrossfadeSeconds == 8.5 && store.Load().PlaybackSpeed == 1.25, "Back lost live settings or persistence");
        Assert(Control<TextBox>(window, "FilterBox").Text == "Трек" && list.SelectedIndex == 12 && Math.Abs(listScroll.VerticalOffset - offset) <= 1,
            "returning from Settings lost the list position or filter");

        SaClick(Control<Button>(window, "SettingsButton"));
        Assert(Control<Slider>(window, "CrossfadeSetting").Value == 8.5, "reopen lost live settings");
        Control<Slider>(window, "CrossfadeSetting").Value = 8.5;
        Control<ComboBox>(window, "SettingsSpeed").SelectedItem = 1.25;
        Control<ComboBox>(window, "SettingsDownloads").SelectedItem = 7;
        Control<ComboBox>(window, "SettingsQuality").SelectedIndex = 1;
        foreach (var (width, height) in new[] { (1050, 680), (1280, 860) })
        {
            await ClResize(window, width, height);
            var panel = Control<Grid>(window, "SettingsPanel"); var scroll = Control<ScrollViewer>(window, "SettingsScroll");
            var back = Control<Button>(window, "SettingsBackButton");
            ClWithin(back, panel, "Settings Back");
            Assert(panel.RowDefinitions.Count == 2 && window.FindName("SettingsApplyButton") is null && window.FindName("SettingsCancelButton") is null
                && scroll.ViewportHeight > 100 && scroll.ScrollableHeight > 0 && !ClIntersects(ClRect(back, window), ClRect(Control<Border>(window, "PlayerBar"), window)), "Settings retains the save footer, cannot scroll or covers the player");
            scroll.ScrollToTop(); await Task.Delay(100);
            App.Capture(window, Path.Combine(_directory, $"presentation-settings-top-{width}.png"));
            Control<Button>(window, "SettingsClearOffline").BringIntoView(); await Task.Delay(100);
            ClWithin(Control<Button>(window, "SettingsClearOffline"), scroll, "Settings offline removal");
            scroll.ScrollToEnd(); await Task.Delay(100);
            App.Capture(window, Path.Combine(_directory, $"presentation-settings-bottom-{width}.png"));
        }
        SaClick(Control<Button>(window, "SettingsBackButton"));
        var saved = store.Load();
        Assert(saved.CrossfadeSeconds == 8.5 && saved.PlaybackSpeed == 1.25 && saved.ConcurrentDownloads == 7 && saved.Quality == AudioQuality.Optimal,
            "inline settings did not persist their supported choices");
        SaClick(Control<Button>(window, "SettingsButton"));
        Assert(Control<Slider>(window, "CrossfadeSetting").Value == 8.5 && (int)Control<ComboBox>(window, "SettingsDownloads").SelectedItem == 7, "saved settings did not reopen");
        var escape = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window), Environment.TickCount, Key.Escape) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
        SaInvoke(window, "Window_KeyDown", window, escape);
        Assert(escape.Handled && !Control<Grid>(window, "SettingsPanel").IsVisible, "Escape did not return to music");
        using (var holdLyrics = new CancellationTokenSource())
        {
            typeof(MainWindow).GetField("_lyricsCts", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(window, holdLyrics);
            SaClick(Control<Button>(window, "SettingsButton"));
            SaClick(Control<Button>(window, "PlayerLyricsButton")); window.UpdateLayout();
            Assert(Control<Grid>(window, "LyricsPanel").IsVisible && !Control<Grid>(window, "SettingsPanel").IsVisible, "lyrics did not replace Settings");
            SaClick(Control<Button>(window, "SettingsButton")); window.UpdateLayout();
            Assert(Control<Grid>(window, "SettingsPanel").IsVisible && !Control<Grid>(window, "LyricsPanel").IsVisible, "Settings did not replace lyrics");
            typeof(MainWindow).GetField("_lyricsCts", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(window, null);
        }
        SaClick(Control<Button>(window, "FavoritesNav")); window.UpdateLayout();
        Assert(!Control<Grid>(window, "SettingsPanel").IsVisible && Control<Grid>(window, "ContentGrid").IsVisible && fixture.Queue.Version == version,
            "sidebar navigation left Settings over the selected page or changed playback");
        Assert(fixture.ResolveCalls == 0 && fixture.MediaCalls == 0, "Settings caused media network requests");
    }

    private static async Task PresentationSelection()
    {
        using var fixture = new PreloadIntegrationFixture("presentation-selection", signedIn: false);
        var rows = Enumerable.Range(0, 3920).Select(i => new TrackRow { Id = "selection-" + i, RequestId = "selection-" + i,
            Title = "Трек " + i, Available = true, DurationMs = 180000 }).ToArray();
        SaField<List<TrackRow>>(fixture.Window, "_favoritesCache").AddRange(rows);
        await fixture.Library.RememberPlaylistAsync(new PlaylistDetails("local:favorites", "liked", "Мне нравится", "", 0, rows), AudioQuality.Excellent, default);
        SaInvoke(fixture.Window, "ShowRows", (object)rows);
        await (SaField<Task?>(fixture.Window, "_playlistStatusTask") ?? Task.CompletedTask).WaitAsync(TimeSpan.FromSeconds(5));
        var status = Control<TextBlock>(fixture.Window, "PlaylistDownloadText"); var before = status.Text;
        var index = Path.Combine(fixture.Library.DirectoryPath, "playlists.index.json");
        File.WriteAllText(index, "deliberately changed metadata for the explicit-refresh check");
        var clock = Stopwatch.StartNew();
        for (var n = 0; n < 40; n++)
        {
            Control<ListView>(fixture.Window, "TracksList").SelectedIndex = n;
            SaInvoke(fixture.Window, "UpdateControls");
        }
        clock.Stop();
        Assert(status.Text == before, "ordinary selection re-read playlist metadata");
        SaInvoke(fixture.Window, "UpdateCount");
        await (SaField<Task?>(fixture.Window, "_playlistStatusTask") ?? Task.CompletedTask).WaitAsync(TimeSpan.FromSeconds(5));
        Assert(status.Text == "Не удалось прочитать статус скачивания", "an explicit library refresh failed to observe changed metadata");
        File.WriteAllText(Path.Combine(_directory, "presentation-selection.json"), JsonSerializer.Serialize(new
        { tracks = rows.Length, selections = 40, selectionMs = clock.Elapsed.TotalMilliseconds, selectionReadsPlaylistIndex = false, explicitRefreshReadsChangedIndex = true }));
    }

    private static T PresentationParent<T>(DependencyObject child) where T : DependencyObject
    {
        for (var parent = VisualTreeHelper.GetParent(child); parent is not null; parent = VisualTreeHelper.GetParent(parent))
            if (parent is T result) return result;
        throw new InvalidOperationException("Missing parent " + typeof(T).Name);
    }
}
