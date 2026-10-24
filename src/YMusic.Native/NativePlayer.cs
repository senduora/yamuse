using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace YMusic.Native;

// Own on the WPF dispatcher, like MediaPlayer itself. Each slot owns only the
// temporary file it downloaded; opening a user/offline URI never transfers ownership.
internal sealed partial class NativePlayer : IDisposable
{
    private Slot? _current, _prepared, _fading;
    private readonly AudioBuffer _buffer;
    private CancellationTokenSource? _opening, _preparing;
    private readonly DispatcherTimer _fadeTimer;
    private readonly Stopwatch _fadeClock = new();
    private TimeSpan _fadeElapsed, _fadeDuration;
    private double _volume = 0.5;
    private bool _disposed;

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
    public string? SourceLocalPath => LocalPath(_current);
    public string? PreparedLocalPath => LocalPath(_prepared);
    public string? FadingLocalPath => LocalPath(_fading);
    public double Volume { get => _volume; set => SetVolume(value); }
    public double SpeedRatio { get; private set; } = 1;
    public double CrossfadeSeconds { get; private set; } = 3;
    public TimeSpan Position => _current?.Position ?? TimeSpan.Zero;
    public TimeSpan Duration => _current?.Duration ?? TimeSpan.Zero;
    public event EventHandler? Ended;
    public event EventHandler? Failed;
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
    private async Task OpenCoreAsync(Uri uri, AudioDescriptor? audio, CancellationToken cancellationToken, IProgress<AudioProgress>? progress, bool play = true)
    {
        ValidateSource(uri); ObjectDisposedException.ThrowIf(_disposed, this);
        Stop();
        var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _opening = operation;
        Slot? slot = null; PlaybackInput? input = null;
        try
        {
            var local = uri;
            if (!uri.IsFile)
            {
                input = await _buffer.OpenPlaybackAsync(audio ?? new AudioDescriptor(uri, "mp3", 0), operation.Token, progress);
                local = input.Uri;
            }
            operation.Token.ThrowIfCancellationRequested();
            if (!ReferenceEquals(_opening, operation)) throw new OperationCanceledException(operation.Token);
            slot = new Slot(this, uri, input, Volume, SpeedRatio); input = null;
            _current = slot; LastNativeError = null;
            slot.Open(local);
            await slot.Opened.Task.WaitAsync(TimeSpan.FromSeconds(30), operation.Token);
            operation.Token.ThrowIfCancellationRequested();
            if (!ReferenceEquals(slot, _current)) throw new OperationCanceledException(operation.Token);
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
            if (ReferenceEquals(_opening, operation)) _opening = null;
            operation.Dispose();
        }
    }

    public Task PrepareNextAsync(Uri uri, CancellationToken cancellationToken)
        => PrepareCoreAsync(uri, null, cancellationToken);
    public Task PrepareNextAsync(AudioDescriptor audio, CancellationToken cancellationToken)
        => PrepareCoreAsync(audio.Uri, audio, cancellationToken);
    private async Task PrepareCoreAsync(Uri uri, AudioDescriptor? audio, CancellationToken cancellationToken)
    {
        ValidateSource(uri); ObjectDisposedException.ThrowIf(_disposed, this);
        DiscardPrepared();
        var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _preparing = operation;
        Slot? slot = null; PlaybackInput? input = null;
        try
        {
            var local = uri;
            if (!uri.IsFile)
            {
                input = await _buffer.OpenPlaybackAsync(audio ?? new AudioDescriptor(uri, "mp3", 0), operation.Token);
                local = input.Uri;
            }
            operation.Token.ThrowIfCancellationRequested();
            if (!ReferenceEquals(_preparing, operation)) throw new OperationCanceledException(operation.Token);
            slot = new Slot(this, uri, input, 0, SpeedRatio); input = null;
            _prepared = slot;
            slot.Open(local); // Decode/open only. Never Play before promotion.
            await slot.Opened.Task.WaitAsync(TimeSpan.FromSeconds(30), operation.Token);
            // MediaOpened can trigger promotion before this continuation runs.
            // The slot then belongs to playback; its old preload token has no authority.
            if (ReferenceEquals(slot, _current) || ReferenceEquals(slot, _fading)) return;
            operation.Token.ThrowIfCancellationRequested();
            if (!ReferenceEquals(slot, _prepared)) throw new OperationCanceledException(operation.Token);
        }
        catch
        {
            if (slot is not null && (ReferenceEquals(slot, _current) || ReferenceEquals(slot, _fading))) return;
            if (ReferenceEquals(_preparing, operation)) DiscardPrepared();
            else slot?.Close();
            throw;
        }
        finally
        {
            input?.Dispose();
            if (ReferenceEquals(_preparing, operation)) _preparing = null;
            operation.Dispose();
        }
    }

