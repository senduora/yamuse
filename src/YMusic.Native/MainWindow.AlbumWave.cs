using System.Windows;

namespace YMusic.Native;

public partial class MainWindow
{
    private CatalogItem? _waveSeedAlbum;

    private void UpdateAlbumWaveButton()
    {
        if (AlbumWaveButton is null) return;
        AlbumWaveButton.IsEnabled = !_closed && _session is not null && !_waveStarting
            && _section == "album-detail" && MusicApi.IsAlbumWaveSeed(_activeAlbumDetails?.Id);
    }

    private async void AlbumWave_Click(object sender, RoutedEventArgs e)
    {
        if (_section != "album-detail" || _activeAlbumDetails is not { } album || !AlbumWaveButton.IsEnabled) return;
        await OpenAlbumWaveAsync(album);
    }

    private async Task OpenAlbumWaveAsync(AlbumDetails album)
    {
        if (_session is null) { SetStatus("Войдите в Яндекс, чтобы запустить волну по альбому."); return; }
        if (!MusicApi.IsAlbumWaveSeed(album.Id)) { SetStatus("У этого альбома нет точного ID для запуска волны.", true); return; }
        SelectSeededWaveView(WaveMode.Album, null, new(album.Id, "album", album.Title, album.Artists, CoverUri: album.CoverUri));
        await StartWaveViewAsync(true);
    }
}
