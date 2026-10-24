using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace YMusic.Native;

public partial class MainWindow
{
    private double? _pendingSeek;
    private readonly System.Diagnostics.Stopwatch _seekClock = new();
    private void InitializeDesign()
    {
        TracksList.Loaded += (_, _) => FitTrackColumns();
        TracksList.SizeChanged += (_, _) => FitTrackColumns();
        SeekSlider.ValueChanged += (_, args) =>
        {
            if (_seekDragging) PositionText.Text = FormatTime(TimeSpan.FromSeconds(args.NewValue));
        };
        // Slider handles rail clicks before ordinary instance handlers.
        SeekSlider.AddHandler(PreviewMouseLeftButtonDownEvent, new MouseButtonEventHandler(Seek_Start), true);
        SeekSlider.AddHandler(PreviewMouseLeftButtonUpEvent, new MouseButtonEventHandler(Seek_End), true);
        SeekSlider.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler((_, args) => FinishSeek(!args.Canceled)), true);
        SeekSlider.AddHandler(Mouse.LostMouseCaptureEvent, new MouseEventHandler((_, _) =>
        {
            if (_seekDragging && !SeekSlider.IsMouseCaptureWithin) FinishSeek(false);
        }), true);
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
        _seekDragging = false;
        if (commit && SeekSlider.IsEnabled) CommitSeek(SeekSlider.Value);
        else UpdateProgress();
    }

    private void CommitSeek(double seconds)
    {
        if (!_player.HasSource || !double.IsFinite(seconds)) return;
        var target = Math.Clamp(seconds, 0, _player.Duration.TotalSeconds);
        _player.Seek(TimeSpan.FromSeconds(target));
        _pendingSeek = target;
        _seekClock.Restart();
        SeekSlider.Value = target;
        PositionText.Text = FormatTime(TimeSpan.FromSeconds(target));
        UpdateLyrics(target);
    }

    private TimeSpan DisplayPosition(TimeSpan nativePosition)
    {
        if (_pendingSeek is not { } target) return nativePosition;
        if (!_player.HasSource || Math.Abs(nativePosition.TotalSeconds - target) < .8 || _seekClock.ElapsedMilliseconds > 1500)
        { _pendingSeek = null; return nativePosition; }
        // MediaPlayer applies Position asynchronously; keep the visible target
        // until the media clock acknowledges it instead of jumping backwards.
        return TimeSpan.FromSeconds(target);
    }
}
