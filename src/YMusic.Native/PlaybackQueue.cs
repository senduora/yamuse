namespace YMusic.Native;

internal enum RepeatMode { Off, All, One }

// An in-memory navigation model. Own it on the WPF dispatcher; it does not play audio,
// change TrackRow.IsPlaying, persist data, or send listening/feedback requests.
internal sealed partial class PlaybackQueue
{
    private sealed record Entry(long Token, TrackRow Track);
    private readonly Random _random;
    private readonly List<Entry> _entries = [];
    private readonly List<Entry> _past = [];
    private readonly List<Entry> _future = [];
    private readonly List<Entry> _shuffleBag = [];
    private readonly List<Entry> _playNext = [];
    private List<Entry>? _reservedShuffleCycle;
    private readonly HashSet<long> _cycleVisited = [];
    private readonly HashSet<long> _presentTokens = [];
    private const int NavigationHistoryLimit = 1000;
    private IReadOnlyList<TrackRow> _items = Array.AsReadOnly(Array.Empty<TrackRow>());
    private IReadOnlyList<TrackRow> _history = Array.AsReadOnly(Array.Empty<TrackRow>());
    private Entry? _current;
    private long _nextToken;
    private bool _shuffle;
    private RepeatMode _repeat;

    public PlaybackQueue(Random? random = null) => _random = random ?? Random.Shared;

    public IReadOnlyList<TrackRow> Items => _items;
    // Up to 1000 prior navigation steps, oldest first, excluding Current. Previous/
    // Next can replay that path; this is not the service's persistent listening history.
    public IReadOnlyList<TrackRow> History => _history;
    public TrackRow? Current => _current?.Track;
    public long? CurrentOccurrenceToken => _current?.Token;
    public int CurrentIndex => _current is null ? -1 : _entries.IndexOf(_current);
    public long Version { get; private set; }
    public bool CanPrevious => Current is not null || _past.Any(IsPlayable);
    public bool CanNext => _future.Any(IsPlayable)
        || (_shuffle ? _playNext.Any(IsPlayable) || _shuffleBag.Any(IsPlayable) || _repeat == RepeatMode.All && _entries.Any(IsPlayable)
            : FindSequential(_current is null ? 0 : CurrentIndex + 1, _repeat == RepeatMode.All) is not null);

    public bool Shuffle
    {
        get => _shuffle;
        set
        {
            if (_shuffle == value) return;
            _shuffle = value;
            _shuffleBag.Clear();
            _playNext.Clear();
            _cycleVisited.Clear();
            if (_current is not null) _cycleVisited.Add(_current.Token);
            if (value) BuildShuffleBag();
            Touch();
        }
    }

    public RepeatMode Repeat
    {
        get => _repeat;
        set
        {
            if (!Enum.IsDefined(value)) throw new ArgumentOutOfRangeException(nameof(value));
            if (_repeat == value) return;
            _repeat = value;
            Touch();
        }
    }

    public void SetItems(IEnumerable<TrackRow> tracks, TrackRow? selected = null)
    {
        var rows = Materialize(tracks);
        var selectedIndex = selected is null ? 0 : Array.FindIndex(rows, row => ReferenceEquals(row, selected));
        if (selected is not null && selectedIndex < 0)
            throw new ArgumentException("Выбранного экземпляра трека нет в новой очереди.", nameof(selected));
        _removalUndo = null;
        _entries.Clear();
        _presentTokens.Clear();
        _past.Clear();
        _future.Clear();
        _shuffleBag.Clear();
        _playNext.Clear();
        _cycleVisited.Clear();
        foreach (var row in rows) _entries.Add(NewEntry(row));
        // An unavailable selected row is skipped forward, with one bounded wrap to
        // find the first playable row. All unavailable rows remain visible in Items.
        _current = FindSequential(selectedIndex, wrap: true);
        MarkVisited(_current);
        if (_shuffle) BuildShuffleBag();
        Touch(itemsChanged: true);
    }

    // Restore an exact occurrence without playing, skipping an unavailable current
    // row, or treating equal IDs as the same entry. Navigation history is ephemeral.
    public void RestoreItems(IEnumerable<TrackRow> tracks, int currentIndex, bool shuffle, RepeatMode repeat)
    {
        var rows = Materialize(tracks);
        if (currentIndex < -1 || currentIndex >= rows.Length) throw new ArgumentOutOfRangeException(nameof(currentIndex));
        if (!Enum.IsDefined(repeat)) throw new ArgumentOutOfRangeException(nameof(repeat));
        _removalUndo = null;
        _entries.Clear(); _presentTokens.Clear(); _past.Clear(); _future.Clear();
        _shuffleBag.Clear(); _playNext.Clear(); _cycleVisited.Clear();
        foreach (var row in rows) _entries.Add(NewEntry(row));
        _current = currentIndex < 0 ? null : _entries[currentIndex];
        _shuffle = shuffle; _repeat = repeat;
        MarkVisited(_current);
        if (_shuffle) BuildShuffleBag();
        Touch(itemsChanged: true);
    }

