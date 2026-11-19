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
    private const int MaximumEntries = 512;
    private static readonly ConcurrentDictionary<string, CacheState> States = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _path = Path.GetFullPath(Path.Combine(directory, "loudness-v1.json"));
    private readonly CacheState _state = States.GetOrAdd(Path.GetFullPath(Path.Combine(directory, "loudness-v1.json")), _ => new());
    private object _gate => _state.Gate;
    private OrderedDictionary<string, LoudnessResult>? _values { get => _state.Values; set => _state.Values = value; }

    internal async Task<LoudnessResult?> MeasureAsync(string path, CancellationToken token, string? verifiedDigest = null)
    {
        long generation;
        lock (_gate) generation = _state.Generation;
        // The indexed playback slot keeps the already verified file pinned
        // throughout analysis, so its digest is a safe cache key.
        await using var stream = verifiedDigest is null
            ? new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan)
            : null;
        var length = stream?.Length ?? new FileInfo(path).Length;
        var written = File.GetLastWriteTimeUtc(path);
        var key = verifiedDigest ?? Convert.ToHexString(await SHA256.HashDataAsync(stream!, token).ConfigureAwait(false));
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
            _state.Pending.Remove(key);
            _state.Pending.Add(key, result);
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
            var preserved = 0;
            try
            {
                _ = UninstallPathGuard.Canonical(Path.GetDirectoryName(_path)!);
                using var writer = GuardedStoreFile.HoldWriteGate(_path);
                using var ancestors = UninstallPathGuard.HoldAncestors(_path);
                if (!ancestors.Complete) return new CacheCleanupResult(0, 0, 0, 0);
                var recovery = new GuardedStoreRecovery(_path, GuardedStoreKind.Loudness);
                preserved = recovery.CountConflictArchives();
                using var handle = GuardedStoreFile.OpenCacheForCleanupUnderWriteGate(_path, GuardedStoreKind.Loudness, out var recovered);
                if (recovered.ArchivedPrevious) preserved++;
                if (recovered.PreserveCurrent) return new(0, preserved + 1, 0, 0);
                if (handle is null) return new(0, preserved, 0, 0);
                if ((File.GetAttributes(handle) & FileAttributes.ReadOnly) != 0) return new(0, preserved, 1, 0);
                if (GuardedStoreFile.ReadFromHandle(handle, MaximumFileBytes) is not { } bytes) return new(0, preserved + 1, 0, 0);
                using var document = JsonDocument.Parse(bytes);
                if (!ValidDocument(document.RootElement)) return new(0, preserved + 1, 0, 0);
                token.ThrowIfCancellationRequested();
                if (FileSecurityPreservation.HasNamedStreamsOrExtendedAttributes(handle)) return new(0, preserved + 1, 0, 0);
                token.ThrowIfCancellationRequested();
                UninstallPathGuard.DeleteByHandle(handle);
                return new(1, preserved, 0, bytes.Length);
            }
            catch (JsonException) { return new(0, preserved + 1, 0, 0); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or Win32Exception) { return new(0, preserved, 1, 0); }
        }
    }, token);

    private static bool ValidDocument(JsonElement root)
    {
        try
        {
            if (root.ValueKind != JsonValueKind.Object) return false;
            var values = root.EnumerateObject().ToArray();
            if (values.Length > MaximumEntries || values.Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() != values.Length) return false;
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
        catch (InvalidOperationException) { return false; } // Decoding a JSON property name can reject malformed Unicode.
    }

    private sealed class CacheState
    {
        internal object Gate { get; } = new();
        internal long Generation;
        internal OrderedDictionary<string, LoudnessResult>? Values;
        internal OrderedDictionary<string, LoudnessResult> Pending { get; } = new(StringComparer.Ordinal);
    }
    private void Load()
    {
        if (_values is not null) return;
        try
        {
            _values = Parse(GuardedStoreFile.Read(_path, MaximumFileBytes, GuardedStoreKind.Loudness));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { }
    }
    private void Save()
    {
        if (_state.Pending.Count == 0) return;
        try
        {
            // A previous Load may have failed temporarily. Merge only the
            // unsaved measurements into the latest disk data inside the writer lease.
            using var storage = GuardedStoreFile.BeginWrite(_path, kind: GuardedStoreKind.Loudness);
            var bytes = storage.ReadExisting(MaximumFileBytes);
            if (bytes is null && storage.Exists) return;
            var merged = Parse(bytes);
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
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { }
    }

    private static OrderedDictionary<string, LoudnessResult> Parse(byte[]? bytes)
    {
        if (bytes is null) return new(StringComparer.Ordinal);
        using var document = JsonDocument.Parse(bytes);
        if (!ValidDocument(document.RootElement)) throw new JsonException("Не подтверждён формат кэша анализа громкости.");
        return document.RootElement.Deserialize<OrderedDictionary<string, LoudnessResult>>() ?? throw new JsonException();
    }

    private static void Trim(OrderedDictionary<string, LoudnessResult> values)
    {
        while (values.Count > MaximumEntries) values.RemoveAt(0);
    }
    private static bool Valid(LoudnessResult? result) => result is not null
        && double.IsFinite(result.IntegratedLufs) && result.IntegratedLufs is >= -120 and <= 20
        && double.IsFinite(result.Peak) && result.Peak is > 0 and <= 100;
}
