using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;
using YMusic.Native;

internal static partial class Program
{
    private static async Task Mini017NativeResizeChecks()
    {
        var window = MiniFixtureWindow();
        try
        {
            var handle = new WindowInteropHelper(window).Handle;
            var chrome = WindowChrome.GetWindowChrome(window);
            Assert(window.ResizeMode == ResizeMode.CanResize && chrome is { CaptionHeight: 0 }
                && chrome.ResizeBorderThickness.Left >= 5, "mini is missing its native resize chrome");
            Assert((Mini017GetWindowLong(handle, -16) & 0x00040000) != 0, "native window has no sizing frame");
            Assert(Mini017GetWindowRect(handle, out var rect), "cannot read mini window bounds");
            var edge = (int)Math.Ceiling(2 * VisualTreeHelper.GetDpi(window).DpiScaleX);
            var left = rect.Left + edge; var right = rect.Right - edge - 1;
            var top = rect.Top + edge; var bottom = rect.Bottom - edge - 1;
            var middleX = (left + right) / 2; var middleY = (top + bottom) / 2;
            foreach (var point in new[] { (left, middleY, 10), (right, middleY, 11), (middleX, top, 12),
                (left, top, 13), (right, top, 14), (middleX, bottom, 15), (left, bottom, 16), (right, bottom, 17) })
            {
                var result = (int)Mini017SendMessage(handle, 0x0084, 0, Mini017ScreenPoint(point.Item1, point.Item2));
                Assert(result == point.Item3, $"native resize hit test at ({point.Item1}, {point.Item2}) returned {result}, expected {point.Item3}");
            }
            var limits = new Mini017MinMaxInfo();
            Mini017GetMinMax(handle, 0x0024, 0, ref limits);
            var dpi = VisualTreeHelper.GetDpi(window);
            Assert(limits.MinTrackSize.X >= window.MinWidth * dpi.DpiScaleX - 2
                && limits.MinTrackSize.Y >= window.MinHeight * dpi.DpiScaleY - 2, "native sizing ignored the minimum dimensions");
            Assert(Mini017SetWindowPos(handle, 0, 0, 0, (int)Math.Round(700 * dpi.DpiScaleX), (int)Math.Round(320 * dpi.DpiScaleY), 0x0016), "native resize failed");
            await Task.Delay(20); window.UpdateLayout();
            Assert(Math.Abs(window.ActualWidth - 700) <= 2 && Math.Abs(window.ActualHeight - 320) <= 2, "native resize did not reach WPF layout");
            Mini017AssertBounds(window);
        }
        finally { window.Close(); }
    }

    private static async Task Mini017ResponsiveChecks()
    {
        var window = MiniFixtureWindow();
        try
        {
            window.Update("Долгое название трека · проверка адаптивного мини-плеера", "Исполнитель с длинным именем · альбом", true, true, true);
            window.UpdateDetails(MiniCover(), 4321, 9876, true, true, true, 64, "responsive-017");
            var commands = new List<ShellAction>(); var seeks = new List<double>(); var volumes = new List<double>();
            window.Command += commands.Add; window.SeekRequested += seeks.Add; window.VolumeRequested += volumes.Add;
            foreach (var size in new[] { (320d, 224d, "minimum"), (484d, 300d, "normal"), (760d, 300d, "wide"), (320d, 440d, "portrait"), (484d, 440d, "tall") })
            {
                window.Width = size.Item1; window.Height = size.Item2;
                await Task.Delay(15); window.UpdateLayout();
                if (!window.IsArtworkVisible) MiniClick(window.ArtworkButton);
                window.UpdateLayout(); Mini017AssertBounds(window);
                var artworkWidth = window.CoverImage.ActualWidth;
                Assert(artworkWidth >= 63 && Math.Abs(artworkWidth - window.CoverImage.ActualHeight) <= 1, "artwork lost its square aspect ratio");
                MiniCapture(window, "mini-017-" + size.Item3 + "-artwork.png");
                var expandedHeight = window.ActualHeight;
                MiniClick(window.ArtworkButton); await Task.Delay(20); window.UpdateLayout(); Mini017AssertBounds(window);
                Assert(!window.CoverImage.IsVisible && window.ActualHeight < expandedHeight - 30 && window.MinHeight < 224 && window.MinWidth < 320,
                    "hidden artwork did not reduce the real native footprint and minimum dimensions");
                MiniCapture(window, "mini-017-" + size.Item3 + "-compact.png");
            }
            Assert(commands.Count == 0 && seeks.Count == 0 && volumes.Count == 0, "resize or artwork layout dispatched a playback callback");
            window.Width = 100; window.Height = 100;
            await Task.Delay(15); window.UpdateLayout();
            Assert(window.ActualWidth >= window.MinWidth && window.ActualHeight >= window.MinHeight, "programmatic layout bypassed minimum size");
            Mini017AssertBounds(window);
        }
        finally { window.Close(); }
    }

