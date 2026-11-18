using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32.SafeHandles;
using YMusic.Native;

internal static partial class Program
{
    private static async Task CheckUninstall()
    {
        await Run("Uninstall: corrupt startup marker is archived only after recovery confirmation", () => Task.Run(() =>
        {
            var paths = UninstallFixture("corrupt-receipt");
            var marker = Path.Combine(paths.Install, "full-uninstall.pending.json");
            const string broken = "{broken";
            Directory.CreateDirectory(paths.Music);
            Directory.CreateDirectory(paths.Data);
            var existingTrack = Path.Combine(paths.Music, "saved.mp3");
            var existingSettings = Path.Combine(paths.Data, "settings.json");
            File.WriteAllText(existingTrack, "download fixture");
            File.WriteAllText(existingSettings, "settings fixture");
            Assert(!UninstallSupport.HasStartupMarker(paths.LocalData), "fixture started with a pending marker");
            File.WriteAllText(marker, broken);
            Assert(UninstallSupport.HasStartupMarker(paths.LocalData), "corrupt marker allowed startup");

            var prompts = 0;
            bool ConfirmRecovery()
            {
                prompts++;
                // The same marker must deny writers across the confirmation dialog.
                RejectUninstall(() => { using var writer = new FileStream(marker, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete); });
                return true;
            }
            using (var locked = new FileStream(marker, FileMode.Open, FileAccess.Read, FileShare.None))
                RejectUninstall(() => UninstallSupport.InspectPendingReceipt(paths, ConfirmRecovery));
            Assert(prompts == 0 && File.ReadAllText(marker) == broken, "sharing failure was treated as corruption");
            Assert(UninstallSupport.InspectPendingReceipt(paths, () => false) == UninstallSupport.PendingReceiptState.RecoveryDeclined
                && File.ReadAllText(marker) == broken, "declining recovery changed the original marker");
            Assert(UninstallSupport.InspectPendingReceipt(paths, ConfirmRecovery) == UninstallSupport.PendingReceiptState.Recovered
                && prompts == 1 && !UninstallSupport.HasStartupMarker(paths.LocalData), "explicit recovery did not unblock startup");
            var archived = Directory.EnumerateFiles(paths.Install, "full-uninstall.archived-*.json").Single();
            Assert(File.ReadAllText(archived) == broken && File.ReadAllText(existingTrack) == "download fixture"
                && File.ReadAllText(existingSettings) == "settings fixture" && File.Exists(paths.Updater),
                "recovery did not preserve the exact marker, profile, music, and install files");
        }));
        await Run("Uninstall: missing updater permits only confirmed marker recovery", () => Task.Run(() =>
        {
            var paths = UninstallFixture("absent-updater", withUpdater: false);
            var marker = Path.Combine(paths.Install, "full-uninstall.pending.json");
            var nonce = UninstallSupport.CreateReceipt(paths);
            UninstallSupport.ValidateReceipt(paths, nonce);
            RejectUninstall(() => UninstallSupport.ValidateInstallation(paths, "YMusic.Native", false, true,
                paths.Install, Path.GetDirectoryName(paths.Executable)!, paths.Updater, paths.Executable));
            var original = File.ReadAllBytes(marker);
            Assert(UninstallSupport.RecoverWithoutInstallation(paths.LocalData, () => false) == UninstallSupport.PendingReceiptState.RecoveryDeclined
                && original.SequenceEqual(File.ReadAllBytes(marker)), "recovering a broken installation ignored a declined reset");
            Assert(UninstallSupport.RecoverWithoutInstallation(paths.LocalData, () => true) == UninstallSupport.PendingReceiptState.Recovered
                && !UninstallSupport.HasStartupMarker(paths.LocalData)
                && original.SequenceEqual(File.ReadAllBytes(Directory.EnumerateFiles(paths.Install, "full-uninstall.archived-*.json").Single()))
                && File.Exists(paths.Executable) && !File.Exists(paths.Updater), "recovery touched the install or failed to archive its pending marker");

            nonce = UninstallSupport.CreateReceipt(paths);
            UninstallSupport.ScheduleNativeHandoffAfterCleanup(paths, nonce, Environment.ProcessId, _ => Process.GetCurrentProcess());
            Assert(UninstallSupport.RecoverWithoutInstallation(paths.LocalData, () => throw new InvalidOperationException("consumed marker offered for reset"))
                == UninstallSupport.PendingReceiptState.Consumed && UninstallSupport.HasStartupMarker(paths.LocalData),
                "missing updater cancelled a completed cleanup");
        }));
        await Run("Uninstall: startup reset rejects a replacement marker after confirmation", () => Task.Run(() =>
        {
            var paths = UninstallFixture("startup-reset", withUpdater: false);
            var marker = Path.Combine(paths.Install, "full-uninstall.pending.json");
            var nonce = UninstallSupport.CreateReceipt(paths);
            var bytes = File.ReadAllBytes(marker);
            var candidate = UninstallSupport.InspectStartupMarkerForReset(paths.LocalData)!;
            var replacement = Path.Combine(paths.Install, "replacement-marker.json");
            File.WriteAllBytes(replacement, bytes);
            File.SetLastWriteTimeUtc(replacement, candidate.LastWriteUtc);
            File.Move(replacement, marker, overwrite: true);
            Assert(!UninstallSupport.TryResetStartupMarker(paths.LocalData, candidate) && UninstallSupport.HasStartupMarker(paths.LocalData),
                "a different file with identical contents replaced the confirmed marker");
            UninstallSupport.ScheduleNativeHandoffAfterCleanup(paths, nonce, Environment.ProcessId, _ => Process.GetCurrentProcess());
            var consumed = UninstallSupport.InspectStartupMarkerForReset(paths.LocalData)!;
            Assert(consumed.Consumed && !UninstallSupport.TryResetStartupMarker(paths.LocalData, consumed), "startup reset bypassed a consumed marker");

            File.WriteAllText(marker, "{broken");
            candidate = UninstallSupport.InspectStartupMarkerForReset(paths.LocalData)!;
            Assert(UninstallSupport.TryResetStartupMarker(paths.LocalData, candidate) && !UninstallSupport.HasStartupMarker(paths.LocalData),
                "explicit startup reset did not unblock the portable/normal launch without the Windows uninstall entry");
        }));
        await Run("Uninstall: relocated folders permit pending recovery and preserve consumed markers", () => Task.Run(() =>
        {
            var paths = UninstallFixture("relocated-receipt");
            var relocated = UninstallPaths.FromKnownFolders(paths.LocalData, paths.Documents + "-relocated");
            var marker = Path.Combine(paths.Install, "full-uninstall.pending.json");
            var originalNonce = UninstallSupport.CreateReceipt(paths);
            RejectUninstall(() => UninstallSupport.ValidateReceipt(relocated, originalNonce));
            Assert(UninstallSupport.InspectPendingReceipt(relocated, () => true) == UninstallSupport.PendingReceiptState.Recovered,
                "unconsumed marker for old folders could not be reset");

            var nonce = UninstallSupport.CreateReceipt(paths);
            UninstallSupport.ScheduleNativeHandoffAfterCleanup(paths, nonce, Environment.ProcessId, _ => Process.GetCurrentProcess());
            var consumed = File.ReadAllBytes(marker);
            Assert(UninstallSupport.InspectPendingReceipt(relocated, () => throw new InvalidOperationException("consumed marker offered for recovery"))
                == UninstallSupport.PendingReceiptState.Consumed, "consumed marker no longer routed to native retry");
            Assert(UninstallSupport.IsReceiptConsumed(relocated) && !UninstallSupport.CancelPendingReceipt(relocated)
                && File.ReadAllBytes(marker).SequenceEqual(consumed), "consumed marker was changed or startup protection removed");
        }));
        await Run("Uninstall: settings and confirmation defer to active cache and offline maintenance", () =>
        {
            using var fixture = new DownloadCenterFixture("uninstall-maintenance", 1);
            using var fakeListener = new ManualResetEvent(false);
            var window = fixture.Window;
            R4Set(window, "_uninstallListener", fakeListener);
            var button = Control<Button>(window, "SettingsUninstall");
            foreach (var flag in new[] { "_cacheMaintenance", "_offlineMaintenance", "_clearOfflineConfirmationPending" })
            {
                R4Set(window, flag, true);
                SaInvoke(window, "Settings_Click", button, new RoutedEventArgs());
                Assert(!button.IsEnabled && !window.ConfirmBeforeFullUninstall(() => throw new InvalidOperationException("confirmation opened during maintenance")),
                    "settings or confirmation ignored an active maintenance operation");
                SaInvoke(window, "SetSettingsVisible", false);
                R4Set(window, flag, false);
                Assert(!window.ConfirmBeforeFullUninstall(() =>
                {
                    R4Set(window, flag, true);
                    return true;
                }) && SaField<bool>(window, flag), "maintenance started during confirmation and was ignored or reset");
                R4Set(window, flag, false);
            }
            SaInvoke(window, "Settings_Click", button, new RoutedEventArgs());
            Assert(button.IsEnabled && window.ConfirmBeforeFullUninstall(() => true), "idle confirmation remained disabled");
            return Task.CompletedTask;
        });
        await Run("Uninstall: fixed app data and indexed audio removed; parents and unrelated siblings retained; retry idempotent", () => Task.Run(() =>
        {
            var paths = UninstallFixture("full");
            Directory.CreateDirectory(Path.Combine(paths.Data, "nested"));
            File.WriteAllText(Path.Combine(paths.Data, "nested", "session.dpapi"), "fake fixture only");
            WriteUninstallAudio(paths, "saved.mp3", "fake downloaded music");
            File.WriteAllText(Path.Combine(paths.Music, "playlists.index.json"), "{\"schemaVersion\":1,\"playlists\":[]}");
            var unrelated = Path.Combine(paths.Documents, "other-music.mp3");
            File.WriteAllText(unrelated, "keep");
            var sibling = Path.Combine(paths.LocalData, "OtherApp");
            Directory.CreateDirectory(sibling); File.WriteAllText(Path.Combine(sibling, "keep.txt"), "keep");
            var result = UninstallCleanup.Execute(paths);
            Assert(result.Complete && result.RemovedFiles == 4 && result.PreservedForeignFiles == 0, JsonSerializer.Serialize(result));
            Assert(!Directory.Exists(paths.Data) && !Directory.Exists(paths.Music), "owned directories remain");
            Assert(Directory.Exists(paths.LocalData) && Directory.Exists(paths.Documents) && File.ReadAllText(unrelated) == "keep"
                && File.ReadAllText(Path.Combine(sibling, "keep.txt")) == "keep" && File.Exists(paths.Updater), "parents/siblings/install changed");
            var retry = UninstallCleanup.Execute(paths);
            Assert(retry.Complete && retry.RemovedFiles == 0 && retry.PreservedForeignFiles == 0, "retry is not idempotent");
        }));
        await Run("Uninstall: empty, relative, drive, device, intersecting and wrong install paths rejected", () => Task.Run(() =>
        {
            var paths = UninstallFixture("paths");
            foreach (var bad in new[] { "", " ", ".", "..", "C:\\", @"\\server\share", @"\\?\C:\data", "C:\\x:ads" })
                RejectUninstall(() => UninstallPaths.FromKnownFolders(bad, paths.Documents));
            RejectUninstall(() => UninstallPaths.FromKnownFolders(paths.Documents, Path.Combine(paths.Documents, "nested")));
            UninstallSupport.ValidateInstallation(paths, "YMusic.Native", false, true, paths.Install, Path.GetDirectoryName(paths.Executable)!, paths.Updater, paths.Executable);
            RejectUninstall(() => UninstallSupport.ValidateInstallation(paths, "OtherApp", false, true, paths.Install, Path.GetDirectoryName(paths.Executable)!, paths.Updater, paths.Executable));
            RejectUninstall(() => UninstallSupport.ValidateInstallation(paths, "YMusic.Native", true, true, paths.Install, Path.GetDirectoryName(paths.Executable)!, paths.Updater, paths.Executable));
            RejectUninstall(() => UninstallSupport.ValidateInstallation(paths, "YMusic.Native", false, true, paths.LocalData, Path.GetDirectoryName(paths.Executable)!, paths.Updater, paths.Executable));
            RejectUninstall(() => UninstallSupport.ValidateInstallation(paths, "YMusic.Native", false, true, paths.Install, paths.Documents, paths.Updater, paths.Executable));
            Directory.CreateDirectory(Path.Combine(paths.Install, "app-0.3.10"));
            RejectUninstall(() => UninstallSupport.ValidateInstallation(paths, "YMusic.Native", false, true, paths.Install, Path.GetDirectoryName(paths.Executable)!, paths.Updater, paths.Executable));
            Assert(File.Exists(paths.Updater), "validation wrote to installation");
        }));
        await Run("Uninstall: foreign and modified audio retained with explicit result and folder", () => Task.Run(() =>
        {
            var paths = UninstallFixture("foreign");
            WriteUninstallAudio(paths, "saved.mp3", "original music");
            File.WriteAllText(Path.Combine(paths.Music, "saved.mp3"), "modified music");
            File.WriteAllText(Path.Combine(paths.Music, "manual.flac"), "foreign music");
            Directory.CreateDirectory(Path.Combine(paths.Music, "manual-album"));
            var result = UninstallCleanup.Execute(paths);
            Assert(result.Complete && result.PreservedForeignFiles == 3 && Directory.Exists(paths.Music), JsonSerializer.Serialize(result));
            Assert(File.ReadAllText(Path.Combine(paths.Music, "saved.mp3")) == "modified music"
                && File.ReadAllText(Path.Combine(paths.Music, "manual.flac")) == "foreign music", "foreign or modified music changed");
        }));
        await Run("Uninstall: locked audio and data reported; index kept for retry; unlocked retry removes known files", () => Task.Run(() =>
        {
            var paths = UninstallFixture("locked");
            Directory.CreateDirectory(paths.Data);
            var lockedData = Path.Combine(paths.Data, "settings.json"); File.WriteAllText(lockedData, "fixture");
            WriteUninstallAudio(paths, "free.mp3", "fixture free audio");
            WriteUninstallAudio(paths, "saved.mp3", "fixture audio");
            using (var music = new FileStream(Path.Combine(paths.Music, "saved.mp3"), FileMode.Open, FileAccess.Read, FileShare.None))
            using (var data = new FileStream(lockedData, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                var result = UninstallCleanup.Execute(paths);
                Assert(!result.Complete && result.Issues.Any(issue => issue.Area == "Музыка") && result.Issues.Any(issue => issue.Area == "Данные"), JsonSerializer.Serialize(result));
                Assert(File.Exists(Path.Combine(paths.Music, "library.index.json")) && File.Exists(lockedData), "locked retry lost ownership index or file");
                Assert(!File.Exists(Path.Combine(paths.Music, "free.mp3")), "independent known audio was not cleaned before partial failure");
            }
            var retry = UninstallCleanup.Execute(paths);
            Assert(retry.Complete && !Directory.Exists(paths.Data) && !Directory.Exists(paths.Music), JsonSerializer.Serialize(retry));
        }));
        await Run("Uninstall: corrupt and escaping music indexes preserve audio and block completion", () => Task.Run(() =>
        {
            var paths = UninstallFixture("corrupt");
            WriteUninstallAudio(paths, "saved.mp3", "fixture audio");
            var index = Path.Combine(paths.Music, "library.index.json");
            foreach (var bad in new[] { "broken", "{}", "[{\"FileName\":\"..\\\\outside.mp3\",\"Bytes\":1,\"Sha256\":\"" + new string('a', 64) + "\"}]" })
            {
                File.WriteAllText(index, bad);
                var result = UninstallCleanup.Execute(paths);
                Assert(!result.Complete && File.Exists(Path.Combine(paths.Music, "saved.mp3")) && File.ReadAllText(index) == bad, "corrupt index erased audio/ownership");
            }
        }));
        await Run("Uninstall: junction in data tree and music root preserves outside files; internal current junction allowed", () => Task.Run(() =>
        {
            var paths = UninstallFixture("junction");
            var outside = Path.Combine(Path.GetDirectoryName(paths.LocalData)!, "external");
            Directory.CreateDirectory(outside); File.WriteAllText(Path.Combine(outside, "keep.txt"), "external");
            Directory.CreateDirectory(paths.Data);
            var dataLink = Path.Combine(paths.Data, "cache-link");
            CreateUninstallJunction(dataLink, outside);
            CreateUninstallJunction(paths.Music, outside);
            var result = UninstallCleanup.Execute(paths);
            Assert(!result.Complete && File.ReadAllText(Path.Combine(outside, "keep.txt")) == "external", JsonSerializer.Serialize(result));
            Assert(Directory.Exists(dataLink) && Directory.Exists(paths.Music), "junction was followed or silently claimed complete");
            var current = Path.GetDirectoryName(paths.Executable)!;
            var version = Path.Combine(paths.Install, "version-0.3.10");
            Directory.Move(current, version); CreateUninstallJunction(current, version);
            UninstallSupport.ValidateInstallation(paths, "YMusic.Native", false, true, paths.Install, current, paths.Updater, paths.Executable);
            Directory.Delete(current, recursive: false); CreateUninstallJunction(current, outside);
            RejectUninstall(() => UninstallSupport.ValidateInstallation(paths, "YMusic.Native", false, true, paths.Install, current, paths.Updater, paths.Executable));
            Assert(File.ReadAllText(Path.Combine(outside, "keep.txt")) == "external", "external install junction data changed");
            var intermediate = UninstallFixture("junction-parent");
            var externalMusic = Path.Combine(outside, "Music");
            Directory.CreateDirectory(externalMusic); File.WriteAllText(Path.Combine(externalMusic, "keep.mp3"), "external music");
            CreateUninstallJunction(Path.GetDirectoryName(intermediate.Music)!, outside);
            var parentResult = UninstallCleanup.Execute(intermediate);
            Assert(!parentResult.Complete && File.ReadAllText(Path.Combine(externalMusic, "keep.mp3")) == "external music", "intermediate music junction was followed");
        }));
        await Run("Uninstall: guarded ancestor cannot be renamed to swap a junction during deletion", () => Task.Run(() =>
        {
            var paths = UninstallFixture("race"); Directory.CreateDirectory(paths.Data);
            using (var guard = UninstallPathGuard.HoldAncestors(Path.Combine(paths.Data, "child")))
                RejectUninstall(() => Directory.Move(paths.Data, paths.Data + "-moved"));
            Assert(Directory.Exists(paths.Data), "directory guard allowed replacement");
            using var missing = UninstallPathGuard.HoldAncestors(paths.Music);
            Assert(!missing.Complete, "missing ancestor incorrectly reported protected");
            RejectUninstall(() => UninstallPathGuard.HoldExistingAncestors(paths.Music));
        }));
        await Run("Uninstall: receipt bound to paths and origin; consumed cancellation/recreation/replay rejected; native retry preserves marker", () => Task.Run(() =>
        {
            var paths = UninstallFixture("receipt");
            var nonce = UninstallSupport.CreateReceipt(paths);
            UninstallSupport.ValidateReceipt(paths, nonce);
            Assert(!UninstallSupport.OriginProcessExited(paths, nonce), "live origin process incorrectly treated as exited");
            RejectUninstall(() => UninstallSupport.ValidateReceipt(paths, ""));
            RejectUninstall(() => UninstallSupport.ValidateReceipt(paths, "../file"));
            RejectUninstall(() => UninstallSupport.ValidateReceipt(paths, new string('a', 64)));
            var receipt = Path.Combine(paths.Install, "full-uninstall.pending.json");
            var original = File.ReadAllText(receipt);
            File.WriteAllText(receipt, original.Replace(paths.Data.Replace("\\", "\\\\"), paths.Documents.Replace("\\", "\\\\"), StringComparison.Ordinal));
            RejectUninstall(() => UninstallSupport.ValidateReceipt(paths, nonce));
            File.WriteAllText(receipt, original);
            UninstallSupport.ScheduleNativeHandoffAfterCleanup(paths, nonce, Environment.ProcessId, _ => Process.GetCurrentProcess());
            RejectUninstall(() => UninstallSupport.ValidateReceipt(paths, nonce));
            Assert(UninstallSupport.HasPendingReceipt(paths), "consumed pending marker lost its startup guard");
            var consumed = File.ReadAllBytes(receipt);
            Assert(!UninstallSupport.CancelPendingReceipt(paths), "cancel removed consumed marker");
            RejectUninstall(() => UninstallSupport.CreateReceipt(paths));
            var retryLaunches = 0;
            UninstallSupport.ScheduleConsumedRetry(paths, Environment.ProcessId, specification =>
            {
                retryLaunches++;
                Assert(specification.ArgumentList.SequenceEqual(new[] { "start", "--waitPid", Environment.ProcessId.ToString(), paths.Updater, "--", "--uninstall" }), "consumed retry changed native target");
                return Process.GetCurrentProcess();
            });
            Assert(retryLaunches == 1 && consumed.SequenceEqual(File.ReadAllBytes(receipt)), "retry or cancellation changed consumed marker");
            File.WriteAllText(receipt, original);
            UninstallSupport.DeleteReceipt(paths, nonce);
            UninstallSupport.DeleteReceipt(paths, nonce);
            RejectUninstall(() => UninstallSupport.ValidateReceipt(paths, nonce));
            Assert(!File.Exists(receipt), "receipt remains");
        }));
        await Run("Uninstall: receipt expiry rejects admission but permits cancellation and handoff after admitted cleanup", () => Task.Run(() =>
        {
            var paths = UninstallFixture("aged-receipt");
            var receipt = Path.Combine(paths.Install, "full-uninstall.pending.json");
            void AgeReceipt()
            {
                var marker = JsonNode.Parse(File.ReadAllText(receipt))!.AsObject();
                marker["CreatedUtc"] = JsonValue.Create(DateTimeOffset.UtcNow.AddHours(-1));
                File.WriteAllText(receipt, marker.ToJsonString());
            }

            var cancelled = UninstallSupport.CreateReceipt(paths);
            UninstallSupport.ValidateReceipt(paths, cancelled);
            AgeReceipt();
            RejectUninstall(() => UninstallSupport.ValidateReceipt(paths, cancelled));
            RejectUninstall(() => UninstallSupport.DeleteReceipt(paths, new string('a', 64)));
            Assert(UninstallSupport.HasPendingReceipt(paths), "other nonce removed the expired marker");
            UninstallSupport.DeleteReceipt(paths, cancelled);
            Assert(!UninstallSupport.HasPendingReceipt(paths), "cancelling an expired request left startup blocked");

            var admitted = UninstallSupport.CreateReceipt(paths);
            UninstallSupport.ValidateReceipt(paths, admitted);
            AgeReceipt();
            var launches = 0;
            var guardedAtLaunch = false;
            RejectUninstall(() => UninstallSupport.ScheduleNativeHandoffAfterCleanup(paths, admitted, Environment.ProcessId, _ =>
            {
                launches++;
                guardedAtLaunch = UninstallSupport.IsReceiptConsumed(paths);
                throw new IOException("injected handoff failure");
            }));
            Assert(launches == 1 && guardedAtLaunch && UninstallSupport.HasPendingReceipt(paths) && !UninstallSupport.IsReceiptConsumed(paths),
                "handoff was blocked by expiry or failure did not restore the pending marker");
            RejectUninstall(() => UninstallSupport.ValidateReceipt(paths, admitted));
            UninstallSupport.ScheduleNativeHandoffAfterCleanup(paths, admitted, Environment.ProcessId, _ => Process.GetCurrentProcess());
            Assert(UninstallSupport.IsReceiptConsumed(paths) && !UninstallSupport.CancelPendingReceipt(paths),
                "an admitted cleanup lost its startup guard during handoff");
        }));
        await Run("Uninstall: failed waiter launch leaves downloads resumable after closing and reopening", async () =>
        {
            const string name = "uninstall-launch-failure";
            using (var fixture = new DownloadCenterFixture(name, 1) { HoldFirstAttempts = true })
            {
                DrEnable(fixture);
                var download = fixture.Start([DcTrack("2")]);
                await Until(() => fixture.Active == 1);
                var error = await Throws<IOException>(() => fixture.Window.StartFullUninstallHandoffAsync(() =>
                {
                    Assert(fixture.Active == 0, "waiter launch ran before download shutdown");
                    throw new IOException("injected waiter failure");
                }));
                Assert(error.Message == "injected waiter failure", "failed before the injected waiter launch");
                await download;
                var checkpoint = new DownloadQueueStore(fixture.DirectoryPath).Load("fixture");
                Assert(fixture.Queue.CanResume && checkpoint?.Entries.Single().State == DownloadEntryState.Paused,
                    "failed launch invalidated the in-memory queue or its checkpoint");
            }

            using var reopened = new DownloadCenterFixture(name, 1);
            DrEnable(reopened);
            await Call(reopened.Window, "RestoreDownloadQueueAsync", "fixture");
            Assert(reopened.Queue.CanResume && reopened.Requests.Count == 0, "reopen lost the paused queue or started it automatically");
            await Call(reopened.Window, "ResumeDownloadQueue");
            Assert(reopened.Queue.Entries.Single().State == DownloadEntryState.Completed, "explicit resume did not complete the held download");
        });
        await Run("Uninstall: handoff drains admitted writes and saves playback before a failing waiter", async () =>
        {
            using var fixture = new PlaybackOptionsFixture("uninstall-pending-writes");
            await fixture.Play();
            await fixture.Rendered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Call(fixture.Window, "TogglePlaybackAsync");
            fixture.Player.Seek(TimeSpan.FromSeconds(7));
            await Until(() => fixture.Player.Position.TotalSeconds >= 6.9 && !fixture.Player.IsPlaying);

            var store = new PlaybackStateStore(Path.Combine(_directory, "uninstall-pending-writes-state"));
            const string account = "uninstall-fixture";
            R4Set(fixture.Window, "_restore", true);
            R4Set(fixture.Window, "_account", new Account(account, "Fixture", false));
            R4Set(fixture.Window, "_playbackStateAccountUid", account);
            R4Set(fixture.Window, "_playbackStateStore", store);
            var session = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var favorites = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            R4Set(fixture.Window, "_sessionRefreshWrite", session.Task);
            R4Set(fixture.Window, "_favoritesCacheSaveTask", favorites.Task);
            R4Set(fixture.Window, "_fullUninstallCommitting", true);
            var launchCalled = false;
            try
            {
                var failed = Throws<IOException>(() => fixture.Window.StartFullUninstallHandoffAsync(() =>
                {
                    launchCalled = true;
                    Assert(store.Load(account) is { CurrentIndex: 0, PositionSeconds: >= 6.9 }, "waiter did not have the paused playback checkpoint");
                    throw new IOException("injected waiter failure after drain");
                }));
                Assert(!launchCalled && !failed.IsCompleted, "waiter started with writes outstanding");
                fixture.Window.Close();
                Assert(!SaField<bool>(fixture.Window, "_closed"), "a competing close interrupted the handoff");
                session.TrySetResult();
                await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);
                Assert(!launchCalled && !failed.IsCompleted, "handoff waited for session but skipped favorites");
                favorites.TrySetResult();
                var error = await failed.WaitAsync(TimeSpan.FromSeconds(5));
                Assert(error.Message == "injected waiter failure after drain" && fixture.Player.HasSource && !fixture.Player.IsPlaying
                    && fixture.Player.Position.TotalSeconds >= 6.9, "failed waiter discarded or rewound the playback source");
            }
            finally
            {
                session.TrySetResult();
                favorites.TrySetResult();
                R4Set(fixture.Window, "_fullUninstallCommitting", false);
            }
        });
        await Run("Uninstall: official waitPid chain uses exact targets and argument list; private fakes never launch updater", () => Task.Run(() =>
        {
            var paths = UninstallFixture("orchestration");
            var nonce = new string('A', 64);
            var first = UninstallSupport.BuildWaitStart(paths, 1234, paths.Executable, [UninstallSupport.CompleteArgument, nonce]);
            var final = UninstallSupport.BuildWaitStart(paths, 5678, paths.Updater, ["--uninstall"]);
            Assert(first.FileName == paths.Updater && !first.UseShellExecute
                && first.ArgumentList.SequenceEqual(new[] { "start", "--waitPid", "1234", paths.Executable, "--", UninstallSupport.CompleteArgument, nonce }), "first native command differs");
            Assert(final.ArgumentList.SequenceEqual(new[] { "start", "--waitPid", "5678", paths.Updater, "--", "--uninstall" }), "final native command differs");
            RejectUninstall(() => UninstallSupport.BuildWaitStart(paths, 0, paths.Executable, []));
            RejectUninstall(() => UninstallSupport.BuildWaitStart(paths, 1234, Path.Combine(paths.Documents, "evil.exe"), []));
            var launched = 0;
            UninstallSupport.StartAfterExit(paths, Environment.ProcessId, paths.Executable, [UninstallSupport.CompleteArgument, nonce], specification =>
            { launched++; Assert(specification.FileName == paths.Updater, "fake target"); return Process.GetCurrentProcess(); });
            Assert(launched == 1, "launch was repeated");
            RejectUninstall(() => UninstallSupport.StartAfterExit(paths, Environment.ProcessId, paths.Updater, ["--uninstall"], _ => null));
            RejectUninstall(() => UninstallSupport.StartAfterExit(paths, Environment.ProcessId, paths.Updater, ["--uninstall"], _ => throw new IOException("injected launch failure")));
            Assert(File.Exists(paths.Updater) && Directory.Exists(paths.Documents), "orchestration deleted actual fixture without cleanup");
            File.WriteAllText(Path.Combine(_directory, "uninstall-orchestration-fakes.json"), JsonSerializer.Serialize(new { nativeCommandExecuted = false, first = first.ArgumentList, final = final.ArgumentList, privateLaunchFakes = 3 }));
        }));
    }

