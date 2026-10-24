using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using YMusic.Native;

internal static partial class Program
{
    private static async Task CheckSearchArtist()
    {
        await Run("Search/Artist: categories use exact API types and pages, friendly groups and compact tracks", SaSearchCategories);
        await Run("Search/Artist: old playlist artist resolves exact ID, profile data and Back preserve browse and queue", SaPlaylistArtist);
        await Run("Search/Artist: Home retains server shelves, full All source and correct card navigation", SaHomeNavigation);
        await Run("Search/Artist: late canceled profile and signout cannot restore prior artist or Home data", SaCanceledArtist);
    }

    private static async Task SaSearchCategories()
    {
        using var fixture = new SaFixture("search");
        var window = fixture.Window; await fixture.Activate();
        SaClick(Control<Button>(window, "SearchNav"));
        var buttons = Control<WrapPanel>(window, "SearchCategories").Children.OfType<Button>().ToArray();
        Assert(buttons.Select(b => (string)b.Tag).SequenceEqual(new[] { "all", "track", "artist", "collections", "album", "playlist" }), "visible category identity/order differs");
        Assert(Control<ComboBox>(window, "CatalogType").Visibility == Visibility.Collapsed, "old search combo is still presented beside the categories");
        Control<TextBox>(window, "CatalogQuery").Text = "точный запрос";
        foreach (var type in new[] { "all", "track", "artist", "album", "playlist", "collections" })
        {
            var before = fixture.Searches.Count;
            SaClick(buttons.Single(b => (string)b.Tag == type));
            await Until(() => SaField<string>(window, "_searchType") == type && SaField<CancellationTokenSource?>(window, "_featureCts") is null);
            var requests = fixture.Searches.Skip(before).ToArray();
            Assert(requests.Length == (type == "collections" ? 2 : 1) && requests.All(r => r.Page == 0 && r.Text == "точный запрос"), "category reused a previous query/page");
            Assert(requests.Select(r => r.Type).Order().SequenceEqual((type == "collections" ? new[] { "album", "playlist" } : new[] { type }).Order()), "category sent the wrong API type");
            if (type == "collections")
                Assert(Control<ListBox>(window, "CatalogList").Items.Cast<CatalogItem>().Select(item => item.Id).SequenceEqual(new[] { "compilation", "official-playlist" }), "collections accepted a normal album or a user playlist named after Yandex");
        }
        SaClick(buttons.Single(b => (string)b.Tag == "track"));
        await Until(() => SaField<string>(window, "_searchType") == "track" && SaField<CancellationTokenSource?>(window, "_featureCts") is null);
        Assert(Control<Button>(window, "MoreCatalogButton").IsEnabled, "search first page lost its continuation");
        SaClick(Control<Button>(window, "MoreCatalogButton"));
        await Until(() => SaField<int>(window, "_searchPage") == 1 && SaField<CancellationTokenSource?>(window, "_featureCts") is null);
        Assert(fixture.Searches.Last() == new SaSearch("track", 1, "точный запрос"), "More did not preserve type/query and increment page");
        Assert(Control<ListBox>(window, "CatalogList").Items.Cast<CatalogItem>().Select(item => item.Id).SequenceEqual(new[] { "track-0", "track-1" }), "continuation replaced or duplicated the first page");

        SaClick(buttons.Single(b => (string)b.Tag == "all"));
        await Until(() => SaField<string>(window, "_searchType") == "all" && SaField<CancellationTokenSource?>(window, "_featureCts") is null);
        window.UpdateLayout(); await Task.Delay(120);
        var list = Control<ListBox>(window, "CatalogList");
        var view = CollectionViewSource.GetDefaultView(list.ItemsSource);
        Assert(view.Groups is { Count: 4 } && view.Groups.Cast<CollectionViewGroup>().Select(g => g.Name.ToString()).ToHashSet().SetEquals(new[] { "track", "artist", "album", "playlist" }), "All results are not grouped by real entity kind");
        var labels = (IValueConverter)window.FindResource("CatalogGroupLabel");
        Assert(new[] { "track", "artist", "album", "playlist" }.Select(kind => (string)labels.Convert(kind, typeof(string), null!, CultureInfo.InvariantCulture)).SequenceEqual(new[] { "Треки", "Исполнители", "Альбомы и сборники", "Плейлисты" }), "group headers expose technical kind labels");
        Assert(!SaVisuals<TextBlock>(list).Any(text => new[] { "track", "artist", "album", "playlist" }.Contains(text.Text)), "technical kind string is rendered in result rows");
        var track = list.Items.Cast<CatalogItem>().First(item => item.Kind == "track"); list.ScrollIntoView(track); window.UpdateLayout();
        var container = (ListBoxItem)list.ItemContainerGenerator.ContainerFromItem(track);
        if (container is null) throw new InvalidOperationException("track result has no realized item container");
        Assert(SaVisuals<Border>(container).Any(border => border.Name == "ResultSurface" && border.MinHeight == 46), "track result is not using the compact row template");
        Assert(SaVisuals<Button>(container).Any(button => button.Name == "ResultOpenButton" && button.Content.ToString() == "▶"), "compact track row lacks its direct play action");
        var heights = new List<object>();
        foreach (var (width, height) in new[] { (1280, 860), (1050, 680) })
        {
            window.Width = width; window.Height = height; window.UpdateLayout(); await Task.Delay(290);
            Assert(Control<WrapPanel>(window, "CollectionToolbar").Visibility == Visibility.Collapsed
                && Control<Grid>(window, "TrackFilterPanel").Visibility == Visibility.Collapsed,
                "search still reserves space for collection-only actions/filter");
            Assert(list.ActualHeight >= 250, $"search results have insufficient visible height at {width}x{height}: {list.ActualHeight:0.##}");
            heights.Add(new { width, height, resultHeight = list.ActualHeight });
            App.Capture(window, Path.Combine(_directory, $"search-artist-search-groups-{width}x{height}.png"));
        }
        File.WriteAllText(Path.Combine(_directory, "search-compact-height-readback.json"), JsonSerializer.Serialize(heights, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static async Task SaPlaylistArtist()
    {
        using var fixture = new SaFixture("playlist-artist");
        var window = fixture.Window; await fixture.Activate();
        // A cached pre-0.3.5 playlist lacks structured artists. Its track metadata must resolve the exact ID.
        var rows = Enumerable.Range(0, 70).Select(i => new TrackRow { Number = i + 1, Id = "legacy-" + i, RequestId = "legacy-" + i + ":1", Title = "Сохранённый трек " + i, Artist = "Одинаковое имя", Available = true, DurationMs = 180000 }).ToArray();
        var playlist = new PlaylistDetails("owner", "77", "Сохранённый плейлист", "", 1, rows);
        SaInvoke(window, "ShowPlaylist", playlist);
        Control<TextBox>(window, "FilterBox").Text = "Сохранённый"; await Task.Delay(300);
        var list = Control<ListView>(window, "TracksList"); list.SelectedIndex = 12;
        window.UpdateLayout(); var scroll = SaVisuals<ScrollViewer>(list).First(); scroll.ScrollToVerticalOffset(368); await Task.Delay(60);
        var beforeOffset = scroll.VerticalOffset;
        var queue = SaField<PlaybackQueue>(window, "_playbackQueue"); queue.SetItems([rows[3], rows[4]], rows[4]); var version = queue.Version;
        var legacy = rows[12];
        SaInvoke(window, "ArtistName_Click", new Button { Tag = legacy }, new RoutedEventArgs(Button.ClickEvent));
        await Until(() => SaField<ArtistDetails?>(window, "_artistDetails")?.Id == "artist-42" && SaField<CancellationTokenSource?>(window, "_featureCts") is null);
        Assert(fixture.Handler.Seen.Any(r => r.Uri.AbsolutePath == "/tracks" && Uri.UnescapeDataString(r.Body).Contains("legacy-12:1", StringComparison.Ordinal)), "legacy artist link did not resolve its exact composite track reference");
        Assert(fixture.Handler.Seen.Any(r => r.Uri.AbsolutePath == "/artists/artist-42/brief-info") && !fixture.Handler.Seen.Any(r => r.Uri.AbsolutePath == "/search"), "artist navigation guessed by name instead of using the returned ID");
        Assert(Control<Grid>(window, "ArtistPanel").IsVisible && !Control<Grid>(window, "ContentGrid").IsVisible, "artist did not open as its own page");
        Assert(Control<TextBlock>(window, "ArtistName").Text == "Исполнитель 42" && Control<TextBlock>(window, "ArtistMonthlyText").Visibility == Visibility.Visible && Control<TextBlock>(window, "ArtistMonthlyText").Text.Contains(1234567L.ToString("N0"), StringComparison.Ordinal), "monthly listeners/name are not the exact profile statistic");
        Assert(Control<ListBox>(window, "ArtistTopTracks").Items.Cast<TrackRow>().Select(row => row.Id).SequenceEqual(new[] { "top-z", "top-a", "top-m" }), "popular tracks lost the service order");
        Assert(Control<ItemsControl>(window, "ArtistAlbums").Items.Count == 1 && Control<ItemsControl>(window, "ArtistSimilar").Items.Count == 1 && Control<TextBlock>(window, "ArtistDescription").Text == "Биография из brief-info", "artist albums/similar/biography were not rendered");
        Assert(queue.Version == version && ReferenceEquals(queue.Current, rows[4]) && !fixture.Player.HasSource, "opening an artist changed the playback queue or started audio");
        await SaCapture(window, "artist-profile");
        SaClick(Control<Button>(window, "ArtistBackButton")); await Task.Delay(80);
        Assert(SaField<string>(window, "_section") == "playlist-detail" && Control<TextBlock>(window, "CollectionTitle").Text == playlist.Title && Control<TextBox>(window, "FilterBox").Text == "Сохранённый", "Back lost the original playlist/filter");
        Assert(list.SelectedIndex == 12 && Math.Abs(scroll.VerticalOffset - beforeOffset) < 1 && queue.Version == version && ReferenceEquals(queue.Current, rows[4]), "Back lost row selection/scroll or changed queue identity");
        await Call(window, "OpenArtistAsync", "artist-empty", "Неизвестная статистика", true);
        Assert(Control<TextBlock>(window, "ArtistMonthlyText").Visibility == Visibility.Collapsed && Control<TextBlock>(window, "ArtistMonthlyText").Text.Length == 0, "unknown listeners retained the previous count or displayed zero");
        Assert(Control<TextBlock>(window, "ArtistDescription").Text == "Биография из about-artist" && Control<TextBlock>(window, "ArtistDescription").Visibility == Visibility.Visible, "direct about-artist biography was lost");
        Assert(queue.Version == version && !fixture.Player.HasSource, "second profile opening started audio or changed queue");
    }

    private static async Task SaHomeNavigation()
    {
        using var fixture = new SaFixture("home");
        var window = fixture.Window; await fixture.Activate(); await Call(window, "LoadHomeAsync"); window.UpdateLayout();
        var shelves = Control<ItemsControl>(window, "HomeShelves");
        Assert(Control<Grid>(window, "HomePanel").IsVisible && !Control<Grid>(window, "ContentGrid").IsVisible && shelves.Items.Count == 2, "Home is still a flattened catalog list");
        Assert(SaVisuals<Button>(Control<Grid>(window, "HomePanel")).Count(button => button.Style == (Style)Control<Grid>(window, "HomePanel").FindResource("HomeHeroButton")) == 2, "Home lacks its two interactive wave cards");
        Assert(SaVisuals<Button>(shelves).Count(button => button.Tag is CatalogItem) == 12, "Home did not limit each shelf preview to six cards");
        Assert(Control<TextBlock>(window, "HomeHeading").Text.Contains("Fixture Account", StringComparison.Ordinal), "Home greeting does not use the activated account");
        var firstAll = SaVisuals<Button>(shelves).First(button => button.Tag is HomeSection);
        var source = (HomeSection)firstAll.Tag;
        Assert(source.Items.Count == 9 && source.Items[0].Id == "home-album-0", "All lost the complete original shelf");
        SaClick(firstAll); window.UpdateLayout();
        Assert(SaField<string>(window, "_section") == "home-shelf" && Control<ListBox>(window, "CatalogList").Items.Cast<CatalogItem>().Select(item => item.Id).SequenceEqual(source.Items.Select(item => item.Id)), "All opened a mixed/truncated shelf");
        SaClick(Control<Button>(window, "BrowseBackButton")); window.UpdateLayout();
        Assert(Control<Grid>(window, "HomePanel").IsVisible && shelves.Items.Count == 2, "Back from full shelf did not restore Home");
        var card = SaVisuals<Button>(shelves).First(button => button.Tag is CatalogItem item && item.Id == "home-album-0");
        SaClick(card); await Until(() => SaField<string>(window, "_section") == "album-detail" && SaField<CancellationTokenSource?>(window, "_featureCts") is null);
        Assert(fixture.Handler.Seen.Any(r => r.Uri.AbsolutePath == "/albums/home-album-0/with-tracks") && !fixture.Player.HasSource, "Home card opened the wrong object or auto-played its album");
        SaClick(Control<Button>(window, "BrowseBackButton")); await Task.Delay(60);
        Assert(Control<Grid>(window, "HomePanel").IsVisible && !fixture.Player.HasSource, "Back from a Home card lost Home or started audio");
        await SaCapture(window, "home-shelves");
    }

    private static async Task SaCanceledArtist()
    {
        using var fixture = new SaFixture("cancellation");
        var window = fixture.Window; await fixture.Activate();
        fixture.HoldArtist = "slow"; var old = Call(window, "OpenArtistAsync", "slow", "Старый исполнитель", true);
        await fixture.Entered.Task; await Call(window, "OpenArtistAsync", "artist-42", "Новый исполнитель", true);
        fixture.Release.TrySetResult(); await old;
        Assert(SaField<ArtistDetails?>(window, "_artistDetails")?.Id == "artist-42" && Control<TextBlock>(window, "ArtistName").Text == "Исполнитель 42" && fixture.CancellationObserved, "canceled response replaced the newer artist");
        await Call(window, "LoadHomeAsync");
        fixture.ResetHold("signout-slow"); var pending = Call(window, "OpenArtistAsync", "signout-slow", "Поздний исполнитель", true);
        await fixture.Entered.Task; window.SignOut(); fixture.Release.TrySetResult(); await pending;
        Assert(SaField<ArtistDetails?>(window, "_artistDetails") is null && SaField<IReadOnlyList<HomeSection>>(window, "_homeSections").Count == 0 && Control<ItemsControl>(window, "HomeShelves").Items.Count == 0, "signout retained account-scoped artist/Home data");
        Assert(Control<ListBox>(window, "ArtistTopTracks").Items.Count == 0 && Control<ItemsControl>(window, "ArtistAlbums").Items.Count == 0 && Control<ItemsControl>(window, "ArtistSimilar").Items.Count == 0 && Control<TextBlock>(window, "ArtistMonthlyText").Text.Length == 0, "late signout response repopulated artist content/statistics");
        Assert(!Control<Grid>(window, "ArtistPanel").IsVisible && !Control<Grid>(window, "HomePanel").IsVisible && SaField<PlaybackQueue>(window, "_playbackQueue").Items.Count == 0, "signout left personalized pages/queue active");
    }

    private static T SaField<T>(MainWindow window, string name) => (T)typeof(MainWindow).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!;
    private static void SaInvoke(MainWindow window, string name, params object[] args) => typeof(MainWindow).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, args);
    private static void SaClick(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static IEnumerable<T> SaVisuals<T>(DependencyObject root) where T : DependencyObject
    { if (root is T typed) yield return typed; for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) foreach (var child in SaVisuals<T>(VisualTreeHelper.GetChild(root, i))) yield return child; }
    private static async Task SaCapture(MainWindow window, string name)
    {
        foreach (var (width, height) in new[] { (1280, 860), (1050, 680) })
        { window.Width = width; window.Height = height; window.UpdateLayout(); await Task.Delay(290); App.Capture(window, Path.Combine(_directory, $"search-artist-{name}-{width}x{height}.png")); }
    }
    private sealed record SaSearch(string Type, int Page, string Text);

    private sealed class SaFixture : IDisposable
    {
        internal MainWindow Window { get; }
        internal NativePlayer Player { get; }
        internal FakeHandler Handler { get; }
        internal List<SaSearch> Searches { get; } = [];
        internal string? HoldArtist;
        internal TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool CancellationObserved;
        internal SaFixture(string name)
        {
            var directory = Path.Combine(_directory, "search-artist-" + name); Directory.CreateDirectory(directory);
            Handler = new FakeHandler(Respond);
            Player = new NativePlayer(new AudioBuffer(Path.Combine(directory, "audio"), new FakeHandler((_, _) => throw new InvalidOperationException("browse opened media network"))));
            var offline = new OfflineLibrary(Path.Combine(directory, "offline"), new AudioBuffer(Path.Combine(directory, "downloads"), new FakeHandler((_, _) => throw new InvalidOperationException("browse downloaded audio"))));
            Window = new(api: new MusicApi(Handler), store: new SessionStore(Path.Combine(directory, "session")), restore: false, diagnosticsDirectory: directory, offline: offline, player: Player)
            { Left = -12000, Top = -12000, ShowActivated = false, ShowInTaskbar = false, Width = 1280, Height = 860, WindowStartupLocation = WindowStartupLocation.Manual };
            Window.Show(); Control<Slider>(Window, "VolumeSlider").Value = 0;
        }
        internal async Task Activate() => await Call(Window, "ActivateAsync", FakeSession, CancellationToken.None);
        internal void ResetHold(string id)
        { HoldArtist = id; Entered = new(TaskCreationOptions.RunContinuationsAsynchronously); Release = new(TaskCreationOptions.RunContinuationsAsynchronously); }
        private async Task<HttpResponseMessage> Respond(HttpRequestMessage request, CancellationToken token)
        {
            var uri = request.RequestUri!; var path = uri.AbsolutePath;
            Assert(uri.Host == "api.music.yandex.net", "browse fixture left the API host");
            if (path == "/account/status") return Json(Profile);
            if (path == "/users/12345/likes/tracks") return Json("""{"result":{"library":{"tracks":[]}}}""");
            if (path == "/users/12345/likes/artists") return Json("""{"result":[{"artist":{"id":"artist-42","name":"Исполнитель 42"}}]}""");
            if (path == "/search")
            {
                var query = uri.Query.TrimStart('?').Split('&').Select(part => part.Split('=', 2)).ToDictionary(part => part[0], part => Uri.UnescapeDataString(part[1]));
                var type = query["type"]; var page = int.Parse(query["page"], CultureInfo.InvariantCulture); Searches.Add(new(type, page, query["text"]));
                return Json(SaSearchJson(type, page));
            }
            if (path == "/tracks")
            {
                var body = await request.Content!.ReadAsStringAsync(token); var ids = Uri.UnescapeDataString(body.Split('&')[0].Split('=')[1]).Split(',').Select(id => id.Split(':')[0]);
                return Json(JsonSerializer.Serialize(new { result = ids.Select(id => SaTrack(id)) }));
            }
            if (path.StartsWith("/artists/", StringComparison.Ordinal) && path.EndsWith("/brief-info", StringComparison.Ordinal))
            {
                var id = path.Split('/')[2];
                if (id == HoldArtist) { Entered.TrySetResult(); await Release.Task; CancellationObserved |= token.IsCancellationRequested; }
                return Json(SaArtistJson(id));
            }
            if (path.StartsWith("/artists/", StringComparison.Ordinal) && path.EndsWith("/about-artist", StringComparison.Ordinal))
                return Json(JsonSerializer.Serialize(new { artist = new { id = path.Split('/')[2] }, description = "Биография из about-artist", stats = new { likesCount = 9999999 } }));
            if (path.StartsWith("/albums/", StringComparison.Ordinal) && path.EndsWith("/with-tracks", StringComparison.Ordinal))
                return Json(JsonSerializer.Serialize(new { result = new { id = path.Split('/')[2], volumes = Array.Empty<object[]>() } }));
            if (path == "/landing3") return Json(SaHomeJson());
            throw new InvalidOperationException("Unexpected search/artist fixture endpoint " + path);
        }
        public void Dispose() { Release.TrySetResult(); Window.Close(); Player.Dispose(); Handler.Dispose(); }
    }

    private static object SaTrack(string id) => new { id, title = "Трек " + id, available = true, durationMs = 180000, artists = new[] { new { id = "artist-42", name = "Одинаковое имя" } }, albums = new[] { new { id = "1", title = "Альбом" } } };
    private static string SaArtistJson(string id) => JsonSerializer.Serialize(new
    {
        result = new
        {
            artist = new { id, name = id == "artist-42" ? "Исполнитель 42" : "Исполнитель " + id, description = id == "artist-42" ? "Биография из brief-info" : "" },
            stats = id == "artist-42" ? new Dictionary<string, long> { ["lastMonthListeners"] = 1234567 } : new Dictionary<string, long> { ["likesCount"] = 9999999 },
            popularTracks = new[] { SaTrack("top-z"), SaTrack("top-a"), SaTrack("top-m") },
            albums = new[] { new { id = "artist-album", title = "Альбом исполнителя" } },
            similarArtists = new[] { new { id = "artist-99", name = "Похожий исполнитель" } }
        }
    });
    private static string SaSearchJson(string type, int page)
    {
        var result = new Dictionary<string, object>();
        if (type is "all" or "track") result["tracks"] = new { order = 0, total = 2, perPage = 1, results = new[] { SaTrack("track-" + page) } };
        if (type is "all" or "artist") result["artists"] = new { order = 1, total = 1, perPage = 1, results = new[] { new { id = "artist-42", name = "Исполнитель 42" } } };
        if (type is "all" or "album") result["albums"] = new { order = 2, total = 2, perPage = 2, results = new[] { new { id = "normal-album", title = "Обычный альбом", type = "single" }, new { id = "compilation", title = "Настоящий сборник", type = "compilation" } } };
        if (type is "all" or "playlist") result["playlists"] = new { order = 3, total = 2, perPage = 2, results = new[] { new { kind = "official-playlist", title = "Редакционный плейлист", owner = new { uid = "103372440", login = "music-blog", name = "Яндекс Музыка", verified = true } }, new { kind = "pretender", title = "Обычный пользователь", owner = new { uid = "wrong", login = "music-blog", name = "Яндекс Музыка", verified = true } } } };
        return JsonSerializer.Serialize(new { result });
    }
    private static string SaHomeJson() => JsonSerializer.Serialize(new
    {
        result = new
        {
            blocks = new[]
            {
                new { title = "Полка альбомов", entities = Enumerable.Range(0, 9).Select(i => new { type = "album", data = new { id = "home-album-" + i, title = "Серверный альбом " + i } }).ToArray() },
                new { title = "Вторая полка", entities = Enumerable.Range(0, 8).Select(i => new { type = "album", data = new { id = "other-album-" + i, title = "Другая полка " + i } }).ToArray() }
            }
        }
    });
}
