using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

namespace YMusic.Native;

internal sealed partial class OfflineLibrary
{
    private readonly ConcurrentDictionary<string, ImmutableHashSet<RejectedAudioRevision>> _rejectedAudio = new(StringComparer.OrdinalIgnoreCase);
    private sealed record RejectedAudioRevision(string Digest, FileSecurityPreservation.FileRevision Revision);
    internal Action? BeforeOpenedCheckpointProofForChecks { get; set; }

    internal LocalAudioSource? ResolvePlaybackSource(TrackRow track, bool requireExactOfflineVariant = false)
    {
        if (!IsAvailable) return null;
        var selectedPath = track.LocalPath;
        var exact = requireExactOfflineVariant && track.IsOfflineLibraryRow;
        if (selectedPath is not null && !IsIndexedPath(selectedPath))
            return exact ? null : new(selectedPath, track.LocalQuality ?? "Локальное аудио");

        var library = LoadPlaybackLibrary();
        if (library is null) return null;

        var preference = track.PreferredLocalVariant;
        var restoredDigest = preference.Sha256 is null ? track.RestoredLocalSha256 : null;
        var selected = selectedPath is not null && library.ById.TryGetValue(track.Id, out var variants)
            ? variants.FirstOrDefault(item => StringComparer.OrdinalIgnoreCase.Equals(ResolvePath(item.FileName), selectedPath)
                && (!track.IsOfflineLibraryRow || item.RequestId == (preference.RequestId ?? track.RequestId)
                    && MatchesSelectedQuality(item, preference))
                && (restoredDigest is null || StringComparer.OrdinalIgnoreCase.Equals(item.Sha256, restoredDigest))
                && ValidFile(item))
            : null;
        // A visible saved-quality row denotes a particular file. Resuming an
        // existing occurrence may still use another saved version of that track.
        if (selected is not null)
            return ResolveIndexedPlaybackSource(track, selected, library, selected: true, exact,
                admitLegacyExact: MatchesLegacySelection(selected, preference)
                    && (restoredDigest is not null || IsUnambiguousSelectedCopy(selected, preference, library)));
        if (track.IsOfflineLibraryRow && (selectedPath is null || BoundEdition(track) is not null))
        {
            var missingFileName = restoredDigest is not null && selectedPath is not null && IsMissingPlaybackPath(selectedPath)
                ? Path.GetFileName(selectedPath) : null;
            return FindBoundCheckpointItem(track, library, exact, missingFileName) is { } bound
                ? ResolveIndexedPlaybackSource(track, bound, library, selected: exact, exact,
                    admitLegacyExact: MatchesLegacySelection(bound, preference)
                        && (StringComparer.OrdinalIgnoreCase.Equals(restoredDigest, bound.Sha256)
                            || (restoredDigest is null && selectedPath is null || missingFileName is not null)
                                && IsUnambiguousSelectedCopy(bound, preference, library, missingFileName))) : null;
        }
        if (exact) return null;
        return Find(track, library) is { } item
            ? ResolveIndexedPlaybackSource(track, item, library, selected: false, exact: false) : null;
    }

    internal OfflineItem? FindCheckpointPlaybackItem(TrackRow track, out bool exactQuality)
    {
        exactQuality = false;
        if (!IsAvailable) return null;
        var library = LoadLibrary();
        var item = track.IsOfflineLibraryRow ? FindBoundCheckpointItem(track, library) : Find(track, library);
        var preference = track.PreferredLocalVariant;
        exactQuality = item is not null && (!track.IsOfflineLibraryRow
            || MatchesSelectedQuality(item, preference)
                && (track.RestoredLocalSha256 is { } expected ? StringComparer.OrdinalIgnoreCase.Equals(item.Sha256, expected)
                    : IsUnambiguousSelectedCopy(item, preference, library)));
        return item;
    }

    private static bool MatchesSelectedQuality(OfflineItem item,
        (string? RequestId, string? Codec, int? Bitrate, int? ConfirmedBitrate, string? Sha256) preference) =>
        preference.Codec is null || item.Codec == preference.Codec && item.Bitrate == preference.Bitrate
            && (preference.Sha256 is { } sha256 ? StringComparer.OrdinalIgnoreCase.Equals(sha256, item.Sha256)
                : preference.ConfirmedBitrate is null || item.ConfirmedBitrate == preference.ConfirmedBitrate);

