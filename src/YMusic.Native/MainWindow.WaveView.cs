using System.Windows;

namespace YMusic.Native;

public partial class MainWindow
{
    private TrackRow? _waveHeroTrack;

    private async Task OpenWaveAsync(WaveMode mode)
    {
        _waveViewMode = mode;
        await StartWaveViewAsync(false);
    }

    private async void CollectionWave_Click(object sender, RoutedEventArgs e) => await OpenWaveAsync(WaveMode.Collection);
    private async void NewWave_Click(object sender, RoutedEventArgs e) => await StartWaveViewAsync(true);
    private async void WavePlay_Click(object sender, RoutedEventArgs e)
    {
        if (_waveStarting) return;
        if (_current is not null && _waveSources.ContainsKey(_current) && _waveBatch?.Mode == _waveViewMode && _queue.Any(row => ReferenceEquals(row, _current)))
        { await TogglePlaybackAsync(); return; }
        if (_waveBatch?.Mode != _waveViewMode || _waveTracks.Count == 0) { await StartWaveViewAsync(true); return; }
        _playbackQueue.SetItems(_waveTracks);
        if (_playbackQueue.Current is { } first) await PlayTrackAsync(first);
    }

    private void UpdateMainContentVisibility()
    {
        SettingsPanel.Visibility = _settingsOpen ? Visibility.Visible : Visibility.Collapsed;
        LyricsPanel.Visibility = !_settingsOpen && _lyricsOpen ? Visibility.Visible : Visibility.Collapsed;
        var browseVisible = !_settingsOpen && !_lyricsOpen;
        WavePanel.Visibility = browseVisible && _section == "wave" ? Visibility.Visible : Visibility.Collapsed;
        HomePanel.Visibility = browseVisible && _section == "home" ? Visibility.Visible : Visibility.Collapsed;
        ArtistPanel.Visibility = browseVisible && _section == "artist-detail" ? Visibility.Visible : Visibility.Collapsed;
        ContentGrid.Visibility = browseVisible && _section is not ("wave" or "home" or "artist-detail") ? Visibility.Visible : Visibility.Collapsed;
        var downloads = _section == "download-center";
        DownloadCenterPanel.Visibility = browseVisible && downloads ? Visibility.Visible : Visibility.Collapsed;
        if (downloads)
        {
            TracksList.Visibility = CatalogList.Visibility = EmptyState.Visibility = Visibility.Collapsed;
            CatalogSearchPanel.Visibility = LoginPanel.Visibility = CodePanel.Visibility = Visibility.Collapsed;
        }
        else
        {
            TracksList.Visibility = _catalogShowing ? Visibility.Collapsed : Visibility.Visible;
            CatalogList.Visibility = _catalogShowing ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void UpdateWavePresentation()
    {
        if (WavePanel is null) return;
        var collection = _waveViewMode == WaveMode.Collection;
        WaveHeading.Text = collection ? "Волна по коллекции" : "Моя волна";
        WaveModeCaption.Text = collection ? "Ваша коллекция задаёт настроение" : "Музыка, которая подходит именно вам";
        var ownWave = _waveBatch?.Mode == _waveViewMode;
        var track = ownWave && _current is not null && _waveSources.ContainsKey(_current) ? _current : ownWave ? _waveTracks.FirstOrDefault() : null;
        WaveTrackTitle.Text = track?.Title ?? (_waveStarting ? "Подбираем музыку…" : "Откройте свою музыку заново");
        WaveTrackArtist.Text = track?.Artist ?? (collection ? "Любимые треки и близкая вам музыка" : "Персональные рекомендации Яндекса");
        if (!ReferenceEquals(track, _waveHeroTrack))
        {
            _waveHeroTrack = track;
            Artwork.SetSourceUri(WaveCover, track?.CoverUri);
            Motion.Enter(WaveCover, 3);
            Motion.Enter(WaveTrackTitle, 3);
        }
        var playingThis = ownWave && ReferenceEquals(track, _current) && _player.HasSource;
        WavePlayButton.Content = _waveStarting ? "Подбираем музыку…" : playingThis && _player.IsPlaying ? "Ⅱ  Пауза" : "▶  Слушать волну";
        WavePlayButton.IsEnabled = _session is not null && !_waveStarting && !_loadingStream;
        WaveNextButton.IsEnabled = playingThis && NextButton.IsEnabled;
    }
}
