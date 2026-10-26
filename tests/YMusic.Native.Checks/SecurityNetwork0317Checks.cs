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
            var body = JsonSerializer.Serialize(new { trackName = track.Title, artistName = track.Artist, duration = 180, instrumental = false, syncedLyrics = lrc });
            using var http = new HttpClient(new SecurityNetwork0317Handler((_, _) => Task.FromResult(Json(body))));
            using var client = new LrclibLyricsClient(http);
            Assert(await client.FindSynchronizedAsync(track, default) is null, "LRCLIB accepted an amplified LRC result");
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
