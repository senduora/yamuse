using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace YMusic.Native;

/// <summary>Short, event-driven motion for template parts and refreshed content.</summary>
public static class Motion
{
    private static readonly ConditionalWeakTable<UIElement, Entrance> Entrances = new();
    private static readonly Duration InteractionDuration = new(TimeSpan.FromMilliseconds(170));
    private static readonly Duration EntranceDuration = new(TimeSpan.FromMilliseconds(260));
    private static readonly IEasingFunction EaseOut = new CubicEase { EasingMode = EasingMode.EaseOut };

    public static readonly DependencyProperty OpacityProperty = DependencyProperty.RegisterAttached(
        "Opacity", typeof(double), typeof(Motion), new PropertyMetadata(1d,
            (target, args) => AnimateNumber(target, UIElement.OpacityProperty, (double)args.NewValue)));
    public static void SetOpacity(DependencyObject target, double value) => target.SetValue(OpacityProperty, value);
    public static double GetOpacity(DependencyObject target) => (double)target.GetValue(OpacityProperty);

    public static readonly DependencyProperty BorderBrushProperty = DependencyProperty.RegisterAttached(
        "BorderBrush", typeof(Brush), typeof(Motion), new PropertyMetadata(null,
            (target, args) => AnimateBrush(target, Border.BorderBrushProperty, (Brush?)args.NewValue)));
    public static void SetBorderBrush(DependencyObject target, Brush? value) => target.SetValue(BorderBrushProperty, value);
    public static Brush? GetBorderBrush(DependencyObject target) => (Brush?)target.GetValue(BorderBrushProperty);

    public static readonly DependencyProperty BackgroundProperty = DependencyProperty.RegisterAttached(
        "Background", typeof(Brush), typeof(Motion), new PropertyMetadata(null,
            (target, args) => AnimateBrush(target, Border.BackgroundProperty, (Brush?)args.NewValue)));
    public static void SetBackground(DependencyObject target, Brush? value) => target.SetValue(BackgroundProperty, value);
    public static Brush? GetBackground(DependencyObject target) => (Brush?)target.GetValue(BackgroundProperty);

    public static readonly DependencyProperty FillProperty = DependencyProperty.RegisterAttached(
        "Fill", typeof(Brush), typeof(Motion), new PropertyMetadata(null,
            (target, args) => AnimateBrush(target, Shape.FillProperty, (Brush?)args.NewValue)));
    public static void SetFill(DependencyObject target, Brush? value) => target.SetValue(FillProperty, value);
    public static Brush? GetFill(DependencyObject target) => (Brush?)target.GetValue(FillProperty);

    public static readonly DependencyProperty ScaleProperty = DependencyProperty.RegisterAttached(
        "Scale", typeof(double), typeof(Motion), new PropertyMetadata(1d, (target, args) =>
        {
            AnimateNumber(ScaleTarget(target), ScaleTransform.ScaleXProperty, (double)args.NewValue);
            AnimateNumber(ScaleTarget(target), ScaleTransform.ScaleYProperty, (double)args.NewValue);
        }));
    public static void SetScale(DependencyObject target, double value) => target.SetValue(ScaleProperty, value);
    public static double GetScale(DependencyObject target) => (double)target.GetValue(ScaleProperty);

    public static readonly DependencyProperty ScaleXProperty = DependencyProperty.RegisterAttached(
        "ScaleX", typeof(double), typeof(Motion), new PropertyMetadata(1d,
            (target, args) => AnimateNumber(ScaleTarget(target), ScaleTransform.ScaleXProperty, (double)args.NewValue)));
    public static void SetScaleX(DependencyObject target, double value) => target.SetValue(ScaleXProperty, value);
    public static double GetScaleX(DependencyObject target) => (double)target.GetValue(ScaleXProperty);

    internal static bool AnimationAllowed(bool clientAreaAnimation, int renderTier, RenderMode renderMode) =>
        clientAreaAnimation && renderTier >= 1 && renderMode != RenderMode.SoftwareOnly;

    internal static bool CanAnimate => AnimationAllowed(SystemParameters.ClientAreaAnimation,
        RenderCapability.Tier >> 16, RenderOptions.ProcessRenderMode);

    private static ScaleTransform ScaleTarget(DependencyObject target) => target is UIElement element
        ? (ScaleTransform)element.RenderTransform : (ScaleTransform)target;

    /// <summary>Animate one visible content root; never call this on every item in a list.</summary>
    public static void Enter(UIElement element, double offsetY = 6)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.VerifyAccess();
        if (Entrances.TryGetValue(element, out var previous)) previous.Finish();
        if (!CanAnimate || !element.IsVisible) return;

