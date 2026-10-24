using System.Buffers;
using System.Net;
using System.Net.Http;

namespace YMusic.Native;

internal sealed record AudioProgress(long Bytes, long? Total);

// Fetch anonymously in managed HTTP. WPF only receives a local path, so its native
// network pipeline never sends Windows credentials or trips the Internet-zone gate.
internal sealed class AudioBuffer : IDisposable
{
    internal const long MaximumBytes = 256L * 1024 * 1024;
    private readonly HttpClient _http;
    private readonly string _directory;
    internal AudioBuffer(string? directory = null, HttpMessageHandler? handler = null)
    {
        _directory = directory ?? Path.Combine(App.DataDirectory, "buffer");
        _http = new HttpClient(handler ?? new SocketsHttpHandler
        {
            AllowAutoRedirect = false, UseCookies = false, Credentials = null,
            AutomaticDecompression = DecompressionMethods.None
        }) { Timeout = Timeout.InfiniteTimeSpan };
    }
    public async Task<string> DownloadAsync(Uri source, CancellationToken cancellationToken, IProgress<AudioProgress>? progress = null)
        => await DownloadAsync(new AudioDescriptor(source, "mp3", 0), cancellationToken, progress).ConfigureAwait(false);
    public async Task<string> DownloadAsync(AudioDescriptor audio, CancellationToken cancellationToken, IProgress<AudioProgress>? progress = null)
    {
        var uri = MusicApi.ValidateMediaUri(audio.Uri);
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "ym-audio-" + Guid.NewGuid().ToString("N") + audio.Extension);
        using var decryptor = audio.Key is null ? null : new AesCounter(audio.Key);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(3));
        try
        {
            HttpResponseMessage response;
            for (var redirects = 0; ; redirects++)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, uri);
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
                if (!response.IsSuccessStatusCode) throw new MusicException($"Аудиосервер вернул HTTP {(int)response.StatusCode}. Нажмите «Слушать» для нового адреса трека.");
                if (response.StatusCode == HttpStatusCode.PartialContent || response.Content.Headers.ContentRange is not null)
                    throw new MusicException("Аудиосервер вернул только часть файла. Загрузка не сохранена; повторите действие.");
                var length = response.Content.Headers.ContentLength;
                if (length > MaximumBytes) throw new MusicException("Размер трека превышает лимит буфера прототипа (256 МБ).");
                await using var input = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
                await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
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
                progress?.Report(new(total, length));
            }
            return path;
        }
        catch { Delete(path); throw; }
    }
    internal static void Delete(string? path)
    {
        if (path is null) return;
        try { File.Delete(path); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
    public void Dispose() => _http.Dispose();
}
