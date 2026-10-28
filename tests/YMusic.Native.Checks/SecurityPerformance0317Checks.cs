using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using YMusic.Native;

internal static partial class Program
{
    private static async Task CheckSecurityPerformance0317()
    {
        await Run("SecurityPerf0317: bulk selection preserves unavailable known-like and account gates without mutation", Sp0317SelectionGates);
        await Run("SecurityPerf0317: 2048-row bulk selection publishes once with bounded allocations", Sp0317SelectionAllocation);
        await Run("SecurityPerf0317: derived download list preserves ListView style native selection and 3920-row virtualization", Sp0317StyleAndVirtualization);
    }

    private static Task Sp0317SelectionGates()
    {
        TrackRow Row(string id, bool available, string? requestId = null) => new()
        { Id = id, RequestId = requestId ?? id, Available = available, Title = "Synthetic " + id, Artist = "Fixture" };
        var rows = new[] { Row("eligible-metadata", false), Row("eligible-server", true), Row("available", true),
            Row("active", false), Row("unknown-like", false), Row("mismatched-id", false, "other-id"), Row("", false) };
        using var fixture = new Sp0317Fixture("gates", rows);
        foreach (var entry in fixture.Queue.Entries) entry.SetState(DownloadEntryState.Unavailable);
        fixture.Queue.Entries.Single(e => e.Track.Id == "available").SetState(DownloadEntryState.Completed);
        fixture.Queue.Entries.Single(e => e.Track.Id == "active").SetState(DownloadEntryState.Downloading);
        Sp0317Field<HashSet<string>>(fixture.Window, "_favoriteTrackIds").Remove("unknown-like");
        var outside = new DownloadEntry(Row("outside-queue", false));
        Sp0317Field<HashSet<string>>(fixture.Window, "_favoriteTrackIds").Add(outside.Track.Id);
        fixture.List.ItemsSource = fixture.Queue.Entries.Append(outside).ToArray();
        var before = fixture.Fingerprint();
        var queueVersion = Sp0317Field<PlaybackQueue>(fixture.Window, "_playbackQueue").Version;
        var originalEntries = fixture.Queue.Entries.ToArray();
        var events = 0; fixture.List.SelectionChanged += (_, _) => events++;
        fixture.SelectUnavailable();
        Assert(events == 1 && fixture.SelectedIds().SetEquals(["eligible-metadata", "eligible-server"]),
            "bulk selection changed eligibility or published per-row updates");
        Assert(fixture.Unlike.IsEnabled && fixture.Menu.IsEnabled && fixture.Unlike.Content?.ToString() == "Убрать лайки · 2",
            "bulk selection left compact unlike actions stale");
        Assert(fixture.List.SelectionMode == SelectionMode.Extended, "bulk list changed native extended selection");
        fixture.List.SelectedItems.Clear();
        fixture.List.SelectedItem = fixture.Queue.Entries[0];
        Assert(fixture.Unlike.IsEnabled && fixture.Unlike.Content?.ToString() == "Убрать лайк", "native single selection did not update actions");
        fixture.List.SelectedItems.Add(fixture.Queue.Entries[1]);
        Assert(fixture.SelectedIds().Count == 2 && fixture.Unlike.Content?.ToString() == "Убрать лайки · 2", "native additive selection changed");

        Sp0317Set(fixture.Window, "_favoriteIdsKnown", false); fixture.SelectUnavailable();
        Assert(fixture.List.SelectedItems.Count == 0 && !fixture.Unlike.IsEnabled && !fixture.Menu.IsEnabled, "unknown likes allowed selection");
        Sp0317Set(fixture.Window, "_favoriteIdsKnown", true);
        Sp0317Set(fixture.Window, "_account", new Account("another-account", "Synthetic", false)); fixture.SelectUnavailable();
        Assert(fixture.List.SelectedItems.Count == 0, "another account reused a previous download queue");
        Sp0317Set(fixture.Window, "_account", new Account("fixture-account", "Synthetic", false));
        Sp0317Set(fixture.Window, "_downloadQueueAccountGeneration", 1); fixture.SelectUnavailable();
        Assert(fixture.List.SelectedItems.Count == 0, "stale account generation allowed selection");
        Sp0317Set(fixture.Window, "_downloadQueueAccountGeneration", 0);
        Sp0317Set(fixture.Window, "_session", null); fixture.SelectUnavailable();
        Assert(fixture.List.SelectedItems.Count == 0, "signed-out bulk selection allowed writes");
        fixture.SetSession(); fixture.SelectUnavailable();
        Assert(fixture.SelectedIds().SetEquals(["eligible-metadata", "eligible-server"]), "fresh account did not restore exact eligible selection");
        Sp0317Set(fixture.Window, "_catalogWriting", true); var guardedEvents = events; fixture.SelectUnavailable();
        Assert(events == guardedEvents && fixture.SelectedIds().Count == 2, "bulk selection interfered with a collection mutation");
        Sp0317Set(fixture.Window, "_catalogWriting", false);
        Assert(fixture.Requests == 0 && fixture.Fingerprint() == before && fixture.Queue.Entries.SequenceEqual(originalEntries)
            && fixture.Queue.Entries.All(e => !e.RemovedFromCollection && !e.SkipFavoritesDownload)
            && Sp0317Field<PlaybackQueue>(fixture.Window, "_playbackQueue").Version == queueVersion && !fixture.Player.HasSource,
            "selection changed files, queue, likes or playback before an explicit confirmed action");
        return Task.CompletedTask;
    }

