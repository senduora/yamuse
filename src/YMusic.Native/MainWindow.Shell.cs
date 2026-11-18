namespace YMusic.Native;

public partial class MainWindow
{
    private readonly AsyncLocal<bool> _shellCommandActive = new();

    private async Task HandleShellCommandAsync(ShellAction action)
    {
        var previous = _shellCommandActive.Value;
        _shellCommandActive.Value = true;
        try
        {
            switch (action)
            {
                case ShellAction.PlayPause: await TogglePlaybackAsync(); break;
                case ShellAction.Next: await AdvanceAsync(1, false); break;
                case ShellAction.Previous:
                    if (_player.Position.TotalSeconds > 3) CommitSeek(0);
                    else await AdvanceAsync(-1, false);
                    break;
                case ShellAction.ToggleLike: await ToggleCurrentLikeAsync(); break;
            }
        }
        finally { _shellCommandActive.Value = previous; }
    }

    private void UpdateShellState()
    {
        if (_shell is null || _closed) return;
        _shell.Update(_current?.Title ?? "", _current?.Artist ?? "", PlaybackTransportActive,
            NextButton.IsEnabled, PreviousButton.IsEnabled);
        _shell.UpdateDetails(null, DisplayPosition(_player.Position).TotalSeconds,
            (RestoredPlaybackDuration ?? _player.Duration).TotalSeconds, SeekSlider.IsEnabled,
            _current is { } row && _favoriteTrackIds.Contains(row.Id), PlayerLikeButton.IsEnabled,
            VolumeSlider.Value, $"{_playbackGeneration}:{_current?.RequestId}", _player.SpeedRatio, _player.IsPlaying && _pendingSeek is null,
            coverUri: _current?.CoverUri);
    }
}
