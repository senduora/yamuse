using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using YMusic.Native;

internal static partial class Program
{
    private static async Task CheckInfiniteMix060()
    {
        await Run("AlbumWave060: exact album seed, continuation, every renewal and original batch feedback", async () =>
        {
            foreach (var renewal in new[] { "{\"unknownSession\":true}", "{\"terminated\":true}", "{\"sequence\":[]}" })
            {
                var starts = 0; var more = 0;
                var handler = new FakeHandler((request, _) => Task.FromResult(Json(request.RequestUri!.AbsolutePath.EndsWith("/feedback/", StringComparison.Ordinal)
                    ? "{\"result\":{}}" : request.RequestUri.AbsolutePath == "/rotor/session/new"
                    ? TwBatch("album-" + ++starts, "batch-" + starts, starts * 100)
                    : ++more == 1 ? TwBatch("album-1", "continued", 401) : "{\"result\":" + renewal + "}")));
                using var api = new MusicApi(handler);
                var first = await api.StartWaveAsync(FakeSession, default, WaveMode.Album, albumSeedId: "12345");
                var continued = await api.MoreWaveAsync(FakeSession, first.SessionId, default);
                var renewed = await api.MoreWaveAsync(FakeSession, continued.SessionId, default);
                Assert(new[] { first, continued, renewed }.All(batch => batch.Mode == WaveMode.Album && batch.AlbumSeedId == "12345" && batch.TrackSeedId is null)
                    && renewed.SessionId == "album-2", "album station identity was lost on continuation/renewal");
                var calls = handler.Seen.Where(call => call.Uri.AbsolutePath == "/rotor/session/new").ToArray();
                Assert(calls.Length == 2, "album renewal retried unexpectedly");
                foreach (var call in calls)
                {
                    using var body = JsonDocument.Parse(call.Body);
                    Assert(call.Authorization == "OAuth " + FakeSession.AccessToken && body.RootElement.GetProperty("seeds")[0].GetString() == "album:12345"
                        && body.RootElement.GetProperty("includeTracksInResponse").GetBoolean(), "album start used a guessed seed or endpoint");
                }
                using var received = JsonDocument.Parse(calls[1].Body);
                var queue = received.RootElement.GetProperty("queue").EnumerateArray().Select(item => item.GetString()).ToArray();
                Assert(queue.Contains("100:91") && queue.Contains("401:91") && queue.Length <= 20, "album renewal forgot its bounded received queue");
                await api.SendWaveFeedbackAsync(FakeSession, first.SessionId, first.BatchId, "trackFinished", first.Tracks[0].Id, 12, default);
                using var feedback = JsonDocument.Parse(handler.Seen[^1].Body);
                Assert(handler.Seen[^1].Uri.AbsolutePath == "/rotor/session/album-1/feedback/"
                    && feedback.RootElement.GetProperty("batchId").GetString() == first.BatchId, "renewal redirected feedback for the original batch");
            }
        });
        await Run("AlbumWave060: invalid album IDs send no HTTP and reset/cancel cannot revive the station", async () =>
        {
            var handler = new FakeHandler((_, _) => Task.FromResult(Json(TwBatch("invalid", "batch", 801))));
            using (var api = new MusicApi(handler))
            {
                foreach (var invalid in new string?[] { null, "", "123:456", "album:123", " 123", "123/456", "123,456", new('1', 129) })
                    await Throws<ArgumentException>(() => api.StartWaveAsync(FakeSession, default, WaveMode.Album, albumSeedId: invalid));
                Assert(handler.Seen.Count == 0, "invalid album ID escaped into HTTP");
            }
            foreach (var cancel in new[] { false, true })
            {
                var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                using var api = new MusicApi(new FakeHandler(async (_, _) => { entered.TrySetResult(); await release.Task; return Json(TwBatch("stale", "batch", 801)); }));
                using var cts = new CancellationTokenSource();
                var pending = api.StartWaveAsync(FakeSession, cts.Token, WaveMode.Album, albumSeedId: "12345");
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                if (cancel) cts.Cancel(); else api.ResetWaveState();
                release.TrySetResult();
                await Throws<OperationCanceledException>(async () => await pending);
                await Throws<MusicException>(() => api.MoreWaveAsync(FakeSession, "stale", default));
            }
        });
        await Run("AlbumWave060: album action launches its exact station, Back/Forward and New wave retain the album", async () =>
        {
            using var f = new TwFixture("album-action"); var w = f.Window;
            var album = Mix060Album("12345"); Mix060ShowAlbum(w, album);
            var button = Control<Button>(w, "AlbumWaveButton");
            Assert(button.IsVisible && button.IsEnabled, "opened album has no usable wave action");
            foreach (var theme in new[] { "Dark", "Light" })
            {
                ThemeManager.SetMode(theme);
                foreach (var width in new[] { 1050d, 1280d })
                {
                    w.Width = width; w.Height = width == 1050 ? 680 : 860; w.UpdateLayout();
                    await Task.Delay(320); w.UpdateLayout();
                    var toolbar = Control<WrapPanel>(w, "CollectionToolbar");
                    var bounds = button.TransformToAncestor(toolbar).TransformBounds(new Rect(button.RenderSize));
                    var viewport = Control<ListView>(w, "TracksList").ActualHeight;
                    App.Capture(w, Path.Combine(_directory, $"album-wave-060-{theme}-{width}.png"));
                    Assert(bounds.Left >= 0 && bounds.Right <= toolbar.ActualWidth + 1 && bounds.Bottom <= toolbar.ActualHeight + 1
                        && viewport >= 250, $"album wave layout: button={bounds}, toolbar={toolbar.ActualWidth}x{toolbar.ActualHeight}, viewport={viewport}");
                }
            }
            ThemeManager.SetMode("Dark");
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Mix060Ready(f);
            Assert(f.Seeds.Single() == "album:12345" && Control<TextBlock>(w, "WaveHeading").Text == "Волна по альбому"
                && Control<TextBlock>(w, "WaveModeCaption").Text.Contains(album.Title), "album action used a track ID or mislabeled the station");
            foreach (var theme in new[] { "Dark", "Light" })
            {
                ThemeManager.SetMode(theme);
                foreach (var width in new[] { 1050d, 1280d })
                {
                    w.Width = width; w.Height = width == 1050 ? 680 : 860;
                    await Task.Delay(320); w.UpdateLayout();
                    Assert(Control<TextBlock>(w, "WaveHeading").IsVisible && Control<TextBlock>(w, "WaveModeCaption").IsVisible,
                        "album wave title or source caption is hidden");
                    App.Capture(w, Path.Combine(_directory, $"album-wave-panel-060-{theme}-{width}.png"));
                }
            }
            ThemeManager.SetMode("Dark");
            await Call(w, "OpenWaveAsync", WaveMode.Collection);
            SaInvoke(w, "BrowseBack_Click", null!, null!);
            Assert(SaField<WaveMode>(w, "_waveViewMode") == WaveMode.Album && SaField<CatalogItem>(w, "_waveSeedAlbum").Id == album.Id,
                "Back lost the album station");
            SaInvoke(w, "BrowseForward"); SaInvoke(w, "BrowseBack_Click", null!, null!);
            await Call(w, "StartWaveViewAsync", true); await Mix060Ready(f);
            Assert(f.Seeds.SequenceEqual(new[] { "album:12345", "personal:collection", "album:12345" }), "New wave changed the album seed");
            SaInvoke(w, "SetSection", "favorites", "Мне нравится");
            Assert(button.Visibility == Visibility.Collapsed, "album action leaked outside its album");
            Mix060ShowAlbum(w, album); w.SignOut();
            Assert(!button.IsEnabled, "album action retained authority after logout");
        });
        await Run("InfiniteMix060: natural MediaEnded starts a track station and keeps ordered listening feedback", async () =>
        {
            using var f = new TwFixture("mix-natural"); var w = f.Window; var last = TwSeed("1234", "Последний трек", true);
            await w.PlayTrackAsync(last); f.Player.SetCrossfade(0);
            SaInvoke(w, "CommitSeek", 29.65);
            await Until(() => f.Seeds.Count == 1 && SaField<TrackRow>(w, "_current").Id == "801" && f.Player.IsPlaying);
            Assert(f.Seeds.Single() == "track:1234", "natural completion did not start a station from the final track");
            await Until(() => f.Feedback.Count >= 2);
            Assert(f.Feedback[0].Type == "radioStarted" && f.Feedback[1] == ("trackStarted", "801"), "autoplay did not preserve ordered feedback");
            Assert(SaField<PlaybackQueue>(w, "_playbackQueue").Repeat == RepeatMode.Off, "autoplay enabled repeat");
        });
        await Run("InfiniteMix060: album origin survives browsing a different album; playlist uses its last track", async () =>
        {
            using (var f = new TwFixture("mix-album-origin"))
            {
                var w = f.Window; var album = Mix060Album("12345"); Mix060ShowAlbum(w, album);
                Control<ListView>(w, "TracksList").SelectedItem = album.Tracks[^1]; await Call(w, "PlaySelectedAsync");
                Mix060ShowAlbum(w, Mix060Album("67890")); f.Player.Stop();
                await Call(w, "AdvanceAsync", 1, true);
                Assert(f.Seeds.Single() == "album:12345" && SaField<string>(w, "_section") == "album-detail"
                    && SaField<AlbumDetails>(w, "_activeAlbumDetails").Id == "67890", "autoplay used the viewed album or navigated away from it");
            }
            using (var f = new TwFixture("mix-playlist-origin"))
            {
                var w = f.Window; var last = Mix060Album("12345").Tracks[^1];
                var playlist = new PlaylistDetails("12345", "11", "Список", "", 1, [last]);
                SaInvoke(w, "SetSection", "playlist-detail", playlist.Title); SaInvoke(w, "ShowPlaylist", playlist);
                Control<ListView>(w, "TracksList").SelectedItem = last; await Call(w, "PlaySelectedAsync");
                f.Player.Stop(); await Call(w, "AdvanceAsync", 1, true);
                Assert(f.Seeds.Single() == "track:" + last.Id, "playlist completion guessed an album from track metadata");
                Assert(Control<Button>(w, "AlbumWaveButton").Visibility == Visibility.Collapsed, "playlist showed the album-only action");
            }
        });
        await Run("InfiniteMix060: repeat modes, after-track sleep, manual Next, missing login and invalid seed do not start autoplay", async () =>
        {
            foreach (var mode in new[] { "all", "one", "sleep", "manual", "offline", "invalid" })
            {
                using var f = new TwFixture("mix-no-start-" + mode); var w = f.Window;
                var last = TwSeed(mode == "invalid" ? "bad:id" : "1234", "Последний трек", true);
                await w.PlayTrackAsync(last); var queue = SaField<PlaybackQueue>(w, "_playbackQueue");
                if (mode == "all") queue.Repeat = RepeatMode.All;
                if (mode == "one") queue.Repeat = RepeatMode.One;
                if (mode == "offline") UlSet(w, "_session", null);
                if (mode == "sleep")
                {
                    SaInvoke(w, "SetSleepTimer", SleepTimerMode.AfterCurrentTrack); f.Player.Stop();
                    Assert((bool)Pc0317Invoke(w, "HandlePlaybackOptionsTrackEnded")!, "after-track timer did not consume natural completion");
                }
                else { f.Player.Stop(); await Call(w, "AdvanceAsync", 1, mode != "manual"); }
                Assert(f.Seeds.Count == 0 && ReferenceEquals(queue.Current, last), "autoplay overrode " + mode);
                if (mode is "all" or "one") Assert(f.Player.IsPlaying, "repeat no longer restarts its track");
                if (mode == "sleep") Assert(!f.Player.IsPlaying, "after-track timer allowed music to resume");
            }
        });
        await Run("InfiniteMix060: ordinary next, unavailable tail and exhausted shuffle choose the actual final playable track", async () =>
        {
            using var f = new TwFixture("mix-shuffle"); var w = f.Window;
            var a = TwSeed("1111", "Первый", true); var b = TwSeed("2222", "Второй", true); var unavailable = TwSeed("3333", "Недоступный", false);
            var queue = SaField<PlaybackQueue>(w, "_playbackQueue"); queue.SetItems([a, b, unavailable], a);
            await w.PlayTrackAsync(a); f.Player.Stop(); await Call(w, "AdvanceAsync", 1, true);
            Assert(ReferenceEquals(queue.Current, b) && f.Seeds.Count == 0, "ordinary next started a station too early");
            queue.Shuffle = true;
            while (queue.CanNext) queue.Next(true);
            await w.PlayTrackAsync(queue.Current!); var final = queue.Current!.Id; f.Player.Stop();
            await Call(w, "AdvanceAsync", 1, true);
            Assert(f.Seeds.Single() == "track:" + final, "shuffle completion used physical list order or an unavailable row");
        });
        await Run("InfiniteMix060: late autoplay cannot override queue, pause, seek, repeat, sleep, account or explicit wave", async () =>
        {
            foreach (var action in new[] { "queue", "pause", "seek", "repeat", "sleep", "logout", "wave" })
            {
                using var f = new TwFixture("mix-race-" + action); var w = f.Window;
                var last = TwSeed("1234", "Последний трек", true); await w.PlayTrackAsync(last); f.Player.Stop();
                f.HoldNext = true; var pending = Call(w, "AdvanceAsync", 1, true);
                await f.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                var queue = SaField<PlaybackQueue>(w, "_playbackQueue"); var manual = TwSeed("9999", "Моя очередь", true); Task? replacement = null;
                if (action == "queue") queue.SetItems([manual]);
                if (action == "pause") await Call(w, "TogglePlaybackAsync");
                if (action == "seek") SaInvoke(w, "CommitSeek", 1d);
                if (action == "repeat") queue.Repeat = RepeatMode.All;
                if (action == "sleep") SaInvoke(w, "FinishSleepTimer");
                if (action == "logout") w.SignOut();
                if (action == "wave") replacement = Call(w, "OpenTrackWaveAsync", TwSeed("5678", "Ручная волна", true));
                f.Release.TrySetResult(); await pending; if (replacement is not null) await replacement;
                Assert(f.Seeds.Count == (action == "wave" ? 2 : 1), "stale autoplay retried after " + action);
                if (action == "queue") Assert(ReferenceEquals(queue.Current, manual), "autoplay replaced the new queue");
                else if (action == "logout") Assert(SaField<TrackRow?>(w, "_current") is null && !f.Player.HasSource, "autoplay revived logout");
                else if (action == "wave") Assert(SaField<RadioBatch>(w, "_waveBatch").TrackSeedId == "5678", "autoplay replaced the explicit station");
                else Assert(ReferenceEquals(queue.Current, last) && ReferenceEquals(SaField<TrackRow>(w, "_current"), last) && !f.Player.IsPlaying,
                    "autoplay ignored " + action);
            }
        });
        await Run("InfiniteMix060: autoplay failure stops safely, does not retry and preserves a newer Settings view", async () =>
        {
            using var f = new TwFixture("mix-failure"); var w = f.Window; var last = TwSeed("1234", "Последний трек", true);
            await w.PlayTrackAsync(last); f.Player.Stop(); f.HoldNext = true; f.FailNext = true;
            var pending = Call(w, "AdvanceAsync", 1, true); await f.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            SaInvoke(w, "Settings_Click", null!, null!); Control<Slider>(w, "CrossfadeSetting").Value = 8;
            f.Release.TrySetResult(); await pending;
            SaInvoke(w, "UpdateControls"); await Task.Delay(120);
            Assert(f.Seeds.Count == 1 && !f.Player.IsPlaying && !f.Player.HasSource && ReferenceEquals(SaField<TrackRow>(w, "_current"), last)
                && SaField<bool>(w, "_settingsOpen") && f.Player.CrossfadeSeconds == 8, "failed autoplay looped, kept playing or changed Settings");
        });
        await Run("InfiniteMix060: source album metadata roundtrips without autoplay; legacy and malformed snapshots stay guarded", () =>
        {
            var directory = PersistenceDirectory("mix060"); var store = new PlaybackStateStore(directory);
            var queue = new PlaybackQueue(); var album = Mix060Album("12345"); queue.SetItems(album.Tracks, album.Tracks[^1]);
            var source = new PlaybackAlbumState(album.Id, album.Title, album.Artists);
            store.Save(PlaybackStateStore.Capture("12345", queue, 12, source));
            Assert(store.Load("12345")?.SourceAlbum == source && store.Load("67890") is null, "album source lost its account boundary or metadata");
            var requests = 0;
            using var api = new MusicApi(new FakeHandler((_, _) => { requests++; throw new InvalidOperationException("restore requested music API"); }));
            using var offline = new OfflineLibrary(Path.Combine(directory, "offline"));
            var w = new MainWindow(api: api, store: new SessionStore(Path.Combine(directory, "session")), restore: true, diagnosticsDirectory: directory,
                offline: offline, preferences: new PreferencesStore(Path.Combine(directory, "preferences")));
            try
            {
                UlSet(w, "_account", new Account("12345", "Fixture", true)); SaInvoke(w, "RestorePlaybackState", "12345");
                Assert((PlaybackAlbumState?)Pc0317Invoke(w, "PlaybackSourceAlbum") == source && !SaField<NativePlayer>(w, "_player").HasSource && requests == 0,
                    "restoring the album source opened audio or lost exact origin");
                SaField<PlaybackQueue>(w, "_playbackQueue").SetItems([TwSeed("9999", "Новая очередь", true)]);
                Assert(Pc0317Invoke(w, "PlaybackSourceAlbum") is null, "restored source leaked into a replacement queue");
                Mix060ShowAlbum(w, album);
                SaField<PlaybackQueue>(w, "_playbackQueue").SetItems(album.Tracks, album.Tracks[^1]);
                UlSet(w, "_current", album.Tracks[^1]); SaInvoke(w, "CapturePlaybackContext"); SaInvoke(w, "SavePlaybackState");
                Assert(store.Load("12345")?.SourceAlbum == source, "window save omitted the actual album playback source");
            }
            finally { w.Close(); }
            store.Save(PlaybackStateStore.Capture("12345", queue, 12));
            Assert(store.Load("12345")?.SourceAlbum is null, "legacy snapshot now requires album metadata");
            var path = Path.Combine(directory, PlaybackStateStore.FileName);
            var malformed = JsonNode.Parse(File.ReadAllText(path))!;
            malformed["sourceAlbum"] = JsonSerializer.SerializeToNode(new { id = "album:12345", title = "Альбом", artist = "Исполнитель" });
            File.WriteAllText(path, malformed.ToJsonString());
            Assert(store.Load("12345") is null && store.LastReadStatus == PlaybackStateReadStatus.Invalid, "malformed source album escaped validation");
            return Task.CompletedTask;
        });
    }

