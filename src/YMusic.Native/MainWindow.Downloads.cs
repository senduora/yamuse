using System.Windows;
using System.Windows.Controls;

namespace YMusic.Native;

public partial class MainWindow
{
    private void ApplyOfflineStates(IEnumerable<TrackRow> tracks)
    {
        try
        {
            foreach (var track in tracks)
            {
                var saved = _offline.Find(track);
                var wasDownloaded = track.IsDownloaded;
                track.DownloadedPath = saved is null ? null : _offline.ResolvePath(saved.FileName);
                track.DownloadedQuality = saved is null ? null : OfflineQuality(saved);
                if (wasDownloaded != track.IsDownloaded) QueueDownloadedSortRefresh();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or MusicException)
        { _diagnostics.LastErrorType = "OfflineIndex:" + ex.GetType().Name; SetStatus("Не удалось прочитать отметки скачанного. Аудиофайлы сохранены.", true); }
    }
    private static string OfflineQuality(OfflineItem item) => item.Codec.StartsWith("flac", StringComparison.Ordinal) ? "Lossless · FLAC" : item.Codec.ToUpperInvariant().Replace("-MP4", "") + (item.Bitrate > 0 ? $" · {item.Bitrate} кбит/с" : "");
    private void RefreshDownloadedMarks()
    {
        ApplyOfflineStates(_tracks.Concat(_favoritesCache).Concat(_waveTracks).Concat(_queue).Concat(_catalogItems.Select(i => i.Track).OfType<TrackRow>()).Distinct());
        if (_current is not null) ApplyOfflineStates([_current]);
        UpdatePlaylistDownloadedStatus();
    }
    private Task? _downloadTask;
    private bool _downloadIsAutomatic;
    private bool _offlineMaintenance;
    private bool _favoritesSnapshotReady;
    private PlaylistDetails? _pendingAutoFavorites;
    private readonly HashSet<string> _deletedUntilFavoritesRefresh = new(StringComparer.Ordinal);
    private (string Owner, string Kind)? _viewedOfflinePlaylist;

    // These are explicitly local snapshot keys. They never enter a server API request.
    private const string LocalFavoritesOwner = "local:favorites";
    private const string LocalFavoritesKind = "liked";
    private PlaylistDetails FavoritesDownloadSnapshot() => new(LocalFavoritesOwner, LocalFavoritesKind,
        "Мне нравится", "Локальный снимок избранного", 0, _favoritesCache.ToArray());
    private PlaylistDetails? CurrentDownloadPlaylist() => _section == "favorites" ? FavoritesDownloadSnapshot() : _activePlaylist;
    private void UpdatePlaylistDownloadedStatus()
    {
        if (DownloadPlaylistButton is null) return;
        var isFavorites = _section == "favorites";
        FavoritesWaveButton.Visibility = isFavorites ? Visibility.Visible : Visibility.Collapsed;
        FavoritesWaveButton.IsEnabled = _session is not null;
        AutoFavoritesCheckBox.Visibility = isFavorites ? Visibility.Visible : Visibility.Collapsed;
        if (isFavorites && _session is not null) PlaylistActions.Visibility = Visibility.Visible;
        if (CurrentDownloadPlaylist() is not { } playlist) { DownloadPlaylistButton.Visibility = Visibility.Collapsed; return; }
        var favorites = _section == "favorites";
        DownloadPlaylistButton.Visibility = Visibility.Visible;
        DownloadPlaylistButton.IsEnabled = !_offlineMaintenance && _downloadCts is null && _session is not null && (!favorites || _favoritesSnapshotReady);
        try
        {
            var state = _offline.GetPlaylistStatus(playlist.Owner, playlist.Kind, playlist.Tracks);
            var title = favorites ? "Мне нравится" : "плейлист";
            DownloadPlaylistButton.Content = state.Complete ? $"✓ Скачан · {state.Total}" : state.Downloaded > 0 ? $"↓ Докачать · {state.Downloaded}/{state.Total}" : $"↓ Скачать {title} целиком";
            PlaylistDownloadText.Text = favorites && !_favoritesSnapshotReady ? "Дождитесь полной загрузки избранного" : state.Complete ? "Все треки доступны офлайн" : state.HasSnapshot ? $"Скачано {state.Downloaded} из {state.Total}" + (state.ChangedSinceSnapshot ? " · список изменился" : "") : "";
        }
        catch (Exception ex) when (ex is MusicException or IOException or UnauthorizedAccessException) { PlaylistDownloadText.Text = "Не удалось прочитать статус скачивания"; }
    }
    private async void DownloadPlaylist_Click(object sender, RoutedEventArgs e) => await DownloadPlaylistAsync();
    private Task DownloadPlaylistAsync()
    {
        if (CurrentDownloadPlaylist() is not { } playlist || _session is null || _downloadCts is not null || _offlineMaintenance
            || _section == "favorites" && !_favoritesSnapshotReady) return Task.CompletedTask;
        return StartDownloadBatchAsync(playlist.Tracks, playlist.Title, playlist, automatic: false);
    }
    private Task StartDownloadBatchAsync(IEnumerable<TrackRow> tracks, string label, PlaylistDetails? playlist, bool automatic)
    {
        if (_downloadCts is not null || _offlineMaintenance || _session is null || _closed) return Task.CompletedTask;
        if (automatic && (_downloadsAutoSuspended || _downloadQueue.HasPaused)) return Task.CompletedTask;
        _downloadQueue.Replace(tracks, label, playlist is { Owner: LocalFavoritesOwner, Kind: LocalFavoritesKind });
        _downloadQueuePlaylist = playlist;
        _downloadQueueQuality = SelectedQuality; _downloadQueueAutomatic = automatic;
        _downloadQueueAccountGeneration = _accountGeneration;
        _downloadQueueAccountUid = _account?.Uid;
        _downloadsAutoSuspended = false;
        return RunDownloadQueueAsync(retryFailed: false);
    }
    private Task RunDownloadQueueAsync(bool retryFailed)
    {
        var selected = _downloadQueue.BeginRun(retryFailed);
        if (selected.Length == 0) { RefreshDownloadCenter(); return Task.CompletedTask; }
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, _accountCts.Token);
        _downloadCts = cts; _downloadIsAutomatic = _downloadQueueAutomatic;
        var quality = _downloadQueueQuality;
        var concurrentDownloads = Math.Clamp(_concurrentDownloads, 1, 10);
        _offline.ConcurrentDownloads = concurrentDownloads;
        _downloadTask = RunBatchAsync();
        return _downloadTask;

        async Task RunBatchAsync()
        {
            // Publish _downloadTask before any completion or auto-follow-up can run.
            await Task.Yield();
            UpdateControls(); UpdatePlaylistDownloadedStatus(); RefreshDownloadCenter();
            _downloadProgressTimer?.Start();
            var next = 0;
            try
            {
                cts.Token.ThrowIfCancellationRequested();
                if (_downloadQueuePlaylist is { } playlist) await _offline.RememberPlaylistAsync(playlist, quality, cts.Token);
                // The fixed worker pool follows this batch's settings snapshot. Metadata stays
                // in the snapshot; audio is streamed to disk with bounded buffers.
                await Task.WhenAll(Enumerable.Range(0, Math.Min(concurrentDownloads, selected.Length)).Select(_ => WorkerAsync()));
                _downloadsAutoSuspended = _downloadQueue.HasPaused;
                if (_downloadQueue.Entries.Any(entry => entry.State == DownloadEntryState.Failed)) _pendingAutoFavorites = null;
                SetStatus(_downloadQueue.Summary, _downloadQueue.Entries.Any(entry => entry.State is DownloadEntryState.Failed or DownloadEntryState.Unavailable));
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
                _downloadsAutoSuspended = true; _pendingAutoFavorites = null;
                if (!_closed && _session is not null && !_offlineMaintenance) SetStatus("Загрузка на паузе. Откройте «Загрузки», чтобы продолжить очередь.");
            }
            catch (Exception ex)
            {
                _pendingAutoFavorites = null;
                _diagnostics.LastErrorType = "DownloadQueue:" + ex.GetType().Name;
                foreach (var entry in selected.Where(entry => entry.Active || entry.State == DownloadEntryState.Queued))
                    entry.SetState(DownloadEntryState.Failed, DownloadFailure.Describe(ex));
                if (!_closed) SetStatus(DownloadFailure.Describe(ex), true);
            }
            finally
            {
                _downloadProgressTimer?.Stop(); _downloadProgress.Clear();
                if (_downloadQueueAccountGeneration != _accountGeneration) _downloadQueue.Suspend();
                _downloadQueue.EndRun(cts.IsCancellationRequested);
                if (ReferenceEquals(_downloadCts, cts)) { _downloadCts = null; _downloadTask = null; _downloadIsAutomatic = false; }
                cts.Dispose(); UpdateControls(); RefreshDownloadedMarks(); RefreshDownloadCenter(); RefreshDownloadStorage();
                TryStartPendingAutoFavorites();
            }

            async Task WorkerAsync()
            {
                while (next < selected.Length)
                {
                    cts.Token.ThrowIfCancellationRequested();
                    var entry = selected[next++]; var track = entry.Track;
                    if (entry.SkipFavoritesDownload) continue;
                    if (_downloadQueueAutomatic && _deletedUntilFavoritesRefresh.Contains(track.Id))
                    { entry.SetState(DownloadEntryState.Removed); continue; }
                    try
                    {
                        entry.Begin(); RefreshDownloadCenter();
                        if (_offline.Find(track) is { } existing) { entry.Complete(existing.Bytes); continue; }
                        if (!track.Available)
                        { entry.SetState(DownloadEntryState.Unavailable, "Трек недоступен в каталоге. Обновите список и проверьте доступность в сервисе."); continue; }
                        var session = await EnsureSessionAsync(cts.Token);
                        cts.Token.ThrowIfCancellationRequested();
                        if (_downloadQueueAccountGeneration != _accountGeneration) { SuspendDownloadQueueForAccountChange(); cts.Token.ThrowIfCancellationRequested(); }
                        var audio = await _api.ResolveAudioAsync(session, track.RequestId, quality, cts.Token);
                        cts.Token.ThrowIfCancellationRequested();
                        entry.SetState(DownloadEntryState.Downloading);
                        var progress = new DownloadProgressSink(); _downloadProgress[entry] = progress;
                        var saved = await _offline.SaveAsync(track, audio, cts.Token, progress);
                        entry.Complete(saved.Bytes);
                        ApplyOfflineStates(_tracks.Concat(_favoritesCache).Concat(_waveTracks).Concat(_queue).Concat(_catalogItems.Select(i => i.Track).OfType<TrackRow>()).Where(t => t.Id == track.Id).Distinct());
                        if (_current?.Id == track.Id) ApplyOfflineStates([_current]);
                    }
                    catch (OperationCanceledException) when (cts.IsCancellationRequested) { throw; }
                    catch (Exception ex)
                    { entry.SetState(DownloadEntryState.Failed, DownloadFailure.Describe(ex)); _diagnostics.LastErrorType = "Download:" + ex.GetType().Name; }
                    finally { _downloadProgress.Remove(entry); RefreshDownloadCenter(); }
                }
            }
        }
    }
    private void BeginFavoritesRefresh()
    {
        _favoritesSnapshotReady = false;
        _pendingAutoFavorites = null;
        UpdatePlaylistDownloadedStatus();
    }
    private void ScheduleAutoFavoritesDownloads()
    {
        _favoritesSnapshotReady = true;
        _deletedUntilFavoritesRefresh.Clear();
        UpdatePlaylistDownloadedStatus();
        if (!_autoFavoritesEnabled || _session is null || _demo || _closed || _offlineMaintenance || _downloadsAutoSuspended || _downloadQueue.HasPaused) return;
        _pendingAutoFavorites = FavoritesDownloadSnapshot();
        TryStartPendingAutoFavorites();
    }
    private void TryStartPendingAutoFavorites()
    {
        if (!_autoFavoritesEnabled || _pendingAutoFavorites is not { } snapshot || _downloadCts is not null || _offlineMaintenance || _session is null || _closed || _downloadsAutoSuspended || _downloadQueue.HasPaused) return;
        _pendingAutoFavorites = null; // No automatic retry loop after errors or cancellation.
        _ = StartDownloadBatchAsync(snapshot.Tracks, "Автоскачивание «Мне нравится»", snapshot, automatic: true);
    }
    private void AutoFavorites_Changed(object sender, RoutedEventArgs e)
    {
        if (!_preferencesReady) return;
        _autoFavoritesEnabled = AutoFavoritesCheckBox.IsChecked == true;
        SavePreferences();
        if (_autoFavoritesEnabled)
        {
            if (_favoritesSnapshotReady) ScheduleAutoFavoritesDownloads();
            SetStatus($"Автоскачивание включено. Избранное скачивается после загрузки списка, до {_concurrentDownloads} треков одновременно.");
        }
        else
        {
            _pendingAutoFavorites = null;
            if (_downloadIsAutomatic) PauseDownloadQueue();
            SetStatus("Автоскачивание выключено. Уже сохранённая музыка останется офлайн.");
        }
    }
    private async void DeleteOfflineTrack_Click(object sender, RoutedEventArgs e)
    {
        if (TracksList.SelectedItem is not TrackRow track || _offlineMaintenance) return;
        _offlineMaintenance = true;
        _deletedUntilFavoritesRefresh.Add(track.Id);
        try
        {
            SuspendDownloadQueueForAccountChange();
            if (_downloadTask is { } task) await task;
            ReleaseOfflinePlayback(track.Id);
            var result = await _offline.RemoveTrackAsync(track.Id, _lifetime.Token);
            _downloadQueue.Removed(track.Id); RefreshDownloadCenter(); RefreshDownloadStorage();
            RefreshOfflineView(); RefreshDownloadedMarks();
            SetStatus(RemovalStatus(result) + (_autoFavoritesEnabled ? " · автоскачивание вернёт трек после следующего обновления избранного" : ""), result.Preserved + result.Failed > 0);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex) { ShowError(ex); }
        finally { _offlineMaintenance = false; UpdateControls(); UpdatePlaylistDownloadedStatus(); TryStartPendingAutoFavorites(); }
    }
    private async void ClearOffline_Click(object sender, RoutedEventArgs e) => await ClearOfflineAsync();
    private async Task ClearOfflineAsync()
    {
        if (_offlineMaintenance) return;
        try
        {
            var count = _offline.Read().Count;
            if (MessageBox.Show(this, $"Удалить всю скачанную музыку ({count} сохранённых версий) и локальные снимки плейлистов?\n\nАвтоскачивание «Мне нравится» будет выключено. Посторонние и изменённые файлы останутся.", "Очистить скачанное", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
            await ClearOfflineAfterConfirmationAsync();
        }
        catch (Exception ex) { ShowError(ex); }
    }
    private async Task ClearOfflineAfterConfirmationAsync()
    {
        if (_offlineMaintenance) return;
        _offlineMaintenance = true; _pendingAutoFavorites = null;
        try
        {
            _autoFavoritesEnabled = false; AutoFavoritesCheckBox.IsChecked = false; SavePreferences();
            SuspendDownloadQueueForAccountChange();
            if (_downloadTask is { } task) await task;
            ReleaseOfflinePlayback(null);
            var result = await _offline.ClearAsync(_lifetime.Token);
            _downloadQueue.Removed(null); RefreshDownloadCenter(); RefreshDownloadStorage();
            RefreshOfflineView(); RefreshDownloadedMarks();
            SetStatus(RemovalStatus(result) + " · автоскачивание выключено", result.Preserved + result.Failed > 0);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex) { ShowError(ex); }
        finally { _offlineMaintenance = false; UpdateControls(); UpdatePlaylistDownloadedStatus(); }
    }
    private void ReleaseOfflinePlayback(string? trackId)
    {
        var paths = _offline.Read().Where(i => trackId is null || i.Id == trackId)
            .Select(i => _offline.ResolvePath(i.FileName)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (_player.PreparedLocalPath is { } prepared && paths.Contains(prepared)) CancelNextPreparation();
        if (_player.FadingLocalPath is { } fading && paths.Contains(fading)) _player.FinishTransition();
        var source = _player.SourceLocalPath;
        if (source is null) return;
        // NativePlayer also exposes a local temp buffer for remote playback.
        // Only an exact indexed file that this operation removes releases playback.
        var affected = paths.Contains(source);
        if (!affected) return;
        _playbackGeneration++; _pendingSeek = null; _seekDragging = false;
        _playCts?.Cancel(); _player.Stop(); _progressTimer.Stop(); _loadingStream = false;
        if (_current is not null) _current.IsPlaying = false;
        HighlightTrack(null);
        if (_current?.LocalPath is not null)
        {
            _current = null; TrackTitle.Text = "Выберите трек"; TrackArtist.Text = "Двойной щелчок или кнопка «Слушать»"; QualityText.Text = "";
        }
        UpdateProgress(); UpdateControls();
    }
    private static string RemovalStatus(OfflineRemovalResult result) => $"Удалено аудиофайлов: {result.Removed}" + (result.Preserved > 0 ? $" · пропущено: {result.Preserved} (файл изменён, отсутствует или является ссылкой)" : "") + (result.Failed > 0 ? $" · ошибок удаления: {result.Failed}" : "");
    private void RefreshOfflineView()
    {
        if (_section == "offline") ShowRows(_offline.Tracks());
        else if (_section == "offline-playlist-detail" && _viewedOfflinePlaylist is { } p) ShowRows(_offline.GetPlaylistTracks(p.Owner, p.Kind));
    }
    private void OfflinePlaylists_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            SetSection("offline-playlists", "Скачанные плейлисты");
            ShowCatalogItems(_offline.GetPlaylists().Select(playlist =>
            {
                var status = _offline.GetPlaylistStatus(playlist.Owner, playlist.Kind);
                return new CatalogItem(playlist.Kind, "offline-playlist", playlist.Title, status.Complete ? $"✓ Скачан · {status.Total} треков" : $"Скачано {status.Downloaded} из {status.Total}", playlist.Owner, playlist.Kind);
            }));
            SetStatus("Сохранённые плейлисты доступны без аккаунта и сети. Неполную загрузку можно продолжить из онлайн-плейлиста.");
        }
        catch (Exception ex) { ShowError(ex); }
    }
    private void ShowOfflinePlaylist(CatalogItem item)
    {
        if (item.Owner is null || item.PlaylistKind is null) return;
        SetSection("offline-playlist-detail", item.Title);
        _viewedOfflinePlaylist = (item.Owner, item.PlaylistKind);
        ShowRows(_offline.GetPlaylistTracks(item.Owner, item.PlaylistKind));
        var status = _offline.GetPlaylistStatus(item.Owner, item.PlaylistKind);
        SetStatus($"Офлайн-плейлист · доступно {status.Downloaded} из {status.Total} треков");
    }
}
