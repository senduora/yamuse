using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using YMusic.Native;

internal static partial class Program
{
    private static async Task CheckCollectionHero()
    {
        await Run("Collection hero: album metadata shares one read and preserves disc/track order and exact IDs", CollectionHeroAlbumRead);
        await Run("Collection hero: playlist artwork, named owner and ISO dates remain optional read metadata", CollectionHeroPlaylistRead);
        await Run("Collection hero: unknown dates and duration are omitted, partial duration is explicit and Russian counts agree", CollectionHeroOptionalPresentation);
        await Run("Collection hero: inline album and playlist render at both sizes and navigation clears the header", CollectionHeroInline);
    }

    private const string HeroAlbumCover = "https://avatars.yandex.net/get-music-content/hero/album/%%";
    private const string HeroPlaylistCover = "https://avatars.yandex.net/get-music-content/hero/playlist/%%";
    private const string HeroAlbumJson = """
        {"result":{"id":"hero-album","title":"Ночной маршрут","artists":[{"id":1,"name":"Северный берег"},{"id":2,"name":"Океан"}],
        "year":2024,"releaseDate":"2024-11-08T23:45:00-03:00","coverUri":"avatars.yandex.net/get-music-content/hero/album/%%",
        "volumes":[[{"id":"hero-2","title":"После полуночи","available":true,"durationMs":180000,"albums":[{"id":"hero-album","title":"Ночной маршрут"}]}],
        [{"id":"hero-1","title":"Огни города","available":true,"durationMs":120000,"albums":[{"id":"hero-album","title":"Ночной маршрут"}]}]]}}
        """;

    private static async Task CollectionHeroAlbumRead()
    {
        var handler = new FakeHandler((request, _) =>
        {
            Assert(request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath == "/albums/hero-album/with-tracks", "album header made an extra or mutating request");
            return Task.FromResult(Json(HeroAlbumJson));
        });
        using var api = new MusicApi(handler);
        var album = await api.GetAlbumDetailsAsync(FakeSession, "hero-album", default);
        Assert(album is { Id: "hero-album", Title: "Ночной маршрут", Artists: "Северный берег, Океан", Year: 2024, CoverUri: HeroAlbumCover }
            && album.ReleaseDate == new DateOnly(2024, 11, 8), "album presentation metadata lost exact server fields or shifted the date through local time");
        Assert(album.Tracks.Select(track => track.Id).SequenceEqual(new[] { "hero-2", "hero-1" })
            && album.Tracks.Select(track => track.Number).SequenceEqual(new[] { 1, 2 })
            && album.Tracks.Select(track => track.RequestId).SequenceEqual(new[] { "hero-2:hero-album", "hero-1:hero-album" }), "header changed disc order, numbering or playback context");
        Assert(handler.Seen.Count == 1, "album details fetched metadata separately");
        var legacyRows = await api.GetAlbumTracksAsync(FakeSession, "hero-album", default);
        Assert(legacyRows.Select(track => track.RequestId).SequenceEqual(album.Tracks.Select(track => track.RequestId)) && handler.Seen.Count == 2,
            "existing album tracks contract made extra requests or reordered tracks");
    }

