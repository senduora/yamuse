using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using YMusic.Native;
using ShapePath = System.Windows.Shapes.Path;

internal static partial class Program
{
    private static async Task CheckPopupGlyph0317()
    {
        await Run("PopupGlyph0317: every ordinary icon paints before popup parenting and follows its own item's colour", async () =>
        {
            using var fixture = new Pc0317Fixture("glyph-owner"); var w = fixture.Window;
            var row = new TrackRow { Id = "1234", RequestId = "1234:91", AlbumId = "91", Title = "Трек", Artist = "Исполнитель", Available = true };
            Pc0317Set(w, "_favoriteIdsKnown", true);
            foreach (var anchor in new FrameworkElement[] { Control<ListView>(w, "TracksList"), Control<Button>(w, "PlayerMoreButton"), Control<ContentControl>(w, "ExpandedPlayerHost") })
            {
                var menu = Ta0317Menu(w, anchor, () => row); Ta0317Open(menu);
                var ordinary = menu.Items.OfType<MenuItem>().Where(item => item.Name is not ("TrackActionLike" or "TrackActionDownload" or "TrackActionRemoveOffline")).ToArray();
                Assert(ordinary.Length == 7, "ordinary track actions disappeared");
                foreach (var item in ordinary)
                {
                    var icon = (ShapePath)item.Icon;
                    Assert(VisualTreeHelper.GetParent(icon) is null, "fixture already attached the glyph to a popup template");
                    Assert(icon.Stroke is SolidColorBrush { Color.A: > 0 }, anchor.Name + "/" + item.Name + " has no paint before a popup supplies visual parents");
                    var colour = new SolidColorBrush(Color.FromRgb(110, 173, 250)); item.Foreground = colour;
                    await Dispatcher.Yield(DispatcherPriority.DataBind);
                    Assert(ReferenceEquals(icon.Stroke, colour), "glyph ignores its owner's updated colour outside the popup tree");
                    icon.Measure(new Size(18, 18)); icon.Arrange(new Rect(0, 0, 18, 18)); icon.UpdateLayout();
                    var pixels = new byte[18 * 18 * 4]; var bitmap = new RenderTargetBitmap(18, 18, 96, 96, PixelFormats.Pbgra32);
                    bitmap.Render(icon); bitmap.CopyPixels(pixels, 18 * 4, 0);
                    Assert(Enumerable.Range(0, 18 * 18).Count(i => pixels[i * 4 + 3] > 32) >= 5,
                        "an existing action icon remains blank when rendered before popup parenting");
                }
                foreach (var key in new[] { "Download", "RemoveOffline" })
                    Assert(ReferenceEquals(((ShapePath)Ta0317Item(menu, key).Icon).Stroke, w.FindResource("DownloadBrush")), "the owner binding replaced an offline action's green colour");
            }
        });
        await Run("PopupGlyph0317: row player and expanded menu render identically after repeated template layout at four DPIs", () =>
        {
            using var fixture = new Pc0317Fixture("glyph-uniform"); var w = fixture.Window;
            var row = new TrackRow { Id = "1234", RequestId = "1234:91", AlbumId = "91", Title = "Трек", Artist = "Исполнитель", Available = true,
                Artists = new[] { new ArtistReference("55", "Исполнитель") } };
            Pc0317Set(w, "_favoriteIdsKnown", true);
            foreach (var scale in new[] { 1d, 1.25, 1.5, 2 })
            {
                byte[]? reference = null;
                foreach (var anchor in new FrameworkElement[] { Control<ListView>(w, "TracksList"), Control<Button>(w, "PlayerMoreButton"), Control<ContentControl>(w, "ExpandedPlayerHost") })
                {
                    var menu = Ta0317Menu(w, anchor, () => row); Ta0317Open(menu); menu.Width = 340;
                    for (var pass = 0; pass < 2; pass++)
                    {
                        menu.Measure(new Size(340, double.PositiveInfinity)); menu.Arrange(new Rect(new Point(), menu.DesiredSize)); menu.UpdateLayout();
                        var width = (int)Math.Ceiling(340 * scale); var height = (int)Math.Ceiling(menu.ActualHeight * scale);
                        var bitmap = new RenderTargetBitmap(width, height, 96 * scale, 96 * scale, PixelFormats.Pbgra32); bitmap.Render(menu);
                        var pixels = new byte[width * height * 4]; bitmap.CopyPixels(pixels, width * 4, 0);
                        if (reference is null) reference = pixels;
                        else Assert(pixels.SequenceEqual(reference), "menu presentation differs by entry point or a second template layout at DPI scale " + scale);
                        if (pass == 1 && scale == 1)
                        {
                            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                            using var output = File.Create(Path.Combine(_directory, "popup-glyph0317-" + anchor.Name + ".png")); encoder.Save(output);
                        }
                    }
                }
            }
            return Task.CompletedTask;
        });
    }
}
