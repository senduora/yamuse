using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
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
        await Run("Home artwork: versioned redirects stay on anonymous allowed CDN hosts", HomeArtworkRedirects);
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
