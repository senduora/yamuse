using System.Globalization;
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
    private const int MaximumApiResponseBytes = 16 * 1024 * 1024;
    private const int MaximumStreamInfoBytes = 64 * 1024;
    private readonly HttpClient _http;
    private readonly TimeSpan _requestTimeout;
    internal MusicApi(HttpMessageHandler? handler = null, TimeSpan? requestTimeout = null)
    {
        _requestTimeout = requestTimeout ?? TimeSpan.FromSeconds(25);
        if (_requestTimeout <= TimeSpan.Zero || _requestTimeout.TotalMilliseconds > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(requestTimeout));
        _http = new HttpClient(handler ?? new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            AllowAutoRedirect = false,
            UseCookies = false,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5)
        }) { Timeout = Timeout.InfiniteTimeSpan };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Yamuse/0.3.14");
        _http.DefaultRequestHeaders.AcceptLanguage.ParseAdd("ru");
    }

    public async Task<DeviceCode> BeginLoginAsync(string deviceId, CancellationToken cancellationToken)
    {
        using var timeout = RequestTimeout(cancellationToken);
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://oauth.yandex.ru/device/code")
        {
            Content = Form(("client_id", ClientId), ("device_id", deviceId), ("device_name", "Yamuse"), ("scope", RequiredScopes))
        };
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.Unauthorized) throw ApiAuthenticationFailure();
        if (!response.IsSuccessStatusCode && response.StatusCode != HttpStatusCode.BadRequest)
            throw new MusicException("Яндекс не выдал код входа. Попробуйте позже.");
        using var json = await ReadJsonAsync(response, timeout.Token).ConfigureAwait(false);
        var root = json.RootElement;
        if (!response.IsSuccessStatusCode || !root.TryGetProperty("device_code", out _))
            throw new MusicException("Яндекс не выдал код входа. Попробуйте позже. OAuth: " + SafeError(root));
        if (!Uri.TryCreate(JsonString(root, "verification_url"), UriKind.Absolute, out var verification))
            throw new MusicException("Получен неожиданный адрес страницы входа.");
        var knownVerificationHost = verification.Host is "oauth.yandex.ru" or "oauth.yandex.com"
            || verification.Host == "ya.ru" && verification.AbsolutePath == "/device";
        if (verification.Scheme != "https" || !knownVerificationHost
            || verification.UserInfo.Length != 0 || !verification.IsDefaultPort)
            throw new MusicException("Получен неожиданный адрес страницы входа.");
        var code = JsonString(root, "device_code");
        var userCode = JsonString(root, "user_code");
        if (!IsOAuthValue(code) || !IsOAuthValue(userCode)) throw new MusicException("Яндекс вернул некорректный код входа.");
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
                using var timeout = RequestTimeout(linked.Token);
                using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
                if (response.StatusCode == HttpStatusCode.Unauthorized) throw ApiAuthenticationFailure();
                if (!response.IsSuccessStatusCode && response.StatusCode != HttpStatusCode.BadRequest) throw OAuthFailure("");
                using var json = await ReadJsonAsync(response, timeout.Token).ConfigureAwait(false);
                var error = Text(json.RootElement, "error");
                if (error == "authorization_pending") continue;
                if (error == "slow_down") { interval = Math.Min(interval + 5, 120); continue; }
                if (!response.IsSuccessStatusCode || json.RootElement.TryGetProperty("error", out _)) throw OAuthFailure(error);
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
        RequirePlayerScopes(session);
        if (!IsOAuthValue(session.RefreshToken)) throw new MusicException("Срок входа истёк. Войдите заново.", true);
        using var timeout = RequestTimeout(cancellationToken);
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://oauth.yandex.ru/token")
        {
            Content = Form(("grant_type", "refresh_token"), ("refresh_token", session.RefreshToken), ("client_id", ClientId), ("client_secret", PublicClientSecret))
        };
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.TooManyRequests || (int)response.StatusCode is >= 500 and <= 599)
            throw new MusicException("Сервис входа Яндекса временно недоступен. Повторите попытку позже.");
        if (!response.IsSuccessStatusCode) throw new MusicException("Не удалось продлить вход. Войдите заново.", true);
        using var json = await ReadJsonAsync(response, timeout.Token).ConfigureAwait(false);
        if (json.RootElement.TryGetProperty("error", out _))
            throw new MusicException("Не удалось продлить вход. Войдите заново.", true);
        var renewed = ParseSession(json.RootElement, session.DeviceId);
        renewed = renewed with { RefreshToken = renewed.RefreshToken ?? session.RefreshToken };
        cancellationToken.ThrowIfCancellationRequested();
        _waveOwners.Add(renewed, WaveOwner(session));
        return renewed;
    }

    public async Task<Account> GetAccountAsync(Session session, CancellationToken cancellationToken, bool includeAvatar = true)
    {
        using var json = await ApiAsync(HttpMethod.Get, "/account/status", session, null, cancellationToken).ConfigureAwait(false);
        var result = Result(json);
        if (result.ValueKind != JsonValueKind.Object || !result.TryGetProperty("account", out var account))
            throw new MusicException("В ответе Яндекса нет данных учётной записи.");
        var uid = Text(account, "uid");
        if (!IsLibraryId(uid)) throw new MusicException("Яндекс вернул некорректные данные учётной записи.");
        var name = Text(account, "displayName");
        if (name.Length == 0) name = Text(account, "login");
        var avatar = ProfileAvatarUri(account);
        if (includeAvatar && avatar is null) avatar = await GetOwnAvatarUriAsync(session, uid, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return new(uid, name.Length > 0 ? name : "Аккаунт Яндекса", result.TryGetProperty("plus", out var plus) && Bool(plus, "hasPlus"), avatar);
    }

    public async Task<IReadOnlyList<LikedId>> GetLikedIdsAsync(Session session, string uid, CancellationToken cancellationToken)
    {
        using var json = await ApiAsync(HttpMethod.Get, $"/users/{CatalogId(uid)}/likes/tracks", session, null, cancellationToken).ConfigureAwait(false);
        var result = CatalogResult(json);
        CatalogObject(result, "избранного");
        if (!result.TryGetProperty("library", out var library)) throw new MusicException("В ответе нет списка избранного.");
        var tracks = CatalogArray(library, "tracks", "избранного");
        if (library.TryGetProperty("uid", out var owner) && owner.ValueKind != JsonValueKind.Null && Text(library, "uid") != uid)
            throw new MusicException("Ответ относится к библиотеке другого аккаунта.");
        var ids = new List<LikedId>(tracks.GetArrayLength());
        foreach (var track in tracks.EnumerateArray())
        {
            var id = LibraryTrackId(track, "id");
            var albumId = track.TryGetProperty("albumId", out var album) && album.ValueKind != JsonValueKind.Null
                ? LibraryTrackId(track, "albumId") : null;
            ids.Add(new(id, albumId));
        }
        return ids;
    }

    public async Task<IReadOnlyList<TrackRow>> GetTracksAsync(Session session, IReadOnlyList<LikedId> ids, int offset, CancellationToken cancellationToken)
    {
        if (ids.Count == 0) return [];
        using var json = await ApiAsync(HttpMethod.Post, "/tracks", session,
            Form(("track-ids", string.Join(',', ids.Select(t => t.RequestId))), ("with-positions", "True")), cancellationToken).ConfigureAwait(false);
        var result = Result(json);
        if (result.ValueKind != JsonValueKind.Array) throw new MusicException("Изменился формат списка треков.");
        var requested = ids.Select(reference => reference.Id).ToHashSet(StringComparer.Ordinal);
        var byId = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        var byAlbum = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        var ambiguousIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var track in result.EnumerateArray())
        {
            var id = LibraryTrackId(track, "id");
            var realId = OptionalTrackMetadataId(track, "realId");
            if (!requested.Contains(id) && (realId is null || !requested.Contains(realId))) throw TrackMetadataError();
            var albums = TrackMetadataRows(track, "albums").Select(album => OptionalTrackMetadataId(album, "id")).OfType<string>().ToArray();
            _ = TrackMetadataRows(track, "artists").Count();
            foreach (var identity in new[] { id, realId }.Distinct(StringComparer.Ordinal))
            {
                if (identity is null || !requested.Contains(identity)) continue;
                if (!byId.TryAdd(identity, track) && !JsonElement.DeepEquals(byId[identity], track)) ambiguousIds.Add(identity);
                foreach (var album in albums)
                {
                    var reference = identity + ":" + album;
                    if (!byAlbum.TryAdd(reference, track) && !JsonElement.DeepEquals(byAlbum[reference], track)) throw TrackMetadataError();
                }
            }
        }
        return ids.Select((id, index) =>
        {
            if (!byAlbum.TryGetValue(id.RequestId, out var track))
            {
                if (ambiguousIds.Contains(id.Id)) throw TrackMetadataError();
                if (!byId.TryGetValue(id.Id, out track))
                    return new TrackRow { Number = offset + index + 1, Id = id.Id, RequestId = id.RequestId, HasTrackMetadata = false };
            }
            var artists = track.TryGetProperty("artists", out var a) && a.ValueKind == JsonValueKind.Array
                ? string.Join(", ", a.EnumerateArray().Select(x => Text(x, "name"))) : "";
            var album = TrackAlbum(track, id.AlbumId);
            return new TrackRow
            {
                Number = offset + index + 1, Id = id.Id, RequestId = id.RequestId,
                Title = TrackTitle(track), Artist = TrackText(artists), Artists = ArtistReferences(track),
                PrimaryArtistName = PrimaryArtistName(track),
                Album = TrackText(album.Title), AlbumId = album.Id, CoverUri = TrackArtwork(track, id.AlbumId ?? album.Id),
                DurationMs = TrackDuration(track), Available = Bool(track, "available") || Bool(track, "availableForPremiumUsers") || Bool(track, "availableFullWithoutPermission")
            };
        }).ToArray();
    }

    private static string? OptionalTrackMetadataId(JsonElement value, string property)
        => value.TryGetProperty(property, out var field) && field.ValueKind != JsonValueKind.Null ? LibraryTrackId(value, property) : null;

    private static IEnumerable<JsonElement> TrackMetadataRows(JsonElement track, string property)
    {
        if (!track.TryGetProperty(property, out var values) || values.ValueKind == JsonValueKind.Null) yield break;
        if (values.ValueKind != JsonValueKind.Array) throw TrackMetadataError();
        foreach (var value in values.EnumerateArray())
        {
            if (value.ValueKind != JsonValueKind.Object) throw TrackMetadataError();
            yield return value;
        }
    }

    private static MusicException TrackMetadataError() => new("Сервис вернул некорректные или противоречивые сведения о треках. Список не был загружен.");

    private static long TrackDuration(JsonElement track)
        => long.TryParse(Text(track, "durationMs"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var duration)
            && duration is >= 0 and <= PlaybackStateStore.MaximumDurationMs ? duration : 0;

    private static string TrackTitle(JsonElement track)
    {
        var title = TrackText(JsonString(track, "title"));
        var version = TrackText(JsonString(track, "version"));
        return string.IsNullOrWhiteSpace(title) ? "Недоступный трек" : version.Length == 0 ? title : TrackText(title + " · " + version);
    }

    private static string TrackText(string value)
    {
        const int maximumLength = 4096;
        if (value.Length > maximumLength)
            value = value[..(char.IsHighSurrogate(value[maximumLength - 1]) ? maximumLength - 1 : maximumLength)];
        return value.Any(char.IsControl) ? new string(value.Select(character => char.IsControl(character) ? ' ' : character).ToArray()) : value;
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
            if (!IsTrackReferencePart(candidate)) continue;
            selected ??= album;
            if (candidate == preferredId) { selected = album; break; }
        }
        if (selected is { } found) return (Text(found, "id"), Text(found, "title"));
        return (null, first is { } titleOnly ? Text(titleOnly, "title") : "");
    }

    public async Task<(Uri Uri, int Bitrate)> ResolveStreamAsync(Session session, string trackId, CancellationToken cancellationToken, int maximumBitrate = 320)
    {
        using var json = await ApiAsync(HttpMethod.Get, $"/tracks/{CatalogId(trackId)}/download-info", session, null, cancellationToken,
            new Dictionary<string, string> { ["X-Yandex-Music-Client"] = "YandexMusicAndroid/24023621", ["User-Agent"] = "Yandex-Music-API" }).ConfigureAwait(false);
        var variants = Result(json);
        if (variants.ValueKind != JsonValueKind.Array) throw new MusicException("Яндекс не вернул варианты воспроизведения.");
        var full = variants.EnumerateArray().Where(v => Text(v, "codec") == "mp3"
                && (!v.TryGetProperty("preview", out var preview) || preview.ValueKind == JsonValueKind.False))
            .Select(v => (Address: JsonString(v, "downloadInfoUrl"),
                Bitrate: v.TryGetProperty("bitrateInKbps", out _) ? Int(v, "bitrateInKbps", -1) : 0))
            .Where(v => v.Bitrate >= 0 && v.Bitrate <= maximumBitrate && v.Address.Length > 0)
            .OrderByDescending(v => v.Bitrate).FirstOrDefault();
        if (full.Address is null) throw new MusicException("Полный MP3-поток недоступен. Проверьте подписку и доступность трека; фрагмент вместо трека не воспроизводится.");
        var uri = ValidateMediaUri(full.Address);
        string xml;
        // CDN requests never carry an account token. Validate every redirect, not just the first URL.
        using var timeout = RequestTimeout(cancellationToken);
        for (var redirects = 0; ; redirects++)
        {
            using var response = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if ((int)response.StatusCode is >= 300 and <= 399)
            {
                if (redirects >= 3 || response.Headers.Location is not { } location)
                    throw new MusicException("Не удалось получить адрес аудиопотока.");
                if (!Uri.TryCreate(uri, location, out var target)) throw MediaUriError();
                uri = ValidateMediaUri(target);
                continue;
            }
            if (response.StatusCode == HttpStatusCode.Unauthorized)
                throw new MusicException("Аудиосервер отказал в доступе к потоку.", true);
            if (!response.IsSuccessStatusCode) throw new MusicException($"Аудиосервер вернул HTTP {(int)response.StatusCode}.");
            try { xml = await ReadBoundedTextAsync(response, MaximumStreamInfoBytes, timeout.Token).ConfigureAwait(false); }
            catch (InvalidOperationException) { throw new MusicException("Аудиосервер вернул данные в неподдерживаемой кодировке."); }
            break;
        }
        try { return (BuildStreamUri(xml), full.Bitrate); }
        catch (XmlException) { throw new MusicException("Аудиосервер вернул данные в неподдерживаемом формате."); }
    }

    internal static Uri BuildStreamUri(string xml)
    {
        using var reader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 64 * 1024 });
        var root = XDocument.Load(reader).Root ?? throw new MusicException("Пустой ответ аудиосервера.");
        var host = root.Element("host")?.Value ?? "";
        var path = root.Element("path")?.Value ?? "";
        var timestamp = root.Element("ts")?.Value ?? "";
        var salt = root.Element("s")?.Value ?? "";
        if (host.Length == 0 || path.Length == 0 || timestamp.Length == 0 || timestamp.Any(char.IsControl) || salt.Length == 0)
            throw new MusicException("Аудиосервер не вернул полные сведения о потоке.");
        if (Uri.CheckHostName(host) != UriHostNameType.Dns || !path.StartsWith('/') || path.StartsWith("//") || path.Contains('#')
            || path.Any(char.IsControl)) throw MediaUriError();
        var sign = Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(SignSalt + path[1..] + salt))).ToLowerInvariant();
        return ValidateMediaUri($"https://{host}/get-mp3/{sign}/{Uri.EscapeDataString(timestamp)}{path}");
    }

    internal static Uri ValidateMediaUri(Uri uri) => IsMediaUri(uri) ? uri : throw MediaUriError();

    private static readonly string[] MediaDomains = ["yandex.net", "yandex.ru", "yandex.com", "yandexmusic.net", "yandexmusic.ru", "yandexmusic.com"];
    private static bool IsMediaUri(Uri uri) => uri.IsAbsoluteUri && uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort
        && uri.UserInfo.Length == 0 && uri.Fragment.Length == 0
        && MediaDomains.Any(domain => uri.DnsSafeHost.Equals(domain, StringComparison.OrdinalIgnoreCase)
            || uri.DnsSafeHost.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase));
    private static Uri ValidateMediaUri(string address) => Uri.TryCreate(address, UriKind.Absolute, out var uri)
        ? ValidateMediaUri(uri) : throw MediaUriError();
    private static MusicException MediaUriError() => new("Яндекс вернул неподдерживаемый адрес аудиосервера.") { UntrustedMediaAddress = true };

    private CancellationTokenSource RequestTimeout(CancellationToken cancellationToken)
    {
        var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_requestTimeout);
        return timeout;
    }

    private Task<JsonDocument> ApiAsync(HttpMethod method, string path, Session session, HttpContent? content, CancellationToken cancellationToken, IReadOnlyDictionary<string, string>? headers = null)
        => SendApiRequestAsync(method, path, session, content, cancellationToken, ReadApiAsync, headers);

    private async Task<T> SendApiRequestAsync<T>(HttpMethod method, string path, Session session, HttpContent? content, CancellationToken cancellationToken,
        Func<HttpResponseMessage, CancellationToken, Task<T>> read, IReadOnlyDictionary<string, string>? headers = null)
    {
        RequirePlayerScopes(session);
        using var timeout = RequestTimeout(cancellationToken);
        using var request = new HttpRequestMessage(method, new Uri("https://api.music.yandex.net" + path)) { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("OAuth", session.AccessToken);
        if (headers is not null) foreach (var header in headers) request.Headers.Add(header.Key, header.Value);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        return await read(response, timeout.Token).ConfigureAwait(false);
    }

    private static MusicException ApiAuthenticationFailure() => new("Вход больше не действует. Выйдите из аккаунта и войдите заново.", true);

    private static void RequireApiSuccess(HttpResponseMessage response)
    {
        if (response.StatusCode == HttpStatusCode.Unauthorized) throw ApiAuthenticationFailure();
        if (response.StatusCode == HttpStatusCode.Forbidden) throw new MusicException("Яндекс отказал в доступе. Проверьте аккаунт, подписку и доступность сервиса.");
        if (!response.IsSuccessStatusCode) throw new MusicException($"Сервис временно недоступен (HTTP {(int)response.StatusCode}).");
    }

    private static async Task<JsonDocument> ReadApiAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        RequireApiSuccess(response);
        var json = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
        if (json.RootElement.TryGetProperty("error", out _)) { json.Dispose(); throw new MusicException("Яндекс вернул ошибку API. Попробуйте повторить действие позже."); }
        return json;
    }

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var json = JsonDocument.Parse(await ReadBoundedTextAsync(response, MaximumApiResponseBytes, cancellationToken).ConfigureAwait(false), new JsonDocumentOptions { MaxDepth = 48 });
            if (json.RootElement.ValueKind == JsonValueKind.Object) return json;
            json.Dispose();
            throw UnexpectedJson();
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException) { throw UnexpectedJson(); }
    }

    private static async Task<string> ReadBoundedTextAsync(HttpResponseMessage response, int limit, CancellationToken cancellationToken)
    {
        var content = response.Content;
        if (response.StatusCode == HttpStatusCode.PartialContent || content.Headers.NonValidated.Contains("Content-Range"))
            throw new MusicException("Сервис вернул только часть данных.");
        var length = content.Headers.ContentLength;
        if (length > limit) throw new MusicException("Сервис превысил допустимый размер ответа.");
        await content.LoadIntoBufferAsync(limit, cancellationToken).ConfigureAwait(false);
        if (length is { } expected && (await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false)).Length != expected)
            throw new HttpRequestException("Сервис передал неполные данные.");
        cancellationToken.ThrowIfCancellationRequested();
        return await content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
    }
    private static MusicException UnexpectedJson() => new("Сервис вернул неожиданный ответ. Попробуйте позже.");
    internal static JsonElement Result(JsonDocument json) => json.RootElement.ValueKind == JsonValueKind.Object
        && json.RootElement.TryGetProperty("result", out var result) ? result : throw new MusicException("В ответе Яндекса нет ожидаемых данных.");
    internal static string JsonString(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.String) return "";
        try { return value.GetString() ?? ""; }
        catch (InvalidOperationException) { return ""; }
    }
    private static string JsonString(JsonElement value, string property)
        => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(property, out var field) ? JsonString(field) : "";
    internal static string Text(JsonElement e, string name) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var p)
        ? p.ValueKind == JsonValueKind.Number ? p.GetRawText() : JsonString(p) : "";
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
        var token = JsonString(root, "access_token");
        var refresh = JsonString(root, "refresh_token");
        if (!IsOAuthValue(token) || root.TryGetProperty("refresh_token", out var field) && field.ValueKind != JsonValueKind.Null && !IsOAuthValue(refresh))
            throw new MusicException("Яндекс не вернул действующий токен.", true);
        return new(token, refresh.Length > 0 ? refresh : null, DateTimeOffset.UtcNow.AddSeconds(Math.Max(1, Int(root, "expires_in", 86400))), deviceId, GrantedPlayerScope(root));
    }
    public void Dispose() { ResetWaveState(); _http.Dispose(); }
}
