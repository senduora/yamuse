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
    { WaveMode.Track => "Волна по треку", WaveMode.Collection => "Волна по коллекции", _ => "Моя волна" };
    private bool IsCurrentWaveView => _waveBatch?.Mode == _waveViewMode
        && (_waveViewMode != WaveMode.Track || _waveBatch.TrackSeedId == _waveSeedTrack?.Id);
    private bool IsWaveTrackInView(TrackRow track) => _waveSources.TryGetValue(track, out var source) && source.Mode == _waveViewMode
        && (_waveViewMode != WaveMode.Track || source.TrackSeedId == _waveSeedTrack?.Id);
    private string WaveSourceLabel(TrackRow track) => _waveSources[track].Mode switch
    { WaveMode.Track => "Волна по треку · рекомендации Яндекса", WaveMode.Collection => "Волна по коллекции · персональные рекомендации", _ => "Моя волна · персональные рекомендации" };

    private void SelectWaveView(WaveMode mode, TrackRow? seed)
    {
        // Capture the old mode and seed before changing the view. Two track
        // stations share the same section but are distinct history entries.
        if (!_restoringBrowse && _featuresReady && _account is not null
            && (_section != "wave" || _waveViewMode != mode || _waveSeedTrack?.Id != seed?.Id)) RememberBrowseView();
        _waveViewMode = mode; _waveSeedTrack = seed;
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
        TrackRow? target = null, pointerTarget = null; var account = -1; var hasPointerTarget = false;
        menu.Opened += (_, _) =>
        {
            target = hasPointerTarget ? pointerTarget : selectedTrack(); hasPointerTarget = false; account = _accountGeneration;
            action.IsEnabled = _session is not null && !_closed && MusicApi.IsTrackWaveSeed(target?.Id);
        };
        action.Click += async (_, e) =>
        {
            e.Handled = true;
            if (target is null || !action.IsEnabled || account != _accountGeneration || _session is null || _closed) return;
            if (IsExpandedPlayerOpen) CloseExpandedPlayer();
            await OpenTrackWaveAsync(target);
        };
        menu.Closed += (_, _) => { target = pointerTarget = null; hasPointerTarget = false; };
        if (anchor is ListBox list)
        {
            list.AddHandler(Mouse.PreviewMouseDownEvent, new MouseButtonEventHandler((_, e) =>
            {
                if (e.ChangedButton != MouseButton.Right) { hasPointerTarget = false; return; }
                // The selected item can differ from the row under the pointer,
                // especially after sorting. Blank space must not reuse it.
                var container = ItemsControl.ContainerFromElement(list, e.OriginalSource as DependencyObject) as ListBoxItem;
                hasPointerTarget = true;
                pointerTarget = container?.DataContext switch
                { TrackRow row => row, CatalogItem { Kind: "track", Track: { } row } => row, DownloadEntry entry => entry.Track, _ => null };
                if (container is null) { list.SelectedIndex = -1; return; }
                if (!container.IsSelected) list.SelectedItem = container.DataContext;
            }), handledEventsToo: true);
            list.PreviewKeyDown += (_, _) => hasPointerTarget = false;
        }
    }
}
