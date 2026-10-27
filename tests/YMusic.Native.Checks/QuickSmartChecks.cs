using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using YMusic.Native;

internal static partial class Program
{
    private static async Task CheckQuickSmart()
    {
        await Run("QuickSmart: rules intersect exact artist favorite and downloaded identities with a per-track duration limit", QuickSmartRules);
        await Run("QuickSmart: account-bound atomic rule store retains concurrent edits and blocks malformed or oversized input", QuickSmartStore);
        await Run("QuickSmart: local index searches all four entity types without conflating names or composite identities", QuickSmartIndex);
        await Run("QuickSmart: Ctrl K dialog respects modifiers Escape themes and account boundaries without native windows", QuickSmartDialog);
        await Run("QuickSmart: local rule create rename edit delete recomposes collection and restores the right source through Back", QuickSmartCrud);
        await Run("QuickSmart: next and enqueue preserve collection order and reject stale account actions", QuickSmartQueue);
    }

    private static TrackRow QuickSmartRow(string id, string album = "album-a", string artist = "artist-a", long duration = 180000,
        bool downloaded = true) => new()
    {
        Id = id, RequestId = id + ":" + album, Title = "Трек " + id, Album = "Одинаковое название альбома", AlbumId = album,
        Artist = "Одинаковое имя", Artists = [new ArtistReference(artist, "Одинаковое имя")],
        DurationMs = duration, Available = true, DownloadedPath = downloaded ? "fixture-local-audio" : null
    };

    private static Task QuickSmartRules()
    {
        var a = QuickSmartRow("a"); var boundary = QuickSmartRow("boundary", duration: 240000);
        var alternate = QuickSmartRow("a", "album-b");
        TrackRow[] rows = [a, a, boundary, QuickSmartRow("long", duration: 240001),
            QuickSmartRow("other-artist", artist: "artist-b"), QuickSmartRow("unknown", duration: 0),
            QuickSmartRow("not-liked"), QuickSmartRow("not-downloaded", downloaded: false), alternate];
        var favorites = rows.Where(row => row.Id != "not-liked").Select(row => row.Id).ToHashSet(StringComparer.Ordinal);
        var rule = new SmartPlaylistRule(Guid.NewGuid(), "Скачанные любимые до четырёх минут", true, true, ["artist-a"], 4);
        var selected = SmartPlaylistEvaluator.Evaluate(rule, rows, favorites);
        Assert(selected.SequenceEqual(new[] { a, boundary, alternate }), "rule intersections, stable order or exact RequestId deduplication failed");
        Assert(selected.Sum(row => row.DurationMs) > 240000, "duration was incorrectly interpreted as a total playlist budget");
        var unrestricted = SmartPlaylistEvaluator.Evaluate(rule with { FavoritesOnly = false, DownloadedOnly = false, ArtistIds = [], MaximumMinutes = null }, rows, favorites);
        Assert(unrestricted.Count == rows.Length - 1 && unrestricted.Any(row => row.DurationMs == 0), "disabled rules still filtered collection");
        favorites.Remove("a");
        Assert(SmartPlaylistEvaluator.Evaluate(rule, rows, favorites).SequenceEqual(new[] { boundary }), "composition did not respond to current likes");
        Assert(!SmartPlaylistEvaluator.Evaluate(rule with { ArtistIds = ["artist-b"] }, rows, favorites).Any(row => row.Artists.Any(artist => artist.Id == "artist-a")),
            "artist names were treated as identities");
        return Task.CompletedTask;
    }

