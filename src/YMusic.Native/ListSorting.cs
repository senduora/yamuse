using System.Collections;
using System.Collections.Specialized;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace YMusic.Native;

internal enum ListSortField { Original, Title, Artist, Album, Duration, Downloaded, State, Progress, Size, Error }
internal sealed record ListSortRule(ListSortField Field = ListSortField.Original, bool Descending = false);
internal sealed record ListSortOption(ListSortField Field, string Label)
{
    public override string ToString() => Label;
}

// Track source positions separately from a view's order. Subscribe before WPF's
// view so an incoming page has stable ranks when its sorted insertions are handled.
internal sealed class ListSourceOrder : IDisposable
{
    private readonly IList _source;
    private readonly INotifyCollectionChanged _notifications;
    private readonly Dictionary<object, int> _ranks = new(ReferenceEqualityComparer.Instance);
    internal ListSourceOrder(IList source)
    {
        _source = source; _notifications = (INotifyCollectionChanged)source;
        Rebuild(); _notifications.CollectionChanged += Changed;
    }
    internal int Rank(object item) => _ranks.GetValueOrDefault(item, int.MaxValue);
    private void Changed(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Add && e.NewStartingIndex + e.NewItems!.Count == _source.Count)
        {
            for (var i = 0; i < e.NewItems.Count; i++) _ranks.TryAdd(e.NewItems[i]!, e.NewStartingIndex + i);
        }
        else Rebuild();
    }
    private void Rebuild()
    { _ranks.Clear(); for (var i = 0; i < _source.Count; i++) _ranks.TryAdd(_source[i]!, i); }
    public void Dispose() => _notifications.CollectionChanged -= Changed;
}

internal sealed class StableListComparer(ListSortRule rule, ListSourceOrder order,
    IReadOnlyDictionary<string, int>? groups = null) : IComparer
{
    private readonly CompareInfo _text = CultureInfo.CurrentCulture.CompareInfo;
    public int Compare(object? x, object? y)
    {
        if (ReferenceEquals(x, y)) return 0;
        if (x is null) return 1;
        if (y is null) return -1;
        if (groups is not null && x is CatalogItem a && y is CatalogItem b)
        {
            var grouped = groups.GetValueOrDefault(a.Kind, int.MaxValue).CompareTo(groups.GetValueOrDefault(b.Kind, int.MaxValue));
            if (grouped != 0) return grouped;
        }
        var left = Key(x); var right = Key(y);
        // Empty metadata remains last in either direction; no fabricated dates,
        // artist identities, or display-string parsing enters a comparison.
        var missingLeft = left is null || left is string { Length: 0 };
        var missingRight = right is null || right is string { Length: 0 };
        if (missingLeft != missingRight) return missingLeft ? 1 : -1;
        var compared = missingLeft ? 0 : left is string ls && right is string rs
            ? _text.Compare(ls, rs, CompareOptions.IgnoreCase) : ((IComparable)left!).CompareTo(right);
        if (compared != 0) return rule.Descending ? -Math.Sign(compared) : Math.Sign(compared);
        return order.Rank(x).CompareTo(order.Rank(y));
    }
    private object? Key(object value) => value switch
    {
        TrackRow track => TrackKey(track),
        CatalogItem item => rule.Field switch
        {
            ListSortField.Title => item.Title,
            ListSortField.Artist => item.Track?.Artist ?? (item.Kind == "album" ? item.Subtitle : item.Kind == "artist" ? item.Title : null),
            _ => item.Track is { } track ? TrackKey(track) : null
        },
        DownloadEntry entry => rule.Field switch
        {
            ListSortField.Title => entry.Title, ListSortField.Artist => entry.Artist,
            ListSortField.State => entry.StatusText, ListSortField.Progress => entry.TotalBytes is > 0 || entry.State == DownloadEntryState.Completed ? entry.Percent : null,
            ListSortField.Size => entry.TotalBytes ?? (entry.Bytes > 0 ? entry.Bytes : null), ListSortField.Error => entry.Error, _ => null
        },
        _ => null
    };
    private object? TrackKey(TrackRow track) => rule.Field switch
    {
        ListSortField.Title => track.Title, ListSortField.Artist => track.Artist, ListSortField.Album => track.Album,
        ListSortField.Duration => track.DurationMs > 0 ? track.DurationMs : null,
        ListSortField.Downloaded => track.IsDownloaded, _ => null
    };
}

internal sealed class ListSortStore(string directory)
{
    private readonly string _path = Path.Combine(directory, "list-sorting.json");
    private static readonly JsonSerializerOptions Json = new()
    { WriteIndented = true, Converters = { new JsonStringEnumConverter<ListSortField>(allowIntegerValues: false) } };
    internal static bool ValidContext(string key) => key is
        "tracks:favorites" or "tracks:offline" or "tracks:playlist-detail" or "tracks:offline-playlist-detail" or
        "tracks:album-detail" or "tracks:track-detail" or "tracks:artist-tracks" or "tracks:queue" or "tracks:queue-history" or "tracks:wave" or "tracks:home-shelf" or
        "catalog:playlists" or "catalog:offline-playlists" or "catalog:albums" or "catalog:artists" or "catalog:home-shelf" or
        "catalog:search:all" or "catalog:search:track" or "catalog:search:artist" or "catalog:search:album" or
        "catalog:search:playlist" or "catalog:search:collections" or "downloads";
    internal Dictionary<string, ListSortRule> Load()
    {
        try
        {
            if (!File.Exists(_path) || new FileInfo(_path).Length > 8192) return [];
            var values = JsonSerializer.Deserialize<Dictionary<string, ListSortRule?>>(File.ReadAllBytes(_path), Json);
            return values?.Where(p => ValidContext(p.Key) && p.Value is { } rule && Enum.IsDefined(rule.Field))
                .ToDictionary(p => p.Key, p => Normalize(p.Value!), StringComparer.Ordinal) ?? [];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return []; }
    }
    internal void Save(IReadOnlyDictionary<string, ListSortRule> rules)
    {
        var safe = rules.Where(p => ValidContext(p.Key) && p.Value is { } rule && Enum.IsDefined(rule.Field))
            .ToDictionary(p => p.Key, p => Normalize(p.Value), StringComparer.Ordinal);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(safe, Json);
        if (bytes.Length > 8192) throw new IOException("Слишком большой файл сортировки.");
        Directory.CreateDirectory(directory);
        var temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { file.Write(bytes); file.Flush(true); }
            if (File.Exists(_path)) File.Replace(temporary, _path, null);
            else File.Move(temporary, _path);
        }
        finally { AudioBuffer.Delete(temporary); }
    }
    private static ListSortRule Normalize(ListSortRule rule) => rule.Field == ListSortField.Original ? new() : rule;
}
