using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using YMusic.Native;

internal static partial class Program
{
    private static async Task CheckQualityOffline017()
    {
        await Run("Quality offline 017: codec-aware tier thresholds and improvement ordering", () =>
        {
            Assert(AudioQualityPolicy.IsSufficient("flac", 0, AudioQuality.Excellent)
                && AudioQualityPolicy.IsSufficient("flac-mp4", 0, AudioQuality.Excellent)
                && AudioQualityPolicy.IsSufficient("aac-mp4", 256, AudioQuality.Excellent)
                && AudioQualityPolicy.IsSufficient("mp3", 320, AudioQuality.Excellent), "excellent renditions rejected");
            Assert(!AudioQualityPolicy.IsSufficient("mp3", 192, AudioQuality.Excellent)
                && !AudioQualityPolicy.IsSufficient("aac-mp4", 192, AudioQuality.Excellent)
                && AudioQualityPolicy.IsSufficient("mp3", 192, AudioQuality.Optimal)
                && !AudioQualityPolicy.IsSufficient("he-aac-mp4", 64, AudioQuality.Optimal)
                && AudioQualityPolicy.IsSufficient("he-aac-mp4", 64, AudioQuality.Economical), "lossy tier thresholds differ");
            Assert(AudioQualityPolicy.IsBetter("flac", 0, "mp3", 320)
                && AudioQualityPolicy.IsBetter("aac-mp4", 256, "mp3", 192)
                && AudioQualityPolicy.IsBetter("mp3", 256, "mp3", 192)
                && !AudioQualityPolicy.IsBetter("mp3", 320, "aac-mp4", 256)
                && !AudioQualityPolicy.IsBetter("mp3", 192, "mp3", 192)
                && !AudioQualityPolicy.IsBetter("mp3", 320, "flac", 0), "improvement compares codec bitrates as interchangeable");
            return Task.CompletedTask;
        });

        await Run("Quality offline 017: lower local playback probes better online without replacing saved audio", async () =>
        {
            using var f = new QualityOffline017Fixture("play-upgrade");
            var row = f.Row("track"); var saved = await f.Save(row, 192); var before = f.Hash(saved);
            await f.Window.PlayTrackAsync(row);
            Assert(f.Player.IsPlaying && f.ModernRequests == 1 && f.PlaybackRequests == 1
                && f.Player.SourceLocalPath != f.Library.ResolvePath(saved.FileName), "better rendition did not replace playback source");
            Assert(Control<TextBlock>(f.Window, "QualityText").Text == "MP3 · 320 кбит/с"
                && f.Library.Read().Count == 1 && f.Hash(saved) == before && f.LibraryRequests == 1,
                "online playback relabelled or rewrote the saved audio");
            Assert(ReferenceEquals(PreloadGet<Session>(f.Window, "_session"), FakeSession) && !Directory.Exists(f.StorePath), "optional upgrade changed credentials");
        });

        await Run("Quality offline 017: server downgrade or metadata failure reuses valid local bytes", async () =>
        {
            foreach (var failure in new[] { false, true })
            {
                using var f = new QualityOffline017Fixture("play-reuse-" + failure) { ServerBitrate = 192, FailMetadata = failure };
                var row = f.Row("track"); var saved = await f.Save(row, 192); var before = f.Hash(saved);
                await f.Window.PlayTrackAsync(row);
                Assert(f.Player.IsPlaying && f.ModernRequests == 1 && f.PlaybackRequests == 0
                    && f.Player.SourceLocalPath == f.Library.ResolvePath(saved.FileName)
                    && Control<TextBlock>(f.Window, "QualityText").Text == "Офлайн · MP3 · 192 кбит/с", "downgrade/error did not keep local playback");
                Assert(f.Hash(saved) == before && f.LibraryRequests == 1 && f.Library.Read().Count == 1
                    && ReferenceEquals(PreloadGet<Session>(f.Window, "_session"), FakeSession), "failed optional probe changed offline audio/session");
            }
        });

        await Run("Quality offline 017: no login or expired login opens local without authentication traffic", async () =>
        {
            foreach (var expired in new[] { false, true })
            {
                using var f = new QualityOffline017Fixture("offline-" + expired, signedIn: false);
                var session = expired ? FakeSession with { ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(-5) } : null;
                typeof(MainWindow).GetField("_session", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(f.Window, session);
                var row = f.Row("track"); var saved = await f.Save(row, 192);
                await f.Window.PlayTrackAsync(row);
                Assert(f.Player.IsPlaying && f.ApiRequests == 0 && f.PlaybackRequests == 0
                    && f.Player.SourceLocalPath == f.Library.ResolvePath(saved.FileName), "offline playback required login/refresh");
                Assert(ReferenceEquals(PreloadGet<Session?>(f.Window, "_session"), session) && !Directory.Exists(f.StorePath), "offline playback changed authentication state");
            }
        });

        await Run("Quality offline 017: a changed corrupt index cannot block a previously resolved local file", async () =>
        {
            using var f = new QualityOffline017Fixture("changed-index");
            var row = f.Row("track"); var saved = await f.Save(row, 192); var before = f.Hash(saved);
            PreloadInvoke(f.Window, "ApplyOfflineStates", (object)new[] { row });
            var index = Path.Combine(f.Library.DirectoryPath, "library.index.json");
            const string corrupt = "{fixture-corrupt-index"; File.WriteAllText(index, corrupt);
            await f.Window.PlayTrackAsync(row);
            Assert(f.Player.IsPlaying && f.Player.SourceLocalPath == f.Library.ResolvePath(saved.FileName)
                && f.ApiRequests == 0 && f.PlaybackRequests == 0, "optional index lookup blocked valid local playback");
            Assert(File.ReadAllText(index) == corrupt && f.Hash(saved) == before && f.LibraryRequests == 1, "corrupt index fallback changed local data");
        });

        await Run("Quality offline 017: optional blocked metadata expires promptly and opens local without refresh or CDN", async () =>
        {
            using var f = new QualityOffline017Fixture("probe-deadline") { BlockMetadata = true };
            var row = f.Row("track"); var saved = await f.Save(row, 192);
            var clock = System.Diagnostics.Stopwatch.StartNew(); await f.Window.PlayTrackAsync(row); clock.Stop();
            Assert(clock.ElapsedMilliseconds < 2500 && f.Player.IsPlaying
                && f.Player.SourceLocalPath == f.Library.ResolvePath(saved.FileName), "optional metadata deadline delayed or blocked offline playback");
            Assert(f.ApiRequests == 1 && f.ModernRequests == 1 && f.PlaybackRequests == 0
                && ReferenceEquals(PreloadGet<Session>(f.Window, "_session"), FakeSession) && !Directory.Exists(f.StorePath), "deadline fallback refreshed credentials or downloaded audio");
        });

        await Run("Quality offline 017: downloads skip sufficient cache and equal downgrade but save actual improvement", async () =>
        {
            using var f = new QualityOffline017Fixture("downloads");
            var row = f.Row("track"); var saved = await f.Save(row, 192); var before = f.Hash(saved);
            Control<ComboBox>(f.Window, "QualityCombo").SelectedIndex = 1;
            await f.Download(row);
            Assert(f.ApiRequests == 0 && f.LibraryRequests == 1 && f.Queue.Entries.Single().State == DownloadEntryState.Completed, "optimal cache was downloaded again");
            Control<ComboBox>(f.Window, "QualityCombo").SelectedIndex = 0; f.ServerBitrate = 192;
            await f.Download(row);
            Assert(f.ModernRequests == 1 && f.LibraryRequests == 1 && f.Library.Read().Count == 1
                && f.Queue.Entries.Single().State == DownloadEntryState.Completed, "same server quality was downloaded again");
            f.ServerBitrate = 320; await f.Download(row);
            Assert(f.ModernRequests == 2 && f.LibraryRequests == 2 && f.Library.Read().Count == 2
                && f.Library.Find(row)!.Bitrate == 320 && f.Hash(saved) == before, "upgrade did not retain old audio and save better variant");
            Assert(f.PlaybackRequests == 0 && !f.Player.HasSource, "download started playback");
        });

        await Run("Quality offline 017: preload upgrades once and quality changes return to sufficient local source", async () =>
        {
            using var f = new QualityOffline017Fixture("preload");
            var first = f.Local("current"); var next = f.Row("next"); var saved = await f.Save(next, 192);
            f.PlaybackQueue.SetItems([first, next], first); await f.Window.PlayTrackAsync(first); await PreloadAwaitPrepared(f.Window);
            Assert(f.ModernRequests == 1 && f.PlaybackRequests == 1 && f.Player.PreparedLocalPath != f.Library.ResolvePath(saved.FileName), "low cached next blocked online preparation");
            for (var i = 0; i < 3; i++) PreloadInvoke(f.Window, "UpdateControls");
            Assert(f.ModernRequests == 1, "same prepared identity repeatedly resolved quality");
            Control<ComboBox>(f.Window, "QualityCombo").SelectedIndex = 1; await PreloadAwaitPrepared(f.Window);
            Assert(f.Player.PreparedLocalPath == f.Library.ResolvePath(saved.FileName) && f.ModernRequests == 1, "optimal identity did not reuse sufficient cache");
            f.ServerBitrate = 192; Control<ComboBox>(f.Window, "QualityCombo").SelectedIndex = 0; await PreloadAwaitPrepared(f.Window);
            var resolves = f.ModernRequests;
            for (var i = 0; i < 3; i++) PreloadInvoke(f.Window, "UpdateControls");
            Assert(resolves == 2 && f.ModernRequests == resolves && f.PlaybackRequests == 1
                && f.Player.PreparedLocalPath == f.Library.ResolvePath(saved.FileName), "downgraded preparation redownloaded or retried unchanged identity");
            await Call(f.Window, "AdvanceAsync", 1, false);
            Assert(f.ModernRequests == resolves && f.Player.IsPlaying
                && Control<TextBlock>(f.Window, "QualityText").Text == "Офлайн · MP3 · 192 кбит/с", "promotion lost local fallback identity or resolved twice");
        });

        await Run("Quality offline 017: automatic synchronization retains lower cache and downloads only new tracks", async () =>
        {
            using var f = new QualityOffline017Fixture("automatic");
            var cached = f.Row("cached"); var saved = await f.Save(cached, 192); var before = f.Hash(saved);
            await f.DownloadAutomatic(cached);
            Assert(f.ApiRequests == 0 && f.LibraryRequests == 1 && f.Queue.Entries.Single().State == DownloadEntryState.Completed,
                "automatic sync probed or downloaded an existing lower-quality track");
            var added = f.Row("new"); await f.DownloadAutomatic(cached, added);
            Assert(f.ModernRequests == 1 && f.LibraryRequests == 2 && f.Library.Read().Count == 2
                && f.Library.Find(added) is { Bitrate: 320 } && f.Library.Find(cached) is { Bitrate: 192 }
                && f.Hash(saved) == before && f.Queue.Entries.All(entry => entry.State == DownloadEntryState.Completed),
                "automatic sync upgraded cache or failed to download the new track at selected quality");
        });

        await Run("Quality offline 017: download rechecks local existence after asynchronous metadata resolution", async () =>
        {
            using var f = new QualityOffline017Fixture("download-removed-cache") { ServerBitrate = 192, BlockMetadata = true };
            var row = f.Row("track"); var saved = await f.Save(row, 192);
            var download = f.Download(row);
            await f.MetadataEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var removed = await f.Library.RemoveTrackAsync(row.Id, default);
            Assert(removed.Removed == 1 && !File.Exists(f.Library.ResolvePath(saved.FileName)), "fixture did not remove the pending cached candidate");
            f.MetadataRelease.TrySetResult(); await download.WaitAsync(TimeSpan.FromSeconds(5));
            Assert(f.LibraryRequests == 2 && f.Library.Find(row) is { Bitrate: 192 }
                && f.Queue.Entries.Single().State == DownloadEntryState.Completed, "download completed using a stale missing cached candidate");
        });

        await Run("Quality offline 017: account cancellation cannot resurrect a pending cached-track upgrade", async () =>
        {
            using var f = new QualityOffline017Fixture("cancel");
            var first = f.Local("current"); var next = f.Row("next"); await f.Save(next, 192);
            f.BlockMetadata = true; f.PlaybackQueue.SetItems([first, next], first); await f.Window.PlayTrackAsync(first);
            await f.MetadataEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var pending = PreloadGet<Task>(f.Window, "_preloadTask"); f.Window.SignOut(); f.MetadataRelease.TrySetResult();
            await pending.WaitAsync(TimeSpan.FromSeconds(5));
            Assert(!f.Player.HasPrepared && !f.Player.HasSource && f.PlaybackRequests == 0
                && PreloadGet<Session?>(f.Window, "_session") is null, "account cancellation resurrected prepared audio");
        });
    }

    private sealed class QualityOffline017Fixture : IDisposable
    {
        private readonly byte[] _media;
        internal MainWindow Window { get; }
        internal NativePlayer Player { get; }
        internal OfflineLibrary Library { get; }
        internal string DirectoryPath { get; }
        internal string StorePath => Path.Combine(DirectoryPath, "session");
        internal PlaybackQueue PlaybackQueue => PreloadGet<PlaybackQueue>(Window, "_playbackQueue");
        internal DownloadQueueState Queue => PreloadGet<DownloadQueueState>(Window, "_downloadQueue");
        internal int ServerBitrate { get; set; } = 320;
        internal bool FailMetadata { get; set; }
        internal bool BlockMetadata { get; set; }
        internal int ApiRequests { get; private set; }
        internal int ModernRequests { get; private set; }
        internal int PlaybackRequests { get; private set; }
        internal int LibraryRequests { get; private set; }
        internal TaskCompletionSource MetadataEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource MetadataRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal QualityOffline017Fixture(string name, bool signedIn = true)
        {
            DirectoryPath = Path.Combine(_directory, "quality-offline017-" + name + "-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(DirectoryPath);
            // PCM fixtures exercise source selection only; assertions never claim
            // that a WAV labelled by the fake API proves MP3/FLAC codec support.
            _media = File.ReadAllBytes(WriteWave(Path.GetFileName(DirectoryPath) + ".wav", 40));
            Library = new OfflineLibrary(Path.Combine(DirectoryPath, "Music"), new AudioBuffer(Path.Combine(DirectoryPath, "save-buffer"), new FakeHandler((_, _) =>
            { LibraryRequests++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(_media) }); })));
            Player = new NativePlayer(new AudioBuffer(Path.Combine(DirectoryPath, "player-buffer"), new FakeHandler((request, _) =>
            {
                Assert(request.Headers.Authorization is null && request.RequestUri!.Host == "s1.vlx.yandex.net", "playback sent credentials or left fake CDN");
                PlaybackRequests++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(_media) });
            })));
            Window = new MainWindow(api: new MusicApi(new FakeHandler(Respond)), store: new SessionStore(StorePath), restore: false,
                diagnosticsDirectory: DirectoryPath, offline: Library, player: Player)
            { ShowActivated = false, ShowInTaskbar = false, Left = -16000, Top = 0, WindowStartupLocation = WindowStartupLocation.Manual };
            Window.Show(); Control<Slider>(Window, "VolumeSlider").Value = 0; Player.SetVolume(0);
            if (signedIn) typeof(MainWindow).GetField("_session", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(Window, FakeSession);
        }

