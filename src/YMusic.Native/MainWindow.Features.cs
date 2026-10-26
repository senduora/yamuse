using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace YMusic.Native;

public partial class MainWindow
{
    private readonly List<TrackRow> _favoritesCache = [];
    private readonly List<TrackRow> _waveTracks = [];
    private readonly Dictionary<TrackRow, (string SessionId, string BatchId, WaveMode Mode, string? TrackSeedId)> _waveSources = new();
    private readonly OfflineLibrary _offline;
    private CancellationTokenSource? _featureCts;
    private CancellationTokenSource? _downloadCts;
    private string _section = "favorites";
    private bool _featuresReady;
    private RadioBatch? _waveBatch;
    private WaveMode _waveViewMode;
    private bool _waveStarting;
    private WaveListening? _waveListening;
    private Task _feedbackTail = Task.CompletedTask;
    private int _accountGeneration;
    private int _playbackGeneration;
    private int _viewGeneration;
    private int _pendingFeedback;
    private bool _advancingWave;
    private AudioQuality SelectedQuality => QualityCombo.SelectedIndex switch { 2 => AudioQuality.Economical, 1 => AudioQuality.Optimal, _ => AudioQuality.Excellent };
    private sealed record WaveListening(TrackRow Track, string SessionId, string BatchId, Stopwatch Clock);

    private void InitializeFeatures() { CatalogList.ItemsSource = _catalogItems; InitializeTrackWaveActions(); _featuresReady = true; }
    private void Favorites_Click(object sender, RoutedEventArgs e)
    {
        SetSection("favorites", "Мне нравится");
        ShowRows(_favoritesCache);
        SetStatus(_session is null ? "Войдите в аккаунт, чтобы загрузить избранное" : "Избранное · ваша коллекция");
    }
    private async void Wave_Click(object sender, RoutedEventArgs e) => await OpenWaveAsync(WaveMode.Personal);
    private void Offline_Click(object sender, RoutedEventArgs e) => ShowOffline();
    private void SetSection(string section, string title) => NavigateSection(section, title, remember: true);
    private void NavigateSection(string section, string title, bool remember)
    {
        if (remember && !_restoringBrowse && _featuresReady && _account is not null && section != _section) RememberBrowseView();
        ApplySection(section, title, closeSettings: true);
    }