    private static bool IsUnambiguousSelectedCopy(OfflineItem item,
        (string? RequestId, string? Codec, int? Bitrate, int? ConfirmedBitrate, string? Sha256) preference, LibraryCache library,
        string? missingFileName = null)
        => preference.Codec is null || preference.Sha256 is not null
            || !library.ByRequest[item.RequestId].Any(other => MatchesSelectedQuality(other, preference)
                && !StringComparer.OrdinalIgnoreCase.Equals(other.FileName, missingFileName)
                && !StringComparer.OrdinalIgnoreCase.Equals(item.Sha256, other.Sha256));

    private static bool MatchesLegacySelection(OfflineItem item,
        (string? RequestId, string? Codec, int? Bitrate, int? ConfirmedBitrate, string? Sha256) preference)
        => preference is { Sha256: null, Codec: not null, Bitrate: not null } && preference.RequestId == item.RequestId
            && MatchesSelectedQuality(item, preference);

    private static bool IsMissingPlaybackPath(string path)
    {
        try
        {
            using var parents = HoldStorageParents(path);
            return !parents.Complete || !UninstallPathGuard.EntryExists(path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or Win32Exception or MusicException)
        { return false; }
    }

    private OfflineItem? FindBoundCheckpointItem(TrackRow track, LibraryCache library, bool exactVersion = false, string? missingFileName = null)
    {
        var preference = track.PreferredLocalVariant;
        if (preference.RequestId is not { } request) return FindPlaylistPlaybackItem(track, library);
        if (!library.ByRequest.TryGetValue(request, out var variants)) return null;
        bool Ready(OfflineItem item) => !StringComparer.OrdinalIgnoreCase.Equals(item.FileName, missingFileName) && ValidFile(item)
            && !IsRejectedPlaybackSource(ResolvePath(item.FileName), item.Sha256, item.Bytes);
        if (preference.Codec is not null && preference.Bitrate is not null)
        {
            if (preference.Sha256 is null && track.RestoredLocalSha256 is { } expected)
            {
                var restored = variants.FirstOrDefault(item => StringComparer.OrdinalIgnoreCase.Equals(item.Sha256, expected)
                    && MatchesSelectedQuality(item, preference) && Ready(item));
                if (restored is not null) return restored;
                // A persisted expectation alone cannot establish that its old file disappeared.
                if (exactVersion && missingFileName is null) return null;
            }
            var preferred = variants.FirstOrDefault(item => MatchesSelectedQuality(item, preference) && Ready(item));
            if (exactVersion)
                return preferred is not null && IsUnambiguousSelectedCopy(preferred, preference, library, missingFileName) ? preferred : null;
            if (preferred is not null) return preferred;
        }
        return variants.FirstOrDefault(Ready);
    }

    private OfflineItem? FindPlaylistPlaybackItem(TrackRow track, LibraryCache library)
    {
        bool Ready(OfflineItem item) => ValidFile(item)
            && !IsRejectedPlaybackSource(ResolvePath(item.FileName), item.Sha256, item.Bytes);
        var exact = library.ByRequest.GetValueOrDefault(track.RequestId)?.FirstOrDefault(Ready);
        return exact ?? library.ById.GetValueOrDefault(track.Id)?.FirstOrDefault(Ready);
    }

    internal LocalAudioSource? FindCatalogPlaybackMark(TrackRow track)
    {
        if (!IsAvailable) return null;
        var library = LoadLibrary();
        var item = Find(track, library);
        return item is not null ? ResolveIndexedPlaybackSource(track, item, library, selected: false, exact: false) : null;
    }

    internal bool HasRejectedPlaybackSource(TrackRow track)
        => !_rejectedAudio.IsEmpty && Find(track) is { } item
            && IsRejectedPlaybackSource(ResolvePath(item.FileName), item.Sha256, item.Bytes);

    private LocalAudioSource? ResolveIndexedPlaybackSource(TrackRow track, OfflineItem item, LibraryCache library, bool selected, bool exact,
        bool admitLegacyExact = false)
    {
        var source = IndexedPlaybackSource(item, selected);
        if (IsRejectedPlaybackSource(source)) return exact ? null : ResolveAlternatePlaybackSources(track, source, library).FirstOrDefault();
        if (track.IsOfflineLibraryRow)
        {
            var preference = track.PreferredLocalVariant;
            var artwork = track.CoverUri is null && (admitLegacyExact
                || preference.RequestId == item.RequestId && preference.Sha256 is not null && MatchesSelectedQuality(item, preference))
                ? FindSelectedPlaybackArtwork(track, item) : null;
            if (admitLegacyExact || artwork is not null) source.RememberIndexedAdmission(admitLegacyExact, artwork);
        }
        return source;
    }

    internal IReadOnlyList<LocalAudioSource> ResolveAlternatePlaybackSources(TrackRow track, LocalAudioSource failed)
    {
        if (failed.Digest is null || !IsAvailable || LoadPlaybackLibrary() is not { } library) return [];
        return ResolveAlternatePlaybackSources(track, failed, library);
    }

    private static string? BoundEdition(TrackRow track)
        => track.IsOfflineLibraryRow && track.PreferredLocalVariant.RequestId is { } requestId
            && PlaybackStateStore.ValidRequestId(requestId, track.Id) ? requestId : null;

    private IReadOnlyList<LocalAudioSource> ResolveAlternatePlaybackSources(TrackRow track, LocalAudioSource failed, LibraryCache library)
    {
        if (!library.ById.TryGetValue(track.Id, out var variants)) return [];

        // For an unbound offline occurrence the attempted indexed file already
        // identified the local edition. Only a successful open persists it.
        var bound = BoundEdition(track) ?? (track.IsOfflineLibraryRow ? failed.Variant?.RequestId : null);
        var requestId = bound ?? track.RequestId;
        var albumHint = track.AlbumId;
        var album = PlaybackStateStore.ValidRequestId(requestId, track.Id)
            ? PlaybackStateStore.ArtworkAlbumId(track.Id, requestId, albumHint)
            : albumHint;
        var seen = new HashSet<(string Path, long? Bytes, string? Sha256)> { Signature(failed) };
        var sources = new List<LocalAudioSource>();
        // The index already orders each track's variants by quality. Keep the
        // exact service request first and never substitute another known album.
        foreach (var item in variants.OrderByDescending(item => item.RequestId == requestId))
        {
            if (bound is not null && item.RequestId != bound) continue;
            if (bound is null && album is not null && (!PlaybackStateStore.ValidRequestId(item.RequestId, track.Id)
                || PlaybackStateStore.ArtworkAlbumId(track.Id, item.RequestId, item.AlbumId) != album)) continue;
            var candidate = IndexedPlaybackSource(item, selected: false);
            if (seen.Add(Signature(candidate)) && ValidFile(item) && !IsRejectedPlaybackSource(candidate)) sources.Add(candidate);
        }
        return sources;

        static (string Path, long? Bytes, string? Sha256) Signature(LocalAudioSource source)
            => (source.Path.ToUpperInvariant(), source.Bytes, source.Digest?.ToUpperInvariant());
    }

    internal bool RememberRejectedPlaybackSource(LocalAudioSource source, LocalAudioIntegrityException error)
    {
        if (source.Digest is not { } digest || error.Revision is not { } revision) return false;
        var rejected = new RejectedAudioRevision(digest.ToUpperInvariant(), revision);
        _rejectedAudio.AddOrUpdate(source.Path, _ => ImmutableHashSet.Create(rejected), (_, previous) => previous.Add(rejected));
        return true;
    }

    private bool IsRejectedPlaybackSource(LocalAudioSource source)
        => IsRejectedPlaybackSource(source.Path, source.Digest, source.Bytes);

    private bool IsRejectedPlaybackSource(string path, string? digest, long? bytes)
    {
        if (digest is null || !_rejectedAudio.TryGetValue(path, out var rejections)) return false;
        var key = digest.ToUpperInvariant();
        if (!rejections.Any(rejected => rejected.Digest == key && rejected.Revision.Length == bytes)) return false;
        try
        {
            using var parents = HoldStorageParents(path);
            if (!parents.Complete) return false;
            using var current = UninstallPathGuard.OpenOwnedFile(path, read: true, deleteAccess: false);
            if (LocalAudioSource.TryReadRevision(current) is not { } revision) return true;
            if (revision.Length != bytes) return false;
            // Remove old revisions from the snapshot we actually observed. A
            // concurrent verifier may have added other digests or revisions.
            var stillCurrent = rejections.Except(rejections.Where(rejected => rejected.Revision != revision));
            if (stillCurrent.IsEmpty) _rejectedAudio.TryRemove(new KeyValuePair<string, ImmutableHashSet<RejectedAudioRevision>>(path, rejections));
            else if (stillCurrent.Count != rejections.Count) _rejectedAudio.TryUpdate(path, stillCurrent, rejections);
            return _rejectedAudio.TryGetValue(path, out var latest) && latest.Contains(new(key, revision));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or Win32Exception)
        {
            // Temporary access loss does not establish that a rejected file was repaired.
            return true;
        }
    }

    private LocalAudioSource IndexedPlaybackSource(OfflineItem item, bool selected)
        => new(ResolvePath(item.FileName), selected ? QualityLabel(item) : QualityLabel(item).Replace("-MP4", ""),
            item.Bytes, item.Sha256, new(item.RequestId, item.Codec, item.Bitrate, item.ConfirmedBitrate, item.Sha256));

    private LibraryCache? LoadPlaybackLibrary()
    {
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
        return library;
    }

    private bool IsIndexedPath(string path) => StringComparer.OrdinalIgnoreCase.Equals(Path.GetDirectoryName(path), DirectoryPath);
}

internal sealed record LocalAudioVariant(string RequestId, string Codec, int Bitrate, int? ConfirmedBitrate = null, string? Sha256 = null);

internal sealed record LocalAudioSource(string Path, string Quality, long? Bytes = null, string? Digest = null,
    LocalAudioVariant? Variant = null)
{
    // Admission applies to this candidate instance. Matching media remains
    // value-based for prepared playback, independently of that provenance.
    private sealed record IndexedAdmission(bool ExactLegacySelection, string? Artwork);
    private static readonly ConditionalWeakTable<LocalAudioSource, IndexedAdmission> IndexedAdmissions = new();
    internal bool AdmittedExactLegacySelection => IndexedAdmissions.TryGetValue(this, out var admission) && admission.ExactLegacySelection;
    internal string? AdmittedArtwork => IndexedAdmissions.TryGetValue(this, out var admission) ? admission.Artwork : null;
    internal void RememberIndexedAdmission(bool exactLegacySelection, string? artwork)
        => IndexedAdmissions.Add(this, new(exactLegacySelection, artwork));

    internal Task<LocalAudioLease> HoldAsync(CancellationToken token)
        => Digest is null ? AcquireAsync(token) : Task.Run(() => AcquireAsync(token), token);

    private async Task<LocalAudioLease> AcquireAsync(CancellationToken token)
    {
        UninstallPathGuard.AncestorLease? parents = null;
        IDisposable? reader = null;
        try
        {
            token.ThrowIfCancellationRequested();
            parents = UninstallPathGuard.HoldAncestors(Path);
            if (!parents.Complete) throw new FileNotFoundException();
            var file = UninstallPathGuard.OpenOwnedFile(Path, read: true, deleteAccess: false);
            reader = file;
            var length = RandomAccess.GetLength(file);
            if (length <= 0 || Bytes is { } expected && length != expected) throw new IOException();
            string? verifiedDigest = null;
            if (Digest is { } expectedDigest)
            {
                var revision = TryReadRevision(file);
                var stream = new FileStream(file, FileAccess.Read, bufferSize: 1, isAsync: false);
                reader = stream;
                verifiedDigest = Convert.ToHexString(await SHA256.HashDataAsync(stream, token).ConfigureAwait(false));
                if (!StringComparer.OrdinalIgnoreCase.Equals(verifiedDigest, expectedDigest))
                    throw new LocalAudioIntegrityException(revision == TryReadRevision(file) ? revision : null);
            }
            token.ThrowIfCancellationRequested();
            return new(Path, parents, reader, verifiedDigest);
        }
        catch (Exception error)
        {
            reader?.Dispose();
            parents?.Dispose();
            if (error is LocalAudioSourceException) throw;
            if (error is IOException or UnauthorizedAccessException or Win32Exception)
                throw new LocalAudioAccessException(error);
            throw;
        }
    }

    internal static FileSecurityPreservation.FileRevision? TryReadRevision(SafeFileHandle file)
    {
        try { return FileSecurityPreservation.GetPersistentRevision(file) is { Changed: > 0 } revision ? revision : null; }
        catch (IOException) { return null; }
    }
}

internal abstract class LocalAudioSourceException(string message, Exception? inner = null) : IOException(message, inner);

internal sealed class LocalAudioAccessException(Exception error)
    : LocalAudioSourceException("Локальный аудиофайл изменился или недоступен. Повторите воспроизведение.", error);

internal sealed class LocalAudioIntegrityException(FileSecurityPreservation.FileRevision? revision)
    : LocalAudioSourceException("Содержимое скачанного файла изменилось. Скачайте трек заново.")
{
    internal FileSecurityPreservation.FileRevision? Revision { get; } = revision;
}

internal sealed class LocalAudioLease(string path, UninstallPathGuard.AncestorLease parents, IDisposable reader, string? verifiedDigest) : IDisposable
{
    internal string Path { get; } = path;
    internal string? VerifiedDigest { get; } = verifiedDigest;

    public void Dispose()
    {
        reader.Dispose();
        parents.Dispose();
    }
}
