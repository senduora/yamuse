using System.ComponentModel;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using YMusic.Native;

internal static partial class Program
{
    private static async Task CheckFullscreenHost040()
    {
        await Run("FullscreenHost040: fullscreen never resizes restyles or changes the main window including maximized", Fh040MainUnchanged);
        await Run("FullscreenHost040: transparent initial frame and cover share the finite reveal in their own surface", Fh040Reveal);
        await Run("FullscreenHost040: native offscreen placement uses exact physical bounds and forwards Escape", Fh040Native);
        await Run("FullscreenHost040: toast reparent close reopen external close and owner close release the surface", Fh040Lifetime);
        await Run("FullscreenHost040: lyrics button follows volume percentage and fullscreen topmost follows activation", Fh040Controls);
    }

    private static Task Fh040MainUnchanged()
    {
        foreach (var state in new[] { WindowState.Normal, WindowState.Maximized, WindowState.Minimized })
        {
            using var f = new Fr0317Fixture("host-state-" + state); var main = f.Window;
            main.WindowState = state;
            var before = Fh040Placement(main); var changes = 0;
            var properties = new[] { System.Windows.Window.WindowStateProperty, System.Windows.Window.WindowStyleProperty,
                System.Windows.Window.ResizeModeProperty, System.Windows.Window.LeftProperty, System.Windows.Window.TopProperty,
                FrameworkElement.WidthProperty, FrameworkElement.HeightProperty };
            var descriptors = properties.Select(property => DependencyPropertyDescriptor.FromProperty(property, typeof(System.Windows.Window))).ToArray();
            EventHandler changed = (_, _) => changes++;
            foreach (var descriptor in descriptors) descriptor.AddValueChanged(main, changed);
            try
            {
                Fr0317Call(main, "SetExpandedPlayerVisible", true, true);
                var window = Fh040Window(main); var stage = Fr0317Get<Grid>(main, "_expandedRevealStage");
                Assert(Fh040Placement(main) == before && changes == 0, "fullscreen changed main placement " + state);
                Assert(window != main && window.WindowStyle == WindowStyle.None && window.WindowState == WindowState.Normal
                    && window.AllowsTransparency && window.Background == Brushes.Transparent && !window.ShowInTaskbar
                    && !window.IsVisible && new WindowInteropHelper(window).Handle == IntPtr.Zero, "not a dedicated transparent never-shown surface");
                Assert(ReferenceEquals(stage.Parent, window.Content) && ((ContentControl)main.FindName("ExpandedPlayerHost")).Content is null,
                    "surface still overlays the resized main tree");
                Fr0317Call(main, "CloseExpandedPlayer");
                Assert(Fh040Placement(main) == before && changes == 0 && ReferenceEquals(((ContentControl)main.FindName("ExpandedPlayerHost")).Content, stage),
                    "exit changed main placement or lost the reusable surface");
            }
            finally { foreach (var descriptor in descriptors) descriptor.RemoveValueChanged(main, changed); }
        }
        return Task.CompletedTask;
    }

    private static async Task Fh040Reveal()
    {
        using var f = new Fr0317Fixture("host-reveal"); var main = f.Window;
        Fr0317Call(main, "SetExpandedPlayerVisible", true, true);
        var window = Fh040Window(main); var stage = Fr0317Get<Grid>(main, "_expandedRevealStage");
        var root = (Grid)window.Content; var cover = Fr0317Get<Button>(main, "_expandedPlayerCoverButton");
        if (Motion.CanAnimate)
        {
            Assert(stage.Opacity == 0 && window.Background == Brushes.Transparent && root.Background == Brushes.Transparent,
                "opaque host or stage flashes before the first frame");
            await Dispatcher.Yield(DispatcherPriority.Loaded);
            var reveal = Fr0317Get<object?>(main, "_expandedReveal");
            Assert(reveal is not null, "moving the surface cancelled its own opening");
            var clock = (System.Windows.Media.Animation.ClockGroup)reveal!.GetType().GetProperty("Completion")!.GetValue(reveal)!;
            clock.Controller!.SeekAlignedToLastTick(TimeSpan.FromMilliseconds(190), System.Windows.Media.Animation.TimeSeekOrigin.BeginTime);
            Assert(Math.Abs(stage.Opacity - .5) < .001, "dedicated window lost the common half-opacity frame");
        }
        Fr0317Call(main, "CloseExpandedPlayer");
        Assert(stage.Opacity == 1 && cover.Opacity == 1 && !stage.HasAnimatedProperties
            && Fr0317Get<object?>(main, "_expandedFullscreenHost") is null, "close retained the fullscreen window or clock");
    }

