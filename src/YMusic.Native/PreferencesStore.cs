using System.IO;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace YMusic.Native;

internal sealed record PlayerPreferences
{
    public const int DefaultConcurrentDownloads = 10;
    public AudioQuality Quality { get; init; } = AudioQuality.Excellent;
    public double Volume { get; init; } = 0.5;
    public double PlaybackSpeed { get; init; } = 1;
    public bool MinimizeToTray { get; init; }
    public bool AutoDownloadFavorites { get; init; } // Opt in; existing settings default to false.
    public int ConcurrentDownloads { get; init; } = DefaultConcurrentDownloads;
    public double CrossfadeSeconds { get; init; } = 3;
    public bool NormalizeVolume { get; init; }
    public bool AccentFromCover { get; init; } = true;
    public string StaticAccentColor { get; init; } = "#EA571C";
}

// This file contains playback preferences only; authentication remains in SessionStore.
internal sealed class PreferencesStore
{
    private const int MaximumFileBytes = 64 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter<AudioQuality>(allowIntegerValues: false) }
    };
    private static readonly ConcurrentDictionary<string, object> Gates = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate;
    internal string FilePath { get; }
    public bool LastLoadRecovered { get; private set; }
    public string? CorruptBackupPath { get; private set; }

    public PreferencesStore(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        FilePath = Path.Combine(Path.GetFullPath(directory), "settings.json");
        _gate = Gates.GetOrAdd(FilePath, static _ => new object());
    }

    public PlayerPreferences Load()
    {
        lock (_gate)
        {
            LastLoadRecovered = false;
            if (!File.Exists(FilePath)) return new();
            if (TryRead(FilePath, out var preferences)) return Normalize(preferences!);
            // Keep the original untouched. Save() moves the exact original to a
            // unique backup atomically before replacing a malformed settings file.
            LastLoadRecovered = true;
            return new();
        }
    }

    public void Save(PlayerPreferences preferences)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        lock (_gate)
        {
            var directory = Path.GetDirectoryName(FilePath)!;
            Directory.CreateDirectory(directory);
            var temporary = Path.Combine(directory, $".settings-{Guid.NewGuid():N}.tmp");
            try
            {
                var bytes = JsonSerializer.SerializeToUtf8Bytes(Normalize(preferences), JsonOptions);
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                    FileShare.None, 4096, FileOptions.WriteThrough))
                {
                    stream.Write(bytes);
                    stream.Flush(flushToDisk: true);
                }
                if (File.Exists(FilePath))
                {
                    var backup = TryRead(FilePath, out _) ? null
                        : Path.Combine(directory, $"settings.corrupt-{DateTime.UtcNow:yyyyMMddTHHmmssfffffffZ}-{Guid.NewGuid():N}.json");
                    // Windows ReplaceFile preserves the destination on failure;
                    // recovery also retains the exact malformed original. Stores
                    // sharing this path serialize their opens with the same gate.
                    File.Replace(temporary, FilePath, backup);
                    if (backup is not null) CorruptBackupPath = backup;
                }
                else File.Move(temporary, FilePath);
            }
            finally
            {
                try { File.Delete(temporary); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
    }

    public static PlayerPreferences Normalize(PlayerPreferences preferences)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        return preferences with
        {
            Quality = Enum.IsDefined(preferences.Quality) ? preferences.Quality : AudioQuality.Excellent,
            Volume = double.IsFinite(preferences.Volume) ? Math.Clamp(preferences.Volume, 0, 1) : 0.5,
            PlaybackSpeed = double.IsFinite(preferences.PlaybackSpeed) ? Math.Clamp(preferences.PlaybackSpeed, 0.5, 2) : 1,
            ConcurrentDownloads = Math.Clamp(preferences.ConcurrentDownloads, 1, 10),
            CrossfadeSeconds = double.IsFinite(preferences.CrossfadeSeconds) ? Math.Clamp(preferences.CrossfadeSeconds, 0, 10) : 3,
            StaticAccentColor = IsAccentColor(preferences.StaticAccentColor) ? preferences.StaticAccentColor.ToUpperInvariant() : "#EA571C"
        };
    }

    internal static bool IsAccentColor(string? value) => value is { Length: 7 } && value[0] == '#'
        && value.AsSpan(1).IndexOfAnyExcept("0123456789abcdefABCDEF") < 0;

    private static bool TryRead(string path, out PlayerPreferences? preferences)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        preferences = null;
        if (stream.Length > MaximumFileBytes) return false;
        try
        {
            preferences = JsonSerializer.Deserialize<PlayerPreferences>(stream, JsonOptions);
            return preferences is not null;
        }
        catch (JsonException) { return false; }
    }
}
