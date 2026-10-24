using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace YMusic.Native;

// Own on the WPF dispatcher, like MediaPlayer itself. Each slot owns only the
// temporary file it downloaded; opening a user/offline URI never transfers ownership.
internal sealed class NativePlayer : IDisposable
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
    public bool HasSource => _current?.Player.Source is not null;
    public bool HasPrepared => _prepared?.Ready == true;
    public string? SourceLocalPath => LocalPath(_current);
    public string? PreparedLocalPath => LocalPath(_prepared);
    public string? FadingLocalPath => LocalPath(_fading);
    public double Volume { get => _volume; set => SetVolume(value); }
    public double SpeedRatio { get; private set; } = 1;
    public TimeSpan Position => _current?.Player.Position ?? TimeSpan.Zero;
    public TimeSpan Duration => _current?.Player.NaturalDuration.HasTimeSpan == true ? _current.Player.NaturalDuration.TimeSpan : TimeSpan.Zero;
    public event EventHandler? Ended;
    public event EventHandler? Failed;
    // true: the overlap ended naturally; false: stop, seek, replacement or failure.
    // Once per actual overlap, after its outgoing player and owned file are released.
    public event Action<bool>? TransitionFinished;

    public Task OpenAsync(Uri uri, CancellationToken cancellationToken, IProgress<AudioProgress>? progress = null)
        => OpenCoreAsync(uri, null, cancellationToken, progress);
    public Task OpenAsync(AudioDescriptor audio, CancellationToken cancellationToken, IProgress<AudioProgress>? progress = null)
        => OpenCoreAsync(audio.Uri, audio, cancellationToken, progress);
    private async Task OpenCoreAsync(Uri uri, AudioDescriptor? audio, CancellationToken cancellationToken, IProgress<AudioProgress>? progress)
    {
        ValidateSource(uri); ObjectDisposedException.ThrowIf(_disposed, this);
        Stop();
        var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _opening = operation;
        Slot? slot = null; string? temporary = null;
        try
        {
            var local = uri;
            if (!uri.IsFile)
            {
                temporary = audio is null ? await _buffer.DownloadAsync(uri, operation.Token, progress) : await _buffer.DownloadAsync(audio, operation.Token, progress);
                local = new Uri(temporary);
            }
            operation.Token.ThrowIfCancellationRequested();
            if (!ReferenceEquals(_opening, operation)) throw new OperationCanceledException(operation.Token);
            slot = new Slot(this, uri, temporary, Volume, SpeedRatio); temporary = null;
            _current = slot; LastNativeError = null;
            slot.Player.Open(local);
            await slot.Opened.Task.WaitAsync(TimeSpan.FromSeconds(30), operation.Token);
            operation.Token.ThrowIfCancellationRequested();
            if (!ReferenceEquals(slot, _current)) throw new OperationCanceledException(operation.Token);
            slot.Player.Play(); IsPlaying = true;
        }
        catch
        {
            if (ReferenceEquals(_opening, operation)) Stop();
            else slot?.Close(); // A stale completion never stops a newer source.
            throw;
        }
        finally
        {
            AudioBuffer.Delete(temporary);
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
        Slot? slot = null; string? temporary = null;
        try
        {
            var local = uri;
            if (!uri.IsFile)
            {
                temporary = audio is null ? await _buffer.DownloadAsync(uri, operation.Token) : await _buffer.DownloadAsync(audio, operation.Token);
                local = new Uri(temporary);
            }
            operation.Token.ThrowIfCancellationRequested();
            if (!ReferenceEquals(_preparing, operation)) throw new OperationCanceledException(operation.Token);
            slot = new Slot(this, uri, temporary, 0, SpeedRatio); temporary = null;
            _prepared = slot;
            slot.Player.Open(local); // Decode/open only. Never Play before promotion.
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
            AudioBuffer.Delete(temporary);
            if (ReferenceEquals(_preparing, operation)) _preparing = null;
            operation.Dispose();
        }
    }

    public bool TryPlayPrepared(Uri source, TimeSpan crossfade)
    {
        ValidateSource(source); ObjectDisposedException.ThrowIf(_disposed, this);
        if (crossfade < TimeSpan.Zero || crossfade > TimeSpan.FromSeconds(8)) throw new ArgumentOutOfRangeException(nameof(crossfade));
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
            _fadeClock.Restart(); _fadeTimer.Start(); next.Player.Volume = 0;
        }
        else { previous?.Close(); next.Player.Volume = Volume; }
        next.Player.SpeedRatio = SpeedRatio;
        next.Player.Play(); IsPlaying = true;
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
            _current.Player.Pause(); _fading?.Player.Pause();
            _fadeElapsed += _fadeClock.Elapsed; _fadeClock.Reset(); _fadeTimer.Stop();
            IsPlaying = false;
        }
        else
        {
            _current.Player.Play(); _fading?.Player.Play();
            if (_fading is not null) { _fadeClock.Restart(); _fadeTimer.Start(); }
            IsPlaying = true;
        }
    }
    public void Seek(TimeSpan position)
    {
        FinishTransition();
        if (_current is not null) _current.Player.Position = TimeSpan.FromSeconds(Math.Clamp(position.TotalSeconds, 0, Duration.TotalSeconds));
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
        if (_current is not null) _current.Player.SpeedRatio = speed;
        if (_prepared is not null) _prepared.Player.SpeedRatio = speed;
        if (_fading is not null) _fading.Player.SpeedRatio = speed;
    }
    private double FadeFraction => _fading is null ? 1 : Math.Clamp((_fadeElapsed + _fadeClock.Elapsed).TotalSeconds / _fadeDuration.TotalSeconds, 0, 1);
    private void ApplyGains()
    {
        var fraction = FadeFraction;
        if (_current is not null) _current.Player.Volume = Volume * fraction;
        if (_fading is not null) _fading.Player.Volume = Volume * (1 - fraction);
        if (_prepared is not null) _prepared.Player.Volume = 0;
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
        if (_current is not null) _current.Player.Volume = Volume;
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
        try { CompleteTransition(natural: false); }
        finally { current?.Close(); }
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { Stop(); } finally { _buffer.Dispose(); }
    }
    private static string? LocalPath(Slot? slot) => slot?.Player.Source is { IsFile: true } source ? source.LocalPath : null;
    private static void ValidateSource(Uri source)
    { ArgumentNullException.ThrowIfNull(source); if (!source.IsAbsoluteUri) throw new ArgumentException("Требуется абсолютный адрес аудио.", nameof(source)); }

    private sealed class Slot
    {
        private readonly NativePlayer _owner;
        private string? _ownedFile;
        private bool _closed;
        public MediaPlayer Player { get; }
        public Uri RequestedSource { get; }
        public TaskCompletionSource Opened { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Ready { get; private set; }
        public Slot(NativePlayer owner, Uri source, string? ownedFile, double volume, double speed)
        {
            _owner = owner; RequestedSource = source; _ownedFile = ownedFile;
            Player = new MediaPlayer { Volume = volume, SpeedRatio = speed };
            Player.MediaOpened += MediaOpened; Player.MediaFailed += MediaFailed; Player.MediaEnded += MediaEnded;
        }
        private void MediaOpened(object? sender, EventArgs e) { if (!_closed) { Ready = true; Opened.TrySetResult(); } }
        private void MediaFailed(object? sender, ExceptionEventArgs e) { if (!_closed) _owner.OnMediaFailed(this, e.ErrorException); }
        private void MediaEnded(object? sender, EventArgs e) { if (!_closed) _owner.OnMediaEnded(this); }
        public void Close()
        {
            if (_closed) return;
            _closed = true;
            Player.MediaOpened -= MediaOpened; Player.MediaFailed -= MediaFailed; Player.MediaEnded -= MediaEnded;
            Opened.TrySetCanceled();
            try { Player.Close(); }
            finally { AudioBuffer.Delete(_ownedFile); _ownedFile = null; }
        }
    }
}
