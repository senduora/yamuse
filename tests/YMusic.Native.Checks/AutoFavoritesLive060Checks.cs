using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using YMusic.Native;

internal static partial class Program
{
    private static async Task CheckAutoFavoritesLive060()
    {
        await Run("AutoFavoritesLive060: restored completed and empty history permits a new player like without resuming old work", async () =>
        {
            foreach (var completed in new[] { true, false })
            {
                using var f = new Af060Fixture("restored-finished-" + completed);
                var previous = Af060Row("900");
                if (completed) await f.StartManual(previous);
                var rows = completed ? new[] { previous } : Array.Empty<TrackRow>();
                f.Queue.Replace(rows, "История автоскачивания", favoritesBatch: true);
                if (completed) f.Queue.Entries.Single().Complete(f.Library.Read().Single().Bytes);
                var directory = Path.Combine(_directory, "auto-favorites-restore-finished-" + completed);
                var store = new DownloadQueueStore(directory);
                store.Save(DownloadQueueStore.Capture("12345", f.Queue, AudioQuality.Excellent, true,
                    new PlaylistDetails("local:favorites", "liked", "Мне нравится", "", 0, rows)));
                UlSet(f.Window, "_downloadQueueStore", store);
                var before = f.Downloads.Values.Sum();
                await Call(f.Window, "RestoreDownloadQueueAsync", "12345");
                Assert(!f.Queue.CanResume && !f.Queue.CanRetry && !SaField<bool>(f.Window, "_downloadsAutoSuspended")
                    && f.Downloads.Values.Sum() == before && !f.Queue.Notice.Contains("на паузе", StringComparison.OrdinalIgnoreCase),
                    "finished restored history blocks new downloads or pretends it is paused");
                var added = Af060Row("101"); UlSet(f.Window, "_current", added); SaInvoke(f.Window, "UpdatePlayerLikeState");
                await Call(f.Window, "ToggleCurrentLikeAsync");
                await Until(() => f.Library.Read().Any(item => item.Id == added.Id));
                Assert(f.Downloads.GetValueOrDefault("101") == 1 && f.Downloads.GetValueOrDefault("900") == (completed ? 1 : 0)
                    && !f.RefreshReleased.Task.IsCompleted, "restored history postponed a player like or restarted finished audio");
            }
        });
        await Run("AutoFavoritesLive060: restored paused and failed queues still require explicit continuation", async () =>
        {
            foreach (var state in new[] { DownloadEntryState.Paused, DownloadEntryState.Failed })
            {
                using var f = new Af060Fixture("restored-held-" + state); var previous = Af060Row("900");
                f.Queue.Replace([previous], "Восстановленная очередь", favoritesBatch: true);
                f.Queue.Entries.Single().SetState(state);
                var directory = Path.Combine(_directory, "auto-favorites-restore-held-" + state); var store = new DownloadQueueStore(directory);
                store.Save(DownloadQueueStore.Capture("12345", f.Queue, AudioQuality.Excellent, true,
                    new PlaylistDetails("local:favorites", "liked", "Мне нравится", "", 0, [previous])));
                UlSet(f.Window, "_downloadQueueStore", store);
                await Call(f.Window, "RestoreDownloadQueueAsync", "12345");
                Assert(SaField<bool>(f.Window, "_downloadsAutoSuspended") && (f.Queue.CanResume || f.Queue.CanRetry),
                    "unfinished restored work lost its explicit continuation requirement");
                await f.Like(Af060Row("101")); await Task.Delay(100);
                Assert(f.Downloads.Count == 0 && f.Queue.Entries.Single().Track.Id == "900", "new like replaced or resumed unfinished restored work");
            }
        });
        await Run("AutoFavoritesLive060: confirmed player like downloads before the held collection refresh completes", async () =>
        {
            using var f = new Af060Fixture("immediate"); var row = Af060Row("101");
            UlSet(f.Window, "_current", row); SaInvoke(f.Window, "UpdatePlayerLikeState");
            await Call(f.Window, "ToggleCurrentLikeAsync"); await f.RefreshEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Until(() => f.Library.Read().Any(item => item.Id == row.Id));
            Assert(!f.RefreshReleased.Task.IsCompleted && f.Downloads.GetValueOrDefault(row.Id) == 1 && row.IsLiked,
                "confirmed like waited for full favorites refresh or downloaded twice");
            Assert(f.Queue.FavoritesBatch && SaField<PlaylistDetails?>(f.Window, "_downloadQueuePlaylist") is null,
                "incremental like lost unlike guards or overwrote the complete favorites snapshot");
        });
        await Run("AutoFavoritesLive060: busy manual queue retains every new like across refresh replacement and unlike removes pending work", async () =>
        {
            using var f = new Af060Fixture("pending", "900");
            var manual = f.StartManual(Af060Row("900")); await f.MediaEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var first = Af060Row("101"); var removed = Af060Row("102");
            await f.Like(first); await f.Like(removed); await f.Like(first); await f.Like(removed, false);
            Assert(f.Queue.Entries.Single().Track.Id == "900" && f.Downloads.GetValueOrDefault("101") == 0,
                "confirmed favorite replaced or ran alongside the active manual batch");
            f.MediaReleased.TrySetResult(); await manual;
            await Until(() => f.Library.Read().Count == 2 && SaField<CancellationTokenSource?>(f.Window, "_downloadCts") is null);
            Assert(f.Downloads.GetValueOrDefault("900") == 1 && f.Downloads.GetValueOrDefault("101") == 1
                && f.Downloads.GetValueOrDefault("102") == 0 && f.Library.Read().All(item => item.Id != "102"),
                "pending likes were lost, duplicated or kept after unlike");
        });
        await Run("AutoFavoritesLive060: disabled preference and existing offline files suppress new network transfers", async () =>
        {
            using var f = new Af060Fixture("disabled-existing"); var existing = Af060Row("101");
            await f.StartManual(existing); await f.Like(existing);
            await Until(() => SaField<CancellationTokenSource?>(f.Window, "_downloadCts") is null);
            Assert(f.Downloads.GetValueOrDefault("101") == 1 && f.Resolves.GetValueOrDefault("101") == 1,
                "new like redownloaded or probed the existing offline file");
            Control<CheckBox>(f.Window, "AutoFavoritesCheckBox").IsChecked = false;
            await f.Like(Af060Row("102")); await Task.Delay(100);
            Assert(f.Downloads.GetValueOrDefault("102") == 0, "disabled preference still downloaded a new like");
            Control<CheckBox>(f.Window, "AutoFavoritesCheckBox").IsChecked = true;
            await f.Like(Af060Row("103")); await Until(() => f.Library.Read().Any(item => item.Id == "103"));
            Assert(f.Downloads.GetValueOrDefault("103") == 1, "new like did not download after re-enabling the preference");
        });
        await Run("AutoFavoritesLive060: pause disable and signout discard pending confirmed likes without automatic resume", async () =>
        {
            foreach (var action in new[] { "pause", "disable", "logout" })
            {
                using var f = new Af060Fixture("lifecycle-" + action, "900");
                var manual = f.StartManual(Af060Row("900")); await f.MediaEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await f.Like(Af060Row("101"));
                if (action == "pause") SaInvoke(f.Window, "PauseDownloadQueue");
                if (action == "disable") Control<CheckBox>(f.Window, "AutoFavoritesCheckBox").IsChecked = false;
                if (action == "logout") f.Window.SignOut();
                f.MediaReleased.TrySetResult(); await manual; await Task.Delay(100);
                Assert(f.Downloads.GetValueOrDefault("101") == 0 && SaField<Dictionary<string, TrackRow>>(f.Window, "_pendingConfirmedFavorites").Count == 0,
                    "pending favorite survived " + action);
                if (action == "pause")
                {
                    await f.Like(Af060Row("102")); await Task.Delay(100);
                    Assert(f.Downloads.GetValueOrDefault("102") == 0, "new like resumed a paused download queue");
                }
            }
        });
        await Run("AutoFavoritesLive060: failed and unconfirmed likes do not schedule audio", async () =>
        {
            foreach (var confirmedFailure in new[] { false, true })
            {
                using var f = new Af060Fixture("failed-like-" + confirmedFailure) { FailWrite = !confirmedFailure, FailReadback = confirmedFailure };
                await f.Like(Af060Row("101")); await Task.Delay(100);
                Assert(f.Downloads.Count == 0 && f.Resolves.Count == 0 && !f.RefreshEntered.Task.IsCompleted,
                    "failed or unconfirmed mutation scheduled favorites refresh/audio");
            }
        });
        await Run("AutoFavoritesLive060: completed collection refresh reuses the immediately downloaded track", async () =>
        {
            using var f = new Af060Fixture("refresh-dedup");
            await f.Like(Af060Row("101")); await Until(() => f.Library.Read().Count == 1);
            f.RefreshReleased.TrySetResult();
            await Until(() => SaField<CancellationTokenSource?>(f.Window, "_libraryCts") is null
                && SaField<CancellationTokenSource?>(f.Window, "_downloadCts") is null);
            Assert(f.Downloads.GetValueOrDefault("101") == 1 && f.Resolves.GetValueOrDefault("101") == 1
                && SaField<List<TrackRow>>(f.Window, "_favoritesCache").Single().Id == "101", "full refresh downloaded the new favorite again");
        });
        await Run("AutoFavoritesLive060: unavailable favorites stay explicit and a failed transfer does not retry automatically", async () =>
        {
            using (var f = new Af060Fixture("unavailable"))
            {
                await f.Like(Af060Row("101", available: false));
                await Until(() => f.Queue.Entries.Count == 1 && SaField<CancellationTokenSource?>(f.Window, "_downloadCts") is null);
                Assert(f.Queue.Entries.Single().State == DownloadEntryState.Unavailable && f.Downloads.Count == 0, "unavailable favorite attempted audio");
            }
            using (var f = new Af060Fixture("failed-transfer") { FailAudio = true })
            {
                await f.Like(Af060Row("101"));
                await Until(() => f.Queue.Entries.Count == 1 && SaField<CancellationTokenSource?>(f.Window, "_downloadCts") is null);
                SaInvoke(f.Window, "TryStartPendingAutoFavorites"); await Task.Delay(150);
                Assert(f.Queue.Entries.Single().State == DownloadEntryState.Failed && f.Downloads.GetValueOrDefault("101") == 1,
                    "failed immediate download was hidden or retried automatically");
            }
        });
        await Run("AutoFavoritesLive060: incremental favorites checkpoints restore paused with unlike guards and no partial playlist", () =>
        {
            var directory = Path.Combine(_directory, "auto-favorites-live-060-checkpoint");
            using var offline = new OfflineLibrary(Path.Combine(directory, "music"));
            var store = new DownloadQueueStore(Path.Combine(directory, "state")); var queue = new DownloadQueueState();
            queue.Replace([Af060Row("101"), Af060Row("102")], "Автоскачивание", favoritesBatch: true);
            queue.BeginRun(false); queue.SetCollectionLike("102", false);
            var snapshot = DownloadQueueStore.Capture("12345", queue, AudioQuality.Excellent, true, null);
            store.Save(snapshot); var restored = DownloadQueueStore.PrepareRestore(store.Load("12345")!, offline);
            var replay = new DownloadQueueState(); replay.Restore(restored);
            Assert(restored.FavoritesBatch && restored.Automatic && restored.Playlist is null
                && replay.Entries[0].State == DownloadEntryState.Paused && replay.Entries[1].SkipFavoritesDownload,
                "incremental favorites checkpoint lost pause, unlike semantics or claimed a complete playlist");
            replay.SetCollectionLike("101", false);
            Assert(replay.Entries[0].SkipFavoritesDownload && !replay.CanResume, "restored incremental batch could resume an unliked favorite");
            try { store.Save(snapshot with { Automatic = false }); throw new InvalidOperationException("manual favorites checkpoint without playlist was accepted"); }
            catch (JsonException) { }
            return Task.CompletedTask;
        });
    }

