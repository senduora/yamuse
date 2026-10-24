using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using YMusic.Native;

internal static partial class Program
{
    private static async Task CheckMediaUi()
    {
        var originalCache = typeof(Artwork).GetField("_cache", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null);
        var artworkRequests = 0;
        var png = MediaCoverPng();
        using var artwork = new ArtworkCache(Path.Combine(_directory, "media-ui-artwork"), new FakeHandler((request, _) =>
        {
            Assert(ArtworkUri.Valid(request.RequestUri!) && request.Headers.Authorization is null && !request.Headers.Contains("Cookie"), "artwork request left anonymous CDN contract");
            Interlocked.Increment(ref artworkRequests);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(png) });
        }));
        Artwork.Cache = artwork;
        try
        {
            await Run("Media UI: handled rail events commit native seek, pause, rapid changes, keyboard and canceled gestures", CheckMediaSeek);
            await Run("Media UI: player heart mutates current track, reads server state and survives a new window", CheckMediaHeart);
            await Run("Media UI: XAML row/player covers resolve frozen images and offline metadata survives restart", CheckMediaCovers);
            if (Results.Any(result => result.ToString()!.Contains("Media UI:")))
                File.WriteAllText(Path.Combine(_directory, "media-ui-network.json"), JsonSerializer.Serialize(new
                { externalNetworkRequests = 0, nativeMediaNetworkRequests = 0, artworkFixtureRequests = artworkRequests, profileRead = false, autoFavoritesEnabled = false }, new JsonSerializerOptions { WriteIndented = true }));
        }
        finally { typeof(Artwork).GetField("_cache", BindingFlags.NonPublic | BindingFlags.Static)!.SetValue(null, originalCache); }
    }

    private static async Task CheckMediaSeek()
    {
        var directory = MediaDirectory("seek");
        var noNetwork = new FakeHandler((_, _) => throw new InvalidOperationException("native seek attempted network"));
        var player = new NativePlayer(new AudioBuffer(Path.Combine(directory, "buffer"), noNetwork));
        var offline = new OfflineLibrary(Path.Combine(directory, "offline"), new AudioBuffer(Path.Combine(directory, "downloads"), noNetwork));
        var window = MediaWindow(directory, noNetwork, player, offline);
        var readback = new List<object>();
        try
        {
            window.Show(); Control<Slider>(window, "VolumeSlider").Value = 0;
            await window.PlayTrackAsync(new TrackRow { Id = "seek-fixture", RequestId = "seek-fixture", Title = "Native seek fixture", DurationMs = 30000, Available = true, LocalPath = WriteWave("media-ui-seek-30s.wav", 30) });
            Assert(player.IsPlaying && player.HasSource && Math.Abs(player.Duration.TotalSeconds - 30) < .2, "30-second native fixture did not open");
            var slider = Control<Slider>(window, "SeekSlider");
            Assert(slider.IsEnabled, "seek slider disabled for open native media");
            foreach (var target in new[] { 20d, 4d, 26d })
            {
                MediaHandledDown(slider);
                Assert(MediaField<bool>(window, "_seekDragging"), "handled rail down did not start seeking through registered event handler");
                slider.Value = target;
                MediaInvoke(window, "UpdateProgress");
                Assert(Math.Abs(slider.Value - target) < .001 && MediaTime(window) == MediaFormat(target), "progress tick overwrote active gesture");
                MediaHandledUp(slider);
                Assert(!MediaField<bool>(window, "_seekDragging"), "handled rail up left seeking active");
                MediaInvoke(window, "UpdateProgress");
                Assert(Math.Abs(slider.Value - target) < .8 && MediaTime(window) == MediaFormat(target), "immediate post-commit UI snapped back");
                await Until(() => Math.Abs(player.Position.TotalSeconds - target) < .8);
                readback.Add(new { gesture = "handled rail", targetSeconds = target, nativeSeconds = player.Position.TotalSeconds, display = MediaTime(window) });
            }
            await Call(window, "TogglePlaybackAsync");
            Assert(!player.IsPlaying, "fixture did not pause");
            MediaCommit(slider, 12);
            await Until(() => Math.Abs(player.Position.TotalSeconds - 12) < .8);
            await Task.Delay(150);
            Assert(!player.IsPlaying && Math.Abs(player.Position.TotalSeconds - 12) < .8, "paused seek resumed playback or moved to wrong native position");
            readback.Add(new { gesture = "paused", targetSeconds = 12, nativeSeconds = player.Position.TotalSeconds, display = MediaTime(window) });
            foreach (var target in new[] { 6d, 18d, 3d, 22d, 9d }) MediaCommit(slider, target);
            MediaInvoke(window, "UpdateProgress");
            Assert(Math.Abs(slider.Value - 9) < .8 && MediaTime(window) == "0:09", "rapid commits did not keep latest display target");
            await Until(() => Math.Abs(player.Position.TotalSeconds - 9) < .8);
            readback.Add(new { gesture = "rapid latest", targetSeconds = 9, nativeSeconds = player.Position.TotalSeconds, display = MediaTime(window) });

            MediaHandledDown(slider); slider.Value = 23;
            slider.RaiseEvent(new DragCompletedEventArgs(0, 0, true) { RoutedEvent = Thumb.DragCompletedEvent, Handled = true });
            Assert(!MediaField<bool>(window, "_seekDragging") && Math.Abs(player.Position.TotalSeconds - 9) < .8, "canceled drag committed or left seeking active");
            MediaHandledDown(slider); slider.Value = 24;
            slider.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount) { RoutedEvent = Mouse.LostMouseCaptureEvent, Handled = true });
            Assert(!MediaField<bool>(window, "_seekDragging") && Math.Abs(player.Position.TotalSeconds - 9) < .8, "lost capture committed or left seeking active");

            foreach (var (key, target) in new[] { (Key.Right, 10d), (Key.Home, 0d), (Key.End, 30d) })
            {
                var source = PresentationSource.FromVisual(window)!;
                slider.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, key) { RoutedEvent = Keyboard.KeyDownEvent });
                Assert(Math.Abs(slider.Value - target) < .8, "native Slider keyboard routing did not update value: " + key);
                slider.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, key) { RoutedEvent = Keyboard.PreviewKeyUpEvent });
                await Until(() => Math.Abs(player.Position.TotalSeconds - target) < .8);
                Assert(!player.IsPlaying, "keyboard seek resumed paused media");
                readback.Add(new { gesture = key.ToString(), targetSeconds = target, nativeSeconds = player.Position.TotalSeconds, display = MediaTime(window) });
            }
            Assert(noNetwork.Seen.Count == 0, "local native playback requested network");
            File.WriteAllText(Path.Combine(directory, "native-readback.json"), JsonSerializer.Serialize(new { toleranceSeconds = .8, readback, networkRequests = 0 }, new JsonSerializerOptions { WriteIndented = true }));
        }
        finally { window.Close(); }
    }

    private static async Task CheckMediaHeart()
    {
        var directory = MediaDirectory("heart");
        var fixture = new MediaFixture();
        var noNetwork = new FakeHandler((_, _) => throw new InvalidOperationException("player heart attempted audio network"));
        var path = WriteWave("media-ui-heart-30s.wav", 30);
        async Task CheckWindow(bool mutate)
        {
            var player = new NativePlayer(new AudioBuffer(Path.Combine(directory, mutate ? "buffer-first" : "buffer-new"), noNetwork));
            var offline = new OfflineLibrary(Path.Combine(directory, "offline"), new AudioBuffer(Path.Combine(directory, "downloads"), noNetwork));
            var window = MediaWindow(directory, fixture.Handler, player, offline);
            try
            {
                window.Show(); Control<Slider>(window, "VolumeSlider").Value = 0;
                await Call(window, "ActivateAsync", FakeSession, CancellationToken.None);
                var metadata = Control<ListView>(window, "TracksList").Items.Cast<TrackRow>().Single(row => row.Id == "900");
                Assert(metadata.RequestId == "900:91", "current track album context missing");
                var current = MediaLocalRow(metadata, path);
                var selected = new TrackRow { Id = "901", RequestId = "901:91", Title = "Selected row 901", DurationMs = 30000, Available = true, LocalPath = path };
                MediaInvoke(window, "ShowPlaylist", new PlaylistDetails("12345", "42", "Heart fixture", "", 7, [current, selected]));
                await window.PlayTrackAsync(current);
                var list = Control<ListView>(window, "TracksList"); list.SelectedItem = selected;
                var heart = Control<Button>(window, "PlayerLikeButton");
                Assert(heart.IsEnabled && heart.Content.ToString() == "\uEB52", "server-like state not reflected in filled player heart");
                Assert(((TrackRow)list.SelectedItem).Id == "901" && MediaField<TrackRow>(window, "_current").Id == "900", "fixture did not separate selection and current playback");
                if (!mutate) return;
                heart.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Until(() => fixture.Writes.Count == 1 && heart.IsEnabled && heart.Content.ToString() == "\uEB51" && MediaField<object?>(window, "_libraryCts") is null);
                Assert(!fixture.Favorites.Contains("900") && fixture.Writes[0] == ("900:91", false), "player heart unliked selected row or wrong composite track");
                heart.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Until(() => fixture.Writes.Count == 2 && heart.IsEnabled && heart.Content.ToString() == "\uEB52" && MediaField<object?>(window, "_libraryCts") is null);
                Assert(fixture.Favorites.Contains("900") && fixture.Writes[1] == ("900:91", true), "player heart failed exact current-track like/readback");
                Assert(fixture.Writes.All(write => write.Id == "900:91") && !fixture.Favorites.Contains("901"), "selected non-playing row was modified");
            }
            finally { window.Close(); }
        }
        await CheckWindow(true);
        await CheckWindow(false);
        Assert(fixture.Writes.Count == 2 && noNetwork.Seen.Count == 0, "restart wrote extra likes or requested audio network");
        File.WriteAllText(Path.Combine(directory, "heart-readback.json"), JsonSerializer.Serialize(new { current = "900:91", selected = "901:91", writes = fixture.Writes.Select(write => new { id = write.Id, liked = write.Liked }), persistedServerLike = fixture.Favorites.Contains("900"), externalNetworkRequests = 0 }, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static async Task CheckMediaCovers()
    {
        var directory = MediaDirectory("covers");
        var fixture = new MediaFixture();
        fixture.Favorites.UnionWith(Enumerable.Range(901, 7).Select(id => id.ToString(CultureInfo.InvariantCulture)));
        using var metadataApi = new MusicApi(fixture.Handler);
        var metadata = (await metadataApi.GetTracksAsync(FakeSession, [new("900", "91")], 0, default)).Single();
        Assert(metadata.CoverUri is not null, "fake API cover metadata missing");
        var media = File.ReadAllBytes(WriteWave("media-ui-cover-30s.wav", 30));
        var audioFixture = new FakeHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(media) }));
        var libraryDirectory = Path.Combine(directory, "offline");
        using (var writer = new OfflineLibrary(libraryDirectory, new AudioBuffer(Path.Combine(directory, "writer-buffer"), audioFixture)))
        {
            await writer.SaveAsync(metadata, new AudioDescriptor(new Uri("https://s1.vlx.yandex.net/fixture.mp3"), "mp3", 192), default);
            await writer.RememberPlaylistAsync(new PlaylistDetails("12345", "42", "Cover fixture", "", 7, [metadata]), AudioQuality.Optimal, default);
        }
        var noNetwork = new FakeHandler((_, _) => throw new InvalidOperationException("cover fixture local playback attempted network"));
        var reopened = new OfflineLibrary(libraryDirectory, new AudioBuffer(Path.Combine(directory, "reader-buffer"), noNetwork));
        Assert(reopened.Read().Single().CoverUri == metadata.CoverUri && reopened.Tracks().Single().CoverUri == metadata.CoverUri
            && reopened.GetPlaylistTracks("12345", "42").Single().CoverUri == metadata.CoverUri, "offline file or playlist snapshot lost CoverUri across restart");
        var player = new NativePlayer(new AudioBuffer(Path.Combine(directory, "native-buffer"), noNetwork));
        var window = MediaWindow(directory, fixture.Handler, player, reopened);
        try
        {
            window.Width = 1280; window.Height = 860; window.Show();
            Control<Slider>(window, "VolumeSlider").Value = 0;
            await Call(window, "ActivateAsync", FakeSession, CancellationToken.None);
            var list = Control<ListView>(window, "TracksList");
            var row = list.Items.Cast<TrackRow>().Single(track => track.Id == "900");
            list.SelectedItem = row; list.ScrollIntoView(row); window.UpdateLayout();
            await Until(() => MediaVisuals<Image>(list).Any(image => image.DataContext is TrackRow track && track.Id == row.Id && image.Source is BitmapSource { IsFrozen: true }));
            var rowImage = MediaVisuals<Image>(list).First(image => image.DataContext is TrackRow track && track.Id == row.Id && image.Source is BitmapSource { IsFrozen: true });
            Assert(Artwork.GetSourceUri(rowImage) == row.CoverUri && Artwork.GetPixelSize(rowImage) == 64, "row XAML cover binding is incorrect");
            await window.PlayTrackAsync(row);
            var cover = Control<Image>(window, "PlayerCover");
            await Until(() => cover.Source is BitmapSource { IsFrozen: true });
            Assert(Artwork.GetSourceUri(cover) == row.CoverUri && Artwork.GetPixelSize(cover) == 256, "player cover did not use current track metadata");
            Assert(player.IsPlaying && player.SourceLocalPath is not null && noNetwork.Seen.Count == 0, "downloaded cover row did not play from local file");
            var heart = Control<Button>(window, "PlayerLikeButton");
            var playlist = Control<Button>(window, "DownloadPlaylistButton");
            var auto = Control<CheckBox>(window, "AutoFavoritesCheckBox");
            Assert(heart.Content.ToString() == "\uEB52" && heart.IsEnabled && playlist.IsVisible && playlist.IsEnabled && auto.IsVisible && auto.IsChecked == false, "required heart/whole-playlist/auto controls not visible with safe defaults");
            Control<TextBlock>(window, "ModeLabel").Text = "ПРОВЕРКА · ЛОКАЛЬНЫЕ ДАННЫЕ · БЕЗ СЕТИ";
            await Task.Delay(100); App.Capture(window, Path.Combine(_directory, "media-ui-1280x860.png"));
            window.Width = 1050; window.Height = 680; window.UpdateLayout(); await Task.Delay(150);
            Assert(rowImage.IsVisible && cover.IsVisible && heart.IsVisible && playlist.IsVisible && auto.IsVisible, "required media UI disappeared at minimum size");
            App.Capture(window, Path.Combine(_directory, "media-ui-1050x680.png"));
            Assert(audioFixture.Seen.Count == 1 && noNetwork.Seen.Count == 0 && !MediaField<bool>(window, "_autoFavoritesEnabled"), "cover verification triggered extra downloads or auto favorites");
            File.WriteAllText(Path.Combine(directory, "cover-readback.json"), JsonSerializer.Serialize(new { rowBound = true, playerBound = true, frozen = true, offlineIndexCover = true, playlistSnapshotCover = true, nativeMediaNetworkRequests = 0, externalNetworkRequests = 0, fixtureDownloadRequests = audioFixture.Seen.Count, screenshotSizes = new[] { "1280x860", "1050x680" } }, new JsonSerializerOptions { WriteIndented = true }));
        }
        finally { window.Close(); }
    }

    private static string MediaDirectory(string name)
    { var path = Path.Combine(_directory, "media-ui-" + name); Directory.CreateDirectory(path); return path; }
    private static MainWindow MediaWindow(string directory, FakeHandler api, NativePlayer player, OfflineLibrary offline)
        => new(api: new MusicApi(api), store: new SessionStore(Path.Combine(directory, "session")), restore: false, diagnosticsDirectory: directory, offline: offline, player: player);
    private static T MediaField<T>(MainWindow window, string name) => (T)typeof(MainWindow).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!;
    private static void MediaInvoke(MainWindow window, string name, params object[] arguments)
        => typeof(MainWindow).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, arguments);
    private static string MediaTime(MainWindow window) => Control<TextBlock>(window, "PositionText").Text;
    private static string MediaFormat(double seconds) => TimeSpan.FromSeconds(seconds).ToString(@"m\:ss", CultureInfo.InvariantCulture);
    private static void MediaHandledDown(Slider slider) => slider.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = UIElement.PreviewMouseLeftButtonDownEvent, Handled = true });
    private static void MediaHandledUp(Slider slider) => slider.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = UIElement.PreviewMouseLeftButtonUpEvent, Handled = true });
    private static void MediaCommit(Slider slider, double seconds) { MediaHandledDown(slider); slider.Value = seconds; MediaHandledUp(slider); }
    private static TrackRow MediaLocalRow(TrackRow source, string path) => new()
    { Number = source.Number, Id = source.Id, RequestId = source.RequestId, Title = source.Title, Artist = source.Artist, Album = source.Album, DurationMs = source.DurationMs, Available = source.Available, CoverUri = source.CoverUri, LocalPath = path };
    private static IEnumerable<T> MediaVisuals<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T typed) yield return typed;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in MediaVisuals<T>(VisualTreeHelper.GetChild(root, i))) yield return child;
    }
    private static byte[] MediaCoverPng()
    {
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            drawing.DrawRectangle(new LinearGradientBrush(Color.FromRgb(255, 136, 118), Color.FromRgb(74, 68, 135), 55), null, new Rect(0, 0, 64, 64));
            drawing.DrawEllipse(new SolidColorBrush(Color.FromArgb(100, 255, 220, 178)), null, new Point(48, 18), 26, 26);
            drawing.DrawEllipse(new SolidColorBrush(Color.FromArgb(150, 43, 48, 86)), null, new Point(14, 55), 27, 27);
            drawing.DrawEllipse(Brushes.White, null, new Point(32, 32), 10, 10);
            drawing.DrawEllipse(new SolidColorBrush(Color.FromRgb(190, 104, 119)), null, new Point(32, 32), 3, 3);
        }
        var bitmap = new RenderTargetBitmap(64, 64, 96, 96, PixelFormats.Pbgra32); bitmap.Render(visual);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var stream = new MemoryStream(); encoder.Save(stream); return stream.ToArray();
    }
    private sealed class MediaFixture
    {
        internal HashSet<string> Favorites { get; } = new(StringComparer.Ordinal) { "900" };
        internal List<(string Id, bool Liked)> Writes { get; } = [];
        internal FakeHandler Handler { get; }
        internal MediaFixture()
        {
            Handler = new FakeHandler(async (request, token) =>
            {
                Assert(request.RequestUri!.Host == "api.music.yandex.net", "media fixture attempted non-API host");
                var path = request.RequestUri.AbsolutePath;
                if (path == "/account/status") return Json(Profile);
                if (request.Method == HttpMethod.Get && path == "/users/12345/likes/tracks")
                    return Json(JsonSerializer.Serialize(new { result = new { library = new { tracks = Favorites.OrderBy(id => id, StringComparer.Ordinal).Select(id => new { id, albumId = "91" }).ToArray() } } }));
                if (request.Method == HttpMethod.Post && path is "/users/12345/likes/tracks/remove" or "/users/12345/likes/tracks/add-multiple")
                {
                    var body = Uri.UnescapeDataString(await request.Content!.ReadAsStringAsync(token));
                    var id = body.Split('&').Single(part => part.StartsWith("track-ids=", StringComparison.Ordinal))[10..];
                    var liked = path.EndsWith("/add-multiple", StringComparison.Ordinal);
                    Writes.Add((id, liked));
                    if (liked) Favorites.Add(id.Split(':')[0]); else Favorites.Remove(id.Split(':')[0]);
                    return Json(JsonSerializer.Serialize(new { result = new { revision = Writes.Count } }));
                }
                if (request.Method == HttpMethod.Post && path == "/tracks")
                {
                    var body = Uri.UnescapeDataString(await request.Content!.ReadAsStringAsync(token));
                    var ids = body.Split('&').Single(part => part.StartsWith("track-ids=", StringComparison.Ordinal))[10..].Split(',').Select(id => id.Split(':')[0]);
                    return Json(JsonSerializer.Serialize(new { result = ids.Select(id => new { id, title = id == "900" ? "Обложка и сердце · текущий трек" : "Тестовый трек " + id, artists = new[] { new { name = "Локальная тестовая запись" } }, albums = new[] { new { id = "91", title = "Проверка интерфейса" } }, coverUri = "avatars.yandex.net/get-music-content/native-fixture/" + id + "/%%", durationMs = 30000, available = true }).ToArray() }));
                }
                throw new InvalidOperationException("Unexpected media fixture endpoint: " + path);
            });
        }
    }
}
