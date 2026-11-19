using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace YMusic.Native;

public partial class MainWindow
{
    private TrackRow? _waveSeedTrack;
    private int _waveRequestGeneration;
    private const string TrackWaveActionText = "Волна по этому треку";
    private string WaveViewTitle => _waveViewMode switch
    { WaveMode.Track => "Волна по треку", WaveMode.Album => "Волна по альбому", WaveMode.Collection => "Волна по коллекции", _ => "Моя волна" };
    private bool IsCurrentWaveView => _waveBatch?.Mode == _waveViewMode
        && (_waveViewMode != WaveMode.Track || _waveBatch.TrackSeedId == _waveSeedTrack?.Id)
        && (_waveViewMode != WaveMode.Album || _waveBatch.AlbumSeedId == _waveSeedAlbum?.Id);
    private bool IsWaveTrackInView(TrackRow track) => _waveSources.TryGetValue(track, out var source) && source.Mode == _waveViewMode
        && ReferenceEquals(source.Station, _waveBatch?.Station)
        && (_waveViewMode != WaveMode.Track || source.TrackSeedId == _waveSeedTrack?.Id)
        && (_waveViewMode != WaveMode.Album || source.AlbumSeedId == _waveSeedAlbum?.Id);
    private string WaveSourceLabel(TrackRow track) => !_waveSources.TryGetValue(track, out var source) ? "" : source.Mode switch
    { WaveMode.Track => "Волна по треку · рекомендации Яндекса", WaveMode.Album => "Волна по альбому · рекомендации Яндекса", WaveMode.Collection => "Волна по коллекции · персональные рекомендации", _ => "Моя волна · персональные рекомендации" };

    private void SelectWaveView(WaveMode mode, TrackRow? seed) => SelectSeededWaveView(mode, seed, null);

    private void SelectSeededWaveView(WaveMode mode, TrackRow? seed, CatalogItem? albumSeed)
    {
        // Capture the old mode and seed before changing the view. Two track
        // stations share the same section but are distinct history entries.
        if (!_restoringBrowse && _featuresReady && _account is not null
            && (_section != "wave" || _waveViewMode != mode || _waveSeedTrack?.Id != seed?.Id || _waveSeedAlbum?.Id != albumSeed?.Id)) RememberBrowseView();
        _waveViewMode = mode; _waveSeedTrack = seed; _waveSeedAlbum = albumSeed;
        NavigateSection("wave", WaveViewTitle, remember: false);
    }

    private async Task OpenTrackWaveAsync(TrackRow track)
    {
        if (_session is null) { SetStatus("Войдите в Яндекс, чтобы запустить волну по треку."); return; }
        if (!MusicApi.IsTrackWaveSeed(track.Id)) { SetStatus("У этого трека нет точного ID для запуска волны.", true); return; }
        SelectWaveView(WaveMode.Track, track);
        await StartWaveViewAsync(true);
    }

    private void InitializeTrackWaveActions()
    {
        AttachTrackWaveMenu(TracksList, () => TracksList.SelectedItem as TrackRow);
        AttachTrackWaveMenu(CatalogList, () => (CatalogList.SelectedItem as CatalogItem) is { Kind: "track", Track: { } track } ? track : null);
        AttachTrackWaveMenu(NowPlayingPanel, () => _current);
        AttachTrackWaveMenu(PlayerTitleButton, () => _current);
        AttachTrackWaveMenu(PlayerCoverButton, () => _current);
        AttachTrackWaveMenu(ExpandedPlayerHost, () => _current);
        // DownloadCenter creates its list after InitializeFeatures. It is ready
        // when the host loads; use the exact DownloadEntry.Track, never a label.
        DownloadCenterPanel.Loaded += (_, _) =>
        {
            if (_downloadEntriesList is { } list) AttachTrackWaveMenu(list, () => (list.SelectedItem as DownloadEntry)?.Track);
        };
    }

    private void AttachTrackWaveMenu(FrameworkElement anchor, Func<TrackRow?> selectedTrack)
    {
        // Downloads also owns batch selection and confirmed-unlike commands.
        if (ReferenceEquals(anchor, _downloadEntriesList)) AttachDownloadTrackWaveMenu(anchor, selectedTrack);
        else AttachTrackActionsMenu(anchor, selectedTrack);
    }

    private void AttachDownloadTrackWaveMenu(FrameworkElement anchor, Func<TrackRow?> selectedTrack)
    {
        var menu = anchor.ContextMenu ??= new ContextMenu();
        if (menu.Items.OfType<MenuItem>().Any(item => item.Header as string == TrackWaveActionText)) return;
        var action = new MenuItem { Header = TrackWaveActionText, IsEnabled = false,
            Icon = new System.Windows.Shapes.Path { Data = (System.Windows.Media.Geometry)FindResource("MenuWaveGeometry"), Style = (Style)FindResource("MenuGlyph") } };
        BindTrackActionGlyph(action);
        menu.Items.Add(action);
        TrackRow? target = null, openingTarget = null; var account = -1; var hasOpeningTarget = false;
        menu.Opened += (_, _) =>
        {
            target = hasOpeningTarget ? openingTarget : selectedTrack();
            openingTarget = null; hasOpeningTarget = false; account = _accountGeneration;
            action.IsEnabled = _session is not null && !_closed && MusicApi.IsTrackWaveSeed(target?.Id);
        };
        action.Click += async (_, e) =>
        {
            e.Handled = true;
            if (target is null || !action.IsEnabled || account != _accountGeneration || _session is null || _closed) return;
            if (IsExpandedPlayerOpen) CloseExpandedPlayer();
            await OpenTrackWaveAsync(target);
        };
        menu.Closed += (_, _) => { target = openingTarget = null; hasOpeningTarget = false; };
        if (anchor is ListBox list)
        {
            list.ContextMenuOpening += (_, e) =>
            {
                var source = e.OriginalSource as DependencyObject;
                hasOpeningTarget = e.CursorLeft != -1 || e.CursorTop != -1 || !ReferenceEquals(source, list);
                openingTarget = source is not null && ItemsControl.ContainerFromElement(list, source) is ListBoxItem { DataContext: DownloadEntry entry }
                    ? entry.Track : null;
            };
        }
    }
}
