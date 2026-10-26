using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using YMusic.Native;

internal static partial class Program
{
    private static async Task CheckLargeCollection015()
    {
        await Run("Large015: bulk replacement emits one Reset and preserves duplicate references and source ranks", Large015Replacement);
        await Run("Large015: 3920 cached favorites render while index IO is blocked and retain a Settings draft on completion", () => Large015Favorites(3920));
        await Run("Large015: 257-row boundary dispatches both marks and playlist IO while 256 remains synchronous", Large015Boundary);
        await Run("Large015: large sorted and filtered views preserve the active queue and exact duplicate occurrence", Large015SortingAndQueue);
        await Run("Large015: superseded and epoch-invalidated workers cannot apply late offline marks or playlist status", Large015ViewRaces);
        await Run("Large015: sign-out and lifetime cancellation reject late workers without account or network access", Large015AccountRaces);
    }

    private static Task Large015Replacement()
    {
        var a = SortTrack("large-a", "A", "", "", 1000); var b = SortTrack("large-b", "B", "", "", 2000);
        var rows = new TrackRows(); using var order = new ListSourceOrder(rows);
        var notifications = new List<NotifyCollectionChangedAction>(); var properties = new List<string?>();
        rows.CollectionChanged += (_, e) => notifications.Add(e.Action);
        ((INotifyPropertyChanged)rows).PropertyChanged += (_, e) => properties.Add(e.PropertyName);
        rows.ReplaceAll([a, b, a]);
        Assert(notifications.SequenceEqual([NotifyCollectionChangedAction.Reset]) && properties.SequenceEqual(["Count", "Item[]"]), "bulk replacement emitted per-row notifications");
        Assert(rows.SequenceEqual([a, b, a]) && ReferenceEquals(rows[0], rows[2]) && order.Rank(b) == 1, "replacement lost occurrences or rebuilt incorrect ranks");
        notifications.Clear(); properties.Clear(); rows.ReplaceAll([a, b, a]); rows.ReplaceAll(rows);
        Assert(notifications.Count == 0 && properties.Count == 0, "unchanged row identities still reset the collection");
        rows.ReplaceAll([b, a, a]);
        Assert(notifications.SequenceEqual([NotifyCollectionChangedAction.Reset]) && order.Rank(b) == 0 && order.Rank(a) == 1, "reordered duplicate references left stale source ranks");
        return Task.CompletedTask;
    }