    public bool TryPlayPrepared(Uri source, TimeSpan crossfade)
    {
        ValidateSource(source); ObjectDisposedException.ThrowIf(_disposed, this);
        if (crossfade < TimeSpan.Zero || crossfade > TimeSpan.FromSeconds(10)) throw new ArgumentOutOfRangeException(nameof(crossfade));
        if (_prepared is not { Ready: true } next || !string.Equals(next.RequestedSource.AbsoluteUri, source.AbsoluteUri, StringComparison.Ordinal)) return false;
        FinishTransition(); // A second promotion can never leave three players playing.
        var opening = _opening; _opening = null; opening?.Cancel();
        _prepared = null; _preparing = null;
        var previous = _current;
        var overlap = IsPlaying && previous?.Ready == true && crossfade > TimeSpan.Zero;
        _current = next; LastNativeError = null;
        if (overlap)
        {
            _fading = previous; _fadeDuration = crossfade; _fadeElapsed = TimeSpan.Zero;
            _fadeClock.Restart(); _fadeTimer.Start(); next.Volume = 0;
        }
        else { previous?.Close(); next.Volume = Volume * next.Gain; }
        next.Speed = SpeedRatio;
        next.Play(); IsPlaying = true;
        StartGainTimer(); // A prepared slot may still be ramping when it becomes audible.
        return true;
    }

    public void DiscardPrepared()
    {
        var preparing = _preparing; _preparing = null; preparing?.Cancel();
        var prepared = _prepared; _prepared = null; prepared?.Close();
    }
    public void FinishTransition() => CompleteTransition(natural: false);

    public void Toggle()
    {
        if (_current?.Ready != true) return;
        if (IsPlaying)
        {
            _current.Pause(); _fading?.Pause();
            _fadeElapsed += _fadeClock.Elapsed; _fadeClock.Reset(); _fadeTimer.Stop();
            IsPlaying = false;
        }
        else
        {
            _current.Play(); _fading?.Play();
            if (_fading is not null) { _fadeClock.Restart(); _fadeTimer.Start(); }
            IsPlaying = true;
        }
    }
    public void Seek(TimeSpan position)
    {
        FinishTransition();
        if (_current is not null) _current.Position = TimeSpan.FromSeconds(Math.Clamp(position.TotalSeconds, 0, Duration.TotalSeconds));
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
        if (_current is not null) _current.Volume = Volume * _current.Gain * fraction;
        if (_fading is not null) _fading.Volume = Volume * _fading.Gain * (1 - fraction);
        if (_prepared is not null) _prepared.Volume = 0;
    }
    private void UpdateFade()
    {
        ApplyGains();
        if (_fading is not null && FadeFraction >= 1) CompleteTransition(natural: true);
    }
    private void CompleteTransition(bool natural)
    {
        _fadeTimer.Stop(); _fadeClock.Reset(); _fadeElapsed = TimeSpan.Zero;
        var fading = _fading; _fading = null;
        fading?.Close();
        if (_current is not null) _current.Volume = Volume * _current.Gain;
        if (fading is not null) TransitionFinished?.Invoke(natural);
    }

