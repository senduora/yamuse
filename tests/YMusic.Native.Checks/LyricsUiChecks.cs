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

        await Run("Lyrics UI: redirected Genius aliases must retain the confirmed song ID", async () =>
        {
            var finalSong = 43;
            var handler = new FakeHandler((request, _) =>
            {
                var path = request.RequestUri!.AbsolutePath;
                if (path == "/api/search/multi")
                    return Task.FromResult(Json("""{"response":{"sections":[{"type":"song","hits":[{"result":{"id":42,"title":"Fixture Song","path":"/old-fixture-song-lyrics","primary_artist":{"name":"Fixture Artist"}}}]}]}}"""));
                if (path == "/old-fixture-song-lyrics")
                {
                    var moved = new HttpResponseMessage(HttpStatusCode.MovedPermanently);
                    moved.Headers.Location = new Uri("/fixture-song-lyrics", UriKind.Relative);
                    return Task.FromResult(moved);
                }
                Assert(path == "/fixture-song-lyrics", "unexpected Genius redirect destination");
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent($"""
                        <html><head><meta property="al:ios:url" content="genius://songs/{finalSong}"></head>
                        <body><div data-lyrics-container="true">Verified lyrics fixture</div></body></html>
                        """, Encoding.UTF8, "text/html")
                });
            });
            using var client = new GeniusLyricsClient(handler);
            await Throws<MusicException>(() => client.FindAsync("Fixture Song", "Fixture Artist", default));
            finalSong = 42;
            var correct = await client.FindAsync("Fixture Song", "Fixture Artist", default);
            Assert(correct?.PlainText == "Verified lyrics fixture" && handler.Seen.Count == 6,
                "the correct canonical alias was blocked or the first result was cached");
        });

        await Run("Lyrics UI: Genius preserves distinct letters and accepts canonical spelling", async () =>
        {
            var title = "мои";
            var artist = "Fixture Artist";
            var handler = new FakeHandler((request, _) =>
            {
                if (request.RequestUri!.AbsolutePath == "/api/search/multi")
                    return Task.FromResult(Json(JsonSerializer.Serialize(new
                    {
                        response = new { sections = new[] { new { type = "song", hits = new[] { new { result = new
                        {
                            title, path = "/fixture-song-lyrics", primary_artist = new { name = artist }
                        } } } } } }
                    })));
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent("<div data-lyrics-container=true>Fixture text</div>", Encoding.UTF8, "text/html") });
            });
            using var client = new GeniusLyricsClient(handler);
            Assert(await client.FindAsync("мой", "Fixture Artist", default) is null && handler.Seen.Count == 1,
                "a distinct letter selected another song");
            title = "мои\u0306";
            var match = await client.FindAsync("мой", "Fixture Artist", default, refresh: true);
            Assert(match?.PlainText == "Fixture text" && handler.Seen.Count == 3,
                "the canonically equivalent spelling failed to match");

            title = "φως";
            artist = "τέλος";
            match = await client.FindAsync("ΦΩΣ", "ΤΈΛΟΣ", default);
            Assert(match?.PlainText == "Fixture text" && handler.Seen.Count == 5,
                "Greek final sigma prevented the equivalent title and artist from reaching the song page");

            artist = "Fixture Artist";
            title = "Ритм (Clean)";
            Assert(await client.FindAsync("Ритм", artist, default) is null && handler.Seen.Count == 6,
                "a clean edition matched the title of the original track");
            match = await client.FindAsync("Ритм (Clean)", artist, default);
            Assert(match?.PlainText == "Fixture text" && handler.Seen.Count == 8, "the exact clean edition failed to match");
            title = "Ритм (Rhythm)";
            match = await client.FindAsync("Ритм", artist, default, refresh: true);
            Assert(match?.PlainText == "Fixture text" && handler.Seen.Count == 10, "a valid translation suffix stopped matching");
        });

        await Run("Lyrics UI: malformed Genius song sections are retryable without hiding a valid match", async () =>
        {
            foreach (var (name, malformed, isHit) in new[]
            {
                ("non-object-section", "null", false), ("missing-section-type", """{"hits":[]}""", false),
                ("bad-section-type", """{"type":42,"hits":[]}""", false),
                ("no-hits", """{"type":"song"}""", false), ("null-hits", """{"type":"song","hits":null}""", false),
                ("wrong-hits", """{"type":"song","hits":{}}""", false), ("null-hit", "null", true),
                ("null-result", """{"type":"song","result":null}""", true),
                ("missing-artist", """{"type":"song","result":{"title":"Fixture Song"}}""", true),
                ("bad-title", """{"result":{"title":42,"primary_artist":{"name":"Fixture Artist"}}}""", true),
                ("bad-address", """{"result":{"title":"Fixture Song","url":"unavailable-fixture-name","primary_artist":{"name":"Fixture Artist"}}}""", true)
            })
            {
                var searches = 0;
                var yandex = new FakeHandler((request, _) => Task.FromResult(LyricsUiYandex(request)));
                var genius = new FakeHandler((request, _) =>
                {
                    if (request.RequestUri!.AbsolutePath == "/api/search/multi")
                    {
                        const string validHit = """{"result":{"title":"Fixture Song","path":"/fixture-song-lyrics","primary_artist":{"name":"Fixture Artist"}}}""";
                        var recovered = ++searches > 1;
                        var sections = isHit
                            ? "{\"type\":\"song\",\"hits\":[" + malformed + (recovered ? "," + validHit : "") + "]}"
                            : malformed + (recovered ? ",{\"type\":\"song\",\"hits\":[" + validHit + "]}" : "");
                        return Task.FromResult(Json("{\"response\":{\"sections\":[" + sections + "]}}"));
                    }
                    Assert(request.RequestUri.AbsolutePath == "/fixture-song-lyrics", "unexpected Genius song page");
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    { Content = new StringContent("<div data-lyrics-container=true>Recovered Genius text</div>") });
                });
                var window = LyricsUiWindow("genius-uncertain-" + name, yandex, genius);
                try
                {
                    var track = LyricsUiTrack("41024");
                    LyricsUiSet(window, "_current", track);
                    LyricsUiOpen(window);
                    await Until(() => LyricsUiField<CancellationTokenSource>(window, "_lyricsCts") is null);
                    Assert(Control<TextBlock>(window, "LyricsNote").Text.Contains("Genius вернул неподдерживаемую выдачу", StringComparison.Ordinal)
                        && searches == 1 && genius.Seen.Count == 1 && !LyricsUiCached(window, track),
                        "a malformed song section or hit was treated as a cacheable absence");

                    LyricsUiOpen(window); LyricsUiOpen(window);
                    await Until(() => LyricsUiLines(window).SequenceEqual(["Recovered Genius text"]));
                    Assert(searches == 2 && genius.Seen.Count == 3 && LyricsUiCached(window, track),
                        "the valid sibling did not recover on ordinary reopening");
                }
                finally { window.Close(); }
            }
        });

        await Run("Lyrics UI: numeric and string Genius errors are retryable in both caches", async () =>
        {
            foreach (var status in new[] { "503", "\"503\"" })
            {
                var searches = 0;
                var yandex = new FakeHandler((request, _) => Task.FromResult(LyricsUiYandex(request)));
                var genius = new FakeHandler((request, _) =>
                {
                    if (request.RequestUri!.AbsolutePath == "/api/search/multi")
                    {
                        var body = ++searches == 1
                            ? "{\"meta\":{\"status\":" + status + "},\"response\":{\"sections\":[{\"type\":\"song\",\"hits\":[]}]}}"
                            : """{"meta":{"status":"200"},"response":{"sections":[{"type":"song","hits":[{"result":{"title":"Fixture Song","path":"/fixture-song-lyrics","primary_artist":{"name":"Fixture Artist"}}}]}]}}""";
                        return Task.FromResult(Json(body));
                    }
                    Assert(request.RequestUri.AbsolutePath == "/fixture-song-lyrics", "unexpected Genius song page");
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    { Content = new StringContent("<div data-lyrics-container=true>Recovered Genius text</div>") });
                });
                var window = LyricsUiWindow("genius-error-" + (status.Length == 3 ? "number" : "string"), yandex, genius);
                try
                {
                    var track = LyricsUiTrack("41028");
                    LyricsUiSet(window, "_current", track);
                    LyricsUiOpen(window);
                    await Until(() => LyricsUiField<CancellationTokenSource>(window, "_lyricsCts") is null);
                    Assert(Control<TextBlock>(window, "LyricsNote").Text.Contains("HTTP 503", StringComparison.Ordinal)
                        && searches == 1 && !LyricsUiCached(window, track), "Genius error was cached as an absent song");
                    LyricsUiOpen(window); LyricsUiOpen(window);
                    await Until(() => LyricsUiLines(window).SequenceEqual(["Recovered Genius text"]));
                    Assert(searches == 2 && genius.Seen.Count == 3 && LyricsUiCached(window, track),
                        "Genius recovery did not reach the search and page after ordinary reopening");
                }
                finally { window.Close(); }
            }
        });

        await Run("Lyrics UI: empty successful Genius song page retries on ordinary reopening", async () =>
        {
            var pages = 0;
            var yandex = new FakeHandler((request, _) => Task.FromResult(LyricsUiYandex(request)));
            var genius = new FakeHandler((request, _) => Task.FromResult(request.RequestUri!.AbsolutePath == "/api/search/multi"
                ? Json("""{"response":{"sections":[{"type":"song","hits":[{"result":{"title":"Fixture Song","path":"/fixture-song-lyrics","primary_artist":{"name":"Fixture Artist"}}}]}]}}""")
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(++pages == 1 ? " \r\n"
                    : "<div data-lyrics-container=true>Recovered Genius text</div>") }));
            var window = LyricsUiWindow("genius-empty-page", yandex, genius);
            try
            {
                var track = LyricsUiTrack("41025");
                LyricsUiSet(window, "_current", track);
                LyricsUiOpen(window);
                await Until(() => LyricsUiField<CancellationTokenSource>(window, "_lyricsCts") is null);
                Assert(Control<TextBlock>(window, "LyricsNote").Text.Contains("Genius вернул пустую страницу", StringComparison.Ordinal)
                    && pages == 1 && !LyricsUiCached(window, track), "a blank successful song page was cached as absent lyrics");

                LyricsUiOpen(window); LyricsUiOpen(window);
                await Until(() => LyricsUiLines(window).SequenceEqual(["Recovered Genius text"]));
                Assert(pages == 2 && LyricsUiCached(window, track), "ordinary reopening did not recover the Genius page");
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

                await Call(window, "ActivateSessionAsync", FakeSession, default(CancellationToken), true, false);
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
            foreach (var status in new[] { HttpStatusCode.Forbidden, HttpStatusCode.RequestTimeout, HttpStatusCode.ServiceUnavailable })
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

        await Run("Lyrics UI: structured Yandex errors retry without fallback while confirmed absence does fall back", async () =>
        {
            foreach (var (shape, supplementFailure, error) in new[]
            {
                ("root", false, """{"error":{"name":"fixture"},"result":{"downloadUrl":"https://fixture.yandex.net/ignored"}}"""),
                ("nested", false, """{"result":{"success":false,"downloadUrl":"https://fixture.yandex.net/ignored"}}"""),
                ("supplement", true, """{"success":false,"result":{"lyrics":null}}"""),
                ("invalid-json", false, "{ unfinished"),
                ("supplement-array", true, """{"result":{"lyrics":[]}}"""),
                ("supplement-number", true, """{"result":{"lyrics":{"hasRights":true,"fullLyrics":42}}}"""),
                ("rendition-address", false, """{"result":{"downloadUrl":"relative-fixture-name"}}"""),
                ("rendition-address-number", false, """{"result":{"downloadUrl":42}}""")
            })
            {
                var recovered = false;
                var yandex = new FakeHandler((request, _) =>
                {
                    var uri = request.RequestUri!;
                    if (uri.AbsolutePath == "/recovered-text")
                        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("Recovered fixture text") });
                    Assert(uri.AbsolutePath != "/ignored", "an error envelope supplied a download address");
                    if (uri.AbsolutePath.StartsWith("/tracks/41018/", StringComparison.Ordinal))
                    {
                        var supplement = uri.AbsolutePath.EndsWith("/supplement", StringComparison.Ordinal);
                        if (!recovered && supplementFailure == supplement) return Task.FromResult(Json(error));
                        if (recovered && uri.Query.Contains("format=TEXT", StringComparison.Ordinal))
                            return Task.FromResult(Json("""{"result":{"downloadUrl":"https://fixture.yandex.net/recovered-text"}}"""));
                    }
                    return Task.FromResult(LyricsUiYandex(request));
                });
                var genius = new FakeHandler((_, _) => Task.FromResult(Json("""{"response":{"sections":[{"type":"song","hits":[]}]}}""")));
                var lrclib = new FakeHandler((_, _) => Task.FromResult(Json("{}", HttpStatusCode.NotFound)));
                using var lrclibHttp = new HttpClient(lrclib);
                var window = LyricsUiWindow("uncertain-" + shape, yandex, genius);
                try
                {
                    LyricsUiField<LrclibLyricsClient>(window, "_lrclibLyrics")?.Dispose();
                    LyricsUiSet(window, "_lrclibLyrics", new LrclibLyricsClient(lrclibHttp));
                    var track = LyricsUiTrack("41018");
                    LyricsUiSet(window, "_current", track);
                    LyricsUiOpen(window);
                    await Until(() => LyricsUiField<CancellationTokenSource>(window, "_lyricsCts") is null);
                    var note = Control<TextBlock>(window, "LyricsNote");
                    Assert(note.Text.Contains("Не удалось загрузить текст", StringComparison.Ordinal) && LyricsUiLines(window).Length == 0
                        && !LyricsUiCached(window, track) && lrclib.Seen.Count == 0 && genius.Seen.Count == 0
                        && yandex.Seen.Count == 3 && yandex.Seen.All(request => request.Uri.Host == "api.music.yandex.net"),
                        "a structured error started the absence fallback or entered the cache");

                    recovered = true;
                    LyricsUiOpen(window); LyricsUiOpen(window);
                    await Until(() => LyricsUiLines(window).SequenceEqual(["Recovered fixture text"]));
                    Assert(LyricsUiCached(window, track) && genius.Seen.Count == 0, "ordinary reopening missed the recovered response");

                    var missing = LyricsUiTrack("41019");
                    LyricsUiSet(window, "_current", missing);
                    LyricsUiInvoke(window, "RefreshLyrics");
                    await Until(() => note.Text.Contains("Текст не найден в Яндекс Музыке и Genius", StringComparison.Ordinal));
                    Assert(lrclib.Seen.Count == 2 && genius.Seen.Count == 1 && LyricsUiCached(window, missing),
                        "confirmed 404 and an empty supplement no longer reach or cache the normal fallback");
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
        var media = SilentMp3Seconds(1);
        using var handler = new FakeHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(media) }));
        var music = Path.Combine(directory, "Music");
        using (var writer = new OfflineLibrary(music, new AudioBuffer(Path.Combine(directory, "buffer"), handler)))
        {
            await writer.SaveAsync(track, new(new Uri("https://s1.vlx.yandex.net/primary-artist-fixture.mp3"), "mp3", 192), default);
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
    private static MouseButtonEventArgs LyricsUiMouse(ListBoxItem row, bool down)
    {
        var args = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
        { RoutedEvent = down ? Mouse.PreviewMouseDownEvent : Mouse.PreviewMouseUpEvent };
        row.RaiseEvent(args);
        return args;
    }
    private static T? LyricsUiField<T>(MainWindow window, string name) where T : class => (T?)typeof(MainWindow).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window);
    private static void LyricsUiSet(MainWindow window, string name, object value) => typeof(MainWindow).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(window, value);
    private static void LyricsUiInvoke(MainWindow window, string name, params object[] arguments) => typeof(MainWindow).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, arguments);
    private static void LyricsUiAssignPending(MainWindow window, CancellationTokenSource? request) =>
        typeof(MainWindow).GetMethod("SetLyricsRequest", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(window, [LyricsUiField<TrackRow>(window, "_current"), request]);
    private static bool LyricsUiCached(MainWindow window, TrackRow track)
    {
        var key = typeof(MainWindow).GetMethod("LyricsCacheIdentity", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, [track]);
        return LyricsUiField<IDictionary>(window, "_lyricsCache")!.Contains(key!);
    }
    private static string[] LyricsUiLines(MainWindow window) => LyricsUiField<ListBox>(window, "_lyricsList")?.Items.Cast<object>().Select(value => value.ToString() ?? "").ToArray() ?? [];
    private static int LyricsActiveIndex(MainWindow window) => Toast040Field<int>(window, "_activeLyricIndex");
    private static bool LyricsRowIsCurrent(object row) => row.GetType().GetProperty("IsCurrent")?.GetValue(row) is true;
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
