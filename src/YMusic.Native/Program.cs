using Velopack;

namespace YMusic.Native;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // Fast install/update/uninstall hooks must finish before any WPF or user
        // data is opened. A downloaded update never interrupts a normal launch.
        VelopackApp.Build().SetArgs(args).SetAutoApplyOnStartup(false).Run();

        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}
