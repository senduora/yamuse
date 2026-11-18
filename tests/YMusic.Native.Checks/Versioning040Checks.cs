using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using Velopack;
using Velopack.Locators;
using YMusic.Native;

internal static partial class Program
{
    private static async Task CheckVersioning040()
    {
        await Run("Versioning040: runtime footer and update state retain the full release identity", V040Metadata);
        await Run("Versioning040: portable stable feed promotes preview to final and prevents equal or older offers", V040Portable);
        await Run("Versioning040: installed updates read only the assets of the latest stable GitHub release", V040InstalledFeed);
        await Run("Versioning040: installed feed rejects wrong package ID, preview version and missing SHA-256", V040InstalledTrust);
        await Run("Versioning040: a pending package requires official metadata and matching bytes before restart", V040PendingTrust);
        await Run("Versioning040: rechecking a pending package observes new and withdrawn offers", V040PendingRefresh);
        await Run("Versioning040: canceling an installed recheck preserves the previous download selection", V040CanceledSelection);
        await Run("Versioning040: canceling after the committed download retains installed readiness", V040CommittedDownload);
        await Run("Versioning040: explicit apply atomically installs the verified updater and rolls back a failed handoff", V040UpdaterApply);
        await Run("Versioning040: updater replacement preserves unsupported metadata and leaves identical updater in place", V040UpdaterMetadata);
        await Run("Versioning040: cleanup retires only verified older archives and retries a locked package", V040TrustedPackageCleanup);
        await Run("Versioning040: cleanup requires a locally authenticated package receipt", V040UnauthenticatedReceipts);
        await Run("Versioning040: unsupported persistent file identity skips both package registration and retirement", V040UnsupportedPackageIdentity);
        await Run("Versioning040: a full package ledger replaces its oldest receipt without deleting its file", V040FullPackageLedger);
    }

    private static Task V040Metadata()
    {
        var assembly = typeof(UpdateSupport).Assembly;
        const string expected = "0.7.0";
        Assert(assembly.GetName().Version == new Version(0, 7, 0, 0), "numeric assembly identity drifted");
        Assert(assembly.GetCustomAttribute<AssemblyFileVersionAttribute>()?.Version == "0.7.0.0", "numeric file identity drifted");
        Assert(assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion == expected,
            "full release identity was lost in informational metadata");
        Assert(FileVersionInfo.GetVersionInfo(assembly.Location).ProductVersion == expected, "Windows product metadata disagrees with the expected release");
        Assert(AppBranding.CurrentVersion == expected && AppBranding.VersionLabel == "v" + expected, "footer identity disagrees with the full release version");
        var service = new UpdateSupport(Path.Combine(_directory, "versioning040-missing-feed.json"), false,
            _ => throw new InvalidOperationException("metadata check created an update backend"));
        Assert(service.State.CurrentVersion == expected && service.State.Status == AppUpdateStatus.NotConfigured,
            "update settings identity disagrees with the footer or missing feed created a check");
        Assert(SemanticVersion.Parse("0.4.0-preview.10") > SemanticVersion.Parse("0.4.0-preview.2")
            && SemanticVersion.Parse("0.4.0") > SemanticVersion.Parse("0.4.0-preview.10")
            && SemanticVersion.Parse("0.4.1") > SemanticVersion.Parse("0.4.0"), "preview or patch ordering is not semantic");
        return Task.CompletedTask;
    }

    private static async Task V040Portable()
    {
        async Task<string?> Check(string current, string target, bool prerelease = false)
            => await UpdateSupport.CheckPortableReleaseAsync(current, CancellationToken.None,
                new FakeHandler((request, _) =>
                {
                    Assert(request.RequestUri?.AbsoluteUri == "https://api.github.com/repos/senduora/yamuse/releases/latest"
                        && request.Method == HttpMethod.Get && request.Headers.Authorization is null,
                        "portable version comparison changed its metadata authority");
                    return Task.FromResult(Json($$"""{"tag_name":"{{target}}","draft":false,"prerelease":{{prerelease.ToString().ToLowerInvariant()}}}"""));
                }));
        Assert(await Check("0.4.0-preview.1", "v0.4.0") == "0.4.0", "final with the same numeric version was not offered to preview");
        Assert(await Check("0.4.0", "v0.4.0") is null, "equal stable version was offered again");
        Assert(await Check("0.4.0-preview.1", "v0.3.16") is null, "last published older line was offered as a downgrade");
        Assert(await Check("0.4.1", "v0.4.0") is null, "older patch was offered as a downgrade");
        Assert(await Check("0.4.0-preview.1", "v0.4.1") == "0.4.1", "newer stable patch was not offered");
        Assert(await Check("0.4.0", "v0.4.0+release.7") is null, "build metadata created an update with equal precedence");
        await Throws<InvalidDataException>(() => Check("0.4.0-preview.1", "v0.5.0-preview.1", true));
        await Throws<InvalidDataException>(() => Check("0.4.0-preview.1", "v0.5.0-preview.1"));
    }

