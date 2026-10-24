using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;
using Windows.Storage;
using YMusic.Native;

internal static partial class Program
{
    private static async Task CheckLoudness()
    {
        await Run("Loudness: actual MF PCM decode, mono/stereo reference, resampling and attenuation-only gain", async () =>
        {
            var directory = LoudnessDirectory("pcm");
            var mono = await LoudnessAnalysis.AnalyzeAsync(LoudnessWave(directory, "mono", 1, 48000, (.8, 3d)), default);
            var stereo = await LoudnessAnalysis.AnalyzeAsync(LoudnessWave(directory, "stereo", 2, 48000, (.8, 3d)), default);
            var quiet = await LoudnessAnalysis.AnalyzeAsync(LoudnessWave(directory, "quiet", 1, 48000, (.02, 3d)), default);
            var resampled = await LoudnessAnalysis.AnalyzeAsync(LoudnessWave(directory, "44100", 2, 44100, (.8, 3d)), default);
            Assert(mono is not null && stereo is not null && quiet is not null && resampled is not null, "native PCM decode returned no result");
            Assert(Math.Abs(mono!.IntegratedLufs - (-3.0103 + 20 * Math.Log10(.8))) < .1, "997 Hz mono calibration differs from expected LKFS");
            Assert(Math.Abs(stereo!.IntegratedLufs - mono.IntegratedLufs - 10 * Math.Log10(2)) < .03, "stereo channels were averaged instead of weighted/summed");
            Assert(Math.Abs(resampled!.IntegratedLufs - stereo.IntegratedLufs) < .05, "44.1 to 48 kHz native resampling changed loudness");
            Assert(Math.Abs(stereo.Peak - .8) < .002 && LoudnessAnalysis.Gain(quiet) == 1, "decoded sample peak or no-boost rule differs");
            var gain = LoudnessAnalysis.Gain(stereo);
            Assert(gain > 0 && gain < 1 && Math.Abs(stereo.IntegratedLufs + 20 * Math.Log10(gain) + 18) < .001, "target -18 LUFS attenuation differs");
            File.WriteAllText(Path.Combine(directory, "measurements.json"), JsonSerializer.Serialize(new { mono, stereo, quiet, resampled, gain }, new JsonSerializerOptions { WriteIndented = true }));
        });

        await Run("Loudness: K weighting, relative/absolute gates, digital silence and short source", async () =>
        {
            var directory = LoudnessDirectory("gates");
            var reference = await LoudnessAnalysis.AnalyzeAsync(LoudnessWave(directory, "reference", 1, 48000, (.8, 4d)), default);
            var dynamic = await LoudnessAnalysis.AnalyzeAsync(LoudnessWave(directory, "dynamic", 1, 48000, (.8, 4d), (.01, 4d), (0d, 4d)), default);
            var silence = await LoudnessAnalysis.AnalyzeAsync(LoudnessWave(directory, "silence", 2, 48000, (0d, 3d)), default);
            var shortSource = await LoudnessAnalysis.AnalyzeAsync(LoudnessWave(directory, "short", 1, 48000, (.8, .1)), default);
            var bassPath = LoudnessWave(directory, "bass", 1, 48000, frequency: 20, segments: [(.8, 3d)]);
            var bass = await LoudnessAnalysis.AnalyzeAsync(bassPath, default);
            Assert(reference is not null && dynamic is not null && bass is not null, "weighted/gated source returned no result");
            Assert(Math.Abs(reference!.IntegratedLufs - dynamic!.IntegratedLufs) < .4, "quiet passages or silence bypassed the two-stage gate");
            Assert(reference.IntegratedLufs - bass!.IntegratedLufs > 10, "K weighting did not suppress 20 Hz relative to 997 Hz");
            Assert(silence is null && shortSource is null && LoudnessAnalysis.Gain(silence) == 1, "silence/insufficient blocks fabricated loudness or gain");
        });

        await Run("Loudness: generated MP3, M4A and FLAC decode through Windows codecs without external media", async () =>
        {
            var directory = LoudnessDirectory("codecs");
            var wave = LoudnessWave(directory, "own-tone", 2, 48000, (.8, 3d));
            var reference = await LoudnessAnalysis.AnalyzeAsync(wave, default);
            var source = await StorageFile.GetFileFromPathAsync(wave);
            var folder = await StorageFolder.GetFolderFromPathAsync(directory);
            var facts = new List<object>();
            foreach (var (extension, profile) in new[] { ("mp3", MediaEncodingProfile.CreateMp3(AudioEncodingQuality.High)),
                ("m4a", MediaEncodingProfile.CreateM4a(AudioEncodingQuality.High)), ("flac", MediaEncodingProfile.CreateFlac(AudioEncodingQuality.High)) })
            {
                var destination = await folder.CreateFileAsync("own-tone." + extension, CreationCollisionOption.ReplaceExisting);
                var prepared = await new MediaTranscoder { HardwareAccelerationEnabled = false }.PrepareFileTranscodeAsync(source, destination, profile);
                Assert(prepared.CanTranscode, extension + " fixture encoder unavailable: " + prepared.FailureReason);
                await prepared.TranscodeAsync();
                var analyzed = await LoudnessAnalysis.AnalyzeAsync(destination.Path, default);
                Assert(analyzed is not null && reference is not null && Math.Abs(analyzed.IntegratedLufs - reference.IntegratedLufs) < .25,
                    extension + " native decode or integrated loudness differs");
                facts.Add(new { extension, bytes = new FileInfo(destination.Path).Length, analyzed, gain = LoudnessAnalysis.Gain(analyzed) });
            }
            File.WriteAllText(Path.Combine(directory, "codec-measurements.json"), JsonSerializer.Serialize(facts, new JsonSerializerOptions { WriteIndented = true }));
        });

        await Run("Loudness: owned loopback HTTP decode and remote/device/invalid source rejection", async () =>
        {
            var directory = LoudnessDirectory("source");
            var wave = LoudnessWave(directory, "loopback", 2, 48000, (.4, 2d));
            var expected = await LoudnessAnalysis.AnalyzeAsync(wave, default);
            await using var server = new LoudnessLoopback(File.ReadAllBytes(wave));
            var actual = await LoudnessAnalysis.AnalyzeAsync(server.Url, default).WaitAsync(TimeSpan.FromSeconds(15));
            Assert(expected is not null && actual is not null && Math.Abs(expected.IntegratedLufs - actual.IntegratedLufs) < .03 && server.Requests > 0,
                "trusted loopback source was not decoded through Media Foundation");
            await using (var redirect = new LoudnessLoopback([], redirect: true))
                Assert(await LoudnessAnalysis.AnalyzeAsync(redirect.Url, default) is null && redirect.Requests == 1, "loopback redirect was followed to a remote URL");
            await using (var oversized = new LoudnessLoopback([], advertisedLength: 512L * 1024 * 1024 + 1))
                Assert(await LoudnessAnalysis.AnalyzeAsync(oversized.Url, default) is null && oversized.Requests == 1, "oversized HTTP content was accepted");
            foreach (var source in new[] { "https://cdn.invalid/music.mp3", "http://localhost:99/music.mp3", "http://127.0.0.1@cdn.invalid/a", "capture://microphone", "relative.wav", "\\\\server\\share\\audio.wav" })
                Assert(await LoudnessAnalysis.AnalyzeAsync(source, default) is null, "untrusted/network/device source was accepted: " + source);
            var invalid = Path.Combine(directory, "invalid.wav"); File.WriteAllText(invalid, "Not an audio file");
            Assert(await LoudnessAnalysis.AnalyzeAsync(invalid, default) is null, "invalid decoder input was accepted");
            Assert(await LoudnessAnalysis.AnalyzeAsync(LoudnessWave(directory, "surround", 4, 48000, (.4, 1d)), default) is null, "unsupported multi-channel measurement was silently downmixed");
            Assert(await LoudnessAnalysis.AnalyzeAsync(new Uri(wave).AbsoluteUri, default) is not null, "owned file URI was rejected");
        });

        await Run("Loudness: cancellation releases MTA reader and global gate; gains remain finite within zero and one", async () =>
        {
            var directory = LoudnessDirectory("cancel");
            var path = LoudnessWave(directory, "long", 2, 48000, (.4, 90d));
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            await Throws<OperationCanceledException>(async () => { await LoudnessAnalysis.AnalyzeAsync(path, cancelled.Token); });
            using var active = new CancellationTokenSource();
            var pending = LoudnessAnalysis.AnalyzeAsync(path, active.Token);
            await Task.Delay(20); active.Cancel();
            await Throws<OperationCanceledException>(async () => { await pending.WaitAsync(TimeSpan.FromSeconds(10)); });
            var shortPath = LoudnessWave(directory, "after-cancel", 1, 48000, (.4, 1d));
            var results = await Task.WhenAll(LoudnessAnalysis.AnalyzeAsync(shortPath, default), LoudnessAnalysis.AnalyzeAsync(shortPath, default)).WaitAsync(TimeSpan.FromSeconds(10));
            Assert(results.All(result => result is not null) && Math.Abs(results[0]!.IntegratedLufs - results[1]!.IntegratedLufs) < .001, "cancelled reader retained the global gate or damaged MF lifetime");
            await using (var stalled = new LoudnessLoopback(File.ReadAllBytes(path), stall: true))
            {
                using var httpCancel = new CancellationTokenSource();
                var httpRead = LoudnessAnalysis.AnalyzeAsync(stalled.Url, httpCancel.Token);
                await stalled.BodyStarted.WaitAsync(TimeSpan.FromSeconds(10));
                await Task.Delay(100); httpCancel.Cancel();
                await Throws<OperationCanceledException>(async () => { await httpRead.WaitAsync(TimeSpan.FromSeconds(5)); });
            }
            foreach (var result in new LoudnessResult?[] { null, new(-100, .001), new(-18, .9), new(10000, 3), new(double.NaN, 1), new(-50, 2), new(-10, double.PositiveInfinity) })
            { var gain = LoudnessAnalysis.Gain(result); Assert(double.IsFinite(gain) && gain is >= 0 and <= 1, "unsafe normalization gain"); }
            Assert(LoudnessAnalysis.Gain(new(-50, 2)) == .5, "sample peak guard did not attenuate over-range PCM");
            File.Delete(path);
        });

        await Run("Loudness: 30-minute bound rejects partial integrated result and keeps working memory bounded", async () =>
        {
            var directory = LoudnessDirectory("duration");
            var path = LoudnessWave(directory, "over-limit", 1, 48000, (.4, 1d));
            try
            {
                // Extend a real PCM source with digital silence. A missing duration
                // guard would return the finite, gated first second as integrated.
                var dataBytes = (1800 * 48000 + 4800) * 2;
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Write))
                using (var writer = new BinaryWriter(stream))
                { stream.SetLength(dataBytes + 44L); stream.Position = 4; writer.Write(dataBytes + 36); stream.Position = 40; writer.Write(dataBytes); }
                var stopwatch = Stopwatch.StartNew();
                var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
                Assert(await LoudnessAnalysis.AnalyzeAsync(path, default).WaitAsync(TimeSpan.FromSeconds(45)) is null, "partial first 30 minutes was reported as full integrated loudness");
                var managedAllocated = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
                Assert(managedAllocated < 64L * 1024 * 1024, "streaming analysis allocated in proportion to decoded PCM duration");
                File.WriteAllText(Path.Combine(directory, "duration-facts.json"), JsonSerializer.Serialize(new { inputFrames = 1800 * 48000 + 4800, bytes = dataBytes + 44L, elapsedMs = stopwatch.ElapsedMilliseconds, managedAllocated }));
            }
            finally { File.Delete(path); }
        });
    }

    private static string LoudnessDirectory(string name)
    { var path = Path.GetFullPath(Path.Combine(_directory, "loudness", name)); Directory.CreateDirectory(path); return path; }
    private static string LoudnessWave(string directory, string name, int channels, int rate, params (double Amplitude, double Seconds)[] segments)
        => LoudnessWave(directory, name, channels, rate, 997, segments);
    private static string LoudnessWave(string directory, string name, int channels, int rate, double frequency, (double Amplitude, double Seconds)[] segments)
    {
        var path = Path.Combine(directory, name + ".wav");
        var frames = segments.Sum(segment => (int)Math.Round(segment.Seconds * rate));
        var bytes = checked(frames * channels * 2);
        using var writer = new BinaryWriter(File.Create(path));
        writer.Write("RIFF"u8); writer.Write(bytes + 36); writer.Write("WAVEfmt "u8); writer.Write(16); writer.Write((short)1); writer.Write((short)channels);
        writer.Write(rate); writer.Write(rate * channels * 2); writer.Write((short)(channels * 2)); writer.Write((short)16); writer.Write("data"u8); writer.Write(bytes);
        // Reuse a one-second PCM cycle; 997 Hz and 20 Hz both close exactly.
        var cycle = new byte[rate * channels * 2];
        foreach (var segment in segments)
        {
            for (var frame = 0; frame < rate; frame++)
            {
                var value = (short)Math.Round(Math.Sin(2 * Math.PI * frequency * frame / rate) * segment.Amplitude * short.MaxValue);
                for (var channel = 0; channel < channels; channel++)
                { var index = (frame * channels + channel) * 2; cycle[index] = (byte)value; cycle[index + 1] = (byte)(value >> 8); }
            }
            var remaining = (int)Math.Round(segment.Seconds * rate) * channels * 2;
            while (remaining > 0) { var count = Math.Min(remaining, cycle.Length); writer.Write(cycle, 0, count); remaining -= count; }
        }
        return path;
    }

    private sealed class LoudnessLoopback : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly List<Task> _clients = [];
        private readonly byte[] _data;
        private readonly Task _accept;
        private readonly bool _stall;
        private readonly bool _redirect;
        private readonly long? _advertisedLength;
        private readonly TaskCompletionSource _bodyStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task BodyStarted => _bodyStarted.Task;
        public string Url { get; }
        private int _requests;
        public int Requests => Volatile.Read(ref _requests);
        public LoudnessLoopback(byte[] data, bool stall = false, bool redirect = false, long? advertisedLength = null)
        {
            _data = data; _stall = stall; _redirect = redirect; _advertisedLength = advertisedLength; _listener.Start();
            Url = $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/own-tone.wav";
            _accept = AcceptAsync();
        }
        private async Task AcceptAsync()
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                    lock (_clients) _clients.Add(ServeAsync(client));
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException) { }
        }
        private async Task ServeAsync(TcpClient client)
        {
            using (client)
            try
            {
                var stream = client.GetStream(); var bytes = new byte[16384]; var count = 0;
                while (count < bytes.Length)
                {
                    var read = await stream.ReadAsync(bytes.AsMemory(count, bytes.Length - count), _stop.Token);
                    if (read == 0) return; count += read;
                    if (Encoding.ASCII.GetString(bytes, 0, count).Contains("\r\n\r\n", StringComparison.Ordinal)) break;
                }
                Interlocked.Increment(ref _requests);
                if (_redirect)
                {
                    await stream.WriteAsync("HTTP/1.1 302 Found\r\nLocation: https://cdn.invalid/never-contact.mp3\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"u8.ToArray(), _stop.Token);
                    return;
                }
                if (_advertisedLength is { } oversized)
                {
                    await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: audio/wav\r\nContent-Length: {oversized}\r\nConnection: close\r\n\r\n"), _stop.Token);
                    return;
                }
                var request = Encoding.ASCII.GetString(bytes, 0, count); var start = 0; var end = _data.Length - 1;
                var range = request.Split("\r\n").FirstOrDefault(line => line.StartsWith("Range: bytes=", StringComparison.OrdinalIgnoreCase));
                if (range is not null)
                {
                    var parts = range[13..].Split('-');
                    if (int.TryParse(parts[0], out var parsed)) start = Math.Clamp(parsed, 0, end);
                    if (parts.Length > 1 && int.TryParse(parts[1], out parsed)) end = Math.Clamp(parsed, start, end);
                }
                var header = $"HTTP/1.1 {(range is null ? "200 OK" : "206 Partial Content")}\r\nContent-Type: audio/wav\r\nAccept-Ranges: bytes\r\nContent-Length: {end - start + 1}\r\n"
                    + (range is null ? "" : $"Content-Range: bytes {start}-{end}/{_data.Length}\r\n") + "Connection: close\r\n\r\n";
                await stream.WriteAsync(Encoding.ASCII.GetBytes(header), _stop.Token);
                if (!request.StartsWith("HEAD ", StringComparison.Ordinal))
                {
                    if (_stall)
                    {
                        var initial = Math.Min(end - start + 1, 4 * 48000 * 2 * 2 + 44);
                        await stream.WriteAsync(_data.AsMemory(start, initial), _stop.Token);
                        _bodyStarted.TrySetResult();
                        await Task.Delay(Timeout.Infinite, _stop.Token);
                    }
                    else await stream.WriteAsync(_data.AsMemory(start, end - start + 1), _stop.Token);
                }
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or SocketException or ObjectDisposedException) { }
        }
        public async ValueTask DisposeAsync()
        {
            _stop.Cancel(); _listener.Stop(); await _accept;
            Task[] clients; lock (_clients) clients = _clients.ToArray();
            await Task.WhenAll(clients); _stop.Dispose();
        }
    }
}
