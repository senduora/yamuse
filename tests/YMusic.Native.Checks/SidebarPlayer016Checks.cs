using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using YMusic.Native;

internal static partial class Program
{
    private static async Task CheckSidebarPlayer016()
    {
        await Run("Sidebar016: profile and bottom actions occupy the full sidebar and navigation remains reachable at both sizes", Sp016Sidebar);
        await Run("Sidebar016: track transport volume and timeline remain bounded and separate to the right of the sidebar", Sp016Player);
    }

    private static readonly (int Width, int Height)[] Sp016Sizes = [(1050, 680), (1280, 860)];

    private static async Task Sp016Sidebar()
    {
        using var f = new SaFixture("sidebar016-profile"); var w = f.Window;
        var readback = new List<object>();
        foreach (var signedIn in new[] { false, true })
        {
            if (signedIn) await f.Activate();
            foreach (var (width, height) in Sp016Sizes)
            {
                await Sp016Resize(w, width, height);
                var root = Control<Grid>(w, "LayoutRoot");
                var sidebar = Control<Border>(w, "Sidebar"); var player = Control<Border>(w, "PlayerBar");
                var profile = Control<Border>(w, "ProfileCard"); var settings = Control<Button>(w, "SettingsButton");
                var mini = SaVisuals<Button>(sidebar).Single(b => Equals(b.Content, "Мини-плеер"));
                var metrics = Control<Border>(w, "MetricsBar"); var nav = Control<ScrollViewer>(w, "NavigationScroll");
                var sidebarRect = Sp016Rect(sidebar, root); var playerRect = Sp016Rect(player, root);
                var metricsRect = Sp016Rect(metrics, root); var profileRect = Sp016Rect(profile, root);
                Assert(Grid.GetRowSpan(sidebar) == 2 && Grid.GetRow(player) == 1 && Grid.GetColumn(player) == 1 && Grid.GetColumnSpan(player) == 1,
                    "sidebar/player no longer use the two-column layout contract");
                Assert(Math.Abs(playerRect.Left - sidebarRect.Right) <= 1 && Math.Abs(sidebarRect.Bottom - playerRect.Bottom) <= 1
                    && Math.Abs(sidebarRect.Bottom - metricsRect.Top) <= 1 && metricsRect.Left <= 1 && metricsRect.Right >= root.ActualWidth - 1,
                    "sidebar is not flush with the player and full-width metrics footer");
                Assert(profileRect.Bottom > playerRect.Top && Sp016Rect(settings, root).Top >= playerRect.Top
                    && Sp016Rect(mini, root).Top >= playerRect.Top, "profile or bottom actions remained above the relocated player row");
                foreach (var element in new FrameworkElement[] { profile, settings, mini, nav }) Sp016Within(element, sidebar);
                foreach (var name in new[] { "AccountText", "LoginButton", "RefreshButton", "LogoutButton" })
                {
                    var element = Control<FrameworkElement>(w, name); if (element.IsVisible) Sp016Within(element, profile);
                }
                Assert(Control<Button>(w, "LoginButton").IsVisible == !signedIn
                    && Control<Button>(w, "RefreshButton").IsVisible == signedIn && Control<Button>(w, "LogoutButton").IsVisible == signedIn
                    && settings.IsVisible && settings.IsEnabled && mini.IsVisible && mini.IsEnabled,
                    "relocated profile/actions lost the signed-in or signed-out state");
                Sp016Separate([profile, mini, settings], root);
                Assert(Sp016Rect(nav, root).Bottom <= profileRect.Top + 1 && nav.ActualHeight > 150,
                    "navigation overlaps the fixed profile or has no usable viewport");
                nav.ScrollToTop(); w.UpdateLayout(); await Dispatcher.Yield(DispatcherPriority.Background);
                Sp016Within(Control<Button>(w, "HomeNav"), nav);
                nav.ScrollToEnd(); w.UpdateLayout(); await Dispatcher.Yield(DispatcherPriority.Background);
                Sp016Within(Control<Button>(w, "QueueHistoryNav"), nav);
                Assert(Sp016Rect(profile, root) == profileRect, "scrolling navigation displaced the fixed profile");
                Assert(Control<StackPanel>(w, "MetricsRow").Children.OfType<TextBlock>().Select(t => t.Name)
                    .SequenceEqual(new[] { "VersionText", "MemoryText", "CpuText" })
                    && SaVisuals<Button>(metrics).Select(button => button.Name).Order()
                        .SequenceEqual(new[] { "ChangelogButton", "FooterUpdateButton", "GithubButton" }), "relocation changed the metrics footer contents");
                readback.Add(new { width, height, signedIn, sidebar = sidebarRect, profile = profileRect, mini = Sp016Rect(mini, root),
                    settings = Sp016Rect(settings, root), player = playerRect, metrics = metricsRect, navigationViewport = nav.ViewportHeight,
                    navigationScrollableHeight = nav.ScrollableHeight, lastNavigationAction = Sp016Rect(Control<Button>(w, "QueueHistoryNav"), nav) });
                nav.ScrollToTop(); w.UpdateLayout();
                App.Capture(w, Path.Combine(_directory, $"sidebar016-{(signedIn ? "profile" : "signedout")}-{width}x{height}.png"));
            }
        }
        Assert(!f.Player.HasSource && !f.Handler.Seen.Any(r => r.Uri.AbsolutePath.Contains("download", StringComparison.OrdinalIgnoreCase)),
            "sidebar layout check opened or downloaded audio");
        Sp016Write("sidebar016-sidebar-readback.json", readback);
    }

