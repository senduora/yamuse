using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;

namespace YMusic.Native;

internal sealed partial class AudioBuffer
{
    private const int MaximumPlaybackRedirects = 3;
    private const int MinimumRangeDelayMilliseconds = 500;
    private const int MaximumInlineRangeDelayMilliseconds = 3000;

    internal async Task<PlaybackInput> OpenPlaybackAsync(AudioDescriptor audio, CancellationToken cancellationToken, IProgress<AudioProgress>? progress = null)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(3));
        var token = timeout.Token;
        var source = audio.Uri;
        RangeHeaderValue? requestedRange = new(0, ProgressiveAudio.ChunkBytes - 1);
        (PlaybackRepresentation Version, long Length)? knownRepresentation = null;
        for (var attempt = 0; ; attempt++)
        {
            token.ThrowIfCancellationRequested();
            HttpResponseMessage? response = null;
            var readingHttp = true;
            try
            {
                var initial = await PlaybackRequestAsync(source, requestedRange, token).ConfigureAwait(false);
                response = initial.Response;
                CheckRangeAvailability(response);
                if (requestedRange is not null && response.StatusCode == HttpStatusCode.PartialContent)
                {
                    var range = ReadByteRange(response);
                    if (range?.From is not { } from || range.To is not { } to || from < 0 || from > to || to >= ProgressiveAudio.ChunkBytes
                        || (range.Length is { } total && (total <= to || total > MaximumBytes)))
                        throw new MusicException("Аудиосервер вернул некорректный диапазон.");
                    var count = (int)(to - from + 1);
                    var version = new PlaybackRepresentation(initial.Resource, response.Headers, source, initial.Route);
                    if (from == 0 && range.Length is { } completeLength
                        && (count == completeLength || (count == ProgressiveAudio.ChunkBytes && version.EntityTag is not null)))
                    {
                        var bytes = await ReadRangeBodyAsync(response, audio, 0, count, token).ConfigureAwait(false);
                        readingHttp = false;
                        return await OpenProgressiveAsync(audio, version, completeLength, bytes, progress, token).ConfigureAwait(false);
                    }

                    // The selected subrange cannot initialize the fixed-size progressive buffer.
                    ValidateRangeContent(response, count);
                    if (range.Length is { } expected && version.EntityTag is not null) knownRepresentation = (version, expected);
                    response.Dispose();
                    response = null;
                    source = initial.Resource;
                    requestedRange = null;
                    initial = await PlaybackRequestAsync(source, requestedRange, token).ConfigureAwait(false);
                    response = initial.Response;
                    CheckRangeAvailability(response);
                }
                if (response.StatusCode != HttpStatusCode.OK)
                    throw new MusicException($"Аудиосервер вернул HTTP {(int)response.StatusCode}.");
                if (response.Content.Headers.NonValidated.Contains("Content-Range"))
                    throw new MusicException("Аудиосервер вернул диапазон вместо полного аудиофайла.");
                ValidateAudioContent(response.Content);
                long? expectedLength = knownRepresentation is { } known && known.Version.Matches(initial.Resource)
                    && known.Version.MatchesValidator(response.Headers) ? known.Length : null;
                readingHttp = false;
                return await OpenFullPlaybackAsync(response, audio, expectedLength, progress, token).ConfigureAwait(false);
            }
            catch (RangeUnavailableException error) when (readingHttp)
            {
                response?.Dispose();
                response = null;
                if (!await WaitToRetryRangeAsync(error, attempt, token).ConfigureAwait(false)) throw;
            }
            catch (Exception error) when (attempt == 0 && !token.IsCancellationRequested
                && (error is PlaybackBodyReadException || (readingHttp && IsTransientRangeFailure(error, timedOut: false))))
            {
                // The next attempt starts from a fresh response and buffer.
            }
            finally { response?.Dispose(); }
        }
    }

    private async Task<PlaybackInput> OpenProgressiveAsync(AudioDescriptor audio, PlaybackRepresentation version, long length,
        byte[] bytes, IProgress<AudioProgress>? progress, CancellationToken token)
    {
        var file = await CreateBufferedFileAsync(audio, FileOptions.RandomAccess, token).ConfigureAwait(false);
        ProgressiveAudio? resource = null;
        try
        {
            token.ThrowIfCancellationRequested();
            resource = new ProgressiveAudio(this, audio, length, file, bytes, version, progress, token);
            progress?.Report(new(bytes.Length, length));
            token.ThrowIfCancellationRequested();
            return new(resource.Uri, file.Path, resource);
        }
        catch
        {
            if (resource is not null) resource.Dispose();
            else file.Dispose();
            throw;
        }
    }

    private async Task<PlaybackInput> OpenFullPlaybackAsync(HttpResponseMessage response, AudioDescriptor audio, long? expectedLength,
        IProgress<AudioProgress>? progress, CancellationToken token)
    {
        var declaredLength = ValidateBodyFraming(response);
        if (declaredLength > MaximumBytes) throw new MusicException("Размер трека превышает лимит буфера (256 МБ).");
        if (expectedLength is { } expected && declaredLength is { } declared && expected != declared)
            throw new MusicException("Длина полного аудиоответа не совпадает с длиной подтверждённой версии.");
        var length = expectedLength ?? declaredLength;
        using var decryptor = audio.Key is null ? null : new AesCounter(audio.Key);
        var file = await CreateBufferedFileAsync(audio, FileOptions.SequentialScan, token).ConfigureAwait(false);
        var readingHttp = false;
        try
        {
            var output = file.Stream;
            readingHttp = true;
            await using (var input = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false))
            {
                var buffer = new byte[65536]; long total = 0; var reported = Environment.TickCount64;
                while (true)
                {
                    readingHttp = true;
                    var read = await input.ReadAsync(buffer, token).ConfigureAwait(false);
                    readingHttp = false;
                    if (read == 0) break;
                    total += read;
                    if (total > MaximumBytes) throw new MusicException("Размер трека превышает лимит буфера (256 МБ).");
                    if (expectedLength is { } required && total > required)
                        throw new MusicException("Длина полного аудиоответа не совпадает с длиной подтверждённой версии.");
                    decryptor?.Transform(buffer.AsSpan(0, read));
                    await output.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
                    if (Environment.TickCount64 - reported > 250) { progress?.Report(new(total, length)); reported = Environment.TickCount64; }
                }
                if (total == 0 || length is > 0 && length != total) throw new MusicException("Аудиофайл загрузился не полностью.");
                await output.FlushAsync(token).ConfigureAwait(false);
                progress?.Report(new(total, length));
            }
            token.ThrowIfCancellationRequested();
            file.Seal();
            token.ThrowIfCancellationRequested();
            return new(PlaybackInput.FileUri(file.Path), file.Path, file);
        }
        catch (Exception error)
        {
            file.Dispose();
            if (readingHttp && !token.IsCancellationRequested && IsTransientRangeFailure(error, timedOut: false))
                throw new PlaybackBodyReadException(error);
            throw;
        }
    }

    private sealed class PlaybackBodyReadException(Exception cause) : IOException("Не удалось полностью прочитать аудиопоток.", cause);

    private async Task<(HttpResponseMessage Response, Uri Resource, IReadOnlyList<Uri> Route)> PlaybackRequestAsync(Uri source, RangeHeaderValue? range,
        CancellationToken token, PlaybackRepresentation? representation = null)
    {
        var uri = MusicApi.ValidateMediaUri(source);
        var route = new List<Uri>(MaximumPlaybackRedirects + 1);
        for (var redirects = 0; ; redirects++)
        {
            route.Add(uri);
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.Range = range;
            request.Headers.AcceptEncoding.ParseAdd("identity");
            if (range is not null && representation is not null && representation.Matches(uri) && representation.EntityTag is { } entityTag)
                request.Headers.IfRange = new RangeConditionHeaderValue(entityTag);
            var response = range is not null && representation is not null
                ? await representation.SendRangeAsync(_http, request, token, canRediscoverRoute: !representation.Matches(uri)
                    && !string.Equals(source.AbsoluteUri, uri.AbsoluteUri, StringComparison.Ordinal))
                    .ConfigureAwait(false)
                : await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            if ((int)response.StatusCode is not (>= 300 and <= 399)) return (response, uri, route);
            using (response)
            {
                if (redirects >= MaximumPlaybackRedirects || response.Headers.Location is null) throw new MusicException("Слишком много перенаправлений аудиопотока.");
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
            // Waiting for another block's 429 belongs to the total budget.
            // Start the per-attempt timeout only when this block can be sent.
            if (representation is not null)
            {
                try { await representation.WaitForRangeAdmissionAsync(MusicApi.ValidateMediaUri(audio.Uri), budget.Token).ConfigureAwait(false); }
                catch (OperationCanceledException timeoutError) when (!token.IsCancellationRequested)
                { throw new RangeUnavailableException(timeoutError); }
            }
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(budget.Token);
            timeout.CancelAfter(_rangeTimeout);
            try
            {
                return await ReadRangeAttemptAsync(audio, first, last, length, representation, timeout.Token).ConfigureAwait(false);
            }
            catch (RangeUnavailableException error)
            {
                token.ThrowIfCancellationRequested();
                if (budget.IsCancellationRequested) throw;
                try { if (!await WaitToRetryRangeAsync(error, attempt, budget.Token).ConfigureAwait(false)) throw; }
                catch (OperationCanceledException timeoutError) when (!token.IsCancellationRequested)
                { throw new RangeUnavailableException(timeoutError); }
            }
            catch (Exception error) when (!token.IsCancellationRequested && IsTransientRangeFailure(error, timeout.IsCancellationRequested))
            {
                if (attempt != 0 || budget.IsCancellationRequested) throw new RangeUnavailableException(error);
            }
        }
    }

    private async Task<byte[]> ReadRangeAttemptAsync(AudioDescriptor audio, long first, long last, long length,
        PlaybackRepresentation? representation, CancellationToken token)
    {
        const int maximumParts = 4;
        var bytes = new byte[checked((int)(last - first + 1))];
        var remaining = new Stack<(long First, long Last)>();
        remaining.Push((first, last));
        for (var part = 0; remaining.TryPop(out var requested); part++)
        {
            if (part == maximumParts) throw new MusicException("Аудиосервер слишком сильно дробит диапазон.");
            var rangeResponse = await PlaybackRequestAsync(audio.Uri, new(requested.First, requested.Last), token, representation).ConfigureAwait(false);
            using var response = rangeResponse.Response;
            CheckRangeAvailability(response, lateRange: true);
            if (response.StatusCode is (HttpStatusCode.OK or HttpStatusCode.PartialContent)
                && representation is not null && !representation.Matches(rangeResponse.Resource))
                throw PlaybackRepresentation.Changed();
            var range = ReadByteRange(response);
            if (response.StatusCode != HttpStatusCode.PartialContent || range is null
                || (range.Length is { } reported ? reported != length : representation?.EntityTag is null)
                || range.From is not { } from || range.To is not { } to || from < requested.First || to > requested.Last || to >= length || from > to)
                throw new MusicException("Аудиосервер не подтвердил диапазон для перемотки. Откройте трек снова.");
            representation?.Verify(response.Headers);
            if (representation?.EntityTag is null && (from != requested.First || to != requested.Last))
                throw new MusicException("Аудиосервер вернул неполный диапазон без сильного ETag.");
            var destination = bytes.AsMemory(checked((int)(from - first)), checked((int)(to - from + 1)));
            await ReadRangeIntoAsync(response, audio, from, destination, token).ConfigureAwait(false);
            representation?.RememberVerifiedRoute(rangeResponse.Route);
            if (to < requested.Last) remaining.Push((to + 1, requested.Last));
            if (from > requested.First) remaining.Push((requested.First, from - 1));
        }
        return bytes;
    }

    private static void CheckRangeAvailability(HttpResponseMessage response, bool lateRange = false)
    {
        var retryAfter = response.Headers.RetryAfter;
        if (response.StatusCode == HttpStatusCode.ServiceUnavailable
            || response.StatusCode == HttpStatusCode.TooManyRequests && (lateRange || retryAfter is not null))
            throw new RangeUnavailableException(response.StatusCode, retryAfter);
    }

    private static async Task<bool> WaitToRetryRangeAsync(RangeUnavailableException error, int attempt, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var delay = error.RemainingDelay;
        if (error.DeferredBeforeRequest || error.RetryOnlyFromReader || attempt != 0
            || delay > TimeSpan.FromMilliseconds(MaximumInlineRangeDelayMilliseconds))
            return false;
        await Task.Delay(delay, token).ConfigureAwait(false);
        return true;
    }

    internal sealed class RangeUnavailableException : IOException
    {
        private readonly long? _routeDiscoveryAt;
        internal long RetryAt { get; }
        internal bool DeferredBeforeRequest { get; }
        internal bool RetryOnlyFromReader { get; }

        internal RangeUnavailableException(HttpStatusCode status, RetryConditionHeaderValue? retryAfter)
            : this(status, GetRetryAt(retryAfter), retryOnlyFromReader: status == HttpStatusCode.TooManyRequests && retryAfter is null) { }

        internal RangeUnavailableException(HttpStatusCode status, long retryAt, bool deferredBeforeRequest = false,
            bool canRediscoverRoute = false, bool retryOnlyFromReader = false)
            : base($"Аудиосервер временно недоступен (HTTP {(int)status}).")
        {
            RetryAt = retryAt;
            DeferredBeforeRequest = deferredBeforeRequest;
            RetryOnlyFromReader = retryOnlyFromReader;
            if (canRediscoverRoute) _routeDiscoveryAt = Environment.TickCount64 + MinimumRangeDelayMilliseconds;
        }

        internal RangeUnavailableException(Exception cause, bool deferredBeforeRequest = false)
            : base("Часть аудиопотока временно недоступна.", cause)
        {
            RetryAt = Environment.TickCount64 + MinimumRangeDelayMilliseconds;
            DeferredBeforeRequest = deferredBeforeRequest;
        }

        internal TimeSpan RemainingDelay => TimeSpan.FromMilliseconds(Math.Max(0, RetryAt - Environment.TickCount64));
        internal bool CanRetryFromReader => Environment.TickCount64 >= Math.Min(RetryAt, _routeDiscoveryAt ?? RetryAt);

        private static long GetRetryAt(RetryConditionHeaderValue? hint)
        {
            var requestedDelay = hint?.Delta ?? (hint?.Date - DateTimeOffset.UtcNow) ?? TimeSpan.Zero;
            return Environment.TickCount64 + (long)Math.Ceiling(Math.Max(MinimumRangeDelayMilliseconds, requestedDelay.TotalMilliseconds));
        }
    }

    internal sealed class PlaybackRepresentation
    {
        private const int MaximumRecipientStates = 16;
        private const int MaximumVerifiedRecipients = 2 * (MaximumPlaybackRedirects + 1);
        private readonly Uri _resource;
        private readonly object _rangeGate = new();
        private readonly Dictionary<string, RecipientState> _recipients = new(StringComparer.Ordinal);
        private readonly Dictionary<string, RecipientState> _verifiedRecipients = new(StringComparer.Ordinal);
        private long _verifiedRouteVersion;
        internal EntityTagHeaderValue? EntityTag { get; }

        internal PlaybackRepresentation(Uri resource, HttpResponseHeaders headers, Uri? startingUri = null,
            IReadOnlyList<Uri>? initialRoute = null)
        {
            _resource = resource;
            EntityTag = ReadStrongTag(headers);
            RememberVerifiedRoute(initialRoute ?? [startingUri ?? resource, resource], initial: true);
        }

        private sealed class RecipientState
        {
            internal long RetryAt;
            internal int ActiveRequests;
            internal bool InitialRoute;
            internal long LastVerifiedRoute;
        }

        internal void RememberVerifiedRoute(IReadOnlyList<Uri> route, bool initial = false)
        {
            lock (_rangeGate)
            {
                var keys = route.Select(uri => uri.AbsoluteUri).Distinct(StringComparer.Ordinal).ToArray();
                var version = ++_verifiedRouteVersion;
                foreach (var key in keys)
                    if (_verifiedRecipients.TryGetValue(key, out var known)) known.LastVerifiedRoute = version;
                var added = keys.Where(key => !_verifiedRecipients.ContainsKey(key)).ToArray();
                var required = _verifiedRecipients.Count + added.Length - MaximumVerifiedRecipients;
                if (required > 0)
                {
                    var now = Environment.TickCount64;
                    var expired = _verifiedRecipients.Where(pair => !pair.Value.InitialRoute && pair.Value.ActiveRequests == 0
                        && pair.Value.RetryAt <= now && !keys.Contains(pair.Key, StringComparer.Ordinal))
                        .OrderBy(pair => pair.Value.LastVerifiedRoute).Take(required).ToArray();
                    if (expired.Length != required) return;
                    foreach (var pair in expired) _verifiedRecipients.Remove(pair.Key);
                }
                foreach (var key in added)
                {
                    var state = _recipients.Remove(key, out var existing) ? existing : new RecipientState();
                    state.InitialRoute = initial;
                    state.LastVerifiedRoute = version;
                    _verifiedRecipients.Add(key, state);
                }
            }
        }

        private RecipientState? FindRecipient(string key)
            => _verifiedRecipients.GetValueOrDefault(key) ?? _recipients.GetValueOrDefault(key);

        internal async Task WaitForRangeAdmissionAsync(Uri recipient, CancellationToken token)
        {
            var key = recipient.AbsoluteUri;
            var waitUntil = Environment.TickCount64 + MaximumInlineRangeDelayMilliseconds;
            while (true)
            {
                token.ThrowIfCancellationRequested();
                long retryAt;
                lock (_rangeGate) retryAt = FindRecipient(key)?.RetryAt ?? 0;
                if (retryAt <= Environment.TickCount64) return;
                await WaitForRecipientAsync(retryAt, waitUntil, token).ConfigureAwait(false);
            }
        }

        internal async Task<HttpResponseMessage> SendRangeAsync(HttpClient client, HttpRequestMessage request, CancellationToken token,
            bool canRediscoverRoute)
        {
            var key = request.RequestUri!.AbsoluteUri;
            var waitUntil = Environment.TickCount64 + MaximumInlineRangeDelayMilliseconds;
            RecipientState state;
            Task<HttpResponseMessage> pending;
            while (true)
            {
                long retryAt;
                lock (_rangeGate)
                {
                    token.ThrowIfCancellationRequested();
                    var now = Environment.TickCount64;
                    var existing = FindRecipient(key);
                    retryAt = existing?.RetryAt ?? 0;
                    if (retryAt <= now)
                    {
                        state = existing ?? AddRecipient(key, now);
                        state.ActiveRequests++;
                        try { pending = client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token); }
                        catch { ReleaseRecipient(key, state); throw; }
                        break;
                    }
                }
                await WaitForRecipientAsync(retryAt, waitUntil, token, canRediscoverRoute).ConfigureAwait(false);
            }
            try
            {
                var response = await pending.ConfigureAwait(false);
                if (response.StatusCode != HttpStatusCode.TooManyRequests)
                    return response;
                using (response)
                {
                    var failure = new RangeUnavailableException(response.StatusCode, response.Headers.RetryAfter);
                    long retryAt;
                    lock (_rangeGate) retryAt = state.RetryAt = Math.Max(state.RetryAt, failure.RetryAt);
                    throw new RangeUnavailableException(response.StatusCode, retryAt, canRediscoverRoute: canRediscoverRoute,
                        retryOnlyFromReader: failure.RetryOnlyFromReader);
                }
            }
            finally { ReleaseRecipient(key, state); }
        }

        private RecipientState AddRecipient(string key, long now)
        {
            if (_recipients.Count >= MaximumRecipientStates)
                foreach (var expired in _recipients.Where(entry => entry.Value.ActiveRequests == 0 && entry.Value.RetryAt <= now).ToArray())
                    _recipients.Remove(expired.Key);
            if (_recipients.Count >= MaximumRecipientStates)
                throw new RangeUnavailableException(new IOException("Слишком много временно недоступных адресов аудиопотока."),
                    deferredBeforeRequest: true);
            var state = new RecipientState();
            _recipients.Add(key, state);
            return state;
        }

        private void ReleaseRecipient(string key, RecipientState state)
        {
            lock (_rangeGate)
            {
                state.ActiveRequests--;
                if (state.ActiveRequests == 0 && state.RetryAt <= Environment.TickCount64) _recipients.Remove(key);
            }
        }

        private static Task WaitForRecipientAsync(long retryAt, long waitUntil, CancellationToken token, bool canRediscoverRoute = false)
        {
            if (retryAt > waitUntil)
                throw new RangeUnavailableException(HttpStatusCode.TooManyRequests, retryAt, deferredBeforeRequest: true,
                    canRediscoverRoute: canRediscoverRoute);
            return Task.Delay(TimeSpan.FromMilliseconds(Math.Max(0, retryAt - Environment.TickCount64)), token);
        }

        internal bool Matches(Uri uri) => string.Equals(_resource.AbsoluteUri, uri.AbsoluteUri, StringComparison.Ordinal);

        internal bool MatchesValidator(HttpResponseHeaders observed)
            => EntityTag is { } expected && string.Equals(expected.Tag, ReadStrongTag(observed)?.Tag, StringComparison.Ordinal);

        internal void Verify(HttpResponseHeaders received)
        {
            if (!MatchesValidator(received)) throw Changed();
        }

        internal static MusicException Changed() => new("Аудиофайл на сервере изменился во время воспроизведения. Откройте трек снова.");

        internal static EntityTagHeaderValue? ReadStrongTag(HttpResponseHeaders headers)
        {
            if (!headers.NonValidated.TryGetValues("ETag", out var entries) || entries.Count != 1) return null;
            return headers.ETag is { IsWeak: false, Tag: not "*" } tag ? tag : null;
        }
    }

    private static bool IsTransientRangeFailure(Exception error, bool timedOut) => error switch
    {
        HttpRequestException request => IsTransientRangeFailure(request.HttpRequestError),
        HttpIOException read => IsTransientRangeFailure(read.HttpRequestError),
        IOException => true,
        OperationCanceledException => timedOut,
        _ => false
    };

    private static bool IsTransientRangeFailure(HttpRequestError error)
        => error is HttpRequestError.ConnectionError or HttpRequestError.ResponseEnded or HttpRequestError.NameResolutionError;

    private static ContentRangeHeaderValue? ReadByteRange(HttpResponseMessage response)
    {
        var headers = response.Content.Headers;
        if (!headers.NonValidated.TryGetValues("Content-Range", out var entries) || entries.Count != 1) return null;
        var range = headers.ContentRange;
        return string.Equals(range?.Unit, "bytes", StringComparison.OrdinalIgnoreCase) ? range : null;
    }

    private static async Task<byte[]> ReadRangeBodyAsync(HttpResponseMessage response, AudioDescriptor audio, long offset, int count, CancellationToken token)
    {
        var bytes = new byte[count];
        await ReadRangeIntoAsync(response, audio, offset, bytes, token).ConfigureAwait(false);
        return bytes;
    }

    private static async Task ReadRangeIntoAsync(HttpResponseMessage response, AudioDescriptor audio, long offset, Memory<byte> bytes, CancellationToken token)
    {
        ValidateRangeContent(response, bytes.Length);
        await using var input = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        await input.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
        var extra = new byte[1];
        if (await input.ReadAsync(extra, token).ConfigureAwait(false) != 0) throw new MusicException("Аудиосервер превысил запрошенный диапазон.");
        using var decryptor = audio.Key is null ? null : new AesCounter(audio.Key, offset);
        decryptor?.Transform(bytes.Span);
    }

    private static void ValidateRangeContent(HttpResponseMessage response, int count)
    {
        ValidateAudioContent(response.Content);
        if (ValidateBodyFraming(response) is { } length && length != count) throw new MusicException("Некорректная длина аудиодиапазона.");
    }
}
