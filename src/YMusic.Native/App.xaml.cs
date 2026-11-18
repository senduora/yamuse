using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace YMusic.Native;

public partial class App : Application
{
    internal static string DataDirectory => BrandPaths.Current.DataDirectory;
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var demo = e.Args.Contains("--demo", StringComparer.OrdinalIgnoreCase);
        if (!demo)
        {
            UninstallSupport.RegisterWindowsUninstall();
            try { BrandPaths.Current.MigrateLegacyData(); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
            {
                MessageBox.Show("Не удалось перенести данные в папки Yamuse. Приложение не будет открывать данные.\n\n" + error.Message +
                    "\n\nУстраните причину и повторите запуск: завершённая часть переноса сохранена.",
                    AppBranding.Name, MessageBoxButton.OK, MessageBoxImage.Warning);
                Shutdown();
                return;
            }
        }
        // A demo preview must not create the branded profile before the first
        // normal launch migrates the user's existing account and settings.
        var window = demo ? CreateDemoWindow(Directory.CreateTempSubdirectory("Yamuse-demo-").FullName) : new MainWindow();
        MainWindow = window;
        window.InitializeFullUninstall();
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
    internal static Mutex? TryBeginNormalStartup(string mutexName, Func<bool> blocksStartup, Action showExistingInstance)
    {
        var mutex = new Mutex(true, mutexName, out var created);
        if (!created)
        {
            // Another handle would keep the name alive even after the owner exits.
            mutex.Dispose();
            showExistingInstance();
            return null;
        }

        var admitted = false;
        try
        {
            admitted = !blocksStartup();
            return admitted ? mutex : null;
        }
        finally
        {
            if (!admitted)
            {
                mutex.ReleaseMutex();
                mutex.Dispose();
            }
        }
    }
    internal static MainWindow CreateDemoWindow(string directory)
    {
        var artwork = new ArtworkCache(Path.Combine(directory, "artwork"));
        Artwork.Cache = artwork;
        var window = new MainWindow(demo: true, restore: false, diagnosticsDirectory: directory);
        window.Closed += (_, _) => artwork.Dispose();
        return window;
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
