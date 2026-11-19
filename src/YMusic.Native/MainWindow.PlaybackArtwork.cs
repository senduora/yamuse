using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Windows.Threading;

namespace YMusic.Native;

public partial class MainWindow
{
    private static readonly TimeSpan RestoredArtworkRetryDelay = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan RestoredArtworkMaximumRetryDelay = TimeSpan.FromSeconds(2);
    private const int RestoredArtworkAttempts = 2;
    private TimeProvider _restoredArtworkClock = TimeProvider.System;
    private Task? _restoredArtworkTask;
    private RestoredArtworkRequest? _deferredRestoredArtwork;

    private sealed record RestoredArtworkRequest(IReadOnlyList<TrackRow> Rows, string AccountUid,
        int AccountGeneration, long QueueGeneration, CancellationToken Token);

    private bool OwnsRestoredArtwork(RestoredArtworkRequest request) => !SessionIsExiting && !request.Token.IsCancellationRequested
        && request.AccountGeneration == _accountGeneration && request.QueueGeneration == _playbackQueue.ReplacementVersion
        && request.Token == _accountCts.Token && !_playbackStateInvalidated && _account?.Uid == request.AccountUid
        && _playbackStateAccountUid == request.AccountUid && _session is not null;

    private void RequestRestoredPlaybackArtwork(RestoredArtworkRequest request)
    {
        if (!OwnsRestoredArtwork(request)) return;
        _deferredRestoredArtwork = null;
        if (!request.Rows.Any(row => row.CoverUri is null)) return;
        if (SessionWriteAdmissionClosed) _deferredRestoredArtwork = request;
        else _restoredArtworkTask = RestorePlaybackArtworkAsync(request);
    }

    private void ResumeDeferredRestoredArtwork()
    {
        if (!PlaybackRestoreAdmissionPaused || SessionIsExiting)
        {
            var admission = _playbackRestoreAdmission;
            _playbackRestoreAdmission = null;
            admission?.TrySetResult();
        }
        if (_deferredRestoredArtwork is not { } request) return;
        if (!OwnsRestoredArtwork(request)) { _deferredRestoredArtwork = null; return; }
        if (SessionWriteAdmissionClosed) return;
        _deferredRestoredArtwork = null;
        _restoredArtworkTask = RestorePlaybackArtworkAsync(request);
    }

