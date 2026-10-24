using Velopack;

namespace YMusic.Native;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // Fast install/update/uninstall hooks must finish before any WPF or user
        // data is opened. A downloaded update never interrupts a normal launch.
        VelopackApp.Build().SetArgs(args).SetAutoApplyOnStartup(false)
            .OnAfterUpdateFastCallback(_ => { UninstallSupport.RegisterWindowsUninstall(); })
            .Run();

        if (UninstallSupport.TryHandleCommand(args)) return;
        UninstallSupport.RegisterWindowsUninstall();

        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}
