using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Huginn.Models;
using Huginn.Services;
using Huginn.Services.Updates;

namespace Huginn.ViewModels;

public partial class MainWindowViewModel : ViewModelBase, IDisposable
{
    private readonly AppSettings _settings;
    private PollingService? _poller;
    private IBadgeService? _badge;
    private readonly INotificationService _notifications;
    private readonly IAutoStartService _autoStart;

    /// <summary>
    /// Shared settings instance — used by the View for window state persistence.
    /// </summary>
    public AppSettings Settings => _settings;

    // Priority-ordered sections
    public ObservableCollection<PullRequestItem> FailedValidationPrs { get; } = [];
    public ObservableCollection<PullRequestItem> UnstaffedPrs { get; } = [];
    public ObservableCollection<PullRequestItem> AutoCompleteOffPrs { get; } = [];
    public ObservableCollection<PullRequestItem> ReadyToReviewPrs { get; } = [];
    public ObservableCollection<PullRequestItem> MyActivePrs { get; } = [];
    public ObservableCollection<BuildItem> FailedBuilds { get; } = [];
    public ObservableCollection<BuildItem> RetryingBuilds { get; } = [];
    public ObservableCollection<PullRequestItem> AcknowledgedPrs { get; } = [];
    public ObservableCollection<BuildItem> AcknowledgedBuilds { get; } = [];

    /// <summary>Health of every source, in the order they appear in settings.</summary>
    public ObservableCollection<ConnectionStatus> Connections { get; } = [];

    public ConnectionStatus AdoStatus { get; } =
        new() { Kind = ConnectionKind.AzureDevOps, Title = "Azure DevOps" };

    public ConnectionStatus SentryStatus { get; } =
        new() { Kind = ConnectionKind.Sentry, Title = "Sentry" };

    public ConnectionStatus AppInsightsStatus { get; } =
        new() { Kind = ConnectionKind.AppInsights, Title = "Application Insights" };

