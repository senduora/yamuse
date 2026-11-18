using System.Collections;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using YMusic.Native;

internal static partial class Program
{
    private static async Task CheckLyricsUi()
    {
        await Run("Lyrics UI: primary artist without a link survives playback favorites and offline snapshots", CheckLyricsPrimaryArtistRoundTrip);

        await Run("Lyrics UI: Yandex supplement displays two lines without Genius", async () =>
        {
            var yandex = new FakeHandler((request, _) => Task.FromResult(request.RequestUri!.AbsolutePath.EndsWith("/supplement", StringComparison.Ordinal)
                ? Json("""{"result":{"lyrics":{"hasRights":false,"lyrics":"Yandex fixture one\nYandex fixture two"}}}""")
                : LyricsUiYandex(request)));
            var genius = new FakeHandler((_, _) => throw new InvalidOperationException("Yandex lyrics unexpectedly contacted Genius"));
            var window = LyricsUiWindow("yandex", yandex, genius);
            try
            {
                LyricsUiSet(window, "_current", LyricsUiTrack("41001"));
                LyricsUiOpen(window);
                await Until(() => LyricsUiLines(window).SequenceEqual(new[] { "Yandex fixture one", "Yandex fixture two" }));
                Assert(Control<Grid>(window, "LyricsPanel").IsVisible && !Control<Grid>(window, "ContentGrid").IsVisible && window.OwnedWindows.Count == 0, "lyrics must open inside the main window only");
                var note = LyricsUiField<TextBlock>(window, "_lyricsNote")!.Text;
                Assert(note.Contains("Источник: Яндекс Музыка", StringComparison.Ordinal)
                    && note.Contains("Полнота доступного обычного текста не подтверждена", StringComparison.Ordinal), "supplement lost its source or completeness warning");
                Assert(LyricsUiField<Button>(window, "_lyricsSourceButton")!.Visibility == Visibility.Collapsed && LyricsUiField<Uri>(window, "_lyricsSource") is null, "Yandex text incorrectly points to Genius");
                Assert(yandex.Seen.Count == 3 && genius.Seen.Count == 0, "expected LRC/TEXT/supplement only");
            }
            finally { window.Close(); }
        });

        await Run("Lyrics UI: empty Yandex uses matched Genius, source link and repeat cache", async () =>
        {
            var yandex = new FakeHandler((request, _) => Task.FromResult(LyricsUiYandex(request)));
            var genius = LyricsUiGenius();
            var window = LyricsUiWindow("genius", yandex, genius);
            try
            {
                LyricsUiSet(window, "_current", LyricsUiTrack("41002"));
                LyricsUiOpen(window);
                await Until(() => LyricsUiLines(window).SequenceEqual(new[] { "Genius fixture one", "Genius fixture two" }));
                var source = LyricsUiField<Uri>(window, "_lyricsSource");
                Assert(source == new Uri("https://genius.com/fixture-artist-fixture-song-lyrics"), "matched Genius source URI missing");
                Assert(LyricsUiField<Button>(window, "_lyricsSourceButton")!.Visibility == Visibility.Visible && LyricsUiField<TextBlock>(window, "_lyricsNote")!.Text.Contains("Источник: Genius", StringComparison.Ordinal), "Genius attribution/link not visible");
                Assert(yandex.Seen.Count == 3 && genius.Seen.Count == 2, "unexpected fallback request count");

                // Toggle the embedded view; its per-track cache should avoid both providers.
                LyricsUiOpen(window);
                Assert(!Control<Grid>(window, "LyricsPanel").IsVisible && Control<Grid>(window, "ContentGrid").IsVisible, "closing lyrics did not restore the collection");
                LyricsUiOpen(window);
                await Until(() => LyricsUiLines(window).SequenceEqual(new[] { "Genius fixture one", "Genius fixture two" }));
                Assert(LyricsUiField<Button>(window, "_lyricsSourceButton")!.Visibility == Visibility.Visible && LyricsUiField<Uri>(window, "_lyricsSource") == source, "cached text lost its source");
                Assert(yandex.Seen.Count == 3 && genius.Seen.Count == 2, "cached reopen performed HTTP");
            }
            finally { window.Close(); }
        });

        await Run("Lyrics UI: manual refresh retries Genius, preserves artist commas and does not cache 403", async () =>
        {
            var yandex = new FakeHandler((request, _) => Task.FromResult(LyricsUiYandex(request)));
            var searches = 0;
            var genius = new FakeHandler((request, _) =>
            {
                var uri = request.RequestUri!;
                if (uri.AbsolutePath == "/api/search/multi")
                {
                    Assert(Uri.UnescapeDataString(uri.Query).EndsWith("q=Fixture Artist, Ensemble Fixture Song", StringComparison.Ordinal),
                        "Genius split a structured primary artist or included the guest");
                    return Task.FromResult(++searches == 2 ? Json("{}", HttpStatusCode.Forbidden)
                        : Json("""{"response":{"sections":[{"type":"song","hits":[{"result":{"title":"Fixture Song","path":"/Fixture-song-lyrics","primary_artist":{"name":"Fixture Artist, Ensemble"}}}]}]}}"""));
                }
                Assert(uri.AbsolutePath == "/Fixture-song-lyrics", "unexpected Genius fixture route");
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    { Content = new StringContent($"<div data-lyrics-container='true'>Lyric {searches}</div>") });
            });
            var window = LyricsUiWindow("genius-refresh", yandex, genius);
            try
            {
                using var catalog = new MusicApi(new FakeHandler((_, _) => Task.FromResult(Json("""
                    {"result":[{"id":41011,"title":"Fixture Song","available":true,"durationMs":180000,
                    "artists":[{"name":"Fixture Artist, Ensemble"},{"id":42,"name":"Guest"}]}]}
                    """))));
                var track = (await catalog.GetTracksAsync(FakeSession, [new("41011", null)], 0, default)).Single();
                Assert(track.Artists.Select(artist => artist.Name).SequenceEqual(["Guest"]), "fixture made a name-only artist navigable");
                LyricsUiSet(window, "_current", track);
                LyricsUiOpen(window);
                await Until(() => LyricsUiLines(window).SequenceEqual(new[] { "Lyric 1" }));

                LyricsUiInvoke(window, "LyricsRetry_Click", window, new RoutedEventArgs());
                var note = LyricsUiField<TextBlock>(window, "_lyricsNote")!;
                await Until(() => note.Text.Contains("HTTP 403", StringComparison.Ordinal));
                Assert(LyricsUiLines(window).Length == 0 && !LyricsUiCached(window, track),
                    "manual Genius failure left stale lyrics or a negative cache");

                LyricsUiOpen(window); LyricsUiOpen(window);
                await Until(() => LyricsUiLines(window).SequenceEqual(new[] { "Lyric 3" }));
                Assert(searches == 3 && genius.Seen.Count == 5, "403 prevented a fresh Genius lookup on reopen");
            }
            finally { window.Close(); }
        });

