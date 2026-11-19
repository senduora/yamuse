using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;

namespace YMusic.Native;

public partial class MainWindow
{
    private const string SessionRemovalFailure = "Не удалось удалить прежний сохранённый вход. Проверьте доступ к файлу session.dpapi и повторите попытку.";
    private const string SessionRemovalSuperseded = "Сохранённый вход изменился. Нажмите «Войти в Яндекс» ещё раз.";
    private const string SavedSessionDamageTitle = "Сохранённый вход повреждён";
    private const string SavedSessionDamageRecovery = "Отзовите прежний доступ устройства Yamuse в Яндекс ID, удалите повреждённый файл session.dpapi и нажмите «Войти в Яндекс» ещё раз.";
    private Task _sessionRefreshWrite = Task.CompletedTask;
    private Task _deviceTokenWrite = Task.CompletedTask;
    private Task? _legacyRevokeWrite;
    private bool _savedSessionReadNeedsRetry;
    private bool _savedSessionDamaged;
    private Session? _unactivatedSession;
    private bool _sessionSaveSuperseded;
    private (Session Session, int AccountGeneration, Session? StoredSession)? _activeRefreshRejection;
    // The exact record this login read during restore or successfully published.
    private Session? _ownedStoredSession;
    private PendingSessionChange? _pendingSessionChange;
    private sealed record PendingSessionChange(int AccountGeneration, Session? Session, Session? ExpectedStoredSession,
        bool WaitForProfile = false);
    private enum SessionChangeResult { Applied, Failed, Superseded }
    private bool SessionIsExiting => _closed || _startupUpdateRestartRequested;
    private bool SessionWriteAdmissionClosed =>
        SessionIsExiting || _closeCommitInProgress || _appUpdatePreparingRestart || _fullUninstallCommitting;

    private Task DrainSessionRefreshWritesAsync() => Task.WhenAll(_sessionRefreshWrite, _deviceTokenWrite, _legacyRevokeWrite ?? Task.CompletedTask);

    private async Task<MusicApi.DeviceTokenPoll> AdmitDeviceTokenPollAsync(
        Func<CancellationToken, Task<MusicApi.DeviceTokenPoll>> send, CancellationToken pollToken,
        CancellationTokenSource auth, CancellationToken accountToken, Session? expectedStoredSession)
    {
        pollToken.ThrowIfCancellationRequested();
        accountToken.ThrowIfCancellationRequested();
        if (SessionIsExiting || !ReferenceEquals(_authCts, auth) || _session is not null)
            throw new OperationCanceledException(pollToken);
        // A canceled close or failed restart leaves this device code and its original expiry intact.
        if (SessionWriteAdmissionClosed) return default;

        // Closing waits for this exchange, while the polling delay remains outside the drain.
        using var exchange = CancellationTokenSource.CreateLinkedTokenSource(auth.Token, accountToken);
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _deviceTokenWrite = completed.Task;
        using var registration = exchange.Token.Register(() => completed.TrySetResult());
        try
        {
            var reply = await send(exchange.Token);
            exchange.Token.ThrowIfCancellationRequested();
            if (!ReferenceEquals(_authCts, auth) || _session is not null || _closed || _startupUpdateRestartRequested)
                throw new OperationCanceledException(exchange.Token);
            if (reply.Session is { } accepted)
            {
                accepted = accepted with { DeviceProfilePending = true };
                reply = reply with { Session = accepted };
                _sessionSaveSuperseded = false;
                _ownedStoredSession = null;
                _unactivatedSession = accepted;
                TrySaveSession(accepted, expectedStoredSession);
            }
            return reply;
        }
        finally { completed.TrySetResult(); }
    }

    private PendingSessionChange? PendingSaveFor(Session session) =>
        _pendingSessionChange is { } pending && pending.AccountGeneration == _accountGeneration
            && ReferenceEquals(pending.Session, session) ? pending : null;