    private static AlbumDetails Mix060Album(string id) => new(id, "Точный исходный альбом " + id, "Тестовый исполнитель",
        [new TrackRow { Id = "1111", RequestId = "1111:777", Title = "Первый трек", Artist = "Тестовый исполнитель", Album = "Другой выпуск",
            AlbumId = "777", Available = true, DurationMs = 30000 },
         new TrackRow { Id = "2222", RequestId = "2222:777", Title = "Последний трек", Artist = "Тестовый исполнитель", Album = "Другой выпуск",
            AlbumId = "777", Available = true, DurationMs = 30000 }]);

    private static void Mix060ShowAlbum(MainWindow w, AlbumDetails album)
    {
        // TwFixture injects fake identity without ActivateAsync. Match the
        // signed-in layout too, rather than measuring its initial login panel.
        Control<FrameworkElement>(w, "LoginPanel").Visibility = Visibility.Collapsed;
        Control<FrameworkElement>(w, "CodePanel").Visibility = Visibility.Collapsed;
        Control<TextBlock>(w, "AccountText").Text = "Тестовый аккаунт";
        Control<Button>(w, "LoginButton").Visibility = Visibility.Collapsed;
        Control<Button>(w, "LogoutButton").Visibility = Visibility.Visible;
        SaInvoke(w, "SetSection", "album-detail", album.Title); SaInvoke(w, "ShowRows", (object)album.Tracks);
        Pc0317Invoke(w, "ShowCollectionHero", album, null);
    }
    private static Task Mix060Ready(TwFixture f) => Until(() => SaField<RadioBatch?>(f.Window, "_waveBatch")?.Mode == WaveMode.Album
        && !SaField<bool>(f.Window, "_waveStarting") && !SaField<bool>(f.Window, "_loadingStream") && f.Player.HasSource);
}
