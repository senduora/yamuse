namespace YMusic.Native;

internal enum SleepTimerMode { Off, Minutes15, Minutes30, Minutes60, AfterCurrentTrack }
internal readonly record struct SleepTimerTick(bool Completed, double Gain, TimeSpan? Remaining);

// Pure state: the window supplies elapsed monotonic time and the current track's
// remaining wall time. No profile, device, network or Windows sleep operations.
internal sealed class SleepTimerState
{
    internal SleepTimerMode Mode { get; private set; }
    internal bool Active => Mode != SleepTimerMode.Off;
    internal bool AfterTrack => Mode == SleepTimerMode.AfterCurrentTrack;
    internal static readonly TimeSpan FadeDuration = TimeSpan.FromSeconds(5);

    internal void Start(SleepTimerMode mode)
    {
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        Mode = mode;
    }
    internal void Cancel() => Mode = SleepTimerMode.Off;
    internal SleepTimerTick Tick(TimeSpan elapsed, TimeSpan? trackRemaining)
    {
        if (!Active) return new(false, 1, null);
        var remaining = Mode switch
        {
            SleepTimerMode.Minutes15 => TimeSpan.FromMinutes(15) - elapsed,
            SleepTimerMode.Minutes30 => TimeSpan.FromMinutes(30) - elapsed,
            SleepTimerMode.Minutes60 => TimeSpan.FromMinutes(60) - elapsed,
            _ => trackRemaining
        };
        if (remaining is null) return new(false, 1, null);
        return new(remaining <= TimeSpan.Zero, Math.Clamp(remaining.Value.TotalSeconds / FadeDuration.TotalSeconds, 0, 1),
            remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero);
    }
}