    private static async Task Sp016Player()
    {
        using var f = new SaFixture("sidebar016-player"); await f.Activate(); var w = f.Window;
        var track = new TrackRow { Id = "sidebar016-track", RequestId = "sidebar016-track:1", AlbumId = "1", Available = true,
            Title = string.Concat(Enumerable.Repeat("Длинное название трека ", 12)), Album = "Локальный альбом",
            Artist = "Первый исполнитель, Второй исполнитель с длинным именем", DurationMs = 240000,
            Artists = [new("sidebar016-first", "Первый исполнитель"), new("sidebar016-second", "Второй исполнитель с длинным именем")] };
        typeof(MainWindow).GetField("_current", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(w, track);
        SaField<PlaybackQueue>(w, "_playbackQueue").SetItems([track], track);
        Control<TextBlock>(w, "TrackTitle").Text = track.Title; Control<TextBlock>(w, "TrackArtist").Text = track.Artist;
        Control<TextBlock>(w, "QualityText").Text = "Офлайн · MP3 · 320 Кбит/с";
        SaInvoke(w, "UpdateControls");
        var readback = new List<object>();
        foreach (var (width, height) in Sp016Sizes)
        {
            await Sp016Resize(w, width, height);
            var root = Control<Grid>(w, "LayoutRoot"); var sidebar = Control<Border>(w, "Sidebar"); var player = Control<Border>(w, "PlayerBar");
            var panels = new FrameworkElement[] { Control<Grid>(w, "NowPlayingPanel"), Control<StackPanel>(w, "TransportControls"),
                Control<Grid>(w, "AudioControls"), Control<Grid>(w, "TimelinePanel") };
            foreach (var panel in panels) Sp016Within(panel, player);
            Sp016Separate(panels, root);
            Assert(Sp016Rect(player, root).Left >= Sp016Rect(sidebar, root).Right - 1
                && Sp016Rect(player, root).Top >= Sp016Rect(Control<Grid>(w, "ContentGrid"), root).Bottom - 1,
                "player escaped its main-content column or covered the collection");
            var trackControls = new FrameworkElement[] { Control<Button>(w, "PlayerCoverButton"), Control<Button>(w, "PlayerTitleButton"),
                Control<DockPanel>(w, "PlayerArtistPanel"), Control<Button>(w, "PlayerLikeButton") };
            foreach (var control in trackControls) Sp016Within(control, panels[0]);
            Sp016Separate(trackControls, root);
            Assert(Control<TextBlock>(w, "TrackTitle").ActualWidth >= 80 && Control<DockPanel>(w, "PlayerArtistPanel").IsVisible
                && Control<TextBlock>(w, "TrackTitle").TextTrimming == TextTrimming.CharacterEllipsis,
                "narrow player lost readable, trimmed track metadata");
            var transport = new[] { "ShuffleButton", "PreviousButton", "PlayPauseButton", "NextButton", "RepeatButton" }
                .Select(name => Control<Button>(w, name)).ToArray();
            foreach (var button in transport) { Sp016Within(button, panels[1]); Assert(button.IsVisible, "missing transport action " + button.Name); }
            Sp016Separate(transport, root);
            var audio = new FrameworkElement[] { Control<Button>(w, "PlayerLyricsButton"), Control<Button>(w, "ExpandPlayerButton"),
                Control<TextBlock>(w, "QualityText"), Control<Slider>(w, "VolumeSlider") };
            foreach (var element in audio) Sp016Within(element, panels[2]);
            Sp016Separate(audio, root);
            Assert(Control<Slider>(w, "VolumeSlider").ActualWidth >= 90 && Control<Slider>(w, "SeekSlider").ActualWidth >= 500,
                "relocated player squeezed the volume or timeline controls");
            var timeline = new FrameworkElement[] { Control<TextBlock>(w, "PositionText"), Control<Grid>(w, "TimelineSeekHost"), Control<TextBlock>(w, "DurationText") };
            foreach (var element in timeline) Sp016Within(element, panels[3]);
            Sp016Separate(timeline, root);
            readback.Add(new { width, height, player = Sp016Rect(player, root), panels = panels.Select(p => new { p.Name, bounds = Sp016Rect(p, root) }),
                trackTitleWidth = Control<TextBlock>(w, "TrackTitle").ActualWidth, volumeWidth = Control<Slider>(w, "VolumeSlider").ActualWidth,
                seekWidth = Control<Slider>(w, "SeekSlider").ActualWidth, transport = transport.Select(b => new { b.Name, bounds = Sp016Rect(b, root) }) });
            App.Capture(w, Path.Combine(_directory, $"sidebar016-player-{width}x{height}.png"));
        }
        Assert(!f.Player.HasSource && !f.Handler.Seen.Any(r => r.Uri.AbsolutePath.Contains("download", StringComparison.OrdinalIgnoreCase)),
            "player geometry check requested media");
        Sp016Write("sidebar016-player-readback.json", readback);
    }

    private static async Task Sp016Resize(MainWindow w, int width, int height)
    { w.Width = width; w.Height = height; w.UpdateLayout(); await Dispatcher.Yield(DispatcherPriority.Render); await Task.Delay(120); w.UpdateLayout(); }
    private static Rect Sp016Rect(FrameworkElement element, Visual ancestor) => element.TransformToAncestor(ancestor).TransformBounds(new Rect(element.RenderSize));
    private static void Sp016Within(FrameworkElement element, FrameworkElement ancestor)
    {
        var bounds = Sp016Rect(element, ancestor);
        Assert(bounds.Width > 0 && bounds.Height > 0 && bounds.Left >= -1 && bounds.Top >= -1
            && bounds.Right <= ancestor.ActualWidth + 1 && bounds.Bottom <= ancestor.ActualHeight + 1,
            $"{element.Name} is outside {ancestor.Name}: {bounds}; host={ancestor.ActualWidth:0.##}x{ancestor.ActualHeight:0.##}");
    }
    private static void Sp016Separate(IReadOnlyList<FrameworkElement> elements, Visual ancestor)
    {
        for (var i = 0; i < elements.Count; i++) for (var j = i + 1; j < elements.Count; j++)
        {
            var overlap = Rect.Intersect(Sp016Rect(elements[i], ancestor), Sp016Rect(elements[j], ancestor));
            Assert(overlap.IsEmpty || overlap.Width <= 1 || overlap.Height <= 1, $"{elements[i].Name} overlaps {elements[j].Name}: {overlap}");
        }
    }
    private static void Sp016Write(string name, object readback) => File.WriteAllText(Path.Combine(_directory, name), JsonSerializer.Serialize(new
    { sizes = readback, realAccountUsed = false, restoredSessionUsed = false, mediaRequests = 0, externalNetworkRequests = 0,
        checkedAssemblySha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(MainWindow).Assembly.Location))) }, new JsonSerializerOptions { WriteIndented = true }));
}
