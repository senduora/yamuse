using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace YMusic.Native;

public partial class MainWindow
{
    private ExpandedFullscreenHost? _expandedFullscreenHost;
    private Window? _expandedFullscreenWindow;
    private Func<nint, FullscreenPixelBounds>? _fullscreenBoundsProvider = null;

    private sealed class ExpandedFullscreenHost(Window window, Grid root, Panel? toastParent, int toastIndex)
    {
        internal Window Window { get; } = window;
        internal Grid Root { get; } = root;
        internal Panel? ToastParent { get; } = toastParent;
        internal int ToastIndex { get; } = toastIndex;
        internal HwndSource? Source;
        internal HwndSourceHook? Hook;
        internal DispatcherOperation? BoundsRefresh;
    }

    private void CreateExpandedFullscreenHost()
    {
        if (_expandedFullscreenHost is not null || _expandedRevealStage is null) return;
        var root = new Grid { Background = Brushes.Transparent };
        var window = _expandedFullscreenWindow;
        if (window is null)
        {
            window = _expandedFullscreenWindow = new Window
        {
            Title = "Yamuse — сейчас играет", Icon = AppBranding.WindowIcon,
            WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize,
            AllowsTransparency = true, Background = Brushes.Transparent,
            ShowInTaskbar = false, ShowActivated = ShowActivated,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = Left, Top = Top, Width = ActualWidth > 0 ? ActualWidth : Width,
            Height = ActualHeight > 0 ? ActualHeight : Height,
            UseLayoutRounding = true
        };
            window.Closing += ExpandedFullscreenClosing;
            window.Closed += CachedExpandedFullscreenClosed;
            Closed += DestroyExpandedFullscreenWindow;
        }
        // Keep one native source alive for this main window. WPF's tooltip
        // safe area can outlive a visual's move between these two windows.
        window.WindowState = WindowState.Normal;
        window.Topmost = false;
        window.ShowActivated = ShowActivated;
        window.Resources = Resources;
        window.DataContext = DataContext;
        window.Content = root;
        var toastParent = StatusNotification.Parent as Panel;
        var host = _expandedFullscreenHost = new(window, root, toastParent, toastParent?.Children.IndexOf(StatusNotification) ?? -1);
        // Move the existing surface. The main window never changes style,
        // state, bounds or layout, including when it was already maximized.
        ExpandedPlayerHost.Content = null;
        root.Children.Add(_expandedRevealStage);
        toastParent?.Children.Remove(StatusNotification);
        root.Children.Add(StatusNotification);
        window.PreviewKeyDown += Window_KeyDown;
        window.PreviewMouseDown += Window_PreviewMouseDown;
        window.Activated += ExpandedFullscreenActivated;
        window.Deactivated += ExpandedFullscreenDeactivated;
        window.StateChanged += ExpandedFullscreenStateChanged;
        window.IsVisibleChanged += FullscreenPowerVisibilityChanged;
        window.Closed += ExpandedFullscreenClosed;
        if (PresentationSource.FromVisual(this) is not null) window.Owner = this;
        // Deterministic, never-shown fixtures can lay out this same surface
        // without a native handle, a monitor request or desktop activation.
        root.Measure(new Size(window.Width, window.Height));
        root.Arrange(new Rect(0, 0, window.Width, window.Height)); root.UpdateLayout();
    }

