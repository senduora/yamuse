using System.Buffers;
using System.Net;
using System.Net.Http;

namespace YMusic.Native;

internal sealed record AudioProgress(long Bytes, long? Total);

// Fetch anonymously in managed HTTP. WPF only receives a local path, so its native
// network pipeline never sends Windows credentials or trips the Internet-zone gate.
internal sealed partial class AudioBuffer : IDisposable
{
    internal const long MaximumBytes = 256L * 1024 * 1024;
    private readonly HttpClient _http;
    private readonly string _directory;
    private readonly TimeSpan _rangeTimeout;
    internal string DirectoryPath => _directory;
    internal AudioBuffer(string? directory = null, HttpMessageHandler? handler = null, TimeSpan? rangeTimeout = null)
    {
        _directory = directory ?? Path.Combine(App.DataDirectory, "buffer");
        _rangeTimeout = rangeTimeout ?? TimeSpan.FromSeconds(45);
        _http = new HttpClient(handler ?? new SocketsHttpHandler
        {
            AllowAutoRedirect = false, UseCookies = false, Credentials = null,
            AutomaticDecompression = DecompressionMethods.None
        }) { Timeout = Timeout.InfiniteTimeSpan };
    }
    public async Task<BufferedAudioFile> DownloadAsync(Uri source, CancellationToken cancellationToken, IProgress<AudioProgress>? progress = null)
        => await DownloadAsync(new AudioDescriptor(source, "mp3", 0), cancellationToken, progress).ConfigureAwait(false);
    public async Task<BufferedAudioFile> DownloadAsync(AudioDescriptor audio, CancellationToken cancellationToken, IProgress<AudioProgress>? progress = null)
    {
        var uri = MusicApi.ValidateMediaUri(audio.Uri);
        using var decryptor = audio.Key is null ? null : new AesCounter(audio.Key);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(3));
        BufferedAudioFile? file = null;
        try
        {
            HttpResponseMessage response;
            for (var redirects = 0; ; redirects++)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                request.Headers.AcceptEncoding.ParseAdd("identity");
                response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
                if ((int)response.StatusCode is not (>= 300 and <= 399)) break;
                using (response)
                {
                    if (redirects >= 3 || response.Headers.Location is null) throw new MusicException("Слишком много перенаправлений аудиопотока.");
                    uri = MusicApi.ValidateMediaUri(new Uri(uri, response.Headers.Location));
                }
            }
            using (response)
            {
                if (response.StatusCode is not (HttpStatusCode.OK or HttpStatusCode.PartialContent))
                    throw new MusicException($"Аудиосервер вернул HTTP {(int)response.StatusCode}. Нажмите «Слушать» для нового адреса трека.");
                if (response.StatusCode == HttpStatusCode.PartialContent || response.Content.Headers.NonValidated.Contains("Content-Range"))
                    throw new MusicException("Аудиосервер вернул только часть файла. Загрузка не сохранена; повторите действие.");
                ValidateAudioContent(response.Content);
                var length = response.Content.Headers.ContentLength;
                if (length > MaximumBytes) throw new MusicException("Размер трека превышает лимит буфера прототипа (256 МБ).");
                await using var input = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
                file = CreateBufferedFile(audio, FileOptions.SequentialScan);
                var output = file.Stream;
                var bytes = ArrayPool<byte>.Shared.Rent(65536);
                long total = 0;
                var lastReport = Environment.TickCount64;
                try
                {
                    int read;
                    while ((read = await input.ReadAsync(bytes.AsMemory(0, 65536), timeout.Token).ConfigureAwait(false)) != 0)
                    {
                        total += read;
                        if (total > MaximumBytes) throw new MusicException("Размер трека превышает лимит буфера прототипа (256 МБ).");
                        decryptor?.Transform(bytes.AsSpan(0, read));
                        await output.WriteAsync(bytes.AsMemory(0, read), timeout.Token).ConfigureAwait(false);
                        if (Environment.TickCount64 - lastReport > 250) { progress?.Report(new(total, length)); lastReport = Environment.TickCount64; }
                    }
                }
                finally { ArrayPool<byte>.Shared.Return(bytes); }
                if (total == 0 || length is > 0 && total != length) throw new MusicException("Аудиофайл загрузился не полностью. Попробуйте ещё раз.");
                await output.FlushAsync(timeout.Token).ConfigureAwait(false);
                progress?.Report(new(total, length));
            }
            timeout.Token.ThrowIfCancellationRequested();
            file.Seal();
            return file;
        }
        catch { file?.Dispose(); throw; }
    }
    private BufferedAudioFile CreateBufferedFile(AudioDescriptor audio, FileOptions options)
        => new(Path.Combine(_directory, "ym-audio-" + Guid.NewGuid().ToString("N") + audio.Extension), options);

    private static void ValidateAudioContent(HttpContent content)
    {
        const StringComparison ignoreCase = StringComparison.OrdinalIgnoreCase;
        if (content.Headers.NonValidated.TryGetValues("Content-Encoding", out var encodings)
            && (encodings.Count == 0 || encodings.Any(value => value.Split(',').Any(encoding => !encoding.Trim().Equals("identity", ignoreCase)))))
            throw new MusicException("Аудиосервер вернул неподдерживаемое кодирование HTTP.");
        if (content.Headers.ContentType?.MediaType is not { } type) return;
        if (type.StartsWith("text/", ignoreCase) || type.Equals("application/html", ignoreCase)
            || type.Equals("application/json", ignoreCase) || type.Equals("application/xml", ignoreCase)
            || type.EndsWith("+json", ignoreCase) || type.EndsWith("+xml", ignoreCase))
            throw new MusicException("Аудиосервер вернул текстовый ответ вместо аудио.");
    }

    internal static void Delete(string? path)
    {
        if (path is null) return;
        try { File.Delete(path); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
    public void Dispose() => _http.Dispose();
}
