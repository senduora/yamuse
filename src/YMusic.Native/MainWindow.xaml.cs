using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace YMusic.Native;

public partial class MainWindow : Window
{
    private readonly bool _demo;
    private readonly bool _restore;
    private readonly MusicApi _api;
    private readonly SessionStore _store;
    private readonly string _diagnosticsDirectory;
    private readonly Diagnostics _diagnostics = new();
    private readonly NativePlayer _player;
    private readonly ObservableCollection<TrackRow> _tracks = new();
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource _accountCts;
    private readonly SemaphoreSlim _refreshLock = new(1);
    private readonly DispatcherTimer _metricsTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly DispatcherTimer _progressTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly DispatcherTimer _filterTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly ICollectionView _view;
    private Session? _session;
    private Account? _account;
    private CancellationTokenSource? _authCts;
    private CancellationTokenSource? _libraryCts;
    private CancellationTokenSource? _playCts;
    private DeviceCode? _deviceCode;
    private string _deviceId = Guid.NewGuid().ToString("N");
    private string _query = "";
    private readonly PlaybackQueue _playbackQueue = new();
    private IReadOnlyList<TrackRow> _queue => _playbackQueue.Items;
    private int _queueIndex => _playbackQueue.CurrentIndex;
    private TrackRow? _current;
    private bool _seekDragging;
    private bool _closed;
    private bool _loadingStream;
    private int _busy;
    private readonly HashSet<string> _favoriteTrackIds = new(StringComparer.Ordinal);
    private bool _favoriteIdsKnown;

    internal MainWindow(bool demo = false, MusicApi? api = null, SessionStore? store = null, bool restore = true, string? diagnosticsDirectory = null, OfflineLibrary? offline = null, NativePlayer? player = null)
    {
        _demo = demo;
        _restore = restore;
        _api = api ?? new MusicApi();
        _store = store ?? new SessionStore(App.DataDirectory);
        _diagnosticsDirectory = diagnosticsDirectory ?? App.DataDirectory;
        _offline = offline ?? new OfflineLibrary();
        _player = player ?? new NativePlayer();
        _accountCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        InitializeComponent();
        WindowAppearance.Apply(this);
        _view = CollectionViewSource.GetDefaultView(_tracks);
        _view.Filter = value => value is TrackRow row && (_query.Length == 0 || row.Matches(_query));
        TracksList.ItemsSource = _view;
        InitializeFeatures();
        InitializeDesign();
        InitializeLyrics();
        LoadPreferences();
        _metricsTimer.Tick += (_, _) => UpdateMetrics();
        _progressTimer.Tick += (_, _) => UpdateProgress();
        _filterTimer.Tick += (_, _) =>
        {
            _filterTimer.Stop();
            _query = FilterBox.Text.Trim();
            _view.Refresh();
            UpdateCount();
        };
        _player.Ended += async (_, _) => await AdvanceAsync(1, true);
        _player.TransitionFinished += OnTransitionFinished;
        _player.Failed += (_, _) => { _progressTimer.Stop(); ReportWaveEnd(false); _diagnostics.Stage = "playback_error"; SetStatus("Windows прервала воспроизведение. Попробуйте снова или выберите другой трек.", true); UpdateControls(); };
        ContentRendered += OnReady;
        Closed += OnClosed;
        SourceInitialized += (_, _) => { InitializeWindows(); InitializeShell(); };
    }

    private async void OnReady(object? sender, EventArgs e)
    {
        ContentRendered -= OnReady;
        if (_closed) return;
        _diagnostics.WindowReady();
        UpdateMetrics();
        _metricsTimer.Start();
        if (!_demo && _restore) _ = CheckForAppUpdatesAsync();
        if (_demo) LoadDemo();
        else if (_restore) await RestoreAsync();
    }

