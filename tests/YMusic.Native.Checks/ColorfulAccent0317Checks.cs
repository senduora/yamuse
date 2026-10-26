using System.IO;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using YMusic.Native;

internal static partial class Program
{
    private static async Task CheckColorfulAccent0317()
    {
        await Run("ColorfulAccent0317: supported red details survive black white and grey backgrounds", Colorful0317Details);
        await Run("ColorfulAccent0317: neighbouring shades combine while isolated vivid noise has no support", Colorful0317Support);
        await Run("ColorfulAccent0317: supported colour area beats a smaller more saturated family", Colorful0317Area);
        await Run("ColorfulAccent0317: achromatic washed and transparent covers retain safe fallbacks", Colorful0317Fallbacks);
        await Run("ColorfulAccent0317: frozen sampling remains deterministic and bounded on large covers", Colorful0317Bounds);
        await Run("ColorfulAccent0317: selected colours retain contrast throughout the unchanged five second transition", Colorful0317Contrast);
    }

    private static Task Colorful0317Details()
    {
        var evidence = new List<object>();
        foreach (var background in new[] { Colors.Black, Colors.Gray, Colors.White })
        {
            var selected = Colorful0317Extract(Colorful0317Bitmap(32, 32, (x, _) => x < 24 ? background : Colors.Red));
            Assert(selected == CoverPalette.Readable(Colors.Red), "a supported red detail lost to " + background);
            evidence.Add(new { background = background.ToString(), selected = selected.ToString() });
        }
        // Thin lettering is represented by several neighbouring dark/red edge
        // shades, alongside a larger white figure on a nearly black cover.
        var lettering = Colorful0317Bitmap(32, 32, (x, y) => y >= 3 && y < 8 && x >= 8 && x < 24
            ? Color.FromRgb((byte)(42 + 7 * (x % 10)), (byte)(8 + y), (byte)(9 + x % 4))
            : y >= 22 && x >= 10 && x < 21 ? Colors.White : Color.FromRgb(15, 16, 17));
        var red = Colorful0317Extract(lettering);
        Colorful0317ExpectRed(red, "antialiased red lettering");
        evidence.Add(new { label = "antialiased-red-title-and-white-figure", selected = red.ToString() });
        Colorful0317Save("details", evidence);
        return Task.CompletedTask;
    }

    private static Task Colorful0317Support()
    {
        foreach (var background in new[] { Colors.Black, Colors.Gray, Colors.White })
        {
            foreach (var noise in new[] { Colors.Red, Colors.Blue, Colors.Magenta })
            {
                var one = Colorful0317Bitmap(32, 32, (x, y) => x == 0 && y == 0 ? noise : background);
                Assert(Colorful0317Extract(one) == CoverPalette.Readable(background), "one vivid pixel became the accent");
            }
            var belowArea = Colorful0317Bitmap(32, 32, (x, y) => y * 32 + x < 30 ? Colors.Red : background);
            Assert(Colorful0317Extract(belowArea) == CoverPalette.Readable(background), "an unsupported sub-three-percent detail became the accent");
            var supported = Colorful0317Bitmap(32, 32, (x, y) => y * 32 + x < 32
                ? (x % 2 == 0 ? Color.FromRgb(205, 18, 28) : Color.FromRgb(185, 25, 16)) : background);
            Colorful0317ExpectRed(Colorful0317Extract(supported), "neighbouring red shades above the area threshold");
        }
        var three = Colorful0317Bitmap(8, 8, (x, y) => y * 8 + x < 3 ? Colors.Red : Colors.Gray);
        Assert(Colorful0317Extract(three) == CoverPalette.Readable(Colors.Gray), "three pixels bypassed the minimum opaque-area support");
        var four = Colorful0317Bitmap(8, 8, (x, y) => y * 8 + x < 4 ? Colors.Red : Colors.Gray);
        Colorful0317ExpectRed(Colorful0317Extract(four), "four supported pixels");
        Colorful0317Save("support", new { onePixelRejected = true, thirtyOf1024Rejected = true, thirtyTwoNeighbouringShadesAccepted = true,
            threeOf64Rejected = true, fourOf64Accepted = true });
        return Task.CompletedTask;
    }

