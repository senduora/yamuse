using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using YMusic.Native;

internal static partial class Program
{
    // Keep this literal independent of the product constant: broadening that
    // constant must fail the wire and activation checks below.
    private const string OAuthMusicScopes = "music:content music:read music:write";
    private const string OAuthDeviceReply = """{"device_code":"scope-fixture-code","user_code":"TEST-SCOPE","verification_url":"https://oauth.yandex.ru/device","interval":1,"expires_in":30}""";

    private static async Task CheckOAuthScopes()
    {
        await Run("OAuthScopes: device request sends exactly three music rights and no optional scope", async () =>
        {
            var http = new OAuthScopesHandler((_, _) => Task.FromResult(Json(OAuthDeviceReply)));
            using var api = new MusicApi(http);
            var code = await api.BeginLoginAsync("scope-fixture-device", default);
            var sent = http.Seen.Single(); var form = OAuthForm(sent.Body);
            Assert(MusicApi.RequiredScopes == OAuthMusicScopes && form["scope"] == OAuthMusicScopes,
                "device authorization did not request the exact minimal music grant");
            Assert(form.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(["client_id", "device_id", "device_name", "scope"])
                && form["device_id"] == "scope-fixture-device" && form["device_name"] == "Yamuse", "unexpected device form fields");
            Assert(sent.Method == HttpMethod.Post && sent.Uri.AbsoluteUri == "https://oauth.yandex.ru/device/code"
                && sent.Authorization is null && sent.Cookie is null && sent.ContentType == "application/x-www-form-urlencoded"
                && code.UserCode == "TEST-SCOPE", "device wire contract or token isolation changed");
        });

        await Run("OAuthScopes: invalid_scope fails once without a broad authorization fallback", async () =>
        {
            var http = new OAuthScopesHandler((_, _) => Task.FromResult(Json("""{"error":"invalid_scope","error_description":"private-oauth-response"}""", HttpStatusCode.BadRequest)));
            using var api = new MusicApi(http);
            var error = await Throws<MusicException>(() => api.BeginLoginAsync("scope-fixture-device", default));
            Assert(http.Seen.Count == 1 && OAuthForm(http.Seen[0].Body)["scope"] == OAuthMusicScopes,
                "rejected rights were retried or broadened");
            Assert(!error.Message.Contains("private-oauth-response", StringComparison.Ordinal), "raw OAuth error leaked");
        });

        await Run("OAuthScopes: omitted token response scope uses documented requested grant and canonical storage", async () =>
        {
            var http = new OAuthScopesHandler((_, _) => Task.FromResult(Json(TokenResponse)));
            using var api = new MusicApi(http);
            var session = await api.CompleteLoginAsync(TestCode(0), "scope-fixture-device", default);
            Assert(session.Scope == OAuthMusicScopes && MusicApi.HasPlayerScopes(session)
                && session.AccessToken == FakeSession.AccessToken, "omitted granted scope lost requested rights");
            var sent = http.Seen.Single(); var form = OAuthForm(sent.Body);
            Assert(sent.Uri.AbsoluteUri == "https://oauth.yandex.ru/token" && sent.Authorization is null && sent.Cookie is null
                && form["grant_type"] == "device_code" && !form.ContainsKey("scope") && !form.ContainsKey("optional_scope"),
                "token exchange broadened rights or leaked account credentials to headers");
        });

        await Run("OAuthScopes: exact reordered grant and repeated spaces normalize to canonical scope", async () =>
        {
            foreach (var value in new[] { OAuthMusicScopes, "music:write music:content music:read", " music:read  music:write music:content " })
            {
                using var api = new MusicApi(new FakeHandler((_, _) => Task.FromResult(Json(OAuthTokenWithScope(value)))));
                var session = await api.CompleteLoginAsync(TestCode(0), "scope-fixture-device", default);
                Assert(session.Scope == OAuthMusicScopes && MusicApi.HasPlayerScopes(session), "exact grant was rejected or saved without normalization");
            }
        });

        await Run("OAuthScopes: explicit missing extra duplicate malformed and non-string grants are authentication errors", async () =>
        {
            var scopes = new[]
            {
                "\"music:content music:read\"", "\"music:content music:read music:write login:email\"",
                "\"music:content music:read music:write music:read\"", "\"\"", "\"music:content,music:read,music:write\"",
                "\"music:content music:read MUSIC:WRITE\"", "\"music:content\\tmusic:read music:write\"",
                "\"music:content music:read music:write\\n\"", "null", "17", "true", "[]", "{}"
            };
            foreach (var scopeJson in scopes)
            {
                var http = new FakeHandler((_, _) => Task.FromResult(Json(OAuthTokenWithScopeJson(scopeJson))));
                using var api = new MusicApi(http);
                var error = await Throws<MusicException>(() => api.CompleteLoginAsync(TestCode(0), "scope-fixture-device", default));
                Assert(error.Authentication && http.Seen.Count == 1, "unsuitable grant was accepted, retried, or classified as a non-authentication failure");
                Assert(!error.Message.Contains(FakeSession.AccessToken, StringComparison.Ordinal), "rejected grant error leaked token");
            }
        });

        await Run("OAuthScopes: legacy broad and malformed sessions cannot reach music API direct discovery or mutations", async () =>
        {
            var http = new FakeHandler((_, _) => throw new InvalidOperationException("legacy token reached fake network"));
            using var api = new MusicApi(http);
            foreach (var scope in OAuthUnsafeSessionScopes())
            {
                var legacy = FakeSession with { Scope = scope };
                Assert(!MusicApi.HasPlayerScopes(legacy), "unsafe stored scope was classified as a player session");
                Func<Task>[] operations =
                [
                    () => api.GetAccountAsync(legacy, default),
                    () => api.GetHomeSectionsAsync(legacy, default),
                    () => api.GetLyricsAsync(legacy, "20:2", default),
                    () => api.ResolveAudioAsync(legacy, "20:2", AudioQuality.Optimal, default),
                    () => api.GetPlaylistsAsync(legacy, "12345", default),
                    () => api.SetTrackLikeAsync(legacy, "12345", new LikedId("20", "2"), true, default)
                ];
                foreach (var operation in operations)
                    Assert((await Throws<MusicException>(operation)).Authentication, "legacy music operation did not require reauthentication");
            }
            Assert(http.Seen.Count == 0, "a stored grant bypassed the central or direct music API guard");
        });

        await Run("OAuthScopes: refresh rejects legacy grants before sending refresh credentials", async () =>
        {
            var http = new FakeHandler((_, _) => throw new InvalidOperationException("legacy refresh reached fake network"));
            using var api = new MusicApi(http);
            foreach (var scope in OAuthUnsafeSessionScopes())
                Assert((await Throws<MusicException>(() => api.RefreshAsync(FakeSession with { Scope = scope }, default))).Authentication,
                    "legacy refresh did not request a new restricted login");
            Assert(http.Seen.Count == 0, "legacy refresh credentials were sent before scope verification");
        });

        await Run("OAuthScopes: scoped refresh rotates tokens normalizes rights and retains omitted refresh token", async () =>
        {
            foreach (var scopeJson in new string?[] { null, "\"music:write music:content music:read\"" })
            {
                var response = scopeJson is null ? """{"access_token":"renewed-scope-fixture","expires_in":3600}"""
                    : OAuthTokenWithScopeJson(scopeJson).Replace(FakeSession.AccessToken, "renewed-scope-fixture", StringComparison.Ordinal)
                        .Replace(",\"refresh_token\":\"fake-test-refresh\"", "", StringComparison.Ordinal);
                var http = new OAuthScopesHandler((_, _) => Task.FromResult(Json(response)));
                using var api = new MusicApi(http);
                var renewed = await api.RefreshAsync(FakeSession, default);
                var sent = http.Seen.Single(); var form = OAuthForm(sent.Body);
                Assert(renewed.Scope == OAuthMusicScopes && renewed.AccessToken == "renewed-scope-fixture"
                    && renewed.RefreshToken == FakeSession.RefreshToken && renewed.DeviceId == FakeSession.DeviceId, "restricted refresh changed identity or lost canonical grant");
                Assert(sent.Uri.AbsoluteUri == "https://oauth.yandex.ru/token" && sent.Authorization is null && sent.Cookie is null
                    && form["grant_type"] == "refresh_token" && form["refresh_token"] == FakeSession.RefreshToken
                    && !form.ContainsKey("scope") && !form.ContainsKey("optional_scope"), "refresh broadened scope or leaked credentials to headers");
            }
        });

        await Run("OAuthScopes: refresh rejects insufficient broad null and non-string returned grants", async () =>
        {
            foreach (var scopeJson in new[] { "\"music:read\"", "\"music:content music:read music:write login:email\"", "null", "[]" })
            {
                var http = new FakeHandler((_, _) => Task.FromResult(Json(OAuthTokenWithScopeJson(scopeJson))));
                using var api = new MusicApi(http);
                Assert((await Throws<MusicException>(() => api.RefreshAsync(FakeSession, default))).Authentication && http.Seen.Count == 1,
                    "refresh accepted unsuitable rights or retried a rejected grant");
            }
        });

        await Run("OAuthScopes: token revocation uses one credential form and no Authorization cookie or music request", async () =>
        {
            var http = new OAuthScopesHandler((_, _) => Task.FromResult(Json("""{"status":"ok"}""")));
            using var api = new MusicApi(http);
            await api.RevokeSessionAsync(FakeSession with { Scope = null }, default);
            var sent = http.Seen.Single(); var form = OAuthForm(sent.Body);
            Assert(sent.Method == HttpMethod.Post && sent.Uri.AbsoluteUri == "https://oauth.yandex.ru/revoke_token"
                && sent.Uri.Query.Length == 0 && sent.Authorization is null && sent.Cookie is null
                && sent.ContentType == "application/x-www-form-urlencoded", "revocation route or secret isolation changed");
            Assert(form.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(["access_token", "client_id", "client_secret"])
                && form["access_token"] == FakeSession.AccessToken && form["client_id"].Length > 0 && form["client_secret"].Length > 0,
                "revocation did not send the expected exact credential form");
        });

        await Run("OAuthScopes: failed malformed or unconfirmed revocation fails safely without raw server content", async () =>
        {
            foreach (var reply in new[]
            {
                (HttpStatusCode.Forbidden, "{\"error\":\"private-revoke-payload\"}"),
                (HttpStatusCode.Redirect, "{\"status\":\"ok\"}"),
                (HttpStatusCode.OK, "{\"status\":\"private-revoke-payload\"}"),
                (HttpStatusCode.OK, "{}"), (HttpStatusCode.OK, "null"),
                (HttpStatusCode.OK, "private-revoke-payload-malformed-json")
            })
            {
                var http = new FakeHandler((_, _) => Task.FromResult(Json(reply.Item2, reply.Item1)));
                using var api = new MusicApi(http);
                var error = await Throws<MusicException>(() => api.RevokeSessionAsync(FakeSession with { Scope = null }, default));
                Assert(error.Authentication && http.Seen.Count == 1 && !error.Message.Contains("private-revoke-payload", StringComparison.Ordinal)
                    && !error.Message.Contains(FakeSession.AccessToken, StringComparison.Ordinal), "revocation failed without a safe authentication error");
            }
        });

        await Run("OAuthScopes: DPAPI persists canonical scope and reads legacy JSON without activating it", async () =>
        {
            var directory = OAuthScopesDirectory("dpapi"); var store = new SessionStore(directory);
            store.Save(FakeSession); Assert(store.Load() == FakeSession, "canonical scope failed encrypted session roundtrip");
            var file = Path.Combine(directory, "session.dpapi");
            Assert(!Encoding.UTF8.GetString(File.ReadAllBytes(file)).Contains(OAuthMusicScopes, StringComparison.Ordinal), "stored rights were plaintext");
            OAuthWriteLegacySession(directory, FakeSession);
            var legacy = store.Load();
            Assert(legacy is { Scope: null } && legacy.AccessToken == FakeSession.AccessToken && legacy.DeviceId == FakeSession.DeviceId
                && !MusicApi.HasPlayerScopes(legacy), "backward JSON read invented a restricted grant");
            var http = new FakeHandler((_, _) => throw new InvalidOperationException("legacy DPAPI record reached network"));
            using var api = new MusicApi(http);
            Assert((await Throws<MusicException>(() => api.GetAccountAsync(legacy!, default))).Authentication && http.Seen.Count == 0,
                "legacy DPAPI read activated the stored token");
            store.Clear();
        });

        await Run("OAuthScopes: corrupt ciphertext blocks new login preserves exact bytes and explains manual revoke", async () =>
        {
            using var fixture = new OAuthScopesUiFixture("corrupt-ciphertext", FakeSession with { Scope = null },
                (_, _) => throw new InvalidOperationException("unreadable old grant reached authorization network"));
            var corrupt = File.ReadAllBytes(fixture.SessionPath); corrupt[^8] ^= 0x42;
            File.WriteAllBytes(fixture.SessionPath, corrupt);
            await Throws<CryptographicException>(() => Task.FromResult(fixture.Store.Load()));
            await OAuthAssertUnreadableLoginBlocked(fixture, corrupt);
        });

        await Run("OAuthScopes: DPAPI-valid malformed or invalid session JSON blocks replacement login without losing bytes", async () =>
        {
            foreach (var json in new[] { "{\"AccessToken\":", "[]", "null", "{\"AccessToken\":17}" })
            {
                using var fixture = new OAuthScopesUiFixture("invalid-session-json", null,
                    (_, _) => throw new InvalidOperationException("invalid old session reached authorization network"));
                OAuthWriteProtectedSession(fixture.DirectoryPath, Encoding.UTF8.GetBytes(json));
                var before = File.ReadAllBytes(fixture.SessionPath);
                await OAuthAssertUnreadableLoginBlocked(fixture, before);
            }
        });

        await Run("OAuthScopes: legacy restore revokes then clears storage and asks for new login without account request", async () =>
        {
            using var fixture = new OAuthScopesUiFixture("legacy-restore", FakeSession with { Scope = null },
                (_, _) => Task.FromResult(Json("""{"status":"ok"}""")));
            await Call(fixture.Window, "RestoreAsync");
            Assert(fixture.Http.Seen.Count == 1 && fixture.Http.Seen[0].Uri.AbsolutePath == "/revoke_token"
                && !File.Exists(fixture.SessionPath) && fixture.Store.Load() is null && OAuthSession(fixture.Window) is null,
                "legacy restore used credentials, kept revoked login, or requested more than revoke");
            Assert(Control<TextBlock>(fixture.Window, "LoginTitle").Text.Contains("заново", StringComparison.OrdinalIgnoreCase)
                && Control<TextBlock>(fixture.Window, "LoginDescription").Text.Contains("музык", StringComparison.OrdinalIgnoreCase),
                "successful legacy migration did not explain restricted reauthentication");
        });

        await Run("OAuthScopes: failed legacy restore preserves exact DPAPI bytes and cannot bypass revoke through login", async () =>
        {
            using var fixture = new OAuthScopesUiFixture("legacy-failure", FakeSession with { Scope = OAuthMusicScopes + " login:email" },
                (_, _) => Task.FromResult(Json("""{"error":"private-revoke-payload"}""", HttpStatusCode.ServiceUnavailable)));
            var before = File.ReadAllBytes(fixture.SessionPath);
            await Call(fixture.Window, "RestoreAsync"); await fixture.Window.StartLoginAsync();
            Assert(File.ReadAllBytes(fixture.SessionPath).SequenceEqual(before) && fixture.Http.Seen.Count == 2
                && fixture.Http.Seen.All(sent => sent.Uri.AbsolutePath == "/revoke_token") && OAuthSession(fixture.Window) is null,
                "failed revoke destroyed the retry record or allowed a new login/music request");
            Assert(!Control<TextBlock>(fixture.Window, "StatusText").Text.Contains("private-revoke-payload", StringComparison.Ordinal), "raw revoke failure leaked into UI");
        });

        await Run("OAuthScopes: legacy login clears only after revoke and then starts exact restricted device flow", async () =>
        {
            OAuthScopesUiFixture? active = null;
            using var fixture = new OAuthScopesUiFixture("legacy-new-login", FakeSession with { Scope = null }, (request, _) =>
            {
                if (request.RequestUri!.AbsolutePath == "/revoke_token")
                {
                    Assert(File.Exists(active!.SessionPath), "legacy record was removed before revoke confirmation");
                    return Task.FromResult(Json("""{"status":"ok"}"""));
                }
                if (request.RequestUri.AbsolutePath == "/device/code")
                {
                    Assert(!File.Exists(active!.SessionPath), "new authorization began before revoked record was cleared");
                    return Task.FromResult(Json("""{"error":"access_denied"}""", HttpStatusCode.BadRequest));
                }
                throw new InvalidOperationException("legacy login reached unexpected route");
            });
            active = fixture; await fixture.Window.StartLoginAsync();
            Assert(fixture.Http.Seen.Select(sent => sent.Uri.AbsolutePath).SequenceEqual(["/revoke_token", "/device/code"])
                && OAuthForm(fixture.Http.Seen[1].Body)["scope"] == OAuthMusicScopes && OAuthSession(fixture.Window) is null
                && !File.Exists(fixture.SessionPath), "restricted reauthorization order or scope changed");
        });

        await Run("OAuthScopes: canceled legacy revoke preserves retry record and cannot start authorization", async () =>
        {
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var fixture = new OAuthScopesUiFixture("legacy-cancel", FakeSession with { Scope = null }, async (_, token) =>
            {
                entered.TrySetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, token);
                throw new InvalidOperationException("canceled revoke completed unexpectedly");
            });
            var before = File.ReadAllBytes(fixture.SessionPath); var restore = Call(fixture.Window, "RestoreAsync");
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var cts = (CancellationTokenSource)typeof(MainWindow).GetField("_authCts", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(fixture.Window)!;
            cts.Cancel(); await restore.WaitAsync(TimeSpan.FromSeconds(3));
            Assert(File.ReadAllBytes(fixture.SessionPath).SequenceEqual(before) && fixture.Http.Seen.Count == 1
                && fixture.Http.Seen[0].Uri.AbsolutePath == "/revoke_token" && OAuthSession(fixture.Window) is null,
                "canceled revoke removed the retry record or restored an account");
        });

        await Run("OAuthScopes: unsuitable login grant never reaches account UI session or DPAPI persistence", async () =>
        {
            using var fixture = new OAuthScopesUiFixture("bad-login-grant", null, (request, _) => Task.FromResult(
                request.RequestUri!.AbsolutePath == "/device/code" ? Json(OAuthDeviceReply)
                : request.RequestUri.AbsolutePath == "/token" ? Json(OAuthTokenWithScope(OAuthMusicScopes + " login:email"))
                : throw new InvalidOperationException("unsuitable grant reached music API")));
            await fixture.Window.StartLoginAsync();
            Assert(OAuthSession(fixture.Window) is null && !File.Exists(fixture.SessionPath)
                && fixture.Http.Seen.Select(sent => sent.Uri.AbsolutePath).SequenceEqual(["/device/code", "/token"])
                && Control<TextBlock>(fixture.Window, "AccountText").Text == "Вход не выполнен",
                "unsuitable token reached persistence or account activation");
        });

        await Run("OAuthScopes: scoped restore uses account and collection without revocation or device flow", async () =>
        {
            using var fixture = new OAuthScopesUiFixture("scoped-restore", FakeSession, (request, _) => Task.FromResult(request.RequestUri!.AbsolutePath switch
            {
                "/account/status" => Json(Profile), "/account/about" => Json("""{"result":{"uid":"12345"}}"""),
                "/users/12345/likes/tracks" => Json("""{"result":{"library":{"tracks":[]}}}"""),
                _ => throw new InvalidOperationException("scoped restore reached unexpected route")
            }));
            await Call(fixture.Window, "RestoreAsync");
            Assert(OAuthSession(fixture.Window)?.Scope == OAuthMusicScopes && fixture.Store.Load()?.Scope == OAuthMusicScopes
                && Control<TextBlock>(fixture.Window, "AccountText").Text == "Fixture Account"
                && fixture.Http.Seen.Any(sent => sent.Uri.AbsolutePath == "/account/status")
                && fixture.Http.Seen.All(sent => sent.Uri.Host == "api.music.yandex.net" && sent.Authorization == "OAuth " + FakeSession.AccessToken),
                "restricted stored session no longer restores through its music account");
        });

        await Run("OAuthScopes: scoped login preserves audio likes and playlist read-write with API-only token", async () =>
        {
            var liked = false; var playlistCreated = false;
            var playlist = """{"kind":"17","owner":{"uid":"12345"},"title":"Scope fixture","revision":1,"visibility":"private","trackCount":0,"tracks":[]}""";
            var http = new OAuthScopesHandler((request, _) =>
            {
                var path = request.RequestUri!.AbsolutePath;
                if (path == "/token") return Task.FromResult(Json(TokenResponse));
                if (path == "/account/status") return Task.FromResult(Json(Profile));
                if (path == "/get-file-info") return Task.FromResult(Json("""{"result":{"downloadInfo":{"quality":"nq","codec":"mp3","bitrate":192,"transport":"raw","url":"https://s1.vlx.yandex.net/scope-fixture.mp3"}}}"""));
                if (path == "/users/12345/likes/tracks/add-multiple") { liked = true; return Task.FromResult(Json("""{"result":{"revision":1}}""")); }
                if (path == "/users/12345/likes/tracks/remove") { liked = false; return Task.FromResult(Json("""{"result":{"revision":2}}""")); }
                if (path == "/users/12345/likes/tracks") return Task.FromResult(Json(liked
                    ? """{"result":{"library":{"tracks":[{"id":"20","albumId":"2"}]}}}""" : """{"result":{"library":{"tracks":[]}}}"""));
                if (path == "/users/12345/playlists/create") { playlistCreated = true; return Task.FromResult(Json("{\"result\":" + playlist + "}")); }
                if (path == "/users/12345/playlists/17") return Task.FromResult(Json("{\"result\":" + playlist + "}"));
                if (path == "/users/12345/playlists/list") return Task.FromResult(Json("{\"result\":[" + (playlistCreated ? playlist : "") + "]}"));
                throw new InvalidOperationException("unexpected restricted music regression route " + path);
            });
            using var api = new MusicApi(http);
            var session = await api.CompleteLoginAsync(TestCode(0), "scope-fixture-device", default);
            Assert((await api.GetAccountAsync(session, default, includeAvatar: false)).Uid == "12345", "restricted account failed");
            var audio = await api.ResolveAudioAsync(session, "20:2", AudioQuality.Optimal, default);
            Assert(audio.Codec == "mp3" && audio.Bitrate == 192, "restricted session lost audio resolution");
            await api.SetTrackLikeAsync(session, "12345", new("20", "2"), true, default);
            Assert((await api.GetLikedIdsAsync(session, "12345", default)).Single().RequestId == "20:2", "restricted session lost like add/read");
            await api.SetTrackLikeAsync(session, "12345", new("20", "2"), false, default);
            Assert((await api.GetLikedIdsAsync(session, "12345", default)).Count == 0, "restricted session lost like removal");
            var created = await api.CreatePlaylistAsync(session, "Scope fixture", "private", default);
            Assert(created.Kind == "17" && (await api.GetPlaylistsAsync(session, "12345", default)).Single().Id == "17", "restricted session lost playlist create/readback/list");
            Assert(http.Seen.Where(sent => sent.Uri.Host == "api.music.yandex.net").All(sent => sent.Authorization == "OAuth " + session.AccessToken)
                && http.Seen.Where(sent => sent.Uri.Host != "api.music.yandex.net").All(sent => sent.Authorization is null && sent.Cookie is null),
                "restricted login leaked token beyond music API or lost its authenticated operations");
        });
    }

