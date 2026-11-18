using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
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
    private readonly TrackRows _tracks = new();
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
    private HashSet<string>? _libraryRemovedLikes;
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
    private double _volumeBeforeMute = 50;
    private long _advanceOperation;
    private int _busy;
    private readonly HashSet<string> _favoriteTrackIds = new(StringComparer.Ordinal);
    private bool _favoriteIdsKnown;

    internal MainWindow(bool demo = false, MusicApi? api = null, SessionStore? store = null, bool restore = true, string? diagnosticsDirectory = null, OfflineLibrary? offline = null, NativePlayer? player = null, PreferencesStore? preferences = null, IAudioOutputDevices? audioOutputDevices = null)
    {
        _demo = demo;
        _restore = restore;
        _api = api ?? new MusicApi();
        var isolated = demo || !restore || diagnosticsDirectory is not null;
        _persistBeforeClose = !isolated;
        _diagnosticsDirectory = diagnosticsDirectory ?? (isolated ? Directory.CreateTempSubdirectory("Yamuse-").FullName : App.DataDirectory);
        _store = store ?? new SessionStore(_diagnosticsDirectory);
        var bufferDirectory = Path.Combine(_diagnosticsDirectory, "buffer");
        _offline = offline ?? (isolated ? new OfflineLibrary(Path.Combine(_diagnosticsDirectory, "music"), new AudioBuffer(bufferDirectory)) : new OfflineLibrary());
        _player = player ?? (isolated ? new NativePlayer(new AudioBuffer(bufferDirectory)) : new NativePlayer());
        // An isolated restore can exercise the real window lifecycle with its own data.
        // Its startup check must use an explicitly supplied test backend.
        if (demo || restore && diagnosticsDirectory is not null) _appUpdatesOverride = new UpdateSupport(null, installed: false);
        _preferenceStore = preferences;
        _accountCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        ThemeManager.Initialize(Application.Current);
        InitializeComponent();
        WindowAppearance.Apply(this);
        InitializeSortingSources();
        _view = CollectionViewSource.GetDefaultView(_tracks);
        _view.Filter = value => value is TrackRow row && (_query.Length == 0 || row.Matches(_query));
        TracksList.ItemsSource = _view;
        InitializeFeatures();
        InitializeDesign();
        InitializeLyrics();
        LoadPreferences();
        InitializePlaybackOptions(audioOutputDevices);
        InitializeQueueEditing();
        InitializeSorting();
        InitializePlaybackPersistence();
        InitializeDownloadPersistence();
        DownloadCenterPanel.Content = DownloadCenterView;
        _metricsTimer.Tick += (_, _) => UpdateMetrics();
        _progressTimer.Tick += (_, _) => { UpdateProgress(); PersistPlaybackStateIfDue(); };
        _filterTimer.Tick += (_, _) =>
        {
            _filterTimer.Stop();
            _query = FilterBox.Text.Trim();
            _view.Refresh();
            UpdateCount();
        };
        _player.Ended += async (_, _) => { if (!HandlePlaybackOptionsTrackEnded()) await AdvanceAsync(1, true); };
        _player.TransitionFinished += OnTransitionFinished;
        _player.PlaybackActivityChanged += UpdateWaveListeningActivity;
        _player.Failed += (_, _) => { _progressTimer.Stop(); ReportWaveEnd(false); _diagnostics.Stage = "playback_error"; SetStatus("Windows прервала воспроизведение. Попробуйте снова или выберите другой трек.", true); UpdateControls(); UpdateProgress(); };
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
        if (SessionWriteAdmissionClosed || _demo || _session is not null || _authCts is not null) return;
        Session? saved;
        try
        {
            CompletePendingSessionRemoval();
            saved = _unactivatedSession ?? _store.Load();
        }
        catch (MusicException ex) { ShowError(ex); return; }
        catch (Exception ex) when (ex is CryptographicException or IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            SetStatus("Не удалось прочитать сохранённый вход. Отзовите прежний доступ устройства Yamuse в Яндекс ID и удалите повреждённую сохранённую сессию перед новым входом.", true);
            return;
        }
        if (saved is null) return;
        if (MusicApi.IsOAuthValue(saved.DeviceId)) _deviceId = saved.DeviceId;
        else saved = saved with { DeviceId = _deviceId };
        var accountToken = _accountCts.Token;
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _authCts = cts;
        LoginButton.Content = "Отменить";
        Busy(true);
        try
        {
            if (!MusicApi.HasPlayerScopes(saved))
            {
                await RevokeLegacyLoginAsync(saved, cts.Token);
                if (!_closed) ShowPlayerLoginRequired();
                return;
            }
            SetStatus("Восстанавливаем вход…");
            _unactivatedSession = saved;
            var alreadySaved = false;
            if (saved.ExpiresAtUtc <= DateTimeOffset.UtcNow.AddMinutes(1))
            {
                // The server can rotate the refresh token before profile loading.
                (saved, alreadySaved) = await RefreshAndSaveSessionAsync(saved, accountToken, cts);
            }
            cts.Token.ThrowIfCancellationRequested();
            await ActivateSessionAsync(saved, cts.Token, alreadySaved);
        }
        catch (OperationCanceledException) when (accountToken.IsCancellationRequested || SessionWriteAdmissionClosed) { }
        catch (OperationCanceledException) when (cts.IsCancellationRequested) { if (_session is null) SetStatus("Восстановление входа отменено."); }
        catch (Exception ex) { ReportLoginFailure(ex, accountToken, cts); }
        finally
        {
            if (ReferenceEquals(_authCts, cts))
            {
                _authCts = null;
                LoginButton.Content = "Войти в Яндекс";
                ShowPendingLoginRetry();
            }
            cts.Dispose();
            Busy(false);
        }
    }

    private async void Login_Click(object sender, RoutedEventArgs e)
    {
        if (_authCts is not null) { _authCts.Cancel(); return; }
        await StartLoginAsync();
    }

    internal async Task StartLoginAsync()
    {
        if (SessionWriteAdmissionClosed || _demo || _session is not null || _authCts is not null) return;
        if (_unactivatedSession is not null)
        {
            await RestoreAsync();
            return;
        }
        var accountToken = _accountCts.Token;
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
            await PreparePlayerLoginAsync(cts.Token);
            cts.Token.ThrowIfCancellationRequested();
            if (SessionWriteAdmissionClosed) return;
            _deviceCode = await _api.BeginLoginAsync(_deviceId, cts.Token);
            cts.Token.ThrowIfCancellationRequested();
            if (SessionWriteAdmissionClosed) return;
            CodeText.Text = _deviceCode.UserCode;
            CodePanel.Visibility = Visibility.Visible;
            LoginDescription.Text = "Откройте страницу Яндекса, введите этот код и подтвердите доступ. Пароль остаётся на стороне Яндекса.";
            SetStatus("Ожидаем подтверждение в браузере. Код действует ограниченное время.");
            if (_restore) OpenLoginPage(); // Test windows never launch a browser.
            var session = await _api.CompleteLoginAsync(_deviceCode, _deviceId, cts.Token);
            cts.Token.ThrowIfCancellationRequested();
            if (SessionWriteAdmissionClosed || accountToken.IsCancellationRequested
                || !ReferenceEquals(_authCts, cts) || _session is not null) return;
            _unactivatedSession = session;
            var alreadySaved = TrySaveSession(session);
            await ActivateSessionAsync(session, cts.Token, alreadySaved);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested) { if (!_closed && _session is null) SetStatus("Вход отменён."); }
        catch (Exception ex) { ReportLoginFailure(ex, accountToken, cts); }
        finally
        {
            if (ReferenceEquals(_authCts, cts))
            {
                _authCts = null; _deviceCode = null; CodeText.Clear(); CodePanel.Visibility = Visibility.Collapsed;
                LoginButton.Content = "Войти в Яндекс";
                if (_session is null) { LoginTitle.Text = "Начните со входа в аккаунт"; LoginDescription.Text = "Нажмите «Войти в Яндекс», чтобы получить новый код."; _diagnostics.Stage = "signed_out"; }
                ShowPendingLoginRetry();
            }
            cts.Dispose();
            Busy(false);
        }
    }

    private Task ActivateAsync(Session session, CancellationToken cancellationToken)
        => ActivateSessionAsync(session, cancellationToken, alreadySaved: false);

    private async Task ActivateSessionAsync(Session session, CancellationToken cancellationToken, bool alreadySaved)
    {
        if (SessionWriteAdmissionClosed) return;
        var account = await _api.GetAccountAsync(session, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (SessionWriteAdmissionClosed) return;
        TrackRow? lyricsToReload = null;
        if (_account?.Uid != account.Uid)
        {
            if (_account is not null) SuspendDownloadQueueForAccountChange();
            _accountGeneration++;
            ResetAccountCancellation();
            _playCts?.Cancel();
            CancelNextPreparation();
            lyricsToReload = ResetLyricsForAccountChange();
            ClearPlaybackContext(clearView: true);
            ResetFavoritesCacheAccount();
            _favoritesAccountUid = account.Uid;
        }
        _session = session;
        _unactivatedSession = null;
        _account = account;
        _deviceId = session.DeviceId;
        AccountText.Text = account.DisplayName;
        AccountText.DataContext = account;
        LoginPanel.Visibility = Visibility.Collapsed;
        LoginButton.Visibility = Visibility.Collapsed;
        LogoutButton.Visibility = Visibility.Visible;
        RefreshButton.Visibility = Visibility.Visible;
        FilterBox.IsEnabled = true;
        if (!alreadySaved) TrySaveSession(session);
        RestorePlaybackState(account.Uid);
        ReloadLyricsAfterAccountChange(lyricsToReload);
        await RestoreDownloadQueueAsync(account.Uid);
        await RefreshLibraryAsync();
    }

    private bool TrySaveSession(Session session)
    {
        if (PersistSessionChange(session)) return true;
        if (_session is null) return false;
        LoginDescription.Text = "Вход выполнен, но Windows пока не сохранила сессию. Yamuse повторит запись перед выходом или обновлением.";
        LoginPanel.Visibility = Visibility.Visible;
        LoginTitle.Text = "Сессия пока не сохранена";
        CodePanel.Visibility = Visibility.Collapsed;
        return false;
    }

    private async Task<Session> EnsureSessionAsync(CancellationToken cancellationToken)
    {
        var accountToken = _accountCts.Token;
        await _refreshLock.WaitAsync(cancellationToken);
        try
        {
            accountToken.ThrowIfCancellationRequested();
            cancellationToken.ThrowIfCancellationRequested();
            var session = _session ?? throw new MusicException("Войдите в аккаунт.", true);
            if (!MusicApi.HasPlayerScopes(session)) throw new MusicException("Войдите заново с доступом только к музыке.", true);
            if (session.ExpiresAtUtc > DateTimeOffset.UtcNow.AddMinutes(1)) return session;
            // Track and view cancellation must not discard a rotated refresh token.
            var (renewed, _) = await RefreshAndSaveSessionAsync(session, accountToken);
            accountToken.ThrowIfCancellationRequested();
            cancellationToken.ThrowIfCancellationRequested();
            return renewed;
        }
        finally { _refreshLock.Release(); }
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        if (_section == "home") await LoadHomeAsync();
        else if (_section == "wave") await StartWaveViewAsync(true);
        else if (_section == "offline") await ShowOffline();
        else if (!await RefreshCatalogViewAsync()) await RefreshLibraryAsync(refreshMetadata: true);
    }

    private void Logout_Click(object sender, RoutedEventArgs e) => SignOut();
    internal void SignOut()
    {
        ClearPlaybackContext(clearView: true);
        ResetFavoritesCacheAccount();
        var downloadsCleared = SuspendDownloadQueueForAccountChange();
        var playbackCleared = ClearPlaybackState();
        ResetFeatures();
        _authCts?.Cancel(); _libraryCts?.Cancel(); _playCts?.Cancel();
        _session = null; _account = null;
        _unactivatedSession = null;
        ResetAccountCancellation();
        _favoriteTrackIds.Clear(); _favoriteIdsKnown = false;
        _player.Stop(); _progressTimer.Stop();
        SetSleepTimer(SleepTimerMode.Off);
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
        var queueWarning = !downloadsCleared ? " Очередь загрузок осталась на диске." : "";
        if (!playbackCleared) queueWarning += " Очередь воспроизведения осталась на диске.";
        if (PersistSessionChange(null))
            SetStatus("Вы вышли из аккаунта. Сохранённый вход удалён с этого компьютера." + queueWarning, queueWarning.Length > 0);
        else
            SetStatus("Вход завершён в этом окне, но не удалось удалить сохранённую сессию. Yamuse повторит удаление перед выходом или обновлением." + queueWarning, true);
    }

    private async void PlaySelection_Click(object sender, RoutedEventArgs e) { if (_catalogShowing) await OpenSelectedCatalogAsync(); else await PlaySelectedAsync(); }
    private async void Tracks_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        var source = e.OriginalSource as DependencyObject;
        if (e.ChangedButton != MouseButton.Left || FromButton(source)) return;
        var index = TrackRowIndex(source);
        if (index < 0) return;
        e.Handled = true;
        await ActivateTrackRowAsync(index);
    }
    private void Tracks_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateControls();
    private bool HasTrackListPlaybackView => ContentGrid.Visibility == Visibility.Visible
        && TracksList.Visibility == Visibility.Visible && !_catalogShowing && _section != "download-center";

    private int TrackRowIndex(DependencyObject? source) => source is not null
        && ItemsControl.ContainerFromElement(TracksList, source) is ListViewItem row
        ? TracksList.ItemContainerGenerator.IndexFromContainer(row) : -1;

    private Task ActivateTrackRowAsync(int viewIndex)
    {
        if (!HasTrackListPlaybackView || viewIndex < 0 || viewIndex >= TracksList.Items.Count
            || TracksList.Items[viewIndex] is not TrackRow row || !CanNavigateToTrack(row)) return Task.CompletedTask;
        var queueIndex = _section == "queue" ? VisibleQueueOccurrences().Where(item => item.ViewIndex == viewIndex)
            .Select(item => item.QueueIndex).FirstOrDefault(-1) : -1;
        return PlayTrackFromListAsync(row, viewIndex, queueIndex);
    }

    private Task PlaySelectedAsync()
    {
        if (!HasTrackListPlaybackView) return Task.CompletedTask;
        var viewIndex = TracksList.SelectedItem is TrackRow chosen && CanNavigateToTrack(chosen) ? TracksList.SelectedIndex : -1;
        var selected = viewIndex >= 0 ? (TrackRow)TracksList.Items[viewIndex] : _view.Cast<TrackRow>().FirstOrDefault(CanNavigateToTrack);
        if (selected is null) return Task.CompletedTask;
        var queueIndex = _section != "queue" ? -1 : viewIndex >= 0 ? SelectedQueueIndex() : FirstVisiblePlayableQueueIndex();
        return PlayTrackFromListAsync(selected, viewIndex, queueIndex);
    }

    private async Task PlayTrackFromListAsync(TrackRow selected, int viewIndex, int queueIndex)
    {
        if (_demo) { SetStatus("Демо показывает 5 000 тестовых строк. Для настоящей музыки откройте обычный режим и войдите в Яндекс."); return; }
        if (_section == "queue")
        {
            if (queueIndex < 0 || !_playbackQueue.SelectAt(queueIndex)) return;
        }
        else
        {
            var rows = _view.Cast<TrackRow>().ToArray();
            if (viewIndex >= 0) _playbackQueue.SetItemsAt(rows, viewIndex);
            else _playbackQueue.SetItems(rows, selected);
            CapturePlaybackContext();
        }
        await PlayTrackAsync(selected);
    }

    internal Task PlayTrackAsync(TrackRow track) => PlayTrackCoreAsync(track, null, false);

    private async Task PlayTrackCoreAsync(TrackRow track, PreparedPlayback? prepared, bool automatic,
        long? advanceOperation = null, bool alreadyPromoted = false)
    {
        if (_closed) return;
        CancelPendingWavePlayback();
        ForgetRestoredPositionFor(track);
        ApplyTrackLikeStates([track]);
        var localSource = alreadyPromoted ? prepared!.Identity.LocalSource : RefreshLocalPlaybackSource(track);
        if (!alreadyPromoted)
        {
            if (localSource is { Bytes: not null } && BlocksOfflineOpen(track.Id)) return;
            if (localSource is null && (track.IsOfflineLibraryRow || track.LocalPath is not null))
            {
                CancelNextPreparation();
                SetStatus("Скачанный файл отсутствует или недоступен. Обновите офлайн-библиотеку.", true);
                return;
            }
            if (_session is null && localSource is null || !track.CanPlay) return;
        }
        if (!ReferenceEquals(_playbackQueue.Current, track))
        {
            if (_queue.Any(row => ReferenceEquals(row, track))) _playbackQueue.Select(track);
            else _playbackQueue.SetItems([track], track);
        }
        ValidatePlaybackContext();
        var promoted = alreadyPromoted || PromotePrepared(track, prepared, localSource, _playbackQueue.CurrentOccurrenceToken, automatic);
        if (!promoted) { CancelNextPreparation(); ReportWaveEnd(automatic); }
        _playbackGeneration++;
        _seekGesture?.Cancel(); _expandedSeekGesture?.Cancel();
        PlaybackTimeline.Stop(SeekSlider);
        _pendingSeek = null; _seekDragging = false;
        _playCts?.Cancel();
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _playCts = cts;
        _pendingOfflinePlayback = !promoted && localSource is { Bytes: not null } ? (track.Id, cts) : null;
        if (!promoted) _player.Stop();
        _progressTimer.Stop();
        if (_current is not null) _current.IsPlaying = false;
        HighlightTrack(null);
        _current = track;
        _loadingStream = true;
        TrackTitle.Text = track.Title; TrackArtist.Text = track.Artist;
        SetPlayerArtwork(track);
        QualityText.Text = "";
        SetStatus(promoted ? "" : localSource is not null ? "Открываем локальный трек…" : "Получаем аудиопоток…");
        _diagnostics.Stage = "resolving_stream";
        var clock = Stopwatch.StartNew();
        var playbackGeneration = _playbackGeneration;
        var accountGeneration = _accountGeneration;
        var occurrence = _playbackQueue.CurrentOccurrenceToken;
        try
        {
            if (promoted)
            {
                cts.Token.ThrowIfCancellationRequested();
                if (!OwnsOpenedTrack()) return;
                ValidateOpenedDuration();
                // Promotion has already made this exact source live. Commit its
                // lifecycle before any pause producer can cancel pending routing.
                CommitOpenedTrack(prepared!.Offline, prepared.QualityLabel);
                if (advanceOperation == _advanceOperation) _advancingPlayback = false;
            }
            // Queue selection and controls may synchronously expire a sleep
            // deadline. A live promoted source must already own its lifecycle.
            RefreshPlaybackView(); RefreshLyrics();
            UpdateControls(); UpdateProgress();
            await EnsureAudioOutputReadyAsync().WaitAsync(cts.Token);
            cts.Token.ThrowIfCancellationRequested();
            if (promoted) return; // Never repeat listening/track-change/save work.
            AudioDescriptor? audio = null;
            if (localSource is null)
            {
                var session = await EnsureSessionAsync(cts.Token);
                audio = await _api.ResolveAudioAsync(session, track.RequestId, SelectedQuality, cts.Token);
            }
            else
            {
                audio = await TryResolveOfflineUpgradeAsync(track, localSource.Path, SelectedQuality, cts.Token);
                cts.Token.ThrowIfCancellationRequested();
                if (audio is not null) SetStatus("Получаем аудиопоток…");
            }
            cts.Token.ThrowIfCancellationRequested();
            if (!OwnsOpenedTrack()) return;
            _diagnostics.Timing("stream_url_ms", clock.Elapsed.TotalMilliseconds);
            if (audio is not null)
            {
                var reportBufferProgress = true;
                var bufferProgress = new Progress<AudioProgress>(value =>
                {
                    if (!reportBufferProgress || !OwnsOpenedTrack()) return;
                    SetStatus($"Загрузка аудио · {value.Bytes / 1048576.0:F1}" + (value.Total.HasValue ? $" из {value.Total / 1048576.0:F1}" : "") + " МБ");
                });
                try
                {
                    await _player.OpenPausedAsync(audio, cts.Token, bufferProgress);
                    if (!OwnsOpenedTrack()) return;
                    ValidateOpenedDuration();
                    localSource = null;
                }
                catch (Exception error) when (localSource is not null && CanFallbackFromOnlineOpen(error) && OwnsOpenedTrack()
                    && !IsAudioOutputUnavailable && !BlocksOfflineOpen(track.Id))
                {
                    _diagnostics.LastErrorType = "OfflineUpgradeOpen:" + error.GetType().Name;
                    SetStatus("Открываем локальный трек…");
                }
                finally { reportBufferProgress = false; }
            }
            if (localSource is not null)
            {
                if (!OwnsOpenedTrack()) return;
                localSource = RefreshLocalPlaybackSource(track) ?? throw new MusicException("Скачанный файл отсутствует или недоступен.");
                await _player.OpenLocalPausedAsync(localSource, cts.Token);
                if (!OwnsOpenedTrack()) return;
                ValidateOpenedDuration();
            }
            cts.Token.ThrowIfCancellationRequested();
            if (!OwnsOpenedTrack()) return;
            ApplyRestoredPositionAfterOpen(track);
            _player.Toggle();
            CommitOpenedTrack(localSource is not null, localSource?.Quality ?? audio!.QualityLabel);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (OwnsOpenedTrack()) { _diagnostics.Stage = "playback_error"; ShowError(ex); }
        }
        finally
        {
            if (_pendingOfflinePlayback is { } pending && ReferenceEquals(pending.Request, cts)) _pendingOfflinePlayback = null;
            if (ReferenceEquals(_playCts, cts)) { _playCts = null; _loadingStream = false; UpdateControls(); UpdateProgress(); SyncPlaybackContextSelection(); }
            cts.Dispose();
        }

        bool OwnsOpenedTrack() => !_closed && !cts.IsCancellationRequested && ReferenceEquals(_playCts, cts)
            && playbackGeneration == _playbackGeneration && accountGeneration == _accountGeneration
            && ReferenceEquals(_current, track) && occurrence == _playbackQueue.CurrentOccurrenceToken;
        void ValidateOpenedDuration()
        {
            if (!LooksLikePreview(track, _player.Duration)) return;
            _player.Stop();
            throw PreviewDurationError();
        }
        void CommitOpenedTrack(bool playingOffline, string quality)
        {
            _diagnostics.Timing("play_requested_to_media_open_ms", clock.Elapsed.TotalMilliseconds);
            var playing = _player.IsPlaying;
            _diagnostics.Stage = !playing ? "paused" : playingOffline ? "playing_offline" : "playing";
            track.IsPlaying = true;
            HighlightTrack(track);
            QualityText.Text = (playingOffline ? "Офлайн · " : "") + quality;
            SetStatus(!playingOffline && _waveSources.ContainsKey(track) ? WaveSourceLabel(track) : "");
            BeginWaveListening(track);
            SetWavePaused(!playing);
            PlaybackOptionsTrackChanged();
            if (playing) _progressTimer.Start(); else _progressTimer.Stop();
            SavePlaybackState();
        }
    }

    private async void PlayPause_Click(object sender, RoutedEventArgs e) => await TogglePlaybackAsync();
    private async Task TogglePlaybackAsync()
    {
        if (_closed) return;
        if (_pendingWaveAdvance is not null || !_player.IsPlaying && _waveStartCts is { IsCancellationRequested: false })
        {
            CancelPendingWavePlayback();
            _diagnostics.Stage = "paused";
            SetStatus("Воспроизведение на паузе.");
            UpdateControls();
            return;
        }
        if (_automaticWaveCts is not null)
        {
            CancelPendingWavePlayback(); StopCompletedQueue(); SetStatus("Воспроизведение на паузе."); return;
        }
        // A promoted source already owns its lifecycle while readiness is pending.
        // Pause stays available without turning a still-unopened source into Play.
        if (_loadingStream && !_player.IsPlaying) return;
        // Pause stays available during routing. A blocked resume is not queued:
        // completing a device switch must never revive an obsolete Play intent.
        if (!_player.IsPlaying && (IsAudioOutputUnavailable || _audioOutputSwitching))
        {
            SetStatus(IsAudioOutputUnavailable
                ? "Аудиоустройство недоступно. Выберите устройство в настройках воспроизведения."
                : "Дождитесь завершения переключения аудиоустройства.", error: IsAudioOutputUnavailable);
            return;
        }
        if (!_player.HasSource)
        {
            if (!_demo && _queue.Count > 0) await PlayExistingQueueAsync();
            else if (_current is not null) await PlayTrackAsync(_current);
            else await PlaySelectedAsync();
            return;
        }
        _player.Toggle();
        if (_player.IsPlaying && _waveListening is null && _current is { } track
            && (_player.Duration <= TimeSpan.Zero || DisplayPosition(_player.Position) < _player.Duration))
            BeginWaveListening(track);
        SavePlaybackState();
        SetWavePaused(!_player.IsPlaying);
        _diagnostics.Stage = _player.IsPlaying ? "playing" : "paused";
        if (_player.IsPlaying) _progressTimer.Start(); else _progressTimer.Stop();
        UpdateControls();
    }

    private async void Previous_Click(object sender, RoutedEventArgs e)
    {
        if (_player.Position.TotalSeconds > 3) { CommitSeek(0); return; }
        await AdvanceAsync(-1, false);
    }
    private async void Next_Click(object sender, RoutedEventArgs e) => await AdvanceAsync(1, false);
    private Task AdvanceAsync(int direction, bool automatic) => AdvanceCoreAsync(direction, automatic);

    private async Task AdvanceCoreAsync(int direction, bool automatic, PreparedPlayback? earlyFade = null)
    {
        if (_closed || _queue.Count == 0 || _advancingPlayback && (automatic || _advancingWave)) return;
        var operation = ++_advanceOperation;
        _advancingPlayback = true;
        try
        {
        if (automatic && !_player.IsPlaying) ReportWaveEnd(true);
        // Before natural completion the queue and the audible slot stay on the
        // outgoing occurrence until the prepared decoder actually accepts Play.
        if (earlyFade is not null && (!ReferenceEquals(ReadyNextPlayback(automatic), earlyFade)
            || !PromotePrepared(earlyFade.Track, earlyFade, earlyFade.Identity.LocalSource, earlyFade.Identity.NextOccurrence, automatic)))
        {
            CancelNextPreparation();
            return;
        }
        var prepared = earlyFade ?? (direction > 0 && PeekPlayableNext(automatic) is not null ? ReadyNextPlayback(automatic) : null);
        var next = direction < 0 ? _playbackQueue.Previous() : _playbackQueue.Next(automatic);
        if (next is null && direction > 0 && IsContinuableWaveTrack(_current))
        {
            var account = _accountGeneration;
            var playbackGeneration = _playbackGeneration;
            var waveRequest = _waveRequestGeneration;
            var occurrence = _playbackQueue.CurrentOccurrenceToken;
            var pause = _wavePauseRevision;
            var supersededRefill = _waveExtensionTask is { IsCompleted: false }
                && _waveExtensionPlaybackGeneration != playbackGeneration;
            using var intent = automatic ? CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, _accountCts.Token) : null;
            if (intent is not null)
            {
                _pendingWaveAdvance = intent;
                _progressTimer.Stop();
                UpdateControls();
            }
            try
            {
                await ExtendWaveQueueAsync().WaitAsync(intent?.Token ?? CancellationToken.None);
                if (!CanContinue()) return;
                if (supersededRefill && PeekPlayableNext(automatic) is null)
                    await ExtendWaveQueueAsync().WaitAsync(intent?.Token ?? CancellationToken.None);
            }
            catch (OperationCanceledException) when (intent?.IsCancellationRequested == true) { return; }
            finally
            {
                if (intent is not null && ReferenceEquals(_pendingWaveAdvance, intent))
                {
                    _pendingWaveAdvance = null;
                    if (!_closed) UpdateControls();
                }
            }
            if (!CanContinue()) return;
            next = PeekPlayableNext(automatic) is null ? null : _playbackQueue.Next(automatic);

            bool CanContinue() => !_closed && account == _accountGeneration && playbackGeneration == _playbackGeneration
                && waveRequest == _waveRequestGeneration && occurrence == _playbackQueue.CurrentOccurrenceToken
                && intent?.IsCancellationRequested != true && pause == _wavePauseRevision
                && !IsAudioOutputUnavailable && operation == _advanceOperation
                && (!automatic || !_player.IsPlaying && CanAutoAdvancePlaybackOptions());
        }
        if (next is null)
        {
            if (automatic && direction > 0 && await TryStartAutomaticWaveAsync()) return;
            if (automatic)
            {
                StopCompletedQueue();
            }
            return;
        }
        // Navigation restarts even when the target is the restored TrackRow.
        // Only the initial Play action may consume its saved resume position.
        ForgetRestoredPositionFor(next, restart: true);
        await PlayTrackCoreAsync(next, prepared, automatic, operation, alreadyPromoted: earlyFade is not null);
        }
        finally
        {
            if (operation == _advanceOperation) { _advancingPlayback = false; RefreshNextPreparation(); }
        }
    }

    private void UpdateControls()
    {
        if (PlaySelectionButton is null || _updatingTrackRows) return;
        PlaybackOptionsPlaybackChanged();
        UpdateQueueEditingControls();
        UpdateQueuePlayingIndex();
        PlaySelectionButton.IsEnabled = _catalogShowing
            ? ContentGrid.Visibility == Visibility.Visible && CatalogList.Visibility == Visibility.Visible && CatalogList.SelectedItem is CatalogItem
            : HasTrackListPlaybackView && _view.Cast<TrackRow>().Any(row => row.CanPlay);
        RefreshTrackDownloadButton();
        PlayPauseButton.IsEnabled = PlaybackTransportActive || !_loadingStream
            && (_player.HasSource || _queue.Any(row => row.CanPlay)
                || _queue.Count == 0 && (_current is not null || HasTrackListPlaybackView && PlaySelectionButton.IsEnabled));
        PlayPauseButton.Content = PlaybackTransportActive ? "\uE769" : "\uE768";
        var playbackAction = PlaybackTransportActive ? "Пауза" : "Воспроизвести";
        AutomationProperties.SetName(PlayPauseButton, playbackAction);
        PlayPauseButton.ToolTip = playbackAction + " · Пробел";
        PreviousButton.IsEnabled = _current is not null && _queue.Count > 0;
        NextButton.IsEnabled = _playbackQueue.CanNext || !_advancingWave && IsContinuableWaveTrack(_current);
        SeekSlider.IsEnabled = !_loadingStream && _player.HasSource && _player.Duration.TotalSeconds > 0;
        UpdateAmbientMotion();
        RefreshPlaybackDisplay();
        UpdatePlayerLikeState();
        UpdateLyricsTimer();
        PlayerLyricsButton.IsEnabled = _current is not null;
        UpdatePlayerArtistLinks();
        ExpandPlayerButton.IsEnabled = _current is not null;
        RefreshExpandedPlayer();
        if (_current is null && _lyricsOpen) SetLyricsVisible(false);
        if (_current is null) Artwork.SetSourceUri(PlayerCover, null);
        UpdateWindowsMedia();
        UpdateQueueControls();
        UpdatePlaybackContextButton();
        UpdateWavePresentation();
        RefreshNextPreparation();
        UpdateShellState();
    }
    private void HighlightTrack(TrackRow? current)
    {
        foreach (var row in _tracks) row.IsPlaying = current is not null && row.RequestId == current.RequestId;
        UpdateQueuePlayingIndex();
    }
    private void UpdateProgress()
    {
        var duration = RestoredPlaybackDuration ?? _player.Duration;
        var position = RefreshPlaybackDisplay();
        SeekSlider.IsEnabled = !_loadingStream && _player.HasSource && duration.TotalSeconds > 0;
        UpdateLyrics(position.TotalSeconds);
        RefreshExpandedPlayer();
        UpdateShellState();
        TryAutomaticTransition();
    }
    private static string FormatTime(TimeSpan time) => time.TotalHours >= 1 ? time.ToString(@"h\:mm\:ss") : time.ToString(@"m\:ss");
    private void Seek_Start(object sender, MouseButtonEventArgs e) { _seekGesture?.Start(sender, e); if (_seekDragging) PositionText.Text = FormatTime(TimeSpan.FromSeconds(SeekSlider.Value)); }
    private void Seek_End(object sender, MouseButtonEventArgs e) => _seekGesture?.End(sender, e);
    private void Seek_KeyUp(object sender, KeyEventArgs e)
    {
        if (!SeekSlider.IsEnabled || !SliderGesture.SeekKey(e.Key)) return;
        if (_seekIgnoreKeyUp) { _seekIgnoreKeyUp = false; return; }
        if (_seekGesture?.IsActive == true) _seekGesture.Complete(true);
        else { PlaybackTimeline.Stop(SeekSlider, useBaseValue: true); CommitSeek(SeekSlider.Value); }
    }
    private void Volume_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (e.NewValue > 0) _volumeBeforeMute = e.NewValue;
        _player.SetVolume(e.NewValue / 100);
        if (SliderGesture.HasActiveGesture || Mouse.LeftButton == MouseButtonState.Pressed)
        { _volumeSaveTimer.Stop(); _volumeSaveTimer.Start(); }
        else { _volumeSaveTimer.Stop(); SavePreferences(); }
        UpdateShellState();
    }
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
        EmptyState.Visibility = visible > 0 || _section == "download-center" ? Visibility.Collapsed : Visibility.Visible;
        EmptyTitle.Text = _query.Length > 0 ? "Ничего не найдено" : _section == "offline" ? "Пока ничего не скачано" : _section == "wave" ? "Запускаем Мою волну" : _session is not null ? "В избранном пока нет треков" : "Здесь появится избранное";
        EmptyDescription.Text = _query.Length > 0 ? "Попробуйте другое название или исполнителя" : _section == "offline" ? "Выберите трек и нажмите «Скачать»" : _session is not null ? "Если коллекция ещё загружается, подождите немного" : "Войдите в Яндекс, чтобы загрузить любимые треки";
        UpdateCatalogCount();
        // A selection or transport change does not change thousands of offline files.
        // Refresh this status when the list changes; downloads/removals also refresh it explicitly.
        InvalidatePlaylistStatus();
        UpdatePlaylistDownloadedStatus();
        UpdateControls();
    }
    private void Busy(bool start)
    {
        _busy = Math.Max(0, _busy + (start ? 1 : -1));
        UpdateStatusNotificationLifetime();
    }
    private void SetStatus(string text, bool error = false)
    {
        if (_closed) return;
        var changed = !string.Equals(StatusText.Text, text, StringComparison.Ordinal);
        StatusText.Text = text;
        ShowStatusNotification(error);
        if (changed && !string.IsNullOrWhiteSpace(text) && AutomationPeer.ListenerExists(AutomationEvents.LiveRegionChanged))
            UIElementAutomationPeer.CreatePeerForElement(StatusText)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
        WaveStatus.Text = text;
        var inlineStatusBrush = (Brush)FindResource(error ? "ErrorBrush" : "MutedBrush");
        WaveStatus.Foreground = inlineStatusBrush;
        HomeStatus.Text = ArtistStatus.Text = text;
        HomeStatus.Foreground = ArtistStatus.Foreground = inlineStatusBrush;
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
        CpuText.Text = $"CPU {sample.CpuPercent:F2}%";
    }
    private void SaveDiagnostics_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog { Filter = "Диагностика JSON|*.json", FileName = $"Yamuse-metrics-{DateTime.Now:yyyyMMdd-HHmmss}.json", AddExtension = true };
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
        if (_closed || e.Handled) return;
        var key = e.Key;
        var source = e.OriginalSource as DependencyObject;
        var keyboard = e.KeyboardDevice;
        var modifiers = keyboard.Modifiers;
        // WPF's Modifiers property reports Alt/Control/Shift, but omits Win.
        var windowsKey = keyboard.IsKeyDown(Key.LWin) || keyboard.IsKeyDown(Key.RWin);
        if (key == Key.Z && modifiers == ModifierKeys.Control && !windowsKey && _section == "queue"
            && !_settingsOpen && !_lyricsOpen && !IsExpandedPlayerOpen && !IsPlaybackTextInput(source))
        { e.Handled = true; UndoQueueRemoval_Click(sender, e); return; }
        if ((modifiers & (ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Windows)) != 0 || windowsKey) return;
        if (key == Key.Escape && IsExpandedPlayerOpen) { e.Handled = true; RequestExpandedPlayerClose(); return; }
        if (key == Key.Escape && _settingsOpen) { e.Handled = true; SettingsClose_Click(sender, e); return; }
        if (key == Key.F11)
        {
            e.Handled = true;
            if (!e.IsRepeat) ExpandPlayer_Click(sender, e);
            return;
        }
        if (_expandedCollapse is { } collapse && (ReferenceEquals(sender, _expandedFullscreenHost?.Window)
            || source is Visual visual && visual.IsDescendantOf(collapse.Stage)))
        { e.Handled = true; return; }
        var mediaKey = key is Key.MediaPlayPause or Key.MediaNextTrack or Key.MediaPreviousTrack;
        if (mediaKey && _windows?.HandlesMediaKeys == true) return;
        if (_settingsOpen && !mediaKey && key is not (Key.P or Key.M or Key.L or Key.OemOpenBrackets or Key.OemCloseBrackets
            or Key.Add or Key.OemPlus or Key.Subtract or Key.OemMinus)) return;
        if (key == Key.Escape && _lyricsOpen) { e.Handled = true; SetLyricsVisible(false); PlayerLyricsButton.Focus(); return; }
        if (!mediaKey && IsPlaybackTextInput(source)) return;
        if ((key is Key.Space or Key.Enter) && (source is ButtonBase || FromButton(source))) return;
        if (key is Key.Add or Key.OemPlus) { e.Handled = true; VolumeSlider.Value = Math.Clamp(VolumeSlider.Value + 10, 0, 100); return; }
        if (key is Key.Subtract or Key.OemMinus) { e.Handled = true; VolumeSlider.Value = Math.Clamp(VolumeSlider.Value - 10, 0, 100); return; }
        if (!mediaKey && source is Slider && key is not (Key.P or Key.M or Key.L or Key.OemOpenBrackets or Key.OemCloseBrackets)) return;
        if (e.IsRepeat && (key is Key.Space or Key.P or Key.M or Key.L)) { e.Handled = true; return; }
        if (key is Key.Space or Key.P or Key.MediaPlayPause) { e.Handled = true; await TogglePlaybackAsync(); }
        else if (key is Key.OemCloseBrackets or Key.MediaNextTrack) { e.Handled = true; await AdvanceAsync(1, false); }
        else if (key is Key.OemOpenBrackets or Key.MediaPreviousTrack) { e.Handled = true; await AdvanceAsync(-1, false); }
        else if (key == Key.M) { e.Handled = true; VolumeSlider.Value = VolumeSlider.Value > 0 ? 0 : Math.Clamp(_volumeBeforeMute, 0, 100); }
        else if (key == Key.L) { e.Handled = true; await ToggleCurrentLikeAsync(); }
        else if (key == Key.Enter && TracksList.IsKeyboardFocusWithin)
        {
            e.Handled = true;
            var rowIndex = TrackRowIndex(source);
            await ActivateTrackRowAsync(rowIndex >= 0 ? rowIndex : TracksList.SelectedIndex);
        }
        else if (key == Key.Enter && CatalogList.IsKeyboardFocusWithin) { e.Handled = true; await OpenSelectedCatalogAsync(); }
    }
    private static bool IsPlaybackTextInput(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is TextBoxBase or PasswordBox or ComboBox or ComboBoxItem or MenuItem) return true;
            source = source is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(source)
                : source is FrameworkContentElement content ? content.Parent : LogicalTreeHelper.GetParent(source);
        }
        return false;
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
        if (!_closeCommitApproved)
        {
            SavePlaybackState();
            SavePreferences();
        }
        SaveDownloadQueueForClose();
        DisposePlaybackOptions();
        ClearQueueDropLine();
        SuspendDownloadQueueForAccountChange();
        _closed = true;
        ClearPlaybackContext(clearView: true);
        CloseSorting();
        _lifetime.Cancel(); _authCts?.Cancel(); _libraryCts?.Cancel(); _playCts?.Cancel();
        CancelNextPreparation();
        _accountCts.Cancel();
        _metricsTimer.Stop(); _progressTimer.Stop(); _filterTimer.Stop(); _volumeSaveTimer.Stop();
        _seekGesture?.Cancel(); _expandedSeekGesture?.Cancel(); _volumeGesture?.Cancel(); _expandedVolumeGesture?.Cancel();
        PlaybackTimeline.Stop(SeekSlider); if (_expandedPlayerSeek is not null) PlaybackTimeline.Stop(_expandedPlayerSeek);
        _player.Dispose();
        _shell?.Dispose();
        _windows?.Dispose();
        CloseDiscovery();
        CloseFeatures();
        try { _diagnostics.SaveAuto(_diagnosticsDirectory); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        _diagnostics.Dispose(); _api.Dispose();
        // Cancellation sources and refresh lock are released by their outstanding async operations.
    }
}