    private static Task Colorful0317Area()
    {
        var mutedGreen = Color.FromRgb(74, 102, 85);
        Assert(Colorful0317Extract(Colorful0317Bitmap(32, 32, (x, _) => x < 24 ? mutedGreen : Colors.Red)) == CoverPalette.Readable(mutedGreen),
            "saturation overrode the seventy-five-percent coloured area");
        var teal = Colorful0317Bitmap(32, 32, (x, y) => x < 24
            ? Color.FromRgb((byte)(15 + 2 * y), (byte)(37 + 4 * y), (byte)(37 + 4 * y + y % 3)) : Colors.Magenta);
        var blue = Colorful0317Bitmap(32, 32, (x, y) => x < 24
            ? Color.FromRgb((byte)(12 + y), (byte)(27 + 2 * y), (byte)(55 + 5 * y)) : Colors.Gray);
        Assert(Colorful0317Extract(teal) == CoverPalette.Readable(Color.FromRgb(46, 99, 99)), "coloured-majority teal shade averaging changed");
        Assert(Colorful0317Extract(blue) == CoverPalette.Readable(Color.FromRgb(27, 58, 132)), "coloured-majority blue shade averaging changed");
        var blueDetail = Colorful0317Extract(Colorful0317Bitmap(32, 32, (x, _) => x < 6 ? Colors.Blue : x == 6 ? Colors.Red : Colors.Gray));
        Assert(blueDetail.B > blueDetail.R && blueDetail.B > blueDetail.G, "a small vivid neighbour beat the supported blue cluster");
        Colorful0317Save("area", new { mutedGreenMajorityPreserved = true, teal = Colorful0317Extract(teal).ToString(), blue = Colorful0317Extract(blue).ToString(),
            supportedBlueDetail = blueDetail.ToString() });
        return Task.CompletedTask;
    }

    private static Task Colorful0317Fallbacks()
    {
        foreach (var neutral in new[] { Colors.Black, Colors.White, Colors.Gray, Color.FromRgb(248, 248, 248), Color.FromRgb(246, 248, 250), Color.FromRgb(235, 220, 218) })
        {
            var selected = Colorful0317Extract(Colorful0317Bitmap(32, 32, (_, _) => neutral));
            Assert(selected == CoverPalette.Readable(neutral), "achromatic or washed cover invented a hue");
        }
        var greyGradient = Colorful0317Extract(Colorful0317Bitmap(32, 32, (_, y) => Color.FromRgb((byte)(y * 8), (byte)(y * 8), (byte)(y * 8))));
        Assert(greyGradient.R == greyGradient.G && greyGradient.G == greyGradient.B, "grey shades acquired a colour");
        Assert(CoverPalette.Extract(Colorful0317Bitmap(32, 32, (_, _) => Color.FromArgb(0, 255, 0, 0))) is null, "transparent RGB supplied a palette");
        var blue = Colorful0317Bitmap(32, 32, (x, _) => x < 24 ? Color.FromArgb(1, 255, 0, 0) : Colors.Blue);
        Assert(Colorful0317Extract(blue) == CoverPalette.Readable(Colors.Blue), "almost invisible red defeated opaque blue");
        var faint = Colorful0317Bitmap(32, 32, (x, y) => y * 32 + x < 51 ? Color.FromArgb(128, 255, 0, 0) : Colors.Gray);
        Assert(Colorful0317Extract(faint) == CoverPalette.Readable(Colors.Gray), "RGB pixel count replaced alpha-weighted support");
        var small = Colorful0317Bitmap(1, 1, (_, _) => Colors.Red);
        Assert(Colorful0317Extract(small) == CoverPalette.Readable(Colors.Red), "a tiny but wholly coloured image lost its available fallback");
        Assert(CoverPalette.Extract(small.Clone()) is null, "unfrozen source was sampled");
        Colorful0317Save("fallbacks", new { achromaticAndWashedPreserved = true, fullyTransparentRejected = true, alphaAreaWeighted = true, tinyWholeCoverRetained = true });
        return Task.CompletedTask;
    }

