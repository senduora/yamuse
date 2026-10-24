using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using YMusic.Native;

internal static partial class Program
{
    private static async Task CheckCompactLayout()
    {
        await Run("Compact layout: exact account avatar, sidebar profile and signed-out actions at both sizes", ClProfile);
        await Run("Compact layout: playlist and favorites space, bounded toolbar and retained quality menu", ClCollections);
    }

    private static async Task ClProfile()
    {
        using var fixture = new ClFixture("profile");
        var window = fixture.Window;
        var readback = new List<object>();
        foreach (var (width, height) in ClSizes)
        {
            await ClResize(window, width, height);
            ClProfileBounds(window);
            Assert(Control<Button>(window, "LoginButton").IsVisible && Control<Button>(window, "LoginButton").IsEnabled
                && !Control<Button>(window, "LogoutButton").IsVisible && !Control<Button>(window, "RefreshButton").IsVisible,
                "signed-out profile actions are missing or overlap the account controls");
            App.Capture(window, Path.Combine(_directory, $"compact-signedout-{width}x{height}.png"));
        }
        await fixture.Activate();
        var avatar = Control<Image>(window, "ProfileAvatar");
        await Until(() => avatar.Source is BitmapSource { IsFrozen: true });
        Assert(fixture.Handler.Seen.Count(r => r.Uri.AbsolutePath == "/account/status") == 1
            && fixture.Handler.Seen.Count(r => r.Uri.AbsolutePath == "/account/about") == 1,
            "profile did not read the exact authenticated about response once");
        var account = Control<TextBlock>(window, "AccountText").DataContext as Account;
        Assert(account is { Uid: "12345", AvatarUri: ClAvatar } && Artwork.GetSourceUri(avatar) == ClAvatar,
            "avatar binding did not retain the server portrait for the confirmed account UID");
        Assert(avatar.Clip is EllipseGeometry { RadiusX: 16, RadiusY: 16 } && fixture.ArtworkRequests == 1,
            "profile avatar is not a single frozen round artwork rendition");
        foreach (var (width, height) in ClSizes)
        {
            await ClResize(window, width, height); ClProfileBounds(window);
            var refresh = Control<Button>(window, "RefreshButton"); var logout = Control<Button>(window, "LogoutButton");
            Assert(refresh.IsVisible && refresh.IsEnabled && logout.IsVisible && logout.IsEnabled
                && !Control<Button>(window, "LoginButton").IsVisible, "signed-in profile icons are not usable");
            Assert(AutomationProperties.GetName(refresh) == "Обновить раздел" && AutomationProperties.GetName(logout) == "Выйти из аккаунта",
                "profile icon actions lost their accessible names");
            Assert(!ClIntersects(ClRect(refresh, window), ClRect(logout, window)), "refresh and logout icons overlap");
            readback.Add(new { width, height, profile = ClRect(Control<Border>(window, "ProfileCard"), window), avatar = ClRect(avatar, window), accountUidMatches = true, artworkFrozen = true });
            App.Capture(window, Path.Combine(_directory, $"compact-profile-{width}x{height}.png"));
        }
        var before = fixture.LibraryRequests;
        ClClick(Control<Button>(window, "RefreshButton"));
        await Until(() => fixture.LibraryRequests > before && ClField<CancellationTokenSource?>(window, "_libraryCts") is null);
        Assert(fixture.MediaRequests == 0 && !fixture.Player.HasSource, "profile refresh opened audio or downloaded music");
        ClClick(Control<Button>(window, "LogoutButton"));
        await Until(() => Control<TextBlock>(window, "AccountText").DataContext is null && avatar.Source is null);
        Assert(Control<Button>(window, "LoginButton").IsVisible && Control<Button>(window, "LoginButton").IsEnabled
            && Artwork.GetSourceUri(avatar) is null && !Control<Button>(window, "LogoutButton").IsVisible,
            "logout retained the previous avatar or hid the login action");
        window.UpdateLayout();
        ClProfileBounds(window);
        File.WriteAllText(Path.Combine(_directory, "compact-profile-readback.json"), JsonSerializer.Serialize(new
        { sizes = readback, exactUid = true, apiAvatarFallback = "account/about", artworkFixtureRequests = fixture.ArtworkRequests, mediaRequests = fixture.MediaRequests, externalNetworkRequests = 0, refreshUsesProductionClick = true, logoutUsesProductionClick = true }, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static async Task ClCollections()
    {
        using var fixture = new ClFixture("collections");
        var window = fixture.Window; await fixture.Activate();
        await Until(() => Control<Image>(window, "ProfileAvatar").Source is BitmapSource { IsFrozen: true });
        var rows = Enumerable.Range(0, 70).Select(i => new TrackRow
        { Number = i + 1, Id = "compact-playlist-" + i, RequestId = "compact-playlist-" + i + ":1", Title = "Трек плейлиста " + (i + 1), Artist = "Исполнитель", Album = "Альбом", Available = true, DurationMs = 180000 }).ToArray();
        var playlist = new PlaylistDetails("12345", "compact-own", "Плейлист для проверки компактного интерфейса", "", 3, rows);
        var readback = new List<object>();
        foreach (var mode in new[] { "favorites", "playlist" })
        {
            if (mode == "favorites") ClClick(Control<Button>(window, "FavoritesNav"));
            else ClInvoke(window, "ShowPlaylist", playlist);
            Control<ListView>(window, "TracksList").SelectedIndex = 0;
            foreach (var (width, height) in ClSizes)
            {
                await ClResize(window, width, height);
                var list = Control<ListView>(window, "TracksList");
                var title = mode == "playlist" ? ClVisuals<TextBlock>(Control<ContentControl>(window, "CollectionHero")).Single(text => text.Name == "CollectionHeroTitle") : Control<TextBlock>(window, "CollectionTitle");
                Assert(list.IsVisible && list.ActualHeight >= 250, $"{mode} list has insufficient visible height at {width}x{height}: {list.ActualHeight:0.##}");
                Assert(title.IsVisible && ClRect(title, window).Top < ClRect(Control<WrapPanel>(window, "CollectionToolbar"), window).Top
                    && ClRect(title, window).Top < ClRect(list, window).Top, "collection title moved below its toolbar or list");
                if (mode == "playlist") Assert(title.Text == playlist.Title, "playlist title was replaced with an action label");
                var toolbar = Control<WrapPanel>(window, "CollectionToolbar");
                var controls = ClVisuals<Button>(toolbar).Where(button => button.IsVisible).Cast<FrameworkElement>()
                    .Concat(ClVisuals<CheckBox>(toolbar).Where(check => check.IsVisible)).ToArray();
                Assert(controls.Length >= 4 && Control<Button>(window, "PlaySelectionButton").IsVisible
                    && Control<Button>(window, "DownloadPlaylistButton").IsVisible && Control<Button>(window, "CollectionOptionsButton").IsVisible,
                    "compact collection toolbar lost a required primary action");
                foreach (var control in controls) ClWithin(control, toolbar, "collection toolbar action");
                for (var i = 0; i < controls.Length; i++) for (var j = i + 1; j < controls.Length; j++)
                    Assert(!ClIntersects(ClRect(controls[i], toolbar), ClRect(controls[j], toolbar)), "collection toolbar actions overlap");
                if (mode == "favorites") Assert(Control<Button>(window, "FavoritesWaveButton").IsVisible
                    && Control<CheckBox>(window, "AutoFavoritesCheckBox").IsVisible && Control<CheckBox>(window, "AutoFavoritesCheckBox").Content.ToString() == "Автоскачивание",
                    "favorites wave or compact auto-download control is missing");
                ClProfileBounds(window);
                readback.Add(new { mode, width, height, listHeight = list.ActualHeight, title = title.Text, toolbarHeight = toolbar.ActualHeight,
                    actions = controls.Select(control => new { control.Name, bounds = ClRect(control, toolbar) }) });
                App.Capture(window, Path.Combine(_directory, $"compact-{mode}-{width}x{height}.png"));
            }
        }

        var quality = Control<ComboBox>(window, "QualityCombo"); quality.SelectedIndex = 2;
        var popup = Control<Popup>(window, "CollectionOptionsPopup"); var options = Control<Button>(window, "CollectionOptionsButton");
        Assert(!popup.IsOpen, "quality popup starts open");
        ClClick(options); await Until(() => popup.IsOpen && popup.Child is FrameworkElement { IsVisible: true });
        Assert(quality.SelectedIndex == 2 && quality.Items.OfType<ComboBoxItem>().Select(item => item.Content.ToString())
            .SequenceEqual(new[] { "Превосходное", "Оптимальное", "Экономичное" }), "quality popup changed the current selection or its three supported modes");
        Assert(Control<Button>(window, "RenamePlaylistButton").IsVisible && Control<Button>(window, "RemovePlaylistTrackButton").IsVisible
            && Control<Button>(window, "DeletePlaylistButton").IsVisible && ClVisuals<Button>(popup.Child).Any(button => button.Content.ToString() == "Открыть папку скачанного"),
            "own-playlist actions or the existing download-folder action are missing from options");
        quality.SelectedIndex = 1; ClClick(options); Assert(!popup.IsOpen, "options button did not close its popup");
        ClClick(options); await Until(() => popup.IsOpen);
        Assert(quality.SelectedIndex == 1 && ClProperty<AudioQuality>(window, "SelectedQuality") == AudioQuality.Optimal,
            "reopening options reset the selected production audio quality");
        if (popup.Child is FrameworkElement content)
        {
            content.UpdateLayout(); ClCaptureElement(content, Path.Combine(_directory, "compact-quality-popup.png"));
            foreach (var control in ClVisuals<Button>(content).Where(button => button.IsVisible)) ClWithin(control, content, "popup action");
        }
        ClClick(options);
        Assert(fixture.MediaRequests == 0 && !fixture.Player.HasSource && !ClField<bool>(window, "_autoFavoritesEnabled"),
            "layout/options checks started media, a download, or automatic favorites");
        File.WriteAllText(Path.Combine(_directory, "compact-collections-readback.json"), JsonSerializer.Serialize(new
        { sizes = readback, qualityLabels = quality.Items.OfType<ComboBoxItem>().Select(item => item.Content.ToString()), selectedQuality = "Optimal", selectedIndexRetained = quality.SelectedIndex, externalNetworkRequests = 0, mediaRequests = fixture.MediaRequests,
            checkedAssemblySha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(MainWindow).Assembly.Location))) }, new JsonSerializerOptions { WriteIndented = true }));
    }

