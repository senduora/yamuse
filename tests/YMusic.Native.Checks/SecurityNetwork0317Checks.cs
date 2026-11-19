using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using YMusic.Native;

internal static partial class Program
{
    private static async Task CheckSecurityNetwork0317()
    {
        await Run("Security network 0317: repeated LRC timestamps cannot amplify an external response", async () =>
        {
            var lrc = string.Concat(Enumerable.Repeat("[00:01]", 100)) + new string('x', 4000);
            Assert(lrc.Length < 8192, "amplification fixture exceeded its input cap");
            await Throws<MusicException>(() => Task.FromResult(MusicApi.ParseSynchronizedLyrics(lrc)));
            var track = new TrackRow { Title = "Fixture Song", Artist = "Fixture Artist", Album = "Fixture Album", DurationMs = 180000 };
            var body = JsonSerializer.Serialize(new { trackName = track.Title, artistName = track.Artist, albumName = track.Album, duration = 180, instrumental = false, syncedLyrics = lrc });
            using var http = new HttpClient(new SecurityNetwork0317Handler((_, _) => Task.FromResult(Json(body))));
            using var client = new LrclibLyricsClient(http);
            Assert((await client.FindSynchronizedAsync(track, default)).Lyrics is null, "LRCLIB accepted an amplified LRC result");
        });

        await Run("Security network 0317: expanded LRC boundary includes newline separators", async () =>
        {
            var limit = MusicApi.MaximumExpandedLyricsCharacters;
            Assert(limit is > 0 and <= 256 * 1024, "boundary fixture exceeded its output cap");
            var source = new List<string>(); var remaining = limit;
            while (remaining > 0)
            {
                if (source.Count > 0) remaining--;
                var length = Math.Min(16000, remaining);
                source.Add("[00:01]" + new string('b', length)); remaining -= length;
            }
            var lrc = string.Join('\n', source);
            var result = MusicApi.ParseSynchronizedLyrics(lrc);
            Assert(result.Synchronized && result.PlainText.Length == limit && result.Lines.Count == source.Count,
                "valid LRC at the exact expanded boundary was rejected or changed");
            await Throws<MusicException>(() => Task.FromResult(MusicApi.ParseSynchronizedLyrics(lrc + "x")));
        });

        await Run("Security network 0317: ordinary repeated timestamps and offsets retain their meaning", () =>
        {
            var result = MusicApi.ParseSynchronizedLyrics("[offset:250]\n[00:01.50][00:03.000]Repeated line\n[00:00.10]First line");
            Assert(result.Synchronized && result.PlainText == "First line\nRepeated line\nRepeated line"
                && result.Lines.Count == 3 && result.Lines[0].TimeSeconds == 0
                && Math.Abs(result.Lines[1].TimeSeconds - 1.25) < .001 && Math.Abs(result.Lines[2].TimeSeconds - 2.75) < .001,
                "multi-time LRC ordering, repetition or positive offset changed");
            var delayed = MusicApi.ParseSynchronizedLyrics("[offset:-250]\n[00:01]Line");
            Assert(Math.Abs(delayed.Lines.Single().TimeSeconds - 1.25) < .001, "negative LRC offset changed");
            return Task.CompletedTask;
        });

        await Run("Security network 0317: LRC preserves lyric brackets and literal hearts with repeated and word timestamps", () =>
        {
            var lyrics = MusicApi.ParseSynchronizedLyrics("[offset:0]\n[00:01.50][00:03.000]<00:01.50>[Chorus] I <3 you: always\n[00:04]Open [bracket");
            Assert(lyrics.Synchronized && lyrics.Lines.Select(line => line.TimeSeconds).SequenceEqual([1.5, 3, 4])
                && lyrics.Lines.Select(line => line.Text).SequenceEqual(["[Chorus] I <3 you: always", "[Chorus] I <3 you: always", "Open [bracket"]),
                "word timestamps or bracketed lyrics disrupted the timed text");
            return Task.CompletedTask;
        });

        await Run("Security network 0317: Yandex TEXT rejects documents and keeps bounded plain lyrics", async () =>
        {
            const string plain = "[Verse]\nI <3 you: always";
            foreach (var (mime, payload, accepted) in new (string?, string, bool)[]
            {
                ("text/plain", plain, true), (null, plain, true),
                ("text/html", "Fixture HTML error", false), ("application/problem+json", "Fixture JSON error", false),
                (null, "  <!DOCTYPE html><html><title>Fixture error</title></html>", false),
                ("text/plain", """{"error":"Temporary fixture message"}""", false),
                (null, new string('x', 1024 * 1024 + 1), false)
            })
            {
                var supplement = 0;
                using var api = new MusicApi(new FakeHandler((request, _) =>
                {
                    var uri = request.RequestUri!;
                    if (uri.AbsolutePath == "/fixture-text")
                    {
                        Assert(request.Headers.Authorization is null, "lyrics CDN request carried account credentials");
                        var content = new StringContent(payload);
                        content.Headers.ContentType = mime is null ? null : new(mime);
                        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
                    }
                    if (uri.AbsolutePath == "/tracks/42/supplement")
                    {
                        supplement++;
                        return Task.FromResult(Json("""{"result":{"lyrics":{"hasRights":true,"fullLyrics":"Supplement fixture"}}}"""));
                    }
                    return Task.FromResult(uri.Query.Contains("format=TEXT", StringComparison.Ordinal)
                        ? Json("""{"result":{"downloadUrl":"https://fixture.yandex.net/fixture-text"}}""")
                        : Json("{}", HttpStatusCode.NotFound));
                }));
                var lyrics = await api.GetLyricsAsync(FakeSession, "42", default);
                Assert(!lyrics.Synchronized && lyrics.PlainText == (accepted ? plain : "Supplement fixture")
                    && supplement == (accepted ? 0 : 1), "unexpected document MIME/body was displayed or plaintext was dropped");
            }
        });

        await Run("Security network 0317: artwork body deadlines release four workers for a healthy fifth URL", async () =>
        {
            var started = 0; var canceled = 0; var requests = 0;
            var allReading = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var png = SecurityNetwork0317Png();
            using var cache = new ArtworkCache(SecurityNetwork0317Directory("timeout"), new SecurityNetwork0317Handler((request, _) =>
            {
                Interlocked.Increment(ref requests);
                HttpContent content = request.RequestUri!.AbsolutePath.StartsWith("/stall", StringComparison.Ordinal)
                    ? new StreamContent(new SecurityNetwork0317StalledStream(
                        () => { if (Interlocked.Increment(ref started) == 4) allReading.TrySetResult(); },
                        () => Interlocked.Increment(ref canceled)))
                    : new ByteArrayContent(png);
                return Task.FromResult(SecurityNetwork0317Image(content));
            }), TimeSpan.FromMilliseconds(500));
            using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var stalled = Enumerable.Range(0, 4).Select(index => cache.GetAsync($"https://avatars.yandex.net/stall{index}/%%", 128, watchdog.Token)).ToArray();
            await allReading.Task.WaitAsync(watchdog.Token);
            var healthy = cache.GetAsync("https://avatars.yandex.net/healthy/%%", 128, watchdog.Token);
            Assert(requests == 4 && !healthy.IsCompleted, "fixture did not fill all artwork workers before queuing the healthy URL");
            var failed = await Task.WhenAll(stalled).WaitAsync(watchdog.Token);
            Assert(failed.All(bitmap => bitmap is null) && canceled == 4, "body deadlines did not finish all stalled artwork requests normally");
            Assert(await healthy.WaitAsync(watchdog.Token) is not null && requests == 5 && !watchdog.IsCancellationRequested,
                "healthy artwork remained blocked or required external cancellation");
        });

        await Run("Security network 0317: artwork deadline spans redirects and body instead of restarting", async () =>
        {
            var requests = 0; var bodyCanceled = 0;
            using var cache = new ArtworkCache(SecurityNetwork0317Directory("redirect"), new SecurityNetwork0317Handler(async (request, token) =>
            {
                Interlocked.Increment(ref requests);
                if (request.RequestUri!.AbsolutePath.StartsWith("/redirect/", StringComparison.Ordinal))
                {
                    await Task.Delay(300, token);
                    var response = new HttpResponseMessage(HttpStatusCode.Found);
                    response.Headers.Location = new Uri("https://avatars.mds.yandex.net/final/200x200");
                    return response;
                }
                return SecurityNetwork0317Image(new StreamContent(new SecurityNetwork0317DelayedStream(SecurityNetwork0317Png(),
                    TimeSpan.FromMilliseconds(300), () => Interlocked.Increment(ref bodyCanceled))));
            }), TimeSpan.FromMilliseconds(500));
            using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            Assert(await cache.GetAsync("https://avatars.yandex.net/redirect/%%", 128, watchdog.Token) is null
                && requests == 2 && bodyCanceled == 1 && !watchdog.IsCancellationRequested,
                "a redirect restarted the artwork budget or the body did not receive its cancellation");
        });

        await Run("Security network 0317: caller cancellation remains cancellation and frees the artwork request", async () =>
        {
            var reading = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var bodyCanceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var cache = new ArtworkCache(SecurityNetwork0317Directory("caller"), new SecurityNetwork0317Handler((_, _) =>
                Task.FromResult(SecurityNetwork0317Image(new StreamContent(new SecurityNetwork0317StalledStream(
                    () => reading.TrySetResult(), () => bodyCanceled.TrySetResult()))))), TimeSpan.FromSeconds(3));
            using var caller = new CancellationTokenSource();
            using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var pending = cache.GetAsync("https://avatars.yandex.net/caller/%%", 128, caller.Token);
            await reading.Task.WaitAsync(watchdog.Token); caller.Cancel();
            await Throws<OperationCanceledException>(async () => await pending.WaitAsync(watchdog.Token));
            await bodyCanceled.Task.WaitAsync(watchdog.Token);
            Assert(pending.IsCanceled && caller.IsCancellationRequested && !watchdog.IsCancellationRequested,
                "caller cancellation became an unavailable artwork result or leaked its body read");
        });

        await Run("Security network 0317: artwork rejects invalid timeout budgets", async () =>
        {
            foreach (var timeout in new[] { TimeSpan.Zero, TimeSpan.FromMilliseconds(-2), Timeout.InfiniteTimeSpan, TimeSpan.FromMilliseconds((long)int.MaxValue + 1) })
            {
                using var handler = new SecurityNetwork0317Handler((_, _) => throw new InvalidOperationException("invalid timeout attempted network"));
                await Throws<ArgumentOutOfRangeException>(() =>
                {
                    using var cache = new ArtworkCache(SecurityNetwork0317Directory("invalid"), handler, timeout);
                    return Task.CompletedTask;
                });
            }
        });

        await Run("Security network 0317: partial image responses never populate artwork caches", async () =>
        {
            var png = SecurityNetwork0317Png();
            var directory = SecurityNetwork0317Directory("partial");
            var attempted = 0;
            using var cache = new ArtworkCache(directory, new SecurityNetwork0317Handler((_, _) =>
            {
                var response = SecurityNetwork0317Image(new ByteArrayContent(png));
                switch (++attempted)
                {
                    case 1: response.StatusCode = HttpStatusCode.PartialContent; break;
                    case 2: response.Content.Headers.ContentLength = png.Length + 10; break;
                }
                return Task.FromResult(response);
            }));
            const string image = "https://avatars.yandex.net/complete/%%";
            Assert(await cache.GetAsync(image, 128, default) is null && await cache.GetAsync(image, 128, default) is null
                && !Directory.Exists(directory), "a partial response became memory or disk artwork");
            Assert(await cache.GetAsync(image, 128, default) is not null && attempted == 3,
                "complete artwork could not replace failed partial downloads");
        });

        await Run("Security network 0317: favorite snapshots must match their account and include every reference", async () =>
        {
            const string valid = """{"result":{"library":{"uid":12345,"tracks":[{"id":7,"albumId":"fixture-album"},{"id":"fixture-track","albumId":null},{"id":9}]}}}""";
            using (var api = new MusicApi(new FakeHandler((_, _) => Task.FromResult(Json(valid)))))
            {
                var ids = await api.GetLikedIdsAsync(FakeSession, "12345", default);
                Assert(ids.Select(id => id.RequestId).SequenceEqual(["7:fixture-album", "fixture-track", "9"]),
                    "positive favorite response lost an opaque or unavailable track reference");
            }
            var bad = new[]
            {
                (valid.Replace("12345", "67890", StringComparison.Ordinal), HttpStatusCode.OK),
                (valid.Replace("12345", "{}", StringComparison.Ordinal), HttpStatusCode.OK),
                (valid.Replace("\"id\":9", "\"id\":null", StringComparison.Ordinal), HttpStatusCode.OK),
                (valid.Replace("\"id\":9", "\"id\":9.25", StringComparison.Ordinal), HttpStatusCode.OK),
                (valid.Replace("\"fixture-track\"", "\"   \"", StringComparison.Ordinal), HttpStatusCode.OK),
                (valid.Replace("\"fixture-album\"", "[]", StringComparison.Ordinal), HttpStatusCode.OK),
                ("{\"result\":{\"library\":[]}}", HttpStatusCode.OK), (valid, HttpStatusCode.PartialContent)
            };
            foreach (var (data, status) in bad)
            {
                using var api = new MusicApi(new FakeHandler((_, _) => Task.FromResult(Json(data, status))));
                await Throws<MusicException>(() => api.GetLikedIdsAsync(FakeSession, "12345", default));
            }
        });

        await Run("Security network 0317: track metadata distinguishes omitted references aliases and invalid responses", async () =>
        {
            LikedId[] requested = [new("1", "7"), new("1", "8"), new("2", null), new("3", "9")];
            const string valid = """{"result":[{"id":1,"title":"Edition seven","albums":[{"id":7}]},{"id":1,"title":"Edition eight","albums":[{"id":8}]},{"id":20,"realId":2,"title":"Confirmed alias","available":true}]}""";
            using (var api = new MusicApi(new FakeHandler((_, _) => Task.FromResult(Json(valid)))))
            {
                var rows = await api.GetTracksAsync(FakeSession, requested, 5, default);
                Assert(rows.Count == 4 && rows.Select(row => row.RequestId).SequenceEqual(["1:7", "1:8", "2", "3:9"])
                    && rows.Select(row => row.Number).SequenceEqual([6, 7, 8, 9])
                    && rows.Take(3).All(row => row.HasTrackMetadata) && !rows[3].HasTrackMetadata && !rows[3].Available
                    && rows[0].Title == "Edition seven" && rows[1].Title == "Edition eight" && rows[2].Title == "Confirmed alias",
                    "metadata did not preserve exact requested editions, recognized alias, or omitted slot");
            }
            foreach (var malformed in new[]
            {
                "{\"result\":[null]}", "{\"result\":[{}]}", "{\"result\":[{\"id\":999}]}",
                "{\"result\":[{\"id\":1,\"artists\":[false]}]}", "{\"result\":[{\"id\":1,\"albums\":17}]}",
                "{\"result\":[{\"id\":1,\"albums\":[{\"id\":7}],\"title\":\"A\"},{\"id\":1,\"albums\":[{\"id\":7}],\"title\":\"B\"}]}"
            })
            {
                using var api = new MusicApi(new FakeHandler((_, _) => Task.FromResult(Json(malformed))));
                await Throws<MusicException>(() => api.GetTracksAsync(FakeSession, requested, 0, default));
            }
        });

        await Run("Security network 0317: network track durations safely preserve bounds in bulk album and wave", async () =>
        {
            const string tracks = """[{"id":1,"title":"Fixture\u0001track","durationMs":-1},{"id":2,"title":"Fixture track","durationMs":604800001},{"id":3,"title":"Fixture track","durationMs":9223372036854775808},{"id":4,"title":"Fixture track","durationMs":604800000},{"id":5,"title":"Fixture track","durationMs":0}]""";
            using var decoded = JsonDocument.Parse(tracks);
            var waveBody = JsonSerializer.Serialize(new { result = new { radioSessionId = "fixture-session", batchId = "fixture-batch",
                sequence = decoded.RootElement.EnumerateArray().Select(track => new { track }).ToArray() } });
            using var api = new MusicApi(new FakeHandler((request, _) => Task.FromResult(Json(request.RequestUri!.AbsolutePath switch
            {
                "/tracks" => "{\"result\":" + tracks + "}",
                "/albums/7/with-tracks" => "{\"result\":{\"id\":7,\"volumes\":[" + tracks + "]}}",
                "/rotor/session/new" => waveBody,
                _ => throw new InvalidOperationException("unexpected network duration fixture route")
            }))));
            var bulk = await api.GetTracksAsync(FakeSession, [new("1", null), new("2", null), new("3", null), new("4", null), new("5", null)], 0, default);
            var album = await api.GetAlbumTracksAsync(FakeSession, "7", default);
            var wave = await api.StartWaveAsync(FakeSession, default);
            foreach (var rows in new[] { bulk, album, wave.Tracks })
                Assert(rows.Select(row => row.DurationMs).SequenceEqual([0, 0, 0, PlaybackStateStore.MaximumDurationMs, 0])
                    && rows[0].Title == "Fixture track", "network metadata escaped the supported duration or text range");
        });

        await Run("Security network 0317: Genius handles malformed status and ignores raw script text", async () =>
        {
            var http = new FakeHandler((request, _) => Task.FromResult(request.RequestUri!.AbsolutePath switch
            {
                "/api/search/multi" => Json("""{"meta":{"status":"200"},"response":{"sections":[{"type":"song","hits":[{"result":{"title":"Fixture song","primary_artist":{"name":"Fixture artist"},"path":"/Fixture-song-lyrics"}}]}]}}"""),
                "/Fixture-song-lyrics" => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(
                    "<div data-lyrics-container='true'>Test line<br><script>fixture text </script-label> ignored fixture text</script>Second line</div>") },
                _ => throw new InvalidOperationException("Unexpected Genius fixture route")
            }));
            using var genius = new GeniusLyricsClient(http);
            var lyrics = await genius.FindAsync("Fixture song", "Fixture artist", default);
            Assert(lyrics?.PlainText == "Test line\nSecond line" && http.Seen.Count == 2
                && http.Seen.All(sent => sent.Authorization is null), "Genius incorrectly read search metadata or included script data in lyrics");
        });

