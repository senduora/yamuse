using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using YMusic.Native;

internal static partial class Program
{
    private static async Task CheckThemeIntegration040()
    {
        await Run("ThemeIntegration040: switching theme during a cover fade preserves its primary clocks and five second deadline", () =>
        {
            var previousMode = ThemeManager.Mode;
            ThemeManager.SetMode("Dark");
            using var controller = new CoverAccentController(new Image(), new ResourceDictionary(), () => null);
            try
            {
                controller.Retarget(Color.FromRgb(48, 215, 135));
                if (!SystemParameters.ClientAreaAnimation) return Task.CompletedTask;
                Ts040Seek(controller, 2.5);
                var primaryKeys = new[] { "AccentBrush", "AccentHoverBrush", "AccentFocusBrush", "AccentTextSelectionBrush" };
                var original = primaryKeys.ToDictionary(key => key, key => controller.Clocks[key]);
                var shown = controller.Brushes["AccentBrush"].Color; var target = controller.TargetColor;
                ThemeManager.SetMode("Light");
                Assert(primaryKeys.All(key => ReferenceEquals(original[key], controller.Clocks[key]))
                    && controller.TargetColor == target && controller.Brushes["AccentBrush"].Color == shown,
                    "theme switch replaced or jumped the original cover fade");
                Assert(new[] { "AccentTextBrush", "AccentSoftBrush", "AccentSelectionBrush" }.All(key =>
                    controller.Clocks[key].Timeline.Duration.TimeSpan == TimeSpan.FromSeconds(2.5)), "theme companion restarted a whole five seconds");
                Ts040Seek(controller, 5.01);
                Assert(controller.Brushes["AccentBrush"].Color == target
                    && controller.Brushes["AccentTextBrush"].Color == ThemeManager.ReadableAccent(target),
                    "theme companion did not finish with the original target");
            }
            finally { ThemeManager.SetMode(previousMode); }
            return Task.CompletedTask;
        });
        await Run("ThemeIntegration040: complete main settings and fullscreen surfaces render in both themes without native windows", async () =>
        {
            using var f = new Fr0317Fixture("theme-integrated"); var main = f.Window;
            var previousMode = ThemeManager.Mode;
            try
            {
                Fr0317Get<CoverAccentController>(main, "_coverAccent").Configure(false, "#E7BB59");
                Fr0317Call(main, "ShowQueue"); f.Layout();
                foreach (var mode in new[] { "Light", "Dark" })
                {
                    ThemeManager.SetMode(mode); await Dispatcher.Yield(DispatcherPriority.DataBind); f.Layout();
                    var background = (SolidColorBrush)Application.Current.Resources["BackgroundBrush"];
                    Assert(((SolidColorBrush)main.Background).Color == background.Color, "main page lost the shared theme background");
                    Fr0317Render(f.Root, Path.Combine(_directory, "theme040-main-" + mode + ".png"));
                    Fr0317Call(main, "Settings_Click", main, new RoutedEventArgs()); f.Layout();
                    Fr0317Render(f.Root, Path.Combine(_directory, "theme040-settings-" + mode + ".png"));
                    Fr0317Call(main, "SetExpandedPlayerVisible", true, true);
                    Fr0317Call(main, "CancelExpandedReveal");
                    var fullscreen = Fh040Window(main); var root = (Grid)fullscreen.Content;
                    root.Measure(f.Size); root.Arrange(new Rect(f.Size)); root.UpdateLayout();
                    Fr0317Render(root, Path.Combine(_directory, "theme040-fullscreen-" + mode + ".png"));
                    var ambient = Fr0317Get<AmbientBackdrop>(main, "_expandedAmbient");
                    Assert(((SolidColorBrush)ambient.Background).Color == ThemeManager.Brush("#11151C").Color,
                        "decorative atmosphere retained a dark backing in light mode");
                    Assert(!main.IsVisible && !fullscreen.IsVisible && PresentationSource.FromVisual(fullscreen) is null,
                        "integrated appearance render showed a native window");
                    Fr0317Call(main, "CloseExpandedPlayer");
                }
            }
            finally { ThemeManager.SetMode(previousMode); }
        });
    }
}
