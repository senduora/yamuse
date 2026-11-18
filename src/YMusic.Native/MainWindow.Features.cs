using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace YMusic.Native;

public partial class MainWindow
{
    private readonly List<TrackRow> _favoritesCache = [];
    private readonly List<TrackRow> _waveTracks = [];
    private readonly Dictionary<TrackRow, (string SessionId, string BatchId, WaveMode Mode, string? TrackSeedId, string? AlbumSeedId)> _waveSources = new();
    private readonly ConditionalWeakTable<TrackRow, object> _waveStations = new();
    private readonly OfflineLibrary _offline;
    private CancellationTokenSource? _featureCts;
    private CancellationTokenSource? _downloadCts;
    private string _section = "favorites";
    private bool _featuresReady;
    private RadioBatch? _waveBatch;
    private WaveMode _waveViewMode;
    private bool _waveStarting;
    private CancellationTokenSource? _waveStartCts;
    private long _waveStartQueueVersion;
    private WaveListening? _waveListening;
    private Task _feedbackTail = Task.CompletedTask;
    private int _accountGeneration;
    private int _playbackGeneration;
    private int _viewGeneration;
    private int _pendingFeedback;
    private bool _advancingWave;
    private Task? _waveExtensionTask;
    private int _waveExtensionPlaybackGeneration;
    private long _wavePauseRevision;
    private CancellationTokenSource? _pendingWaveAdvance;
    private bool PlaybackTransportActive => _player.IsPlaying || _automaticWaveCts is not null || _pendingWaveAdvance is not null
        || _waveStartCts is { IsCancellationRequested: false };
    private AudioQuality SelectedQuality => QualityCombo.SelectedIndex switch { 2 => AudioQuality.Economical, 1 => AudioQuality.Optimal, _ => AudioQuality.Excellent };
    private sealed record WaveListening(TrackRow Track, string SessionId, string BatchId, Stopwatch Clock);

    private void InitializeFeatures()
    {
        CatalogList.ItemsSource = _catalogItems;
        InitializeTrackWaveActions();
        if (!_offline.IsAvailable)
        {
            foreach (var button in new[] { OfflineNav, OfflinePlaylistsNav, DownloadCenterNav })
            {
                button.IsEnabled = false;
                button.ToolTip = BrandPaths.OfflineUnavailableMessage;
                ToolTipService.SetShowOnDisabled(button, true);
            }
            SettingsOfflineLocation.Text = BrandPaths.OfflineUnavailableMessage;
            SettingsOpenMusicFolder.IsEnabled = SettingsClearOffline.IsEnabled = SettingsAutoFavorites.IsEnabled = SettingsDownloads.IsEnabled = false;
            AutoFavoritesCheckBox.IsEnabled = false;
        }
        _featuresReady = true;
    }
    private void Favorites_Click(object sender, RoutedEventArgs e)
    {
        SetSection("favorites", "Мне нравится");
        ShowRows(_favoritesCache);
        SetStatus(_session is null ? "Войдите в аккаунт, чтобы загрузить избранное" : "Избранное · ваша коллекция");
        RefreshFavoritesOnOpen();
    }
    private async void Wave_Click(object sender, RoutedEventArgs e) => await OpenWaveAsync(WaveMode.Personal);
    private async void Offline_Click(object sender, RoutedEventArgs e)
    {
        await ShowOffline();
        if (_section == "offline") await RetryDownloadQueueRestoreAsync();
    }
    private static bool IsOfflineSection(string section) => section is "offline" or "offline-playlists" or "offline-playlist-detail";
    private void SetSection(string section, string title) => NavigateSection(section, title, remember: true);
    private void NavigateSection(string section, string title, bool remember)
    {
        if (remember && !_restoringBrowse && _featuresReady && section != _section
            && (_account is not null || IsOfflineSection(section) || IsOfflineSection(_section))) RememberBrowseView();
        ApplySection(section, title, closeSettings: true);
    }

