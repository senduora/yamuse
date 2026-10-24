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
        await Run("Lyrics UI: Yandex supplement displays two lines without Genius", async () =>
        {
            var yandex = new FakeHandler((request, _) => Task.FromResult(LyricsUiYandex(request, "Yandex fixture one\nYandex fixture two")));
            var genius = new FakeHandler((_, _) => throw new InvalidOperationException("Yandex lyrics unexpectedly contacted Genius"));
            var window = LyricsUiWindow("yandex", yandex, genius);
            try
            {
                LyricsUiSet(window, "_current", LyricsUiTrack("41001"));
                LyricsUiOpen(window);
                await Until(() => LyricsUiLines(window).SequenceEqual(new[] { "Yandex fixture one", "Yandex fixture two" }));
                Assert(Control<Grid>(window, "LyricsPanel").IsVisible && !Control<Grid>(window, "ContentGrid").IsVisible && window.OwnedWindows.Count == 0, "lyrics must open inside the main window only");
                Assert(LyricsUiField<TextBlock>(window, "_lyricsNote")!.Text.Contains("Источник: Яндекс Музыка", StringComparison.Ordinal), "Yandex source label missing");
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
                LyricsUiSet(window, "_current", LyricsUiTrack("41003", "Old fixture song"));
                LyricsUiOpen(window);
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                var oldCancellation = LyricsUiField<CancellationTokenSource>(window, "_lyricsCts")!;
                LyricsUiSet(window, "_current", LyricsUiTrack("41004", "Current fixture song"));
                LyricsUiInvoke(window, "RefreshLyrics");
                await Until(() => LyricsUiLines(window).SequenceEqual(new[] { "Current fixture one", "Current fixture two" }));
                Assert(cancellationObserved, "switching the track did not cancel its pending lyrics request");
                release.TrySetResult();
                await returned.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await Until(() => LyricsUiDisposed(oldCancellation));
                Assert(LyricsUiLines(window).SequenceEqual(new[] { "Current fixture one", "Current fixture two" }), "old response overwrote the new text");
                Assert(LyricsUiField<TextBlock>(window, "_lyricsHeading")!.Text == "Current fixture song", "old response overwrote the current heading");
                var cache = LyricsUiField<IDictionary>(window, "_lyricsCache")!;
                Assert(!cache.Contains("41003") && cache.Contains("41004") && genius.Seen.Count == 0, "canceled text entered the cache or started fallback");
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
                    Assert(genius.Seen.Count == 0 && !LyricsUiField<IDictionary>(window, "_lyricsCache")!.Contains(track.Id), "provider error was negatively cached or sent to Genius");
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