    private bool TrySaveSession(Session session, Session? expected)
    {
        if (_sessionSaveSuperseded) return true;
        _pendingSessionChange = new(_accountGeneration, session, expected);
        if (TryFlushPendingSessionChange()) return true;
        if (_session is null) return false;
        LoginDescription.Text = "Вход выполнен, но Windows пока не сохранила сессию. Yamuse повторит запись перед выходом или обновлением.";
        LoginPanel.Visibility = Visibility.Visible;
        LoginTitle.Text = "Сессия пока не сохранена";
        CodePanel.Visibility = Visibility.Collapsed;
        return false;
    }

    private SessionChangeResult RetireStoredSession(Session? expected)
    {
        _pendingSessionChange = expected is null ? null : new(_accountGeneration, Session: null, ExpectedStoredSession: expected);
        return FlushPendingSessionChange();
    }

    private bool TryFlushPendingSessionChange()
    {
        var retiring = _pendingSessionChange is { Session: null };
        var result = FlushPendingSessionChange();
        if (retiring && _authCts is null) ShowLoginRetry(offerNewCode: true);
        return result != SessionChangeResult.Failed;
    }

    private SessionChangeResult FlushPendingSessionChange()
    {
        if (_pendingSessionChange is not { WaitForProfile: false } pending) return SessionChangeResult.Applied;
        if (_closed) return SessionChangeResult.Failed;
        var ownsChange = pending.Session is { } saving
            ? ReferenceEquals(_session, saving) || ReferenceEquals(_unactivatedSession, saving)
            : _unactivatedSession is null && (_session is null
                || (_activeRefreshRejection is { } rejection && rejection.AccountGeneration == _accountGeneration
                    && ReferenceEquals(_session, rejection.Session) && ReferenceEquals(pending.ExpectedStoredSession, rejection.StoredSession)));
        if (pending.AccountGeneration != _accountGeneration || !ownsChange)
        {
            _pendingSessionChange = null;
            return SessionChangeResult.Superseded;
        }
        try
        {
            var applied = pending.Session is { } session
                ? _store.SaveIfMatches(session, pending.ExpectedStoredSession)
                : _store.ClearIfMatches(pending.ExpectedStoredSession!);
            _pendingSessionChange = null;
            if (!applied)
            {
                _savedSessionReadNeedsRetry = true;
                if (pending.Session is not null)
                {
                    _sessionSaveSuperseded = true;
                    SetStatus("Сохранённый вход изменился. Yamuse проверит его при следующем запуске.", true);
                }
                return SessionChangeResult.Superseded;
            }
            if (pending.Session is not null) _ownedStoredSession = pending.Session;
            return SessionChangeResult.Applied;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or CryptographicException)
        {
            _diagnostics.LastErrorType = error.GetType().Name;
            return SessionChangeResult.Failed;
        }
    }

    private void CompletePendingSessionRemoval()
    {
        if (_pendingSessionChange is { Session: null }) RequireSessionRemoval(FlushPendingSessionChange());
    }

    private static void RequireSessionRemoval(SessionChangeResult result)
    {
        if (result == SessionChangeResult.Failed) throw new MusicException(SessionRemovalFailure, true);
        if (result == SessionChangeResult.Superseded) throw new MusicException(SessionRemovalSuperseded);
    }

    private void ReportLoginFailure(Exception error, CancellationToken accountToken, CancellationTokenSource auth)
    {
        if (!ReferenceEquals(_authCts, auth) || accountToken.IsCancellationRequested || auth.IsCancellationRequested) return;
        ShowError(error);
        if (error is not MusicException { Authentication: true } rejection || _unactivatedSession is null
            || _session is not null || SessionIsExiting) return;
        // Finish the already admitted profile check even while close is asking about a different write.
        if (RetirePendingLogin(_ownedStoredSession) == SessionChangeResult.Failed)
            SetStatus(rejection.Message + " " + SessionRemovalFailure, true);
    }

    private SessionChangeResult RetirePendingLogin(Session? stored)
    {
        _unactivatedSession = null;
        _sessionSaveSuperseded = false;
        _ownedStoredSession = null;
        var result = RetireStoredSession(stored);
        ShowLoginRetry(offerNewCode: true);
        return result;
    }

