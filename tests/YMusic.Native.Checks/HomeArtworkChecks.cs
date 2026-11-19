using System.ComponentModel;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using YMusic.Native;

internal static partial class Program
{
    private static async Task CheckHomeArtwork()
    {
        await Run("Home artwork: numeric cache versions preserve renditions and reject unsafe addresses", HomeArtworkAddresses);
        await Run("Home artwork: version changes invalidate memory and disk cache without losing old versions", HomeArtworkVersions);
        await Run("Home artwork: disk hits leave the timestamp of a hard-linked image untouched", HomeArtworkHardLinkTimestamp);
        await Run("Home artwork: damaged disk entries are replaced by verified artwork", HomeArtworkDamagedEntry);
        await Run("Home artwork: automatic pruning verifies only a bounded batch and preserves unrelated files", HomeArtworkPrune);
        await Run("Home artwork: pruning retains old temporary stages with added file data", HomeArtworkPruneTemporaryMetadata);
        await Run("Home artwork: automatic pruning preserves the published image of a pending replacement", HomeArtworkPrunePendingReplacement);
        await Run("Home artwork: disk hits preserve replacement revisions and allow Archive repair", HomeArtworkDiskHitPendingReplacement);
        await Run("Home artwork: cold read restores a verified parked image without requesting it", HomeArtworkColdReadRestoresParked);
        await Run("Home artwork: a visible image recovers after cache clear while recycled images stay canceled", HomeArtworkClear);
        await Run("Home artwork: clear resolves authenticated slots without reviving stale images", HomeArtworkClearParkedImages);
        await Run("Home artwork: a changed returned image is preserved on clear and on disk read", HomeArtworkClearReturnedChanged);
        await Run("Home artwork: repeated clear preserves both files of a witnessed cache conflict", HomeArtworkClearConflict);
        await Run("Home artwork: clear drains a disk reader after its last subscriber cancels", HomeArtworkClearDetachedDiskReader);
        await Run("Home artwork: canceling a subscriber leaves the same key open to a fresh request", HomeArtworkCancellationLeavesCacheAvailable);
        await Run("Home artwork: clearing rejects results before the worker receives cancellation", HomeArtworkClearBeforeCancellation);
        await Run("Home artwork: explicit clearing retries still-visible transient failures together", HomeArtworkClearRecovery);
        await Run("Home artwork: a result held before dispatcher publication cannot cross a cache clear", HomeArtworkDispatcherAfterClear);
        await Run("Home artwork: versioned redirects stay on anonymous allowed CDN hosts", HomeArtworkRedirects);
        await Run("Home artwork: raw Content-Range prevents caching even when the header cannot be parsed", HomeArtworkContentRange);
        await Run("Home artwork: accepted MIME types are case insensitive and other types remain rejected", HomeArtworkMimeTypes);
        await Run("Home artwork: personal landing playlists populate frozen images through real Home XAML", HomeArtworkCards);
    }

    private static Task HomeArtworkAddresses()
    {
        const string source = "avatars.yandex.net/get-music-user-playlist/1/fixture/%%?1730453322475";
        Assert(ArtworkUri.Metadata(source) == "https://" + source, "numeric playlist cover version was discarded before downloading");
        foreach (var (size, rendition) in new[] { (64, 100), (128, 200), (256, 400), (1024, 1000) })
        {
            Assert(ArtworkUri.TryCreate(source, size, out var uri), "versioned rendition was rejected");
            Assert(uri.AbsolutePath.EndsWith($"/{rendition}x{rendition}") && uri.Query == "?1730453322475", "rendition lost its version or requested a non-CDN size");
        }
        foreach (var host in new[] { "avatars.yandex.net", "avatars.mds.yandex.net", "avatars.mdst.yandex.net" })
            Assert(ArtworkUri.TryCreate($"https://{host}/get-music-user-playlist/1/fixture/%%?1", 128, out _), "an existing allowed CDN host lost numeric version support");
        foreach (var invalid in new[]
        {
            "https://avatars.yandex.net/a/%%?version=123", "https://avatars.yandex.net/a/%%?123&456",
            "https://avatars.yandex.net/a/%%?123%0d", "https://avatars.yandex.net/a/%%?123#fragment",
            "https://avatars.yandex.net/a/%%?123456789012345678901", "https://avatars.yandex.net.evil.test/a/%%?1",
            "https://avatars.yandex.net@evil.test/a/%%?1", "https://avatars.yandex.net:444/a/%%?1",
            "http://avatars.yandex.net/a/%%?1", "file:///a/cover.png?1"
        }) Assert(ArtworkUri.Metadata(invalid) is null && !ArtworkUri.TryCreate(invalid, 128, out _), "unsafe artwork address was accepted");
        return Task.CompletedTask;
    }

    private static async Task HomeArtworkVersions()
    {
        const string prefix = "avatars.yandex.net/get-music-user-playlist/1/fixture/%%?";
        var directory = Path.Combine(_directory, "home-artwork-versions");
        var requests = new List<string>();
        using (var cache = new ArtworkCache(directory, new FakeHandler((request, _) =>
        {
            HomeArtworkAnonymous(request);
            requests.Add(request.RequestUri!.Query);
            return Task.FromResult(HomeArtworkResponse(HomeArtworkPng(request.RequestUri.Query == "?1" ? Colors.Red : Colors.Blue)));
        })))
        {
            var first = await cache.GetAsync(prefix + "1", 256, CancellationToken.None);
            var second = await cache.GetAsync(prefix + "2", 256, CancellationToken.None);
            Assert(first is not null && second is not null && requests.SequenceEqual(new[] { "?1", "?2" }), "versioned covers did not reach their distinct CDN resources");
            Assert(HomeArtworkPixel(first!)[2] == 255 && HomeArtworkPixel(second!)[0] == 255, "a newer version reused the previous cover pixels");
            Assert(ReferenceEquals(first, await cache.GetAsync(prefix + "1", 256, CancellationToken.None)), "memory cache lost an unchanged version");
        }
        using var restarted = new ArtworkCache(directory, new FakeHandler((_, _) => throw new InvalidOperationException("warm artwork cache attempted network")));
        var diskFirst = await restarted.GetAsync(prefix + "1", 256, CancellationToken.None);
        var diskSecond = await restarted.GetAsync(prefix + "2", 256, CancellationToken.None);
        Assert(diskFirst is not null && diskSecond is not null && HomeArtworkPixel(diskFirst)[2] == 255 && HomeArtworkPixel(diskSecond)[0] == 255,
            "disk cache mixed distinct image versions after restarting");
    }

