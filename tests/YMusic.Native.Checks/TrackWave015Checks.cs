using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using YMusic.Native;

internal static partial class Program
{
    private static async Task CheckTrackWave015()
    {
        await Run("TrackWave015: exact canonical seed starts server recommendations and feedback uses returned session", async () =>
        {
            var handler = new FakeHandler((request, _) => Task.FromResult(Json(request.RequestUri!.AbsolutePath.EndsWith("/feedback/", StringComparison.Ordinal)
                ? "{\"result\":{}}" : TwBatch("track-session", "batch-one", 801))));
            using var api = new MusicApi(handler);
            var batch = await api.StartWaveAsync(FakeSession, default, WaveMode.Track, "1234");
            var start = handler.Seen.Single(); using var body = JsonDocument.Parse(start.Body);
            Assert(start.Uri.AbsolutePath == "/rotor/session/new" && start.Authorization == "OAuth " + FakeSession.AccessToken
                && body.RootElement.GetProperty("seeds")[0].GetString() == "track:1234"
                && body.RootElement.GetProperty("queue").GetArrayLength() == 0 && body.RootElement.GetProperty("includeTracksInResponse").GetBoolean(), "track seed POST contract differs");
            Assert(batch.Mode == WaveMode.Track && batch.TrackSeedId == "1234" && batch.Tracks[0].Id == "801"
                && batch.Tracks[0].RequestId == "801:91", "radio response was replaced by a local seed/shuffle or lost its provenance");
            await api.SendWaveFeedbackAsync(FakeSession, batch.SessionId, batch.BatchId, "trackStarted", batch.Tracks[0].Id, 0, default);
            Assert(handler.Seen[^1].Uri.AbsolutePath == "/rotor/session/track-session/feedback/", "feedback used the station seed instead of the returned session");
            using var feedback = JsonDocument.Parse(handler.Seen[^1].Body);
            Assert(feedback.RootElement.GetProperty("batchId").GetString() == "batch-one" && feedback.RootElement.GetProperty("event").GetProperty("trackId").GetString() == "801", "feedback lost actual batch/track ID");
        });
        await Run("TrackWave015: continuation and all renewal paths retain exact track seed and received queue", async () =>
        {
            foreach (var renewal in new[] { "{\"unknownSession\":true}", "{\"terminated\":true}", "{\"sequence\":[]}" })
            {
                var starts = 0; var more = 0;
                var handler = new FakeHandler((request, _) => Task.FromResult(Json(request.RequestUri!.AbsolutePath == "/rotor/session/new"
                    ? TwBatch("s" + ++starts, "b" + starts, starts * 100)
                    : ++more == 1 ? TwBatch("s1", "continued", 401) : "{\"result\":" + renewal + "}")));
                using var api = new MusicApi(handler);
                var first = await api.StartWaveAsync(FakeSession, default, WaveMode.Track, "1234");
                var continued = await api.MoreWaveAsync(FakeSession, first.SessionId, default);
                var renewed = await api.MoreWaveAsync(FakeSession, continued.SessionId, default);
                Assert(continued.TrackSeedId == "1234" && renewed.TrackSeedId == "1234" && renewed.Mode == WaveMode.Track && renewed.SessionId == "s2", "continuation/renewal lost track identity");
                var newCalls = handler.Seen.Where(call => call.Uri.AbsolutePath == "/rotor/session/new").ToArray();
                using var renewBody = JsonDocument.Parse(newCalls[1].Body);
                var queue = renewBody.RootElement.GetProperty("queue").EnumerateArray().Select(item => item.GetString()).ToArray();
                Assert(newCalls.Length == 2 && renewBody.RootElement.GetProperty("seeds")[0].GetString() == "track:1234"
                    && queue.Contains("100:91") && queue.Contains("401:91") && queue.Length <= 20, "renewal used a different station or forgot received tracks");
            }
        });
        await Run("TrackWave015: invalid/composite seed sends no HTTP and existing personal/collection seeds stay exact", async () =>
        {
            var handler = new FakeHandler((_, _) => Task.FromResult(Json(TwBatch("s", "b", 800))));
            using var api = new MusicApi(handler);
            foreach (var invalid in new string?[] { null, "", "1234:91", " 1234", "1234/91", "track:1234" })
                await Throws<ArgumentException>(() => api.StartWaveAsync(FakeSession, default, WaveMode.Track, invalid));
            Assert(handler.Seen.Count == 0, "invalid seed escaped into an API request");
            await api.StartWaveAsync(FakeSession, default, WaveMode.Personal); await api.StartWaveAsync(FakeSession, default, WaveMode.Collection);
            Assert(handler.Seen.Select(call => JsonDocument.Parse(call.Body).RootElement.GetProperty("seeds")[0].GetString())
                .SequenceEqual(new[] { "user:onyourwave", "personal:collection" }), "existing wave stations regressed");
        });
        await Run("TrackWave015: late HTTP after reset/cancel is rejected and cannot revive session or cross owner", async () =>
        {
            foreach (var cancel in new[] { false, true })
            {
                var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                using var api = new MusicApi(new FakeHandler(async (_, _) => { entered.TrySetResult(); await release.Task; return Json(TwBatch("stale", "b", 800)); }));
                using var cts = new CancellationTokenSource();
                var start = api.StartWaveAsync(FakeSession, cts.Token, WaveMode.Track, "1234"); await entered.Task;
                if (cancel) cts.Cancel(); else api.ResetWaveState(); release.TrySetResult();
                await Throws<OperationCanceledException>(async () => await start);
                await Throws<MusicException>(() => api.MoreWaveAsync(FakeSession, "stale", default));
            }
            using var owned = new MusicApi(new FakeHandler((_, _) => Task.FromResult(Json(TwBatch("owned", "b", 800)))));
            await owned.StartWaveAsync(FakeSession, default, WaveMode.Track, "1234");
            await Throws<MusicException>(() => owned.MoreWaveAsync(FakeSession with { AccessToken = "other-account" }, "owned", default));
        });
        await Run("TrackWave015: sorted list context targets unavailable canonical row and plays returned recommendations", async () =>
        {
            using var f = new TwFixture("list"); var w = f.Window;
            var wrong = TwSeed("9999", "Такое же название", true, 60000); var seed = TwSeed("1234", "Такое же название", false);
            SaInvoke(w, "ShowRows", (object)new[] { wrong, seed });
            var list = Control<ListView>(w, "TracksList"); list.SelectedItem = wrong;
            list.SelectedItems.Add(seed);
            SortChoose(w, ListSortField.Duration);
            Assert(ReferenceEquals(list.Items[0], seed), "fixture did not reorder source rows before contextual targeting");
            list.ScrollIntoView(seed); w.UpdateLayout();
            var row = (ListViewItem)list.ItemContainerGenerator.ContainerFromItem(seed)!;
            row.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Right) { RoutedEvent = Mouse.PreviewMouseDownEvent });
            Assert(list.SelectedItems.Count == 2 && list.SelectedItems.Contains(wrong) && list.SelectedItems.Contains(seed), "wave context changed the existing multiple selection");
            TwClickWave(list); await f.Ready("1234");
            Assert(f.Seeds.Single() == "track:1234" && SaField<TrackRow>(w, "_current").Id == "801", "context used name, composite ID, availability or local shuffle");
            Assert(Control<TextBlock>(w, "WaveHeading").Text == "Волна по треку" && Control<TextBlock>(w, "WaveModeCaption").Text.Contains(seed.Title)
                && !ReferenceEquals(Control<Button>(w, "WaveNav").Foreground, w.FindResource("AccentBrush")), "track station mislabeled as personal wave");
            await Until(() => f.Feedback.Count >= 2);
            Assert(f.Feedback[0].Type == "radioStarted" && f.Feedback[1].Type == "trackStarted" && f.Feedback[1].Track == "801", "native track-wave playback did not use existing ordered feedback");
            foreach (var width in new[] { 1050d, 1280d }) { w.Width = width; w.Height = width == 1050 ? 680 : 860; w.UpdateLayout(); App.Capture(w, Path.Combine(_directory, $"track-wave-015-{width}.png")); }
        });
        await Run("TrackWave015: catalog track, queue, downloads and player contexts keep exact target; album and stale/logout actions disabled", async () =>
        {
            using var f = new TwFixture("contexts"); var w = f.Window; var seed = TwSeed("5678", "Точный источник", false);
            var catalog = Control<ListBox>(w, "CatalogList");
            SaInvoke(w, "ShowCatalogItems", (object)new[] { new CatalogItem("5678", "track", seed.Title, seed.Artist, Track: seed) }, false);
            catalog.SelectedIndex = 0; TwClickWave(catalog); await f.Ready("5678");
            SaInvoke(w, "ShowCatalogItems", (object)new[] { new CatalogItem("91", "album", "Альбом", "") }, false); catalog.SelectedIndex = 0;
            Assert(!TwMenu(catalog).IsEnabled, "album catalog item was interpreted as a track");
            var queue = SaField<PlaybackQueue>(w, "_playbackQueue"); queue.SetItems([seed]); SaInvoke(w, "ShowQueue");
            var list = Control<ListView>(w, "TracksList"); list.SelectedItem = seed; TwClickWave(list); await f.Ready("5678", 2);
            var downloadQueue = SaField<DownloadQueueState>(w, "_downloadQueue"); downloadQueue.Replace([seed], "Тестовая очередь");
            var downloads = SaField<ListView>(w, "_downloadEntriesList"); downloads.SelectedIndex = 0; TwClickWave(downloads); await f.Ready("5678", 3);
            UlSet(w, "_current", seed); SaInvoke(w, "UpdateControls"); TwClickWave(Control<Button>(w, "PlayerTitleButton")); await f.Ready("5678", 4);
            var stale = TwMenu(Control<Button>(w, "PlayerTitleButton")); var calls = f.Seeds.Count;
            w.SignOut(); stale.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); await Task.Delay(80);
            Assert(f.Seeds.Count == calls && !TwMenu(Control<Button>(w, "PlayerTitleButton")).IsEnabled, "menu retained authority after logout");
        });
        await Run("TrackWave015: track seed survives Back/Forward and personal/collection switch keeps each station identity", async () =>
        {
            using var f = new TwFixture("history"); var w = f.Window; var seed = TwSeed("1234", "Исходный трек", true);
            await Call(w, "OpenTrackWaveAsync", seed); await f.Ready("1234");
            await Call(w, "OpenWaveAsync", WaveMode.Collection); await Until(() => SaField<RadioBatch?>(w, "_waveBatch")?.Mode == WaveMode.Collection && !SaField<bool>(w, "_waveStarting"));
            SaInvoke(w, "BrowseBack_Click", null!, null!);
            Assert(SaField<WaveMode>(w, "_waveViewMode") == WaveMode.Track && SaField<TrackRow>(w, "_waveSeedTrack").Id == "1234"
                && Control<TextBlock>(w, "WaveHeading").Text == "Волна по треку", "Back lost exact seed or displayed a different station");
            SaInvoke(w, "BrowseForward"); Assert(SaField<WaveMode>(w, "_waveViewMode") == WaveMode.Collection, "Forward lost collection mode");
            SaInvoke(w, "BrowseBack_Click", null!, null!); await Call(w, "StartWaveViewAsync", true); await f.Ready("1234", 3);
            await Call(w, "OpenWaveAsync", WaveMode.Personal);
            Assert(f.Seeds.SequenceEqual(new[] { "track:1234", "personal:collection", "track:1234", "user:onyourwave" }), "navigation or old modes reused a different track seed");
        });
        await Run("TrackWave015: late request cannot replace manual queue, new station, account or unsaved Settings draft", async () =>
        {
            foreach (var action in new[] { "queue", "switch", "logout", "settings" })
            {
                using var f = new TwFixture("race-" + action) { HoldNext = true }; var w = f.Window;
                var pending = Call(w, "OpenTrackWaveAsync", TwSeed("1234", "Задержанный трек", false)); await f.Entered.Task;
                var queue = SaField<PlaybackQueue>(w, "_playbackQueue"); var manual = TwSeed("9999", "Моя очередь", true); Task? replacement = null;
                if (action == "queue") queue.SetItems([manual]);
                if (action == "switch") replacement = Call(w, "OpenTrackWaveAsync", TwSeed("5678", "Другая волна", false));
                if (action == "logout") w.SignOut();
                if (action == "settings") { SaInvoke(w, "Settings_Click", null!, null!); Control<Slider>(w, "CrossfadeSetting").Value = 9.5; }
                f.Release.TrySetResult(); await pending; if (replacement is not null) await replacement;
                if (action == "queue") Assert(ReferenceEquals(queue.Current, manual) && !f.Player.HasSource, "late response replaced manual queue");
                if (action == "switch") Assert(SaField<RadioBatch>(w, "_waveBatch").TrackSeedId == "5678" && f.Seeds.Last() == "track:5678", "canceled response replaced a newer station");
                if (action == "logout") Assert(SaField<RadioBatch?>(w, "_waveBatch") is null && !f.Player.HasSource && SaField<Session?>(w, "_session") is null, "late response revived signed-out account");
                if (action == "settings") Assert(SaField<bool>(w, "_settingsOpen") && Control<Slider>(w, "CrossfadeSetting").Value == 9.5 && f.Player.CrossfadeSeconds != 9.5, "late response closed/applied the Settings draft");
            }
        });
        await Run("TrackWave015: explicit HTTP failure leaves original queue and permits retry", async () =>
        {
            using var f = new TwFixture("error") { FailNext = true }; var w = f.Window;
            var original = TwSeed("9999", "Моя очередь", true); var queue = SaField<PlaybackQueue>(w, "_playbackQueue"); queue.SetItems([original]);
            await Call(w, "OpenTrackWaveAsync", TwSeed("1234", "Источник", false));
            Assert(ReferenceEquals(queue.Current, original) && !SaField<bool>(w, "_waveStarting") && !f.Player.HasSource, "failed session replaced queue or left action busy");
            await Call(w, "OpenTrackWaveAsync", TwSeed("1234", "Источник", false)); await f.Ready("1234", 2);
        });
        await Run("TrackWave015: late continuation cannot append to manual queue, replacement station or signed-out account", async () =>
        {
            foreach (var action in new[] { "queue", "switch", "logout" })
            {
                using var f = new TwFixture("continuation-" + action); var w = f.Window;
                await Call(w, "OpenTrackWaveAsync", TwSeed("1234", "Первая волна", false)); await f.Ready("1234");
                var before = SaField<List<TrackRow>>(w, "_waveTracks").Count;
                f.HoldMore = true; var pending = Call(w, "ExtendWaveQueueAsync"); await f.Entered.Task;
                var queue = SaField<PlaybackQueue>(w, "_playbackQueue"); var manual = TwSeed("9999", "Новая очередь", true); Task? replacement = null;
                if (action == "queue") queue.SetItems([manual]);
                if (action == "switch") replacement = Call(w, "OpenTrackWaveAsync", TwSeed("5678", "Вторая волна", false));
                if (action == "logout") w.SignOut();
                f.Release.TrySetResult(); await pending; if (replacement is not null) await replacement;
                if (action == "queue") Assert(ReferenceEquals(queue.Current, manual) && queue.Items.Count == 1 && SaField<List<TrackRow>>(w, "_waveTracks").Count == before, "late continuation contaminated manual queue");
                if (action == "switch") Assert(SaField<RadioBatch>(w, "_waveBatch").TrackSeedId == "5678" && queue.Items.All(track => !track.Id.StartsWith("90", StringComparison.Ordinal)), "previous continuation contaminated new station");
                if (action == "logout") Assert(SaField<RadioBatch?>(w, "_waveBatch") is null && SaField<List<TrackRow>>(w, "_waveTracks").Count == 0, "late continuation revived signed-out wave");
            }
        });
    }

    private static TrackRow TwSeed(string id, string title, bool available, long duration = 30000) => new() { Id = id, RequestId = id + ":777", Title = title, Artist = "Тестовый исполнитель", Available = available, DurationMs = duration };
    private static MenuItem TwMenu(FrameworkElement anchor)
    {
        var menu = anchor.ContextMenu ?? throw new InvalidOperationException("track-wave context missing on " + anchor.Name);
        menu.RaiseEvent(new RoutedEventArgs(ContextMenu.OpenedEvent));
        return menu.Items.OfType<MenuItem>().Single(item => item.Header as string == "Волна по этому треку");
    }
    private static void TwClickWave(FrameworkElement anchor)
    { var action = TwMenu(anchor); Assert(action.IsEnabled, "track wave action unexpectedly disabled on " + anchor.Name); action.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); }
    private static string TwBatch(string session, string batch, int first) => JsonSerializer.Serialize(new { result = new { radioSessionId = session, batchId = batch,
        sequence = Enumerable.Range(first, 4).Select(id => new { type = "track", track = new { id = id.ToString(), title = "Рекомендация Яндекса " + id, available = true, durationMs = 30000,
            artists = new[] { new { name = "Тестовый исполнитель" } }, albums = new[] { new { id = "91", title = "Рекомендованный альбом" } } } }).ToArray() } });

    private sealed class TwFixture : IDisposable
    {
        internal MainWindow Window { get; }
        internal NativePlayer Player { get; }
        internal List<string> Seeds { get; } = [];
        internal List<(string Type, string? Track)> Feedback { get; } = [];
        internal bool HoldNext, HoldMore, FailNext;
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _sessions;
        internal TwFixture(string name)
        {
            var directory = Path.Combine(_directory, "track-wave-015-" + name); Directory.CreateDirectory(directory);
            var media = File.ReadAllBytes(WriteWave("track-wave-015-" + name + ".wav", 30));
            Player = new NativePlayer(new AudioBuffer(Path.Combine(directory, "buffer"), new FakeHandler((request, _) =>
            {
                Assert(request.RequestUri!.Host == "s1.vlx.yandex.net" && request.Headers.Authorization is null, "fixture audio escaped anonymous CDN boundary");
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(media) });
            })));
            var api = new MusicApi(new FakeHandler(async (request, token) =>
            {
                Assert(request.RequestUri!.Host == "api.music.yandex.net", "fixture used an unexpected API host"); var path = request.RequestUri.AbsolutePath;
                if (path == "/rotor/session/new")
                {
                    using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token)); Seeds.Add(body.RootElement.GetProperty("seeds")[0].GetString()!);
                    var ordinal = ++_sessions;
                    if (HoldNext) { HoldNext = false; Entered.TrySetResult(); await Release.Task; }
                    if (FailNext) { FailNext = false; return Json("{}", HttpStatusCode.ServiceUnavailable); }
                    return Json(TwBatch("tw-" + ordinal, "first-" + ordinal, 800 + ordinal));
                }
                if (path.EndsWith("/tracks", StringComparison.Ordinal) && path.StartsWith("/rotor/session/", StringComparison.Ordinal))
                { if (HoldMore) { HoldMore = false; Entered.TrySetResult(); await Release.Task; } return Json(TwBatch(path.Split('/')[3], "more", 901)); }
                if (path.EndsWith("/feedback/", StringComparison.Ordinal))
                {
                    using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token)); var feedback = body.RootElement.GetProperty("event");
                    Feedback.Add((feedback.GetProperty("type").GetString()!, feedback.TryGetProperty("trackId", out var id) ? id.GetString() : null)); return Json("{\"result\":{}}");
                }
                if (path == "/get-file-info") return Json("{\"result\":{\"downloadInfo\":{\"quality\":\"lossless\",\"codec\":\"mp3\",\"bitrate\":320,\"transport\":\"raw\",\"url\":\"https://s1.vlx.yandex.net/track-wave-fixture.mp3\"}}}");
                if (path.EndsWith("/lyrics", StringComparison.Ordinal) || path.EndsWith("/supplement", StringComparison.Ordinal)) return LyricsUiYandex(request, "Локальная проверка волны");
                throw new InvalidOperationException("Unexpected track-wave fixture route: " + path);
            }));
            Window = new MainWindow(api: api, store: new SessionStore(Path.Combine(directory, "session")), restore: false,
                diagnosticsDirectory: Path.Combine(directory, "diagnostics"), offline: new OfflineLibrary(Path.Combine(directory, "offline")), player: Player);
            Window.Show(); Control<Slider>(Window, "VolumeSlider").Value = 0;
            UlSet(Window, "_session", FakeSession); UlSet(Window, "_account", new Account("12345", "Fixture", true)); SaInvoke(Window, "UpdateControls");
        }
        internal async Task Ready(string seed, int starts = 1) => await Until(() => Seeds.Count >= starts && SaField<RadioBatch?>(Window, "_waveBatch")?.TrackSeedId == seed
            && !SaField<bool>(Window, "_waveStarting") && !SaField<bool>(Window, "_loadingStream") && Player.HasSource);
        public void Dispose() { Release.TrySetResult(); Window.Close(); }
    }
}
