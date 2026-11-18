using System.Net;
using System.Net.Http;
using YMusic.Native;

internal static partial class Program
{
    private static async Task CheckQuality017()
    {
        const string aac192 = """{"result":{"downloadInfo":{"quality":"nq","codec":"aac-mp4","bitrate":192,"transport":"raw","url":"https://s1.vlx.yandex.net/modern.m4a"}}}""";
        const string legacy192 = """{"result":[{"codec":"mp3","bitrateInKbps":192,"downloadInfoUrl":"https://s1.vlx.yandex.net/info.xml"}]}""";
        await Run("Quality 017: only actual FLAC codecs prove lossless", () =>
        {
            var uri = new Uri("https://s1.vlx.yandex.net/audio");
            foreach (var codec in new[] { "flac", "flac-mp4" })
                Assert(new AudioDescriptor(uri, codec, 0).IsLossless, "zero nominal FLAC bitrate rejected");
            foreach (var codec in new[] { "aac", "aac-mp4", "mp3", "flac-unknown" })
            {
                var descriptor = new AudioDescriptor(uri, codec, 1411);
                Assert(!descriptor.IsLossless && !descriptor.QualityLabel.Contains("Lossless"), "lossy/unknown codec labelled lossless");
            }
            return Task.CompletedTask;
        });
        await Run("Quality 017: valid modern rendition survives one failed legacy alternative", async () =>
        {
            var modern = 0; var legacy = 0;
            using var api = new MusicApi(new FakeHandler((request, _) =>
            {
                if (request.RequestUri!.AbsolutePath == "/get-file-info") { modern++; return Task.FromResult(Json(aac192)); }
                legacy++; return Task.FromResult(Json("{}", HttpStatusCode.ServiceUnavailable));
            }));
            var audio = await api.ResolveAudioAsync(FakeSession, "42", AudioQuality.Excellent, default);
            Assert(audio.Codec == "aac-mp4" && audio.Bitrate == 192 && !audio.IsLossless, "valid actual rendition lost or relabelled");
            Assert(modern == 1 && legacy == 1, "failed legacy alternative repeated");
        });
        await Run("Quality 017: service downgrade remains actual 192 after legacy fallback", async () =>
        {
            using var api = new MusicApi(new FakeHandler((request, _) => Task.FromResult(request.RequestUri!.AbsolutePath switch
            {
                "/get-file-info" => Json(aac192),
                "/tracks/42/download-info" => Json(legacy192),
                "/info.xml" => Xml(),
                _ => throw new InvalidOperationException("Unexpected fixture request")
            })));
            var audio = await api.ResolveAudioAsync(FakeSession, "42", AudioQuality.Excellent, default);
            Assert(audio.Codec == "aac-mp4" && audio.Bitrate == 192 && !audio.IsLossless, "requested tier passed as actual quality");
        });
        await Run("Quality 017: optional alternative transport failures retain modern audio but parent cancellation wins", async () =>
        {
            foreach (var failure in new[] { 0, 1, 2 })
            {
                using var cancellation = new CancellationTokenSource();
                using var api = new MusicApi(new FakeHandler((request, _) =>
                {
                    if (request.RequestUri!.AbsolutePath == "/get-file-info") return Task.FromResult(Json(aac192));
                    if (failure == 0) throw new System.Net.Http.HttpRequestException();
                    if (failure == 2) cancellation.Cancel();
                    throw new OperationCanceledException();
                }));
                if (failure == 2)
                    await Throws<OperationCanceledException>(() => api.ResolveAudioAsync(FakeSession, "42", AudioQuality.Excellent, cancellation.Token));
                else
                {
                    var audio = await api.ResolveAudioAsync(FakeSession, "42", AudioQuality.Excellent, cancellation.Token);
                    Assert(audio.Codec == "aac-mp4" && audio.Bitrate == 192 && !audio.IsLossless, "transport error discarded valid modern audio");
                }
            }
        });
        await Run("Quality 017: modern or alternative 401 never becomes a successful fallback", async () =>
        {
            foreach (var (unauthorizedPath, expectedCalls) in new[] { ("/get-file-info", 1), ("/tracks/42/download-info", 2), ("/info.xml", 3) })
            {
                var calls = 0; var bodyReads = 0;
                using var api = new MusicApi(new FakeHandler((request, _) =>
                {
                    calls++;
                    var path = request.RequestUri!.AbsolutePath;
                    return Task.FromResult(path == unauthorizedPath
                        ? new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StreamContent(
                            new SecurityNetwork0317StalledStream(() => bodyReads++, () => { })) }
                        : path == "/get-file-info" ? Json(aac192) : Json(legacy192));
                }), TimeSpan.FromSeconds(1));
                var error = await Throws<MusicException>(() => api.ResolveAudioAsync(FakeSession, "42", AudioQuality.Excellent, default));
                Assert(error.Authentication && calls == expectedCalls && bodyReads == 0, "auth rejection buffered its body, disappeared, or retried");
            }
        });
        await Run("Quality 017: modern 403 uses actual legacy codec without lossless claim", async () =>
        {
            using var api = new MusicApi(new FakeHandler((request, _) => Task.FromResult(request.RequestUri!.AbsolutePath switch
            {
                "/get-file-info" => Json("{}", HttpStatusCode.Forbidden),
                "/tracks/42/download-info" => Json(legacy192),
                "/info.xml" => Xml(),
                _ => throw new InvalidOperationException("Unexpected fixture request")
            })));
            var audio = await api.ResolveAudioAsync(FakeSession, "42", AudioQuality.Excellent, default);
            Assert(audio.Codec == "mp3" && audio.Bitrate == 192 && !audio.IsLossless, "legacy fallback mislabeled");
        });

