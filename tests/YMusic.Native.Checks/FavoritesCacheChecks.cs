using System.Collections.Concurrent;
using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Net;
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
    private static async Task CheckFavoritesCache()
    {
        await Run("FavoritesCache: 3900 cached rows render during held IDs and repeated refresh reuses metadata without Reset", FcWarm);
        await Run("FavoritesCache: composite album changes and reordered IDs fetch only missing metadata", FcChangedIds);
        await Run("FavoritesCache: stale and explicit refresh keep the old view until replacement metadata arrives", FcRevalidation);
        await Run("FavoritesCache: failed ID and metadata refresh preserve the complete visible and persisted snapshot", FcFailures);
        await Run("FavoritesCache: superseded refresh signout and late account cache reads cannot restore obsolete rows", FcRaces);
        await Run("FavoritesCache: confirmed unlike survives late cache ID and metadata responses", FcUnlike);
        await Run("FavoritesCache: cold 100-row IO stays off dispatcher while cooperative appends preserve queue and UI marks", FcColdWorker);
        await Run("FavoritesCache: replacement retains sort filter selection viewport and exact active queue occurrence", FcPresentation);
    }

    private static async Task FcWarm()
    {
        using var f = await FcFixture.Create("warm", 3900);
        var events = new List<NotifyCollectionChangedAction>(); f.Source.CollectionChanged += (_, e) => events.Add(e.Action);
        var gate = f.Http.HoldIds(); var clock = Stopwatch.StartNew();
        var refresh = f.Window.RefreshLibraryAsync(); await FcEntered(gate);
        f.Window.UpdateLayout(); await Dispatcher.Yield(DispatcherPriority.Render);
        Assert(f.Source.Count == 3900 && f.List.Items.Count == 3900 && f.Http.MetadataBatches.IsEmpty,
            "cached collection waited for remote IDs or fetched metadata before ID validation");
        Assert(f.List.ItemContainerGenerator.ContainerFromIndex(0) is ListViewItem, "cached rows did not render while ID response was held");
        var references = f.Source.ToArray(); var resetBefore = events.Count(action => action == NotifyCollectionChangedAction.Reset);
        Assert(resetBefore == 1, "warm cache did not publish the full snapshot with one Reset");
        var sentinel = false;
        await f.Window.Dispatcher.InvokeAsync(() => sentinel = true, DispatcherPriority.Background);
        Assert(sentinel && !gate.Release.Task.IsCompleted && !refresh.IsCompleted, "held ID response prevented unrelated dispatcher work");
        var cacheVisibleMs = clock.Elapsed.TotalMilliseconds;
        gate.Release.TrySetResult(); await FcComplete(refresh); await f.Saved();
        Assert(f.Source.SequenceEqual(references) && events.Count(action => action == NotifyCollectionChangedAction.Reset) == resetBefore,
            "unchanged IDs replaced cached row identities or reset the visible view");
        events.Clear(); await FcComplete(f.Window.RefreshLibraryAsync()); await f.Saved();
        Assert(f.Http.MetadataBatches.IsEmpty && events.Count == 0 && f.Source.SequenceEqual(references),
            "repeated unchanged refresh downloaded metadata or republished the view");
        f.Facts(new { rows = 3900, cacheVisibleMs, heldIdsDispatcherSentinel = sentinel, metadataRequests = 0,
            initialResets = resetBefore, repeatedNotifications = events.Count, f.Http.IdRequests });
    }

    private static async Task FcChangedIds()
    {
        var seed = new[] { FcRow(0, "11"), FcRow(0, "22"), FcRow(1, "33"), FcRow(2, "44") };
        using var f = await FcFixture.Create("changed-composite", seed);
        await FcComplete(f.Window.RefreshLibraryAsync());
        var changed = new[] { new LikedId("fc-1", "33"), new LikedId("fc-0", "22"), new LikedId("fc-0", "99"), new LikedId("fc-9", "10") };
        f.Http.Likes = changed;
        await FcComplete(f.Window.RefreshLibraryAsync()); await f.Saved();
        var requests = f.Http.MetadataBatches.ToArray();
        Assert(requests.Length == 1 && requests[0].SequenceEqual(["fc-0:99", "fc-9:10"]),
            "changed IDs fetched cached album contexts or omitted a missing composite reference");
        Assert(f.Source.Select(row => row.RequestId).SequenceEqual(changed.Select(id => id.RequestId))
            && f.Source.Select(row => row.Number).SequenceEqual(Enumerable.Range(1, changed.Length)),
            "reordered composite IDs lost service order or row numbering");
        Assert(f.Source[2].AlbumId == "99" && f.Source[2].Album == "Album 99", "new album context was inferred from an old track instance");
        var persisted = f.Store.Load(f.Uid)!;
        Assert(persisted.Tracks.Select(row => row.RequestId).SequenceEqual(changed.Select(id => id.RequestId)), "persisted cache retained removed entries or old order");
        f.Facts(new { requested = requests, result = f.Source.Select(row => row.RequestId).ToArray(), persisted = persisted.Tracks.Count });
        await FcReorderedMarks();
    }

    private static async Task FcReorderedMarks()
    {
        using var f = await FcFixture.Create("reordered-offline-marks", 300);
        var rows = Enumerable.Range(0, 300).Select(index => FcRow(index)).ToArray();
        Directory.CreateDirectory(f.Offline.DirectoryPath);
        var wave = File.ReadAllBytes(WriteWave("favorites-cache-offline-marks.wav", .02));
        var hash = Convert.ToHexString(SHA256.HashData(wave));
        var items = rows.Take(100).Select(row => new OfflineItem(row.Id, row.RequestId, row.Title, row.Artist, row.Album,
            row.DurationMs, row.Id + ".wav", "wav", 0, wave.Length, hash, DateTimeOffset.UtcNow,
            Artists: row.Artists, AlbumId: row.AlbumId)).ToArray();
        foreach (var item in items) File.WriteAllBytes(Path.Combine(f.Offline.DirectoryPath, item.FileName), wave);
        File.WriteAllText(Path.Combine(f.Offline.DirectoryPath, "library.index.json"), JsonSerializer.Serialize(items));
        Large015IndexHold? hold = null;
        // Begin outside a playlist so its empty synchronous status does not take
        // the fixture lock before the controlled cache-loading boundary.
        FcSet(f.Window, "_section", "queue");
        f.Window.FavoritesCacheReader = async (uid, _) =>
        {
            var snapshot = f.Store.Load(uid);
            hold = await Large015IndexHold.Acquire(f.Offline);
            FcSet(f.Window, "_section", "favorites");
            return snapshot;
        };
        f.Http.Likes = rows.Reverse().Select(FcLiked).ToArray();
        var gate = f.Http.HoldIds();
        try
        {
            var refresh = f.Window.RefreshLibraryAsync(); await FcEntered(gate);
            var activeHold = hold ?? throw new InvalidOperationException("Controlled offline index hold was not established.");
            var initialTask = SaField<Task?>(f.Window, "_offlineMarksTask"); var original = f.Source.ToArray();
            Assert(activeHold.IsHeld && !activeHold.TimedOut && initialTask is { IsCompleted: false }
                && original.Length == 300 && original.All(row => !row.IsDownloaded), "cached offline scan was not held before renumbering");
            gate.Release.TrySetResult(); await FcComplete(refresh);
            var publishedTask = SaField<Task?>(f.Window, "_offlineMarksTask");
            Assert(activeHold.IsHeld && !activeHold.TimedOut && publishedTask is { IsCompleted: false }
                && !ReferenceEquals(initialTask, publishedTask) && !f.Source.Any(row => original.Contains(row))
                && f.Source.All(row => !row.IsDownloaded), "renumbered references did not schedule their own pending offline scan");
            activeHold.Release(); await FcComplete(publishedTask!); await FcComplete(initialTask!); await f.Saved();
            Assert(f.Http.MetadataBatches.IsEmpty && f.Source.All(row => row.IsDownloaded == (int.Parse(row.Id[3..]) < 100))
                && f.Source.Count(row => row.IsDownloaded) == 100 && !activeHold.TimedOut,
                "late offline scan updated old references or left current cached clones with stale badges");
            f.Facts(new { cachedRows = 300, actualWavFiles = 100, metadataRequests = 0,
                initialScanHeld = true, replacementQueuedOwnScan = true, currentDownloaded = f.Source.Count(row => row.IsDownloaded) });
        }
        finally { gate.Release.TrySetResult(); hold?.Dispose(); }
    }

    private static async Task FcRevalidation()
    {
        foreach (var mode in new[] { "stale", "explicit" })
        {
            using var f = await FcFixture.Create("revalidate-" + mode, 8, mode == "stale" ? DateTimeOffset.UtcNow.AddHours(-30) : DateTimeOffset.UtcNow);
            var gate = f.Http.HoldMetadata(); var refresh = f.Window.RefreshLibraryAsync(refreshMetadata: mode == "explicit");
            await FcEntered(gate); var before = f.Source.ToArray();
            Assert(before.Length == 8 && before.All(row => row.Title.Contains("Cached", StringComparison.Ordinal)), "metadata revalidation cleared or prematurely replaced cached rows");
            var sentinel = false; await f.Window.Dispatcher.InvokeAsync(() => sentinel = true, DispatcherPriority.Input);
            Assert(sentinel && f.Source.SequenceEqual(before) && !refresh.IsCompleted, "held revalidation blocked input or changed the old view");
            gate.Release.TrySetResult(); await FcComplete(refresh); await f.Saved();
            Assert(f.Http.MetadataBatches.Count == 1 && f.Source.All(row => row.Title.Contains("Live", StringComparison.Ordinal))
                && f.Store.Load(f.Uid)!.SavedUtc > DateTimeOffset.UtcNow.AddMinutes(-5), "required metadata refresh did not replace and persist fresh metadata");
            f.Facts(new { mode, rows = f.Source.Count, metadataRequests = f.Http.MetadataBatches.Count, oldViewRetainedWhileHeld = true });
        }
    }

    private static async Task FcFailures()
    {
        foreach (var phase in new[] { "ids", "metadata" })
        {
            using var f = await FcFixture.Create("failure-" + phase, 8);
            var beforeHash = f.CacheHash(); var gate = phase == "ids" ? f.Http.HoldIds() : f.Http.HoldMetadata();
            f.Http.FailIds = phase == "ids"; f.Http.FailMetadata = phase == "metadata";
            var refresh = f.Window.RefreshLibraryAsync(refreshMetadata: true); await FcEntered(gate);
            var rows = f.Source.ToArray(); gate.Release.TrySetResult(); await FcComplete(refresh); await f.Saved();
            Assert(rows.Length == 8 && f.Source.SequenceEqual(rows) && f.CacheHash() == beforeHash
                && SaField<bool>(f.Window, "_favoritesCacheComplete"), "failed refresh destroyed the complete cached snapshot");
            Assert(SaField<Diagnostics>(f.Window, "_diagnostics").Stage == "library_partial_or_error", "failed refresh was reported as complete");
            f.Facts(new { phase, preservedRows = f.Source.Count, persistedHashUnchanged = true });
        }
        using (var f = await FcFixture.Create("optional-offline-error", []))
        {
            f.Http.Likes = Enumerable.Range(0, 100).Select(index => FcLiked(FcRow(index))).ToArray();
            var ioThread = Environment.CurrentManagedThreadId; var ui = ioThread;
            f.Window.FavoritesBeforeOfflineRead = _ =>
            { ioThread = Environment.CurrentManagedThreadId; throw new IOException("Fixture optional offline index failure"); };
            await FcComplete(f.Window.RefreshLibraryAsync()); await f.Saved();
            Assert(ioThread != ui && f.Source.Count == 100 && SaField<bool>(f.Window, "_favoritesCacheComplete")
                && f.Store.Load(f.Uid)!.Tracks.Count == 100 && SaField<Diagnostics>(f.Window, "_diagnostics").Stage == "library_ready",
                "optional worker IO failure discarded successful online favorites metadata");
            f.Facts(new { workerIOException = true, ioThread, ui, onlineTracksVisibleAndPersisted = 100 });
        }
        using (var f = await FcFixture.Create("malformed-cache-fallback", []))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(f.Store.FilePath(f.Uid))!);
            File.WriteAllText(f.Store.FilePath(f.Uid), "{ malformed fixture cache");
            f.Http.Likes = Enumerable.Range(0, 7).Select(index => FcLiked(FcRow(index))).ToArray();
            await FcComplete(f.Window.RefreshLibraryAsync()); await f.Saved();
            Assert(f.Source.Count == 7 && f.Http.MetadataBatches.Count == 1 && f.Store.Load(f.Uid)!.Tracks.Count == 7,
                "unreadable optional metadata cache prevented network recovery or remained corrupt after completion");
            f.Facts(new { malformedCache = true, recoveredRows = 7, metadataRequests = 1 });
        }
    }

    private static async Task FcRaces()
    {
        foreach (var mode in new[] { "supersede", "signout-ids", "signout-cache", "account-cache" })
        {
            using var f = await FcFixture.Create("race-" + mode, 12);
            var cacheRead = mode.EndsWith("cache", StringComparison.Ordinal);
            var gate = cacheRead ? f.Gate() : f.Http.HoldIds();
            if (cacheRead)
            {
                f.Window.FavoritesCacheReader = async (uid, _) =>
                {
                    var snapshot = f.Store.Load(uid);
                    if (uid == "12345") { gate.Entered.TrySetResult(); await gate.Release.Task; }
                    return snapshot;
                };
            }
            var old = f.Window.RefreshLibraryAsync(); await FcEntered(gate);
            if (mode == "supersede")
            {
                var next = new[] { new LikedId("fc-99", "91") }; f.Http.Likes = next;
                await FcComplete(f.Window.RefreshLibraryAsync());
                gate.Release.TrySetResult(); await FcComplete(old); await f.Saved();
                Assert(f.Source.Select(row => row.RequestId).SequenceEqual(["fc-99:91"]), "superseded ID response restored obsolete rows");
            }
            else
            {
                f.Window.SignOut();
                if (mode == "account-cache")
                {
                    var nextRows = new[] { FcRow(77), FcRow(78) };
                    await f.Store.SaveInBackgroundAsync(FavoritesCacheStore.Capture("67890", nextRows, DateTimeOffset.UtcNow));
                    f.Account("67890"); f.Http.Likes = nextRows.Select(FcLiked).ToArray();
                    await FcComplete(f.Window.RefreshLibraryAsync()); await f.Saved();
                }
                gate.Release.TrySetResult(); await FcComplete(old);
                if (mode == "account-cache")
                    Assert(f.Source.Select(row => row.Id).SequenceEqual(["fc-77", "fc-78"])
                        && SaField<string?>(f.Window, "_favoritesAccountUid") == "67890"
                        && SaField<HashSet<string>>(f.Window, "_favoriteTrackIds").SetEquals(["fc-77", "fc-78"]),
                        "late old-account cache read leaked rows or like IDs into the new account");
                else Assert(f.Source.Count == 0 && SaField<List<TrackRow>>(f.Window, "_favoritesCache").Count == 0
                    && SaField<Account?>(f.Window, "_account") is null, "late cache or ID response restored signed-out state");
            }
            f.Facts(new { mode, visibleIds = f.Source.Select(row => row.Id).ToArray(), account = SaField<string?>(f.Window, "_favoritesAccountUid") });
        }
    }

    private static async Task FcUnlike()
    {
        foreach (var phase in new[] { "cache", "ids", "metadata" })
        {
            using var f = await FcFixture.Create("unlike-" + phase, 5);
            var removed = FcRow(1); var gate = phase == "cache" ? f.Gate() : phase == "ids" ? f.Http.HoldIds() : f.Http.HoldMetadata();
            if (phase == "cache")
                f.Window.FavoritesCacheReader = async (uid, _) =>
                { var snapshot = f.Store.Load(uid); gate.Entered.TrySetResult(); await gate.Release.Task; return snapshot; };
            var refresh = f.Window.RefreshLibraryAsync(refreshMetadata: phase == "metadata"); await FcEntered(gate);
            // Exercise the confirmed mutation boundary; real POST/readback is covered by Unavailable likes fixtures.
            f.Http.Likes = f.Http.Likes.Where(id => id.Id != removed.Id).ToArray();
            SaInvoke(f.Window, "ApplyConfirmedTrackLike", removed, false, false);
            gate.Release.TrySetResult(); await FcComplete(refresh); await f.Saved();
            Assert(f.Source.Count == 4 && f.Source.All(row => row.Id != removed.Id)
                && !SaField<HashSet<string>>(f.Window, "_favoriteTrackIds").Contains(removed.Id)
                && f.Store.Load(f.Uid)!.Tracks.All(row => row.Id != removed.Id), "late " + phase + " restored a confirmed unlike");
            f.Facts(new { phase, removedId = removed.Id, surviving = f.Source.Count, resurrected = false });
        }
    }

    private static async Task FcColdWorker()
    {
        using var f = await FcFixture.Create("cold-worker", []);
        f.Http.Likes = Enumerable.Range(0, 100).Select(index => FcLiked(FcRow(index))).ToArray();
        var gate = f.Gate(); var ui = Environment.CurrentManagedThreadId; var workerThread = ui;
        f.Window.FavoritesBeforeOfflineRead = async token =>
        { workerThread = Environment.CurrentManagedThreadId; gate.Entered.TrySetResult(); await gate.Release.Task.WaitAsync(token).ConfigureAwait(false); };
        var queue = SaField<PlaybackQueue>(f.Window, "_playbackQueue"); var duplicate = FcRow(9000);
        queue.SetItems([duplicate, FcRow(9001), duplicate]); queue.SelectAt(2); FcSet(f.Window, "_current", duplicate);
        var version = queue.Version; var occurrence = queue.CurrentOccurrenceToken; var queueRows = queue.Items.ToArray();
        var adds = 0; var resets = 0; var wrongThread = false; var propertyChanges = 0; var inputAt = -1;
        f.Source.CollectionChanged += (_, args) =>
        {
            wrongThread |= Environment.CurrentManagedThreadId != ui;
            if (args.Action == NotifyCollectionChangedAction.Reset) resets++;
            if (args.Action != NotifyCollectionChangedAction.Add) return;
            foreach (TrackRow row in args.NewItems!)
            {
                adds++;
                row.PropertyChanged += (_, _) => { propertyChanges++; wrongThread |= Environment.CurrentManagedThreadId != ui; };
                if (adds == 1) _ = f.Window.Dispatcher.InvokeAsync(() => inputAt = adds, DispatcherPriority.Input);
            }
        };
        var refresh = f.Window.RefreshLibraryAsync(); await FcEntered(gate);
        Assert(workerThread != ui && !refresh.IsCompleted && f.Source.Count == 0, "100-row offline read ran on dispatcher or published before its worker boundary");
        var input = false; var background = false;
        await f.Window.Dispatcher.InvokeAsync(() => input = true, DispatcherPriority.Input);
        await f.Window.Dispatcher.InvokeAsync(() => background = true, DispatcherPriority.Background);
        Assert(input && background && !gate.Release.Task.IsCompleted, "held offline worker prevented dispatcher input/background work");
        gate.Release.TrySetResult(); await FcComplete(refresh); await f.Saved();
        Assert(adds == 100 && resets == 0 && inputAt is > 0 and < 100 && !wrongThread,
            "cold loading reset the collection, blocked all rows in one dispatcher turn or published outside UI");
        Assert(queue.Version == version && queue.CurrentOccurrenceToken == occurrence && queue.Items.SequenceEqual(queueRows)
            && ReferenceEquals(SaField<TrackRow?>(f.Window, "_current"), duplicate) && !f.Player.HasSource, "cold loading changed the playing queue or attempted playback");
        f.Window.FavoritesBeforeOfflineRead = null;
        f.Http.Likes = f.Http.Likes.Skip(1).ToArray();
        await FcComplete(f.Window.RefreshLibraryAsync()); await f.Saved();
        Assert(propertyChanges > 0 && !wrongThread, "subsequent ID revalidation did not publish new rows' like changes on UI");
        f.Facts(new { coldRows = 100, workerThread, uiThread = ui, input, background, inputAt, initialAddNotifications = adds,
            initialResets = 0, observedPropertyChanges = propertyChanges, queueOccurrencePreserved = true });
    }

    private static async Task FcPresentation()
    {
        using var f = await FcFixture.Create("presentation", 420);
        await FcComplete(f.Window.RefreshLibraryAsync()); await f.Saved();
        SortChoose(f.Window, ListSortField.Title);
        Control<TextBox>(f.Window, "FilterBox").Text = "match";
        await FcUntil(() => f.List.Items.Count == 140);
        var selected = (TrackRow)f.List.Items[60]; f.List.SelectedItem = selected;
        var viewer = SaVisuals<ScrollViewer>(f.List).First();
        SmoothScroll.CancelAnimation(viewer); viewer.ScrollToVerticalOffset(1200); await Task.Delay(60); f.Window.UpdateLayout();
        var offset = viewer.VerticalOffset;
        Assert(offset > 100, "presentation fixture did not establish a scrolled filtered viewport");
        var queue = SaField<PlaybackQueue>(f.Window, "_playbackQueue"); var duplicate = f.Source[3];
        queue.SetItems([duplicate, f.Source[4], duplicate]); queue.SelectAt(2); FcSet(f.Window, "_current", duplicate);
        var version = queue.Version; var occurrence = queue.CurrentOccurrenceToken; var queueRows = queue.Items.ToArray();
        var gate = f.Http.HoldMetadata(); var refresh = f.Window.RefreshLibraryAsync(refreshMetadata: true);
        await FcEntered(gate);
        Assert(ReferenceEquals(f.List.SelectedItem, selected) && Math.Abs(viewer.VerticalOffset - offset) <= 1,
            "held revalidation changed selection or viewport before a complete snapshot");
        gate.Release.TrySetResult(); await FcComplete(refresh); await f.Saved();
        f.Window.UpdateLayout(); await Dispatcher.Yield(DispatcherPriority.Loaded); f.Window.UpdateLayout();
        var visible = f.List.Items.Cast<TrackRow>().ToArray();
        Assert(Control<TextBox>(f.Window, "FilterBox").Text == "match" && visible.Length == 140
            && visible.All(row => row.Matches("match")) && visible.SequenceEqual(visible.OrderBy(row => row.Title, StringComparer.CurrentCultureIgnoreCase)),
            "snapshot replacement changed the active filter or stable title sort");
        Assert(f.List.SelectedItem is TrackRow after && after.RequestId == selected.RequestId
            && Math.Abs(viewer.VerticalOffset - offset) <= 1, "snapshot replacement lost selected composite ID or viewport offset");
        Assert(queue.Version == version && queue.CurrentOccurrenceToken == occurrence && queue.Items.SequenceEqual(queueRows)
            && ReferenceEquals(SaField<TrackRow?>(f.Window, "_current"), duplicate) && !f.Player.HasSource,
            "view replacement changed the exact duplicate queue occurrence or attempted playback");
        f.Facts(new { totalRows = 420, filteredRows = visible.Length, selected.RequestId, offsetBefore = offset,
            offsetAfter = viewer.VerticalOffset, queueVersion = version, queueOccurrence = occurrence, playbackStarted = f.Player.HasSource });
    }

    private static TrackRow FcRow(int index, string album = "91") => new()
    {
        Number = index + 1, Id = "fc-" + index, RequestId = "fc-" + index + ":" + album, AlbumId = album,
        Title = (index % 3 == 0 ? "match " : "") + "Cached " + index.ToString("D6"), Artist = "Fixture artist",
        Artists = [new("fixture-artist", "Fixture artist")], Album = "Album " + album, DurationMs = 1000L * (index + 1), Available = true
    };
    private static LikedId FcLiked(TrackRow row) => new(row.Id, row.AlbumId);
    private static void FcSet(MainWindow window, string field, object? value)
        => typeof(MainWindow).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, value);
    private static Task FcEntered(FcGate gate) => gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
    private static Task FcComplete(Task task) => task.WaitAsync(TimeSpan.FromSeconds(20));
    private static async Task FcUntil(Func<bool> ready)
    { var clock = Stopwatch.StartNew(); while (!ready()) { if (clock.Elapsed > TimeSpan.FromSeconds(10)) throw new InvalidOperationException("Expected favorites fixture state did not arrive."); await Task.Delay(10); } }

    private sealed class FcGate
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class FcFixture : IDisposable
    {
        internal string DirectoryPath { get; }
        internal string Uid { get; private set; } = "12345";
        internal FavoritesCacheStore Store { get; }
        internal MainWindow Window { get; }
        internal NativePlayer Player { get; }
        internal OfflineLibrary Offline { get; }
        internal FcHttp Http { get; }
        internal TrackRows Source => SaField<TrackRows>(Window, "_tracks");
        internal ListView List => Control<ListView>(Window, "TracksList");
        private readonly ConcurrentBag<FcGate> _gates = [];
        private FcFixture(string name)
        {
            DirectoryPath = Path.Combine(_directory, "favorites-cache-" + name); Directory.CreateDirectory(DirectoryPath);
            Store = new FavoritesCacheStore(DirectoryPath); Http = new FcHttp(Gate);
            FakeHandler NoAudio() => new((_, _) => throw new InvalidOperationException("Favorites fixture attempted audio or outside network"));
            Offline = new OfflineLibrary(Path.Combine(DirectoryPath, "offline"), new AudioBuffer(Path.Combine(DirectoryPath, "downloads"), NoAudio()));
            Player = new NativePlayer(new AudioBuffer(Path.Combine(DirectoryPath, "audio"), NoAudio()));
            var preferences = new PreferencesStore(Path.Combine(DirectoryPath, "preferences"));
            preferences.Save(new PlayerPreferences { Volume = 0, CrossfadeSeconds = 0, Theme = "Dark" });
            Window = new MainWindow(api: new MusicApi(Http), store: new SessionStore(Path.Combine(DirectoryPath, "session")), restore: false,
                diagnosticsDirectory: DirectoryPath, offline: Offline, player: Player, preferences: preferences)
            { ShowActivated = false, ShowInTaskbar = false, Width = 1280, Height = 860, Left = -16000, Top = 0, WindowStartupLocation = WindowStartupLocation.Manual };
            FcSet(Window, "_favoritesStore", Store); Account(Uid); Window.Show();
            Control<FrameworkElement>(Window, "LoginPanel").Visibility = Visibility.Collapsed;
        }
        internal FcGate Gate() { var gate = new FcGate(); _gates.Add(gate); return gate; }
        internal void Account(string uid)
        {
            Uid = uid;
            var accountCts = SaField<CancellationTokenSource>(Window, "_accountCts");
            if (accountCts.IsCancellationRequested)
            { accountCts.Dispose(); FcSet(Window, "_accountCts", CancellationTokenSource.CreateLinkedTokenSource(SaField<CancellationTokenSource>(Window, "_lifetime").Token)); }
            FcSet(Window, "_session", FakeSession); FcSet(Window, "_account", new Account(uid, "Favorites fixture", true));
            Control<FrameworkElement>(Window, "LoginPanel").Visibility = Visibility.Collapsed;
        }
        internal static Task<FcFixture> Create(string name, int count, DateTimeOffset? savedUtc = null)
            => Create(name, Enumerable.Range(0, count).Select(index => FcRow(index)).ToArray(), savedUtc);
        internal static async Task<FcFixture> Create(string name, TrackRow[] rows, DateTimeOffset? savedUtc = null)
        {
            var fixture = new FcFixture(name);
            try
            {
                fixture.Http.Likes = rows.Select(FcLiked).ToArray();
                if (rows.Length > 0) await fixture.Store.SaveInBackgroundAsync(FavoritesCacheStore.Capture(fixture.Uid, rows, savedUtc ?? DateTimeOffset.UtcNow));
                return fixture;
            }
            catch { fixture.Dispose(); throw; }
        }
        internal async Task Saved()
        {
            if (SaField<Task?>(Window, "_favoritesCacheSaveTask") is { } save) await FcComplete(save);
            await FcComplete(Task.WhenAll(new[] { SaField<Task?>(Window, "_offlineMarksTask"), SaField<Task?>(Window, "_playlistStatusTask") }.OfType<Task>()));
        }
        internal string CacheHash() => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Store.FilePath(Uid))));
        internal void Facts(object value) => File.WriteAllText(Path.Combine(DirectoryPath, "facts.json"), JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));
        public void Dispose() { foreach (var gate in _gates) gate.Release.TrySetResult(); Window.Close(); Player.Dispose(); Http.Dispose(); }
    }

    private sealed class FcHttp(Func<FcGate> gateFactory) : HttpMessageHandler
    {
        private LikedId[] _likes = [];
        internal LikedId[] Likes { get => Volatile.Read(ref _likes); set => Volatile.Write(ref _likes, value.ToArray()); }
        internal ConcurrentQueue<string[]> MetadataBatches { get; } = [];
        internal bool FailIds, FailMetadata;
        internal int IdRequests;
        private FcGate? _idsGate, _metadataGate;
        internal FcGate HoldIds() => _idsGate = gateFactory();
        internal FcGate HoldMetadata() => _metadataGate = gateFactory();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Assert(request.RequestUri!.Host == "api.music.yandex.net", "favorites fixture left its in-process API");
            var path = request.RequestUri.AbsolutePath;
            if (path == "/account/status") return Json(Profile);
            if (path.StartsWith("/users/", StringComparison.Ordinal) && path.EndsWith("/likes/tracks", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref IdRequests); var failure = FailIds;
                var json = JsonSerializer.Serialize(new { result = new { library = new { tracks = Likes.Select(id => new { id = id.Id, albumId = id.AlbumId }).ToArray() } } });
                if (Interlocked.Exchange(ref _idsGate, null) is { } gate) { gate.Entered.TrySetResult(); await gate.Release.Task; }
                return failure ? new(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("Fixture ID failure") } : Json(json);
            }
            if (path == "/tracks")
            {
                var body = Uri.UnescapeDataString(await request.Content!.ReadAsStringAsync(token));
                var ids = body.Split('&').Single(part => part.StartsWith("track-ids=", StringComparison.Ordinal))[10..].Split(',');
                MetadataBatches.Enqueue(ids); var failure = FailMetadata;
                var json = JsonSerializer.Serialize(new { result = ids.Select(reference =>
                {
                    var parts = reference.Split(':'); var index = int.Parse(parts[0][3..]); var album = parts.Length > 1 ? parts[1] : "91";
                    return new { id = parts[0], title = (index % 3 == 0 ? "match " : "") + "Live " + index.ToString("D6"), available = true, durationMs = 1000L * (index + 1),
                        artists = new[] { new { id = "fixture-artist", name = "Fixture artist" } }, albums = new[] { new { id = album, title = "Album " + album } } };
                }).ToArray() });
                if (Interlocked.Exchange(ref _metadataGate, null) is { } gate) { gate.Entered.TrySetResult(); await gate.Release.Task; }
                return failure ? new(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("Fixture metadata failure") } : Json(json);
            }
            throw new InvalidOperationException("Unexpected favorites fixture route: " + path);
        }
    }
}
