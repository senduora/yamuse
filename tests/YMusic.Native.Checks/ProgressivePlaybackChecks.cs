using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using System.Text.Json;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;
using Windows.Storage;
using YMusic.Native;

internal static partial class Program
{
    private static async Task CheckProgressivePlayback()
    {
        await Run("Playback persistence: queued background writes cannot revive logout data or overwrite the final close snapshot", async () =>
        {
            var directory = Path.Combine(_directory, "persistence-background");
            var store = new PlaybackStateStore(directory);
            var queue = new PlaybackQueue(); queue.SetItems([PersistenceTrack("101")]);
            var snapshot = PlaybackStateStore.Capture("12345", queue, 7);
            var gate = typeof(PlaybackStateStore).GetField("_gate", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(store)!;
            Task pending;
            lock (gate)
            {
                pending = store.SaveInBackgroundAsync(snapshot);
                new PlaybackStateStore(directory).Clear();
            }
            await pending;
            Assert(!File.Exists(Path.Combine(directory, PlaybackStateStore.FileName)), "background writer restored a signed-out account's state");
            lock (gate)
            {
                pending = store.SaveInBackgroundAsync(snapshot);
                store.Save(snapshot with { PositionSeconds = 33 });
            }
            await pending;
            Assert(store.Load("12345")?.PositionSeconds == 33, "an older periodic save overwrote the close snapshot");
        });
        await Run("Progressive playback: AES ranges preserve non-aligned counter offsets and reject invalid ranges", () =>
        {
            var data = new byte[600_041]; new Random(311).NextBytes(data);
            var cipher = data.ToArray(); const string key = "00112233445566778899aabbccddeeff";
            using (var encrypt = new AesCounter(key)) encrypt.Transform(cipher);
            foreach (var offset in new[] { 0, 1, 15, 16, 4095, 4096, 262_147 })
            {
                var decoded = cipher.AsSpan(offset, 2137).ToArray();
                using var decrypt = new AesCounter(key, offset); decrypt.Transform(decoded);
                Assert(decoded.SequenceEqual(data.AsSpan(offset, decoded.Length).ToArray()), "AES counter restarted at zero for a seek range");
            }
            Assert(ProgressiveAudio.TryRange("bytes=-17", 100, out var first, out var last) && first == 83 && last == 99, "suffix range differs");
            Assert(ProgressiveAudio.TryRange("bytes=31-", 100, out first, out last) && first == 31 && last == 99, "open range differs");
            foreach (var bad in new[] { "bytes=100-", "bytes=5-4", "bytes=0-1,4-5", "bytes=-0", "bytes=+1-4", "bytes=999999999999999999999999-" })
                Assert(!ProgressiveAudio.TryRange(bad, 100, out _, out _), "invalid range accepted");
            return Task.CompletedTask;
        });
        await Run("Audio buffer: ownership preserves an existing file and releases only completed temporary audio", async () =>
        {
            var directory = Path.Combine(_directory, "audio-buffer-file-ownership");
            Directory.CreateDirectory(directory);
            var existing = Path.Combine(directory, "existing.mp3");
            byte[] original = [1, 2, 3];
            File.WriteAllBytes(existing, original);
            await Throws<IOException>(() =>
            {
                using var collision = new BufferedAudioFile(existing, FileOptions.SequentialScan);
                return Task.CompletedTask;
            });

            var path = Path.Combine(directory, "temporary.mp3");
            using var file = new BufferedAudioFile(path, FileOptions.SequentialScan);
            byte[] audio = [4, 5, 6, 7];
            await file.Stream.WriteAsync(audio);
            await file.Stream.FlushAsync();
            file.Seal();
            using (var reader = File.OpenRead(file.Path))
            {
                var bytes = new byte[audio.Length];
                await reader.ReadExactlyAsync(bytes);
                Assert(bytes.SequenceEqual(audio), "completed temporary audio could not be opened for reading");
            }
            file.Dispose();
            file.Dispose();
            Assert(!File.Exists(path) && File.ReadAllBytes(existing).SequenceEqual(original), "temporary cleanup changed an unrelated file or left its own file");
        });
        await Run("Audio buffer: cleanup waits for a reader that does not share delete", async () =>
        {
            var directory = Path.Combine(_directory, "audio-buffer-active-reader");
            var path = Path.Combine(directory, "temporary.mp3");
            var existing = Path.Combine(directory, "existing.mp3");
            byte[] audio = [4, 5, 6, 7];
            using var file = new BufferedAudioFile(path, FileOptions.SequentialScan);
            File.WriteAllBytes(existing, audio);
            await file.Stream.WriteAsync(audio);
            await file.Stream.FlushAsync();
            file.Seal();
            using (var reader = File.OpenRead(path))
            {
                file.Dispose();
                file.Dispose();
                Assert(File.Exists(path) && reader.ReadByte() == audio[0], "closing the buffer affected the active reader");
            }
            await Until(() => !Directory.EnumerateFiles(directory).Contains(path));
            Assert(File.ReadAllBytes(existing).SequenceEqual(audio), "deferred cleanup modified an unrelated file");
        });
        await CheckAudioResponseTypes();
        await CheckAudioRepresentations();
        await CheckAudioRangeRetries();
        await CheckUnavailableAudioRanges();
        await Run("Progressive playback: loopback serves decrypted seeks and rejects foreign Host and invalid capabilities", async () =>
        {
            var directory = Path.Combine(_directory, "progressive-ranges"); Directory.CreateDirectory(directory);
            var data = new byte[1_400_003]; new Random(311).NextBytes(data);
            var cipher = data.ToArray(); const string key = "00112233445566778899aabbccddeeff";
            using (var encrypt = new AesCounter(key)) encrypt.Transform(cipher);
            var requests = new ConcurrentBag<long>();
            using var buffer = new AudioBuffer(directory, new FakeHandler((request, token) =>
            {
                Assert(request.Headers.Authorization is null && !request.Headers.Contains("Cookie"), "CDN received credentials");
                return Task.FromResult(ProgressiveResponse(request, cipher, requests));
            }));
            using var input = await buffer.OpenPlaybackAsync(new(new Uri("https://s1.vlx.yandex.net/progressive.m4a"), "aac-mp4", 192, key), CancellationToken.None);
            Assert(input.Progressive && input.Uri.IsLoopback && input.Uri.Scheme == "http", "input did not use a private loopback resource");
            using var http = new HttpClient();
            using (var get = new HttpRequestMessage(HttpMethod.Get, input.Uri))
            {
                get.Headers.Range = new RangeHeaderValue(1_000_003, 1_000_043);
                get.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "fake-never-forward");
                using var response = await http.SendAsync(get);
                Assert(response.StatusCode == HttpStatusCode.PartialContent && (await response.Content.ReadAsByteArrayAsync()).SequenceEqual(data.AsSpan(1_000_003, 41).ToArray()), "seek returned wrong decrypted bytes");
            }
            using (var wrongHost = new HttpRequestMessage(HttpMethod.Get, input.Uri))
            {
                wrongHost.Headers.Host = "foreign.invalid";
                using var response = await http.SendAsync(wrongHost);
                Assert(response.StatusCode == HttpStatusCode.Forbidden, "DNS rebinding Host accepted");
            }
            using (var response = await http.GetAsync(new Uri(input.Uri, "/audio/not-the-capability.m4a")))
                Assert(response.StatusCode == HttpStatusCode.NotFound, "unknown capability exposed media");
            using (var invalid = new HttpRequestMessage(HttpMethod.Get, input.Uri))
            {
                invalid.Headers.TryAddWithoutValidation("Range", "bytes=0-1,4-5");
                using var response = await http.SendAsync(invalid);
                Assert(response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable, "multipart range accepted");
            }
            await input.Completion.WaitAsync(TimeSpan.FromSeconds(5));
            Assert(File.ReadAllBytes(input.Path).SequenceEqual(data), "complete progressive cache differs from plaintext");
            var uri = input.Uri; input.Dispose();
            await Until(() => !File.Exists(input.Path));
            try { await http.GetAsync(uri); throw new InvalidOperationException("closed playback resource remains accessible"); }
            catch (HttpRequestException) { }
        });
        await Run("Progressive playback: closing the input during its final range releases its cache", async () =>
        {
            var directory = Path.Combine(_directory, "progressive-close-at-completion");
            var data = new byte[ProgressiveAudio.ChunkBytes + 1];
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var requests = new ConcurrentBag<long>();
            using var buffer = new AudioBuffer(directory, new FakeHandler(async (request, _) =>
            {
                if (request.Headers.Range!.Ranges.Single().From > 0)
                {
                    entered.TrySetResult();
                    await release.Task;
                }
                return ProgressiveResponse(request, data, requests);
            }));
            using var input = await buffer.OpenPlaybackAsync(new(new Uri("https://s1.vlx.yandex.net/progressive.mp3"), "mp3", 320), default);
            try
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                release.TrySetResult();
                input.Dispose();
                try { await input.Completion.WaitAsync(TimeSpan.FromSeconds(5)); }
                catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException) { }
                Assert(!File.Exists(input.Path), "final range retained the disposed progressive cache");
            }
            finally { release.TrySetResult(); }
        });
        await Run("Progressive playback: actual Windows MP3 starts before the remaining CDN bytes arrive and seeks correctly", async () =>
        {
            var directory = Path.Combine(_directory, "progressive-native"); Directory.CreateDirectory(directory);
            var wave = WriteWave("progressive-native-60s.wav", 60);
            var mp3 = Path.Combine(directory, "synthetic.mp3");
            var target = await (await StorageFolder.GetFolderFromPathAsync(directory)).CreateFileAsync("synthetic.mp3", CreationCollisionOption.FailIfExists);
            var encoding = await new MediaTranscoder { HardwareAccelerationEnabled = false }.PrepareFileTranscodeAsync(
                await StorageFile.GetFileFromPathAsync(wave), target, MediaEncodingProfile.CreateMp3(AudioEncodingQuality.High));
            Assert(encoding.CanTranscode, "Windows could not generate the MP3 fixture");
            await encoding.TranscodeAsync();
            var data = await File.ReadAllBytesAsync(mp3);
            Assert(data.Length > ProgressiveAudio.ChunkBytes * 2, "fixture is too small to prove progressive startup");
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var ranges = new ConcurrentBag<long>();
            using var player = new NativePlayer(new AudioBuffer(Path.Combine(directory, "buffer"), new FakeHandler(async (request, token) =>
            {
                var first = request.Headers.Range!.Ranges.Single().From!.Value;
                // The first frames and trailing tags can be read, but the middle
                // of the track is deliberately unavailable until playback opens.
                if (first > 0 && first < data.Length - ProgressiveAudio.ChunkBytes)
                { entered.TrySetResult(); await gate.Task.WaitAsync(token); }
                return ProgressiveResponse(request, data, ranges);
            })));
            player.Volume = 0;
            var clock = Stopwatch.StartNew();
            try
            {
                await player.OpenAsync(new AudioDescriptor(new Uri("https://s1.vlx.yandex.net/synthetic.mp3"), "mp3", 320), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(12));
                Assert(player.IsPlaying && !gate.Task.IsCompleted && player.Duration.TotalSeconds > 55, "native playback waited for a complete file");
                var startMs = clock.Elapsed.TotalMilliseconds;
                gate.TrySetResult();
                player.Seek(TimeSpan.FromSeconds(43));
                await Until(() => player.Position.TotalSeconds >= 42);
                var nextSource = new Uri("https://s1.vlx.yandex.net/synthetic-next.mp3");
                await player.PrepareNextAsync(new AudioDescriptor(nextSource, "mp3", 320), CancellationToken.None);
                var prepared = typeof(NativePlayer).GetField("_prepared", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(player)!;
                var network = (Windows.Media.Playback.MediaPlayer)prepared.GetType().GetField("_network", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(prepared)!;
                Assert(network.PlaybackSession.PlaybackState != Windows.Media.Playback.MediaPlaybackState.Playing
                    && network.PlaybackSession.Position == TimeSpan.Zero, "online preload started before promotion");
                var path = player.PreparedLocalPath;
                Assert(player.TryPlayPrepared(nextSource, TimeSpan.FromSeconds(.5)) && player.SourceLocalPath == path
                    && player.IsPlaying && !player.HasPrepared, "online promotion reopened or lost its prepared decoder");
                await Task.Delay(80);
                Assert(player.Position.TotalSeconds < 2 && player.FadingLocalPath is not null, "online crossfade clock or source promotion failed");
                File.WriteAllText(Path.Combine(directory, "startup.json"), JsonSerializer.Serialize(new { startMs, fullFileWasBlockedAtStart = true, seekSeconds = player.Position.TotalSeconds, mediaBytes = data.Length, requests = ranges.Count }));
            }
            finally
            {
                File.WriteAllText(Path.Combine(directory, "native-observation.json"), JsonSerializer.Serialize(new { hasSource = player.HasSource, nativeError = player.LastNativeError, requestedChunks = ranges.ToArray(), mediaBytes = data.Length, openedMs = clock.Elapsed.TotalMilliseconds }));
                gate.TrySetResult(); player.Stop();
            }
            await Until(() => !Directory.EnumerateFiles(Path.Combine(directory, "buffer"), "ym-audio-*").Any());
            Assert(File.Exists(wave) && File.Exists(mp3), "closing progressive playback deleted a user/fixture source");
        });
        await Run("Playback persistence: explicit Play resumes indexed offline audio at the saved position without network", async () =>
        {
            var directory = Path.Combine(_directory, "persistence-native-resume"); Directory.CreateDirectory(directory);
            var music = Path.Combine(directory, "music"); Directory.CreateDirectory(music);
            var wave = WriteWave("persistence-resume-source.wav", 30);
            var file = Path.Combine(music, "indexed.mp3"); File.Copy(wave, file);
            var bytes = await File.ReadAllBytesAsync(file);
            var row = new TrackRow { Id = "101", RequestId = "101", Title = "Resume fixture", Artist = "Fixture", DurationMs = 30000, Available = true };
            var item = new OfflineItem("101", "101", row.Title, row.Artist, "", 30000, "indexed.mp3", "mp3", 320, bytes.Length, Convert.ToHexString(SHA256.HashData(bytes)), DateTimeOffset.UtcNow);
            File.WriteAllText(Path.Combine(music, "library.index.json"), JsonSerializer.Serialize(new[] { item }));
            using var offline = new OfflineLibrary(music);
            using var api = new MusicApi(new FakeHandler((_, _) => throw new InvalidOperationException("offline resume requested network")));
            var queue = new PlaybackQueue(); queue.SetItems([row]); new PlaybackStateStore(directory).Save(PlaybackStateStore.Capture("12345", queue, 17.25));
            using var player = new NativePlayer(new AudioBuffer(Path.Combine(directory, "buffer"))); player.Volume = 0;
            var window = new MainWindow(api: api, store: new SessionStore(directory), restore: true, diagnosticsDirectory: directory, offline: offline, player: player, preferences: new PreferencesStore(Path.Combine(directory, "preferences")));
            try
            {
                PersistenceField(window, "_account", new Account("12345", "Fixture", true));
                PersistenceInvoke(window, "RestorePlaybackState", "12345");
                Assert(!player.HasSource && !player.IsPlaying, "restore itself began playing");
                await Call(window, "TogglePlaybackAsync");
                await Until(() => player.Position.TotalSeconds >= 16.5);
                Assert(player.IsPlaying && player.SourceLocalPath == file, "Play did not resume the indexed file at the stored position");
            }
            finally { window.Close(); }
            Assert(new PlaybackStateStore(directory).Load("12345")?.PositionSeconds is >= 17 and < 20, "close lost the resumed playback position");
            Assert(File.Exists(file), "closing the resumed source removed downloaded audio");
        });
        await Run("Progressive playback: Windows M4A and FLAC open before full download with encrypted range seeks", async () =>
        {
            var directory = Path.Combine(_directory, "progressive-codecs"); Directory.CreateDirectory(directory);
            var wave = WriteWave("progressive-codecs-noise.wav", 40);
            using (var output = new FileStream(wave, FileMode.Open, FileAccess.Write))
            using (var writer = new BinaryWriter(output))
            {
                output.Position = 44;
                var random = new Random(311);
                for (var frame = 0; frame < 44100 * 40; frame++) writer.Write((short)random.Next(-12000, 12000));
            }
            var results = new List<object>();
            foreach (var (extension, codec, profile) in new[] {
                ("m4a", "aac-mp4", MediaEncodingProfile.CreateM4a(AudioEncodingQuality.High)),
                ("flac", "flac", MediaEncodingProfile.CreateFlac(AudioEncodingQuality.High)) })
            {
                var file = await (await StorageFolder.GetFolderFromPathAsync(directory)).CreateFileAsync("synthetic." + extension, CreationCollisionOption.FailIfExists);
                var encode = await new MediaTranscoder { HardwareAccelerationEnabled = false }.PrepareFileTranscodeAsync(await StorageFile.GetFileFromPathAsync(wave), file, profile);
                Assert(encode.CanTranscode, "Windows could not create codec fixture: " + extension);
                await encode.TranscodeAsync();
                var data = await File.ReadAllBytesAsync(file.Path);
                Assert(data.Length > ProgressiveAudio.ChunkBytes * 2, "compressed fixture is too short");
                const string key = "00112233445566778899aabbccddeeff";
                var cipher = data.ToArray(); using (var aes = new AesCounter(key)) aes.Transform(cipher);
                var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var ranges = new ConcurrentBag<long>();
                using var player = new NativePlayer(new AudioBuffer(Path.Combine(directory, extension + "-buffer"), new FakeHandler(async (request, token) =>
                {
                    var first = request.Headers.Range!.Ranges.Single().From!.Value;
                    if (first > 0 && first < cipher.Length - ProgressiveAudio.ChunkBytes) await gate.Task.WaitAsync(token);
                    return ProgressiveResponse(request, cipher, ranges);
                })));
                player.Volume = 0;
                var watch = Stopwatch.StartNew();
                try
                {
                    await player.OpenAsync(new AudioDescriptor(new Uri("https://s1.vlx.yandex.net/synthetic." + extension), codec, 192, key), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(12));
                    Assert(player.IsPlaying && !gate.Task.IsCompleted && player.Duration.TotalSeconds > 35, extension + " waited for complete data");
                    var startupMs = watch.Elapsed.TotalMilliseconds;
                    gate.TrySetResult();
                    player.Seek(TimeSpan.FromSeconds(25));
                    await Until(() => player.Position.TotalSeconds >= 24);
                    results.Add(new { codec, startupMs, fullFileBlocked = true, encrypted = true, seekSeconds = player.Position.TotalSeconds });
                }
                finally { gate.TrySetResult(); player.Stop(); }
            }
            File.WriteAllText(Path.Combine(directory, "startup-codecs.json"), JsonSerializer.Serialize(results));
        });
        await Run("Normalization UI: settings save cancel and attenuation preserve master volume and local audio", async () =>
        {
            using var fixture = new SaFixture("normalization-ui"); await fixture.Activate();
            var preferences = new PreferencesStore(Path.Combine(_directory, "normalization-settings"));
            typeof(MainWindow).GetField("_preferenceStore", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(fixture.Window, preferences);
            var player = SaField<NativePlayer>(fixture.Window, "_player");
            Assert(!player.NormalizeVolume, "normalization changed the default playback path");
            SaInvoke(fixture.Window, "Settings_Click", null!, null!);
            Control<CheckBox>(fixture.Window, "SettingsNormalizeVolume").IsChecked = true;
            SaClick(Control<Button>(fixture.Window, "SettingsBackButton"));
            Assert(player.NormalizeVolume && preferences.Load().NormalizeVolume, "Back rolled back immediate normalization");
            SaInvoke(fixture.Window, "Settings_Click", null!, null!);
            Control<CheckBox>(fixture.Window, "SettingsNormalizeVolume").IsChecked = true;
            SaClick(Control<Button>(fixture.Window, "SettingsBackButton"));
            Assert(player.NormalizeVolume && preferences.Load().NormalizeVolume, "normalization choice was not applied and saved");
            var path = WriteWave("normalization-player.wav", 3); var before = SHA256.HashData(File.ReadAllBytes(path));
            var master = .7; player.Volume = 0;
            await player.OpenAsync(new Uri(path), CancellationToken.None);
            PreloadSlot(player, "_current")!.IsMuted = true;
            player.Volume = master;
            await Until(() => !player.IsAnalyzingLoudness);
            Assert(player.CurrentNormalizationGain is > 0 and <= 1 && player.Volume == master, "normalization altered master volume or amplified audio");
            player.SetNormalization(false); player.Stop();
            Assert(SHA256.HashData(File.ReadAllBytes(path)).SequenceEqual(before), "normalization rewrote local audio");
        });
        foreach (var fade in new[] { 0d, .1d })
        await Run($"Normalization promotion: an unfinished gain ramp settles after {fade:0.0}s crossfade", async () =>
        {
            var directory = Path.Combine(_directory, "normalization-promotion-" + (int)(fade * 1000)); Directory.CreateDirectory(directory);
            var first = LoudnessWave(directory, "first", 1, 48000, (.8, 8d));
            var second = LoudnessWave(directory, "second", 1, 48000, (.8, 8d));
            using var player = new NativePlayer(new AudioBuffer(Path.Combine(directory, "buffer")));
            player.Volume = 0; player.SetNormalization(true);
            await player.OpenPausedAsync(new Uri(first), default);
            PreloadSlot(player, "_current")!.IsMuted = true; player.Toggle();
            await Until(() => !player.IsAnalyzingLoudness && player.CurrentNormalizationGain < .3);
            await Task.Delay(850);
            await player.PrepareNextAsync(new Uri(second), default);
            var slot = typeof(NativePlayer).GetField("_prepared", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(player)!;
            var measuredGain = slot.GetType().GetField("MeasuredGain", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            var next = PreloadSlot(player, "_prepared")!; next.IsMuted = true;
            await Until(() => (double)measuredGain.GetValue(slot)! < .3);
            player.Volume = .8;
            Assert(player.TryPlayPrepared(new Uri(second), TimeSpan.FromSeconds(fade)), "prepared promotion failed");
            Assert(player.CurrentNormalizationGain > (double)measuredGain.GetValue(slot)! + .1, "fixture promoted after the ramp had already settled");
            await Task.Delay(1000);
            var expected = player.Volume * player.CurrentNormalizationGain;
            Assert(expected < .3 && Math.Abs(next.Volume - expected) < .015 && player.Volume == .8,
                "native volume froze before normalization finished after promotion");
            player.Stop();
            Assert(File.Exists(first) && File.Exists(second), "promotion deleted a local source");
        });
        await Run("Normalization playback: measured attenuation composes with crossfade and disabling restores unchanged master gain", async () =>
        {
            var directory = Path.Combine(_directory, "normalization-native"); Directory.CreateDirectory(directory);
            var first = LoudnessWave(directory, "loud-first", 1, 48000, (.8, 8d));
            var second = LoudnessWave(directory, "loud-second", 1, 48000, (.8, 8d));
            using var player = new NativePlayer(new AudioBuffer(Path.Combine(directory, "buffer")));
            player.Volume = 0; player.SetNormalization(true);
            await player.OpenPausedAsync(new Uri(first), default);
            var current = PreloadSlot(player, "_current")!; current.IsMuted = true; player.Toggle();
            await Until(() => player.CurrentNormalizationGain < .3);
            await player.PrepareNextAsync(new Uri(second), default);
            var next = PreloadSlot(player, "_prepared")!; next.IsMuted = true;
            await Task.Delay(900);
            player.Volume = .8;
            Assert(player.TryPlayPrepared(new Uri(second), TimeSpan.FromSeconds(1)), "normalized prepared track could not be promoted");
            await Task.Delay(120);
            Assert(next.Volume >= 0 && current.Volume >= 0 && next.Volume + current.Volume < .3
                && player.Volume == .8, "crossfade discarded attenuation or multiplied master volume twice");
            player.SetNormalization(false);
            await Until(() => Math.Abs(player.CurrentNormalizationGain - 1) < .001);
            Assert(player.Volume == .8, "disabling normalization rewrote master volume");
            player.Stop();
            var cache = File.ReadAllText(Path.Combine(directory, "buffer/loudness/loudness-v1.json"));
            Assert(!cache.Contains(directory, StringComparison.OrdinalIgnoreCase) && !cache.Contains("http", StringComparison.OrdinalIgnoreCase), "loudness cache persisted source locations");
        });
    }
    private static async Task CheckAudioResponseTypes()
    {
        await Run("Audio buffer: text and structured error MIME types are rejected across download and playback", async () =>
        {
            var directory = Path.Combine(_directory, "audio-buffer-content-type");
            var audio = new AudioDescriptor(new Uri("https://s1.vlx.yandex.net/content-type.mp3"), "mp3", 320);
            byte[] bytes = [1, 2, 3, 4];
            foreach (var type in new[] { "text/html; charset=utf-8", "application/json", "application/problem+json", "application/xml", "application/xhtml+xml" })
            {
                var fullHandler = new FakeHandler((_, _) => Task.FromResult(Respond(HttpStatusCode.OK, type)));
                using var full = new AudioBuffer(directory, fullHandler);
                await Throws<MusicException>(() => full.DownloadAsync(audio, default));
                await Throws<MusicException>(() => full.OpenPlaybackAsync(audio, default));
                var partialHandler = new FakeHandler((_, _) => Task.FromResult(Respond(HttpStatusCode.PartialContent, type)));
                using var partial = new AudioBuffer(directory, partialHandler);
                await Throws<MusicException>(() => partial.OpenPlaybackAsync(audio, default));
                await Throws<MusicException>(() => partial.ReadPlaybackRangeAsync(audio, 0, 3, 4, default));
                Assert(fullHandler.Seen.Count == 2 && partialHandler.Seen.Count == 2 && !Directory.Exists(directory),
                    "text MIME caused retries or created an audio buffer");
            }

            foreach (var type in new[] { null, "audio/mpeg", "application/octet-stream" })
            {
                using var full = new AudioBuffer(directory, new FakeHandler((_, _) => Task.FromResult(Respond(HttpStatusCode.OK, type))));
                using var file = await full.DownloadAsync(audio, default);
                using var input = await full.OpenPlaybackAsync(audio, default);
                using var partial = new AudioBuffer(directory, new FakeHandler((_, _) => Task.FromResult(Respond(HttpStatusCode.PartialContent, type))));
                Assert(File.ReadAllBytes(file.Path).SequenceEqual(bytes) && File.ReadAllBytes(input.Path).SequenceEqual(bytes)
                    && (await partial.ReadPlaybackRangeAsync(audio, 0, 3, 4, default)).SequenceEqual(bytes), "an ordinary audio MIME type was rejected");
            }

            HttpResponseMessage Respond(HttpStatusCode status, string? type)
            {
                var body = new ByteArrayContent(bytes);
                if (type is not null) body.Headers.ContentType = MediaTypeHeaderValue.Parse(type);
                if (status == HttpStatusCode.PartialContent) body.Headers.ContentRange = new ContentRangeHeaderValue(0, 3, 4);
                return new(status) { Content = body };
            }
        });
    }

    private static async Task CheckAudioRepresentations()
    {
        var audio = new AudioDescriptor(new Uri("https://s1.vlx.yandex.net/representation.mp3"), "mp3", 320);
        await Run("Audio buffer: requests identity and rejects encoded full and partial representations", async () =>
        {
            var directory = Path.Combine(_directory, "audio-buffer-content-encoding");
            var requests = 0;
            var compressed = new FakeHandler((request, _) =>
            {
                Assert(request.Headers.AcceptEncoding.Single().Value == "identity", "audio GET did not request identity encoding");
                var body = new ByteArrayContent([1, 2, 3, 4]);
                body.Headers.ContentEncoding.Add("gzip");
                var status = ++requests <= 2 ? HttpStatusCode.OK : HttpStatusCode.PartialContent;
                if (status == HttpStatusCode.PartialContent) body.Headers.ContentRange = new ContentRangeHeaderValue(0, 3, 4);
                return Task.FromResult(new HttpResponseMessage(status) { Content = body });
            });
            using (var buffer = new AudioBuffer(directory, compressed))
            {
                await Throws<MusicException>(() => buffer.DownloadAsync(audio, default));
                await Throws<MusicException>(() => buffer.OpenPlaybackAsync(audio, default));
                await Throws<MusicException>(() => buffer.OpenPlaybackAsync(audio, default));
                await Throws<MusicException>(() => buffer.ReadPlaybackRangeAsync(audio, 0, 3, 4, default));
            }
            Assert(requests == 4 && !Directory.Exists(directory), "encoded audio was buffered or retried");

            using var identity = new AudioBuffer(directory, new FakeHandler((request, _) =>
            {
                Assert(request.Headers.AcceptEncoding.Single().Value == "identity", "audio GET did not request identity encoding");
                var body = new ByteArrayContent([1, 2, 3, 4]);
                body.Headers.ContentEncoding.Add("Identity");
                body.Headers.ContentRange = new ContentRangeHeaderValue(0, 3, 4);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = body });
            }));
            Assert((await identity.ReadPlaybackRangeAsync(audio, 0, 3, 4, default)).Length == 4, "explicit identity was rejected");
        });

        await Run("Progressive playback: strong validators reject changed audio and tolerate missing or weak tags", async () =>
        {
            var directory = Path.Combine(_directory, "progressive-representation-validator");
            const int chunk = ProgressiveAudio.ChunkBytes;
            var media = new byte[2 * chunk + 13]; new Random(44).NextBytes(media);
            foreach (var scenario in new[]
            {
                (First: "\"one\"", Middle: (string?)null, Last: "\"two\"", Compatible: false),
                (First: (string?)null, Middle: "\"one\"", Last: "\"two\"", Compatible: false),
                (First: "\"one\"", Middle: "W/\"two\"", Last: (string?)null, Compatible: true)
            })
            {
                var requests = new ConcurrentBag<long>();
                using var buffer = new AudioBuffer(directory, new FakeHandler((request, _) =>
                {
                    var start = request.Headers.Range!.Ranges.Single().From!.Value;
                    var expected = start == chunk ? scenario.First : start == 2 * chunk ? "\"one\"" : null;
                    Assert(request.Headers.IfRange?.EntityTag?.Tag == expected, "If-Range did not use the known strong validator");
                    var response = ProgressiveResponse(request, media, requests);
                    var tag = start == 0 ? scenario.First : start == chunk ? scenario.Middle : scenario.Last;
                    if (tag is not null) response.Headers.ETag = EntityTagHeaderValue.Parse(tag);
                    return Task.FromResult(response);
                }));
                using var input = await buffer.OpenPlaybackAsync(audio, default);
                if (scenario.Compatible)
                {
                    await input.Completion.WaitAsync(TimeSpan.FromSeconds(5));
                    Assert(File.ReadAllBytes(input.Path).SequenceEqual(media), "missing or weak validators changed ordinary playback");
                }
                else await Throws<MusicException>(() => input.Completion.WaitAsync(TimeSpan.FromSeconds(5)));
                Assert(requests.Count == 3, "a representation mismatch was retried");
            }
        });
    }

    private static async Task CheckAudioRangeRetries()
    {
        var audio = new AudioDescriptor(new Uri("https://s1.vlx.yandex.net/retry.mp3"), "mp3", 320);
        await Run("Progressive playback: the first range recovers from one disconnect or short read", async () =>
        {
            var directory = Path.Combine(_directory, "progressive-first-range-retry");
            byte[] data = [1, 2, 3, 4, 5];
            foreach (var disconnect in new[] { true, false })
            {
                var attempts = 0;
                var ranges = new ConcurrentBag<long>();
                using var buffer = new AudioBuffer(directory, new FakeHandler((request, _) =>
                {
                    if (++attempts != 1) return Task.FromResult(ProgressiveResponse(request, data, ranges));
                    if (disconnect) throw new HttpRequestException(HttpRequestError.ConnectionError, "fixture connection closed");
                    var shortBody = new ByteArrayContent(data[..2]);
                    shortBody.Headers.ContentLength = data.Length;
                    shortBody.Headers.ContentRange = new ContentRangeHeaderValue(0, data.Length - 1, data.Length);
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = shortBody });
                }));
                using var input = await buffer.OpenPlaybackAsync(audio, default);
                await input.Completion.WaitAsync(TimeSpan.FromSeconds(5));
                Assert(attempts == 2 && input.Progressive && File.ReadAllBytes(input.Path).SequenceEqual(data), "the initial range did not recover");
            }
        });

        await Run("Audio buffer: complete responses reject Content-Range and partial responses require bytes", async () =>
        {
            var directory = Path.Combine(_directory, "audio-buffer-range-metadata");
            foreach (var header in new[] { "bytes 0-3/4", "bytes" })
            {
                var handler = new FakeHandler((_, _) =>
                {
                    var body = new ByteArrayContent([1, 2, 3, 4]);
                    Assert(body.Headers.TryAddWithoutValidation("Content-Range", header), "fixture could not supply the response header");
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = body });
                });
                using var buffer = new AudioBuffer(directory, handler);
                await Throws<MusicException>(() => buffer.DownloadAsync(audio, default));
                await Throws<MusicException>(() => buffer.OpenPlaybackAsync(audio, default));
                Assert(handler.Seen.Count == 2, "a complete response with Content-Range was retried");
            }
            var partial = new FakeHandler((_, _) =>
            {
                var body = new ByteArrayContent([1, 2, 3, 4]);
                body.Headers.ContentRange = new ContentRangeHeaderValue(0, 3, 4) { Unit = "items" };
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = body });
            });
            using (var buffer = new AudioBuffer(directory, partial))
            {
                await Throws<MusicException>(() => buffer.OpenPlaybackAsync(audio, default));
                await Throws<MusicException>(() => buffer.ReadPlaybackRangeAsync(audio, 0, 3, 4, default));
            }
            Assert(partial.Seen.Count == 2 && !Directory.Exists(directory), "a non-byte range was published or retried");
        });

        await Run("Progressive playback: a short read is retried once for all readers of the range", async () =>
        {
            var directory = Path.Combine(_directory, "progressive-range-retry");
            const int start = ProgressiveAudio.ChunkBytes;
            var data = new byte[start + 31]; new Random(317).NextBytes(data);
            var end = data.Length - 1;
            var requests = new ConcurrentBag<long>();
            var laterAttempts = 0;
            using var buffer = new AudioBuffer(directory, new FakeHandler((request, _) =>
            {
                if (request.Headers.Range!.Ranges.Single().From == start && Interlocked.Increment(ref laterAttempts) == 1)
                {
                    var shortBody = new ByteArrayContent(data.AsSpan(start, 7).ToArray());
                    shortBody.Headers.ContentRange = new ContentRangeHeaderValue(start, end, data.Length);
                    shortBody.Headers.ContentLength = data.Length - start;
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = shortBody });
                }
                return Task.FromResult(ProgressiveResponse(request, data, requests));
            }));
            using var input = await buffer.OpenPlaybackAsync(audio, default);
            using var http = new HttpClient();
            var reads = await Task.WhenAll(ReadLastChunkAsync(), ReadLastChunkAsync()).WaitAsync(TimeSpan.FromSeconds(5));
            await input.Completion.WaitAsync(TimeSpan.FromSeconds(5));
            var expected = data[start..];
            Assert(laterAttempts == 2 && reads.All(bytes => bytes.SequenceEqual(expected))
                && File.ReadAllBytes(input.Path).SequenceEqual(data), "short read was not retried as a shared range fetch");

            async Task<byte[]> ReadLastChunkAsync()
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, input.Uri);
                request.Headers.Range = new RangeHeaderValue(start, end);
                using var response = await http.SendAsync(request);
                Assert(response.StatusCode == HttpStatusCode.PartialContent, "loopback did not return the recovered range");
                return await response.Content.ReadAsByteArrayAsync();
            }
        });

        await Run("Progressive playback: retries stop after one transport failure and skip protocol errors and cancellation", async () =>
        {
            var directory = Path.Combine(_directory, "progressive-range-retry-limits");
            var transport = new FakeHandler((_, _) => throw new HttpRequestException(HttpRequestError.ConnectionError, "fixture connection closed"));
            using (var buffer = new AudioBuffer(directory, transport))
            {
                var failure = await Throws<AudioBuffer.RangeUnavailableException>(() => buffer.ReadPlaybackRangeAsync(audio, 16, 19, 20, default));
                Assert(failure.InnerException is HttpRequestException, "transport failure lost its cause");
            }
            Assert(transport.Seen.Count == 2, "transport failure did not stop after the retry");

            var protocol = new FakeHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.RequestedRangeNotSatisfiable)));
            using (var buffer = new AudioBuffer(directory, protocol))
                await Throws<MusicException>(() => buffer.ReadPlaybackRangeAsync(audio, 16, 19, 20, default));
            Assert(protocol.Seen.Count == 1, "rejected range was retried");

            using var cancellation = new CancellationTokenSource();
            var canceled = new FakeHandler((_, token) =>
            {
                cancellation.Cancel();
                return Task.FromCanceled<HttpResponseMessage>(token);
            });
            using (var buffer = new AudioBuffer(directory, canceled))
                await Throws<OperationCanceledException>(() => buffer.ReadPlaybackRangeAsync(audio, 16, 19, 20, cancellation.Token));
            Assert(canceled.Seen.Count == 1, "canceled range was retried");
        });
    }

    private static async Task CheckUnavailableAudioRanges()
    {
        var audio = new AudioDescriptor(new Uri("https://s1.vlx.yandex.net/busy.mp3"), "mp3", 320);
        await Run("Progressive playback: late HTTP 503 honors Retry-After, cancellation and retry budget", async () =>
        {
            var directory = Path.Combine(_directory, "progressive-busy-range-policy");
            byte[] data = [1, 2, 3, 4];
            var ranges = new ConcurrentBag<long>();
            var attempts = 0;
            var watch = Stopwatch.StartNew();
            using (var buffer = new AudioBuffer(directory, new FakeHandler((request, _) => Task.FromResult(++attempts == 1
                ? RangeUnavailable(TimeSpan.FromSeconds(1)) : ProgressiveResponse(request, data, ranges)))))
            {
                var result = await buffer.ReadPlaybackRangeAsync(audio, 0, 3, 4, default).WaitAsync(TimeSpan.FromSeconds(5));
                Assert(attempts == 2 && watch.Elapsed >= TimeSpan.FromMilliseconds(950) && result.SequenceEqual(data), "Retry-After was ignored");
            }

            var delayed = new FakeHandler((_, _) => Task.FromResult(RangeUnavailable(TimeSpan.FromSeconds(60))));
            using (var buffer = new AudioBuffer(directory, delayed))
                await Throws<AudioBuffer.RangeUnavailableException>(() => buffer.ReadPlaybackRangeAsync(audio, 0, 3, 4, default).WaitAsync(TimeSpan.FromSeconds(3)));
            Assert(delayed.Seen.Count == 1, "a long Retry-After caused an inline retry");

            using var cancellation = new CancellationTokenSource();
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var canceled = new FakeHandler((_, _) =>
            {
                entered.TrySetResult();
                return Task.FromResult(RangeUnavailable(TimeSpan.FromSeconds(2)));
            });
            using (var buffer = new AudioBuffer(directory, canceled))
            {
                var pending = buffer.ReadPlaybackRangeAsync(audio, 0, 3, 4, cancellation.Token);
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
                cancellation.Cancel();
                await Throws<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(3)));
            }
            Assert(canceled.Seen.Count == 1, "cancellation sent another request during Retry-After");
        });

        await Run("Progressive playback: later readers share recovery and resume prefetch after exhausted transient failures", async () =>
        {
            var directory = Path.Combine(_directory, "progressive-busy-range-recovery");
            const int start = ProgressiveAudio.ChunkBytes;
            var media = new byte[2 * start + 13]; new Random(815).NextBytes(media);
            foreach (var failure in new[] { "503", "disconnect", "short", "timeout" })
            {
                var requests = new ConcurrentBag<long>();
                var attempts = 0;
                using var buffer = new AudioBuffer(directory, new FakeHandler(async (request, token) =>
                {
                    if (request.Headers.Range!.Ranges.Single().From != start || Interlocked.Increment(ref attempts) > 2)
                        return ProgressiveResponse(request, media, requests);
                    if (failure == "503") return RangeUnavailable();
                    if (failure == "disconnect") throw new HttpRequestException(HttpRequestError.ConnectionError, "fixture disconnect");
                    if (failure == "timeout") await Task.Delay(Timeout.InfiniteTimeSpan, token);
                    var body = new ByteArrayContent(media.AsSpan(start, 5).ToArray());
                    body.Headers.ContentLength = start;
                    body.Headers.ContentRange = new ContentRangeHeaderValue(start, 2 * start - 1, media.Length);
                    return new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = body };
                }), failure == "timeout" ? TimeSpan.FromMilliseconds(350) : null);
                using var input = await buffer.OpenPlaybackAsync(audio, default);
                using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false });
                await Throws<HttpRequestException>(() => ReadAsync().WaitAsync(TimeSpan.FromSeconds(5)));
                await Task.Delay(650);
                Assert(attempts == 2 && !input.Completion.IsCompleted, "prefetch retried on its own or abandoned the file");

                var result = await Task.WhenAll(ReadAsync(), ReadAsync()).WaitAsync(TimeSpan.FromSeconds(5));
                await input.Completion.WaitAsync(TimeSpan.FromSeconds(5));
                Assert(attempts == 3 && result.All(bytes => bytes.SequenceEqual(media[start..(2 * start)]))
                    && requests.Count(at => at == 2 * start) == 1 && File.ReadAllBytes(input.Path).SequenceEqual(media),
                    "recovery did not coalesce, resume prefetch and seal the complete file");

                async Task<byte[]> ReadAsync()
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, input.Uri);
                    request.Headers.Range = new RangeHeaderValue(start, 2 * start - 1);
                    using var response = await http.SendAsync(request);
                    Assert(response.StatusCode == HttpStatusCode.PartialContent, "recovered audio did not return a range");
                    return await response.Content.ReadAsByteArrayAsync();
                }
            }
        });

        await Run("Progressive playback: closing a source cancels a waiting recovery", async () =>
        {
            var directory = Path.Combine(_directory, "progressive-recovery-close");
            var media = new byte[ProgressiveAudio.ChunkBytes + 13];
            var requests = new ConcurrentBag<long>();
            var blocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var attempts = 0;
            using var buffer = new AudioBuffer(directory, new FakeHandler((request, _) =>
            {
                if (request.Headers.Range!.Ranges.Single().From == 0)
                    return Task.FromResult(ProgressiveResponse(request, media, requests));
                Interlocked.Increment(ref attempts);
                blocked.TrySetResult();
                return Task.FromResult(RangeUnavailable(TimeSpan.FromSeconds(60)));
            }));
            using var input = await buffer.OpenPlaybackAsync(audio, default);
            await blocked.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert(!input.Completion.IsCompleted, "temporary failure prematurely finished the buffer");
            input.Dispose();
            await Throws<OperationCanceledException>(() => input.Completion.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert(attempts == 1 && !File.Exists(input.Path), "closing attempted another fetch or retained the buffer");
        });
    }

    private static HttpResponseMessage RangeUnavailable(TimeSpan? delay = null)
    {
        var response = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        if (delay is { } wait) response.Headers.RetryAfter = new RetryConditionHeaderValue(wait);
        return response;
    }

    private static HttpResponseMessage ProgressiveResponse(HttpRequestMessage request, byte[] bytes, ConcurrentBag<long> requests)
    {
        var range = request.Headers.Range?.Ranges.Single() ?? throw new InvalidOperationException("CDN playback request lacks a bounded range");
        var first = range.From!.Value; var last = Math.Min(range.To!.Value, bytes.Length - 1);
        requests.Add(first);
        var content = new ByteArrayContent(bytes.AsSpan((int)first, (int)(last - first + 1)).ToArray());
        content.Headers.ContentRange = new ContentRangeHeaderValue(first, last, bytes.Length);
        return new(HttpStatusCode.PartialContent) { Content = content };
    }
}
