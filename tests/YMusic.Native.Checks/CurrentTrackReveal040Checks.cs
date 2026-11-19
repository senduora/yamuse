using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
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
        await Run("CurrentTrackReveal040: manual main-list input owns scrolling between the two reveal passes", Cr040ManualMainReveal);
        await Run("CurrentTrackReveal040: pending fullscreen reveal accepts append and rejects queue replacement", Cr040FullscreenMutation);
        await Run("CurrentTrackReveal040: ordinary pending reveal uses the live occurrence after append", Cr040OrdinaryQueueAppend);
        await Run("CurrentTrackReveal040: delayed wave append preserves queue choices before and after reveal", Cr040PassiveAppendSelection);
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
        var token = f.Queue.CurrentOccurrenceToken;
        var replacement = f.Row("same-id", title: "Новый дубль");
        Pc0317Invoke(f.Window, "ShowPlaylist", new PlaylistDetails("owner", "sorted", "Снова два вхождения", "", 4, [freshFirst, replacement]));
        f.Playlist("other", f.Rows(1, "other")); f.Return(); await Cr040Idle(f.Window);
        Assert(f.Queue.CurrentOccurrenceToken == token && f.List.Items.Count == 2 && f.List.SelectedIndex == -1
            && f.Window.QueuePlayingIndex == -1 && !replacement.IsPlaying,
            "restoring the same ordered references after a removal revived the ambiguous source occurrence");

        var originalFirst = f.Row("reordered-copy", title: "Первый");
        var originalSecond = f.Row("reordered-copy", title: "Второй");
        f.Playlist("reordered", [originalFirst, originalSecond, f.Row("separator")]);
        await f.Play(originalSecond);
        token = f.Queue.CurrentOccurrenceToken;
        var freshOrder = new[] { f.Row("reordered-copy"), f.Row("separator"), f.Row("reordered-copy") };
        Pc0317Invoke(f.Window, "ShowPlaylist", new PlaylistDetails("owner", "reordered", "Другой порядок", "", 2, freshOrder));
        Assert(f.Window.QueuePlayingIndex == -1, "a changed order with the same duplicate count kept the old ordinal");
        var restoredOrder = new[] { f.Row("reordered-copy"), f.Row("reordered-copy"), f.Row("separator") };
        Pc0317Invoke(f.Window, "ShowPlaylist", new PlaylistDetails("owner", "reordered", "Исходный порядок", "", 3, restoredOrder));
        f.Playlist("other", f.Rows(1, "other")); f.Return(); await Cr040Idle(f.Window);
        Assert(f.Queue.CurrentOccurrenceToken == token && f.Window.QueuePlayingIndex == -1 && f.List.SelectedIndex == -1
            && restoredOrder.All(row => !row.IsPlaying), "restoring the original order after an equal-count reorder revived the stale occurrence");
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
        f.Queue.SelectAt(3000); await f.Window.PlayTrackAsync(rows[3000]);
        await Until(() => !Pc0317Get<bool>(f.Window, "_expandedQueueRevealPending"));
        await Cr040Idle(f.Window);
        Assert(list.SelectedIndex == 3000, "animated reopen used an old cursor"); Cr040Visible(list, 3000, "fullscreen-reopen-live-cursor");
        Assert(!SmoothScroll.IsAnimating(Cr040Viewer(list)), "explicit fullscreen reveal animated through thousands of rows");
        var panel = Pc0317Get<Grid>(f.Window, "_expandedQueuePanel");
        var current = f.Queue.CurrentOccurrenceToken;
        foreach (var input in new[] { "wheel", "list press", "scrollbar press", "navigation key" })
        {
            viewer = Cr040Viewer(list);
            viewer.ScrollToTop(); await Cr040Idle(f.Window);
            var scrollbar = EpVisuals<ScrollBar>(viewer).Single(bar => bar.Orientation == Orientation.Vertical);
            Pc0317Invoke(f.Window, "SetExpandedQueueVisible", false, false);
            // For the wheel case the reveal is already queued. The other cases can interrupt pane animation.
            Pc0317Invoke(f.Window, "SetExpandedQueueVisible", true, input != "wheel");
            Assert(Pc0317Get<bool>(f.Window, "_expandedQueueRevealPending"), "reopening did not start a current-track reveal");
            if (input == "wheel")
                list.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, 120)
                    { RoutedEvent = Mouse.PreviewMouseWheelEvent });
            else if (input == "navigation key")
                list.RaiseEvent(new KeyEventArgs(new Pk040Keyboard(ModifierKeys.None), new Pk040Source(list), Environment.TickCount, Key.Home)
                    { RoutedEvent = Keyboard.PreviewKeyDownEvent });
            else
                (input == "scrollbar press" ? (UIElement)scrollbar : list).RaiseEvent(
                    new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
                    { RoutedEvent = Mouse.PreviewMouseDownEvent });
            Assert(!Pc0317Get<bool>(f.Window, "_expandedQueueRevealPending"), "manual input left the opening reveal in control: " + input);
            viewer.ScrollToTop();
            await Until(() => panel.Width == (double)panel.GetAnimationBaseValue(FrameworkElement.WidthProperty));
            await Cr040Idle(f.Window);
            Assert(f.Queue.CurrentOccurrenceToken == current && viewer.VerticalOffset < 1,
                "the opening reveal moved the viewport after manual input: " + input);
        }
        Pc0317Invoke(f.Window, "SetExpandedQueueVisible", false, false);
        Assert(list.ItemsSource is null, "closing fullscreen queue retained its source");
        Pc0317Invoke(f.Window, "SetExpandedQueueVisible", true, false); f.Window.SignOut(); await Cr040Idle(f.Window);
        Assert(list.ItemsSource is null && f.Queue.Items.Count == 0 && Pc0317Get<object?>(f.Window, "_expandedFullscreenHost") is null,
            "late fullscreen reveal restored signed-out queue bindings or its closed host");
    }

    private static async Task Cr040ManualMainReveal()
    {
        using var f = new Pc0317Fixture("reveal040-manual-between-passes"); var rows = f.Rows(1200);
        f.Playlist("origin", rows); await f.Play(rows[999]); Cr040Show(f.Window);
        var current = f.Queue.CurrentOccurrenceToken;
        foreach (var source in new[] { true, false })
        {
            f.Playlist("other", f.Rows(1, "other"));
            if (source) f.Return();
            else Pc0317Invoke(f.Window, "ShowQueue");
            await Dispatcher.CurrentDispatcher.InvokeAsync(() =>
            {
                var viewer = Cr040Viewer(f.List);
                Assert(viewer.VerticalOffset > 100 && f.List.SelectedIndex == 999, "the first reveal pass did not run before simulated input");
                viewer.ScrollToTop(); f.Window.UpdateLayout();
                f.List.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, 120)
                    { RoutedEvent = Mouse.PreviewMouseWheelEvent });
                f.List.SelectedIndex = 0;
            }, DispatcherPriority.Input);
            await Cr040Idle(f.Window);
            Assert(f.List.SelectedIndex == 0 && Cr040Viewer(f.List).VerticalOffset < 1 && f.Queue.CurrentOccurrenceToken == current,
                "the second reveal pass displaced the manual viewport or choice");
        }
    }

    private static async Task Cr040FullscreenMutation()
    {
        using var f = new Pc0317Fixture("reveal040-fullscreen-mutation"); var rows = f.Rows(160);
        f.Playlist("origin", rows); await f.Play(rows[120]); Cr040Show(f.Window);
        var current = f.Queue.CurrentOccurrenceToken;
        Pc0317Invoke(f.Window, "SetExpandedPlayerVisible", true, false);
        Pc0317Invoke(f.Window, "SetExpandedQueueVisible", true, false);
        var list = Pc0317Get<ListBox>(f.Window, "_expandedQueueList");
        var priorItems = f.Queue.Items;
        f.Queue.Append([f.Row("added-during-reveal")]);
        Assert(!ReferenceEquals(priorItems, f.Queue.Items) && f.Queue.CurrentOccurrenceToken == current, "append did not change only the queue snapshot");
        await Until(() => !Pc0317Get<bool>(f.Window, "_expandedQueueRevealPending"));
        await Cr040Idle(f.Window);
        Assert(list.Items.Count == 161 && list.SelectedIndex == 120, "same-queue append lost the live cursor or bound an outdated list");
        Cr040Visible(list, 120, "fullscreen-appended-before-reveal");

        var viewer = Cr040Viewer(list);
        viewer.ScrollToTop(); await Cr040Idle(f.Window);
        Pc0317Invoke(f.Window, "SetExpandedQueueVisible", false, false);
        Pc0317Invoke(f.Window, "SetExpandedQueueVisible", true, false);
        list.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, 120) { RoutedEvent = Mouse.PreviewMouseWheelEvent });
        Pc0317Invoke(f.Window, "LayoutExpandedPanes", false);
        await Cr040Idle(f.Window);
        Assert(viewer.VerticalOffset < 1 && !Pc0317Get<bool>(f.Window, "_expandedQueueRevealPending"), "re-layout reinstated manual reveal");

        Pc0317Invoke(f.Window, "SetExpandedQueueVisible", false, false);
        Pc0317Invoke(f.Window, "SetExpandedQueueVisible", true, false);
        f.Queue.SetItems(rows, rows[120]);
        Pc0317Invoke(f.Window, "RefreshExpandedQueue");
        await Cr040Idle(f.Window);
        Assert(viewer.VerticalOffset < 1 && !Pc0317Get<bool>(f.Window, "_expandedQueueRevealPending"), "an old opening scrolled a replacement queue");
    }

    private static async Task Cr040OrdinaryQueueAppend()
    {
        using var f = new Pc0317Fixture("reveal040-ordinary-queue-append"); var rows = f.Rows(160);
        rows[8] = rows[120];
        f.Playlist("origin", rows);
        f.List.SelectedIndex = 120;
        await Pc0317Call(f.Window, "PlaySelectedAsync"); Cr040Show(f.Window);
        var current = f.Queue.CurrentOccurrenceToken;
        Assert(current is not null && f.Queue.CurrentIndex == 120, "the fixture did not start the second shared occurrence");
        foreach (var betweenPasses in new[] { false, true })
        {
            var items = f.Queue.Items;
            var count = items.Count;
            Pc0317Invoke(f.Window, "ShowQueue");
            if (betweenPasses)
            {
                await Dispatcher.CurrentDispatcher.InvokeAsync(() =>
                {
                    var viewer = Cr040Viewer(f.List);
                    Assert(viewer.VerticalOffset > 100, "the first reveal pass did not run before append");
                    Append();
                    viewer.ScrollToTop(); f.Window.UpdateLayout();
                }, DispatcherPriority.Input);
            }
            else
            {
                Cr040Viewer(f.List).ScrollToTop(); f.Window.UpdateLayout();
                Append();
            }
            await Cr040Idle(f.Window);
            Assert(f.List.Items.Count == count + 1 && f.List.SelectedIndex == 120 && f.Queue.CurrentOccurrenceToken == current,
                "a same-queue append lost the live duplicate or published stale rows");
            Cr040Visible(f.List, 120, "ordinary-queue-append-" + betweenPasses);

            void Append()
            {
                f.Queue.Append([f.Row($"appended-{betweenPasses}")]);
                Assert(!ReferenceEquals(f.Queue.Items, items), "append did not publish a replacement Items array");
                Pc0317Invoke(f.Window, "RefreshPlaybackView");
            }
        }
        Pc0317Invoke(f.Window, "ShowQueue");
        f.Queue.SetItemsAt(rows, 120);
        Pc0317Invoke(f.Window, "RefreshPlaybackView");
        Cr040Viewer(f.List).ScrollToTop(); f.Window.UpdateLayout(); await Cr040Idle(f.Window);
        Assert(f.Queue.CurrentOccurrenceToken != current && Cr040Viewer(f.List).VerticalOffset < 1,
            "an old ordinary reveal moved the viewport after queue replacement");
    }

    private static async Task Cr040PassiveAppendSelection()
    {
        foreach (var fullscreen in new[] { false, true })
        foreach (var beforeIdle in new[] { false, true })
        {
            using var f = new TwFixture($"reveal040-selection-{fullscreen}-{beforeIdle}");
            var w = f.Window;
            await Call(w, "OpenTrackWaveAsync", TwSeed("1234", "Волна", true)); await f.Ready("1234");
            Cr040Show(w);
            var queue = Pc0317Get<PlaybackQueue>(w, "_playbackQueue");
            var source = queue.Current!;
            var next = queue.Items[1];
            var previous = Enumerable.Range(1, 22).Select(i => new TrackRow
                { Id = $"prior-{i}", RequestId = $"prior-{i}:1", Title = $"Прослушанный трек {i}", Available = true }).ToArray();
            queue.SetItems([.. previous, source, next]);
            for (var i = 0; i < previous.Length; i++) queue.Next();
            var cursor = queue.CurrentOccurrenceToken;
            var expired = queue.OccurrenceTokenAt(0);
            var selected = queue.OccurrenceTokenAt(5);
            var second = queue.OccurrenceTokenAt(6);
            Assert(ReferenceEquals(queue.Current, source), "fixture dropped the playing wave source before the refill");
            f.HoldMore = true;
            var refill = Call(w, "ExtendWaveQueueAsync");
            try
            {
                await f.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                ListBox list;
                if (fullscreen)
                {
                    Pc0317Invoke(w, "SetExpandedPlayerVisible", true, false);
                    Pc0317Invoke(w, "SetExpandedQueueVisible", true, false);
                    list = Pc0317Get<ListBox>(w, "_expandedQueueList");
                }
                else
                {
                    Pc0317Invoke(w, "ShowQueue");
                    list = Control<ListView>(w, "TracksList");
                }
                if (!beforeIdle) await Cr040Idle(w);
                await Dispatcher.CurrentDispatcher.InvokeAsync(() =>
                {
                    var presentation = PresentationSource.FromVisual(list) ?? PresentationSource.FromVisual(w)!;
                    list.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, presentation, Environment.TickCount, Key.Down)
                        { RoutedEvent = Keyboard.PreviewKeyDownEvent });
                    list.SelectedIndex = 5;
                    if (fullscreen) return;
                    list.SelectedItems.Add(previous[0]);
                    list.SelectedItems.Add(previous[6]);
                }, DispatcherPriority.Input);

                f.Release.TrySetResult();
                await refill.WaitAsync(TimeSpan.FromSeconds(5)); await Cr040Idle(w);
                Assert(queue.CurrentOccurrenceToken == cursor && queue.Items.Any(row => row.Id == "901")
                    && Enumerable.Range(0, queue.Items.Count).All(i => queue.OccurrenceTokenAt(i) != expired),
                    "held refill did not publish an append with a shifted queue cursor");
                if (fullscreen)
                    Assert(list.SelectedIndex == 3 && queue.OccurrenceTokenAt(list.SelectedIndex) == selected,
                        "fullscreen append moved the manual choice to a different occurrence");
                else
                {
                    Assert(list.SelectedItems.OfType<TrackRow>().ToHashSet().SetEquals([previous[5], previous[6]]),
                        "ordinary append kept an evicted selection or replaced the surviving selected occurrences");
                    await Call(w, "RemoveSelectedQueueAsync");
                    Assert(queue.CurrentOccurrenceToken == cursor && Enumerable.Range(0, queue.Items.Count)
                        .Select(queue.OccurrenceTokenAt).All(token => token != selected && token != second),
                        "ordinary removal did not use the preserved choice");
                }

                list.UnselectAll();
                queue.Append([new TrackRow { Id = "another", RequestId = "another:1", Title = "Ещё один трек", Available = true }]);
                if (fullscreen) Pc0317Invoke(w, "RefreshExpandedQueue");
                else Pc0317Invoke(w, "RefreshPlaybackView");
                Assert(list.SelectedItems.Count == 0 && queue.CurrentOccurrenceToken == cursor, "same-cursor redraw filled an explicit empty selection");
            }
            finally { f.Release.TrySetResult(); }
        }
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
