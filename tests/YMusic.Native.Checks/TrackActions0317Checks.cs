using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using YMusic.Native;

internal static partial class Program
{
    private static async Task CheckTrackActions0317()
    {
        await Run("TrackActions0317: list and player expose the same useful vector actions and current like state", () =>
        {
            using var f = new Library017Fixture("actions-identical"); var track = f.Row("shared", "album-a");
            SaInvoke(f.Window, "ShowRows", (object)new[] { track }); Control<ListView>(f.Window, "TracksList").SelectedItem = track;
            Library017Set(f.Window, "_current", track);
            var list = Control<ListView>(f.Window, "TracksList").ContextMenu!; Ta0317Open(list);
            var player = Ta0317Menu(f.Window, Control<Button>(f.Window, "PlayerMoreButton"), () => track); Ta0317Open(player);
            var names = new[] { "TrackActionLike", "TrackActionWave", "TrackActionNext", "TrackActionEnqueue", "TrackActionPlaylist", "TrackActionDownload", "TrackActionRemoveOffline", "TrackActionAlbum", "TrackActionArtists", "TrackActionLink" };
            Assert(list.Items.OfType<MenuItem>().Select(item => item.Name).SequenceEqual(names)
                && list.Items.OfType<MenuItem>().Select(item => (item.Name, item.Header, item.IsEnabled))
                    .SequenceEqual(player.Items.OfType<MenuItem>().Select(item => (item.Name, item.Header, item.IsEnabled))),
                "list and player have different action order or authority for the same track");
            Assert(player.Items.OfType<MenuItem>().All(item => item.Icon is System.Windows.Shapes.Path { Data: not null })
                && (string)Ta0317Item(player, "Like").Header == "Убрать из Мне нравится", "actions lost icons or the account's existing like");
            foreach (var key in new[] { "Download", "RemoveOffline" })
                Assert(((System.Windows.Shapes.Path)Ta0317Item(player, key).Icon).Stroke == f.Window.FindResource("DownloadBrush"), "offline action lost its green glyph");
            player.Width = 340; player.Measure(new Size(340, double.PositiveInfinity)); player.Arrange(new Rect(new Point(), player.DesiredSize)); player.UpdateLayout();
            var bitmap = new RenderTargetBitmap(340, (int)Math.Ceiling(player.ActualHeight), 96, 96, PixelFormats.Pbgra32); bitmap.Render(player);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var output = File.Create(Path.Combine(_directory, "track-actions0317-shared-menu.png")); encoder.Save(output);
            return Task.CompletedTask;
        });
        await Run("TrackActions0317: unavailable liked track targets its captured row rather than the newer selection", async () =>
        {
            using var f = new Library017Fixture("actions-unavailable");
            var unavailable = new TrackRow { Id = "shared", RequestId = "shared:album-a", AlbumId = "album-a", Title = "Недоступен", Available = false };
            var other = f.Row("other", "album-b"); SaInvoke(f.Window, "ShowRows", (object)new[] { unavailable, other });
            var list = Control<ListView>(f.Window, "TracksList"); list.SelectedItem = unavailable;
            var menu = list.ContextMenu!; Ta0317Open(menu);
            Assert(Ta0317Item(menu, "Like").IsEnabled && !Ta0317Item(menu, "Next").IsEnabled, "unavailable rows lost unlike or gained playback");
            list.SelectedItem = other;
            Ta0317Item(menu, "Like").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            await Until(() => f.Writes.Count == 1 && !SaField<bool>(f.Window, "_catalogWriting"));
            Assert(f.Writes.Single() == ("remove", "shared:album-a") && !SaField<HashSet<string>>(f.Window, "_favoriteTrackIds").Contains("shared"),
                "menu removed the new selection's like rather than the unavailable captured row");
        });
        await Run("TrackActions0317: queue actions retain the opened player target and closed or stale menus have no authority", () =>
        {
            using var f = new Library017Fixture("actions-queue"); var a = f.Row("101", "1"); var b = f.Row("102", "2");
            var current = a; var menu = Ta0317Menu(f.Window, Control<Button>(f.Window, "PlayerMoreButton"), () => current);
            Ta0317Open(menu); current = b; Ta0317Item(menu, "Next").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            var queue = SaField<PlaybackQueue>(f.Window, "_playbackQueue");
            Assert(queue.Items.Count == 1 && ReferenceEquals(queue.Items[0], a), "a changing player redirected an already opened menu");
            menu.RaiseEvent(new RoutedEventArgs(ContextMenu.ClosedEvent)); Ta0317Item(menu, "Enqueue").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Assert(queue.Items.Count == 1, "closed menu still changed the queue");
            Ta0317Open(menu); Library017Set(f.Window, "_accountGeneration", SaField<int>(f.Window, "_accountGeneration") + 1);
            Ta0317Item(menu, "Enqueue").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Assert(queue.Items.Count == 1, "old account menu still changed the queue");
            return Task.CompletedTask;
        });
        await Run("TrackActions0317: share copies only the captured public canonical track URL without touching the clipboard fixture", () =>
        {
            using var f = new Library017Fixture("actions-share"); var copied = new List<string>();
            Library017Set(f.Window, "_trackLinkCopier", new Action<string>(copied.Add));
            var current = f.Row("1234", "91"); var menu = Ta0317Menu(f.Window, Control<Button>(f.Window, "PlayerMoreButton"), () => current);
            Ta0317Open(menu); current = f.Row("5678", "92"); Ta0317Item(menu, "Link").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Assert(copied.SequenceEqual(new[] { "https://music.yandex.ru/track/1234" }), "share leaked a credential, composite id, or the newer track");
            current = f.Row("1234:91", "91"); Ta0317Open(menu);
            Assert(!Ta0317Item(menu, "Link").IsEnabled, "invalid canonical track identity produced a public link");
            return Task.CompletedTask;
        });
        await Run("TrackActions0317: remove offline uses its exact player target while another track is selected", async () =>
        {
            using var f = new Library017Fixture("actions-remove"); await f.SaveShared();
            var shared = f.Row("shared", "album-a"); var other = f.Row("other", "album-b");
            SaInvoke(f.Window, "ShowRows", (object)new[] { shared, other }); SaInvoke(f.Window, "RefreshDownloadedMarks");
            Control<ListView>(f.Window, "TracksList").SelectedItem = other;
            var menu = Ta0317Menu(f.Window, Control<Button>(f.Window, "PlayerMoreButton"), () => shared); Ta0317Open(menu);
            Assert(Ta0317Item(menu, "RemoveOffline").IsEnabled, "saved track did not offer removal");
            Ta0317Item(menu, "RemoveOffline").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            await Until(() => !SaField<bool>(f.Window, "_offlineMaintenance") && f.Library.Find(shared) is null);
            Assert(f.Library.Read().Count == 0 && f.MediaRequests == 1, "exact local deletion triggered new audio or retained its index");
        });
        await Run("TrackActions0317: source queue and metadata actions fit the minimum player and list hover affordance remains available", async () =>
        {
            using var f = new Pc0317Fixture("actions-layout"); var rows = f.Rows(4); f.Playlist("origin", rows); await f.Play(rows[0]);
            var bar = Control<Border>(f.Window, "PlayerBar"); var parent = (Panel)bar.Parent; var index = parent.Children.IndexOf(bar);
            parent.Children.Remove(bar); var host = new Border { Child = bar };
            try
            {
                foreach (var width in new[] { 1050d, 1280d })
                {
                    host.Measure(new Size(width - 220, 118)); host.Arrange(new Rect(0, 0, width - 220, 118)); host.UpdateLayout();
                    var buttons = new[] { "PlayerMoreButton", "PlayerLikeButton", "PlaybackContextButton", "PlayerQueueButton", "PlayerLyricsButton", "ExpandPlayerButton" }
                        .Select(name => Control<Button>(f.Window, name)).ToArray();
                    foreach (var button in buttons) { var bounds = Sp016Rect(button, bar); Assert(bounds.Width >= 20 && bounds.Height > 0 && bounds.Left >= -1 && bounds.Top >= -1 && bounds.Right <= bar.ActualWidth + 1 && bounds.Bottom <= bar.ActualHeight + 1, button.Name + " is clipped"); }
                    Sp016Separate(buttons, bar);
                    var bitmap = new RenderTargetBitmap((int)(width - 220), 118, 96, 96, PixelFormats.Pbgra32); bitmap.Render(bar);
                    var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using var output = File.Create(Path.Combine(_directory, $"track-actions0317-player-{width}.png")); encoder.Save(output);
                }
            }
            finally { host.Child = null; parent.Children.Insert(index, bar); }
            var listParent = (Panel)f.List.Parent; var listIndex = listParent.Children.IndexOf(f.List); listParent.Children.Remove(f.List);
            var listHost = new Border { Child = f.List };
            try
            {
                listHost.Measure(new Size(900, 400)); listHost.Arrange(new Rect(0, 0, 900, 400)); listHost.UpdateLayout();
                var row = (ListViewItem)f.List.ItemContainerGenerator.ContainerFromItem(rows[0]);
                Assert(row is not null && SaVisuals<Button>(row).Any(button => ReferenceEquals(button.Tag, rows[0]) && button.Style == f.Window.FindResource("TrackMoreButton")),
                    "realized track row has no compact action button");
            }
            finally { listHost.Child = null; listParent.Children.Insert(listIndex, f.List); }
        });
    }
    private static ContextMenu Ta0317Menu(MainWindow window, FrameworkElement anchor, Func<TrackRow?> track)
        => (ContextMenu)typeof(MainWindow).GetMethod("CreateTrackActionsMenu", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [anchor, track, false])!;
    private static void Ta0317Open(ContextMenu menu) => menu.RaiseEvent(new RoutedEventArgs(ContextMenu.OpenedEvent));
    private static MenuItem Ta0317Item(ContextMenu menu, string action) => menu.Items.OfType<MenuItem>().Single(item => item.Name == "TrackAction" + action);
}