    private static async Task Large015Favorites(int count)
    {
        using var f = await Large015Fixture.Create("favorites-" + count, count);
        var source = SaField<TrackRows>(f.Window, "_tracks"); var notifications = new List<NotifyCollectionChangedAction>();
        source.CollectionChanged += (_, e) => notifications.Add(e.Action);
        var ui = Environment.CurrentManagedThreadId; var wrongThread = false;
        foreach (var row in f.Rows) row.PropertyChanged += (_, _) => wrongThread |= Environment.CurrentManagedThreadId != ui;
        using var hold = await Large015IndexHold.Acquire(f.Offline);
        var clock = Stopwatch.StartNew(); SaInvoke(f.Window, "Favorites_Click", f.Window, new RoutedEventArgs()); clock.Stop();
        Assert(hold.IsHeld && !hold.TimedOut, "opening cached favorites waited for index IO on the dispatcher");
        Assert(source.Count == count && f.List.Items.Count == count && notifications.SequenceEqual([NotifyCollectionChangedAction.Reset]), "large favorites did not publish all rows with one Reset");
        var pending = Large015Pending(f.Window);
        Assert(pending.Length == 2 && pending.All(task => !task.IsCompleted) && !f.Rows[0].IsDownloaded,
            "marks or playlist status performed inline IO, or the blocked fixture was not exercised");
        var firstStatusTask = SaField<Task?>(f.Window, "_playlistStatusTask");
        SaInvoke(f.Window, "UpdatePlaylistDownloadedStatus");
        Assert(ReferenceEquals(firstStatusTask, SaField<Task?>(f.Window, "_playlistStatusTask")), "an unchanged pending status spawned another full scan");
        f.Window.UpdateLayout(); await Dispatcher.Yield(DispatcherPriority.Render);
        Assert(f.List.ItemContainerGenerator.ContainerFromIndex(0) is ListViewItem && hold.IsHeld && !hold.TimedOut,
            "first visible rows could not render while offline IO was blocked");
        var sentinel = false;
        await f.Window.Dispatcher.InvokeAsync(() => sentinel = true, DispatcherPriority.Background).Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert(sentinel && hold.IsHeld && !hold.TimedOut, "dispatcher could not service unrelated work during offline IO");
        // Opening the draft is independent of delayed offline completion.
        f.OpenSettingsDraft(); var preferencesBefore = File.ReadAllBytes(f.Preferences.FilePath);
        hold.Release(); await Large015Complete(pending); f.Window.UpdateLayout();
        Assert(!wrongThread && f.Rows.Take(2).All(row => row.IsDownloaded) && f.Rows.Skip(2).All(row => !row.IsDownloaded), "completed marks were stale or changed properties outside the dispatcher");
        Assert(Control<TextBlock>(f.Window, "PlaylistDownloadText").Text == $"Скачано 2 из {count}"
            && Control<Button>(f.Window, "DownloadPlaylistButton").Content!.ToString() == $"↓ Докачать · 2/{count}", "completed playlist status did not match actual managed files");
        Assert(Control<Grid>(f.Window, "SettingsPanel").IsVisible && Control<Slider>(f.Window, "CrossfadeSetting").Value == 7.5
            && (int)Control<ComboBox>(f.Window, "SettingsDownloads").SelectedItem == 7
            && preferencesBefore.SequenceEqual(File.ReadAllBytes(f.Preferences.FilePath)), "offline completion closed, reset or saved the Settings draft");
        using (var warmHold = await Large015IndexHold.Acquire(f.Offline))
        {
            SaInvoke(f.Window, "UpdatePlaylistDownloadedStatus");
            Assert(warmHold.IsHeld && !warmHold.TimedOut && ReferenceEquals(firstStatusTask, SaField<Task?>(f.Window, "_playlistStatusTask")), "cached playlist status repeated IO for an unchanged view");
        }
        SaClick(Control<Button>(f.Window, "SettingsBackButton"));
        notifications.Clear(); SaInvoke(f.Window, "Favorites_Click", f.Window, new RoutedEventArgs());
        Assert(notifications.Count == 0, "opening identical cached rows caused a redundant source Reset");
        await Large015Complete(Large015Pending(f.Window));
        Assert(f.Requests == 0, "cached collection test accessed account, audio or network data");
        File.WriteAllText(Path.Combine(f.Directory, "facts.json"), JsonSerializer.Serialize(new
        {
            count, sourceNotifications = 1, repeatedSourceNotifications = notifications.Count, handlerMs = clock.Elapsed.TotalMilliseconds,
            renderedWhileIndexHeld = true, dispatcherSentinelWhileIndexHeld = true, downloaded = f.Rows.Count(row => row.IsDownloaded),
            offlinePropertyChangesOnDispatcher = !wrongThread, settingsDraftPreserved = true, requests = f.Requests
        }, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static async Task Large015Boundary()
    {
        await Large015Favorites(257);
        using var f = await Large015Fixture.Create("small-256", 256);
        SaInvoke(f.Window, "Favorites_Click", f.Window, new RoutedEventArgs());
        Assert(f.Rows.Take(2).All(row => row.IsDownloaded) && SaField<Task?>(f.Window, "_offlineMarksTask") is null
            && SaField<Task?>(f.Window, "_playlistStatusTask") is null
            && Control<TextBlock>(f.Window, "PlaylistDownloadText").Text == "Скачано 2 из 256", "small-list immediate mark/status semantics changed");
        Assert(f.Requests == 0, "small-list check accessed network");
    }

    private static async Task Large015SortingAndQueue()
    {
        using var f = await Large015Fixture.Create("sorting-queue", 3920); var w = f.Window;
        SaInvoke(w, "Favorites_Click", w, new RoutedEventArgs()); await Large015Complete(Large015Pending(w));
        SortChoose(w, ListSortField.Duration);
        Assert(f.List.Items.Cast<TrackRow>().SequenceEqual(f.Rows.OrderBy(row => row.DurationMs)), "bulk Reset broke numeric sorting");
        Control<TextBox>(w, "FilterBox").Text = "фильтр-метка";
        var expected = f.Rows.Where(row => row.Matches("фильтр-метка")).OrderBy(row => row.DurationMs).ToArray();
        await Large015Until(() => f.List.Items.Count == expected.Length);
        Assert(f.List.Items.Cast<TrackRow>().SequenceEqual(expected), "filter and active sort disagree after bulk replacement");
        Control<TextBox>(w, "FilterBox").Clear(); await Large015Until(() => f.List.Items.Count == f.Rows.Length);
        var duplicate = f.Rows[17]; var queueRows = f.Rows.ToArray(); queueRows[^1] = duplicate;
        var queue = SaField<PlaybackQueue>(w, "_playbackQueue"); queue.SetItems(queueRows); queue.SelectAt(queueRows.Length - 1);
        Large015Set(w, "_current", duplicate); var version = queue.Version; var token = queue.CurrentOccurrenceToken;
        SaInvoke(w, "ShowQueue"); await Large015Complete(Large015Pending(w));
        Assert(f.List.Items.Count == queueRows.Length && f.List.SelectedIndex == queueRows.Length - 1, "large queue lost its second duplicate occurrence");
        SortChoose(w, ListSortField.Title);
        Assert((int)SaInvokeResult(w, "SelectedQueueIndex")! == queueRows.Length - 1 && queue.Version == version
            && queue.CurrentOccurrenceToken == token && queue.Items.SequenceEqual(queueRows), "view sort changed current queue occurrence or scheduling order");
        SaInvoke(w, "Favorites_Click", w, new RoutedEventArgs()); await Large015Complete(Large015Pending(w));
        Assert(queue.Version == version && queue.CurrentOccurrenceToken == token && queue.Items.SequenceEqual(queueRows)
            && ReferenceEquals(SaField<TrackRow?>(w, "_current"), duplicate) && !f.Player.HasSource && f.Requests == 0,
            "large collection navigation changed playback/current queue or attempted playback");
    }

    private static async Task Large015ViewRaces()
    {
        foreach (var mode in new[] { "supersede", "epoch" })
        {
            using var f = await Large015Fixture.Create("race-" + mode, 257);
            f.Rows[0].DownloadedPath = "fixture-current-" + mode;
            using var hold = await Large015IndexHold.Acquire(f.Offline);
            SaInvoke(f.Window, "Favorites_Click", f.Window, new RoutedEventArgs()); var old = Large015Pending(f.Window);
            Assert(hold.IsHeld && old.Length == 2 && old.All(task => !task.IsCompleted), "late-worker race was not established");
            if (mode == "supersede")
            {
                var replacement = f.Rows.Select((row, index) => SortTrack("replacement-" + index, row.Title, "", "", 1000)).ToArray();
                SaInvoke(f.Window, "ShowRows", (object)replacement);
                var next = Large015Pending(f.Window); hold.Release(); await Large015Complete(old.Concat(next));
                Assert(f.List.Items.Cast<TrackRow>().SequenceEqual(replacement) && replacement.All(row => !row.IsDownloaded), "superseded marks changed the new view");
            }
            else
            {
                SaInvoke(f.Window, "InvalidateOfflineViewState");
                Control<TextBlock>(f.Window, "PlaylistDownloadText").Text = "fixture-current-epoch";
                hold.Release(); await Large015Complete(old);
                Assert(SaField<object?>(f.Window, "_playlistStatusCache") is null
                    && Control<TextBlock>(f.Window, "PlaylistDownloadText").Text == "fixture-current-epoch", "old epoch repopulated cached status or its UI");
            }
            Assert(f.Rows[0].DownloadedPath == "fixture-current-" + mode && !hold.TimedOut && f.Requests == 0, "canceled marks mutated the old row or fixture escaped its paths");
        }
    }

    private static async Task Large015AccountRaces()
    {
        foreach (var mode in new[] { "signout", "lifetime" })
        {
            using var f = await Large015Fixture.Create("race-" + mode, 257);
            f.Rows[0].DownloadedPath = "fixture-current-" + mode;
            using var hold = await Large015IndexHold.Acquire(f.Offline);
            SaInvoke(f.Window, "Favorites_Click", f.Window, new RoutedEventArgs()); var old = Large015Pending(f.Window);
            Assert(hold.IsHeld && old.Length == 2 && old.All(task => !task.IsCompleted), "account/lifetime race was not established");
            // Release IO and cancel in this same dispatcher turn, before an
            // awaited worker continuation can apply its former view/account.
            hold.Release();
            if (mode == "signout") f.Window.SignOut(); else f.Window.Close();
            await Large015Complete(old);
            Assert(f.Rows[0].DownloadedPath == "fixture-current-" + mode && !hold.TimedOut && f.Requests == 0, "late account/lifetime worker applied stale marks");
            if (mode == "signout")
                Assert(f.List.Items.Count == 0 && SaField<Session?>(f.Window, "_session") is null
                    && SaField<object?>(f.Window, "_playlistStatusCache") is null, "late status restored signed-out collection state");
        }
    }

    private static Task[] Large015Pending(MainWindow w) => new[] { SaField<Task?>(w, "_offlineMarksTask"), SaField<Task?>(w, "_playlistStatusTask") }.OfType<Task>().Distinct().ToArray();
    private static async Task Large015Complete(IEnumerable<Task> tasks) => await Task.WhenAll(tasks.Distinct()).WaitAsync(TimeSpan.FromSeconds(10));
    private static async Task Large015Until(Func<bool> ready)
    {
        var clock = Stopwatch.StartNew();
        while (!ready())
        {
            if (clock.Elapsed > TimeSpan.FromSeconds(4)) throw new InvalidOperationException("Expected collection state did not arrive.");
            await Task.Delay(10);
        }
    }
    private static void Large015Set(MainWindow w, string field, object? value) => typeof(MainWindow).GetField(field, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(w, value);

    // The independent release watchdog makes a dispatcher regression fail
    // without leaving the test runner blocked on its injected cache lock.
    private sealed class Large015IndexHold : IDisposable
    {
        private readonly ManualResetEventSlim _release = new();
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Task _holder;
        private int _held, _timedOut;
        internal bool IsHeld => Volatile.Read(ref _held) == 1;
        internal bool TimedOut => Volatile.Read(ref _timedOut) == 1;
        private Large015IndexHold(OfflineLibrary offline)
        {
            var cacheLock = typeof(OfflineLibrary).GetField("_cacheLock", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(offline)!;
            _holder = Task.Run(() =>
            {
                lock (cacheLock)
                {
                    Volatile.Write(ref _held, 1); _entered.TrySetResult();
                    if (!_release.Wait(TimeSpan.FromSeconds(8))) Volatile.Write(ref _timedOut, 1);
                    Volatile.Write(ref _held, 0);
                }
            });
        }
        internal static async Task<Large015IndexHold> Acquire(OfflineLibrary offline)
        {
            var hold = new Large015IndexHold(offline);
            try { await hold._entered.Task.WaitAsync(TimeSpan.FromSeconds(3)); return hold; }
            catch { hold.Release(); throw; }
        }
        internal void Release() => _release.Set();
        public void Dispose() { Release(); _holder.GetAwaiter().GetResult(); _release.Dispose(); }
    }

    private sealed class Large015Fixture : IDisposable
    {
        internal string Directory { get; }
        internal MainWindow Window { get; }
        internal OfflineLibrary Offline { get; }
        internal NativePlayer Player { get; }
        internal PreferencesStore Preferences { get; }
        internal TrackRow[] Rows { get; }
        internal ListView List => Control<ListView>(Window, "TracksList");
        private int _requests;
        internal int Requests => Volatile.Read(ref _requests);
        private Large015Fixture(string name, int count)
        {
            Directory = Path.Combine(_directory, "large015-" + name); System.IO.Directory.CreateDirectory(Directory);
            Rows = Enumerable.Range(0, count).Select(index => SortTrack("large-" + index,
                (index % 197 == 0 ? "фильтр-метка " : "Композиция ") + (count - index).ToString("D5"), "Исполнитель", "Альбом", (count - index) * 1000L)).ToArray();
            FakeHandler NoNetwork() => new((_, _) => { Interlocked.Increment(ref _requests); throw new InvalidOperationException("Large collection fixture attempted network access."); });
            var music = Path.Combine(Directory, "music"); System.IO.Directory.CreateDirectory(music);
            var bytes = new byte[64]; var hash = Convert.ToHexString(SHA256.HashData(bytes));
            var items = Rows.Take(2).Select(row => new OfflineItem(row.Id, row.RequestId, row.Title, row.Artist, row.Album, row.DurationMs,
                row.Id + ".mp3", "mp3", 320, bytes.Length, hash, DateTimeOffset.UtcNow)).ToArray();
            foreach (var item in items) File.WriteAllBytes(Path.Combine(music, item.FileName), bytes);
            File.WriteAllText(Path.Combine(music, "library.index.json"), JsonSerializer.Serialize(items));
            Offline = new OfflineLibrary(music, new AudioBuffer(Path.Combine(Directory, "downloads"), NoNetwork()));
            Player = new NativePlayer(new AudioBuffer(Path.Combine(Directory, "audio"), NoNetwork()));
            Preferences = new PreferencesStore(Path.Combine(Directory, "preferences")); Preferences.Save(new PlayerPreferences { Volume = 0 });
            Window = new MainWindow(api: new MusicApi(NoNetwork()), store: new SessionStore(Path.Combine(Directory, "session")), restore: false,
                diagnosticsDirectory: Directory, offline: Offline, player: Player, preferences: Preferences)
            { Left = -16000, Top = 0, Width = 1280, Height = 860, ShowActivated = false, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual };
            Window.Show(); Large015Set(Window, "_session", FakeSession); Large015Set(Window, "_favoritesSnapshotReady", true);
            Control<FrameworkElement>(Window, "LoginPanel").Visibility = Visibility.Collapsed;
            SaField<List<TrackRow>>(Window, "_favoritesCache").AddRange(Rows);
            Control<ContentControl>(Window, "SettingsUpdates").Content = new Border();
        }
        internal static async Task<Large015Fixture> Create(string name, int count)
        {
            var f = new Large015Fixture(name, count);
            try
            {
                await f.Offline.RememberPlaylistAsync(new PlaylistDetails("local:favorites", "liked", "Мне нравится", "", 0, f.Rows), AudioQuality.Excellent, default);
                return f;
            }
            catch { f.Dispose(); throw; }
        }
        internal void OpenSettingsDraft()
        {
            SaClick(Control<Button>(Window, "SettingsButton"));
            Control<Slider>(Window, "CrossfadeSetting").Value = 7.5;
            Control<ComboBox>(Window, "SettingsDownloads").SelectedItem = 7;
            Window.UpdateLayout();
        }
        public void Dispose() { Window.Close(); Player.Dispose(); }
    }
}
