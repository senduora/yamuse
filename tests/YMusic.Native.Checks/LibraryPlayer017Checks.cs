using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using YMusic.Native;

internal static partial class Program
{
    private static async Task CheckLibraryPlayer017()
    {
        await Run("Library017: like marks follow confirmed exact track IDs across separate view instances", Library017LikeMarks);
        await Run("Library017: failed like writes retain confirmed state and release the action", Library017FailedLike);
        await Run("Library017: saved audio is green across other playlists, albums, search and artist; removal clears every mark", Library017OfflineMarks);
        await Run("Library017: marks precede covers and only favorites omit ordinal numbers across album playlist and queue navigation", Library017FavoritesLayout);
        await Run("Library017: track cover single click uses the selected filtered sorted playlist without album navigation", Library017CoverPlay);
        await Run("Library017: transparent Play is visible only over the cover and absent from playlist cards", Library017CoverHover);
        await Run("Library017: saved track cover plays without an account and unavailable covers cannot play", Library017OfflinePlay);
        await Run("Library017: cover click retains repeated occurrence and virtualization in a 3900-track queue", Library017LargeQueueCover);
        await Run("Library017: search and artist track covers reuse exact one-click playback", Library017OtherTrackCovers);
    }

    private static async Task Library017LikeMarks()
    {
        using var f = new Library017Fixture("likes");
        var visible = f.Row("shared", "album-b"); var sameName = f.Row("different", "album-b");
        var queued = f.Row("shared", "album-c"); var searched = f.Row("shared", "album-d");
        SaField<PlaybackQueue>(f.Window, "_playbackQueue").SetItems([queued]);
        SaField<ObservableCollection<CatalogItem>>(f.Window, "_catalogItems").Add(new("shared", "track", "Трек", "", Track: searched));
        SaInvoke(f.Window, "ShowPlaylist", new PlaylistDetails("owner", "42", "Другой плейлист", "", 1, [visible, sameName]));
        SaInvoke(f.Window, "UpdatePlayerLikeState"); f.Window.UpdateLayout();
        Assert(new[] { visible, queued, searched }.All(row => row.IsLiked && row.LikeStateKnown && row.CanToggleLike)
            && !sameName.IsLiked, "like marks used names/album contexts or omitted a view instance");
        var button = Library017LikeButton(f.Window, visible);
        Assert(button.IsEnabled && button.Content.ToString() == "\uEB52", "confirmed heart is not rendered in playlist row");
        SaClick(button);
        await Until(() => f.Writes.Count == 1 && !SaField<bool>(f.Window, "_catalogWriting"));
        Assert(f.Writes[0] == ("remove", "shared:album-b") && new[] { visible, queued, searched }.All(row => !row.IsLiked)
            && button.Content.ToString() == "\uEB51", "unlike targeted the wrong composite ID or left another view liked");
        SaClick(button);
        await Until(() => f.Writes.Count == 2 && !SaField<bool>(f.Window, "_catalogWriting")
            && SaField<CancellationTokenSource?>(f.Window, "_libraryCts") is null);
        Assert(f.Writes[1] == ("add-multiple", "shared:album-b") && new[] { visible, queued, searched }.All(row => row.IsLiked)
            && !sameName.IsLiked, "like confirmation/refresh lost another view's exact identity");
        var queue = SaField<PlaybackQueue>(f.Window, "_playbackQueue");
        Assert(ReferenceEquals(queue.Current, queued) && !f.Player.HasSource, "heart action started audio or replaced the queue");
        foreach (var (width, height) in new[] { (1280, 860), (1050, 680) })
        {
            f.Window.Width = width; f.Window.Height = height; f.Window.UpdateLayout(); await Task.Delay(180);
            var heart = Library017LikeButton(f.Window, visible);
            var rect = new Rect(heart.TranslatePoint(new Point(), f.Window), heart.RenderSize);
            Assert(heart.ActualWidth > 0 && rect.Right <= f.Window.ActualWidth && rect.Bottom <= f.Window.ActualHeight, "track actions clipped at narrow window");
            App.Capture(f.Window, Path.Combine(_directory, $"library-017-likes-{width}.png"));
        }
    }

