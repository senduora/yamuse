using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace YMusic.Native;

/// <summary>Soft cover-coloured atmosphere, drawn and animated by WPF without frame callbacks.</summary>
public sealed class AmbientBackdrop : Grid
{
    public static readonly DependencyProperty AccentBrushProperty = DependencyProperty.Register(
        nameof(AccentBrush), typeof(SolidColorBrush), typeof(AmbientBackdrop), new PropertyMetadata(null));
    public SolidColorBrush? AccentBrush
    {
        get => (SolidColorBrush?)GetValue(AccentBrushProperty);
        set => SetValue(AccentBrushProperty, value);
    }

    public static readonly DependencyProperty CornerRadiusProperty = DependencyProperty.Register(
        nameof(CornerRadius), typeof(double), typeof(AmbientBackdrop), new PropertyMetadata(0d,
            (target, _) => ((AmbientBackdrop)target).UpdateClip()));
    public double CornerRadius
    {
        get => (double)GetValue(CornerRadiusProperty);
        set => SetValue(CornerRadiusProperty, value);
    }

    public static readonly DependencyProperty IsMotionEnabledProperty = DependencyProperty.Register(
        nameof(IsMotionEnabled), typeof(bool), typeof(AmbientBackdrop), new PropertyMetadata(true,
            (target, _) => ((AmbientBackdrop)target).UpdateMotion()));
    public bool IsMotionEnabled
    {
        get => (bool)GetValue(IsMotionEnabledProperty);
        set => SetValue(IsMotionEnabledProperty, value);
    }

    private readonly Storyboard _motion = new();
    private readonly RectangleGeometry _roundedClip = new();
    private Window? _owner;
    private bool _begun, _running, _closed;
    private TimeSpan _resumeTime;

    public AmbientBackdrop()
    {
        IsHitTestVisible = false;
        Focusable = false;
        ClipToBounds = true;
        Clip = _roundedClip;
        Background = ThemeManager.Brush(Color.FromRgb(17, 21, 28));
        SetResourceReference(AccentBrushProperty, "AccentBrush");

        // A fixed logical canvas only scales with layout. The animated transforms
        // never invalidate the surrounding player/lyrics measure or arrangement.
        var clouds = new Canvas { Width = 1000, Height = 700, IsHitTestVisible = false };
        Children.Add(new Viewbox { Stretch = Stretch.Fill, Child = clouds, IsHitTestVisible = false });
        AddCloud(clouds, 0, -170, -240, 880, 780, -60, 110, 65, -55, 20);
        AddCloud(clouds, 1, 510, -180, 780, 710, 55, -110, -45, 120, 22);
        AddCloud(clouds, 2, 180, 300, 760, 640, -70, 95, 60, -65, 22);
        AddCloud(clouds, 3, -300, 330, 750, 600, 90, -55, -25, 85, 21);
        var veil = new LinearGradientBrush { StartPoint = new Point(.5, 0), EndPoint = new Point(.5, 1) };
        foreach (var (alpha, offset) in new[] { ((byte)90, 0d), ((byte)135, .48), ((byte)211, 1d) })
        {
            var stop = new GradientStop { Offset = offset };
            BindingOperations.SetBinding(stop, GradientStop.ColorProperty, new Binding(nameof(SolidColorBrush.Color))
            { Source = ThemeManager.Brush(Color.FromArgb(alpha, 10, 14, 22)), Converter = VeilColorConverter.Instance });
            veil.GradientStops.Add(stop);
        }
        Children.Add(new Border { Background = veil, IsHitTestVisible = false });
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        IsVisibleChanged += OnVisibleChanged;
        SizeChanged += (_, _) => UpdateClip();
    }

