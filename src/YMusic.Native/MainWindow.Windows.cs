using System.Windows;

namespace YMusic.Native;

public partial class MainWindow
{
    private WindowsIntegration? _windows;
    private void InitializeWindows()
    {
        _windows = new WindowsIntegration(this);
        _windows.Failed += _ => _diagnostics.LastErrorType = "WindowsMediaUnavailable";
        _windows.Command += async action =>
        {
            if (_closed) return;
            switch (action)
            {
                case DesktopMediaAction.PlayPause: await TogglePlaybackAsync(); break;
                case DesktopMediaAction.Next: await AdvanceAsync(1, false); break;
                case DesktopMediaAction.Previous:
                    if (_player.Position.TotalSeconds > 3) { CommitSeek(0); }
                    else await AdvanceAsync(-1, false);
                    break;
                case DesktopMediaAction.Stop:
                    _playbackGeneration++; _playCts?.Cancel(); ReportWaveEnd(false);
                    _player.Stop(); _progressTimer.Stop(); HighlightTrack(null);
                    _diagnostics.Stage = "stopped"; SetStatus("Воспроизведение остановлено");
                    UpdateControls(); UpdateProgress(); break;
            }
        };
        UpdateWindowsMedia();
    }
    private void UpdateWindowsMedia()
    {
        if (_windows is null) return;
        if (_current is null || !_player.HasSource) { _windows.Clear(); return; }
        _windows.Update(_current.Title, _current.Artist, _current.Album, _player.IsPlaying, NextButton.IsEnabled, PreviousButton.IsEnabled);
    }
}
