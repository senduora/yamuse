using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace YMusic.Native;

// Own on the WPF dispatcher, like MediaPlayer itself. A slot can own a temporary
// download or a read lease. It never deletes a user/offline file.
internal sealed partial class NativePlayer : IDisposable
{
    private Slot? _current, _prepared, _fading, _waveFallback;
    private readonly AudioBuffer _buffer;
    private CancellationTokenSource? _opening, _preparing, _preparingWaveFallback;
    private WaveFallbackAdmission? _waveFallbackOwner;
    private readonly List<(string Path, Task Released)> _localSourceReleases = [];
    private readonly DispatcherTimer _fadeTimer;
    private readonly Stopwatch _fadeClock = new();
    private TimeSpan _fadeElapsed, _fadeDuration;
    private double _volume = 0.5;
    private bool _disposed;
    internal Func<MediaPlayer, TimeSpan>? ReadWpfPositionForChecks { get; set; }
    internal Func<CancellationToken, Task>? BeforeRouteDecoderInstallForChecks { get; set; }
    internal Action<Action<Exception>>? RouteDecoderOpenedForChecks { get; set; }
    internal Action<Uri>? AfterDecoderCloseForChecks { get; set; }
    internal Action<Uri, bool>? BeforeDecoderTransportForChecks { get; set; }
    internal Action? BeforeDecoderSeekForChecks { get; set; }