    private static async Task QuickSmartStore()
    {
        var directory = Path.Combine(_directory, "quick-smart-store");
        var store = new SmartPlaylistStore(directory); var other = new SmartPlaylistStore(directory);
        const string accountA = "fixture-account-A", accountB = "fixture-account-B";
        var first = new SmartPlaylistRule(Guid.NewGuid(), "Скачанные любимые", true, true);
        Assert(store.Load(accountA).Count == 0, "new account was not empty");
        store.Upsert(accountA, first);
        Assert(other.Load(accountA).Single().Name == first.Name && other.Load(accountB).Count == 0, "readback or account isolation failed");
        Assert(!store.FilePath(accountA).Contains(accountA, StringComparison.Ordinal)
            && !File.ReadAllText(store.FilePath(accountA)).Contains(accountA, StringComparison.Ordinal), "raw account identity entered rule storage");
        await Task.WhenAll(Enumerable.Range(0, 12).Select(index => Task.Run(() =>
            new SmartPlaylistStore(directory).Upsert(accountA, new SmartPlaylistRule(Guid.NewGuid(), "Параллельная " + index, MaximumMinutes: index + 1)))));
        Assert(store.Load(accountA).Count == 13, "concurrent store instances lost edits");
        other.Upsert(accountA, first with { Name = "Новое имя", MaximumMinutes = 4 });
        var restored = store.Load(accountA).Single(rule => rule.Id == first.Id);
        Assert(restored.Name == "Новое имя" && restored.MaximumMinutes == 4, "editing duplicated or failed to persist the rule");
        other.Remove(accountA, first.Id); Assert(store.Load(accountA).Count == 12, "delete was not persisted");
        var goodBytes = File.ReadAllBytes(store.FilePath(accountA));
        File.WriteAllBytes(store.FilePath(accountB), goodBytes);
        try { store.Load(accountB); throw new InvalidOperationException("foreign account file was accepted"); } catch (InvalidDataException) { }
        File.WriteAllText(store.FilePath(accountA), "{bad rules}");
        var corruptHash = SHA256.HashData(File.ReadAllBytes(store.FilePath(accountA)));
        try { store.Upsert(accountA, first); throw new InvalidOperationException("corrupt storage was overwritten"); } catch (InvalidDataException) { }
        Assert(SHA256.HashData(File.ReadAllBytes(store.FilePath(accountA))).SequenceEqual(corruptHash), "malformed input changed during failed save");
        File.WriteAllBytes(store.FilePath(accountA), new byte[SmartPlaylistStore.MaximumBytes + 1]);
        try { store.Load(accountA); throw new InvalidOperationException("unbounded storage was accepted"); } catch (InvalidDataException) { }
        Assert(!Directory.EnumerateFiles(directory, "*.tmp").Any(), "atomic save left a temporary file");
    }

    private static Task QuickSmartIndex()
    {
        TrackRow[] rows = [QuickSmartRow("first"), QuickSmartRow("second", "album-b", "artist-b"), QuickSmartRow("first")];
        var playlist = new CatalogItem("17", "playlist", "Мой тестовый плейлист", "Автор", "owner-a", "17");
        var secondOwner = playlist with { Owner = "owner-b", Title = "Другой плейлист" };
        var tracks = new Dictionary<string, IReadOnlyList<TrackRow>>(StringComparer.Ordinal)
        { [QuickAccessIndex.CatalogKey(playlist)] = [rows[0]] };
        var index = QuickAccessIndex.Build(rows, [playlist, secondOwner], tracks);
        Assert(index.Count(entry => entry.Kind == "track") == 2 && index.Count(entry => entry.Kind == "album") == 2
            && index.Count(entry => entry.Kind == "artist") == 2 && index.Count(entry => entry.Kind == "playlist") == 2,
            "entity identity was inferred from names or track duplicates survived");
        Assert(QuickAccessIndex.Search(index, "тестовый мой", "playlist").Single().Tracks.Single().Id == "first", "case-insensitive token search or cached playlist composition failed");
        Assert(QuickAccessIndex.Search(index, "", maximum: 1).Count == 1
            && QuickAccessIndex.Search(index, "ничего подобного").Count == 0, "result limit or empty result failed");
        Assert(QuickAccessIndex.Search(index, "одинаковое", "album").Count == 2, "same-titled distinct albums were merged");
        return Task.CompletedTask;
    }

