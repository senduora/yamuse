using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace YMusic.Native;

internal static class WindowAppearance
{
    private static readonly HashSet<Window> AttachedWindows = [];
    private static readonly DependencyProperty AttachedProperty = DependencyProperty.RegisterAttached(
        "Attached", typeof(bool), typeof(WindowAppearance), new PropertyMetadata(false));
    private static readonly DependencyProperty ResultProperty = DependencyProperty.RegisterAttached(
        "FrameApplication", typeof(FrameApplication), typeof(WindowAppearance), new PropertyMetadata(null));
    internal sealed record FrameApplication(int DarkResult, int? CaptionResult, int? TextResult,
        bool HighContrast, uint? CaptionColor, uint? TextColor);
    internal static FrameApplication? GetLastApplication(Window window) => window.GetValue(ResultProperty) as FrameApplication;
    internal static bool IsLight => ThemeManager.IsLight;

    internal static void Apply(Window window)
    {
        window.Dispatcher.VerifyAccess();
        window.Icon ??= AppBranding.WindowIcon;
        if (!(bool)window.GetValue(AttachedProperty))
        {
            window.SetValue(AttachedProperty, true);
            window.SourceInitialized += SourceInitialized;
            if (AttachedWindows.Add(window) && AttachedWindows.Count == 1) ThemeManager.Changed += ThemeChanged;
            window.Closed += Closed;
        }
        var handle = new WindowInteropHelper(window).Handle;
        if (handle != 0) ApplyFrame(window, handle);
    }

    private static void ThemeChanged(object? sender, EventArgs args)
    {
        foreach (var window in AttachedWindows)
        {
            var handle = new WindowInteropHelper(window).Handle;
            if (handle != 0) ApplyFrame(window, handle);
        }
    }

    private static void Closed(object? sender, EventArgs args)
    {
        if (sender is not Window window) return;
        AttachedWindows.Remove(window);
        if (AttachedWindows.Count == 0) ThemeManager.Changed -= ThemeChanged;
        window.Closed -= Closed;
        window.SourceInitialized -= SourceInitialized;
    }

    private static void SourceInitialized(object? sender, EventArgs e)
    {
        if (sender is Window window) ApplyFrame(window, new WindowInteropHelper(window).Handle);
    }

    private static void ApplyFrame(Window window, nint handle)
    {
        if (handle == 0 || !OperatingSystem.IsWindows()) return;
        var highContrast = SystemParameters.HighContrast;
        var dark = highContrast || ThemeManager.IsLight ? 0 : 1;
        var darkResult = DwmSetWindowAttribute(handle, 20, ref dark, sizeof(int));
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
        {
            window.SetValue(ResultProperty, new FrameApplication(darkResult, null, null, highContrast, null, null));
            return;
        }
        var background = window.Background is SolidColorBrush brush ? brush.Color : Color.FromRgb(16, 18, 22);
        var caption = highContrast ? uint.MaxValue : ToColorRef(background);
        var foreground = window.Foreground is SolidColorBrush ink ? ink.Color : ThemeManager.IsLight ? Color.FromRgb(35, 37, 43) : Color.FromRgb(244, 245, 247);
        var text = highContrast ? uint.MaxValue : ToColorRef(foreground);
        var captionResult = DwmSetWindowAttribute(handle, 35, ref caption, sizeof(uint));
        var textResult = DwmSetWindowAttribute(handle, 36, ref text, sizeof(uint));
        window.SetValue(ResultProperty, new FrameApplication(darkResult, captionResult, textResult, highContrast, caption, text));
    }

    internal static uint ToColorRef(Color color) => (uint)(color.R | color.G << 8 | color.B << 16);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref uint value, int size);
}