    private async Task RestorePlaybackArtworkAsync(RestoredArtworkRequest request)
    {
        var cancellationToken = request.Token;
        bool OwnsRestore() => OwnsRestoredArtwork(request);
        if (!OwnsRestore()) return;
        var references = request.Rows.Where(row => row.CoverUri is null)
            .OrderByDescending(row => ReferenceEquals(row, _current))
            .Select(RestoredArtworkReference).OfType<LikedId>().Distinct()
            .ToArray();

        var anyChanged = false;
        var requestedTracks = new HashSet<LikedId>();
        var skippedTrackPages = new List<(LikedId[] Page, int Attempts)>();
        var unresolved = new List<LikedId>();
        var resolved = new Dictionary<LikedId, string>();
        Dictionary<LikedId, TrackRow[]> liveByReference = [];
        var liveVersion = -1L;
        var trackPacing = new RestoredArtworkBackoff(_restoredArtworkClock);
        var albumPacing = new RestoredArtworkBackoff(_restoredArtworkClock);
        try
        {
            await ReadTrackPagesAsync(references.Chunk(100).Select(page => (page, RestoredArtworkAttempts)), skippedTrackPages);
            // Undo can restore an original occurrence while its page is in flight.
            // Recheck unsent references once, using the same route cooldown.
            await RevisitTrackPagesAsync(keepSkipped: true);

            if (unresolved.Count == 0) return;
            // Complete the paged requests before fetching parents. A slow album
            // then cannot hold up coverage already available from /tracks.
            var originalAlbumReferences = references.Where(reference => reference.AlbumId is not null)
                .ToLookup(reference => reference.AlbumId!, StringComparer.Ordinal);
            var skippedAlbums = new List<(IGrouping<string, LikedId> Album, int Attempts)>();
            var albums = unresolved.GroupBy(reference => reference.AlbumId!, StringComparer.Ordinal)
                .Select(album => (Album: album, Attempts: RestoredArtworkAttempts));
            await ReadAlbumsAsync(albums, originalAlbumReferences, skippedAlbums);
            // Undo may return the only living member of an earlier album while
            // another is in flight. Revisit skipped groups once with their remaining attempts.
            await ReadAlbumsAsync(skippedAlbums, originalAlbumReferences);
            // An occurrence may have returned while a different album was in flight.
            await RevisitTrackPagesAsync(keepSkipped: false);
        }
        catch (OperationCanceledException) when (!OwnsRestore()) { }
        catch (MusicException error) when (error.Authentication)
        { RecordFailure(error); }
        finally
        {
            // A later page may finish after an already resolved reference is
            // admitted again from a view that did not have the enriched row.
            if (OwnsRestore()) await ApplyArtworkAsync(resolved.Keys);
            if (anyChanged && OwnsRestore()) await SaveRestoredArtworkAsync();
            // Only the pending Undo keeps detached occurrences beyond this task.
            // Apply their proven metadata when the same tokens are put back.
            if (resolved.Count > 0 && OwnsRestore())
                _playbackQueue.RememberRemovalArtwork(row => RestoredArtworkReference(row) is { } reference
                    ? resolved.GetValueOrDefault(reference) : null);
        }

        Dictionary<LikedId, TrackRow[]> LiveRows()
        {
            if (liveVersion == _playbackQueue.ItemsVersion) return liveByReference;
            liveVersion = _playbackQueue.ItemsVersion;
            liveByReference = _playbackQueue.Items.Select(row => (Row: row, Reference: RestoredArtworkReference(row)))
                .Where(item => item.Reference is not null).GroupBy(item => item.Reference!)
                .ToDictionary(group => group.Key, group => group.Select(item => item.Row).ToArray());
            return liveByReference;
        }

        LikedId[] MissingReferences(IEnumerable<LikedId> candidates)
        {
            var live = LiveRows();
            return candidates.Where(reference => live.TryGetValue(reference, out var rows) && rows.Any(row => row.CoverUri is null)).ToArray();
        }

        async Task ReadAlbumsAsync(IEnumerable<(IGrouping<string, LikedId> Album, int Attempts)> albums,
            ILookup<string, LikedId> originalReferences, List<(IGrouping<string, LikedId> Album, int Attempts)>? skipped = null)
        {
            foreach (var (album, attempts) in albums)
            {
                if (!OwnsRestore() || albumPacing.ExceedsBudget) return;
                if (MissingReferences(album).Length == 0) { skipped?.Add((album, attempts)); continue; }
                var details = await ReadMetadataAsync<AlbumDetails>(albumPacing, async (session, remaining) =>
                {
                    if (MissingReferences(album).Length == 0)
                    {
                        skipped?.Add((album, remaining));
                        return null;
                    }
                    return await _api.GetAlbumDetailsAsync(session, album.Key, cancellationToken);
                }, attempts: attempts);
                if (!OwnsRestore()) return;
                if (details is null || details.Id != album.Key) continue;
                var parentCover = ArtworkUri.Metadata(details.CoverUri);
                var members = details.Tracks.Where(row => row.AlbumId == album.Key)
                    .GroupBy(row => row.RequestId, StringComparer.Ordinal)
                    .ToDictionary(group => group.Key, group =>
                    {
                        var specific = group.Select(row => ArtworkUri.Metadata(row.CoverUri))
                            .Where(cover => cover is not null && cover != parentCover).Distinct(StringComparer.Ordinal).Take(2).ToArray();
                        return specific.Length == 1 ? specific[0] : parentCover;
                    }, StringComparer.Ordinal);
                // The confirmed album can also supply members from a failed page
                // or occurrences that returned during the request.
                var candidates = originalReferences[album.Key];
                foreach (var reference in candidates)
                    if (members.GetValueOrDefault(reference.RequestId) is { } cover) resolved.TryAdd(reference, cover);
                await ApplyArtworkAsync(candidates);
            }
        }

        async Task RevisitTrackPagesAsync(bool keepSkipped)
        {
            if (!OwnsRestore() || trackPacing.ExceedsBudget) return;
            var pending = skippedTrackPages.ToArray();
            skippedTrackPages.Clear();
            var reserved = pending.SelectMany(batch => batch.Page).ToHashSet();
            var unsent = MissingReferences(references.Where(reference => !reserved.Contains(reference) && !requestedTracks.Contains(reference)));
            await ReadTrackPagesAsync(pending.Concat(unsent.Chunk(100).Select(page => (page, RestoredArtworkAttempts))),
                keepSkipped ? skippedTrackPages : null);
        }

        async Task ReadTrackPagesAsync(IEnumerable<(LikedId[] Page, int Attempts)> pages, List<(LikedId[] Page, int Attempts)>? skipped)
        {
            foreach (var (page, attempts) in pages)
            {
                if (!OwnsRestore() || trackPacing.ExceedsBudget) return;
                if (MissingTrackReferences().Length == 0) { skipped?.Add((page, attempts)); continue; }
                LikedId[] sent = [];
                var albumCandidates = new HashSet<LikedId>();
                var retryThroughAlbum = true;
                var metadata = await ReadMetadataAsync<IReadOnlyList<TrackRow>>(trackPacing, async (session, remaining) =>
                {
                    sent = MissingTrackReferences();
                    if (sent.Length == 0)
                    {
                        skipped?.Add((page, remaining));
                        return null;
                    }
                    requestedTracks.UnionWith(sent);
                    try { return await _api.GetTracksAsync(session, sent, 0, cancellationToken); }
                    catch (MusicException error) when (AllowsAlbumFallback(error))
                    {
                        albumCandidates.UnionWith(sent.Where(reference => reference.AlbumId is not null));
                        throw;
                    }
                }, failure => retryThroughAlbum = AllowsAlbumFallback(failure), attempts: attempts);
                if (!OwnsRestore()) return;
                if (metadata is null)
                {
                    if (retryThroughAlbum) unresolved.AddRange(albumCandidates);
                    continue;
                }
                // A returned occurrence may have been absent in the successful retry.
                // Keep the exact parent route earned by its earlier failed request.
                unresolved.AddRange(albumCandidates.Except(sent));
                var byRequest = metadata.GroupBy(row => row.RequestId, StringComparer.Ordinal)
                    .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
                foreach (var reference in sent)
                {
                    if (byRequest.TryGetValue(reference.RequestId, out var source)
                        && (reference.AlbumId is null || source.AlbumId == reference.AlbumId)
                        && ArtworkUri.Metadata(source.CoverUri) is { } cover)
                        resolved.Add(reference, cover);
                    else if (reference.AlbumId is not null) unresolved.Add(reference);
                }
                await ApplyArtworkAsync(page);

                LikedId[] MissingTrackReferences() => MissingReferences(page.Where(reference => !resolved.ContainsKey(reference)));
            }
        }

        static bool AllowsAlbumFallback(Exception failure) => IsRestoredArtworkRetryable(failure)
            && failure is MusicException { ApiStatusCode: not HttpStatusCode.TooManyRequests, ApiRetryAfter: null };

        async Task<T?> ReadMetadataAsync<T>(RestoredArtworkBackoff pacing, Func<Session, int, Task<T?>> read,
            Action<Exception>? onFailure = null, int attempts = RestoredArtworkAttempts) where T : class
        {
            while (attempts > 0 && OwnsRestore() && !pacing.ExceedsBudget)
            {
                try
                {
                    if (!await WaitForPlaybackRestoreAdmissionAsync(cancellationToken) || !OwnsRestore()) return null;
                    await pacing.WaitAsync(cancellationToken);
                    if (!await WaitForPlaybackRestoreAdmissionAsync(cancellationToken) || !OwnsRestore()) return null;
                    Session session;
                    try { session = await EnsureSessionAsync(cancellationToken); }
                    catch (OperationCanceledException error) when (error.CancellationToken == cancellationToken
                        && OwnsRestore() && PlaybackRestoreAdmissionPaused)
                    { continue; }
                    // Closing can begin while session admission is in flight.
                    if (!OwnsRestore()) return null;
                    if (PlaybackRestoreAdmissionPaused) continue;
                    return SessionWriteAdmissionClosed ? null : await read(session, attempts);
                }
                catch (Exception error) when (error is (MusicException { Authentication: false } or HttpRequestException
                    or JsonException or IOException or UnauthorizedAccessException or OperationCanceledException))
                {
                    if (!OwnsRestore()) return null;
                    attempts--;
                    var retryable = IsRestoredArtworkRetryable(error);
                    // Keep server pacing even after this page uses its last attempt.
                    if (retryable && error is MusicException { ApiRetryAfter: { } advertised }) pacing.DelayAtLeast(advertised);
                    if (attempts > 0 && retryable && !pacing.ExceedsBudget) pacing.DelayAtLeast(RestoredArtworkRetryDelay);
                    else { RecordFailure(error); onFailure?.Invoke(error); return null; }
                }
            }
            return null;
        }

        async Task ApplyArtworkAsync(IEnumerable<LikedId> candidates)
        {
            if (resolved.Count == 0) return;
            var currentChanged = false;
            var visited = 0;
            while (OwnsRestore())
            {
                var live = LiveRows();
                var version = liveVersion;
                foreach (var reference in candidates)
                {
                    if (!OwnsRestore()) return;
                    if (version != _playbackQueue.ItemsVersion) break;
                    if (!resolved.TryGetValue(reference, out var cover) || !live.TryGetValue(reference, out var rows)) continue;
                    foreach (var row in rows)
                    {
                        if (!OwnsRestore()) return;
                        if (version != _playbackQueue.ItemsVersion) break;
                        if (!row.SetMissingArtwork(cover)) continue;
                        anyChanged = true;
                        currentChanged |= ReferenceEquals(row, _current);
                        if (++visited % 16 != 0) continue;
                        if (currentChanged && OwnsRestore()) { ApplyRestoredPlaybackArtwork(); currentChanged = false; }
                        await Dispatcher.Yield(DispatcherPriority.Background);
                    }
                }
                if (version == _playbackQueue.ItemsVersion) break;
            }
            if (currentChanged && OwnsRestore()) ApplyRestoredPlaybackArtwork();
        }

        void RecordFailure(Exception error)
        {
            if (OwnsRestore()) _diagnostics.LastErrorType = "PlaybackArtwork:" + error.GetType().Name;
        }

        async Task SaveRestoredArtworkAsync()
        {
            // An edit can have saved its queue just before the artwork arrived.
            // Serialize behind that snapshot and write even if playback is paused.
            try
            {
                while (OwnsRestore() && await WaitForPlaybackRestoreAdmissionAsync(cancellationToken) && OwnsRestore())
                {
                    await ApplyArtworkAsync(resolved.Keys);
                    if (!OwnsRestore()) return;
                    if (PlaybackRestoreAdmissionPaused) continue;
                    if (_playbackStateWriteTask is { IsCompleted: false } pending)
                    {
                        await pending.WaitAsync(cancellationToken);
                        continue;
                    }
                    QueuePlaybackStateSave(force: true);
                    return;
                }
            }
            catch (OperationCanceledException) when (!OwnsRestore()) { }
        }
    }

