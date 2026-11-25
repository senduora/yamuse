using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
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
        await Run("Versioning040: published asset metadata binds both the index and full package", V040PublishedAssetMetadata);
        await Run("Versioning040: a pending package requires official metadata and matching bytes before restart", V040PendingTrust);
        await Run("Versioning040: replacing an invalid cached package preserves or refuses its metadata", V040CacheReplacement);
        await Run("Versioning040: rechecking a pending package observes new and withdrawn offers", V040PendingRefresh);
        await Run("Versioning040: a bounded secondary-rate-limit response preserves a verified pending package", V040SecondaryRateLimit);
        await Run("Versioning040: repeated checks respect GitHub quota cooldown and retain explicit apply", V040LatestRateLimitCooldown);
        await Run("Versioning040: consecutive secondary limits increase the restored cooldown until success", V040SecondaryBackoff);
        await Run("Versioning040: canceling an installed recheck preserves the previous download selection", V040CanceledSelection);
        await Run("Versioning040: canceling after the committed download retains installed readiness", V040CommittedDownload);
        await Run("Versioning040: explicit apply atomically installs the verified updater and rolls back a failed handoff", V040UpdaterApply);
        await Run("Versioning040: the Velopack fallback updater retains the original install root", V040FallbackUpdater);
        await Run("Versioning040: explicit apply recreates a missing pinned fallback updater after a refusal", V040MissingFallbackUpdater);
        await Run("Versioning040: updater replacement preserves unsupported metadata and leaves identical updater in place", V040UpdaterMetadata);
        await Run("Versioning040: ready update does not wait for older package maintenance", V040MaintenanceDoesNotDelayUpdate);
        await Run("Versioning040: package receipt survives immediate updater handoff during maintenance", V040ReceiptBeforeHandoff);
        await Run("Versioning040: unavailable receipt storage preserves the usable package and permits retry", V040ReceiptWriteFailure);
        await Run("Versioning040: cleanup retires only verified older archives and retries a locked package", V040TrustedPackageCleanup);
        await Run("Versioning040: cleanup rotates exact receipts after late archive deletion refusals", V040LateRetirementRefusal);
        await Run("Versioning040: canceled older-package hashing stops at the next block and preserves its receipt", V040CanceledPackageCleanup);
        await Run("Versioning040: cleanup requires a locally authenticated package receipt", V040UnauthenticatedReceipts);
        await Run("Versioning040: only authenticated damaged receipts are archived and reinitialized", V040DamagedReceiptRecovery);
        await Run("Versioning040: unsupported persistent file identity skips both package registration and retirement", V040UnsupportedPackageIdentity);
        await Run("Versioning040: a full package ledger replaces its oldest receipt without deleting its file", V040FullPackageLedger);
    }

    private static Task V040Metadata()
    {
        var assembly = typeof(UpdateSupport).Assembly;
        const string expected = "0.9.2";
        var numericVersion = new Version(expected + ".0");
        Assert(assembly.GetName().Version == numericVersion, "numeric assembly identity drifted");
        Assert(assembly.GetCustomAttribute<AssemblyFileVersionAttribute>()?.Version == numericVersion.ToString(), "numeric file identity drifted");
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

        var directory = Path.Combine(_directory, "versioning040-portable-quota");
        Directory.CreateDirectory(directory);
        var feed = Path.Combine(directory, "update-feed.json");
        File.WriteAllText(feed, """{"githubRepository":"https://github.com/senduora/yamuse"}""");
        var clock = new V040UpdateClock();
        var downloader = new V040GithubDownloader { LatestStatus = HttpStatusCode.TooManyRequests };
        var service = new UpdateSupport(feed, installed: false, _ => throw new InvalidOperationException("fixture created an installed backend"),
            token => UpdateSupport.CheckPortableReleaseAsync("0.4.0-preview.1", token, new FakeHandler(downloader.ReadMetadataAsync)), clock: clock);
        var limited = await service.CheckAsync();
        downloader.LatestStatus = HttpStatusCode.OK;
        Assert(limited.Status == AppUpdateStatus.Error && !limited.CheckSucceeded
            && await service.CheckAsync() == limited && downloader.Requests.Count == 1,
            "portable checks did not share the official Latest cooldown");
        clock.Advance(TimeSpan.FromMinutes(1));
        var resumed = await service.CheckAsync();
        Assert(resumed.Status == AppUpdateStatus.Available && resumed.AvailableVersion == "0.4.0" && resumed.CheckSucceeded
            && downloader.Requests.Count == 2, "portable check did not resume after the official Latest cooldown");
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

    private static async Task V040PublishedAssetMetadata()
    {
        var directory = Path.Combine(_directory, "versioning040-published-asset-metadata");
        var locator = new TestVelopackLocator("YMusic.Native", "0.3.16", directory);
        var (downloader, bytes) = V040PackageDownloader();
        var feedBytes = System.Text.Encoding.UTF8.GetBytes(downloader.Feed);
        var feedDigest = Convert.ToHexString(SHA256.HashData(feedBytes));
        var packageDigest = Convert.ToHexString(SHA256.HashData(bytes));
        static string Fields(long size, string digest) => $",\"state\":\"uploaded\",\"size\":{size},\"digest\":\"sha256:{digest}\"";
        downloader.FeedFields = Fields(feedBytes.Length, feedDigest);
        downloader.PackageFields = Fields(bytes.Length, packageDigest);
        var verified = V040Service(directory, locator, downloader);
        Assert((await verified.CheckAsync()).Status == AppUpdateStatus.Available
            && (await verified.DownloadAsync()).Status == AppUpdateStatus.Downloaded, "matching GitHub asset metadata blocked the update");
        await V040AwaitMaintenance(verified);

        var nulls = """, "size":null, "digest":null, "state":null""";
        downloader.FeedFields = downloader.PackageFields = nulls;
        Assert((await V040CheckAndMaintain(V040Service(directory, locator, downloader))).Status == AppUpdateStatus.Downloaded,
            "legacy null asset fields blocked an official package");

        var wrongHash = new string('0', 64);
        foreach (var (index, package) in new[]
        {
            (Fields(feedBytes.Length + 1, feedDigest), nulls), (Fields(feedBytes.Length, wrongHash), nulls),
            (nulls, Fields(bytes.Length + 1, packageDigest)), (nulls, Fields(bytes.Length, wrongHash)),
            (",\"state\":\"open\"", nulls), (nulls, """, "state":true"""),
            (",\"size\":\"123\"", nulls), (nulls, """, "size":-1"""),
            (nulls, ",\"digest\":\"sha1:00\"")
        })
        {
            downloader.FeedFields = index;
            downloader.PackageFields = package;
            var rejected = await V040Service(directory, locator, downloader).CheckAsync();
            Assert(rejected.Status == AppUpdateStatus.Error && rejected.AvailableVersion is null && downloader.Downloads == 1,
                "mismatched or malformed GitHub asset fields authorized a package");
        }
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
        var restored = await V040CheckAndMaintain(service);
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
        await V040AwaitMaintenance(service);
        Assert(repaired.Status == AppUpdateStatus.Downloaded && downloader.Downloads == 2 && File.ReadAllBytes(path).SequenceEqual(officialBytes)
            && File.ReadAllText(updater) == "fixture updater before download",
            "explicit download failed to replace the unverified cached bytes");

        downloader.Offline = true;
        var restarted = V040Service(directory, locator, downloader);
        var offline = await restarted.CheckAsync();
        Assert(offline.Status == AppUpdateStatus.Error && offline.AvailableVersion is null && downloader.Downloads == 2,
            "an offline check presented a pending package without trusted metadata");
    }

    private static async Task V040CacheReplacement()
    {
        var directory = Path.Combine(_directory, "versioning040-cached-package-replacement");
        var locator = new TestVelopackLocator("YMusic.Native", "0.3.16", directory);
        Directory.CreateDirectory(locator.PackagesDir!);
        var path = Path.Combine(locator.PackagesDir!, V040GithubDownloader.PackageName);
        var (downloader, official) = V040PackageDownloader();
        var bad = official.ToArray();
        bad[0] ^= 1;
        File.WriteAllBytes(path, bad);
        var service = V040Service(directory, locator, downloader);
        Assert((await service.CheckAsync()).Status == AppUpdateStatus.Available, "repair fixture did not select the package");

        if (new DriveInfo(Path.GetPathRoot(directory)!).DriveFormat == "NTFS")
        {
            var stream = path + ":YamuseFixture";
            File.WriteAllText(stream, "cached package metadata fixture");
            Assert((await service.DownloadAsync()).Status == AppUpdateStatus.Error && File.ReadAllBytes(path).SequenceEqual(bad)
                && File.ReadAllText(stream) == "cached package metadata fixture", "repair removed the cached package's named stream");
            File.Delete(stream);
        }

        File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.Hidden);
        var attributes = File.GetAttributes(path);
        using (var reader = File.OpenRead(path))
            Assert((await service.DownloadAsync()).Status == AppUpdateStatus.Error && File.ReadAllBytes(path).SequenceEqual(bad)
                && File.GetAttributes(path) == attributes, "blocked replacement removed or modified the held cache entry");
        var repaired = await service.DownloadAsync();
        await V040AwaitMaintenance(service);
        Assert(repaired.Status == AppUpdateStatus.Downloaded && File.ReadAllBytes(path).SequenceEqual(official)
            && File.GetAttributes(path) == attributes && !Directory.EnumerateFiles(locator.PackagesDir!, "*.tmp").Any(),
            "atomic cache repair did not preserve supported attributes or left a staged file");
    }

    private static async Task V040PendingRefresh()
    {
        var directory = Path.Combine(_directory, "versioning040-pending-refresh");
        var locator = new TestVelopackLocator("YMusic.Native", "0.3.16", directory);
        var (downloader, bytes) = V040PackageDownloader();
        Directory.CreateDirectory(locator.PackagesDir!);
        File.WriteAllBytes(Path.Combine(locator.PackagesDir!, V040GithubDownloader.PackageName), bytes);
        var clock = new V040UpdateClock();
        var service = V040Service(directory, locator, downloader, TimeSpan.FromSeconds(1), clock: clock);
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
            clock.Advance(TimeSpan.FromMinutes(1));
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
        var completeFeed = V040Feed("YMusic.Native", "0.4.1", new string('0', 64), 1);
        const string starter = """, "state":"starter", "size":0, "digest":null""";
        void RestoreCompleteFeed()
        {
            downloader.Feed = completeFeed;
            downloader.OmitFeedAsset = downloader.OmitPackageAsset = false;
            downloader.FeedFields = downloader.PackageFields = "";
            downloader.FeedStatus = HttpStatusCode.OK;
        }
        foreach (var incomplete in new Action[]
        {
            () => downloader.Feed = """{"Assets":[]}""",
            () => downloader.Feed = completeFeed.Replace("\"Full\"", "\"Delta\"", StringComparison.Ordinal),
            () => downloader.OmitFeedAsset = true,
            () => downloader.OmitPackageAsset = true,
            () => downloader.FeedFields = starter,
            () => downloader.PackageFields = starter,
            () => downloader.FeedStatus = HttpStatusCode.NotFound
        })
        {
            RestoreCompleteFeed();
            incomplete();
            var warm = await service.CheckAsync();
            Assert(warm.Status == AppUpdateStatus.Downloaded && warm.AvailableVersion == "0.4.0" && !warm.CheckSucceeded,
                "a newer incomplete feed discarded the verified pending archive or counted as a success");
            var cold = await V040Service(directory, locator, downloader).CheckAsync();
            Assert(cold.Status == AppUpdateStatus.Error && !cold.CheckSucceeded && cold.AvailableVersion is null,
                "a newer incomplete feed reported the installed version as current");
        }
        RestoreCompleteFeed();
        var updated = await service.CheckAsync();
        Assert(updated.Status == AppUpdateStatus.Available && updated.AvailableVersion == "0.4.1" && updated.CheckSucceeded,
            "a pending package masked the newer official offer");

        downloader.ReleaseVersion = "0.4.0";
        downloader.Feed = V040Feed("YMusic.Native", "0.4.0", Convert.ToHexString(SHA256.HashData(bytes)), bytes.Length);
        Assert((await service.CheckAsync()).Status == AppUpdateStatus.Downloaded, "fixture could not restore the old pending state");
        downloader.OmitPackageAsset = true;
        var missing = await service.CheckAsync();
        await Throws<InvalidOperationException>(() => { service.ApplyAndRestart(); return Task.CompletedTask; });
        Assert(missing.Status == AppUpdateStatus.Error && !missing.CheckSucceeded && missing.AvailableVersion is null,
            "removing an asset from the same release let the withdrawn archive be applied");
        downloader.OmitPackageAsset = false;
        Assert((await service.CheckAsync()).Status == AppUpdateStatus.Downloaded, "the restored asset was not revalidated");
        downloader.LatestStatus = HttpStatusCode.NotFound;
        var withdrawn = await service.CheckAsync();
        await Throws<InvalidOperationException>(() => { service.ApplyAndRestart(); return Task.CompletedTask; });
        Assert(withdrawn.Status == AppUpdateStatus.NoUpdate && withdrawn.AvailableVersion is null && withdrawn.CheckSucceeded
            && locator.ExitCode is null && downloader.Downloads == 0, "a withdrawn package remained available to apply");
    }

    private static async Task V040SecondaryRateLimit()
    {
        var directory = Path.Combine(_directory, "versioning040-secondary-limit");
        var locator = new TestVelopackLocator("YMusic.Native", "0.3.16", directory);
        var (downloader, bytes) = V040PackageDownloader();
        Directory.CreateDirectory(locator.PackagesDir!);
        var archive = Path.Combine(locator.PackagesDir!, V040GithubDownloader.PackageName);
        File.WriteAllBytes(archive, bytes);
        var clock = new V040UpdateClock();
        var service = V040Service(directory, locator, downloader, TimeSpan.FromSeconds(1), clock: clock);
        const string throttled = """{"message":"You have exceeded a secondary rate limit. Please wait a few minutes before you try again.","status":"403"}""";

        async Task CheckForbidden(Func<HttpContent> content, AppUpdateStatus expected)
        {
            downloader.LatestStatus = HttpStatusCode.OK;
            Assert((await service.CheckAsync()).Status == AppUpdateStatus.Downloaded, "quota fixture did not verify the pending archive");
            downloader.LatestStatus = HttpStatusCode.Forbidden;
            downloader.RemainingQuota = "42";
            downloader.LatestErrorContent = content;
            var result = await service.CheckAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert(result.Status == expected && !result.CheckSucceeded
                && result.AvailableVersion == (expected == AppUpdateStatus.Downloaded ? "0.4.0" : null)
                && File.ReadAllBytes(archive).SequenceEqual(bytes), "403 response misclassified the verified archive");
        }

        await CheckForbidden(() => new StringContent(throttled), AppUpdateStatus.Downloaded);
        clock.Advance(TimeSpan.FromMinutes(1));
        await CheckForbidden(() => new StringContent("""{"message":"Resource not accessible","detail":"You have exceeded a secondary rate limit."}"""),
            AppUpdateStatus.Error);
        await CheckForbidden(() => new StringContent(throttled + new string(' ', 17 * 1024)), AppUpdateStatus.Error);
        await CheckForbidden(() => new StreamContent(new V040WaitingBody()), AppUpdateStatus.Downloaded);
    }

    private static async Task V040LatestRateLimitCooldown()
    {
        var directory = Path.Combine(_directory, "versioning040-github-check-cooldown");
        var locator = new V040NoScanLocator(directory);
        var (downloader, bytes) = V040PackageDownloader();
        Directory.CreateDirectory(locator.PackagesDir!);
        Directory.CreateDirectory(locator.RootAppDir);
        File.WriteAllBytes(Path.Combine(locator.PackagesDir!, V040GithubDownloader.PackageName), bytes);
        File.WriteAllText(locator.UpdateExePath, "fixture archive marker");
        var clock = new V040UpdateClock();
        var service = V040Service(directory, locator, downloader, clock: clock);
        Assert((await service.CheckAsync()).Status == AppUpdateStatus.Downloaded, "quota fixture did not locate the verified package");

        async Task VerifyCooldown(HttpStatusCode status, int seconds, RetryConditionHeaderValue? retryAfter = null,
            string? remaining = null, string? reset = null, string? body = null, bool apply = false)
        {
            downloader.LatestStatus = status;
            downloader.RetryAfter = retryAfter;
            downloader.RemainingQuota = remaining;
            downloader.QuotaReset = reset;
            downloader.LatestErrorContent = body is not null ? () => new StringContent(body) : null;
            var limited = await service.CheckAsync();
            var requests = downloader.Requests.Count;
            Assert(limited.Status == AppUpdateStatus.Downloaded && limited.AvailableVersion == "0.4.0" && !limited.CheckSucceeded,
                "official quota response removed the downloaded update");

            downloader.LatestStatus = HttpStatusCode.OK;
            var skipped = await service.CheckAsync();
            clock.Advance(TimeSpan.FromSeconds(seconds) - TimeSpan.FromTicks(1));
            var tooEarly = await service.CheckAsync();
            Assert(skipped == limited && tooEarly == limited && downloader.Requests.Count == requests,
                "early or repeated checks reached GitHub during the cooldown");
            if (apply)
            {
                locator.FailStart = true;
                await Throws<InvalidOperationException>(() => { service.ApplyAndRestart(); return Task.CompletedTask; });
                Assert(locator.Starts == 1 && locator.ExitCode is null && service.State.Status == AppUpdateStatus.Downloaded
                    && service.State.AvailableVersion == "0.4.0" && !service.State.CheckSucceeded && downloader.Requests.Count == requests,
                    "a failed handoff lost the revalidated package during the cooldown");
                Assert(await service.CheckAsync() == limited && downloader.Requests.Count == requests,
                    "a manual check after a failed handoff contacted GitHub or hid the verified package");
                locator.FailStart = false;
                service.ApplyAndRestart();
                Assert(locator.Starts == 2 && locator.ExitCode == 0 && downloader.Requests.Count == requests,
                    "quota cooldown prevented retrying the explicit apply of verified local bytes");
            }
            clock.Advance(TimeSpan.FromTicks(1));
            var resumed = await service.CheckAsync();
            Assert(resumed.Status == AppUpdateStatus.Downloaded && resumed.CheckSucceeded && downloader.Requests.Count == requests + 2,
                "official checks did not resume at the expiry of the applicable cooldown");
        }

        await VerifyCooldown(HttpStatusCode.Forbidden, 35, new RetryConditionHeaderValue(TimeSpan.FromSeconds(35)));
        await VerifyCooldown(HttpStatusCode.TooManyRequests, 25, new RetryConditionHeaderValue(clock.GetUtcNow().AddSeconds(25)));
        await VerifyCooldown(HttpStatusCode.TooManyRequests, 60);
        await VerifyCooldown(HttpStatusCode.Forbidden, 90, new RetryConditionHeaderValue(TimeSpan.FromSeconds(30)), "0",
            clock.GetUtcNow().AddSeconds(90).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture));
        await VerifyCooldown(HttpStatusCode.Forbidden, 60, remaining: "35",
            reset: clock.GetUtcNow().AddHours(1).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture),
            body: """{"message":"You have exceeded a secondary rate limit."}""", apply: true);

        downloader.LatestStatus = HttpStatusCode.NotFound;
        Assert((await service.CheckAsync()).Status == AppUpdateStatus.NoUpdate,
            "an official withdrawal after the cooldown kept the prepared release selected");
        await Throws<InvalidOperationException>(() => { service.ApplyAndRestart(); return Task.CompletedTask; });
        Assert(locator.Starts == 2, "an official withdrawal allowed another updater handoff");

        downloader.LatestStatus = HttpStatusCode.OK;
        Assert((await service.CheckAsync()).Status == AppUpdateStatus.Downloaded, "fixture could not restore the verified package");
        downloader.LatestStatus = HttpStatusCode.TooManyRequests;
        Assert((await service.CheckAsync()).Status == AppUpdateStatus.Downloaded, "fixture could not start a new cooldown");
        var requests = downloader.Requests.Count;
        locator.FailStart = true;
        await Throws<InvalidOperationException>(() => { service.ApplyAndRestart(); return Task.CompletedTask; });
        var changed = bytes.ToArray();
        changed[0] ^= 1;
        File.WriteAllBytes(Path.Combine(locator.PackagesDir!, V040GithubDownloader.PackageName), changed);
        await Throws<InvalidOperationException>(() => { service.ApplyAndRestart(); return Task.CompletedTask; });
        Assert(service.State.Status == AppUpdateStatus.Error && (await service.CheckAsync()).Status == AppUpdateStatus.Error
            && locator.Starts == 3 && downloader.Requests.Count == requests,
            "local revalidation failure during the cooldown retained a stale apply action or started a request");
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

    private static async Task V040SecondaryBackoff()
    {
        var directory = Path.Combine(_directory, "versioning040-secondary-backoff");
        var locator = new TestVelopackLocator("YMusic.Native", "0.3.16", directory);
        var (downloader, _) = V040PackageDownloader();
        var clock = new V040UpdateClock();
        var first = V040Service(directory, locator, downloader, clock: clock);
        downloader.LatestStatus = HttpStatusCode.Forbidden;
        downloader.RemainingQuota = "99";
        downloader.LatestErrorContent = () => new StringContent("""{"message":"You have exceeded a secondary rate limit."}""");
        Assert(!(await first.CheckAsync()).CheckSucceeded && first.RateLimitCheckpoint?.SecondaryLimitCount == 1
            && first.RemainingRateLimit == TimeSpan.FromMinutes(1), "initial secondary limit did not record the default pause");

        var resumed = V040Service(directory, locator, downloader, clock: clock);
        resumed.RestoreRateLimit(first.RateLimitCheckpoint);
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert(!(await resumed.CheckAsync()).CheckSucceeded && resumed.RateLimitCheckpoint?.SecondaryLimitCount == 2
            && resumed.RemainingRateLimit == TimeSpan.FromMinutes(2), "a repeated secondary limit did not increase the restored pause");
        clock.Advance(TimeSpan.FromMinutes(1));
        await resumed.CheckAsync();
        Assert(downloader.Requests.Count == 2, "a check reached GitHub before the end of the longer pause");

        clock.Advance(TimeSpan.FromMinutes(1));
        downloader.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromMinutes(5));
        Assert(!(await resumed.CheckAsync()).CheckSucceeded && resumed.RateLimitCheckpoint?.SecondaryLimitCount == 3
            && resumed.RemainingRateLimit == TimeSpan.FromMinutes(5), "the exponential pause shortened the minimum sent by GitHub");
        clock.Advance(TimeSpan.FromMinutes(5));
        downloader.LatestStatus = HttpStatusCode.OK;
        Assert((await resumed.CheckAsync()).CheckSucceeded && resumed.RateLimitCheckpoint is null, "successful check did not clear the secondary streak");
        downloader.LatestStatus = HttpStatusCode.Forbidden;
        downloader.RetryAfter = null;
        Assert(!(await resumed.CheckAsync()).CheckSucceeded && resumed.RateLimitCheckpoint?.SecondaryLimitCount == 1
            && resumed.RemainingRateLimit == TimeSpan.FromMinutes(1), "a new secondary limit inherited the cleared streak");
        clock.Advance(TimeSpan.FromMinutes(1));
        downloader.LatestStatus = HttpStatusCode.OK;
        Assert((await resumed.CheckAsync()).CheckSucceeded, "fixture could not clear the first streak");
        downloader.LatestStatus = HttpStatusCode.Forbidden;
        downloader.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromMinutes(10));
        await resumed.CheckAsync();
        var restarted = V040Service(directory, locator, downloader, clock: clock);
        restarted.RestoreRateLimit(resumed.RateLimitCheckpoint);
        clock.Advance(TimeSpan.FromMinutes(10));
        downloader.RetryAfter = null;
        await restarted.CheckAsync();
        var attempts = downloader.Requests.Count;
        clock.Advance(TimeSpan.FromMinutes(2));
        await restarted.CheckAsync();
        Assert(downloader.Requests.Count == attempts && restarted.RateLimitCheckpoint?.SecondaryLimitDelay == TimeSpan.FromMinutes(20)
            && restarted.RemainingRateLimit == TimeSpan.FromMinutes(18), "another secondary limit shortened a previous server-supplied wait after restart");
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

    private static async Task V040FallbackUpdater()
    {
        var directory = Path.Combine(_directory, "versioning040-fallback-updater");
        var localAppData = Path.Combine(directory, "local-appdata-fixture");
        var locator = new V040NoScanLocator(directory, localAppData);
        Directory.CreateDirectory(locator.PackagesDir!);
        Directory.CreateDirectory(locator.RootAppDir);
        var primary = Path.Combine(locator.RootAppDir, "Update.exe");
        File.WriteAllText(primary, "original primary updater fixture");
        File.WriteAllText(locator.UpdateExePath, "original fallback updater fixture");
        var (downloader, bytes) = V040PackageDownloader();
        var package = Path.Combine(locator.PackagesDir!, V040GithubDownloader.PackageName);
        File.WriteAllBytes(package, bytes);

        var rejected = V040Service(directory, locator, downloader, localAppDataFolder: () => Path.Combine(directory, "different-anchor"));
        Assert((await rejected.CheckAsync()).Status == AppUpdateStatus.Downloaded, "fallback fixture did not restore the pending package");
        await Throws<InvalidOperationException>(() => { rejected.ApplyAndRestart(); return Task.CompletedTask; });
        Assert(locator.Starts == 0 && File.ReadAllText(locator.UpdateExePath) == "original fallback updater fixture",
            "an unanchored fallback updater reached the handoff");

        var accepted = V040Service(directory, locator, downloader, localAppDataFolder: () => localAppData);
        Assert((await accepted.CheckAsync()).Status == AppUpdateStatus.Downloaded, "anchored fallback lost the pending package");
        accepted.ApplyAndRestart();
        Assert(locator.Starts == 1 && locator.ExitCode == 0 && locator.SeenExecutable == locator.UpdateExePath
            && locator.SeenArguments is { } arguments && V040HasArgument(arguments, "--rootDir", locator.RootAppDir)
            && V040HasArgument(arguments, "--package", package) && File.ReadAllText(primary) == "original primary updater fixture"
            && File.ReadAllText(locator.UpdateExePath) == "fixture archive marker"
            && !Directory.EnumerateFiles(Path.GetDirectoryName(locator.UpdateExePath)!, "*.tmp").Any(),
            "fallback handoff changed the SDK installation root, package path or primary updater");
    }

    private static async Task V040MissingFallbackUpdater()
    {
        var directory = Path.Combine(_directory, "versioning040-missing-fallback-updater");
        var localAppData = Path.Combine(directory, "local-appdata-fixture");
        var locator = new V040NoScanLocator(directory, localAppData);
        Directory.CreateDirectory(locator.PackagesDir!);
        Directory.CreateDirectory(locator.RootAppDir);
        var primary = Path.Combine(locator.RootAppDir, "Update.exe");
        File.WriteAllText(primary, "original primary updater fixture");
        var (downloader, bytes) = V040PackageDownloader();
        var package = Path.Combine(locator.PackagesDir!, V040GithubDownloader.PackageName);
        File.WriteAllBytes(package, bytes);
        var service = V040Service(directory, locator, downloader, localAppDataFolder: () => localAppData);
        Assert(!File.Exists(locator.UpdateExePath) && (await service.CheckAsync()).Status == AppUpdateStatus.Downloaded,
            "missing fallback fixture did not prepare the update");

        Directory.CreateDirectory(locator.UpdateExePath);
        await Throws<InvalidOperationException>(() => { service.ApplyAndRestart(); return Task.CompletedTask; });
        Assert(Directory.Exists(locator.UpdateExePath) && locator.Starts == 0 && locator.ExitCode is null,
            "fallback recovery overwrote an occupied path or started an updater before admission");
        Directory.Delete(locator.UpdateExePath);

        Assert((await service.CheckAsync()).Status == AppUpdateStatus.Downloaded && !File.Exists(locator.UpdateExePath),
            "retry did not reuse the verified archive or modified the updater before explicit apply");
        service.ApplyAndRestart();
        Assert(locator.Starts == 1 && locator.ExitCode == 0 && locator.SeenExecutable == locator.UpdateExePath
            && locator.SeenUpdater == "fixture archive marker" && File.ReadAllText(locator.UpdateExePath) == "fixture archive marker"
            && locator.SeenArguments is { } arguments && V040HasArgument(arguments, "--rootDir", locator.RootAppDir)
            && V040HasArgument(arguments, "--package", package) && File.ReadAllText(primary) == "original primary updater fixture"
            && !Directory.EnumerateFiles(Path.GetDirectoryName(locator.UpdateExePath)!, "*.tmp").Any(),
            "explicit retry did not publish and use the pinned verified updater with the original installation root");
    }

    private static bool V040HasArgument(string[] arguments, string name, string value)
    {
        var index = Array.IndexOf(arguments, name);
        return index >= 0 && index + 1 < arguments.Length && arguments[index + 1] == value;
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

    private static async Task V040MaintenanceDoesNotDelayUpdate()
    {
        var directory = Path.Combine(_directory, "versioning040-background-package-cleanup");
        var initial = new TestVelopackLocator("YMusic.Native", "0.3.16", directory);
        var packages = initial.PackagesDir!;
        Directory.CreateDirectory(packages);
        var olderPath = Path.Combine(packages, V040GithubDownloader.PackageName);
        var (older, olderBytes) = V040PackageDownloader();
        File.WriteAllBytes(olderPath, olderBytes);
        Assert((await V040CheckAndMaintain(V040Service(directory, initial, older))).Status == AppUpdateStatus.Downloaded,
            "older package could not be verified and recorded");

        var readyPath = Path.Combine(packages, "Yamuse-0.4.2-full.nupkg");
        var (latest, readyBytes) = V040PackageDownloader("0.4.2");
        File.WriteAllBytes(readyPath, readyBytes);
        var nextPath = Path.Combine(packages, "Yamuse-0.4.3-full.nupkg");
        var (nextRelease, nextBytes) = V040PackageDownloader("0.4.3");
        var thirdPath = Path.Combine(packages, "Yamuse-0.4.4-full.nupkg");
        var (thirdRelease, thirdBytes) = V040PackageDownloader("0.4.4");
        var later = new TestVelopackLocator("YMusic.Native", "0.4.1", directory);
        var ledger = Path.Combine(packages, "yamuse-package-receipts.dat");
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var reads = 0;
        byte[] HoldOlderRead(Stream file, CancellationToken token)
        {
            Interlocked.Increment(ref reads);
            started.TrySetResult();
            if (!release.Wait(TimeSpan.FromSeconds(10), token)) throw new TimeoutException("older archive read was never released");
            return VelopackUpdateBackend.HashRetiredPackage(file, token);
        }

        var service = V040Service(directory, later, latest, retiredPackageHash: HoldOlderRead);
        try
        {
            var check = service.CheckAsync();
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var ready = await check.WaitAsync(TimeSpan.FromSeconds(3));
            Assert(ready.Status == AppUpdateStatus.Downloaded && ready.CheckSucceeded && ready.AvailableVersion == "0.4.2"
                && File.Exists(olderPath), "checking a ready update waited for retirement of the older archive");
            for (var index = 0; index < 3; index++)
                Assert((await service.CheckAsync().WaitAsync(TimeSpan.FromSeconds(3))).Status == AppUpdateStatus.Downloaded,
                    "checking again while retirement was active blocked update readiness");
            latest.ReleaseVersion = nextRelease.ReleaseVersion;
            latest.Feed = nextRelease.Feed;
            var offer = await service.CheckAsync().WaitAsync(TimeSpan.FromSeconds(3));
            Assert(offer.Status == AppUpdateStatus.Available && offer.AvailableVersion == "0.4.3" && offer.CheckSucceeded,
                "a new official offer waited for active retirement");
            File.WriteAllBytes(nextPath, nextBytes);
            using (var checkLifetime = new CancellationTokenSource())
                Assert((await service.CheckAsync(checkLifetime.Token).WaitAsync(TimeSpan.FromSeconds(3))).Status == AppUpdateStatus.Downloaded,
                    "the newer cached package waited for active retirement");
            latest.ReleaseVersion = thirdRelease.ReleaseVersion;
            latest.Feed = thirdRelease.Feed;
            File.WriteAllBytes(thirdPath, thirdBytes);
            using (var checkLifetime = new CancellationTokenSource())
                Assert((await service.CheckAsync(checkLifetime.Token).WaitAsync(TimeSpan.FromSeconds(3))).Status == AppUpdateStatus.Downloaded,
                    "the third cached package waited for active retirement");
            Assert(V040ReceiptVersions(ledger).SequenceEqual(new[] { "0.4.0", "0.4.2", "0.4.3", "0.4.4" }),
                "a later check displaced an earlier verified receipt while retirement was active");
            Assert(Volatile.Read(ref reads) == 1 && File.ReadAllBytes(readyPath).SequenceEqual(readyBytes),
                "rechecks started another retirement read or altered the selected archive");
        }
        finally
        {
            release.Set();
            await V040AwaitMaintenance(service);
        }

        Assert(!File.Exists(olderPath) && File.ReadAllBytes(readyPath).SequenceEqual(readyBytes) && reads == 1
            && File.ReadAllBytes(nextPath).SequenceEqual(nextBytes) && File.ReadAllBytes(thirdPath).SequenceEqual(thirdBytes)
            && V040ReceiptVersions(ledger).SequenceEqual(new[] { "0.4.2", "0.4.3", "0.4.4" }),
            "background retirement rewrote newer receipts or did not remove the retired archive exactly once");
    }

    private static async Task V040ReceiptBeforeHandoff()
    {
        var directory = Path.Combine(_directory, "versioning040-receipt-handoff");
        var initial = new TestVelopackLocator("YMusic.Native", "0.3.16", directory);
        var packages = initial.PackagesDir!;
        Directory.CreateDirectory(packages);
        var olderPath = Path.Combine(packages, V040GithubDownloader.PackageName);
        var ledger = Path.Combine(packages, "yamuse-package-receipts.dat");
        var (older, olderBytes) = V040PackageDownloader();
        File.WriteAllBytes(olderPath, olderBytes);
        Assert((await V040CheckAndMaintain(V040Service(directory, initial, older))).Status == AppUpdateStatus.Downloaded,
            "handoff fixture could not record the older archive");

        using var lifetime = new CancellationTokenSource();
        using var release = new ManualResetEventSlim();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        byte[] HoldOlderRead(Stream file, CancellationToken token)
        {
            started.TrySetResult();
            if (!release.Wait(TimeSpan.FromSeconds(10), token)) throw new TimeoutException("older archive read was never released");
            return VelopackUpdateBackend.HashRetiredPackage(file, token);
        }

        string?[]? handedOffReceipts = null;
        var locator = new V040NoScanLocator(directory, current: "0.4.1")
        {
            BeforeStart = () => handedOffReceipts = V040ReceiptVersions(ledger)
        };
        Directory.CreateDirectory(locator.RootAppDir);
        File.WriteAllText(locator.UpdateExePath, "fixture archive marker");
        var (latest, bytes) = V040PackageDownloader("0.4.2");
        var downloadedPath = Path.Combine(packages, "Yamuse-0.4.2-full.nupkg");
        var service = V040Service(directory, locator, latest, retiredPackageHash: HoldOlderRead);
        try
        {
            Assert((await service.CheckAsync(lifetime.Token)).Status == AppUpdateStatus.Available, "handoff fixture did not select the package");
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var ready = await service.DownloadAsync(cancellationToken: lifetime.Token).WaitAsync(TimeSpan.FromSeconds(3));
            Assert(ready.Status == AppUpdateStatus.Downloaded && V040ReceiptVersions(ledger).Contains("0.4.2"),
                "verified download became ready before writing its receipt");
            service.ApplyAndRestart();
            Assert(locator.ExitCode == 0 && locator.Starts == 1 && handedOffReceipts is { } receipts
                && receipts.SequenceEqual(new[] { "0.4.0", "0.4.2" }) && File.Exists(olderPath),
                "immediate updater handoff lost the new receipt or waited for the older archive");
        }
        finally
        {
            lifetime.Cancel();
            release.Set();
            await V040AwaitMaintenance(service);
        }

        Assert(File.ReadAllBytes(olderPath).SequenceEqual(olderBytes) && File.ReadAllBytes(downloadedPath).SequenceEqual(bytes)
            && V040ReceiptVersions(ledger).SequenceEqual(new[] { "0.4.0", "0.4.2" }),
            "exit while reading an older archive changed a retained package or its receipt");
    }

    private static async Task V040ReceiptWriteFailure()
    {
        var directory = Path.Combine(_directory, "versioning040-package-receipt-locked");
        var initial = new TestVelopackLocator("YMusic.Native", "0.3.16", directory);
        var packages = initial.PackagesDir!;
        Directory.CreateDirectory(packages);
        var olderPath = Path.Combine(packages, V040GithubDownloader.PackageName);
        var ledger = Path.Combine(packages, "yamuse-package-receipts.dat");
        var (older, olderBytes) = V040PackageDownloader();
        File.WriteAllBytes(olderPath, olderBytes);
        await V040CheckAndMaintain(V040Service(directory, initial, older));
        var originalLedger = File.ReadAllBytes(ledger);
        var later = new TestVelopackLocator("YMusic.Native", "0.4.0", directory);
        var (latest, bytes) = V040PackageDownloader("0.4.2");
        var path = Path.Combine(packages, "Yamuse-0.4.2-full.nupkg");
        var service = V040Service(directory, later, latest);
        using (var locked = File.OpenRead(ledger))
        {
            Assert((await V040CheckAndMaintain(service)).Status == AppUpdateStatus.Available,
                "ledger contention hid the official offer");
            Assert((await service.DownloadAsync()).Status == AppUpdateStatus.Downloaded && File.ReadAllBytes(path).SequenceEqual(bytes)
                && File.ReadAllBytes(olderPath).SequenceEqual(olderBytes) && File.ReadAllBytes(ledger).SequenceEqual(originalLedger)
                && !Directory.EnumerateFiles(packages, "*.tmp").Any(),
                "a failed receipt write removed a package, changed the guarded ledger or reported a bad download");
        }
        Assert((await V040CheckAndMaintain(service)).Status == AppUpdateStatus.Downloaded
            && V040ReceiptVersions(ledger).SequenceEqual(new[] { "0.4.0", "0.4.2" }) && File.ReadAllBytes(olderPath).SequenceEqual(olderBytes)
            && File.ReadAllBytes(path).SequenceEqual(bytes) && latest.Downloads == 1,
            "receipt retry did not record the verified bytes or changed the installed version's archive");
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
                await V040AwaitMaintenance(original);
            }
            else
            {
                File.WriteAllBytes(PackagePath(version), bytes);
                Assert((await V040CheckAndMaintain(original)).Status == AppUpdateStatus.Downloaded, "existing package was not confirmed and adopted");
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
            var first = await V040CheckAndMaintain(check);
            Assert(first.Status == AppUpdateStatus.Downloaded && first.CheckSucceeded && first.AvailableVersion == "0.4.5"
                && File.Exists(busy) && !File.Exists(obsolete), "locked receipt blocked an independent removal or altered update readiness");
        }

        await V040CheckAndMaintain(check);
        var retried = await V040CheckAndMaintain(check);
        Assert(retried.Status == AppUpdateStatus.Downloaded && retried.CheckSucceeded && retried.AvailableVersion == "0.4.5"
            && !File.Exists(busy) && File.ReadAllBytes(changed).SequenceEqual(altered) && File.ReadAllBytes(current).SequenceEqual(keepCurrent)
            && File.ReadAllBytes(pending).SequenceEqual(nextBytes) && File.ReadAllText(unknown) == "untracked legacy package fixture"
            && File.ReadAllBytes(replaced).SequenceEqual(copied) && File.ReadAllBytes(movedOriginal).SequenceEqual(copied)
            && File.ReadAllText(foreign) == "foreign package fixture" && next.Downloads == 0,
            "cleanup missed its retry or modified current/pending/changed/foreign files");
    }

    private static async Task V040LateRetirementRefusal()
    {
        var directory = Path.Combine(_directory, "versioning040-retirement-disposition");
        var initial = new TestVelopackLocator("YMusic.Native", "0.3.16", directory);
        var packages = initial.PackagesDir!;
        Directory.CreateDirectory(packages);
        string PackagePath(string version) => Path.Combine(packages, $"Yamuse-{version}-full.nupkg");
        var versions = new[] { "0.4.0", "0.4.1", "0.4.2" };
        var originals = new Dictionary<string, byte[]>();
        var blocked = new HashSet<(ulong Volume, ulong Low, ulong High)>();
        foreach (var version in versions)
        {
            var (downloader, bytes) = V040PackageDownloader(version);
            originals.Add(version, bytes);
            File.WriteAllBytes(PackagePath(version), bytes);
            Assert((await V040CheckAndMaintain(V040Service(directory, initial, downloader))).Status == AppUpdateStatus.Downloaded,
                "late refusal fixture did not record the archive");
            if (version == "0.4.2") continue;
            using var archive = File.OpenRead(PackagePath(version));
            var identity = FileSecurityPreservation.GetPersistentRevision(archive.SafeFileHandle)
                ?? throw new InvalidOperationException("fixture volume does not provide persistent file identity");
            blocked.Add((identity.Volume, identity.Low, identity.High));
        }

        var hashReads = 0;
        byte[] HashArchive(Stream archive, CancellationToken token)
        {
            hashReads++;
            return VelopackUpdateBackend.HashRetiredPackage(archive, token);
        }
        var refusals = 0;
        void DeleteArchive(SafeFileHandle archive)
        {
            if (FileSecurityPreservation.GetPersistentRevision(archive) is { } identity
                && blocked.Contains((identity.Volume, identity.Low, identity.High)))
            {
                refusals++;
                throw new Win32Exception(5);
            }
            UninstallPathGuard.DeleteByHandle(archive);
        }

        var ledger = Path.Combine(packages, "yamuse-package-receipts.dat");
        string[] Receipts() => V040ReadReceipts(ledger).Select(item => item.GetRawText()).ToArray();
        var before = Receipts();
        var current = new TestVelopackLocator("YMusic.Native", "0.4.3", directory);
        var service = V040Service(directory, current, V040PackageDownloader("0.4.3").Downloader,
            retiredPackageHash: HashArchive, deleteRetiredPackage: DeleteArchive);
        var first = await V040CheckAndMaintain(service);
        Assert(first.Status == AppUpdateStatus.NoUpdate && first.CheckSucceeded && hashReads == 2 && refusals == 2
            && Receipts().SequenceEqual(new[] { before[2], before[0], before[1] })
            && versions.All(version => File.ReadAllBytes(PackagePath(version)).SequenceEqual(originals[version])),
            "late disposition errors discarded an exact receipt, changed a package or exceeded the per-check budget");
        var second = await V040CheckAndMaintain(service);
        Assert(second.CheckSucceeded && hashReads == 4 && refusals == 3 && !File.Exists(PackagePath("0.4.2"))
            && Receipts().SequenceEqual(new[] { before[1], before[0] })
            && versions.Take(2).All(version => File.ReadAllBytes(PackagePath(version)).SequenceEqual(originals[version]))
            && !Directory.EnumerateFiles(packages, "*.tmp").Any(),
            "late disposition errors starved a later eligible archive or left an incomplete staging file");
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
        var adopted = await V040CheckAndMaintain(V040Service(directory, initial, downloader));
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
            var result = await V040CheckAndMaintain(service);
            Assert(result.Status == AppUpdateStatus.NoUpdate && result.CheckSucceeded && File.ReadAllBytes(archive).SequenceEqual(bytes)
                && File.ReadAllBytes(ledger).SequenceEqual(inadmissible), "unprotected or wrongly scoped receipt authorized deletion");
        }

        File.WriteAllBytes(ledger, protectedReceipt);
        Assert((await V040CheckAndMaintain(service)).CheckSucceeded && !File.Exists(archive), "authentic receipt did not authorize the exact archive");
    }

    private static async Task V040CanceledPackageCleanup()
    {
        using (var cancelRead = new CancellationTokenSource())
        using (var source = new V040CancelingStream(cancelRead))
        {
            await Throws<OperationCanceledException>(() =>
            {
                VelopackUpdateBackend.HashRetiredPackage(source, cancelRead.Token);
                return Task.CompletedTask;
            });
            Assert(source.Reads == 1, "retirement hash read another block after cancellation");
        }

        var directory = Path.Combine(_directory, "versioning040-canceled-package-cleanup");
        var initial = new TestVelopackLocator("YMusic.Native", "0.3.16", directory);
        Directory.CreateDirectory(initial.PackagesDir!);
        var archive = Path.Combine(initial.PackagesDir!, V040GithubDownloader.PackageName);
        var ledger = Path.Combine(initial.PackagesDir!, "yamuse-package-receipts.dat");
        var (downloader, bytes) = V040PackageDownloader();
        File.WriteAllBytes(archive, bytes);
        Assert((await V040CheckAndMaintain(V040Service(directory, initial, downloader))).Status == AppUpdateStatus.Downloaded,
            "cleanup cancellation fixture did not verify the package");
        var receipt = File.ReadAllBytes(ledger);
        using var cancellation = new CancellationTokenSource();
        var reads = 0;
        byte[] CancelDuringHash(Stream file, CancellationToken token)
        {
            using var source = new V040CancelingStream(cancellation, file);
            try { return VelopackUpdateBackend.HashRetiredPackage(source, token); }
            finally { reads = source.Reads; }
        }
        var later = new TestVelopackLocator("YMusic.Native", "0.4.1", directory);
        var canceled = await V040CheckAndMaintain(V040Service(directory, later, downloader, retiredPackageHash: CancelDuringHash), cancellation.Token);
        Assert(canceled.Status == AppUpdateStatus.NoUpdate && cancellation.IsCancellationRequested && reads == 1
            && File.ReadAllBytes(archive).SequenceEqual(bytes) && File.ReadAllBytes(ledger).SequenceEqual(receipt),
            "cancellation changed the archive or receipt after committing the official check");
        Assert((await V040CheckAndMaintain(V040Service(directory, later, downloader))).CheckSucceeded && !File.Exists(archive),
            "the next successful check did not retire the previously canceled archive");
    }

    private static async Task V040DamagedReceiptRecovery()
    {
        var directory = Path.Combine(_directory, "versioning040-damaged-receipt");
        var locator = new TestVelopackLocator("YMusic.Native", "0.3.16", directory);
        var packages = locator.PackagesDir!;
        Directory.CreateDirectory(packages);
        var ledger = Path.Combine(packages, "yamuse-package-receipts.dat");
        var legacy = Path.Combine(packages, "Yamuse-0.3.1-full.nupkg");
        File.WriteAllText(legacy, "unattested older cache fixture");
        var (downloader, bytes) = V040PackageDownloader();
        File.WriteAllBytes(Path.Combine(packages, V040GithubDownloader.PackageName), bytes);
        var service = V040Service(directory, locator, downloader);
        var entropy = V040ReceiptEntropy(ledger);
        var damaged = ProtectedData.Protect("""{"Schema":1,"Packages":null}"""u8.ToArray(), entropy, DataProtectionScope.CurrentUser);
        var future = ProtectedData.Protect("""{"Schema":2,"Packages":[]}"""u8.ToArray(), entropy, DataProtectionScope.CurrentUser);
        var opaque = ProtectedData.Protect("""{"Schema":1,"Packages":[]}"""u8.ToArray(), [3, 4, 5], DataProtectionScope.CurrentUser);
        foreach (var untouched in new[] { opaque, future })
        {
            File.WriteAllBytes(ledger, untouched);
            var checkedUpdate = await V040CheckAndMaintain(service);
            Assert(checkedUpdate.Status == AppUpdateStatus.Downloaded && checkedUpdate.CheckSucceeded
                && File.ReadAllBytes(ledger).SequenceEqual(untouched) && !Directory.EnumerateFiles(packages, "*.bak").Any(),
                "a locked authentication or newer receipt format was replaced");
        }

        File.WriteAllBytes(ledger, damaged);
        var recovered = await V040CheckAndMaintain(service);
        var archived = Directory.EnumerateFiles(packages, "*.bak").Single();
        using var current = JsonDocument.Parse(ProtectedData.Unprotect(File.ReadAllBytes(ledger), entropy, DataProtectionScope.CurrentUser));
        var entries = current.RootElement.GetProperty("Packages");
        Assert(recovered.Status == AppUpdateStatus.Downloaded && recovered.CheckSucceeded && File.ReadAllBytes(archived).SequenceEqual(damaged)
            && entries.GetArrayLength() == 1 && entries[0].GetProperty("Version").GetString() == "0.4.0"
            && File.ReadAllText(legacy) == "unattested older cache fixture", "receipt recovery used unproven data or failed to preserve the original bytes");
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
        var first = await V040CheckAndMaintain(V040Service(directory, initial, downloader, packageRevision: Unsupported));
        Assert(first.Status == AppUpdateStatus.Downloaded && first.CheckSucceeded && count == 1 && !File.Exists(ledger),
            "lacking a persistent ID prevented normal readiness or created an unproven receipt");

        var enrolled = await V040CheckAndMaintain(V040Service(directory, initial, downloader));
        Assert(enrolled.Status == AppUpdateStatus.Downloaded && File.Exists(ledger), "supported fixture file was not enrolled");
        var later = new TestVelopackLocator("YMusic.Native", "0.4.1", directory);
        var ignored = await V040CheckAndMaintain(V040Service(directory, later, downloader, packageRevision: Unsupported));
        Assert(ignored.Status == AppUpdateStatus.NoUpdate && ignored.CheckSucceeded && count == 2 && File.ReadAllBytes(archive).SequenceEqual(bytes),
            "receipt authorized retirement while the current persistent ID was unavailable");
        Assert((await V040CheckAndMaintain(V040Service(directory, later, downloader))).CheckSucceeded && !File.Exists(archive),
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
        var entropy = V040ReceiptEntropy(ledger);
        var encoded = ProtectedData.Protect(JsonSerializer.SerializeToUtf8Bytes(new { Schema = 1, Packages = fakeReceipts }),
            entropy, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(ledger, encoded);

        var (later, bytes) = V040PackageDownloader("0.5.0");
        File.WriteAllBytes(Path.Combine(locator.PackagesDir!, "Yamuse-0.5.0-full.nupkg"), bytes);
        var result = await V040CheckAndMaintain(V040Service(directory, locator, later));
        using var decoded = JsonDocument.Parse(ProtectedData.Unprotect(File.ReadAllBytes(ledger), entropy, DataProtectionScope.CurrentUser));
        var versions = decoded.RootElement.GetProperty("Packages").EnumerateArray()
            .Select(receipt => receipt.GetProperty("Version").GetString()).ToArray();
        Assert(result.Status == AppUpdateStatus.Downloaded && result.CheckSucceeded && versions.Length == 64 && versions.Contains("0.5.0")
            && !versions.Contains("0.4.0") && versions.Contains("0.4.1") && File.ReadAllBytes(oldest).SequenceEqual(oldestBytes),
            "ledger overflow refused the new package, forgot a different receipt or deleted the forgotten file");
    }

    private static byte[] V040ReceiptEntropy(string ledger)
        => SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("Yamuse.VerifiedUpdatePackages.1\0" + ledger.ToUpperInvariant()));

    private static string?[] V040ReceiptVersions(string ledger)
        => V040ReadReceipts(ledger).Select(item => item.GetProperty("Version").GetString()).ToArray();

    private static JsonElement[] V040ReadReceipts(string ledger)
    {
        using var json = JsonDocument.Parse(ProtectedData.Unprotect(File.ReadAllBytes(ledger), V040ReceiptEntropy(ledger),
            DataProtectionScope.CurrentUser));
        return json.RootElement.GetProperty("Packages").EnumerateArray().Select(item => item.Clone()).ToArray();
    }

    private static async Task<AppUpdateState> V040CheckAndMaintain(UpdateSupport service, CancellationToken token = default)
    {
        var result = await service.CheckAsync(token);
        await V040AwaitMaintenance(service);
        return result;
    }

    private static Task V040AwaitMaintenance(UpdateSupport service)
    {
        var backend = typeof(UpdateSupport).GetField("_backend", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(service);
        return ((VelopackUpdateBackend)backend!).WaitForPackageMaintenanceAsync().WaitAsync(TimeSpan.FromSeconds(5));
    }

    private static UpdateSupport V040Service(string directory, TestVelopackLocator locator, V040GithubDownloader downloader,
        TimeSpan? metadataDeadline = null, Func<SafeFileHandle, FileSecurityPreservation.FileRevision?>? packageRevision = null,
        Func<string>? localAppDataFolder = null, Func<Stream, CancellationToken, byte[]>? retiredPackageHash = null,
        Action<SafeFileHandle>? deleteRetiredPackage = null, TimeProvider? clock = null)
    {
        Directory.CreateDirectory(directory);
        var feedPath = Path.Combine(directory, "update-feed.json");
        File.WriteAllText(feedPath, """{"githubRepository":"https://github.com/senduora/yamuse"}""");
        return new UpdateSupport(feedPath, installed: true, uri => new VelopackUpdateBackend(uri, locator,
            () => new FakeHandler(downloader.ReadMetadataAsync), () => new FakeHandler(downloader.ReadPackageAsync), metadataDeadline, packageRevision,
            localAppDataFolder, retiredPackageHash, deleteRetiredPackage), clock: clock);
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
        internal V040NoScanLocator(string directory, string? localAppData = null, string current = "0.3.16") : base("YMusic.Native", current,
            localAppData is null ? directory : Path.Combine(localAppData, "YMusic.Native", "packages"))
        {
            RootAppDir = Path.Combine(directory, "app-root");
            UpdateExePath = Path.Combine(localAppData is null ? RootAppDir : Path.Combine(localAppData, "YMusic.Native"), "Update.exe");
        }

        public override string RootAppDir { get; }
        public override string UpdateExePath { get; }
        public override IProcessImpl Process => this;
        internal bool FailStart { get; set; }
        internal Action? BeforeStart { get; init; }
        internal int Starts { get; private set; }
        internal string? SeenExecutable { get; private set; }
        internal string? SeenUpdater { get; private set; }
        internal string[]? SeenArguments { get; private set; }
        void IProcessImpl.StartProcess(string executable, IEnumerable<string> arguments, string? workingDirectory, bool noShow)
        {
            BeforeStart?.Invoke();
            Starts++;
            SeenExecutable = executable;
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
        internal string FeedFields { get; set; } = "";
        internal string PackageFields { get; set; } = "";
        internal byte[]? PackageBytes { get; set; }
        internal bool Offline { get; set; }
        internal HttpStatusCode LatestStatus { get; set; } = HttpStatusCode.OK;
        internal HttpStatusCode FeedStatus { get; set; } = HttpStatusCode.OK;
        internal bool OmitFeedAsset { get; set; }
        internal bool OmitPackageAsset { get; set; }
        internal string? QuotaHeader { get; set; }
        internal RetryConditionHeaderValue? RetryAfter { get; set; }
        internal string? RemainingQuota { get; set; }
        internal string? QuotaReset { get; set; }
        internal Func<HttpContent>? LatestErrorContent { get; set; }
        internal Func<CancellationToken, Task>? BeforeLatest { get; set; }
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
                if (BeforeLatest is { } beforeLatest) await beforeLatest(token).ConfigureAwait(false);
                if (LatestStatus != HttpStatusCode.OK)
                {
                    var response = LatestErrorContent is { } errorContent
                        ? new HttpResponseMessage(LatestStatus) { Content = errorContent() }
                        : Json("{}", LatestStatus);
                    if (QuotaHeader is { } header) response.Headers.TryAddWithoutValidation(header, header == "Retry-After" ? "30" : "0");
                    if (RetryAfter is not null) response.Headers.RetryAfter = RetryAfter;
                    if (RemainingQuota is { } quota) response.Headers.TryAddWithoutValidation("x-ratelimit-remaining", quota);
                    if (QuotaReset is { } reset) response.Headers.TryAddWithoutValidation("x-ratelimit-reset", reset);
                    return response;
                }
                var name = $"Yamuse-{ReleaseVersion}-full.nupkg";
                var packageUrl = $"{ReleaseRoot}{PackageTagOverride ?? "v" + ReleaseVersion}/{name}";
                var assets = new List<string>(2);
                if (!OmitFeedAsset) assets.Add($$"""{"name":"releases.win.json","browser_download_url":"{{FeedUrl}}"{{FeedFields}}}""");
                if (!OmitPackageAsset) assets.Add($$"""{"name":"{{name}}","browser_download_url":"{{packageUrl}}"{{PackageFields}}}""");
                return Json($$"""{"tag_name":"v{{ReleaseVersion}}","draft":false,"prerelease":false,"assets":[{{string.Join(",", assets)}}]}""");
            }
            if (url.StartsWith("https://api.github.com/repos/senduora/yamuse/releases?", StringComparison.Ordinal))
                return Json("[" + string.Join(",", Enumerable.Repeat("""{"name":"preview","prerelease":true}""", 10)) + "]");
            if (url != FeedUrl) return Json("{}", HttpStatusCode.ServiceUnavailable);
            if (FeedStatus != HttpStatusCode.OK) return Json("{}", FeedStatus);
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

    private sealed class V040CancelingStream(CancellationTokenSource cancellation, Stream? source = null) : MemoryStream
    {
        internal int Reads { get; private set; }

        public override int Read(byte[] buffer, int offset, int count)
        {
            Reads++;
            if (Reads > 1) return 0;
            buffer[offset] = 1;
            var read = source?.Read(buffer, offset, count) ?? 1;
            cancellation.Cancel();
            return read;
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

    private sealed class V040WaitingBody : MemoryStream
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
    }

    private sealed class V040UpdateClock : TimeProvider
    {
        private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _ticks;
        public override DateTimeOffset GetUtcNow() => new DateTimeOffset(2026, 10, 17, 0, 0, 0, TimeSpan.Zero) + TimeSpan.FromTicks(_ticks);
        internal void Advance(TimeSpan elapsed) => _ticks += elapsed.Ticks;
    }
}