    private void OnMediaFailed(Slot slot, Exception error)
    {
        if (!slot.Ready) slot.Opened.TrySetException(new MusicException("Windows не удалось открыть аудиофайл. Попробуйте другой трек."));
        if (ReferenceEquals(slot, _current))
        {
            LastNativeError = error.GetType().Name + ":0x" + error.HResult.ToString("X8");
            IsPlaying = false; FinishTransition();
            if (slot.Ready) Failed?.Invoke(this, EventArgs.Empty);
        }
        else if (ReferenceEquals(slot, _prepared)) { _prepared = null; slot.Close(); }
        else if (ReferenceEquals(slot, _fading)) CompleteTransition(natural: false);
    }
    private void OnMediaEnded(Slot slot)
    {
        if (ReferenceEquals(slot, _fading)) { CompleteTransition(natural: true); return; }
        if (!ReferenceEquals(slot, _current)) return;
        IsPlaying = false; FinishTransition(); Ended?.Invoke(this, EventArgs.Empty);
    }
    public void Stop()
    {
        var opening = _opening; _opening = null; opening?.Cancel();
        DiscardPrepared();
        var current = _current; _current = null; IsPlaying = false;
        _gainTimer?.Stop();
        try { CompleteTransition(natural: false); }
        finally { current?.Close(); }
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { Stop(); } finally { _buffer.Dispose(); }
    }
    private static string? LocalPath(Slot? slot) => slot?.Input?.Path ?? (slot?.Player.Source is { IsFile: true } source ? source.LocalPath : null);
    private static void ValidateSource(Uri source)
    { ArgumentNullException.ThrowIfNull(source); if (!source.IsAbsoluteUri) throw new ArgumentException("Требуется абсолютный адрес аудио.", nameof(source)); }

    private sealed class Slot
    {
        private readonly NativePlayer _owner;
        internal PlaybackInput? Input { get; }
        private Windows.Media.Playback.MediaPlayer? _network;
        private Windows.Media.Core.MediaSource? _networkSource;
        private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
        internal bool HasSource => _networkSource is not null || Player.Source is not null;
        internal TimeSpan Position { get => _network?.PlaybackSession.Position ?? Player.Position; set { if (_network is not null) _network.PlaybackSession.Position = value; else Player.Position = value; } }
        internal TimeSpan Duration => _network?.PlaybackSession.NaturalDuration ?? (Player.NaturalDuration.HasTimeSpan ? Player.NaturalDuration.TimeSpan : TimeSpan.Zero);
        internal double Volume { set { if (_network is not null) _network.Volume = value; else Player.Volume = value; } }
        internal double Speed { set { if (_network is not null) _network.PlaybackSession.PlaybackRate = value; else Player.SpeedRatio = value; } }
        internal void Play() { if (_network is not null) _network.Play(); else Player.Play(); }
        internal void Pause() { if (_network is not null) _network.Pause(); else Player.Pause(); }
        internal void Open(Uri source)
        {
            if (Input?.Progressive != true) { Player.Open(source); return; }
            _network = new Windows.Media.Playback.MediaPlayer { AutoPlay = false, Volume = Player.Volume };
            _network.CommandManager.IsEnabled = false;
            _network.PlaybackSession.PlaybackRate = Player.SpeedRatio;
            _network.MediaOpened += (_, _) => Post(() => MediaOpened(this, EventArgs.Empty));
            _network.MediaEnded += (_, _) => Post(() => _owner.OnMediaEnded(this));
            _network.MediaFailed += (_, args) => Post(() => _owner.OnMediaFailed(this, args.ExtendedErrorCode ?? new MusicException("Windows не удалось открыть поток.")));
            _networkSource = Windows.Media.Core.MediaSource.CreateFromUri(source);
            _network.Source = _networkSource;
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
        public Slot(NativePlayer owner, Uri source, PlaybackInput? input, double volume, double speed)
        {
            _owner = owner; RequestedSource = source; Input = input;
            Player = new MediaPlayer { Volume = volume, SpeedRatio = speed };
            Player.MediaOpened += MediaOpened; Player.MediaFailed += MediaFailed; Player.MediaEnded += MediaEnded;
        }
        private void MediaOpened(object? sender, EventArgs e) { if (!_closed) { Ready = true; _owner.BeginNormalization(this); Opened.TrySetResult(); } }
        private void MediaFailed(object? sender, ExceptionEventArgs e) { if (!_closed) _owner.OnMediaFailed(this, e.ErrorException); }
        private void MediaEnded(object? sender, EventArgs e) { if (!_closed) _owner.OnMediaEnded(this); }
        public void Close()
        {
            if (_closed) return;
            _closed = true;
            NormalizationCancellation.Cancel();
            if (NormalizationTask is { } task)
                _ = task.ContinueWith(_ => NormalizationCancellation.Dispose(), TaskScheduler.Default);
            else NormalizationCancellation.Dispose();
            Player.MediaOpened -= MediaOpened; Player.MediaFailed -= MediaFailed; Player.MediaEnded -= MediaEnded;
            Opened.TrySetCanceled();
            try { Player.Close(); _network?.Dispose(); _networkSource?.Dispose(); }
            finally { Input?.Dispose(); }
        }
    }
}
