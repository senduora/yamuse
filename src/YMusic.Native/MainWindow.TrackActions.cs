using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using ShapePath = System.Windows.Shapes.Path;

namespace YMusic.Native;

public partial class MainWindow
{
    private sealed record TrackActionTarget(TrackRow Track, int AccountGeneration, string? AccountUid,
        bool LikeDesired, TrackRow[] Downloads);
    private Action<string>? _trackLinkCopier = null;

    private void TrackMore_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is FrameworkElement anchor && anchor.Tag is TrackRow track) OpenTrackActionsMenu(anchor, track);
    }
    private void PlayerMore_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is FrameworkElement anchor && _current is { } track) OpenTrackActionsMenu(anchor, track);
    }
    private void OpenTrackActionsMenu(FrameworkElement anchor, TrackRow track)
    {
        if (_closed) return;
        var menu = CreateTrackActionsMenu(anchor, () => track, useSelection: false);
        anchor.ContextMenu = menu;
        menu.IsOpen = true;
    }
    private void AttachTrackActionsMenu(FrameworkElement anchor, Func<TrackRow?> selectedTrack)
    {
        if (anchor.ContextMenu is { } existing && existing.Resources.Contains("YamuseTrackActions")) return;
        anchor.ContextMenu = CreateTrackActionsMenu(anchor, selectedTrack, useSelection: ReferenceEquals(anchor, TracksList));
    }
    private ContextMenu CreateTrackActionsMenu(FrameworkElement anchor, Func<TrackRow?> selectedTrack, bool useSelection)
    {
        var menu = new ContextMenu { PlacementTarget = anchor, Placement = PlacementMode.Bottom };
        menu.Resources["YamuseTrackActions"] = true;
        TrackActionTarget? target = null;
        TrackRow? pointerTarget = null;
        var hasPointerTarget = false;
        bool Current(TrackActionTarget state) => !_closed && state.AccountGeneration == _accountGeneration && state.AccountUid == _account?.Uid;
        MenuItem Action(string key, string title, string geometry, Func<TrackActionTarget, Task> perform, bool download = false)
        {
            var item = new MenuItem { Name = key, Header = title, IsEnabled = false, Icon = TrackActionIcon(geometry, download) };
            if (!download) BindTrackActionGlyph(item);
            item.Click += async (_, e) =>
            {
                e.Handled = true;
                if (target is not { } state || !item.IsEnabled || !Current(state)) return;
                try { await perform(state); }
                catch (OperationCanceledException) { }
                catch (Exception ex) { if (Current(state)) ShowError(ex); }
            };
            menu.Items.Add(item); return item;
        }
        var like = Action("TrackActionLike", "Добавить в Мне нравится", "MenuLikeGeometry",
            state => SetTrackLikeAsync(state.Track, state.LikeDesired));
        var wave = Action("TrackActionWave", TrackWaveActionText, "MenuWaveGeometry", async state =>
        { if (IsExpandedPlayerOpen) CloseExpandedPlayer(); await OpenTrackWaveAsync(state.Track); });
        menu.Items.Add(new Separator());
        var next = Action("TrackActionNext", "Играть следующим", "MenuNextGeometry", state =>
        { EnqueueActionTrack(state.Track, true); return Task.CompletedTask; });
        var enqueue = Action("TrackActionEnqueue", "Добавить в очередь", "MenuQueueGeometry", state =>
        { EnqueueActionTrack(state.Track, false); return Task.CompletedTask; });
        var playlist = Action("TrackActionPlaylist", "Добавить в плейлист…", "MenuPlaylistGeometry", async state =>
        { if (IsExpandedPlayerOpen) CloseExpandedPlayer(); await AddTrackToPlaylistAsync(state.Track); });
        menu.Items.Add(new Separator());
        var download = Action("TrackActionDownload", "Скачать для офлайн", "MenuDownloadGeometry",
            state => StartDownloadBatchAsync(state.Downloads, "Скачивание", null, automatic: false), download: true);
        var remove = Action("TrackActionRemoveOffline", "Удалить трек из офлайна", "MenuDeleteGeometry",
            state => DeleteOfflineTrackAsync(state.Track), download: true);
        menu.Items.Add(new Separator());
        var album = Action("TrackActionAlbum", "Перейти к альбому", "M 3,3 L 21,3 21,21 3,21 Z M 12,7 A 5,5 0 1 1 11.99,7 M 12,11 A 1,1 0 1 1 11.99,11", async state =>
        { if (IsExpandedPlayerOpen) CloseExpandedPlayer(); await OpenTrackAlbumAsync(state.Track); });
        var artists = new MenuItem { Name = "TrackActionArtists", Header = "Перейти к исполнителю", IsEnabled = false,
            Icon = TrackActionIcon("M 8,7 A 4,4 0 1 1 15.99,7 A 4,4 0 1 1 8,7 M 4,22 C 4,12 20,12 20,22") };
        BindTrackActionGlyph(artists);
        menu.Items.Add(artists);
        var link = Action("TrackActionLink", "Копировать ссылку на трек", "M 9,14 L 15,8 M 8,17 L 6,19 A 4,4 0 0 1 1,13 L 6,8 M 15,5 L 17,3 A 4,4 0 0 1 23,9 L 18,14", state =>
        {
            if (TrackShareUri(state.Track) is { } uri)
            {
                if (_trackLinkCopier is { } copy) copy(uri.AbsoluteUri); else Clipboard.SetText(uri.AbsoluteUri);
                SetStatus("Ссылка на трек скопирована");
            }
            return Task.CompletedTask;
        });
        menu.Opened += (_, _) =>
        {
            var track = hasPointerTarget ? pointerTarget : selectedTrack();
            hasPointerTarget = false; pointerTarget = null;
            target = track is null || _closed ? null : new(track, _accountGeneration, _account?.Uid,
                !_favoriteTrackIds.Contains(track.Id), useSelection && TracksList.SelectedItems.Contains(track)
                    ? TracksList.SelectedItems.OfType<TrackRow>().Where(row => row.Available && row.LocalPath is null).ToArray()
                    : track.Available && track.LocalPath is null ? [track] : []);
            foreach (var item in menu.Items.OfType<MenuItem>()) item.IsEnabled = false;
            artists.Items.Clear();
            if (target is not { } state) return;
            track = state.Track;
            var online = _session is not null;
            like.Header = state.LikeDesired ? "Добавить в Мне нравится" : "Убрать из Мне нравится";
            like.IsEnabled = online && _account is not null && _favoriteIdsKnown && !_catalogWriting && !_demo && track!.Id.Length > 0;
            ((ShapePath)like.Icon).SetResourceReference(ShapePath.StrokeProperty, state.LikeDesired ? "MutedBrush" : "AccentTextBrush");
            wave.IsEnabled = online && MusicApi.IsTrackWaveSeed(track!.Id);
            next.IsEnabled = enqueue.IsEnabled = track!.CanPlay;
            playlist.IsEnabled = online && _account is not null && !_catalogWriting && track.LocalPath is null && TrackReference(track).AlbumId is not null;
            download.IsEnabled = online && state.Downloads.Length > 0 && !_offlineMaintenance && _downloadCts is null;
            download.Header = state.Downloads.Length > 1 ? $"Скачать выбранные · {state.Downloads.Length}" : "Скачать для офлайн";
            remove.IsEnabled = track.IsDownloaded && !_offlineMaintenance;
            album.IsEnabled = online && track.LocalPath is null && track.Id.Length > 0;
            foreach (var artist in track.Artists)
            {
                var item = new MenuItem { Header = artist.Name };
                item.Click += async (_, e) =>
                {
                    e.Handled = true;
                    if (!ReferenceEquals(target, state) || !Current(state) || _session is null) return;
                    if (IsExpandedPlayerOpen) CloseExpandedPlayer();
                    await OpenArtistAsync(artist.Id, artist.Name);
                };
                artists.Items.Add(item);
            }
            artists.IsEnabled = online && artists.Items.Count > 0;
            link.IsEnabled = TrackShareUri(track) is not null;
        };
        menu.Closed += (_, _) => { target = null; pointerTarget = null; hasPointerTarget = false; artists.Items.Clear(); };
        if (anchor is ListBox list)
        {
            list.AddHandler(Mouse.PreviewMouseDownEvent, new MouseButtonEventHandler((_, e) =>
            {
                if (e.ChangedButton != MouseButton.Right) { hasPointerTarget = false; return; }
                var container = ItemsControl.ContainerFromElement(list, e.OriginalSource as DependencyObject) as ListBoxItem;
                hasPointerTarget = true;
                pointerTarget = container?.DataContext switch
                { TrackRow row => row, CatalogItem { Kind: "track", Track: { } row } => row, DownloadEntry entry => entry.Track, _ => null };
                if (container is null) { list.SelectedIndex = -1; return; }
                if (!container.IsSelected) list.SelectedItem = container.DataContext;
            }), handledEventsToo: true);
            list.PreviewKeyDown += (_, _) => hasPointerTarget = false;
        }
        return menu;
    }
    private ShapePath TrackActionIcon(string geometry, bool download = false)
    {
        var data = TryFindResource(geometry) as Geometry ?? Geometry.Parse(geometry);
        var icon = new ShapePath { Data = data, Style = (Style)FindResource("MenuGlyph") };
        if (download) icon.SetResourceReference(ShapePath.StrokeProperty, "DownloadBrush");
        return icon;
    }
    private static void BindTrackActionGlyph(MenuItem item)
    {
        // Icon presenters can cross a popup/template boundary. Their owning
        // menu item remains stable even before the icon gains a visual parent.
        if (item.Icon is ShapePath icon)
            icon.SetBinding(ShapePath.StrokeProperty, new Binding(nameof(Control.Foreground)) { Source = item, Mode = BindingMode.OneWay });
    }
    private void EnqueueActionTrack(TrackRow track, bool next)
    {
        if (!track.CanPlay || _closed) return;
        _playbackQueue.Enqueue(track, next);
        if (_section == "queue") ShowQueue();
        SetStatus(next ? "Трек будет играть следующим" : "Трек добавлен в конец очереди"); UpdateControls();
    }
    private static Uri? TrackShareUri(TrackRow track) => MusicApi.IsTrackWaveSeed(track.Id)
        ? new Uri("https://music.yandex.ru/track/" + Uri.EscapeDataString(track.Id)) : null;
}
