using OfflineCopyCheck = YMusic.Native.OfflineLibrary.OfflineCopyCheck;
using OfflineCopyChecks = YMusic.Native.OfflineLibrary.OfflineCopyChecks;

namespace YMusic.Native;

public partial class MainWindow
{
    private (string TrackId, CancellationTokenSource Request)? _pendingOfflinePlayback;
    private string? _offlineRemovalTrackId;
    private CancellationTokenSource? _queueAdmissionCts;
    private Task _manualQueueAdmissionTail = Task.CompletedTask;
    private long _manualQueueAdmissionRevision;
    private long? _naturalAdvanceOperation;
    private CancellationTokenSource? _manualQueueAdvanceCts;
    private DeferredQueueTrackEnd? _deferredQueueTrackEnd;
    private PendingQueuePlayback? _automaticTrackStart;
    private PendingQueuePlayback? _queueRemovalContinuation;
    private PendingQueuePlayback? _queueAdvanceContinuation;
    private bool NaturalPlaybackContinuationPending => _advancingPlayback && _naturalAdvanceOperation == _advanceOperation
        && (_queueAdmissionCts is { IsCancellationRequested: false }
            || _waveAdvanceWait is { Automatic: true } wait && OwnsWaveAdvance(wait));
    private bool ColdQueueContinuationPending => NaturalPlaybackContinuationPending || OwnsDeferredQueueTrackEnd() || QueuePlaybackBeforeFirstPlay;
    private bool QueuePlaybackBeforeFirstPlay => AutomaticTrackStartPending || OwnsQueuePlayback(_queueRemovalContinuation, _queueAdmissionCts)
        || OwnsQueuePlayback(_queueAdvanceContinuation, _queueAdmissionCts);
    private bool AutomaticTrackStartPending => OwnsQueuePlayback(_automaticTrackStart, _playCts);
    internal Func<CancellationToken, Task>? QueuePlaybackBeforeVerificationForChecks { get; set; }
    internal Func<CancellationToken, Task>? ExactOfflineRetryBeforeCommitForChecks { get; set; }

    private sealed record PendingQueuePlayback(CancellationTokenSource Request, int Playback, int Account, long Queue, long? Occurrence);

    private PendingQueuePlayback CaptureQueuePlayback(CancellationTokenSource request)
        => new(request, _playbackGeneration, _accountGeneration, _playbackQueue.ReplacementVersion, _playbackQueue.CurrentOccurrenceToken);

    private bool OwnsQueuePlayback(PendingQueuePlayback? pending, CancellationTokenSource? request)
        => !_closed && pending is { Request.IsCancellationRequested: false, Occurrence: not null } owner
            && ReferenceEquals(owner.Request, request) && owner.Playback == _playbackGeneration && owner.Account == _accountGeneration
            && owner.Queue == _playbackQueue.ReplacementVersion && owner.Occurrence == _playbackQueue.CurrentOccurrenceToken;

    private sealed record DeferredQueueTrackEnd(TrackRow Track, long? Occurrence, long Queue, int Playback, int Account)
    {
        internal HashSet<long> Excluded { get; } = [];
    }

    private bool OwnsWaveAdvance(WaveAdvanceWait wait) => wait.Operation == _advanceOperation
        && OwnsQueuePlayback(wait.Owner, wait.Cancellation);

    private bool TryDeferQueueTrackEnd() => _manualQueueAdvanceCts is { IsCancellationRequested: false } request
        && ReferenceEquals(_queueAdmissionCts, request) && RememberDeferredQueueTrackEnd();

    private bool RememberDeferredQueueTrackEnd()
    {
        if (_current is not { } current || !ReferenceEquals(current, _playbackQueue.Current)) return false;
        if (!OwnsDeferredQueueTrackEnd())
            _deferredQueueTrackEnd = new(current, _playbackQueue.CurrentOccurrenceToken,
                _playbackQueue.ReplacementVersion, _playbackGeneration, _accountGeneration);
        return true;
    }

