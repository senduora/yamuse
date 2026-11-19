using System.Windows;
using System.Windows.Input;

namespace YMusic.Native;

public partial class MainWindow
{
    private WindowsIntegration? _windows;
    private (long Occurrence, long Queue, int Playback, int Account)? _pausedDesktopOccurrence;

    private void RememberPausedDesktopOccurrence()
    {
        if (_playbackQueue.CurrentOccurrenceToken is { } occurrence)
            _pausedDesktopOccurrence = (occurrence, _playbackQueue.ReplacementVersion, _playbackGeneration, _accountGeneration);
    }

    private bool CanResumePausedDesktopOccurrence => _pausedDesktopOccurrence is { } paused
        && paused.Playback == _playbackGeneration && paused.Account == _accountGeneration
        && paused.Queue == _playbackQueue.ReplacementVersion && paused.Occurrence == _playbackQueue.CurrentOccurrenceToken
        && _current is { CanPlayInQueue: true } && ReferenceEquals(_current, _playbackQueue.Current);

    private bool CanRestartCanceledColdOpen => _playCts is { IsCancellationRequested: true } && CanResumePausedDesktopOccurrence;

    private void InitializeWindows()
    {
        _windows = new WindowsIntegration(this);
        _windows.Failed += _ => _diagnostics.LastErrorType = "WindowsMediaUnavailable";
        _windows.Command += async action => await HandleDesktopMediaCommandAsync(action);
        UpdateWindowsMedia();
    }

    internal void AttachMediaCommands(Window window)
    {
        CommandManager.AddPreviewCanExecuteHandler(window, LocalMediaCommandCanExecute);
        CommandManager.AddPreviewExecutedHandler(window, LocalMediaCommandExecuted);
        if (window is MiniPlayerWindow) window.PreviewKeyDown += MiniMediaKeyDown;
        if (!ReferenceEquals(window, this)) window.Closed += DetachMediaCommands;
    }

    private void DetachMediaCommands(object? sender, EventArgs e)
    {
        if (sender is not Window window) return;
        window.Closed -= DetachMediaCommands;
        CommandManager.RemovePreviewCanExecuteHandler(window, LocalMediaCommandCanExecute);
        CommandManager.RemovePreviewExecutedHandler(window, LocalMediaCommandExecuted);
        if (window is MiniPlayerWindow) window.PreviewKeyDown -= MiniMediaKeyDown;
    }

    private async void MiniMediaKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Handled || !IsLocalMediaKey(e.Key) || !CanHandleLocalMediaCommands) return;
        var keyboard = e.KeyboardDevice;
        var windowsKey = keyboard.IsKeyDown(Key.LWin) || keyboard.IsKeyDown(Key.RWin);
        if (HasTransportModifier(keyboard.Modifiers, windowsKey)) return;
        e.Handled = true;
        await HandleLocalMediaKeyAsync(e.Key);
    }

    private static bool IsLocalMediaKey(Key key) => key is Key.MediaPlayPause or Key.MediaNextTrack or Key.MediaPreviousTrack;

    private Task HandleLocalMediaKeyAsync(Key key) => key switch
    {
        Key.MediaPlayPause => TogglePlaybackAsync(),
        Key.MediaNextTrack => AdvanceAsync(1, false),
        Key.MediaPreviousTrack => HandleDesktopMediaCommandAsync(DesktopMediaAction.Previous),
        _ => Task.CompletedTask
    };

    private bool CanHandleLocalMediaCommands => !_closed && _windows?.HandlesMediaKeys != true;

    private static DesktopMediaAction? DesktopActionFor(ICommand command)
        => command == MediaCommands.Play ? DesktopMediaAction.Play
            : command == MediaCommands.Pause ? DesktopMediaAction.Pause
            : command == MediaCommands.Stop ? DesktopMediaAction.Stop
            : command == MediaCommands.NextTrack ? DesktopMediaAction.Next
            : command == MediaCommands.PreviousTrack ? DesktopMediaAction.Previous
            : null;

    private void LocalMediaCommandCanExecute(object sender, CanExecuteRoutedEventArgs e)
    {
        if (!CanHandleLocalMediaCommands || e.Command != MediaCommands.TogglePlayPause && DesktopActionFor(e.Command) is null) return;
        e.CanExecute = true;
        e.Handled = true;
    }

    private async void LocalMediaCommandExecuted(object sender, ExecutedRoutedEventArgs e)
    {
        if (!CanHandleLocalMediaCommands) return;
        var action = DesktopActionFor(e.Command);
        if (action is null && e.Command != MediaCommands.TogglePlayPause) return;
        e.Handled = true;
        if (action is { } desktop) await HandleDesktopMediaCommandAsync(desktop);
        else await TogglePlaybackAsync();
    }

    internal async Task HandleDesktopMediaCommandAsync(DesktopMediaAction action)
    {
        if (_closed) return;
        switch (action)
        {
            case DesktopMediaAction.Play:
                if (!PlaybackTransportActive) await TogglePlaybackAsync();
                break;
            case DesktopMediaAction.Pause:
                if (PlaybackTransportActive) await TogglePlaybackAsync();
                break;
            case DesktopMediaAction.Next: await AdvanceAsync(1, false); break;
            case DesktopMediaAction.Previous: await PlayPreviousAsync(); break;
            case DesktopMediaAction.Stop:
                _playbackGeneration++;
                _pausedDesktopOccurrence = null;
                _seekGesture?.Cancel();
                _expandedSeekGesture?.Cancel();
                _seekDragging = false;
                _pendingSeek = null;
                ClearRestoredPosition();
                CancelPendingWavePlayback(); _playCts?.Cancel(); ReportWaveEnd(false);
                _player.Stop(); _progressTimer.Stop();
                if (_current is not null) _current.IsPlaying = false;
                HighlightTrack(null);
                _diagnostics.Stage = "stopped"; SetStatus("Воспроизведение остановлено");
                SavePlaybackState();
                UpdateControls(); UpdateProgress(); break;
        }
    }
    private Task PlayPreviousAsync()
    {
        if (DisplayPosition(_player.Position).TotalSeconds <= 3) return AdvanceAsync(-1, false);
        if (!_player.HasSource && _current is { } track && TryGetRestoredPosition(track, out _))
        {
            if (QueuePlaybackBeforeFirstPlay) RememberPausedDesktopOccurrence();
            CancelPendingWavePlayback();
            _restoredPlaybackSeconds = 0;
            _pendingSeek = null;
            UpdateControls(); UpdateProgress();
            SavePlaybackState();
        }
        else CommitSeek(0);
        return Task.CompletedTask;
    }

    private void UpdateWindowsMedia()
    {
        if (_windows is null) return;
        var transportActive = PlaybackTransportActive;
        if (_current is null || !_player.HasSource && !transportActive && !CanResumePausedDesktopOccurrence) { _windows.Clear(); return; }
        _windows.Update(_current.Title, _current.Artist, _current.Album, transportActive, NextButton.IsEnabled, PreviousButton.IsEnabled);
    }
}
