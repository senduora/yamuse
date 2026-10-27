namespace YMusic.Native;

internal sealed record QuickAccessEntry(string Key, string Kind, string Title, string Subtitle,
    IReadOnlyList<TrackRow> Tracks, CatalogItem? Catalog = null, Guid? SmartRuleId = null)
{
    public string TypeLabel => Kind switch
    { "track" => "Трек", "album" => "Альбом", "artist" => "Исполнитель", "smart" => "Локальная подборка", _ => "Плейлист" };
    public override string ToString() => Title;
}

internal static class QuickAccessIndex
{
    internal static string CatalogKey(CatalogItem item) => item.Kind + ":" + (item.Owner ?? "") + ":" + (item.PlaylistKind ?? item.Id);

    public static IReadOnlyList<QuickAccessEntry> Build(IEnumerable<TrackRow> source,
        IEnumerable<CatalogItem> collection, IReadOnlyDictionary<string, IReadOnlyList<TrackRow>> playlistTracks)
    {
        var tracks = source.Where(track => !string.IsNullOrWhiteSpace(track.RequestId))
            .DistinctBy(track => track.RequestId, StringComparer.Ordinal).ToArray();
        var entries = new Dictionary<string, QuickAccessEntry>(StringComparer.Ordinal);
        foreach (var track in tracks)
            entries["track:" + track.RequestId] = new("track:" + track.RequestId, "track", track.Title,
                string.Join(" · ", new[] { track.Artist, track.Album }.Where(value => !string.IsNullOrWhiteSpace(value))), [track]);
        foreach (var album in tracks.Where(track => !string.IsNullOrWhiteSpace(track.AlbumId)).GroupBy(track => track.AlbumId!, StringComparer.Ordinal))
        {
            var first = album.First();
            var catalog = new CatalogItem(album.Key, "album", first.Album, first.Artist);
            var key = CatalogKey(catalog);
            entries[key] = new(key, "album", first.Album, first.Artist, album.ToArray(), catalog);
        }
        foreach (var artist in tracks.SelectMany(track => track.Artists.Select(artist => (Artist: artist, Track: track)))
            .Where(pair => !string.IsNullOrWhiteSpace(pair.Artist.Id)).GroupBy(pair => pair.Artist.Id, StringComparer.Ordinal))
        {
            var name = artist.First().Artist.Name;
            var rows = artist.Select(pair => pair.Track).DistinctBy(track => track.RequestId, StringComparer.Ordinal).ToArray();
            var catalog = new CatalogItem(artist.Key, "artist", name, $"В коллекции: {rows.Length:N0}");
            var key = CatalogKey(catalog);
            entries[key] = new(key, "artist", name, catalog.Subtitle, rows, catalog);
        }
        foreach (var item in collection.Where(item => item.Kind is "album" or "artist" or "playlist" or "offline-playlist"))
        {
            var key = CatalogKey(item);
            var rows = entries.TryGetValue(key, out var existing) ? existing.Tracks
                : playlistTracks.TryGetValue(key, out var saved) ? saved : [];
            entries[key] = new(key, item.Kind, item.Title, item.DisplaySubtitle, rows, item);
        }
        return entries.Values.ToArray();
    }

    public static IReadOnlyList<QuickAccessEntry> Search(IEnumerable<QuickAccessEntry> entries, string query,
        string kind = "all", int maximum = 60)
    {
        var normalized = query.Trim();
        var tokens = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return entries.Where(entry => kind == "all" || entry.Kind == kind || kind == "playlist" && entry.Kind is "smart" or "offline-playlist")
            .Where(entry => tokens.All(token => entry.Title.Contains(token, StringComparison.CurrentCultureIgnoreCase)
                || entry.Subtitle.Contains(token, StringComparison.CurrentCultureIgnoreCase)))
            .OrderBy(entry => normalized.Length > 0 && entry.Title.StartsWith(normalized, StringComparison.CurrentCultureIgnoreCase) ? 0 : 1)
            .ThenBy(entry => entry.Title, StringComparer.CurrentCultureIgnoreCase)
            .Take(Math.Clamp(maximum, 1, 200)).ToArray();
    }
}