    private static async Task V040InstalledFeed()
    {
        foreach (var (current, expected) in new[]
        {
            ("0.3.16", "0.4.0"), ("0.4.0-preview.1", "0.4.0"), ("0.4.0", (string?)null), ("0.4.1", (string?)null)
        })
        {
            var directory = Path.Combine(_directory, "versioning040-feed", current);
            Directory.CreateDirectory(directory);
            var downloader = new V040GithubDownloader();
            var locator = new TestVelopackLocator("YMusic.Native", current, directory);
            var backend = new VelopackUpdateBackend(UpdateSupport.OfficialRepository, locator, () => new FakeHandler(downloader.ReadMetadataAsync));
            var version = await backend.CheckAsync(CancellationToken.None);
            Assert(locator.CurrentlyInstalledVersion?.ToString() == current && version == expected,
                "installed stable feed version comparison drifted for " + current);
            var expectedRequests = expected is null ? new[] { V040GithubDownloader.Latest }
                : new[] { V040GithubDownloader.Latest, downloader.FeedUrl };
            Assert(downloader.Requests.SequenceEqual(expectedRequests)
                && downloader.Downloads == 0, "stable check read preview assets or downloaded/applied a package");
        }
    }

    private static async Task V040InstalledTrust()
    {
        foreach (var (packageId, version, hash) in new (string, string, string?)[]
        {
            ("Other.Application", "0.4.0", new string('0', 64)),
            ("YMusic.Native", "0.4.0-preview.1", new string('0', 64)),
            ("YMusic.Native", "0.4.0", null)
        })
        {
            var directory = Path.Combine(_directory, "versioning040-trust", packageId + "-" + version + "-" + (hash is null ? "no-hash" : "hashed"));
            var locator = new TestVelopackLocator("YMusic.Native", "0.3.16", directory);
            var downloader = new V040GithubDownloader { Feed = V040Feed(packageId, version, hash, 1) };
            var service = V040Service(directory, locator, downloader);
            var state = await service.CheckAsync();
            Assert(state.Status == AppUpdateStatus.Error && state.AvailableVersion is null && downloader.Downloads == 0,
                "invalid installed feed entry reached the download action");
        }

        var crossTagDirectory = Path.Combine(_directory, "versioning040-cross-tag");
        var crossTagLocator = new TestVelopackLocator("YMusic.Native", "0.3.16", crossTagDirectory);
        var crossTag = new V040GithubDownloader { PackageTagOverride = "v0.3.17" };
        var crossTagState = await V040Service(crossTagDirectory, crossTagLocator, crossTag).CheckAsync();
        Assert(crossTagState.Status == AppUpdateStatus.Error && crossTagState.AvailableVersion is null && crossTag.Downloads == 0,
            "latest release metadata accepted a package link belonging to a different tag");
    }

    private static async Task V040PendingTrust()
    {
        var directory = Path.Combine(_directory, "versioning040-pending");
        var locator = new V040NoScanLocator(directory);
        var (downloader, officialBytes) = V040PackageDownloader();
        Directory.CreateDirectory(locator.PackagesDir!);
        var path = Path.Combine(locator.PackagesDir!, V040GithubDownloader.PackageName);
        var updater = locator.UpdateExePath!;
        Directory.CreateDirectory(Path.GetDirectoryName(updater)!);
        File.WriteAllText(updater, "fixture updater before download");
        File.WriteAllBytes(path, officialBytes);
        var service = V040Service(directory, locator, downloader);
        var restored = await service.CheckAsync();
        Assert(restored.Status == AppUpdateStatus.Downloaded && restored.AvailableVersion == "0.4.0" && downloader.Downloads == 0,
            "a verified pending package was not restored without a second download");

        var changed = officialBytes.ToArray();
        changed[0] ^= 1;
        File.WriteAllBytes(path, changed);
        await Throws<InvalidOperationException>(() => { service.ApplyAndRestart(); return Task.CompletedTask; });
        Assert(service.State.Status == AppUpdateStatus.Error && locator.ExitCode is null,
            "a replaced package was accepted for restart");
        var offer = await service.CheckAsync();
        Assert(offer.Status == AppUpdateStatus.Available && offer.AvailableVersion == "0.4.0",
            "a damaged cached file hid the verified official offer or appeared ready to install");
        downloader.UntrustedRedirect = true;
        Assert((await service.DownloadAsync()).Status == AppUpdateStatus.Error && downloader.Downloads == 0,
            "an off-origin redirect was followed");
        downloader.UntrustedRedirect = false;
        downloader.PackageBytes = changed;
        Assert((await service.DownloadAsync()).Status == AppUpdateStatus.Error && File.ReadAllBytes(path).SequenceEqual(changed)
            && File.ReadAllText(updater) == "fixture updater before download" && !Directory.EnumerateFiles(locator.PackagesDir!, "*.tmp").Any(),
            "invalid incoming bytes replaced the cache, touched the updater or left a staged file");
        downloader.PackageBytes = officialBytes;
        var repaired = await service.DownloadAsync();
        Assert(repaired.Status == AppUpdateStatus.Downloaded && downloader.Downloads == 2 && File.ReadAllBytes(path).SequenceEqual(officialBytes)
            && File.ReadAllText(updater) == "fixture updater before download",
            "explicit download failed to replace the unverified cached bytes");

        downloader.Offline = true;
        var restarted = V040Service(directory, locator, downloader);
        var offline = await restarted.CheckAsync();
        Assert(offline.Status == AppUpdateStatus.Error && offline.AvailableVersion is null && downloader.Downloads == 2,
            "an offline check presented a pending package without trusted metadata");
    }