    private static LikedId? RestoredArtworkReference(TrackRow row)
    {
        var reference = TryTrackReference(row);
        return reference is { AlbumId: null } && !string.IsNullOrEmpty(row.AlbumId)
            ? reference with { AlbumId = row.AlbumId } : reference;
    }

    private static bool IsRestoredArtworkRetryable(Exception error)
        => error is MusicException { ApiStatusCode: HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests
            or HttpStatusCode.InternalServerError or HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout }
            or HttpRequestException { StatusCode: null, HttpRequestError: HttpRequestError.Unknown or HttpRequestError.ConnectionError
                or HttpRequestError.NameResolutionError or HttpRequestError.ResponseEnded }
            or IOException or OperationCanceledException;

    private sealed class RestoredArtworkBackoff(TimeProvider clock)
    {
        private long _receivedAt = clock.GetTimestamp();
        private TimeSpan _delay;
        private TimeSpan Remaining => _delay - clock.GetElapsedTime(_receivedAt);
        internal bool ExceedsBudget => _delay > RestoredArtworkMaximumRetryDelay;

        internal void DelayAtLeast(TimeSpan delay)
        {
            if (delay <= TimeSpan.Zero || delay <= Remaining) return;
            _receivedAt = clock.GetTimestamp();
            _delay = delay;
        }

        internal async Task WaitAsync(CancellationToken cancellationToken)
        {
            for (var remaining = Remaining; remaining > TimeSpan.Zero; remaining = Remaining)
                await Task.Delay(remaining, clock, cancellationToken);
        }
    }

    private void ApplyRestoredPlaybackArtwork()
    {
        if (_current is not { } current || _closed) return;
        Artwork.SetSourceUri(PlayerCover, current.CoverUri);
        SetLyricsArtwork(current);
        // Refresh the expanded player without rebuilding the queue or changing
        // its selected occurrence. Queue images observe CoverUri notifications.
        UpdateControls();
    }
}
