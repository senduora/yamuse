using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using YMusic.Native;

internal static partial class Program
{
    // Native fixtures in this group must run through Invoke-PrivateDesktopChecks.
    private static async Task CheckCurrentTrackReveal040()
    {
        await Run("CurrentTrackReveal040: favorites return reveals paused row 1000 with bounded virtualization and unchanged playback", Cr040Favorites);
        await Run("CurrentTrackReveal040: source duplicates retain exact occurrence through sorting refresh and removal", Cr040SourceOccurrences);
        await Run("CurrentTrackReveal040: deferred source return follows live cursor and rejects newer navigation or late page IO", Cr040SourceRaces);
        await Run("CurrentTrackReveal040: ordinary queue reveals the current duplicate and preserves sorting filter and manual browsing", Cr040Queue);
        await Run("CurrentTrackReveal040: fullscreen queue opens around the live paused cursor and refresh never follows manual browsing", Cr040FullscreenQueue);
        await Run("CurrentTrackReveal040: empty removed replaced account closed and rapid requests reject stale reveal", Cr040Lifetime);
    }

    private static async Task Cr040Favorites()
    {
        using var f = new Pc0317Fixture("reveal040-favorites"); var rows = f.Rows(3900);
        Pc0317Invoke(f.Window, "SetSection", "favorites", "Мне нравится"); Pc0317Invoke(f.Window, "ShowRows", (object)rows);
        Pc0317Get<List<TrackRow>>(f.Window, "_favoritesCache").AddRange(rows);
        Pc0317Get<HashSet<string>>(f.Window, "_favoriteTrackIds").UnionWith(rows.Select(row => row.Id)); Pc0317Set(f.Window, "_favoriteIdsKnown", true);
        await f.Play(rows[999]); await Pc0317Call(f.Window, "TogglePlaybackAsync"); Cr040Show(f.Window);
        var items = f.Queue.Items; var token = f.Queue.CurrentOccurrenceToken; var version = f.Queue.Version; var source = f.Player.SourceLocalPath;
        f.Playlist("other", f.Rows(2, "other")); var clock = Stopwatch.StartNew(); f.Return(); await Cr040Idle(f.Window);
        Assert(f.List.SelectedIndex == 999 && !f.Player.IsPlaying && ReferenceEquals(items, f.Queue.Items)
            && token == f.Queue.CurrentOccurrenceToken && version == f.Queue.Version && source == f.Player.SourceLocalPath,
            "return lost the paused current row or changed playback");
        Cr040Visible(f.List, 999, "favorites-1000", clock.Elapsed.TotalMilliseconds);
        f.Filter("Трек"); f.Return(); await Cr040Idle(f.Window);
        Assert(f.FilterText.Text == "Трек" && f.List.SelectedIndex == 999, "explicit return cleared a filter that already includes current");
        f.Return(); f.Filter("Трек 1"); await Cr040Idle(f.Window);
        Assert(f.FilterText.Text == "Трек 1" && f.List.Items.Count > 0 && !f.List.Items.Cast<TrackRow>().Any(row => ReferenceEquals(row, rows[999])),
            "deferred source reveal cleared a filter entered after the return click");
        f.Filter(""); f.Return(); await Cr040Idle(f.Window);
        var viewer = Cr040Viewer(f.List); viewer.ScrollToTop(); f.Window.UpdateLayout(); await Cr040Idle(f.Window); var manual = viewer.VerticalOffset;
        for (var i = 0; i < 100; i++) Pc0317Invoke(f.Window, "UpdateProgress");
        Assert(Math.Abs(viewer.VerticalOffset - manual) < .1, "progress snapped the source back to current");
        Assert(f.Handler.Requests == 0, "source return fetched a service page");
    }

    private static async Task Cr040SourceOccurrences()
    {
        using var f = new Pc0317Fixture("reveal040-source-duplicates"); var rows = f.Rows(3900);
        var repeated = f.Row("same-reference", title: "Трек повтор"); rows[10] = repeated; rows[999] = repeated; rows[2500] = repeated;
        f.Playlist("origin", rows); await f.Play(rows[0]); f.Queue.SelectAt(999); await f.Window.PlayTrackAsync(repeated); Cr040Show(f.Window);
        f.Playlist("other", f.Rows(1, "other")); f.Return(); await Cr040Idle(f.Window);
        Assert(f.List.SelectedIndex == 999, "source selected the first shared TrackRow reference"); Cr040Visible(f.List, 999, "source-duplicate-1000");
        // Different instances of the same RequestId can have different sort keys.
        // A refreshed source must retain the source ordinal, not the sorted queue ordinal.
        var first = f.Row("same-id", title: "Я" ); var second = f.Row("same-id", title: "А");
        f.Playlist("sorted", [first, second]); Pc0317Invoke(f.Window, "ChangeListSort", new ListSortRule(ListSortField.Title)); await f.Play(second);
        var freshFirst = f.Row("same-id", title: "Я"); var freshSecond = f.Row("same-id", title: "А");
        Pc0317Invoke(f.Window, "ShowPlaylist", new PlaylistDetails("owner", "sorted", "Обновлённый источник", "", 2, [freshFirst, freshSecond]));
        f.Playlist("other", f.Rows(1, "other")); f.Return(); await Cr040Idle(f.Window);
        Assert(ReferenceEquals(f.List.SelectedItem, freshSecond), "fresh sorted duplicate resolved by queue ordinal instead of source occurrence");
        Pc0317Invoke(f.Window, "ShowPlaylist", new PlaylistDetails("owner", "sorted", "Удалённый current", "", 3, [freshFirst]));
        f.Playlist("other", f.Rows(1, "other")); f.Return(); await Cr040Idle(f.Window);
        Assert(f.List.Items.Count == 1 && f.List.SelectedIndex == -1, "removed ambiguous duplicate selected another occurrence");
    }