    private bool OwnsDeferredQueueTrackEnd() => !_closed && !_player.IsPlaying && _deferredQueueTrackEnd is { } end
        && end.Playback == _playbackGeneration && end.Account == _accountGeneration
        && end.Queue == _playbackQueue.ReplacementVersion && end.Occurrence == _playbackQueue.CurrentOccurrenceToken
        && ReferenceEquals(end.Track, _current);

    private async Task ResumeDeferredQueueTrackEndAsync()
    {
        if (_advancingPlayback) return;
        var end = OwnsDeferredQueueTrackEnd() ? _deferredQueueTrackEnd : null;
        _deferredQueueTrackEnd = null;
        if (end is not null && !HandlePlaybackOptionsTrackEnded())
            await AdvanceWithAdmissionAsync(1, true, earlyFade: null, admitted: null, excluded: end.Excluded);
    }

    private LocalAudioSource? RefreshLocalPlaybackSource(TrackRow track, bool requireExactOfflineVariant = false)
    {
        var checks = CurrentOfflineDownloadChecks;
        var exact = requireExactOfflineVariant && track.IsOfflineLibraryRow;
        var source = _offline.ResolvePlaybackSource(track, requireExactOfflineVariant);
        // The badge belongs to the row's exact version, even when resuming an
        // existing occurrence can retry an indexed candidate under a held handle.
        var exactSource = track.IsOfflineLibraryRow && !requireExactOfflineVariant
            ? ApplyOfflineCopyCheck(track, _offline.ResolvePlaybackSource(track, requireExactOfflineVariant: true), checks, allowAlternate: false)
            : ApplyOfflineCopyCheck(track, source, checks, allowAlternate: !exact);
        var indexed = exactSource is { Bytes: not null } ? exactSource : null;
        var queueVariant = (track.IsOfflineLibraryRow || !track.Available) && indexed is null
            && (exact ? _offline.ResolvePlaybackSource(track) : source) is { Bytes: not null };
        ApplyOfflineMarks([new(track, indexed?.Path, indexed?.Quality, queueVariant)]);
        return source;
    }

    private LocalAudioSource? ApplyOfflineCopyCheck(TrackRow track, LocalAudioSource? source,
        OfflineCopyChecks? checks, bool allowAlternate)
    {
        if (source is not { Bytes: not null } || checks is null || _offline.AllowsVerifiedCopy(checks, track.Id, source)) return source;
        return allowAlternate ? _offline.ResolveAlternatePlaybackSources(track, source)
            .FirstOrDefault(alternate => _offline.AllowsVerifiedCopy(checks, track.Id, alternate)) : null;
    }

    private bool CanNavigateToTrack(TrackRow track, bool requireExactOfflineVariant = false)
    {
        if (requireExactOfflineVariant && track.IsOfflineLibraryRow)
            return RefreshLocalPlaybackSource(track, requireExactOfflineVariant: true) is not null && track.CanPlay;
        var canResumeOffline = !requireExactOfflineVariant && track.IsOfflineLibraryRow && ReferenceEquals(track, _playbackQueue.Current);
        if (!(requireExactOfflineVariant ? track.CanPlay : track.CanPlayInQueue) && !canResumeOffline) return false;
        if (track.Available && !track.IsOfflineLibraryRow) return true;
        return RefreshLocalPlaybackSource(track, requireExactOfflineVariant) is not null
            && ((requireExactOfflineVariant ? track.CanPlay : track.CanPlayInQueue) || canResumeOffline);
    }