    private static async Task Library017FailedLike()
    {
        using var f = new Library017Fixture("failed-like") { FailMutation = true };
        var row = f.Row("shared", "album-b"); SaInvoke(f.Window, "ShowRows", (object)new[] { row }); f.Window.UpdateLayout();
        var button = Library017LikeButton(f.Window, row); SaClick(button);
        await Until(() => f.Writes.Count == 1 && !SaField<bool>(f.Window, "_catalogWriting"));
        Assert(row.IsLiked && button.Content.ToString() == "\uEB52" && button.IsEnabled, "failed write erased confirmed mark or left action disabled");
        Library017Set(f.Window, "_favoriteIdsKnown", false); SaInvoke(f.Window, "UpdatePlayerLikeState");
        Assert(!button.IsEnabled && !row.LikeStateKnown, "unknown collection state allowed a like write");
    }

    private static async Task Library017OfflineMarks()
    {
        using var f = new Library017Fixture("offline-marks");
        var saved = await f.SaveShared();
        var playlist = f.Row("shared", "album-b"); var album = f.Row("shared", "album-c");
        var search = f.Row("shared", "album-d"); var artist = f.Row("shared", "album-e"); var other = f.Row("different", "album-b");
        SaField<PlaybackQueue>(f.Window, "_playbackQueue").SetItems([playlist, album]);
        SaInvoke(f.Window, "ShowPlaylist", new PlaylistDetails("owner", "other", "Чужой плейлист", "", 1, [playlist, other]));
        Assert(playlist.IsDownloaded && !other.IsDownloaded, "download mark did not cross playlist/album context by exact track ID");
        f.Window.UpdateLayout();
        var firstBadge = SaVisuals<TextBlock>(Control<ListView>(f.Window, "TracksList")).Single(text => text.Name == "TrackDownloadedBadge" && ReferenceEquals(text.DataContext, playlist));
        var green = ((SolidColorBrush)f.Window.FindResource("DownloadBrush")).Color;
        Assert(firstBadge.IsVisible && ((SolidColorBrush)firstBadge.Foreground).Color == green && green.G > green.R && green.G > green.B, "saved playlist indicator is absent or not green");
        SaInvoke(f.Window, "ShowCatalogItems", new[] { new CatalogItem("shared", "track", "Трек", "", Track: search) }, false);
        Assert(search.IsDownloaded, "saved mark missing from catalog search");
        SaInvoke(f.Window, "ShowArtistDetails", new ArtistDetails("a", "Исполнитель", null, null, [artist], [], [], ""));
        Assert(artist.IsDownloaded, "saved mark missing from artist's popular tracks");
        SaInvoke(f.Window, "ShowRows", (object)new[] { album }); f.Window.UpdateLayout();
        Assert(album.IsDownloaded, "saved mark missing from album view");
        var priorAccent = Application.Current.Resources["AccentBrush"];
        try
        {
            Application.Current.Resources["AccentBrush"] = new SolidColorBrush(Colors.MediumPurple);
            f.Window.UpdateLayout();
            var badge = SaVisuals<TextBlock>(Control<ListView>(f.Window, "TracksList")).Single(text => text.Name == "TrackDownloadedBadge" && ReferenceEquals(text.DataContext, album));
            Assert(((SolidColorBrush)badge.Foreground).Color == green, "cover accent recolored the download indicator");
            App.Capture(f.Window, Path.Combine(_directory, "library-017-green-album.png"));
        }
        finally { Application.Current.Resources["AccentBrush"] = priorAccent; }
        SaField<List<TrackRow>>(f.Window, "_favoritesCache").Add(search);
        File.Delete(f.Library.ResolvePath(saved.FileName)); SaInvoke(f.Window, "RefreshDownloadedMarks");
        Assert(new[] { playlist, album, search, artist }.All(row => !row.IsDownloaded), "removed saved audio retained a stale badge in another view");
        Assert(f.MediaRequests == 1 && f.Writes.Count == 0, "reading/removing download marks downloaded or mutated a track");
    }

