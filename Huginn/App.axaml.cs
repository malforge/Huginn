using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using System;
using Avalonia.Controls;
using Avalonia.Threading;
using Huginn.Services;
using Huginn.Services.Agent;
using Huginn.ViewModels;
using Huginn.Views;

namespace Huginn;

public partial class App : Application
{
    public static AppSettings Settings { get; private set; } = null!;
    public static INotificationService Notifications { get; private set; } = null!;

    /// <summary>Serves agents over HTTP for as long as the window runs.</summary>
    public static AgentEndpoint? AgentEndpoint { get; private set; }

    /// <summary>Why agents cannot reach this copy over HTTP, or null when they can.</summary>
    public static string? AgentEndpointError => AgentEndpoint?.Error;

    private static SingleInstance? _instance;

    /// <summary>
    /// Called from Program.Main before Avalonia starts, to initialize shared services.
    /// </summary>
    public static void InitializeServices(SingleInstance? instance)
    {
        _instance = instance;
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
            // A port that cannot be had leaves the rest of the app working, says why on the
            // Agents page, and is tried again until it can be had.
            AgentEndpoint = new AgentEndpoint(
                McpEndpoint.Port, Settings.EnsureAgentToken(), McpServer.Respond, AgentEndpoint.DefaultRetryInterval);
            AgentEndpoint.Start();
            desktop.Exit += (_, _) => AgentEndpoint?.Dispose();

            desktop.MainWindow = new MainWindow
            {
                DataContext = new MainWindowViewModel(Settings, Notifications),
            };

            if (_instance != null)
                _instance.ActivationRequested += () => Dispatcher.UIThread.Post(() => BringForward(desktop.MainWindow));
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>Shows the window to someone who launched Huginn again while it was running.</summary>
    private static void BringForward(Window? window)
    {
        if (window == null) return;
        if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
        window.Show();
        window.Activate();
    }
}
