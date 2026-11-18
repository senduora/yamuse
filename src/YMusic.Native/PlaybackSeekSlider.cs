using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;

namespace YMusic.Native;

public sealed class PlaybackSeekSlider : Slider
{
    internal event Action<double>? AutomationSeekRequested;

    protected override AutomationPeer OnCreateAutomationPeer() => new SeekPeer(this);
    private void SeekFromAutomation(double value) => AutomationSeekRequested?.Invoke(value);

    private sealed class SeekPeer(PlaybackSeekSlider slider) : SliderAutomationPeer(slider), IRangeValueProvider
    {
        void IRangeValueProvider.SetValue(double value)
        {
            if (!IsEnabled()) throw new ElementNotEnabledException();
            if (!double.IsFinite(value) || value < slider.Minimum || value > slider.Maximum)
                throw new ArgumentOutOfRangeException(nameof(value));
            slider.SeekFromAutomation(value);
        }
    }
}
