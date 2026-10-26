using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using YMusic.Native;

internal static partial class Program
{
    private static async Task CheckSearchStateRecovery040()
    {
        await Run("SearchRecovery040: empty sidebar revisit resets continuation and the next Find starts at page zero", Search040SidebarReset);
        await Run("SearchRecovery040: mouse Back and Forward restore category, first page and continuation together", Search040BackForward);
        await Run("SearchRecovery040: history restores the exhausted second page without re-enabling More", Search040ExhaustedPage);
        await Run("SearchRecovery040: collections and albums restore separate categories and continuation requests", Search040Collections);
        await Run("SearchRecovery040: a late search response cannot overwrite mouse Back or a signed-out view", Search040LateHistory);
        await Run("SearchRecovery040: choosing an empty category cancels the previous query and clears its cursor", Search040EmptyCategory);
    }

    private static async Task Search040SidebarReset()
    {
        using var f = new Search040Fixture("sidebar-reset"); await f.Activate(); var w = f.Window;
        await Search040Start(f, "track", "first query");
        Search040Assert(w, "track", "track", false, "first query", 0, true, ["track-0"]);
        SaClick(Control<Button>(w, "FavoritesNav")); SaClick(Control<Button>(w, "SearchNav"));
        Search040Assert(w, "track", "track", false, "", 0, false, []);
        Assert(f.Searches.Length == 1, "opening an empty search issued a hidden request");
        SaClick(Search040Find(w)); await Search040Idle(w);
        Assert(f.Searches.Last() == new Search040Request("track", 0, "first query"), "Find resumed the discarded cursor");
        SaClick(Control<Button>(w, "MoreCatalogButton")); await Search040Idle(w);
        Search040Assert(w, "track", "track", false, "first query", 1, false, ["track-0", "track-1"]);
        Search040Facts(f, "sidebar-reset");
    }

    private static async Task Search040BackForward()
    {
        using var f = new Search040Fixture("back-forward"); await f.Activate(); var w = f.Window;
        await Search040Start(f, "track", "track query");
        var queue = SaField<PlaybackQueue>(w, "_playbackQueue"); var queueVersion = queue.Version;
        await Search040OpenArtist(f);
        SaClick(Control<Button>(w, "SearchNav")); SaClick(Search040Category(w, "album")); await Search040Idle(w);
        Search040Assert(w, "album", "album", false, "track query", 0, false, ["album-0"]);
        var requests = f.Searches.Length;
        NavMouse(w, MouseButton.XButton1); NavMouse(w, MouseButton.XButton1); await Task.Delay(60);
        Search040Assert(w, "track", "track", false, "track query", 0, true, ["track-0"]);
        NavMouse(w, MouseButton.XButton2); NavMouse(w, MouseButton.XButton2); await Task.Delay(60);
        Search040Assert(w, "album", "album", false, "track query", 0, false, ["album-0"]);
        Assert(f.Searches.Length == requests, "history reissued the restored searches");
        NavMouse(w, MouseButton.XButton1); NavMouse(w, MouseButton.XButton1);
        SaClick(Control<Button>(w, "MoreCatalogButton")); await Search040Idle(w);
        Search040Assert(w, "track", "track", false, "track query", 1, false, ["track-0", "track-1"]);
        Assert(f.Searches.Last() == new Search040Request("track", 1, "track query"), "restored More used the album category or wrong page");
        Assert(queue.Version == queueVersion && !f.Player.HasSource, "search history changed playback");
        Search040Facts(f, "back-forward");
    }

    private static async Task Search040ExhaustedPage()
    {
        using var f = new Search040Fixture("exhausted-page"); await f.Activate(); var w = f.Window;
        await Search040Start(f, "track", "exhausted query");
        SaClick(Control<Button>(w, "MoreCatalogButton")); await Search040Idle(w);
        await Search040OpenArtist(f);
        SaClick(Control<Button>(w, "SearchNav")); SaClick(Search040Category(w, "album")); await Search040Idle(w);
        NavMouse(w, MouseButton.XButton1); NavMouse(w, MouseButton.XButton1);
        Search040Assert(w, "track", "track", false, "exhausted query", 1, false, ["track-0", "track-1"]);
        SaClick(Search040Find(w)); await Search040Idle(w);
        Search040Assert(w, "track", "track", false, "exhausted query", 0, true, ["track-0"]);
        Assert(f.Searches.Last() == new Search040Request("track", 0, "exhausted query"), "Find did not replace the restored second page");
        Search040Facts(f, "exhausted-page");
    }