    public bool Select(TrackRow track)
    {
        ArgumentNullException.ThrowIfNull(track);
        var index = IndexOfReference(track);
        if (index < 0) throw new ArgumentException("Экземпляра трека нет в очереди.", nameof(track));
        return SelectAt(index);
    }

    public bool SelectAt(int index)
    {
        ValidateIndex(index);
        var entry = _entries[index];
        if (!IsPlayable(entry)) return false;
        if (ReferenceEquals(_current, entry)) return true;
        _future.Clear();
        Transition(entry);
        Touch();
        return true;
    }

    public void Enqueue(TrackRow track, bool playNext = false)
    {
        ArgumentNullException.ThrowIfNull(track);
        var entry = NewEntry(track);
        if (playNext)
        {
            _entries.Insert(_current is null ? 0 : CurrentIndex + 1, entry);
            _future.Clear();
            if (_shuffle && IsPlayable(entry)) _playNext.Insert(0, entry);
        }
        else
        {
            _entries.Add(entry);
            AddToShuffleBag(entry);
        }
        if (_current is null && IsPlayable(entry))
        {
            _current = entry;
            MarkVisited(entry);
        }
        Touch(itemsChanged: true);
    }

    // Returns Current after removal. Removing a non-current entry does not move it.
    // Removing Current selects the next playable occurrence in the active playback
    // order; if none exists, Current becomes null. The caller updates/stops the player.
    public TrackRow? Remove(TrackRow track)
    {
        ArgumentNullException.ThrowIfNull(track);
        var index = IndexOfReference(track);
        if (index < 0) throw new ArgumentException("Экземпляра трека нет в очереди.", nameof(track));
        return RemoveAt(index);
    }

    public TrackRow? RemoveAt(int index)
    {
        ValidateIndex(index);
        _removalUndo = null;
        var removed = _entries[index];
        var wasCurrent = ReferenceEquals(removed, _current);
        _entries.RemoveAt(index);
        Forget(removed);
        if (wasCurrent)
        {
            _current = null;
            var replacement = PopPlayable(_future)
                ?? (_shuffle ? TakeShuffle(_repeat == RepeatMode.All, avoidFirst: null)
                    : FindSequential(index, _repeat == RepeatMode.All));
            _current = replacement;
            MarkVisited(replacement);
        }
        Touch(itemsChanged: true);
        return Current;
    }

    // to is the final zero-based index after removal/insertion, not an insertion gap.
    // Move never reselects Current, rewrites history, or reseeds a shuffle pass.
    public void Move(int from, int to)
    {
        ValidateIndex(from);
        ValidateIndex(to);
        if (from == to) return;
        var entry = _entries[from];
        _entries.RemoveAt(from);
        _entries.Insert(to, entry);
        Touch(itemsChanged: true);
    }

    // Does not navigate, change Version/history, or deduplicate equal TrackRows.
    // A shuffle repeat boundary reserves the next cycle for the following Next.
    public TrackRow? PeekNext(bool naturalEnd = false) => PeekNextEntry(naturalEnd)?.Track;
    public long? PeekNextOccurrenceToken(bool naturalEnd = false) => PeekNextEntry(naturalEnd)?.Token;
    private Entry? PeekNextEntry(bool naturalEnd)
    {
        if (naturalEnd && _repeat == RepeatMode.One && _current is not null && IsPlayable(_current)) return _current;
        var future = _future.LastOrDefault(IsPlayable);
        if (future is not null) return future;
        if (!_shuffle) return FindSequential(_current is null ? 0 : CurrentIndex + 1, _repeat == RepeatMode.All);
        var next = _playNext.FirstOrDefault(IsPlayable) ?? _shuffleBag.FirstOrDefault(IsPlayable);
        if (next is not null || _repeat != RepeatMode.All) return next;
        return ReservedShuffleCycle(_current).FirstOrDefault(IsPlayable);
    }

