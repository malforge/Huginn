using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Huginn.Services;
using Huginn.ViewModels;
using Huginn.Views;

namespace Huginn;

public partial class App : Application
{
    public static AppSettings Settings { get; private set; } = null!;
    public static INotificationService Notifications { get; private set; } = null!;

    /// <summary>
    /// Called from Program.Main before Avalonia starts, to initialize shared services.
    /// </summary>
    public static void InitializeServices()
    {
        var credentials = PlatformFactory.CreateCredentialStore();
        Settings = AppSettings.Load(credentials);
        Notifications = PlatformFactory.CreateNotificationService();
        Notifications.RegisterActivation();
    }

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow
            {
                DataContext = new MainWindowViewModel(Settings, Notifications),
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}