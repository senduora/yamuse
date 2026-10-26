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
    private static async Task CheckInterfacePolish0317()
    {
        await Run("Interface0317: tray identity survives replacement at one path and separates portable locations", () =>
        {
            var directory = Path.Combine(_directory, "tray-identity"); Directory.CreateDirectory(directory);
            var executable = Path.Combine(directory, "Yamuse.exe");
            File.WriteAllText(executable, "fixture version A");
            var before = DesktopShell.TrayIdentity(executable);
            File.WriteAllText(executable, "fixture version B with a new icon and version");
            Assert(before != Guid.Empty && before == DesktopShell.TrayIdentity(executable)
                && before == DesktopShell.TrayIdentity(executable.ToUpperInvariant().Replace('\\', '/')),
                "a restart, content update or equivalent Windows path changed the tray identity");
            Assert(before != DesktopShell.TrayIdentity(Path.Combine(directory, "another-copy", "Yamuse.exe")),
                "two unsigned side-by-side paths share an incompatible Explorer registration");
            return Task.CompletedTask;
        });
        await Run("Interface0317: playback timelines retain rail thumb and keyboard focus without an enclosing outline", () =>
        {
            using var fixture = new Library017Fixture("timeline-outline");
            var mini = MiniFixtureWindow();
            try
            {
                void Check(Slider slider)
                {
                    slider.ApplyTemplate(); slider.UpdateLayout();
                    Assert(slider.ActualWidth > 20 && slider.ActualHeight > 0 && slider.Focusable && slider.FocusVisualStyle is null
                        && slider.Template.FindName("PART_Track", slider) is System.Windows.Controls.Primitives.Track { Thumb: not null },
                        "removing the rectangle disabled the timeline's input or thumb");
                    Assert(!SaVisuals<Border>(slider).Any(border => border.BorderThickness.Left > 0
                        && border.ActualHeight >= slider.ActualHeight - 2 && border.ActualWidth >= slider.ActualWidth - 2),
                        "a full-sized rectangle remains in a playback slider template");
                }
                var window = fixture.Window; window.UpdateLayout();
                foreach (var slider in new[] { Control<Slider>(window, "SeekSlider"), Control<Slider>(window, "VolumeSlider"), mini.SeekSlider, mini.VolumeSlider }) Check(slider);
                Library017Set(window, "_current", fixture.Row("outline-track", "album"));
                SaInvoke(window, "SetExpandedPlayerVisible", true, false); window.UpdateLayout();
                Check(SaField<Slider>(window, "_expandedPlayerSeek"));
                return Task.CompletedTask;
            }
            finally { mini.Close(); }
        });
        await Run("Interface0317: track context menu has vector icons and a dark gutter", () =>
        {
            using var fixture = new Library017Fixture("context-menu");
            var original = Control<ListView>(fixture.Window, "TracksList").ContextMenu!;
            var sourceItems = original.Items.OfType<MenuItem>().ToArray();
            Assert(sourceItems.Length == 10 && sourceItems.All(item => item.Icon is ShapePath { Data: not null }),
                "a track action, including its wave action, has no vector icon");
            var menu = new ContextMenu { Width = 340, Style = (Style)Application.Current.FindResource(typeof(ContextMenu)) };
            foreach (var source in original.Items)
            {
                if (source is Separator) { menu.Items.Add(new Separator()); continue; }
                var item = (MenuItem)source; var vector = (ShapePath)item.Icon;
                var icon = new ShapePath { Data = vector.Data, Style = (Style)Application.Current.FindResource("MenuGlyph") };
                if (item.Header as string == "Скачать для офлайн") icon.Stroke = (Brush)Application.Current.FindResource("DownloadBrush");
                menu.Items.Add(new MenuItem { Header = item.Header, Icon = icon });
            }
            // Render the real templates without opening a native popup or
            // taking keyboard/mouse focus away from the running application.
            menu.Measure(new Size(340, double.PositiveInfinity));
            menu.Arrange(new Rect(new Point(), menu.DesiredSize)); menu.UpdateLayout();
            Assert(menu.ActualHeight > 200, "context menu did not realize its action rows");
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(menu.ActualWidth), (int)Math.Ceiling(menu.ActualHeight), 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(menu);
            var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4]; bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
            for (var y = 18; y < bitmap.PixelHeight - 18; y++)
            {
                var offset = (y * bitmap.PixelWidth + 7) * 4;
                Assert(pixels[offset + 3] > 240 && pixels[offset] < 100 && pixels[offset + 1] < 100 && pixels[offset + 2] < 100,
                    "a light system gutter remains along the menu's left edge");
            }
            foreach (var separator in menu.Items.OfType<Separator>())
            {
                var bounds = new Rect(separator.TranslatePoint(new Point(), menu), separator.RenderSize);
                for (var y = Math.Max(0, (int)bounds.Top); y < Math.Min(bitmap.PixelHeight, (int)Math.Ceiling(bounds.Bottom)); y++)
                    for (var x = 50; x < bitmap.PixelWidth - 15; x++)
                    {
                        var offset = (y * bitmap.PixelWidth + x) * 4;
                        Assert(pixels[offset] < 100 && pixels[offset + 1] < 100 && pixels[offset + 2] < 100,
                            "a bright system separator breaks the dark menu surface");
                    }
            }
            var download = menu.Items.OfType<MenuItem>().Single(item => item.Header as string == "Скачать для офлайн");
            Assert(download.Icon is ShapePath { Stroke: SolidColorBrush green } && green.Color == ((SolidColorBrush)Application.Current.FindResource("DownloadBrush")).Color,
                "download vector lost its independent green colour");
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var file = File.Create(Path.Combine(_directory, "context-menu-dark-icons.png"))) encoder.Save(file);
            return Task.CompletedTask;
        });

        await Run("Interface0317: transient status overlays reclaim list space and preserve newer messages", async () =>
        {
            var fixture = new Library017Fixture("status-overlay");
            try
            {
                var window = fixture.Window;
                SaInvoke(window, "ShowRows", (object)new[] { fixture.Row("status-track", "album") });
                await Task.Delay(250); window.UpdateLayout();
                SaInvoke(window, "HideStatusNotification"); await Task.Delay(230); window.UpdateLayout();
                var list = Control<ListView>(window, "TracksList"); var height = list.ActualHeight;
                var toast = Control<Border>(window, "StatusNotification");
                Assert(height > 100 && toast.Visibility == Visibility.Collapsed, "initial status still reserves a footer");
                SaInvoke(window, "SetStatus", "Тестовое уведомление поверх списка", false);
                await Task.Delay(240); window.UpdateLayout();
                Assert(toast.Visibility == Visibility.Visible && toast.Opacity > .99 && Math.Abs(list.ActualHeight - height) < 1,
                    "showing a message changed the list's available height");
                App.Capture(window, Path.Combine(_directory, "status-overlay-visible.png"));
                SaInvoke(window, "HideStatusNotification");
                SaInvoke(window, "SetStatus", "Новое сообщение сохраняется после старой анимации", false);
                await Task.Delay(240);
                Assert(toast.Visibility == Visibility.Visible && Control<TextBlock>(window, "StatusText").Text.StartsWith("Новое сообщение"),
                    "the old closing animation hid a newer message");
                var timer = SaField<DispatcherTimer>(window, "_statusDismissTimer");
                SaInvoke(window, "Busy", true);
                Assert(!timer.IsEnabled, "an in-progress operation was scheduled to disappear");
                SaInvoke(window, "Busy", false);
                Assert(timer.IsEnabled, "a completed operation did not schedule dismissal");
                timer.Interval = TimeSpan.FromMilliseconds(40);
                await Task.Delay(320); window.UpdateLayout();
                Assert(toast.Visibility == Visibility.Collapsed && !timer.IsEnabled && Math.Abs(list.ActualHeight - height) < 1,
                    "auto-dismiss left a panel, active timer or reserved list space");
                App.Capture(window, Path.Combine(_directory, "status-overlay-hidden.png"));
                SaInvoke(window, "SetStatus", "Закрытие освобождает таймер уведомления", false);
                fixture.Dispose();
                Assert(!timer.IsEnabled, "closing retained the notification timer");
                return;
            }
            finally { if (!SaField<bool>(fixture.Window, "_closed")) fixture.Dispose(); }
        });
    }
}
