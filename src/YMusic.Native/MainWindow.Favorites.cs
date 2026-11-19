using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace YMusic.Native;

public partial class MainWindow
{
    private static readonly TimeSpan FavoritesMetadataLifetime = TimeSpan.FromHours(24);
    private FavoritesCacheStore? _favoritesStore;
    private string? _favoritesAccountUid;
    private bool _favoritesCacheRead, _favoritesCacheComplete;
    private int? _verifiedFavoritesGeneration;
    private long _favoritesLikeRevision;
    private DateTimeOffset _favoritesMetadataSavedUtc;
    private Task? _favoritesCacheSaveTask;
    private DispatcherOperation? _favoritesOpenRefresh;
    // Deterministic fixtures can hold the asynchronous boundaries without touching
    // a real profile, disk scheduler, music account or native input.
    internal Func<string, CancellationToken, Task<FavoritesCacheSnapshot?>>? FavoritesCacheReader { get; set; }
    internal Func<CancellationToken, Task>? FavoritesBeforeOfflineRead { get; set; }

    private void ResetFavoritesCacheAccount()
    {
        _libraryCts?.Cancel();
        _favoritesOpenRefresh?.Abort();
        _favoritesOpenRefresh = null;
        _favoritesAccountUid = null;
        _favoritesCacheRead = _favoritesCacheComplete = false;
        _favoritesSnapshotReady = false;
        _verifiedFavoritesGeneration = null;
        _favoritesMetadataSavedUtc = default;
        _favoritesLikeRevision++;
        _favoritesCache.Clear();
        _favoriteTrackIds.Clear();
        _favoriteIdsKnown = false;
        if (_section == "favorites") _tracks.ReplaceAll([]);
    }

    private void RefreshFavoritesOnOpen()
    {
        if (_account is not { } account || _favoritesOpenRefresh is not null || !CanRefresh()) return;
        var generation = _accountGeneration;
        _favoritesOpenRefresh = Dispatcher.InvokeAsync(() =>
        {
            _favoritesOpenRefresh = null;
            if (_account?.Uid == account.Uid && _accountGeneration == generation && CanRefresh()) _ = RefreshLibraryAsync();
        }, DispatcherPriority.Background);

        bool CanRefresh() => _section == "favorites" && _session is not null && !_demo && !SessionWriteAdmissionClosed
            && !DownloadQueueRestorePending && _libraryCts is not { IsCancellationRequested: false };
    }

