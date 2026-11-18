using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using YMusic.Native;

internal static partial class Program
{
    private static async Task CheckHomeArtwork()
    {
        await Run("Home artwork: numeric cache versions preserve renditions and reject unsafe addresses", HomeArtworkAddresses);
        await Run("Home artwork: version changes invalidate memory and disk cache without losing old versions", HomeArtworkVersions);
        await Run("Home artwork: damaged disk entries are replaced by verified artwork", HomeArtworkDamagedEntry);
        await Run("Home artwork: a visible image recovers after cache clear while recycled images stay canceled", HomeArtworkClear);
        await Run("Home artwork: clearing rejects results before the worker receives cancellation", HomeArtworkClearBeforeCancellation);
        await Run("Home artwork: versioned redirects stay on anonymous allowed CDN hosts", HomeArtworkRedirects);
        await Run("Home artwork: raw Content-Range prevents caching even when the header cannot be parsed", HomeArtworkContentRange);
        await Run("Home artwork: personal landing playlists populate frozen images through real Home XAML", HomeArtworkCards);
    }

    private static Task HomeArtworkAddresses()
    {
        const string source = "avatars.yandex.net/get-music-user-playlist/1/fixture/%%?1730453322475";
        Assert(ArtworkUri.Metadata(source) == "https://" + source, "numeric playlist cover version was discarded before downloading");
        foreach (var (size, rendition) in new[] { (64, 100), (128, 200), (256, 400), (1024, 1000) })
        {
            Assert(ArtworkUri.TryCreate(source, size, out var uri), "versioned rendition was rejected");
            Assert(uri.AbsolutePath.EndsWith($"/{rendition}x{rendition}") && uri.Query == "?1730453322475", "rendition lost its version or requested a non-CDN size");
        }
        foreach (var host in new[] { "avatars.yandex.net", "avatars.mds.yandex.net", "avatars.mdst.yandex.net" })
            Assert(ArtworkUri.TryCreate($"https://{host}/get-music-user-playlist/1/fixture/%%?1", 128, out _), "an existing allowed CDN host lost numeric version support");
        foreach (var invalid in new[]
        {
            "https://avatars.yandex.net/a/%%?version=123", "https://avatars.yandex.net/a/%%?123&456",
            "https://avatars.yandex.net/a/%%?123%0d", "https://avatars.yandex.net/a/%%?123#fragment",
            "https://avatars.yandex.net/a/%%?123456789012345678901", "https://avatars.yandex.net.evil.test/a/%%?1",
            "https://avatars.yandex.net@evil.test/a/%%?1", "https://avatars.yandex.net:444/a/%%?1",
            "http://avatars.yandex.net/a/%%?1", "file:///a/cover.png?1"
        }) Assert(ArtworkUri.Metadata(invalid) is null && !ArtworkUri.TryCreate(invalid, 128, out _), "unsafe artwork address was accepted");
        return Task.CompletedTask;
    }

    private static async Task HomeArtworkVersions()
    {
        const string prefix = "avatars.yandex.net/get-music-user-playlist/1/fixture/%%?";
        var directory = Path.Combine(_directory, "home-artwork-versions");
        var requests = new List<string>();
        using (var cache = new ArtworkCache(directory, new FakeHandler((request, _) =>
        {
            HomeArtworkAnonymous(request);
            requests.Add(request.RequestUri!.Query);
            return Task.FromResult(HomeArtworkResponse(HomeArtworkPng(request.RequestUri.Query == "?1" ? Colors.Red : Colors.Blue)));
        })))
        {
            var first = await cache.GetAsync(prefix + "1", 256, CancellationToken.None);
            var second = await cache.GetAsync(prefix + "2", 256, CancellationToken.None);
            Assert(first is not null && second is not null && requests.SequenceEqual(new[] { "?1", "?2" }), "versioned covers did not reach their distinct CDN resources");
            Assert(HomeArtworkPixel(first!)[2] == 255 && HomeArtworkPixel(second!)[0] == 255, "a newer version reused the previous cover pixels");
            Assert(ReferenceEquals(first, await cache.GetAsync(prefix + "1", 256, CancellationToken.None)), "memory cache lost an unchanged version");
        }
        using var restarted = new ArtworkCache(directory, new FakeHandler((_, _) => throw new InvalidOperationException("warm artwork cache attempted network")));
        var diskFirst = await restarted.GetAsync(prefix + "1", 256, CancellationToken.None);
        var diskSecond = await restarted.GetAsync(prefix + "2", 256, CancellationToken.None);
        Assert(diskFirst is not null && diskSecond is not null && HomeArtworkPixel(diskFirst)[2] == 255 && HomeArtworkPixel(diskSecond)[0] == 255,
            "disk cache mixed distinct image versions after restarting");
    }

