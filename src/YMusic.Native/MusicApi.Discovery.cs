using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace YMusic.Native;

// Protocol: upstream-dotnet, MIT, Copyright (c) 2026 FrigaT, commit
// 8c929094386137669766632f9cc2c97d3b05f670: LandingClient, TracksClient,
// TrackRequestSigner and landing/lyrics models. Full attribution belongs in notices.
internal sealed partial class MusicApi
{
    private const int DiscoveryJsonLimit = 4 * 1024 * 1024;
    private const int DiscoveryLyricsLimit = 1024 * 1024;
    internal const int MaximumExpandedLyricsCharacters = 128 * 1024;
    private const int DiscoverySectionLimit = 32;
    private const int DiscoveryItemLimit = 200;
    private static readonly Regex DiscoveryTime = new(
        @"^(?:(?<h>[0-9]{1,6}):)?(?<m>[0-9]{1,6}):(?<s>[0-9]{2})(?:[.,](?<f>[0-9]{1,3}))?$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    public async Task<IReadOnlyList<HomeSection>> GetHomeSectionsAsync(Session session, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        DiscoveryUnavailable? landingFailure = null;
        try
        {
            using var json = await DiscoveryJsonAsync(session,
                "/landing3?blocks=personal-playlists%2Cnew-releases%2Cnew-playlists%2Cchart", false, cancellationToken).ConfigureAwait(false);
            var sections = ReadDiscoveryLanding(DiscoveryResult(json));
            if (sections.Any(section => section.Items.Count > 0)) return sections;
            if (sections.Count > 0) landingFailure = new("Объекты главной пока не поддерживаются.", false);
        }
        catch (DiscoveryUnavailable error) { landingFailure = error; }
        catch (HttpRequestException) { landingFailure = new("Главная не ответила по сети.", true); }

        try
        {
            using var json = await DiscoveryJsonAsync(session, "/feed", false, cancellationToken).ConfigureAwait(false);
            var sections = ReadDiscoveryFeed(DiscoveryResult(json));
            if (!sections.Any(section => section.Items.Count > 0) && landingFailure is not null)
                throw new MusicException("Не удалось загрузить рекомендации: главная недоступна, а лента не выдала поддерживаемых объектов.");
            if (landingFailure is not null)
                return sections.Select(section => section with { Note = JoinDiscoveryNote(section.Note, "Главная недоступна; показаны серверные данные ленты.") }).ToArray();
            return sections;
        }
        catch (DiscoveryUnavailable error) { throw new MusicException("Не удалось загрузить рекомендации. " + error.Message); }
    }

