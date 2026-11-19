using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using YMusic.Native;

internal static partial class Program
{
    private static async Task CheckUpdateSchedule0317()
    {
        await Run("Updates0317: all intervals calendar month restart and future checkpoints are bounded", U317Calendar);
        await Run("Updates0317: missing invalid preferences and corrupt checkpoint preserve safe defaults", U317Storage);
        await Run("Updates0317: legacy UTF-8 checkpoint and busy replacement preserve the previous attempt", U317GuardedCheckpoint);
        await Run("Updates0317: named NTFS metadata prevents a lossy checkpoint replacement", U317CheckpointMetadata);
        await Run("Updates0317: replacement preserves explicit and inherited NTFS compression", U317CompressedCheckpoint);
        await Run("Updates0317: Startup Off and periodic checks remain serialized and stop on close", U317Scheduler);
        await Run("Updates0317: persisted GitHub cooldown defers startup and schedules one check at expiry", U317SchedulerCooldown);
        await Run("Updates0317: blocked cooldown writes retry only the pending fields on check and close", U317CooldownWriteRetry);
        await Run("Updates0317: a blocked checkpoint read defers exactly one startup check through cooldown", U317DeferredStartupCooldown);
        await Run("Updates0317: failed attempt publication retries with success and defers an early restarted check", U317AttemptWriteRetry);
        await Run("Updates0317: unsaved update checkpoint defers Apply and participates in the normal close choice", U317CheckpointExitAdmission);
        await Run("Updates0317: damaged checkpoints recover before Latest and preserve readable limits and replacement files", U317DamagedScheduleRecovery);
        await Run("Updates0317: a checkpoint damaged after load keeps the fresh rate decision through guarded repair", U317LateScheduleDamage);
        await Run("Updates0317: manual Latest honors a later checkpoint read at admission or completion", U317LateStoredRateAdmission);
        await Run("Updates0317: admission repairs partial rate history without advancing the attempt", U317PartialRateAdmission);
        await Run("Updates0317: admission preserves the restored hourly cadence and named checkpoint streams", U317RestoredHourlyAdmission);
        await Run("Updates0317: a longer accepted 429 merges a concurrent pause before Close and Apply", U317ConcurrentRateDecision);
        await Run("Updates0317: admission uses a restored expired secondary history before Latest", U317RestoredExpiredRateHistory);
        await Run("Updates0317: a failed Latest confirms its admitted attempt without rewriting clean storage", U317FailedLatestCheckpoint);
        await Run("Updates0317: close and apply settle a classified Latest result within a bounded wait", U317SettleAdmittedLatest);
        await Run("Updates0317: manual checks report unreadable schedules and retry without bypassing the deadline", U317ManualUnreadableSchedule);
        await Run("Updates0317: canceling close shows only the deferred startup offer", U317DeferredStartupOfferOnCanceledClose);
        await Run("Updates0317: unreadable preferences defer automatic checks but allow a manual check", U317UnreadablePreferenceSchedule);
        await Run("Updates0317: explicit frequency becomes effective only after a successful preference save", U317CommittedFrequency);
        await Run("Updates0317: close and apply retry merge delayed settings with the recovered file", U317RetryPreferenceCommit);
        await Run("Updates0317: persisted attempt survives restart and a failed request waits the selected interval", U317Restart);
        await Run("Updates0317: immediate frequency persistence and automatic status preserve current edits", U317SettingsDraft);
        await Run("Updates0317: first bind live apply invalid HEX debounce close and centered settings have no save footer", U317InstantSettings);
        await Run("Updates0317: portable check and explicit official links never create installer backend", U317Portable);
        await Run("Updates0317: portable HTTP metadata rejects redirects oversized malformed and canceled responses", U317PortableTrust);
        await Run("Updates0317: only the project feed can create an update backend", U317Repository);
        await Run("Updates0317: an internal timeout clears a stale available release", U317Timeout);
        await Run("Updates0317: pending settings can check manually without downloading or applying an update", U317PendingManualCheck);
        await Run("Updates0317: footer status and links fit the compact footer at both window sizes", U317Footer);
    }

    private static async Task U317Repository()
    {
        var path = Path.Combine(_directory, "updates0317-alternate-feed.json");
        File.WriteAllText(path, """{"githubRepository":"https://github.com/example/demo"}""");
        foreach (var installed in new[] { true, false })
        {
            var calls = 0;
            var service = new UpdateSupport(path, installed, _ => { calls++; return new Su016Backend(); },
                _ => { calls++; return Task.FromResult<string?>(null); });
            var state = await service.CheckAsync();
            await service.DownloadAsync();
            Assert(state.Status == AppUpdateStatus.Error && !state.FeedConfigured && calls == 0,
                "alternate repository reached an update backend");
        }
    }

    private static async Task U317Timeout()
    {
        var path = Path.Combine(_directory, "updates0317-timeout.json");
        File.WriteAllText(path, """{"githubRepository":"https://github.com/senduora/yamuse"}""");
        var checks = 0;
        var service = new UpdateSupport(path, false, portableCheck: _ => ++checks == 1
            ? Task.FromResult<string?>("9.1.0")
            : Task.FromException<string?>(new TaskCanceledException("fixture timeout")));
        Assert((await service.CheckAsync()).Status == AppUpdateStatus.Available, "fixture update offer missing");
        var state = await service.CheckAsync();
        Assert(state.Status == AppUpdateStatus.Error && state.AvailableVersion is null,
            "a timed out check reported the old update as current");
    }

    private static async Task U317PendingManualCheck()
    {
        using var fixture = new SaFixture("updates0317-pending-manual");
        var window = fixture.Window;
        var backend = new Su016Backend { PendingVersion = "9.1.0" };
        var updates = Su016Service(window, "u317-pending-manual", backend);
        Assert((await updates.CheckAsync()).Status == AppUpdateStatus.Downloaded, "pending fixture was not confirmed");
        SaClick(Control<Button>(window, "SettingsButton"));
        var panel = (StackPanel)Control<ContentControl>(window, "SettingsUpdates").Content;
        var apply = panel.Children.OfType<Button>().Single();
        var check = panel.Children.OfType<StackPanel>().Single().Children.OfType<Button>().Single();
        Assert(Equals(apply.Content, "Обновить и перезапустить") && Equals(check.Content, "Проверить обновления") && check.IsEnabled,
            "pending settings did not preserve explicit apply and separate manual check");

        backend.PendingVersion = null;
        backend.Version = "9.2.0";
        SaClick(check);
        await Until(() => updates.State.Status == AppUpdateStatus.Available && !SaField<bool>(window, "_updateCheckInProgress"));
        Assert(updates.State.AvailableVersion == "9.2.0" && backend.Checks == 2 && backend.Downloads == 0 && backend.Applies == 0,
            "manual recheck did not refresh the offer independently of download or apply");
    }

    private static Task U317Calendar()
    {
        var jan = new DateTimeOffset(2028, 1, 31, 20, 15, 0, TimeSpan.Zero);
        foreach (var (frequency, expected) in new[]
        {
            (UpdateCheckFrequency.Hourly, jan.AddHours(1)), (UpdateCheckFrequency.Daily, jan.AddDays(1)),
            (UpdateCheckFrequency.Weekly, jan.AddDays(7)), (UpdateCheckFrequency.Monthly, new DateTimeOffset(2028, 2, 29, 20, 15, 0, TimeSpan.Zero))
        })
        {
            var checkpoint = new UpdateScheduleCheckpoint(jan, jan.AddMinutes(-1));
            Assert(UpdateSchedulePolicy.NextDue(frequency, checkpoint, jan) == expected, frequency + " interval drift");
            Assert(UpdateSchedulePolicy.NextDue(frequency, checkpoint, expected.AddTicks(-1)) > expected.AddTicks(-1), frequency + " checked early");
            Assert(UpdateSchedulePolicy.NextDue(frequency, checkpoint, expected) == expected, frequency + " lost due on restart");
            Assert(UpdateSchedulePolicy.NextDue(frequency, new(jan.AddYears(3)), jan) > jan, frequency + " future clock created retry loop");
            Assert(UpdateSchedulePolicy.NextDue(frequency, new(), jan) == jan, frequency + " first check is not due");
        }
        Assert(UpdateSchedulePolicy.NextDue(UpdateCheckFrequency.Off, new(), jan) is null
            && UpdateSchedulePolicy.NextDue(UpdateCheckFrequency.Startup, new(), jan) is null
            && UpdateSchedulePolicy.NextDue((UpdateCheckFrequency)999, new(), jan) is null, "disabled or invalid frequency scheduled a timer");
        Assert(UpdateSchedulePolicy.NextDue(UpdateCheckFrequency.Monthly, new(new(2027, 1, 31, 0, 0, 0, TimeSpan.Zero)), jan)
            == new DateTimeOffset(2027, 2, 28, 0, 0, 0, TimeSpan.Zero), "nonleap month boundary drift");
        return Task.CompletedTask;
    }

    private static Task U317Storage()
    {
        var directory = Path.Combine(_directory, "updates0317-store"); Directory.CreateDirectory(directory);
        var preferences = new PreferencesStore(directory);
        Assert(preferences.Load().UpdateCheckFrequency == UpdateCheckFrequency.Startup, "missing preference did not preserve Startup");
        File.WriteAllText(preferences.FilePath, "{\"volume\":0.23,\"updateCheckFrequency\":\"future-mode\"}");
        var recovered = preferences.Load();
        Assert(recovered.Volume == .23 && recovered.UpdateCheckFrequency == UpdateCheckFrequency.Startup, "invalid frequency reset unrelated preferences");
        foreach (var frequency in Enum.GetValues<UpdateCheckFrequency>())
        { preferences.Save(new() { UpdateCheckFrequency = frequency }); Assert(preferences.Load().UpdateCheckFrequency == frequency, "mode persistence drift"); }
        var store = new UpdateScheduleStore(directory); var now = DateTimeOffset.UtcNow;
        foreach (var corrupt in new[] { "{", "{}", new string('x', 5000), "{\"LastAttemptUtc\":\"bad-date\"}" })
        { File.WriteAllText(store.FilePath, corrupt); Assert(store.Load(now).LastAttemptUtc is null, "corrupt checkpoint survived"); }
        store.Save(new(now.AddDays(2), now.AddDays(1)));
        Assert(store.Load(now) == new UpdateScheduleCheckpoint(now, now), "future persisted clock was not reset");
        Assert(Directory.GetFiles(directory, "*.tmp").Length == 0, "checkpoint write left temporary files");
        return Task.CompletedTask;
    }

    private static async Task U317GuardedCheckpoint()
    {
        var directory = Path.Combine(_directory, "updates0317-checkpoint-guarded", "data");
        var store = new UpdateScheduleStore(directory);
        var now = new DateTimeOffset(2028, 3, 1, 12, 0, 0, TimeSpan.Zero);
        Assert(store.Load(now) == new UpdateScheduleCheckpoint() && !Directory.Exists(directory), "checkpoint read created missing parents");
        store.Save(new(now.AddHours(-1)));
        var original = File.ReadAllText(store.FilePath);
        File.WriteAllText(store.FilePath, original, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        var bytes = File.ReadAllBytes(store.FilePath);
        Assert(store.Load(now) == new UpdateScheduleCheckpoint(now.AddHours(-1)), "legacy UTF-8 checkpoint with BOM failed to load");
        using (File.Open(store.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            await Throws<IOException>(() => { store.Save(new(now)); return Task.CompletedTask; });
            Assert(bytes.SequenceEqual(File.ReadAllBytes(store.FilePath)) && !Directory.EnumerateFiles(directory, "*.tmp").Any(),
                "busy checkpoint lost the previous attempt or left a staging file");
        }
        store.Save(new(now));
        Assert(store.Load(now) == new UpdateScheduleCheckpoint(now), "checkpoint could not retry after the file became writable");
    }

    private static async Task U317CheckpointMetadata()
    {
        var directory = Path.Combine(_directory, "updates0317-checkpoint-metadata");
        var store = new UpdateScheduleStore(directory);
        if (new DriveInfo(Path.GetPathRoot(store.FilePath)!).DriveFormat != "NTFS") return;
        var now = new DateTimeOffset(2028, 3, 1, 12, 0, 0, TimeSpan.Zero);
        var previous = new UpdateScheduleCheckpoint(now.AddHours(-1));
        store.Save(previous);
        var stream = store.FilePath + ":yamuse-fixture";
        File.WriteAllText(stream, "synthetic metadata");
        var original = File.ReadAllBytes(store.FilePath);
        await Throws<IOException>(() => { store.Save(new(now)); return Task.CompletedTask; });
        Assert(store.Load(now) == previous && original.SequenceEqual(File.ReadAllBytes(store.FilePath))
            && File.ReadAllText(stream) == "synthetic metadata" && !Directory.EnumerateFiles(directory, "*.tmp").Any(),
            "replacement changed the checkpoint or its named stream");
    }

    private static Task U317CompressedCheckpoint()
    {
        const uint fileCompression = 0x00000010;
        var drive = Path.GetPathRoot(_directory)!;
        if (new DriveInfo(drive).DriveFormat != "NTFS") return Task.CompletedTask;
        Assert(U317GetVolumeInformation(drive, IntPtr.Zero, 0, out _, out _, out var flags, IntPtr.Zero, 0),
            "could not read fixture volume capabilities");
        if ((flags & fileCompression) == 0) return Task.CompletedTask;

        var now = DateTimeOffset.UtcNow;
        foreach (var (compressedDirectory, compressedFile) in new[] { (false, true), (true, true), (true, false) })
        {
            var directory = Path.Combine(_directory, $"updates0317-compressed-{compressedDirectory}-{compressedFile}");
            Directory.CreateDirectory(directory);
            SetCompression(directory, compressedDirectory);
            var store = new UpdateScheduleStore(directory);
            store.Save(new(now.AddHours(-1)));
            SetCompression(store.FilePath, compressedFile);
            store.Save(new(now));
            Assert(store.Load(now) == new UpdateScheduleCheckpoint(now)
                && File.GetAttributes(store.FilePath).HasFlag(FileAttributes.Compressed) == compressedFile
                && !Directory.EnumerateFiles(directory, "*.tmp").Any(), "replacement lost compression state or the checkpoint");
        }
        return Task.CompletedTask;

        static void SetCompression(string path, bool compressed)
        {
            var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "compact.exe"))
                { UseShellExecute = false, CreateNoWindow = true };
            start.ArgumentList.Add(compressed ? "/C" : "/U");
            start.ArgumentList.Add("/Q");
            start.ArgumentList.Add(path);
            using var process = Process.Start(start) ?? throw new IOException("Could not start the fixture compression tool.");
            Assert(process.WaitForExit(5000) && process.ExitCode == 0
                && File.GetAttributes(path).HasFlag(FileAttributes.Compressed) == compressed, "could not set fixture compression mode");
        }
    }

