namespace YMusic.Native;

public partial class MainWindow
{
    private double CrossfadeSeconds => _player.CrossfadeSeconds;
    private CancellationTokenSource? _preloadCts;
    private Task? _preloadTask;
    private PreloadIdentity? _preloadIdentity;
    private PreparedPlayback? _preparedPlayback;
    private bool _advancingPlayback;
    private WaveListening? _fadingWaveListening;
    private bool _fadingWaveNatural;
    private int? _wavePrefetchPlaybackGeneration;

    private sealed record PreloadIdentity(long QueueVersion, long? CurrentOccurrence, long? NextOccurrence,
        int PlaybackGeneration, int AccountGeneration, AudioQuality Quality, LocalAudioSource? LocalSource);
    private sealed record PreparedPlayback(PreloadIdentity Identity, TrackRow Track, Uri Source, string QualityLabel, bool Offline,
        LocalAudioSource? OpenedLocalSource = null);

    private PreloadIdentity NextPreloadIdentity(LocalAudioSource? localSource, bool naturalEnd = true) => new(
        _playbackQueue.Version, _playbackQueue.CurrentOccurrenceToken, _playbackQueue.PeekNextOccurrenceToken(naturalEnd),
        _playbackGeneration, _accountGeneration, SelectedQuality, localSource);

    private void RefreshNextPreparation()
    {
        if (_closed || _loadingStream || !_player.HasSource || _current is null || _offlineMaintenance
            || IsAudioOutputUnavailable || !CanAutoAdvancePlaybackOptions())
        { CancelNextPreparation(); return; }
        if (_advancingPlayback) return;
        // Obtain another server batch while there is still music to play. This
        // adds metadata only; a prefetched track never produces listening feedback.
        if (!_advancingWave && IsContinuableWaveTrack(_current) && _playbackQueue.CurrentIndex >= _queue.Count - 2
            && _wavePrefetchPlaybackGeneration != _playbackGeneration)
        {
            _wavePrefetchPlaybackGeneration = _playbackGeneration;
            _ = ExtendWaveQueueAsync();
        }
        var next = _playbackQueue.PeekNext(naturalEnd: true);
        if (next is null) { CancelNextPreparation(); return; }
        ApplyTrackLikeStates([next]);
        var local = RefreshLocalPlaybackSource(next);
        if (local is null && (next.IsOfflineLibraryRow || next.LocalPath is not null || _session is null || !next.Available))
        { CancelNextPreparation(); return; }
        var identity = NextPreloadIdentity(local);
        if (_preloadIdentity == identity) return; // Includes a failed attempt: no retry loop.
        CancelNextPreparation();
        _preloadIdentity = identity;
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, _accountCts.Token);
        _preloadCts = cts;
        _preloadTask = PrepareAsync();

        async Task PrepareAsync()
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                AudioDescriptor? audio = null;
                var localSource = identity.LocalSource;
                if (localSource is null)
                {
                    var session = await EnsureSessionAsync(cts.Token);
                    audio = await _api.ResolveAudioAsync(session, next.RequestId, identity.Quality, cts.Token);
                }
                else
                {
                    audio = await TryResolveOfflineUpgradeAsync(next, localSource.Path, identity.Quality, cts.Token);
                }
                cts.Token.ThrowIfCancellationRequested();
                if (_preloadIdentity != identity || NextPreloadIdentity(localSource) != identity) return;
                if (localSource is { Bytes: not null } && BlocksOfflineOpen(next.Id)) return;
                if (_offline.ResolvePlaybackSource(next) != localSource)
                {
                    _ = Dispatcher.InvokeAsync(() =>
                    {
                        if (_preloadIdentity == identity) RefreshNextPreparation();
                    });
                    return;
                }
                try { await PrepareSourceAsync(audio, localSource); }
                catch (LocalAudioIntegrityException) when (audio is null && !cts.IsCancellationRequested
                    && _preloadIdentity == identity && NextPreloadIdentity(identity.LocalSource) == identity
                    && CanStreamAfterLocalIntegrityFailure(next))
                {
                    _diagnostics.LastErrorType = nameof(LocalAudioIntegrityException);
                    _preloadIdentity = identity = NextPreloadIdentity(_offline.ResolvePlaybackSource(next));
                    var session = await EnsureSessionAsync(cts.Token);
                    var stream = await _api.ResolveAudioAsync(session, next.RequestId, identity.Quality, cts.Token);
                    cts.Token.ThrowIfCancellationRequested();
                    if (_preloadIdentity != identity || NextPreloadIdentity(identity.LocalSource) != identity
                        || !CanStreamAfterLocalIntegrityFailure(next)) return;
                    await PrepareSourceAsync(stream, null);
                }
                catch (Exception error) when (audio is not null && localSource is not null && CanFallbackFromOnlineOpen(error)
                    && !cts.IsCancellationRequested && _preloadIdentity == identity && NextPreloadIdentity(localSource) == identity
                    && !IsAudioOutputUnavailable && !BlocksOfflineOpen(next.Id))
                {
                    var fallback = _offline.ResolvePlaybackSource(next);
                    if (fallback is null) throw;
                    _diagnostics.LastErrorType = "PreloadUpgradeOpen:" + error.GetType().Name;
                    _preloadIdentity = identity = identity with { LocalSource = fallback };
                    await PrepareSourceAsync(null, fallback);
                }
                cts.Token.ThrowIfCancellationRequested();
                if (_preloadIdentity != identity) return;
                _diagnostics.Timing("next_track_prepared_ms", clock.Elapsed.TotalMilliseconds);