    private static async Task Library017FavoritesLayout()
    {
        using var f = new Library017Fixture("favorites-layout"); await f.SaveShared();
        var window = f.Window; var list = Control<ListView>(window, "TracksList"); var grid = (GridView)list.View;
        var rows = Enumerable.Range(0, 120).Select(index => new TrackRow
        {
            Number = index + 1, Id = index == 23 ? "shared" : "layout-" + index,
            RequestId = (index == 23 ? "shared" : "layout-" + index) + ":album-layout", AlbumId = "album-layout",
            Title = $"Трек {119 - index:000} с длинным названием для проверки расположения отметок",
            Artist = "Исполнитель", Album = "Альбом", DurationMs = 4000, Available = true
        }).ToArray();
        SaField<List<TrackRow>>(window, "_favoritesCache").AddRange(rows);
        SaInvoke(window, "ShowRows", (object)rows); window.UpdateLayout(); await Task.Delay(80);
        string[] Fields() => grid.Columns.Select(column => (string)((GridViewColumnHeader)column.Header).Tag).ToArray();
        Assert(SaField<string>(window, "_section") == "favorites"
            && Fields().SequenceEqual(new[] { "Downloaded", "Title", "Artist", "Album", "Duration" }),
            "first favorites view retained numbering or placed marks after covers");
        var columns = grid.Columns.ToArray();
        list.SelectedItems.Add(rows[0]); list.SelectedItems.Add(rows[23]);
        SortHeader(window, ListSortField.Downloaded);
        Assert(ReferenceEquals(list.Items[0], rows[23]) && list.SelectedItems.Contains(rows[0]) && list.SelectedItems.Contains(rows[23]),
            "relocated download header lost its sort action or multi-selection");
        foreach (var (width, height) in new[] { (1280, 860), (1050, 680) })
        {
            window.Width = width; window.Height = height; list.ScrollIntoView(rows[23]); window.UpdateLayout(); await Task.Delay(100);
            var container = (ListViewItem)list.ItemContainerGenerator.ContainerFromItem(rows[23])!;
            var cover = Library017Cover(window, rows[23]); var heart = Library017LikeButton(window, rows[23]);
            var badge = SaVisuals<TextBlock>(container).Single(text => text.Name == "TrackDownloadedBadge");
            var title = SaVisuals<Button>(container).Single(button => button.Content is TextBlock text && text.Text == rows[23].Title);
            var artist = SaVisuals<Button>(container).Single(button => button.Content is TextBlock text && text.Text == rows[23].Artist);
            Rect Bounds(FrameworkElement element) => new(element.TranslatePoint(new Point(), list), element.RenderSize);
            var coverRect = Bounds(cover); var titleRect = Bounds(title); var heartRect = Bounds(heart); var badgeRect = Bounds(badge); var artistRect = Bounds(artist);
            Assert(heartRect.Left < 20 && heartRect.Right <= badgeRect.Left + 1 && badgeRect.Right <= coverRect.Left + 1
                && coverRect.Right <= titleRect.Left + 1 && titleRect.Right <= artistRect.Left + 1,
                "favorites heart and download mark do not precede the cover and title");
            Assert(heart.IsVisible && badge.IsVisible && artistRect.Right <= list.ActualWidth && grid.Columns.Sum(column => column.Width) <= list.ActualWidth,
                "favorites actions or trailing columns clipped at a supported width");
            foreach (var target in new[] { cover, heart })
            {
                var center = target.TranslatePoint(new Point(target.ActualWidth / 2, target.ActualHeight / 2), window);
                Assert(window.InputHitTest(center) is DependencyObject hit && Library017Within(hit, target), "relocated action lost its actual hit area");
            }
            var downloadedHeader = (GridViewColumnHeader)grid.Columns[0].Header;
            var titleHeader = (GridViewColumnHeader)grid.Columns[1].Header;
            Assert(titleHeader.IsVisible && downloadedHeader.IsVisible && titleHeader.ActualWidth > 100 && downloadedHeader.ActualWidth >= 62
                && downloadedHeader.TranslatePoint(new Point(), list).X < titleHeader.TranslatePoint(new Point(), list).X,
                "title/download sort headers no longer align with their columns");
            App.Capture(window, Path.Combine(_directory, $"library-017-favorites-layout-{width}.png"));
        }
        list.ScrollIntoView(rows[^1]); window.UpdateLayout(); await Task.Delay(80);
        Assert(list.ItemContainerGenerator.ContainerFromItem(rows[^1]) is ListViewItem
            && SaVisuals<ListViewItem>(list).Count() < 80 && VirtualizingPanel.GetVirtualizationMode(list) == VirtualizationMode.Recycling,
            "favorites column move disabled bounded recycling or access to the list tail");
        async Task CheckNumberedLayout(string section)
        {
            Assert(Fields().SequenceEqual(new[] { "Original", "Downloaded", "Title", "Artist", "Album", "Duration" }) && grid.Columns[0].Width == 40,
                section + " lost its ordinal column or placed marks after the cover");
            list.ScrollIntoView(rows[23]); window.UpdateLayout(); await Task.Delay(100);
            var container = (ListViewItem)list.ItemContainerGenerator.ContainerFromItem(rows[23])!;
            var number = SaVisuals<TextBlock>(container).Single(text => text.Text == rows[23].Number.ToString());
            var heart = Library017LikeButton(window, rows[23]); var cover = Library017Cover(window, rows[23]);
            var badge = SaVisuals<TextBlock>(container).Single(text => text.Name == "TrackDownloadedBadge");
            Rect Bounds(FrameworkElement element) => new(element.TranslatePoint(new Point(), list), element.RenderSize);
            Assert(number.IsVisible && heart.IsVisible && badge.IsVisible && Bounds(number).Left < 40
                && Bounds(number).Right <= Bounds(heart).Left + 1 && Bounds(heart).Right <= Bounds(badge).Left + 1
                && Bounds(badge).Right <= Bounds(cover).Left + 1, section + " does not show number, marks and cover in order");
            App.Capture(window, Path.Combine(_directory, "library-017-" + section + "-numbered-layout.png"));
        }
        SaInvoke(window, "SetSection", "album-detail", "Альбом"); SaInvoke(window, "ShowRows", (object)rows); window.UpdateLayout();
        await CheckNumberedLayout("album");
        var numberColumn = grid.Columns[0];
        SaClick(Control<Button>(window, "BrowseBackButton")); window.UpdateLayout(); await Task.Delay(80);
        Assert(SaField<string>(window, "_section") == "favorites" && grid.Columns.SequenceEqual(columns)
            && (ListSortField)Control<ComboBox>(window, "ListSortCombo").SelectedValue == ListSortField.Downloaded,
            "Back lost the favorites column identities/order or its sort");
        SaInvoke(window, "SetSection", "playlist-detail", "Плейлист");
        SaInvoke(window, "ShowPlaylist", new PlaylistDetails("owner", "layout-list", "Плейлист", "", 1, rows));
        await CheckNumberedLayout("playlist");
        SaClick(Control<Button>(window, "BrowseBackButton")); window.UpdateLayout(); await Task.Delay(80);
        Assert(SaField<string>(window, "_section") == "favorites" && grid.Columns.SequenceEqual(columns),
            "returning from a playlist did not restore favorites without numbers");
        SaField<PlaybackQueue>(window, "_playbackQueue").SetItems(rows);
        SaInvoke(window, "ShowQueue"); window.UpdateLayout();
        Assert(Fields().SequenceEqual(new[] { "Original", "Downloaded", "Title", "Artist", "Album", "Duration" })
            && ReferenceEquals(grid.Columns[0], numberColumn), "queue did not restore the original numbered columns");
        SortHeader(window, ListSortField.Original);
        SaClick(Control<Button>(window, "FavoritesNav")); window.UpdateLayout();
        Assert(grid.Columns.SequenceEqual(columns) && ReferenceEquals(list.Items[0], rows[23]) && !f.Player.HasSource && f.Writes.Count == 0,
            "returning to favorites retained queue columns/sort or changed playback/likes");
    }