    private async Task RestoreAsync()
    {
        Session? saved;
        try { saved = _store.Load(); }
        catch (Exception ex) when (ex is CryptographicException or IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            SetStatus("Не удалось прочитать сохранённый вход. Новый вход заменит повреждённое сохранение.", true);
            return;
        }
        if (saved is null) return;
        _deviceId = saved.DeviceId;
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _authCts = cts;
        LoginButton.Content = "Отменить";
        Busy(true);
        try
        {
            SetStatus("Восстанавливаем вход…");
            if (saved.ExpiresAtUtc <= DateTimeOffset.UtcNow.AddMinutes(1)) saved = await _api.RefreshAsync(saved, cts.Token);
            await ActivateAsync(saved, cts.Token);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested) { if (!_closed && _session is null) SetStatus("Восстановление входа отменено."); }
        catch (Exception ex) { ShowError(ex); }
        finally { if (ReferenceEquals(_authCts, cts)) { _authCts = null; LoginButton.Content = "Войти в Яндекс"; } cts.Dispose(); Busy(false); }
    }

    private async void Login_Click(object sender, RoutedEventArgs e)
    {
        if (_authCts is not null) { _authCts.Cancel(); return; }
        await StartLoginAsync();
    }

    internal async Task StartLoginAsync()
    {
        if (_closed || _session is not null || _authCts is not null) return;
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _authCts = cts;
        LoginButton.Content = "Отменить";
        LoginPanel.Visibility = Visibility.Visible;
        LoginTitle.Text = "Вход через Яндекс";
        LoginDescription.Text = "Получаем код для подтверждения входа…";
        CodePanel.Visibility = Visibility.Collapsed;
        _diagnostics.Stage = "auth_pending";
        Busy(true);
        try
        {
            _deviceCode = await _api.BeginLoginAsync(_deviceId, cts.Token);
            cts.Token.ThrowIfCancellationRequested();
            CodeText.Text = _deviceCode.UserCode;
            CodePanel.Visibility = Visibility.Visible;
            LoginDescription.Text = "Откройте страницу Яндекса, введите этот код и подтвердите доступ. Пароль остаётся на стороне Яндекса.";
            SetStatus("Ожидаем подтверждение в браузере. Код действует ограниченное время.");
            if (_restore) OpenLoginPage(); // Test windows never launch a browser.
            var session = await _api.CompleteLoginAsync(_deviceCode, _deviceId, cts.Token);
            cts.Token.ThrowIfCancellationRequested();
            await ActivateAsync(session, cts.Token);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested) { if (!_closed && _session is null) SetStatus("Вход отменён."); }
        catch (Exception ex) { ShowError(ex); }
        finally
        {
            if (ReferenceEquals(_authCts, cts))
            {
                _authCts = null; _deviceCode = null; CodeText.Clear(); CodePanel.Visibility = Visibility.Collapsed;
                LoginButton.Content = "Войти в Яндекс";
                if (_session is null) { LoginTitle.Text = "Начните со входа в аккаунт"; LoginDescription.Text = "Нажмите «Войти в Яндекс», чтобы получить новый код."; _diagnostics.Stage = "signed_out"; }
            }
            cts.Dispose();
            Busy(false);
        }
    }

    private async Task ActivateAsync(Session session, CancellationToken cancellationToken)
    {
        var account = await _api.GetAccountAsync(session, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (_accountCts.IsCancellationRequested) { _accountCts.Dispose(); _accountCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token); }
        _session = session;
        _account = account;
        _deviceId = session.DeviceId;
        AccountText.Text = account.DisplayName;
        AccountText.DataContext = account;
        LoginPanel.Visibility = Visibility.Collapsed;
        LoginButton.Visibility = Visibility.Collapsed;
        LogoutButton.Visibility = Visibility.Visible;
        RefreshButton.Visibility = Visibility.Visible;
        FilterBox.IsEnabled = true;
        TrySaveSession(session);
        await RefreshLibraryAsync();
    }

    private bool TrySaveSession(Session session)
    {
        try { _store.Save(session); return true; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException)
        {
            _diagnostics.LastErrorType = ex.GetType().Name;
            LoginDescription.Text = "Вход выполнен, но Windows не сохранила сессию. После закрытия понадобится войти снова.";
            LoginPanel.Visibility = Visibility.Visible;
            LoginTitle.Text = "Вход сохранён только до закрытия";
            CodePanel.Visibility = Visibility.Collapsed;
            return false;
        }
    }

