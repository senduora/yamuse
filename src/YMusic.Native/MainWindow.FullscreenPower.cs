using System.Runtime.InteropServices;
using System.Windows;

namespace YMusic.Native;

public partial class MainWindow
{
    private FullscreenDisplayRequest? _fullscreenDisplayRequest;
    private bool _fullscreenPowerInitialized;

    private void InitializeFullscreenPower()
    {
        if (_fullscreenPowerInitialized) return;
        _fullscreenPowerInitialized = true;
        _fullscreenDisplayRequest ??= new();
        StateChanged += FullscreenPowerStateChanged;
        IsVisibleChanged += FullscreenPowerVisibilityChanged;
        ExpandedPlayerHost.IsVisibleChanged += FullscreenPowerVisibilityChanged;
        Closed += FullscreenPowerClosed;
    }

    private void UpdateFullscreenPower() => _fullscreenDisplayRequest?.Update(
        fullscreen: _expandedWindowPlacement is not null && IsExpandedPlayerOpen && _expandedCollapse is null,
        visible: IsVisible && ExpandedPlayerHost.IsVisible && (_expandedFullscreenHost?.Window.IsVisible ?? false),
        minimized: WindowState == WindowState.Minimized || _expandedFullscreenHost?.Window.WindowState == WindowState.Minimized,
        playing: _player.IsPlaying,
        closed: _closed);

    private void FullscreenPowerStateChanged(object? sender, EventArgs args)
    {
        if (WindowState == WindowState.Minimized && _expandedFullscreenHost is not null) CloseExpandedPlayer();
        UpdateFullscreenPower();
    }
    private void FullscreenPowerVisibilityChanged(object sender, DependencyPropertyChangedEventArgs args)
    {
        if (_expandedFullscreenHost is { } host && !((UIElement)sender).IsVisible
            && (ReferenceEquals(sender, this) || ReferenceEquals(sender, host.Window))) CloseExpandedPlayer();
        UpdateFullscreenPower();
    }

    private void FullscreenPowerClosed(object? sender, EventArgs args)
    {
        StateChanged -= FullscreenPowerStateChanged;
        IsVisibleChanged -= FullscreenPowerVisibilityChanged;
        ExpandedPlayerHost.IsVisibleChanged -= FullscreenPowerVisibilityChanged;
        Closed -= FullscreenPowerClosed;
        _fullscreenDisplayRequest?.Dispose();
        _fullscreenDisplayRequest = null;
        _fullscreenPowerInitialized = false;
    }
}

// Windows keeps this request on the calling UI thread. No timer, input
// simulation or power-plan change is needed; release on that same thread.
internal sealed class FullscreenDisplayRequest : IDisposable
{
    internal const uint Continuous = 0x80000000;
    internal const uint WhilePlaying = Continuous | 0x00000002 | 0x00000001;
    private readonly Func<uint, uint> _setState;
    private readonly int _ownerThread = Environment.CurrentManagedThreadId;
    private bool _disposed;
    internal bool IsHeld { get; private set; }

    internal FullscreenDisplayRequest(Func<uint, uint>? setState = null) => _setState = setState ?? SetThreadExecutionState;

    internal bool Update(bool fullscreen, bool visible, bool minimized, bool playing, bool closed) =>
        SetRequired(fullscreen && visible && !minimized && playing && !closed);

    internal bool SetRequired(bool required)
    {
        VerifyThread();
        if (_disposed) return !required && !IsHeld;
        if (required == IsHeld) return true;
        if (_setState(required ? WhilePlaying : Continuous) == 0) return false;
        IsHeld = required;
        return true;
    }

    public void Dispose()
    {
        VerifyThread();
        if (_disposed) return;
        SetRequired(false);
        _disposed = true;
    }

    private void VerifyThread()
    {
        if (Environment.CurrentManagedThreadId != _ownerThread)
            throw new InvalidOperationException("Display request must be updated on its owning thread.");
    }

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern uint SetThreadExecutionState(uint executionState);
}
