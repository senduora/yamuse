using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;

namespace YMusic.Native;

public partial class MainWindow
{
    private const string SessionRemovalFailure = "Не удалось удалить прежний сохранённый вход. Проверьте доступ к файлу session.dpapi и повторите попытку.";
    private Task _sessionRefreshWrite = Task.CompletedTask;
    private Session? _unactivatedSession;
    private PendingSessionChange? _pendingSessionChange;
    private sealed record PendingSessionChange(int AccountGeneration, Session? Session);
    private bool SessionWriteAdmissionClosed =>
        _closed || _closeCommitInProgress || _appUpdatePreparingRestart || _startupUpdateRestartRequested || _fullUninstallCommitting;

    private Task DrainSessionRefreshWritesAsync() => _sessionRefreshWrite;

    private bool PersistSessionChange(Session? session)
    {
        _pendingSessionChange = new(_accountGeneration, session);
        return TryFlushPendingSessionChange();
    }

    private bool TryFlushPendingSessionChange()
    {
        if (_pendingSessionChange is not { } pending) return true;
        if (_closed) return false;
        var ownsChange = pending.Session is { } saving
            ? ReferenceEquals(_session, saving) || ReferenceEquals(_unactivatedSession, saving)
            : _session is null && _unactivatedSession is null;
        if (pending.AccountGeneration != _accountGeneration || !ownsChange)
        {
            _pendingSessionChange = null;
            return true;
        }
        try
        {
            if (pending.Session is { } session) _store.Save(session);
            else _store.Clear();
            _pendingSessionChange = null;
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or CryptographicException)
        {
            _diagnostics.LastErrorType = error.GetType().Name;
            return false;
        }
    }

    private void CompletePendingSessionRemoval()
    {
        if (_pendingSessionChange is { Session: null } && !TryFlushPendingSessionChange())
            throw new MusicException(SessionRemovalFailure, true);
    }

    private void ReportLoginFailure(Exception error, CancellationToken accountToken, CancellationTokenSource auth)
    {
        if (!ReferenceEquals(_authCts, auth) || accountToken.IsCancellationRequested || auth.IsCancellationRequested) return;
        ShowError(error);
        if (error is not MusicException { Authentication: true } rejection || _unactivatedSession is null || _session is not null
            || SessionWriteAdmissionClosed) return;
        _unactivatedSession = null;
        if (!PersistSessionChange(null)) SetStatus(rejection.Message + " " + SessionRemovalFailure, true);
    }

    private void ShowPendingLoginRetry()
    {
        if (_session is not null || _closed) return;
        LogoutButton.Visibility = _unactivatedSession is null ? Visibility.Collapsed : Visibility.Visible;
        if (_unactivatedSession is null) return;
        LoginTitle.Text = "Завершите вход";
        LoginDescription.Text = "Доступ уже подтверждён. Нажмите «Войти в Яндекс», чтобы снова загрузить профиль. Для нового входа сначала выйдите из аккаунта.";
    }

    private void ResetAccountCancellation()
    {
        using var previous = _accountCts;
        _accountCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        previous.Cancel();
    }

    private async Task<(Session Session, bool Saved)> RefreshAndSaveSessionAsync(
        Session session, CancellationToken accountToken, CancellationTokenSource? restoreOwner = null)
    {
        if (SessionWriteAdmissionClosed) throw new OperationCanceledException(accountToken);
        accountToken.ThrowIfCancellationRequested();
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _sessionRefreshWrite = completed.Task;
        // Logout invalidates this write even if the HTTP response takes longer to finish.
        using var registration = accountToken.Register(() => completed.TrySetResult());
        try
        {
            var renewed = await _api.RefreshAsync(session, accountToken);
            accountToken.ThrowIfCancellationRequested();
            if (restoreOwner is null)
            {
                if (!ReferenceEquals(_session, session)) throw new OperationCanceledException(accountToken);
                _session = renewed;
            }
            else if (!ReferenceEquals(_authCts, restoreOwner) || !ReferenceEquals(_unactivatedSession, session) || _session is not null)
                throw new OperationCanceledException(accountToken);
            else _unactivatedSession = renewed;
            return (renewed, TrySaveSession(renewed));
        }
        finally { completed.TrySetResult(); }
    }

    private async Task RevokeLegacyLoginAsync(Session saved, CancellationToken cancellationToken)
    {
        var accountToken = _accountCts.Token;
        var auth = _authCts;
        SetStatus("Отзываем прежний доступ перед входом с музыкальными разрешениями…");
        await _api.RevokeSessionAsync(saved, cancellationToken);
        // Clear only after a confirmed revoke. A failed/cancelled request retains
        // the DPAPI record for a safe retry, never for a Music API call.
        accountToken.ThrowIfCancellationRequested();
        if (_closed || _startupUpdateRestartRequested || _fullUninstallCommitting || !ReferenceEquals(_authCts, auth) || _session is not null)
            throw new OperationCanceledException(accountToken);
        if (!PersistSessionChange(null)) throw new MusicException(SessionRemovalFailure, true);
        cancellationToken.ThrowIfCancellationRequested();
    }

    private async Task PreparePlayerLoginAsync(CancellationToken cancellationToken)
    {
        CompletePendingSessionRemoval();
        Session? previous;
        try { previous = _store.Load(); }
        // An unreadable record can still represent an active broad grant. Do
        // not overwrite the only local copy without a confirmed device revoke.
        catch (Exception error) when (error is CryptographicException or JsonException or IOException or UnauthorizedAccessException)
        {
            throw new MusicException("Сохранённый вход повреждён. Отзовите прежний доступ устройства Yamuse в Яндекс ID и удалите повреждённую сохранённую сессию перед новым входом.", true);
        }
        if (previous is not null && !MusicApi.HasPlayerScopes(previous))
            await RevokeLegacyLoginAsync(previous, cancellationToken);
    }

    private void ShowPlayerLoginRequired()
    {
        LoginTitle.Text = "Нужно войти заново";
        LoginDescription.Text = "Прежний доступ отозван. Новый вход запросит только каталог, чтение и изменение вашей музыкальной коллекции.";
        SetStatus("Разрешения сокращены до музыкальных. Нажмите «Войти в Яндекс».");
    }
}