        internal TrackRow Row(string id) => new() { Id = id, RequestId = id + ":1", Title = id, DurationMs = 40000, Available = true };
        internal TrackRow Local(string id)
        {
            var path = Path.Combine(DirectoryPath, id + ".wav"); File.WriteAllBytes(path, _media);
            return new() { Id = id, RequestId = id + ":1", Title = id, DurationMs = 40000, Available = true, LocalPath = path, LocalQuality = "Fixture WAV" };
        }
        internal Task<OfflineItem> Save(TrackRow row, int bitrate) => Library.SaveAsync(row,
            new AudioDescriptor(new Uri("https://s1.vlx.yandex.net/saved.mp3"), "mp3", bitrate), default);
        internal string Hash(OfflineItem item) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Library.ResolvePath(item.FileName))));
        internal Task Download(TrackRow row) => Call(Window, "StartDownloadBatchAsync", new[] { row }, "Quality fixture", null!, false);
        internal Task DownloadAutomatic(params TrackRow[] rows) => Call(Window, "StartDownloadBatchAsync", rows, "Automatic quality fixture", null!, true);
        private async Task<HttpResponseMessage> Respond(HttpRequestMessage request, CancellationToken token)
        {
            ApiRequests++;
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/get-file-info")
            {
                ModernRequests++; MetadataEntered.TrySetResult();
                if (BlockMetadata) await MetadataRelease.Task.WaitAsync(token);
                if (FailMetadata) return Json("{}", HttpStatusCode.ServiceUnavailable);
                return Json(JsonSerializer.Serialize(new { result = new { downloadInfo = new { quality = ServerBitrate >= 320 ? "lossless" : "nq", codec = "mp3", bitrate = ServerBitrate, transport = "raw", url = "https://s1.vlx.yandex.net/online.mp3" } } }));
            }
            if (path.EndsWith("/download-info", StringComparison.Ordinal))
                return FailMetadata ? Json("{}", HttpStatusCode.ServiceUnavailable)
                    : Json(JsonSerializer.Serialize(new { result = new[] { new { codec = "mp3", bitrateInKbps = ServerBitrate, downloadInfoUrl = "https://storage.mds.yandex.net/metadata" } } }));
            if (request.RequestUri.Host == "storage.mds.yandex.net") return Xml();
            throw new InvalidOperationException("Unexpected quality fixture request: " + path);
        }
        public void Dispose() { MetadataRelease.TrySetResult(); Window.Close(); }
    }
}
