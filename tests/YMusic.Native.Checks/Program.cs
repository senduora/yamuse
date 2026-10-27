using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using System.Windows.Markup;
using System.Xml.Linq;
using YMusic.Native;

internal static partial class Program
{
    private static readonly List<object> Results = [];
    private static string _directory = "";
    private static int _failed;
    private static string[] _only = [];
    private static bool _realAccount;
    private static readonly Session FakeSession = new("fake-test-token-DO-NOT-USE", "fake-test-refresh", DateTimeOffset.UtcNow.AddHours(1), "test-device");

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--security-offline-child") return SecurityOffline0317Child(args[1..]);
        _directory = Path.GetFullPath(args.FirstOrDefault(a => !a.StartsWith("--")) ?? "check-results");
        _only = args.FirstOrDefault(a => a.StartsWith("--only="))?[7..].Split(',') ?? [];
        _realAccount = args.Contains("--account");
        Directory.CreateDirectory(_directory);
        // A UI fixture that forgets its own artwork cache must still stay
        // inside the test directory and never reach the real CDN or cache.
        Artwork.Cache = new ArtworkCache(Path.Combine(_directory, "shared-artwork"),
            new FakeHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound))));
        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        // All UI checks render their own windows. Keep them off the user's
        // desktop and never activate them while the real preview is in use.
        System.Windows.Window.ShowActivatedProperty.OverrideMetadata(typeof(MainWindow), new FrameworkPropertyMetadata(false));
        EventManager.RegisterClassHandler(typeof(MainWindow), FrameworkElement.LoadedEvent, new RoutedEventHandler((sender, args) =>
        {
            if (sender is not MainWindow window || !ReferenceEquals(args.OriginalSource, window)) return;
            window.ShowInTaskbar = false; window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Left = -16000; window.Top = 0;
        }));
        // Product App queues its real login/startup even without Application.Run.
        // Embed the exact checked styles and load them without constructing Product App.
        using var styles = typeof(Program).Assembly.GetManifestResourceStream("YMusic.Native.Checks.AppStyles.xaml")!;
        var source = XDocument.Load(styles).Root!;
        var ns = XNamespace.Get("http://schemas.microsoft.com/winfx/2006/xaml/presentation");
        var dictionary = new XElement(ns + "ResourceDictionary",
            source.Attributes().Where(a => a.IsNamespaceDeclaration).Select(a => new XAttribute(a)),
            source.Element(ns + "Application.Resources")!.Elements().Select(e => new XElement(e)));
        application.Resources = (ResourceDictionary)XamlReader.Parse(dictionary.ToString(SaveOptions.DisableFormatting)
            .Replace("clr-namespace:YMusic.Native", "clr-namespace:YMusic.Native;assembly=YMusic.Native", StringComparison.Ordinal));
        ThemeManager.SystemLightDetector = () => false;
        ThemeManager.Initialize(application);
        Dispatcher.CurrentDispatcher.BeginInvoke(new Action(async () =>
        {
            try { await CheckAll(args.Contains("--online")); }
            catch (Exception e) { _failed++; Console.WriteLine("HARNESS FAILURE: " + e); }
            finally
            {
                File.WriteAllText(Path.Combine(_directory, "checks.json"), JsonSerializer.Serialize(new { utc = DateTimeOffset.UtcNow, failed = _failed, checks = Results }, new JsonSerializerOptions { WriteIndented = true }));
                Console.WriteLine($"RESULT: {Results.Count - _failed}/{Results.Count} passed");
                Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        }));
        Dispatcher.Run();
        return _failed > 0 ? 1 : 0;
    }

    private static async Task Run(string name, Func<Task> test)
    {
        if (_only.Length > 0 && !_only.Any(prefix => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))) return;
        var clock = Stopwatch.StartNew();
        try { await test(); Results.Add(new { name, status = "PASS", ms = clock.ElapsedMilliseconds }); Console.WriteLine("PASS " + name); }
        catch (Exception e) { _failed++; Results.Add(new { name, status = "FAIL", ms = clock.ElapsedMilliseconds, error = e.GetType().Name, detail = e.Message }); Console.WriteLine("FAIL " + name + ": " + e); }
    }
    private static void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static async Task<T> Throws<T>(Func<Task> task) where T : Exception
    {
        try { await task(); } catch (T e) { return e; }
        throw new InvalidOperationException("Expected " + typeof(T).Name);
    }
    private static HttpResponseMessage Json(string content, HttpStatusCode status = HttpStatusCode.OK) => new(status) { Content = new StringContent(content, Encoding.UTF8, "application/json") };
    private static HttpResponseMessage Xml(string host = "s1.vlx.yandex.net") => new(HttpStatusCode.OK) { Content = new StringContent($"<download-info><host>{host}</host><path>/test/file.mp3</path><ts>1234</ts><s>salt</s></download-info>") };
    private static DeviceCode TestCode(int interval = 1, int seconds = 10) => new("fake-device-code", "TEST-CODE", new Uri("https://oauth.yandex.ru/device"), interval, seconds);
    private const string Profile = """{"result":{"account":{"uid":12345,"displayName":"Fixture Account"},"plus":{"hasPlus":true}}}""";
    private const string TokenResponse = """{"access_token":"fake-test-token-DO-NOT-USE","refresh_token":"fake-test-refresh","expires_in":3600}""";

    private static async Task CheckAll(bool online)
    {
        await CheckQueueEditing();
        await CheckPlaybackOptions();
        await CheckDownloadRestore();
        await CheckHomeDiscovery041();
        await CheckHomeWaveUi041();
        await CheckAmbientLight041();
        await CheckSelectorLabels040();
        await CheckFullscreenRapid040();
        await CheckLyricsScrollRecovery040();
        await CheckPersistenceRecovery040();
        await CheckSeekOwnershipRecovery040();
        await CheckSearchStateRecovery040();
        await CheckLyricsManual040();
        await CheckPlayerAudioLayout040();
        await CheckFullscreenCollapse040();
        await CheckCurrentTrackReveal040();
        await CheckPlaybackKeys040();
        await CheckRapidSkip0317();
        await CheckSecurityNetwork0317();
        await CheckSecurityOffline0317();
        await CheckSecurityStability0317();
        await CheckSecurityPerformance0317();
        await CheckLibraryPlayer017();
        await CheckIdlePerformance017();
        await CheckQualityOffline017();
        await CheckQuality017();
        await Run("DPAPI current-user roundtrip, encrypted file, tamper rejection and logout deletion", () =>
        {
            var directory = Path.Combine(_directory, "dpapi");
            var store = new SessionStore(directory);
            Assert(store.Load() is null, "new store must be empty");
            store.Save(FakeSession);
            Assert(store.Load() == FakeSession, "session readback differs");
            var file = Path.Combine(directory, "session.dpapi");
            var bytes = File.ReadAllBytes(file);
            Assert(!Encoding.UTF8.GetString(bytes).Contains(FakeSession.AccessToken), "plaintext token on disk");
            bytes[^8] ^= 0x42; File.WriteAllBytes(file, bytes);
            try { store.Load(); throw new InvalidOperationException("tampered file accepted"); } catch (CryptographicException) { }
            store.Clear(); Assert(!File.Exists(file), "logout failed to delete session");
            return Task.CompletedTask;
        });
        await Run("Account parsing and OAuth header only on API host", async () =>
        {
            var handler = new FakeHandler((request, _) => Task.FromResult(request.RequestUri!.AbsolutePath switch
            {
                "/account/status" => Json(Profile),
                "/account/about" => Json("{\"result\":{\"uid\":\"12345\"}}"),
                _ => throw new InvalidOperationException("Unexpected profile endpoint")
            }));
            using var api = new MusicApi(handler);
            var account = await api.GetAccountAsync(FakeSession, default);
            Assert(account.Uid == "12345" && account.Plus && account.DisplayName == "Fixture Account", "profile parsing");
            Assert(handler.Seen.Select(call => call.Uri.AbsolutePath).SequenceEqual(new[] { "/account/status", "/account/about" }), "profile route sequence changed");
            Assert(handler.Seen.All(call => call.Authorization == "OAuth " + FakeSession.AccessToken), "API auth missing");
            Assert(handler.Seen.All(call => call.Uri.Host == "api.music.yandex.net"), "wrong API host");
        });
        await Run("Favorites: exact composite IDs, API order, absent metadata kept unavailable", async () =>
        {
            var handler = new FakeHandler((request, _) => Task.FromResult(Json(request.Method == HttpMethod.Get
                ? """{"result":{"library":{"tracks":[{"id":20,"albumId":2},{"id":"10","albumId":"1"},{"id":99}]}}}"""
                : """{"result":[{"id":"10","title":"Ten","durationMs":180000,"available":true,"artists":[{"name":"A"}],"albums":[{"title":"Album"}]},{"id":20,"title":"Twenty","availableForPremiumUsers":true}]}""")));
            using var api = new MusicApi(handler);
            var ids = await api.GetLikedIdsAsync(FakeSession, "12345", default);
            var rows = await api.GetTracksAsync(FakeSession, ids, 100, default);
            Assert(rows.Count == 3 && rows[0].Id == "20" && rows[1].Id == "10" && rows[2].Id == "99", "order/identity changed");
            Assert(rows[0].RequestId == "20:2" && rows[0].Number == 101 && rows[0].Available && !rows[2].Available, "metadata/offset fallback");
            Assert(rows[1].Duration == "3:00", "duration");
            Assert(Uri.UnescapeDataString(handler.Seen[1].Body).Contains("track-ids=20:2,10:1,99"), "batch request differs");
        });
        await Run("HTTP failures and API schema errors are explicit, without raw response or retry", async () =>
        {
            foreach (var status in new[] { HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden, HttpStatusCode.TooManyRequests, HttpStatusCode.Redirect })
            {
                var handler = new FakeHandler((_, _) => Task.FromResult(Json("server-private-payload", status)));
                using var api = new MusicApi(handler);
                var error = await Throws<MusicException>(() => api.GetAccountAsync(FakeSession, default));
                Assert(!error.Message.Contains("server-private-payload"), "raw response leaked");
                Assert(error.Authentication == (status == HttpStatusCode.Unauthorized) && handler.Seen.Count == 1, "unexpected retry or auth state");
            }
            using var malformed = new MusicApi(new FakeHandler((_, _) => Task.FromResult(Json("{\"result\":{}}"))));
            await Throws<MusicException>(() => malformed.GetLikedIdsAsync(FakeSession, "12345", default));
        });
        await Run("Preview-only stream rejected before contacting CDN", async () =>
        {
            var handler = new FakeHandler((_, _) => Task.FromResult(Json("""{"result":[{"codec":"mp3","preview":true,"bitrateInKbps":320,"downloadInfoUrl":"https://storage.mds.yandex.net/preview"}]}""")));
            using var api = new MusicApi(handler);
            await Throws<MusicException>(() => api.ResolveStreamAsync(FakeSession, "20:2", default));
            Assert(handler.Seen.Count == 1, "preview CDN contacted");
        });
        await Run("Highest full MP3 selected, signature generated, no token sent to CDN", async () =>
        {
            var handler = new FakeHandler((request, _) => Task.FromResult(request.RequestUri!.Host == "api.music.yandex.net"
                ? Json("""{"result":[{"codec":"mp3","preview":false,"bitrateInKbps":192,"downloadInfoUrl":"https://storage.mds.yandex.net/192"},{"codec":"mp3","preview":true,"bitrateInKbps":999,"downloadInfoUrl":"https://storage.mds.yandex.net/preview"},{"codec":"mp3","preview":false,"bitrateInKbps":320,"downloadInfoUrl":"https://storage.mds.yandex.net/320"}]}""") : Xml()));
            using var api = new MusicApi(handler);
            var stream = await api.ResolveStreamAsync(FakeSession, "20:2", default);
            Assert(stream.Bitrate == 320 && stream.Uri.Host == "s1.vlx.yandex.net", "wrong variant");
            Assert(handler.Seen[1].Uri.AbsolutePath == "/320" && handler.Seen[1].Authorization is null, "CDN token leak");
            var expected = Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes("XGRlBW9FXlekgbPrRHuSiA" + "test/file.mp3" + "salt"))).ToLowerInvariant();
            Assert(stream.Uri.AbsolutePath == $"/get-mp3/{expected}/1234/test/file.mp3", "signature mismatch");
        });
        await Run("External CDN redirects, unsafe media URLs and XML entities rejected", async () =>
        {
            foreach (var url in new[] { "http://s1.vlx.yandex.net/x", "https://yandex.net.evil.example/x", "https://u:p@s1.vlx.yandex.net/x", "https://127.0.0.1/x", "https://s1.vlx.yandex.net:8888/x" })
            {
                await Throws<MusicException>(() => Task.FromResult(MusicApi.ValidateMediaUri(new Uri(url))));
            }
            await Throws<System.Xml.XmlException>(() => Task.FromResult(MusicApi.BuildStreamUri("<!DOCTYPE x [<!ENTITY leak SYSTEM 'file:///C:/Windows/win.ini'>]><download-info>&leak;</download-info>")));
            var handler = new FakeHandler((request, _) =>
            {
                if (request.RequestUri!.Host == "api.music.yandex.net") return Task.FromResult(Json("""{"result":[{"codec":"mp3","downloadInfoUrl":"https://storage.mds.yandex.net/x"}]}"""));
                var redirect = new HttpResponseMessage(HttpStatusCode.Redirect); redirect.Headers.Location = new Uri("https://evil.example/token"); return Task.FromResult(redirect);
            });
            using var api = new MusicApi(handler);
            await Throws<MusicException>(() => api.ResolveStreamAsync(FakeSession, "20", default));
            Assert(handler.Seen.Count == 2 && handler.Seen.All(s => s.Uri.Host != "evil.example"), "unsafe redirect followed");
        });
        await Run("Device OAuth begins on expected host, pending then successful token", async () =>
        {
            var polls = 0;
            var handler = new FakeHandler((request, _) => Task.FromResult(request.RequestUri!.AbsolutePath == "/device/code"
                ? Json("""{"device_code":"fake-device-code","user_code":"TEST-CODE","verification_url":"https://oauth.yandex.ru/device","interval":1,"expires_in":20}""")
                : ++polls == 1 ? Json("""{"error":"authorization_pending"}""", HttpStatusCode.BadRequest) : Json(TokenResponse)));
            using var api = new MusicApi(handler);
            var code = await api.BeginLoginAsync("test-device", default);
            var session = await api.CompleteLoginAsync(code, "test-device", default);
            Assert(session.AccessToken == FakeSession.AccessToken && polls == 2 && session.ExpiresAtUtc > DateTimeOffset.UtcNow.AddMinutes(59), "OAuth not complete");
            Assert(handler.Seen.All(s => s.Uri.Host == "oauth.yandex.ru" && s.Authorization is null), "OAuth account header leak");
        });
        await Run("Device OAuth cancellation, expiry, denial and unsafe verification URL", async () =>
        {
            using var api = new MusicApi(new FakeHandler((_, _) => Task.FromResult(Json("""{"error":"authorization_pending"}""", HttpStatusCode.BadRequest))));
            using var cancel = new CancellationTokenSource(50);
            await Throws<OperationCanceledException>(() => api.CompleteLoginAsync(TestCode(), "test", cancel.Token));
            var expired = await Throws<MusicException>(() => api.CompleteLoginAsync(TestCode(3, 1), "test", default));
            Assert(expired.Message.Contains("истекло"), "expiry not explained");
            using var denied = new MusicApi(new FakeHandler((_, _) => Task.FromResult(Json("""{"error":"access_denied"}""", HttpStatusCode.BadRequest))));
            await Throws<MusicException>(() => denied.CompleteLoginAsync(TestCode(), "test", default));
            using var evil = new MusicApi(new FakeHandler((_, _) => Task.FromResult(Json("""{"device_code":"x","user_code":"y","verification_url":"https://evil.example/"}"""))));
            await Throws<MusicException>(() => evil.BeginLoginAsync("test", default));
            using var shortDomain = new MusicApi(new FakeHandler((_, _) => Task.FromResult(Json("""{"device_code":"x","user_code":"y","verification_url":"https://ya.ru/device"}"""))));
            Assert((await shortDomain.BeginLoginAsync("test", default)).VerificationUri.Host == "ya.ru", "live short domain unsupported");
        });
        await Run("Refresh rotates access token, retains omitted refresh token, rejects expired grant", async () =>
        {
            using var api = new MusicApi(new FakeHandler((_, _) => Task.FromResult(Json("""{"access_token":"new-fake-token","expires_in":3600}"""))));
            var renewed = await api.RefreshAsync(FakeSession, default);
            Assert(renewed.AccessToken == "new-fake-token" && renewed.RefreshToken == FakeSession.RefreshToken, "refresh persistence");
            using var bad = new MusicApi(new FakeHandler((_, _) => Task.FromResult(Json("""{"error":"invalid_grant"}""", HttpStatusCode.BadRequest))));
            Assert((await Throws<MusicException>(() => bad.RefreshAsync(FakeSession, default))).Authentication, "refresh failure not authentication error");
        });
        await Run("Native audio: open, duration, advancing position, pause, seek, resume, stop", async () =>
        {
            var path = WriteWave("tone.wav", 5);
            using var player = new NativePlayer(); player.SetVolume(0);
            await player.OpenAsync(new Uri(path), default);
            Assert(player.IsPlaying && player.Duration.TotalSeconds is > 4.8 and < 5.2, "native media duration/state");
            await Task.Delay(350);
            Assert(player.Position.TotalMilliseconds > 100, "audio clock not advancing");
            player.Toggle(); var paused = player.Position;
            await Task.Delay(200); Assert(!player.IsPlaying && Math.Abs((player.Position - paused).TotalMilliseconds) < 100, "pause failed");
            player.Seek(TimeSpan.FromSeconds(2)); player.Toggle();
            await Task.Delay(200); Assert(player.Position.TotalSeconds >= 1.9 && player.IsPlaying, "seek/resume failed");
            player.Stop(); Assert(!player.HasSource && !player.IsPlaying, "stop did not release source");
        });
        await Run("Native audio: canceled old open cannot close newer player; MediaEnded fires", async () =>
        {
            var path = WriteWave("short.wav", 0.8);
            using var player = new NativePlayer(); player.SetVolume(0);
            using var cancel = new CancellationTokenSource();
            var oldOpen = player.OpenAsync(new Uri(path), cancel.Token);
            cancel.Cancel();
            var newOpen = player.OpenAsync(new Uri(path), default);
            await Throws<OperationCanceledException>(() => oldOpen);
            await newOpen;
            Assert(player.IsPlaying && player.HasSource, "old cancellation closed new media");
            var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            player.Ended += (_, _) => ended.TrySetResult();
            await ended.Task.WaitAsync(TimeSpan.FromSeconds(4));
            Assert(!player.IsPlaying, "end state wrong");
        });
        await Run("Audio buffer: anonymous bounded download, cancellation cleanup and native playback", async () =>
        {
            var wave = File.ReadAllBytes(WriteWave("buffer-source.wav", 3));
            var handler = new FakeHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(wave) }));
            var directory = Path.Combine(_directory, "audio-buffer");
            using (var player = new NativePlayer(new AudioBuffer(directory, handler)))
            {
                player.SetVolume(0);
                await player.OpenAsync(new Uri("https://s1.vlx.yandex.net/test.mp3"), default);
                await Task.Delay(200);
                Assert(player.IsPlaying && player.Position.TotalMilliseconds > 50, "buffered audio not playing");
                Assert(handler.Seen.All(s => s.Authorization is null), "audio transport sent account header");
                Assert(Directory.GetFiles(directory).Length == 1, "expected one owned buffer file");
                player.Stop();
                Assert(Directory.GetFiles(directory).Length == 0, "buffer remained after stop");
            }
            var oversized = new FakeHandler((_, _) =>
            {
                var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) };
                response.Content.Headers.ContentLength = AudioBuffer.MaximumBytes + 1; return Task.FromResult(response);
            });
            using var buffer = new AudioBuffer(directory, oversized);
            await Throws<MusicException>(() => buffer.DownloadAsync(new Uri("https://s1.vlx.yandex.net/test.mp3"), default));
            Assert(Directory.GetFiles(directory).Length == 0, "oversized download left temp file");
        });
        await Run("AES CTR: fixed zero-key vector and chunk boundary preservation", () =>
        {
            var bytes = new byte[32];
            using (var decryptor = new AesCounter(new string('0', 32))) { decryptor.Transform(bytes.AsSpan(0, 7)); decryptor.Transform(bytes.AsSpan(7)); }
            Assert(Convert.ToHexString(bytes).ToLowerInvariant() == "66e94bd4ef8a2c3b884cfa59ca342b2e58e2fccefa7e3061367f1d57a4e7455a", "AES-CTR fixed vector mismatch");
            return Task.CompletedTask;
        });
        await Run("Quality: 192 bitrate ceiling and lossless codec/header/signature validation", async () =>
        {
            var handler = new FakeHandler((request, _) => Task.FromResult(request.RequestUri!.AbsolutePath == "/get-file-info"
                ? Json("""{"result":{"downloadInfo":{"quality":"lossless","codec":"flac","transport":"raw","bitrate":900,"url":"https://s1.vlx.yandex.net/full.flac"}}}""")
                : request.RequestUri.Host == "api.music.yandex.net" ? Json("""{"result":[{"codec":"mp3","bitrateInKbps":192,"downloadInfoUrl":"https://storage.mds.yandex.net/192"},{"codec":"mp3","bitrateInKbps":320,"downloadInfoUrl":"https://storage.mds.yandex.net/320"}]}""") : Xml()));
            using var api = new MusicApi(handler);
            var standard = await api.ResolveStreamAsync(FakeSession, "20:2", default, 192);
            Assert(standard.Bitrate == 192 && handler.Seen[1].Uri.AbsolutePath == "/192", "quality ceiling ignored");
            var lossless = await api.ResolveAudioAsync(FakeSession, "20:2", AudioQuality.Excellent, default);
            Assert(lossless.Codec == "flac" && lossless.Extension == ".flac" && lossless.Key is null, "lossless descriptor");
            Assert(handler.Seen.Last().MusicClient == "YandexMusicAndroid/24023621" && handler.Seen.Last().Uri.Query.Contains("sign="), "lossless signature/header missing");
            Assert(!lossless.ToString().Contains("https"), "signed URI formatted as display metadata");
            using var downgraded = new MusicApi(new FakeHandler((request, _) => Task.FromResult(request.RequestUri!.AbsolutePath == "/get-file-info"
                ? Json("""{"result":{"downloadInfo":{"quality":"nq","codec":"mp3","bitrate":192,"transport":"raw","url":"https://s1.vlx.yandex.net/x"}}}""")
                : request.RequestUri.Host == "api.music.yandex.net" ? Json("""{"result":[{"codec":"mp3","bitrateInKbps":192,"downloadInfoUrl":"https://storage.mds.yandex.net/192"}]}""") : Xml())));
            var fallback = await downgraded.ResolveAudioAsync(FakeSession, "20", AudioQuality.Excellent, default);
            Assert(fallback.Codec == "mp3" && fallback.Bitrate == 192 && !fallback.QualityLabel.Contains("FLAC"), "fallback mislabelled as FLAC");
        });
        await Run("Offline: selected download, safe file name, full hash readback, deduplication and preserved files", async () =>
        {
            var wave = File.ReadAllBytes(WriteWave("offline-source.wav", 3));
            var handler = new FakeHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(wave) }));
            var directory = Path.Combine(_directory, "offline-library");
            using var library = new OfflineLibrary(directory, new AudioBuffer(Path.Combine(_directory, "offline-temp"), handler));
            var track = new TrackRow { Id = "99", RequestId = "99:3", Title = "Bad:/title?", Artist = "Fixture", Album = "Album", DurationMs = 3000, Available = true };
            var audio = new AudioDescriptor(new Uri("https://s1.vlx.yandex.net/full.mp3"), "mp3", 320);
            var item = await library.SaveAsync(track, audio, default);
            var path = library.ResolvePath(item.FileName);
            Assert(File.Exists(path) && item.Sha256 == Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant(), "offline hash readback");
            Assert(Path.GetFileName(path) == item.FileName && library.Read().Count == 1 && library.Tracks()[0].LocalPath == path, "offline index");
            await library.SaveAsync(track, audio, default);
            Assert(handler.Seen.Count == 1 && library.Read().Count == 1, "duplicate download fetched twice");
            var original = File.ReadAllBytes(path); original[100] ^= 1; File.WriteAllBytes(path, original);
            var replacement = await library.SaveAsync(track, audio, default);
            Assert(replacement.FileName != item.FileName && File.ReadAllBytes(path).SequenceEqual(original), "changed user file overwritten/deleted");
            Assert(library.Read().Count == 1, "damaged entry duplicated in index");
            await Throws<MusicException>(() => Task.FromResult(library.ResolvePath("..\\secret.mp3")));
        });
        await Run("Offline: corrupt index is not overwritten; UI plays a local file with no account/network", async () =>
        {
            var directory = Path.Combine(_directory, "offline-ui");
            var wave = File.ReadAllBytes(WriteWave("offline-ui-source.wav", 3));
            var mediaHandler = new FakeHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(wave) }));
            var library = new OfflineLibrary(directory, new AudioBuffer(Path.Combine(_directory, "offline-ui-temp"), mediaHandler));
            var track = new TrackRow { Id = "88", RequestId = "88", Title = "Offline fixture", DurationMs = 3000, Available = true };
            var saved = await library.SaveAsync(track, new AudioDescriptor(new Uri("https://s1.vlx.yandex.net/full.mp3"), "mp3", 320), default);
            var noNetwork = new FakeHandler((_, _) => throw new InvalidOperationException("offline attempted API access"));
            var window = new MainWindow(api: new MusicApi(noNetwork), restore: false, diagnosticsDirectory: Path.Combine(_directory, "ui-data"), offline: library);
            try
            {
                window.Show(); Control<Slider>(window, "VolumeSlider").Value = 0;
                Control<Button>(window, "OfflineNav").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                var rows = Control<ListView>(window, "TracksList");
                Assert(rows.Items.Count == 1, "offline row not visible");
                await window.PlayTrackAsync((TrackRow)rows.Items[0]); await Task.Delay(250);
                Assert(((TrackRow)rows.Items[0]).IsPlaying && noNetwork.Seen.Count == 0, "offline playback failed or attempted API access");
                Assert(Control<TextBlock>(window, "QualityText").Text.StartsWith("Офлайн · ", StringComparison.Ordinal)
                    && Control<TextBlock>(window, "StatusText").Text == "", "offline source must appear beside player quality");
            }
            finally { window.Close(); }
            Assert(File.Exists(library.ResolvePath(saved.FileName)), "closing player deleted user's offline file");
            var index = Path.Combine(directory, "library.index.json");
            File.WriteAllText(index, "damaged fixture index");
            using var corrupted = new OfflineLibrary(directory);
            await Throws<MusicException>(() => corrupted.SaveAsync(track, new AudioDescriptor(new Uri("https://s1.vlx.yandex.net/x"), "mp3", 320), default));
            Assert(File.ReadAllText(index) == "damaged fixture index", "corrupt index was replaced");
        });
        await Run("UI: signed-out controls and rendered login screen", async () =>
        {
            var handler = new FakeHandler((_, _) => throw new InvalidOperationException("unexpected network before login"));
            var window = Window(handler);
            try
            {
                window.Show(); await Task.Delay(150);
                Assert(Control<Button>(window, "LoginButton").IsVisible && !Control<Button>(window, "RefreshButton").IsVisible, "login controls");
                Assert(!Control<TextBox>(window, "FilterBox").IsEnabled && handler.Seen.Count == 0, "unsigned access");
                App.Capture(window, Path.Combine(_directory, "ui-signed-out.png"));
            }
            finally { window.Close(); }
        });
        await Run("UI: 5000 rows virtualized, scrolling and filter; demo visibly marked", async () =>
        {
            var window = new MainWindow(true, restore: false, diagnosticsDirectory: Path.Combine(_directory, "ui-data"), offline: new OfflineLibrary(Path.Combine(_directory, "demo-offline")));
            try
            {
                window.Show(); await Task.Delay(200);
                var list = Control<ListView>(window, "TracksList");
                Assert(list.Items.Count == 5000, "demo row count");
                await Dispatcher.Yield(DispatcherPriority.ContextIdle);
                window.UpdateLayout(); list.UpdateLayout();
                var realized = CountVisual<ListViewItem>(list);
                if (realized == 0) App.Capture(window, Path.Combine(_directory, "virtualization-failure.png"));
                Assert(realized > 0 && realized < 100, "virtualization disabled: " + realized);
                list.ScrollIntoView(list.Items[^1]); await Task.Delay(100);
                Assert(CountVisual<ListViewItem>(list) < 100, "scroll creates all rows");
                Control<TextBox>(window, "FilterBox").Text = "Утренний свет · 4999";
                await Task.Delay(350);
                Assert(list.Items.Count == 1, "local filter failed");
                Control<TextBox>(window, "FilterBox").Clear(); await Task.Delay(350);
                list.ScrollIntoView(list.Items[0]);
                Assert(Control<TextBlock>(window, "ModeLabel").Text.Contains("ТЕСТОВЫЕ"), "demo not labeled");
                window.UpdateLayout(); App.Capture(window, Path.Combine(_directory, "ui-demo.png"));
                window.Width = 1050; window.Height = 680; window.UpdateLayout();
                App.Capture(window, Path.Combine(_directory, "ui-minimum.png"));
            }
            finally { window.Close(); }
        });
        await Run("UI: device login -> profile -> 205 ordered favorites in 3 batches -> encrypted session -> logout", async () =>
        {
            var fixture = new CatalogFixture();
            var window = Window(fixture.Handler);
            try
            {
                window.Show(); await window.StartLoginAsync();
                var list = Control<ListView>(window, "TracksList");
                Assert(list.Items.Count == 205, "not all favorites loaded");
                Assert(((TrackRow)list.Items[0]).Id == "0" && ((TrackRow)list.Items[^1]).Id == "204", "favorite order lost");
                Assert(fixture.BatchSizes.SequenceEqual(new[] { 100, 100, 5 }), "wrong batching");
                Assert(Control<TextBlock>(window, "AccountText").Text == "Fixture Account", "account not active");
                Assert(!Control<Button>(window, "LoginButton").IsVisible && Control<Button>(window, "LogoutButton").IsVisible, "login UI not updated");
                Assert(File.Exists(Path.Combine(_directory, "ui-store", "session.dpapi")), "session not persisted");
                Assert(!fixture.Handler.Seen.Any(s => s.Uri.AbsolutePath.Contains("add") || s.Uri.AbsolutePath.Contains("remove")), "collection mutated");
                App.Capture(window, Path.Combine(_directory, "ui-signed-in-fixture.png"));
                window.SignOut();
                Assert(list.Items.Count == 0 && !File.Exists(Path.Combine(_directory, "ui-store", "session.dpapi")), "logout left data");
            }
            finally { window.Close(); }
        });
        await Run("UI: logout during in-flight library reload cannot restore account or rows", async () =>
        {
            var fixture = new CatalogFixture();
            var window = Window(fixture.Handler);
            try
            {
                window.Show(); await window.StartLoginAsync();
                fixture.BlockLibrary = true;
                var reload = window.RefreshLibraryAsync();
                await fixture.LibraryEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
                window.SignOut(); fixture.ReleaseLibrary.TrySetResult();
                await reload;
                Assert(Control<ListView>(window, "TracksList").Items.Count == 0 && Control<TextBlock>(window, "AccountText").Text == "Вход не выполнен", "stale load resurrected account");
                Assert(!File.Exists(Path.Combine(_directory, "ui-store", "session.dpapi")), "session resurrected");
            }
            finally { fixture.ReleaseLibrary.TrySetResult(); window.Close(); }
        });
        await Run("UI: canceled login leaves no token or favorite rows", async () =>
        {
            var fixture = new CatalogFixture { PendingForever = true };
            var window = Window(fixture.Handler);
            try
            {
                window.Show(); var login = window.StartLoginAsync();
                await Task.Delay(100); window.SignOut(); await login;
                Assert(Control<ListView>(window, "TracksList").Items.Count == 0, "canceled login loaded data");
                Assert(!File.Exists(Path.Combine(_directory, "ui-store", "session.dpapi")), "canceled login persisted session");
            }
            finally { window.Close(); }
        });
        await CheckAdvanced();
        await CheckMediaUi();
        await CheckLyricsUi();
        await CheckWavePresentation();
        await CheckPlaybackPreload();
        await CheckLrclib();
        await CheckSearchArtist();
        await CheckLyricsFollow();
        await CheckCompactLayout();
        await CheckAlbumMetadata037();
        await CheckTrackLinks037();
        await CheckMiniPlayer();
        await CheckBrandingAppearance038();
        await CheckCrossfadeOnly();
        await CheckClientPresentation();
        await CheckPlaylistSettings();
        await CheckHomeArtwork();
        await CheckUninstall();
        await CheckDownloadCenter();
        await CheckPlaybackPersistence();
        await CheckScrollPerformance();
        await CheckLoudness();
        await CheckProgressivePlayback();
        await CheckListSorting();
        await CheckUnavailableLikes();
        await CheckBulkUnavailableLikes();
        await CheckBrandMigration();
        await CheckCollectionHero();
        await CheckExpandedPlayer();
        await CheckControlChrome();
        await CheckNavigation014();
        await CheckUiPolish015();
        await CheckCleanupPerformance015();
        await CheckLargeCollection015();
        await CheckTransferMaintenance015();
        await CheckTrackWave015();
        await CheckCoverAccent015();
        await CheckAmbientBackdrop015();
        await CheckCoverPalette016();
        await CheckColorfulAccent0317();
        await CheckCoverAccent016();
        await CheckSidebarPlayer016();
        await CheckCompactTransfer016();
        await CheckSmoothScroll016();
        await CheckPlaybackControls016();
        await CheckPlaybackContext0317();
        await CheckExpandedQueue0317();
        await CheckFullscreenReveal0317();
        await CheckFullscreenPower0317();
        await CheckFullscreenHost040();
        await CheckToastPlacement040();
        await CheckVersioning040();
        await CheckBranding040();
        await CheckTheme040();
        await CheckThemeSettings040();
        await CheckThemeIntegration040();
        await CheckTrackActions0317();
        await CheckPopupGlyph0317();
        await CheckInterfacePolish0317();
        await CheckTrackPosition0317();
        await CheckUpdateSchedule0317();
        await CheckUpdateApplyPreferences0317();
        await CheckCompactDownload0317();
        await CheckStartupUpdates016();
        await Run("Diagnostics contain resources and timings, without credentials or account data", () =>
        {
            var path = Path.Combine(_directory, "sanitized-diagnostics.json");
            using var diagnostics = new Diagnostics { Stage = "library_ready", LoadedTracks = 205, TotalTracks = 205 };
            diagnostics.WindowReady(); diagnostics.Sample(); diagnostics.Timing("favorites_all_ms", 123); diagnostics.Save(path);
            var text = File.ReadAllText(path);
            Assert(!text.Contains(FakeSession.AccessToken) && !text.Contains(FakeSession.RefreshToken!) && !text.Contains("Fixture Account") && !text.Contains("access_token"), "diagnostics leaked data");
            using var json = JsonDocument.Parse(text);
            Assert(json.RootElement.GetProperty("executableSha256").GetString()!.Length == 64 && json.RootElement.GetProperty("samples").GetArrayLength() >= 1, "missing measurement evidence");
            return Task.CompletedTask;
        });
        if (online)
        {
            await Run("LIVE native MP3 decoder with anonymously fetched public fixture, muted", async () =>
            {
                using var http = new HttpClient();
                var path = Path.Combine(_directory, "public-fixture.mp3");
                await File.WriteAllBytesAsync(path, await http.GetByteArrayAsync("https://www.w3schools.com/html/horse.mp3"));
                using var player = new NativePlayer(); player.SetVolume(0);
                await player.OpenAsync(new Uri(path), default);
                await Task.Delay(250);
                Assert(player.IsPlaying && player.Duration.TotalSeconds > 0 && player.Position.TotalMilliseconds > 50, "remote MP3 clock did not advance");
                player.Stop();
            });
            await Run("LIVE preflight: Yandex issues a device code (no account authentication performed)", async () =>
            {
                using var api = new MusicApi();
                var code = await api.BeginLoginAsync(Guid.NewGuid().ToString("N"), default);
                Assert(code.Code.Length > 0 && code.UserCode.Length > 0 && code.ExpiresInSeconds > 0, "no device code");
                // Never print, serialize or complete the temporary device code.
            });
        }
        if (_realAccount)
        {
            await Run("ACCOUNT: server wave batches are real and continue", async () =>
            {
                var session = new SessionStore(App.DataDirectory).Load() ?? throw new InvalidOperationException("Prototype session missing");
                using var api = new MusicApi();
                var first = await api.StartWaveAsync(session, default);
                Assert(first.Tracks.Count > 0 && first.Tracks.Any(t => t.Available), "real server wave is empty");
                var more = await api.MoreWaveAsync(session, first.SessionId, default);
                Assert(more.Tracks.Count > 0, "real wave continuation empty");
                File.WriteAllText(Path.Combine(_directory, "real-wave-sanitized.json"), JsonSerializer.Serialize(new { firstBatch = first.Tracks.Count, nextBatch = more.Tracks.Count, serverSession = true, feedbackSent = false, accountDetailsSaved = false }));
            });
            await Run("ACCOUNT: quality lossless and server wave metadata", async () =>
            {
                var session = new SessionStore(App.DataDirectory).Load() ?? throw new InvalidOperationException("Prototype session missing");
                using var api = new MusicApi();
                var account = await api.GetAccountAsync(session, default);
                Console.WriteLine("INFO real account hasPlus=" + account.Plus);
                var ids = await api.GetLikedIdsAsync(session, account.Uid, default);
                var candidates = (await api.GetTracksAsync(session, ids.Take(100).ToArray(), 0, default)).Where(t => t.Available).Take(5).ToArray();
                AudioDescriptor? audio = null;
                foreach (var track in candidates)
                {
                    try
                    {
                        var candidate = await api.ResolveAudioAsync(session, track.RequestId, AudioQuality.Excellent, default);
                        if (candidate.IsLossless) { audio = candidate; break; }
                        Console.WriteLine("INFO server returned lossy codec=" + candidate.Codec + ", bitrate=" + candidate.Bitrate);
                    }
                    catch (MusicException error) { Console.WriteLine("INFO lossless probe: " + error.Message); }
                }
                Assert(audio is { IsLossless: true }, "Server returned no actual FLAC rendition among first five available favorites; lossy playback does not prove lossless");
                using var player = new NativePlayer(new AudioBuffer(Path.Combine(_directory, "real-flac-buffer")));
                player.SetVolume(0); await player.OpenAsync(audio!, default); await Task.Delay(300);
                Assert(player.IsPlaying && player.Duration.TotalSeconds > 35 && player.Position.TotalMilliseconds > 50, "real lossless playback failed");
                player.Stop();
                var wave = await api.StartWaveAsync(session, default);
                Assert(wave.Tracks.Count > 0 && wave.Tracks.Any(t => t.Available), "server wave empty");
                var more = await api.MoreWaveAsync(session, wave.SessionId, default);
                Assert(more.Tracks.Count > 0, "wave continuation empty");
                File.WriteAllText(Path.Combine(_directory, "real-quality-wave-sanitized.json"), JsonSerializer.Serialize(new { codec = audio!.Codec, actualQuality = audio.QualityLabel, nativeClockAdvanced = true, firstWaveBatch = wave.Tracks.Count, nextWaveBatch = more.Tracks.Count, accountDetailsSaved = false }, new JsonSerializerOptions { WriteIndented = true }));
            });
            await Run("ACCOUNT: saved prototype session -> real favorites -> full audio -> native playback", async () =>
            {
                var session = new SessionStore(App.DataDirectory).Load() ?? throw new InvalidOperationException("Prototype session is missing");
                using var api = new MusicApi();
                var account = await api.GetAccountAsync(session, default);
                var ids = await api.GetLikedIdsAsync(session, account.Uid, default);
                Assert(ids.Count > 0, "real favorites list is empty");
                var rows = await api.GetTracksAsync(session, ids.Take(100).ToArray(), 0, default);
                var track = rows.First(t => t.Available);
                var stream = await api.ResolveAudioAsync(session, track.RequestId, AudioQuality.Excellent, default);
                using var player = new NativePlayer(new AudioBuffer(Path.Combine(_directory, "real-audio-buffer")));
                player.SetVolume(0);
                await player.OpenAsync(stream, default);
                await Task.Delay(500);
                Assert(player.IsPlaying && player.Position.TotalMilliseconds > 100 && player.Duration.TotalSeconds > 35, "real full MP3 not playing");
                var actualDuration = player.Duration.TotalSeconds;
                player.Toggle(); player.Seek(TimeSpan.FromSeconds(10)); player.Toggle(); await Task.Delay(150);
                Assert(player.Position.TotalSeconds >= 9.5, "real MP3 seek failed");
                player.Stop();
                File.WriteAllText(Path.Combine(_directory, "real-account-sanitized.json"), JsonSerializer.Serialize(new { favoritesCount = ids.Count, metadataBatch = rows.Count, fullTrack = true, codec = stream.Codec, bitrate = stream.Bitrate, actualDurationSeconds = actualDuration, nativeClockAdvanced = true, seekPassed = true, tokenLogged = false, audibleOutputAssessed = false }, new JsonSerializerOptions { WriteIndented = true }));
            });
        }
    }

    private static MainWindow Window(FakeHandler handler) => new(api: new MusicApi(handler), store: new SessionStore(Path.Combine(_directory, "ui-store")), restore: false, diagnosticsDirectory: Path.Combine(_directory, "ui-data"), offline: new OfflineLibrary(Path.Combine(_directory, "ui-offline")));
    private static T Control<T>(MainWindow window, string name) where T : FrameworkElement => (T)window.FindName(name);
    private static int CountVisual<T>(DependencyObject element) where T : DependencyObject
    {
        var count = element is T ? 1 : 0;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(element); i++) count += CountVisual<T>(VisualTreeHelper.GetChild(element, i));
        return count;
    }
    private static string WriteWave(string name, double seconds)
    {
        var path = Path.Combine(_directory, name);
        const int rate = 44100;
        var samples = (int)(rate * seconds);
        using var writer = new BinaryWriter(File.Create(path));
        writer.Write(Encoding.ASCII.GetBytes("RIFF")); writer.Write(36 + samples * 2); writer.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
        writer.Write(16); writer.Write((short)1); writer.Write((short)1); writer.Write(rate); writer.Write(rate * 2); writer.Write((short)2); writer.Write((short)16);
        writer.Write(Encoding.ASCII.GetBytes("data")); writer.Write(samples * 2);
        for (var i = 0; i < samples; i++) writer.Write((short)(Math.Sin(i * 2 * Math.PI * 220 / rate) * 1200));
        return path;
    }

    private sealed record RequestSnapshot(Uri Uri, string? Authorization, string Body, string? MusicClient);
    private sealed class FakeHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public List<RequestSnapshot> Seen { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            lock (Seen) Seen.Add(new(request.RequestUri!, request.Headers.Authorization?.ToString(), body, request.Headers.TryGetValues("X-Yandex-Music-Client", out var client) ? client.Single() : null));
            return await respond(request, cancellationToken);
        }
    }
    private sealed class CatalogFixture
    {
        public FakeHandler Handler { get; }
        public List<int> BatchSizes { get; } = [];
        public bool BlockLibrary { get; set; }
        public bool PendingForever { get; init; }
        public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? Extra { get; set; }
        public TaskCompletionSource LibraryEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseLibrary { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CatalogFixture() { Handler = new FakeHandler(Respond); }
        private async Task<HttpResponseMessage> Respond(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/device/code") return Json("""{"device_code":"fake","user_code":"TEST-CODE","verification_url":"https://oauth.yandex.ru/device","interval":1,"expires_in":30}""");
            if (path == "/token") return PendingForever ? Json("""{"error":"authorization_pending"}""", HttpStatusCode.BadRequest) : Json(TokenResponse);
            if (path == "/account/status") return Json(Profile);
            if (path.Contains("/likes/tracks"))
            {
                if (BlockLibrary) { LibraryEntered.TrySetResult(); await ReleaseLibrary.Task; }
                return Json(JsonSerializer.Serialize(new { result = new { library = new { tracks = Enumerable.Range(0, 205).Select(i => new { id = i, albumId = 1 }) } } }));
            }
            if (path == "/tracks")
            {
                var body = await request.Content!.ReadAsStringAsync(cancellationToken);
                var ids = Uri.UnescapeDataString(body.Split('&')[0].Split('=')[1]).Split(',').Select(s => s.Split(':')[0]).ToArray();
                BatchSizes.Add(ids.Length);
                return Json(JsonSerializer.Serialize(new { result = ids.Reverse().Select(id => new { id, title = "Fixture track " + id, available = true, durationMs = 180000, artists = new[] { new { name = "Fixture Artist" } }, albums = new[] { new { title = "Fixture Album" } } }) }));
            }
            if (Extra is not null) return await Extra(request, cancellationToken);
            throw new InvalidOperationException("Unexpected fixture endpoint " + path);
        }
    }
}
