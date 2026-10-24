using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace YMusic.Native;

// Cache only a content digest and measured numbers. No account, title, URL or
// filename is persisted; playback never rewrites the original audio.
internal sealed class LoudnessCache(string directory)
{
    private readonly object _gate = new();
    private readonly string _path = Path.Combine(directory, "loudness-v1.json");
    private Dictionary<string, LoudnessResult>? _values;

    internal async Task<LoudnessResult?> MeasureAsync(string path, CancellationToken token)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var length = stream.Length; var written = File.GetLastWriteTimeUtc(path);
        var key = Convert.ToHexString(await SHA256.HashDataAsync(stream, token).ConfigureAwait(false));
        lock (_gate)
        {
            Load();
            if (_values!.TryGetValue(key, out var cached)) return cached;
        }
        var result = await LoudnessAnalysis.AnalyzeAsync(path, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        if (result is null || !Valid(result) || new FileInfo(path).Length != length || File.GetLastWriteTimeUtc(path) != written) return null;
        lock (_gate)
        {
            _values![key] = result;
            while (_values.Count > 512) _values.Remove(_values.Keys.First());
            Save();
        }
        return result;
    }
    private void Load()
    {
        if (_values is not null) return;
        _values = new(StringComparer.Ordinal);
        try
        {
            if (!File.Exists(_path) || new FileInfo(_path).Length > 256 * 1024) return;
            var stored = JsonSerializer.Deserialize<Dictionary<string, LoudnessResult>>(File.ReadAllBytes(_path));
            if (stored is not null)
                foreach (var (key, result) in stored.Take(512))
                    if (key.Length == 64 && key.All(Uri.IsHexDigit) && Valid(result)) _values[key] = result;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { }
    }
    private void Save()
    {
        string? temporary = null;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { JsonSerializer.Serialize(file, _values); file.Flush(true); }
            File.Move(temporary, _path, true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        finally { if (temporary is not null) AudioBuffer.Delete(temporary); }
    }
    private static bool Valid(LoudnessResult? result) => result is not null
        && double.IsFinite(result.IntegratedLufs) && result.IntegratedLufs is >= -120 and <= 20
        && double.IsFinite(result.Peak) && result.Peak is > 0 and <= 100;
}