    private void ShowLoginRetry(bool offerNewCode = false)
    {
        if (_session is not null || _closed) return;
        LogoutButton.Visibility = _unactivatedSession is null ? Visibility.Collapsed : Visibility.Visible;
        if (_unactivatedSession is not null)
        {
            LoginTitle.Text = "Завершите вход";
            LoginDescription.Text = "Доступ уже подтверждён. Нажмите «Войти в Яндекс», чтобы снова загрузить профиль. Для нового входа сначала выйдите из аккаунта.";
        }
        else if (_pendingSessionChange is { Session: null })
        {
            LoginTitle.Text = "Сохранённый вход пока не удалён";
            LoginDescription.Text = "Нажмите «Войти в Яндекс», чтобы повторить удаление.";
        }
        else if (_savedSessionDamaged)
        {
            LoginTitle.Text = SavedSessionDamageTitle;
            LoginDescription.Text = SavedSessionDamageRecovery;
        }
        else if (_savedSessionReadNeedsRetry)
        {
            LoginTitle.Text = "Проверьте сохранённый вход";
            LoginDescription.Text = "Нажмите «Войти в Яндекс», чтобы снова проверить сохранённый вход.";
        }
        else if (offerNewCode)
        {
            LoginTitle.Text = "Начните со входа в аккаунт";
            LoginDescription.Text = "Нажмите «Войти в Яндекс», чтобы получить новый код.";
        }
    }

    private void ResetAccountCancellation()
    {
        using var previous = _accountCts;
        _accountCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        previous.Cancel();
    }

    private static bool CanRefreshExpiredProfileAccess(Session session) =>
        session.ExpiresAtUtc <= DateTimeOffset.UtcNow && MusicApi.IsOAuthValue(session.RefreshToken);

    private static MusicException UnverifiedRefreshedProfile() =>
        new("Яндекс не подтвердил профиль. Повторите восстановление входа.");

    private async Task ActivatePendingProfileAsync(Session session, CancellationToken accountToken, CancellationTokenSource auth)
    {
        var refreshFirst = session.DeviceProfilePending
            ? CanRefreshExpiredProfileAccess(session)
            : session.ExpiresAtUtc <= DateTimeOffset.UtcNow;
        if (refreshFirst) session = await RefreshAndSaveSessionAsync(session, accountToken, auth);
        auth.Token.ThrowIfCancellationRequested();
        try { await ActivateSessionAsync(session, auth.Token, refreshedForRestore: refreshFirst); }
        catch (MusicException error) when (error.Authentication && !refreshFirst && _session is null
            && ReferenceEquals(_authCts, auth) && ReferenceEquals(_unactivatedSession, session)
            && !auth.IsCancellationRequested && !accountToken.IsCancellationRequested)
        {
            if (!CanRefreshExpiredProfileAccess(session))
            {
                if (session.ProfileRefreshValidated) throw UnverifiedRefreshedProfile();
                throw;
            }
            // The first profile request may have started before the access token expired.
            if (SessionWriteAdmissionClosed)
            {
                if (!SessionIsExiting) SetStatus("Восстановление входа приостановлено.");
                return;
            }
            var renewed = await RefreshAndSaveSessionAsync(session, accountToken, auth);
            auth.Token.ThrowIfCancellationRequested();
            await ActivateSessionAsync(renewed, auth.Token, refreshedForRestore: true);
        }
    }