    private static UninstallPaths UninstallFixture(string name, bool withUpdater = true)
    {
        var root = Path.GetFullPath(Path.Combine(_directory, "uninstall", name + "-" + Guid.NewGuid().ToString("N")));
        Assert(root.StartsWith(Path.GetFullPath(_directory) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase), "fixture escaped results directory");
        var paths = UninstallPaths.FromKnownFolders(Path.Combine(root, "LocalAppData"), Path.Combine(root, "Documents"));
        Directory.CreateDirectory(paths.LocalData); Directory.CreateDirectory(paths.Documents);
        Directory.CreateDirectory(Path.GetDirectoryName(paths.Executable)!);
        File.WriteAllText(paths.Executable, "fake executable: never run");
        if (withUpdater) File.WriteAllText(paths.Updater, "fake updater: never run");
        return paths;
    }
    private static void WriteUninstallAudio(UninstallPaths paths, string name, string content)
    {
        Directory.CreateDirectory(paths.Music);
        var bytes = Encoding.UTF8.GetBytes(content); File.WriteAllBytes(Path.Combine(paths.Music, name), bytes);
        var index = Path.Combine(paths.Music, "library.index.json");
        var rows = File.Exists(index) ? JsonSerializer.Deserialize<List<UninstallFixtureAudio>>(File.ReadAllText(index))! : [];
        rows.RemoveAll(row => row.FileName == name);
        rows.Add(new(name, bytes.Length, Convert.ToHexString(SHA256.HashData(bytes))));
        File.WriteAllText(index, JsonSerializer.Serialize(rows));
    }
    private sealed record UninstallFixtureAudio(string FileName, long Bytes, string Sha256);
    private static void RejectUninstall(Action action)
    {
        try { action(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or JsonException or InvalidOperationException) { return; }
        throw new InvalidOperationException("unsafe or failed uninstall operation was accepted");
    }

    // Junctions are supported for an ordinary user without Developer Mode or
    // symlink privileges. Every target here is an isolated fixture directory.
    private static void CreateUninstallJunction(string path, string target)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!); Directory.CreateDirectory(path);
        var substitute = Encoding.Unicode.GetBytes(@"\??\" + Path.GetFullPath(target));
        var print = Encoding.Unicode.GetBytes(Path.GetFullPath(target));
        var buffer = new byte[16 + substitute.Length + 2 + print.Length + 2];
        BitConverter.GetBytes(0xA0000003u).CopyTo(buffer, 0);
        BitConverter.GetBytes((ushort)(buffer.Length - 8)).CopyTo(buffer, 4);
        BitConverter.GetBytes((ushort)substitute.Length).CopyTo(buffer, 10);
        BitConverter.GetBytes((ushort)(substitute.Length + 2)).CopyTo(buffer, 12);
        BitConverter.GetBytes((ushort)print.Length).CopyTo(buffer, 14);
        substitute.CopyTo(buffer, 16); print.CopyTo(buffer, 18 + substitute.Length);
        using var handle = UninstallCreateFile(path, 0x40000000, 0, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
        if (handle.IsInvalid || !UninstallDeviceIoControl(handle, 0x900A4, buffer, buffer.Length, IntPtr.Zero, 0, out _, IntPtr.Zero))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
    }
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle UninstallCreateFile(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", EntryPoint = "DeviceIoControl", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UninstallDeviceIoControl(SafeFileHandle handle, uint code, byte[] input, int length, IntPtr output, int outputLength, out int returned, IntPtr overlapped);
}
