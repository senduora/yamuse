using System.IO;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using YMusic.Native;

internal static partial class Program
{
    private static async Task CheckCoverPalette016()
    {
        await Run("CoverPalette016: shaded teal and blue majority beats flat vivid and neutral minorities", Cover016ShadedArea);
        await Run("CoverPalette016: neighbouring hue boundaries and circular red remain one family", Cover016HueBoundaries);
        await Run("CoverPalette016: varied neutral majority remains neutral against vivid artwork", Cover016NeutralArea);
        await Run("CoverPalette016: visible alpha area defeats almost transparent RGB and requires frozen artwork", Cover016AlphaArea);
    }

    private static Task Cover016ShadedArea()
    {
        var evidence = new List<object>();
        foreach (var minority in new[] { Colors.Magenta, Color.FromRgb(144, 144, 144) })
        {
            // Thirty-two distinct shades occupy 75% of the cover. None of their
            // old RGB voxels alone has the area of the uniform 25% minority.
            var teal = Accent015Bitmap(32, 32, (x, y) => x < 24
                ? Color.FromRgb((byte)(15 + 2 * y), (byte)(37 + 4 * y), (byte)(37 + 4 * y + y % 3)) : minority);
            var blue = Accent015Bitmap(32, 32, (x, y) => x < 24
                ? Color.FromRgb((byte)(12 + y), (byte)(27 + 2 * y), (byte)(55 + 5 * y)) : minority);
            evidence.Add(Cover016Expect(teal, Color.FromRgb(46, 99, 99), "teal shades versus " + minority));
            evidence.Add(Cover016Expect(blue, Color.FromRgb(27, 58, 132), "blue shades versus " + minority));
        }
        File.WriteAllText(Path.Combine(_directory, "cover-palette016-shades.json"), JsonSerializer.Serialize(evidence));
        return Task.CompletedTask;
    }

    private static Task Cover016HueBoundaries()
    {
        var aroundYellow = new[] {
            Color.FromRgb(140, 138, 20), Color.FromRgb(190, 188, 35), Color.FromRgb(220, 218, 45),
            Color.FromRgb(138, 140, 20), Color.FromRgb(188, 190, 35), Color.FromRgb(218, 220, 45) };
        var aroundRed = new[] {
            Color.FromRgb(120, 18, 15), Color.FromRgb(180, 27, 22), Color.FromRgb(240, 36, 30),
            Color.FromRgb(120, 15, 18), Color.FromRgb(180, 22, 27), Color.FromRgb(240, 30, 36) };
        var evidence = new[] {
            Cover016Expect(Accent015Bitmap(32, 32, (x, _) => x < 24 ? aroundYellow[x % 6] : Colors.Blue), Color.FromRgb(182, 182, 33), "yellow across 60 degrees"),
            Cover016Expect(Accent015Bitmap(32, 32, (x, _) => x < 24 ? aroundRed[x % 6] : Colors.Gray), Color.FromRgb(180, 24, 24), "red across 0 and 360 degrees") };
        File.WriteAllText(Path.Combine(_directory, "cover-palette016-boundaries.json"), JsonSerializer.Serialize(evidence));
        return Task.CompletedTask;
    }

    private static Task Cover016NeutralArea()
    {
        var evidence = new[] {
            Cover016Expect(Accent015Bitmap(32, 32, (x, y) => x < 24 ? Color.FromRgb((byte)(10 + 7 * y), (byte)(10 + 7 * y), (byte)(10 + 7 * y)) : Colors.Red), Color.FromRgb(118, 118, 118), "32 grey shades versus vivid red"),
            Cover016Expect(Accent015Bitmap(32, 32, (x, _) => x < 12 ? Colors.Black : x < 24 ? Colors.White : Colors.Cyan), Color.FromRgb(127, 127, 127), "black and white versus vivid cyan") };
        foreach (var item in evidence) Assert(item.Color.R == item.Color.G && item.Color.G == item.Color.B, "neutral majority acquired a colour");
        File.WriteAllText(Path.Combine(_directory, "cover-palette016-neutrals.json"), JsonSerializer.Serialize(evidence));
        return Task.CompletedTask;
    }

    private static Task Cover016AlphaArea()
    {
        var evidence = Cover016Expect(Accent015Bitmap(32, 32, (x, _) => x < 24 ? Color.FromArgb(1, 255, 0, 0) : Colors.Blue), Colors.Blue, "opaque blue versus almost invisible red");
        var frozen = Accent015Bitmap(32, 32, (_, _) => Color.FromRgb(15, 37, 37));
        Cover016Expect(frozen, Color.FromRgb(15, 37, 37), "saturated dark teal stays coloured");
        Assert(CoverPalette.Extract(frozen.Clone()) is null, "unfrozen artwork was sampled");
        Assert(CoverPalette.Extract(Accent015Bitmap(32, 32, (_, _) => Colors.Transparent)) is null, "invisible artwork provided a colour");
        File.WriteAllText(Path.Combine(_directory, "cover-palette016-alpha.json"), JsonSerializer.Serialize(evidence));
        return Task.CompletedTask;
    }

    private sealed record Cover016Evidence(string Label, Color Mean, Color Color, double MinimumContrast);

    private static Cover016Evidence Cover016Expect(BitmapSource bitmap, Color expectedMean, string label)
    {
        var actual = CoverPalette.Extract(bitmap);
        Assert(actual == CoverPalette.Readable(expectedMean), label + " selected the wrong family: " + actual);
        var minimum = double.MaxValue;
        for (var step = 0; step <= 100; step++)
        {
            var current = CoverPalette.Mix(CoverPalette.Fallback, actual!.Value, step / 100d);
            minimum = Math.Min(minimum, Math.Min(CoverPalette.Contrast(current, CoverPalette.Ink), CoverPalette.Contrast(current, Color.FromRgb(23, 25, 29))));
        }
        Assert(minimum >= 4.5, label + " lost readable interpolated contrast: " + minimum);
        return new Cover016Evidence(label, expectedMean, actual!.Value, minimum);
    }
}
