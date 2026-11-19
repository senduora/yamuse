using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using YMusic.Native;

internal static partial class Program
{
    private static TrackRow LrclibTrack(long durationMs = 180125) => new()
    {
        Id = "lrclib-fixture", Title = "Песня (Live Remix)", Artist = "Исполнитель & Гость",
        Album = "Альбом / 2026", DurationMs = durationMs, Available = true
    };

    private static string LrclibRecord(string? artist = null, string? title = null, double duration = 180.125,
        string? lrc = "[offset:250]\n[00:01.50][00:03.000]Synthetic fixture line", bool instrumental = false, string album = "Альбом / 2026")
        => JsonSerializer.Serialize(new
        {
            trackName = title ?? "песня (live remix)", artistName = artist ?? "  исполнитель   & гость  ",
            albumName = album, duration, instrumental, plainLyrics = "Synthetic plain fixture", syncedLyrics = lrc
        });

    private static string LrclibUnreadableField(string record, string field) =>
        record.Replace($"\"{field}\":", $"\"{field}\":\"\\uD800\",\"unused\":", StringComparison.Ordinal);

    private static async Task CheckLrclib()
    {
        await Run("LRCLIB: exact anonymous lookup preserves edition and strict LRC timestamps", async () =>
        {
            var calls = 0;
            using var http = new HttpClient(new LrclibHandler((request, _) =>
            {
                calls++;
                var uri = request.RequestUri!;
                Assert(request.Method == HttpMethod.Get && uri.Scheme == "https" && uri.Host == "lrclib.net"
                    && uri.AbsolutePath == "/api/get" && uri.IsDefaultPort && uri.UserInfo.Length == 0, "LRCLIB left its fixed read-only endpoint");
                Assert(request.Headers.Authorization is null && !request.Headers.Contains("Cookie")
                    && !request.Headers.Contains("X-Yandex-Music-Client") && request.Headers.UserAgent.Count > 0, "LRCLIB leaked account headers or omitted client identity");
                var query = uri.Query.TrimStart('?').Split('&').Select(part => part.Split('=', 2))
                    .ToDictionary(part => part[0], part => Uri.UnescapeDataString(part[1]));
                var track = LrclibTrack();
                Assert(query.Count == 4 && query["artist_name"] == track.Artist && query["track_name"] == track.Title
                    && query["album_name"] == track.Album && query["duration"] == "180.125", "exact query lost artist, edition, album or fractional invariant duration");
                return Task.FromResult(Json(LrclibRecord(duration: 182.125)));
            }));
            using var client = new LrclibLyricsClient(http);
            var result = (await client.FindSynchronizedAsync(LrclibTrack(), default)).Lyrics;
            Assert(calls == 1 && result is { Synchronized: true, Lines.Count: 2 }
                && Math.Abs(result.Lines[0].TimeSeconds - 1.25) < .001 && Math.Abs(result.Lines[1].TimeSeconds - 2.75) < .001,
                "matched LRC did not retain parser offset/multiple-stamp semantics or the inclusive two-second boundary");
            Assert(result!.Note == "Источник: LRCLIB · синхронизированный текст", "source attribution is missing");
        });

        await Run("LRCLIB: wrong recording, plain text, instrumentals and invalid bodies are rejected", async () =>
        {
            var rejected = new[]
            {
                LrclibRecord(artist: "Другой исполнитель"),
                LrclibRecord(artist: "Исполнитель"),
                LrclibRecord(title: "Песня"),
                LrclibRecord(title: "Песня (Studio Remix)"),
                LrclibRecord(album: "Другой альбом"),
                LrclibRecord(album: ""),
                LrclibRecord(duration: 182.126),
                LrclibRecord(lrc: null),
                LrclibRecord(lrc: ""),
                LrclibRecord(instrumental: true),
                LrclibRecord(lrc: "[00:99.00]Invalid time fixture"),
                LrclibRecord(lrc: "Untimed synthetic text"),
                LrclibRecord(lrc: "[ar:Synthetic fixture]"),
                "{malformed",
                "[]",
                LrclibUnreadableField(LrclibRecord(), "syncedLyrics"),
                LrclibRecord().Replace("\"instrumental\":false", "\"instrumental\":\"false\"", StringComparison.Ordinal),
                "{\"trackName\":\"Песня (Live Remix)\",\"artistName\":\"Исполнитель & Гость\",\"duration\":\"180.125\",\"syncedLyrics\":\"[00:01]Fixture\"}"
            };
            for (var index = 0; index < rejected.Length; index++)
            {
                using var http = new HttpClient(new LrclibHandler((_, _) => Task.FromResult(Json(rejected[index]))));
                using var client = new LrclibLyricsClient(http);
                Assert((await client.FindSynchronizedAsync(LrclibTrack(), default)).Lyrics is null, "rejected recording/body case accepted: " + index);
            }
            foreach (var announcedLength in new[] { true, false })
            {
                var bytes = Encoding.UTF8.GetBytes(LrclibRecord()[..^1] + ",\"padding\":\"" + new string('x', 1024 * 1024) + "\"}");
                // Both bodies carry JSON media type; rejection must come from the announced or streamed size.
                using var sizeHttp = new HttpClient(new LrclibHandler((_, _) =>
                {
                    HttpContent content = announcedLength ? new ByteArrayContent(bytes) : new LrclibUnknownLengthContent(bytes);
                    content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
                }));
                using var sizeClient = new LrclibLyricsClient(sizeHttp);
                Assert((await sizeClient.FindSynchronizedAsync(LrclibTrack(), default)).Lyrics is null, "oversized LRCLIB body was accepted");
            }
            var unexpectedCalls = 0;
            using var emptyHttp = new HttpClient(new LrclibHandler((_, _) => { unexpectedCalls++; throw new InvalidOperationException("invalid input reached HTTP"); }));
            using var emptyClient = new LrclibLyricsClient(emptyHttp);
            Assert((await emptyClient.FindSynchronizedAsync(LrclibTrack(0), default)).Lyrics is null
                && (await emptyClient.FindSynchronizedAsync(LrclibTrack(3600001), default)).Lyrics is null && unexpectedCalls == 0,
                "unknown/out-of-range duration was sent to LRCLIB");

            using var shortHttp = new HttpClient(new LrclibHandler((_, _) =>
            {
                var reply = Json(LrclibRecord());
                reply.Content.Headers.ContentLength = reply.Content.Headers.ContentLength!.Value + 1;
                return Task.FromResult(reply);
            }));
            using var shortClient = new LrclibLyricsClient(shortHttp);
            Assert((await shortClient.FindSynchronizedAsync(LrclibTrack(), default)) is { Lyrics: null, TemporarilyUnavailable: true },
                "a valid prefix of an incomplete LRCLIB body was accepted");

            using var malformedHttp = new HttpClient(new LrclibHandler((_, _) => Task.FromResult(Json("{}"))));
            using var malformedClient = new LrclibLyricsClient(malformedHttp);
            Assert((await malformedClient.FindSynchronizedAsync(LrclibTrack(), default)) is { Lyrics: null, TemporarilyUnavailable: true },
                "a malformed LRCLIB record was treated as a confirmed miss");

            using var rangeHttp = new HttpClient(new LrclibHandler((_, _) =>
            {
                var reply = Json(LrclibRecord());
                Assert(reply.Content.Headers.TryAddWithoutValidation("Content-Range", "invalid fixture range"), "raw range fixture was rejected by the header collection");
                return Task.FromResult(reply);
            }));
            using var rangeClient = new LrclibLyricsClient(rangeHttp);
            Assert((await rangeClient.FindSynchronizedAsync(LrclibTrack(), default)) is { Lyrics: null, TemporarilyUnavailable: true },
                "LRCLIB ignored a nonempty malformed Content-Range");
        });

        await Run("LRCLIB: unreadable advertised strings stay retryable and explicit absence does not", async () =>
        {
            var valid = LrclibRecord();
            foreach (var field in new[] { "syncedLyrics", "albumName" })
            {
                var unavailable = LrclibUnreadableField(valid, field);
                var requests = 0;
                using var http = new HttpClient(new LrclibHandler((_, _) => Task.FromResult(Json(++requests == 1 ? unavailable : valid))));
                using var client = new LrclibLyricsClient(http);
                Assert((await client.FindSynchronizedAsync(LrclibTrack(), default)) is { Lyrics: null, TemporarilyUnavailable: true },
                    "an undecodable LRCLIB field was folded into a confirmed miss: " + field);
                Assert((await client.FindSynchronizedAsync(LrclibTrack(), default)) is { Lyrics.Synchronized: true, TemporarilyUnavailable: false }
                    && requests == 2, "valid data did not recover after the unreadable " + field);
            }

            foreach (var field in new[] { "instrumental", "syncedLyrics", "albumName" })
            {
                var incomplete = System.Text.Json.Nodes.JsonNode.Parse(valid)!.AsObject();
                incomplete.Remove(field);
                var requests = 0;
                using var http = new HttpClient(new LrclibHandler((_, _) =>
                    Task.FromResult(Json(++requests == 1 ? incomplete.ToJsonString() : valid))));
                using var client = new LrclibLyricsClient(http);
                Assert((await client.FindSynchronizedAsync(LrclibTrack(), default)) is { Lyrics: null, TemporarilyUnavailable: true },
                    "a required missing LRCLIB field was folded into absence: " + field);
                Assert((await client.FindSynchronizedAsync(LrclibTrack(), default)) is { Lyrics.Synchronized: true }
                    && requests == 2, "repair of a missing LRCLIB field was not retried");
            }

            foreach (var absent in new[] { LrclibRecord(lrc: null), LrclibRecord(lrc: " \t"), LrclibRecord(instrumental: true), LrclibRecord(album: "Other album") })
            {
                using var http = new HttpClient(new LrclibHandler((_, _) => Task.FromResult(Json(absent))));
                using var client = new LrclibLyricsClient(http);
                Assert((await client.FindSynchronizedAsync(LrclibTrack(), default)) is { Lyrics: null, TemporarilyUnavailable: false },
                    "a valid absence or album mismatch became a provider error");
            }
        });

        await Run("LRCLIB: cancellation, HTTP errors and redirects never turn into cached success", async () =>
        {
            foreach (var status in new[] { 401, 403, 404, 429, 500, 503, 302 })
            {
                var calls = 0;
                using var http = new HttpClient(new LrclibHandler((_, _) =>
                {
                    calls++;
                    var response = Json(LrclibRecord(), (HttpStatusCode)status);
                    if (status == 429) response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(30));
                    if (status == 302) response.Headers.Location = new Uri("https://example.invalid/api/get");
                    return Task.FromResult(response);
                }));
                using var client = new LrclibLyricsClient(http);
                var lookup = await client.FindSynchronizedAsync(LrclibTrack(), default);
                Assert(lookup.Lyrics is null && lookup.TemporarilyUnavailable == (status != 404) && calls == 1,
                    "HTTP failure/redirect was treated as lyrics or absence: " + status);
                if (status == 429)
                    Assert((await client.FindSynchronizedAsync(LrclibTrack(), default)) is { Lyrics: null, TemporarilyUnavailable: true }
                        && calls == 1, "Retry-After was ignored or turned into an absence");
            }
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var cancelHttp = new HttpClient(new LrclibHandler(async (_, token) =>
            {
                entered.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                throw new InvalidOperationException("canceled handler continued");
            }));
            using var cancelClient = new LrclibLyricsClient(cancelHttp);
            using var cancel = new CancellationTokenSource();
            var pending = cancelClient.FindSynchronizedAsync(LrclibTrack(), cancel.Token);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(2)); cancel.Cancel();
            await Throws<OperationCanceledException>(async () => await pending);

            var sequence = 0;
            using var retryHttp = new HttpClient(new LrclibHandler((_, _) => Task.FromResult(++sequence switch
            {
                1 => Json(LrclibRecord(), HttpStatusCode.Forbidden),
                2 => Json(LrclibRecord()),
                _ => Json(LrclibRecord(), HttpStatusCode.ServiceUnavailable)
            })));
            using var retryClient = new LrclibLyricsClient(retryHttp);
            Assert((await retryClient.FindSynchronizedAsync(LrclibTrack(), default)).TemporarilyUnavailable
                && (await retryClient.FindSynchronizedAsync(LrclibTrack(), default)).Lyrics is { Synchronized: true }
                && (await retryClient.FindSynchronizedAsync(LrclibTrack(), default)).TemporarilyUnavailable && sequence == 3,
                "forbidden response blocked recovery or a server failure reused a previous success");

            using var timeoutHttp = new HttpClient(new LrclibHandler(async (_, token) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                throw new InvalidOperationException("timeout handler continued");
            })) { Timeout = TimeSpan.FromMilliseconds(50) };
            using var timeoutClient = new LrclibLyricsClient(timeoutHttp);
            Assert((await timeoutClient.FindSynchronizedAsync(LrclibTrack(), default)) is { Lyrics: null, TemporarilyUnavailable: true },
                "request timeout became lyrics, an absence, or an uncaught user cancellation");

            var authCalls = 0;
            using var authHttp = new HttpClient(new LrclibHandler((_, _) => { authCalls++; throw new InvalidOperationException("credentialed HTTP sent"); }));
            authHttp.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "fake-fixture-not-a-secret");
            using var authClient = new LrclibLyricsClient(authHttp);
            await Throws<InvalidOperationException>(() => authClient.FindSynchronizedAsync(LrclibTrack(), default));
            Assert(authCalls == 0, "injected account header reached the external lyrics service");
        });
    }

    private sealed class LrclibHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => respond(request, cancellationToken);
    }

    private sealed class LrclibUnknownLengthContent(byte[] bytes) : HttpContent
    {
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(bytes).AsTask();
        protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult<Stream>(new MemoryStream(bytes, writable: false));
    }
}
