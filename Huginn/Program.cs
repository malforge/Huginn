using Avalonia;
using System;
using Velopack;

namespace Huginn;

sealed class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // Must run before Avalonia: handles the --veloapp-* hooks the Velopack
        // installer/updater invokes (first-install, uninstall, post-update, etc.)
        // and exits the process for those modes. No-op on normal launches.
        VelopackApp.Build().Run();

        App.InitializeServices();

        BuildAvaloniaApp()
            .StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
