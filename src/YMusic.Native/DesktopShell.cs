using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

namespace YMusic.Native;

internal enum ShellAction { PlayPause, Next, Previous, Exit }

// All windows and commands belong to this process. There are no global hooks,
// background processes, WebView, Windows Forms, or startup registration.
internal sealed class DesktopShell : IDisposable
{
    private const uint TrayCallback = 0x8000 + 51;
    private const uint TrayId = 1;
    private const uint NifMessage = 0x1, NifIcon = 0x2, NifTip = 0x4, NifGuid = 0x20;
    private readonly Window _owner;
    private readonly HwndSource _source;
    private readonly Guid _iconGuid = Guid.NewGuid();
    private readonly uint _taskbarCreated;
    private readonly nint _icon;
    private bool _minimizeToTray;
    private bool _version4;
    private bool _exitRequested;
    private bool _ownerClosed;
    private int _disposed;
    private WindowState _restoreState;
    private Window? _mini;
    private TextBlock? _miniTitle;
    private TextBlock? _miniArtist;
    private Button? _miniPlay;
    private Button? _miniNext;
    private Button? _miniPrevious;
    private string _title = "";
    private string _artist = "";
    private bool _isPlaying;
    private bool _canNext;
    private bool _canPrevious;

    public event Action<ShellAction>? Command;
    public bool IsAvailable { get; private set; }
    public string? FailureReason { get; private set; }
    internal nint WindowHandle { get; }
    internal Guid IconGuid => _iconGuid;

    public bool MinimizeToTray
    {
        get => _minimizeToTray;
        set
        {
            _owner.Dispatcher.VerifyAccess();
            if (Volatile.Read(ref _disposed) != 0 || _minimizeToTray == value) return;
            if (value)
            {
                if (!TryAddTray()) return; // Never hide without a working restore path.
                _minimizeToTray = true;
                if (_owner.WindowState == WindowState.Minimized) _owner.Hide();
            }
            else
            {
                _minimizeToTray = false;
                RemoveTray();
                if (!_owner.IsVisible) ShowMainWindow();
            }
        }
    }

    public DesktopShell(Window owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        owner.Dispatcher.VerifyAccess();
        _owner = owner;
        _restoreState = owner.WindowState == WindowState.Minimized ? WindowState.Normal : owner.WindowState;
        WindowHandle = new WindowInteropHelper(owner).EnsureHandle();
        _ = GetWindowThreadProcessId(WindowHandle, out var processId);
        if (WindowHandle == 0 || processId != Environment.ProcessId)
            throw new InvalidOperationException("Оболочку можно привязать только к окну этого приложения.");
        _source = HwndSource.FromHwnd(WindowHandle) ?? throw new InvalidOperationException("Недоступно окно приложения.");
        _source.AddHook(WindowProcedure);
        _taskbarCreated = RegisterWindowMessage("TaskbarCreated");
        _icon = LoadIcon(0, (nint)32512); // Shared Windows icon; do not destroy it.
        owner.Closing += OwnerClosing;
        owner.Closed += OwnerClosed;
        owner.StateChanged += OwnerStateChanged;
    }

    public void Update(string title, string artist, bool isPlaying, bool canNext, bool canPrevious)
    {
        OnWindowThread(() =>
        {
            _title = (title ?? "").Replace('\0', ' ');
            _artist = (artist ?? "").Replace('\0', ' ');
            _isPlaying = isPlaying;
            _canNext = canNext;
            _canPrevious = canPrevious;
            UpdateMiniPlayer();
            if (IsAvailable)
            {
                var data = CreateIconData();
                if (!Shell_NotifyIcon(1, ref data)) DisableUnavailableTray();
            }
        });
    }

    public void ShowMainWindow()
    {
        OnWindowThread(() =>
        {
            if (_ownerClosed) return;
            if (!_owner.IsVisible) _owner.Show();
            if (_owner.WindowState == WindowState.Minimized) _owner.WindowState = _restoreState;
            _owner.Activate();
        });
    }