    private static async Task Mini017ArtworkChecks()
    {
        var window = MiniFixtureWindow();
        try
        {
            window.Update("Artwork fixture", "Artist", true, true, true);
            var cover = MiniCover(); window.UpdateDetails(cover, 20, 180, true, true, true, 42, "artwork-017");
            var commands = new List<ShellAction>(); var seeks = new List<double>(); var volumes = new List<double>(); var opened = 0;
            window.Command += commands.Add; window.SeekRequested += seeks.Add; window.VolumeRequested += volumes.Add; window.ShowMainRequested += () => opened++;
            Assert(AutomationProperties.GetName(window.ArtworkButton) == "Скрыть обложку"
                && AutomationProperties.GetHelpText(window.ArtworkButton) == "Обложка показана" && window.ArtworkButton.Focusable, "artwork button has no accessible initial action/state");
            MiniDragStart(window); window.SeekSlider.Value = 75;
            MiniClick(window.ArtworkButton); window.Width = 620; window.Height = 290;
            await Task.Delay(15); window.UpdateLayout();
            Assert(window.IsSeeking && window.SeekSlider.Value == 75 && !window.IsArtworkVisible
                && AutomationProperties.GetName(window.ArtworkButton) == "Показать обложку"
                && AutomationProperties.GetHelpText(window.ArtworkButton) == "Обложка скрыта", "layout toggle altered the active seek or accessible action");
            var replacementCover = MiniCover();
            window.UpdateDetails(replacementCover, 22, 180, true, false, true, 42, "artwork-017");
            Assert(ReferenceEquals(window.CoverImage.Source, replacementCover) && !window.CoverImage.IsVisible
                && window.SeekSlider.Value == 75 && commands.Count == 0 && seeks.Count == 0 && volumes.Count == 0, "hidden artwork synchronization changed playback or became visible");
            MiniDragEnd(window, false);
            Assert(seeks.SequenceEqual(new[] { 75d }), "seek after resize/toggle did not commit once");
            MiniClick(window.ArtworkButton); window.UpdateLayout();
            Assert(window.IsArtworkVisible && window.CoverImage.IsVisible && ReferenceEquals(window.CoverImage.Source, replacementCover), "show artwork lost its latest synchronized image");
            MiniClick(window.PlayButton); MiniClick(MiniButton(window, "Предыдущий трек")); MiniClick(MiniButton(window, "Следующий трек")); MiniClick(window.LikeButton);
            window.VolumeSlider.Value = 83; MiniClick(MiniButton(window, "Открыть основное окно"));
            Assert(commands.SequenceEqual(new[] { ShellAction.PlayPause, ShellAction.Previous, ShellAction.Next, ShellAction.ToggleLike })
                && volumes.SequenceEqual(new[] { 83d }) && opened == 1, "controls changed their contracts after resizing/toggling artwork");
            MiniClick(window.ArtworkButton); window.Update("", "", false, false, false);
            MiniClick(window.ArtworkButton); window.UpdateLayout();
            Assert(window.CoverImage.Source is null && !window.PlayButton.IsEnabled && !window.LikeButton.IsEnabled && seeks.Count == 1,
                "artwork toggle restored stale track state");
        }
        finally { window.Close(); }
    }