    private static async Task V040PendingRefresh()
    {
        var directory = Path.Combine(_directory, "versioning040-pending-refresh");
        var locator = new TestVelopackLocator("YMusic.Native", "0.3.16", directory);
        var (downloader, bytes) = V040PackageDownloader();
        Directory.CreateDirectory(locator.PackagesDir!);
        File.WriteAllBytes(Path.Combine(locator.PackagesDir!, V040GithubDownloader.PackageName), bytes);
        var service = V040Service(directory, locator, downloader, TimeSpan.FromSeconds(1));
        var pending = await service.CheckAsync();
        Assert(pending.Status == AppUpdateStatus.Downloaded && pending.CheckSucceeded, "pending fixture could not be confirmed");

        downloader.Offline = true;
        var offline = await service.CheckAsync();
        Assert(offline.Status == AppUpdateStatus.Downloaded && offline.AvailableVersion == "0.4.0" && !offline.CheckSucceeded,
            "temporary offline check dropped the verified package or reported success");
        downloader.Offline = false;
        foreach (var bodyFailure in new IOException[]
        {
            new HttpIOException(HttpRequestError.ResponseEnded, "fixture HTTP body ended"),
            new("fixture TLS connection reset", new SocketException((int)SocketError.ConnectionReset))
        })
        {
            downloader.BodyFailure = bodyFailure;
            var ended = await service.CheckAsync();
            Assert(ended.Status == AppUpdateStatus.Downloaded && !ended.CheckSucceeded, "body interruption dropped the verified pending package");
        }
        downloader.BodyFailure = null;
        downloader.LatestStatus = HttpStatusCode.Forbidden;
        foreach (var quotaHeader in new[] { "x-ratelimit-remaining", "Retry-After" })
        {
            downloader.QuotaHeader = quotaHeader;
            var throttled = await service.CheckAsync();
            Assert(throttled.Status == AppUpdateStatus.Downloaded && !throttled.CheckSucceeded, "documented 403 quota dropped the verified pending package");
        }
        downloader.QuotaHeader = null;
        Assert((await service.CheckAsync()).Status == AppUpdateStatus.Error, "ordinary 403 preserved unconfirmed readiness");
        downloader.LatestStatus = HttpStatusCode.OK;
        Assert((await service.CheckAsync()).Status == AppUpdateStatus.Downloaded, "fixture did not revalidate the pending package");
        downloader.BeforeFeed = token => Task.Delay(Timeout.InfiniteTimeSpan, token);
        var timedOut = await service.CheckAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert(timedOut.Status == AppUpdateStatus.Downloaded && timedOut.AvailableVersion == "0.4.0" && !timedOut.CheckSucceeded,
            "the metadata deadline left the check outstanding or invalidated the verified package");
        downloader.ReleaseVersion = "0.4.1";
        downloader.Feed = V040Feed("YMusic.Native", "0.4.1", new string('0', 64), 1);
        using (var cancellation = new CancellationTokenSource())
        {
            downloader.BeforeFeed = _ => { cancellation.Cancel(); return Task.CompletedTask; };
            var canceled = await service.CheckAsync(cancellation.Token);
            Assert(canceled.Status == AppUpdateStatus.Downloaded && canceled.AvailableVersion == "0.4.0" && !canceled.CheckSucceeded,
                "canceling a pending recheck changed the committed selection");
        }
        downloader.BeforeFeed = null;
        var updated = await service.CheckAsync();
        Assert(updated.Status == AppUpdateStatus.Available && updated.AvailableVersion == "0.4.1" && updated.CheckSucceeded,
            "a pending package masked the newer official offer");

        downloader.ReleaseVersion = "0.4.0";
        downloader.Feed = V040Feed("YMusic.Native", "0.4.0", Convert.ToHexString(SHA256.HashData(bytes)), bytes.Length);
        Assert((await service.CheckAsync()).Status == AppUpdateStatus.Downloaded, "fixture could not restore the old pending state");
        downloader.LatestStatus = HttpStatusCode.NotFound;
        var withdrawn = await service.CheckAsync();
        await Throws<InvalidOperationException>(() => { service.ApplyAndRestart(); return Task.CompletedTask; });
        Assert(withdrawn.Status == AppUpdateStatus.NoUpdate && withdrawn.AvailableVersion is null && withdrawn.CheckSucceeded
            && locator.ExitCode is null && downloader.Downloads == 0, "a withdrawn package remained available to apply");
    }

