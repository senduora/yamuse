namespace YMusic.Native;

internal sealed partial class PlaybackQueue
{
    private sealed record RemovedOccurrence(Entry Entry, int Index, bool Visited);
    private sealed record RemovalEdit(Entry[] OriginalOrder, RemovedOccurrence[] Removed, Entry[] PlayNext, long ShuffleCycle)
    {
        internal Dictionary<long, string>? Artwork { get; set; }
    }
    private RemovalEdit? _removalUndo;
    private long _shuffleCycle;
    public bool CanUndoRemoval => _removalUndo is not null;
    internal long OccurrenceTokenAt(int index) { ValidateIndex(index); return _entries[index].Token; }

    internal int[] IndicesOfOccurrences(IReadOnlySet<long> tokens)
        => Enumerable.Range(0, _entries.Count).Where(index => tokens.Contains(_entries[index].Token)).ToArray();

    // insertionGap is in the original list, including its final gap. Occurrences,
    // current, shuffle reservations and navigation history retain their identity.
    public void MoveMany(IEnumerable<int> indices, int insertionGap)
    {
        var selected = CheckedIndices(indices);
        if (insertionGap < 0 || insertionGap > _entries.Count) throw new ArgumentOutOfRangeException(nameof(insertionGap));
        if (selected.Length == 0) return;
        var moving = selected.Select(i => _entries[i]).ToArray();
        var gap = insertionGap - selected.Count(i => i < insertionGap);
        var result = _entries.Where((_, i) => Array.BinarySearch(selected, i) < 0).ToList();
        result.InsertRange(gap, moving);
        if (result.SequenceEqual(_entries)) return;
        _entries.Clear(); _entries.AddRange(result);
        Touch(itemsChanged: true);
    }

    internal (TrackRow Track, long Token)? PeekRemovalReplacement(int[] selected, IReadOnlySet<long> excluded)
    {
        var removed = selected.Select(index => _entries[index].Token).ToHashSet();
        bool Eligible(Entry entry) => IsPlayable(entry) && !removed.Contains(entry.Token) && !excluded.Contains(entry.Token);
        var replacement = _future.LastOrDefault(Eligible) ?? (_shuffle
            ? _playNext.FirstOrDefault(Eligible) ?? _shuffleBag.FirstOrDefault(Eligible)
                ?? (_repeat == RepeatMode.All ? ReservedShuffleCycle(null).FirstOrDefault(Eligible) : null)
            : _entries.Skip(CurrentIndex + 1).FirstOrDefault(Eligible)
                ?? (_repeat == RepeatMode.All ? _entries.FirstOrDefault(Eligible) : null));
        return replacement is null ? null : (replacement.Track, replacement.Token);
    }

    // Remove a batch atomically so the caller never starts another selected row
    // while it is about to be removed. Only the last batch can be undone.
    public TrackRow? RemoveMany(IEnumerable<int> indices, Func<TrackRow, bool>? canReplace = null)
        => RemoveManyCore(indices, canReplace is null ? null : entry => canReplace(entry.Track));

    internal TrackRow? RemoveManyWithReplacement(IEnumerable<int> indices, long? replacementOccurrence)
        => RemoveManyCore(indices, entry => entry.Token == replacementOccurrence);

    private TrackRow? RemoveManyCore(IEnumerable<int> indices, Predicate<Entry>? canReplace)
    {
        var selected = CheckedIndices(indices);
        if (selected.Length == 0) return Current;
        var original = _entries.ToArray();
        var removed = selected.Select(i => new RemovedOccurrence(original[i], i, _cycleVisited.Contains(original[i].Token))).ToArray();
        var edit = new RemovalEdit(original, removed, _playNext.ToArray(), _shuffleCycle);
        var currentIndex = CurrentIndex;
        var removingCurrent = selected.Contains(currentIndex);
        foreach (var item in removed) { _entries.Remove(item.Entry); Forget(item.Entry); }
        if (removingCurrent)
        {
            _current = null;
            _reservedShuffleCycle?.RemoveAll(entry => !_presentTokens.Contains(entry.Token));
            bool Eligible(Entry entry) => IsPlayable(entry) && (canReplace is null || canReplace(entry));
            _current = PopPlayable(_future, Eligible) ?? (_shuffle ? TakeShuffle(_repeat == RepeatMode.All, null, Eligible)
                : original.Skip(currentIndex + 1).FirstOrDefault(Eligible)
                    ?? (_repeat == RepeatMode.All ? _entries.FirstOrDefault(Eligible) : null));
            MarkVisited(_current);
        }
        _removalUndo = edit;
        Touch(itemsChanged: true);
        return Current;
    }

    // Restore removed entries around surviving original anchors. New enqueued
    // entries survive; playback is never rewound or restarted by Undo.
    public bool UndoRemoval()
    {
        if (_removalUndo is not { } edit) return false;
        var sameCycle = edit.ShuffleCycle == _shuffleCycle;
        var originalPriorities = sameCycle ? edit.PlayNext.ToHashSet() : [];
        var restoredPriorities = new HashSet<Entry>();
        foreach (var item in edit.Removed.Reverse())
        {
            if (_presentTokens.Contains(item.Entry.Token)) continue;
            var anchor = edit.OriginalOrder.Skip(item.Index + 1).FirstOrDefault(e => _presentTokens.Contains(e.Token));
            _entries.Insert(anchor is null ? _entries.Count : _entries.IndexOf(anchor), item.Entry);
            _presentTokens.Add(item.Entry.Token);
            if (edit.Artwork?.GetValueOrDefault(item.Entry.Token) is { } artwork) item.Entry.Track.SetMissingArtwork(artwork);
            if (!_shuffle) continue;
            if (sameCycle && item.Visited) _cycleVisited.Add(item.Entry.Token);
            else if (originalPriorities.Contains(item.Entry))
                restoredPriorities.Add(item.Entry);
            else AddToShuffleBag(item.Entry);
        }
        if (restoredPriorities.Count > 0)
        {
            // Later PlayNext requests were inserted at the front. Leave those first
            // and merge the restored picks with the still-pending original picks.
            var pending = _playNext.Where(originalPriorities.Contains).ToHashSet();
            _playNext.RemoveAll(originalPriorities.Contains);
            _playNext.AddRange(edit.PlayNext.Where(item => pending.Contains(item) || restoredPriorities.Contains(item)));
        }
        _removalUndo = null;
        Touch(itemsChanged: true);
        return true;
    }

    internal void RememberRemovalArtwork(Func<TrackRow, string?> resolve)
    {
        if (_removalUndo is not { } edit) return;
        foreach (var item in edit.Removed)
        {
            var row = item.Entry.Track;
            if (row.CoverUri is null && resolve(row) is { } artwork)
                (edit.Artwork ??= [])[item.Entry.Token] = artwork;
        }
    }

    private int[] CheckedIndices(IEnumerable<int> indices)
    {
        ArgumentNullException.ThrowIfNull(indices);
        var selected = indices.Distinct().Order().ToArray();
        foreach (var index in selected) ValidateIndex(index);
        return selected;
    }
}
