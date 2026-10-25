using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media.Animation;

namespace YMusic.Native;

// The decoder remains the authority. Between its existing 500-ms observations,
// WPF advances only the displayed playhead; no per-frame UI timer or native seek.
internal static class PlaybackTimeline
{
    private const double HorizonSeconds = .65;

    internal static void Update(Slider slider, double position, double duration, bool playing, double speed = 1)
    {
        duration = double.IsFinite(duration) ? Math.Max(0, duration) : 0;
        position = double.IsFinite(position) ? Math.Clamp(position, 0, duration) : 0;
        speed = double.IsFinite(speed) ? Math.Clamp(speed, .5, 2) : 1;
        var previous = slider.Value;
        slider.BeginAnimation(RangeBase.ValueProperty, null);
        slider.Maximum = Math.Max(1, duration);
        // SetCurrentValue is a temporary effective value: installing a new WPF
        // animation can discard it and reveal the previous track's local value.
        // Timelines have no Value binding, so retain the actual sample as base.
        slider.Value = position;
        if (!playing || duration <= 0 || !slider.IsVisible) return;
        // Small native clock quantization is absorbed without a backward twitch.
        // Discontinuous samples (track changes and seeks) snap to the true sample.
        var from = Math.Abs(previous - position) <= .22 ? Math.Max(previous, position) : position;
        var to = Math.Min(duration, position + HorizonSeconds * speed);
        if (to <= from) return;
        slider.BeginAnimation(RangeBase.ValueProperty, new DoubleAnimation(from, to, TimeSpan.FromSeconds((to - from) / speed))
        { FillBehavior = FillBehavior.HoldEnd }, HandoffBehavior.SnapshotAndReplace);
    }

    internal static void Stop(Slider slider, bool useBaseValue = false)
    {
        var value = useBaseValue ? (double)slider.GetAnimationBaseValue(RangeBase.ValueProperty) : slider.Value;
        slider.BeginAnimation(RangeBase.ValueProperty, null);
        value = Math.Clamp(value, slider.Minimum, slider.Maximum);
        // Volume uses the same gesture suspension and its expanded view is bound.
        if (BindingOperations.IsDataBound(slider, RangeBase.ValueProperty)) slider.SetCurrentValue(RangeBase.ValueProperty, value);
        else slider.Value = value;
    }
}
