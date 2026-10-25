using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace YMusic.Native;

// Native Slider still owns thumb and keyboard behavior. Point clicks additionally
// capture the rail so holding and moving after a click becomes a continuous drag.
internal sealed class SliderGesture
{
    private readonly Slider _slider;
    private readonly Action _begin;
    private readonly Action<bool> _finish;
    private bool _pointer, _keyboard, _rail;
    private static int _active;
    internal static bool HasActiveGesture => _active > 0;
    internal bool IsActive => _pointer || _keyboard;
    internal bool IsKeyboardActive => _keyboard;

    internal SliderGesture(Slider slider, Action begin, Action<bool> finish, bool registerPointer = true)
    {
        _slider = slider; _begin = begin; _finish = finish;
        if (registerPointer)
        {
            slider.AddHandler(UIElement.PreviewMouseLeftButtonDownEvent, new MouseButtonEventHandler(Start), true);
            slider.AddHandler(UIElement.PreviewMouseLeftButtonUpEvent, new MouseButtonEventHandler(End), true);
        }
        slider.AddHandler(Mouse.MouseMoveEvent, new MouseEventHandler((_, e) =>
        { if (_rail && _pointer && e.LeftButton == MouseButtonState.Pressed) { Point(e.GetPosition(slider)); e.Handled = true; } }), true);
        slider.AddHandler(Thumb.DragStartedEvent, new DragStartedEventHandler((_, _) => Begin(false)), true);
        slider.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler((_, e) => Complete(!e.Canceled)), true);
        slider.AddHandler(Mouse.LostMouseCaptureEvent, new MouseEventHandler((_, _) =>
        { if (_pointer && !slider.IsMouseCaptureWithin) Cancel(); }), true);
        slider.AddHandler(Keyboard.PreviewKeyDownEvent, new KeyEventHandler((_, e) =>
        { if (SeekKey(e.Key)) Begin(true); else if (e.Key == Key.Escape && IsActive) { Cancel(); e.Handled = true; } }), true);
        slider.AddHandler(Keyboard.PreviewKeyUpEvent, new KeyEventHandler((_, e) =>
        { if (SeekKey(e.Key) && _keyboard) { Complete(true); e.Handled = true; } }), true);
        slider.LostKeyboardFocus += (_, _) => { if (_keyboard) Complete(true); };
        slider.IsEnabledChanged += (_, _) => { if (!slider.IsEnabled) Cancel(); };
        slider.IsVisibleChanged += (_, _) => { if (!slider.IsVisible) { Cancel(); PlaybackTimeline.Stop(slider); } };
        slider.Unloaded += (_, _) => { Cancel(); PlaybackTimeline.Stop(slider); };
    }

    internal void Start(object sender, MouseButtonEventArgs e)
    {
        if (!_slider.IsEnabled || e.ChangedButton != MouseButton.Left) return;
        var thumb = InsideThumb(e.OriginalSource as DependencyObject);
        // Slider's class handler applies move-to-point before instance handlers.
        // Preserve that new base value rather than the old animation overlay.
        PlaybackTimeline.Stop(_slider, useBaseValue: !thumb && e.Handled);
        Begin(false);
        if (!thumb && Mouse.LeftButton == MouseButtonState.Pressed)
        {
            _rail = true; _slider.Focus(); _slider.CaptureMouse();
            Point(e.GetPosition(_slider)); e.Handled = true;
        }
    }

    internal void End(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || !_pointer) return;
        if (_rail) Point(e.GetPosition(_slider));
        Complete(true);
    }

    internal void Begin(bool keyboard)
    {
        if (!_slider.IsEnabled) return;
        if (keyboard ? _keyboard : _pointer) return;
        PlaybackTimeline.Stop(_slider);
        if (!IsActive) _active++;
        if (keyboard) _keyboard = true; else _pointer = true;
        _begin();
    }

    internal void Complete(bool commit)
    {
        if (!IsActive) return;
        Clear(); _finish(commit);
        if (!commit && _slider.IsMouseCaptureWithin && Mouse.Captured is Thumb thumb) thumb.CancelDrag();
        if (ReferenceEquals(Mouse.Captured, _slider)) _slider.ReleaseMouseCapture();
    }

    internal void Cancel() => Complete(false);
    private void Clear() { if (IsActive) _active--; _pointer = _keyboard = _rail = false; }

    internal void Point(Point point)
    {
        if (_slider.Template.FindName("PART_Track", _slider) is not Track track) return;
        var local = _slider.TranslatePoint(point, track);
        var horizontal = track.Orientation == Orientation.Horizontal;
        var thumb = horizontal ? track.Thumb?.ActualWidth ?? 0 : track.Thumb?.ActualHeight ?? 0;
        var length = (horizontal ? track.ActualWidth : track.ActualHeight) - thumb;
        if (length <= 0) return;
        var fraction = Math.Clamp(((horizontal ? local.X : local.Y) - thumb / 2) / length, 0, 1);
        if (horizontal ? track.IsDirectionReversed : !track.IsDirectionReversed) fraction = 1 - fraction;
        var target = _slider.Minimum + fraction * (_slider.Maximum - _slider.Minimum);
        if (double.IsFinite(target)) _slider.SetCurrentValue(RangeBase.ValueProperty, Math.Clamp(target, _slider.Minimum, _slider.Maximum));
    }

    private bool InsideThumb(DependencyObject? source)
    {
        for (var node = source; node is not null && !ReferenceEquals(node, _slider); node = node is Visual ? VisualTreeHelper.GetParent(node) : (node as FrameworkContentElement)?.Parent)
            if (node is Thumb) return true;
        return false;
    }
    internal static bool SeekKey(Key key) => key is Key.Left or Key.Right or Key.Up or Key.Down or Key.PageUp or Key.PageDown or Key.Home or Key.End;
}