        await Run("Quality 017: malformed modern metadata falls back to full MP3", async () =>
        {
            foreach (var modern in new[]
            {
                "null", "[]", "{\"result\":[]}",
                """{"result":{"downloadInfo":{"codec":"mp3","transport":"raw","urls":[null,23,{}]}}}""",
                """{"result":{"downloadInfo":{"codec":"mp3","transport":"raw","url":"relative-address"}}}""",
                """{"result":{"downloadInfo":{"codec":"mp3","transport":"raw","bitrate":-10,"url":"https://s1.vlx.yandex.net/modern.mp3"}}}""",
                """{"result":{"downloadInfo":{"codec":"mp3","transport":"encraw","key":"zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz","url":"https://s1.vlx.yandex.net/modern.mp3"}}}"""
            })
            {
                var fixture = new FakeHandler((request, _) => Task.FromResult(request.RequestUri!.AbsolutePath switch
                {
                    "/get-file-info" => Json(modern), "/tracks/42/download-info" => Json(legacy192), "/info.xml" => Xml(),
                    _ => throw new InvalidOperationException("Unexpected metadata fixture route")
                }));
                using var api = new MusicApi(fixture);
                var audio = await api.ResolveAudioAsync(FakeSession, "42", AudioQuality.Optimal, default);
                Assert(audio.Codec == "mp3" && audio.Bitrate == 192 && fixture.Seen.Count == 3,
                    "invalid modern descriptor bypassed full MP3 or caused a subsequent CDN request");
                Assert(fixture.Seen[2].Authorization is null, "CDN metadata request contained authorization");
            }
        });

        await Run("Quality 017: temporary modern body or transport failure can fall back while caller cancellation stops", async () =>
        {
            foreach (var mode in new[] { "transport", "short", "timeout", "canceled", "range", "size" })
            {
                var reading = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var stopped = 0;
                using var cancellation = new CancellationTokenSource();
                var fixture = new FakeHandler((request, _) =>
                {
                    var path = request.RequestUri!.AbsolutePath;
                    if (path == "/tracks/42/download-info") return Task.FromResult(Json(legacy192));
                    if (path == "/info.xml") return Task.FromResult(Xml());
                    if (mode == "transport") throw new HttpRequestException();
                    var response = Json(aac192);
                    if (mode is "timeout" or "canceled")
                        response.Content = new StreamContent(new SecurityNetwork0317StalledStream(() => reading.TrySetResult(), () => stopped++));
                    else if (mode == "range") response.Content.Headers.TryAddWithoutValidation("Content-Range", "invalid-fixture-range");
                    else response.Content.Headers.ContentLength = mode == "size" ? 16 * 1024 * 1024 + 1 : response.Content.Headers.ContentLength!.Value + 1;
                    return Task.FromResult(response);
                });
                using var api = new MusicApi(fixture, TimeSpan.FromMilliseconds(500));
                var resolve = api.ResolveAudioAsync(FakeSession, "42", AudioQuality.Optimal, cancellation.Token);
                if (mode == "canceled")
                {
                    await reading.Task.WaitAsync(TimeSpan.FromSeconds(2));
                    cancellation.Cancel();
                    await Throws<OperationCanceledException>(() => resolve);
                    Assert(fixture.Seen.Count == 1 && stopped == 1, "caller cancellation issued a fallback request");
                }
                else
                {
                    var audio = await resolve.WaitAsync(TimeSpan.FromSeconds(3));
                    Assert(fixture.Seen.Count == 3 && audio.Codec == "mp3" && audio.Bitrate == 192
                        && (mode != "timeout" || stopped == 1), "modern response failure bypassed the bounded fallback");
                }
            }
        });

        await Run("Quality 017: unknown FLAC nominal bitrate stays zero at the API boundary", async () =>
        {
            var fixture = new FakeHandler((_, _) => Task.FromResult(Json("""{"result":{"downloadInfo":{"quality":"lossless","codec":"flac","bitrate":0,"transport":"raw","url":"https://s1.vlx.yandex.net/modern.flac"}}}""")));
            using var api = new MusicApi(fixture);
            var audio = await api.ResolveAudioAsync(FakeSession, "42", AudioQuality.Excellent, default);
            Assert(audio.IsLossless && audio.Bitrate == 0 && fixture.Seen.Count == 1, "valid FLAC with unknown nominal bitrate was discarded");
        });

