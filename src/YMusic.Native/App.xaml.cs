using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace YMusic.Native;

public partial class App : Application
{
    private Mutex? _instanceMutex;
    internal static string DataDirectory { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "YMusic.Native.Prototype");
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var demo = e.Args.Contains("--demo", StringComparer.OrdinalIgnoreCase);
        if (!demo)
        {
            _instanceMutex = new Mutex(true, @"Local\YMusic.Native.Prototype", out var created);
            if (!created)
            {
                MessageBox.Show("Yamuse уже открыт. Переключитесь на его окно.", AppBranding.Name, MessageBoxButton.OK, MessageBoxImage.Information);
                Shutdown();
                return;
            }
        }
        var window = new MainWindow(demo);
        MainWindow = window;
        if (!demo && e.Args.Contains("--login", StringComparer.OrdinalIgnoreCase))
            window.ContentRendered += async (_, _) => await window.StartLoginAsync();
        var captureIndex = Array.IndexOf(e.Args, "--capture");
        if (demo && captureIndex >= 0 && captureIndex + 1 < e.Args.Length)
        {
            window.ContentRendered += async (_, _) =>
            {
                await Task.Delay(1500);
                Capture(window, e.Args[captureIndex + 1]);
                window.Close();
            };
        }
        window.Show();
    }
    protected override void OnExit(ExitEventArgs e)
    {
        _instanceMutex?.Dispose();
        base.OnExit(e);
    }
    internal static void Capture(Window window, string path)
    {
        window.UpdateLayout();
        var image = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        image.Render(window);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var stream = File.Create(path);
        encoder.Save(stream);
    }
}
