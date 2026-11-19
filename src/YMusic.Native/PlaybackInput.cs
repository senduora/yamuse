using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace YMusic.Native;

// Only our own anonymous HTTP client contacts the CDN. Windows reads a private,
// capability-addressed loopback resource; no remote headers or credentials pass through.
internal sealed class PlaybackInput : IDisposable
{
    private readonly IDisposable _resource;
    internal Uri Uri { get; }
    internal string Path { get; }
    internal bool Progressive => _resource is ProgressiveAudio;
    internal Task Completion => _resource is ProgressiveAudio audio ? audio.Completion : Task.CompletedTask;
    internal PlaybackInput(Uri uri, string path, IDisposable resource)
        { Uri = uri; Path = path; _resource = resource; }

    internal static Uri FileUri(string path)
    {
        // Implicit file URIs interpret literal percent escapes in Windows paths.
        // Encode them before WPF or WinRT obtains the path through Uri.LocalPath.
        var escaped = System.IO.Path.GetFullPath(path).Replace('\\', '/')
            .Replace("%", "%25", StringComparison.Ordinal)
            .Replace("#", "%23", StringComparison.Ordinal)
            .Replace("?", "%3F", StringComparison.Ordinal);
        return new Uri((escaped.StartsWith("//", StringComparison.Ordinal) ? "file:" : "file:///") + escaped);
    }

    public void Dispose() => _resource.Dispose();
}

internal sealed class ProgressiveAudio : IDisposable
{
    internal const int ChunkBytes = 256 * 1024;
    private readonly AudioBuffer _owner;
    private readonly AudioDescriptor _audio;
    private readonly long _length;
    private readonly BufferedAudioFile _file;
    private readonly AudioBuffer.PlaybackRepresentation _representation;
    private readonly SemaphoreSlim _fileGate = new(1);
    private readonly ConcurrentDictionary<int, ChunkAttempt> _chunks = new();
    private readonly SemaphoreSlim _fetchGate = new(2);
    private readonly SemaphoreSlim _clients = new(8);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly TcpListener _listener;
    private readonly IProgress<AudioProgress>? _progress;
    private readonly string _requestPath;
    private long _downloaded;
    private int _disposed;
    internal Uri Uri { get; }
    internal Task Completion { get; }

