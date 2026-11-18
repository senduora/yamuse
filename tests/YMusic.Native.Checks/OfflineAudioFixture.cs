using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using YMusic.Native;

internal static partial class Program
{
    private static void WriteFixtureAudioIndex(string musicDirectory, IReadOnlyList<TrackRow> rows, Func<int, int>? bitrate = null)
    {
        // The decoder consumes generated WAV files. The index records their
        // real size and digest plus the synthetic service quality for each test.
        var items = rows.Select((row, index) =>
        {
            var file = new FileInfo(row.LocalPath ?? throw new InvalidOperationException("Fixture audio has no path."));
            return new OfflineItem(row.Id, row.RequestId, row.Title, row.Artist, row.Album, row.DurationMs,
                file.Name, "mp3", bitrate?.Invoke(index) ?? 320, file.Length,
                Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file.FullName))), DateTimeOffset.UtcNow);
        }).ToArray();
        File.WriteAllText(Path.Combine(musicDirectory, "library.index.json"), JsonSerializer.Serialize(items));
    }
}
