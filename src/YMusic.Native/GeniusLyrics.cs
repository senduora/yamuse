using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace YMusic.Native;

internal sealed record GeniusLyrics(string PlainText, Uri Source);

// Anonymous public search and song HTML only. No account headers, scripts or assets.
internal sealed class GeniusLyricsClient : IDisposable
{
    private const int SearchLimit = 1024 * 1024;
    private const int PageLimit = 2 * 1024 * 1024;
    private const int TextLimit = 128 * 1024;
    private readonly HttpClient _http;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _sync = new();
    private readonly Dictionary<(string Title, string Artist), CacheEntry> _cache = new();
    private readonly Dictionary<(string Title, string Artist), QueryGate> _gates = new();
    private long _access;
    private bool _disposed;

    internal GeniusLyricsClient(HttpMessageHandler? handler = null)
    {
        _http = new HttpClient(handler ?? new SocketsHttpHandler
        {
            AllowAutoRedirect = false, UseCookies = false, Credentials = null,
            AutomaticDecompression = DecompressionMethods.All
        }) { Timeout = Timeout.InfiniteTimeSpan };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Yamuse/0.3.8");
    }

    public async Task<GeniusLyrics?> FindAsync(string title, string artist, CancellationToken cancellationToken,
        bool refresh = false, bool artistIsExact = false)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(title); ArgumentNullException.ThrowIfNull(artist);
        if (title.Length > 256 || artist.Length > 1024) return null;
        var mainArtist = (artistIsExact ? artist : artist.Split(',', 2)[0]).Trim();
        var song = Normalize(title); var performer = Normalize(mainArtist);
        if (song.Length == 0 || performer.Length == 0) return null;
        var key = (Title: title, Artist: mainArtist);
        QueryGate gate;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!refresh && TryCached(key, out var cached)) return cached;
            if (!_gates.TryGetValue(key, out gate!)) _gates[key] = gate = new();
            gate.Users++;
        }
        var entered = false;
        try
        {
            await gate.Semaphore.WaitAsync(cancellationToken).ConfigureAwait(false); entered = true;
            lock (_sync)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                cancellationToken.ThrowIfCancellationRequested();
                if (refresh) _cache.Remove(key);
                else if (TryCached(key, out var cached)) return cached;
            }
            var result = await FindCoreAsync(title, mainArtist, song, performer, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            lock (_sync)
            {
                if (!_disposed)
                {
                    foreach (var expired in _cache.Where(pair => pair.Value.Expires <= DateTimeOffset.UtcNow).Select(pair => pair.Key).ToArray()) _cache.Remove(expired);
                    if (_cache.Count >= 64) _cache.Remove(_cache.MinBy(pair => pair.Value.Access).Key);
                    _cache[key] = new(result, DateTimeOffset.UtcNow.AddMinutes(result is null ? 10 : 120), ++_access);
                }
            }
            return result;
        }
        finally
        {
            if (entered) gate.Semaphore.Release();
            lock (_sync)
            {
                if (--gate.Users == 0) { _gates.Remove(key); gate.Semaphore.Dispose(); }
            }
        }
    }

    private bool TryCached((string Title, string Artist) key, out GeniusLyrics? value)
    {
        if (_cache.TryGetValue(key, out var entry) && entry.Expires > DateTimeOffset.UtcNow)
        { _cache[key] = entry with { Access = ++_access }; value = entry.Value; return true; }
        _cache.Remove(key); value = null; return false;
    }

    private async Task<GeniusLyrics?> FindCoreAsync(string title, string artist, string song, string performer, CancellationToken cancellationToken)
    {
        var searchUri = new Uri("https://genius.com/api/search/multi?per_page=3&q=" + Uri.EscapeDataString(artist + " " + title));
        var search = await FetchAsync(searchUri, SearchLimit, cancellationToken).ConfigureAwait(false)
            ?? throw new MusicException("Genius временно не выдал ответ поиска.");
        SongMatch? source;
        try
        {
            using var json = JsonDocument.Parse(search.Body, new JsonDocumentOptions { MaxDepth = 48 });
            source = FindMatch(json.RootElement, song, performer);
        }
        catch (JsonException) { throw new MusicException("Genius вернул неподдерживаемый ответ поиска."); }
        if (source is null) return null;
        var page = await FetchAsync(source.Source, PageLimit, cancellationToken, allowMissingPage: true).ConfigureAwait(false);
        if (page is null) return null;
        if (!IsLyricsPath(page.Source)) throw UnverifiedSong();
        var pageSongId = source.Id is null ? null : ReadPageSongId(page.Body);
        if (pageSongId is not null && pageSongId != source.Id
            || !SameSongPath(source.Source, page.Source) && pageSongId is null) throw UnverifiedSong();
        if (string.IsNullOrWhiteSpace(page.Body)) throw new MusicException("Genius вернул пустую страницу песни.");
        var plain = ExtractLyrics(page.Body, false, out var modernContainer);
        if (plain.Length == 0)
        {
            plain = ExtractLyrics(page.Body, true, out var legacyContainer);
            if (!modernContainer && !legacyContainer)
                throw new MusicException("Genius вернул страницу без поддерживаемой разметки текста.");
        }
        if (plain.Length == 0) throw new MusicException("Genius не выдал читаемый текст песни.");
        return new(plain, page.Source);
    }

    private static SongMatch? FindMatch(JsonElement root, string song, string performer)
    {
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("meta", out var meta) && meta.ValueKind == JsonValueKind.Object
            && meta.TryGetProperty("status", out var status) &&
            (status.ValueKind == JsonValueKind.Number && status.TryGetInt32(out var code)
                || status.ValueKind == JsonValueKind.String && int.TryParse(status.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out code))
            && code >= 400)
            throw new MusicException($"Genius временно недоступен или отказал в доступе (HTTP {code}).");
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("response", out var response) || response.ValueKind != JsonValueKind.Object
            || !response.TryGetProperty("sections", out var sections) || sections.ValueKind != JsonValueKind.Array)
            throw new MusicException("Genius изменил формат поиска.");
        var matches = new Dictionary<string, SongMatch>(StringComparer.Ordinal);
        var uncertainSongs = false;
        foreach (var section in sections.EnumerateArray().Take(32))
        {
            var sectionType = Text(section, "type");
            if (string.IsNullOrWhiteSpace(sectionType)) { uncertainSongs = true; continue; }
            if (sectionType != "song") continue;
            if (!section.TryGetProperty("hits", out var hits) || hits.ValueKind != JsonValueKind.Array)
            {
                uncertainSongs = true;
                continue;
            }
            foreach (var hit in hits.EnumerateArray().Take(20))
            {
                if (hit.ValueKind != JsonValueKind.Object)
                {
                    uncertainSongs = true;
                    continue;
                }
                if (hit.TryGetProperty("type", out var hitType))
                {
                    var type = MusicApi.JsonString(hitType);
                    if (string.IsNullOrWhiteSpace(type)) { uncertainSongs = true; continue; }
                    if (type != "song") continue;
                }
                if (!hit.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Object
                    || !result.TryGetProperty("primary_artist", out var primary) || primary.ValueKind != JsonValueKind.Object
                    || Text(result, "title") is not { Length: > 0 } title || string.IsNullOrWhiteSpace(title)
                    || Text(primary, "name") is not { Length: > 0 } artist || string.IsNullOrWhiteSpace(artist))
                {
                    uncertainSongs = true;
                    continue;
                }
                if (!MatchesIdentity(title, song) || !MatchesIdentity(artist, performer)) continue;
                var candidate = ReadSongUri(result);
                if (candidate is null)
                {
                    uncertainSongs = true;
                    continue;
                }
                var id = ReadSearchSongId(result);
                if (matches.TryGetValue(candidate.AbsoluteUri, out var previous) && previous.Id is not null)
                {
                    if (id is not null && id != previous.Id) throw UnverifiedSong();
                    continue;
                }
                matches[candidate.AbsoluteUri] = new(candidate, id);
            }
        }
        // Multiple distinct exact matches are ambiguous; never choose an arbitrary first hit.
        if (matches.Count == 1) return matches.Values.Single();
        if (uncertainSongs) throw new MusicException("Genius вернул неподдерживаемую выдачу песен.");
        return null;
    }

    private static Uri? ReadSongUri(JsonElement result)
    {
        var url = Text(result, "url");
        Uri? candidate;
        if (url.Length > 0)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out candidate)) return null;
        }
        else
        {
            var path = Text(result, "path");
            if (!path.StartsWith('/') || path.StartsWith("//", StringComparison.Ordinal)
                || !Uri.TryCreate(new Uri("https://genius.com"), path, out candidate)) return null;
        }
        try { candidate = ValidateUri(candidate); }
        catch (MusicException) { return null; }
        return IsLyricsPath(candidate) ? candidate : null;
    }

    private async Task<BodyResult?> FetchAsync(Uri source, int limit, CancellationToken cancellationToken, bool allowMissingPage = false)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(25));
        try
        {
            var uri = ValidateUri(source);
            for (var redirects = 0; ; redirects++)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
                var code = (int)response.StatusCode;
                if (code is >= 300 and <= 399)
                {
                    if (redirects >= 3 || response.Headers.Location is not { } location || !Uri.TryCreate(uri, location, out var target))
                        throw new MusicException("Не удалось обработать перенаправление Genius.");
                    uri = ValidateUri(target); continue;
                }
                if (response.StatusCode == HttpStatusCode.PartialContent || response.Content.Headers.NonValidated.Contains("Content-Range"))
                    throw new MusicException("Genius передал только часть данных.");
                if (allowMissingPage && response.StatusCode is (HttpStatusCode.NotFound or HttpStatusCode.Gone))
                {
                    if (!SameSongPath(source, uri)) throw UnverifiedSong();
                    return null;
                }
                if (!response.IsSuccessStatusCode) throw new MusicException($"Genius временно недоступен или отказал в доступе (HTTP {code}).");
                if (response.Content.Headers.ContentLength > limit) throw new MusicException("Genius: превышен размер ответа.");
                await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
                using var bytes = new MemoryStream();
                var buffer = new byte[8192]; int count;
                while ((count = await stream.ReadAsync(buffer, timeout.Token).ConfigureAwait(false)) > 0)
                {
                    if (bytes.Length + count > limit) throw new MusicException("Genius: превышен размер ответа.");
                    bytes.Write(buffer, 0, count);
                }
                if (response.Content.Headers.ContentLength is { } length && length != bytes.Length) throw new MusicException("Genius передал неполные данные.");
                try { return new(new UTF8Encoding(false, true).GetString(bytes.ToArray()).TrimStart('\uFEFF'), uri); }
                catch (DecoderFallbackException) { throw new MusicException("Genius вернул неподдерживаемую кодировку."); }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && !_lifetime.IsCancellationRequested)
        { throw new MusicException("Genius не ответил вовремя."); }
        catch (HttpRequestException) { throw new MusicException("Не удалось загрузить текст с Genius по сети."); }
        catch (IOException) { throw new MusicException("Не удалось дочитать ответ Genius по сети."); }
    }

    private static Uri ValidateUri(Uri uri)
    {
        if (!uri.IsAbsoluteUri || uri.Scheme != Uri.UriSchemeHttps || uri.UserInfo.Length != 0 || !uri.IsDefaultPort
            || uri.Host is not ("genius.com" or "www.genius.com")) throw new MusicException("Genius вернул неподдерживаемый адрес.");
        return new UriBuilder(uri) { Host = "genius.com", Fragment = "" }.Uri;
    }
    private static bool IsLyricsPath(Uri uri) => uri.AbsolutePath.TrimEnd('/').EndsWith("-lyrics", StringComparison.OrdinalIgnoreCase);
    private static bool SameSongPath(Uri left, Uri right) => string.Equals(left.AbsolutePath.TrimEnd('/'), right.AbsolutePath.TrimEnd('/'), StringComparison.Ordinal);
    private static MusicException UnverifiedSong() => new("Не удалось подтвердить, что страница Genius относится к выбранной песне.");
    private static string Text(JsonElement value, string key) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(key, out var property) ? MusicApi.JsonString(property) : "";

    private static long? ReadSearchSongId(JsonElement result)
    {
        if (!result.TryGetProperty("id", out var id)) return null;
        if (id.ValueKind == JsonValueKind.Number && id.TryGetInt64(out var number) && number > 0) return number;
        return ParseSongId(MusicApi.JsonString(id).AsSpan());
    }

    private static long? ParseSongId(ReadOnlySpan<char> value) => value.Length is > 0 and <= 19
        && long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0 ? id : null;

    private static long? ReadPageSongId(string html)
    {
        const string prefix = "genius://songs/";
        long? songId = null;
        var inHead = false;
        for (var position = 0; position < html.Length;)
        {
            var start = html.IndexOf('<', position);
            if (start < 0) break;
            if (html.AsSpan(start).StartsWith("<!--", StringComparison.Ordinal))
            {
                var end = html.IndexOf("-->", start + 4, StringComparison.Ordinal);
                if (end < 0) break;
                position = end + 3;
                continue;
            }
            var tag = ReadTag(html, start);
            position = tag?.End ?? start + 1;
            if (tag is null) continue;
            if (tag.Name == "head")
            {
                if (tag.Closing || tag.SelfClosing) break;
                inHead = true;
                continue;
            }
            if (tag.Name == "body" || !inHead && tag.Name != "html") break;
            if (!tag.Closing && !tag.SelfClosing && tag.Name is ("script" or "style" or "title" or "noscript" or "template"))
            {
                position = SkipElement(html, tag);
                continue;
            }
            if (tag.Closing || tag.Name != "meta") continue;
            var property = Attribute(html, tag, "property");
            if (!property.Equals("al:ios:url", StringComparison.OrdinalIgnoreCase)
                && !property.Equals("al:android:url", StringComparison.OrdinalIgnoreCase)) continue;
            var content = Attribute(html, tag, "content");
            if (!content.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || ParseSongId(content.AsSpan(prefix.Length)) is not { } id) continue;
            if (songId is not null && songId != id) throw UnverifiedSong();
            songId = id;
        }
        return songId;
    }

    private static string Normalize(string value)
    {
        var result = new StringBuilder(); var separated = true;
        foreach (var rune in WebUtility.HtmlDecode(value).Normalize(NormalizationForm.FormKC).EnumerateRunes())
        {
            var category = Rune.GetUnicodeCategory(rune);
            if (category == UnicodeCategory.Format) continue;
            if (category is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark)
            {
                if (!separated) result.Append(rune);
                continue;
            }
            if (Rune.IsLetterOrDigit(rune)) { result.Append(Rune.ToLowerInvariant(rune)); separated = false; }
            else if (!separated) { result.Append(' '); separated = true; }
        }
        return result.ToString().TrimEnd();
    }

    private static bool MatchesIdentity(string candidate, string expected)
    {
        if (string.Equals(Normalize(candidate), expected, StringComparison.OrdinalIgnoreCase)) return true;
        // Genius can label a Cyrillic original with one Latin translation or
        // transliteration, e.g. Бумажки (Papers), Скриптонит (Skryptonite).
        // Only an exact Cyrillic base permits this suffix; version words stay significant.
        var value = WebUtility.HtmlDecode(candidate).Normalize(NormalizationForm.FormKC).Trim();
        var opening = value.IndexOf('(');
        if (opening <= 0 || !value.EndsWith(')') || value.LastIndexOf('(') != opening || value.IndexOf(')') != value.Length - 1) return false;
        var basis = value[..opening].TrimEnd();
        if (!string.Equals(Normalize(basis), expected, StringComparison.OrdinalIgnoreCase)) return false;
        var hasCyrillic = false;
        foreach (var rune in basis.EnumerateRunes())
        {
            if (!Rune.IsLetter(rune)) continue;
            if (rune.Value is not (>= 0x0400 and <= 0x052F or >= 0x1C80 and <= 0x1C8F or >= 0xA640 and <= 0xA69F)) return false;
            hasCyrillic = true;
        }
        if (!hasCyrillic) return false;
        var alias = value[(opening + 1)..^1].Trim(); var hasLatin = false;
        foreach (var rune in alias.EnumerateRunes())
        {
            if (!Rune.IsLetter(rune)) continue;
            if (rune.Value is not (>= 0x0041 and <= 0x007A or >= 0x00C0 and <= 0x024F or >= 0x1E00 and <= 0x1EFF)) return false;
            hasLatin = true;
        }
        if (!hasLatin) return false;
        foreach (var word in Normalize(alias).Split(' ', StringSplitOptions.RemoveEmptyEntries))
            if (word is "live" or "demo" or "acoustic" or "instrumental" or "version" or "mix" or "mixed" or "sped" or "spedup" or "speed" or "slowed" or "sloweddown"
                or "edit" or "radio" or "extended" or "unplugged" or "rework" or "reworked" or "nightcore" or "karaoke"
                or "clean" or "explicit" or "censored" or "uncensored" or "edited" or "unedited"
                || word.StartsWith("remix", StringComparison.Ordinal) || word.StartsWith("remaster", StringComparison.Ordinal)) return false;
        return true;
    }

    private static string ExtractLyrics(string html, bool legacy, out bool foundContainer)
    {
        foundContainer = false;
        var result = new StringBuilder(); var position = 0; var divDepth = 0; var activeDepth = 0;
        while (position < html.Length)
        {
            var start = html.IndexOf('<', position);
            if (start < 0) { if (activeDepth > 0) result.Append(html.AsSpan(position)); break; }
            if (activeDepth > 0) result.Append(html.AsSpan(position, start - position));
            if (html.AsSpan(start).StartsWith("<!--", StringComparison.Ordinal))
            {
                var close = html.IndexOf("-->", start + 4, StringComparison.Ordinal);
                if (close < 0) break;
                position = close + 3; continue;
            }
            var token = ReadTag(html, start);
            if (token is null) { if (activeDepth > 0) result.Append('<'); position = start + 1; continue; }
            position = token.End;
            if (!token.Closing && (token.Name is "script" or "style" or "noscript" or "template" or "svg" or "button"))
            { if (!token.SelfClosing) position = SkipElement(html, token); continue; }
            if (token.Name == "div")
            {
                if (!token.Closing && !token.SelfClosing)
                {
                    divDepth++;
                    if (activeDepth == 0 && (legacy ? Attribute(html, token, "class").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Contains("lyrics", StringComparer.OrdinalIgnoreCase)
                        : Attribute(html, token, "data-lyrics-container").Equals("true", StringComparison.OrdinalIgnoreCase)))
                    {
                        foundContainer = true;
                        if (result.Length > 0) result.Append('\n');
                        activeDepth = divDepth;
                    }
                    else if (activeDepth > 0) result.Append('\n');
                }
                else if (token.Closing)
                {
                    if (activeDepth > 0) { result.Append('\n'); if (activeDepth == divDepth) activeDepth = 0; }
                    divDepth = Math.Max(0, divDepth - 1);
                }
            }
            else if (activeDepth > 0 && (token.Name == "br" || token.Name is "p" or "li" or "section")) result.Append('\n');
            if (result.Length > PageLimit) throw new MusicException("Genius: превышен размер текста.");
        }
        if (activeDepth > 0) throw new MusicException("Genius вернул незавершённую разметку текста.");
        var decoded = WebUtility.HtmlDecode(result.ToString()).Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Replace('\u00A0', ' ');
        var lines = decoded.Split('\n').Select(line => line.Trim()).ToList();
        var normalized = new StringBuilder(); var previousBlank = false;
        foreach (var line in lines)
        {
            if (line.Length == 0) { if (normalized.Length == 0 || previousBlank) continue; previousBlank = true; }
            else previousBlank = false;
            if (normalized.Length > 0) normalized.Append('\n'); normalized.Append(line);
        }
        var plain = normalized.ToString().Trim();
        if (Encoding.UTF8.GetByteCount(plain) > TextLimit) throw new MusicException("Genius: превышен размер текста.");
        return plain;
    }

    private static Tag? ReadTag(string html, int start)
    {
        var position = start + 1; var closing = position < html.Length && html[position] == '/'; if (closing) position++;
        while (position < html.Length && char.IsWhiteSpace(html[position])) position++;
        var nameStart = position;
        while (position < html.Length && (char.IsAsciiLetterOrDigit(html[position]) || html[position] is ':' or '-')) position++;
        if (position == nameStart) return null;
        var name = html[nameStart..position].ToLowerInvariant(); var attributes = position; var quote = '\0';
        for (; position < html.Length; position++)
        {
            var value = html[position];
            if (quote != '\0') { if (value == quote) quote = '\0'; }
            else if (value is '\'' or '"') quote = value;
            else if (value == '>') return new(name, closing, position > start && html[position - 1] == '/', attributes, position, position + 1);
        }
        throw new MusicException("Genius вернул незавершённую HTML-разметку.");
    }
    private static string Attribute(string html, Tag tag, string wanted)
    {
        var position = tag.Attributes;
        while (position < tag.Close)
        {
            while (position < tag.Close && (char.IsWhiteSpace(html[position]) || html[position] == '/')) position++;
            var start = position;
            while (position < tag.Close && !char.IsWhiteSpace(html[position]) && html[position] is not ('=' or '/' or '>')) position++;
            var name = html[start..position];
            while (position < tag.Close && char.IsWhiteSpace(html[position])) position++;
            var value = "";
            if (position < tag.Close && html[position] == '=')
            {
                position++; while (position < tag.Close && char.IsWhiteSpace(html[position])) position++;
                var quote = position < tag.Close && html[position] is '\'' or '"' ? html[position++] : '\0'; start = position;
                if (quote != '\0') { while (position < tag.Close && html[position] != quote) position++; value = html[start..position]; if (position < tag.Close) position++; }
                else { while (position < tag.Close && !char.IsWhiteSpace(html[position]) && html[position] != '>') position++; value = html[start..position]; }
            }
            if (name.Equals(wanted, StringComparison.OrdinalIgnoreCase)) return WebUtility.HtmlDecode(value).Trim();
            if (position == start) position++;
        }
        return "";
    }
    private static int SkipElement(string html, Tag opening)
    {
        if (opening.Name is "script" or "style")
        {
            var prefix = "</" + opening.Name;
            for (var scan = opening.End; ; scan++)
            {
                scan = html.IndexOf(prefix, scan, StringComparison.OrdinalIgnoreCase);
                if (scan < 0) return html.Length;
                var end = scan + prefix.Length;
                if (end < html.Length && (html[end] is '>' or '/' || char.IsWhiteSpace(html[end])))
                    return ReadTag(html, scan)?.End ?? html.Length;
            }
        }
        var depth = 1; var position = opening.End;
        while (position < html.Length)
        {
            var start = html.IndexOf('<', position); if (start < 0) break;
            var tag = ReadTag(html, start); position = tag?.End ?? start + 1;
            if (tag is null || tag.Name != opening.Name) continue;
            if (tag.Closing) { if (--depth == 0) return position; }
            else if (!tag.SelfClosing) depth++;
        }
        return html.Length;
    }

    public void Dispose()
    {
        lock (_sync) { if (_disposed) return; _disposed = true; _cache.Clear(); }
        _lifetime.Cancel(); _http.Dispose();
    }
    private sealed record CacheEntry(GeniusLyrics? Value, DateTimeOffset Expires, long Access);
    private sealed record SongMatch(Uri Source, long? Id);
    private sealed class QueryGate { public readonly SemaphoreSlim Semaphore = new(1, 1); public int Users; }
    private sealed record BodyResult(string Body, Uri Source);
    private sealed record Tag(string Name, bool Closing, bool SelfClosing, int Attributes, int Close, int End);
}