        await Run("Lyrics UI: a visible guest track reloads lyrics when account activation keeps playback", async () =>
        {
            var yandex = new FakeHandler((request, _) => Task.FromResult(request.RequestUri!.AbsolutePath switch
            {
                "/account/status" => Json(Profile),
                "/account/about" => Json("""{"result":{"uid":"12345"}}"""),
                "/users/12345/likes/tracks" => Json("""{"result":{"library":{"tracks":[]}}}"""),
                _ => LyricsUiYandex(request, "Account lyrics fixture")
            }));
            var genius = LyricsUiGenius();
            var window = LyricsUiWindow("guest-activation", yandex, genius);
            try
            {
                var track = LyricsUiTrack("41012");
                LyricsUiSet(window, "_session", null!);
                LyricsUiSet(window, "_account", null!);
                LyricsUiSet(window, "_current", track);
                var player = LyricsUiField<NativePlayer>(window, "_player")!;
                await player.OpenPausedAsync(new Uri(WriteWave("lyrics-guest-activation.wav", 10)), default);
                LyricsUiOpen(window);
                await Until(() => LyricsUiLines(window).SequenceEqual(["Genius fixture one", "Genius fixture two"]));
                Assert(LyricsUiCached(window, track) && yandex.Seen.Count == 0, "fixture did not reach cached guest lyrics");

                await Call(window, "ActivateSessionAsync", FakeSession, default(CancellationToken), true);
                await Until(() => LyricsUiLines(window).SequenceEqual(["Account lyrics fixture"]));
                Assert(Control<Grid>(window, "LyricsPanel").IsVisible && player.SourceLocalPath is not null &&
                    ReferenceEquals(track, LyricsUiField<TrackRow>(window, "_current")) && LyricsUiCached(window, track)
                    && LyricsUiField<IDictionary>(window, "_lyricsCache")!.Count == 1,
                    "account activation hid the occupied panel, replaced local playback, or kept the guest cache");
            }
            finally { window.Close(); }
        });

