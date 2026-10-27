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
    private long _favoritesLikeRevision;
    private DateTimeOffset _favoritesMetadataSavedUtc;
    private Task? _favoritesCacheSaveTask;
    // Deterministic fixtures can hold the asynchronous boundaries without touching
    // a real profile, disk scheduler, music account or native input.
    internal Func<string, CancellationToken, Task<FavoritesCacheSnapshot?>>? FavoritesCacheReader { get; set; }
    internal Func<CancellationToken, Task>? FavoritesBeforeOfflineRead { get; set; }

    private void ResetFavoritesCacheAccount()
    {
        _libraryCts?.Cancel();
        _favoritesAccountUid = null;
        _favoritesCacheRead = _favoritesCacheComplete = false;
        _favoritesMetadataSavedUtc = default;
        _favoritesLikeRevision++;
        _favoritesCache.Clear();
        _favoriteTrackIds.Clear();
        _favoriteIdsKnown = false;
        if (_section == "favorites") _tracks.ReplaceAll([]);
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
                var snapshot = FavoritesCacheReader is { } read
                    ? await read(uid, cts.Token)
                    : store is null ? null : await Task.Run(() => store.Load(uid), cts.Token);
                Guard();
                _favoritesCacheRead = true;
                if (snapshot is not null && snapshot.AccountHash == FavoritesCacheStore.AccountHash(uid)
                    && likeRevision == _favoritesLikeRevision && _favoritesCache.Count == 0)
                {
                    _favoritesMetadataSavedUtc = snapshot.SavedUtc;
                    _favoritesCacheComplete = true;
                    var cached = FavoritesCacheStore.Restore(snapshot);
                    _favoritesCache.AddRange(cached);
                    _favoriteTrackIds.UnionWith(cached.Select(row => row.Id));
                    _favoriteIdsKnown = true;
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
            _favoriteTrackIds.Clear();
            _favoriteTrackIds.UnionWith(ids.Select(id => id.Id));
            _favoriteIdsKnown = true;
            UpdatePlayerLikeState();
            _diagnostics.TotalTracks = ids.Count;
            var hasRows = _favoritesCache.Count > 0;
            var reuse = !refreshMetadata && _favoritesMetadataSavedUtc > DateTimeOffset.UtcNow - FavoritesMetadataLifetime
                && _favoritesMetadataSavedUtc <= DateTimeOffset.UtcNow.AddMinutes(5);
            var metadata = reuse ? _favoritesCache.GroupBy(row => row.RequestId, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal) : [];
            var ordered = new TrackRow?[ids.Count];
            var missing = new List<int>();
            for (var i = 0; i < ids.Count; i++)
                if (metadata.TryGetValue(ids[i].RequestId, out var row)) ordered[i] = FavoriteNumber(row, i + 1);
                else missing.Add(i);

            // A populated view remains usable while the new snapshot is assembled.
            // Cold loading appends in small dispatcher slices, never Reset per page.
            if (!hasRows)
            {
                _favoritesCacheComplete = false;
                if (_section == "favorites" && _tracks.Count > 0) ShowRows([]);
            }
            _diagnostics.LoadedTracks = ordered.Count(row => row is not null);
            UpdateCount();
            for (var offset = 0; offset < missing.Count; offset += 100)
            {
                var positions = missing.Skip(offset).Take(100).ToArray();
                var rows = await _api.GetTracksAsync(session, positions.Select(index => ids[index]).ToArray(), 0, cts.Token);
                Guard();
                var page = rows.Select((row, index) => FavoriteNumber(row, positions[index] + 1)).ToArray();
                var epoch = _offlineViewEpoch;
                OfflineMark[]? marks = null;
                try
                {
                    marks = await Task.Run(async () =>
                    {
                        if (FavoritesBeforeOfflineRead is { } before) await before(cts.Token).ConfigureAwait(false);
                        return ReadOfflineMarks(page, cts.Token);
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
                if (marks is not null && epoch == _offlineViewEpoch && !_offlineMaintenance) ApplyOfflineMarks(marks);
                ApplyTrackLikeStates(page);
                for (var i = 0; i < page.Length; i++) ordered[positions[i]] = page[i];
                if (!hasRows)
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
            foreach (var row in complete)
            {
                var playing = _player.HasSource && _current?.RequestId == row.RequestId;
                if (row.IsPlaying != playing) row.IsPlaying = playing;
            }
            _favoritesCache.Clear();
            _favoritesCache.AddRange(complete);
            _favoritesCacheComplete = true;
            if (!reuse || metadata.Count == 0) _favoritesMetadataSavedUtc = DateTimeOffset.UtcNow;
            _diagnostics.TotalTracks = _diagnostics.LoadedTracks = complete.Length;
            if (_section == "favorites") ApplyFavoritesSnapshot(complete);
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
                ShowError(error, _favoritesCache.Count > 0 ? "Сохранённый список доступен. " : "");
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

    private static TrackRow FavoriteNumber(TrackRow row, int number) => row.Number == number ? row : new()
    {
        Number = number, Id = row.Id, RequestId = row.RequestId, Title = row.Title, Artist = row.Artist,
        Artists = row.Artists, Album = row.Album, AlbumId = row.AlbumId, CoverUri = row.CoverUri,
        DurationMs = row.DurationMs, Available = row.Available, LocalPath = row.LocalPath, LocalQuality = row.LocalQuality,
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
        if (_favoritesStore is null || !_favoritesCacheComplete || _favoritesAccountUid != _account?.Uid || _account is null) return;
        try
        {
            var snapshot = FavoritesCacheStore.Capture(_account.Uid, _favoritesCache, _favoritesMetadataSavedUtc);
            _favoritesCacheSaveTask = SaveAsync();
            async Task SaveAsync()
            {
                try { await _favoritesStore.SaveInBackgroundAsync(snapshot); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or System.Text.Json.JsonException)
                { if (!_closed) _diagnostics.LastErrorType = "FavoritesCacheSave:" + error.GetType().Name; }
            }
        }
        catch (Exception error) when (error is IOException or ArgumentException or System.Text.Json.JsonException)
        { _diagnostics.LastErrorType = "FavoritesCacheSave:" + error.GetType().Name; }
    }
}