    private static async Task Search040Collections()
    {
        using var f = new Search040Fixture("collections"); await f.Activate(); var w = f.Window;
        await Search040Start(f, "album", "collections query");
        SaClick(Search040Category(w, "collections")); await Search040Idle(w);
        Search040Assert(w, "collections", "album", true, "collections query", 0, true, ["compilation-0", "editorial-0"]);
        // Leave through a real sidebar route so the collections snapshot has its own category state.
        SaClick(Control<Button>(w, "FavoritesNav")); SaClick(Control<Button>(w, "SearchNav"));
        Control<TextBox>(w, "CatalogQuery").Text = "album query";
        SaClick(Search040Category(w, "album")); await Search040Idle(w);
        Search040Assert(w, "album", "album", false, "album query", 0, false, ["album-0"]);
        NavMouse(w, MouseButton.XButton1); NavMouse(w, MouseButton.XButton1);
        Search040Assert(w, "collections", "album", true, "collections query", 0, true, ["compilation-0", "editorial-0"]);
        var before = f.Searches.Length;
        SaClick(Control<Button>(w, "MoreCatalogButton")); await Search040Idle(w);
        var continuation = f.Searches.Skip(before).ToArray();
        Assert(continuation.Length == 2 && continuation.All(r => r.Page == 1 && r.Text == "collections query")
            && continuation.Select(r => r.Type).Order().SequenceEqual(["album", "playlist"]), "restored collections More did not fetch both exact sources");
        Search040Assert(w, "collections", "album", true, "collections query", 1, false,
            ["compilation-0", "editorial-0", "compilation-1", "editorial-1"]);
        Search040Facts(f, "collections");
    }

    private static async Task Search040LateHistory()
    {
        using var f = new Search040Fixture("late-history"); await f.Activate(); var w = f.Window;
        await Search040Start(f, "track", "kept query"); await Search040OpenArtist(f);
        SaClick(Control<Button>(w, "SearchNav")); Control<TextBox>(w, "CatalogQuery").Text = "slow history";
        f.Hold("slow history"); SaClick(Search040Category(w, "album")); await f.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        NavMouse(w, MouseButton.XButton1); NavMouse(w, MouseButton.XButton1);
        f.Release.TrySetResult(); await Search040Idle(w);
        Search040Assert(w, "track", "track", false, "kept query", 0, true, ["track-0"]);
        Assert(f.CancellationObserved, "Back did not cancel the late search token");
        f.Hold("slow signout"); Control<TextBox>(w, "CatalogQuery").Text = "slow signout";
        SaClick(Search040Find(w)); await f.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        w.SignOut();
        var signedOutHistory = NavHistoryCount(w, "_browseHistory");
        var signedOutSection = SaField<string>(w, "_section");
        f.Release.TrySetResult(); await Search040Idle(w);
        Assert(SaField<Session?>(w, "_session") is null && Control<ListBox>(w, "CatalogList").Items.Count == 0
            && SaField<string>(w, "_section") == signedOutSection && NavHistoryCount(w, "_browseHistory") == signedOutHistory,
            "a late search changed the signed-out view or its history");
        Assert(f.CancellationObserved && !f.Player.HasSource, "signout failed to cancel search or started audio");
        Search040Facts(f, "late-history");
    }

    private static async Task Search040EmptyCategory()
    {
        using var f = new Search040Fixture("empty-category"); await f.Activate(); var w = f.Window;
        await Search040Start(f, "track", "previous query");
        f.Hold("slow empty"); Control<TextBox>(w, "CatalogQuery").Text = "slow empty";
        SaClick(Search040Find(w)); await f.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Control<TextBox>(w, "CatalogQuery").Clear(); SaClick(Search040Category(w, "album"));
        f.Release.TrySetResult(); await Search040Idle(w);
        Search040Assert(w, "album", "album", false, "", 0, false, []);
        Assert(f.CancellationObserved, "changing an empty category left the previous query alive");
        Assert(f.Searches.Length == 2, "an empty category issued an API request");
        Control<TextBox>(w, "CatalogQuery").Text = "next album"; SaClick(Search040Find(w)); await Search040Idle(w);
        Search040Assert(w, "album", "album", false, "next album", 0, false, ["album-0"]);
        Search040Facts(f, "empty-category");
    }

