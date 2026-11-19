using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using YMusic.Native;

internal static partial class Program
{
    private static void WriteFixtureAudioIndex(string musicDirectory, IReadOnlyList<TrackRow> rows,
        Func<int, int>? bitrate = null, bool confirmedMp3 = false)
    {
        // These are legacy rows unless the caller generated MP3 frames at the supplied rates.
        var items = rows.Select((row, index) =>
        {
            var file = new FileInfo(row.LocalPath ?? throw new InvalidOperationException("Fixture audio has no path."));
            var nominal = bitrate?.Invoke(index) ?? 320;
            return new OfflineItem(row.Id, row.RequestId, row.Title, row.Artist, row.Album, row.DurationMs,
                file.Name, "mp3", nominal, file.Length,
                Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file.FullName))), DateTimeOffset.UtcNow,
                ConfirmedBitrate: confirmedMp3 ? nominal : null);
        }).ToArray();
        File.WriteAllText(Path.Combine(musicDirectory, "library.index.json"), JsonSerializer.Serialize(items));
    }
}
