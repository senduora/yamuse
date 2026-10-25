namespace YMusic.Native;

internal sealed partial class NativePlayer
{
    internal Task<CacheCleanupResult> ClearAnalysisCacheAsync(CancellationToken token)
        => LoudnessCache.ClearDirectoryAsync(Path.Combine(_buffer.DirectoryPath, "loudness"), token);
}