    public TrackRow? Next(bool naturalEnd = false)
    {
        if (naturalEnd && _repeat == RepeatMode.One && _current is not null && IsPlayable(_current))
            return Current;
        var next = PopPlayable(_future)
            ?? (_shuffle ? TakeShuffle(_repeat == RepeatMode.All, _current)
                : FindSequential(_current is null ? 0 : CurrentIndex + 1, _repeat == RepeatMode.All));
        // Off/explicit Next under Repeat.One: preserve the final Current so a wave
        // can append to its tail. null means stop/obtain more, not a fabricated track.
        if (next is null) return null;
        Transition(next);
        Touch();
        return Current;
    }

    public TrackRow? Previous(double positionSeconds = 0)
    {
        if (!double.IsFinite(positionSeconds) || positionSeconds < 0)
            throw new ArgumentOutOfRangeException(nameof(positionSeconds));
        if (positionSeconds > 3 && _current is not null) return Current;
        var previous = PopPlayable(_past);
        if (previous is null) return Current; // Restart first; do not invent unplayed history.
        if (_current is not null) _future.Add(_current);
        _current = previous;
        MarkVisited(previous);
        Touch();
        return Current;
    }

    // Intended for wave continuation. Keep at most 20 previous navigation steps and
    // at most 300 queue occurrences. Never silently discard an unplayed/future item
    // or part of an incoming batch: capacity failure leaves the entire model unchanged.
    public void Append(IEnumerable<TrackRow> tracks)
    {
        var rows = Materialize(tracks);
        var keepPast = _past.TakeLast(20).ToArray();
        var protectedTokens = keepPast.Select(e => e.Token).Concat(_future.Select(e => e.Token)).ToHashSet();
        if (_current is not null) protectedTokens.Add(_current.Token);
        if (_shuffle)
            foreach (var entry in _shuffleBag.Concat(_playNext)) protectedTokens.Add(entry.Token);
        var evict = _past.Take(Math.Max(0, _past.Count - 20)).Select(e => e.Token)
            .Where(token => !protectedTokens.Contains(token)).ToHashSet();
        var survivingCount = _entries.Count(e => !evict.Contains(e.Token));
        if ((long)survivingCount + rows.Length > 300)
            throw new InvalidOperationException("Продолжение не помещается в очередь из 300 элементов без потери ожидающих треков. Новая партия не добавлена.");
        _entries.RemoveAll(e => evict.Contains(e.Token));
        _presentTokens.RemoveWhere(evict.Contains);
        _shuffleBag.RemoveAll(e => evict.Contains(e.Token));
        _playNext.RemoveAll(e => evict.Contains(e.Token));
        _cycleVisited.RemoveWhere(evict.Contains);
        _past.Clear();
        _past.AddRange(keepPast);
        Entry? firstNewPlayable = null;
        foreach (var row in rows)
        {
            var entry = NewEntry(row);
            _entries.Add(entry);
            AddToShuffleBag(entry);
            if (firstNewPlayable is null && IsPlayable(entry)) firstNewPlayable = entry;
        }
        if (_current is null && firstNewPlayable is not null)
        {
            _current = firstNewPlayable;
            MarkVisited(firstNewPlayable);
        }
        Touch(itemsChanged: true);
    }

    private Entry NewEntry(TrackRow track)
    {
        var entry = new Entry(++_nextToken, track);
        _presentTokens.Add(entry.Token);
        return entry;
    }

    private void Transition(Entry next)
    {
        if (_current is not null && !ReferenceEquals(_current, next))
        {
            _past.Add(_current);
            if (_past.Count > NavigationHistoryLimit) _past.RemoveRange(0, _past.Count - NavigationHistoryLimit);
        }
        _current = next;
        MarkVisited(next);
    }

    private void MarkVisited(Entry? entry)
    {
        if (entry is null) return;
        _cycleVisited.Add(entry.Token);
        _shuffleBag.RemoveAll(e => e.Token == entry.Token);
        _playNext.RemoveAll(e => e.Token == entry.Token);
    }

    private Entry? FindSequential(int start, bool wrap)
    {
        if (_entries.Count == 0) return null;
        for (var step = 0; step < _entries.Count; step++)
        {
            var index = start + step;
            if (index >= _entries.Count)
            {
                if (!wrap) return null;
                index %= _entries.Count;
            }
            if (IsPlayable(_entries[index])) return _entries[index];
        }
        return null;
    }