    [DllImport("kernel32.dll", EntryPoint = "GetVolumeInformationW", CharSet = CharSet.Unicode, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool U317GetVolumeInformation(string root, IntPtr volumeName, uint volumeNameSize, out uint serial,
        out uint maxName, out uint flags, IntPtr fileSystemName, uint fileSystemNameSize);

    private static async Task U317Scheduler()
    {
        using var f = new SaFixture("updates0317-scheduler"); var w = f.Window;
        var backend = new Su016Backend { HoldCheck = true }; Su016Service(w, "u317-scheduler", backend);
        var now = new DateTimeOffset(2028, 3, 1, 1, 0, 0, TimeSpan.Zero); U317Set(w, "_updateClock", (Func<DateTimeOffset>)(() => now));
        U317Set(w, "_updateCheckFrequency", UpdateCheckFrequency.Off);
        await Call(w, "CheckForAppUpdatesAsync"); await Call(w, "RunScheduledUpdateCheckAsync");
        Assert(backend.Checks == 0 && SaField<DispatcherTimer?>(w, "_updateScheduleTimer") is null, "Off started a check or timer");
        U317Set(w, "_updateCheckFrequency", UpdateCheckFrequency.Hourly);
        var first = Call(w, "RunScheduledUpdateCheckAsync"); await backend.CheckEntered.Task;
        await Call(w, "RunScheduledUpdateCheckAsync"); SaClick(Control<Button>(w, "FooterUpdateButton"));
        Assert(backend.Checks == 1 && !Control<Button>(w, "FooterUpdateButton").IsEnabled, "manual/timer requests overlapped");
        backend.CheckRelease.TrySetResult(); await first;
        Assert(Su016Find<Border>(w, "StartupUpdatePrompt") is null && backend.Downloads == 0 && backend.Applies == 0, "periodic check navigated, prompted, or installed");
        await Call(w, "RunScheduledUpdateCheckAsync"); Assert(backend.Checks == 1, "checked before due");
        now = now.AddHours(1); backend.HoldCheck = false;
        await Call(w, "RunScheduledUpdateCheckAsync"); Assert(backend.Checks == 2, "missed due hourly check");
        var timer = SaField<DispatcherTimer?>(w, "_updateScheduleTimer"); Assert(timer?.IsEnabled == true, "interval timer was not armed");
        w.Close(); now = now.AddHours(1); await Call(w, "RunScheduledUpdateCheckAsync");
        Assert(timer is { IsEnabled: false } && SaField<DispatcherTimer?>(w, "_updateScheduleTimer") is null && backend.Checks == 2,
            "timer or clock callback survived close");
    }

    private static async Task U317UnreadablePreferenceSchedule()
    {
        using var f = new SaFixture("updates0317-unreadable-preferences");
        var w = f.Window;
        var backend = new Su016Backend();
        Su016Service(w, "u317-unreadable-preferences", backend);
        var preferences = new PreferencesStore(Path.Combine(_directory, "updates0317-unreadable-preference-store"));
        var saved = new PlayerPreferences { UpdateCheckFrequency = UpdateCheckFrequency.Off, Volume = .27,
            PlaybackSpeed = 1.25, CrossfadeSeconds = 6.5, AutoDownloadFavorites = true };
        preferences.Save(saved);
        var priorBytes = File.ReadAllBytes(preferences.FilePath);
        U317Set(w, "_restore", true);
        U317Set(w, "_preferenceStore", preferences);
        using (var held = new FileStream(preferences.FilePath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            typeof(MainWindow).GetMethod("LoadPreferences", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(w, null);
            Assert(SaField<bool>(w, "_preferencesLoadFailed"), "fixture did not hold the saved preferences");
            Assert(SaInvokeResult(w, "SaveStateBeforeClosing") is string, "close ignored a still inaccessible preferences file");
            await Call(w, "CheckForAppUpdatesAsync");
            U317Set(w, "_updateCheckFrequency", UpdateCheckFrequency.Hourly);
            await Call(w, "RunScheduledUpdateCheckAsync");
            Assert(backend.Checks == 0 && SaField<DispatcherTimer?>(w, "_updateScheduleTimer") is null,
                "unread settings started a network request or timer");
            SaClick(Control<Button>(w, "FooterUpdateButton"));
            await Until(() => backend.Checks == 1);
        }
        Assert(preferences.Load().UpdateCheckFrequency == UpdateCheckFrequency.Off,
            "manual check overwrote an unread opt-out");
        Assert(SaInvokeResult(w, "SaveStateBeforeClosing") is null && File.ReadAllBytes(preferences.FilePath).SequenceEqual(priorBytes),
            "close could not retry the read or replaced saved settings with runtime defaults");
        using (var held = new FileStream(preferences.FilePath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            SaClick(Control<Button>(w, "SettingsButton"));
            Control<Slider>(w, "CrossfadeSetting").Value = 4;
            var automatic = Control<CheckBox>(w, "SettingsAutoFavorites");
            automatic.IsChecked = true;
            Assert(automatic.IsChecked == false && SaField<bool>(w, "_settingsSavePending")
                && SaInvokeResult(w, "SaveStateBeforeClosing") is string,
                "a failed auto-download write discarded another queued settings edit");
            SaInvoke(w, "FlushSettingsSave");
            Assert(SaInvokeResult(w, "SaveStateBeforeClosing") is string, "a failed timer flush discarded the edit");
        }
        Assert(File.ReadAllBytes(preferences.FilePath).SequenceEqual(priorBytes), "a blocked save changed the original file");
        var merged = saved with { CrossfadeSeconds = 4 };
        Assert(SaInvokeResult(w, "SaveStateBeforeClosing") is null && preferences.Load() == merged,
            "retry failed to merge the delayed edit or carried a rejected auto-download proposal");

        var externallyUpdated = merged with { Volume = .43, ConcurrentDownloads = 7 };
        preferences.Save(externallyUpdated);
        Control<ComboBox>(w, "SettingsSpeed").SelectedItem = 1.5;
        Assert(preferences.Load() == (externallyUpdated with { PlaybackSpeed = 1.5 }) && backend.Checks == 1,
            "a second accepted edit replaced untouched fields or armed the unread update schedule");

        var frequency = SaField<ComboBox>(w, "_updateFrequencyCombo");
        frequency.SelectedValue = UpdateCheckFrequency.Daily;
        Assert(preferences.Load() == (externallyUpdated with { PlaybackSpeed = 1.5, UpdateCheckFrequency = UpdateCheckFrequency.Daily })
            && SaField<DispatcherTimer?>(w, "_updateScheduleTimer") is { IsEnabled: true } && backend.Checks == 1,
            "the explicitly merged frequency did not arm scheduling in the same window");
    }

    private static async Task U317SchedulerCooldown()
    {
        var directory = Path.Combine(_directory, "updates0317-cooldown");
        var store = new UpdateScheduleStore(directory);
        var clock = new V040UpdateClock();
        var start = clock.GetUtcNow();
        var (downloader, _) = V040PackageDownloader();
        downloader.LatestStatus = HttpStatusCode.TooManyRequests;
        downloader.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromMinutes(61));
        var locator = new Velopack.Locators.TestVelopackLocator("YMusic.Native", "0.3.16", directory);
        void Configure(MainWindow window, UpdateSupport updates)
        {
            U317Set(window, "_appUpdatesOverride", updates);
            U317Set(window, "_updateScheduleStore", store);
            U317Set(window, "_updateClock", (Func<DateTimeOffset>)clock.GetUtcNow);
        }

        using (var first = new SaFixture("updates0317-cooldown-before-restart"))
        {
            var updates = V040Service(directory, locator, downloader, clock: clock);
            Configure(first.Window, updates);
            await Call(first.Window, "CheckForAppUpdatesAsync");
            var persisted = store.Load(start);
            Assert(downloader.Requests.Count == 1 && persisted.LastAttemptUtc == start
                && persisted.RateLimit?.RetryNotBeforeUtc == start.AddMinutes(61), "server cooldown was not recorded in the schedule checkpoint");
            clock.Advance(TimeSpan.FromMinutes(1));
            await Call(first.Window, "CheckAppUpdateNowAsync", updates);
            Assert(downloader.Requests.Count == 1 && store.Load(clock.GetUtcNow()) == persisted,
                "locally suppressed manual check incorrectly moved the request checkpoint");
        }

        using var second = new SaFixture("updates0317-cooldown-after-restart");
        var fresh = V040Service(directory, locator, downloader, clock: clock);
        Configure(second.Window, fresh);
        downloader.LatestStatus = HttpStatusCode.OK;
        await Call(second.Window, "CheckForAppUpdatesAsync");
        Assert(downloader.Requests.Count == 1 && store.Load(clock.GetUtcNow()).LastAttemptUtc == start,
            "a new Startup check ignored the persisted cooldown or moved the checkpoint");

        U317Set(second.Window, "_updateCheckFrequency", UpdateCheckFrequency.Hourly);
        clock.Advance(TimeSpan.FromMinutes(59));
        await Call(second.Window, "RunScheduledUpdateCheckAsync");
        Assert(downloader.Requests.Count == 1 && store.Load(clock.GetUtcNow()).LastAttemptUtc == start
            && SaField<DispatcherTimer?>(second.Window, "_updateScheduleTimer") is { IsEnabled: true } timer
            && timer.Interval == TimeSpan.FromMinutes(1), "hourly check did not wait for the later of the due time and GitHub cooldown");
        clock.Advance(TimeSpan.FromMinutes(1));
        await Call(second.Window, "RunScheduledUpdateCheckAsync");
        await Call(second.Window, "RunScheduledUpdateCheckAsync");
        var complete = store.Load(clock.GetUtcNow());
        Assert(downloader.Requests.Count == 3 && complete.LastAttemptUtc == clock.GetUtcNow() && complete.LastSuccessUtc == clock.GetUtcNow()
            && complete.RateLimit is null, "due check did not resume exactly once and clear the saved cooldown");
    }

    private static async Task U317CommittedFrequency()
    {
        using var fixture = new SaFixture("updates0317-committed-frequency");
        var window = fixture.Window;
        var backend = new Su016Backend();
        Su016Service(window, "u317-committed-frequency", backend);
        var store = new PreferencesStore(Path.Combine(_directory, "updates0317-committed-frequency-store"));
        var saved = new PlayerPreferences { UpdateCheckFrequency = UpdateCheckFrequency.Off, Volume = .27, PlaybackSpeed = 1.25 };
        store.Save(saved);
        U317Set(window, "_restore", true);
        U317Set(window, "_preferenceStore", store);
        using (var unreadable = new FileStream(store.FilePath, FileMode.Open, FileAccess.Read, FileShare.None))
            SaInvoke(window, "LoadPreferences");
        await Call(window, "CheckForAppUpdatesAsync");
        SaClick(Control<Button>(window, "SettingsButton"));
        var frequency = SaField<ComboBox>(window, "_updateFrequencyCombo");
        Assert(frequency.SelectedIndex == -1 && backend.Checks == 0, "unread preferences presented or used an assumed frequency");
        frequency.SelectedValue = UpdateCheckFrequency.Startup;
        Assert(frequency.SelectedValue is UpdateCheckFrequency.Startup && store.Load() == (saved with { UpdateCheckFrequency = UpdateCheckFrequency.Startup }),
            "explicit Startup selection was treated as the unconfirmed default or changed unrelated preferences");

        frequency.SelectedValue = UpdateCheckFrequency.Off;
        using (var locked = new FileStream(store.FilePath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            frequency.SelectedValue = UpdateCheckFrequency.Hourly;
            await Call(window, "RunScheduledUpdateCheckAsync");
            Assert(frequency.SelectedValue is UpdateCheckFrequency.Off && backend.Checks == 0
                && SaField<DispatcherTimer?>(window, "_updateScheduleTimer")?.IsEnabled != true,
                "a failed save replaced the active opt-out or issued an automatic request");
        }
        Assert(store.Load() == saved, "failed change replaced the last committed preferences");
        Control<ComboBox>(window, "SettingsSpeed").SelectedItem = 1.5;
        saved = saved with { PlaybackSpeed = 1.5 };
        await Call(window, "RunScheduledUpdateCheckAsync");
        Assert(store.Load() == saved && frequency.SelectedValue is UpdateCheckFrequency.Off && backend.Checks == 0,
            "a different setting publication silently enabled the rejected interval");
        frequency.SelectedValue = UpdateCheckFrequency.Hourly;
        await Call(window, "RunScheduledUpdateCheckAsync");
        Assert(frequency.SelectedValue is UpdateCheckFrequency.Hourly && store.Load() == (saved with { UpdateCheckFrequency = UpdateCheckFrequency.Hourly })
            && backend.Checks == 1 && SaField<DispatcherTimer?>(window, "_updateScheduleTimer") is { IsEnabled: true },
            "retry did not persist and activate the selected interval while preserving adjacent settings");
    }

    private static async Task U317CooldownWriteRetry()
    {
        foreach (var manualRetry in new[] { true, false })
        {
            var directory = Path.Combine(_directory, "updates0317-cooldown-write", manualRetry.ToString());
            var store = new UpdateScheduleStore(directory);
            var clock = new V040UpdateClock();
            var start = clock.GetUtcNow();
            var (downloader, _) = V040PackageDownloader();
            var locator = new Velopack.Locators.TestVelopackLocator("YMusic.Native", "0.3.16", directory);
            var expectedUntil = start.AddMinutes(12);
            FileStream? locked = null;
            Task BlockWrite(CancellationToken _)
            {
                locked = File.OpenRead(store.FilePath);
                return Task.CompletedTask;
            }
            UpdateSupport Attach(MainWindow window)
            {
                var updates = V040Service(directory, locator, downloader, clock: clock);
                U317Set(window, "_appUpdatesOverride", updates);
                U317Set(window, "_updateScheduleStore", store);
                U317Set(window, "_updateClock", (Func<DateTimeOffset>)clock.GetUtcNow);
                return updates;
            }
            var independentSuccess = start.AddMinutes(-8);
            void ChangeUnrelatedFields()
            {
                var document = JsonNode.Parse(File.ReadAllText(store.FilePath))!.AsObject();
                document["LastSuccessUtc"] = JsonValue.Create(independentSuccess);
                document["FutureExtension"] = new JsonObject { ["Retain"] = true };
                File.WriteAllText(store.FilePath, document.ToJsonString());
            }

            using (var window = new SaFixture("updates0317-cooldown-write-first-" + manualRetry))
            {
                var updates = Attach(window.Window);
                downloader.LatestStatus = HttpStatusCode.TooManyRequests;
                downloader.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromMinutes(12));
                downloader.BeforeLatest = BlockWrite;
                try { await Call(window.Window, "CheckForAppUpdatesAsync"); }
                finally { downloader.BeforeLatest = null; locked?.Dispose(); }
                Assert(downloader.Requests.Count == 1 && store.Load(start).LastAttemptUtc == start
                    && store.Load(start).RateLimit is null, "fixture did not block only the cooldown publication");
                ChangeUnrelatedFields();
                if (manualRetry) await Call(window.Window, "CheckAppUpdateNowAsync", updates);
                if (manualRetry) Assert(store.Load(start).RateLimit?.RetryNotBeforeUtc == expectedUntil,
                    "local check did not retry the pending cooldown without a request");
                window.Window.Close();
            }
            Assert(downloader.Requests.Count == 1 && store.Load(start) is { LastAttemptUtc: var attempt, LastSuccessUtc: var success, RateLimit: var saved }
                && attempt == start && success == independentSuccess && saved?.RetryNotBeforeUtc == expectedUntil
                && JsonNode.Parse(File.ReadAllText(store.FilePath))?["FutureExtension"]?["Retain"]?.GetValue<bool>() == true,
                "cooldown retry lost the original deadline or rewrote independent checkpoint fields");

            using var restarted = new SaFixture("updates0317-cooldown-write-restart-" + manualRetry);
            var fresh = Attach(restarted.Window);
            downloader.LatestStatus = HttpStatusCode.OK;
            await Call(restarted.Window, "CheckForAppUpdatesAsync");
            Assert(downloader.Requests.Count == 1, "restart ignored the recovered cooldown");
            clock.Advance(TimeSpan.FromMinutes(12));
            downloader.BeforeLatest = BlockWrite;
            try { await Call(restarted.Window, "CheckAppUpdateNowAsync", fresh); }
            finally { downloader.BeforeLatest = null; locked?.Dispose(); }
            Assert(downloader.Requests.Count == 3 && store.Load(clock.GetUtcNow()).RateLimit?.RetryNotBeforeUtc == expectedUntil,
                "fixture did not block clearing the cooldown after a real successful check");
            restarted.Window.Close();
            Assert(store.Load(clock.GetUtcNow()) is { RateLimit: null, LastAttemptUtc: var last } && last == clock.GetUtcNow()
                && JsonNode.Parse(File.ReadAllText(store.FilePath))?["FutureExtension"]?["Retain"]?.GetValue<bool>() == true,
                "close did not persist the cleared cooldown independently of the real check time");
        }
    }

    private static async Task U317DeferredStartupCooldown()
    {
        foreach (var action in new[] { "timer", "manual", "off" })
        {
            var directory = Path.Combine(_directory, "updates0317-cooldown-read", action);
            var store = new UpdateScheduleStore(directory);
            var clock = new V040UpdateClock();
            var start = clock.GetUtcNow();
            var saved = new UpdateScheduleCheckpoint(start, RateLimit: new(start.AddMinutes(3), 1));
            store.Save(saved);
            var (downloader, _) = V040PackageDownloader();
            var locator = new Velopack.Locators.TestVelopackLocator("YMusic.Native", "0.3.16", directory);
            var updates = V040Service(directory, locator, downloader, clock: clock);
            using var window = new SaFixture("updates0317-cooldown-read-" + action);
            U317Set(window.Window, "_appUpdatesOverride", updates);
            U317Set(window.Window, "_updateScheduleStore", store);
            U317Set(window.Window, "_updateClock", (Func<DateTimeOffset>)clock.GetUtcNow);
            using (var blocked = new FileStream(store.FilePath, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                await Call(window.Window, "CheckForAppUpdatesAsync");
                await Call(window.Window, "CheckAppUpdateNowAsync", updates);
                Assert(downloader.Requests.Count == 0, "unread checkpoint admitted an early Latest request");
            }
            await Call(window.Window, "CheckAppUpdateNowAsync", updates);
            Assert(updates.RateLimitCheckpoint == saved.RateLimit && store.Load(start) == saved
                && downloader.Requests.Count == 0 && SaField<DispatcherTimer?>(window.Window, "_updateScheduleTimer") is { IsEnabled: true } timer
                && timer.Interval == TimeSpan.FromMinutes(3), "startup did not recover the original deadline before allowing a request");
            if (action == "off")
            {
                U317Set(window.Window, "_updateCheckFrequency", UpdateCheckFrequency.Off);
                SaInvoke(window.Window, "ArmUpdateSchedule");
            }
            clock.Advance(TimeSpan.FromMinutes(3));
            if (action == "manual") await Call(window.Window, "CheckAppUpdateNowAsync", updates);
            await Call(window.Window, "RunScheduledUpdateCheckAsync");
            await Call(window.Window, "RunScheduledUpdateCheckAsync");
            Assert(downloader.Requests.Count == (action == "off" ? 0 : 2)
                && SaField<DispatcherTimer?>(window.Window, "_updateScheduleTimer")?.IsEnabled != true,
                "Startup did not drain its single deferred check, or ignored the manual request or frequency change");
        }
    }

    private static UpdateSupport U317AttachUpdateSchedule(MainWindow window, string directory, UpdateScheduleStore store,
        V040UpdateClock clock, Velopack.Locators.TestVelopackLocator locator, V040GithubDownloader downloader)
    {
        var updates = V040Service(directory, locator, downloader, clock: clock);
        U317Set(window, "_appUpdatesOverride", updates);
        U317Set(window, "_updateScheduleStore", store);
        U317Set(window, "_updateClock", (Func<DateTimeOffset>)clock.GetUtcNow);
        return updates;
    }

    private static async Task U317AttemptWriteRetry()
    {
        var directory = Path.Combine(_directory, "updates0317-retry-attempt");
        var store = new UpdateScheduleStore(directory);
        var clock = new V040UpdateClock();
        var start = clock.GetUtcNow();
        var older = new UpdateScheduleCheckpoint(start.AddHours(-2), start.AddHours(-3));
        store.Save(older);
        var locator = new Velopack.Locators.TestVelopackLocator("YMusic.Native", "0.3.16", directory);
        var (downloader, _) = V040PackageDownloader();
        using (var first = new SaFixture("updates0317-attempt-retry-before-restart"))
        using (var busy = File.OpenRead(store.FilePath))
        {
            U317AttachUpdateSchedule(first.Window, directory, store, clock, locator, downloader);
            U317Set(first.Window, "_updateCheckFrequency", UpdateCheckFrequency.Hourly);
            downloader.BeforeLatest = _ =>
            {
                Assert(store.Load(start) == older, "the blocked attempt already changed the schedule");
                busy.Dispose();
                var document = JsonNode.Parse(File.ReadAllText(store.FilePath))!.AsObject();
                document["IndependentExtension"] = "keep";
                File.WriteAllText(store.FilePath, document.ToJsonString());
                clock.Advance(TimeSpan.FromMinutes(1));
                return Task.CompletedTask;
            };
            await Call(first.Window, "CheckForAppUpdatesAsync");
            downloader.BeforeLatest = null;
            var correct = new UpdateScheduleCheckpoint(start, start.AddMinutes(1));
            Assert(downloader.Requests.Count == 2 && store.Load(clock.GetUtcNow()) == correct
                && JsonNode.Parse(File.ReadAllText(store.FilePath))?["IndependentExtension"]?.GetValue<string>() == "keep",
                "success did not retry the original attempt time together with its own observation");
        }
        clock.Advance(TimeSpan.FromMinutes(9));
        using var restarted = new SaFixture("updates0317-attempt-retry-after-restart");
        U317AttachUpdateSchedule(restarted.Window, directory, store, clock, locator, downloader);
        U317Set(restarted.Window, "_updateCheckFrequency", UpdateCheckFrequency.Hourly);
        await Call(restarted.Window, "CheckForAppUpdatesAsync");
        Assert(downloader.Requests.Count == 2 && SaField<DispatcherTimer?>(restarted.Window, "_updateScheduleTimer")?.IsEnabled == true,
            "restart sent Latest again before the saved interval elapsed");
        var later = start.AddMinutes(2);
        store.Apply(new(Attempt: new(start, later)), later);
        store.Apply(new(Attempt: new(older.LastAttemptUtc, start)), later);
        Assert(store.Load(later).LastAttemptUtc == later, "an older attempt edit overwrote a later observed request");
    }

    private static async Task U317CheckpointExitAdmission()
    {
        foreach (var apply in new[] { false, true })
        {
            var directory = Path.Combine(_directory, "updates0317-commit-checkpoint", apply.ToString());
            var store = new UpdateScheduleStore(directory);
            var clock = new V040UpdateClock();
            var (downloader, bytes) = V040PackageDownloader();
            UpdateScheduleCheckpoint? atHandoff = null;
            var locator = new V040NoScanLocator(directory) { BeforeStart = () => atHandoff = store.Load(clock.GetUtcNow()) };
            Directory.CreateDirectory(locator.PackagesDir!);
            Directory.CreateDirectory(locator.RootAppDir);
            File.WriteAllBytes(Path.Combine(locator.PackagesDir!, V040GithubDownloader.PackageName), bytes);
            File.WriteAllText(locator.UpdateExePath, "fixture archive marker");
            using (var fixture = new SaFixture("updates0317-commit-checkpoint-" + apply))
            {
                var window = fixture.Window;
                var updates = U317AttachUpdateSchedule(window, directory, store, clock, locator, downloader);
                await Call(window, "CheckForAppUpdatesAsync");
                Assert(updates.State.Status == AppUpdateStatus.Downloaded, "checkpoint fixture did not verify its package");
                clock.Advance(TimeSpan.FromMinutes(1));
                var actualAttempt = clock.GetUtcNow();
                using var busy = File.OpenRead(store.FilePath);
                downloader.LatestStatus = HttpStatusCode.TooManyRequests;
                downloader.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromMinutes(12));
                await Call(window, "CheckAppUpdateNowAsync", updates);
                Assert(updates.State.Status == AppUpdateStatus.Downloaded && store.Load(actualAttempt).RateLimit is null
                    && store.Load(actualAttempt).LastAttemptUtc < actualAttempt, "fixture did not hold the attempt and cooldown publications");

                if (apply)
                {
                    Task<bool> Apply() => (Task<bool>)Call(window, "ApplyAppUpdateAsync", updates);
                    Assert(!await Apply() && locator.Starts == 0 && updates.State.Status == AppUpdateStatus.Downloaded,
                        "explicit apply ignored an unsaved active cooldown or lost its retry action");
                    busy.Dispose();
                    Assert(await Apply() && locator.Starts == 1 && locator.ExitCode == 0
                        && atHandoff?.LastAttemptUtc == actualAttempt && atHandoff?.RateLimit?.RetryNotBeforeUtc == actualAttempt.AddMinutes(12),
                        "explicit apply did not retry checkpoint publication after the lock cleared");
                }
                else
                {
                    var prompts = new List<string>();
                    window.CloseSaveFailurePrompt = message => { prompts.Add(message); return MessageBoxResult.Cancel; };
                    window.Close();
                    await Until(() => !SaField<bool>(window, "_closeCommitInProgress"));
                    Assert(prompts.Count == 1 && prompts[0].Contains("проверки обновлений", StringComparison.Ordinal)
                        && !SaField<bool>(window, "_closed"), "close did not offer the existing save/cancel choice for the pending checkpoint");
                    window.CloseSaveFailurePrompt = message => { prompts.Add(message); busy.Dispose(); return MessageBoxResult.Yes; };
                    window.Close();
                    await Until(() => SaField<bool>(window, "_closed"));
                    Assert(prompts.Count == 2, "retry choice did not close after publishing the checkpoint");
                }
                var saved = store.Load(actualAttempt);
                Assert(saved.LastAttemptUtc == actualAttempt && saved.RateLimit?.RetryNotBeforeUtc == actualAttempt.AddMinutes(12),
                    "admitted exit did not publish the exact failed attempt and active deadline before finishing");
            }
            var requests = downloader.Requests.Count;
            using var restarted = new SaFixture("updates0317-restart-committed-checkpoint-" + apply);
            U317AttachUpdateSchedule(restarted.Window, directory, store, clock, locator, downloader);
            await Call(restarted.Window, "CheckForAppUpdatesAsync");
            Assert(downloader.Requests.Count == requests, "startup ignored the cooldown saved by explicit exit or apply");
        }
    }

    private static async Task U317DamagedScheduleRecovery()
    {
        foreach (var original in new[] { "{", "", "partial", new string('x', 5000) })
        {
            var directory = Path.Combine(_directory, "updates0317-damaged-schedule", original.Length.ToString());
            Directory.CreateDirectory(directory);
            var store = new UpdateScheduleStore(directory);
            var clock = new V040UpdateClock();
            var start = clock.GetUtcNow();
            var partial = original == "partial";
            var raw = partial ? $$"""{"LastAttemptUtc":"bad-date","RateLimit":{"RetryNotBeforeUtc":"{{start.AddMinutes(3):O}}","Future":true},"Independent":"keep"}""" : original;
            File.WriteAllText(store.FilePath, raw);
            var before = File.ReadAllBytes(store.FilePath);
            var (downloader, _) = V040PackageDownloader();
            var locator = new Velopack.Locators.TestVelopackLocator("YMusic.Native", "0.3.16", directory);
            downloader.LatestStatus = HttpStatusCode.TooManyRequests;
            downloader.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromMinutes(9));
            using (var fixture = new SaFixture("updates0317-damaged-schedule-" + original.Length))
            {
                var updates = U317AttachUpdateSchedule(fixture.Window, directory, store, clock, locator, downloader);
                await Call(fixture.Window, "CheckForAppUpdatesAsync");
                if (original.Length > 4096)
                {
                    Assert(downloader.Requests.Count == 0 && File.ReadAllBytes(store.FilePath).SequenceEqual(before)
                        && !Directory.EnumerateFiles(directory, "*.bak").Any(), "unbounded file was admitted or recovered speculatively");
                    continue;
                }
                Assert(File.ReadAllBytes(Directory.EnumerateFiles(directory, "*.bak").Single()).SequenceEqual(before),
                    "damaged original was not preserved byte for byte");
                if (partial)
                {
                    Assert(downloader.Requests.Count == 0 && updates.RateLimitCheckpoint?.RetryNotBeforeUtc == start.AddMinutes(3),
                        "an invalid attempt date overrode an independently readable active cooldown");
                    clock.Advance(TimeSpan.FromMinutes(3));
                    await Call(fixture.Window, "RunScheduledUpdateCheckAsync");
                    var document = JsonNode.Parse(File.ReadAllText(store.FilePath));
                    Assert(document?["Independent"]?.GetValue<string>() == "keep"
                        && document?["RateLimit"]?["Future"]?.GetValue<bool>() == true,
                        "recovery or subsequent rate publication dropped an independent field");
                }
                Assert(downloader.Requests.Count == 1 && store.Load(clock.GetUtcNow()).RateLimit?.RetryNotBeforeUtc == clock.GetUtcNow().AddMinutes(9),
                    "a confirmed repaired checkpoint did not persist the next real rate response");
            }
            using var restarted = new SaFixture("updates0317-restart-repaired-schedule-" + original.Length);
            U317AttachUpdateSchedule(restarted.Window, directory, store, clock, locator, downloader);
            await Call(restarted.Window, "CheckForAppUpdatesAsync");
            Assert(downloader.Requests.Count == 1, "a new instance ignored the recovered schedule");
        }

        var replacementDirectory = Path.Combine(_directory, "updates0317-repair-replaced-schedule");
        Directory.CreateDirectory(replacementDirectory);
        var now = DateTimeOffset.UtcNow;
        var replacement = new UpdateScheduleCheckpoint(now, RateLimit: new(now.AddMinutes(6), 1));
        var writer = new UpdateScheduleStore(replacementDirectory);
        File.WriteAllText(writer.FilePath, "{");
        var observed = new UpdateScheduleStore(replacementDirectory) { BeforeRepairForChecks = () => writer.Save(replacement) };
        Assert(!observed.TryLoad(out _) && writer.Load(now) == replacement && !Directory.EnumerateFiles(replacementDirectory, "*.bak").Any(),
            "stale corruption repair archived or replaced an independently updated file");
    }

    private static async Task U317LateScheduleDamage()
    {
        foreach (var damage in new[] { "truncated-limit", "truncated-success", "partial", "missing-deadline", "explicit-null",
            "damaged-count", "null-count", "damaged-delay", "stronger-deadline" })
        {
            var directory = Path.Combine(_directory, "updates0317-schedule-damaged-after-load", damage);
            var store = new UpdateScheduleStore(directory);
            var clock = new V040UpdateClock();
            var start = clock.GetUtcNow();
            var secondary = damage is "damaged-count" or "null-count" or "damaged-delay" or "stronger-deadline";
            var oldLimit = secondary ? new UpdateRateLimitCheckpoint(start.AddMinutes(1), 1, TimeSpan.FromMinutes(1))
                : new UpdateRateLimitCheckpoint(start.AddMinutes(1));
            var previousSuccess = start.AddHours(-2);
            store.Save(new(start.AddHours(-1), previousSuccess, oldLimit));
            var (downloader, _) = V040PackageDownloader();
            var locator = new Velopack.Locators.TestVelopackLocator("YMusic.Native", "0.3.16", directory);
            var succeeds = damage == "truncated-success";
            var explicitNull = damage == "explicit-null";
            var requests = succeeds ? 2 : 1;
            UpdateRateLimitCheckpoint? closedLimit = null;
            using (var fixture = new SaFixture("updates0317-schedule-late-damage-" + damage))
            {
                var window = fixture.Window;
                var updates = U317AttachUpdateSchedule(window, directory, store, clock, locator, downloader);
                U317Set(window, "_updateCheckFrequency", UpdateCheckFrequency.Hourly);
                await Call(window, "CheckForAppUpdatesAsync");
                Assert(SaField<bool>(window, "_updateCheckpointLoaded") && updates.RateLimitCheckpoint == oldLimit
                    && downloader.Requests.Count == 0, "fixture did not establish its original readable checkpoint");

                clock.Advance(TimeSpan.FromMinutes(1));
                var now = clock.GetUtcNow();
                var longer = now.AddMinutes(45);
                var corrupt = damage switch
                {
                    "partial" => $$"""{"LastAttemptUtc":"{{now:O}}","LastSuccessUtc":"bad-date","RateLimit":{"RetryNotBeforeUtc":"{{oldLimit.RetryNotBeforeUtc:O}}","Future":23},"Independent":"keep"}""",
                    "missing-deadline" => $$"""{"LastAttemptUtc":"{{now:O}}","LastSuccessUtc":"{{previousSuccess:O}}","RateLimit":{"SecondaryLimitCount":1,"SecondaryLimitDelay":"00:03:00","Future":23},"Independent":"keep"}""",
                    "explicit-null" => $$"""{"LastAttemptUtc":null,"LastSuccessUtc":null,"RateLimit":{"RetryNotBeforeUtc":"{{oldLimit.RetryNotBeforeUtc:O}}","SecondaryLimitDelay":"bad","Future":23},"Independent":"keep"}""",
                    "damaged-count" => $$"""{"LastAttemptUtc":"{{now:O}}","LastSuccessUtc":"{{previousSuccess:O}}","RateLimit":{"RetryNotBeforeUtc":"{{oldLimit.RetryNotBeforeUtc:O}}","SecondaryLimitCount":"bad","SecondaryLimitDelay":"00:01:00","Future":23},"Independent":"keep"}""",
                    "null-count" => $$"""{"LastAttemptUtc":"{{now:O}}","LastSuccessUtc":"{{previousSuccess:O}}","RateLimit":{"RetryNotBeforeUtc":"{{oldLimit.RetryNotBeforeUtc:O}}","SecondaryLimitCount":null,"SecondaryLimitDelay":"00:01:00","Future":23},"Independent":"keep"}""",
                    "damaged-delay" => $$"""{"LastAttemptUtc":"{{now:O}}","LastSuccessUtc":"{{previousSuccess:O}}","RateLimit":{"RetryNotBeforeUtc":"{{oldLimit.RetryNotBeforeUtc:O}}","SecondaryLimitCount":1,"SecondaryLimitDelay":"bad","Future":23},"Independent":"keep"}""",
                    "stronger-deadline" => $$"""{"LastAttemptUtc":"{{now:O}}","LastSuccessUtc":"{{previousSuccess:O}}","RateLimit":{"RetryNotBeforeUtc":"{{longer:O}}","SecondaryLimitCount":"bad","SecondaryLimitDelay":"00:01:00","Future":23},"Independent":"keep"}""",
                    _ => "{"
                };
                downloader.LatestStatus = succeeds ? HttpStatusCode.OK : HttpStatusCode.TooManyRequests;
                downloader.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromMinutes(10));
                downloader.BeforeLatest = _ =>
                {
                    Assert(store.Load(now).LastAttemptUtc == now, "scheduled attempt was not published before the induced damage");
                    File.WriteAllText(store.FilePath, corrupt);
                    return Task.CompletedTask;
                };
                await Call(window, "RunScheduledUpdateCheckAsync");
                downloader.BeforeLatest = null;
                var archive = Directory.EnumerateFiles(directory, "*.bak").Single();
                var saved = store.Load(now);
                var expectedDeadline = succeeds ? (DateTimeOffset?)null : damage == "stronger-deadline" ? longer : now.AddMinutes(10);
                Assert(downloader.Requests.Count == requests && File.ReadAllBytes(archive).SequenceEqual(Encoding.UTF8.GetBytes(corrupt))
                    && saved.RateLimit?.RetryNotBeforeUtc == expectedDeadline
                    && saved.LastAttemptUtc == (explicitNull ? null : now)
                    && saved.LastSuccessUtc == (explicitNull ? null : succeeds ? now : previousSuccess),
                    "repair did not preserve the exact observed timestamps and fresh result or ignored an explicit null");
                if (damage is "damaged-count" or "null-count" or "damaged-delay" or "stronger-deadline")
                    Assert(saved.RateLimit?.SecondaryLimitCount == 2 && saved.RateLimit?.SecondaryLimitDelay == TimeSpan.FromMinutes(10),
                        "a damaged history field prevented persistence of the new secondary decision");
                if (!damage.StartsWith("truncated", StringComparison.Ordinal))
                {
                    var document = JsonNode.Parse(File.ReadAllText(store.FilePath));
                    Assert(document?["Independent"]?.GetValue<string>() == "keep"
                        && document?["RateLimit"]?["Future"]?.GetValue<int>() == 23, "repair overwrote readable checkpoint fields");
                }
                if (explicitNull) continue;
                if (damage == "null-count")
                {
                    window.CloseSaveFailurePrompt = message => throw new InvalidOperationException("repaired pause should allow close: " + message);
                    window.Close();
                    await Until(() => SaField<bool>(window, "_closed"));
                    closedLimit = saved.RateLimit;
                    Assert(store.Load(clock.GetUtcNow()).RateLimit == saved.RateLimit, "ordinary close changed the repaired pause");
                }
                else
                {
                    clock.Advance(TimeSpan.FromMinutes(10));
                    await Call(window, "RunScheduledUpdateCheckAsync");
                    Assert(downloader.Requests.Count == requests && SaField<DispatcherTimer?>(window, "_updateScheduleTimer")?.Interval == TimeSpan.FromMinutes(50),
                        "repair lost the original hourly attempt in the current process");
                }
                if (damage == "stronger-deadline")
                {
                    SaClick(Control<Button>(window, "SettingsButton"));
                    var panel = (StackPanel)Control<ContentControl>(window, "SettingsUpdates").Content;
                    var manual = panel.Children.OfType<Button>().Single();
                    await Until(() => panel.IsLoaded && manual.IsLoaded && manual.IsVisible && manual.IsEnabled);
                    var before = updates.State;
                    SaClick(manual);
                    await Until(() => !ReferenceEquals(updates.State, before) && !SaField<bool>(window, "_updateCheckInProgress"));
                    Assert(updates.RateLimitCheckpoint == saved.RateLimit && downloader.Requests.Count == requests
                        && store.Load(clock.GetUtcNow()).LastAttemptUtc == now,
                        "manual check disregarded the stronger saved deadline or recorded a skipped request");

                    clock.Advance(TimeSpan.FromMinutes(35));
                    downloader.RetryAfter = null;
                    before = updates.State;
                    SaClick(manual);
                    await Until(() => !ReferenceEquals(updates.State, before) && !SaField<bool>(window, "_updateCheckInProgress"));
                    requests++;
                    var escalated = new UpdateRateLimitCheckpoint(clock.GetUtcNow().AddMinutes(20), 3, TimeSpan.FromMinutes(20));
                    Assert(downloader.Requests.Count == requests && updates.RateLimitCheckpoint == escalated
                        && store.Load(clock.GetUtcNow()).RateLimit == escalated, "the repaired history did not escalate a new secondary response without Retry-After");
                    using (var cold = new SaFixture("updates0317-reopen-repaired-secondary-history"))
                    {
                        var coldUpdates = U317AttachUpdateSchedule(cold.Window, directory, store, clock, locator, downloader);
                        U317Set(cold.Window, "_updateCheckFrequency", UpdateCheckFrequency.Startup);
                        await Call(cold.Window, "CheckForAppUpdatesAsync");
                        Assert(downloader.Requests.Count == requests && coldUpdates.RateLimitCheckpoint == escalated
                            && coldUpdates.RemainingRateLimit == TimeSpan.FromMinutes(20), "restart changed the escalated secondary pause");
                    }

                    clock.Advance(TimeSpan.FromMinutes(20));
                    downloader.LatestStatus = HttpStatusCode.OK;
                    SaClick(manual);
                    await Until(() => updates.State.CheckSucceeded && !SaField<bool>(window, "_updateCheckInProgress"));
                    requests += 2;
                    Assert(downloader.Requests.Count == requests && updates.RateLimitCheckpoint is null && store.Load(clock.GetUtcNow()).RateLimit is null,
                        "the accepted saved decision was not cleared by a successful manual check at expiry");
                }
            }
            using var reopened = new SaFixture("updates0317-reopen-repaired-attempt-" + damage);
            var reopenedUpdates = U317AttachUpdateSchedule(reopened.Window, directory, store, clock, locator, downloader);
            var frequency = damage == "null-count" ? UpdateCheckFrequency.Startup : UpdateCheckFrequency.Hourly;
            U317Set(reopened.Window, "_updateCheckFrequency", frequency);
            await Call(reopened.Window, "CheckForAppUpdatesAsync");
            if (damage == "null-count")
                Assert(closedLimit is not null && reopenedUpdates.RateLimitCheckpoint == closedLimit && reopenedUpdates.RemainingRateLimit == TimeSpan.FromMinutes(10),
                    "the restart did not restore the newly saved active pause before Startup");
            var expectedMinutes = damage switch { "null-count" => 10, "stronger-deadline" => 60, _ => 50 };
            Assert(downloader.Requests.Count == requests
                && SaField<DispatcherTimer?>(reopened.Window, "_updateScheduleTimer")?.Interval == TimeSpan.FromMinutes(expectedMinutes),
                "restart sent Latest before the repaired schedule allowed it");
        }
    }

    private static async Task U317LateStoredRateAdmission()
    {
        foreach (var mode in new[] { "admission", "completion", "completion-empty-baseline", "completion-write-blocked" })
        {
            var duringLatest = mode != "admission";
            var writeBlocked = mode == "completion-write-blocked";
            var emptyBaseline = mode == "completion-empty-baseline" || writeBlocked;
            var directory = Path.Combine(_directory, "updates0317-stronger-saved-limit", mode);
            var store = new UpdateScheduleStore(directory);
            var clock = new V040UpdateClock();
            var start = clock.GetUtcNow();
            var original = new UpdateScheduleCheckpoint(start.AddHours(-2), start.AddHours(-3), emptyBaseline ? null : new(start.AddMinutes(1)));
            store.Save(original);
            var (downloader, _) = V040PackageDownloader();
            downloader.ReleaseVersion = "0.3.16";
            var locator = new Velopack.Locators.TestVelopackLocator("YMusic.Native", "0.3.16", directory);
            using var fixture = new SaFixture("updates0317-stronger-saved-limit-" + mode);
            var window = fixture.Window;
            var updates = U317AttachUpdateSchedule(window, directory, store, clock, locator, downloader);
            U317Set(window, "_updateCheckFrequency", emptyBaseline ? UpdateCheckFrequency.Off : UpdateCheckFrequency.Startup);
            await Call(window, "CheckForAppUpdatesAsync");
            Assert(updates.RateLimitCheckpoint == original.RateLimit && downloader.Requests.Count == 0,
                "the startup gate did not load the original pause");

            clock.Advance(TimeSpan.FromMinutes(1));
            var now = clock.GetUtcNow();
            var later = new UpdateRateLimitCheckpoint(now.AddMinutes(45), 2, TimeSpan.FromMinutes(10));
            void SaveLater()
            {
                var document = JsonNode.Parse(File.ReadAllText(store.FilePath))!.AsObject();
                document["RateLimit"] = System.Text.Json.JsonSerializer.SerializeToNode(later);
                document["Independent"] = "keep";
                File.WriteAllText(store.FilePath, document.ToJsonString());
            }
            FileStream? pendingWrite = null;
            if (duringLatest)
                downloader.BeforeLatest = _ =>
                {
                    SaveLater();
                    if (writeBlocked) pendingWrite = File.OpenRead(store.FilePath);
                    return Task.CompletedTask;
                };
            else SaveLater();

            SaClick(Control<Button>(window, "SettingsButton"));
            var panel = (StackPanel)Control<ContentControl>(window, "SettingsUpdates").Content;
            var manual = panel.Children.OfType<Button>().Single();
            await Until(() => panel.IsLoaded && manual.IsLoaded && manual.IsVisible && manual.IsEnabled);
            async Task ClickManual()
            {
                var before = updates.State;
                SaClick(manual);
                await Until(() => !ReferenceEquals(updates.State, before) && manual.IsEnabled
                    && !SaField<bool>(window, "_updateCheckInProgress"));
            }

            var attempts = duringLatest ? 1 : 0;
            var expectedAttempt = duringLatest ? now : original.LastAttemptUtc;
            var expectedSuccess = duringLatest ? now : original.LastSuccessUtc;
            try
            {
                await ClickManual();
                Assert(downloader.Requests.Count == attempts && updates.RateLimitCheckpoint == later
                    && store.Load(now) == new UpdateScheduleCheckpoint(expectedAttempt, writeBlocked ? original.LastSuccessUtc : expectedSuccess, later),
                    "manual admission ignored a later saved pause, or a successful Latest lost a concurrent pause");
            }
            finally { pendingWrite?.Dispose(); downloader.BeforeLatest = null; }
            if (!duringLatest)
                Assert(SaField<bool>(window, "_startupUpdatePending"), "a denied manual check consumed the pending startup check");
            else if (!writeBlocked)
            {
                var preferences = new PreferencesStore(directory);
                preferences.Save(new());
                var prompts = new List<string>();
                U317Set(window, "_restore", true);
                U317Set(window, "_preferencesLoadFailed", true);
                U317Set(window, "_preferenceStore", preferences);
                window.CloseSaveFailurePrompt = message => { prompts.Add(message); return MessageBoxResult.Cancel; };
                try
                {
                    using var busy = new FileStream(preferences.FilePath, FileMode.Open, FileAccess.Read, FileShare.None);
                    window.Close();
                    await Until(() => prompts.Count == 1 && !SaField<bool>(window, "_closeCommitInProgress"));
                    Assert(!SaField<bool>(window, "_closed") && updates.RateLimitCheckpoint == later && store.Load(now).RateLimit == later
                        && prompts[0].Contains("настройк", StringComparison.OrdinalIgnoreCase)
                        && !prompts[0].Contains("время проверки обновлений", StringComparison.Ordinal), "canceling close lost the preserved active pause");
                }
                finally
                {
                    window.CloseSaveFailurePrompt = null;
                    U317Set(window, "_restore", false);
                    U317Set(window, "_preferencesLoadFailed", false);
                    U317Set(window, "_preferenceStore", null);
                }
            }

            clock.Advance(TimeSpan.FromMinutes(10));
            if (emptyBaseline)
            {
                using var unreadable = new FileStream(store.FilePath, FileMode.Open, FileAccess.Read, FileShare.None);
                await ClickManual();
            }
            else await ClickManual();
            Assert(downloader.Requests.Count == attempts && store.Load(clock.GetUtcNow()).LastAttemptUtc == expectedAttempt,
                "the same session sent Latest or advanced the attempt before the later permission time");
            if (writeBlocked)
            {
                Assert(store.Load(clock.GetUtcNow()).LastSuccessUtc == original.LastSuccessUtc, "locked read rewrote the pending success");
                await ClickManual();
                Assert(downloader.Requests.Count == attempts && store.Load(clock.GetUtcNow()) == new UpdateScheduleCheckpoint(expectedAttempt, expectedSuccess, later),
                    "retry did not persist the original success without another Latest");
            }

            clock.Advance(TimeSpan.FromMinutes(35));
            await ClickManual();
            var expiry = clock.GetUtcNow();
            Assert(updates.State.CheckSucceeded && downloader.Requests.Count == attempts + 1 && updates.RateLimitCheckpoint is null
                && store.Load(expiry) == new UpdateScheduleCheckpoint(expiry, expiry)
                && JsonNode.Parse(File.ReadAllText(store.FilePath))?["Independent"]?.GetValue<string>() == "keep",
                "the exact later deadline did not admit one Latest, clear its pause, and preserve independent storage fields");
        }
    }

    private static async Task U317PartialRateAdmission()
    {
        foreach (var restart in new[] { false, true })
        {
            var directory = Path.Combine(_directory, "updates0317-partial-admission", restart.ToString());
            var store = new UpdateScheduleStore(directory);
            var clock = new V040UpdateClock();
            var start = clock.GetUtcNow();
            var old = new UpdateScheduleCheckpoint(start.AddHours(-2), start.AddHours(-3), new(start.AddMinutes(1), 2, TimeSpan.FromMinutes(10)));
            store.Save(old);
            var (downloader, _) = V040PackageDownloader();
            downloader.LatestStatus = HttpStatusCode.TooManyRequests;
            var locator = new Velopack.Locators.TestVelopackLocator("YMusic.Native", "0.3.16", directory);
            using var fixture = new SaFixture("updates0317-partial-admission-" + restart);
            var window = fixture.Window;
            var updates = U317AttachUpdateSchedule(window, directory, store, clock, locator, downloader);
            await Call(window, "CheckForAppUpdatesAsync");
            Assert(updates.RateLimitCheckpoint == old.RateLimit && downloader.Requests.Count == 0, "fixture did not load the verified streak");
            clock.Advance(TimeSpan.FromMinutes(1));
            var now = clock.GetUtcNow();
            var due = now.AddMinutes(45);
            var damaged = $$"""{"LastAttemptUtc":"{{old.LastAttemptUtc:O}}","LastSuccessUtc":"{{old.LastSuccessUtc:O}}","RateLimit":{"RetryNotBeforeUtc":"{{due:O}}","SecondaryLimitCount":null,"SecondaryLimitDelay":"00:01:00","Future":23},"Independent":"keep"}""";
            File.WriteAllText(store.FilePath, damaged);

            SaClick(Control<Button>(window, "SettingsButton"));
            var panel = (StackPanel)Control<ContentControl>(window, "SettingsUpdates").Content;
            var manual = panel.Children.OfType<Button>().Single();
            await Until(() => panel.IsLoaded && manual.IsLoaded && manual.IsVisible && manual.IsEnabled);
            var before = updates.State;
            async Task ClickManual()
            {
                SaClick(manual);
                await Until(() => !ReferenceEquals(updates.State, before) && manual.IsEnabled && !SaField<bool>(window, "_updateCheckInProgress"));
            }
            if (restart)
            {
                using var busy = File.OpenRead(store.FilePath);
                await ClickManual();
                Assert(File.ReadAllText(store.FilePath) == damaged && SaField<UpdateScheduleCheckpoint>(window, "_updateCheckpoint").RateLimit == old.RateLimit,
                    "a temporary repair failure treated the partial checkpoint as committed");
            }
            else await ClickManual();
            var merged = new UpdateRateLimitCheckpoint(due, 2, TimeSpan.FromMinutes(10));
            Assert(updates.RateLimitCheckpoint == merged && downloader.Requests.Count == 0, "partial admission lost the streak or sent Latest early");
            void AssertRepair()
            {
                var document = JsonNode.Parse(File.ReadAllText(store.FilePath));
                Assert(File.ReadAllText(Directory.EnumerateFiles(directory, "*.bak").Single()) == damaged
                    && store.Load(now) == (old with { RateLimit = merged }) && document?["Independent"]?.GetValue<string>() == "keep"
                    && document?["RateLimit"]?["Future"]?.GetValue<int>() == 23, "guarded repair did not archive the source and preserve independent fields");
            }

            async Task CheckAtExpiry(MainWindow target, UpdateSupport service)
            {
                clock.Advance(TimeSpan.FromMinutes(45));
                await Call(target, "CheckAppUpdateNowAsync", service);
                var escalated = new UpdateRateLimitCheckpoint(due.AddMinutes(20), 3, TimeSpan.FromMinutes(20));
                Assert(downloader.Requests.Count == 1 && service.RateLimitCheckpoint == escalated
                    && store.Load(due).RateLimit == escalated, "the next secondary response did not continue the verified streak");
            }

            if (!restart)
            {
                AssertRepair();
                await CheckAtExpiry(window, updates);
            }
            else
            {
                window.CloseSaveFailurePrompt = message => throw new InvalidOperationException("repaired checkpoint blocked ordinary close: " + message);
                window.Close();
                await Until(() => SaField<bool>(window, "_closed"));
                AssertRepair();
                using var reopened = new SaFixture("updates0317-partial-admission-reopened");
                var service = U317AttachUpdateSchedule(reopened.Window, directory, store, clock, locator, downloader);
                await Call(reopened.Window, "CheckForAppUpdatesAsync");
                Assert(downloader.Requests.Count == 0 && service.RateLimitCheckpoint == merged, "ordinary restart ignored the repaired pause");
                await CheckAtExpiry(reopened.Window, service);
            }
        }
    }

    private static async Task U317RestoredHourlyAdmission()
    {
        foreach (var mode in new[] { "pause", "named-stream", "no-pause", "expired" })
        {
            var namedStream = mode == "named-stream";
            var rateAdmission = mode is "pause" or "named-stream";
            var directory = Path.Combine(_directory, "updates0317-restored-hourly", mode);
            var store = new UpdateScheduleStore(directory);
            if (namedStream && new DriveInfo(Path.GetPathRoot(store.FilePath)!).DriveFormat != "NTFS") continue;
            var clock = new V040UpdateClock();
            var start = clock.GetUtcNow();
            var original = new UpdateScheduleCheckpoint(start.AddHours(-2), start.AddHours(-3), new(start.AddMinutes(1)));
            store.Save(original);
            var (downloader, _) = V040PackageDownloader();
            downloader.ReleaseVersion = "0.3.16";
            var locator = new Velopack.Locators.TestVelopackLocator("YMusic.Native", "0.3.16", directory);
            using var fixture = new SaFixture("updates0317-restored-hourly-" + mode);
            var window = fixture.Window;
            var updates = U317AttachUpdateSchedule(window, directory, store, clock, locator, downloader);
            U317Set(window, "_updateCheckFrequency", UpdateCheckFrequency.Hourly);
            await Call(window, "CheckForAppUpdatesAsync");
            Assert(updates.RateLimitCheckpoint == original.RateLimit && downloader.Requests.Count == 0, "fixture did not load the first schedule");
            clock.Advance(TimeSpan.FromMinutes(1));
            var now = clock.GetUtcNow();
            var limit = rateAdmission ? new UpdateRateLimitCheckpoint(now.AddMinutes(10))
                : mode == "expired" ? new UpdateRateLimitCheckpoint(now.AddSeconds(-10)) : null;
            var restored = new UpdateScheduleCheckpoint(now.AddMinutes(-1), now.AddMinutes(-2), limit);
            store.Save(restored);
            var stream = store.FilePath + ":checkpoint-fixture";
            if (namedStream) File.WriteAllText(stream, "preserve stream");
            var bytes = File.ReadAllBytes(store.FilePath);

            var before = updates.State;
            if (namedStream) await Call(window, "CheckManualUpdateNowAsync", updates);
            else await Call(window, "RunScheduledUpdateCheckAsync");
            var observed = SaField<UpdateScheduleCheckpoint>(window, "_updateCheckpoint");
            Assert((rateAdmission ? updates.RateLimitCheckpoint == restored.RateLimit : ReferenceEquals(updates.State, before)) && downloader.Requests.Count == 0
                && observed.LastAttemptUtc == restored.LastAttemptUtc && observed.LastSuccessUtc == restored.LastSuccessUtc
                && File.ReadAllBytes(store.FilePath).SequenceEqual(bytes), "admission did not respect the restored checkpoint or recorded a skipped attempt");
            clock.Advance(TimeSpan.FromMinutes(10));
            await Call(window, "RunScheduledUpdateCheckAsync");
            Assert(downloader.Requests.Count == 0 && File.ReadAllBytes(store.FilePath).SequenceEqual(bytes)
                && SaField<DispatcherTimer?>(window, "_updateScheduleTimer")?.Interval == TimeSpan.FromMinutes(49),
                "the hourly interval advanced from the old attempt after the restored pause expired");
            if (mode == "no-pause")
            {
                clock.Advance(TimeSpan.FromMinutes(49));
                await Call(window, "RunScheduledUpdateCheckAsync");
            }
            else await Call(window, "CheckManualUpdateNowAsync", updates);
            Assert(updates.State.CheckSucceeded && downloader.Requests.Count == 1, "the due automatic or explicit manual check was unavailable");
            if (namedStream)
                Assert(File.ReadAllBytes(store.FilePath).SequenceEqual(bytes) && File.ReadAllText(stream) == "preserve stream",
                    "failed replacement changed the readable checkpoint or its named stream");
            else Assert(store.Load(clock.GetUtcNow()) == new UpdateScheduleCheckpoint(clock.GetUtcNow(), clock.GetUtcNow()),
                "the admitted manual attempt did not update the writable checkpoint");
        }
    }

    private static async Task U317ConcurrentRateDecision()
    {
        foreach (var (apply, replacement) in new[] { (false, "pause"), (true, "pause"), (false, "null"), (true, "null"), (false, "missing") })
        {
            var directory = Path.Combine(_directory, "updates0317-concurrent-rate", $"{apply}-{replacement}");
            var store = new UpdateScheduleStore(directory);
            var clock = new V040UpdateClock();
            var now = clock.GetUtcNow();
            var old = new UpdateScheduleCheckpoint(now.AddHours(-1), now.AddHours(-2), new(now.AddMinutes(-1), 1, TimeSpan.FromMinutes(1)));
            store.Save(old);
            var (downloader, bytes) = V040PackageDownloader();
            UpdateScheduleCheckpoint? handoff = null;
            var locator = new V040NoScanLocator(directory) { BeforeStart = () => handoff = store.Load(clock.GetUtcNow()) };
            Directory.CreateDirectory(locator.PackagesDir!);
            Directory.CreateDirectory(locator.RootAppDir);
            File.WriteAllBytes(Path.Combine(locator.PackagesDir!, V040GithubDownloader.PackageName), bytes);
            File.WriteAllText(locator.UpdateExePath, "fixture archive marker");
            var independentSuccess = now.AddMinutes(-20);
            var responseAt = now.AddMinutes(1);
            var concurrent = replacement == "pause" ? new UpdateRateLimitCheckpoint(responseAt.AddMinutes(5), 3, TimeSpan.FromMinutes(7)) : null;
            var merged = new UpdateRateLimitCheckpoint(responseAt.AddMinutes(25), concurrent?.SecondaryLimitCount ?? 2, TimeSpan.FromMinutes(25));
            using (var fixture = new SaFixture($"updates0317-concurrent-rate-{apply}-{replacement}"))
            {
                var window = fixture.Window;
                var updates = U317AttachUpdateSchedule(window, directory, store, clock, locator, downloader);
                Assert((await updates.CheckAsync()).Status == AppUpdateStatus.Downloaded, "fixture did not prepare its fake package");
                U317Set(window, "_updateCheckFrequency", UpdateCheckFrequency.Off);
                await Call(window, "CheckForAppUpdatesAsync");
                Assert(updates.RateLimitCheckpoint == old.RateLimit, "fixture did not observe the expired original pause");

                FileStream? busy = null;
                downloader.LatestStatus = HttpStatusCode.TooManyRequests;
                downloader.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromMinutes(25));
                downloader.BeforeLatest = _ =>
                {
                    Assert(store.Load(now).LastAttemptUtc == now, "fixture did not admit Latest before changing the file");
                    clock.Advance(TimeSpan.FromMinutes(1));
                    if (replacement == "missing")
                    {
                        File.Delete(store.FilePath);
                        return Task.CompletedTask;
                    }
                    var document = JsonNode.Parse(File.ReadAllText(store.FilePath))!.AsObject();
                    var rate = System.Text.Json.JsonSerializer.SerializeToNode(concurrent)?.AsObject();
                    if (rate is not null) rate["Future"] = 23;
                    document["RateLimit"] = rate;
                    document["LastAttemptUtc"] = JsonValue.Create(responseAt);
                    document["LastSuccessUtc"] = JsonValue.Create(independentSuccess);
                    document["Independent"] = "keep";
                    File.WriteAllText(store.FilePath, document.ToJsonString());
                    busy = File.OpenRead(store.FilePath);
                    return Task.CompletedTask;
                };
                try
                {
                    await Call(window, "CheckManualUpdateNowAsync", updates);
                    var beforeClose = replacement == "missing" ? merged : concurrent;
                    Assert(downloader.Requests.Count == 3 && store.Load(responseAt).RateLimit == beforeClose
                        && updates.RateLimitCheckpoint?.RetryNotBeforeUtc == merged.RetryNotBeforeUtc,
                        "fixture did not retain the admitted stronger reply when publication was blocked");
                }
                finally { busy?.Dispose(); downloader.BeforeLatest = null; }

                var expected = replacement == "missing" ? new UpdateScheduleCheckpoint(now, RateLimit: merged)
                    : new UpdateScheduleCheckpoint(responseAt, independentSuccess, merged);
                if (apply)
                {
                    Assert(await (Task<bool>)Call(window, "ApplyAppUpdateAsync", updates) && locator.Starts == 1 && handoff == expected,
                        "Apply did not commit the stronger decision before handing off to the fake updater");
                }
                else
                {
                    window.CloseSaveFailurePrompt = message => throw new InvalidOperationException("merged limits should allow close: " + message);
                    window.Close();
                    await Until(() => SaField<bool>(window, "_closed"));
                }
                var saved = JsonNode.Parse(File.ReadAllText(store.FilePath));
                Assert(store.Load(responseAt) == expected
                    && (replacement == "missing" || saved?["Independent"]?.GetValue<string>() == "keep")
                    && (concurrent is null || saved?["RateLimit"]?["Future"]?.GetValue<int>() == 23),
                    "the completed transaction lost the stronger limit, compatible history or an independent field");
            }

            clock.Advance(TimeSpan.FromMinutes(10));
            using var reopened = new SaFixture($"updates0317-concurrent-rate-reopened-{apply}-{replacement}");
            var restored = U317AttachUpdateSchedule(reopened.Window, directory, store, clock, locator, downloader);
            await Call(reopened.Window, "CheckForAppUpdatesAsync");
            Assert(downloader.Requests.Count == 3 && restored.RateLimitCheckpoint == merged,
                "restart admitted Latest in the gap between the concurrent and accepted limits");
        }
    }

    private static async Task U317RestoredExpiredRateHistory()
    {
        foreach (var succeeds in new[] { false, true })
        {
            var directory = Path.Combine(_directory, "updates0317-restored-expired-rate", succeeds.ToString());
            var store = new UpdateScheduleStore(directory);
            var clock = new V040UpdateClock();
            var now = clock.GetUtcNow();
            var original = new UpdateScheduleCheckpoint(now.AddHours(-1), now.AddHours(-2), new(now.AddMinutes(-2), 1, TimeSpan.FromMinutes(1)));
            store.Save(original);
            var (downloader, _) = V040PackageDownloader();
            downloader.ReleaseVersion = "0.3.16";
            downloader.LatestStatus = succeeds ? HttpStatusCode.OK : HttpStatusCode.TooManyRequests;
            var locator = new Velopack.Locators.TestVelopackLocator("YMusic.Native", "0.3.16", directory);
            var history = new UpdateRateLimitCheckpoint(now.AddMinutes(-1), 4, TimeSpan.FromMinutes(16));
            var expected = succeeds ? null : new UpdateRateLimitCheckpoint(now.AddMinutes(32), 5, TimeSpan.FromMinutes(32));
            using (var fixture = new SaFixture("updates0317-restored-expired-rate-" + succeeds))
            {
                var updates = U317AttachUpdateSchedule(fixture.Window, directory, store, clock, locator, downloader);
                U317Set(fixture.Window, "_updateCheckFrequency", UpdateCheckFrequency.Off);
                await Call(fixture.Window, "CheckForAppUpdatesAsync");
                Assert(updates.RateLimitCheckpoint == original.RateLimit && downloader.Requests.Count == 0, "fixture did not load the expired original history");
                store.Save(original with { RateLimit = history });
                await Call(fixture.Window, "CheckManualUpdateNowAsync", updates);
                Assert(downloader.Requests.Count == 1 && updates.State.CheckSucceeded == succeeds && updates.RateLimitCheckpoint == expected
                    && store.Load(now) == new UpdateScheduleCheckpoint(now, succeeds ? now : original.LastSuccessUtc, expected),
                    "Latest did not extend the admitted secondary history or clear the exact expired baseline after success");
            }
            using var restarted = new SaFixture("updates0317-restored-expired-rate-restarted-" + succeeds);
            var restored = U317AttachUpdateSchedule(restarted.Window, directory, store, clock, locator, downloader);
            U317Set(restarted.Window, "_updateCheckFrequency", succeeds ? UpdateCheckFrequency.Off : UpdateCheckFrequency.Startup);
            await Call(restarted.Window, "CheckForAppUpdatesAsync");
            Assert(downloader.Requests.Count == 1 && restored.RateLimitCheckpoint == expected, "restart did not restore the exact result of Latest");
        }
    }

    private static async Task U317FailedLatestCheckpoint()
    {
        foreach (var outcome in new[] { "unchanged", "damaged", "busy-damage", "older", "empty-attempt", "later" })
        {
            var directory = Path.Combine(_directory, "updates0317-failed-latest-checkpoint", outcome);
            var store = new UpdateScheduleStore(directory);
            var clock = new V040UpdateClock();
            var now = clock.GetUtcNow();
            var success = now.AddHours(-3);
            var expectedAttempt = outcome == "later" ? now.AddMinutes(1) : now;
            var independentRestore = outcome is "older" or "empty-attempt" or "later";
            store.Save(new(now.AddHours(-2), success));
            var (downloader, _) = V040PackageDownloader();
            var locator = new Velopack.Locators.TestVelopackLocator("YMusic.Native", "0.3.16", directory);
            downloader.LatestStatus = HttpStatusCode.ServiceUnavailable;
            FileStream? busy = null;
            FileSecurityPreservation.FileRevision? attemptRevision = null;
            byte[]? admittedBytes = null;
            downloader.BeforeLatest = _ =>
            {
                Assert(store.Load(now).LastAttemptUtc == now, "fixture did not publish the attempt before returning 503");
                if (outcome == "unchanged")
                {
                    admittedBytes = File.ReadAllBytes(store.FilePath);
                    using var held = File.OpenRead(store.FilePath);
                    attemptRevision = FileSecurityPreservation.GetPersistentRevision(held.SafeFileHandle);
                }
                else if (independentRestore)
                {
                    var document = JsonNode.Parse(File.ReadAllText(store.FilePath))!.AsObject();
                    var restoredAttempt = outcome == "older" ? now.AddHours(-2) : (DateTimeOffset?)null;
                    if (outcome == "later")
                    {
                        clock.Advance(TimeSpan.FromMinutes(1));
                        restoredAttempt = expectedAttempt;
                    }
                    document["LastAttemptUtc"] = System.Text.Json.JsonSerializer.SerializeToNode(restoredAttempt);
                    document["Independent"] = "keep";
                    File.WriteAllText(store.FilePath, document.ToJsonString());
                }
                else File.WriteAllText(store.FilePath, "{");
                if (outcome == "busy-damage") busy = File.OpenRead(store.FilePath);
                return Task.CompletedTask;
            };

            using (var fixture = new SaFixture("updates0317-failed-latest-checkpoint-" + outcome))
            {
                var window = fixture.Window;
                U317AttachUpdateSchedule(window, directory, store, clock, locator, downloader);
                U317Set(window, "_updateCheckFrequency", UpdateCheckFrequency.Hourly);
                try
                {
                    await Call(window, "CheckForAppUpdatesAsync");
                    Assert(downloader.Requests.Count == 1, "failed Latest was retried unexpectedly");
                    if (outcome == "busy-damage")
                        Assert(File.ReadAllText(store.FilePath) == "{" && SaField<bool>(window, "_updateCheckpointNeedsVerification"),
                            "verification lost its retry or mutated a locked checkpoint");
                    else
                        Assert(store.Load(clock.GetUtcNow()) == new UpdateScheduleCheckpoint(expectedAttempt, success)
                            && !SaField<bool>(window, "_updateCheckpointNeedsVerification"), "completed attempt was not confirmed after 503");
                    if (independentRestore)
                        Assert(SaField<DispatcherTimer?>(window, "_updateScheduleTimer")?.Interval == TimeSpan.FromHours(1),
                            "the active Hourly timer lost a completed attempt after a readable file restore");
                }
                finally { busy?.Dispose(); downloader.BeforeLatest = null; }
                window.CloseSaveFailurePrompt = message => throw new InvalidOperationException("checkpoint retry should allow close: " + message);
                window.Close();
                await Until(() => SaField<bool>(window, "_closed"));
            }
            Assert(store.Load(clock.GetUtcNow()) == new UpdateScheduleCheckpoint(expectedAttempt, success), "exit did not retain the confirmed attempt and previous success");
            if (outcome == "unchanged")
            {
                using var held = File.OpenRead(store.FilePath);
                Assert(attemptRevision is not null && FileSecurityPreservation.GetPersistentRevision(held.SafeFileHandle) == attemptRevision
                    && admittedBytes is not null && File.ReadAllBytes(store.FilePath).SequenceEqual(admittedBytes)
                    && !Directory.EnumerateFiles(directory, "*.bak").Any(), "verification republished unchanged storage after 503");
            }
            else if (independentRestore)
                Assert(!Directory.EnumerateFiles(directory, "*.bak").Any()
                    && JsonNode.Parse(File.ReadAllText(store.FilePath))?["Independent"]?.GetValue<string>() == "keep", "readable restore lost an independent field");
            else Assert(File.ReadAllText(Directory.EnumerateFiles(directory, "*.bak").Single()) == "{", "exact late damage was not archived");

            clock.Advance(TimeSpan.FromMinutes(10));
            using var restarted = new SaFixture("updates0317-failed-latest-checkpoint-restart-" + outcome);
            U317AttachUpdateSchedule(restarted.Window, directory, store, clock, locator, downloader);
            U317Set(restarted.Window, "_updateCheckFrequency", UpdateCheckFrequency.Hourly);
            await Call(restarted.Window, "CheckForAppUpdatesAsync");
            Assert(downloader.Requests.Count == 1 && SaField<DispatcherTimer?>(restarted.Window, "_updateScheduleTimer")?.Interval == TimeSpan.FromMinutes(50),
                "503 checkpoint loss caused a premature request after restart");
        }
    }

    private static async Task U317SettleAdmittedLatest()
    {
        foreach (var action in new[] { "close", "apply", "unresolved" })
        {
            var directory = Path.Combine(_directory, "updates0317-exit-settle", action);
            var store = new UpdateScheduleStore(directory);
            var clock = new V040UpdateClock();
            var now = clock.GetUtcNow();
            using var releaseDecision = new ManualResetEventSlim();
            var classified = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var serviceClock = new U317RateDecisionClock(now, classified, releaseDecision);
            var (downloader, bytes) = V040PackageDownloader();
            UpdateScheduleCheckpoint? atHandoff = null;
            var locator = new V040NoScanLocator(directory) { BeforeStart = () => atHandoff = store.Load(now) };
            Directory.CreateDirectory(locator.PackagesDir!);
            Directory.CreateDirectory(locator.RootAppDir);
            File.WriteAllBytes(Path.Combine(locator.PackagesDir!, V040GithubDownloader.PackageName), bytes);
            File.WriteAllText(locator.UpdateExePath, "fixture archive marker");
            using var fixture = new SaFixture("updates0317-exit-settle-" + action);
            var window = fixture.Window;
            var updates = V040Service(directory, locator, downloader, clock: serviceClock);
            U317Set(window, "_appUpdatesOverride", updates);
            U317Set(window, "_updateScheduleStore", store);
            U317Set(window, "_updateClock", (Func<DateTimeOffset>)clock.GetUtcNow);
            await Call(window, "CheckForAppUpdatesAsync");
            Assert(updates.State.Status == AppUpdateStatus.Downloaded, "settle fixture could not verify its package");

            var waitingResponse = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var deliverResponse = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            downloader.BeforeLatest = async token => { waitingResponse.TrySetResult(); await deliverResponse.Task.WaitAsync(token); };
            downloader.LatestStatus = HttpStatusCode.TooManyRequests;
            downloader.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromMinutes(12));
            var check = Call(window, "CheckAppUpdateNowAsync", updates);
            try
            {
                await waitingResponse.Task.WaitAsync(TimeSpan.FromSeconds(5));
                serviceClock.PauseNextUtcRead();
                if (action != "unresolved")
                {
                    deliverResponse.TrySetResult();
                    await classified.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    Assert(updates.RateLimitCheckpoint is null, "fixture did not pause the classified result before publication");
                }
                var prompts = new List<string>();
                window.CloseSaveFailurePrompt = message => { prompts.Add(message); return MessageBoxResult.Cancel; };
                if (action == "apply")
                {
                    var apply = (Task<bool>)Call(window, "ApplyAppUpdateAsync", updates);
                    Assert(!apply.IsCompleted && locator.Starts == 0, "Apply overtook the accepted update check");
                    releaseDecision.Set();
                    Assert(await apply.WaitAsync(TimeSpan.FromSeconds(5)) && locator.Starts == 1
                        && atHandoff?.RateLimit?.RetryNotBeforeUtc == now.AddMinutes(12), "Apply did not settle and persist the accepted limit before handoff");
                }
                else if (action == "close")
                {
                    window.Close();
                    await Dispatcher.Yield(DispatcherPriority.Background);
                    Assert(SaField<bool>(window, "_closeCommitInProgress") && !SaField<bool>(window, "_closed"),
                        "close overtook the accepted update check");
                    releaseDecision.Set();
                    await Until(() => SaField<bool>(window, "_closed"));
                    Assert(prompts.Count == 0, "successful publication still required a close decision");
                }
                else
                {
                    U317Set(window, "_updateCheckExitTimeout", TimeSpan.Zero);
                    window.Close();
                    await Until(() => prompts.Count == 1 && !SaField<bool>(window, "_closeCommitInProgress"));
                    Assert(!SaField<bool>(window, "_closed") && !check.IsCompleted
                        && prompts[0].Contains("дождаться завершения проверки обновлений", StringComparison.Ordinal),
                        "an unresolved network wait bypassed the existing close choice");
                    releaseDecision.Set();
                    deliverResponse.TrySetResult();
                    await check.WaitAsync(TimeSpan.FromSeconds(5));
                    window.Close();
                    await Until(() => SaField<bool>(window, "_closed"));
                }
                await check.WaitAsync(TimeSpan.FromSeconds(5));
                Assert(store.Load(now).RateLimit?.RetryNotBeforeUtc == now.AddMinutes(12),
                    "settled checkpoint did not persist the exact accepted deadline");
                var requests = downloader.Requests.Count;
                using var restarted = new SaFixture("updates0317-exit-settle-resumed-" + action);
                U317AttachUpdateSchedule(restarted.Window, directory, store, clock, locator, downloader);
                await Call(restarted.Window, "CheckForAppUpdatesAsync");
                Assert(downloader.Requests.Count == requests, "restart did not enforce the saved deadline");
            }
            finally
            {
                releaseDecision.Set();
                deliverResponse.TrySetResult();
                await check.WaitAsync(TimeSpan.FromSeconds(5));
                downloader.BeforeLatest = null;
            }
        }
    }