    private static Task QuickSmartDialog()
    {
        using var fixture = QuickSmartFixture("dialog"); var window = fixture.Window;
        foreach (var modifier in new[] { ModifierKeys.None, ModifierKeys.Control | ModifierKeys.Shift, ModifierKeys.Alt, ModifierKeys.Control | ModifierKeys.Windows })
            Assert(!fixture.Key(Key.K, modifiers: modifier).Handled, "unrelated key combination was consumed: " + modifier);
        Assert(fixture.Key(Key.K, modifiers: ModifierKeys.Control).Handled, "Ctrl K did not route to quick search");
        var dialog = Pk040Field<Window>(window, "_quickAccessDialog");
        Assert(!dialog.IsVisible && new WindowInteropHelper(dialog).Handle == 0, "quick fixture created a native window");
        var mode = ThemeManager.Mode;
        try
        {
            foreach (var theme in new[] { "Dark", "Light" })
            {
                ThemeManager.SetMode(theme);
                QuickSmartRenderDialog(dialog, new Size(712, 530), "quick-smart-search-" + theme.ToLowerInvariant() + ".png");
            }
        }
        finally { ThemeManager.SetMode(mode); }
        var results = QuickSmartElement<ListBox>(dialog, "QuickAccessResults");
        Assert(results.Items.Count >= 3 && results.Items.Cast<QuickAccessEntry>().Any(entry => entry.Kind == "album"), "local track/album results were absent");
        var enqueue = QuickSmartLogical<Button>((DependencyObject)dialog.Content).Single(button => button.Content is string label && label == "В очередь");
        Assert(!fixture.Key(Key.Enter, enqueue).Handled, "dialog consumed Enter intended for an action button");
        var query = QuickSmartElement<TextBox>(dialog, "QuickAccessQuery"); query.Text = "Ничего подобного в коллекции нет";
        Assert(fixture.Key(Key.Enter, query).Handled && results.Items.Count == 0,
            "Enter in the query did not apply the pending search or escape the search-input scope");
        Assert(fixture.Key(Key.Escape, dialog).Handled && Pk040Field<Window?>(window, "_quickAccessDialog") is null, "Escape did not close the search dialog");
        fixture.Key(Key.K, modifiers: ModifierKeys.Control);
        window.SignOut();
        Assert(Pk040Field<Window?>(window, "_quickAccessDialog") is null
            && Pk040Field<Dictionary<string, CatalogItem[]>>(window, "_quickCollections").Count == 0,
            "logout retained old-account results");
        Assert(fixture.Http.Requests == 0 && !fixture.Player.HasSource, "opening or dismissing local search reached network/audio");
        return Task.CompletedTask;
    }