    private static async Task Search040Start(Search040Fixture f, string category, string query)
    {
        SaClick(Control<Button>(f.Window, "SearchNav")); Control<TextBox>(f.Window, "CatalogQuery").Text = query;
        SaClick(Search040Category(f.Window, category)); await Search040Idle(f.Window);
    }
    private static Task Search040Idle(MainWindow window) => Until(() => SaField<CancellationTokenSource?>(window, "_featureCts") is null);
    private static Button Search040Category(MainWindow window, string type)
        => Control<WrapPanel>(window, "SearchCategories").Children.OfType<Button>().Single(b => (string)b.Tag == type);
    private static Button Search040Find(MainWindow window)
        => SaVisuals<Button>(Control<StackPanel>(window, "CatalogSearchPanel")).Single(b => b.Content is string text && text == "Найти");
    private static async Task Search040OpenArtist(Search040Fixture f)
    {
        var list = Control<ListBox>(f.Window, "CatalogList"); var item = list.Items.Cast<CatalogItem>().First(i => i.Kind == "track");
        list.ScrollIntoView(item); f.Window.UpdateLayout();
        var row = (ListBoxItem)list.ItemContainerGenerator.ContainerFromItem(item)!;
        var link = SaVisuals<Button>(row).Single(b => b.Tag is ArtistReference artist && artist.Id == "artist-42");
        SaClick(link); await Search040Idle(f.Window);
        Assert(SaField<string>(f.Window, "_section") == "artist-detail" && SaField<ArtistDetails?>(f.Window, "_artistDetails")?.Id == "artist-42",
            "the realized track artist link did not open its exact artist");
    }
    private static void Search040Assert(MainWindow w, string type, string combo, bool collections, string text, int page, bool more, string[] ids)
    {
        Assert(SaField<string>(w, "_section") == "search" && Control<StackPanel>(w, "CatalogSearchPanel").Visibility == Visibility.Visible,
            "the search surface was not restored");
        Assert(SaField<string>(w, "_searchType") == type && SaField<string>(w, "_searchText") == text && SaField<int>(w, "_searchPage") == page,
            "the result query/type/page cursor differs");
        Assert((string)((ComboBoxItem)Control<ComboBox>(w, "CatalogType").SelectedItem).Tag == combo && SaField<bool>(w, "_collectionsSearch") == collections,
            "the category UI differs from its restored snapshot");
        var selected = collections ? "collections" : combo;
        Assert(Control<WrapPanel>(w, "SearchCategories").Children.OfType<Button>()
            .All(button => (AutomationProperties.GetHelpText(button) == "Выбрано") == ((string)button.Tag == selected)), "the category highlight differs from its state");
        Assert(Control<Button>(w, "MoreCatalogButton").IsEnabled == more, "the continuation button differs from the restored page");
        Assert(Control<ListBox>(w, "CatalogList").Items.Cast<CatalogItem>().Select(item => item.Id).SequenceEqual(ids), "the result pages were replaced, skipped or duplicated");
    }
    private static void Search040Facts(Search040Fixture f, string name)
    {
        var w = f.Window;
        File.WriteAllText(Path.Combine(_directory, "search-recovery040-" + name + ".json"), JsonSerializer.Serialize(new
        {
            section = SaField<string>(w, "_section"), searchText = SaField<string>(w, "_searchText"), searchType = SaField<string>(w, "_searchType"),
            page = SaField<int>(w, "_searchPage"), selectedType = (string)((ComboBoxItem)Control<ComboBox>(w, "CatalogType").SelectedItem).Tag,
            collections = SaField<bool>(w, "_collectionsSearch"), moreEnabled = Control<Button>(w, "MoreCatalogButton").IsEnabled,
            ids = Control<ListBox>(w, "CatalogList").Items.Cast<CatalogItem>().Select(item => item.Id).ToArray(), requests = f.Searches,
            cancellationObserved = f.CancellationObserved, nativeAudioOpened = f.Player.HasSource,
            historyCount = NavHistoryCount(w, "_browseHistory"), forwardCount = NavHistoryCount(w, "_browseForward")
        }, new JsonSerializerOptions { WriteIndented = true }));
    }

