using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Net.Http;
using System.Runtime.InteropServices;
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
        AttachMediaCommands(this);
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
        _player.Ended += async (_, _) =>
        {
            if (_closed) return;
            var deferred = TryDeferQueueTrackEnd();
            if (_waveAdvanceWait is { } wait && OwnsWaveAdvance(wait))
            {
                wait.NaturalEndDuringWait = true;
                if (!wait.Automatic) deferred = RememberDeferredQueueTrackEnd();
            }
            _progressTimer.Stop();
            ReportWaveEnd(true);
            if (HandlePlaybackOptionsTrackEnded()) return;
            if (deferred) UpdateControls();
            else await AdvanceAsync(1, true);
        };
        _player.TransitionFinished += OnTransitionFinished;
        _player.PlaybackActivityChanged += OnNativePlaybackActivityChanged;
        _player.Failed += OnNativePlaybackFailed;
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

    private async Task<bool> RestoreAsync()
    {
        if (SessionWriteAdmissionClosed || _demo || _session is not null || _authCts is not null) return true;
        Session? saved;
        try
        {
            CompletePendingSessionRemoval();
            saved = _unactivatedSession;
            if (saved is null)
            {
                saved = ReadSavedSession();
                _sessionSaveSuperseded = false;
                _ownedStoredSession = saved is not null && MusicApi.HasPlayerScopes(saved) ? saved : null;
            }
        }
        catch (MusicException ex) { ShowError(ex); ShowLoginRetry(); return true; }
        if (saved is null) return false;
        if (MusicApi.IsOAuthValue(saved.DeviceId)) _deviceId = saved.DeviceId;
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
                if (!SessionWriteAdmissionClosed) ShowPlayerLoginRequired();
                return true;
            }
            if (!MusicApi.IsOAuthValue(saved.DeviceId))
            {
                var original = saved;
                saved = saved with { DeviceId = _deviceId };
                _pendingSessionChange = new(_accountGeneration, saved, original, WaitForProfile: true);
            }
            SetStatus("Восстанавливаем вход…");
            _unactivatedSession = saved;
            if (saved.DeviceProfilePending || saved.ProfileRefreshValidated)
            {
                await ActivatePendingProfileAsync(saved, accountToken, cts);
                return true;
            }
            var refreshFirst = saved.ExpiresAtUtc <= DateTimeOffset.UtcNow.AddMinutes(1);
            if (refreshFirst)
            {
                // The server can rotate the refresh token before profile loading.
                saved = await RefreshAndSaveSessionAsync(saved, accountToken, cts);
            }
            cts.Token.ThrowIfCancellationRequested();
            try { await ActivateSessionAsync(saved, cts.Token, refreshedForRestore: saved.ProfileRefreshValidated); }
            catch (MusicException error) when (error.Authentication && !refreshFirst && _session is null
                && !cts.IsCancellationRequested && !accountToken.IsCancellationRequested
                && ReferenceEquals(_authCts, cts) && ReferenceEquals(_unactivatedSession, saved)
                && MusicApi.IsOAuthValue(saved.RefreshToken))
            {
                // A resource-only refusal does not retire a refresh grant when close or update defers the exchange.
                if (SessionWriteAdmissionClosed)
                {
                    if (!SessionIsExiting) SetStatus("Восстановление входа приостановлено.");
                    return true;
                }
                // Music API rejected the access token, independently of its stored expiry.
                saved = await RefreshAndSaveSessionAsync(saved, accountToken, cts);
                cts.Token.ThrowIfCancellationRequested();
                await ActivateSessionAsync(saved, cts.Token, refreshedForRestore: true);
            }
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
                ShowLoginRetry();
            }
            cts.Dispose();
            Busy(false);
        }
        return true;
    }

    private async void Login_Click(object sender, RoutedEventArgs e)
    {
        if (_authCts is not null) { _authCts.Cancel(); return; }
        await StartLoginAsync();
    }

    internal async Task StartLoginAsync()
    {
        if (SessionWriteAdmissionClosed || _demo || _session is not null || _authCts is not null) return;
        if ((_unactivatedSession is not null || _savedSessionReadNeedsRetry) && await RestoreAsync()) return;
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
            var expectedStoredSession = await PreparePlayerLoginAsync(cts.Token);
            cts.Token.ThrowIfCancellationRequested();
            if (SessionWriteAdmissionClosed) return;
            _deviceCode = await _api.BeginLoginAsync(_deviceId, cts.Token);
            cts.Token.ThrowIfCancellationRequested();
            if (SessionIsExiting || accountToken.IsCancellationRequested || !ReferenceEquals(_authCts, cts)) return;
            if (_deviceCode.RemainingLifetime <= TimeSpan.Zero) throw MusicApi.DeviceCodeExpired();
            CodeText.Text = _deviceCode.UserCode;
            CodePanel.Visibility = Visibility.Visible;
            LoginDescription.Text = "Откройте страницу Яндекса, введите этот код и подтвердите доступ. Пароль остаётся на стороне Яндекса.";
            SetStatus("Ожидаем подтверждение в браузере. Код действует ограниченное время.");
            if (_restore && !SessionWriteAdmissionClosed) OpenLoginPage(); // Test windows never launch a browser.
            var session = await _api.CompleteLoginAsync(_deviceCode, _deviceId, cts.Token,
                (send, pollToken) => AdmitDeviceTokenPollAsync(send, pollToken, cts, accountToken, expectedStoredSession));
            cts.Token.ThrowIfCancellationRequested();
            if (SessionWriteAdmissionClosed || accountToken.IsCancellationRequested
                || !ReferenceEquals(_authCts, cts) || !ReferenceEquals(_unactivatedSession, session) || _session is not null) return;
            await ActivatePendingProfileAsync(session, accountToken, cts);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested) { if (!_closed && _session is null) SetStatus("Вход отменён."); }
        catch (OperationCanceledException) when (SessionWriteAdmissionClosed || accountToken.IsCancellationRequested) { }
        catch (Exception ex) { ReportLoginFailure(ex, accountToken, cts); }
        finally
        {
            if (ReferenceEquals(_authCts, cts))
            {
                _authCts = null; _deviceCode = null; CodeText.Clear(); CodePanel.Visibility = Visibility.Collapsed;
                LoginButton.Content = "Войти в Яндекс";
                if (_session is null) _diagnostics.Stage = "signed_out";
                ShowLoginRetry(offerNewCode: true);
            }
            cts.Dispose();
            Busy(false);
        }
    }

    private Task ActivateAsync(Session session, CancellationToken cancellationToken)
        => ActivateSessionAsync(session, cancellationToken, skipInitialSave: false);

    private async Task ActivateSessionAsync(Session session, CancellationToken cancellationToken, bool skipInitialSave = true, bool refreshedForRestore = false)
    {
        if (SessionWriteAdmissionClosed) return;
        if (!skipInitialSave)
            _pendingSessionChange = new(_accountGeneration, session, _ownedStoredSession, WaitForProfile: true);
        Account account;
        try { account = await _api.GetAccountAsync(session, cancellationToken); }
        catch (MusicException error) when (refreshedForRestore && error.Authentication)
        {
            // Music API rejects the access token. Only OAuth can reject the validated refresh grant.
            throw UnverifiedRefreshedProfile();
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (SessionWriteAdmissionClosed) return;
        // Exit preparation can already have resolved this save while the profile was pending.
        var pendingSave = PendingSaveFor(session);
        if (session.DeviceProfilePending || session.ProfileRefreshValidated)
        {
            // A published pending grant needs one conditional write to record profile confirmation.
            if (pendingSave is null && _ownedStoredSession == session)
                pendingSave = new(_accountGeneration, session, _ownedStoredSession);
            session = session with { DeviceProfilePending = false, ProfileRefreshValidated = false };
        }
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
        _activeRefreshRejection = null;
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
        if (pendingSave is not null)
            TrySaveSession(session, pendingSave.ExpectedStoredSession);
        RestorePlaybackState(account.Uid);
        _ = RetryPlaybackRestoreAfterRecoveryAsync();
        ReloadLyricsAfterAccountChange(lyricsToReload);
        await RestoreDownloadQueueAsync(account.Uid);
        await RefreshLibraryAsync();
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
            if (_activeRefreshRejection is { } rejection && rejection.AccountGeneration == _accountGeneration
                && ReferenceEquals(rejection.Session, session))
                throw new RefreshGrantRejectedException();
            if (!MusicApi.HasPlayerScopes(session)) throw new MusicException("Войдите заново с доступом только к музыке.", true);
            if (session.ExpiresAtUtc > DateTimeOffset.UtcNow.AddMinutes(1)) return session;
            // Track and view cancellation must not discard a rotated refresh token.
            var renewed = await RefreshAndSaveSessionAsync(session, accountToken);
            accountToken.ThrowIfCancellationRequested();
            cancellationToken.ThrowIfCancellationRequested();
            return renewed;
        }
        finally { _refreshLock.Release(); }
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        if (HasPendingOfflineNavigation) return;
        if (_section == "home") await LoadHomeAsync();
        else if (_section == "wave") await StartWaveViewAsync(true);
        else if (_section == "offline") await ShowOffline();
        else if (!await RefreshCatalogViewAsync()) await RefreshLibraryAsync(refreshMetadata: true);
        await RetryPlaybackRestoreAfterRecoveryAsync();
    }

    private void Logout_Click(object sender, RoutedEventArgs e) => SignOut();
    internal void SignOut()
    {
        var storedSession = _ownedStoredSession
            ?? (_pendingSessionChange is { Session: null } removal ? removal.ExpectedStoredSession : null);
        ClearPlaybackContext(clearView: true);
        ResetFavoritesCacheAccount();
        var downloadsCleared = SuspendDownloadQueueForAccountChange(explicitLogout: true);
        var playbackCleared = ClearPlaybackState(_account?.Uid);
        ResetFeatures();
        _authCts?.Cancel(); _libraryCts?.Cancel(); _playCts?.Cancel();
        _session = null; _account = null;
        _activeRefreshRejection = null;
        _unactivatedSession = null;
        _sessionSaveSuperseded = false;
        _ownedStoredSession = null;
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
        if (!playbackCleared) queueWarning += " Очередь воспроизведения пока не удалена. Yamuse проверит удаление при входе, выходе или обновлении.";
        var result = RetireStoredSession(storedSession);
        var message = result switch
        {
            SessionChangeResult.Failed => "Вход завершён в этом окне, но не удалось удалить сохранённую сессию. Yamuse повторит удаление перед выходом или обновлением.",
            SessionChangeResult.Superseded => "Вы вышли из аккаунта в этом окне. Сохранённый вход изменился.",
            _ when storedSession is not null => "Вы вышли из аккаунта. Сохранённый вход удалён с этого компьютера.",
            _ => "Вы вышли из аккаунта."
        };
        SetStatus(message + queueWarning, result != SessionChangeResult.Applied || queueWarning.Length > 0);
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
    private void Tracks_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_applyingPlaylistSelection) return;
        ObservePlaylistSelection(e);
        UpdateControls();
    }
    private bool HasTrackListPlaybackView => ContentGrid.Visibility == Visibility.Visible
        && TracksList.Visibility == Visibility.Visible && !_catalogShowing && _section != "download-center";

    private int TrackRowIndex(DependencyObject? source) => source is not null
        && ItemsControl.ContainerFromElement(TracksList, source) is ListViewItem row
        ? TracksList.ItemContainerGenerator.IndexFromContainer(row) : -1;

    private async Task ActivateTrackRowAsync(int viewIndex)
    {
        if (!HasTrackListPlaybackView || viewIndex < 0 || viewIndex >= TracksList.Items.Count
            || TracksList.Items[viewIndex] is not TrackRow row) return;
        var queueIndex = _section == "queue" ? VisibleQueueOccurrences().Where(item => item.ViewIndex == viewIndex)
            .Select(item => item.QueueIndex).FirstOrDefault(-1) : -1;
        if (CanNavigateToTrack(row, requireExactOfflineVariant: true))
        {
            await PlayTrackFromListAsync(row, viewIndex, queueIndex);
            return;
        }
        var view = _viewGeneration;
        await RetryExactOfflineRowAsync(row, allowUnavailableCatalog: queueIndex >= 0,
            () => view == _viewGeneration && HasTrackListPlaybackView && viewIndex < TracksList.Items.Count
                && ReferenceEquals(row, TracksList.Items[viewIndex]),
            admission => PlayTrackFromListAsync(row, viewIndex, queueIndex, admission));
    }

    private Task PlaySelectedAsync()
    {
        if (!HasTrackListPlaybackView) return Task.CompletedTask;
        if (TracksList.SelectedIndex >= 0) return ActivateTrackRowAsync(TracksList.SelectedIndex);
        if (_section == "queue") return PlayFirstVisibleQueueOccurrenceAsync();
        var selected = _view.Cast<TrackRow>().FirstOrDefault(row => CanNavigateToTrack(row));
        if (selected is null) return Task.CompletedTask;
        return PlayTrackFromListAsync(selected, -1, -1);
    }

    private async Task PlayTrackFromListAsync(TrackRow selected, int viewIndex, int queueIndex, NativePlayer.LocalPlaybackAdmission? localAdmission = null)
    {
        if (_demo) { SetStatus("Демо показывает 5 000 тестовых строк. Для настоящей музыки откройте обычный режим и войдите в Яндекс."); return; }
        var requireExactOfflineVariant = viewIndex >= 0;
        if (!CanSelectWhileRemovingAudio(selected, requireExactOfflineVariant)) return;
        if (_section == "queue")
        {
            if (queueIndex < 0 || queueIndex >= _queue.Count || !ReferenceEquals(_queue[queueIndex], selected)
                || !_playbackQueue.SelectAt(queueIndex)) return;
        }
        else
        {
            if (_activePlaylist is not null) UsePlaybackContextSelection();
            var rows = _view.Cast<TrackRow>().ToArray();
            if (viewIndex >= 0) _playbackQueue.SetItemsAt(rows, viewIndex);
            else _playbackQueue.SetItems(rows, selected);
            CapturePlaybackContext();
        }
        await PlayTrackCoreAsync(selected, null, false, requireExactOfflineVariant: requireExactOfflineVariant, localAdmission: localAdmission);
    }

    internal Task PlayTrackAsync(TrackRow track) => PlayTrackCoreAsync(track, null, false);

    private Task PlayTrackCoreAsync(TrackRow track, PreparedPlayback? prepared, bool automatic,
        long? advanceOperation = null, bool alreadyPromoted = false, bool requireExactOfflineVariant = false,
        NativePlayer.LocalPlaybackAdmission? localAdmission = null)
        => PlayTrackWithSourceAsync(track, prepared, automatic, advanceOperation, alreadyPromoted, requireExactOfflineVariant, localAdmission);

    private async Task PlayTrackWithSourceAsync(TrackRow track, PreparedPlayback? prepared, bool automatic,
        long? advanceOperation = null, bool alreadyPromoted = false, bool requireExactOfflineVariant = false,
        NativePlayer.LocalPlaybackAdmission? localAdmission = null, bool streamOnly = false, bool continueAfterEnd = false,
        NativePlayer.WaveFallbackAdmission? waveFallback = null)
    {
        if (_closed || streamOnly && !CanStreamAfterLocalIntegrityFailure(track)) return;
        var retryingPausedOccurrence = ReferenceEquals(track, _current) && CanResumePausedDesktopOccurrence;
        CancelPendingWavePlayback(preserveQueuedNavigation: advanceOperation.HasValue);
        ForgetRestoredPositionFor(track);
        ApplyTrackLikeStates([track]);
        var localSource = alreadyPromoted ? prepared!.Identity.LocalSource
            : streamOnly ? null : localAdmission?.Source ?? RefreshLocalPlaybackSource(track, requireExactOfflineVariant);
        if (!alreadyPromoted)
        {
            if (localSource is { Bytes: not null } && BlocksOfflineOpen(track.Id)) return;
            if (localSource is null && (track.IsOfflineLibraryRow || track.LocalPath is not null))
            {
                CancelNextPreparation();
                SetStatus("Скачанный файл отсутствует или недоступен. Обновите офлайн-библиотеку.", true);
                return;
            }
            if (localSource is null && (_session is null || !track.CanPlay)) return;
        }
        if (!ReferenceEquals(_playbackQueue.Current, track))
        {
            if (_queue.Any(row => ReferenceEquals(row, track))) _playbackQueue.Select(track);
            else _playbackQueue.SetItems([track], track);
        }
        ValidatePlaybackContext();
        var promoted = alreadyPromoted;
        _playbackGeneration++;
        _seekGesture?.Cancel(); _expandedSeekGesture?.Cancel();
        PlaybackTimeline.Stop(SeekSlider);
        _pendingSeek = null; _seekDragging = false;
        _playCts?.Cancel();
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _playCts = cts;
        _pendingOfflinePlayback = !promoted && localSource is { Bytes: not null } ? (track.Id, cts) : null;
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
        var coldContinuation = !promoted && (automatic || continueAfterEnd || retryingPausedOccurrence || waveFallback is not null);
        if (coldContinuation)
            _automaticTrackStart = CaptureQueuePlayback(cts);
        if (retryingPausedOccurrence) RememberPausedDesktopOccurrence();
        try
        {
            if (!promoted)
            {
                // Stop retires both the old current and prepared slots. Do not
                // retire the prepared one separately before this cue owns errors.
                CancelNextPreparation(discardPlayer: false);
                ReportWaveEnd(automatic);
                if (waveFallback is null) _player.Stop();
                else if (!waveFallback.TryAdopt())
                {
                    _player.Stop();
                    throw new MusicException("Не удалось открыть подготовленный источник. Попробуйте снова.");
                }
            }
            if (promoted)
            {
                cts.Token.ThrowIfCancellationRequested();
                if (!OwnsOpenedTrack()) return;
                ValidateOpenedDuration();
                // Promotion has already made this exact source live. Commit its
                // lifecycle before any pause producer can cancel pending routing.
                CommitOpenedTrack(prepared!.Offline, prepared.QualityLabel, prepared.OpenedLocalSource);
                if (advanceOperation == _advanceOperation) _advancingPlayback = false;
            }
            // Queue selection and controls may synchronously expire a sleep
            // deadline. A live promoted source must already own its lifecycle.
            RefreshPlaybackView(); RefreshLyrics();
            UpdateControls(); UpdateProgress();
            var admittedMinuteSleep = ActiveMinuteSleepRevision;
            await EnsureAudioOutputReadyAsync(cts.Token);
            cts.Token.ThrowIfCancellationRequested();
            if (promoted) return; // Never repeat listening/track-change/save work.
            AudioDescriptor? audio = waveFallback?.Audio;
            if (localSource is null && audio is null)
            {
                var session = await EnsureSessionAsync(cts.Token);
                if (!OwnsOpenedTrack()) return;
                audio = await _api.ResolveAudioAsync(session, track.RequestId, SelectedQuality, cts.Token);
            }
            else if (localSource is not null)
            {
                audio = await TryResolveOfflineUpgradeAsync(track, localSource.Path, SelectedQuality, cts.Token);
                cts.Token.ThrowIfCancellationRequested();
                if (audio is not null) SetStatus("Получаем аудиопоток…");
            }
            await EnsureAudioOutputReadyAsync(cts.Token);
            if (!OwnsOpenedTrack()) return;
            _diagnostics.Timing("stream_url_ms", clock.Elapsed.TotalMilliseconds);
            if (audio is not null && waveFallback is null)
            {
                try
                {
                    await OpenOnlineAsync(audio);
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
            }
            if (localSource is not null)
            {
                if (!OwnsOpenedTrack()) return;
                if (BlocksOfflineOpen(track.Id) && localSource.Bytes is not null) return;
                localSource = localAdmission?.Source ?? RefreshLocalPlaybackSource(track, requireExactOfflineVariant)
                    ?? throw new MusicException("Скачанный файл отсутствует или недоступен.");
                try
                {
                    localSource = await OpenLocalPlaybackVariantAsync(track, localSource,
                        requireExactOfflineVariant && track.IsOfflineLibraryRow, cts.Token,
                        candidate => _player.OpenLocalPausedAsync(candidate, cts.Token, localAdmission));
                }
                catch (LocalAudioIntegrityException error)
                {
                    if (!OwnsOpenedTrack()) return;
                    _diagnostics.LastErrorType = nameof(LocalAudioIntegrityException);
                    if (error.Revision is null) ApplyOfflineMarks([new(track, null, null)]);
                    if (audio is not null || !CanStreamAfterLocalIntegrityFailure(track))
                    {
                        _diagnostics.Stage = "playback_error";
                        SetStatus(error.Message, true);
                        return;
                    }
                    SetStatus("Скачанный файл изменился. Получаем аудиопоток…");
                    var session = await EnsureSessionAsync(cts.Token);
                    audio = await _api.ResolveAudioAsync(session, track.RequestId, SelectedQuality, cts.Token);
                    await EnsureAudioOutputReadyAsync(cts.Token);
                    if (!OwnsOpenedTrack()) return;
                    await OpenOnlineAsync(audio);
                    localSource = null;
                }
                if (!OwnsOpenedTrack()) return;
                ValidateOpenedDuration();
            }
            try
            {
                // The decoder opened paused. A switch that started while opening
                // must finish routing this exact slot before it becomes audible.
                do { await EnsureAudioOutputReadyAsync(cts.Token); } while (AudioOutputChangePending);
            }
            catch
            {
                if (!_closed && ReferenceEquals(_playCts, cts)) _player.Stop();
                throw;
            }
            if (!OwnsOpenedTrack()) return;
            if ((coldContinuation || admittedMinuteSleep is not null && admittedMinuteSleep == ActiveMinuteSleepRevision)
                && HasMinuteSleepExpired())
            {
                if (!OwnsOpenedTrack()) return;
                // An unapplied cold source has no listening lifecycle to keep.
                _player.Stop();
                FinishSleepTimer();
                return;
            }
            ApplyRestoredPositionAfterOpen(track);
            if (!_player.Toggle())
                throw new MusicException("Windows не удалось начать воспроизведение. Попробуйте ещё раз.");
            if (ReferenceEquals(_automaticTrackStart?.Request, cts)) _automaticTrackStart = null;
            _pausedDesktopOccurrence = null;
            ClearRestoredPosition();
            CommitOpenedTrack(localSource is not null, localSource?.Quality ?? audio!.QualityLabel, localSource);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (OwnsOpenedTrack())
            {
                if (!promoted && !_player.IsPlaying)
                {
                    if (coldContinuation) RememberPausedDesktopOccurrence();
                    _pendingSeek = null;
                    // A provisional decoder cannot be resumed by the ordinary Toggle path.
                    try { _player.Stop(); }
                    catch (Exception cleanup) when (cleanup is System.Runtime.InteropServices.COMException or InvalidOperationException) { }
                }
                _diagnostics.Stage = "playback_error";
                ShowError(ex);
            }
        }
        finally
        {
            if (_pendingOfflinePlayback is { } pending && ReferenceEquals(pending.Request, cts)) _pendingOfflinePlayback = null;
            if (ReferenceEquals(_automaticTrackStart?.Request, cts)) _automaticTrackStart = null;
            if (ReferenceEquals(_playCts, cts)) { _playCts = null; _loadingStream = false; UpdateControls(); UpdateProgress(); SyncPlaybackContextSelection(); }
            cts.Dispose();
        }

        bool OwnsOpenedTrack() => !_closed && !cts.IsCancellationRequested && ReferenceEquals(_playCts, cts)
            && playbackGeneration == _playbackGeneration && accountGeneration == _accountGeneration
            && ReferenceEquals(_current, track) && occurrence == _playbackQueue.CurrentOccurrenceToken;
        async Task OpenOnlineAsync(AudioDescriptor source)
        {
            var reportBufferProgress = true;
            var bufferProgress = new Progress<AudioProgress>(value =>
            {
                if (!reportBufferProgress || !OwnsOpenedTrack()) return;
                SetStatus($"Загрузка аудио · {value.Bytes / 1048576.0:F1}" + (value.Total.HasValue ? $" из {value.Total / 1048576.0:F1}" : "") + " МБ");
            });
            try { await _player.OpenPausedAsync(source, cts.Token, bufferProgress); }
            finally { reportBufferProgress = false; }
        }
        void ValidateOpenedDuration()
        {
            if (!LooksLikePreview(track, _player.Duration)) return;
            _player.Stop();
            throw PreviewDurationError();
        }
        void CommitOpenedTrack(bool playingOffline, string quality, LocalAudioSource? openedLocal)
        {
            var preferred = track.PreferredLocalVariant;
            if (track.IsOfflineLibraryRow && openedLocal?.Variant is { Sha256: not null } variant
                && StringComparer.OrdinalIgnoreCase.Equals(_player.SourceLocalPath, openedLocal.Path)
                && StringComparer.OrdinalIgnoreCase.Equals(_player.SourceVerifiedLocalDigest, variant.Sha256))
            {
                _offline.BeforeOpenedCheckpointProofForChecks?.Invoke();
                // Old snapshots can acquire exact content proof after opening
                // the candidate admitted against their original index snapshot.
                var legacyExact = openedLocal.AdmittedExactLegacySelection && preferred.Sha256 is null && preferred.Codec == variant.Codec
                    && preferred.Bitrate == variant.Bitrate
                    && (preferred.ConfirmedBitrate is null || preferred.ConfirmedBitrate == variant.ConfirmedBitrate);
                if (preferred.RequestId is null || preferred.RequestId == variant.RequestId && (preferred.Codec is null || legacyExact))
                    track.OpenedLocalVariant = variant;
                if (track.SetMissingArtwork(openedLocal.AdmittedArtwork))
                {
                    SetPlayerArtwork(track);
                    SetLyricsArtwork(track);
                }
            }
            _diagnostics.Timing("play_requested_to_media_open_ms", clock.Elapsed.TotalMilliseconds);
            var playing = _player.IsPlaying;
            _diagnostics.Stage = !playing ? "paused" : playingOffline ? "playing_offline" : "playing";
            track.IsPlaying = true;
            HighlightTrack(track);
            QualityText.Text = (playingOffline ? "Офлайн · " : "") + quality;
            SetStatus(playingOffline ? "" : WaveSourceLabel(track));
            BeginWaveListening(track);
            SetWavePaused(!playing);
            PlaybackOptionsTrackChanged();
            if (playing) _progressTimer.Start(); else _progressTimer.Stop();
            RememberPlaybackDuration();
            // Only a playable new queue supersedes a checkpoint we could not read.
            _playbackStateWriteBlocked = false;
            _playbackStateRestoreUnavailable = false;
            SavePlaybackState();
        }
    }

    private async void PlayPause_Click(object sender, RoutedEventArgs e) => await TogglePlaybackAsync();
    private async Task TogglePlaybackAsync()
    {
        if (_closed) return;
        if (!_player.IsPlaying && (WaitingForWaveAdvance || ColdQueueContinuationPending
            || _waveStartCts is { IsCancellationRequested: false }))
        {
            if (QueuePlaybackBeforeFirstPlay) RememberPausedDesktopOccurrence();
            CancelPendingWavePlayback();
            _diagnostics.Stage = "paused";
            SetStatus("");
            UpdateControls();
            return;
        }
        if (_automaticWaveCts is not null)
        {
            CancelPendingWavePlayback(); StopCompletedQueue(""); return;
        }
        // A canceled opener may still be draining a shared account refresh.
        // A new Play for the exact paused cue can own the next open immediately.
        var restartCanceledOpen = _loadingStream && !_player.IsPlaying && CanRestartCanceledColdOpen;
        if (_loadingStream && !_player.IsPlaying && !restartCanceledOpen) return;
        // Pause stays available during routing. A blocked resume is not queued:
        // completing a device switch must never revive an obsolete Play intent.
        if (!_player.IsPlaying && (IsAudioOutputUnavailable || AudioOutputChangePending))
        {
            SetStatus(IsAudioOutputUnavailable
                ? "Аудиоустройство недоступно. Выберите устройство в настройках воспроизведения."
                : "Дождитесь завершения переключения аудиоустройства.", error: IsAudioOutputUnavailable);
            return;
        }
        if (!_player.HasSource || restartCanceledOpen)
        {
            if (!_demo && _queue.Count > 0) await PlayExistingQueueAsync();
            else if (_current is not null) await PlayTrackAsync(_current);
            else await PlaySelectedAsync();
            return;
        }
        var changed = _player.Toggle();
        if (_player.IsPlaying && _waveListening is null && _current is { } track
            && (_player.Duration <= TimeSpan.Zero || DisplayPosition(_player.Position) < _player.Duration))
            BeginWaveListening(track);
        SavePlaybackState();
        SetWavePaused(!_player.IsPlaying);
        _diagnostics.Stage = _player.IsPlaying ? "playing" : "paused";
        if (_player.IsPlaying) _progressTimer.Start(); else _progressTimer.Stop();
        UpdateControls();
        if (!changed) SetStatus("Windows не удалось изменить состояние воспроизведения. Попробуйте ещё раз.", true);
    }

    private async void Previous_Click(object sender, RoutedEventArgs e) => await PlayPreviousAsync();
    private async void Next_Click(object sender, RoutedEventArgs e) => await AdvanceAsync(1, false);
    private Task AdvanceAsync(int direction, bool automatic)
        => automatic ? AdvanceCoreAsync(direction, true) : AdvanceManualAsync(direction);

    private async Task AdvanceManualAsync(int direction)
    {
        if (_closed) return;
        var revision = _manualQueueAdmissionRevision;
        var queue = _playbackQueue.ReplacementVersion;
        var preceding = _manualQueueAdmissionTail;
        var admitted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _manualQueueAdmissionTail = admitted.Task;
        try
        {
            // Queue order depends on the previous command's committed occurrence.
            // Opening the decoder and optional service probes do not hold this turn.
            await preceding.WaitAsync(_lifetime.Token);
            if (_closed || revision != _manualQueueAdmissionRevision || queue != _playbackQueue.ReplacementVersion) return;
            await AdvanceWithAdmissionAsync(direction, false, earlyFade: null, admitted: admitted);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        finally { admitted.TrySetResult(); }
        if (_manualQueueAdmissionTail.IsCompleted) await ResumeDeferredQueueTrackEndAsync();
    }

    private Task AdvanceCoreAsync(int direction, bool automatic, PreparedPlayback? earlyFade = null)
        => AdvanceWithAdmissionAsync(direction, automatic, earlyFade, admitted: null);

    private async Task AdvanceWithAdmissionAsync(int direction, bool automatic, PreparedPlayback? earlyFade,
        TaskCompletionSource? admitted, HashSet<long>? excluded = null)
    {
        if (_closed || _queue.Count == 0 || _advancingPlayback && automatic) return;
        var knownNext = direction > 0 ? PeekPlayableNext(automatic, excluded) : null;
        var target = direction < 0 ? PeekPlayablePrevious() : knownNext;
        if (!automatic && direction > 0 && _automaticWaveCts is not null)
        {
            // A rejected local candidate must not disown an otherwise live station request.
            if (knownNext is not null && _queueAdmissionCts is null && CanTakeTarget(knownNext))
                await CommitQueueAdvanceAsync(knownNext, ReadyNextPlayback(false), direction, false, _advanceOperation,
                    admitted: admitted, preserveAutomaticWave: true);
            return;
        }
        if (_advancingPlayback && _advancingWave
            && (target is null || _waveAdvanceWait?.Operation != _advanceOperation && _waveExtensionTask is not { IsCompleted: false })) return;
        if (direction < 0 && target is null || !CanTakeTarget(target)) return;
        if (!automatic && NaturalPlaybackContinuationPending) RememberDeferredQueueTrackEnd();
        // A known target can inherit an admission revoked below. While waiting
        // for More the current decoder retains ownership and may complete Play.
        var continueBeforeFirstPlay = !automatic && target is not null && QueuePlaybackBeforeFirstPlay;
        var resumeTail = !automatic && target is null && direction > 0 && IsContinuableWaveTrack(_current)
            && OwnsQueuePlayback(_queueRemovalContinuation, _queueAdmissionCts) ? _current : null;
        var operation = ++_advanceOperation;
        _advancingPlayback = true;
        _naturalAdvanceOperation = automatic && earlyFade is null ? operation : null;
        _waveAdvanceWait?.Cancellation.Cancel();
        _queueAdmissionCts?.Cancel();
        Task? resumedOpening = null;
        try
        {
        if (resumeTail is not null && operation == _advanceOperation && ReferenceEquals(resumeTail, _playbackQueue.Current))
        {
            // More can be empty, so keep the inherited current startup independent
            // of this Next. A later playable candidate can still replace it.
            var pause = _wavePauseRevision;
            resumedOpening = PlayTrackWithSourceAsync(resumeTail, null, false, operation, continueAfterEnd: true);
            if (pause != _wavePauseRevision) return;
        }
        if (automatic && !_player.IsPlaying) ReportWaveEnd(true);
        if (earlyFade is not null && !ReferenceEquals(ReadyNextPlayback(automatic), earlyFade))
        {
            CancelNextPreparation();
            return;
        }
        if (automatic) excluded ??= [];
        var next = target;
        if (automatic && next is not null)
        {
            var result = await ContinueExistingQueueAsync(operation, excluded!, earlyFade);
            if (result != QueueAdvanceResult.Exhausted || earlyFade is not null) return;
            next = null;
        }
        if (next is null && direction > 0 && IsContinuableWaveTrack(_current))
        {
            var account = _accountGeneration;
            var playbackGeneration = _playbackGeneration;
            var waveRequest = _waveRequestGeneration;
            var occurrence = _playbackQueue.CurrentOccurrenceToken;
            var pause = _wavePauseRevision;
            var supersededRefill = _waveExtensionTask is { IsCompleted: false }
                && _waveExtensionPlaybackGeneration != playbackGeneration;
            using var intent = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, _accountCts.Token);
            var wait = _waveAdvanceWait = new WaveAdvanceWait(operation, automatic, CaptureQueuePlayback(intent));
            if (automatic) _progressTimer.Stop();
            // Waiting for recommendations is not a queue mutation. Another
            // manual action may supersede it as soon as it has a playable target.
            admitted?.TrySetResult();
            try
            {
                UpdateControls();
                intent.Token.ThrowIfCancellationRequested();
                await ExtendWaveQueueAsync().WaitAsync(intent.Token);
                if (!CanContinue()) return;
                if (supersededRefill && PeekPlayableNext(automatic, excluded) is null)
                    await ExtendWaveQueueAsync().WaitAsync(intent.Token);
            }
            catch (OperationCanceledException) when (intent.IsCancellationRequested) { return; }
            finally
            {
                if (ReferenceEquals(_waveAdvanceWait, wait))
                {
                    _waveAdvanceWait = null;
                    if (!_closed && operation == _advanceOperation) UpdateControls();
                }
            }
            if (!CanContinue()) return;
            knownNext = PeekPlayableNext(automatic, excluded);
            if (!CanTakeTarget(knownNext, wait.NaturalEndDuringWait)) return;
            next = knownNext;
            if (!automatic && next is null && wait.NaturalEndDuringWait && !_player.IsPlaying)
            {
                StopCompletedQueue();
                return;
            }

            bool CanContinue() => !_closed && account == _accountGeneration && playbackGeneration == _playbackGeneration
                && waveRequest == _waveRequestGeneration && occurrence == _playbackQueue.CurrentOccurrenceToken
                && !intent.IsCancellationRequested && pause == _wavePauseRevision
                && !IsAudioOutputUnavailable && operation == _advanceOperation
                && (!automatic || !_player.IsPlaying && CanAutoAdvancePlaybackOptions());
        }
        if (automatic && next is not null)
        {
            if (await ContinueExistingQueueAsync(operation, excluded!) != QueueAdvanceResult.Exhausted) return;
            next = null;
        }
        if (next is null)
        {
            if (automatic && direction > 0 && await TryStartAutomaticWaveAsync(excluded!)) return;
            if (automatic)
            {
                StopCompletedQueue();
            }
            return;
        }
        await CommitQueueAdvanceAsync(next, direction > 0 ? ReadyNextPlayback(false) : null, direction, false, operation,
            admitted: admitted, continueBeforeFirstPlay: continueBeforeFirstPlay);
        }
        finally
        {
            if (operation == _advanceOperation) { _advancingPlayback = false; RefreshNextPreparation(); }
            else if (automatic)
                // A manual open can cancel this one while still verifying its queue target.
                await _manualQueueAdmissionTail;
            if (resumedOpening is not null) await resumedOpening;
        }

        bool CanTakeTarget(TrackRow? track, bool endedWhileWaiting = false)
        {
            if (!IsPlaybackSelectionBlockedByRemoval(track)) return true;
            if ((automatic || endedWhileWaiting) && !_player.IsPlaying) StopCompletedQueue(OfflineRemovalPlaybackStatus);
            else SetStatus(OfflineRemovalPlaybackStatus);
            return false;
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
            : HasTrackListPlaybackView && (TracksList.SelectedItem is TrackRow selected ? selected.CanPlay
                : _view.Cast<TrackRow>().Any(row => _section == "queue" ? row.CanPlayInQueue : row.CanPlay));
        RefreshTrackDownloadButton();
        PlayPauseButton.IsEnabled = PlaybackTransportActive || CanRestartCanceledColdOpen || !_loadingStream
            && (_player.HasSource || _queue.Any(row => row.CanPlayInQueue)
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
        if (_section == "offline")
        {
            var target = current is not null ? FindPlayingOfflineRow(current) : null;
            foreach (var row in _tracks) row.IsPlaying = ReferenceEquals(row, target);
        }
        else if (IsPlaybackContextView())
        {
            var target = current is not null && ReferenceEquals(current, _playbackQueue.Current) ? PlaybackContextCurrentOccurrence()?.Row : null;
            foreach (var row in _tracks) row.IsPlaying = ReferenceEquals(row, target);
        }
        else
        {
            foreach (var row in _tracks) row.IsPlaying = current is not null && row.RequestId == current.RequestId;
        }
        UpdateQueuePlayingIndex();
    }
    private void OnNativePlaybackActivityChanged()
    {
        if (_closed) return;
        UpdateWaveListeningActivity();
        RefreshPlaybackDisplay();
        RefreshExpandedPlayer();
        UpdateShellState();
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
        if (e.NewValue != e.OldValue) RememberPreferenceEdit(PreferenceField.Volume);
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
        var newlyShown = ShowStatusNotification(error);
        if ((changed || newlyShown) && !string.IsNullOrWhiteSpace(text) && AutomationPeer.ListenerExists(AutomationEvents.LiveRegionChanged))
            UIElementAutomationPeer.CreatePeerForElement(StatusText)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
        WaveStatus.Text = text;
        var inlineStatusBrush = (Brush)FindResource(error ? "ErrorBrush" : "MutedBrush");
        WaveStatus.Foreground = inlineStatusBrush;
    }
    private void ShowError(Exception error, string prefix = "")
    {
        if (_closed) return;
        _diagnostics.LastErrorType = error.GetType().Name;
        var message = error switch
        {
            MusicException music => music.Message,
            LocalAudioSourceException local => local.Message,
            Diagnostics.ReplacementUnavailableException report => report.Message,
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
        var dialog = new Microsoft.Win32.SaveFileDialog { Filter = "Диагностика JSON|*.json", FileName = $"Yamuse-metrics-{DateTime.Now:yyyyMMdd-HHmmss}.json", AddExtension = true, OverwritePrompt = false };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            if (_diagnostics.Save(dialog.FileName, () => MessageBox.Show(this, "Файл отчёта уже существует. Заменить его?",
                AppBranding.Name, MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes))
                SetStatus("Замер сохранён. Файл содержит ресурсы и время загрузки без данных аккаунта.");
        }
        catch (GuardedStoreRecovery.RecoveryException ex) { SetStatus(ex.Message, true); }
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
        if (HasTransportModifier(modifiers, windowsKey)) return;
        if (key == Key.Escape && IsExpandedPlayerOpen) { e.Handled = true; RequestExpandedPlayerClose(); return; }
        if (key == Key.Escape && _settingsOpen) { e.Handled = true; SettingsClose_Click(sender, e); return; }
        if (key == Key.F11)
        {
            e.Handled = true;
            if (!e.IsRepeat) ExpandPlayer_Click(sender, e);
            return;
        }
        if (IsLocalMediaKey(key))
        {
            if (CanHandleLocalMediaCommands) { e.Handled = true; await HandleLocalMediaKeyAsync(key); }
            return;
        }
        if (_expandedCollapse is { } collapse && (ReferenceEquals(sender, _expandedFullscreenHost?.Window)
            || source is Visual visual && visual.IsDescendantOf(collapse.Stage)))
        { e.Handled = true; return; }
        if (_settingsOpen && key is not (Key.P or Key.M or Key.L or Key.OemOpenBrackets or Key.OemCloseBrackets
            or Key.Add or Key.OemPlus or Key.Subtract or Key.OemMinus)) return;
        if (key == Key.Escape && _lyricsOpen) { e.Handled = true; SetLyricsVisible(false); PlayerLyricsButton.Focus(); return; }
        if (IsPlaybackTextInput(source)) return;
        if ((key is Key.Space or Key.Enter) && (source is ButtonBase || FromButton(source))) return;
        if (key is Key.Add or Key.OemPlus) { e.Handled = true; VolumeSlider.Value = Math.Clamp(VolumeSlider.Value + 10, 0, 100); return; }
        if (key is Key.Subtract or Key.OemMinus) { e.Handled = true; VolumeSlider.Value = Math.Clamp(VolumeSlider.Value - 10, 0, 100); return; }
        if (source is Slider && key is not (Key.P or Key.M or Key.L or Key.OemOpenBrackets or Key.OemCloseBrackets)) return;
        if (e.IsRepeat && (key is Key.Space or Key.P or Key.M or Key.L)) { e.Handled = true; return; }
        if (key is Key.Space or Key.P) { e.Handled = true; await TogglePlaybackAsync(); }
        else if (key == Key.OemCloseBrackets) { e.Handled = true; await AdvanceAsync(1, false); }
        else if (key == Key.OemOpenBrackets) { e.Handled = true; await AdvanceAsync(-1, false); }
        else if (key == Key.M) { e.Handled = true; VolumeSlider.Value = VolumeSlider.Value > 0 ? 0 : Math.Clamp(_volumeBeforeMute, 0, 100); }
        else if (key == Key.L) { e.Handled = true; await ToggleCurrentLikeAsync(); }
        else if (key == Key.Enter && TracksList.IsKeyboardFocusWithin)
        {
            e.Handled = true;
            if (e.IsRepeat) return;
            var rowIndex = TrackRowIndex(source);
            await ActivateTrackRowAsync(rowIndex >= 0 ? rowIndex : TracksList.SelectedIndex);
        }
        else if (key == Key.Enter && CatalogList.IsKeyboardFocusWithin)
        {
            var item = ReferenceEquals(source, CatalogList) ? CatalogList.SelectedItem as CatalogItem
                : source is null ? null : (ItemsControl.ContainerFromElement(CatalogList, source) as ListBoxItem)?.Content as CatalogItem;
            if (item is null) return;
            e.Handled = true;
            if (!e.IsRepeat) await OpenCatalogAsync(item);
        }
    }
    private static bool HasTransportModifier(ModifierKeys modifiers, bool windowsKey)
        => windowsKey || (modifiers & (ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Windows)) != 0;

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
            TryFlushPendingSessionChange();
            SavePlaybackCheckpointOnExit(restoreUntouchedQueue: false);
            SavePreferences();
            TryFlushUnsavedListSorts();
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