    [ObservableProperty] private string _statusText = "Not connected";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasConnectionError))]
    private string? _connectionError;
    public bool HasConnectionError => !string.IsNullOrEmpty(ConnectionError);
    [ObservableProperty] private bool _hasFailedValidation;
    [ObservableProperty] private bool _hasUnstaffedPrs;
    [ObservableProperty] private bool _hasAutoCompleteOffPrs;
    [ObservableProperty] private bool _hasMyActivePrs;
    [ObservableProperty] private bool _hasFailedBuilds;
    [ObservableProperty] private bool _hasRetryingBuilds;
    [ObservableProperty] private bool _hasAcknowledged;
    [ObservableProperty] private bool _isSettingsVisible;
    [ObservableProperty] private bool _isConnected;
    [ObservableProperty] private bool _isRefreshing;
    [ObservableProperty] private bool _showApproved;
    [ObservableProperty] private bool _updateAvailable;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasHiddenApproved))]
    private int _hiddenApprovedCount;
    public bool HasHiddenApproved => HiddenApprovedCount > 0;

    // Settings fields
    [ObservableProperty] private string _organization = "";
    [ObservableProperty] private string _project = "";
    [ObservableProperty] private string _pat = "";
    [ObservableProperty] private string _sentryOrganization = "";
    [ObservableProperty] private string _sentryRegionUrl = "";
    [ObservableProperty] private string _sentryToken = "";
    [ObservableProperty] private string _appInsightsTenantId = "";
    [ObservableProperty] private string _appInsightsClientId = "";
    [ObservableProperty] private string _appInsightsAccount = "";
    [ObservableProperty] private bool _isAdoExpanded;
    [ObservableProperty] private bool _isSentryExpanded;
    [ObservableProperty] private bool _isAppInsightsExpanded;
    [ObservableProperty] private bool _monitorMyBuilds;
    [ObservableProperty] private bool _autoStartEnabled;

    // Pipeline picker
    public ObservableCollection<SelectablePipeline> AvailablePipelines { get; } = [];
    public ObservableCollection<SelectablePipeline> FilteredPipelines { get; } = [];
    [ObservableProperty] private bool _isLoadingPipelines;
    [ObservableProperty] private string _pipelinePickerStatus = "";
    [ObservableProperty] private string _pipelinePickerTooltip = "";
    [ObservableProperty] private string _pipelineFilter = "";

    // Application Insights component picker
    public ObservableCollection<SelectableComponent> AvailableComponents { get; } = [];
    public ObservableCollection<SelectableComponent> FilteredComponents { get; } = [];
    [ObservableProperty] private bool _isLoadingComponents;
    [ObservableProperty] private string _componentPickerStatus = "";
    [ObservableProperty] private string _componentFilter = "";

    // Cached data for re-categorization on filter change
    private PollResult? _lastPollResult;

    public MainWindowViewModel(AppSettings settings, INotificationService notifications)
    {
        _settings = settings;
        _notifications = notifications;
        _autoStart = PlatformFactory.CreateAutoStartService();
        Organization = _settings.Organization;
        Project = _settings.Project;
        Pat = _settings.GetPat() ?? "";
        SentryOrganization = _settings.SentryOrganization;
        SentryRegionUrl = _settings.SentryRegionUrl;
        AppInsightsTenantId = _settings.AppInsightsTenantId;
        AppInsightsClientId = _settings.AppInsightsClientId;
        SentryToken = _settings.GetSentryToken() ?? "";
        MonitorMyBuilds = _settings.MonitorMyBuilds;
        AutoStartEnabled = _autoStart.IsEnabled;

        UpdateService.Instance.PropertyChanged += OnUpdateServicePropertyChanged;
        UpdateService.Instance.StartPolling();

        Connections.Add(AdoStatus);
        Connections.Add(SentryStatus);
        Connections.Add(AppInsightsStatus);
        foreach (var connection in Connections)
            connection.PropertyChanged += (_, _) => RefreshConnectionBanner();

        if (_settings.IsSentryConfigured)
            SentryStatus.Set(ConnectionState.Connected, "Configured, not yet polled");

        _ = ResumeAzureSignInAsync();

        if (_settings.IsAdoConfigured)
        {
            _ = ConnectAsync();
        }
        else
        {
            ExpandConnectionsNeedingAttention();
            IsSettingsVisible = true;
        }

        RefreshConnectionBanner();
    }

    private void OnUpdateServicePropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        // Every property on UpdateService can shift one or more of the derived
        // labels below, so just re-raise the lot rather than tracking which
        // depends on which.
        UpdateAvailable = UpdateService.Instance.State == UpdateState.UpdateReady;
        OnPropertyChanged(nameof(CurrentVersionText));
        OnPropertyChanged(nameof(UpdateStatusLabel));
        OnPropertyChanged(nameof(UpdateStatusText));
        OnPropertyChanged(nameof(CanCheckForUpdates));
        OnPropertyChanged(nameof(ReleaseNotesText));
    }

    public string CurrentVersionText => $"v{UpdateService.Instance.CurrentVersion}";

    public string UpdateStatusLabel => UpdateService.Instance.State switch
    {
        UpdateState.UpdateReady => $"Update ready: v{UpdateService.Instance.AvailableVersion}",
        UpdateState.Checking => "Checking for updates…",
        UpdateState.Failed => "Update check failed",
        UpdateState.UpToDate => "Up to date",
        _ => "Updates",
    };

    public string UpdateStatusText
    {
        get
        {
            if (!UpdateService.Instance.CanUpdate)
                return "Auto-update is only active in installed builds — this is a dev/local-publish build, so updates won't apply here. Install from a GitHub Release to enable.";
            return UpdateService.Instance.State switch
            {
                UpdateState.Idle => "Huginn checks for new versions on launch and every 30 minutes thereafter.",
                UpdateState.Checking => "Talking to GitHub…",
                UpdateState.UpToDate => "You're on the latest version. Huginn rechecks every 30 minutes.",
                UpdateState.UpdateReady => $"v{UpdateService.Instance.AvailableVersion} has been downloaded and will install the next time you launch Huginn — or click Restart now to do it immediately.",
                UpdateState.Failed => UpdateService.Instance.ErrorMessage ?? "Last check failed. Will retry on the next interval.",
                _ => string.Empty,
            };
        }
    }

    public bool CanCheckForUpdates =>
        UpdateService.Instance.CanUpdate && UpdateService.Instance.State != UpdateState.Checking;

    public string ReleaseNotesText =>
        UpdateService.Instance.ReleaseNotesMarkdown ?? "Loading release notes…";

    [RelayCommand]
    private Task CheckForUpdates() => UpdateService.Instance.CheckAsync();

    /// <summary>
    /// Called from the view once the window HWND is available.
    /// </summary>
    public void InitializeTaskbarBadge(IntPtr hwnd)
    {
        _badge = PlatformFactory.CreateBadgeService();
        _badge.Initialize(hwnd);
    }

    [RelayCommand]
    private void ToggleSettings()
    {
        IsSettingsVisible = !IsSettingsVisible;
        if (!IsSettingsVisible) return;
        ExpandConnectionsNeedingAttention();
        _ = LoadPipelinesAsync();
    }

    /// <summary>
    /// Opens the connections the user has to act on and collapses the rest, so a healthy setup
    /// shows one line per source instead of three screens of setup instructions.
    /// </summary>
    private void ExpandConnectionsNeedingAttention()
    {
        IsAdoExpanded = AdoStatus.NeedsAttention;
        IsSentryExpanded = SentryStatus.NeedsAttention;
        IsAppInsightsExpanded = AppInsightsStatus.NeedsAttention;
    }

    /// <summary>
    /// Collapses every unhealthy connection into the single banner slot. Naming at most two keeps
    /// the header a fixed height whatever the number of sources.
    /// </summary>
    private void RefreshConnectionBanner()
    {
        var unhealthy = Connections.Where(c => c.NeedsAttention).ToList();
        ConnectionError = unhealthy.Count switch
        {
            0 => null,
            1 => $"{unhealthy[0].Title}: {unhealthy[0].StateText}.",
            2 => $"{unhealthy[0].Title}: {unhealthy[0].StateText}. "
               + $"{unhealthy[1].Title}: {unhealthy[1].StateText}.",
            _ => $"{unhealthy.Count} connections need attention.",
        };
    }

    partial void OnPipelineFilterChanged(string value) => ApplyPipelineFilter();

    partial void OnComponentFilterChanged(string value) => ApplyComponentFilter();

    partial void OnAutoStartEnabledChanged(bool value)
    {
        if (value)
        {
            var exe = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exe))
                _autoStart.Enable(exe);
        }
        else
        {
            _autoStart.Disable();
        }
    }

    private void ApplyPipelineFilter()
    {
        FilteredPipelines.Clear();
        var filter = PipelineFilter?.Trim() ?? "";
        foreach (var p in AvailablePipelines)
        {
            if (filter.Length == 0 || p.DisplayName.Contains(filter, StringComparison.OrdinalIgnoreCase))
                FilteredPipelines.Add(p);
        }
    }

    partial void OnShowApprovedChanged(bool value)
    {
        if (_lastPollResult != null)
            HandlePollResult(_lastPollResult);
    }

    [RelayCommand]
    private Task TestConnectionAsync(ConnectionKind kind) => kind switch
    {
        ConnectionKind.AzureDevOps => TestAzureDevOpsAsync(),
        ConnectionKind.Sentry => TestSentryAsync(),
        ConnectionKind.AppInsights => TestAppInsightsAsync(),
        _ => Task.CompletedTask,
    };

    /// <summary>
    /// Tests what is currently typed into the settings fields rather than what is saved, so the
    /// user can check credentials before committing them.
    /// </summary>
    private Task TestAzureDevOpsAsync()
    {
        if (string.IsNullOrWhiteSpace(Organization) || string.IsNullOrWhiteSpace(Pat))
        {
            AdoStatus.Set(ConnectionState.NotConfigured, "Organization and PAT are required");
            return Task.CompletedTask;
        }

        return RunConnectionTestAsync(AdoStatus, async () =>
        {
            using var client = new AdoApiClient(Organization.Trim(), Project.Trim(), Pat.Trim());
            var userId = await client.GetMyProfileIdAsync();
            return string.IsNullOrEmpty(userId)
                ? (ConnectionState.AuthFailed, "Auth failed, check your PAT")
                : (ConnectionState.Connected, $"Connected as {userId[..Math.Min(8, userId.Length)]}…");
        }, SaveAdoConnection);
    }

    private AzureSignIn CreateAzureSignIn() =>
        new("appinsights", AppInsightsTenantId?.Trim(), AppInsightsClientId?.Trim());

    /// <summary>
    /// Picks up a previous sign-in without prompting, so a restart does not demand the browser.
    /// </summary>
    private async Task ResumeAzureSignInAsync()
    {
        var signIn = CreateAzureSignIn();
        if (!signIn.HasStoredAccount)
        {
            AppInsightsStatus.Set(ConnectionState.NotConfigured);
            return;
        }

        try
        {
            var (_, account) = await signIn.GetCredentialAsync(allowPrompt: false);
            AppInsightsAccount = account;
            // Name what is missing rather than the state it is in: the banner is a list of things
            // to act on, and "signed in" is not one of them.
            AppInsightsStatus.Set(
                _settings.IsAppInsightsConfigured ? ConnectionState.Connected : ConnectionState.NotConfigured,
                _settings.IsAppInsightsConfigured ? $"Signed in as {account}" : "No resources selected");
        }
        catch (Exception ex)
        {
            AppInsightsStatus.Set(ConnectionState.AuthFailed, ex.Message);
        }
    }

    /// <summary>
    /// Sign-in opens the system default browser and then waits for it to redirect back. Closing
    /// that browser strands the wait, so it is bounded and cancellable.
    /// </summary>
    private static readonly TimeSpan SignInTimeout = TimeSpan.FromMinutes(5);

    private CancellationTokenSource? _azureSignInCts;

    [RelayCommand]
    private async Task SignInToAzureAsync()
    {
        CancelAzureSignIn();
        _azureSignInCts = new CancellationTokenSource(SignInTimeout);
        var ct = _azureSignInCts.Token;

        AppInsightsStatus.Set(ConnectionState.Connecting, "Waiting for your browser… (Cancel to stop)");
        try
        {
            var (_, account) = await CreateAzureSignIn().GetCredentialAsync(allowPrompt: true, ct);
            AppInsightsAccount = account;
            SaveAppInsightsConnection();
            AppInsightsStatus.Set(ConnectionState.Connected, $"Signed in as {account}");
            await LoadComponentsAsync();
        }
        catch (OperationCanceledException)
        {
            AppInsightsStatus.Set(ConnectionState.NotConfigured, "Sign-in cancelled");
        }
        catch (Exception ex)
        {
            AppInsightsStatus.Set(ConnectionState.Error, ex.Message);
        }
        finally
        {
            _azureSignInCts?.Dispose();
            _azureSignInCts = null;
        }
    }

    [RelayCommand]
    private void CancelAzureSignIn()
    {
        try
        {
            _azureSignInCts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Already finished; nothing to stop.
        }
    }

    [RelayCommand]
    private void SignOutOfAzure()
    {
        CreateAzureSignIn().SignOut();
        AppInsightsAccount = "";
        AvailableComponents.Clear();
        FilteredComponents.Clear();
        ComponentPickerStatus = "";
        AppInsightsStatus.Set(ConnectionState.NotConfigured);
    }

    [RelayCommand]
    private async Task LoadComponentsAsync()
    {
        var signIn = CreateAzureSignIn();
        if (!signIn.HasStoredAccount)
        {
            ComponentPickerStatus = "Sign in first";
            return;
        }

        IsLoadingComponents = true;
        ComponentPickerStatus = "Loading resources…";
        try
        {
            var (credential, _) = await signIn.GetCredentialAsync(allowPrompt: false);
            using var client = new AppInsightsApiClient(credential);
            var found = await client.GetComponentsAsync();

            var selected = new HashSet<string>(
                AvailableComponents.Where(c => c.IsSelected).Select(c => c.AppId));
            foreach (var id in _settings.WatchedAppInsightsAppIds)
                selected.Add(id);

            AvailableComponents.Clear();
            foreach (var c in found
                .OrderByDescending(c => selected.Contains(c.AppId))
                .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase))
            {
                AvailableComponents.Add(new SelectableComponent
                {
                    AppId = c.AppId,
                    DisplayName = c.Name,
                    Qualifier = c.Qualifier,
                    IsSelected = selected.Contains(c.AppId),
                });
            }

            ComponentPickerStatus = $"{found.Count} resource{(found.Count != 1 ? "s" : "")} visible to you";
            ComponentFilter = "";
            ApplyComponentFilter();
        }
        catch (Exception ex)
        {
            ComponentPickerStatus = ex.Message;
        }
        finally
        {
            IsLoadingComponents = false;
        }
    }

    private void ApplyComponentFilter()
    {
        FilteredComponents.Clear();
        var filter = ComponentFilter?.Trim() ?? "";
        foreach (var c in AvailableComponents)
        {
            if (filter.Length == 0
                || c.DisplayName.Contains(filter, StringComparison.OrdinalIgnoreCase)
                || c.Qualifier.Contains(filter, StringComparison.OrdinalIgnoreCase))
                FilteredComponents.Add(c);
        }
    }

    private Task TestAppInsightsAsync()
    {
        var signIn = CreateAzureSignIn();
        if (!signIn.HasStoredAccount)
        {
            AppInsightsStatus.Set(ConnectionState.NotConfigured, "Sign in first");
            return Task.CompletedTask;
        }

        var watched = AvailableComponents.Where(c => c.IsSelected).Select(c => c.AppId).ToList();
        if (watched.Count == 0) watched = _settings.WatchedAppInsightsAppIds;
        if (watched.Count == 0)
        {
            AppInsightsStatus.Set(ConnectionState.NotConfigured, "Pick at least one resource to watch");
            return Task.CompletedTask;
        }

        return RunConnectionTestAsync(AppInsightsStatus, async () =>
        {
            var (credential, account) = await signIn.GetCredentialAsync(allowPrompt: false);
            using var client = new AppInsightsApiClient(credential);

            // Cheapest query that proves both the token and the resource are usable.
            var table = await client.QueryAsync(watched[0], "requests | limit 1 | project timestamp");
            return (ConnectionState.Connected,
                $"Signed in as {account}, {watched.Count} resource{(watched.Count != 1 ? "s" : "")} readable"
                + (table.Rows.Count == 0 ? " (no recent requests)" : ""));
        }, SaveAppInsightsConnection);
    }

    private Task TestSentryAsync()
    {
        if (string.IsNullOrWhiteSpace(SentryOrganization) || string.IsNullOrWhiteSpace(SentryToken))
        {
            SentryStatus.Set(ConnectionState.NotConfigured, "Organization and token are required");
            return Task.CompletedTask;
        }

        return RunConnectionTestAsync(SentryStatus, async () =>
        {
            var region = string.IsNullOrWhiteSpace(SentryRegionUrl) ? "https://sentry.io" : SentryRegionUrl.Trim();
            using var client = new SentryApiClient($"{region.TrimEnd('/')}/api/0", SentryToken.Trim());
            var name = await client.GetOrganizationNameAsync(SentryOrganization.Trim());
            return string.IsNullOrEmpty(name)
                ? (ConnectionState.AuthFailed, "Auth failed, check the token and its scopes")
                : (ConnectionState.Connected, $"Connected to {name}");
        }, SaveSentryConnection);
    }

    /// <summary>
    /// Minimum time the Testing state stays on screen. These calls can finish faster than the eye
    /// registers, and an instant result on an already-connected source looks like a dead button.
    /// </summary>
    private static readonly TimeSpan MinimumTestFeedback = TimeSpan.FromMilliseconds(450);

    private static async Task RunConnectionTestAsync(
        ConnectionStatus status,
        Func<Task<(ConnectionState State, string Message)>> probe,
        Action? persist = null)
    {
        status.Set(ConnectionState.Connecting, "Testing…");
        var started = Stopwatch.StartNew();

        (ConnectionState State, string Message) result;
        try
        {
            result = await probe();
        }
        catch (Exception ex)
        {
            result = (ConnectionState.Error, ex.Message);
        }

        // Persist whatever was typed regardless of the outcome. A failing test is usually a
        // wrong region or scope rather than a wrong secret, and discarding the secret because the
        // test failed loses something the user often cannot get again.
        persist?.Invoke();
        var suffix = persist is null ? "" : ", saved";

        var remaining = MinimumTestFeedback - started.Elapsed;
        if (remaining > TimeSpan.Zero)
            await Task.Delay(remaining);

        status.Set(result.State, result.Message + suffix);
    }

    private void SaveAdoConnection()
    {
        _settings.Organization = Organization.Trim();
        _settings.Project = Project.Trim();
        if (!string.IsNullOrWhiteSpace(Pat)) _settings.SetPat(Pat.Trim());
        _settings.Save();
    }

    private void SaveSentryConnection()
    {
        _settings.SentryOrganization = SentryOrganization.Trim();
        _settings.SentryRegionUrl = SentryRegionUrl.Trim();
        if (!string.IsNullOrWhiteSpace(SentryToken)) _settings.SetSentryToken(SentryToken.Trim());
        _settings.Save();
    }

    private void SaveAppInsightsConnection()
    {
        _settings.AppInsightsTenantId = AppInsightsTenantId.Trim();
        _settings.AppInsightsClientId = AppInsightsClientId.Trim();
        _settings.WatchedAppInsightsAppIds = AvailableComponents
            .Where(c => c.IsSelected).Select(c => c.AppId).ToList();
        _settings.Save();
    }

    [RelayCommand]
    private async Task SaveAndConnectAsync()
    {
        _settings.Organization = Organization.Trim();
        _settings.Project = Project.Trim();
        _settings.SentryOrganization = SentryOrganization.Trim();
        _settings.SentryRegionUrl = SentryRegionUrl.Trim();
        _settings.AppInsightsTenantId = AppInsightsTenantId.Trim();
        _settings.AppInsightsClientId = AppInsightsClientId.Trim();
        _settings.WatchedAppInsightsAppIds = AvailableComponents
            .Where(c => c.IsSelected).Select(c => c.AppId).ToList();
        _settings.MonitorMyBuilds = MonitorMyBuilds;
        _settings.WatchedPipelineIds = AvailablePipelines
            .Where(p => p.IsSelected).Select(p => p.Id).ToList();
        _settings.Save();

        if (!string.IsNullOrWhiteSpace(Pat))
            _settings.SetPat(Pat.Trim());

        if (!string.IsNullOrWhiteSpace(SentryToken))
            _settings.SetSentryToken(SentryToken.Trim());

        SentryStatus.Set(
            _settings.IsSentryConfigured ? ConnectionState.Connected : ConnectionState.NotConfigured,
            _settings.IsSentryConfigured ? "Configured, not yet polled" : "");

        IsSettingsVisible = false;
        await ConnectAsync();
    }

    [RelayCommand]
    private async Task LoadPipelinesAsync()
    {
        if (string.IsNullOrWhiteSpace(Organization) || string.IsNullOrWhiteSpace(Project)
            || string.IsNullOrWhiteSpace(Pat))
        {
            PipelinePickerStatus = "⚠";
            PipelinePickerTooltip = "Fill in Organization, Project, and PAT first";
            return;
        }

        IsLoadingPipelines = true;
        PipelinePickerStatus = "Loading pipelines…";
        PipelinePickerTooltip = "";

        try
        {
            using var client = new AdoApiClient(Organization.Trim(), Project.Trim(), Pat.Trim());
            var definitions = await client.GetBuildDefinitionsAsync();

            // Preserve current selections across refresh
            var selected = new HashSet<int>(
                AvailablePipelines.Where(p => p.IsSelected).Select(p => p.Id));
            // Also include persisted selections for first load
            foreach (var id in _settings.WatchedPipelineIds)
                selected.Add(id);

            AvailablePipelines.Clear();
            foreach (var def in definitions
                .OrderByDescending(d => selected.Contains(d.Id))
                .ThenBy(d => d.DisplayName))
            {
                AvailablePipelines.Add(new SelectablePipeline
                {
                    Id = def.Id,
                    DisplayName = def.DisplayName,
                    IsSelected = selected.Contains(def.Id),
                });
            }

            PipelinePickerStatus = $"{definitions.Count} pipeline{(definitions.Count != 1 ? "s" : "")} found";
            PipelinePickerTooltip = "";
            PipelineFilter = "";
            ApplyPipelineFilter();
        }
        catch (Exception ex)
        {
            PipelinePickerStatus = "⚠";
            PipelinePickerTooltip = ex.Message;
        }
        finally
        {
            IsLoadingPipelines = false;
        }
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (_poller == null || !IsConnected) return;
        IsRefreshing = true;
        try
        {
            await _poller.PollNowAsync();
        }
        finally
        {
            IsRefreshing = false;
        }
    }

    [RelayCommand]
    private void OpenInBrowser(PullRequestItem? pr)
    {
        if (pr == null) return;
        var url = pr.DevOpsUrl(_settings.GetWebBaseUrl());
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch
        {
            // Ignore browser launch failures
        }
    }

    [RelayCommand]
    private async Task CopyPrLinkAsync(PullRequestItem? pr)
    {
        if (pr == null) return;
        var url = pr.DevOpsUrl(_settings.GetWebBaseUrl());
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop
            && desktop.MainWindow?.Clipboard is { } clipboard)
        {
            try
            {
                var transfer = new DataTransfer();
                transfer.Add(DataTransferItem.CreateText(url));
                await clipboard.SetDataAsync(transfer);
            }
            catch { }
        }
    }

    [RelayCommand]
    private void OpenBuildInBrowser(BuildItem? build)
    {
        if (build == null || string.IsNullOrEmpty(build.WebUrl)) return;
        try
        {
            Process.Start(new ProcessStartInfo(build.WebUrl) { UseShellExecute = true });
        }
        catch
        {
            // Ignore browser launch failures
        }
    }

    [RelayCommand]
    private void OpenRetryBuild(BuildItem? build)
    {
        if (build == null || string.IsNullOrEmpty(build.RetryBuildUrl)) return;
        try
        {
            Process.Start(new ProcessStartInfo(build.RetryBuildUrl) { UseShellExecute = true });
        }
        catch { }
    }

    [RelayCommand]
    private void ToggleBuildAcknowledged(BuildItem? build)
    {
        if (build == null) return;
        build.IsAcknowledged = !build.IsAcknowledged;
        _settings.SetBuildAcknowledged(build.Id, build.IsAcknowledged);
        if (_lastPollResult != null) HandlePollResult(_lastPollResult);
    }

    [RelayCommand]
    private void TogglePrAcknowledged(PullRequestItem? pr)
    {
        if (pr == null) return;
        pr.IsAcknowledged = !pr.IsAcknowledged;
        _settings.SetPrAcknowledged(pr.PullRequestId, pr.IsAcknowledged);
        if (_lastPollResult != null) HandlePollResult(_lastPollResult);
    }

    [RelayCommand]
    private void OpenPatPage()
    {
        var org = Organization?.Trim();
        if (string.IsNullOrWhiteSpace(org))
        {
            // The URL is organisation scoped, so there is nowhere to go until it is filled in.
            AdoStatus.Set(ConnectionState.NotConfigured, "Enter the Organization first");
            return;
        }

        OpenUrl($"https://dev.azure.com/{Uri.EscapeDataString(org)}/_usersSettings/tokens", AdoStatus);
    }

    /// <summary>
    /// Opens a link in the default browser, reporting failure through the connection rather than
    /// leaving a link that silently does nothing.
    /// </summary>
    private static void OpenUrl(string url, ConnectionStatus status)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Error($"Could not open {url}: {ex.Message}");
            status.Set(ConnectionState.Error, $"Could not open the browser: {ex.Message}");
        }
    }

    private void HandlePollResult(PollResult result)
    {
        _lastPollResult = result;

        // Apply persisted acknowledgements before categorization so badge math sees them.
        foreach (var pr in result.ReviewPrs.Concat(result.UnstaffedPrs).Concat(result.StaffedMyPrs))
            pr.IsAcknowledged = _settings.IsPrAcknowledged(pr.PullRequestId);
        foreach (var b in result.MyFailedBuilds.Concat(result.WatchedPipelineFailures))
            b.IsAcknowledged = _settings.IsBuildAcknowledged(b.Id);

        var cats = PrCategorizationService.Categorize(result, ShowApproved);

        FailedValidationPrs.Clear();
        UnstaffedPrs.Clear();
        AutoCompleteOffPrs.Clear();
        ReadyToReviewPrs.Clear();
        MyActivePrs.Clear();
        FailedBuilds.Clear();
        RetryingBuilds.Clear();
        AcknowledgedPrs.Clear();
        AcknowledgedBuilds.Clear();

        // Acknowledged items get pulled out of their priority sections and shown in a dedicated
        // neutral section at the bottom — preserves visibility but drops the urgency styling.
        AddPrs(cats.FailedValidation, FailedValidationPrs);
        AddPrs(cats.Unstaffed, UnstaffedPrs);
        AddPrs(cats.AutoCompleteOff, AutoCompleteOffPrs);
        AddPrs(cats.ReadyToReview, ReadyToReviewPrs);
        AddPrs(cats.MyActive, MyActivePrs);

        // Combine my failed builds + watched pipeline failures
        // Deduplicate by definition — keep the latest build per pipeline
        var allFailed = result.MyFailedBuilds.Concat(result.WatchedPipelineFailures)
            .GroupBy(b => b.DefinitionId)
            .Select(g => g.OrderByDescending(b => b.Id).First())
            .ToList();

        // Split into critical (no retry), de-escalated (retry in progress), or acknowledged
        foreach (var build in allFailed)
        {
            if (build.IsAcknowledged)
                AcknowledgedBuilds.Add(build);
            else if (build.RetryInProgress)
                RetryingBuilds.Add(build);
            else
                FailedBuilds.Add(build);
        }

        HasFailedValidation = FailedValidationPrs.Count > 0;
        HasUnstaffedPrs = UnstaffedPrs.Count > 0;
        HasAutoCompleteOffPrs = AutoCompleteOffPrs.Count > 0;
        HasMyActivePrs = MyActivePrs.Count > 0;
        HasFailedBuilds = FailedBuilds.Count > 0;
        HasRetryingBuilds = RetryingBuilds.Count > 0;
        HasAcknowledged = AcknowledgedPrs.Count > 0 || AcknowledgedBuilds.Count > 0;
        HiddenApprovedCount = cats.ApprovedByMeCount;

        UpdateBadge();

        // Drop acknowledgements for items that have aged out of poll results.
        var alivePrIds = result.ReviewPrs.Concat(result.UnstaffedPrs).Concat(result.StaffedMyPrs)
            .Select(p => p.PullRequestId).ToHashSet();
        var aliveBuildIds = result.MyFailedBuilds.Concat(result.WatchedPipelineFailures)
            .Select(b => b.Id).ToHashSet();
        _settings.PruneAcknowledgements(aliveBuildIds, alivePrIds);
    }

    private void AddPrs(IEnumerable<PullRequestItem> source, ObservableCollection<PullRequestItem> destination)
    {
        foreach (var pr in source)
        {
            if (pr.IsAcknowledged) AcknowledgedPrs.Add(pr);
            else destination.Add(pr);
        }
    }

    private void UpdateBadge()
    {
        // Acknowledged items live in their own collection now, so priority lists are inherently non-acked.
        var prBadge = FailedValidationPrs.Count(p => !p.IsApprovedByMe)
                    + UnstaffedPrs.Count(p => !p.IsApprovedByMe)
                    + AutoCompleteOffPrs.Count
                    + ReadyToReviewPrs.Count(p => !p.IsApprovedByMe);
        _badge?.SetBadge(prBadge + FailedBuilds.Count);
    }

    private async Task ConnectAsync()
    {
        ConnectionError = null;
        UnsubscribePollerEvents();
        _poller?.Dispose();

        _poller = new PollingService(_settings);

        _poller.PollCompleted += OnPollCompleted;
        _poller.NewPullRequestDetected += OnNewPullRequestDetected;
        _poller.NewBuildFailureDetected += OnNewBuildFailureDetected;
        _poller.StatusChanged += OnStatusChanged;
        _poller.ErrorOccurred += OnErrorOccurred;

        AdoStatus.Set(ConnectionState.Connecting);

        var ok = await _poller.StartAsync();
        IsConnected = ok;

        // A failure has already been reported through ErrorOccurred and set the state by now,
        // so only the success edge is left to record here.
        if (ok) AdoStatus.Set(ConnectionState.Connected);
    }

    private void OnPollCompleted(PollResult result)
        => Dispatcher.UIThread.Post(() => HandlePollResult(result));

    private void OnNewPullRequestDetected(PullRequestItem pr)
        => Dispatcher.UIThread.Post(() => _notifications.ShowNewPullRequest(
            pr.Title, pr.CreatedByName, pr.RepositoryName, pr.DevOpsUrl(_settings.GetWebBaseUrl())));

    private void OnNewBuildFailureDetected(BuildItem build)
        => Dispatcher.UIThread.Post(() => _notifications.ShowBuildFailed(
            build.DefinitionName, build.BranchShortName, build.WebUrl));

    private void OnStatusChanged(string msg)
        => Dispatcher.UIThread.Post(() => StatusText = msg);

    private void OnErrorOccurred(string msg)
        => Dispatcher.UIThread.Post(() =>
        {
            StatusText = $"⚠ {msg}";
            AdoStatus.Set(
                msg.Contains("auth", StringComparison.OrdinalIgnoreCase) || msg.Contains("PAT", StringComparison.Ordinal)
                    ? ConnectionState.AuthFailed
                    : ConnectionState.Error,
                msg);
            IsConnected = false;
        });

    [RelayCommand]
    private void OpenSettings()
    {
        IsSettingsVisible = true;
        ExpandConnectionsNeedingAttention();
        _ = LoadPipelinesAsync();
    }

    [RelayCommand]
    private void OpenSentryTokenPage()
    {
        _settings.SentryRegionUrl = SentryRegionUrl?.Trim() ?? "";
        OpenUrl(_settings.GetSentryTokenPageUrl(), SentryStatus);
    }

[RelayCommand]
    private void RestartNow() => UpdateService.Instance.ApplyAndRestart();

    private void UnsubscribePollerEvents()
    {
        if (_poller == null) return;
        _poller.PollCompleted -= OnPollCompleted;
        _poller.NewPullRequestDetected -= OnNewPullRequestDetected;
        _poller.NewBuildFailureDetected -= OnNewBuildFailureDetected;
        _poller.StatusChanged -= OnStatusChanged;
        _poller.ErrorOccurred -= OnErrorOccurred;
    }

    public void Dispose()
    {
        UpdateService.Instance.PropertyChanged -= OnUpdateServicePropertyChanged;
        UnsubscribePollerEvents();
        _poller?.Dispose();
        _badge?.Dispose();
    }
}