    private static async Task Mini017AccentChecks()
    {
        var window = MiniFixtureWindow();
        var resources = Application.Current.Resources;
        var accent = resources["AccentBrush"]; var foreground = resources["AccentForegroundBrush"]; var accentText = resources["AccentTextBrush"];
        try
        {
            window.Update("Accent fixture", "Artist", true, true, true); window.UpdateDetails(null, 20, 180, true, true, true, 42);
            window.PinButton.IsChecked = true;
            var replacement = new SolidColorBrush(Color.FromRgb(61, 197, 153));
            var text = new SolidColorBrush(Color.FromRgb(16, 24, 28));
            var textAccent = new SolidColorBrush(Color.FromRgb(43, 92, 157));
            resources["AccentBrush"] = replacement; resources["AccentForegroundBrush"] = text; resources["AccentTextBrush"] = textAccent;
            await Task.Delay(15); window.UpdateLayout();
            Assert(ReferenceEquals(window.PlayButton.Background, replacement) && ReferenceEquals(window.PlayButton.Foreground, text)
                && ReferenceEquals(window.PinButton.Foreground, textAccent) && ReferenceEquals(window.LikeButton.Foreground, textAccent)
                && ReferenceEquals(window.SeekSlider.Foreground, replacement) && ReferenceEquals(window.VolumeSlider.Foreground, replacement), "mini retained a stale accent after the resources changed");
        }
        finally { resources["AccentBrush"] = accent; resources["AccentForegroundBrush"] = foreground; resources["AccentTextBrush"] = accentText; window.Close(); }
    }

    private static async Task Mini017CompactFootprintChecks()
    {
        var window = MiniFixtureWindow();
        try
        {
            window.Update("Компактный режим без пустого места", "Первый исполнитель, второй исполнитель, ещё один участник", false, true, true);
            window.UpdateDetails(MiniCover(), 61, 240, true, true, true, 64, "compact-footprint");
            window.Width = 484; window.Height = 300; window.UpdateLayout(); await Task.Delay(20);
            var expanded = window.RenderSize;
            MiniClick(window.ArtworkButton); await Task.Delay(20); window.UpdateLayout();
            Assert(window.ActualHeight <= 186 && window.ActualWidth * window.ActualHeight < expanded.Width * expanded.Height * .7
                && window.MinWidth == 280 && window.MinHeight == 184, "hiding artwork did not reclaim the actual window footprint");
            Mini017AssertBounds(window);
            MiniClick(window.ArtworkButton); await Task.Delay(20); window.UpdateLayout();
            Assert(Math.Abs(window.ActualWidth - expanded.Width) < 2 && Math.Abs(window.ActualHeight - expanded.Height) < 2,
                "showing artwork without a manual resize did not restore the prior dimensions");
            MiniClick(window.ArtworkButton); window.Width = 280; window.Height = 184;
            await Task.Delay(20); window.UpdateLayout(); Mini017AssertBounds(window);
            MiniCapture(window, "mini-017-compact-280.png");
            var limits = new Mini017MinMaxInfo();
            Mini017GetMinMax(new WindowInteropHelper(window).Handle, 0x0024, 0, ref limits);
            var dpi = VisualTreeHelper.GetDpi(window);
            Assert(limits.MinTrackSize.X >= 280 * dpi.DpiScaleX - 2 && limits.MinTrackSize.X < 320 * dpi.DpiScaleX
                && limits.MinTrackSize.Y >= 184 * dpi.DpiScaleY - 2 && limits.MinTrackSize.Y < 224 * dpi.DpiScaleY,
                "compact mode retained the expanded native resize limits");
            MiniClick(window.ArtworkButton); await Task.Delay(20); window.UpdateLayout(); Mini017AssertBounds(window);
            Assert(window.ActualWidth <= 322 && window.ActualHeight <= 226, "show artwork discarded the user's compact resize");
            window.Width = 760; window.Height = 300; window.UpdateLayout();
            MiniClick(window.ArtworkButton); await Task.Delay(20); window.UpdateLayout(); Mini017AssertBounds(window);
            Assert(window.ActualHeight <= 158 && window.MinHeight == 156 && Mini017Bounds(window.PlayButton, window).Left > Mini017Bounds(window.TitleText, window).Right,
                "wide coverless player did not become a slim inline layout");
            MiniCapture(window, "mini-017-slim-wide.png");
        }
        finally { window.Close(); }
    }

