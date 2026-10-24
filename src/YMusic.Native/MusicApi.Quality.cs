using System.Globalization;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;

namespace YMusic.Native;

internal sealed partial class MusicApi
{
    public async Task<AudioDescriptor> ResolveAudioAsync(Session session, string trackId, AudioQuality quality, CancellationToken cancellationToken)
    {
        if (!Enum.IsDefined(quality)) throw new ArgumentOutOfRangeException(nameof(quality));
        try
        {
            var result = await ResolveFileInfoAsync(session, trackId, quality, cancellationToken).ConfigureAwait(false);
            if (quality != AudioQuality.Excellent || result.Codec.StartsWith("flac", StringComparison.Ordinal) || result.Codec.Contains("aac", StringComparison.Ordinal) && result.Bitrate >= 256 || result.Bitrate >= 320) return result;
            var alternative = await ResolveStreamAsync(session, trackId, cancellationToken, 320).ConfigureAwait(false);
            return alternative.Bitrate > result.Bitrate ? new(alternative.Uri, "mp3", alternative.Bitrate) : result;
        }
        catch (MusicException error) when (!error.Authentication) { cancellationToken.ThrowIfCancellationRequested(); }
        var mp3 = await ResolveStreamAsync(session, trackId, cancellationToken, quality == AudioQuality.Economical ? 64 : quality == AudioQuality.Optimal ? 192 : 320).ConfigureAwait(false);
        return new(mp3.Uri, "mp3", mp3.Bitrate);
    }
    private async Task<AudioDescriptor> ResolveFileInfoAsync(Session session, string trackId, AudioQuality quality, CancellationToken cancellationToken)
    {
        // Android signature/header pair, as implemented by the attributed upstream.
        // Quality is a service tier, not a promise of one codec. Display actual codec.
        string[] codecs = ["flac", "flac-mp4", "mp3", "aac", "he-aac", "aac-mp4", "he-aac-mp4"];
        const string transport = "encraw";
        var requestedQuality = quality switch { AudioQuality.Economical => "lq", AudioQuality.Optimal => "nq", _ => "lossless" };
        var id = trackId.Split(':')[0];
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        var message = timestamp + id + requestedQuality + string.Concat(codecs) + transport;
        var signature = Convert.ToBase64String(HMACSHA256.HashData(Encoding.UTF8.GetBytes("p93jhgh689SBReK6ghtw62"), Encoding.UTF8.GetBytes(message))).TrimEnd('=');
        var path = $"/get-file-info?ts={timestamp}&trackId={Uri.EscapeDataString(id)}&quality={requestedQuality}&codecs={Uri.EscapeDataString(string.Join(',', codecs))}&transports={transport}&sign={Uri.EscapeDataString(signature)}";
        using var json = await ApiAsync(HttpMethod.Get, path, session, null, cancellationToken,
            new Dictionary<string, string> { ["X-Yandex-Music-Client"] = "YandexMusicAndroid/24023621", ["User-Agent"] = "Yandex-Music-API" }).ConfigureAwait(false);
        var result = Result(json);
        if (!result.TryGetProperty("downloadInfo", out var info)) throw new MusicException("Сервис не предоставил выбранное качество аудио.");
        var actualCodec = Text(info, "codec");
        var actualTransport = Text(info, "transport");
        if (!codecs.Contains(actualCodec, StringComparer.Ordinal)) throw new MusicException("Сервис вернул неподдерживаемый аудиоформат.");
        if (actualCodec.StartsWith("flac", StringComparison.Ordinal) && Text(info, "quality") != "lossless") throw new MusicException("Сервис не подтвердил качество FLAC.");
        var key = Text(info, "key");
        if (actualTransport != "raw" && (actualTransport != "encraw" || key.Length is not (32 or 48 or 64))) throw new MusicException("Этот способ доставки аудио пока не поддерживается.");
        var url = Text(info, "url");
        if (url.Length == 0 && info.TryGetProperty("urls", out var urls) && urls.ValueKind == System.Text.Json.JsonValueKind.Array)
            url = urls.EnumerateArray().FirstOrDefault(item => item.ValueKind == System.Text.Json.JsonValueKind.String).GetString() ?? "";
        if (url.Length == 0) throw new MusicException("Яндекс не вернул адрес выбранного аудио.");
        return new(ValidateMediaUri(new Uri(url)), actualCodec, Int(info, "bitrate"), actualTransport == "encraw" ? key : null);
    }
}
