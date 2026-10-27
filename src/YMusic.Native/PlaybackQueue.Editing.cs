namespace YMusic.Native;

internal sealed partial class PlaybackQueue
{
    private sealed record RemovedOccurrence(Entry Entry, int Index, bool PendingShuffle);
    private sealed record RemovalEdit(Entry[] OriginalOrder, RemovedOccurrence[] Removed);
    private RemovalEdit? _removalUndo;
    public bool CanUndoRemoval => _removalUndo is not null;
    internal long OccurrenceTokenAt(int index) { ValidateIndex(index); return _entries[index].Token; }

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

    // Remove a batch atomically so the caller never starts another selected row
    // while it is about to be removed. Only the last batch can be undone.
    public TrackRow? RemoveMany(IEnumerable<int> indices)
    {
        var selected = CheckedIndices(indices);
        if (selected.Length == 0) return Current;
        var original = _entries.ToArray();
        var removed = selected.Select(i => new RemovedOccurrence(original[i], i,
            _shuffleBag.Contains(original[i]) || _playNext.Contains(original[i]))).ToArray();
        var currentIndex = CurrentIndex;
        var removingCurrent = selected.Contains(currentIndex);
        foreach (var item in removed) { _entries.Remove(item.Entry); Forget(item.Entry); }
        if (removingCurrent)
        {
            _current = null;
            _current = PopPlayable(_future) ?? (_shuffle ? TakeShuffle(_repeat == RepeatMode.All, null)
                : original.Skip(currentIndex + 1).FirstOrDefault(IsPlayable)
                    ?? (_repeat == RepeatMode.All ? _entries.FirstOrDefault(IsPlayable) : null));
            MarkVisited(_current);
        }
        _removalUndo = new(original, removed);
        Touch(itemsChanged: true);
        return Current;
    }

    // Restore removed entries around surviving original anchors. New enqueued
    // entries survive; playback is never rewound or restarted by Undo.
    public bool UndoRemoval()
    {
        if (_removalUndo is not { } edit) return false;
        foreach (var item in edit.Removed.Reverse())
        {
            if (_presentTokens.Contains(item.Entry.Token)) continue;
            var anchor = edit.OriginalOrder.Skip(item.Index + 1).FirstOrDefault(e => _presentTokens.Contains(e.Token));
            _entries.Insert(anchor is null ? _entries.Count : _entries.IndexOf(anchor), item.Entry);
            _presentTokens.Add(item.Entry.Token);
            if (_shuffle && item.PendingShuffle) AddToShuffleBag(item.Entry);
        }
        _removalUndo = null;
        Touch(itemsChanged: true);
        return true;
    }

    private int[] CheckedIndices(IEnumerable<int> indices)
    {
        ArgumentNullException.ThrowIfNull(indices);
        var selected = indices.Distinct().Order().ToArray();
        foreach (var index in selected) ValidateIndex(index);
        return selected;
    }
}
