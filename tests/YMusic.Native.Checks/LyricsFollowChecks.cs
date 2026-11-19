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
using System.Windows.Interop;
using System.Windows.Media;
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
            await Run("Lyrics follow: transient LRCLIB fallback retries and applies the paused restored position", () => CheckLyricsFollowRetryAndResume(unreadable: false));
            await Run("Lyrics follow: unreadable LRCLIB text retries on ordinary reopening", () => CheckLyricsFollowRetryAndResume(unreadable: true));
            await Run("Lyrics follow: transient Yandex timing retries after a plain fallback", () => CheckLyricsFollowYandexRetry(HttpStatusCode.RequestTimeout));
            await Run("Lyrics follow: temporary Yandex LRC failure retries after synchronized LRCLIB fallback",
                () => CheckLyricsFollowYandexRetry(HttpStatusCode.ServiceUnavailable, syncedFallback: true));
            await Run("Lyrics follow: partial Yandex LRC cannot cache a successful TEXT fallback", CheckLyricsFollowYandexPartial);
            await Run("Lyrics follow: unreadable advertised Yandex LRC cannot cache a successful TEXT fallback", CheckLyricsFollowYandexCdnFailure);
            await Run("Lyrics follow: temporary TEXT failure cannot cache a supplement excerpt", () => CheckLyricsFollowTextRetry(HttpStatusCode.ServiceUnavailable));
            await Run("Lyrics follow: format-specific Yandex refusal retries available fallbacks", async () =>
            {
                await CheckLyricsFollowYandexRetry(HttpStatusCode.Forbidden);
                await CheckLyricsFollowTextRetry(HttpStatusCode.Forbidden);
            });
            await Run("Lyrics follow: supplement removes BOM before choosing an excerpt or error", CheckLyricsFollowSupplementBom);
            await Run("Lyrics follow: distinct editions cannot share the current-track lyrics cache", CheckLyricsFollowEditions);
            await Run("Lyrics follow: lyrics cache follows the exact effective metadata", CheckLyricsFollowMetadata);
            await Run("Lyrics follow: removing the paused current track cancels the old lyrics and cues the replacement", CheckLyricsFollowQueueCue);
            await Run("Lyrics follow: Yandex authentication/access/server errors never start another provider", CheckLyricsFollowProviderErrors);
            await Run("Lyrics follow: canceled late LRCLIB cannot replace a new track or enter its cache", CheckLyricsFollowRace);
            await Run("Lyrics follow: native clock, exact single click and backward seek, visible playback timer", CheckLyricsFollowNative);
            await Run("Lyrics follow: inline mouse gesture owns one track and row", () => CheckLyricsMouseOwnership(expanded: false));
            await Run("Lyrics follow: fullscreen mouse gesture owns one track and row", () => CheckLyricsMouseOwnership(expanded: true));
            await Run("Lyrics follow: a seek before the first timestamp cancels both deferred centers", CheckLyricsSeekBeforeFirstTimestamp);
            await Run("Lyrics follow: first reveal of cached paused lyrics makes the current line visible", CheckFirstInlineLyricsReveal);
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
        await fixture.Window.PlayTrackAsync(LyricsFollowNativeTrack("42102", "plain"));
        LyricsUiOpen(fixture.Window);
        await Until(() => LyricsUiLines(fixture.Window).SequenceEqual(new[] { "Original first", "Original second" }));
        Assert(LyricsUiField<IReadOnlyList<LyricLine>>(fixture.Window, "_lyricLines")!.Count == 0
            && LyricsUiField<Uri>(fixture.Window, "_lyricsSource") is null, "Yandex plain was mislabeled synchronized or external");
        Assert(fixture.Lrclib.Seen.Count == 1 && fixture.Genius.Seen.Count == 0, "LRCLIB miss discarded valid Yandex text or started Genius");
        Assert(LyricsUiField<NativePlayer>(fixture.Window, "_player")!.IsPlaying
            && !LyricsUiField<DispatcherTimer>(fixture.Window, "_lyricsTimer")!.IsEnabled,
            "the visible plain text started a dedicated lyrics clock");
        Assert(LyricsUiField<TextBlock>(fixture.Window, "_lyricsNote")!.Text.Contains("Яндекс", StringComparison.Ordinal), "plain Yandex source attribution was lost");
    }

    private static async Task CheckLyricsFollowRetryAndResume(bool unreadable)
    {
        var track = LyricsFollowTrack("42109");
        var requests = 0;
        var record = LyricsFollowRecordBody(track, "[00:00]First\n[00:04]Second\n[00:08]Third");
        using var fixture = new LyricsFollowFixture("retry-and-resume-" + unreadable, LyricsFollowYandexPlain("Original Yandex plain"), (_, _) =>
            Task.FromResult(++requests > 1 ? Json(record) : unreadable
                ? Json(LrclibUnreadableField(record, "syncedLyrics")) : Json("{}", HttpStatusCode.ServiceUnavailable)));
        var window = fixture.Window;
        var queue = LyricsUiField<PlaybackQueue>(window, "_playbackQueue")!;
        queue.SetItems([track]);
        LyricsUiSet(window, "_current", track);
        LyricsUiSet(window, "_restoredPlaybackOccurrence", queue.CurrentOccurrenceToken!.Value);
        LyricsUiSet(window, "_restoredPlaybackSeconds", 9d);
        LyricsUiOpen(window);
        await Until(() => LyricsUiLines(window).SequenceEqual(new[] { "Original Yandex plain" }));
        Assert(!LyricsUiCached(window, track), "temporary LRCLIB failure cached the plain fallback");

        LyricsUiOpen(window); LyricsUiOpen(window);
        await Until(() => LyricsUiLines(window).SequenceEqual(new[] { "First", "Second", "Third" }));
        Assert(requests == 2 && !LyricsUiField<NativePlayer>(window, "_player")!.HasSource
            && LyricsActiveIndex(window) == 2, "timed lyrics missed the saved paused position or opened media");
    }

    private static async Task CheckLyricsFollowYandexRetry(HttpStatusCode failure, bool syncedFallback = false)
    {
        var yandexLrc = 0;
        var track = LyricsFollowTrack("42110");
        using var fixture = new LyricsFollowFixture("yandex-retry-" + (int)failure + "-" + syncedFallback, new FakeHandler((request, _) =>
        {
            var uri = request.RequestUri!;
            if (uri.AbsolutePath == "/fixture-timed")
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[00:00]Recovered timed fixture") });
            if (uri.Query.Contains("format=LRC", StringComparison.Ordinal))
                return Task.FromResult(++yandexLrc == 1 ? Json("{}", failure)
                    : Json("""{"result":{"downloadUrl":"https://fixture.yandex.net/fixture-timed"}}"""));
            if (uri.AbsolutePath == "/fixture-plain")
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("Original Yandex plain") });
            Assert(uri.Query.Contains("format=TEXT", StringComparison.Ordinal), "fixture did not reach the modern plain-text fallback");
            return Task.FromResult(Json("""{"result":{"downloadUrl":"https://fixture.yandex.net/fixture-plain"}}"""));
        }), (_, _) => Task.FromResult(syncedFallback ? LyricsFollowRecord(track, "[00:00]Timed LRCLIB fixture")
            : Json("{}", HttpStatusCode.NotFound)));
        var window = fixture.Window;
        LyricsUiSet(window, "_current", track);
        LyricsUiOpen(window);
        var fallback = syncedFallback ? "Timed LRCLIB fixture" : "Original Yandex plain";
        await Until(() => LyricsUiLines(window).SequenceEqual([fallback]));
        Assert(!LyricsUiCached(window, track), "temporary Yandex timing failure cached the fallback");

        LyricsUiOpen(window); LyricsUiOpen(window);
        await Until(() => LyricsUiLines(window).SequenceEqual(["Recovered timed fixture"]));
        Assert(yandexLrc == 2 && fixture.Lrclib.Seen.Count == 1 && LyricsUiCached(window, track),
            "LRC recovery stayed blocked or was not cached after success");
    }

    private static async Task CheckLyricsFollowYandexPartial()
    {
        foreach (var malformedRange in new[] { false, true })
        {
            var lrcRequests = 0;
            using var fixture = new LyricsFollowFixture("yandex-partial-" + malformedRange, new FakeHandler((request, _) =>
            {
                var uri = request.RequestUri!;
                if (uri.AbsolutePath == "/fixture-timed")
                {
                    var partial = ++lrcRequests == 1;
                    var reply = new HttpResponseMessage(partial && !malformedRange ? HttpStatusCode.PartialContent : HttpStatusCode.OK)
                    { Content = new StringContent("[00:00]Recovered timed text") };
                    if (partial && malformedRange)
                        Assert(reply.Content.Headers.TryAddWithoutValidation("Content-Range", "invalid fixture range"), "raw range fixture was not installed");
                    return Task.FromResult(reply);
                }
                if (uri.AbsolutePath == "/fixture-plain")
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("Original Yandex TEXT") });
                return Task.FromResult(uri.Query.Contains("format=LRC", StringComparison.Ordinal)
                    ? Json("""{"result":{"downloadUrl":"https://fixture.yandex.net/fixture-timed"}}""")
                    : Json("""{"result":{"downloadUrl":"https://fixture.yandex.net/fixture-plain"}}"""));
            }), (_, _) => Task.FromResult(Json("{}", HttpStatusCode.NotFound)));
            var track = LyricsFollowTrack("42112");
            var window = fixture.Window;
            LyricsUiSet(window, "_current", track);
            LyricsUiOpen(window);
            await Until(() => LyricsUiLines(window).SequenceEqual(["Original Yandex TEXT"]));
            Assert(!LyricsUiCached(window, track), "incomplete LRC made the TEXT fallback sticky");

            LyricsUiOpen(window); LyricsUiOpen(window);
            await Until(() => LyricsUiLines(window).SequenceEqual(["Recovered timed text"]));
            Assert(lrcRequests == 2 && LyricsUiCached(window, track), "completed LRC was not retried or cached");
        }
    }

    private static async Task CheckLyricsFollowYandexCdnFailure()
    {
        foreach (var html in new[] { true, false })
        {
            var lrcRequests = 0;
            using var fixture = new LyricsFollowFixture("advertised-lrc-" + html, new FakeHandler((request, _) =>
            {
                var uri = request.RequestUri!;
                if (uri.AbsolutePath == "/fixture-timed")
                {
                    if (++lrcRequests > 1)
                        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[00:00]Recovered timed text") });
                    return Task.FromResult(html
                        ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html><body>Fixture unavailable</body></html>", System.Text.Encoding.UTF8, "text/html") }
                        : Json("{}", HttpStatusCode.NotFound));
                }
                if (uri.AbsolutePath == "/fixture-plain")
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("Original Yandex TEXT") });
                return Task.FromResult(uri.Query.Contains("format=LRC", StringComparison.Ordinal)
                    ? Json("""{"result":{"downloadUrl":"https://fixture.yandex.net/fixture-timed"}}""")
                    : Json("""{"result":{"downloadUrl":"https://fixture.yandex.net/fixture-plain"}}"""));
            }), (_, _) => Task.FromResult(Json("{}", HttpStatusCode.NotFound)));
            var track = LyricsFollowTrack("42124");
            var window = fixture.Window;
            LyricsUiSet(window, "_current", track);
            LyricsUiOpen(window);
            await Until(() => LyricsUiLines(window).SequenceEqual(["Original Yandex TEXT"]));
            Assert(lrcRequests == 1 && !LyricsUiCached(window, track), "failed advertised rendition was treated as a permanent miss");

            LyricsUiOpen(window); LyricsUiOpen(window);
            await Until(() => LyricsUiLines(window).SequenceEqual(["Recovered timed text"]));
            Assert(lrcRequests == 2 && LyricsUiCached(window, track), "reopening did not repeat the advertised rendition");
        }
    }

    private static async Task CheckLyricsFollowTextRetry(HttpStatusCode failure)
    {
        var textRequests = 0;
        using var fixture = new LyricsFollowFixture("text-retry-" + (int)failure, new FakeHandler((request, _) =>
        {
            var uri = request.RequestUri!;
            if (uri.AbsolutePath == "/fixture-recovered-text")
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent("Recovered full text") });
            if (uri.AbsolutePath.EndsWith("/supplement", StringComparison.Ordinal))
                return Task.FromResult(Json("""{"result":{"lyrics":{"hasRights":false,"lyrics":"Temporary excerpt"}}}"""));
            if (uri.Query.Contains("format=TEXT", StringComparison.Ordinal))
                return Task.FromResult(++textRequests == 1 ? Json("{}", failure)
                    : Json("""{"result":{"downloadUrl":"https://fixture.yandex.net/fixture-recovered-text"}}"""));
            return Task.FromResult(Json("{}", HttpStatusCode.NotFound));
        }), (_, _) => Task.FromResult(Json("{}", HttpStatusCode.NotFound)));
        var track = LyricsFollowTrack("42113");
        var window = fixture.Window;
        LyricsUiSet(window, "_current", track);
        LyricsUiOpen(window);
        await Until(() => LyricsUiLines(window).SequenceEqual(["Temporary excerpt"]));
        Assert(!LyricsUiCached(window, track), "a transient TEXT failure pinned an excerpt");
        LyricsUiOpen(window); LyricsUiOpen(window);
        await Until(() => LyricsUiLines(window).SequenceEqual(["Recovered full text"]));
        Assert(textRequests == 2 && LyricsUiCached(window, track), "ordinary reopen did not recover the full text");
    }

    private static async Task CheckLyricsFollowSupplementBom()
    {
        foreach (var hasExcerpt in new[] { true, false })
        {
            var supplement = hasExcerpt
                ? """{"result":{"lyrics":{"hasRights":true,"fullLyrics":"\uFEFF \r\n","lyrics":"\uFEFFUsable excerpt"}}}"""
                : """{"result":{"lyrics":{"hasRights":true,"fullLyrics":"\uFEFF \r\n","lyrics":"\uFEFF \t"}}}""";
            using var fixture = new LyricsFollowFixture("supplement-bom-" + hasExcerpt, new FakeHandler((request, _) =>
            {
                var uri = request.RequestUri!;
                if (uri.AbsolutePath.EndsWith("/supplement", StringComparison.Ordinal)) return Task.FromResult(Json(supplement));
                return Task.FromResult(Json("{}", !hasExcerpt && uri.Query.Contains("format=LRC", StringComparison.Ordinal)
                    ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.NotFound));
            }), (_, _) => Task.FromResult(Json("{}", HttpStatusCode.NotFound)));
            var track = LyricsFollowTrack("42125");
            var window = fixture.Window;
            LyricsUiSet(window, "_current", track);
            LyricsUiOpen(window);
            await Until(() => LyricsUiField<CancellationTokenSource>(window, "_lyricsCts") is null);
            var note = Control<TextBlock>(window, "LyricsNote").Text;
            if (hasExcerpt)
            {
                Assert(LyricsUiLines(window).SequenceEqual(["Usable excerpt"]) && fixture.Lrclib.Seen.Count == 1
                    && note.Contains("Полнота доступного обычного текста", StringComparison.Ordinal),
                    "a BOM-only full fragment hid the normalized, explicitly partial excerpt");
            }
            else
            {
                Assert(LyricsUiLines(window).Length == 0 && !LyricsUiCached(window, track) && fixture.Lrclib.Seen.Count == 0
                    && note.Contains("Не удалось загрузить текст", StringComparison.Ordinal),
                    "empty normalized supplement fragments hid the earlier provider error");
            }
            Assert(fixture.Genius.Seen.Count == 0, "supplement BOM handling started external fallback");
        }
    }

    private static async Task CheckLyricsFollowEditions()
    {
        TrackRow Edition(string album) => new() { Id = "42111", RequestId = "42111:" + album, AlbumId = album,
            Album = album, Title = "Edition fixture", Artist = "Fixture Artist", DurationMs = 180000, Available = true };
        var first = Edition("7"); var second = Edition("8");
        using var fixture = new LyricsFollowFixture("editions", LyricsFollowYandexPlain("Original Yandex plain"), (request, _) =>
        {
            var track = LyricsFollowQuery(request.RequestUri!)["album_name"] == first.Album ? first : second;
            return Task.FromResult(LyricsFollowRecord(track, "[00:01]Edition " + track.Album));
        });
        var window = fixture.Window;
        LyricsUiSet(window, "_current", first);
        LyricsUiOpen(window);
        await Until(() => LyricsUiLines(window).SequenceEqual(["Edition 7"]));
        LyricsUiSet(window, "_current", second);
        LyricsUiInvoke(window, "RefreshLyrics");
        await Until(() => LyricsUiLines(window).SequenceEqual(["Edition 8"]));
        LyricsUiSet(window, "_current", first);
        LyricsUiInvoke(window, "RefreshLyrics");
        await Until(() => LyricsUiLines(window).SequenceEqual(["Edition 7"]));
        Assert(fixture.Lrclib.Seen.Count == 2 && LyricsUiCached(window, first) && LyricsUiCached(window, second),
            "edition lookup reused or displaced a sibling result");
    }

    private static async Task CheckLyricsFollowMetadata()
    {
        (string Title, string Artist, string Primary, long Duration)[] variants = { ("Song?", "Primary, Guest", "Primary", 180000L),
            ("Song!", "Primary, Guest", "Primary", 180000L), ("Song!", "Primary, Ensemble, Guest", "Primary", 180000L),
            ("Song!", "Primary, Ensemble, Guest", "Primary, Ensemble", 180000L), ("Song!", "Primary, Ensemble, Guest", "Primary, Ensemble", 198000L) };
        var rows = variants.Select(variant => new TrackRow { Id = "42113", RequestId = "42113:8", AlbumId = "8", Album = "Fixture Album",
            Title = variant.Title, Artist = variant.Artist, PrimaryArtistName = variant.Primary, DurationMs = variant.Duration, Available = true }).ToArray();
        var expected = rows[0];
        var attempts = 0;
        using var fixture = new LyricsFollowFixture("cache-effective-metadata", LyricsFollowYandexPlain("Original Yandex plain"), (request, _) =>
        {
            var query = LyricsFollowQuery(request.RequestUri!);
            Assert(query["track_name"] == expected.Title && query["artist_name"] == expected.Artist
                && query["duration"] == (expected.DurationMs / 1000).ToString(CultureInfo.InvariantCulture), "lookup used stale effective metadata");
            return Task.FromResult(LyricsFollowRecord(expected, "[00:00]Fresh metadata " + ++attempts));
        });
        var window = fixture.Window;
        for (var i = 0; i < rows.Length; i++)
        {
            expected = rows[i];
            LyricsUiSet(window, "_current", expected);
            if (i == 0) LyricsUiOpen(window); else LyricsUiInvoke(window, "RefreshLyrics");
            var expectedLine = "Fresh metadata " + (i + 1);
            await Until(() => LyricsUiLines(window).SequenceEqual([expectedLine]));
            Assert(LyricsUiCached(window, expected), "fresh metadata did not obtain its own stable cache entry");
        }
        Assert(attempts == rows.Length, "unchanged IDs caused sibling lookup results to be reused");
    }

    private static async Task CheckLyricsFollowQueueCue()
    {
        var previous = LyricsFollowTrack("42114", "Previous lyrics fixture");
        var next = LyricsFollowTrack("42115", "Cued lyrics fixture");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var fixture = new LyricsFollowFixture("paused-queue-cue", new FakeHandler(async (request, _) =>
        {
            if (request.RequestUri!.AbsolutePath == "/tracks/42114/lyrics")
            {
                entered.TrySetResult();
                await release.Task;
            }
            return LyricsUiYandex(request, "Replacement lyrics");
        }), (_, _) => Task.FromResult(Json("{}", HttpStatusCode.NotFound)));
        try
        {
            var window = fixture.Window;
            var queue = LyricsUiField<PlaybackQueue>(window, "_playbackQueue")!;
            queue.SetItems([previous, next], previous);
            LyricsUiSet(window, "_current", previous);
            LyricsUiInvoke(window, "ShowQueueCore", false);
            LyricsUiOpen(window);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var oldRequest = LyricsUiField<CancellationTokenSource>(window, "_lyricsCts")!;

            await Call(window, "RemoveSelectedQueueAsync");
            Assert(oldRequest.IsCancellationRequested && ReferenceEquals(queue.Current, next)
                && Control<TextBlock>(window, "LyricsHeading").Text == next.Title
                && !LyricsUiField<NativePlayer>(window, "_player")!.HasSource,
                "paused queue removal left the old request, heading, or playback active");
            await Until(() => LyricsUiLines(window).SequenceEqual(["Replacement lyrics"]));
            release.TrySetResult();
            await Until(() => LyricsUiDisposed(oldRequest));
            Assert(Control<Grid>(window, "LyricsPanel").IsVisible && LyricsUiCached(window, next) && !LyricsUiCached(window, previous),
                "late lyrics from the removed occurrence escaped the new ownership");
        }
        finally { release.TrySetResult(); }
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
            Assert(!LyricsUiCached(fixture.Window, track)
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
            LyricsUiOpen(fixture.Window); LyricsUiOpen(fixture.Window);
            Assert(ReferenceEquals(oldCancellation, LyricsUiField<CancellationTokenSource>(fixture.Window, "_lyricsCts")),
                "opening the panel again restarted the current track's existing request");
            LyricsUiOpen(fixture.Window);
            LyricsUiSet(fixture.Window, "_current", newTrack);
            LyricsUiOpen(fixture.Window);
            await Until(() => Volatile.Read(ref cancelled) == 1);
            release.TrySetResult(); // Release the old request gate so the new exact request can run.
            await returned.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Until(() => LyricsUiLines(fixture.Window).SequenceEqual(new[] { "Current timed response", "Current second" }));
            await Until(() => LyricsUiDisposed(oldCancellation));
            Assert(!LyricsUiCached(fixture.Window, oldTrack) && LyricsUiCached(fixture.Window, newTrack), "late canceled LRCLIB response entered the cache");
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
        await Until(() => LyricsActiveIndex(window) >= 1);
        Assert(Math.Abs(LyricsActiveIndex(window) * .5 - player.Position.TotalSeconds) < .7, "active line did not follow the native clock");
        LyricsUiInvoke(window, "UpdateLyrics", 4.499d);
        Assert(LyricsActiveIndex(window) == 8, "line activation used an artificial lead");
        LyricsUiInvoke(window, "UpdateLyrics", 8.2d); Assert(LyricsActiveIndex(window) == 16, "forward line lookup");
        LyricsUiInvoke(window, "UpdateLyrics", 2.1d); Assert(LyricsActiveIndex(window) == 4, "backward seek retained a later active line");

        list.SelectedIndex = 10; list.ScrollIntoView(list.Items[10]); window.UpdateLayout();
        var container = list.ItemContainerGenerator.ContainerFromIndex(10) as ListBoxItem;
        Assert(container is not null, "single-click fixture row was not realized");
        var beforeClick = player.Position.TotalSeconds;
        Assert(Math.Abs(beforeClick - 5) > 2, "native fixture cannot distinguish a seek from ordinary playback");
        LyricsUiMouse(container!, down: true);
        var click = LyricsUiMouse(container!, down: false);
        Assert(!click.Handled, "the lyric seek blocked the ListBox MouseUp route");
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

        window.WindowState = WindowState.Minimized;
        await Until(() => !timer.IsEnabled);
        Assert(player.IsPlaying, "minimizing the lyrics view paused playback");
        window.WindowState = WindowState.Normal;
        await Until(() => timer.IsEnabled);
        window.Hide();
        await Until(() => !timer.IsEnabled);
        window.Show();
        await Until(() => timer.IsEnabled);
        LyricsUiField<DispatcherTimer>(window, "_progressTimer")!.Stop();
        LyricsUiSet(window, "_pendingSeek", 18d);
        LyricsUiField<System.Diagnostics.Stopwatch>(window, "_seekClock")!.Restart();
        await Until(() => LyricsActiveIndex(window) == 36);
        Assert(Math.Abs(player.Position.TotalSeconds - 18) > .8, "fixture did not exercise an unacknowledged seek");
        LyricsUiOpen(window);
        Assert(!timer.IsEnabled && !Control<Grid>(window, "LyricsPanel").IsVisible && player.IsPlaying, "hidden lyrics kept its timer or stopped audio");
        fixture.Close();
        Assert(!timer.IsEnabled, "closing the window left the lyrics timer enabled");
    }

    private static async Task CheckLyricsMouseOwnership(bool expanded)
    {
        var kind = expanded ? "expanded" : "inline";
        var first = LyricsFollowNativeTrack("42116", "mouse-first-" + kind);
        var second = LyricsFollowNativeTrack("42117", "mouse-second-" + kind);
        var revision = 0;
        string ActiveLine(TrackRow row) => row.Id + " fixture " + revision;
        using var fixture = new LyricsFollowFixture("mouse-" + kind, LyricsFollowYandexPlain("Mouse fixture plain"), (request, _) =>
        {
            var track = LyricsFollowQuery(request.RequestUri!)["track_name"] == first.Title ? first : second;
            return Task.FromResult(LyricsFollowRecord(track, "[00:02]Other line\n[00:07]" + ActiveLine(track)));
        });
        var window = fixture.Window;
        await window.PlayTrackAsync(first);
        await Call(window, "TogglePlaybackAsync");
        if (expanded)
        {
            LyricsUiInvoke(window, "SetExpandedPlayerVisible", true, false);
            LyricsUiInvoke(window, "SetExpandedLyricsVisible", true, false);
        }
        else LyricsUiOpen(window);
        var list = expanded ? LyricsUiField<ListBox>(window, "_expandedLyricsList")! : Control<ListBox>(window, "LyricsList");
        var player = LyricsUiField<NativePlayer>(window, "_player")!;

        async Task<ListBoxItem> RealizedLine(int index)
        {
            list.ScrollIntoView(list.Items[index]);
            await Until(() => { window.UpdateLayout(); return list.ItemContainerGenerator.ContainerFromIndex(index) is ListBoxItem; });
            return (ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(index)!;
        }

        await Until(() => list.IsVisible && list.Items.Count == 2);
        var target = await RealizedLine(1);
        Assert(player.HasSource && !player.IsPlaying, "fixture did not pause a usable source before the gesture");
        Assert(!LyricsUiMouse(target, down: false).Handled && EpField<double?>(window, "_pendingSeek") is null,
            "release alone sought to a lyric line");
        var adjacent = await RealizedLine(0);
        LyricsUiMouse(target, down: true);
        Assert(!LyricsUiMouse(adjacent, down: false).Handled && EpField<double?>(window, "_pendingSeek") is null,
            "pressing one row and releasing another sought to a lyric line");
        LyricsUiMouse(target, down: true);
        list.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount) { RoutedEvent = Mouse.LostMouseCaptureEvent });
        Assert(!LyricsUiMouse(target, down: false).Handled && EpField<double?>(window, "_pendingSeek") is null,
            "losing capture before release left an active lyric seek gesture");

        var playback = EpField<int>(window, "_playbackGeneration");
        LyricsUiMouse(target, down: true);
        revision++;
        LyricsUiInvoke(window, "LyricsRetry_Click", window, new RoutedEventArgs());
        await Until(() => list.Items.Count == 2 && list.Items[1].ToString() == ActiveLine(first));
        target = await RealizedLine(1);
        Assert(EpField<int>(window, "_playbackGeneration") == playback
            && !LyricsUiMouse(target, down: false).Handled && EpField<double?>(window, "_pendingSeek") is null,
            "replacing lyrics on the same track let an old press seek the new text");

        LyricsUiMouse(target, down: true);
        await window.PlayTrackAsync(second);
        await Call(window, "TogglePlaybackAsync");
        await Until(() => list.Items.Count == 2 && list.Items[1].ToString() == ActiveLine(second));
        target = await RealizedLine(1);
        Assert(EpField<int>(window, "_playbackGeneration") != playback && !player.IsPlaying
            && !LyricsUiMouse(target, down: false).Handled && EpField<double?>(window, "_pendingSeek") is null,
            "a press on the previous track sought the replacement track on release");

        LyricsUiMouse(target, down: true);
        var mouseUpCount = 0;
        MouseButtonEventHandler observeMouseUp = (_, args) =>
        {
            if (ReferenceEquals(args.OriginalSource, target) && args.ChangedButton == MouseButton.Left) mouseUpCount++;
        };
        list.AddHandler(Mouse.MouseUpEvent, observeMouseUp, handledEventsToo: true);
        try
        {
            // Inactive Windows desktops can decline native capture. The routed MouseUp contract still applies.
            var hasNativeCapture = Mouse.Capture(list, CaptureMode.SubTree) && ReferenceEquals(Mouse.Captured, list);
            var preview = LyricsUiMouse(target, down: false);
            Assert(!preview.Handled && EpField<double?>(window, "_pendingSeek") == 7,
                "the accepted gesture missed its exact seek target or blocked WPF MouseUp");
            var mouseUp = new MouseButtonEventArgs(Mouse.PrimaryDevice, preview.Timestamp, MouseButton.Left)
            { RoutedEvent = Mouse.MouseUpEvent };
            target.RaiseEvent(mouseUp);
            Assert(mouseUpCount == 1, "the subsequent MouseUp did not bubble through the lyrics ListBox");
            if (hasNativeCapture) Assert(Mouse.Captured is null, "ListBox kept its native capture after MouseUp");
            LyricsUiSet(window, "_pendingSeek", null!);
            LyricsUiMouse(target, down: false);
            Assert(EpField<double?>(window, "_pendingSeek") is null, "another release repeated the accepted gesture");
        }
        finally
        {
            list.RemoveHandler(Mouse.MouseUpEvent, observeMouseUp);
            if (ReferenceEquals(Mouse.Captured, list)) Mouse.Capture(null);
        }
    }

    private static async Task CheckLyricsSeekBeforeFirstTimestamp()
    {
        var previousRenderMode = RenderOptions.ProcessRenderMode;
        try
        {
            RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;
            foreach (var expanded in new[] { false, true })
            {
                var kind = expanded ? "expanded" : "inline";
                var track = LyricsFollowNativeTrack("42127", "deferred-center-" + kind);
                var lrc = string.Join('\n', Enumerable.Range(0, 50).Select(index =>
                    "[" + TimeSpan.FromSeconds(1 + index * .5).ToString(@"mm\:ss\.fff", CultureInfo.InvariantCulture)
                    + "]Fixture line " + index));
                using var fixture = new LyricsFollowFixture("deferred-center-" + kind, LyricsFollowYandexPlain("Fixture"),
                    (_, _) => Task.FromResult(LyricsFollowRecord(track, lrc)));
                var window = fixture.Window;
                await window.PlayTrackAsync(track);
                await Call(window, "TogglePlaybackAsync");
                if (expanded)
                {
                    LyricsUiInvoke(window, "SetExpandedPlayerVisible", true, false);
                    LyricsUiInvoke(window, "SetExpandedLyricsVisible", true, false);
                }
                else LyricsUiOpen(window);

                var inlineList = Control<ListBox>(window, "LyricsList");
                var list = expanded ? LyricsUiField<ListBox>(window, "_expandedLyricsList")! : inlineList;
                await Until(() =>
                {
                    if (!list.IsVisible || list.Items.Count != 50) return false;
                    list.ApplyTemplate();
                    System.Windows.Window.GetWindow(list)?.UpdateLayout();
                    return LyricsUiVisuals<ScrollViewer>(list).FirstOrDefault() is { ViewportHeight: > 0, ScrollableHeight: > 0 };
                });
                var viewer = LyricsUiVisuals<ScrollViewer>(list).First();
                LyricsUiInvoke(window, "CommitSeek", 0d);
                viewer.ScrollToVerticalOffset(0);
                await Dispatcher.Yield(DispatcherPriority.ContextIdle);
                window.UpdateLayout();
                var before = viewer.VerticalOffset;
                var row = LyricsUiVisuals<ListBoxItem>(list).LastOrDefault(item =>
                {
                    var center = item.TranslatePoint(new Point(0, item.ActualHeight / 2), viewer).Y;
                    return center > viewer.ViewportHeight * .65 && center < viewer.ViewportHeight;
                }) ?? throw new InvalidOperationException("Fixture did not realize a lower visible lyric row.");
                var index = list.ItemContainerGenerator.IndexFromContainer(row);
                Assert(index > 0 && before < 1 && !Motion.CanAnimate, "fixture did not start above a realized deferred target");

                LyricsUiInvoke(window, "UpdateLyrics", 1 + index * .5);
                Assert(LyricsActiveIndex(window) == index && Math.Abs(viewer.VerticalOffset - before) < 1,
                    "realized target did not schedule deferred centering");
                LyricsUiInvoke(window, "CommitSeek", 0d);
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Assert(LyricsActiveIndex(window) == -1 && Math.Abs(viewer.VerticalOffset - before) < 1,
                    kind + " lyrics followed a line cleared by the seek");

                LyricsUiInvoke(window, "UpdateLyrics", 1 + index * .5);
                await Until(() => viewer.VerticalOffset > before + 5);
            }
        }
        finally { RenderOptions.ProcessRenderMode = previousRenderMode; }
    }

    private static async Task CheckFirstInlineLyricsReveal()
    {
        const double seconds = 20.125;
        const int currentLine = 80;
        var track = LyricsFollowNativeTrack("42128", "first-inline-reveal");
        var lrc = string.Join('\n', Enumerable.Range(0, 120).Select(index =>
            "[" + TimeSpan.FromSeconds(index * .25).ToString(@"mm\:ss\.fff", CultureInfo.InvariantCulture)
                + "]Fixture line " + index));
        using var fixture = new LyricsFollowFixture("first-inline-reveal", LyricsFollowYandexPlain("Fixture"),
            (_, _) => Task.FromResult(LyricsFollowRecord(track, lrc)));
        var window = fixture.Window;
        await window.PlayTrackAsync(track);
        EpOpen(window);
        LyricsUiInvoke(window, "SetExpandedLyricsVisible", true, false);
        await Until(() => LyricsUiCached(window, track));
        LyricsUiInvoke(window, "CloseExpandedPlayer");
        await Call(window, "TogglePlaybackAsync");
        var player = LyricsUiField<NativePlayer>(window, "_player")!;
        player.Seek(TimeSpan.FromSeconds(seconds));
        await Until(() => Math.Abs(player.Position.TotalSeconds - seconds) < .05);

        var list = Control<ListBox>(window, "LyricsList");
        list.Template = null;
        list.ClearValue(ListBox.TemplateProperty);
        Assert(!list.IsVisible && !LyricsUiVisuals<ScrollViewer>(list).Any(), "the hidden fixture already had a realized lyrics viewport");
        LyricsUiOpen(window);
        await Until(() =>
        {
            if (list.ItemContainerGenerator.ContainerFromIndex(currentLine) is not ListBoxItem row
                || LyricsUiVisuals<ScrollViewer>(list).FirstOrDefault() is not { ViewportHeight: > 0 } viewer)
                return false;
            var center = row.TranslatePoint(new Point(0, row.ActualHeight / 2), viewer).Y;
            return center > 0 && center < viewer.ViewportHeight && viewer.VerticalOffset > 0;
        });
        Assert(LyricsActiveIndex(window) == currentLine && !player.IsPlaying
            && Math.Abs(player.Position.TotalSeconds - seconds) < .15 && fixture.Lrclib.Seen.Count == 1,
            "the first reveal changed the paused position or fetched the cached lyrics again");
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
        var previousIndex = LyricsActiveIndex(window);
        LyricsUiInvoke(window, "UpdateLyrics", 11.2d);
        Assert(LyricsActiveIndex(window) == 22, "manual follow pause also paused active-line highlighting");
        await Task.Delay(250);
        Assert(Math.Abs(viewer.VerticalOffset - manualOffset) < 1.5, "a playback tick stole the manual scroll offset");
        Assert(previousIndex != 22, "manual fixture did not exercise a changed highlight");
        await Until(() => DateTimeOffset.UtcNow >= pausedUntil);
        await Until(() => Math.Abs(viewer.VerticalOffset - manualOffset) > 5);
        await Until(() => !SmoothScroll.IsAnimating(viewer));
        Assert(LyricsActiveIndex(window) >= 0, "follow resumed without an active line");

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
    private static HttpResponseMessage LyricsFollowRecord(TrackRow track, string lrc) => Json(LyricsFollowRecordBody(track, lrc));
    private static string LyricsFollowRecordBody(TrackRow track, string lrc) => JsonSerializer.Serialize(new
    { id = 1, trackName = track.Title, artistName = track.Artist, albumName = track.Album, duration = track.DurationMs / 1000d, instrumental = false, syncedLyrics = lrc });

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