    private static async Task Mini017ReflowChecks()
    {
        var window = MiniFixtureWindow();
        try
        {
            const string artists = "Первый исполнитель, второй исполнитель с длинным именем, третий участник записи";
            window.Update("Длинное название композиции для проверки переноса", artists, true, true, true);
            window.UpdateDetails(MiniCover(), 61, 240, true, true, true, 64, "layout-reflow");
            var commands = new List<ShellAction>(); var seeks = new List<double>(); var volumes = new List<double>();
            window.Command += commands.Add; window.SeekRequested += seeks.Add; window.VolumeRequested += volumes.Add;
            window.Width = 484; window.Height = 300; await Task.Delay(20); window.UpdateLayout();
            var initialTimeline = window.SeekSlider.ActualWidth;
            window.Width = 760; await Task.Delay(20); window.UpdateLayout();
            Assert(Math.Abs(window.SeekSlider.ActualWidth - initialTimeline - (760 - 484)) < 3,
                "timeline did not expand with the usable window width");
            Assert(Mini017Bounds(window.CoverImage, window).Right < Mini017Bounds(window.TitleText, window).Left,
                "landscape artwork did not stay beside its metadata");
            window.Width = 320; window.Height = 224; await Task.Delay(20); window.UpdateLayout(); Mini017AssertBounds(window);
            Assert(window.ArtistText.TextWrapping == TextWrapping.Wrap && window.ArtistText.ActualHeight > window.ArtistText.LineHeight + 1
                && window.ArtistText.Text == artists && window.ArtistText.ToolTip?.ToString() == artists,
                $"artist reflow differs: wrap={window.ArtistText.TextWrapping}, size={window.ArtistText.ActualWidth}x{window.ArtistText.ActualHeight}, maxWidth={window.ArtistText.MaxWidth}, line={window.ArtistText.LineHeight}, text={window.ArtistText.Text == artists}, tooltip={window.ArtistText.ToolTip?.ToString() == artists}");
            window.Height = 440; await Task.Delay(20); window.UpdateLayout(); Mini017AssertBounds(window);
            Assert(Mini017Bounds(window.CoverImage, window).Bottom < Mini017Bounds(window.TitleText, window).Top
                && window.TitleText.TextAlignment == TextAlignment.Center && window.CoverImage.ActualWidth > 180,
                "portrait player did not move the artwork above its metadata");
            MiniCapture(window, "mini-017-reflow-portrait.png");
            Assert(window.SeekSlider.Value == 61 && window.VolumeSlider.Value == 64 && commands.Count == 0 && seeks.Count == 0 && volumes.Count == 0,
                "geometry changes mutated playback, seek or volume");
        }
        finally { window.Close(); }
    }

