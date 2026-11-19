using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using Microsoft.Win32.SafeHandles;
using Velopack;
using Velopack.Locators;

namespace YMusic.Native;

internal static class Program
{
    internal enum StartupMode { Normal, Demo, Uninstall, VelopackFastHook }
    internal static bool RecoverInstalledUpdateLocator { get; private set; }

    // Velopack 1.2.161 dispatches these fast hooks only as the first argument,
    // followed by a version. Keep them ahead of the application's own commands.
    private static readonly HashSet<string> VelopackFastHooks = new(StringComparer.OrdinalIgnoreCase)
    {
        "--veloapp-install", "--veloapp-updated", "--veloapp-obsolete", "--veloapp-uninstall",
        "--squirrel-install", "--squirrel-updated", "--squirrel-obsolete", "--squirrel-uninstall"
    };

    [STAThread]
    public static void Main(string[] args)
    {
        var mode = GetStartupMode(args);
        if (mode == StartupMode.Uninstall)
        {
            UninstallSupport.TryHandleCommand(args);
            return;
        }

        string? startupBlockReason = null;
        using var instanceMutex = mode == StartupMode.Normal
            ? App.TryBeginNormalStartup(
                () => (startupBlockReason = UninstallSupport.GetNormalStartupBlockReason()) is not null,
                () => MessageBox.Show("Yamuse уже открыт. Переключитесь на его окно.", AppBranding.Name, MessageBoxButton.OK, MessageBoxImage.Information))
            : null;
        if (mode == StartupMode.Normal && instanceMutex is null)
        {
            if (startupBlockReason is not null) UninstallSupport.ShowNormalStartupBlocked(startupBlockReason);
            return;
        }

        if (!ContinueToWindow(mode, StartupUpdaterFinished, () => RunVelopack(args), out var lifecycleFailed))
        {
            if (mode == StartupMode.Normal)
            {
                instanceMutex!.Dispose();
                MessageBox.Show("Yamuse ещё применяет обновление. Дождитесь завершения и запустите приложение снова.",
                    AppBranding.Name, MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else if (lifecycleFailed) Environment.ExitCode = -1;
            return;
        }

        if (mode == StartupMode.Normal)
            RecoverInstalledUpdateLocator = CanRecoverInstalledUpdateLocator(
                VelopackLocator.IsCurrentSet ? VelopackLocator.Current : null, Environment.ProcessPath!,
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
        var app = new App();
        app.InitializeComponent();
        app.Run();
    }

    internal static bool ContinueToWindow(StartupMode mode, Func<bool> updaterFinished, Action runLifecycle, out bool lifecycleFailed)
    {
        // The locator can create install directories and logs. A failure to
        // access them should not prevent an admitted normal launch.
        lifecycleFailed = false;
        if (mode == StartupMode.Demo) return true;
        if (mode == StartupMode.Normal && !updaterFinished()) return false;
        try { runLifecycle(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or Win32Exception)
        {
            lifecycleFailed = true;
            Trace.TraceWarning("Velopack startup: {0}", error.GetType().Name);
        }
        return mode == StartupMode.Normal;
    }

    private static void RunVelopack(string[] args)
    {
        VelopackApp.Build().SetArgs(args).SetAutoApplyOnStartup(false)
            .SetLocator(CreateStartupLocator(args, () => new StartupVelopackLocator(), () => UninstallSupport.RegisterWindowsUninstall()))
            .OnAfterUpdateFastCallback(_ => { UninstallSupport.RegisterWindowsUninstall(); })
            .Run();
    }

    internal static IVelopackLocator CreateStartupLocator(string[] args, Func<IVelopackLocator> create, Action registerUninstall)
    {
        try { return create(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or Win32Exception)
        {
            // The pinned SDK initializes its cache before entering the hook.
            // The after-update registration itself needs only the installed files.
            if (args.Length >= 2 && args[0].Equals("--veloapp-updated", StringComparison.OrdinalIgnoreCase)
                && SemanticVersion.TryParse(args[1], out _)) registerUninstall();
            throw;
        }
    }

    internal static bool CanRecoverInstalledUpdateLocator(IVelopackLocator? locator, string executable, string? localData = null,
        Func<string, FileAttributes>? readCurrentAttributes = null)
    {
        if (locator?.CurrentlyInstalledVersion is not { } version)
            return UninstallSupport.HasInstalledUpdateLayout(executable, requireManifest: false, localData: localData,
                readCurrentAttributes: readCurrentAttributes);
        try
        {
            if (string.IsNullOrEmpty(localData) || locator.IsPortable || locator.AppId != BrandPaths.LegacyPackageId
                || locator.RootAppDir is not { } root || locator.AppContentDir is not { } content) return false;
            root = UninstallPathGuard.Canonical(root);
            var updater = CheckedUpdateExecutable(locator, root, () => localData);
            if (updater.Equals(Path.Combine(root, "Update.exe"), StringComparison.OrdinalIgnoreCase)) return false;
            try
            {
                using var parents = UninstallPathGuard.HoldExistingAncestors(updater);
                using var file = UninstallPathGuard.OpenOwnedFile(updater, read: true, deleteAccess: false);
                return false; // The exact per-user updater is already available.
            }
            catch (Exception error) when (UninstallSupport.IsUnavailableInstallEvidence(error))
            {
                // Velopack can retain a versioned per-user locator without
                // publishing its fallback. Keep only a chance to re-probe.
                UninstallSupport.ValidateInstalledUpdateFiles(root, executable, UninstallPathGuard.Canonical(content), updater: null, version);
                return true;
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or Win32Exception or ArgumentException
            or InvalidOperationException or NotSupportedException) { return false; }
    }

    internal static bool CanUseInstalledUpdateLocator(IVelopackLocator locator, Func<string, bool>? entryExists = null,
        string? executable = null, Func<string>? localDataFolder = null)
    {
        try
        {
            if (locator.CurrentlyInstalledVersion is not { } version || locator.IsPortable || locator.AppId != BrandPaths.LegacyPackageId
                || locator.RootAppDir is not { } root || locator.AppContentDir is not { } content) return false;
            root = UninstallPathGuard.Canonical(root);
            if (entryExists?.Invoke(Path.Combine(root, ".portable")) == true) return false;
            var updater = CheckedUpdateExecutable(locator, root,
                localDataFolder ?? (() => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)));
            UninstallSupport.ValidateInstalledUpdateFiles(root, executable ?? Environment.ProcessPath!,
                UninstallPathGuard.Canonical(content), updater, version);
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or Win32Exception or ArgumentException
            or InvalidOperationException or NotSupportedException)
        { return false; }
    }

    internal static string CheckedUpdateExecutable(IVelopackLocator locator, string root, Func<string> localDataFolder)
    {
        var updater = UninstallPathGuard.Canonical(locator.UpdateExePath
            ?? throw new IOException("Программа установки обновлений недоступна."));
        if (updater.Equals(Path.Combine(root, "Update.exe"), StringComparison.OrdinalIgnoreCase)) return updater;

        var fallback = Path.Combine(UninstallPathGuard.Canonical(localDataFolder()), BrandPaths.LegacyPackageId);
        if (!updater.Equals(Path.Combine(fallback, "Update.exe"), StringComparison.OrdinalIgnoreCase)
            || locator.PackagesDir is not { } packages
            || !UninstallPathGuard.Canonical(packages).Equals(Path.Combine(fallback, "packages"), StringComparison.OrdinalIgnoreCase))
            throw new IOException("Расположение программы установки обновлений некорректно.");
        return updater;
    }

    internal static IVelopackLocator CreateRecoveredUpdateLocator()
    {
        var locator = CreateRecoveredUpdateLocator(Environment.ProcessPath!, () => new StartupVelopackLocator());
        UninstallSupport.RegisterWindowsUninstall();
        return locator;
    }

    internal static IVelopackLocator CreateRecoveredUpdateLocator(string executable, Func<IVelopackLocator> create,
        Func<string>? localDataFolder = null)
    {
        if (!UninstallSupport.HasInstalledUpdateLayout(executable))
            throw new IOException("Не удалось проверить установленный пакет обновления Yamuse.");
        var locator = create();
        if (!CanUseInstalledUpdateLocator(locator, executable: executable, localDataFolder: localDataFolder))
            throw new InvalidOperationException("Установленный пакет обновления не распознан.");
        return locator;
    }

    private static bool StartupUpdaterFinished()
    {
        var images = StartupUpdaterImages(Environment.ProcessPath,
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
        return WaitForUpdaterExit(images, waitMilliseconds: 5000);
    }

    internal static string[] StartupUpdaterImages(string? executable, string localData, Func<string, string>? normalize = null)
    {
        // Velopack's installed layout places the app in current and the updater
        // one level above it. A read-only installation can use the per-user copy.
        // This probe is independent of the contents and permissions of packages.
        if (executable is not null) executable = (normalize ?? NormalizeImagePath)(executable);
        if (!string.Equals(Path.GetFileName(executable), BrandPaths.LegacyPackageId + ".exe", StringComparison.OrdinalIgnoreCase)
            || Path.GetDirectoryName(executable) is not { } content
            || !string.Equals(Path.GetFileName(content), "current", StringComparison.OrdinalIgnoreCase)
            || Path.GetDirectoryName(content) is not { } root || File.Exists(Path.Combine(root, ".portable")))
            return [];
        var primary = Path.Combine(root, "Update.exe");
        if (!Path.IsPathFullyQualified(localData)) return [primary];
        var fallback = (normalize ?? NormalizeImagePath)(Path.Combine(localData, BrandPaths.LegacyPackageId, "Update.exe"));
        return primary.Equals(fallback, StringComparison.OrdinalIgnoreCase) ? [primary] : [primary, fallback];
    }

    // Velopack can stop mapped processes in this installation during apply.
    // Wait only for a matching running image and check/wait on one OS handle.
    // Fast hooks must bypass this check because the updater itself waits for them.
    internal static bool WaitForUpdaterExit(IReadOnlyList<string> updaterPaths, uint waitMilliseconds)
    {
        if (updaterPaths.Count == 0) return true;
        var start = Stopwatch.GetTimestamp();
        var normalized = updaterPaths.Select(NormalizeImagePath).ToArray();
        var candidates = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(normalized[0]));
        try
        {
            foreach (var candidate in candidates)
            {
                const uint syncAndQueryLimitedInformation = 0x00101000;
                using var process = OpenProcess(syncAndQueryLimitedInformation, false, candidate.Id);
                if (process.IsInvalid) continue;
                var image = new StringBuilder(32768);
                var capacity = (uint)image.Capacity;
                if (!QueryFullProcessImageName(process, 0, image, ref capacity)
                    || !normalized.Contains(NormalizeImagePath(image.ToString()), StringComparer.OrdinalIgnoreCase))
                    continue;
                var remaining = (uint)Math.Max(0, waitMilliseconds - Stopwatch.GetElapsedTime(start).TotalMilliseconds);
                if (WaitForSingleObject(process, remaining) != 0) return false;
            }
            return true;
        }
        finally { foreach (var candidate in candidates) candidate.Dispose(); }
    }

    internal static string NormalizeImagePath(string path)
    {
        const string extendedLocal = @"\\?\";
        if (path.StartsWith(extendedLocal, StringComparison.Ordinal) && path.Length > 6 && path[5] == ':')
            path = path[4..];
        if (!Path.IsPathFullyQualified(path) || path.StartsWith(@"\\", StringComparison.Ordinal)) return path;
        var expanded = new StringBuilder(32768);
        var length = GetLongPathName(UninstallPathGuard.ToWin32Path(path), expanded, (uint)expanded.Capacity);
        if (length == 0 || length >= expanded.Capacity) return path;
        var longPath = expanded.ToString();
        return longPath.StartsWith(extendedLocal, StringComparison.Ordinal) ? longPath[4..] : longPath;
    }

    [DllImport("kernel32.dll", EntryPoint = "GetLongPathNameW", CharSet = CharSet.Unicode)]
    private static extern uint GetLongPathName(string shortPath, StringBuilder longPath, uint capacity);

    [DllImport("kernel32.dll")]
    private static extern SafeWaitHandle OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inherit, int processId);

    [DllImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(SafeWaitHandle process, uint flags, StringBuilder name, ref uint length);

    [DllImport("kernel32.dll")]
    private static extern uint WaitForSingleObject(SafeWaitHandle handle, uint milliseconds);

    internal static StartupMode GetStartupMode(string[] args)
    {
        if (args.Length >= 2 && VelopackFastHooks.Contains(args[0])) return StartupMode.VelopackFastHook;
        if (args.Contains(UninstallSupport.RequestArgument, StringComparer.OrdinalIgnoreCase)
            || args.Contains(UninstallSupport.CompleteArgument, StringComparer.OrdinalIgnoreCase)) return StartupMode.Uninstall;
        return args.Contains("--demo", StringComparer.OrdinalIgnoreCase) ? StartupMode.Demo : StartupMode.Normal;
    }

    // Run also cleans local packages with auto-apply disabled. Keep the SDK's
    // lifecycle hooks and install paths, and leave package access to UpdateSupport.
    private sealed class StartupVelopackLocator() : WindowsVelopackLocator(null, null)
    {
        public override List<VelopackAsset> GetLocalPackages() => [];
        public override VelopackAsset? GetLatestLocalFullPackage() => null;
    }
}
