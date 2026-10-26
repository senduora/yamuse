using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using YMusic.Native;

internal static partial class Program
{
    private static async Task CheckBranding040()
    {
        await Run("Branding040: embedded logo preserves both supplied flowing arrows on the new gold plate", () =>
        {
            using var stream = Branding040Resource("Yamuse.png");
            var logo = BitmapFrame.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            BrandingSuppliedLogo040(logo);
            return Task.CompletedTask;
        });
        await Run("Branding040: every native ICO size decodes with transparent corners and the same black and gold mark", () =>
        {
            using var stream = Branding040Resource("Yamuse.ico");
            var bytes = stream.ToArray();
            var expected = new[] { 16, 20, 24, 32, 40, 48, 64, 96, 128, 256 };
            Assert(BitConverter.ToUInt16(bytes, 0) == 0 && BitConverter.ToUInt16(bytes, 2) == 1
                && BitConverter.ToUInt16(bytes, 4) == expected.Length, "new icon header or frame count differs");
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            Assert(decoder.Frames.Select(frame => frame.PixelWidth).Order().SequenceEqual(expected)
                && decoder.Frames.All(frame => frame.PixelHeight == frame.PixelWidth), "required native icon sizes differ");
            foreach (var frame in decoder.Frames)
            {
                var pixels = Branding040Pixels(frame); var count = frame.PixelWidth * frame.PixelHeight;
                Assert(pixels[3] == 0 && pixels[(frame.PixelWidth - 1) * 4 + 3] == 0
                    && pixels[(count - 1) * 4 + 3] == 0, "native icon lost its transparent rounded corners");
                Assert(Enumerable.Range(0, count).Any(i => pixels[i * 4 + 3] is > 0 and < 255), "native icon lost edge antialiasing");
                // At 16px each source stroke is about one pixel wide, so its
                // antialiased dark core blends with the gold plate.
                var black = Enumerable.Range(0, count).Count(i => pixels[i * 4 + 3] > 220
                    && pixels[i * 4] < 80 && pixels[i * 4 + 1] < 80 && pixels[i * 4 + 2] < 80);
                var gold = Enumerable.Range(0, count).Count(i => pixels[i * 4 + 3] > 220
                    && Math.Abs(pixels[i * 4] - 89) <= 2 && Math.Abs(pixels[i * 4 + 1] - 187) <= 2 && Math.Abs(pixels[i * 4 + 2] - 231) <= 2);
                Assert(black >= count * .05 && gold >= count * .35, "black arrows or #E7BB59 plate disappeared in a native size");
            }
            File.WriteAllText(Path.Combine(_directory, "branding040-assets.json"), JsonSerializer.Serialize(new
            {
                pngDimensions = new[] { 512, 512 }, icoSizes = expected, goldPlate = "#E7BB59", suppliedFlowingArrowSamples = true,
                icoSha256 = Convert.ToHexString(SHA256.HashData(bytes)), syntheticAssetRenderingOnly = true,
                nativeWindowCreated = false, windowShown = false, userDesktopCaptured = false
            }, new JsonSerializerOptions { WriteIndented = true }));
            return Task.CompletedTask;
        });
        await Run("Branding040: window and tray use the packaged new icon without opening a native window", () =>
        {
            Assert(AppBranding.WindowIcon is BitmapSource { PixelWidth: 256, PixelHeight: 256, IsFrozen: true },
                "window branding did not load the largest frozen packaged frame");
            using var tray = AppBranding.CreateTrayIcon(0);
            Assert(tray.IsBranded && !tray.IsInvalid, "native tray icon fell back to the generic Windows icon");
            return Task.CompletedTask;
        });
    }

    // Also used by the existing sidebar fixture. These are points inside the
    // supplied upper and lower silhouettes, followed by their five open gaps.
    private static void BrandingSuppliedLogo040(BitmapSource logo)
    {
        Assert(logo.PixelWidth == 512 && logo.PixelHeight == 512, "new sidebar logo is not the 512px packaged mark");
        var pixels = Branding040Pixels(logo);
        foreach (var (x, y) in new[] { (127, 151), (198, 259), (335, 178), (453, 245),
            (80, 302), (198, 345), (340, 259), (434, 330), (387, 375) })
        {
            var at = (y * 512 + x) * 4;
            Assert(pixels[at] < 5 && pixels[at + 1] < 5 && pixels[at + 2] < 5 && pixels[at + 3] == 255,
                "a supplied flowing arrow contour differs");
        }
        foreach (var (x, y) in new[] { (292, 221), (151, 316), (103, 221), (245, 292), (458, 292), (256, 30) })
        {
            var at = (y * 512 + x) * 4;
            Assert(pixels[at] == 89 && pixels[at + 1] == 187 && pixels[at + 2] == 231 && pixels[at + 3] == 255,
                "the new #E7BB59 plate or source silhouette gaps differ");
        }
        Assert(pixels[3] == 0 && pixels[(512 * 512 - 1) * 4 + 3] == 0, "the plate's outer corners are not transparent");
    }

    private static MemoryStream Branding040Resource(string name)
    {
        using var stream = Application.GetResourceStream(new Uri("/YMusic.Native;component/Assets/" + name, UriKind.Relative))!.Stream;
        var copy = new MemoryStream(); stream.CopyTo(copy); copy.Position = 0; return copy;
    }

    private static byte[] Branding040Pixels(BitmapSource bitmap)
    {
        var converted = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
        var result = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4]; converted.CopyPixels(result, bitmap.PixelWidth * 4, 0); return result;
    }
}