    private void AddCloud(Canvas canvas, int tint, double left, double top, double width, double height,
        double xFrom, double xTo, double yFrom, double yTo, double seconds)
    {
        var brush = new RadialGradientBrush { Center = new Point(.5, .5), GradientOrigin = new Point(.5, .5), RadiusX = .5, RadiusY = .5 };
        foreach (var (offset, opacity) in new[] { (0d, .90), (.30, .70), (.62, .22), (1d, 0d) })
        {
            var stop = new GradientStop { Offset = offset };
            var colors = new MultiBinding { Converter = CloudColorConverter.Instance, ConverterParameter = new CloudTint(tint, opacity) };
            colors.Bindings.Add(new Binding(nameof(AccentBrush) + ".Color") { Source = this });
            // The stable theme brush also invalidates the presentation when a
            // theme changes in the middle of the existing cover colour fade.
            colors.Bindings.Add(new Binding(nameof(Background) + ".Color") { Source = this });
            BindingOperations.SetBinding(stop, GradientStop.ColorProperty, colors);
            brush.GradientStops.Add(stop);
        }
        var translation = new TranslateTransform(xFrom, yFrom);
        var scale = new ScaleTransform(1, 1);
        var transforms = new TransformGroup(); transforms.Children.Add(scale); transforms.Children.Add(translation);
        var cloud = new Ellipse { Width = width, Height = height, Fill = brush, RenderTransform = transforms,
            RenderTransformOrigin = new Point(.5, .5), IsHitTestVisible = false };
        Canvas.SetLeft(cloud, left); Canvas.SetTop(cloud, top); canvas.Children.Add(cloud);
        Animate(cloud, 1, TranslateTransform.XProperty, xFrom, xTo, seconds);
        Animate(cloud, 1, TranslateTransform.YProperty, yFrom, yTo, seconds + 2);
        Animate(cloud, 0, ScaleTransform.ScaleXProperty, 1, 1.13, seconds + 1);
        Animate(cloud, 0, ScaleTransform.ScaleYProperty, 1, 1.09, seconds + 3);
    }

