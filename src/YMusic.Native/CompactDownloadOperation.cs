using System.ComponentModel;

namespace YMusic.Native;

// A single run's missing/upgrade cohort, rather than the playlist's old cached audio.
// Entry notifications update one contribution; rendering never rescans the playlist.
internal sealed class CompactDownloadOperation : IDisposable
{
    private readonly Dictionary<DownloadEntry, Contribution> _entries = [];
    private Action? _changed;
    private bool _disposed;
    private double _partial;
    internal int Total { get; private set; }
    internal int Completed { get; private set; }
    internal int Failed { get; private set; }
    internal int Unavailable { get; private set; }
    internal int Paused { get; private set; }
    internal int Suspended { get; private set; }
    internal int AccountGeneration { get; }
    internal PlaylistDetails? Playlist { get; }
    internal double Percent => Total == 0 ? 0 : Math.Clamp(100.0 * (Completed + _partial) / Total, 0,
        Completed == Total ? 100 : 99.9);
    internal bool Complete => Total > 0 && Completed == Total;
    internal bool Disposed => _disposed;

    internal CompactDownloadOperation(IEnumerable<DownloadEntry> entries, PlaylistDetails? playlist, int accountGeneration, Action changed)
    {
        Playlist = playlist; AccountGeneration = accountGeneration;
        foreach (var entry in entries) Include(entry);
        _changed = changed;
    }

    internal bool Contains(DownloadEntry entry) => _entries.ContainsKey(entry);
    internal void Include(DownloadEntry entry)
    {
        if (_disposed || entry.SkipFavoritesDownload || entry.State == DownloadEntryState.Removed || _entries.ContainsKey(entry)) return;
        var contribution = Contribution.Read(entry);
        _entries.Add(entry, contribution); Total++; Add(contribution, 1);
        entry.PropertyChanged += EntryChanged;
        _changed?.Invoke();
    }
    internal void Exclude(DownloadEntry entry)
    {
        if (_disposed || !_entries.Remove(entry, out var previous)) return;
        entry.PropertyChanged -= EntryChanged; Total--; Add(previous, -1);
        _changed?.Invoke();
    }
    private void EntryChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (_disposed || sender is not DownloadEntry entry || !_entries.TryGetValue(entry, out var previous)) return;
        if (entry.SkipFavoritesDownload || entry.State == DownloadEntryState.Removed) { Exclude(entry); return; }
        var current = Contribution.Read(entry);
        if (current == previous) return;
        Add(previous, -1); Add(current, 1); _entries[entry] = current;
        _changed?.Invoke();
    }
    private void Add(Contribution value, int sign)
    {
        Completed += sign * value.Completed; Failed += sign * value.Failed;
        Unavailable += sign * value.Unavailable; Paused += sign * value.Paused;
        Suspended += sign * value.Suspended; _partial += sign * value.Partial;
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var entry in _entries.Keys) entry.PropertyChanged -= EntryChanged;
        _entries.Clear(); _changed = null;
        // Terminal counters remain an immutable presentation snapshot, without subscriptions.
    }
    private readonly record struct Contribution(int Completed, int Failed, int Unavailable, int Paused, int Suspended, double Partial)
    {
        internal static Contribution Read(DownloadEntry entry) => new(
            entry.State == DownloadEntryState.Completed ? 1 : 0,
            entry.State == DownloadEntryState.Failed ? 1 : 0,
            entry.State == DownloadEntryState.Unavailable ? 1 : 0,
            entry.State == DownloadEntryState.Paused ? 1 : 0,
            entry.State == DownloadEntryState.Suspended ? 1 : 0,
            // Network EOF is not a verified, committed Save. Only Complete earns the last fraction.
            entry.Active ? Math.Min(.99, entry.Percent / 100.0) : 0);
    }
}

// One dispatcher callback per burst across all workers. Clearing the pending flag
// before draining permits reports arriving during the drain to schedule the next one.
internal sealed class DownloadProgressDispatch(Action<Action> enqueue, Action flush) : IDisposable
{
    private int _pending, _disposed;
    internal void Request()
    {
        if (Volatile.Read(ref _disposed) != 0 || Interlocked.CompareExchange(ref _pending, 1, 0) != 0) return;
        try { enqueue(Drain); }
        catch (InvalidOperationException) { Dispose(); }
    }
    private void Drain()
    {
        Interlocked.Exchange(ref _pending, 0);
        if (Volatile.Read(ref _disposed) == 0) flush();
    }
    public void Dispose() => Interlocked.Exchange(ref _disposed, 1);
}
