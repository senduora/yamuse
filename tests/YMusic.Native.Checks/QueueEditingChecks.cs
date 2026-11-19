using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using YMusic.Native;

internal static partial class Program
{
    private static async Task CheckQueueEditing()
    {
        TrackRow Row(string id) => new() { Id = id, RequestId = id, Title = "Трек " + id, Artist = "Исполнитель", Available = true, DurationMs = 180000 };
        await Run("QueueEditing: batch move preserves duplicate occurrence current history and shuffle next", () =>
        {
            var shared = Row("same"); var q = new PlaybackQueue(new Random(7));
            q.SetItems([Row("a"), shared, shared, Row("b"), Row("c")]); q.SelectAt(2); q.Shuffle = true;
            var token = q.CurrentOccurrenceToken; var next = q.PeekNextOccurrenceToken(); var history = q.History.ToArray();
            q.MoveMany([1, 2], 5);
            Assert(q.CurrentIndex == 4 && q.CurrentOccurrenceToken == token && q.PeekNextOccurrenceToken() == next, "Move lost the exact duplicate or reseeded shuffle");
            Assert(q.Items.Select(t => t.Id).SequenceEqual(new[] { "a", "b", "c", "same", "same" }) && q.History.SequenceEqual(history), "Move changed internal order/history");
            var version = q.Version; q.MoveMany([3, 4], 4); Assert(q.Version == version, "Drop within the selected block must be a no-op");
            return Task.CompletedTask;
        });
        await Run("QueueEditing: remove selected current and next atomically undo never rewinds playback", () =>
        {
            var shared = Row("same"); var q = new PlaybackQueue(); q.SetItems([Row("a"), shared, shared, Row("z")]); q.SelectAt(1);
            var removedToken = q.CurrentOccurrenceToken; var next = q.RemoveMany([1, 2]); var replacement = q.CurrentOccurrenceToken;
            Assert(next?.Id == "z" && q.Items.Count == 2 && q.CanUndoRemoval, "Batch removal selected another removed entry");
            Assert(q.UndoRemoval() && q.Items.Count == 4 && q.CurrentOccurrenceToken == replacement && q.CurrentIndex == 3, "Undo rewound current or lost duplicate entries");
            Assert(q.OccurrenceTokenAt(1) == removedToken && !q.CanUndoRemoval && !q.UndoRemoval(), "Undo did not restore exact original token once");
            return Task.CompletedTask;
        });
        await Run("QueueEditing: undo preserves newly enqueued tracks and fresh source invalidates undo", () =>
        {
            var q = new PlaybackQueue(); q.SetItems([Row("a"), Row("b"), Row("c")]); q.RemoveMany([0, 1]); q.Enqueue(Row("new")); q.UndoRemoval();
            Assert(q.Items.Select(t => t.Id).SequenceEqual(new[] { "a", "b", "c", "new" }), "Undo discarded or reordered new items");
            q.RemoveMany([0]); q.SetItems([Row("fresh")]); Assert(!q.CanUndoRemoval && !q.UndoRemoval(), "Undo crossed a queue replacement");
            q.RemoveMany([0]); Assert(q.Current is null && q.Items.Count == 0, "Full removal did not clear current");
            q.UndoRemoval(); Assert(q.Items.Count == 1 && q.Current is null, "Undo full removal must not auto-select/play");
            return Task.CompletedTask;
        });
        await Run("QueueEditing: invalid edit leaves order and version untouched", () =>
        {
            var q = new PlaybackQueue(); q.SetItems([Row("a"), Row("b")]); var version = q.Version;
            try { q.MoveMany([0, 9], 1); throw new Exception("Expected bounds rejection"); } catch (ArgumentOutOfRangeException) { }
            try { q.RemoveMany([-1]); throw new Exception("Expected bounds rejection"); } catch (ArgumentOutOfRangeException) { }
            Assert(q.Version == version && q.Items.Count == 2, "Invalid edit partially changed state"); return Task.CompletedTask;
        });
        await Run("QueueEditing: recommendation append keeps a previously played occurrence moved into the pending order", () =>
        {
            var queue = new PlaybackQueue();
            var rows = Enumerable.Range(0, 23).Select(index => Row("moved-" + index)).ToArray();
            queue.SetItems(rows);
            var token = queue.OccurrenceTokenAt(0);
            for (var i = 1; i <= 21; i++) Assert(queue.Next() == rows[i], "the fixture skipped a history step");
            queue.MoveMany([0], 22);
            Assert(queue.PeekNextOccurrenceToken() == token, "the fixture did not move a played row into the pending order");
            queue.Append([Row("recommendation")]);
            Assert(queue.PeekNextOccurrenceToken() == token && queue.Next() == rows[0] && queue.Next() == rows[22],
                "appending recommendations discarded the manually placed occurrence");
            return Task.CompletedTask;
        });
        await Run("QueueEditing: shuffle admits a previously unavailable occurrence once after it is downloaded", () =>
        {
            var queue = new PlaybackQueue(new Random(19));
            var pending = new TrackRow { Id = "pending-local", RequestId = "pending-local", Title = "Fixture pending" };
            var ready = Row("always-ready");
            queue.SetItems([Row("current"), pending, ready]);
            queue.Shuffle = true;
            Assert(queue.Next() == ready && !queue.CanNext, "shuffle treated an unavailable occurrence as playable");
            pending.DownloadedPath = Path.Combine(_directory, "mark-only-fixture.mp3");
            Assert(queue.PeekNext() == pending && queue.CanNext && queue.Next() == pending && queue.Next() is null,
                "shuffle skipped or repeated the newly playable occurrence");
            return Task.CompletedTask;
        });
        await Run("QueueEditing: adding a row already in queue creates an independent accessible occurrence", () =>
        {
            using var fixture = new SaFixture("queue-distinct-accessible-occurrence");
            var window = fixture.Window;
            var queue = EpField<PlaybackQueue>(window, "_playbackQueue");
            var track = Row("repeat-accessible");
            queue.SetItems([track]);
            SaInvoke(window, "ShowQueueCore", false);
            SaInvoke(window, "EnqueueActionTrack", track, false);
            var list = Control<ListView>(window, "TracksList");
            window.UpdateLayout();
            Assert(queue.Items.Count == 2 && !ReferenceEquals(queue.Items[0], queue.Items[1])
                && queue.Items[0].RequestId == queue.Items[1].RequestId, "adding in queue reused the presentation object");
            var peer = System.Windows.Automation.Peers.UIElementAutomationPeer.CreatePeerForElement(list);
            Assert(peer?.GetChildren()?.OfType<System.Windows.Automation.Peers.ItemAutomationPeer>().Count() == 2,
                "the WPF list exposed only one automation peer for two occurrences");
            return Task.CompletedTask;
        });
        await Run("QueueEditing: rejected replacement retains undo and shuffle removal remains reversible", () =>
        {
            var q = new PlaybackQueue(new Random(2)); q.SetItems([Row("a"), Row("b"), Row("c"), Row("d")]); q.Shuffle = true;
            var token = q.CurrentOccurrenceToken; q.RemoveMany([1, 2]);
            try { q.RestoreItems([Row("x")], 8, false, RepeatMode.Off); throw new Exception("Expected bounds rejection"); } catch (ArgumentOutOfRangeException) { }
            Assert(q.CanUndoRemoval && q.UndoRemoval() && q.CurrentOccurrenceToken == token, "Rejected replacement destroyed undo/current");
            var visited = new HashSet<long> { token!.Value };
            while (q.Next() is not null) Assert(visited.Add(q.CurrentOccurrenceToken!.Value), "Undo duplicated a shuffle occurrence");
            Assert(visited.Count == 4, "Undo lost unplayed shuffled occurrences");
            return Task.CompletedTask;
        });
        await Run("QueueEditing: undo restores PlayNext priority between pending and newer requests", () =>
        {
            var queue = new PlaybackQueue(new Random(17));
            queue.SetItems([Row("current"), Row("shuffled")]);
            queue.Shuffle = true;
            var tail = Row("priority-tail"); var keep = Row("priority-keep");
            var head = Row("priority-head"); var consumed = Row("priority-consumed");
            foreach (var track in new[] { tail, keep, head, consumed }) queue.Enqueue(track, playNext: true);
            queue.RemoveMany([2, 4]);
            Assert(ReferenceEquals(queue.Next(), consumed), "removal disturbed the surviving priority order");
            var fresh = Row("priority-fresh");
            queue.Enqueue(fresh, playNext: true);
            var current = queue.CurrentOccurrenceToken;
            Assert(queue.UndoRemoval() && queue.CurrentOccurrenceToken == current, "undo changed the active occurrence");
            foreach (var track in new[] { fresh, head, keep, tail })
            {
                Assert(ReferenceEquals(queue.PeekNext(), track), "undo lost explicit priority or reordered pending requests");
                Assert(ReferenceEquals(queue.Next(), track), "advance disagreed with the restored priority");
            }
            Assert(queue.Next()?.Id == "shuffled" && queue.Next() is null, "undo replayed an already consumed priority");
            return Task.CompletedTask;
        });
        await Run("QueueEditing: Play uses visible fallback only when nothing is selected", async () =>
        {
            var path = WriteWave("queue-play-fallback.wav", 30);
            foreach (var selectUnavailable in new[] { false, true })
            {
                using var fixture = new SaFixture(selectUnavailable ? "queue-play-unavailable" : "queue-play-unselected");
                var window = fixture.Window;
                var unavailable = new TrackRow { Id = "unavailable", RequestId = "unavailable", Title = "А" };
                var chosen = new TrackRow { Id = "selected", RequestId = "selected", Title = "Б", Available = true, LocalPath = path, DurationMs = 30000 };
                var later = new TrackRow { Id = "later", RequestId = "later", Title = "В", Available = true, LocalPath = path, DurationMs = 30000 };
                var queue = EpField<PlaybackQueue>(window, "_playbackQueue");
                queue.RestoreItems([later, chosen, unavailable, chosen], -1, false, RepeatMode.Off);
                var token = queue.OccurrenceTokenAt(1);
                SaInvoke(window, "ShowQueueCore", false);
                SortChoose(window, ListSortField.Title);
                var list = Control<ListView>(window, "TracksList");
                list.SelectedIndex = selectUnavailable ? 0 : -1;
                Assert(Control<Button>(window, "PlaySelectionButton").IsEnabled == !selectUnavailable,
                    "Listen did not reflect the explicit row's availability");
                await Call(window, "PlaySelectedAsync");
                if (selectUnavailable)
                {
                    Assert(!fixture.Player.HasSource && queue.CurrentOccurrenceToken is null, "Listen replaced an explicitly unavailable selection");
                    continue;
                }
                Assert(fixture.Player.IsPlaying && queue.CurrentOccurrenceToken == token && ReferenceEquals(queue.Current, chosen),
                    "play ignored the sorted available row or opened another occurrence");
            }
        });
        await Run("QueueEditing: mouse press owns its selection and a delayed click focuses the confirmed occurrence", QueueEditingPressOwnership);
        await Run("QueueEditing: passive refill preserves a delayed click only on the same surviving occurrence", QueueEditingPassiveClick);
        await Run("QueueEditing: a superseded removal resumes only the still-owned unplayed occurrence", QueueEditingResumeInterruptedRemoval);
        await Run("QueueEditing: a late unavailable Next leaves the now-playing Wave decoder intact", QueueEditingLateNext);
        await Run("QueueEditing: sleep during the replacement cue respects the exact paused occurrence", QueueEditingSleepDuringCue);
        await Run("QueueEditing: empty Wave Next inherits the unplayed occurrence from removal", QueueEditingEmptyWaveNext);
        await Run("QueueEditing: move remove and undo retain the filter and exact surviving selection", async () =>
        {
            using var fixture = new SaFixture("queue-edits-filter");
            await fixture.Activate();
            var window = fixture.Window;
            var queue = EpField<PlaybackQueue>(window, "_playbackQueue");
            var outside = SortTrack("outside", "Другая песня", "", "", 100000);
            var shared = SortTrack("repeat", "Видимая Б", "", "", 100000);
            var middle = SortTrack("middle", "Видимая А", "", "", 100000);
            queue.SetItems([outside, shared, middle, shared], outside);
            var repeatedToken = queue.OccurrenceTokenAt(3);
            var middleToken = queue.OccurrenceTokenAt(2);
            SaInvoke(window, "ShowQueueCore", false);
            SortChoose(window, ListSortField.Title);
            var list = Control<ListView>(window, "TracksList");
            var filter = Control<TextBox>(window, "FilterBox");
            filter.Text = "Видимая";
            await Until(() => list.Items.Count == 3);
            list.SelectedIndex = 2;

            SaInvoke(window, "MoveQueueUp_Click", null!, null!);
            Assert(queue.OccurrenceTokenAt(2) == repeatedToken && list.SelectedIndex == 2 && filter.Text == "Видимая",
                "move changed the filter or lost the selected duplicate token");
            await Call(window, "RemoveSelectedQueueAsync");
            Assert(filter.Text == "Видимая" && list.Items.Cast<TrackRow>().SequenceEqual([middle, shared]) && list.SelectedItems.Count == 0,
                "remove cleared the filter or selected a different row");
            list.SelectedIndex = 0;
            SaInvoke(window, "UndoQueueRemoval_Click", null!, null!);
            var selectedIndex = (int)SaInvokeResult(window, "SelectedQueueIndex")!;
            Assert(filter.Text == "Видимая" && list.Items.Cast<TrackRow>().SequenceEqual([middle, shared, shared])
                && queue.OccurrenceTokenAt(2) == repeatedToken && selectedIndex >= 0
                && queue.OccurrenceTokenAt(selectedIndex) == middleToken && !fixture.Player.HasSource,
                "undo cleared the filter, changed the selection, or opened playback");
        });
        await Run("QueueEditing: Back uses live history after replacing the playback source", async () =>
        {
            using var fixture = new SaFixture("queue-history-live-back");
            await fixture.Activate();
            var window = fixture.Window;
            var queue = EpField<PlaybackQueue>(window, "_playbackQueue");
            queue.SetItems([Row("old-first"), Row("old-second")]);
            queue.Next();
            SaInvoke(window, "History_Click", null!, null!);
            var list = Control<ListView>(window, "TracksList");
            Assert(list.Items.Count == 1, "fixture had no old history before leaving");
            list.SelectedIndex = 0;
            SaClick(Control<Button>(window, "SearchNav"));
            var fresh = Row("fresh-first");
            queue.SetItems([fresh, Row("fresh-second")]);
            queue.Next();
            var version = queue.Version;

            SaInvoke(window, "BrowseBack_Click", null!, null!);
            Assert(list.Items.Cast<TrackRow>().SequenceEqual([fresh]) && list.SelectedItems.Count == 0 && queue.Version == version,
                "Back restored old history or selected its stale index in the replacement source");
        });
        await Run("QueueEditing: undo readmits restored tracks only when unvisited in the current shuffle cycle", () =>
        {
            var queue = new PlaybackQueue(new Random(13));
            var a = Row("a"); var b = Row("b"); var c = Row("c");
            queue.SetItems([a, b, c]);
            queue.RemoveMany([1]);
            queue.Shuffle = true;
            Assert(queue.UndoRemoval(), "undo did not restore the removed track");
            Assert(new[] { queue.Next(), queue.Next() }.ToHashSet().SetEquals([b, c]) && queue.Next() is null,
                "enabling shuffle with a removed track lost or duplicated the restored occurrence");

            queue.SetItems([a, b, c]);
            queue.SelectAt(1); queue.SelectAt(0);
            queue.RemoveMany([1]);
            Assert(queue.UndoRemoval() && ReferenceEquals(queue.Next(), c) && queue.Next() is null,
                "undo replayed an occurrence already visited in the same shuffle cycle");

            queue.SetItems([a, b, c]);
            queue.SelectAt(1); queue.SelectAt(0);
            queue.RemoveMany([1]);
            queue.Repeat = RepeatMode.All;
            Assert(ReferenceEquals(queue.Next(), c) && ReferenceEquals(queue.Next(), a), "fixture failed to cross the repeat boundary");
            queue.Repeat = RepeatMode.Off;
            Assert(queue.UndoRemoval() && new[] { queue.Next(), queue.Next() }.ToHashSet().SetEquals([b, c]) && queue.Next() is null,
                "undo treated a track visited in the old cycle as visited in the new cycle");
            return Task.CompletedTask;
        });
        await Run("QueueEditing: deleting a paused or restored current track cues the exact successor until Play", async () =>
        {
            var path = WriteWave("queue-remove-paused.wav", 30);
            foreach (var restored in new[] { false, true })
            {
                using var fixture = new SaFixture(restored ? "queue-remove-restored" : "queue-remove-paused");
                var window = fixture.Window;
                var old = new TrackRow { Id = "old", RequestId = "old", Title = "Первый", Available = true, LocalPath = path, DurationMs = 30000 };
                var shared = new TrackRow { Id = "shared", RequestId = "shared", Title = "Следующий", Available = true, LocalPath = path, DurationMs = 30000 };
                var queue = SaField<PlaybackQueue>(window, "_playbackQueue");
                queue.RestoreItems([shared, old, shared], 1, false, RepeatMode.Off);
                var targetToken = queue.OccurrenceTokenAt(2);
                if (restored)
                {
                    UlSet(window, "_current", old);
                    UlSet(window, "_restoredPlaybackOccurrence", queue.CurrentOccurrenceToken);
                    UlSet(window, "_restoredPlaybackSeconds", 18d);
                }
                else
                {
                    await window.PlayTrackAsync(old);
                    await Call(window, "TogglePlaybackAsync");
                    Assert(fixture.Player.HasSource && !fixture.Player.IsPlaying, "fixture did not pause a live source");
                }
                SaInvoke(window, "ShowQueueCore", false);
                await Call(window, "RemoveSelectedQueueAsync");
                Assert(queue.CurrentOccurrenceToken == targetToken && ReferenceEquals(SaField<TrackRow>(window, "_current"), shared)
                    && (int)SaInvokeResult(window, "SelectedQueueIndex")! == 1 && !fixture.Player.HasSource && !fixture.Player.IsPlaying,
                    "deletion autoplayed or did not cue the exact surviving occurrence");
                Assert(SaField<double?>(window, "_restoredPlaybackSeconds") is null && Control<Button>(window, "PlayPauseButton").IsEnabled,
                    "the previous seek survived deletion or explicit Play was disabled");
                await Call(window, "TogglePlaybackAsync");
                Assert(fixture.Player.IsPlaying && queue.CurrentOccurrenceToken == targetToken && fixture.Player.Position.TotalSeconds < 2,
                    "explicit Play did not start the cued occurrence at its beginning");
            }
        });
        await Run("QueueEditing: transport Play starts a manually built queue from Home", async () =>
        {
            using var fixture = new SaFixture("queue-transport-from-home");
            var window = fixture.Window;
            var path = WriteWave("queue-transport-home.wav", 30);
            var a = new TrackRow { Id = "a", RequestId = "a", Title = "A", Available = true, LocalPath = path, DurationMs = 30000 };
            var b = new TrackRow { Id = "b", RequestId = "b", Title = "B", Available = true, LocalPath = path, DurationMs = 30000 };
            SaInvoke(window, "ShowRows", (object)new[] { Row("hidden-unrelated") });
            var queue = SaField<PlaybackQueue>(window, "_playbackQueue");
            queue.Enqueue(a); queue.Enqueue(b);
            var current = queue.CurrentOccurrenceToken;
            var replacement = queue.ReplacementVersion;
            SaInvoke(window, "SetSection", "home", "Главная");
            SaInvoke(window, "UpdateControls");
            Assert(Control<Button>(window, "PlayPauseButton").IsEnabled, "queued tracks did not enable the lower Play button");
            await Call(window, "TogglePlaybackAsync");
            Assert(queue.Items.SequenceEqual([a, b]) && queue.CurrentOccurrenceToken == current && queue.ReplacementVersion == replacement
                && fixture.Player.IsPlaying && ReferenceEquals(SaField<TrackRow>(window, "_current"), a),
                "lower Play used the hidden page instead of the explicit queue");
        });
        await Run("QueueEditing: ordinary queue highlights only the playing duplicate through a sort", async () =>
        {
            using var fixture = new SaFixture("queue-playing-duplicate-mark");
            var window = fixture.Window;
            var path = WriteWave("queue-playing-mark.wav", 30);
            var a = new TrackRow { Id = "a", RequestId = "a", Title = "А", Available = true, LocalPath = path, DurationMs = 30000 };
            var b = new TrackRow { Id = "b", RequestId = "b", Title = "Я", Available = true, LocalPath = path, DurationMs = 30000 };
            var queue = SaField<PlaybackQueue>(window, "_playbackQueue");
            queue.RestoreItems([a, b, a], 2, false, RepeatMode.Off);
            var current = queue.CurrentOccurrenceToken;
            await window.PlayTrackAsync(a);
            SaInvoke(window, "ShowQueueCore", false);
            var list = Control<ListView>(window, "TracksList");
            bool[] Marks()
            {
                window.UpdateLayout();
                return Enumerable.Range(0, list.Items.Count).Select(index =>
                {
                    var row = (ListViewItem)list.ItemContainerGenerator.ContainerFromIndex(index)!;
                    return ((FrameworkElement)row.Template.FindName("PlayingMark", row)).Visibility == Visibility.Visible;
                }).ToArray();
            }
            Assert(Marks().SequenceEqual([false, false, true]), "both duplicate rows received the active playback mark");
            SortChoose(window, ListSortField.Title);
            list.SelectedIndex = 0;
            Assert(Marks().SequenceEqual([false, true, false]) && queue.CurrentOccurrenceToken == current,
                "sort or independent selection transferred the marker to the wrong duplicate");
        });
        await Run("QueueEditing: 3900-row recycled selection moves and removes exact offscreen duplicate occurrences", QueueEditingVirtualizedDuplicates);
        await Run("QaFixes061: queue idle text follows remaining empty and restored rows without autoplay", async () =>
        {
            var directory = Path.Combine(_directory, "qa-fixes061-queue-idle"); Directory.CreateDirectory(directory);
            var requests = 0;
            FakeHandler NoNetwork() => new((_, _) =>
            {
                Interlocked.Increment(ref requests); throw new InvalidOperationException("queue idle fixture attempted network");
            });
            var originalArtwork = typeof(Artwork).GetField("_cache", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null);
            using var artwork = new ArtworkCache(Path.Combine(directory, "artwork"), NoNetwork());
            using var player = new NativePlayer(new AudioBuffer(Path.Combine(directory, "audio"), NoNetwork()));
            using var offline = new OfflineLibrary(Path.Combine(directory, "music"), new AudioBuffer(Path.Combine(directory, "downloads"), NoNetwork()));
            var preferences = new PreferencesStore(Path.Combine(directory, "preferences"));
            preferences.Save(new PlayerPreferences { Volume = 0, CrossfadeSeconds = 0, Theme = "Dark" });
            var previousTheme = ThemeManager.Mode;
            MainWindow? window = null;
            try
            {
                Artwork.Cache = artwork;
                window = new MainWindow(api: new MusicApi(NoNetwork()), store: new SessionStore(Path.Combine(directory, "session")), restore: false,
                    diagnosticsDirectory: directory, offline: offline, player: player, preferences: preferences, audioOutputDevices: new PlaybackOptionsFakeOutputs());
                var wav = WriteWave("qa-fixes061-queue-idle.wav", 30);
                var rows = Enumerable.Range(0, 3).Select(index => new TrackRow { Id = "queue-idle-" + index, RequestId = "queue-idle-" + index,
                    Title = "Queue idle fixture " + index, Artist = "Fixture", Available = true, DurationMs = 30000, LocalPath = wav }).ToArray();
                var queue = EpField<PlaybackQueue>(window, "_playbackQueue");
                queue.SetItems(rows, rows[2]);
                var tokens = Enumerable.Range(0, rows.Length).Select(queue.OccurrenceTokenAt).ToArray();
                await window.PlayTrackAsync(rows[2]);
                EpInvoke(window, "ShowQueueCore", false);
                Assert(player.IsPlaying && player.HasSource && player.Volume == 0 && Control<TextBlock>(window, "TrackTitle").Text == rows[2].Title,
                    "fixture did not start the actual muted last-track lifecycle before queue removal");
                await Call(window, "RemoveSelectedQueueAsync");
                Assert(queue.Items.SequenceEqual(rows.Take(2)) && Enumerable.Range(0, 2).Select(queue.OccurrenceTokenAt).SequenceEqual(tokens.Take(2)),
                    "last-current removal changed the remaining occurrences or playback order");
                void AssertIdleText(string title, string artist) => Assert(queue.Current is null && EpField<TrackRow?>(window, "_current") is null
                    && !player.HasSource && !player.IsPlaying && Control<TextBlock>(window, "TrackTitle").Text == title
                    && Control<TextBlock>(window, "TrackArtist").Text == artist && Control<TextBlock>(window, "QualityText").Text == "",
                    "queue idle message disagrees with rows or restarted playback");
                AssertIdleText("Выберите трек", "Двойной щелчок или кнопка «Слушать»");
                EpInvoke(window, "UndoQueueRemoval_Click", window, new RoutedEventArgs());
                Assert(queue.Items.SequenceEqual(rows) && Enumerable.Range(0, 3).Select(queue.OccurrenceTokenAt).SequenceEqual(tokens),
                    "undo did not restore the exact last occurrence in its original order");
                AssertIdleText("Выберите трек", "Двойной щелчок или кнопка «Слушать»");
                queue.SetItems([rows[2]], rows[2]);
                var onlyToken = queue.CurrentOccurrenceToken;
                await window.PlayTrackAsync(rows[2]); EpInvoke(window, "ShowQueueCore", false);
                Assert(player.IsPlaying && player.HasSource && Control<TextBlock>(window, "TrackTitle").Text == rows[2].Title,
                    "fixture did not restart its local source for the truly empty queue case");
                await Call(window, "RemoveSelectedQueueAsync");
                Assert(queue.Items.Count == 0, "removing the only occurrence did not empty the queue");
                AssertIdleText("Очередь пуста", "Добавьте треки в очередь");
                EpInvoke(window, "UndoQueueRemoval_Click", window, new RoutedEventArgs());
                Assert(queue.Items.Count == 1 && queue.OccurrenceTokenAt(0) == onlyToken && (int)EpInvokeResultForQueue(window, "SelectedQueueIndex")! == 0,
                    "empty-queue undo lost the occurrence or explicit-Play selection");
                AssertIdleText("Выберите трек", "Двойной щелчок или кнопка «Слушать»");
                Assert(requests == 0, "queue idle fixture accessed a network seam");
            }
            finally
            {
                window?.Close(); ThemeManager.SetMode(previousTheme);
                typeof(Artwork).GetField("_cache", BindingFlags.NonPublic | BindingFlags.Static)!.SetValue(null, originalArtwork);
            }
        });
        await Run("QueueEditing: WPF multiple selection move remove undo and themed layouts", async () =>
        {
            var directory = Path.Combine(_directory, "queue-editing"); var noNetwork = new FakeHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)));
            var window = new MainWindow(api: new MusicApi(noNetwork), store: new SessionStore(Path.Combine(directory, "session")), restore: false,
                diagnosticsDirectory: directory, offline: new OfflineLibrary(Path.Combine(directory, "music")));
            var previous = ThemeManager.Mode;
            try
            {
                window.Show(); var q = EpField<PlaybackQueue>(window, "_playbackQueue"); q.SetItems([Row("a"), Row("b"), Row("c"), Row("d")]);
                EpInvoke(window, "ShowQueue"); var list = Control<ListView>(window, "TracksList");
                list.SelectedItems.Clear(); list.SelectedItems.Add(q.Items[1]); list.SelectedItems.Add(q.Items[2]);
                EpInvoke(window, "MoveQueueDown_Click", window, new RoutedEventArgs());
                Assert(q.Items.Select(r => r.Id).SequenceEqual(new[] { "a", "d", "b", "c" }), "UI moved only one selected track");
                list.SelectedItems.Clear(); list.SelectedItems.Add(q.Items[2]); list.SelectedItems.Add(q.Items[3]);
                await Call(window, "RemoveSelectedQueueAsync");
                Assert(q.Items.Count == 2 && Control<Button>(window, "QueueUndoButton").IsEnabled, "UI did not remove a batch or enable undo");
                EpInvoke(window, "UndoQueueRemoval_Click", window, new RoutedEventArgs()); Assert(q.Items.Count == 4, "UI undo lost tracks");
                Assert(list.AllowDrop && noNetwork.Seen.Count == 0, "Drag disabled or queue editing reached network");
                q.RemoveMany(Enumerable.Range(0, q.Items.Count)); EpInvoke(window, "ShowQueue");
                EpInvoke(window, "UndoQueueRemoval_Click", window, new RoutedEventArgs());
                Assert(q.Current is null && (int)EpInvokeResultForQueue(window, "SelectedQueueIndex")! == 0,
                    "Undo an empty queue must leave a selectable first track for explicit Play without auto-start");
                q.SetItems([Row("a"), Row("b"), Row("c"), Row("d")]); EpInvoke(window, "ShowQueue"); window.UpdateLayout();
                var target = (ListViewItem)list.ItemContainerGenerator.ContainerFromIndex(3);
                var dragType = typeof(MainWindow).GetNestedType("QueueDrag", BindingFlags.NonPublic)!;
                var stampType = typeof(MainWindow).GetNestedType("QueueInputStamp", BindingFlags.NonPublic)!;
                var nonce = Guid.NewGuid().ToString("N"); var generation = EpField<int>(window, "_accountGeneration");
                object Payload(long version, int account, long? presentation = null)
                {
                    const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                    var stamp = Activator.CreateInstance(stampType, flags, null,
                        [version, EpField<int>(window, "_viewGeneration"), presentation ?? EpField<long>(window, "_queuePresentationGeneration"), account, null], null)!;
                    return Activator.CreateInstance(dragType, flags, null, [nonce, stamp, new[] { 0, 1 }], null)!;
                }
                void SetPayload(object? payload) => typeof(MainWindow).GetField("_activeQueueDrag", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(window, payload);
                DragEventArgs Drag(RoutedEvent routed, string marker)
                {
                    // Replacing the view during a Drop recycles its containers.
                    // The next routed event needs a current visual target.
                    window.UpdateLayout();
                    target = (ListViewItem)list.ItemContainerGenerator.ContainerFromIndex(Math.Min(3, list.Items.Count - 1));
                    var args = (DragEventArgs)Activator.CreateInstance(typeof(DragEventArgs), BindingFlags.Instance | BindingFlags.NonPublic,
                        null, [new DataObject(DataFormats.UnicodeText, marker), DragDropKeyStates.LeftMouseButton, DragDropEffects.Move, target, new Point(2, target.ActualHeight - 2)], null)!;
                    args.RoutedEvent = routed; target.RaiseEvent(args); return args;
                }
                SetPayload(Payload(q.Version, generation));
                Assert(Drag(DragDrop.DragOverEvent, nonce).Effects == DragDropEffects.Move, "Own drag was not accepted");
                Assert(!EpField<System.Windows.Documents.Adorner>(window, "_queueDropLine").IsHitTestVisible, "Drop line intercepts the pointer");
                Assert(Drag(DragDrop.DropEvent, nonce).Effects == DragDropEffects.Move
                    && q.Items.Select(r => r.Id).SequenceEqual(new[] { "c", "d", "a", "b" }), "Routed multi-drop used the wrong insertion gap");
                var version = q.Version;
                SetPayload(Payload(version - 1, generation)); Assert(Drag(DragDrop.DropEvent, nonce).Effects == DragDropEffects.None && q.Version == version, "Stale queue drag changed the queue");
                SetPayload(Payload(version, generation + 1)); Assert(Drag(DragDrop.DropEvent, nonce).Effects == DragDropEffects.None && q.Version == version, "Another account's drag changed the queue");
                SetPayload(Payload(version, generation, EpField<long>(window, "_queuePresentationGeneration") - 1));
                Assert(Drag(DragDrop.DropEvent, nonce).Effects == DragDropEffects.None && q.Version == version, "a drag from stale row presentation changed the queue");
                SetPayload(Payload(version, generation)); Assert(Drag(DragDrop.DropEvent, "external text").Effects == DragDropEffects.None && q.Version == version, "External data was accepted");
                SetPayload(null);
                foreach (var theme in new[] { "Light", "Dark" })
                { ThemeManager.SetMode(theme); await HomeWaveUi041Capture(window, "queue-editing", theme, 1050, 680); }
            }
            finally { window.Close(); ThemeManager.SetMode(previous); }
        });
    }
    private static Task QueueEditingPressOwnership()
    {
        using var f = new SaFixture("queue-press-ownership");
        var w = f.Window;
        var queue = SaField<PlaybackQueue>(w, "_playbackQueue");
        var repeated = Row("repeat-press");
        queue.SetItems([repeated, Row("other-press"), repeated, Row("advance-press")]);
        SaInvoke(w, "ShowQueueCore", false);
        var list = Control<ListView>(w, "TracksList");
        w.UpdateLayout();
        var beforeFocus = Keyboard.FocusedElement as DependencyObject;
        try
        {
            SelectMultiple();
            Press(RowAt(2));
            list.SelectedIndex = 3;
            var drag = EpInvokeResultForQueue(w, "TakeQueueDrag");
            Assert(drag?.GetType().GetProperty("Indices")?.GetValue(drag) is int[] indices && indices.SequenceEqual([1, 2]),
                "motion used new selection instead of the pressed occurrence tokens");

            SelectMultiple();
            Press(RowAt(2));
            queue.SelectAt(3);
            SaInvoke(w, "RefreshPlaybackView");
            Assert(EpInvokeResultForQueue(w, "TakeQueueDrag") is null && list.SelectedIndex == 3,
                "an advance during the press armed a drag of the new track");

            SelectMultiple();
            Press(RowAt(2));
            SortChoose(w, ListSortField.Title); SortChoose(w, ListSortField.Original);
            Assert(EpInvokeResultForQueue(w, "TakeQueueDrag") is null, "a reordered/recreated list accepted a stale press");

            SelectMultiple();
            var target = RowAt(2);
            Pk040Focus(RowAt(0));
            target.PreviewGotKeyboardFocus += LocalFocus;
            try
            {
                Press(target);
                var up = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
                    { RoutedEvent = UIElement.PreviewMouseLeftButtonUpEvent, Source = target };
                SaInvoke(w, "QueueDragUp", list, up);
            }
            finally { target.PreviewGotKeyboardFocus -= LocalFocus; }
            Assert(list.SelectedIndex == 2 && list.SelectedItems.Count == 1 && target.IsKeyboardFocused,
                "click without drag did not give the exact duplicate the selection and Enter focus");
            var expected = queue.OccurrenceTokenAt(2);
            target.RaiseEvent(new KeyEventArgs(new Pk040Keyboard(ModifierKeys.None), new Pk040Source(target), Environment.TickCount, Key.Enter)
                { RoutedEvent = Keyboard.PreviewKeyDownEvent });
            Assert(queue.CurrentOccurrenceToken == expected, "Enter after a delayed click used the old focused occurrence");
            return Task.CompletedTask;
        }
        finally { Pk040Focus(beforeFocus); }

        ListViewItem RowAt(int index) => (ListViewItem)list.ItemContainerGenerator.ContainerFromIndex(index);
        void SelectMultiple() { w.UpdateLayout(); list.UnselectAll(); RowAt(1).IsSelected = RowAt(2).IsSelected = true; }
        void Press(ListViewItem row)
        {
            var down = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
                { RoutedEvent = UIElement.PreviewMouseLeftButtonDownEvent, Source = row };
            SaInvoke(w, "QueueDragDown", list, down);
            Assert(down.Handled, "fixture did not preserve multiselection during the press");
        }
        static void LocalFocus(object sender, KeyboardFocusChangedEventArgs args)
        {
            if (ReferenceEquals(sender, args.NewFocus)) Pk040Focus((DependencyObject)sender);
        }
        static TrackRow Row(string id) => new() { Id = id, RequestId = id, Title = id, Available = true };
    }

    private static async Task QueueEditingResumeInterruptedRemoval()
    {
        foreach (var action in new[] { "resume", "pause", "stop" })
        {
            using var f = new TwFixture("partial-removal-resume-" + action);
            var w = f.Window;
            await Call(w, "OpenTrackWaveAsync", TwSeed("1234", "Волна", true));
            await f.Ready("1234");
            var q = SaField<PlaybackQueue>(w, "_playbackQueue");
            var a = q.Items[0];
            var b = q.Items[1];
            var c = q.Items[2];
            var d = q.Items[3];
            var bToken = q.OccurrenceTokenAt(1);
            var cToken = q.OccurrenceTokenAt(2);
            f.IndexAudio([d.Id]);
            var route = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            SaInvoke(w, "CancelNextPreparation", true);
            UlSet(w, "_audioOutputReady", route.Task);
            w.QueuePlaybackBeforeVerificationForChecks = token =>
            {
                entered.TrySetResult();
                return release.Task.WaitAsync(token);
            };
            try
            {
                PlaybackOptionsInvoke(f.Player, "OnMediaEnded", PreloadGet<object>(f.Player, "_current"));
                await Until(() => q.CurrentOccurrenceToken == bToken);
                Assert(!f.Player.HasSource, "fixture did not hold B before opening");
                SaInvoke(w, "ShowQueueCore", false);
                var list = Control<ListView>(w, "TracksList");
                list.UnselectAll();
                list.SelectedItems.Add(b);
                list.SelectedItems.Add(c);
                var grouped = Call(w, "RemoveSelectedQueueAsync");
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                list.UnselectAll();
                list.SelectedItems.Add(c);
                await Call(w, "RemoveSelectedQueueAsync");
                Assert(q.CurrentOccurrenceToken == bToken && !q.IndicesOfOccurrences(new HashSet<long> { cToken }).Any()
                    && q.Items.Contains(d), "the second removal did not delete only C");
                if (action == "pause") await w.HandleDesktopMediaCommandAsync(DesktopMediaAction.Pause);
                if (action == "stop") await w.HandleDesktopMediaCommandAsync(DesktopMediaAction.Stop);
                release.TrySetResult();
                route.TrySetResult();
                await grouped.WaitAsync(TimeSpan.FromSeconds(8));
                var playing = action == "resume";
                await SaField<Task>(w, "_feedbackTail");
                Assert(q.CurrentOccurrenceToken == bToken && f.Player.IsPlaying == playing
                    && f.Feedback.Count(e => e == ("trackFinished", a.Id)) == 1
                    && f.Feedback.Count(e => e == ("trackStarted", b.Id)) == (playing ? 1 : 0)
                    && f.Feedback.All(e => e != ("trackStarted", d.Id)),
                    $"{action}: the obsolete group removal lost or resurrected playback at B");
            }
            finally { route.TrySetResult(); release.TrySetResult(); }
        }
    }

    private static async Task QueueEditingLateNext()
    {
        using var f = new TwFixture("late-next-current-begins");
        var w = f.Window;
        await Call(w, "OpenTrackWaveAsync", TwSeed("1234", "Волна", true));
        await f.Ready("1234");
        var q = SaField<PlaybackQueue>(w, "_playbackQueue");
        var a = q.Items[0];
        var b = q.Items[1];
        q.SetItems([a, b], a);
        var target = q.OccurrenceTokenAt(1);
        f.IndexAudio(["901"]);
        var library = SaField<OfflineLibrary>(w, "_offline");
        var file = library.ResolvePath(library.Read().Single().FileName);
        SaInvoke(w, "CancelNextPreparation", true);
        var route = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        UlSet(w, "_audioOutputReady", route.Task);
        try
        {
            PlaybackOptionsInvoke(f.Player, "OnMediaEnded", PreloadGet<object>(f.Player, "_current"));
            await Until(() => q.CurrentOccurrenceToken == target);
            Assert(!f.Player.HasSource && !f.Player.IsPlaying, "fixture did not hold B before its first Play");
            f.HoldMore = true;
            var next = Call(w, "AdvanceAsync", 1, false);
            await f.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            route.TrySetResult();
            await Until(() => f.Player.IsPlaying && f.Feedback.Contains(("trackStarted", b.Id)));
            f.Player.Seek(TimeSpan.FromSeconds(7));
            await Until(() => f.Player.Position.TotalSeconds >= 6);
            var decoder = PreloadGet<object>(f.Player, "_current");
            using (File.Open(file, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                f.Release.TrySetResult();
                await next.WaitAsync(TimeSpan.FromSeconds(8));
            }
            await SaField<Task>(w, "_feedbackTail");
            Assert(q.CurrentOccurrenceToken == target && q.Items.Any(row => row.Id == "901")
                && ReferenceEquals(decoder, PreloadGet<object>(f.Player, "_current")) && f.Player.IsPlaying
                && f.Player.Position.TotalSeconds >= 6 && f.Feedback.Count(e => e == ("trackStarted", b.Id)) == 1
                && f.Feedback.All(e => e != ("skip", b.Id)), "rejected late Next restarted an already sounding occurrence");
        }
        finally { route.TrySetResult(); f.Release.TrySetResult(); }
    }

    private static async Task QueueEditingSleepDuringCue()
    {
        using var f = new TwFixture("sleep-after-removal-cue");
        var w = f.Window;
        await Call(w, "OpenTrackWaveAsync", TwSeed("1234", "Волна", true));
        await f.Ready("1234");
        var q = SaField<PlaybackQueue>(w, "_playbackQueue");
        var old = q.CurrentOccurrenceToken;
        var replacement = q.Items[1];
        var target = q.OccurrenceTokenAt(1);
        f.IndexAudio([replacement.Id]);
        var (windows, Dispatch) = PlaybackOptionsMediaGate(w);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var expiredOnCue = false;
        w.SleepElapsedForChecks = () =>
        {
            if (q.CurrentOccurrenceToken != target || !ReferenceEquals(SaField<TrackRow?>(w, "_current"), replacement)) return TimeSpan.Zero;
            expiredOnCue = true;
            return TimeSpan.FromMinutes(15);
        };
        w.SetSleepTimer(SleepTimerMode.Minutes15);
        w.QueuePlaybackBeforeVerificationForChecks = token => { entered.TrySetResult(); return release.Task.WaitAsync(token); };
        SaInvoke(w, "ShowQueueCore", false);
        SaInvoke(w, "SelectVisibleQueueOccurrence", q.CurrentIndex);
        var removal = Call(w, "RemoveSelectedQueueAsync");
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert(q.CurrentOccurrenceToken == old && f.Player.IsPlaying && !expiredOnCue, "fixture did not hold admitted removal");
            release.TrySetResult();
            await removal.WaitAsync(TimeSpan.FromSeconds(8));
            await SaField<Task>(w, "_feedbackTail");
            Assert(expiredOnCue && q.CurrentOccurrenceToken == target && q.CanUndoRemoval
                && !f.Player.HasSource && !f.Player.IsPlaying && windows.HandlesMediaKeys
                && f.Feedback.All(item => item != ("trackStarted", replacement.Id)), "removal restarted the replacement after the sleep deadline");
            await Dispatch(Windows.Media.SystemMediaTransportControlsButton.Play).WaitAsync(TimeSpan.FromSeconds(8));
            await Until(() => f.Feedback.Contains(("trackStarted", replacement.Id)));
            Assert(f.Player.IsPlaying && q.CurrentOccurrenceToken == target
                && f.Feedback.Count(item => item == ("trackStarted", replacement.Id)) == 1,
                "explicit Play failed to start the paused replacement once");
        }
        finally { release.TrySetResult(); }
    }

    private static async Task QueueEditingEmptyWaveNext()
    {
        foreach (var action in new[] { "play", "busy", "pause", "stop" })
        {
            using var f = new TwFixture("empty-wave-next-from-removal-" + action)
            {
                // A live B can prefetch again after manual More finishes.
                MoreAvailability = action == "busy" ? null : static _ => false
            };
            var w = f.Window;
            await Call(w, "OpenTrackWaveAsync", TwSeed("1234", "Волна", true));
            await f.Ready("1234");
            var q = SaField<PlaybackQueue>(w, "_playbackQueue");
            var a = q.Items[0]; var b = q.Items[1]; var c = q.Items[2]; var d = q.Items[3];
            var target = q.OccurrenceTokenAt(1);
            f.IndexAudio(action == "busy" ? [d.Id, "901"] : [d.Id]);
            SaInvoke(w, "CancelNextPreparation", true);
            var route = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var admitted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var lateAdmission = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            UlSet(w, "_audioOutputReady", route.Task);
            w.QueuePlaybackBeforeVerificationForChecks = token =>
            {
                if (admitted.TrySetResult()) return release.Task.WaitAsync(token);
                lateAdmission.TrySetResult();
                return Task.CompletedTask;
            };
            try
            {
                PlaybackOptionsInvoke(f.Player, "OnMediaEnded", PreloadGet<object>(f.Player, "_current"));
                await Until(() => q.CurrentOccurrenceToken == target);
                Assert(!f.Player.HasSource && !f.Player.IsPlaying, "B already started before group removal");
                SaInvoke(w, "ShowQueueCore", false);
                var list = Control<ListView>(w, "TracksList");
                Select(b, c);
                var removing = Call(w, "RemoveSelectedQueueAsync");
                await admitted.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Select(c, d);
                await Call(w, "RemoveSelectedQueueAsync");
                Assert(q.Items.SequenceEqual([a, b]) && q.CurrentOccurrenceToken == target, "independent removal did not make B the tail");
                if (action != "busy") f.UnavailableMore = 3;
                f.HoldMore = true;
                var next = Call(w, "AdvanceAsync", 1, false);
                await f.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await removing.WaitAsync(TimeSpan.FromSeconds(5));
                if (action == "pause") await w.HandleDesktopMediaCommandAsync(DesktopMediaAction.Pause);
                if (action == "stop") await w.HandleDesktopMediaCommandAsync(DesktopMediaAction.Stop);
                var library = SaField<OfflineLibrary>(w, "_offline");
                using var busy = action == "busy" ? File.Open(library.ResolvePath(library.Read().Single(item => item.Id == "901").FileName),
                    FileMode.Open, FileAccess.Read, FileShare.None) : null;
                f.Release.TrySetResult();
                if (action == "busy") await lateAdmission.Task.WaitAsync(TimeSpan.FromSeconds(5));
                route.TrySetResult();
                await next.WaitAsync(TimeSpan.FromSeconds(8));
                await SaField<Task>(w, "_feedbackTail");
                var playing = action is "play" or "busy";
                Assert(q.CurrentOccurrenceToken == target && q.Items.Take(2).SequenceEqual([a, b])
                    && q.Items.Any(item => item.Id == "901") == (action == "busy"),
                    $"{action}: refill or removal displaced the exact live occurrence");
                Assert(f.Player.IsPlaying == playing && f.Feedback.Count(item => item == ("trackFinished", a.Id)) == 1
                    && f.Feedback.Count(item => item == ("trackStarted", b.Id)) == (playing ? 1 : 0),
                    $"{action}: More discarded B's live opening or revived a canceled intent");

                void Select(params TrackRow[] tracks)
                {
                    list.UnselectAll();
                    foreach (var track in tracks) list.SelectedItems.Add(track);
                }
            }
            finally { release.TrySetResult(); route.TrySetResult(); f.Release.TrySetResult(); }
        }
    }

    private static async Task QueueEditingPassiveClick()
    {
        foreach (var action in new[] { "click", "empty", "empty-then-append", "different", "trim", "replace", "drag", "fullscreen-roundtrip" })
        {
            using var f = new TwFixture("queue-press-refill-" + action);
            f.UnavailableMore = action == "empty" ? 3 : action == "empty-then-append" ? 1 : 0;
            var w = f.Window;
            await Call(w, "OpenTrackWaveAsync", TwSeed("1234", "Волна", true)); await f.Ready("1234");
            var queue = SaField<PlaybackQueue>(w, "_playbackQueue");
            var source = queue.Current!;
            var next = queue.Items[1];
            var count = action == "trim" ? 22 : 2;
            var otherIndex = action == "trim" ? 3 : 1;
            var duplicate = new TrackRow { Id = "repeated", RequestId = "repeated:1", Title = "Оба вхождения", Available = true };
            var prior = Enumerable.Range(0, count).Select(index => index == 0 || index == otherIndex ? duplicate
                : new TrackRow { Id = $"prior-{index}", RequestId = $"prior-{index}:1", Title = $"Прослушанный {index}", Available = true }).ToArray();
            TrackRow[] rows = [.. prior, source, next];
            queue.SetItems(rows);
            for (var i = 0; i < count; i++) queue.Next();
            var cursor = queue.CurrentOccurrenceToken;
            var pressed = queue.OccurrenceTokenAt(0);
            var other = queue.OccurrenceTokenAt(otherIndex);
            f.HoldMore = true;
            var refill = Call(w, "ExtendWaveQueueAsync");
            try
            {
                await f.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                SaInvoke(w, "ShowQueueCore", false);
                var list = Control<ListView>(w, "TracksList");
                var viewer = Cr040Viewer(list);
                viewer.ScrollToTop(); w.UpdateLayout();
                SelectBoth();
                var down = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
                    { RoutedEvent = UIElement.PreviewMouseLeftButtonDownEvent, Source = RowAt(0) };
                SaInvoke(w, "QueueDragDown", list, down);
                Assert(down.Handled, "the fixture did not enter delayed click with the duplicate group selected");
                if (action == "fullscreen-roundtrip")
                {
                    var version = queue.Version;
                    EpOpen(w);
                    EpInvoke(w, "CloseExpandedPlayer");
                    var up = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
                        { RoutedEvent = UIElement.PreviewMouseLeftButtonUpEvent, Source = RowAt(0) };
                    SaInvoke(w, "QueueDragUp", list, up);
                    Assert(queue.Version == version && SelectedTokens().SequenceEqual([pressed, other]),
                        "leaving and returning to the queue allowed an obsolete click");
                    SaInvoke(w, "QueueDragDown", list, down);
                    SaInvoke(w, "QueueDragUp", list, up);
                    Assert(SelectedTokens().SequenceEqual([pressed]), "a fresh click after Fullscreen did not select the exact occurrence");
                    f.Release.TrySetResult();
                    await refill.WaitAsync(TimeSpan.FromSeconds(5));
                    continue;
                }
                if (action == "replace")
                {
                    queue.SetItems(rows, source);
                    SaInvoke(w, "RefreshPlaybackView");
                    w.UpdateLayout();
                    SelectBoth();
                }
                f.Release.TrySetResult();
                await refill.WaitAsync(TimeSpan.FromSeconds(5));
                viewer.ScrollToTop(); w.UpdateLayout();
                Assert(action == "replace" ? queue.CurrentOccurrenceToken != cursor
                    : queue.CurrentOccurrenceToken == cursor && queue.Items.Any(row => row.Id == "901") == (action != "empty")
                        && (action is not ("empty" or "empty-then-append") || f.MoreRequests == (action == "empty" ? 3 : 2)),
                    "the held response did not exercise the requested queue transition");

                if (action == "drag")
                    Assert(EpInvokeResultForQueue(w, "TakeQueueDrag") is null && SelectedTokens().SequenceEqual([pressed, other]),
                        "refill restamped a drag or changed its selected occurrences");
                else
                {
                    var up = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
                        { RoutedEvent = UIElement.PreviewMouseLeftButtonUpEvent, Source = RowAt(action == "different" ? 1 : 0) };
                    SaInvoke(w, "QueueDragUp", list, up);
                    if (action is "click" or "empty" or "empty-then-append")
                    {
                        Assert(SelectedTokens().SequenceEqual([pressed]) && list.SelectedIndex == 0,
                            $"{action}: delayed click kept both occurrences through a passive response");
                        await Call(w, "RemoveSelectedQueueAsync");
                        var live = Enumerable.Range(0, queue.Items.Count).Select(queue.OccurrenceTokenAt).ToArray();
                        Assert(!live.Contains(pressed) && live.Contains(other) && queue.CurrentOccurrenceToken == cursor,
                            "removal after the click also removed the other duplicate or changed playback");
                    }
                    else
                    {
                        long[] expected = action == "trim" ? [other] : action == "replace"
                            ? [queue.OccurrenceTokenAt(0), queue.OccurrenceTokenAt(1)] : [pressed, other];
                        Assert(SelectedTokens().SequenceEqual(expected), "a released click escaped its original token or queue");
                    }
                }

                ListViewItem RowAt(int index) => (ListViewItem)list.ItemContainerGenerator.ContainerFromIndex(index);
                void SelectBoth() { list.UnselectAll(); RowAt(0).IsSelected = RowAt(otherIndex).IsSelected = true; }
                long[] SelectedTokens() => ((int[])EpInvokeResultForQueue(w, "SelectedQueueIndices")!).Select(queue.OccurrenceTokenAt).ToArray();
            }
            finally { f.Release.TrySetResult(); }
        }
    }

    private static async Task QueueEditingVirtualizedDuplicates()
    {
        using var fixture = await Large015Fixture.Create("queue-editing-duplicates", 3900);
        var window = fixture.Window; var list = fixture.List; var rows = fixture.Rows.ToArray();
        var shared = rows[700]; rows[1900] = rows[3300] = shared;
        var queue = EpField<PlaybackQueue>(window, "_playbackQueue"); queue.SetItems(rows);
        EpInvoke(window, "ShowQueueCore", false); await Large015Complete(Large015Pending(window));
        window.UpdateLayout(); await Dispatcher.Yield(DispatcherPriority.ContextIdle);
        var panel = EpVisuals<VirtualizingStackPanel>(list).Single(item => item.IsItemsHost);
        var viewer = EpVisuals<ScrollViewer>(list).First();
        var maxRealized = EpVisuals<ListViewItem>(list).Count();
        EventHandler measure = (_, _) => maxRealized = Math.Max(maxRealized, EpVisuals<ListViewItem>(list).Count());
        list.LayoutUpdated += measure;
        try
        {
            list.SelectedItems.Clear();
            var selected = new[] { 700, 1100, 3300 }; var duplicateIndices = new[] { 700, 1900, 3300 };
            var originalTokens = Enumerable.Range(0, queue.Items.Count).Select(queue.OccurrenceTokenAt).ToArray();
            var selectedTokens = selected.Select(queue.OccurrenceTokenAt).ToHashSet();
            var unselectedToken = queue.OccurrenceTokenAt(1900); var current = queue.CurrentOccurrenceToken;
            foreach (var index in selected)
            {
                panel.BringIndexIntoViewPublic(index); window.UpdateLayout();
                Assert(list.ItemContainerGenerator.ContainerFromIndex(index) is ListViewItem,
                    "fixture could not realize an exact selection candidate");
                ((ListViewItem)list.ItemContainerGenerator.ContainerFromIndex(index)).IsSelected = true;
            }
            Assert(list.SelectedItems.Count == 3 && list.SelectedItems.Cast<TrackRow>().Count(row => ReferenceEquals(row, shared)) == 2,
                "fixture did not establish a partial repeated-reference selection");
            viewer.ScrollToVerticalOffset(50 * 46 + 13); window.UpdateLayout();
            await Dispatcher.Yield(DispatcherPriority.ContextIdle); window.UpdateLayout();
            Assert(duplicateIndices.All(index => list.ItemContainerGenerator.ContainerFromIndex(index) is null),
                "selected duplicate containers were retained instead of recycled");
            var horizontal = viewer.HorizontalOffset; var vertical = viewer.VerticalOffset;
            void AssertViewport(string stage)
            {
                Assert(Math.Abs(viewer.VerticalOffset - vertical) < 1 && Math.Abs(viewer.HorizontalOffset - horizontal) < 1,
                    stage + " changed the original queue viewport");
                Assert(VirtualizingPanel.GetIsVirtualizing(list) && VirtualizingPanel.GetVirtualizationMode(list) == VirtualizationMode.Recycling
                    && ScrollViewer.GetCanContentScroll(list) && maxRealized is > 1 and < 100,
                    stage + " disabled bounded recycling virtualization");
            }
            int[] ReadSelection() => (int[])EpInvokeResultForQueue(window, "SelectedQueueIndices")!;
            Assert(ReadSelection().SequenceEqual(selected), "offscreen selection lost or added an exact duplicate occurrence");
            AssertViewport("reading selection");
            const int gap = 3700;
            var expectedOrder = originalTokens.Where(token => !selectedTokens.Contains(token)).ToList();
            expectedOrder.InsertRange(gap - selected.Count(index => index < gap), selected.Select(index => originalTokens[index]));
            window.MoveQueueSelection(selected, gap); window.UpdateLayout();
            Assert(Enumerable.Range(0, queue.Items.Count).Select(queue.OccurrenceTokenAt).SequenceEqual(expectedOrder)
                && queue.CurrentOccurrenceToken == current, "moving selected occurrences changed another token or the current occurrence");
            Assert(ReadSelection().Select(queue.OccurrenceTokenAt).ToHashSet().SetEquals(selectedTokens)
                && list.SelectedItems.Count == 3, "move restored the first equal duplicate instead of exact selected tokens");
            AssertViewport("moving selection");
            await Call(window, "RemoveSelectedQueueAsync"); await Large015Complete(Large015Pending(window));
            window.UpdateLayout();
            var surviving = Enumerable.Range(0, queue.Items.Count).Select(queue.OccurrenceTokenAt).ToArray();
            Assert(surviving.SequenceEqual(originalTokens.Where(token => !selectedTokens.Contains(token)))
                && surviving.Contains(unselectedToken) && queue.Items.Count == 3897 && queue.CurrentOccurrenceToken == current,
                "remove deleted an unselected duplicate or retained a selected occurrence");
            AssertViewport("removing selection");
            Assert(fixture.Requests == 0 && !fixture.Player.IsPlaying && !fixture.Player.HasSource,
                "queue editing accessed the account/network or started playback");
            File.WriteAllText(Path.Combine(fixture.Directory, "queue-occurrences.json"), JsonSerializer.Serialize(new
            { rows = 3900, duplicateIndices, selected, selectedTokens, unselectedToken, maxRealized, horizontal, vertical,
                surviving = surviving.Length, exactMove = true, exactRemove = true, viewportPreserved = true, requests = fixture.Requests }));
        }
        finally { list.LayoutUpdated -= measure; }
    }
    private static object? EpInvokeResultForQueue(MainWindow window, string method)
        => typeof(MainWindow).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null);
}
