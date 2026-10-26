using System.ComponentModel;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using YMusic.Native;

internal static partial class Program
{
    private static async Task CheckFullscreenRapid040()
    {
        await Run("FullscreenRapid040: tooltip safe area never references a destroyed fullscreen HWND", Frp040Tooltip);
        await Run("FullscreenRapid040: open context menu closes before its owner moves to another HWND", Frp040Menu);
        await Run("FullscreenRapid040: 48 F11 reversals with live tooltips preserve owner and release clocks", Frp040Stress);
        await Run("FullscreenRapid040: external native close with a live tooltip restores the reusable surface", Frp040ExternalClose);
    }

    private const BindingFlags Frp040Flags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private static object Frp040Service => typeof(ToolTip).Assembly.GetType("System.Windows.Controls.PopupControlService")!
        .GetProperty("Current", Frp040Flags)!.GetValue(null)!;
    private static object? Frp040Property(string name) => Frp040Service.GetType().GetProperty(name, Frp040Flags)!.GetValue(Frp040Service);
    private static object? Frp040ServiceCall(string method, params object?[] args) => Frp040Service.GetType().GetMethod(method, Frp040Flags)!.Invoke(Frp040Service, args);
    private static void Frp040Dismiss() => Frp040ServiceCall("DismissToolTips");
    private static async Task Frp040ShowTip(FrameworkElement owner)
    {
        Frp040Dismiss();
        // Test-only entry into WPF's real service: the same implicit ToolTip,
        // native popup, safe area, owner and timer as an actual hover. No input
        // desktop, mouse cursor or user keyboard state is changed.
        Frp040ServiceCall("ShowToolTip", owner, false);
        await Until(() => Frp040Property("CurrentToolTip") is ToolTip { IsOpen: true }
            && Frp040Property("SafeArea") is not null);
    }

    private static void Frp040Native(Fr0317Fixture f)
    {
        Fr0317Set(f.Window, "_fullscreenBoundsProvider", new Func<nint, FullscreenPixelBounds>(_ => new(-18000, -4000, -16720, -3140)));
        f.Window.Show(); f.Window.Left = -18000; f.Window.Top = -4000; f.Window.UpdateLayout();
        Fr0317Call(f.Window, "SetExpandedPlayerVisible", true, true);
    }

    private static async Task Frp040Tooltip()
    {
        using var f = new Fr0317Fixture("rapid-tooltip"); Frp040Native(f);
        await Dispatcher.Yield(DispatcherPriority.Loaded);
        var host = Fh040Window(f.Window); var handle = new WindowInteropHelper(host).Handle;
        var owner = Fr0317Get<Button>(f.Window, "_expandedPlayerCloseButton");
        await Frp040ShowTip(owner);
        string? error = null; bool tooltipOpen = false, safeAreaPresent = false;
        try
        {
            Fr0317Call(f.Window, "CloseExpandedPlayer");
            tooltipOpen = Frp040Property("CurrentToolTip") is ToolTip { IsOpen: true };
            safeAreaPresent = Frp040Property("SafeArea") is not null;
            try { Frp040ServiceCall("MouseHasLeftSafeArea"); }
            catch (TargetInvocationException ex) { error = ex.InnerException?.ToString(); }
            File.WriteAllText(Path.Combine(_directory, "fullscreen-rapid040-tooltip.json"), JsonSerializer.Serialize(new
            {
                oldHandleAlive = IsWindowRapid040(handle), tooltipOpen, safeAreaPresent, error,
                nativeWindowsOnInactiveDesktop = true, desktopInputUsed = false,
                serviceInjectionTestOnly = true, privateFrameworkAccessInProduct = false
            }, new JsonSerializerOptions { WriteIndented = true }));
            Assert(error is null, "recreated actual stale tooltip safe-area exception: " + error);
            Assert(!tooltipOpen && (!safeAreaPresent || IsWindowRapid040(handle)), "fullscreen teardown retained an open tooltip or destroyed its cached source");
        }
        finally { Frp040Dismiss(); }
    }

    private static async Task Frp040Menu()
    {
        using var f = new Fr0317Fixture("rapid-menu"); Frp040Native(f);
        await Dispatcher.Yield(DispatcherPriority.Loaded);
        var owner = Fr0317Get<Button>(f.Window, "_expandedPlayerCloseButton");
        var menu = new ContextMenu { PlacementTarget = owner };
        menu.Items.Add(new MenuItem { Header = "Private fixture" }); owner.ContextMenu = menu;
        menu.IsOpen = true; await Dispatcher.Yield(DispatcherPriority.Loaded);
        try
        {
            Assert(menu.IsOpen, "fixture menu did not open");
            Fr0317Call(f.Window, "CloseExpandedPlayer");
            Assert(!menu.IsOpen, "moving fullscreen controls left their menu open on the old HWND");
        }
        finally { menu.IsOpen = false; Frp040Dismiss(); }
    }

