using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using YMusic.Native;

internal static partial class Program
{
    private static async Task CheckBulkUnavailableLikes()
    {
        await Run("Bulk unlike: unavailable selector, sorted multi-selection and right-click preserve exact targets", async () =>
        {
            using var f = new UlFixture("bulk-selection"); await f.Start();
            Assert(f.List.SelectionMode == SelectionMode.Extended, "downloads do not support Ctrl/Shift selection");
            SaClick(BulkSelectButton(f));
            Assert(f.List.SelectedItems.Cast<DownloadEntry>().Select(e => e.Track.Id).ToHashSet().SetEquals(["ul-dead", "ul-other"])
                && f.Unlike.Content.ToString()!.Contains("2") && f.Writes.IsEmpty, "selector included an available track or sent a write");
            SaInvoke(f.Window, "ChangeListSort", new ListSortRule(ListSortField.Title, true)); f.Window.UpdateLayout();
            var dead = f.Queue.Entries.Single(e => e.Track.Id == "ul-dead");
            f.List.ScrollIntoView(dead); f.Window.UpdateLayout();
            SaInvoke(f.Window, "SelectDownloadContextRow", f.List.ItemContainerGenerator.ContainerFromItem(dead));
            Assert(f.List.SelectedItems.Count == 2 && f.Menu.Header.ToString()!.Contains("2"), "right-click on a selected item collapsed the group");
            var failed = f.Queue.Entries.Single(e => e.Track.Id == "ul-error"); f.List.ScrollIntoView(failed); f.Window.UpdateLayout();
            SaInvoke(f.Window, "SelectDownloadContextRow", f.List.ItemContainerGenerator.ContainerFromItem(failed));
            Assert(f.List.SelectedItems.Count == 1 && ReferenceEquals(f.List.SelectedItem, failed), "right-click on another row retained unrelated targets");
            SaInvoke(f.Window, "SelectDownloadContextRow", (object)null!);
            Assert(f.List.SelectedItems.Count == 0 && !f.Unlike.IsEnabled, "empty space retained a destructive selection");
            SaClick(BulkSelectButton(f));
            await BulkCapture(f, "selected");
        });
        await Run("Bulk unlike: one click uses an immutable deduplicated snapshot and confirms each result without touching audio", async () =>
        {
            using var f = new UlFixture("bulk-success"); await f.Start();
            var duplicate = new DownloadEntry(UlRow("ul-dead", false)); f.Queue.Entries.Add(duplicate);
            SaClick(BulkSelectButton(f)); Assert(f.List.SelectedItems.Count == 3, "duplicate fixture is not selected");
            var files = f.FileHashes(); var resolves = f.ResolveRequests; f.HoldWrite = true;
            var task = Call(f.Window, "UnlikeSelectedDownloadAsync"); await f.WriteEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert(!f.Unlike.IsEnabled && !f.Retry.IsEnabled && !BulkSelectButton(f).IsEnabled
                && f.Queue.Entries.All(e => !e.RemovedFromCollection), "pending batch shows optimistic success or allows a second mutation");
            f.List.SelectedItems.Clear(); f.List.SelectedItem = f.Queue.Entries.Single(e => e.Track.Id == "ul-error");
            await Call(f.Window, "UnlikeSelectedDownloadAsync"); Assert(f.Writes.Count == 1, "double click sent another request");
            f.WriteRelease.TrySetResult(); await task;
            Assert(f.Writes.Count == 2 && f.Writes.ToHashSet().SetEquals(["ul-dead", "ul-other"]), "selection change or duplicate changed the request snapshot");
            Assert(f.Queue.Entries.Where(e => e.Track.Id != "ul-error").All(e => e.RemovedFromCollection && e.SkipFavoritesDownload)
                && SaField<HashSet<string>>(f.Window, "_favoriteTrackIds").SetEquals(["ul-error"]), "confirmed removals were not applied to all matching rows");
            Assert(f.MetadataRequests == 0 && f.AudioRequests == 0 && f.ResolveRequests == resolves && f.FileHashes() == files,
                "bulk unlike refreshed metadata, requested audio or changed downloaded files");
            Assert(!SaField<bool>(f.Window, "_catalogWriting") && f.Unlike.IsEnabled && f.Retry.IsEnabled, "batch did not restore actions for the remaining selection");
            await BulkCapture(f, "confirmed");
        });
        await Run("Bulk unlike: a keyboard menu snapshots the focused row before selection changes", async () =>
        {
            using var f = new UlFixture("bulk-keyboard-menu");
            await f.Start();
            var focused = f.Queue.Entries.Single(entry => entry.Track.Id == "ul-error");
            SaClick(BulkSelectButton(f));
            f.List.ScrollIntoView(focused);
            f.Window.UpdateLayout();
            Ta0317RaiseOpening((ListViewItem)f.List.ItemContainerGenerator.ContainerFromItem(focused));
            var menu = f.List.ContextMenu!;
            Ta0317Open(menu);
            Assert(f.Menu.IsEnabled && ReferenceEquals(f.List.SelectedItem, focused), "the keyboard menu ignored the focused download");
            SaClick(BulkSelectButton(f));
            f.Menu.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            await Until(() => f.Writes.Count == 1 && !SaField<bool>(f.Window, "_catalogWriting"));
            Assert(f.Writes.Single() == focused.Track.Id, "unlike used a selection made after opening the menu");
            menu.RaiseEvent(new RoutedEventArgs(ContextMenu.ClosedEvent));
        });
        await Run("Bulk unlike: a partial failure preserves confirmed removals and stops before further writes", async () =>
        {
            foreach (var failure in new[] { "http", "revision", "readback" })
            {
                using var f = new UlFixture("bulk-partial-" + failure); await f.Start();
                foreach (var entry in f.Queue.Entries) f.List.SelectedItems.Add(entry);
                f.Failure = failure; f.FailureAtWrite = 2; var files = f.FileHashes();
                await Call(f.Window, "UnlikeSelectedDownloadAsync");
                Assert(f.Writes.SequenceEqual(["ul-dead", "ul-error"]) && f.Queue.Entries[0].RemovedFromCollection
                    && f.Queue.Entries.Skip(1).All(e => !e.RemovedFromCollection), "partial failure retried, continued or rolled back confirmed work");
                Assert(SaField<HashSet<string>>(f.Window, "_favoriteTrackIds").SetEquals(["ul-error", "ul-other"])
                    && Control<TextBlock>(f.Window, "StatusText").Text.Contains("1 из 3"), "partial result count or favorite state is incorrect");
                Assert(f.Unlike.IsEnabled && f.Unlike.Content.ToString()!.Contains("2") && f.FileHashes() == files,
                    "failed entries cannot be retried explicitly or local files changed");
            }
        });
        await Run("Bulk unlike: changing accounts cancels the rest of a batch and ignores a late response", async () =>
        {
            using var f = new UlFixture("bulk-signout"); await f.Start(); SaClick(BulkSelectButton(f)); f.HoldWrite = true;
            var task = Call(f.Window, "UnlikeSelectedDownloadAsync"); await f.WriteEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            f.Window.SignOut(); UlSet(f.Window, "_account", new Account("67890", "Other fixture", true)); UlSet(f.Window, "_session", FakeSession);
            var likes = SaField<HashSet<string>>(f.Window, "_favoriteTrackIds"); likes.Add("other-account-track"); UlSet(f.Window, "_favoriteIdsKnown", true);
            f.WriteRelease.TrySetResult(); await task;
            Assert(f.Writes.Count == 1 && likes.SetEquals(["other-account-track"]) && f.Queue.Entries.All(e => !e.RemovedFromCollection)
                && !f.Unlike.IsEnabled && !SaField<bool>(f.Window, "_catalogWriting"), "late batch response changed a different account or continued writing");
        });
        await Run("Bulk unlike: active and already unliked selections are skipped without removing unrelated favorites", async () =>
        {
            using var f = new UlFixture("bulk-eligibility", includeUnselectedFavorite: true); await f.Start();
            var active = f.Queue.Entries.Single(e => e.Track.Id == "ul-other"); active.SetState(DownloadEntryState.Downloading);
            var unselected = f.Queue.Entries.Single(e => e.Track.Id == "ul-unselected");
            var alreadyUnliked = f.Queue.Entries.Single(e => e.Track.Id == "ul-error");
            f.RemoveServerLike(alreadyUnliked.Track.Id);
            var likes = SaField<HashSet<string>>(f.Window, "_favoriteTrackIds"); likes.Remove(alreadyUnliked.Track.Id);
            f.List.SelectedItems.Clear();
            foreach (var entry in f.Queue.Entries.Where(entry => !ReferenceEquals(entry, unselected))) f.List.SelectedItems.Add(entry);
            Assert(f.List.SelectedItems.Count == 3 && !f.List.SelectedItems.Contains(unselected), "fixture did not leave an unselected favorite");
            await Call(f.Window, "UnlikeSelectedDownloadAsync");
            Assert(f.Writes.SequenceEqual(["ul-dead"]) && !active.RemovedFromCollection && !unselected.RemovedFromCollection
                && f.IsLiked(active.Track.Id) && f.IsLiked(unselected.Track.Id)
                && likes.SetEquals([active.Track.Id, unselected.Track.Id]),
                "batch included an active/non-favorite track or removed an unselected favorite");
            Assert(alreadyUnliked.RemovedFromCollection && alreadyUnliked.SkipFavoritesDownload,
                "verified membership did not exclude an already unliked failed favorite from retry");
        });
        await Run("Client polish014: footer shows version RAM CPU in order while diagnostics stay in Settings", async () =>
        {
            using var f = new UlFixture("polish-footer"); await f.Start(); SaInvoke(f.Window, "UpdateMetrics");
            var footer = Control<StackPanel>(f.Window, "MetricsRow");
            Assert(footer.Children.OfType<TextBlock>().Select(t => t.Name).SequenceEqual(["VersionText", "MemoryText", "CpuText"])
                && Control<TextBlock>(f.Window, "VersionText").Text == AppBranding.VersionLabel
                && SaVisuals<Button>(Control<Border>(f.Window, "MetricsBar")).Select(button => button.Name).Order()
                    .SequenceEqual(new[] { "ChangelogButton", "FooterUpdateButton", "GithubButton" }), "footer retains extra labels/actions");
            SaInvoke(f.Window, "Settings_Click", null!, null!); f.Window.UpdateLayout();
            Assert(SaVisuals<TextBlock>(Control<ContentControl>(f.Window, "SettingsUpdates")).Any(t => t.Text.Contains("Версия " + AppBranding.VersionLabel[1..]))
                && SaVisuals<Button>(Control<Grid>(f.Window, "SettingsPanel")).Any(b => b.Name == "SaveMetricsButton"), "version or diagnostics action missing from Settings");
            Assert(((SolidColorBrush)f.Window.FindResource("AccentBrush")).Color == Color.FromRgb(231, 187, 89), "accent does not match the supplied swatch");
            var scroll = SaVisuals<ScrollViewer>(Control<Grid>(f.Window, "SettingsPanel")).First(); scroll.ScrollToEnd();
            await Task.Delay(200); App.Capture(f.Window, Path.Combine(_directory, "polish014-settings.png"));
        });
        await Run("Client polish014: minimize stays visible, close-to-tray hides, restore and explicit Exit retain their roles", () =>
        {
            foreach (var closeToTray in new[] { false, true })
            {
                var owner = new Window { Width = 240, Height = 100, Left = -16000, Top = 0, ShowActivated = false, ShowInTaskbar = false };
                var closed = false; owner.Closed += (_, _) => closed = true; owner.Show();
                using var shell = new DesktopShell(owner);
                try
                {
                    typeof(DesktopShell).GetField("_minimizeToTray", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(shell, closeToTray);
                    typeof(DesktopShell).GetField("<IsAvailable>k__BackingField", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(shell, true);
                    owner.WindowState = WindowState.Minimized;
                    Assert(owner.IsVisible && !closed && owner.WindowState == WindowState.Minimized, "ordinary minimize hid the window in the tray");
                    shell.ShowMainWindow(); Assert(owner.IsVisible && owner.WindowState == WindowState.Normal, "restore lost the previous window state");
                    owner.Close();
                    Assert(closeToTray ? !closed && !owner.IsVisible : closed, "close-to-tray preference did not apply only to closing");
                    if (closeToTray) { shell.ShowMainWindow(); Assert(owner.IsVisible, "tray restore failed"); shell.RequestExit(); Assert(closed, "explicit Exit was intercepted by close-to-tray"); }
                }
                finally { if (!closed) shell.RequestExit(); }
            }
            return Task.CompletedTask;
        });
        await Run("Client polish014: an unavailable tray restores the window and registers again on TaskbarCreated", () =>
        {
            var owner = new Window { Width = 240, Height = 100, Left = -16000, ShowActivated = false, ShowInTaskbar = false };
            owner.Show();
            var canAdd = false;
            var canModify = true;
            var addCount = 0;
            using var shell = new DesktopShell(owner, message =>
            {
                if (message == 1) return canModify;
                if (message != 0) return true;
                addCount++;
                return canAdd;
            }) { MinimizeToTray = true };
            try
            {
                Assert(shell.MinimizeToTray && !shell.IsAvailable && owner.IsVisible, "failed initial registration lost the preference or hid the owner");
                canAdd = true;
                TaskbarCreated();
                Assert(shell.IsAvailable && addCount == 2, "first TaskbarCreated did not register a requested tray");
                var iconData = typeof(DesktopShell).GetMethod("CreateIconData", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(shell, null)!;
                var flags = (uint)iconData.GetType().GetField("Flags")!.GetValue(iconData)!;
                Assert((flags & 0x80) != 0, "version 4 tray icon omitted NIF_SHOWTIP");
                owner.Close();
                Assert(!owner.IsVisible, "working tray did not allow close-to-tray");
                canModify = false;
                shell.Update("Fixture song", "Fixture artist", true, false, false, canPlay: true);
                Assert(!shell.IsAvailable && shell.MinimizeToTray && owner.IsVisible, "failed modification lost the preference or left the window hidden");
                canAdd = false;
                TaskbarCreated();
                Assert(!shell.IsAvailable && shell.MinimizeToTray, "failed recreation lost the preference");
                canAdd = true;
                TaskbarCreated();
                Assert(shell.IsAvailable && addCount == 4, "later TaskbarCreated did not recover the tray");
            }
            finally { shell.RequestExit(); }
            return Task.CompletedTask;

            void TaskbarCreated()
            {
                const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
                var message = (uint)typeof(DesktopShell).GetField("_taskbarCreated", flags)!.GetValue(shell)!;
                Assert(message != 0, "could not register the TaskbarCreated message");
                typeof(DesktopShell).GetMethod("WindowProcedure", flags)!.Invoke(shell, [shell.WindowHandle, (int)message, (nint)0, (nint)0, false]);
            }
        });
        await Run("Client polish014: a version 4 tray context menu returns focus after Cancel and playback actions", () =>
        {
            var owner = new Window { Width = 240, Height = 100, Left = -16000, ShowActivated = false, ShowInTaskbar = false };
            owner.Show();
            var operations = new List<uint>();
            var actions = new List<ShellAction>();
            Guid icon = default;
            (int X, int Y)? menu = null;
            uint selection = 0;
            using var shell = new DesktopShell(owner, message => { operations.Add(message); return true; })
            {
                TrayMenuAnchorForChecks = guid => { icon = guid; return (-400, 925); },
                TrackTrayMenuForChecks = (x, y) => { menu = (x, y); return selection; },
                MinimizeToTray = true
            };
            try
            {
                shell.Command += actions.Add;
                shell.Update("Fixture track", "Fixture artist", true, canNext: true, canPrevious: true, canPlay: true);
                owner.Close();
                Assert(!owner.IsVisible, "fixture did not hide the main window");
                object[] callback = [shell.WindowHandle, 0x8000 + 51, (nint)(-1), (nint)(0x10000 | 0x7b), false];
                var dispatch = typeof(DesktopShell).GetMethod("WindowProcedure", BindingFlags.Instance | BindingFlags.NonPublic)!;
                dispatch.Invoke(shell, callback);
                Assert((bool)callback[^1] && icon == shell.IconGuid && menu is (-400, 925)
                    && operations.SequenceEqual(new uint[] { 0, 4, 1, 3 }) && !owner.IsVisible,
                    "keyboard tray menu used the pointer, lost focus, or opened the owner on Cancel");
                for (selection = 3; selection <= 5; selection++) dispatch.Invoke(shell, callback);
                Assert(actions.SequenceEqual(new[] { ShellAction.PlayPause, ShellAction.Next, ShellAction.Previous })
                    && operations.Skip(3).SequenceEqual(new uint[] { 3, 3, 3, 3 }) && !owner.IsVisible,
                    "playback commands from the tray did not return focus without opening an app window");
                selection = 3;
                shell.Update("", "", false, false, false, canPlay: true);
                dispatch.Invoke(shell, callback);
                Assert(actions.Count == 4 && actions[^1] == ShellAction.PlayPause, "tray did not start ready playback from an empty player");
                shell.Update("", "", false, false, false, canPlay: false);
                dispatch.Invoke(shell, callback);
                shell.Update("Opening fixture", "", false, false, false, canPlay: false);
                dispatch.Invoke(shell, callback);
                Assert(actions.Count == 4, "tray dispatched Play while the main command was disabled");
            }
            finally { shell.RequestExit(); }
            return Task.CompletedTask;
        });
    }

    private static Button BulkSelectButton(UlFixture f) => SaVisuals<Button>(f.Window.DownloadCenterView).Single(b => b.Name == "DownloadSelectUnavailableButton");
    private static async Task BulkCapture(UlFixture f, string name)
    {
        foreach (var (width, height) in new[] { (1280, 860), (1050, 680) })
        {
            f.Window.Width = width; f.Window.Height = height; f.Window.UpdateLayout(); await Task.Delay(180);
            var buttons = SaVisuals<Button>(f.Window.DownloadCenterView).Where(b => b.Name.StartsWith("Download", StringComparison.Ordinal)).ToArray();
            Assert(buttons.All(b => b.Height == 32 && b.ActualHeight == 32 && b.IsVisible), "compact download actions clipped or changed height");
            App.Capture(f.Window, Path.Combine(_directory, $"bulk-unavailable-{name}-{width}.png"));
        }
    }
}
