using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using YMusic.Native;

internal static partial class Program
{
    private static async Task CheckBrandMigration()
    {
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
            Directory.CreateDirectory(paths.Data); File.WriteAllText(Path.Combine(paths.Data, "session.dpapi"), "current fake encrypted bytes");
            Directory.CreateDirectory(paths.LegacyData); File.WriteAllText(Path.Combine(paths.LegacyData, "session.dpapi"), "old fake encrypted bytes");
            WriteUninstallAudio(paths, "new.mp3", "new managed audio");
            Directory.CreateDirectory(paths.LegacyMusic);
            var audio = Encoding.UTF8.GetBytes("old managed audio");
            File.WriteAllBytes(Path.Combine(paths.LegacyMusic, "old.mp3"), audio);
            File.WriteAllText(Path.Combine(paths.LegacyMusic, "library.index.json"), JsonSerializer.Serialize(new[] {
                new UninstallFixtureAudio("old.mp3", audio.Length, Convert.ToHexString(SHA256.HashData(audio))) }));
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

    private static BrandPaths BrandFixture(string name)
    {
        var root = Path.GetFullPath(Path.Combine(_directory, "brand-migration", name + "-" + Guid.NewGuid().ToString("N")));
        Assert(root.StartsWith(Path.GetFullPath(_directory) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase), "brand fixture escaped results directory");
        var paths = BrandPaths.FromKnownFolders(Path.Combine(root, "LocalAppData"), Path.Combine(root, "Documents"));
        Directory.CreateDirectory(paths.LocalData); Directory.CreateDirectory(paths.Documents);
        return paths;
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
