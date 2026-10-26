using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using YMusic.Native;

internal static partial class Program
{
    private static bool _miniFixtureConfigured;
    private static async Task CheckMiniPlayer()
    {
        if (!_miniFixtureConfigured)
        {
            System.Windows.Window.ShowActivatedProperty.OverrideMetadata(typeof(MiniPlayerWindow), new FrameworkPropertyMetadata(false));
            EventManager.RegisterClassHandler(typeof(MiniPlayerWindow), FrameworkElement.LoadedEvent, new RoutedEventHandler((sender, args) =>
            {
                if (sender is not MiniPlayerWindow window || !ReferenceEquals(args.OriginalSource, window)) return;
                window.ShowInTaskbar = false; window.WindowStartupLocation = WindowStartupLocation.Manual; window.Left = -16000; window.Top = 0;
            }));
            _miniFixtureConfigured = true;
        }
        await Run("Mini player: synchronized artwork, metadata, play, liked and empty states with bounded layout", MiniStateChecks);
        await Run("Mini player: pointer and keyboard seek commit once; progress, cancel and track guards", MiniSeekChecks);
        await Run("Mini player: occurrence keys cancel old seeks and keyboard blur/deactivate completes once", MiniOccurrenceChecks);
        await Run("Mini player: volume synchronization has no feedback and buttons retain exact commands", MiniCommandChecks);
        await Run("Mini player: shell forwards state once, reuses, reopens and disposes its own windows", MiniLifecycleChecks);
        await Run("Mini player: Yamuse frozen icon and owned DPI-sized HICON cleanup", MiniIconChecks);
        await Run("Mini player 017: native edge/corner resize and minimum window constraints", Mini017NativeResizeChecks);
        await Run("Mini player 017: minimum, normal, wide and tall layouts with visible/hidden artwork", Mini017ResponsiveChecks);
        await Run("Mini player 017: artwork toggle preserves synchronized state and active seek without callbacks", Mini017ArtworkChecks);
        await Run("Mini player 017: accent resources remain live across pin, liked and playback controls", Mini017AccentChecks);
        await Run("Mini player 017: artwork toggle shrinks native footprint and restores size without undoing user resize", Mini017CompactFootprintChecks);
        await Run("Mini player 017: timeline stretches while artwork and wrapped artists reflow without playback callbacks", Mini017ReflowChecks);
    }

    private static async Task MiniStateChecks()
    {
        var window = MiniFixtureWindow();
        try
        {
            var cover = MiniCover();
            window.Update("Трек с очень длинным названием для компактного окна", "Исполнитель · альбом", true, true, false);
            window.UpdateDetails(cover, 61, 237, true, true, true, 64);
            await Task.Delay(30); window.UpdateLayout();
            Assert(window.Title.Contains(AppBranding.Name) && window.TitleText.Text.StartsWith("Трек с очень")
                && window.ArtistText.Text == "Исполнитель · альбом" && ReferenceEquals(window.CoverImage.Source, cover), "mini state metadata/cover differs");
            Assert(AutomationProperties.GetName(window.PlayButton) == "Пауза" && window.PlayButton.IsEnabled
                && ((TextBlock)window.LikeButton.Content).Text == "\uEB52" && window.LikeButton.IsEnabled, "mini playback/liked state differs");
            Assert(window.SeekSlider.Value == 61 && window.SeekSlider.Maximum == 237 && window.SeekSlider.IsEnabled
                && window.PositionText.Text == "1:01" && window.DurationText.Text == "3:57", "mini timeline/time differs");
            Assert(!MiniButton(window, "Предыдущий трек").IsEnabled && MiniButton(window, "Следующий трек").IsEnabled, "transport availability differs");
            foreach (var element in new FrameworkElement[] { window.CoverImage, window.TitleText, window.ArtistText, window.LikeButton, window.SeekSlider, window.VolumeSlider, window.PlayButton, window.PositionText, window.DurationText, window.PinButton })
            {
                var rect = new Rect(element.TranslatePoint(new Point(), window), element.RenderSize);
                Assert(element.ActualWidth > 0 && element.ActualHeight > 0 && rect.Left >= 0 && rect.Top >= 0
                    && rect.Right <= window.ActualWidth + 1 && rect.Bottom <= window.ActualHeight + 1, "mini control is clipped: " + element.GetType().Name);
            }
            MiniCapture(window, "mini-playing.png");
            window.Update("Новый трек", "Другой исполнитель", false, false, true);
            Assert(window.CoverImage.Source is null, "changed metadata retained the previous track cover");
            window.UpdateDetails(null, 0, 0, false, false, false, 64);
            Assert(AutomationProperties.GetName(window.PlayButton) == "Играть" && !window.SeekSlider.IsEnabled && !window.LikeButton.IsEnabled, "new track readiness differs");
            window.Update("", "", false, true, true); window.UpdateDetails(cover, 7, 180, true, true, true, 64);
            Assert(window.CoverImage.Source is null && !window.PlayButton.IsEnabled && !window.SeekSlider.IsEnabled
                && !window.LikeButton.IsEnabled && !MiniButton(window, "Следующий трек").IsEnabled, "empty mini retained a playable track");
            window.UpdateLayout(); MiniCapture(window, "mini-empty.png");
        }
        finally { window.Close(); }
    }

