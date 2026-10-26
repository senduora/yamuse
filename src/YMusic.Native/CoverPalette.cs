using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace YMusic.Native;

/// <summary>A small, anonymous palette of an already decoded player cover.</summary>
internal static class CoverPalette
{
    internal const int SampleEdge = 32;
    internal static readonly Color Fallback = Color.FromRgb(231, 187, 89);
    internal static readonly Color Ink = Color.FromRgb(23, 17, 20);

    internal static Color? Extract(BitmapSource source)
    {
        // ArtworkCache returns frozen sources. Never decode another rendition or
        // read the full bitmap into a managed pixel array just for its palette.
        if (!source.IsFrozen || source.PixelWidth < 1 || source.PixelHeight < 1) return null;
        var scale = Math.Min(1, (double)SampleEdge / Math.Max(source.PixelWidth, source.PixelHeight));
        BitmapSource small = scale < 1 ? new TransformedBitmap(source, new ScaleTransform(scale, scale)) : source;
        if (small.Format != PixelFormats.Bgra32) small = new FormatConvertedBitmap(small, PixelFormats.Bgra32, null, 0);
        var stride = small.PixelWidth * 4;
        var pixels = new byte[stride * small.PixelHeight];
        small.CopyPixels(pixels, stride, 0);
        return Select(pixels);
    }

    private static Color? Select(byte[] pixels)
    {
        // Neutrals are a fallback, not a competing colour family: a supported
        // coloured title should survive a mostly black/white cover. Circular hue
        // windows combine neighbouring shades without splitting red at zero.
        const int bins = 360, familyWidth = 60;
        var counts = new int[bins]; var red = new int[bins]; var green = new int[bins]; var blue = new int[bins];
        var chromaArea = new int[bins];
        var detailRed = new long[bins]; var detailGreen = new long[bins]; var detailBlue = new long[bins];
        var neutralCount = 0; var neutralRed = 0; var neutralGreen = 0; var neutralBlue = 0;
        var totalCount = 0; var totalRed = 0; var totalGreen = 0; var totalBlue = 0;
        for (var i = 0; i + 3 < pixels.Length; i += 4)
        {
            var area = pixels[i + 3];
            if (area == 0) continue;
            var b = pixels[i]; var g = pixels[i + 1]; var r = pixels[i + 2];
            totalCount += area; totalRed += r * area; totalGreen += g * area; totalBlue += b * area;
            var maximum = Math.Max(r, Math.Max(g, b));
            var chroma = maximum - Math.Min(r, Math.Min(g, b));
            // Relative saturation keeps coloured shadows (e.g. 15,37,37) in
            // their hue family; tiny channel noise still belongs to neutrals.
            if (chroma < 8 || chroma * 6 < maximum)
            {
                neutralCount += area; neutralRed += r * area; neutralGreen += g * area; neutralBlue += b * area;
                continue;
            }
            var hue = 60 * (maximum == r ? (g - b) / (double)chroma : maximum == g ? 2 + (b - r) / (double)chroma : 4 + (r - g) / (double)chroma);
            var bin = (int)(hue < 0 ? hue + 360 : hue);
            counts[bin] += area; red[bin] += r * area; green[bin] += g * area; blue[bin] += b * area;
            chromaArea[bin] += chroma * area;
            detailRed[bin] += (long)r * chroma * area; detailGreen[bin] += (long)g * chroma * area; detailBlue[bin] += (long)b * chroma * area;
        }
        var bestCount = neutralCount; long bestRed = neutralRed, bestGreen = neutralGreen, bestBlue = neutralBlue;
        if (bestCount == 0) { bestCount = totalCount; bestRed = totalRed; bestGreen = totalGreen; bestBlue = totalBlue; }
        var count = 0; var sumRed = 0; var sumGreen = 0; var sumBlue = 0; var sumChroma = 0; var bestScore = 0L;
        long sumDetailRed = 0, sumDetailGreen = 0, sumDetailBlue = 0;
        for (var bin = 0; bin < familyWidth; bin++)
        {
            count += counts[bin]; sumRed += red[bin]; sumGreen += green[bin]; sumBlue += blue[bin]; sumChroma += chromaArea[bin];
            sumDetailRed += detailRed[bin]; sumDetailGreen += detailGreen[bin]; sumDetailBlue += detailBlue[bin];
        }
        for (var start = 0; start < bins; start++)
        {
            // At least 3% of visible alpha area and four opaque sample pixels
            // admit meaningful details while rejecting isolated vivid noise.
            // Chroma can at most double area weight; it cannot make one bright
            // pixel beat a well-supported family of darker coloured shades.
            var score = count * 255L + sumChroma;
            if (count >= 4 * 255 && count * 100L >= totalCount * 3L && score > bestScore)
            {
                bestScore = score; bestCount = count; bestRed = sumRed; bestGreen = sumGreen; bestBlue = sumBlue;
                // On a neutral background, antialiased/shadow edge pixels can
                // outnumber the coloured detail itself. A chroma-weighted mean
                // retains that detail without selecting a single vivid pixel.
                if (neutralCount * 2 > totalCount)
                { bestCount = sumChroma; bestRed = sumDetailRed; bestGreen = sumDetailGreen; bestBlue = sumDetailBlue; }
            }
            var next = (start + familyWidth) % bins;
            count += counts[next] - counts[start]; sumRed += red[next] - red[start]; sumGreen += green[next] - green[start]; sumBlue += blue[next] - blue[start];
            sumChroma += chromaArea[next] - chromaArea[start];
            sumDetailRed += detailRed[next] - detailRed[start]; sumDetailGreen += detailGreen[next] - detailGreen[start]; sumDetailBlue += detailBlue[next] - detailBlue[start];
        }
        if (bestCount == 0) return null;
        var color = Color.FromRgb((byte)(bestRed / bestCount), (byte)(bestGreen / bestCount), (byte)(bestBlue / bestCount));
        // A thin detail downsampled against black is dimmer than its own colour.
        // Lift only a supported detail's mean, keeping its hue before Readable.
        var maximumChannel = Math.Max(color.R, Math.Max(color.G, color.B));
        if (bestScore > 0 && neutralCount * 2 > totalCount && maximumChannel > 0)
            color = Color.FromRgb((byte)(color.R * 255 / maximumChannel), (byte)(color.G * 255 / maximumChannel), (byte)(color.B * 255 / maximumChannel));
        return Readable(color);
    }

