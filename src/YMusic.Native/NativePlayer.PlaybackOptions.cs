namespace YMusic.Native;

internal sealed partial class NativePlayer
{
    private IAudioOutputRoute _outputRoute = SystemAudioOutputRoute.Instance;
    private int _outputRevision;
    private double _sleepGain = 1;
    internal string OutputDeviceId => _outputRoute.Id;
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
    internal async Task SetOutputDeviceAsync(IAudioOutputRoute route)
    {
        ArgumentNullException.ThrowIfNull(route);
        ObjectDisposedException.ThrowIf(_disposed, this);
        var previous = _outputRoute;
        var revision = ++_outputRevision;
        _outputRoute = route; // New or still-opening slots inherit the same endpoint.
        try
        {
            await Task.WhenAll(new[] { _current, _prepared, _fading }.OfType<Slot>().Distinct().Select(s => s.RouteAsync(route)));
        }
        catch when (!_disposed && revision == _outputRevision)
        {
            Pause(); // A partial route must never leave a different slot audible.
            _outputRoute = previous;
            try { await Task.WhenAll(new[] { _current, _prepared, _fading }.OfType<Slot>().Distinct().Select(s => s.RouteAsync(previous))); }
            catch { /* Stay paused; the UI reports the failure and keeps the old preference. */ }
            throw new MusicException("Не удалось переключить аудиоустройство. Воспроизведение на паузе.");
        }
    }
}
