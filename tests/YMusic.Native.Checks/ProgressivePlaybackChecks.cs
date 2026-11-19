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
        await Run("Progressive playback: literal Windows paths survive local open, preload and complete buffering", async () =>
        {
            var folder = Directory.CreateDirectory(Path.Combine(_directory, "literal%61-playback")).FullName;
            var bytes = SilentMp3Seconds(4);
            var local = Path.Combine(folder, "first#%61.mp3");
            var next = Path.Combine(folder, "second%20.mp3");
            File.WriteAllBytes(local, bytes);
            File.WriteAllBytes(next, bytes);
            var digest = Convert.ToHexString(SHA256.HashData(bytes));
            var bufferPath = Path.Combine(folder, "buffer");
            var media = new FakeHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new ByteArrayContent(bytes) }));
            using var player = new NativePlayer(new AudioBuffer(bufferPath, media)) { Volume = 0 };
            await player.OpenLocalPausedAsync(new(local, "MP3", bytes.Length, digest), default);
            Assert(player.HasSource && player.SourceLocalPath == local, "native open lost the literal verified filename");
            await player.PrepareLocalNextAsync(new(next, "MP3", bytes.Length, digest), default);
            Assert(player.TryPlayPrepared(PlaybackInput.FileUri(next), TimeSpan.Zero) && player.SourceLocalPath == next,
                "native preload did not promote its exact literal filepath");
            player.Stop();
            await player.OpenPausedAsync(new AudioDescriptor(new Uri("https://s1.vlx.yandex.net/literal.mp3"), "mp3", 192), default);
            Assert(player.HasSource && player.SourceLocalPath is { } buffered
                && Path.GetDirectoryName(buffered) == bufferPath && File.Exists(buffered),
                "complete audio response did not open the literal buffer directory");
            player.Stop();
            Assert(!Directory.EnumerateFiles(bufferPath, "*", SearchOption.AllDirectories).Any(),
                "stopping a complete audio buffer retained its owned file");
        });

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
                new PlaybackStateStore(directory).Clear("12345", out _);
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
        await CheckAudioFraming();
        await CheckAudioRepresentations();
        await CheckShortRangesAndFullBodyRetry();
        await CheckAbandonedAudioBuffers();
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
            var requests = 0;
            using var api = new MusicApi(new FakeHandler((_, _) =>
            {
                Interlocked.Increment(ref requests);
                throw new InvalidOperationException("offline resume requested network");
            }));
            var queue = new PlaybackQueue(); queue.SetItems([row]); new PlaybackStateStore(directory).Save(PlaybackStateStore.Capture("12345", queue, 17.25));
            using var player = new NativePlayer(new AudioBuffer(Path.Combine(directory, "buffer"))); player.Volume = 0;
            var window = new MainWindow(api: api, store: new SessionStore(directory), restore: true, diagnosticsDirectory: directory,
                offline: offline, player: player, preferences: new PreferencesStore(Path.Combine(directory, "preferences")),
                audioOutputDevices: new PlaybackOptionsFakeOutputs());
            try
            {
                await ReadyPlaybackOutput(window).WaitAsync(TimeSpan.FromSeconds(5));
                PersistenceField(window, "_account", new Account("12345", "Fixture", true));
                PersistenceInvoke(window, "RestorePlaybackState", "12345");
                Assert(PersistenceField<TrackRow?>(window, "_current") is { } restored && restored.DownloadedPath == file
                    && PersistenceField<double?>(window, "_restoredPlaybackSeconds") == 17.25 && !player.HasSource && !player.IsPlaying,
                    "restore did not bind the indexed fixture and exact saved position without starting playback");
                await Call(window, "TogglePlaybackAsync");
                Assert(player.IsPlaying && player.SourceLocalPath == file && Volatile.Read(ref requests) == 0,
                    "explicit Play did not open the restored indexed file offline: " + Control<TextBlock>(window, "StatusText").Text);
                await Until(() => player.Position.TotalSeconds >= 16.5);
                Assert(player.IsPlaying && player.SourceLocalPath == file && Volatile.Read(ref requests) == 0,
                    "seek did not resume the restored indexed source without network");
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

    private static async Task CheckAudioFraming()
    {
        await Run("Audio buffer: full and partial bodies require consistent HTTP framing", async () =>
        {
            var directory = Path.Combine(_directory, "audio-buffer-http-framing");
            var audio = new AudioDescriptor(new Uri("https://s1.vlx.yandex.net/framing.mp3"), "mp3", 320);
            byte[] data = [1, 2, 3, 4];
            var scenarios = new (string[]? Length, string? Transfer, bool Accepted)[]
            {
                (["4"], null, true), (["4", "4"], null, true), (null, "chunked", true), (null, null, true),
                (["unknown"], null, false), (["4", "5"], null, false), (["4", "unknown"], null, false),
                (["4"], "chunked", false), (null, "identity", false)
            };
            for (var index = 0; index < scenarios.Length; index++)
            {
                var scenario = scenarios[index];
                var root = Path.Combine(directory, index.ToString(System.Globalization.CultureInfo.InvariantCulture));
                using var full = new AudioBuffer(root, new FakeHandler((_, _) => Task.FromResult(Response(HttpStatusCode.OK))));
                using var partial = new AudioBuffer(root, new FakeHandler((_, _) => Task.FromResult(Response(HttpStatusCode.PartialContent))));
                if (scenario.Accepted)
                {
                    using var file = await full.DownloadAsync(audio, default);
                    using var input = await full.OpenPlaybackAsync(audio, default);
                    var bytes = await partial.ReadPlaybackRangeAsync(audio, 0, 3, 4, default);
                    Assert(File.ReadAllBytes(file.Path).SequenceEqual(data) && File.ReadAllBytes(input.Path).SequenceEqual(data)
                        && bytes.SequenceEqual(data), "valid HTTP body framing changed the audio bytes");
                }
                else
                {
                    await Throws<MusicException>(() => full.DownloadAsync(audio, default));
                    await Throws<MusicException>(() => full.OpenPlaybackAsync(audio, default));
                    await Throws<MusicException>(() => partial.OpenPlaybackAsync(audio, default));
                    await Throws<MusicException>(() => partial.ReadPlaybackRangeAsync(audio, 0, 3, 4, default));
                    Assert(!Directory.Exists(root), "invalid HTTP framing was written to an audio buffer");
                }

                HttpResponseMessage Response(HttpStatusCode status)
                {
                    var body = new StreamContent(new UnboundedAudioStream(data));
                    if (scenario.Length is { } lengths)
                        Assert(body.Headers.TryAddWithoutValidation("Content-Length", lengths), "fixture could not set Content-Length");
                    if (status == HttpStatusCode.PartialContent) body.Headers.ContentRange = new ContentRangeHeaderValue(0, 3, 4);
                    var response = new HttpResponseMessage(status) { Content = body };
                    if (scenario.Transfer is { } transfer) response.Headers.TransferEncoding.ParseAdd(transfer);
                    return response;
                }
            }
        });
    }

    private static async Task CheckAudioRepresentations()
    {
        var audio = new AudioDescriptor(new Uri("https://s1.vlx.yandex.net/representation.mp3"), "mp3", 320);
        await Run("Progressive playback: redirects bind validators and bytes to the exact final audio URI", async () =>
        {
            var directory = Path.Combine(_directory, "progressive-redirect-representation");
            const int chunk = ProgressiveAudio.ChunkBytes;
            var data = new byte[chunk + 13]; new Random(71).NextBytes(data);
            var otherData = new byte[data.Length]; new Random(72).NextBytes(otherData);
            foreach (var different in new[] { false, true })
            {
                var requests = new ConcurrentBag<long>();
                var redirects = 0;
                using var buffer = new AudioBuffer(directory, new FakeHandler((request, _) =>
                {
                    var first = request.Headers.Range!.Ranges.Single().From == 0;
                    var uri = request.RequestUri!;
                    if (uri == audio.Uri)
                    {
                        Assert(request.Headers.IfRange is null, "a resource validator was forwarded to the redirect alias");
                        Interlocked.Increment(ref redirects);
                        var redirect = new HttpResponseMessage(HttpStatusCode.Found);
                        redirect.Headers.Location = new Uri(different && !first ? "/audio.mp3?v=2" : "/audio.mp3?v=1", UriKind.Relative);
                        return Task.FromResult(redirect);
                    }
                    var same = uri.Query == "?v=1";
                    var expected = !first && same ? "\"fixture\"" : null;
                    Assert(request.Headers.IfRange?.EntityTag?.Tag == expected, "If-Range escaped the exact final audio URI");
                    var response = ProgressiveResponse(request, same ? data : otherData, requests);
                    response.Headers.ETag = new EntityTagHeaderValue("\"fixture\"");
                    return Task.FromResult(response);
                }));
                using var input = await buffer.OpenPlaybackAsync(audio, default);
                if (different) await Throws<MusicException>(() => input.Completion.WaitAsync(TimeSpan.FromSeconds(5)));
                else
                {
                    await input.Completion.WaitAsync(TimeSpan.FromSeconds(5));
                    Assert(File.ReadAllBytes(input.Path).SequenceEqual(data), "stable redirect did not produce the original audio");
                }
                Assert(redirects == 2 && requests.Count == 2, "the redirected range was unexpectedly retried or skipped");
            }
        });

        await Run("Progressive playback: a temporary outage at a redirect hop retries the original range", async () =>
        {
            var directory = Path.Combine(_directory, "progressive-redirect-outage");
            const int chunk = ProgressiveAudio.ChunkBytes;
            var data = new byte[chunk + 13]; new Random(73).NextBytes(data);
            var ranges = new ConcurrentBag<long>();
            var retries = 0;
            using var buffer = new AudioBuffer(directory, new FakeHandler((request, _) =>
            {
                var start = request.Headers.Range!.Ranges.Single().From!.Value;
                var path = request.RequestUri!.AbsolutePath;
                if (path == "/audio.mp3")
                {
                    var expected = start == 0 ? null : "\"fixture\"";
                    Assert(request.Headers.IfRange?.EntityTag?.Tag == expected, "the final resource lost its validator after retry");
                    var audioResponse = ProgressiveResponse(request, data, ranges);
                    audioResponse.Headers.ETag = new EntityTagHeaderValue("\"fixture\"");
                    return Task.FromResult(audioResponse);
                }
                Assert(request.Headers.IfRange is null, "a resource validator was forwarded to a redirect hop");
                if (path == "/relay" && start != 0 && Interlocked.Increment(ref retries) == 1)
                    return Task.FromResult(RangeUnavailable());
                var redirect = new HttpResponseMessage(HttpStatusCode.Found);
                redirect.Headers.Location = new Uri(path == "/relay" ? "/audio.mp3" : "/relay", UriKind.Relative);
                return Task.FromResult(redirect);
            }));
            using var input = await buffer.OpenPlaybackAsync(audio, default);
            await input.Completion.WaitAsync(TimeSpan.FromSeconds(5));
            Assert(retries == 2 && ranges.Count == 2 && File.ReadAllBytes(input.Path).SequenceEqual(data),
                "an intermediate HTTP 503 did not retry and recover the original representation");
        });

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

        await Run("Progressive playback: multipart audio requires one matching strong validator", async () =>
        {
            var directory = Path.Combine(_directory, "progressive-representation-validator");
            const int chunk = ProgressiveAudio.ChunkBytes;
            var media = new byte[2 * chunk + 13]; new Random(44).NextBytes(media);
            var changedMedia = new byte[media.Length]; new Random(45).NextBytes(changedMedia);
            foreach (var scenario in new[]
            {
                (Last: "\"two\"", Compatible: false),
                (Last: "W/\"one\"", Compatible: false),
                (Last: (string?)null, Compatible: false),
                (Last: "\"one\"", Compatible: true)
            })
            {
                var requests = new ConcurrentBag<long>();
                using var buffer = new AudioBuffer(directory, new FakeHandler((request, _) =>
                {
                    var start = request.Headers.Range!.Ranges.Single().From!.Value;
                    var expected = start == 0 ? null : "\"one\"";
                    Assert(request.Headers.IfRange?.EntityTag?.Tag == expected, "If-Range did not use the known strong validator");
                    var response = ProgressiveResponse(request, start == 2 * chunk && !scenario.Compatible ? changedMedia : media, requests);
                    var tag = start == 2 * chunk ? scenario.Last : "\"one\"";
                    response.Headers.ETag = tag is null ? null : EntityTagHeaderValue.Parse(tag);
                    return Task.FromResult(response);
                }));
                using var input = await buffer.OpenPlaybackAsync(audio, default);
                if (scenario.Compatible)
                {
                    await input.Completion.WaitAsync(TimeSpan.FromSeconds(5));
                    Assert(File.ReadAllBytes(input.Path).SequenceEqual(media), "matching validators changed the audio bytes");
                }
                else await Throws<MusicException>(() => input.Completion.WaitAsync(TimeSpan.FromSeconds(5)));
                Assert(requests.Count == 3, "a missing, weak or conflicting validator was retried");
            }
        });

        await Run("Progressive playback: an unvalidated prefix is replaced by one complete HTTP response", async () =>
        {
            var directory = Path.Combine(_directory, "progressive-no-strong-validator");
            var prefixVersion = new byte[ProgressiveAudio.ChunkBytes + 13]; new Random(46).NextBytes(prefixVersion);
            var fullVersion = new byte[prefixVersion.Length]; new Random(47).NextBytes(fullVersion);
            foreach (var weak in new[] { false, true })
            {
                var ranges = new ConcurrentBag<long>();
                var wholeRequests = 0;
                var redirects = 0;
                using var buffer = new AudioBuffer(directory, new FakeHandler((request, _) =>
                {
                    Assert(request.Headers.IfRange is null, "an unvalidated request supplied If-Range");
                    if (request.RequestUri!.AbsolutePath == audio.Uri.AbsolutePath)
                    {
                        Assert(request.Headers.Range is not null, "fallback returned to the redirect alias");
                        redirects++;
                        var redirect = new HttpResponseMessage(HttpStatusCode.Found);
                        redirect.Headers.Location = new Uri("/fixture-full-audio.mp3", UriKind.Relative);
                        return Task.FromResult(redirect);
                    }
                    if (request.Headers.Range is not null)
                    {
                        var part = ProgressiveResponse(request, prefixVersion, ranges);
                        part.Headers.ETag = weak ? new EntityTagHeaderValue("\"fixture\"", isWeak: true) : null;
                        return Task.FromResult(part);
                    }
                    if (++wholeRequests == 1 && weak) return Task.FromResult(RangeUnavailable(TimeSpan.Zero));
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(fullVersion) });
                }));
                using var input = await buffer.OpenPlaybackAsync(audio, default).WaitAsync(TimeSpan.FromSeconds(5));
                Assert(!input.Progressive && input.Uri.IsFile && redirects == 1 && ranges.SequenceEqual(new long[] { 0 })
                    && wholeRequests == (weak ? 2 : 1)
                    && File.ReadAllBytes(input.Path).SequenceEqual(fullVersion), "fallback replayed the prefix or used bytes from two versions");
            }

            var invalidDirectory = Path.Combine(directory, "invalid");
            var requests = new ConcurrentBag<long>();
            using var invalid = new AudioBuffer(invalidDirectory, new FakeHandler((request, _) =>
            {
                if (request.Headers.Range is not null)
                {
                    var part = ProgressiveResponse(request, prefixVersion, requests);
                    part.Headers.ETag = null;
                    return Task.FromResult(part);
                }
                var bad = new ByteArrayContent(fullVersion);
                bad.Headers.ContentRange = new ContentRangeHeaderValue(0, fullVersion.Length - 1, fullVersion.Length);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = bad });
            }));
            await Throws<MusicException>(() => invalid.OpenPlaybackAsync(audio, default));
            Assert(!Directory.Exists(invalidDirectory), "an invalid full response left a buffered file");

            var limitedDirectory = Path.Combine(directory, "retry-limit");
            var probes = 0;
            var fullAttempts = 0;
            using var limited = new AudioBuffer(limitedDirectory, new FakeHandler((request, _) =>
            {
                if (request.Headers.Range is null)
                {
                    fullAttempts++;
                    return Task.FromResult(RangeUnavailable(TimeSpan.Zero));
                }
                if (++probes == 1) return Task.FromResult(RangeUnavailable(TimeSpan.Zero));
                var prefix = ProgressiveResponse(request, prefixVersion, requests);
                prefix.Headers.ETag = null;
                return Task.FromResult(prefix);
            }));
            await Throws<AudioBuffer.RangeUnavailableException>(() => limited.OpenPlaybackAsync(audio, default).WaitAsync(TimeSpan.FromSeconds(5)));
            Assert(probes == 2 && fullAttempts == 1 && !Directory.Exists(limitedDirectory), "full fallback restarted the retry budget");
        });

        await Run("Progressive playback: an untagged range covering all bytes needs no fallback", async () =>
        {
            var directory = Path.Combine(_directory, "progressive-single-untagged-range");
            var ranges = new ConcurrentBag<long>();
            byte[] data = [1, 2, 3, 4];
            using var buffer = new AudioBuffer(directory, new FakeHandler((request, _) =>
            {
                var entire = ProgressiveResponse(request, data, ranges);
                entire.Headers.ETag = null;
                return Task.FromResult(entire);
            }));
            using var input = await buffer.OpenPlaybackAsync(audio, default);
            await input.Completion.WaitAsync(TimeSpan.FromSeconds(5));
            Assert(ranges.Count == 1 && File.ReadAllBytes(input.Path).SequenceEqual(data), "an untagged complete range was downloaded twice");
        });
    }

    private static async Task CheckShortRangesAndFullBodyRetry()
    {
        var audio = new AudioDescriptor(new Uri("https://s1.vlx.yandex.net/short.mp3"), "mp3", 320);
        await Run("Progressive playback: a valid shorter initial range opens through one full response", async () =>
        {
            var directory = Path.Combine(_directory, "progressive-short-initial-range");
            var media = new byte[ProgressiveAudio.ChunkBytes + 13]; new Random(76).NextBytes(media);
            foreach (var scenario in new[] { (From: 0, Known: true), (From: 7, Known: true), (From: 0, Known: false), (From: 7, Known: false) })
            {
                var requests = 0;
                using var buffer = new AudioBuffer(directory, new FakeHandler((request, _) =>
                {
                    requests++;
                    if (request.Headers.Range is null)
                        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(media), Headers = { ETag = FixtureRangeTag } });
                    const int until = ProgressiveAudio.ChunkBytes / 2;
                    var part = new ByteArrayContent(media[scenario.From..until]);
                    part.Headers.ContentRange = scenario.Known ? new ContentRangeHeaderValue(scenario.From, until - 1, media.Length)
                        : new ContentRangeHeaderValue(scenario.From, until - 1);
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = part, Headers = { ETag = FixtureRangeTag } });
                }));
                using var input = await buffer.OpenPlaybackAsync(audio, default);
                Assert(!input.Progressive && requests == 2 && File.ReadAllBytes(input.Path).SequenceEqual(media),
                    "a valid contained range was rejected or exposed before full download");
            }

            var disallowed = new FakeHandler((_, _) =>
            {
                var part = new ByteArrayContent([1, 2]);
                part.Headers.ContentRange = new ContentRangeHeaderValue(ProgressiveAudio.ChunkBytes - 1, ProgressiveAudio.ChunkBytes);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = part });
            });
            using var invalid = new AudioBuffer(directory, disallowed);
            await Throws<MusicException>(() => invalid.OpenPlaybackAsync(audio, default));
            Assert(disallowed.Seen.Count == 1, "an initial range outside the request opened an unrelated full fetch");
        });

        await Run("Progressive playback: a shorter confirmed range is completed within bounded requests", async () =>
        {
            var directory = Path.Combine(_directory, "progressive-short-later-range");
            const int chunk = ProgressiveAudio.ChunkBytes;
            var media = new byte[chunk + 13]; new Random(77).NextBytes(media);
            var requests = new ConcurrentQueue<(long First, long Last)>();
            using var buffer = new AudioBuffer(directory, new FakeHandler((request, _) =>
            {
                var range = request.Headers.Range!.Ranges.Single();
                var from = range.From!.Value;
                var to = Math.Min(range.To!.Value, media.Length - 1);
                requests.Enqueue((from, to));
                var unknownTotal = from == chunk && to == media.Length - 1;
                if (unknownTotal) { from += 4; to -= 4; }
                var part = new ByteArrayContent(media.AsSpan((int)from, (int)(to - from + 1)).ToArray());
                part.Headers.ContentRange = unknownTotal ? new ContentRangeHeaderValue(from, to) : new ContentRangeHeaderValue(from, to, media.Length);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = part, Headers = { ETag = FixtureRangeTag } });
            }));
            using var input = await buffer.OpenPlaybackAsync(audio, default);
            await input.Completion.WaitAsync(TimeSpan.FromSeconds(5));
            (long First, long Last)[] expected = [(0, chunk - 1), (chunk, chunk + 12), (chunk, chunk + 3), (chunk + 9, chunk + 12)];
            Assert(input.Progressive && requests.SequenceEqual(expected) && File.ReadAllBytes(input.Path).SequenceEqual(media),
                "confirmed shorter ranges did not fill only the missing bytes");

            foreach (var fault in new[] { "outside", "length", "fragmented", "unknown-missing-tag", "unknown-no-baseline" })
            {
                var attempts = 0;
                using var reference = new HttpResponseMessage { Headers = { ETag = FixtureRangeTag } };
                var version = new AudioBuffer.PlaybackRepresentation(audio.Uri, reference.Headers);
                using var bad = new AudioBuffer(directory, new FakeHandler((request, _) =>
                {
                    attempts++;
                    var from = request.Headers.Range!.Ranges.Single().From!.Value;
                    if (fault == "outside") from--;
                    var part = new ByteArrayContent([1]);
                    part.Headers.ContentRange = fault.StartsWith("unknown-", StringComparison.Ordinal) ? new ContentRangeHeaderValue(from, from)
                        : new ContentRangeHeaderValue(from, from, media.Length + (fault == "length" ? 1 : 0));
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.PartialContent)
                        { Content = part, Headers = { ETag = fault == "unknown-missing-tag" ? null : FixtureRangeTag } });
                }));
                await Throws<MusicException>(() => bad.ReadPlaybackRangeAsync(audio, chunk, media.Length - 1, media.Length, default,
                    fault == "unknown-no-baseline" ? null : version));
                Assert(attempts == (fault == "fragmented" ? 4 : 1), "invalid or overly fragmented range continued issuing requests");
            }
        });

        await Run("Audio buffer: a complete fallback verifies the known length of its matching version", async () =>
        {
            var directory = Path.Combine(_directory, "audio-fallback-version-length");
            byte[] media = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12];
            foreach (var scenario in new[]
            {
                (Count: 12, Advertise: false, SameTag: true, Accept: true),
                (Count: 3, Advertise: false, SameTag: true, Accept: false),
                (Count: 3, Advertise: true, SameTag: true, Accept: false),
                (Count: 3, Advertise: false, SameTag: false, Accept: true)
            })
            {
                var requests = 0;
                var fullBytes = media[..scenario.Count];
                using var buffer = new AudioBuffer(directory, new FakeHandler((request, _) =>
                {
                    requests++;
                    if (request.Headers.Range is not null)
                    {
                        var part = new ByteArrayContent(media[5..8]);
                        part.Headers.ContentRange = new ContentRangeHeaderValue(5, 7, media.Length);
                        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = part, Headers = { ETag = FixtureRangeTag } });
                    }
                    var body = new StreamContent(new UnboundedAudioStream(fullBytes));
                    if (scenario.Advertise) body.Headers.ContentLength = fullBytes.Length;
                    else Assert(body.Headers.ContentLength is null, "fixture response unexpectedly advertised a length");
                    var tag = scenario.SameTag ? FixtureRangeTag : new EntityTagHeaderValue("\"new-version\"");
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = body, Headers = { ETag = tag } });
                }));
                if (scenario.Accept)
                {
                    using var input = await buffer.OpenPlaybackAsync(audio, default);
                    Assert(!input.Progressive && File.ReadAllBytes(input.Path).SequenceEqual(fullBytes), "the complete selected version could not play");
                }
                else await Throws<MusicException>(() => buffer.OpenPlaybackAsync(audio, default));
                Assert(requests == 2 && !Directory.EnumerateFiles(directory, "ym-audio-*").Any(),
                    "full-response length validation left a file or retried an inconsistent body");
            }
        });

        await Run("Audio buffer: a broken initial HTTP 200 body retries from a fresh file", async () =>
        {
            var directory = Path.Combine(_directory, "audio-incomplete-full-response");
            byte[] media = [5, 6, 7, 8];
            var requests = 0;
            using var buffer = new AudioBuffer(directory, new FakeHandler((_, _) =>
            {
                HttpContent body = ++requests == 1 ? new StreamContent(new EndedAudioStream([1, 2])) : new ByteArrayContent(media);
                body.Headers.ContentLength = media.Length;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = body });
            }));
            using var input = await buffer.OpenPlaybackAsync(audio, default);
            Assert(!input.Progressive && requests == 2 && File.ReadAllBytes(input.Path).SequenceEqual(media)
                && Directory.EnumerateFiles(directory, "ym-audio-*").Count() == 1
                && Directory.EnumerateFiles(directory, "*.lease", SearchOption.AllDirectories).Count() <= 1,
                "the failed full body left bytes or a receipt in the succeeding attempt");

            var blockedPath = Path.Combine(directory, "not-a-directory");
            File.WriteAllBytes(blockedPath, media);
            var localFailure = new FakeHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(media) }));
            using var blocked = new AudioBuffer(blockedPath, localFailure);
            await Throws<IOException>(() => blocked.OpenPlaybackAsync(audio, default));
            Assert(localFailure.Seen.Count == 1 && File.ReadAllBytes(blockedPath).SequenceEqual(media), "a local file error restarted HTTP");
        });
    }

    private sealed class EndedAudioStream(byte[] prefix) : MemoryStream(prefix)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> destination, CancellationToken cancellationToken = default)
        {
            if (Position == Length)
                return ValueTask.FromException<int>(new HttpIOException(HttpRequestError.ResponseEnded, "synthetic incomplete HTTP body"));
            return base.ReadAsync(destination, cancellationToken);
        }
    }

    private sealed class UnboundedAudioStream(byte[] data) : MemoryStream(data)
    {
        public override bool CanSeek => false;
    }

    private static async Task CheckAbandonedAudioBuffers()
    {
        await Run("Audio buffer: a new buffer releases abandoned files recorded by identity", async () =>
        {
            var directory = Path.Combine(_directory, "audio-buffer-recovery");
            Directory.CreateDirectory(directory);
            string NewBufferPath() => Path.Combine(directory, "ym-audio-" + Guid.NewGuid().ToString("N") + ".mp3");
            var abandoned = NewBufferPath();
            var replaced = NewBufferPath();
            var active = NewBufferPath();
            var unrecorded = NewBufferPath();
            var moved = Path.Combine(directory, "fixture-moved.mp3");
            byte[] data = [1, 2, 3, 4];
            File.WriteAllBytes(unrecorded, data);
            if (!LeaveAbandoned(abandoned))
            {
                Assert(AudioBufferRecovery.Recover(directory) == 0 && File.ReadAllBytes(abandoned).SequenceEqual(data),
                    "recovery attempted an unverified filesystem identity");
                return;
            }
            Assert(LeaveAbandoned(replaced), "fixture could not record the original file");
            File.Move(replaced, moved);
            File.WriteAllBytes(replaced, data);
            using var activeWriter = new FileStream(active, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read);
            using var activeReceipt = AudioBufferRecovery.Record(active, activeWriter.SafeFileHandle);
            Assert(activeReceipt is not null, "fixture could not record the active file");

            var descriptor = new AudioDescriptor(new Uri("https://s1.vlx.yandex.net/recovery.mp3"), "mp3", 320);
            using var buffer = new AudioBuffer(directory, new FakeHandler((_, _) => Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(data) })));
            using (var file = await buffer.DownloadAsync(descriptor, default))
                Assert(File.ReadAllBytes(file.Path).SequenceEqual(data), "an active download was lost during recovery");
            Assert(!File.Exists(abandoned) && File.Exists(active) && File.ReadAllBytes(replaced).SequenceEqual(data)
                && File.ReadAllBytes(moved).SequenceEqual(data) && File.ReadAllBytes(unrecorded).SequenceEqual(data),
                "recovery retained an abandoned buffer or changed a file without a matching inactive receipt");
            Assert(Directory.EnumerateFiles(directory, "*.lease", SearchOption.AllDirectories).Count() == 2,
                "normal cleanup or recovery retained a receipt for deleted temporary audio");

            bool LeaveAbandoned(string path)
            {
                using var writer = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read);
                using var receipt = AudioBufferRecovery.Record(path, writer.SafeFileHandle);
                writer.Write(data);
                return receipt is not null;
            }
        });

        await Run("Audio buffer: recovery rejects a moved receipt and preserves named streams", () =>
        {
            var directory = Path.Combine(_directory, "audio-buffer-authenticated-recovery");
            Directory.CreateDirectory(directory);
            string NewBufferPath() => Path.Combine(directory, "ym-audio-" + Guid.NewGuid().ToString("N") + ".mp3");
            var original = NewBufferPath();
            var moved = NewBufferPath();
            byte[] data = [1, 2, 3, 4];
            if (!LeaveRecordedAudio(original)) return Task.CompletedTask;

            var originalReceipt = Directory.EnumerateFiles(directory, Path.GetFileName(original) + ".lease", SearchOption.AllDirectories).Single();
            var receiptDirectory = Path.GetDirectoryName(originalReceipt)!;
            var copiedReceipt = Path.Combine(receiptDirectory, Path.GetFileName(moved) + ".lease");
            File.Copy(originalReceipt, copiedReceipt);
            File.Move(original, moved);

            var unverified = NewBufferPath();
            var unverifiedReceipt = Path.Combine(receiptDirectory, Path.GetFileName(unverified) + ".lease");
            File.WriteAllBytes(unverified, data);
            File.WriteAllBytes(unverifiedReceipt, "synthetic unprotected record"u8.ToArray());
            Assert(AudioBufferRecovery.Recover(directory) == 1 && File.ReadAllBytes(moved).SequenceEqual(data)
                && File.ReadAllBytes(unverified).SequenceEqual(data) && File.Exists(copiedReceipt) && File.Exists(unverifiedReceipt),
                "a moved or unprotected receipt authorized deletion");

            if (new DriveInfo(Path.GetPathRoot(directory)!).DriveFormat == "NTFS")
            {
                var withStream = NewBufferPath();
                Assert(LeaveRecordedAudio(withStream), "fixture could not record a file for the named stream check");
                const string metadata = "synthetic audio metadata stream";
                var streamPath = withStream + ":yamuse-fixture";
                File.WriteAllText(streamPath, metadata);
                Assert(AudioBufferRecovery.Recover(directory) == 0 && File.ReadAllBytes(withStream).SequenceEqual(data)
                    && File.ReadAllText(streamPath) == metadata, "recovery lost an additional NTFS data stream");

                var withAttributes = NewBufferPath();
                var withProtectedReceipt = NewBufferPath();
                Assert(LeaveRecordedAudio(withAttributes) && LeaveRecordedAudio(withProtectedReceipt), "fixture could not record the EA cases");
                Cleanup015AddExtendedAttribute(withAttributes);
                var protectedReceipt = Path.Combine(receiptDirectory, Path.GetFileName(withProtectedReceipt) + ".lease");
                Cleanup015AddExtendedAttribute(protectedReceipt);
                Assert(AudioBufferRecovery.Recover(directory) == 1 && !File.Exists(withProtectedReceipt)
                    && File.ReadAllBytes(withAttributes).SequenceEqual(data), "recovery did not distinguish protected data from a clean inactive file");
                using var recordedAudio = File.OpenHandle(withAttributes);
                using var recordedReceipt = File.OpenHandle(protectedReceipt);
                Assert(FileSecurityPreservation.HasNamedStreamsOrExtendedAttributes(recordedAudio)
                    && FileSecurityPreservation.HasNamedStreamsOrExtendedAttributes(recordedReceipt), "recovery lost extended attributes on audio or receipt");

                using var ordinary = new BufferedAudioFile(NewBufferPath(), FileOptions.SequentialScan);
                ordinary.Stream.Write(data);
                ordinary.Stream.Flush();
                ordinary.Seal();
                var ordinaryStream = ordinary.Path + ":yamuse-fixture";
                File.WriteAllText(ordinaryStream, metadata);
                ordinary.Dispose();
                Assert(File.ReadAllBytes(ordinary.Path).SequenceEqual(data) && File.ReadAllText(ordinaryStream) == metadata,
                    "ordinary cleanup lost an additional NTFS data stream");

                using var ordinaryWithAttributes = new BufferedAudioFile(NewBufferPath(), FileOptions.SequentialScan);
                ordinaryWithAttributes.Stream.Write(data);
                ordinaryWithAttributes.Stream.Flush();
                Cleanup015AddExtendedAttribute(ordinaryWithAttributes.Stream.SafeFileHandle);
                ordinaryWithAttributes.Seal();
                ordinaryWithAttributes.Dispose();
                using var returned = File.OpenHandle(ordinaryWithAttributes.Path);
                Assert(File.ReadAllBytes(ordinaryWithAttributes.Path).SequenceEqual(data)
                    && FileSecurityPreservation.HasNamedStreamsOrExtendedAttributes(returned), "ordinary cleanup lost extended attributes");
            }
            return Task.CompletedTask;

            bool LeaveRecordedAudio(string path)
            {
                using var writer = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read);
                using var receipt = AudioBufferRecovery.Record(path, writer.SafeFileHandle);
                writer.Write(data);
                return receipt is not null;
            }
        });

        await Run("Audio buffer: later acquisition retries only temporarily held recovery records", async () =>
        {
            var directory = Path.Combine(_directory, "audio-buffer-busy-recovery");
            Directory.CreateDirectory(directory);
            var blockedAudio = Path.Combine(directory, "ym-audio-" + Guid.NewGuid().ToString("N") + ".mp3");
            var blockedMarker = Path.Combine(directory, "ym-audio-" + Guid.NewGuid().ToString("N") + ".mp3");
            byte[] data = [1, 2, 3, 4];
            if (!Record(blockedAudio)) return;
            Assert(Record(blockedMarker), "fixture could not record the second file");
            var markerForAudio = FindReceipt(blockedAudio);
            var markerForMarker = FindReceipt(blockedMarker);
            var unrecorded = Path.Combine(directory, "ym-audio-" + Guid.NewGuid().ToString("N") + ".mp3");
            File.WriteAllBytes(unrecorded, data);

            var descriptor = new AudioDescriptor(new Uri("https://s1.vlx.yandex.net/blocked.mp3"), "mp3", 320);
            using var buffer = new AudioBuffer(directory, new FakeHandler((_, _) => Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(data) })));
            using (File.OpenRead(blockedAudio))
            using (File.OpenRead(markerForMarker))
            using (var first = await buffer.DownloadAsync(descriptor, default))
            {
                Assert(File.Exists(blockedAudio) && File.Exists(blockedMarker) && File.Exists(markerForAudio) && File.Exists(markerForMarker),
                    "recovery changed a currently held audio file or receipt");
            }
            using (var next = await buffer.DownloadAsync(descriptor, default))
            {
                Assert(!File.Exists(blockedAudio) && !File.Exists(blockedMarker) && !File.Exists(markerForAudio) && !File.Exists(markerForMarker)
                    && File.ReadAllBytes(unrecorded).SequenceEqual(data), "the same buffer did not retry exactly its deferred owned files");
            }

            bool Record(string path)
            {
                using var writer = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read);
                using var receipt = AudioBufferRecovery.Record(path, writer.SafeFileHandle);
                writer.Write(data);
                return receipt is not null;
            }

            string FindReceipt(string path) => Directory.EnumerateFiles(directory, Path.GetFileName(path) + ".lease", SearchOption.AllDirectories).Single();
        });

        await Run("Audio buffer: cancellation while waiting for recovery creates no audio file", async () =>
        {
            foreach (var operation in new[] { "range", "full", "offline" })
            {
                var directory = Path.Combine(_directory, "audio-buffer-canceled-recovery-" + operation);
                var audio = new AudioDescriptor(new Uri("https://s1.vlx.yandex.net/canceled-recovery.mp3"), "mp3", 320);
                var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                using var buffer = new AudioBuffer(directory, new FakeHandler((_, _) =>
                {
                    var status = operation == "range" ? HttpStatusCode.PartialContent : HttpStatusCode.OK;
                    var body = new ByteArrayContent([1, 2, 3, 4]);
                    if (status == HttpStatusCode.PartialContent) body.Headers.ContentRange = new ContentRangeHeaderValue(0, 3, 4);
                    received.TrySetResult();
                    return Task.FromResult(new HttpResponseMessage(status) { Content = body });
                }));
                var recovery = AudioBufferRecovery.ForDirectory(directory);
                var gate = (SemaphoreSlim)typeof(AudioBufferRecovery).GetField("_gate", System.Reflection.BindingFlags.Instance
                    | System.Reflection.BindingFlags.NonPublic)!.GetValue(recovery)!;
                using var canceled = new CancellationTokenSource();
                await gate.WaitAsync();
                try
                {
                    Task waiting = operation == "offline" ? buffer.DownloadAsync(audio, canceled.Token) : buffer.OpenPlaybackAsync(audio, canceled.Token);
                    await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    Assert(!waiting.IsCompleted, "fixture did not hold completion on the shared recovery gate");
                    canceled.Cancel();
                    await Throws<OperationCanceledException>(() => waiting.WaitAsync(TimeSpan.FromSeconds(5)));
                }
                finally { gate.Release(); }
                Assert(!Directory.Exists(directory), "canceled recovery created an audio or receipt directory");
            }
        });

        await Run("Audio buffer: playback and download share deferred cleanup by canonical buffer root", async () =>
        {
            var directory = Path.Combine(_directory, "audio-buffer-shared-recovery");
            var otherDirectory = Path.Combine(_directory, "audio-buffer-other-recovery");
            var abandoned = Path.Combine(directory, "ym-audio-" + Guid.NewGuid().ToString("N") + ".mp3");
            byte[] data = [1, 2, 3, 4];
            var descriptor = new AudioDescriptor(new Uri("https://s1.vlx.yandex.net/shared.mp3"), "mp3", 320);
            using var playback = CreateBuffer(directory);
            using var download = CreateBuffer(Path.Combine(directory, "."));
            using var other = CreateBuffer(otherDirectory);
            Assert(!ReferenceEquals(AudioBufferRecovery.ForDirectory(directory), AudioBufferRecovery.ForDirectory(otherDirectory)),
                "unrelated buffers share their recovery state");

            var opening = Task.Run(() => playback.OpenPlaybackAsync(descriptor, default));
            var saving = Task.Run(() => download.DownloadAsync(descriptor, default));
            using (var opened = await opening)
            using (var saved = await saving)
                Assert(opened.Path != saved.Path && File.Exists(opened.Path) && File.Exists(saved.Path),
                    "two cold acquisitions shared a temporary audio file");

            using (var file = new FileStream(abandoned, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read))
            {
                using var receipt = AudioBufferRecovery.Record(abandoned, file.SafeFileHandle);
                if (receipt is null) return;
                file.Write(data);
            }
            var marker = Directory.EnumerateFiles(directory, Path.GetFileName(abandoned) + ".lease", SearchOption.AllDirectories).Single();
            using var active = await playback.OpenPlaybackAsync(descriptor, default);
            var unrecorded = Path.Combine(directory, "ym-audio-" + Guid.NewGuid().ToString("N") + ".mp3");
            File.WriteAllBytes(unrecorded, data);
            AudioBufferRecovery.ForDirectory(directory).Defer(abandoned);
            AudioBufferRecovery.ForDirectory(directory).Defer(active.Path);
            using (File.OpenRead(abandoned))
            using (var held = await download.DownloadAsync(descriptor, default))
                Assert(File.Exists(abandoned) && File.Exists(marker), "another buffer changed a held recovery pair");
            using (var unrelated = await other.DownloadAsync(descriptor, default))
                Assert(File.Exists(abandoned) && File.Exists(marker), "another root touched this recovery pair");
            using (var recovered = await download.DownloadAsync(descriptor, default))
                Assert(!File.Exists(abandoned) && !File.Exists(marker) && File.ReadAllBytes(active.Path).SequenceEqual(data)
                    && File.ReadAllBytes(unrecorded).SequenceEqual(data), "acquisition by a sibling buffer did not safely consume the deferred pair");

            AudioBuffer CreateBuffer(string path) => new(path, new FakeHandler((_, _) => Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(data) })));
        });

        await Run("Audio buffer: a managed file-range lock marks a recovery receipt temporary", async () =>
        {
            var directory = Path.Combine(_directory, "audio-buffer-receipt-range-lock");
            if (new DriveInfo(Path.GetPathRoot(directory)!).DriveFormat != "NTFS") return;
            Directory.CreateDirectory(directory);
            var audio = Path.Combine(directory, "ym-audio-" + Guid.NewGuid().ToString("N") + ".mp3");
            byte[] data = [1, 2, 3, 4];
            using (var file = new FileStream(audio, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read))
            {
                using var receipt = AudioBufferRecovery.Record(audio, file.SafeFileHandle);
                if (receipt is null) return;
                file.Write(data);
            }
            var marker = Directory.EnumerateFiles(directory, Path.GetFileName(audio) + ".lease", SearchOption.AllDirectories).Single();
            var descriptor = new AudioDescriptor(new Uri("https://s1.vlx.yandex.net/range-lock.mp3"), "mp3", 320);
            using var buffer = new AudioBuffer(directory, new FakeHandler((_, _) => Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(data) })));
            using (var locked = new FileStream(marker, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                locked.Lock(0, 1);
                using var read = File.OpenHandle(marker, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                var error = await Throws<IOException>(() => Task.FromResult(GuardedStoreFile.ReadFromHandle(read, 4096)));
                Assert(error.HResult == unchecked((int)0x80070021) && AudioBufferRecovery.IsTemporarilyHeld(error)
                    && !AudioBufferRecovery.IsTemporarilyHeld(new IOException("synthetic unrelated failure", unchecked((int)0x80130021))),
                    "the managed HRESULT lost or misclassified the native lock status");
                using (var first = await buffer.DownloadAsync(descriptor, default))
                    Assert(File.ReadAllBytes(audio).SequenceEqual(data) && File.Exists(marker), "a range lock changed the recovery pair");
                locked.Unlock(0, 1);
            }
            using var next = await buffer.DownloadAsync(descriptor, default);
            Assert(!File.Exists(audio) && !File.Exists(marker), "recovery did not revisit a receipt after its range lock cleared");
        });

        await Run("Audio buffer: recovery and normal disposal preserve secondary lease data", async () =>
        {
            var directory = Path.Combine(_directory, "audio-buffer-lease-metadata");
            if (new DriveInfo(Path.GetPathRoot(directory)!).DriveFormat != "NTFS") return;
            Directory.CreateDirectory(directory);
            var abandoned = Path.Combine(directory, "ym-audio-" + Guid.NewGuid().ToString("N") + ".mp3");
            byte[] data = [1, 2, 3, 4];
            const string metadata = "synthetic lease metadata";
            using (var writer = new FileStream(abandoned, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read))
            {
                using var receipt = AudioBufferRecovery.Record(abandoned, writer.SafeFileHandle);
                if (receipt is null) return;
                writer.Write(data);
            }
            var abandonedReceipt = FindReceipt(abandoned);
            var abandonedStream = abandonedReceipt + ":yamuse-fixture";
            File.WriteAllText(abandonedStream, metadata);
            Assert(AudioBufferRecovery.Recover(directory) == 1 && !File.Exists(abandoned)
                && File.ReadAllText(abandonedStream) == metadata, "recovery discarded receipt metadata");

            var descriptor = new AudioDescriptor(new Uri("https://s1.vlx.yandex.net/lease.mp3"), "mp3", 320);
            using var buffer = new AudioBuffer(directory, new FakeHandler((_, _) => Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(data) })));
            using var audio = await buffer.DownloadAsync(descriptor, default);
            var currentReceipt = FindReceipt(audio.Path);
            var currentStream = currentReceipt + ":yamuse-fixture";
            using (var stream = new FileStream(currentStream, FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
            using (var writer = new StreamWriter(stream)) writer.Write(metadata);
            audio.Dispose();
            Assert(!File.Exists(audio.Path) && File.ReadAllText(currentStream) == metadata,
                "normal disposal discarded receipt metadata");

            string FindReceipt(string path) => Directory.EnumerateFiles(directory, Path.GetFileName(path) + ".lease", SearchOption.AllDirectories).Single();
        });
    }

    private static async Task CheckAudioRangeRetries()
    {
        var audio = new AudioDescriptor(new Uri("https://s1.vlx.yandex.net/retry.mp3"), "mp3", 320);
        await Run("Progressive playback: initial HTTP 503 and 429 retry a delayed range with cancellation and a bounded wait", async () =>
        {
            var directory = Path.Combine(_directory, "progressive-initial-status-retry");
            byte[] data = [1, 2, 3, 4, 5];
            foreach (var status in new[] { HttpStatusCode.ServiceUnavailable, HttpStatusCode.TooManyRequests })
            {
                var ranges = new ConcurrentBag<long>();
                var terminal = 0;
                var redirects = 0;
                var watch = Stopwatch.StartNew();
                using var buffer = new AudioBuffer(directory, new FakeHandler((request, _) =>
                {
                    if (request.RequestUri!.AbsolutePath == "/retry.mp3")
                    {
                        redirects++;
                        var redirect = new HttpResponseMessage(HttpStatusCode.Found);
                        redirect.Headers.Location = new Uri("/fixture-audio.mp3", UriKind.Relative);
                        return Task.FromResult(redirect);
                    }
                    Assert(request.Headers.IfRange is null, "startup retry used a validator before accepting audio");
                    return Task.FromResult(++terminal == 1 ? RangeUnavailable(TimeSpan.FromSeconds(1), status)
                        : ProgressiveResponse(request, data, ranges));
                }));
                using var input = await buffer.OpenPlaybackAsync(audio, default).WaitAsync(TimeSpan.FromSeconds(5));
                await input.Completion.WaitAsync(TimeSpan.FromSeconds(5));
                Assert(redirects == 2 && terminal == 2 && watch.Elapsed >= TimeSpan.FromMilliseconds(950)
                    && input.Progressive && File.ReadAllBytes(input.Path).SequenceEqual(data), "initial range ignored Retry-After or lost audio");
            }

            var delayed = new FakeHandler((_, _) => Task.FromResult(RangeUnavailable(TimeSpan.FromSeconds(60), HttpStatusCode.TooManyRequests)));
            using (var buffer = new AudioBuffer(directory, delayed))
                await Throws<AudioBuffer.RangeUnavailableException>(() => buffer.OpenPlaybackAsync(audio, default).WaitAsync(TimeSpan.FromSeconds(3)));
            Assert(delayed.Seen.Count == 1, "a long startup Retry-After emitted a second request");

            using var cancellation = new CancellationTokenSource();
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var canceled = new FakeHandler((_, _) =>
            {
                entered.TrySetResult();
                return Task.FromResult(RangeUnavailable(TimeSpan.FromSeconds(2)));
            });
            using (var buffer = new AudioBuffer(directory, canceled))
            {
                var pending = buffer.OpenPlaybackAsync(audio, cancellation.Token);
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
                cancellation.Cancel();
                await Throws<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(3)));
            }
            Assert(canceled.Seen.Count == 1, "startup cancellation emitted a second request");
            var permanent = new FakeHandler((_, _) => Task.FromResult(RangeUnavailable(status: HttpStatusCode.TooManyRequests)));
            using (var buffer = new AudioBuffer(directory, permanent))
                await Throws<MusicException>(() => buffer.OpenPlaybackAsync(audio, default));
            Assert(permanent.Seen.Count == 1, "startup HTTP 429 without Retry-After emitted a second request");
        });

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
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = shortBody, Headers = { ETag = FixtureRangeTag } });
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

            var tls = new FakeHandler((_, _) => throw new HttpRequestException(HttpRequestError.SecureConnectionError, "fixture certificate failure"));
            using (var buffer = new AudioBuffer(directory, tls))
                await Throws<HttpRequestException>(() => buffer.ReadPlaybackRangeAsync(audio, 16, 19, 20, default));
            Assert(tls.Seen.Count == 1, "TLS failure was retried");

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
        await Run("Progressive playback: late HTTP 429 uses a declared Retry-After", async () =>
        {
            var directory = Path.Combine(_directory, "progressive-limited-range-policy");
            byte[] data = [1, 2, 3, 4];
            var ranges = new ConcurrentBag<long>();
            var attempts = 0;
            var watch = Stopwatch.StartNew();
            using (var buffer = new AudioBuffer(directory, new FakeHandler((request, _) => Task.FromResult(++attempts == 1
                ? RangeUnavailable(TimeSpan.FromSeconds(1), HttpStatusCode.TooManyRequests) : ProgressiveResponse(request, data, ranges)))))
            {
                var rangesRead = await buffer.ReadPlaybackRangeAsync(audio, 0, 3, 4, default).WaitAsync(TimeSpan.FromSeconds(5));
                Assert(attempts == 2 && watch.Elapsed >= TimeSpan.FromMilliseconds(950) && rangesRead.SequenceEqual(data), "HTTP 429 ignored Retry-After");
            }

            var withoutHint = new FakeHandler((_, _) => Task.FromResult(RangeUnavailable(status: HttpStatusCode.TooManyRequests)));
            using (var buffer = new AudioBuffer(directory, withoutHint))
            {
                var start = Environment.TickCount64;
                var failure = await Throws<AudioBuffer.RangeUnavailableException>(() => buffer.ReadPlaybackRangeAsync(audio, 0, 3, 4, default));
                Assert(failure.RetryAt >= start + 500 && failure.RetryOnlyFromReader, "HTTP 429 without Retry-After lost the reader cooldown");
            }
            Assert(withoutHint.Seen.Count == 1, "HTTP 429 without Retry-After started a retry");
        });

        await Run("Progressive playback: a seek to another uncached range honors the shared HTTP 429 pause", async () =>
        {
            var directory = Path.Combine(_directory, "progressive-shared-range-pause");
            const int chunk = ProgressiveAudio.ChunkBytes;
            var data = new byte[4 * chunk + 13]; new Random(817).NextBytes(data);
            var other = audio with { Uri = new Uri("https://s1.vlx.yandex.net/other-range.mp3") };
            using var initial = new HttpResponseMessage(HttpStatusCode.PartialContent) { Headers = { ETag = FixtureRangeTag } };
            var representation = new AudioBuffer.PlaybackRepresentation(audio.Uri, initial.Headers);
            var independent = new AudioBuffer.PlaybackRepresentation(other.Uri, initial.Headers);
            var served = new ConcurrentBag<long>();
            var sent = new ConcurrentBag<(Uri Uri, long First, long At)>();
            var prefetchEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var prefetchAttempts = 0;
            var seekAttempts = 0;
            using var buffer = new AudioBuffer(directory, new FakeHandler((request, _) =>
            {
                var first = request.Headers.Range!.Ranges.Single().From!.Value;
                sent.Add((request.RequestUri!, first, Environment.TickCount64));
                if (request.RequestUri == audio.Uri)
                {
                    if (first == chunk && Interlocked.Increment(ref prefetchAttempts) == 1)
                    {
                        prefetchEntered.TrySetResult();
                        return Task.FromResult(RangeUnavailable(TimeSpan.FromSeconds(1), HttpStatusCode.TooManyRequests));
                    }
                    if (first == 2 * chunk && Interlocked.Increment(ref seekAttempts) == 1)
                        throw new HttpRequestException(HttpRequestError.ConnectionError, "fixture transient seek read");
                }
                return Task.FromResult(ProgressiveResponse(request, data, served));
            }), rangeTimeout: TimeSpan.FromMilliseconds(900));
            var prefetch = Read(audio, representation, chunk);
            await prefetchEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            using var cancellation = new CancellationTokenSource();
            var canceled = Read(audio, representation, 3 * chunk, cancellation.Token);
            var seek = Read(audio, representation, 2 * chunk);
            try
            {
                Assert(!sent.Any(request => request.Uri == audio.Uri && request.First > chunk),
                    "an uncached seek range was sent during the shared pause");
                Assert((await Read(other, independent, 2 * chunk).WaitAsync(TimeSpan.FromSeconds(3))).SequenceEqual(data[(2 * chunk)..(3 * chunk)]),
                    "a different source was paused by the limited resource");
            }
            finally { cancellation.Cancel(); }
            await Throws<OperationCanceledException>(() => canceled.WaitAsync(TimeSpan.FromSeconds(3)));
            var chunks = await Task.WhenAll(prefetch, seek).WaitAsync(TimeSpan.FromSeconds(5));
            var firstLimit = sent.Where(request => request.Uri == audio.Uri && request.First == chunk).Min(request => request.At);
            Assert(prefetchAttempts == 2 && seekAttempts == 2
                && sent.All(request => request.Uri != audio.Uri || request.First != 3 * chunk)
                && sent.Where(request => request.Uri == audio.Uri && request.First == 2 * chunk).All(request => request.At - firstLimit >= 950)
                && chunks[0].SequenceEqual(data[chunk..(2 * chunk)]) && chunks[1].SequenceEqual(data[(2 * chunk)..(3 * chunk)]),
                "shared waiting sent a canceled or early range, consumed a block retry, or changed recovered bytes");

            Task<byte[]> Read(AudioDescriptor source, AudioBuffer.PlaybackRepresentation version, long first, CancellationToken token = default)
                => buffer.ReadPlaybackRangeAsync(source, first, first + chunk - 1, data.Length, token, version);
        });

        await Run("Progressive playback: overlapping HTTP 429 responses keep the latest source deadline", async () =>
        {
            var directory = Path.Combine(_directory, "progressive-overlapping-range-pause");
            const int chunk = ProgressiveAudio.ChunkBytes;
            var data = new byte[4 * chunk];
            using var initial = new HttpResponseMessage(HttpStatusCode.PartialContent) { Headers = { ETag = FixtureRangeTag } };
            var representation = new AudioBuffer.PlaybackRepresentation(audio.Uri, initial.Headers);
            var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var secondEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseSecond = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var sent = new ConcurrentBag<long>();
            using var buffer = new AudioBuffer(directory, new FakeHandler(async (request, token) =>
            {
                var first = request.Headers.Range!.Ranges.Single().From!.Value;
                sent.Add(first);
                if (first == chunk)
                {
                    firstEntered.TrySetResult();
                    await releaseFirst.Task.WaitAsync(token);
                    return RangeUnavailable(TimeSpan.FromSeconds(60), HttpStatusCode.TooManyRequests);
                }
                if (first == 2 * chunk)
                {
                    secondEntered.TrySetResult();
                    await releaseSecond.Task.WaitAsync(token);
                    return RangeUnavailable(TimeSpan.FromSeconds(1), HttpStatusCode.TooManyRequests);
                }
                return ProgressiveResponse(request, data, new ConcurrentBag<long>());
            }));
            using var cancel = new CancellationTokenSource();
            var first = Read(chunk);
            var second = Read(2 * chunk);
            try
            {
                await Task.WhenAll(firstEntered.Task, secondEntered.Task).WaitAsync(TimeSpan.FromSeconds(3));
                releaseFirst.TrySetResult();
                var longPause = await Throws<AudioBuffer.RangeUnavailableException>(() => first.WaitAsync(TimeSpan.FromSeconds(3)));
                releaseSecond.TrySetResult();
                var shortPause = await Throws<AudioBuffer.RangeUnavailableException>(() => second.WaitAsync(TimeSpan.FromSeconds(3)));
                var deferredSeek = await Throws<AudioBuffer.RangeUnavailableException>(() => Read(3 * chunk).WaitAsync(TimeSpan.FromSeconds(3)));
                Assert(shortPause.RetryAt == longPause.RetryAt && deferredSeek.RetryAt == longPause.RetryAt
                    && deferredSeek.RemainingDelay > TimeSpan.FromSeconds(30)
                    && sent.Order().SequenceEqual([chunk, 2L * chunk]),
                    "a later short hint reduced the pause or a deferred seek reached the CDN");
            }
            finally { cancel.Cancel(); releaseFirst.TrySetResult(); releaseSecond.TrySetResult(); }

            Task<byte[]> Read(long first) => buffer.ReadPlaybackRangeAsync(audio, first, first + chunk - 1, data.Length, cancel.Token, representation);
        });

        await Run("Progressive playback: a throttled redirect hop does not pause the verified recipient", async () =>
        {
            var directory = Path.Combine(_directory, "progressive-redirect-range-pause");
            const int chunk = ProgressiveAudio.ChunkBytes;
            var data = new byte[7 * chunk]; new Random(819).NextBytes(data);
            var source = audio with { Uri = new Uri("https://s1.vlx.yandex.net/resolve-range.mp3") };
            var verified = new Uri("https://s2.vlx.yandex.net/verified-range.mp3");
            var temporary = new Uri("https://s3.vlx.yandex.net/temporary-hop.mp3");
            var different = new Uri("https://s2.vlx.yandex.net/different-range.mp3");
            var destination = verified;
            var throttleVerified = false;
            using var initial = new HttpResponseMessage(HttpStatusCode.PartialContent) { Headers = { ETag = FixtureRangeTag } };
            var representation = new AudioBuffer.PlaybackRepresentation(verified, initial.Headers, source.Uri);
            var sent = new ConcurrentBag<(Uri Uri, long First, string? Validator)>();
            var served = new ConcurrentBag<long>();
            using var buffer = new AudioBuffer(directory, new FakeHandler((request, _) =>
            {
                var uri = request.RequestUri!;
                var first = request.Headers.Range!.Ranges.Single().From!.Value;
                sent.Add((uri, first, request.Headers.IfRange?.EntityTag?.Tag));
                if (uri == source.Uri) return Task.FromResult(Redirect(destination));
                if (uri == temporary)
                    return Task.FromResult(first == chunk
                        ? RangeUnavailable(TimeSpan.FromSeconds(60), HttpStatusCode.TooManyRequests) : Redirect(verified));
                if (uri == verified && throttleVerified)
                    return Task.FromResult(RangeUnavailable(TimeSpan.FromSeconds(40), HttpStatusCode.TooManyRequests));
                return Task.FromResult(ProgressiveResponse(request, data, served));
            }));
            Assert((await Read(0).WaitAsync(TimeSpan.FromSeconds(3))).SequenceEqual(data[..chunk]),
                "the fixture failed to read the verified recipient before the redirect changed");
            destination = temporary;
            var busy = await Throws<AudioBuffer.RangeUnavailableException>(() => Read(chunk).WaitAsync(TimeSpan.FromSeconds(3)));
            var deferred = await Throws<AudioBuffer.RangeUnavailableException>(() => Read(2 * chunk).WaitAsync(TimeSpan.FromSeconds(3)));
            Assert(deferred.RetryAt == busy.RetryAt && sent.Count(item => item.Uri == temporary) == 1
                && sent.Any(item => item.Uri == source.Uri && item.First == 2 * chunk),
                "the next seek skipped redirect discovery or resent a request to the paused temporary hop");
            destination = verified;
            Assert((await Read(3 * chunk).WaitAsync(TimeSpan.FromSeconds(3))).SequenceEqual(data[(3 * chunk)..(4 * chunk)]),
                "redirecting back to the verified recipient did not recover a new range");

            throttleVerified = true;
            var ownPause = await Throws<AudioBuffer.RangeUnavailableException>(() => Read(4 * chunk).WaitAsync(TimeSpan.FromSeconds(3)));
            var ownDeferred = await Throws<AudioBuffer.RangeUnavailableException>(() => Read(5 * chunk).WaitAsync(TimeSpan.FromSeconds(3)));
            Assert(ownDeferred.RetryAt == ownPause.RetryAt && !sent.Any(item => item.Uri == verified && item.First == 5 * chunk),
                "the verified recipient's own pause failed to cover a different range");
            destination = different;
            var changed = await Throws<MusicException>(() => Read(6 * chunk).WaitAsync(TimeSpan.FromSeconds(3)));
            Assert(changed.Message.Contains("изменился", StringComparison.Ordinal)
                && sent.All(item => item.Validator == (item.Uri == verified ? FixtureRangeTag.Tag : null)),
                "endpoint validation or the exact scope of If-Range changed");

            Task<byte[]> Read(long first) => buffer.ReadPlaybackRangeAsync(source, first, first + chunk - 1, data.Length, default, representation);
            static HttpResponseMessage Redirect(Uri target) => new(HttpStatusCode.Redirect) { Headers = { Location = target } };
        });

        await Run("Progressive playback: bounding paused redirect hops preserves live hints and the verified route", async () =>
        {
            var directory = Path.Combine(_directory, "progressive-bounded-range-pauses");
            var source = audio with { Uri = new Uri("https://s1.vlx.yandex.net/resolve-capped-range.mp3") };
            var verified = new Uri("https://s2.vlx.yandex.net/capped-range.mp3");
            var temporary = Enumerable.Range(0, 20).Select(i => new Uri($"https://s3.vlx.yandex.net/route-{i}.mp3")).ToArray();
            var destination = temporary[0];
            using var initial = new HttpResponseMessage(HttpStatusCode.PartialContent) { Headers = { ETag = FixtureRangeTag } };
            var representation = new AudioBuffer.PlaybackRepresentation(verified, initial.Headers, source.Uri);
            var requests = new ConcurrentBag<Uri>();
            var served = new ConcurrentBag<long>();
            byte[] data = [1, 2, 3, 4];
            using var buffer = new AudioBuffer(directory, new FakeHandler((request, _) =>
            {
                var uri = request.RequestUri!;
                requests.Add(uri);
                return Task.FromResult(uri == source.Uri
                    ? new HttpResponseMessage(HttpStatusCode.Redirect) { Headers = { Location = destination } }
                    : uri == verified ? ProgressiveResponse(request, data, served)
                    : RangeUnavailable(TimeSpan.FromSeconds(60), HttpStatusCode.TooManyRequests));
            }));
            var first = await Throws<AudioBuffer.RangeUnavailableException>(() => Read().WaitAsync(TimeSpan.FromSeconds(3)));
            foreach (var uri in temporary.Skip(1))
            {
                destination = uri;
                await Throws<AudioBuffer.RangeUnavailableException>(() => Read().WaitAsync(TimeSpan.FromSeconds(3)));
            }
            destination = temporary[0];
            var stillPaused = await Throws<AudioBuffer.RangeUnavailableException>(() => Read().WaitAsync(TimeSpan.FromSeconds(3)));
            destination = verified;
            Assert(stillPaused.RetryAt == first.RetryAt && requests.Count(uri => uri == temporary[0]) == 1
                && requests.Count(uri => temporary.Contains(uri)) < temporary.Length
                && (await Read().WaitAsync(TimeSpan.FromSeconds(3))).SequenceEqual(data),
                "the recipient limit evicted a live pause or prevented returning to the verified address");

            Task<byte[]> Read() => buffer.ReadPlaybackRangeAsync(source, 0, data.Length - 1, data.Length, default, representation);
        });

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
            foreach (var failure in new[] { "503", "429", "dns", "disconnect", "short", "timeout" })
            {
                var requests = new ConcurrentBag<long>();
                var attempts = 0;
                using var buffer = new AudioBuffer(directory, new FakeHandler(async (request, token) =>
                {
                    if (request.Headers.Range!.Ranges.Single().From != start || Interlocked.Increment(ref attempts) > 2)
                        return ProgressiveResponse(request, media, requests);
                    if (failure == "503") return RangeUnavailable();
                    if (failure == "429") return RangeUnavailable(TimeSpan.Zero, HttpStatusCode.TooManyRequests);
                    if (failure == "dns") throw new HttpRequestException(HttpRequestError.NameResolutionError, "fixture temporary DNS failure");
                    if (failure == "disconnect") throw new HttpRequestException(HttpRequestError.ConnectionError, "fixture disconnect");
                    if (failure == "timeout") await Task.Delay(Timeout.InfiniteTimeSpan, token);
                    var body = new ByteArrayContent(media.AsSpan(start, 5).ToArray());
                    body.Headers.ContentLength = start;
                    body.Headers.ContentRange = new ContentRangeHeaderValue(start, 2 * start - 1, media.Length);
                    return new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = body, Headers = { ETag = FixtureRangeTag } };
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

        await Run("Progressive playback: a new loopback reader rediscovers the same block after a redirect pause",
            () => CheckSameBlockRedirectAsync(TimeSpan.FromSeconds(60)));
        await Run("Progressive playback: a new loopback reader recovers the same block after HTTP 429 without Retry-After",
            () => CheckSameBlockRedirectAsync(null));

        async Task CheckSameBlockRedirectAsync(TimeSpan? pause)
        {
            const int start = ProgressiveAudio.ChunkBytes;
            var media = new byte[2 * start]; new Random(821).NextBytes(media);
            var source = audio with { Uri = new Uri("https://s1.vlx.yandex.net/resolve-same-block.mp3") };
            var verified = new Uri("https://s2.vlx.yandex.net/verified-same-block.mp3");
            var limited = new Uri("https://s3.vlx.yandex.net/limited-same-block.mp3");
            foreach (var originIsLimited in pause is null ? new[] { false } : new[] { false, true })
            {
                var directory = Path.Combine(_directory, $"progressive-same-block-redirect-{pause.HasValue}-{originIsLimited}");
                var requests = new ConcurrentBag<(Uri Uri, long First)>();
                var served = new ConcurrentBag<long>();
                var recovered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var routeRestored = 0;
                var pausedAt = new Stopwatch();
                using var buffer = new AudioBuffer(directory, new FakeHandler(async (request, token) =>
                {
                    var target = request.RequestUri!;
                    var first = request.Headers.Range!.Ranges.Single().From!.Value;
                    requests.Add((target, first));
                    if (target == source.Uri)
                    {
                        if (first == 0 || Volatile.Read(ref routeRestored) != 0)
                            return new HttpResponseMessage(HttpStatusCode.Redirect) { Headers = { Location = verified } };
                        if (!originIsLimited) return new HttpResponseMessage(HttpStatusCode.Redirect) { Headers = { Location = limited } };
                        pausedAt.Start();
                        return RangeUnavailable(pause, HttpStatusCode.TooManyRequests);
                    }
                    if (target == limited)
                    {
                        pausedAt.Start();
                        return RangeUnavailable(pause, HttpStatusCode.TooManyRequests);
                    }
                    if (first == start)
                    {
                        recovered.TrySetResult();
                        await release.Task.WaitAsync(token);
                    }
                    return ProgressiveResponse(request, media, served);
                }));
                using var input = await buffer.OpenPlaybackAsync(source, default);
                using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false });
                await Throws<HttpRequestException>(() => Read(start).WaitAsync(TimeSpan.FromSeconds(5)));
                Assert((await Read(0).WaitAsync(TimeSpan.FromSeconds(5))).SequenceEqual(media[..start]),
                    "a paused remote recipient made the cached prefix unavailable");
                await Task.Delay(650);
                if (originIsLimited)
                {
                    Volatile.Write(ref routeRestored, 1);
                    await Throws<HttpRequestException>(() => Read(start).WaitAsync(TimeSpan.FromSeconds(5)));
                    Assert(requests.Count(request => request == (source.Uri, start)) == 1,
                        "the origin's own pause permitted a new HTTP request to that origin");
                    input.Dispose();
                    await Throws<OperationCanceledException>(() => input.Completion.WaitAsync(TimeSpan.FromSeconds(5)));
                    continue;
                }

                var requestsBeforeRecovery = 1;
                if (pause is not null)
                {
                    await Throws<HttpRequestException>(() => Read(start).WaitAsync(TimeSpan.FromSeconds(5)));
                    requestsBeforeRecovery++;
                    Assert(requests.Count(request => request == (source.Uri, start)) == requestsBeforeRecovery
                        && requests.Count(request => request == (limited, start)) == 1,
                        "rediscovering the same block did not reach the resolver or resent to the paused hop");
                }
                else Assert(requests.Count(request => request == (source.Uri, start)) == 1 && !input.Completion.IsCompleted,
                    "prefetch retried or abandoned the hintless HTTP 429 without a new reader");

                Volatile.Write(ref routeRestored, 1);
                if (pause is not null) await Task.Delay(650);
                var readers = Task.WhenAll(Read(start), Read(start));
                try { await recovered.Task.WaitAsync(TimeSpan.FromSeconds(5)); }
                finally { release.TrySetResult(); }
                var chunks = await readers.WaitAsync(TimeSpan.FromSeconds(5));
                await input.Completion.WaitAsync(TimeSpan.FromSeconds(5));
                Assert(pausedAt.Elapsed < TimeSpan.FromSeconds(60)
                    && requests.Count(request => request == (limited, start)) == 1
                    && requests.Count(request => request == (source.Uri, start)) == requestsBeforeRecovery + 1
                    && requests.Count(request => request == (verified, start)) == 1
                    && chunks.All(bytes => bytes.SequenceEqual(media[start..])) && File.ReadAllBytes(input.Path).SequenceEqual(media),
                    "same-block rediscovery failed to share the recovered attempt or fill the progressive buffer");

                Task<byte[]> Read(long first) => ReadProgressiveFixtureRangeAsync(http, input, first, start);
            }
        }

        await Run("Progressive playback: a full recipient map reserves the initial intermediate hop and honors its own pause", async () =>
        {
            const int chunk = ProgressiveAudio.ChunkBytes;
            const int healthy = 21, hint = 22, deferred = 23, remembered = 24;
            var directory = Path.Combine(_directory, "progressive-reserved-initial-route");
            var data = new byte[25 * chunk]; new Random(823).NextBytes(data);
            var source = audio with { Uri = new Uri("https://s1.vlx.yandex.net/resolve-reserved-route.mp3") };
            var intermediate = new Uri("https://s3.vlx.yandex.net/initial-intermediate.mp3");
            var verified = new Uri("https://s2.vlx.yandex.net/verified-reserved-route.mp3");
            var temporary = Enumerable.Range(0, 20).Select(i => new Uri($"https://s3.vlx.yandex.net/reserved-route-{i}.mp3")).ToArray();
            var requests = new ConcurrentBag<(Uri Uri, int Block)>();
            var served = new ConcurrentBag<long>();
            using var buffer = new AudioBuffer(directory, new FakeHandler((request, _) =>
            {
                var target = request.RequestUri!;
                var block = (int)(request.Headers.Range!.Ranges.Single().From!.Value / chunk);
                requests.Add((target, block));
                if (target == source.Uri)
                {
                    var next = block is >= 1 and <= 20 ? temporary[block - 1] : block == remembered ? temporary[0] : intermediate;
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Redirect) { Headers = { Location = next } });
                }
                if (target == intermediate && block != hint)
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Redirect) { Headers = { Location = verified } });
                return Task.FromResult(target == verified
                    ? ProgressiveResponse(request, data, served)
                    : RangeUnavailable(TimeSpan.FromSeconds(60), HttpStatusCode.TooManyRequests));
            }));
            using var input = await buffer.OpenPlaybackAsync(source, default);
            using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false });
            for (var block = 1; block <= temporary.Length; block++)
                await Throws<HttpRequestException>(() => Read(block).WaitAsync(TimeSpan.FromSeconds(5)));
            Assert(requests.Count(request => temporary.Contains(request.Uri)) == 16
                && (await Read(healthy).WaitAsync(TimeSpan.FromSeconds(5))).SequenceEqual(data[(healthy * chunk)..((healthy + 1) * chunk)])
                && requests.Contains((intermediate, healthy)) && requests.Contains((verified, healthy)),
                "saturating temporary redirects displaced the validated initial hop");
            await Throws<HttpRequestException>(() => Read(remembered).WaitAsync(TimeSpan.FromSeconds(5)));
            await Throws<HttpRequestException>(() => Read(hint).WaitAsync(TimeSpan.FromSeconds(5)));
            await Throws<HttpRequestException>(() => Read(deferred).WaitAsync(TimeSpan.FromSeconds(5)));
            Assert(!requests.Contains((temporary[0], remembered)) && requests.Contains((intermediate, hint))
                && !requests.Contains((intermediate, deferred)) && !requests.Contains((verified, deferred)),
                "reserving a validated route bypassed a cooldown or discarded a live hint");

            Task<byte[]> Read(int block) => ReadProgressiveFixtureRangeAsync(http, input, block * (long)chunk, chunk);
        });

        await Run("Progressive playback: a verified late route replaces idle old hops in the full reserve", async () =>
        {
            var directory = Path.Combine(_directory, "progressive-renewed-late-route");
            var origin = audio with { Uri = new Uri("https://s1.vlx.yandex.net/renew-late-route.mp3") };
            var initialFirst = new Uri("https://s3.vlx.yandex.net/initial-first-hop.mp3");
            var initialSecond = new Uri("https://s3.vlx.yandex.net/initial-second-hop.mp3");
            var verified = new Uri("https://s2.vlx.yandex.net/renewed-verified-range.mp3");
            var late = Enumerable.Range(0, 5).Select(i => new Uri($"https://s3.vlx.yandex.net/verified-late-{i}.mp3")).ToArray();
            var temporary = Enumerable.Range(0, 20).Select(i => new Uri($"https://s3.vlx.yandex.net/busy-late-{i}.mp3")).ToArray();
            byte[] data = [1, 2, 3, 4];
            using var initial = new HttpResponseMessage(HttpStatusCode.PartialContent) { Headers = { ETag = FixtureRangeTag } };
            var representation = new AudioBuffer.PlaybackRepresentation(verified, initial.Headers, origin.Uri,
                [origin.Uri, initialFirst, initialSecond, verified]);
            var sent = new ConcurrentBag<Uri>();
            var served = new ConcurrentBag<long>();
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var destination = initialFirst;
            var holdOldest = 0;
            var pauseOld = false;
            var pauseCurrent = false;
            using var cancel = new CancellationTokenSource();
            using var buffer = new AudioBuffer(directory, new FakeHandler(async (request, token) =>
            {
                var uri = request.RequestUri!;
                sent.Add(uri);
                if (uri == origin.Uri) return Redirect(destination);
                if (uri == initialFirst) return Redirect(initialSecond);
                if (uri == verified) return ProgressiveResponse(request, data, served);
                if (uri == late[0] && Interlocked.Exchange(ref holdOldest, 0) == 1)
                {
                    entered.TrySetResult();
                    await release.Task.WaitAsync(token);
                }
                if (temporary.Contains(uri) || (uri == late[3] && pauseOld) || (uri == late[4] && pauseCurrent))
                    return RangeUnavailable(TimeSpan.FromSeconds(60), HttpStatusCode.TooManyRequests);
                return Redirect(verified);
            }));

            try
            {
                foreach (var hop in late.Take(4))
                {
                    destination = hop;
                    Assert((await Read()).SequenceEqual(data), "a healthy late route failed before filling the reserve");
                }
                pauseOld = true;
                destination = late[3];
                var oldPause = await Throws<AudioBuffer.RangeUnavailableException>(Read);
                destination = late[0];
                Interlocked.Exchange(ref holdOldest, 1);
                var active = Read();
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

                destination = late[4];
                Assert((await Read()).SequenceEqual(data), "a new late route failed to become verified");
                foreach (var hop in temporary)
                {
                    destination = hop;
                    await Throws<AudioBuffer.RangeUnavailableException>(Read);
                }
                Assert(sent.Count(temporary.Contains) == 16, "the fixture did not fill the temporary recipient map");
                destination = late[1];
                await Throws<AudioBuffer.RangeUnavailableException>(Read);
                Assert(sent.Count(uri => uri == late[1]) == 1, "the oldest idle hop unexpectedly retained its slot");

                destination = late[3];
                var stillPaused = await Throws<AudioBuffer.RangeUnavailableException>(Read);
                Assert(stillPaused.RetryAt == oldPause.RetryAt && sent.Count(uri => uri == late[3]) == 2,
                    "renewing the reserve shortened an older live pause");
                release.TrySetResult();
                Assert((await active).SequenceEqual(data), "renewing the reserve interrupted an active request");
                foreach (var hop in new[] { initialFirst, late[0], late[4] })
                {
                    destination = hop;
                    Assert((await Read()).SequenceEqual(data), "the initial, active, or newly verified route lost admission");
                }

                destination = late[4];
                pauseCurrent = true;
                var currentPause = await Throws<AudioBuffer.RangeUnavailableException>(Read);
                var deferred = await Throws<AudioBuffer.RangeUnavailableException>(Read);
                Assert(deferred.RetryAt == currentPause.RetryAt && sent.Count(uri => uri == late[4]) == 3,
                    "the newly verified hop bypassed its own pause");
            }
            finally { cancel.Cancel(); release.TrySetResult(); }

            Task<byte[]> Read() => buffer.ReadPlaybackRangeAsync(origin, 0, data.Length - 1, data.Length, cancel.Token, representation)
                .WaitAsync(TimeSpan.FromSeconds(5));
            static HttpResponseMessage Redirect(Uri uri) => new(HttpStatusCode.Redirect) { Headers = { Location = uri } };
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

    private static async Task<byte[]> ReadProgressiveFixtureRangeAsync(HttpClient http, PlaybackInput input, long first, int count)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, input.Uri);
        request.Headers.Range = new RangeHeaderValue(first, first + count - 1);
        using var response = await http.SendAsync(request);
        Assert(response.StatusCode == HttpStatusCode.PartialContent, "the local fixture did not receive a verified range");
        return await response.Content.ReadAsByteArrayAsync();
    }

    private static HttpResponseMessage RangeUnavailable(TimeSpan? delay = null, HttpStatusCode status = HttpStatusCode.ServiceUnavailable)
    {
        var response = new HttpResponseMessage(status);
        if (delay is { } wait) response.Headers.RetryAfter = new RetryConditionHeaderValue(wait);
        return response;
    }

    private static readonly EntityTagHeaderValue FixtureRangeTag = new("\"fixture-range\"");

    private static HttpResponseMessage ProgressiveResponse(HttpRequestMessage request, byte[] bytes, ConcurrentBag<long> requests)
    {
        var range = request.Headers.Range?.Ranges.Single() ?? throw new InvalidOperationException("CDN playback request lacks a bounded range");
        var first = range.From!.Value; var last = Math.Min(range.To!.Value, bytes.Length - 1);
        requests.Add(first);
        var content = new ByteArrayContent(bytes.AsSpan((int)first, (int)(last - first + 1)).ToArray());
        content.Headers.ContentRange = new ContentRangeHeaderValue(first, last, bytes.Length);
        return new(HttpStatusCode.PartialContent) { Content = content, Headers = { ETag = FixtureRangeTag } };
    }
}