    private LocalAudioSource? FindExactOfflineRetryCandidate(TrackRow track, bool allowUnavailableCatalog)
    {
        var preferred = track.PreferredLocalVariant;
        if (track.IsOfflineLibraryRow ? preferred.RequestId is null || preferred.Codec is null
            : !allowUnavailableCatalog || track.Available || track.LocalPath is not null) return null;
        if (BlocksOfflineOpen(track.Id) || CurrentOfflineDownloadChecks is not { } checks) return null;
        var source = _offline.ResolvePlaybackSource(track, requireExactOfflineVariant: true);
        return source is { Digest: not null } && (checks.Refuses(source) || checks.GetValueOrDefault(track.Id) is { RetryOnNavigation: true }
            && !_offline.AllowsVerifiedCopy(checks, track.Id, source)) ? source : null;
    }

    private async Task RetryExactOfflineRowAsync(TrackRow track, bool allowUnavailableCatalog, Func<bool> ownsTarget,
        Func<NativePlayer.LocalPlaybackAdmission, Task> play)
    {
        if (_demo || _pendingOfflinePlayback is not null || !ownsTarget()
            || FindExactOfflineRetryCandidate(track, allowUnavailableCatalog) is not { } source) return;
        var account = _accountGeneration;
        var uid = _account?.Uid;
        var playback = _playbackGeneration;
        var queue = _playbackQueue.Version;
        var pause = _wavePauseRevision;
        var advance = _advanceOperation;
        using var request = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, _accountCts.Token);
        _pendingOfflinePlayback = (track.Id, request);
        bool OwnsRequest() => !_closed && !request.IsCancellationRequested && account == _accountGeneration && uid == _account?.Uid
            && playback == _playbackGeneration && queue == _playbackQueue.Version && pause == _wavePauseRevision && advance == _advanceOperation
            && _pendingOfflinePlayback is { } pending && ReferenceEquals(pending.Request, request) && ownsTarget();
        try
        {
            using var admission = await _player.VerifyLocalAdvanceAsync(source, request.Token);
            if (ExactOfflineRetryBeforeCommitForChecks is { } before) await before(request.Token);
            if (!OwnsRequest() || BlocksOfflineOpen(track.Id)
                || _offline.ResolvePlaybackSource(track, requireExactOfflineVariant: true) is not { } current
                || !OfflineLibrary.SameOfflineCopy(source, current)) return;
            if (HasMinuteSleepExpired()) { FinishSleepTimer(); return; }
            if (RememberOpenedOfflineCopy(track, source)) RefreshVerifiedOfflineMarks(track, refreshDetails: true);
            if (OwnsRequest()) await play(admission);
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { }
        catch (LocalAudioSourceException error) { if (OwnsRequest()) ShowError(error); }
        finally { if (_pendingOfflinePlayback is { } pending && ReferenceEquals(pending.Request, request)) _pendingOfflinePlayback = null; }
    }

    private TrackRow? PeekPlayableNext(bool naturalEnd, IReadOnlySet<long>? excluded = null)
    {
        // Updating a stale local mark makes the same queue search skip it
        // without consuming an occurrence or adding it to navigation history.
        while (_playbackQueue.PeekNext(naturalEnd, excluded) is { } next)
        {
            if (CanNavigateToTrack(next)) return next;
        }
        return null;
    }

    private TrackRow? PeekPlayablePrevious()
    {
        while (_playbackQueue.PeekPrevious() is { } previous)
        {
            if (CanNavigateToTrack(previous)) return previous;
            // Playback can restart the current occurrence only when it still has a source.
            // Stop probing if a failed source check did not invalidate the queue mark.
            if (ReferenceEquals(previous, _playbackQueue.Current) || previous.CanPlayInQueue) break;
        }
        return null;
    }

    private bool BlocksOfflineOpen(string trackId) => _offlineMaintenance
        && (_offlineRemovalTrackId is null || _offlineRemovalTrackId == trackId);

    private enum QueueAdvanceResult { Abandoned, Committed, TargetChanged, LocalRejected, Exhausted }