        await Run("Lyrics UI: late canceled track response cannot replace the current text", async () =>
        {
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var returned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var cancellationObserved = false;
            var yandex = new FakeHandler(async (request, token) =>
            {
                if (request.RequestUri!.AbsolutePath == "/tracks/41003/lyrics")
                {
                    using var observe = token.Register(() => cancellationObserved = true);
                    entered.TrySetResult();
                    // Deliberately return after cancellation, like an uncooperative late server.
                    await release.Task;
                    returned.TrySetResult();
                    return Json("{}", HttpStatusCode.NotFound);
                }
                return LyricsUiYandex(request, "Current fixture one\nCurrent fixture two");
            });
            var genius = new FakeHandler((_, _) => throw new InvalidOperationException("Track race unexpectedly contacted Genius"));
            var window = LyricsUiWindow("race", yandex, genius);
            try
            {
                var oldTrack = LyricsUiTrack("41003", "Old fixture song");
                var newTrack = LyricsUiTrack("41004", "Current fixture song");
                LyricsUiSet(window, "_current", oldTrack);
                LyricsUiOpen(window);
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                var oldCancellation = LyricsUiField<CancellationTokenSource>(window, "_lyricsCts")!;
                LyricsUiSet(window, "_current", newTrack);
                LyricsUiInvoke(window, "RefreshLyrics");
                await Until(() => LyricsUiLines(window).SequenceEqual(new[] { "Current fixture one", "Current fixture two" }));
                Assert(cancellationObserved, "switching the track did not cancel its pending lyrics request");
                release.TrySetResult();
                await returned.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await Until(() => LyricsUiDisposed(oldCancellation));
                Assert(LyricsUiLines(window).SequenceEqual(new[] { "Current fixture one", "Current fixture two" }), "old response overwrote the new text");
                Assert(LyricsUiField<TextBlock>(window, "_lyricsHeading")!.Text == "Current fixture song", "old response overwrote the current heading");
                Assert(!LyricsUiCached(window, oldTrack) && LyricsUiCached(window, newTrack) && genius.Seen.Count == 0,
                    "canceled text entered the cache or started fallback");
            }
            finally { release.TrySetResult(); window.Close(); }
        });

