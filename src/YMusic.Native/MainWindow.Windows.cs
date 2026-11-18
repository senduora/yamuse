using System.Windows;

namespace YMusic.Native;

public partial class MainWindow
{
    private WindowsIntegration? _windows;

    private void InitializeWindows()
    {
        _windows = new WindowsIntegration(this);
        _windows.Failed += _ => _diagnostics.LastErrorType = "WindowsMediaUnavailable";
        _windows.Command += async action => await HandleDesktopMediaCommandAsync(action);
        UpdateWindowsMedia();
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
            case DesktopMediaAction.Previous:
                if (_player.Position.TotalSeconds > 3) { CommitSeek(0); }
                else await AdvanceAsync(-1, false);
                break;
            case DesktopMediaAction.Stop:
                _playbackGeneration++;
                CancelPendingWavePlayback(); _playCts?.Cancel(); ReportWaveEnd(false);
                _player.Stop(); _progressTimer.Stop();
                if (_current is not null) _current.IsPlaying = false;
                HighlightTrack(null);
                _diagnostics.Stage = "stopped"; SetStatus("Воспроизведение остановлено");
                UpdateControls(); UpdateProgress(); break;
        }
    }
    private void UpdateWindowsMedia()
    {
        if (_windows is null) return;
        var transportActive = PlaybackTransportActive;
        if (_current is null || !_player.HasSource && !transportActive) { _windows.Clear(); return; }
        _windows.Update(_current.Title, _current.Artist, _current.Album, transportActive, NextButton.IsEnabled, PreviousButton.IsEnabled);
    }
}