    private static async Task Frp040Stress()
    {
        using var f = new Fr0317Fixture("rapid-stress"); Frp040Native(f);
        var placement = Fh040Placement(f.Window); var countBefore = Application.Current.Windows.Count;
        var errors = new List<string>(); var ownerHandle = new WindowInteropHelper(f.Window).Handle;
        var reusableHandle = new WindowInteropHelper(Fh040Window(f.Window)).Handle;
        try
        {
            for (var i = 0; i < 48; i++)
            {
                if (Fr0317Get<object?>(f.Window, "_expandedFullscreenHost") is null)
                    Frp040F11(f.Window);
                await Dispatcher.Yield(DispatcherPriority.Loaded);
                var host = Fh040Window(f.Window);
                Assert(new WindowInteropHelper(host).Handle == reusableHandle, "F11 recreated the native tooltip source");
                await Frp040ShowTip(Fr0317Get<Button>(f.Window, "_expandedPlayerCloseButton"));
                // Reverse the entry while it is in flight, then reopen partway
                // through closing. The final F11 completes the close.
                Frp040F11(host);
                if (i % 3 == 0)
                {
                    Frp040F11(host); await Dispatcher.Yield(DispatcherPriority.Loaded);
                    Frp040F11(Fh040Window(f.Window));
                }
                if (Fr0317Get<object?>(f.Window, "_expandedCollapse") is { } closing)
                {
                    var clock = (ClockGroup)closing.GetType().GetProperty("Completion")!.GetValue(closing)!;
                    clock.Controller!.SeekAlignedToLastTick(TimeSpan.FromMilliseconds(380), TimeSeekOrigin.BeginTime);
                    await Dispatcher.Yield(DispatcherPriority.Background);
                }
                try { Frp040ServiceCall("MouseHasLeftSafeArea"); }
                catch (TargetInvocationException ex) { errors.Add(ex.InnerException?.ToString() ?? ex.ToString()); }
                Assert(Fr0317Get<object?>(f.Window, "_expandedFullscreenHost") is null
                    && Fr0317Get<object?>(f.Window, "_expandedCollapse") is null
                    && Fr0317Get<object?>(f.Window, "_expandedReveal") is null, "rapid F11 leaked a fullscreen host or clock at " + i);
                Assert(Fh040Placement(f.Window) == placement && IsWindowRapid040(ownerHandle), "rapid F11 changed or destroyed owner");
                Assert(Frp040Property("CurrentToolTip") is not ToolTip { IsOpen: true }, "rapid F11 left a visible tooltip behind");
                Assert(Application.Current.Windows.Count < countBefore + 2, "rapid F11 accumulated windows");
            }
            Assert(errors.Count == 0, "rapid F11 retained invalid tooltip HWNDs: " + string.Join("; ", errors.Take(3)));
            File.WriteAllText(Path.Combine(_directory, "fullscreen-rapid040-stress.json"), JsonSerializer.Serialize(new
            { cycles = 48, errors, inputDesktopUsed = false, physicalMonitorTested = false, originalPlacementPreserved = true }, new JsonSerializerOptions { WriteIndented = true }));
        }
        finally { Frp040Dismiss(); }
    }

    private static void Frp040F11(Window window)
    {
        var args = new KeyEventArgs(new Pk040Keyboard(ModifierKeys.None), PresentationSource.FromVisual(window), Environment.TickCount, Key.F11)
        { RoutedEvent = Keyboard.PreviewKeyDownEvent };
        window.RaiseEvent(args); Assert(args.Handled, "F11 was not routed to the active player");
    }

    private static async Task Frp040ExternalClose()
    {
        using var f = new Fr0317Fixture("rapid-external-close"); Frp040Native(f);
        await Dispatcher.Yield(DispatcherPriority.Loaded);
        var stage = Fr0317Get<System.Windows.Controls.Grid>(f.Window, "_expandedRevealStage");
        await Frp040ShowTip(Fr0317Get<Button>(f.Window, "_expandedPlayerCloseButton"));
        try
        {
            Fh040Window(f.Window).Close();
            if (Fr0317Get<object?>(f.Window, "_expandedCollapse") is { } closing)
            {
                var clock = (ClockGroup)closing.GetType().GetProperty("Completion")!.GetValue(closing)!;
                clock.Controller!.SeekAlignedToLastTick(TimeSpan.FromMilliseconds(380), TimeSeekOrigin.BeginTime);
                await Dispatcher.Yield(DispatcherPriority.Background);
            }
            Assert(Fr0317Get<object?>(f.Window, "_expandedFullscreenHost") is null
                && ReferenceEquals(((ContentControl)f.Window.FindName("ExpandedPlayerHost")).Content, stage),
                "externally closed host failed to restore its surface");
            Frp040ServiceCall("MouseHasLeftSafeArea");
            Frp040F11(f.Window); await Dispatcher.Yield(DispatcherPriority.Loaded);
            Assert(Fh040Window(f.Window).IsVisible, "cannot reopen after external close");
        }
        finally { Frp040Dismiss(); }
    }

    [DllImport("user32.dll", EntryPoint = "IsWindow", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowRapid040(nint window);
}