    private void ApplySection(string section, string title, bool closeSettings)
    {
        if (closeSettings) SetSettingsVisible(false);
        SetLyricsVisible(false);
        _featureCts?.Cancel();
        CancelArtistMembershipRead();
        HideCatalog();
        _viewedOfflinePlaylist = null;
        HideCollectionHero();
        _section = section;
        FitTrackColumns();
        CollectionOptionsPopup.IsOpen = false;
        CollectionToolbar.Visibility = section is "search" or "download-center" ? Visibility.Collapsed : Visibility.Visible;
        CollectionSummary.Visibility = section is "search" or "download-center" ? Visibility.Collapsed : Visibility.Visible;
        TrackFilterPanel.Visibility = section is "search" or "download-center" ? Visibility.Collapsed : Visibility.Visible;
        PlaylistDownloadText.Text = "";
        UpdateMainContentVisibility();
        _viewGeneration++;
        QueueActions.Visibility = Visibility.Collapsed;
        CollectionTitle.Text = title;
        FilterBox.Clear(); _query = "";
        FilterBox.IsEnabled = _session is not null || IsOfflineSection(section) || _demo;
        RefreshButton.Visibility = _session is not null || IsOfflineSection(section) ? Visibility.Visible : Visibility.Collapsed;
        RefreshButton.ToolTip = section == "wave" ? "Новая волна" : "Обновить раздел";
        FilterHint.Text = section switch { "favorites" => "Поиск в избранном", "offline" => "Поиск в скачанном", "wave" => "Поиск в очереди волны", _ => "Поиск в этом списке" };
        var active = (Brush)FindResource("AccentSoftBrush");
        FavoritesNav.Background = section == "favorites" ? active : Brushes.Transparent;
        WaveNav.Background = section == "wave" ? active : Brushes.Transparent;
        OfflineNav.Background = section == "offline" ? active : Brushes.Transparent;
        foreach (var (name, button) in new (string, Button)[] { ("home", HomeNav), ("wave", WaveNav), ("collection-wave", CollectionWaveNav), ("favorites", FavoritesNav), ("offline", OfflineNav), ("download-center", DownloadCenterNav), ("search", SearchNav), ("playlists", PlaylistsNav), ("albums", AlbumsNav), ("artists", ArtistsNav), ("queue", QueueNav), ("offline-playlists", OfflinePlaylistsNav), ("queue-history", QueueHistoryNav) })
        {
            var selected = section == name || section == "home-shelf" && name == "home" || section == "playlist-detail" && name == "playlists" || section == "album-detail" && name == "albums" || section is "artist-detail" or "artist-tracks" && name == "artists" || section == "offline-playlist-detail" && name == "offline-playlists";
            if (name is "wave" or "collection-wave") selected = section == "wave" && (_waveViewMode == WaveMode.Collection ? name == "collection-wave" : _waveViewMode == WaveMode.Personal && name == "wave");
            button.Background = selected ? active : Brushes.Transparent;
            button.Foreground = selected ? (Brush)FindResource("AccentTextBrush") : ThemeManager.Brush("#B8BAC3");
        }
        UpdatePlaylistDownloadedStatus();
        UpdateBrowseButtons();
        ApplyListSorting(false);
        UpdateControls();
        if (!_settingsOpen) Motion.Enter(CurrentContent());
    }
    private bool _updatingTrackRows;
    private void ShowRows(IEnumerable<TrackRow> rows)
    {
        _catalogShowing = false; CatalogList.Visibility = Visibility.Collapsed; TracksList.Visibility = Visibility.Visible;
        FilterBox.IsEnabled = _session is not null || IsOfflineSection(_section) || _demo;
        PlaySelectionButton.Content = "▶  Слушать";
        var snapshot = rows.ToArray();
        if (_section == "favorites" && _libraryCts is not null)
        { ApplyTrackLikeStates(snapshot); if (snapshot.Length > 0) QueueOfflineMarks(snapshot); }
        else ApplyOfflineStates(snapshot);
        foreach (var row in snapshot) row.IsPlaying = _player.HasSource && _current?.RequestId == row.RequestId;
        _updatingTrackRows = true;
        try
        {
            using (_view.DeferRefresh())
            {
                _tracks.ReplaceAll(snapshot);
                ApplyListSorting(false);
            }
        }
        finally { _updatingTrackRows = false; }
        UpdateCount();
        Motion.Enter(TracksList);
        PlaybackContextViewShown();
    }
    private Task ShowOffline() => ReadOfflineViewAsync(_ => _offline.Tracks(), rows =>
    {
        if (_section != "offline") SetSection("offline", "Скачанное");
        ShowRows(rows);
        SetStatus("Офлайн-библиотека · Документы\\Yamuse\\Music");
    });
    private async Task StartWaveViewAsync(bool renew)
    {
        if (_session is null) { SetStatus("Войдите в Яндекс, чтобы запустить персональную волну."); return; }
        CancelPendingWavePlayback();
        var mode = _waveViewMode;
        var seed = _waveSeedTrack?.Id;
        var albumSeed = _waveSeedAlbum?.Id;
        SetSection("wave", WaveViewTitle);
        if (!renew && IsCurrentWaveView && _waveTracks.Count > 0) { ShowRows(_waveTracks); UpdateWavePresentation(); return; }
        _featureCts?.Cancel();
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, _accountCts.Token);
        _featureCts = _waveStartCts = cts;
        var request = ++_waveRequestGeneration; var account = _accountGeneration;
        var uid = _account?.Uid;
        var playback = _playbackGeneration;
        _waveStartQueueVersion = _playbackQueue.Version;
        bool Current() => !_closed && !cts.IsCancellationRequested && account == _accountGeneration && uid == _account?.Uid
            && request == _waveRequestGeneration && ReferenceEquals(_featureCts, cts) && ReferenceEquals(_waveStartCts, cts);
        bool CanAccept() => Current() && playback == _playbackGeneration && _waveStartQueueVersion == _playbackQueue.Version;
        _waveStarting = true; Busy(true); UpdateControls();
        try
        {
            if (!CanAccept()) return;
            SetStatus(mode switch { WaveMode.Track => "Подбираем волну по треку…", WaveMode.Album => "Подбираем волну по альбому…", _ => "Подбираем треки Моей волны…" });
            var session = await EnsureSessionAsync(cts.Token);
            if (!CanAccept()) return;
            var candidate = await _api.RequestWaveAsync(session, cts.Token, mode, mode == WaveMode.Track ? seed : null,
                mode == WaveMode.Album ? albumSeed : null);
            if (!CanAccept()) return;
            var playable = WavePlayableTracks(candidate.Batch.Tracks);
            if (!CanAccept()) return;
            if (playable.Length == 0)
            {
                SetStatus("Сервис не выдал доступных треков. Нажмите «Новая волна».", true);
                return;
            }
            var batch = _api.AcceptWave(candidate, cts.Token);
            _waveStartCts = null;
            _waveTracks.Clear();
            AddWaveBatch(batch);
            QueueWaveFeedback(batch.SessionId, batch.BatchId, "radioStarted", null, 0);
            _playbackQueue.SetItems(playable);
            if (_playbackQueue.Current is { } first) await PlayTrackAsync(first);
            if (!_closed && _section == "wave" && IsCurrentWaveView && ReferenceEquals(batch.Station, _waveBatch?.Station))
                ShowRows(_waveTracks);
        }
        catch (Exception ex) { if (CanAccept()) ShowError(ex); }
        finally
        {
            if (ReferenceEquals(_featureCts, cts)) _featureCts = null;
            if (ReferenceEquals(_waveStartCts, cts)) _waveStartCts = null;
            var finishingCurrentStart = request == _waveRequestGeneration;
            if (finishingCurrentStart) _waveStarting = false;
            cts.Dispose(); Busy(false);
            if (!_closed)
            {
                if (finishingCurrentStart) UpdateControls();
                else UpdateWavePresentation();
            }
        }
    }
    private void AddWaveBatch(RadioBatch batch)
    {
        _waveBatch = batch;
        foreach (var row in batch.Tracks)
        {
            _waveTracks.Add(row);
            _waveSources[row] = (batch.SessionId, batch.BatchId, batch.Mode, batch.TrackSeedId, batch.AlbumSeedId);
            if (batch.Station is { } station) _waveStations.AddOrUpdate(row, station);
        }
        while (_waveTracks.Count > 300)
        {
            var old = _waveTracks[0]; _waveTracks.RemoveAt(0);
            if (!_queue.Contains(old) && !ReferenceEquals(_current, old)) _waveSources.Remove(old);
        }
        UpdateWavePresentation();
    }
    private bool IsContinuableWaveTrack(TrackRow? track) => track is not null && _waveBatch?.Station is { } station
        && _waveStations.TryGetValue(track, out var owner) && ReferenceEquals(station, owner);

    private TrackRow[] WavePlayableTracks(IReadOnlyList<TrackRow> tracks)
    {
        var unavailable = tracks.Where(track => !track.CanPlay).ToArray();
        if (unavailable.Length > 0) ApplyOfflineStates(unavailable);
        return tracks.Where(track => track.CanPlay).ToArray();
    }

    private bool CancelExplicitWaveStart()
    {
        if (_waveStartCts is not { } start) return false;
        _waveStartCts = null;
        _waveStarting = false;
        start.Cancel();
        return true;
    }

    private Task ExtendWaveQueueAsync()
    {
        if (_waveExtensionTask is { IsCompleted: false } pending) return pending;
        if (_advancingWave || _waveBatch is not { } batch || _session is null
            || !ReferenceEquals(_playbackQueue.Current, _current) || !IsContinuableWaveTrack(_current)) return Task.CompletedTask;
        _waveExtensionPlaybackGeneration = _playbackGeneration;
        return _waveExtensionTask = ExtendWaveQueueCoreAsync(batch);
    }

    private async Task ExtendWaveQueueCoreAsync(RadioBatch previousBatch)
    {
        _advancingWave = true; UpdateControls();
        var account = _accountGeneration;
        var uid = _account?.Uid;
        var queue = _playbackQueue.ReplacementVersion;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, _accountCts.Token);
        bool OwnsWave() => !cts.IsCancellationRequested && !_closed && _session is not null
            && account == _accountGeneration && uid == _account?.Uid
            && queue == _playbackQueue.ReplacementVersion && ReferenceEquals(_waveBatch, previousBatch)
            && IsContinuableWaveTrack(_playbackQueue.Current);
        try
        {
            var session = await EnsureSessionAsync(cts.Token);
            // A batch can contain only unavailable tracks. Advance the service's
            // received list, but limit consecutive requests while it stays empty.
            const int maximumBatches = 3;
            for (var attempt = 0; attempt < maximumBatches && OwnsWave(); attempt++)
            {
                var batch = await _api.MoreWaveAsync(session, previousBatch, cts.Token);
                if (!OwnsWave()) return;
                var playable = WavePlayableTracks(batch.Tracks);
                if (!OwnsWave()) return;
                if (playable.Length > 0)
                {
                    var version = _playbackQueue.Version;
                    _playbackQueue.Append(playable);
                    // Passive recommendations are compatible with the explicit
                    // request. Keep any earlier user edit visible to its guard.
                    if (_waveStartCts is { IsCancellationRequested: false } && _waveStartQueueVersion == version)
                        _waveStartQueueVersion = _playbackQueue.Version;
                }
                var renewed = batch.SessionId != previousBatch.SessionId;
                previousBatch = batch;
                AddWaveBatch(batch);
                if (renewed) QueueWaveFeedback(batch.SessionId, batch.BatchId, "radioStarted", null, 0);
                RefreshPlaybackView();
                foreach (var old in _waveSources.Keys.Where(row => !_waveTracks.Contains(row) && !_queue.Contains(row) && !ReferenceEquals(row, _current)).ToArray()) _waveSources.Remove(old);
                if (_section == "wave" && IsCurrentWaveView) ShowRows(_waveTracks);
                if (playable.Length > 0) return;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (OwnsWave()) ShowError(ex); }
        finally { _advancingWave = false; if (!_closed) UpdateControls(); }
    }
    private void BeginWaveListening(TrackRow track)
    {
        if (!_waveSources.TryGetValue(track, out var source)) return;
        _waveListening = new(track, source.SessionId, source.BatchId, new Stopwatch());
        QueueWaveFeedback(source.SessionId, source.BatchId, "trackStarted", track.Id, 0);
    }
    private void SetWavePaused(bool paused)
    {
        if (paused) { _wavePauseRevision++; CancelPendingWavePlayback(); }
        SetWaveListeningClock(_waveListening, !paused && _player.IsCurrentAudioAdvancing);
        SetWaveListeningClock(_fadingWaveListening, !paused && _player.IsFadingAudioAdvancing);
    }
    private void UpdateWaveListeningActivity()
    {
        if (_closed) return;
        SetWaveListeningClock(_waveListening, _player.IsCurrentAudioAdvancing);
        SetWaveListeningClock(_fadingWaveListening, _player.IsFadingAudioAdvancing);
    }
    private static void SetWaveListeningClock(WaveListening? listening, bool advancing)
    {
        if (listening is null) return;
        if (advancing) listening.Clock.Start(); else listening.Clock.Stop();
    }
    private void ReportWaveEnd(bool finished)
    {
        var listening = _waveListening; _waveListening = null;
        FinishWaveListening(listening, finished);
    }
    private void FinishWaveListening(WaveListening? listening, bool finished)
    {
        if (listening is null) return;
        listening.Clock.Stop();
        QueueWaveFeedback(listening.SessionId, listening.BatchId, finished ? "trackFinished" : "skip", listening.Track.Id, listening.Clock.Elapsed.TotalSeconds);
    }
    private void QueueWaveFeedback(string sessionId, string batchId, string type, string? trackId, double seconds)
    {
        if (_session is null || _closed) return;
        if (_pendingFeedback >= 32) { _diagnostics.LastErrorType = "WaveFeedback:QueueFull"; return; }
        _pendingFeedback++;
        var generation = _accountGeneration; var previous = _feedbackTail;
        var token = _accountCts.Token;
        bool Current() => !_closed && _session is not null && !token.IsCancellationRequested && generation == _accountGeneration;
        _feedbackTail = SendQueuedAsync();
        async Task SendQueuedAsync()
        {
            try
            {
                await previous;
                if (!Current()) return;
                var session = await EnsureSessionAsync(token);
                if (!Current()) return;
                await _api.SendWaveFeedbackAsync(session, sessionId, batchId, type, trackId, seconds, token);
            }
            catch (Exception ex) { if (Current()) _diagnostics.LastErrorType = "WaveFeedback:" + ex.GetType().Name; }
            finally { _pendingFeedback--; }
        }
    }
    private void Quality_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_featuresReady) { SetStatus("Качество применится при следующем запуске трека. Фактический формат указан у плеера."); SavePreferences(); RefreshNextPreparation(); }
    }
    private async void Download_Click(object sender, RoutedEventArgs e) => await DownloadSelectedAsync();
    private async Task DownloadSelectedAsync()
    {
        if (_downloadCts is not null) { PauseDownloadQueue(); return; }
        // Materialize SelectedItems before starting: Ctrl/Shift selection remains exact.
        var selected = TracksList.SelectedItems.Cast<TrackRow>().Where(t => t.Available && t.LocalPath is null).ToArray();
        if (selected.Length == 0 || _session is null) return;
        await StartDownloadBatchAsync(selected, "Скачивание", null, automatic: false);
    }
    private void OpenMusicFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var path = _offline.DirectoryPath;
            using var parents = UninstallPathGuard.HoldAncestors(Path.Combine(path, "library.index.json"), create: true);
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex) { ShowError(ex); }
    }
    private void ResetFeatures()
    {
        _accountGeneration++; _waveRequestGeneration++; _waveStarting = false; _waveSeedTrack = null; _waveSeedAlbum = null; _waveViewMode = WaveMode.Personal; _featureCts?.Cancel(); _downloadCts?.Cancel();
        CancelPendingWavePlayback();
        ResetBrowseState();
        CancelNextPreparation();
        ClearPendingAutoFavorites();
        CloseDiscovery();
        _waveListening?.Clock.Stop(); _waveListening = null;
        _api.ResetWaveState(); _favoritesCache.Clear(); _waveTracks.Clear(); _waveSources.Clear(); _waveBatch = null;
        NavigateSection("favorites", "Мне нравится", remember: false);
    }
    private void CloseFeatures()
    {
        _accountGeneration++; _featureCts?.Cancel(); _downloadCts?.Cancel(); _waveListening?.Clock.Stop();
        ResetBrowseState();
        _api.ResetWaveState(); _offline.Dispose();
    }
}