    private static async Task V040CanceledSelection()
    {
        var directory = Path.Combine(_directory, "versioning040-cancellation");
        var locator = new TestVelopackLocator("YMusic.Native", "0.3.16", directory);
        var (downloader, _) = V040PackageDownloader();
        var service = V040Service(directory, locator, downloader);
        Assert((await service.CheckAsync()).AvailableVersion == "0.4.0", "fixture did not select the original offer");

        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        downloader.ReleaseVersion = "0.4.1";
        downloader.Feed = V040Feed("YMusic.Native", "0.4.1", new string('0', 64), 1);
        downloader.BeforeFeed = async _ => { entered.TrySetResult(); await finish.Task.ConfigureAwait(false); };
        using var cancellation = new CancellationTokenSource();
        var checking = service.CheckAsync(cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var downloading = service.DownloadAsync();
        var wasSerialized = !downloading.IsCompleted && downloader.Downloads == 0;
        cancellation.Cancel();
        finish.TrySetResult();
        var canceled = await checking;
        var downloaded = await downloading;
        Assert(wasSerialized && canceled.Status == AppUpdateStatus.Available && canceled.AvailableVersion == "0.4.0"
            && downloaded.Status == AppUpdateStatus.Downloaded && downloaded.AvailableVersion == "0.4.0"
            && downloader.Downloads == 1, "a canceled check changed the selected package or released the operation early");
    }

    private static async Task V040CommittedDownload()
    {
        using var cancellation = new CancellationTokenSource();
        var directory = Path.Combine(_directory, "versioning040-commit-cancel");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "update-feed.json");
        File.WriteAllText(path, """{"githubRepository":"https://github.com/senduora/yamuse"}""");
        var backend = new V040CommitBackend(cancellation);
        var service = new UpdateSupport(path, true, _ => backend);
        Assert((await service.CheckAsync()).Status == AppUpdateStatus.Available, "commit fixture did not select an update");
        var state = await service.DownloadAsync(cancellationToken: cancellation.Token);
        Assert(cancellation.IsCancellationRequested && state.Status == AppUpdateStatus.Downloaded && state.AvailableVersion == "0.4.0",
            "cancellation after backend commit revoked the completed state");
    }

    private static async Task V040UpdaterApply()
    {
        var directory = Path.Combine(_directory, "versioning040-updater-apply");
        var locator = new V040NoScanLocator(directory) { FailStart = true };
        var (downloader, bytes) = V040PackageDownloader();
        Directory.CreateDirectory(locator.PackagesDir!);
        Directory.CreateDirectory(locator.RootAppDir);
        var package = Path.Combine(locator.PackagesDir!, V040GithubDownloader.PackageName);
        File.WriteAllText(locator.UpdateExePath, "original updater fixture");
        var service = V040Service(directory, locator, downloader);
        Assert((await service.CheckAsync()).Status == AppUpdateStatus.Available, "apply fixture did not select an update");
        Assert((await service.DownloadAsync()).Status == AppUpdateStatus.Downloaded && File.ReadAllText(locator.UpdateExePath) == "original updater fixture",
            "downloading touched the canonical updater before the explicit apply");

        using (var locked = File.OpenRead(locator.UpdateExePath))
            await Throws<InvalidOperationException>(() => { service.ApplyAndRestart(); return Task.CompletedTask; });
        Assert(locator.Starts == 0 && File.ReadAllText(locator.UpdateExePath) == "original updater fixture"
            && !Directory.EnumerateFiles(locator.RootAppDir, "*.tmp").Any(), "failed promotion changed the existing locked updater or leaked a stage");
        Assert((await service.CheckAsync()).Status == AppUpdateStatus.Downloaded, "fixture did not restore the pending package after a blocked promotion");

        await Throws<InvalidOperationException>(() => { service.ApplyAndRestart(); return Task.CompletedTask; });
        Assert(locator.Starts == 1 && locator.SeenUpdater == "fixture archive marker" && locator.ExitCode is null
            && File.ReadAllText(locator.UpdateExePath) == "original updater fixture" && !Directory.EnumerateFiles(locator.RootAppDir, "*.tmp").Any(),
            "failed handoff did not retain the previous updater byte for byte");
        Assert((await service.CheckAsync()).Status == AppUpdateStatus.Downloaded, "fixture did not restore the pending package after failed handoff");
        locator.FailStart = false;
        service.ApplyAndRestart();
        Assert(locator.Starts == 2 && locator.ExitCode == 0 && locator.SeenUpdater == "fixture archive marker"
            && locator.SeenArguments is { } arguments && arguments.Contains("apply") && arguments.Contains(package)
            && File.ReadAllText(locator.UpdateExePath) == "fixture archive marker" && File.ReadAllBytes(package).SequenceEqual(bytes)
            && !Directory.EnumerateFiles(locator.RootAppDir, "*.tmp").Any(),
            "explicit apply did not hand off the verified updater and the exact official package");
    }

