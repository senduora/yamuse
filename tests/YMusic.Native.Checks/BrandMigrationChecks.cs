using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using Velopack;
using Velopack.Locators;
using Velopack.NuGet;
using YMusic.Native;

internal static partial class Program
{
    private static async Task CheckBrandMigration()
    {
        await Run("Brand migration: bootstrap keeps demo isolated and recognizes explicit Velopack and Yamuse commands", () => Task.Run(() =>
        {
            Assert(YMusic.Native.Program.GetStartupMode([]) == YMusic.Native.Program.StartupMode.Normal
                && YMusic.Native.Program.GetStartupMode(["--login"]) == YMusic.Native.Program.StartupMode.Normal,
                "ordinary startup or login was routed as a lifecycle command");
            Assert(YMusic.Native.Program.GetStartupMode(["--DeMo", "--capture", "preview.png"]) == YMusic.Native.Program.StartupMode.Demo,
                "demo was admitted as ordinary startup");
            Assert(YMusic.Native.Program.GetStartupMode(["--demo", "--uninstall"]) == YMusic.Native.Program.StartupMode.Uninstall
                && YMusic.Native.Program.GetStartupMode(["--complete-full-uninstall", "fixture"]) == YMusic.Native.Program.StartupMode.Uninstall,
                "demo or normal startup displaced an explicit Yamuse uninstall command");
            foreach (var flag in new[] { "--veloapp-install", "--VELOAPP-UPDATED", "--veloapp-obsolete", "--veloapp-uninstall",
                "--squirrel-install", "--squirrel-updated", "--squirrel-obsolete", "--squirrel-uninstall" })
            {
                Assert(YMusic.Native.Program.GetStartupMode([flag, "0.6.2", "--demo", "--uninstall"]) == YMusic.Native.Program.StartupMode.VelopackFastHook,
                    "fast SDK callback did not have priority: " + flag);
                Assert(YMusic.Native.Program.GetStartupMode([flag]) == YMusic.Native.Program.StartupMode.Normal
                    && YMusic.Native.Program.GetStartupMode(["--demo", flag, "0.6.2"]) == YMusic.Native.Program.StartupMode.Demo,
                    "SDK fast flags were accepted in an unsupported position: " + flag);
            }
        }));
        await Run("Brand migration: startup checks the marker under the mutex and releases the secondary handle before notification", () => Task.Run(() =>
        {
            var paths = BrandFixture("startup-admission");
            var marker = Path.Combine(paths.LocalData, "fake-uninstall.pending");
            var mutexName = @"Local\Yamuse-startup-fixture-" + Guid.NewGuid().ToString("N");
            var profileName = @"Global\Yamuse-profile-fixture-" + Guid.NewGuid().ToString("N");
            var probes = 0;
            bool BlocksStartup()
            {
                using var observer = new Mutex(false, mutexName, out var created);
                Assert(!created, "startup checked the marker before acquiring the mutex");
                probes++;
                return File.Exists(marker);
            }
            void UnexpectedNotification() => throw new InvalidOperationException("a closed instance still kept a mutex handle open");

            using var primary = App.TryBeginNormalStartup(BlocksStartup, UnexpectedNotification, mutexName, profileName);
            Assert(primary is not null && probes == 1, "startup failed without a pending marker");
            using var secondary = App.TryBeginNormalStartup(BlocksStartup, () =>
            {
                File.WriteAllText(marker, "fixture only");
                primary!.Dispose();
                using var duringNotification = App.TryBeginNormalStartup(BlocksStartup, UnexpectedNotification, mutexName, profileName);
                Assert(duringNotification is null && probes == 2, "startup ignored a marker written before the previous owner exited");
            }, mutexName, profileName);
            Assert(secondary is null && File.Exists(marker), "the secondary startup passed admission or changed the marker");

            File.Delete(marker);
            using var next = App.TryBeginNormalStartup(BlocksStartup, UnexpectedNotification, mutexName, profileName);
            Assert(next is not null && probes == 3, "an interrupted startup blocked the next attempt");
        }));
        await Run("Brand migration: an unavailable installer cache still admits the normal window", () =>
        {
            var calls = 0;
            void BlockedCache()
            {
                calls++;
                throw new IOException("fixture package directory is unavailable");
            }
            Assert(YMusic.Native.Program.ContinueToWindow(YMusic.Native.Program.StartupMode.Normal, () => true, BlockedCache, out var normalFailure)
                && normalFailure && calls == 1,
                "a package cache I/O error blocked the normal startup");
            bool UnexpectedWait() => throw new InvalidOperationException("a fast hook or demo checked the updater");
            Assert(!YMusic.Native.Program.ContinueToWindow(YMusic.Native.Program.StartupMode.VelopackFastHook, UnexpectedWait, BlockedCache, out var hookFailure)
                && hookFailure && calls == 2, "an unavailable installer lifecycle started the normal window or reported success");
            Assert(YMusic.Native.Program.ContinueToWindow(YMusic.Native.Program.StartupMode.Demo, UnexpectedWait, BlockedCache, out var demoFailure)
                && !demoFailure && calls == 2,
                "demo touched the installer cache");
            return Task.CompletedTask;
        });
        await Run("Brand migration: the after-update hook repairs registration with an unavailable installer cache", () =>
        {
            var registrations = 0;
            void RunWithBlockedCache(string flag, string version)
            {
                Assert(!YMusic.Native.Program.ContinueToWindow(YMusic.Native.Program.StartupMode.VelopackFastHook,
                    () => throw new InvalidOperationException("the updater's own hook waited for the updater"),
                    () => YMusic.Native.Program.CreateStartupLocator([flag, version],
                        () => throw new IOException("fixture package directory is unavailable"), () => registrations++), out var failed)
                    && failed, "the hook cache failure started the ordinary window or reported success");
            }
            RunWithBlockedCache("--VeloApp-Updated", "0.7.0");
            Assert(registrations == 1, "the after-update hook skipped registration repair");
            RunWithBlockedCache("--veloapp-updated", "malformed");
            RunWithBlockedCache("--squirrel-updated", "0.7.0");
            Assert(registrations == 1, "registration repair ran for an argument the SDK does not dispatch");
            return Task.CompletedTask;
        });
        await Run("Brand migration: installed update checking recovers in the same process after a cache failure", async () =>
        {
            var paths = UninstallFixture("recover-update-cache");
            var cache = Path.Combine(paths.Install, "packages");
            File.WriteAllText(cache, "blocked package directory");
            Assert(UninstallSupport.HasInstalledUpdateLayout(paths.Executable), "temporary cache access failure masked a checked installation");
            var feed = Path.Combine(paths.Install, "fixture-update-feed.json");
            File.WriteAllText(feed, """{"githubRepository":"https://github.com/senduora/yamuse"}""");
            var backend = new Su016Backend();
            var attempts = 0;
            var service = new UpdateSupport(feed,
                portableCheck: _ => throw new InvalidOperationException("known installation fell back to portable metadata"),
                recoveryBackend: _ =>
                {
                    attempts++;
                    if (!Directory.Exists(cache)) throw new IOException("fixture cache is inaccessible");
                    return backend;
                });
            Assert((await service.CheckAsync()).Status == AppUpdateStatus.Error && service.State.IsInstalled
                && attempts == 1 && backend.Checks == 0, "initial failure did not preserve the installed candidate");
            File.Delete(cache);
            Directory.CreateDirectory(cache);
            Assert((await service.CheckAsync()).Status == AppUpdateStatus.Available && service.State.IsInstalled
                && attempts == 2 && backend.Checks == 1 && backend.Downloads == 0 && backend.Applies == 0,
                "manual retry did not recreate the installed backend in the admitted process");
            File.WriteAllText(Path.Combine(paths.Install, ".portable"), "");
            Assert(!UninstallSupport.HasInstalledUpdateLayout(paths.Executable), "startup recovery accepted a portable layout");
        });
        foreach (var absent in new[] { false, true })
        await Run("Brand migration: an incomplete SDK locator recovers after " + (absent ? "the manifest reappears" : "reopening the installed manifest"), async () =>
        {
            var paths = UninstallFixture("recover-update-manifest");
            var manifest = Path.Combine(Path.GetDirectoryName(paths.Executable)!, "sq.version");
            var parked = manifest + ".fixture-unavailable";
            var feed = Path.Combine(paths.Install, "fixture-update-feed.json");
            File.WriteAllText(feed, """{"githubRepository":"https://github.com/senduora/yamuse"}""");
            var backend = new Su016Backend();
            var rebuilt = 0;
            UpdateSupport service;
            if (absent) File.Move(manifest, parked);
            try
            {
                using var blocked = absent ? null : new FileStream(manifest, FileMode.Open, FileAccess.Read, FileShare.None);
                // This is the parser WindowsVelopackLocator uses. Avoid its
                // constructor, which would create a log in the live profile.
                Assert(!PackageManifest.TryParseFromFile(manifest, out _), "the SDK fixture did not lose the installed version");
                var incomplete = new UnversionedFixtureLocator(paths.Install);
                Assert(YMusic.Native.Program.CanRecoverInstalledUpdateLocator(incomplete, paths.Executable),
                    "a locator without a version erased the fixed candidate");
                Assert(!UninstallSupport.HasInstalledUpdateLayout(paths.Executable), "an unreadable manifest supplied installation proof");
                service = new UpdateSupport(feed,
                    portableCheck: _ => throw new InvalidOperationException("known installation used the portable release check"),
                    recoveryBackend: _ =>
                    {
                        var restored = YMusic.Native.Program.CreateRecoveredUpdateLocator(paths.Executable, () =>
                        {
                            rebuilt++;
                            if (!PackageManifest.TryParseFromFile(manifest, out var parsed))
                                throw new IOException("fixture SDK failed to read the restored manifest");
                            return BrandFixtureInstallLocator(paths, parsed.Id!, parsed.Version!.ToString());
                        });
                        Assert(restored.AppId == BrandPaths.LegacyPackageId && restored.CurrentlyInstalledVersion?.ToString() == "0.7.0",
                            "recovery accepted a mismatched SDK locator");
                        return backend;
                    });
                Assert((await service.CheckAsync()).Status == AppUpdateStatus.Error && service.State.IsInstalled && rebuilt == 0,
                    "a retry could use a locator before authenticating its manifest");
            }
            finally { if (absent) File.Move(parked, manifest); }
            var original = File.ReadAllBytes(manifest);
            WriteUninstallManifest(paths.Executable, "Other.App");
            try
            {
                Assert((await service.CheckAsync()).Status == AppUpdateStatus.Error && rebuilt == 0,
                    "the recovery candidate authorized an unrelated package manifest");
            }
            finally { File.WriteAllBytes(manifest, original); }
            Assert((await service.CheckAsync()).Status == AppUpdateStatus.Available && rebuilt == 1
                && backend.Checks == 1 && backend.Downloads == 0 && backend.Applies == 0, "the process did not recover installed update checking");
            File.WriteAllText(Path.Combine(paths.Install, ".portable"), "");
            Assert(!YMusic.Native.Program.CanRecoverInstalledUpdateLocator(new UnversionedFixtureLocator(paths.Install), paths.Executable),
                "the recovery candidate accepted a portable application");
        });
        await Run("Brand migration: installed update checking recovers after the primary updater returns", async () =>
        {
            var paths = UninstallFixture("recover-primary-updater");
            var parked = paths.Updater + ".fixture-unavailable";
            var feed = Path.Combine(paths.Install, "fixture-update-feed.json");
            File.WriteAllText(feed, """{"githubRepository":"https://github.com/senduora/yamuse"}""");
            var incomplete = new UnversionedFixtureLocator(paths.Install);
            var backend = new Su016Backend();
            var locators = 0;
            UpdateSupport service;
            File.Move(paths.Updater, parked);
            try
            {
                Assert(YMusic.Native.Program.StartupUpdaterImages(paths.Executable, paths.LocalData).SequenceEqual([paths.Updater]),
                    "the startup updater guard lost the exact image when its file was temporarily unavailable");
                var manifest = Path.Combine(Path.GetDirectoryName(paths.Executable)!, "sq.version");
                var original = File.ReadAllBytes(manifest);
                WriteUninstallManifest(paths.Executable, "Other.App");
                try { Assert(!YMusic.Native.Program.CanRecoverInstalledUpdateLocator(incomplete, paths.Executable), "the candidate accepted a different package"); }
                finally { File.WriteAllBytes(manifest, original); }

                var canRecover = YMusic.Native.Program.CanRecoverInstalledUpdateLocator(incomplete, paths.Executable);
                Assert(canRecover, "the missing primary updater made a checked installation portable");
                service = new UpdateSupport(feed,
                    portableCheck: _ => throw new InvalidOperationException("known installation used portable metadata"),
                    recoveryBackend: canRecover ? _ =>
                    {
                        YMusic.Native.Program.CreateRecoveredUpdateLocator(paths.Executable, () =>
                        {
                            locators++;
                            return BrandFixtureInstallLocator(paths);
                        });
                        return backend;
                    } : null);
                Assert((await service.CheckAsync()).Status == AppUpdateStatus.Error && service.State.IsInstalled
                    && locators == 0 && backend.Checks == 0, "a missing updater supplied full backend authority");
            }
            finally { File.Move(parked, paths.Updater); }
            Assert((await service.CheckAsync()).Status == AppUpdateStatus.Available && service.State.IsInstalled
                && locators == 1 && backend.Checks == 1 && backend.Downloads == 0 && backend.Applies == 0,
                "the original updater did not restore built-in checking in the same process");
        });
        foreach (var (condition, lockImage, blockedDirectoryName) in new (string, bool, string?)[]
        {
            ("both installation files return", false, null),
            ("the image and manifest unlock", true, null),
            ("the current directory and manifest unlock", false, "current"),
            ("the managed current attributes and manifest return", false, "attributes"),
            ("the installation root and manifest unlock", false, "root"),
            ("the installation ancestor and manifest unlock", false, "parent")
        })
        await Run("Brand migration: the fixed installation retries updates after " + condition, async () =>
        {
            var paths = UninstallFixture("recover-updater-and-manifest");
            var feed = Path.Combine(paths.Install, "fixture-update-feed.json");
            File.WriteAllText(feed, """{"githubRepository":"https://github.com/senduora/yamuse"}""");
            var incomplete = new UnversionedFixtureLocator(paths.Install);
            var current = Path.GetDirectoryName(paths.Executable)!;
            var manifest = Path.Combine(current, "sq.version");
            var parkedUpdater = paths.Updater + ".fixture-unavailable";
            var portable = Path.Combine(paths.Install, ".portable");
            var backend = new Su016Backend();
            var locators = 0;
            var denyAttributes = blockedDirectoryName == "attributes";
            FileAttributes ReadCurrentAttributes(string path)
            {
                Assert(path == current, "the startup probe inspected a different content directory");
                return denyAttributes ? throw new UnauthorizedAccessException("fixture current directory attributes") : File.GetAttributes(path);
            }
            UpdateSupport service;
            if (!lockImage && blockedDirectoryName is null) File.Move(paths.Updater, parkedUpdater);
            try
            {
                using var blockedManifest = new FileStream(manifest, FileMode.Open, FileAccess.Read, FileShare.None);
                using var blockedImage = lockImage ? new FileStream(paths.Executable, FileMode.Open, FileAccess.Read, FileShare.None) : null;
                var directory = blockedDirectoryName switch { "parent" => paths.LocalData, "root" => paths.Install, _ => current };
                using var blockedDirectory = blockedDirectoryName is "parent" or "root" or "current" ? HoldBrandFixtureDirectory(directory) : null;
                if (blockedDirectory is not null)
                {
                    var sharingDenied = false;
                    try { using var other = UninstallPathGuard.HoldDirectory(directory); }
                    catch (Win32Exception error) when (error.NativeErrorCode is 32 or 33) { sharingDenied = true; }
                    Assert(sharingDenied, "the fixture directory handle did not deny a guarded read");
                    if (blockedDirectoryName != "current")
                        Assert(!UninstallSupport.HasStartupMarker(paths.LocalData), "startup could not see the absence of its marker");
                }
                Assert(!YMusic.Native.Program.CanRecoverInstalledUpdateLocator(incomplete, paths.Executable, paths.LocalData + "-other", ReadCurrentAttributes),
                    "the unavailable files gave an arbitrary directory installed status");
                File.WriteAllText(portable, "");
                try
                {
                    Assert(!YMusic.Native.Program.CanRecoverInstalledUpdateLocator(incomplete, paths.Executable, paths.LocalData, ReadCurrentAttributes),
                        "the fixed portable layout became an installed candidate");
                }
                finally { File.Delete(portable); }

                var canRetry = YMusic.Native.Program.CanRecoverInstalledUpdateLocator(incomplete, paths.Executable, paths.LocalData, ReadCurrentAttributes);
                Assert(canRetry, "the fixed checked image lost its retry when both installation files were unavailable");
                service = new UpdateSupport(feed,
                    portableCheck: _ => throw new InvalidOperationException("known installation used portable metadata"),
                    recoveryBackend: canRetry ? _ =>
                    {
                        YMusic.Native.Program.CreateRecoveredUpdateLocator(paths.Executable, () =>
                        {
                            locators++;
                            return BrandFixtureInstallLocator(paths);
                        });
                        return backend;
                    } : null);
                Assert((await service.CheckAsync()).Status == AppUpdateStatus.Error && locators == 0,
                    "a checked image created a backend without proof of the whole installation");
                if (blockedImage is not null) blockedImage.Dispose();
                else if (blockedDirectory is not null) blockedDirectory.Dispose();
                else if (denyAttributes) denyAttributes = false;
                else File.Move(parkedUpdater, paths.Updater);
                Assert((await service.CheckAsync()).Status == AppUpdateStatus.Error && locators == 0,
                    "restoring one path authorized a backend before the manifest returned");
            }
            finally { if (File.Exists(parkedUpdater)) File.Move(parkedUpdater, paths.Updater); }
            Assert((await service.CheckAsync()).Status == AppUpdateStatus.Available && service.State.IsInstalled
                && locators == 1 && backend.Checks == 1 && backend.Downloads == 0 && backend.Applies == 0,
                "the same update service could not recover after both files returned");
        });
        await Run("Brand migration: a versioned per-user locator recovers after a failed updater copy", async () =>
        {
            var paths = UninstallFixture("versioned-fallback-recovery");
            var content = Path.GetDirectoryName(paths.Executable)!;
            var local = Path.Combine(paths.LocalData, "fixture-other-local-data");
            var fallback = Path.Combine(local, BrandPaths.LegacyPackageId);
            var packages = Path.Combine(fallback, "packages");
            var updater = Path.Combine(fallback, "Update.exe");
            Directory.CreateDirectory(packages);
            var stale = new TestVelopackLocator(BrandPaths.LegacyPackageId, "0.7.0", packages, content, paths.Install, updater);
            var bad = new TestVelopackLocator(BrandPaths.LegacyPackageId, "0.7.0", Path.Combine(paths.Install, "packages"), content, paths.Install, updater);
            var feed = Path.Combine(paths.Install, "fixture-update-feed.json");
            File.WriteAllText(feed, """{"githubRepository":"https://github.com/senduora/yamuse"}""");
            var probes = 0;
            var backends = 0;
            var backend = new Su016Backend();
            UpdateSupport service;
            using (var unavailable = new FileStream(paths.Updater, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                Assert(!YMusic.Native.Program.CanUseInstalledUpdateLocator(stale, executable: paths.Executable, localDataFolder: () => local)
                    && !YMusic.Native.Program.CanRecoverInstalledUpdateLocator(bad, paths.Executable, local),
                    "an absent per-user updater or mismatched cache supplied authority");
                var canRecover = YMusic.Native.Program.CanRecoverInstalledUpdateLocator(stale, paths.Executable, local);
                Assert(canRecover, "a versioned locator with a missing exact per-user updater lost its retry");
                service = new UpdateSupport(feed,
                    portableCheck: _ => throw new InvalidOperationException("known alternative installation used portable metadata"),
                    recoveryBackend: canRecover ? _ =>
                    {
                        var refreshed = YMusic.Native.Program.CreateRecoveredUpdateLocator(paths.Executable, () =>
                        {
                            probes++;
                            return new TestVelopackLocator(BrandPaths.LegacyPackageId, "0.7.0", packages, content, paths.Install, updater);
                        }, () => local);
                        Assert(refreshed.RootAppDir == paths.Install && refreshed.PackagesDir == packages && refreshed.UpdateExePath == updater,
                            "the recovered per-user locator changed the original installation or exact cache");
                        backends++;
                        return backend;
                    } : null);
                Assert((await service.CheckAsync()).Status == AppUpdateStatus.Error && probes == 0 && backends == 0,
                    "the missing copy bypassed the inaccessible primary updater");
            }
            File.Copy(paths.Updater, updater); // Simulate the SDK's successful copy after its first attempt failed.
            using (var unavailable = new FileStream(updater, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                Assert((await service.CheckAsync()).Status == AppUpdateStatus.Error && probes == 1 && backends == 0,
                    "a refreshed versioned locator authorized an inaccessible per-user updater");
            }
            Assert((await service.CheckAsync()).Status == AppUpdateStatus.Available && probes == 2 && backends == 1
                && backend.Checks == 1 && backend.Downloads == 0 && backend.Applies == 0,
                "the same service did not recover built-in updates with the checked per-user updater");
        });
        await Run("Brand migration: normal update admission distinguishes denied portable metadata from confirmed absence", async () =>
        {
            foreach (var portable in new[] { true, false })
            {
                var paths = UninstallFixture("portable-marker-admission");
                var marker = Path.Combine(paths.Install, ".portable");
                if (portable) File.WriteAllText(marker, "fixture portable");
                var feed = Path.Combine(paths.Install, "fixture-update-feed.json");
                File.WriteAllText(feed, """{"githubRepository":"https://github.com/senduora/yamuse"}""");
                var locator = new TestVelopackLocator(BrandPaths.LegacyPackageId, "0.7.0", Path.Combine(paths.Install, "packages"),
                    Path.GetDirectoryName(paths.Executable), paths.Install, paths.Updater);
                Assert(!locator.IsPortable, "fixture must reproduce the optimistic SDK flag");
                var denied = true;
                var created = 0;
                var portableChecks = 0;
                var backend = new Su016Backend();
                bool ProbeMarker(string path)
                {
                    Assert(path == marker, "normal admission checked another marker");
                    if (denied) throw new UnauthorizedAccessException("fixture denial querying marker attributes");
                    return UninstallPathGuard.EntryExists(path);
                }
                var service = new UpdateSupport(feed,
                    backendFactory: _ => { created++; return backend; },
                    portableCheck: _ => { portableChecks++; return Task.FromResult<string?>("9.1.0"); },
                    installedAdmission: () => YMusic.Native.Program.CanUseInstalledUpdateLocator(locator, ProbeMarker, paths.Executable));
                Assert(!service.State.IsInstalled && !(await service.CheckAsync()).IsInstalled && created == 0 && portableChecks == 1,
                    "an unreadable .portable marker authorized creating an installer backend");
                denied = false;
                var recovered = await service.CheckAsync();
                Assert(recovered.Status == AppUpdateStatus.Available && recovered.IsInstalled != portable
                    && created == (portable ? 0 : 1) && backend.Checks == (portable ? 0 : 1) && portableChecks == (portable ? 2 : 1),
                    "the same service did not respect the confirmed marker status");
            }
        });
        await Run("Brand migration: normal backend admission rechecks installed files after marker recovery", async () =>
        {
            foreach (var usingFallback in new[] { false, true })
            {
                var paths = UninstallFixture(usingFallback ? "normal-fallback-admission" : "normal-files-admission");
                var current = Path.GetDirectoryName(paths.Executable)!;
                var manifest = Path.Combine(current, "sq.version");
                var fallbackLocalData = Path.Combine(paths.LocalData, "fixture-fallback-local-data");
                var fallback = Path.Combine(fallbackLocalData, BrandPaths.LegacyPackageId);
                var cache = Path.Combine(usingFallback ? fallback : paths.Install, "packages");
                var updater = usingFallback ? Path.Combine(fallback, "Update.exe") : paths.Updater;
                if (usingFallback)
                {
                    Directory.CreateDirectory(cache);
                    File.WriteAllText(updater, "fixture updater: never run");
                    File.Delete(paths.Updater);
                }
                var locator = new TestVelopackLocator(BrandPaths.LegacyPackageId, "0.7.0", cache, current, paths.Install, updater);
                var wrongCache = new TestVelopackLocator(BrandPaths.LegacyPackageId, "0.7.0", Path.Combine(paths.Install, "packages"),
                    current, paths.Install, updater);
                var markerDenied = true;
                var created = 0;
                var backend = new Su016Backend();
                var feed = Path.Combine(paths.Install, "fixture-update-feed.json");
                File.WriteAllText(feed, """{"githubRepository":"https://github.com/senduora/yamuse"}""");
                var service = new UpdateSupport(feed,
                    backendFactory: _ => { created++; return backend; }, portableCheck: _ => Task.FromResult<string?>(null),
                    installedAdmission: () => YMusic.Native.Program.CanUseInstalledUpdateLocator(locator,
                        marker => markerDenied ? throw new UnauthorizedAccessException("fixture marker attributes") : UninstallPathGuard.EntryExists(marker),
                        paths.Executable, () => fallbackLocalData));
                Assert(!service.State.IsInstalled, "the initial marker refusal authorized an installed backend");
                markerDenied = false;
                foreach (var file in new[] { updater, manifest })
                {
                    using var held = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.None);
                    Assert(!(await service.CheckAsync()).IsInstalled && created == 0 && backend.Checks == 0,
                        "the unavailable updater or manifest authorized an installed backend");
                }
                if (usingFallback)
                    Assert(!YMusic.Native.Program.CanUseInstalledUpdateLocator(wrongCache, executable: paths.Executable,
                        localDataFolder: () => fallbackLocalData), "an unrelated cache authorized a per-user updater");
                Assert((await service.CheckAsync()) is { IsInstalled: true, Status: AppUpdateStatus.Available }
                    && created == 1 && backend.Checks == 1 && backend.Applies == 0,
                    "the same service did not recover the precise installed updater and manifest");
            }
        });
        await Run("Brand migration: startup waits only for the exact updater image of this installation", () =>
        {
            var image = Environment.ProcessPath!;
            var other = Path.Combine(_directory, "other-installation", Path.GetFileName(image));
            Assert(!YMusic.Native.Program.WaitForUpdaterExit([image], waitMilliseconds: 0),
                "an active, exact worker did not block startup");
            Assert(!YMusic.Native.Program.WaitForUpdaterExit([@"\\?\" + image], waitMilliseconds: 0),
                "an alternate Win32 spelling of the same image did not block startup");
            Assert(YMusic.Native.Program.WaitForUpdaterExit([other], waitMilliseconds: 0),
                "an unrelated image with the same basename blocked startup");
            var locatorCalled = false;
            Assert(!YMusic.Native.Program.ContinueToWindow(YMusic.Native.Program.StartupMode.Normal, () => false,
                () => { locatorCalled = true; throw new IOException("fixture cache is blocked"); }, out var failed) && !failed && !locatorCalled,
                "the normal window started while the updater was still active");

            var profile = BrandFixture("startup-updater-images");
            var install = Path.Combine(profile.LocalData, BrandPaths.LegacyPackageId);
            var app = Path.Combine(install, "current", BrandPaths.LegacyPackageId + ".exe");
            Directory.CreateDirectory(install);
            File.WriteAllText(Path.Combine(install, "packages"), "fixture cache is unavailable as a directory");
            Assert(YMusic.Native.Program.StartupUpdaterImages(app, profile.LocalData).SequenceEqual([Path.Combine(install, "Update.exe")]),
                "the updater probe depended on the installer cache");
            var shortImage = Path.Combine(profile.LocalData, "YMUSIC~1", "current", "YMUSIC~1.EXE");
            Assert(YMusic.Native.Program.StartupUpdaterImages(shortImage, profile.LocalData,
                imagePath => imagePath == shortImage ? app : imagePath).SequenceEqual([Path.Combine(install, "Update.exe")]),
                "startup inferred the updater path before expanding a short executable path");
            File.WriteAllText(Path.Combine(install, ".portable"), "");
            Assert(YMusic.Native.Program.StartupUpdaterImages(app, profile.LocalData).Length == 0,
                "portable startup tried to wait for an installed updater");
            var alternateInstall = Path.Combine(profile.LocalData, "readonly-install");
            var alternateApp = Path.Combine(alternateInstall, "current", BrandPaths.LegacyPackageId + ".exe");
            Assert(YMusic.Native.Program.StartupUpdaterImages(alternateApp, profile.LocalData)
                .SequenceEqual([Path.Combine(alternateInstall, "Update.exe"), Path.Combine(install, "Update.exe")]),
                "a read-only installation lost its primary or per-user image candidate");
            return Task.CompletedTask;
        });
        await Run("Brand migration: automatic diagnostics publishes only under its checked profile directory", () =>
        {
            var fixture = BrandFixture("automatic-diagnostics");
            var destination = Path.Combine(fixture.DataDirectory, "diagnostics");
            Directory.CreateDirectory(destination);
            var existing = Path.Combine(destination, "session-existing.json");
            File.WriteAllText(existing, "keep this report");
            using var diagnostics = new Diagnostics();
            var report = diagnostics.SaveAuto(fixture.DataDirectory);
            Assert(Path.GetDirectoryName(report) == destination && report != existing
                && File.ReadAllText(existing) == "keep this report", "automatic report overwrote an existing report or used another directory");
            using (var json = JsonDocument.Parse(File.ReadAllBytes(report)))
                Assert(json.RootElement.GetProperty("schema").GetInt32() == 1, "automatic report was not completed");
            Assert(diagnostics.SaveAuto(fixture.DataDirectory) != report && File.Exists(report), "another automatic report reused the first name");

            var separateProfile = Path.Combine(fixture.LocalData, "other-profile");
            Directory.CreateDirectory(separateProfile);
            CreateUninstallJunction(Path.Combine(separateProfile, "diagnostics"), destination);
            var count = Directory.EnumerateFiles(destination).Count();
            RejectUninstall(() => diagnostics.SaveAuto(separateProfile));
            Assert(Directory.EnumerateFiles(destination).Count() == count, "automatic report followed a redirected profile directory");
            return Task.CompletedTask;
        });
        await Run("Brand migration: rejected explicit diagnostics replacement leaves the original report intact", () =>
        {
            var fixture = BrandFixture("explicit-diagnostics");
            var destination = Path.Combine(fixture.Documents, "report.json");
            File.WriteAllText(destination, "existing report");
            using var diagnostics = new Diagnostics();
            using (var held = new FileStream(destination, FileMode.Open, FileAccess.Read, FileShare.Read))
                RejectUninstall(() => diagnostics.Save(destination, () => true));
            Assert(File.ReadAllText(destination) == "existing report" && Directory.EnumerateFiles(fixture.Documents).Count() == 1,
                "failed export changed the existing report or left a staging file");
            Assert(diagnostics.Save(destination, () => true), "the user-approved report replacement was skipped");
            using var json = JsonDocument.Parse(File.ReadAllBytes(destination));
            Assert(json.RootElement.GetProperty("schema").GetInt32() == 1, "explicit export did not replace the entire report");
            return Task.CompletedTask;
        });
        await Run("Brand migration: profile admission spans Windows sessions while retaining the legacy mutex", () => Task.Run(() =>
        {
            var fixture = Guid.NewGuid().ToString("N");
            var primaryName = @"Local\Yamuse-profile-primary-" + fixture;
            var secondaryName = @"Local\Yamuse-profile-secondary-" + fixture;
            var profileName = @"Global\Yamuse-profile-fixture-" + fixture;
            using (var first = App.TryAcquireProfileInstance(primaryName, profileName))
            {
                Assert(first is not null, "the first profile instance was refused");
                using var legacy = new Mutex(false, primaryName, out var canRunLegacy);
                Assert(!canRunLegacy, "this session no longer excludes a legacy Yamuse instance");
                using var second = App.TryAcquireProfileInstance(secondaryName, profileName);
                Assert(second is null, "different session-local names admitted two writers to the same profile");
                using var independent = new Mutex(false, secondaryName, out var localReleased);
                Assert(localReleased, "a refused launch retained its session-local mutex");
            }
            using var next = App.TryAcquireProfileInstance(secondaryName, profileName);
            Assert(next is not null, "the profile remained blocked after the first lease was released");
        }));
        await Run("Brand migration: fresh install uses Yamuse paths and creates no profile files", () => Task.Run(() =>
        {
            var paths = BrandFixture("fresh");
            Assert(paths.DataDirectory == Path.Combine(paths.LocalData, "Yamuse")
                && paths.MusicDirectory == Path.Combine(paths.Documents, "Yamuse", "Music"), "brand paths differ");
            paths.MigrateLegacyData(_ => throw new InvalidOperationException("fresh launch attempted a move"));
            Assert(!Directory.Exists(paths.DataDirectory) && !Directory.Exists(paths.MusicDirectory)
                && Directory.EnumerateFileSystemEntries(paths.LocalData).Count() == 0
                && Directory.EnumerateFileSystemEntries(paths.Documents).Count() == 0, "fresh migration created data");
            foreach (var bad in new[] { "", ".", "C:\\", @"\\server\share", @"\\?\C:\data", "C:\\x:ads" })
                RejectUninstall(() => BrandPaths.FromKnownFolders(bad, paths.Documents));
            RejectUninstall(() => BrandPaths.FromKnownFolders(paths.LocalData, Path.Combine(paths.LocalData, "nested")));
        }));
        await Run("Brand migration: a separate volume can anchor Documents without becoming a deletion target", () =>
        {
            var fixture = BrandFixture("documents-volume-root");
            var currentRoot = Path.GetPathRoot(fixture.LocalData)!;
            var otherRoot = currentRoot.StartsWith("D:", StringComparison.OrdinalIgnoreCase) ? @"E:\" : @"D:\";
            var paths = BrandPaths.FromKnownFolders(fixture.LocalData, otherRoot);
            var uninstall = UninstallPaths.FromKnownFolders(fixture.LocalData, otherRoot, root =>
            {
                Assert(root == otherRoot, "the separate volume check queried the wrong drive");
                return DriveType.Fixed;
            });
            Assert(paths.MusicDirectory == Path.Combine(otherRoot, "Yamuse", "Music")
                && uninstall.Music == paths.MusicDirectory && uninstall.LegacyMusic == paths.LegacyMusicDirectory,
                "the separate volume did not anchor fixed private music folders");
            RejectUninstall(() => UninstallPathGuard.Canonical(otherRoot));
            RejectUninstall(() => BrandPaths.FromKnownFolders(fixture.LocalData, currentRoot));
            return Task.CompletedTask;
        });
        await Run("Brand migration: unavailable Documents still migrates the local profile and disables offline I/O", async () =>
        {
            var fixture = BrandFixture("documents-unavailable");
            WriteBrandLegacy(fixture);
            new SessionStore(fixture.LegacyDataDirectory).Save(FakeSession);
            var local = BrandPaths.FromStartupKnownFolders(fixture.LocalData, fixture.Documents);
            Assert(local.AvailableMusicDirectory == fixture.MusicDirectory, "accessible local Documents was rejected");
            var missing = Path.Combine(fixture.Documents, "missing");
            Assert(BrandPaths.FromStartupKnownFolders(fixture.LocalData, missing).AvailableMusicDirectory is null && !Directory.Exists(missing),
                "startup admitted or created missing Documents");
            var redirected = Path.Combine(Path.GetDirectoryName(fixture.Documents)!, "redirected-documents");
            CreateUninstallJunction(redirected, fixture.Documents);
            Assert(BrandPaths.FromStartupKnownFolders(fixture.LocalData, redirected).AvailableMusicDirectory is null,
                "startup admitted linked Documents");
            var paths = BrandPaths.FromStartupKnownFolders(fixture.LocalData, @"\\yamuse-fixture.invalid\documents");
            Assert(paths.AvailableMusicDirectory is null, "UNC Documents was admitted as a local store");
            paths.MigrateLegacyData();
            Assert(new SessionStore(paths.DataDirectory).Load() == FakeSession && File.Exists(Path.Combine(fixture.LegacyMusicDirectory, "fixture.mp3"))
                && !Directory.Exists(fixture.MusicDirectory), "offline unavailability changed profile migration or touched music");

            var buffer = Path.Combine(paths.DataDirectory, "unused-offline-buffer");
            var library = new OfflineLibrary(downloader: new AudioBuffer(buffer), knownPaths: paths);
            var window = new MainWindow(restore: false, diagnosticsDirectory: paths.DataDirectory, offline: library);
            try
            {
                Assert(!library.IsAvailable && library.Read().Count == 0 && library.GetPlaylists().Count == 0
                    && library.ResolvePlaybackSource(new TrackRow { Id = "fixture" }) is null,
                    "disabled offline storage exposed rows or a local source");
                Assert(!Control<System.Windows.Controls.Button>(window, "OfflineNav").IsEnabled
                    && !Control<System.Windows.Controls.Button>(window, "DownloadCenterNav").IsEnabled
                    && !Control<System.Windows.Controls.Button>(window, "SettingsClearOffline").IsEnabled
                    && Control<System.Windows.Controls.TextBlock>(window, "SettingsOfflineLocation").Text == BrandPaths.OfflineUnavailableMessage,
                    "offline commands were enabled or the unavailable state was hidden");
                await Throws<MusicException>(() => library.ClearAsync(default));
                Assert(!Directory.Exists(buffer) && !Directory.Exists(fixture.MusicDirectory), "disabled offline storage created a backup location");
            }
            finally { window.Close(); }
        });
        await Run("Brand migration: encrypted login settings playback sort indexes artwork and audio retain exact bytes", () => Task.Run(() =>
        {
            var paths = BrandFixture("bytes");
            new SessionStore(paths.LegacyDataDirectory).Save(FakeSession);
            foreach (var name in new[] { "settings.json", "playback-state.json", "list-sorts.json", "diagnostics.json" })
                File.WriteAllBytes(Path.Combine(paths.LegacyDataDirectory, name), Encoding.UTF8.GetBytes("\uFEFF{\r\n  \"fixture\": \"" + name + "\"\r\n}\r\n"));
            Directory.CreateDirectory(Path.Combine(paths.LegacyDataDirectory, "artwork"));
            File.WriteAllBytes(Path.Combine(paths.LegacyDataDirectory, "artwork", "fixture.img"), [0, 255, 14, 42]);
            Directory.CreateDirectory(paths.LegacyMusicDirectory);
            var audio = Enumerable.Range(0, 4096).Select(i => (byte)(i % 251)).ToArray();
            File.WriteAllBytes(Path.Combine(paths.LegacyMusicDirectory, "fixture.mp3"), audio);
            var item = new OfflineItem("41", "41:2", "Fixture", "Artist", "Album", 180000, "fixture.mp3", "mp3", 320,
                audio.Length, Convert.ToHexString(SHA256.HashData(audio)).ToLowerInvariant(), DateTimeOffset.UtcNow);
            File.WriteAllText(Path.Combine(paths.LegacyMusicDirectory, "library.index.json"), JsonSerializer.Serialize(new[] { item }));
            File.WriteAllText(Path.Combine(paths.LegacyMusicDirectory, "playlists.index.json"), "{\"schemaVersion\":1,\"playlists\":[]}");
            var beforeData = BrandTreeBytes(paths.LegacyDataDirectory);
            var beforeMusic = BrandTreeBytes(paths.LegacyMusicDirectory);
            var dataCreated = Directory.GetCreationTimeUtc(paths.LegacyDataDirectory);
            var musicCreated = Directory.GetCreationTimeUtc(paths.LegacyMusicDirectory);
            paths.MigrateLegacyData();
            Assert(!Directory.Exists(paths.LegacyDataDirectory) && !Directory.Exists(paths.LegacyMusicDirectory)
                && !Directory.Exists(Path.GetDirectoryName(paths.LegacyMusicDirectory)), "legacy tree was copied instead of renamed or empty old parent remains");
            AssertBrandTree(beforeData, paths.DataDirectory);
            AssertBrandTree(beforeMusic, paths.MusicDirectory);
            Assert(Directory.GetCreationTimeUtc(paths.DataDirectory) == dataCreated && Directory.GetCreationTimeUtc(paths.MusicDirectory) == musicCreated, "directory identity metadata changed");
            Assert(new SessionStore(paths.DataDirectory).Load() == FakeSession, "moved DPAPI login no longer decrypts for same user");
            using var library = new OfflineLibrary(paths.MusicDirectory);
            Assert(library.Find(new TrackRow { Id = "41", RequestId = "41:2" })?.FileName == "fixture.mp3"
                && library.Tracks().Single().LocalPath == Path.Combine(paths.MusicDirectory, "fixture.mp3")
                && library.GetPlaylists().Count == 0, "moved relative indexes no longer resolve");
        }));
        await Run("Brand migration: repeated launch leaves migrated bytes untouched without opening audio", () => Task.Run(() =>
        {
            var paths = BrandFixture("repeat");
            WriteBrandLegacy(paths);
            paths.MigrateLegacyData();
            var beforeData = BrandTreeBytes(paths.DataDirectory);
            var beforeMusic = BrandTreeBytes(paths.MusicDirectory);
            using (var audio = new FileStream(Path.Combine(paths.MusicDirectory, "fixture.mp3"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                paths.MigrateLegacyData(_ => throw new InvalidOperationException("no-op launch attempted a move"));
            AssertBrandTree(beforeData, paths.DataDirectory);
            AssertBrandTree(beforeMusic, paths.MusicDirectory);
        }));
        await Run("Brand migration: empty destination folders accepted; empty legacy folder cannot conflict with current data", () => Task.Run(() =>
        {
            var paths = BrandFixture("empty");
            WriteBrandLegacy(paths);
            Directory.CreateDirectory(paths.DataDirectory); Directory.CreateDirectory(paths.MusicDirectory);
            paths.MigrateLegacyData();
            Assert(File.ReadAllText(Path.Combine(paths.DataDirectory, "settings.json")) == "legacy settings"
                && File.ReadAllText(Path.Combine(paths.MusicDirectory, "fixture.mp3")) == "fixture audio", "empty destinations lost data");
            Directory.CreateDirectory(paths.LegacyDataDirectory); Directory.CreateDirectory(paths.LegacyMusicDirectory);
            paths.MigrateLegacyData(_ => throw new InvalidOperationException("empty legacy folders must not replace current data"));
            Assert(File.Exists(Path.Combine(paths.DataDirectory, "settings.json")), "current data overwritten by empty legacy folder");
        }));
        await Run("Brand migration: both nonempty destinations block all moves and preserve both versions", () => Task.Run(() =>
        {
            foreach (var conflictInData in new[] { true, false })
            {
                var paths = BrandFixture("conflict"); WriteBrandLegacy(paths);
                var conflict = conflictInData ? paths.DataDirectory : paths.MusicDirectory;
                Directory.CreateDirectory(conflict); File.WriteAllText(Path.Combine(conflict, "keep.txt"), "current data");
                RejectUninstall(() => paths.MigrateLegacyData());
                Assert(File.ReadAllText(Path.Combine(conflict, "keep.txt")) == "current data"
                    && File.ReadAllText(Path.Combine(paths.LegacyDataDirectory, "settings.json")) == "legacy settings"
                    && File.ReadAllText(Path.Combine(paths.LegacyMusicDirectory, "fixture.mp3")) == "fixture audio", "conflict moved or replaced a version");
                Assert(!Directory.Exists(conflictInData ? paths.MusicDirectory : paths.DataDirectory), "conflict did not preflight both trees");
            }
        }));
        await Run("Brand migration: alternate streams count as data on both migration directories", () => Task.Run(() =>
        {
            var conflict = BrandFixture("directory-stream-conflict");
            WriteBrandLegacy(conflict);
            Directory.CreateDirectory(conflict.MusicDirectory);
            var targetStream = conflict.MusicDirectory + ":fixture-note";
            File.WriteAllText(targetStream, "current note");
            RejectUninstall(() => conflict.MigrateLegacyData());
            Assert(File.ReadAllText(targetStream) == "current note"
                && File.Exists(Path.Combine(conflict.LegacyDataDirectory, "settings.json"))
                && File.Exists(Path.Combine(conflict.LegacyMusicDirectory, "fixture.mp3")),
                "an empty directory with a named stream was discarded or failed preflight too late");

            var move = BrandFixture("directory-stream-move");
            Directory.CreateDirectory(move.LegacyMusicDirectory);
            var legacyParent = Path.GetDirectoryName(move.LegacyMusicDirectory)!;
            File.WriteAllText(legacyParent + ":fixture-note", "parent note");
            File.WriteAllText(move.LegacyMusicDirectory + ":fixture-note", "music note");
            move.MigrateLegacyData();
            Assert(File.ReadAllText(legacyParent + ":fixture-note") == "parent note"
                && File.ReadAllText(move.MusicDirectory + ":fixture-note") == "music note",
                "renaming the source or tidying its old parent lost a named stream");
        }));
        await Run("Brand migration: checks extended attributes on empty targets at preflight and before replacement", () => Task.Run(() =>
        {
            foreach (var late in new[] { false, true })
            {
                var paths = BrandFixture("directory-ea-" + late);
                var target = late ? paths.DataDirectory : paths.MusicDirectory;
                if (new DriveInfo(Path.GetPathRoot(target)!).DriveFormat != "NTFS") continue;
                WriteBrandLegacy(paths);
                Directory.CreateDirectory(target);
                if (!late) AddFixtureDirectoryExtendedAttribute(target);
                var attached = !late;
                RejectUninstall(() => paths.MigrateLegacyData(source =>
                {
                    if (!late || source != paths.LegacyDataDirectory) return;
                    AddFixtureDirectoryExtendedAttribute(target);
                    attached = true;
                }));
                Assert(attached && File.Exists(Path.Combine(paths.LegacyDataDirectory, "settings.json"))
                    && File.Exists(Path.Combine(paths.LegacyMusicDirectory, "fixture.mp3")), "migration consumed a source before detecting target metadata");
                using var handle = UninstallPathGuard.HoldDirectory(target);
                Assert(!Directory.EnumerateFileSystemEntries(target).Any()
                    && FileSecurityPreservation.HasNamedStreamsOrExtendedAttributes(handle)
                    && !FileSecurityPreservation.HasNamedDataStreams(handle), "migration changed an EA-only target");
            }
        }));
        await Run("Brand migration: interrupted second rename resumes with first tree unchanged", () => Task.Run(() =>
        {
            var paths = BrandFixture("partial"); WriteBrandLegacy(paths);
            RejectUninstall(() => paths.MigrateLegacyData(source =>
            { if (source == paths.LegacyMusicDirectory) throw new IOException("injected second-tree failure"); }));
            Assert(!Directory.Exists(paths.LegacyDataDirectory) && File.Exists(Path.Combine(paths.DataDirectory, "settings.json"))
                && File.Exists(Path.Combine(paths.LegacyMusicDirectory, "fixture.mp3")) && !Directory.Exists(paths.MusicDirectory), "partial outcome lost or copied data");
            var before = BrandTreeBytes(paths.DataDirectory);
            paths.MigrateLegacyData(source => Assert(source == paths.LegacyMusicDirectory, "retry moved already completed data tree"));
            AssertBrandTree(before, paths.DataDirectory);
            Assert(File.ReadAllText(Path.Combine(paths.MusicDirectory, "fixture.mp3")) == "fixture audio", "retry failed to finish music tree");
        }));
        await Run("Brand migration: long local paths preserve both complete trees", () => Task.Run(() =>
        {
            var paths = BrandFixture("long-local-paths", longPaths: true);
            WriteBrandLegacy(paths);
            var nested = Path.Combine(paths.LegacyMusicDirectory, "nested");
            Directory.CreateDirectory(nested);
            File.WriteAllText(Path.Combine(nested, "keep.txt"), "nested fixture");
            Directory.CreateDirectory(paths.DataDirectory);
            Directory.CreateDirectory(paths.MusicDirectory);
            var data = BrandTreeBytes(paths.LegacyDataDirectory);
            var music = BrandTreeBytes(paths.LegacyMusicDirectory);

            paths.MigrateLegacyData();
            Assert(!Directory.Exists(paths.LegacyDataDirectory) && !Directory.Exists(Path.GetDirectoryName(paths.LegacyMusicDirectory)),
                "long-path migration left the legacy tree or its empty parent");
            paths.MigrateLegacyData(_ => throw new InvalidOperationException("repeated long-path migration attempted a move"));
            AssertBrandTree(data, paths.DataDirectory);
            AssertBrandTree(music, paths.MusicDirectory);
        }));
        await Run("Brand migration: sharing failure and destination races never overwrite or delete data", () => Task.Run(() =>
        {
            var locked = BrandFixture("locked"); WriteBrandLegacy(locked);
            using (var root = UninstallPathGuard.HoldDirectory(locked.LegacyDataDirectory))
                RejectUninstall(() => locked.MigrateLegacyData());
            Assert(File.Exists(Path.Combine(locked.LegacyDataDirectory, "settings.json")) && !Directory.Exists(locked.DataDirectory), "sharing failure changed source");
            locked.MigrateLegacyData();
            foreach (var emptyDestination in new[] { true, false })
            {
                var paths = BrandFixture("race"); WriteBrandLegacy(paths);
                if (emptyDestination) Directory.CreateDirectory(paths.DataDirectory);
                RejectUninstall(() => paths.MigrateLegacyData(source =>
                {
                    if (source != paths.LegacyDataDirectory) return;
                    Directory.CreateDirectory(paths.DataDirectory);
                    File.WriteAllText(Path.Combine(paths.DataDirectory, "keep.txt"), "raced file");
                }));
                Assert(File.ReadAllText(Path.Combine(paths.DataDirectory, "keep.txt")) == "raced file"
                    && File.ReadAllText(Path.Combine(paths.LegacyDataDirectory, "settings.json")) == "legacy settings"
                    && Directory.Exists(paths.LegacyMusicDirectory), "destination race overwrote/deleted/moved data");
            }
        }));
        await Run("Brand migration: source junction and redirected destination parent preserve external data", () => Task.Run(() =>
        {
            foreach (var kind in new[] { "source-child", "source-root", "target-parent", "target-root" })
            {
                var paths = BrandFixture("links");
                var outside = Path.Combine(Path.GetDirectoryName(paths.LocalData)!, "outside");
                Directory.CreateDirectory(outside); File.WriteAllText(Path.Combine(outside, "keep.txt"), "external bytes");
                if (kind != "source-root") WriteBrandLegacy(paths);
                else Directory.CreateDirectory(paths.LegacyDataDirectory);
                var link = kind switch
                {
                    "source-child" => Path.Combine(paths.LegacyDataDirectory, "linked"),
                    "source-root" => paths.LegacyMusicDirectory,
                    "target-parent" => Path.GetDirectoryName(paths.MusicDirectory)!,
                    _ => paths.MusicDirectory
                };
                CreateUninstallJunction(link, outside);
                RejectUninstall(() => paths.MigrateLegacyData());
                Assert(File.ReadAllText(Path.Combine(outside, "keep.txt")) == "external bytes" && Directory.Exists(link)
                    && Directory.Exists(paths.LegacyDataDirectory) && !Directory.Exists(paths.DataDirectory), "link preflight followed or moved data");
            }
        }));
        await Run("Brand migration: full uninstall covers new and legacy paths while retaining foreign files and siblings", () => Task.Run(() =>
        {
            var brand = BrandFixture("uninstall");
            var paths = UninstallPaths.FromKnownFolders(brand.LocalData, brand.Documents);
            Assert(paths.Data == brand.DataDirectory && paths.Music == brand.MusicDirectory, "uninstall brand targets differ");
            var expiry = DateTimeOffset.UtcNow.AddHours(1);
            new SessionStore(paths.Data).Save(new Session("current-fixture-token", null, expiry, "current-fixture"));
            new SessionStore(paths.LegacyData).Save(new Session("legacy-fixture-token", null, expiry, "legacy-fixture"));
            WriteUninstallAudio(paths, "new.mp3", "new managed audio");
            Directory.CreateDirectory(paths.LegacyMusic);
            var audio = Encoding.UTF8.GetBytes("old managed audio");
            File.WriteAllBytes(Path.Combine(paths.LegacyMusic, "old.mp3"), audio);
            File.WriteAllText(Path.Combine(paths.LegacyMusic, "library.index.json"), JsonSerializer.Serialize(new[] {
                UninstallFixtureAudio("old.mp3", audio) }));
            File.WriteAllText(Path.Combine(paths.LegacyMusic, "manual.flac"), "foreign audio");
            var unrelated = Path.Combine(paths.Documents, "other-app.txt"); File.WriteAllText(unrelated, "keep sibling");
            var legacySibling = Path.Combine(Path.GetDirectoryName(paths.LegacyMusic)!, "keep.txt"); File.WriteAllText(legacySibling, "keep parent child");
            var confirmation = UninstallSupport.ConfirmationText(paths);
            Assert(confirmation.Contains(paths.Data) && confirmation.Contains(paths.Music)
                && confirmation.Contains(paths.LegacyData) && confirmation.Contains(paths.LegacyMusic), "uninstall confirmation omits legacy scope");
            var result = UninstallCleanup.Execute(paths);
            Assert(result.Complete && result.RemovedFiles == 6 && result.PreservedForeignFiles == 1, JsonSerializer.Serialize(result));
            Assert(!Directory.Exists(paths.Data) && !Directory.Exists(paths.LegacyData) && !Directory.Exists(paths.Music)
                && !File.Exists(Path.Combine(paths.LegacyMusic, "old.mp3"))
                && File.ReadAllText(Path.Combine(paths.LegacyMusic, "manual.flac")) == "foreign audio"
                && File.ReadAllText(unrelated) == "keep sibling" && File.ReadAllText(legacySibling) == "keep parent child", "uninstall lost foreign data or left managed bytes");
        }));
        await Run("Brand migration: previous uninstall receipt can be cancelled without opening or deleting data", () => Task.Run(() =>
        {
            var paths = UninstallFixture("legacy-receipt");
            Directory.CreateDirectory(paths.LegacyData); File.WriteAllText(Path.Combine(paths.LegacyData, "keep.dpapi"), "old encrypted fixture");
            Directory.CreateDirectory(paths.Data); File.WriteAllText(Path.Combine(paths.Data, "keep.dpapi"), "new encrypted fixture");
            var nonce = UninstallSupport.CreateReceipt(paths);
            var receipt = Path.Combine(paths.Install, "full-uninstall.pending.json");
            var json = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(receipt))!;
            json["Data"] = paths.LegacyData; json["Music"] = paths.LegacyMusic;
            File.WriteAllText(receipt, json.ToJsonString());
            UninstallSupport.ValidateReceipt(paths, nonce);
            Assert(UninstallSupport.CancelPendingReceipt(paths) && !File.Exists(receipt), "legacy receipt cannot be safely cancelled");
            Assert(File.ReadAllText(Path.Combine(paths.LegacyData, "keep.dpapi")) == "old encrypted fixture"
                && File.ReadAllText(Path.Combine(paths.Data, "keep.dpapi")) == "new encrypted fixture", "legacy receipt cancellation deleted data");
        }));
    }

    private static SafeFileHandle HoldBrandFixtureDirectory(string directory)
    {
        const uint readAttributes = 0x80, read = 0x80000000, shareAll = 7;
        const uint openExisting = 3, openDirectoryWithoutFollowingLinks = 0x02200000;
        const FileAttributes directoryKind = FileAttributes.Directory | FileAttributes.ReparsePoint;
        var nativePath = UninstallPathGuard.ToWin32Path(directory);
        var expected = ReadIdentity();
        int[] retryDelays = [25, 75];
        for (var attempt = 0; ; attempt++)
        {
            var handle = UninstallCreateFile(nativePath, read, 0, IntPtr.Zero, openExisting, openDirectoryWithoutFollowingLinks, IntPtr.Zero);
            var nativeError = Marshal.GetLastPInvokeError();
            if (!handle.IsInvalid)
            {
                try
                {
                    Assert((File.GetAttributes(directory) & directoryKind) == FileAttributes.Directory
                        && UninstallPathGuard.GetStableFileIdentity(handle) == expected,
                        "the locked fixture directory changed or cannot be inspected");
                    return handle;
                }
                catch { handle.Dispose(); throw; }
            }

            handle.Dispose();
            if (nativeError is not (32 or 33) || attempt == retryDelays.Length)
                throw new Win32Exception(nativeError, $"Could not lock fixture directory '{directory}' (Win32 {nativeError}).");
            Thread.Sleep(retryDelays[attempt]);
            Assert(ReadIdentity() == expected, "the fixture directory changed before retrying its lock");
        }

        UninstallFileIdentity ReadIdentity()
        {
            using var attributes = UninstallCreateFile(nativePath, readAttributes, shareAll, IntPtr.Zero, openExisting,
                openDirectoryWithoutFollowingLinks, IntPtr.Zero);
            var nativeError = Marshal.GetLastPInvokeError();
            if (attributes.IsInvalid)
                throw new Win32Exception(nativeError, $"Could not inspect fixture directory '{directory}' (Win32 {nativeError}).");
            Assert((File.GetAttributes(directory) & directoryKind) == FileAttributes.Directory, "the fixture path is not an ordinary directory");
            return UninstallPathGuard.GetStableFileIdentity(attributes);
        }
    }

    private static BrandPaths BrandFixture(string name, bool longPaths = false)
    {
        var root = Path.GetFullPath(Path.Combine(_directory, "brand-migration", name + "-" + Guid.NewGuid().ToString("N")));
        Assert(root.StartsWith(Path.GetFullPath(_directory) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase), "brand fixture escaped results directory");
        while (longPaths && root.Length < 270) root = Path.Combine(root, new string('m', 72));
        var paths = BrandPaths.FromKnownFolders(Path.Combine(root, "LocalAppData"), Path.Combine(root, "Documents"));
        Directory.CreateDirectory(paths.LocalData); Directory.CreateDirectory(paths.Documents);
        return paths;
    }

    private static TestVelopackLocator BrandFixtureInstallLocator(UninstallPaths paths, string id = BrandPaths.LegacyPackageId, string version = "0.7.0")
        => new(id, version, Path.Combine(paths.Install, "packages"), Path.GetDirectoryName(paths.Executable), paths.Install, paths.Updater);

    private sealed class UnversionedFixtureLocator(string root)
        : TestVelopackLocator(BrandPaths.LegacyPackageId, "0.7.0", Path.Combine(root, "packages"))
    {
        public override SemanticVersion? CurrentlyInstalledVersion => null;
    }

    private static void WriteBrandLegacy(BrandPaths paths)
    {
        Directory.CreateDirectory(paths.LegacyDataDirectory); Directory.CreateDirectory(paths.LegacyMusicDirectory);
        File.WriteAllText(Path.Combine(paths.LegacyDataDirectory, "settings.json"), "legacy settings");
        File.WriteAllText(Path.Combine(paths.LegacyMusicDirectory, "fixture.mp3"), "fixture audio");
    }

    private static Dictionary<string, byte[]> BrandTreeBytes(string root) => Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
        .ToDictionary(file => Path.GetRelativePath(root, file), File.ReadAllBytes, StringComparer.OrdinalIgnoreCase);

    private static void AssertBrandTree(Dictionary<string, byte[]> expected, string root)
    {
        var actual = BrandTreeBytes(root);
        Assert(expected.Count == actual.Count && expected.All(pair => actual.TryGetValue(pair.Key, out var bytes)
            && pair.Value.SequenceEqual(bytes)), "migration changed file bytes or tree membership: " + root);
    }
}