    private async Task<Session> EnsureSessionAsync(CancellationToken cancellationToken)
    {
        await _refreshLock.WaitAsync(cancellationToken);
        try
        {
            var session = _session ?? throw new MusicException("Войдите в аккаунт.", true);
            if (session.ExpiresAtUtc > DateTimeOffset.UtcNow.AddMinutes(1)) return session;
            var renewed = await _api.RefreshAsync(session, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (!ReferenceEquals(_session, session)) throw new OperationCanceledException(cancellationToken);
            _session = renewed;
            TrySaveSession(renewed);
            return renewed;
        }
        finally { _refreshLock.Release(); }
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        if (_section == "home") await LoadHomeAsync();
        else if (_section == "wave") await StartWaveViewAsync(true);
        else if (_section == "offline") ShowOffline();
        else if (!await RefreshCatalogViewAsync()) await RefreshLibraryAsync();
    }

    internal async Task RefreshLibraryAsync()
    {
        if (_account is null || _closed) return;
        BeginFavoritesRefresh();
        _libraryCts?.Cancel();
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _libraryCts = cts;
        var clock = Stopwatch.StartNew();
        Busy(true);
        RefreshButton.IsEnabled = false;
        _diagnostics.Stage = _player.IsPlaying ? "playing_library_loading" : "library_loading";
        try
        {
            var uid = _account.Uid;
            var session = await EnsureSessionAsync(cts.Token);
            SetStatus("Получаем список избранного…");
            var ids = await _api.GetLikedIdsAsync(session, uid, cts.Token);
            cts.Token.ThrowIfCancellationRequested();
            _favoriteTrackIds.Clear();
            _favoriteTrackIds.UnionWith(ids.Select(id => id.Id));
            _favoriteIdsKnown = true;
            UpdatePlayerLikeState();
            _favoritesCache.Clear();
            if (_section == "favorites") _tracks.Clear();
            _diagnostics.TotalTracks = ids.Count;
            _diagnostics.LoadedTracks = 0;
            UpdateCount();
            for (var offset = 0; offset < ids.Count; offset += 100)
            {
                var batch = ids.Skip(offset).Take(100).ToArray();
                var rows = await _api.GetTracksAsync(session, batch, offset, cts.Token);
                cts.Token.ThrowIfCancellationRequested();
                ApplyOfflineStates(rows);
                foreach (var row in rows) { row.IsPlaying = _player.HasSource && _current?.RequestId == row.RequestId; _favoritesCache.Add(row); if (_section == "favorites") _tracks.Add(row); }
                _diagnostics.LoadedTracks = _favoritesCache.Count;
                if (offset == 0) _diagnostics.Timing("favorites_first_batch_ms", clock.Elapsed.TotalMilliseconds);
                if (_section == "favorites") SetStatus($"Загружено {_favoritesCache.Count:N0} из {ids.Count:N0} треков…");
                UpdateCount();
            }
            _diagnostics.Timing("favorites_all_ms", clock.Elapsed.TotalMilliseconds);
            _diagnostics.Stage = _player.IsPlaying ? "playing" : "library_ready";
            if (_section == "favorites") SetStatus($"Избранное загружено · {_favoritesCache.Count:N0} треков · {clock.Elapsed.TotalSeconds:F2} с");
            UpdateCount();
            ScheduleAutoFavoritesDownloads();
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _diagnostics.Stage = "library_partial_or_error";
            ShowError(ex, $"Загружено {_favoritesCache.Count:N0} из {_diagnostics.TotalTracks:N0}. ");
        }
        finally
        {
            if (ReferenceEquals(_libraryCts, cts)) { _libraryCts = null; RefreshButton.IsEnabled = true; }
            cts.Dispose();
            Busy(false);
        }
    }

    private void Logout_Click(object sender, RoutedEventArgs e) => SignOut();
    internal void SignOut()
    {
        ResetFeatures();
        _accountCts.Cancel();
        _authCts?.Cancel(); _libraryCts?.Cancel(); _playCts?.Cancel();
        _session = null; _account = null;
        _favoriteTrackIds.Clear(); _favoriteIdsKnown = false;
        _player.Stop(); _progressTimer.Stop();
        if (_current is not null) _current.IsPlaying = false;
        HighlightTrack(null);
        _current = null; _playbackQueue.SetItems([]); _loadingStream = false;
        _tracks.Clear(); _deviceCode = null; CodeText.Clear();
        _diagnostics.Stage = "signed_out"; _diagnostics.LoadedTracks = 0; _diagnostics.TotalTracks = 0;
        AccountText.Text = "Вход не выполнен";
        AccountText.DataContext = null;
        LoginButton.Visibility = Visibility.Visible; LoginButton.Content = "Войти в Яндекс";
        LogoutButton.Visibility = Visibility.Collapsed; RefreshButton.Visibility = Visibility.Collapsed;
        LoginPanel.Visibility = Visibility.Visible; CodePanel.Visibility = Visibility.Collapsed;
        LoginTitle.Text = "Начните со входа в аккаунт";
        LoginDescription.Text = "Подтвердите вход на странице Яндекса — избранное появится здесь автоматически.";
        FilterBox.Clear(); FilterBox.IsEnabled = false;
        TrackTitle.Text = "Выберите трек"; TrackArtist.Text = "Двойной щелчок или кнопка «Слушать»"; QualityText.Text = "";
        Artwork.SetSourceUri(PlayerCover, null);
        UpdateProgress(); UpdateCount(); UpdateControls();
        try { _store.Clear(); SetStatus("Вы вышли из аккаунта. Сохранённый вход удалён с этого компьютера."); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SetStatus("Вход завершён в этом окне, но не удалось удалить сохранённую сессию. Закройте приложение и удалите session.dpapi в папке данных.", true);
        }
    }

    private async void PlaySelection_Click(object sender, RoutedEventArgs e) { if (_catalogShowing) await OpenSelectedCatalogAsync(); else await PlaySelectedAsync(); }
    private async void Tracks_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (FromButton(e.OriginalSource as DependencyObject)) return;
        var item = ItemsControl.ContainerFromElement(TracksList, e.OriginalSource as DependencyObject) as ListViewItem;
        if (item is not null) await PlaySelectedAsync();
    }
    private void Tracks_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateControls();
    private async Task PlaySelectedAsync()
    {
        var selected = TracksList.SelectedItem is TrackRow { CanPlay: true } chosen
            ? chosen : _view.Cast<TrackRow>().FirstOrDefault(row => row.CanPlay);
        if (selected is null || !selected.CanPlay) return;
        if (_demo) { SetStatus("Демо показывает 5 000 тестовых строк. Для настоящей музыки откройте обычный режим и войдите в Яндекс."); return; }
        if (_section == "queue")
        {
            var index = SelectedQueueIndex();
            if (index < 0 || !_playbackQueue.SelectAt(index)) return;
        }
        else _playbackQueue.SetItems(_view.Cast<TrackRow>().ToArray(), selected);
        await PlayTrackAsync(selected);
    }

