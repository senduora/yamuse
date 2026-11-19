using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace YMusic.Native;

internal readonly record struct LrclibLookup(LyricsResult? Lyrics, bool TemporarilyUnavailable = false)
{
    internal static LrclibLookup Unavailable => new(null, TemporarilyUnavailable: true);
}

// Anonymous exact-match GET only: https://lrclib.net/docs. No search, account headers or disk cache.
internal sealed class LrclibLyricsClient : IDisposable
{
    private const int BodyLimit = 1024 * 1024;
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly SemaphoreSlim _requestGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly CancellationToken _lifetimeToken;
    private DateTimeOffset _nextRequestUtc;
    private bool _disposed;

    internal LrclibLyricsClient(HttpClient? http = null)
    {
        _lifetimeToken = _lifetime.Token;
        _ownsHttp = http is null;
        _http = http ?? new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = false, UseCookies = false, Credentials = null,
            AutomaticDecompression = DecompressionMethods.All
        }) { Timeout = Timeout.InfiniteTimeSpan };
    }

    public async Task<LrclibLookup> FindSynchronizedAsync(TrackRow track, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(track);
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_disposed, this);
        var duration = track.DurationMs / 1000d;
        if (duration is < 1 or > 3600 || string.IsNullOrWhiteSpace(track.Title) || string.IsNullOrWhiteSpace(track.Artist)
            || track.Title.Length > 1024 || track.Artist.Length > 2048 || track.Album.Length > 1024) return default;

        var uri = new Uri("https://lrclib.net/api/get?artist_name=" + Uri.EscapeDataString(track.Artist.Trim())
            + "&track_name=" + Uri.EscapeDataString(track.Title.Trim())
            + "&album_name=" + Uri.EscapeDataString(track.Album.Trim())
            + "&duration=" + duration.ToString("0.###", CultureInfo.InvariantCulture));
        if (uri.AbsoluteUri.Length > 16384) return default;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetimeToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        var entered = false;
        try
        {
            await _requestGate.WaitAsync(timeout.Token).ConfigureAwait(false);
            entered = true;
            var delay = _nextRequestUtc - DateTimeOffset.UtcNow;
            // A previous rate-limit response is not a reason to hold a caller for a long retry.
            if (delay > TimeSpan.FromSeconds(1)) return LrclibLookup.Unavailable;
            if (delay > TimeSpan.Zero) await Task.Delay(delay, timeout.Token).ConfigureAwait(false);
            timeout.Token.ThrowIfCancellationRequested();
            if (_http.DefaultRequestHeaders.Authorization is not null || _http.DefaultRequestHeaders.Contains("Cookie"))
                throw new InvalidOperationException("Для LRCLIB нужен HTTP-клиент без учётных заголовков.");
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.UserAgent.ParseAdd("Yamuse/0.3.8");
            request.Headers.Accept.ParseAdd("application/json");
            _nextRequestUtc = DateTimeOffset.UtcNow.AddMilliseconds(250);
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            timeout.Token.ThrowIfCancellationRequested();
            var retry = response.Headers.RetryAfter;
            if (response.StatusCode == HttpStatusCode.TooManyRequests
                || response.StatusCode == HttpStatusCode.ServiceUnavailable && retry is not null)
            {
                var retryAt = retry?.Date ?? DateTimeOffset.UtcNow.Add(retry?.Delta ?? TimeSpan.FromMinutes(1));
                if (retryAt > _nextRequestUtc) _nextRequestUtc = retryAt;
            }
            if (response.Content.Headers.NonValidated.Contains("Content-Range")) return LrclibLookup.Unavailable;
            if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone) return default;
            if (response.StatusCode != HttpStatusCode.OK) return LrclibLookup.Unavailable;
            if (!Allowed(response.RequestMessage?.RequestUri ?? uri)) return LrclibLookup.Unavailable;
            var mediaType = response.Content.Headers.ContentType?.MediaType;
            if (mediaType is null || !(mediaType.Equals("application/json", StringComparison.OrdinalIgnoreCase)
                || mediaType.EndsWith("+json", StringComparison.OrdinalIgnoreCase))) return LrclibLookup.Unavailable;
            var contentLength = response.Content.Headers.ContentLength;
            if (contentLength is > BodyLimit) return LrclibLookup.Unavailable;
            using var body = await ReadBodyAsync(response.Content, timeout.Token).ConfigureAwait(false);
            if (body is null || contentLength is { } lengthInBytes && body.Length != lengthInBytes) return LrclibLookup.Unavailable;
            using var json = await JsonDocument.ParseAsync(body, cancellationToken: timeout.Token).ConfigureAwait(false);
            var record = json.RootElement;
            if (record.ValueKind != JsonValueKind.Object
                || ReadName(record, "trackName") is not { Length: > 0 } title
                || ReadName(record, "artistName") is not { Length: > 0 } artist
                || !record.TryGetProperty("duration", out var length) || length.ValueKind != JsonValueKind.Number || !length.TryGetDouble(out var actualDuration)
                || !double.IsFinite(actualDuration) || actualDuration <= 0) return LrclibLookup.Unavailable;
            if (Normalize(title) != Normalize(track.Title) || Normalize(artist) != Normalize(track.Artist)
                || Math.Abs(actualDuration - duration) > 2) return default;
            if (!string.IsNullOrWhiteSpace(track.Album))
            {
                if (!record.TryGetProperty("albumName", out var album)) return LrclibLookup.Unavailable;
                if (album.ValueKind == JsonValueKind.Null) return default;
                if (!TryReadText(album, out var albumName)) return LrclibLookup.Unavailable;
                if (Normalize(albumName) != Normalize(track.Album)) return default;
            }
            if (!record.TryGetProperty("instrumental", out var instrumental)) return LrclibLookup.Unavailable;
            if (instrumental.ValueKind == JsonValueKind.True) return default;
            if (instrumental.ValueKind != JsonValueKind.False) return LrclibLookup.Unavailable;
            if (!record.TryGetProperty("syncedLyrics", out var synced)) return LrclibLookup.Unavailable;
            if (synced.ValueKind == JsonValueKind.Null) return default;
            if (!TryReadText(synced, out var text)) return LrclibLookup.Unavailable;
            if (string.IsNullOrWhiteSpace(text)) return default;
            var parsed = MusicApi.ParseSynchronizedLyrics(text);
            timeout.Token.ThrowIfCancellationRequested();
            return parsed.Synchronized && parsed.Lines.Count > 0
                ? new(parsed with { Note = "Источник: LRCLIB · синхронизированный текст" }) : default;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && !_lifetimeToken.IsCancellationRequested) { return LrclibLookup.Unavailable; }
        catch (Exception error) when (error is HttpRequestException or IOException or JsonException or MusicException) { return LrclibLookup.Unavailable; }
        finally { if (entered) _requestGate.Release(); }
    }

    private static bool Allowed(Uri uri) => uri.IsAbsoluteUri && uri.Scheme == Uri.UriSchemeHttps && uri.Host == "lrclib.net"
        && uri.IsDefaultPort && uri.UserInfo.Length == 0 && uri.Fragment.Length == 0 && uri.AbsolutePath == "/api/get";

    private static string ReadName(JsonElement record, string property)
        => record.TryGetProperty(property, out var value) ? MusicApi.JsonString(value) : "";

    private static bool TryReadText(JsonElement value, out string text)
    {
        text = "";
        if (value.ValueKind != JsonValueKind.String) return false;
        try { text = value.GetString() ?? ""; return true; }
        catch (InvalidOperationException) { return false; }
    }

    private static string Normalize(string value) => string.Join(' ',
        value.Normalize(NormalizationForm.FormKC).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToUpperInvariant();

    private static async Task<MemoryStream?> ReadBodyAsync(HttpContent content, CancellationToken cancellationToken)
    {
        await using var source = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var destination = new MemoryStream();
        try
        {
            var buffer = new byte[8192];
            while (true)
            {
                var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read == 0) break;
                if (destination.Length + read > BodyLimit) { destination.Dispose(); return null; }
                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            }
            destination.Position = 0;
            return destination;
        }
        catch { destination.Dispose(); throw; }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _lifetime.Cancel();
        if (_ownsHttp) _http.Dispose();
        _lifetime.Dispose();
    }
}
