namespace YMusic.Native;

public partial class MainWindow
{
    private void UpdateShellState()
    {
        if (_shell is null || _closed) return;
        _shell.Update(_current?.Title ?? "", _current?.Artist ?? "", _player.IsPlaying,
            NextButton.IsEnabled, PreviousButton.IsEnabled);
        _shell.UpdateDetails(PlayerCover.Source, DisplayPosition(_player.Position).TotalSeconds,
            _player.Duration.TotalSeconds, SeekSlider.IsEnabled,
            _current is { } row && _favoriteTrackIds.Contains(row.Id), PlayerLikeButton.IsEnabled,
            VolumeSlider.Value, $"{_playbackGeneration}:{_current?.RequestId}");
    }
}