    private static Task Colorful0317Bounds()
    {
        var large = Colorful0317Bitmap(1024, 1024, (x, y) => x >= 256 && x < 768 && y >= 96 && y < 224 ? Colors.Red : Colors.Black);
        var expected = Colorful0317Extract(large);
        for (var i = 0; i < 12; i++) Assert(Colorful0317Extract(large) == expected, "repeat extraction was nondeterministic");
        var before = GC.GetAllocatedBytesForCurrentThread(); var actual = Colorful0317Extract(large);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert(actual == expected && allocated < 2 * 1024 * 1024, "large cover copied a full managed pixel buffer");
        Assert(CoverPalette.SampleEdge == 32, "bounded sample dimensions changed");
        Colorful0317Save("bounds", new { sourceWidth = 1024, sourceHeight = 1024, maximumSamplePixels = 1024, repeatCount = 12,
            selected = actual.ToString(), managedAllocatedBytes = allocated, tightWallClockAssertion = false });
        return Task.CompletedTask;
    }

    private static Task Colorful0317Contrast()
    {
        var selected = new[] { Colors.Red, Colors.Blue, Colors.Green, Colors.Cyan, Colors.Magenta, Colors.Black, Colors.Gray, Colors.White }
            .Select(detail => Colorful0317Extract(Colorful0317Bitmap(32, 32, (x, _) => x < 28 ? Colors.Black : detail))).ToArray();
        var minimum = double.MaxValue;
        foreach (var from in selected.Append(CoverPalette.Fallback)) foreach (var to in selected.Append(CoverPalette.Fallback)) for (var i = 0; i <= 100; i++)
        {
            var color = CoverPalette.Mix(from, to, i / 100d);
            var contrast = Math.Min(CoverPalette.Contrast(color, CoverPalette.Ink), CoverPalette.Contrast(color, Color.FromRgb(23, 25, 29)));
            Assert(double.IsFinite(contrast) && contrast >= 4.5, "an interpolated selected accent lost readable foreground contrast");
            minimum = Math.Min(minimum, contrast);
        }
        Assert(CoverAccentController.TransitionDuration == TimeSpan.FromSeconds(5), "the five second transition contract changed");
        Colorful0317Save("contrast", new { colours = selected.Length + 1, stepsPerPair = 101, minimumRatio = minimum, transitionSeconds = 5,
            controllersOrWindowsCreated = false });
        return Task.CompletedTask;
    }

    private static Color Colorful0317Extract(BitmapSource bitmap) => CoverPalette.Extract(bitmap) ?? throw new InvalidOperationException("Visible synthetic cover did not supply a palette");
    private static void Colorful0317ExpectRed(Color color, string label)
        => Assert(color.R >= color.G + 60 && color.R >= color.B + 60, label + " did not retain a visible red accent: " + color);
    private static void Colorful0317Save(string name, object data)
        => File.WriteAllText(Path.Combine(_directory, "colorful-accent0317-" + name + ".json"), JsonSerializer.Serialize(data));
    private static BitmapSource Colorful0317Bitmap(int width, int height, Func<int, int, Color> pixel)
    {
        var data = new byte[width * height * 4];
        for (var y = 0; y < height; y++) for (var x = 0; x < width; x++)
        { var color = pixel(x, y); var i = (y * width + x) * 4; data[i] = color.B; data[i + 1] = color.G; data[i + 2] = color.R; data[i + 3] = color.A; }
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, data, width * 4); bitmap.Freeze(); return bitmap;
    }
}
