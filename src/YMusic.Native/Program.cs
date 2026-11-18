using System.Windows;
using Velopack;
using Velopack.Locators;

namespace YMusic.Native;

internal static class Program
{
    internal enum StartupMode { Normal, Demo, Uninstall, VelopackFastHook }

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
            ? App.TryBeginNormalStartup(BrandPaths.LegacyInstanceMutex,
                () => (startupBlockReason = UninstallSupport.GetNormalStartupBlockReason()) is not null,
                () => MessageBox.Show("Yamuse уже открыт. Переключитесь на его окно.", AppBranding.Name, MessageBoxButton.OK, MessageBoxImage.Information))
            : null;
        if (mode == StartupMode.Normal && instanceMutex is null)
        {
            if (startupBlockReason is not null) UninstallSupport.ShowNormalStartupBlocked(startupBlockReason);
            return;
        }

        // The locator can create install directories and logs. Initialize it
        // only for an admitted normal launch or an explicit lifecycle command.
        if (mode != StartupMode.Demo)
            VelopackApp.Build().SetArgs(args).SetAutoApplyOnStartup(false)
                .SetLocator(new StartupVelopackLocator())
                .OnAfterUpdateFastCallback(_ => { UninstallSupport.RegisterWindowsUninstall(); })
                .Run();

        if (mode == StartupMode.VelopackFastHook) return;
        var app = new App();
        app.InitializeComponent();
        app.Run();
    }

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
