using Microsoft.Win32;
using System.Security;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace YMusic.Native;

/// <summary>One event-driven theme palette shared by XAML and code-created views.</summary>
internal static class ThemeManager
{
    private static readonly Dictionary<string, (Color Dark, Color Light)> Palette = new()
    {
        ["BackgroundBrush"] = Pair("#101114", "#F5F6F8"), ["PanelBrush"] = Pair("#17191D", "#FFFFFF"),
        ["SidebarBrush"] = Pair("#15171B", "#ECEEF2"), ["ForegroundBrush"] = Pair("#F3F1F0", "#23252B"),
        ["MutedBrush"] = Pair("#ADB0BA", "#606570"), ["BorderBrush"] = Pair("#343842", "#CDD1D9"),
        ["StrongBorderBrush"] = Pair("#555861", "#959CA8"), ["InputBrush"] = Pair("#1A1C21", "#FFFFFF"),
        ["ControlBrush"] = Pair("#25282F", "#E7EAF0"), ["HoverBrush"] = Pair("#343841", "#DCE1E9"),
        ["PopupBrush"] = Pair("#25272D", "#FFFFFF"), ["ToastSurfaceBrush"] = Pair("#25282F", "#FFFFFF"),
        ["ToastBorderBrush"] = Pair("#3B3F48", "#C2C8D2"), ["CoverPlaceholderBrush"] = Pair("#28272E", "#E1E5EC"),
        ["TrackBrush"] = Pair("#34363D", "#C5CBD5"), ["ScrollThumbBrush"] = Pair("#51545D", "#A2A9B5"),
        ["ScrollHoverBrush"] = Pair("#8E929D", "#737C8A"), ["HoverOverlayBrush"] = Pair("#FFFFFF", "#000000"),
        ["DownloadBrush"] = Pair("#5BD69A", "#167344"), ["ErrorBrush"] = Pair("#FF9A8B", "#A42D20"),
        ["HeroStartBrush"] = Pair("#3C2A38", "#E9E2EC"), ["HeroEndBrush"] = Pair("#1D202A", "#EDF0F6")
    };
    private static readonly Dictionary<string, SolidColorBrush> Shared = [];
    private static readonly Dictionary<Color, SolidColorBrush> Converted = [];
    private static readonly Dictionary<Color, string> NeutralKeys = new()
    {
        [Parse("#181A20")] = "PanelBrush", [Parse("#F4F1F0")] = "ForegroundBrush", [Parse("#EEECEE")] = "ForegroundBrush",
        [Parse("#989BA7")] = "MutedBrush", [Parse("#353740")] = "BorderBrush", [Parse("#292B33")] = "CoverPlaceholderBrush",
        [Parse("#23262E")] = "ControlBrush", [Parse("#3A3C45")] = "TrackBrush"
    };
    private static Application? _application;
    private static SolidColorBrush? _defaultAccentText;
    private static bool _subscribed;
    private static bool _highContrast;
    internal static string Mode { get; private set; } = "System";
    internal static bool IsLight { get; private set; }
    internal static Func<bool> SystemLightDetector { get; set; } = ReadWindowsLight;
    internal static event EventHandler? Changed;

    internal static void Initialize(Application application)
    {
        application.Dispatcher.VerifyAccess();
        if (ReferenceEquals(_application, application)) return;
        if (_application is not null) _application.Exit -= OnExit;
        _application = application;
        foreach (var (key, colors) in Palette)
        {
            // A held completed clock prevents ResourceDictionary/style sealing
            // from freezing these stable shared instances; no frame timer runs.
            var brush = new SolidColorBrush(colors.Dark); HoldMutable(brush, colors.Dark);
            Shared[key] = brush; application.Resources[key] = brush;
        }
        _defaultAccentText = new SolidColorBrush(Parse("#E7BB59")); HoldMutable(_defaultAccentText, _defaultAccentText.Color);
        application.Resources["AccentTextBrush"] = _defaultAccentText;
        application.Exit += OnExit;
        if (!_subscribed && OperatingSystem.IsWindows())
        {
            SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged; _subscribed = true;
        }
        Apply(force: true);
    }

    internal static void SetMode(string? mode)
    {
        _application?.Dispatcher.VerifyAccess();
        var normalized = string.Equals(mode, "Light", StringComparison.OrdinalIgnoreCase) ? "Light"
            : string.Equals(mode, "Dark", StringComparison.OrdinalIgnoreCase) ? "Dark" : "System";
        if (Mode == normalized) { Apply(); return; }
        Mode = normalized; Apply(force: true);
    }

    internal static void RefreshSystemTheme()
    {
        var application = _application;
        if (application is null || application.Dispatcher.HasShutdownStarted) return;
        if (!application.Dispatcher.CheckAccess()) { application.Dispatcher.BeginInvoke(new Action(RefreshSystemTheme)); return; }
        Apply();
    }

    private static void Apply(bool force = false)
    {
        if (_application is null) return;
        var highContrast = SystemParameters.HighContrast;
        var light = highContrast ? Luminance(SystemColors.WindowColor) > .5
            : Mode == "Light" || Mode == "System" && SystemLightDetector();
        if (!force && IsLight == light && _highContrast == highContrast) return;
        IsLight = light; _highContrast = highContrast;
        foreach (var (key, colors) in Palette)
            HoldMutable(Shared[key], highContrast ? HighContrastColor(key) : light ? colors.Light : colors.Dark);
        foreach (var (original, brush) in Converted) HoldMutable(brush, ConvertNeutral(original));
        // The cover accent controller may take ownership of this resource.
        // Keep its chosen accent intact; its Changed subscriber retargets it.
        if (_defaultAccentText is not null && ReferenceEquals(_application.Resources["AccentTextBrush"], _defaultAccentText))
            HoldMutable(_defaultAccentText, highContrast ? SystemColors.HotTrackColor : Parse(light ? "#886000" : "#E7BB59"));
        Changed?.Invoke(null, EventArgs.Empty);
    }