                async Task PrepareSourceAsync(AudioDescriptor? stream, LocalAudioSource? local)
                {
                    if (stream is null)
                    {
                        await OpenLocalPlaybackVariantAsync(next, local!, requireExactOfflineVariant: false, cts.Token, candidate =>
                        {
                            cts.Token.ThrowIfCancellationRequested();
                            if (_preloadIdentity != identity || NextPreloadIdentity(identity.LocalSource) != identity)
                                throw new OperationCanceledException(cts.Token);
                            _preloadIdentity = identity = NextPreloadIdentity(_offline.ResolvePlaybackSource(next));
                            _preparedPlayback = new(identity, next, PlaybackInput.FileUri(candidate.Path), candidate.Quality, Offline: true,
                                OpenedLocalSource: candidate);
                            return _player.PrepareLocalNextAsync(candidate, cts.Token);
                        });
                        return;
                    }
                    _preparedPlayback = new(identity, next, stream.Uri, stream.QualityLabel, Offline: false);
                    await _player.PrepareNextAsync(stream, cts.Token);
                    if (local is not null && _preloadIdentity == identity && LooksLikePreview(next, _player.PreparedDuration))
                    {
                        _player.DiscardPrepared();
                        throw PreviewDurationError();
                    }
                }
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested) { }
            catch (Exception ex)
            {
                if (_preloadIdentity == identity)
                { _preparedPlayback = null; _diagnostics.LastErrorType = "Preload:" + ex.GetType().Name; }
            }
            finally
            {
                if (ReferenceEquals(_preloadCts, cts)) _preloadCts = null;
                cts.Dispose();
            }
        }
    }

    private void CancelNextPreparation(bool discardPlayer = true)
    {
        var cts = _preloadCts; _preloadCts = null;
        _preloadIdentity = null; _preparedPlayback = null;
        cts?.Cancel();
        if (discardPlayer) _player.DiscardPrepared();
    }

    private PreparedPlayback? ReadyNextPlayback(bool naturalEnd)
    {
        if (_preparedPlayback is not { } prepared || !_player.HasPrepared) return null;
        var next = _playbackQueue.PeekNext(naturalEnd);
        if (next is null || !ReferenceEquals(next, prepared.Track)
            || NextPreloadIdentity(prepared.Identity.LocalSource, naturalEnd) != prepared.Identity
            || LooksLikePreview(next, _player.PreparedDuration)) return null;
        var preferred = RefreshLocalPlaybackSource(next);
        if (preferred == prepared.Identity.LocalSource
            && (prepared.OpenedLocalSource is not { } opened || opened == preferred
                || preferred is not null && _offline.ResolveAlternatePlaybackSources(next, preferred).Contains(opened))) return prepared;
        CancelNextPreparation();
        return null;
    }

    private bool PromotePrepared(TrackRow track, PreparedPlayback? prepared, LocalAudioSource? localSource, long? occurrence, bool automatic)
    {
        if (IsAudioOutputUnavailable || prepared is null || !ReferenceEquals(prepared.Track, track) || prepared.Identity.NextOccurrence != occurrence
            || prepared.Identity.PlaybackGeneration != _playbackGeneration || prepared.Identity.AccountGeneration != _accountGeneration
            || prepared.Identity.Quality != SelectedQuality || prepared.Identity.LocalSource != localSource
            || localSource is { Bytes: not null } && BlocksOfflineOpen(track.Id)
            || LooksLikePreview(track, _player.PreparedDuration)) return false;
        var remaining = Math.Max(0, (_player.Duration - _player.Position).TotalSeconds / _player.SpeedRatio);
        var fade = _player.IsPlaying ? Math.Min(automatic ? CrossfadeSeconds : CrossfadeSeconds > 0 ? .22 : 0, remaining) : 0;
        if (automatic && HasMinuteSleepExpired())
        {
            // The transition can be due before the sleep dispatcher tick runs.
            FinishSleepTimer();
            return false;
        }
        if (!_player.TryPlayPrepared(prepared.Source, TimeSpan.FromSeconds(fade))) return false;
        if (fade > 0)
        {
            _fadingWaveListening = _waveListening; _waveListening = null;
            _fadingWaveNatural = automatic;
        }
        else ReportWaveEnd(automatic);
        CancelNextPreparation(discardPlayer: false);
        return true;
    }

    private void OnTransitionFinished(bool natural)
    {
        var listening = _fadingWaveListening; _fadingWaveListening = null;
        FinishWaveListening(listening, natural && _fadingWaveNatural);
    }

    private void TryAutomaticTransition()
    {
        if (_closed || _loadingStream || _advancingPlayback || !_player.IsPlaying || _player.OutputRouteChanging || !CanAutoAdvancePlaybackOptions()
            || _seekDragging || _expandedSeekDragging || _shell?.IsSeeking == true || _pendingSeek is not null) return;
        var duration = _player.Duration.TotalSeconds;
        var remaining = (duration - _player.Position.TotalSeconds) / _player.SpeedRatio;
        var overlap = Math.Min(CrossfadeSeconds, duration / _player.SpeedRatio / 3);
        if (duration > 0 && remaining > 0 && remaining <= overlap && ReadyNextPlayback(true) is { } prepared)
            _ = AdvanceCoreAsync(1, true, earlyFade: prepared);
    }
}
