using System.Net;
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
            foreach (var modernValid in new[] { false, true })
            {
                var calls = 0;
                using var api = new MusicApi(new FakeHandler((request, _) =>
                {
                    calls++;
                    return Task.FromResult(modernValid && request.RequestUri!.AbsolutePath == "/get-file-info"
                        ? Json(aac192) : Json("{}", HttpStatusCode.Unauthorized));
                }));
                var error = await Throws<MusicException>(() => api.ResolveAudioAsync(FakeSession, "42", AudioQuality.Excellent, default));
                Assert(error.Authentication && calls == (modernValid ? 2 : 1), "auth rejection hidden or request repeated");
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
    }
}
