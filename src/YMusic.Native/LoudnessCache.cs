using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Collections.Concurrent;
using System.ComponentModel;

namespace YMusic.Native;

// Cache only a content digest and measured numbers. No account, title, URL or
// filename is persisted; playback never rewrites the original audio.
internal sealed class LoudnessCache(string directory)
{
    private const int MaximumFileBytes = 256 * 1024;
    private static readonly ConcurrentDictionary<string, CacheState> States = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _path = Path.GetFullPath(Path.Combine(directory, "loudness-v1.json"));
    private readonly CacheState _state = States.GetOrAdd(Path.GetFullPath(Path.Combine(directory, "loudness-v1.json")), _ => new());
    private object _gate => _state.Gate;
    private Dictionary<string, LoudnessResult>? _values { get => _state.Values; set => _state.Values = value; }

    internal async Task<LoudnessResult?> MeasureAsync(string path, CancellationToken token)
    {
        long generation;
        lock (_gate) generation = _state.Generation;
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var length = stream.Length; var written = File.GetLastWriteTimeUtc(path);
        var key = Convert.ToHexString(await SHA256.HashDataAsync(stream, token).ConfigureAwait(false));
        lock (_gate)
        {
            Load();
            if (_state.Pending.TryGetValue(key, out var cached) || _values?.TryGetValue(key, out cached) == true)
            {
                if (_state.Pending.Count > 0) Save();
                return cached;
            }
        }
        var result = await LoudnessAnalysis.AnalyzeAsync(path, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        if (result is null || !Valid(result) || new FileInfo(path).Length != length || File.GetLastWriteTimeUtc(path) != written) return null;
        lock (_gate)
        {
            // A clear may finish while Media Foundation analyzes the audio.
            // The result may still serve the playing slot, but cannot recreate
            // a cache entry from a generation that the user just cleared.
            if (_state.Generation != generation) return result;
            _state.Pending[key] = result;
            Trim(_state.Pending);
            Save();
        }
        return result;
    }

    internal static Task<CacheCleanupResult> ClearDirectoryAsync(string directory, CancellationToken token)
        => new LoudnessCache(directory).ClearAsync(token);

    internal Task<CacheCleanupResult> ClearAsync(CancellationToken token) => Task.Run(() =>
    {
        lock (_gate)
        {
            token.ThrowIfCancellationRequested();
            _state.Generation++;
            _values = new(StringComparer.Ordinal);
            _state.Pending.Clear();
            try
            {
                _ = UninstallPathGuard.Canonical(Path.GetDirectoryName(_path)!);
                using var writer = GuardedStoreFile.HoldWriteGate(_path);
                using var ancestors = UninstallPathGuard.HoldAncestors(_path);
                if (!ancestors.Complete || !File.Exists(_path)) return new CacheCleanupResult(0, 0, 0, 0);
                var attributes = File.GetAttributes(_path);
                if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0) return new(0, 1, 0, 0);
                if ((attributes & FileAttributes.ReadOnly) != 0) return new(0, 0, 1, 0);
                using var handle = UninstallPathGuard.OpenOwnedFile(_path, read: true);
                using var file = new FileStream(handle, FileAccess.Read, 16 * 1024, isAsync: false);
                if (file.Length is <= 0 or > MaximumFileBytes) return new(0, 1, 0, 0);
                using var document = JsonDocument.Parse(file);
                if (!ValidDocument(document.RootElement)) return new(0, 1, 0, 0);
                token.ThrowIfCancellationRequested();
                var bytes = file.Length;
                UninstallPathGuard.DeleteByHandle(file.SafeFileHandle);
                return new(1, 0, 0, bytes);
            }
            catch (JsonException) { return new(0, 1, 0, 0); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or Win32Exception) { return new(0, 0, 1, 0); }
        }
    }, token);

    private static bool ValidDocument(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return false;
        var values = root.EnumerateObject().ToArray();
        if (values.Length > 512 || values.Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() != values.Length) return false;
        foreach (var value in values)
        {
            if (value.Name.Length != 64 || !value.Name.All(Uri.IsHexDigit) || value.Value.ValueKind != JsonValueKind.Object) return false;
            var numbers = value.Value.EnumerateObject().ToArray();
            if (numbers.Length != 2 || !numbers.Any(p => p.Name == "IntegratedLufs") || !numbers.Any(p => p.Name == "Peak")
                || !value.Value.TryGetProperty("IntegratedLufs", out var lufs) || lufs.ValueKind != JsonValueKind.Number || !lufs.TryGetDouble(out var loudness)
                || !value.Value.TryGetProperty("Peak", out var peak) || peak.ValueKind != JsonValueKind.Number || !peak.TryGetDouble(out var maximum)
                || !Valid(new(loudness, maximum))) return false;
        }
        return true;
    }

    private sealed class CacheState
    {
        internal object Gate { get; } = new();
        internal long Generation;
        internal Dictionary<string, LoudnessResult>? Values;
        internal Dictionary<string, LoudnessResult> Pending { get; } = new(StringComparer.Ordinal);
    }
    private void Load()
    {
        if (_values is not null) return;
        try
        {
            _values = Parse(GuardedStoreFile.Read(_path, MaximumFileBytes));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
    private void Save()
    {
        if (_state.Pending.Count == 0) return;
        try
        {
            // A previous Load may have failed temporarily. Merge only the
            // unsaved measurements into the latest disk data inside the writer lease.
            using var storage = GuardedStoreFile.BeginWrite(_path);
            var merged = Parse(storage.ReadExisting(MaximumFileBytes));
            foreach (var (key, result) in _state.Pending)
            {
                merged.Remove(key);
                merged.Add(key, result);
            }
            Trim(merged);
            storage.Write(JsonSerializer.SerializeToUtf8Bytes(merged));
            _values = merged;
            _state.Pending.Clear();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    private static Dictionary<string, LoudnessResult> Parse(byte[]? bytes)
    {
        var values = new Dictionary<string, LoudnessResult>(StringComparer.Ordinal);
        if (bytes is null) return values;
        try
        {
            var stored = JsonSerializer.Deserialize<Dictionary<string, LoudnessResult>>(bytes);
            if (stored is not null)
                foreach (var (key, result) in stored.Take(512))
                    if (key.Length == 64 && key.All(Uri.IsHexDigit) && Valid(result)) values[key] = result;
        }
        catch (JsonException) { }
        return values;
    }

    private static void Trim(Dictionary<string, LoudnessResult> values)
    {
        while (values.Count > 512) values.Remove(values.Keys.First());
    }
    private static bool Valid(LoudnessResult? result) => result is not null
        && double.IsFinite(result.IntegratedLufs) && result.IntegratedLufs is >= -120 and <= 20
        && double.IsFinite(result.Peak) && result.Peak is > 0 and <= 100;
}
