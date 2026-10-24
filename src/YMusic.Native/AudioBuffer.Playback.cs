using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;

namespace YMusic.Native;

internal sealed partial class AudioBuffer
{
    internal async Task<PlaybackInput> OpenPlaybackAsync(AudioDescriptor audio, CancellationToken cancellationToken, IProgress<AudioProgress>? progress = null)
    {
        Directory.CreateDirectory(_directory);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(3));
        using var response = await PlaybackRequestAsync(audio.Uri, 0, ProgressiveAudio.ChunkBytes - 1, timeout.Token).ConfigureAwait(false);
        var path = Path.Combine(_directory, "ym-audio-" + Guid.NewGuid().ToString("N") + audio.Extension);
        try
        {
            if (response.StatusCode == HttpStatusCode.PartialContent)
            {
                var range = response.Content.Headers.ContentRange;
                if (range?.From != 0 || range.To is null || range.Length is not > 0 || range.Length > MaximumBytes
                    || range.To != Math.Min(range.Length.Value - 1, ProgressiveAudio.ChunkBytes - 1))
                    throw new MusicException("Аудиосервер вернул некорректный диапазон.");
                var bytes = await ReadRangeBodyAsync(response, audio, 0, (int)(range.To.Value + 1), timeout.Token).ConfigureAwait(false);
                var resource = new ProgressiveAudio(this, audio, range.Length.Value, path, bytes, progress);
                progress?.Report(new(bytes.Length, range.Length));
                return new(resource.Uri, path, resource);
            }
            if (!response.IsSuccessStatusCode || response.StatusCode != HttpStatusCode.OK || response.Content.Headers.ContentRange is not null)
                throw new MusicException($"Аудиосервер вернул HTTP {(int)response.StatusCode}.");
            // Servers without Range retain the complete-file path. Reuse this same
            // response, so unsupported ranges never cause a second full download.
            var length = response.Content.Headers.ContentLength;
            if (length > MaximumBytes) throw new MusicException("Размер трека превышает лимит буфера (256 МБ).");
            using var decryptor = audio.Key is null ? null : new AesCounter(audio.Key);
            await using (var input = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false))
            await using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                var buffer = new byte[65536]; long total = 0; var reported = Environment.TickCount64;
                while (true)
                {
                    var read = await input.ReadAsync(buffer, timeout.Token).ConfigureAwait(false);
                    if (read == 0) break;
                    total += read;
                    if (total > MaximumBytes) throw new MusicException("Размер трека превышает лимит буфера (256 МБ).");
                    decryptor?.Transform(buffer.AsSpan(0, read));
                    await output.WriteAsync(buffer.AsMemory(0, read), timeout.Token).ConfigureAwait(false);
                    if (Environment.TickCount64 - reported > 250) { progress?.Report(new(total, length)); reported = Environment.TickCount64; }
                }
                if (total == 0 || length is > 0 && length != total) throw new MusicException("Аудиофайл загрузился не полностью.");
                progress?.Report(new(total, length));
            }
            return new(new Uri(path), path);
        }
        catch { Delete(path); throw; }
    }

    private async Task<HttpResponseMessage> PlaybackRequestAsync(Uri source, long first, long last, CancellationToken token)
    {
        var uri = MusicApi.ValidateMediaUri(source);
        for (var redirects = 0; ; redirects++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.Range = new RangeHeaderValue(first, last);
            var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            if ((int)response.StatusCode is not (>= 300 and <= 399)) return response;
            using (response)
            {
                if (redirects >= 3 || response.Headers.Location is null) throw new MusicException("Слишком много перенаправлений аудиопотока.");
                uri = MusicApi.ValidateMediaUri(new Uri(uri, response.Headers.Location));
            }
        }
    }
    internal async Task<byte[]> ReadPlaybackRangeAsync(AudioDescriptor audio, long first, long last, long length, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(45));
        using var response = await PlaybackRequestAsync(audio.Uri, first, last, timeout.Token).ConfigureAwait(false);
        var range = response.Content.Headers.ContentRange;
        if (response.StatusCode != HttpStatusCode.PartialContent || range?.From != first || range.To != last || range.Length != length)
            throw new MusicException("Аудиосервер не подтвердил диапазон для перемотки. Откройте трек снова.");
        return await ReadRangeBodyAsync(response, audio, first, checked((int)(last - first + 1)), timeout.Token).ConfigureAwait(false);
    }
    private static async Task<byte[]> ReadRangeBodyAsync(HttpResponseMessage response, AudioDescriptor audio, long offset, int count, CancellationToken token)
    {
        if (response.Content.Headers.ContentLength is { } length && length != count) throw new MusicException("Некорректная длина аудиодиапазона.");
        var bytes = new byte[count];
        await using var input = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        await input.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
        var extra = new byte[1];
        if (await input.ReadAsync(extra, token).ConfigureAwait(false) != 0) throw new MusicException("Аудиосервер превысил запрошенный диапазон.");
        using var decryptor = audio.Key is null ? null : new AesCounter(audio.Key, offset);
        decryptor?.Transform(bytes);
        return bytes;
    }
}
