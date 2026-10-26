using System.Net;
using System.Net.Http;
using YMusic.Native;

internal static partial class Program
{
    private static async Task CheckHomeDiscovery041()
    {
        await Run("HomeDiscovery041: exact seven-block query preserves server shelves and musical item order", HomeDiscovery041Sections);
        await Run("HomeDiscovery041: personal and chart wrappers retain identity without guessing unknown links", HomeDiscovery041Wrappers);
        await Run("HomeDiscovery041: empty unsupported malformed and failed landing use the ordered server feed", HomeDiscovery041Fallback);
        await Run("HomeDiscovery041: expired login does not fall back and unsupported feed remains an error", HomeDiscovery041Failures);
        await Run("HomeDiscovery041: cancellation before HTTP or during either endpoint cannot start another request", HomeDiscovery041Cancellation);
    }

    private static async Task HomeDiscovery041Sections()
    {
        var handler = new FakeHandler((request, _) =>
        {
            Assert(request.Method == HttpMethod.Get && request.RequestUri!.Scheme == "https"
                && request.RequestUri.Host == "api.music.yandex.net" && request.RequestUri.AbsolutePath == "/landing3"
                && Uri.UnescapeDataString(request.RequestUri.Query) == "?blocks=personalplaylists,new-releases,new-playlists,chart,artists,albums,playlists",
                "Home query lost a supported block, used the response type spelling or added unrelated parameters");
            return Task.FromResult(Json("""
                {"result":{"blocks":[
                  {"title":"Вторая по названию","entities":[
                    {"type":"playlist","data":{"uid":12345,"kind":71,"title":"Серверный плейлист"}},
                    {"type":"artist","data":{"id":"artist-3","name":"Серверный исполнитель"}},
                    {"type":"album","data":{"id":"album-7","title":"Серверный альбом"}},
                    {"type":"album","data":{"id":"album-7","title":"Серверный альбом"}}
                  ]},
                  {"title":"Первая по названию","entities":[
                    {"type":"album","data":{"id":"album-7","title":"Серверный альбом"}},
                    {"type":"album","data":{"id":"album-2","title":"Другой альбом"}}
                  ]}
                ]}}
                """));
        });
        using var api = new MusicApi(handler);
        var sections = await api.GetHomeSectionsAsync(FakeSession, default);
        Assert(handler.Seen.Count == 1 && sections.Select(section => section.Title).SequenceEqual(new[] { "Вторая по названию", "Первая по названию" }),
            "Home reordered or merged server shelves, or fetched feed after a supported landing");
        Assert(sections[0].Items.Select(item => item.Kind).SequenceEqual(new[] { "playlist", "artist", "album" })
            && sections[0].Items.Select(item => item.Id).SequenceEqual(new[] { "71", "artist-3", "album-7" })
            && sections[0].Items[0].Owner == "12345" && sections[0].Items[0].PlaylistKind == "71",
            "Home reordered mixed musical objects, lost playlist identity or changed local duplicate handling");
        Assert(sections[1].Items.Select(item => item.Id).SequenceEqual(new[] { "album-7", "album-2" })
            && sections.All(section => section.Note is null), "Home removed an object from a different shelf or reported valid objects as skipped");
    }

    private static async Task HomeDiscovery041Wrappers()
    {
        var handler = new FakeHandler((request, _) =>
        {
            Assert(request.RequestUri!.AbsolutePath == "/landing3", "Partial landing unexpectedly requested a fallback endpoint");
            return Task.FromResult(Json("""
                {"result":{"blocks":[{"title":"Серверные обёртки","entities":[
                  {"type":"personal-playlist","data":{"ready":true,"data":{"owner":{"uid":12345},"kind":201,"title":"Персональный плейлист"}}},
                  {"type":"promotion","data":{"id":"do-not-guess-album","title":"Не альбом","album":{"id":"nested-album","title":"Не извлекать"}}},
                  {"type":"chart-item","data":{"track":{"id":"chart-track","title":"Трек чарта","albums":[{"id":"chart-album"}]}}},
                  {"type":"mix-link","data":{"uid":12345,"kind":999,"title":"Не плейлист","playlist":{"uid":12345,"kind":998,"title":"Не извлекать"}}}
                ]}]}}
                """));
        });
        using var api = new MusicApi(handler);
        var sections = await api.GetHomeSectionsAsync(FakeSession, default);
        var section = sections.Single();
        Assert(handler.Seen.Count == 1 && section.Items.Select(item => item.Kind).SequenceEqual(new[] { "playlist", "track" })
            && section.Items.Select(item => item.Id).SequenceEqual(new[] { "201", "chart-track" }),
            "Home lost known wrappers or guessed catalog objects from an unsupported promotion/mix link");
        Assert(section.Items[0].Owner == "12345" && section.Items[0].PlaylistKind == "201"
            && section.Items[1].AlbumId == "chart-album" && section.Note?.Contains("пропущено объектов: 2", StringComparison.Ordinal) == true,
            "Home wrappers lost exact owner/album identity or partial-result disclosure");
    }

