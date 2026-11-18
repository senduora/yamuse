using System.ComponentModel;
using Microsoft.Win32.SafeHandles;

namespace YMusic.Native;

internal sealed partial class OfflineLibrary
{
    internal LocalAudioSource? ResolvePlaybackSource(TrackRow track)
    {
        if (!IsAvailable) return null;
        var selectedPath = track.LocalPath;
        if (selectedPath is not null && !IsIndexedPath(selectedPath))
            return new(selectedPath, track.LocalQuality ?? "Локальное аудио");

        LibraryCache? library;
        try { library = LoadLibrary(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or MusicException)
        {
            // A previously resolved file can still play when only the index is
            // damaged. Unsafe directories must never use that in-memory fallback.
            try { EnsureSafeDirectory(); }
            catch (Exception directoryError) when (directoryError is IOException or UnauthorizedAccessException or MusicException) { return null; }
            lock (_cacheLock) library = _libraryCache;
        }
        if (library is null) return null;

        var selected = selectedPath is not null && library.ById.TryGetValue(track.Id, out var variants)
            ? variants.FirstOrDefault(item => StringComparer.OrdinalIgnoreCase.Equals(ResolvePath(item.FileName), selectedPath) && ValidFile(item))
            : null;
        var item = selected ?? Find(track, library);
        return item is null ? null : new(ResolvePath(item.FileName), selected is null ? QualityLabel(item).Replace("-MP4", "") : QualityLabel(item),
            item.Bytes, item.Sha256);
    }

    private bool IsIndexedPath(string path) => StringComparer.OrdinalIgnoreCase.Equals(Path.GetDirectoryName(path), DirectoryPath);
}

internal sealed record LocalAudioSource(string Path, string Quality, long? Bytes = null, string? Digest = null)
{
    internal LocalAudioLease Hold()
    {
        UninstallPathGuard.AncestorLease? parents = null;
        SafeFileHandle? file = null;
        try
        {
            parents = UninstallPathGuard.HoldAncestors(Path);
            if (!parents.Complete) throw new FileNotFoundException();
            file = UninstallPathGuard.OpenOwnedFile(Path, read: true, deleteAccess: false);
            var length = RandomAccess.GetLength(file);
            if (length <= 0 || Bytes is { } expected && length != expected) throw new IOException();
            return new(Path, parents, file);
        }
        catch (Exception error)
        {
            file?.Dispose();
            parents?.Dispose();
            if (error is IOException or UnauthorizedAccessException or Win32Exception)
                throw new MusicException("Локальный аудиофайл изменился или недоступен. Повторите воспроизведение.");
            throw;
        }
    }
}

internal sealed class LocalAudioLease(string path, UninstallPathGuard.AncestorLease parents, SafeFileHandle file) : IDisposable
{
    internal string Path { get; } = path;

    public void Dispose()
    {
        file.Dispose();
        parents.Dispose();
    }
}