    public async Task<LyricsResult> GetLyricsAsync(Session session, string trackId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(trackId);
        var number = trackId.Split(':')[0];
        if (number.Length == 0) throw new ArgumentException("Пустой идентификатор трека.", nameof(trackId));
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        var signature = Convert.ToBase64String(HMACSHA256.HashData(Encoding.UTF8.GetBytes("p93jhgh689SBReK6ghtw62"), Encoding.UTF8.GetBytes(number + timestamp)));
        // TEXT is a separate modern rendition. A missing LRC does not mean that
        // the track lacks text; supplement is only the final legacy fallback.
        var failures = new List<DiscoveryUnavailable>();
        foreach (var format in new[] { "LRC", "TEXT" })
        {
            try
            {
                var path = $"/tracks/{Uri.EscapeDataString(trackId)}/lyrics?format={format}&timeStamp={timestamp}&sign={Uri.EscapeDataString(signature)}";
                using var json = await DiscoveryJsonAsync(session, path, true, cancellationToken).ConfigureAwait(false);
                var result = DiscoveryResult(json);
                var url = Text(result, "downloadUrl");
                if (!Uri.TryCreate(url, UriKind.Absolute, out var download)) throw new DiscoveryUnavailable($"Сервис не выдал адрес {format}.", false);
                var text = await DownloadDiscoveryLyricsAsync(download, cancellationToken).ConfigureAwait(false);
                if (format == "LRC") return ParseDiscoveryLrc(text);
                var plain = NormalizeDiscoveryLines(text).TrimStart('\uFEFF');
                if (string.IsNullOrWhiteSpace(plain)) throw new DiscoveryUnavailable("Источник TEXT не выдал текст.", false);
                return new(plain, [], false, "Синхронизация недоступна. " + failures[0].Message);
            }
            catch (DiscoveryUnavailable error) { failures.Add(error); }
            catch (HttpRequestException) { failures.Add(new($"Сервис {format} не ответил по сети.", true)); }
        }

        try
        {
            using var json = await DiscoveryJsonAsync(session, $"/tracks/{Uri.EscapeDataString(trackId)}/supplement", false, cancellationToken).ConfigureAwait(false);
            var result = DiscoveryResult(json);
            var note = "Синхронизация недоступна. " + string.Join(" ", failures.Select(error => error.Message));
            if (!result.TryGetProperty("lyrics", out var lyrics) || lyrics.ValueKind is JsonValueKind.Null)
            {
                if (failures.Any(error => error.Transient || error.Denied)) throw new MusicException("Не удалось загрузить текст: источник временно недоступен или отказал в доступе; резервный источник не выдал текст.");
                return new("", [], false, note + " Обычный текст для трека не предоставлен.");
            }
            if (lyrics.ValueKind != JsonValueKind.Object) throw new DiscoveryUnavailable("Изменился формат обычного текста.", false);
            var fullAllowed = Bool(lyrics, "hasRights");
            var plain = fullAllowed ? Text(lyrics, "fullLyrics") : "";
            if (string.IsNullOrWhiteSpace(plain)) plain = Text(lyrics, "lyrics");
            if (string.IsNullOrWhiteSpace(plain)) plain = "";
            if (string.IsNullOrWhiteSpace(plain) && failures.Any(error => error.Transient || error.Denied))
                throw new MusicException("Не удалось загрузить текст: источник временно недоступен или отказал в доступе; резервный источник не выдал текст.");
            if (!fullAllowed) note += " Полнота доступного обычного текста не подтверждена.";
            if (plain.Length == 0) note += " Обычный текст недоступен.";
            return new(NormalizeDiscoveryLines(plain).TrimStart('\uFEFF'), [], false, note);
        }
        catch (DiscoveryUnavailable error)
        {
            if (error.Transient || error.Denied || failures.Any(failure => failure.Transient || failure.Denied))
                throw new MusicException("Не удалось загрузить текст. " + error.Message);
            return new("", [], false, "Текст недоступен. " + string.Join(" ", failures.Select(failure => failure.Message)) + " " + error.Message);
        }
        catch (HttpRequestException) { throw new MusicException("Не удалось загрузить текст по сети."); }
    }