        await Run("Lyrics UI: Yandex 403 or 5xx is an error, without absence cache or Genius", async () =>
        {
            foreach (var status in new[] { HttpStatusCode.Forbidden, HttpStatusCode.ServiceUnavailable })
            {
                var yandex = new FakeHandler((request, _) => Task.FromResult(LyricsUiYandex(request, failure: status)));
                var genius = new FakeHandler((_, _) => throw new InvalidOperationException("Yandex error must not start Genius fallback"));
                var window = LyricsUiWindow("failure-" + (int)status, yandex, genius);
                try
                {
                    var track = LyricsUiTrack("41005"); LyricsUiSet(window, "_current", track);
                    LyricsUiOpen(window);
                    await Until(() => LyricsUiField<CancellationTokenSource>(window, "_lyricsCts") is null);
                    var note = LyricsUiField<TextBlock>(window, "_lyricsNote")!.Text;
                    Assert(note.Contains("Не удалось загрузить текст", StringComparison.Ordinal) && !note.Contains("Текст не найден", StringComparison.Ordinal), "provider error was presented as absent lyrics");
                    Assert(LyricsUiLines(window).Length == 0 && LyricsUiField<Button>(window, "_lyricsSourceButton")!.Visibility == Visibility.Collapsed, "provider error displayed cached/fallback text");
                    Assert(genius.Seen.Count == 0 && !LyricsUiCached(window, track), "provider error was negatively cached or sent to Genius");
                    Assert(yandex.Seen.Count == 3, "expected independent TEXT/supplement fallback before reporting the error");
                }
                finally { window.Close(); }
            }
        });

