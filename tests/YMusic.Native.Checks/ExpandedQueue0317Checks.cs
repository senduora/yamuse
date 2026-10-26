using System.Collections;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using YMusic.Native;

internal static partial class Program
{
    private static async Task CheckExpandedQueue0317()
    {
        var original = typeof(Artwork).GetField("_cache", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null);
        using var artwork = new ArtworkCache(Path.Combine(_directory, "expanded-queue-artwork"), new FakeHandler((_, _) =>
            Task.FromResult(new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new System.Net.Http.ByteArrayContent(EpArtwork()) })));
        Artwork.Cache = artwork;
        try
        {
            await Run("ExpandedQueue0317: fullscreen shuffle repeat and ordinary queue forward existing commands", Eq0317Modes);
            await Run("ExpandedQueue0317: four independent responsive pane states retain accessible controls", Eq0317Layout);
            await Run("ExpandedQueue0317: 3904 rows recycle and refresh only on queue changes", Eq0317LargeQueue);
            await Run("ExpandedQueue0317: exact duplicate occurrence keyboard selection rejects stale account and queue snapshots", Eq0317Selection);
            await Run("ExpandedQueue0317: fullscreen shared-cover reveal is bounded and cancels on rapid close account and lifetime", Eq0317Reveal);
            File.WriteAllText(Path.Combine(_directory, "expanded-queue-0317-scope.json"), JsonSerializer.Serialize(new
            {
                realAccountLoaded = false, realClientTouched = false, externalNetworkRequests = 0, userAudioDirectoryUsed = false,
                windowsOffscreen = true, windowActivation = false, queueOccurrences = 3904, nativeWavMuted = true,
                assemblySha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(MainWindow).Assembly.Location)))
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        finally { typeof(Artwork).GetField("_cache", BindingFlags.NonPublic | BindingFlags.Static)!.SetValue(null, original); }
    }

    private static async Task Eq0317Modes()
    {
        using var f = new ExpandedFixture("queue-modes"); await f.Play(); EpOpen(f.Window);
        var w = f.Window; var items = f.Queue.Items; var occurrence = f.Queue.CurrentOccurrenceToken;
        var shuffle = EpElement<Button>(w, "ExpandedPlayerShuffle"); var repeat = EpElement<Button>(w, "ExpandedPlayerRepeat");
        EpClick(shuffle); await Dispatcher.Yield(DispatcherPriority.DataBind);
        Assert(f.Queue.Shuffle && ReferenceEquals(shuffle.Foreground, Control<Button>(w, "ShuffleButton").Foreground), "expanded shuffle does not follow the existing state/accent");
        EpClick(shuffle); Assert(!f.Queue.Shuffle, "second expanded shuffle click did not disable the shared mode");
        foreach (var expected in new[] { RepeatMode.All, RepeatMode.One, RepeatMode.Off })
        {
            EpClick(repeat); await Dispatcher.Yield(DispatcherPriority.DataBind);
            Assert(f.Queue.Repeat == expected && AutomationProperties.GetName(repeat) == AutomationProperties.GetName(Control<Button>(w, "RepeatButton"))
                && ReferenceEquals(repeat.Foreground, Control<Button>(w, "RepeatButton").Foreground), "repeat cycle or accessible state diverged");
        }
        Assert(ReferenceEquals(items, f.Queue.Items) && occurrence == f.Queue.CurrentOccurrenceToken && f.MediaCalls == 0, "mode buttons replaced the source queue/current occurrence");
        EpInvoke(w, "CloseExpandedPlayer"); EpClick(Control<Button>(w, "PlayerQueueButton"));
        Assert(EpField<string>(w, "_section") == "queue" && Control<ListView>(w, "TracksList").Items.Count == 2, "ordinary queue button did not open the existing queue view");
    }

