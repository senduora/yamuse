using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Windows.Media;

namespace YMusic.Native;

internal enum DesktopMediaAction { PlayPause, Next, Previous, Stop }

// Native desktop SMTC, bound to this WPF window only. Requires the Windows TFM;
// no browser, global RegisterHotKey, keyboard hook, or WinUI/WindowsAppSDK.
internal sealed class WindowsIntegration : IDisposable
{
    private readonly Window _window;
    private SystemMediaTransportControls? _controls;
    private int _disposed;
    private bool _hasMedia;
    private bool _isPlaying;
    private bool _canNext;
    private bool _canPrevious;
    private string _title = "";
    private string _artist = "";
    private string _album = "";

    public event Action<DesktopMediaAction>? Command;
    public event Action<string>? Failed;
    public bool IsAvailable { get; private set; }
    public string? FailureReason { get; private set; }
    internal nint WindowHandle { get; }

    public WindowsIntegration(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        window.Dispatcher.VerifyAccess();
        _window = window;
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763))
        {
            FailureReason = "SMTC требует Windows 10 версии 1809 или новее. Управление доступно в окне приложения.";
            return;
        }
        try
        {
            WindowHandle = new WindowInteropHelper(window).EnsureHandle();
            _ = GetWindowThreadProcessId(WindowHandle, out var processId);
            if (WindowHandle == 0 || processId != Environment.ProcessId || GetAncestor(WindowHandle, 2) != WindowHandle)
                throw new InvalidOperationException("SMTC можно привязать только к собственному верхнеуровневому окну.");
            _controls = SystemMediaTransportControlsInterop.GetForWindow(WindowHandle);
            _controls.ButtonPressed += OnButtonPressed;
            _controls.IsEnabled = false;
            _controls.PlaybackStatus = MediaPlaybackStatus.Closed;
            _window.Closed += WindowClosed;
            IsAvailable = true;
        }
        catch (Exception ex) when (InteropFailure(ex)) { FailCore(ex); }
    }

    public void Update(string title, string artist, string album, bool isPlaying, bool canNext, bool canPrevious)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        OnWindowThread(() => UpdateCore(title ?? "", artist ?? "", album ?? "", isPlaying, canNext, canPrevious));
    }

    // Use on logout/empty queue so previous account metadata is removed from Windows.
    public void Clear()
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        OnWindowThread(ClearCore);
    }

    private void UpdateCore(string title, string artist, string album, bool isPlaying, bool canNext, bool canPrevious)
    {
        if (!IsAvailable || _controls is null || Volatile.Read(ref _disposed) != 0) return;
        if (string.IsNullOrWhiteSpace(title)) { ClearCore(); return; }
        try
        {
            var metadataChanged = !_hasMedia || _title != title || _artist != artist || _album != album;
            _isPlaying = isPlaying;
            _canNext = canNext;
            _canPrevious = canPrevious;
            _controls.IsPlayEnabled = true;
            _controls.IsPauseEnabled = true;
            _controls.IsStopEnabled = true;
            _controls.IsNextEnabled = canNext;
            _controls.IsPreviousEnabled = canPrevious;
            if (metadataChanged)
            {
                var display = _controls.DisplayUpdater;
                display.Type = MediaPlaybackType.Music;
                display.MusicProperties.Title = title;
                display.MusicProperties.Artist = artist;
                display.MusicProperties.AlbumTitle = album;
                display.Update();
                _title = title;
                _artist = artist;
                _album = album;
            }
            _hasMedia = true;
            _controls.PlaybackStatus = isPlaying ? MediaPlaybackStatus.Playing : MediaPlaybackStatus.Paused;
            _controls.IsEnabled = true;
        }
        catch (Exception ex) when (InteropFailure(ex)) { FailCore(ex); }
    }

    private void OnButtonPressed(SystemMediaTransportControls sender, SystemMediaTransportControlsButtonPressedEventArgs args)
    {
        // WinRT raises this on a worker thread. Both state checks and the application
        // command run on the WPF dispatcher, after any queued Update/Clear operation.
        var button = args.Button;
        OnWindowThread(() =>
        {
            if (Volatile.Read(ref _disposed) != 0 || !IsAvailable || !_hasMedia) return;
            DesktopMediaAction? action = button switch
            {
                // SMTC has distinct Play and Pause commands. Gate the toggle so a
                // repeated Play cannot accidentally pause an already playing track.
                SystemMediaTransportControlsButton.Play when !_isPlaying => DesktopMediaAction.PlayPause,
                SystemMediaTransportControlsButton.Pause when _isPlaying => DesktopMediaAction.PlayPause,
                SystemMediaTransportControlsButton.Next when _canNext => DesktopMediaAction.Next,
                SystemMediaTransportControlsButton.Previous when _canPrevious => DesktopMediaAction.Previous,
                SystemMediaTransportControlsButton.Stop => DesktopMediaAction.Stop,
                _ => null
            };
            if (action is { } command) Command?.Invoke(command);
        });
    }

    private void OnWindowThread(Action action)
    {
        if (_window.Dispatcher.HasShutdownStarted || _window.Dispatcher.HasShutdownFinished) return;
        if (_window.Dispatcher.CheckAccess()) action();
        else _window.Dispatcher.BeginInvoke(action);
    }

    private void ClearCore()
    {
        _hasMedia = false;
        _isPlaying = false;
        _canNext = false;
        _canPrevious = false;
        _title = _artist = _album = "";
        if (!IsAvailable || _controls is null) return;
        try
        {
            _controls.IsPlayEnabled = false;
            _controls.IsPauseEnabled = false;
            _controls.IsStopEnabled = false;
            _controls.IsNextEnabled = false;
            _controls.IsPreviousEnabled = false;
            _controls.PlaybackStatus = MediaPlaybackStatus.Closed;
            _controls.DisplayUpdater.ClearAll();
            _controls.DisplayUpdater.Update();
            _controls.IsEnabled = false;
        }
        catch (Exception ex) when (InteropFailure(ex)) { FailCore(ex); }
    }

    private static bool InteropFailure(Exception ex)
        => ex is COMException or InvalidOperationException or NotSupportedException or EntryPointNotFoundException
            or TypeLoadException or TypeInitializationException or Win32Exception;

    private void FailCore(Exception ex)
    {
        IsAvailable = false;
        _hasMedia = false;
        FailureReason = $"SMTC недоступен (0x{ex.HResult:X8}). Управление доступно в окне приложения.";
        DetachControls();
        Failed?.Invoke(FailureReason);
    }

    private void DetachControls()
    {
        var controls = _controls;
        _controls = null;
        if (controls is null) return;
        try { controls.ButtonPressed -= OnButtonPressed; }
        catch (Exception ex) when (InteropFailure(ex)) { }
        try
        {
            controls.PlaybackStatus = MediaPlaybackStatus.Closed;
            controls.DisplayUpdater.ClearAll();
            controls.DisplayUpdater.Update();
            controls.IsEnabled = false;
        }
        catch (Exception ex) when (InteropFailure(ex)) { }
        // C#/WinRT owns the projected COM reference. Do not call ReleaseComObject.
    }

    private void WindowClosed(object? sender, EventArgs args) => Dispose();

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        void Cleanup()
        {
            _window.Closed -= WindowClosed;
            _hasMedia = false;
            IsAvailable = false;
            DetachControls();
            Command = null;
            Failed = null;
        }
        if (_window.Dispatcher.CheckAccess()) Cleanup();
        else if (!_window.Dispatcher.HasShutdownStarted && !_window.Dispatcher.HasShutdownFinished) _window.Dispatcher.Invoke(Cleanup);
        else { IsAvailable = false; DetachControls(); }
    }

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint window, out int processId);
    [DllImport("user32.dll")]
    private static extern nint GetAncestor(nint window, uint flags);
}