    private static async Task V040UpdaterMetadata()
    {
        var directory = Path.Combine(_directory, "versioning040-updater-metadata");
        if (new DriveInfo(Path.GetPathRoot(directory)!).DriveFormat != "NTFS") return;
        var locator = new V040NoScanLocator(directory);
        Directory.CreateDirectory(locator.PackagesDir!);
        Directory.CreateDirectory(locator.RootAppDir);
        var (downloader, bytes) = V040PackageDownloader();
        File.WriteAllBytes(Path.Combine(locator.PackagesDir!, V040GithubDownloader.PackageName), bytes);
        var namedStream = locator.UpdateExePath + ":YamuseFixture";
        File.WriteAllText(locator.UpdateExePath, "original updater with a named stream");
        File.WriteAllText(namedStream, "fixture stream contents");
        var service = V040Service(directory, locator, downloader);
        Assert((await service.CheckAsync()).Status == AppUpdateStatus.Downloaded, "metadata test did not restore the pending archive");

        await Throws<InvalidOperationException>(() => { service.ApplyAndRestart(); return Task.CompletedTask; });
        Assert(locator.Starts == 0 && locator.ExitCode is null && File.ReadAllText(locator.UpdateExePath) == "original updater with a named stream"
            && File.ReadAllText(namedStream) == "fixture stream contents" && !Directory.EnumerateFiles(locator.RootAppDir, "*.tmp").Any(),
            "changing the updater dropped unsupported metadata or started the updater");

        File.WriteAllText(locator.UpdateExePath, "fixture archive marker");
        File.WriteAllText(namedStream, "fixture stream contents");
        Assert((await service.CheckAsync()).Status == AppUpdateStatus.Downloaded, "metadata fixture did not revalidate the pending archive");
        service.ApplyAndRestart();
        Assert(locator.Starts == 1 && locator.ExitCode == 0 && locator.SeenUpdater == "fixture archive marker"
            && File.ReadAllText(namedStream) == "fixture stream contents" && !Directory.EnumerateFiles(locator.RootAppDir, "*.tmp").Any(),
            "identical updater was unnecessarily replaced or its metadata blocked the handoff");
    }

    private static async Task V040TrustedPackageCleanup()
    {
        var directory = Path.Combine(_directory, "versioning040-package-cleanup");
        var oldLocator = new TestVelopackLocator("YMusic.Native", "0.3.16", directory);
        Directory.CreateDirectory(oldLocator.PackagesDir!);
        string PackagePath(string version) => Path.Combine(oldLocator.PackagesDir!, $"Yamuse-{version}-full.nupkg");
        var busy = PackagePath("0.4.0");
        var obsolete = PackagePath("0.4.1");
        var changed = PackagePath("0.4.2");
        var replaced = PackagePath("0.3.17");
        var unknown = PackagePath("0.4.3");
        var current = PackagePath("0.4.4");
        var foreign = Path.Combine(oldLocator.PackagesDir!, "OtherApplication-0.4.0-full.nupkg");

        foreach (var version in new[] { "0.4.0", "0.4.1", "0.4.2", "0.3.17", "0.4.4" })
        {
            var (downloader, bytes) = V040PackageDownloader(version);
            var original = V040Service(directory, oldLocator, downloader);
            if (version == "0.4.0")
            {
                Assert((await original.CheckAsync()).Status == AppUpdateStatus.Available, "seed package was not offered");
                Assert((await original.DownloadAsync()).Status == AppUpdateStatus.Downloaded, "seed package was not committed");
            }
            else
            {
                File.WriteAllBytes(PackagePath(version), bytes);
                Assert((await original.CheckAsync()).Status == AppUpdateStatus.Downloaded, "existing package was not confirmed and adopted");
            }
        }

        var altered = File.ReadAllBytes(changed);
        altered[^1] ^= 1;
        File.WriteAllBytes(changed, altered);
        var movedOriginal = Path.Combine(oldLocator.PackagesDir!, "original-moved-package.bin");
        var copied = File.ReadAllBytes(replaced);
        File.Move(replaced, movedOriginal);
        File.WriteAllBytes(replaced, copied);
        File.WriteAllText(unknown, "untracked legacy package fixture");
        File.WriteAllText(foreign, "foreign package fixture");
        var keepCurrent = File.ReadAllBytes(current);
        var (next, nextBytes) = V040PackageDownloader("0.4.5");
        var pending = PackagePath("0.4.5");
        File.WriteAllBytes(pending, nextBytes);
        var newLocator = new TestVelopackLocator("YMusic.Native", "0.4.4", directory);
        var check = V040Service(directory, newLocator, next);

        using (var reader = File.OpenRead(busy))
        {
            var first = await check.CheckAsync();
            Assert(first.Status == AppUpdateStatus.Downloaded && first.CheckSucceeded && first.AvailableVersion == "0.4.5"
                && File.Exists(busy) && !File.Exists(obsolete), "locked receipt blocked an independent removal or altered update readiness");
        }

        await check.CheckAsync();
        var retried = await check.CheckAsync();
        Assert(retried.Status == AppUpdateStatus.Downloaded && retried.CheckSucceeded && retried.AvailableVersion == "0.4.5"
            && !File.Exists(busy) && File.ReadAllBytes(changed).SequenceEqual(altered) && File.ReadAllBytes(current).SequenceEqual(keepCurrent)
            && File.ReadAllBytes(pending).SequenceEqual(nextBytes) && File.ReadAllText(unknown) == "untracked legacy package fixture"
            && File.ReadAllBytes(replaced).SequenceEqual(copied) && File.ReadAllBytes(movedOriginal).SequenceEqual(copied)
            && File.ReadAllText(foreign) == "foreign package fixture" && next.Downloads == 0,
            "cleanup missed its retry or modified current/pending/changed/foreign files");
    }