    internal Task PlayTrackAsync(TrackRow track) => PlayTrackCoreAsync(track, null, false);

    private async Task PlayTrackCoreAsync(TrackRow track, PreparedPlayback? prepared, bool automatic)
    {
        if (_closed) return;
        ApplyOfflineStates([track]);
        var localPath = track.LocalPath ?? track.DownloadedPath;
        var localQuality = track.LocalQuality ?? track.DownloadedQuality;
        if (_session is null && localPath is null || !track.CanPlay) return;
        if (!ReferenceEquals(_playbackQueue.Current, track))
        {
            if (_queue.Any(row => ReferenceEquals(row, track))) _playbackQueue.Select(track);
            else _playbackQueue.SetItems([track], track);
        }
        var promoted = PromotePrepared(track, prepared, automatic);
        if (!promoted) { CancelNextPreparation(); ReportWaveEnd(automatic); }
        _playbackGeneration++;
        _pendingSeek = null; _seekDragging = false;
        _playCts?.Cancel();
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _playCts = cts;
        if (!promoted) _player.Stop();
        _progressTimer.Stop();
        if (_current is not null) _current.IsPlaying = false;
        HighlightTrack(null);
        _current = track;
        RefreshPlaybackView();
        RefreshLyrics();
        _loadingStream = true;
        TrackTitle.Text = track.Title; TrackArtist.Text = track.Artist;
        Artwork.SetSourceUri(PlayerCover, track.CoverUri);
        QualityText.Text = "";
        SetStatus("Открываем аудиопоток…");
        _diagnostics.Stage = "resolving_stream";
        UpdateControls(); UpdateProgress();
        var clock = Stopwatch.StartNew();
        try
        {
            AudioDescriptor? audio = null;
            if (!promoted && localPath is null)
            {
                var session = await EnsureSessionAsync(cts.Token);
                audio = await _api.ResolveAudioAsync(session, track.RequestId, SelectedQuality, cts.Token);
            }
            cts.Token.ThrowIfCancellationRequested();
            _diagnostics.Timing("stream_url_ms", clock.Elapsed.TotalMilliseconds);
            var bufferProgress = new Progress<AudioProgress>(value =>
            {
                if (!ReferenceEquals(_playCts, cts) || cts.IsCancellationRequested) return;
                SetStatus($"Загрузка аудио · {value.Bytes / 1048576.0:F1}" + (value.Total.HasValue ? $" из {value.Total / 1048576.0:F1}" : "") + " МБ");
            });
            if (!promoted)
            {
                if (localPath is not null) await _player.OpenAsync(new Uri(localPath), cts.Token);
                else await _player.OpenAsync(audio!, cts.Token, bufferProgress);
            }
            cts.Token.ThrowIfCancellationRequested();
            if (track.DurationMs > 60_000 && _player.Duration.TotalSeconds is > 0 and <= 35)
            {
                _player.Stop();
                throw new MusicException("Длительность потока похожа на фрагмент. Проверьте подписку; воспроизведение остановлено.");
            }
            _diagnostics.Timing("play_requested_to_media_open_ms", clock.Elapsed.TotalMilliseconds);
            var playingOffline = promoted ? prepared!.Offline : localPath is not null;
            _diagnostics.Stage = playingOffline ? "playing_offline" : "playing";
            track.IsPlaying = true;
            HighlightTrack(track);
            QualityText.Text = promoted ? prepared!.QualityLabel : localQuality ?? audio?.QualityLabel ?? "Локальное аудио";
            SetStatus(playingOffline ? "Офлайн · локальный аудиофайл" : _waveSources.ContainsKey(track) ? _waveBatch?.Mode == WaveMode.Collection ? "Волна по коллекции · персональные рекомендации" : "Моя волна · персональные рекомендации" : "Воспроизведение · очередь треков");
            BeginWaveListening(track);
            _progressTimer.Start();
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested) { }
        catch (Exception ex) { _diagnostics.Stage = "playback_error"; ShowError(ex); }
        finally
        {
            if (ReferenceEquals(_playCts, cts)) { _playCts = null; _loadingStream = false; UpdateControls(); UpdateProgress(); }
            cts.Dispose();
        }
    }