    internal NativePlayer(AudioBuffer? buffer = null)
    {
        _buffer = buffer ?? new AudioBuffer();
        _fadeTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(20) };
        _fadeTimer.Tick += (_, _) => UpdateFade();
    }
    public string? LastNativeError { get; private set; }
    public bool IsPlaying { get; private set; }
    public bool HasSource => _current?.HasSource == true;
    public bool HasPrepared => _prepared?.Ready == true;
    internal TimeSpan PreparedDuration => _prepared?.Duration ?? TimeSpan.Zero;
    public string? SourceLocalPath => LocalPath(_current);
    internal string? SourceVerifiedLocalDigest => _current?.VerifiedLocalDigest;
    public string? PreparedLocalPath => LocalPath(_prepared);
    public string? FadingLocalPath => LocalPath(_fading);
    public double Volume { get => _volume; set => SetVolume(value); }
    public double SpeedRatio { get; private set; } = 1;
    public double CrossfadeSeconds { get; private set; } = 3;
    public TimeSpan Position => _pausedOutputClock is { } clock && ReferenceEquals(clock.Slot, _current)
        ? clock.Position : _current?.Position ?? TimeSpan.Zero;
    public TimeSpan Duration => _current?.Duration ?? TimeSpan.Zero;
    internal bool IsCurrentAudioAdvancing => IsPlaying && !_routeMixHeld && _current?.IsAudioAdvancing == true;
    internal bool IsFadingAudioAdvancing => IsPlaying && !_routeMixHeld && _fading?.IsAudioAdvancing == true;
    internal event Action? PlaybackActivityChanged;
    public event EventHandler? Ended;
    public event Action<TimeSpan>? Failed;
    // true: the overlap ended naturally; false: stop, seek, replacement or failure.
    // Once per actual overlap, after its outgoing player and owned file are released.
    public event Action<bool>? TransitionFinished;

    public Task OpenAsync(Uri uri, CancellationToken cancellationToken, IProgress<AudioProgress>? progress = null)
        => OpenCoreAsync(uri, null, cancellationToken, progress);
    public Task OpenAsync(AudioDescriptor audio, CancellationToken cancellationToken, IProgress<AudioProgress>? progress = null)
        => OpenCoreAsync(audio.Uri, audio, cancellationToken, progress);
    internal Task OpenPausedAsync(Uri uri, CancellationToken cancellationToken, IProgress<AudioProgress>? progress = null)
        => OpenCoreAsync(uri, null, cancellationToken, progress, false);
    internal Task OpenPausedAsync(AudioDescriptor audio, CancellationToken cancellationToken, IProgress<AudioProgress>? progress = null)
        => OpenCoreAsync(audio.Uri, audio, cancellationToken, progress, false);
    internal Task OpenLocalPausedAsync(LocalAudioSource source, CancellationToken cancellationToken, LocalPlaybackAdmission? admission = null)
        => OpenCoreAsync(PlaybackInput.FileUri(source.Path), null, cancellationToken, null, play: false, localSource: source, admission: admission);
    private async Task OpenCoreAsync(Uri uri, AudioDescriptor? audio, CancellationToken cancellationToken, IProgress<AudioProgress>? progress,
        bool play = true, LocalAudioSource? localSource = null, LocalPlaybackAdmission? admission = null)
    {
        ValidateSource(uri); ObjectDisposedException.ThrowIf(_disposed, this);
        Stop();
        var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _opening = operation;
        Slot? slot = null; PlaybackInput? input = null;
        LocalAudioLease? lease = null;
        TaskCompletionSource? unlockingLocal = null;
        try
        {
            var local = uri;
            if (!uri.IsFile)
            {
                input = await _buffer.OpenPlaybackAsync(audio ?? new AudioDescriptor(uri, "mp3", 0), operation.Token, progress);
                local = input.Uri;
            }
            if (localSource is not null)
            {
                unlockingLocal = TrackLocalSourceOpen(localSource);
                lease = admission?.Take(localSource) ?? await localSource.HoldAsync(operation.Token);
            }
            operation.Token.ThrowIfCancellationRequested();
            if (!ReferenceEquals(_opening, operation)) throw new OperationCanceledException(operation.Token);
            slot = new Slot(this, uri, input, lease, Volume, SpeedRatio); input = null; lease = null;
            _current = slot; LastNativeError = null;
            slot.Open(local);
            await slot.Opened.Task.WaitAsync(TimeSpan.FromSeconds(30), operation.Token);
            operation.Token.ThrowIfCancellationRequested();
            if (!ReferenceEquals(slot, _current)) throw new OperationCanceledException(operation.Token);
            ApplyGains();
            if (play) { slot.Play(); IsPlaying = true; }
        }
        catch
        {
            if (ReferenceEquals(_opening, operation)) Stop();
            else slot?.Close(); // A stale completion never stops a newer source.
            throw;
        }
        finally
        {
            input?.Dispose();
            lease?.Dispose();
            unlockingLocal?.TrySetResult();
            if (ReferenceEquals(_opening, operation)) _opening = null;
            operation.Dispose();
        }
    }

    public Task PrepareNextAsync(Uri uri, CancellationToken cancellationToken)
        => PrepareCoreAsync(uri, null, cancellationToken);
    public Task PrepareNextAsync(AudioDescriptor audio, CancellationToken cancellationToken)
        => PrepareCoreAsync(audio.Uri, audio, cancellationToken);
    internal Task PrepareLocalNextAsync(LocalAudioSource source, CancellationToken cancellationToken)
        => PrepareCoreAsync(PlaybackInput.FileUri(source.Path), null, cancellationToken, source);
    internal async Task<WaveFallbackAdmission> PrepareWaveFallbackAsync(AudioDescriptor audio, CancellationToken cancellationToken)
    {
        DiscardWaveFallback();
        var owner = new WaveFallbackAdmission(this, audio);
        _waveFallbackOwner = owner;
        try
        {
            await PrepareCoreAsync(audio.Uri, audio, cancellationToken, wave: owner);
            return owner;
        }
        catch { owner.Dispose(); throw; }
    }

    private async Task PrepareCoreAsync(Uri uri, AudioDescriptor? audio, CancellationToken cancellationToken,
        LocalAudioSource? localSource = null, WaveFallbackAdmission? wave = null)
    {
        ValidateSource(uri); ObjectDisposedException.ThrowIf(_disposed, this);
        if (wave is null) DiscardPrepared();
        var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (wave is null) _preparing = operation;
        else _preparingWaveFallback = operation;
        Slot? slot = null; PlaybackInput? input = null;
        LocalAudioLease? lease = null;
        TaskCompletionSource? unlockingLocal = null;
        try
        {
            var local = uri;
            if (!uri.IsFile)
            {
                input = await _buffer.OpenPlaybackAsync(audio ?? new AudioDescriptor(uri, "mp3", 0), operation.Token);
                local = input.Uri;
            }
            if (localSource is not null)
            {
                unlockingLocal = TrackLocalSourceOpen(localSource);
                lease = await localSource.HoldAsync(operation.Token);
            }
            operation.Token.ThrowIfCancellationRequested();
            if (!OwnsPreparation()) throw new OperationCanceledException(operation.Token);
            slot = new Slot(this, uri, input, lease, 0, SpeedRatio); input = null; lease = null;
            if (wave is null) _prepared = slot;
            else _waveFallback = slot;
            slot.Open(local); // Decode/open only. Never Play before promotion.
            await slot.Opened.Task.WaitAsync(TimeSpan.FromSeconds(30), operation.Token);
            // MediaOpened can trigger promotion before this continuation runs.
            // The slot then belongs to playback; its old preload token has no authority.
            if (ReferenceEquals(slot, _current) || ReferenceEquals(slot, _fading)) return;
            operation.Token.ThrowIfCancellationRequested();
            if (!ReferenceEquals(slot, wave is null ? _prepared : _waveFallback)) throw new OperationCanceledException(operation.Token);
        }
        catch
        {
            if (slot is not null && (ReferenceEquals(slot, _current) || ReferenceEquals(slot, _fading))) return;
            if (!OwnsPreparation()) slot?.Close();
            else if (wave is null) DiscardPrepared();
            else DiscardWaveFallback();
            throw;
        }
        finally
        {
            input?.Dispose();
            lease?.Dispose();
            unlockingLocal?.TrySetResult();
            if (ReferenceEquals(_preparing, operation)) _preparing = null;
            if (ReferenceEquals(_preparingWaveFallback, operation)) _preparingWaveFallback = null;
            operation.Dispose();
        }

        bool OwnsPreparation() => ReferenceEquals(wave is null ? _preparing : _preparingWaveFallback, operation);
    }

    internal sealed class WaveFallbackAdmission(NativePlayer owner, AudioDescriptor audio) : IDisposable
    {
        private Slot? _adopted;
        internal AudioDescriptor Audio { get; } = audio;
        internal bool IsReady => ReferenceEquals(owner._waveFallbackOwner, this) && owner._waveFallback?.Ready == true;
        internal TimeSpan Duration => IsReady ? owner._waveFallback!.Duration : TimeSpan.Zero;
        internal bool TryAdopt()
        {
            if (!IsReady || owner.OutputRouteChanging) return false;
            var slot = owner._waveFallback!;
            owner._waveFallback = null;
            owner._waveFallbackOwner = null;
            try
            {
                owner.Stop();
                owner._current = slot;
                owner.LastNativeError = null;
                owner.ApplyGains();
                _adopted = slot;
                return true;
            }
            catch
            {
                if (ReferenceEquals(owner._current, slot)) owner._current = null;
                try { slot.Close(); }
                catch (Exception cleanup) when (cleanup is COMException or InvalidOperationException) { }
                throw;
            }
        }
        public void Dispose()
        {
            if (ReferenceEquals(owner._waveFallbackOwner, this)) owner.DiscardWaveFallback();
            var adopted = _adopted;
            _adopted = null;
            // An adopted decoder is still provisional until its first native Play.
            if (adopted is { HasPlayed: false } && ReferenceEquals(owner._current, adopted)) owner.Stop();
        }
    }

    private void DiscardWaveFallback()
    {
        _waveFallbackOwner = null;
        var preparing = _preparingWaveFallback; _preparingWaveFallback = null; preparing?.Cancel();
        var staged = _waveFallback; _waveFallback = null; staged?.Close();
    }

    public bool TryPlayPrepared(Uri source, TimeSpan crossfade)
    {
        ValidateSource(source); ObjectDisposedException.ThrowIf(_disposed, this);
        if (crossfade < TimeSpan.Zero || crossfade > TimeSpan.FromSeconds(10)) throw new ArgumentOutOfRangeException(nameof(crossfade));
        if (OutputRouteChanging) return false;
        if (_prepared is not { Ready: true } next || !string.Equals(next.RequestedSource.AbsoluteUri, source.AbsoluteUri, StringComparison.Ordinal)) return false;
        var previous = _current;
        var overlap = IsPlaying && previous?.Ready == true && crossfade > TimeSpan.Zero;
        try
        {
            next.Volume = overlap ? 0 : Volume * OutputMixGain * next.Gain;
            next.Speed = SpeedRatio;
            next.Play();
        }
        catch (Exception error) when (error is InvalidOperationException or COMException)
        {
            try { DiscardPrepared(); }
            catch (Exception cleanup) when (cleanup is COMException or InvalidOperationException) { }
            return false;
        }
        // Play succeeded. Retiring either old decoder must not hide promotion.
        Exception? retirementError = null;
        try { CompleteTransition(natural: false); }
        catch (Exception error) when (error is COMException or InvalidOperationException) { retirementError = error; }
        var opening = _opening; _opening = null; opening?.Cancel();
        _prepared = null; _preparing = null;
        _current = next; _pausedOutputClock = null; IsPlaying = true;
        if (overlap)
        {
            _fading = previous; _fadeDuration = crossfade; _fadeElapsed = TimeSpan.Zero;
            _fadeClock.Restart(); _fadeTimer.Start();
        }
        else
        {
            try { previous?.Close(); }
            catch (Exception error) when (error is COMException or InvalidOperationException) { retirementError ??= error; }
        }
        LastNativeError = retirementError is null ? null : retirementError.GetType().Name + ":0x" + retirementError.HResult.ToString("X8");
        StartGainTimer(); // A prepared slot may still be ramping when it becomes audible.
        return true;
    }

    public void DiscardPrepared()
    {
        var preparing = _preparing; _preparing = null; preparing?.Cancel();
        var prepared = _prepared; _prepared = null; prepared?.Close();
    }
    public void FinishTransition() => CompletePlaybackTransition(natural: false);

    public bool Toggle()
    {
        if (_current?.Ready != true) return false;
        if (IsPlaying) return PauseOutputTransport();
        if (!OutputRouteIsValid) return false;
        try
        {
            if (!_routeMixHeld)
            {
                if (_pausedOutputClock is { } clock && ReferenceEquals(clock.Slot, _current))
                    clock.Slot.Position = clock.Position;
                PlaySlotsAndFade();
                _pausedOutputClock = null;
            }
            IsPlaying = true;
            return true;
        }
        catch (Exception error) when (error is COMException or InvalidOperationException)
        {
            RecordNativeError(error);
            PauseOutputTransport();
            return false;
        }
    }

    private void PauseSlotsAndFade()
    {
        _current?.Pause(); _fading?.Pause();
        PauseFadeClock();
    }

    private void PauseFadeClock()
    {
        _fadeElapsed += _fadeClock.Elapsed; _fadeClock.Reset(); _fadeTimer.Stop();
    }

    private void PlaySlotsAndFade()
    {
        if (_current?.Ready == true) _current.Play();
        _fading?.Play();
        if (_fading is not null) { _fadeClock.Restart(); _fadeTimer.Start(); }
    }

    public bool Seek(TimeSpan position)
    {
        FinishTransition();
        if (_current is not { } current) return false;
        try
        {
            var target = TimeSpan.FromSeconds(Math.Clamp(position.TotalSeconds, 0, Duration.TotalSeconds));
            BeforeDecoderSeekForChecks?.Invoke();
            current.Position = target;
            if (_pausedOutputClock is { } clock && ReferenceEquals(clock.Slot, current)) _pausedOutputClock = (current, target);
            return true;
        }
        catch (Exception error) when (error is COMException or InvalidOperationException)
        {
            RecordNativeError(error);
            return false;
        }
    }
    public void SetVolume(double volume)
    {
        if (!double.IsFinite(volume)) throw new ArgumentOutOfRangeException(nameof(volume));
        _volume = Math.Clamp(volume, 0, 1);
        ApplyGains();
    }
    public void SetSpeed(double speed)
    {
        if (!double.IsFinite(speed) || speed is < 0.5 or > 2) throw new ArgumentOutOfRangeException(nameof(speed));
        SpeedRatio = speed;
        if (_current is not null) _current.Speed = speed;
        if (_prepared is not null) _prepared.Speed = speed;
        if (_waveFallback is not null) _waveFallback.Speed = speed;
        if (_fading is not null) _fading.Speed = speed;
    }
    public void SetCrossfade(double seconds)
    {
        if (!double.IsFinite(seconds) || seconds is < 0 or > 10) throw new ArgumentOutOfRangeException(nameof(seconds));
        CrossfadeSeconds = seconds;
    }
    private double FadeFraction => _fading is null ? 1 : Math.Clamp((_fadeElapsed + _fadeClock.Elapsed).TotalSeconds / _fadeDuration.TotalSeconds, 0, 1);
    private void ApplyGains()
    {
        var fraction = FadeFraction;
        if (_current is not null) _current.Volume = Volume * OutputMixGain * _current.Gain * fraction;
        if (_fading is not null) _fading.Volume = Volume * OutputMixGain * _fading.Gain * (1 - fraction);
        if (_prepared is not null) _prepared.Volume = 0;
    }
    private void UpdateFade()
    {
        if (_fading is not null && FadeFraction >= 1) CompletePlaybackTransition(natural: true);
        else ApplyGains();
    }

    private void CompletePlaybackTransition(bool natural)
    {
        try { CompleteTransition(natural); }
        catch (Exception error) when (error is COMException or InvalidOperationException) { RecordNativeError(error); }
    }

    private void RecordNativeError(Exception error)
        => LastNativeError = error.GetType().Name + ":0x" + error.HResult.ToString("X8");
    private void CompleteTransition(bool natural)
    {
        _fadeTimer.Stop(); _fadeClock.Reset(); _fadeElapsed = TimeSpan.Zero;
        var fading = _fading; _fading = null;
        try { fading?.Close(); }
        finally
        {
            try { if (_current is not null) _current.Volume = Volume * OutputMixGain * _current.Gain; }
            finally { if (fading is not null) TransitionFinished?.Invoke(natural); }
        }
    }

    private void OnMediaFailed(Slot slot, Exception error)
    {
        if (!slot.Ready) slot.Opened.TrySetException(new MusicException("Windows не удалось открыть аудиофайл. Попробуйте другой трек."));
        if (ReferenceEquals(slot, _current))
        {
            RecordNativeError(error);
            if (!slot.Ready) { IsPlaying = false; FinishTransition(); return; }
            var position = slot.PositionAfterFailure();
            try { Stop(); }
            catch (Exception cleanup) when (cleanup is COMException or InvalidOperationException) { }
            Failed?.Invoke(position);
        }
        else if (ReferenceEquals(slot, _prepared)) { _prepared = null; slot.Close(); }
        else if (ReferenceEquals(slot, _waveFallback)) { _waveFallback = null; slot.Close(); }
        else if (ReferenceEquals(slot, _fading)) CompletePlaybackTransition(natural: false);
    }
    private void OnMediaEnded(Slot slot)
    {
        if (ReferenceEquals(slot, _fading)) { CompletePlaybackTransition(natural: true); return; }
        if (!ReferenceEquals(slot, _current) || !IsPlaying) return;
        IsPlaying = false; CompletePlaybackTransition(natural: false); Ended?.Invoke(this, EventArgs.Empty);
    }
    private void OnPlaybackActivityChanged(Slot slot)
    {
        if (ReferenceEquals(slot, _current) || ReferenceEquals(slot, _fading)) PlaybackActivityChanged?.Invoke();
    }
    public void Stop()
    {
        var opening = _opening; _opening = null;
        var current = _current; _current = null; _pausedOutputClock = null; IsPlaying = false;
        _gainTimer?.Stop();
        try
        {
            opening?.Cancel();
            try { DiscardPrepared(); }
            finally { DiscardWaveFallback(); }
        }
        finally
        {
            try { CompleteTransition(natural: false); }
            finally { current?.Close(); }
        }
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { Stop(); } finally { _buffer.Dispose(); }
    }
    internal Task WaitForClosedLocalSourcesAsync(IReadOnlySet<string> paths)
    {
        _localSourceReleases.RemoveAll(source => source.Released.IsCompleted);
        return Task.WhenAll(_localSourceReleases.Where(source => paths.Contains(source.Path)).Select(source => source.Released));
    }

    internal async Task<LocalPlaybackAdmission> VerifyLocalAdvanceAsync(LocalAudioSource source, CancellationToken token)
    {
        var released = TrackLocalSourceOpen(source);
        try { return new(source, await source.HoldAsync(token), released); }
        catch { released?.TrySetResult(); throw; }
    }

    internal sealed class LocalPlaybackAdmission(LocalAudioSource source, LocalAudioLease lease, TaskCompletionSource? released) : IDisposable
    {
        internal LocalAudioSource Source { get; } = source;
        private LocalAudioLease? _lease = lease;
        internal LocalAudioLease? Take(LocalAudioSource requested)
            => requested == Source ? Interlocked.Exchange(ref _lease, null) : null;
        public void Dispose()
        {
            Interlocked.Exchange(ref _lease, null)?.Dispose();
            released?.TrySetResult();
        }
    }

    private TaskCompletionSource? TrackLocalSourceOpen(LocalAudioSource source)
    {
        // Removal must also wait for a cancelled verifier that still holds its handle.
        if (source.Digest is null) return null;
        var released = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _localSourceReleases.RemoveAll(operation => operation.Released.IsCompleted);
        _localSourceReleases.Add((source.Path, released.Task));
        return released;
    }

    private void ReleaseLocalSource(LocalAudioLease lease, IEnumerable<Task> routing, Task? normalization)
    {
        var readers = routing.Where(task => !task.IsCompleted).ToList();
        if (normalization is { IsCompleted: false }) readers.Add(normalization);
        if (readers.Count == 0) { lease.Dispose(); return; }
        var released = Task.WhenAll(readers).ContinueWith(reading =>
        {
            _ = reading.Exception;
            lease.Dispose();
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        _localSourceReleases.RemoveAll(source => source.Released.IsCompleted);
        _localSourceReleases.Add((lease.Path, released));
    }

    private static string? LocalPath(Slot? slot)
        => slot?.LocalAudioPath ?? slot?.Input?.Path ?? (slot?.OpenedSource is { IsFile: true } source ? source.LocalPath : null);
    private static void ValidateSource(Uri source)
    { ArgumentNullException.ThrowIfNull(source); if (!source.IsAbsoluteUri) throw new ArgumentException("Требуется абсолютный адрес аудио.", nameof(source)); }

    private sealed class Slot
    {
        private readonly NativePlayer _owner;
        private readonly LocalAudioLease? _localAudio;
        private readonly List<Task> _routingReads = [];
        internal string? LocalAudioPath => _localAudio?.Path;
        internal string? VerifiedLocalDigest => _localAudio?.VerifiedDigest;
        internal PlaybackInput? Input { get; }
        private Windows.Media.Playback.MediaPlayer? _network;
        private Windows.Media.Core.MediaSource? _networkSource;
        private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
        private bool _wpfBuffering;
        internal Uri? OpenedSource { get; private set; }
        private int _routeRevision;
        internal bool HasSource => _networkSource is not null || Player.Source is not null;
        internal bool IsAudioAdvancing => _network is { } network
            ? network.PlaybackSession.PlaybackState == Windows.Media.Playback.MediaPlaybackState.Playing
            : Ready && !_wpfBuffering;
        private TimeSpan _lastPosition;
        private TimeSpan? _pendingWpfSeek;
        internal TimeSpan Position
        {
            get
            {
                var value = _network?.PlaybackSession.Position ?? _owner.ReadWpfPositionForChecks?.Invoke(Player) ?? Player.Position;
                if (_pendingWpfSeek is { } target && Math.Abs((value - target).TotalSeconds) < .8) _pendingWpfSeek = null;
                if (value > TimeSpan.Zero) _lastPosition = value;
                return value;
            }
            set
            {
                if (_network is not null) _network.PlaybackSession.Position = value;
                else
                {
                    Player.Position = value;
                    _pendingWpfSeek = value;
                }
                _lastPosition = value;
            }
        }
        internal TimeSpan PositionAfterFailure()
        {
            // A failed decoder can reset its clock or refuse to return it.
            try { _ = Position; }
            catch (Exception error) when (error is COMException or InvalidOperationException) { }
            return _lastPosition;
        }
        internal TimeSpan Duration => _network?.PlaybackSession.NaturalDuration ?? (Player.NaturalDuration.HasTimeSpan ? Player.NaturalDuration.TimeSpan : TimeSpan.Zero);
        internal double Volume { set { if (_network is not null) _network.Volume = value; else Player.Volume = value; } }
        internal double Speed { set { if (_network is not null) _network.PlaybackSession.PlaybackRate = value; else Player.SpeedRatio = value; } }
        internal bool HasPlayed { get; private set; }
        internal void Play()
        {
            _owner.BeforeDecoderTransportForChecks?.Invoke(RequestedSource, true);
            if (_network is not null) _network.Play(); else Player.Play();
            HasPlayed = true;
        }
        internal void Pause()
        {
            _owner.BeforeDecoderTransportForChecks?.Invoke(RequestedSource, false);
            if (_network is not null) _network.Pause(); else Player.Pause();
        }
        internal void Open(Uri source)
        {
            OpenedSource = source;
            if (Input?.Progressive != true && _owner._outputRoute.Id.Length == 0) { Player.Open(source); return; }
            _networkSource = Windows.Media.Core.MediaSource.CreateFromUri(source);
            var item = new Windows.Media.Playback.MediaPlaybackItem(_networkSource);
            _network = CreateNetworkPlayer(_owner._outputRoute, item);
            _network.Source = item;
        }
        private sealed class RouteDecoderOpening
        {
            private static readonly object Accepted = new();
            private object? _result;
            internal TaskCompletionSource Opened { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            internal Exception? Failure => Volatile.Read(ref _result) as Exception;
            internal void Fail(Exception error)
            {
                if (Interlocked.CompareExchange(ref _result, error, null) is null) Opened.TrySetException(error);
            }
            internal void Accept()
            {
                if (Interlocked.CompareExchange(ref _result, Accepted, null) is Exception failure) throw failure;
            }
        }

        private Windows.Media.Playback.MediaPlayer CreateNetworkPlayer(IAudioOutputRoute route, Windows.Media.Playback.MediaPlaybackItem item,
            RouteDecoderOpening? routing = null)
        {
            var player = new Windows.Media.Playback.MediaPlayer { AutoPlay = false, Volume = 0 };
            try
            {
                player.CommandManager.IsEnabled = false;
                player.PlaybackSession.PlaybackRate = _owner.SpeedRatio;
                route.Apply(player);
                player.PlaybackSession.PlaybackStateChanged += (_, _) => Post(() =>
                {
                    if (ReferenceEquals(player, _network)) _owner.OnPlaybackActivityChanged(this);
                });
                player.MediaOpened += (_, _) => Post(() =>
                {
                    var current = ReferenceEquals(player, _network);
                    if (!current && (routing is null || routing.Opened.Task.IsCompleted)) return;
                    if (item.AudioTracks.Count == 0)
                    {
                        var error = MissingAudioTrack();
                        routing?.Fail(error);
                        if (current) _owner.OnMediaFailed(this, error);
                        return;
                    }
                    routing?.Opened.TrySetResult();
                    if (current) { CompleteOpen(); _owner.ApplyGains(); }
                });
                player.MediaEnded += (_, _) => Post(() => { if (ReferenceEquals(player, _network)) _owner.OnMediaEnded(this); });
                player.MediaFailed += (_, args) =>
                {
                    var error = args.ExtendedErrorCode ?? new MusicException("Windows не удалось открыть поток.");
                    routing?.Fail(error);
                    Post(() => { if (ReferenceEquals(player, _network)) _owner.OnMediaFailed(this, error); });
                };
                return player;
            }
            catch { player.Dispose(); throw; }
        }
        internal Task RouteAsync(IAudioOutputRoute route)
        {
            var routing = RouteCoreAsync(route);
            if (_localAudio is not null && !routing.IsCompleted)
            {
                _routingReads.RemoveAll(task => task.IsCompleted);
                _routingReads.Add(routing);
            }
            return routing;
        }
        private async Task RouteCoreAsync(IAudioOutputRoute route)
        {
            var revision = ++_routeRevision;
            if (_closed) return;
            if (_network is not null) { route.Apply(_network); return; }
            if (route.Id.Length == 0) return; // WPF already follows the Windows default.
            // Keep the existing WPF decoder alive until the replacement has opened.
            // Its source still belongs to this slot, including a buffered temp file.
            try { await Opened.Task.WaitAsync(TimeSpan.FromSeconds(30), NormalizationCancellation.Token); }
            catch (OperationCanceledException) when (_closed) { return; }
            if (_closed || revision != _routeRevision) return;
            var opening = new RouteDecoderOpening();
            Windows.Media.Playback.MediaPlayer? replacement = null;
            Windows.Media.Core.MediaSource? source = null;
            try
            {
                source = Windows.Media.Core.MediaSource.CreateFromUri(OpenedSource!);
                var item = new Windows.Media.Playback.MediaPlaybackItem(source);
                replacement = CreateNetworkPlayer(route, item, opening);
                replacement.Source = item;
                await opening.Opened.Task.WaitAsync(TimeSpan.FromSeconds(30), NormalizationCancellation.Token);
                if (_closed || revision != _routeRevision) return;
                _owner.RouteDecoderOpenedForChecks?.Invoke(opening.Fail);
                if (_owner.BeforeRouteDecoderInstallForChecks is { } before)
                    await before(NormalizationCancellation.Token);
                if (_closed || revision != _routeRevision) return;
                if (opening.Failure is { } failure) throw failure;
                replacement.PlaybackSession.PlaybackRate = _owner.SpeedRatio;
                Player.Pause();
                var position = Position; // Observe an acknowledged seek before choosing the handoff position.
                replacement.PlaybackSession.Position = _pendingWpfSeek ?? position;
                // Native MediaFailed can win until the candidate owns the handoff.
                opening.Accept();
                DetachWpfEvents();
                _network = replacement; _networkSource = source;
                _pendingWpfSeek = null;
                replacement = null; source = null;
                Player.Close();
                _owner.ApplyGains();
                _owner.OnPlaybackActivityChanged(this);
                if (_owner.IsPlaying && !_owner._routeMixHeld
                    && (ReferenceEquals(_owner._current, this) || ReferenceEquals(_owner._fading, this))) _network.Play();
            }
            catch (OperationCanceledException) when (_closed) { }
            finally
            {
                opening.Opened.TrySetCanceled();
                try { replacement?.Dispose(); }
                finally { source?.Dispose(); }
            }
        }
        private void Post(Action action)
        {
            if (!_closed && !_dispatcher.HasShutdownStarted)
                _dispatcher.BeginInvoke(new Action(() => { if (!_closed) action(); }));
        }
        private bool _closed;
        internal bool Closed => _closed;
        internal Task? NormalizationTask;
        internal CancellationTokenSource NormalizationCancellation { get; } = new();
        internal double MeasuredGain = 1;
        private double _gainFrom = 1, _gainTarget = 1;
        private long _gainStarted;
        internal bool GainChanging => Math.Abs(_gainFrom - _gainTarget) > .00001 && Stopwatch.GetElapsedTime(_gainStarted).TotalMilliseconds < 800;
        internal double Gain => _gainFrom + (_gainTarget - _gainFrom) * Math.Clamp(Stopwatch.GetElapsedTime(_gainStarted).TotalMilliseconds / 800, 0, 1);
        internal void SetGain(double gain) { _gainFrom = Gain; _gainTarget = Math.Clamp(gain, 0, 1); _gainStarted = Stopwatch.GetTimestamp(); }
        public MediaPlayer Player { get; }
        public Uri RequestedSource { get; }
        public TaskCompletionSource Opened { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Ready { get; private set; }
        public Slot(NativePlayer owner, Uri source, PlaybackInput? input, LocalAudioLease? localAudio, double volume, double speed)
        {
            _owner = owner; RequestedSource = source; Input = input; _localAudio = localAudio;
            Player = new MediaPlayer { Volume = volume, SpeedRatio = speed };
            Player.MediaOpened += MediaOpened; Player.MediaFailed += MediaFailed; Player.MediaEnded += MediaEnded;
            Player.BufferingStarted += BufferingStarted; Player.BufferingEnded += BufferingEnded;
        }
        private void BufferingStarted(object? sender, EventArgs e) => SetWpfBuffering(true);
        private void BufferingEnded(object? sender, EventArgs e) => SetWpfBuffering(false);
        private void SetWpfBuffering(bool buffering)
        {
            if (_closed || _network is not null) return;
            _wpfBuffering = buffering;
            _owner.OnPlaybackActivityChanged(this);
        }
        private void MediaOpened(object? sender, EventArgs e)
        {
            if (_closed || _network is not null) return;
            if (!Player.HasAudio) { _owner.OnMediaFailed(this, MissingAudioTrack()); return; }
            CompleteOpen();
        }
        private void CompleteOpen()
        {
            if (_closed || Ready) return;
            Ready = true;
            _owner.BeginNormalization(this);
            Opened.TrySetResult();
        }
        private static MusicException MissingAudioTrack() => new("В источнике отсутствует звуковая дорожка.");
        private void MediaFailed(object? sender, ExceptionEventArgs e) { if (!_closed && _network is null) _owner.OnMediaFailed(this, e.ErrorException); }
        private void MediaEnded(object? sender, EventArgs e) { if (!_closed && _network is null) _owner.OnMediaEnded(this); }
        private void DetachWpfEvents()
        {
            Player.MediaOpened -= MediaOpened; Player.MediaFailed -= MediaFailed; Player.MediaEnded -= MediaEnded;
            Player.BufferingStarted -= BufferingStarted; Player.BufferingEnded -= BufferingEnded;
        }
        public void Close()
        {
            if (_closed) return;
            _closed = true;
            ++_routeRevision; OpenedSource = null;
            NormalizationCancellation.Cancel();
            if (NormalizationTask is { } task)
                _ = task.ContinueWith(_ => NormalizationCancellation.Dispose(), TaskScheduler.Default);
            else NormalizationCancellation.Dispose();
            DetachWpfEvents();
            Opened.TrySetCanceled();
            try
            {
                try { Player.Close(); }
                finally
                {
                    try { _network?.Dispose(); }
                    finally { _networkSource?.Dispose(); }
                }
            }
            finally
            {
                if (_localAudio is not null) _owner.ReleaseLocalSource(_localAudio, _routingReads, NormalizationTask);
                Input?.Dispose();
            }
            _owner.AfterDecoderCloseForChecks?.Invoke(RequestedSource);
        }
    }
}
