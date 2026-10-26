using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace YMusic.Native;

// A finite, determinate indicator. No animation clock or idle timer is created.
internal sealed class CircularDownloadProgress : ProgressBar
{
    private static readonly Pen Rail = FrozenPen(new SolidColorBrush(Color.FromRgb(64, 68, 73)));
    static CircularDownloadProgress()
    {
        ValueProperty.OverrideMetadata(typeof(CircularDownloadProgress), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));
        ForegroundProperty.OverrideMetadata(typeof(CircularDownloadProgress), new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));
    }
    internal CircularDownloadProgress()
    {
        Width = Height = 18; Minimum = 0; Maximum = 100; IsIndeterminate = false;
        Template = new ControlTemplate(typeof(ProgressBar));
        Focusable = false; IsHitTestVisible = false; SnapsToDevicePixels = true;
    }
    protected override void OnRender(DrawingContext drawing)
    {
        base.OnRender(drawing);
        var radius = Math.Max(0, Math.Min(ActualWidth, ActualHeight) / 2 - 2);
        var center = new Point(ActualWidth / 2, ActualHeight / 2);
        drawing.DrawEllipse(null, Rail, center, radius, radius);
        var fraction = Math.Clamp(Value / 100.0, 0, 1);
        if (fraction <= 0 || radius <= 0) return;
        var pen = new Pen(Foreground, 2) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        if (fraction >= 1) { drawing.DrawEllipse(null, pen, center, radius, radius); return; }
        var angle = fraction * Math.PI * 2;
        var arc = new StreamGeometry();
        using (var context = arc.Open())
        {
            context.BeginFigure(new Point(center.X, center.Y - radius), false, false);
            context.ArcTo(new Point(center.X + Math.Sin(angle) * radius, center.Y - Math.Cos(angle) * radius),
                new Size(radius, radius), 0, fraction > .5, SweepDirection.Clockwise, true, false);
        }
        arc.Freeze(); drawing.DrawGeometry(null, pen, arc);
    }
    private static Pen FrozenPen(Brush brush)
    { brush.Freeze(); var pen = new Pen(brush, 2); pen.Freeze(); return pen; }
}