    private async Task<QueueAdvanceResult> CommitQueueAdvanceAsync(TrackRow track, PreparedPlayback? prepared, int direction, bool automatic,
        long operation, bool requirePrepared = false, TaskCompletionSource? admitted = null,
        IReadOnlySet<long>? excluded = null, bool preserveAutomaticWave = false, bool continueBeforeFirstPlay = false)
    {
        var queue = _playbackQueue.ReplacementVersion;
        var from = _playbackQueue.CurrentOccurrenceToken;
        var playback = _playbackGeneration;
        var occurrence = TargetOccurrence();
        if (!(direction < 0 ? ReferenceEquals(_playbackQueue.PeekPrevious(), track) : ReferenceEquals(_playbackQueue.PeekNext(automatic, excluded), track)))
            return QueueAdvanceResult.Abandoned;

        var local = RefreshLocalPlaybackSource(track);
        if (local is null && (_session is null || !track.Available || track.IsOfflineLibraryRow || track.LocalPath is not null))
            return automatic ? QueueAdvanceResult.LocalRejected : QueueAdvanceResult.Abandoned;
        // Supersede a cold open (including its optional quality probe) now.
        // An audible source still belongs to the player until verification succeeds.
        var interruptedStartup = !automatic && (continueBeforeFirstPlay || QueuePlaybackBeforeFirstPlay);
        var coldOpen = !automatic && _loadingStream && !_player.IsPlaying ? _playCts : null;
        var wasPlaying = _player.IsPlaying;
        var pause = _wavePauseRevision;
        NativePlayer.LocalPlaybackAdmission? admission = null;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, _accountCts.Token);
        _queueAdmissionCts = cts;
        var ownsManualContinuation = false;
        try
        {
            if (interruptedStartup) _queueAdvanceContinuation = CaptureQueuePlayback(cts);
            coldOpen?.Cancel();
            if (interruptedStartup) UpdateControls();
            if (!OwnsAdvance() || StopForSleep()) return QueueAdvanceResult.Abandoned;
            var routeBlocksPromotion = prepared is not null && _player.OutputRouteChanging;
            var promoted = prepared is not null && !routeBlocksPromotion && (!AudioOutputChangePending || _player.IsPlaying)
                && PromotePrepared(track, prepared, local, occurrence, automatic);
            var streamOnly = false;
            if (!promoted)
            {
                if (requirePrepared)
                {
                    if (!routeBlocksPromotion) CancelNextPreparation();
                    return QueueAdvanceResult.Abandoned;
                }
                if (BeforeCommit() is { } before) return await ResumeInterruptedStartupAsync(before);
                if (local is { Bytes: not null })
                {
                    _pendingOfflinePlayback = (track.Id, cts);
                    if (!automatic) _manualQueueAdvanceCts = cts;
                    SetStatus("Открываем локальный трек…");
                    if (automatic && !_player.IsPlaying) UpdateControls();
                    try
                    {
                        admission = await VerifyQueuePlaybackSourceAsync(track, local, cts.Token);
                    }
                    catch (LocalAudioSourceException error)
                    {
                        if (!OwnsAdvance() || StopForSleep()) return QueueAdvanceResult.Abandoned;
                        if (error is LocalAudioIntegrityException && CanStreamAfterLocalIntegrityFailure(track))
                            streamOnly = true;
                        else if (automatic || preserveAutomaticWave || OwnsDeferredQueueTrackEnd())
                        {
                            if (!automatic && occurrence is { } rejected) _deferredQueueTrackEnd?.Excluded.Add(rejected);
                            return QueueAdvanceResult.LocalRejected;
                        }
                        else
                        {
                            if (interruptedStartup) return await ResumeInterruptedStartupAsync(QueueAdvanceResult.LocalRejected);
                            if (wasPlaying && !_player.IsPlaying) StopCompletedQueue();
                            ShowError(error);
                            return QueueAdvanceResult.Abandoned;
                        }
                    }
                    if (BeforeCommit() is { } after) return await ResumeInterruptedStartupAsync(after);
                }
            }
            var continueAfterEnd = OwnsDeferredQueueTrackEnd() || routeBlocksPromotion || interruptedStartup;
            var next = direction < 0 ? _playbackQueue.Previous() : _playbackQueue.Next(automatic, excluded);
            if (next is null) return QueueAdvanceResult.Abandoned;
            // Hand a verified file to the native slot without releasing it or repeating SHA.
            _queueAdmissionCts = null;
            if (preserveAutomaticWave)
            {
                operation = ++_advanceOperation;
                ownsManualContinuation = true;
            }
            ForgetRestoredPositionFor(next, restart: true);
            var opening = PlayTrackWithSourceAsync(next, promoted ? prepared : null, automatic, operation,
                alreadyPromoted: promoted, localAdmission: admission, streamOnly: streamOnly, continueAfterEnd: continueAfterEnd);
            admitted?.TrySetResult();
            await opening;
            return QueueAdvanceResult.Committed;
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested) { return QueueAdvanceResult.Abandoned; }
        finally
        {
            admission?.Dispose();
            if (ReferenceEquals(_queueAdmissionCts, cts)) _queueAdmissionCts = null;
            if (ReferenceEquals(_manualQueueAdvanceCts, cts)) _manualQueueAdvanceCts = null;
            if (ReferenceEquals(_queueAdvanceContinuation?.Request, cts)) _queueAdvanceContinuation = null;
            if (_pendingOfflinePlayback is { } pending && ReferenceEquals(pending.Request, cts)) _pendingOfflinePlayback = null;
            if (ownsManualContinuation && operation == _advanceOperation)
            {
                _advancingPlayback = false;
                RefreshNextPreparation();
            }
        }