        await Run("Quality 017: file info checks the echoed track id and allows the canonical alias", async () =>
        {
            foreach (var (identity, expectedModern) in new[]
            {
                ("", true), ("\"trackId\":42,", true), ("\"trackId\":\"42:7\",\"realId\":987,", true),
                ("\"trackId\":987,\"realId\":42,", false), ("\"trackId\":{},", false)
            })
            {
                var body = aac192.Replace("\"downloadInfo\":{", "\"downloadInfo\":{" + identity, StringComparison.Ordinal);
                var fixture = new FakeHandler((request, _) =>
                {
                    var uri = request.RequestUri!;
                    if (uri.AbsolutePath == "/get-file-info")
                    {
                        Assert(uri.Query.Contains("&trackId=42&", StringComparison.Ordinal), "quality request lost canonical track part");
                        return Task.FromResult(Json(body));
                    }
                    if (uri.AbsolutePath.EndsWith("/download-info", StringComparison.Ordinal)) return Task.FromResult(Json(legacy192));
                    if (uri.AbsolutePath == "/info.xml") return Task.FromResult(Xml());
                    throw new InvalidOperationException("Unexpected quality identity fixture request");
                });
                using var api = new MusicApi(fixture);
                var audio = await api.ResolveAudioAsync(FakeSession, "42:7", AudioQuality.Optimal, default);
                Assert(expectedModern ? audio.Uri.AbsolutePath == "/modern.m4a" && fixture.Seen.Count == 1
                    : audio.Codec == "mp3" && fixture.Seen.Count == 3,
                    "wrong-track media was accepted or a matching alias lost its modern descriptor");
            }
        });

        await Run("Quality 017: unusable URL entries do not discard a valid modern rendition", async () =>
        {
            var fixture = new FakeHandler((_, _) => Task.FromResult(Json("""{"result":{"downloadInfo":{"codec":"mp3","bitrate":192,"transport":"raw","urls":[null,"","incomplete-address","https://s1.vlx.yandex.net/modern.mp3"]}}}""")));
            using var api = new MusicApi(fixture);
            var audio = await api.ResolveAudioAsync(FakeSession, "42", AudioQuality.Optimal, default);
            Assert(audio.Bitrate == 192 && audio.Uri.AbsolutePath == "/modern.mp3" && fixture.Seen.Count == 1,
                "the first valid allowlisted modern URL was discarded");
        });

        await Run("Quality 017: disallowed media URL or CDN redirect stops before further requests", async () =>
        {
            const string blockedModern = """{"result":{"downloadInfo":{"codec":"mp3","bitrate":192,"transport":"raw","urls":["https://fixture.invalid/audio","https://s1.vlx.yandex.net/modern.mp3"]}}}""";
            const string blockedLegacy = """{"result":[{"codec":"mp3","bitrateInKbps":320,"downloadInfoUrl":"https://fixture.invalid/info.xml"},{"codec":"mp3","bitrateInKbps":192,"downloadInfoUrl":"https://s1.vlx.yandex.net/info.xml"}]}""";
            foreach (var stage in new[] { "modern", "legacy", "redirect" })
            {
                var fixture = new FakeHandler((request, _) => Task.FromResult(request.RequestUri!.AbsolutePath switch
                {
                    "/get-file-info" => Json(stage == "modern" ? blockedModern : aac192),
                    "/tracks/42/download-info" => Json(stage == "legacy" ? blockedLegacy : legacy192),
                    "/info.xml" => new HttpResponseMessage(HttpStatusCode.Redirect) { Headers = { Location = new Uri("https://fixture.invalid/audio") } },
                    _ => throw new InvalidOperationException("Unexpected media policy fixture route")
                }));
                using var api = new MusicApi(fixture);
                var error = await Throws<MusicException>(() => api.ResolveAudioAsync(FakeSession, "42", AudioQuality.Excellent, default));
                var expected = stage == "modern" ? 1 : stage == "legacy" ? 2 : 3;
                Assert(error.UntrustedMediaAddress && !error.Authentication && fixture.Seen.Count == expected
                    && fixture.Seen.All(sent => sent.Uri.Host != "fixture.invalid"),
                    "the media trust failure was suppressed or another request followed it");
            }
        });

        await Run("Quality 017: invalid legacy XML cannot discard confirmed modern audio", async () =>
        {
            var fixture = new FakeHandler((request, _) => Task.FromResult(request.RequestUri!.AbsolutePath switch
            {
                "/get-file-info" => Json(aac192), "/tracks/42/download-info" => Json(legacy192),
                "/info.xml" => Json("<fixture-formats><incomplete>"),
                _ => throw new InvalidOperationException("Unexpected metadata fixture route")
            }));
            using var api = new MusicApi(fixture);
            var audio = await api.ResolveAudioAsync(FakeSession, "42", AudioQuality.Excellent, default);
            Assert(audio.Codec == "aac-mp4" && audio.Bitrate == 192 && fixture.Seen.Count == 3,
                "incorrect XML from the optional fallback blocked a valid modern descriptor");
        });
    }
}