    private static Task Sp0317SelectionAllocation()
    {
        using var fixture = new Sp0317Fixture("allocation", Sp0317Rows(2048));
        var events = 0; fixture.List.SelectionChanged += (_, _) => events++;
        var before = GC.GetAllocatedBytesForCurrentThread(); var clock = Stopwatch.StartNew();
        fixture.SelectUnavailable();
        var bytes = GC.GetAllocatedBytesForCurrentThread() - before; var milliseconds = clock.Elapsed.TotalMilliseconds;
        Assert(fixture.List.SelectedItems.Count == 2048 && events == 1, "bulk selection still publishes per-row changes");
        // Baseline DLL 9CD19A...: 558,188,624 temporary bytes at 2048 rows.
        // This broad bound allows instrumentation/runtime variance, without timing assertions.
        Assert(bytes < 32L * 1024 * 1024 && fixture.Requests == 0, "bulk selection retained baseline-scale allocation or contacted a service");
        File.WriteAllText(Path.Combine(_directory, "security-performance-0317-allocation.json"), JsonSerializer.Serialize(new
        { rows = 2048, selectionChangedEvents = events, allocatedBytes = bytes, elapsedMs = milliseconds, baselineAllocatedBytes = 558188624L,
            allocationLimitBytes = 32L * 1024 * 1024, externalRequests = fixture.Requests }, new JsonSerializerOptions { WriteIndented = true }));
        return Task.CompletedTask;
    }

    private static async Task Sp0317StyleAndVirtualization()
    {
        var resources = Application.Current.Resources;
        var hadStyle = resources.Contains(typeof(ListView)); var previousStyle = resources[typeof(ListView)];
        var style = new Style(typeof(ListView)); style.Setters.Add(new Setter(System.Windows.Controls.Control.FontSizeProperty, 13.0));
        resources[typeof(ListView)] = style;
        try
        {
            using var fixture = new Sp0317Fixture("virtualization", Sp0317Rows(3920));
            fixture.Host.Show(); fixture.Host.UpdateLayout(); await Dispatcher.Yield(DispatcherPriority.Loaded);
            Assert(fixture.List is BatchSelectionListView && ReferenceEquals(fixture.List.Style, style) && fixture.List.FontSize == 13
                && fixture.List.View is GridView { Columns.Count: 5 } && fixture.List.ItemContainerStyle is not null
                && VirtualizingPanel.GetIsVirtualizing(fixture.List) && VirtualizingPanel.GetVirtualizationMode(fixture.List) == VirtualizationMode.Recycling,
                "derived list lost its implicit ListView style, columns, row template or recycling policy");
            var viewer = Sp0317Visuals<ScrollViewer>(fixture.List).First();
            var realized = Sp0317Visuals<ListViewItem>(fixture.List).Count();
            var events = 0; fixture.List.SelectionChanged += (_, _) => events++;
            fixture.SelectUnavailable(); fixture.Host.UpdateLayout();
            Assert(events == 1 && fixture.List.SelectedItems.Count == 3920, "large selection did not publish a single full replacement");
            foreach (var end in new[] { true, false })
            {
                viewer.ScrollToVerticalOffset(end ? viewer.ScrollableHeight : 0); fixture.Host.UpdateLayout();
                await Dispatcher.Yield(DispatcherPriority.Background);
                realized = Math.Max(realized, Sp0317Visuals<ListViewItem>(fixture.List).Count());
            }
            var visible = Sp0317Visuals<ListViewItem>(fixture.List).First();
            fixture.List.SelectedItem = fixture.Queue.Entries[^1];
            Sp0317Invoke(fixture.Window, "SelectDownloadContextRow", visible);
            Assert(fixture.List.SelectedItems.Count == 1 && ReferenceEquals(fixture.List.SelectedItem, visible.Content) && fixture.Unlike.IsEnabled,
                "right-click targeting lost native row selection or compact actions");
            Sp0317Invoke(fixture.Window, "SelectDownloadContextRow", (object?)null);
            Assert(fixture.List.SelectedItems.Count == 0 && !fixture.Unlike.IsEnabled && !fixture.Menu.IsEnabled,
                "blank-area context action retained a previous target");
            Assert(realized is > 0 and < 150 && fixture.List.Items.Count == 3920 && fixture.Requests == 0,
                "bulk selection disabled virtualization or contacted a service");
            File.WriteAllText(Path.Combine(_directory, "security-performance-0317-virtualization.json"), JsonSerializer.Serialize(new
            { rows = 3920, maxRealizedRows = realized, inheritedListViewStyle = true, nativeContextSelection = true,
                externalRequests = fixture.Requests }, new JsonSerializerOptions { WriteIndented = true }));
        }
        finally { if (hadStyle) resources[typeof(ListView)] = previousStyle; else resources.Remove(typeof(ListView)); }
    }

