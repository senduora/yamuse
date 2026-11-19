using System.IO;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;
using Windows.Storage;

internal static partial class Program
{
    // MPEG-1 Layer III, 48 kHz, synthetic silence encoded by Windows. The
    // frame headers and ancillary padding below carry the requested rate.
    private static readonly byte[] SilentMp3FirstFrame = Convert.FromHexString(
        "fffbb440000004e0004b80000008000009700000010000012e00000020000025c0000004");
    private static readonly byte[] SilentMp3NextFrame = Convert.FromHexString(
        "fffbb440c0000ffc004b800000081c8009700000010000012e00000020000025c0000004");

    private static byte[] SilentMp3Seconds(int seconds, int bitrate = 320) => SilentMp3Bytes(seconds * bitrate * 125, bitrate);

    private static byte[] SilentMp3Bytes(int size, int bitrate = 320)
    {
        var rateIndex = bitrate switch
        {
            128 => 9, 160 => 10, 192 => 11, 320 => 14,
            _ => throw new ArgumentOutOfRangeException(nameof(bitrate))
        };
        var frameBytes = bitrate * 3; // 1152 samples at 48 kHz
        var frames = Math.DivRem(size, frameBytes, out var prefixBytes);
        if (prefixBytes is > 0 and < 10) { frames--; prefixBytes += frameBytes; }
        if (frames < 2) throw new ArgumentOutOfRangeException(nameof(size));
        var bytes = new byte[size];
        // An ID3v2.3 padding-only tag preserves exact byte-count fixtures.
        if (prefixBytes != 0)
        {
            "ID3"u8.CopyTo(bytes);
            bytes[3] = 3;
            bytes[8] = (byte)((prefixBytes - 10) >> 7);
            bytes[9] = (byte)((prefixBytes - 10) & 127);
        }
        bytes.AsSpan(prefixBytes).Fill(0xff);
        for (var i = 0; i < frames; i++)
        {
            var offset = prefixBytes + i * frameBytes;
            (i == 0 ? SilentMp3FirstFrame : SilentMp3NextFrame).CopyTo(bytes, offset);
            bytes[offset + 2] = (byte)((rateIndex << 4) | 4);
        }
        return bytes;
    }

    private static async Task<byte[]> EncodeAudioFixtureAsync(string extension, MediaEncodingProfile profile, int seconds = 1)
    {
        var prefix = "codec-fixture-" + Guid.NewGuid().ToString("N");
        var wave = WriteWave(prefix + ".wav", seconds);
        var folder = await StorageFolder.GetFolderFromPathAsync(_directory);
        var target = await folder.CreateFileAsync(prefix + "." + extension, CreationCollisionOption.FailIfExists);
        var encode = await new MediaTranscoder { HardwareAccelerationEnabled = false }
            .PrepareFileTranscodeAsync(await StorageFile.GetFileFromPathAsync(wave), target, profile);
        Assert(encode.CanTranscode, "Windows could not encode the synthetic " + extension + " fixture");
        await encode.TranscodeAsync();
        return await File.ReadAllBytesAsync(target.Path);
    }
}