    private void ShowExpandedFullscreenHost()
    {
        if (_expandedFullscreenHost is not { } host || !IsVisible || PresentationSource.FromVisual(this) is null) return;
        host.Window.Show();
        PositionExpandedFullscreenHost(host);
        var source = HwndSource.FromHwnd(new WindowInteropHelper(host.Window).Handle);
        if (source is not null)
        {
            host.Source = source;
            host.Hook = (nint hwnd, int message, nint wParam, nint lParam, ref bool handled) =>
            {
                if (message is 0x007E or 0x02E0 && host.BoundsRefresh is not { Status: DispatcherOperationStatus.Pending })
                    host.BoundsRefresh = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
                    {
                        host.BoundsRefresh = null;
                        if (ReferenceEquals(_expandedFullscreenHost, host) && !_closed && host.Window.IsVisible)
                        {
                            try { PositionExpandedFullscreenHost(host); }
                            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or ArgumentException)
                            {
                                CloseExpandedPlayer();
                                SetStatus("Полноэкранный проигрыватель закрыт после изменения экрана. Откройте его ещё раз.", true);
                            }
                        }
                    }));
                return 0;
            };
            source.AddHook(host.Hook);
        }
        UpdateFullscreenPower();
    }

    private void PositionExpandedFullscreenHost(ExpandedFullscreenHost host)
    {
        var handle = new WindowInteropHelper(host.Window).Handle;
        if (handle == 0) return;
        var owner = new WindowInteropHelper(this).Handle;
        var bounds = (_fullscreenBoundsProvider ?? FullscreenMonitor.Read)(owner);
        FullscreenMonitor.Place(handle, bounds);
    }

    private void ExpandedFullscreenActivated(object? sender, EventArgs args)
    {
        if (_expandedFullscreenHost is not { } host || !ReferenceEquals(sender, host.Window)) return;
        // Cover the taskbar only while this surface owns focus. Alt+Tab and
        // the Windows key remain free to bring other applications forward.
        host.Window.Topmost = true;
        UpdateFullscreenPower();
    }
    private void ExpandedFullscreenDeactivated(object? sender, EventArgs args)
    {
        if (_expandedFullscreenHost is { } host && ReferenceEquals(sender, host.Window)) host.Window.Topmost = false;
    }
    private void ExpandedFullscreenStateChanged(object? sender, EventArgs args)
    {
        if (_expandedFullscreenHost is { } host && ReferenceEquals(sender, host.Window) && host.Window.WindowState == WindowState.Minimized)
            CloseExpandedPlayer();
        else UpdateFullscreenPower();
    }
    private void ExpandedFullscreenClosed(object? sender, EventArgs args)
    {
        if (_expandedFullscreenHost is { } host && ReferenceEquals(sender, host.Window)) CloseExpandedPlayer();
    }

    private void ExpandedFullscreenClosing(object? sender, CancelEventArgs args)
    {
        if (_closed) return;
        args.Cancel = true;
        if (_expandedFullscreenHost is { } host && ReferenceEquals(sender, host.Window)) RequestExpandedPlayerClose();
        else if (sender is Window { IsVisible: true } window) window.Hide();
    }

    private void CachedExpandedFullscreenClosed(object? sender, EventArgs args)
    {
        if (ReferenceEquals(sender, _expandedFullscreenWindow)) _expandedFullscreenWindow = null;
        Closed -= DestroyExpandedFullscreenWindow;
    }

    private void DestroyExpandedFullscreenWindow(object? sender, EventArgs args)
    {
        Closed -= DestroyExpandedFullscreenWindow;
        if (_expandedFullscreenWindow is not { } window) return;
        _expandedFullscreenWindow = null;
        CloseExpandedPlayer();
        window.Closing -= ExpandedFullscreenClosing;
        window.Closed -= CachedExpandedFullscreenClosed;
        window.Close();
    }

    private void CloseExpandedFullscreenHost()
    {
        if (_expandedFullscreenHost is not { } host) return;
        _expandedFullscreenHost = null;
        if (host.BoundsRefresh is { Status: DispatcherOperationStatus.Pending }) host.BoundsRefresh.Abort();
        if (host.Hook is not null) host.Source?.RemoveHook(host.Hook);
        host.Window.PreviewKeyDown -= Window_KeyDown;
        host.Window.PreviewMouseDown -= Window_PreviewMouseDown;
        host.Window.Activated -= ExpandedFullscreenActivated;
        host.Window.Deactivated -= ExpandedFullscreenDeactivated;
        host.Window.StateChanged -= ExpandedFullscreenStateChanged;
        host.Window.IsVisibleChanged -= FullscreenPowerVisibilityChanged;
        host.Window.Closed -= ExpandedFullscreenClosed;

        // Dismiss visible popups before moving their owners. Hiding this
        // reusable native host keeps any WPF-internal cached source valid.
        CloseExpandedMenus(host.Root);
        CloseExpandedToolTips(host.Window);
        host.Window.Topmost = false;
        if (host.Window.IsVisible) host.Window.Hide();
        host.Root.Children.Remove(StatusNotification);
        if (_expandedRevealStage is not null)
            host.Root.Children.Remove(_expandedRevealStage);
        host.Window.Content = null;
        host.Window.Resources = new ResourceDictionary();
        if (_expandedRevealStage is not null)
            ExpandedPlayerHost.Content = _expandedRevealStage;
        if (host.ToastParent is not null)
            host.ToastParent.Children.Insert(Math.Clamp(host.ToastIndex, 0, host.ToastParent.Children.Count), StatusNotification);
    }

    private static void CloseExpandedMenus(DependencyObject root)
    {
        if (root is FrameworkElement { ContextMenu: { IsOpen: true } menu }) menu.IsOpen = false;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            CloseExpandedMenus(VisualTreeHelper.GetChild(root, index));
    }

    private static void CloseExpandedToolTips(Window owner)
    {
        // A string tooltip is wrapped in a service-owned ToolTip, separate
        // from the owner's visual tree. Use only public WPF APIs to close it.
        foreach (var source in PresentationSource.CurrentSources.Cast<PresentationSource>().ToArray())
            if (source.RootVisual is { } root) Close(root);

        void Close(DependencyObject node)
        {
            if (node is ToolTip { IsOpen: true } tip && tip.PlacementTarget is { } target
                && ReferenceEquals(Window.GetWindow(target), owner))
                tip.SetCurrentValue(System.Windows.Controls.ToolTip.IsOpenProperty, false);
            for (var index = 0; index < VisualTreeHelper.GetChildrenCount(node); index++)
                Close(VisualTreeHelper.GetChild(node, index));
        }
    }
}

internal readonly record struct FullscreenPixelBounds(int Left, int Top, int Right, int Bottom)
{
    internal int Width => checked(Right - Left);
    internal int Height => checked(Bottom - Top);
}

internal static class FullscreenMonitor
{
    internal static FullscreenPixelBounds Read(nint owner)
    {
        var monitor = MonitorFromWindow(owner, 2);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (monitor == 0 || !GetMonitorInfo(monitor, ref info)) throw new Win32Exception(Marshal.GetLastWin32Error());
        // rcMonitor, not rcWork: fullscreen includes every taskbar edge.
        return new(info.Monitor.Left, info.Monitor.Top, info.Monitor.Right, info.Monitor.Bottom);
    }
    internal static void Place(nint window, FullscreenPixelBounds bounds)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0) throw new ArgumentOutOfRangeException(nameof(bounds));
        // Absolute physical pixels remain correct for negative monitor origins
        // and mixed DPI. Leave both activation and the owner's Z-order alone.
        if (!SetWindowPos(window, 0, bounds.Left, bounds.Top, bounds.Width, bounds.Height, 0x0004 | 0x0010 | 0x0200))
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { internal int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo { internal int Size; internal NativeRect Monitor, Work; internal int Flags; }
    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern nint MonitorFromWindow(nint window, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(nint window, nint insertAfter, int x, int y, int width, int height, uint flags);
}