    private static async Task V040UnauthenticatedReceipts()
    {
        var directory = Path.Combine(_directory, "versioning040-untrusted-receipt");
        var initial = new TestVelopackLocator("YMusic.Native", "0.3.16", directory);
        var stored = initial.PackagesDir!;
        Directory.CreateDirectory(stored);
        var (downloader, bytes) = V040PackageDownloader();
        var name = V040GithubDownloader.PackageName;
        var archive = Path.Combine(stored, name);
        var ledger = Path.Combine(stored, "yamuse-package-receipts.dat");
        File.WriteAllBytes(archive, bytes);
        var adopted = await V040Service(directory, initial, downloader).CheckAsync();
        Assert(adopted.Status == AppUpdateStatus.Downloaded && File.Exists(ledger), "verified fixture was not recorded");

        var protectedReceipt = File.ReadAllBytes(ledger);
        FileSecurityPreservation.FileRevision identity;
        using (var file = File.OpenRead(archive)) identity = FileSecurityPreservation.GetPersistentRevision(file.SafeFileHandle)
            ?? throw new InvalidOperationException("fixture volume does not provide persistent file identity");
        var plain = JsonSerializer.SerializeToUtf8Bytes(new
        {
            Schema = 1,
            Packages = new[]
            {
                new { Version = "0.4.0", FileName = name, Size = bytes.LongLength, Sha256 = Convert.ToHexString(SHA256.HashData(bytes)),
                    Identity = new { identity.Volume, identity.Low, identity.High, identity.Created } }
            }
        });
        var later = new TestVelopackLocator("YMusic.Native", "0.4.1", directory);
        var service = V040Service(directory, later, downloader);
        foreach (var inadmissible in new[] { plain, ProtectedData.Protect(plain, [1, 2, 3], DataProtectionScope.CurrentUser) })
        {
            File.WriteAllBytes(ledger, inadmissible);
            var result = await service.CheckAsync();
            Assert(result.Status == AppUpdateStatus.NoUpdate && result.CheckSucceeded && File.ReadAllBytes(archive).SequenceEqual(bytes)
                && File.ReadAllBytes(ledger).SequenceEqual(inadmissible), "unprotected or wrongly scoped receipt authorized deletion");
        }

        File.WriteAllBytes(ledger, protectedReceipt);
        Assert((await service.CheckAsync()).CheckSucceeded && !File.Exists(archive), "authentic receipt did not authorize the exact archive");
    }

    private static async Task V040UnsupportedPackageIdentity()
    {
        var directory = Path.Combine(_directory, "versioning040-unsupported-package-id");
        var initial = new TestVelopackLocator("YMusic.Native", "0.3.16", directory);
        Directory.CreateDirectory(initial.PackagesDir!);
        var archive = Path.Combine(initial.PackagesDir!, V040GithubDownloader.PackageName);
        var ledger = Path.Combine(initial.PackagesDir!, "yamuse-package-receipts.dat");
        var (downloader, bytes) = V040PackageDownloader();
        File.WriteAllBytes(archive, bytes);
        var count = 0;
        FileSecurityPreservation.FileRevision? Unsupported(SafeFileHandle file) { count++; return null; }
        var first = await V040Service(directory, initial, downloader, packageRevision: Unsupported).CheckAsync();
        Assert(first.Status == AppUpdateStatus.Downloaded && first.CheckSucceeded && count == 1 && !File.Exists(ledger),
            "lacking a persistent ID prevented normal readiness or created an unproven receipt");

        var enrolled = await V040Service(directory, initial, downloader).CheckAsync();
        Assert(enrolled.Status == AppUpdateStatus.Downloaded && File.Exists(ledger), "supported fixture file was not enrolled");
        var later = new TestVelopackLocator("YMusic.Native", "0.4.1", directory);
        var ignored = await V040Service(directory, later, downloader, packageRevision: Unsupported).CheckAsync();
        Assert(ignored.Status == AppUpdateStatus.NoUpdate && ignored.CheckSucceeded && count == 2 && File.ReadAllBytes(archive).SequenceEqual(bytes),
            "receipt authorized retirement while the current persistent ID was unavailable");
        Assert((await V040Service(directory, later, downloader).CheckAsync()).CheckSucceeded && !File.Exists(archive),
            "safe retry did not retire the verified archive after the persistent ID was available");
    }

