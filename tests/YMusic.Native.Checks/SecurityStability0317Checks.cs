using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using YMusic.Native;

internal static partial class Program
{
    private static async Task CheckSecurityStability0317()
    {
        await Run("Security stability0317: active offline and cache cleanup reject restart without changing preferences, then retry once", R4ActiveMaintenance);
        await Run("Security stability0317: cleanup starting during a pending download prevents restart and preference writes", R4MaintenanceDuringWait);
        await Run("Security stability0317: lifetime cancellation and closed window prevent a pending restart", R4CanceledRestart);
        await Run("Security stability0317: both update controls follow maintenance without replacing the settings draft", R4MaintenanceUpdateControls);
        await Run("Security stability0317: deleting one local fixture track disables restart then restores retry controls", R4SingleTrackMaintenanceControls);
        await Run("QaFixes061: update aborted by real cache maintenance keeps paused checkpoint and explicit continuation", Qf061A10UpdateMaintenance);
        await Run("QaFixes061: failed update apply and checkpoint replacement preserve recoverable downloads", Qf061A10UpdateErrors);
        await Run("QaFixes061: update close cancellation logout and same-account login preserve download isolation", Qf061A10UpdateBoundaries);
        await Run("QaFixes061: pending restore and superseding admission prevent stale update checkpoint writes", Qf061A10UpdateAdmission);
        await Run("Update apply: unavailable settings or playback storage defers restart and allows retry", UpdateApplySaveFailure);
        await Run("Update apply: failed restart with an idle queue keeps automatic favorites working", UpdateApplyIdleQueue);
        await Run("Update apply: same-account relogin and a replacement account can finish restoring before restart", UpdateApplyAccountRestore);
        await Run("Update apply: a scheduled check removes an obsolete startup offer and permits a new confirmed offer", UpdateApplyObsoleteOffer);
        await Run("Update apply: manual recheck retains a deferral for the same downloaded package and clears it when the offer changes", UpdateApplyRecheckedDeferral);
        await Run("Update exit: switching off a startup check in flight suppresses the invitation", UpdateOffDuringStartupCheck);
        await Run("Update exit: update and close wait for admitted session, favorites and offline writes", UpdateExitDrainsWrites);
        await Run("Update exit: close-to-tray bypasses saves and explicit Exit retries, cancels, or discards a failed save", CloseChoices);
        await Run("Update exit: recover an unavailable playback snapshot without replacing a corrupt snapshot", ClosePlaybackSnapshotRecovery);
        await Run("Update exit: canceled exit reschedules only unfinished work it interrupted and retains pending favorites", CloseResumesOnlyInterruptedDownloads);
        await Run("Update exit: retry a pending guest session save for update and a failed sign-out deletion on exit", ExitFlushesSessionChange);
    }

    private static async Task R4ActiveMaintenance()
    {
        foreach (var flag in new[] { "_offlineMaintenance", "_cacheMaintenance", "_clearOfflineConfirmationPending" })
        {
            using var f = new R4UpdateFixture("active-" + flag);
            R4Set(f.Window, flag, true);
            var applied = await R4Task<bool>(f.Window, "ApplyAppUpdateAsync", f.Updates);
            await R4Task(f.Window, "InstallStartupUpdateAsync");
            Assert(!applied && f.Backend.Applies == 0 && f.PreferencesUnchanged && R4Get<bool>(f.Window, "_autoFavoritesEnabled")
                && !R4Get<DownloadQueueState>(f.Window, "_downloadQueue").Suspended,
                "known cleanup allowed apply or changed preferences/download state");
            R4Set(f.Window, flag, false); R4Call(f.Window, "RefreshMaintenanceActions");
            await R4Task(f.Window, "InstallStartupUpdateAsync");
            await R4Task(f.Window, "InstallStartupUpdateAsync");
            Assert(f.Backend.Applies == 1 && R4Get<bool>(f.Window, "_startupUpdateRestartRequested")
                && R4Get<bool>(f.Window, "_autoFavoritesEnabled") && f.LastErrorType is null,
                $"retry did not apply exactly once or changed automatic downloads ({f.LastErrorType ?? "no recorded error"})");
        }
    }

    private static async Task R4MaintenanceDuringWait()
    {
        foreach (var flag in new[] { "_offlineMaintenance", "_cacheMaintenance", "_clearOfflineConfirmationPending" })
        {
            using var f = new R4UpdateFixture("late-" + flag);
            var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            R4Set(f.Window, "_downloadTask", pending.Task);
            var restart = R4Task(f.Window, "InstallStartupUpdateAsync");
            Assert(!restart.IsCompleted && f.Backend.Applies == 0 && f.PreferencesUnchanged, "restart did not wait without saving preferences");
            R4Set(f.Window, flag, true); R4Call(f.Window, "RefreshMaintenanceActions");
            pending.TrySetResult(); await restart.WaitAsync(TimeSpan.FromSeconds(5));
            Assert(f.Backend.Applies == 0 && f.PreferencesUnchanged && !R4Get<bool>(f.Window, "_startupUpdateRestartRequested")
                && R4Get<bool>(f.Window, "_autoFavoritesEnabled"), "cleanup that began during download wait was bypassed");
            R4Set(f.Window, flag, false); R4Set(f.Window, "_downloadTask", null); R4Call(f.Window, "RefreshMaintenanceActions");
            await R4Task(f.Window, "InstallStartupUpdateAsync"); await R4Task(f.Window, "InstallStartupUpdateAsync");
            Assert(f.Backend.Applies == 1 && f.LastErrorType is null,
                $"late-blocked update was not retriable exactly once ({f.LastErrorType ?? "no recorded error"})");
        }
    }

    private static async Task R4CanceledRestart()
    {
        foreach (var closed in new[] { false, true })
        {
            using var f = new R4UpdateFixture(closed ? "closed" : "lifetime");
            var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            R4Set(f.Window, "_downloadTask", pending.Task);
            var restart = R4Task(f.Window, "InstallStartupUpdateAsync");
            Assert(!restart.IsCompleted, "fixture did not enter pending restart wait");
            if (closed) { R4Set(f.Window, "_closed", true); pending.TrySetResult(); }
            else f.Lifetime.Cancel();
            await restart.WaitAsync(TimeSpan.FromSeconds(5)); pending.TrySetResult();
            Assert(f.Backend.Applies == 0 && f.PreferencesUnchanged && R4Get<bool>(f.Window, "_autoFavoritesEnabled"),
                "closed/canceled window applied an update or saved new preferences");
        }
    }