        await Run("Lyrics UI: embedded player view preserves playback and collection, wraps at minimum and closes with Escape/navigation", async () =>
        {
            var text = string.Join('\n', Enumerable.Range(1, 12).Select(i => $"Строка {i}: длинный текст для проверки переноса внутри основного окна"));
            var yandex = new FakeHandler((request, _) => Task.FromResult(LyricsUiYandex(request, text)));
            var genius = new FakeHandler((_, _) => throw new InvalidOperationException("Unexpected Genius request"));
            var window = LyricsUiWindow("embedded", yandex, genius);
            try
            {
                var row = new TrackRow { Id = "41006", RequestId = "41006:1", Title = "Текст внутри проигрывателя", Artist = "Тестовый исполнитель", Available = true, DurationMs = 20000, LocalPath = WriteWave("inline-lyrics-20s.wav", 20) };
                LyricsUiInvoke(window, "ShowRows", (object)new[] { row });
                var tracks = Control<ListView>(window, "TracksList"); tracks.SelectedItem = row;
                Control<TextBox>(window, "FilterBox").Text = "Текст";
                await window.PlayTrackAsync(row);
                var player = LyricsUiField<NativePlayer>(window, "_player")!;
                Assert(player.IsPlaying && player.SourceLocalPath == row.LocalPath, "local muted fixture did not play");
                LyricsUiOpen(window);
                await Until(() => LyricsUiLines(window).Length == 12);
                await Task.Delay(200); window.UpdateLayout();
                Assert(window.OwnedWindows.Count == 0 && Control<Grid>(window, "LyricsPanel").IsVisible && Control<System.Windows.Controls.Border>(window, "PlayerBar").IsVisible, "lyrics opened another window or hid player controls");
                App.Capture(window, Path.Combine(_directory, "lyrics-inline-1280x860.png"));
                window.Width = 1050; window.Height = 680; window.UpdateLayout(); await Task.Delay(200);
                var scroll = LyricsUiVisuals<ScrollViewer>(Control<ListBox>(window, "LyricsList")).First();
                Assert(scroll.ScrollableWidth < .5 && LyricsUiVisuals<TextBlock>(scroll).Any(t => t.ActualHeight > 45), "lyrics are cropped instead of wrapping at minimum window size");
                App.Capture(window, Path.Combine(_directory, "lyrics-inline-1050x680.png"));
                var escape = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window), Environment.TickCount, Key.Escape) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
                window.RaiseEvent(escape);
                Assert(escape.Handled && !Control<Grid>(window, "LyricsPanel").IsVisible && Control<Grid>(window, "ContentGrid").IsVisible, "Escape did not restore collection");
                Assert(player.IsPlaying && Control<TextBox>(window, "FilterBox").Text == "Текст" && ReferenceEquals(tracks.SelectedItem, row), "lyrics toggle disturbed playback, selection or filter");
                LyricsUiOpen(window);
                await Until(() => LyricsUiLines(window).Length == 12);
                LyricsUiInvoke(window, "SetSection", "offline", "Скачанное");
                Assert(!Control<Grid>(window, "LyricsPanel").IsVisible && Control<Grid>(window, "ContentGrid").IsVisible && player.IsPlaying && yandex.Seen.Count == 3, "navigation did not leave lyrics cleanly or cached reopen requested HTTP");
            }
            finally { window.Close(); }
        });
    }

    private static async Task CheckLyricsPrimaryArtistRoundTrip()
    {
        const string primary = "Fixture Artist, Ensemble";
        var track = new TrackRow { Id = "71001", RequestId = "71001:17", AlbumId = "17", Title = "Fixture track",
            Artist = primary + ", Guest", PrimaryArtistName = primary, Artists = [new("42", "Guest")], DurationMs = 1000, Available = true };
        var legacyTrack = new TrackRow { Id = "71002", RequestId = "71002", Title = "Legacy fixture", Artist = "Legacy Artist, Guest",
            Artists = [new("42", "Guest")], DurationMs = 1000, Available = true };
        var directory = Path.Combine(_directory, "lyrics-primary-artist-checkpoints");
        var media = File.ReadAllBytes(WriteWave("lyrics-primary-artist.wav", 1));
        using var handler = new FakeHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(media) }));
        var music = Path.Combine(directory, "Music");
        using (var writer = new OfflineLibrary(music, new AudioBuffer(Path.Combine(directory, "buffer"), handler)))
        {
            await writer.SaveAsync(track, new(new Uri("https://s1.vlx.yandex.net/primary-artist-fixture.wav"), "mp3", 192), default);
            await writer.RememberPlaylistAsync(new("fixture", "17", "Offline fixture", "", 1, [track]), AudioQuality.Optimal, default);
        }

        using var offline = new OfflineLibrary(music);
        var playback = new PlaybackQueue(); playback.SetItems([track, legacyTrack]);
        var playbackStore = new PlaybackStateStore(directory);
        var playbackSnapshot = PlaybackStateStore.Capture("fixture", playback, 0);
        playbackStore.Save(playbackSnapshot);
        var restored = PlaybackStateStore.RestoreQueue(playbackStore.Load("fixture")!, offline).Tracks;
        Assert(restored[1].PrimaryArtistName is null && restored[1].Artist == legacyTrack.Artist,
            "an old snapshot obtained a guessed artist identity");
        await Throws<JsonException>(() =>
        {
            playbackStore.Save(playbackSnapshot with { Tracks = [playbackSnapshot.Tracks[0] with { PrimaryArtistName = new string('x', 4097) }] });
            return Task.CompletedTask;
        });

        var favorites = new FavoritesCacheStore(directory);
        await favorites.SaveInBackgroundAsync(FavoritesCacheStore.Capture("fixture", [track], DateTimeOffset.UtcNow));
        var downloads = new DownloadQueueState(); downloads.Replace([track], "Download fixture");
        var downloadStore = new DownloadQueueStore(directory);
        downloadStore.Save(DownloadQueueStore.Capture("fixture", downloads, AudioQuality.Optimal, false, null));

        var restoredRows = new[] { restored[0], FavoritesCacheStore.Restore(favorites.Load("fixture")!).Single(),
            DownloadQueueStore.PrepareRestore(downloadStore.Load("fixture")!, offline).Entries.Single().Track,
            offline.Tracks().Single(), offline.GetPlaylistTracks("fixture", "17").Single() };
        Assert(restoredRows.All(row => row.PrimaryArtistName == primary && row.Artists.Single().Name == "Guest"),
            "a saved track lost its structured primary name or exposed it as a navigation link");
    }

    private static MainWindow LyricsUiWindow(string suffix, FakeHandler yandex, FakeHandler genius)
    {
        var directory = Path.Combine(_directory, "lyrics-ui-" + suffix);
        var window = new MainWindow(api: new MusicApi(yandex), store: new SessionStore(Path.Combine(directory, "store")),
            restore: false, diagnosticsDirectory: Path.Combine(directory, "diagnostics"), offline: new OfflineLibrary(Path.Combine(directory, "offline")));
        LyricsUiSet(window, "_geniusLyrics", new GeniusLyricsClient(genius));
        LyricsUiSet(window, "_lrclibLyrics", new LrclibLyricsClient(new HttpClient(new FakeHandler((_, _) => Task.FromResult(Json("{}", HttpStatusCode.NotFound))))));
        LyricsUiSet(window, "_session", FakeSession);
        LyricsUiSet(window, "_account", new Account("12345", "Lyrics fixture", true));
        window.Show(); Control<Slider>(window, "VolumeSlider").Value = 0;
        return window;
    }
    private static TrackRow LyricsUiTrack(string id, string title = "Fixture Song") => new() { Id = id, RequestId = id + ":1", Title = title, Artist = "Fixture Artist, Fixture Guest", Available = true, DurationMs = 180000 };
    private static void LyricsUiOpen(MainWindow window) => Control<Button>(window, "PlayerLyricsButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static T? LyricsUiField<T>(MainWindow window, string name) where T : class => (T?)typeof(MainWindow).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window);
    private static void LyricsUiSet(MainWindow window, string name, object value) => typeof(MainWindow).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(window, value);
    private static void LyricsUiInvoke(MainWindow window, string name, params object[] arguments) => typeof(MainWindow).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, arguments);
    private static bool LyricsUiCached(MainWindow window, TrackRow track)
    {
        var key = typeof(MainWindow).GetMethod("LyricsCacheIdentity", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, [track]);
        return LyricsUiField<IDictionary>(window, "_lyricsCache")!.Contains(key!);
    }
    private static string[] LyricsUiLines(MainWindow window) => LyricsUiField<ListBox>(window, "_lyricsList")?.Items.Cast<object>().Select(value => value.ToString() ?? "").ToArray() ?? [];
    private static bool LyricsUiDisposed(CancellationTokenSource cancellation)
    {
        try { _ = cancellation.Token; return false; }
        catch (ObjectDisposedException) { return true; }
    }
    private static IEnumerable<T> LyricsUiVisuals<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T value) yield return value;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in LyricsUiVisuals<T>(VisualTreeHelper.GetChild(root, i))) yield return child;
    }
    private static HttpResponseMessage LyricsUiYandex(HttpRequestMessage request, string? text = null, HttpStatusCode? failure = null)
    {
        Assert(request.Method == HttpMethod.Get && request.RequestUri!.Host == "api.music.yandex.net", "lyrics fixture attempted another endpoint/method");
        if (request.RequestUri!.AbsolutePath.EndsWith("/supplement", StringComparison.Ordinal))
            return Json(text is null ? """{"result":{"lyrics":null}}""" : JsonSerializer.Serialize(new { result = new { lyrics = new { hasRights = true, fullLyrics = text } } }));
        Assert(request.RequestUri.AbsolutePath.EndsWith("/lyrics", StringComparison.Ordinal), "unexpected lyrics fixture endpoint");
        return Json("{}", failure is { } status && request.RequestUri.Query.Contains("format=LRC", StringComparison.Ordinal) ? status : HttpStatusCode.NotFound);
    }
    private static FakeHandler LyricsUiGenius() => new((request, _) =>
    {
        Assert(request.Method == HttpMethod.Get && request.RequestUri!.Host == "genius.com" && request.Headers.Authorization is null && !request.Headers.Contains("Cookie"), "Genius fixture request was not anonymous");
        if (request.RequestUri!.AbsolutePath == "/api/search/multi")
            return Task.FromResult(Json("""{"response":{"sections":[{"type":"song","hits":[{"type":"song","result":{"title":"Fixture Song","url":"https://genius.com/fixture-artist-fixture-song-lyrics","primary_artist":{"name":"Fixture Artist"}}}]}]}}"""));
        Assert(request.RequestUri.AbsolutePath == "/fixture-artist-fixture-song-lyrics", "unexpected Genius fixture page");
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<div data-lyrics-container=true>Genius fixture one<br>Genius fixture two</div>", Encoding.UTF8, "text/html") });
    });
}