    private Entry? TakeShuffle(bool allowNewCycle, Entry? avoidFirst)
    {
        var next = ShiftPlayable(_playNext) ?? ShiftPlayable(_shuffleBag);
        if (next is not null || !allowNewCycle) return next;
        _cycleVisited.Clear();
        _shuffleBag.Clear();
        _shuffleBag.AddRange(ReservedShuffleCycle(avoidFirst));
        _reservedShuffleCycle = null;
        return ShiftPlayable(_shuffleBag);
    }

    private List<Entry> ReservedShuffleCycle(Entry? avoidFirst)
    {
        // TrackRow.CanPlay can change after an offline download without queue
        // navigation. Do not retain a reservation made for different playable rows.
        if (_reservedShuffleCycle is null || !_reservedShuffleCycle.Select(e => e.Token).ToHashSet()
            .SetEquals(_entries.Where(IsPlayable).Select(e => e.Token)))
            _reservedShuffleCycle = NewShuffleCycle(avoidFirst);
        return _reservedShuffleCycle;
    }

    private List<Entry> NewShuffleCycle(Entry? avoidFirst)
    {
        var cycle = _entries.Where(IsPlayable).ToList();
        for (var i = cycle.Count - 1; i > 0; i--)
        {
            var swap = _random.Next(i + 1);
            (cycle[i], cycle[swap]) = (cycle[swap], cycle[i]);
        }
        // At a repeat-all boundary avoid an immediate duplicate when another
        // playable occurrence exists; occurrences of the same TrackRow still differ.
        if (cycle.Count > 1 && ReferenceEquals(cycle[0], avoidFirst))
        {
            var swap = 1 + _random.Next(cycle.Count - 1);
            (cycle[0], cycle[swap]) = (cycle[swap], cycle[0]);
        }
        return cycle;
    }

    private void BuildShuffleBag()
    {
        _shuffleBag.Clear();
        _shuffleBag.AddRange(_entries.Where(e => IsPlayable(e) && !_cycleVisited.Contains(e.Token)));
        for (var i = _shuffleBag.Count - 1; i > 0; i--)
        {
            var swap = _random.Next(i + 1);
            (_shuffleBag[i], _shuffleBag[swap]) = (_shuffleBag[swap], _shuffleBag[i]);
        }
    }

    private void AddToShuffleBag(Entry entry)
    {
        if (_shuffle && IsPlayable(entry)) _shuffleBag.Insert(_random.Next(_shuffleBag.Count + 1), entry);
    }

    private Entry? PopPlayable(List<Entry> stack)
    {
        while (stack.Count > 0)
        {
            var entry = stack[^1];
            stack.RemoveAt(stack.Count - 1);
            if (IsPlayable(entry)) return entry;
        }
        return null;
    }

    private Entry? ShiftPlayable(List<Entry> list)
    {
        while (list.Count > 0)
        {
            var entry = list[0];
            list.RemoveAt(0);
            if (IsPlayable(entry)) return entry;
        }
        return null;
    }

    private bool IsPlayable(Entry entry) => entry.Track.CanPlay && _presentTokens.Contains(entry.Token);

    private int IndexOfReference(TrackRow track)
    {
        // Reference identity, not RequestId. Duplicate occurrences have separate tokens.
        if (_current is not null && ReferenceEquals(_current.Track, track)) return CurrentIndex;
        return _entries.FindIndex(e => ReferenceEquals(e.Track, track));
    }

    private void Forget(Entry entry)
    {
        _presentTokens.Remove(entry.Token);
        _past.RemoveAll(e => e.Token == entry.Token);
        _future.RemoveAll(e => e.Token == entry.Token);
        _shuffleBag.RemoveAll(e => e.Token == entry.Token);
        _playNext.RemoveAll(e => e.Token == entry.Token);
        _cycleVisited.Remove(entry.Token);
    }

    private static TrackRow[] Materialize(IEnumerable<TrackRow> tracks)
    {
        ArgumentNullException.ThrowIfNull(tracks);
        var rows = tracks.ToArray();
        if (rows.Any(row => row is null)) throw new ArgumentException("Очередь не может содержать null вместо трека.", nameof(tracks));
        return rows;
    }
    private void ValidateIndex(int index)
    { if (index < 0 || index >= _entries.Count) throw new ArgumentOutOfRangeException(nameof(index)); }
    private void Touch(bool itemsChanged = false)
    {
        _reservedShuffleCycle = null;
        if (itemsChanged) _items = Array.AsReadOnly(_entries.Select(e => e.Track).ToArray());
        _history = Array.AsReadOnly(_past.Select(e => e.Track).ToArray());
        Version++;
    }
}