    private static async Task R4MaintenanceUpdateControls()
    {
        using var f = new R4UpdateFixture("controls");
        var panel = (StackPanel)R4Call(f.Window, "CreateUpdateSettingsPanelCore", f.Updates)!;
        var button = panel.Children.OfType<Button>().Single();
        var text = panel.Children.OfType<TextBlock>().Last();
        panel.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
        try
        {
            foreach (var flag in new[] { "_offlineMaintenance", "_cacheMaintenance", "_clearOfflineConfirmationPending" })
            {
                R4Set(f.Window, flag, true); R4Call(f.Window, "RefreshMaintenanceActions");
                Assert(!button.IsEnabled && !f.StartupButton.IsEnabled && text.Text.Contains("Дождитесь", StringComparison.Ordinal)
                    && f.StartupText.Text.Contains("повторите", StringComparison.Ordinal), "maintenance left an enabled update button or no explanation");
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await R4Task(f.Window, "InstallStartupUpdateAsync");
                Assert(f.Backend.Applies == 0 && f.PreferencesUnchanged, "disabled UI handler bypassed cleanup guard");
                R4Set(f.Window, flag, false); R4Call(f.Window, "RefreshMaintenanceActions");
                Assert(button.IsEnabled && f.StartupButton.IsEnabled && R4Get<bool>(f.Window, "_settingsOpen")
                    && R4Get<TextBox>(f.Window, "SettingsAccentHex").Text == "#20A0B0", "cleanup completion did not enable retry or replaced the settings draft");
            }
        }
        finally { panel.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent)); }
    }

    private static async Task R4SingleTrackMaintenanceControls()
    {
        using var f = new R4UpdateFixture("single-track");
        var music = Path.Combine(f.DirectoryPath, "Music"); Directory.CreateDirectory(music);
        var bytes = new byte[4096]; File.WriteAllBytes(Path.Combine(music, "fixture.mp3"), bytes);
        var item = new OfflineItem("fixture", "fixture:album", "Fixture", "Fixture", "Fixture", 3000, "fixture.mp3", "mp3", 320,
            bytes.Length, Convert.ToHexString(SHA256.HashData(bytes)), DateTimeOffset.UtcNow);
        File.WriteAllText(Path.Combine(music, "library.index.json"), JsonSerializer.Serialize(new[] { item }));
        using var library = new OfflineLibrary(music, new AudioBuffer(Path.Combine(f.DirectoryPath, "offline-buffer"), new R4NoNetwork()));
        var track = new TrackRow { Id = item.Id, RequestId = item.RequestId, Title = item.Title };
        var list = new ListView(); list.Items.Add(track); list.SelectedItem = track;
        R4Set(f.Window, "TracksList", list); R4Set(f.Window, "_offline", library);
        R4Set(f.Window, "_tracks", new TrackRows()); R4Set(f.Window, "_favoritesCache", new List<TrackRow>());
        R4Set(f.Window, "_waveTracks", new List<TrackRow>()); R4Set(f.Window, "_playbackQueue", new PlaybackQueue());
        R4Set(f.Window, "_catalogItems", new ObservableCollection<CatalogItem>()); R4Set(f.Window, "_homeSections", Array.Empty<HomeSection>());
        R4Set(f.Window, "_deletedUntilFavoritesRefresh", new HashSet<string>(StringComparer.Ordinal));
        foreach (var name in new[] { "StatusText", "WaveStatus", "HomeStatus", "ArtistStatus" }) R4Set(f.Window, name, new TextBlock());
        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        R4Set(f.Window, "_downloadTask", pending.Task);
        R4Call(f.Window, "DeleteOfflineTrack_Click", f.Window, new RoutedEventArgs());
        Assert(R4Get<bool>(f.Window, "_offlineMaintenance") && !f.StartupButton.IsEnabled,
            "single-track deletion did not immediately disable update restart");
        R4Call(f.Window, "DeleteOfflineTrack_Click", f.Window, new RoutedEventArgs()); // Active-operation early return.
        Assert(!await R4Task<bool>(f.Window, "ApplyAppUpdateAsync", f.Updates), "single-track deletion allowed SDK apply");
        pending.TrySetResult();
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (R4Get<bool>(f.Window, "_offlineMaintenance"))
        {
            Assert(DateTime.UtcNow < deadline, "single-track fixture did not complete"); await Task.Delay(10);
        }
        Assert(library.Read().Count == 0 && !File.Exists(Path.Combine(music, "fixture.mp3")) && f.StartupButton.IsEnabled
            && R4Get<bool>(f.Window, "_settingsOpen") && R4Get<TextBox>(f.Window, "SettingsAccentHex").Text == "#20A0B0"
            && f.Backend.Applies == 0 && f.PreferencesUnchanged,
            "single-track deletion failed or did not restore update retry without changing the settings draft");
    }

    private static FieldInfo R4Field(object target, string name) => target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static void R4Set(object target, string name, object? value) => R4Field(target, name).SetValue(target, value);
    private static T R4Get<T>(object target, string name) => (T)R4Field(target, name).GetValue(target)!;
    private static object? R4Call(object target, string name, params object[] args) => target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, args);
    private static Task R4Task(object target, string name, params object[] args) => (Task)R4Call(target, name, args)!;
    private static Task<T> R4Task<T>(object target, string name, params object[] args) => (Task<T>)R4Call(target, name, args)!;

    private static async Task Qf061A10UpdateMaintenance()
    {
        using var f = new Qf061A10UpdateFixture("maintenance");
        var previousCache = Artwork.Cache;
        using var cache = new ArtworkCache(Path.Combine(f.DirectoryPath, "artwork"), new R4NoNetwork());
        var gate = R4Get<SemaphoreSlim>(cache, "_diskGate");
        await gate.WaitAsync();
        Task? cleanup = null;
        try
        {
            Artwork.Cache = cache;
            var update = f.BeginUpdate();
            Assert(!update.IsCompleted && R4Get<bool>(f.Window, "_appUpdateActionInProgress")
                && Control<Button>(f.Window, "SettingsClearCache").IsEnabled,
                "update did not enter its worker wait or blocked the real cache entry");
            cleanup = R4Task(f.Window, "ClearCachesAsync");
            Assert(R4Get<bool>(f.Window, "_cacheMaintenance") && !cleanup.IsCompleted,
                "real cache cleanup did not overlap the pending update");
            f.FinishWorker(); await update.WaitAsync(TimeSpan.FromSeconds(5));
            Assert(f.Backend.Applies == 0 && !R4Get<bool>(f.Window, "_startupUpdateRestartRequested"),
                "late maintenance allowed an update apply");
            f.AssertPausedCheckpoint();
        }
        finally
        {
            gate.Release();
            try { if (cleanup is not null) await cleanup.WaitAsync(TimeSpan.FromSeconds(5)); }
            finally { Artwork.Cache = previousCache; }
        }
        f.AssertPausedCheckpoint();
        Assert(f.Queue.CanResume && !f.Queue.Running && R4Get<bool>(f.Window, "_downloadsAutoSuspended")
            && R4Get<Task?>(f.Window, "_downloadTask") is null && f.Requests == 0,
            "aborted update resumed downloads automatically or disabled explicit continuation");
        await R4Task(f.Window, "ResumeDownloadQueue");
        Assert(f.Queue.Entries.All(entry => entry.State == DownloadEntryState.Unavailable) && !f.Queue.Running && f.Requests == 0,
            "explicit continuation did not process the paused fixture queue without network");
    }

    private static async Task Qf061A10UpdateErrors()
    {
        using (var f = new Qf061A10UpdateFixture("apply-error"))
        {
            f.Backend.FailApply = true;
            var update = f.BeginUpdate(); f.FinishWorker();
            await update.WaitAsync(TimeSpan.FromSeconds(5));
            f.AssertPausedCheckpoint();
            Assert(f.Backend.Applies == 1 && !R4Get<bool>(f.Window, "_startupUpdateRestartRequested") && f.Queue.CanResume
                && R4Get<bool>(f.Window, "_downloadsAutoSuspended") && f.Requests == 0,
                "failed fake apply discarded or automatically resumed the paused queue");
        }
        using (var f = new Qf061A10UpdateFixture("checkpoint-error"))
        {
            var before = File.ReadAllBytes(f.SnapshotPath);
            using (var locked = new FileStream(f.SnapshotPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var update = f.BeginUpdate();
                try { await update.WaitAsync(TimeSpan.FromSeconds(5)); }
                finally { f.FinishWorker(); }
                Assert(f.Backend.Applies == 0 && before.SequenceEqual(File.ReadAllBytes(f.SnapshotPath))
                    && R4Get<Diagnostics>(f.Window, "_diagnostics").LastErrorType?.StartsWith("DownloadSave:", StringComparison.Ordinal) == true,
                    "failed checkpoint replacement allowed apply or damaged the previous snapshot");
            }
            f.AssertPausedCheckpoint();
            Assert(f.Queue.CanResume && f.Requests == 0, "checkpoint failure stranded the live paused queue");
        }
    }

    private static async Task Qf061A10UpdateBoundaries()
    {
        foreach (var action in new[] { "close", "cancel", "logout", "same-account" })
        {
            using var f = new Qf061A10UpdateFixture(action);
            var update = f.BeginUpdate();
            Assert(!update.IsCompleted, action + ": fixture did not enter the update wait");
            if (action == "close") f.Window.Close();
            else if (action == "cancel") R4Get<CancellationTokenSource>(f.Window, "_lifetime").Cancel();
            else
            {
                f.Window.SignOut();
                if (action == "same-account")
                {
                    R4Set(f.Window, "_account", new Account(Qf061A10UpdateFixture.AccountUid, "Fixture relogin", false));
                    R4Set(f.Window, "_session", FakeSession);
                }
            }
            f.FinishWorker(); await update.WaitAsync(TimeSpan.FromSeconds(5));
            Assert(f.Backend.Applies == 0 && f.Requests == 0, action + ": stale update applied or accessed the service");
            if (action is "close" or "cancel") f.AssertPausedCheckpoint();
            else Assert(!File.Exists(f.SnapshotPath) && f.Queue.Suspended
                && R4Get<bool>(f.Window, "_downloadPersistenceInvalidated"),
                action + ": old account checkpoint or resumable entries were revived");
        }
    }

    private static async Task Qf061A10UpdateAdmission()
    {
        using var f = new Qf061A10UpdateFixture("new-admission");
        var update = f.BeginUpdate();
        f.FinishWorker();
        // Worker completion queues the update continuation. A new user batch can
        // be admitted before that continuation runs on this same dispatcher.
        f.Queue.Replace([new TrackRow { Id = "replacement", RequestId = "replacement", Title = "New queue", Available = false }], "New queue");
        R4Call(f.Window, "BeginDownloadQueuePersistence");
        f.Store.Save(DownloadQueueStore.Capture(Qf061A10UpdateFixture.AccountUid, f.Queue, AudioQuality.Optimal, false, null));
        var replacement = File.ReadAllBytes(f.SnapshotPath);
        await update.WaitAsync(TimeSpan.FromSeconds(5));
        Assert(f.Backend.Applies == 0 && replacement.SequenceEqual(File.ReadAllBytes(f.SnapshotPath))
            && f.Store.Load(Qf061A10UpdateFixture.AccountUid)?.Entries.Single().Track.Id == "replacement",
            "superseded update applied or rewrote the newly admitted download checkpoint");

        using var restoring = new Qf061A10UpdateFixture("pending-restore");
        var snapshot = restoring.Store.Load(Qf061A10UpdateFixture.AccountUid)!;
        restoring.Queue.EndRun(true); restoring.Queue.Replace([], "Restoring");
        R4Set(restoring.Window, "_downloadQueueAccountUid", null); R4Set(restoring.Window, "_downloadPersistenceAccountUid", null);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var read = new TaskCompletionSource<DownloadQueueSnapshot?>(TaskCreationOptions.RunContinuationsAsynchronously);
        restoring.Window.DownloadQueueRestoreReadAsync = _ => { entered.TrySetResult(); return read.Task; };
        var restore = R4Task(restoring.Window, "RestoreDownloadQueueAsync", Qf061A10UpdateFixture.AccountUid);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var admission = R4Get<long>(restoring.Window, "_downloadQueueAdmissionRevision");
            await R4Task(restoring.Window, "InstallStartupUpdateAsync").WaitAsync(TimeSpan.FromSeconds(5));
            Assert(restoring.Backend.Applies == 0 && !restore.IsCompleted
                && R4Get<long>(restoring.Window, "_downloadQueueAdmissionRevision") == admission,
                "update superseded an in-flight initial restore before it could load the saved queue");
        }
        finally
        {
            read.TrySetResult(snapshot);
            await restore.WaitAsync(TimeSpan.FromSeconds(5));
        }
        restoring.AssertPausedCheckpoint();
        Assert(restoring.Queue.CanResume && !restoring.Queue.Running && restoring.Requests == 0,
            "rejected update left the initial restored queue unloaded or started its downloads");
    }

    private static async Task UpdateApplySaveFailure()
    {
        using (var f = new Qf061A10UpdateFixture("settings-save-failure"))
        {
            f.Queue.EndRun(false);
            var preferences = R4Get<PreferencesStore>(f.Window, "_preferenceStore");
            var before = preferences.Load().PlaybackSpeed;
            var panel = (StackPanel)R4Call(f.Window, "CreateUpdateSettingsPanel")!;
            var button = panel.Children.OfType<Button>().Single();
            var status = panel.Children.OfType<TextBlock>().Last();
            panel.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
            try
            {
                using (var locked = new FileStream(preferences.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    R4Get<NativePlayer>(f.Window, "_player").SetSpeed(1.5);
                    button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Assert(f.Backend.Applies == 0 && button.IsEnabled && preferences.Load().PlaybackSpeed == before
                        && status.Text.Contains("Не удалось сохранить настройки", StringComparison.Ordinal),
                        "settings write failure restarted the app or did not explain how to retry");
                }
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert(f.Backend.Applies == 1 && !button.IsEnabled && preferences.Load().PlaybackSpeed == 1.5,
                    "settings retry did not save and request exactly one restart");
            }
            finally { panel.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent)); }
        }

        using (var f = new Qf061A10UpdateFixture("playback-save-failure"))
        {
            f.Queue.EndRun(false);
            var store = R4Get<PlaybackStateStore>(f.Window, "_playbackStateStore");
            var queue = R4Get<PlaybackQueue>(f.Window, "_playbackQueue");
            var track = new TrackRow { Id = "1", RequestId = "1", Title = "Fixture current", DurationMs = 120000 };
            queue.SetItems([track], track);
            R4Set(f.Window, "_current", track);
            Assert((bool)R4Call(f.Window, "TrySavePlaybackStateForExit")!, "could not write the initial playback checkpoint");
            using (var locked = new FileStream(Path.Combine(f.DirectoryPath, PlaybackStateStore.FileName), FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                queue.Enqueue(new TrackRow { Id = "2", RequestId = "2", Title = "Fixture added", DurationMs = 120000 });
                await R4Task(f.Window, "InstallStartupUpdateAsync");
                Assert(f.Backend.Applies == 0 && store.Load(Qf061A10UpdateFixture.AccountUid)?.Tracks.Count == 1
                    && R4Get<string>(f.Window, "_appUpdateFailure").Contains("очередь воспроизведения", StringComparison.Ordinal),
                    "playback write failure restarted the app or did not explain the deferral");
            }
            await R4Task(f.Window, "InstallStartupUpdateAsync");
            Assert(f.Backend.Applies == 1 && store.Load(Qf061A10UpdateFixture.AccountUid)?.Tracks.Count == 2,
                "playback retry did not save the new queue before restarting");
        }
    }

    private static async Task UpdateApplyIdleQueue()
    {
        foreach (var completed in new[] { false, true })
        {
            using var f = new Qf061A10UpdateFixture(completed ? "completed-apply-failure" : "empty-apply-failure");
            f.Queue.EndRun(false);
            f.Queue.Replace(completed ? [new TrackRow { Id = "1", RequestId = "1", Title = "Downloaded fixture" }] : [], "Idle fixture");
            foreach (var entry in f.Queue.Entries) entry.SetState(DownloadEntryState.Completed);
            f.Queue.EndRun(false);
            var notice = f.Queue.Notice;
            f.Backend.FailApply = true;
            await R4Task(f.Window, "InstallStartupUpdateAsync");
            Assert(f.Backend.Applies == 1 && !R4Get<bool>(f.Window, "_downloadsAutoSuspended") && f.Queue.Notice == notice
                && !f.Queue.CanResume && !f.Queue.CanRetry, "idle queue remained suspended after a failed restart");

            var track = new TrackRow { Id = "2", RequestId = "2", Title = "New favorite", Available = false };
            R4Get<HashSet<string>>(f.Window, "_favoriteTrackIds").Add(track.Id);
            R4Call(f.Window, "ScheduleConfirmedFavoriteDownload", track);
            var work = R4Get<Task?>(f.Window, "_downloadTask");
            Assert(work is not null, "confirmed favorite was blocked after the failed restart");
            await work!.WaitAsync(TimeSpan.FromSeconds(5));
            Assert(f.Queue.Entries.Single().Track.Id == track.Id && f.Requests == 0,
                "confirmed favorite did not enter the isolated download batch");
        }
    }

    private static async Task UpdateApplyAccountRestore()
    {
        foreach (var sameAccount in new[] { true, false })
        {
            using var f = new Qf061A10UpdateFixture(sameAccount ? "same-account-restore" : "replace-account-restore");
            if (sameAccount) f.Queue.EndRun(true); else f.StartWorker();
            f.Window.SignOut();
            var accountUid = sameAccount ? Qf061A10UpdateFixture.AccountUid : "replacement-account";
            R4Set(f.Window, "_account", new Account(accountUid, "Fixture relogin", false));
            R4Set(f.Window, "_session", FakeSession);
            var readEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var read = new TaskCompletionSource<DownloadQueueSnapshot?>(TaskCreationOptions.RunContinuationsAsynchronously);
            f.Window.DownloadQueueRestoreReadAsync = _ => { readEntered.TrySetResult(); return read.Task; };
            var restore = R4Task(f.Window, "RestoreDownloadQueueAsync", accountUid);
            try
            {
                Assert(readEntered.Task.IsCompleted == sameAccount && !restore.IsCompleted,
                    "fixture did not hold the expected stage of account restore");
                var admission = R4Get<long>(f.Window, "_downloadQueueAdmissionRevision");
                await R4Task(f.Window, "InstallStartupUpdateAsync").WaitAsync(TimeSpan.FromSeconds(5));
                Assert(f.Backend.Applies == 0 && R4Get<long>(f.Window, "_downloadQueueAdmissionRevision") == admission
                    && R4Get<string>(f.Window, "_appUpdateFailure").Contains("восстановления очереди", StringComparison.Ordinal),
                    "update bypassed an in-flight account restore");
            }
            finally
            {
                f.FinishWorker(); read.TrySetResult(null);
                await restore.WaitAsync(TimeSpan.FromSeconds(5));
            }
            Assert(f.Queue.Entries.Count == 0 && !f.Queue.Suspended && R4Get<string>(f.Window, "_downloadQueueAccountUid") == accountUid
                && R4Get<int>(f.Window, "_downloadQueueAccountGeneration") == R4Get<int>(f.Window, "_accountGeneration"),
                "the new account retained the previous admission's suspended queue");
        }
    }

    private static async Task UpdateApplyObsoleteOffer()
    {
        foreach (var failedCheck in new[] { false, true })
        {
            using var f = new SaFixture("obsolete-update-offer-" + failedCheck);
            var window = f.Window;
            var backend = new Su016Backend();
            var updates = Su016Service(window, "obsolete-offer-" + failedCheck, backend);
            var now = new DateTimeOffset(2028, 4, 1, 1, 0, 0, TimeSpan.Zero);
            R4Set(window, "_updateClock", (Func<DateTimeOffset>)(() => now));
            await Call(window, "CheckForAppUpdatesAsync");
            var prompt = Su016Find<Border>(window, "StartupUpdatePrompt")!;
            var button = Su016Find<Button>(window, "StartupUpdateButton")!;
            Assert(prompt.Visibility == Visibility.Visible && button.IsEnabled, "fixture startup offer missing");
            R4Set(window, "_updateCheckFrequency", UpdateCheckFrequency.Hourly);
            backend.Version = null;
            backend.CheckFailures = failedCheck ? 1 : 0;
            now = now.AddHours(1);
            await Call(window, "RunScheduledUpdateCheckAsync");
            Assert(updates.State.Status == (failedCheck ? AppUpdateStatus.Error : AppUpdateStatus.NoUpdate)
                && prompt.Visibility == Visibility.Collapsed && !button.IsEnabled && backend.Applies == 0,
                "obsolete startup offer remained actionable");
            now = now.AddHours(1);
            backend.Version = "9.2.0";
            backend.HoldCheck = true;
            backend.CheckEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
            var check = Call(window, "RunScheduledUpdateCheckAsync");
            await backend.CheckEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert(updates.State.Status == AppUpdateStatus.Checking && prompt.Visibility == Visibility.Collapsed,
                "checking reopened the obsolete offer before another version was confirmed");
            backend.CheckRelease.TrySetResult();
            await check.WaitAsync(TimeSpan.FromSeconds(5));
            Assert(prompt.Visibility == Visibility.Visible && button.IsEnabled && updates.State.AvailableVersion == "9.2.0",
                "newly confirmed release did not restore the startup offer");
        }
    }

    private static async Task UpdateApplyRecheckedDeferral()
    {
        foreach (var (offered, pending, expected) in new (string?, string?, AppUpdateStatus)[]
        {
            (null, "9.1.0", AppUpdateStatus.NoUpdate),
            (null, "9.1.0", AppUpdateStatus.Error),
            ("9.2.0", "9.1.0", AppUpdateStatus.Available),
            ("9.2.0", "9.2.0", AppUpdateStatus.Downloaded)
        })
        {
            using var f = new Qf061A10UpdateFixture("recheck-deferral-" + expected);
            f.Queue.EndRun(false);
            await Call(f.Window, "CheckForAppUpdatesAsync");
            var prompt = R4Get<Border>(f.Window, "_startupUpdatePrompt");
            var install = R4Get<Button>(f.Window, "_startupUpdateButton");
            var startupText = R4Get<TextBlock>(f.Window, "_startupUpdateText");
            var panel = (StackPanel)R4Call(f.Window, "CreateUpdateSettingsPanel")!;
            var recheck = SaVisuals<Button>(panel).Single(button => button.Name == "AppUpdateCheckButton");
            var settingsText = panel.Children.OfType<TextBlock>().Last();
            panel.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
            try
            {
                var preferences = R4Get<PreferencesStore>(f.Window, "_preferenceStore");
                using var locked = new FileStream(preferences.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                SaClick(install);
                await Until(() => !R4Get<bool>(f.Window, "_appUpdateActionInProgress"));
                const string deferral = "Не удалось сохранить настройки.";
                Assert(f.Backend.Applies == 0 && startupText.Text.Contains(deferral, StringComparison.Ordinal) && recheck.IsEnabled,
                    "fixture did not defer application of the downloaded package");
                var checks = f.Backend.Checks;
                SaClick(recheck);
                await Until(() => f.Backend.Checks == checks + 1 && !R4Get<bool>(f.Window, "_updateCheckInProgress"));
                Assert(prompt.Visibility == Visibility.Visible && startupText.Text.Contains(deferral, StringComparison.Ordinal)
                    && settingsText.Text.Contains(deferral, StringComparison.Ordinal), "reconfirming the same downloaded package lost its deferral");

                f.Backend.CheckError = new HttpRequestException("Temporary fixture failure");
                SaClick(recheck);
                await Until(() => f.Backend.Checks == checks + 2 && !R4Get<bool>(f.Window, "_updateCheckInProgress"));
                Assert(f.Updates.State is { Status: AppUpdateStatus.Downloaded, AvailableVersion: "9.1.0", CheckSucceeded: false }
                    && prompt.Visibility == Visibility.Visible && startupText.Text.Contains(deferral, StringComparison.Ordinal),
                    "temporary check failure discarded the deferral for the still downloaded package");

                f.Backend.OfferedVersion = offered;
                f.Backend.PendingVersion = pending;
                f.Backend.CheckError = expected == AppUpdateStatus.Error ? new InvalidDataException("Invalid fixture release metadata") : null;
                SaClick(recheck);
                await Until(() => f.Backend.Checks == checks + 3 && !R4Get<bool>(f.Window, "_updateCheckInProgress")
                    && f.Updates.State.Status == expected);
                Assert(!settingsText.Text.Contains(deferral, StringComparison.Ordinal) && f.Backend.Applies == 0,
                    "the previous package's deferral remained in Settings");
                Assert(offered is null ? prompt.Visibility == Visibility.Collapsed && !install.IsEnabled
                    : prompt.Visibility == Visibility.Visible && install.IsEnabled && startupText.Text.Contains(offered, StringComparison.Ordinal),
                    "startup still offered the withdrawn package or displayed the wrong version");
            }
            finally { panel.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent)); }
        }
    }

    private static async Task UpdateOffDuringStartupCheck()
    {
        using var f = new SaFixture("off-during-startup-check");
        var backend = new Su016Backend { HoldCheck = true };
        var service = Su016Service(f.Window, "off-during-startup-check", backend);
        var startup = Call(f.Window, "CheckForAppUpdatesAsync");
        try
        {
            await backend.CheckEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            SaClick(Control<Button>(f.Window, "SettingsButton"));
            R4Get<ComboBox>(f.Window, "_updateFrequencyCombo").SelectedValue = UpdateCheckFrequency.Off;
            Assert(R4Get<UpdateCheckFrequency>(f.Window, "_updateCheckFrequency") == UpdateCheckFrequency.Off,
                "fixture did not disable automatic checks in Settings");
        }
        finally { backend.CheckRelease.TrySetResult(); }
        await startup.WaitAsync(TimeSpan.FromSeconds(5));
        Assert(service.State.Status == AppUpdateStatus.Available && backend.Checks == 1 && backend.Downloads == 0 && backend.Applies == 0
            && Su016Find<Border>(f.Window, "StartupUpdatePrompt") is null, "disabled startup check still showed the invitation");
    }

    private static async Task UpdateExitDrainsWrites()
    {
        foreach (var update in new[] { true, false })
        {
            using var f = new Qf061A10UpdateFixture(update ? "update-drain" : "close-drain");
            var sessionWrite = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var favoritesWrite = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            R4Set(f.Window, "_sessionRefreshWrite", sessionWrite.Task);
            R4Set(f.Window, "_favoritesCacheSaveTask", favoritesWrite.Task);
            Task? restart = null;
            try
            {
                if (update) restart = f.BeginUpdate();
                else
                {
                    ShowCloseFixture(f.Window);
                    f.Window.CloseSaveFailurePrompt = _ => throw new InvalidOperationException("successful close displayed a dialog");
                    f.StartWorker();
                    f.Window.Close();
                }
                var preparation = update ? "_appUpdatePreparingRestart" : "_closeCommitInProgress";
                await Until(() => R4Get<bool>(f.Window, preparation)
                    && R4Get<CancellationTokenSource?>(f.Window, "_downloadCts")?.IsCancellationRequested == true);
                Assert(f.Backend.Applies == 0 && !R4Get<bool>(f.Window, "_closed"), "exit skipped the pending downloader");
                f.FinishWorker();
                await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);
                Assert(f.Backend.Applies == 0 && !R4Get<bool>(f.Window, "_closed") && R4Get<bool>(f.Window, preparation),
                    "exit skipped a pending session write after the downloader settled");
                sessionWrite.TrySetResult();
                await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);
                Assert(f.Backend.Applies == 0 && !R4Get<bool>(f.Window, "_closed") && R4Get<bool>(f.Window, preparation),
                    "exit skipped the pending favorites write after the session settled");
                favoritesWrite.TrySetResult();
                if (restart is not null) await restart.WaitAsync(TimeSpan.FromSeconds(5));
                else await Until(() => R4Get<bool>(f.Window, "_closed"));
                Assert(update ? f.Backend.Applies == 1 : f.Backend.Applies == 0, "exit did not finish through its original route");
                f.AssertPausedCheckpoint();
            }
            finally
            {
                sessionWrite.TrySetResult();
                favoritesWrite.TrySetResult();
                f.Window.CloseSaveFailurePrompt = null;
            }
        }
    }

    private static void ShowCloseFixture(MainWindow window)
    {
        window.Left = -12000; window.Top = -12000;
        window.ShowActivated = false; window.ShowInTaskbar = false; window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Show();
    }

    private static async Task CloseChoices()
    {
        foreach (var decision in new[] { MessageBoxResult.Cancel, MessageBoxResult.Yes, MessageBoxResult.No })
        {
            using var f = new Qf061A10UpdateFixture("close-" + decision);
            f.Queue.EndRun(false);
            ShowCloseFixture(f.Window);
            R4Get<DesktopShell?>(f.Window, "_shell")?.Dispose();
            using var shell = new DesktopShell(f.Window, _ => true) { MinimizeToTray = true };
            R4Set(f.Window, "_shell", shell);
            var store = R4Get<PreferencesStore>(f.Window, "_preferenceStore");
            var old = store.Load().PlaybackSpeed;
            using var locked = new FileStream(store.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            R4Get<NativePlayer>(f.Window, "_player").SetSpeed(1.5);
            var prompts = 0;
            f.Window.CloseSaveFailurePrompt = failure =>
            {
                prompts++;
                Assert(failure.Contains("сохранить настройки", StringComparison.Ordinal), "close reported the wrong write failure");
                if (decision == MessageBoxResult.Yes) locked.Dispose();
                return decision;
            };
            try
            {
                f.Window.Close();
                Assert(!f.Window.IsVisible && !R4Get<bool>(f.Window, "_closeCommitInProgress") && prompts == 0,
                    "close-to-tray started saving the window state");
                shell.ShowMainWindow();
                R4Call(shell, "Dispatch", ShellAction.Exit);
                await Until(() => !R4Get<bool>(f.Window, "_closeCommitInProgress"));
                Assert(prompts == 1 && store.Load().PlaybackSpeed == (decision == MessageBoxResult.Yes ? 1.5 : old),
                    "close did not honor its retry, cancel, or discard choice");
                Assert(R4Get<bool>(f.Window, "_closed") == (decision != MessageBoxResult.Cancel), "close completed despite cancel or failed to exit");
                if (decision == MessageBoxResult.Cancel)
                {
                    f.Window.Close();
                    Assert(!f.Window.IsVisible && !R4Get<bool>(f.Window, "_closed") && prompts == 1,
                        "a canceled tray Exit changed the next close-to-tray action");
                }
            }
            finally
            {
                locked.Dispose();
                f.Window.CloseSaveFailurePrompt = null;
                if (!R4Get<bool>(f.Window, "_closed")) { shell.MinimizeToTray = false; f.Window.Close(); }
            }
        }
    }

    private static async Task CloseResumesOnlyInterruptedDownloads()
    {
        foreach (var alreadyPaused in new[] { false, true })
        {
            using var f = new Qf061A10UpdateFixture("cancel-close-resume-" + alreadyPaused);
            f.Queue.EndRun(false);
            var rows = Enumerable.Range(1, 4).Select(index => new TrackRow
            { Id = index.ToString(), RequestId = index.ToString(), Title = "Fixture", Available = false }).ToArray();
            f.Queue.Replace(rows, "Resume fixture");
            f.Queue.BeginRun(false);
            var active = f.Queue.Entries[0]; active.Begin();
            var queued = f.Queue.Entries[1];
            var paused = f.Queue.Entries[2]; paused.SetState(DownloadEntryState.Paused);
            var failed = f.Queue.Entries[3]; failed.SetState(DownloadEntryState.Failed);
            f.StartWorker();
            var oldCts = R4Get<CancellationTokenSource>(f.Window, "_downloadCts");
            if (alreadyPaused) SaInvoke(f.Window, "PauseDownloadQueue");
            var pendingFavorite = new TrackRow { Id = "5", RequestId = "5", Title = "Pending favorite", Available = false };
            var snapshot = new PlaylistDetails("local:favorites", "liked", "Мне нравится", "", 0, [pendingFavorite]);
            if (!alreadyPaused) R4Set(f.Window, "_pendingAutoFavorites", snapshot);

            ShowCloseFixture(f.Window);
            var preferences = R4Get<PreferencesStore>(f.Window, "_preferenceStore");
            using var locked = new FileStream(preferences.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var prompts = 0;
            f.Window.CloseSaveFailurePrompt = _ => { prompts++; return MessageBoxResult.Cancel; };
            try
            {
                f.Window.Close();
                await Until(() => oldCts.IsCancellationRequested && R4Get<bool>(f.Window, "_closeCommitInProgress"));
                f.FinishWorker();
                await Until(() => !R4Get<bool>(f.Window, "_closeCommitInProgress"));
                if (R4Get<Task?>(f.Window, "_downloadTask") is { } resumed) await resumed.WaitAsync(TimeSpan.FromSeconds(5));
                var expected = alreadyPaused ? DownloadEntryState.Paused : DownloadEntryState.Unavailable;
                Assert(prompts == 1 && !R4Get<bool>(f.Window, "_closed") && active.State == expected && queued.State == expected
                    && paused.State == DownloadEntryState.Paused && failed.State == DownloadEntryState.Failed
                    && !f.Queue.Running && f.Requests == 0, "canceled close dropped active work, started a second worker, or removed an earlier pause");
                Assert(alreadyPaused || ReferenceEquals(R4Get<PlaylistDetails?>(f.Window, "_pendingAutoFavorites"), snapshot),
                    "canceled close discarded the pending favorites snapshot");
            }
            finally { f.Window.CloseSaveFailurePrompt = null; }
        }
    }

    private static async Task ExitFlushesSessionChange()
    {
        using (var f = new Qf061A10UpdateFixture("guest-session-update"))
        {
            f.Queue.EndRun(false);
            R4Set(f.Window, "_account", null); R4Set(f.Window, "_session", null);
            var store = R4Get<SessionStore>(f.Window, "_store");
            store.Save(FakeSession);
            var observed = store.Load();
            Assert(observed == FakeSession, "fixture did not verify the saved guest session");
            var replacement = FakeSession with { AccessToken = "fake-rotated-guest-token-DO-NOT-USE" };
            R4Set(f.Window, "_unactivatedSession", replacement);
            using (var locked = new FileStream(Path.Combine(f.DirectoryPath, "session.dpapi"), FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                Assert(!(bool)R4Call(f.Window, "TrySaveSession", replacement, observed!)!, "fixture did not block the guest session write");
                await R4Task(f.Window, "InstallStartupUpdateAsync");
                Assert(f.Backend.Applies == 0 && R4Get<string>(f.Window, "_appUpdateFailure").Contains("сохранить обновлённый вход", StringComparison.Ordinal),
                    "update ignored an unwritten rotation without an active account");
            }
            await R4Task(f.Window, "InstallStartupUpdateAsync");
            Assert(f.Backend.Applies == 1 && store.Load()?.AccessToken == replacement.AccessToken,
                "retry applied without persisting the pending guest session");
        }

        using (var f = new Qf061A10UpdateFixture("signed-out-session-close"))
        {
            f.Queue.EndRun(false);
            var contentReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            f.Window.ContentRendered += (_, _) => contentReady.TrySetResult();
            ShowCloseFixture(f.Window);
            await contentReady.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var store = R4Get<SessionStore>(f.Window, "_store");
            store.Save(FakeSession);
            var observed = store.Load();
            Assert(observed == FakeSession, "fixture did not verify the saved login before sign-out");
            R4Set(f.Window, "_ownedStoredSession", observed);
            using var locked = new FileStream(Path.Combine(f.DirectoryPath, "session.dpapi"), FileMode.Open, FileAccess.Read, FileShare.Read);
            f.Window.SignOut();
            var prompts = 0;
            f.Window.CloseSaveFailurePrompt = failure =>
            {
                prompts++;
                Assert(failure.Contains("снова открыть этот аккаунт", StringComparison.Ordinal) && store.Load() is not null,
                    "exit did not explain the retained sign-in");
                locked.Dispose();
                return MessageBoxResult.Yes;
            };
            try
            {
                f.Window.Close();
                await Until(() => R4Get<bool>(f.Window, "_closed"));
                Assert(prompts == 1 && store.Load() is null, "exit did not retry the pending deletion before closing");
            }
            finally { f.Window.CloseSaveFailurePrompt = null; }
        }
    }

    private static Task ClosePlaybackSnapshotRecovery()
    {
        foreach (var corrupt in new[] { false, true })
        {
            using var f = new Qf061A10UpdateFixture(corrupt ? "corrupt-playback" : "unavailable-playback");
            var store = R4Get<PlaybackStateStore>(f.Window, "_playbackStateStore");
            var path = Path.Combine(f.DirectoryPath, PlaybackStateStore.FileName);
            var savedQueue = new PlaybackQueue();
            var row = new TrackRow { Id = "1", RequestId = "1", Title = "Saved fixture", DurationMs = 120000, Available = true };
            savedQueue.SetItems([row], row);
            var baseline = PlaybackStateStore.Capture(Qf061A10UpdateFixture.AccountUid, savedQueue, 24);
            Assert(baseline.CurrentIndex == 0 && baseline.PositionSeconds == 24, "fixture did not create a resumable playback checkpoint");
            store.Save(baseline);
            R4Set(f.Window, "_playbackStateAccountUid", null);
            R4Set(f.Window, "_session", null);
            if (corrupt) File.WriteAllText(path, "{");
            using (var locked = corrupt ? null : new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                R4Call(f.Window, "RestorePlaybackState", Qf061A10UpdateFixture.AccountUid);
                Assert(R4Get<PlaybackQueue>(f.Window, "_playbackQueue").Items.Count == 0
                    && !(bool)R4Call(f.Window, "TrySavePlaybackStateForExit")!, "unread snapshot was replaced by the empty live queue");
            }
            var saved = (bool)R4Call(f.Window, "TrySavePlaybackStateForExit")!;
            Assert(corrupt ? !saved && File.ReadAllText(path) == "{"
                : saved && store.Load(Qf061A10UpdateFixture.AccountUid) is { PositionSeconds: 24, Tracks.Count: 1 }
                    && R4Get<PlaybackQueue>(f.Window, "_playbackQueue").Current?.Id == row.Id,
                "exit recovery lost the stored position or did not preserve corrupt data");
        }
        return Task.CompletedTask;
    }

    private sealed class Qf061A10UpdateFixture : IDisposable
    {
        internal const string AccountUid = "qa-fixes061-update";
        internal string DirectoryPath { get; }
        internal string SnapshotPath => Path.Combine(DirectoryPath, DownloadQueueStore.FileName);
        internal MainWindow Window { get; }
        internal DownloadQueueState Queue => R4Get<DownloadQueueState>(Window, "_downloadQueue");
        internal DownloadQueueStore Store => R4Get<DownloadQueueStore>(Window, "_downloadQueueStore");
        internal Qf061A10UpdateBackend Backend { get; } = new();
        internal UpdateSupport Updates { get; }
        internal int Requests { get; private set; }
        private readonly OfflineLibrary _offline;
        private readonly CancellationTokenSource _workerCancellation;
        private TaskCompletionSource? _worker;
        internal Qf061A10UpdateFixture(string name)
        {
            DirectoryPath = Path.Combine(_directory, "qa-fixes061-update-" + name); Directory.CreateDirectory(DirectoryPath);
            var feed = Path.Combine(DirectoryPath, "feed.json");
            File.WriteAllText(feed, "{\"githubRepository\":\"https://github.com/senduora/yamuse\"}");
            Updates = new UpdateSupport(feed, true, _ => Backend);
            Updates.CheckAsync().GetAwaiter().GetResult();
            _offline = new OfflineLibrary(Path.Combine(DirectoryPath, "music"),
                new AudioBuffer(Path.Combine(DirectoryPath, "download-buffer"), new R4NoNetwork()));
            var player = new NativePlayer(new AudioBuffer(Path.Combine(DirectoryPath, "player-buffer"), new R4NoNetwork()));
            var preferences = new PreferencesStore(DirectoryPath); preferences.Save(new() { Volume = 0, AutoDownloadFavorites = true });
            Window = new MainWindow(restore: false,
                api: new MusicApi(new FakeHandler((_, _) => { Requests++; throw new InvalidOperationException("Update fixture forbids network"); })),
                store: new SessionStore(DirectoryPath), diagnosticsDirectory: DirectoryPath, offline: _offline, player: player,
                preferences: preferences, audioOutputDevices: new PlaybackOptionsFakeOutputs());
            R4Set(Window, "_restore", true); R4Set(Window, "_account", new Account(AccountUid, "Fixture", false));
            R4Set(Window, "_session", FakeSession); R4Set(Window, "_appUpdatesOverride", Updates);
            R4Call(Window, "InitializePlaybackPersistence"); R4Call(Window, "RestorePlaybackState", AccountUid);
            R4Call(Window, "InitializeDownloadPersistence"); R4Call(Window, "BeginDownloadQueuePersistence");
            R4Set(Window, "_downloadQueueAccountUid", AccountUid); R4Set(Window, "_downloadQueueQuality", AudioQuality.Optimal);
            Queue.Replace(Enumerable.Range(1, 2).Select(id => new TrackRow
            { Id = "pending-" + id, RequestId = "pending-" + id, Title = "Pending fixture", Available = false }), "Update fixture");
            Queue.BeginRun(false); // A live batch can yield in preflight before workers inspect availability.
            Store.Save(DownloadQueueStore.Capture(AccountUid, Queue, AudioQuality.Optimal, false, null));
            _workerCancellation = CancellationTokenSource.CreateLinkedTokenSource(R4Get<CancellationTokenSource>(Window, "_lifetime").Token);
        }
        internal void StartWorker()
        {
            _worker = new(TaskCreationOptions.RunContinuationsAsynchronously);
            R4Set(Window, "_downloadTask", _worker.Task); R4Set(Window, "_downloadCts", _workerCancellation);
        }
        internal Task BeginUpdate()
        {
            StartWorker();
            return R4Task(Window, "InstallStartupUpdateAsync");
        }
        internal void FinishWorker()
        {
            if (_worker is null || _worker.Task.IsCompleted) return;
            if (R4Get<int>(Window, "_downloadQueueAccountGeneration") != R4Get<int>(Window, "_accountGeneration")) Queue.Suspend();
            Queue.EndRun(true);
            R4Set(Window, "_downloadCts", null); R4Set(Window, "_downloadTask", null);
            _worker.TrySetResult();
        }
        internal void AssertPausedCheckpoint()
        {
            var snapshot = Store.Load(AccountUid);
            Assert(snapshot is not null, "update lost the paused download checkpoint");
            var restored = new DownloadQueueState(); restored.Restore(DownloadQueueStore.PrepareRestore(snapshot!, _offline));
            Assert(restored.Entries.Count == 2 && restored.CanResume && !restored.Running
                && restored.Entries.All(entry => entry.State == DownloadEntryState.Paused),
                "update checkpoint did not restore unfinished downloads paused and resumable");
        }
        public void Dispose()
        {
            FinishWorker();
            if (!R4Get<bool>(Window, "_closed")) Window.Close();
            _offline.Dispose(); _workerCancellation.Dispose();
        }
    }
    private sealed class Qf061A10UpdateBackend : IAppUpdateBackend
    {
        internal int Applies, Checks;
        internal bool FailApply;
        internal string? OfferedVersion = "9.1.0";
        internal Exception? CheckError;
        public string? PendingVersion { get; set; } = "9.1.0";
        public Task<string?> CheckAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Checks++;
            return CheckError is { } error ? Task.FromException<string?>(error) : Task.FromResult(OfferedVersion);
        }
        public Task DownloadAsync(Action<int> progress, CancellationToken token) => throw new InvalidOperationException("Fake pending update must not download");
        public void ApplyAndRestart()
        { Applies++; if (FailApply) throw new IOException("Fixture apply failure"); }
    }

    // The product constructor reads several optional stores before a fixture
    // could replace them. This private fixture never calls it or shows a window.
    private sealed class R4UpdateFixture : IDisposable
    {
        internal MainWindow Window { get; }
        internal CancellationTokenSource Lifetime { get; } = new();
        internal R4UpdateBackend Backend { get; } = new();
        internal UpdateSupport Updates { get; }
        internal Button StartupButton { get; } = new();
        internal TextBlock StartupText { get; } = new();
        internal string DirectoryPath { get; }
        private readonly NativePlayer _player;
        private readonly OfflineLibrary _offline;
        private readonly Diagnostics _diagnostics = new();
        private readonly string _preferencesPath;
        private readonly byte[] _preferencesBefore;
        internal bool PreferencesUnchanged => File.ReadAllBytes(_preferencesPath).SequenceEqual(_preferencesBefore);
        internal string? LastErrorType => _diagnostics.LastErrorType;

        internal R4UpdateFixture(string name)
        {
            var directory = DirectoryPath = Path.Combine(_directory, "security-stability0317-" + name); Directory.CreateDirectory(directory);
            var feed = Path.Combine(directory, "feed.json");
            File.WriteAllText(feed, "{\"githubRepository\":\"https://github.com/senduora/yamuse\"}");
            Updates = new UpdateSupport(feed, true, _ => Backend);
            Updates.CheckAsync().GetAwaiter().GetResult(); // The fake backend confirms the pending version locally.
            var preferences = new PreferencesStore(directory);
            preferences.Save(new PlayerPreferences { Volume = .21, AutoDownloadFavorites = true });
            _preferencesPath = Directory.GetFiles(directory, "*.json").Single(path => Path.GetFileName(path) != "feed.json");
            _preferencesBefore = File.ReadAllBytes(_preferencesPath);
            _player = new NativePlayer(new AudioBuffer(Path.Combine(directory, "buffer"), new R4NoNetwork()));
            _offline = new OfflineLibrary(Path.Combine(directory, "offline"),
                new AudioBuffer(Path.Combine(directory, "offline-buffer"), new R4NoNetwork()));
            Window = (MainWindow)RuntimeHelpers.GetUninitializedObject(typeof(MainWindow));
            R4Set(Window, "_lifetime", Lifetime); R4Set(Window, "_downloadQueue", new DownloadQueueState());
            R4Set(Window, "_pendingUpdateSchedule", new UpdateSchedulePatch());
            R4Set(Window, "_diagnostics", _diagnostics);
            R4Set(Window, "_sessionRefreshWrite", Task.CompletedTask);
            R4Set(Window, "_deviceTokenWrite", Task.CompletedTask);
            R4Set(Window, "_pendingConfirmedFavorites", new Dictionary<string, TrackRow>(StringComparer.Ordinal));
            R4Set(Window, "_appUpdatesOverride", Updates); R4Set(Window, "_player", _player); R4Set(Window, "_offline", _offline);
            R4Set(Window, "_preferencesReady", true); R4Set(Window, "_preferenceStore", preferences);
            R4Set(Window, "_autoFavoritesEnabled", true); R4Set(Window, "_concurrentDownloads", 3);
            R4Set(Window, "_accentFromCover", true); R4Set(Window, "_staticAccentColor", "#EA571C");
            R4Set(Window, "QualityCombo", new ComboBox { SelectedIndex = 1 }); R4Set(Window, "VolumeSlider", new Slider { Maximum = 100, Value = 67 });
            R4Set(Window, "SettingsButton", new Button()); R4Set(Window, "SettingsClearOffline", new Button());
            R4Set(Window, "SettingsClearCache", new Button()); R4Set(Window, "SettingsUninstall", new Button());
            R4Set(Window, "_startupUpdatePrompt", new Border()); R4Set(Window, "_startupUpdateText", StartupText);
            R4Set(Window, "_startupUpdateButton", StartupButton); R4Set(Window, "_startupUpdateLater", new Button());
            R4Set(Window, "_settingsOpen", true); R4Set(Window, "SettingsAccentHex", new TextBox { Text = "#20A0B0" });
        }
        public void Dispose() { Lifetime.Cancel(); _offline.Dispose(); _player.Dispose(); _diagnostics.Dispose(); Lifetime.Dispose(); }
    }
    private sealed class R4UpdateBackend : IAppUpdateBackend
    {
        internal int Applies;
        public string? PendingVersion => "9.1.0";
        public Task<string?> CheckAsync(CancellationToken token) => Task.FromResult(PendingVersion);
        public Task DownloadAsync(Action<int> progress, CancellationToken token) => throw new InvalidOperationException("Fake package is already downloaded");
        public void ApplyAndRestart() => Applies++;
    }
    private sealed class R4NoNetwork : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
            => throw new InvalidOperationException("Security stability fixture forbids network requests");
    }
}
