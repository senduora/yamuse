using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using YMusic.Native;

internal static partial class Program
{
    private static async Task CheckWavePresentation()
    {
        var originalCache = typeof(Artwork).GetField("_cache", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null);
        var artworkRequests = 0;
        var png = MediaCoverPng();
        using var artwork = new ArtworkCache(System.IO.Path.Combine(_directory, "wave-presentation-artwork"), new FakeHandler((request, _) =>
        {
            Assert(ArtworkUri.Valid(request.RequestUri!) && request.Headers.Authorization is null && !request.Headers.Contains("Cookie"), "Wave artwork left the anonymous CDN contract");
            Interlocked.Increment(ref artworkRequests);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(png) });
        }));
        Artwork.Cache = artwork;
        try
        {
            await Run("Wave presentation: production collection and personal actions, hero, queue and inline lyrics", CheckWaveHeroPresentation);
            await Run("Wave presentation: hover time and highlight clamp without changing native playback", CheckWaveSeekHover);
            await Run("Wave presentation: compact transport states, accessibility, memory labels and pixel virtualization", CheckWaveCompactControls);
            File.WriteAllText(System.IO.Path.Combine(_directory, "wave-presentation-scope.json"), JsonSerializer.Serialize(new
            {
                externalNetworkRequests = 0, nativeMediaNetworkRequests = 0, artworkFixtureRequests = artworkRequests,
                realSessionLoaded = false, realFeedbackSent = false, waveformMuted = true,
                checkedAssemblySha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(MainWindow).Assembly.Location))),
                hoverMethod = "Production UpdateSeekHover invoked with local coordinates; no global pointer input",
                settingsConcurrency = "Not repeated here; covered by the dedicated settings checks"
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        finally { typeof(Artwork).GetField("_cache", BindingFlags.NonPublic | BindingFlags.Static)!.SetValue(null, originalCache); }
    }

    private static async Task CheckWaveHeroPresentation()
    {
        using var fixture = new WavePresentationFixture("hero");
        var window = fixture.Window;
        try
        {
            window.Width = 1280; window.Height = 860; window.Show();
            Control<Slider>(window, "VolumeSlider").Value = 0;
            await Call(window, "ActivateAsync", FakeSession, CancellationToken.None);
            var favorites = Control<Button>(window, "FavoritesWaveButton");
            Assert(favorites.IsVisible && favorites.IsEnabled, "collection action is missing from Мне нравится");
            WaveClick(favorites);
            await WaveReady(window, fixture.Player, WaveMode.Collection);
            Assert(fixture.Seeds.FirstOrDefault() == "personal:collection", "favorites production click used the wrong server seed");
            WaveAssertHero(window, fixture.Player, WaveMode.Collection);
            await WaveCapture(window, "collection");

            WaveClick(Control<Button>(window, "WavePlayButton"));
            await Until(() => !fixture.Player.IsPlaying);
            Assert(Control<Button>(window, "WavePlayButton").Content.ToString()!.Contains("Слушать", StringComparison.Ordinal), "hero pause did not expose its play state");
            WaveClick(Control<Button>(window, "WavePlayButton"));
            await Until(() => fixture.Player.IsPlaying);
            Assert(Control<Button>(window, "WavePlayButton").Content.ToString()!.Contains("Пауза", StringComparison.Ordinal), "hero resume did not expose its pause state");

            var beforeNext = WaveField<TrackRow>(window, "_current");
            WaveClick(Control<Button>(window, "WaveNextButton"));
            await Until(() => !ReferenceEquals(WaveField<TrackRow>(window, "_current"), beforeNext) && !WaveField<bool>(window, "_loadingStream"));
            Assert(fixture.MoreRequests > 0 && WaveField<RadioBatch>(window, "_waveBatch").Mode == WaveMode.Collection, "Next did not obtain a collection batch");
            WaveAssertHero(window, fixture.Player, WaveMode.Collection);

            WaveClick(Control<Button>(window, "QueueNav"));
            Assert(!Control<Grid>(window, "WavePanel").IsVisible && Control<Grid>(window, "ContentGrid").IsVisible
                && Control<ListView>(window, "TracksList").IsVisible && WaveField<string>(window, "_section") == "queue", "queue navigation still displayed the Wave hero");
            Assert(Control<ListView>(window, "TracksList").Items.Count > 0 && fixture.Player.HasSource, "queue view lost its tracks or current audio");
            WaveClick(Control<Button>(window, "CollectionWaveNav"));
            await WaveReady(window, fixture.Player, WaveMode.Collection);
            WaveAssertHero(window, fixture.Player, WaveMode.Collection);
            var collectionSource = fixture.Player.SourceLocalPath;
            WaveClick(Control<Button>(window, "PlayerLyricsButton"));
            await Until(() => Control<ListBox>(window, "LyricsList").Items.Count == 2);
            Assert(Control<Grid>(window, "LyricsPanel").IsVisible && !Control<Grid>(window, "WavePanel").IsVisible
                && !Control<Grid>(window, "ContentGrid").IsVisible && window.OwnedWindows.Count == 0, "lyrics did not replace the hero inline");
            var escape = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window), 0, Key.Escape) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            window.RaiseEvent(escape);
            Assert(escape.Handled && !Control<Grid>(window, "LyricsPanel").IsVisible && Control<Grid>(window, "WavePanel").IsVisible
                && !Control<Grid>(window, "ContentGrid").IsVisible && fixture.Player.SourceLocalPath == collectionSource, "Escape did not restore Wave while retaining playback");

            WaveClick(Control<Button>(window, "WaveNav"));
            await WaveReady(window, fixture.Player, WaveMode.Personal);
            Assert(fixture.Seeds.Contains("user:onyourwave") && fixture.Seeds.All(seed => seed is "personal:collection" or "user:onyourwave"), "personal navigation confused server seed modes");
            WaveAssertHero(window, fixture.Player, WaveMode.Personal);
            await WaveCapture(window, "personal");
            WaveClick(Control<Button>(window, "CollectionWaveNav"));
            await WaveReady(window, fixture.Player, WaveMode.Collection);
            Assert(fixture.Seeds.Last() == "personal:collection", "collection sidebar did not invoke the production collection action");
            WaveAssertHero(window, fixture.Player, WaveMode.Collection);
            Assert(fixture.GeniusRequests == 0 && fixture.MediaRequests > 0, "Wave fixture escaped the intended local-audio/Yandex-lyrics path");
            File.WriteAllText(System.IO.Path.Combine(_directory, "wave-hero-readback.json"), JsonSerializer.Serialize(new
            {
                serverSeeds = fixture.Seeds, collectionContinuationRequests = fixture.MoreRequests,
                heroAndQueueDistinct = true, metadataAndFrozenCoverBound = true, playPauseBound = true,
                inlineLyricsEscapeReturnsHero = true, personalAndCollectionNavDistinct = true,
                artworkAndMediaHttp = "Synthetic fixtures only", screenshots = new[] { "1280x860", "1050x680" }
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        finally { window.Close(); }
    }

    private static async Task CheckWaveSeekHover()
    {
        using var fixture = new WavePresentationFixture("hover");
        var window = fixture.Window;
        try
        {
            window.Width = 1050; window.Height = 680; window.Show();
            Control<Slider>(window, "VolumeSlider").Value = 0;
            await window.PlayTrackAsync(new TrackRow { Id = "hover-fixture", RequestId = "hover-fixture", Title = "Проверка подсказки времени", Artist = "Локальный WAV", DurationMs = 30000, Available = true, LocalPath = WriteWave("wave-presentation-hover-30s.wav", 30) });
            Assert(fixture.Player.IsPlaying && fixture.Player.Duration.TotalSeconds > 29.8, "native hover WAV did not open");
            await Call(window, "TogglePlaybackAsync");
            WaveInvoke(window, "UpdateProgress"); window.UpdateLayout();
            var slider = Control<Slider>(window, "SeekSlider");
            var overlay = Control<Grid>(window, "SeekHoverOverlay");
            var range = Control<Border>(window, "SeekHoverRange");
            var bubble = Control<Border>(window, "SeekHoverBubble");
            var marker = Control<Border>(window, "SeekHoverMarker");
            Assert(slider.IsEnabled && slider.ActualWidth > 100 && !overlay.IsHitTestVisible, "hover prerequisites or hit-testing contract failed");
            var beforeValue = slider.Value; var beforePosition = fixture.Player.Position.TotalSeconds;
            var rail = slider.ActualWidth - 16;
            foreach (var (pointer, expectedTime, width) in new[]
            {
                (8 + rail / 2, "0:15", rail / 2), (slider.ActualWidth - 8, "0:30", rail),
                (-100d, "0:00", 0d), (slider.ActualWidth + 100, "0:30", rail)
            })
            {
                WaveInvoke(window, "UpdateSeekHover", pointer);
                Assert(overlay.Visibility == Visibility.Visible && Control<TextBlock>(window, "SeekHoverTime").Text == expectedTime, "hover time math or edge clamp failed");
                Assert(Math.Abs(range.Width - width) < .01 && Math.Abs(Canvas.GetLeft(marker) - (7 + width)) < .01, "hover range/marker did not follow the local rail coordinate");
                Assert(Canvas.GetLeft(bubble) >= 0 && Canvas.GetLeft(bubble) <= Math.Max(0, slider.ActualWidth - bubble.Width), "hover bubble exceeded the slider edges");
            }
            Assert(Math.Abs(slider.Value - beforeValue) < .001 && Math.Abs(fixture.Player.Position.TotalSeconds - beforePosition) < .05, "hover mutated the Slider or paused native position");
            WaveInvoke(window, "UpdateSeekHover", 8 + rail / 2);
            await Task.Delay(300); App.Capture(window, System.IO.Path.Combine(_directory, "wave-player-hover-1050x680.png"));
            slider.IsEnabled = false;
            Assert(overlay.Visibility == Visibility.Collapsed, "disabling native seek did not hide hover");
            WaveInvoke(window, "UpdateSeekHover", 40d);
            Assert(overlay.Visibility == Visibility.Collapsed, "disabled hover reopened");
            slider.IsEnabled = true; WaveInvoke(window, "UpdateSeekHover", double.NaN);
            Assert(overlay.Visibility == Visibility.Collapsed, "non-finite hover coordinate was accepted");
            WaveInvoke(window, "UpdateSeekHover", 40d);
            slider.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount) { RoutedEvent = UIElement.MouseLeaveEvent });
            Assert(overlay.Visibility == Visibility.Collapsed, "native mouse leave handler did not hide hover");
            File.WriteAllText(System.IO.Path.Combine(_directory, "wave-hover-readback.json"), JsonSerializer.Serialize(new { middle = "0:15", end = "0:30", leftAndRightClamped = true, previewOnlyNoSeek = true, disableInvalidAndLeaveHide = true, nativePositionBefore = beforePosition, nativePositionAfter = fixture.Player.Position.TotalSeconds, globalMouseInputUsed = false }));
        }
        finally { window.Close(); }
    }

    private static Task CheckWaveCompactControls()
    {
        using var fixture = new WavePresentationFixture("controls");
        var window = fixture.Window;
        try
        {
            window.Show(); Control<Slider>(window, "VolumeSlider").Value = 0; WaveInvoke(window, "UpdateControls");
            var shuffle = Control<Button>(window, "ShuffleButton"); var repeat = Control<Button>(window, "RepeatButton");
            var shuffleContent = shuffle.Content; var repeatContent = repeat.Content;
            Assert(shuffleContent is Grid && repeatContent is Grid && WaveVisuals<System.Windows.Shapes.Path>((Grid)shuffleContent).Any() && WaveVisuals<System.Windows.Shapes.Path>((Grid)repeatContent).Any(), "transport controls are not icon grids");
            Assert(Control<Ellipse>(window, "ShuffleIndicator").Visibility == Visibility.Collapsed && Control<Ellipse>(window, "RepeatIndicator").Visibility == Visibility.Collapsed, "default transport indicators are incorrect");
            WaveClick(shuffle);
            Assert(Control<Ellipse>(window, "ShuffleIndicator").Visibility == Visibility.Visible && ReferenceEquals(shuffle.Foreground, window.FindResource("AccentBrush")), "shuffle state lacks accent/dot");
            Assert(shuffle.ToolTip.ToString() == "Перемешивание включено" && AutomationProperties.GetName(shuffle) == shuffle.ToolTip.ToString(), "shuffle accessible state is missing");
            WaveClick(shuffle);
            Assert(Control<Ellipse>(window, "ShuffleIndicator").Visibility == Visibility.Collapsed && shuffle.ToolTip.ToString() == "Включить перемешивание", "shuffle off state did not return");
            foreach (var (tooltip, dot, one) in new[] { ("Повтор всей очереди", true, false), ("Повтор трека", true, true), ("Повтор выключен", false, false) })
            {
                WaveClick(repeat);
                Assert(repeat.ToolTip.ToString() == tooltip && AutomationProperties.GetName(repeat) == tooltip, "repeat accessible cycle is incorrect");
                Assert((Control<Ellipse>(window, "RepeatIndicator").Visibility == Visibility.Visible) == dot && (Control<TextBlock>(window, "RepeatOneLabel").Visibility == Visibility.Visible) == one, "repeat icon state is incorrect");
                Assert(ReferenceEquals(repeat.Foreground, window.FindResource(dot ? "AccentBrush" : "MutedBrush")), "repeat state foreground is incorrect");
            }
            Assert(ReferenceEquals(shuffle.Content, shuffleContent) && ReferenceEquals(repeat.Content, repeatContent)
                && !WaveVisuals<TextBlock>((Grid)shuffleContent).Any() && WaveVisuals<TextBlock>((Grid)repeatContent).All(text => text.Text == "1"), "toggle injected clipped Вкл text into compact controls");
            WaveInvoke(window, "UpdateMetrics");
            Assert(Control<TextBlock>(window, "MemoryText").Text.StartsWith("RAM всего ", StringComparison.Ordinal) && Control<TextBlock>(window, "PrivateText").Text.StartsWith("Commit ", StringComparison.Ordinal), "memory footer labels remain ambiguous");
            foreach (var name in new[] { "TracksList", "CatalogList", "LyricsList" })
            {
                var list = Control<ItemsControl>(window, name);
                Assert(VirtualizingPanel.GetIsVirtualizing(list) && VirtualizingPanel.GetVirtualizationMode(list) == VirtualizationMode.Recycling
                    && VirtualizingPanel.GetScrollUnit(list) == ScrollUnit.Pixel && ScrollViewer.GetCanContentScroll(list), name + " lost pixel recycling virtualization");
            }
            File.WriteAllText(System.IO.Path.Combine(_directory, "wave-controls-readback.json"), JsonSerializer.Serialize(new { iconsOnly = true, shuffleAccentAndDot = true, repeatAllOneOff = true, accessibleNamesMatchState = true, ramLabel = "RAM всего", privateBytesLabel = "Commit", pixelRecyclingLists = new[] { "TracksList", "CatalogList", "LyricsList" } }));
        }
        finally { window.Close(); }
        return Task.CompletedTask;
    }

    private static async Task WaveReady(MainWindow window, NativePlayer player, WaveMode mode)
        => await Until(() => !WaveField<bool>(window, "_waveStarting") && !WaveField<bool>(window, "_loadingStream") && WaveField<RadioBatch?>(window, "_waveBatch")?.Mode == mode && player.HasSource && player.IsPlaying);
    private static void WaveAssertHero(MainWindow window, NativePlayer player, WaveMode mode)
    {
        var current = WaveField<TrackRow>(window, "_current");
        Assert(Control<Grid>(window, "WavePanel").IsVisible && !Control<Grid>(window, "ContentGrid").IsVisible, "Wave is still displayed as the collection list");
        Assert(Control<TextBlock>(window, "WaveHeading").Text == (mode == WaveMode.Collection ? "Волна по коллекции" : "Моя волна"), "Wave heading does not match the selected mode");
        Assert(Control<TextBlock>(window, "WaveTrackTitle").Text == current.Title && Control<TextBlock>(window, "WaveTrackArtist").Text == current.Artist
            && Artwork.GetSourceUri(Control<Image>(window, "WaveCover")) == current.CoverUri, "hero metadata is not bound to the playing track");
        Assert(Control<Button>(window, "WavePlayButton").Content.ToString()!.Contains("Пауза", StringComparison.Ordinal) && player.IsPlaying, "hero transport state does not match native playback");
        var selected = Control<Button>(window, mode == WaveMode.Collection ? "CollectionWaveNav" : "WaveNav");
        var other = Control<Button>(window, mode == WaveMode.Collection ? "WaveNav" : "CollectionWaveNav");
        Assert(ReferenceEquals(selected.Foreground, window.FindResource("AccentBrush")) && !ReferenceEquals(other.Foreground, window.FindResource("AccentBrush")), "both Wave modes appear selected or the wrong one is selected");
    }
    private static async Task WaveCapture(MainWindow window, string mode)
    {
        foreach (var (width, height) in new[] { (1280, 860), (1050, 680) })
        {
            window.Width = width; window.Height = height; window.UpdateLayout();
            await Until(() => Control<Image>(window, "WaveCover").Source is BitmapSource { IsFrozen: true });
            await Task.Delay(300);
            Assert(Control<Grid>(window, "WavePanel").IsVisible && Control<Button>(window, "WavePlayButton").IsVisible && Control<TextBlock>(window, "WaveTrackTitle").IsVisible, "Wave content disappeared on resize");
            App.Capture(window, System.IO.Path.Combine(_directory, $"wave-{mode}-{width}x{height}.png"));
        }
    }
    private static void WaveClick(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static T WaveField<T>(MainWindow window, string name) => (T)typeof(MainWindow).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!;
    private static void WaveInvoke(MainWindow window, string method, params object[] arguments) => typeof(MainWindow).GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, arguments);
    private static IEnumerable<T> WaveVisuals<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T typed) yield return typed;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in WaveVisuals<T>(VisualTreeHelper.GetChild(root, i))) yield return child;
    }

    private sealed class WavePresentationFixture : IDisposable
    {
        internal MainWindow Window { get; }
        internal NativePlayer Player { get; }
        internal List<string> Seeds { get; } = [];
        internal int MoreRequests { get; private set; }
        internal int MediaRequests { get; private set; }
        internal int GeniusRequests { get; private set; }
        private readonly CatalogFixture _catalog = new();
        private int _session;
        internal WavePresentationFixture(string suffix)
        {
            var directory = System.IO.Path.Combine(_directory, "wave-presentation-" + suffix); Directory.CreateDirectory(directory);
            var media = File.ReadAllBytes(WriteWave("wave-presentation-" + suffix + ".wav", 30));
            var mediaHandler = new FakeHandler((request, _) =>
            {
                Assert(MusicApi.ValidateMediaUri(request.RequestUri!) == request.RequestUri && request.Headers.Authorization is null, "native media fixture received an unexpected URI or credentials");
                MediaRequests++;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(media) });
            });
            Player = new NativePlayer(new AudioBuffer(System.IO.Path.Combine(directory, "buffer"), mediaHandler));
            _catalog.Extra = async (request, token) =>
            {
                var path = request.RequestUri!.AbsolutePath;
                if (path == "/rotor/session/new")
                {
                    using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
                    var seed = body.RootElement.GetProperty("seeds")[0].GetString()!; Seeds.Add(seed);
                    Assert(seed is "personal:collection" or "user:onyourwave", "unexpected Wave seed");
                    var mode = seed == "personal:collection" ? "collection" : "personal";
                    var ordinal = ++_session;
                    return Json(WavePresentationBatch(mode + "-" + ordinal, "first-" + ordinal, ordinal * 100 + 1, mode));
                }
                if (path.StartsWith("/rotor/session/", StringComparison.Ordinal) && path.EndsWith("/tracks", StringComparison.Ordinal))
                {
                    MoreRequests++; var sid = path.Split('/')[3];
                    return Json(WavePresentationBatch(sid, "more-" + MoreRequests, _session * 100 + 1 + MoreRequests, sid.StartsWith("collection", StringComparison.Ordinal) ? "collection" : "personal"));
                }
                if (path.StartsWith("/rotor/session/", StringComparison.Ordinal) && path.TrimEnd('/').EndsWith("/feedback", StringComparison.Ordinal)) return Json("{\"result\":{}}");
                if (path == "/get-file-info")
                {
                    // Complete audio fixture for the active preference; these UI
                    // checks do not assert codec quality or service negotiation.
                    var query = request.RequestUri.Query;
                    var quality = query.Contains("quality=lq", StringComparison.Ordinal) ? "lq" : query.Contains("quality=nq", StringComparison.Ordinal) ? "nq" : "lossless";
                    var bitrate = quality == "lq" ? 64 : quality == "nq" ? 192 : 320;
                    return Json(JsonSerializer.Serialize(new { result = new { downloadInfo = new { quality, codec = "mp3", bitrate, transport = "raw", url = "https://s1.vlx.yandex.net/wave-presentation-" + quality + ".mp3" } } }));
                }
                if (path.EndsWith("/download-info", StringComparison.Ordinal))
                    return Json(JsonSerializer.Serialize(new { result = new[] { 64, 192, 320 }.Select(bitrate => new { codec = "mp3", bitrateInKbps = bitrate, downloadInfoUrl = "https://storage.mds.yandex.net/wave-presentation" }) }));
                if (request.RequestUri.Host == "storage.mds.yandex.net") return Xml();
                if (path.EndsWith("/lyrics", StringComparison.Ordinal) || path.EndsWith("/supplement", StringComparison.Ordinal)) return LyricsUiYandex(request, "Строка тестовой волны\nВторая строка тестовой волны");
                throw new InvalidOperationException("Unexpected Wave fixture route: " + path);
            };
            Window = new MainWindow(api: new MusicApi(_catalog.Handler), store: new SessionStore(System.IO.Path.Combine(directory, "session")), restore: false,
                diagnosticsDirectory: System.IO.Path.Combine(directory, "diagnostics"), offline: new OfflineLibrary(System.IO.Path.Combine(directory, "offline")), player: Player);
            typeof(MainWindow).GetField("_geniusLyrics", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(Window,
                new GeniusLyricsClient(new FakeHandler((_, _) => { GeniusRequests++; throw new InvalidOperationException("Wave lyrics unexpectedly requested Genius"); })));
            typeof(MainWindow).GetField("_lrclibLyrics", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(Window,
                new LrclibLyricsClient(new HttpClient(new FakeHandler((_, _) => Task.FromResult(Json("{}", HttpStatusCode.NotFound))))));
        }
        private static string WavePresentationBatch(string sid, string batch, int id, string mode) => JsonSerializer.Serialize(new
        {
            result = new { radioSessionId = sid, batchId = batch, sequence = new[] { new { type = "track", track = new
            {
                id = id.ToString(), title = (mode == "collection" ? "Волна из вашей коллекции" : "На своей волне") + " · " + id, available = true, durationMs = 30000,
                coverUri = "avatars.yandex.net/get-music-content/wave-presentation-fixture/cover-" + id + "/%%", artists = new[] { new { name = "Тестовый исполнитель " + id } }, albums = new[] { new { id = "91", title = "Локальная проверка" } }
            } } } }
        });
        public void Dispose() { if (Window.IsLoaded) Window.Close(); }
    }
}