    private static string?[] OAuthUnsafeSessionScopes() =>
    [
        null, "", "music:read", OAuthMusicScopes + " login:email", OAuthMusicScopes + " music:read",
        "music:content\tmusic:read music:write", "music:content music:read music:write\n"
    ];
    private static string OAuthTokenWithScope(string scope) => OAuthTokenWithScopeJson(JsonSerializer.Serialize(scope));
    private static string OAuthTokenWithScopeJson(string scopeJson) => TokenResponse[..^1] + ",\"scope\":" + scopeJson + "}";
    private static Dictionary<string, string> OAuthForm(string body) => body.Split('&').Select(field => field.Split('=', 2))
        .ToDictionary(pair => Uri.UnescapeDataString(pair[0].Replace('+', ' ')), pair => Uri.UnescapeDataString(pair[1].Replace('+', ' ')), StringComparer.Ordinal);
    private static string OAuthScopesDirectory(string name)
    {
        var directory = Path.Combine(_directory, "oauth-scopes", name + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory); return directory;
    }
    private static Session? OAuthSession(MainWindow window) =>
        (Session?)typeof(MainWindow).GetField("_session", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window);
    private static async Task OAuthAssertUnreadableLoginBlocked(OAuthScopesUiFixture fixture, byte[] before)
    {
        await fixture.Window.StartLoginAsync();
        Assert(fixture.Http.Seen.Count == 0 && File.ReadAllBytes(fixture.SessionPath).SequenceEqual(before)
            && OAuthSession(fixture.Window) is null && Control<TextBlock>(fixture.Window, "AccountText").Text == "Вход не выполнен",
            "unreadable old grant was replaced, removed, activated, or sent to authorization network");
        var message = Control<TextBlock>(fixture.Window, "StatusText").Text;
        Assert(message.Contains("Яндекс ID", StringComparison.Ordinal) && message.Contains("отзов", StringComparison.OrdinalIgnoreCase)
            && message.Contains("удал", StringComparison.OrdinalIgnoreCase), "unreadable login did not explain manual device revoke and local session reset");
        var error = await Throws<MusicException>(() => Call(fixture.Window, "PreparePlayerLoginAsync", CancellationToken.None));
        Assert(error.Authentication && fixture.Http.Seen.Count == 0 && File.ReadAllBytes(fixture.SessionPath).SequenceEqual(before),
            "unreadable session preparation was not a non-mutating authentication failure");
    }
    private static void OAuthWriteLegacySession(string directory, Session session)
        => OAuthWriteProtectedSession(directory, JsonSerializer.SerializeToUtf8Bytes(new { session.AccessToken, session.RefreshToken, session.ExpiresAtUtc, session.DeviceId }));
    private static void OAuthWriteProtectedSession(string directory, byte[] plain)
    {
        try
        {
            var encrypted = (byte[])typeof(SessionStore).GetMethod("Protect", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [plain])!;
            File.WriteAllBytes(Path.Combine(directory, "session.dpapi"), encrypted);
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }
    private sealed record OAuthScopesRequest(HttpMethod Method, Uri Uri, string? Authorization, string? Cookie, string? ContentType, string Body);
    private sealed class OAuthScopesHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        internal List<OAuthScopesRequest> Seen { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            lock (Seen) Seen.Add(new(request.Method, request.RequestUri!, request.Headers.Authorization?.ToString(),
                request.Headers.TryGetValues("Cookie", out var cookie) ? string.Join(";", cookie) : null, request.Content?.Headers.ContentType?.MediaType, body));
            return await respond(request, cancellationToken);
        }
    }
    private sealed class OAuthScopesUiFixture : IDisposable
    {
        internal string DirectoryPath { get; }
        internal string SessionPath => Path.Combine(DirectoryPath, "session.dpapi");
        internal SessionStore Store { get; }
        internal OAuthScopesHandler Http { get; }
        internal MainWindow Window { get; }
        internal OAuthScopesUiFixture(string name, Session? saved, Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond)
        {
            DirectoryPath = OAuthScopesDirectory(name); Store = new SessionStore(DirectoryPath);
            if (saved is not null) Store.Save(saved);
            Http = new OAuthScopesHandler(respond);
            var noNetwork = new FakeHandler((_, _) => throw new InvalidOperationException("auth fixture attempted media download"));
            var player = new NativePlayer(new AudioBuffer(Path.Combine(DirectoryPath, "player-buffer"), noNetwork));
            player.SetVolume(0);
            Window = new MainWindow(api: new MusicApi(Http), store: Store, restore: false, diagnosticsDirectory: DirectoryPath,
                offline: new OfflineLibrary(Path.Combine(DirectoryPath, "music"), new AudioBuffer(Path.Combine(DirectoryPath, "download-buffer"),
                    new FakeHandler((_, _) => throw new InvalidOperationException("auth fixture attempted offline download")))),
                player: player, preferences: new PreferencesStore(Path.Combine(DirectoryPath, "preferences")));
            // Never Show: no browser, updater, shell, visible window, or real profile.
        }
        public void Dispose() => Window.Close();
    }
}