    private void ApplySection(string section, string title, bool closeSettings)
    {
        if (closeSettings) SetSettingsVisible(false);
        SetLyricsVisible(false);
        _featureCts?.Cancel();
        HideCatalog();
        HideCollectionHero();
        _section = section;
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
        FilterBox.IsEnabled = _session is not null || section == "offline" || _demo;
        RefreshButton.Visibility = _session is not null || section == "offline" ? Visibility.Visible : Visibility.Collapsed;
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
        if (!_settingsOpen) Motion.Enter(CurrentContent());
    }
    private bool _updatingTrackRows;
    private void ShowRows(IEnumerable<TrackRow> rows)
    {
        _catalogShowing = false; CatalogList.Visibility = Visibility.Collapsed; TracksList.Visibility = Visibility.Visible;
        FilterBox.IsEnabled = _session is not null || _section == "offline" || _demo;
        PlaySelectionButton.Content = "▶  Слушать";
        var snapshot = rows.ToArray();
        ApplyOfflineStates(snapshot);
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
    private void ShowOffline()
    {
        try
        {
            SetSection("offline", "Скачанное");
            ShowRows(_offline.Tracks());
            SetStatus("Офлайн-библиотека · Документы\\Yamuse\\Music");
        }
        catch (Exception ex) { ShowError(ex); }
    }
    private async Task StartWaveViewAsync(bool renew)
    {
        if (_session is null) { SetStatus("Войдите в Яндекс, чтобы запустить персональную волну."); return; }
        var mode = _waveViewMode;
        var seed = _waveSeedTrack?.Id;
        SetSection("wave", WaveViewTitle);
        if (!renew && IsCurrentWaveView && _waveTracks.Count > 0) { ShowRows(_waveTracks); UpdateWavePresentation(); return; }
        _featureCts?.Cancel();
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, _accountCts.Token);
        _featureCts = cts;
        var request = ++_waveRequestGeneration; var account = _accountGeneration;
        var playback = _playbackGeneration; var queueVersion = _playbackQueue.Version;
        _waveStarting = true; Busy(true); UpdateWavePresentation();
        try
        {
            SetStatus(mode == WaveMode.Track ? "Подбираем волну по треку…" : "Подбираем треки Моей волны…");
            var session = await EnsureSessionAsync(cts.Token);
            var batch = await _api.StartWaveAsync(session, cts.Token, mode, mode == WaveMode.Track ? seed : null);
            cts.Token.ThrowIfCancellationRequested();
            if (_closed || account != _accountGeneration || request != _waveRequestGeneration || !ReferenceEquals(_featureCts, cts)) return;
            if (_playbackGeneration != playback || _playbackQueue.Version != queueVersion)
            { _api.ResetWaveState(); _waveBatch = null; _waveTracks.Clear(); _waveSources.Clear(); return; }
            _waveTracks.Clear();
            AddWaveBatch(batch);
            QueueWaveFeedback(batch.SessionId, batch.BatchId, "radioStarted", null, 0);
            if (_section == "wave") ShowRows(_waveTracks);
            _playbackQueue.SetItems(batch.Tracks);
            if (_playbackQueue.Current is { } first) await PlayTrackAsync(first);
            else SetStatus("Сервис не выдал доступных треков. Нажмите «Новая волна».", true);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested) { }
        catch (Exception ex) { if (!cts.IsCancellationRequested && account == _accountGeneration && request == _waveRequestGeneration) ShowError(ex); }
        finally { if (ReferenceEquals(_featureCts, cts)) _featureCts = null; if (request == _waveRequestGeneration) _waveStarting = false; cts.Dispose(); Busy(false); UpdateWavePresentation(); }
    }
    private void AddWaveBatch(RadioBatch batch)
    {
        _waveBatch = batch;
        foreach (var row in batch.Tracks) { _waveTracks.Add(row); _waveSources[row] = (batch.SessionId, batch.BatchId, batch.Mode, batch.TrackSeedId); }
        while (_waveTracks.Count > 300)
        {
            var old = _waveTracks[0]; _waveTracks.RemoveAt(0);
            if (!_queue.Contains(old) && !ReferenceEquals(_current, old)) _waveSources.Remove(old);
        }
        UpdateWavePresentation();
    }
    private async Task ExtendWaveQueueAsync()
    {
        if (_waveBatch is null || _session is null) return;
        if (_current is null || !ReferenceEquals(_playbackQueue.Current, _current) || !_waveSources.TryGetValue(_current, out var activeSource)
            || activeSource.Mode != _waveBatch.Mode || activeSource.TrackSeedId != _waveBatch.TrackSeedId) return;
        if (_advancingWave) return;
        _advancingWave = true; UpdateControls();
        var generation = _playbackGeneration;
        var request = _waveRequestGeneration; var account = _accountGeneration; var previousBatch = _waveBatch;
        var previousVersion = _playbackQueue.Version;
        var previousTrack = _current;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, _accountCts.Token);
        try
        {
            var previousSession = _waveBatch.SessionId;
            var session = await EnsureSessionAsync(cts.Token);
            var batch = await _api.MoreWaveAsync(session, previousSession, cts.Token);
            if (_session is null || _closed || generation != _playbackGeneration || account != _accountGeneration || request != _waveRequestGeneration
                || !ReferenceEquals(_waveBatch, previousBatch) || _playbackQueue.Version != previousVersion || !ReferenceEquals(_current, previousTrack)) return;
            AddWaveBatch(batch);
            if (batch.SessionId != previousSession) QueueWaveFeedback(batch.SessionId, batch.BatchId, "radioStarted", null, 0);
            // Keep only a small playback history; the server queue has its own bounded state.
            _playbackQueue.Append(batch.Tracks);
            RefreshPlaybackView();
            foreach (var old in _waveSources.Keys.Where(row => !_waveTracks.Contains(row) && !_queue.Contains(row) && !ReferenceEquals(row, _current)).ToArray()) _waveSources.Remove(old);
            if (_section == "wave") ShowRows(_waveTracks);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!cts.IsCancellationRequested && account == _accountGeneration && request == _waveRequestGeneration) ShowError(ex); }
        finally { _advancingWave = false; UpdateControls(); }
    }
    private void BeginWaveListening(TrackRow track)
    {
        if (!_waveSources.TryGetValue(track, out var source)) return;
        _waveListening = new(track, source.SessionId, source.BatchId, Stopwatch.StartNew());
        QueueWaveFeedback(source.SessionId, source.BatchId, "trackStarted", track.Id, 0);
    }
    private void SetWavePaused(bool paused)
    {
        foreach (var listening in new[] { _waveListening, _fadingWaveListening })
            if (listening is not null) { if (paused) listening.Clock.Stop(); else listening.Clock.Start(); }
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
        _feedbackTail = SendQueuedAsync();
        async Task SendQueuedAsync()
        {
            try
            {
                await previous;
                if (_closed || _session is null || generation != _accountGeneration) return;
                var session = await EnsureSessionAsync(_lifetime.Token);
                if (_closed || _session is null || generation != _accountGeneration) return;
                await _api.SendWaveFeedbackAsync(session, sessionId, batchId, type, trackId, seconds, _lifetime.Token);
            }
            catch (Exception ex) { if (!_closed) _diagnostics.LastErrorType = "WaveFeedback:" + ex.GetType().Name; }
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
        if (_downloadCts is not null) { _pendingAutoFavorites = null; _downloadCts.Cancel(); return; }
        // Materialize SelectedItems before starting: Ctrl/Shift selection remains exact.
        var selected = TracksList.SelectedItems.Cast<TrackRow>().Where(t => t.Available && t.LocalPath is null).ToArray();
        if (selected.Length == 0 || _session is null) return;
        await StartDownloadBatchAsync(selected, "Скачивание", null, automatic: false);
    }
    private void OpenMusicFolder_Click(object sender, RoutedEventArgs e)
    {
        try { Directory.CreateDirectory(_offline.DirectoryPath); Process.Start(new ProcessStartInfo(_offline.DirectoryPath) { UseShellExecute = true }); }
        catch (Exception ex) { ShowError(ex); }
    }
    private void ResetFeatures()
    {
        _accountGeneration++; _waveRequestGeneration++; _waveStarting = false; _waveSeedTrack = null; _waveViewMode = WaveMode.Personal; _featureCts?.Cancel(); _downloadCts?.Cancel();
        ResetBrowseState();
        CancelNextPreparation();
        _pendingAutoFavorites = null;
        CloseDiscovery();
        _waveListening?.Clock.Stop(); _waveListening = null;
        _api.ResetWaveState(); _favoritesCache.Clear(); _waveTracks.Clear(); _waveSources.Clear(); _waveBatch = null;
        SetSection("favorites", "Мне нравится");
    }
    private void CloseFeatures()
    {
        _accountGeneration++; _featureCts?.Cancel(); _downloadCts?.Cancel(); _waveListening?.Clock.Stop();
        ResetBrowseState();
        _api.ResetWaveState(); _offline.Dispose();
    }
}