    internal static SolidColorBrush Brush(string original) => Brush(Parse(original));
    internal static SolidColorBrush Brush(Color original)
    {
        if (Application.Current is { } application && !ReferenceEquals(_application, application)) Initialize(application);
        _application?.Dispatcher.VerifyAccess();
        if (Converted.TryGetValue(original, out var existing)) return existing;
        var brush = new SolidColorBrush(ConvertNeutral(original)); HoldMutable(brush, brush.Color); Converted.Add(original, brush); return brush;
    }

    internal static Color ReadableAccent(Color color)
    {
        if (SystemParameters.HighContrast) return SystemColors.HotTrackColor;
        if (!IsLight) return color;
        // Hover is the darkest standard light surface. Passing it also keeps
        // links readable on the lighter sidebar, panel and page backgrounds.
        var background = Palette["HoverBrush"].Light;
        var original = Color.FromRgb(color.R, color.G, color.B);
        if (Contrast(original, background) >= 4.5) return original;
        for (var step = 1; step <= 100; step++)
        {
            var fraction = 1 - step / 100d;
            var darker = Color.FromRgb((byte)Math.Round(original.R * fraction), (byte)Math.Round(original.G * fraction), (byte)Math.Round(original.B * fraction));
            if (Contrast(darker, background) >= 4.5) return darker;
        }
        return Colors.Black;
    }

    private static Color ConvertNeutral(Color original)
    {
        // Exact known UI neutrals are converted. Arbitrary artwork/accent
        // colours are not passed through the shared theme palette.
        var maximum = Math.Max(original.R, Math.Max(original.G, original.B));
        var minimum = Math.Min(original.R, Math.Min(original.G, original.B));
        if (maximum - minimum > 40) return original;
        string key;
        if (NeutralKeys.TryGetValue(original, out var knownKey)) key = knownKey;
        else if (maximum >= 220) key = "ForegroundBrush";
        else if (maximum >= 135) key = "MutedBrush";
        else if (maximum >= 105) key = "ScrollHoverBrush";
        else if (maximum >= 75) key = "ScrollThumbBrush";
        else if (maximum >= 58) key = "StrongBorderBrush";
        else if (maximum >= 47) key = "BorderBrush";
        else if (maximum >= 37) key = "ControlBrush";
        else if (maximum >= 28) key = "PanelBrush";
        else key = "BackgroundBrush";
        var target = SystemParameters.HighContrast ? HighContrastColor(key) : IsLight ? Palette[key].Light : original;
        return Color.FromArgb(original.A, target.R, target.G, target.B);
    }

    private static Color HighContrastColor(string key) => key switch
    {
        "ForegroundBrush" or "MutedBrush" or "ErrorBrush" or "DownloadBrush" or "ScrollThumbBrush" or "ScrollHoverBrush" => SystemColors.WindowTextColor,
        "BorderBrush" or "StrongBorderBrush" or "ToastBorderBrush" or "TrackBrush" => SystemColors.WindowTextColor,
        "HoverBrush" => SystemColors.HighlightColor,
        "HoverOverlayBrush" => SystemColors.WindowTextColor,
        _ => SystemColors.WindowColor
    };
    private static void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs args) => RefreshSystemTheme();
    private static void OnExit(object? sender, ExitEventArgs args)
    {
        if (_subscribed) { SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged; _subscribed = false; }
        if (_application is not null) _application.Exit -= OnExit;
        _application = null;
    }
    private static bool ReadWindowsLight()
    {
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", writable: false);
            return key?.GetValue("AppsUseLightTheme") is not int value || value != 0;
        }
        catch (Exception error) when (error is SecurityException or UnauthorizedAccessException or System.IO.IOException) { return false; }
    }
    private static (Color, Color) Pair(string dark, string light) => (Parse(dark), Parse(light));
    private static Color Parse(string text) => (Color)ColorConverter.ConvertFromString(text);
    private static void HoldMutable(SolidColorBrush brush, Color color)
    {
        brush.Color = color;
        var clock = (AnimationClock)new ColorAnimation(color, color, new Duration(TimeSpan.Zero)) { FillBehavior = FillBehavior.HoldEnd }.CreateClock(true);
        brush.ApplyAnimationClock(SolidColorBrush.ColorProperty, clock, HandoffBehavior.SnapshotAndReplace);
        // Theme consumers (including native captions) run synchronously in
        // Changed. Commit the completed clock now, before the next WPF tick.
        clock.Controller!.Begin();
        clock.Controller.SeekAlignedToLastTick(TimeSpan.Zero, TimeSeekOrigin.BeginTime);
    }
    private static double Luminance(Color color)
    {
        static double Channel(byte value) { var s = value / 255d; return s <= .04045 ? s / 12.92 : Math.Pow((s + .055) / 1.055, 2.4); }
        return .2126 * Channel(color.R) + .7152 * Channel(color.G) + .0722 * Channel(color.B);
    }
    private static double Contrast(Color left, Color right) { var a = Luminance(left); var b = Luminance(right); return (Math.Max(a, b) + .05) / (Math.Min(a, b) + .05); }
}