    private static void Mini017AssertBounds(MiniPlayerWindow window)
    {
        var tools = (StackPanel)VisualTreeHelper.GetParent(window.ArtworkButton);
        var header = (Grid)VisualTreeHelper.GetParent(tools);
        var topEdges = new List<double>();
        foreach (var button in tools.Children.OfType<Control>())
        {
            var inTools = new Rect(button.TranslatePoint(new Point(), tools), button.RenderSize);
            var inHeader = new Rect(button.TranslatePoint(new Point(), header), button.RenderSize);
            Assert(inTools.Top >= -.01 && inTools.Bottom <= tools.ActualHeight + .01
                && inHeader.Top >= -.01 && inHeader.Bottom <= header.ActualHeight + .01,
                $"mini header clips {AutomationProperties.GetName(button)}: {inTools} inside toolbar height {tools.ActualHeight}");
            topEdges.Add(inHeader.Top);
        }
        Assert(topEdges.Max() - topEdges.Min() < 1, "mini header buttons are not vertically aligned");
        var controls = new FrameworkElement[] { window.TitleText, window.ArtistText, window.LikeButton, window.SeekSlider, window.VolumeSlider,
            window.PlayButton, window.PositionText, window.DurationText, window.PinButton, window.ArtworkButton,
            MiniButton(window, "Предыдущий трек"), MiniButton(window, "Следующий трек"), MiniButton(window, "Открыть основное окно"), MiniButton(window, "Закрыть мини-плеер") };
        // WPF Image renders 0x0 for a null source; the containing square also
        // hosts the empty-state glyph and remains the visible artwork surface.
        if (window.IsArtworkVisible)
        {
            controls = controls.Append((FrameworkElement)VisualTreeHelper.GetParent(window.CoverImage)).ToArray();
            if (window.CoverImage.Source is not null) controls = controls.Append(window.CoverImage).ToArray();
        }
        foreach (var control in controls)
        {
            var bounds = Mini017Bounds(control, window);
            Assert(control.ActualWidth > 0 && control.ActualHeight > 0 && bounds.Left >= 5 && bounds.Top >= 5
                && bounds.Right <= window.ActualWidth - 5 && bounds.Bottom <= window.ActualHeight - 5,
                $"mini control is outside the resize-safe content: {control.GetType().Name} {AutomationProperties.GetName(control)} {bounds}");
        }
        foreach (var pair in new (FrameworkElement, FrameworkElement)[] { (window.VolumeSlider, MiniButton(window, "Предыдущий трек")),
            (window.SeekSlider, (FrameworkElement)window.TitleText), (window.PlayButton, (FrameworkElement)window.PositionText),
            (window.TitleText, (FrameworkElement)window.LikeButton), (window.ArtistText, (FrameworkElement)window.LikeButton) })
            Assert(!Mini017Bounds(pair.Item1, window).IntersectsWith(Mini017Bounds(pair.Item2, window)), "mini layout overlaps unrelated controls");
        Assert(window.TitleText.ActualWidth >= 100 && window.SeekSlider.ActualWidth >= window.ActualWidth - 36 && window.VolumeSlider.ActualWidth >= 38,
            "minimum layout no longer leaves readable metadata or usable sliders");
    }

    private static Rect Mini017Bounds(FrameworkElement element, Window window) => new(element.TranslatePoint(new Point(), window), element.RenderSize);
    private static nint Mini017ScreenPoint(int x, int y) => (nint)((y & 0xffff) << 16 | (x & 0xffff));
    [StructLayout(LayoutKind.Sequential)] private struct Mini017Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct Mini017Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct Mini017MinMaxInfo { public Mini017Point Reserved, MaxSize, MaxPosition, MinTrackSize, MaxTrackSize; }
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int Mini017GetWindowLong(nint handle, int index);
    [DllImport("user32.dll", EntryPoint = "GetWindowRect")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool Mini017GetWindowRect(nint handle, out Mini017Rect rect);
    [DllImport("user32.dll", EntryPoint = "SendMessageW")] private static extern nint Mini017SendMessage(nint handle, uint message, nint wParam, nint lParam);
    [DllImport("user32.dll", EntryPoint = "SendMessageW")] private static extern nint Mini017GetMinMax(nint handle, uint message, nint wParam, ref Mini017MinMaxInfo limits);
    [DllImport("user32.dll", EntryPoint = "SetWindowPos")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool Mini017SetWindowPos(nint handle, nint after, int x, int y, int width, int height, uint flags);
}