    private static async Task Fh040Native()
    {
        using var f = new Fr0317Fixture("host-native-offscreen"); var main = f.Window;
        var target = new FullscreenPixelBounds(-18000, -4000, -16720, -3140);
        Fr0317Set(main, "_fullscreenBoundsProvider", new Func<nint, FullscreenPixelBounds>(_ => target));
        main.ShowActivated = false; main.ShowInTaskbar = false;
        main.Left = -18000; main.Top = -4000; main.Show();
        main.Left = -18000; main.Top = -4000; main.UpdateLayout();
        var ownerHandle = new WindowInteropHelper(main).Handle;
        Assert(GetWindowRect040(ownerHandle, out var before), "cannot read private owner bounds");
        Fr0317Call(main, "SetExpandedPlayerVisible", true, true);
        var window = Fh040Window(main); var handle = new WindowInteropHelper(window).Handle;
        await Dispatcher.Yield(DispatcherPriority.Loaded);
        Assert(handle != 0 && GetWindowRect040(handle, out var actual) && actual.Bounds == target, "physical fullscreen bounds were clipped to a work area or converted twice");
        Assert(GetWindowRect040(ownerHandle, out var after) && before.Bounds == after.Bounds, "native fullscreen resized the owner HWND");
        var source = PresentationSource.FromVisual(window);
        var escape = new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, Key.Escape) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
        window.RaiseEvent(escape);
        if (Fr0317Get<object?>(main, "_expandedCollapse") is { } closing)
        {
            var clock = (System.Windows.Media.Animation.ClockGroup)closing.GetType().GetProperty("Completion")!.GetValue(closing)!;
            clock.Controller!.SeekAlignedToLastTick(TimeSpan.FromMilliseconds(380), System.Windows.Media.Animation.TimeSeekOrigin.BeginTime);
            await Dispatcher.Yield(DispatcherPriority.Background);
        }
        Assert(escape.Handled && Fr0317Get<object?>(main, "_expandedFullscreenHost") is null
            && GetWindowRect040(ownerHandle, out after) && before.Bounds == after.Bounds, "Escape did not restore the untouched main window");
        File.WriteAllText(Path.Combine(_directory, "fullscreen-host040-native.json"), JsonSerializer.Serialize(new
        {
            target, ownerBefore = before.Bounds, ownerAfter = after.Bounds,
            fixtureOffscreen = true, activated = false, desktopInputUsed = false,
            actualPhysicalMonitorCovered = false, monitorBoundsProviderInjected = true
        }, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static Task Fh040Lifetime()
    {
        using var f = new Fr0317Fixture("host-lifetime"); var main = f.Window;
        var toast = (Border)main.FindName("StatusNotification"); var parent = (Panel)toast.Parent;
        var index = parent.Children.IndexOf(toast);
        for (var pass = 0; pass < 3; pass++)
        {
            Fr0317Call(main, "SetExpandedPlayerVisible", true, true);
            var window = Fh040Window(main); var root = (Grid)window.Content;
            Assert(ReferenceEquals(toast.Parent, root) && root.Children.Count == 2, "toast was cloned or left behind the fullscreen window");
            if (pass == 1) window.Close(); else Fr0317Call(main, "CloseExpandedPlayer");
            Assert(ReferenceEquals(toast.Parent, parent) && parent.Children.IndexOf(toast) == index
                && Fr0317Get<object?>(main, "_expandedFullscreenHost") is null && window.Content is null,
                "close did not restore toast ownership or retained detached content");
        }
        Fr0317Call(main, "SetExpandedPlayerVisible", true, true); main.Close();
        Assert(Fr0317Get<object?>(main, "_expandedFullscreenHost") is null && Fr0317Get<object?>(main, "_expandedReveal") is null,
            "owner close left a fullscreen window or clock alive");
        return Task.CompletedTask;
    }

    private static Task Fh040Controls()
    {
        using var f = new Fr0317Fixture("host-controls"); var main = f.Window;
        Fr0317Call(main, "SetExpandedPlayerVisible", true, true);
        var window = Fh040Window(main); var lyrics = Fr0317Get<Button>(main, "_expandedPlayerLyricsButton");
        var row = (StackPanel)lyrics.Parent;
        Assert(ReferenceEquals(row.Children[^1], lyrics) && row.Children[^2] is TextBlock,
            "lyrics remains before queue rather than after volume percentage");
        Fr0317Call(main, "ExpandedFullscreenActivated", window, EventArgs.Empty);
        Assert(window.Topmost, "active fullscreen does not cover a topmost taskbar");
        Fr0317Call(main, "ExpandedFullscreenDeactivated", window, EventArgs.Empty);
        Assert(!window.Topmost, "inactive fullscreen blocks other applications");
        return Task.CompletedTask;
    }

    private static System.Windows.Window Fh040Window(MainWindow main)
    {
        var host = Fr0317Get<object>(main, "_expandedFullscreenHost");
        return (System.Windows.Window)host.GetType().GetProperty("Window", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(host)!;
    }
    private static (double, double, double, double, WindowState, WindowStyle, ResizeMode) Fh040Placement(MainWindow window) =>
        (window.Left, window.Top, window.Width, window.Height, window.WindowState, window.WindowStyle, window.ResizeMode);
    [StructLayout(LayoutKind.Sequential)]
    private struct Rect040
    {
        internal int Left, Top, Right, Bottom;
        internal FullscreenPixelBounds Bounds => new(Left, Top, Right, Bottom);
    }
    [DllImport("user32.dll", EntryPoint = "GetWindowRect", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect040(nint window, out Rect040 bounds);
}
