using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Windows.Media.MediaProperties;
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

        await Run("Quality offline 017: accepted response does not save audio and a later OK can succeed", async () =>
        {
            var directory = Path.Combine(_directory, "quality-offline017-http-status");
            var bufferPath = Path.Combine(directory, "buffer");
            var media = SilentMp3Seconds(1);
            var attempts = 0;
            using var library = new OfflineLibrary(Path.Combine(directory, "Music"), new AudioBuffer(bufferPath, new FakeHandler((_, _) =>
            {
                var response = ++attempts == 1
                    ? new HttpResponseMessage(HttpStatusCode.Accepted) { Content = new StringContent("Audio request accepted") }
                    : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(media) };
                return Task.FromResult(response);
            })));
            var track = new TrackRow { Id = "100", RequestId = "100:1", Title = "Fixture audio", DurationMs = 1000, Available = true };
            var audio = new AudioDescriptor(new Uri("https://s1.vlx.yandex.net/save.mp3"), "mp3", 320);
            var error = await Throws<MusicException>(() => library.SaveAsync(track, audio, default));
            Assert(error.Message.Contains("HTTP 202", StringComparison.Ordinal) && attempts == 1, "HTTP 202 was accepted as audio or retried");
            Assert(library.Read().Count == 0 && (!Directory.Exists(bufferPath) || !Directory.EnumerateFiles(bufferPath).Any())
                && (!Directory.Exists(library.DirectoryPath) || !Directory.EnumerateFiles(library.DirectoryPath).Any()),
                "HTTP 202 left bytes or an index entry on disk");

            var saved = await library.SaveAsync(track, audio, default);
            Assert(attempts == 2 && library.Read().Single().FileName == saved.FileName
                && File.ReadAllBytes(library.ResolvePath(saved.FileName)).SequenceEqual(media), "HTTP 200 did not save the expected audio");
        });

        await Run("Quality offline 017: only matching full audio can publish a new download", async () =>
        {
            var directory = Path.Combine(_directory, "quality-offline017-audio-evidence");
            var bufferPath = Path.Combine(directory, "buffer");
            var valid = SilentMp3Seconds(72, 192);
            const string key = "00112233445566778899aabbccddeeff";
            var encrypted = valid.ToArray();
            using (var cipher = new AesCounter(key)) cipher.Transform(encrypted);
            var supplied = encrypted;
            using var library = new OfflineLibrary(Path.Combine(directory, "Music"), new AudioBuffer(bufferPath, new FakeHandler((_, _) =>
            {
                var content = new ByteArrayContent(supplied);
                content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
            })));
            static TrackRow Track(long durationMs) => new()
                { Id = "101", RequestId = "101:1", Title = "Fixture audio", DurationMs = durationMs, Available = true };
            var track = Track(72_000);
            var audio = new AudioDescriptor(new Uri("https://s1.vlx.yandex.net/save.mp3"), "mp3", 192, key);
            var earlier = await library.SaveAsync(track, audio, default);
            var index = Path.Combine(library.DirectoryPath, "library.index.json");
            var originalIndex = File.ReadAllBytes(index);
            var original = library.ResolvePath(earlier.FileName);

            var aacLc = await EncodeAudioFixtureAsync("m4a", MediaEncodingProfile.CreateM4a(AudioEncodingQuality.High));
            foreach (var (body, responseKey, codec, duration, expectedError) in new (byte[], string?, string, long, string)[]
            {
                (CreateSilentPcmWave(44), null, "mp3", 72_000, "звуковой дорожки"),
                (CreateSilentPcmWave(4096), null, "mp3", 72_000, "звуковой дорожки"),
                (new byte[4096], null, "mp3", 72_000, "звуковой дорожки"),
                (encrypted, "ffeeddccbbaa99887766554433221100", "mp3", 72_000, "звуковой дорожки"),
                (SilentMp3Seconds(30), null, "mp3", 72_000, "фрагмент"),
                (SilentMp3Seconds(72), null, "mp3", 240_000, "существенно меньше"),
                (valid, null, "mp3", 72_000, "битрейт"),
                (aacLc, null, "flac-mp4", 72_000, "звуковой дорожки"),
                (aacLc, null, "he-aac-mp4", 1_000, "звуковой дорожки")
            })
            {
                supplied = body;
                var error = await Throws<MusicException>(() => library.SaveAsync(Track(duration),
                    audio with { Codec = codec, Bitrate = codec == "flac-mp4" ? 0 : 320, Key = responseKey }, default));
                Assert(error.Message.Contains(expectedError, StringComparison.Ordinal)
                    && library.Read().Single().FileName == earlier.FileName && File.ReadAllBytes(index).SequenceEqual(originalIndex)
                    && File.ReadAllBytes(original).SequenceEqual(valid) && Directory.GetFiles(library.DirectoryPath, "*.mp3").Length == 1
                    && !Directory.EnumerateFiles(bufferPath).Any(), "rejected audio changed the library or retained a temporary file");
            }
            Assert(!AudioQualityPolicy.IsSufficient(earlier.Codec, earlier.QualityBitrate, AudioQuality.Excellent),
                "the confirmed 192 kbit/s version qualified as Excellent");
            supplied = [.. SilentMp3Seconds(3, 192), .. SilentMp3Seconds(3, 320)];
            var vbr = await library.SaveAsync(Track(7000), audio with { Key = null, Bitrate = 260 }, default);
            Assert(vbr.Bitrate == 260 && vbr.ConfirmedBitrate == 256 && library.Read().Count == 2,
                "normal VBR bitrate or duration rounding prevented saving a valid variant");
        });

        await Run("Quality offline 017: a truncated MP3 frame cannot publish without catalogue metadata", async () =>
        {
            var directory = Path.Combine(_directory, "quality-offline017-mp3-tail");
            var bufferPath = Path.Combine(directory, "buffer");
            var full = SilentMp3Seconds(3, 192);
            var supplied = full;
            var requests = 0;
            using var library = new OfflineLibrary(Path.Combine(directory, "Music"), new AudioBuffer(bufferPath, new FakeHandler((_, _) =>
            {
                requests++;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(supplied) });
            })));
            var track = new TrackRow { Id = "105", RequestId = "105:1", Title = "MP3 frame fixture", Available = true };
            var audio = new AudioDescriptor(new Uri("https://s1.vlx.yandex.net/fixture.mp3"), "mp3", 0);
            var original = await library.SaveAsync(track, audio with { Bitrate = 192 }, default);
            var originalPath = library.ResolvePath(original.FileName);
            var index = Path.Combine(library.DirectoryPath, "library.index.json");
            var originalIndex = File.ReadAllBytes(index);

            supplied = full[..^1];
            var refusal = await Throws<MusicException>(() => library.SaveAsync(track, audio, default));
            Assert(refusal.Message.Contains("до конца", StringComparison.Ordinal) && requests == 2
                && library.Read().Single().FileName == original.FileName && File.ReadAllBytes(index).SequenceEqual(originalIndex)
                && File.ReadAllBytes(originalPath).SequenceEqual(full) && !Directory.EnumerateFiles(bufferPath).Any(),
                "a definite partial final frame changed the offline library or retained a temporary candidate");

            var tagged = new byte[full.Length + 128];
            full.CopyTo(tagged, 0);
            "TAG"u8.CopyTo(tagged.AsSpan(full.Length));
            supplied = tagged;
            var saved = await library.SaveAsync(track, audio, default);
            Assert(requests == 3 && saved.Bitrate == 0 && saved.ConfirmedBitrate == 192 && library.Read().Count == 2
                && File.ReadAllBytes(library.ResolvePath(saved.FileName)).SequenceEqual(tagged),
                "a full MP3 with ID3v1 and omitted nominal bitrate lost its measured rate");

            var unclassified = Path.Combine(directory, "unclassified-tail.mp3");
            File.WriteAllBytes(unclassified, [.. full, 0]);
            var unknown = Mp3FrameEvidence.Read(unclassified, 0, default);
            Assert(unknown.Frames is null && !unknown.IncompleteFrame, "an unknown tail was reported as an incomplete audio frame");

            var completeFrame = full.ToArray();
            "TAG"u8.CopyTo(completeFrame.AsSpan(completeFrame.Length - 128));
            File.WriteAllBytes(unclassified, completeFrame);
            var complete = Mp3FrameEvidence.Read(unclassified, 0, default);
            Assert(complete.Frames?.KilobitsPerSecond == 192 && !complete.IncompleteFrame,
                "an incidental signature inside a complete frame was mistaken for a tag");
        });

        await Run("Quality offline 017: AAC tail reads and selected audio duration govern admission independently of bitrate", async () =>
        {
            var audio = new AudioDescriptor(new Uri("https://s1.vlx.yandex.net/fixture.m4a"), "aac-mp4", 256);
            var presentation = TimeSpan.FromSeconds(240);
            var selected = TimeSpan.FromSeconds(72);
            const uint error = 1, endOfStream = 2, streamTick = 0x100;
            var sample = new LoudnessAnalysis.AacPacketRead(0, 0, HasSample: true, Bytes: 20_000, DurationTicks: TimeSpan.TicksPerSecond);
            var end = new LoudnessAnalysis.AacPacketRead(0, endOfStream, HasSample: false);
            OfflineAudioEvidence Read(params LoudnessAnalysis.AacPacketRead[] packets)
            {
                var reader = new Queue<LoudnessAnalysis.AacPacketRead>(packets);
                return LoudnessAnalysis.ReadAacEvidence(presentation, fileBytes: 2_000_000, reader.Dequeue, default);
            }

            foreach (var failed in new[] { end with { Status = -1 }, end with { Flags = error | endOfStream } })
            {
                var evidence = Read(sample with { Bytes = null, DurationTicks = null }, sample, failed);
                var refusal = await Throws<MusicException>(() => Task.FromResult(OfflineAudioProbe.ConfirmAudioEvidence(evidence, audio, 240_000)));
                Assert(refusal.Message.Contains("до конца", StringComparison.Ordinal), "a definite late read error was treated as unknown bitrate");
            }

            var packets = Enumerable.Repeat(sample, (int)selected.TotalSeconds).ToArray();
            var complete = Read([.. packets, end]);
            Assert(complete.Duration == selected && OfflineAudioProbe.ConfirmAudioEvidence(complete, audio, 72_000) == 160,
                "AAC measurement used the whole presentation instead of the selected audio duration");
            var shortTrack = await Throws<MusicException>(() => Task.FromResult(OfflineAudioProbe.ConfirmAudioEvidence(complete, audio, 240_000)));
            Assert(shortTrack.Message.Contains("существенно меньше", StringComparison.Ordinal), "a short selected audio track passed using the presentation duration");

            var unknownRate = Read([sample with { Bytes = null }, .. packets.Skip(1), end]);
            Assert(unknownRate.Duration == selected && OfflineAudioProbe.ConfirmAudioEvidence(unknownRate, audio, 72_000) is null,
                "unknown compressed byte length discarded verified timing or advertised a nominal bitrate");
            await Throws<MusicException>(() => Task.FromResult(OfflineAudioProbe.ConfirmAudioEvidence(unknownRate, audio, 240_000)));

            var unknownTiming = Read(sample with { DurationTicks = null }, end);
            var gap = Read(sample, new(0, streamTick, HasSample: false), end);
            Assert(unknownTiming.Duration == presentation && gap.Duration == presentation
                && OfflineAudioProbe.ConfirmAudioEvidence(unknownTiming, audio, 240_000) is null
                && OfflineAudioProbe.ConfirmAudioEvidence(gap, audio, 240_000) is null,
                "unsupported timing or a deliberate stream gap made playable AAC fail admission or gain an estimated rate");
        });

        await Run("Quality offline 017: persisted measured bitrate governs labels and verified quality", async () =>
        {
            var directory = Path.Combine(_directory, "quality-offline017-confirmed-rate");
            var bufferPath = Path.Combine(directory, "buffer");
            var aacBytes = await EncodeAudioFixtureAsync("m4a", MediaEncodingProfile.CreateM4a(AudioEncodingQuality.High), seconds: 3);
            var lowerBytes = await EncodeAudioFixtureAsync("m4a", MediaEncodingProfile.CreateM4a(AudioEncodingQuality.Low), seconds: 3);
            var suppliedAac = lowerBytes;
            var mp3Bytes = SilentMp3Seconds(3);
            var calls = 0;
            using var library = new OfflineLibrary(Path.Combine(directory, "Music"), new AudioBuffer(bufferPath, new FakeHandler((request, _) =>
            {
                calls++;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    { Content = new ByteArrayContent(request.RequestUri!.AbsolutePath.EndsWith(".m4a", StringComparison.Ordinal) ? suppliedAac : mp3Bytes) });
            })));
            var track = new TrackRow { Id = "104", RequestId = "104:1", Title = "Confirmed rate fixture", DurationMs = 3000, Available = true };
            var aac = new AudioDescriptor(new Uri("https://s1.vlx.yandex.net/save.m4a"), "aac-mp4", 256);
            var mp3 = new AudioDescriptor(new Uri("https://s1.vlx.yandex.net/save.mp3"), "mp3", 192);
            var earlierAac = await library.SaveAsync(track, aac, default);
            suppliedAac = aacBytes;
            var savedAac = await library.SaveAsync(track, aac, default, recheckQuality: true);
            var earlierPath = library.ResolvePath(earlierAac.FileName);
            Assert(savedAac.Bitrate == 256 && savedAac.ConfirmedBitrate is >= 180 and <= 220
                && !AudioQualityPolicy.IsSufficient(savedAac.Codec, savedAac.QualityBitrate, AudioQuality.Excellent)
                && OfflineLibrary.QualityLabel(savedAac).Contains($" · {savedAac.ConfirmedBitrate} кбит/с", StringComparison.Ordinal),
                "ordinary AAC used the advertised 256 kbit/s");
            Assert(earlierAac.QualityBitrate > 0 && earlierAac.QualityBitrate < savedAac.QualityBitrate
                && library.Find(track)?.FileName == savedAac.FileName
                && library.Read().Any(item => item.FileName == earlierAac.FileName && item.ConfirmedBitrate == earlierAac.ConfirmedBitrate)
                && File.ReadAllBytes(earlierPath).SequenceEqual(lowerBytes)
                && library.ResolvePlaybackSource(library.Tracks().Single(row => row.LocalPath == earlierPath), requireExactOfflineVariant: true)?.Path == earlierPath,
                "an improved realization of the nominal variant unindexed or displaced the explicit older file");
            var retriedAac = await library.SaveAsync(track, aac, default, recheckQuality: true);
            Assert(calls == 3 && retriedAac.FileName == savedAac.FileName && library.Read().Count == 2,
                "rechecking quality deduplicated before evidence or created an identical file again");
            var savedMp3 = await library.SaveAsync(track, mp3, default);
            var mp3Path = library.ResolvePath(savedMp3.FileName);
            Assert(savedMp3.Bitrate == 192 && savedMp3.ConfirmedBitrate == 320 && library.Find(track)?.FileName == savedMp3.FileName
                && AudioQualityPolicy.IsSufficient(savedMp3.Codec, savedMp3.QualityBitrate, AudioQuality.Excellent)
                && OfflineLibrary.QualityLabel(savedMp3) == "MP3 · 320 кбит/с",
                "actual MP3 bitrate did not drive the index and ranking");

            var index = Path.Combine(library.DirectoryPath, "library.index.json");
            var older = JsonSerializer.SerializeToUtf8Bytes(new[] { savedMp3 with { ConfirmedBitrate = null } });
            await File.WriteAllBytesAsync(index, older);
            using var reopening = new OfflineLibrary(library.DirectoryPath, new AudioBuffer(Path.Combine(directory, "reopen-buffer"),
                new FakeHandler((_, _) => throw new InvalidOperationException("Legacy index read contacted the CDN"))));
            var legacy = reopening.Read().Single();
            Assert(legacy.Bitrate == 192 && legacy.ConfirmedBitrate is null && legacy.FileName == savedMp3.FileName
                && OfflineLibrary.QualityLabel(legacy) == "MP3", "legacy rendition identity or bitrate certainty changed");
            var queue = new DownloadQueueState();
            queue.Replace([track], "Confirmed rate fixture");
            queue.Pause();
            var pending = await DownloadQueueStore.PrepareRestoreAsync(
                DownloadQueueStore.Capture("fixture", queue, AudioQuality.Excellent, false, null), reopening, default);
            queue.Entries[0].Complete(legacy.Bytes);
            var completed = await DownloadQueueStore.PrepareRestoreAsync(
                DownloadQueueStore.Capture("fixture", queue, AudioQuality.Excellent, false, null), reopening, default);
            Assert(pending.Entries.Single().State == DownloadEntryState.Paused
                && completed.Entries.Single().State == DownloadEntryState.Completed
                && completed.Entries.Single().Track.DownloadedPath == mp3Path
                && File.ReadAllBytes(index).SequenceEqual(older), "legacy refresh claimed premium quality or disturbed completed work");
            var confirmed = await library.SaveAsync(track, mp3, default, recheckQuality: true);
            Assert(confirmed.FileName == savedMp3.FileName && confirmed.ConfirmedBitrate == 320
                && reopening.Read().Single().ConfirmedBitrate == 320 && File.ReadAllBytes(mp3Path).SequenceEqual(mp3Bytes)
                && Directory.GetFiles(library.DirectoryPath, "*.mp3").Single() == mp3Path,
                "identical verified legacy bytes changed the existing path or created a second file");
        });

        await Run("Quality offline 017: cancelling a delayed decoder releases the download slot and retains its protected buffer", async () =>
        {
            var directory = Path.Combine(_directory, "quality-offline017-audio-probe-cancel");
            var bufferPath = Path.Combine(directory, "buffer");
            var media = SilentMp3Seconds(1);
            using var library = new OfflineLibrary(Path.Combine(directory, "Music"), new AudioBuffer(bufferPath, new FakeHandler((_, _) =>
                Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(media) }))));
            library.ConcurrentDownloads = 1;
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            library.SaveCheckpoint = point =>
            {
                if (point != "save-before-audio-probe") return;
                entered.TrySetResult();
                release.Task.GetAwaiter().GetResult();
            };
            using var cancellation = new CancellationTokenSource();
            var track = new TrackRow { Id = "102", RequestId = "102:1", Title = "Delayed decoder", DurationMs = 1000, Available = true };
            var next = new TrackRow { Id = "103", RequestId = "103:1", Title = "Next download", DurationMs = 1000, Available = true };
            var audio = new AudioDescriptor(new Uri("https://s1.vlx.yandex.net/save.mp3"), "mp3", 192);
            var first = library.SaveAsync(track, audio, cancellation.Token);
            try
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                var held = Directory.GetFiles(bufferPath, "*.mp3").Single();
                cancellation.Cancel();
                await Throws<OperationCanceledException>(() => first.WaitAsync(TimeSpan.FromSeconds(3)));
                Assert(library.Read().Count == 0 && File.ReadAllBytes(held).SequenceEqual(media), "cancelled download published audio or released a live buffer");
                await Throws<IOException>(() =>
                {
                    using var writer = File.Open(held, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
                    return Task.CompletedTask;
                });
                library.SaveCheckpoint = null;
                await library.SaveAsync(next, audio, default).WaitAsync(TimeSpan.FromSeconds(5));
                Assert(File.Exists(held) && library.Read().Single().Id == next.Id, "cancelled decoder retained the download slot or released its buffer early");
            }
            finally { release.TrySetResult(); }
            await Until(() => !Directory.EnumerateFiles(bufferPath).Any());
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

        await Run("Quality offline 017: logout keeps local preloading and anonymous lyrics available", async () =>
        {
            using var f = new QualityOffline017Fixture("signed-out-local");
            var previous = PreloadGet<CancellationTokenSource>(f.Window, "_accountCts").Token;
            f.Window.SignOut();
            Assert(previous.IsCancellationRequested && !PreloadGet<CancellationTokenSource>(f.Window, "_accountCts").IsCancellationRequested,
                "logout did not replace the cancelled account scope");
            var current = f.Local("guest-current", artist: "Fixture Artist");
            var next = f.Local("guest-next");
            var requests = 0;
            using var http = new HttpClient(new LrclibHandler((request, _) =>
            {
                requests++;
                Assert(request.Headers.Authorization is null && request.RequestUri!.Host == "lrclib.net",
                    "post-logout lyrics lookup reached an unexpected host or used an account");
                return Task.FromResult(Json(LrclibRecord(artist: current.Artist, title: current.Title, duration: 40)));
            }));
            PreloadGet<LrclibLyricsClient>(f.Window, "_lrclibLyrics").Dispose();
            var flags = BindingFlags.Instance | BindingFlags.NonPublic;
            typeof(MainWindow).GetField("_lrclibLyrics", flags)!.SetValue(f.Window, new LrclibLyricsClient(http));
            typeof(MainWindow).GetField("_geniusLyrics", flags)!.SetValue(f.Window,
                new GeniusLyricsClient(new FakeHandler((_, _) => throw new InvalidOperationException("unexpected Genius fallback"))));
            f.PlaybackQueue.SetItems([current, next], current);
            PreloadInvoke(f.Window, "SetLyricsVisible", true);

            await f.Window.PlayTrackAsync(current);
            await PreloadAwaitPrepared(f.Window);
            await Until(() => Control<ListBox>(f.Window, "LyricsList").Items.Count == 2);
            Assert(f.Player.IsPlaying && f.Player.PreparedLocalPath == next.LocalPath && requests == 1
                && f.ApiRequests == 0 && f.PlaybackRequests == 0, "guest lost local preload or anonymous lyrics after logout");
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

        await Run("Quality offline 017: same-size changed offline audio never reaches a decoder and retains the target occurrence", async () =>
        {
            using var f = new QualityOffline017Fixture("integrity-offline");
            var saved = await f.Save(f.Row("indexed"), 192);
            var path = f.Library.ResolvePath(saved.FileName);
            var replacement = ReplaceWithSameLengthSilentWave(path, saved);
            var target = f.Library.Tracks().Single();
            var after = f.Local("after");
            f.PlaybackQueue.SetItems([target, after], target);
            var occurrence = f.PlaybackQueue.OccurrenceTokenAt(0);
            await Call(f.Window, "PlayTrackCoreAsync", target, null!, false, null!, false, true, null!);
            Assert(ReferenceEquals(f.PlaybackQueue.Current, target) && f.PlaybackQueue.CurrentOccurrenceToken == occurrence
                && f.PlaybackQueue.History.Count == 0 && !f.Player.HasSource && !target.IsPlaying
                && f.ApiRequests == 0 && f.PlaybackRequests == 0,
                "offline-only refusal opened a source, navigated away from the target, or used the network");
            Assert(Control<TextBlock>(f.Window, "StatusText").Text.Contains("Содержимое скачанного файла изменилось", StringComparison.Ordinal)
                && File.ReadAllBytes(path).SequenceEqual(replacement) && f.Library.Read().Single().Sha256 == saved.Sha256,
                "offline refusal omitted its integrity status or changed the indexed file");
        });

        await Run("Quality offline 017: same-size changed download falls back once for an online-capable occurrence", async () =>
        {
            foreach (var preload in new[] { false, true })
            {
                using var f = new QualityOffline017Fixture("integrity-online-" + preload);
                var target = f.Row("indexed");
                var saved = await f.Save(target, 320);
                var path = f.Library.ResolvePath(saved.FileName);
                var replacement = ReplaceWithSameLengthSilentWave(path, saved);
                long occurrence;
                if (preload)
                {
                    var first = f.Local("first");
                    f.PlaybackQueue.SetItems([first, target], first);
                    occurrence = f.PlaybackQueue.OccurrenceTokenAt(1);
                    await f.Window.PlayTrackAsync(first);
                    await PreloadAwaitPrepared(f.Window);
                    Assert(f.Player.PreparedLocalPath != path && f.Player.SourceLocalPath == first.LocalPath,
                        "preload did not prepare the permitted online fallback");
                    await Call(f.Window, "AdvanceAsync", 1, false);
                }
                else
                {
                    f.PlaybackQueue.SetItems([target], target);
                    occurrence = f.PlaybackQueue.OccurrenceTokenAt(0);
                    await f.Window.PlayTrackAsync(target);
                }
                Assert(ReferenceEquals(f.PlaybackQueue.Current, target) && f.PlaybackQueue.CurrentOccurrenceToken == occurrence
                    && f.Player.IsPlaying && f.Player.SourceLocalPath != path && f.ModernRequests == 1 && f.PlaybackRequests == 1
                    && !Control<TextBlock>(f.Window, "QualityText").Text.StartsWith("Офлайн", StringComparison.Ordinal),
                    "permitted online fallback changed occurrence, reopened, or played the indexed file");
                Assert(File.ReadAllBytes(path).SequenceEqual(replacement) && f.Library.Read().Single().Sha256 == saved.Sha256,
                    "online fallback altered the preserved local data");
            }
        });

        await Run("Quality offline 017: incompatible sharing skips to a verified queue variant without changing the occurrence", async () =>
        {
            foreach (var preload in new[] { false, true })
            {
                using var f = new QualityOffline017Fixture("busy-variant-" + preload, signedIn: false);
                var row = f.Row("indexed");
                var low = await f.Save(row, 160);
                var middle = await f.Save(row, 192);
                var high = await f.Save(row, 320);
                var lowPath = f.Library.ResolvePath(low.FileName);
                var highPath = f.Library.ResolvePath(high.FileName);
                var exact = f.Library.Tracks().Single(item => item.LocalPath == highPath);
                using (File.Open(highPath, FileMode.Open, FileAccess.Read, FileShare.None))
                using (File.Open(f.Library.ResolvePath(middle.FileName), FileMode.Open, FileAccess.Read, FileShare.None))
                {
                    Assert(f.Library.ResolvePlaybackSource(row)?.Path == highPath,
                        "fixture sharing conflict was resolved before the held file check");
                    long occurrence;
                    if (preload)
                    {
                        var first = f.Local("first");
                        f.PlaybackQueue.SetItems([first, row], first);
                        occurrence = f.PlaybackQueue.OccurrenceTokenAt(1);
                        await f.Window.PlayTrackAsync(first);
                        await PreloadAwaitPrepared(f.Window);
                        var verifiedSlot = PreloadGet<object>(f.Player, "_prepared");
                        Assert(f.Player.PreparedLocalPath == lowPath && f.Player.SourceLocalPath == first.LocalPath,
                            "preload stopped at a busy variant or affected the playing source");
                        await Call(f.Window, "AdvanceAsync", 1, false);
                        Assert(ReferenceEquals(verifiedSlot, PreloadGet<object>(f.Player, "_current")),
                            "verified fallback was opened again during promotion");
                    }
                    else
                    {
                        f.PlaybackQueue.SetItems([exact], exact);
                        await Call(f.Window, "PlayTrackCoreAsync", exact, null!, false, null!, false, true, null!);
                        Assert(!f.Player.HasSource && ReferenceEquals(f.PlaybackQueue.Current, exact) && !exact.IsDownloaded,
                            "denied exact playback substituted a source or retained its playable badge");
                        f.PlaybackQueue.SetItems([row], row);
                        occurrence = f.PlaybackQueue.OccurrenceTokenAt(0);
                        await f.Window.PlayTrackAsync(row);
                    }
                    Assert(f.Player.IsPlaying && f.Player.SourceLocalPath == lowPath && f.PlaybackQueue.CurrentOccurrenceToken == occurrence
                        && f.ApiRequests == 0 && f.PlaybackRequests == 0 && exact.IsDownloaded == preload,
                        "temporary sharing affected queue identity, caused traffic, or poisoned the exact marker");
                }
                await Call(f.Window, "PlayTrackCoreAsync", exact, null!, false, null!, false, true, null!);
                Assert(f.Player.IsPlaying && f.Player.SourceLocalPath == highPath && exact.IsDownloaded && f.ApiRequests == 0,
                    "explicit playback did not retry the exact file after its temporary lock cleared");
            }
        });

        await Run("Quality offline 017: the same exact queue row can retry a denied activation without reopening", async () =>
        {
            using var f = new QualityOffline017Fixture("exact-row-after-sharing", signedIn: false);
            var saved = await f.Save(f.Row("blocked"), 320);
            var path = f.Library.ResolvePath(saved.FileName);
            var exact = f.Library.Tracks().Single();
            f.PlaybackQueue.SetItems([exact], exact);
            var occurrence = f.PlaybackQueue.CurrentOccurrenceToken;
            SaInvoke(f.Window, "ShowQueueCore", false);
            var list = Control<ListView>(f.Window, "TracksList");
            using (File.Open(path, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                await Call(f.Window, "ActivateTrackRowAsync", list.Items.IndexOf(exact));
                await Call(f.Window, "ActivateTrackRowAsync", list.Items.IndexOf(exact));
                Assert(!exact.IsDownloaded && !f.Player.HasSource && f.PlaybackQueue.CurrentOccurrenceToken == occurrence,
                    "the exact copy was marked, substituted, or moved while the lock was still held");
            }
            await Call(f.Window, "ActivateTrackRowAsync", list.Items.IndexOf(exact));
            Assert(exact.IsDownloaded && f.Player.IsPlaying && f.Player.SourceLocalPath == path
                && f.PlaybackQueue.CurrentOccurrenceToken == occurrence && f.ApiRequests == 0,
                "the same verified occurrence did not recover through exact activation");
        });

        await Run("Quality offline 017: exact row retry respects Pause Stop and the sleep timer", async () =>
        {
            foreach (var command in new[] { "pause", "desktop-pause", "sleep", "stop" })
                await QualityExplicitRetryOwnership(command, expanded: false, ordinaryCatalog: false);
        });
        await Run("Quality offline 017: unavailable catalog rows can retry from either explicit queue", async () =>
        {
            foreach (var expanded in new[] { false, true })
                await QualityExplicitRetryOwnership("continue", expanded, ordinaryCatalog: true);
        });

        await Run("Quality offline 017: held mismatch clears only that revision and repaired bytes restore the exact badge", async () =>
        {
            using var f = new QualityOffline017Fixture("rejected-revision", signedIn: false);
            var catalogue = f.Row("indexed");
            var low = await f.Save(catalogue, 192);
            var high = await f.Save(catalogue, 320);
            var highPath = f.Library.ResolvePath(high.FileName);
            var lowPath = f.Library.ResolvePath(low.FileName);
            var original = File.ReadAllBytes(highPath);
            ReplaceWithSameLengthSilentWave(highPath, high);
            var rows = f.Library.Tracks();
            var exact = rows.Single(track => track.LocalPath == highPath);
            var healthy = rows.Single(track => track.LocalPath == lowPath);
            var all = new[] { exact, healthy, catalogue };
            var first = f.Local("first");
            PreloadInvoke(f.Window, "ShowRows", (object)all);
            f.PlaybackQueue.SetItems([first, catalogue], first);
            var occurrence = f.PlaybackQueue.OccurrenceTokenAt(1);
            await f.Window.PlayTrackAsync(first);
            await PreloadAwaitPrepared(f.Window);
            var verifiedSlot = PreloadGet<object>(f.Player, "_prepared");
            Assert(f.Player.PreparedLocalPath == lowPath && f.Player.SourceLocalPath == first.LocalPath, "mismatch did not prepare the verified alternative");
            CheckMarks();
            Assert(!f.Library.Tracks().Single(track => track.LocalPath == highPath).IsDownloaded,
                "opening a new offline view restored the rejected exact mark before background refresh");

            PreloadInvoke(f.Window, "ApplyOfflineStates", (object)all);
            CheckMarks();
            PreloadInvoke(f.Window, "QueueOfflineMarks", (object)all);
            await PreloadGet<Task>(f.Window, "_offlineMarksTask").WaitAsync(TimeSpan.FromSeconds(5));
            CheckMarks();
            await Call(f.Window, "AdvanceAsync", 1, false);
            Assert(f.PlaybackQueue.CurrentOccurrenceToken == occurrence && f.Player.SourceLocalPath == lowPath
                && ReferenceEquals(verifiedSlot, PreloadGet<object>(f.Player, "_current")) && f.ApiRequests == 0,
                "mark refresh invalidated the ready fallback or changed the queue occurrence");

            var length = new FileInfo(highPath).Length;
            var written = File.GetLastWriteTimeUtc(highPath);
            File.WriteAllBytes(highPath, original);
            File.SetLastWriteTimeUtc(highPath, written);
            Assert(new FileInfo(highPath).Length == length, "fixture repair changed the expected length");
            PreloadInvoke(f.Window, "ApplyOfflineStates", (object)all);
            Assert(exact.IsDownloaded && exact.DownloadedPath == highPath && catalogue.DownloadedPath == highPath
                && healthy.DownloadedPath == lowPath && f.Player.SourceLocalPath == lowPath,
                "known rejection survived repair of the exact revision or poisoned the other quality");

            void CheckMarks() => Assert(!exact.IsDownloaded && !exact.CanPlay && exact.CanPlayInQueue
                && healthy.DownloadedPath == lowPath && catalogue.DownloadedPath == lowPath,
                "confirmed mismatch did not distinguish the damaged exact row from a usable queue version");
        });

        await Run("Quality offline 017: Play honors the selected indexed version and replaces its missing file", async () =>
        {
            using var f = new QualityOffline017Fixture("selected-version", signedIn: false);
            var track = f.Row("track");
            var selected = await f.Save(track, 192);
            var alternate = await f.Save(track, 320);
            var selectedPath = f.Library.ResolvePath(selected.FileName);
            var row = f.Library.Tracks().Single(row => row.LocalPath == selectedPath);

            await f.Window.PlayTrackAsync(row);
            Assert(f.Player.IsPlaying && f.Player.SourceLocalPath == selectedPath, "Play ignored the explicitly selected version");
            var couldOpenForWrite = false;
            try
            {
                using var writer = File.Open(selectedPath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
                couldOpenForWrite = true;
            }
            catch (IOException) { }
            Assert(!couldOpenForWrite, "playing local source did not hold a read lease");
            f.Player.Stop();
            await f.Player.WaitForClosedLocalSourcesAsync(new HashSet<string> { selectedPath });
            File.Delete(selectedPath);

            await f.Window.PlayTrackAsync(row);
            Assert(f.Player.IsPlaying && f.Player.SourceLocalPath == f.Library.ResolvePath(alternate.FileName)
                && f.ApiRequests == 0 && f.PlaybackRequests == 0, "Play did not use the remaining valid local version");
        });
        await Run("Quality offline 017: Previous validates its history before selecting a saved occurrence", async () =>
        {
            foreach (var alternateExists in new[] { false, true })
            {
                using var f = new QualityOffline017Fixture("previous-stale-" + alternateExists, signedIn: false);
                var a = await f.Save(f.Row("first"), 192); var b = await f.Save(f.Row("middle"), 192);
                var c = await f.Save(f.Row("last"), 192);
                var alternate = alternateExists ? await f.Save(f.Row("middle"), 320) : null;
                var paths = new[] { a, b, c }.Select(item => f.Library.ResolvePath(item.FileName)).ToArray();
                var rows = paths.Select(path => f.Library.Tracks().Single(row => row.LocalPath == path)).ToArray();
                var queue = f.PlaybackQueue;
                f.Player.SetCrossfade(0);
                queue.SetItems(rows);
                foreach (var row in rows) await f.Window.PlayTrackAsync(row);
                await f.Player.WaitForClosedLocalSourcesAsync(new HashSet<string> { paths[1] });
                File.Delete(paths[1]);
                Assert(rows[1].CanPlayInQueue && queue.History.Count == 2, "fixture failed to keep a stale previous mark");
                var targetIndex = alternateExists ? 1 : 0;
                var expected = alternate is null ? paths[0] : f.Library.ResolvePath(alternate.FileName);
                var token = queue.OccurrenceTokenAt(targetIndex);
                if (alternateExists)
                {
                    var current = queue.CurrentOccurrenceToken; var version = queue.Version;
                    UlSet(f.Window, "_offlineMaintenance", true);
                    UlSet(f.Window, "_offlineRemovalTrackId", rows[1].Id);
                    await f.Window.HandleDesktopMediaCommandAsync(DesktopMediaAction.Previous);
                    Assert(f.Player.IsPlaying && f.Player.SourceLocalPath == paths[2] && queue.CurrentOccurrenceToken == current
                        && queue.Version == version && queue.History.Count == 2, "blocked alternate consumed history or changed the active source");
                    UlSet(f.Window, "_offlineMaintenance", false);
                }
                await f.Window.HandleDesktopMediaCommandAsync(DesktopMediaAction.Previous);
                Assert(f.Player.IsPlaying && f.Player.SourceLocalPath == expected && queue.CurrentOccurrenceToken == token
                    && ReferenceEquals(queue.Current, rows[targetIndex]) && !rows[1].IsDownloaded && f.ApiRequests == 0 && f.PlaybackRequests == 0,
                    "Previous lost the exact viable occurrence or changed only the queue");
            }
        });
        await Run("Quality offline 017: queue navigation preserves its source when local admission fails", async () =>
        {
            foreach (var previous in new[] { false, true })
            foreach (var busy in new[] { false, true })
            {
                using var f = new QualityOffline017Fixture($"admission-{previous}-{busy}", signedIn: false);
                var first = await f.Save(f.Row("admission-first"), 320); var second = await f.Save(f.Row("admission-second"), 320);
                await f.Library.RecoverPendingAsync();
                var firstPath = f.Library.ResolvePath(first.FileName); var secondPath = f.Library.ResolvePath(second.FileName);
                var rows = f.Library.Tracks(); var a = rows.Single(row => row.LocalPath == firstPath); var b = rows.Single(row => row.LocalPath == secondPath);
                var queue = f.PlaybackQueue;
                f.Player.SetCrossfade(0);
                queue.SetItems(previous ? [a, b] : [a]);
                await f.Window.PlayTrackAsync(a);
                if (previous)
                {
                    await f.Window.PlayTrackAsync(b);
                    await f.Player.WaitForClosedLocalSourcesAsync(new HashSet<string> { firstPath });
                }
                else queue.SetItems([a, b]);
                var active = queue.CurrentOccurrenceToken; var wanted = queue.OccurrenceTokenAt(previous ? 0 : 1);
                var version = queue.Version; var history = queue.History.ToArray(); var source = f.Player.SourceLocalPath;
                var position = f.Player.Position; var targetPath = previous ? firstPath : secondPath;
                var original = File.ReadAllBytes(targetPath);
                var command = previous ? DesktopMediaAction.Previous : DesktopMediaAction.Next;
                using (var hold = busy ? File.Open(targetPath, FileMode.Open, FileAccess.Read, FileShare.None) : null)
                {
                    if (!busy)
                    {
                        var damaged = original.ToArray(); damaged[^1] ^= 1; File.WriteAllBytes(targetPath, damaged);
                    }
                    await f.Window.HandleDesktopMediaCommandAsync(command);
                    Assert(f.Player.IsPlaying && f.Player.SourceLocalPath == source && f.Player.Position >= position - TimeSpan.FromMilliseconds(100)
                        && queue.CurrentOccurrenceToken == active && queue.Version == version && queue.History.SequenceEqual(history),
                        "failed local admission changed the audible source, occurrence or navigation history");
                }
                if (!busy)
                {
                    File.WriteAllBytes(targetPath, original);
                    SaInvoke(f.Window, "ApplyOfflineStates", (object)new[] { a, b });
                }
                await f.Window.HandleDesktopMediaCommandAsync(command);
                Assert(f.Player.IsPlaying && f.Player.SourceLocalPath == targetPath && queue.CurrentOccurrenceToken == wanted
                    && f.ApiRequests == 0 && f.PlaybackRequests == 0, "restored local admission did not play its exact occurrence");
            }
        });

        await Run("Quality offline 017: an unavailable catalog occurrence retries its local copy after sharing clears", async () =>
        {
            using var f = new QualityOffline017Fixture("unavailable-catalog-sharing", signedIn: false);
            var available = f.Row("unavailable-catalog-track");
            var saved = await f.Save(available, 320);
            var path = f.Library.ResolvePath(saved.FileName);
            var blocked = new TrackRow { Id = available.Id, RequestId = available.RequestId, Title = available.Title,
                DurationMs = available.DurationMs, Available = false };
            SaInvoke(f.Window, "ApplyOfflineStates", (object)new[] { blocked });
            Assert(blocked.IsDownloaded && !blocked.IsOfflineLibraryRow && blocked.LocalPath is null,
                "fixture did not create an ordinary unavailable catalog row with a local mark");
            var first = f.Local("first");
            f.Player.SetCrossfade(0);
            f.PlaybackQueue.SetItems([first], first);
            await f.Window.PlayTrackAsync(first);
            using (File.Open(path, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                f.PlaybackQueue.SetItems([first, blocked]);
                SaInvoke(f.Window, "CancelNextPreparation", true);
                await f.Window.HandleDesktopMediaCommandAsync(DesktopMediaAction.Next);
                Assert(f.Player.IsPlaying && f.Player.SourceLocalPath == first.LocalPath && ReferenceEquals(f.PlaybackQueue.Current, first)
                    && !blocked.IsDownloaded && blocked.CanPlayInQueue, "the failed open either moved the queue or removed the retry candidate");
            }
            var wanted = f.PlaybackQueue.OccurrenceTokenAt(1);
            await f.Window.HandleDesktopMediaCommandAsync(DesktopMediaAction.Next);
            Assert(f.PlaybackQueue.CurrentOccurrenceToken == wanted && f.Player.IsPlaying && f.Player.SourceLocalPath == path
                && blocked.IsDownloaded && f.ApiRequests == 0 && f.PlaybackRequests == 0,
                "the released catalog copy did not play in its original occurrence without streaming");
        });

        await Run("Quality offline 017: cold Next Previous and native end stream the same catalog occurrence after SHA rejection", async () =>
        {
            foreach (var action in new[] { "next", "previous", "ended" })
            {
                using var f = new QualityOffline017Fixture("cold-sha-stream-" + action);
                var target = f.Row("catalog");
                var saved = await f.Save(target, 320);
                var path = f.Library.ResolvePath(saved.FileName);
                var first = f.Local("first");
                var queue = f.PlaybackQueue;
                queue.SetItems([first]);
                await f.Window.PlayTrackAsync(first);
                if (action == "previous")
                {
                    queue.SetItems([target, first]);
                    queue.Next();
                }
                else queue.SetItems([first, target]);
                var occurrence = queue.OccurrenceTokenAt(action == "previous" ? 0 : 1);
                var damaged = ReplaceWithSameLengthSilentWave(path, saved);
                SaInvoke(f.Window, "CancelNextPreparation", true);
                if (action == "ended")
                {
                    PlaybackOptionsInvoke(f.Player, "OnMediaEnded", PreloadGet<object>(f.Player, "_current"));
                    await Until(() => !PreloadGet<bool>(f.Window, "_advancingPlayback"));
                }
                else await Call(f.Window, "AdvanceAsync", action == "previous" ? -1 : 1, false);
                Assert(queue.CurrentOccurrenceToken == occurrence && ReferenceEquals(queue.Current, target)
                    && f.Player.IsPlaying && f.Player.SourceLocalPath != path && f.ModernRequests == 1 && f.PlaybackRequests == 1
                    && File.ReadAllBytes(path).SequenceEqual(damaged), "cold admission lost the allowed online occurrence or modified its local file");
            }
        });

        await Run("Quality offline 017: natural queue skips rejected local occurrences only within its current pass", async () =>
        {
            foreach (var scenario in new[] { "busy", "damaged", "exhausted" })
            {
                using var f = new QualityOffline017Fixture("natural-sha-" + scenario, signedIn: false);
                var saved = await f.Save(f.Row("rejected"), 320);
                var path = f.Library.ResolvePath(saved.FileName);
                var rejected = f.Library.Tracks().Single();
                var first = f.Local("first");
                var next = f.Local("next");
                var queue = f.PlaybackQueue;
                queue.SetItems([first]);
                await f.Window.PlayTrackAsync(first);
                queue.SetItems(scenario == "exhausted" ? [first, rejected] : [first, rejected, next]);
                var source = queue.CurrentOccurrenceToken;
                var wanted = scenario == "exhausted" ? source : queue.OccurrenceTokenAt(2);
                using var busy = scenario == "busy" ? File.Open(path, FileMode.Open, FileAccess.Read, FileShare.None) : null;
                if (busy is null) ReplaceWithSameLengthSilentWave(path, saved);
                SaInvoke(f.Window, "CancelNextPreparation", true);
                PlaybackOptionsInvoke(f.Player, "OnMediaEnded", PreloadGet<object>(f.Player, "_current"));
                await Until(() => !PreloadGet<bool>(f.Window, "_advancingPlayback"));
                Assert(queue.CurrentOccurrenceToken == wanted && queue.History.SequenceEqual(scenario == "exhausted" ? [] : [first])
                    && (scenario == "exhausted" ? !f.Player.HasSource : f.Player.SourceLocalPath == next.LocalPath && f.Player.IsPlaying)
                    && f.ApiRequests == 0, "natural admission consumed the rejected occurrence or lost the next playable source");
                if (busy is not null)
                {
                    busy.Dispose();
                    queue.SelectAt(0);
                    await f.Window.PlayTrackAsync(first);
                    await Call(f.Window, "AdvanceAsync", 1, false);
                    Assert(ReferenceEquals(queue.Current, rejected) && f.Player.SourceLocalPath == path,
                        "a sharing conflict persisted past the ended pass");
                }
            }
        });

        await Run("Quality offline 017: natural SHA admission honors compatible edits and sleep before success or failure", async () =>
        {
            foreach (var action in new[] { "append", "priority", "after-track", "minute", "minute-failed", "control" })
            {
                using var f = new QualityOffline017Fixture("admission-owner-" + action, signedIn: false);
                var saved = await f.Save(f.Row("checked"), 320);
                var target = f.Library.Tracks().Single();
                if (action == "minute-failed") ReplaceWithSameLengthSilentWave(f.Library.ResolvePath(saved.FileName), saved);
                var first = f.Local("first");
                var extra = f.Local("extra");
                var queue = f.PlaybackQueue;
                queue.SetItems([first]);
                await f.Window.PlayTrackAsync(first);
                var elapsed = TimeSpan.Zero;
                f.Window.SleepElapsedForChecks = () => elapsed;
                if (action.StartsWith("minute", StringComparison.Ordinal)) f.Window.SetSleepTimer(SleepTimerMode.Minutes15);
                queue.SetItems([first, target]);
                var current = queue.CurrentOccurrenceToken;
                var targetOccurrence = queue.OccurrenceTokenAt(1);
                var verifying = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                f.Window.QueuePlaybackBeforeVerificationForChecks = token =>
                {
                    verifying.TrySetResult();
                    return release.Task.WaitAsync(token);
                };
                SaInvoke(f.Window, "CancelNextPreparation", true);
                PlaybackOptionsInvoke(f.Player, "OnMediaEnded", PreloadGet<object>(f.Player, "_current"));
                await verifying.Task.WaitAsync(TimeSpan.FromSeconds(3));
                Assert(PreloadGet<bool>(f.Window, "_advancingPlayback") && queue.CurrentOccurrenceToken == current,
                    "fixture did not intercept the natural local admission");
                if (action == "append" || action == "priority") queue.Enqueue(extra, playNext: action == "priority");
                else if (action == "after-track") f.Window.SetSleepTimer(SleepTimerMode.AfterCurrentTrack);
                else if (action.StartsWith("minute", StringComparison.Ordinal)) elapsed = TimeSpan.FromMinutes(15);
                release.TrySetResult();
                await Until(() => !PreloadGet<bool>(f.Window, "_advancingPlayback"));
                var sleeping = action == "after-track" || action.StartsWith("minute", StringComparison.Ordinal);
                if (sleeping)
                {
                    Assert(queue.CurrentOccurrenceToken == current && !f.Player.IsPlaying && f.Player.SourceLocalPath == first.LocalPath
                        && !PreloadGet<SleepTimerState>(f.Window, "_sleepTimer").Active
                        && Control<TextBlock>(f.Window, "StatusText").Text == "Таймер сна: музыка на паузе.",
                        "late sleep was cleared or overwritten by the completed admission");
                }
                else
                {
                    Assert(queue.CurrentOccurrenceToken == (action == "priority" ? queue.OccurrenceTokenAt(1) : targetOccurrence)
                        && queue.History.SequenceEqual([first]) && f.Player.IsPlaying
                        && f.Player.SourceLocalPath == (action == "priority" ? extra.LocalPath : target.LocalPath),
                        "a compatible append or changed exact next target stopped or duplicated the continuation");
                }
                Assert(f.ApiRequests == 0, "local admission unexpectedly used the fake API");
            }
        });

        await Run("Quality offline 017: a held manual admission preserves the source's actual natural end", async () =>
        {
            foreach (var scenario in new[] { "busy", "damaged", "changed-target", "accepted", "pause", "after-track", "takeover" })
            {
                using var f = new QualityOffline017Fixture("deferred-end-" + scenario, signedIn: false);
                var saved = await f.Save(f.Row("target"), 320);
                var target = f.Library.Tracks().Single();
                var first = f.Local("first");
                var next = f.Local("next");
                var q = f.PlaybackQueue;
                var w = f.Window;
                q.SetItems([first]);
                await w.PlayTrackAsync(first);
                q.SetItems([first, target, next]);
                var firstToken = q.CurrentOccurrenceToken;
                var targetToken = q.OccurrenceTokenAt(1);
                var nextToken = q.OccurrenceTokenAt(2);
                var path = f.Library.ResolvePath(saved.FileName);
                var verification = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var takeover = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var attempts = 0;
                w.QueuePlaybackBeforeVerificationForChecks = token =>
                {
                    if (++attempts == 1) verification.TrySetResult();
                    else takeover.TrySetResult();
                    return release.Task.WaitAsync(token);
                };
                if (scenario is "damaged" or "takeover") ReplaceWithSameLengthSilentWave(path, saved);
                PreloadInvoke(w, "CancelNextPreparation", true);
                var outgoing = PreloadGet<object>(f.Player, "_current");
                if (scenario == "takeover")
                {
                    PlaybackOptionsInvoke(f.Player, "OnMediaEnded", outgoing);
                    await verification.Task.WaitAsync(TimeSpan.FromSeconds(5));
                }
                var manual = Call(w, "AdvanceAsync", 1, false);
                await (scenario == "takeover" ? takeover.Task : verification.Task).WaitAsync(TimeSpan.FromSeconds(5));
                if (scenario != "takeover") PlaybackOptionsInvoke(f.Player, "OnMediaEnded", outgoing);
                Assert(q.CurrentOccurrenceToken == firstToken && !f.Player.IsPlaying, "fixture did not hold the ended source at admission");
                using var busy = scenario == "busy" ? File.Open(path, FileMode.Open, FileAccess.Read, FileShare.None) : null;
                if (scenario == "changed-target")
                {
                    q.Move(2, 1);
                    nextToken = q.OccurrenceTokenAt(1);
                }
                if (scenario == "pause") await w.HandleDesktopMediaCommandAsync(DesktopMediaAction.Pause);
                if (scenario == "after-track") w.SetSleepTimer(SleepTimerMode.AfterCurrentTrack);
                release.TrySetResult();
                await manual.WaitAsync(TimeSpan.FromSeconds(5));
                await Until(() => !PreloadGet<bool>(w, "_advancingPlayback"));
                if (scenario is "pause" or "after-track")
                {
                    Assert(q.CurrentOccurrenceToken == firstToken && q.History.Count == 0 && !f.Player.IsPlaying
                        && ReferenceEquals(PreloadGet<object>(f.Player, "_current"), outgoing),
                        "a later stop intent let the deferred native end start a new source");
                }
                else
                {
                    Assert(q.CurrentOccurrenceToken == (scenario == "accepted" ? targetToken : nextToken)
                        && q.History.SequenceEqual([first]) && f.Player.IsPlaying
                        && f.Player.SourceLocalPath == (scenario == "accepted" ? path : next.LocalPath) && f.ApiRequests == 0,
                        "manual completion lost the native end or consumed an extra queue occurrence");
                }
            }
        });

        await Run("Quality offline 017: transient rejection preserves exact forward history in shuffle", async () =>
        {
            using var f = new QualityOffline017Fixture("shuffle-deferred-future", signedIn: false);
            var saved = await f.Save(f.Row("forward"), 320);
            var path = f.Library.ResolvePath(saved.FileName);
            var forward = f.Library.Tracks().Single();
            var first = f.Local("first");
            var next = f.Local("next");
            var q = f.PlaybackQueue;
            q.SetItems([first]);
            await f.Window.PlayTrackAsync(first);
            q.SetItems([first, forward, next]);
            q.Shuffle = true;
            q.SelectAt(1); q.SelectAt(2); q.Previous(); q.Previous();
            var forwardToken = q.OccurrenceTokenAt(1);
            var nextToken = q.OccurrenceTokenAt(2);
            Assert(q.PeekNextOccurrenceToken() == forwardToken, "fixture did not put the visited occurrence in forward history");
            PreloadInvoke(f.Window, "CancelNextPreparation", true);
            using (File.Open(path, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                PlaybackOptionsInvoke(f.Player, "OnMediaEnded", PreloadGet<object>(f.Player, "_current"));
                await Until(() => !PreloadGet<bool>(f.Window, "_advancingPlayback"));
                Assert(q.CurrentOccurrenceToken == nextToken && f.Player.SourceLocalPath == next.LocalPath && q.History.SequenceEqual([first]),
                    "the blocked forward occurrence was treated as a navigation step");
            }
            await Call(f.Window, "AdvanceAsync", 1, false);
            Assert(q.CurrentOccurrenceToken == forwardToken && q.History.SequenceEqual([first, next])
                && f.Player.SourceLocalPath == path && f.Player.IsPlaying && f.ApiRequests == 0,
                "the same shuffle pass lost the temporarily excluded forward occurrence");
        });

        await Run("Quality offline 017: removing the current occurrence skips an unusable indexed successor", async () =>
        {
            foreach (var scenario in new[] { "playing", "paused", "alternate" })
            foreach (var damaged in new[] { false, true })
            {
                using var f = new QualityOffline017Fixture($"remove-stale-{scenario}-{damaged}", signedIn: false);
                var a = await f.Save(f.Row("removal-a"), 320); var b = await f.Save(f.Row("removal-b"), 320);
                var c = await f.Save(f.Row("removal-c"), 320);
                var alternate = scenario == "alternate" ? await f.Save(f.Row("removal-b"), 192) : null;
                var primaryB = f.Library.ResolvePath(b.FileName);
                var rows = new[] { a, b, c }.Select(item => f.Library.Tracks().Single(row => row.LocalPath == f.Library.ResolvePath(item.FileName))).ToArray();
                var q = f.PlaybackQueue; var w = f.Window;
                q.SetItems([rows[0]]);
                await w.PlayTrackAsync(rows[0]);
                if (scenario == "paused") await Call(w, "TogglePlaybackAsync");
                q.SetItems(rows);
                if (damaged) ReplaceWithSameLengthSilentWave(primaryB, b);
                else File.Delete(primaryB);
                var prior = q.OccurrenceTokenAt(0); var target = q.OccurrenceTokenAt(alternate is null ? 2 : 1);
                SaInvoke(w, "ShowQueueCore", false);
                // The view's mark refresh may already observe the missing file.
                // Expose a stale admitted row just as when deletion precedes a refresh.
                rows[1].DownloadedPath = primaryB;
                Control<ListView>(w, "TracksList").SelectedItem = rows[0];
                await Call(w, "RemoveSelectedQueueAsync");
                var expected = f.Library.ResolvePath((alternate ?? c).FileName);
                Assert(q.CurrentOccurrenceToken == target && f.Player.IsPlaying == (scenario != "paused")
                    && (scenario == "paused" ? !f.Player.HasSource : f.Player.SourceLocalPath == expected),
                    "removal chose a stale copy or violated the prior play state");
                Assert(q.UndoRemoval() && q.CurrentOccurrenceToken == target && q.OccurrenceTokenAt(0) == prior,
                    "Undo rewound the valid replacement after removal");
            }
        });

        await Run("Quality offline 017: playlist projection excludes rejected copies and binds its actual saved edition", async () =>
        {
            using var f = new QualityOffline017Fixture("playlist-bound-edition", signedIn: false);
            var savedA = f.Row("bound-edition");
            var editionB = new TrackRow { Id = savedA.Id, RequestId = savedA.Id + ":2", Title = savedA.Title, DurationMs = 40000, Available = true };
            var primary = await f.Save(savedA, 320); var primaryPath = f.Library.ResolvePath(primary.FileName);
            var direct = f.Library.Tracks().Single();
            var list = new PlaylistDetails("fixture", "1", "Saved", "", 1, [editionB]);
            await f.Library.RememberPlaylistAsync(list, AudioQuality.Excellent, default);
            var shared = f.Library.GetPlaylistTracks(list.Owner, list.Kind).Single();
            Assert(shared.RequestId == editionB.RequestId && shared.LocalSourceRequestId == savedA.RequestId
                && f.Library.ResolvePlaybackSource(shared, requireExactOfflineVariant: true)?.Path == primaryPath,
                "playlist did not retain the real edition of a shared saved copy");
            var original = File.ReadAllBytes(primaryPath);
            var damaged = original.ToArray(); damaged[^1] ^= 1; File.WriteAllBytes(primaryPath, damaged);
            var source = f.Library.ResolvePlaybackSource(direct, requireExactOfflineVariant: true)!;
            try { using var unexpected = await source.HoldAsync(default); throw new InvalidOperationException("damaged fixture passed SHA"); }
            catch (LocalAudioIntegrityException error) { Assert(f.Library.RememberRejectedPlaybackSource(source, error), "fixture could not record the rejected revision"); }
            Assert(f.Library.GetPlaylistStatus(list.Owner, list.Kind).Downloaded == 0
                && f.Library.GetPlaylistTracks(list.Owner, list.Kind).Single().LocalPath is null, "playlist still counted its rejected file");
            var alternate = await f.Save(savedA, 192);
            var alternatePath = f.Library.ResolvePath(alternate.FileName);
            var ready = f.Library.GetPlaylistTracks(list.Owner, list.Kind).Single();
            Assert(f.Library.GetPlaylistStatus(list.Owner, list.Kind).Downloaded == 1
                && ready.LocalPath == alternatePath && f.Library.ResolvePlaybackSource(ready, requireExactOfflineVariant: true)?.Path == alternatePath
                && f.Library.ResolvePlaybackSource(direct, requireExactOfflineVariant: true) is null,
                "reopening the playlist failed to bind the valid lower quality exactly");

            var first = f.Local("shared-edition-origin");
            f.PlaybackQueue.SetItems([first]);
            await f.Window.PlayTrackAsync(first);
            f.PlaybackQueue.SetItems([first, shared]);
            var occurrence = f.PlaybackQueue.OccurrenceTokenAt(1);
            SaInvoke(f.Window, "ApplyOfflineStates", (object)new[] { shared });
            Assert(f.Library.ResolvePlaybackSource(shared, requireExactOfflineVariant: true) is null
                && f.Library.ResolvePlaybackSource(shared)?.Path == alternatePath && shared.CanPlayInQueue && !shared.CanPlay,
                "existing playlist occurrence confused its exact file with the replacement of its bound edition");
            await Call(f.Window, "AdvanceAsync", 1, false);
            Assert(f.Player.SourceLocalPath == alternatePath && f.PlaybackQueue.CurrentOccurrenceToken == occurrence,
                "existing playlist occurrence lost the saved edition during transition");
            f.Player.Stop();
            await f.Window.PlayTrackAsync(shared);
            Assert(f.Player.SourceLocalPath == alternatePath && f.PlaybackQueue.CurrentOccurrenceToken == occurrence && f.ApiRequests == 0,
                "existing playlist occurrence resumed a different version or token");
            f.Player.Stop();

            File.WriteAllBytes(primaryPath, original);
            Assert((await f.Library.RemoveTrackAsync(savedA.Id, default)).Removed == 2, "fixture could not free the original path");
            var reused = await f.Save(editionB, 320);
            var newlySaved = f.Library.Tracks().Single();
            Assert(f.Library.ResolvePath(reused.FileName) == primaryPath
                && f.Library.ResolvePlaybackSource(direct, requireExactOfflineVariant: true) is null
                && f.Library.ResolvePlaybackSource(shared, requireExactOfflineVariant: true) is null
                && f.Library.ResolvePlaybackSource(newlySaved, requireExactOfflineVariant: true)?.Path == primaryPath,
                "reused path accepted a different saved edition through a stale row");
        });

        await Run("Quality offline 017: confirmed SHA rejection updates selected visible saved-playlist card", async () =>
        {
            using var f = new QualityOffline017Fixture("visible-playlist-sha", signedIn: false);
            var catalog = f.Row("visible-playlist");
            var saved = await f.Save(catalog, 320);
            var list = new PlaylistDetails("fixture", "2", "Saved", "", 1, [catalog]);
            await f.Library.RememberPlaylistAsync(list, AudioQuality.Excellent, default);
            var first = f.Local("first");
            f.PlaybackQueue.SetItems([first]);
            await f.Window.PlayTrackAsync(first);
            await Call(f.Window, "ShowOfflinePlaylistsAsync");
            var cards = Control<ListBox>(f.Window, "CatalogList");
            var card = cards.Items.OfType<CatalogItem>().Single();
            cards.SelectedItem = card;
            Assert(card.Subtitle.StartsWith("✓", StringComparison.Ordinal), "fixture playlist was incomplete before the SHA failure");
            var row = f.Library.GetPlaylistTracks(list.Owner, list.Kind).Single();
            f.PlaybackQueue.SetItems([first, row]);
            ReplaceWithSameLengthSilentWave(f.Library.ResolvePath(saved.FileName), saved);
            await Call(f.Window, "AdvanceAsync", 1, false);
            await Until(() => cards.SelectedItem is CatalogItem selected && selected.Subtitle == "Скачано 0 из 1");
            Assert(cards.SelectedItem is CatalogItem updated && updated.Owner == card.Owner && updated.PlaylistKind == card.PlaylistKind
                && SaField<string>(f.Window, "_section") == "offline-playlists" && ReferenceEquals(f.PlaybackQueue.Current, first),
                "card refresh changed navigation, selected a different card, or consumed the failed row");
        });

        await Run("Quality offline 017: transport and unselected queue Listen use an existing alternate without opening an explicit missing row", async () =>
        {
            foreach (var single in new[] { true, false })
            {
                using var f = new QualityOffline017Fixture("alternate-queue-controls-" + single, signedIn: false);
                var catalog = f.Row("a-existing"); var primary = await f.Save(catalog, 192);
                var alternate = await f.Save(catalog, 320);
                var other = await f.Save(f.Row("z-other"), 192);
                var primaryPath = f.Library.ResolvePath(primary.FileName); var alternatePath = f.Library.ResolvePath(alternate.FileName);
                var choices = f.Library.Tracks();
                var row = choices.Single(item => item.LocalPath == primaryPath);
                var ordinary = choices.Single(item => item.LocalPath == f.Library.ResolvePath(other.FileName));
                var queue = f.PlaybackQueue; var w = f.Window;
                queue.SetItems(single ? [row] : [ordinary, row]);
                var desiredToken = queue.OccurrenceTokenAt(single ? 0 : 1);
                File.Delete(primaryPath);
                SaInvoke(w, "ApplyOfflineStates", (object)new[] { row });
                Assert(!row.CanPlay && row.CanPlayInQueue && !row.IsDownloaded, "fixture did not separate exact badge from queue access");
                if (single)
                {
                    SaInvoke(w, "UpdateControls");
                    var play = Control<Button>(w, "PlayPauseButton");
                    Assert(play.IsEnabled, "transport excluded the only resumable occurrence");
                    SaClick(play);
                    await Until(() => f.Player.IsPlaying);
                    Assert(f.Player.SourceLocalPath == alternatePath, "transport opened the wrong saved version");
                    f.Player.Stop();
                    await f.Player.WaitForClosedLocalSourcesAsync(new HashSet<string> { alternatePath });
                    File.Delete(alternatePath);
                    SaInvoke(w, "ApplyOfflineStates", (object)new[] { row });
                    SaInvoke(w, "UpdateControls");
                    Assert(!play.IsEnabled && !row.CanPlayInQueue, "transport offered a row after both versions disappeared");
                }
                else
                {
                    SaInvoke(w, "ShowQueueCore", false);
                    SortChoose(w, ListSortField.Title);
                    var list = Control<ListView>(w, "TracksList"); var listen = Control<Button>(w, "PlaySelectionButton");
                    list.SelectedItem = row;
                    var current = queue.CurrentOccurrenceToken;
                    Assert(!listen.IsEnabled, "explicit missing version offered Listen despite requiring its exact file");
                    await Call(w, "PlaySelectedAsync");
                    Assert(!f.Player.HasSource && queue.CurrentOccurrenceToken == current, "explicit action substituted another version");
                    list.SelectedIndex = -1;
                    Assert(listen.IsEnabled && ReferenceEquals(list.Items[0], row), "visible queue did not offer the sorted alternate");
                    SaClick(listen);
                    await Until(() => f.Player.IsPlaying);
                    Assert(f.Player.SourceLocalPath == alternatePath && !row.IsDownloaded, "implicit queue Listen misreported or lost the saved alternate");
                }
                Assert(queue.CurrentOccurrenceToken == desiredToken && f.ApiRequests == 0 && f.PlaybackRequests == 0,
                    "queue action selected a different occurrence or contacted playback API");
            }
        });

        await Run("Quality offline 017: promotion discards a prepared version removed from the index", async () =>
        {
            using var f = new QualityOffline017Fixture("stale-promotion", signedIn: false);
            var track = f.Row("track");
            var selected = await f.Save(track, 192);
            var alternate = await f.Save(track, 320);
            var selectedPath = f.Library.ResolvePath(selected.FileName);
            var row = f.Library.Tracks().Single(row => row.LocalPath == selectedPath);
            var first = f.Local("current");
            f.PlaybackQueue.SetItems([first, row], first);
            await f.Window.PlayTrackAsync(first); await PreloadAwaitPrepared(f.Window);
            Assert(f.Player.PreparedLocalPath == selectedPath, "fixture did not prepare the selected version");

            var index = Path.Combine(f.Library.DirectoryPath, "library.index.json");
            var rest = f.Library.Read().Where(item => item.FileName != selected.FileName).ToArray();
            File.WriteAllText(index, JsonSerializer.Serialize(rest));
            var occurrence = f.PlaybackQueue.CurrentOccurrenceToken;
            var version = f.PlaybackQueue.Version;
            Assert(PreloadInvoke(f.Window, "ReadyNextPlayback", true) is null
                && f.PlaybackQueue.CurrentOccurrenceToken == occurrence && f.PlaybackQueue.Version == version
                && f.Player.IsPlaying && f.Player.SourceLocalPath == first.LocalPath,
                "stale preload became eligible for early fade or interrupted the outgoing track");
            await Call(f.Window, "AdvanceAsync", 1, false);
            Assert(f.Player.IsPlaying && f.Player.SourceLocalPath == f.Library.ResolvePath(alternate.FileName)
                && File.Exists(selectedPath) && f.ApiRequests == 0 && !row.IsDownloaded && row.DownloadedQuality is null,
                "promotion used the removed index entry, changed its file or labelled the exact row with another variant");
        });

        await Run("Quality offline 017: failed early promotion preserves the outgoing exact occurrence", async () =>
        {
            using var f = new QualityOffline017Fixture("early-promotion-failed", signedIn: false);
            var first = f.Local("current");
            var next = f.Local("next");
            f.PlaybackQueue.SetItems([first, next], first);
            await f.Window.PlayTrackAsync(first);
            await PreloadAwaitPrepared(f.Window);
            var prepared = PreloadGet<object>(f.Window, "_preparedPlayback");
            prepared.GetType().GetProperty("Source")!.SetValue(prepared, new Uri(first.LocalPath!));
            var occurrence = f.PlaybackQueue.CurrentOccurrenceToken;
            var version = f.PlaybackQueue.Version;
            await Call(f.Window, "AdvanceCoreAsync", 1, true, prepared);
            Assert(f.PlaybackQueue.CurrentOccurrenceToken == occurrence && f.PlaybackQueue.Version == version
                && f.PlaybackQueue.History.Count == 0 && ReferenceEquals(f.PlaybackQueue.Current, first)
                && f.Player.IsPlaying && f.Player.SourceLocalPath == first.LocalPath,
                "refused early fade navigated or stopped the outgoing track");
        });

        await Run("Quality offline 017: failed optional online open falls back once during Play and Preload", async () =>
        {
            foreach (var prepare in new[] { false, true })
            {
                using var f = new QualityOffline017Fixture("online-open-fallback-" + prepare) { RejectPlayback = true };
                var next = f.Row("next");
                var saved = await f.Save(next, 192);
                var localPath = f.Library.ResolvePath(saved.FileName);
                if (prepare)
                {
                    var first = f.Local("current");
                    f.PlaybackQueue.SetItems([first, next], first);
                    await f.Window.PlayTrackAsync(first);
                    await PreloadAwaitPrepared(f.Window);
                    Assert(f.Player.PreparedLocalPath == localPath && f.Player.SourceLocalPath == first.LocalPath,
                        "failed optional online preload did not prepare the local file or interrupted current");
                    await Call(f.Window, "AdvanceAsync", 1, false);
                }
                else await f.Window.PlayTrackAsync(next);
                Assert(f.Player.IsPlaying && f.Player.SourceLocalPath == localPath
                    && f.ModernRequests == 1 && f.PlaybackRequests == 1
                    && Control<TextBlock>(f.Window, "QualityText").Text == "Офлайн · MP3 · 192 кбит/с",
                    "optional online open failed to fall back once with the actual local quality");
            }
        });

        await Run("Quality offline 017: optional online open fallback rechecks the local variant and ownership", async () =>
        {
            foreach (var supersede in new[] { false, true })
            {
                using var f = new QualityOffline017Fixture("online-open-owner-" + supersede) { RejectPlayback = true, HoldPlayback = true };
                var next = f.Row("next");
                var alternate = await f.Save(next, 128);
                var selected = await f.Save(next, 192);
                var play = f.Window.PlayTrackAsync(next);
                await f.PlaybackEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                File.Delete(f.Library.ResolvePath(selected.FileName));
                var expected = f.Library.ResolvePath(alternate.FileName);
                if (supersede)
                {
                    var newer = f.Local("newer");
                    await f.Window.PlayTrackAsync(newer);
                    expected = newer.LocalPath!;
                }
                f.PlaybackRelease.TrySetResult();
                await play.WaitAsync(TimeSpan.FromSeconds(5));
                Assert(f.Player.IsPlaying && f.Player.SourceLocalPath == expected && f.PlaybackRequests == 1,
                    "fallback used a removed file or replaced the newer occurrence");
            }
        });

        await Run("Quality offline 017: removal cancels Play waiting on a local quality probe", async () =>
        {
            using var f = new QualityOffline017Fixture("play-removed-cache") { BlockMetadata = true };
            var row = f.Row("track");
            var saved = await f.Save(row, 192);
            var play = f.Window.PlayTrackAsync(row);
            await f.MetadataEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var playToken = PreloadGet<CancellationTokenSource>(f.Window, "_playCts").Token;
            await Call(f.Window, "DeleteOfflineTrackAsync", row).WaitAsync(TimeSpan.FromSeconds(5));
            f.MetadataRelease.TrySetResult();
            await play.WaitAsync(TimeSpan.FromSeconds(5));
            Assert(playToken.IsCancellationRequested && f.Library.Read().Count == 0 && !File.Exists(f.Library.ResolvePath(saved.FileName))
                && !f.Player.HasSource && f.PlaybackRequests == 0, "Play opened a deleted source or fell back to the network after deletion");
        });

        await Run("Quality offline 017: removal cancels Preload waiting on a local quality probe", async () =>
        {
            using var f = new QualityOffline017Fixture("preload-removed-cache") { BlockMetadata = true };
            var row = f.Row("next");
            var saved = await f.Save(row, 192);
            var first = f.Local("current");
            f.PlaybackQueue.SetItems([first, row], first);
            await f.Window.PlayTrackAsync(first);
            await f.MetadataEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var preload = PreloadGet<Task>(f.Window, "_preloadTask");
            var preloadToken = PreloadGet<CancellationTokenSource>(f.Window, "_preloadCts").Token;

            await Call(f.Window, "DeleteOfflineTrackAsync", row).WaitAsync(TimeSpan.FromSeconds(5));
            Assert(preloadToken.IsCancellationRequested, "removal did not cancel the old quality probe");
            // The original service row remains eligible for a new, independent
            // online preload. Isolate the cancelled request this fixture owns.
            PreloadInvoke(f.Window, "CancelNextPreparation", true);
            f.MetadataRelease.TrySetResult();
            await preload.WaitAsync(TimeSpan.FromSeconds(5));
            Assert(!File.Exists(f.Library.ResolvePath(saved.FileName)) && f.Player.SourceLocalPath == first.LocalPath
                && f.Player.IsPlaying && !f.Player.HasPrepared && f.PlaybackRequests == 0,
                "preload opened a deleted source or interrupted unrelated playback");
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
        await Run("Quality offline 017: explicit and automatic downloads replace same-size damaged cache without touching its original bytes", async () =>
        {
            foreach (var mode in new[] { "sufficient", "downgrade", "automatic" })
            {
                using var f = new QualityOffline017Fixture("download-damaged-cache-" + mode) { ServerBitrate = 192 };
                var row = f.Row("track");
                var old = await f.Save(row, 192);
                var original = f.Library.ResolvePath(old.FileName);
                var damaged = File.ReadAllBytes(original); damaged[^1] ^= 1; File.WriteAllBytes(original, damaged);
                Control<ComboBox>(f.Window, "QualityCombo").SelectedIndex = mode == "downgrade" ? 0 : 1;

                if (mode == "automatic") await f.DownloadAutomatic(row);
                else await f.Download(row);
                var repaired = f.Library.Find(row);
                Assert(f.LibraryRequests == 2 && f.Queue.Entries.Single().State == DownloadEntryState.Completed
                    && repaired is not null && repaired.FileName != old.FileName && f.Hash(repaired).Equals(old.Sha256, StringComparison.OrdinalIgnoreCase)
                    && File.ReadAllBytes(original).SequenceEqual(damaged), "the worker trusted damaged bytes, failed to save the repair, or altered the original");

                Control<ComboBox>(f.Window, "QualityCombo").SelectedIndex = 1;
                var resolves = f.ModernRequests;
                await f.Download(row);
                Assert(f.ModernRequests == resolves && f.LibraryRequests == 2, "the verified repair was downloaded twice");
            }
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

    private static byte[] ReplaceWithSameLengthSilentWave(string path, OfflineItem item)
    {
        var replacement = File.ReadAllBytes(path);
        // Generated PCM WAV has a 44-byte header. A silent version is valid audio.
        Array.Clear(replacement, 44, replacement.Length - 44);
        Assert(replacement.LongLength == item.Bytes
            && !StringComparer.OrdinalIgnoreCase.Equals(Convert.ToHexString(SHA256.HashData(replacement)), item.Sha256),
            "fixture did not create different PCM at the indexed length");
        File.WriteAllBytes(path, replacement);
        return replacement;
    }

    private static async Task QualityExplicitRetryOwnership(string command, bool expanded, bool ordinaryCatalog)
    {
        using var f = new QualityOffline017Fixture($"explicit-retry-{command}-{expanded}-{ordinaryCatalog}", signedIn: false);
        var metadata = f.Row("target");
        var item = await f.Save(metadata, 320);
        var path = f.Library.ResolvePath(item.FileName);
        var target = ordinaryCatalog ? new TrackRow { Id = metadata.Id, RequestId = metadata.RequestId, Title = metadata.Title,
            DurationMs = metadata.DurationMs, Available = false } : f.Library.Tracks().Single();
        SaInvoke(f.Window, "ApplyOfflineStates", (object)new[] { target });
        var playing = f.Local("playing");
        f.Player.SetCrossfade(0);
        f.PlaybackQueue.SetItems([playing], playing);
        await f.Window.PlayTrackAsync(playing);
        long? listenToken;
        using (File.Open(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            f.PlaybackQueue.SetItems([playing, target]);
            listenToken = f.PlaybackQueue.CurrentOccurrenceToken;
            SaInvoke(f.Window, "CancelNextPreparation", true);
            await f.Window.HandleDesktopMediaCommandAsync(DesktopMediaAction.Next);
        }
        Assert(f.Player.IsPlaying && f.PlaybackQueue.CurrentOccurrenceToken == listenToken && !target.IsDownloaded,
            "fixture did not preserve the current source and record the original sharing refusal");
        var targetToken = f.PlaybackQueue.OccurrenceTokenAt(1);
        var history = f.PlaybackQueue.History.ToArray();

        SaInvoke(f.Window, "ShowQueueCore", false);
        if (expanded)
        {
            EpOpen(f.Window);
            EpInvoke(f.Window, "SetExpandedQueueVisible", true, false);
        }
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Window.ExactOfflineRetryBeforeCommitForChecks = token => { entered.TrySetResult(); return release.Task.WaitAsync(token); };
        Task Activate() => expanded
            ? Eq0317PlayRow(f.Window, EpField<ListBox>(f.Window, "_expandedQueueList").Items[1])
            : Call(f.Window, "ActivateTrackRowAsync", Control<ListView>(f.Window, "TracksList").Items.IndexOf(target));
        var retry = Activate();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            switch (command)
            {
                case "pause": await Call(f.Window, "TogglePlaybackAsync"); break;
                case "desktop-pause": await f.Window.HandleDesktopMediaCommandAsync(DesktopMediaAction.Pause); break;
                case "sleep":
                    f.Window.SetSleepTimer(SleepTimerMode.Minutes15);
                    SaInvoke(f.Window, "ApplySleepTimerElapsed", TimeSpan.FromMinutes(16));
                    break;
                case "stop": await f.Window.HandleDesktopMediaCommandAsync(DesktopMediaAction.Stop); break;
            }
            var position = f.Player.Position;
            release.TrySetResult();
            await retry.WaitAsync(TimeSpan.FromSeconds(5));
            if (command != "continue")
            {
                Assert(!f.Player.IsPlaying && f.PlaybackQueue.CurrentOccurrenceToken == listenToken && f.PlaybackQueue.History.SequenceEqual(history)
                    && (command == "stop" ? !f.Player.HasSource : f.Player.SourceLocalPath == playing.LocalPath
                        && (f.Player.Position - position).Duration() < TimeSpan.FromMilliseconds(150)),
                    "the pending exact verification displaced a newer transport action");
                await Activate().WaitAsync(TimeSpan.FromSeconds(5));
            }
            Assert(f.Player.IsPlaying && f.Player.SourceLocalPath == path && target.IsDownloaded
                && f.PlaybackQueue.CurrentOccurrenceToken == targetToken && f.ApiRequests == 0 && f.PlaybackRequests == 0,
                "authorized exact retry did not select the verified file and original token");
        }
        finally { release.TrySetResult(); await retry.WaitAsync(TimeSpan.FromSeconds(5)); }
    }

    private sealed class QualityOffline017Fixture : IDisposable
    {
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, byte[]> Downloads = new();
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
        internal bool RejectPlayback { get; set; }
        internal bool HoldPlayback { get; set; }
        internal int ApiRequests { get; private set; }
        internal int ModernRequests { get; private set; }
        internal int PlaybackRequests { get; private set; }
        internal int LibraryRequests { get; private set; }
        internal TaskCompletionSource MetadataEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource MetadataRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource PlaybackEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource PlaybackRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal QualityOffline017Fixture(string name, bool signedIn = true)
        {
            DirectoryPath = Path.Combine(_directory, "quality-offline017-" + name + "-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(DirectoryPath);
            _media = File.ReadAllBytes(WriteWave(Path.GetFileName(DirectoryPath) + ".wav", 40));
            Library = new OfflineLibrary(Path.Combine(DirectoryPath, "Music"), new AudioBuffer(Path.Combine(DirectoryPath, "save-buffer"), new FakeHandler((request, _) =>
            {
                var rate = int.TryParse(Path.GetFileNameWithoutExtension(request.RequestUri!.AbsolutePath), out var specified)
                    ? specified : ServerBitrate;
                LibraryRequests++;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    { Content = new ByteArrayContent(Downloads.GetOrAdd(rate, value => SilentMp3Seconds(40, value))) });
            })));
            Player = new NativePlayer(new AudioBuffer(Path.Combine(DirectoryPath, "player-buffer"), new FakeHandler(async (request, _) =>
            {
                Assert(request.Headers.Authorization is null && request.RequestUri!.Host == "s1.vlx.yandex.net", "playback sent credentials or left fake CDN");
                PlaybackRequests++; PlaybackEntered.TrySetResult();
                if (HoldPlayback) await PlaybackRelease.Task;
                return RejectPlayback ? new HttpResponseMessage(HttpStatusCode.Forbidden)
                    : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(_media) };
            })));
            Window = new MainWindow(api: new MusicApi(new FakeHandler(Respond)), store: new SessionStore(StorePath), restore: false,
                diagnosticsDirectory: DirectoryPath, offline: Library, player: Player)
            { ShowActivated = false, ShowInTaskbar = false, Left = -16000, Top = 0, WindowStartupLocation = WindowStartupLocation.Manual };
            Window.Show(); Control<Slider>(Window, "VolumeSlider").Value = 0; Player.SetVolume(0);
            if (signedIn) typeof(MainWindow).GetField("_session", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(Window, FakeSession);
        }

        internal TrackRow Row(string id) => new() { Id = id, RequestId = id + ":1", Title = id, DurationMs = 40000, Available = true };
        internal TrackRow Local(string id, string artist = "")
        {
            var path = Path.Combine(DirectoryPath, id + ".wav"); File.WriteAllBytes(path, _media);
            return new() { Id = id, RequestId = id + ":1", Title = id, Artist = artist, DurationMs = 40000, Available = true, LocalPath = path, LocalQuality = "Fixture WAV" };
        }
        internal Task<OfflineItem> Save(TrackRow row, int bitrate) => Library.SaveAsync(row,
            new AudioDescriptor(new Uri("https://s1.vlx.yandex.net/" + bitrate + ".mp3"), "mp3", bitrate), default);
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
        public void Dispose() { MetadataRelease.TrySetResult(); PlaybackRelease.TrySetResult(); Window.Close(); }
    }
}