    private static async Task MiniSeekChecks()
    {
        var window = MiniFixtureWindow();
        try
        {
            window.Update("Seek fixture", "Fixture", false, true, true);
            var commits = new List<double>(); window.SeekRequested += commits.Add;
            window.UpdateDetails(null, 25, 200, true, false, true, 70);
            window.UpdateDetails(null, 60, 200, true, false, true, 70);
            Assert(commits.Count == 0 && window.SeekSlider.Value == 60, "progress caused a seek callback");
            MiniDragStart(window); window.SeekSlider.Value = 92;
            window.UpdateDetails(null, 65, 200, true, false, true, 70);
            Assert(window.IsSeeking && window.SeekSlider.Value == 92 && window.PositionText.Text == "1:32", "progress overwrote the active pointer seek");
            MiniDragEnd(window, false);
            window.SeekSlider.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = Mouse.PreviewMouseUpEvent });
            Assert(!window.IsSeeking && commits.SequenceEqual(new[] { 92d }), "pointer completion did not commit exactly once");
            MiniDragStart(window); window.SeekSlider.Value = 120; MiniDragEnd(window, true);
            Assert(commits.Count == 1 && !window.IsSeeking && window.SeekSlider.Value == 92, "canceled drag committed a seek");
            MiniKey(window, Key.Right, true); window.SeekSlider.Value = 70;
            window.UpdateDetails(null, 67, 200, true, false, true, 70);
            Assert(window.SeekSlider.Value == 70, "progress overwrote keyboard interaction"); MiniKey(window, Key.Right, false);
            Assert(commits.SequenceEqual(new[] { 92d, 70d }) && !window.IsSeeking, "keyboard did not commit once on release");
            MiniDragStart(window); window.SeekSlider.Value = 100; window.UpdateDetails(null, 68, 200, false, false, true, 70); MiniDragEnd(window, false);
            Assert(commits.Count == 2 && !window.SeekSlider.IsEnabled && window.SeekSlider.Value == 68, "disabled seek committed or retained its drag");
            window.UpdateDetails(null, 69, 200, true, false, true, 70); MiniDragStart(window); window.SeekSlider.Value = 110;
            window.Update("Different track", "Fixture", false, true, true); window.UpdateDetails(null, 10, 120, true, false, true, 70); MiniDragEnd(window, false);
            Assert(commits.Count == 2 && window.SeekSlider.Value == 10 && !window.IsSeeking, "old track drag sought the replacement track");
            window.UpdateDetails(null, double.NaN, double.PositiveInfinity, true, false, true, 70);
            Assert(!window.SeekSlider.IsEnabled && window.SeekSlider.Value == 0 && commits.Count == 2, "non-finite duration/position passed the guard");
            window.UpdateDetails(null, 20, 120, true, false, true, 70); window.SeekSlider.Value = 33;
            window.SeekSlider.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = Mouse.PreviewMouseDownEvent, Handled = true });
            window.UpdateDetails(null, 21, 120, true, false, true, 70);
            Assert(window.IsSeeking && window.SeekSlider.Value == 33, "handled move-to-point did not retain its gesture");
            window.SeekSlider.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = Mouse.PreviewMouseUpEvent, Handled = true });
            Assert(commits.Count == 3 && commits[^1] == 33 && !window.IsSeeking, "handled point release did not commit exactly once");
            MiniDragStart(window); window.SeekSlider.Value = 55;
            window.SeekSlider.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount) { RoutedEvent = Mouse.LostMouseCaptureEvent });
            Assert(!window.IsSeeking && commits.Count == 3 && window.SeekSlider.Value == 33, "capture loss retained or committed its abandoned gesture");
            await Task.CompletedTask;
        }
        finally { window.Close(); }
    }

    private static async Task MiniOccurrenceChecks()
    {
        var window = MiniFixtureWindow();
        try
        {
            window.Update("Same title", "Same artist", true, true, true);
            var commits = new List<double>(); window.SeekRequested += commits.Add;
            window.UpdateDetails(null, 20, 180, true, false, true, 70, "1:request-42");
            MiniDragStart(window); window.SeekSlider.Value = 100;
            window.UpdateDetails(null, 5, 180, true, false, true, 70, "2:request-42");
            Assert(!window.IsSeeking && window.SeekSlider.Value == 5 && commits.Count == 0, "same-title occurrence retained the old drag");
            MiniDragEnd(window, false); Assert(commits.Count == 0, "late release sought the new occurrence");
            MiniDragStart(window); window.SeekSlider.Value = 45; MiniDragEnd(window, false);
            Assert(commits.SequenceEqual(new[] { 45d }), "new occurrence's own gesture did not commit once");
            MiniKey(window, Key.Right, true); window.SeekSlider.Value = 55;
            window.UpdateDetails(null, 6, 180, true, false, true, 70, "3:request-42"); MiniKey(window, Key.Right, false);
            Assert(commits.Count == 1 && !window.IsSeeking && window.SeekSlider.Value == 6, "old keyboard gesture reached the new occurrence");
            MiniKey(window, Key.Right, true); window.SeekSlider.Value = 30;
            window.UpdateDetails(null, 7, 180, true, false, true, 70, "3:request-42");
            window.SeekSlider.RaiseEvent(new KeyboardFocusChangedEventArgs(Keyboard.PrimaryDevice, Environment.TickCount, window.SeekSlider, window.PlayButton) { RoutedEvent = Keyboard.LostKeyboardFocusEvent });
            MiniKey(window, Key.Right, false);
            Assert(commits.SequenceEqual(new[] { 45d, 30d }) && !window.IsSeeking, "keyboard blur left a gesture active or duplicated its commit");
            MiniKey(window, Key.Right, true); window.SeekSlider.Value = 40;
            MiniDeactivate(window); MiniKey(window, Key.Right, false);
            Assert(commits.SequenceEqual(new[] { 45d, 30d, 40d }) && !window.IsSeeking, "deactivation left a keyboard gesture active or duplicated its commit");
            MiniDragStart(window); window.SeekSlider.Value = 60; MiniDeactivate(window); MiniDragEnd(window, false);
            Assert(commits.Count == 3 && !window.IsSeeking && window.SeekSlider.Value == 40, "deactivation committed an abandoned pointer gesture");
            await Task.CompletedTask;
        }
        finally { window.Close(); }
    }

    private static async Task MiniCommandChecks()
    {
        var window = MiniFixtureWindow();
        try
        {
            window.Update("Commands fixture", "Fixture", false, true, true);
            var volume = new List<double>(); var commands = new List<ShellAction>(); var opened = 0;
            window.VolumeRequested += volume.Add; window.Command += commands.Add; window.ShowMainRequested += () => opened++;
            window.UpdateDetails(null, 0, 180, true, false, true, 30); window.UpdateDetails(null, 1, 180, true, false, true, 35);
            Assert(volume.Count == 0 && window.VolumeSlider.Value == 35, "state volume synchronization fed back into the player");
            window.VolumeSlider.Value = 84; Assert(volume.SequenceEqual(new[] { 84d }), "volume input did not forward its exact percentage");
            window.UpdateDetails(null, 2, 180, true, false, true, 200);
            Assert(volume.Count == 1 && window.VolumeSlider.Value == 100, "state volume clamp produced a callback");
            MiniClick(window.PlayButton); MiniClick(MiniButton(window, "Предыдущий трек")); MiniClick(MiniButton(window, "Следующий трек")); MiniClick(window.LikeButton);
            Assert(commands.SequenceEqual(new[] { ShellAction.PlayPause, ShellAction.Previous, ShellAction.Next, ShellAction.ToggleLike }), "mini button command sequence differs");
            MiniClick(MiniButton(window, "Открыть основное окно")); Assert(opened == 1, "open main action missing");
            window.PinButton.IsChecked = true; Assert(window.Topmost, "pin did not enable topmost"); window.PinButton.IsChecked = false; Assert(!window.Topmost, "pin did not disable topmost");
            window.Update("", "", false, false, false); MiniClick(window.PlayButton); MiniClick(window.LikeButton);
            Assert(commands.Count == 4, "disabled no-track button dispatched an action");
            await Task.CompletedTask;
        }
        finally { window.Close(); }
    }

    private static async Task MiniLifecycleChecks()
    {
        var owner = new Window { Width = 300, Height = 180, Left = -16000, Top = 0, ShowActivated = false, ShowInTaskbar = false };
        owner.Show();
        using var shell = new DesktopShell(owner);
        try
        {
            var commands = new List<ShellAction>(); var seeks = new List<double>(); var volumes = new List<double>();
            shell.Command += commands.Add; shell.SeekRequested += seeks.Add; shell.VolumeRequested += volumes.Add;
            shell.Update("Shell fixture", "Artist", false, true, true); var cover = MiniCover(); shell.UpdateDetails(cover, 20, 180, true, true, true, 50, "shell-1:request-42");
            shell.ShowMiniPlayer(); await Task.Delay(20); var first = MiniShellWindow(shell) ?? throw new InvalidOperationException("mini did not open");
            Assert(ReferenceEquals(first.CoverImage.Source, cover) && first.SeekSlider.Value == 20, "shell did not supply stored detail state on first show");
            shell.ShowMiniPlayer(); Assert(ReferenceEquals(first, MiniShellWindow(shell)), "repeated ShowMiniPlayer created another window");
            MiniClick(first.PlayButton); MiniDragStart(first); first.SeekSlider.Value = 44; MiniDragEnd(first, false); first.VolumeSlider.Value = 82;
            Assert(commands.SequenceEqual(new[] { ShellAction.PlayPause }) && seeks.SequenceEqual(new[] { 44d }) && volumes.SequenceEqual(new[] { 82d }), "shell forwarded an input more or less than once");
            MiniDragStart(first); first.SeekSlider.Value = 100; shell.UpdateDetails(cover, 5, 180, true, true, true, 82, "shell-2:request-42"); MiniDragEnd(first, false);
            Assert(seeks.Count == 1 && !first.IsSeeking && first.SeekSlider.Value == 5, "shell did not forward an occurrence-key change");
            MiniClick(MiniButton(first, "Закрыть мини-плеер")); Assert(MiniShellWindow(shell) is null && owner.IsVisible, "closing mini hid or closed the main window");
            shell.Update("Reopened fixture", "Artist", true, false, true); shell.UpdateDetails(cover, 45, 180, true, false, true, 82); shell.ShowMiniPlayer(); await Task.Delay(20);
            var second = MiniShellWindow(shell)!; Assert(!ReferenceEquals(first, second) && second.TitleText.Text == "Reopened fixture" && second.SeekSlider.Value == 45, "mini reopen state differs");
            MiniClick(second.PlayButton); Assert(commands.Count == 2, "reopen duplicated the command subscription");
            var icon = (AppBranding.OwnedIcon)typeof(DesktopShell).GetField("_icon", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(shell)!;
            owner.Close(); Assert(icon.IsClosed && MiniShellWindow(shell) is null && !second.IsVisible, "owner close did not clean its icon and mini");
            shell.Dispose(); shell.ShowMiniPlayer(); Assert(MiniShellWindow(shell) is null, "disposed shell reopened a mini window");
        }
        finally { if (owner.IsVisible) owner.Close(); }
    }

    private static async Task MiniIconChecks()
    {
        Assert(AppBranding.Name == "Yamuse" && AppBranding.WindowIcon.IsFrozen && AppBranding.WindowIcon is BitmapSource, "packaged brand icon missing or not frozen");
        var owner = new Window { Width = 100, Height = 100, Left = -16000, Top = 0, ShowActivated = false, ShowInTaskbar = false };
        try
        {
            var handle = new WindowInteropHelper(owner).EnsureHandle();
            var icon = AppBranding.CreateTrayIcon(handle); var native = icon.DangerousGetHandle();
            var readable = MiniGetIconInfo(native, out var info);
            Assert(icon.IsBranded && !icon.IsInvalid && readable, "tray did not receive its owned Yamuse HICON");
            int width;
            try
            {
                _ = MiniGetObject(info.Color, Marshal.SizeOf<MiniBitmap>(), out var bitmap); width = bitmap.Width;
                var expected = Math.Max(16, MiniGetSystemMetricsForDpi(49, MiniGetDpiForWindow(handle)));
                Assert(bitmap.Width == expected && bitmap.Height == expected, "tray icon ignored the current window DPI");
            }
            finally { if (info.Mask != 0) MiniDeleteObject(info.Mask); if (info.Color != 0) MiniDeleteObject(info.Color); }
            icon.Dispose(); Assert(icon.IsClosed && !MiniGetIconInfo(native, out _), "owned HICON remained valid after disposal");
            File.WriteAllText(Path.Combine(_directory, "mini-scope.json"), JsonSerializer.Serialize(new
            {
                title = AppBranding.Name, miniWidth = 484, miniHeight = 300, miniMinimumWidth = 360, miniMinimumHeight = 264, trayIconPixels = width,
                ownedHiconDestroyed = true, frozenPackagedWindowIcon = true, externalNetworkRequests = 0,
                playerOrAudioEngineConstructed = false, userAccountLoaded = false, globalInputUsed = false,
                actualTrayRegistrationTested = false, offscreenWpfWindows = true
            }, new JsonSerializerOptions { WriteIndented = true }));
            await Task.CompletedTask;
        }
        finally { owner.Close(); }
    }

    private static MiniPlayerWindow MiniFixtureWindow() { var window = new MiniPlayerWindow { Left = -16000, Top = 0, WindowStartupLocation = WindowStartupLocation.Manual, ShowActivated = false }; WindowAppearance.Apply(window); window.Show(); window.UpdateLayout(); return window; }
    private static MiniPlayerWindow? MiniShellWindow(DesktopShell shell) => (MiniPlayerWindow?)typeof(DesktopShell).GetField("_mini", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(shell);
    private static void MiniDragStart(MiniPlayerWindow window) => window.SeekSlider.RaiseEvent(new DragStartedEventArgs(0, 0) { RoutedEvent = Thumb.DragStartedEvent });
    private static void MiniDragEnd(MiniPlayerWindow window, bool canceled) => window.SeekSlider.RaiseEvent(new DragCompletedEventArgs(0, 0, canceled) { RoutedEvent = Thumb.DragCompletedEvent });
    private static void MiniKey(MiniPlayerWindow window, Key key, bool down) => window.SeekSlider.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window.SeekSlider), Environment.TickCount, key) { RoutedEvent = down ? Keyboard.PreviewKeyDownEvent : Keyboard.PreviewKeyUpEvent });
    private static void MiniClick(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static void MiniDeactivate(Window window) => typeof(Window).GetMethod("OnDeactivated", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, new object[] { EventArgs.Empty });
    private static Button MiniButton(DependencyObject root, string name) => MiniChildren<Button>(root).Single(button => AutomationProperties.GetName(button) == name);
    private static IEnumerable<T> MiniChildren<T>(DependencyObject root) where T : DependencyObject { for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++) { var child = VisualTreeHelper.GetChild(root, index); if (child is T match) yield return match; foreach (var nested in MiniChildren<T>(child)) yield return nested; } }
    private static ImageSource MiniCover() { var drawing = new DrawingGroup(); drawing.Children.Add(new GeometryDrawing(new LinearGradientBrush(Color.FromRgb(49, 47, 98), Color.FromRgb(228, 127, 103), 45), null, new RectangleGeometry(new Rect(0, 0, 160, 160)))); drawing.Children.Add(new GeometryDrawing(new SolidColorBrush(Color.FromRgb(255, 210, 148)), null, new EllipseGeometry(new Point(108, 57), 31, 31))); var image = new DrawingImage(drawing); image.Freeze(); return image; }
    private static void MiniCapture(Window window, string file) { var dpi = VisualTreeHelper.GetDpi(window); var bitmap = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth * dpi.DpiScaleX), (int)Math.Ceiling(window.ActualHeight * dpi.DpiScaleY), 96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, PixelFormats.Pbgra32); bitmap.Render(window); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var stream = File.Create(Path.Combine(_directory, file)); encoder.Save(stream); }
    [StructLayout(LayoutKind.Sequential)] private struct MiniIconInfo { [MarshalAs(UnmanagedType.Bool)] public bool Icon; public uint XHotspot, YHotspot; public nint Mask, Color; }
    [StructLayout(LayoutKind.Sequential)] private struct MiniBitmap { public int Type, Width, Height, WidthBytes; public ushort Planes, BitsPixel; public nint Bits; }
    [DllImport("user32.dll", EntryPoint = "GetIconInfo")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool MiniGetIconInfo(nint icon, out MiniIconInfo info);
    [DllImport("user32.dll", EntryPoint = "GetDpiForWindow")] private static extern uint MiniGetDpiForWindow(nint window);
    [DllImport("user32.dll", EntryPoint = "GetSystemMetricsForDpi")] private static extern int MiniGetSystemMetricsForDpi(int index, uint dpi);
    [DllImport("gdi32.dll", EntryPoint = "GetObjectW")] private static extern int MiniGetObject(nint value, int count, out MiniBitmap bitmap);
    [DllImport("gdi32.dll", EntryPoint = "DeleteObject")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool MiniDeleteObject(nint value);
}
