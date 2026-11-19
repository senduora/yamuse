using System.Runtime.InteropServices;

namespace YMusic.Native;

internal sealed partial class NativePlayer
{
    private IAudioOutputRoute _outputRoute = SystemAudioOutputRoute.Instance;
    private int _outputRevision;
    private bool _routingOutput;
    private bool _routeMixHeld;
    private bool _routePauseUnconfirmed;
    private (Slot Slot, TimeSpan Position)? _pausedOutputClock;
    internal Action? BeforeRouteMixResumeForChecks { get; set; }
    private double _sleepGain = 1;
    private double OutputMixGain => _routeMixHeld ? 0 : _sleepGain;
    internal string OutputDeviceId => _outputRoute.Id;
    internal bool OutputRouteChanging => _routingOutput || _routeMixHeld;
    internal bool OutputRouteIsValid { get; private set; } = true;
    internal double SleepGain => _sleepGain;
    internal void SetSleepGain(double gain)
    {
        if (!double.IsFinite(gain)) throw new ArgumentOutOfRangeException(nameof(gain));
        _sleepGain = Math.Clamp(gain, 0, 1); ApplyGains();
    }
    internal void Pause()
    {
        if (IsPlaying) Toggle();
    }
    internal async Task SetOutputDeviceAsync(IAudioOutputRoute route, Func<Task>? confirmRoute = null)
    {
        ArgumentNullException.ThrowIfNull(route);
        ObjectDisposedException.ThrowIf(_disposed, this);
        var previous = _outputRoute;
        var revision = ++_outputRevision;
        _outputRoute = route; // New or still-opening slots inherit the same endpoint.
        _routingOutput = true;
        try
        {
            if (_routePauseUnconfirmed)
            {
                if (!PauseOutputSlots()) throw new MusicException("Не удалось приостановить смену аудиовыхода.");
                _routePauseUnconfirmed = false;
            }
            if (_fading is not null) HoldRouteMix();
            await RouteSlotsAsync(route);
            if (!_disposed && revision == _outputRevision && confirmRoute is not null) await confirmRoute();
            if (!_disposed && revision == _outputRevision)
            {
                ReleaseRouteMix();
                OutputRouteIsValid = true;
            }
        }
        catch when (!_disposed && revision == _outputRevision)
        {
            _outputRoute = previous;
            OutputRouteIsValid = false;
            _routePauseUnconfirmed = !PauseOutputSlots();
            if (_routePauseUnconfirmed) MuteUnconfirmedOutput();
            try
            {
                await RouteSlotsAsync(previous);
                if (!_disposed && revision == _outputRevision && !_routePauseUnconfirmed)
                {
                    ReleaseRouteMix();
                    OutputRouteIsValid = true;
                }
            }
            catch
            {
                // A slot may still be on the requested route. Keep playback blocked until a full application succeeds.
            }
            throw new MusicException("Не удалось переключить аудиоустройство. Воспроизведение на паузе.");
        }
        finally
        {
            if (revision == _outputRevision) _routingOutput = false;
        }
    }

    internal void PauseAfterOutputFailure()
    {
        if (IsPlaying) PauseOutputTransport();
    }

    private bool PauseOutputTransport()
    {
        if (PauseOutputSlots()) return true;
        _routePauseUnconfirmed = true;
        OutputRouteIsValid = false;
        MuteUnconfirmedOutput();
        return false;
    }

    private bool PauseOutputSlots()
    {
        var current = _current;
        var position = _pausedOutputClock is { } clock && ReferenceEquals(clock.Slot, current)
            ? clock.Position : current?.PositionAfterFailure() ?? TimeSpan.Zero;
        IsPlaying = false;
        PauseFadeClock();
        var paused = true;
        foreach (var slot in new[] { _current, _fading }.OfType<Slot>().Where(slot => slot.Ready))
        {
            try { slot.Pause(); }
            catch (Exception error) when (error is COMException or InvalidOperationException)
            {
                paused = false;
                RecordNativeError(error);
            }
        }
        if (!paused && current is { Ready: true } && ReferenceEquals(current, _current))
            _pausedOutputClock = (current, position);
        return paused;
    }

    private void MuteUnconfirmedOutput()
    {
        _routeMixHeld = true;
        var muted = true;
        foreach (var slot in new[] { _current, _fading }.OfType<Slot>())
        {
            try { slot.Volume = 0; }
            catch (Exception error) when (error is COMException or InvalidOperationException)
            {
                muted = false;
                RecordNativeError(error);
            }
        }
        if (muted) return;
        // A decoder that cannot be paused or silenced cannot remain attached.
        try { Stop(); }
        catch (Exception error) when (error is COMException or InvalidOperationException) { RecordNativeError(error); }
    }

    private void HoldRouteMix()
    {
        if (_routeMixHeld) return;
        _routeMixHeld = true;
        ApplyGains();
        // Keep the play intent while both clocks are suspended on their existing decoders.
        if (IsPlaying) PauseSlotsAndFade();
        PlaybackActivityChanged?.Invoke();
    }

    private void ReleaseRouteMix()
    {
        if (!_routeMixHeld) return;
        if (IsPlaying)
        {
            BeforeRouteMixResumeForChecks?.Invoke();
            PlaySlotsAndFade();
        }
        _routeMixHeld = false;
        ApplyGains();
        PlaybackActivityChanged?.Invoke();
    }

    private Task RouteSlotsAsync(IAudioOutputRoute route)
        => Task.WhenAll(new[] { _current, _prepared, _fading, _waveFallback }.OfType<Slot>().Distinct().Select(slot => slot.RouteAsync(route)));
}