    private static async Task Library017CoverPlay()
    {
        using var f = new Library017Fixture("track-cover");
        TrackRow Make(string id, string title) => new() { Id = id, RequestId = id + ":1", Title = title, Artist = "Исполнитель", AlbumId = "1", Album = "Альбом", DurationMs = 4000, Available = true };
        var first = Make("a", "Queue A"); var target = Make("b", "Queue B"); var last = Make("c", "Queue C"); var hidden = Make("hidden", "Другой трек");
        SaInvoke(f.Window, "ShowPlaylist", new PlaylistDetails("owner", "42", "Треки плейлиста", "", 1, [first, target, hidden, last]));
        var list = Control<ListView>(f.Window, "TracksList");
        var view = CollectionViewSource.GetDefaultView(list.ItemsSource);
        using (view.DeferRefresh())
        {
            view.Filter = value => value is TrackRow row && row.Title.StartsWith("Queue", StringComparison.Ordinal);
            view.SortDescriptions.Add(new SortDescription(nameof(TrackRow.Title), ListSortDirection.Descending));
        }
        f.Window.UpdateLayout(); await Task.Delay(60);
        var cover = Library017Cover(f.Window, target); var click = new RoutedEventArgs(Button.ClickEvent);
        cover.RaiseEvent(click); await Until(() => f.Player.IsPlaying && !SaField<bool>(f.Window, "_loadingStream"));
        var queue = SaField<PlaybackQueue>(f.Window, "_playbackQueue");
        Assert(click.Handled && ReferenceEquals(queue.Current, target) && queue.Items.SequenceEqual(new[] { last, target, first })
            && ReferenceEquals(SaField<TrackRow?>(f.Window, "_current"), target), "single cover click used another track or rebuilt the unfiltered API order");
        Assert(f.StreamTracks.Count(id => id.Split(':')[0] == target.Id) == 1 && f.Writes.Count == 0
            && !f.Paths.Any(path => path.StartsWith("/albums/", StringComparison.Ordinal) || path.Contains("/playlists/", StringComparison.Ordinal)),
            "one cover click duplicated playback, opened an album, or fetched the playlist again");
        Assert(SaField<string>(f.Window, "_section") == "playlist-detail", "cover playback navigated away from the playlist");
    }