    private static async Task V040FullPackageLedger()
    {
        var directory = Path.Combine(_directory, "versioning040-full-package-ledger");
        var locator = new TestVelopackLocator("YMusic.Native", "0.3.16", directory);
        Directory.CreateDirectory(locator.PackagesDir!);
        var ledger = Path.Combine(locator.PackagesDir!, "yamuse-package-receipts.dat");
        var oldest = Path.Combine(locator.PackagesDir!, V040GithubDownloader.PackageName);
        var oldestBytes = V040PackageDownloader().Bytes;
        File.WriteAllBytes(oldest, oldestBytes);
        FileSecurityPreservation.FileRevision identity;
        using (var file = File.OpenRead(oldest)) identity = FileSecurityPreservation.GetPersistentRevision(file.SafeFileHandle)
            ?? throw new InvalidOperationException("fixture volume does not provide persistent file identity");

        var oldestHash = Convert.ToHexString(SHA256.HashData(oldestBytes));
        var fakeReceipts = Enumerable.Range(0, 64).Reverse().Select(number => new
        {
            Version = "0.4." + number,
            FileName = $"Yamuse-0.4.{number}-full.nupkg",
            Size = oldestBytes.Length,
            Sha256 = oldestHash,
            Identity = new { identity.Volume, identity.Low, identity.High, identity.Created }
        });
        var entropy = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("Yamuse.VerifiedUpdatePackages.1\0" + ledger.ToUpperInvariant()));
        var encoded = ProtectedData.Protect(JsonSerializer.SerializeToUtf8Bytes(new { Schema = 1, Packages = fakeReceipts }),
            entropy, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(ledger, encoded);

        var (later, bytes) = V040PackageDownloader("0.5.0");
        File.WriteAllBytes(Path.Combine(locator.PackagesDir!, "Yamuse-0.5.0-full.nupkg"), bytes);
        var result = await V040Service(directory, locator, later).CheckAsync();
        using var decoded = JsonDocument.Parse(ProtectedData.Unprotect(File.ReadAllBytes(ledger), entropy, DataProtectionScope.CurrentUser));
        var versions = decoded.RootElement.GetProperty("Packages").EnumerateArray()
            .Select(receipt => receipt.GetProperty("Version").GetString()).ToArray();
        Assert(result.Status == AppUpdateStatus.Downloaded && result.CheckSucceeded && versions.Length == 64 && versions.Contains("0.5.0")
            && !versions.Contains("0.4.0") && versions.Contains("0.4.1") && File.ReadAllBytes(oldest).SequenceEqual(oldestBytes),
            "ledger overflow refused the new package, forgot a different receipt or deleted the forgotten file");
    }

    private static UpdateSupport V040Service(string directory, TestVelopackLocator locator, V040GithubDownloader downloader,
        TimeSpan? metadataDeadline = null, Func<SafeFileHandle, FileSecurityPreservation.FileRevision?>? packageRevision = null)
    {
        Directory.CreateDirectory(directory);
        var feedPath = Path.Combine(directory, "update-feed.json");
        File.WriteAllText(feedPath, """{"githubRepository":"https://github.com/senduora/yamuse"}""");
        return new UpdateSupport(feedPath, installed: true, uri => new VelopackUpdateBackend(uri, locator,
            () => new FakeHandler(downloader.ReadMetadataAsync), () => new FakeHandler(downloader.ReadPackageAsync), metadataDeadline, packageRevision));
    }

    private static (V040GithubDownloader Downloader, byte[] Bytes) V040PackageDownloader(string version = "0.4.0")
    {
        using var content = new MemoryStream();
        using (var zip = new ZipArchive(content, ZipArchiveMode.Create, leaveOpen: true))
        {
            using (var manifest = new StreamWriter(zip.CreateEntry("YMusic.Native.nuspec").Open()))
                manifest.Write($"<package><metadata><id>YMusic.Native</id><version>{version}</version><authors>Yamuse fixture</authors><description>Fixture package</description></metadata></package>");
            using var updater = new StreamWriter(zip.CreateEntry("lib/app/Squirrel.exe").Open());
            updater.Write("fixture archive marker");
        }
        var bytes = content.ToArray();
        return (new V040GithubDownloader
        {
            Feed = V040Feed("YMusic.Native", version, Convert.ToHexString(SHA256.HashData(bytes)), bytes.Length),
            ReleaseVersion = version,
            DownloadVersion = version,
            PackageBytes = bytes
        }, bytes);
    }

    private static string V040Feed(string packageId, string version, string? hash, long size)
        => $$"""{"Assets":[{"PackageId":"{{packageId}}","Version":"{{version}}","Type":"Full","FileName":"Yamuse-{{version}}-full.nupkg","SHA1":"0000000000000000000000000000000000000000","SHA256":"{{hash}}","Size":{{size}}}]}""";

    private sealed class V040NoScanLocator : TestVelopackLocator, IProcessImpl
    {
        internal V040NoScanLocator(string directory) : base("YMusic.Native", "0.3.16", directory)
        {
            RootAppDir = Path.Combine(directory, "app-root");
            UpdateExePath = Path.Combine(RootAppDir, "Update.exe");
        }