    internal async Task RefreshLibraryAsync(bool refreshMetadata = false)
    {
        if (_account is null || _closed) return;
        // Existing confirmed in-memory likes belong to the active account even
        // before this optional disk-cache coordinator has been initialized.
        // Activate/sign-out already reset them at the actual account boundary.
        if (_favoritesAccountUid is null) _favoritesAccountUid = _account.Uid;
        else if (_favoritesAccountUid != _account.Uid)
        {
            ResetFavoritesCacheAccount();
            _favoritesAccountUid = _account.Uid;
        }
        _libraryCts?.Cancel();
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, _accountCts.Token);
        _libraryCts = cts;
        BeginFavoritesRefresh();
        var uid = _account.Uid;
        var generation = _accountGeneration;
        var removed = _libraryRemovedLikes = new HashSet<string>(StringComparer.Ordinal);
        var clock = Stopwatch.StartNew();
        bool Current() => !_closed && !cts.IsCancellationRequested && ReferenceEquals(_libraryCts, cts)
            && _account?.Uid == uid && _accountGeneration == generation;
        void Guard() { cts.Token.ThrowIfCancellationRequested(); if (!Current()) throw new OperationCanceledException(cts.Token); }
        Busy(true);
        RefreshButton.IsEnabled = false;
        _diagnostics.Stage = _player.IsPlaying ? "playing_library_loading" : "library_loading";
        try
        {
            var store = _favoritesStore ??= _restore && !_demo ? new FavoritesCacheStore(_diagnosticsDirectory) : null;
            if (!_favoritesCacheRead)
            {
                var likeRevision = _favoritesLikeRevision;
                var (status, snapshot) = FavoritesCacheReader is { } read
                    ? (FavoritesCacheReadStatus.Loaded, await read(uid, cts.Token))
                    : store is null ? (FavoritesCacheReadStatus.Missing, null)
                    : await Task.Run(() => store.Read(uid), cts.Token);
                Guard();
                _favoritesCacheRead = status != FavoritesCacheReadStatus.Unavailable;
                if (snapshot is not null && snapshot.AccountHash == FavoritesCacheStore.AccountHash(uid)
                    && likeRevision == _favoritesLikeRevision && !_favoritesCacheComplete && _favoritesCache.Count == 0)
                {
                    _favoritesMetadataSavedUtc = snapshot.SavedUtc;
                    _favoritesCacheComplete = true;
                    var cached = FavoritesCacheStore.Restore(snapshot);
                    _favoritesCache.AddRange(cached);
                    if (_verifiedFavoritesGeneration != generation)
                    {
                        _favoriteTrackIds.UnionWith(cached.Select(row => row.Id));
                        _favoriteIdsKnown = true;
                    }
                    _diagnostics.TotalTracks = _diagnostics.LoadedTracks = cached.Length;
                    if (_section == "favorites")
                    {
                        ShowRows(cached);
                        SetStatus($"Избранное из кэша · {cached.Length:N0} треков · проверяем изменения…");
                    }
                    UpdatePlayerLikeState();
                    _diagnostics.Timing("favorites_cache_ms", clock.Elapsed.TotalMilliseconds);
                }
            }

            Guard();
            var session = await EnsureSessionAsync(cts.Token);
            Guard();
            if (_section == "favorites" && _favoritesCache.Count == 0) SetStatus("Получаем список избранного…");
            var ids = await _api.GetLikedIdsAsync(session, uid, cts.Token);
            Guard();
            ids = ids.Where(id => !removed.Contains(id.Id)).ToArray();
            ApplyVerifiedFavoriteIds(ids);
            _diagnostics.TotalTracks = ids.Count;
            var hasSnapshot = _favoritesCacheComplete;
            var reuse = !refreshMetadata && _favoritesMetadataSavedUtc > DateTimeOffset.UtcNow - FavoritesMetadataLifetime
                && _favoritesMetadataSavedUtc <= DateTimeOffset.UtcNow.AddMinutes(5);
            var metadata = hasSnapshot ? _favoritesCache.GroupBy(row => row.RequestId, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal) : [];
            var ordered = new TrackRow?[ids.Count];
            var missing = new List<int>();
            for (var i = 0; i < ids.Count; i++)
                if (reuse && metadata.TryGetValue(ids[i].RequestId, out var row) && row.HasTrackMetadata)
                    ordered[i] = FavoriteNumber(row, i + 1);
                else missing.Add(i);

            // A populated view remains usable while the new snapshot is assembled.
            // Cold loading appends in small dispatcher slices, never Reset per page.
            if (!hasSnapshot)
            {
                _favoritesCache.Clear();
                if (_section == "favorites" && _tracks.Count > 0) ShowRows([]);
            }
            _diagnostics.LoadedTracks = ordered.Count(row => row is not null);
            UpdateCount();
            for (var offset = 0; offset < missing.Count; offset += 100)
            {
                var positions = missing.Skip(offset).Take(100).ToArray();
                var references = positions.Select(index => ids[index]).ToArray();
                var rows = await _api.GetTracksAsync(session, references, 0, cts.Token);
                Guard();
                if (!rows.Select(row => row.RequestId).SequenceEqual(references.Select(reference => reference.RequestId)))
                    throw new MusicException("Ответ не соответствует запрошенной странице избранного.");
                var page = new TrackRow[rows.Count];
                for (var i = 0; i < rows.Count; i++)
                {
                    var row = rows[i];
                    var number = positions[i] + 1;
                    if (!row.HasTrackMetadata && metadata.TryGetValue(row.RequestId, out var cached))
                        page[i] = FavoriteNumber(cached, number, available: false, hasTrackMetadata: false);
                    else page[i] = FavoriteNumber(row, number);
                }
                var epoch = _offlineViewEpoch;
                var checks = CurrentOfflineDownloadChecks;
                OfflineMark[]? marks = null;
                try
                {
                    marks = await Task.Run(async () =>
                    {
                        if (FavoritesBeforeOfflineRead is { } before) await before(cts.Token).ConfigureAwait(false);
                        return ReadOfflineMarks(page, cts.Token, checks);
                    }, cts.Token);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or MusicException)
                {
                    Guard();
                    // Offline badges are optional. A damaged local index must
                    // not discard successfully received online track metadata.
                    _diagnostics.LastErrorType = "FavoritesOfflineMarks:" + error.GetType().Name;
                }
                Guard();
                if (marks is not null && epoch == _offlineViewEpoch && !_offlineMaintenance
                    && ReferenceEquals(checks, CurrentOfflineDownloadChecks)) ApplyOfflineMarks(marks);
                ApplyTrackLikeStates(page);
                for (var i = 0; i < page.Length; i++) ordered[positions[i]] = page[i];
                if (!hasSnapshot)
                {
                    for (var first = 0; first < page.Length; first += 16)
                    {
                        await System.Windows.Threading.Dispatcher.Yield(DispatcherPriority.Background);
                        Guard();
                        _updatingTrackRows = true;
                        try
                        {
                            foreach (var row in page.Skip(first).Take(16).Where(row => !removed.Contains(row.Id)))
                            {
                                row.IsPlaying = _player.HasSource && _current?.RequestId == row.RequestId;
                                _favoritesCache.Add(row);
                                if (_section == "favorites") _tracks.Add(row);
                            }
                        }
                        finally { _updatingTrackRows = false; }
                    }
                }
                _diagnostics.LoadedTracks = ordered.Count(row => row is not null && !removed.Contains(row.Id));
                if (offset == 0) _diagnostics.Timing("favorites_first_batch_ms", clock.Elapsed.TotalMilliseconds);
                if (_section == "favorites") SetStatus($"Обновляем избранное · {_diagnostics.LoadedTracks:N0} из {ids.Count:N0} треков…");
                UpdateCount();
            }
            Guard();
            var complete = ordered.Where(row => row is not null && !removed.Contains(row.Id))
                .Select((row, index) => FavoriteNumber(row!, index + 1)).ToArray();
            if (!reuse || metadata.Count == 0) _favoritesMetadataSavedUtc = DateTimeOffset.UtcNow;
            PublishFavoritesSnapshot(complete);
            PersistFavoritesCache();
            _diagnostics.Timing("favorites_all_ms", clock.Elapsed.TotalMilliseconds);
            _diagnostics.Stage = _player.IsPlaying ? "playing" : "library_ready";
            if (_section == "favorites") SetStatus($"Избранное обновлено · {complete.Length:N0} треков");
            UpdateCount();
            if (_section == "favorites") PlaybackContextViewShown();
            ScheduleAutoFavoritesDownloads();
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested || !Current()) { }
        catch (Exception error)
        {
            if (Current())
            {
                _diagnostics.Stage = "library_partial_or_error";
                if (!_favoritesCacheComplete)
                {
                    _favoritesCache.Clear();
                    _diagnostics.LoadedTracks = 0;
                    if (_section == "favorites") ShowRows([]);
                }
                if (_section == "favorites") ShowError(error, _favoritesCache.Count > 0 ? "Сохранённый список доступен. " : "");
                else _diagnostics.LastErrorType = error.GetType().Name;
            }
        }
        finally
        {
            if (ReferenceEquals(_libraryCts, cts))
            { _libraryCts = null; _libraryRemovedLikes = null; RefreshButton.IsEnabled = true; }
            cts.Dispose();
            Busy(false);
        }
    }

    private void ApplyVerifiedFavoriteIds(IReadOnlyList<LikedId> ids)
    {
        var order = ids.Select(reference => reference.RequestId);
        if (_pendingAutoFavorites is { } pending && !pending.Tracks.Select(row => row.RequestId).SequenceEqual(order))
            _pendingAutoFavorites = null;
        if (_favoritesSnapshotReady && (!_favoritesCacheComplete
            || !_favoritesCache.Select(row => row.RequestId).SequenceEqual(order)))
        {
            _favoritesSnapshotReady = false;
            UpdatePlaylistDownloadedStatus();
        }
        _favoriteTrackIds.Clear();
        _favoriteTrackIds.UnionWith(ids.Select(reference => reference.Id));
        _verifiedFavoritesGeneration = _accountGeneration;
        foreach (var id in _pendingConfirmedFavorites.Keys.Where(id => !_favoriteTrackIds.Contains(id)).ToArray())
            _pendingConfirmedFavorites.Remove(id);
        _favoriteIdsKnown = true;
        ReconcileDownloadQueueWithVerifiedFavorites();
        UpdatePlayerLikeState();
    }

    private void ReconcileDownloadQueueWithVerifiedFavorites()
    {
        if (_verifiedFavoritesGeneration == _accountGeneration && _favoritesAccountUid is { } uid && uid == _account?.Uid)
            ReconcileDownloadQueueFavorites(uid, _favoriteTrackIds);
    }

    private bool TryApplyConfirmedFavoriteSnapshot(TrackRow? likedTrack, IReadOnlyList<LikedId> readback)
    {
        if (_favoritesAccountUid != _account?.Uid || !_favoritesCacheComplete && likedTrack is not null) return false;

        var metadata = new Dictionary<string, TrackRow>(StringComparer.Ordinal);
        foreach (var row in _favoritesCache) metadata.TryAdd(row.RequestId, row);

        var rows = new TrackRow[readback.Count];
        for (var i = 0; i < readback.Count; i++)
        {
            var reference = readback[i];
            if (metadata.TryGetValue(reference.RequestId, out var row))
            {
                rows[i] = FavoriteNumber(row, i + 1);
                continue;
            }
            // Use the source row only if it carries online metadata for this context.
            if (likedTrack is null || reference.Id != likedTrack.Id || reference.RequestId != likedTrack.RequestId
                && (reference.AlbumId is null || reference.AlbumId != likedTrack.AlbumId)) return false;
            rows[i] = FavoriteNumber(likedTrack, i + 1, reference.RequestId);
        }

        if (_favoritesMetadataSavedUtc == default) _favoritesMetadataSavedUtc = DateTimeOffset.UtcNow;
        PublishFavoritesSnapshot(rows);
        _favoritesSnapshotReady = true;
        UpdatePlaylistDownloadedStatus();
        return true;
    }

    private void PublishFavoritesSnapshot(TrackRow[] rows)
    {
        foreach (var row in rows) row.IsPlaying = _player.HasSource && _current?.RequestId == row.RequestId;
        _favoritesCache.Clear();
        _favoritesCache.AddRange(rows);
        _favoritesCacheRead = true;
        _favoritesCacheComplete = true;
        _diagnostics.TotalTracks = _diagnostics.LoadedTracks = rows.Length;
        if (_section == "favorites") ApplyFavoritesSnapshot(rows);
    }

    private static TrackRow FavoriteNumber(TrackRow row, int number, string? requestId = null, bool? available = null, bool? hasTrackMetadata = null)
        => row.Number == number && requestId is null && (available is null || available == row.Available)
            && (hasTrackMetadata is null || hasTrackMetadata == row.HasTrackMetadata) ? row : new()
    {
        Number = number, Id = row.Id, RequestId = requestId ?? row.RequestId, Title = row.Title, Artist = row.Artist,
        Artists = row.Artists, PrimaryArtistName = row.PrimaryArtistName, Album = row.Album, AlbumId = row.AlbumId, CoverUri = row.CoverUri,
        DurationMs = row.DurationMs, Available = available ?? row.Available, HasTrackMetadata = hasTrackMetadata ?? row.HasTrackMetadata,
        LocalPath = row.LocalPath, LocalQuality = row.LocalQuality, IsOfflineLibraryRow = row.IsOfflineLibraryRow,
        DownloadedPath = row.DownloadedPath, DownloadedQuality = row.DownloadedQuality,
        IsLiked = row.IsLiked, LikeStateKnown = row.LikeStateKnown, CanToggleLike = row.CanToggleLike, IsPlaying = row.IsPlaying
    };

    private void ApplyFavoritesSnapshot(TrackRow[] rows)
    {
        if (_tracks.Count == rows.Length && _tracks.Zip(rows).All(pair => ReferenceEquals(pair.First, pair.Second))) return;
        var selected = TracksList.SelectedItems.Cast<TrackRow>().Select(row => row.RequestId).ToHashSet(StringComparer.Ordinal);
        var scroll = FavoritesScroll(TracksList);
        var offset = scroll?.VerticalOffset ?? 0;
        _updatingTrackRows = true;
        try
        {
            using (_view.DeferRefresh()) _tracks.ReplaceAll(rows);
            foreach (var row in rows) if (selected.Contains(row.RequestId) && _view.Contains(row)) TracksList.SelectedItems.Add(row);
            TracksList.UpdateLayout();
            scroll?.ScrollToVerticalOffset(offset);
        }
        finally { _updatingTrackRows = false; }
        // Renumbered cached rows are new references. An older offline scan can
        // only update its original objects, so validate the published references.
        ApplyTrackLikeStates(rows);
        if (rows.Length > 0) QueueOfflineMarks(rows);
    }

    private static ScrollViewer? FavoritesScroll(DependencyObject parent)
    {
        if (parent is ScrollViewer scroll) return scroll;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            if (FavoritesScroll(VisualTreeHelper.GetChild(parent, i)) is { } found) return found;
        return null;
    }

    private void PersistFavoritesCache()
    {
        if (SessionWriteAdmissionClosed || _favoritesStore is null
            || !_favoritesCacheComplete || _favoritesAccountUid != _account?.Uid || _account is null) return;
        try
        {
            var uid = _account.Uid;
            var generation = _accountGeneration;
            var store = _favoritesStore;
            var snapshot = FavoritesCacheStore.Capture(uid, _favoritesCache, _favoritesMetadataSavedUtc);
            var save = SaveAsync();
            _favoritesCacheSaveTask = _favoritesCacheSaveTask is { IsCompleted: false } previous
                ? Task.WhenAll(previous, save) : save;
            async Task SaveAsync()
            {
                try { await store.SaveInBackgroundAsync(snapshot); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or System.Text.Json.JsonException)
                { if (!_closed && _account?.Uid == uid && _accountGeneration == generation) _diagnostics.LastErrorType = "FavoritesCacheSave:" + error.GetType().Name; }
            }
        }
        catch (Exception error) when (error is IOException or ArgumentException or System.Text.Json.JsonException)
        { _diagnostics.LastErrorType = "FavoritesCacheSave:" + error.GetType().Name; }
    }

    private Task DrainFavoritesCacheWritesAsync() => _favoritesCacheSaveTask ?? Task.CompletedTask;
}