    private async Task<JsonDocument> DiscoveryJsonAsync(Session session, string path, bool android, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(25));
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("https://api.music.yandex.net" + path));
            request.Headers.Authorization = new AuthenticationHeaderValue("OAuth", session.AccessToken);
            if (android)
            {
                request.Headers.Add("X-Yandex-Music-Client", "YandexMusicAndroid/24023621");
                request.Headers.UserAgent.ParseAdd("Yandex-Music-API");
            }
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.Unauthorized) throw new MusicException("Вход больше не действует. Войдите в аккаунт заново.", true);
            CheckDiscoveryStatus(response);
            var bytes = await ReadDiscoveryBodyAsync(response, DiscoveryJsonLimit, timeout.Token).ConfigureAwait(false);
            timeout.Token.ThrowIfCancellationRequested();
            try { return JsonDocument.Parse(StripDiscoveryBom(bytes), new JsonDocumentOptions { MaxDepth = 48 }); }
            catch (JsonException) { throw new DiscoveryUnavailable("Сервис вернул неподдерживаемый формат данных.", false); }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new DiscoveryUnavailable("Сервис не ответил вовремя.", true); }
        catch (IOException) { throw new DiscoveryUnavailable("Не удалось дочитать данные сервиса по сети.", true); }
    }

    private async Task<string> DownloadDiscoveryLyricsAsync(Uri source, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(25));
        try
        {
            var uri = ValidateDiscoveryLyricsUri(source);
            for (var redirects = 0; ; redirects++)
            {
                // Deliberately no Authorization or Android client header on CDN.
                using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
                if ((int)response.StatusCode is >= 300 and <= 399)
                {
                    if (redirects >= 3 || response.Headers.Location is null) throw new DiscoveryUnavailable("Слишком много перенаправлений текста.", false);
                    uri = ValidateDiscoveryLyricsUri(new Uri(uri, response.Headers.Location));
                    continue;
                }
                CheckDiscoveryStatus(response);
                var bytes = await ReadDiscoveryBodyAsync(response, DiscoveryLyricsLimit, timeout.Token).ConfigureAwait(false);
                try { return new UTF8Encoding(false, true).GetString(StripDiscoveryBom(bytes).Span); }
                catch (DecoderFallbackException) { throw new DiscoveryUnavailable("Кодировка текста не поддерживается.", false); }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new DiscoveryUnavailable("Сервис текста не ответил вовремя.", true); }
        catch (IOException) { throw new DiscoveryUnavailable("Не удалось дочитать текст по сети.", true); }
    }

    private static Uri ValidateDiscoveryLyricsUri(Uri uri)
    {
        try { return ValidateMediaUri(uri); }
        catch (MusicException) { throw new DiscoveryUnavailable("Сервис вернул неподдерживаемый адрес текста.", false); }
    }
    private static void CheckDiscoveryStatus(HttpResponseMessage response)
    {
        if (response.StatusCode == HttpStatusCode.PartialContent || response.Content.Headers.ContentRange is not null)
            throw new DiscoveryUnavailable("Сервис вернул только часть данных.", false);
        if (!response.IsSuccessStatusCode)
            throw new DiscoveryUnavailable($"Источник недоступен (HTTP {(int)response.StatusCode}).", (int)response.StatusCode >= 500 || (int)response.StatusCode == 429,
                response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden);
    }
    private static async Task<byte[]> ReadDiscoveryBodyAsync(HttpResponseMessage response, int limit, CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength > limit) throw new DiscoveryUnavailable("Превышен размер данных прототипа.", false);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var body = new MemoryStream();
        var buffer = new byte[8192];
        int count;
        while ((count = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (body.Length + count > limit) throw new DiscoveryUnavailable("Превышен размер данных прототипа.", false);
            body.Write(buffer, 0, count);
        }
        if (response.Content.Headers.ContentLength is { } length && length != body.Length)
            throw new DiscoveryUnavailable("Источник передал неполные данные.", true);
        return body.ToArray();
    }
    private static ReadOnlyMemory<byte> StripDiscoveryBom(byte[] bytes)
        => bytes.Length >= 3 && bytes[0] == 239 && bytes[1] == 187 && bytes[2] == 191 ? bytes.AsMemory(3) : bytes;
    private static JsonElement DiscoveryResult(JsonDocument json)
    {
        var root = json.RootElement;
        if (root.ValueKind != JsonValueKind.Object || root.TryGetProperty("error", out var error) && error.ValueKind is not (JsonValueKind.Null or JsonValueKind.False)
            || !root.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Object)
            throw new DiscoveryUnavailable("Источник не выдал поддерживаемых данных.", false);
        if (result.TryGetProperty("error", out var nested) && nested.ValueKind is not (JsonValueKind.Null or JsonValueKind.False)
            || result.TryGetProperty("success", out var success) && success.ValueKind == JsonValueKind.False)
            throw new DiscoveryUnavailable("Сервис не подтвердил данные.", false);
        return result;
    }

    private static IReadOnlyList<HomeSection> ReadDiscoveryLanding(JsonElement result)
    {
        if (!result.TryGetProperty("blocks", out var blocks) || blocks.ValueKind != JsonValueKind.Array)
            throw new DiscoveryUnavailable("Формат главной не поддерживается.", false);
        var sections = new List<HomeSection>();
        var unsupportedBlocks = 0;
        foreach (var block in blocks.EnumerateArray())
        {
            if (sections.Count == DiscoverySectionLimit) break;
            if (block.ValueKind != JsonValueKind.Object || !block.TryGetProperty("entities", out var entities) || entities.ValueKind != JsonValueKind.Array)
            { unsupportedBlocks++; continue; }
            var items = new List<CatalogItem>();
            var skipped = 0;
            foreach (var entity in entities.EnumerateArray())
            {
                CatalogItem? item = null;
                if (entity.ValueKind == JsonValueKind.Object && entity.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object)
                {
                    var type = Text(entity, "type");
                    if (type == "personal-playlist") item = DiscoveryGeneratedPlaylist(data);
                    else if (type == "chart-item" && data.TryGetProperty("track", out var track)) item = DiscoveryItem(track, "track");
                    else if (type is "track" or "album" or "artist" or "playlist") item = DiscoveryItem(data, type);
                }
                if (item is null || items.Count == DiscoveryItemLimit) skipped++;
                else if (!items.Contains(item)) items.Add(item);
            }
            var title = Text(block, "title");
            sections.Add(new(title.Length > 0 ? title : "Подборки Яндекса", items.ToArray(),
                skipped > 0 ? $"Показана поддерживаемая часть раздела; пропущено объектов: {skipped}." : null));
        }
        if (blocks.GetArrayLength() > DiscoverySectionLimit && sections.Count > 0)
            sections[^1] = sections[^1] with { Note = JoinDiscoveryNote(sections[^1].Note, "Количество разделов ограничено прототипом.") };
        if (unsupportedBlocks > 0 && sections.Count > 0)
            sections[0] = sections[0] with { Note = JoinDiscoveryNote(sections[0].Note, "Часть разделов главной имеет неподдерживаемый формат.") };
        if (blocks.GetArrayLength() > 0 && sections.Count == 0)
            throw new DiscoveryUnavailable("Разделы главной имеют неподдерживаемый формат.", false);
        return sections;
    }

    private static IReadOnlyList<HomeSection> ReadDiscoveryFeed(JsonElement result)
    {
        var sections = new List<HomeSection>();
        var recognized = false;
        if (result.TryGetProperty("generatedPlaylists", out var generated) && generated.ValueKind == JsonValueKind.Array)
        {
            recognized = true;
            AddDiscoveryFeedSection(sections, "Персональные подборки Яндекса", generated.EnumerateArray().Select(DiscoveryGeneratedPlaylist));
        }
        if (result.TryGetProperty("days", out var days) && days.ValueKind == JsonValueKind.Array)
        {
            recognized = true;
            foreach (var day in days.EnumerateArray())
            {
                if (sections.Count == DiscoverySectionLimit) break;
                if (day.ValueKind != JsonValueKind.Object) continue;
                if (day.TryGetProperty("tracksToPlay", out var tracks) && tracks.ValueKind == JsonValueKind.Array)
                    AddDiscoveryFeedSection(sections, "Треки из ленты · " + Text(day, "day"), tracks.EnumerateArray().Select(track => DiscoveryItem(track, "track")));
                if (!day.TryGetProperty("events", out var events) || events.ValueKind != JsonValueKind.Array) continue;
                foreach (var entry in events.EnumerateArray())
                {
                    if (sections.Count == DiscoverySectionLimit) break;
                    if (entry.ValueKind != JsonValueKind.Object) continue;
                    var items = new List<CatalogItem?>();
                    foreach (var kind in new[] { "track", "album", "artist" })
                    {
                        if (!entry.TryGetProperty(kind + "s", out var rows) || rows.ValueKind != JsonValueKind.Array) continue;
                        foreach (var row in rows.EnumerateArray())
                            items.Add(DiscoveryItem(kind != "track" && row.ValueKind == JsonValueKind.Object && row.TryGetProperty(kind, out var nested) ? nested : row, kind));
                    }
                    var title = Text(entry, "title");
                    AddDiscoveryFeedSection(sections, title.Length > 0 ? title : "Из серверной ленты", items);
                }
            }
        }
        if (!recognized) throw new DiscoveryUnavailable("Формат ленты не поддерживается.", false);
        if (sections.Count == DiscoverySectionLimit)
            sections[^1] = sections[^1] with { Note = JoinDiscoveryNote(sections[^1].Note, "Выдача ограничена 32 разделами прототипа.") };
        return sections;
    }
    private static void AddDiscoveryFeedSection(List<HomeSection> sections, string title, IEnumerable<CatalogItem?> candidates)
    {
        if (sections.Count == DiscoverySectionLimit) return;
        var items = new List<CatalogItem>();
        var skipped = 0;
        foreach (var candidate in candidates)
            if (candidate is null || items.Count == DiscoveryItemLimit) skipped++;
            else if (!items.Contains(candidate)) items.Add(candidate);
        if (items.Count > 0 || skipped > 0)
            sections.Add(new(title, items.ToArray(), skipped > 0 ? $"Показана поддерживаемая часть выдачи; пропущено объектов: {skipped}." : null));
    }
    private static CatalogItem? DiscoveryGeneratedPlaylist(JsonElement row)
    {
        if (row.ValueKind != JsonValueKind.Object || row.TryGetProperty("ready", out var ready) && ready.ValueKind == JsonValueKind.False) return null;
        return row.TryGetProperty("data", out var data) ? DiscoveryItem(data, "playlist") : null;
    }
    private static CatalogItem? DiscoveryItem(JsonElement row, string kind)
    {
        if (row.ValueKind != JsonValueKind.Object) return null;
        var id = Text(row, kind == "playlist" ? "kind" : "id");
        var title = Text(row, kind == "artist" ? "name" : "title");
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(title)) return null;
        var subtitle = row.TryGetProperty("artists", out var artists) && artists.ValueKind == JsonValueKind.Array
            ? string.Join(", ", artists.EnumerateArray().Select(artist => Text(artist, "name")).Where(name => name.Length > 0)) : "";
        if (kind == "playlist")
        {
            var uid = Text(row, "uid");
            var owner = row.TryGetProperty("owner", out var ownerRow) ? Text(ownerRow, "uid") : "";
            if (owner.Length > 0 && uid.Length > 0 && owner != uid) return null;
            if (owner.Length == 0) owner = uid;
            return string.IsNullOrWhiteSpace(owner) ? null : new(id, kind, title, Text(row, "description"), owner, id, CoverUri: EntityArtwork(row, kind), OwnerName: CatalogPlaylistOwnerName(row));
        }
        string? albumId = null;
        if (kind == "track" && row.TryGetProperty("albums", out var albums) && albums.ValueKind == JsonValueKind.Array && albums.GetArrayLength() > 0)
            albumId = Text(albums[0], "id") is { Length: > 0 } value ? value : null;
        return new(id, kind, title, kind == "artist" ? "Исполнитель" : subtitle, AlbumId: albumId, CoverUri: EntityArtwork(row, kind));
    }

    private static LyricsResult ParseDiscoveryLrc(string source)
    {
        var raw = NormalizeDiscoveryLines(source).TrimStart('\uFEFF').Split('\n');
        if (raw.Length > 20000) throw new DiscoveryUnavailable("Слишком много строк LRC.", false);
        var timed = new List<LyricLine>();
        long expandedTextCharacters = 0;
        long? offset = null;
        foreach (var rawLine in raw)
        {
            var line = rawLine.Trim();
            if (line.Length == 0) continue;
            if (line.Length > 16384) throw new DiscoveryUnavailable("Слишком длинная строка LRC.", false);
            var times = new List<double>();
            var position = 0;
            while (position < line.Length && line[position] == '[')
            {
                var close = line.IndexOf(']', position + 1);
                if (close < 0) throw new DiscoveryUnavailable("Некорректная метка LRC.", false);
                var tag = line[(position + 1)..close];
                var match = DiscoveryTime.Match(tag);
                if (match.Success)
                {
                    var minutes = int.Parse(match.Groups["m"].Value, CultureInfo.InvariantCulture);
                    var seconds = int.Parse(match.Groups["s"].Value, CultureInfo.InvariantCulture);
                    var hours = match.Groups["h"].Success ? int.Parse(match.Groups["h"].Value, CultureInfo.InvariantCulture) : 0;
                    if (seconds >= 60 || match.Groups["h"].Success && minutes >= 60) throw new DiscoveryUnavailable("Некорректный таймкод LRC.", false);
                    var fraction = match.Groups["f"].Value;
                    times.Add(hours * 3600d + minutes * 60d + seconds + (fraction.Length == 0 ? 0 : int.Parse(fraction, CultureInfo.InvariantCulture) / Math.Pow(10, fraction.Length)));
                }
                else if (tag.StartsWith("offset:", StringComparison.OrdinalIgnoreCase))
                {
                    if (!long.TryParse(tag[7..].Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value)
                        || offset is { } previous && previous != value) throw new DiscoveryUnavailable("Некорректный или противоречивый offset LRC.", false);
                    offset = value;
                }
                else if (tag.Length > 0 && (char.IsAsciiDigit(tag[0]) || tag[0] == '-' && tag.Length > 1 && char.IsAsciiDigit(tag[1])))
                    throw new DiscoveryUnavailable("Таймкод LRC не поддерживается.", false);
                else if (tag.IndexOf(':') <= 0) throw new DiscoveryUnavailable("Разметка LRC не поддерживается.", false);
                position = close + 1;
            }
            var text = line[position..].Trim();
            if (times.Count == 0)
            {
                if (text.Length > 0) throw new DiscoveryUnavailable("В LRC есть строки без таймкодов.", false);
                continue;
            }
            if (text.Contains('<') && text.Contains(':')) throw new DiscoveryUnavailable("Словная разметка LRC пока не поддерживается.", false);
            if (timed.Count + times.Count > 20000) throw new DiscoveryUnavailable("Слишком много временных меток LRC.", false);
            // Repeated timestamps share their text until string.Join materializes
            // every copy. Bound the expanded text and its separators first.
            var nextTextCharacters = expandedTextCharacters + (long)text.Length * times.Count;
            if (nextTextCharacters + timed.Count + times.Count - 1 > MaximumExpandedLyricsCharacters)
                throw new DiscoveryUnavailable("Слишком большой развёрнутый текст LRC.", false);
            expandedTextCharacters = nextTextCharacters;
            foreach (var time in times) timed.Add(new(time, text));
        }
        if (timed.Count == 0 || timed.All(line => line.Text.Length == 0)) throw new DiscoveryUnavailable("LRC не содержит синхронизированных строк.", false);
        // Standard LRC: a positive offset advances lyrics, so subtract it.
        var shift = offset.GetValueOrDefault() / 1000d;
        if (Math.Abs(shift) > TimeSpan.MaxValue.TotalSeconds) throw new DiscoveryUnavailable("Offset LRC выходит за поддерживаемый диапазон времени.", false);
        if (timed.Any(line => line.TimeSeconds - shift >= TimeSpan.MaxValue.TotalSeconds))
            throw new DiscoveryUnavailable("Таймкод LRC выходит за поддерживаемый диапазон времени.", false);
        var lines = timed.OrderBy(line => line.TimeSeconds).Select(line => line with { TimeSeconds = Math.Max(0, line.TimeSeconds - shift) }).ToArray();
        return new(string.Join('\n', lines.Select(line => line.Text)), lines, true, null);
    }
    private static string NormalizeDiscoveryLines(string value) => value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
    private static string JoinDiscoveryNote(string? first, string second) => string.IsNullOrEmpty(first) ? second : first + " " + second;
    private sealed class DiscoveryUnavailable(string message, bool transient, bool denied = false) : Exception(message)
    {
        public bool Transient { get; } = transient;
        public bool Denied { get; } = denied;
    }
}
