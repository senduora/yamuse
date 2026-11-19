using System.Security.AccessControl;
using System.Security.Principal;
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
        var uninstallRegistration = UninstallSupport.WindowsUninstallRegistration.Inapplicable;
        if (!demo)
        {
            uninstallRegistration = UninstallSupport.CheckWindowsUninstallRegistration();
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
        window.InitializeFullUninstall(uninstallRegistration);
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
    internal static InstanceLease? TryAcquireProfileInstance(string legacyName = BrandPaths.LegacyInstanceMutex, string? profileName = null)
    {
        var local = new Mutex(true, legacyName, out var createdLocal);
        if (!createdLocal)
        {
            local.Dispose();
            return null;
        }

        Mutex? shared = null;
        var acquired = false;
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var sid = identity.User ?? throw new UnauthorizedAccessException("Не удалось определить владельца профиля Yamuse.");
            var security = new MutexSecurity();
            security.SetOwner(sid);
            security.AddAccessRule(new MutexAccessRule(sid, MutexRights.FullControl, AccessControlType.Allow));
            shared = MutexAcl.Create(true, profileName ?? @"Global\Yamuse.Profile." + sid.Value, out acquired, security);
            return acquired ? new InstanceLease(local, shared) : null;
        }
        finally
        {
            if (!acquired)
            {
                shared?.Dispose();
                local.ReleaseMutex();
                local.Dispose();
            }
        }
    }

    internal sealed class InstanceLease(Mutex local, Mutex shared) : IDisposable
    {
        private bool _released;

        public void Dispose()
        {
            if (_released) return;
            _released = true;
            shared.ReleaseMutex();
            shared.Dispose();
            local.ReleaseMutex();
            local.Dispose();
        }
    }

    internal static InstanceLease? TryBeginNormalStartup(Func<bool> blocksStartup, Action showExistingInstance,
        string legacyName = BrandPaths.LegacyInstanceMutex, string? profileName = null)
    {
        InstanceLease? instance;
        try { instance = TryAcquireProfileInstance(legacyName, profileName); }
        catch (Exception error) when (error is UnauthorizedAccessException or WaitHandleCannotBeOpenedException or System.ComponentModel.Win32Exception)
        {
            MessageBox.Show("Не удалось получить доступ к профилю Yamuse. Закройте другие экземпляры и повторите запуск.",
                AppBranding.Name, MessageBoxButton.OK, MessageBoxImage.Warning);
            return null;
        }
        if (instance is null)
        {
            showExistingInstance();
            return null;
        }

        var admitted = false;
        try
        {
            admitted = !blocksStartup();
            return admitted ? instance : null;
        }
        finally { if (!admitted) instance.Dispose(); }
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