    private static async Task Library017CoverHover()
    {
        using var f = new Library017Fixture("cover-hover"); var row = f.Row("shared", "1");
        SaInvoke(f.Window, "ShowRows", (object)new[] { row }); f.Window.UpdateLayout(); await Task.Delay(250);
        var list = Control<ListView>(f.Window, "TracksList"); var cover = Library017Cover(f.Window, row);
        var glyph = (System.Windows.Shapes.Path)cover.Template.FindName("TrackCoverPlayGlyph", cover);
        Assert(glyph.Opacity == 0 && ((SolidColorBrush)cover.Background).Color.A == 0
            && cover.BorderThickness == new Thickness(0), "idle cover has a Play badge or opaque button background");
        var container = (ListViewItem)list.ItemContainerGenerator.ContainerFromIndex(0);
        var rowHover = (Border)container.Template.FindName("RowHover", container);
        async Task<double> MidFade(FrameworkElement element)
        {
            // WPF starts the clock on the next render tick; reading immediately
            // after ChangeMouseOver can still return the new base value.
            await Task.Delay(40);
            var opacity = element.Opacity;
            Assert(!Motion.CanAnimate || opacity > 0 && opacity < 1,
                $"hover did not render an intermediate opacity: {element.Name}={opacity}");
            return opacity;
        }
        var previous = Mouse.PrimaryDevice.DirectlyOver;
        try
        {
            list.SelectedIndex = -1;
            Library017MouseOver(null);
            f.Window.UpdateLayout(); await Task.Delay(250);
            Assert(rowHover.IsVisible && rowHover.Opacity == 0 && !container.IsMouseOver,
                $"row hover fixture is not idle: visible={rowHover.IsVisible}, opacity={rowHover.Opacity}, selected={container.IsSelected}, over={container.IsMouseOver}");
            Library017MouseOver(container);
            Assert(!cover.IsMouseOver && glyph.Opacity == 0, "hovering the row reveals the cover Play");
            Assert(!Motion.CanAnimate || rowHover.HasAnimatedProperties,
                $"row hover appeared without a transition: animated={rowHover.HasAnimatedProperties}, opacity={rowHover.Opacity}, visible={rowHover.IsVisible}, selected={container.IsSelected}, over={container.IsMouseOver}");
            var rowFadeIn = await MidFade(rowHover);
            await Task.Delay(250);
            Assert(rowHover.Opacity == 1, "row hover did not settle");
            Library017MouseOver(cover);
            Assert(!Motion.CanAnimate || glyph.HasAnimatedProperties, "cover Play appeared without a transition");
            var coverFadeIn = await MidFade(glyph);
            await Task.Delay(250);
            Assert(cover.IsMouseOver && glyph.Visibility == Visibility.Visible && glyph.Opacity == 1 && ((SolidColorBrush)cover.Background).Color.A == 0,
                "cover hover did not reveal a transparent Play");
            var inside = VisualTreeHelper.HitTest(cover, new Point(17, 17));
            var outside = VisualTreeHelper.HitTest(list, cover.TranslatePoint(new Point(cover.ActualWidth + 3, 17), list));
            Assert(inside is not null && !Library017Within(outside?.VisualHit, cover), "cover hit area extends into the title/row gap");
            App.Capture(f.Window, Path.Combine(_directory, "track-cover-017-hover.png"));
            SaClick(cover); Library017MouseOver(container);
            Assert(!Motion.CanAnimate || glyph.HasAnimatedProperties, "cover Play disappeared without a transition");
            var coverFadeOut = await MidFade(glyph);
            await Task.Delay(250);
            Assert(!cover.IsMouseOver && glyph.Opacity == 0, "click left Play visible after the cursor left the cover");
            await Until(() => f.Player.IsPlaying && !SaField<bool>(f.Window, "_loadingStream"));
            Assert(container.IsSelected && rowHover.Visibility == Visibility.Collapsed, "hover overlay obscures the selected track");
            list.SelectedIndex = -1;
            Library017MouseOver(null);
            Assert(!Motion.CanAnimate || rowHover.HasAnimatedProperties, "row hover disappeared without a transition");
            var rowFadeOut = await MidFade(rowHover);
            await Task.Delay(250);
            Assert(rowHover.Opacity == 0, "row hover remained after pointer exit");
            File.WriteAllText(Path.Combine(_directory, "track-hover-motion.json"), JsonSerializer.Serialize(new
            { animationAssertionsRun = Motion.CanAnimate, rowFadeIn, rowFadeOut, coverFadeIn, coverFadeOut,
                rowAndCoverFadeInOut = true, selectedTrackPreserved = true, localWpfHoverOnly = true, nativeInputSent = false }));
            App.Capture(f.Window, Path.Combine(_directory, "track-cover-017-idle.png"));
        }
        finally { Library017MouseOver(previous); }
        SaInvoke(f.Window, "SetSection", "playlists", "Плейлисты");
        SaInvoke(f.Window, "ShowCatalogItems", new[] { new CatalogItem("42", "playlist", "Плейлист", "Автор", "owner", "42") }, false);
        f.Window.UpdateLayout();
        Assert(!SaVisuals<Button>(Control<ListBox>(f.Window, "CatalogList")).Any(button => button.Name is "TrackCoverPlayButton" or "CatalogCoverPlayButton"),
            "a playlist card still has the incorrectly placed Play");
    }