    private static async Task CollectionHeroPlaylistRead()
    {
        var handler = new FakeHandler((request, _) =>
        {
            Assert(request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath == "/users/12345/playlists/hero-list", "playlist header made an extra or mutating request");
            return Task.FromResult(Json("""
                {"result":{"kind":"hero-list","owner":{"uid":"12345","name":"Александр"},"title":"Тихий вечер","description":"Музыка для неспешных прогулок у моря.",
                "revision":8,"trackCount":0,"tracks":[],"created":"2025-04-15T23:30:01.123+10:00","modified":"2026-10-25T02:00:00Z",
                "cover":{"uri":"avatars.yandex.net/get-music-content/hero/playlist/%%"}}}
                """));
        });
        using var api = new MusicApi(handler);
        var playlist = await api.GetPlaylistAsync(FakeSession, "12345", "hero-list", default);
        Assert(playlist is { OwnerName: "Александр", CoverUri: HeroPlaylistCover, Description: "Музыка для неспешных прогулок у моря.", Revision: 8 }
            && playlist.Created == new DateOnly(2025, 4, 15) && playlist.Modified == new DateOnly(2026, 10, 25)
            && handler.Seen.Count == 1, "playlist metadata changed the read protocol, dates or author identity");
        var malformed = new FakeHandler((request, _) => Task.FromResult(Json(request.RequestUri!.AbsolutePath.StartsWith("/albums/", StringComparison.Ordinal)
            ? """{"result":{"id":"optional","title":false,"year":"2024","releaseDate":2024,"volumes":[]}}"""
            : """{"result":{"kind":"optional","owner":{"uid":"12345","name":12},"title":"Optional","revision":1,"trackCount":0,"tracks":[],"created":"03/04/2024","modified":true}}""")));
        using var optionalApi = new MusicApi(malformed);
        var emptyAlbum = await optionalApi.GetAlbumDetailsAsync(FakeSession, "optional", default);
        var emptyPlaylist = await optionalApi.GetPlaylistAsync(FakeSession, "12345", "optional", default);
        Assert(emptyAlbum is { Title: "", Year: null, ReleaseDate: null, CoverUri: null }
            && emptyPlaylist is { OwnerName: null, Created: null, Modified: null, CoverUri: null }, "optional fields were guessed from incompatible JSON types or ambiguous dates");
    }

    private static Task CollectionHeroOptionalPresentation()
    {
        var unknown = MainWindow.BuildAlbumHero(new("old", "", "", []), "Название из каталога");
        var unknownText = HeroVisuals<TextBlock>(unknown).ToArray();
        Assert(unknownText.Single(text => text.Name == "CollectionHeroTitle").Text == "Название из каталога"
            && unknownText.Single(text => text.Name == "CollectionHeroFacts").Text == "0 треков"
            && !unknownText.Any(text => text.Name is "CollectionHeroCreator" or "CollectionHeroDescription"), "unknown metadata created placeholder dates, duration, creator or description");
        var partial = MainWindow.BuildAlbumHero(new("partial", "Неполная длительность", "", [new TrackRow { DurationMs = 90000 }, new TrackRow()]));
        Assert(HeroVisuals<TextBlock>(partial).Single(text => text.Name == "CollectionHeroFacts").Text == "2 трека · не менее 1 мин", "partial duration was presented as a complete total");
        foreach (var (count, word) in new[] { (1, "трек"), (2, "трека"), (5, "треков"), (11, "треков"), (21, "трек"), (24, "трека") })
        {
            var hero = MainWindow.BuildAlbumHero(new("count", "Count", "", Enumerable.Range(0, count).Select(_ => new TrackRow()).ToArray(), Year: 2024));
            Assert(HeroVisuals<TextBlock>(hero).Single(text => text.Name == "CollectionHeroFacts").Text == $"2024 · {count} {word}", "Russian track count or year-only fallback differs");
        }
        var updated = MainWindow.BuildPlaylistHero(new("12345", "1", "Обновлённый", "", 1, [], Modified: new DateOnly(2024, 2, 29)));
        Assert(HeroVisuals<TextBlock>(updated).Single(text => text.Name == "CollectionHeroFacts").Text.StartsWith("Обновлён 29 февраля 2024 · ", StringComparison.Ordinal), "playlist modification was mislabeled as release/creation");
        return Task.CompletedTask;
    }