    private static async Task HomeArtworkHardLinkTimestamp()
    {
        const string prefix = "https://avatars.yandex.net/get-music-content/1/hard-link/";
        var directory = Path.Combine(_directory, "home-artwork-link-timestamp");
        var png = HomeArtworkPng(Colors.Coral);
        string linked;
        string ordinary;
        using (var initial = new ArtworkCache(directory, new FakeHandler((_, _) => Task.FromResult(HomeArtworkResponse(png)))))
        {
            Assert(await initial.GetAsync(prefix + "linked", 256, default) is not null, "could not save the first artwork fixture");
            linked = Directory.EnumerateFiles(directory, "*.img").Single();
            Assert(await initial.GetAsync(prefix + "ordinary", 256, default) is not null, "could not save the second artwork fixture");
            ordinary = Directory.EnumerateFiles(directory, "*.img").Single(path => path != linked);
        }
        var alias = Path.Combine(_directory, "home-artwork-linked-image.png");
        Assert(FcCreateHardLink(alias, linked, IntPtr.Zero), "could not create the isolated cache hard link");
        var old = DateTime.UtcNow.AddDays(-2);
        File.SetLastWriteTimeUtc(linked, old);
        File.SetLastWriteTimeUtc(ordinary, old);
        var linkedTime = File.GetLastWriteTimeUtc(alias);
        var ordinaryTime = File.GetLastWriteTimeUtc(ordinary);
        using var restored = new ArtworkCache(directory, new FakeHandler((_, _) => throw new InvalidOperationException("warm artwork requested network")));
        Assert(await restored.GetAsync(prefix + "linked", 256, default) is { IsFrozen: true }
            && await restored.GetAsync(prefix + "ordinary", 256, default) is { IsFrozen: true }, "the file check rejected a readable image");
        Assert(File.GetLastWriteTimeUtc(linked) == linkedTime && File.GetLastWriteTimeUtc(alias) == linkedTime
            && File.GetLastWriteTimeUtc(ordinary) > ordinaryTime,
            "reading a hard-linked image changed the shared timestamp or skipped the ordinary recency update");
    }

    private static async Task HomeArtworkDamagedEntry()
    {
        const string source = "https://avatars.yandex.net/get-music-content/1/damaged/200x200";
        var directory = Path.Combine(_directory, "home-artwork-damaged");
        var png = HomeArtworkPng(Colors.Coral);
        using (var initial = new ArtworkCache(directory, new FakeHandler((_, _) => Task.FromResult(HomeArtworkResponse(png)))))
            Assert(await initial.GetAsync(source, 128, default) is not null, "could not prime the artwork disk cache");

        var path = Directory.EnumerateFiles(directory, "*.img").Single();
        File.WriteAllBytes(path, []);
        using (var cache = new ArtworkCache(directory, new FakeHandler((_, _) =>
        {
            Assert(new FileInfo(path).Length == 0, "a failed disk read removed the cache path before a replacement was ready");
            return Task.FromResult(HomeArtworkResponse(png));
        })))
            Assert(await cache.GetAsync(source, 128, default) is not null, "an invalid disk entry prevented a healthy download");

        Assert(File.ReadAllBytes(path).SequenceEqual(png) && Directory.EnumerateFiles(directory).Single() == path,
            "a verified replacement was absent or left a staged file behind");
    }

    private static async Task HomeArtworkPrune()
    {
        const string prefix = "https://avatars.yandex.net/get-music-content/1/prune/";
        var directory = Path.Combine(_directory, "home-artwork-prune");
        Directory.CreateDirectory(directory);
        var png = HomeArtworkPng(Colors.Coral);
        for (var index = 0; index < ArtworkCache.DiskEntryLimit; index++)
            File.WriteAllBytes(Path.Combine(directory, $"{index:x64}.img"), png);

        var invalid = Path.Combine(directory, new string('a', 64) + ".img");
        File.WriteAllText(invalid, "preserve this unrelated file");
        var now = DateTime.UtcNow;
        File.SetLastWriteTimeUtc(invalid, now.AddDays(-3));
        var namedImage = Path.Combine(directory, "user-selected.img");
        File.WriteAllBytes(namedImage, png);
        var oldImages = Enumerable.Range(0, 6).Select(index =>
        {
            var path = Path.Combine(directory, $"{ArtworkCache.DiskEntryLimit + index:x64}.img");
            File.WriteAllBytes(path, png);
            File.SetLastWriteTimeUtc(path, now.AddDays(-2).AddMinutes(-index));
            return path;
        }).ToArray();

        using var cache = new ArtworkCache(directory, new FakeHandler((request, _) =>
        {
            HomeArtworkAnonymous(request);
            return Task.FromResult(HomeArtworkResponse(png));
        }));
        Assert(await cache.GetAsync(prefix + "first", 256, default) is { IsFrozen: true }, "first artwork could not publish");
        Assert(File.Exists(invalid) && File.ReadAllText(invalid) == "preserve this unrelated file"
            && File.Exists(namedImage) && File.ReadAllBytes(namedImage).SequenceEqual(png),
            "automatic pruning deleted or changed unrelated artwork-directory files");
        var survivors = oldImages.Count(File.Exists);
        Assert(survivors is > 2 and < 6, "automatic pruning did not limit itself to one bounded batch of old images");

        Assert(await cache.GetAsync(prefix + "second", 256, default) is { IsFrozen: true }, "second artwork could not publish");
        Assert(oldImages.Count(File.Exists) < survivors && File.Exists(invalid) && File.Exists(namedImage),
            "the next pruning sweep did not progress past a preserved file");
    }

