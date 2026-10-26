using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using YMusic.Native;

internal static partial class Program
{
    private static async Task CheckExpandedPlayer()
    {
        var original = typeof(Artwork).GetField("_cache", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null);
        var artworkCalls = 0;
        var artworkBytes = EpArtwork();
        using var artwork = new ArtworkCache(Path.Combine(_directory, "expanded-player-artwork"), new FakeHandler((request, _) =>
        {
            Assert(ArtworkUri.Valid(request.RequestUri!) && request.Headers.Authorization is null && !request.Headers.Contains("Cookie"), "expanded artwork left anonymous fixture CDN");
            Interlocked.Increment(ref artworkCalls);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(artworkBytes) });
        }));
        Artwork.Cache = artwork;
        try
        {
            await Run("Expanded player: large artwork and complete inline controls fit both supported window sizes", () => EpAppearance(() => artworkCalls));
            await Run("Expanded player: actual transport, exact seek, canceled drag, shared volume and confirmed like", EpTransport);
            await Run("Expanded player: lyrics slide left, share native timed lines, seek, follow and return to center", EpLyrics);
            await Run("Expanded player: lyrics loading, errors, plain fallback and late account response stay isolated", EpLyricsStates);
            await Run("Expanded player: album navigation, Escape and signout preserve or clear the shared queue", EpLifecycle);
            await Run("Expanded player: fullscreen placement restores normal and maximized state without showing a window", EpPlacement);
            File.WriteAllText(Path.Combine(_directory, "expanded-player-scope.json"), JsonSerializer.Serialize(new
            {
                realAccountLoaded = false, externalNetworkRequests = 0, userAudioDirectoryUsed = false,
                globalInputUsed = false, realClientWindowTouched = false, shownFixturesMaximized = false,
                fullscreenWindowPropertiesCheckedOnNeverShownWindows = true,
                physicalMonitorFullscreenAndTaskbarCoverage = "Not exercised; fixture windows remain offscreen",
                artworkFixtureRequests = artworkCalls, nativeWavMuted = true, lyricsTimerMilliseconds = 100,
                checkedAssemblySha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(MainWindow).Assembly.Location)))
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        finally { typeof(Artwork).GetField("_cache", BindingFlags.NonPublic | BindingFlags.Static)!.SetValue(null, original); }
    }

    private static async Task EpAppearance(Func<int> artworkCalls)
    {
        using var f = new ExpandedFixture("appearance"); await f.Play();
        var window = f.Window; var queueVersion = f.Queue.Version; var queueToken = f.Queue.CurrentOccurrenceToken;
        var windowCount = Application.Current.Windows.Count;
        EpOpen(window); await Until(() => EpField<Image>(window, "_expandedPlayerCover").Source is BitmapSource);
        var host = Control<ContentControl>(window, "ExpandedPlayerHost");
        Assert(ReferenceEquals(EpField<NativePlayer>(window, "_player"), f.Player) && ReferenceEquals(f.Queue, EpField<PlaybackQueue>(window, "_playbackQueue")), "expanded view created a second playback owner");
        Assert(Artwork.GetPixelSize(EpField<Image>(window, "_expandedPlayerCover")) == 1024, "expanded artwork is not decoded at the dedicated large size");
        var decodedCover = (BitmapSource)EpField<Image>(window, "_expandedPlayerCover").Source;
        Assert(decodedCover.PixelWidth is > 256 and <= 1024 && decodedCover.PixelHeight is > 256 and <= 1024,
            "large CDN request was decoded back to the small cover bucket");
        foreach (var (width, height) in new[] { (1050, 680), (1280, 860) })
        {
            window.Width = width; window.Height = height; window.UpdateLayout(); await Task.Delay(220);
            var cover = EpElement<Button>(window, "ExpandedPlayerCover");
            var bounds = cover.TransformToAncestor(host).TransformBounds(new Rect(cover.RenderSize));
            Assert(bounds.Width > 275 && bounds.Height > 275 && Math.Abs(bounds.Width - bounds.Height) < 2, "large square artwork is cropped or too small");
            foreach (var name in new[] { "ExpandedPlayerClose", "ExpandedPlayerPlayPause", "ExpandedPlayerPrevious", "ExpandedPlayerNext", "ExpandedPlayerLike", "ExpandedPlayerLyrics", "ExpandedPlayerSeek", "ExpandedPlayerVolume" })
            {
                var control = EpVisuals<FrameworkElement>(host).Single(element => element.Name == name);
                var rectangle = control.TransformToAncestor(host).TransformBounds(new Rect(control.RenderSize));
                Assert(control.IsVisible && rectangle.Left >= 0 && rectangle.Top >= 0 && rectangle.Right <= host.ActualWidth + 1 && rectangle.Bottom <= host.ActualHeight + 1,
                    name + " is clipped at " + width);
                Assert(!string.IsNullOrWhiteSpace(AutomationProperties.GetName(control)), name + " has no accessible name");
            }
            App.Capture(window, Path.Combine(_directory, $"expanded-player-centered-{width}x{height}.png"));
        }
        var beforeArtwork = artworkCalls(); var beforeApi = f.Handler.Seen.Count;
        for (var i = 0; i < 80; i++) { EpInvoke(window, "UpdateProgress"); EpInvoke(window, "RefreshExpandedPlayer"); window.UpdateLayout(); }
        await Task.Delay(100);
        Assert(beforeArtwork == artworkCalls() && beforeApi == f.Handler.Seen.Count && f.MediaCalls == 0, "progress/render issued network or reloaded artwork");
        Assert(f.Queue.Version == queueVersion && f.Queue.CurrentOccurrenceToken == queueToken && Application.Current.Windows.Count == windowCount && window.OwnedWindows.Count == 0,
            "opening/rendering changed the queue or opened an auxiliary window");
    }

    private static async Task EpTransport()
    {
        using var f = new ExpandedFixture("transport"); await f.Play(); EpOpen(f.Window);
        var w = f.Window; var version = f.Queue.Version; var items = f.Queue.Items; var occurrence = f.Queue.CurrentOccurrenceToken;
        EpClick(EpElement<Button>(w, "ExpandedPlayerPlayPause")); await Until(() => !f.Player.IsPlaying);
        Assert(Equals(EpElement<Button>(w, "ExpandedPlayerPlayPause").Content, Control<Button>(w, "PlayPauseButton").Content), "expanded pause glyph drifted");
        EpClick(EpElement<Button>(w, "ExpandedPlayerPlayPause")); await Until(() => f.Player.IsPlaying);
        var seek = EpElement<Slider>(w, "ExpandedPlayerSeek");
        seek.Value = 12.25; EpKey(seek, Key.Right);
        Assert(EpField<double?>(w, "_pendingSeek") == 12.25 && Math.Abs(Control<Slider>(w, "SeekSlider").Value - 12.25) < .001, "expanded keyboard seek bypassed the shared exact target");
        await Until(() => Math.Abs(f.Player.Position.TotalSeconds - 12.25) < .4);
        EpSet(w, "_expandedSeekDragging", true); seek.Value = 22;
        EpInvoke(w, "UpdateProgress"); Assert(Math.Abs(seek.Value - 22) < .001, "progress overwrote a drag preview");
        seek.RaiseEvent(new DragCompletedEventArgs(0, 0, false) { RoutedEvent = Thumb.DragCompletedEvent });
        Assert(EpField<double?>(w, "_pendingSeek") == 22, "completed drag did not use CommitSeek");
        EpSet(w, "_expandedSeekDragging", true); seek.Value = 34;
        seek.RaiseEvent(new DragCompletedEventArgs(0, 0, true) { RoutedEvent = Thumb.DragCompletedEvent });
        Assert(EpField<double?>(w, "_pendingSeek") != 34 && !EpField<bool>(w, "_expandedSeekDragging"), "canceled expanded drag committed audio position");
        EpInvoke(w, "CommitSeek", 0d);
        EpClick(EpElement<Button>(w, "ExpandedPlayerNext")); await Until(() => ReferenceEquals(EpField<TrackRow>(w, "_current"), f.Second) && !EpField<bool>(w, "_loadingStream"));
        Assert(EpField<TextBlock>(w, "_expandedPlayerTitle").Text == f.Second.Title && f.Player.SourceLocalPath == f.Second.LocalPath, "next did not synchronize the existing player and title");
        EpClick(EpElement<Button>(w, "ExpandedPlayerPrevious")); await Until(() => ReferenceEquals(EpField<TrackRow>(w, "_current"), f.First) && !EpField<bool>(w, "_loadingStream"));
        EpClick(EpElement<Button>(w, "ExpandedPlayerPlayPause")); await Until(() => !f.Player.IsPlaying);
        EpElement<Slider>(w, "ExpandedPlayerVolume").Value = 37;
        Assert(Control<Slider>(w, "VolumeSlider").Value == 37 && Math.Abs(f.Player.Volume - .37) < .001 && Math.Abs(f.Preferences.Load().Volume - .37) < .001,
            "expanded volume did not use the existing preference/native route");
        Control<Slider>(w, "VolumeSlider").Value = 0;
        Assert(EpElement<Slider>(w, "ExpandedPlayerVolume").Value == 0, "ordinary volume did not synchronize back");
        EpClick(EpElement<Button>(w, "ExpandedPlayerLike")); await Until(() => !EpField<bool>(w, "_catalogWriting") && !EpField<HashSet<string>>(w, "_favoriteTrackIds").Contains(f.First.Id));
        Assert(f.Handler.Seen.Count(call => call.Uri.AbsolutePath == "/users/12345/likes/tracks/remove") == 1 && Equals(EpElement<Button>(w, "ExpandedPlayerLike").Content, "\uEB51"), "expanded like did not use one confirmed existing mutation");
        Assert(f.Queue.Version == version + 2 && ReferenceEquals(f.Queue.Items, items) && f.Queue.CurrentOccurrenceToken == occurrence && ReferenceEquals(f.Queue.Current, f.First) && f.MediaCalls == 0, "controls rebuilt the queue or used media network");
        EpSet(w, "_loadingStream", true); EpInvoke(w, "UpdateControls"); EpInvoke(w, "RefreshExpandedPlayer");
        Assert(!EpElement<Button>(w, "ExpandedPlayerPlayPause").IsEnabled && !seek.IsEnabled, "loading transport/seek are not synchronized");
        EpSet(w, "_loadingStream", false); EpInvoke(w, "UpdateControls");
    }

    private static async Task EpLyrics()
    {
        using var f = new ExpandedFixture("lyrics"); await f.Play();
        var w = f.Window; w.Width = 1280; w.Height = 860; w.UpdateLayout(); EpOpen(w); w.UpdateLayout();
        var host = Control<ContentControl>(w, "ExpandedPlayerHost"); var cover = EpElement<Button>(w, "ExpandedPlayerCover");
        var center = cover.TranslatePoint(new Point(cover.ActualWidth / 2, 0), host).X;
        var version = f.Queue.Version; var before = f.Handler.Seen.Count;
        EpClick(EpElement<Button>(w, "ExpandedPlayerLyrics"));
        var panel = EpField<Grid>(w, "_expandedLyricsPanel");
        if (SystemParameters.ClientAreaAnimation)
        {
            await Task.Delay(125);
            Assert(panel.Width > 0 && panel.Width < ((FrameworkElement)panel.Parent).ActualWidth * .49, "lyrics did not animate through an intermediate layout");
        }
        await Until(() => EpField<ListBox>(w, "_expandedLyricsList").Items.Count == 50);
        await Task.Delay(380); w.UpdateLayout();
        var list = EpField<ListBox>(w, "_expandedLyricsList");
        Assert(ReferenceEquals(list.ItemsSource, Control<ListBox>(w, "LyricsList").ItemsSource) && ReferenceEquals(list.ItemContainerStyle, Control<ListBox>(w, "LyricsList").ItemContainerStyle), "expanded lyrics copied another lyrics state or style");
        Assert(cover.TranslatePoint(new Point(cover.ActualWidth / 2, 0), host).X < center - 150 && panel.IsVisible && panel.ActualWidth > 400, "lyrics did not move the cover left and reveal the right panel");
        Assert(EpField<DispatcherTimer>(w, "_lyricsTimer").IsEnabled && EpField<DispatcherTimer>(w, "_lyricsTimer").Interval == TimeSpan.FromMilliseconds(100), "expanded lyrics do not share the existing native clock");
        Assert(f.Handler.Seen.Count - before == 3 && f.Lrclib.Seen.Count == 1 && f.Genius.Seen.Count == 0, "expanded lyrics duplicated a provider request or bypassed exact LRCLIB fallback");
        EpClick(EpElement<Button>(w, "ExpandedPlayerPlayPause")); await Until(() => !f.Player.IsPlaying);
        EpInvoke(w, "UpdateLyrics", 7.9d); await Dispatcher.Yield(DispatcherPriority.DataBind);
        Assert(list.SelectedIndex == 7 && Control<ListBox>(w, "LyricsList").SelectedIndex == 7, "expanded active line differs from the original timed source");
        // Manual navigation suspends lyric follow. Bring the exact virtualized
        // index through WPF rather than relying on the item-based request.
        EpInvoke(w, "PauseExpandedLyricsFollow");
        typeof(VirtualizingStackPanel).GetMethod("BringIndexIntoView", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(EpVisuals<VirtualizingStackPanel>(list).First(), [15]);
        await Until(() => { w.UpdateLayout(); return list.ItemContainerGenerator.ContainerFromIndex(15) is ListBoxItem; });
        var line = (ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(15)!;
        Assert(line is not null, "expanded lyric seek row did not realize");
        var click = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = Mouse.PreviewMouseUpEvent };
        line!.RaiseEvent(click);
        Assert(click.Handled && EpField<double?>(w, "_pendingSeek") == 15 && Math.Abs(Control<Slider>(w, "SeekSlider").Value - 15) < .001, "expanded single-click lyric did not seek through the shared CommitSeek");
        EpInvoke(w, "PauseExpandedLyricsFollow");
        var viewer = EpVisuals<ScrollViewer>(list).First(); SmoothScroll.CancelAnimation(viewer); viewer.ScrollToVerticalOffset(viewer.ScrollableHeight * .7); w.UpdateLayout();
        var offset = viewer.VerticalOffset; EpInvoke(w, "UpdateLyrics", 2d); await Task.Delay(120); w.UpdateLayout();
        Assert(Math.Abs(viewer.VerticalOffset - offset) < 1, "native line updates overrode manual expanded lyrics scrolling");
        EpSet(w, "_expandedLyricsFollowUntil", DateTimeOffset.UtcNow.AddMilliseconds(-1)); EpInvoke(w, "UpdateLyrics", 2d);
        await Task.Delay(350); w.UpdateLayout(); Assert(viewer.VerticalOffset < offset - 30, "expanded lyrics did not resume follow after idle");
        foreach (var (width, height) in new[] { (1050, 680), (1280, 860) })
        { w.Width = width; w.Height = height; w.UpdateLayout(); await Task.Delay(160); Assert(list.ActualWidth > 350 && list.ActualHeight > 300 && viewer.ScrollableWidth < 1, "lyrics panel is cropped at supported size"); App.Capture(w, Path.Combine(_directory, $"expanded-player-lyrics-{width}x{height}.png")); }
        EpClick(EpElement<Button>(w, "ExpandedPlayerLyrics")); await Task.Delay(380); w.UpdateLayout();
        Assert(!panel.IsVisible && !EpField<DispatcherTimer>(w, "_lyricsTimer").IsEnabled && Math.Abs(cover.TranslatePoint(new Point(cover.ActualWidth / 2, 0), host).X - center) < 2, "second lyrics click did not restore the centered cover and stop the hidden timer");
        EpClick(EpElement<Button>(w, "ExpandedPlayerLyrics")); await Task.Delay(380);
        Assert(f.Handler.Seen.Count - before == 3 && f.Lrclib.Seen.Count == 1 && f.Queue.Version == version, "cached expanded reopen requested data again or changed the queue");
        await w.PlayTrackAsync(f.Second);
        await Until(() => list.Items.Count == 50 && Control<TextBlock>(w, "LyricsHeading").Text == f.Second.Title);
        Assert(EpField<TextBlock>(w, "_expandedPlayerTitle").Text == f.Second.Title && ReferenceEquals(list.ItemsSource, Control<ListBox>(w, "LyricsList").ItemsSource)
            && f.Lrclib.Seen.Count == 2, "current-track change retained old expanded metadata or started another lyrics pipeline");
    }

    private static async Task EpLyricsStates()
    {
        using (var f = new ExpandedFixture("lyrics-plain", synchronized: false))
        {
            await f.Play(); EpOpen(f.Window); EpClick(EpElement<Button>(f.Window, "ExpandedPlayerLyrics"));
            await Until(() => EpField<ListBox>(f.Window, "_expandedLyricsList").Items.Count == 2);
            Assert(EpField<IReadOnlyList<LyricLine>>(f.Window, "_lyricLines").Count == 0 && EpVisuals<TextBlock>(Control<ContentControl>(f.Window, "ExpandedPlayerHost")).Any(t => t.Text.Contains("без синхронизации", StringComparison.Ordinal)), "plain fallback lost its existing source note or gained false timing");
        }
        using (var f = new ExpandedFixture("lyrics-error", failure: true))
        {
            await f.Play(); EpOpen(f.Window); EpClick(EpElement<Button>(f.Window, "ExpandedPlayerLyrics"));
            await Until(() => EpField<CancellationTokenSource?>(f.Window, "_lyricsCts") is null);
            Assert(Control<TextBlock>(f.Window, "LyricsNote").Text.Contains("Не удалось загрузить текст", StringComparison.Ordinal) && f.Lrclib.Seen.Count == 0 && f.Genius.Seen.Count == 0,
                "existing lyrics error switched providers or was hidden in expanded mode");
            Assert(EpField<ListBox>(f.Window, "_expandedLyricsList").Items.Count == 0 && EpVisuals<TextBlock>(Control<ContentControl>(f.Window, "ExpandedPlayerHost")).Any(t => t.Text.Contains("Не удалось загрузить текст", StringComparison.Ordinal)), "expanded error did not match the shared source state");
        }
        using (var f = new ExpandedFixture("lyrics-late", hold: true))
        {
            await f.Play(); EpOpen(f.Window); EpClick(EpElement<Button>(f.Window, "ExpandedPlayerLyrics"));
            await f.LyricsEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert(Control<TextBlock>(f.Window, "LyricsNote").Text.Contains("Загружаем текст", StringComparison.Ordinal), "expanded lyrics did not expose loading");
            var sharedRequest = EpField<CancellationTokenSource>(f.Window, "_lyricsCts");
            f.Window.SignOut();
            Assert(!EpOpenState(f.Window) && sharedRequest.IsCancellationRequested && !f.Player.HasSource && !EpField<DispatcherTimer>(f.Window, "_lyricsTimer").IsEnabled, "signout kept expanded lyrics/request/player alive");
            f.ReleaseLyrics.TrySetResult(); await Task.Delay(200);
            Assert(!EpOpenState(f.Window) && EpField<ListBox>(f.Window, "_expandedLyricsList").Items.Count == 0 && f.Queue.Items.Count == 0,
                "late lyrics response resurrected the old account view or queue");
        }
        using (var f = new ExpandedFixture("lyrics-track-race", hold: true))
        {
            await f.Play(); EpOpen(f.Window); EpClick(EpElement<Button>(f.Window, "ExpandedPlayerLyrics"));
            await f.LyricsEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var old = EpField<CancellationTokenSource>(f.Window, "_lyricsCts");
            await f.Window.PlayTrackAsync(f.Second);
            await Until(() => EpField<ListBox>(f.Window, "_expandedLyricsList").Items.Count == 50);
            Assert(old.IsCancellationRequested && Control<TextBlock>(f.Window, "LyricsHeading").Text == f.Second.Title, "track change did not cancel the previous shared lyrics request");
            f.ReleaseLyrics.TrySetResult(); await Task.Delay(180);
            Assert(EpField<TextBlock>(f.Window, "_expandedPlayerTitle").Text == f.Second.Title && Control<TextBlock>(f.Window, "LyricsHeading").Text == f.Second.Title
                && !EpField<System.Collections.IDictionary>(f.Window, "_lyricsCache").Contains(f.First.Id), "late previous-track lyrics overwrote or entered the expanded current-track state");
        }
    }

    private static async Task EpLifecycle()
    {
        using var f = new ExpandedFixture("lifecycle"); await f.Play(); EpOpen(f.Window);
        var queueVersion = f.Queue.Version; var current = f.Queue.Current; var w = f.Window;
        EpClick(EpElement<Button>(w, "ExpandedPlayerTitle"));
        await Until(() => EpField<string>(w, "_section") == "album-detail" && EpField<CancellationTokenSource?>(w, "_featureCts") is null);
        Assert(!EpOpenState(w) && f.Handler.Seen.Any(call => call.Uri.AbsolutePath == "/albums/ep-album/with-tracks") && f.Queue.Version == queueVersion && ReferenceEquals(f.Queue.Current, current) && f.Player.IsPlaying,
            "expanded title did not close into the exact album without disturbing playback");
        EpOpen(w); EpClick(EpElement<Button>(w, "ExpandedPlayerCover")); await Task.Delay(80);
        Assert(!EpOpenState(w), "cover did not close expanded before opening its album");
        EpOpen(w);
        var escape = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(w), Environment.TickCount, Key.Escape) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
        w.RaiseEvent(escape);
        Assert(escape.Handled && !EpOpenState(w) && f.Player.IsPlaying && f.Queue.Version == queueVersion, "Escape did not restore the same shared playback view");
        EpOpen(w); w.SignOut();
        Assert(!EpOpenState(w) && EpField<Image>(w, "_expandedPlayerCover").Source is null && Artwork.GetSourceUri(EpField<Image>(w, "_expandedPlayerCover")) is null && !f.Player.HasSource && f.Queue.Items.Count == 0,
            "signout retained expanded cover or current playback");
        EpOpen(w); Assert(!EpOpenState(w), "expanded view opened without a current track");
    }

    private static Task EpPlacement()
    {
        foreach (var state in new[] { WindowState.Normal, WindowState.Maximized })
        {
            using var f = new ExpandedFixture("placement-" + state, show: false);
            var w = f.Window; EpSet(w, "_current", f.First); w.Left = -17000; w.Top = 20; w.Width = 1180; w.Height = 760;
            w.WindowState = state; var style = w.WindowStyle; var resize = w.ResizeMode;
            Assert(!w.IsVisible && PresentationSource.FromVisual(w) is null, "placement fixture unexpectedly became visible");
            EpInvoke(w, "SetExpandedPlayerVisible", true, true);
            var fullscreen = Fh040Window(w);
            Assert(w.WindowStyle == style && w.ResizeMode == resize && w.WindowState == state
                && fullscreen.WindowStyle == WindowStyle.None && fullscreen.ResizeMode == ResizeMode.NoResize
                && fullscreen.WindowState == WindowState.Normal && fullscreen.AllowsTransparency
                && !w.IsVisible && !fullscreen.IsVisible && PresentationSource.FromVisual(w) is null,
                "fullscreen changed the main window or created native presentation in a never-shown fixture");
            EpInvoke(w, "CloseExpandedPlayer");
            Assert(w.WindowStyle == style && w.ResizeMode == resize && w.WindowState == state && w.Left == -17000 && w.Top == 20 && w.Width == 1180 && w.Height == 760 && !w.IsVisible,
                "closing fullscreen did not restore prior normal/maximized placement");
        }
        return Task.CompletedTask;
    }

    private static void EpOpen(MainWindow w) { EpInvoke(w, "SetExpandedPlayerVisible", true, false); w.UpdateLayout(); }
    private static bool EpOpenState(MainWindow w) => Control<ContentControl>(w, "ExpandedPlayerHost").Visibility == Visibility.Visible;
    private static void EpClick(Button button) { Assert(button.IsEnabled, button.Name + " is unexpectedly disabled"); button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); }
    private static void EpKey(Slider slider, Key key) => slider.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(slider), Environment.TickCount, key) { RoutedEvent = Keyboard.PreviewKeyUpEvent });
    private static void EpInvoke(MainWindow w, string name, params object?[] args) => typeof(MainWindow).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(w, args);
    private static T EpField<T>(MainWindow w, string name) => (T)typeof(MainWindow).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(w)!;
    private static void EpSet(MainWindow w, string name, object? value) => typeof(MainWindow).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(w, value);
    private static T EpElement<T>(MainWindow w, string name) where T : FrameworkElement => EpVisuals<T>(Control<ContentControl>(w, "ExpandedPlayerHost")).Single(element => element.Name == name);
    private static IEnumerable<T> EpVisuals<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T typed) yield return typed;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) foreach (var child in EpVisuals<T>(VisualTreeHelper.GetChild(root, i))) yield return child;
    }
    private static byte[] EpArtwork()
    {
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            drawing.DrawRectangle(new LinearGradientBrush(Color.FromRgb(223, 100, 46), Color.FromRgb(45, 54, 90), 55), null, new Rect(0, 0, 1024, 1024));
            drawing.DrawEllipse(new SolidColorBrush(Color.FromArgb(130, 255, 201, 103)), null, new Point(790, 240), 450, 450);
            drawing.DrawEllipse(new SolidColorBrush(Color.FromArgb(160, 29, 38, 58)), null, new Point(190, 880), 470, 470);
            drawing.DrawEllipse(new SolidColorBrush(Color.FromArgb(235, 245, 230, 213)), null, new Point(512, 512), 170, 170);
            drawing.DrawEllipse(new SolidColorBrush(Color.FromRgb(172, 77, 44)), null, new Point(512, 512), 45, 45);
        }
        var bitmap = new RenderTargetBitmap(1024, 1024, 96, 96, PixelFormats.Pbgra32); bitmap.Render(visual);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var stream = new MemoryStream(); encoder.Save(stream); return stream.ToArray();
    }

    private sealed class ExpandedFixture : IDisposable
    {
        public MainWindow Window { get; }
        public NativePlayer Player { get; }
        public TrackRow First { get; }
        public TrackRow Second { get; }
        public PlaybackQueue Queue => EpField<PlaybackQueue>(Window, "_playbackQueue");
        public FakeHandler Handler { get; }
        public FakeHandler Lrclib { get; }
        public FakeHandler Genius { get; }
        public PreferencesStore Preferences { get; }
        public TaskCompletionSource LyricsEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseLyrics { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int MediaCalls;
        private readonly HttpClient _lyricsHttp;
        private bool _liked = true;
        public ExpandedFixture(string suffix, bool synchronized = true, bool failure = false, bool hold = false, bool show = true)
        {
            var directory = Path.Combine(_directory, "expanded-" + suffix); Directory.CreateDirectory(directory);
            var wav = WriteWave("expanded-" + suffix + ".wav", 50);
            First = new() { Id = "70101", RequestId = "70101:ep-album", Title = "Огни большого города", Artist = "Тестовый исполнитель", Album = "Коллекция звука", AlbumId = "ep-album", CoverUri = "avatars.yandex.net/get-music-content/expanded/first/%%", LocalPath = wav, LocalQuality = "Fixture WAV", DurationMs = 50000, Available = true };
            Second = new() { Id = "70102", RequestId = "70102:ep-album", Title = "После полуночи", Artist = "Тестовый исполнитель · Другой участник", Album = "Коллекция звука", AlbumId = "ep-album", CoverUri = "avatars.yandex.net/get-music-content/expanded/second/%%", LocalPath = wav, LocalQuality = "Fixture WAV", DurationMs = 50000, Available = true };
            Handler = new FakeHandler(async (request, token) =>
            {
                Assert(request.RequestUri!.Host == "api.music.yandex.net", "expanded fixture API left its host");
                var path = request.RequestUri.AbsolutePath;
                if (path is "/account/status" or "/account/about") return path == "/account/status" ? Json(Profile) : Json("{\"result\":{\"uid\":\"12345\"}}");
                if (path == "/users/12345/likes/tracks/remove") { _liked = false; return Json("{\"result\":{\"revision\":2}}"); }
                if (path == "/users/12345/likes/tracks") return Json(_liked ? "{\"result\":{\"library\":{\"tracks\":[{\"id\":\"70101\",\"albumId\":\"ep-album\"}]}}}" : "{\"result\":{\"library\":{\"tracks\":[]}}}");
                if (path == "/albums/ep-album/with-tracks") return Json("{\"result\":{\"id\":\"ep-album\",\"title\":\"Коллекция звука\",\"volumes\":[]}}");
                if (path.EndsWith("/lyrics", StringComparison.Ordinal))
                {
                    if (hold && path.Contains("/tracks/" + First.Id + "/", StringComparison.Ordinal)) { LyricsEntered.TrySetResult(); await ReleaseLyrics.Task; }
                    return Json("{}", failure ? HttpStatusCode.Forbidden : HttpStatusCode.NotFound);
                }
                if (path.EndsWith("/supplement", StringComparison.Ordinal)) return failure ? Json("{\"result\":{\"lyrics\":null}}") : Json("{\"result\":{\"lyrics\":{\"hasRights\":true,\"fullLyrics\":\"Первая тестовая строка\\nВторая тестовая строка\"}}}");
                throw new InvalidOperationException("Unexpected expanded fixture route " + path);
            });
            Lrclib = new FakeHandler((request, _) =>
            {
                Assert(request.RequestUri!.Host == "lrclib.net" && request.Headers.Authorization is null && !request.Headers.Contains("Cookie"), "expanded LRCLIB left anonymous fixture transport");
                var text = string.Join('\n', Enumerable.Range(0, 50).Select(index => "[" + TimeSpan.FromSeconds(index).ToString(@"mm\:ss\.fff") + "]Строка " + index + ": огни города отражаются в воде"));
                var track = LyricsFollowQuery(request.RequestUri)["track_name"] == Second.Title ? Second : First;
                return Task.FromResult(synchronized ? LyricsFollowRecord(track, text) : Json("{}", HttpStatusCode.NotFound));
            });
            Genius = new FakeHandler((_, _) => throw new InvalidOperationException("expanded fixture unexpectedly contacted Genius"));
            Player = new NativePlayer(new AudioBuffer(Path.Combine(directory, "audio"), new FakeHandler((_, _) => { Interlocked.Increment(ref MediaCalls); throw new InvalidOperationException("expanded local fixture contacted media network"); })));
            Preferences = new PreferencesStore(Path.Combine(directory, "preferences"));
            Window = new MainWindow(api: new MusicApi(Handler), store: new SessionStore(Path.Combine(directory, "store")), restore: false, diagnosticsDirectory: directory,
                offline: new OfflineLibrary(Path.Combine(directory, "offline")), player: Player, preferences: Preferences)
            { ShowActivated = false, ShowInTaskbar = false, Left = -16000, Top = 0, WindowStartupLocation = WindowStartupLocation.Manual };
            EpField<LrclibLyricsClient>(Window, "_lrclibLyrics").Dispose(); _lyricsHttp = new HttpClient(Lrclib);
            EpSet(Window, "_lrclibLyrics", new LrclibLyricsClient(_lyricsHttp)); EpSet(Window, "_geniusLyrics", new GeniusLyricsClient(Genius));
            EpSet(Window, "_session", FakeSession); EpSet(Window, "_account", new Account("12345", "Expanded fixture", true));
            EpSet(Window, "_favoriteIdsKnown", true); EpField<HashSet<string>>(Window, "_favoriteTrackIds").Add(First.Id);
            if (show) Window.Show(); Control<Slider>(Window, "VolumeSlider").Value = 0; Player.SetVolume(0);
        }
        public async Task Play() { Queue.SetItems([First, Second], First); await Window.PlayTrackAsync(First); Assert(Player.HasSource && Player.IsPlaying && Player.Volume == 0, "muted local expanded fixture did not start"); }
        public void Dispose() { ReleaseLyrics.TrySetResult(); Window.Close(); Player.Dispose(); Handler.Dispose(); _lyricsHttp.Dispose(); }
    }
}