    private async void PlayPause_Click(object sender, RoutedEventArgs e) => await TogglePlaybackAsync();
    private async Task TogglePlaybackAsync()
    {
        if (_loadingStream) return;
        if (!_player.HasSource)
        {
            if (_current is not null) await PlayTrackAsync(_current);
            else await PlaySelectedAsync();
            return;
        }
        _player.Toggle();
        SetWavePaused(!_player.IsPlaying);
        _diagnostics.Stage = _player.IsPlaying ? "playing" : "paused";
        SetStatus(_player.IsPlaying ? "Воспроизведение · очередь из избранного" : "Пауза");
        if (_player.IsPlaying) _progressTimer.Start(); else _progressTimer.Stop();
        UpdateControls();
    }

    private async void Previous_Click(object sender, RoutedEventArgs e)
    {
        if (_player.Position.TotalSeconds > 3) { CommitSeek(0); return; }
        await AdvanceAsync(-1, false);
    }
    private async void Next_Click(object sender, RoutedEventArgs e) => await AdvanceAsync(1, false);
    private async Task AdvanceAsync(int direction, bool automatic)
    {
        if (_closed || _queue.Count == 0 || _advancingPlayback) return;
        _advancingPlayback = true;
        try
        {
        if (automatic && !_player.IsPlaying) ReportWaveEnd(true);
        var prepared = direction > 0 ? ReadyNextPlayback(automatic) : null;
        var next = direction < 0 ? _playbackQueue.Previous() : _playbackQueue.Next(automatic);
        if (next is null && direction > 0 && _current is not null && _waveSources.ContainsKey(_current))
        {
            var generation = _accountGeneration;
            var playbackGeneration = _playbackGeneration;
            await ExtendWaveQueueAsync();
            if (generation != _accountGeneration || playbackGeneration != _playbackGeneration || _closed) return;
            next = _playbackQueue.Next(automatic);
        }
        if (next is null)
        {
            if (automatic)
            {
                _player.Stop(); _progressTimer.Stop(); if (_current is not null) _current.IsPlaying = false;
                CancelNextPreparation(); ReportWaveEnd(true);
                HighlightTrack(null);
                _diagnostics.Stage = "library_ready"; SetStatus("Очередь завершена"); UpdateControls(); UpdateProgress();
            }
            return;
        }
        await PlayTrackCoreAsync(next, prepared, automatic);
        }
        finally { _advancingPlayback = false; RefreshNextPreparation(); }
    }