    private async Task<Session> RefreshAndSaveSessionAsync(
        Session session, CancellationToken accountToken, CancellationTokenSource? restoreOwner = null)
    {
        if (SessionWriteAdmissionClosed) throw new OperationCanceledException(accountToken);
        accountToken.ThrowIfCancellationRequested();
        var expected = PendingSaveFor(session) is { } save ? save.ExpectedStoredSession : _ownedStoredSession;
        var generation = _accountGeneration;
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _sessionRefreshWrite = completed.Task;
        // Logout invalidates this write even if the HTTP response takes longer to finish.
        using var registration = accountToken.Register(() => completed.TrySetResult());
        try
        {
            Session renewed;
            try { renewed = await _api.RefreshAsync(session, accountToken); }
            catch (RefreshGrantRejectedException rejection) when (generation == _accountGeneration
                && !accountToken.IsCancellationRequested && !SessionIsExiting
                && (restoreOwner is null ? ReferenceEquals(_session, session)
                    : ReferenceEquals(_authCts, restoreOwner) && _session is null && ReferenceEquals(_unactivatedSession, session)))
            {
                var stored = _ownedStoredSession;
                if (restoreOwner is not null)
                {
                    // Canceling the screen still leaves the admitted OAuth result authoritative.
                    // An unpublished rotation leaves the original restore record in our care.
                    var removal = RetirePendingLogin(stored);
                    restoreOwner.Token.ThrowIfCancellationRequested();
                    if (removal == SessionChangeResult.Failed)
                        throw new MusicException(rejection.Message + " " + SessionRemovalFailure, true);
                }
                else
                {
                    // The stored active grant can still have older access or profile metadata.
                    if (stored?.RefreshToken != session.RefreshToken) stored = null;
                    _activeRefreshRejection = (session, generation, stored);
                    if (stored is not null)
                    {
                        _ownedStoredSession = null;
                        RetireStoredSession(stored);
                    }
                    else if (PendingSaveFor(session) is not null) _pendingSessionChange = null;
                }
                throw;
            }
            accountToken.ThrowIfCancellationRequested();
            if (restoreOwner is null)
            {
                if (!ReferenceEquals(_session, session)) throw new OperationCanceledException(accountToken);
                _session = renewed;
            }
            else if (!ReferenceEquals(_authCts, restoreOwner) || !ReferenceEquals(_unactivatedSession, session) || _session is not null)
                throw new OperationCanceledException(accountToken);
            else
            {
                renewed = renewed with { ProfileRefreshValidated = true };
                _unactivatedSession = renewed;
            }
            TrySaveSession(renewed, expected);
            return renewed;
        }
        finally { completed.TrySetResult(); }
    }

    private async Task RevokeLegacyLoginAsync(Session saved, CancellationToken cancellationToken)
    {
        var accountToken = _accountCts.Token;
        var auth = _authCts;
        cancellationToken.ThrowIfCancellationRequested();
        if (SessionWriteAdmissionClosed) throw new OperationCanceledException(cancellationToken);
        SetStatus("Отзываем прежний доступ перед входом с музыкальными разрешениями…");
        using var exchange = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, accountToken);
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _legacyRevokeWrite = completed.Task;
        using var registration = accountToken.Register(() => completed.TrySetResult());
        try
        {
            await _api.RevokeSessionAsync(saved, exchange.Token);
            // Clear only a confirmed revoke for the account which admitted it.
            accountToken.ThrowIfCancellationRequested();
            if (SessionIsExiting || !ReferenceEquals(_authCts, auth) || _session is not null)
                throw new OperationCanceledException(accountToken);
            RequireSessionRemoval(RetireStoredSession(saved));
            cancellationToken.ThrowIfCancellationRequested();
        }
        finally { completed.TrySetResult(); }
    }

    private Session? ReadSavedSession()
    {
        try
        {
            var saved = _store.Load();
            _savedSessionReadNeedsRetry = false;
            _savedSessionDamaged = false;
            return saved;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            _savedSessionReadNeedsRetry = true;
            throw new MusicException("Сохранённый вход временно недоступен. Проверьте доступ к файлу session.dpapi и нажмите «Войти в Яндекс» ещё раз.");
        }
        catch (Exception error) when (error is CryptographicException or JsonException)
        {
            _savedSessionReadNeedsRetry = true;
            _savedSessionDamaged = true;
            throw new MusicException(SavedSessionDamageTitle + ". " + SavedSessionDamageRecovery, true);
        }
    }

    private async Task<Session?> PreparePlayerLoginAsync(CancellationToken cancellationToken)
    {
        CompletePendingSessionRemoval();
        var previous = ReadSavedSession();
        if (previous is null || MusicApi.HasPlayerScopes(previous)) return previous;
        await RevokeLegacyLoginAsync(previous, cancellationToken);
        return null;
    }

    private void ShowPlayerLoginRequired()
    {
        LoginTitle.Text = "Нужно войти заново";
        LoginDescription.Text = "Прежний доступ отозван. Новый вход запросит только каталог, чтение и изменение вашей музыкальной коллекции.";
        SetStatus("Разрешения сокращены до музыкальных. Нажмите «Войти в Яндекс».");
    }
}
