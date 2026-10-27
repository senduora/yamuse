using System.Collections.Concurrent;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace YMusic.Native;

internal sealed record SmartPlaylistRule(Guid Id, string Name, bool FavoritesOnly = true,
    bool DownloadedOnly = false, IReadOnlyList<string>? ArtistIds = null, int? MaximumMinutes = null)
{
    public string Description => string.Join(" · ", new[]
    {
        FavoritesOnly ? "Любимые" : "Коллекция",
        DownloadedOnly ? "Скачанные" : null,
        ArtistIds is { Count: > 0 } ? $"Исполнителей: {ArtistIds.Count}" : null,
        MaximumMinutes is { } minutes ? $"Треки до {minutes} мин" : null
    }.Where(value => value is not null));
    public override string ToString() => Name;
}

// Only local rules are stored. Track metadata, credentials, file paths and audio
// URLs never enter this document. Every account has a separate hashed filename.
internal sealed class SmartPlaylistStore(string directory)
{
    internal const int MaximumRules = 128;
    internal const int MaximumBytes = 512 * 1024;
    internal const int MaximumArtists = 64;
    private readonly string _directory = Path.GetFullPath(directory);
    private static readonly ConcurrentDictionary<string, object> Gates = new(StringComparer.OrdinalIgnoreCase);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 12,
        WriteIndented = true
    };
    private sealed record Document(int Version, string AccountHash, IReadOnlyList<SmartPlaylistRule> Rules);

    internal string FilePath(string accountUid) => Path.Combine(_directory, "smart-playlists-" + AccountHash(accountUid) + ".json");

    public IReadOnlyList<SmartPlaylistRule> Load(string accountUid)
    {
        var path = FilePath(accountUid);
        lock (Gates.GetOrAdd(path, static _ => new object())) return Read(path, AccountHash(accountUid));
    }

    public IReadOnlyList<SmartPlaylistRule> Upsert(string accountUid, SmartPlaylistRule rule)
    {
        rule = Validate(rule);
        return Change(accountUid, rules =>
        {
            var result = rules.ToList();
            var index = result.FindIndex(item => item.Id == rule.Id);
            if (index < 0) result.Add(rule); else result[index] = rule;
            if (result.Count > MaximumRules) throw new InvalidOperationException($"Можно сохранить не больше {MaximumRules} локальных подборок.");
            return result;
        });
    }

    public IReadOnlyList<SmartPlaylistRule> Remove(string accountUid, Guid id)
        => Change(accountUid, rules => rules.Where(rule => rule.Id != id).ToArray());

    private IReadOnlyList<SmartPlaylistRule> Change(string accountUid,
        Func<IReadOnlyList<SmartPlaylistRule>, IReadOnlyList<SmartPlaylistRule>> change)
    {
        var path = FilePath(accountUid);
        var hash = AccountHash(accountUid);
        lock (Gates.GetOrAdd(path, static _ => new object()))
        {
            // Read under the same gate as replacement, so distinct store instances
            // cannot drop one another's edits. Invalid input is never overwritten.
            var rules = change(Read(path, hash)).Select(Validate).ToArray();
            var bytes = JsonSerializer.SerializeToUtf8Bytes(new Document(1, hash, rules), JsonOptions);
            if (bytes.Length > MaximumBytes) throw new InvalidDataException("Файл локальных подборок слишком большой.");
            Directory.CreateDirectory(_directory);
            var temporary = Path.Combine(_directory, ".smart-playlists-" + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                    4096, FileOptions.WriteThrough))
                {
                    stream.Write(bytes);
                    stream.Flush(flushToDisk: true);
                }
                if (File.Exists(path)) File.Replace(temporary, path, null);
                else File.Move(temporary, path);
            }
            finally
            {
                try { File.Delete(temporary); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            }
            return Array.AsReadOnly(rules);
        }
    }

    private static IReadOnlyList<SmartPlaylistRule> Read(string path, string hash)
    {
        if (!File.Exists(path)) return [];
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096);
            if (stream.Length is <= 0 or > MaximumBytes) throw new InvalidDataException("Недопустимый размер файла локальных подборок.");
            var document = JsonSerializer.Deserialize<Document>(stream, JsonOptions);
            if (document is null || document.Version != 1 || document.AccountHash != hash
                || document.Rules is null || document.Rules.Count > MaximumRules)
                throw new InvalidDataException("Файл локальных подборок повреждён или относится к другому аккаунту.");
            var rules = document.Rules.Select(Validate).ToArray();
            if (rules.Select(rule => rule.Id).Distinct().Count() != rules.Length)
                throw new InvalidDataException("В локальных подборках повторяются идентификаторы.");
            return Array.AsReadOnly(rules);
        }
        catch (Exception error) when (error is JsonException or ArgumentException or NullReferenceException)
        { throw new InvalidDataException("Не удалось прочитать правила локальных подборок. Исходный файл сохранён.", error); }
    }

    internal static SmartPlaylistRule Validate(SmartPlaylistRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        var name = rule.Name?.Trim();
        if (rule.Id == Guid.Empty || string.IsNullOrWhiteSpace(name) || name.Length > 100 || name.Any(char.IsControl))
            throw new ArgumentException("Название должно содержать от 1 до 100 символов.");
        if (rule.MaximumMinutes is < 1 or > 1440)
            throw new ArgumentException("Длительность трека — от 1 до 1440 минут.");
        var artists = rule.ArtistIds ?? [];
        if (artists.Count > MaximumArtists || artists.Any(id => string.IsNullOrWhiteSpace(id) || id.Length > 128 || id.Any(char.IsControl)))
            throw new ArgumentException($"Выберите не больше {MaximumArtists} исполнителей с корректными ID.");
        return rule with { Name = name, ArtistIds = Array.AsReadOnly(artists.Distinct(StringComparer.Ordinal).ToArray()) };
    }

    private static string AccountHash(string accountUid)
    {
        if (string.IsNullOrWhiteSpace(accountUid) || accountUid.Length > 256) throw new ArgumentException("Не указан аккаунт.", nameof(accountUid));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(accountUid))).ToLowerInvariant();
    }
}

internal static class SmartPlaylistEvaluator
{
    // Stable collection order; each exact request identity appears once. The
    // duration rule applies to EACH track; unknown durations cannot pass it.
    public static IReadOnlyList<TrackRow> Evaluate(SmartPlaylistRule rule, IEnumerable<TrackRow> collection,
        IReadOnlySet<string> favoriteIds)
    {
        rule = SmartPlaylistStore.Validate(rule);
        var artists = new HashSet<string>(rule.ArtistIds ?? [], StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var maximumDuration = rule.MaximumMinutes is { } minutes ? minutes * 60_000L : long.MaxValue;
        var result = new List<TrackRow>();
        foreach (var row in collection)
        {
            if (string.IsNullOrWhiteSpace(row.RequestId) || !seen.Add(row.RequestId)) continue;
            if (rule.FavoritesOnly && !favoriteIds.Contains(row.Id)) continue;
            if (rule.DownloadedOnly && !row.IsDownloaded && row.LocalPath is null) continue;
            if (artists.Count > 0 && !row.Artists.Any(artist => artists.Contains(artist.Id))) continue;
            if (rule.MaximumMinutes is not null && (row.DurationMs <= 0 || row.DurationMs > maximumDuration)) continue;
            result.Add(row);
        }
        return result.AsReadOnly();
    }
}