    private static TrackRow Af060Row(string id, bool available = true) => new() { Id = id, RequestId = id + ":91", Title = "Тестовый трек " + id,
        Artist = "Тестовый исполнитель", Album = "Тестовый альбом", AlbumId = "91", Available = available, DurationMs = 180000 };

    private sealed class Af060Fixture : IDisposable
    {
        internal MainWindow Window { get; }
        internal OfflineLibrary Library { get; }
        internal DownloadQueueState Queue => SaField<DownloadQueueState>(Window, "_downloadQueue");
        internal ConcurrentDictionary<string, int> Downloads { get; } = new(StringComparer.Ordinal);
        internal ConcurrentDictionary<string, int> Resolves { get; } = new(StringComparer.Ordinal);
        internal TaskCompletionSource RefreshEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource RefreshReleased { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource MediaEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource MediaReleased { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool FailWrite, FailReadback, FailAudio;
        private readonly HashSet<string> _liked = new(StringComparer.Ordinal);
        private int _readback;

        internal Af060Fixture(string name, string? heldMedia = null)
        {
            var directory = Path.Combine(_directory, "auto-favorites-live-060-" + name); Directory.CreateDirectory(directory);
            var api = new MusicApi(new FakeHandler(async (request, token) =>
            {
                Assert(request.RequestUri!.Host == "api.music.yandex.net", "fixture API escaped its host"); var path = request.RequestUri.AbsolutePath;
                if (path == "/account/status") return Json(Profile);
                if (path is "/users/12345/likes/tracks/add-multiple" or "/users/12345/likes/tracks/remove")
                {
                    if (FailWrite) return Json("{}", HttpStatusCode.ServiceUnavailable);
                    var fields = Af060Form(await request.Content!.ReadAsStringAsync(token)); var id = fields["track-ids"].Split(':')[0];
                    if (path.EndsWith("add-multiple", StringComparison.Ordinal)) _liked.Add(id); else _liked.Remove(id);
                    Interlocked.Exchange(ref _readback, 1); return Json("{\"result\":{\"revision\":2}}");
                }
                if (path == "/users/12345/likes/tracks")
                {
                    if (Interlocked.Exchange(ref _readback, 0) == 1)
                    { if (FailReadback) return Json("{\"result\":{\"library\":{\"tracks\":[]}}}"); }
                    else { RefreshEntered.TrySetResult(); await RefreshReleased.Task.WaitAsync(token); }
                    return Json(JsonSerializer.Serialize(new { result = new { library = new { tracks = _liked.Select(id => new { id, albumId = "91" }).ToArray() } } }));
                }
                if (path == "/tracks")
                {
                    var fields = Af060Form(await request.Content!.ReadAsStringAsync(token));
                    return Json(JsonSerializer.Serialize(new { result = fields["track-ids"].Split(',').Select(key => new { id = key.Split(':')[0],
                        title = "Тестовый трек", available = true, durationMs = 180000, albums = new[] { new { id = "91", title = "Альбом" } } }).ToArray() }));
                }
                if (path == "/get-file-info")
                {
                    var id = Af060Form(request.RequestUri.Query.TrimStart('?'))["trackId"].Split(':')[0]; Resolves.AddOrUpdate(id, 1, (_, count) => count + 1);
                    return Json("{\"result\":{\"downloadInfo\":{\"quality\":\"lossless\",\"codec\":\"mp3\",\"bitrate\":320,\"transport\":\"raw\",\"url\":\"https://s1.vlx.yandex.net/" + id + ".mp3\"}}}");
                }
                throw new InvalidOperationException("Unexpected auto-favorites fixture route: " + path);
            }));
            var media = new FakeHandler(async (request, token) =>
            {
                Assert(request.RequestUri!.Host == "s1.vlx.yandex.net" && request.Headers.Authorization is null, "fixture audio escaped the anonymous CDN boundary");
                var id = Path.GetFileNameWithoutExtension(request.RequestUri.AbsolutePath); Downloads.AddOrUpdate(id, 1, (_, count) => count + 1);
                if (id == heldMedia) { MediaEntered.TrySetResult(); await MediaReleased.Task.WaitAsync(token); }
                return new HttpResponseMessage(FailAudio ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK) { Content = new ByteArrayContent(DownloadCenterFixture.Media) };
            });
            Library = new OfflineLibrary(Path.Combine(directory, "music"), new AudioBuffer(Path.Combine(directory, "buffer"), media));
            Window = new MainWindow(api: api, store: new SessionStore(Path.Combine(directory, "session")), restore: false,
                diagnosticsDirectory: Path.Combine(directory, "diagnostics"), offline: Library, preferences: new PreferencesStore(Path.Combine(directory, "preferences")));
            UlSet(Window, "_session", FakeSession); UlSet(Window, "_account", new Account("12345", "Fixture", true));
            UlSet(Window, "_favoriteIdsKnown", true); UlSet(Window, "_favoritesAccountUid", "12345"); UlSet(Window, "_favoritesCacheRead", true);
            Control<CheckBox>(Window, "AutoFavoritesCheckBox").IsChecked = true;
        }
        internal Task Like(TrackRow row, bool liked = true) => Call(Window, "SetTrackLikeAsync", row, liked);
        internal Task StartManual(params TrackRow[] rows) => Call(Window, "StartDownloadBatchAsync", rows, "Ручная загрузка", null!, false);
        public void Dispose() { RefreshReleased.TrySetResult(); MediaReleased.TrySetResult(); Window.Close(); }
    }
    private static Dictionary<string, string> Af060Form(string form) => form.Split('&', StringSplitOptions.RemoveEmptyEntries)
        .Select(part => part.Split('=', 2)).ToDictionary(part => WebUtility.UrlDecode(part[0]), part => WebUtility.UrlDecode(part.ElementAtOrDefault(1) ?? ""), StringComparer.Ordinal);
}
