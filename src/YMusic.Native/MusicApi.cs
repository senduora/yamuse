using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;

namespace YMusic.Native;

// Protocol references and attribution: THIRD-PARTY-NOTICES.txt.
// No shared Authorization header: account tokens only go to api.music.yandex.net.
internal sealed partial class MusicApi : IDisposable
{
    private const string ClientId = "23cabbbdc6cd418abb4b39c32c41195d";
    private const string PublicClientSecret = "53bc75238f0c4d08a118e51fe9203300";
    private const string SignSalt = "XGRlBW9FXlekgbPrRHuSiA";
    private readonly HttpClient _http;
    internal MusicApi(HttpMessageHandler? handler = null)
    {
        _http = new HttpClient(handler ?? new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            AllowAutoRedirect = false,
            UseCookies = false,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5)
        }) { Timeout = TimeSpan.FromSeconds(25), MaxResponseContentBufferSize = 16 * 1024 * 1024 };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Yamuse/0.3.14");
        _http.DefaultRequestHeaders.AcceptLanguage.ParseAdd("ru");
    }

    public async Task<DeviceCode> BeginLoginAsync(string deviceId, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://oauth.yandex.ru/device/code")
        {
            Content = Form(("client_id", ClientId), ("device_id", deviceId), ("device_name", "Yamuse"))
        };
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        using var json = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
        var root = json.RootElement;
        if (!response.IsSuccessStatusCode || !root.TryGetProperty("device_code", out _))
            throw new MusicException("Яндекс не выдал код входа. Попробуйте позже. OAuth: " + SafeError(root));
        var verification = new Uri(Text(root, "verification_url"));
        var knownVerificationHost = verification.Host is "oauth.yandex.ru" or "oauth.yandex.com"
            || verification.Host == "ya.ru" && verification.AbsolutePath == "/device";
        if (verification.Scheme != "https" || !knownVerificationHost
            || verification.UserInfo.Length != 0 || !verification.IsDefaultPort)
            throw new MusicException("Получен неожиданный адрес страницы входа.");
        var code = Text(root, "device_code");
        var userCode = Text(root, "user_code");
        if (code.Length == 0 || userCode.Length == 0) throw new MusicException("Яндекс вернул пустой код входа.");
        return new(code, userCode, verification, Math.Clamp(Int(root, "interval", 5), 1, 60), Math.Clamp(Int(root, "expires_in", 600), 1, 3600));
    }

    public async Task<Session> CompleteLoginAsync(DeviceCode code, string deviceId, CancellationToken cancellationToken)
    {
        var interval = code.IntervalSeconds;
        using var expiry = new CancellationTokenSource(TimeSpan.FromSeconds(code.ExpiresInSeconds));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(expiry.Token, cancellationToken);
        try
        {
            while (true)
            {
                await Task.Delay(TimeSpan.FromSeconds(interval), linked.Token).ConfigureAwait(false);
                using var request = new HttpRequestMessage(HttpMethod.Post, "https://oauth.yandex.ru/token")
                {
                    Content = Form(("grant_type", "device_code"), ("code", code.Code), ("client_id", ClientId), ("client_secret", PublicClientSecret))
                };
                using var response = await _http.SendAsync(request, linked.Token).ConfigureAwait(false);
                using var json = await ReadJsonAsync(response, linked.Token).ConfigureAwait(false);
                var error = Text(json.RootElement, "error");
                if (error == "authorization_pending") continue;
                if (error == "slow_down") { interval = Math.Min(interval + 5, 120); continue; }
                if (!response.IsSuccessStatusCode || error.Length > 0) throw OAuthFailure(error);
                return ParseSession(json.RootElement, deviceId);
            }
        }
        catch (OperationCanceledException) when (expiry.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new MusicException("Время действия кода истекло. Нажмите «Войти» ещё раз.");
        }
    }

    public async Task<Session> RefreshAsync(Session session, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(session.RefreshToken)) throw new MusicException("Срок входа истёк. Войдите заново.", true);
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://oauth.yandex.ru/token")
        {
            Content = Form(("grant_type", "refresh_token"), ("refresh_token", session.RefreshToken), ("client_id", ClientId), ("client_secret", PublicClientSecret))
        };
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        using var json = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode || Text(json.RootElement, "error").Length > 0)
            throw new MusicException("Не удалось продлить вход. Войдите заново.", true);
        var renewed = ParseSession(json.RootElement, session.DeviceId);
        return renewed with { RefreshToken = renewed.RefreshToken ?? session.RefreshToken };
    }

    public async Task<Account> GetAccountAsync(Session session, CancellationToken cancellationToken, bool includeAvatar = true)
    {
        using var json = await ApiAsync(HttpMethod.Get, "/account/status", session, null, cancellationToken).ConfigureAwait(false);
        var result = Result(json);
        if (!result.TryGetProperty("account", out var account)) throw new MusicException("В ответе Яндекса нет учётной записи.", true);
        var uid = Text(account, "uid");
        if (uid.Length == 0 || uid == "0") throw new MusicException("Яндекс не подтвердил вход в аккаунт.", true);
        var name = Text(account, "displayName");
        if (name.Length == 0) name = Text(account, "login");
        var avatar = ProfileAvatarUri(account);
        if (includeAvatar && avatar is null) avatar = await GetOwnAvatarUriAsync(session, uid, cancellationToken).ConfigureAwait(false);
        return new(uid, name.Length > 0 ? name : "Аккаунт Яндекса", result.TryGetProperty("plus", out var plus) && Bool(plus, "hasPlus"), avatar);
    }

    public async Task<IReadOnlyList<LikedId>> GetLikedIdsAsync(Session session, string uid, CancellationToken cancellationToken)
    {
        using var json = await ApiAsync(HttpMethod.Get, $"/users/{Uri.EscapeDataString(uid)}/likes/tracks", session, null, cancellationToken).ConfigureAwait(false);
        var result = Result(json);
        if (!result.TryGetProperty("library", out var library) || !library.TryGetProperty("tracks", out var tracks) || tracks.ValueKind != JsonValueKind.Array)
            throw new MusicException("Изменился формат избранного. Список не был загружен.");
        return tracks.EnumerateArray().Select(t => new LikedId(Text(t, "id"), Optional(t, "albumId"))).Where(t => t.Id.Length > 0).ToArray();
    }

    public async Task<IReadOnlyList<TrackRow>> GetTracksAsync(Session session, IReadOnlyList<LikedId> ids, int offset, CancellationToken cancellationToken)
    {
        if (ids.Count == 0) return [];
        using var json = await ApiAsync(HttpMethod.Post, "/tracks", session,
            Form(("track-ids", string.Join(',', ids.Select(t => t.RequestId))), ("with-positions", "True")), cancellationToken).ConfigureAwait(false);
        var result = Result(json);
        if (result.ValueKind != JsonValueKind.Array) throw new MusicException("Изменился формат списка треков.");
        var byId = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var track in result.EnumerateArray())
        {
            var id = Text(track, "id");
            if (id.Length > 0) byId.TryAdd(id, track);
            var realId = Text(track, "realId");
            if (realId.Length > 0) byId.TryAdd(realId, track);
        }
        return ids.Select((id, index) =>
        {
            if (!byId.TryGetValue(id.Id, out var track)) return new TrackRow { Number = offset + index + 1, Id = id.Id, RequestId = id.RequestId };
            var artists = track.TryGetProperty("artists", out var a) && a.ValueKind == JsonValueKind.Array
                ? string.Join(", ", a.EnumerateArray().Select(x => Text(x, "name"))) : "";
            var album = TrackAlbum(track, id.AlbumId);
            var version = Text(track, "version");
            return new TrackRow
            {
                Number = offset + index + 1, Id = id.Id, RequestId = id.RequestId,
                Title = Text(track, "title") + (version.Length > 0 ? " · " + version : ""),
                Artist = artists, Artists = ArtistReferences(track), Album = album.Title, AlbumId = album.Id, CoverUri = TrackArtwork(track),
                DurationMs = Int(track, "durationMs"), Available = Bool(track, "available") || Bool(track, "availableForPremiumUsers") || Bool(track, "availableFullWithoutPermission")
            };
        }).ToArray();
    }

    // Navigation is based on an album returned for this exact track, never its name
    // or an unconfirmed album suffix from the playback request.
    private static (string? Id, string Title) TrackAlbum(JsonElement track, string? preferredId = null)
    {
        if (!track.TryGetProperty("albums", out var albums) || albums.ValueKind != JsonValueKind.Array) return (null, "");
        JsonElement? first = null, selected = null;
        foreach (var album in albums.EnumerateArray())
        {
            if (album.ValueKind != JsonValueKind.Object) continue;
            first ??= album;
            var candidate = Text(album, "id");
            if (string.IsNullOrWhiteSpace(candidate) || candidate.Length > 512 || candidate.Any(char.IsControl)) continue;
            selected ??= album;
            if (candidate == preferredId) { selected = album; break; }
        }
        if (selected is { } found) return (Text(found, "id"), Text(found, "title"));
        return (null, first is { } titleOnly ? Text(titleOnly, "title") : "");
    }

    public async Task<(Uri Uri, int Bitrate)> ResolveStreamAsync(Session session, string trackId, CancellationToken cancellationToken, int maximumBitrate = 320)
    {
        using var json = await ApiAsync(HttpMethod.Get, $"/tracks/{Uri.EscapeDataString(trackId)}/download-info", session, null, cancellationToken,
            new Dictionary<string, string> { ["X-Yandex-Music-Client"] = "YandexMusicAndroid/24023621", ["User-Agent"] = "Yandex-Music-API" }).ConfigureAwait(false);
        var variants = Result(json);
        if (variants.ValueKind != JsonValueKind.Array) throw new MusicException("Яндекс не вернул варианты воспроизведения.");
        var full = variants.EnumerateArray().Where(v => Text(v, "codec") == "mp3" && !Bool(v, "preview") && Int(v, "bitrateInKbps") <= maximumBitrate && Text(v, "downloadInfoUrl").Length > 0)
            .OrderByDescending(v => Int(v, "bitrateInKbps")).ToArray();
        if (full.Length == 0) throw new MusicException("Полный MP3-поток недоступен. Проверьте подписку и доступность трека; фрагмент вместо трека не воспроизводится.");
        var variant = full[0];
        var uri = ValidateMediaUri(new Uri(Text(variant, "downloadInfoUrl")));
        string xml;
        // CDN requests never carry an account token. Validate every redirect, not just the first URL.
        for (var redirects = 0; ; redirects++)
        {
            using var response = await _http.GetAsync(uri, cancellationToken).ConfigureAwait(false);
            if ((int)response.StatusCode is >= 300 and <= 399)
            {
                if (redirects >= 3 || response.Headers.Location is null) throw new MusicException("Не удалось получить адрес аудиопотока.");
                uri = ValidateMediaUri(new Uri(uri, response.Headers.Location));
                continue;
            }
            if (!response.IsSuccessStatusCode) throw new MusicException($"Аудиосервер вернул HTTP {(int)response.StatusCode}.");
            xml = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            break;
        }
        return (BuildStreamUri(xml), Int(variant, "bitrateInKbps"));
    }

    internal static Uri BuildStreamUri(string xml)
    {
        using var reader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 64 * 1024 });
        var root = XDocument.Load(reader).Root ?? throw new MusicException("Пустой ответ аудиосервера.");
        var host = root.Element("host")?.Value ?? "";
        var path = root.Element("path")?.Value ?? "";
        var timestamp = root.Element("ts")?.Value ?? "";
        var salt = root.Element("s")?.Value ?? "";
        if (Uri.CheckHostName(host) != UriHostNameType.Dns || !path.StartsWith('/') || path.StartsWith("//") || path.Contains('#')
            || path.Any(char.IsControl) || timestamp.Length == 0 || timestamp.Any(char.IsControl) || salt.Length == 0)
            throw new MusicException("Некорректный адрес аудиосервера.");
        var sign = Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(SignSalt + path[1..] + salt))).ToLowerInvariant();
        return ValidateMediaUri(new Uri($"https://{host}/get-mp3/{sign}/{Uri.EscapeDataString(timestamp)}{path}"));
    }

    internal static Uri ValidateMediaUri(Uri uri)
    {
        string[] allowed = ["yandex.net", "yandex.ru", "yandex.com", "yandexmusic.net", "yandexmusic.ru", "yandexmusic.com"];
        if (uri.Scheme != "https" || !uri.IsDefaultPort || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0
            || !allowed.Any(domain => uri.DnsSafeHost.Equals(domain, StringComparison.OrdinalIgnoreCase) || uri.DnsSafeHost.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase)))
            throw new MusicException("Яндекс вернул неподдерживаемый адрес аудиосервера.");
        return uri;
    }

    private async Task<JsonDocument> ApiAsync(HttpMethod method, string path, Session session, HttpContent? content, CancellationToken cancellationToken, IReadOnlyDictionary<string, string>? headers = null)
    {
        using var request = new HttpRequestMessage(method, new Uri("https://api.music.yandex.net" + path)) { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("OAuth", session.AccessToken);
        if (headers is not null) foreach (var header in headers) request.Headers.Add(header.Key, header.Value);
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.Unauthorized) throw new MusicException("Вход больше не действует. Выйдите из аккаунта и войдите заново.", true);
        if (response.StatusCode == HttpStatusCode.Forbidden) throw new MusicException("Яндекс отказал в доступе. Проверьте аккаунт, подписку и доступность сервиса.");
        if (!response.IsSuccessStatusCode) throw new MusicException($"Сервис временно недоступен (HTTP {(int)response.StatusCode}).");
        var json = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
        if (json.RootElement.TryGetProperty("error", out _)) { json.Dispose(); throw new MusicException("Яндекс вернул ошибку API. Попробуйте повторить действие позже."); }
        return json;
    }

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try { return JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false), new JsonDocumentOptions { MaxDepth = 48 }); }
        catch (JsonException) { throw new MusicException("Сервис вернул неожиданный ответ. Попробуйте позже."); }
    }
    internal static JsonElement Result(JsonDocument json) => json.RootElement.TryGetProperty("result", out var result) ? result : throw new MusicException("В ответе Яндекса нет ожидаемых данных.");
    internal static string Text(JsonElement e, string name) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var p) && p.ValueKind is JsonValueKind.String or JsonValueKind.Number ? p.ToString() : "";
    private static string? Optional(JsonElement e, string name) { var value = Text(e, name); return value.Length > 0 ? value : null; }
    internal static int Int(JsonElement e, string name, int fallback = 0) => int.TryParse(Text(e, name), out var value) ? value : fallback;
    internal static bool Bool(JsonElement e, string name) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.True;
    private static FormUrlEncodedContent Form(params (string Key, string Value)[] fields) => new(fields.Select(f => new KeyValuePair<string, string>(f.Key, f.Value)));
    private static string SafeError(JsonElement root) => Text(root, "error") switch { "invalid_client" => "invalid_client", "unauthorized_client" => "unauthorized_client", "invalid_scope" => "invalid_scope", _ => "не удалось начать вход" };
    private static MusicException OAuthFailure(string error) => new(error switch
    {
        "access_denied" => "Вход отклонён на странице Яндекса.",
        "expired_token" or "invalid_grant" => "Код входа истёк. Нажмите «Войти» ещё раз.",
        _ => "Не удалось завершить вход. Попробуйте получить новый код."
    });
    private static Session ParseSession(JsonElement root, string deviceId)
    {
        var token = Text(root, "access_token");
        if (string.IsNullOrWhiteSpace(token) || token.Any(char.IsWhiteSpace)) throw new MusicException("Яндекс не вернул действующий токен.");
        return new(token, Optional(root, "refresh_token"), DateTimeOffset.UtcNow.AddSeconds(Math.Max(1, Int(root, "expires_in", 86400))), deviceId);
    }
    public void Dispose() { ResetWaveState(); _http.Dispose(); }
}