    private static async Task HomeDiscovery041Fallback()
    {
        foreach (var mode in new[] { "empty", "unsupported", "malformed", "network" })
        {
            var handler = new FakeHandler((request, _) =>
            {
                Assert(request.Method == HttpMethod.Get, "Home fallback changed the request method");
                if (request.RequestUri!.AbsolutePath == "/feed") return Task.FromResult(Json(HomeDiscovery041Feed));
                Assert(request.RequestUri.AbsolutePath == "/landing3", "Home fallback requested an unrelated endpoint");
                return mode switch
                {
                    "empty" => Task.FromResult(Json("""{"result":{"blocks":[]}}""")),
                    "unsupported" => Task.FromResult(Json("""{"result":{"blocks":[{"title":"Не поддерживается","entities":[{"type":"mix-link","data":{"title":"Ссылка"}}]}]}}""")),
                    "malformed" => Task.FromResult(Json("""{"result":{"blocks":{}}}""")),
                    _ => Task.FromException<HttpResponseMessage>(new HttpRequestException("Fixture network failure"))
                };
            });
            using var api = new MusicApi(handler);
            var sections = await api.GetHomeSectionsAsync(FakeSession, default);
            Assert(handler.Seen.Select(call => call.Uri.AbsolutePath).SequenceEqual(new[] { "/landing3", "/feed" })
                && sections.SelectMany(section => section.Items).Select(item => item.Id).SequenceEqual(new[] { "901", "feed-track", "feed-album", "feed-artist" }),
                "Home did not use the supported feed in server order after " + mode + " landing");
            Assert(sections.Select(section => section.Title).SequenceEqual(new[] { "Персональные подборки Яндекса", "Треки из ленты · 2030-01-01", "Первое событие", "Второе событие" })
                && sections[0].Items.Single().Owner == "12345" && sections[1].Items.Single().AlbumId == "feed-track-album",
                "Feed fallback lost server event titles, playlist owner or track album identity");
            if (mode != "empty")
                Assert(sections.All(section => section.Note?.Contains("Главная недоступна", StringComparison.Ordinal) == true), "Fallback concealed the unavailable landing");
        }
    }

    private static async Task HomeDiscovery041Failures()
    {
        var denied = new FakeHandler((_, _) => Task.FromResult(Json("{}", HttpStatusCode.Unauthorized)));
        using (var api = new MusicApi(denied))
        {
            var error = await Throws<MusicException>(() => api.GetHomeSectionsAsync(FakeSession, default));
            Assert(error.Authentication && denied.Seen.Count == 1 && denied.Seen[0].Uri.AbsolutePath == "/landing3",
                "Expired login was hidden by a feed fallback");
        }
        var unsupported = new FakeHandler((request, _) => Task.FromResult(Json(request.RequestUri!.AbsolutePath == "/landing3"
            ? """{"result":{"blocks":{}}}""" : """{"result":{}}""")));
        using (var api = new MusicApi(unsupported))
        {
            var error = await Throws<MusicException>(() => api.GetHomeSectionsAsync(FakeSession, default));
            Assert(!error.Authentication && unsupported.Seen.Select(call => call.Uri.AbsolutePath).SequenceEqual(new[] { "/landing3", "/feed" }),
                "Unsupported landing and feed produced invented recommendations or the wrong failure");
        }
    }

    private static async Task HomeDiscovery041Cancellation()
    {
        var untouched = new FakeHandler((_, _) => Task.FromResult(Json(HomeDiscovery041Feed)));
        using (var api = new MusicApi(untouched))
        using (var canceled = new CancellationTokenSource())
        {
            canceled.Cancel();
            await Throws<OperationCanceledException>(() => api.GetHomeSectionsAsync(FakeSession, canceled.Token));
            Assert(untouched.Seen.Count == 0, "Pre-canceled Home load sent HTTP");
        }
        foreach (var endpoint in new[] { "/landing3", "/feed" })
        {
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var handler = new FakeHandler(async (request, token) =>
            {
                if (endpoint == "/feed" && request.RequestUri!.AbsolutePath == "/landing3") return Json("""{"result":{"blocks":[]}}""");
                Assert(request.RequestUri!.AbsolutePath == endpoint, "Cancellation started an unexpected endpoint");
                entered.TrySetResult();
                await Task.Delay(Timeout.Infinite, token);
                return Json(HomeDiscovery041Feed);
            });
            using var api = new MusicApi(handler);
            using var canceled = new CancellationTokenSource();
            var loading = api.GetHomeSectionsAsync(FakeSession, canceled.Token);
            try
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                canceled.Cancel();
                await Throws<OperationCanceledException>(async () => await loading.WaitAsync(TimeSpan.FromSeconds(5)));
            }
            finally { canceled.Cancel(); }
            Assert(handler.Seen.Select(call => call.Uri.AbsolutePath).SequenceEqual(endpoint == "/landing3" ? new[] { "/landing3" } : new[] { "/landing3", "/feed" }),
                "Cancellation was treated as a fallback condition or sent another request");
        }
    }

    private const string HomeDiscovery041Feed = """
        {"result":{
          "generatedPlaylists":[{"ready":true,"data":{"uid":12345,"kind":901,"title":"Персональный fixture"}}],
          "days":[{"day":"2030-01-01","tracksToPlay":[{"id":"feed-track","title":"Серверный трек","albums":[{"id":"feed-track-album"}]}],"events":[
            {"title":"Первое событие","albums":[{"album":{"id":"feed-album","title":"Альбом из ленты"}}]},
            {"title":"Второе событие","artists":[{"artist":{"id":"feed-artist","name":"Исполнитель из ленты"}}]}
          ]}]
        }}
        """;
}