        // SetCurrentValue keeps an existing binding. Restore only our wrapper, so a source
        // update or another owner replacing RenderTransform during the transition wins.
        var entrance = new Entrance(element, double.IsFinite(offsetY) ? Math.Clamp(offsetY, -16, 16) : 0);
        Entrances.Add(element, entrance);
        entrance.Start();
    }

    private static void AnimateNumber(DependencyObject target, DependencyProperty property, double value)
    {
        var from = (double)target.GetValue(property);
        // These attached values own private template parts. A local base value is
        // required here: SetCurrentValue can be discarded when an animation stops.
        target.SetValue(property, value);
        var animated = (IAnimatable)target;
        animated.BeginAnimation(property, CanAnimate && (target is not UIElement element || element.IsVisible) && from != value
            ? new DoubleAnimation(from, value, InteractionDuration) { EasingFunction = EaseOut, FillBehavior = FillBehavior.Stop }
            : null, HandoffBehavior.SnapshotAndReplace);
    }

    private static void AnimateBrush(DependencyObject target, DependencyProperty property, Brush? value)
    {
        var previous = target.GetValue(property) as SolidColorBrush;
        if (!CanAnimate || previous is null || value is not SolidColorBrush next || previous.Color == next.Color)
        {
            target.SetCurrentValue(property, value);
            return;
        }

        // Resource brushes may be frozen and shared. Animate only a private copy.
        var brush = next.CloneCurrentValue();
        target.SetCurrentValue(property, brush);
        brush.BeginAnimation(SolidColorBrush.ColorProperty,
            new ColorAnimation(previous.Color, next.Color, InteractionDuration)
            { EasingFunction = EaseOut, FillBehavior = FillBehavior.Stop }, HandoffBehavior.SnapshotAndReplace);
    }

    private sealed class Entrance
    {
        private readonly UIElement _element;
        private readonly Transform? _originalTransform;
        private readonly TransformGroup _wrapper;
        private readonly TranslateTransform _translation;
        private readonly double _offset;
        private AnimationClock? _clock;
        private bool _finished;

        internal Entrance(UIElement element, double offset)
        {
            _element = element;
            _offset = offset;
            _originalTransform = element.RenderTransform;
            _translation = new TranslateTransform();
            _wrapper = new TransformGroup();
            if (_originalTransform is not null) _wrapper.Children.Add(_originalTransform);
            _wrapper.Children.Add(_translation);
        }

        internal void Start()
        {
            if (_offset != 0) _element.SetCurrentValue(UIElement.RenderTransformProperty, _wrapper);
            if (_element is FrameworkElement frameworkElement) frameworkElement.Unloaded += OnUnloaded;
            _element.IsVisibleChanged += OnVisibilityChanged;
            var opacity = new DoubleAnimation(_element.Opacity * 0.12, _element.Opacity, EntranceDuration)
            { EasingFunction = EaseOut, FillBehavior = FillBehavior.Stop };
            _clock = opacity.CreateClock();
            _clock.Completed += OnCompleted;
            _element.ApplyAnimationClock(UIElement.OpacityProperty, _clock, HandoffBehavior.SnapshotAndReplace);
            if (_offset != 0)
                _translation.BeginAnimation(TranslateTransform.YProperty,
                    new DoubleAnimation(_offset, 0, EntranceDuration)
                    { EasingFunction = EaseOut, FillBehavior = FillBehavior.Stop });
        }

        private void OnCompleted(object? sender, EventArgs args) => Finish();
        private void OnUnloaded(object sender, RoutedEventArgs args) => Finish();
        private void OnVisibilityChanged(object sender, DependencyPropertyChangedEventArgs args)
        {
            if (!(bool)args.NewValue) Finish();
        }

        internal void Finish()
        {
            if (_finished) return;
            _finished = true;
            if (_element is FrameworkElement frameworkElement) frameworkElement.Unloaded -= OnUnloaded;
            _element.IsVisibleChanged -= OnVisibilityChanged;
            if (_clock is not null) _clock.Completed -= OnCompleted;
            _element.ApplyAnimationClock(UIElement.OpacityProperty, null);
            _translation.BeginAnimation(TranslateTransform.YProperty, null);
            if (ReferenceEquals(_element.RenderTransform, _wrapper))
                _element.SetCurrentValue(UIElement.RenderTransformProperty, _originalTransform);
            Entrances.Remove(_element);
        }
    }
}
