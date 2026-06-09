using Avalonia;
using System;

namespace Huginn;

sealed class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
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
