using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Win32;
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
            RejectUninstall(() => UninstallSupport.ValidateInstallation(paths.LocalData, paths.Executable));
            var original = File.ReadAllBytes(marker);
            Assert(UninstallSupport.RecoverWithoutInstallation(paths.LocalData, () => false) == UninstallSupport.PendingReceiptState.RecoveryDeclined
                && original.SequenceEqual(File.ReadAllBytes(marker)), "recovering a broken installation ignored a declined reset");
            Assert(UninstallSupport.RecoverWithoutInstallation(paths.LocalData, () => true) == UninstallSupport.PendingReceiptState.Recovered
                && !UninstallSupport.HasStartupMarker(paths.LocalData)
                && original.SequenceEqual(File.ReadAllBytes(Directory.EnumerateFiles(paths.Install, "full-uninstall.archived-*.json").Single()))
                && File.Exists(paths.Executable) && !File.Exists(paths.Updater), "recovery touched the install or failed to archive its pending marker");

            nonce = UninstallSupport.CreateReceipt(paths);
            RejectUninstall(() => UninstallSupport.ScheduleNativeHandoffAfterCleanup(paths, nonce, Environment.ProcessId, _ => Process.GetCurrentProcess()));
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
            RejectUninstall(() => UninstallSupport.ScheduleNativeHandoffAfterCleanup(paths, nonce, Environment.ProcessId, _ => Process.GetCurrentProcess()));
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
        await Run("Uninstall: consumed retry needs only validated local installation even without Documents", () => Task.Run(() =>
        {
            var paths = UninstallFixture("consumed-without-documents");
            var blockedCache = Path.Combine(paths.Install, "packages");
            File.WriteAllText(blockedCache, "fixture blocks creating the update cache directory");
            var pid = Environment.ProcessId;
            var launches = 0;
            Process FinishNative(ProcessStartInfo command)
            {
                launches++;
                Assert(command.FileName == paths.Updater && command.WorkingDirectory == paths.Install && !command.UseShellExecute
                    && command.ArgumentList.SequenceEqual(new[] { "start", "--waitPid", pid.ToString(), paths.Updater, "--", "--uninstall" }),
                    "consumed retry selected an unexpected operation");
                return Process.GetCurrentProcess();
            }

            RejectUninstall(() => UninstallPaths.FromKnownFolders(paths.LocalData, ""));
            RejectUninstall(() => UninstallPaths.FromKnownFolders(paths.LocalData, @"\\fixture.invalid\Documents"));
            var nonce = UninstallSupport.CreateReceipt(paths);
            var confirmations = 0;
            bool Confirm() { confirmations++; return true; }
            RejectUninstall(() => UninstallSupport.ScheduleConsumedRetry(paths.LocalData, paths.Executable, pid, Confirm, FinishNative));
            Assert(confirmations == 0 && launches == 0, "pending receipt authorized the native operation");
            UninstallSupport.ScheduleNativeHandoffAfterCleanup(paths, nonce, pid, _ => Process.GetCurrentProcess());
            Directory.Move(paths.Documents, paths.Documents + "-temporarily-unavailable");
            var marker = Path.Combine(paths.Install, "full-uninstall.pending.json");
            var consumed = File.ReadAllBytes(marker);
            UninstallSupport.ScheduleConsumedRetry(paths.LocalData, paths.Executable, pid, () => false, FinishNative);
            Assert(launches == 0 && File.ReadAllBytes(marker).SequenceEqual(consumed), "declining consumed retry changed its request");

            var heldUpdater = paths.Updater + ".held";
            try
            {
                RejectUninstall(() => UninstallSupport.ScheduleConsumedRetry(paths.LocalData, paths.Executable, pid, () =>
                {
                    File.Move(paths.Updater, heldUpdater);
                    return true;
                }, FinishNative));
                Assert(launches == 0 && File.ReadAllBytes(marker).SequenceEqual(consumed), "retry did not recheck the updater after confirmation");
            }
            finally { if (File.Exists(heldUpdater)) File.Move(heldUpdater, paths.Updater); }

            UninstallSupport.ScheduleConsumedRetry(paths.LocalData, paths.Executable, pid, () =>
            {
                RejectUninstall(() => { using var writer = new FileStream(marker, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete); });
                return Confirm();
            }, FinishNative);
            Assert(confirmations == 1 && launches == 1 && File.ReadAllBytes(marker).SequenceEqual(consumed) && File.Exists(blockedCache)
                && !Directory.Exists(paths.Documents) && !Directory.Exists(paths.Data), "native retry changed the consumed marker or opened the data folders");
        }));
        await Run("Uninstall: installation path with a percent sign stays blocked at every native handoff", () => Task.Run(() =>
        {
            var source = UninstallFixture("old-marker");
            var paths = UninstallFixture("Music 100% local");
            var pid = Environment.ProcessId;
            var nonce = UninstallSupport.CreateReceipt(source);
            var oldMarker = JsonNode.Parse(File.ReadAllText(Path.Combine(source.Install, "full-uninstall.pending.json")))!.AsObject();
            oldMarker["Install"] = paths.Install;
            oldMarker["Data"] = paths.Data;
            oldMarker["Music"] = paths.Music;
            var marker = Path.Combine(paths.Install, "full-uninstall.pending.json");
            File.WriteAllText(marker, oldMarker.ToJsonString());
            RejectUninstall(() => UninstallSupport.ValidateInstallation(paths.LocalData, paths.Executable));
            RejectUninstall(() => UninstallSupport.CreateReceipt(paths));
            RejectUninstall(() => UninstallSupport.BuildWaitStart(paths, pid, paths.Executable, [UninstallSupport.CompleteArgument, nonce]));
            var launches = 0;
            Process MockLaunch(ProcessStartInfo _) { launches++; return Process.GetCurrentProcess(); }
            var pending = File.ReadAllBytes(marker);
            RejectUninstall(() => UninstallSupport.ScheduleNativeHandoffAfterCleanup(paths, nonce, pid, MockLaunch));
            Assert(File.ReadAllBytes(marker).SequenceEqual(pending) && launches == 0, "unsafe handoff altered the pending marker or started a process");

            oldMarker["Consumed"] = true;
            File.WriteAllText(marker, oldMarker.ToJsonString());
            var consumed = File.ReadAllBytes(marker);
            var confirmed = false;
            RejectUninstall(() => UninstallSupport.ScheduleConsumedRetry(paths.LocalData, paths.Executable, pid,
                () => { confirmed = true; return true; }, MockLaunch));
            Assert(!confirmed && launches == 0 && File.ReadAllBytes(marker).SequenceEqual(consumed), "unsafe consumed retry was offered or changed its marker");
        }));
        await Run("Uninstall: denied HKCU registration is a best effort startup operation", () => Task.Run(() =>
        {
            var paths = UninstallFixture("blocked-registry");
            var attempts = 0;
            var registered = UninstallSupport.RegisterWindowsUninstall(() => paths.LocalData, paths.Executable, (_, _, _) =>
            {
                attempts++;
                throw new SecurityException("Synthetic registry refusal");
            });
            Assert(attempts == 1 && !registered && File.Exists(paths.Executable), "registry access refusal escaped setup registration");
        }));
        await Run("Uninstall: Windows registration verifies installed metadata independent of Documents and package cache", () => Task.Run(() =>
        {
            var paths = UninstallFixture("register-without-documents", createDocuments: false);
            var cache = Path.Combine(paths.Install, "packages");
            File.WriteAllText(cache, "fixture blocks the update cache");
            var registrations = 0;
            bool RegisterSyntheticKey(string install, string native, string wrapper)
            {
                registrations++;
                Assert(install == paths.Install && native == $"\"{paths.Updater}\" --uninstall"
                    && wrapper == $"\"{paths.Executable}\" {UninstallSupport.RequestArgument}", "registration used an unexpected key location or wrapper");
                return true;
            }
            Assert(UninstallSupport.RegisterWindowsUninstall(() => paths.LocalData, paths.Executable, RegisterSyntheticKey)
                && registrations == 1 && !Directory.Exists(paths.Documents) && File.ReadAllText(cache) == "fixture blocks the update cache",
                "Windows registration required unsupported cleanup paths or touched the cache");
            File.WriteAllText(Path.Combine(Path.GetDirectoryName(paths.Executable)!, "sq.version"), "<not a manifest");
            Assert(!UninstallSupport.RegisterWindowsUninstall(() => paths.LocalData, paths.Executable, RegisterSyntheticKey) && registrations == 1,
                "registration accepted damaged installation metadata");
        }));
        await Run("Uninstall: Windows registration repairs only the exact raw REG_SZ commands", () =>
        {
            var paths = UninstallFixture("registry-value-types");
            const string item = "UninstallString", quiet = "QuietUninstallString";
            var native = $"\"{paths.Updater}\" --uninstall";
            var wrapper = $"\"{paths.Executable}\" {UninstallSupport.RequestArgument}";
            var entries = new Dictionary<string, (RegistryValueKind? Kind, object? Value)>
            {
                ["InstallLocation"] = (RegistryValueKind.String, paths.Install),
                [item] = (RegistryValueKind.String, native),
                [quiet] = (RegistryValueKind.String, native + " --silent")
            };
            bool Repair() => UninstallSupport.RepairWindowsUninstallCommands(paths.Install, native, wrapper,
                name => entries.GetValueOrDefault(name),
                (name, value) => entries[name] = (RegistryValueKind.String, value),
                name => entries.Remove(name));
            foreach (var invalid in new (string Name, RegistryValueKind Type, object? Value)[]
            {
                ("InstallLocation", RegistryValueKind.ExpandString, paths.Install),
                (item, RegistryValueKind.ExpandString, native),
                (quiet, RegistryValueKind.ExpandString, native + " --silent"),
                (quiet, RegistryValueKind.ExpandString, "\"%FIXTURE_HOME%\\Update.exe\" --uninstall --silent"),
                (quiet, RegistryValueKind.Unknown, null)
            })
            {
                var original = entries[invalid.Name];
                entries[invalid.Name] = (invalid.Type, invalid.Value);
                var before = entries.ToArray();
                Assert(!Repair() && entries.SequenceEqual(before), "repair changed a custom registry value");
                entries[invalid.Name] = original;
            }
            Assert(Repair() && Equals(entries[item].Value, wrapper) && !entries.ContainsKey(quiet), "the known Windows commands were not repaired");
            Assert(Repair() && entries[item].Kind == RegistryValueKind.String, "the registered wrapper was not reusable");
            return Task.CompletedTask;
        });
        await Run("Uninstall: startup retries transient registry refusal and incomplete repair after installation becomes readable", async () =>
        {
            using var fixture = new DownloadCenterFixture("uninstall-registration-retry", 1);
            var window = fixture.Window;
            var paths = UninstallFixture("registration-retry");
            var manifest = Path.Combine(Path.GetDirectoryName(paths.Executable)!, "sq.version");
            var pipe = "Yamuse-uninstall-fixture-" + Guid.NewGuid().ToString("N");
            var native = $"\"{paths.Updater}\" --uninstall";
            const string item = "UninstallString", quiet = "QuietUninstallString";
            var values = new Dictionary<string, (RegistryValueKind? Kind, object? Value)>
            {
                ["InstallLocation"] = (RegistryValueKind.String, paths.Install),
                [item] = (RegistryValueKind.String, native),
                [quiet] = (RegistryValueKind.String, native + " --silent")
            };
            var writes = 0;
            var prompts = 0;
            var listeners = 0;
            var keyAttempts = 0;
            var denyCurrentAttributes = false;
            FileAttributes ReadCurrentAttributes(string path)
            {
                Assert(path == Path.GetDirectoryName(paths.Executable), "startup queried the wrong current directory");
                return denyCurrentAttributes ? throw new UnauthorizedAccessException("fixture current directory attributes") : File.GetAttributes(path);
            }
            var delays = new[] { 1, 3, 10, 30, 60, 60, 60 };
            var ready = delays.Select(_ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).ToArray();
            var elapsed = delays.Select(_ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).ToArray();
            var scheduled = 0;
            Task Delay(TimeSpan duration, CancellationToken token)
            {
                var index = scheduled++;
                Assert(index < delays.Length && duration == TimeSpan.FromSeconds(delays[index]), "startup exceeded its retry frequency or kept retrying after repair");
                ready[index].SetResult();
                return elapsed[index].Task.WaitAsync(token);
            }
            UninstallSupport.WindowsUninstallRegistration Register() => UninstallSupport.CheckWindowsUninstallRegistration(
                () => paths.LocalData, paths.Executable, (install, existing, wrapper) =>
                {
                    if (++keyAttempts == 1) throw new SecurityException("fixture writable key is unavailable");
                    return UninstallSupport.RepairWindowsUninstallCommands(install, existing, wrapper,
                        name => values.GetValueOrDefault(name),
                        (name, value) => { writes++; values[name] = (RegistryValueKind.String, value); },
                        name => { if (keyAttempts != 2) { writes++; values.Remove(name); } });
                }, ReadCurrentAttributes);
            IDisposable Listen()
            {
                listeners++;
                return UninstallSupport.Listen(window, reply => window.ForwardFullUninstall(reply, () =>
                {
                    prompts++;
                    return Task.CompletedTask;
                }), pipe);
            }
            R4Set(window, "_restore", true);
            try
            {
                var initial = Register();
                Assert(initial == UninstallSupport.WindowsUninstallRegistration.Retryable && keyAttempts == 1 && writes == 0,
                    "temporary denial opening the known writable key terminated the retry");
                Task retry;
                using (var blocked = new FileStream(manifest, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    blocked.Lock(0, 1);
                    denyCurrentAttributes = true;
                    Assert(Register() == UninstallSupport.WindowsUninstallRegistration.Retryable && keyAttempts == 1 && writes == 0,
                        "managed current directory denial terminated the registry retry or touched the registry");
                    retry = window.InitializeFullUninstall(initial,
                        () => UninstallSupport.ValidateInstallationImage(paths.LocalData, paths.Executable, ReadCurrentAttributes), Listen, Register, Delay);
                    await ready[0].Task.WaitAsync(TimeSpan.FromSeconds(5));
                    elapsed[0].SetResult();
                    await ready[1].Task.WaitAsync(TimeSpan.FromSeconds(5));
                    Assert(listeners == 0 && writes == 0, "startup accepted the inaccessible image");
                    denyCurrentAttributes = false;
                    for (var index = 1; index < 5; index++)
                    {
                        elapsed[index].SetResult();
                        await ready[index + 1].Task.WaitAsync(TimeSpan.FromSeconds(5));
                    }
                    Assert(listeners == 1 && writes == 0 && values.ContainsKey(quiet), "startup skipped exact proof or failed to recover the listener");
                    blocked.Unlock(0, 1);
                }
                elapsed[5].SetResult();
                await ready[6].Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert(writes == 1 && keyAttempts == 2 && values.ContainsKey(quiet),
                    "the known partial repair did not remain eligible for another attempt");
                elapsed[^1].SetResult();
                await retry.WaitAsync(TimeSpan.FromSeconds(5));
                Assert(writes == 2 && keyAttempts == 3 && Equals(values[item].Value, $"\"{paths.Executable}\" --uninstall") && !values.ContainsKey(quiet),
                    "ordinary startup failed to restore the known Windows command");
                Assert(await Task.Run(() => UninstallSupport.TryForwardToOpenApplication(pipe)) == UninstallSupport.ForwardedUninstall.Accepted,
                    "the restored wrapper could not reach the existing listener");
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Assert(listeners == 1 && prompts == 1 && !UninstallSupport.HasPendingReceipt(paths), "retry duplicated the listener or wrote a receipt");
            }
            finally { R4Set(window, "_restore", false); }
        });
        await Run("Uninstall: portable startup never queues repair for an absent unrelated install root", async () =>
        {
            using var fixture = new DownloadCenterFixture("uninstall-portable-startup", 1);
            var window = fixture.Window;
            var root = Path.Combine(_directory, "portable-startup-" + Guid.NewGuid().ToString("N"));
            var local = Path.Combine(root, "LocalAppData");
            var portable = Path.Combine(root, "Portable", "current", "YMusic.Native.exe");
            Directory.CreateDirectory(local);
            Directory.CreateDirectory(Path.GetDirectoryName(portable)!);
            File.WriteAllText(portable, "fixture portable image");
            var registration = UninstallSupport.CheckWindowsUninstallRegistration(() => local, portable,
                (_, _, _) => throw new InvalidOperationException("portable tried to write a registry key"));
            R4Set(window, "_restore", true);
            try
            {
                await window.InitializeFullUninstall(registration, () => UninstallSupport.ValidateInstallationImage(local, portable),
                    () => throw new InvalidOperationException("portable started an uninstall listener"),
                    () => throw new InvalidOperationException("portable attempted registration"),
                    (_, _) => throw new InvalidOperationException("portable scheduled a retry"));
                Assert(registration == UninstallSupport.WindowsUninstallRegistration.Inapplicable
                    && !Directory.Exists(Path.Combine(local, BrandPaths.LegacyPackageId)), "portable was treated as a temporarily unavailable installation");
            }
            finally { R4Set(window, "_restore", false); }
        });
        await Run("Uninstall: listener survives unavailable Documents and checks the folder for each request", () =>
        {
            using var fixture = new DownloadCenterFixture("uninstall-listener", 1);
            var paths = UninstallFixture("listener-without-documents", createDocuments: false);
            var window = fixture.Window;
            using var listener = new ManualResetEvent(false);
            R4Set(window, "_restore", true);
            window.InitializeFullUninstall(() => UninstallSupport.ValidateInstallation(paths.LocalData, paths.Executable), () => listener);
            Assert(ReferenceEquals(SaField<IDisposable>(window, "_uninstallListener"), listener)
                && Control<Button>(window, "SettingsUninstall").IsEnabled, "the installed listener depended on an available Documents folder");

            var confirmations = 0;
            bool Decline() { confirmations++; return false; }
            RejectUninstall(() => window.ConfirmDocumentsForFullUninstall(paths, Decline));
            Assert(confirmations == 0 && !UninstallSupport.HasPendingReceipt(paths), "unavailable Documents reached confirmation or wrote a marker");
            Directory.CreateDirectory(paths.Documents);
            Assert(window.ConfirmDocumentsForFullUninstall(paths, Decline) is null && confirmations == 1
                && !UninstallSupport.HasPendingReceipt(paths), "the same listener could not prompt after Documents returned");
            R4Set(window, "_restore", false);
            return Task.CompletedTask;
        });
        foreach (var (condition, isManifest, absent) in new[]
        {
            ("manifest lock", true, false), ("an absent manifest", true, true),
            ("updater lock", false, false), ("an absent updater", false, true)
        })
        await Run("Uninstall: " + condition + " preserves the installed listener and permits known-key repair later", async () =>
        {
            using var fixture = new DownloadCenterFixture("uninstall-evidence-unavailable-" + isManifest + "-" + absent, 1);
            var paths = UninstallFixture("listener-manifest-lock");
            var blockedPath = isManifest ? Path.Combine(Path.GetDirectoryName(paths.Executable)!, "sq.version") : paths.Updater;
            var parked = blockedPath + ".fixture-unavailable";
            var window = fixture.Window;
            var pipe = "Yamuse-uninstall-fixture-" + Guid.NewGuid().ToString("N");
            var listeners = 0;
            var requests = 0;
            var registrations = 0;
            IDisposable Listen()
            {
                listeners++;
                return UninstallSupport.Listen(window, reply => window.ForwardFullUninstall(reply, () =>
                {
                    UninstallSupport.ValidateInstallation(paths.LocalData, paths.Executable);
                    requests++;
                    return Task.CompletedTask;
                }), pipe);
            }
            bool Register() => UninstallSupport.RegisterWindowsUninstall(() => paths.LocalData, paths.Executable, (install, _, _) =>
            {
                Assert(install == paths.Install, "registration proof changed installation");
                registrations++;
                return true;
            });
            R4Set(window, "_restore", true);
            try
            {
                if (absent) File.Move(blockedPath, parked);
                try
                {
                    using var blocked = absent ? null : new FileStream(blockedPath, FileMode.Open, FileAccess.Read, FileShare.None);
                    window.InitializeFullUninstall(() => UninstallSupport.ValidateInstallationImage(paths.LocalData, paths.Executable), Listen);
                    Assert(listeners == 1, "unavailable installation metadata discarded the installed listener");
                    window.RetryWindowsUninstallRegistration(Register);
                    Assert(registrations == 0, "the existing Windows key changed before authenticating the manifest");
                    RejectUninstall(() => UninstallSupport.ValidateInstallation(paths.LocalData, paths.Executable));
                }
                finally { if (absent) File.Move(parked, blockedPath); }
                window.RetryWindowsUninstallRegistration(Register);
                window.RetryWindowsUninstallRegistration(Register);
                Assert(Control<Button>(window, "SettingsUninstall").IsEnabled && listeners == 1 && registrations == 1,
                    "the same window did not restore the checked route");
                Assert(await Task.Run(() => UninstallSupport.TryForwardToOpenApplication(pipe)) == UninstallSupport.ForwardedUninstall.Accepted,
                    "the recovered window did not accept the wrapper request");
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Assert(requests == 1 && !UninstallSupport.HasPendingReceipt(paths), "the synthetic request did not run once without writing a receipt");
            }
            finally { R4Set(window, "_restore", false); }
        });
        await Run("Uninstall: Windows forwarding reports a busy hidden window and a closing dispatcher", async () =>
        {
            using var fixture = new DownloadCenterFixture("uninstall-forwarding", 1);
            var window = fixture.Window;
            window.Hide();
            var pipeName = "Yamuse-uninstall-fixture-" + Guid.NewGuid().ToString("N");
            var accepted = 0;
            using var listener = UninstallSupport.Listen(window, reply => window.ForwardFullUninstall(reply, () =>
            {
                accepted++;
                return Task.CompletedTask;
            }), pipeName);
            R4Set(window, "_restore", true);
            try
            {
                R4Set(window, "_cacheMaintenance", true);
                var busy = await Task.Run(() => UninstallSupport.TryForwardToOpenApplication(pipeName));
                Assert(busy == UninstallSupport.ForwardedUninstall.Maintenance && accepted == 0,
                    "forwarding reported delivery to a hidden busy window");
                Assert(UninstallSupport.ForwardedUninstallMessage(busy.GetValueOrDefault()).Contains("очистки"), "Windows wrapper did not have a visible maintenance explanation");
                R4Set(window, "_cacheMaintenance", false);
                R4Set(window, "_closeCommitInProgress", true);
                var closing = await Task.Run(() => UninstallSupport.TryForwardToOpenApplication(pipeName));
                Assert(closing == UninstallSupport.ForwardedUninstall.Closing && accepted == 0, "forwarding silently lost a closing-window request");
                R4Set(window, "_closeCommitInProgress", false);
                Assert(await Task.Run(() => UninstallSupport.TryForwardToOpenApplication(pipeName)) == UninstallSupport.ForwardedUninstall.Accepted,
                    "the repeated request was not accepted");
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Assert(accepted == 1, "the repeated request did not run exactly once");
            }
            finally
            {
                R4Set(window, "_cacheMaintenance", false);
                R4Set(window, "_closeCommitInProgress", false);
                R4Set(window, "_restore", false);
            }

            using var delayed = UninstallSupport.Listen(window, reply =>
            {
                accepted++;
                reply(UninstallSupport.ForwardedUninstall.Accepted);
            }, pipeName + "-delayed", TimeSpan.FromMilliseconds(100));
            UninstallSupport.ForwardedUninstall? missed;
            using (Dispatcher.CurrentDispatcher.DisableProcessing())
                missed = Task.Run(() => UninstallSupport.TryForwardToOpenApplication(pipeName + "-delayed")).GetAwaiter().GetResult();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert(missed == UninstallSupport.ForwardedUninstall.Closing && accepted == 1,
                "the wrapper lost a dispatcher timeout or showed an abandoned confirmation");
        });
        await Run("Uninstall: another Windows request receives Busy while confirmation is modal", async () =>
        {
            using var fixture = new DownloadCenterFixture("uninstall-forwarding-modal", 1);
            var window = fixture.Window;
            var pipeName = "Yamuse-uninstall-fixture-" + Guid.NewGuid().ToString("N");
            var modal = new DispatcherFrame();
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var confirmations = 0;
            Task Confirm()
            {
                if (++confirmations == 1)
                {
                    entered.SetResult();
                    Dispatcher.PushFrame(modal);
                }
                return Task.CompletedTask;
            }

            R4Set(window, "_restore", true);
            using var listener = UninstallSupport.Listen(window, reply => window.ForwardFullUninstall(reply, Confirm), pipeName);
            Task<UninstallSupport.ForwardedUninstall?> Request() => Task.Run(() => UninstallSupport.TryForwardToOpenApplication(pipeName));
            try
            {
                RejectUninstall(() => window.ForwardFullUninstall(_ => throw new IOException("fixture wrapper disconnected"), Confirm));
                Assert(!SaField<bool>(window, "_fullUninstallPending") && confirmations == 0, "a failed reply left the prompt reserved");
                Assert(await Request() == UninstallSupport.ForwardedUninstall.Accepted, "the first wrapper was not admitted");
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert(await Request() == UninstallSupport.ForwardedUninstall.Busy && confirmations == 1,
                    "the pipe stayed occupied by the modal operation or opened duplicate confirmations");
                modal.Continue = false;
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Assert(await Request() == UninstallSupport.ForwardedUninstall.Accepted, "the dismissed dialog kept the next wrapper busy");
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Assert(confirmations == 2, "the new request did not run after the previous confirmation ended");
            }
            finally
            {
                modal.Continue = false;
                R4Set(window, "_restore", false);
            }
        });
        await Run("Uninstall: the pipe accepts a new client after an early lost connection", async () =>
        {
            using var fixture = new DownloadCenterFixture("uninstall-forwarding-lost-connection", 1);
            var pipeName = "Yamuse-uninstall-fixture-" + Guid.NewGuid().ToString("N");
            const int noData = unchecked((int)0x800700E8);
            NamedPipeServerStream? abandoned = null;
            var requests = 0;
            Task Accept(NamedPipeServerStream pipe, CancellationToken token)
            {
                if (abandoned is not null)
                {
                    Assert(!ReferenceEquals(abandoned, pipe), "the listener reused an abandoned native endpoint");
                    return pipe.WaitForConnectionAsync(token);
                }
                abandoned = pipe;
                return Task.FromException(new IOException("fixture client closed before accept", noData));
            }

            using var listener = UninstallSupport.Listen(fixture.Window, reply =>
            {
                requests++;
                reply(UninstallSupport.ForwardedUninstall.Busy);
            }, pipeName, waitForConnection: Accept);
            var result = await Task.Run(() => UninstallSupport.TryForwardToOpenApplication(pipeName));
            Assert(result == UninstallSupport.ForwardedUninstall.Busy && requests == 1, "a lost connection stopped later requests");
        });
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
            Directory.CreateDirectory(paths.Data);
            new SessionStore(paths.Data).Save(new("fixture-token", null, DateTimeOffset.UnixEpoch, "fixture-device"));
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
        await Run("Uninstall: both data roots preserve unknown entries and metadata on known entries", () => Task.Run(() =>
        {
            var paths = UninstallFixture("data-ownership");
            var useNtfs = new DriveInfo(Path.GetPathRoot(paths.Data)!).DriveFormat == "NTFS";
            var preserved = new Dictionary<string, string>();
            foreach (var root in new[] { paths.Data, paths.LegacyData })
            {
                var backup = Path.Combine(root, "personal backup");
                Directory.CreateDirectory(backup);
                var artwork = Path.Combine(root, "artwork");
                Directory.CreateDirectory(artwork);
                preserved.Add(Path.Combine(root, "notes.txt"), "fixture notes");
                preserved.Add(Path.Combine(backup, "settings.json"), "fixture personal backup");
                preserved.Add(Path.Combine(artwork, "my-cover.png"), "fixture foreign image bytes");
                File.WriteAllText(Path.Combine(root, DownloadQueueStore.FileName), """{"version":0}""");
            }
            preserved.Add(Path.Combine(paths.LegacyData, "settings.json"), """{"note":"fixture forecast"}""");
            preserved.Add(Path.Combine(paths.Data, PlaybackStateStore.FileName), "corrupt fixture playback state");
            foreach (var (file, bytes) in preserved) File.WriteAllText(file, bytes);
            var withStream = Path.Combine(paths.Data, "settings.json");
            var withEa = Path.Combine(paths.LegacyData, "session.dpapi");
            var taggedDirectory = Path.Combine(paths.LegacyData, "diagnostics");
            if (useNtfs)
            {
                File.WriteAllText(withStream, """{"volume":0.25}""");
                new SessionStore(paths.LegacyData).Save(new("fixture-token", null, DateTimeOffset.UnixEpoch, "fixture-device"));
                File.WriteAllText(withStream + ":fixture-note", "fixture stream");
                Cleanup015AddExtendedAttribute(withEa);
                Directory.CreateDirectory(taggedDirectory);
                AddFixtureDirectoryExtendedAttribute(taggedDirectory);
            }

            var result = UninstallCleanup.Execute(paths);
            Assert(result.Complete && result.RemovedFiles == 2 && result.PreservedForeignFiles == preserved.Count + (useNtfs ? 3 : 0),
                JsonSerializer.Serialize(result));
            Assert(preserved.All(entry => File.ReadAllText(entry.Key) == entry.Value), "cleanup changed a user file or nested app-like backup");
            foreach (var root in new[] { paths.Data, paths.LegacyData }) Assert(!File.Exists(Path.Combine(root, DownloadQueueStore.FileName)), "cleanup left a plain reserved state slot");
            if (useNtfs)
            {
                Assert(File.ReadAllText(withStream + ":fixture-note") == "fixture stream", "data cleanup lost an app file stream");
                using var file = UninstallPathGuard.OpenOwnedFile(withEa, read: true, deleteAccess: false);
                using var directory = UninstallPathGuard.HoldDirectory(taggedDirectory);
                Assert(FileSecurityPreservation.HasNamedStreamsOrExtendedAttributes(file)
                    && FileSecurityPreservation.HasNamedStreamsOrExtendedAttributes(directory), "data cleanup lost file or directory EA");
            }
        }));
        await Run("Uninstall: interrupted protected session saves are removed after an inaccessible previous copy returns", () => Task.Run(() =>
        {
            foreach (var published in new[] { false, true })
            {
                var paths = UninstallFixture("interrupted-session-" + published);
                var root = published ? paths.LegacyData : paths.Data;
                var slot = Path.Combine(root, "session.dpapi");
                var replacementRoot = Path.Combine(paths.LocalData, "fixture-new-session");
                new SessionStore(root).Save(new("fixture-prior-token", null, DateTimeOffset.UnixEpoch, "fixture-device"));
                new SessionStore(replacementRoot).Save(new("fixture-new-token", null, DateTimeOffset.UnixEpoch, "fixture-device"));
                var recovery = new GuardedStoreRecovery(slot);
                var receipt = Cleanup015ParkStoreReplacement(slot, recovery, publishReplacement: published,
                    replacement: File.ReadAllBytes(Path.Combine(replacementRoot, "session.dpapi")));
                var marker = Path.ChangeExtension(recovery.Previous, ".receipt");
                var priorBytes = File.ReadAllBytes(recovery.Previous);
                var receiptBytes = File.ReadAllBytes(marker);
                Assert(receipt.PreviousProof is not null && File.Exists(slot) == published, "fixture did not interrupt the authenticated save at the intended point");
                using (var denied = new FileStream(recovery.Previous, FileMode.Open, FileAccess.Read, FileShare.None))
                {
                    var partial = UninstallCleanup.Execute(paths);
                    Assert(!partial.Complete && partial.RemovedFiles == 0 && File.Exists(slot) == published && File.Exists(marker),
                        "temporary denial completing a session save was treated as complete or changed its primary file");
                }
                Assert(File.ReadAllBytes(recovery.Previous).SequenceEqual(priorBytes) && File.ReadAllBytes(marker).SequenceEqual(receiptBytes),
                    "the blocked cleanup changed the parked session or its receipt");
                var result = UninstallCleanup.Execute(paths);
                Assert(result.Complete && result.RemovedFiles == (published ? 3 : 2) && result.PreservedForeignFiles == 0
                    && !File.Exists(slot) && !recovery.Pending && !Directory.Exists(root), JsonSerializer.Serialize(result));
            }
        }));
        await Run("Uninstall: changed or annotated parked files retain their authenticated receipts", () => Task.Run(() =>
        {
            foreach (var annotated in new[] { false, true })
            {
                var paths = UninstallFixture("changed-parked-state-" + annotated);
                if (annotated && new DriveInfo(Path.GetPathRoot(paths.Data)!).DriveFormat != "NTFS") continue;
                var store = new PreferencesStore(paths.Data);
                store.Save(new() { Volume = 0.25 });
                var recovery = new GuardedStoreRecovery(store.FilePath);
                Cleanup015ParkStoreReplacement(store.FilePath, recovery);
                if (annotated) File.WriteAllText(recovery.Previous + ":fixture-note", "personal fixture metadata");
                else File.WriteAllText(recovery.Previous, """{"volume":0.35}""");
                var marker = Path.ChangeExtension(recovery.Previous, ".receipt");
                var priorBytes = File.ReadAllBytes(recovery.Previous);
                var receiptBytes = File.ReadAllBytes(marker);
                var result = UninstallCleanup.Execute(paths);
                Assert(result.Complete && result.RemovedFiles == 0 && result.PreservedForeignFiles == 2
                    && !File.Exists(store.FilePath) && File.ReadAllBytes(recovery.Previous).SequenceEqual(priorBytes)
                    && File.ReadAllBytes(marker).SequenceEqual(receiptBytes), JsonSerializer.Serialize(result));
                if (annotated) Assert(File.ReadAllText(recovery.Previous + ":fixture-note") == "personal fixture metadata", "cleanup changed the parked file's named stream");
            }
        }));
        await Run("Uninstall: favorites cache deletion uses the store validator in both data roots", async () =>
        {
            var paths = UninstallFixture("favorites-cache-proof");
            var valid = new List<string>();
            var foreign = new Dictionary<string, byte[]>();
            foreach (var root in new[] { paths.Data, paths.LegacyData })
            {
                const string keptUid = "fixture-invalid-favorite", removedUid = "fixture-valid-favorite";
                var store = new FavoritesCacheStore(root);
                await store.SaveInBackgroundAsync(FavoritesCacheStore.Capture(removedUid, [FcTrack("101")], DateTimeOffset.UtcNow));
                await store.SaveInBackgroundAsync(FavoritesCacheStore.Capture(keptUid, [FcTrack("102")], DateTimeOffset.UtcNow));
                valid.Add(store.FilePath(removedUid));
                var file = store.FilePath(keptUid);
                var json = JsonNode.Parse(File.ReadAllText(file))!.AsObject();
                json["tracks"] = new JsonArray((JsonNode?)null);
                var bytes = Encoding.UTF8.GetBytes(json.ToJsonString());
                File.WriteAllBytes(file, bytes);
                foreign.Add(file, bytes);
                Assert(store.Read(removedUid).Status == FavoritesCacheReadStatus.Loaded && store.Read(keptUid).Status == FavoritesCacheReadStatus.Invalid,
                    "fixture snapshots did not distinguish genuine from invalid favorites rows");
            }
            var result = await Task.Run(() => UninstallCleanup.Execute(paths));
            Assert(result.Complete && result.RemovedFiles == valid.Count && result.PreservedForeignFiles == foreign.Count
                && valid.All(file => !File.Exists(file)) && foreign.All(entry => File.ReadAllBytes(entry.Key).SequenceEqual(entry.Value)),
                JsonSerializer.Serialize(result));
        });
        await Run("Uninstall: both published generations of automatic reports require matching scope and metadata", () => Task.Run(() =>
        {
            var paths = UninstallFixture("diagnostic-generations");
            var useNtfs = new DriveInfo(Path.GetPathRoot(paths.Data)!).DriveFormat == "NTFS";
            var valid = new List<string>();
            var foreign = new Dictionary<string, byte[]>();
            foreach (var root in new[] { paths.Data, paths.LegacyData })
            {
                var directory = Path.Combine(root, "diagnostics");
                Directory.CreateDirectory(directory);
                var report = JsonSerializer.SerializeToUtf8Bytes(new { schema = 1, appVersion = "0.7.0", processId = 1234,
                    executableSha256 = new string('a', 64), samples = Array.Empty<object>() });
                valid.Add(Path.Combine(directory, "session-20261119-110000-1234.json"));
                valid.Add(Path.Combine(directory, "session-20261119-110001-1234-" + Guid.NewGuid().ToString("N") + ".json"));
                foreach (var file in valid.Where(file => Path.GetDirectoryName(file) == directory)) File.WriteAllBytes(file, report);
                var mismatched = Path.Combine(directory, "session-20261119-110000-5678.json");
                File.WriteAllBytes(mismatched, report);
                foreign.Add(mismatched, report);
                if (useNtfs)
                {
                    var tagged = Path.Combine(directory, "session-20261119-110002-1234.json");
                    File.WriteAllBytes(tagged, report);
                    File.WriteAllText(tagged + ":fixture-note", "personal fixture metadata");
                    foreign.Add(tagged, report);
                }
            }
            var result = UninstallCleanup.Execute(paths);
            Assert(result.Complete && result.RemovedFiles == valid.Count && result.PreservedForeignFiles == foreign.Count
                && valid.All(file => !File.Exists(file)) && foreign.All(entry => File.ReadAllBytes(entry.Key).SequenceEqual(entry.Value)),
                JsonSerializer.Serialize(result));
        }));
        await Run("Uninstall: a busy authenticated audio-buffer lease is retried and unleased audio survives", () => Task.Run(() =>
        {
            var paths = UninstallFixture("data-audio-buffer");
            if (new DriveInfo(Path.GetPathRoot(paths.Data)!).DriveFormat != "NTFS") return;
            var directory = Path.Combine(paths.Data, "buffer");
            Directory.CreateDirectory(directory);
            var audio = Path.Combine(directory, "ym-audio-" + Guid.NewGuid().ToString("N") + ".mp3");
            var unleased = Path.Combine(directory, "ym-audio-" + Guid.NewGuid().ToString("N") + ".mp3");
            File.WriteAllText(unleased, "fixture personal audio");
            using (var owned = UninstallPathGuard.CreateStagedFile(audio))
            {
                RandomAccess.Write(owned, Encoding.UTF8.GetBytes("fixture temporary audio"), 0);
                using var record = AudioBufferRecovery.Record(audio, owned);
                Assert(record is not null, "fixture needs an authenticated audio lease");
            }

            using (var locked = new FileStream(audio, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                var partial = UninstallCleanup.Execute(paths);
                Assert(!partial.Complete && partial.RemovedFiles == 0 && File.Exists(audio), "busy owned audio lost its recovery lease or was reported complete");
            }
            var result = UninstallCleanup.Execute(paths);
            Assert(result.Complete && result.RemovedFiles == 2 && result.PreservedForeignFiles == 1
                && !File.Exists(audio) && File.ReadAllText(unleased) == "fixture personal audio", JsonSerializer.Serialize(result));
        }));
        await Run("Uninstall: empty, relative, drive, device, intersecting and wrong install paths rejected", () => Task.Run(() =>
        {
            var paths = UninstallFixture("paths");
            foreach (var bad in new[] { "", " ", ".", "..", "C:\\", @"\\server\share", @"\\?\C:\data", "C:\\x:ads" })
                RejectUninstall(() => UninstallPaths.FromKnownFolders(bad, paths.Documents));
            RejectUninstall(() => UninstallPaths.FromKnownFolders(paths.Documents, Path.Combine(paths.Documents, "nested")));
            UninstallSupport.ValidateInstallation(paths.LocalData, paths.Executable);
            WriteUninstallManifest(paths.Executable, "OtherApp");
            RejectUninstall(() => UninstallSupport.ValidateInstallation(paths.LocalData, paths.Executable));
            WriteUninstallManifest(paths.Executable);
            var portable = Path.Combine(paths.Install, ".portable");
            File.WriteAllText(portable, "fixture portable marker");
            RejectUninstall(() => UninstallSupport.ValidateInstallation(paths.LocalData, paths.Executable));
            File.Delete(portable);
            RejectUninstall(() => UninstallSupport.ValidateInstallation(paths.Install, paths.Executable));
            RejectUninstall(() => UninstallSupport.ValidateInstallation(paths.LocalData, Path.Combine(paths.Documents, "YMusic.Native.exe")));
            Directory.CreateDirectory(Path.Combine(paths.Install, "app-0.3.10"));
            RejectUninstall(() => UninstallSupport.ValidateInstallation(paths.LocalData, paths.Executable));
            Assert(File.Exists(paths.Updater), "validation wrote to installation");
        }));
        await Run("Uninstall: unavailable Documents leaves cleanup incomplete until that exact folder returns", () => Task.Run(() =>
        {
            var paths = UninstallFixture("documents-unavailable-during-cleanup");
            WriteUninstallAudio(paths, "saved.mp3", "fixture audio");
            var moved = paths.Documents + "-unavailable";
            Directory.Move(paths.Documents, moved);
            var unavailable = UninstallCleanup.Execute(paths);
            Assert(!unavailable.Complete && unavailable.Issues.Any(issue => issue.Reason.Contains("«Документы»", StringComparison.Ordinal)),
                "the worker reported complete cleanup with no Documents anchor");
            Directory.Move(moved, paths.Documents);
            Assert(File.ReadAllText(Path.Combine(paths.Music, "saved.mp3")) == "fixture audio", "the unavailable library was changed");
            var retry = UninstallCleanup.Execute(paths);
            Assert(retry.Complete && retry.RemovedFiles == 2 && !Directory.Exists(paths.Music),
                "the worker did not clean up or admit a retry when Documents returned");
            var empty = UninstallCleanup.Execute(paths);
            Assert(empty.Complete && empty.RemovedFiles == 0, "an existing Documents with no library was rejected");
        }));
        await Run("Uninstall: Documents on a mapped drive is rejected at admission and checked again by cleanup", () => Task.Run(() =>
        {
            var original = UninstallFixture("mapped-documents");
            var network = false;
            DriveType Classify(string root)
            {
                Assert(root == Path.GetPathRoot(original.Documents), "the Documents guard queried the wrong volume");
                return network ? DriveType.Network : DriveType.Fixed;
            }
            var paths = UninstallPaths.FromKnownFolders(original.LocalData, original.Documents, Classify);
            WriteUninstallAudio(paths, "saved.mp3", "fixture audio");
            var admitted = UninstallSupport.CaptureDocumentsIdentity(paths);
            network = true;
            RejectUninstall(() => UninstallPaths.FromKnownFolders(paths.LocalData, paths.Documents, Classify));
            RejectUninstall(() => UninstallSupport.CreateReceipt(paths, admitted));
            var result = UninstallCleanup.Execute(paths, admitted);
            Assert(!result.Complete && File.ReadAllText(Path.Combine(paths.Music, "saved.mp3")) == "fixture audio"
                && !UninstallSupport.HasPendingReceipt(paths), "the mapped Documents folder reached cleanup or marker creation");
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
        await Run("Uninstall: indexed audio with a user data stream stays on disk", () => Task.Run(() =>
        {
            var paths = UninstallFixture("named-streams");
            WriteUninstallAudio(paths, "tagged.mp3", "fixture owned audio");
            WriteUninstallAudio(paths, "plain.mp3", "fixture owned audio without metadata");
            var tagged = Path.Combine(paths.Music, "tagged.mp3");
            var comment = tagged + ":user-comment";
            File.WriteAllText(comment, "private user metadata fixture");
            var result = UninstallCleanup.Execute(paths);
            Assert(result.Complete && result.PreservedForeignFiles == 1 && result.RemovedFiles == 2, JsonSerializer.Serialize(result));
            Assert(File.ReadAllText(tagged) == "fixture owned audio" && File.ReadAllText(comment) == "private user metadata fixture"
                && !File.Exists(Path.Combine(paths.Music, "plain.mp3")), "cleanup lost user metadata or kept unrelated owned audio");
        }));
        await Run("Uninstall: named streams in music indexes and both music trees are preserved", () => Task.Run(() =>
        {
            var paths = UninstallFixture("music-index-directory-streams");
            WriteUninstallAudio(paths, "saved.mp3", "fixture audio");
            var library = Path.Combine(paths.Music, "library.index.json");
            var playlists = Path.Combine(paths.Music, "playlists.index.json");
            File.WriteAllText(playlists, """{"schemaVersion":1,"playlists":[]}""");
            Directory.CreateDirectory(paths.LegacyMusic);
            var legacyParent = Path.GetDirectoryName(paths.LegacyMusic)!;
            var notes = new[] { library + ":fixture-note", playlists + ":fixture-note",
                paths.Music + ":fixture-note", legacyParent + ":fixture-note" };
            foreach (var note in notes) File.WriteAllText(note, "keep user data");
            var result = UninstallCleanup.Execute(paths);
            Assert(result.Complete && result.RemovedFiles == 1 && result.PreservedForeignFiles == notes.Length,
                JsonSerializer.Serialize(result));
            Assert(notes.All(note => File.ReadAllText(note) == "keep user data")
                && !File.Exists(Path.Combine(paths.Music, "saved.mp3")) && !Directory.Exists(paths.LegacyMusic),
                "cleanup changed a named stream or retained clean owned entries");
        }));
        await Run("Uninstall: extended attributes survive on owned music, indexes and empty directories", () => Task.Run(() =>
        {
            var paths = UninstallFixture("music-extended-attributes");
            if (new DriveInfo(Path.GetPathRoot(paths.Music)!).DriveFormat != "NTFS") return;
            WriteUninstallAudio(paths, "tagged.mp3", "owned fixture audio with metadata");
            WriteUninstallAudio(paths, "plain.mp3", "owned fixture audio without metadata");
            var tagged = Path.Combine(paths.Music, "tagged.mp3");
            var library = Path.Combine(paths.Music, "library.index.json");
            var playlists = Path.Combine(paths.Music, "playlists.index.json");
            File.WriteAllText(playlists, """{"schemaVersion":1,"playlists":[]}""");
            var taggedFiles = new[] { tagged, library, playlists };
            foreach (var file in taggedFiles) Cleanup015AddExtendedAttribute(file);

            Directory.CreateDirectory(paths.LegacyMusic);
            var taggedDirectories = new[] { paths.LegacyMusic, Path.GetDirectoryName(paths.LegacyMusic)! };
            foreach (var directory in taggedDirectories) AddFixtureDirectoryExtendedAttribute(directory);

            var result = UninstallCleanup.Execute(paths);
            Assert(result.Complete && result.RemovedFiles == 1 && result.PreservedForeignFiles == taggedFiles.Length + taggedDirectories.Length,
                JsonSerializer.Serialize(result));
            Assert(!File.Exists(Path.Combine(paths.Music, "plain.mp3")) && File.ReadAllText(tagged) == "owned fixture audio with metadata",
                "the worker did not distinguish owned files with and without additional attributes");
            foreach (var file in taggedFiles)
            {
                using var handle = UninstallPathGuard.OpenOwnedFile(file, read: true, deleteAccess: false);
                Assert(FileSecurityPreservation.HasNamedStreamsOrExtendedAttributes(handle) && !FileSecurityPreservation.HasNamedDataStreams(handle),
                    "a preserved music file lost its extended attribute");
            }
            foreach (var directory in taggedDirectories)
            {
                using var handle = UninstallPathGuard.HoldDirectory(directory);
                Assert(FileSecurityPreservation.HasNamedStreamsOrExtendedAttributes(handle) && !FileSecurityPreservation.HasNamedDataStreams(handle),
                    "a preserved directory lost its extended attribute");
            }
        }));
        await Run("Uninstall: locked audio and data reported; index kept for retry; unlocked retry removes known files", () => Task.Run(() =>
        {
            var paths = UninstallFixture("locked");
            Directory.CreateDirectory(paths.Data);
            var lockedData = Path.Combine(paths.Data, "settings.json"); File.WriteAllText(lockedData, """{"volume":0.25}""");
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
        await Run("Uninstall: music index uses the same full rows and encoding as offline playback", () => Task.Run(() =>
        {
            var paths = UninstallFixture("index-encoding");
            WriteUninstallAudio(paths, "saved.mp3", "fixture audio");
            var index = Path.Combine(paths.Music, "library.index.json");
            var sound = Path.Combine(paths.Music, "saved.mp3");
            var original = File.ReadAllText(index);
            var item = JsonSerializer.Deserialize<OfflineItem[]>(original)!.Single();
            var incomplete = JsonSerializer.Serialize(new[] { new { item.FileName, item.Bytes, item.Sha256 } });
            File.WriteAllText(index, incomplete);
            var rejected = UninstallCleanup.Execute(paths);
            Assert(!rejected.Complete && File.ReadAllText(sound) == "fixture audio" && File.ReadAllText(index) == incomplete,
                "destructive cleanup accepted a row refused by the library");
            File.WriteAllText(index, original, Encoding.Unicode);
            using (var library = new OfflineLibrary(paths.Music, new AudioBuffer(Path.Combine(paths.Music, ".fixture-buffer"))))
                Assert(library.Read().Single().Sha256 == item.Sha256, "the UTF-16 fixture was not a supported library index");
            var accepted = UninstallCleanup.Execute(paths);
            Assert(accepted.Complete && accepted.RemovedFiles == 2 && !File.Exists(sound) && !File.Exists(index),
                "destructive cleanup rejected the library's supported BOM");
        }));
        await Run("Uninstall: ambiguous audio index properties preserve the index and its audio", () => Task.Run(() =>
        {
            var paths = UninstallFixture("duplicate-audio-index");
            WriteUninstallAudio(paths, "saved.mp3", "fixture indexed audio");
            var index = Path.Combine(paths.Music, "library.index.json");
            var valid = File.ReadAllText(index);
            var duplicates = valid.Replace("\"Title\":\"Fixture track\"", "\"Title\":\"Earlier title\",\"Title\":\"Fixture track\"", StringComparison.Ordinal);
            Assert(duplicates != valid, "fixture must add a duplicate property to a supported row");
            var original = Encoding.UTF8.GetBytes(duplicates);
            File.WriteAllBytes(index, original);
            var result = UninstallCleanup.Execute(paths);
            Assert(!result.Complete && result.RemovedFiles == 0 && result.Issues.Any(issue => issue.Reason.Contains("Индекс музыки повреждён", StringComparison.Ordinal))
                && File.ReadAllBytes(index).SequenceEqual(original) && File.ReadAllText(Path.Combine(paths.Music, "saved.mp3")) == "fixture indexed audio",
                "duplicate audio ownership preserved neither the malformed index nor its audio");
        }));
        await Run("Uninstall: requires supported playlist index bytes in both music trees", () => Task.Run(() =>
        {
            foreach (var (label, text, remove) in new[]
            {
                ("supported", "{\"schemaVersion\":1,\"playlists\":[]}", true),
                ("metadata", "{\"schemaVersion\":1,\"playlists\":[]}", false),
                ("foreign", "A note saved under the reserved name.", false),
                ("corrupt", "{\"schemaVersion\":1,\"playlists\":[", false),
                ("unsupported", "{\"schemaVersion\":2,\"playlists\":[]}", false),
                ("duplicate-root", """{"schemaVersion":2,"schemaVersion":1,"playlists":[]}""", false),
                ("duplicate-row", """{"schemaVersion":1,"playlists":[{"owner":"fixture","kind":"playlist","title":"Earlier","title":"Fixture","revision":0,"requestedAudioQuality":1,"updatedUtc":"2026-01-01T00:00:00Z","tracks":[]}]}""", false)
            })
            {
                var paths = UninstallFixture("playlist-index-" + label);
                if (label == "metadata" && new DriveInfo(Path.GetPathRoot(paths.Music)!).DriveFormat != "NTFS") continue;
                var indexes = new[] { paths.Music, paths.LegacyMusic }.Select(music =>
                {
                    Directory.CreateDirectory(music);
                    var path = Path.Combine(music, "playlists.index.json");
                    File.WriteAllText(path, text, Encoding.Unicode);
                    return path;
                }).ToArray();
                if (label == "metadata")
                {
                    File.WriteAllText(indexes[0] + ":fixture-note", "fixture metadata");
                    Cleanup015AddExtendedAttribute(indexes[1]);
                }
                var original = File.ReadAllBytes(indexes[0]);
                var result = UninstallCleanup.Execute(paths);
                Assert(result.Complete && result.RemovedFiles == (remove ? 2 : 0) && result.PreservedForeignFiles == (remove ? 0 : 2),
                    label + " " + JsonSerializer.Serialize(result));
                Assert(indexes.All(index => remove ? !File.Exists(index) : File.ReadAllBytes(index).SequenceEqual(original)),
                    "the worker did not preserve unknown playlist bytes or remove supported ones");
                if (label == "metadata")
                {
                    Assert(File.ReadAllText(indexes[0] + ":fixture-note") == "fixture metadata", "the playlist index lost its named stream");
                    using var file = UninstallPathGuard.OpenOwnedFile(indexes[1], read: true, deleteAccess: false);
                    Assert(FileSecurityPreservation.HasNamedStreamsOrExtendedAttributes(file), "the legacy playlist index lost its EA");
                }
            }
        }));
        await Run("Uninstall: playlist index I/O refusal preserves exact bytes for a later retry", () => Task.Run(() =>
        {
            var paths = UninstallFixture("playlist-index-busy");
            Directory.CreateDirectory(paths.LegacyMusic);
            var index = Path.Combine(paths.LegacyMusic, "playlists.index.json");
            var original = Encoding.UTF8.GetBytes("""{"schemaVersion":1,"playlists":[]}""");
            File.WriteAllBytes(index, original);
            using (var locked = new FileStream(index, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                locked.Lock(0, 1);
                using (var proof = UninstallPathGuard.OpenOwnedFile(index, read: true))
                {
                    var busy = false;
                    try { OfflineLibrary.IsSupportedPlaylistIndex(proof); }
                    catch (IOException error) when (error.HResult == unchecked((int)0x80070021)) { busy = true; }
                    Assert(busy, "the fixture must deny reading an already opened playlist index");
                }
                var result = UninstallCleanup.Execute(paths);
                Assert(!result.Complete && result.RemovedFiles == 0 && result.PreservedForeignFiles == 0
                    && result.Issues.Any(issue => issue.RelativePath == "playlists.index.json") && File.Exists(index),
                    "temporary playlist I/O was treated as successful preservation or deletion");
                locked.Unlock(0, 1);
            }
            Assert(File.ReadAllBytes(index).SequenceEqual(original), "temporary playlist I/O changed bytes before the retry");
            var retry = UninstallCleanup.Execute(paths);
            Assert(retry.Complete && retry.RemovedFiles == 1 && !File.Exists(index), "retry did not remove the readable supported playlist index");
        }));
        await Run("Uninstall: playlist ownership requires valid Unicode and accepts supported BOMs", () => Task.Run(() =>
        {
            const string title = "\uFFFD";
            var json = "{\"schemaVersion\":1,\"playlists\":[{\"owner\":\"fixture\",\"kind\":\"playlist\",\"title\":\"" + title
                + "\",\"revision\":0,\"requestedAudioQuality\":1,\"updatedUtc\":\"2026-01-01T00:00:00Z\",\"tracks\":[]}]}";
            var encodings = new Encoding[]
            {
                new UTF8Encoding(false), new UTF8Encoding(true), Encoding.Unicode, Encoding.BigEndianUnicode,
                Encoding.UTF32, new UTF32Encoding(true, true)
            };
            foreach (var encoding in encodings)
            {
                var paths = UninstallFixture("playlist-unicode-" + encoding.CodePage);
                Directory.CreateDirectory(paths.Music);
                var valid = Path.Combine(paths.Music, "playlists.index.json");
                File.WriteAllText(valid, json, encoding);
                using (var library = new OfflineLibrary(paths.Music, new AudioBuffer(Path.Combine(paths.Music, ".fixture-buffer"))))
                    Assert(library.GetPlaylists().Single().Title == title, "the decoder changed a literal replacement character");
                var result = UninstallCleanup.Execute(paths);
                Assert(result.Complete && result.RemovedFiles == 1 && result.PreservedForeignFiles == 0 && !File.Exists(valid),
                    "uninstall stopped recognizing a supported playlist encoding");
            }

            var corruptPaths = UninstallFixture("playlist-damaged-unicode");
            Directory.CreateDirectory(corruptPaths.LegacyMusic);
            var corrupt = Path.Combine(corruptPaths.LegacyMusic, "playlists.index.json");
            var original = Encoding.UTF8.GetBytes(json);
            var titleOffset = original.AsSpan().IndexOf(Encoding.UTF8.GetBytes(title));
            Assert(titleOffset >= 0, "the fixture did not contain the literal encoded title");
            original[titleOffset] = 0xff;
            File.WriteAllBytes(corrupt, original);
            var rejected = UninstallCleanup.Execute(corruptPaths);
            Assert(rejected.Complete && rejected.RemovedFiles == 0 && rejected.PreservedForeignFiles == 1
                && File.ReadAllBytes(corrupt).SequenceEqual(original), "uninstall rewrote or erased invalid Unicode playlist data");
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
            UninstallSupport.ValidateInstallation(paths.LocalData, paths.Executable);
            Directory.Delete(current, recursive: false); CreateUninstallJunction(current, outside);
            RejectUninstall(() => UninstallSupport.ValidateInstallationImage(paths.LocalData, paths.Executable));
            RejectUninstall(() => UninstallSupport.ValidateInstallation(paths.LocalData, paths.Executable));
            Assert(!YMusic.Native.Program.CanRecoverInstalledUpdateLocator(null, paths.Executable, paths.LocalData),
                "an external current target was accepted as a recovery candidate");
            Assert(!YMusic.Native.Program.CanRecoverInstalledUpdateLocator(null, paths.Executable, paths.LocalData,
                _ => throw new UnauthorizedAccessException("fixture current directory attributes")),
                "the deferred attribute proof accepted an unexamined external current target");
            foreach (var directory in new[] { paths.Install, paths.LocalData })
            {
                using var blockedRoot = UninstallCreateFile(UninstallPathGuard.ToWin32Path(directory), 0x80000000, 0,
                    IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
                Assert(!blockedRoot.IsInvalid, "the fixture could not hold the root containing the external current target");
                Assert(!YMusic.Native.Program.CanRecoverInstalledUpdateLocator(null, paths.Executable, paths.LocalData),
                    "the root fallback accepted an external current target");
            }
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
            UninstallSupport.ScheduleConsumedRetry(paths.LocalData, paths.Executable, Environment.ProcessId, () => true, specification =>
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
        await Run("Uninstall: a bounded receipt round trips long Unicode folder paths", () => Task.Run(() =>
        {
            var longSegment = new string('я', 110);
            var paths = UninstallFixture(Path.Combine(longSegment, longSegment, longSegment));
            var nonce = UninstallSupport.CreateReceipt(paths);
            var marker = Path.Combine(paths.Install, "full-uninstall.pending.json");
            Assert(new FileInfo(marker).Length > 4096, "the long-path fixture did not exercise the former reader limit");
            UninstallSupport.ValidateReceipt(paths, nonce);
            UninstallSupport.ScheduleNativeHandoffAfterCleanup(paths, nonce, Environment.ProcessId, _ => Process.GetCurrentProcess());
            Assert(UninstallSupport.IsReceiptConsumed(paths), "the long-path receipt was unreadable after recording completion");

            var shorter = UninstallFixture("receipt-path-too-long");
            var overLimit = Path.Combine(shorter.Documents, string.Join(Path.DirectorySeparatorChar, Enumerable.Repeat(new string('я', 200), 170)));
            RejectUninstall(() => UninstallSupport.CreateReceipt(UninstallPaths.FromKnownFolders(shorter.LocalData, overLimit)));
            Assert(!UninstallSupport.HasPendingReceipt(shorter), "an unsupported Windows path published a marker");
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
            Assert(launches == 1 && guardedAtLaunch && UninstallSupport.HasPendingReceipt(paths) && UninstallSupport.IsReceiptConsumed(paths),
                "handoff was blocked by expiry or failure lost the completed cleanup");
            RejectUninstall(() => UninstallSupport.ValidateReceipt(paths, admitted));
            Directory.Move(paths.Documents, paths.Documents + "-temporarily-unavailable");
            UninstallSupport.ScheduleConsumedRetry(paths.LocalData, paths.Executable, Environment.ProcessId,
                () => true, _ => { launches++; return Process.GetCurrentProcess(); });
            Assert(launches == 2 && UninstallSupport.IsReceiptConsumed(paths) && !UninstallSupport.CancelPendingReceipt(paths),
                "an admitted cleanup lost its startup guard during handoff");
        }));
        await Run("Uninstall: a backward clock step only blocks initial receipt admission", () => Task.Run(() =>
        {
            var paths = UninstallFixture("receipt-clock-step");
            var nonce = UninstallSupport.CreateReceipt(paths);
            UninstallSupport.ValidateReceipt(paths, nonce);
            var marker = Path.Combine(paths.Install, "full-uninstall.pending.json");
            var receipt = JsonNode.Parse(File.ReadAllText(marker))!.AsObject();
            receipt["CreatedUtc"] = JsonValue.Create(DateTimeOffset.UtcNow.AddHours(1));
            File.WriteAllText(marker, receipt.ToJsonString());
            RejectUninstall(() => UninstallSupport.ValidateReceipt(paths, nonce));
            var launches = 0;
            Process MockLaunch(ProcessStartInfo _) { launches++; return Process.GetCurrentProcess(); }
            RejectUninstall(() => UninstallSupport.ScheduleNativeHandoffAfterCleanup(paths, new string('A', 64), Environment.ProcessId, MockLaunch));
            UninstallSupport.ScheduleNativeHandoffAfterCleanup(paths, nonce, Environment.ProcessId, MockLaunch);
            Assert(launches == 1 && UninstallSupport.IsReceiptConsumed(paths), "previously admitted receipt could not finish after the clock shifted");
        }));
        await Run("Uninstall: a replaced Documents folder requires new consent and the original anchor can be retried", () => Task.Run(() =>
        {
            var paths = UninstallFixture("documents-file-identity");
            WriteUninstallAudio(paths, "admitted.mp3", "original fixture audio");
            var id = UninstallSupport.CaptureDocumentsIdentity(paths);
            var nonce = UninstallSupport.CreateReceipt(paths, id);
            var marker = Path.Combine(paths.Install, "full-uninstall.pending.json");
            var originalBytes = File.ReadAllText(marker);
            var oldMarker = JsonNode.Parse(originalBytes)!.AsObject();
            Assert(oldMarker["DocumentsIdentity"]!.Deserialize<UninstallFileIdentity>() == id && oldMarker.Remove("DocumentsIdentity"),
                "the receipt failed to bind the confirmed native directory identity");
            File.WriteAllText(marker, oldMarker.ToJsonString());
            RejectUninstall(() => UninstallSupport.ValidateReceipt(paths, nonce));
            File.WriteAllText(marker, originalBytes);
            UninstallSupport.ValidateReceipt(paths, nonce);

            var original = paths.Documents + "-original";
            var replacement = paths.Documents + "-replacement";
            Directory.Move(paths.Documents, original);
            WriteUninstallAudio(paths, "different.mp3", "replacement fixture audio");
            Directory.CreateDirectory(paths.Data);
            var settings = Path.Combine(paths.Data, "settings.json");
            File.WriteAllText(settings, """{"volume":0.25}""");
            var replacementId = UninstallSupport.CaptureDocumentsIdentity(paths);
            Assert(replacementId != id, "fixture did not replace the Documents object");
            RejectUninstall(() => UninstallSupport.CreateReceipt(paths, id));
            var denied = UninstallCleanup.Execute(paths, id);
            Assert(!denied.Complete && denied.RemovedFiles == 0 && File.Exists(settings)
                && File.Exists(Path.Combine(paths.Music, "different.mp3")) && File.ReadAllText(marker) == originalBytes,
                "cleanup accepted the replacement folder or rewrote the admitted request");

            try { Directory.Move(paths.Documents, replacement); }
            catch (IOException error) when ((error.HResult & 0xffff) is 5 or 32 or 33)
            {
                // A short-lived Windows handle can delay a fixture-only rename.
                Thread.Sleep(100);
                if (UninstallPathGuard.EntryExists(replacement) || UninstallSupport.CaptureDocumentsIdentity(paths) != replacementId) throw;
                Console.WriteLine($"NOTE Uninstall fixture: retrying replacement Documents move after Windows error {error.HResult & 0xffff}.");
                Directory.Move(paths.Documents, replacement);
            }
            Directory.Move(original, paths.Documents);
            var retry = UninstallCleanup.Execute(paths, id);
            Assert(retry.Complete && !Directory.Exists(paths.Music) && !File.Exists(settings)
                && File.Exists(Path.Combine(replacement, "Yamuse", "Music", "different.mp3")), "returning the original folder did not resume the admitted cleanup");
        }));
        await Run("Uninstall: unsupported Documents identity explains the filesystem requirement before any cleanup", () => Task.Run(() =>
        {
            var paths = UninstallFixture("documents-no-fileid-info");
            WriteUninstallAudio(paths, "saved.mp3", "fixture audio");
            Directory.CreateDirectory(paths.Data);
            var settings = Path.Combine(paths.Data, "settings.json");
            File.WriteAllText(settings, "fixture profile");
            UninstallFileIdentity Unsupported(SafeFileHandle _) => throw new System.ComponentModel.Win32Exception(87);
            var shown = false;
            try { UninstallSupport.CaptureDocumentsIdentity(paths, Unsupported); }
            catch (DocumentsIdentityNotSupportedException error) { shown = error.Message.Contains("NTFS", StringComparison.Ordinal); }
            Assert(shown && !UninstallSupport.HasPendingReceipt(paths), "unsupported FileIdInfo did not explain the requirement at confirmation");

            var admitted = UninstallSupport.CaptureDocumentsIdentity(paths);
            var result = UninstallCleanup.Execute(paths, admitted, Unsupported);
            Assert(!result.Complete && result.RemovedFiles == 0 && result.Issues.Any(issue => issue.Reason.Contains("NTFS", StringComparison.Ordinal))
                && File.ReadAllText(settings) == "fixture profile" && File.ReadAllText(Path.Combine(paths.Music, "saved.mp3")) == "fixture audio",
                "unsupported FileIdInfo did not block cleanup of the confirmed paths");
        }));
        await Run("Uninstall: updater is rechecked and held at both handoffs", () => Task.Run(() =>
        {
            var paths = UninstallFixture("handoff-validates-updater");
            var nonce = UninstallSupport.CreateReceipt(paths);
            var held = paths.Updater + ".held";
            var launches = 0;
            Process MockLaunch(ProcessStartInfo _)
            {
                launches++;
                try { using var writer = new FileStream(paths.Updater, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete); }
                catch (IOException error) when ((error.HResult & 0xffff) == 32) { return Process.GetCurrentProcess(); }
                throw new InvalidOperationException("the starter could open the checked updater for writing");
            }
            UninstallSupport.ValidateInstallation(paths.LocalData, paths.Executable);
            File.Move(paths.Updater, held);
            RejectUninstall(() => UninstallSupport.StartAfterExit(paths, Environment.ProcessId, paths.Executable,
                [UninstallSupport.CompleteArgument, nonce], MockLaunch));
            Assert(launches == 0, "first handoff bypassed the current updater check");
            File.Move(held, paths.Updater);
            UninstallSupport.StartAfterExit(paths, Environment.ProcessId, paths.Executable, [UninstallSupport.CompleteArgument, nonce], MockLaunch);
            using (new FileStream(paths.Updater, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete)) { }
            RejectUninstall(() => UninstallSupport.ScheduleNativeHandoffAfterCleanup(paths, nonce, Environment.ProcessId, MockLaunch,
                onCleanupRecorded: () => File.Move(paths.Updater, held)));
            Assert(launches == 1 && UninstallSupport.IsReceiptConsumed(paths), "failed final handoff started a waiter or lost the completed state");
        }));
        await Run("Uninstall: an active updater defers removal and leaves pending receipts untouched", () => Task.Run(() =>
        {
            var paths = UninstallFixture("updater-active-during-admission");
            var nonce = UninstallSupport.CreateReceipt(paths);
            var marker = Path.Combine(paths.Install, "full-uninstall.pending.json");
            var pending = File.ReadAllBytes(marker);
            var active = true;
            var probes = 0;
            bool Wait(IReadOnlyList<string> images, uint timeout)
            {
                Assert(images.SequenceEqual([paths.Updater]) && timeout > 0, "uninstall waited for the wrong image");
                probes++;
                return !active;
            }
            RejectUninstall(() => UninstallSupport.RequireUpdaterFinished(paths.Install, Wait));
            Assert(probes == 1 && File.ReadAllBytes(marker).SequenceEqual(pending), "updater contention changed the confirmed request");
            active = false;
            UninstallSupport.RequireUpdaterFinished(paths.Install, Wait);
            UninstallSupport.ValidateReceipt(paths, nonce);
            Assert(probes == 2 && File.ReadAllBytes(marker).SequenceEqual(pending), "the inactive updater still blocked the confirmed request");
        }));
        await Run("Uninstall: legacy staging blocks both native handoffs and preserves the consumed receipt", () => Task.Run(() =>
        {
            var paths = UninstallFixture("legacy-staging");
            var staging = Path.Combine(paths.Install, "staging", "app-0.3.10");
            Directory.CreateDirectory(staging);
            var launches = 0;
            Process? Launch(ProcessStartInfo _) { launches++; return Process.GetCurrentProcess(); }
            var registrations = 0;
            Assert(UninstallSupport.RegisterWindowsUninstall(() => paths.LocalData, paths.Executable, (install, _, wrapper) =>
            {
                Assert(install == paths.Install && wrapper.EndsWith(UninstallSupport.RequestArgument, StringComparison.Ordinal),
                    "registry repair did not select the checked wrapper");
                registrations++;
                return true;
            }) && registrations == 1, "legacy staging left the native Windows uninstall command in place");
            RejectUninstall(() => UninstallSupport.ValidateInstallation(paths.LocalData, paths.Executable));
            RejectUninstall(() => UninstallSupport.StartAfterExit(paths, Environment.ProcessId, paths.Executable,
                [UninstallSupport.CompleteArgument, new string('a', 64)], Launch));
            Directory.Delete(staging);
            UninstallSupport.ValidateInstallation(paths.LocalData, paths.Executable);
            var nonce = UninstallSupport.CreateReceipt(paths);
            RejectUninstall(() => UninstallSupport.ScheduleNativeHandoffAfterCleanup(paths, nonce, Environment.ProcessId,
                Launch, onCleanupRecorded: () => Directory.CreateDirectory(staging)));
            Assert(launches == 0 && UninstallSupport.IsReceiptConsumed(paths), "legacy staging reached a launch or lost the completed marker");
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

    private static UninstallPaths UninstallFixture(string name, bool withUpdater = true, bool createDocuments = true)
    {
        var root = Path.GetFullPath(Path.Combine(_directory, "uninstall", name + "-" + Guid.NewGuid().ToString("N")));
        Assert(root.StartsWith(Path.GetFullPath(_directory) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase), "fixture escaped results directory");
        var paths = UninstallPaths.FromKnownFolders(Path.Combine(root, "LocalAppData"), Path.Combine(root, "Documents"));
        Directory.CreateDirectory(paths.LocalData);
        if (createDocuments) Directory.CreateDirectory(paths.Documents);
        Directory.CreateDirectory(Path.GetDirectoryName(paths.Executable)!);
        File.WriteAllText(paths.Executable, "fake executable: never run");
        WriteUninstallManifest(paths.Executable);
        if (withUpdater) File.WriteAllText(paths.Updater, "fake updater: never run");
        return paths;
    }
    private static void WriteUninstallManifest(string executable, string id = "YMusic.Native")
        => File.WriteAllText(Path.Combine(Path.GetDirectoryName(executable)!, "sq.version"),
            $"<package><metadata><id>{id}</id><version>0.7.0</version></metadata></package>");
    private static void WriteUninstallAudio(UninstallPaths paths, string name, string content)
    {
        Directory.CreateDirectory(paths.Music);
        var bytes = Encoding.UTF8.GetBytes(content); File.WriteAllBytes(Path.Combine(paths.Music, name), bytes);
        var index = Path.Combine(paths.Music, "library.index.json");
        var rows = File.Exists(index) ? JsonSerializer.Deserialize<List<OfflineItem>>(File.ReadAllText(index))! : [];
        rows.RemoveAll(row => row.FileName == name);
        rows.Add(UninstallFixtureAudio(name, bytes));
        File.WriteAllText(index, JsonSerializer.Serialize(rows));
    }
    private static OfflineItem UninstallFixtureAudio(string fileName, byte[] bytes)
        => new(fileName, fileName, "Fixture track", "", "", 1000, fileName, "mp3", 320,
            bytes.Length, Convert.ToHexString(SHA256.HashData(bytes)), DateTimeOffset.UtcNow);

    private static void AddFixtureDirectoryExtendedAttribute(string directory)
    {
        const uint writeEaAndSynchronize = 0x00100010;
        const uint openDirectoryWithoutFollowingLinks = 0x02200000;
        const uint shareReadWriteDelete = 7;
        using var handle = UninstallCreateFile(UninstallPathGuard.ToWin32Path(directory), writeEaAndSynchronize,
            shareReadWriteDelete, IntPtr.Zero, 3, openDirectoryWithoutFollowingLinks, IntPtr.Zero);
        Assert(!handle.IsInvalid, "could not open the synthetic directory for EA setup");
        Cleanup015AddExtendedAttribute(handle);
    }

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
