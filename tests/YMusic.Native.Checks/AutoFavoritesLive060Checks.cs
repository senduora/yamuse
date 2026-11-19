using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using YMusic.Native;

internal static partial class Program
{
    private static async Task CheckAutoFavoritesLive060()
    {
        await CheckSingleRemovalAuto061();
        await Run("AutoFavoritesReenable061: confirmed clear then enabling the checkbox downloads the ready collection without manual action", async () =>
        {
            using var f = new Af060Fixture("clear-reenable-ready");
            await f.Like(Af060Row("101")); await f.Like(Af060Row("102"));
            f.RefreshReleased.TrySetResult();
            await Until(() => f.Library.Read().Count == 2 && SaField<bool>(f.Window, "_favoritesSnapshotReady")
                && SaField<CancellationTokenSource?>(f.Window, "_downloadCts") is null);
            await Call(f.Window, "ClearOfflineAfterConfirmationAsync");
            Assert(f.Library.Read().Count == 0 && !Control<CheckBox>(f.Window, "AutoFavoritesCheckBox").IsChecked.GetValueOrDefault()
                && f.Queue.Entries.All(entry => entry.State == DownloadEntryState.Removed), "clear retained audio or enabled automatic downloads");
            var before = f.Downloads.Values.Sum(); await Task.Delay(100);
            Assert(f.Downloads.Values.Sum() == before, "clear redownloaded favorites before explicit enable");
            Control<CheckBox>(f.Window, "AutoFavoritesCheckBox").IsChecked = true;
            await Until(() => f.Library.Read().Count == 2 && SaField<CancellationTokenSource?>(f.Window, "_downloadCts") is null);
            Assert(f.Downloads.GetValueOrDefault("101") == 2 && f.Downloads.GetValueOrDefault("102") == 2,
                "enabling after clear did not redownload each missing favorite exactly once");
            Control<CheckBox>(f.Window, "AutoFavoritesCheckBox").IsChecked = false;
            Control<CheckBox>(f.Window, "AutoFavoritesCheckBox").IsChecked = true;
            await Until(() => SaField<CancellationTokenSource?>(f.Window, "_downloadCts") is null);
            Assert(f.Downloads.Values.Sum() == before + 2, "re-enabling redownloaded existing offline files");
        });
        await Run("AutoFavoritesReenable061: enabling after clear waits for the held collection then downloads it automatically", async () =>
        {
            using var f = new Af060Fixture("clear-reenable-refresh");
            await f.Like(Af060Row("101")); await f.RefreshEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Until(() => f.Library.Read().Count == 1 && SaField<CancellationTokenSource?>(f.Window, "_downloadCts") is null);
            await Call(f.Window, "ClearOfflineAfterConfirmationAsync");
            Control<CheckBox>(f.Window, "AutoFavoritesCheckBox").IsChecked = true; await Task.Delay(100);
            Assert(f.Library.Read().Count == 0 && f.Downloads.GetValueOrDefault("101") == 1
                && !SaField<bool>(f.Window, "_favoritesSnapshotReady"), "incomplete collection was downloaded before refresh");
            f.RefreshReleased.TrySetResult();
            await Until(() => f.Library.Read().Count == 1 && SaField<CancellationTokenSource?>(f.Window, "_downloadCts") is null);
            Assert(f.Downloads.GetValueOrDefault("101") == 2, "completed refresh did not start downloads after clear and enable");
        });
        await Run("AutoFavoritesReenable061: clear cancels active work and enabling starts only the current favorite snapshot", async () =>
        {
            using var f = new Af060Fixture("clear-reenable-active", "900");
            var manual = f.StartManual(Af060Row("900")); await f.MediaEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await f.Like(Af060Row("101")); f.RefreshReleased.TrySetResult();
            await Until(() => SaField<bool>(f.Window, "_favoritesSnapshotReady"));
            await Call(f.Window, "ClearOfflineAfterConfirmationAsync"); await manual;
            Assert(f.Library.Read().Count == 0 && f.Downloads.GetValueOrDefault("101") == 0
                && SaField<Dictionary<string, TrackRow>>(f.Window, "_pendingConfirmedFavorites").Count == 0,
                "clear retained audio or started a pending favorite");
            Control<CheckBox>(f.Window, "AutoFavoritesCheckBox").IsChecked = true;
            await Until(() => f.Library.Read().Count == 1 && SaField<CancellationTokenSource?>(f.Window, "_downloadCts") is null);
            Assert(f.Library.Read().Single().Id == "101" && f.Downloads.GetValueOrDefault("101") == 1
                && f.Downloads.GetValueOrDefault("900") == 1, "enable lost the favorite or resumed canceled manual work");
        });
        await Run("AutoFavoritesReenable061: enabling from settings during clear starts favorites after cleanup", async () =>
        {
            using var f = new Af060Fixture("clear-enable-in-settings", "900") { HoldMediaCancellation = true };
            Control<CheckBox>(f.Window, "AutoFavoritesCheckBox").IsChecked = false;
            await f.Like(Af060Row("101"));
            f.RefreshReleased.TrySetResult();
            await Af061DownloadsIdle(f);
            var manual = f.StartManual(Af060Row("900"));
            await f.MediaEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            SaInvoke(f.Window, "Settings_Click", null!, null!);

            var clear = Call(f.Window, "ClearOfflineAfterConfirmationAsync");
            await f.MediaCancellationEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Control<CheckBox>(f.Window, "SettingsAutoFavorites").IsChecked = true;
            Assert(f.Downloads.GetValueOrDefault("101") == 0, "cleanup admitted a favorite before its workers drained");
            f.MediaCancellationReleased.TrySetResult();
            await clear.WaitAsync(TimeSpan.FromSeconds(5));
            await manual;
            await Af061DownloadsIdle(f);

            Assert(f.Library.Read().SingleOrDefault()?.Id == "101" && f.Downloads.GetValueOrDefault("101") == 1
                && f.Downloads.GetValueOrDefault("900") == 1, "the enable action was lost during clear or resumed the canceled batch");
        });
        await Run("AutoFavoritesReenable061: a confirmed like during clear survives cancellation without waiting for the full refresh", async () =>
        {
            using var f = new Af060Fixture("clear-confirmed-like-with-held-refresh", "900") { HoldMediaCancellation = true };
            Control<CheckBox>(f.Window, "AutoFavoritesCheckBox").IsChecked = false;
            var manual = f.StartManual(Af060Row("900"));
            await f.MediaEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            SaInvoke(f.Window, "Settings_Click", null!, null!);
            var clear = Call(f.Window, "ClearOfflineAfterConfirmationAsync");
            await f.MediaCancellationEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Control<CheckBox>(f.Window, "SettingsAutoFavorites").IsChecked = true;
            await f.Like(Af060Row("101"));
            await f.RefreshEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var admission = SaField<long>(f.Window, "_downloadQueueAdmissionRevision");
            await Call(f.Window, "DownloadSelectedAsync");
            Assert(admission == SaField<long>(f.Window, "_downloadQueueAdmissionRevision") && f.Downloads.GetValueOrDefault("101") == 0,
                "the disabled cancel action invalidated the clear or allowed a premature download");

            f.MediaCancellationReleased.TrySetResult();
            await clear.WaitAsync(TimeSpan.FromSeconds(5)); await manual;
            await Until(() => f.Library.Read().Any(item => item.Id == "101") && SaField<Task?>(f.Window, "_downloadTask") is null);
            Assert(!f.RefreshReleased.Task.IsCompleted && f.Library.Read().Count == 1
                && f.Downloads.GetValueOrDefault("101") == 1 && f.Downloads.GetValueOrDefault("900") == 1,
                "cleanup lost the staged like, waited for the full refresh or restarted canceled manual audio");
        });
        await Run("AutoFavoritesReenable061: clear preserves offline music when the preference or queue cannot be saved", async () =>
        {
            foreach (var lockSettings in new[] { true, false })
            {
                using var f = new Af060Fixture("clear-busy-profile-" + lockSettings);
                var preferences = new PreferencesStore(Path.Combine(f.DirectoryPath, "preferences"));
                var diagnostics = Path.Combine(f.DirectoryPath, "diagnostics");
                var store = new DownloadQueueStore(diagnostics);
                UlSet(f.Window, "_downloadQueueStore", store);
                await f.StartManual(Af060Row("900"));
                var queuePath = Path.Combine(diagnostics, DownloadQueueStore.FileName);
                var checkpoint = File.ReadAllBytes(queuePath);
                using (File.Open(lockSettings ? preferences.FilePath : queuePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                    await Call(f.Window, "ClearOfflineAfterConfirmationAsync");

                Assert(f.Library.Read().Single().Id == "900" && File.ReadAllBytes(queuePath).SequenceEqual(checkpoint)
                    && preferences.Load().AutoDownloadFavorites == lockSettings
                    && Control<CheckBox>(f.Window, "AutoFavoritesCheckBox").IsChecked == lockSettings
                    && Control<TextBlock>(f.Window, "MaintenanceStatus").Text.Contains("Очистка отменена", StringComparison.Ordinal),
                    "failed clear removed offline music or left the preference out of sync");
            }
        });
        await Run("AutoFavoritesLive060: restored completed and empty history permits a new player like without resuming old work", async () =>
        {
            foreach (var completed in new[] { true, false })
            {
                using var f = new Af060Fixture("restored-finished-" + completed);
                var previous = Af060Row("900");
                if (completed) await f.StartManual(previous);
                var rows = completed ? new[] { previous } : Array.Empty<TrackRow>();
                f.Queue.Replace(rows, "История автоскачивания", favoritesBatch: true);
                if (completed) f.Queue.Entries.Single().Complete(f.Library.Read().Single().Bytes);
                var directory = Path.Combine(_directory, "auto-favorites-restore-finished-" + completed);
                var store = new DownloadQueueStore(directory);
                store.Save(DownloadQueueStore.Capture("12345", f.Queue, AudioQuality.Excellent, true,
                    new PlaylistDetails("local:favorites", "liked", "Мне нравится", "", 0, rows)));
                UlSet(f.Window, "_downloadQueueStore", store);
                var before = f.Downloads.Values.Sum();
                await Call(f.Window, "RestoreDownloadQueueAsync", "12345");
                Assert(!f.Queue.CanResume && !f.Queue.CanRetry && !SaField<bool>(f.Window, "_downloadsAutoSuspended")
                    && f.Downloads.Values.Sum() == before && !f.Queue.Notice.Contains("на паузе", StringComparison.OrdinalIgnoreCase),
                    "finished restored history blocks new downloads or pretends it is paused");
                var added = Af060Row("101"); UlSet(f.Window, "_current", added); SaInvoke(f.Window, "UpdatePlayerLikeState");
                await Call(f.Window, "ToggleCurrentLikeAsync");
                await Until(() => f.Library.Read().Any(item => item.Id == added.Id));
                Assert(f.Downloads.GetValueOrDefault("101") == 1 && f.Downloads.GetValueOrDefault("900") == (completed ? 1 : 0)
                    && !f.RefreshReleased.Task.IsCompleted, "restored history postponed a player like or restarted finished audio");
            }
        });
        await Run("AutoFavoritesLive060: restored pauses wait for Continue and restored failures allow independent favorites", async () =>
        {
            foreach (var state in new[] { DownloadEntryState.Paused, DownloadEntryState.Failed })
            {
                using var f = new Af060Fixture("restored-held-" + state); var previous = Af060Row("900");
                Control<CheckBox>(f.Window, "AutoFavoritesCheckBox").IsChecked = false;
                await f.Like(previous);
                f.Queue.Replace([previous], "Восстановленная очередь", favoritesBatch: true);
                f.Queue.Entries.Single().SetState(state);
                var directory = Path.Combine(_directory, "auto-favorites-restore-held-" + state); var store = new DownloadQueueStore(directory);
                store.Save(DownloadQueueStore.Capture("12345", f.Queue, AudioQuality.Excellent, true,
                    new PlaylistDetails("local:favorites", "liked", "Мне нравится", "", 0, [previous])));
                UlSet(f.Window, "_downloadQueueStore", store);
                await Call(f.Window, "RestoreDownloadQueueAsync", "12345");
                var paused = state == DownloadEntryState.Paused;
                Assert(f.Queue.HasPaused == paused && f.Queue.CanResume == paused
                    && f.Queue.CanRetry == !paused, "restore changed the explicit Continue or Retry requirements");
                Control<CheckBox>(f.Window, "AutoFavoritesCheckBox").IsChecked = true;
                await f.Like(Af060Row("101"));
                if (paused)
                {
                    Assert(f.Downloads.Count == 0 && f.Queue.Entries.Single().Track.Id == "900", "new like replaced or resumed paused work");
                }
                else
                {
                    await Until(() => f.Library.Read().Any(item => item.Id == "101") && SaField<Task?>(f.Window, "_downloadTask") is null);
                    Assert(!f.RefreshReleased.Task.IsCompleted && f.Downloads.GetValueOrDefault("101") == 1
                        && f.Downloads.GetValueOrDefault("900") == 0 && f.Queue.CanRetry,
                        "the new like waited for the collection refresh or retried the old failure");
                }
            }
        });
        await Run("AutoFavoritesLive060: pausing or disabling appended favorites keeps a valid checkpoint and full offline playlist", async () =>
        {
            foreach (var disabling in new[] { false, true })
            {
                using var f = new Af060Fixture("appended-favorites-checkpoint-" + disabling) { FailMediaTrack = "900", HeldFollowUpMedia = "101" };
                var first = Af060Row("900"); var second = Af060Row("101"); var third = Af060Row("102");
                Control<CheckBox>(f.Window, "AutoFavoritesCheckBox").IsChecked = false;
                foreach (var row in new[] { first, second, third }) await f.Like(row);
                f.RefreshReleased.TrySetResult(); await Af061DownloadsIdle(f);
                UlSet(f.Window, "_concurrentDownloads", 1);
                var store = new DownloadQueueStore(Path.Combine(f.DirectoryPath, "diagnostics"));
                UlSet(f.Window, "_downloadQueueStore", store);
                var playlist = new PlaylistDetails("local:favorites", "liked", "Мне нравится", "", 0, [first]);
                await Call(f.Window, "StartDownloadBatchAsync", new[] { first }, playlist.Title, playlist, false);

                Control<CheckBox>(f.Window, "AutoFavoritesCheckBox").IsChecked = true;
                await f.FollowUpMediaEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                var active = SaField<Task>(f.Window, "_downloadTask");
                if (disabling) Control<CheckBox>(f.Window, "AutoFavoritesCheckBox").IsChecked = false;
                else SaInvoke(f.Window, "PauseDownloadQueue");
                await active.WaitAsync(TimeSpan.FromSeconds(5));
                SaInvoke(f.Window, "TrySaveDownloadQueueCheckpoint");
                var checkpoint = store.Load("12345");
                Assert(checkpoint?.Playlist is { HistoryOnly: true } context && context.TrackIds.SequenceEqual(["900"])
                    && checkpoint.Entries.Select(entry => entry.Track.Id).SequenceEqual(["900", "101"]),
                    "discarding an unstarted favorite left a broken playlist checkpoint");

                f.FailMediaTrack = null;
                await Call(f.Window, "RetryFailedDownloads");
                Assert(f.Library.GetPlaylists().Single().Tracks.Select(track => track.Id).ToHashSet().SetEquals(["900", "101", "102"]),
                    "Retry replaced the full offline favorites with its historical queue membership");
            }
        });
        await Run("AutoFavoritesLive060: disabled auto trims a restored queue context without changing saved playlist membership", async () =>
        {
            using var f = new Af060Fixture("restore-discarded-favorites-checkpoint");
            Control<CheckBox>(f.Window, "AutoFavoritesCheckBox").IsChecked = false;
            var first = Af060Row("900"); var second = Af060Row("101");
            var playlist = new PlaylistDetails("local:favorites", "liked", "Мне нравится", "", 0, [first, second]);
            await f.Library.RememberPlaylistAsync(playlist, AudioQuality.Excellent, default);
            var queue = new DownloadQueueState(); queue.Replace([first], playlist.Title, favoritesBatch: true);
            queue.Entries.Single().SetState(DownloadEntryState.Failed);
            queue.AppendAutomaticFavorites([second], AudioQuality.Excellent); queue.Pause();
            var store = new DownloadQueueStore(Path.Combine(f.DirectoryPath, "diagnostics"));
            store.Save(DownloadQueueStore.Capture("12345", queue, AudioQuality.Excellent, false, playlist));
            UlSet(f.Window, "_downloadQueueStore", store);

            await Call(f.Window, "RestoreDownloadQueueAsync", "12345");
            SaInvoke(f.Window, "TrySaveDownloadQueueCheckpoint");
            var checkpoint = store.Load("12345");
            Assert(checkpoint?.Playlist is { HistoryOnly: true } context && context.TrackIds.SequenceEqual(["900"])
                && checkpoint.Entries.Single().Track.Id == "900" && f.Queue.CanRetry,
                "disabled auto could not checkpoint the restored queue after dropping a pending favorite");
            await Call(f.Window, "RetryFailedDownloads");
            Assert(f.Library.GetPlaylists().Single().Tracks.Select(track => track.Id).SequenceEqual(["900", "101"])
                && f.Downloads.GetValueOrDefault("101") == 0, "Retry rewrote full favorites or started a discarded entry");
        });
        await Run("AutoFavoritesLive060: confirmed player like downloads before the held collection refresh completes", async () =>
        {
            using var f = new Af060Fixture("immediate"); var row = Af060Row("101");
            UlSet(f.Window, "_current", row); SaInvoke(f.Window, "UpdatePlayerLikeState");
            await Call(f.Window, "ToggleCurrentLikeAsync"); await f.RefreshEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Until(() => f.Library.Read().Any(item => item.Id == row.Id));
            Assert(!f.RefreshReleased.Task.IsCompleted && f.Downloads.GetValueOrDefault(row.Id) == 1 && row.IsLiked,
                "confirmed like waited for full favorites refresh or downloaded twice");
            Assert(f.Queue.FavoritesBatch && SaField<PlaylistDetails?>(f.Window, "_downloadQueuePlaylist") is null,
                "incremental like lost unlike guards or overwrote the complete favorites snapshot");
        });
        await Run("AutoFavoritesLive060: busy manual queue retains every new like across refresh replacement and unlike removes pending work", async () =>
        {
            using var f = new Af060Fixture("pending", "900");
            var manual = f.StartManual(Af060Row("900")); await f.MediaEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var first = Af060Row("101"); var removed = Af060Row("102");
            await f.Like(first); await f.Like(removed); await f.Like(first); await f.Like(removed, false);
            Assert(f.Queue.Entries.Single().Track.Id == "900" && f.Downloads.GetValueOrDefault("101") == 0,
                "confirmed favorite replaced or ran alongside the active manual batch");
            f.MediaReleased.TrySetResult(); await manual;
            await Until(() => f.Library.Read().Count == 2 && SaField<CancellationTokenSource?>(f.Window, "_downloadCts") is null);
            Assert(f.Downloads.GetValueOrDefault("900") == 1 && f.Downloads.GetValueOrDefault("101") == 1
                && f.Downloads.GetValueOrDefault("102") == 0 && f.Library.Read().All(item => item.Id != "102"),
                "pending likes were lost, duplicated or kept after unlike");
        });
        await Run("AutoFavoritesLive060: likes wait for a held restore and do not replace paused work", async () =>
        {
            using var f = new Af060Fixture("held-restore-like");
            var store = new DownloadQueueStore(Path.Combine(f.DirectoryPath, "diagnostics"));
            var saved = new DownloadQueueState();
            saved.Replace([Af060Row("900")], "Ручная очередь");
            store.Save(DownloadQueueStore.Capture("12345", saved, AudioQuality.Excellent, false, null));
            UlSet(f.Window, "_downloadQueueStore", store);
            var read = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            f.Window.DownloadQueueRestoreReadAsync = async uid => { await read.Task; return store.Load(uid); };
            var restoring = Call(f.Window, "RestoreDownloadQueueAsync", "12345");
            try
            {
                await f.Like(Af060Row("101"));
                f.RefreshReleased.TrySetResult();
                await Until(() => SaField<CancellationTokenSource?>(f.Window, "_libraryCts") is null);
                Assert(f.Downloads.Count == 0 && store.Load("12345")?.Entries.Single().Track.Id == "900",
                    "automatic work superseded the held restoration");
            }
            finally { read.TrySetResult(); }
            await restoring;
            Assert(f.Queue.CanResume && f.Queue.Entries.Single().Track.Id == "900" && f.Downloads.Count == 0,
                "the confirmed like replaced a restored pause");
            await Call(f.Window, "ResumeDownloadQueue");
            await Af061DownloadsIdle(f);
            Assert(f.Downloads.GetValueOrDefault("900") == 1 && f.Downloads.GetValueOrDefault("101") == 1,
                "explicit continuation lost the prior queue or the confirmed like");
        });
        await Run("AutoFavoritesLive060: update pause checkpoints confirmed likes for resume, reopen or disable", async () =>
        {
            foreach (var action in new[] { "resume", "reopen", "disable", "disable-active" })
            {
                var name = "update-pause-like-" + action;
                DownloadQueueStore store;
                using (var f = new Af060Fixture(name, "900"))
                {
                    store = new DownloadQueueStore(Path.Combine(f.DirectoryPath, "diagnostics"));
                    UlSet(f.Window, "_downloadQueueStore", store);
                    var manual = f.StartManual(Af060Row("900"));
                    await f.MediaEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    await f.Like(Af060Row("101"));
                    SaInvoke(f.Window, "PauseDownloadQueueForAppUpdate");
                    await manual.WaitAsync(TimeSpan.FromSeconds(5));
                    SaInvoke(f.Window, "TrySaveDownloadQueueCheckpoint");
                    var checkpoint = store.Load("12345");
                    Assert(checkpoint is { Entries.Count: 2 } && checkpoint.Entries.All(entry => entry.State == DownloadEntryState.Paused)
                        && checkpoint.Entries.Single(entry => entry.Track.Id == "101").AutomaticFavoriteQuality is not null
                        && f.Downloads.GetValueOrDefault("101") == 0, "update pause lost or started a pending confirmed like");
                    if (action != "reopen")
                    {
                        if (action == "disable") Control<CheckBox>(f.Window, "AutoFavoritesCheckBox").IsChecked = false;
                        if (action == "disable-active") f.HeldFollowUpMedia = "101";
                        f.MediaReleased.TrySetResult();
                        var continued = Call(f.Window, "ResumeDownloadQueue");
                        if (action == "disable-active")
                        {
                            await f.FollowUpMediaEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                            Control<CheckBox>(f.Window, "AutoFavoritesCheckBox").IsChecked = false;
                        }
                        await continued.WaitAsync(TimeSpan.FromSeconds(5));
                        Assert(f.Downloads.GetValueOrDefault("900") == 2 && f.Downloads.GetValueOrDefault("101") == (action == "disable" ? 0 : 1),
                            "continuation or disabling did not honor the paused confirmed like");
                        if (action == "disable-active")
                            Assert(f.Library.Read().Single().Id == "900" && f.Queue.Entries.Single(entry => entry.Track.Id == "101").State == DownloadEntryState.Paused,
                                "disabling during a mixed run did not pause only the automatic favorite");
                    }
                }
                if (action != "reopen") continue;
                using var reopened = new Af060Fixture(name);
                UlSet(reopened.Window, "_downloadQueueStore", store);
                await Call(reopened.Window, "RestoreDownloadQueueAsync", "12345");
                Assert(reopened.Queue.CanResume && reopened.Downloads.Count == 0, "restored pending work started without Continue");
                await Call(reopened.Window, "ResumeDownloadQueue");
                Assert(reopened.Downloads.GetValueOrDefault("900") == 1 && reopened.Downloads.GetValueOrDefault("101") == 1,
                    "the reopened queue lost the pending confirmed like");
            }
        });
        await Run("AutoFavoritesLive060: a failed transfer leaves unrelated confirmed favorites queued without retrying the failure", async () =>
        {
            foreach (var refreshBeforeFailure in new[] { true, false })
            {
                using var f = new Af060Fixture("failed-transfer-with-confirmed-like-" + refreshBeforeFailure, "900") { FailMediaTrack = "900" };
                var failing = Af060Row("900");
                Control<CheckBox>(f.Window, "AutoFavoritesCheckBox").IsChecked = false;
                await f.Like(failing);
                await f.Like(Af060Row("102"));
                await f.RefreshEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Control<CheckBox>(f.Window, "AutoFavoritesCheckBox").IsChecked = true;

                var manual = Call(f.Window, "StartDownloadBatchAsync", new[] { failing }, "Ручная загрузка",
                    new PlaylistDetails("12345", "91", "Ручной плейлист", "", 1, [failing]), false);
                await f.MediaEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await f.Like(Af060Row("101"));
                if (refreshBeforeFailure)
                {
                    f.RefreshReleased.TrySetResult();
                    await Until(() => SaField<CancellationTokenSource?>(f.Window, "_libraryCts") is null);
                }
                f.MediaReleased.TrySetResult();
                await manual.WaitAsync(TimeSpan.FromSeconds(5));
                if (!refreshBeforeFailure)
                {
                    await Until(() => f.Downloads.GetValueOrDefault("101") == 1);
                    f.RefreshReleased.TrySetResult();
                }
                await Af061DownloadsIdle(f);

                Assert(f.Library.Read().Select(item => item.Id).ToHashSet().SetEquals(["101", "102"])
                    && f.Downloads.GetValueOrDefault("101") == 1 && f.Downloads.GetValueOrDefault("102") == 1
                    && f.Downloads.GetValueOrDefault("900") == 1 && f.Queue.CanRetry
                    && f.Queue.Entries.Single(entry => entry.Track.Id == "900").State == DownloadEntryState.Failed,
                    "a failed transfer lost another favorite, retried automatically, or disappeared from Retry");
                Assert(f.Library.GetPlaylists().Single(playlist => playlist.Owner == "local:favorites")
                    .Tracks.Select(track => track.Id).ToHashSet().SetEquals(["900", "101", "102"]),
                    "the full favorites snapshot was lost while retaining the manual queue");

                f.FailMediaTrack = null;
                await Call(f.Window, "RetryFailedDownloads");
                Assert(f.Library.Read().Count == 3 && f.Downloads.GetValueOrDefault("900") == 2
                    && f.Downloads.GetValueOrDefault("101") == 1 && f.Downloads.GetValueOrDefault("102") == 1
                    && f.Library.GetPlaylists().Single(playlist => playlist.Owner == "12345").Tracks.Single().Id == "900",
                    "explicit Retry did not select only the original failed track");
            }
        });
        await Run("AutoFavoritesLive060: disabled preference and existing offline files suppress new network transfers", async () =>
        {
            using var f = new Af060Fixture("disabled-existing"); var existing = Af060Row("101");
            await f.StartManual(existing); await f.Like(existing);
            await Until(() => SaField<CancellationTokenSource?>(f.Window, "_downloadCts") is null);
            Assert(f.Downloads.GetValueOrDefault("101") == 1 && f.Resolves.GetValueOrDefault("101") == 1,
                "new like redownloaded or probed the existing offline file");
            Control<CheckBox>(f.Window, "AutoFavoritesCheckBox").IsChecked = false;
            await f.Like(Af060Row("102")); await Task.Delay(100);
            Assert(f.Downloads.GetValueOrDefault("102") == 0, "disabled preference still downloaded a new like");
            Control<CheckBox>(f.Window, "AutoFavoritesCheckBox").IsChecked = true;
            await f.Like(Af060Row("103")); await Until(() => f.Library.Read().Any(item => item.Id == "103"));
            Assert(f.Downloads.GetValueOrDefault("103") == 1, "new like did not download after re-enabling the preference");
        });
        await Run("AutoFavoritesLive060: pause disable and signout discard pending confirmed likes without automatic resume", async () =>
        {
            foreach (var action in new[] { "pause", "disable", "logout" })
            {
                using var f = new Af060Fixture("lifecycle-" + action, "900");
                var manual = f.StartManual(Af060Row("900")); await f.MediaEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await f.Like(Af060Row("101"));
                if (action == "pause") SaInvoke(f.Window, "PauseDownloadQueue");
                if (action == "disable") Control<CheckBox>(f.Window, "AutoFavoritesCheckBox").IsChecked = false;
                if (action == "logout") f.Window.SignOut();
                f.MediaReleased.TrySetResult(); await manual; await Task.Delay(100);
                Assert(f.Downloads.GetValueOrDefault("101") == 0 && SaField<Dictionary<string, TrackRow>>(f.Window, "_pendingConfirmedFavorites").Count == 0,
                    "pending favorite survived " + action);
                if (action == "pause")
                {
                    await f.Like(Af060Row("102")); await Task.Delay(100);
                    Assert(f.Downloads.GetValueOrDefault("102") == 0, "new like resumed a paused download queue");
                }
            }
        });
        await Run("AutoFavoritesLive060: failed and unconfirmed likes do not schedule audio", async () =>
        {
            foreach (var confirmedFailure in new[] { false, true })
            {
                using var f = new Af060Fixture("failed-like-" + confirmedFailure) { FailWrite = !confirmedFailure, FailReadback = confirmedFailure };
                await f.Like(Af060Row("101")); await Task.Delay(100);
                Assert(f.Downloads.Count == 0 && f.Resolves.Count == 0 && !f.RefreshEntered.Task.IsCompleted,
                    "failed or unconfirmed mutation scheduled favorites refresh/audio");
            }
        });
        await Run("AutoFavoritesLive060: completed collection refresh reuses the immediately downloaded track", async () =>
        {
            using var f = new Af060Fixture("refresh-dedup");
            await f.Like(Af060Row("101")); await Until(() => f.Library.Read().Count == 1);
            f.RefreshReleased.TrySetResult();
            await Until(() => SaField<CancellationTokenSource?>(f.Window, "_libraryCts") is null
                && SaField<CancellationTokenSource?>(f.Window, "_downloadCts") is null);
            Assert(f.Downloads.GetValueOrDefault("101") == 1 && f.Resolves.GetValueOrDefault("101") == 1
                && SaField<List<TrackRow>>(f.Window, "_favoritesCache").Single().Id == "101", "full refresh downloaded the new favorite again");
        });
        await Run("AutoFavoritesLive060: unavailable favorites stay explicit and a failed transfer does not retry automatically", async () =>
        {
            using (var f = new Af060Fixture("unavailable"))
            {
                await f.Like(Af060Row("101", available: false));
                await Until(() => f.Queue.Entries.Count == 1 && SaField<CancellationTokenSource?>(f.Window, "_downloadCts") is null);
                Assert(f.Queue.Entries.Single().State == DownloadEntryState.Unavailable && f.Downloads.Count == 0, "unavailable favorite attempted audio");
            }
            using (var f = new Af060Fixture("failed-transfer") { FailAudio = true })
            {
                await f.Like(Af060Row("101"));
                await Until(() => f.Queue.Entries.Count == 1 && SaField<CancellationTokenSource?>(f.Window, "_downloadCts") is null);
                SaInvoke(f.Window, "TryStartPendingAutoFavorites"); await Task.Delay(150);
                Assert(f.Queue.Entries.Single().State == DownloadEntryState.Failed && f.Downloads.GetValueOrDefault("101") == 1,
                    "failed immediate download was hidden or retried automatically");
            }
        });
        await Run("AutoFavoritesLive060: new favorite metadata reopens missing or unavailable history without retrying another failure", async () =>
        {
            using var f = new Af060Fixture("reuse-auto-favorites-history") { FailMediaTrack = "900" };
            var failing = Af060Row("900"); var outdated = Af060Row("101", available: false);
            var missing = Af060Row("102"); var cached = Af060Row("103");
            var tracks = new[] { failing, outdated, missing, cached };
            var playlist = new PlaylistDetails("12345", "91", "Ручной плейлист", "", 1, tracks);
            var store = new DownloadQueueStore(Path.Combine(f.DirectoryPath, "diagnostics"));
            UlSet(f.Window, "_downloadQueueStore", store);
            await Call(f.Window, "StartDownloadBatchAsync", tracks, playlist.Title, playlist, false);
            File.Delete(f.Library.ResolvePath(f.Library.Find(missing)!.FileName));

            foreach (var track in new[] { Af060Row("101"), missing, cached })
            {
                await f.Like(track);
                await Until(() => SaField<Task?>(f.Window, "_downloadTask") is null);
            }
            Assert(!f.RefreshReleased.Task.IsCompleted && f.Downloads.GetValueOrDefault("900") == 1 && f.Queue.CanRetry
                && f.Downloads.GetValueOrDefault("101") == 1 && f.Downloads.GetValueOrDefault("102") == 2
                && f.Downloads.GetValueOrDefault("103") == 1 && f.Library.Read().Count == 3,
                "automatic reuse skipped newly available audio, retried a failure or fetched a valid cached file again");
            var checkpoint = store.Load("12345");
            Assert(checkpoint?.Playlist is { } context && context.TrackIds.SequenceEqual(tracks.Select(track => track.Id))
                && checkpoint.Entries.Count == tracks.Length, "reopening a queue row changed its playlist membership or duplicated an ID");
        });
        await Run("AutoFavoritesLive060: a busy completed favorite does not fail independent new work", async () =>
        {
            using var f = new Af060Fixture("busy-completed-favorite") { FailMediaTrack = "900" };
            var old = Af060Row("101"); var next = Af060Row("102");
            var auto = Control<CheckBox>(f.Window, "AutoFavoritesCheckBox");
            auto.IsChecked = false;
            await f.Like(old); await f.Like(next);
            await f.StartManual(old, Af060Row("900"));
            var existing = f.Library.ResolvePath(f.Library.Find(old)!.FileName);
            using (File.Open(existing, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                auto.IsChecked = true;
                f.RefreshReleased.TrySetResult(); await f.Window.RefreshLibraryAsync(refreshMetadata: true);
                await Af061DownloadsIdle(f);
                Assert(f.Library.Find(next) is not null && f.Queue.Entries.Single(entry => entry.Track.Id == "101").State == DownloadEntryState.Completed
                    && f.Queue.CanRetry && f.Downloads.GetValueOrDefault("101") == 1 && f.Downloads.GetValueOrDefault("102") == 1
                    && f.Downloads.GetValueOrDefault("900") == 1,
                    "a temporarily unreadable completed favorite aborted new work or retried an older row");
            }
        });
        await Run("AutoFavoritesLive060: idle and automatic work cannot replace another owner's checkpoint on close or apply", async () =>
        {
            foreach (var mode in new[] { "idle", "snapshot", "confirmed", "staged" })
            {
                using var f = new Af060Fixture("foreign-checkpoint-automatic-" + mode);
                var directory = Path.Combine(f.DirectoryPath, "diagnostics");
                var store = new DownloadQueueStore(directory);
                UlSet(f.Window, "_downloadQueueStore", store);
                store.Save(DrSnapshot("previous"));
                var path = Path.Combine(directory, DownloadQueueStore.FileName);
                var original = File.ReadAllBytes(path);
                await Call(f.Window, "RestoreDownloadQueueAsync", "12345");
                var track = Af060Row("101");
                if (mode == "snapshot")
                {
                    f.AddServerFavorite(track.Id);
                    f.RefreshReleased.TrySetResult();
                    await f.Window.RefreshLibraryAsync(refreshMetadata: true);
                    await Af061DownloadsIdle(f);
                }
                else if (mode == "confirmed")
                {
                    await f.Like(track);
                    await Until(() => f.Downloads.ContainsKey(track.Id));
                    if (SaField<Task?>(f.Window, "_downloadTask") is { } download)
                        await download.WaitAsync(TimeSpan.FromSeconds(10));
                }
                else if (mode == "staged")
                {
                    SaField<Dictionary<string, TrackRow>>(f.Window, "_pendingConfirmedFavorites")[track.Id] = track;
                    SaInvoke(f.Window, "PauseDownloadsForClose");
                    Assert(f.Queue.CanResume && !SaField<bool>(f.Window, "_downloadQueueAutomatic"),
                        "fixture did not stage a confirmed like into an otherwise nonautomatic queue");
                }
                Assert(f.Downloads.GetValueOrDefault(track.Id) == (mode is "snapshot" or "confirmed" ? 1 : 0)
                    && File.ReadAllBytes(path).SequenceEqual(original), "automatic work changed the previous account's checkpoint");
                Assert((SaInvokeResult(f.Window, "SaveStateBeforeClosing") is null) == (mode != "staged")
                    && File.ReadAllBytes(path).SequenceEqual(original), "closing lost staged work or overwrote the protected checkpoint");
                var backend = new Su016Backend { PendingVersion = "9.1.0" };
                var updates = Su016Service(f.Window, "foreign-checkpoint-automatic-" + mode, backend);
                await updates.CheckAsync();
                Assert(await (Task<bool>)Call(f.Window, "ApplyAppUpdateAsync", updates) == (mode != "staged")
                    && backend.Applies == (mode == "staged" ? 0 : 1)
                    && File.ReadAllBytes(path).SequenceEqual(original), "applying overwrote the previous account's checkpoint");
            }
        });
        await Run("AutoFavoritesLive060: deleting finished memory-only audio admits new likes while preserving the foreign checkpoint", async () =>
        {
            foreach (var pause in new[] { false, true })
            {
                using var f = new Af060Fixture("foreign-removal-preserves-new-favorites-" + pause, pause ? "104" : null);
                var store = new DownloadQueueStore(Path.Combine(f.DirectoryPath, "diagnostics"));
                UlSet(f.Window, "_downloadQueueStore", store);
                store.Save(DrSnapshot("previous"));
                var path = Path.Combine(f.DirectoryPath, "diagnostics", DownloadQueueStore.FileName);
                var original = File.ReadAllBytes(path);
                await Call(f.Window, "RestoreDownloadQueueAsync", "12345");
                var removed = Af060Row("101"); var next = Af060Row("102");
                await f.Like(removed);
                await Af060WaitForDownloadedTrack(f, removed.Id);
                if (pause)
                {
                    await f.Like(Af060Row("104"));
                    await f.MediaEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    var work = SaField<Task>(f.Window, "_downloadTask");
                    SaInvoke(f.Window, "PauseDownloadQueue");
                    await work;
                    Assert(f.Queue.CanResume, "fixture did not pause the automatic row explicitly");
                }
                await Call(f.Window, "DeleteOfflineTrackAsync", removed);
                await f.Like(next);
                if (!pause) await Af060WaitForDownloadedTrack(f, next.Id);
                Assert(!f.RefreshReleased.Task.IsCompleted && f.Downloads.GetValueOrDefault(next.Id) == (pause ? 0 : 1)
                    && f.Queue.CanResume == pause && f.Library.Find(removed) is null && f.Downloads.GetValueOrDefault(removed.Id) == 1
                    && File.ReadAllBytes(path).SequenceEqual(original), "deletion lost a new confirmed like, overrode a pause, or changed the foreign checkpoint");

                if (!pause)
                {
                    await f.Like(removed, liked: false);
                    await f.Like(removed);
                    await Af060WaitForDownloadedTrack(f, removed.Id, attempts: 2);
                    Assert(f.Library.Find(removed) is not null, "an explicit re-like did not lift the removal suppression");
                    var backend = new Su016Backend { PendingVersion = "9.1.0" };
                    var updates = Su016Service(f.Window, "foreign-removal-favorites", backend);
                    await updates.CheckAsync();
                    Assert(SaInvokeResult(f.Window, "SaveStateBeforeClosing") is null
                        && await (Task<bool>)Call(f.Window, "ApplyAppUpdateAsync", updates) && backend.Applies == 1
                        && File.ReadAllBytes(path).SequenceEqual(original), "close or apply replaced the foreign checkpoint after resumed automatic work");
                }
            }
        });
        await Run("AutoFavoritesLive060: deletion resumes interrupted automatic work after verified refresh under a foreign checkpoint", async () =>
        {
            foreach (var pause in new[] { false, true })
            {
                using var f = new Af060Fixture("foreign-active-removal-" + pause, "102");
                var directory = Path.Combine(f.DirectoryPath, "diagnostics");
                var store = new DownloadQueueStore(directory);
                UlSet(f.Window, "_downloadQueueStore", store);
                store.Save(DrSnapshot("previous"));
                var path = Path.Combine(directory, DownloadQueueStore.FileName);
                var original = File.ReadAllBytes(path);
                await Call(f.Window, "RestoreDownloadQueueAsync", "12345");
                var removed = Af060Row("101"); var interrupted = Af060Row("102"); var next = Af060Row("103");
                await f.Like(removed);
                await Af060WaitForDownloadedTrack(f, removed.Id);
                await f.Like(interrupted);
                await f.MediaEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                if (pause)
                {
                    var running = SaField<Task>(f.Window, "_downloadTask");
                    SaInvoke(f.Window, "PauseDownloadQueue");
                    await running;
                }
                await Call(f.Window, "DeleteOfflineTrackAsync", removed);
                Assert(f.Queue.CanResume && f.Downloads.GetValueOrDefault(interrupted.Id) == 1,
                    "the interrupted batch restarted before a verified refresh");
                f.MediaReleased.TrySetResult(); f.RefreshReleased.TrySetResult();
                await f.Window.RefreshLibraryAsync(refreshMetadata: true);
                await Af061DownloadsIdle(f);
                await f.Like(next);
                if (!pause) await Af060WaitForDownloadedTrack(f, next.Id);
                Assert(f.Downloads.GetValueOrDefault(interrupted.Id) == (pause ? 1 : 2)
                    && f.Downloads.GetValueOrDefault(next.Id) == (pause ? 0 : 1) && f.Queue.CanResume == pause
                    && f.Library.Find(removed) is null && f.Downloads.GetValueOrDefault(removed.Id) == 1
                    && File.ReadAllBytes(path).SequenceEqual(original),
                    "refresh failed to distinguish the removal pause or changed the removed track or foreign checkpoint");
            }
        });
        await Run("AutoFavoritesLive060: clean removal admits a new confirmed like without resuming interrupted favorites", async () =>
        {
            foreach (var foreign in new[] { false, true })
                foreach (var duringRemoval in new[] { false, true })
                    await Af060LikeAfterInterruptingRemoval(foreign, duringRemoval, completeRefresh: false);
        });
        await Run("AutoFavoritesLive060: a verified refresh while canceled workers drain resumes only eligible favorites", async () =>
        {
            await Af060LikeAfterInterruptingRemoval(foreign: false, duringRemoval: true, completeRefresh: true);
            await Af060LikeAfterInterruptingRemoval(foreign: true, duringRemoval: true, completeRefresh: true);
            await Af060LikeAfterInterruptingRemoval(foreign: true, duringRemoval: true, completeRefresh: true, explicitPause: true);
            await Af060LikeAfterInterruptingRemoval(foreign: true, duringRemoval: true, completeRefresh: true, disableAfterRefresh: true);
        });
        await Run("AutoFavoritesLive060: confirmed unlike updates a verified snapshot during single removal", async () =>
        {
            foreach (var foreign in new[] { false, true })
                await Af060LikeAfterInterruptingRemoval(foreign, duringRemoval: true, completeRefresh: true, unlikeAfterRefresh: true);
        });
        await Run("AutoFavoritesLive060: single removal admits confirmed likes while preserving an older failed favorite", async () =>
        {
            foreach (var duringRemoval in new[] { false, true })
                await Af060LikeAfterInterruptingRemovalWithFailure(duringRemoval);
        });
        await Run("AutoFavoritesLive060: verified unlike replaces a discarded pending snapshot during removal", async () =>
            await Af060LikeAfterInterruptingRemoval(foreign: true, duringRemoval: true, completeRefresh: true,
                unlikeAfterRefresh: true, invalidatePendingBeforeUnlike: true));
        await Run("AutoFavoritesLive060: confirmed unlike releases interrupted work after single removal", Af060UnlikeAfterRemoval);
        await Run("AutoFavoritesLive060: bulk unlike drains before scheduling a confirmed prefix after removal", Af060BulkUnlikeDuringRemoval);
        await Run("AutoFavoritesLive060: explicit Continue admits a new confirmed like while earlier audio is held", Af060LikeDuringExplicitResume);
        await Run("AutoFavoritesLive060: removal distinguishes appended automatic work from mixed manual work", async () =>
        {
            foreach (var duringRemoval in new[] { false, true })
                await Af060LikeAfterInterruptingRemovalWithFailure(duringRemoval, manualQueue: true);
            await Af060RemovalDuringMixedWork();
        });
        await Run("AutoFavoritesLive060: a like confirmed during protected deletion stays with its owner until checkpoint repair", async () =>
        {
            foreach (var mode in new[] { "active", "disabled", "paused", "account" })
                await Af060ConfirmLikeDuringProtectedRemoval(mode);
        });
        await Run("AutoFavoritesLive060: checkpoint repair uses the verified snapshot or confirmed like without another refresh", async () =>
        {
            foreach (var mode in new[] { "snapshot", "confirmed", "disabled" })
            {
                using var f = new Af060Fixture("active-favorites-after-index-repair-" + mode);
                var old = Af060Row("101"); var next = Af060Row("102");
                f.AddServerFavorite(old.Id);
                await f.Library.SaveAsync(old, new(new Uri("https://s1.vlx.yandex.net/101.mp3"), "mp3", 320), default);
                var playlist = new PlaylistDetails("local:favorites", "liked", "Мне нравится", "", 0, [old]);
                await f.Library.RememberPlaylistAsync(playlist, AudioQuality.Optimal, default);
                var queue = new DownloadQueueState(); queue.Replace([old], playlist.Title, favoritesBatch: true);
                queue.Entries[0].Complete(Af060Fixture.Media.Length);
                var store = new DownloadQueueStore(Path.Combine(f.DirectoryPath, "diagnostics"));
                UlSet(f.Window, "_downloadQueueStore", store);
                store.Save(DownloadQueueStore.Capture("12345", queue, AudioQuality.Optimal, true, playlist));
                var index = Path.Combine(f.Library.DirectoryPath, "playlists.index.json");
                var good = File.ReadAllBytes(index); File.WriteAllText(index, "{");
                await Call(f.Window, "RestoreDownloadQueueAsync", "12345");
                if (mode == "disabled") Control<CheckBox>(f.Window, "AutoFavoritesCheckBox").IsChecked = false;
                if (mode == "confirmed") await f.Like(next);
                else
                {
                    f.AddServerFavorite(next.Id);
                    f.RefreshReleased.TrySetResult(); await f.Window.RefreshLibraryAsync(refreshMetadata: true);
                }
                Assert(f.Downloads.GetValueOrDefault(next.Id) == 0, "protected checkpoint started automatic work before repair");
                var independent = Af060Row("103");
                if (mode != "disabled") await f.StartManual(independent);
                File.WriteAllBytes(index, good);
                if (mode != "disabled")
                {
                    await Call(f.Window, "DeleteOfflineTrackAsync", independent);
                    Assert(f.Library.Find(independent) is null && f.Downloads.GetValueOrDefault(independent.Id) == 1,
                        "fixture did not independently delete the temporary manual download before recovery");
                }
                await Call(f.Window, "RetryDownloadQueueRestoreAsync");
                if (mode == "confirmed")
                {
                    await Until(() => f.Downloads.ContainsKey(next.Id));
                    if (SaField<Task?>(f.Window, "_downloadTask") is { } download)
                        await download.WaitAsync(TimeSpan.FromSeconds(10));
                }
                else await Af061DownloadsIdle(f);
                Assert(f.Downloads.GetValueOrDefault(old.Id) == 1 && f.Downloads.GetValueOrDefault(next.Id) == (mode == "disabled" ? 0 : 1)
                    && (f.Library.Find(next) is not null) == (mode != "disabled"),
                    "repair lost current authorized favorites or ignored the disabled preference");
                if (mode == "confirmed") Assert(!f.RefreshReleased.Task.IsCompleted, "the confirmed like relied on a completed metadata refresh");
            }
        });
        await Run("AutoFavoritesLive060: verified external relikes clear exclusions on terminal rows without retrying other failures", async () =>
        {
            using var f = new Af060Fixture("verified-terminal-relike") { FailMediaTrack = "900" };
            var unavailable = Af060Row("101", available: false); var ready = Af060Row("102");
            var removed = Af060Row("103"); var failing = Af060Row("900");
            var tracks = new[] { unavailable, ready, removed, failing };
            var auto = Control<CheckBox>(f.Window, "AutoFavoritesCheckBox"); auto.IsChecked = false;
            foreach (var track in tracks) await f.Like(track);
            f.RefreshReleased.TrySetResult(); await Af061DownloadsIdle(f);
            var playlist = new PlaylistDetails("local:favorites", "liked", "Мне нравится", "", 0, tracks);
            await Call(f.Window, "StartDownloadBatchAsync", tracks, playlist.Title, playlist, false);
            await Call(f.Window, "DeleteOfflineTrackAsync", removed);

            await f.Like(unavailable, liked: false);
            f.RemoveServerFavorite(ready.Id); f.RemoveServerFavorite(removed.Id);
            await f.Window.RefreshLibraryAsync(refreshMetadata: true);
            Assert(f.Queue.Entries.Take(3).All(entry => entry.RemovedFromCollection && entry.SkipFavoritesDownload)
                && f.Queue.CanRetry && f.Downloads.GetValueOrDefault(failing.Id) == 1,
                "verified removal did not mark unavailable, completed and removed favorite rows");

            auto.IsChecked = true;
            foreach (var track in tracks.Take(3)) f.AddServerFavorite(track.Id);
            await f.Window.RefreshLibraryAsync(refreshMetadata: true);
            await Af061DownloadsIdle(f);
            Assert(f.Queue.Entries.Take(3).All(entry => entry.State == DownloadEntryState.Completed && !entry.SkipFavoritesDownload && !entry.RemovedFromCollection)
                && f.Downloads.GetValueOrDefault(unavailable.Id) == 1 && f.Downloads.GetValueOrDefault(ready.Id) == 1
                && f.Downloads.GetValueOrDefault(removed.Id) == 2 && f.Downloads.GetValueOrDefault(failing.Id) == 1 && f.Queue.CanRetry,
                "external relike stayed excluded, repeated a valid cache or lost the unrelated Retry");
        });
        await Run("AutoFavoritesLive060: update and canceled close preserve confirmed newer metadata for an existing unavailable row", async () =>
        {
            foreach (var action in new[] { "reopen", "disable", "cancel-close" })
            {
                var name = "requeue-unavailable-on-pause-" + action;
                DownloadQueueStore store;
                using (var f = new Af060Fixture(name, "900"))
                {
                    store = new DownloadQueueStore(Path.Combine(f.DirectoryPath, "diagnostics"));
                    UlSet(f.Window, "_downloadQueueStore", store);
                    var running = f.StartManual(Af060Row("101", available: false), Af060Row("900"));
                    await f.MediaEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    await Until(() => f.Queue.Entries[0].State == DownloadEntryState.Unavailable);
                    await f.Like(Af060Row("101"));

                    object? canceledClose = null;
                    if (action == "cancel-close") canceledClose = typeof(MainWindow).GetMethod("PauseDownloadsForClose",
                        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(f.Window, null);
                    else SaInvoke(f.Window, "PauseDownloadQueueForAppUpdate");
                    await running.WaitAsync(TimeSpan.FromSeconds(5));
                    SaInvoke(f.Window, "TrySaveDownloadQueueCheckpoint");
                    var saved = store.Load("12345");
                    Assert(saved?.Entries[0] is { State: DownloadEntryState.Paused, Track.Available: true, DeferredFavorite.PreviousState: DownloadEntryState.Unavailable }
                        && f.Downloads.GetValueOrDefault("101") == 0, "pause lost or started the new metadata before continuation");

                    if (action != "reopen")
                    {
                        f.MediaReleased.TrySetResult();
                        if (action == "disable")
                        {
                            Control<CheckBox>(f.Window, "AutoFavoritesCheckBox").IsChecked = false;
                            await Call(f.Window, "ResumeDownloadQueue");
                            Assert(f.Queue.Entries[0].State == DownloadEntryState.Unavailable && f.Downloads.GetValueOrDefault("101") == 0,
                                "disabling auto deleted history or started a deferred favorite");
                        }
                        else
                        {
                            SaInvoke(f.Window, "ResumeDownloadsAfterCanceledClose", canceledClose!);
                            await Until(() => f.Library.Read().Count == 2 && SaField<Task?>(f.Window, "_downloadTask") is null);
                            Assert(f.Downloads.GetValueOrDefault("101") == 1, "canceled close lost a reopened row");
                        }
                    }
                }
                if (action != "reopen") continue;
                using var reopened = new Af060Fixture(name);
                UlSet(reopened.Window, "_downloadQueueStore", store);
                await Call(reopened.Window, "RestoreDownloadQueueAsync", "12345");
                Assert(reopened.Queue.CanResume && reopened.Downloads.Count == 0, "the reopened queue started before Continue");
                await Call(reopened.Window, "ResumeDownloadQueue");
                Assert(reopened.Downloads.GetValueOrDefault("101") == 1, "restored Continue did not download the confirmed row once");
            }
        });
        await Run("AutoFavoritesLive060: verified removal marks an active favorite before its transfer fails or pauses", async () =>
        {
            foreach (var mode in new[] { "failed", "paused", "manual" })
            {
                using var f = new Af060Fixture("verified-removal-active-" + mode, "101");
                var track = Af060Row("101");
                if (mode == "manual") Control<CheckBox>(f.Window, "AutoFavoritesCheckBox").IsChecked = false;
                await f.Like(track);
                if (mode == "manual") _ = f.StartManual(track);
                await f.MediaEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                var transfer = SaField<Task>(f.Window, "_downloadTask");
                var entry = f.Queue.Entries.Single();
                f.RemoveServerFavorite(track.Id);
                f.RefreshReleased.TrySetResult();
                await f.Window.RefreshLibraryAsync(refreshMetadata: true);
                Assert(!transfer.IsCompleted && entry.State == DownloadEntryState.Downloading
                    && entry.SkipFavoritesDownload == (mode != "manual"), "reconciliation canceled the transfer or changed manual eligibility");

                if (mode == "paused") SaInvoke(f.Window, "PauseDownloadQueue");
                else f.FailMediaTrack = track.Id;
                f.MediaReleased.TrySetResult();
                await transfer.WaitAsync(TimeSpan.FromSeconds(5));
                Assert(entry.State == (mode == "paused" ? DownloadEntryState.Paused : DownloadEntryState.Failed)
                    && !f.Queue.CanResume && f.Queue.CanRetry == (mode == "manual"),
                    "a verified removed favorite remained eligible for Continue or Retry");
            }
        });
        await Run("AutoFavoritesLive060: incremental favorites checkpoints restore paused with unlike guards and no partial playlist", () =>
        {
            var directory = Path.Combine(_directory, "auto-favorites-live-060-checkpoint");
            using var offline = new OfflineLibrary(Path.Combine(directory, "music"));
            var store = new DownloadQueueStore(Path.Combine(directory, "state")); var queue = new DownloadQueueState();
            queue.Replace([Af060Row("101"), Af060Row("102")], "Автоскачивание", favoritesBatch: true);
            queue.BeginRun(false); queue.SetCollectionLike("102", false);
            var snapshot = DownloadQueueStore.Capture("12345", queue, AudioQuality.Excellent, true, null);
            store.Save(snapshot); var restored = DownloadQueueStore.PrepareRestore(store.Load("12345")!, offline);
            var replay = new DownloadQueueState(); replay.Restore(restored);
            Assert(restored.FavoritesBatch && restored.Automatic && restored.Playlist is null
                && replay.Entries[0].State == DownloadEntryState.Paused && replay.Entries[1].SkipFavoritesDownload,
                "incremental favorites checkpoint lost pause, unlike semantics or claimed a complete playlist");
            replay.SetCollectionLike("101", false);
            Assert(replay.Entries[0].SkipFavoritesDownload && !replay.CanResume, "restored incremental batch could resume an unliked favorite");
            try { store.Save(snapshot with { Automatic = false }); throw new InvalidOperationException("manual favorites checkpoint without playlist was accepted"); }
            catch (JsonException) { }
            return Task.CompletedTask;
        });
    }

    private static async Task CheckSingleRemovalAuto061()
    {
        await Run("QaFixes061: ordinary finished deletion admits an independent confirmed like before refresh", async () =>
        {
            foreach (var mode in new[] { "during", "after", "failed" })
            {
                using var f = new Af060Fixture("ordinary-removal-confirmed-like-" + mode);
                var store = new DownloadQueueStore(Path.Combine(f.DirectoryPath, "diagnostics"));
                UlSet(f.Window, "_downloadQueueStore", store);
                var removed = Af060Row("101"); var next = Af060Row("102");
                await f.Like(removed);
                await Af060WaitForDownloadedTrack(f, removed.Id);
                Assert(store.Load("12345")?.Entries.Single().State == DownloadEntryState.Completed,
                    "fixture did not save an ordinary completed checkpoint");
                var path = f.Library.ResolvePath(f.Library.Find(removed)!.FileName);
                var attributes = File.GetAttributes(path);
                if (mode == "failed") File.SetAttributes(path, attributes | FileAttributes.ReadOnly);
                try
                {
                    if (mode == "during")
                    {
                        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                        using var release = new ManualResetEventSlim();
                        f.Library.RemovalCheckpoint = phase =>
                        {
                            if (phase != "after-stage") return;
                            entered.TrySetResult();
                            release.Wait(TimeSpan.FromSeconds(10));
                        };
                        var remove = Call(f.Window, "DeleteOfflineTrackAsync", removed);
                        try
                        {
                            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                            await f.Like(next);
                            Assert(!f.Downloads.ContainsKey(next.Id), "confirmed work started before offline deletion finished");
                        }
                        finally { release.Set(); }
                        await remove.WaitAsync(TimeSpan.FromSeconds(5));
                    }
                    else
                    {
                        await Call(f.Window, "DeleteOfflineTrackAsync", removed);
                        await f.Like(next);
                    }
                    if (mode != "failed") await Af060WaitForDownloadedTrack(f, next.Id);
                    Assert(!f.RefreshReleased.Task.IsCompleted && f.Downloads.GetValueOrDefault(next.Id) == (mode == "failed" ? 0 : 1)
                        && (f.Library.Find(removed) is not null) == (mode == "failed") && f.Downloads.GetValueOrDefault(removed.Id) == 1
                        && SaField<Task?>(f.Window, "_downloadTask") is null,
                        "deletion lost an independent confirmed like, revived the removed track, or lifted a failed deletion's pause");
                }
                finally { if (File.Exists(path)) File.SetAttributes(path, attributes); }
            }
        });
        await Run("QaFixes061: single offline deletion waits for favorites refresh then fills only missing tracks", async () =>
        {
            using var f = new Af060Fixture("qa-single-removal-ready");
            var removed = Af060Row("101"); var retained = Af060Row("102");
            await Af061Ready(f, removed, retained);
            var retainedPath = f.Library.ResolvePath(f.Library.Find(retained)!.FileName);
            var retainedHash = System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(retainedPath));
            await Call(f.Window, "DeleteOfflineTrackAsync", removed);
            Control<CheckBox>(f.Window, "AutoFavoritesCheckBox").IsChecked = false;
            Control<CheckBox>(f.Window, "AutoFavoritesCheckBox").IsChecked = true;
            SaInvoke(f.Window, "TryStartPendingAutoFavorites");
            Assert(f.Library.Read().Single().Id == retained.Id && f.Downloads.GetValueOrDefault("101") == 1
                && SaField<CancellationTokenSource?>(f.Window, "_downloadCts") is null,
                "single removal immediately returned the deleted favorite before refresh");
            await f.Window.RefreshLibraryAsync(refreshMetadata: true);
            await Af061DownloadsIdle(f);
            Assert(f.Library.Read().Count == 2, "refresh did not restore the expected offline collection");
            Assert(f.Downloads.GetValueOrDefault("101") == 2 && f.Downloads.GetValueOrDefault("102") == 1
                && retainedHash.SequenceEqual(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(retainedPath))),
                "refresh did not fill only the deleted favorite or changed retained audio");
            await f.Window.RefreshLibraryAsync(refreshMetadata: true);
            await Af061DownloadsIdle(f);
            await f.Like(Af060Row("103"));
            await Af061DownloadsIdle(f);
            Assert(f.Library.Read().Count == 3, "new confirmed like did not complete the offline collection");
            Assert(f.Downloads.GetValueOrDefault("101") == 2 && f.Downloads.GetValueOrDefault("102") == 1
                && f.Downloads.GetValueOrDefault("103") == 1, "single removal left new confirmed likes blocked or redownloaded ready music");
        });
        await Run("QaFixes061: single offline deletion during automatic work restarts only after refreshed favorites", async () =>
        {
            using var f = new Af060Fixture("qa-single-removal-active-auto", "102");
            await Af061Ready(f, Af060Row("101"));
            await f.Like(Af060Row("102")); await f.MediaEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert(SaField<bool>(f.Window, "_downloadIsAutomatic"), "fixture did not reach an automatic worker");
            await Call(f.Window, "DeleteOfflineTrackAsync", Af060Row("101"));
            Assert(f.Library.Read().Count == 0 && f.Downloads.GetValueOrDefault("101") == 1
                && f.Downloads.GetValueOrDefault("102") == 1 && f.Queue.HasPaused
                && SaField<CancellationTokenSource?>(f.Window, "_downloadCts") is null,
                "single removal restarted the canceled automatic cohort before refresh");
            f.MediaReleased.TrySetResult();
            await f.Window.RefreshLibraryAsync(refreshMetadata: true);
            await Af061DownloadsIdle(f);
            Assert(f.Library.Read().Count == 2, "refreshed automatic cohort did not save both missing tracks");
            Assert(f.Downloads.GetValueOrDefault("101") == 2 && f.Downloads.GetValueOrDefault("102") == 2
                && !f.Queue.Suspended && !SaField<bool>(f.Window, "_downloadsAutoSuspended"),
                "maintenance-created suspended auto work permanently blocked the next refresh");
        });
        await Run("QaFixes061: oversized verified favorites cannot replace an interrupted automatic queue after deletion", async () =>
        {
            using var f = new Af060Fixture("qa-single-removal-reject-oversized-refresh", "102");
            var store = new DownloadQueueStore(Path.Combine(f.DirectoryPath, "diagnostics"));
            UlSet(f.Window, "_downloadQueueStore", store);
            await Af061Ready(f, Af060Row("101"));
            await f.Like(Af060Row("102")); await f.MediaEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Call(f.Window, "DeleteOfflineTrackAsync", Af060Row("101"));
            var paused = f.Queue.Entries.Single(entry => entry.Track.Id == "102");
            var cache = SaField<List<TrackRow>>(f.Window, "_favoritesCache");
            cache.Clear();
            cache.AddRange(Enumerable.Range(1000, DownloadQueueStore.MaximumEntries + 1).Select(id => Af060Row(id.ToString())));
            SaInvoke(f.Window, "ScheduleAutoFavoritesDownloads");
            SaInvoke(f.Window, "TrySaveDownloadQueueCheckpoint");
            var persisted = store.Load("12345")?.Entries;
            Assert(f.Queue.CanResume && f.Queue.Entries.Contains(paused) && paused.State == DownloadEntryState.Paused
                && persisted is not null && persisted.Single(entry => entry.Track.Id == "102").State == DownloadEntryState.Paused
                && persisted.All(entry => entry.Track.Id is "101" or "102")
                && f.Downloads.GetValueOrDefault("102") == 1
                && Control<TextBlock>(f.Window, "StatusText").Text.Contains("не помещается", StringComparison.Ordinal),
                "rejected refresh erased the previous queue or began an unadmitted download");
            f.MediaReleased.TrySetResult();
            await Call(f.Window, "ResumeDownloadQueue");
            Assert(f.Downloads.GetValueOrDefault("102") == 2 && f.Downloads.GetValueOrDefault("101") == 1,
                "explicit resume did not restrict work to the preserved pause");
        });
        await Run("QaFixes061: single offline deletion preserves pauses and keeps the deleted favorite out of verified refresh", async () =>
        {
            foreach (var mode in new[] { "user-pause", "restore-paused", "restore-failed", "active-manual" })
            {
                using var f = new Af060Fixture("qa-single-removal-guard-" + mode, "900");
                await Af061Ready(f, Af060Row("101"));
                if (mode is "user-pause" or "active-manual")
                {
                    var manual = f.StartManual(Af060Row("900")); await f.MediaEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    if (mode == "user-pause") { SaInvoke(f.Window, "PauseDownloadQueue"); await manual; }
                    await Call(f.Window, "DeleteOfflineTrackAsync", Af060Row("101"));
                    await manual;
                }
                else
                {
                    var previous = Af060Row("900");
                    var auto = Control<CheckBox>(f.Window, "AutoFavoritesCheckBox");
                    auto.IsChecked = false;
                    // This case represents a paused or failed favorite that is
                    // still in the verified collection, not a remote removal.
                    await f.Like(previous);
                    await f.Window.RefreshLibraryAsync(refreshMetadata: true);
                    f.Queue.Replace([previous], "Восстановленная очередь", favoritesBatch: true);
                    var failed = mode == "restore-failed";
                    f.Queue.Entries.Single().SetState(failed ? DownloadEntryState.Failed : DownloadEntryState.Paused);
                    var store = new DownloadQueueStore(Path.Combine(_directory, "qa-single-removal-store-" + mode));
                    store.Save(DownloadQueueStore.Capture("12345", f.Queue, AudioQuality.Excellent, true,
                        new PlaylistDetails("local:favorites", "liked", "Мне нравится", "", 0, [previous])));
                    UlSet(f.Window, "_downloadQueueStore", store);
                    await Call(f.Window, "RestoreDownloadQueueAsync", "12345");
                    Assert(!f.Queue.Entries.Single().SkipFavoritesDownload && f.Queue.CanResume == !failed && f.Queue.CanRetry == failed,
                        "fixture did not restore a currently liked row with its original state");
                    auto.IsChecked = true;
                    await Af061DownloadsIdle(f);
                    await Call(f.Window, "DeleteOfflineTrackAsync", Af060Row("101"));
                }
                var removalStatus = Control<TextBlock>(f.Window, "StatusText").Text;
                var before = f.Downloads.Values.Sum();
                f.MediaReleased.TrySetResult(); await f.Window.RefreshLibraryAsync(refreshMetadata: true);
                SaInvoke(f.Window, "TryStartPendingAutoFavorites");
                await Af061DownloadsIdle(f);
                Assert(f.Library.Read().Count == 0 && f.Downloads.Values.Sum() == before
                    && SaField<bool>(f.Window, "_downloadsAutoSuspended") == (mode != "restore-failed")
                    && f.Queue.CanRetry == (mode == "restore-failed")
                    && !removalStatus.Contains("вернёт трек", StringComparison.Ordinal),
                    "single removal erased a protected pause or advertised an automatic restart: " + mode);
            }
        });
        await Run("QaFixes061: repeated pause and cancel cannot invalidate single deletion while workers drain", async () =>
        {
            foreach (var pause in new[] { true, false })
            {
                using var f = new Af060Fixture("qa-single-removal-cancel-race-" + pause, "102") { HoldMediaCancellation = true };
                await Af061Ready(f, Af060Row("101"));
                await f.Like(Af060Row("102")); await f.MediaEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                var removal = Call(f.Window, "DeleteOfflineTrackAsync", Af060Row("101"));
                await f.MediaCancellationEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                var before = SaField<long>(f.Window, "_downloadQueueAdmissionRevision");
                if (pause) SaInvoke(f.Window, "PauseDownloadQueue");
                else await Call(f.Window, "DownloadSelectedAsync");
                Assert(SaField<long>(f.Window, "_downloadQueueAdmissionRevision") == before
                    && !Control<Button>(f.Window, "DownloadButton").IsEnabled, "cancel invalidated an already canceled worker's admission");
                f.MediaCancellationReleased.TrySetResult();
                await removal;
                f.MediaReleased.TrySetResult(); await f.Window.RefreshLibraryAsync(refreshMetadata: true);
                await Af061DownloadsIdle(f);
                Assert(f.Library.Read().Count == 2 && f.Downloads.GetValueOrDefault("101") == 2 && f.Downloads.GetValueOrDefault("102") == 2,
                    "the repeated cancel prevented the expected post-removal refresh");
            }
        });
        await Run("QaFixes061: single removal preserves Retry and starts independently confirmed favorites", async () =>
        {
            using var f = new Af060Fixture("qa-single-removal-keep-retry") { FailMediaTrack = "900" };
            var track = Af060Row("101");
            var auto = Control<CheckBox>(f.Window, "AutoFavoritesCheckBox");
            auto.IsChecked = false;
            await f.Like(track);
            auto.IsChecked = true;
            var store = new DownloadQueueStore(Path.Combine(f.DirectoryPath, "diagnostics"));
            UlSet(f.Window, "_downloadQueueStore", store);
            await f.StartManual(Af060Row("900"), track);
            await Call(f.Window, "DeleteOfflineTrackAsync", track);
            SaInvoke(f.Window, "TrySaveDownloadQueueCheckpoint");
            Assert(f.Queue.CanRetry && f.Downloads.GetValueOrDefault("900") == 1
                && store.Load("12345")?.Entries.Single(entry => entry.Track.Id == "900").State == DownloadEntryState.Failed,
                "single removal left an unrelated failure suspended or lost its checkpoint");
            await f.Like(Af060Row("102"));
            await Until(() => f.Library.Read().Any(item => item.Id == "102") && SaField<Task?>(f.Window, "_downloadTask") is null);
            Assert(!f.RefreshReleased.Task.IsCompleted && f.Queue.CanRetry && f.Downloads.GetValueOrDefault("900") == 1
                && f.Downloads.GetValueOrDefault("101") == 1 && f.Downloads.GetValueOrDefault("102") == 1,
                "single removal blocked a new confirmed favorite or retried previous work");
            f.RefreshReleased.TrySetResult(); await f.Window.RefreshLibraryAsync(refreshMetadata: true);
            await Af061DownloadsIdle(f);
            Assert(f.Library.Find(track) is null && f.Downloads.GetValueOrDefault("101") == 1 && f.Queue.CanRetry,
                "the normal verified refresh downloaded the explicitly removed track");
            await f.Like(track, liked: false);
            await f.Like(track);
            await Af061DownloadsIdle(f);
            Assert(f.Downloads.GetValueOrDefault("101") == 2 && f.Queue.CanRetry, "an explicitly reliked removed track did not reopen its row");
            f.FailMediaTrack = null;
            await Call(f.Window, "RetryFailedDownloads");
            Assert(f.Library.Read().Count == 3 && f.Downloads.GetValueOrDefault("101") == 2 && f.Downloads.GetValueOrDefault("900") == 2,
                "explicit retry revived the removed track or missed the failed track");
        });
        await Run("QaFixes061: update pause invalidates a completed single removal automatic restart admission", async () =>
        {
            using var f = new Af060Fixture("qa-single-removal-update-pause", "102");
            await Af061Ready(f, Af060Row("101"));
            await f.Like(Af060Row("102")); await f.MediaEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Call(f.Window, "DeleteOfflineTrackAsync", Af060Row("101"));
            var before = f.Downloads.Values.Sum();
            Assert(SaField<CancellationTokenSource?>(f.Window, "_downloadCts") is null, "removal workers have not finished");
            SaInvoke(f.Window, "PauseDownloadQueueForAppUpdate");
            f.MediaReleased.TrySetResult(); await f.Window.RefreshLibraryAsync(refreshMetadata: true);
            Assert(f.Library.Read().Count == 0 && f.Downloads.Values.Sum() == before
                && SaField<bool>(f.Window, "_downloadsAutoSuspended"), "pending removal admission cleared the later update pause");
        });
        await Run("QaFixes061: failed single offline removal keeps automatic downloading suspended", async () =>
        {
            using var f = new Af060Fixture("qa-single-removal-io-failure"); var row = Af060Row("101");
            await Af061Ready(f, row);
            var path = f.Library.ResolvePath(f.Library.Find(row)!.FileName); var attributes = File.GetAttributes(path);
            try
            {
                File.SetAttributes(path, attributes | FileAttributes.ReadOnly);
                await Call(f.Window, "DeleteOfflineTrackAsync", row);
                Assert(f.Library.Read().Count == 1 && SaField<bool>(f.Window, "_downloadsAutoSuspended")
                    && !Control<TextBlock>(f.Window, "StatusText").Text.Contains("вернёт трек", StringComparison.Ordinal),
                    "failed removal granted automatic restart or promised to redownload a retained file");
                await f.Window.RefreshLibraryAsync(refreshMetadata: true);
                Assert(f.Downloads.GetValueOrDefault("101") == 1 && SaField<bool>(f.Window, "_downloadsAutoSuspended"),
                    "a successful refresh cleared the removal failure guard");
            }
            finally { File.SetAttributes(path, attributes); }
        });
        await Run("QaFixes061: account change during single offline removal cannot grant automatic restart", async () =>
        {
            using var f = new Af060Fixture("qa-single-removal-account");
            await Af061Ready(f, Af060Row("101"));
            f.Library.RemovalCheckpoint = stage =>
            {
                if (stage == "after-stage") f.Window.Dispatcher.Invoke(() => f.Window.SignOut());
            };
            await Call(f.Window, "DeleteOfflineTrackAsync", Af060Row("101"));
            f.Library.RemovalCheckpoint = null;
            var before = f.Downloads.Values.Sum();
            SaInvoke(f.Window, "ScheduleAutoFavoritesDownloads");
            Assert(SaField<Session?>(f.Window, "_session") is null && f.Queue.Suspended
                && SaField<bool>(f.Window, "_downloadsAutoSuspended") && f.Downloads.Values.Sum() == before,
                "single removal granted a restart after the account changed");
        });
    }

    private static async Task Af060LikeAfterInterruptingRemoval(bool foreign, bool duringRemoval, bool completeRefresh,
        bool explicitPause = false, bool disableAfterRefresh = false, bool unlikeAfterRefresh = false, bool invalidatePendingBeforeUnlike = false)
    {
        using var f = new Af060Fixture($"interrupted-removal-{foreign}-{duringRemoval}-{completeRefresh}-{explicitPause}-{disableAfterRefresh}-{unlikeAfterRefresh}-{invalidatePendingBeforeUnlike}", "102")
            { HoldMediaCancellation = true };
        var diagnostics = Path.Combine(f.DirectoryPath, "diagnostics");
        var store = new DownloadQueueStore(diagnostics);
        UlSet(f.Window, "_downloadQueueStore", store);
        var checkpointPath = Path.Combine(diagnostics, DownloadQueueStore.FileName);
        byte[]? original = null;
        if (foreign)
        {
            store.Save(DrSnapshot("previous"));
            original = File.ReadAllBytes(checkpointPath);
            await Call(f.Window, "RestoreDownloadQueueAsync", "12345");
        }
        var removed = Af060Row("101"); var interrupted = Af060Row("102"); var next = Af060Row("103");
        await f.Like(removed);
        await Af060WaitForDownloadedTrack(f, removed.Id);
        await f.Like(interrupted);
        await f.MediaEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var unliked = Af060Row("104");
        var externalUnlike = Af060Row("105");
        if (unlikeAfterRefresh) f.AddServerFavorite(unliked.Id);
        if (invalidatePendingBeforeUnlike) f.AddServerFavorite(externalUnlike.Id);
        if (explicitPause) SaInvoke(f.Window, "PauseDownloadQueue");

        var removal = Call(f.Window, "DeleteOfflineTrackAsync", removed);
        try
        {
            await f.MediaCancellationEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (duringRemoval) await f.Like(next);
            if (completeRefresh)
            {
                f.RefreshReleased.TrySetResult();
                await f.Window.RefreshLibraryAsync(refreshMetadata: true);
                Assert(SaField<bool>(f.Window, "_favoritesSnapshotReady") && !removal.IsCompleted,
                    "the authoritative snapshot did not finish while the interrupted transfer was draining");
                if (unlikeAfterRefresh)
                {
                    Assert(SaField<PlaylistDetails>(f.Window, "_pendingAutoFavorites").Tracks.Any(track => track.Id == unliked.Id),
                        "the verified snapshot did not include the track that will be unliked");
                    Task? superseded = null;
                    if (invalidatePendingBeforeUnlike)
                    {
                        f.RemoveServerFavorite(externalUnlike.Id);
                        f.HoldMetadataRefresh = true;
                        superseded = f.Window.RefreshLibraryAsync(refreshMetadata: true);
                        await f.MetadataEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                        Assert(SaField<PlaylistDetails?>(f.Window, "_pendingAutoFavorites") is null && !superseded.IsCompleted,
                            "the intervening collection read did not invalidate the pending snapshot before requesting metadata");
                    }
                    f.FailFullRefresh = true;
                    await f.Like(unliked, liked: false);
                    if (superseded is not null) await superseded.WaitAsync(TimeSpan.FromSeconds(5));
                    Assert(SaField<bool>(f.Window, "_favoritesSnapshotReady")
                        && SaField<PlaylistDetails?>(f.Window, "_pendingAutoFavorites") is { } pending
                        && pending.Tracks.All(track => track.Id != unliked.Id && track.Id != externalUnlike.Id),
                        "the confirmed unlike did not publish a complete replacement snapshot");
                }
                if (disableAfterRefresh) Control<CheckBox>(f.Window, "AutoFavoritesCheckBox").IsChecked = false;
            }
            Assert(!f.Downloads.ContainsKey(next.Id), "a confirmed like started during offline maintenance");
        }
        finally
        {
            f.MediaReleased.TrySetResult();
            f.MediaCancellationReleased.TrySetResult();
            await removal.WaitAsync(TimeSpan.FromSeconds(10));
        }
        if (!duringRemoval) await f.Like(next);

        var mayDownload = !explicitPause && !disableAfterRefresh;
        if (mayDownload) await Af060WaitForDownloadedTrack(f, next.Id);
        if (completeRefresh) await Af061DownloadsIdle(f);
        else
        {
            Assert(!f.RefreshReleased.Task.IsCompleted && f.Library.Find(next) is not null
                && f.Queue.Entries.Single(entry => entry.Track.Id == interrupted.Id).State == DownloadEntryState.Paused,
                "the new like depended on the full refresh or resumed a paused track");
            f.FailFullRefresh = true;
            f.RefreshReleased.TrySetResult();
            await f.Window.RefreshLibraryAsync(refreshMetadata: true);
        }
        var expectRestart = completeRefresh && mayDownload;
        Assert(f.Downloads.GetValueOrDefault(next.Id) == (mayDownload ? 1 : 0)
            && f.Downloads.GetValueOrDefault(interrupted.Id) == (expectRestart ? 2 : 1)
            && (f.Library.Find(interrupted) is not null) == expectRestart && f.Queue.CanResume == !expectRestart
            && f.Downloads.GetValueOrDefault(removed.Id) == 1 && f.Library.Find(removed) is null
            && (original is null || File.ReadAllBytes(checkpointPath).SequenceEqual(original)),
            "removal changed the user pause, retried the wrong track, lost a verified like, or replaced a foreign checkpoint");
        if (unlikeAfterRefresh)
            Assert(f.Downloads.GetValueOrDefault(unliked.Id) == 0 && f.Library.Find(unliked) is null
                && f.Downloads.GetValueOrDefault(externalUnlike.Id) == 0 && f.Library.Find(externalUnlike) is null,
                "the interrupted queue restarted from the snapshot that preceded the confirmed unlike");
    }

    private static async Task Af060LikeAfterInterruptingRemovalWithFailure(bool duringRemoval, bool manualQueue = false)
    {
        using var f = new Af060Fixture($"interrupted-removal-failed-{duringRemoval}-{manualQueue}", "102")
            { HoldMediaCancellation = true, FailMediaTrack = "104" };
        UlSet(f.Window, "_concurrentDownloads", 2);
        var removed = Af060Row("101"); var interrupted = Af060Row("102"); var next = Af060Row("103"); var failed = Af060Row("104");
        await f.Like(removed);
        await Af060WaitForDownloadedTrack(f, removed.Id);
        if (manualQueue)
        {
            await f.StartManual(failed);
            await f.Like(interrupted);
        }
        else
        {
            f.AddServerFavorite(failed.Id);
            f.AddServerFavorite(interrupted.Id);
            f.RefreshReleased.TrySetResult();
            await f.Window.RefreshLibraryAsync(refreshMetadata: true);
            f.HoldNextFullRefresh();
        }
        await f.MediaEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Until(() => f.Queue.Entries.Any(entry => entry.Track.Id == failed.Id && entry.State == DownloadEntryState.Failed));

        Assert(SaField<bool>(f.Window, "_downloadIsAutomatic") && SaField<bool>(f.Window, "_downloadQueueAutomatic") != manualQueue,
            "fixture did not start the automatic worker in the intended queue");
        var removal = Call(f.Window, "DeleteOfflineTrackAsync", removed);
        try
        {
            await f.MediaCancellationEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (duringRemoval) await f.Like(next);
            Assert(!f.Downloads.ContainsKey(next.Id), "a confirmed like started during removal");
        }
        finally
        {
            f.MediaReleased.TrySetResult();
            f.MediaCancellationReleased.TrySetResult();
            await removal.WaitAsync(TimeSpan.FromSeconds(10));
        }
        if (!duringRemoval) await f.Like(next);
        await Af060WaitForDownloadedTrack(f, next.Id);
        Assert(!f.RefreshReleased.Task.IsCompleted && f.Queue.CanRetry && f.Queue.CanResume
            && f.Downloads.GetValueOrDefault(failed.Id) == 1 && f.Downloads.GetValueOrDefault(interrupted.Id) == 1,
            "the confirmed like depended on refresh, retried the old error, or resumed interrupted work early");

        f.RefreshReleased.TrySetResult();
        await f.Window.RefreshLibraryAsync(refreshMetadata: true);
        await Af061DownloadsIdle(f);
        Assert(f.Queue.CanRetry && !f.Queue.CanResume && f.Downloads.GetValueOrDefault(failed.Id) == 1
            && f.Downloads.GetValueOrDefault(interrupted.Id) == 2 && f.Downloads.GetValueOrDefault(next.Id) == 1
            && f.Downloads.GetValueOrDefault(removed.Id) == 1 && f.Library.Find(removed) is null,
            "the authoritative refresh revived removed or failed work, or lost the interrupted track");
        f.FailMediaTrack = null;
        await Call(f.Window, "RetryFailedDownloads");
        Assert(f.Downloads.GetValueOrDefault(failed.Id) == 2 && f.Library.Find(failed) is not null,
            "the old favorite was no longer available for explicit retry");
    }

    private static async Task Af060UnlikeAfterRemoval()
    {
        using var f = new Af060Fixture("unlike-after-removal", "102");
        var removed = Af060Row("101"); var interrupted = Af060Row("102"); var unliked = Af060Row("104");
        await Af061Ready(f, removed, unliked);
        f.HoldNextFullRefresh();
        await f.Like(interrupted);
        await f.MediaEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Call(f.Window, "DeleteOfflineTrackAsync", removed);
        Assert(f.Queue.CanResume && f.Downloads.GetValueOrDefault(interrupted.Id) == 1,
            "fixture did not leave automatic work waiting for verified membership after deletion");
        f.MediaReleased.TrySetResult();
        await f.Like(unliked, liked: false);
        await Af060WaitForDownloadedTrack(f, interrupted.Id, attempts: 2);
        Assert(!f.RefreshReleased.Task.IsCompleted && !f.Queue.CanResume && f.Library.Find(interrupted) is not null
            && f.Library.Find(removed) is null && f.Downloads.GetValueOrDefault(removed.Id) == 1
            && f.Downloads.GetValueOrDefault(unliked.Id) == 1 && !SaField<HashSet<string>>(f.Window, "_favoriteTrackIds").Contains(unliked.Id),
            "the confirmed unlike did not resume current work or revived an excluded favorite");
    }

    private static async Task Af060BulkUnlikeDuringRemoval()
    {
        using var f = new Af060Fixture("bulk-unlike-during-removal", "102") { HoldMediaCancellation = true };
        UlSet(f.Window, "_concurrentDownloads", 1);
        var removed = Af060Row("101"); var interrupted = Af060Row("102");
        var confirmed = Af060Row("104"); var failed = Af060Row("105");
        await Af061Ready(f, removed, confirmed, failed);
        f.AddServerFavorite(interrupted.Id);
        await f.Window.RefreshLibraryAsync(refreshMetadata: true);
        await f.MediaEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        f.HoldNextFullRefresh();
        var entries = new[] { confirmed, failed }.Select(row => f.Queue.Entries.Single(entry => entry.Track.Id == row.Id)).ToArray();
        f.HeldUnlikeFailure = failed.Id;
        var removal = Call(f.Window, "DeleteOfflineTrackAsync", removed);
        try
        {
            await f.MediaCancellationEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var unlike = Call(f.Window, "UnlikeDownloadsAsync", (object)entries);
            await f.UnlikeFailureEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            f.MediaReleased.TrySetResult();
            f.MediaCancellationReleased.TrySetResult();
            await removal.WaitAsync(TimeSpan.FromSeconds(5));
            Assert(f.Queue.CanResume && f.Downloads.GetValueOrDefault(interrupted.Id) == 1
                && entries[0].RemovedFromCollection && !entries[1].RemovedFromCollection,
                "removal resumed a prefix of an in-flight bulk unlike or lost the confirmed write");
            f.UnlikeFailureReleased.TrySetResult();
            await unlike.WaitAsync(TimeSpan.FromSeconds(5));
            await Af060WaitForDownloadedTrack(f, interrupted.Id, attempts: 2);
            var favorites = SaField<HashSet<string>>(f.Window, "_favoriteTrackIds");
            Assert(!f.RefreshReleased.Task.IsCompleted && !favorites.Contains(confirmed.Id) && favorites.Contains(failed.Id)
                && f.Library.Find(interrupted) is not null && f.Library.Find(removed) is null
                && f.Downloads.GetValueOrDefault(confirmed.Id) == 1 && f.Downloads.GetValueOrDefault(failed.Id) == 1,
                "the failed suffix lost the confirmed prefix, depended on refresh, or repeated saved work");
        }
        finally
        {
            f.MediaReleased.TrySetResult(); f.MediaCancellationReleased.TrySetResult(); f.UnlikeFailureReleased.TrySetResult();
            await removal.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    private static async Task Af060LikeDuringExplicitResume()
    {
        using var f = new Af060Fixture("confirmed-like-during-explicit-resume", "102");
        var interrupted = Af060Row("102"); var next = Af060Row("103");
        await f.Like(interrupted);
        await f.MediaEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var initial = SaField<Task>(f.Window, "_downloadTask");
        SaInvoke(f.Window, "PauseDownloadQueue");
        await initial.WaitAsync(TimeSpan.FromSeconds(5));
        Assert(f.Queue.CanResume && SaField<bool>(f.Window, "_downloadsAutoSuspended"), "the fixture did not apply an explicit pause");

        var resumed = Call(f.Window, "ResumeDownloadQueue");
        await Until(() => f.Downloads.GetValueOrDefault(interrupted.Id) == 2);
        await f.Like(next);
        f.RefreshReleased.TrySetResult();
        await f.Window.RefreshLibraryAsync(refreshMetadata: true);
        Assert(!resumed.IsCompleted && !f.Downloads.ContainsKey(next.Id)
            && SaField<PlaylistDetails?>(f.Window, "_pendingAutoFavorites")?.Tracks.Any(track => track.Id == next.Id) == true
            && SaField<Dictionary<string, TrackRow>>(f.Window, "_pendingConfirmedFavorites").ContainsKey(next.Id),
            "explicit Continue left the old automatic pause or started new work before the resumed batch released the queue");
        f.MediaReleased.TrySetResult();
        await resumed.WaitAsync(TimeSpan.FromSeconds(5));
        await Af061DownloadsIdle(f);
        Assert(f.Downloads.GetValueOrDefault(next.Id) == 1 && f.Downloads.GetValueOrDefault(interrupted.Id) == 2
            && f.Library.Find(next) is not null, "the new like required another refresh after resumed work finished");
    }

    private static async Task Af060RemovalDuringMixedWork()
    {
        using var f = new Af060Fixture("interrupted-removal-mixed", "102")
            { HoldMediaCancellation = true, HeldFollowUpMedia = "901", FailMediaTrack = "104" };
        UlSet(f.Window, "_concurrentDownloads", 3);
        var removed = Af060Row("101"); var automatic = Af060Row("102"); var next = Af060Row("103");
        var failed = Af060Row("104"); var manual = Af060Row("901");
        await f.Like(removed);
        await Af060WaitForDownloadedTrack(f, removed.Id);
        var original = f.StartManual(failed, manual);
        await f.FollowUpMediaEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Until(() => f.Queue.Entries.Any(entry => entry.Track.Id == failed.Id && entry.State == DownloadEntryState.Failed));
        await f.Like(automatic);
        SaInvoke(f.Window, "PauseDownloadQueueForAppUpdate");
        await original.WaitAsync(TimeSpan.FromSeconds(5));
        var mixed = Call(f.Window, "ResumeDownloadQueue");
        await f.MediaEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Until(() => f.Downloads.GetValueOrDefault(manual.Id) == 2);
        Assert(!SaField<bool>(f.Window, "_downloadIsAutomatic"), "fixture did not resume a mixed manual and automatic batch");
        var removal = Call(f.Window, "DeleteOfflineTrackAsync", removed);
        try { await f.MediaCancellationEntered.Task.WaitAsync(TimeSpan.FromSeconds(5)); }
        finally
        {
            f.MediaReleased.TrySetResult();
            f.MediaCancellationReleased.TrySetResult();
            await removal.WaitAsync(TimeSpan.FromSeconds(10));
            await mixed.WaitAsync(TimeSpan.FromSeconds(5));
        }
        await f.Like(next);
        f.RefreshReleased.TrySetResult();
        await f.Window.RefreshLibraryAsync(refreshMetadata: true);
        await Af061DownloadsIdle(f);
        Assert(f.Queue.CanRetry && f.Queue.CanResume && f.Downloads.GetValueOrDefault(manual.Id) == 2
            && f.Downloads.GetValueOrDefault(automatic.Id) == 1 && f.Downloads.GetValueOrDefault(next.Id) == 0,
            "removal granted automatic restart authority to a mixed batch or lost its paused or failed work");
    }

    private static async Task Af060ConfirmLikeDuringProtectedRemoval(string mode)
    {
        using var f = new Af060Fixture("confirmed-during-protected-removal-" + mode, mode == "paused" ? "104" : null);
        var old = Af060Row("101"); var next = Af060Row("102"); var independent = Af060Row("103");
        f.AddServerFavorite(old.Id);
        await f.Library.SaveAsync(old, new(new Uri("https://s1.vlx.yandex.net/101.mp3"), "mp3", 320), default);
        var playlist = new PlaylistDetails("local:favorites", "liked", "Мне нравится", "", 0, [old]);
        await f.Library.RememberPlaylistAsync(playlist, AudioQuality.Optimal, default);
        var queue = new DownloadQueueState(); queue.Replace([old], playlist.Title, favoritesBatch: true);
        queue.Entries[0].Complete(Af060Fixture.Media.Length);
        var store = new DownloadQueueStore(Path.Combine(f.DirectoryPath, "diagnostics"));
        UlSet(f.Window, "_downloadQueueStore", store);
        store.Save(DownloadQueueStore.Capture("12345", queue, AudioQuality.Optimal, true, playlist));
        var index = Path.Combine(f.Library.DirectoryPath, "playlists.index.json");
        var good = File.ReadAllBytes(index); File.WriteAllText(index, "{");
        await Call(f.Window, "RestoreDownloadQueueAsync", "12345");
        if (mode == "paused")
        {
            var work = f.StartManual(independent, Af060Row("104"));
            await f.MediaEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Until(() => f.Queue.Entries[0].State == DownloadEntryState.Completed);
            SaInvoke(f.Window, "PauseDownloadQueue");
            await work;
        }
        else await f.StartManual(independent);
        File.WriteAllBytes(index, good);

        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        f.Library.RemovalCheckpoint = phase =>
        {
            if (phase != "after-stage") return;
            entered.TrySetResult();
            release.Wait(TimeSpan.FromSeconds(10));
        };
        var remove = Call(f.Window, "DeleteOfflineTrackAsync", independent);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await f.Like(next);
            await f.Like(independent);
            var pending = SaField<Dictionary<string, TrackRow>>(f.Window, "_pendingConfirmedFavorites");
            Assert(pending.ContainsKey(next.Id) && pending.ContainsKey(independent.Id) && !f.Downloads.ContainsKey(next.Id),
                "an acknowledged like was discarded or started before protected deletion settled");
            if (mode == "disabled") Control<CheckBox>(f.Window, "AutoFavoritesCheckBox").IsChecked = false;
            if (mode == "account")
            {
                f.Window.SignOut();
                UlSet(f.Window, "_session", FakeSession);
                UlSet(f.Window, "_account", new Account("12345", "New generation", true));
                var newLike = Af060Row("105");
                SaField<HashSet<string>>(f.Window, "_favoriteTrackIds").Add(newLike.Id);
                SaInvoke(f.Window, "ScheduleConfirmedFavoriteDownload", newLike);
                Assert(pending.Count == 0, "a new session reused the previous session's removal admission");
            }
        }
        finally { release.Set(); }
        await remove.WaitAsync(TimeSpan.FromSeconds(5));
        await Call(f.Window, "RetryDownloadQueueRestoreAsync");
        if (mode == "active") await Af060WaitForDownloadedTrack(f, next.Id);
        Assert(!f.RefreshReleased.Task.IsCompleted && f.Downloads.GetValueOrDefault(next.Id) == (mode == "active" ? 1 : 0)
            && f.Downloads.GetValueOrDefault(independent.Id) == 1 && f.Library.Find(independent) is null
            && (mode != "paused" || f.Queue.CanResume), "repair redownloaded the removed ID or ignored the pause, preference, or account generation");
    }

    private static async Task Af060WaitForDownloadedTrack(Af060Fixture fixture, string id, int attempts = 1)
    {
        await Until(() => fixture.Downloads.GetValueOrDefault(id) >= attempts);
        if (SaField<Task?>(fixture.Window, "_downloadTask") is { } task) await task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    private static async Task Af061Ready(Af060Fixture fixture, params TrackRow[] rows)
    {
        foreach (var row in rows) await fixture.Like(row);
        fixture.RefreshReleased.TrySetResult();
        await Af061DownloadsIdle(fixture);
        Assert(fixture.Library.Read().Count == rows.Length, "ready fixture did not save the expected favorite snapshot");
    }

    private static async Task Af061DownloadsIdle(Af060Fixture fixture)
    {
        await Until(() => SaField<bool>(fixture.Window, "_favoritesSnapshotReady")
            && SaField<CancellationTokenSource?>(fixture.Window, "_libraryCts") is null);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        // A confirmed like and the completed collection refresh can own successive
        // batches. Await each published owner before opening its offline index.
        while (SaField<Task?>(fixture.Window, "_downloadTask") is { } download)
        {
            timeout.Token.ThrowIfCancellationRequested();
            await download.WaitAsync(timeout.Token);
        }
        Assert(SaField<CancellationTokenSource?>(fixture.Window, "_downloadCts") is null && !fixture.Queue.Running,
            "download task completion left an active fixture writer");
    }

    private const int Af060TrackSeconds = 180;
    private static TrackRow Af060Row(string id, bool available = true) => new() { Id = id, RequestId = id + ":91", Title = "Тестовый трек " + id,
        Artist = "Тестовый исполнитель", Album = "Тестовый альбом", AlbumId = "91", Available = available, DurationMs = Af060TrackSeconds * 1000 };

    private sealed class Af060Fixture : IDisposable
    {
        internal static readonly byte[] Media = SilentMp3Seconds(Af060TrackSeconds);
        internal MainWindow Window { get; }
        internal string DirectoryPath { get; }
        internal OfflineLibrary Library { get; }
        internal DownloadQueueState Queue => SaField<DownloadQueueState>(Window, "_downloadQueue");
        internal ConcurrentDictionary<string, int> Downloads { get; } = new(StringComparer.Ordinal);
        internal ConcurrentDictionary<string, int> Resolves { get; } = new(StringComparer.Ordinal);
        internal TaskCompletionSource RefreshEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource MetadataEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource UnlikeFailureEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource UnlikeFailureReleased { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource RefreshReleased { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource MediaEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource MediaReleased { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource MediaCancellationEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource MediaCancellationReleased { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource FollowUpMediaEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool HoldMediaCancellation;
        internal bool FailWrite, FailReadback, FailAudio, FailFullRefresh, HoldMetadataRefresh;
        internal string? FailMediaTrack;
        internal string? HeldUnlikeFailure;
        internal string? HeldFollowUpMedia;
        private readonly HashSet<string> _liked = new(StringComparer.Ordinal);
        private int _readback;

        internal Af060Fixture(string name, string? heldMedia = null)
        {
            var directory = DirectoryPath = Path.Combine(_directory, "auto-favorites-live-060-" + name); Directory.CreateDirectory(directory);
            var api = new MusicApi(new FakeHandler(async (request, token) =>
            {
                Assert(request.RequestUri!.Host == "api.music.yandex.net", "fixture API escaped its host"); var path = request.RequestUri.AbsolutePath;
                if (path == "/account/status") return Json(Profile);
                if (path is "/users/12345/likes/tracks/add-multiple" or "/users/12345/likes/tracks/remove")
                {
                    if (FailWrite) return Json("{}", HttpStatusCode.ServiceUnavailable);
                    var fields = Af060Form(await request.Content!.ReadAsStringAsync(token)); var id = fields["track-ids"].Split(':')[0];
                    if (id == HeldUnlikeFailure && path.EndsWith("/remove", StringComparison.Ordinal))
                    {
                        UnlikeFailureEntered.TrySetResult();
                        await UnlikeFailureReleased.Task.WaitAsync(token);
                        return Json("{}", HttpStatusCode.ServiceUnavailable);
                    }
                    if (path.EndsWith("add-multiple", StringComparison.Ordinal)) _liked.Add(id); else _liked.Remove(id);
                    Interlocked.Exchange(ref _readback, 1); return Json("{\"result\":{\"revision\":2}}");
                }
                if (path == "/users/12345/likes/tracks")
                {
                    if (Interlocked.Exchange(ref _readback, 0) == 1)
                    { if (FailReadback) return Json("{\"result\":{\"library\":{\"tracks\":[]}}}"); }
                    else
                    {
                        RefreshEntered.TrySetResult(); await RefreshReleased.Task.WaitAsync(token);
                        if (FailFullRefresh) return Json("{}", HttpStatusCode.ServiceUnavailable);
                    }
                    return Json(JsonSerializer.Serialize(new { result = new { library = new { tracks = _liked.Select(id => new { id, albumId = "91" }).ToArray() } } }));
                }
                if (path == "/tracks")
                {
                    if (HoldMetadataRefresh)
                    {
                        MetadataEntered.TrySetResult();
                        await Task.Delay(Timeout.InfiniteTimeSpan, token);
                    }
                    var fields = Af060Form(await request.Content!.ReadAsStringAsync(token));
                    return Json(JsonSerializer.Serialize(new { result = fields["track-ids"].Split(',').Select(key => new { id = key.Split(':')[0],
                        title = "Тестовый трек", available = true, durationMs = Af060TrackSeconds * 1000, albums = new[] { new { id = "91", title = "Альбом" } } }).ToArray() }));
                }
                if (path == "/get-file-info")
                {
                    var id = Af060Form(request.RequestUri.Query.TrimStart('?'))["trackId"].Split(':')[0]; Resolves.AddOrUpdate(id, 1, (_, count) => count + 1);
                    return Json("{\"result\":{\"downloadInfo\":{\"quality\":\"lossless\",\"codec\":\"mp3\",\"bitrate\":320,\"transport\":\"raw\",\"url\":\"https://s1.vlx.yandex.net/" + id + ".mp3\"}}}");
                }
                throw new InvalidOperationException("Unexpected auto-favorites fixture route: " + path);
            }));
            var media = new FakeHandler(async (request, token) =>
            {
                Assert(request.RequestUri!.Host == "s1.vlx.yandex.net" && request.Headers.Authorization is null, "fixture audio escaped the anonymous CDN boundary");
                var id = Path.GetFileNameWithoutExtension(request.RequestUri.AbsolutePath); Downloads.AddOrUpdate(id, 1, (_, count) => count + 1);
                if (id == heldMedia)
                {
                    MediaEntered.TrySetResult();
                    try { await MediaReleased.Task.WaitAsync(token); }
                    catch (OperationCanceledException) when (token.IsCancellationRequested && HoldMediaCancellation)
                    {
                        MediaCancellationEntered.TrySetResult();
                        await MediaCancellationReleased.Task;
                        throw;
                    }
                }
                if (id == HeldFollowUpMedia)
                {
                    FollowUpMediaEntered.TrySetResult();
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                }
                var status = FailAudio || id == FailMediaTrack ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK;
                return new HttpResponseMessage(status) { Content = new ByteArrayContent(Media) };
            });
            Library = new OfflineLibrary(Path.Combine(directory, "music"), new AudioBuffer(Path.Combine(directory, "buffer"), media));
            var player = new NativePlayer(new AudioBuffer(Path.Combine(directory, "player-buffer"),
                new FakeHandler((_, _) => throw new InvalidOperationException("auto-favorites fixture must not start playback"))));
            Window = new MainWindow(api: api, store: new SessionStore(Path.Combine(directory, "session")), restore: false,
                diagnosticsDirectory: Path.Combine(directory, "diagnostics"), offline: Library, player: player,
                preferences: new PreferencesStore(Path.Combine(directory, "preferences")));
            player.Volume = 0;
            UlSet(Window, "_session", FakeSession); UlSet(Window, "_account", new Account("12345", "Fixture", true));
            UlSet(Window, "_favoriteIdsKnown", true); UlSet(Window, "_favoritesAccountUid", "12345"); UlSet(Window, "_favoritesCacheRead", true);
            Control<CheckBox>(Window, "AutoFavoritesCheckBox").IsChecked = true;
        }
        internal Task Like(TrackRow row, bool liked = true) => Call(Window, "SetTrackLikeAsync", row, liked);
        internal void HoldNextFullRefresh() => RefreshReleased = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal void RemoveServerFavorite(string trackId) => _liked.Remove(trackId);
        internal void AddServerFavorite(string trackId) => _liked.Add(trackId);
        internal Task StartManual(params TrackRow[] rows) => Call(Window, "StartDownloadBatchAsync", rows, "Ручная загрузка", null!, false);
        public void Dispose()
        {
            RefreshReleased.TrySetResult(); MediaReleased.TrySetResult(); MediaCancellationReleased.TrySetResult();
            UnlikeFailureReleased.TrySetResult(); Window.Close();
        }
    }
    private static Dictionary<string, string> Af060Form(string form) => form.Split('&', StringSplitOptions.RemoveEmptyEntries)
        .Select(part => part.Split('=', 2)).ToDictionary(part => WebUtility.UrlDecode(part[0]), part => WebUtility.UrlDecode(part.ElementAtOrDefault(1) ?? ""), StringComparer.Ordinal);
}