    private static TrackRow[] Sp0317Rows(int count) => Enumerable.Range(0, count).Select(i => new TrackRow
    { Id = "synthetic-" + i, RequestId = "synthetic-" + i, Title = "Synthetic track " + i, Artist = "Fixture", Available = false }).ToArray();
    private static T Sp0317Field<T>(object target, string name) => (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;
    private static void Sp0317Set(object target, string name, object? value) => target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);
    private static void Sp0317Invoke(object target, string name, params object?[] args) => target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, args);
    private static IEnumerable<T> Sp0317Visuals<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i); if (child is T result) yield return result;
            foreach (var nested in Sp0317Visuals<T>(child)) yield return nested;
        }
    }

    private sealed class Sp0317Fixture : IDisposable
    {
        private readonly string _path;
        private readonly Sp0317RejectHandler _handler = new();
        internal MainWindow Window { get; }
        internal Window Host { get; }
        internal NativePlayer Player { get; }
        internal DownloadQueueState Queue => Sp0317Field<DownloadQueueState>(Window, "_downloadQueue");
        internal BatchSelectionListView List => Sp0317Field<BatchSelectionListView>(Window, "_downloadEntriesList");
        internal Button Unlike => Sp0317Field<Button>(Window, "_downloadUnlikeButton");
        internal MenuItem Menu => Sp0317Field<MenuItem>(Window, "_downloadUnlikeMenu");
        internal int Requests => _handler.Requests;
        internal Sp0317Fixture(string name, TrackRow[] rows)
        {
            _path = Path.Combine(_directory, "security-performance-0317-" + name); Directory.CreateDirectory(_path);
            File.WriteAllText(Path.Combine(_path, "preserved.txt"), "Synthetic fixture file");
            Player = new NativePlayer(new AudioBuffer(Path.Combine(_path, "audio"), _handler));
            var offline = new OfflineLibrary(Path.Combine(_path, "offline"), new AudioBuffer(Path.Combine(_path, "downloads"), _handler));
            Window = new MainWindow(api: new MusicApi(_handler), store: new SessionStore(Path.Combine(_path, "session")), restore: false,
                diagnosticsDirectory: _path, offline: offline, player: Player, preferences: new PreferencesStore(Path.Combine(_path, "preferences")));
            SetSession(); Sp0317Set(Window, "_account", new Account("fixture-account", "Synthetic", false));
            Sp0317Set(Window, "_downloadQueueAccountUid", "fixture-account"); Sp0317Set(Window, "_favoriteIdsKnown", true);
            Sp0317Field<HashSet<string>>(Window, "_favoriteTrackIds").UnionWith(rows.Select(r => r.Id));
            Queue.Replace(rows, "Synthetic unavailable tracks", favoritesBatch: true);
            foreach (var entry in Queue.Entries) entry.SetState(DownloadEntryState.Unavailable);
            Sp0317Invoke(Window, "DownloadCenter_Click", null, null);
            // Render only checked content; MainWindow never creates a native shell,
            // media controls, real startup restore, or update request.
            var content = (UIElement)Window.Content; Window.Content = null;
            Host = new Window { Content = content, Width = 1280, Height = 860, Left = -16000, Top = 0, ShowActivated = false,
                ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, UseLayoutRounding = true };
        }
        internal void SetSession() => Sp0317Set(Window, "_session", new Session("synthetic", null, DateTimeOffset.UtcNow.AddHours(1), "synthetic", MusicApi.RequiredScopes));
        internal void SelectUnavailable() => Sp0317Invoke(Window, "SelectUnavailableDownloads");
        internal HashSet<string> SelectedIds() => List.SelectedItems.OfType<DownloadEntry>().Select(e => e.Track.Id).ToHashSet(StringComparer.Ordinal);
        internal string Fingerprint() => string.Join(";", Directory.EnumerateFiles(_path, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)
            .Select(path => Path.GetRelativePath(_path, path) + ":" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))));
        public void Dispose() { Host.Close(); Window.Close(); }
    }
    private sealed class Sp0317RejectHandler : HttpMessageHandler
    {
        internal int Requests;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        { Interlocked.Increment(ref Requests); throw new InvalidOperationException("Security performance fixture contacted a service"); }
    }
}
