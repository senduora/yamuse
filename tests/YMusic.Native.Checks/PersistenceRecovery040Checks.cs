using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using YMusic.Native;

internal static partial class Program
{
    private static async Task CheckPersistenceRecovery040()
    {
        await Run("PersistenceRecovery040: invalid capture keeps snapshot and queue and throttles timer retries", Pr040Invalid);
        await Run("PersistenceRecovery040: maximum valid queue is saved without truncation", Pr040Maximum);
    }

    private static TrackRow Pr040Row(int i, string? title = null, string? artist = null, string? album = null) => new()
    {
        Id = "pr040-" + i, RequestId = "pr040-" + i, Title = title ?? "Fixture " + i,
        Artist = artist ?? "Fixture", Album = album ?? "Fixture", Available = true, DurationMs = 30000
    };

    private static (MainWindow Window, PlaybackQueue Queue, PlaybackStateStore Store, Diagnostics Diagnostics, string Path) Pr040Fixture(string name, TrackRow[] rows)
    {
        var directory = Path.Combine(_directory, "persistence-recovery040-" + name);
        Directory.CreateDirectory(directory);
        var store = new PlaybackStateStore(directory);
        var queue = new PlaybackQueue(); queue.SetItems(Enumerable.Range(0, 3).Select(i => Pr040Row(i)));
        store.Save(PlaybackStateStore.Capture("fixture", queue, 0));
        queue.SetItems(rows);
        var diagnostics = new Diagnostics();
        var window = (MainWindow)RuntimeHelpers.GetUninitializedObject(typeof(MainWindow));
        Pr040Set(window, "_restore", true);
        Pr040Set(window, "_account", new Account("fixture", "Fixture", false));
        Pr040Set(window, "_playbackStateAccountUid", "fixture");
        Pr040Set(window, "_playbackQueue", queue);
        Pr040Set(window, "_playbackStateStore", store);
        Pr040Set(window, "_diagnostics", diagnostics);
        Pr040Set(window, "_player", RuntimeHelpers.GetUninitializedObject(typeof(NativePlayer)));
        return (window, queue, store, diagnostics, Path.Combine(directory, PlaybackStateStore.FileName));
    }

    private static async Task Pr040Invalid()
    {
        var cases = new (string Name, TrackRow[] Rows)[]
        {
            ("large", Enumerable.Range(0, 10001).Select(i => Pr040Row(i)).ToArray()),
            ("title", [Pr040Row(1, title: "A\nB")]),
            ("artist", [Pr040Row(1, artist: "A\tB")]),
            ("album", [Pr040Row(1, album: "A\rB")])
        };
        foreach (var (name, rows) in cases)
        {
            var f = Pr040Fixture(name, rows); using var diagnostics = f.Diagnostics;
            var original = File.ReadAllBytes(f.Path);
            var current = f.Queue.Current; var occurrence = f.Queue.CurrentOccurrenceToken;
            var before = DateTimeOffset.UtcNow;
            Pr040Tick(f.Window);
            Assert(diagnostics.LastErrorType == "PlaybackSave:JsonException" && Pr040Get<Task?>(f.Window, "_playbackStateWriteTask") is null,
                name + ": invalid capture escaped validation or started a write");
            var checkpoint = Pr040Get<DateTimeOffset>(f.Window, "_lastPlaybackStateSave");
            Assert(checkpoint >= before && original.SequenceEqual(File.ReadAllBytes(f.Path)), name + ": old snapshot changed or retry throttle was not set");
            Assert(f.Queue.Items.Count == rows.Length && f.Queue.Items.Zip(rows).All(pair => ReferenceEquals(pair.First, pair.Second))
                && ReferenceEquals(f.Queue.Current, current) && f.Queue.CurrentOccurrenceToken == occurrence, name + ": queue changed during failed capture");
            diagnostics.LastErrorType = "timer-sentinel";
            for (var i = 0; i < 200; i++) Pr040Tick(f.Window);
            Assert(diagnostics.LastErrorType == "timer-sentinel" && Pr040Get<DateTimeOffset>(f.Window, "_lastPlaybackStateSave") == checkpoint,
                name + ": invalid snapshot was recaptured every timer tick");
            Pr040Set(f.Window, "_lastPlaybackStateSave", DateTimeOffset.UtcNow.AddSeconds(-5.1));
            Pr040Tick(f.Window);
            Assert(diagnostics.LastErrorType == "PlaybackSave:JsonException" && original.SequenceEqual(File.ReadAllBytes(f.Path)), name + ": due retry lost validation or the previous snapshot");
            var valid = Enumerable.Range(0, 7).Select(i => Pr040Row(i)).ToArray(); f.Queue.SetItems(valid);
            Pr040Set(f.Window, "_lastPlaybackStateSave", DateTimeOffset.UtcNow.AddSeconds(-5.1)); Pr040Tick(f.Window);
            var task = Pr040Get<Task?>(f.Window, "_playbackStateWriteTask"); Assert(task is not null, name + ": valid recovery did not start"); await task!;
            Assert(f.Store.Load("fixture")?.Tracks.Count == 7 && f.Queue.Items.Count == 7
                && f.Queue.Items.Zip(valid).All(pair => ReferenceEquals(pair.First, pair.Second)), name + ": valid save did not recover");
        }
    }

    private static async Task Pr040Maximum()
    {
        var rows = Enumerable.Range(0, PlaybackStateStore.MaximumTracks).Select(i => Pr040Row(i)).ToArray();
        var f = Pr040Fixture("valid-max", rows); using var diagnostics = f.Diagnostics;
        Pr040Tick(f.Window);
        var task = Pr040Get<Task?>(f.Window, "_playbackStateWriteTask"); Assert(task is not null, "valid maximum queue was rejected"); await task!;
        Assert(f.Store.Load("fixture")?.Tracks.Count == rows.Length && f.Queue.Items.Count == rows.Length
            && f.Queue.Items.Zip(rows).All(pair => ReferenceEquals(pair.First, pair.Second)), "valid maximum was truncated or mutated");
        Assert(diagnostics.LastErrorType is null, "valid maximum produced a save error");
    }

    private static void Pr040Tick(MainWindow window) => typeof(MainWindow).GetMethod("PersistPlaybackStateIfDue", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null);
    private static void Pr040Set(MainWindow window, string name, object value) => typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, value);
    private static T Pr040Get<T>(MainWindow window, string name) => (T)typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
}
