using System.Globalization;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace YMusic.Native;

internal sealed partial class MusicApi
{
    public async Task<AudioDescriptor> ResolveAudioAsync(Session session, string trackId, AudioQuality quality, CancellationToken cancellationToken)
    {
        if (!Enum.IsDefined(quality)) throw new ArgumentOutOfRangeException(nameof(quality));
        AudioDescriptor? preferred = null;
        try
        {
            preferred = await ResolveFileInfoAsync(session, trackId, quality, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (quality != AudioQuality.Excellent || AudioQualityPolicy.IsSufficient(preferred.Codec, preferred.Bitrate, quality)) return preferred;
        }
        catch (MusicException error) when (!error.Authentication && !error.UntrustedMediaAddress) { cancellationToken.ThrowIfCancellationRequested(); }
        catch (Exception error) when (error is HttpRequestException or IOException) { cancellationToken.ThrowIfCancellationRequested(); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }
        try
        {
            var mp3 = await ResolveStreamAsync(session, trackId, cancellationToken, quality == AudioQuality.Economical ? 64 : quality == AudioQuality.Optimal ? 192 : 320).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return preferred is null || AudioQualityPolicy.IsBetter("mp3", mp3.Bitrate, preferred.Codec, preferred.Bitrate)
                ? new(mp3.Uri, "mp3", mp3.Bitrate) : preferred;
        }
        // Failure of the optional legacy alternative must not discard a valid
        // modern response or trigger the very same legacy request a second time.
        catch (MusicException error) when (preferred is not null && !error.Authentication && !error.UntrustedMediaAddress)
        { cancellationToken.ThrowIfCancellationRequested(); return preferred; }
        catch (Exception error) when (preferred is not null && error is (HttpRequestException or IOException))
        { cancellationToken.ThrowIfCancellationRequested(); return preferred; }
        catch (OperationCanceledException) when (preferred is not null && !cancellationToken.IsCancellationRequested)
        { return preferred; }
    }
    private async Task<AudioDescriptor> ResolveFileInfoAsync(Session session, string trackId, AudioQuality quality, CancellationToken cancellationToken)
    {
        // Android signature/header pair, as implemented by the attributed upstream.
        // Quality is a service tier, not a promise of one codec. Display actual codec.
        string[] codecs = ["flac", "flac-mp4", "mp3", "aac", "he-aac", "aac-mp4", "he-aac-mp4"];
        const string transport = "encraw";
        var requestedQuality = quality switch { AudioQuality.Economical => "lq", AudioQuality.Optimal => "nq", _ => "lossless" };
        _ = CatalogId(trackId);
        var id = trackId.Split(':')[0];
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        var message = timestamp + id + requestedQuality + string.Concat(codecs) + transport;
        var signature = Convert.ToBase64String(HMACSHA256.HashData(Encoding.UTF8.GetBytes("p93jhgh689SBReK6ghtw62"), Encoding.UTF8.GetBytes(message))).TrimEnd('=');
        var path = $"/get-file-info?ts={timestamp}&trackId={CatalogId(id)}&quality={requestedQuality}&codecs={Uri.EscapeDataString(string.Join(',', codecs))}&transports={transport}&sign={Uri.EscapeDataString(signature)}";
        using var json = await ApiAsync(HttpMethod.Get, path, session, null, cancellationToken,
            new Dictionary<string, string> { ["X-Yandex-Music-Client"] = "YandexMusicAndroid/24023621", ["User-Agent"] = "Yandex-Music-API" }).ConfigureAwait(false);
        var result = Result(json);
        if (result.ValueKind != JsonValueKind.Object || !result.TryGetProperty("downloadInfo", out var info) || info.ValueKind != JsonValueKind.Object)
            throw new MusicException("Сервис не предоставил выбранное качество аудио.");
        VerifyFileInfoTrack(info, id);
        var actualCodec = Text(info, "codec");
        var actualTransport = Text(info, "transport");
        if (!codecs.Contains(actualCodec, StringComparer.Ordinal)) throw new MusicException("Сервис вернул неподдерживаемый аудиоформат.");
        if (actualCodec.StartsWith("flac", StringComparison.Ordinal) && Text(info, "quality") != "lossless") throw new MusicException("Сервис не подтвердил качество FLAC.");
        var key = JsonString(info, "key");
        if (actualTransport != "raw" && (actualTransport != "encraw" || key.Length is not (32 or 48 or 64) || !key.All(char.IsAsciiHexDigit)))
            throw new MusicException("Этот способ доставки аудио пока не поддерживается.");
        var bitrate = info.TryGetProperty("bitrate", out var rate) && rate.ValueKind != JsonValueKind.Null ? Int(info, "bitrate", -1) : 0;
        if (bitrate < 0) throw new MusicException("Сервис вернул некорректный битрейт аудио.");
        return new(FileInfoUri(info), actualCodec, bitrate, actualTransport == "encraw" ? key : null);
    }

    private static void VerifyFileInfoTrack(JsonElement metadata, string requestedId)
    {
        if (!metadata.TryGetProperty("trackId", out var track) || track.ValueKind == JsonValueKind.Null) return;
        // trackId echoes the request. The canonical realId may differ for an alias.
        var reference = Text(metadata, "trackId");
        var parts = reference.Split(':', 2);
        if (parts[0] != requestedId || parts.Length == 2 && !IsTrackReferencePart(parts[1]))
            throw new MusicException("Сервис вернул адрес для другого трека.");
    }

    private static Uri FileInfoUri(JsonElement info)
    {
        if (Uri.TryCreate(JsonString(info, "url"), UriKind.Absolute, out var uri)) return ValidateMediaUri(uri);
        if (info.TryGetProperty("urls", out var urls) && urls.ValueKind == JsonValueKind.Array)
            foreach (var item in urls.EnumerateArray())
                if (Uri.TryCreate(JsonString(item), UriKind.Absolute, out uri)) return ValidateMediaUri(uri);
        throw new MusicException("Яндекс не вернул корректный адрес выбранного аудио.");
    }
}
