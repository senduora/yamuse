using System.Windows;
using System.Windows.Controls;
using OfflineCopyCheck = YMusic.Native.OfflineLibrary.OfflineCopyCheck;
using OfflineCopyChecks = YMusic.Native.OfflineLibrary.OfflineCopyChecks;

namespace YMusic.Native;

public partial class MainWindow
{
    private void ApplyOfflineStates(IEnumerable<TrackRow> tracks)
    {
        var snapshot = tracks as TrackRow[] ?? tracks.ToArray();
        ApplyTrackLikeStates(snapshot);
        if (snapshot.Length > BackgroundOfflineThreshold) { QueueOfflineMarks(snapshot); return; }
        try
        {
            ApplyOfflineMarks(ReadOfflineMarks(snapshot, _lifetime.Token, CurrentOfflineDownloadChecks));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or MusicException)
        { _diagnostics.LastErrorType = "OfflineIndex:" + ex.GetType().Name; SetStatus("Не удалось прочитать отметки скачанного. Аудиофайлы сохранены.", true); }
    }
    private static string OfflineQuality(OfflineItem item) => OfflineLibrary.QualityLabel(item).Replace("-MP4", "");
    private void RefreshDownloadedMarks()
    {
        InvalidateOfflineViewState();
        ApplyOfflineStates(KnownTrackRows());
        UpdateControls();
        UpdatePlaylistDownloadedStatus();
    }
    private Task? _downloadTask;
    private CancellationTokenSource? _appendedFavoritesCts;
    private bool _downloadIsAutomatic;
    private bool _offlineMaintenance;
    private bool _clearOfflineConfirmationPending;
    private bool _favoritesSnapshotReady;
    private PlaylistDetails? _pendingAutoFavorites;
    private readonly Dictionary<string, TrackRow> _pendingConfirmedFavorites = new(StringComparer.Ordinal);
    private readonly HashSet<string> _deletedUntilFavoritesRefresh = new(StringComparer.Ordinal);
    private readonly HashSet<string> _deletedUntilConfirmedLike = new(StringComparer.Ordinal);
    private sealed record AutomaticFavoritesAfterRemoval(long AdmissionRevision, int AccountGeneration, string AccountUid,
        PlaylistDetails? VerifiedSnapshot = null);
    private AutomaticFavoritesAfterRemoval? _autoFavoritesRemovalRestart;
    private PlaylistDetails? _singleRemovalVerifiedFavorites;
    private (long AdmissionRevision, int AccountGeneration, string AccountUid)? _singleRemovalFavoritesAdmission;
    private (long AdmissionRevision, int AccountGeneration, string AccountUid)? _clearFavoritesAdmission;
    private (string Owner, string Kind)? _viewedOfflinePlaylist;

    // These are explicitly local snapshot keys. They never enter a server API request.
    private const string LocalFavoritesOwner = "local:favorites";
    private const string LocalFavoritesKind = "liked";
    private const string ManualDownloadAdmissionMessage = "Не удалось подготовить сохраняемую очередь. Выберите меньше треков или обновите список.";
    private const string AutomaticDownloadAdmissionMessage = "Автоскачивание отложено. Новый пакет не помещается в сохраняемую очередь загрузок.";
    private PlaylistDetails FavoritesDownloadSnapshot() => new(LocalFavoritesOwner, LocalFavoritesKind,
        "Мне нравится", "Локальный снимок избранного", 0, _favoritesCache.ToArray());
    private PlaylistDetails? CurrentDownloadPlaylist() => _section == "favorites" ? FavoritesDownloadSnapshot() : _activePlaylist;
    private void UpdatePlaylistDownloadedStatus()
    {
        RefreshTransferProgress();
        if (DownloadPlaylistButton is null) return;
        var isFavorites = _section == "favorites";
        FavoritesWaveButton.Visibility = isFavorites ? Visibility.Visible : Visibility.Collapsed;
        FavoritesWaveButton.IsEnabled = _session is not null;
        AutoFavoritesCheckBox.Visibility = isFavorites ? Visibility.Visible : Visibility.Collapsed;
        if (isFavorites && _session is not null) PlaylistActions.Visibility = Visibility.Visible;
        if (CurrentDownloadPlaylist() is not { } playlist) { DownloadPlaylistButton.Visibility = Visibility.Collapsed; return; }
        var favorites = _section == "favorites";
        DownloadPlaylistButton.Visibility = Visibility.Visible;
        if (!_offline.IsAvailable)
        {
            DownloadPlaylistButton.IsEnabled = false;
            PlaylistDownloadText.Text = BrandPaths.OfflineUnavailableMessage;
            return;
        }
        DownloadPlaylistButton.IsEnabled = !_offlineMaintenance && _downloadCts is null && _session is not null && (!favorites || _favoritesSnapshotReady);
        if (RefreshCompactDownloadAction()) return;
        if (favorites && _libraryCts is not null && !_favoritesSnapshotReady)
        {
            DownloadPlaylistButton.Content = "↓ Скачать Мне нравится целиком";
            PlaylistDownloadText.Text = "Обновляем список избранного…";
            return;
        }
        try
        {
            var title = favorites ? "Мне нравится" : "плейлист";
            var state = playlist.Tracks.Count > BackgroundOfflineThreshold || favorites && _libraryCts is not null
                ? LargePlaylistStatus(playlist) : _offline.InspectPlaylistStatus(playlist.Owner, playlist.Kind, playlist.Tracks,
                    _lifetime.Token, CurrentOfflineDownloadChecks);
            if (state is null)
            {
                DownloadPlaylistButton.Content = $"↓ Скачать {title} целиком";
                PlaylistDownloadText.Text = "Проверяем скачанную музыку…";
                return;
            }
            DownloadPlaylistButton.Content = state.Complete ? $"✓ Скачан · {state.Total}" : state.Downloaded > 0 ? $"↓ Докачать · {state.Downloaded}/{state.Total}" : $"↓ Скачать {title} целиком";
            PlaylistDownloadText.Text = favorites && !_favoritesSnapshotReady ? "Дождитесь полной загрузки избранного" : state.Complete ? "Все треки доступны офлайн" : state.HasSnapshot ? $"Скачано {state.Downloaded} из {state.Total}" + (state.ChangedSinceSnapshot ? " · список изменился" : "") : "";
        }
        catch (Exception ex) when (ex is MusicException or IOException or UnauthorizedAccessException) { PlaylistDownloadText.Text = "Не удалось прочитать статус скачивания"; }
    }
    private async void DownloadPlaylist_Click(object sender, RoutedEventArgs e)
    {
        if (IsCurrentCompactDownload() && _compactDownloadOperation is { Total: > 0 } operation
            && (_downloadQueue.Running || !operation.Complete))
        { DownloadCenter_Click(sender, e); return; }
        await DownloadPlaylistAsync();
    }
    private Task DownloadPlaylistAsync()
    {
        if (CurrentDownloadPlaylist() is not { } playlist || _session is null || _downloadCts is not null || _offlineMaintenance
            || _section == "favorites" && !_favoritesSnapshotReady) return Task.CompletedTask;
        return StartDownloadBatchAsync(playlist.Tracks, playlist.Title, playlist, automatic: false);
    }
    private Task StartDownloadBatchAsync(IEnumerable<TrackRow> tracks, string label, PlaylistDetails? playlist, bool automatic)
        => AdmitDownloadBatchAsync(tracks, label, playlist, automatic, automaticAfterRemoval: false);

