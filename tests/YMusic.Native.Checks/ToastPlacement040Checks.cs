using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using YMusic.Native;

internal static partial class Program
{
    private static async Task CheckToastPlacement040()
    {
        await Run("ToastPlacement040: plain accent text shares the footer with resources and has no card or progress bar", () =>
        {
            using var fixture = new Toast040Fixture("shell");
            var w = fixture.Window;
            var toast = Control<Border>(w, "StatusNotification");
            var shell = fixture.Layout;
            var footer = Control<Grid>(w, "FooterContent"); var text = Control<TextBlock>(w, "StatusText");
            Assert(ReferenceEquals(toast.Parent, footer) && ReferenceEquals(footer.Parent, Control<Border>(w, "MetricsBar"))
                && Grid.GetColumn(toast) == 1 && Grid.GetColumnSpan(toast) == 1 && Grid.GetRowSpan(toast) == 1
                && footer.ColumnDefinitions.Count == 2 && footer.ColumnDefinitions[0].Width.IsAuto && footer.ColumnDefinitions[1].Width.IsStar,
                "notification does not use the free space to the right of footer resources");
            Assert(toast.HorizontalAlignment == HorizontalAlignment.Right && toast.VerticalAlignment == VerticalAlignment.Bottom
                && Panel.GetZIndex(toast) > Panel.GetZIndex(Control<ContentControl>(w, "ExpandedPlayerHost")),
                "toast is not anchored bottom right above the inline expanded player");
            Assert(shell.RowDefinitions.Count == 3 && shell.ColumnDefinitions.Count == 2 && toast.MaxWidth <= 620
                && toast.Background is null && toast.BorderBrush is null && toast.BorderThickness == new Thickness()
                && toast.Padding == new Thickness() && toast.CornerRadius == new CornerRadius() && ReferenceEquals(toast.Child, text)
                && w.FindName("BusyBar") is null && text.TextWrapping == TextWrapping.NoWrap && text.TextTrimming == TextTrimming.CharacterEllipsis
                && text.FontSize == Control<TextBlock>(w, "MemoryText").FontSize && text.FontSize == Control<TextBlock>(w, "VersionText").FontSize,
                "notification retains a card, indicator, extra row or different footer typography");
            return Task.CompletedTask;
        });
        await Run("ToastPlacement040: minimum normal and large private layouts keep one line clear of footer links", () =>
        {
            using var fixture = new Toast040Fixture("geometry");
            var w = fixture.Window; var shell = fixture.Layout;
            var toast = Control<Border>(w, "StatusNotification"); var text = Control<TextBlock>(w, "StatusText");
            var player = Control<Border>(w, "PlayerBar"); var list = Control<ListView>(w, "TracksList");
            var metrics = Control<Border>(w, "MetricsBar"); var resources = Control<StackPanel>(w, "MetricsRow");
            var measurements = new List<object>();
            foreach (var size in new[] { new Size(1050, 680), new Size(1280, 860), new Size(1920, 1080) })
            {
                Toast040Invoke(w, "HideStatusNotification"); Toast040Layout(shell, size);
                var playerBefore = Toast040Bounds(player, shell); var listBefore = Toast040Bounds(list, shell);
                var rowsBefore = shell.RowDefinitions.Select(row => row.ActualHeight).ToArray();
                foreach (var message in new[] { "Трек добавлен в Мне нравится.",
                    string.Join(" ", Enumerable.Repeat("Не удалось завершить загрузку: проверьте подключение и повторите попытку.", 5)),
                    new string('Ж', 240) + " — конец длинного сообщения" })
                {
                    Toast040Invoke(w, "SetStatus", message, false); Toast040Layout(shell, size);
                    var bounds = Toast040Bounds(toast, shell); var textBounds = Toast040Bounds(text, toast);
                    Assert(Math.Abs(bounds.Right - (size.Width - metrics.Padding.Right)) < 1
                        && Math.Abs(bounds.Bottom - (size.Height - toast.Margin.Bottom)) < 1
                        && bounds.Left >= Toast040Bounds(resources, shell).Right + toast.Margin.Left - 1
                        && bounds.Top >= Toast040Bounds(metrics, shell).Top && bounds.Width <= toast.MaxWidth + 1,
                        "toast escaped the bottom right viewport at " + size);
                    Assert(text.Text == message && text.ToolTip as string == message && textBounds.Left >= toast.Padding.Left
                        && textBounds.Right <= toast.ActualWidth - toast.Padding.Right + 1 && textBounds.Bottom <= toast.ActualHeight - toast.Padding.Bottom + 1,
                        "long status text clips outside its padded notification at " + size);
                    Assert(Toast040Bounds(player, shell) == playerBefore && Toast040Bounds(list, shell) == listBefore
                        && shell.RowDefinitions.Select(row => row.ActualHeight).SequenceEqual(rowsBefore),
                        "showing a toast moved the player, list or shell rows at " + size);
                    Assert(text.ActualHeight <= 2 * text.FontSize, "status grew beyond one footer line");
                    measurements.Add(new { width = size.Width, height = size.Height, messageLength = message.Length, bounds, textBounds, textLinesHeight = text.ActualHeight });
                }
                Toast040Render(shell, size, "toast040-shell-" + (int)size.Width + ".png");
            }
            Toast040Evidence("geometry", new { measurements, windowsShown = false, fixture.Requests });
            Assert(fixture.Requests == 0, "toast geometry accessed the network");
            return Task.CompletedTask;
        });
        await Run("ToastPlacement040: the same border remains bottom right after moving into a single cell fullscreen surface", () =>
        {
            using var fixture = new Toast040Fixture("reparent"); var w = fixture.Window;
            var toast = Control<Border>(w, "StatusNotification"); var shell = fixture.Layout;
            var originalParent = (Grid)toast.Parent;
            var originalIndex = originalParent.Children.IndexOf(toast); var host = new Grid { Background = new SolidColorBrush(Color.FromRgb(18, 19, 23)) };
            Toast040Invoke(w, "SetStatus", "Сообщение на отдельной поверхности · длинный текст переносится и остаётся внутри окна.", true);
            var timer = Toast040Field<DispatcherTimer>(w, "_statusDismissTimer");
            var version = Toast040Field<int>(w, "_statusNotificationVersion");
            originalParent.Children.Remove(toast); host.Children.Add(toast);
            try
            {
                foreach (var size in new[] { new Size(1050, 680), new Size(1280, 860), new Size(1920, 1080) })
                {
                    Toast040Layout(host, size); var bounds = Toast040Bounds(toast, host);
                    Assert(host.RowDefinitions.Count == 0 && host.ColumnDefinitions.Count == 0
                        && Math.Abs(bounds.Right - (size.Width - toast.Margin.Right)) < 1
                        && Math.Abs(bounds.Bottom - (size.Height - toast.Margin.Bottom)) < 1 && bounds.Left >= 0 && bounds.Top >= 0,
                        "the reparented toast depends on the main shell's rows or columns");
                }
                Assert(ReferenceEquals(timer, Toast040Field<DispatcherTimer>(w, "_statusDismissTimer"))
                    && version == Toast040Field<int>(w, "_statusNotificationVersion") && timer.IsEnabled,
                    "reparenting restarted or replaced notification state");
                Toast040Render(host, new Size(1280, 860), "toast040-fullscreen-surface.png");
            }
            finally { host.Children.Remove(toast); originalParent.Children.Insert(originalIndex, toast); }
            Toast040Layout(shell, new Size(1280, 860));
            Assert(ReferenceEquals(toast.Parent, originalParent) && originalParent.Children.IndexOf(toast) == originalIndex
                && toast.Visibility == Visibility.Visible, "restoring the notification lost its original parent, index or visibility");
            return Task.CompletedTask;
        });
        await Run("ToastPlacement040: normal error busy replacement and close preserve notification lifetime guards", async () =>
        {
            using var fixture = new Toast040Fixture("lifetime"); var w = fixture.Window;
            var toast = Control<Border>(w, "StatusNotification");
            Toast040Invoke(w, "SetStatus", "Обычное сообщение", false);
            var timer = Toast040Field<DispatcherTimer>(w, "_statusDismissTimer");
            Assert(timer.IsEnabled && timer.Interval == TimeSpan.FromSeconds(3.2), "normal notification lifetime changed");
            Toast040Invoke(w, "SetStatus", "Сообщение об ошибке", true);
            Assert(timer.IsEnabled && timer.Interval == TimeSpan.FromSeconds(6), "error notification lifetime changed");
            Toast040Invoke(w, "Busy", true);
            Assert(!timer.IsEnabled && Toast040Field<int>(w, "_busy") == 1 && toast.Child is TextBlock,
                "busy notification can disappear or replaced the text with an indicator");
            Toast040Invoke(w, "Busy", false);
            Assert(timer.IsEnabled && timer.Interval == TimeSpan.FromSeconds(6) && Toast040Field<int>(w, "_busy") == 0,
                "busy completion lost the error lifetime");
            Toast040Invoke(w, "HideStatusNotification"); var hiddenVersion = Toast040Field<int>(w, "_statusNotificationVersion");
            Toast040Invoke(w, "SetStatus", "Новое сообщение после скрытия", false);
            await Dispatcher.Yield(DispatcherPriority.ContextIdle);
            Assert(toast.Visibility == Visibility.Visible && toast.Opacity == 1 && Control<TextBlock>(w, "StatusText").Text.StartsWith("Новое сообщение")
                && Toast040Field<int>(w, "_statusNotificationVersion") > hiddenVersion, "hiding an older message hid its replacement");
            timer.Interval = TimeSpan.FromMilliseconds(30); await Task.Delay(100);
            Assert(!timer.IsEnabled && toast.Visibility == Visibility.Collapsed, "auto dismissal retained the overlay or timer");
            Toast040Invoke(w, "SetStatus", "Таймер останавливается при закрытии", false);
            var beforeClose = Toast040Field<int>(w, "_statusNotificationVersion"); fixture.Dispose();
            Assert(!timer.IsEnabled && Toast040Field<int>(w, "_statusNotificationVersion") > beforeClose, "close retained a timer or stale completion version");
        });
        await Run("ToastPlacement040: status and version follow the same live readable accent in both themes", async () =>
        {
            var previousMode = ThemeManager.Mode;
            using var fixture = new Toast040Fixture("accent"); var w = fixture.Window;
            try
            {
                var controller = Toast040Field<CoverAccentController>(w, "_coverAccent");
                var text = Control<TextBlock>(w, "StatusText"); var version = Control<TextBlock>(w, "VersionText");
                var memory = Control<TextBlock>(w, "MemoryText"); var cpu = Control<TextBlock>(w, "CpuText");
                foreach (var mode in new[] { "Dark", "Light" })
                {
                    ThemeManager.SetMode(mode); controller.Configure(false, "#55AAEE"); Ts040Seek(controller, 5.01);
                    foreach (var error in new[] { false, true })
                    {
                        Toast040Invoke(w, "SetStatus", "Проверка цвета уведомления", error);
                        await Dispatcher.Yield(DispatcherPriority.DataBind); Toast040Layout(fixture.Layout, new Size(1050, 680));
                        Assert(ReferenceEquals(text.Foreground, controller.Brushes["AccentTextBrush"])
                            && ReferenceEquals(version.Foreground, text.Foreground)
                            && ReferenceEquals(memory.Foreground, w.FindResource("MutedBrush")) && ReferenceEquals(cpu.Foreground, memory.Foreground),
                            "status/version lost the live accent or resource metrics were recoloured");
                        Assert(ReferenceEquals(Control<TextBlock>(w, "WaveStatus").Foreground, w.FindResource(error ? "ErrorBrush" : "MutedBrush")),
                            "footer accent changed inline error/readability colours");
                    }
                    controller.Retarget(Color.FromRgb(212, 83, 146)); Ts040Seek(controller, 2.5);
                    Assert(ReferenceEquals(text.Foreground, version.Foreground) && ReferenceEquals(text.Foreground, controller.Brushes["AccentTextBrush"]),
                        "an accent transition detached the footer brush");
                    Ts040Seek(controller, 5.01);
                    Toast040Render(fixture.Layout, new Size(1050, 680), "footer040-" + mode + ".png");
                }
            }
            finally { ThemeManager.SetMode(previousMode); }
        });
    }