    internal static Color Readable(Color color)
    {
        if (color == Fallback) return color;
        // A supporting plane of convex sRGB luminance at the gold fallback.
        // Keeping every target in this half-space also keeps ALL interpolated
        // and interrupted transitions at least as bright as the fallback.
        var r = Fallback.R / 255d; var g = Fallback.G / 255d; var b = Fallback.B / 255d;
        var wr = .2126 * Derivative(r); var wg = .7152 * Derivative(g); var wb = .0722 * Derivative(b);
        var minimum = wr * r + wg * g + wb * b;
        var value = wr * color.R / 255 + wg * color.G / 255 + wb * color.B / 255;
        if (value >= minimum) return Color.FromRgb(color.R, color.G, color.B);
        var amount = Math.Clamp((minimum - value) / (wr + wg + wb - value) + .004, 0, 1);
        return Mix(color, Colors.White, amount);
    }

    internal static Color Mix(Color a, Color b, double amount) => Color.FromRgb(
        (byte)Math.Round(a.R + (b.R - a.R) * amount), (byte)Math.Round(a.G + (b.G - a.G) * amount), (byte)Math.Round(a.B + (b.B - a.B) * amount));
    internal static double Contrast(Color a, Color b) => (Math.Max(Luminance(a), Luminance(b)) + .05) / (Math.Min(Luminance(a), Luminance(b)) + .05);
    private static double Luminance(Color c) => .2126 * Linear(c.R / 255d) + .7152 * Linear(c.G / 255d) + .0722 * Linear(c.B / 255d);
    private static double Linear(double value) => value <= .04045 ? value / 12.92 : Math.Pow((value + .055) / 1.055, 2.4);
    private static double Derivative(double value) => value <= .04045 ? 1 / 12.92 : 2.4 / 1.055 * Math.Pow((value + .055) / 1.055, 1.4);
}