    // Explicit Exit bypasses the optional close-to-tray behavior. This method does
    // not raise Command, so the application's Exit handler may call it directly.
    public void RequestExit()
    {
        OnWindowThread(() =>
        {
            if (_ownerClosed) return;
            _exitRequested = true;
            _owner.Close();
        });
    }

    public void ShowMiniPlayer()
    {
        OnWindowThread(() =>
        {
            if (_mini is not null) { _mini.Show(); _mini.Activate(); return; }
            var window = new Window
            {
                Title = "Мини-плеер · YMusic Native", Width = 390, Height = 190,
                ResizeMode = ResizeMode.NoResize, WindowStyle = WindowStyle.ToolWindow,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                ShowInTaskbar = false, Background = new SolidColorBrush(Color.FromRgb(24, 24, 29)),
                Foreground = Brushes.White, FontFamily = new FontFamily("Segoe UI")
            };
            var grid = new Grid { Margin = new Thickness(14, 10, 14, 10) };
            grid.RowDefinitions.Add(new() { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new() { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new() { Height = GridLength.Auto });
            _miniTitle = new TextBlock { FontSize = 16, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
            _miniArtist = new TextBlock { FontSize = 12, Foreground = Brushes.LightGray, Margin = new Thickness(0, 3, 0, 7), TextTrimming = TextTrimming.CharacterEllipsis };
            grid.Children.Add(_miniTitle);
            Grid.SetRow(_miniArtist, 1); grid.Children.Add(_miniArtist);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            _miniPrevious = MiniButton("Назад", () => Dispatch(ShellAction.Previous));
            _miniPlay = MiniButton("Играть", () => Dispatch(ShellAction.PlayPause));
            _miniNext = MiniButton("Далее", () => Dispatch(ShellAction.Next));
            buttons.Children.Add(_miniPrevious); buttons.Children.Add(_miniPlay); buttons.Children.Add(_miniNext);
            Grid.SetRow(buttons, 2); grid.Children.Add(buttons);
            var footer = new DockPanel { Margin = new Thickness(0, 7, 0, 0), LastChildFill = false };
            var open = new Button { Content = "Открыть", Padding = new Thickness(8, 2, 8, 2), HorizontalAlignment = HorizontalAlignment.Right };
            open.Click += (_, _) => ShowMainWindow();
            DockPanel.SetDock(open, Dock.Right); footer.Children.Add(open);
            var topmost = new CheckBox { Content = "Поверх окон", Foreground = Brushes.LightGray, VerticalAlignment = VerticalAlignment.Center };
            topmost.Checked += (_, _) => window.Topmost = true;
            topmost.Unchecked += (_, _) => window.Topmost = false;
            footer.Children.Add(topmost); Grid.SetRow(footer, 3); grid.Children.Add(footer);
            window.Content = grid;
            window.Closed += (_, _) =>
            {
                if (!ReferenceEquals(_mini, window)) return;
                _mini = null; _miniTitle = _miniArtist = null;
                _miniPlay = _miniNext = _miniPrevious = null;
            };
            _mini = window;
            UpdateMiniPlayer();
            window.Show();
        });
    }

    private static Button MiniButton(string text, Action action)
    {
        var button = new Button { Content = text, MinWidth = 90, Padding = new Thickness(8, 6, 8, 6), Margin = new Thickness(4, 0, 4, 0) };
        button.Click += (_, _) => action();
        return button;
    }

    private void UpdateMiniPlayer()
    {
        if (_mini is null) return;
        _miniTitle!.Text = string.IsNullOrWhiteSpace(_title) ? "YMusic Native" : _title;
        _miniArtist!.Text = string.IsNullOrWhiteSpace(_artist) ? "Выберите музыку в основном окне" : _artist;
        _miniPlay!.Content = _isPlaying ? "Пауза" : "Играть";
        _miniPlay.IsEnabled = !string.IsNullOrWhiteSpace(_title);
        _miniNext!.IsEnabled = _canNext;
        _miniPrevious!.IsEnabled = _canPrevious;
    }

    private bool TryAddTray()
    {
        if (IsAvailable) return true;
        var data = CreateIconData();
        if (_icon == 0 || !Shell_NotifyIcon(0, ref data))
        {
            FailureReason = "Область уведомлений Windows недоступна. Окно остаётся доступным.";
            return false;
        }
        IsAvailable = true;
        FailureReason = null;
        data.TimeoutOrVersion = 4;
        _version4 = Shell_NotifyIcon(4, ref data);
        return true;
    }

    private void RemoveTray()
    {
        if (!IsAvailable) return;
        var data = CreateIconData();
        _ = Shell_NotifyIcon(2, ref data);
        IsAvailable = false;
        _version4 = false;
    }

    private void DisableUnavailableTray()
    {
        RemoveTray();
        _minimizeToTray = false;
        FailureReason = "Область уведомлений Windows недоступна. Окно остаётся доступным.";
        if (!_owner.IsVisible) ShowMainWindow();
    }

    private NotifyIconData CreateIconData()
    {
        var tip = string.IsNullOrWhiteSpace(_title) ? "YMusic Native" : $"{_title} — {_artist}";
        if (tip.Length > 127) tip = tip[..(char.IsHighSurrogate(tip[126]) ? 126 : 127)];
        return new()
        {
            Size = (uint)Marshal.SizeOf<NotifyIconData>(), Window = WindowHandle, Id = TrayId,
            Flags = NifMessage | NifIcon | NifTip | NifGuid, CallbackMessage = TrayCallback,
            Icon = _icon, Tip = tip, Info = "", InfoTitle = "", Guid = _iconGuid
        };
    }

    private nint WindowProcedure(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (Volatile.Read(ref _disposed) != 0 || hwnd != WindowHandle) return 0;
        if (_taskbarCreated != 0 && (uint)message == _taskbarCreated && _minimizeToTray)
        {
            IsAvailable = false;
            if (!TryAddTray()) DisableUnavailableTray();
            return 0;
        }
        if ((uint)message != TrayCallback || !IsAvailable) return 0;
        var notification = _version4 ? (uint)(lParam.ToInt64() & 0xffff) : unchecked((uint)lParam.ToInt64());
        var id = _version4 ? (uint)((lParam.ToInt64() >> 16) & 0xffff) : unchecked((uint)wParam.ToInt64());
        if (id != TrayId) return 0;
        handled = true;
        if (notification is 0x400 or 0x401 or 0x203) ShowMainWindow(); // NIN_SELECT / KEYSELECT / double-click
        else if (notification is 0x7b or 0x205) ShowTrayMenu(); // context menu / legacy right-button-up
        return 0;
    }

    private void ShowTrayMenu()
    {
        var menu = CreatePopupMenu();
        if (menu == 0) return;
        try
        {
            _ = AppendMenu(menu, 0, 1, "Открыть YMusic Native");
            _ = AppendMenu(menu, 0, 2, "Мини-плеер");
            _ = AppendMenu(menu, 0x800, 0, null);
            _ = AppendMenu(menu, string.IsNullOrWhiteSpace(_title) ? 0x1u : 0u, 3, _isPlaying ? "Пауза" : "Играть");
            _ = AppendMenu(menu, _canNext ? 0u : 0x1u, 4, "Следующий трек");
            _ = AppendMenu(menu, _canPrevious ? 0u : 0x1u, 5, "Предыдущий трек");
            _ = AppendMenu(menu, 0x800, 0, null);
            _ = AppendMenu(menu, 0, 6, "Выход");
            _ = GetCursorPos(out var point);
            _ = SetForegroundWindow(WindowHandle);
            var selected = TrackPopupMenuEx(menu, 0x100 | 0x2, point.X, point.Y, WindowHandle, 0);
            _ = PostMessage(WindowHandle, 0, 0, 0);
            switch (selected)
            {
                case 1: ShowMainWindow(); break;
                case 2: ShowMiniPlayer(); break;
                case 3: Dispatch(ShellAction.PlayPause); break;
                case 4: Dispatch(ShellAction.Next); break;
                case 5: Dispatch(ShellAction.Previous); break;
                case 6: Dispatch(ShellAction.Exit); break;
            }
        }
        finally { _ = DestroyMenu(menu); }
    }

    private void Dispatch(ShellAction action)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        if (action == ShellAction.PlayPause && string.IsNullOrWhiteSpace(_title)
            || action == ShellAction.Next && !_canNext
            || action == ShellAction.Previous && !_canPrevious) return;
        if (action == ShellAction.Exit) _exitRequested = true;
        Command?.Invoke(action);
        if (action == ShellAction.Exit && !_ownerClosed) RequestExit();
    }

    private void OwnerStateChanged(object? sender, EventArgs e)
    {
        if (_owner.WindowState != WindowState.Minimized) _restoreState = _owner.WindowState;
        else if (_minimizeToTray && IsAvailable) _owner.Hide();
    }

    private void OwnerClosing(object? sender, CancelEventArgs e)
    {
        if (_exitRequested || !_minimizeToTray || !IsAvailable || Volatile.Read(ref _disposed) != 0) return;
        e.Cancel = true;
        _owner.Hide();
    }

    private void OwnerClosed(object? sender, EventArgs e)
    {
        _ownerClosed = true;
        Dispose();
    }

    private void OnWindowThread(Action action)
    {
        if (Volatile.Read(ref _disposed) != 0 || _owner.Dispatcher.HasShutdownStarted || _owner.Dispatcher.HasShutdownFinished) return;
        void Guarded() { if (Volatile.Read(ref _disposed) == 0) action(); }
        if (_owner.Dispatcher.CheckAccess()) Guarded();
        else _owner.Dispatcher.BeginInvoke((Action)Guarded);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        void DisposeCore()
        {
            RemoveTray();
            _minimizeToTray = false;
            _source.RemoveHook(WindowProcedure);
            _owner.Closing -= OwnerClosing;
            _owner.Closed -= OwnerClosed;
            _owner.StateChanged -= OwnerStateChanged;
            _mini?.Close();
            Command = null;
        }
        if (_owner.Dispatcher.CheckAccess()) DisposeCore();
        else if (!_owner.Dispatcher.HasShutdownStarted && !_owner.Dispatcher.HasShutdownFinished) _owner.Dispatcher.Invoke(DisposeCore);
        else RemoveTray();
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public uint Size;
        public nint Window;
        public uint Id, Flags, CallbackMessage;
        public nint Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tip;
        public uint State, StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Info;
        public uint TimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string InfoTitle;
        public uint InfoFlags;
        public Guid Guid;
        public nint BalloonIcon;
    }
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [DllImport("shell32.dll", EntryPoint = "Shell_NotifyIconW", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool Shell_NotifyIcon(uint message, ref NotifyIconData data);
    [DllImport("user32.dll", EntryPoint = "RegisterWindowMessageW", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string message);
    [DllImport("user32.dll", EntryPoint = "LoadIconW")] private static extern nint LoadIcon(nint instance, nint name);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hwnd, out int processId);
    [DllImport("user32.dll")] private static extern nint CreatePopupMenu();
    [DllImport("user32.dll", EntryPoint = "AppendMenuW", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool AppendMenu(nint menu, uint flags, nuint id, string? text);
    [DllImport("user32.dll")] private static extern uint TrackPopupMenuEx(nint menu, uint flags, int x, int y, nint owner, nint parameters);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DestroyMenu(nint menu);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetCursorPos(out NativePoint point);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetForegroundWindow(nint hwnd);
    [DllImport("user32.dll", EntryPoint = "PostMessageW")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool PostMessage(nint hwnd, uint message, nint wParam, nint lParam);
}
