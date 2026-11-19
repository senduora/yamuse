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

        await Run("TrackWave015: conflicting continuation session IDs do not publish tracks or change the received queue", async () =>
        {
            var replies = new Queue<string>(new[]
            {
                TwBatch("other-session", "foreign-batch", 990, 1),
                TwBatch("active", "malformed-batch", 991, 1).Replace("\"radioSessionId\":\"active\"", "\"radioSessionId\":7", StringComparison.Ordinal),
                TwBatch("active", "valid-batch", 802, 1),
                TwBatch("active", "no-id-batch", 803, 1).Replace("\"radioSessionId\":\"active\",", "", StringComparison.Ordinal)
            });
            var handler = new FakeHandler((request, _) => Task.FromResult(Json(request.RequestUri!.AbsolutePath switch
            {
                "/rotor/session/new" => TwBatch("active", "first-batch", 801, 1),
                "/rotor/session/active/tracks" => replies.Dequeue(),
                "/rotor/session/active/feedback/" => "{\"result\":{}}",
                _ => throw new InvalidOperationException("Unexpected continuation identity fixture endpoint")
            })));
            using var api = new MusicApi(handler);
            var first = await api.StartWaveAsync(FakeSession, default);
            await Throws<MusicException>(() => api.MoreWaveAsync(FakeSession, first.SessionId, default));
            await Throws<MusicException>(() => api.MoreWaveAsync(FakeSession, first.SessionId, default));
            var valid = await api.MoreWaveAsync(FakeSession, first.SessionId, default);
            var omitted = await api.MoreWaveAsync(FakeSession, first.SessionId, default);
            Assert(valid is { SessionId: "active", BatchId: "valid-batch" } && omitted is { SessionId: "active", BatchId: "no-id-batch" }
                && handler.Seen.Count == 5, "an invalid continuation changed the station or generated extra requests");
            foreach (var request in handler.Seen.Skip(1).Take(3))
            {
                using var body = JsonDocument.Parse(request.Body);
                Assert(body.RootElement.GetProperty("queue").EnumerateArray().Select(item => item.GetString()).SequenceEqual(["801:91"]),
                    "the rejected batch contaminated the queue sent for another continuation");
            }
            await api.SendWaveFeedbackAsync(FakeSession, omitted.SessionId, omitted.BatchId, "trackStarted", omitted.Tracks[0].Id, 0, default);
            using var feedback = JsonDocument.Parse(handler.Seen[^1].Body);
            Assert(handler.Seen.Count == 6 && feedback.RootElement.GetProperty("batchId").GetString() == "no-id-batch",
                "feedback did not belong to the accepted continuation");
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
            Ta0317RaiseOpening(row, 10, 10);
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
            UlSet(w, "_downloadQueueAccountUid", SaField<Account>(w, "_account").Uid);
            UlSet(w, "_downloadQueueAccountGeneration", SaField<int>(w, "_accountGeneration"));
            SaInvoke(w, "RefreshDownloadCenter");
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
                if (action == "settings") Assert(SaField<bool>(w, "_settingsOpen") && Control<Slider>(w, "CrossfadeSetting").Value == 9.5 && f.Player.CrossfadeSeconds == 9.5, "late response closed Settings or reset live controls/runtime");
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
        await Run("TrackWave015: pending new station preserves active radio until playback accepts it", async () =>
        {
            foreach (var action in new[] { "next", "natural", "pause", "seek", "minute", "after-track", "unavailable", "refill" })
            {
                using var f = new TwFixture("rejected-station-" + action); var w = f.Window;
                await Call(w, "OpenTrackWaveAsync", TwSeed("1234", "Текущая волна", true)); await f.Ready("1234");
                var queue = SaField<PlaybackQueue>(w, "_playbackQueue");
                var expected = action is "next" or "natural" ? queue.Items[1] : queue.Current;
                f.HoldNext = true;
                if (action == "unavailable") f.UnavailableStart = 2;
                var pending = Call(w, "OpenTrackWaveAsync", TwSeed("5678", "Новая волна", true));
                await f.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                switch (action)
                {
                    case "next": await Call(w, "AdvanceAsync", 1, false); break;
                    case "natural":
                        f.Player.Seek(f.Player.Duration - TimeSpan.FromSeconds(.25));
                        await Until(() => ReferenceEquals(queue.Current, expected) && f.Player.IsPlaying);
                        break;
                    case "pause": await w.HandleDesktopMediaCommandAsync(DesktopMediaAction.Pause); break;
                    case "seek": SaInvoke(w, "CommitSeek", 1d); break;
                    case "minute":
                        w.SetSleepTimer(SleepTimerMode.Minutes15);
                        SaInvoke(w, "ApplySleepTimerElapsed", TimeSpan.FromMinutes(15));
                        break;
                    case "after-track": w.SetSleepTimer(SleepTimerMode.AfterCurrentTrack); break;
                    case "refill":
                        await Call(w, "ExtendWaveQueueAsync").WaitAsync(TimeSpan.FromSeconds(5));
                        Assert(f.MoreSessions.SequenceEqual(new[] { "tw-1" }) && queue.Items.Any(row => row.Id == "901"),
                            "a pending new station blocked refill of the playing radio");
                        break;
                }
                f.Release.TrySetResult(); await pending;
                if (action == "refill")
                {
                    Assert(SaField<RadioBatch>(w, "_waveBatch").SessionId == "tw-2" && queue.Current?.Id == "802" && f.Player.IsPlaying,
                        "passive refill displaced the explicit request");
                    continue;
                }
                Assert(SaField<RadioBatch>(w, "_waveBatch").SessionId == "tw-1" && ReferenceEquals(queue.Current, expected)
                    && f.Player.IsPlaying == (action is not ("pause" or "minute")), "late station replaced active radio or playback after " + action);
                await Call(w, "ExtendWaveQueueAsync");
                Assert(f.MoreSessions.SequenceEqual(new[] { "tw-1" }) && queue.Items.Any(row => row.Id == "901"),
                    "rejected station prevented another refill for the original radio");
                await Until(() => f.FeedbackSources.Any(x => x == ("tw-1", "first-1", "trackStarted", expected!.Id)));
                Assert(f.FeedbackSources.All(x => x.Session == "tw-1"), "rejected station emitted feedback");
            }
        });
        await Run("TrackWave015: late continuation cannot append to manual queue, replacement station or signed-out account", async () =>
        {
            foreach (var action in new[] { "queue", "switch", "logout" })
            {
                using var f = new TwFixture("continuation-" + action); var w = f.Window;
                await Call(w, "OpenTrackWaveAsync", TwSeed("1234", "Первая волна", false)); await f.Ready("1234");
                var before = SaField<List<TrackRow>>(w, "_waveTracks").Count;
                f.HoldMore = true; f.RenewMore = action == "queue";
                var pending = Call(w, "ExtendWaveQueueAsync"); await f.Entered.Task;
                var queue = SaField<PlaybackQueue>(w, "_playbackQueue"); var manual = TwSeed("9999", "Новая очередь", true); Task? replacement = null;
                if (action == "queue") queue.SetItems([manual]);
                if (action == "switch") replacement = Call(w, "OpenTrackWaveAsync", TwSeed("5678", "Вторая волна", false));
                if (action == "logout") w.SignOut();
                f.Release.TrySetResult(); await pending; if (replacement is not null) await replacement;
                if (action == "queue")
                {
                    Assert(ReferenceEquals(queue.Current, manual) && queue.Items.Count == 1 && SaField<List<TrackRow>>(w, "_waveTracks").Count == before, "late continuation contaminated manual queue");
                    SaInvoke(w, "WavePlay_Click", null!, null!);
                    await f.Ready("1234");
                    await Call(w, "ExtendWaveQueueAsync");
                    Assert(f.MoreSessions.SequenceEqual(new[] { "tw-1", "tw-2" }) && queue.Items.Count == before + 4,
                        "returning to the same station could not recover a newer server session ID");
                }
                if (action == "switch") Assert(SaField<RadioBatch>(w, "_waveBatch").TrackSeedId == "5678" && queue.Items.All(track => !track.Id.StartsWith("90", StringComparison.Ordinal)), "previous continuation contaminated new station");
                if (action == "logout") Assert(SaField<RadioBatch?>(w, "_waveBatch") is null && SaField<List<TrackRow>>(w, "_waveTracks").Count == 0, "late continuation revived signed-out wave");
            }
        });
        await Run("TrackWave015: delayed renewal follows the live station across a prepared transition", async () =>
        {
            using var f = new TwFixture("renew-crossfade"); var w = f.Window;
            await Call(w, "OpenTrackWaveAsync", TwSeed("1234", "Волна", true)); await f.Ready("1234");
            var queue = SaField<PlaybackQueue>(w, "_playbackQueue"); var last = queue.Items[^1];
            f.HoldMore = f.RenewMore = true;
            await w.PlayTrackAsync(queue.Items[^2]);
            await f.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Until(() => f.Player.HasPrepared);
            await Call(w, "AdvanceAsync", 1, false);
            Assert(ReferenceEquals(queue.Current, last) && f.Player.IsPlaying && f.MoreRequests == 1,
                "known next track could not advance while continuation was held");
            f.Release.TrySetResult();
            await Until(() => SaField<RadioBatch>(w, "_waveBatch").SessionId == "tw-2");
            Assert(queue.Items.Count == 8 && ReferenceEquals(queue.Current, last), "same-queue transition lost a renewed batch");
            await Call(w, "ExtendWaveQueueAsync");
            Assert(f.MoreSessions.SequenceEqual(new[] { "tw-1", "tw-2" }), "next refill used the expired session");
            await Until(() => f.FeedbackSources.Contains(("tw-2", "first-2", "radioStarted", null)));
            Assert(f.FeedbackSources.Contains(("tw-1", "first-1", "trackStarted", last.Id))
                && f.FeedbackSources.Count(x => x == ("tw-2", "first-2", "radioStarted", null)) == 1,
                "renewal moved original listening feedback or sent a repeated station start");
        });
        await Run("TrackWave015: wave acceptance and next honor an indexed source being removed", async () =>
        {
            foreach (var scenario in new[] { "next-or-explicit", "all-blocked", "automatic", "cached" })
            {
                using var f = new TwFixture("offline-wave-admission-" + scenario); var w = f.Window;
                var queue = SaField<PlaybackQueue>(w, "_playbackQueue");
                if (scenario == "automatic")
                    await w.PlayTrackAsync(TwSeed("1234", "Обычный трек", true));
                else
                {
                    await Call(w, "OpenTrackWaveAsync", TwSeed("1234", "Волна", true));
                    await f.Ready("1234");
                }
                var prior = queue.Current;
                var version = queue.ReplacementVersion;
                if (scenario == "cached")
                    await w.PlayTrackAsync(TwSeed("9876", "Другая очередь", true));
                var firstBlocked = scenario is "automatic" or "cached" ? "801" : "802";
                f.IndexAudio(scenario == "all-blocked" ? ["802", "803", "804", "805"] : [firstBlocked]);
                UlSet(w, "_offlineRemovalTrackId", scenario == "all-blocked" ? null : firstBlocked);
                UlSet(w, "_offlineMaintenance", true);
                if (scenario == "next-or-explicit")
                {
                    await Call(w, "AdvanceAsync", 1, false);
                    Assert(ReferenceEquals(queue.Current, prior) && f.Player.IsPlaying,
                        "Next selected an indexed track while its file was being removed");
                }
                if (scenario == "automatic")
                {
                    f.Player.Stop();
                    await Call(w, "AdvanceAsync", 1, true);
                }
                else if (scenario == "cached")
                {
                    SaInvoke(w, "WavePlay_Click", null!, null!);
                    await Until(() => !SaField<bool>(w, "_loadingStream") && queue.Current?.Id == "802");
                }
                else await Call(w, "OpenTrackWaveAsync", TwSeed("5678", "Другая волна", true));

                if (scenario == "all-blocked")
                {
                    Assert(ReferenceEquals(queue.Current, prior) && queue.ReplacementVersion == version && f.Player.IsPlaying
                        && SaField<RadioBatch>(w, "_waveBatch").SessionId == "tw-1"
                        && !f.FeedbackSources.Any(item => item.Session == "tw-2"),
                        "blocked candidate replaced live source, queue or server station");
                    await Call(w, "ExtendWaveQueueAsync");
                    Assert(f.MoreSessions.SequenceEqual(new[] { "tw-1" }), "old API station lost continuation after rejection");
                }
                else
                {
                    var expected = scenario == "next-or-explicit" ? "803" : "802";
                    Assert(f.Player.IsPlaying && queue.Current?.Id == expected && queue.Items.All(row => row.Id != firstBlocked),
                        "wave did not select a source that could open during removal");
                }
            }
        });
        await Run("TrackWave015: native end closes listening while manual Next waits and an inserted next supersedes it", async () =>
        {
            foreach (var supersede in new[] { false, true })
            {
                using var f = new TwFixture("manual-end-" + supersede); var w = f.Window;
                await Call(w, "OpenTrackWaveAsync", TwSeed("1234", "Волна", true)); await f.Ready("1234");
                var queue = SaField<PlaybackQueue>(w, "_playbackQueue"); var last = queue.Items[^1];
                f.HoldMore = true;
                await w.PlayTrackAsync(last);
                await f.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                var pending = Call(w, "AdvanceAsync", 1, false);
                Assert(!pending.IsCompleted, "fixture did not hold manual Next");
                var listening = SaField<object>(w, "_waveListening");
                var clock = (System.Diagnostics.Stopwatch)listening.GetType().GetProperty("Clock")!.GetValue(listening)!;
                f.Player.Seek(f.Player.Duration - TimeSpan.FromSeconds(.25));
                await Until(() => f.Feedback.Contains(("trackFinished", last.Id)));
                var elapsed = clock.Elapsed;
                await Task.Delay(90);
                Assert(!clock.IsRunning && clock.Elapsed == elapsed && !pending.IsCompleted && !f.Player.IsPlaying,
                    "waiting after MediaEnded counted silence, or abandoned manual Next");

                var inserted = TwSeed("9876", "Следующий по выбору", true);
                if (supersede)
                {
                    queue.Enqueue(inserted, playNext: true);
                    SaInvoke(w, "UpdateControls");
                    await w.HandleDesktopMediaCommandAsync(DesktopMediaAction.Next).WaitAsync(TimeSpan.FromSeconds(5));
                    await pending.WaitAsync(TimeSpan.FromSeconds(5));
                    Assert(ReferenceEquals(queue.Current, inserted) && f.Player.IsPlaying, "known Play Next waited for server recommendations");
                }
                f.Release.TrySetResult();
                await pending.WaitAsync(TimeSpan.FromSeconds(5));
                await Until(() => !SaField<bool>(w, "_advancingWave"));
                Assert(f.Player.IsPlaying && queue.Current?.Id == (supersede ? inserted.Id : "901")
                    && f.FeedbackSources.Where(item => item.Track == last.Id).Select(item => item.Type)
                        .SequenceEqual(new[] { "trackStarted", "trackFinished" }),
                    "manual Next lost the native end or reported an incorrect skip");
            }
        });
        await Run("TrackWave015: a passive refill preserves native continuation while exact local admission waits", async () =>
        {
            using var f = new TwFixture("passive-append-admission");
            var w = f.Window;
            await Call(w, "OpenTrackWaveAsync", TwSeed("1234", "Волна", true));
            await f.Ready("1234");
            var queue = SaField<PlaybackQueue>(w, "_playbackQueue");
            var first = queue.Current!;
            var next = queue.Items[1];
            var token = queue.OccurrenceTokenAt(1);
            f.IndexAudio([next.Id]);
            f.HoldMore = true;
            var refill = Call(w, "ExtendWaveQueueAsync");
            await f.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var verifying = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            w.QueuePlaybackBeforeVerificationForChecks = cancellation =>
            {
                verifying.TrySetResult();
                return release.Task.WaitAsync(cancellation);
            };
            SaInvoke(w, "CancelNextPreparation", true);
            PlaybackOptionsInvoke(f.Player, "OnMediaEnded", PlaybackOptionsField<object>(f.Player, "_current"));
            await verifying.Task.WaitAsync(TimeSpan.FromSeconds(5));
            f.Release.TrySetResult();
            await refill.WaitAsync(TimeSpan.FromSeconds(5));
            Assert(ReferenceEquals(queue.Current, first) && queue.Items.Any(row => row.Id == "901"),
                "fixture did not append the passive response before committing the verified target");
            release.TrySetResult();
            await Until(() => f.Player.IsPlaying && queue.CurrentOccurrenceToken == token
                && f.Feedback.Contains(("trackStarted", next.Id)));
            Assert(ReferenceEquals(queue.Current, next) && queue.History.SequenceEqual([first])
                && f.Feedback.Count(item => item == ("trackStarted", next.Id)) == 1,
                "passive append invalidated the same accepted occurrence or restarted it");
        });
        await Run("TrackWave015: queued Previous and Next retain exact occurrences during passive refill", async () =>
        {
            foreach (var duplicate in new[] { false, true })
            {
                using var f = new TwFixture("passive-fifo-" + duplicate);
                var w = f.Window;
                await Call(w, "OpenTrackWaveAsync", TwSeed("1234", "Волна", true));
                await f.Ready("1234");
                var queue = SaField<PlaybackQueue>(w, "_playbackQueue");
                var first = queue.Current!;
                var previousToken = queue.CurrentOccurrenceToken;
                if (duplicate) queue.Enqueue(first, playNext: true);
                var second = queue.Next()!;
                var nextToken = queue.CurrentOccurrenceToken;
                await w.PlayTrackAsync(second);
                f.HoldMore = true;
                var refill = Call(w, "ExtendWaveQueueAsync");
                await f.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                SaInvoke(w, "CancelNextPreparation", true);
                var readiness = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                UlSet(w, "_audioOutputReady", readiness.Task);
                var previous = Call(w, "AdvanceAsync", -1, false);
                await Until(() => queue.CurrentOccurrenceToken == previousToken);
                Assert(!previous.IsCompleted && !refill.IsCompleted, "fixture did not hold the opening and passive refill");
                var next = Call(w, "AdvanceAsync", 1, false);
                await Until(() => queue.CurrentOccurrenceToken == nextToken);
                Assert(!next.IsCompleted && !refill.IsCompleted, "known Next waited for a decoder or for the passive reply");
                readiness.TrySetResult();
                f.Release.TrySetResult();
                await Task.WhenAll(previous, next, refill).WaitAsync(TimeSpan.FromSeconds(5));
                Assert(f.Player.IsPlaying && ReferenceEquals(queue.Current, second) && queue.CurrentOccurrenceToken == nextToken
                    && queue.History.SequenceEqual([first]) && queue.Items.Any(row => row.Id == "901"),
                    "late open or reply replaced the newest exact occurrence");
            }
        });
        await Run("TrackWave015: desktop Play Pause Stop and player Toggle honor a held cold natural admission", async () =>
        {
            foreach (var command in new[] { "play", "pause", "toggle", "stop" })
            {
                using var f = new TwFixture("cold-transport-" + command);
                var w = f.Window;
                await Call(w, "OpenTrackWaveAsync", TwSeed("1234", "Волна", true));
                await f.Ready("1234");
                var q = SaField<PlaybackQueue>(w, "_playbackQueue");
                var source = q.Current!;
                var next = q.Items[1];
                var sourceToken = q.CurrentOccurrenceToken;
                var nextToken = q.OccurrenceTokenAt(1);
                f.IndexAudio([next.Id]);
                var verifying = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                w.QueuePlaybackBeforeVerificationForChecks = token =>
                {
                    verifying.TrySetResult();
                    return release.Task.WaitAsync(token);
                };
                SaInvoke(w, "CancelNextPreparation", true);
                PlaybackOptionsInvoke(f.Player, "OnMediaEnded", PreloadGet<object>(f.Player, "_current"));
                await verifying.Task.WaitAsync(TimeSpan.FromSeconds(5));
                var playPause = Control<Button>(w, "PlayPauseButton");
                Assert(System.Windows.Automation.AutomationProperties.GetName(playPause) == "Пауза"
                    && !f.Player.IsPlaying && q.CurrentOccurrenceToken == sourceToken, "held admission was missing from transport state");
                switch (command)
                {
                    case "play":
                        await w.HandleDesktopMediaCommandAsync(DesktopMediaAction.Play);
                        Assert(q.CurrentOccurrenceToken == sourceToken && !f.Player.IsPlaying,
                            "SMTC Play restarted the ended source or replaced the pending transition");
                        break;
                    case "pause":
                        await w.HandleDesktopMediaCommandAsync(DesktopMediaAction.Pause);
                        await w.HandleDesktopMediaCommandAsync(DesktopMediaAction.Pause);
                        break;
                    case "toggle": await Call(w, "TogglePlaybackAsync"); break;
                    case "stop": await w.HandleDesktopMediaCommandAsync(DesktopMediaAction.Stop); break;
                }
                release.TrySetResult();
                await Until(() => !SaField<bool>(w, "_advancingPlayback")
                    && f.Feedback.Count(item => item == ("trackFinished", source.Id)) == 1);
                if (command == "play")
                {
                    await Until(() => f.Feedback.Contains(("trackStarted", next.Id)));
                    Assert(q.CurrentOccurrenceToken == nextToken && f.Player.IsPlaying
                        && f.Feedback.Count(item => item == ("trackStarted", next.Id)) == 1,
                        "SMTC Play disowned an active cold transition");
                }
                else
                {
                    Assert(q.CurrentOccurrenceToken == sourceToken && !f.Player.IsPlaying && f.Player.HasSource == (command != "stop")
                        && f.Feedback.All(item => item != ("trackStarted", next.Id)),
                        "Pause or Stop allowed a late local admission to launch the next track");
                }
            }
        });
        await Run("TrackWave015: held removal follows exact selected tokens across passive append and trimming", async () =>
        {
            foreach (var scenario in new[] { "append", "ended", "trim" })
            {
                using var f = new TwFixture("remove-passive-" + scenario);
                var w = f.Window;
                await Call(w, "OpenTrackWaveAsync", TwSeed("1234", "Волна", true));
                await f.Ready("1234");
                var q = SaField<PlaybackQueue>(w, "_playbackQueue");
                var source = q.Current!;
                var next = q.Items[1];
                if (scenario == "trim")
                {
                    q.SetItems(Enumerable.Repeat(source, 23).Append(next));
                    for (var index = 0; index < 22; index++) q.Next();
                }
                var sourceToken = q.CurrentOccurrenceToken;
                var nextToken = q.OccurrenceTokenAt(q.CurrentIndex + 1);
                f.IndexAudio([next.Id]);
                f.HoldMore = true;
                var refill = Call(w, "ExtendWaveQueueAsync");
                await f.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                var verifying = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                w.QueuePlaybackBeforeVerificationForChecks = token =>
                {
                    verifying.TrySetResult();
                    return release.Task.WaitAsync(token);
                };
                SaInvoke(w, "CancelNextPreparation", true);
                SaInvoke(w, "ShowQueueCore", false);
                var beforeIndex = q.CurrentIndex;
                var removing = Call(w, "RemoveSelectedQueueAsync");
                await verifying.Task.WaitAsync(TimeSpan.FromSeconds(5));
                if (scenario != "append") PlaybackOptionsInvoke(f.Player, "OnMediaEnded", PreloadGet<object>(f.Player, "_current"));
                f.Release.TrySetResult();
                await refill.WaitAsync(TimeSpan.FromSeconds(5));
                Assert(q.CurrentOccurrenceToken == sourceToken && q.Items.Any(row => row.Id == "901")
                    && (scenario != "trim" || q.CurrentIndex < beforeIndex), "fixture did not present a compatible structural edit");
                release.TrySetResult();
                await removing.WaitAsync(TimeSpan.FromSeconds(5));
                Assert(q.CurrentOccurrenceToken == nextToken && f.Player.IsPlaying && q.Items.Any(row => row.Id == "901")
                    && Enumerable.Range(0, q.Items.Count).All(index => q.OccurrenceTokenAt(index) != sourceToken),
                    "passive refill canceled removal or changed which occurrence was removed");
                Assert(q.UndoRemoval() && !q.CanUndoRemoval && q.CurrentOccurrenceToken == nextToken
                    && Enumerable.Range(0, q.Items.Count).Any(index => q.OccurrenceTokenAt(index) == sourceToken),
                    "Undo did not restore the exact removed token once");
                if (scenario != "append")
                {
                    await Until(() => f.Feedback.Contains(("trackStarted", next.Id)));
                    Assert(f.Feedback.Count(item => item == ("trackFinished", source.Id)) == 1
                        && f.Feedback.Count(item => item == ("trackStarted", next.Id)) == 1,
                        "the native end and accepted removal both consumed the same transition");
                }
            }
        });
        await Run("TrackWave015: a manual Wave wait carries the native end into a cold accepted candidate", async () =>
        {
            foreach (var scenario in new[] { "busy", "damaged", "pause", "second-next", "second-next-empty" })
            {
                using var f = new TwFixture("ended-refill-admission-" + scenario) { MoreAvailability = id => id != 901 };
                var w = f.Window;
                await Call(w, "OpenTrackWaveAsync", TwSeed("1234", "Волна", true));
                await f.Ready("1234");
                var q = SaField<PlaybackQueue>(w, "_playbackQueue");
                var source = q.Items[^1];
                f.IndexAudio(["901"]);
                var library = SaField<OfflineLibrary>(w, "_offline");
                var saved = library.Read().Single();
                var path = library.ResolvePath(saved.FileName);
                if (scenario == "damaged") ReplaceWithSameLengthSilentWave(path, saved);
                f.HoldMore = true;
                await w.PlayTrackAsync(source);
                var sourceToken = q.CurrentOccurrenceToken;
                await f.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                var manual = Call(w, "AdvanceAsync", 1, false);
                Assert(!manual.IsCompleted, "fixture did not enter a manual recommendation wait");
                PlaybackOptionsInvoke(f.Player, "OnMediaEnded", PreloadGet<object>(f.Player, "_current"));
                var checking = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                w.QueuePlaybackBeforeVerificationForChecks = token =>
                {
                    checking.TrySetResult();
                    return release.Task.WaitAsync(token);
                };
                Task? newer = null;
                if (scenario == "second-next-empty")
                {
                    await Call(w, "AdvanceAsync", 1, false).WaitAsync(TimeSpan.FromSeconds(5));
                    Assert(!manual.IsCompleted && q.CurrentOccurrenceToken == sourceToken, "second Next had a target or displaced the active wait");
                }
                var expected = "902";
                if (scenario == "second-next")
                {
                    var rejected = new TrackRow { Id = "901", RequestId = "901:91", Title = "Проверяемый трек", DurationMs = 30000 };
                    var following = TwSeed("9876", "Следующий", true);
                    SaInvoke(w, "ApplyOfflineStates", (object)new[] { rejected });
                    q.Enqueue(rejected, playNext: true);
                    q.Enqueue(following);
                    expected = following.Id;
                    newer = Call(w, "AdvanceAsync", 1, false);
                }
                else f.Release.TrySetResult();

                await checking.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert(q.CurrentOccurrenceToken == sourceToken && !f.Player.IsPlaying
                    && System.Windows.Automation.AutomationProperties.GetName(Control<Button>(w, "PlayPauseButton")) == "Пауза",
                    "the ended source lost its transport intent between recommendations and local admission");
                using var busy = scenario is "busy" or "second-next" or "second-next-empty"
                    ? File.Open(path, FileMode.Open, FileAccess.Read, FileShare.None) : null;
                if (scenario == "pause") await w.HandleDesktopMediaCommandAsync(DesktopMediaAction.Pause);
                release.TrySetResult();
                await Task.WhenAll(manual, newer ?? Task.CompletedTask).WaitAsync(TimeSpan.FromSeconds(5));
                f.Release.TrySetResult();
                await Until(() => !SaField<bool>(w, "_advancingPlayback") && !SaField<bool>(w, "_advancingWave")
                    && f.Feedback.Contains(("trackFinished", source.Id)));
                Assert(f.Feedback.Count(item => item == ("trackFinished", source.Id)) == 1
                    && f.Feedback.All(item => item != ("trackStarted", "901")), "the wait duplicated Ended or started the first rejected source");
                if (scenario == "pause")
                    Assert(q.CurrentOccurrenceToken == sourceToken && !f.Player.IsPlaying, "Pause let the Wave handoff start a track");
                else
                    Assert(q.Current?.Id == expected && f.Player.IsPlaying && ReferenceEquals(q.History[^1], source),
                        "the failed manual admission did not carry the original native end to the next candidate");
            }
        });
        await Run("TrackWave015: Next inherits Ended from automatic More after the first known local source fails", async () =>
        {
            using var f = new TwFixture("automatic-more-next-admission");
            var w = f.Window;
            await Call(w, "OpenTrackWaveAsync", TwSeed("1234", "Волна", true));
            await f.Ready("1234");
            var q = SaField<PlaybackQueue>(w, "_playbackQueue");
            var source = q.Items[^1];
            var failed = TwSeed("901", "Проверяемый", false);
            var following = TwSeed("9876", "Следующий", true);
            f.IndexAudio([failed.Id]);
            var library = SaField<OfflineLibrary>(w, "_offline");
            var path = library.ResolvePath(library.Read().Single().FileName);
            SaInvoke(w, "ApplyOfflineStates", (object)new[] { failed });
            SaInvoke(w, "RememberWaveSource", following, SaField<RadioBatch>(w, "_waveBatch"));
            f.HoldMore = true;
            await w.PlayTrackAsync(source);
            await f.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            PlaybackOptionsInvoke(f.Player, "OnMediaEnded", PreloadGet<object>(f.Player, "_current"));
            Assert(SaField<object?>(w, "_waveAdvanceWait") is { } wait
                && wait.GetType().GetProperty("Automatic")?.GetValue(wait) is true && !f.Player.IsPlaying,
                "fixture did not hold an automatic Wave continuation at Ended");
            SaInvoke(w, "EnqueueActionTrack", failed, true);
            SaInvoke(w, "EnqueueActionTrack", following, false);
            var target = q.OccurrenceTokenAt(q.Items.Count - 1);
            using (File.Open(path, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                await w.HandleDesktopMediaCommandAsync(DesktopMediaAction.Next).WaitAsync(TimeSpan.FromSeconds(5));
                await Until(() => f.Feedback.Contains(("trackStarted", following.Id)));
                Assert(q.CurrentOccurrenceToken == target && f.Player.IsPlaying && ReferenceEquals(q.History[^1], source)
                    && f.Feedback.Count(item => item == ("trackFinished", source.Id)) == 1
                    && f.Feedback.Count(item => item == ("trackStarted", following.Id)) == 1
                    && f.Feedback.All(item => item != ("trackStarted", failed.Id)),
                    "manual Next lost the original native end or started a rejected occurrence");
            }
            f.Release.TrySetResult();
        });
        await Run("TrackWave015: showing the loaded personal Wave keeps the committed cold start", async () =>
        {
            using var f = new TwFixture("loaded-personal-cold");
            var w = f.Window;
            await Call(w, "OpenWaveAsync", WaveMode.Personal);
            await Until(() => f.Player.IsPlaying);
            var q = SaField<PlaybackQueue>(w, "_playbackQueue");
            var next = q.Items[1];
            var nextToken = q.OccurrenceTokenAt(1);
            SaInvoke(w, "ShowQueueCore", false);
            SaInvoke(w, "CancelNextPreparation", true);
            var routing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            UlSet(w, "_audioOutputReady", routing.Task);
            try
            {
                PlaybackOptionsInvoke(f.Player, "OnMediaEnded", PreloadGet<object>(f.Player, "_current"));
                await Until(() => q.CurrentOccurrenceToken == nextToken);
                Assert(!f.Player.HasSource && !routing.Task.IsCompleted, "fixture did not hold the committed cold source");
                await Call(w, "OpenWaveAsync", WaveMode.Personal);
                Assert(SaField<string>(w, "_section") == "wave" && q.CurrentOccurrenceToken == nextToken && f.Seeds.Count == 1,
                    "showing an existing Wave replaced its pending occurrence or requested a new station");
                routing.TrySetResult();
                await Until(() => f.Player.IsPlaying && f.Feedback.Contains(("trackStarted", next.Id)));
                Assert(ReferenceEquals(q.Current, next) && q.CurrentOccurrenceToken == nextToken
                    && f.Feedback.Count(item => item == ("trackStarted", next.Id)) == 1,
                    "showing the existing station canceled or restarted its cold track");
            }
            finally { routing.TrySetResult(); }
        });
        await Run("TrackWave015: removal inherits the active cold continuation after native end", async () =>
        {
            foreach (var cancel in new[] { false, true })
            {
                using var f = new TwFixture("removal-inherits-cold-" + cancel);
                var w = f.Window;
                await Call(w, "OpenTrackWaveAsync", TwSeed("1234", "Волна", true));
                await f.Ready("1234");
                var q = SaField<PlaybackQueue>(w, "_playbackQueue");
                var source = q.Current!;
                var next = q.Items[1];
                var sourceToken = q.CurrentOccurrenceToken;
                var nextToken = q.OccurrenceTokenAt(1);
                f.IndexAudio([next.Id]);
                SaInvoke(w, "ShowQueueCore", false);
                SaInvoke(w, "CancelNextPreparation", true);
                var automatic = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var deletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var verifications = 0;
                w.QueuePlaybackBeforeVerificationForChecks = token =>
                {
                    if (++verifications == 1) automatic.TrySetResult();
                    else deletion.TrySetResult();
                    return release.Task.WaitAsync(token);
                };
                PlaybackOptionsInvoke(f.Player, "OnMediaEnded", PreloadGet<object>(f.Player, "_current"));
                await automatic.Task.WaitAsync(TimeSpan.FromSeconds(5));
                var remove = Call(w, "RemoveSelectedQueueAsync");
                await deletion.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert(q.CurrentOccurrenceToken == sourceToken && !f.Player.IsPlaying
                    && System.Windows.Automation.AutomationProperties.GetName(Control<Button>(w, "PlayPauseButton")) == "Пауза",
                    "removal stopped exposing the inherited play intent");
                if (cancel) await w.HandleDesktopMediaCommandAsync(DesktopMediaAction.Pause);
                release.TrySetResult();
                await remove.WaitAsync(TimeSpan.FromSeconds(5));
                await Until(() => !SaField<bool>(w, "_advancingPlayback")
                    && f.Feedback.Contains(("trackFinished", source.Id))
                    && (cancel || f.Feedback.Contains(("trackStarted", next.Id))));
                Assert(f.Feedback.Count(item => item == ("trackFinished", source.Id)) == 1
                    && q.CurrentOccurrenceToken == (cancel ? sourceToken : nextToken) && f.Player.IsPlaying != cancel,
                    "removal duplicated Ended or lost the active-versus-paused intent");
                if (cancel)
                    Assert(!q.CanUndoRemoval && f.Feedback.All(item => item != ("trackStarted", next.Id)), "Pause failed to cancel the held deletion transition");
                else
                    Assert(f.Feedback.Count(item => item == ("trackStarted", next.Id)) == 1 && q.UndoRemoval()
                        && !q.CanUndoRemoval && q.CurrentOccurrenceToken == nextToken && q.OccurrenceTokenAt(0) == sourceToken,
                        "removal did not play and retain one Undo of the removed token");
            }
        });
        await Run("TrackWave015: New Wave validates an indexed first recommendation before publishing the station", async () =>
        {
            foreach (var pause in new[] { false, true })
            {
                using var f = new TwFixture("new-wave-first-admission-" + pause) { StartAvailability = id => id != 801 };
                var w = f.Window;
                var q = SaField<PlaybackQueue>(w, "_playbackQueue");
                var old = TwSeed("1234", "Прежний трек", true);
                await w.PlayTrackAsync(old);
                var occurrence = q.CurrentOccurrenceToken;
                f.IndexAudio(["801"]);
                var library = SaField<OfflineLibrary>(w, "_offline");
                var saved = library.Read().Single();
                ReplaceWithSameLengthSilentWave(library.ResolvePath(saved.FileName), saved);
                var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                w.QueuePlaybackBeforeVerificationForChecks = token =>
                {
                    entered.TrySetResult();
                    return release.Task.WaitAsync(token);
                };
                var start = Call(w, "StartWaveViewAsync", true);
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert(q.CurrentOccurrenceToken == occurrence && f.Player.IsPlaying && f.Feedback.Count == 0,
                    "the old queue was replaced before first-file admission");
                if (pause) await w.HandleDesktopMediaCommandAsync(DesktopMediaAction.Pause);
                release.TrySetResult();
                await start.WaitAsync(TimeSpan.FromSeconds(5));
                if (pause)
                    Assert(q.CurrentOccurrenceToken == occurrence && !f.Player.IsPlaying && f.Feedback.Count == 0,
                        "Pause allowed an unaccepted station to replace the current queue");
                else
                {
                    await Until(() => f.Feedback.Contains(("trackStarted", "802")));
                    Assert(q.Current?.Id == "802" && f.Player.IsPlaying && q.History.Count == 0
                        && f.Feedback.Count(item => item.Type == "radioStarted") == 1
                        && f.Feedback.Where(item => item.Type == "trackStarted").Select(item => item.Track).SequenceEqual(["802"]),
                        "New Wave stopped at its rejected file or published an extra track start");
                }
            }
        });
        await Run("TrackWave015: cached and new Wave and automatic mix stage damaged-file online fallback", async () =>
        {
            var full = await PlaybackOptionsMp3Async();
            foreach (var action in new[] { "new-short", "new-full", "new-pause", "cached-short", "cached-rejected", "mix-short" })
            {
                var cached = action.StartsWith("cached", StringComparison.Ordinal);
                var automatic = action == "mix-short";
                var useFull = action == "new-full";
                using var f = new TwFixture("staged-wave-online-" + action)
                {
                    LongStartTrack = 801,
                    LongStartAudio = cached || useFull ? full : null,
                    StartAvailability = action == "cached-rejected" ? id => id == 801 : null,
                    MoreAvailability = action == "cached-rejected" ? static _ => false : null
                };
                var w = f.Window;
                if (cached)
                {
                    await Call(w, "OpenTrackWaveAsync", TwSeed("2345", "Прежняя волна", true));
                    await f.Ready("2345");
                    f.LongStartAudio = null;
                }
                var ordinary = TwSeed("1234", "Прежняя очередь", true);
                await w.PlayTrackAsync(ordinary);
                await SaField<Task>(w, "_feedbackTail");
                f.Feedback.Clear();
                var q = SaField<PlaybackQueue>(w, "_playbackQueue");
                var prior = q.CurrentOccurrenceToken;
                var priorReplacement = q.ReplacementVersion;
                var priorHistory = q.History.ToArray();
                var previousStation = SaField<RadioBatch?>(w, "_waveBatch")?.Station;
                if (action == "cached-rejected")
                    Assert(SaField<List<TrackRow>>(w, "_waveTracks").Where(track => track.Available).Select(track => track.Id).SequenceEqual(["801"]),
                        "the exhausted cache fixture has another playable recommendation");
                f.IndexAudio(["801"]);
                var library = SaField<OfflineLibrary>(w, "_offline");
                var saved = library.Read().Single();
                ReplaceWithSameLengthSilentWave(library.ResolvePath(saved.FileName), saved);
                f.HoldLongStart = true;

                Task start;
                if (automatic)
                {
                    PlaybackOptionsInvoke(f.Player, "OnMediaEnded", PreloadGet<object>(f.Player, "_current"));
                    start = Until(() => !SaField<bool>(w, "_advancingPlayback"));
                }
                else if (cached)
                {
                    SaInvoke(w, "WavePlay_Click", null!, null!);
                    start = Until(() => !SaField<bool>(w, "_waveStarting"));
                }
                else start = Call(w, "StartWaveViewAsync", true);

                await f.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert(q.CurrentOccurrenceToken == prior && f.Player.IsPlaying == !automatic && f.Feedback.Count == 0
                    && ReferenceEquals(previousStation, SaField<RadioBatch?>(w, "_waveBatch")?.Station),
                    "the fallback replaced the queue or published feedback before opening the decoder");
                if (action == "new-pause") await w.HandleDesktopMediaCommandAsync(DesktopMediaAction.Pause);
                f.Release.TrySetResult();
                await start.WaitAsync(TimeSpan.FromSeconds(8));
                await SaField<Task>(w, "_feedbackTail");
                if (action is "new-pause" or "cached-rejected")
                {
                    Assert(q.CurrentOccurrenceToken == prior && ReferenceEquals(q.Current, ordinary)
                        && q.ReplacementVersion == priorReplacement && q.History.SequenceEqual(priorHistory)
                        && f.Player.IsPlaying == (action == "cached-rejected") && f.Feedback.Count == 0
                        && ReferenceEquals(previousStation, SaField<RadioBatch?>(w, "_waveBatch")?.Station),
                        $"{action}: a canceled or exhausted admission displaced the old queue or station");
                    continue;
                }
                var expected = useFull ? "801" : "802";
                Assert(q.Current?.Id == expected && f.Player.IsPlaying
                    && f.Feedback.Count(item => item.Type == "radioStarted") == (cached ? 0 : 1)
                    && f.Feedback.Where(item => item.Type == "trackStarted").Select(item => item.Track).SequenceEqual([expected]),
                    "a viable Wave fallback was dropped, replayed, or failed to move past a preview");
                if (cached)
                    Assert(f.Seeds.Count == 1 && ReferenceEquals(previousStation, SaField<RadioBatch>(w, "_waveBatch").Station),
                        "cached Wave playback renewed the station");
            }
        });
        await Run("TrackWave015: Next can replace an unplayed occurrence without losing its fallback or Pause", async () =>
        {
            foreach (var (priorRemoval, action) in new[] { (false, "busy"), (false, "success"), (false, "pause"),
                (true, "busy"), (true, "success"), (true, "pause"), (true, "stop") })
            {
                using var f = new TwFixture($"next-before-first-play-{priorRemoval}-{action}");
                var w = f.Window;
                await Call(w, "OpenTrackWaveAsync", TwSeed("1234", "Волна", true));
                await f.Ready("1234");
                var q = SaField<PlaybackQueue>(w, "_playbackQueue");
                var source = q.Current!;
                var pending = q.Items[1];
                var successor = q.Items[2];
                var pendingToken = q.OccurrenceTokenAt(1);
                var successorToken = q.OccurrenceTokenAt(2);
                f.IndexAudio([successor.Id]);
                var library = SaField<OfflineLibrary>(w, "_offline");
                var path = library.ResolvePath(library.Read().Single().FileName);
                var (windows, Dispatch) = PlaybackOptionsMediaGate(w);
                SaInvoke(w, "CancelNextPreparation", true);
                var routing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                UlSet(w, "_audioOutputReady", routing.Task);
                var removing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                w.QueuePlaybackBeforeVerificationForChecks = token =>
                {
                    if (!priorRemoval || !removing.TrySetResult()) entered.TrySetResult();
                    return release.Task.WaitAsync(token);
                };
                try
                {
                    PlaybackOptionsInvoke(f.Player, "OnMediaEnded", PreloadGet<object>(f.Player, "_current"));
                    await Until(() => q.CurrentOccurrenceToken == pendingToken);
                    Assert(!f.Player.HasSource && !f.Player.IsPlaying, "fixture did not hold B before the first Play");
                    Task? removal = null;
                    if (priorRemoval)
                    {
                        SaInvoke(w, "ShowQueueCore", false);
                        SaInvoke(w, "SelectVisibleQueueOccurrence", q.CurrentIndex);
                        removal = Call(w, "RemoveSelectedQueueAsync");
                        await removing.Task.WaitAsync(TimeSpan.FromSeconds(5));
                        Assert(SaField<object?>(w, "_queueRemovalContinuation") is not null, "removal did not own the cold start");
                    }
                    var command = Call(w, "AdvanceAsync", 1, false);
                    await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    if (removal is not null) await removal.WaitAsync(TimeSpan.FromSeconds(5));
                    Assert(q.CurrentOccurrenceToken == pendingToken && windows.HandlesMediaKeys
                        && System.Windows.Automation.AutomationProperties.GetName(Control<Button>(w, "PlayPauseButton")) == "Пауза",
                        "manual admission took away the exact current occurrence or the Pause action");
                    using var busy = action == "busy" ? File.Open(path, FileMode.Open, FileAccess.Read, FileShare.None) : null;
                    if (action == "pause") await Dispatch(Windows.Media.SystemMediaTransportControlsButton.Pause);
                    if (action == "stop") await Dispatch(Windows.Media.SystemMediaTransportControlsButton.Stop);
                    release.TrySetResult();
                    routing.TrySetResult();
                    await command.WaitAsync(TimeSpan.FromSeconds(5));
                    await Until(() => f.Feedback.Contains(("trackFinished", source.Id)));
                    if (action is "pause" or "stop")
                    {
                        Assert(q.CurrentOccurrenceToken == pendingToken && !f.Player.IsPlaying && windows.HandlesMediaKeys == (action == "pause")
                            && !q.CanUndoRemoval && f.Feedback.All(item => item != ("trackStarted", pending.Id) && item != ("trackStarted", successor.Id)),
                            "Pause or Stop lost the current occurrence or allowed an abandoned start");
                    }
                    else
                    {
                        var expected = action == "success" ? successor : pending;
                        var rejected = action == "success" ? pending : successor;
                        await Until(() => f.Feedback.Contains(("trackStarted", expected.Id)));
                        Assert(q.CurrentOccurrenceToken == (action == "success" ? successorToken : pendingToken) && f.Player.IsPlaying
                            && f.Feedback.Count(item => item == ("trackStarted", expected.Id)) == 1
                            && f.Feedback.All(item => item != ("trackStarted", rejected.Id)),
                            "the rejected replacement discarded B, or admitted C started both sources");
                    }
                }
                finally { routing.TrySetResult(); release.TrySetResult(); }
            }
        });
        await Run("TrackWave015: removing the committed automatic occurrence transfers its unplayed intent", async () =>
        {
            foreach (var (repeat, action) in new[] { (false, "play"), (false, "pause"), (true, "play"),
                (true, "pause"), (true, "stop"), (true, "after-track"), (true, "output-lost") })
            {
                var cancel = action != "play";
                using var f = new TwFixture($"remove-selected-cold-{repeat}-{action}");
                var w = f.Window;
                await Call(w, "OpenTrackWaveAsync", TwSeed("1234", "Волна", true));
                await f.Ready("1234");
                var (windows, Dispatch) = PlaybackOptionsMediaGate(w);
                var q = SaField<PlaybackQueue>(w, "_playbackQueue");
                var source = q.Current!;
                var removed = q.Items[1];
                var next = q.Items[2];
                var removedToken = q.OccurrenceTokenAt(1);
                var nextToken = q.OccurrenceTokenAt(2);
                f.IndexAudio([next.Id]);
                SaInvoke(w, "ShowQueueCore", false);
                SaInvoke(w, "CancelNextPreparation", true);
                var routing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                UlSet(w, "_audioOutputReady", routing.Task);
                var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var reentered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var verifications = 0;
                w.QueuePlaybackBeforeVerificationForChecks = token =>
                {
                    if (++verifications == 1) entered.TrySetResult();
                    else reentered.TrySetResult();
                    return release.Task.WaitAsync(token);
                };
                try
                {
                    PlaybackOptionsInvoke(f.Player, "OnMediaEnded", PreloadGet<object>(f.Player, "_current"));
                    await Until(() => q.CurrentOccurrenceToken == removedToken);
                    Assert(!f.Player.HasSource && !f.Player.IsPlaying, "B already started before the removal command");
                    SaInvoke(w, "SelectVisibleQueueOccurrence", q.CurrentIndex);
                    var removal = Call(w, "RemoveSelectedQueueAsync");
                    await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    Task? repeatedRemoval = null;
                    if (repeat)
                    {
                        repeatedRemoval = Call(w, "RemoveSelectedQueueAsync");
                        await reentered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                        await removal.WaitAsync(TimeSpan.FromSeconds(5));
                    }
                    var checking = SaField<(string TrackId, CancellationTokenSource Request)?>(w, "_pendingOfflinePlayback");
                    var continuation = SaField<object?>(w, "_queueRemovalContinuation");
                    Assert(q.CurrentOccurrenceToken == removedToken && checking is { } candidate
                        && candidate.TrackId == next.Id && ReferenceEquals(candidate.Request,
                            continuation?.GetType().GetProperty("Request")?.GetValue(continuation)),
                        $"removal did not inherit the exact target and pending admission ({repeat}, {action})");
                    Assert(System.Windows.Automation.AutomationProperties.GetName(Control<Button>(w, "PlayPauseButton")) == "Пауза",
                        $"transport did not publish inherited Pause during admission ({repeat}, {action})");
                    if (action == "pause") await Dispatch(Windows.Media.SystemMediaTransportControlsButton.Pause);
                    if (action == "stop") await w.HandleDesktopMediaCommandAsync(DesktopMediaAction.Stop);
                    if (action == "after-track") w.SetSleepTimer(SleepTimerMode.AfterCurrentTrack);
                    if (action == "output-lost") SaInvoke(w, "PausePlaybackAfterOutputLoss");
                    release.TrySetResult();
                    if (!cancel) await Until(() => q.CurrentOccurrenceToken == nextToken);
                    routing.TrySetResult();
                    await (repeatedRemoval ?? removal).WaitAsync(TimeSpan.FromSeconds(5));
                    await Until(() => f.Feedback.Contains(("trackFinished", source.Id))
                        && (cancel || f.Feedback.Contains(("trackStarted", next.Id))));
                    Assert(q.CurrentOccurrenceToken == (cancel ? removedToken : nextToken) && f.Player.IsPlaying != cancel
                        && f.Feedback.Count(item => item == ("trackFinished", source.Id)) == 1
                        && f.Feedback.All(item => item != ("trackStarted", removed.Id)),
                        "removal or Pause resurrected the canceled source or lost its successor");
                    if (cancel)
                    {
                        Assert(!q.CanUndoRemoval && f.Feedback.All(item => item != ("trackStarted", next.Id)),
                            $"{action} did not withdraw the pending removal continuation");
                        if (action is "pause" or "output-lost")
                        {
                            Assert(windows.HandlesMediaKeys && !f.Player.HasSource, "canceled removal discarded the retained SMTC occurrence");
                            await Dispatch(Windows.Media.SystemMediaTransportControlsButton.Play).WaitAsync(TimeSpan.FromSeconds(5));
                            await Until(() => f.Feedback.Contains(("trackStarted", removed.Id)));
                            Assert(q.CurrentOccurrenceToken == removedToken && f.Player.IsPlaying
                                && f.Feedback.Count(item => item == ("trackStarted", removed.Id)) == 1,
                                "system Play did not resume the exact removed-but-uncommitted occurrence");
                        }
                    }
                    else Assert(f.Feedback.Count(item => item == ("trackStarted", next.Id)) == 1 && q.UndoRemoval()
                        && !q.CanUndoRemoval && q.CurrentOccurrenceToken == nextToken && q.OccurrenceTokenAt(1) == removedToken,
                        "the successor did not start once or Undo lost the exact removed occurrence");
                }
                finally { routing.TrySetResult(); release.TrySetResult(); }
            }
        });
        await Run("TrackWave015: Previous supersedes manual Next while recommendations are held", async () =>
        {
            using var f = new TwFixture("manual-next-previous"); var w = f.Window;
            f.Player.SetCrossfade(0);
            await Call(w, "OpenTrackWaveAsync", TwSeed("1234", "Волна", true)); await f.Ready("1234");
            var queue = SaField<PlaybackQueue>(w, "_playbackQueue"); var previous = queue.Current!;
            var occurrence = queue.CurrentOccurrenceToken;
            f.HoldMore = true;
            await w.PlayTrackAsync(queue.Items[^1]);
            await f.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var pending = Call(w, "AdvanceAsync", 1, false);
            Assert(!pending.IsCompleted && f.Player.Position.TotalSeconds < 3 && ReferenceEquals(queue.History[^1], previous),
                "fixture did not hold manual Next with an exact Previous target");
            await w.HandleDesktopMediaCommandAsync(DesktopMediaAction.Previous).WaitAsync(TimeSpan.FromSeconds(5));
            await pending.WaitAsync(TimeSpan.FromSeconds(5));
            Assert(f.Player.IsPlaying && ReferenceEquals(queue.Current, previous) && queue.CurrentOccurrenceToken == occurrence,
                "Previous waited for the server or lost the prior occurrence");
            f.Release.TrySetResult();
            await Until(() => !SaField<bool>(w, "_advancingWave"));
            Assert(ReferenceEquals(queue.Current, previous) && queue.CurrentOccurrenceToken == occurrence && f.Player.IsPlaying
                && f.Feedback.All(item => item != ("trackStarted", "901")), "late refill advanced after Previous");
        });
        await Run("TrackWave015: exhausted manual refill completes only the ended source it was waiting on", async () =>
        {
            foreach (var scenario in new[] { "ended-failure", "ended-unavailable", "still-playing", "new-play" })
            {
                using var f = new TwFixture("manual-next-empty-" + scenario) { HoldMore = true }; var w = f.Window;
                if (scenario == "ended-unavailable") f.UnavailableMore = 3;
                else f.MoreFailures = 1;
                await Call(w, "OpenTrackWaveAsync", TwSeed("1234", "Волна", true)); await f.Ready("1234");
                var queue = SaField<PlaybackQueue>(w, "_playbackQueue"); var last = queue.Items[^1];
                await w.PlayTrackAsync(last);
                await f.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                var occurrence = queue.CurrentOccurrenceToken;
                var source = f.Player.SourceLocalPath;
                var pending = Call(w, "AdvanceAsync", 1, false);
                Assert(!pending.IsCompleted, "fixture did not hold manual Next through the refill");
                if (scenario != "still-playing")
                {
                    f.Player.Seek(f.Player.Duration - TimeSpan.FromSeconds(.25));
                    await Until(() => f.Feedback.Contains(("trackFinished", last.Id)));
                }
                if (scenario == "new-play")
                {
                    SaInvoke(w, "CommitSeek", 1d);
                    await w.HandleDesktopMediaCommandAsync(DesktopMediaAction.Play);
                    Assert(f.Player.IsPlaying, "new Play did not resume the sought track");
                }
                f.Release.TrySetResult();
                await pending.WaitAsync(TimeSpan.FromSeconds(5));
                await Until(() => !SaField<bool>(w, "_advancingWave"));
                Assert(ReferenceEquals(queue.Current, last) && queue.CurrentOccurrenceToken == occurrence,
                    "exhausted refill changed the selected occurrence");
                var events = f.Feedback.Where(item => item.Track == last.Id).Select(item => item.Type).ToArray();
                if (scenario is "still-playing" or "new-play")
                {
                    Assert(f.Player.IsPlaying && f.Player.SourceLocalPath == source && !events.Contains("skip"),
                        "exhausted refill stopped a playing source");
                    continue;
                }
                Assert(!f.Player.HasSource && !last.IsPlaying
                    && SaField<Diagnostics>(w, "_diagnostics").Stage == "library_ready"
                    && events.SequenceEqual(new[] { "trackStarted", "trackFinished" }),
                    "exhausted refill left an ended source open or repeated its feedback");
                await w.HandleDesktopMediaCommandAsync(DesktopMediaAction.Play);
                await Until(() => f.Feedback.Count(item => item == ("trackStarted", last.Id)) == 2);
                Assert(f.Player.IsPlaying && f.Player.HasSource && f.Player.Position.TotalSeconds < 3
                    && queue.CurrentOccurrenceToken == occurrence, "ordinary Play could not restart the selected track");
            }
        });
        await Run("TrackWave015: undo after the wave window trims a row preserves original feedback and continuation", async () =>
        {
            using var f = new TwFixture("undo-after-trim") { MixedMoreSize = 300 }; var w = f.Window;
            await Call(w, "OpenTrackWaveAsync", TwSeed("1234", "Волна", true)); await f.Ready("1234");
            var queue = SaField<PlaybackQueue>(w, "_playbackQueue"); var removed = queue.Items[1]; var occurrence = queue.OccurrenceTokenAt(1);
            queue.RemoveMany([1]);
            await Call(w, "ExtendWaveQueueAsync");
            Assert(!SaField<List<TrackRow>>(w, "_waveTracks").Contains(removed) && queue.CanUndoRemoval,
                "fixture did not trim the removed row from the live wave window");
            Assert(queue.UndoRemoval() && queue.OccurrenceTokenAt(1) == occurrence && ReferenceEquals(queue.Items[1], removed),
                "Undo changed the original queue occurrence");
            await w.PlayTrackAsync(removed);
            await Until(() => f.FeedbackSources.Contains(("tw-1", "first-1", "trackStarted", removed.Id)));
            f.MixedMoreSize = 0;
            await Call(w, "ExtendWaveQueueAsync");
            Assert(ReferenceEquals(queue.Current, removed) && f.Player.IsPlaying && f.MoreSessions.SequenceEqual(new[] { "tw-1", "tw-1" }),
                "restored Wave track lost access to its station");
        });
        await Run("TrackWave015: ordinary enqueue and Play Next preserve Wave provenance on new occurrences", async () =>
        {
            using var f = new TwFixture("enqueue-provenance"); var w = f.Window;
            f.Player.SetCrossfade(0);
            await Call(w, "OpenTrackWaveAsync", TwSeed("1234", "Волна", true)); await f.Ready("1234");
            var queue = SaField<PlaybackQueue>(w, "_playbackQueue"); var original = queue.Items[1]; var originalToken = queue.OccurrenceTokenAt(1);
            SaInvoke(w, "ShowQueueCore", false);
            Control<ListView>(w, "TracksList").SelectedItem = original;
            SaInvoke(w, "AddSelectedToQueue", false);
            var tail = queue.Items[^1]; var tailToken = queue.OccurrenceTokenAt(queue.Items.Count - 1);
            SaInvoke(w, "EnqueueActionTrack", original, true);
            var priority = queue.Items[1]; var priorityToken = queue.OccurrenceTokenAt(1);
            Assert(!ReferenceEquals(original, tail) && !ReferenceEquals(original, priority) && !ReferenceEquals(tail, priority)
                && originalToken != tailToken && originalToken != priorityToken && tailToken != priorityToken,
                "queue actions did not create independent exact occurrences");

            await Call(w, "AdvanceAsync", 1, false);
            Assert(queue.CurrentOccurrenceToken == priorityToken && ReferenceEquals(queue.Current, priority), "Play Next did not use its own occurrence");
            await Until(() => f.FeedbackSources.Contains(("tw-1", "first-1", "trackStarted", original.Id)));
            await w.PlayTrackAsync(tail);
            Assert(queue.CurrentOccurrenceToken == tailToken, "tail copy lost its occurrence on playback");
            await Until(() => f.FeedbackSources.Count(item => item == ("tw-1", "first-1", "trackStarted", original.Id)) == 2
                && f.MoreSessions.Count > 0);
            Assert(f.Seeds.Count == 1 && f.MoreSessions.All(session => session == "tw-1"), "wave copy did not continue its live station");

            var ordinary = TwSeed("9876", "Обычный трек", true);
            SaInvoke(w, "EnqueueActionTrack", ordinary, true);
            await Call(w, "AdvanceAsync", 1, false);
            Assert(queue.Current?.Id == ordinary.Id && !ReferenceEquals(ordinary, queue.Current)
                && SaField<object?>(w, "_waveListening") is null, "an ordinary copied track gained wave provenance");
        });
        await Run("TrackWave015: pending natural continuation respects pause seek and both sleep modes", async () =>
        {
            foreach (var action in new[] { "continue", "minute", "after-track", "pause", "seek" })
            {
                using var f = new TwFixture("end-pending-" + action); var w = f.Window;
                await Call(w, "OpenTrackWaveAsync", TwSeed("1234", "Волна", true)); await f.Ready("1234");
                var queue = SaField<PlaybackQueue>(w, "_playbackQueue"); var last = queue.Items[^1];
                f.HoldMore = true;
                await w.PlayTrackAsync(last);
                await f.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                if (action == "minute") w.SetSleepTimer(SleepTimerMode.Minutes15);
                f.Player.Seek(f.Player.Duration - TimeSpan.FromSeconds(.25));
                await Until(() => !f.Player.IsPlaying);
                Assert(f.Player.HasSource && SaField<bool>(w, "_advancingPlayback")
                    && Equals(Control<Button>(w, "PlayPauseButton").Content, "\uE769"), "native end abandoned the pending continuation");
                if (action == "minute") SaInvoke(w, "ApplySleepTimerElapsed", TimeSpan.FromMinutes(15));
                if (action == "after-track")
                {
                    w.SetSleepTimer(SleepTimerMode.AfterCurrentTrack);
                    Assert(!SaField<SleepTimerState>(w, "_sleepTimer").Active, "after-track timer waited on a completed track");
                }
                if (action == "pause")
                {
                    await w.HandleDesktopMediaCommandAsync(DesktopMediaAction.Pause);
                    await w.HandleDesktopMediaCommandAsync(DesktopMediaAction.Pause);
                }
                if (action == "seek")
                {
                    SaInvoke(w, "CommitSeek", 1d);
                    Assert(Math.Abs(Control<Slider>(w, "SeekSlider").Value - 1) < .001,
                        "the paused player did not acknowledge the requested seek in the UI");
                }
                if (action != "continue")
                    await Until(() => !SaField<bool>(w, "_advancingPlayback")
                        && Equals(Control<Button>(w, "PlayPauseButton").Content, "\uE768"));
                f.Release.TrySetResult();
                await Until(() => !SaField<bool>(w, "_advancingPlayback") && !SaField<bool>(w, "_advancingWave"));
                Assert(f.MoreRequests == 1, "native end duplicated an in-flight request");
                if (action == "continue") Assert(f.Player.IsPlaying && queue.Current?.Id == "901", "pending continuation did not continue playback");
                else Assert(!f.Player.IsPlaying && ReferenceEquals(queue.Current, last), "late continuation overrode " + action);
                if (action == "seek")
                {
                    // WPF applies Position asynchronously after the network waiter can complete.
                    await Until(() => f.Player.Position.TotalSeconds is >= .9 and <= 1.1);
                    Assert(f.Player.HasSource && !f.Player.IsPlaying && ReferenceEquals(queue.Current, last),
                        "the late continuation changed the paused source after seek");
                    await w.HandleDesktopMediaCommandAsync(DesktopMediaAction.Play);
                    await w.HandleDesktopMediaCommandAsync(DesktopMediaAction.Play);
                    await Until(() => f.Feedback.Count(feedback => feedback == ("trackStarted", last.Id)) == 2);
                    Assert(f.Player.IsPlaying && f.FeedbackSources.Where(x => x.Track == last.Id)
                        .Select(x => (x.Session, x.Batch, x.Type)).SequenceEqual(new[]
                        { ("tw-1", "first-1", "trackStarted"), ("tw-1", "first-1", "trackFinished"), ("tw-1", "first-1", "trackStarted") }),
                        "resuming a completed track did not begin exactly one new cycle for its original batch");
                }
            }
        });
        await Run("TrackWave015: decoder buffering pauses listening time until playback resumes", async () =>
        {
            using var f = new TwFixture("buffer-clock"); var w = f.Window;
            await Call(w, "OpenTrackWaveAsync", TwSeed("1234", "Волна", true)); await f.Ready("1234");
            var listening = SaField<object>(w, "_waveListening");
            var clock = (System.Diagnostics.Stopwatch)listening.GetType().GetProperty("Clock")!.GetValue(listening)!;
            var slot = typeof(NativePlayer).GetField("_current", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(f.Player)!;
            var buffering = slot.GetType().GetMethod("SetWpfBuffering", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            Assert(clock.IsRunning, "ready playing slot had no listening time");
            buffering.Invoke(slot, [true]);
            var elapsed = clock.Elapsed;
            await Task.Delay(120);
            Assert(!clock.IsRunning && clock.Elapsed == elapsed && f.Player.IsPlaying, "buffering was counted or changed explicit Play state");
            buffering.Invoke(slot, [false]);
            Assert(clock.IsRunning, "ready decoder did not restart its listening clock");
            await Call(w, "TogglePlaybackAsync");
            buffering.Invoke(slot, [false]);
            Assert(!clock.IsRunning, "late buffering callback restarted the paused listening clock");
        });
        await Run("TrackWave015: a failed background continuation does not retry until a new playback action", async () =>
        {
            using var f = new TwFixture("prefetch-failure") { MoreFailures = 2 }; var w = f.Window;
            await Call(w, "OpenTrackWaveAsync", TwSeed("1234", "Волна", true)); await f.Ready("1234");
            var queue = SaField<PlaybackQueue>(w, "_playbackQueue");
            await w.PlayTrackAsync(queue.Items[^1]);
            await Until(() => f.MoreRequests > 0 && !SaField<bool>(w, "_advancingWave"));
            SaInvoke(w, "UpdateControls");
            Assert(f.MoreRequests == 1 && f.Player.IsPlaying, "failed background continuation retried without a new playback action");
        });
        await Run("TrackWave015: unavailable continuations are bounded and do not occupy playback queue capacity", async () =>
        {
            using var f = new TwFixture("availability") { UnavailableMore = 2, MixedMoreSize = 300 }; var w = f.Window;
            await Call(w, "OpenTrackWaveAsync", TwSeed("1234", "Волна", true)); await f.Ready("1234");
            var queue = SaField<PlaybackQueue>(w, "_playbackQueue");
            await Call(w, "ExtendWaveQueueAsync");
            Assert(f.MoreRequests == 3 && queue.Items.Count == 154 && queue.Items.All(row => row.CanPlay),
                "refill did not skip unavailable batches or left unavailable entries in playback");
            while (queue.CanNext) queue.Next(true);
            UlSet(w, "_current", queue.Current);
            await Call(w, "ExtendWaveQueueAsync");
            Assert(f.MoreRequests == 4 && queue.Items.Count <= 300 && queue.CanNext, "long mixed wave exhausted its playback capacity");

            var count = queue.Items.Count;
            f.UnavailableMore = 10;
            await Call(w, "ExtendWaveQueueAsync");
            Assert(f.MoreRequests == 7 && queue.Items.Count == count && queue.CanNext, "unavailable-only tail retried without limit or replaced playable items");
        });
        await Run("TrackWave015: after-track timer stays armed at the position boundary and reports natural finish", async () =>
        {
            using var f = new TwFixture("sleep-finish"); var w = f.Window;
            await Call(w, "OpenTrackWaveAsync", TwSeed("1234", "Волна", true)); await f.Ready("1234");
            var queue = SaField<PlaybackQueue>(w, "_playbackQueue"); var first = queue.Current;
            w.SetSleepTimer(SleepTimerMode.AfterCurrentTrack);
            f.Player.Seek(f.Player.Duration);
            SaInvoke(w, "ApplySleepTimerElapsed", TimeSpan.Zero);
            Assert(SaField<SleepTimerState>(w, "_sleepTimer").AfterTrack, "position boundary disarmed sleep before native end");
            await Until(() => !f.Player.IsPlaying && f.Feedback.Contains(("trackFinished", first?.Id)));
            Assert(ReferenceEquals(queue.Current, first) && !SaField<SleepTimerState>(w, "_sleepTimer").Active
                && f.Feedback.Count(entry => entry == ("trackFinished", first?.Id)) == 1 && !f.Feedback.Any(entry => entry.Type == "skip"),
                "after-track timer advanced or misreported the finished track");
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
    private static string TwBatch(string session, string batch, int first, int count = 4, Func<int, bool>? available = null, int longTrack = 0) => JsonSerializer.Serialize(new { result = new { radioSessionId = session, batchId = batch,
        sequence = Enumerable.Range(first, count).Select(id => new { type = "track", track = new { id = id.ToString(), title = "Рекомендация Яндекса " + id, available = available?.Invoke(id) ?? true, durationMs = id == longTrack ? 240000 : 30000,
            artists = new[] { new { name = "Тестовый исполнитель" } }, albums = new[] { new { id = "91", title = "Рекомендованный альбом" } } } }).ToArray() } });

    private sealed class TwFixture : IDisposable
    {
        internal MainWindow Window { get; }
        internal NativePlayer Player { get; }
        internal List<string> Seeds { get; } = [];
        internal List<(string Type, string? Track)> Feedback { get; } = [];
        internal List<(string Session, string Batch, string Type, string? Track)> FeedbackSources { get; } = [];
        internal List<string> MoreSessions { get; } = [];
        internal bool HoldNext, HoldMore, FailNext, RenewMore;
        internal Func<int, bool>? MoreAvailability { get; init; }
        internal Func<int, bool>? StartAvailability { get; init; }
        internal int LongStartTrack { get; init; }
        internal byte[]? LongStartAudio { get; set; }
        internal bool HoldLongStart { get; set; }
        internal int MoreRequests, MoreFailures, UnavailableStart, UnavailableMore, MixedMoreSize;
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
                var bytes = request.RequestUri.AbsolutePath == "/track-wave-long.mp3" ? LongStartAudio ?? media : media;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
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
                    return Json(TwBatch("tw-" + ordinal, "first-" + ordinal, 800 + ordinal,
                        available: ordinal == UnavailableStart ? static _ => false : StartAvailability, longTrack: LongStartTrack));
                }
                if (path.EndsWith("/tracks", StringComparison.Ordinal) && path.StartsWith("/rotor/session/", StringComparison.Ordinal))
                {
                    MoreRequests++; MoreSessions.Add(path.Split('/')[3]);
                    if (HoldMore) { HoldMore = false; Entered.TrySetResult(); await Release.Task; }
                    if (MoreFailures > 0) { MoreFailures--; return Json("{}", HttpStatusCode.ServiceUnavailable); }
                    if (RenewMore) { RenewMore = false; return Json("{\"result\":{\"unknownSession\":true}}"); }
                    if (UnavailableMore > 0)
                    {
                        UnavailableMore--;
                        return Json(TwBatch(path.Split('/')[3], "unavailable-" + MoreRequests, 2000 + 4 * MoreRequests, available: static _ => false));
                    }
                    if (MixedMoreSize > 0)
                        return Json(TwBatch(path.Split('/')[3], "mixed-" + MoreRequests, 3000 + MixedMoreSize * MoreRequests, MixedMoreSize, static id => id % 2 == 0));
                    return Json(TwBatch(path.Split('/')[3], "more", 901, available: MoreAvailability));
                }
                if (path.EndsWith("/feedback/", StringComparison.Ordinal))
                {
                    using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token)); var feedback = body.RootElement.GetProperty("event");
                    var type = feedback.GetProperty("type").GetString()!; var track = feedback.TryGetProperty("trackId", out var id) ? id.GetString() : null;
                    Feedback.Add((type, track));
                    FeedbackSources.Add((path.Split('/')[3], body.RootElement.GetProperty("batchId").GetString()!, type, track));
                    return Json("{\"result\":{}}");
                }
                if (path == "/get-file-info")
                {
                    var isLong = LongStartTrack > 0 && request.RequestUri.Query.Split('&').Contains("trackId=" + LongStartTrack);
                    if (isLong && HoldLongStart)
                    {
                        HoldLongStart = false;
                        Entered.TrySetResult();
                        await Release.Task.WaitAsync(token);
                    }
                    return Json(JsonSerializer.Serialize(new { result = new { downloadInfo = new
                    {
                        quality = "lossless", codec = "mp3", bitrate = 320, transport = "raw",
                        url = "https://s1.vlx.yandex.net/" + (isLong ? "track-wave-long.mp3" : "track-wave-fixture.mp3")
                    } } }));
                }
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
        internal void IndexAudio(string[] ids)
        {
            var directory = SaField<OfflineLibrary>(Window, "_offline").DirectoryPath;
            Directory.CreateDirectory(directory);
            var rows = ids.Select(id =>
            {
                var path = Path.Combine(directory, id + ".mp3");
                File.Copy(WriteWave($"track-wave-index-{id}.wav", 1), path);
                return new TrackRow { Id = id, RequestId = id + ":91", Title = "Indexed test audio", DurationMs = 30000, LocalPath = path };
            }).ToArray();
            WriteFixtureAudioIndex(directory, rows);
        }
        public void Dispose() { Release.TrySetResult(); Window.Close(); }
    }
}