    private static async Task Cr040SourceRaces()
    {
        using var f = new Pc0317Fixture("reveal040-source-races"); var rows = f.Rows(3900);
        f.Playlist("origin", rows); await f.Play(rows[999]); Cr040Show(f.Window); f.Playlist("other", f.Rows(1, "other"));
        f.Return(); f.Queue.SelectAt(2500); await f.Window.PlayTrackAsync(rows[2500]); await Cr040Idle(f.Window);
        Assert(f.List.SelectedIndex == 2500, "deferred return revealed the click-time selection after current changed");
        Cr040Visible(f.List, 2500, "source-live-cursor");
        f.Return(); var other = f.Rows(2, "newer-navigation"); f.Playlist("newer", other); await Cr040Idle(f.Window);
        Assert(Pc0317Get<PlaylistDetails>(f.Window, "_activePlaylist").Kind == "newer"
            && f.List.Items.Cast<TrackRow>().SequenceEqual(other) && f.List.SelectedIndex == -1, "older return changed a newer page selection");
        f.Handler.Block = true;
        var pending = Pc0317Call(f.Window, "OpenCatalogAsync", new CatalogItem("pending", "playlist", "Late page", "", Owner: "owner", PlaylistKind: "pending"), true);
        await f.Handler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); f.Return();
        f.Handler.Release.TrySetResult(); await pending.WaitAsync(TimeSpan.FromSeconds(5)); await Cr040Idle(f.Window);
        Assert(Pc0317Get<PlaylistDetails>(f.Window, "_activePlaylist").Kind == "origin" && f.List.SelectedIndex == 2500, "late page completion replaced source reveal");
        Cr040Visible(f.List, 2500, "source-late-page");
    }

    private static async Task Cr040Queue()
    {
        using var f = new Pc0317Fixture("reveal040-queue"); var rows = f.Rows(3900);
        var repeated = f.Row("queue-repeat", title: "Повтор"); rows[10] = repeated; rows[999] = repeated; rows[2500] = repeated;
        f.Playlist("origin", rows); await f.Play(rows[0]); f.Queue.SelectAt(999); await f.Window.PlayTrackAsync(repeated); Cr040Show(f.Window);
        Pc0317Invoke(f.Window, "ShowQueue"); await Cr040Idle(f.Window);
        Assert(f.List.SelectedIndex == 999 && (int)Pc0317Invoke(f.Window, "SelectedQueueIndex")! == 999, "ordinary queue confused a shared-reference occurrence");
        Cr040Visible(f.List, 999, "queue-duplicate-1000");
        Pc0317Invoke(f.Window, "ChangeListSort", new ListSortRule(ListSortField.Title, true));
        var queueItems = f.Queue.Items; var token = f.Queue.CurrentOccurrenceToken;
        Pc0317Invoke(f.Window, "ShowQueue"); f.Queue.SelectAt(2500); await f.Window.PlayTrackAsync(repeated); await Cr040Idle(f.Window);
        Assert((int)Pc0317Invoke(f.Window, "SelectedQueueIndex")! == 2500 && ReferenceEquals(queueItems, f.Queue.Items)
            && token != f.Queue.CurrentOccurrenceToken, "sorted reveal changed queue order or used an old cursor");
        Cr040Visible(f.List, f.List.SelectedIndex, "queue-sorted-live-cursor");
        var viewer = Cr040Viewer(f.List); viewer.ScrollToTop(); f.Window.UpdateLayout(); await Cr040Idle(f.Window); var manual = viewer.VerticalOffset;
        for (var i = 0; i < 100; i++) Pc0317Invoke(f.Window, "UpdateProgress");
        Pc0317Invoke(f.Window, "RefreshPlaybackView"); await Cr040Idle(f.Window);
        Assert(Math.Abs(viewer.VerticalOffset - manual) < .1, "ordinary queue refresh followed current over manual browsing");
        Pc0317Invoke(f.Window, "ShowQueue"); f.Filter("Трек 1"); await Cr040Idle(f.Window);
        Assert(f.FilterText.Text == "Трек 1" && f.List.Items.Count > 0 && f.List.SelectedIndex == -1, "deferred queue reveal cleared a newer filter or chose a different row");
    }

    private static async Task Cr040FullscreenQueue()
    {
        using var f = new Pc0317Fixture("reveal040-fullscreen"); var rows = f.Rows(3900);
        var repeated = f.Row("fullscreen-repeat"); rows[10] = repeated; rows[999] = repeated; rows[2500] = repeated;
        f.Playlist("origin", rows); await f.Play(rows[0]); f.Queue.SelectAt(2500); await f.Window.PlayTrackAsync(repeated);
        await Pc0317Call(f.Window, "TogglePlaybackAsync"); Cr040Show(f.Window);
        Pc0317Invoke(f.Window, "SetExpandedPlayerVisible", true, true);
        Pc0317Invoke(f.Window, "SetExpandedQueueVisible", true, false); await Cr040Idle(f.Window);
        var list = Pc0317Get<ListBox>(f.Window, "_expandedQueueList");
        Assert(list.SelectedIndex == 2500 && !f.Player.IsPlaying && Fh040Window(f.Window).IsVisible, "fullscreen queue did not open at paused occurrence");
        Cr040Visible(list, 2500, "fullscreen-queue-2501");
        var viewer = Cr040Viewer(list); viewer.ScrollToTop(); Fh040Window(f.Window).UpdateLayout(); await Cr040Idle(f.Window); var manual = viewer.VerticalOffset;
        for (var i = 0; i < 100; i++) { Pc0317Invoke(f.Window, "UpdateProgress"); Pc0317Invoke(f.Window, "RefreshExpandedQueue"); }
        f.Queue.SelectAt(999); Pc0317Invoke(f.Window, "RefreshExpandedQueue"); await Cr040Idle(f.Window);
        Assert(Math.Abs(viewer.VerticalOffset - manual) < .1 && list.SelectedIndex == 999, "fullscreen refresh scrolled the manually viewed queue");
        Pc0317Invoke(f.Window, "SetExpandedQueueVisible", false, false);
        Pc0317Invoke(f.Window, "SetExpandedQueueVisible", true, true);
        f.Queue.SelectAt(3000); await f.Window.PlayTrackAsync(rows[3000]); await Cr040Idle(f.Window);
        await Task.Delay(380); await Cr040Idle(f.Window);
        Assert(list.SelectedIndex == 3000, "animated reopen used an old cursor"); Cr040Visible(list, 3000, "fullscreen-reopen-live-cursor");
        Assert(!SmoothScroll.IsAnimating(Cr040Viewer(list)), "explicit fullscreen reveal animated through thousands of rows");
        Pc0317Invoke(f.Window, "SetExpandedQueueVisible", false, false);
        Assert(list.ItemsSource is null, "closing fullscreen queue retained its source");
        Pc0317Invoke(f.Window, "SetExpandedQueueVisible", true, false); f.Window.SignOut(); await Cr040Idle(f.Window);
        Assert(list.ItemsSource is null && f.Queue.Items.Count == 0 && Pc0317Get<object?>(f.Window, "_expandedFullscreenHost") is null,
            "late fullscreen reveal restored signed-out queue bindings or its closed host");
    }

    private static async Task Cr040Lifetime()
    {
        foreach (var mode in new[] { "account", "closed", "replaced", "removed", "empty", "rapid" })
        {
            using var f = new Pc0317Fixture("reveal040-lifetime-" + mode); var rows = f.Rows(1200);
            f.Playlist("origin", rows); await f.Play(rows[999]); Cr040Show(f.Window);
            Pc0317Invoke(f.Window, "ShowQueue");
            f.Window.UpdateLayout(); var viewer = Cr040Viewer(f.List); var beforeOffset = viewer.VerticalOffset;
            if (mode == "account") Pc0317Set(f.Window, "_accountGeneration", Pc0317Get<int>(f.Window, "_accountGeneration") + 1);
            else if (mode == "closed") f.Window.Close();
            else if (mode == "replaced") { f.Queue.SetItems([rows[2]], rows[2]); Pc0317Invoke(f.Window, "RefreshPlaybackView"); }
            else if (mode == "removed") { f.Queue.RemoveAt(999); Pc0317Invoke(f.Window, "RefreshPlaybackView"); }
            else if (mode == "empty") { f.Queue.SetItems([]); Pc0317Invoke(f.Window, "RefreshPlaybackView"); }
            else { f.Return(); Pc0317Invoke(f.Window, "ShowQueue"); f.Queue.SelectAt(1100); await f.Window.PlayTrackAsync(rows[1100]); }
            await Cr040Idle(f.Window);
            if (mode == "rapid") { Assert(f.List.SelectedIndex == 1100, "rapid return/open requests applied an old reveal"); Cr040Visible(f.List, 1100, "queue-rapid-open"); }
            else if (mode == "empty") Assert(f.List.Items.Count == 0 && f.List.SelectedIndex == -1, "empty queue retained a reveal target");
            else if (mode == "replaced") Assert(f.List.Items.Count == 1 && ReferenceEquals(f.List.SelectedItem, rows[2]), "old reveal altered replaced queue selection");
            else if (mode == "removed") Assert(f.Queue.CurrentIndex == 999 && ReferenceEquals(f.Queue.Current, rows[1000])
                && ReferenceEquals(f.List.SelectedItem, rows[1000]), "removed current produced a stale selection");
            else if (mode == "account") Assert(Math.Abs(viewer.VerticalOffset - beforeOffset) < .1, "old account request still scrolled the queue");
            Cr040Evidence("lifetime-" + mode, new { noLateFailure = true, currentIndex = f.Queue.CurrentIndex, visibleCount = f.List.Items.Count });
        }
        using var unknown = new Pc0317Fixture("reveal040-no-source"); await unknown.Window.PlayTrackAsync(unknown.Row("direct"));
        var before = Pc0317Get<int>(unknown.Window, "_viewGeneration"); unknown.Return(); await Cr040Idle(unknown.Window);
        Assert(Pc0317Get<int>(unknown.Window, "_viewGeneration") == before && unknown.Button.Visibility == Visibility.Collapsed, "unknown source invented navigation");
    }

    private static void Cr040Show(MainWindow window)
    {
        window.ShowActivated = false; window.ShowInTaskbar = false; window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = -18000; window.Top = -4000; window.Width = 1280; window.Height = 860;
        Pc0317Set(window, "_fullscreenBoundsProvider", new Func<nint, FullscreenPixelBounds>(_ => new(-18000, -4000, -16720, -3140)));
        window.Show(); window.Left = -18000; window.Top = -4000; window.UpdateLayout();
    }

    private static async Task Cr040Idle(MainWindow window)
    {
        await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
        window.UpdateLayout();
        if (Pc0317Get<object?>(window, "_expandedFullscreenHost") is not null) Fh040Window(window).UpdateLayout();
        await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
    }

    private static ScrollViewer Cr040Viewer(DependencyObject root) => EpVisuals<ScrollViewer>(root).First();

    private static void Cr040Visible(ListBox list, int index, string name, double? elapsedMs = null)
    {
        list.UpdateLayout(); var viewer = Cr040Viewer(list);
        Assert(list.ItemContainerGenerator.ContainerFromIndex(index) is FrameworkElement, "current row has no realized container: " + name);
        var row = (FrameworkElement)list.ItemContainerGenerator.ContainerFromIndex(index)!;
        var viewport = (ScrollContentPresenter)viewer.Template.FindName("PART_ScrollContentPresenter", viewer)!;
        var bounds = row.TransformToAncestor(viewport).TransformBounds(new Rect(row.RenderSize));
        var realized = EpVisuals<ListBoxItem>(list).Count();
        Assert(row.IsVisible && bounds.Height > 0 && bounds.Top >= -1 && bounds.Bottom <= viewport.ActualHeight + 1,
            $"current container is outside the actual viewport: {name}; row={bounds}; viewport={viewport.RenderSize}; offset={viewer.VerticalOffset}");
        Assert(VirtualizingPanel.GetIsVirtualizing(list) && VirtualizingPanel.GetVirtualizationMode(list) == VirtualizationMode.Recycling
            && ScrollViewer.GetCanContentScroll(list) && realized is > 1 and < 100, "explicit reveal disabled bounded virtualization: " + name);
        Assert(!SmoothScroll.IsAnimating(viewer), "explicit reveal left a long scrolling animation: " + name);
        Cr040Evidence(name, new { itemCount = list.Items.Count, index, realized, bounds = bounds.ToString(), viewport = viewport.RenderSize.ToString(),
            offset = viewer.VerticalOffset, elapsedMs, smoothScrollAnimating = false });
    }

    private static void Cr040Evidence(string name, object data) => File.WriteAllText(Path.Combine(_directory, "current-track-reveal040-" + name + ".json"),
        JsonSerializer.Serialize(new { data, productSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(MainWindow).Assembly.Location))),
            nativeWindowsPrivateDesktopOnly = true, activated = false, inputDesktopUsed = false, realAccountUsed = false, accountProfileRead = false },
            new JsonSerializerOptions { WriteIndented = true }));
}
