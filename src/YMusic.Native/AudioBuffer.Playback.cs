using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;

namespace YMusic.Native;

internal sealed partial class AudioBuffer
{
    internal async Task<PlaybackInput> OpenPlaybackAsync(AudioDescriptor audio, CancellationToken cancellationToken, IProgress<AudioProgress>? progress = null)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(3));
        var initial = await ReadPlaybackStartAsync(audio, timeout.Token).ConfigureAwait(false);
        using var response = initial.Response;
        BufferedAudioFile? file = null;
        ProgressiveAudio? resource = null;
        try
        {
            if (initial.Chunk is { } bytes)
            {
                timeout.Token.ThrowIfCancellationRequested();
                file = CreateBufferedFile(audio, FileOptions.RandomAccess);
                resource = new ProgressiveAudio(this, audio, initial.Length, file, bytes, new PlaybackRepresentation(response.Headers.ETag), progress);
                progress?.Report(new(bytes.Length, initial.Length));
                return new(resource.Uri, file.Path, resource);
            }
            // Servers without Range retain the complete-file path. Reuse this same
            // response, so unsupported ranges never cause a second full download.
            var length = response.Content.Headers.ContentLength;
            if (length > MaximumBytes) throw new MusicException("Размер трека превышает лимит буфера (256 МБ).");
            using var decryptor = audio.Key is null ? null : new AesCounter(audio.Key);
            file = CreateBufferedFile(audio, FileOptions.SequentialScan);
            var output = file.Stream;
            await using (var input = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false))
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
                await output.FlushAsync(timeout.Token).ConfigureAwait(false);
                progress?.Report(new(total, length));
            }
            timeout.Token.ThrowIfCancellationRequested();
            file.Seal();
            return new(new Uri(file.Path), file.Path, file);
        }
        catch
        {
            if (resource is not null) resource.Dispose();
            else file?.Dispose();
            throw;
        }
    }

    private async Task<(HttpResponseMessage Response, byte[]? Chunk, long Length)> ReadPlaybackStartAsync(AudioDescriptor audio, CancellationToken token)
    {
        for (var attempt = 0; ; attempt++)
        {
            token.ThrowIfCancellationRequested();
            HttpResponseMessage? response = null;
            try
            {
                response = await PlaybackRequestAsync(audio.Uri, 0, ProgressiveAudio.ChunkBytes - 1, token).ConfigureAwait(false);
                if (response.StatusCode == HttpStatusCode.OK)
                {
                    if (response.Content.Headers.NonValidated.Contains("Content-Range"))
                        throw new MusicException("Аудиосервер вернул диапазон вместо полного аудиофайла.");
                    ValidateAudioContent(response.Content);
                    return (response, null, 0);
                }
                if (response.StatusCode != HttpStatusCode.PartialContent)
                    throw new MusicException($"Аудиосервер вернул HTTP {(int)response.StatusCode}.");
                var range = ReadByteRange(response);
                if (range is null || range.From != 0 || range.To is null || range.Length is not > 0 || range.Length > MaximumBytes
                    || range.To != Math.Min(range.Length.Value - 1, ProgressiveAudio.ChunkBytes - 1))
                    throw new MusicException("Аудиосервер вернул некорректный диапазон.");
                var bytes = await ReadRangeBodyAsync(response, audio, 0, (int)(range.To.Value + 1), token).ConfigureAwait(false);
                return (response, bytes, range.Length.Value);
            }
            catch (Exception error)
            {
                response?.Dispose();
                if (attempt != 0 || token.IsCancellationRequested || !IsTransientRangeFailure(error, timedOut: false)) throw;
            }
        }
    }

    private async Task<HttpResponseMessage> PlaybackRequestAsync(Uri source, long first, long last, CancellationToken token,
        EntityTagHeaderValue? entityTag = null)
    {
        var uri = MusicApi.ValidateMediaUri(source);
        for (var redirects = 0; ; redirects++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.Range = new RangeHeaderValue(first, last);
            request.Headers.AcceptEncoding.ParseAdd("identity");
            if (entityTag is not null) request.Headers.IfRange = new RangeConditionHeaderValue(entityTag);
            var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            if ((int)response.StatusCode is not (>= 300 and <= 399)) return response;
            using (response)
            {
                if (redirects >= 3 || response.Headers.Location is null) throw new MusicException("Слишком много перенаправлений аудиопотока.");
                uri = MusicApi.ValidateMediaUri(new Uri(uri, response.Headers.Location));
            }
        }
    }
    internal async Task<byte[]> ReadPlaybackRangeAsync(AudioDescriptor audio, long first, long last, long length, CancellationToken token,
        PlaybackRepresentation? representation = null)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(token);
        budget.CancelAfter(_rangeTimeout * 2);
        for (var attempt = 0; ; attempt++)
        {
            token.ThrowIfCancellationRequested();
            if (budget.IsCancellationRequested) throw new RangeUnavailableException(new TimeoutException());
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(budget.Token);
            timeout.CancelAfter(_rangeTimeout);
            try
            {
                using var response = await PlaybackRequestAsync(audio.Uri, first, last, timeout.Token, representation?.EntityTag).ConfigureAwait(false);
                if (response.StatusCode == HttpStatusCode.ServiceUnavailable)
                    throw new RangeUnavailableException(response.Headers.RetryAfter);
                var range = ReadByteRange(response);
                if (response.StatusCode != HttpStatusCode.PartialContent || range is null
                    || range.From != first || range.To != last || range.Length != length)
                    throw new MusicException("Аудиосервер не подтвердил диапазон для перемотки. Откройте трек снова.");
                var bytes = await ReadRangeBodyAsync(response, audio, first, checked((int)(last - first + 1)), timeout.Token).ConfigureAwait(false);
                representation?.Confirm(response.Headers.ETag);
                return bytes;
            }
            catch (RangeUnavailableException error)
            {
                token.ThrowIfCancellationRequested();
                var delay = error.RemainingDelay;
                if (attempt != 0 || delay > TimeSpan.FromSeconds(3) || budget.IsCancellationRequested) throw;
                try { await Task.Delay(delay, budget.Token).ConfigureAwait(false); }
                catch (OperationCanceledException timeoutError) when (!token.IsCancellationRequested)
                { throw new RangeUnavailableException(timeoutError); }
            }
            catch (Exception error) when (!token.IsCancellationRequested && IsTransientRangeFailure(error, timeout.IsCancellationRequested))
            {
                if (attempt != 0 || budget.IsCancellationRequested) throw new RangeUnavailableException(error);
            }
        }
    }

    internal sealed class RangeUnavailableException : IOException
    {
        private readonly long _retryAt;

        internal RangeUnavailableException(RetryConditionHeaderValue? retryAfter)
            : base("Аудиосервер временно недоступен (HTTP 503).")
        {
            var requestedDelay = retryAfter?.Delta ?? (retryAfter?.Date - DateTimeOffset.UtcNow) ?? TimeSpan.Zero;
            var delay = (long)Math.Ceiling(Math.Max(500, requestedDelay.TotalMilliseconds));
            _retryAt = Environment.TickCount64 + delay;
        }

        internal RangeUnavailableException(Exception cause)
            : base("Часть аудиопотока временно недоступна.", cause)
        {
            _retryAt = Environment.TickCount64 + 500;
        }

        internal TimeSpan RemainingDelay => TimeSpan.FromMilliseconds(Math.Max(0, _retryAt - Environment.TickCount64));
    }

    internal sealed class PlaybackRepresentation(EntityTagHeaderValue? initialTag)
    {
        private EntityTagHeaderValue? _entityTag = StrongTag(initialTag);

        internal EntityTagHeaderValue? EntityTag => Volatile.Read(ref _entityTag);

        internal void Confirm(EntityTagHeaderValue? received)
        {
            if (StrongTag(received) is not { } tag) return;
            var current = Interlocked.CompareExchange(ref _entityTag, tag, null);
            if (current is not null && !string.Equals(current.Tag, tag.Tag, StringComparison.Ordinal))
                throw new MusicException("Аудиофайл на сервере изменился во время воспроизведения. Откройте трек снова.");
        }

        private static EntityTagHeaderValue? StrongTag(EntityTagHeaderValue? tag)
            => tag is { IsWeak: false, Tag: not "*" } ? tag : null;
    }

    private static bool IsTransientRangeFailure(Exception error, bool timedOut) => error switch
    {
        HttpRequestException request => request.HttpRequestError is HttpRequestError.ConnectionError or HttpRequestError.ResponseEnded,
        HttpIOException read => read.HttpRequestError is HttpRequestError.ConnectionError or HttpRequestError.ResponseEnded,
        IOException => true,
        OperationCanceledException => timedOut,
        _ => false
    };

    private static ContentRangeHeaderValue? ReadByteRange(HttpResponseMessage response)
    {
        var headers = response.Content.Headers;
        if (!headers.NonValidated.TryGetValues("Content-Range", out var entries) || entries.Count != 1) return null;
        var range = headers.ContentRange;
        return string.Equals(range?.Unit, "bytes", StringComparison.OrdinalIgnoreCase) ? range : null;
    }

    private static async Task<byte[]> ReadRangeBodyAsync(HttpResponseMessage response, AudioDescriptor audio, long offset, int count, CancellationToken token)
    {
        ValidateAudioContent(response.Content);
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
