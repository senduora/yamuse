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

    private sealed record PreloadIdentity(long QueueVersion, long? CurrentOccurrence, long? NextOccurrence,
        int PlaybackGeneration, int AccountGeneration, AudioQuality Quality, string? LocalPath);
    private sealed record PreparedPlayback(PreloadIdentity Identity, TrackRow Track, Uri Source, string QualityLabel, bool Offline);

    private PreloadIdentity NextPreloadIdentity(TrackRow next, bool naturalEnd = true) => new(
        _playbackQueue.Version, _playbackQueue.CurrentOccurrenceToken, _playbackQueue.PeekNextOccurrenceToken(naturalEnd),
        _playbackGeneration, _accountGeneration, SelectedQuality, next.LocalPath ?? next.DownloadedPath);

    private void RefreshNextPreparation()
    {
        if (_closed || _loadingStream || !_player.HasSource || _current is null || _offlineMaintenance)
        { CancelNextPreparation(); return; }
        if (_advancingPlayback || _advancingWave) return;
        // Obtain another server batch while there is still music to play. This
        // adds metadata only; a prefetched track never produces listening feedback.
        if (_waveBatch is not null && _waveSources.ContainsKey(_current) && _playbackQueue.CurrentIndex >= _queue.Count - 2)
        {
            CancelNextPreparation();
            _ = ExtendWaveQueueAsync();
            return;
        }
        var next = _playbackQueue.PeekNext(naturalEnd: true);
        if (next is null) { CancelNextPreparation(); return; }
        ApplyOfflineStates([next]);
        var identity = NextPreloadIdentity(next);
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
                if (identity.LocalPath is null)
                {
                    var session = await EnsureSessionAsync(cts.Token);
                    audio = await _api.ResolveAudioAsync(session, next.RequestId, identity.Quality, cts.Token);
                }
                cts.Token.ThrowIfCancellationRequested();
                if (_preloadIdentity != identity || NextPreloadIdentity(next) != identity) return;
                var source = identity.LocalPath is { } path ? new Uri(path) : audio!.Uri;
                var prepared = new PreparedPlayback(identity, next, source,
                    next.LocalQuality ?? next.DownloadedQuality ?? audio?.QualityLabel ?? "Локальное аудио", identity.LocalPath is not null);
                _preparedPlayback = prepared;
                if (audio is null) await _player.PrepareNextAsync(source, cts.Token);
                else await _player.PrepareNextAsync(audio, cts.Token);
                cts.Token.ThrowIfCancellationRequested();
                if (_preloadIdentity != identity) return;
                _diagnostics.Timing("next_track_prepared_ms", clock.Elapsed.TotalMilliseconds);
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
        return next is not null && ReferenceEquals(next, prepared.Track) && NextPreloadIdentity(next, naturalEnd) == prepared.Identity
            ? prepared : null;
    }

    private bool PromotePrepared(TrackRow track, PreparedPlayback? prepared, bool automatic)
    {
        if (prepared is null || !ReferenceEquals(prepared.Track, track) || prepared.Identity.NextOccurrence != _playbackQueue.CurrentOccurrenceToken
            || prepared.Identity.PlaybackGeneration != _playbackGeneration || prepared.Identity.AccountGeneration != _accountGeneration
            || prepared.Identity.Quality != SelectedQuality) return false;
        _player.FinishTransition();
        var remaining = Math.Max(0, (_player.Duration - _player.Position).TotalSeconds / _player.SpeedRatio);
        var fade = _player.IsPlaying ? Math.Min(automatic ? CrossfadeSeconds : CrossfadeSeconds > 0 ? .22 : 0, remaining) : 0;
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
        if (_closed || _loadingStream || _advancingPlayback || !_player.IsPlaying || _seekDragging || _pendingSeek is not null) return;
        var duration = _player.Duration.TotalSeconds;
        var remaining = (duration - _player.Position.TotalSeconds) / _player.SpeedRatio;
        var overlap = Math.Min(CrossfadeSeconds, duration / _player.SpeedRatio / 3);
        if (duration > 0 && remaining > 0 && remaining <= overlap && ReadyNextPlayback(true) is not null)
            _ = AdvanceAsync(1, true);
    }
}
