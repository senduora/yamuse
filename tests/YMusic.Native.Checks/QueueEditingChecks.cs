using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
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
        await Run("QueueEditing: 3900-row recycled selection moves and removes exact offscreen duplicate occurrences", QueueEditingVirtualizedDuplicates);
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
                var nonce = Guid.NewGuid().ToString("N"); var generation = EpField<int>(window, "_accountGeneration");
                object Payload(long version, int account) => Activator.CreateInstance(dragType,
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, [nonce, version, account, null, new[] { 0, 1 }], null)!;
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
                SetPayload(Payload(version, generation)); Assert(Drag(DragDrop.DropEvent, "external text").Effects == DragDropEffects.None && q.Version == version, "External data was accepted");
                SetPayload(null);
                foreach (var theme in new[] { "Light", "Dark" })
                { ThemeManager.SetMode(theme); await HomeWaveUi041Capture(window, "queue-editing", theme, 1050, 680); }
            }
            finally { window.Close(); ThemeManager.SetMode(previous); }
        });
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
