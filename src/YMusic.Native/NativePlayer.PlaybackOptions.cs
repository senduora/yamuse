namespace YMusic.Native;

internal sealed partial class NativePlayer
{
    private IAudioOutputRoute _outputRoute = SystemAudioOutputRoute.Instance;
    private int _outputRevision;
    private double _sleepGain = 1;
    internal string OutputDeviceId => _outputRoute.Id;
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
        try
        {
            await RouteSlotsAsync(route);
            if (!_disposed && revision == _outputRevision && confirmRoute is not null) await confirmRoute();
            if (!_disposed && revision == _outputRevision) OutputRouteIsValid = true;
        }
        catch when (!_disposed && revision == _outputRevision)
        {
            _outputRoute = previous;
            OutputRouteIsValid = false;
            Pause(); // A partial route must never leave a different slot audible.
            try
            {
                await RouteSlotsAsync(previous);
                if (!_disposed && revision == _outputRevision) OutputRouteIsValid = true;
            }
            catch
            {
                // A slot may still be on the requested route. Keep playback blocked until a full application succeeds.
            }
            throw new MusicException("Не удалось переключить аудиоустройство. Воспроизведение на паузе.");
        }
    }

    private Task RouteSlotsAsync(IAudioOutputRoute route)
        => Task.WhenAll(new[] { _current, _prepared, _fading }.OfType<Slot>().Distinct().Select(slot => slot.RouteAsync(route)));
}