    private static async Task HomeArtworkDamagedEntry()
    {
        const string source = "https://avatars.yandex.net/get-music-content/1/damaged/200x200";
        var directory = Path.Combine(_directory, "home-artwork-damaged");
        var png = HomeArtworkPng(Colors.Coral);
        using (var initial = new ArtworkCache(directory, new FakeHandler((_, _) => Task.FromResult(HomeArtworkResponse(png)))))
            Assert(await initial.GetAsync(source, 128, default) is not null, "could not prime the artwork disk cache");

        var path = Directory.EnumerateFiles(directory, "*.img").Single();
        File.WriteAllBytes(path, []);
        using (var cache = new ArtworkCache(directory, new FakeHandler((_, _) =>
        {
            Assert(new FileInfo(path).Length == 0, "a failed disk read removed the cache path before a replacement was ready");
            return Task.FromResult(HomeArtworkResponse(png));
        })))
            Assert(await cache.GetAsync(source, 128, default) is not null, "an invalid disk entry prevented a healthy download");

        Assert(File.ReadAllBytes(path).SequenceEqual(png) && Directory.EnumerateFiles(directory).Single() == path,
            "a verified replacement was absent or left a staged file behind");
    }

    private static async Task HomeArtworkClear()
    {
        const string stableSource = "https://avatars.yandex.net/get-music-content/1/stable/200x200";
        const string loadingSource = "https://avatars.yandex.net/get-music-content/1/loading/200x200";
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstCanceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var loadingRequests = 0;
        var red = HomeArtworkPng(Colors.Red);
        var blue = HomeArtworkPng(Colors.Blue);
        using var cache = new ArtworkCache(Path.Combine(_directory, "home-artwork-clear"), new FakeHandler(async (request, token) =>
        {
            if (request.RequestUri!.AbsoluteUri == stableSource) return HomeArtworkResponse(red);
            if (Interlocked.Increment(ref loadingRequests) == 1)
            {
                using var cancellation = token.Register(() => firstCanceled.TrySetResult());
                firstEntered.SetResult();
                await releaseFirst.Task;
                token.ThrowIfCancellationRequested();
            }
            return HomeArtworkResponse(blue);
        }));
        var stable = new Image();
        var current = new Image();
        var recycled = new Image();
        var arriving = new Image();
        var window = new Window
        {
            Left = -16000, Width = 200, Height = 200, ShowActivated = false, ShowInTaskbar = false,
            Content = new StackPanel { Children = { stable, current, recycled, arriving } }
        };
        var previous = Artwork.Cache;
        Artwork.Cache = cache;
        try
        {
            window.Show();
            Artwork.SetSourceUri(stable, stableSource);
            await Until(() => Artwork.GetLoadStatus(stable) == ArtworkLoadStatus.Ready);
            var stableBitmap = stable.Source;
            Artwork.SetSourceUri(current, loadingSource);
            Artwork.SetSourceUri(recycled, loadingSource);
            await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

            var clear = cache.ClearAsync(default);
            await firstCanceled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Artwork.SetSourceUri(recycled, null);
            Artwork.SetSourceUri(arriving, loadingSource);
            Assert(Artwork.GetLoadStatus(current) == ArtworkLoadStatus.Loading
                && Artwork.GetLoadStatus(arriving) == ArtworkLoadStatus.Loading,
                "clearing the cache marked a current or new visible request unavailable");
            releaseFirst.SetResult();
            await clear.WaitAsync(TimeSpan.FromSeconds(5));

            await Until(() => Artwork.GetLoadStatus(current) == ArtworkLoadStatus.Ready
                && Artwork.GetLoadStatus(arriving) == ArtworkLoadStatus.Ready);
            Assert(ReferenceEquals(stableBitmap, stable.Source), "cache clear dropped an already displayed cover");
            Assert(Artwork.GetLoadStatus(recycled) == ArtworkLoadStatus.None && recycled.Source is null,
                "a recycled image published a completed request after clear");
            Assert(loadingRequests == 2 && ReferenceEquals(current.Source, arriving.Source)
                && HomeArtworkPixel((BitmapSource)current.Source)[0] == 255,
                "visible requests did not share one fresh, valid cover after clear");
        }
        finally
        {
            releaseFirst.TrySetResult();
            window.Close();
            Artwork.Cache = previous;
        }
    }