    private static async Task Library017OfflinePlay()
    {
        using var f = new Library017Fixture("offline-track-cover"); var saved = await f.SaveShared(); f.Window.SignOut();
        var offline = new TrackRow { Id = "shared", RequestId = "shared:another-album", Title = "Сохранённый трек", DurationMs = 4000, Available = false };
        var unavailable = new TrackRow { Id = "missing", RequestId = "missing:1", Title = "Недоступный", Available = false };
        SaInvoke(f.Window, "SetSection", "offline", "Скачанное"); SaInvoke(f.Window, "ShowRows", (object)new[] { offline, unavailable }); f.Window.UpdateLayout();
        var disabled = Library017Cover(f.Window, unavailable);
        Assert(!disabled.IsEnabled, "unavailable unsaved track exposes an enabled Play");
        var before = SaField<PlaybackQueue>(f.Window, "_playbackQueue").Version; SaClick(disabled);
        Assert(!f.Player.HasSource && SaField<PlaybackQueue>(f.Window, "_playbackQueue").Version == before, "disabled cover started a queue");
        SaClick(Library017Cover(f.Window, offline)); await Until(() => f.Player.IsPlaying && !SaField<bool>(f.Window, "_loadingStream"));
        Assert(f.Player.SourceLocalPath == f.Library.ResolvePath(saved.FileName) && f.ApiRequests == 0 && offline.IsDownloaded,
            "saved cover required login, used network, or lost its mark across album contexts");
    }

    private static async Task Library017LargeQueueCover()
    {
        using var f = new Library017Fixture("large-queue-cover"); await f.SaveShared();
        var repeated = f.Row("shared", "1"); const int targetIndex = 2000;
        var rows = Enumerable.Range(0, 3900).Select(index => index is 0 or targetIndex ? repeated : f.Row("track-" + index, "1")).ToArray();
        var queue = SaField<PlaybackQueue>(f.Window, "_playbackQueue"); queue.SetItems(rows, repeated);
        queue.SelectAt(targetIndex); var occurrence = queue.CurrentOccurrenceToken; queue.SelectAt(0); var version = queue.Version;
        SaInvoke(f.Window, "ShowQueue"); var list = Control<ListView>(f.Window, "TracksList"); f.Window.UpdateLayout(); await Task.Delay(150);
        Assert(list.Items.Count == 3900, "large queue was not presented");
        // Bring the precise occurrence through WPF, rather than guessing a
        // pixel offset or ScrollIntoView(repeated), which finds its first copy.
        var panel = SaVisuals<VirtualizingStackPanel>(list).First();
        var bring = typeof(VirtualizingStackPanel).GetMethod("BringIndexIntoView", BindingFlags.Instance | BindingFlags.NonPublic)!;
        bring.Invoke(panel, [targetIndex]); f.Window.UpdateLayout(); await Task.Delay(80);
        var container = (ListViewItem?)list.ItemContainerGenerator.ContainerFromIndex(targetIndex);
        var scroll = SaVisuals<ScrollViewer>(list).First();
        Assert(container is not null, $"target occurrence was not virtualized: offset={scroll.VerticalOffset}, extent={scroll.ExtentHeight}, viewport={scroll.ViewportHeight}");
        var cover = SaVisuals<Button>(container!).Single(button => button.Name == "TrackCoverPlayButton");
        SaClick(cover); await Until(() => f.Player.IsPlaying && !SaField<bool>(f.Window, "_loadingStream"));
        Assert(queue.Version == version + 1 && queue.Items.SequenceEqual(rows) && queue.CurrentIndex == targetIndex
            && queue.CurrentOccurrenceToken == occurrence && ReferenceEquals(queue.Current, repeated),
            $"cover click rebuilt the queue or chose the first occurrence: version={queue.Version}/{version}, index={queue.CurrentIndex}, token={queue.CurrentOccurrenceToken}/{occurrence}");
        Assert(Enumerable.Range(0, list.Items.Count).Count(index => list.ItemContainerGenerator.ContainerFromIndex(index) is not null) < 80,
            "cover buttons disabled large-list virtualization");
    }