    internal ProgressiveAudio(AudioBuffer owner, AudioDescriptor audio, long length, BufferedAudioFile file,
        byte[] firstChunk, AudioBuffer.PlaybackRepresentation representation, IProgress<AudioProgress>? progress, CancellationToken token)
    {
        _owner = owner; _audio = audio; _length = length; _file = file; _representation = representation; _progress = progress;
        _listener = new(IPAddress.Loopback, 0);
        try
        {
            token.ThrowIfCancellationRequested();
            file.Stream.SetLength(length);
            RandomAccess.Write(file.Stream.SafeFileHandle, firstChunk, 0);
            _downloaded = firstChunk.Length;
            _chunks[0] = new(() => Task.CompletedTask);
            token.ThrowIfCancellationRequested();
            _listener.Start(8);
            _requestPath = "/audio/" + Convert.ToHexString(RandomNumberGenerator.GetBytes(32)) + audio.Extension;
            Uri = new($"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}{_requestPath}");
            _ = AcceptAsync();
            Completion = PrefetchAsync();
            _ = Completion.ContinueWith(task => { _ = task.Exception; }, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
        }
        catch { _listener.Stop(); throw; }
    }

    private async Task PrefetchAsync()
    {
        // Let Windows obtain its first headers/frames before filling the rest.
        await Task.Delay(100, _lifetime.Token).ConfigureAwait(false);
        for (var index = 1; index * (long)ChunkBytes < _length; index++)
            await PrefetchChunkAsync(index).ConfigureAwait(false);
        await _fileGate.WaitAsync(_lifetime.Token).ConfigureAwait(false);
        try
        {
            await _file.Stream.FlushAsync(_lifetime.Token).ConfigureAwait(false);
            _lifetime.Token.ThrowIfCancellationRequested();
            _file.Seal();
        }
        finally { _fileGate.Release(); }
    }

    private async Task AcceptAsync()
    {
        try
        {
            while (!_lifetime.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(_lifetime.Token).ConfigureAwait(false);
                if (!_clients.Wait(0)) { client.Dispose(); continue; }
                _ = ServeAsync(client);
            }
        }
        catch (Exception e) when (e is OperationCanceledException or SocketException or ObjectDisposedException) { }
    }

    private async Task ServeAsync(TcpClient client)
    {
        using (client)
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token))
        {
            timeout.CancelAfter(TimeSpan.FromMinutes(3));
            try
            {
                var stream = client.GetStream();
                var header = new byte[16 * 1024]; var count = 0;
                while (count < header.Length)
                {
                    var read = await stream.ReadAsync(header.AsMemory(count, 1), timeout.Token).ConfigureAwait(false);
                    if (read == 0) return;
                    count += read;
                    if (count >= 4 && header[count - 4] == 13 && header[count - 3] == 10 && header[count - 2] == 13 && header[count - 1] == 10) break;
                }
                if (count == header.Length) { await ErrorAsync(stream, 431, timeout.Token); return; }
                var lines = Encoding.ASCII.GetString(header, 0, count).Split("\r\n");
                var request = lines[0].Split(' ');
                if (request.Length != 3 || request[1] != _requestPath || request[2] is not ("HTTP/1.1" or "HTTP/1.0"))
                    { await ErrorAsync(stream, 404, timeout.Token); return; }
                if (request[0] is not ("GET" or "HEAD")) { await ErrorAsync(stream, 405, timeout.Token); return; }
                string? range = null; var hostCount = 0;
                foreach (var line in lines.Skip(1).Where(line => line.Length > 0))
                {
                    var colon = line.IndexOf(':');
                    if (colon <= 0) { await ErrorAsync(stream, 400, timeout.Token); return; }
                    var name = line[..colon]; var value = line[(colon + 1)..].Trim();
                    if (name.Equals("Host", StringComparison.OrdinalIgnoreCase))
                    {
                        hostCount++;
                        if (value != Uri.Authority) { await ErrorAsync(stream, 403, timeout.Token); return; }
                    }
                    if (name.Equals("Range", StringComparison.OrdinalIgnoreCase))
                    {
                        if (range is not null) { await ErrorAsync(stream, 416, timeout.Token); return; }
                        range = value;
                    }
                }
                if (hostCount > 1 || request[2] == "HTTP/1.1" && hostCount != 1) { await ErrorAsync(stream, 400, timeout.Token); return; }
                if (!TryRange(range, _length, out var first, out var last))
                    { await ErrorAsync(stream, 416, timeout.Token); return; }
                // Validate/fetch before committing success headers. Never present a failed
                // partial response to Windows as a valid complete audio file.
                if (request[0] == "GET") await EnsureChunkAsync((int)(first / ChunkBytes), timeout.Token).ConfigureAwait(false);
                var type = _audio.Extension switch { ".m4a" => "audio/mp4", ".flac" => "audio/flac", _ => "audio/mpeg" };
                var response = $"HTTP/1.1 {(range is null ? "200 OK" : "206 Partial Content")}\r\nConnection: close\r\nAccept-Ranges: bytes\r\nContent-Type: {type}\r\nContent-Length: {last - first + 1}\r\n";
                if (range is not null) response += $"Content-Range: bytes {first}-{last}/{_length}\r\n";
                await stream.WriteAsync(Encoding.ASCII.GetBytes(response + "\r\n"), timeout.Token).ConfigureAwait(false);
                if (request[0] == "HEAD") return;
                var buffer = new byte[64 * 1024];
                for (var position = first; position <= last;)
                {
                    var chunk = (int)(position / ChunkBytes);
                    await EnsureChunkAsync(chunk, timeout.Token).ConfigureAwait(false);
                    var available = (int)Math.Min(buffer.Length, Math.Min(last - position + 1, (chunk + 1L) * ChunkBytes - position));
                    await _fileGate.WaitAsync(timeout.Token).ConfigureAwait(false);
                    int read;
                    try { read = await RandomAccess.ReadAsync(_file.Stream.SafeFileHandle, buffer.AsMemory(0, available), position, timeout.Token).ConfigureAwait(false); }
                    finally { _fileGate.Release(); }
                    if (read == 0) throw new IOException("Incomplete playback cache.");
                    await stream.WriteAsync(buffer.AsMemory(0, read), timeout.Token).ConfigureAwait(false);
                    position += read;
                }
            }
            catch (Exception e) when (e is IOException or SocketException or OperationCanceledException or ObjectDisposedException or HttpRequestException or MusicException) { }
            finally { _clients.Release(); }
        }
    }

    private Task EnsureChunkAsync(int chunk, CancellationToken caller)
    {
        caller.ThrowIfCancellationRequested();
        var entry = _chunks.GetOrAdd(chunk, NewChunk);
        if (entry.Fetch.IsValueCreated && entry.Fetch.Value.Exception?.InnerException is AudioBuffer.RangeUnavailableException failure
            && failure.CanRetryFromReader)
        {
            var replacement = NewChunk(chunk);
            if (_chunks.TryUpdate(chunk, replacement, entry)) entry.Replaced.TrySetResult();
            entry = _chunks[chunk];
        }
        return entry.Fetch.Value.WaitAsync(caller);
    }

    private async Task PrefetchChunkAsync(int chunk)
    {
        while (true)
        {
            var entry = _chunks.GetOrAdd(chunk, NewChunk);
            try { await entry.Fetch.Value.ConfigureAwait(false); return; }
            catch (AudioBuffer.RangeUnavailableException)
            {
                // Only a reader may replace a failed attempt. Prefetch follows that
                // attempt and resumes filling the file after recovery.
                await entry.Replaced.Task.WaitAsync(_lifetime.Token).ConfigureAwait(false);
            }
        }
    }

    private ChunkAttempt NewChunk(int index) => new(() => FetchAsync(index));

    private sealed class ChunkAttempt(Func<Task> fetch)
    {
        internal Lazy<Task> Fetch { get; } = new(fetch, LazyThreadSafetyMode.ExecutionAndPublication);
        internal TaskCompletionSource Replaced { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private async Task FetchAsync(int chunk)
    {
        await _fetchGate.WaitAsync(_lifetime.Token).ConfigureAwait(false);
        try
        {
            var start = chunk * (long)ChunkBytes; var end = Math.Min(_length - 1, start + ChunkBytes - 1);
            var bytes = await _owner.ReadPlaybackRangeAsync(_audio, start, end, _length, _lifetime.Token, _representation).ConfigureAwait(false);
            await _fileGate.WaitAsync(_lifetime.Token).ConfigureAwait(false);
            try { await RandomAccess.WriteAsync(_file.Stream.SafeFileHandle, bytes, start, _lifetime.Token).ConfigureAwait(false); }
            finally { _fileGate.Release(); }
            var downloaded = Interlocked.Add(ref _downloaded, bytes.Length);
            _progress?.Report(new(downloaded, _length));
        }
        finally { _fetchGate.Release(); }
    }

    internal static bool TryRange(string? range, long length, out long first, out long last)
    {
        first = 0; last = length - 1;
        if (range is null) return length > 0;
        if (!range.StartsWith("bytes=", StringComparison.Ordinal) || range.Contains(',')) return false;
        var parts = range[6..].Split('-');
        if (parts.Length != 2) return false;
        if (parts[0].Length == 0)
        {
            if (!long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var suffix) || suffix <= 0) return false;
            first = Math.Max(0, length - suffix); return true;
        }
        if (!long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out first) || first < 0 || first >= length) return false;
        if (parts[1].Length > 0 && !long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out last)) return false;
        last = Math.Min(last, length - 1);
        return last >= first;
    }
    private Task ErrorAsync(NetworkStream stream, int code, CancellationToken token)
    {
        var range = code == 416 ? $"Content-Range: bytes */{_length}\r\n" : "";
        return stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 {code} Unavailable\r\nConnection: close\r\n{range}Content-Length: 0\r\n\r\n"), token).AsTask();
    }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _lifetime.Cancel(); _listener.Stop(); _file.Dispose();
        // In-flight requests own their token registrations and finish after cancellation.
    }
}
