using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
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
    private const string OAuthConfirmedProfile = """{"result":{"account":{"uid":12345,"displayName":"Fixture","avatarId":"123/fixture"}}}""";
    private const string OAuthRotatedTokens = """{"access_token":"fixture-new-access","refresh_token":"fixture-new-refresh","expires_in":3600}""";

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

        await Run("OAuthScopes: malformed JSON or verification address fails with a controlled response", async () =>
        {
            foreach (var reply in new[] { "null", "[]", "\"fixture-payload\"",
                OAuthDeviceReply[..^1] + ",\"verification_url\":\"https://oauth.yandex.ru/device\"}",
                OAuthDeviceReply.Replace("https://oauth.yandex.ru/device", "relative-fixture-payload", StringComparison.Ordinal),
                OAuthDeviceReply.Replace("\"scope-fixture-code\"", "17", StringComparison.Ordinal) })
            {
                var http = new OAuthScopesHandler((_, _) => Task.FromResult(Json(reply)));
                using var api = new MusicApi(http);
                var error = await Throws<MusicException>(() => api.BeginLoginAsync("scope-fixture-device", default));
                Assert(http.Seen.Count == 1 && !error.Message.Contains("fixture-payload", StringComparison.Ordinal),
                    "malformed OAuth reply appeared in the error or scheduled a retry");
            }
        });

        await Run("OAuthScopes: device code expires across response body and delayed polling without a token POST", async () =>
        {
            var shortReply = OAuthDeviceReply.Replace("\"expires_in\":30", "\"expires_in\":1", StringComparison.Ordinal);
            using (var fixture = new OAuthScopesUiFixture("device-code-body-expiry", null, (request, _) =>
            {
                Assert(request.RequestUri!.AbsolutePath == "/device/code", "an expired code reached the token endpoint");
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new OAuthBodyContent(shortReply, token => Task.Delay(1250, token))
                });
            }))
            {
                var panel = Control<FrameworkElement>(fixture.Window, "CodePanel");
                var visibility = System.ComponentModel.DependencyPropertyDescriptor.FromProperty(UIElement.VisibilityProperty, typeof(FrameworkElement))!;
                var shown = false;
                EventHandler observe = (_, _) => shown |= panel.Visibility == Visibility.Visible;
                visibility.AddValueChanged(panel, observe);
                try { await fixture.Window.StartLoginAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
                finally { visibility.RemoveValueChanged(panel, observe); }

                Assert(!shown && fixture.Http.Seen.Count == 1
                    && Control<TextBlock>(fixture.Window, "StatusText").Text.Contains("Время действия кода истекло", StringComparison.Ordinal),
                    "the delayed device response presented an expired code or attempted exchange");
            }

            var http = new FakeHandler((_, _) => Task.FromResult(Json(OAuthDeviceReply)));
            using var api = new MusicApi(http);
            var code = await api.BeginLoginAsync("test-device", default);
            // Model time spent before CompleteLoginAsync without launching the browser.
            var aged = code with { ReceivedAtTimestamp = code.ReceivedAtTimestamp - System.Diagnostics.Stopwatch.Frequency * 60 };
            var expired = await Throws<MusicException>(() => api.CompleteLoginAsync(aged, "test-device", default));
            Assert(expired.Message.Contains("Время действия кода истекло", StringComparison.Ordinal) && http.Seen.Count == 1,
                "starting the poll loop extended the original deadline");
        });

        await Run("OAuthScopes: a server poll minimum above 60 and slow down above 120 are honored", async () =>
        {
            var reply = OAuthDeviceReply.Replace("\"interval\":1", "\"interval\":75", StringComparison.Ordinal);
            var http = new FakeHandler((request, _) =>
            {
                Assert(request.RequestUri!.AbsolutePath == "/device/code", "an interval outside the code lifetime started a poll");
                return Task.FromResult(Json(reply));
            });
            using var api = new MusicApi(http);
            var code = await api.BeginLoginAsync("test-device", default);
            Assert(code.IntervalSeconds == 75, "the server's initial minimum was shortened");
            var slow = MusicApi.NextDevicePollIntervalSeconds(120, MusicApi.DevicePollDelay.SlowDown);
            Assert(slow == 125 && MusicApi.NextDevicePollIntervalSeconds(slow, MusicApi.DevicePollDelay.SlowDown) == 130
                && MusicApi.NextDevicePollIntervalSeconds(slow, MusicApi.DevicePollDelay.TemporaryFailure) >= slow,
                "a retry shortened the already required server interval");
            var expires = await Throws<MusicException>(() =>
                api.CompleteLoginAsync(code with { ExpiresInSeconds = 1 }, "test-device", default).WaitAsync(TimeSpan.FromSeconds(4)));
            Assert(expires.Message.Contains("Время действия кода истекло", StringComparison.Ordinal) && http.Seen.Count == 1,
                "a minimum exceeding the remaining lifetime did not wait for expiry");
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

        await Run("OAuthScopes: transient refresh failures can be retried with the same saved grant", async () =>
        {
            HttpStatusCode[] failures = [HttpStatusCode.RequestTimeout, HttpStatusCode.TooManyRequests, HttpStatusCode.InternalServerError,
                HttpStatusCode.ServiceUnavailable, HttpStatusCode.Unauthorized];
            var attempts = 0;
            var stored = FakeSession with { ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1) };
            var store = new SessionStore(OAuthScopesDirectory("refresh-temporary"));
            store.Save(stored);
            var http = new OAuthScopesHandler((_, _) => Task.FromResult(attempts < failures.Length
                ? Json("{\"error\":\"fixture-private-response\"}", failures[attempts++]) : Json(OAuthRotatedTokens)));
            using var api = new MusicApi(http);
            for (var i = 0; i < failures.Length; i++)
            {
                var error = await Throws<MusicException>(() => api.RefreshAsync(store.Load()!, default));
                Assert(!error.Authentication && error.Message.Contains("временно недоступен", StringComparison.Ordinal)
                    && !error.Message.Contains("fixture-private-response", StringComparison.Ordinal)
                    && http.Seen.Count == i + 1 && store.Load() == stored,
                    "refresh misclassified the response, retried automatically, or changed the saved grant");
            }
            var renewed = await api.RefreshAsync(store.Load()!, default);
            Assert(renewed.RefreshToken == "fixture-new-refresh" && http.Seen.Count == failures.Length + 1
                && http.Seen.All(request => OAuthForm(request.Body)["refresh_token"] == stored.RefreshToken),
                "the saved refresh token could not be reused on the next request");
        });

        await Run("OAuthScopes: refresh rejects insufficient broad null and non-string returned grants", async () =>
        {
            foreach (var scopeJson in new[] { "\"music:read\"", "\"music:content music:read music:write login:email\"", "null", "[]" })
            {
                var http = new FakeHandler((_, _) => Task.FromResult(Json(OAuthTokenWithScopeJson(scopeJson))));
                using var api = new MusicApi(http);
                var error = await Throws<MusicException>(() => api.RefreshAsync(FakeSession, default));
                Assert(error.Authentication == scopeJson.StartsWith('"') && http.Seen.Count == 1,
                    "refresh accepted unsuitable rights or retried a rejected grant");
            }
        });

        await Run("OAuthScopes: successful token rotations preserve active wave ownership only for that session", async () =>
        {
            const string wave = """{"result":{"radioSessionId":"fixture-radio","batchId":"fixture-batch","sequence":[{"track":{"id":42,"title":"Fixture track","available":true}}]}}""";
            var http = new FakeHandler((request, _) => Task.FromResult(request.RequestUri!.AbsolutePath switch
            {
                "/rotor/session/new" or "/rotor/session/fixture-radio/tracks" => Json(wave),
                "/rotor/session/fixture-radio/feedback/" => Json("{\"result\":{}}"),
                "/token" => Json(OAuthRotatedTokens),
                _ => throw new InvalidOperationException("Unexpected wave refresh fixture route")
            }));
            using var api = new MusicApi(http);
            var first = await api.StartWaveAsync(FakeSession, default);
            var refreshed = await api.RefreshAsync(FakeSession, default);
            var refreshedAgain = await api.RefreshAsync(refreshed, default);
            var more = await api.MoreWaveAsync(refreshedAgain, first.SessionId, default);
            await api.SendWaveFeedbackAsync(refreshedAgain, more.SessionId, more.BatchId, "trackStarted", "42", 0, default);
            Assert(http.Seen.Count == 5 && http.Seen.Skip(3).All(sent => sent.Authorization == "OAuth fixture-new-access"),
                "the active wave did not follow refresh with the renewed credentials");
            var unrelated = refreshedAgain with { };
            await Throws<MusicException>(() => api.MoreWaveAsync(unrelated, more.SessionId, default));
            api.ResetWaveState();
            await Throws<MusicException>(() => api.MoreWaveAsync(refreshedAgain, more.SessionId, default));
            Assert(http.Seen.Count == 5, "an unrelated or reset session reached the current wave endpoint");
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
                (HttpStatusCode.PartialContent, "{\"status\":\"ok\"}"),
                (HttpStatusCode.OK, "{\"status\":\"ok\",\"error\":\"private-revoke-payload\"}"),
                (HttpStatusCode.OK, """{"status":"private-revoke-payload","status":"ok"}"""),
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

        await Run("OAuthScopes: session storage tolerates absence and preserves a locked record", async () =>
        {
            var directory = Path.Combine(OAuthScopesDirectory("session-storage"), "nested", "sessions");
            var store = new SessionStore(directory);
            var path = Path.Combine(directory, "session.dpapi");
            Assert(store.Load() is null, "missing session directory was not treated as missing");
            store.Clear();
            Assert(!Directory.Exists(directory), "clear created a missing session directory");

            store.Save(FakeSession);
            var replacement = FakeSession with { AccessToken = "fixture-replacement-token" };
            using (File.Open(path, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                await Throws<IOException>(() => Task.Run(() => store.Save(replacement)));
                await Throws<IOException>(() => Task.Run(store.Clear));
            }
            Assert(store.Load() == FakeSession && Directory.GetFileSystemEntries(directory).SequenceEqual([path]),
                "failed replacement or deletion changed the session or left a staging file");

            var file = new FileInfo(path);
            var security = file.GetAccessControl(AccessControlSections.Access);
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: true);
            var fixtureSid = new SecurityIdentifier("S-1-5-21-111111111-222222222-333333333-4444");
            security.AddAccessRule(new FileSystemAccessRule(fixtureSid, FileSystemRights.ReadData, AccessControlType.Deny));
            file.SetAccessControl(security);
            var expected = file.GetAccessControl(AccessControlSections.Access).GetSecurityDescriptorSddlForm(AccessControlSections.Access);
            store.Save(replacement);
            Assert(store.Load() == replacement && Directory.GetFileSystemEntries(directory).SequenceEqual([path])
                && file.GetAccessControl(AccessControlSections.Access).GetSecurityDescriptorSddlForm(AccessControlSections.Access) == expected,
                "replacement did not publish the new record as the only entry");
            var sibling = Path.Combine(directory, "fixture.txt");
            File.WriteAllText(sibling, "keep");
            store.Clear();
            store.Clear();
            Assert(store.Load() is null && File.ReadAllText(sibling) == "keep",
                "clear did not remove exactly the saved session");
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
            foreach (var json in new[] { "{\"AccessToken\":", "[]", "null", "{\"AccessToken\":17}", "{}", "{\"AccessToken\":\"invalid token\"}" })
            {
                using var fixture = new OAuthScopesUiFixture("invalid-session-json", null,
                    (_, _) => throw new InvalidOperationException("invalid old session reached authorization network"));
                OAuthWriteProtectedSession(fixture.DirectoryPath, Encoding.UTF8.GetBytes(json));
                var before = File.ReadAllBytes(fixture.SessionPath);
                await OAuthAssertUnreadableLoginBlocked(fixture, before);
            }
        });

        await Run("OAuthScopes: damaged saved login keeps recovery instructions until repair or removal", async () =>
        {
            foreach (var replaced in new[] { false, true })
            foreach (var repaired in new[] { false, true })
            {
                var expired = FakeSession with { ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(-5) };
                var healthy = FakeSession with { AccessToken = "fixture-repaired-access", ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(30) };
                var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                using var fixture = new OAuthScopesUiFixture("damaged-login-card", replaced ? expired : null, async (request, _) =>
                {
                    switch (request.RequestUri!.AbsolutePath)
                    {
                        case "/token" when replaced:
                            entered.TrySetResult();
                            await release.Task;
                            return Json("""{"error":"invalid_grant"}""", HttpStatusCode.BadRequest);
                        case "/account/status" when request.Headers.Authorization?.Parameter == healthy.AccessToken:
                            return Json(OAuthConfirmedProfile);
                        case "/users/12345/likes/tracks": return Json("""{"result":{"library":{"tracks":[]}}}""");
                        case "/device/code": return Json("""{"error":"fixture-stop"}""", HttpStatusCode.BadRequest);
                        default: throw new InvalidOperationException("Damaged login reached an unexpected route");
                    }
                });
                var title = Control<TextBlock>(fixture.Window, "LoginTitle");
                byte[] corrupt = [1, 2, 3, 4, 5];
                if (replaced)
                {
                    var restore = Call(fixture.Window, "RestoreAsync");
                    try
                    {
                        await entered.Task.WaitAsync(TimeSpan.FromSeconds(4));
                        File.WriteAllBytes(fixture.SessionPath, corrupt);
                    }
                    finally { release.TrySetResult(); }
                    await restore.WaitAsync(TimeSpan.FromSeconds(4));
                    Assert(title.Text.Contains("Проверьте", StringComparison.Ordinal)
                        && File.ReadAllBytes(fixture.SessionPath).SequenceEqual(corrupt),
                        "retirement removed the independent damaged replacement");
                    await fixture.Window.StartLoginAsync();
                }
                else
                {
                    File.WriteAllBytes(fixture.SessionPath, corrupt);
                    await Call(fixture.Window, "RestoreAsync");
                }

                var baseline = replaced ? 1 : 0;
                AssertDamagedCard();
                PreloadInvoke(fixture.Window, "HideStatusNotification");
                Assert(Control<Border>(fixture.Window, "StatusNotification").Visibility == Visibility.Collapsed,
                    "the fixture did not dismiss the transient notification");
                AssertDamagedCard();
                await fixture.Window.StartLoginAsync();
                AssertDamagedCard();

                if (repaired) fixture.Store.Save(healthy);
                else fixture.Store.Clear();
                await fixture.Window.StartLoginAsync();
                string[] next = repaired ? ["/account/status", "/users/12345/likes/tracks"] : ["/device/code"];
                Assert(fixture.Http.Seen.Skip(baseline).Select(sent => sent.Uri.AbsolutePath).SequenceEqual(next)
                    && (repaired ? OAuthSession(fixture.Window) == healthy
                        && Control<Border>(fixture.Window, "LoginPanel").Visibility == Visibility.Collapsed
                        : !File.Exists(fixture.SessionPath) && title.Text != "Сохранённый вход повреждён"),
                    "repair did not resume the saved login or removal did not permit a new code");

                void AssertDamagedCard()
                {
                    var text = Control<TextBlock>(fixture.Window, "LoginDescription").Text;
                    Assert(title.Text == "Сохранённый вход повреждён" && text.Contains("Яндекс ID", StringComparison.Ordinal)
                        && text.Contains("удалите повреждённый файл session.dpapi", StringComparison.Ordinal)
                        && fixture.Http.Seen.Count == baseline && OAuthSession(fixture.Window) is null
                        && File.ReadAllBytes(fixture.SessionPath).SequenceEqual(corrupt),
                        "damaged login lost its recovery instructions, changed on disk, or sent an authorization request");
                }
            }
        });

        await Run("OAuthScopes: a locked session survives startup and resumes without a device code", async () =>
        {
            using var fixture = new OAuthScopesUiFixture("locked-session", FakeSession,
                (request, _) => Task.FromResult(request.RequestUri!.AbsolutePath switch
                {
                    "/account/status" => Json(OAuthConfirmedProfile),
                    "/users/12345/likes/tracks" => Json("""{"result":{"library":{"tracks":[]}}}"""),
                    _ => throw new InvalidOperationException("Temporary session blockage triggered device authorization")
                }));
            var before = File.ReadAllBytes(fixture.SessionPath);
            using (File.Open(fixture.SessionPath, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                await Throws<IOException>(() => Task.FromResult(fixture.Store.Load()));
                await Call(fixture.Window, "RestoreAsync");
                Assert(Control<TextBlock>(fixture.Window, "StatusText").Text.Contains("временно недоступен", StringComparison.Ordinal),
                    "startup treated a sharing lock as a damaged record");
                await fixture.Window.StartLoginAsync();
                Assert(fixture.Http.Seen.Count == 0
                    && Control<TextBlock>(fixture.Window, "StatusText").Text.Contains("временно недоступен", StringComparison.Ordinal),
                    "retry while locked reached the network or lost the temporary failure explanation");
            }
            Assert(File.ReadAllBytes(fixture.SessionPath).SequenceEqual(before), "locked startup changed the stored record");
            await fixture.Window.StartLoginAsync();
            Assert(PreloadGet<Account?>(fixture.Window, "_account")?.Uid == "12345" && OAuthSession(fixture.Window) == FakeSession
                && fixture.Http.Seen.Select(sent => sent.Uri.AbsolutePath).SequenceEqual(["/account/status", "/users/12345/likes/tracks"])
                && fixture.Http.Seen.All(sent => sent.Authorization == "OAuth " + FakeSession.AccessToken),
                "retry after unlock did not restore the original account");
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
            var message = Control<TextBlock>(fixture.Window, "StatusText").Text;
            Assert(!message.Contains("private-revoke-payload", StringComparison.Ordinal), "raw revoke failure leaked into UI");
            Assert(message.Contains("Яндекс ID", StringComparison.Ordinal) && message.Contains("session.dpapi", StringComparison.Ordinal),
                "failed revoke did not explain how to retry after removing the old grant manually");
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
            var drain = Call(fixture.Window, "DrainExitWritesAsync");
            Assert(!drain.IsCompleted, "exit did not observe an admitted legacy revoke");
            var cts = (CancellationTokenSource)typeof(MainWindow).GetField("_authCts", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(fixture.Window)!;
            cts.Cancel(); await restore.WaitAsync(TimeSpan.FromSeconds(3));
            await drain.WaitAsync(TimeSpan.FromSeconds(3));
            Assert(File.ReadAllBytes(fixture.SessionPath).SequenceEqual(before) && fixture.Http.Seen.Count == 1
                && fixture.Http.Seen[0].Uri.AbsolutePath == "/revoke_token" && OAuthSession(fixture.Window) is null,
                "canceled revoke removed the retry record or restored an account");
        });

        await Run("OAuthScopes: exit waits for legacy revoke and prevents following device authorization", async () =>
        {
            foreach (var confirmed in new[] { false, true })
            {
                var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var original = FakeSession with { Scope = null };
                using var fixture = new OAuthScopesUiFixture("legacy-exit-drain", original, async (request, _) =>
                {
                    Assert(request.RequestUri!.AbsolutePath == "/revoke_token", "Unexpected auth during exit");
                    entered.TrySetResult();
                    await release.Task;
                    return confirmed ? Json("""{"status":"ok"}""") : Json("{}", HttpStatusCode.ServiceUnavailable);
                });
                var login = fixture.Window.StartLoginAsync();
                try
                {
                    await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
                    R4Set(fixture.Window, "_closeCommitInProgress", true);
                    var drain = Call(fixture.Window, "DrainExitWritesAsync");
                    Assert(!drain.IsCompleted && fixture.Store.Load() == original, "exit finished before legacy revocation completed");
                    release.TrySetResult();
                    await drain.WaitAsync(TimeSpan.FromSeconds(3));
                    Assert(fixture.Store.Load() == (confirmed ? null : original) && fixture.Http.Seen.Count == 1,
                        "exit did not honor the revoke result or sent a new device request");
                    await login.WaitAsync(TimeSpan.FromSeconds(3));
                }
                finally
                {
                    release.TrySetResult();
                    R4Set(fixture.Window, "_closeCommitInProgress", false);
                }
            }
        });

        await Run("OAuthScopes: a confirmed legacy revoke preserves a replacement session", async () =>
        {
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var fixture = new OAuthScopesUiFixture("legacy-replaced-during-revoke", FakeSession with { Scope = null },
                async (request, _) =>
                {
                    if (request.RequestUri!.AbsolutePath == "/revoke_token")
                    {
                        entered.TrySetResult();
                        await release.Task;
                        return Json("""{"status":"ok"}""");
                    }
                    if (request.RequestUri.AbsolutePath == "/account/status") return Json(OAuthConfirmedProfile);
                    if (request.RequestUri.AbsolutePath == "/users/12345/likes/tracks") return Json("""{"result":{"library":{"tracks":[]}}}""");
                    throw new InvalidOperationException("A replaced session was ignored");
                });
            var login = fixture.Window.StartLoginAsync();
            try
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
                fixture.Store.Save(FakeSession);
                var replacement = File.ReadAllBytes(fixture.SessionPath);
                release.TrySetResult();
                await login.WaitAsync(TimeSpan.FromSeconds(3));
                Assert(fixture.Http.Seen.Count == 1 && File.ReadAllBytes(fixture.SessionPath).SequenceEqual(replacement),
                    "a confirmed revoke deleted or replaced another saved grant");
            }
            finally { release.TrySetResult(); }
            await fixture.Window.StartLoginAsync();
            Assert(OAuthSession(fixture.Window) == FakeSession
                && fixture.Http.Seen.All(sent => sent.Uri.AbsolutePath != "/device/code"),
                "the replacement session could not be restored after confirming the obsolete revoke");
        });

        await Run("OAuthScopes: confirmed legacy revoke retries only local deletion after a sharing failure", async () =>
        {
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var fixture = new OAuthScopesUiFixture("legacy-clear-retry", FakeSession with { Scope = null }, async (request, _) =>
            {
                if (request.RequestUri!.AbsolutePath == "/device/code")
                    return Json("""{"error":"access_denied"}""", HttpStatusCode.BadRequest);
                Assert(request.RequestUri.AbsolutePath == "/revoke_token", "Unexpected legacy retry route");
                entered.TrySetResult();
                await release.Task;
                return Json("""{"status":"ok"}""");
            });
            var restore = Call(fixture.Window, "RestoreAsync");
            try
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
                using (File.Open(fixture.SessionPath, FileMode.Open, FileAccess.Read, FileShare.None))
                {
                    release.TrySetResult();
                    await restore.WaitAsync(TimeSpan.FromSeconds(3));
                    Assert(!OAuthFlushSessionChange(fixture.Window) && fixture.Http.Seen.Count == 1,
                        "failed local deletion lost the confirmed revoke or began a new login");
                }
            }
            finally { release.TrySetResult(); }
            await fixture.Window.StartLoginAsync();
            Assert(fixture.Store.Load() is null
                && fixture.Http.Seen.Select(sent => sent.Uri.AbsolutePath).SequenceEqual(["/revoke_token", "/device/code"]),
                "repeating login replaced the blocked record before deletion or repeated the confirmed revoke");
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
            var guest = PreloadGet<CancellationTokenSource>(fixture.Window, "_accountCts").Token;
            var published = File.ReadAllBytes(fixture.SessionPath);
            await Call(fixture.Window, "RestoreAsync");
            Assert(OAuthSession(fixture.Window)?.Scope == OAuthMusicScopes && fixture.Store.Load()?.Scope == OAuthMusicScopes
                && Control<TextBlock>(fixture.Window, "AccountText").Text == "Fixture Account"
                && File.ReadAllBytes(fixture.SessionPath).SequenceEqual(published)
                && fixture.Http.Seen.Any(sent => sent.Uri.AbsolutePath == "/account/status")
                && fixture.Http.Seen.All(sent => sent.Uri.Host == "api.music.yandex.net" && sent.Authorization == "OAuth " + FakeSession.AccessToken),
                "restricted stored session no longer restores through its music account");
            Assert(guest.IsCancellationRequested && !PreloadGet<CancellationTokenSource>(fixture.Window, "_accountCts").IsCancellationRequested,
                "activation did not invalidate guest work and open a live account scope");
        });

        await Run("OAuthScopes: legacy device ID migration waits for the confirmed profile and preserves its source", async () =>
        {
            foreach (var replaced in new[] { false, true })
            {
                var original = FakeSession with { DeviceId = "", ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(30) };
                var replacement = FakeSession with { AccessToken = "fixture-replacement-access" };
                var profiles = 0;
                using var fixture = new OAuthScopesUiFixture("legacy-device-profile", original, (request, _) =>
                    Task.FromResult(request.RequestUri!.AbsolutePath switch
                    {
                        "/account/status" => ++profiles == 1 ? Json("{}", HttpStatusCode.ServiceUnavailable) : Json(OAuthConfirmedProfile),
                        "/users/12345/likes/tracks" => Json("""{"result":{"library":{"tracks":[]}}}"""),
                        _ => throw new InvalidOperationException("Device ID migration started an unexpected OAuth exchange")
                    }));
                var originalBytes = File.ReadAllBytes(fixture.SessionPath);
                await Call(fixture.Window, "RestoreAsync");
                Assert(OAuthSession(fixture.Window) is null && OAuthFlushSessionChange(fixture.Window)
                    && File.ReadAllBytes(fixture.SessionPath).SequenceEqual(originalBytes),
                    "an unconfirmed profile published the normalized device ID");

                if (replaced) fixture.Store.Save(replacement);
                var beforeRetry = File.ReadAllBytes(fixture.SessionPath);
                await fixture.Window.StartLoginAsync();
                var active = OAuthSession(fixture.Window);
                Assert(active is not null && Guid.TryParse(active.DeviceId, out _)
                    && fixture.Http.Seen.Select(sent => sent.Uri.AbsolutePath)
                        .SequenceEqual(["/account/status", "/account/status", "/users/12345/likes/tracks"]),
                    "the legacy session did not confirm its profile with the normalized device ID");
                Assert(replaced ? File.ReadAllBytes(fixture.SessionPath).SequenceEqual(beforeRetry) : fixture.Store.Load() == active,
                    "device ID migration changed a replacement or failed to publish its own record");
            }
        });

        await Run("OAuthScopes: stored access denial refreshes once and respects temporary or terminal failures", async () =>
        {
            foreach (var outcome in new[] { "success", "temporary-refresh", "invalid-grant" })
            {
                var refreshAttempts = 0;
                using var fixture = new OAuthScopesUiFixture("early-access-denial", FakeSession, (request, _) =>
                {
                    if (request.RequestUri!.AbsolutePath == "/token")
                    {
                        refreshAttempts++;
                        return Task.FromResult(outcome switch
                        {
                            "temporary-refresh" when refreshAttempts == 1 => Json("{}", HttpStatusCode.ServiceUnavailable),
                            "invalid-grant" => Json("""{"error":"invalid_grant"}""", HttpStatusCode.BadRequest),
                            _ => Json(OAuthRotatedTokens)
                        });
                    }
                    return Task.FromResult(request.RequestUri.AbsolutePath switch
                    {
                        "/account/status" when request.Headers.Authorization?.Parameter == FakeSession.AccessToken
                            => Json("{}", HttpStatusCode.Unauthorized),
                        "/account/status" => Json(OAuthConfirmedProfile),
                        "/users/12345/likes/tracks" => Json("""{"result":{"library":{"tracks":[]}}}"""),
                        _ => throw new InvalidOperationException("Profile recovery started an unexpected authorization exchange")
                    });
                });
                var before = File.ReadAllBytes(fixture.SessionPath);
                await Call(fixture.Window, "RestoreAsync");
                Assert(refreshAttempts == 1 && fixture.Http.Seen.Take(2).Select(reply => reply.Uri.AbsolutePath).SequenceEqual(["/account/status", "/token"]),
                    "a first access refusal did not use exactly one refresh for the saved session");
                if (outcome == "temporary-refresh")
                {
                    Assert(OAuthSession(fixture.Window) is null && File.ReadAllBytes(fixture.SessionPath).SequenceEqual(before),
                        "a temporary refresh failure deleted or replaced the old grant");
                    await fixture.Window.StartLoginAsync();
                    Assert(refreshAttempts == 2, "explicit retry did not reuse the original grant");
                }
                if (outcome == "invalid-grant")
                {
                    Assert(fixture.Store.Load() is null && OAuthSession(fixture.Window) is null,
                        "a terminal rejection retained the unusable grant or activated the account");
                }
                else
                {
                    Assert(OAuthSession(fixture.Window) == fixture.Store.Load() && fixture.Store.Load() is { RefreshToken: "fixture-new-refresh" }
                        && fixture.Http.Seen.Last(reply => reply.Uri.AbsolutePath == "/account/status").Authorization == "OAuth fixture-new-access",
                        "profile recovery did not commit a valid rotation and activate the account");
                }
                Assert(fixture.Http.Seen.Where(sent => sent.Uri.AbsolutePath == "/token")
                    .All(sent => OAuthForm(sent.Body)["refresh_token"] == FakeSession.RefreshToken),
                    "profile recovery did not exchange the original refresh token");
            }
        });

        await Run("OAuthScopes: profile 401 after a validated refresh retains the new grant until a profile retry", async () =>
        {
            foreach (var expired in new[] { false, true })
            {
                var stored = FakeSession with { ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(expired ? -5 : 30) };
                // OAuth may renew the refresh token while retaining the same access token.
                var access = expired ? stored.AccessToken : "fixture-new-access";
                using var fixture = new OAuthScopesUiFixture("restored-profile-denied", stored, (request, _) =>
                    Task.FromResult(request.RequestUri!.AbsolutePath switch
                    {
                        "/token" => Json(OAuthRotatedTokens.Replace("fixture-new-access", access, StringComparison.Ordinal)),
                        "/account/status" => Json("{}", HttpStatusCode.Unauthorized),
                        _ => throw new InvalidOperationException("Unconfirmed restored profile started another flow")
                    }));

                await Call(fixture.Window, "RestoreAsync");
                string[] beforeRetry = expired ? ["/token", "/account/status"] : ["/account/status", "/token", "/account/status"];
                Assert(fixture.Http.Seen.Select(sent => sent.Uri.AbsolutePath).SequenceEqual(beforeRetry)
                    && OAuthSession(fixture.Window) is null && PreloadGet<Account?>(fixture.Window, "_account") is null
                    && fixture.Store.Load() is { AccessToken: var persistedAccess, RefreshToken: "fixture-new-refresh",
                        DeviceProfilePending: false, ProfileRefreshValidated: true }
                    && persistedAccess == access,
                    "a resource-only refusal deleted a validated refresh grant, refreshed again, or activated a profile");

                var rotated = File.ReadAllBytes(fixture.SessionPath);
                await fixture.Window.StartLoginAsync();
                Assert(fixture.Http.Seen.Skip(beforeRetry.Length).Select(sent => sent.Uri.AbsolutePath).SequenceEqual(["/account/status"])
                    && fixture.Http.Seen.Last().Authorization == "OAuth " + access
                    && File.ReadAllBytes(fixture.SessionPath).SequenceEqual(rotated) && OAuthSession(fixture.Window) is null,
                    "manual retry exchanged or changed the validated grant after a profile-only refusal");
                fixture.Window.CloseSaveFailurePrompt = failure => throw new InvalidOperationException(failure);
                fixture.Window.Close();
                await Until(() => PreloadGet<bool>(fixture.Window, "_closed"));

                var profiles = 0;
                using var restarted = new OAuthScopesUiFixture("restarted-restored-profile-denied", null, (request, _) =>
                    Task.FromResult(request.RequestUri!.AbsolutePath switch
                    {
                        "/account/status" when request.Headers.Authorization?.Parameter == access
                            => ++profiles == 1 ? Json("{}", HttpStatusCode.Unauthorized) : Json(OAuthConfirmedProfile),
                        "/users/12345/likes/tracks" => Json("""{"result":{"library":{"tracks":[]}}}"""),
                        _ => throw new InvalidOperationException("Restart started another exchange for the validated grant")
                    }), existingSessionDirectory: fixture.DirectoryPath);
                await Call(restarted.Window, "RestoreAsync");
                Assert(restarted.Http.Seen.Select(sent => sent.Uri.AbsolutePath).SequenceEqual(["/account/status"])
                    && OAuthSession(restarted.Window) is null && File.ReadAllBytes(fixture.SessionPath).SequenceEqual(rotated),
                    "restart did not preserve the rotated grant across another profile refusal");
                await restarted.Window.StartLoginAsync();
                Assert(PreloadGet<Account?>(restarted.Window, "_account")?.Uid == "12345"
                    && restarted.Http.Seen.Select(sent => sent.Uri.AbsolutePath)
                        .SequenceEqual(["/account/status", "/account/status", "/users/12345/likes/tracks"])
                    && restarted.Store.Load() is { ProfileRefreshValidated: false, DeviceProfilePending: false }
                    && OAuthSession(restarted.Window) == restarted.Store.Load(),
                    "restart did not confirm the same access or settle the profile validation marker");
            }
        });

        await Run("OAuthScopes: ambiguous profile identity cannot activate or authorize a playlist write", async () =>
        {
            var replies = new[]
            {
                """{"result":{"account":{"uid":6789}},"result":{"account":{"uid":12345}}}""",
                """{"result":{"account":{"uid":6789},"account":{"uid":12345}}}""",
                """{"result":{"account":{"uid":6789,"uid":12345}}}"""
            };
            foreach (var reply in replies)
            {
                var read = 0;
                using var fixture = new OAuthScopesUiFixture("ambiguous-profile", FakeSession, (request, _) =>
                    Task.FromResult(request.RequestUri!.AbsolutePath switch
                    {
                        "/account/status" => Json(++read == 1 ? reply : OAuthConfirmedProfile),
                        "/users/12345/likes/tracks" => Json("""{"result":{"library":{"tracks":[]}}}"""),
                        _ => throw new InvalidOperationException("Ambiguous profile sent an account-bound request")
                    }));
                var before = File.ReadAllBytes(fixture.SessionPath);
                await Call(fixture.Window, "RestoreAsync");
                Assert(OAuthSession(fixture.Window) is null && fixture.Http.Seen.Count == 1
                    && File.ReadAllBytes(fixture.SessionPath).SequenceEqual(before),
                    "ambiguous identity activated an account or erased the prior grant");
                await fixture.Window.StartLoginAsync();
                Assert(PreloadGet<Account?>(fixture.Window, "_account")?.Uid == "12345"
                    && fixture.Http.Seen.All(sent => sent.Uri.AbsolutePath != "/device/code"),
                    "an unambiguous reread could not restore the original authorization");

                var http = new FakeHandler((request, _) => request.RequestUri!.AbsolutePath == "/account/status"
                    ? Task.FromResult(Json(reply)) : throw new InvalidOperationException("Ambiguous profile reached a playlist write"));
                using var api = new MusicApi(http);
                var error = await Throws<MusicException>(() => api.CreatePlaylistAsync(FakeSession, "Fixture playlist", default));
                Assert(!error.Authentication && http.Seen.Count == 1,
                    "a duplicate owner field allowed a write or became terminal authentication failure");
            }
        });

        await Run("OAuthScopes: optional profile refusal after confirmed account does not block activation", async () =>
        {
            foreach (var optionalStatus in new[] { HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden, HttpStatusCode.OK })
            {
                using var fixture = new OAuthScopesUiFixture("optional-profile-" + (int)optionalStatus, FakeSession, (request, _) =>
                    Task.FromResult(request.RequestUri!.AbsolutePath switch
                    {
                        "/account/status" => Json(Profile),
                        "/account/about" => Json("""{"result":{"uid":67890,"avatarId":"98765/foreign"}}""", optionalStatus),
                        "/users/12345/likes/tracks" => Json("""{"result":{"library":{"tracks":[]}}}"""),
                        _ => throw new InvalidOperationException("Unexpected optional profile fixture route")
                    }));
                await Call(fixture.Window, "RestoreAsync");
                Assert(OAuthSession(fixture.Window) is not null && Control<TextBlock>(fixture.Window, "AccountText").DataContext
                        is Account { Uid: "12345", AvatarUri: null }
                    && fixture.Http.Seen.Select(call => call.Uri.AbsolutePath)
                        .SequenceEqual(["/account/status", "/account/about", "/users/12345/likes/tracks"]),
                    "optional avatar prevented activation or attached a different account portrait");
            }
        });

        await Run("OAuthScopes: library mark rejects and readback failures keep their status after one POST", async () =>
        {
            foreach (var mark in new[] { "track-like", "album-like", "track-dislike", "artist-dislike" })
            foreach (var failure in new[] { "post-auth", "post-forbidden", "post-conflict", "post-error", "post-incomplete", "read-auth", "read-unavailable" })
            {
                var posts = 0; var readbacks = 0;
                var http = new OAuthScopesHandler((request, _) =>
                {
                    if (request.RequestUri!.AbsolutePath == "/account/status") return Task.FromResult(Json(Profile));
                    if (request.Method == HttpMethod.Post)
                    {
                        posts++;
                        return Task.FromResult(failure switch
                        {
                            "post-auth" => Json("{}", HttpStatusCode.Unauthorized),
                            "post-forbidden" => Json("{}", HttpStatusCode.Forbidden),
                            "post-conflict" => Json("{}", HttpStatusCode.Conflict),
                            "post-error" => Json("""{"error":{"message":"fixture-private-rejection"}}"""),
                            "post-incomplete" => Json("{"),
                            _ => Json(mark.StartsWith("track", StringComparison.Ordinal) ? """{"result":{"revision":1}}""" : """{"result":"ok"}""")
                        });
                    }
                    readbacks++;
                    return Task.FromResult(Json("{}", failure == "read-auth" ? HttpStatusCode.Unauthorized : HttpStatusCode.ServiceUnavailable));
                });
                using var api = new MusicApi(http);
                Task mutation = mark switch
                {
                    "track-like" => api.SetTrackLikeAsync(FakeSession, "12345", new("42", "7"), true, default),
                    "album-like" => api.SetEntityLikeAsync(FakeSession, "12345", new("7", "album", "Fixture album", ""), true, default),
                    "track-dislike" => api.SetDislikeAsync(FakeSession, "12345", "track", new("42", "7"), true, default),
                    _ => api.SetDislikeAsync(FakeSession, "12345", "artist", new("17", null), true, default)
                };
                var error = await Throws<MusicException>(() => mutation);
                Assert(posts == 1 && readbacks == (failure.StartsWith("read-", StringComparison.Ordinal) ? 1 : 0)
                    && error.Authentication == (failure is "post-auth" or "read-auth")
                    && error.Message.Contains("итог не подтверждён", StringComparison.Ordinal) == (failure is "post-incomplete" or "read-unavailable")
                    && !error.Message.Contains("fixture-private-rejection", StringComparison.Ordinal),
                    "library mark hid a rejection, retried the write, or disclosed a raw response");
            }
        });

        await Run("OAuthScopes: startup saves rotated credentials before loading the account profile", async () =>
        {
            var expired = FakeSession with { ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(-5), DeviceId = "" };
            OAuthScopesUiFixture? active = null;
            var savedBeforeProfile = false;
            using var fixture = new OAuthScopesUiFixture("restore-rotation", expired, (request, _) =>
            {
                if (request.RequestUri!.AbsolutePath == "/token") return Task.FromResult(Json(OAuthRotatedTokens));
                if (request.RequestUri.AbsolutePath == "/account/status")
                {
                    savedBeforeProfile = active!.Store.Load() is { AccessToken: "fixture-new-access", RefreshToken: "fixture-new-refresh" };
                    return Task.FromResult(Json("{}", HttpStatusCode.ServiceUnavailable));
                }
                throw new InvalidOperationException("Unexpected restore fixture route");
            });
            active = fixture;

            await Call(fixture.Window, "RestoreAsync");

            var saved = fixture.Store.Load();
            Assert(savedBeforeProfile && saved is { Scope: OAuthMusicScopes, RefreshToken: "fixture-new-refresh" }
                && Guid.TryParseExact(saved.DeviceId, "N", out _) && OAuthSession(fixture.Window) is null,
                "failed profile loading lost the rotated grant or activated the account");
            Assert(fixture.Http.Seen.Select(sent => sent.Uri.AbsolutePath).SequenceEqual(["/token", "/account/status"])
                && fixture.Http.Seen[1].Authorization == "OAuth fixture-new-access", "restore did not use the rotated access token");
        });

        await Run("OAuthScopes: startup retries HTTP 408 and ambiguous refresh replies without a device code", async () =>
        {
            (HttpStatusCode Status, string Body)[] failures =
            [
                (HttpStatusCode.RequestTimeout, """{"error":"invalid_grant"}"""),
                (HttpStatusCode.TooManyRequests, """{"error":"invalid_grant"}"""),
                (HttpStatusCode.ServiceUnavailable, """{"error":"invalid_grant"}"""),
                (HttpStatusCode.BadRequest, """{"error":"invalid_client"}"""),
                (HttpStatusCode.BadRequest, """{"error":"invalid_client","error":"invalid_grant"}"""),
                (HttpStatusCode.BadRequest, """{"error":"invalid_grant","access_token":"fixture-contradictory"}"""),
                (HttpStatusCode.Unauthorized, """{"error":"invalid_client"}"""),
                (HttpStatusCode.Unauthorized, """{"error":"fixture-private-response"}"""),
                (HttpStatusCode.Unauthorized, ""),
                (HttpStatusCode.Unauthorized, """{"error":"invalid_client","error":"invalid_grant"}"""),
                (HttpStatusCode.Unauthorized, """{"error":"invalid_grant","refresh_token":"fixture-contradictory"}"""),
                (HttpStatusCode.OK, """{"error":"fixture-private-response"}"""),
                (HttpStatusCode.OK, """{"error":"invalid_grant"}"""),
                (HttpStatusCode.OK, """{"error":null}"""),
                (HttpStatusCode.OK, """{"access_token":42}"""),
                (HttpStatusCode.OK, OAuthConflictingScopes(restrictedLast: true)),
                (HttpStatusCode.OK, OAuthConflictingScopes(restrictedLast: false)),
                (HttpStatusCode.OK, "not JSON")
            ];
            var expired = FakeSession with { ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(-5) };
            var attempts = 0;
            using var fixture = new OAuthScopesUiFixture("refresh-startup-recovery", expired, (request, _) =>
            {
                if (request.RequestUri!.AbsolutePath == "/token")
                {
                    if (attempts >= failures.Length) return Task.FromResult(Json(OAuthRotatedTokens));
                    var failure = failures[attempts++];
                    return Task.FromResult(Json(failure.Body, failure.Status));
                }
                return Task.FromResult(request.RequestUri.AbsolutePath switch
                {
                    "/account/status" => Json("""{"result":{"account":{"uid":12345,"displayName":"Fixture","avatarId":"123/fixture"}}}"""),
                    "/users/12345/likes/tracks" => Json("""{"result":{"library":{"tracks":[]}}}"""),
                    _ => throw new InvalidOperationException("A nonterminal refresh attempted to start another device flow")
                });
            });
            var before = File.ReadAllBytes(fixture.SessionPath);
            for (var attempt = 0; attempt < failures.Length; attempt++)
            {
                if (attempt == 0) await Call(fixture.Window, "RestoreAsync");
                else await fixture.Window.StartLoginAsync();
                Assert(OAuthSession(fixture.Window) is null && File.ReadAllBytes(fixture.SessionPath).SequenceEqual(before)
                    && fixture.Http.Seen.Count == attempt + 1,
                    "nonterminal startup failure removed or replaced the saved grant");
            }
            await fixture.Window.StartLoginAsync();
            var renewals = fixture.Http.Seen.Where(sent => sent.Uri.AbsolutePath == "/token").ToArray();
            Assert(renewals.Length == failures.Length + 1
                && renewals.All(sent => OAuthForm(sent.Body)["refresh_token"] == expired.RefreshToken)
                && fixture.Http.Seen.All(sent => sent.Uri.AbsolutePath != "/device/code")
                && OAuthSession(fixture.Window) == fixture.Store.Load()
                && fixture.Store.Load() is { RefreshToken: "fixture-new-refresh", Scope: OAuthMusicScopes },
                "explicit retry failed to rotate the original grant and activate the account");
        });

        await Run("OAuthScopes: conflicting device token scopes never activate or save a new grant", async () =>
        {
            foreach (var restrictedLast in new[] { false, true })
            {
                using var fixture = new OAuthScopesUiFixture("device-duplicate-scope", null, (request, _) =>
                    Task.FromResult(request.RequestUri!.AbsolutePath switch
                    {
                        "/device/code" => Json(OAuthDeviceReply),
                        "/token" => Json(OAuthConflictingScopes(restrictedLast)),
                        _ => throw new InvalidOperationException("Ambiguous device token reached a profile request")
                    }));
                await fixture.Window.StartLoginAsync();
                Assert(fixture.Http.Seen.Select(sent => sent.Uri.AbsolutePath).SequenceEqual(["/device/code", "/token"])
                    && fixture.Store.Load() is null && OAuthSession(fixture.Window) is null,
                    "a duplicate scope order activated or saved a new grant");
            }
        });

        await Run("OAuthScopes: confirmed startup refresh rejection clears only the rejected grant", async () =>
        {
            foreach (var status in new[] { HttpStatusCode.BadRequest, HttpStatusCode.Unauthorized })
            {
                var expired = FakeSession with { ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(-5) };
                using var fixture = new OAuthScopesUiFixture("refresh-terminal", expired, (request, _) =>
                    Task.FromResult(request.RequestUri!.AbsolutePath switch
                    {
                        "/token" => Json("""{"error":"invalid_grant"}""", status),
                        "/device/code" => Json("""{"error":"fixture-stop"}""", HttpStatusCode.BadRequest),
                        _ => throw new InvalidOperationException("Rejected grant was used for a Music API request")
                    }));
                await Call(fixture.Window, "RestoreAsync");
                Assert(fixture.Store.Load() is null && OAuthSession(fixture.Window) is null
                    && fixture.Http.Seen.Count == 1
                    && Control<TextBlock>(fixture.Window, "StatusText").Text.Contains("Войдите заново", StringComparison.Ordinal),
                    "terminal rejection did not retire the expired grant and request a new login");
                await fixture.Window.StartLoginAsync();
                Assert(fixture.Http.Seen.Select(sent => sent.Uri.AbsolutePath).SequenceEqual(["/token", "/device/code"]),
                    "a terminally rejected grant was retried instead of requesting a new code");
            }
        });

        await Run("OAuthScopes: terminal refresh retires the exact legacy device record during update preparation", async () =>
        {
            foreach (var replaced in new[] { false, true })
            {
                var old = FakeSession with { DeviceId = "", ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(-5) };
                var latest = FakeSession with { AccessToken = "fixture-replacement-access", RefreshToken = "fixture-replacement-refresh" };
                var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                using var fixture = new OAuthScopesUiFixture("legacy-device-terminal", old, async (request, _) =>
                {
                    switch (request.RequestUri!.AbsolutePath)
                    {
                        case "/token":
                            entered.TrySetResult();
                            await release.Task;
                            return Json("""{"error":"invalid_grant"}""", HttpStatusCode.BadRequest);
                        case "/device/code": return Json("""{"error":"fixture-stop"}""", HttpStatusCode.BadRequest);
                        case "/account/status" when replaced && request.Headers.Authorization?.Parameter == latest.AccessToken:
                            return Json(OAuthConfirmedProfile);
                        case "/users/12345/likes/tracks" when replaced:
                            return Json("""{"result":{"library":{"tracks":[]}}}""");
                        default: throw new InvalidOperationException("Unexpected legacy device rejection route");
                    }
                });

                var restore = Call(fixture.Window, "RestoreAsync");
                try
                {
                    await entered.Task.WaitAsync(TimeSpan.FromSeconds(4));
                    Assert(Guid.TryParse(PreloadGet<Session?>(fixture.Window, "_unactivatedSession")?.DeviceId, out _),
                        "the legacy device ID was not normalized for the active restore");
                    R4Set(fixture.Window, "_appUpdatePreparingRestart", true);
                    if (replaced) fixture.Store.Save(latest);
                    var replacementBytes = replaced ? File.ReadAllBytes(fixture.SessionPath) : null;
                    release.TrySetResult();
                    await restore;

                    Assert(fixture.Http.Seen.Count == 1
                        && PreloadGet<Session?>(fixture.Window, "_unactivatedSession") is null && OAuthSession(fixture.Window) is null,
                        "terminal refresh did not retire the normalized pending session");
                    Assert(replaced ? File.ReadAllBytes(fixture.SessionPath).SequenceEqual(replacementBytes!) : fixture.Store.Load() is null,
                        "terminal refresh removed a replacement or left its exact original grant");
                }
                finally
                {
                    release.TrySetResult();
                    R4Set(fixture.Window, "_appUpdatePreparingRestart", false);
                }

                await fixture.Window.StartLoginAsync();
                string[] expected = replaced ? ["/token", "/account/status", "/users/12345/likes/tracks"] : ["/token", "/device/code"];
                Assert(fixture.Http.Seen.Select(sent => sent.Uri.AbsolutePath).SequenceEqual(expected),
                    "explicit login repeated a rejected refresh, skipped the replacement, or failed to request a new code");
            }
        });

        await Run("OAuthScopes: rotation updates the exact retirement record only after a successful write", async () =>
        {
            foreach (var lockStore in new[] { false, true })
            {
                var original = FakeSession with { DeviceId = "", ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(-5) };
                var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                using var fixture = new OAuthScopesUiFixture("rotation-retirement-record", original, async (request, _) =>
                {
                    switch (request.RequestUri!.AbsolutePath)
                    {
                        case "/token":
                            var refreshToken = OAuthForm(await request.Content!.ReadAsStringAsync())["refresh_token"];
                            if (refreshToken != original.RefreshToken)
                                return Json("""{"error":"invalid_grant"}""", HttpStatusCode.BadRequest);
                            entered.TrySetResult();
                            await release.Task;
                            return Json(OAuthRotatedTokens.Replace("3600", "1", StringComparison.Ordinal));
                        case "/account/status":
                            return Json("{}", HttpStatusCode.ServiceUnavailable);
                        default: throw new InvalidOperationException("Unexpected rotation retirement route");
                    }
                });

                var restore = Call(fixture.Window, "RestoreAsync");
                try
                {
                    await entered.Task.WaitAsync(TimeSpan.FromSeconds(4));
                    using var held = lockStore ? File.Open(fixture.SessionPath, FileMode.Open, FileAccess.Read, FileShare.None) : null;
                    release.TrySetResult();
                    await restore;
                }
                finally { release.TrySetResult(); }

                Assert(lockStore ? fixture.Store.Load() == original : fixture.Store.Load() is { RefreshToken: "fixture-new-refresh" },
                    "the fixture did not exercise the selected persistence result");
                var pending = PreloadGet<Session>(fixture.Window, "_unactivatedSession");
                await Until(() => pending.ExpiresAtUtc <= DateTimeOffset.UtcNow);
                await fixture.Window.StartLoginAsync();
                Assert(fixture.Store.Load() is null && OAuthSession(fixture.Window) is null
                    && fixture.Http.Seen.Select(sent => sent.Uri.AbsolutePath).SequenceEqual(["/token", "/account/status", "/token"])
                    && OAuthForm(fixture.Http.Seen.Last().Body)["refresh_token"] == "fixture-new-refresh",
                    "terminal refresh did not remove the grant actually present on disk");
            }
        });

        await Run("OAuthScopes: malformed account status cannot activate an account or erase a rotated grant", async () =>
        {
            var expired = FakeSession with { ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(-5) };
            foreach (var malformed in new[] { "{\"result\":{}}", "{\"result\":{\"account\":{}}}", "{\"result\":{\"account\":{\"uid\":0}}}" })
            {
                using var fixture = new OAuthScopesUiFixture("rotated-invalid-profile", expired, (request, _) =>
                    Task.FromResult(request.RequestUri!.AbsolutePath switch
                    {
                        "/token" => Json(OAuthRotatedTokens),
                        "/account/status" => Json(malformed),
                        _ => throw new InvalidOperationException("invalid profile reached an authenticated read")
                    }));
                await Call(fixture.Window, "RestoreAsync");
                Assert(fixture.Store.Load() is { AccessToken: "fixture-new-access", RefreshToken: "fixture-new-refresh" }
                    && OAuthSession(fixture.Window) is null
                    && fixture.Http.Seen.Select(call => call.Uri.AbsolutePath).SequenceEqual(["/token", "/account/status"]),
                    "malformed profile activated the account, erased a rotated grant, or continued to its library");
            }
        });

        await Run("OAuthScopes: startup rotation and device grant survive a locked session and unavailable profile", async () =>
        {
            foreach (var startup in new[] { false, true })
            {
                var original = startup ? FakeSession with { ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(-5) } : FakeSession;
                var tokenEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                using var fixture = new OAuthScopesUiFixture("locked-accepted-grant", original, async (request, _) =>
                {
                    if (request.RequestUri!.AbsolutePath == "/device/code") return Json(OAuthDeviceReply);
                    if (request.RequestUri.AbsolutePath == "/token")
                    {
                        tokenEntered.TrySetResult();
                        await release.Task;
                        return Json(OAuthRotatedTokens);
                    }
                    if (request.RequestUri.AbsolutePath == "/account/status")
                        return Json("{}", HttpStatusCode.ServiceUnavailable);
                    throw new InvalidOperationException("Unexpected locked authorization fixture route");
                });
                var pending = startup ? Call(fixture.Window, "RestoreAsync") : fixture.Window.StartLoginAsync();
                try
                {
                    await tokenEntered.Task.WaitAsync(TimeSpan.FromSeconds(4));
                    using (File.Open(fixture.SessionPath, FileMode.Open, FileAccess.Read, FileShare.None))
                    {
                        release.TrySetResult();
                        await pending.WaitAsync(TimeSpan.FromSeconds(3));
                        Assert(!OAuthFlushSessionChange(fixture.Window), "the blocked session write was not retained");
                    }
                }
                finally { release.TrySetResult(); }

                Assert(OAuthSession(fixture.Window) is null && fixture.Store.Load() == original && OAuthFlushSessionChange(fixture.Window)
                    && fixture.Store.Load() is { RefreshToken: "fixture-new-refresh" },
                    "close or update cannot retry the accepted grant while profile is unavailable");
            }
        });

        await Run("OAuthScopes: approved device login retries the profile without requesting another device code", async () =>
        {
            foreach (var denied in new[] { false, true })
            {
                OAuthScopesUiFixture? active = null;
                var confirmedBeforeProfile = false;
                var profiles = 0;
                var posts = 0;
                using var fixture = new OAuthScopesUiFixture("approved-device-profile", null, (request, _) =>
                {
                    return Task.FromResult(request.RequestUri!.AbsolutePath switch
                    {
                        "/device/code" => Json(OAuthDeviceReply),
                        "/token" => ++posts == 1 ? Json(OAuthRotatedTokens) : Json("{}", HttpStatusCode.ServiceUnavailable),
                        "/account/status" => ProfileReply(),
                        "/users/12345/likes/tracks" when !denied => Json("""{"result":{"library":{"tracks":[]}}}"""),
                        _ => throw new InvalidOperationException("Unexpected device login fixture route")
                    });
                });
                active = fixture;

                await fixture.Window.StartLoginAsync();
                Assert(confirmedBeforeProfile && OAuthSession(fixture.Window) is null, "temporary profile error lost or activated the accepted grant");
                await fixture.Window.StartLoginAsync();
                Assert(fixture.Http.Seen.Count(sent => sent.Uri.AbsolutePath == "/device/code") == 1 && posts == 1 && profiles == 2,
                    "repeating login requested a second token POST instead of finishing the current authorization");
                Assert(denied ? OAuthSession(fixture.Window) is null && fixture.Store.Load() is null
                        && PreloadGet<Session?>(fixture.Window, "_unactivatedSession") is null
                    : OAuthSession(fixture.Window) is { AccessToken: "fixture-new-access" },
                    "retrying a device profile did not keep its terminal or successful outcome");

                HttpResponseMessage ProfileReply()
                {
                    confirmedBeforeProfile = active!.Store.Load() is { Scope: OAuthMusicScopes, RefreshToken: "fixture-new-refresh" };
                    return ++profiles == 1 ? Json("{}", HttpStatusCode.ServiceUnavailable)
                        : denied ? Json("{}", HttpStatusCode.Unauthorized) : Json(OAuthConfirmedProfile);
                }
            }
        });

        await Run("OAuthScopes: canceled close drains the sent device exchange and retries only the profile", async () =>
        {
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            OAuthScopesUiFixture? active = null;
            using var fixture = new OAuthScopesUiFixture("close-during-device-token", FakeSession, async (request, _) =>
            {
                switch (request.RequestUri!.AbsolutePath)
                {
                    case "/device/code": return Json(OAuthDeviceReply);
                    case "/token":
                        entered.TrySetResult();
                        await release.Task;
                        return Json(OAuthRotatedTokens);
                    case "/account/status":
                        Assert(!PreloadGet<bool>(active!.Window, "_closeCommitInProgress"), "a profile request overtook close preparation");
                        return Json("""{"result":{"account":{"uid":12345,"displayName":"Fixture","avatarId":"123/fixture"}}}""");
                    case "/users/12345/likes/tracks": return Json("""{"result":{"library":{"tracks":[]}}}""");
                    default: throw new InvalidOperationException("Unexpected close fixture route");
                }
            });
            active = fixture;
            string? closeFailure = null;
            fixture.Window.CloseSaveFailurePrompt = reason =>
            {
                closeFailure = reason;
                return MessageBoxResult.Cancel;
            };
            var login = fixture.Window.StartLoginAsync();
            try
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(4));
                using (File.Open(fixture.SessionPath, FileMode.Open, FileAccess.Read, FileShare.None))
                {
                    fixture.Window.Close();
                    var drain = Call(fixture.Window, "DrainExitWritesAsync");
                    Assert(!drain.IsCompleted && closeFailure is null, "close did not wait for the already-sent device POST");
                    release.TrySetResult();
                    await login.WaitAsync(TimeSpan.FromSeconds(3));
                    await Until(() => !PreloadGet<bool>(fixture.Window, "_closeCommitInProgress"));
                    Assert(closeFailure?.Contains("Не удалось сохранить обновлённый вход", StringComparison.Ordinal) == true,
                        "close omitted the failed write of the accepted device token");
                }
            }
            finally
            {
                release.TrySetResult();
                fixture.Window.CloseSaveFailurePrompt = null;
            }

            if (OAuthSession(fixture.Window) is null) await fixture.Window.StartLoginAsync();
            else Assert(OAuthFlushSessionChange(fixture.Window), "the accepted grant could not be saved after canceling close");
            Assert(OAuthSession(fixture.Window) == fixture.Store.Load() && fixture.Store.Load() is { RefreshToken: "fixture-new-refresh" }
                && fixture.Http.Seen.Count(sent => sent.Uri.AbsolutePath == "/device/code") == 1
                && fixture.Http.Seen.Count(sent => sent.Uri.AbsolutePath == "/token") == 1,
                "canceling close lost the accepted grant or started another device flow");
        });

        await Run("OAuthScopes: a deferred device profile rejection never refreshes or removes a different saved login", async () =>
        {
            foreach (var apply in new[] { false, true })
            {
                var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var finishDownload = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var posts = 0;
                using var fixture = new OAuthScopesUiFixture("device-rejection-after-exit", FakeSession, async (request, _) =>
                {
                    switch (request.RequestUri!.AbsolutePath)
                    {
                        case "/device/code": return Json(OAuthDeviceReply);
                        case "/token":
                            if (++posts != 1) return Json("{}", HttpStatusCode.ServiceUnavailable);
                            entered.TrySetResult();
                            await release.Task;
                            return Json(OAuthRotatedTokens);
                        case "/account/status": return Json("{}", HttpStatusCode.Unauthorized);
                        default: throw new InvalidOperationException("Unexpected deferred device fixture route");
                    }
                });
                var bytes = File.ReadAllBytes(fixture.SessionPath);
                var (updates, backend) = await OAuthPendingUpdate(fixture);
                string? closeFailure = null;
                fixture.Window.CloseSaveFailurePrompt = reason => { closeFailure = reason; return MessageBoxResult.Cancel; };
                var login = fixture.Window.StartLoginAsync();
                try
                {
                    await entered.Task.WaitAsync(TimeSpan.FromSeconds(4));
                    using var held = File.Open(fixture.SessionPath, FileMode.Open, FileAccess.Read, FileShare.None);
                    // Exit can resume before the login continuation after the
                    // token write. Keep a normal exit dependency pending.
                    R4Set(fixture.Window, "_downloadTask", finishDownload.Task);
                    var applying = apply ? R4Task<bool>(fixture.Window, "ApplyAppUpdateAsync", updates) : null;
                    if (!apply) fixture.Window.Close();
                    var exitFlag = apply ? "_appUpdatePreparingRestart" : "_closeCommitInProgress";
                    Assert(PreloadGet<bool>(fixture.Window, exitFlag),
                        "the fixture did not pause the admitted device grant before its first profile");
                    release.TrySetResult();
                    await login.WaitAsync(TimeSpan.FromSeconds(4));
                    Assert(PreloadGet<bool>(fixture.Window, exitFlag)
                        && fixture.Http.Seen.Select(sent => sent.Uri.AbsolutePath).SequenceEqual(["/device/code", "/token"]),
                        $"the first profile was admitted during {(apply ? "apply" : "close")} preparation");
                    finishDownload.TrySetResult();
                    if (applying is not null)
                        Assert(!await applying && backend.Applies == 0, "apply bypassed the pending session save");
                    else
                    {
                        await Until(() => !PreloadGet<bool>(fixture.Window, "_closeCommitInProgress"));
                        Assert(closeFailure?.Contains("Не удалось сохранить обновлённый вход", StringComparison.Ordinal) == true,
                            "close omitted the pending session save");
                    }
                }
                finally
                {
                    release.TrySetResult();
                    finishDownload.TrySetResult();
                    R4Set(fixture.Window, "_downloadTask", null);
                    fixture.Window.CloseSaveFailurePrompt = null;
                }

                Assert(fixture.Http.Seen.Select(sent => sent.Uri.AbsolutePath).SequenceEqual(["/device/code", "/token"]),
                    "exit preparation left an unsolicited profile request");
                await fixture.Window.StartLoginAsync();
                Assert(fixture.Http.Seen.Select(sent => sent.Uri.AbsolutePath).SequenceEqual(["/device/code", "/token", "/account/status"])
                    && OAuthSession(fixture.Window) is null && PreloadGet<Session?>(fixture.Window, "_unactivatedSession") is null
                    && OAuthFlushSessionChange(fixture.Window) && File.ReadAllBytes(fixture.SessionPath).SequenceEqual(bytes),
                    "the first profile rejection started a refresh or mutated a different saved login");
            }
        });

        await Run("OAuthScopes: profile activation keeps the session write result reached during close or apply", async () =>
        {
            foreach (var apply in new[] { false, true })
            foreach (var (removed, restoring) in new[] { (true, false), (false, false), (false, true) })
            {
                var original = FakeSession with { ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(restoring ? -5 : 30) };
                var tokenEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var releaseToken = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var profileEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var releaseProfile = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var tokenPosts = 0;
                using var fixture = new OAuthScopesUiFixture("profile-save-result", original, async (request, _) =>
                {
                    switch (request.RequestUri!.AbsolutePath)
                    {
                        case "/device/code" when !restoring: return Json(OAuthDeviceReply);
                        case "/token":
                            if (++tokenPosts == 2 && removed)
                                return Json(OAuthRotatedTokens.Replace("fixture-new-", "fixture-later-", StringComparison.Ordinal));
                            if (tokenPosts == 3 && removed)
                                return Json(OAuthRotatedTokens.Replace("fixture-new-", "fixture-new-login-", StringComparison.Ordinal));
                            Assert(tokenPosts == 1, "activation admitted an unexpected refresh");
                            tokenEntered.TrySetResult();
                            await releaseToken.Task;
                            return Json(removed ? OAuthRotatedTokens.Replace("3600", "30", StringComparison.Ordinal) : OAuthRotatedTokens);
                        case "/account/status":
                            profileEntered.TrySetResult();
                            await releaseProfile.Task;
                            return Json(OAuthConfirmedProfile);
                        case "/users/12345/likes/tracks": return Json("""{"result":{"library":{"tracks":[]}}}""");
                        default: throw new InvalidOperationException("Unexpected profile activation route");
                    }
                });
                var preferences = new PreferencesStore(Path.Combine(fixture.DirectoryPath, "close-preferences"));
                preferences.Save(new PlayerPreferences());
                R4Set(fixture.Window, "_preferenceStore", preferences);
                var (updates, backend) = await OAuthPendingUpdate(fixture);
                R4Set(fixture.Window, "_appUpdatesOverride", updates);
                var login = restoring ? Call(fixture.Window, "RestoreAsync") : fixture.Window.StartLoginAsync();
                try
                {
                    await tokenEntered.Task.WaitAsync(TimeSpan.FromSeconds(4));
                    using (File.Open(fixture.SessionPath, FileMode.Open, FileAccess.Read, FileShare.None))
                    {
                        releaseToken.TrySetResult();
                        await profileEntered.Task.WaitAsync(TimeSpan.FromSeconds(4));
                        Assert(!OAuthFlushSessionChange(fixture.Window), "the device save was not pending while its profile was held");
                    }
                    Assert(fixture.Store.Load() == original, "the original record changed during the blocked save");
                    if (removed) fixture.Store.Clear();

                    // The browser decision has already been reached with restore:false.
                    R4Set(fixture.Window, "_restore", true);
                    using (File.Open(preferences.FilePath, FileMode.Open, FileAccess.Read, FileShare.None))
                    {
                        if (apply)
                        {
                            Assert(!await R4Task<bool>(fixture.Window, "ApplyAppUpdateAsync", updates) && backend.Applies == 0
                                && PreloadGet<string?>(fixture.Window, "_appUpdateFailure")?.Contains("настройки", StringComparison.Ordinal) == true,
                                "apply did not resolve the session save before deferring on preferences");
                        }
                        else
                        {
                            string? failure = null;
                            fixture.Window.CloseSaveFailurePrompt = reason => { failure = reason; return MessageBoxResult.Cancel; };
                            fixture.Window.Close();
                            await Until(() => !PreloadGet<bool>(fixture.Window, "_closeCommitInProgress"));
                            Assert(failure is not null && failure.Contains("настройки", StringComparison.Ordinal)
                                && !failure.Contains("обновлённый вход", StringComparison.Ordinal)
                                && !PreloadGet<bool>(fixture.Window, "_closed"),
                                "close did not resolve the session save before canceling on preferences");
                        }
                    }
                    R4Set(fixture.Window, "_restore", false);
                    var written = fixture.Store.Load();
                    Assert(!login.IsCompleted && (removed ? written is null
                        : written is { RefreshToken: "fixture-new-refresh" } && written.DeviceProfilePending != restoring
                            && written.ProfileRefreshValidated == restoring),
                        "exit preparation did not settle the original save while the profile was held");

                    releaseProfile.TrySetResult();
                    await login.WaitAsync(TimeSpan.FromSeconds(4));
                    var active = OAuthSession(fixture.Window);
                    string[] routes = restoring ? ["/token", "/account/status", "/users/12345/likes/tracks"]
                        : removed ? ["/device/code", "/token", "/account/status", "/token", "/users/12345/likes/tracks"]
                        : ["/device/code", "/token", "/account/status", "/users/12345/likes/tracks"];
                    Assert(active is { DeviceProfilePending: false }
                        && active.RefreshToken == (removed ? "fixture-later-refresh" : "fixture-new-refresh")
                        && PreloadGet<Account?>(fixture.Window, "_account")?.Uid == "12345"
                        && fixture.Http.Seen.Select(sent => sent.Uri.AbsolutePath).SequenceEqual(routes)
                        && OAuthFlushSessionChange(fixture.Window),
                        "the confirmed profile did not activate once after the window remained open");
                    Assert(removed ? !File.Exists(fixture.SessionPath)
                        : fixture.Store.Load() == (written! with { DeviceProfilePending = false, ProfileRefreshValidated = false }),
                        "profile activation or refresh recreated a removed login or did not settle its pending profile");
                    if (removed)
                    {
                        fixture.Window.SignOut();
                        await fixture.Window.StartLoginAsync();
                        Assert(fixture.Store.Load() is { RefreshToken: "fixture-new-login-refresh", DeviceProfilePending: false }
                            && OAuthSession(fixture.Window) == fixture.Store.Load() && tokenPosts == 3,
                            "new device authorization could not publish after independently observing the empty path");
                    }
                }
                finally
                {
                    releaseToken.TrySetResult();
                    releaseProfile.TrySetResult();
                    fixture.Window.CloseSaveFailurePrompt = null;
                    R4Set(fixture.Window, "_restore", false);
                }
            }
        });

        await Run("OAuthScopes: a restarted pending device profile is verified or retired without another token POST", async () =>
        {
            foreach (var apply in new[] { false, true })
            foreach (var denied in new[] { false, true })
            {
                var first = new OAuthScopesUiFixture("pending-device-restart", null, (request, _) =>
                    Task.FromResult(request.RequestUri!.AbsolutePath switch
                    {
                        "/device/code" => Json(OAuthDeviceReply),
                        "/token" => Json(OAuthRotatedTokens),
                        "/account/status" => Json("{}", HttpStatusCode.ServiceUnavailable),
                        _ => throw new InvalidOperationException("Unexpected first device profile route")
                    }));
                using (first)
                {
                    await first.Window.StartLoginAsync();
                    Assert(first.Store.Load() is { RefreshToken: "fixture-new-refresh", DeviceProfilePending: true }
                        && OAuthSession(first.Window) is null,
                        "the incomplete device grant was not saved with its pending profile provenance");
                    if (apply)
                    {
                        var (updates, backend) = await OAuthPendingUpdate(first);
                        Assert(await R4Task<bool>(first.Window, "ApplyAppUpdateAsync", updates) && backend.Applies == 1,
                            "fixture apply did not finish with the pending device grant");
                    }
                    else
                    {
                        first.Window.CloseSaveFailurePrompt = failure => throw new InvalidOperationException(failure);
                        first.Window.Close();
                        await Until(() => PreloadGet<bool>(first.Window, "_closed"));
                    }
                }

                using var reopened = new OAuthScopesUiFixture("reopened-pending-device", null, (request, _) =>
                    Task.FromResult(request.RequestUri!.AbsolutePath switch
                    {
                        "/account/status" => denied ? Json("{}", HttpStatusCode.Unauthorized) : Json(OAuthConfirmedProfile),
                        "/users/12345/likes/tracks" when !denied => Json("""{"result":{"library":{"tracks":[]}}}"""),
                        "/token" => Json("{}", HttpStatusCode.ServiceUnavailable),
                        _ => throw new InvalidOperationException("Restarted device profile started unexpected authorization")
                    }), existingSessionDirectory: first.DirectoryPath);
                await Call(reopened.Window, "RestoreAsync");
                string[] routes = denied ? ["/account/status"] : ["/account/status", "/users/12345/likes/tracks"];
                Assert(reopened.Http.Seen.Select(sent => sent.Uri.AbsolutePath).SequenceEqual(routes)
                    && (denied ? reopened.Store.Load() is null && OAuthSession(reopened.Window) is null
                        : reopened.Store.Load() is { RefreshToken: "fixture-new-refresh", DeviceProfilePending: false }
                            && OAuthSession(reopened.Window) == reopened.Store.Load()),
                    "restart refreshed before the first confirmed profile or failed to settle its exact stored grant");
            }
        });

        await Run("OAuthScopes: an expired pending device grant refreshes before its first profile after restart", async () =>
        {
            foreach (var (apply, locked, replaced) in new[] { (false, false, false), (true, true, false), (true, true, true) })
            {
                using var first = new OAuthScopesUiFixture("expired-pending-device", null, (request, _) =>
                    Task.FromResult(request.RequestUri!.AbsolutePath switch
                    {
                        "/device/code" => Json(OAuthDeviceReply),
                        "/token" => Json(OAuthRotatedTokens),
                        "/account/status" => Json("{}", HttpStatusCode.ServiceUnavailable),
                        _ => throw new InvalidOperationException("Unexpected initial device login route")
                    }));
                await first.Window.StartLoginAsync();
                var pending = first.Store.Load() ?? throw new InvalidOperationException("device grant was not saved");
                Assert(pending.DeviceProfilePending && OAuthSession(first.Window) is null,
                    "the initial temporary profile failure did not retain the pending grant");
                if (apply)
                {
                    var (updates, backend) = await OAuthPendingUpdate(first);
                    Assert(await R4Task<bool>(first.Window, "ApplyAppUpdateAsync", updates) && backend.Applies == 1,
                        "apply did not keep the pending grant for restart");
                }
                else
                {
                    first.Window.CloseSaveFailurePrompt = failure => throw new InvalidOperationException(failure);
                    first.Window.Close();
                    await Until(() => PreloadGet<bool>(first.Window, "_closed"));
                }
                pending = pending with { ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1) };
                first.Store.Save(pending);
                var original = File.ReadAllBytes(first.SessionPath);

                var refreshes = 0;
                var pendingAtProfile = false;
                using var reopened = new OAuthScopesUiFixture("reopened-expired-device", null, (request, _) =>
                {
                    switch (request.RequestUri!.AbsolutePath)
                    {
                        case "/token":
                            return Task.FromResult(++refreshes == 1 ? Json("{}", HttpStatusCode.ServiceUnavailable)
                                : Json(OAuthRotatedTokens.Replace("fixture-new-", "fixture-recovered-", StringComparison.Ordinal)));
                        case "/account/status":
                            pendingAtProfile = request.Headers.Authorization?.Parameter == "fixture-recovered-access";
                            return Task.FromResult(Json("{}", HttpStatusCode.Unauthorized));
                        default: throw new InvalidOperationException("Expired device grant started an unexpected exchange");
                    }
                }, existingSessionDirectory: first.DirectoryPath);
                await Call(reopened.Window, "RestoreAsync");
                Assert(reopened.Http.Seen.Select(sent => sent.Uri.AbsolutePath).SequenceEqual(["/token"])
                    && OAuthSession(reopened.Window) is null && File.ReadAllBytes(first.SessionPath).SequenceEqual(original),
                    "a transient refresh attempted the stale profile or changed the pending stored grant");

                using (var held = locked ? File.Open(first.SessionPath, FileMode.Open, FileAccess.Read, FileShare.None) : null)
                {
                    await reopened.Window.StartLoginAsync();
                    if (locked) Assert(!OAuthFlushSessionChange(reopened.Window), "the profile 401 dropped the locked rotation");
                }
                Assert(pendingAtProfile && reopened.Http.Seen.Select(sent => sent.Uri.AbsolutePath)
                    .SequenceEqual(["/token", "/token", "/account/status"])
                    && reopened.Http.Seen.Take(2).All(sent => OAuthForm(sent.Body)["refresh_token"] == pending.RefreshToken)
                    && OAuthSession(reopened.Window) is null,
                    "a profile-only 401 refreshed again or activated an unverified account");
                if (replaced)
                {
                    first.Store.Save(FakeSession with { AccessToken = "fixture-independent-login" });
                    var replacement = File.ReadAllBytes(first.SessionPath);
                    Assert(OAuthFlushSessionChange(reopened.Window), "a replacement did not supersede the old pending rotation");
                    await reopened.Window.StartLoginAsync();
                    Assert(reopened.Http.Seen.Select(sent => sent.Uri.AbsolutePath)
                        .SequenceEqual(["/token", "/token", "/account/status", "/account/status"])
                        && File.ReadAllBytes(first.SessionPath).SequenceEqual(replacement),
                        "retrying a rotated profile exchanged twice or deleted the independent login");
                    continue;
                }
                Assert(OAuthFlushSessionChange(reopened.Window)
                    && reopened.Store.Load() is { RefreshToken: "fixture-recovered-refresh", DeviceProfilePending: true, ProfileRefreshValidated: true },
                    "the verified refresh was not retained with its pending profile");
                var rotated = File.ReadAllBytes(first.SessionPath);
                await reopened.Window.StartLoginAsync();
                Assert(reopened.Http.Seen.Select(sent => sent.Uri.AbsolutePath)
                    .SequenceEqual(["/token", "/token", "/account/status", "/account/status"])
                    && File.ReadAllBytes(first.SessionPath).SequenceEqual(rotated),
                    "the in-window retry did not preserve the exact rotated grant");
                reopened.Window.CloseSaveFailurePrompt = failure => throw new InvalidOperationException(failure);
                reopened.Window.Close();
                await Until(() => PreloadGet<bool>(reopened.Window, "_closed"));

                var profiles = 0;
                using var restarted = new OAuthScopesUiFixture("restarted-rotated-device", null, (request, _) =>
                    Task.FromResult(request.RequestUri!.AbsolutePath switch
                    {
                        "/account/status" when request.Headers.Authorization?.Parameter == "fixture-recovered-access"
                            => ++profiles == 1 ? Json("{}", HttpStatusCode.Unauthorized) : Json(OAuthConfirmedProfile),
                        "/users/12345/likes/tracks" => Json("""{"result":{"library":{"tracks":[]}}}"""),
                        _ => throw new InvalidOperationException("Restarting the rotated grant requested another OAuth exchange")
                    }), existingSessionDirectory: first.DirectoryPath);
                await Call(restarted.Window, "RestoreAsync");
                Assert(OAuthSession(restarted.Window) is null && File.ReadAllBytes(first.SessionPath).SequenceEqual(rotated),
                    "the first profile refusal after restart erased or changed the rotated grant");
                await restarted.Window.StartLoginAsync();
                Assert(restarted.Http.Seen.Select(sent => sent.Uri.AbsolutePath)
                    .SequenceEqual(["/account/status", "/account/status", "/users/12345/likes/tracks"])
                    && restarted.Store.Load() is { DeviceProfilePending: false, ProfileRefreshValidated: false }
                    && OAuthSession(restarted.Window) == restarted.Store.Load(),
                    "the restarted profile did not settle its refreshed session without another POST");
            }
        });

        await Run("OAuthScopes: a pending access that expires during its profile request uses one bounded refresh", async () =>
        {
            foreach (var (device, outcome) in new[]
            {
                (true, "temporary"), (true, "deferred"),
                (false, "success"), (false, "deferred"), (false, "profile-refusal")
            })
            {
                var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var refreshes = 0;
                var renewedProfiles = 0;
                var deferred = outcome == "deferred";
                var temporary = outcome == "temporary";
                var profileRefused = outcome == "profile-refusal";
                using var fixture = new OAuthScopesUiFixture("access-expires-in-profile", null, async (request, _) =>
                {
                    switch (request.RequestUri!.AbsolutePath)
                    {
                        case "/account/status" when request.Headers.Authorization?.Parameter == FakeSession.AccessToken:
                            entered.TrySetResult();
                            await release.Task;
                            return Json("{}", HttpStatusCode.Unauthorized);
                        case "/token":
                            return ++refreshes == 1 && temporary ? Json("{}", HttpStatusCode.ServiceUnavailable) : Json(OAuthRotatedTokens);
                        case "/account/status" when request.Headers.Authorization?.Parameter == "fixture-new-access":
                            return ++renewedProfiles == 1 && profileRefused
                                ? Json("{}", HttpStatusCode.Unauthorized) : Json(OAuthConfirmedProfile);
                        case "/users/12345/likes/tracks":
                            return Json("""{"result":{"library":{"tracks":[]}}}""");
                        default: throw new InvalidOperationException("Pending profile deadline reached an unexpected route");
                    }
                });
                var original = FakeSession with
                {
                    DeviceProfilePending = device,
                    ProfileRefreshValidated = !device,
                    ExpiresAtUtc = DateTimeOffset.UtcNow.AddSeconds(2)
                };
                fixture.Store.Save(original);
                var before = File.ReadAllBytes(fixture.SessionPath);
                var restore = Call(fixture.Window, "RestoreAsync");
                try
                {
                    await entered.Task.WaitAsync(TimeSpan.FromSeconds(4));
                    Assert(fixture.Http.Seen.Count == 1 && fixture.Http.Seen[0].Uri.AbsolutePath == "/account/status",
                        "the still-valid pending access was not admitted directly to the profile");
                    await Until(() => original.ExpiresAtUtc <= DateTimeOffset.UtcNow);
                    if (deferred) R4Set(fixture.Window, "_appUpdatePreparingRestart", true);
                    release.TrySetResult();
                    await restore.WaitAsync(TimeSpan.FromSeconds(4));
                    string[] requests = outcome switch
                    {
                        "deferred" => ["/account/status"],
                        "temporary" => ["/account/status", "/token"],
                        "profile-refusal" => ["/account/status", "/token", "/account/status"],
                        _ => ["/account/status", "/token", "/account/status", "/users/12345/likes/tracks"]
                    };
                    Assert(fixture.Http.Seen.Select(sent => sent.Uri.AbsolutePath).SequenceEqual(requests),
                        "profile expiry did not trigger exactly the allowed exchange within the original attempt");
                    if (deferred || temporary)
                        Assert(OAuthSession(fixture.Window) is null && File.ReadAllBytes(fixture.SessionPath).SequenceEqual(before),
                            "a deferred or temporarily unavailable refresh changed the pending grant");
                    if (profileRefused)
                        Assert(OAuthSession(fixture.Window) is null
                            && fixture.Store.Load() is { ProfileRefreshValidated: true, RefreshToken: "fixture-new-refresh" },
                            "another profile 401 discarded the just-renewed grant");
                }
                finally
                {
                    release.TrySetResult();
                    R4Set(fixture.Window, "_appUpdatePreparingRestart", false);
                }

                if (outcome != "success") await fixture.Window.StartLoginAsync();
                string[] expected = outcome switch
                {
                    "temporary" => ["/account/status", "/token", "/token", "/account/status", "/users/12345/likes/tracks"],
                    "profile-refusal" => ["/account/status", "/token", "/account/status", "/account/status", "/users/12345/likes/tracks"],
                    _ => ["/account/status", "/token", "/account/status", "/users/12345/likes/tracks"]
                };
                Assert(fixture.Http.Seen.Select(sent => sent.Uri.AbsolutePath).SequenceEqual(expected)
                    && fixture.Http.Seen.Where(sent => sent.Uri.AbsolutePath == "/token")
                        .All(sent => OAuthForm(sent.Body)["refresh_token"] == original.RefreshToken)
                    && OAuthSession(fixture.Window) == fixture.Store.Load()
                    && fixture.Store.Load() is { RefreshToken: "fixture-new-refresh", DeviceProfilePending: false, ProfileRefreshValidated: false },
                    "retry after the pending access deadline did not recover with the original refresh");
            }
        });

        await Run("OAuthScopes: device polling delay and explicit cancellation do not hold exit", async () =>
        {
            foreach (var inFlight in new[] { false, true })
            {
                var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                using var fixture = new OAuthScopesUiFixture("cancel-device-token", null, async (request, _) =>
                {
                    if (request.RequestUri!.AbsolutePath == "/device/code")
                        return Json(inFlight ? OAuthDeviceReply : OAuthDeviceReply.Replace("\"interval\":1", "\"interval\":60", StringComparison.Ordinal));
                    Assert(request.RequestUri.AbsolutePath == "/token", "canceled device flow reached an unexpected route");
                    entered.TrySetResult();
                    await release.Task;
                    return Json(OAuthRotatedTokens);
                });
                var login = fixture.Window.StartLoginAsync();
                try
                {
                    if (inFlight) await entered.Task.WaitAsync(TimeSpan.FromSeconds(4));
                    else await Until(() => Control<FrameworkElement>(fixture.Window, "CodePanel").Visibility == Visibility.Visible);
                    var drain = Call(fixture.Window, "DrainExitWritesAsync");
                    Assert(drain.IsCompleted == !inFlight, "exit waited for a polling interval or missed the admitted exchange");
                    PreloadGet<CancellationTokenSource>(fixture.Window, "_authCts").Cancel();
                    await drain.WaitAsync(TimeSpan.FromSeconds(3));
                    Assert(fixture.Store.Load() is null, "canceling the device flow left a stored grant");
                }
                finally { release.TrySetResult(); }
                await login.WaitAsync(TimeSpan.FromSeconds(3));
                Assert(fixture.Store.Load() is null && OAuthSession(fixture.Window) is null && fixture.Http.Seen.Count == (inFlight ? 2 : 1),
                    "late device reply bypassed explicit cancel or issued an extra poll");
            }
        });

        await Run("OAuthScopes: polling resumes the same device code after a canceled exit or deferred update", async () =>
        {
            foreach (var flag in new[] { "_closeCommitInProgress", "_appUpdatePreparingRestart" })
            {
                using var fixture = new OAuthScopesUiFixture("device-poll-resume", null, (request, _) =>
                    Task.FromResult(request.RequestUri!.AbsolutePath switch
                    {
                        "/device/code" => Json(OAuthDeviceReply),
                        "/token" => Json(OAuthRotatedTokens),
                        "/account/status" => Json("""{"result":{"account":{"uid":12345,"displayName":"Fixture","avatarId":"123/fixture"}}}"""),
                        "/users/12345/likes/tracks" => Json("""{"result":{"library":{"tracks":[]}}}"""),
                        _ => throw new InvalidOperationException("Unexpected device poll route")
                    }));
                var login = fixture.Window.StartLoginAsync();
                await Until(() => Control<FrameworkElement>(fixture.Window, "CodePanel").Visibility == Visibility.Visible);
                var auth = PreloadGet<CancellationTokenSource>(fixture.Window, "_authCts");
                R4Set(fixture.Window, flag, true);
                try
                {
                    await Task.Delay(1500);
                    Assert(!login.IsCompleted && fixture.Http.Seen.Count == 1 && fixture.Store.Load() is null
                        && Control<FrameworkElement>(fixture.Window, "CodePanel").Visibility == Visibility.Visible
                        && ReferenceEquals(PreloadGet<CancellationTokenSource>(fixture.Window, "_authCts"), auth)
                        && Call(fixture.Window, "DrainExitWritesAsync").IsCompleted,
                        "a temporarily closed exit gate sent or lost a poll, or waited for the device flow");
                }
                finally { R4Set(fixture.Window, flag, false); }
                await login.WaitAsync(TimeSpan.FromSeconds(4));
                Assert(OAuthSession(fixture.Window) is { RefreshToken: "fixture-new-refresh" }
                    && fixture.Http.Seen.Count(sent => sent.Uri.AbsolutePath == "/device/code") == 1
                    && OAuthForm(fixture.Http.Seen.Single(sent => sent.Uri.AbsolutePath == "/token").Body)["code"] == "scope-fixture-code",
                    "reopening the gate failed to reuse the original device code exactly once");
            }
        });

        await Run("OAuthScopes: device polling backs off after 429, 503, a request timeout or connection setup failure", async () =>
        {
            (HttpStatusCode? Status, HttpRequestError? Transport)[] failures =
            [
                (HttpStatusCode.TooManyRequests, null), (HttpStatusCode.ServiceUnavailable, null), (null, null),
                (null, HttpRequestError.NameResolutionError), (null, HttpRequestError.ConnectionError)
            ];
            foreach (var failure in failures)
            {
                var clock = System.Diagnostics.Stopwatch.StartNew();
                var firstPoll = new TaskCompletionSource<TimeSpan>(TaskCreationOptions.RunContinuationsAsynchronously);
                var resumedPoll = new TaskCompletionSource<TimeSpan>(TaskCreationOptions.RunContinuationsAsynchronously);
                var polls = 0;
                using var fixture = new OAuthScopesUiFixture("device-poll-backoff", null, async (request, token) =>
                {
                    switch (request.RequestUri!.AbsolutePath)
                    {
                        case "/device/code": return Json(OAuthDeviceReply);
                        case "/token":
                            if (++polls > 1)
                            {
                                resumedPoll.TrySetResult(clock.Elapsed);
                                return Json(OAuthRotatedTokens);
                            }
                            firstPoll.TrySetResult(clock.Elapsed);
                            if (failure.Transport is { } transport) throw new HttpRequestException(transport, "fixture connection setup");
                            if (failure.Status is null)
                            {
                                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                                throw new InvalidOperationException("Fake OAuth request timeout did not cancel the transport");
                            }
                            var response = Json("{}", failure.Status.Value);
                            if (failure.Status == HttpStatusCode.TooManyRequests)
                                response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(3));
                            return response;
                        case "/account/status": return Json(OAuthConfirmedProfile);
                        case "/users/12345/likes/tracks": return Json("""{"result":{"library":{"tracks":[]}}}""");
                        default: throw new InvalidOperationException("Unexpected device retry route");
                    }
                }, requestTimeout: failure is (null, null) ? TimeSpan.FromMilliseconds(250) : null);
                var login = fixture.Window.StartLoginAsync();
                var started = await firstPoll.Task.WaitAsync(TimeSpan.FromSeconds(4));
                await Task.Delay(150);
                Assert(!login.IsCompleted && Control<FrameworkElement>(fixture.Window, "CodePanel").Visibility == Visibility.Visible,
                    "a temporary response discarded the code or hid the confirmation panel");
                await login.WaitAsync(TimeSpan.FromSeconds(8));
                var resumed = await resumedPoll.Task.WaitAsync(TimeSpan.FromSeconds(2));
                var minimum = failure.Status == HttpStatusCode.TooManyRequests ? 2.9 : 1.9;
                Assert((resumed - started).TotalSeconds >= minimum && OAuthSession(fixture.Window) is { RefreshToken: "fixture-new-refresh" }
                    && fixture.Http.Seen.Count(sent => sent.Uri.AbsolutePath == "/device/code") == 1
                    && fixture.Http.Seen.Where(sent => sent.Uri.AbsolutePath == "/token")
                        .Select(sent => OAuthForm(sent.Body)["code"]).SequenceEqual(["scope-fixture-code", "scope-fixture-code"]),
                    "device retry did not back off and exchange the same code once");
            }
        });

        await Run("OAuthScopes: device poll backoff respects original expiry and explicit cancellation", async () =>
        {
            var http = new FakeHandler((_, _) =>
            {
                var response = Json("{}", HttpStatusCode.TooManyRequests);
                response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromMinutes(5));
                return Task.FromResult(response);
            });
            using var api = new MusicApi(http);
            var expired = await Throws<MusicException>(() =>
                api.CompleteLoginAsync(TestCode(0, 1), "test-device", CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(4)));
            Assert(expired.Message.Contains("Время действия кода истекло", StringComparison.Ordinal),
                "device retry did not finish with the code expiry message");
            Assert(http.Seen.Count == 1, $"an overflowing Retry-After sent another poll before expiry completed (requests: {http.Seen.Count})");

            using var cancel = new CancellationTokenSource();
            var waiting = api.CompleteLoginAsync(TestCode(0, 30), "test-device", cancel.Token);
            await Until(() => http.Seen.Count == 2);
            cancel.Cancel();
            await Throws<OperationCanceledException>(() => waiting.WaitAsync(TimeSpan.FromSeconds(3)));
            Assert(http.Seen.Count == 2, "canceling a delayed retry sent another poll");
        });

        await Run("OAuthScopes: ambiguous transport or TLS failure does not replay a device exchange", async () =>
        {
            (HttpRequestError Error, bool AfterHeaders)[] failures =
            [
                (HttpRequestError.SecureConnectionError, false), (HttpRequestError.ResponseEnded, false),
                (HttpRequestError.Unknown, false), (HttpRequestError.ConnectionError, true)
            ];
            foreach (var failure in failures)
            {
                var http = new FakeHandler((_, _) =>
                {
                    var error = new HttpRequestException(failure.Error, "fixture exchange failure");
                    return !failure.AfterHeaders ? Task.FromException<HttpResponseMessage>(error)
                        : Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                        {
                            Content = new OAuthBodyContent(OAuthRotatedTokens, _ => Task.FromException(error))
                        });
                });
                using var api = new MusicApi(http);
                await Throws<HttpRequestException>(() =>
                    api.CompleteLoginAsync(TestCode(0), "test-device", default).WaitAsync(TimeSpan.FromSeconds(4)));
                Assert(http.Seen.Count == 1, "an ambiguous transport failure repeated the exchange");
            }
        });

        await Run("OAuthScopes: timeout reading a successful device exchange does not send a second POST", async () =>
        {
            var bodyRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var http = new FakeHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new OAuthBodyContent(OAuthRotatedTokens, async token =>
                {
                    bodyRead.TrySetResult();
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                })
            }));
            using var api = new MusicApi(http, TimeSpan.FromMilliseconds(250));
            var failure = await Throws<MusicException>(() =>
                api.CompleteLoginAsync(TestCode(0, 15), "test-device", default).WaitAsync(TimeSpan.FromSeconds(4)));
            Assert(bodyRead.Task.IsCompleted && http.Seen.Count == 1
                && failure.Message.Contains("Не удалось прочитать результат обмена", StringComparison.Ordinal),
                "a stalled success body lost its specific failure or replayed the exchange");
        });

        await Run("OAuthScopes: an admitted device exchange can finish when its local deadline expires", async () =>
        {
            var expiryReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var http = new FakeHandler(async (_, _) =>
            {
                await expiryReached.Task;
                return Json(OAuthRotatedTokens);
            });
            using var api = new MusicApi(http);
            using var owner = new CancellationTokenSource();
            var grant = await api.CompleteLoginAsync(TestCode(0, 1), "test-device", owner.Token, async (send, pollToken) =>
            {
                using var registration = pollToken.Register(() => expiryReached.TrySetResult());
                return await send(owner.Token);
            }).WaitAsync(TimeSpan.FromSeconds(4));
            Assert(expiryReached.Task.IsCompleted && http.Seen.Count == 1
                && grant is { RefreshToken: "fixture-new-refresh", Scope: OAuthMusicScopes },
                "the local code deadline canceled or repeated an already-sent exchange owned by the current login");
        });

        await Run("OAuthScopes: rejected device profile cannot leave a saved or pending grant", async () =>
        {
            using var fixture = new OAuthScopesUiFixture("rejected-device-profile", null, (request, _) =>
                Task.FromResult(request.RequestUri!.AbsolutePath switch
                {
                    "/device/code" => Json(OAuthDeviceReply),
                    "/token" => Json(OAuthRotatedTokens),
                    "/account/status" => Json("{}", HttpStatusCode.Unauthorized),
                    _ => throw new InvalidOperationException("Unexpected rejected authorization request")
                }));

            await fixture.Window.StartLoginAsync();
            Assert(OAuthSession(fixture.Window) is null && OAuthFlushSessionChange(fixture.Window) && fixture.Store.Load() is null,
                "an account with a rejected grant was activated or remained saved");
        });

        await Run("OAuthScopes: an unpublished device grant cannot retire the previous saved login", async () =>
        {
            foreach (var scenario in new[] { "rejected", "signed-out", "new-record" })
            {
                var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var absent = scenario == "new-record";
                using var fixture = new OAuthScopesUiFixture("unpublished-device-rejection", absent ? null : FakeSession, async (request, _) =>
                {
                    switch (request.RequestUri!.AbsolutePath)
                    {
                        case "/device/code": return Json(OAuthDeviceReply);
                        case "/token":
                            entered.TrySetResult();
                            await release.Task;
                            return Json(OAuthRotatedTokens);
                        case "/account/status": return Json("{}", scenario == "signed-out" ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.Unauthorized);
                        default: throw new InvalidOperationException("Unexpected profile rejection fixture route");
                    }
                });
                var login = fixture.Window.StartLoginAsync();
                byte[] before;
                try
                {
                    await entered.Task.WaitAsync(TimeSpan.FromSeconds(4));
                    if (absent) fixture.Store.Save(FakeSession);
                    before = File.ReadAllBytes(fixture.SessionPath);
                    using var held = absent ? null : File.Open(fixture.SessionPath, FileMode.Open, FileAccess.Read, FileShare.None);
                    release.TrySetResult();
                    await login;
                    if (scenario == "signed-out")
                    {
                        Assert(PreloadGet<Session?>(fixture.Window, "_unactivatedSession") is { RefreshToken: "fixture-new-refresh" },
                            "the fixture did not retain a pending device grant");
                        fixture.Window.SignOut();
                    }
                    Assert(OAuthFlushSessionChange(fixture.Window), "the rejected grant left an unauthorized removal or save pending");
                }
                finally { release.TrySetResult(); }
                Assert(OAuthSession(fixture.Window) is null && PreloadGet<Session?>(fixture.Window, "_unactivatedSession") is null
                    && File.ReadAllBytes(fixture.SessionPath).SequenceEqual(before),
                    "an unpublished device grant replaced or deleted a different saved login");
            }
        });

        await Run("OAuthScopes: canceled close retains a saved grant awaiting refresh and clears a rejected device grant", async () =>
        {
            foreach (var restoring in new[] { false, true })
            {
                var saved = restoring ? FakeSession with { ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(30) } : null;
                var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var profileReads = 0;
                using var fixture = new OAuthScopesUiFixture("close-profile-refusal", saved, async (request, _) =>
                {
                    switch (request.RequestUri!.AbsolutePath)
                    {
                        case "/device/code" when !restoring: return Json(OAuthDeviceReply);
                        case "/token": return Json(OAuthRotatedTokens);
                        case "/account/status":
                            if (++profileReads == 1)
                            {
                                entered.TrySetResult();
                                await release.Task;
                            }
                            return restoring && request.Headers.Authorization?.Parameter == "fixture-new-access"
                                ? Json(OAuthConfirmedProfile) : Json("{}", HttpStatusCode.Unauthorized);
                        case "/users/12345/likes/tracks" when restoring:
                            return Json("""{"result":{"library":{"tracks":[]}}}""");
                        default: throw new InvalidOperationException("Unexpected canceled close fixture route");
                    }
                });
                var preferences = new PreferencesStore(Path.Combine(fixture.DirectoryPath, "close-preferences"));
                preferences.Save(new PlayerPreferences());
                R4Set(fixture.Window, "_preferenceStore", preferences);
                var login = restoring ? Call(fixture.Window, "RestoreAsync") : fixture.Window.StartLoginAsync();
                var promptShown = false;
                Exception? responseFailure = null;
                try
                {
                    await entered.Task.WaitAsync(TimeSpan.FromSeconds(4));
                    Assert(fixture.Store.Load()?.RefreshToken == (restoring ? saved!.RefreshToken : "fixture-new-refresh"),
                        "the admitted profile had no saved grant");
                    var before = File.ReadAllBytes(fixture.SessionPath);
                    R4Set(fixture.Window, "_restore", true);
                    using var held = File.Open(preferences.FilePath, FileMode.Open, FileAccess.Read, FileShare.None);
                    fixture.Window.CloseSaveFailurePrompt = _ =>
                    {
                        promptShown = true;
                        var frame = new System.Windows.Threading.DispatcherFrame();
                        fixture.Window.Dispatcher.BeginInvoke(new Action(async () =>
                        {
                            try { release.TrySetResult(); await login.WaitAsync(TimeSpan.FromSeconds(3)); }
                            catch (Exception error) { responseFailure = error; }
                            finally { frame.Continue = false; }
                        }));
                        System.Windows.Threading.Dispatcher.PushFrame(frame);
                        return MessageBoxResult.Cancel;
                    };
                    fixture.Window.Close();
                    await Until(() => promptShown && !PreloadGet<bool>(fixture.Window, "_closeCommitInProgress"));
                    Assert(responseFailure is null && OAuthSession(fixture.Window) is null,
                        "the canceled close did not finish the profile refusal without activating an account");
                    if (restoring)
                    {
                        Assert(fixture.Http.Seen.Count == 1 && PreloadGet<Session?>(fixture.Window, "_unactivatedSession") == saved
                            && File.ReadAllBytes(fixture.SessionPath).SequenceEqual(before),
                            "the deferred refresh lost the saved login");
                    }
                    else
                    {
                        Assert(PreloadGet<Session?>(fixture.Window, "_unactivatedSession") is null && fixture.Store.Load() is null,
                            "the canceled close left the rejected device grant in the window or on disk");
                    }
                }
                finally
                {
                    release.TrySetResult();
                    fixture.Window.CloseSaveFailurePrompt = null;
                    R4Set(fixture.Window, "_restore", false);
                }
                if (restoring)
                {
                    await fixture.Window.StartLoginAsync();
                    Assert(fixture.Http.Seen.Select(sent => sent.Uri.AbsolutePath)
                        .SequenceEqual(["/account/status", "/account/status", "/token", "/account/status", "/users/12345/likes/tracks"])
                        && OAuthForm(fixture.Http.Seen.Single(sent => sent.Uri.AbsolutePath == "/token").Body)["refresh_token"] == saved!.RefreshToken
                        && OAuthSession(fixture.Window) == fixture.Store.Load() && fixture.Store.Load() is { RefreshToken: "fixture-new-refresh" },
                        "canceling close did not resume the original login with one refresh");
                }
            }
        });

        await Run("OAuthScopes: abandoning an unverified login preserves the previous account's exact checkpoints", async () =>
        {
            const string owner = "12345";
            var expired = FakeSession with { ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(-5) };
            using var fixture = new OAuthScopesUiFixture("abandon-unverified-login", expired, async (request, token) =>
            {
                switch (request.RequestUri!.AbsolutePath)
                {
                    case "/token":
                        var body = OAuthForm(await request.Content!.ReadAsStringAsync(token));
                        return body["grant_type"] == "refresh_token"
                            ? Json("""{"error":"invalid_grant"}""", HttpStatusCode.BadRequest) : Json(OAuthRotatedTokens);
                    case "/device/code": return Json(OAuthDeviceReply);
                    case "/account/status" when request.Headers.Authorization?.Parameter == "fixture-new-access":
                        return Json("{}", HttpStatusCode.Forbidden);
                    case "/account/status" when request.Headers.Authorization?.Parameter == FakeSession.AccessToken:
                        return Json(OAuthConfirmedProfile);
                    case "/users/12345/likes/tracks": return Json("""{"result":{"library":{"tracks":[]}}}""");
                    case "/tracks": return Json("""{"result":[]}""");
                    default: throw new InvalidOperationException("Unverified authorization reached another account operation");
                }
            });
            R4Set(fixture.Window, "_restore", true);
            R4Call(fixture.Window, "InitializePlaybackPersistence");
            R4Call(fixture.Window, "InitializeDownloadPersistence");
            var playback = new PlaybackQueue();
            var song = new TrackRow { Id = "10", RequestId = "10", Title = "Previous track", DurationMs = 120_000, Available = true };
            playback.SetItems([song], song);
            new PlaybackStateStore(fixture.DirectoryPath).Save(PlaybackStateStore.Capture(owner, playback, 37));
            var downloads = new DownloadQueueState();
            downloads.Replace([new TrackRow { Id = "11", RequestId = "11", Title = "Previous download", Available = true }], "Previous downloads");
            Assert(new DownloadQueueStore(fixture.DirectoryPath).Save(DownloadQueueStore.Capture(owner, downloads, AudioQuality.Optimal, false, null)),
                "the test download checkpoint was not written");
            var playbackPath = Path.Combine(fixture.DirectoryPath, PlaybackStateStore.FileName);
            var downloadsPath = Path.Combine(fixture.DirectoryPath, DownloadQueueStore.FileName);
            var playbackBytes = File.ReadAllBytes(playbackPath);
            var downloadsBytes = File.ReadAllBytes(downloadsPath);

            await Call(fixture.Window, "RestoreAsync");
            Assert(fixture.Store.Load() is null && PreloadGet<Account?>(fixture.Window, "_account") is null,
                "the previous expired grant did not reach the explicit terminal response");

            // The normal persistence stores remain initialized. The browser is suppressed for the fake device flow.
            R4Set(fixture.Window, "_restore", false);
            try { await fixture.Window.StartLoginAsync(); }
            finally { R4Set(fixture.Window, "_restore", true); }
            Assert(fixture.Store.Load() is { AccessToken: "fixture-new-access" } && OAuthSession(fixture.Window) is null
                && PreloadGet<Account?>(fixture.Window, "_account") is null && Control<Button>(fixture.Window, "LogoutButton").Visibility == Visibility.Visible,
                "the fixture did not reach a pending grant with a refused profile");
            fixture.Window.SignOut();
            Assert(fixture.Store.Load() is null && File.ReadAllBytes(playbackPath).SequenceEqual(playbackBytes)
                && File.ReadAllBytes(downloadsPath).SequenceEqual(downloadsBytes),
                "abandoning an unverified grant deleted or rewrote an earlier account's checkpoint");

            fixture.Store.Save(FakeSession);
            await Call(fixture.Window, "RestoreAsync");
            Assert(PreloadGet<Account?>(fixture.Window, "_account")?.Uid == owner
                && PreloadGet<PlaybackQueue>(fixture.Window, "_playbackQueue").Current?.Id == "10"
                && PreloadGet<double?>(fixture.Window, "_restoredPlaybackSeconds") == 37
                && PreloadGet<DownloadQueueState>(fixture.Window, "_downloadQueue").Entries.Single().Track.Id == "11",
                "the original verified account could not recover both preserved checkpoints");
            fixture.Window.SignOut();
            Assert(!File.Exists(playbackPath) && !File.Exists(downloadsPath),
                "the verified owner's explicit logout stopped clearing its own checkpoints");
        });

        await Run("OAuthScopes: logout supersedes a failed save and retries the pending removal", async () =>
        {
            var expired = FakeSession with { ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(-5) };
            using var fixture = new OAuthScopesUiFixture("rotation-then-locked-logout", expired, (request, _) =>
            {
                Assert(request.RequestUri!.AbsolutePath == "/token", "Unexpected logout fixture request");
                return Task.FromResult(Json(OAuthRotatedTokens));
            });
            OAuthUseStoredSession(fixture);
            using (File.Open(fixture.SessionPath, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                await Call(fixture.Window, "EnsureSessionAsync", CancellationToken.None);
                Assert(OAuthSession(fixture.Window) is { RefreshToken: "fixture-new-refresh" } && !OAuthFlushSessionChange(fixture.Window),
                    "the failed rotation write was not retained for the same account");
                fixture.Window.SignOut();
                Assert(!OAuthFlushSessionChange(fixture.Window) && OAuthSession(fixture.Window) is null,
                    "logout failed to retain the blocked session removal");
            }
            Assert(fixture.Store.Load() == expired && OAuthFlushSessionChange(fixture.Window) && fixture.Store.Load() is null,
                "retry restored the previous account or failed to remove it");
        });

        await Run("OAuthScopes: admitted refresh preserves a newer saved login before its first write", async () =>
        {
            foreach (var restoring in new[] { false, true })
            {
                var expired = FakeSession with { DeviceId = "", ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(-5) };
                var replacement = FakeSession with { AccessToken = "fixture-new-owner-access", RefreshToken = "fixture-new-owner-refresh" };
                var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                using var fixture = new OAuthScopesUiFixture("refresh-new-owner", expired, async (request, _) =>
                {
                    switch (request.RequestUri!.AbsolutePath)
                    {
                        case "/token":
                            entered.TrySetResult();
                            await release.Task;
                            return Json(OAuthRotatedTokens);
                        case "/account/status" when restoring: return Json(OAuthConfirmedProfile);
                        case "/users/12345/likes/tracks" when restoring: return Json("""{"result":{"library":{"tracks":[]}}}""");
                        default: throw new InvalidOperationException("Unexpected newer login fixture route");
                    }
                });
                if (!restoring) OAuthUseStoredSession(fixture);
                var refresh = restoring ? Call(fixture.Window, "RestoreAsync") : Call(fixture.Window, "EnsureSessionAsync", CancellationToken.None);
                try
                {
                    await entered.Task.WaitAsync(TimeSpan.FromSeconds(4));
                    fixture.Store.Save(replacement);
                    var bytes = File.ReadAllBytes(fixture.SessionPath);
                    release.TrySetResult();
                    await refresh;
                    Assert(OAuthSession(fixture.Window) is { RefreshToken: "fixture-new-refresh" } && OAuthFlushSessionChange(fixture.Window)
                        && File.ReadAllBytes(fixture.SessionPath).SequenceEqual(bytes),
                        "the admitted refresh replaced another login or did not preserve its own in-memory session");
                    fixture.Window.SignOut();
                    Assert(OAuthSession(fixture.Window) is null && File.ReadAllBytes(fixture.SessionPath).SequenceEqual(bytes),
                        "logout let an in-memory rotation delete another saved login");
                }
                finally { release.TrySetResult(); }
            }
        });

        await Run("OAuthScopes: close and apply retry only the observed saved login", async () =>
        {
            foreach (var apply in new[] { false, true })
            foreach (var logout in new[] { false, true })
            foreach (var replaced in new[] { false, true })
            {
                var expired = FakeSession with { ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(-5) };
                var replacement = FakeSession with { AccessToken = "fixture-new-owner-access", RefreshToken = "fixture-new-owner-refresh" };
                using var fixture = new OAuthScopesUiFixture("exit-session-retry", expired, (request, _) =>
                    Task.FromResult(request.RequestUri!.AbsolutePath == "/token" ? Json(OAuthRotatedTokens)
                        : throw new InvalidOperationException("Unexpected exit session fixture route")));
                OAuthUseStoredSession(fixture);
                var (updates, backend) = await OAuthPendingUpdate(fixture);
                var prompts = 0;
                fixture.Window.CloseSaveFailurePrompt = _ => { prompts++; return MessageBoxResult.Cancel; };
                try
                {
                    using (File.Open(fixture.SessionPath, FileMode.Open, FileAccess.Read, FileShare.None))
                    {
                        await Call(fixture.Window, "EnsureSessionAsync", CancellationToken.None);
                        Assert(OAuthSession(fixture.Window) is { RefreshToken: "fixture-new-refresh" }, "the active grant did not rotate");
                        if (logout) fixture.Window.SignOut();
                        Assert(!await ExitAsync(), "exit bypassed a locked pending session change");
                    }
                    Assert(fixture.Store.Load() == expired, "the locked attempt changed the original grant");
                    if (replaced) fixture.Store.Save(replacement);
                    var before = File.ReadAllBytes(fixture.SessionPath);
                    Assert(await ExitAsync() && (apply ? backend.Applies == 1 : prompts == 1),
                        "the exit coordinator did not finish after the pending write became resolvable");
                    Assert(replaced
                        ? File.ReadAllBytes(fixture.SessionPath).SequenceEqual(before)
                        : logout ? fixture.Store.Load() is null : fixture.Store.Load() is { RefreshToken: "fixture-new-refresh" },
                        "the exit retry did not preserve the replacement or finish its own session change");
                }
                finally { fixture.Window.CloseSaveFailurePrompt = null; }

                async Task<bool> ExitAsync()
                {
                    if (apply) return await R4Task<bool>(fixture.Window, "ApplyAppUpdateAsync", updates);
                    fixture.Window.Close();
                    await Until(() => !PreloadGet<bool>(fixture.Window, "_closeCommitInProgress"));
                    return PreloadGet<bool>(fixture.Window, "_closed");
                }
            }
        });

        await Run("OAuthScopes: terminal active refresh cancels its pending write and repeated exchanges", async () =>
        {
            foreach (var apply in new[] { false, true })
            foreach (var replaced in new[] { false, true })
            foreach (var retainsRefresh in new[] { false, true })
            {
                var original = FakeSession with { ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(-5) };
                var replacement = FakeSession with { AccessToken = "fixture-new-owner-access", RefreshToken = "fixture-new-owner-refresh" };
                var activeRefresh = retainsRefresh ? original.RefreshToken : "fixture-new-refresh";
                var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var posts = 0;
                using var fixture = new OAuthScopesUiFixture("terminal-active-refresh", original, async (request, _) =>
                {
                    switch (request.RequestUri!.AbsolutePath)
                    {
                        case "/account/status": return Json(OAuthConfirmedProfile);
                        case "/token":
                            switch (++posts)
                            {
                                case 1:
                                    entered.TrySetResult();
                                    await release.Task;
                                    return Json(retainsRefresh ? """{"access_token":"fixture-new-access","expires_in":1}"""
                                        : OAuthRotatedTokens.Replace("3600", "1", StringComparison.Ordinal));
                                case 2: return Json("{}", HttpStatusCode.ServiceUnavailable);
                                default: return Json("""{"error":"invalid_grant"}""", HttpStatusCode.BadRequest);
                            }
                        default: throw new InvalidOperationException("Unexpected active refresh route");
                    }
                });
                var restore = Call(fixture.Window, "RestoreAsync");
                try
                {
                    await entered.Task.WaitAsync(TimeSpan.FromSeconds(4));
                    using (File.Open(fixture.SessionPath, FileMode.Open, FileAccess.Read, FileShare.None))
                    {
                        release.TrySetResult();
                        await restore.WaitAsync(TimeSpan.FromSeconds(4));
                        Assert(OAuthSession(fixture.Window)?.RefreshToken == activeRefresh
                            && posts == 2 && !OAuthFlushSessionChange(fixture.Window),
                            "temporary active refresh removed the pending write or did not follow confirmed activation");

                        await Throws<RefreshGrantRejectedException>(() => Call(fixture.Window, "EnsureSessionAsync", CancellationToken.None));
                        await Throws<RefreshGrantRejectedException>(() => Call(fixture.Window, "EnsureSessionAsync", CancellationToken.None));
                        Assert(posts == 3 && OAuthFlushSessionChange(fixture.Window) != retainsRefresh
                            && fixture.Http.Seen.Where(sent => sent.Uri.AbsolutePath == "/token").Skip(1)
                                .All(sent => OAuthForm(sent.Body)["refresh_token"] == activeRefresh),
                            "the rejected active grant was retried, or retained a pending save");
                    }
                    if (replaced) fixture.Store.Save(replacement);
                    var before = File.ReadAllBytes(fixture.SessionPath);
                    if (apply)
                    {
                        var (updates, backend) = await OAuthPendingUpdate(fixture);
                        Assert(await R4Task<bool>(fixture.Window, "ApplyAppUpdateAsync", updates) && backend.Applies == 1,
                            "the rejected write remained an apply dependency");
                    }
                    else
                    {
                        fixture.Window.CloseSaveFailurePrompt = failure => throw new InvalidOperationException(failure);
                        fixture.Window.Close();
                        await Until(() => PreloadGet<bool>(fixture.Window, "_closed"));
                    }
                    Assert(retainsRefresh && !replaced ? !File.Exists(fixture.SessionPath)
                        : File.ReadAllBytes(fixture.SessionPath).SequenceEqual(before)
                            && fixture.Store.Load() == (replaced ? replacement : original),
                        "exit published the rejected grant or removed another stored login");
                }
                finally
                {
                    release.TrySetResult();
                    fixture.Window.CloseSaveFailurePrompt = null;
                }
            }
        });

        await Run("OAuthScopes: exit retires the exact published active grant after a terminal refresh rejection", async () =>
        {
            foreach (var apply in new[] { false, true })
            foreach (var replaced in new[] { false, true })
            foreach (var markerWriteBlocked in new[] { false, true })
            {
                var posts = 0;
                var profileEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var releaseProfile = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                using var active = new OAuthScopesUiFixture("published-active-rejection", null, async (request, _) =>
                {
                    switch (request.RequestUri!.AbsolutePath)
                    {
                        case "/device/code": return Json(OAuthDeviceReply);
                        case "/token":
                            return ++posts switch
                            {
                                1 => Json(OAuthRotatedTokens.Replace("3600", "30", StringComparison.Ordinal)),
                                2 => Json("{}", HttpStatusCode.ServiceUnavailable),
                                _ => Json("""{"error":"invalid_grant"}""", HttpStatusCode.BadRequest)
                            };
                        case "/account/status":
                            if (markerWriteBlocked)
                            {
                                profileEntered.TrySetResult();
                                await releaseProfile.Task;
                            }
                            return Json(OAuthConfirmedProfile);
                        default: throw new InvalidOperationException("Unexpected active grant rejection route");
                    }
                });
                var login = active.Window.StartLoginAsync();
                Session? published;
                byte[] before;
                try
                {
                    if (markerWriteBlocked) await profileEntered.Task.WaitAsync(TimeSpan.FromSeconds(4));
                    else await login;
                    published = active.Store.Load();
                    Assert(published is { RefreshToken: "fixture-new-refresh" } && published.DeviceProfilePending == markerWriteBlocked,
                        "the fixture did not establish the expected published device grant");
                    before = File.ReadAllBytes(active.SessionPath);

                    using (File.Open(active.SessionPath, FileMode.Open, FileAccess.Read, FileShare.None))
                    {
                        releaseProfile.TrySetResult();
                        await login.WaitAsync(TimeSpan.FromSeconds(4));
                        Assert(OAuthSession(active.Window) == (published! with { DeviceProfilePending = false }) && posts == 2
                            && (!markerWriteBlocked || !OAuthFlushSessionChange(active.Window)),
                            "the active profile was not verified or its marker write did not remain blocked");
                        await Throws<RefreshGrantRejectedException>(() => Call(active.Window, "EnsureSessionAsync", CancellationToken.None));
                        await Throws<RefreshGrantRejectedException>(() => Call(active.Window, "EnsureSessionAsync", CancellationToken.None));
                        Assert(posts == 3 && !OAuthFlushSessionChange(active.Window),
                            "the rejected published grant was sent again or its blocked retirement was discarded");
                    }
                }
                finally { releaseProfile.TrySetResult(); }
                Assert(File.ReadAllBytes(active.SessionPath).SequenceEqual(before),
                    "the blocked retirement altered the published record");

                var newer = FakeSession with { AccessToken = "fixture-later-owner-access", ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(30) };
                if (markerWriteBlocked) newer = newer with { RefreshToken = published!.RefreshToken };
                if (replaced) active.Store.Save(newer);
                var replacementBytes = replaced ? File.ReadAllBytes(active.SessionPath) : null;
                if (apply)
                {
                    var (updates, backend) = await OAuthPendingUpdate(active);
                    Assert(await R4Task<bool>(active.Window, "ApplyAppUpdateAsync", updates) && backend.Applies == 1,
                        "apply did not settle the terminal grant's exact retirement");
                }
                else
                {
                    active.Window.CloseSaveFailurePrompt = failure => throw new InvalidOperationException(failure);
                    active.Window.Close();
                    await Until(() => PreloadGet<bool>(active.Window, "_closed"));
                }
                Assert(replaced ? File.ReadAllBytes(active.SessionPath).SequenceEqual(replacementBytes!) : !File.Exists(active.SessionPath),
                    "exit retained the rejected published grant or removed its independent replacement");

                using var reopened = new OAuthScopesUiFixture("reopened-active-rejection", null, (request, _) =>
                    Task.FromResult(request.RequestUri!.AbsolutePath switch
                    {
                        "/account/status" when request.Headers.Authorization?.Parameter == newer.AccessToken => Json(OAuthConfirmedProfile),
                        "/users/12345/likes/tracks" => Json("""{"result":{"library":{"tracks":[]}}}"""),
                        "/token" => Json("{}", HttpStatusCode.ServiceUnavailable),
                        _ => throw new InvalidOperationException("Unexpected restart after rejected grant")
                    }), existingSessionDirectory: active.DirectoryPath);
                await Call(reopened.Window, "RestoreAsync");
                string[] routes = replaced ? ["/account/status", "/users/12345/likes/tracks"] : [];
                Assert(reopened.Http.Seen.Select(sent => sent.Uri.AbsolutePath).SequenceEqual(routes)
                    && OAuthSession(reopened.Window) == (replaced ? newer : null),
                    "restart sent another refresh for the rejected grant or failed to restore the replacement");
            }
        });

        await Run("OAuthScopes: canceling startup restore keeps rotated credentials without activating the account", async () =>
        {
            var expired = FakeSession with { ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(-5) };
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var fixture = new OAuthScopesUiFixture("rotation-restore-cancel", expired, async (request, _) =>
            {
                Assert(request.RequestUri!.AbsolutePath == "/token", "Canceled restore requested the profile");
                entered.TrySetResult();
                await release.Task;
                return Json(OAuthRotatedTokens);
            });
            var restore = Call(fixture.Window, "RestoreAsync");
            try
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
                var auth = (CancellationTokenSource)typeof(MainWindow).GetField("_authCts", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(fixture.Window)!;
                auth.Cancel();
            }
            finally { release.TrySetResult(); }
            await restore.WaitAsync(TimeSpan.FromSeconds(3));

            Assert(fixture.Store.Load() is { AccessToken: "fixture-new-access", RefreshToken: "fixture-new-refresh" }
                && OAuthSession(fixture.Window) is null && fixture.Http.Seen.Count == 1,
                "canceling startup restore lost the rotation or activated the account");
        });

        await Run("OAuthScopes: canceled restore retires a terminal grant inside the admitted refresh", async () =>
        {
            foreach (var (outcome, pendingProfile) in new[]
            {
                ("immediate", false), ("immediate", true), ("close", false), ("apply", true),
                ("replacement", true), ("temporary", false)
            })
            {
                var expired = FakeSession with { ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(-5), DeviceProfilePending = pendingProfile };
                var replacement = expired with { AccessToken = "fixture-replacement-access", ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(30) };
                var locked = outcome is "close" or "apply" or "replacement";
                var temporary = outcome == "temporary";
                var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var posts = 0;
                using var fixture = new OAuthScopesUiFixture("cancel-terminal-restore", expired, async (request, _) =>
                {
                    switch (request.RequestUri!.AbsolutePath)
                    {
                        case "/token":
                            if (++posts > 1) return Json(OAuthRotatedTokens);
                            entered.TrySetResult();
                            await release.Task;
                            return temporary ? Json("{}", HttpStatusCode.ServiceUnavailable)
                                : Json("""{"error":"invalid_grant"}""", HttpStatusCode.BadRequest);
                        case "/device/code": return Json("""{"error":"fixture-stop"}""", HttpStatusCode.BadRequest);
                        case "/account/status" when temporary: return Json(OAuthConfirmedProfile);
                        case "/users/12345/likes/tracks" when temporary: return Json("""{"result":{"library":{"tracks":[]}}}""");
                        default: throw new InvalidOperationException("Canceled restore sent an unexpected request");
                    }
                });
                var initialBytes = File.ReadAllBytes(fixture.SessionPath);
                var restore = Call(fixture.Window, "RestoreAsync");
                try
                {
                    await entered.Task.WaitAsync(TimeSpan.FromSeconds(4));
                    using var held = locked ? File.Open(fixture.SessionPath, FileMode.Open, FileAccess.Read, FileShare.None) : null;
                    PreloadGet<CancellationTokenSource>(fixture.Window, "_authCts").Cancel();
                    var drain = Call(fixture.Window, "DrainSessionRefreshWritesAsync");
                    Assert(!drain.IsCompleted, "canceling the screen abandoned an admitted refresh");
                    release.TrySetResult();
                    await drain.WaitAsync(TimeSpan.FromSeconds(4));
                    Assert(temporary ? File.ReadAllBytes(fixture.SessionPath).SequenceEqual(initialBytes)
                        : locked ? !OAuthFlushSessionChange(fixture.Window) : !File.Exists(fixture.SessionPath),
                        "the admitted refresh did not settle or retain its exact retirement before draining");
                    await restore.WaitAsync(TimeSpan.FromSeconds(4));
                    Assert(posts == 1 && OAuthSession(fixture.Window) is null
                        && (PreloadGet<Session?>(fixture.Window, "_unactivatedSession") is not null) == temporary,
                        "canceled restore activated a profile or retained the rejected grant in memory");
                }
                finally { release.TrySetResult(); }

                if (!locked)
                {
                    await fixture.Window.StartLoginAsync();
                    string[] requests = temporary ? ["/token", "/token", "/account/status", "/users/12345/likes/tracks"]
                        : ["/token", "/device/code"];
                    Assert(fixture.Http.Seen.Select(sent => sent.Uri.AbsolutePath).SequenceEqual(requests)
                        && (temporary ? fixture.Store.Load() is { RefreshToken: "fixture-new-refresh" } : !File.Exists(fixture.SessionPath)),
                        "manual retry did not distinguish a temporary refresh failure from confirmed rejection");
                    continue;
                }

                var replaced = outcome == "replacement";
                if (replaced) fixture.Store.Save(replacement);
                var replacementBytes = replaced ? File.ReadAllBytes(fixture.SessionPath) : null;
                if (outcome == "apply")
                {
                    var (updates, backend) = await OAuthPendingUpdate(fixture);
                    Assert(await R4Task<bool>(fixture.Window, "ApplyAppUpdateAsync", updates) && backend.Applies == 1,
                        "apply did not finish the exact retirement after release");
                }
                else
                {
                    fixture.Window.CloseSaveFailurePrompt = failure => throw new InvalidOperationException(failure);
                    fixture.Window.Close();
                    await Until(() => PreloadGet<bool>(fixture.Window, "_closed"));
                }
                Assert(replaced ? File.ReadAllBytes(fixture.SessionPath).SequenceEqual(replacementBytes!) : !File.Exists(fixture.SessionPath),
                    "exit left a rejected grant or deleted a replacement with the same refresh");

                using var reopened = new OAuthScopesUiFixture("reopened-canceled-terminal-restore", null, (request, _) =>
                    Task.FromResult(request.RequestUri!.AbsolutePath switch
                    {
                        "/account/status" when replaced && request.Headers.Authorization?.Parameter == replacement.AccessToken
                            => Json(OAuthConfirmedProfile),
                        "/users/12345/likes/tracks" when replaced => Json("""{"result":{"library":{"tracks":[]}}}"""),
                        _ => throw new InvalidOperationException("Restart tried the rejected refresh again")
                    }), existingSessionDirectory: fixture.DirectoryPath);
                await Call(reopened.Window, "RestoreAsync");
                string[] routes = replaced ? ["/account/status", "/users/12345/likes/tracks"] : [];
                Assert(reopened.Http.Seen.Select(sent => sent.Uri.AbsolutePath).SequenceEqual(routes)
                    && OAuthSession(reopened.Window) == (replaced ? replacement with { DeviceProfilePending = false } : null),
                    "restart sent the rejected grant again or could not open the preserved replacement");
            }
        });

        await Run("OAuthScopes: terminal manual restore updates the login card and next action", async () =>
        {
            foreach (var canceled in new[] { false, true })
            foreach (var outcome in new[] { "remove", "blocked", "replace" })
            {
                var expired = FakeSession with { ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(-5) };
                var replacement = expired with { AccessToken = "fixture-current-saved-access", ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(30) };
                var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var posts = 0;
                using var fixture = new OAuthScopesUiFixture("terminal-restore-card", expired, async (request, _) =>
                {
                    switch (request.RequestUri!.AbsolutePath)
                    {
                        case "/token":
                            if (++posts == 1) return Json("{}", HttpStatusCode.ServiceUnavailable);
                            entered.TrySetResult();
                            await release.Task;
                            return Json("""{"error":"invalid_grant"}""", HttpStatusCode.BadRequest);
                        case "/device/code": return Json("""{"error":"fixture-stop"}""", HttpStatusCode.BadRequest);
                        case "/account/status" when request.Headers.Authorization?.Parameter == replacement.AccessToken:
                            return Json(OAuthConfirmedProfile);
                        case "/users/12345/likes/tracks": return Json("""{"result":{"library":{"tracks":[]}}}""");
                        default: throw new InvalidOperationException("Terminal card reached an unexpected route");
                    }
                });
                var title = Control<TextBlock>(fixture.Window, "LoginTitle");
                var description = Control<TextBlock>(fixture.Window, "LoginDescription");
                var logout = Control<Button>(fixture.Window, "LogoutButton");
                await Call(fixture.Window, "RestoreAsync");
                Assert(title.Text == "Завершите вход" && logout.Visibility == Visibility.Visible,
                    "the initial temporary refresh did not offer profile recovery");

                var retry = fixture.Window.StartLoginAsync();
                try
                {
                    await entered.Task.WaitAsync(TimeSpan.FromSeconds(4));
                    using var held = outcome == "blocked" ? File.Open(fixture.SessionPath, FileMode.Open, FileAccess.Read, FileShare.None) : null;
                    if (outcome == "replace") fixture.Store.Save(replacement);
                    if (canceled) PreloadGet<CancellationTokenSource>(fixture.Window, "_authCts").Cancel();
                    release.TrySetResult();
                    await retry.WaitAsync(TimeSpan.FromSeconds(4));

                    var expected = outcome switch
                    {
                        "blocked" => "повторить удаление",
                        "replace" => "проверить сохранённый вход",
                        _ => "получить новый код"
                    };
                    Assert(title.Text != "Завершите вход" && description.Text.Contains(expected, StringComparison.Ordinal)
                        && logout.Visibility == Visibility.Collapsed && posts == 2,
                        "the terminal response left a stale profile retry card or offered the wrong next action");
                    if (outcome == "blocked")
                    {
                        await fixture.Window.StartLoginAsync();
                        Assert(fixture.Http.Seen.Count == 2 && description.Text.Contains(expected, StringComparison.Ordinal),
                            "retrying a blocked removal contacted OAuth or forgot the removal instructions");
                    }
                }
                finally { release.TrySetResult(); }

                byte[]? replacementBytes = outcome == "replace" ? File.ReadAllBytes(fixture.SessionPath) : null;
                await fixture.Window.StartLoginAsync();
                string[] routes = outcome == "replace" ? ["/token", "/token", "/account/status", "/users/12345/likes/tracks"]
                    : ["/token", "/token", "/device/code"];
                Assert(fixture.Http.Seen.Select(sent => sent.Uri.AbsolutePath).SequenceEqual(routes)
                    && (outcome == "replace" ? OAuthSession(fixture.Window) == replacement
                        && File.ReadAllBytes(fixture.SessionPath).SequenceEqual(replacementBytes!) : !File.Exists(fixture.SessionPath)),
                    "the action offered after terminal rejection did not match the exact stored state");
            }
        });

        await Run("OAuthScopes: canceling caller work preserves the account token rotation", async () =>
        {
            var expired = FakeSession with { ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(-5) };
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var fixture = new OAuthScopesUiFixture("rotation-caller-cancel", expired, async (request, _) =>
            {
                Assert(request.RequestUri!.AbsolutePath == "/token", "Unexpected renewal fixture route");
                entered.TrySetResult();
                await release.Task;
                return Json(OAuthRotatedTokens);
            });
            OAuthUseStoredSession(fixture);
            using var caller = new CancellationTokenSource();
            var pending = Call(fixture.Window, "EnsureSessionAsync", caller.Token);
            try
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
                caller.Cancel();
            }
            finally { release.TrySetResult(); }
            await Throws<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(3)));

            var next = await ((Task<Session>)Call(fixture.Window, "EnsureSessionAsync", CancellationToken.None)).WaitAsync(TimeSpan.FromSeconds(3));
            Assert(next is { AccessToken: "fixture-new-access", RefreshToken: "fixture-new-refresh" }
                && fixture.Store.Load() == next && fixture.Http.Seen.Count == 1,
                "canceling the caller lost the rotation or forced a second OAuth request");
        });

        await Run("OAuthScopes: shutdown drains an admitted rotation without waiting for account activation", async () =>
        {
            var expired = FakeSession with { ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(-5) };
            var rotationEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseRotation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var profileEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseProfile = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var fixture = new OAuthScopesUiFixture("rotation-drain", expired, async (request, _) =>
            {
                if (request.RequestUri!.AbsolutePath == "/token")
                {
                    rotationEntered.TrySetResult();
                    await releaseRotation.Task;
                    return Json(OAuthRotatedTokens);
                }
                if (request.RequestUri.AbsolutePath == "/account/status")
                {
                    profileEntered.TrySetResult();
                    await releaseProfile.Task;
                    return Json("""{"result":{"account":{"uid":12345,"displayName":"Fixture Account","avatarId":"456/fixture"}}}""");
                }
                throw new InvalidOperationException("Unexpected auth request while draining rotation");
            });
            var restore = Call(fixture.Window, "RestoreAsync");
            try
            {
                await rotationEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
                var drain = Call(fixture.Window, "DrainSessionRefreshWritesAsync");
                Assert(!drain.IsCompleted && fixture.Store.Load() == expired, "shutdown did not observe the pending rotation");
                releaseRotation.TrySetResult();
                await profileEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
                await drain.WaitAsync(TimeSpan.FromSeconds(3));
                Assert(!restore.IsCompleted && fixture.Store.Load() is { RefreshToken: "fixture-new-refresh" },
                    "shutdown waited for profile activation or completed before the rotated session was saved");
                typeof(MainWindow).GetField("_appUpdatePreparingRestart", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(fixture.Window, true);
            }
            finally
            {
                releaseRotation.TrySetResult();
                releaseProfile.TrySetResult();
            }
            await restore.WaitAsync(TimeSpan.FromSeconds(3));
            Assert(OAuthSession(fixture.Window) is null, "profile activated while update preparation was closed to writes");
            typeof(MainWindow).GetField("_session", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(fixture.Window, expired);
            await Throws<OperationCanceledException>(() => Call(fixture.Window, "EnsureSessionAsync", CancellationToken.None));
            Assert(fixture.Http.Seen.Select(sent => sent.Uri.AbsolutePath).SequenceEqual(["/token", "/account/status"]),
                "shutdown admitted another token refresh");
        });

        await Run("OAuthScopes: logout and close prevent pending rotations from replacing saved sessions", async () =>
        {
            foreach (var restore in new[] { false, true })
            foreach (var close in new[] { false, true })
            {
                var expired = FakeSession with { ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(-5) };
                var replacement = FakeSession with { AccessToken = "fixture-succeeding-account-token" };
                var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                using var fixture = new OAuthScopesUiFixture("stale-rotation", expired, async (request, _) =>
                {
                    Assert(request.RequestUri!.AbsolutePath == "/token", "Unexpected stale renewal fixture route");
                    entered.TrySetResult();
                    await release.Task;
                    return Json(OAuthRotatedTokens);
                });
                if (!restore) OAuthUseStoredSession(fixture);
                var pending = restore ? Call(fixture.Window, "RestoreAsync")
                    : Call(fixture.Window, "EnsureSessionAsync", CancellationToken.None);
                try
                {
                    await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
                    if (close) fixture.Window.Close();
                    else
                    {
                        fixture.Window.SignOut();
                        Assert(fixture.Store.Load() is null, "logout retained the active account's stored session");
                        fixture.Store.Save(replacement);
                    }
                    Assert(Call(fixture.Window, "DrainSessionRefreshWritesAsync").IsCompleted,
                        "an invalidated rotation remained a shutdown dependency");
                }
                finally { release.TrySetResult(); }

                if (restore) await pending.WaitAsync(TimeSpan.FromSeconds(3));
                else await Throws<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(3)));
                Assert(fixture.Store.Load() == (close ? expired : replacement) && fixture.Http.Seen.Count == 1,
                    $"late OAuth tokens replaced the saved session after {(close ? "close" : "logout")} (restore: {restore})");
            }
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
    private static string OAuthConflictingScopes(bool restrictedLast)
    {
        var restricted = JsonSerializer.Serialize(OAuthMusicScopes);
        var broad = JsonSerializer.Serialize(OAuthMusicScopes + " login:email");
        return OAuthRotatedTokens[..^1] + ",\"scope\":" + (restrictedLast ? broad : restricted)
            + ",\"scope\":" + (restrictedLast ? restricted : broad) + "}";
    }
    private static Dictionary<string, string> OAuthForm(string body) => body.Split('&').Select(field => field.Split('=', 2))
        .ToDictionary(pair => Uri.UnescapeDataString(pair[0].Replace('+', ' ')), pair => Uri.UnescapeDataString(pair[1].Replace('+', ' ')), StringComparer.Ordinal);
    private static string OAuthScopesDirectory(string name)
    {
        var directory = Path.Combine(_directory, "oauth-scopes", name + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory); return directory;
    }
    private static Session? OAuthSession(MainWindow window) =>
        (Session?)typeof(MainWindow).GetField("_session", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window);
    private static bool OAuthFlushSessionChange(MainWindow window) => (bool)PreloadInvoke(window, "TryFlushPendingSessionChange")!;

    private static void OAuthUseStoredSession(OAuthScopesUiFixture fixture)
    {
        var saved = fixture.Store.Load() ?? throw new InvalidOperationException("The fixture has no stored session");
        R4Set(fixture.Window, "_session", saved);
        R4Set(fixture.Window, "_ownedStoredSession", saved);
    }

    private static async Task<(UpdateSupport Updates, Qf061A10UpdateBackend Backend)> OAuthPendingUpdate(OAuthScopesUiFixture fixture)
    {
        var feed = Path.Combine(fixture.DirectoryPath, "feed.json");
        File.WriteAllText(feed, """{"githubRepository":"https://github.com/senduora/yamuse"}""");
        var backend = new Qf061A10UpdateBackend();
        var updates = new UpdateSupport(feed, true, _ => backend);
        await updates.CheckAsync();
        return (updates, backend);
    }
    private static async Task OAuthAssertUnreadableLoginBlocked(OAuthScopesUiFixture fixture, byte[] before)
    {
        await fixture.Window.StartLoginAsync();
        Assert(fixture.Http.Seen.Count == 0 && File.ReadAllBytes(fixture.SessionPath).SequenceEqual(before)
            && OAuthSession(fixture.Window) is null && Control<TextBlock>(fixture.Window, "AccountText").Text == "Вход не выполнен",
            "unreadable old grant was replaced, removed, activated, or sent to authorization network");
        var message = Control<TextBlock>(fixture.Window, "StatusText").Text;
        Assert(message.Contains("Яндекс ID", StringComparison.Ordinal) && message.Contains("отзов", StringComparison.OrdinalIgnoreCase)
            && message.Contains("удал", StringComparison.OrdinalIgnoreCase), "unreadable login did not explain manual device revoke and local session reset");
        Assert(Control<TextBlock>(fixture.Window, "LoginTitle").Text == "Сохранённый вход повреждён"
            && Control<TextBlock>(fixture.Window, "LoginDescription").Text.Contains("session.dpapi", StringComparison.Ordinal),
            "the first manual attempt did not retain recovery instructions on the login card");
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
    private sealed class OAuthBodyContent(string json, Func<CancellationToken, Task> beforeWrite) : HttpContent
    {
        private readonly byte[] _bytes = Encoding.UTF8.GetBytes(json);

        protected override bool TryComputeLength(out long length)
        {
            length = _bytes.Length;
            return true;
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
            => SerializeToStreamAsync(stream, context, CancellationToken.None);

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
        {
            await beforeWrite(cancellationToken);
            await stream.WriteAsync(_bytes, cancellationToken);
        }
    }

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
        internal string SessionPath { get; }
        internal SessionStore Store { get; }
        internal OAuthScopesHandler Http { get; }
        internal MainWindow Window { get; }
        internal OAuthScopesUiFixture(string name, Session? saved, Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond,
            TimeSpan? requestTimeout = null, string? existingSessionDirectory = null)
        {
            DirectoryPath = OAuthScopesDirectory(name);
            var sessionDirectory = existingSessionDirectory ?? DirectoryPath;
            SessionPath = Path.Combine(sessionDirectory, "session.dpapi");
            Store = new SessionStore(sessionDirectory);
            if (saved is not null) Store.Save(saved);
            Http = new OAuthScopesHandler(respond);
            var noNetwork = new FakeHandler((_, _) => throw new InvalidOperationException("auth fixture attempted media download"));
            var player = new NativePlayer(new AudioBuffer(Path.Combine(DirectoryPath, "player-buffer"), noNetwork));
            player.SetVolume(0);
            Window = new MainWindow(api: new MusicApi(Http, requestTimeout), store: Store, restore: false, diagnosticsDirectory: DirectoryPath,
                offline: new OfflineLibrary(Path.Combine(DirectoryPath, "music"), new AudioBuffer(Path.Combine(DirectoryPath, "download-buffer"),
                    new FakeHandler((_, _) => throw new InvalidOperationException("auth fixture attempted offline download")))),
                player: player, preferences: new PreferencesStore(Path.Combine(DirectoryPath, "preferences")));
            // Never Show: no browser, updater, shell, visible window, or real profile.
        }
        public void Dispose() => Window.Close();
    }
}