    private static async Task Library017OtherTrackCovers()
    {
        using var f = new Library017Fixture("other-track-covers"); var first = f.Row("search-a", "1"); var second = f.Row("search-b", "1");
        SaInvoke(f.Window, "SetSection", "search", "Поиск");
        SaInvoke(f.Window, "ShowCatalogItems", new[] { new CatalogItem(first.Id, "track", "Первый", "", Track: first), new CatalogItem(second.Id, "track", "Второй", "", Track: second) }, false);
        f.Window.UpdateLayout();
        var searchCover = SaVisuals<Button>(Control<ListBox>(f.Window, "CatalogList")).Single(button => button.Name == "TrackCoverPlayButton" && ReferenceEquals(button.DataContext, second));
        SaClick(searchCover); await Until(() => f.Player.IsPlaying && !SaField<bool>(f.Window, "_loadingStream"));
        Assert(ReferenceEquals(SaField<PlaybackQueue>(f.Window, "_playbackQueue").Current, second), "search cover selected another track");
        var artistTrack = f.Row("artist-top", "1");
        Control<Grid>(f.Window, "ArtistPanel").Visibility = Visibility.Visible;
        SaInvoke(f.Window, "ShowArtistDetails", new ArtistDetails("a", "Исполнитель", null, null, [artistTrack], [], [], "")); f.Window.UpdateLayout();
        var artistCover = SaVisuals<Button>(Control<ListBox>(f.Window, "ArtistTopTracks")).Single(button => button.Name == "TrackCoverPlayButton");
        SaClick(artistCover); await Until(() => ReferenceEquals(SaField<TrackRow?>(f.Window, "_current"), artistTrack) && f.Player.IsPlaying && !SaField<bool>(f.Window, "_loadingStream"));
        Assert(SaField<PlaybackQueue>(f.Window, "_playbackQueue").Items.SequenceEqual(new[] { artistTrack }), "artist cover kept another view's queue");
    }

    private static Button Library017Cover(MainWindow window, TrackRow row) => SaVisuals<Button>(Control<ListView>(window, "TracksList"))
        .Single(button => button.Name == "TrackCoverPlayButton" && ReferenceEquals(button.DataContext, row));
    private static bool Library017Within(DependencyObject? element, DependencyObject parent)
    {
        for (; element is not null; element = VisualTreeHelper.GetParent(element)) if (ReferenceEquals(element, parent)) return true;
        return false;
    }
    private static void Library017MouseOver(IInputElement? element)
    {
        // Exercise WPF's real reverse-inherited hover state inside this isolated
        // dispatcher, without moving the user's OS cursor or injecting input.
        var change = typeof(MouseDevice).GetMethod("ChangeMouseOver", BindingFlags.Instance | BindingFlags.NonPublic,
            null, [typeof(IInputElement), typeof(int)], null) ?? throw new InvalidOperationException("WPF hover test hook unavailable");
        change.Invoke(Mouse.PrimaryDevice, [element, Environment.TickCount]);
    }

    private static Button Library017LikeButton(MainWindow window, TrackRow row)
        => SaVisuals<Button>(Control<ListView>(window, "TracksList")).Single(button => button.Name == "TrackLikeButton" && ReferenceEquals(button.Tag, row));
    private static void Library017Set(MainWindow window, string name, object? value)
        => typeof(MainWindow).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(window, value);