    private static void Toast040Layout(FrameworkElement root, Size size) { root.Measure(size); root.Arrange(new Rect(new Point(), size)); root.UpdateLayout(); }
    private static Rect Toast040Bounds(FrameworkElement element, UIElement relativeTo) => new(element.TranslatePoint(new Point(), relativeTo), element.RenderSize);
    private static object? Toast040Invoke(MainWindow window, string method, params object?[] args) => typeof(MainWindow).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, args);
    private static T Toast040Field<T>(MainWindow window, string field) => (T)typeof(MainWindow).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
    private static void Toast040Evidence(string name, object data) => File.WriteAllText(Path.Combine(_directory, "toast040-" + name + ".json"),
        JsonSerializer.Serialize(new { data, productSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(MainWindow).Assembly.Location))), runtime = Environment.Version.ToString(), realAccountUsed = false }, new JsonSerializerOptions { WriteIndented = true }));
    private static void Toast040Render(FrameworkElement root, Size size, string filename)
    {
        Toast040Layout(root, size); var bitmap = new RenderTargetBitmap((int)size.Width, (int)size.Height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(root);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var output = File.Create(Path.Combine(_directory, filename)); encoder.Save(output);
    }
    private sealed class Toast040Fixture : IDisposable
    {
        internal MainWindow Window { get; }
        internal Grid Layout { get; }
        internal int Requests { get; private set; }
        private readonly MusicApi _api;
        private readonly NativePlayer _player;
        private bool _disposed;
        internal Toast040Fixture(string name)
        {
            var directory = Path.Combine(_directory, "toast040-" + name); Directory.CreateDirectory(directory);
            var handler = new FakeHandler((_, _) => { Requests++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)); });
            _api = new MusicApi(handler); _player = new NativePlayer(new AudioBuffer(Path.Combine(directory, "buffer"), handler));
            var preferences = new PreferencesStore(Path.Combine(directory, "preferences")); preferences.Save(new PlayerPreferences { Volume = 0 });
            Window = new MainWindow(api: _api, store: new SessionStore(Path.Combine(directory, "session")), restore: false,
                diagnosticsDirectory: Path.Combine(directory, "diagnostics"), offline: new OfflineLibrary(Path.Combine(directory, "offline"), new AudioBuffer(Path.Combine(directory, "downloads"), handler)),
                player: _player, preferences: preferences);
            Layout = (Grid)Window.Content; Window.Content = null;
            Toast040Invoke(Window, "ShowRows", (object)new[] { new TrackRow { Id = "toast-private", Title = "Проверка уведомлений", Artist = "Локальный пример", Available = true } });
            // Never Show: layout and PNG rendering use this detached private tree.
        }
        public void Dispose()
        {
            if (_disposed) return; _disposed = true;
            Window.Content = Layout; Window.Close(); _player.Dispose(); _api.Dispose();
        }
    }
}
