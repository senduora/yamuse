using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using YMusic.Native;

internal static partial class Program
{
    private static async Task CheckPlayerAudioLayout040()
    {
        await Run("AudioLayout040: quality sits below wider responsive volume without overlapping transport at every window size and theme", async () =>
        {
            using var fixture = new SaFixture("audio040-layout"); await fixture.Activate(); var w = fixture.Window;
            var previousMode = ThemeManager.Mode;
            try
            {
                var quality = Control<TextBlock>(w, "QualityText");
                var slider = Control<Slider>(w, "VolumeSlider"); var audio = Control<Grid>(w, "AudioControls");
                const string message = "Офлайн · AAC · 192 кбит/с"; quality.Text = message;
                var facts = new List<object>();
                foreach (var mode in new[] { "Dark", "Light" })
                {
                    ThemeManager.SetMode(mode);
                    foreach (var size in new[] { (1050, 680), (1280, 860), (1920, 1080) })
                    {
                        await Sp016Resize(w, size.Item1, size.Item2);
                        var player = Control<Border>(w, "PlayerBar"); var root = Control<Grid>(w, "LayoutRoot");
                        var q = Sp016Rect(quality, root); var v = Sp016Rect(slider, root); var a = Sp016Rect(audio, root);
                        Assert(ReferenceEquals(quality.Parent, Control<Grid>(w, "AudioVolumePanel")) && Grid.GetRow(quality) == 1,
                            "format is not in the second volume row");
                        Assert(q.Top >= v.Bottom - .1 && q.Right <= a.Right + .1 && q.Left >= a.Left - .1 && q.Bottom <= a.Bottom + .1,
                            "format overlaps volume or escapes the audio panel");
                        Assert(slider.ActualWidth >= 90 && slider.ActualWidth <= 144.1 && (size.Item1 < 1280 || slider.ActualWidth >= 143),
                            $"volume is not wider at normal widths or stops shrinking at minimum width: window={w.ActualWidth:F2}, audio={audio.ActualWidth:F2}, slider={slider.ActualWidth:F2}, requested={size.Item1}");
                        Assert(quality.ActualWidth >= slider.ActualWidth && quality.Text == message && quality.ToolTip as string == message
                            && quality.TextTrimming == TextTrimming.CharacterEllipsis,
                            "format lost its reserved width, content, full tooltip or final narrow-window trimming");
                        var natural = new TextBlock { Text = message, FontSize = quality.FontSize, FontFamily = quality.FontFamily };
                        natural.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                        if (size.Item1 >= 1280) Assert(quality.ActualWidth >= natural.DesiredSize.Width, "normal-sized format still needs truncation");
                        foreach (var name in new[] { "PlayerLyricsButton", "ExpandPlayerButton", "PlayerQueueButton", "PlaybackContextButton" })
                        {
                            var control = Control<Button>(w, name);
                            if (control.Visibility == Visibility.Visible) Assert(Sp016Rect(control, root).Right <= q.Left + .1, "quality overlaps " + name);
                        }
                        Assert(Sp016Rect(Control<Grid>(w, "TimelinePanel"), root).Top >= a.Bottom - .1,
                            "two-row volume panel overlaps the track timeline");
                        Sp016Within(audio, player);
                        facts.Add(new { mode, width = size.Item1, height = size.Item2, quality = q, volume = v, naturalTextWidth = natural.DesiredSize.Width });
                        if (size.Item1 == 1280)
                        {
                            var drawing = new DrawingVisual();
                            using (var context = drawing.RenderOpen())
                                context.DrawRectangle(new VisualBrush(player), null, new Rect(0, 0, player.ActualWidth, player.ActualHeight));
                            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(player.ActualWidth), (int)Math.Ceiling(player.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                            bitmap.Render(drawing); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                            using var output = File.Create(Path.Combine(_directory, "audio-layout040-" + mode + ".png")); encoder.Save(output);
                        }
                    }
                }
                File.WriteAllText(Path.Combine(_directory, "audio-layout040.json"), JsonSerializer.Serialize(facts, new JsonSerializerOptions { WriteIndented = true }));
            }
            finally { ThemeManager.SetMode(previousMode); }
        });
    }
}