    private static async Task Eq0317Layout()
    {
        using var f = new ExpandedFixture("queue-layout"); await f.Play(); var w = f.Window; EpOpen(w);
        await Until(() => EpField<Image>(w, "_expandedPlayerCover").Source is not null);
        var measurements = new List<object>();
        foreach (var (width, height) in new[] { (1050, 680), (1280, 860), (1920, 1080) })
        {
            w.Width = width; w.Height = height; w.UpdateLayout();
            foreach (var (lyrics, queue, state) in new[] { (false, false, "cover"), (true, false, "lyrics"), (true, true, "lyrics-queue"), (false, true, "queue") })
            {
                EpInvoke(w, "SetExpandedLyricsVisible", lyrics, false); EpInvoke(w, "SetExpandedQueueVisible", queue, false);
                w.UpdateLayout(); await Task.Delay(60); w.UpdateLayout();
                var host = Control<ContentControl>(w, "ExpandedPlayerHost");
                Assert(EpField<bool>(w, "_expandedLyricsOpen") == lyrics && EpField<bool>(w, "_expandedQueueOpen") == queue, "opening/closing one pane reset the other");
                var rectangles = new Dictionary<string, Rect>();
                foreach (var name in new[] { "ExpandedPlayerCover", "ExpandedPlayerPlayPause", "ExpandedPlayerPrevious", "ExpandedPlayerNext", "ExpandedPlayerShuffle", "ExpandedPlayerRepeat", "ExpandedPlayerLike", "ExpandedPlayerLyrics", "ExpandedPlayerQueue", "ExpandedPlayerMore", "ExpandedPlayerVolume", "ExpandedPlayerSeek" })
                {
                    var element = EpVisuals<FrameworkElement>(host).Single(e => e.Name == name);
                    var bounds = element.TransformToAncestor(host).TransformBounds(new Rect(element.RenderSize)); rectangles[name] = bounds;
                    Assert(element.IsVisible && bounds.Width > 10 && bounds.Height > 10 && bounds.Left >= 0 && bounds.Top >= 0 && bounds.Right <= host.ActualWidth + 1 && bounds.Bottom <= host.ActualHeight + 1,
                        $"{name} is clipped at {width}x{height} {state}: {bounds}");
                    Assert(!string.IsNullOrWhiteSpace(AutomationProperties.GetName(element)), name + " has no accessible name");
                }
                var lyricsPanel = EpField<Grid>(w, "_expandedLyricsPanel"); var queuePanel = EpField<Grid>(w, "_expandedQueuePanel");
                Assert(lyricsPanel.IsVisible == lyrics && queuePanel.IsVisible == queue, "pane visibility differs from its independent state");
                if (lyrics) Assert(lyricsPanel.ActualWidth > 280 && lyricsPanel.ActualHeight > 300, "lyrics pane lost usable dimensions");
                if (queue)
                {
                    Assert(queuePanel.ActualWidth > 240 && queuePanel.ActualHeight > 300, "queue pane lost usable dimensions");
                    var queueBounds = queuePanel.TransformToAncestor(host).TransformBounds(new Rect(queuePanel.RenderSize));
                    Assert(rectangles.Values.All(rect => queueBounds.Right <= rect.Left + 1), "left queue overlaps player controls");
                }
                if (lyrics && queue)
                {
                    var l = lyricsPanel.TransformToAncestor(host).TransformBounds(new Rect(lyricsPanel.RenderSize));
                    var q = queuePanel.TransformToAncestor(host).TransformBounds(new Rect(queuePanel.RenderSize));
                    Assert(rectangles.Values.All(rect => q.Right <= rect.Left + 1 && rect.Right <= l.Left + 1), "queue-player-lyrics order overlaps");
                }
                App.Capture(w, Path.Combine(_directory, $"expanded-queue-0317-{width}x{height}-{state}.png"));
                measurements.Add(new { width, height, state, cover = rectangles["ExpandedPlayerCover"].ToString(), lyricsWidth = lyricsPanel.ActualWidth, queueWidth = queuePanel.ActualWidth });
            }
        }
        File.WriteAllText(Path.Combine(_directory, "expanded-queue-0317-layout.json"), JsonSerializer.Serialize(measurements, new JsonSerializerOptions { WriteIndented = true }));
        EpInvoke(w, "SetExpandedLyricsVisible", true, false); await Until(() => EpField<ListBox>(w, "_expandedLyricsList").Items.Count == 50);
        var lines = EpField<ListBox>(w, "_expandedLyricsList").ItemsSource;
        EpInvoke(w, "SetExpandedQueueVisible", false, false);
        Assert(EpField<bool>(w, "_expandedLyricsOpen") && ReferenceEquals(lines, EpField<ListBox>(w, "_expandedLyricsList").ItemsSource) && EpField<DispatcherTimer>(w, "_lyricsTimer").IsEnabled, "closing queue reset lyrics source or timer");
        EpInvoke(w, "SetExpandedQueueVisible", true, false); var rows = EpField<ListBox>(w, "_expandedQueueList").ItemsSource;
        EpInvoke(w, "SetExpandedLyricsVisible", false, false);
        Assert(EpField<bool>(w, "_expandedQueueOpen") && ReferenceEquals(rows, EpField<ListBox>(w, "_expandedQueueList").ItemsSource), "closing lyrics reset queue source");
        w.SignOut(); Assert(!EpOpenState(w) && EpField<ListBox>(w, "_expandedQueueList").ItemsSource is null && !EpField<DispatcherTimer>(w, "_lyricsTimer").IsEnabled, "account close retained queue or lyrics activity");
    }