    private static async Task HomeArtworkPruneTemporaryMetadata()
    {
        var directory = Directory.CreateDirectory(Path.Combine(_directory, "home-artwork-prune-temporary-metadata")).FullName;
        if (new DriveInfo(Path.GetPathRoot(directory)!).DriveFormat != "NTFS") return;
        var expired = DateTime.UtcNow.AddDays(-3);
        var withEa = OldStage('a');
        var withStream = OldStage('b');
        var ordinary = OldStage('c');
        var busy = OldStage('d');
        Cleanup015AddExtendedAttribute(withEa);
        File.WriteAllText(withStream + ":fixture-note", "preserve annotation");
        foreach (var path in new[] { withEa, withStream, ordinary, busy }) File.SetLastWriteTimeUtc(path, expired);

        using var cache = new ArtworkCache(directory, new FakeHandler((request, _) =>
        {
            HomeArtworkAnonymous(request);
            return Task.FromResult(HomeArtworkResponse(HomeArtworkPng(Colors.Green)));
        }));
        using (var blocked = new FileStream(busy, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert(await cache.GetAsync("https://avatars.yandex.net/cleanup-fixture/prune-temporary/first", 128, default) is not null,
                "fixture could not publish the new artwork to trigger pruning");
            Assert(!File.Exists(ordinary) && File.Exists(busy), "prune lost the busy stage or skipped an ordinary expired stage");
        }
        Assert(await cache.GetAsync("https://avatars.yandex.net/cleanup-fixture/prune-temporary/second", 128, default) is not null
            && !File.Exists(busy), "a later prune did not retry the released stage");
        using var ea = UninstallPathGuard.OpenOwnedFile(withEa, read: true, deleteAccess: false);
        Assert(File.ReadAllText(withEa) == "preserve stage" && !FileSecurityPreservation.HasNamedDataStreams(ea)
            && FileSecurityPreservation.HasNamedStreamsOrExtendedAttributes(ea)
            && File.ReadAllText(withStream) == "preserve stage" && File.ReadAllText(withStream + ":fixture-note") == "preserve annotation",
            "prune lost stage contents, extended attributes, or named stream contents");

        string OldStage(char key)
        {
            var path = Path.Combine(directory, $"{new string(key, 64)}.{Guid.NewGuid():N}.tmp");
            File.WriteAllText(path, "preserve stage");
            return path;
        }
    }

    private static async Task HomeArtworkPrunePendingReplacement()
    {
        const string source = "https://avatars.yandex.net/get-music-content/1/prune-pending/current";
        const string independent = "https://avatars.yandex.net/get-music-content/1/prune-pending/independent";
        var directory = Path.Combine(_directory, "home-artwork-prune-pending");
        Directory.CreateDirectory(directory);
        if (new DriveInfo(Path.GetPathRoot(directory)!).DriveFormat != "NTFS") return;
        var oldImage = HomeArtworkPng(Colors.Red);
        var published = HomeArtworkPng(Colors.Blue);
        var independentImage = HomeArtworkPng(Colors.Green);
        for (var index = 0; index < ArtworkCache.DiskEntryLimit; index++)
            File.WriteAllBytes(Path.Combine(directory, $"{index:x64}.img"), independentImage);

        var path = Path.Combine(directory,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source))).ToLowerInvariant() + ".img");
        File.WriteAllBytes(path, oldImage);
        var slot = HomeArtworkParkReplacement(path, published, publish: true);
        Assert(slot.Pending && File.ReadAllBytes(slot.Previous).SequenceEqual(oldImage) && File.ReadAllBytes(path).SequenceEqual(published),
            "fixture did not leave both authenticated versions after publication");

        var requests = new List<string>();
        using var cache = new ArtworkCache(directory, new FakeHandler((request, _) =>
        {
            HomeArtworkAnonymous(request);
            requests.Add(request.RequestUri!.AbsoluteUri);
            return Task.FromResult(HomeArtworkResponse(independentImage));
        }));
        Assert(await cache.GetAsync(independent, 128, default) is not null, "the independent image did not trigger pruning");
        Assert(Directory.EnumerateFiles(directory, "*.img").Count() == ArtworkCache.DiskEntryLimit + 1,
            "pruning failed to continue to an eligible entry after the guarded slot");
        var result = await cache.GetAsync(source, 128, default);
        Assert(result is not null && HomeArtworkPixel(result)[0] == 255 && requests.SequenceEqual([independent]),
            "a cold cache read supplied the previous image after pruning the published one");
        Assert(slot.Pending && File.ReadAllBytes(path).SequenceEqual(published) && File.ReadAllBytes(slot.Previous).SequenceEqual(oldImage),
            "automatic pruning altered one of the versions in the pending slot");
    }

    private static async Task HomeArtworkDiskHitPendingReplacement()
    {
        const string source = "https://avatars.yandex.net/get-music-content/1/disk-hit-recovery";
        var root = Directory.CreateDirectory(Path.Combine(_directory, "home-artwork-disk-hit-recovery")).FullName;
        if (new DriveInfo(Path.GetPathRoot(root)!).DriveFormat != "NTFS") return;
        var previous = HomeArtworkPng(Colors.Red);
        var replacement = HomeArtworkPng(Colors.Blue);
        foreach (var returned in new[] { false, true })
        {
            var directory = Directory.CreateDirectory(Path.Combine(root, returned ? "returned" : "published")).FullName;
            var path = Path.Combine(directory,
                Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source))).ToLowerInvariant() + ".img");
            File.WriteAllBytes(path, previous);
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-3));
            File.SetAttributes(path, FileAttributes.Hidden);
            var slot = HomeArtworkParkReplacement(path, replacement, publish: !returned);
            if (returned)
            {
                using (var ancestors = UninstallPathGuard.HoldExistingAncestors(path))
                using (var writer = GuardedStoreFile.HoldWriteGate(path))
                using (var original = UninstallPathGuard.OpenOwnedFile(slot.Previous, read: true))
                    ManagedCacheCleanup.RenameByHandle(original, path);
                File.SetAttributes(path, FileAttributes.Hidden | FileAttributes.Archive);
            }
            var written = File.GetLastWriteTimeUtc(path);
            Assert(slot.Pending, "fixture could not retain the interrupted artwork replacement");
            using var cache = new ArtworkCache(directory,
                new FakeHandler((_, _) => throw new InvalidOperationException("a valid fixture image should be read from disk")));
            var bitmap = await cache.GetAsync(source, 128, default);
            Assert(bitmap is not null && HomeArtworkPixel(bitmap)[returned ? 2 : 0] == 255 && File.GetLastWriteTimeUtc(path) == written,
                "a disk hit changed Written while it was part of a pending replacement");
            if (returned)
                Assert(!slot.Pending && File.GetAttributes(path) == FileAttributes.Hidden,
                    "the disk hit did not complete the interrupted Archive repair");
            else
                Assert(slot.Pending && File.ReadAllBytes(slot.Previous).SequenceEqual(previous),
                    "the disk hit changed a still-pending complete replacement");

            var removed = await cache.ClearAsync(default);
            Assert(removed.Removed == 1 && removed.Failed == 0 && !slot.Pending && !Directory.EnumerateFileSystemEntries(directory).Any(),
                "clear left an archive from an otherwise unchanged artwork replacement");
        }
    }

    private static GuardedStoreRecovery HomeArtworkParkReplacement(string path, byte[] replacement, bool publish)
    {
        var slot = new GuardedStoreRecovery(path, GuardedStoreKind.Artwork);
        using var ancestors = UninstallPathGuard.HoldExistingAncestors(path);
        using var writer = GuardedStoreFile.HoldWriteGate(path);
        using var original = UninstallPathGuard.OpenOwnedFile(path, read: true);
        using var staged = UninstallPathGuard.CreateStagedFile(path + ".fixture-stage", deleteOnClose: true);
        RandomAccess.Write(staged, replacement, 0);
        RandomAccess.FlushToDisk(staged);
        File.SetLastWriteTimeUtc(staged, DateTime.UtcNow.AddDays(-2));
        using var receipt = slot.Record(original, staged);
        ManagedCacheCleanup.RenameByHandle(original, slot.Previous);
        if (publish)
        {
            ManagedCacheCleanup.RenameByHandle(staged, path);
            UninstallPathGuard.KeepFileOnClose(staged);
        }
        return slot;
    }

    private static async Task HomeArtworkColdReadRestoresParked()
    {
        const string source = "https://avatars.yandex.net/get-music-content/1/parked-read-recovery";
        var directory = Directory.CreateDirectory(Path.Combine(_directory, "home-artwork-parked-read-recovery")).FullName;
        if (new DriveInfo(Path.GetPathRoot(directory)!).DriveFormat != "NTFS") return;
        var path = Path.Combine(directory,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source))).ToLowerInvariant() + ".img");
        var previous = HomeArtworkPng(Colors.Red);
        var expectedHash = SHA256.HashData(previous);
        File.WriteAllBytes(path, previous);
        File.SetAttributes(path, FileAttributes.Hidden);
        var written = File.GetLastWriteTimeUtc(path);
        var slot = HomeArtworkParkReplacement(path, HomeArtworkPng(Colors.Blue), publish: false);
        Assert(!File.Exists(path) && slot.Pending && File.Exists(slot.Previous), "fixture did not leave a recoverable parked image");

        var requests = 0;
        using var cache = new ArtworkCache(directory, new FakeHandler((_, _) =>
        {
            Interlocked.Increment(ref requests);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }));
        var bitmap = await cache.GetAsync(source, 128, default);
        Assert(bitmap is not null && HomeArtworkPixel(bitmap)[2] == 255 && requests == 0,
            "cold artwork load did not serve the prior image without requesting it");
        Assert(!slot.Pending && SHA256.HashData(File.ReadAllBytes(path)).SequenceEqual(expectedHash)
            && File.GetLastWriteTimeUtc(path) == written && File.GetAttributes(path) == FileAttributes.Hidden,
            "cold artwork load did not restore the exact prior file and its revision");
    }

    private static async Task HomeArtworkClear()
    {
        const string stableSource = "https://avatars.yandex.net/get-music-content/1/stable/200x200";
        const string loadingSource = "https://avatars.yandex.net/get-music-content/1/loading/200x200";
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstCanceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var loadingRequests = 0;
        var red = HomeArtworkPng(Colors.Red);
        var blue = HomeArtworkPng(Colors.Blue);
        using var cache = new ArtworkCache(Path.Combine(_directory, "home-artwork-clear"), new FakeHandler(async (request, token) =>
        {
            if (request.RequestUri!.AbsoluteUri == stableSource) return HomeArtworkResponse(red);
            if (Interlocked.Increment(ref loadingRequests) == 1)
            {
                using var cancellation = token.Register(() => firstCanceled.TrySetResult());
                firstEntered.SetResult();
                await releaseFirst.Task;
                token.ThrowIfCancellationRequested();
            }
            return HomeArtworkResponse(blue);
        }));
        var stable = new Image();
        var current = new Image();
        var recycled = new Image();
        var arriving = new Image();
        var window = new Window
        {
            Left = -16000, Width = 200, Height = 200, ShowActivated = false, ShowInTaskbar = false,
            Content = new StackPanel { Children = { stable, current, recycled, arriving } }
        };
        var previous = Artwork.Cache;
        Artwork.Cache = cache;
        try
        {
            window.Show();
            Artwork.SetSourceUri(stable, stableSource);
            await Until(() => Artwork.GetLoadStatus(stable) == ArtworkLoadStatus.Ready);
            var stableBitmap = stable.Source;
            Artwork.SetSourceUri(current, loadingSource);
            Artwork.SetSourceUri(recycled, loadingSource);
            await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

            var clear = cache.ClearAsync(default);
            await firstCanceled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Artwork.SetSourceUri(recycled, null);
            Artwork.SetSourceUri(arriving, loadingSource);
            Assert(Artwork.GetLoadStatus(current) == ArtworkLoadStatus.Loading
                && Artwork.GetLoadStatus(arriving) == ArtworkLoadStatus.Loading,
                "clearing the cache marked a current or new visible request unavailable");
            releaseFirst.SetResult();
            await clear.WaitAsync(TimeSpan.FromSeconds(5));

            await Until(() => Artwork.GetLoadStatus(current) == ArtworkLoadStatus.Ready
                && Artwork.GetLoadStatus(arriving) == ArtworkLoadStatus.Ready);
            Assert(ReferenceEquals(stableBitmap, stable.Source), "cache clear dropped an already displayed cover");
            Assert(Artwork.GetLoadStatus(recycled) == ArtworkLoadStatus.None && recycled.Source is null,
                "a recycled image published a completed request after clear");
            Assert(loadingRequests == 2 && ReferenceEquals(current.Source, arriving.Source)
                && HomeArtworkPixel((BitmapSource)current.Source)[0] == 255,
                "visible requests did not share one fresh, valid cover after clear");
        }
        finally
        {
            releaseFirst.TrySetResult();
            window.Close();
            Artwork.Cache = previous;
        }
    }

    private static async Task HomeArtworkClearParkedImages()
    {
        const string prefix = "https://avatars.yandex.net/get-music-content/1/parked-clear/";
        var directory = Path.Combine(_directory, "home-artwork-parked-clear");
        Directory.CreateDirectory(directory);
        if (new DriveInfo(Path.GetPathRoot(directory)!).DriveFormat != "NTFS") return;
        var red = HomeArtworkPng(Colors.Red);
        var blue = HomeArtworkPng(Colors.Blue);
        var requests = 0;
        using var cache = new ArtworkCache(directory, new FakeHandler((request, _) =>
        {
            HomeArtworkAnonymous(request);
            return Task.FromResult(HomeArtworkResponse(Interlocked.Increment(ref requests) <= 3 ? red : blue));
        }));
        var displayed = new Image();
        var window = new Window { Left = -16000, Width = 200, Height = 200, ShowActivated = false, ShowInTaskbar = false, Content = displayed };
        var previous = Artwork.Cache;
        Artwork.Cache = cache;
        try
        {
            window.Show();
            Artwork.SetSourceUri(displayed, prefix + "confirmed");
            await Until(() => Artwork.GetLoadStatus(displayed) == ArtworkLoadStatus.Ready);
            var oldBitmap = displayed.Source;
            Assert(await cache.GetAsync(prefix + "protected", 128, default) is not null
                && await cache.GetAsync(prefix + "legacy", 128, default) is not null, "fixture could not prime the original artwork files");

            var confirmed = Park("confirmed", artwork: true);
            var protectedSlot = Park("protected", artwork: true);
            File.WriteAllText(protectedSlot.Previous + ":yamuse-fixture", "local annotation");
            var legacy = Park("legacy", artwork: false);
            var unknown = new GuardedStoreRecovery(ImagePath("unknown"), GuardedStoreKind.Artwork);
            File.WriteAllText(unknown.Previous, "unverified file");

            var alreadyPreserved = Directory.EnumerateFileSystemEntries(directory)
                .Count(entry => GuardedStoreRecovery.ArtworkImageName(Path.GetFileName(entry)) is null);
            var cleared = await cache.ClearAsync(default);
            Assert(cleared.Removed == 1 && cleared.RemovedBytes == red.Length && !confirmed.Pending && !File.Exists(ImagePath("confirmed")),
                "clear did not retire the exact authenticated old image");
            Assert(cleared.Preserved == alreadyPreserved + 1, "clear omitted the new conflict archive from its preserved count");
            Assert(ReferenceEquals(displayed.Source, oldBitmap) && Artwork.GetLoadStatus(displayed) == ArtworkLoadStatus.Ready,
                "clear discarded a displayed bitmap");
            var archive = Directory.EnumerateFiles(directory, Path.GetFileName(protectedSlot.Previous) + ".conflict-*.bak").Single();
            Assert(!protectedSlot.Pending && !File.Exists(ImagePath("protected"))
                && File.ReadAllBytes(archive).SequenceEqual(red)
                && File.ReadAllText(archive + ":yamuse-fixture") == "local annotation"
                && File.ReadAllBytes(legacy.Previous).SequenceEqual(red) && File.ReadAllText(unknown.Previous) == "unverified file",
                "clear changed a protected or unmapped recovery file");

            foreach (var key in new[] { "confirmed", "protected", "legacy" })
            {
                var fresh = await cache.GetAsync(prefix + key, 128, default);
                Assert(fresh is not null && HomeArtworkPixel(fresh)[0] == 255, "a cleared or protected recovery slot supplied stale artwork");
            }
            await Until(() => File.Exists(ImagePath("protected")));
            Assert(requests == 6 && !protectedSlot.Pending && legacy.Pending && File.ReadAllBytes(archive).SequenceEqual(red),
                "post-clear requests failed to store new artwork without disturbing the archive");
        }
        finally { window.Close(); Artwork.Cache = previous; }

        string ImagePath(string key) => Path.Combine(directory,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(prefix + key))).ToLowerInvariant() + ".img");

        GuardedStoreRecovery Park(string key, bool artwork)
        {
            var path = ImagePath(key);
            var slot = new GuardedStoreRecovery(path, artwork ? GuardedStoreKind.Artwork : GuardedStoreKind.Regular);
            using (var parents = UninstallPathGuard.HoldExistingAncestors(path))
            using (var gate = GuardedStoreFile.HoldWriteGate(path))
            using (var original = UninstallPathGuard.OpenOwnedFile(path, read: true))
            using (var staged = UninstallPathGuard.CreateStagedFile(path + ".fixture-stage", deleteOnClose: true))
            {
                RandomAccess.Write(staged, blue, 0);
                RandomAccess.FlushToDisk(staged);
                using var receipt = slot.Record(original, staged);
                ManagedCacheCleanup.RenameByHandle(original, slot.Previous);
            }
            Assert(slot.Pending && !File.Exists(path), "fixture did not leave an authenticated image at its native recovery slot");
            return slot;
        }
    }

    private static async Task HomeArtworkClearReturnedChanged()
    {
        const string uri = "https://avatars.yandex.net/get-music-content/1/returned-changed";
        var root = Directory.CreateDirectory(Path.Combine(_directory, "home-artwork-clear-returned-changed")).FullName;
        if (new DriveInfo(Path.GetPathRoot(root)!).DriveFormat != "NTFS") return;
        var previous = SolidBmp(Colors.Red);
        var changed = SolidBmp(Colors.Blue);
        Assert(previous.Length == changed.Length && !previous.AsSpan().SequenceEqual(changed), "fixture must produce same-size valid images");
        foreach (var readFirst in new[] { false, true })
        {
            var directory = Directory.CreateDirectory(Path.Combine(root, readFirst ? "read-first" : "clear-first")).FullName;
            var path = Path.Combine(directory, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(uri))).ToLowerInvariant() + ".img");
            File.WriteAllBytes(path, previous);
            File.SetAttributes(path, readFirst ? FileAttributes.Hidden : FileAttributes.Hidden | FileAttributes.Archive);
            var written = File.GetLastWriteTimeUtc(path);
            var slot = HomeArtworkParkReplacement(path, HomeArtworkPng(Colors.Green), publish: false);
            using (var parents = UninstallPathGuard.HoldExistingAncestors(path))
            using (var writer = GuardedStoreFile.HoldWriteGate(path))
            using (var original = UninstallPathGuard.OpenOwnedFile(slot.Previous, read: true))
                ManagedCacheCleanup.RenameByHandle(original, path);
            using (var edit = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.Read)) edit.Write(changed);
            File.SetLastWriteTimeUtc(path, written);
            Assert(slot.Pending && !File.Exists(slot.Previous), "fixture did not leave the returned object and its receipt");

            using var cache = new ArtworkCache(directory, new FakeHandler((request, _) =>
            {
                HomeArtworkAnonymous(request);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            }));
            if (readFirst)
                Assert(await cache.GetAsync(uri, 128, default) is null, "disk read served changed bytes as authenticated artwork");
            var result = await cache.ClearAsync(default);
            var archive = Directory.EnumerateFiles(directory, "*.previous.conflict-*.bak").Single();
            Assert(result.Removed == 0 && result.Preserved == 1 && result.Failed == 0 && !slot.Pending && !File.Exists(path)
                && File.ReadAllBytes(archive).SequenceEqual(changed), "clear failed to preserve the verified returned object's changed bytes");
        }

        static byte[] SolidBmp(Color color)
        {
            var pixels = new[] { color.B, color.G, color.R, color.A };
            var bitmap = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, pixels, 4);
            var encoder = new BmpBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var data = new MemoryStream();
            encoder.Save(data);
            return data.ToArray();
        }
    }

    private static async Task HomeArtworkClearConflict()
    {
        const string uri = "https://avatars.yandex.net/get-music-content/1/witnessed-conflict";
        var root = Directory.CreateDirectory(Path.Combine(_directory, "home-artwork-witnessed-conflict")).FullName;
        if (new DriveInfo(Path.GetPathRoot(root)!).DriveFormat != "NTFS") return;
        var previous = HomeArtworkPng(Colors.Red);
        var occupant = HomeArtworkPng(Colors.Blue);
        foreach (var readFirst in new[] { false, true })
        {
            var directory = Directory.CreateDirectory(Path.Combine(root, readFirst ? "read-first" : "clear-first")).FullName;
            var path = Path.Combine(directory, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(uri))).ToLowerInvariant() + ".img");
            File.WriteAllBytes(path, previous);
            var slot = HomeArtworkParkReplacement(path, HomeArtworkPng(Colors.Green), publish: false);
            File.WriteAllBytes(path, occupant);
            using var cache = new ArtworkCache(directory, new FakeHandler((request, _) =>
            {
                HomeArtworkAnonymous(request);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            }));
            if (readFirst)
            {
                var receipt = Directory.EnumerateFiles(directory, "*.receipt").Single();
                var receiptBytes = File.ReadAllBytes(receipt);
                Assert(await cache.GetAsync(uri, 128, default) is null
                    && File.ReadAllBytes(receipt).SequenceEqual(receiptBytes)
                    && File.ReadAllBytes(slot.Previous).SequenceEqual(previous)
                    && File.ReadAllBytes(path).SequenceEqual(occupant)
                    && !Directory.EnumerateFiles(directory, "*.protected").Any(),
                    "a cold read served conflicting artwork or altered the pending slot");
            }

            for (var pass = 0; pass < 2; pass++)
            {
                if (pass == 1)
                {
                    var created = File.GetCreationTimeUtc(path);
                    File.SetCreationTimeUtc(path, created.AddHours(-2));
                    Assert(File.GetCreationTimeUtc(path) != created, "fixture did not change the creation time of the protected image");
                }
                Assert(await cache.ClearAsync(default) == new CacheCleanupResult(0, 2, 0, 0), "clear miscounted the two conflict copies");
                var archive = Directory.EnumerateFiles(directory, "*.previous.conflict-*.bak").Single();
                Assert(File.ReadAllBytes(path).SequenceEqual(occupant) && File.ReadAllBytes(archive).SequenceEqual(previous)
                    && !File.Exists(slot.Previous), "clear lost one of the conflict copies");
            }
        }
    }

    private static async Task HomeArtworkClearDetachedDiskReader()
    {
        const string source = "https://avatars.yandex.net/get-music-content/1/clear-detached-reader";
        var directory = Directory.CreateDirectory(Path.Combine(_directory, "home-artwork-clear-detached-reader")).FullName;
        var path = Path.Combine(directory,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source))).ToLowerInvariant() + ".img");
        var red = HomeArtworkPng(Colors.Red);
        var blue = HomeArtworkPng(Colors.Blue);
        File.WriteAllBytes(path, red);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var requests = 0;
        using var cache = new ArtworkCache(directory, new FakeHandler((request, _) =>
        {
            HomeArtworkAnonymous(request);
            Interlocked.Increment(ref requests);
            return Task.FromResult(HomeArtworkResponse(blue));
        }))
        {
            DiskReadOpenedForChecks = () => { entered.TrySetResult(); return release.Task; }
        };
        var generation = typeof(ArtworkCache).GetField("_clearCompletion", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var initial = generation.GetValue(cache);
        using var subscriber = new CancellationTokenSource();
        var abandoned = cache.GetAsync(source, 128, subscriber.Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            subscriber.Cancel();
            await Throws<OperationCanceledException>(() => abandoned.WaitAsync(TimeSpan.FromSeconds(5)));
            var clear = cache.ClearAsync(default);
            await Until(() => !ReferenceEquals(initial, generation.GetValue(cache)));
            var next = cache.GetAsync(source, 128, default, retryAfterClear: true);
            await Task.WhenAny(clear, Task.Delay(150));
            Assert(!clear.IsCompleted && !next.IsCompleted && requests == 0 && File.Exists(path),
                "clear or a fresh subscriber overlapped a detached disk read");
            release.SetResult();

            var deleted = await clear.WaitAsync(TimeSpan.FromSeconds(5));
            var bitmap = await next.WaitAsync(TimeSpan.FromSeconds(5));
            Assert(deleted.Removed == 1 && deleted.Failed == 0 && deleted.RemovedBytes == red.Length
                && bitmap is not null && HomeArtworkPixel(bitmap)[0] == 255 && requests == 1,
                "clear failed to remove the old disk image and admit a fresh subscriber after release");
        }
        finally { release.TrySetResult(); }
    }

    private static async Task HomeArtworkCancellationLeavesCacheAvailable()
    {
        const string source = "https://avatars.yandex.net/get-music-content/1/cancel-callback/200x200";
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelReleased = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCallback = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var red = HomeArtworkPng(Colors.Red);
        var blue = HomeArtworkPng(Colors.Blue);
        var requests = 0;
        using var cache = new ArtworkCache(Path.Combine(_directory, "home-artwork-cancel-callback"), new FakeHandler(async (request, token) =>
        {
            HomeArtworkAnonymous(request);
            if (Interlocked.Increment(ref requests) != 1) return HomeArtworkResponse(blue);
            using var registration = token.Register(() =>
            {
                cancelEntered.SetResult();
                try { releaseCallback.Task.Wait(TimeSpan.FromSeconds(10)); }
                finally { cancelReleased.SetResult(); }
            });
            entered.SetResult();
            await releaseFirst.Task;
            return HomeArtworkResponse(red);
        }));
        using var cancellation = new CancellationTokenSource();
        var old = cache.GetAsync(source, 128, cancellation.Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var cancelSubscriber = Task.Run(cancellation.Cancel);
            await cancelEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var next = Task.Run(() => cache.GetAsync(source, 128, default));
            var bitmap = await next.WaitAsync(TimeSpan.FromSeconds(5));
            Assert(!cancelReleased.Task.IsCompleted && bitmap is not null && HomeArtworkPixel(bitmap)[0] == 255 && requests == 2,
                "a blocked cancellation callback prevented a fresh request from acquiring cache state");
            await cancelSubscriber.WaitAsync(TimeSpan.FromSeconds(5));
            await Throws<OperationCanceledException>(() => old.WaitAsync(TimeSpan.FromSeconds(5)));
            releaseCallback.SetResult();
            releaseFirst.SetResult();
            await cancelReleased.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert(ReferenceEquals(bitmap, await cache.GetAsync(source, 128, default)) && requests == 2,
                "the detached load replaced the fresh cached result");
        }
        finally { releaseCallback.TrySetResult(); releaseFirst.TrySetResult(); }
    }

    private static async Task HomeArtworkClearBeforeCancellation()
    {
        const string blockingSource = "https://avatars.yandex.net/get-music-content/1/blocked/200x200";
        const string racingSource = "https://avatars.yandex.net/get-music-content/1/racing/200x200";
        var blockerEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var racingEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellationEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseBlocker = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRacing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCancellation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var requests = 0;
        var red = HomeArtworkPng(Colors.Red);
        var blue = HomeArtworkPng(Colors.Blue);
        using var cache = new ArtworkCache(Path.Combine(_directory, "home-artwork-clear-publication"), new FakeHandler(async (request, token) =>
        {
            if (request.RequestUri!.AbsoluteUri == blockingSource)
            {
                using var registration = token.Register(() =>
                {
                    cancellationEntered.SetResult();
                    releaseCancellation.Task.Wait(TimeSpan.FromSeconds(10));
                });
                blockerEntered.SetResult();
                await releaseBlocker.Task;
                token.ThrowIfCancellationRequested();
            }
            if (Interlocked.Increment(ref requests) > 1) return HomeArtworkResponse(blue);
            racingEntered.SetResult();
            await releaseRacing.Task;
            return HomeArtworkResponse(red);
        }));
        var blocker = cache.GetAsync(blockingSource, 256, default);
        try
        {
            await blockerEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var old = cache.GetAsync(racingSource, 256, default);
            var visible = cache.GetAsync(racingSource, 256, default, retryAfterClear: true);
            await racingEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var clear = cache.ClearAsync(default);
            await cancellationEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            releaseRacing.SetResult();
            await Throws<OperationCanceledException>(() => old.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert(!visible.IsCompleted && requests == 1, "artwork returned or retried while cache clear was still active");
            releaseCancellation.SetResult();
            releaseBlocker.SetResult();
            await clear.WaitAsync(TimeSpan.FromSeconds(5));
            await Throws<OperationCanceledException>(() => blocker.WaitAsync(TimeSpan.FromSeconds(5)));
            var bitmap = await visible.WaitAsync(TimeSpan.FromSeconds(5));
            Assert(bitmap is not null && requests == 2 && HomeArtworkPixel(bitmap)[0] == 255
                && ReferenceEquals(bitmap, await cache.GetAsync(racingSource, 256, default)), "stale artwork survived the clear or replaced its fresh result");
        }
        finally
        {
            releaseRacing.TrySetResult();
            releaseCancellation.TrySetResult();
            releaseBlocker.TrySetResult();
        }
    }

    private static async Task HomeArtworkClearRecovery()
    {
        const string prefix = "https://avatars.yandex.net/get-music-content/1/clear-recovery/";
        var red = HomeArtworkPng(Colors.Red);
        var blue = HomeArtworkPng(Colors.Blue);
        var transientRequests = 0;
        var missingRequests = 0;
        using var cache = new ArtworkCache(Path.Combine(_directory, "home-artwork-clear-recovery"), new FakeHandler((request, _) =>
        {
            HomeArtworkAnonymous(request);
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/stable", StringComparison.Ordinal)) return Task.FromResult(HomeArtworkResponse(red));
            if (path.EndsWith("/missing", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref missingRequests);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            }
            return Task.FromResult(Interlocked.Increment(ref transientRequests) <= 2
                ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) : HomeArtworkResponse(blue));
        }));
        var first = new Image(); var second = new Image(); var recycled = new Image();
        var stable = new Image(); var missing = new Image();
        var track = new TrackRow { Id = "home-artwork-clear-recovery", Title = "Paused fixture track" };
        using var accent = new CoverAccentController(first, new ResourceDictionary(), () => track);
        var window = new Window
        {
            Left = -16000, Width = 200, Height = 200, ShowActivated = false, ShowInTaskbar = false,
            Content = new StackPanel { Children = { first, second, recycled, stable, missing } }
        };
        var previous = Artwork.Cache;
        Artwork.Cache = cache;
        try
        {
            window.Show();
            foreach (var image in new[] { first, second, recycled }) Artwork.SetSourceUri(image, prefix + "current");
            Artwork.SetSourceUri(stable, prefix + "stable");
            Artwork.SetSourceUri(missing, prefix + "missing");
            await Until(() => Artwork.GetLoadStatus(stable) == ArtworkLoadStatus.Ready
                && new[] { first, second, recycled, missing }.All(image => Artwork.GetLoadStatus(image) == ArtworkLoadStatus.Unavailable));
            Assert(transientRequests == 2 && missingRequests == 1, "the initial transient and permanent requests did not use their own limits");
            var stableBitmap = stable.Source;
            Artwork.SetSourceUri(recycled, null);

            await cache.ClearAsync(default);
            await Until(() => Artwork.GetLoadStatus(first) == ArtworkLoadStatus.Ready && Artwork.GetLoadStatus(second) == ArtworkLoadStatus.Ready);
            await accent.PendingAnalysis;
            Assert(transientRequests == 3 && ReferenceEquals(first.Source, second.Source)
                && accent.TargetColor == CoverPalette.Readable(System.Windows.Media.Colors.Blue),
                "the unchanged cover did not share one fresh request and update its accent after clearing");
            Assert(ReferenceEquals(stableBitmap, stable.Source) && recycled.Source is null
                && Artwork.GetLoadStatus(recycled) == ArtworkLoadStatus.None
                && missing.Source is null && Artwork.GetLoadStatus(missing) == ArtworkLoadStatus.Unavailable && missingRequests == 1,
                "clearing restarted a ready, recycled, or permanently missing cover");
        }
        finally { window.Close(); Artwork.Cache = previous; }
    }

    private static async Task HomeArtworkDispatcherAfterClear()
    {
        const string prefix = "https://avatars.yandex.net/get-music-content/1/dispatcher-clear/";
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var red = HomeArtworkPng(Colors.Red);
        var blue = HomeArtworkPng(Colors.Blue);
        var requests = 0;
        using var cache = new ArtworkCache(Path.Combine(_directory, "home-artwork-dispatcher-clear"), new FakeHandler(async (request, token) =>
        {
            HomeArtworkAnonymous(request);
            if (request.RequestUri!.AbsolutePath.EndsWith("/stable", StringComparison.Ordinal)) return HomeArtworkResponse(blue);
            if (Interlocked.Increment(ref requests) > 1) return HomeArtworkResponse(blue);
            await releaseFirst.Task.WaitAsync(token);
            return HomeArtworkResponse(red);
        }));
        var first = new Image(); var recycled = new Image(); var stable = new Image();
        var window = new Window
        {
            Left = -16000, Width = 200, Height = 200, ShowActivated = false, ShowInTaskbar = false,
            Content = new StackPanel { Children = { first, recycled, stable } }
        };
        var previous = Artwork.Cache;
        Artwork.Cache = cache;
        var observedBlue = new List<byte>();
        var sourceProperty = DependencyPropertyDescriptor.FromProperty(Image.SourceProperty, typeof(Image));
        EventHandler observe = (_, _) => { if (first.Source is BitmapSource bitmap) observedBlue.Add(HomeArtworkPixel(bitmap)[0]); };
        sourceProperty.AddValueChanged(first, observe);
        try
        {
            window.Show();
            Artwork.SetSourceUri(stable, prefix + "stable");
            await Until(() => first.IsLoaded && recycled.IsLoaded && Artwork.GetLoadStatus(stable) == ArtworkLoadStatus.Ready);
            var stableBitmap = stable.Source;
            var firstDelivery = BindHeld(first);
            var recycledDelivery = BindHeld(recycled);
            releaseFirst.SetResult();
            var deliver = await Task.WhenAll(firstDelivery, recycledDelivery).WaitAsync(TimeSpan.FromSeconds(5));
            Assert(Artwork.GetLoadStatus(first) == ArtworkLoadStatus.Loading && first.Source is null && requests == 1,
                "fixture did not hold the completed result before publishing it");

            await cache.ClearAsync(default);
            Artwork.SetSourceUri(recycled, null);
            foreach (var callback in deliver) callback();
            await Until(() => Artwork.GetLoadStatus(first) == ArtworkLoadStatus.Ready);
            Assert(requests == 2 && observedBlue.SequenceEqual(new byte[] { 255 })
                && ReferenceEquals(stableBitmap, stable.Source) && recycled.Source is null,
                "a completed old-generation bitmap crossed the dispatcher or the replacement was lost");
        }
        finally
        {
            releaseFirst.TrySetResult();
            sourceProperty.RemoveValueChanged(first, observe);
            window.Close(); Artwork.Cache = previous;
        }

        Task<Action> BindHeld(Image image)
        {
            var context = new HomeArtworkHeldDelivery(window.Dispatcher);
            var original = SynchronizationContext.Current;
            try
            {
                SynchronizationContext.SetSynchronizationContext(context);
                Artwork.SetSourceUri(image, prefix + "current");
                return context.Held.Task;
            }
            finally { SynchronizationContext.SetSynchronizationContext(original); }
        }
    }

    private sealed class HomeArtworkHeldDelivery(Dispatcher dispatcher) : SynchronizationContext
    {
        internal TaskCompletionSource<Action> Held { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override void Post(SendOrPostCallback callback, object? state)
        {
            if (!Held.TrySetResult(() => callback(state))) dispatcher.BeginInvoke(callback, state);
        }
    }

    private static async Task HomeArtworkRedirects()
    {
        const string source = "https://avatars.yandex.net/get-music-user-playlist/1/fixture/%%?1";
        foreach (var allowed in new[] { true, false })
        {
            var requests = 0;
            using var cache = new ArtworkCache(Path.Combine(_directory, "home-artwork-redirect-" + allowed), new FakeHandler((request, _) =>
            {
                HomeArtworkAnonymous(request); requests++;
                if (requests == 1)
                {
                    var redirect = new HttpResponseMessage(HttpStatusCode.Found);
                    redirect.Headers.Location = new Uri("https://" + (allowed ? "avatars.mds.yandex.net" : "evil.test") + "/get-music-user-playlist/1/fixture/400x400?2");
                    return Task.FromResult(redirect);
                }
                Assert(allowed && request.RequestUri!.Host == "avatars.mds.yandex.net" && request.RequestUri.Query == "?2", "redirect escaped its allowed CDN contract");
                return Task.FromResult(HomeArtworkResponse(HomeArtworkPng(Colors.Blue)));
            }));
            var bitmap = await cache.GetAsync(source, 256, CancellationToken.None);
            Assert(allowed ? bitmap is not null && requests == 2 : bitmap is null && requests == 1, "versioned redirect followed an unsafe host or rejected the allowed CDN");
        }
    }

    private static async Task HomeArtworkContentRange()
    {
        const string source = "https://avatars.yandex.net/get-music-content/1/range/400x400";
        var directory = Path.Combine(_directory, "home-artwork-content-range");
        var png = HomeArtworkPng(Colors.Blue);
        var requests = 0;
        using var cache = new ArtworkCache(directory, new FakeHandler((request, _) =>
        {
            HomeArtworkAnonymous(request);
            var response = HomeArtworkResponse(png);
            if (Interlocked.Increment(ref requests) == 1)
                Assert(response.Content.Headers.TryAddWithoutValidation("Content-Range", "invalid range"), "could not attach raw fixture header");
            return Task.FromResult(response);
        }));
        Assert(await cache.GetAsync(source, 256, default) is null && !Directory.Exists(directory),
            "an unparseable Content-Range reached the memory or disk artwork cache");
        Assert(await cache.GetAsync(source, 256, default) is { IsFrozen: true } && requests == 2,
            "rejecting the range prevented a later complete cover from loading");
    }

    private static async Task HomeArtworkMimeTypes()
    {
        const string prefix = "https://avatars.yandex.net/get-music-content/1/mime/";
        var directory = Path.Combine(_directory, "home-artwork-mime-types");
        var png = HomeArtworkPng(Colors.Blue);
        using var cache = new ArtworkCache(directory, new FakeHandler((request, _) =>
        {
            HomeArtworkAnonymous(request);
            var response = HomeArtworkResponse(png);
            response.Content.Headers.ContentType = new(request.RequestUri!.AbsolutePath.EndsWith("/octets", StringComparison.Ordinal)
                ? "Application/OctEt-Stream" : "application/json");
            return Task.FromResult(response);
        }));
        Assert(await cache.GetAsync(prefix + "octets", 256, default) is { IsFrozen: true }, "artwork rejected a mixed-case octet stream");
        Assert(await cache.GetAsync(prefix + "json", 256, default) is null && Directory.EnumerateFiles(directory, "*.img").Count() == 1,
            "supporting mixed-case octet stream accepted an unrelated MIME type");
    }

    private static async Task HomeArtworkCards()
    {
        var titles = new[] { "Премьера", "Дежавю", "Плейлист дня" };
        var apiRequests = 0;
        using var api = new MusicApi(new FakeHandler((request, _) =>
        {
            Assert(request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath == "/landing3", "Home cover parsing requested an unrelated endpoint");
            apiRequests++;
            return Task.FromResult(Json(JsonSerializer.Serialize(new { result = new { blocks = new[] {
                new { type = "personal-playlists", title = "Собираем для вас", entities = titles.Select((title, index) => new {
                    type = "personal-playlist", data = new { ready = true, data = new { uid = 12345, kind = 201 + index, title,
                        cover = new { type = "pic", uri = $"avatars.yandex.net/get-music-user-playlist/1/card-{index}/%%?173045332247{index}" } } }
                }).ToArray() }
            } } })));
        }));
        var sections = await api.GetHomeSectionsAsync(FakeSession, CancellationToken.None);
        Assert(apiRequests == 1 && sections.Single().Items.Count == 3 && sections.Single().Items.All(i => i.CoverUri is not null),
            "personal playlists lost versioned covers while parsing the landing response");
        var cacheField = typeof(Artwork).GetField("_cache", BindingFlags.NonPublic | BindingFlags.Static)!;
        var previous = cacheField.GetValue(null);
        var cdnRequests = 0;
        using var artwork = new ArtworkCache(Path.Combine(_directory, "home-artwork-cards"), new FakeHandler((request, _) =>
        {
            HomeArtworkAnonymous(request); Interlocked.Increment(ref cdnRequests);
            Assert(request.RequestUri!.AbsolutePath.EndsWith("/200x200"), "Home did not request its actual 200px CDN rendition");
            return Task.FromResult(HomeArtworkResponse(HomeArtworkPng(Colors.Coral)));
        }));
        Artwork.Cache = artwork;
        try
        {
            using var fixture = new SaFixture("home-artwork");
            await fixture.Activate();
            SaInvoke(fixture.Window, "SetSection", "home", "Главная");
            SaInvoke(fixture.Window, "ShowHomeSections", new object[] { sections });
            fixture.Window.UpdateLayout();
            var images = SaVisuals<Image>(Control<ItemsControl>(fixture.Window, "HomeShelves")).Where(i => Artwork.GetSourceUri(i) is not null).ToArray();
            Assert(images.Length == 3, "Home XAML dropped a personal playlist cover binding");
            await Until(() => images.All(i => i.Source is BitmapSource { IsFrozen: true }));
            Assert(cdnRequests == 3, "Home artwork did not load one anonymous image per visible card");
            await Task.Delay(350);
            App.Capture(fixture.Window, Path.Combine(_directory, "home-artwork-personal-playlists.png"));
        }
        finally { cacheField.SetValue(null, previous); }
    }

    private static void HomeArtworkAnonymous(HttpRequestMessage request)
        => Assert(request.Method == HttpMethod.Get && ArtworkUri.Valid(request.RequestUri!) && request.Headers.Authorization is null
            && !request.Headers.Contains("Cookie") && !request.Headers.Contains("X-Yandex-Music-Client"), "artwork request leaked private headers or left the CDN allowlist");
    private static HttpResponseMessage HomeArtworkResponse(byte[] bytes)
        => new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) { Headers = { ContentType = new("image/png") } } };
    private static byte[] HomeArtworkPng(Color color)
    {
        var bitmap = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[] { color.B, color.G, color.R, color.A }, 4);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream(); encoder.Save(stream); return stream.ToArray();
    }
    private static byte[] HomeArtworkPixel(BitmapSource source)
    {
        var converted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        var pixel = new byte[4]; converted.CopyPixels(pixel, 4, 0); return pixel;
    }
}