    private sealed class U317RateDecisionClock(DateTimeOffset now, TaskCompletionSource classified, ManualResetEventSlim release) : TimeProvider
    {
        private int _pause;
        internal void PauseNextUtcRead() => Interlocked.Exchange(ref _pause, 1);
        public override DateTimeOffset GetUtcNow()
        {
            if (Interlocked.Exchange(ref _pause, 0) == 1)
            {
                classified.TrySetResult();
                if (!release.Wait(TimeSpan.FromSeconds(8))) throw new TimeoutException("fixture did not release Latest classification");
            }
            return now;
        }
    }

    private static async Task U317ManualUnreadableSchedule()
    {
        var directory = Path.Combine(_directory, "updates0317-manual-unreadable");
        var store = new UpdateScheduleStore(directory);
        var clock = new V040UpdateClock();
        var now = clock.GetUtcNow();
        var saved = new UpdateScheduleCheckpoint(now.AddHours(-1), RateLimit: new(now.AddMinutes(12)));
        store.Save(saved);
        var (downloader, _) = V040PackageDownloader();
        var locator = new Velopack.Locators.TestVelopackLocator("YMusic.Native", "0.3.16", directory);
        using var fixture = new SaFixture("updates0317-manual-unreadable");
        var window = fixture.Window;
        var updates = U317AttachUpdateSchedule(window, directory, store, clock, locator, downloader);
        U317Set(window, "_updateCheckFrequency", UpdateCheckFrequency.Off);
        var footer = Control<Button>(window, "FooterUpdateButton");
        StackPanel panel;
        Button check;
        using (var busy = new FileStream(store.FilePath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            await Call(window, "CheckForAppUpdatesAsync");
            SaClick(footer);
            Assert(Equals(footer.Content, "!") && footer.IsEnabled && footer.ToolTip?.ToString()?.Contains("прочитать расписание", StringComparison.Ordinal) == true,
                "manual footer check did not explain the locally blocked check or allow retry");
            SaClick(Control<Button>(window, "SettingsButton"));
            panel = (StackPanel)Control<ContentControl>(window, "SettingsUpdates").Content;
            check = panel.Children.OfType<Button>().Single();
            // The real settings button cannot be clicked until the panel has subscribed to update refreshes.
            await Until(() => panel.IsLoaded && check.IsLoaded && check.IsVisible);
            var diagnostics = SaField<Diagnostics>(window, "_diagnostics");
            diagnostics.LastErrorType = null;
            SaClick(check);
            Assert(diagnostics.LastErrorType == "UpdateScheduleRead:Unavailable" && check.IsEnabled
                && downloader.Requests.Count == 0 && panel.Children.OfType<TextBlock>()
                .Any(text => text.Text.Contains("прочитать расписание", StringComparison.Ordinal)), "manual settings check did not show the blocked local read");
        }
        SaClick(check);
        await Until(() => updates.RateLimitCheckpoint == saved.RateLimit && !SaField<bool>(window, "_updateCheckInProgress"));
        Assert(downloader.Requests.Count == 0 && check.IsEnabled && store.Load(now) == saved
            && panel.Children.OfType<TextBlock>().Any(text => text.Text.Contains("временно ограничил", StringComparison.Ordinal)),
            "retry did not acknowledge the saved deadline and clear the local read failure");
    }

    private static async Task U317DeferredStartupOfferOnCanceledClose()
    {
        using var fixture = new SaFixture("updates0317-startup-offer-after-cancel-close");
        var window = fixture.Window;
        var backend = new Su016Backend { HoldCheck = true };
        Su016Service(window, "u317-startup-offer-after-cancel-close", backend);
        var preferences = new PreferencesStore(Path.Combine(_directory, "updates0317-close-startup-offer-preferences"));
        preferences.Save(new());
        U317Set(window, "_restore", true);
        U317Set(window, "_preferenceStore", preferences);
        var savePending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        U317Set(window, "_sessionRefreshWrite", savePending.Task);
        var prompts = 0;
        window.CloseSaveFailurePrompt = _ => { prompts++; return MessageBoxResult.Cancel; };
        using var busy = File.OpenRead(preferences.FilePath);
        var startup = Call(window, "CheckForAppUpdatesAsync");
        try
        {
            await backend.CheckEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            window.Close();
            await Dispatcher.Yield(DispatcherPriority.Background);
            Assert(SaField<bool>(window, "_closeCommitInProgress"), "fixture did not start ordinary close");
            backend.CheckRelease.TrySetResult();
            await startup.WaitAsync(TimeSpan.FromSeconds(5));
            Assert(Su016Find<Border>(window, "StartupUpdatePrompt") is null, "offer appeared while close was in progress");
            savePending.TrySetResult();
            await Until(() => prompts == 1 && !SaField<bool>(window, "_closeCommitInProgress"));
            Assert(!SaField<bool>(window, "_closed") && Su016Find<Border>(window, "StartupUpdatePrompt") is { Visibility: Visibility.Visible }
                && backend.Checks == 1 && backend.Downloads == 0 && backend.Applies == 0,
                "canceling close lost the confirmed startup offer or started another operation");
        }
        finally
        {
            savePending.TrySetResult();
            backend.CheckRelease.TrySetResult();
            await startup.WaitAsync(TimeSpan.FromSeconds(5));
            window.CloseSaveFailurePrompt = null;
        }
    }

    private static async Task U317RetryPreferenceCommit()
    {
        foreach (var apply in new[] { false, true })
        {
            using var fixture = new SaFixture("updates0317-preferences-commit-" + apply);
            var window = fixture.Window;
            var backend = new Su016Backend { PendingVersion = "9.1.0" };
            var updates = Su016Service(window, "u317-preferences-commit-" + apply, backend);
            var store = new PreferencesStore(Path.Combine(_directory, "updates0317-preferences-commit-store-" + apply));
            var saved = new PlayerPreferences { Volume = .24, CrossfadeSeconds = 7, UpdateCheckFrequency = UpdateCheckFrequency.Off };
            store.Save(saved);
            var bytes = File.ReadAllBytes(store.FilePath);
            U317Set(window, "_restore", true);
            U317Set(window, "_preferenceStore", store);
            using var held = new FileStream(store.FilePath, FileMode.Open, FileAccess.Read, FileShare.None);
            SaInvoke(window, "LoadPreferences");
            Assert(SaField<bool>(window, "_preferencesLoadFailed"), "initial preferences should be locked");
            SaClick(Control<Button>(window, "SettingsButton"));
            Control<Slider>(window, "CrossfadeSetting").Value = 4;
            SaInvoke(window, "FlushSettingsSave");

            if (apply)
            {
                Assert((await updates.CheckAsync()).Status == AppUpdateStatus.Downloaded, "fake package was not ready");
                Task<bool> Apply() => (Task<bool>)Call(window, "ApplyAppUpdateAsync", updates);
                Assert(!await Apply() && backend.Applies == 0, "apply bypassed the still-locked settings edit");
                held.Dispose();
                Assert(File.ReadAllBytes(store.FilePath).SequenceEqual(bytes), "failed apply changed the prior settings");
                Assert(await Apply() && backend.Applies == 1, "apply retry failed to commit the edit and hand off once");
            }
            else
            {
                var prompts = 0;
                window.CloseSaveFailurePrompt = _ =>
                {
                    held.Dispose();
                    return ++prompts == 1 && File.ReadAllBytes(store.FilePath).SequenceEqual(bytes)
                        ? MessageBoxResult.Yes : MessageBoxResult.Cancel;
                };
                window.Close();
                await Until(() => SaField<bool>(window, "_closed") || !SaField<bool>(window, "_closeCommitInProgress"));
                Assert(SaField<bool>(window, "_closed") && prompts == 1, "close retry did not finish once the original became readable");
            }
            Assert(store.Load() == (saved with { CrossfadeSeconds = 4 }), "exit retry lost the edit or replaced unrelated settings");
        }
    }

    private static async Task U317Restart()
    {
        var directory = Path.Combine(_directory, "updates0317-restart"); var store = new UpdateScheduleStore(directory);
        var now = new DateTimeOffset(2028, 5, 1, 3, 0, 0, TimeSpan.Zero);
        using (var first = new SaFixture("updates0317-restart1"))
        {
            var backend = new Su016Backend { CheckFailures = 1 }; Su016Service(first.Window, "u317-restart1", backend);
            U317Set(first.Window, "_updateCheckFrequency", UpdateCheckFrequency.Daily); U317Set(first.Window, "_updateScheduleStore", store);
            U317Set(first.Window, "_updateClock", (Func<DateTimeOffset>)(() => now));
            await Call(first.Window, "CheckForAppUpdatesAsync");
            Assert(backend.Checks == 1 && store.Load(now).LastAttemptUtc == now && store.Load(now).LastSuccessUtc is null,
                "failed check did not persist only its attempt");
        }
        using var second = new SaFixture("updates0317-restart2"); var retry = new Su016Backend(); Su016Service(second.Window, "u317-restart2", retry);
        U317Set(second.Window, "_updateCheckFrequency", UpdateCheckFrequency.Daily); U317Set(second.Window, "_updateScheduleStore", store);
        U317Set(second.Window, "_updateClock", (Func<DateTimeOffset>)(() => now));
        await Call(second.Window, "CheckForAppUpdatesAsync"); Assert(retry.Checks == 0, "restart retried a failed request too soon");
        now = now.AddDays(1); await Call(second.Window, "RunScheduledUpdateCheckAsync");
        Assert(retry.Checks == 1 && store.Load(now).LastSuccessUtc == now, "due restart did not record the successful check");
    }

    private static async Task U317SettingsDraft()
    {
        using var f = new SaFixture("updates0317-draft"); var w = f.Window;
        var backend = new Su016Backend { Version = null }; Su016Service(w, "u317-draft", backend);
        var store = new PreferencesStore(Path.Combine(_directory, "updates0317-draft-settings"));
        U317Set(w, "_preferenceStore", store); U317Set(w, "_preferencesReady", true);
        U317Set(w, "_updateCheckFrequency", UpdateCheckFrequency.Hourly); SaInvoke(w, "SavePreferences");
        SaClick(Control<Button>(w, "SettingsButton"));
        var frequency = SaField<ComboBox>(w, "_updateFrequencyCombo"); frequency.SelectedValue = UpdateCheckFrequency.Weekly;
        Control<TextBox>(w, "SettingsAccentHex").Text = "#2399BB";
        Control<ComboBox>(w, "SettingsSpeed").SelectedItem = 1.5;
        var before = File.ReadAllBytes(store.FilePath);
        Assert(store.Load().UpdateCheckFrequency == UpdateCheckFrequency.Weekly && f.Player.SpeedRatio == 1.5,
            "selection did not apply immediately");
        Control<TextBox>(w, "SettingsAccentHex").Text = "#23";
        var settingsRoot = Control<Grid>(w, "SettingsPanel"); var currentInput = Control<TextBox>(w, "SettingsAccentHex");
        System.Windows.Input.FocusManager.SetFocusedElement(settingsRoot, currentInput);
        await Call(w, "CheckForAppUpdatesAsync"); SaInvoke(w, "RefreshAppUpdateUi");
        Assert(File.ReadAllBytes(store.FilePath).SequenceEqual(before) && frequency.SelectedValue is UpdateCheckFrequency.Weekly
            && Control<TextBox>(w, "SettingsAccentHex").Text == "#23" && Equals(Control<ComboBox>(w, "SettingsSpeed").SelectedItem, 1.5)
            && ReferenceEquals(System.Windows.Input.FocusManager.GetFocusedElement(settingsRoot), currentInput),
            "automatic status/checkpoint persisted or replaced current input");
        SaInvoke(w, "SettingsClose_Click", Control<Button>(w, "SettingsBackButton"), new RoutedEventArgs());
        SaClick(Control<Button>(w, "SettingsButton"));
        Assert(frequency.SelectedValue is UpdateCheckFrequency.Weekly && store.Load().UpdateCheckFrequency == UpdateCheckFrequency.Weekly,
            "Back rolled back the current frequency");
        frequency.SelectedValue = UpdateCheckFrequency.Off;
        Assert(store.Load().UpdateCheckFrequency == UpdateCheckFrequency.Off && SaField<DispatcherTimer?>(w, "_updateScheduleTimer")?.IsEnabled != true,
            "Off did not persist immediately and stop the scheduler");
        frequency.SelectedValue = UpdateCheckFrequency.Hourly;
        Assert(store.Load().UpdateCheckFrequency == UpdateCheckFrequency.Hourly && SaField<DispatcherTimer?>(w, "_updateScheduleTimer")?.IsEnabled == true,
            "frequency change did not rearm and persist immediately");
        frequency.SelectedValue = UpdateCheckFrequency.Off;
        SaClick(Control<Button>(w, "FooterUpdateButton")); await Until(() => backend.Checks == 2 && !SaField<bool>(w, "_updateCheckInProgress"));
        Assert(store.Load().UpdateCheckFrequency == UpdateCheckFrequency.Off, "manual check silently enabled automatic updates");
    }

    private static async Task U317InstantSettings()
    {
        using var f = new SaFixture("updates0317-instant"); var w = f.Window;
        var directory = Path.Combine(_directory, "updates0317-instant-settings"); var store = new PreferencesStore(directory);
        store.Save(new() { Volume = .37, PlaybackSpeed = 1.25, CrossfadeSeconds = 6, AutoDownloadFavorites = true, StaticAccentColor = "#6EADFA" });
        var initial = File.ReadAllBytes(store.FilePath);
        U317Set(w, "_preferenceStore", store); SaInvoke(w, "LoadPreferences");
        var requestsBefore = f.Handler.Seen.Count;
        SaClick(Control<Button>(w, "SettingsButton"));
        Assert(File.ReadAllBytes(store.FilePath).SequenceEqual(initial) && f.Handler.Seen.Count == requestsBefore
            && SaField<Task?>(w, "_downloadTask") is null && f.Player.SpeedRatio == 1.25 && f.Player.CrossfadeSeconds == 6,
            "initial binding rewrote preferences or started downloads/network");
        var panel = Control<Grid>(w, "SettingsPanel");
        Assert(w.FindName("SettingsApplyButton") is null && w.FindName("SettingsCancelButton") is null && panel.RowDefinitions.Count == 2,
            "removed Save/Cancel footer remains in XAML");
        foreach (var (width, height) in new[] { (1050, 680), (1280, 860), (1600, 1000) })
        {
            w.Width = width; w.Height = height; w.UpdateLayout(); await Task.Delay(350);
            var root = Control<Grid>(w, "LayoutRoot"); var bounds = panel.TransformToAncestor(root).TransformBounds(new Rect(panel.RenderSize));
            var sidebar = Control<Border>(w, "Sidebar"); var expectedCenter = sidebar.ActualWidth + (root.ActualWidth - sidebar.ActualWidth) / 2;
            Assert(Math.Abs(bounds.Left + bounds.Width / 2 - expectedCenter) <= 1 && panel.ActualWidth <= 800.5
                && Control<ScrollViewer>(w, "SettingsScroll").ViewportHeight > 200, "Settings content/header is not centered or former footer still consumes space");
            App.Capture(w, Path.Combine(_directory, $"updates0317-settings-{width}x{height}.png"));
        }
        Control<ComboBox>(w, "SettingsSpeed").SelectedItem = 1.5;
        Assert(f.Player.SpeedRatio == 1.5 && store.Load().PlaybackSpeed == 1.5, "speed did not apply/save immediately");
        var beforeFailure = File.ReadAllBytes(store.FilePath);
        using (var blocked = new FileStream(store.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Control<ComboBox>(w, "SettingsSpeed").SelectedItem = 2d;
            Assert(f.Player.SpeedRatio == 2 && Equals(Control<ComboBox>(w, "SettingsSpeed").SelectedItem, 2d)
                && File.ReadAllBytes(store.FilePath).SequenceEqual(beforeFailure)
                && SaField<Diagnostics>(w, "_diagnostics").LastErrorType?.StartsWith("PreferencesSave:", StringComparison.Ordinal) == true,
                "failed atomic save corrupted disk, reset live input, or hid the failure");
        }
        Control<ComboBox>(w, "SettingsSpeed").SelectedItem = 1.25;
        Assert(store.Load().PlaybackSpeed == 1.25, "retry after failed atomic save did not persist");
        Control<TextBox>(w, "SettingsAccentHex").Text = "#123456";
        var valid = File.ReadAllBytes(store.FilePath);
        Control<TextBox>(w, "SettingsAccentHex").Text = "#12";
        Assert(Control<TextBox>(w, "SettingsAccentHex").Text == "#12" && File.ReadAllBytes(store.FilePath).SequenceEqual(valid)
            && SaField<string>(w, "_staticAccentColor") == "#123456", "incomplete HEX replaced the last valid color or lost input");
        var slider = Control<Slider>(w, "CrossfadeSetting");
        var beforeSlider = File.ReadAllBytes(store.FilePath);
        for (var i = 0; i < 10000; i++) slider.Value = i % 2 == 0 ? 3 : 7;
        Assert(f.Player.CrossfadeSeconds == 7 && File.ReadAllBytes(store.FilePath).SequenceEqual(beforeSlider), "slider did not apply runtime immediately or wrote per change");
        await Task.Delay(350);
        Assert(store.Load().CrossfadeSeconds == 7 && SaField<DispatcherTimer?>(w, "_settingsSaveTimer")?.IsEnabled != true,
            "slider debounce did not persist and stop");
        slider.Value = 8; w.Close();
        Assert(store.Load().CrossfadeSeconds == 8 && SaField<DispatcherTimer?>(w, "_settingsSaveTimer") is null, "close lost a pending slider value or retained its timer");
    }

    private static UpdateSupport U317PortableService(MainWindow window, Func<CancellationToken, Task<string?>> check)
    {
        var directory = Path.Combine(_directory, "updates0317-portable-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        var config = Path.Combine(directory, "feed.json"); File.WriteAllText(config, "{\"githubRepository\":\"https://github.com/senduora/yamuse\"}");
        var service = new UpdateSupport(config, false, _ => throw new InvalidOperationException("portable created installer backend"), check);
        U317Set(window, "_appUpdatesOverride", service); return service;
    }

    private static async Task U317Portable()
    {
        using var f = new SaFixture("updates0317-portable"); var w = f.Window; var checks = 0; var opened = new List<Uri>();
        string? published = "9.1.0";
        var service = U317PortableService(w, token => { token.ThrowIfCancellationRequested(); checks++; return Task.FromResult<string?>(published); });
        U317Set(w, "_updateLinkLauncher", (Action<Uri>)(uri => opened.Add(uri)));
        await Call(w, "CheckForAppUpdatesAsync");
        Assert(checks == 1 && service.State.Status == AppUpdateStatus.Available && Su016Find<Border>(w, "StartupUpdatePrompt") is null
            && opened.Count == 0, "portable startup did not check quietly or forced navigation");
        await service.DownloadAsync(); await Throws<InvalidOperationException>(() => { service.ApplyAndRestart(); return Task.CompletedTask; });
        Assert(service.State.Status == AppUpdateStatus.Available, "portable download/apply changed the service");
        SaClick(Control<Button>(w, "FooterUpdateButton"));
        SaClick(Control<Button>(w, "GithubButton")); SaClick(Control<Button>(w, "ChangelogButton"));
        Assert(opened.Select(uri => uri.AbsoluteUri).SequenceEqual(new[] { "https://github.com/senduora/yamuse/releases", "https://github.com/senduora/yamuse", "https://github.com/senduora/yamuse/releases" }),
            "portable click opened an untrusted page or installer");
        SaClick(Control<Button>(w, "SettingsButton"));
        var settingsPanel = (StackPanel)Control<ContentControl>(w, "SettingsUpdates").Content;
        SaClick(settingsPanel.Children.OfType<Button>().Single());
        Assert(opened.Count == 4 && opened[^1].AbsoluteUri == "https://github.com/senduora/yamuse/releases" && checks == 1,
            "portable settings action used an installer or repeated metadata check");
        var check = settingsPanel.Children.OfType<StackPanel>().Single().Children.OfType<Button>().Single();
        Assert(check.Name == "AppUpdateCheckButton" && check.IsEnabled && ((StackPanel)check.Parent).Visibility == Visibility.Visible,
            "a portable release hid the manual recheck");
        published = null;
        SaClick(check);
        await Until(() => checks == 2 && service.State.Status == AppUpdateStatus.NoUpdate);
        Assert(opened.Count == 4, "the portable recheck opened the release page again");
        using var late = new SaFixture("updates0317-portable-late"); var held = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var canceled = U317PortableService(late.Window, _ => held.Task);
        var pending = Call(late.Window, "CheckForAppUpdatesAsync"); late.Window.Close(); held.TrySetResult("9.1.0"); await pending;
        Assert(canceled.State.Status == AppUpdateStatus.Portable && Su016Find<Border>(late.Window, "StartupUpdatePrompt") is null, "late portable check published stale state");
    }

    private static async Task U317PortableTrust()
    {
        async Task<string?> Read(string json, HttpStatusCode code = HttpStatusCode.OK)
            => await UpdateSupport.CheckPortableReleaseAsync("0.3.17", CancellationToken.None, new FakeHandler((request, _) =>
            {
                Assert(request.RequestUri!.AbsoluteUri == "https://api.github.com/repos/senduora/yamuse/releases/latest"
                    && request.Method == HttpMethod.Get && request.Headers.Authorization is null && request.Headers.UserAgent.Count != 0,
                    "portable request lost fixed HTTPS/no credentials contract");
                return Task.FromResult(Json(json, code));
            }));
        Assert(await Read("{\"tag_name\":\"v0.3.18\",\"draft\":false,\"prerelease\":false}") == "0.3.18", "valid version was not detected");
        Assert(await Read("{\"tag_name\":\"v0.3.17\",\"draft\":false,\"prerelease\":false}") is null, "equal version offered update");
        Assert(await Read("{\"tag_name\":\"v0.3.16\",\"draft\":false,\"prerelease\":false}") is null, "older version offered downgrade");
        await Throws<HttpRequestException>(() => Read("{}", HttpStatusCode.Redirect));
        foreach (var json in new[] { "{", "{}", "{\"tag_name\":\"https://evil.invalid/a.exe\",\"draft\":false,\"prerelease\":false}",
            "{\"tag_name\":\"9.1.0\",\"draft\":true,\"prerelease\":false}", "{\"tag_name\":\"9.1.0\",\"draft\":false,\"prerelease\":true}" })
        {
            try { await Read(json); throw new InvalidOperationException("invalid metadata accepted"); }
            catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidDataException) { }
        }
        await Throws<InvalidDataException>(() => Read(new string(' ', 65537)));
        await Throws<InvalidDataException>(() => UpdateSupport.CheckPortableReleaseAsync("0.3.17", CancellationToken.None,
            new FakeHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new U317UnknownLengthContent() }))));
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        await Throws<OperationCanceledException>(() => UpdateSupport.CheckPortableReleaseAsync("0.3.17", cancel.Token,
            new FakeHandler((_, token) => { token.ThrowIfCancellationRequested(); throw new InvalidOperationException("canceled HTTP executed"); })));
    }

    private static async Task U317Footer()
    {
        using var f = new SaFixture("updates0317-footer"); var w = f.Window; var backend = new Su016Backend { Version = null };
        Su016Service(w, "u317-footer", backend); await Call(w, "CheckForAppUpdatesAsync");
        Assert(Equals(Control<Button>(w, "FooterUpdateButton").Content, "✓"), "up to date status was not truthful");
        foreach (var (width, height) in new[] { (1280, 860), (1050, 680) })
        {
            w.Width = width; w.Height = height; w.UpdateLayout();
            var footer = Control<Border>(w, "MetricsBar");
            foreach (var name in new[] { "VersionText", "FooterUpdateButton", "MemoryText", "CpuText", "GithubButton", "ChangelogButton" })
            {
                var element = (FrameworkElement)w.FindName(name);
                var bounds = element.TransformToAncestor(footer).TransformBounds(new Rect(element.RenderSize));
                Assert(bounds.Left >= 0 && bounds.Top >= -.5 && bounds.Right <= footer.ActualWidth + .5 && bounds.Bottom <= footer.ActualHeight + .5,
                    name + " escaped compact footer");
            }
            App.Capture(w, Path.Combine(_directory, $"updates0317-footer-{width}x{height}.png"));
        }
        backend.CheckFailures = 1; SaClick(Control<Button>(w, "FooterUpdateButton"));
        await Until(() => !SaField<bool>(w, "_updateCheckInProgress"));
        Assert(Equals(Control<Button>(w, "FooterUpdateButton").Content, "!") && Control<Button>(w, "FooterUpdateButton").IsEnabled,
            "failed check claimed success or lost retry");
    }

    private static void U317Set(MainWindow window, string name, object? value)
        => typeof(MainWindow).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(window, value);

    private sealed class U317UnknownLengthContent : HttpContent
    {
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(new byte[65537]).AsTask();
    }
}