    private static Task QuickSmartCrud()
    {
        using var fixture = QuickSmartFixture("crud"); var window = fixture.Window;
        Pk040Call(window, "OpenSmartPlaylists");
        var manager = Pk040Field<Window>(window, "_smartPlaylistDialog");
        QuickSmartClick(manager, "＋ Создать");
        var editor = Pk040Field<Window>(window, "_smartRuleDialog");
        QuickSmartElement<TextBox>(editor, "SmartRuleName").Text = "Композиции до четырёх минут";
        QuickSmartElement<CheckBox>(editor, "SmartRuleDownloaded").IsChecked = false;
        QuickSmartElement<TextBox>(editor, "SmartRuleMinutes").Text = "4";
        var mode = ThemeManager.Mode;
        try
        {
            foreach (var theme in new[] { "Dark", "Light" })
            {
                ThemeManager.SetMode(theme);
                QuickSmartRenderDialog(editor, new Size(592, 610), "quick-smart-rules-" + theme.ToLowerInvariant() + ".png");
            }
        }
        finally { ThemeManager.SetMode(mode); }
        QuickSmartClick(editor, "Сохранить правила");
        var list = QuickSmartElement<ListBox>(manager, "SmartPlaylistsList");
        var rule = (SmartPlaylistRule)list.Items[0];
        Assert(list.Items.Count == 1 && rule.MaximumMinutes == 4 && !rule.DownloadedOnly, "editor did not persist the requested rule");
        QuickSmartRenderDialog(manager, new Size(732, 550), "quick-smart-library.png");
        QuickSmartClick(manager, "Открыть");
        Assert(Pk040Field<string>(window, "_section") == "smart-playlist-detail"
            && Pk040Field<TrackRows>(window, "_tracks").Count == 3, "local rule did not open its dynamic composition");
        Pk040Field<HashSet<string>>(window, "_favoriteTrackIds").Remove("quick-1");
        Pk040Call(window, "RefreshQuickAccessCollection");
        Assert(Pk040Field<System.Windows.Threading.DispatcherTimer>(window, "_quickRefreshTimer").IsEnabled, "collection events were not coalesced");
        Pk040Call(window, "FlushQuickAccessCollection");
        Assert(Pk040Field<TrackRows>(window, "_tracks").Count == 2, "composition retained a removed favorite");
        Pk040Call(window, "SetSection", "favorites", "Мне нравится");
        Pk040Call(window, "ShowRows", Pk040Field<List<TrackRow>>(window, "_favoritesCache"));
        Pk040Call(window, "OpenSmartPlaylists"); manager = Pk040Field<Window>(window, "_smartPlaylistDialog");
        QuickSmartClick(manager, "Изменить / переименовать"); editor = Pk040Field<Window>(window, "_smartRuleDialog");
        QuickSmartElement<TextBox>(editor, "SmartRuleName").Text = "Переименованная подборка";
        QuickSmartClick(editor, "Сохранить правила"); manager.Close();
        Pk040Call(window, "BrowseBack_Click", window, new RoutedEventArgs());
        Assert(Control<TextBlock>(window, "CollectionTitle").Text == "Переименованная подборка"
            && Pk040Field<TrackRows>(window, "_tracks").Count == 2
            && Pk040Field<CatalogItem>(window, "_openedCatalogItem").Id == rule.Id.ToString("D"),
            "Back restored an old composition, title or a different rule");
        Pk040Call(window, "OpenSmartPlaylists"); manager = Pk040Field<Window>(window, "_smartPlaylistDialog");
        var beforeWindows = Application.Current.Windows.Cast<Window>().ToHashSet();
        QuickSmartClick(manager, "Удалить");
        var confirmation = Application.Current.Windows.Cast<Window>().Single(candidate => !beforeWindows.Contains(candidate));
        QuickSmartClick(confirmation, "Удалить правило"); Pk040Call(window, "FlushQuickAccessCollection");
        Assert(QuickSmartElement<ListBox>(manager, "SmartPlaylistsList").Items.Count == 0
            && Pk040Field<TrackRows>(window, "_tracks").Count == 0,
            "delete retained a saved rule or its visible composition");
        manager.Close();
        Assert(fixture.Http.Requests == 0 && !fixture.Player.HasSource, "local smart CRUD used account/network playback");
        return Task.CompletedTask;
    }

    private static async Task QuickSmartQueue()
    {
        using var fixture = QuickSmartFixture("queue"); var window = fixture.Window;
        var rows = Pk040Field<List<TrackRow>>(window, "_favoritesCache");
        var queue = Pk040Field<PlaybackQueue>(window, "_playbackQueue"); queue.SetItems([rows[0]]);
        var entry = new QuickAccessEntry("test", "album", "Локальный набор", "", [rows[1], rows[2]]);
        var generation = Pk040Field<int>(window, "_accountGeneration");
        await QuickSmartExecute(window, entry, "next", generation);
        Assert(queue.Items.SequenceEqual(rows), "Play Next reversed collection order");
        await QuickSmartExecute(window, entry, "enqueue", generation);
        Assert(queue.Items.SequenceEqual(new[] { rows[0], rows[1], rows[2], rows[1], rows[2] }), "enqueue failed to append in order");
        var version = queue.Version;
        await QuickSmartExecute(window, entry, "next", generation - 1);
        Assert(queue.Version == version, "a stale action mutated the queue");
        queue.SetItems([]);
        await QuickSmartExecute(window, entry, "next", generation);
        Assert(queue.Items.SequenceEqual(new[] { rows[1], rows[2] }) && ReferenceEquals(queue.Current, rows[1]),
            "Play Next reversed a batch in an empty queue");
        var unavailable = new TrackRow { Id = "unavailable", RequestId = "unavailable:0", Available = false };
        queue.SetItems([unavailable]);
        await QuickSmartExecute(window, entry, "next", generation);
        Assert(queue.Items.SequenceEqual(new[] { rows[1], rows[2], unavailable }) && ReferenceEquals(queue.Current, rows[1]),
            "Play Next lost unavailable rows or reversed a batch when no current occurrence existed");
        await QuickSmartExecute(window, entry, "open", generation);
        Assert(Pk040Field<CatalogItem>(window, "_openedCatalogItem").Kind == "quick"
            && (bool)typeof(MainWindow).GetMethod("RefreshSmartPlaylistView", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, null)!,
            "quick source lost its snapshot identity or refresh fell through to an unsupported service entity");
        Assert(fixture.Http.Requests == 0 && !fixture.Player.HasSource, "queue actions started music/network");
    }