    private static async Task Eq0317LargeQueue()
    {
        using var f = new ExpandedFixture("queue-large"); await f.Play(); var w = f.Window;
        var rows = Enumerable.Range(0, 3904).Select(index => new TrackRow { Id = "eq-" + index, RequestId = "eq-" + index, Title = "Длинное название трека " + index,
            Artist = "Проверочный исполнитель", DurationMs = 50000, Available = true, LocalPath = f.First.LocalPath }).ToArray();
        f.Queue.SetItems(rows, rows[0]); await w.PlayTrackAsync(rows[0]); EpOpen(w); EpInvoke(w, "SetExpandedQueueVisible", true, false); w.UpdateLayout();
        var list = EpField<ListBox>(w, "_expandedQueueList"); var source = list.ItemsSource; var wrappers = EpField<Array>(w, "_expandedQueueRows");
        Assert(list.Items.Count == 3904 && VirtualizingPanel.GetIsVirtualizing(list) && VirtualizingPanel.GetVirtualizationMode(list) == VirtualizationMode.Recycling
            && VirtualizingPanel.GetScrollUnit(list) == ScrollUnit.Pixel && ScrollViewer.GetCanContentScroll(list), "queue list is not pixel/recycling virtualized");
        var realized = EpVisuals<ListBoxItem>(list).Count(); Assert(realized is > 1 and < 100, "3904-row list realized every row");
        var calls = f.Handler.Seen.Count; var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 200; i++) EpInvoke(w, "RefreshExpandedQueue");
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert(ReferenceEquals(source, list.ItemsSource) && ReferenceEquals(wrappers, EpField<Array>(w, "_expandedQueueRows")) && allocated < 1_000_000 && calls == f.Handler.Seen.Count, "unchanged tick rebuilt queue or issued requests");
        f.Queue.SelectAt(2500); EpInvoke(w, "RefreshExpandedQueue");
        Assert(ReferenceEquals(source, list.ItemsSource) && ReferenceEquals(wrappers, EpField<Array>(w, "_expandedQueueRows")) && list.SelectedIndex == 2500, "current-only version rebuilt cached wrappers");
        list.ScrollIntoView(list.Items[2500]); w.UpdateLayout(); await Task.Delay(70); w.UpdateLayout();
        Assert(EpVisuals<ListBoxItem>(list).Count() < 100 && list.ItemContainerGenerator.ContainerFromIndex(2500) is not null,
            $"large queue scroll failed virtualization; realized={EpVisuals<ListBoxItem>(list).Count()}, target={list.ItemContainerGenerator.ContainerFromIndex(2500) is not null}, offset={EpVisuals<ScrollViewer>(list).First().VerticalOffset}");
        App.Capture(w, Path.Combine(_directory, "expanded-queue-0317-3904-scroll.png"));
        EpInvoke(w, "SetExpandedQueueVisible", false, false);
        Assert(list.ItemsSource is null && EpField<Array>(w, "_expandedQueueRows").Length == 0, "closing queue retained row bindings");
        File.WriteAllText(Path.Combine(_directory, "expanded-queue-0317-performance.json"), JsonSerializer.Serialize(new { rows = 3904, realized, unchangedRefreshes = 200, allocatedBytes = allocated, rowSourceStable = true, currentOnlyWrapperArrayStable = true }));
    }

    private static async Task Eq0317Selection()
    {
        using var f = new ExpandedFixture("queue-selection"); await f.Play(); var w = f.Window;
        f.Queue.SetItems([f.First, f.First, f.Second], f.First); await w.PlayTrackAsync(f.First); var originalItems = f.Queue.Items;
        EpOpen(w); EpInvoke(w, "SetExpandedQueueVisible", true, false); w.UpdateLayout();
        var list = EpField<ListBox>(w, "_expandedQueueList"); var token = f.Queue.CurrentOccurrenceToken;
        EpSet(w, "_pendingSeek", 30d); list.SelectedIndex = 1;
        list.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(list), Environment.TickCount, Key.Enter) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
        await Until(() => !EpField<bool>(w, "_loadingStream"));
        Assert(f.Queue.CurrentIndex == 1 && f.Queue.CurrentOccurrenceToken != token && ReferenceEquals(f.Queue.Items, originalItems) && f.Player.SourceLocalPath == f.First.LocalPath
            && EpField<double?>(w, "_pendingSeek") is null, "keyboard queue selection lost exact duplicate occurrence, replaced queue, or retained stale seek");
        var stale = list.Items[2]; f.Queue.Move(2, 0); var version = f.Queue.Version;
        await Eq0317PlayRow(w, stale); Assert(f.Queue.Version == version, "stale row selected a different occurrence after reorder");
        EpInvoke(w, "RefreshExpandedQueue"); var fresh = list.Items[0]; var generation = EpField<int>(w, "_accountGeneration"); EpSet(w, "_accountGeneration", generation + 1);
        await Eq0317PlayRow(w, fresh); Assert(f.Queue.Version == version, "old account queue row entered playback");
        EpSet(w, "_accountGeneration", generation); w.SignOut(); await Eq0317PlayRow(w, fresh);
        Assert(!f.Player.HasSource && f.Queue.Items.Count == 0 && list.ItemsSource is null && f.MediaCalls == 0, "late signed-out click revived playback or retained rows");
    }

    private static Task Eq0317PlayRow(MainWindow window, object row) => (Task)typeof(MainWindow).GetMethod("PlayExpandedQueueRowAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [row])!;

    private static async Task Eq0317Reveal()
    {
        using (var f = new ExpandedFixture("queue-reveal"))
        {
            await f.Play(); var w = f.Window; await Until(() => Control<Image>(w, "PlayerCover").Source is not null);
            var origin = typeof(MainWindow).GetMethod("CaptureExpandedRevealOrigin", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(w, null);
            EpOpen(w); var stage = EpField<Grid>(w, "_expandedRevealStage"); var surface = EpField<Grid>(w, "_expandedPlayerSurface"); var baseChildren = stage.Children.Count;
            EpInvoke(w, "BeginExpandedReveal", origin); await Dispatcher.Yield(DispatcherPriority.Loaded);
            if (Motion.CanAnimate)
            {
                Assert(origin is not null && EpField<object?>(w, "_expandedReveal") is not null, "animated fixture did not start a bounded reveal");
                var sourceBounds = (Rect)origin!.GetType().GetProperty("ScreenBounds")!.GetValue(origin)!;
                Assert(sourceBounds.Left < 0 && sourceBounds.Width > 40, "cover origin lost physical negative desktop coordinates");
                var ghost = EpVisuals<Image>(stage).Single(image => image.Name == "ExpandedCoverTransition");
                Assert(ReferenceEquals(ghost.Source, Control<Image>(w, "PlayerCover").Source), "cover transition captured/redecoded an image instead of sharing its existing source");
                App.Capture(w, Path.Combine(_directory, "fullscreen-reveal-0317-frame-000.png"));
                await Task.Delay(110); w.UpdateLayout();
                var mid = ghost.TransformToAncestor(stage).TransformBounds(new Rect(ghost.RenderSize));
                Assert(stage.Opacity is > 0 and < 1 && surface.Opacity == 1 && mid.Width > 60 && mid.Width < EpElement<Button>(w, "ExpandedPlayerCover").ActualWidth,
                    "cover reveal did not pass through an intermediate scale/opacity");
                App.Capture(w, Path.Combine(_directory, "fullscreen-reveal-0317-frame-110.png"));
                await Task.Delay(130); App.Capture(w, Path.Combine(_directory, "fullscreen-reveal-0317-frame-240.png"));
                await Task.Delay(200); w.UpdateLayout();
                Assert(EpField<object?>(w, "_expandedReveal") is null && stage.Children.Count == baseChildren && !stage.HasAnimatedProperties && stage.Opacity == 1 && !surface.HasAnimatedProperties && surface.Opacity == 1 && EpElement<Button>(w, "ExpandedPlayerCover").Opacity == 1,
                    $"completed reveal retained state={EpField<object?>(w, "_expandedReveal") is not null}, children={stage.Children.Count}/{baseChildren}, clocks={stage.HasAnimatedProperties}, stage={stage.Opacity}, surface={surface.Opacity}, cover={EpElement<Button>(w, "ExpandedPlayerCover").Opacity}");
                App.Capture(w, Path.Combine(_directory, "fullscreen-reveal-0317-frame-440.png"));
            }
            else Assert(EpField<object?>(w, "_expandedReveal") is null && stage.Opacity == 1 && surface.Opacity == 1 && stage.Children.Count == baseChildren, "reduced-motion/software rendering still started fullscreen animation");
            for (var i = 0; i < 4; i++)
            {
                EpInvoke(w, "BeginExpandedReveal", origin); EpInvoke(w, "CloseExpandedPlayer");
                Assert(EpField<object?>(w, "_expandedReveal") is null && EpField<object?>(w, "_expandedRevealPending") is null && stage.Opacity == 1 && surface.Opacity == 1 && stage.Children.Count == baseChildren, "rapid close retained pending callback or overlay");
                EpOpen(w);
            }
            EpInvoke(w, "BeginExpandedReveal", origin); await Dispatcher.Yield(DispatcherPriority.Loaded);
            await w.PlayTrackAsync(f.Second);
            Assert(EpField<object?>(w, "_expandedReveal") is null && EpField<TextBlock>(w, "_expandedPlayerTitle").Text == f.Second.Title, "track change retained an old shared-cover ghost");
            EpInvoke(w, "BeginExpandedReveal", origin); await Dispatcher.Yield(DispatcherPriority.Loaded);
            EpInvoke(w, "SetExpandedQueueVisible", true, false);
            Assert(EpField<object?>(w, "_expandedReveal") is null && EpField<bool>(w, "_expandedQueueOpen"), "pane toggle retained a ghost with obsolete cover geometry");
            EpInvoke(w, "BeginExpandedReveal", origin); await Dispatcher.Yield(DispatcherPriority.Loaded); w.SignOut();
            Assert(!EpOpenState(w) && EpField<object?>(w, "_expandedReveal") is null && EpField<object?>(w, "_expandedRevealPending") is null && stage.Children.Count == baseChildren, "signout retained fullscreen transition work");
        }
        using (var f = new ExpandedFixture("queue-reveal-close"))
        {
            await f.Play(); EpOpen(f.Window); EpInvoke(f.Window, "BeginExpandedReveal", new object?[] { null });
            await Dispatcher.Yield(DispatcherPriority.Loaded); f.Window.Close();
            Assert(EpField<object?>(f.Window, "_expandedReveal") is null && EpField<object?>(f.Window, "_expandedRevealPending") is null, "closed window retained fullscreen clocks");
        }
        using (var f = new ExpandedFixture("queue-reveal-minimized", show: false))
        {
            var w = f.Window; EpSet(w, "_current", f.First); w.Left = -17000; w.Top = 20; w.Width = 1180; w.Height = 760;
            w.WindowState = WindowState.Minimized; var style = w.WindowStyle; var resize = w.ResizeMode;
            EpInvoke(w, "SetExpandedPlayerVisible", true, true); EpInvoke(w, "CloseExpandedPlayer");
            Assert(w.WindowState == WindowState.Minimized && w.WindowStyle == style && w.ResizeMode == resize && w.Left == -17000 && w.Top == 20 && w.Width == 1180 && w.Height == 760 && !w.IsVisible,
                "fullscreen from minimized lost saved placement or showed a fixture window");
        }
        File.WriteAllText(Path.Combine(_directory, "fullscreen-reveal-0317-scope.json"), JsonSerializer.Serialize(new
        {
            motionEnabledInFixture = Motion.CanAnimate, durationMs = 380, frameCallbacks = false, timers = false, imageSourceShared = true,
            perFrameNativeWindowWrites = false, actualDesktopWindowMaximized = false, placementCheckedOnNeverShownWindows = true,
            negativeScreenCoordinatesChecked = Motion.CanAnimate, physicalMultimonitorAndDifferentDpi = "Not exercised on user desktop; WPF screen-coordinate conversion used"
        }, new JsonSerializerOptions { WriteIndented = true }));
    }
}
