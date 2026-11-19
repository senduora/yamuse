using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Controls;
using YMusic.Native;

internal static partial class Program
{
    private static async Task CheckPlaybackPersistence()
    {
        await Run("Playback persistence: immutable metadata, duplicate occurrences, current index and modes roundtrip", () =>
        {
            var directory = PersistenceDirectory("roundtrip");
            var store = new PlaybackStateStore(directory);
            var shared = PersistenceTrack("101", "101:201");
            var queue = new PlaybackQueue(new Random(42));
            queue.SetItems([shared, PersistenceTrack("102"), shared]);
            queue.SelectAt(2); queue.Shuffle = true; queue.Repeat = RepeatMode.One;
            var snapshot = PlaybackStateStore.Capture("12345", queue, 77.25);
            shared.DownloadedPath = Path.Combine(directory, "credential-must-not-be-persisted.mp3");
            store.Save(snapshot);
            var json = File.ReadAllText(Path.Combine(directory, PlaybackStateStore.FileName));
            Assert(!json.Contains("http", StringComparison.OrdinalIgnoreCase) && !json.Contains("LocalPath", StringComparison.OrdinalIgnoreCase)
                && !json.Contains("DownloadedPath", StringComparison.OrdinalIgnoreCase) && !json.Contains("credential-must-not-be-persisted")
                && !json.Contains("fake-test-token"), "snapshot persisted a media location or credential");
            var saved = store.Load("12345")!;
            using var offline = new OfflineLibrary(Path.Combine(directory, "music"));
            var restored = PlaybackStateStore.RestoreQueue(saved, offline);
            var replay = new PlaybackQueue(new Random(42));
            replay.RestoreItems(restored.Tracks, restored.CurrentIndex, saved.Shuffle, saved.Repeat);
            Assert(store.LastReadStatus == PlaybackStateReadStatus.Loaded && saved.Version == 1 && saved.AccountUid == "12345", "snapshot header changed");
            Assert(replay.Items.Select(row => row.RequestId).SequenceEqual(new[] { "101:201", "102", "101:201" })
                && replay.CurrentIndex == 2 && ReferenceEquals(replay.Current, replay.Items[2]), "duplicate occurrence or current mapping was lost");
            Assert(!ReferenceEquals(replay.Items[0], replay.Items[2]) && replay.Items[0].Artists[0] == shared.Artists[0]
                && replay.Shuffle && replay.Repeat == RepeatMode.One && restored.PositionSeconds == 77.25, "metadata or modes changed");
            Assert(replay.Items.All(row => row.LocalPath is null && row.DownloadedPath is null && !row.IsPlaying), "restored unindexed source or playing flag");
            try { ((IList<PlaybackTrackState>)saved.Tracks)[0] = saved.Tracks[1]; throw new InvalidOperationException("mutable snapshot tracks"); }
            catch (NotSupportedException) { }
            try { ((IList<ArtistReference>)saved.Tracks[0].Artists)[0] = new("999", "changed"); throw new InvalidOperationException("mutable snapshot artists"); }
            catch (NotSupportedException) { }
            return Task.CompletedTask;
        });

        await Run("Playback persistence: two explicit Play Next occurrences keep their order after shuffle restart", () =>
        {
            var directory = PersistenceDirectory("play-next-order");
            var store = new PlaybackStateStore(directory);
            var queue = new PlaybackQueue(new Random(41));
            queue.SetItems([PersistenceTrack("101"), PersistenceTrack("102")]);
            queue.Shuffle = true;
            var repeated = PersistenceTrack("103", "103:12");
            queue.Enqueue(repeated, playNext: true);
            queue.Enqueue(repeated, playNext: true);
            queue.MoveMany([1], 3);
            var snapshot = PlaybackStateStore.Capture("12345", queue, 29);
            Assert(snapshot.PlayNextIndices?.SequenceEqual([2, 1]) == true, "checkpoint did not record exact priority positions");

            // A tombstone left after clearing is a valid existing state to replace.
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, PlaybackStateStore.FileName), """{"version":0}""");
            store.SaveVerifiedCheckpoint(snapshot);
            var saved = store.Load("12345")!;
            using var offline = new OfflineLibrary(Path.Combine(directory, "music"));
            var restored = PlaybackStateStore.RestoreQueue(saved, offline);
            var replay = new PlaybackQueue(new Random(99));
            replay.RestoreItems(restored.Tracks, restored.CurrentIndex, saved.Shuffle, saved.Repeat, restored.PlayNextIndices);
            var first = replay.OccurrenceTokenAt(2);
            var second = replay.OccurrenceTokenAt(1);
            Assert(replay.CurrentIndex == 0 && replay.PeekNextOccurrenceToken() == first, "restore changed current or lost latest Play Next priority");
            _ = replay.Next();
            Assert(replay.CurrentOccurrenceToken == first && replay.PeekNextOccurrenceToken() == second, "shuffle reordered the identical priority occurrences");
            _ = replay.Next();
            Assert(replay.CurrentOccurrenceToken == second && replay.Next()?.Id == "102" && replay.Next() is null,
                "restored priorities reappeared in the ordinary shuffle bag");
            return Task.CompletedTask;
        });
        await Run("Playback persistence: position clamps to metadata duration and unavailable current stays exact", () =>
        {
            var directory = PersistenceDirectory("position");
            var store = new PlaybackStateStore(directory);
            var queue = new PlaybackQueue(); queue.SetItems([PersistenceTrack("101"), PersistenceTrack("102", available: false)]);
            queue.RestoreItems(queue.Items, 1, true, RepeatMode.All);
            Assert(queue.CurrentIndex == 1 && queue.Current?.Id == "102", "unavailable current was skipped on restore");
            foreach (var pair in new[] { (double.NaN, 0d), (double.PositiveInfinity, 0d), (-4d, 0d), (250d, 180d), (71.125d, 71.125d) })
                Assert(PlaybackStateStore.Capture("12345", queue, pair.Item1).PositionSeconds == pair.Item2, "capture position was not bounded");
            store.Save(PlaybackStateStore.Capture("12345", queue, 71));
            var path = Path.Combine(directory, PlaybackStateStore.FileName);
            var json = JsonNode.Parse(File.ReadAllText(path))!; json["positionSeconds"] = 99999; File.WriteAllText(path, json.ToJsonString());
            Assert(store.Load("12345") is { PositionSeconds: 180 }, "overshooting loaded position was not clamped");
            var duplicate = PersistenceTrack("103");
            queue.SetItemsAt([duplicate, duplicate], 1);
            var staleToken = queue.OccurrenceTokenAt(0);
            var currentToken = queue.OccurrenceTokenAt(1);
            var native = TimeSpan.FromSeconds(240);
            var stale = PlaybackStateStore.Capture("12345", queue, 210, observedDuration: (staleToken, native));
            var exact = PlaybackStateStore.Capture("12345", queue, 210, observedDuration: (currentToken, native));
            Assert(stale.PositionSeconds == 180 && exact.CurrentIndex == 1 && exact.PositionSeconds == 210
                && stale.PositionDurationMs is null && exact.PositionDurationMs == 240000
                && exact.Tracks[0].DurationMs == 180000 && exact.Tracks[1].DurationMs == 180000,
                "an observed duration was lost, propagated to another occurrence or stored as catalogue metadata");
            var sample = PlaybackStateStore.Capture("12345", queue, 44, observedDuration: (currentToken, TimeSpan.FromSeconds(45)));
            store.Save(sample);
            var reopened = store.Load("12345") ?? throw new InvalidOperationException("missing preview checkpoint fixture");
            using var offline = new OfflineLibrary(Path.Combine(directory, "preview-library"));
            var restored = PlaybackStateStore.RestoreQueue(reopened, offline);
            var resumed = restored.Tracks[restored.CurrentIndex];
            var looksLikePreview = typeof(MainWindow).GetMethod("LooksLikePreview", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
            Assert(restored.PositionSeconds == 44 && restored.PositionDurationMs == 45000 && resumed.DurationMs == 180000
                && looksLikePreview.Invoke(null, [resumed, TimeSpan.FromSeconds(30)]) is true,
                "reopening an accepted source disabled the catalogue preview check");
            var empty = new PlaybackQueue();
            Assert(PlaybackStateStore.Capture("12345", empty, 100).PositionSeconds == 0, "empty queue retained position");
            return Task.CompletedTask;
        });

        await Run("Playback persistence: malformed, missing, unknown, invalid and oversized data recover without overwrite", () =>
        {
            var directory = PersistenceDirectory("corrupt");
            var store = new PlaybackStateStore(directory);
            Assert(store.Load("12345") is null && store.LastReadStatus == PlaybackStateReadStatus.Missing, "missing file was not empty");
            var queue = new PlaybackQueue(); queue.SetItems([PersistenceTrack("101")]);
            var valid = PlaybackStateStore.Capture("12345", queue, 12);
            store.Save(valid);
            var path = Path.Combine(directory, PlaybackStateStore.FileName);
            var good = File.ReadAllText(path);
            var bad = new List<string> { "{", "null", "{}", good.Replace("\"version\":1", "\"version\":99", StringComparison.Ordinal) };
            foreach (var (key, value) in new (string, JsonNode?)[] { ("currentIndex", JsonValue.Create(2)), ("positionSeconds", JsonValue.Create(-1)),
                ("localPath", JsonValue.Create("C:\\Windows\\secret.mp3")), ("accountUid", JsonValue.Create("../another")), ("repeat", JsonValue.Create(7)) })
            { var node = JsonNode.Parse(good)!; node[key] = value; bad.Add(node.ToJsonString()); }
            var invalidTrack = JsonNode.Parse(good)!; invalidTrack["tracks"]![0]!["requestId"] = "https://cdn.invalid/signed"; bad.Add(invalidTrack.ToJsonString());
            invalidTrack = JsonNode.Parse(good)!; invalidTrack["tracks"]![0]!["durationMs"] = PlaybackStateStore.MaximumDurationMs + 1; bad.Add(invalidTrack.ToJsonString());
            invalidTrack = JsonNode.Parse(good)!; invalidTrack["tracks"]![0]!["artists"] = null; bad.Add(invalidTrack.ToJsonString());
            foreach (var text in bad)
            {
                File.WriteAllText(path, text);
                Assert(store.Load("12345") is null && store.LastReadStatus == PlaybackStateReadStatus.Invalid, "invalid JSON state was accepted");
                Assert(File.ReadAllText(path) == text, "read-only recovery overwrote corrupt evidence");
            }
            using (var oversized = new FileStream(path, FileMode.Create, FileAccess.Write)) oversized.SetLength(PlaybackStateStore.MaximumBytes + 1L);
            Assert(store.Load("12345") is null && store.LastReadStatus == PlaybackStateReadStatus.Invalid, "oversized input was read");
            store.Save(valid);
            Assert(store.Load("12345") is { PositionSeconds: 12 }, "valid save did not recover the corrupt snapshot");
            return Task.CompletedTask;
        });

        await Run("Playback persistence: account isolation, unknown owner, disabled reads and logout deletion", () =>
        {
            var directory = PersistenceDirectory("account");
            var store = new PlaybackStateStore(directory);
            var queue = new PlaybackQueue(); queue.SetItems([PersistenceTrack("101")]);
            store.Save(PlaybackStateStore.Capture("12345", queue, 12));
            var path = Path.Combine(directory, PlaybackStateStore.FileName); var original = File.ReadAllBytes(path);
            Assert(store.Load("67890") is null && store.LastReadStatus == PlaybackStateReadStatus.OtherAccount, "another account received queue metadata");
            Assert(store.Load(null) is null && store.LastReadStatus == PlaybackStateReadStatus.Disabled, "unknown account received queue metadata");
            Assert(store.Load("12345", enabled: false) is null && store.LastReadStatus == PlaybackStateReadStatus.Disabled, "disabled restore read a snapshot");
            Assert(original.SequenceEqual(File.ReadAllBytes(path)), "isolation checks modified another account's snapshot");
            store.Clear("12345", out _);
            Assert(!File.Exists(path) && store.Load("12345") is null, "logout snapshot survived");
            store.Clear("12345", out _);
            return Task.CompletedTask;
        });

        await Run("Playback persistence: an exact logout marker is missing state and can be replaced", () =>
        {
            var directory = PersistenceDirectory("logout-marker");
            var path = Path.Combine(directory, PlaybackStateStore.FileName);
            var store = new PlaybackStateStore(directory);
            foreach (var other in new[] { "{\"version\":0 }", "{\"version\":0,\"tracks\":[]}", "{\"version\":99}" })
            {
                File.WriteAllText(path, other);
                Assert(store.Load("12345") is null && store.LastReadStatus == PlaybackStateReadStatus.Invalid,
                    "unknown or malformed state was accepted as a logout marker");
            }
            File.WriteAllBytes(path, "{\"version\":0}"u8.ToArray());
            Assert(store.Load("12345") is null && store.LastReadStatus == PlaybackStateReadStatus.Missing,
                "an exact logout marker blocked later playback saving");
            var queue = new PlaybackQueue(); queue.SetItems([PersistenceTrack("101")]);
            store.Save(PlaybackStateStore.Capture("12345", queue, 17));
            Assert(store.Load("12345") is { PositionSeconds: 17 }, "new state did not replace the logout marker");
            return Task.CompletedTask;
        });

        await Run("Playback persistence: failed atomic replacement and bounded save preserve previous snapshot", () =>
        {
            var directory = PersistenceDirectory("atomic");
            var store = new PlaybackStateStore(directory);
            var queue = new PlaybackQueue(); queue.SetItems([PersistenceTrack("101")]);
            var snapshot = PlaybackStateStore.Capture("12345", queue, 12); store.Save(snapshot);
            var path = Path.Combine(directory, PlaybackStateStore.FileName); var original = File.ReadAllBytes(path);
            using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                try { store.Save(snapshot with { PositionSeconds = 45 }); throw new InvalidOperationException("locked replacement unexpectedly succeeded"); }
                catch (IOException) { }
                Assert(original.SequenceEqual(File.ReadAllBytes(path)), "failed replacement changed the old snapshot");
            }
            var hugeTrack = snapshot.Tracks[0] with { Title = new string('a', 4096) };
            try { store.Save(snapshot with { Tracks = Array.AsReadOnly(Enumerable.Repeat(hugeTrack, PlaybackStateStore.MaximumTracks).ToArray()) }); throw new InvalidOperationException("oversized save succeeded"); }
            catch (JsonException) { }
            try { store.Save(snapshot with { Tracks = Array.AsReadOnly(Enumerable.Repeat(hugeTrack, PlaybackStateStore.MaximumTracks + 1).ToArray()) }); throw new InvalidOperationException("track cap was ignored"); }
            catch (JsonException) { }
            Assert(original.SequenceEqual(File.ReadAllBytes(path)) && !Directory.EnumerateFiles(directory, "*.tmp").Any(), "failed save damaged snapshot or left temporary state");
            store.Save(snapshot with { PositionSeconds = 45 });
            Assert(store.Load("12345") is { PositionSeconds: 45 }, "subsequent replacement did not recover");
            return Task.CompletedTask;
        });

        await Run("Playback persistence: replacement preserves a protected snapshot DACL and supported file properties", () =>
        {
            var directory = PersistenceDirectory("snapshot-dacl");
            var store = new PlaybackStateStore(directory);
            var queue = new PlaybackQueue(); queue.SetItems([PersistenceTrack("101")]);
            var snapshot = PlaybackStateStore.Capture("12345", queue, 12);
            store.Save(snapshot);
            var file = new FileInfo(Path.Combine(directory, PlaybackStateStore.FileName));
            var security = file.GetAccessControl(AccessControlSections.Access);
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: true);
            var fixtureSid = new SecurityIdentifier("S-1-5-21-111111111-222222222-333333333-4444");
            security.AddAccessRule(new FileSystemAccessRule(fixtureSid, FileSystemRights.ReadData, AccessControlType.Deny));
            file.SetAccessControl(security);
            var appliedSecurity = file.GetAccessControl(AccessControlSections.Access);
            Assert(appliedSecurity.AreAccessRulesProtected && appliedSecurity.GetAccessRules(true, false, typeof(SecurityIdentifier))
                .OfType<FileSystemAccessRule>().Any(rule => rule.IdentityReference.Equals(fixtureSid)),
                "fixture Windows ACL was not applied");
            var expected = appliedSecurity.GetSecurityDescriptorSddlForm(AccessControlSections.Access);
            File.SetAttributes(file.FullName, File.GetAttributes(file.FullName) | FileAttributes.Hidden | FileAttributes.NotContentIndexed);
            File.SetCreationTimeUtc(file.FullName, new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc));
            var expectedAttributes = File.GetAttributes(file.FullName);
            var expectedCreationTime = File.GetCreationTimeUtc(file.FullName);

            using (var held = File.Open(file.FullName, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                try { store.Save(snapshot with { PositionSeconds = 53 }); throw new InvalidOperationException("locked replacement unexpectedly succeeded"); }
                catch (IOException) { }
            }
            Assert(store.Load("12345") is { PositionSeconds: 12 }
                && file.GetAccessControl(AccessControlSections.Access).GetSecurityDescriptorSddlForm(AccessControlSections.Access) == expected
                && File.GetAttributes(file.FullName) == expectedAttributes && File.GetCreationTimeUtc(file.FullName) == expectedCreationTime,
                "failed replacement altered the previous snapshot, DACL or file properties");
            store.Save(snapshot with { PositionSeconds = 53 });
            Assert(store.Load("12345") is { PositionSeconds: 53 }
                && file.GetAccessControl(AccessControlSections.Access).GetSecurityDescriptorSddlForm(AccessControlSections.Access) == expected
                && File.GetAttributes(file.FullName) == expectedAttributes && File.GetCreationTimeUtc(file.FullName) == expectedCreationTime,
                "successful replacement lost the original DACL or supported file properties");
            return Task.CompletedTask;
        });

        await Run("Playback persistence: local paths rebind only from validated offline index with duplicate mapping", () =>
        {
            var directory = PersistenceDirectory("offline");
            var music = Path.Combine(directory, "music"); Directory.CreateDirectory(music);
            var media = Path.Combine(music, "fixture.mp3"); File.WriteAllBytes(media, [1, 2, 3, 4, 5]);
            var item = new OfflineItem("101", "101:201", "Indexed title", "Indexed artist", "Indexed album", 180000,
                "fixture.mp3", "mp3", 320, 5, new string('a', 64), DateTimeOffset.UtcNow);
            File.WriteAllText(Path.Combine(music, "library.index.json"), JsonSerializer.Serialize(new[] { item }));
            using var offline = new OfflineLibrary(music);
            var queue = new PlaybackQueue(); queue.SetItems([PersistenceTrack("102"), PersistenceTrack("101", "101:201"), PersistenceTrack("101", "101:201")]);
            queue.SelectAt(2);
            var snapshot = PlaybackStateStore.Capture("12345", queue, 77);
            var restored = PlaybackStateStore.RestoreQueue(snapshot, offline);
            Assert(restored.Tracks[0].DownloadedPath is null && restored.Tracks[1].DownloadedPath == media && restored.Tracks[2].DownloadedPath == media,
                "local rebinding bypassed indexed entries or lost duplicate");
            Assert(restored.Tracks[1].Title == "Fixture title" && restored.Tracks[1].LocalPath is null, "index metadata overwrote queue metadata");
            var local = PlaybackStateStore.RestoreQueue(snapshot, offline, localOnly: true);
            Assert(local.Tracks.Count == 2 && local.CurrentIndex == 1 && local.PositionSeconds == 77 && local.Tracks.All(track => !track.Available && track.IsDownloaded),
                "local subset changed duplicate/current mapping or retained online availability");
            File.WriteAllBytes(media, [1]);
            Assert(PlaybackStateStore.RestoreQueue(snapshot, offline, localOnly: true) is { Tracks.Count: 0, CurrentIndex: -1, PositionSeconds: 0 }, "invalid indexed media remained playable");
            return Task.CompletedTask;
        });

        await Run("Playback persistence: restored window stays paused without source or network; fixture disable and logout gate", () =>
        {
            var directory = PersistenceDirectory("window");
            var queue = new PlaybackQueue(); queue.SetItems([PersistenceTrack("101"), PersistenceTrack("101")]); queue.SelectAt(1);
            var store = new PlaybackStateStore(directory); store.Save(PlaybackStateStore.Capture("12345", queue, 77));
            var requests = 0;
            using var api = new MusicApi(new FakeHandler((_, _) => { requests++; throw new InvalidOperationException("Restoration requested network"); }));
            using var offline = new OfflineLibrary(Path.Combine(directory, "music"));
            var window = new MainWindow(api: api, store: new SessionStore(directory), restore: true, diagnosticsDirectory: directory, offline: offline);
            try
            {
                Assert(PersistenceField<PreferencesStore>(window, "_preferenceStore").FilePath == Path.Combine(directory, "settings.json")
                    && !PersistenceField<UpdateSupport>(window, "_appUpdatesOverride").State.FeedConfigured,
                    "isolated startup used a different profile or enabled a live release check");
                PersistenceField(window, "_account", new Account("12345", "Fixture", true));
                PersistenceInvoke(window, "InitializePlaybackPersistence"); PersistenceInvoke(window, "RestorePlaybackState", "12345");
                var current = PersistenceField<TrackRow?>(window, "_current");
                var player = PersistenceField<NativePlayer>(window, "_player");
                Assert(current?.Id == "101" && PersistenceField<PlaybackQueue>(window, "_playbackQueue").CurrentIndex == 1
                    && !player.HasSource && !player.IsPlaying && requests == 0, "restoration opened audio or changed current occurrence");
                Assert(Control<TextBlock>(window, "PositionText").Text == "1:17", "restored paused position is not visible");
                _ = new System.Windows.Interop.WindowInteropHelper(window).EnsureHandle();
                var shell = PersistenceField<DesktopShell>(window, "_shell");
                shell.ShowMiniPlayer();
                var mini = MiniShellWindow(shell) ?? throw new InvalidOperationException("restored shell did not open the mini");
                Assert(mini.TitleText.Text == current!.Title && mini.PositionText.Text == "1:17" && mini.DurationText.Text == "3:00",
                    $"mini displayed '{mini.TitleText.Text}' at {mini.PositionText.Text} of {mini.DurationText.Text} instead of the restored track at 1:17 of 3:00");
                Assert(!mini.SeekSlider.IsEnabled && !player.HasSource && !player.IsPlaying && requests == 0,
                    "restored mini enabled seeking or opened audio before Play");
                window.SignOut();
                Assert(store.Load("12345") is null && PersistenceField<TrackRow?>(window, "_current") is null, "logout retained queue state");
            }
            finally { window.Close(); }

            store.Save(PlaybackStateStore.Capture("12345", queue, 77));
            var before = File.ReadAllBytes(Path.Combine(directory, PlaybackStateStore.FileName));
            using var disabledApi = new MusicApi(new FakeHandler((_, _) => { requests++; throw new InvalidOperationException("Disabled restoration requested network"); }));
            using var disabledOffline = new OfflineLibrary(Path.Combine(directory, "music"));
            var disabled = new MainWindow(api: disabledApi, store: new SessionStore(directory), restore: false, diagnosticsDirectory: directory, offline: disabledOffline);
            try
            {
                PersistenceField(disabled, "_account", new Account("12345", "Fixture", true));
                PersistenceInvoke(disabled, "InitializePlaybackPersistence"); PersistenceInvoke(disabled, "RestorePlaybackState", "12345");
                Assert(PersistenceField<TrackRow?>(disabled, "_current") is null, "restore:false fixture loaded snapshot");
                PersistenceInvoke(disabled, "SavePlaybackState");
            }
            finally { disabled.Close(); }
            Assert(before.SequenceEqual(File.ReadAllBytes(Path.Combine(directory, PlaybackStateStore.FileName))) && requests == 0, "disabled fixture wrote state or requested network");
            return Task.CompletedTask;
        });
    }

    private static string PersistenceDirectory(string name)
    {
        var directory = Path.Combine(_directory, "playback-persistence", name);
        Directory.CreateDirectory(directory); return directory;
    }
    private static TrackRow PersistenceTrack(string id, string? requestId = null, bool available = true) => new()
    {
        Id = id, RequestId = requestId ?? id, Title = "Fixture title", Artist = "Fixture artist", Album = "Fixture album",
        AlbumId = "201", Artists = Array.AsReadOnly(new[] { new ArtistReference("301", "Fixture artist") }), DurationMs = 180000,
        Available = available, CoverUri = "https://cdn.invalid/signed?token=must-not-be-persisted"
    };
    private static T PersistenceField<T>(MainWindow window, string name)
        => (T)typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
    private static void PersistenceField(MainWindow window, string name, object? value)
        => typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, value);
    private static void PersistenceInvoke(MainWindow window, string name, params object?[] args)
        => typeof(MainWindow).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, args);
}