    private static Pk040Fixture QuickSmartFixture(string name)
    {
        var fixture = new Pk040Fixture("quick-smart-" + name); fixture.Account();
        var rows = Enumerable.Range(0, 3).Select(index => new TrackRow
        {
            Id = "quick-" + index, RequestId = "quick-" + index + ":album-quick", Title = "Приватный тестовый трек " + index,
            Artist = "Локальный исполнитель", Artists = [new ArtistReference("artist-quick", "Локальный исполнитель")],
            Album = "Приватный тестовый альбом", AlbumId = "album-quick", DurationMs = 180000,
            Available = true, LocalPath = fixture.Rows[0].LocalPath
        }).ToArray();
        Pk040Field<List<TrackRow>>(fixture.Window, "_favoritesCache").AddRange(rows);
        Pk040Field<HashSet<string>>(fixture.Window, "_favoriteTrackIds").UnionWith(rows.Select(row => row.Id));
        Pk040Set(fixture.Window, "_favoriteIdsKnown", true);
        Pk040Call(fixture.Window, "ShowRows", (object)rows);
        return fixture;
    }

    private static Task QuickSmartExecute(MainWindow window, QuickAccessEntry entry, string action, int generation)
        => (Task)typeof(MainWindow).GetMethod("ExecuteQuickAccessEntryAsync", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(window, [entry, action, "12345", generation])!;

    private static void QuickSmartRenderDialog(Window dialog, Size size, string name)
    {
        var content = (FrameworkElement)dialog.Content;
        // An unshown Window may retain a layout clip from an earlier manual
        // capture. Arrange the identical content in a temporary root so the
        // window's native-size constraints cannot crop the fixture evidence.
        dialog.Content = null;
        var root = new Grid { Background = dialog.Background };
        TextElement.SetForeground(root, dialog.Foreground);
        root.Children.Add(content);
        RenderTargetBitmap bitmap;
        try
        {
            root.Measure(size); root.Arrange(new Rect(new Point(), size)); root.UpdateLayout();
            var drawing = new DrawingVisual();
            using (var context = drawing.RenderOpen())
            {
                context.DrawRectangle(dialog.Background, null, new Rect(new Point(), size));
                context.DrawRectangle(new VisualBrush(root), null, new Rect(new Point(), size));
            }
            bitmap = new RenderTargetBitmap((int)Math.Ceiling(size.Width), (int)Math.Ceiling(size.Height), 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(drawing);
        }
        finally { root.Children.Remove(content); dialog.Content = content; }
        var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
        bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
        for (var alpha = 3; alpha < pixels.Length; alpha += 4)
            Assert(pixels[alpha] == 255, "dialog evidence retained transparent pixels: " + name);
        Theme040Save(bitmap, name);
    }

    private static T QuickSmartElement<T>(Window window, string name) where T : FrameworkElement
        => QuickSmartLogical<T>((DependencyObject)window.Content).Single(element => element.Name == name);
    private static void QuickSmartClick(Window window, string text)
        => QuickSmartLogical<Button>((DependencyObject)window.Content).Single(button => button.Content is string content && content == text)
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static IEnumerable<T> QuickSmartLogical<T>(DependencyObject parent) where T : DependencyObject
    {
        if (parent is T result) yield return result;
        foreach (var child in LogicalTreeHelper.GetChildren(parent).OfType<DependencyObject>())
            foreach (var element in QuickSmartLogical<T>(child)) yield return element;
    }
}