        public override string RootAppDir { get; }
        public override string UpdateExePath { get; }
        public override IProcessImpl Process => this;
        internal bool FailStart { get; set; }
        internal int Starts { get; private set; }
        internal string? SeenUpdater { get; private set; }
        internal string[]? SeenArguments { get; private set; }
        void IProcessImpl.StartProcess(string executable, IEnumerable<string> arguments, string? workingDirectory, bool noShow)
        {
            Starts++;
            SeenUpdater = File.ReadAllText(executable);
            SeenArguments = arguments.ToArray();
            if (FailStart) throw new IOException("fixture launch failure");
        }
        public override VelopackAsset? GetLatestLocalFullPackage() => throw new InvalidOperationException("Unverified local packages were inspected");
        public override List<VelopackAsset> GetLocalPackages() => throw new InvalidOperationException("Unverified local packages were enumerated");
    }

    private sealed class V040CommitBackend(CancellationTokenSource cancellation) : IAppUpdateBackend
    {
        public string? PendingVersion { get; private set; }
        public Task<string?> CheckAsync(CancellationToken token) => Task.FromResult<string?>("0.4.0");
        public Task DownloadAsync(Action<int> progress, CancellationToken token)
        {
            PendingVersion = "0.4.0";
            cancellation.Cancel();
            return Task.CompletedTask;
        }
        public void ApplyAndRestart() => throw new InvalidOperationException("fixture never applies an update");
    }

    private sealed class V040GithubDownloader
    {
        internal const string Latest = "https://api.github.com/repos/senduora/yamuse/releases/latest";
        private const string ReleaseRoot = "https://github.com/senduora/yamuse/releases/download/";
        internal const string PackageName = "Yamuse-0.4.0-full.nupkg";
        internal string FeedUrl => $"{ReleaseRoot}v{ReleaseVersion}/releases.win.json";
        internal List<string> Requests { get; } = [];
        internal int Downloads { get; private set; }
        internal string Feed { get; set; } = V040Feed("YMusic.Native", "0.4.0", new string('0', 64), 1);
        internal string ReleaseVersion { get; set; } = "0.4.0";
        internal string DownloadVersion { get; init; } = "0.4.0";
        internal string? PackageTagOverride { get; set; }
        internal byte[]? PackageBytes { get; set; }
        internal bool Offline { get; set; }
        internal HttpStatusCode LatestStatus { get; set; } = HttpStatusCode.OK;
        internal string? QuotaHeader { get; set; }
        internal IOException? BodyFailure { get; set; }
        internal bool UntrustedRedirect { get; set; }
        internal Func<CancellationToken, Task>? BeforeFeed { get; set; }

        internal async Task<HttpResponseMessage> ReadMetadataAsync(HttpRequestMessage request, CancellationToken token)
        {
            var url = request.RequestUri!.AbsoluteUri;
            Requests.Add(url);
            if (Offline) throw new HttpRequestException("fixture offline");
            Assert(request.Method == HttpMethod.Get && request.Headers.Authorization is null, "public metadata request changed its trust policy");
            if (url == Latest)
            {
                if (LatestStatus != HttpStatusCode.OK)
                {
                    var response = Json("{}", LatestStatus);
                    if (QuotaHeader is { } header) response.Headers.TryAddWithoutValidation(header, header == "Retry-After" ? "30" : "0");
                    return response;
                }
                var name = $"Yamuse-{ReleaseVersion}-full.nupkg";
                var packageUrl = $"{ReleaseRoot}{PackageTagOverride ?? "v" + ReleaseVersion}/{name}";
                return Json($$"""{"tag_name":"v{{ReleaseVersion}}","draft":false,"prerelease":false,"assets":[{"name":"releases.win.json","browser_download_url":"{{FeedUrl}}"},{"name":"{{name}}","browser_download_url":"{{packageUrl}}"}]}""");
            }
            if (url.StartsWith("https://api.github.com/repos/senduora/yamuse/releases?", StringComparison.Ordinal))
                return Json("[" + string.Join(",", Enumerable.Repeat("""{"name":"preview","prerelease":true}""", 10)) + "]");
            if (url != FeedUrl) return Json("{}", HttpStatusCode.ServiceUnavailable);
            if (BeforeFeed is { } pause) await pause(token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            return BodyFailure is { } error ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new V040InterruptedStream(error)) } : Json(Feed);
        }

        internal Task<HttpResponseMessage> ReadPackageAsync(HttpRequestMessage request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var url = request.RequestUri?.AbsoluteUri;
            var name = $"Yamuse-{DownloadVersion}-full.nupkg";
            var packageUrl = $"{ReleaseRoot}v{DownloadVersion}/{name}";
            var cdnUrl = $"https://release-assets.githubusercontent.com/fixture/{name}";
            if (url == packageUrl)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Redirect)
                {
                    Headers = { Location = new Uri(UntrustedRedirect ? "https://example.invalid/fixture" : cdnUrl) }
                });
            }
            var bytes = PackageBytes ?? throw new InvalidOperationException("fixture package is missing");
            Assert(url == cdnUrl && request.Headers.Authorization is null, "unexpected package request");
            Downloads++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
        }
    }

    private sealed class V040InterruptedStream(IOException error) : MemoryStream
    {
        private bool _started;
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_started) return ValueTask.FromException<int>(error);
            _started = true;
            buffer.Span[0] = (byte)'{';
            return ValueTask.FromResult(1);
        }
    }
}