        await Run("Security network 0317: Genius retries an unreadable song page and caches explicit misses", async () =>
        {
            const string match = """{"response":{"sections":[{"type":"song","hits":[{"result":{"title":"Fixture song","primary_artist":{"name":"Fixture artist"},"path":"/Fixture-song-lyrics"}}]}]}}""";
            foreach (var unreadable in new[] { "", " \r\n", "<main>Temporary fixture page</main>", "<div class='lyrics'><br></div>",
                "<div data-lyrics-container='true'>&nbsp;</div>" })
            {
                var pages = 0;
                var http = new FakeHandler((request, _) => Task.FromResult(request.RequestUri!.AbsolutePath switch
                {
                    "/api/search/multi" => Json(match),
                    "/Fixture-song-lyrics" => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(++pages == 1
                        ? unreadable : "<div data-lyrics-container='true'>Fixture lyric</div>") },
                    _ => throw new InvalidOperationException("Unexpected Genius fixture route")
                }));
                using var genius = new GeniusLyricsClient(http);
                await Throws<MusicException>(() => genius.FindAsync("Fixture song", "Fixture artist", default));
                Assert((await genius.FindAsync("Fixture song", "Fixture artist", default))?.PlainText == "Fixture lyric" && pages == 2,
                    "Genius cached an unusable successful page as absent text");
            }

            var emptySearch = new FakeHandler((_, _) => Task.FromResult(Json("""{"response":{"sections":[{"type":"song","hits":[]}]}}""")));
            using var missing = new GeniusLyricsClient(emptySearch);
            Assert(await missing.FindAsync("Unknown song", "Fixture artist", default) is null
                && await missing.FindAsync("Unknown song", "Fixture artist", default) is null && emptySearch.Seen.Count == 1,
                "a genuine search miss stopped using the existing negative cache");
            Assert(await missing.FindAsync("Unknown song", "Fixture artist", default, refresh: true) is null && emptySearch.Seen.Count == 2,
                "an explicit refresh reused the Genius negative cache");

            foreach (var status in new[] { HttpStatusCode.NotFound, HttpStatusCode.Gone })
            {
                var emptyPage = new FakeHandler((request, _) => Task.FromResult(request.RequestUri!.AbsolutePath == "/api/search/multi"
                    ? Json(match) : new HttpResponseMessage(status) { Content = new StringContent("") }));
                using var empty = new GeniusLyricsClient(emptyPage);
                Assert(await empty.FindAsync("Fixture song", "Fixture artist", default) is null
                    && await empty.FindAsync("Fixture song", "Fixture artist", default) is null && emptyPage.Seen.Count == 2,
                    "an explicitly absent lyric was reported as broken markup");
            }

            var range = new FakeHandler((_, _) =>
            {
                var reply = Json(match);
                Assert(reply.Content.Headers.TryAddWithoutValidation("Content-Range", "invalid fixture range"), "raw range fixture was rejected by the header collection");
                return Task.FromResult(reply);
            });
            using var partial = new GeniusLyricsClient(range);
            await Throws<MusicException>(() => partial.FindAsync("Fixture song", "Fixture artist", default));
            Assert(range.Seen.Count == 1, "Genius accepted a search response with malformed Content-Range");
        });

        await Run("Security network 0317: Genius retries missing search endpoints and keys exact queries", async () =>
        {
            const string match = """{"response":{"sections":[{"type":"song","hits":[{"result":{"title":"Fixture song","primary_artist":{"name":"Fixture artist"},"path":"/Fixture-song-lyrics"}}]}]}}""";
            foreach (var status in new[] { HttpStatusCode.NotFound, HttpStatusCode.Gone })
            {
                var searches = 0;
                var http = new FakeHandler((request, _) => Task.FromResult(request.RequestUri!.AbsolutePath == "/api/search/multi"
                    ? ++searches == 1 ? Json("{}", status) : Json(match)
                    : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<div data-lyrics-container='true'>Fixture lyric</div>") }));
                using var genius = new GeniusLyricsClient(http);
                await Throws<MusicException>(() => genius.FindAsync("Fixture song", "Fixture artist", default));
                Assert((await genius.FindAsync("Fixture song", "Fixture artist", default))?.PlainText == "Fixture lyric" && searches == 2,
                    "Genius treated a missing search endpoint as a cached song miss");
            }

            var queryHttp = new FakeHandler((request, _) => Task.FromResult(request.RequestUri!.AbsolutePath != "/api/search/multi"
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<div data-lyrics-container='true'>New text</div>") }
                : Uri.UnescapeDataString(request.RequestUri.Query).EndsWith("Fixture song?", StringComparison.Ordinal)
                    ? Json("""{"response":{"sections":[{"type":"song","hits":[]}]}}""") : Json(match)));
            using var queryClient = new GeniusLyricsClient(queryHttp);
            Assert(await queryClient.FindAsync("Fixture song?", "Fixture artist", default) is null
                && (await queryClient.FindAsync("Fixture song!", "Fixture artist", default))?.PlainText == "New text" && queryHttp.Seen.Count == 3,
                "a normalized matching key reused a miss for a different exact query");
        });
    }

    private static string SecurityNetwork0317Directory(string kind)
        => Path.Combine(_directory, "security-network-0317-" + kind + "-" + Guid.NewGuid().ToString("N"));

    private static HttpResponseMessage SecurityNetwork0317Image(HttpContent content)
    {
        content.Headers.ContentType = new("image/png");
        return new(HttpStatusCode.OK) { Content = content };
    }

    private static byte[] SecurityNetwork0317Png()
    {
        var bitmap = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[] { 1, 2, 3, 255 }, 4);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream(); encoder.Save(stream); return stream.ToArray();
    }

    private sealed class SecurityNetwork0317Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> action) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => action(request, cancellationToken);
    }

    private sealed class SecurityNetwork0317StalledStream(Action started, Action canceled) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            started();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); return 0; }
            finally { if (token.IsCancellationRequested) canceled(); }
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class SecurityNetwork0317DelayedStream(byte[] bytes, TimeSpan delay, Action canceled) : MemoryStream(bytes, false)
    {
        private bool _delayed;
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            if (!_delayed)
            {
                _delayed = true;
                try { await Task.Delay(delay, token); }
                catch (OperationCanceledException) { canceled(); throw; }
            }
            return await base.ReadAsync(buffer, token);
        }
    }
}
