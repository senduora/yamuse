using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using YMusic.Native;

internal static partial class Program
{
    private static Task Call(MainWindow window, string method, params object[] args) => (Task)typeof(MainWindow).GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, args)!;
    private static async Task Until(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(20, timeout.Token);
    }
    private static string WaveJson(string batch, int id) => JsonSerializer.Serialize(new { result = new { radioSessionId = "s1", batchId = batch, sequence = new[] { new { track = new { id = id.ToString(), title = "Wave fixture " + id, available = true, durationMs = 10000, artists = new[] { new { name = "Fixture" } }, albums = new[] { new { id = "91", title = "Fixture" } } } } } } });
    private static async Task CheckAdvanced()
    {
        await Run("Offline integration: downloaded catalog track reopens locally after restart without account or network", async () =>
        {
            var media = File.ReadAllBytes(WriteWave("instant-offline.wav", 4));
            var directory = Path.Combine(_directory, "instant-library");
            var original = new TrackRow { Id = "870", RequestId = "870:1", Title = "Saved fixture", Artist = "Fixture", DurationMs = 4000, Available = true };
            using (var first = new OfflineLibrary(directory, new AudioBuffer(Path.Combine(_directory, "instant-buffer"), new FakeHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(media) })))))
                await first.SaveAsync(original, new AudioDescriptor(new Uri("https://s1.vlx.yandex.net/audio.mp3"), "mp3", 192), default);
            var noNetwork = new FakeHandler((_, _) => throw new InvalidOperationException("downloaded playback attempted network"));
            var reopened = new OfflineLibrary(directory);
            var native = new NativePlayer(new AudioBuffer(Path.Combine(_directory, "instant-player-buffer"), noNetwork));
            var window = new MainWindow(api: new MusicApi(noNetwork), store: new SessionStore(Path.Combine(_directory, "instant-store")), restore: false, diagnosticsDirectory: _directory, offline: reopened, player: native);
            try
            {
                window.Show(); Control<Slider>(window, "VolumeSlider").Value = 0;
                var sameServerTrack = new TrackRow { Id = "870", RequestId = "870:99", Title = "Saved fixture", DurationMs = 4000, Available = false };
                var clock = System.Diagnostics.Stopwatch.StartNew();
                await window.PlayTrackAsync(sameServerTrack); clock.Stop();
                Assert(native.IsPlaying && sameServerTrack.IsDownloaded && sameServerTrack.CanPlay && sameServerTrack.DownloadBadge == "✓", "saved track not recognized across album context/restart");
                Assert(noNetwork.Seen.Count == 0, "downloaded audio performed network lookup");
                Assert(Control<TextBlock>(window, "QualityText").Text.StartsWith("Офлайн · ", StringComparison.Ordinal)
                    && Control<TextBlock>(window, "StatusText").Text == "", "downloaded source must appear beside player quality");
                File.WriteAllText(Path.Combine(_directory, "offline-local-start.json"), JsonSerializer.Serialize(new { localOpenMs = clock.Elapsed.TotalMilliseconds, networkRequests = 0, fullFileHashOnPlayback = false, persistedMark = true }));
            }
            finally { window.Close(); }
            Assert(File.Exists(Path.Combine(directory, reopened.Read()[0].FileName)), "closing player deleted offline track");
        });
        await Run("Offline integration: whole playlist download, persistent complete mark and no-account reopen", async () =>
        {
            var fixture = new CatalogFixture();
            fixture.Extra = (request, _) => Task.FromResult(request.RequestUri!.AbsolutePath == "/get-file-info"
                ? Json("{\"result\":{\"downloadInfo\":{\"quality\":\"lossless\",\"codec\":\"mp3\",\"bitrate\":320,\"transport\":\"raw\",\"url\":\"https://s1.vlx.yandex.net/audio.mp3\"}}}") : throw new InvalidOperationException("unexpected full-playlist request"));
            var media = File.ReadAllBytes(WriteWave("playlist-full.wav", 3));
            var mediaHandler = new FakeHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(media) }));
            var directory = Path.Combine(_directory, "whole-playlist");
            var library = new OfflineLibrary(directory, new AudioBuffer(Path.Combine(_directory, "whole-playlist-buffer"), mediaHandler));
            var window = new MainWindow(api: new MusicApi(fixture.Handler), store: new SessionStore(Path.Combine(_directory, "playlist-store")), restore: false, diagnosticsDirectory: _directory, offline: library);
            try
            {
                window.Show(); await window.StartLoginAsync();
                var rows = Control<ListView>(window, "TracksList").Items.Cast<TrackRow>().Take(2).Select(row => new TrackRow { Id = row.Id, RequestId = row.RequestId, Title = row.Title, Artist = row.Artist, Album = row.Album, DurationMs = 3000, Available = true }).ToArray();
                var details = new PlaylistDetails("12345", "42", "Offline playlist fixture", "", 7, rows);
                typeof(MainWindow).GetMethod("ShowPlaylist", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, [details]);
                await Call(window, "DownloadPlaylistAsync");
                Assert(library.GetPlaylistStatus("12345", "42", rows).Complete && library.Read().Count == 2 && mediaHandler.Seen.Count == 2, "whole playlist incomplete");
                Assert(Control<Button>(window, "DownloadPlaylistButton").Content.ToString()!.Contains("✓ Скачан") && rows.All(row => row.IsDownloaded), "playlist/track downloaded mark absent");
                App.Capture(window, Path.Combine(_directory, "ui-playlist-downloaded.png"));
            }
            finally { window.Close(); }
            var noNetwork = new FakeHandler((_, _) => throw new InvalidOperationException("offline playlist accessed network"));
            var reopened = new OfflineLibrary(directory);
            var offlineWindow = new MainWindow(api: new MusicApi(noNetwork), restore: false, diagnosticsDirectory: _directory, offline: reopened);
            try
            {
                offlineWindow.Show(); Control<Slider>(offlineWindow, "VolumeSlider").Value = 0;
                typeof(MainWindow).GetMethod("ShowOfflinePlaylist", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(offlineWindow, [new CatalogItem("42", "offline-playlist", "Fixture", "", "12345", "42")]);
                var list = Control<ListView>(offlineWindow, "TracksList");
                Assert(list.Items.Count == 2 && reopened.GetPlaylistStatus("12345", "42").Complete, "playlist status not persisted");
                await offlineWindow.PlayTrackAsync((TrackRow)list.Items[0]);
                Assert(noNetwork.Seen.Count == 0 && ((TrackRow)list.Items[0]).IsPlaying, "offline playlist did not play locally");
                Assert(Control<TextBlock>(offlineWindow, "QualityText").Text.StartsWith("Офлайн · ", StringComparison.Ordinal)
                    && Control<TextBlock>(offlineWindow, "StatusText").Text == "", "offline playlist source must appear beside player quality");
            }
            finally { offlineWindow.Close(); }
        });
        await Run("Quality tiers: exactly three service modes, AAC/HE-AAC negotiation and actual codecs", async () =>
        {
            var key = new string('0', 64); var count = 0;
            var handler = new FakeHandler((request, _) =>
            {
                Assert(request.RequestUri!.AbsolutePath == "/get-file-info", "unexpected legacy quality fallback");
                var query = request.RequestUri.Query;
                Assert(query.Contains("transports=encraw") && Uri.UnescapeDataString(query).Contains("he-aac-mp4") && query.Contains("sign="), "modern codec negotiation missing");
                var quality = ++count == 1 ? "lq" : count == 2 ? "nq" : "lossless";
                Assert(query.Contains("quality=" + quality), "wrong service tier");
                var codec = count == 1 ? "he-aac-mp4" : count == 2 ? "aac-mp4" : "flac-mp4";
                return Task.FromResult(Json(JsonSerializer.Serialize(new { result = new { downloadInfo = new { codec, quality, bitrate = count == 1 ? 64 : count == 2 ? 192 : 0, transport = "encraw", key, urls = new[] { "https://s1.vlx.yandex.net/audio" } } } })));
            });
            using var api = new MusicApi(handler);
            var economical = await api.ResolveAudioAsync(FakeSession, "20", AudioQuality.Economical, default);
            var optimal = await api.ResolveAudioAsync(FakeSession, "20", AudioQuality.Optimal, default);
            var excellent = await api.ResolveAudioAsync(FakeSession, "20", AudioQuality.Excellent, default);
            Assert(economical.Bitrate == 64 && economical.Extension == ".m4a" && optimal.Bitrate == 192 && optimal.Extension == ".m4a" && excellent.Codec == "flac-mp4" && excellent.Extension == ".m4a", "codec/container tier mismatch");
            var window = new MainWindow(demo: true, restore: false, diagnosticsDirectory: _directory, offline: new OfflineLibrary(Path.Combine(_directory, "advanced-demo-offline")));
            try { window.Show(); var combo = Control<ComboBox>(window, "QualityCombo"); Assert(combo.Items.Count == 3 && ((ComboBoxItem)combo.Items[0]).Content.ToString() == "Превосходное" && ((ComboBoxItem)combo.Items[1]).Content.ToString() == "Оптимальное" && ((ComboBoxItem)combo.Items[2]).Content.ToString() == "Экономичное", "UI choices differ from official modes"); }
            finally { window.Close(); }
        });
        await Run("UI integration: only two explicitly selected tracks downloaded; cancellation preserves completed files", async () =>
        {
            var fixture = new CatalogFixture();
            fixture.Extra = (request, _) => Task.FromResult(request.RequestUri!.AbsolutePath.EndsWith("/download-info")
                ? Json("{\"result\":[{\"codec\":\"mp3\",\"bitrateInKbps\":192,\"downloadInfoUrl\":\"https://storage.mds.yandex.net/fake\"}]}") : Xml());
            var media = File.ReadAllBytes(WriteWave("multi-download.wav", 2));
            var calls = 0; var blockThird = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var mediaHandler = new FakeHandler(async (_, token) =>
            {
                if (++calls == 3) { blockThird.TrySetResult(); await Task.Delay(Timeout.Infinite, token); }
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(media) };
            });
            var library = new OfflineLibrary(Path.Combine(_directory, "selected-downloads"), new AudioBuffer(Path.Combine(_directory, "selected-downloads-buffer"), mediaHandler));
            var window = new MainWindow(api: new MusicApi(fixture.Handler), store: new SessionStore(Path.Combine(_directory, "downloads-store")), restore: false, diagnosticsDirectory: _directory, offline: library);
            try
            {
                window.Show(); await window.StartLoginAsync(); var list = Control<ListView>(window, "TracksList");
                list.SelectedItems.Add(list.Items[0]); list.SelectedItems.Add(list.Items[1]);
                await Call(window, "DownloadSelectedAsync");
                Assert(library.Read().Count == 2 && calls == 2, "unselected tracks downloaded or selection ignored");
                list.SelectedItems.Clear(); list.SelectedItems.Add(list.Items[2]);
                var downloading = Call(window, "DownloadSelectedAsync"); await blockThird.Task.WaitAsync(TimeSpan.FromSeconds(2));
                Assert(Control<Button>(window, "DownloadButton").IsEnabled, "cancel download button disabled");
                await Call(window, "DownloadSelectedAsync"); await downloading;
                Assert(library.Read().Count == 2 && library.Read().All(item => File.Exists(library.ResolvePath(item.FileName))), "cancel deleted completed music");
                Assert(!Directory.EnumerateFiles(library.DirectoryPath).Any(path => path.Contains(".partial-")), "canceled partial file leaked");
            }
            finally { window.Close(); }
        });
        await Run("UI integration: server Wave -> native playback -> pause feedback -> prefetched server batch", async () =>
        {
            var fixture = new CatalogFixture();
            var feedback = new List<string>(); var more = 0;
            fixture.Extra = async (request, token) =>
            {
                var path = request.RequestUri!.AbsolutePath;
                if (path == "/get-file-info") return Json("{\"result\":{\"downloadInfo\":{\"quality\":\"lossless\",\"codec\":\"mp3\",\"bitrate\":320,\"transport\":\"raw\",\"url\":\"https://s1.vlx.yandex.net/fake.mp3\"}}}");
                if (path == "/rotor/session/new") return Json(WaveJson("b1", 501));
                if (path == "/rotor/session/s1/tracks") { more++; return Json(WaveJson("b2", 502)); }
                if (path == "/rotor/session/s1/feedback/") { feedback.Add(await request.Content!.ReadAsStringAsync(token)); return Json("{\"result\":{}}"); }
                if (path.EndsWith("/download-info")) return Json("{\"result\":[{\"codec\":\"mp3\",\"bitrateInKbps\":192,\"downloadInfoUrl\":\"https://storage.mds.yandex.net/fake\"}]}");
                if (request.RequestUri.Host == "storage.mds.yandex.net") return Xml();
                throw new InvalidOperationException("Unexpected Wave fixture route");
            };
            var media = File.ReadAllBytes(WriteWave("wave-fixture.wav", 10));
            var mediaHandler = new FakeHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(media) }));
            var native = new NativePlayer(new AudioBuffer(Path.Combine(_directory, "wave-buffer"), mediaHandler));
            var window = new MainWindow(api: new MusicApi(fixture.Handler), store: new SessionStore(Path.Combine(_directory, "wave-store")), restore: false, diagnosticsDirectory: _directory, player: native, offline: new OfflineLibrary(Path.Combine(_directory, "advanced-wave-offline")));
            try
            {
                window.Show(); Control<Slider>(window, "VolumeSlider").Value = 0; await window.StartLoginAsync();
                await Call(window, "StartWaveViewAsync", false);
                Assert(Control<TextBlock>(window, "TrackTitle").Text == "Wave fixture 501", "Wave did not play server track");
                Assert(native.IsPlaying && native.Duration.TotalSeconds >= 9, "Wave native audio did not open");
                Assert(Control<Button>(window, "NextButton").IsEnabled, "last Wave Next disabled");
                await Task.Delay(100); await Call(window, "TogglePlaybackAsync"); await Task.Delay(300);
                var prefetchedBatches = more;
                Assert(prefetchedBatches is >= 1 and <= 2, "Wave tail was not prepared, or fetched without a bounded lookahead");
                await Call(window, "AdvanceAsync", 1, false);
                Assert(more >= prefetchedBatches && more <= prefetchedBatches + 1 && Control<TextBlock>(window, "TrackTitle").Text == "Wave fixture 502", "Wave extension/advance failed");
                await Until(() => feedback.Count >= 4);
                var events = feedback.Select(body => JsonDocument.Parse(body).RootElement.GetProperty("event").Clone()).ToArray();
                Assert(events.Take(4).Select(value => value.GetProperty("type").GetString()).SequenceEqual(new[] { "radioStarted", "trackStarted", "skip", "trackStarted" }), "feedback order");
                var played = events[2].GetProperty("totalPlayedSeconds").GetDouble();
                Assert(played >= 0.06 && played < 0.30, "paused time incorrectly counted as listening");
                App.Capture(window, Path.Combine(_directory, "ui-wave.png"));
            }
            finally { window.Close(); }
        });
        await Run("UI integration: global catalog search, album detail, navigation preserves active player queue", async () =>
        {
            var fixture = new CatalogFixture();
            fixture.Extra = (request, _) => Task.FromResult(request.RequestUri!.AbsolutePath switch
            {
                "/search" => Json("{\"result\":{\"albums\":{\"total\":1,\"perPage\":20,\"results\":[{\"id\":91,\"title\":\"Catalog album\",\"artists\":[{\"name\":\"Fixture\"}]}]}}}"),
                "/albums/91/with-tracks" => Json("{\"result\":{\"id\":91,\"title\":\"Catalog album\",\"volumes\":[[{\"id\":501,\"title\":\"Album track\",\"available\":true,\"albums\":[{\"id\":91}],\"artists\":[{\"name\":\"Fixture\"}]}]]}}"),
                _ => throw new InvalidOperationException("Unexpected catalog fixture route")
            });
            var window = Window(fixture.Handler);
            try
            {
                window.Show(); await window.StartLoginAsync();
                Control<TextBox>(window, "CatalogQuery").Text = "Catalog";
                await Call(window, "SearchCatalogViewAsync", false);
                var list = Control<ListBox>(window, "CatalogList");
                Assert(list.IsVisible && list.Items.Count == 1, "global search results not visible");
                list.SelectedIndex = 0; await Call(window, "OpenSelectedCatalogAsync");
                Assert(Control<ListView>(window, "TracksList").Items.Count == 1 && Control<TextBlock>(window, "CollectionTitle").Text == "Catalog album", "album open failed");
                App.Capture(window, Path.Combine(_directory, "ui-catalog-album.png"));
                Control<Button>(window, "FavoritesNav").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert(Control<ListView>(window, "TracksList").Items.Count == 205, "favorites cache not restored");
            }
            finally { window.Close(); }
        });
        if (_realAccount) await Run("ACCOUNT: catalog read-only search, own playlists and saved albums/artists", async () =>
        {
            var session = new SessionStore(App.DataDirectory).Load() ?? throw new InvalidOperationException("Prototype session missing");
            using var api = new MusicApi(); var account = await api.GetAccountAsync(session, default);
            var playlists = await api.GetPlaylistsAsync(session, account.Uid, default);
            var albums = await api.GetLikedAlbumsAsync(session, account.Uid, default);
            var artists = await api.GetLikedArtistsAsync(session, account.Uid, default);
            var search = await api.SearchCatalogAsync(session, "музыка", "all", 0, default);
            Assert(search.Items.Count > 0, "real search returned no usable entities");
            File.WriteAllText(Path.Combine(_directory, "real-catalog-sanitized.json"), JsonSerializer.Serialize(new { playlists = playlists.Count, albums = albums.Count, artists = artists.Count, searchResults = search.Items.Count, mutations = 0, accountDetailsSaved = false }));
        });
    }
}
