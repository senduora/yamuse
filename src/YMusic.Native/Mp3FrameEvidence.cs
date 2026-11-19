using System.Buffers.Binary;

namespace YMusic.Native;

internal sealed record Mp3FrameEvidence(TimeSpan Duration, double KilobitsPerSecond, bool Variable)
{
    private static readonly int[] Mpeg1Rates = [0, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320];
    private static readonly int[] Mpeg2Rates = [0, 8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 144, 160];
    private static readonly int[] Mpeg1SampleRates = [44100, 48000, 32000];

    internal static (Mp3FrameEvidence? Frames, bool IncompleteFrame) Read(string path, long start, CancellationToken token)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.SequentialScan);
        var end = stream.Length;
        Span<byte> frame = stackalloc byte[1441]; // largest Layer III frame including padding
        stream.Position = start;
        int frames = 0, firstRate = 0;
        double seconds = 0, weightedRates = 0;
        var variable = false;
        while (stream.Position < end)
        {
            if ((frames & 1023) == 0) token.ThrowIfCancellationRequested();
            var remaining = end - stream.Position;
            if (remaining < 4) return default;
            stream.ReadExactly(frame[..4]);
            // A complete ID3v1 trailer begins at an established frame boundary.
            if (remaining == 128 && frame[..3].SequenceEqual("TAG"u8)) break;
            var header = BinaryPrimitives.ReadUInt32BigEndian(frame);
            if ((header & 0xffe60000) != 0xffe20000) return default;
            var version = (header >> 19) & 3;
            var rateIndex = (header >> 12) & 15;
            var sampleIndex = (header >> 10) & 3;
            if (version == 1 || rateIndex is 0 or 15 || sampleIndex == 3) return default;
            var mpeg1 = version == 3;
            var rate = (mpeg1 ? Mpeg1Rates : Mpeg2Rates)[rateIndex];
            var samplesPerSecond = Mpeg1SampleRates[sampleIndex] / (mpeg1 ? 1 : version == 2 ? 2 : 4);
            var length = (mpeg1 ? 144000 : 72000) * rate / samplesPerSecond + (int)((header >> 9) & 1);
            if (length > frame.Length) return default;
            if (length > remaining) return (null, true);
            stream.ReadExactly(frame[..(length - 4)]);
            var frameSeconds = (mpeg1 ? 1152d : 576d) / samplesPerSecond;
            seconds += frameSeconds;
            weightedRates += frameSeconds * rate;
            if (frames++ == 0) firstRate = rate;
            else variable |= firstRate != rate;
        }
        return frames >= 2 ? (new(TimeSpan.FromSeconds(seconds), variable ? weightedRates / seconds : firstRate, variable), false) : default;
    }
}