    private Task AdmitDownloadBatchAsync(IEnumerable<TrackRow> tracks, string label, PlaylistDetails? playlist, bool automatic, bool automaticAfterRemoval)
    {
        if (!_offline.IsAvailable) return Task.CompletedTask;
        if (_downloadCts is not null || _offlineMaintenance || _session is null || _closed || _closeCommitInProgress) return Task.CompletedTask;
        if (DownloadQueueRestoreCanRetry || !TryFlushPendingDownloadQueueClears())
        {
            if (!automatic) SetStatus(DownloadQueueRestoreRetryMessage, true);
            return Task.CompletedTask;
        }
        if (HasPendingDownloadQueueClear && _downloadCheckpointProtection is not { ObservedForeign: null })
            _downloadCheckpointProtection = new();
        if (_downloadPlaylistIndexDamaged && playlist is not null)
        {
            if (!automatic) SetStatus(DownloadQueuePlaylistIndexDamagedMessage, true);
            return Task.CompletedTask;
        }
        if (automatic && (DownloadCheckpointBlocksAutomaticWork || DownloadQueueRestorePending
            || automaticAfterRemoval && !CanRestartAutoFavoritesAfterRemoval()
            || !automaticAfterRemoval && (_downloadsAutoSuspended || _downloadQueue.HasPaused))) return Task.CompletedTask;
        var confirmedOnlyAfterRemoval = automaticAfterRemoval && playlist is null && _downloadQueue.HasPaused;
        if (confirmedOnlyAfterRemoval && !DownloadQueueBelongsToActiveAccount) return Task.CompletedTask;
        if (automatic && DownloadQueueBelongsToActiveAccount
            && (confirmedOnlyAfterRemoval || _downloadQueue.Entries.Any(entry => entry.State == DownloadEntryState.Failed)))
        {
            var completedCandidates = new List<TrackRow>();
            var eligible = tracks.Where(track => !IsAutoRedownloadBlocked(track.Id));
            var appended = _downloadQueue.AppendAutomaticFavorites(eligible, SelectedQuality, completedCandidates,
                CanAdmitCurrentDownloadQueue, requeueExisting: true);
            if (appended == DownloadQueueAppendResult.Rejected)
            {
                SetStatus(AutomaticDownloadAdmissionMessage, true);
                return Task.CompletedTask;
            }
            if (appended == DownloadQueueAppendResult.Unchanged
                && completedCandidates.Count == 0 && playlist is null) return Task.CompletedTask;
            if (playlist is { Owner: LocalFavoritesOwner, Kind: LocalFavoritesKind }
                && _downloadQueuePlaylist is { Owner: LocalFavoritesOwner, Kind: LocalFavoritesKind })
            {
                if (!_downloadQueuePlaylistHistoryOnly && appended == DownloadQueueAppendResult.Unchanged
                    && !CanAdmitCurrentDownloadQueue(_downloadQueue.Entries))
                {
                    SetStatus(AutomaticDownloadAdmissionMessage, true);
                    return Task.CompletedTask;
                }
                _downloadQueuePlaylistHistoryOnly = true;
            }
            if (automaticAfterRemoval && playlist is not null) _autoFavoritesRemovalRestart = null;
            HashSet<DownloadEntry>? resumeEntries = null;
            if (confirmedOnlyAfterRemoval)
            {
                // Only run the independently confirmed tracks. The interrupted
                // batch still needs the verified collection or an explicit Continue.
                var confirmedIds = eligible.Select(track => track.Id).ToHashSet(StringComparer.Ordinal);
                resumeEntries = _downloadQueue.Entries.Where(entry => entry.State == DownloadEntryState.Queued
                    && entry.AutomaticFavoriteQuality is not null && confirmedIds.Contains(entry.Track.Id)).ToHashSet();
            }
            return RunDownloadQueueAsync(retryFailed: false, automaticFavoritesSnapshot: playlist,
                resumeEntries: resumeEntries, completedFavorites: completedCandidates);
        }
        var selected = DownloadQueueState.SelectWorkTracks(tracks, DownloadQueueStore.MaximumEntries + 1);
        var favoritesBatch = automatic || playlist is { Owner: LocalFavoritesOwner, Kind: LocalFavoritesKind };
        var quality = SelectedQuality;
        if (!DownloadQueueStore.CanAdmit(_account?.Uid ?? "unbound", selected.Select(track => new DownloadEntry(track)),
            label, quality, automatic, favoritesBatch, playlist))
        {
            SetStatus(automatic ? AutomaticDownloadAdmissionMessage : ManualDownloadAdmissionMessage, true);
            return Task.CompletedTask;
        }
        _autoFavoritesRemovalRestart = null;
        _downloadQueue.Replace(selected, label, favoritesBatch);
        _downloadQueuePlaylist = playlist;
        _downloadQueuePlaylistPersistence = null;
        _downloadQueuePlaylistHistoryOnly = false;
        _downloadQueueRecordPlaylistOccurrences = true;
        _downloadQueueQuality = quality; _downloadQueueAutomatic = automatic;
        _downloadQueueAccountGeneration = _accountGeneration;
        _downloadQueueAccountUid = _account?.Uid;
        _downloadsAutoSuspended = false;
        _downloadCheckpointExplicitBatch = !automatic && selected.Count > 0;
        BeginDownloadQueuePersistence();
        return RunDownloadQueueAsync(retryFailed: false);
    }
    private bool CanAdmitCurrentDownloadQueue(IEnumerable<DownloadEntry> proposed) => DownloadQueueStore.CanAdmit(_account?.Uid ?? "unbound",
        proposed, _downloadQueue.Label, _downloadQueueQuality, _downloadQueueAutomatic, _downloadQueue.FavoritesBatch,
        _downloadQueuePlaylist, _downloadQueueRecordPlaylistOccurrences, _downloadQueuePlaylistPersistence);
    private Task RunDownloadQueueAsync(bool retryFailed, PlaylistDetails? automaticFavoritesSnapshot = null,
        IReadOnlySet<DownloadEntry>? resumeEntries = null, IReadOnlyList<TrackRow>? completedFavorites = null)
    {
        if (!_offline.IsAvailable) return Task.CompletedTask;
        var selected = _downloadQueue.BeginRun(retryFailed, resumeEntries);
        if (selected.Length == 0 && automaticFavoritesSnapshot is null && completedFavorites is not { Count: > 0 })
        { RefreshDownloadCenter(); return Task.CompletedTask; }
        var accountToken = _accountCts.Token;
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, accountToken);
        var appendedFavoritesOnly = selected.All(entry => entry.AutomaticFavoriteQuality is not null);
        _downloadCts = cts; _downloadIsAutomatic = _downloadQueueAutomatic || appendedFavoritesOnly;
        var appendedFavoritesCts = !_downloadIsAutomatic && selected.Any(entry => entry.AutomaticFavoriteQuality is not null)
            ? CancellationTokenSource.CreateLinkedTokenSource(cts.Token) : null;
        _appendedFavoritesCts = appendedFavoritesCts;
        _compactDownloadOperation?.Dispose(); _compactDownloadOperation = null;
        var quality = _downloadQueueQuality;
        var favoritesQuality = SelectedQuality;
        var automatic = _downloadQueueAutomatic;
        var playlist = automaticFavoritesSnapshot ?? (appendedFavoritesOnly ? null : _downloadQueuePlaylist);
        var rememberPlaylist = automaticFavoritesSnapshot is not null || !_downloadQueuePlaylistHistoryOnly;
        var playlistQuality = automaticFavoritesSnapshot is null ? quality : favoritesQuality;
        var accountGeneration = _downloadQueueAccountGeneration;
        var accountUid = _downloadQueueAccountUid;
        bool CanRefreshBatchCopies() => !_closed && !_closeCommitInProgress && !_offlineMaintenance
            && !_appUpdatePreparingRestart && !_startupUpdateRestartRequested && !_lifetime.IsCancellationRequested
            && !accountToken.IsCancellationRequested && ReferenceEquals(_downloadCts, cts)
            && DownloadQueueBelongsToActiveAccount && accountGeneration == _accountGeneration && accountUid == _account?.Uid;
        var concurrentDownloads = Math.Clamp(_concurrentDownloads, 1, 10);
        _offline.ConcurrentDownloads = concurrentDownloads;
        CompactDownloadOperation? operation = null;
        var dispatch = new DownloadProgressDispatch(
            action => Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, action),
            () =>
            {
                if (_closed || cts.IsCancellationRequested || accountGeneration != _accountGeneration
                    || !ReferenceEquals(_downloadCts, cts) || !ReferenceEquals(_compactDownloadOperation, operation)) return;
                FlushDownloadProgress();
            });
        _downloadTask = RunBatchAsync();
        return _downloadTask;

        async Task RunBatchAsync()
        {
            // Publish _downloadTask before any completion or auto-follow-up can run.
            await Task.Yield();
            UpdateControls(); UpdatePlaylistDownloadedStatus(); RefreshDownloadCenter();
            var next = 0;
            var offlineChanged = false;
            var initialRejectionVersion = _offline.DownloadVerificationRejectionVersion;
            HashSet<DownloadEntry>? inspectedHistory = null;
            HashSet<DownloadEntry> likelyTransfers = [];
            try
            {
                await PersistDownloadQueueAsync(force: true);
                await _offline.RecoverPendingAsync(cts.Token);
                cts.Token.ThrowIfCancellationRequested();
                // Estimate the initial transfer cohort by metadata. Workers add
                // damaged candidates to the cohort after verification and before
                // opening audio, without hashing newly selected rows twice.
                var (work, missingCompleted) = await Task.Run(async () =>
                {
                    var lookup = _offline.CaptureTrackLookup();
                    var work = selected.Where(entry =>
                    {
                        cts.Token.ThrowIfCancellationRequested();
                        var existing = lookup(entry.Track);
                        return existing is null || !((automatic || entry.AutomaticFavoriteQuality is not null) && !retryFailed
                            || AudioQualityPolicy.IsSufficient(existing.Codec, existing.QualityBitrate, entry.AutomaticFavoriteQuality ?? quality));
                    }).ToArray();
                    var missing = new List<TrackRow>();
                    if (completedFavorites is not null)
                    {
                        foreach (var tracks in completedFavorites.Chunk(OfflineLibrary.DownloadVerificationBatchSize))
                        {
                            try
                            {
                                using var verified = await _offline.HoldDownloadCacheBatchAsync(
                                    tracks.Select(track => new OfflineLibrary.DownloadCacheRequest(track)).ToArray(), cts.Token).ConfigureAwait(false);
                                for (var i = 0; i < tracks.Length; i++)
                                {
                                    var result = verified[i];
                                    if (result.Item is null && result.Unavailable is null) missing.Add(tracks[i]);
                                }
                            }
                            catch (IOException) { /* Recheck these history entries on the next verified refresh. */ }
                        }
                    }
                    return (work, missing);
                }, cts.Token);
                cts.Token.ThrowIfCancellationRequested();
                if (accountGeneration != _accountGeneration) { SuspendDownloadQueueForAccountChange(); cts.Token.ThrowIfCancellationRequested(); }
                var reopeningRejected = false;
                var reopened = _autoFavoritesEnabled && missingCompleted.Count > 0
                    ? _downloadQueue.RequeueMissingCompletedFavorites(missingCompleted.Where(track => _favoriteTrackIds.Contains(track.Id)).ToArray(),
                        favoritesQuality, CanAdmitCurrentDownloadQueue, out reopeningRejected)
                    : [];
                if (reopened.Length > 0)
                {
                    inspectedHistory = reopened.ToHashSet();
                    selected = [.. selected, .. reopened];
                    work = [.. work, .. reopened];
                    RefreshDownloadCenter();
                }
                likelyTransfers = work.ToHashSet();
                operation = new CompactDownloadOperation(work, playlist, accountGeneration, () => RefreshCompactDownloadAction());
                _compactDownloadOperation = operation;
                RefreshDownloadActivity();
                if (rememberPlaylist && playlist is not null)
                {
                    await _offline.RememberPlaylistAsync(playlist, playlistQuality, cts.Token);
                    offlineChanged = true;
                }
                // The fixed worker pool follows this batch's settings snapshot. Metadata stays
                // in the snapshot; audio is streamed to disk with bounded buffers.
                await Task.WhenAll(Enumerable.Range(0, Math.Min(concurrentDownloads, selected.Length)).Select(_ => WorkerAsync()));
                cts.Token.ThrowIfCancellationRequested();
                _downloadsAutoSuspended = _downloadQueue.HasPaused;
                if (reopeningRejected) SetStatus(AutomaticDownloadAdmissionMessage, true);
                else ReportDownloadOutcome();
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
                _downloadsAutoSuspended = true;
                if (!CanStageFavoritesDuringClear() && !CanStageFavoritesDuringRemoval())
                    ClearPendingAutoFavorites();
                if (!_closed && DownloadQueueBelongsToActiveAccount && !_offlineMaintenance)
                    SetStatus("Загрузка на паузе. Откройте «Загрузки», чтобы продолжить очередь.");
            }
            catch (Exception ex)
            {
                _diagnostics.LastErrorType = "DownloadQueue:" + ex.GetType().Name;
                foreach (var entry in selected.Where(entry => entry.Active || entry.State == DownloadEntryState.Queued))
                    entry.SetState(DownloadEntryState.Failed, DownloadFailure.Describe(ex));
                if (!_closed) SetStatus(DownloadFailure.Describe(ex), true);
            }
            finally
            {
                dispatch.Dispose(); _downloadProgress.Clear();
                if (_downloadQueueAccountGeneration != _accountGeneration) _downloadQueue.Suspend();
                _downloadQueue.EndRun(cts.IsCancellationRequested);
                operation?.Dispose();
                // Keep this operation published until its checkpoint finishes;
                // a new batch/account restore must not replace the shared queue
                // while the prior task is still completing asynchronously.
                if (!_closed) await PersistDownloadQueueAsync(force: true);
                // A user pause stops transfers. Already committed copies can
                // still replace the earlier temporary result for this account.
                IReadOnlyDictionary<string, OfflineCopyCheck>? verifiedCopies = null;
                if (CanRefreshBatchCopies())
                {
                    var verifyFailures = !cts.IsCancellationRequested;
                    var previousChecks = CurrentOfflineDownloadChecks;
                    var tracksToVerify = selected.Where(entry => verifyFailures && entry.State == DownloadEntryState.Failed
                        || entry.State == DownloadEntryState.Completed
                            && previousChecks?.GetValueOrDefault(entry.Track.Id)?.RetryOnNavigation == true)
                        .Select(entry => entry.Track).ToArray();
                    if (tracksToVerify.Length > 0)
                    {
                        using var pausedVerification = verifyFailures ? null : CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, accountToken);
                        var verificationToken = pausedVerification?.Token ?? cts.Token;
                        try
                        {
                            verifiedCopies = await Task.Run(() => VerifyOfflineCopiesAsync(tracksToVerify, verificationToken), verificationToken);
                            if (OfflineCopyChecksBeforePublishForChecks is { } before) await before(verificationToken);
                        }
                        catch (OperationCanceledException) when (verificationToken.IsCancellationRequested) { }
                        catch (Exception error) when (error is IOException or UnauthorizedAccessException or MusicException or System.ComponentModel.Win32Exception)
                        {
                            if (CanRefreshBatchCopies())
                                _diagnostics.LastErrorType = "OfflineDownloadVerification:" + error.GetType().Name;
                            if (verifyFailures)
                                verifiedCopies = selected.Where(entry => entry.State == DownloadEntryState.Failed)
                                    .Select(entry => entry.Track).DistinctBy(track => track.Id).ToDictionary(track => track.Id,
                                        track => new OfflineCopyCheck(track, [], RetryOnNavigation: true), StringComparer.Ordinal);
                        }
                        var latest = CurrentOfflineDownloadChecks;
                        if (verifiedCopies is { Count: > 0 } && !ReferenceEquals(latest, previousChecks))
                            verifiedCopies = verifiedCopies.Where(check => ReferenceEquals(latest?.GetValueOrDefault(check.Key), previousChecks?.GetValueOrDefault(check.Key))
                                    && check.Value.RefusedCopies?.Any(copy => previousChecks?.Refuses(copy) == true && latest?.Refuses(copy) != true) != true)
                                .ToDictionary(check => check.Key, check => check.Value, StringComparer.Ordinal);
                    }
                }
                var refreshBatchCopies = CanRefreshBatchCopies();
                if (ReferenceEquals(_downloadCts, cts))
                { _downloadCts = null; _downloadTask = null; _downloadIsAutomatic = false; _appendedFavoritesCts = null; }
                appendedFavoritesCts?.Dispose();
                cts.Dispose();
                if (!_closed)
                {
                    if (refreshBatchCopies && verifiedCopies is { Count: > 0 }) RememberOfflineDownloadChecks(verifiedCopies);
                    RefreshDownloadedMarks(); RefreshDownloadCenter(); RefreshDownloadStorage();
                    if (refreshBatchCopies && (offlineChanged || verifiedCopies is { Count: > 0 }
                        || initialRejectionVersion != _offline.DownloadVerificationRejectionVersion))
                        _ = RefreshCurrentOfflineViewAfterDownloadAsync();
                    TryStartPendingAutoFavorites();
                }
            }

            async Task WorkerAsync()
            {
                while (next < selected.Length)
                {
                    cts.Token.ThrowIfCancellationRequested();
                    var start = next++;
                    var entry = selected[start];
                    if (likelyTransfers.Contains(entry))
                    {
                        await ProcessEntryAsync(entry, inspectedHistory?.Contains(entry) == true);
                        continue;
                    }
                    while (next < selected.Length && next - start < OfflineLibrary.DownloadVerificationBatchSize
                        && !likelyTransfers.Contains(selected[next])) next++;
                    var entries = selected[start..next];
                    var requests = entries.Select(item =>
                    {
                        var track = item.Track;
                        var automaticQuality = item.AutomaticFavoriteQuality;
                        return new OfflineLibrary.DownloadCacheRequest(track, candidate => AcceptCached(track, automaticQuality, candidate));
                    }).ToArray();
                    var missing = new List<DownloadEntry>();
                    OfflineLibrary.VerifiedDownloadBatch verified;
                    try { verified = await Task.Run(() => _offline.HoldDownloadCacheBatchAsync(requests, cts.Token), cts.Token); }
                    catch (IOException error)
                    {
                        foreach (var item in entries) await ProcessEntryAsync(item, checkedCache: true, new(null, error));
                        continue;
                    }
                    using (verified)
                    {
                        for (var i = 0; i < entries.Length; i++)
                        {
                            var result = verified[i];
                            if (result.Item is null && result.Unavailable is null) missing.Add(entries[i]);
                            else await ProcessEntryAsync(entries[i], checkedCache: true, result);
                        }
                    }
                    foreach (var item in missing) await ProcessEntryAsync(item, checkedCache: true);
                }
            }

            bool AcceptCached(TrackRow track, AudioQuality? automaticQuality, OfflineItem item) => !track.Available
                || (automatic || automaticQuality is not null) && !retryFailed
                || AudioQualityPolicy.IsSufficient(item.Codec, item.QualityBitrate, automaticQuality ?? quality);

            async Task ProcessEntryAsync(DownloadEntry entry, bool checkedCache, OfflineLibrary.DownloadCacheResult verified = default)
            {
                var track = entry.Track;
                cts.Token.ThrowIfCancellationRequested();
                if (entry.SkipFavoritesDownload || entry.State != DownloadEntryState.Queued) { operation!.Exclude(entry); return; }
                var automaticQuality = entry.AutomaticFavoriteQuality;
                var entryAutomatic = automatic || automaticQuality is not null;
                var entryQuality = automaticQuality ?? quality;
                var entryToken = automaticQuality is not null && appendedFavoritesCts is not null
                    ? appendedFavoritesCts.Token : cts.Token;
                if (entryToken.IsCancellationRequested)
                { entry.SetState(DownloadEntryState.Paused); operation!.Exclude(entry); return; }
                if (entryAutomatic && IsAutoRedownloadBlocked(track.Id))
                { entry.SetState(DownloadEntryState.Removed); operation!.Exclude(entry); return; }
                try
                {
                    entry.Begin();
                    // Automatic favorites synchronization only fills missing
                    // tracks; read the full checksum only for real reuse.
                    if (verified.Unavailable is not null) throw verified.Unavailable;
                    var existing = checkedCache ? verified.Item : await Task.Run(() => _offline.FindVerifiedAsync(track, entryToken,
                        item => AcceptCached(track, automaticQuality, item), failIfUnavailable: true), entryToken);
                    entryToken.ThrowIfCancellationRequested();
                    if (entry.SkipFavoritesDownload)
                    { entry.SetState(DownloadEntryState.Queued); operation!.Exclude(entry); return; }
                    if (existing is not null)
                    { operation!.Exclude(entry); entry.Complete(existing.Bytes); return; }
                    // Recheck the presentation cohort if an indexed file changed
                    // after preflight. The downloader's admission rules stay below.
                    operation!.Include(entry);
                    if (!track.Available)
                    {
                        entry.SetState(DownloadEntryState.Unavailable, DownloadFailure.UnavailableReason);
                        return;
                    }
                    var session = await EnsureSessionAsync(entryToken);
                    entryToken.ThrowIfCancellationRequested();
                    if (_downloadQueueAccountGeneration != _accountGeneration) { SuspendDownloadQueueForAccountChange(); cts.Token.ThrowIfCancellationRequested(); }
                    var audio = await _api.ResolveAudioAsync(session, track.RequestId, entryQuality, entryToken);
                    entryToken.ThrowIfCancellationRequested();
                    if (entry.SkipFavoritesDownload)
                    { entry.SetState(DownloadEntryState.Queued); operation.Exclude(entry); return; }
                    // Metadata resolution yields: another library operation may
                    // have removed or upgraded the earlier cached candidate.
                    existing = await Task.Run(() => _offline.FindVerifiedAsync(track, entryToken,
                        item => !AudioQualityPolicy.IsBetter(audio.Codec, audio.Bitrate, item.Codec, item.QualityBitrate),
                        failIfUnavailable: true), entryToken);
                    entryToken.ThrowIfCancellationRequested();
                    if (entry.SkipFavoritesDownload)
                    { entry.SetState(DownloadEntryState.Queued); operation.Exclude(entry); return; }
                    if (existing is not null)
                    { operation.Exclude(entry); entry.Complete(existing.Bytes); return; }
                    entry.SetState(DownloadEntryState.Downloading);
                    var transferred = false;
                    var progress = new DownloadProgressSink(() => { Volatile.Write(ref transferred, true); dispatch.Request(); });
                    _downloadProgress[entry] = progress;
                    var saved = await _offline.SaveAsync(track, audio, entryToken, progress, recheckQuality: true);
                    offlineChanged = true;
                    // A concurrent same-key Save may supply an already committed
                    // file without any media transfer by this run.
                    if (!Volatile.Read(ref transferred)) operation.Exclude(entry);
                    entry.Complete(saved.Bytes);
                    ApplyOfflineStates(KnownTrackRows().Where(row => row.Id == track.Id));
                }
                catch (OperationCanceledException) when (cts.IsCancellationRequested) { throw; }
                catch (OperationCanceledException) when (entryToken.IsCancellationRequested) { entry.SetState(DownloadEntryState.Paused); }
                catch (Exception ex)
                {
                    cts.Token.ThrowIfCancellationRequested();
                    if (entryToken.IsCancellationRequested) entry.SetState(DownloadEntryState.Paused);
                    else
                    {
                        entry.SetState(DownloadEntryState.Failed, DownloadFailure.Describe(ex));
                        _diagnostics.LastErrorType = "Download:" + ex.GetType().Name;
                    }
                }
                finally
                {
                    _downloadProgress.Remove(entry);
                    // Cached rows can number in thousands: their existing row
                    // notifications suffice, followed by the single final refresh.
                    if (operation!.Contains(entry)) RefreshDownloadActivity();
                }
            }
        }
    }
    private void ReportDownloadOutcome()
    {
        var progress = PlaylistTransferProgress.Read(_downloadQueue.Entries);
        if (progress.Failed > 0 || progress.Unavailable > 0)
            SetStatus($"Скачано {progress.Completed:N0} из {progress.Total:N0}. " +
                (progress.Failed > 0 ? $"Не удалось скачать: {progress.Failed}. " : "") +
                (progress.Unavailable > 0 ? $"Недоступно: {progress.Unavailable}. " : "") +
                "Подробности в «Загрузках».", true);
        else if (!_downloadQueueAutomatic && progress.Total > 0 && progress.Completed == progress.Total)
            SetStatus(_downloadQueue.Entries.Any(entry => entry.State == DownloadEntryState.Removed)
                ? "Оставшиеся треки скачаны и доступны офлайн." : "Музыка скачана и доступна офлайн.");
    }
    private void BeginFavoritesRefresh()
    {
        UpdatePlaylistDownloadedStatus();
    }
    private void ClearPendingAutoFavorites()
    {
        _pendingAutoFavorites = null;
        _pendingConfirmedFavorites.Clear();
    }
    private void UpdatePendingAutoFavoritesFromConfirmedReadback(PlaylistDetails? previous, bool allowNewRemovalSnapshot)
    {
        var stageForRemoval = _autoFavoritesEnabled && CanStageFavoritesDuringRemoval()
            && (allowNewRemovalSnapshot || previous is not null && ReferenceEquals(previous, _singleRemovalVerifiedFavorites));
        var restart = _autoFavoritesRemovalRestart is { } admission && _autoFavoritesEnabled && CanRestartAutoFavoritesAfterRemoval()
            && (allowNewRemovalSnapshot || previous is not null && ReferenceEquals(previous, admission.VerifiedSnapshot))
            ? admission : null;
        if (previous is null && !stageForRemoval && restart is null) return;
        var current = _pendingAutoFavorites = FavoritesDownloadSnapshot();
        if (stageForRemoval) _singleRemovalVerifiedFavorites = current;
        if (restart is not null) _autoFavoritesRemovalRestart = restart with { VerifiedSnapshot = current };
    }
    private void ScheduleConfirmedFavoriteDownload(TrackRow row)
    {
        if (!_offline.IsAvailable) return;
        if (_session is null || _demo || _closed || !_favoriteTrackIds.Contains(row.Id)) return;
        ClearOfflineDeletionSuppression(row.Id);
        if (!_autoFavoritesEnabled) return;
        if (_offlineMaintenance ? !CanStageFavoritesDuringClear() && !CanStageFavoritesDuringRemoval()
            : !CanRestartAutoFavoritesAfterRemoval() && !DownloadQueueRestorePending && !DownloadQueueRestoreCanRetry && !DownloadCheckpointBlocksAutomaticWork
                && (_downloadsAutoSuspended || _downloadQueue.HasPaused)) return;
        // Confirmed metadata is already available. A held/failed full refresh
        // must not postpone this track or erase earlier pending confirmed likes.
        _pendingConfirmedFavorites[row.Id] = row;
        TryStartPendingAutoFavorites();
    }
    private void ScheduleAutoFavoritesDownloads() => ScheduleAutoFavoritesFromSnapshot(collectionRefreshed: true);
    private void ScheduleAutoFavoritesFromSnapshot(bool collectionRefreshed)
    {
        _favoritesSnapshotReady = true;
        if (collectionRefreshed && !_offlineMaintenance) _deletedUntilFavoritesRefresh.Clear();
        UpdatePlaylistDownloadedStatus();
        if (!_offline.IsAvailable) return;
        if (!_autoFavoritesEnabled || _session is null || _demo || _closed) return;
        if (_offlineMaintenance)
        {
            if (collectionRefreshed && CanStageFavoritesDuringRemoval())
                _singleRemovalVerifiedFavorites = _pendingAutoFavorites = FavoritesDownloadSnapshot();
            return;
        }
        if (!collectionRefreshed && _deletedUntilFavoritesRefresh.Count > 0) return;
        if (DownloadQueueRestorePending || DownloadQueueRestoreCanRetry || DownloadCheckpointBlocksAutomaticWork)
        {
            _pendingAutoFavorites = FavoritesDownloadSnapshot();
            return;
        }
        if (collectionRefreshed && _autoFavoritesRemovalRestart is { } restart && CanRestartAutoFavoritesAfterRemoval())
        {
            _pendingAutoFavorites = FavoritesDownloadSnapshot();
            _autoFavoritesRemovalRestart = restart with { VerifiedSnapshot = _pendingAutoFavorites };
            TryStartPendingAutoFavorites();
            return;
        }
        if (_downloadsAutoSuspended) return;
        if (!_downloadQueue.HasPaused) _pendingAutoFavorites = FavoritesDownloadSnapshot();
        else if (!_downloadQueue.Entries.Any(entry => entry.ResumeAutomaticallyOnStartup)) return;
        TryStartPendingAutoFavorites();
    }
    private bool CanRestartAutoFavoritesAfterRemoval() => _autoFavoritesRemovalRestart is { } restart
        && restart.AdmissionRevision == _downloadQueueAdmissionRevision && restart.AccountGeneration == _accountGeneration
        && restart.AccountUid == _account?.Uid;
    private bool CanStageFavoritesDuringClear() => _clearFavoritesAdmission is { } clear
        && clear.AdmissionRevision == _downloadQueueAdmissionRevision && clear.AccountGeneration == _accountGeneration
        && clear.AccountUid == _account?.Uid;
    private bool CanStageFavoritesDuringRemoval() => _singleRemovalFavoritesAdmission is { } removal
        && removal.AdmissionRevision == _downloadQueueAdmissionRevision && removal.AccountGeneration == _accountGeneration
        && removal.AccountUid == _account?.Uid;
    private bool IsAutoRedownloadBlocked(string id) => _deletedUntilFavoritesRefresh.Contains(id) || _deletedUntilConfirmedLike.Contains(id);
    private void ClearOfflineDeletionSuppression(string id)
    { _deletedUntilFavoritesRefresh.Remove(id); _deletedUntilConfirmedLike.Remove(id); }
    private void TryStartPendingAutoFavorites()
    {
        if (!_autoFavoritesEnabled || _downloadCts is not null || _downloadUnlikeTotal > 0 || _offlineMaintenance || _session is null || _closed || _closeCommitInProgress
            || _appUpdatePreparingRestart || _startupUpdateRestartRequested || DownloadQueueRestorePending || DownloadQueueRestoreCanRetry
            || DownloadCheckpointBlocksAutomaticWork) return;
        var restartAfterRemoval = CanRestartAutoFavoritesAfterRemoval();
        // A subsequent authoritative membership update can invalidate the pending snapshot.
        if (restartAfterRemoval && _autoFavoritesRemovalRestart?.VerifiedSnapshot is { } verified
            && ReferenceEquals(verified, _pendingAutoFavorites))
        {
            _pendingAutoFavorites = null;
            _ = AdmitDownloadBatchAsync(verified.Tracks, "Автоскачивание «Мне нравится»", verified,
                automatic: true, automaticAfterRemoval: true);
            if (_downloadCts is not null)
            {
                foreach (var track in verified.Tracks)
                    _pendingConfirmedFavorites.Remove(track.Id);
                return;
            }
        }
        var hasPaused = _downloadQueue.HasPaused;
        if (hasPaused && !_downloadsAutoSuspended && _favoritesSnapshotReady && _verifiedFavoritesGeneration == _accountGeneration
            && _favoritesAccountUid == _account?.Uid && DownloadQueueBelongsToActiveAccount && !_downloadQueue.Suspended)
        {
            // A verified like can include an edition whose metadata is still missing.
            var idsWithMissingMetadata = _favoritesCache.Where(track => !track.HasTrackMetadata)
                .Select(track => track.Id).ToHashSet(StringComparer.Ordinal);
            var verifiedEditions = _favoritesCache.Where(track => idsWithMissingMetadata.Contains(track.Id)
                && track.HasTrackMetadata && track.Available && TryTrackReference(track) is { } reference
                && (reference.AlbumId is null || reference.AlbumId == track.AlbumId))
                .Select(track => track.RequestId).ToHashSet(StringComparer.Ordinal);
            var resumeEntries = _downloadQueue.Entries.Where(entry => entry.ResumeAutomaticallyOnStartup
                && !entry.SkipFavoritesDownload && _favoriteTrackIds.Contains(entry.Track.Id)
                && (!idsWithMissingMetadata.Contains(entry.Track.Id) || verifiedEditions.Contains(entry.Track.RequestId))
                && !IsAutoRedownloadBlocked(entry.Track.Id)).ToHashSet();
            if (resumeEntries.Count > 0)
            {
                _ = RunDownloadQueueAsync(retryFailed: false, resumeEntries: resumeEntries);
                return;
            }
        }
        var waitingForVerifiedCollection = _downloadsAutoSuspended || hasPaused;
        if (waitingForVerifiedCollection && !restartAfterRemoval) return;
        if (_pendingConfirmedFavorites.Count > 0)
        {
            var tracks = _pendingConfirmedFavorites.Values.ToArray();
            _pendingConfirmedFavorites.Clear();
            // This is an incremental batch, not a complete collection snapshot.
            _ = AdmitDownloadBatchAsync(tracks, "Автоскачивание «Мне нравится»", null,
                automatic: true, automaticAfterRemoval: waitingForVerifiedCollection);
            if (_downloadCts is not null) return;
        }
        if (waitingForVerifiedCollection) return;
        if (_pendingAutoFavorites is not { } snapshot) return;
        _pendingAutoFavorites = null;
        _ = StartDownloadBatchAsync(snapshot.Tracks, "Автоскачивание «Мне нравится»", snapshot, automatic: true);
    }
    private void AutoFavorites_Changed(object sender, RoutedEventArgs e)
    {
        if (!_preferencesReady || !_offline.IsAvailable) return;
        var enabled = AutoFavoritesCheckBox.IsChecked == true;
        if (!TrySavePreferences(out _, autoDownloadFavorites: enabled))
        {
            ApplyAutoFavoritesPreference(_autoFavoritesEnabled);
            return;
        }
        _autoFavoritesEnabled = enabled;
        if (_autoFavoritesEnabled)
        {
            SetStatus($"Автоскачивание включено. Избранное скачивается после загрузки списка, до {_concurrentDownloads} треков одновременно.");
            if (_favoritesSnapshotReady) ScheduleAutoFavoritesFromSnapshot(collectionRefreshed: false);
        }
        else
        {
            ClearPendingAutoFavorites();
            _downloadQueue.KeepPausedAfterRestart();
            if (_downloadIsAutomatic) PauseDownloadQueue();
            else if (DownloadQueueBelongsToActiveAccount)
            {
                _appendedFavoritesCts?.Cancel();
                DiscardUnstartedAutomaticFavorites();
            }
            SetStatus("Автоскачивание выключено. Уже сохранённая музыка останется офлайн.");
        }
    }
    private async void DeleteOfflineTrack_Click(object sender, RoutedEventArgs e)
    {
        if (TracksList.SelectedItem is TrackRow track) await DeleteOfflineTrackAsync(track);
    }
    private async Task DeleteOfflineTrackAsync(TrackRow track)
    {
        if (_closed || _closeCommitInProgress || !_offline.IsAvailable || _offlineMaintenance) return;
        var pendingAutomaticRestart = CanRestartAutoFavoritesAfterRemoval();
        var canRestartAfterRefresh = pendingAutomaticRestart || !_downloadsAutoSuspended && !_downloadQueue.HasPaused;
        var automaticCancellation = pendingAutomaticRestart || HasActiveOwnedDownload && _downloadIsAutomatic;
        var accountGeneration = _accountGeneration; var accountUid = _account?.Uid;
        var protectsCheckpoint = DownloadCheckpointNeedsRemovalVerification;
        var verifiedForeignCheckpoint = false;
        var ownsQueue = DownloadQueueBelongsToActiveAccount;
        var suspendedHere = false;
        var admissionRevision = _downloadQueueAdmissionRevision;
        bool IsCurrentRemoval() => !_closed && accountGeneration == _accountGeneration && accountUid == _account?.Uid
            && admissionRevision == _downloadQueueAdmissionRevision;
        void TrackFavoriteAdmission() => _singleRemovalFavoritesAdmission = (protectsCheckpoint || canRestartAfterRefresh) && accountUid is not null
            ? (admissionRevision, accountGeneration, accountUid) : null;
        _offlineRemovalTrackId = track.Id;
        BeginOfflineMaintenance();
        TrackFavoriteAdmission();
        RefreshDownloadCenter();
        RefreshMaintenanceActions();
        try
        {
            await _offline.RecoverPendingAsync(_lifetime.Token);
            if (!IsCurrentRemoval()) return;
            if (protectsCheckpoint && _downloadQueueStore is { } store && accountUid is not null)
            {
                var persisted = await Task.Run(() => store.Read(accountUid), _lifetime.Token);
                if (!IsCurrentRemoval()) return;
                protectsCheckpoint = DownloadCheckpointNeedsRemovalVerification;
                verifiedForeignCheckpoint = persisted.Status == DownloadQueueReadStatus.OtherAccount
                    && persisted.Replacement is { ObservedForeign: not null };
                TrackFavoriteAdmission();
                ownsQueue = DownloadQueueBelongsToActiveAccount;
                if (protectsCheckpoint && (persisted.Status is not (DownloadQueueReadStatus.Loaded or DownloadQueueReadStatus.Missing or DownloadQueueReadStatus.OtherAccount)
                    || persisted.Snapshot?.Entries.Any(entry => entry.Track.Id == track.Id) == true))
                {
                    SetStatus("Сначала восстановите очередь в «Центре загрузок», затем повторите удаление трека.", true);
                    return;
                }
            }
            _autoFavoritesRemovalRestart = null;
            _downloadsAutoSuspended = true;
            if (!DownloadQueueRestorePending && !DownloadQueueRestoreCanRetry && ownsQueue && !_downloadQueue.Suspended)
            {
                admissionRevision = ++_downloadQueueAdmissionRevision;
                TrackFavoriteAdmission();
                _compactDownloadOperation?.Dispose();
                if (!protectsCheckpoint)
                {
                    if (!ReferenceEquals(_singleRemovalVerifiedFavorites, _pendingAutoFavorites)) _pendingAutoFavorites = null;
                    if (!CanStageFavoritesDuringRemoval()) _pendingConfirmedFavorites.Clear();
                }
                _downloadQueue.Suspend();
                _downloadCts?.Cancel();
                suspendedHere = true;
            }
            _deletedUntilFavoritesRefresh.Add(track.Id);
            if (_downloadTask is { } task) await task;
            if (!IsCurrentRemoval()) return;
            await ReleaseOfflinePlaybackAsync(track.Id);
            var result = await _offline.RemoveTrackAsync(track.Id, _lifetime.Token);
            if (IsCurrentRemoval() && !result.HasRetainedIndexEntries)
            {
                if (DownloadQueueBelongsToActiveAccount) _downloadQueue.Removed(track.Id);
                _pendingConfirmedFavorites.Remove(track.Id);
                _deletedUntilFavoritesRefresh.Add(track.Id);
                var hasFailures = _downloadQueue.Entries.Any(entry => entry.State == DownloadEntryState.Failed);
                if (protectsCheckpoint || ownsQueue && _downloadQueue.Entries.Any(entry => entry.State == DownloadEntryState.Failed && !entry.SkipFavoritesDownload))
                    _deletedUntilConfirmedLike.Add(track.Id);
                if (result.Failed == 0 && result.Preserved == 0 && canRestartAfterRefresh && !_lifetime.IsCancellationRequested
                    && _session is not null && accountUid is not null
                    && (!protectsCheckpoint || verifiedForeignCheckpoint && !DownloadCheckpointBlocksAutomaticWork))
                {
                    // Only the removal's automatic pause can admit independent likes.
                    // Interrupted tracks still need a verified collection snapshot.
                    var hasPaused = _downloadQueue.HasPaused;
                    if (!hasPaused) _downloadsAutoSuspended = false;
                    if (hasPaused ? ownsQueue && automaticCancellation : !protectsCheckpoint && !hasFailures)
                        _autoFavoritesRemovalRestart = new(admissionRevision, accountGeneration, accountUid,
                            ReferenceEquals(_singleRemovalVerifiedFavorites, _pendingAutoFavorites) ? _singleRemovalVerifiedFavorites : null);
                }
            }
            RefreshDownloadCenter(); RefreshDownloadStorage();
            RefreshOfflineView(); RefreshDownloadedMarks();
            SetStatus(RemovalStatus(result) + (_autoFavoritesEnabled && !protectsCheckpoint && CanRestartAutoFavoritesAfterRemoval()
                && !_deletedUntilConfirmedLike.Contains(track.Id)
                ? " · автоскачивание вернёт трек после следующего обновления избранного" : ""), result.Preserved + result.Failed > 0);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex) { ShowError(ex); }
        finally
        {
            if (suspendedHere && IsCurrentRemoval())
            {
                _downloadQueue.AllowExplicitActionsAfterRemoval();
                _ = PersistDownloadQueueAsync(force: true);
            }
            _offlineRemovalTrackId = null;
            _singleRemovalFavoritesAdmission = null;
            _singleRemovalVerifiedFavorites = null;
            EndOfflineMaintenance();
            if (!_closed) RefreshMaintenanceActions();
            RefreshDownloadCenter(); UpdateControls(); UpdatePlaylistDownloadedStatus(); TryStartPendingAutoFavorites();
        }
    }
    private async void ClearOffline_Click(object sender, RoutedEventArgs e) => await ClearOfflineAsync();
    private async Task ClearOfflineAsync()
    {
        if (_closed || _closeCommitInProgress || !_offline.IsAvailable || _offlineMaintenance || _cacheMaintenance || _clearOfflineConfirmationPending) return;
        _clearOfflineConfirmationPending = true;
        RefreshMaintenanceActions();
        try
        {
            await _offline.RecoverPendingAsync(_lifetime.Token);
            if (_closed || _closeCommitInProgress || _offlineMaintenance || _cacheMaintenance) return;
            var count = _offline.Read().Count;
            if (MessageBox.Show(this, $"Удалить всю скачанную музыку ({count} сохранённых версий) и локальные снимки плейлистов?\n\nАвтоскачивание «Мне нравится» будет выключено. Посторонние и изменённые файлы останутся.", "Очистить скачанное", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { return; }
        catch (Exception ex) { ShowError(ex); return; }
        finally
        {
            _clearOfflineConfirmationPending = false;
            if (!_closed) RefreshMaintenanceActions();
        }
        await ClearOfflineAfterConfirmationAsync();
    }
    private async Task ClearOfflineAfterConfirmationAsync()
    {
        if (_closed || _closeCommitInProgress || !_offline.IsAvailable || _offlineMaintenance || _cacheMaintenance) return;
        BeginOfflineMaintenance();
        var accountGeneration = _accountGeneration; var accountUid = _account?.Uid;
        var maintenanceGeneration = ++_maintenanceGeneration;
        var resumeAutoFavorites = false;
        RefreshMaintenanceActions(); ShowMaintenance("Подготавливаем очистку музыки…", 0);
        try
        {
            if (!TrySavePreferences(out _, autoDownloadFavorites: false))
            {
                ShowMaintenance("Очистка отменена. Проверьте доступ к настройкам. Скачанная музыка сохранена.", null);
                return;
            }
            ApplyAutoFavoritesPreference(enabled: false);
            if (!SuspendDownloadQueueForAccountChange(keepQueueVisible: true))
            {
                ClearPendingAutoFavorites();
                if (_downloadIsAutomatic && HasActiveOwnedDownload) PauseDownloadQueueCore();
                ShowMaintenance("Очистка отменена. Проверьте доступ к сохранённой очереди загрузок. Скачанная музыка сохранена.", null);
                return;
            }
            var admissionRevision = _downloadQueueAdmissionRevision;
            _deletedUntilFavoritesRefresh.Clear(); _deletedUntilConfirmedLike.Clear();
            if (accountUid is not null) _clearFavoritesAdmission = (admissionRevision, accountGeneration, accountUid);
            if (_downloadTask is { } task) await task;
            await _offline.RecoverPendingAsync(_lifetime.Token);
            await ReleaseOfflinePlaybackAsync(null);
            var result = await _offline.ClearAsync(_lifetime.Token, new Progress<OfflineCleanupProgress>(progress =>
            { if (maintenanceGeneration == _maintenanceGeneration) ReportOfflineCleanup(progress); }));
            // The cleared queue stays suspended; enabling automatic favorites
            // may start a fresh batch without reviving the removed work.
            if (!_closed && admissionRevision == _downloadQueueAdmissionRevision
                && accountGeneration == _accountGeneration && accountUid == _account?.Uid)
            {
                _downloadQueue.Removed(null);
                _downloadsAutoSuspended = false;
                resumeAutoFavorites = _autoFavoritesEnabled;
            }
            RefreshDownloadCenter(); RefreshDownloadStorage();
            RefreshOfflineView(); RefreshDownloadedMarks();
            SetStatus(RemovalStatus(result) + (_autoFavoritesEnabled ? "" : " · автоскачивание выключено"), result.Preserved + result.Failed > 0);
            ShowMaintenance(RemovalStatus(result), 100);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex) { ShowError(ex); ShowMaintenance("Очистка не завершена. Сохранённые файлы можно проверить и повторить действие.", null); }
        finally
        {
            if (CanStageFavoritesDuringClear() && !resumeAutoFavorites) ClearPendingAutoFavorites();
            _clearFavoritesAdmission = null;
            EndOfflineMaintenance();
            RefreshMaintenanceActions(); UpdateControls(); UpdatePlaylistDownloadedStatus();
            if (resumeAutoFavorites)
            {
                if (_favoritesSnapshotReady) ScheduleAutoFavoritesFromSnapshot(collectionRefreshed: false);
                else TryStartPendingAutoFavorites();
            }
        }
    }
    private void ApplyAutoFavoritesPreference(bool enabled)
    {
        _autoFavoritesEnabled = enabled;
        var preferencesReady = _preferencesReady;
        var settingsInitializing = _settingsInitializing;
        try
        {
            _preferencesReady = false;
            _settingsInitializing = true;
            AutoFavoritesCheckBox.IsChecked = enabled;
            SettingsAutoFavorites.IsChecked = enabled;
        }
        finally
        {
            _preferencesReady = preferencesReady;
            _settingsInitializing = settingsInitializing;
        }
    }
    private Task ReleaseOfflinePlaybackAsync(string? trackId)
    {
        var paths = _offline.Read().Where(i => trackId is null || i.Id == trackId)
            .Select(i => _offline.ResolvePath(i.FileName)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (_pendingOfflinePlayback is { } pending && (trackId is null || trackId == pending.TrackId))
            pending.Request.Cancel();
        if ((_preloadIdentity?.LocalSource is { Bytes: not null } pendingPrepared && paths.Contains(pendingPrepared.Path))
            || (_preparedPlayback?.OpenedLocalSource is { Bytes: not null } openedPrepared && paths.Contains(openedPrepared.Path))
            || (_player.PreparedLocalPath is { } prepared && paths.Contains(prepared)))
            CancelNextPreparation();
        if (_player.FadingLocalPath is { } fading && paths.Contains(fading)) _player.FinishTransition();
        // NativePlayer also exposes a local temp buffer for remote playback.
        // Only an exact indexed file that this operation removes releases playback.
        if (_player.SourceLocalPath is { } source && paths.Contains(source))
        {
            _playbackGeneration++; _pendingSeek = null; _seekDragging = false;
            _playCts?.Cancel(); ReportWaveEnd(false); _player.Stop(); _progressTimer.Stop(); _loadingStream = false;
            if (_current is not null) _current.IsPlaying = false;
            HighlightTrack(null);
            if (_current is { } current && (current.IsOfflineLibraryRow || current.LocalPath is not null))
            {
                _current = null; TrackTitle.Text = "Выберите трек"; TrackArtist.Text = "Двойной щелчок или кнопка «Слушать»"; QualityText.Text = "";
            }
            UpdateProgress(); UpdateControls();
        }
        return _player.WaitForClosedLocalSourcesAsync(paths).WaitAsync(_lifetime.Token);
    }
    private static string RemovalStatus(OfflineRemovalResult result) => $"Удалено аудиофайлов: {result.Removed}" + (result.Preserved > 0 ? $" · пропущено: {result.Preserved} (файл изменён, отсутствует или является ссылкой)" : "") + (result.Failed > 0 ? $" · ошибок удаления: {result.Failed}" : "");
    private void RefreshOfflineView()
    {
        var checks = CurrentOfflineDownloadChecks;
        if (_section == "offline") ShowRows(_offline.Tracks(checks));
        else if (_section == "offline-playlists") ShowCatalogItems(ReadOfflinePlaylists(_lifetime.Token, checks));
        else if (_section == "offline-playlist-detail" && _viewedOfflinePlaylist is { } playlist)
        {
            var view = ReadOfflinePlaylist(playlist.Owner, playlist.Kind, CollectionTitle.Text, _lifetime.Token, checks);
            CollectionTitle.Text = view.Title;
            ShowRows(view.Rows);
        }
    }
    private async void OfflinePlaylists_Click(object sender, RoutedEventArgs e)
    {
        await ShowOfflinePlaylistsAsync();
        if (_section == "offline-playlists") await RetryDownloadQueueRestoreAsync();
    }

    private Task ShowOfflinePlaylistsAsync() => ReadOfflineViewAsync(ReadOfflinePlaylists, items =>
    {
        if (_section != "offline-playlists") SetSection("offline-playlists", "Скачанные плейлисты");
        ShowCatalogItems(items);
        SetStatus("Сохранённые плейлисты доступны без аккаунта и сети. Неполную загрузку можно продолжить из онлайн-плейлиста.");
    });

    private CatalogItem[] ReadOfflinePlaylists(CancellationToken token, OfflineCopyChecks? verifiedCopies)
        => _offline.GetPlaylists().Select(playlist =>
    {
        var status = _offline.InspectPlaylistStatus(playlist.Owner, playlist.Kind, null, token, verifiedCopies);
        return new CatalogItem(playlist.Kind, "offline-playlist", playlist.Title, status.Complete ? $"✓ Скачан · {status.Total} треков"
            : $"Скачано {status.Downloaded} из {status.Total}", playlist.Owner, playlist.Kind);
    }).ToArray();

    private Task ShowOfflinePlaylist(CatalogItem item)
    {
        if (item.Owner is not { } owner || item.PlaylistKind is not { } kind) return Task.CompletedTask;
        return ReadOfflineViewAsync((token, checks) => ReadOfflinePlaylist(owner, kind, item.Title, token, checks), view =>
        {
            if (_section != "offline-playlist-detail" || _viewedOfflinePlaylist != (owner, kind))
            {
                RememberBrowseView();
                NavigateSection("offline-playlist-detail", view.Title, remember: false);
            }
            else CollectionTitle.Text = view.Title;
            _viewedOfflinePlaylist = (owner, kind);
            ShowRows(view.Rows);
            SetOfflinePlaylistStatus(view.Status);
        });
    }

    private sealed record OfflinePlaylistView(string Title, IReadOnlyList<TrackRow> Rows, OfflinePlaylistStatus Status);

    private OfflinePlaylistView ReadOfflinePlaylist(string owner, string kind, string fallbackTitle, CancellationToken token,
        OfflineCopyChecks? verifiedCopies)
    {
        token.ThrowIfCancellationRequested();
        var title = _offline.GetPlaylists().FirstOrDefault(playlist => playlist.Owner == owner && playlist.Kind == kind)?.Title ?? fallbackTitle;
        var rows = _offline.GetPlaylistTracks(owner, kind, verifiedCopies);
        return new(title, rows, _offline.InspectPlaylistStatus(owner, kind, null, token, verifiedCopies));
    }

    private void SetOfflinePlaylistStatus(OfflinePlaylistStatus status)
        => SetStatus($"Офлайн-плейлист · доступно {status.Downloaded} из {status.Total} треков");

    private CancellationTokenSource? _offlineDownloadRefresh;
    private bool _offlineDownloadRefreshDeferred;
    internal Func<CancellationToken, Task>? OfflineCopyChecksBeforePublishForChecks { get; set; }
    private (int Account, string? Uid, OfflineCopyChecks Checks)? _offlineDownloadChecks;

    private OfflineCopyChecks? CurrentOfflineDownloadChecks
        => _offlineDownloadChecks is { } saved && saved.Account == _accountGeneration && saved.Uid == _account?.Uid ? saved.Checks : null;

    private void RememberOfflineDownloadChecks(IReadOnlyDictionary<string, OfflineCopyCheck> checks)
    {
        var merged = CurrentOfflineDownloadChecks is { } previous
            ? new Dictionary<string, OfflineCopyCheck>(previous, StringComparer.Ordinal)
            : new Dictionary<string, OfflineCopyCheck>(StringComparer.Ordinal);
        foreach (var check in checks) merged[check.Key] = check.Value;
        PublishOfflineDownloadChecks(merged);
    }

    private void PublishOfflineDownloadChecks(Dictionary<string, OfflineCopyCheck> checks)
        => _offlineDownloadChecks = checks.Count == 0 ? null : (_accountGeneration, _account?.Uid, new(checks));

    private async Task RefreshCurrentOfflineViewAfterDownloadAsync()
    {
        if (_closed || _offlineMaintenance || !_offline.IsAvailable) return;
        if (HasPendingOfflineNavigation)
        {
            _offlineDownloadRefreshDeferred = true;
            return;
        }
        if (!IsOfflineSection(_section)) return;
        _offlineDownloadRefresh?.Cancel();
        using var request = CancellationTokenSource.CreateLinkedTokenSource(_accountCts.Token);
        _offlineDownloadRefresh = request;
        var view = _viewGeneration;
        var account = _accountGeneration;
        var uid = _account?.Uid;
        var playlist = _viewedOfflinePlaylist;
        bool Current() => !_closed && !request.IsCancellationRequested && ReferenceEquals(_offlineDownloadRefresh, request)
            && !HasPendingOfflineNavigation && !_offlineMaintenance && view == _viewGeneration
            && account == _accountGeneration && uid == _account?.Uid;
        try
        {
            if (_section == "offline")
                await ReadStableOfflineViewAsync((_, checks) => _offline.Tracks(checks), rows => RefreshOfflineRows(rows, playlist: false), request.Token, Current);
            else if (_section == "offline-playlists")
                await ReadStableOfflineViewAsync(ReadOfflinePlaylists, RefreshOfflineCards, request.Token, Current);
            else if (playlist is { } local)
            {
                var title = CollectionTitle.Text;
                await ReadStableOfflineViewAsync((token, checks) => ReadOfflinePlaylist(local.Owner, local.Kind, title, token, checks), details =>
                {
                    CollectionTitle.Text = details.Title;
                    RefreshOfflineRows(details.Rows, playlist: true);
                    SetOfflinePlaylistStatus(details.Status);
                }, request.Token, Current);
            }
        }
        catch (OperationCanceledException) when (!Current()) { }
        catch (Exception error)
        {
            if (Current()) _diagnostics.LastErrorType = "OfflineDownloadRefresh:" + error.GetType().Name;
        }
        finally { if (ReferenceEquals(_offlineDownloadRefresh, request)) _offlineDownloadRefresh = null; }
    }

    private async Task<IReadOnlyDictionary<string, OfflineCopyCheck>> VerifyOfflineCopiesAsync(TrackRow[] tracks, CancellationToken token)
    {
        var checks = new Dictionary<string, OfflineCopyCheck>(StringComparer.Ordinal);
        foreach (var batch in tracks.DistinctBy(track => track.Id).Chunk(OfflineLibrary.DownloadVerificationBatchSize))
        {
            var requests = batch.Select(track => new OfflineLibrary.DownloadCacheRequest(track)).ToArray();
            using var verified = await _offline.HoldDownloadCacheBatchAsync(requests, token, verifyAllVariants: true).ConfigureAwait(false);
            for (var i = 0; i < batch.Length; i++)
            {
                var result = verified[i];
                checks.Add(batch[i].Id, new(batch[i], result.VerifiedCopies ?? [], result.RefusedCopies is { Count: > 0 }, result.RefusedCopies));
            }
        }
        return checks;
    }

    private void RefreshOfflineCards(CatalogItem[] items)
    {
        var selected = CatalogList.SelectedItem as CatalogItem;
        ShowCatalogItems(items);
        if (selected is not null)
            CatalogList.SelectedItem = CatalogList.Items.OfType<CatalogItem>()
                .FirstOrDefault(item => item.Owner == selected.Owner && item.PlaylistKind == selected.PlaylistKind);
    }

    private void RefreshOfflineRows(IReadOnlyList<TrackRow> rows, bool playlist)
    {
        var selected = TracksList.SelectedItems.OfType<TrackRow>().ToHashSet();
        var restored = new List<TrackRow>();
        if (selected.Count > 0 && playlist)
        {
            var next = rows.ToLookup(row => row.RequestId, StringComparer.Ordinal);
            foreach (var previous in _tracks.GroupBy(row => row.RequestId, StringComparer.Ordinal))
            {
                var matches = next[previous.Key];
                if (previous.Count() != matches.Count()) continue;
                foreach (var (old, current) in previous.Zip(matches))
                    if (selected.Contains(old)) restored.Add(current);
            }
        }
        else if (selected.Count > 0)
        {
            var byPath = selected.Where(row => row.LocalPath is not null).ToLookup(row => row.LocalPath!, StringComparer.OrdinalIgnoreCase);
            restored.AddRange(rows.Where(row => row.LocalPath is { } path && byPath[path].Any(old => SameSavedTrack(row, old))));
        }
        ShowRows(rows);
        if (restored.Count == 0) return;
        var visible = _view.Cast<TrackRow>().ToHashSet();
        _updatingTrackRows = true;
        try
        {
            foreach (var row in restored)
                if (visible.Contains(row)) TracksList.SelectedItems.Add(row);
        }
        finally { _updatingTrackRows = false; }
        UpdateControls();
    }

    private static bool SameSavedTrack(TrackRow row, TrackRow selected)
        => selected.LocalPath is { } path && row.Id == selected.Id && row.RequestId == selected.RequestId
            && StringComparer.OrdinalIgnoreCase.Equals(row.LocalPath, path);

    private TaskCompletionSource? _offlineMaintenanceCompletion;
    internal Func<CancellationToken, Task>? OfflineViewAfterRead { get; set; }

    private void BeginOfflineMaintenance()
    {
        _offlineMaintenance = true;
        _offlineMaintenanceCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        InvalidateOfflineViewState();
    }

    private void EndOfflineMaintenance()
    {
        var completion = _offlineMaintenanceCompletion;
        _offlineMaintenanceCompletion = null;
        _offlineMaintenance = false;
        // A watch notification can arrive while changes to the offline library
        // are paused, including a removal that exits before refreshing rows.
        _queueOfflineWatcherTimer?.Start();
        completion?.TrySetResult();
    }

    private async Task ReadOfflineViewAsync<T>(Func<CancellationToken, OfflineCopyChecks?, T> read, Action<T> show)
    {
        if (_offlineDownloadRefresh is not null) _offlineDownloadRefreshDeferred = true;
        var account = _accountGeneration;
        var uid = _account?.Uid;
        var rendered = false;
        await ReadFeatureViewAsync(async (token, isCurrent) =>
        {
            if (!_offline.IsAvailable)
            {
                if (isCurrent()) SetStatus(BrandPaths.OfflineUnavailableMessage, true);
                return;
            }
            await ReadStableOfflineViewAsync(read, result =>
            {
                show(result);
                rendered = true;
            }, token, isCurrent, recheckUnreadableCopies: true);
        }, offlineNavigation: true);
        if (HasPendingOfflineNavigation || account != _accountGeneration || uid != _account?.Uid) return;
        ResumeDeletedPlaylistFallback();
        if (!_offlineDownloadRefreshDeferred) return;
        _offlineDownloadRefreshDeferred = false;
        if (!rendered) _ = RefreshCurrentOfflineViewAfterDownloadAsync();
    }

    private async Task ReadStableOfflineViewAsync<T>(Func<CancellationToken, OfflineCopyChecks?, T> read,
        Action<T> show, CancellationToken token, Func<bool> isCurrent, bool recheckUnreadableCopies = false)
    {
        while (isCurrent())
        {
            if (_offlineMaintenanceCompletion is { } waiting)
            {
                await waiting.Task.WaitAsync(token);
                continue;
            }
            var epoch = _offlineViewEpoch;
            var checks = CurrentOfflineDownloadChecks;
            T result;
            try
            {
                await _offline.RecoverPendingAsync(token);
                if (!isCurrent()) return;
                if (epoch != _offlineViewEpoch) continue;
                // A deliberate open retries only temporary refusals. An earlier
                // projection may still be rendering after access has recovered.
                if (recheckUnreadableCopies && checks is not null)
                {
                    var unreadable = checks.Values.Where(check => check.RetryOnNavigation).Select(check => check.Track).ToArray();
                    if (unreadable.Length > 0)
                    {
                        var retried = await Task.Run(() => VerifyOfflineCopiesAsync(unreadable, token), token);
                        if (!isCurrent()) return;
                        if (epoch != _offlineViewEpoch || !ReferenceEquals(CurrentOfflineDownloadChecks, checks)) continue;
                        RememberOfflineDownloadChecks(retried);
                        checks = CurrentOfflineDownloadChecks;
                    }
                    recheckUnreadableCopies = false;
                }
                result = await Task.Run(() => read(token, checks), token);
                if (OfflineViewAfterRead is { } after) await after(token);
            }
            catch (Exception error) when (epoch != _offlineViewEpoch && error is IOException or UnauthorizedAccessException or MusicException)
            { continue; }
            if (epoch != _offlineViewEpoch || !ReferenceEquals(CurrentOfflineDownloadChecks, checks)) continue;
            if (isCurrent())
            {
                show(result);
                if (checks is not null && ReferenceEquals(CurrentOfflineDownloadChecks, checks))
                {
                    var unreadable = checks.Where(pair => pair.Value.RetryOnNavigation).ToArray();
                    if (unreadable.Length != checks.Count)
                        PublishOfflineDownloadChecks(new Dictionary<string, OfflineCopyCheck>(unreadable, StringComparer.Ordinal));
                }
            }
            return;
        }
    }
}