    private sealed class Library017Fixture : IDisposable
    {
        internal MainWindow Window { get; }
        internal NativePlayer Player { get; }
        internal OfflineLibrary Library { get; }
        internal List<(string Action, string Id)> Writes { get; } = [];
        internal List<string> Paths { get; } = [];
        internal List<string> StreamTracks { get; } = [];
        internal bool FailMutation;
        internal int ApiRequests, MediaRequests;
        private readonly HashSet<string> _likes = new(StringComparer.Ordinal) { "shared" };
        private readonly FakeHandler _handler, _media;
        private readonly string _directoryPath;
        internal Library017Fixture(string name)
        {
            _directoryPath = Path.Combine(_directory, "library017-" + name); Directory.CreateDirectory(_directoryPath);
            var bytes = File.ReadAllBytes(WriteWave("library017-" + name + ".wav", 4));
            _handler = new FakeHandler(Respond);
            _media = new FakeHandler((_, _) => { MediaRequests++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }); });
            Library = new OfflineLibrary(Path.Combine(_directoryPath, "offline"), new AudioBuffer(Path.Combine(_directoryPath, "downloads"), _media));
            Player = new NativePlayer(new AudioBuffer(Path.Combine(_directoryPath, "buffer"), _media));
            Window = new(api: new MusicApi(_handler), store: new SessionStore(Path.Combine(_directoryPath, "session")), restore: false,
                diagnosticsDirectory: _directoryPath, offline: Library, player: Player);
            Window.Show(); Control<FrameworkElement>(Window, "LoginPanel").Visibility = Visibility.Collapsed; Control<Slider>(Window, "VolumeSlider").Value = 0; Control<ComboBox>(Window, "QualityCombo").SelectedIndex = 1;
            Library017Set(Window, "_session", FakeSession); Library017Set(Window, "_account", new Account("12345", "Fixture", true));
            SaField<HashSet<string>>(Window, "_favoriteTrackIds").UnionWith(_likes); Library017Set(Window, "_favoriteIdsKnown", true);
        }
        internal TrackRow Row(string id, string album) => new() { Id = id, RequestId = id + ":" + album, AlbumId = album, Number = 1,
            Title = "Одинаковое название", Artist = "Исполнитель", Album = "Альбом", DurationMs = 4000, Available = true };
        internal Task<OfflineItem> SaveShared() => Library.SaveAsync(Row("shared", "album-a"), new AudioDescriptor(new Uri("https://s1.vlx.yandex.net/audio.mp3"), "mp3", 320), default);
        private async Task<HttpResponseMessage> Respond(HttpRequestMessage request, CancellationToken token)
        {
            ApiRequests++; var path = request.RequestUri!.AbsolutePath; Paths.Add(path);
            Assert(request.RequestUri.Host == "api.music.yandex.net", "library fixture left the API host");
            if (path == "/account/status") return Json(Profile);
            if (path == "/users/12345/likes/tracks") return Json(JsonSerializer.Serialize(new { result = new { library = new { tracks = _likes.Select(id => new { id, albumId = "album-a" }).ToArray() } } }));
            if (path.StartsWith("/users/12345/likes/tracks/", StringComparison.Ordinal))
            {
                var body = Uri.UnescapeDataString(await request.Content!.ReadAsStringAsync(token));
                var id = body.Split('&').Select(part => part.Split('=', 2)).First(part => part[0] == "track-ids")[1];
                var action = path.Split('/')[^1]; Writes.Add((action, id));
                if (FailMutation) return Json("{}", HttpStatusCode.InternalServerError);
                if (action == "remove") _likes.Remove(id.Split(':')[0]); else _likes.Add(id.Split(':')[0]);
                return Json("{\"result\":{\"revision\":1}}");
            }
            if (path == "/tracks")
            {
                var body = Uri.UnescapeDataString(await request.Content!.ReadAsStringAsync(token));
                var ids = body.Split('&')[0].Split('=')[1].Split(',').Select(value => value.Split(':')[0]);
                return Json(JsonSerializer.Serialize(new { result = ids.Where(id => id != "unavailable").Select(id => new { id, title = "Трек " + id,
                    available = true, durationMs = 4000, artists = new[] { new { id = "artist", name = "Исполнитель" } }, albums = new[] { new { id = "1", title = "Альбом" } } }).ToArray() }));
            }
            if (path == "/get-file-info")
            {
                var parameter = request.RequestUri.Query.TrimStart('?').Split('&').Select(part => part.Split('=', 2)).FirstOrDefault(part => part[0] == "trackId");
                if (parameter?.Length == 2) StreamTracks.Add(Uri.UnescapeDataString(parameter[1]));
                return Json("{\"result\":{\"downloadInfo\":{\"quality\":\"lossless\",\"codec\":\"mp3\",\"bitrate\":320,\"transport\":\"raw\",\"url\":\"https://s1.vlx.yandex.net/audio.mp3\"}}}");
            }
            throw new InvalidOperationException("Unexpected library017 route " + path);
        }
        public void Dispose() { Window.Close(); Player.Dispose(); Library.Dispose(); _handler.Dispose(); _media.Dispose(); }
    }
}