    private void Animate(Ellipse target, int transformIndex, DependencyProperty property, double from, double to, double seconds)
    {
        var animation = new DoubleAnimation(from, to, TimeSpan.FromSeconds(seconds / 2))
        { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } };
        Storyboard.SetTarget(animation, target);
        // Address the transform through its visual owner. A Freezable stored as
        // a direct timeline target can be cloned with the timeline itself.
        Storyboard.SetTargetProperty(animation, new PropertyPath($"(0).(1)[{transformIndex}].(2)",
            UIElement.RenderTransformProperty, TransformGroup.ChildrenProperty, property));
        _motion.Children.Add(animation);
    }

    private void UpdateClip()
    {
        _roundedClip.Rect = new Rect(0, 0, Math.Max(0, ActualWidth), Math.Max(0, ActualHeight));
        _roundedClip.RadiusX = _roundedClip.RadiusY = Math.Max(0, CornerRadius);
    }

    private void OnLoaded(object sender, RoutedEventArgs args)
    {
        if (_closed) return;
        var owner = Window.GetWindow(this);
        if (!ReferenceEquals(owner, _owner))
        {
            DetachOwner(); _owner = owner;
            if (_owner is not null) { _owner.StateChanged += OnOwnerStateChanged; _owner.Closed += OnOwnerClosed; }
        }
        UpdateClip(); UpdateMotion();
    }

    private void OnUnloaded(object sender, RoutedEventArgs args)
    {
        if (_begun)
        {
            _resumeTime = _motion.GetCurrentTime(this) ?? TimeSpan.Zero;
            _motion.Remove(this); _begun = false;
        }
        _running = false; DetachOwner();
    }
    private void OnVisibleChanged(object sender, DependencyPropertyChangedEventArgs args) => UpdateMotion();
    private void OnOwnerStateChanged(object? sender, EventArgs args) => UpdateMotion();
    private void OnOwnerClosed(object? sender, EventArgs args) => Release();
    internal void Release()
    {
        _closed = true;
        if (_begun) _motion.Remove(this);
        _begun = _running = false; DetachOwner();
        Loaded -= OnLoaded; Unloaded -= OnUnloaded; IsVisibleChanged -= OnVisibleChanged;
    }

    private void DetachOwner()
    {
        if (_owner is null) return;
        _owner.StateChanged -= OnOwnerStateChanged; _owner.Closed -= OnOwnerClosed; _owner = null;
    }

    private void UpdateMotion()
    {
        if (_closed) return;
        if (!IsMotionEnabled || !IsLoaded || !IsVisible || _owner?.WindowState == WindowState.Minimized || !Motion.CanAnimate) { PauseMotion(); return; }
        if (!_begun)
        {
            _motion.Begin(this, HandoffBehavior.SnapshotAndReplace, true); _begun = true;
            if (_resumeTime > TimeSpan.Zero) _motion.SeekAlignedToLastTick(this, _resumeTime, TimeSeekOrigin.BeginTime);
        }
        else if (!_running) _motion.Resume(this);
        _running = true;
    }

    private void PauseMotion()
    {
        if (_begun && _running) _motion.Pause(this);
        _running = false;
    }

    private sealed record CloudTint(int Index, double Opacity);
    private sealed class CloudColorConverter : IMultiValueConverter
    {
        internal static readonly CloudColorConverter Instance = new();
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            if (values.Length == 0 || values[0] is not Color color || parameter is not CloudTint tint) return Colors.Transparent;
            var light = ThemeManager.IsLight && !SystemParameters.HighContrast;
            // Nearby tones keep the atmosphere related to the cover even when
            // the selected accent is blue/green; there is no fixed orange layer.
            var mix = tint.Index switch { 1 => .28, 2 => .12, 3 => .20, _ => 0 };
            var red = color.R * (1 - mix) + (tint.Index == 1 ? color.B : color.G) * mix;
            var green = color.G * (1 - mix) + color.B * mix;
            var blue = color.B * (1 - mix) + color.R * mix;
            var shown = Color.FromRgb((byte)red, (byte)green, (byte)blue);
            if (light) shown = LightCloudColor(shown);
            // A broader soft falloff gives Light a visible colour field rather
            // than leaving almost the whole surface behind the white veil.
            var opacity = light ? Math.Sqrt(tint.Opacity) : tint.Opacity;
            return Color.FromArgb((byte)Math.Round(255 * opacity), shown.R, shown.G, shown.B);
        }
        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) => [Binding.DoNothing, Binding.DoNothing];

        private static Color LightCloudColor(Color color)
        {
            var maximum = Math.Max(color.R, Math.Max(color.G, color.B));
            var chroma = maximum - Math.Min(color.R, Math.Min(color.G, color.B));
            if (chroma < 8)
            {
                // Lift neutral covers without magnifying tiny channel noise.
                var lift = Math.Max(0, 235 - maximum);
                return Color.FromRgb((byte)(color.R + lift), (byte)(color.G + lift), (byte)(color.B + lift));
            }
            // Retain a much wider cover-colour range than a near-white pastel.
            // Fit each nearby cloud tone to the actual muted text colour before
            // compositing, so even overlapping clouds remain readable.
            var amount = Math.Clamp((chroma - 8) / 8d, 0, 1);
            var peak = Math.Max(235d, maximum);
            peak += (252 - peak) * amount;
            var scale = Math.Min(1 + 3 * amount, (peak - 156) / chroma);
            var tint = Color.FromRgb((byte)Math.Round(peak - (maximum - color.R) * scale),
                (byte)Math.Round(peak - (maximum - color.G) * scale), (byte)Math.Round(peak - (maximum - color.B) * scale));
            // Shared resources update before converted neutral backgrounds.
            // A converted Muted brush can still hold the previous theme while
            // this converter handles the new background's binding notification.
            var muted = ((SolidColorBrush)Application.Current.Resources["MutedBrush"]).Color;
            if (CoverPalette.Contrast(tint, muted) >= 4.55) return tint;
            var low = 0d; var high = 1d;
            for (var step = 0; step < 8; step++)
            {
                var middle = (low + high) / 2;
                if (CoverPalette.Contrast(CoverPalette.Mix(tint, Colors.White, middle), muted) >= 4.55) high = middle;
                else low = middle;
            }
            return CoverPalette.Mix(tint, Colors.White, high);
        }
    }

    private sealed class VeilColorConverter : IValueConverter
    {
        internal static readonly VeilColorConverter Instance = new();
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => value is Color color && ThemeManager.IsLight && !SystemParameters.HighContrast
                ? Color.FromArgb((byte)Math.Round(color.A * .30), color.R, color.G, color.B) : value;
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
    }
}
