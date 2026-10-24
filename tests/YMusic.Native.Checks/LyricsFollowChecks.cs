using System.Collections;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using YMusic.Native;

internal static partial class Program
{
    private static async Task CheckLyricsFollow()
    {
        var originalArtwork = typeof(Artwork).GetField("_cache", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null);
        var artworkRequests = 0;
        using var artwork = new ArtworkCache(Path.Combine(_directory, "lyrics-follow-artwork"), new FakeHandler((request, _) =>
        {
            Assert(ArtworkUri.Valid(request.RequestUri!) && request.Headers.Authorization is null && !request.Headers.Contains("Cookie"), "lyrics artwork escaped anonymous fixture transport");
            Interlocked.Increment(ref artworkRequests);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(MediaCoverPng()) });
        }));
        Artwork.Cache = artwork;
        try
        {
            await Run("Lyrics follow: Yandex plain upgrades to exact timed LRCLIB with source and reopen cache", CheckLyricsFollowFallback);
            await Run("Lyrics follow: LRCLIB miss retains Yandex plain without Genius", CheckLyricsFollowPlain);
            await Run("Lyrics follow: Yandex authentication/access/server errors never start another provider", CheckLyricsFollowProviderErrors);
            await Run("Lyrics follow: canceled late LRCLIB cannot replace a new track or enter its cache", CheckLyricsFollowRace);
            await Run("Lyrics follow: native clock, exact single click and backward seek, visible playback timer", CheckLyricsFollowNative);
            await Run("Lyrics follow: routed wheel preserves manual offset and idle resumes follow; contextual return", CheckLyricsFollowManual);
            File.WriteAllText(Path.Combine(_directory, "lyrics-follow-scope.json"), JsonSerializer.Serialize(new
            {
                realAccountLoaded = false, externalNetworkRequests = 0, userAudioDirectoryUsed = false,
                globalPointerOrKeyboardInput = false, syntheticRoutedInput = true, nativeWavMuted = true,
                artworkFixtureRequests = artworkRequests, manualFollowIdleMilliseconds = 3500,
                checkedAssemblySha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(MainWindow).Assembly.Location)))
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        finally { typeof(Artwork).GetField("_cache", BindingFlags.NonPublic | BindingFlags.Static)!.SetValue(null, originalArtwork); }
    }

    private static async Task CheckLyricsFollowFallback()
    {
        var track = LyricsFollowTrack("42101");
        using var fixture = new LyricsFollowFixture("fallback", LyricsFollowYandexPlain("Original Yandex plain"),
            (request, _) =>
            {
                LyricsFollowAnonymous(request);
                var query = LyricsFollowQuery(request.RequestUri!);
                Assert(query["track_name"] == track.Title && query["artist_name"] == track.Artist
                    && query["album_name"] == track.Album && query["duration"] == "180", "LRCLIB did not use the current exact metadata");
                return Task.FromResult(LyricsFollowRecord(track, "[00:00.00]Timed first\n[00:04.00]Timed second\n[00:08.00]Timed third"));
            });
        var window = fixture.Window;
        LyricsUiSet(window, "_current", track); LyricsUiOpen(window);
        await Until(() => LyricsUiLines(window).SequenceEqual(new[] { "Timed first", "Timed second", "Timed third" }));
        var timed = LyricsUiField<IReadOnlyList<LyricLine>>(window, "_lyricLines")!;
        Assert(timed.Select(line => line.TimeSeconds).SequenceEqual(new[] { 0d, 4d, 8d }), "LRCLIB timestamps were replaced or lost");
        Assert(LyricsUiField<Uri>(window, "_lyricsSource") == new Uri("https://lrclib.net"), "LRCLIB source URL missing");
        var source = LyricsUiField<Button>(window, "_lyricsSourceButton")!;
        Assert(source.Visibility == Visibility.Visible && (source.Content?.ToString() ?? "").Contains("LRCLIB", StringComparison.Ordinal)
            && LyricsUiField<TextBlock>(window, "_lyricsNote")!.Text.Contains("LRCLIB", StringComparison.Ordinal), "LRCLIB attribution/caption missing");
        Assert(fixture.Yandex.Seen.Count == 3 && fixture.Lrclib.Seen.Count == 1 && fixture.Genius.Seen.Count == 0, "unexpected provider request sequence");
        LyricsUiOpen(window); LyricsUiOpen(window);
        await Until(() => LyricsUiLines(window).Length == 3);
        Assert(fixture.Yandex.Seen.Count == 3 && fixture.Lrclib.Seen.Count == 1 && fixture.Genius.Seen.Count == 0,
            "cached synchronized reopen made another provider request");
        Assert(LyricsUiField<Uri>(window, "_lyricsSource") == new Uri("https://lrclib.net"), "cached timed result lost provenance");
    }

    private static async Task CheckLyricsFollowPlain()
    {
        using var fixture = new LyricsFollowFixture("plain", LyricsFollowYandexPlain("Original first\nOriginal second"),
            (request, _) => { LyricsFollowAnonymous(request); return Task.FromResult(Json("{}", HttpStatusCode.NotFound)); });
        LyricsUiSet(fixture.Window, "_current", LyricsFollowTrack("42102")); LyricsUiOpen(fixture.Window);
        await Until(() => LyricsUiLines(fixture.Window).SequenceEqual(new[] { "Original first", "Original second" }));
        Assert(LyricsUiField<IReadOnlyList<LyricLine>>(fixture.Window, "_lyricLines")!.Count == 0
            && LyricsUiField<Uri>(fixture.Window, "_lyricsSource") is null, "Yandex plain was mislabeled synchronized or external");
        Assert(fixture.Lrclib.Seen.Count == 1 && fixture.Genius.Seen.Count == 0, "LRCLIB miss discarded valid Yandex text or started Genius");
        Assert(LyricsUiField<TextBlock>(fixture.Window, "_lyricsNote")!.Text.Contains("Яндекс", StringComparison.Ordinal), "plain Yandex source attribution was lost");
    }

    private static async Task CheckLyricsFollowProviderErrors()
    {
        foreach (var status in new[] { HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden, HttpStatusCode.ServiceUnavailable })
        {
            using var fixture = new LyricsFollowFixture("error-" + (int)status, new FakeHandler((request, _) =>
                Task.FromResult(LyricsUiYandex(request, failure: status))),
                (_, _) => throw new InvalidOperationException("Yandex error unexpectedly started LRCLIB"));
            var track = LyricsFollowTrack("42103-" + (int)status);
            LyricsUiSet(fixture.Window, "_current", track); LyricsUiOpen(fixture.Window);
            await Until(() => LyricsUiField<CancellationTokenSource>(fixture.Window, "_lyricsCts") is null);
            Assert(fixture.Lrclib.Seen.Count == 0 && fixture.Genius.Seen.Count == 0, "Yandex failure bypassed provider error handling");
            Assert(!LyricsUiField<IDictionary>(fixture.Window, "_lyricsCache")!.Contains(track.Id)
                && LyricsUiLines(fixture.Window).Length == 0, "Yandex failure entered the absence cache or displayed fallback text");
            Assert(!LyricsUiField<TextBlock>(fixture.Window, "_lyricsNote")!.Text.Contains("Текст не найден", StringComparison.Ordinal), "provider error was presented as an absent song");
        }
    }

    private static async Task CheckLyricsFollowRace()
    {
        var oldTrack = LyricsFollowTrack("42104", "Old timed fixture");
        var newTrack = LyricsFollowTrack("42105", "Current timed fixture");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var returned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = 0;
        using var fixture = new LyricsFollowFixture("race", LyricsFollowYandexPlain("Yandex plain"), async (request, token) =>
        {
            LyricsFollowAnonymous(request);
            if (LyricsFollowQuery(request.RequestUri!)["track_name"] == oldTrack.Title)
            {
                using var observation = token.Register(() => Interlocked.Exchange(ref cancelled, 1));
                entered.TrySetResult();
                await release.Task; // Deliberately simulate a server completing after cancellation.
                returned.TrySetResult();
                return LyricsFollowRecord(oldTrack, "[00:00.00]Old timed response\n[00:04.00]Old second");
            }
            return LyricsFollowRecord(newTrack, "[00:00.00]Current timed response\n[00:04.00]Current second");
        });
        try
        {
            LyricsUiSet(fixture.Window, "_current", oldTrack); LyricsUiOpen(fixture.Window);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var oldCancellation = LyricsUiField<CancellationTokenSource>(fixture.Window, "_lyricsCts")!;
            LyricsUiSet(fixture.Window, "_current", newTrack); LyricsUiInvoke(fixture.Window, "RefreshLyrics");
            await Until(() => Volatile.Read(ref cancelled) == 1);
            release.TrySetResult(); // Release the old request gate so the new exact request can run.
            await returned.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Until(() => LyricsUiLines(fixture.Window).SequenceEqual(new[] { "Current timed response", "Current second" }));
            await Until(() => LyricsUiDisposed(oldCancellation));
            var cache = LyricsUiField<IDictionary>(fixture.Window, "_lyricsCache")!;
            Assert(!cache.Contains(oldTrack.Id) && cache.Contains(newTrack.Id), "late canceled LRCLIB response entered the cache");
            Assert(LyricsUiField<TextBlock>(fixture.Window, "_lyricsHeading")!.Text == newTrack.Title
                && fixture.Genius.Seen.Count == 0, "late response changed the heading or started another provider");
        }
        finally { release.TrySetResult(); }
    }

    private static async Task CheckLyricsFollowNative()
    {
        var row = LyricsFollowNativeTrack("42106", "native");
        using var fixture = new LyricsFollowFixture("native", LyricsFollowYandexPlain("Native Yandex plain"),
            (request, _) => { LyricsFollowAnonymous(request); return Task.FromResult(LyricsFollowRecord(row, LyricsFollowLongLrc())); });
        var window = fixture.Window;
        await window.PlayTrackAsync(row); LyricsUiOpen(window);
        await Until(() => LyricsUiLines(window).Length == 60);
        var player = LyricsUiField<NativePlayer>(window, "_player")!;
        var timer = LyricsUiField<DispatcherTimer>(window, "_lyricsTimer")!;
        var list = Control<ListBox>(window, "LyricsList");
        Assert(player.IsPlaying && player.SourceLocalPath == row.LocalPath && timer.IsEnabled
            && timer.Interval == TimeSpan.FromMilliseconds(100), "native lyrics timer did not follow visible playback");
        await Until(() => list.SelectedIndex >= 1);
        Assert(Math.Abs(list.SelectedIndex * .5 - player.Position.TotalSeconds) < .7, "active line did not follow the native clock");
        LyricsUiInvoke(window, "UpdateLyrics", 4.499d);
        Assert(list.SelectedIndex == 8, "line activation used an artificial lead");
        LyricsUiInvoke(window, "UpdateLyrics", 8.2d); Assert(list.SelectedIndex == 16, "forward line lookup");
        LyricsUiInvoke(window, "UpdateLyrics", 2.1d); Assert(list.SelectedIndex == 4, "backward seek retained a later active line");

        list.SelectedIndex = 10; list.ScrollIntoView(list.Items[10]); window.UpdateLayout();
        var container = list.ItemContainerGenerator.ContainerFromIndex(10) as ListBoxItem;
        Assert(container is not null, "single-click fixture row was not realized");
        var beforeClick = player.Position.TotalSeconds;
        Assert(Math.Abs(beforeClick - 5) > 2, "native fixture cannot distinguish a seek from ordinary playback");
        var click = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
            { RoutedEvent = Mouse.PreviewMouseUpEvent };
        container!.RaiseEvent(click);
        Assert(click.Handled, "production single-click lyric handler did not consume the seek");
        var pendingSeek = (double?)typeof(MainWindow).GetField("_pendingSeek", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window);
        Assert(pendingSeek == 5 && Math.Abs(Control<Slider>(window, "SeekSlider").Value - 5) < .001,
            "single-click callback did not commit the exact pending seek");
        var nativeDeadline = DateTimeOffset.UtcNow.AddSeconds(1);
        await Until(() => Math.Abs(player.Position.TotalSeconds - 5) < .3 || DateTimeOffset.UtcNow >= nativeDeadline);
        Assert(Math.Abs(player.Position.TotalSeconds - 5) < .3, "single click did not seek immediately to the exact lyric timestamp");
        await Call(window, "TogglePlaybackAsync");
        await Until(() => !player.IsPlaying && !timer.IsEnabled);
        await Call(window, "TogglePlaybackAsync");
        await Until(() => player.IsPlaying && timer.IsEnabled);
        LyricsUiOpen(window);
        Assert(!timer.IsEnabled && !Control<Grid>(window, "LyricsPanel").IsVisible && player.IsPlaying, "hidden lyrics kept its timer or stopped audio");
        fixture.Close();
        Assert(!timer.IsEnabled, "closing the window left the lyrics timer enabled");
    }

    private static async Task CheckLyricsFollowManual()
    {
        var row = LyricsFollowNativeTrack("42107", "manual");
        using var fixture = new LyricsFollowFixture("manual", LyricsFollowYandexPlain("Manual Yandex plain"),
            (request, _) => { LyricsFollowAnonymous(request); return Task.FromResult(LyricsFollowRecord(row, LyricsFollowLongLrc())); });
        var window = fixture.Window;
        await window.PlayTrackAsync(row); LyricsUiOpen(window);
        await Until(() => LyricsUiLines(window).Length == 60);
        var list = Control<ListBox>(window, "LyricsList");
        window.UpdateLayout();
        var viewer = LyricsUiVisuals<ScrollViewer>(list).First();
        Assert(viewer.ScrollableHeight > viewer.ViewportHeight, "manual follow fixture does not have enough scrollable content");
        LyricsUiInvoke(window, "PauseLyricsFollow");
        viewer.ScrollToVerticalOffset(viewer.ScrollableHeight * .7); window.UpdateLayout();
        await Dispatcher.Yield(DispatcherPriority.ContextIdle);
        var wheelSource = LyricsUiVisuals<ListBoxItem>(list).FirstOrDefault(item => item.IsVisible) ?? (DependencyObject)list;
        LyricsUiSet(window, "_lyricsFollowUntil", DateTimeOffset.MinValue);
        var beforeWheel = viewer.VerticalOffset;
        var wheel = new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, 120) { RoutedEvent = Mouse.PreviewMouseWheelEvent };
        ((UIElement)wheelSource).RaiseEvent(wheel);
        var pausedUntil = (DateTimeOffset)typeof(MainWindow).GetField("_lyricsFollowUntil", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!;
        Assert(pausedUntil - DateTimeOffset.UtcNow is { TotalMilliseconds: > 2900 and < 3700 }, "manual wheel did not defer auto-follow for the requested idle period");
        await Until(() => !SmoothScroll.IsAnimating(viewer));
        await Dispatcher.Yield(DispatcherPriority.ContextIdle);
        var manualOffset = viewer.VerticalOffset;
        Assert(Math.Abs(manualOffset - beforeWheel) > .5, "routed wheel did not produce an actual manual scroll");
        var previousIndex = list.SelectedIndex;
        LyricsUiInvoke(window, "UpdateLyrics", 11.2d);
        Assert(list.SelectedIndex == 22, "manual follow pause also paused active-line highlighting");
        await Task.Delay(250);
        Assert(Math.Abs(viewer.VerticalOffset - manualOffset) < 1.5, "a playback tick stole the manual scroll offset");
        Assert(previousIndex != 22, "manual fixture did not exercise a changed highlight");
        await Until(() => DateTimeOffset.UtcNow >= pausedUntil);
        await Until(() => Math.Abs(viewer.VerticalOffset - manualOffset) > 5);
        await Until(() => !SmoothScroll.IsAnimating(viewer));
        Assert(list.SelectedIndex >= 0, "follow resumed without an active line");

        foreach (var (section, panelName) in new[] { ("home", "HomePanel"), ("artist-detail", "ArtistPanel") })
        {
            LyricsUiInvoke(window, "SetSection", section, "Fixture context");
            if (LyricsUiField<ListBox>(window, "_lyricsList") is not null && Control<Grid>(window, "LyricsPanel").IsVisible) LyricsUiOpen(window);
            Assert(Control<Grid>(window, panelName).IsVisible, "context fixture panel was not selected");
            LyricsUiOpen(window);
            Assert(Control<Grid>(window, "LyricsPanel").IsVisible, "context did not open inline lyrics");
            LyricsUiOpen(window);
            var current = (FrameworkElement)typeof(MainWindow).GetMethod("CurrentContent", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, null)!;
            Assert(ReferenceEquals(current, Control<Grid>(window, panelName)) && current.IsVisible,
                "closing inline lyrics returned to a different content panel");
        }
        var timer = LyricsUiField<DispatcherTimer>(window, "_lyricsTimer")!;
        Assert(!timer.IsEnabled, "contextual lyrics close left its timer running");
    }

    private static TrackRow LyricsFollowTrack(string id, string title = "Follow fixture song") => new()
    { Id = id, RequestId = id + ":1", Title = title, Artist = "Follow Fixture Artist", Album = "Follow Fixture Album", DurationMs = 180000, Available = true };

    private static TrackRow LyricsFollowNativeTrack(string id, string suffix) => new()
    {
        Id = id, RequestId = id + ":1", Title = "Follow native " + suffix, Artist = "Follow Fixture Artist", Album = "Follow Fixture Album",
        DurationMs = 30000, Available = true, LocalPath = WriteWave("lyrics-follow-" + suffix + "-30s.wav", 30),
        CoverUri = "avatars.yandex.net/get-music-content/fixture/lyrics-follow/%%"
    };

    private static string LyricsFollowLongLrc() => string.Join('\n', Enumerable.Range(0, 60).Select(index =>
        "[" + TimeSpan.FromMilliseconds(index * 500).ToString(@"mm\:ss\.fff", CultureInfo.InvariantCulture)
        + "]Строка " + index + ": длинный синтетический текст для проверки выделения и ручной прокрутки"));

    private static FakeHandler LyricsFollowYandexPlain(string text) => new((request, _) => Task.FromResult(LyricsUiYandex(request, text)));
    private static HttpResponseMessage LyricsFollowRecord(TrackRow track, string lrc) => Json(JsonSerializer.Serialize(new
    { id = 1, trackName = track.Title, artistName = track.Artist, albumName = track.Album, duration = track.DurationMs / 1000d, instrumental = false, syncedLyrics = lrc }));

    private static void LyricsFollowAnonymous(HttpRequestMessage request)
        => Assert(request.Method == HttpMethod.Get && request.RequestUri is { Scheme: "https", Host: "lrclib.net", AbsolutePath: "/api/get" }
            && request.Headers.Authorization is null && !request.Headers.Contains("Cookie"), "LRCLIB fallback escaped anonymous exact GET transport");

    private static Dictionary<string, string> LyricsFollowQuery(Uri uri) => uri.Query.TrimStart('?').Split('&').Select(part => part.Split('=', 2))
        .ToDictionary(part => Uri.UnescapeDataString(part[0]), part => part.Length == 2 ? Uri.UnescapeDataString(part[1]) : "", StringComparer.Ordinal);

    private sealed class LyricsFollowFixture : IDisposable
    {
        internal MainWindow Window { get; }
        internal FakeHandler Yandex { get; }
        internal FakeHandler Lrclib { get; }
        internal FakeHandler Genius { get; }
        private readonly HttpClient _http;
        private bool _closed;
        internal LyricsFollowFixture(string suffix, FakeHandler yandex, Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> lrclib)
        {
            Yandex = yandex; Lrclib = new FakeHandler(lrclib);
            Genius = new FakeHandler((_, _) => throw new InvalidOperationException("lyrics follow fixture unexpectedly contacted Genius"));
            _http = new HttpClient(Lrclib);
            var directory = Path.Combine(_directory, "lyrics-follow-" + suffix);
            Window = new MainWindow(api: new MusicApi(Yandex), store: new SessionStore(Path.Combine(directory, "store")), restore: false,
                diagnosticsDirectory: Path.Combine(directory, "diagnostics"), offline: new OfflineLibrary(Path.Combine(directory, "offline")))
            { ShowActivated = false, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -12000, Top = 0 };
            LyricsUiSet(Window, "_geniusLyrics", new GeniusLyricsClient(Genius));
            LyricsUiField<LrclibLyricsClient>(Window, "_lrclibLyrics")?.Dispose();
            LyricsUiSet(Window, "_lrclibLyrics", new LrclibLyricsClient(_http));
            LyricsUiSet(Window, "_session", FakeSession); LyricsUiSet(Window, "_account", new Account("12345", "Lyrics follow fixture", true));
            Window.Show(); Control<Slider>(Window, "VolumeSlider").Value = 0;
        }
        internal void Close() { if (_closed) return; _closed = true; Window.Close(); }
        public void Dispose() { Close(); _http.Dispose(); }
    }
}
