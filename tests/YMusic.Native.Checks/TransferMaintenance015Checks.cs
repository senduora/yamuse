using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using YMusic.Native;

internal static partial class Program
{
    private static async Task CheckTransferMaintenance015()
    {
        await Run("Transfer015: overall work separates saved errors unavailable partial and unknown bytes", Transfer015Model);
        await Run("Transfer015: playlist button and download center retain real progress through pause resume and completion", Transfer015Playlist);
        await Run("Transfer015: failed and unavailable entries finish work without claiming every track saved", Transfer015Errors);
        await Run("Maintenance015: cache button clears private caches while preserving music and Settings draft", Maintenance015Cache);
        await Run("Maintenance015: confirmed offline clear displays progress and preserves foreign files and draft", Maintenance015Offline);
        await Run("Maintenance015: inline artwork cancellation cannot corrupt pending enumeration or retain the clearing state", Maintenance015InlineCancellation);
        await Run("Authors015: server owner names and exact current-owner fallback populate cards and playlist header", Authors015);
    }
    private static Task Transfer015Model()
    {
        var queue = new DownloadQueueState(); queue.Replace(Enumerable.Range(1, 5).Select(i => DcTrack(i.ToString())), "Fixture");
        var entries = queue.BeginRun(false);
        entries[0].Complete(100); entries[1].SetState(DownloadEntryState.Failed, "Fixture failure"); entries[2].SetState(DownloadEntryState.Unavailable);
        entries[3].Begin(); entries[3].Progress(new(50, 100)); entries[4].Begin(); entries[4].Progress(new(40, null));
        var p = PlaylistTransferProgress.Read(queue.Entries);
        Assert(p.Total == 5 && p.Completed == 1 && p.Failed == 1 && p.Unavailable == 1 && p.Active == 2 && p.Finished == 3
            && Math.Abs(p.Percent - 70) < .001 && p.ReceivedBytes == 190, "aggregate work fabricated byte totals or hid errors");
        Assert(p.Details.Contains("Скачано 1 из 5") && p.Details.Contains("ошибок 1") && p.Details.Contains("недоступно 1"), "progress conflates saved with processed");
        entries[3].Complete(100); entries[4].Complete(100); queue.EndRun(false); p = PlaylistTransferProgress.Read(queue.Entries);
        Assert(p.Percent == 100 && p.Completed == 3 && p.Total == 5, "failed work falsely claimed all downloads succeeded");
        queue.BeginRun(true); p = PlaylistTransferProgress.Read(queue.Entries);
        Assert(p.Percent < 100 && p.Failed == 0, "retry left a false completed bar");
        entries[2].SetCollectionLike(false, favoritesBatch: true);
        Assert(PlaylistTransferProgress.Read(queue.Entries).Total == 4 && PlaylistTransferProgress.Read([]).Percent == 0, "excluded or empty queue has an invalid denominator");
        return Task.CompletedTask;
    }
    private static async Task Transfer015Playlist()
    {
        using var f = new DownloadCenterFixture("transfer015-playlist", 2) { HoldFirstAttempts = true }; var w = f.Window;
        var playlist = new PlaylistDetails("12345", "42", "Плейлист для проверки загрузки", "", 1,
            [DcTrack("1"), DcTrack("2"), DcTrack("3")], OwnerName: "Автор fixture");
        SaInvoke(w, "ShowPlaylist", playlist); var task = Call(w, "DownloadPlaylistAsync");
        await Until(() => f.Queue.Entries.Count == 3 && f.Queue.Entries.Any(e => e.State == DownloadEntryState.Completed) && f.Active > 0);
        SaInvoke(w, "FlushDownloadProgress"); w.UpdateLayout();
        var bar = SaField<CircularDownloadProgress>(w, "_compactDownloadRing");
        Assert(Control<Border>(w, "PlaylistTransferPanel").Visibility == Visibility.Collapsed && bar.Value is > 0 and < 100
            && SaField<TextBlock>(w, "_compactDownloadCaption").Text.Contains("1/3"), "playlist has no real current-run progress");
        var overall = SaVisuals<ProgressBar>(w.DownloadCenterView).Single(p => p.Name == "DownloadOverallProgress");
        SaInvoke(w, "RefreshDownloadCenter");
        Assert(Math.Abs(overall.Value - bar.Value) < .01, "playlist and download center disagree");
        Assert(bar.Width == 18 && bar.Height == 18 && bar.ActualWidth is >= 16 and <= 20 && bar.ActualHeight is >= 16 and <= 20
            && !bar.IsIndeterminate, "progress ring is not compact and finite");
        App.Capture(w, Path.Combine(_directory, "transfer015-playlist-running-1280.png"));
        SaInvoke(w, "PauseDownloadQueue"); await task;
        Assert(SaField<TextBlock>(w, "_compactDownloadCaption").Text.Contains("паузе") && f.Library.Read().Count == 1, "pause lost saved tracks or progress");
        await Call(w, "ResumeDownloadQueue");
        Assert(bar.Value == 100 && f.Library.Read().Count == 3 && Control<TextBlock>(w, "PlaylistTransferTitle").Text == "Плейлист скачан", "resume did not reach a confirmed saved state");
        SaInvoke(w, "DownloadCenter_Click", w, new RoutedEventArgs()); App.Capture(w, Path.Combine(_directory, "transfer015-downloads-complete-1280.png"));
        SaInvoke(w, "SetSection", "favorites", "Мне нравится");
        Assert(Control<Border>(w, "PlaylistTransferPanel").Visibility == Visibility.Collapsed, "unrelated playlist shows the old queue progress");
    }
    private static async Task Transfer015Errors()
    {
        using var f = new DownloadCenterFixture("transfer015-errors", 2) { FailTrack = "2" }; var w = f.Window;
        var playlist = new PlaylistDetails("12345", "43", "Смешанный результат", "", 0, [DcTrack("1"), DcTrack("2"), DcTrack("3", false)]);
        SaInvoke(w, "ShowPlaylist", playlist); await Call(w, "DownloadPlaylistAsync");
        var p = PlaylistTransferProgress.Read(f.Queue.Entries);
        Assert(p.Percent == 100 && p.Completed == 1 && p.Failed == 1 && p.Unavailable == 1, "error completion lost actual outcomes");
        Assert(SaField<CircularDownloadProgress>(w, "_compactDownloadRing").Value < 100
            && SaField<TextBlock>(w, "_compactDownloadCaption").Text == "Не скачано · 1/3"
            && Control<Button>(w, "DownloadPlaylistButton").ToolTip.ToString()!.Contains("ошибок 1 · недоступно 1"), "UI falsely reports a full offline playlist");
        App.Capture(w, Path.Combine(_directory, "transfer015-partial-result-1280.png"));
    }
    private static async Task Maintenance015Cache()
    {
        using var f = new DownloadCenterFixture("maintenance015-cache", 1); var w = f.Window;
        var field = typeof(Artwork).GetField("_cache", BindingFlags.Static | BindingFlags.NonPublic)!; var prior = field.GetValue(null);
        var artworkPath = Path.Combine(f.DirectoryPath, "artwork");
        var bitmap = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[] { 32, 64, 255, 255 }, 4);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var png = new MemoryStream(); encoder.Save(png);
        using var cache = new ArtworkCache(artworkPath, new FakeHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(png.ToArray()) })));
        try
        {
            Artwork.Cache = cache;
            Assert(await cache.GetAsync("https://avatars.yandex.net/get-music-content/1/fixture/200x200", 128, default) is not null, "private artwork fixture failed");
            var analysisDir = Path.Combine(f.DirectoryPath, "player-buffer", "loudness"); Directory.CreateDirectory(analysisDir);
            var analysis = Path.Combine(analysisDir, "loudness-v1.json");
            File.WriteAllText(analysis, JsonSerializer.Serialize(new Dictionary<string, object> { [new string('a', 64)] = new { IntegratedLufs = -18, Peak = .8 } }));
            var music = Path.Combine(f.DirectoryPath, "Music", "foreign-song.mp3"); Directory.CreateDirectory(Path.GetDirectoryName(music)!); File.WriteAllText(music, "USER-OWNED-FIXTURE");
            SaInvoke(w, "Settings_Click", w, new RoutedEventArgs()); Control<ComboBox>(w, "SettingsSpeed").SelectedItem = 1.25;
            await Call(w, "ClearCachesAsync");
            Assert(!Directory.EnumerateFiles(artworkPath, "*.img").Any() && !File.Exists(analysis) && File.ReadAllText(music) == "USER-OWNED-FIXTURE", "cache UI touched music or retained its private caches");
            Assert(SaField<bool>(w, "_settingsOpen") && Equals(Control<ComboBox>(w, "SettingsSpeed").SelectedItem, 1.25)
                && Control<TextBlock>(w, "MaintenanceStatus").Text.Contains("Кэш очищен") && Control<Button>(w, "SettingsClearCache").IsEnabled, "cache completion closed Settings or lost its draft");
            Control<StackPanel>(w, "MaintenancePanel").BringIntoView(); w.UpdateLayout();
            App.Capture(w, Path.Combine(_directory, "maintenance015-cache-settings-1280.png"));
        }
        finally { field.SetValue(null, prior); }
    }
    private static async Task Maintenance015Offline()
    {
        using var f = new DownloadCenterFixture("maintenance015-offline", 2); await f.Start([DcTrack("1"), DcTrack("2")]);
        var foreign = Path.Combine(f.Library.DirectoryPath, "foreign.txt"); File.WriteAllText(foreign, "PRESERVE");
        SaInvoke(f.Window, "Settings_Click", f.Window, new RoutedEventArgs()); Control<ComboBox>(f.Window, "SettingsSpeed").SelectedItem = 1.25;
        await Call(f.Window, "ClearOfflineAfterConfirmationAsync");
        Assert(f.Library.Read().Count == 0 && File.ReadAllText(foreign) == "PRESERVE" && f.Queue.Suspended, "clear frontend violated the queue or preserved-file contract");
        Assert(Control<ProgressBar>(f.Window, "MaintenanceProgress").Value == 100 && Control<TextBlock>(f.Window, "MaintenanceStatus").Text.Contains("Удалено")
            && SaField<bool>(f.Window, "_settingsOpen") && Equals(Control<ComboBox>(f.Window, "SettingsSpeed").SelectedItem, 1.25), "clear progress lost Settings draft");
        Control<StackPanel>(f.Window, "MaintenancePanel").BringIntoView(); f.Window.UpdateLayout();
        App.Capture(f.Window, Path.Combine(_directory, "maintenance015-music-cleared-1280.png"));
    }
    private static async Task Maintenance015InlineCancellation()
    {
        var directory = Path.Combine(_directory, "maintenance015-inline-cancel");
        var admitted = 0;
        using var cache = new ArtworkCache(directory, new FakeHandler((_, token) =>
        {
            Interlocked.Increment(ref admitted);
            var reply = new TaskCompletionSource<HttpResponseMessage>();
            token.Register(() => reply.TrySetCanceled(token));
            return reply.Task;
        }));
        var first = cache.GetAsync("https://avatars.yandex.net/get-music-content/1/inline-a/200x200", 128, default);
        var second = cache.GetAsync("https://avatars.yandex.net/get-music-content/1/inline-b/200x200", 128, default);
        await Until(() => Volatile.Read(ref admitted) == 2);
        await cache.ClearAsync(default).WaitAsync(TimeSpan.FromSeconds(5));
        foreach (var task in new[] { first, second })
            try { await task; } catch (OperationCanceledException) { }
        var next = cache.GetAsync("https://avatars.yandex.net/get-music-content/1/inline-next/200x200", 128, default);
        await Until(() => Volatile.Read(ref admitted) == 3);
        await cache.ClearAsync(default).WaitAsync(TimeSpan.FromSeconds(5));
        try { await next; } catch (OperationCanceledException) { }
        Assert(admitted == 3, "cache retained a permanent clearing state after inline cancellations");
    }
    private static async Task Authors015()
    {
        using var api = new MusicApi(new FakeHandler((request, _) =>
        {
            Assert(request.RequestUri!.AbsolutePath == "/search", "author check made an extra lookup");
            return Task.FromResult(Json("{\"result\":{\"playlists\":{\"page\":0,\"perPage\":20,\"total\":2,\"results\":[{\"kind\":\"10\",\"title\":\"Подборка\",\"owner\":{\"uid\":\"44\",\"displayName\":\"Яндекс Музыка\"}},{\"kind\":\"11\",\"title\":\"Другой плейлист\",\"owner\":{\"uid\":\"45\",\"name\":\"Другой автор\"}}]}}}"));
        }));
        var results = await api.SearchCatalogAsync(FakeSession, "fixture", "playlist", 0, default);
        Assert(results.Items[0].OwnerName == "Яндекс Музыка" && results.Items[0].DisplaySubtitle == "Яндекс Музыка" && results.Items[1].OwnerName == "Другой автор", "search discarded playlist authors");
        using var f = new DownloadCenterFixture("authors015", 1); var w = f.Window;
        typeof(MainWindow).GetField("_account", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(w, new Account("12345", "Мой профиль fixture", true));
        SaInvoke(w, "ShowCatalogItems", (object)new[] { new CatalogItem("42", "playlist", "Мой плейлист", "", "12345", "42"),
            new CatalogItem("43", "playlist", "Неизвестный автор", "", "999", "43"), results.Items[0] }, false);
        var items = SaField<System.Collections.ObjectModel.ObservableCollection<CatalogItem>>(w, "_catalogItems");
        Assert(items[0].DisplaySubtitle == "Мой профиль fixture" && items[1].DisplaySubtitle == "" && items[2].DisplaySubtitle == "Яндекс Музыка", "profile name was guessed for another author's playlist");
        SaInvoke(w, "ShowPlaylist", new PlaylistDetails("12345", "42", "Мой плейлист", "", 0, [DcTrack("1")]));
        Assert(SaField<PlaylistDetails>(w, "_activePlaylist").OwnerName == "Мой профиль fixture", "playlist metadata omitted its exact current-owner fallback");
        w.UpdateLayout();
        Assert(SaVisuals<TextBlock>(Control<ContentControl>(w, "CollectionHero")).Any(t => t.Text == "Мой профиль fixture"), "realized header omitted its known creator");
        App.Capture(w, Path.Combine(_directory, "authors015-own-playlist-1280.png"));
    }
}
