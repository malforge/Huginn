using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using System;
using Huginn.Services;
using Huginn.Services.Agent;
using Huginn.ViewModels;
using Huginn.Views;

namespace Huginn;

public partial class App : Application
{
    public static AppSettings Settings { get; private set; } = null!;
    public static INotificationService Notifications { get; private set; } = null!;

    /// <summary>Why agents cannot reach this copy over HTTP, or null when they can.</summary>
    public static string? AgentEndpointError { get; private set; }

    private static McpHttpHost? _agentHost;

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
            StartAgentEndpoint();
            desktop.Exit += (_, _) => _agentHost?.Dispose();

            desktop.MainWindow = new MainWindow
            {
                DataContext = new MainWindowViewModel(Settings, Notifications),
            };
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// Serves agents over HTTP for as long as the window runs. A port that cannot be had leaves
    /// the rest of the app working and says why on the Agents page.
    /// </summary>
    private static void StartAgentEndpoint()
    {
        try
        {
            _agentHost = new McpHttpHost(McpEndpoint.Port, Settings.EnsureAgentToken(), McpServer.Respond);
            _agentHost.Start();
            Log.Info($"MCP over HTTP at {McpEndpoint.Url}");
        }
        catch (Exception ex)
        {
            _agentHost?.Dispose();
            _agentHost = null;
            AgentEndpointError = $"Could not open {McpEndpoint.Url}: {ex.Message}";
            Log.Error(AgentEndpointError);
        }
    }
}