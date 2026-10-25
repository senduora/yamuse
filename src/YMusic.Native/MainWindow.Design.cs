using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace YMusic.Native;

public partial class MainWindow
{
    private double? _pendingSeek;
    private readonly System.Diagnostics.Stopwatch _seekClock = new();
    private bool _committingSeek;
    private bool _seekKeyboardGesture, _seekIgnoreKeyUp, _expandedSeekKeyboardGesture, _expandedSeekIgnoreKeyUp;
    private SliderGesture? _seekGesture, _volumeGesture, _expandedSeekGesture, _expandedVolumeGesture;
    private readonly System.Windows.Threading.DispatcherTimer _volumeSaveTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private void InitializeDesign()
    {
        InitializeDynamicAccent();
        InitializeAmbientBackdrops();
        TracksList.Loaded += (_, _) => FitTrackColumns();
        TracksList.SizeChanged += (_, _) => FitTrackColumns();
        SeekSlider.ValueChanged += (_, args) =>
        {
            if (_seekDragging) PositionText.Text = FormatTime(TimeSpan.FromSeconds(args.NewValue));
        };
        _seekGesture = new SliderGesture(SeekSlider, () =>
        { _seekDragging = true; _seekIgnoreKeyUp = false; _seekKeyboardGesture = _seekGesture?.IsKeyboardActive == true; }, FinishSeek, registerPointer: false);
        _volumeGesture = new SliderGesture(VolumeSlider, () => { }, _ => FlushVolumePreferences());
        _volumeSaveTimer.Tick += (_, _) => FlushVolumePreferences();
        Deactivated += (_, _) => { _seekGesture.Cancel(); _expandedSeekGesture?.Cancel(); };
        // Retain the original event routes used by keyboard and accessibility.
        SeekSlider.AddHandler(PreviewMouseLeftButtonDownEvent, new MouseButtonEventHandler(Seek_Start), true);
        SeekSlider.AddHandler(PreviewMouseLeftButtonUpEvent, new MouseButtonEventHandler(Seek_End), true);
        SeekSlider.AddHandler(Mouse.MouseMoveEvent, new MouseEventHandler((_, args) =>
            UpdateSeekHover(args.GetPosition(SeekSlider).X)), true);
        SeekSlider.MouseLeave += (_, _) => SeekHoverOverlay.Visibility = Visibility.Collapsed;
        SeekSlider.IsEnabledChanged += (_, _) => { if (!SeekSlider.IsEnabled) SeekHoverOverlay.Visibility = Visibility.Collapsed; };
    }

    private void UpdateSeekHover(double pointerX)
    {
        if (!SeekSlider.IsEnabled || SeekSlider.ActualWidth <= 16 || !double.IsFinite(pointerX))
        { SeekHoverOverlay.Visibility = Visibility.Collapsed; return; }
        // Match the native Track's 16-DIP thumb and 8-DIP rail insets. This is
        // only a preview: hovering never mutates Slider.Value or native Position.
        var railWidth = SeekSlider.ActualWidth - 16;
        var offset = Math.Clamp(pointerX - 8, 0, railWidth);
        var fraction = offset / railWidth;
        if (SeekSlider.IsDirectionReversed) fraction = 1 - fraction;
        SeekHoverTime.Text = FormatTime(TimeSpan.FromSeconds(SeekSlider.Minimum + fraction * (SeekSlider.Maximum - SeekSlider.Minimum)));
        SeekHoverRange.Width = offset;
        Canvas.SetLeft(SeekHoverMarker, 8 + offset - 1);
        Canvas.SetLeft(SeekHoverBubble, Math.Clamp(8 + offset - 30, 0, Math.Max(0, SeekSlider.ActualWidth - 60)));
        SeekHoverOverlay.Visibility = Visibility.Visible;
    }

    // Leave space for the 10-DIP scrollbar and row padding. A resizing window
    // should not hide the album/duration or create a horizontal scrollbar.
    private void FitTrackColumns()
    {
        if (TracksList.View is not GridView view || view.Columns.Count != 6 || TracksList.ActualWidth < 360) return;
        var flexible = Math.Max(160, TracksList.ActualWidth - 22 - 40 - 38 - 64);
        double[] widths = [40, 38, Math.Floor(flexible * .47), Math.Floor(flexible * .29), Math.Floor(flexible * .24), 64];
        for (var i = 0; i < widths.Length; i++)
            if (double.IsNaN(view.Columns[i].Width) || Math.Abs(view.Columns[i].Width - widths[i]) > .5)
                view.Columns[i].Width = widths[i];
    }

    private void FinishSeek(bool commit)
    {
        if (!_seekDragging) return;
        if (!commit && _seekKeyboardGesture) _seekIgnoreKeyUp = true;
        _seekKeyboardGesture = false;
        _seekDragging = false;
        _seekGesture?.Cancel();
        if (commit && SeekSlider.IsEnabled) CommitSeek(SeekSlider.Value);
        else UpdateProgress();
    }

    private void CommitSeek(double seconds)
    {
        if (!_player.HasSource || !double.IsFinite(seconds)) return;
        var target = Math.Clamp(seconds, 0, _player.Duration.TotalSeconds);
        PlaybackTimeline.Stop(SeekSlider);
        if (_expandedPlayerSeek is not null) PlaybackTimeline.Stop(_expandedPlayerSeek);
        _committingSeek = true;
        try
        {
            _pendingSeek = target; _seekClock.Restart();
            _player.Seek(TimeSpan.FromSeconds(target));
            SeekSlider.Value = target;
            PositionText.Text = FormatTime(TimeSpan.FromSeconds(target));
            UpdateLyrics(target); RefreshExpandedPlayer(); UpdateShellState(); SavePlaybackState();
        }
        finally { _committingSeek = false; }
    }

    private void FlushVolumePreferences()
    {
        if (_closed) { _volumeSaveTimer.Stop(); return; }
        if (SliderGesture.HasActiveGesture || Mouse.LeftButton == MouseButtonState.Pressed)
        { _volumeSaveTimer.Stop(); _volumeSaveTimer.Start(); return; }
        _volumeSaveTimer.Stop(); SavePreferences();
    }

    private TimeSpan RefreshPlaybackDisplay()
    {
        var duration = RestoredPlaybackDuration ?? _player.Duration;
        var position = DisplayPosition(_player.Position);
        if (!_seekDragging) PlaybackTimeline.Update(SeekSlider, position.TotalSeconds, duration.TotalSeconds,
            _player.IsPlaying && _pendingSeek is null && !_loadingStream && !_closed, _player.SpeedRatio);
        PositionText.Text = FormatTime(_seekDragging ? TimeSpan.FromSeconds(SeekSlider.Value) : position);
        DurationText.Text = FormatTime(duration);
        return position;
    }

    private TimeSpan DisplayPosition(TimeSpan nativePosition)
    {
        if (RestoredPlaybackPosition is { } restored) return restored;
        if (_pendingSeek is not { } target) return nativePosition;
        if (_committingSeek) return TimeSpan.FromSeconds(target);
        if (!_player.HasSource || Math.Abs(nativePosition.TotalSeconds - target) < .8 || _seekClock.ElapsedMilliseconds > 1500)
        { _pendingSeek = null; return nativePosition; }
        // MediaPlayer applies Position asynchronously; keep the visible target
        // until the media clock acknowledges it instead of jumping backwards.
        return TimeSpan.FromSeconds(target);
    }
}