    private const string ClAvatar = "https://avatars.mds.yandex.net/get-yapic/54321/local-avatar/islands-retina-middle";
    private static readonly (int Width, int Height)[] ClSizes = [(1050, 680), (1280, 860)];
    private static void ClClick(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static T ClField<T>(MainWindow window, string name) => (T)typeof(MainWindow).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!;
    private static T ClProperty<T>(MainWindow window, string name) => (T)typeof(MainWindow).GetProperty(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!;
    private static void ClInvoke(MainWindow window, string name, params object[] args) => typeof(MainWindow).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, args);
    private static async Task ClResize(MainWindow window, int width, int height)
    { window.Width = width; window.Height = height; window.UpdateLayout(); await Task.Delay(290); }
    private static Rect ClRect(FrameworkElement element, Visual ancestor) => element.TransformToAncestor(ancestor).TransformBounds(new Rect(element.RenderSize));
    private static bool ClIntersects(Rect a, Rect b) { a.Intersect(b); return !a.IsEmpty && a.Width > 1 && a.Height > 1; }
    private static void ClWithin(FrameworkElement element, FrameworkElement ancestor, string description)
    {
        var rect = ClRect(element, ancestor);
        Assert(rect.Width > 0 && rect.Height > 0 && rect.Left >= -1 && rect.Top >= -1 && rect.Right <= ancestor.ActualWidth + 1 && rect.Bottom <= ancestor.ActualHeight + 1,
            description + " lies outside its allocated area: " + element.Name + " " + rect);
    }
    private static void ClProfileBounds(MainWindow window)
    {
        var profile = Control<Border>(window, "ProfileCard"); var sidebar = Control<Border>(window, "Sidebar");
        ClWithin(profile, sidebar, "sidebar profile");
        var avatar = Control<Image>(window, "ProfileAvatar");
        var placeholder = ClVisuals<Border>(profile).Single(border => border.Width == 32 && border.Height == 32);
        Assert(placeholder.IsVisible, "signed-out profile avatar placeholder is hidden");
        ClWithin(placeholder, profile, "profile avatar placeholder");
        if (avatar.Source is not null) ClWithin(avatar, profile, "loaded profile avatar");
        ClWithin(Control<TextBlock>(window, "AccountText"), profile, "profile account text");
        foreach (var name in new[] { "LoginButton", "RefreshButton", "LogoutButton" })
        { var button = Control<Button>(window, name); if (button.IsVisible) ClWithin(button, profile, "profile action"); }
        Assert(ClRect(profile, window).Bottom <= ClRect(sidebar, window).Bottom + 1, "profile extends into the player row");
    }
    private static IEnumerable<T> ClVisuals<T>(DependencyObject root) where T : DependencyObject
    { if (root is T typed) yield return typed; for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) foreach (var child in ClVisuals<T>(VisualTreeHelper.GetChild(root, i))) yield return child; }
    private static void ClCaptureElement(FrameworkElement element, string path)
    {
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth), (int)Math.Ceiling(element.ActualHeight), 96, 96, PixelFormats.Pbgra32); bitmap.Render(element);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var file = File.Create(path); encoder.Save(file);
    }

    private sealed class ClFixture : IDisposable
    {
        internal MainWindow Window { get; }
        internal NativePlayer Player { get; }
        internal FakeHandler Handler { get; }
        internal int LibraryRequests, ArtworkRequests, MediaRequests;
        private readonly object? _oldArtwork;
        private readonly ArtworkCache _artwork;
        internal ClFixture(string name)
        {
            var directory = Path.Combine(_directory, "compact-" + name); Directory.CreateDirectory(directory);
            _oldArtwork = typeof(Artwork).GetField("_cache", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null);
            var png = MediaCoverPng();
            _artwork = new ArtworkCache(Path.Combine(directory, "artwork"), new FakeHandler((request, _) =>
            {
                Assert(request.RequestUri!.AbsoluteUri == ClAvatar && request.Headers.Authorization is null && !request.Headers.Contains("Cookie"), "profile artwork escaped the exact anonymous avatar contract");
                Interlocked.Increment(ref ArtworkRequests);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(png) });
            }));
            Artwork.Cache = _artwork;
            Handler = new FakeHandler(Respond);
            var media = new FakeHandler((_, _) => { Interlocked.Increment(ref MediaRequests); throw new InvalidOperationException("compact layout requested audio"); });
            Player = new NativePlayer(new AudioBuffer(Path.Combine(directory, "audio"), media));
            var offline = new OfflineLibrary(Path.Combine(directory, "offline"), new AudioBuffer(Path.Combine(directory, "downloads"), new FakeHandler((_, _) => { Interlocked.Increment(ref MediaRequests); throw new InvalidOperationException("compact layout downloaded audio"); })));
            Window = new(api: new MusicApi(Handler), store: new SessionStore(Path.Combine(directory, "session")), restore: false, diagnosticsDirectory: directory, offline: offline, player: Player)
            { Width = 1050, Height = 680, Left = -16000, Top = 0, ShowActivated = false, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual };
            Window.Show(); Control<Slider>(Window, "VolumeSlider").Value = 0;
        }
        internal Task Activate() => Call(Window, "ActivateAsync", FakeSession, CancellationToken.None);
        private async Task<HttpResponseMessage> Respond(HttpRequestMessage request, CancellationToken token)
        {
            var path = request.RequestUri!.AbsolutePath;
            Assert(request.RequestUri.Host == "api.music.yandex.net", "compact profile API left its fake API host");
            if (path == "/account/status") return Json(Profile);
            if (path == "/account/about") return Json("""{"result":{"uid":"12345","avatarId":"54321/local-avatar"}}""");
            if (path == "/users/12345/likes/tracks")
            {
                Interlocked.Increment(ref LibraryRequests);
                return Json(JsonSerializer.Serialize(new { result = new { library = new { tracks = Enumerable.Range(0, 30).Select(i => new { id = "compact-" + i, albumId = "1" }) } } }));
            }
            if (path == "/tracks")
            {
                var body = await request.Content!.ReadAsStringAsync(token); var ids = Uri.UnescapeDataString(body.Split('&')[0].Split('=')[1]).Split(',').Select(value => value.Split(':')[0]);
                return Json(JsonSerializer.Serialize(new { result = ids.Select(id => new { id, title = "Локальный трек " + id, available = true, durationMs = 180000, artists = new[] { new { name = "Исполнитель" } }, albums = new[] { new { id = "1", title = "Альбом" } } }) }));
            }
            throw new InvalidOperationException("Unexpected compact layout fixture endpoint " + path);
        }
        public void Dispose()
        { Window.Close(); Player.Dispose(); Handler.Dispose(); typeof(Artwork).GetField("_cache", BindingFlags.NonPublic | BindingFlags.Static)!.SetValue(null, _oldArtwork); _artwork.Dispose(); }
    }
}
