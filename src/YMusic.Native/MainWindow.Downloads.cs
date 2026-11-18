using System.Windows;
using System.Windows.Controls;

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
            ApplyOfflineMarks(ReadOfflineMarks(snapshot, _lifetime.Token));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or MusicException)
        { _diagnostics.LastErrorType = "OfflineIndex:" + ex.GetType().Name; SetStatus("Не удалось прочитать отметки скачанного. Аудиофайлы сохранены.", true); }
    }
    private static string OfflineQuality(OfflineItem item) => item.Codec.StartsWith("flac", StringComparison.Ordinal) ? "Lossless · FLAC" : item.Codec.ToUpperInvariant().Replace("-MP4", "") + (item.Bitrate > 0 ? $" · {item.Bitrate} кбит/с" : "");
    private void RefreshDownloadedMarks()
    {
        InvalidateOfflineViewState();
        ApplyOfflineStates(KnownTrackRows());
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
    private (long AdmissionRevision, int AccountGeneration, string AccountUid)? _autoFavoritesRemovalRestart;
    private (long AdmissionRevision, int AccountGeneration, string AccountUid)? _clearFavoritesAdmission;
    private (string Owner, string Kind)? _viewedOfflinePlaylist;

    // These are explicitly local snapshot keys. They never enter a server API request.
    private const string LocalFavoritesOwner = "local:favorites";
    private const string LocalFavoritesKind = "liked";
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
                ? LargePlaylistStatus(playlist) : _offline.GetPlaylistStatus(playlist.Owner, playlist.Kind, playlist.Tracks);
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
    {
        if (!_offline.IsAvailable) return Task.CompletedTask;
        if (_downloadCts is not null || _offlineMaintenance || _session is null || _closed || _closeCommitInProgress) return Task.CompletedTask;
        if (automatic && (DownloadQueueRestorePending || _downloadsAutoSuspended || _downloadQueue.HasPaused)) return Task.CompletedTask;
        if (automatic && DownloadQueueBelongsToActiveAccount && _downloadQueue.Entries.Any(entry => entry.State == DownloadEntryState.Failed))
        {
            if (!_downloadQueue.AppendAutomaticFavorites(tracks, SelectedQuality) && playlist is null) return Task.CompletedTask;
            if (playlist is { Owner: LocalFavoritesOwner, Kind: LocalFavoritesKind }
                && _downloadQueuePlaylist is { Owner: LocalFavoritesOwner, Kind: LocalFavoritesKind })
                _downloadQueuePlaylistHistoryOnly = true;
            return RunDownloadQueueAsync(retryFailed: false, automaticFavoritesSnapshot: playlist);
        }
        _autoFavoritesRemovalRestart = null;
        _downloadQueue.Replace(tracks, label, automatic || playlist is { Owner: LocalFavoritesOwner, Kind: LocalFavoritesKind });
        _downloadQueuePlaylist = playlist;
        _downloadQueuePlaylistHistoryOnly = false;
        _downloadQueueQuality = SelectedQuality; _downloadQueueAutomatic = automatic;
        _downloadQueueAccountGeneration = _accountGeneration;
        _downloadQueueAccountUid = _account?.Uid;
        _downloadsAutoSuspended = false;
        BeginDownloadQueuePersistence();
        return RunDownloadQueueAsync(retryFailed: false);
    }
    private Task RunDownloadQueueAsync(bool retryFailed, PlaylistDetails? automaticFavoritesSnapshot = null,
        IReadOnlySet<DownloadEntry>? resumeEntries = null)
    {
        if (!_offline.IsAvailable) return Task.CompletedTask;
        var selected = _downloadQueue.BeginRun(retryFailed, resumeEntries);
        if (selected.Length == 0 && automaticFavoritesSnapshot is null) { RefreshDownloadCenter(); return Task.CompletedTask; }
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, _accountCts.Token);
        var appendedFavoritesOnly = selected.All(entry => entry.AutomaticFavoriteQuality is not null);
        _downloadCts = cts; _downloadIsAutomatic = _downloadQueueAutomatic || appendedFavoritesOnly;
        var appendedFavoritesCts = !_downloadIsAutomatic && selected.Any(entry => entry.AutomaticFavoriteQuality is not null)
            ? CancellationTokenSource.CreateLinkedTokenSource(cts.Token) : null;
        _appendedFavoritesCts = appendedFavoritesCts;
        _compactDownloadOperation?.Dispose(); _compactDownloadOperation = null;
        var quality = _downloadQueueQuality;
        var automatic = _downloadQueueAutomatic;
        var playlist = automaticFavoritesSnapshot ?? (appendedFavoritesOnly ? null : _downloadQueuePlaylist);
        var rememberPlaylist = automaticFavoritesSnapshot is not null || !_downloadQueuePlaylistHistoryOnly;
        var playlistQuality = automaticFavoritesSnapshot is null ? quality : SelectedQuality;
        var accountGeneration = _downloadQueueAccountGeneration;
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
            try
            {
                await PersistDownloadQueueAsync(force: true);
                await _offline.RecoverPendingAsync(cts.Token);
                cts.Token.ThrowIfCancellationRequested();
                // A single background pass uses the existing index/path/length checks,
                // never hashes audio or probes the whole library on a progress frame.
                var work = await Task.Run(() => selected.Where(entry =>
                {
                    cts.Token.ThrowIfCancellationRequested();
                    var existing = _offline.Find(entry.Track);
                    return existing is null || !((automatic || entry.AutomaticFavoriteQuality is not null) && !retryFailed
                        || AudioQualityPolicy.IsSufficient(existing.Codec, existing.Bitrate, entry.AutomaticFavoriteQuality ?? quality));
                }).ToArray(), cts.Token);
                cts.Token.ThrowIfCancellationRequested();
                if (accountGeneration != _accountGeneration) { SuspendDownloadQueueForAccountChange(); cts.Token.ThrowIfCancellationRequested(); }
                operation = new CompactDownloadOperation(work, playlist, accountGeneration, () => RefreshCompactDownloadAction());
                _compactDownloadOperation = operation;
                RefreshDownloadActivity();
                if (rememberPlaylist && playlist is not null) await _offline.RememberPlaylistAsync(playlist, playlistQuality, cts.Token);
                // The fixed worker pool follows this batch's settings snapshot. Metadata stays
                // in the snapshot; audio is streamed to disk with bounded buffers.
                await Task.WhenAll(Enumerable.Range(0, Math.Min(concurrentDownloads, selected.Length)).Select(_ => WorkerAsync()));
                cts.Token.ThrowIfCancellationRequested();
                _downloadsAutoSuspended = _downloadQueue.HasPaused;
                ReportDownloadOutcome();
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
                _downloadsAutoSuspended = true;
                if (!CanStageFavoritesDuringClear()) ClearPendingAutoFavorites();
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
                if (ReferenceEquals(_downloadCts, cts))
                { _downloadCts = null; _downloadTask = null; _downloadIsAutomatic = false; _appendedFavoritesCts = null; }
                appendedFavoritesCts?.Dispose();
                cts.Dispose();
                if (!_closed)
                {
                    UpdateControls(); RefreshDownloadedMarks(); RefreshDownloadCenter(); RefreshDownloadStorage();
                    TryStartPendingAutoFavorites();
                }
            }

            async Task WorkerAsync()
            {
                while (next < selected.Length)
                {
                    cts.Token.ThrowIfCancellationRequested();
                    var entry = selected[next++]; var track = entry.Track;
                    if (entry.SkipFavoritesDownload || entry.State == DownloadEntryState.Removed) { operation!.Exclude(entry); continue; }
                    var entryAutomatic = automatic || entry.AutomaticFavoriteQuality is not null;
                    var entryQuality = entry.AutomaticFavoriteQuality ?? quality;
                    var entryToken = entry.AutomaticFavoriteQuality is not null && appendedFavoritesCts is not null
                        ? appendedFavoritesCts.Token : cts.Token;
                    if (entryToken.IsCancellationRequested)
                    { entry.SetState(DownloadEntryState.Paused); operation!.Exclude(entry); continue; }
                    if (entryAutomatic && _deletedUntilFavoritesRefresh.Contains(track.Id))
                    { entry.SetState(DownloadEntryState.Removed); operation!.Exclude(entry); continue; }
                    try
                    {
                        entry.Begin();
                        var existing = _offline.Find(track);
                        // Automatic favorites synchronization only fills missing
                        // tracks; existing quality upgrades require a manual run.
                        if (existing is not null && (entryAutomatic && !retryFailed
                            || AudioQualityPolicy.IsSufficient(existing.Codec, existing.Bitrate, entryQuality)))
                        { operation!.Exclude(entry); entry.Complete(existing.Bytes); continue; }
                        // Recheck the presentation cohort if an indexed file changed
                        // after preflight. The downloader's admission rules stay below.
                        operation!.Include(entry);
                        if (!track.Available)
                        {
                            if (existing is not null) { operation.Exclude(entry); entry.Complete(existing.Bytes); }
                            else entry.SetState(DownloadEntryState.Unavailable, "Трек недоступен в каталоге. Обновите список и проверьте доступность в сервисе.");
                            continue;
                        }
                        var session = await EnsureSessionAsync(entryToken);
                        entryToken.ThrowIfCancellationRequested();
                        if (_downloadQueueAccountGeneration != _accountGeneration) { SuspendDownloadQueueForAccountChange(); cts.Token.ThrowIfCancellationRequested(); }
                        var audio = await _api.ResolveAudioAsync(session, track.RequestId, entryQuality, entryToken);
                        entryToken.ThrowIfCancellationRequested();
                        if (entry.SkipFavoritesDownload)
                        { entry.SetState(DownloadEntryState.Queued); operation.Exclude(entry); continue; }
                        // Metadata resolution yields: another library operation may
                        // have removed or upgraded the earlier cached candidate.
                        existing = _offline.Find(track);
                        if (existing is not null && !AudioQualityPolicy.IsBetter(audio.Codec, audio.Bitrate, existing.Codec, existing.Bitrate))
                        { operation.Exclude(entry); entry.Complete(existing.Bytes); continue; }
                        entry.SetState(DownloadEntryState.Downloading);
                        var transferred = false;
                        var progress = new DownloadProgressSink(() => { Volatile.Write(ref transferred, true); dispatch.Request(); });
                        _downloadProgress[entry] = progress;
                        var saved = await _offline.SaveAsync(track, audio, entryToken, progress);
                        // A concurrent same-key Save may supply an already committed
                        // file without any media transfer by this run.
                        if (!Volatile.Read(ref transferred)) operation.Exclude(entry);
                        entry.Complete(saved.Bytes);
                        ApplyOfflineStates(KnownTrackRows().Where(row => row.Id == track.Id));
                    }
                    catch (OperationCanceledException) when (cts.IsCancellationRequested) { throw; }
                    catch (OperationCanceledException) when (entryToken.IsCancellationRequested) { entry.SetState(DownloadEntryState.Paused); }
                    catch (Exception ex)
                    { entry.SetState(DownloadEntryState.Failed, DownloadFailure.Describe(ex)); _diagnostics.LastErrorType = "Download:" + ex.GetType().Name; }
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
            SetStatus("Музыка скачана и доступна офлайн.");
    }
    private void BeginFavoritesRefresh()
    {
        _pendingAutoFavorites = null;
        UpdatePlaylistDownloadedStatus();
    }
    private void ClearPendingAutoFavorites()
    {
        _pendingAutoFavorites = null;
        _pendingConfirmedFavorites.Clear();
    }
    private void ScheduleConfirmedFavoriteDownload(TrackRow row)
    {
        if (!_offline.IsAvailable) return;
        if (!_autoFavoritesEnabled || _session is null || _demo || _closed || !_favoriteTrackIds.Contains(row.Id)) return;
        if (_offlineMaintenance ? !CanStageFavoritesDuringClear()
            : !DownloadQueueRestorePending && !DownloadQueueRestoreCanRetry && (_downloadsAutoSuspended || _downloadQueue.HasPaused)) return;
        // Confirmed metadata is already available. A held/failed full refresh
        // must not postpone this track or erase earlier pending confirmed likes.
        _pendingConfirmedFavorites[row.Id] = row;
        _deletedUntilFavoritesRefresh.Remove(row.Id);
        TryStartPendingAutoFavorites();
    }
    private void ScheduleAutoFavoritesDownloads() => ScheduleAutoFavoritesFromSnapshot(collectionRefreshed: true);
    private void ScheduleAutoFavoritesFromSnapshot(bool collectionRefreshed)
    {
        _favoritesSnapshotReady = true;
        if (collectionRefreshed || !CanRestartAutoFavoritesAfterRemoval()) _deletedUntilFavoritesRefresh.Clear();
        UpdatePlaylistDownloadedStatus();
        if (!_offline.IsAvailable) return;
        if (!_autoFavoritesEnabled || _session is null || _demo || _closed || _offlineMaintenance) return;
        if (DownloadQueueRestorePending || DownloadQueueRestoreCanRetry)
        {
            _pendingAutoFavorites = FavoritesDownloadSnapshot();
            return;
        }
        if (collectionRefreshed && CanRestartAutoFavoritesAfterRemoval() && _downloadCts is null)
        {
            // Only this successful refresh may replace work suspended by a single
            // removal. Account changes, update pauses and new batches invalidate
            // the admission; a pre-existing user/restore pause never gets a ticket.
            if (_downloadQueue.HasPaused) _downloadQueue.Replace([], _downloadQueue.Label, favoritesBatch: true);
            _downloadsAutoSuspended = false;
            _autoFavoritesRemovalRestart = null;
        }
        if (_downloadsAutoSuspended || _downloadQueue.HasPaused) return;
        _pendingAutoFavorites = FavoritesDownloadSnapshot();
        TryStartPendingAutoFavorites();
    }
    private bool CanRestartAutoFavoritesAfterRemoval() => _autoFavoritesRemovalRestart is { } restart
        && restart.AdmissionRevision == _downloadQueueAdmissionRevision && restart.AccountGeneration == _accountGeneration
        && restart.AccountUid == _account?.Uid;
    private bool CanStageFavoritesDuringClear() => _clearFavoritesAdmission is { } clear
        && clear.AdmissionRevision == _downloadQueueAdmissionRevision && clear.AccountGeneration == _accountGeneration
        && clear.AccountUid == _account?.Uid;
    private void TryStartPendingAutoFavorites()
    {
        if (!_autoFavoritesEnabled || _downloadCts is not null || _offlineMaintenance || _session is null || _closed || _closeCommitInProgress
            || _appUpdatePreparingRestart || _startupUpdateRestartRequested || DownloadQueueRestorePending
            || _downloadsAutoSuspended || _downloadQueue.HasPaused) return;
        if (_pendingConfirmedFavorites.Count > 0)
        {
            var tracks = _pendingConfirmedFavorites.Values.ToArray();
            _pendingConfirmedFavorites.Clear();
            // This is an incremental batch, not a complete collection snapshot.
            _ = StartDownloadBatchAsync(tracks, "Автоскачивание «Мне нравится»", null, automatic: true);
            if (_downloadCts is not null) return;
        }
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
            if (_favoritesSnapshotReady) ScheduleAutoFavoritesFromSnapshot(collectionRefreshed: false);
            SetStatus($"Автоскачивание включено. Избранное скачивается после загрузки списка, до {_concurrentDownloads} треков одновременно.");
        }
        else
        {
            ClearPendingAutoFavorites();
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
        if (_closed || !_offline.IsAvailable || _offlineMaintenance) return;
        var pendingAutomaticRestart = CanRestartAutoFavoritesAfterRemoval();
        var canRestartAfterRefresh = pendingAutomaticRestart || !_downloadsAutoSuspended && !_downloadQueue.HasPaused;
        var automaticCancellation = _downloadQueueAutomatic && (_downloadQueue.Running || pendingAutomaticRestart);
        var accountGeneration = _accountGeneration; var accountUid = _account?.Uid;
        _offlineRemovalTrackId = track.Id;
        BeginOfflineMaintenance();
        if (!_closed) RefreshMaintenanceActions();
        try
        {
            await _offline.RecoverPendingAsync(_lifetime.Token);
            if (_closed || accountGeneration != _accountGeneration || accountUid != _account?.Uid) return;
            if (!SuspendDownloadQueueForAccountChange(keepQueueVisible: true)) return;
            _deletedUntilFavoritesRefresh.Add(track.Id);
            var admissionRevision = _downloadQueueAdmissionRevision;
            if (_downloadTask is { } task) await task;
            await ReleaseOfflinePlaybackAsync(track.Id);
            var result = await _offline.RemoveTrackAsync(track.Id, _lifetime.Token);
            if (!_closed && accountGeneration == _accountGeneration && accountUid == _account?.Uid
                && admissionRevision == _downloadQueueAdmissionRevision)
            {
                _downloadQueue.Removed(track.Id);
                if (DownloadQueueBelongsToActiveAccount && _downloadQueue.Entries.Any(entry => entry.State == DownloadEntryState.Failed && !entry.SkipFavoritesDownload))
                {
                    _downloadQueue.AllowExplicitActionsAfterRemoval();
                    BeginDownloadQueuePersistence();
                    _ = PersistDownloadQueueAsync(force: true);
                }
                else if (canRestartAfterRefresh && result.Preserved == 0 && result.Failed == 0 && !_lifetime.IsCancellationRequested
                    && _session is not null && accountUid is not null && (!_downloadQueue.HasPaused || automaticCancellation)
                    && !_downloadQueue.Entries.Any(entry => entry.State == DownloadEntryState.Failed))
                    _autoFavoritesRemovalRestart = (admissionRevision, accountGeneration, accountUid);
            }
            RefreshDownloadCenter(); RefreshDownloadStorage();
            RefreshOfflineView(); RefreshDownloadedMarks();
            SetStatus(RemovalStatus(result) + (_autoFavoritesEnabled && CanRestartAutoFavoritesAfterRemoval()
                ? " · автоскачивание вернёт трек после следующего обновления избранного" : ""), result.Preserved + result.Failed > 0);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex) { ShowError(ex); }
        finally
        {
            _offlineRemovalTrackId = null;
            EndOfflineMaintenance();
            if (!_closed) RefreshMaintenanceActions();
            UpdateControls(); UpdatePlaylistDownloadedStatus(); TryStartPendingAutoFavorites();
        }
    }
    private async void ClearOffline_Click(object sender, RoutedEventArgs e) => await ClearOfflineAsync();
    private async Task ClearOfflineAsync()
    {
        if (_closed || !_offline.IsAvailable || _offlineMaintenance || _cacheMaintenance || _clearOfflineConfirmationPending) return;
        _clearOfflineConfirmationPending = true;
        RefreshFullUninstallAction();
        try
        {
            await _offline.RecoverPendingAsync(_lifetime.Token);
            if (_closed || _offlineMaintenance || _cacheMaintenance) return;
            var count = _offline.Read().Count;
            if (MessageBox.Show(this, $"Удалить всю скачанную музыку ({count} сохранённых версий) и локальные снимки плейлистов?\n\nАвтоскачивание «Мне нравится» будет выключено. Посторонние и изменённые файлы останутся.", "Очистить скачанное", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { return; }
        catch (Exception ex) { ShowError(ex); return; }
        finally
        {
            _clearOfflineConfirmationPending = false;
            if (!_closed) RefreshFullUninstallAction();
        }
        await ClearOfflineAfterConfirmationAsync();
    }
    private async Task ClearOfflineAfterConfirmationAsync()
    {
        if (!_offline.IsAvailable || _offlineMaintenance || _cacheMaintenance) return;
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
        if (_section == "offline") ShowRows(_offline.Tracks());
        else if (_section == "offline-playlists") ShowCatalogItems(ReadOfflinePlaylists(_lifetime.Token));
        else if (_section == "offline-playlist-detail" && _viewedOfflinePlaylist is { } playlist)
        {
            var view = ReadOfflinePlaylist(playlist.Owner, playlist.Kind, CollectionTitle.Text, _lifetime.Token);
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

    private CatalogItem[] ReadOfflinePlaylists(CancellationToken token) => _offline.GetPlaylists().Select(playlist =>
    {
        var status = _offline.InspectPlaylistStatus(playlist.Owner, playlist.Kind, null, token);
        return new CatalogItem(playlist.Kind, "offline-playlist", playlist.Title, status.Complete ? $"✓ Скачан · {status.Total} треков"
            : $"Скачано {status.Downloaded} из {status.Total}", playlist.Owner, playlist.Kind);
    }).ToArray();

    private Task ShowOfflinePlaylist(CatalogItem item)
    {
        if (item.Owner is not { } owner || item.PlaylistKind is not { } kind) return Task.CompletedTask;
        return ReadOfflineViewAsync(token => ReadOfflinePlaylist(owner, kind, item.Title, token), view =>
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

    private OfflinePlaylistView ReadOfflinePlaylist(string owner, string kind, string fallbackTitle, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var title = _offline.GetPlaylists().FirstOrDefault(playlist => playlist.Owner == owner && playlist.Kind == kind)?.Title ?? fallbackTitle;
        var rows = _offline.GetPlaylistTracks(owner, kind);
        return new(title, rows, _offline.InspectPlaylistStatus(owner, kind, null, token));
    }

    private void SetOfflinePlaylistStatus(OfflinePlaylistStatus status)
        => SetStatus($"Офлайн-плейлист · доступно {status.Downloaded} из {status.Total} треков");

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
        completion?.TrySetResult();
    }

    private Task ReadOfflineViewAsync<T>(Func<CancellationToken, T> read, Action<T> show)
        => ReadFeatureViewAsync(async (token, isCurrent) =>
        {
            if (!_offline.IsAvailable)
            {
                if (isCurrent()) SetStatus(BrandPaths.OfflineUnavailableMessage, true);
                return;
            }
            while (isCurrent())
            {
                if (_offlineMaintenanceCompletion is { } waiting)
                {
                    await waiting.Task.WaitAsync(token);
                    continue;
                }
                var epoch = _offlineViewEpoch;
                T result;
                try
                {
                    await _offline.RecoverPendingAsync(token);
                    if (!isCurrent()) return;
                    if (epoch != _offlineViewEpoch) continue;
                    result = await Task.Run(() => read(token), token);
                    if (OfflineViewAfterRead is { } after) await after(token);
                }
                catch (Exception error) when (epoch != _offlineViewEpoch && error is IOException or UnauthorizedAccessException or MusicException)
                { continue; }
                if (epoch != _offlineViewEpoch) continue;
                if (isCurrent()) show(result);
                return;
            }
        });
}