    private static async Task CollectionHeroInline()
    {
        using var fixture = new CollectionHeroFixture();
        var window = fixture.Window;
        await fixture.Activate();
        var initialRequests = fixture.Handler.Seen.Count;
        var sortControls = Control<StackPanel>(window, "ListSortControls");
        var sortParent = (Panel)sortControls.Parent;
        var sortIndex = sortParent.Children.IndexOf(sortControls);
        var sortColumn = Grid.GetColumn(sortControls);
        var sortMargin = sortControls.Margin;
        var downloadStatus = Control<TextBlock>(window, "PlaylistDownloadText");
        var downloadParent = (Panel)downloadStatus.Parent;
        var downloadIndex = downloadParent.Children.IndexOf(downloadStatus);
        var downloadMargin = downloadStatus.Margin;
        var back = Control<Button>(window, "BrowseBackButton");
        var backParent = (Panel)back.Parent;
        var backIndex = backParent.Children.IndexOf(back);
        var backMargin = back.Margin;
        var backPadding = back.Padding;
        var filter = Control<Grid>(window, "TrackFilterPanel");
        var filterParent = (Panel)filter.Parent;
        var filterIndex = filterParent.Children.IndexOf(filter);
        var filterMargin = filter.Margin;
        var filterWidth = filter.Width;
        var contentMargin = Control<Grid>(window, "ContentGrid").Margin;
        var comboWidth = Control<ComboBox>(window, "ListSortCombo").Width;
        var readback = new List<object>();
        foreach (var kind in new[] { "album", "playlist" })
        {
            var item = kind == "album" ? new CatalogItem("hero-album", "album", "Название из каталога", "")
                : new CatalogItem("hero-list", "playlist", "Название из каталога", "", "12345", "hero-list");
            var queue = (PlaybackQueue)typeof(MainWindow).GetField("_playbackQueue", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
            var queueVersion = queue.Version;
            await Call(window, "OpenCatalogAsync", item, true);
            var activeAlbum = typeof(MainWindow).GetField("_activeAlbumDetails", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window) as AlbumDetails;
            Assert(kind == "album" ? activeAlbum?.Id == item.Id : activeAlbum is null, "browse history retained the wrong album metadata");
            var host = Control<ContentControl>(window, "CollectionHero");
            Assert(host.Visibility == Visibility.Visible && host.Content is FrameworkElement && Control<TextBlock>(window, "CollectionTitle").Visibility == Visibility.Collapsed,
                "detail header was not rendered inline or duplicated the collection title");
            var surface = (FrameworkElement)host.Content;
            var image = HeroVisuals<Image>(surface).Single();
            await Until(() => image.Source is BitmapSource { IsFrozen: true });
            Assert(Artwork.GetSourceUri(image) == (kind == "album" ? HeroAlbumCover : HeroPlaylistCover), "detail header did not use the exact anonymous artwork pipeline");
            var text = HeroVisuals<TextBlock>(surface).ToArray();
            Assert(text.Single(block => block.Name == "CollectionHeroTitle").FontSize == 32
                && text.Single(block => block.Name == "CollectionHeroFacts").Text.Contains(kind == "album" ? "8 ноября 2024" : "Создан 15 апреля 2025", StringComparison.Ordinal), "large title or real service date missing");
            if (kind == "playlist") Assert(text.Single(block => block.Name == "CollectionHeroDescription").Text == "Музыка для неспешных прогулок у моря.", "playlist description omitted");
            foreach (var (width, height) in new[] { (1050, 680), (1280, 860) })
            {
                window.Width = width; window.Height = height; window.UpdateLayout(); await Task.Delay(180);
                var metrics = new { kind, width, height, headerHeight = surface.ActualHeight, viewportHeight = Control<ListView>(window, "TracksList").ActualHeight,
                    headerVisible = surface.IsVisible, toolbarVisible = Control<WrapPanel>(window, "CollectionToolbar").IsVisible,
                    sortVisible = sortControls.IsVisible, loginVisible = Control<Border>(window, "LoginPanel").IsVisible,
                    facts = text.Single(block => block.Name == "CollectionHeroFacts").Text };
                readback.Add(metrics);
                Console.WriteLine("INFO Collection hero layout: " + JsonSerializer.Serialize(metrics));
                File.WriteAllText(Path.Combine(_directory, "collection-hero-layout-progress.json"), JsonSerializer.Serialize(readback, new JsonSerializerOptions { WriteIndented = true }));
                App.Capture(window, Path.Combine(_directory, $"collection-hero-{kind}-{width}x{height}.png"));
                Assert(surface.IsVisible && surface.ActualHeight is >= 120 and <= 128 && image.ActualWidth == 120 && image.ActualHeight == 120,
                    "detail header expanded beyond its compact height or lost the cover dimensions");
                Assert(Control<ListView>(window, "TracksList").ActualHeight >= 250 && Control<WrapPanel>(window, "CollectionToolbar").IsVisible
                    && sortControls.IsVisible && ReferenceEquals(sortControls.Parent, Control<WrapPanel>(window, "CollectionToolbar"))
                    && Control<WrapPanel>(window, "CollectionSummary").Visibility == Visibility.Collapsed,
                    "detail header hid track viewport, actions or sort controls, or duplicated the summary");
                var title = text.Single(block => block.Name == "CollectionHeroTitle");
                var facts = text.Single(block => block.Name == "CollectionHeroFacts");
                var titleBounds = title.TransformToAncestor(surface).TransformBounds(new Rect(title.RenderSize));
                var factsBounds = facts.TransformToAncestor(surface).TransformBounds(new Rect(facts.RenderSize));
                Assert(titleBounds.Right <= surface.ActualWidth + 1 && factsBounds.Bottom <= surface.ActualHeight + 1, "title or informative line extends outside the header");
                var filterBounds = filter.TransformToAncestor(surface).TransformBounds(new Rect(filter.RenderSize));
                foreach (var block in text.Where(block => block.Name.StartsWith("CollectionHero", StringComparison.Ordinal)))
                {
                    var bounds = block.TransformToAncestor(surface).TransformBounds(new Rect(block.RenderSize));
                    bounds.Intersect(filterBounds);
                    Assert(bounds.IsEmpty || bounds.Width <= 1 || bounds.Height <= 1, "detail filter overlaps header metadata");
                }
            }
            Assert(queue.Version == queueVersion && !fixture.Player.HasSource, "opening the collection header changed the playback queue or started audio");
        }
        Control<Button>(window, "FavoritesNav").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert(Control<ContentControl>(window, "CollectionHero") is { Visibility: Visibility.Collapsed, Content: null }
            && Control<TextBlock>(window, "CollectionTitle").Visibility == Visibility.Visible, "leaving the detail retained its header/artwork or hid the ordinary title");
        Assert(ReferenceEquals(sortControls.Parent, sortParent) && sortParent.Children.IndexOf(sortControls) == sortIndex
            && Grid.GetColumn(sortControls) == sortColumn && sortControls.Margin == sortMargin
            && ReferenceEquals(downloadStatus.Parent, downloadParent) && downloadParent.Children.IndexOf(downloadStatus) == downloadIndex
            && downloadStatus.Margin == downloadMargin && Control<WrapPanel>(window, "CollectionSummary").Visibility == Visibility.Visible,
            "leaving the detail did not restore the exact sort/download controls, parent, index, attached column or margins");
        Assert(ReferenceEquals(back.Parent, backParent) && backParent.Children.IndexOf(back) == backIndex && back.Margin == backMargin && back.Padding == backPadding
            && ReferenceEquals(filter.Parent, filterParent) && filterParent.Children.IndexOf(filter) == filterIndex && filter.Margin == filterMargin && filter.Width.Equals(filterWidth)
            && Control<Grid>(window, "ContentGrid").Margin == contentMargin && Control<ComboBox>(window, "ListSortCombo").Width == comboWidth
            && typeof(MainWindow).GetField("_activeAlbumDetails", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window) is null,
            "leaving detail did not restore back/filter layout or clear album history metadata");
        Assert(fixture.Handler.Seen.Count == initialRequests + 2, "opening headers fetched extra API data");
        File.WriteAllText(Path.Combine(_directory, "collection-hero-readback.json"), JsonSerializer.Serialize(new
        { sizes = readback, exactServerArtwork = true, apiRequests = fixture.Handler.Seen.Count, mediaRequests = 0, externalNetworkRequests = 0, playbackQueueUnchanged = true, headerClearedOnNavigation = true }, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static IEnumerable<T> HeroVisuals<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T match) yield return match;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            foreach (var child in HeroVisuals<T>(VisualTreeHelper.GetChild(root, index))) yield return child;
    }

    private sealed class CollectionHeroFixture : IDisposable
    {
        internal MainWindow Window { get; }
        internal NativePlayer Player { get; }
        internal FakeHandler Handler { get; }
        private readonly object? _oldArtwork;
        private readonly ArtworkCache _artwork;
        internal CollectionHeroFixture()
        {
            var directory = Path.Combine(_directory, "collection-hero-fixture"); Directory.CreateDirectory(directory);
            _oldArtwork = typeof(Artwork).GetField("_cache", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null);
            var png = MediaCoverPng();
            _artwork = new ArtworkCache(Path.Combine(directory, "artwork"), new FakeHandler((request, _) =>
            {
                Assert(ArtworkUri.Valid(request.RequestUri!) && request.Headers.Authorization is null && !request.Headers.Contains("Cookie"), "header artwork sent credentials or left its CDN allowlist");
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(png) });
            }));
            Artwork.Cache = _artwork;
            Handler = new FakeHandler((request, _) =>
            {
                Assert(request.Method == HttpMethod.Get, "header caused a catalog write");
                return Task.FromResult(Json(request.RequestUri!.AbsolutePath switch
                {
                "/account/status" => Profile,
                "/account/about" => """{"result":{"uid":"12345"}}""",
                "/users/12345/likes/tracks" => """{"result":{"library":{"tracks":[]}}}""",
                "/albums/hero-album/with-tracks" => HeroAlbumJson,
                "/users/12345/playlists/hero-list" => """{"result":{"kind":"hero-list","owner":{"uid":"12345","name":"Александр"},"title":"Тихий вечер","description":"Музыка для неспешных прогулок у моря.","revision":8,"trackCount":0,"tracks":[],"created":"2025-04-15T23:30:00+10:00","cover":{"uri":"avatars.yandex.net/get-music-content/hero/playlist/%%"}}}""",
                _ => throw new InvalidOperationException("Unexpected collection hero fixture endpoint")
                }));
            });
            Player = new NativePlayer(new AudioBuffer(Path.Combine(directory, "buffer"), new FakeHandler((_, _) => throw new InvalidOperationException("header requested audio"))));
            var offline = new OfflineLibrary(Path.Combine(directory, "offline"), new AudioBuffer(Path.Combine(directory, "downloads"),
                new FakeHandler((_, _) => throw new InvalidOperationException("header downloaded audio"))));
            Window = new MainWindow(api: new MusicApi(Handler), store: new SessionStore(Path.Combine(directory, "session")), restore: false,
                diagnosticsDirectory: directory, offline: offline, player: Player, preferences: new PreferencesStore(Path.Combine(directory, "preferences")))
            { Width = 1050, Height = 680, Left = -16000, Top = 0, ShowActivated = false, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual };
            Window.Show();
            Control<Slider>(Window, "VolumeSlider").Value = 0;
        }
        internal Task Activate() => Call(Window, "ActivateAsync", FakeSession, CancellationToken.None);
        public void Dispose()
        {
            Window.Close(); Player.Dispose(); Handler.Dispose();
            typeof(Artwork).GetField("_cache", BindingFlags.Static | BindingFlags.NonPublic)!.SetValue(null, _oldArtwork);
            _artwork.Dispose();
        }
    }
}