    private static async Task HomeArtworkClearBeforeCancellation()
    {
        const string blockingSource = "https://avatars.yandex.net/get-music-content/1/blocked/200x200";
        const string racingSource = "https://avatars.yandex.net/get-music-content/1/racing/200x200";
        var blockerEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var racingEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellationEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseBlocker = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRacing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCancellation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var requests = 0;
        var red = HomeArtworkPng(Colors.Red);
        var blue = HomeArtworkPng(Colors.Blue);
        using var cache = new ArtworkCache(Path.Combine(_directory, "home-artwork-clear-publication"), new FakeHandler(async (request, token) =>
        {
            if (request.RequestUri!.AbsoluteUri == blockingSource)
            {
                using var registration = token.Register(() =>
                {
                    cancellationEntered.SetResult();
                    releaseCancellation.Task.Wait(TimeSpan.FromSeconds(10));
                });
                blockerEntered.SetResult();
                await releaseBlocker.Task;
                token.ThrowIfCancellationRequested();
            }
            if (Interlocked.Increment(ref requests) > 1) return HomeArtworkResponse(blue);
            racingEntered.SetResult();
            await releaseRacing.Task;
            return HomeArtworkResponse(red);
        }));
        var blocker = cache.GetAsync(blockingSource, 256, default);
        try
        {
            await blockerEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var old = cache.GetAsync(racingSource, 256, default);
            var visible = cache.GetAsync(racingSource, 256, default, retryAfterClear: true);
            await racingEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var clear = cache.ClearAsync(default);
            await cancellationEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            releaseRacing.SetResult();
            await Throws<OperationCanceledException>(() => old.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert(!visible.IsCompleted && requests == 1, "artwork returned or retried while cache clear was still active");
            releaseCancellation.SetResult();
            releaseBlocker.SetResult();
            await clear.WaitAsync(TimeSpan.FromSeconds(5));
            await Throws<OperationCanceledException>(() => blocker.WaitAsync(TimeSpan.FromSeconds(5)));
            var bitmap = await visible.WaitAsync(TimeSpan.FromSeconds(5));
            Assert(bitmap is not null && requests == 2 && HomeArtworkPixel(bitmap)[0] == 255
                && ReferenceEquals(bitmap, await cache.GetAsync(racingSource, 256, default)), "stale artwork survived the clear or replaced its fresh result");
        }
        finally
        {
            releaseRacing.TrySetResult();
            releaseCancellation.TrySetResult();
            releaseBlocker.TrySetResult();
        }
    }

    private static async Task HomeArtworkRedirects()
    {
        const string source = "https://avatars.yandex.net/get-music-user-playlist/1/fixture/%%?1";
        foreach (var allowed in new[] { true, false })
        {
            var requests = 0;
            using var cache = new ArtworkCache(Path.Combine(_directory, "home-artwork-redirect-" + allowed), new FakeHandler((request, _) =>
            {
                HomeArtworkAnonymous(request); requests++;
                if (requests == 1)
                {
                    var redirect = new HttpResponseMessage(HttpStatusCode.Found);
                    redirect.Headers.Location = new Uri("https://" + (allowed ? "avatars.mds.yandex.net" : "evil.test") + "/get-music-user-playlist/1/fixture/400x400?2");
                    return Task.FromResult(redirect);
                }
                Assert(allowed && request.RequestUri!.Host == "avatars.mds.yandex.net" && request.RequestUri.Query == "?2", "redirect escaped its allowed CDN contract");
                return Task.FromResult(HomeArtworkResponse(HomeArtworkPng(Colors.Blue)));
            }));
            var bitmap = await cache.GetAsync(source, 256, CancellationToken.None);
            Assert(allowed ? bitmap is not null && requests == 2 : bitmap is null && requests == 1, "versioned redirect followed an unsafe host or rejected the allowed CDN");
        }
    }

    private static async Task HomeArtworkContentRange()
    {
        const string source = "https://avatars.yandex.net/get-music-content/1/range/400x400";
        var directory = Path.Combine(_directory, "home-artwork-content-range");
        var png = HomeArtworkPng(Colors.Blue);
        var requests = 0;
        using var cache = new ArtworkCache(directory, new FakeHandler((request, _) =>
        {
            HomeArtworkAnonymous(request);
            var response = HomeArtworkResponse(png);
            if (Interlocked.Increment(ref requests) == 1)
                Assert(response.Content.Headers.TryAddWithoutValidation("Content-Range", "invalid range"), "could not attach raw fixture header");
            return Task.FromResult(response);
        }));
        Assert(await cache.GetAsync(source, 256, default) is null && !Directory.Exists(directory),
            "an unparseable Content-Range reached the memory or disk artwork cache");
        Assert(await cache.GetAsync(source, 256, default) is { IsFrozen: true } && requests == 2,
            "rejecting the range prevented a later complete cover from loading");
    }

    private static async Task HomeArtworkCards()
    {
        var titles = new[] { "Премьера", "Дежавю", "Плейлист дня" };
        var apiRequests = 0;
        using var api = new MusicApi(new FakeHandler((request, _) =>
        {
            Assert(request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath == "/landing3", "Home cover parsing requested an unrelated endpoint");
            apiRequests++;
            return Task.FromResult(Json(JsonSerializer.Serialize(new { result = new { blocks = new[] {
                new { type = "personal-playlists", title = "Собираем для вас", entities = titles.Select((title, index) => new {
                    type = "personal-playlist", data = new { ready = true, data = new { uid = 12345, kind = 201 + index, title,
                        cover = new { type = "pic", uri = $"avatars.yandex.net/get-music-user-playlist/1/card-{index}/%%?173045332247{index}" } } }
                }).ToArray() }
            } } })));
        }));
        var sections = await api.GetHomeSectionsAsync(FakeSession, CancellationToken.None);
        Assert(apiRequests == 1 && sections.Single().Items.Count == 3 && sections.Single().Items.All(i => i.CoverUri is not null),
            "personal playlists lost versioned covers while parsing the landing response");
        var cacheField = typeof(Artwork).GetField("_cache", BindingFlags.NonPublic | BindingFlags.Static)!;
        var previous = cacheField.GetValue(null);
        var cdnRequests = 0;
        using var artwork = new ArtworkCache(Path.Combine(_directory, "home-artwork-cards"), new FakeHandler((request, _) =>
        {
            HomeArtworkAnonymous(request); Interlocked.Increment(ref cdnRequests);
            Assert(request.RequestUri!.AbsolutePath.EndsWith("/200x200"), "Home did not request its actual 200px CDN rendition");
            return Task.FromResult(HomeArtworkResponse(HomeArtworkPng(Colors.Coral)));
        }));
        Artwork.Cache = artwork;
        try
        {
            using var fixture = new SaFixture("home-artwork");
            await fixture.Activate();
            SaInvoke(fixture.Window, "SetSection", "home", "Главная");
            SaInvoke(fixture.Window, "ShowHomeSections", new object[] { sections });
            fixture.Window.UpdateLayout();
            var images = SaVisuals<Image>(Control<ItemsControl>(fixture.Window, "HomeShelves")).Where(i => Artwork.GetSourceUri(i) is not null).ToArray();
            Assert(images.Length == 3, "Home XAML dropped a personal playlist cover binding");
            await Until(() => images.All(i => i.Source is BitmapSource { IsFrozen: true }));
            Assert(cdnRequests == 3, "Home artwork did not load one anonymous image per visible card");
            await Task.Delay(350);
            App.Capture(fixture.Window, Path.Combine(_directory, "home-artwork-personal-playlists.png"));
        }
        finally { cacheField.SetValue(null, previous); }
    }

    private static void HomeArtworkAnonymous(HttpRequestMessage request)
        => Assert(request.Method == HttpMethod.Get && ArtworkUri.Valid(request.RequestUri!) && request.Headers.Authorization is null
            && !request.Headers.Contains("Cookie") && !request.Headers.Contains("X-Yandex-Music-Client"), "artwork request leaked private headers or left the CDN allowlist");
    private static HttpResponseMessage HomeArtworkResponse(byte[] bytes)
        => new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) { Headers = { ContentType = new("image/png") } } };
    private static byte[] HomeArtworkPng(Color color)
    {
        var bitmap = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[] { color.B, color.G, color.R, color.A }, 4);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream(); encoder.Save(stream); return stream.ToArray();
    }
    private static byte[] HomeArtworkPixel(BitmapSource source)
    {
        var converted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        var pixel = new byte[4]; converted.CopyPixels(pixel, 4, 0); return pixel;
    }
}
