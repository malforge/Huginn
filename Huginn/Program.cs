using Avalonia;
using System;
using System.Linq;
using Huginn.Services;
using Huginn.Services.Agent;
using Velopack;

namespace Huginn;

sealed class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // Serve agents and exit, before anything touches Velopack or a window. The protocol owns
        // stdout, so nothing else may be started that might write to it.
        if (args.Any(a => string.Equals(a, "--mcp", StringComparison.OrdinalIgnoreCase)))
            return McpServer.Run();

        // Must run before Avalonia: handles the --veloapp-* hooks the Velopack
        // installer/updater invokes (first-install, uninstall, post-update, etc.)
        // and exits the process for those modes. No-op on normal launches.
        VelopackApp.Build().Run();

        using SingleInstance? instance = SingleInstance.TryAcquire(AppSettings.FolderName);
        if (instance == null)
        {
            if (!SingleInstance.SignalRunning(AppSettings.FolderName, TimeSpan.FromSeconds(5)))
                Log.Warn("Huginn is already running but did not answer when asked to show its window. This launch exits.");
            return 0;
        }

        App.InitializeServices(instance);

        BuildAvaloniaApp()
            .StartWithClassicDesktopLifetime(args);

        return 0;
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