    private sealed record Search040Request(string Type, int Page, string Text);
    private sealed class Search040Fixture : IDisposable
    {
        internal MainWindow Window { get; }
        internal NativePlayer Player { get; }
        private readonly MusicApi _api;
        private readonly List<Search040Request> _searches = [];
        internal Search040Request[] Searches { get { lock (_searches) return _searches.ToArray(); } }
        private string? _holdQuery;
        internal TaskCompletionSource Entered { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool CancellationObserved { get; private set; }
        internal Search040Fixture(string name)
        {
            var directory = Path.Combine(_directory, "search-recovery040-" + name); Directory.CreateDirectory(directory);
            _api = new MusicApi(new FakeHandler(Respond));
            var noMedia = new FakeHandler((_, _) => throw new InvalidOperationException("search recovery opened media network"));
            Player = new NativePlayer(new AudioBuffer(Path.Combine(directory, "audio"), noMedia));
            var preferences = new PreferencesStore(Path.Combine(directory, "preferences"));
            preferences.Save(new PlayerPreferences { Volume = 0, UpdateCheckFrequency = UpdateCheckFrequency.Off });
            Window = new MainWindow(api: _api, store: new SessionStore(Path.Combine(directory, "session")), restore: false,
                diagnosticsDirectory: Path.Combine(directory, "diagnostics"),
                offline: new OfflineLibrary(Path.Combine(directory, "offline"), new AudioBuffer(Path.Combine(directory, "downloads"), noMedia)),
                player: Player, preferences: preferences)
            { Width = 1280, Height = 860, ShowActivated = false, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -18000, Top = 0 };
            Window.Show();
        }
        internal Task Activate() => Call(Window, "ActivateAsync", FakeSession, CancellationToken.None);
        internal void Hold(string query)
        {
            _holdQuery = query; CancellationObserved = false;
            Entered = new(TaskCreationOptions.RunContinuationsAsynchronously); Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        private async Task<HttpResponseMessage> Respond(HttpRequestMessage request, CancellationToken token)
        {
            var uri = request.RequestUri!; var path = uri.AbsolutePath;
            Assert(uri.Host == "api.music.yandex.net", "search fixture left its fake API host");
            if (path == "/account/status") return Json(Profile);
            if (path == "/users/12345/likes/tracks") return Json("""{"result":{"library":{"tracks":[]}}}""");
            if (path == "/users/12345/likes/artists") return Json("""{"result":[]}""");
            if (path == "/search")
            {
                var query = uri.Query.TrimStart('?').Split('&').Select(part => part.Split('=', 2))
                    .ToDictionary(part => part[0], part => Uri.UnescapeDataString(part[1]));
                var search = new Search040Request(query["type"], int.Parse(query["page"], CultureInfo.InvariantCulture), query["text"]);
                lock (_searches) _searches.Add(search);
                if (search.Text == _holdQuery) { Entered.TrySetResult(); await Release.Task; CancellationObserved |= token.IsCancellationRequested; }
                return Json(Search040Json(search));
            }
            if (path == "/artists/artist-42/brief-info") return Json(SaArtistJson("artist-42"));
            throw new InvalidOperationException("Unexpected search recovery fixture endpoint " + path);
        }
        public void Dispose() { Release.TrySetResult(); Window.Close(); Player.Dispose(); _api.Dispose(); }
    }
    private static string Search040Json(Search040Request search)
    {
        var collections = search.Text == "collections query";
        var result = new Dictionary<string, object>();
        if (search.Type == "track") result["tracks"] = new { total = 2, perPage = 1, results = new[] { SaTrack("track-" + search.Page) } };
        if (search.Type == "album") result["albums"] = new { total = collections ? 2 : 1, perPage = 1,
            results = new[] { new { id = (collections ? "compilation-" : "album-") + search.Page, title = "Локальный альбом", type = collections ? "compilation" : "single" } } };
        if (search.Type == "playlist") result["playlists"] = new { total = 2, perPage = 1,
            results = new[] { new { kind = "editorial-" + search.Page, title = "Локальная подборка", owner = new { uid = "103372440", login = "music-blog", name = "Яндекс Музыка", verified = true } } } };
        return JsonSerializer.Serialize(new { result });
    }
}
