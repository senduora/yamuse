using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using YMusic.Native;

internal static partial class Program
{
    private static async Task CheckNavigation014()
    {
        await Run("Navigation014: mouse Back and Forward restore playlist filter, selection, scroll and album header without changing playback", async () =>
        {
            using var f = new SaFixture("mouse-navigation"); await f.Activate(); var w = f.Window;
            var rows = Enumerable.Range(0, 60).Select(i => new TrackRow { Id = "nav-" + i, RequestId = "nav-" + i + ":same-title-b", Title = "Трек " + i,
                AlbumId = "same-title-b", Album = "Альбом", Available = true }).ToArray();
            SaInvoke(w, "ShowPlaylist", new PlaylistDetails("owner", "nav-list", "Исходный плейлист", "", 1, rows));
            Control<TextBox>(w, "FilterBox").Text = "Трек"; await Task.Delay(300);
            var list = Control<ListView>(w, "TracksList"); list.SelectedItem = rows[12]; list.ScrollIntoView(rows[12]); w.UpdateLayout();
            var scroll = SaVisuals<ScrollViewer>(list).First(); var offset = scroll.VerticalOffset;
            var queue = SaField<PlaybackQueue>(w, "_playbackQueue"); queue.SetItems([rows[2], rows[3]], rows[2]); var version = queue.Version;
            await Call(w, "OpenCatalogAsync", new CatalogItem("same-title-b", "album", "Контрольный альбом", ""), true);
            NavMouse(w, MouseButton.XButton1); await Task.Delay(80); w.UpdateLayout();
            Assert(SaField<string>(w, "_section") == "playlist-detail" && ReferenceEquals(list.SelectedItem, rows[12])
                && Control<TextBox>(w, "FilterBox").Text == "Трек" && Math.Abs(scroll.VerticalOffset - offset) < 1, "mouse Back lost the playlist state");
            NavMouse(w, MouseButton.XButton2); await Task.Delay(80); w.UpdateLayout();
            Assert(SaField<string>(w, "_section") == "album-detail" && Control<ContentControl>(w, "CollectionHero").IsVisible
                && SaField<AlbumDetails?>(w, "_activeAlbumDetails")?.Id == "same-title-b", "mouse Forward lost the album metadata/header");
            Assert(queue.Version == version && ReferenceEquals(queue.Current, rows[2]) && !f.Player.HasSource, "history navigation changed the playback queue or opened audio");
            NavMouse(w, MouseButton.XButton1); SaClick(Control<Button>(w, "FavoritesNav")); NavMouse(w, MouseButton.XButton2);
            Assert(SaField<string>(w, "_section") == "favorites", "a new navigation did not invalidate Forward history");
            w.SignOut(); var section = SaField<string>(w, "_section"); NavMouse(w, MouseButton.XButton1); NavMouse(w, MouseButton.XButton2);
            Assert(SaField<string>(w, "_section") == section && NavHistoryCount(w, "_browseHistory") == 0 && NavHistoryCount(w, "_browseForward") == 0,
                "history exposed the previous account after signout");
        });
        await Run("Navigation014: mouse handling leaves ordinary buttons alone and Back retains live Settings changes", async () =>
        {
            using var f = new SaFixture("mouse-overlay"); await f.Activate(); var w = f.Window;
            var left = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = Mouse.PreviewMouseDownEvent };
            w.RaiseEvent(left); Assert(!left.Handled, "history swallowed an ordinary mouse button");
            var crossfade = f.Player.CrossfadeSeconds; SaInvoke(w, "Settings_Click", null!, null!); Control<Slider>(w, "CrossfadeSetting").Value = 9.5;
            NavMouse(w, MouseButton.XButton1);
            Assert(!SaField<bool>(w, "_settingsOpen") && f.Player.CrossfadeSeconds == 9.5, "mouse Back rolled back live Settings");
            for (var i = 0; i < 30; i++) SaInvoke(w, "SetSection", i % 2 == 0 ? "favorites" : "offline", "Раздел " + i);
            Assert(NavHistoryCount(w, "_browseHistory") == 24, "navigation history is unbounded or clears all entries at the limit");
        });
        await Run("Navigation014: list title and album links plus player links open the exact album without playing audio", async () =>
        {
            foreach (var origin in new[] { "list-title", "list-album", "player-title", "player-cover" })
            {
                using var f = new SaFixture("album-affordance-" + origin); await f.Activate(); var w = f.Window;
                var track = new TrackRow { Id = "nav-track", RequestId = "nav-track:same-title-b", Title = "Точное название", Album = "Точный альбом", AlbumId = "same-title-b", Available = true };
                SaInvoke(w, "ShowPlaylist", new PlaylistDetails("owner", "source", "Плейлист", "", 1, [track]));
                UlSet(w, "_current", track); SaInvoke(w, "UpdateControls"); w.UpdateLayout();
                var queue = SaField<PlaybackQueue>(w, "_playbackQueue"); queue.SetItems([track], track); var version = queue.Version;
                Button button;
                if (origin.StartsWith("player", StringComparison.Ordinal)) button = Control<Button>(w, origin.EndsWith("title", StringComparison.Ordinal) ? "PlayerTitleButton" : "PlayerCoverButton");
                else
                {
                    var list = Control<ListView>(w, "TracksList"); list.ScrollIntoView(track); w.UpdateLayout();
                    var row = (ListViewItem)list.ItemContainerGenerator.ContainerFromItem(track)!;
                    button = SaVisuals<Button>(row).Single(b => ReferenceEquals(b.Tag, track) && (origin.EndsWith("title", StringComparison.Ordinal)
                        ? SaVisuals<TextBlock>(b).Any(t => t.Text == track.Title) : SaVisuals<TextBlock>(b).Any(t => t.Text == track.Album)));
                }
                SaClick(button); await Until(() => SaField<string>(w, "_section") == "album-detail" && SaField<CancellationTokenSource?>(w, "_featureCts") is null);
                Assert(f.Handler.Seen.Any(c => c.Uri.AbsolutePath == "/albums/same-title-b/with-tracks") && !f.Handler.Seen.Any(c => c.Uri.AbsolutePath == "/search"), "album link guessed a title instead of using the exact ID");
                Assert(queue.Version == version && ReferenceEquals(queue.Current, track) && !f.Player.HasSource, "an album affordance started audio or changed the queue");
            }
        });
    }

    private static int NavHistoryCount(MainWindow window, string field)
    {
        var stack = typeof(MainWindow).GetField(field, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!;
        return (int)stack.GetType().GetProperty("Count")!.GetValue(stack)!;
    }
    private static void NavMouse(MainWindow window, MouseButton button)
    {
        var args = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, button) { RoutedEvent = Mouse.PreviewMouseDownEvent };
        window.RaiseEvent(args); Assert(args.Handled, "side mouse button was not handled by the real window route");
    }
}