    private void UpdateControls()
    {
        if (PlaySelectionButton is null) return;
        PlaySelectionButton.IsEnabled = _catalogShowing ? CatalogList.SelectedItem is CatalogItem : _view.Cast<TrackRow>().Any(row => row.CanPlay);
        if (DownloadButton is not null)
        {
            DownloadButton.Content = _downloadCts is not null ? "✕" : "↓";
            DownloadButton.ToolTip = _downloadCts is not null ? "Отменить загрузку" : "Скачать выбранные треки";
            DownloadButton.IsEnabled = _downloadCts is not null || !_catalogShowing && _session is not null && TracksList.SelectedItems.Cast<TrackRow>().Any(t => t is { Available: true, LocalPath: null });
        }
        PlayPauseButton.IsEnabled = !_loadingStream && (_current is not null || PlaySelectionButton.IsEnabled);
        PlayPauseButton.Content = _player.IsPlaying ? "\uE769" : "\uE768";
        PreviousButton.IsEnabled = _current is not null && _queue.Count > 0;
        NextButton.IsEnabled = !_advancingWave && (_playbackQueue.CanNext || _current is not null && _waveSources.ContainsKey(_current));
        SeekSlider.IsEnabled = !_loadingStream && _player.HasSource && _player.Duration.TotalSeconds > 0;
        UpdatePlayerLikeState();
        UpdateLyricsTimer();
        PlayerLyricsButton.IsEnabled = _current is not null;
        UpdatePlayerArtistLinks();
        if (_current is null && _lyricsOpen) SetLyricsVisible(false);
        if (_current is null) Artwork.SetSourceUri(PlayerCover, null);
        UpdateWindowsMedia();
        UpdateQueueControls();
        UpdateWavePresentation();
        RefreshNextPreparation();
        UpdateShellState();
    }
    private void HighlightTrack(TrackRow? current)
    {
        foreach (var row in _tracks) row.IsPlaying = current is not null && row.RequestId == current.RequestId;
    }
    private void UpdateProgress()
    {
        var duration = _player.Duration;
        var position = DisplayPosition(_player.Position);
        if (!_seekDragging) { SeekSlider.Maximum = Math.Max(1, duration.TotalSeconds); SeekSlider.Value = position.TotalSeconds; }
        PositionText.Text = FormatTime(_seekDragging ? TimeSpan.FromSeconds(SeekSlider.Value) : position);
        DurationText.Text = FormatTime(duration);
        SeekSlider.IsEnabled = !_loadingStream && _player.HasSource && duration.TotalSeconds > 0;
        UpdateLyrics(position.TotalSeconds);
        UpdateShellState();
        TryAutomaticTransition();
    }
    private static string FormatTime(TimeSpan time) => time.TotalHours >= 1 ? time.ToString(@"h\:mm\:ss") : time.ToString(@"m\:ss");
    private void Seek_Start(object sender, MouseButtonEventArgs e) { if (SeekSlider.IsEnabled) { _seekDragging = true; PositionText.Text = FormatTime(TimeSpan.FromSeconds(SeekSlider.Value)); } }
    private void Seek_End(object sender, MouseButtonEventArgs e) => FinishSeek(true);
    private void Seek_KeyUp(object sender, KeyEventArgs e) { if (SeekSlider.IsEnabled && e.Key is Key.Left or Key.Right or Key.Home or Key.End or Key.PageDown or Key.PageUp) CommitSeek(SeekSlider.Value); }
    private void Volume_Changed(object sender, RoutedPropertyChangedEventArgs<double> e) { _player.SetVolume(e.NewValue / 100); SavePreferences(); UpdateShellState(); }
    private void Filter_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (FilterHint is null) return;
        FilterHint.Visibility = FilterBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        _filterTimer.Stop(); _filterTimer.Start();
    }
    private void UpdateCount()
    {
        var visible = _query.Length == 0 ? _tracks.Count : _view.Cast<object>().Count();
        var total = _section == "favorites" ? _diagnostics.TotalTracks : _tracks.Count;
        CountText.Text = _session is not null || _demo || _section == "offline" ? (_query.Length > 0 ? $"Найдено {visible:N0} · загружено {_tracks.Count:N0} из {total:N0}" : $"{_tracks.Count:N0} из {total:N0} треков") : "Любимые треки из вашего аккаунта";
        EmptyState.Visibility = visible > 0 ? Visibility.Collapsed : Visibility.Visible;
        EmptyTitle.Text = _query.Length > 0 ? "Ничего не найдено" : _section == "offline" ? "Пока ничего не скачано" : _section == "wave" ? "Запускаем Мою волну" : _session is not null ? "В избранном пока нет треков" : "Здесь появится избранное";
        EmptyDescription.Text = _query.Length > 0 ? "Попробуйте другое название или исполнителя" : _section == "offline" ? "Выберите трек и нажмите «Скачать»" : _session is not null ? "Если коллекция ещё загружается, подождите немного" : "Войдите в Яндекс, чтобы загрузить любимые треки";
        UpdateCatalogCount();
        // A selection or transport change does not change thousands of offline files.
        // Refresh this status when the list changes; downloads/removals also refresh it explicitly.
        UpdatePlaylistDownloadedStatus();
        UpdateControls();
    }
    private void Busy(bool start)
    {
        _busy = Math.Max(0, _busy + (start ? 1 : -1));
        if (!_closed) BusyBar.Visibility = _busy > 0 ? Visibility.Visible : Visibility.Collapsed;
    }
    private void SetStatus(string text, bool error = false)
    {
        if (_closed) return;
        StatusText.Text = text;
        StatusText.Foreground = new SolidColorBrush(error ? Color.FromRgb(255, 154, 139) : Color.FromRgb(156, 165, 181));
        WaveStatus.Text = text;
        WaveStatus.Foreground = StatusText.Foreground;
        HomeStatus.Text = ArtistStatus.Text = text;
        HomeStatus.Foreground = ArtistStatus.Foreground = StatusText.Foreground;
    }
    private void ShowError(Exception error, string prefix = "")
    {
        if (_closed) return;
        _diagnostics.LastErrorType = error.GetType().Name;
        var message = error switch
        {
            MusicException music => music.Message,
            HttpRequestException => "Не удалось связаться с Яндексом. Проверьте подключение к интернету.",
            OperationCanceledException or TimeoutException => "Сервис не ответил вовремя. Попробуйте ещё раз.",
            _ => "Действие не завершено. Повторите его; тип ошибки сохранён в диагностике."
        };
        SetStatus(prefix + message, true);
    }
    private void UpdateMetrics()
    {
        if (_closed) return;
        var sample = _diagnostics.Sample();
        MemoryText.Text = $"RAM всего {sample.WorkingSetBytes / 1048576.0:F0} МБ";
        PrivateText.Text = $"Commit {sample.PrivateBytes / 1048576.0:F0} МБ";
        CpuText.Text = $"CPU {sample.CpuPercent:F2}%";
        StartupText.Text = $"Запуск {_diagnostics.WindowReadyMs / 1000:F2} с";
    }
    private void SaveDiagnostics_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog { Filter = "Диагностика JSON|*.json", FileName = $"YMusic-metrics-{DateTime.Now:yyyyMMdd-HHmmss}.json", AddExtension = true };
        if (dialog.ShowDialog(this) != true) return;
        try { _diagnostics.Save(dialog.FileName); SetStatus("Замер сохранён. Файл содержит ресурсы и время загрузки без данных аккаунта."); }
        catch (Exception ex) { ShowError(ex); }
    }
    private void OpenLoginPage_Click(object sender, RoutedEventArgs e) => OpenLoginPage();
    private void OpenLoginPage()
    {
        if (_deviceCode is null) return;
        try { Process.Start(new ProcessStartInfo(_deviceCode.VerificationUri.AbsoluteUri) { UseShellExecute = true }); }
        catch { SetStatus($"Откройте {_deviceCode.VerificationUri.GetLeftPart(UriPartial.Path)} в браузере и введите код из окна.", true); }
    }
    private void CopyCode_Click(object sender, RoutedEventArgs e)
    {
        if (_deviceCode is null) return;
        try { Clipboard.SetText(_deviceCode.UserCode); SetStatus("Код скопирован. Введите его на странице Яндекса."); }
        catch (COMException) { SetStatus("Буфер обмена занят. Выделите код и скопируйте вручную.", true); }
    }
    private async void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && _settingsOpen) { e.Handled = true; SettingsClose_Click(sender, e); return; }
        if (_settingsOpen && e.Key is not (Key.MediaPlayPause or Key.MediaNextTrack or Key.MediaPreviousTrack)) return;
        if (e.Key == Key.Escape && _lyricsOpen) { e.Handled = true; SetLyricsVisible(false); PlayerLyricsButton.Focus(); return; }
        if (e.OriginalSource is TextBoxBase or Slider) return;
        if (_windows?.IsAvailable == true && e.Key is Key.MediaPlayPause or Key.MediaNextTrack or Key.MediaPreviousTrack) return;
        if (e.Key is Key.Space or Key.MediaPlayPause) { e.Handled = true; await TogglePlaybackAsync(); }
        else if (e.Key == Key.MediaNextTrack) { e.Handled = true; await AdvanceAsync(1, false); }
        else if (e.Key == Key.MediaPreviousTrack) { e.Handled = true; await AdvanceAsync(-1, false); }
        else if (e.Key == Key.Enter && TracksList.IsKeyboardFocusWithin) { e.Handled = true; await PlaySelectedAsync(); }
        else if (e.Key == Key.Enter && CatalogList.IsKeyboardFocusWithin) { e.Handled = true; await OpenSelectedCatalogAsync(); }
    }
    private void LoadDemo()
    {
        ModeLabel.Text = "ДЕМО · ТЕСТОВЫЕ ДАННЫЕ";
        AccountText.Text = "Проверка интерфейса · без подключения к аккаунту";
        LoginPanel.Visibility = Visibility.Collapsed; LoginButton.Visibility = Visibility.Collapsed;
        FilterBox.IsEnabled = true;
        string[] titles = ["Утренний свет", "В городе тишина", "За горизонт", "На своей волне", "Долгая дорога", "Последний трамвай"];
        string[] artists = ["Тестовый исполнитель", "Демонстрационная группа", "Пример артиста"];
        for (var i = 0; i < 5000; i++) _tracks.Add(new TrackRow { Number = i + 1, Id = "demo-" + i, RequestId = "demo-" + i, Title = titles[i % titles.Length] + (i > 5 ? " · " + (i + 1) : ""), Artist = artists[i % artists.Length], Album = "Демоальбом " + (i % 20 + 1), DurationMs = 180000 + i % 200 * 1000, Available = i % 19 != 18 });
        _favoritesCache.AddRange(_tracks);
        _diagnostics.LoadedTracks = _tracks.Count; _diagnostics.TotalTracks = _tracks.Count; _diagnostics.Stage = "demo_5000_rows";
        UpdateCount(); SetStatus("Демонстрация списка из 5 000 строк. Для реального входа запустите приложение без --demo.");
    }
    private void OnClosed(object? sender, EventArgs e)
    {
        _closed = true;
        _lifetime.Cancel(); _authCts?.Cancel(); _libraryCts?.Cancel(); _playCts?.Cancel();
        CancelNextPreparation();
        _accountCts.Cancel();
        _metricsTimer.Stop(); _progressTimer.Stop(); _filterTimer.Stop();
        _player.Dispose();
        SavePreferences(); _shell?.Dispose();
        _windows?.Dispose();
        CloseDiscovery();
        CloseFeatures();
        try { _diagnostics.SaveAuto(_diagnosticsDirectory); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        _diagnostics.Dispose(); _api.Dispose();
        // Cancellation sources and refresh lock are released by their outstanding async operations.
    }
}