        async Task<QueueAdvanceResult> ResumeInterruptedStartupAsync(QueueAdvanceResult result)
        {
            if (!interruptedStartup || !OwnsAdvance() || _playbackQueue.Current is not { } current) return result;
            // An unplayed current occurrence can still start if the attempted successor never commits.
            admission?.Dispose();
            admission = null;
            _queueAdmissionCts = null;
            var opening = PlayTrackWithSourceAsync(current, null, automatic: false, operation, continueAfterEnd: true);
            admitted?.TrySetResult();
            await opening;
            return result;
        }

        long? TargetOccurrence() => direction < 0 ? _playbackQueue.PeekPreviousOccurrenceToken() : _playbackQueue.PeekNextOccurrenceToken(automatic, excluded);
        bool OwnsAdvance() => !_closed && !cts.IsCancellationRequested && operation == _advanceOperation
            && playback == _playbackGeneration && queue == _playbackQueue.ReplacementVersion && from == _playbackQueue.CurrentOccurrenceToken
            && pause == _wavePauseRevision;
        bool StopForSleep()
        {
            if (!automatic) return false;
            if (HasMinuteSleepExpired()) { FinishSleepTimer(); return true; }
            if (CanAutoAdvancePlaybackOptions()) return false;
            if (!_player.IsPlaying) FinishSleepTimer();
            return true;
        }
        QueueAdvanceResult? BeforeCommit()
        {
            if (!OwnsAdvance() || StopForSleep()) return QueueAdvanceResult.Abandoned;
            if (TargetOccurrence() != occurrence) return automatic ? QueueAdvanceResult.TargetChanged : QueueAdvanceResult.Abandoned;
            if (local is not { Bytes: not null } || !BlocksOfflineOpen(track.Id)) return null;
            if (automatic && !_player.IsPlaying) StopCompletedQueue(OfflineRemovalPlaybackStatus);
            else SetStatus(OfflineRemovalPlaybackStatus);
            return QueueAdvanceResult.Abandoned;
        }
    }

    private async Task<QueueAdvanceResult> ContinueExistingQueueAsync(long operation, HashSet<long> excluded,
        PreparedPlayback? earlyFade = null)
    {
        while (PeekPlayableNext(naturalEnd: true, excluded) is { } next
            && _playbackQueue.PeekNextOccurrenceToken(naturalEnd: true, excluded) is { } occurrence)
        {
            if (IsPlaybackSelectionBlockedByRemoval(next))
            {
                if (!_player.IsPlaying) StopCompletedQueue(OfflineRemovalPlaybackStatus);
                else SetStatus(OfflineRemovalPlaybackStatus);
                return QueueAdvanceResult.Abandoned;
            }
            var result = await CommitQueueAdvanceAsync(next, earlyFade ?? ReadyNextPlayback(naturalEnd: true),
                1, true, operation, requirePrepared: earlyFade is not null, excluded: excluded);
            if (result == QueueAdvanceResult.LocalRejected) excluded.Add(occurrence);
            else if (result != QueueAdvanceResult.TargetChanged) return result;
        }
        return QueueAdvanceResult.Exhausted;
    }

    private sealed record WaveStartupAdmission(NativePlayer.LocalPlaybackAdmission? Local,
        NativePlayer.WaveFallbackAdmission? Online = null, bool RequiresStream = false) : IDisposable
    {
        public void Dispose() { Local?.Dispose(); Online?.Dispose(); }
    }

    private bool CanUseWaveStartupAdmission(TrackRow track, WaveStartupAdmission admission)
    {
        if (admission.Local is not null) return !BlocksOfflineOpen(track.Id);
        if (admission.RequiresStream && !CanStreamAfterLocalIntegrityFailure(track)) return false;
        return admission.Online is not { } online || online.IsReady && !AudioOutputChangePending
            && !_player.OutputRouteChanging && !LooksLikePreview(track, online.Duration);
    }

    private async Task<WaveStartupAdmission?> AdmitWaveStartupAsync(TrackRow track, CancellationTokenSource cts)
    {
        var source = RefreshLocalPlaybackSource(track);
        if (source is null)
        {
            if (!CanStreamAfterLocalIntegrityFailure(track)) return null;
            return _offline.HasRejectedPlaybackSource(track) ? await AdmitFallbackAsync() : new(null, RequiresStream: true);
        }
        if (source.Bytes is null) return new(null);
        if (BlocksOfflineOpen(track.Id)) return null;
        _pendingOfflinePlayback = (track.Id, cts);
        try
        {
            return new(await VerifyQueuePlaybackSourceAsync(track, source, cts.Token));
        }
        catch (LocalAudioSourceException error)
        {
            cts.Token.ThrowIfCancellationRequested();
            if (error is not LocalAudioIntegrityException || !CanStreamAfterLocalIntegrityFailure(track)) return null;
        }
        finally
        {
            if (_pendingOfflinePlayback is { } pending && ReferenceEquals(pending.Request, cts)) _pendingOfflinePlayback = null;
        }
        return await AdmitFallbackAsync();

        async Task<WaveStartupAdmission?> AdmitFallbackAsync()
        {
            NativePlayer.WaveFallbackAdmission? staged = null;
            try
            {
                var session = await EnsureSessionAsync(cts.Token);
                var audio = await _api.ResolveAudioAsync(session, track.RequestId, SelectedQuality, cts.Token);
                await EnsureAudioOutputReadyAsync(cts.Token);
                staged = await _player.PrepareWaveFallbackAsync(audio, cts.Token);
                await EnsureAudioOutputReadyAsync(cts.Token);
                var admission = new WaveStartupAdmission(null, staged, RequiresStream: true);
                if (!CanUseWaveStartupAdmission(track, admission)) return null;
                staged = null;
                return admission;
            }
            catch (Exception error) when (!cts.IsCancellationRequested && CanFallbackFromOnlineOpen(error))
            {
                _diagnostics.LastErrorType = "WaveFallback:" + error.GetType().Name;
                return null;
            }
            finally { staged?.Dispose(); }
        }
    }

    private async Task<NativePlayer.LocalPlaybackAdmission> VerifyQueuePlaybackSourceAsync(
        TrackRow track, LocalAudioSource source, CancellationToken token)
    {
        NativePlayer.LocalPlaybackAdmission? admission = null;
        try
        {
            if (QueuePlaybackBeforeVerificationForChecks is { } before) await before(token);
            await OpenLocalPlaybackVariantAsync(track, source, requireExactOfflineVariant: false, token, async candidate =>
            {
                admission = await _player.VerifyLocalAdvanceAsync(candidate, token);
            });
            return admission!;
        }
        catch
        {
            admission?.Dispose();
            throw;
        }
    }

    private async Task<LocalAudioSource> OpenLocalPlaybackVariantAsync(TrackRow track, LocalAudioSource source,
        bool requireExactOfflineVariant, CancellationToken token, Func<LocalAudioSource, Task> open)
    {
        var confirmedMismatch = false;
        var availabilityChanged = false;
        var account = _accountGeneration;
        var uid = _account?.Uid;
        var initialChecks = CurrentOfflineDownloadChecks;
        bool CanPublish() => !_closed && !token.IsCancellationRequested && account == _accountGeneration && uid == _account?.Uid
            && !BlocksOfflineOpen(track.Id);
        try
        {
            try
            {
                await open(source);
                if (CanPublish()) availabilityChanged |= RememberOpenedOfflineCopy(track, source);
                return source;
            }
            catch (LocalAudioSourceException error)
            {
                RememberOpenFailure(source, error);
                if (requireExactOfflineVariant || token.IsCancellationRequested) throw;
                foreach (var alternate in _offline.ResolveAlternatePlaybackSources(track, source))
                {
                    token.ThrowIfCancellationRequested();
                    if (BlocksOfflineOpen(track.Id)) throw;
                    try
                    {
                        await open(alternate);
                        if (CanPublish()) availabilityChanged |= RememberOpenedOfflineCopy(track, alternate);
                        return alternate;
                    }
                    catch (LocalAudioSourceException nextError) when (!token.IsCancellationRequested)
                    {
                        RememberOpenFailure(alternate, nextError);
                    }
                }
                throw;
            }
        }
        finally
        {
            if ((confirmedMismatch || availabilityChanged) && CanPublish()) RefreshVerifiedOfflineMarks(track, refreshDetails: availabilityChanged);
        }

        void RememberOpenFailure(LocalAudioSource candidate, LocalAudioSourceException error)
        {
            if (error is LocalAudioIntegrityException mismatch)
                confirmedMismatch |= _offline.RememberRejectedPlaybackSource(candidate, mismatch);
            else if (error is LocalAudioAccessException && candidate is { Bytes: not null, Digest: not null, Variant: not null } && CanPublish())
            {
                var current = CurrentOfflineDownloadChecks;
                if (current?.Refuses(candidate) == true) return;
                var previous = current?.GetValueOrDefault(track.Id);
                if (!ReferenceEquals(previous, initialChecks?.GetValueOrDefault(track.Id))
                    && previous?.VerifiedCopies?.Any(copy => _offline.MatchesOfflineCopy(candidate, copy)) == true) return;
                var refused = previous?.RefusedCopies ?? [];
                RememberOfflineDownloadChecks(new Dictionary<string, OfflineCopyCheck>(StringComparer.Ordinal)
                    { [track.Id] = new(track, previous?.VerifiedCopies, RetryOnNavigation: true, RefusedCopies: [.. refused, candidate]) });
                availabilityChanged = true;
            }
        }

    }

    private bool RememberOpenedOfflineCopy(TrackRow track, LocalAudioSource opened)
    {
        if (opened.Digest is null || CurrentOfflineDownloadChecks is not { } checks
            || _offline.AllowsVerifiedCopy(checks, track.Id, opened)) return false;
        try
        {
            var aliases = _offline.Read().Where(copy => _offline.MatchesPhysicalCopy(opened, copy)).ToLookup(copy => copy.Id);
            if (!aliases[track.Id].Any(copy => _offline.MatchesOfflineCopy(opened, copy))) return false;
            var updated = new Dictionary<string, OfflineCopyCheck>(checks, StringComparer.Ordinal);
            foreach (var (id, previous) in checks)
            {
                var refused = previous.RefusedCopies;
                var clearsRefusal = refused?.Any(copy => OfflineLibrary.SamePhysicalCopy(opened, copy)) == true;
                if (clearsRefusal) refused = refused!.Where(copy => !OfflineLibrary.SamePhysicalCopy(opened, copy)).ToArray();
                var verified = previous.VerifiedCopies;
                var additions = verified is not null && previous.RetryOnNavigation ? aliases[id].Except(verified).ToArray() : [];
                if (!clearsRefusal && additions.Length == 0) continue;
                if (verified is null && refused is not { Count: > 0 }) updated.Remove(id);
                else updated[id] = previous with { VerifiedCopies = verified is null ? null : [.. verified, .. additions], RefusedCopies = refused };
            }
            PublishOfflineDownloadChecks(updated);
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or MusicException)
        { _diagnostics.LastErrorType = "OfflineOpen:" + error.GetType().Name; return false; }
    }

    private void RefreshVerifiedOfflineMarks(TrackRow track, bool refreshDetails)
    {
        var recheckBackground = _offlineMarksCts is not null;
        InvalidateOfflineViewState();
        try
        {
            if (refreshDetails) ApplyOfflineStates(KnownTrackRows().Append(track).Distinct());
            else
            {
                var rows = KnownTrackRows().Where(row => row.Id == track.Id).Append(track).Distinct().ToArray();
                ApplyOfflineMarks(ReadOfflineMarks(rows, _lifetime.Token, CurrentOfflineDownloadChecks));
                if (recheckBackground) QueueOfflineMarks(KnownTrackRows().ToArray());
            }
            UpdateControls();
            UpdatePlaylistDownloadedStatus();
            if (_section == "offline-playlists" || refreshDetails && IsOfflineSection(_section))
                _ = RefreshCurrentOfflineViewAfterDownloadAsync();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or MusicException)
        {
            _diagnostics.LastErrorType = "OfflineMarks:" + error.GetType().Name;
        }
    }

    private const string OfflineRemovalPlaybackStatus = "Дождитесь завершения удаления аудио и повторите переход.";

    private bool IsPlaybackSelectionBlockedByRemoval(TrackRow? track, bool requireExactOfflineVariant = false)
        => track is not null && BlocksOfflineOpen(track.Id)
            && RefreshLocalPlaybackSource(track, requireExactOfflineVariant) is { Bytes: not null };

    private bool CanSelectWhileRemovingAudio(TrackRow track, bool requireExactOfflineVariant = false)
    {
        if (!IsPlaybackSelectionBlockedByRemoval(track, requireExactOfflineVariant)) return true;
        SetStatus(OfflineRemovalPlaybackStatus);
        return false;
    }

    private bool CanStreamAfterLocalIntegrityFailure(TrackRow track)
        => !track.IsOfflineLibraryRow && track.LocalPath is null && track.Available && _session is not null
            && !IsAudioOutputUnavailable && !BlocksOfflineOpen(track.Id);

    private static bool CanFallbackFromOnlineOpen(Exception error) => error is MusicException or IOException
        or UnauthorizedAccessException or InvalidOperationException or System.Net.Http.HttpRequestException or TimeoutException
        or System.Runtime.InteropServices.COMException or OperationCanceledException;

    private static bool LooksLikePreview(TrackRow track, TimeSpan duration)
        => OfflineAudioProbe.LooksLikePreview(track.DurationMs, duration);

    private static MusicException PreviewDurationError()
        => new("Длительность потока похожа на фрагмент. Проверьте подписку; воспроизведение остановлено.");
}
