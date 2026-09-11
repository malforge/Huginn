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
    /// <summary>One section per watched Sentry project, since each is a separate app.</summary>
    public ObservableCollection<SentryProjectGroup> SentryGroups { get; } = [];

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

    // Sentry project picker
    public ObservableCollection<SelectableSentryProject> AvailableSentryProjects { get; } = [];
    [ObservableProperty] private bool _isLoadingSentryProjects;
    [ObservableProperty] private string _sentryProjectPickerStatus = "";

    // Application Insights component picker
    public ObservableCollection<SelectableComponent> AvailableComponents { get; } = [];
    public ObservableCollection<SelectableComponent> FilteredComponents { get; } = [];
    [ObservableProperty] private bool _isLoadingComponents;
    [ObservableProperty] private string _componentPickerStatus = "";
    [ObservableProperty] private string _componentFilter = "";

    // Cached data for re-categorization on filter change
    private PollResult? _lastPollResult;

    /// <summary>
    /// Sentry publishes separately from the Azure DevOps poll, so its last result is held here
    /// rather than on PollResult. Either source can repaint without waiting for the other.
    /// </summary>
    private List<SentryIssueItem> _lastSentryIssues = [];

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
        else if (_settings.HasSentryCredentials)
            SentryStatus.Set(ConnectionState.NotConfigured, "No projects selected");

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
        if (IsSettingsVisible)
        {
            CloseSettings();
            return;
        }

        IsSettingsVisible = true;
        _settingsOnOpen = ConnectionFingerprint();
        ExpandConnectionsNeedingAttention();
        _ = LoadPipelinesAsync();
        _ = LoadSentryProjectsAsync();
    }

    /// <summary>
    /// Everything typed into settings is committed when the panel closes. Leaving edits to an
    /// explicit save is how a credential that can only be copied once gets lost.
    /// </summary>
    public void CloseSettings()
    {
        IsSettingsVisible = false;
        CommitSettings();
    }

    /// <summary>Writes the settings out, reconnecting when the connection details have moved.</summary>
    public void CommitSettings()
    {
        var before = _settingsOnOpen;
        SaveAllConnections();
        _settingsOnOpen = ConnectionFingerprint();

        if (before is not null && before != _settingsOnOpen)
            _ = ConnectAsync();
    }

    private string? _settingsOnOpen;

    /// <summary>Identifies the connection details, so a reconnect only happens when they change.</summary>
    private string ConnectionFingerprint() => string.Join(
        '\u001f',
        Organization?.Trim(), Project?.Trim(), Pat?.Trim(),
        SentryOrganization?.Trim(), SentryRegionUrl?.Trim(), SentryToken?.Trim(),
        AppInsightsTenantId?.Trim(), AppInsightsClientId?.Trim());

    private void SaveAllConnections()
    {
        SaveAdoConnection();
        SaveSentryConnection();
        SaveAppInsightsConnection();
        _settings.MonitorMyBuilds = MonitorMyBuilds;
        _settings.WatchedPipelineIds = AvailablePipelines
            .Where(p => p.IsSelected).Select(p => p.Id).ToList();
        _settings.Save();
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

    [RelayCommand]
    private async Task LoadSentryProjectsAsync()
    {
        if (!_settings.HasSentryCredentials
            && (string.IsNullOrWhiteSpace(SentryOrganization) || string.IsNullOrWhiteSpace(SentryToken)))
        {
            SentryProjectPickerStatus = "Enter the organisation and token first";
            return;
        }

        IsLoadingSentryProjects = true;
        SentryProjectPickerStatus = "Loading projects…";
        try
        {
            var region = string.IsNullOrWhiteSpace(SentryRegionUrl) ? "https://sentry.io" : SentryRegionUrl.Trim();
            using var client = new SentryApiClient($"{region.TrimEnd('/')}/api/0", SentryToken.Trim());
            var slugs = await client.GetProjectSlugsAsync(SentryOrganization.Trim());

            var selected = new HashSet<string>(
                AvailableSentryProjects.Where(p => p.IsSelected).Select(p => p.Slug));
            foreach (var slug in _settings.WatchedSentryProjects)
                selected.Add(slug);

            AvailableSentryProjects.Clear();
            foreach (var slug in slugs)
            {
                var project = new SelectableSentryProject
                {
                    Slug = slug,
                    IsSelected = selected.Contains(slug),
                };

                // Ticking a box is an explicit choice and must not need a separate save.
                project.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(SelectableSentryProject.IsSelected))
                        PersistWatchedSentryProjects();
                };

                AvailableSentryProjects.Add(project);
            }

            SentryProjectPickerStatus = $"{slugs.Count} project{(slugs.Count != 1 ? "s" : "")} found";
        }
        catch (Exception ex)
        {
            SentryProjectPickerStatus = ex.Message;
        }
        finally
        {
            IsLoadingSentryProjects = false;
        }
    }

    /// <summary>
    /// Rebuilds the per-project sections from the last poll. Separate from the poll handler so a
    /// mute or a dismiss can repaint without re-running everything else.
    /// </summary>
    private void RebuildSentryList()
    {
        foreach (var issue in _lastSentryIssues)
        {
            issue.IsMuted = _settings.IsSentryIssueMuted(issue.Id);
            issue.IsFlagged = _settings.IsSentryIssueFlagged(issue.Id);
            issue.FlagReason = _settings.GetSentryFlagReason(issue.Id);
        }

        // Groups are updated in place rather than rebuilt, so a section keeps its own muted
        // toggle across polls.
        foreach (var byProject in _lastSentryIssues.GroupBy(i => i.ProjectSlug).OrderBy(g => g.Key))
        {
            var group = SentryGroups.FirstOrDefault(g => g.Slug == byProject.Key);
            if (group is null)
            {
                group = new SentryProjectGroup { Slug = byProject.Key };
                SentryGroups.Add(group);
            }

            group.SetIssues([.. byProject]);
        }

        // A project that has stopped reporting, or been unwatched, loses its section.
        var live = _lastSentryIssues.Select(i => i.ProjectSlug).ToHashSet();
        for (var i = SentryGroups.Count - 1; i >= 0; i--)
        {
            if (!live.Contains(SentryGroups[i].Slug)) SentryGroups.RemoveAt(i);
        }

    }

    [RelayCommand]
    private void ToggleSentryIssueMuted(SentryIssueItem? issue)
    {
        if (issue is null) return;

        if (issue.IsMuted)
        {
            _settings.UnmuteSentryIssue(issue.Id);
            issue.IsMuted = false;
            issue.MutedAtUserCount = 0;
        }
        else
        {
            // Muting is a stronger answer than dismissing, so it settles the alert too.
            _settings.MuteSentryIssue(issue.Id, issue.UserCount, issue.EventCount);
            _settings.DismissSentryIssue(issue.Id);
            issue.IsMuted = true;
            issue.IsFlagged = false;
            issue.FlagReason = "";
            issue.MutedAtUserCount = issue.UserCount;
        }

        RebuildSentryList();
    }

    /// <summary>
    /// Clears a raised alert, which is what lets the issue fall back into plain impact order.
    /// </summary>
    [RelayCommand]
    private void DismissSentryIssue(SentryIssueItem? issue)
    {
        if (issue is null) return;
        _settings.DismissSentryIssue(issue.Id);
        issue.IsFlagged = false;
        issue.FlagReason = "";
        RebuildSentryList();
    }

    private void PersistWatchedSentryProjects()
    {
        _settings.WatchedSentryProjects = AvailableSentryProjects
            .Where(p => p.IsSelected).Select(p => p.Slug).ToList();
        _settings.Save();

        SentryStatus.Set(
            _settings.IsSentryConfigured ? ConnectionState.Connected : ConnectionState.NotConfigured,
            _settings.IsSentryConfigured
                ? $"Watching {_settings.WatchedSentryProjects.Count} project"
                  + (_settings.WatchedSentryProjects.Count == 1 ? "" : "s")
                : "No projects selected");
    }

    [RelayCommand]
    private void OpenSentryIssue(SentryIssueItem? issue)
    {
        if (issue is null || string.IsNullOrEmpty(issue.Permalink)) return;
        OpenUrl(issue.Permalink, SentryStatus);
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
            var (credential, account) = await signIn.GetCredentialAsync(allowPrompt: false);

            if (!await signIn.IsStillSignedInAsync(credential))
            {
                AppInsightsAccount = "";
                AppInsightsStatus.Set(ConnectionState.AuthFailed, "Sign-in expired, sign in again");
                return;
            }

            AppInsightsAccount = account;
            ReportAppInsightsState();
        }
        catch (Exception ex)
        {
            AppInsightsAccount = "";
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
            ReportAppInsightsState();
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

    /// <summary>
    /// Being signed in and watching nothing is not the same as being signed out, and reporting
    /// them identically reads as having been logged out.
    /// </summary>
    private void ReportAppInsightsState()
    {
        if (string.IsNullOrEmpty(AppInsightsAccount))
        {
            AppInsightsStatus.Set(ConnectionState.NotConfigured, "Not signed in");
            return;
        }

        var watched = _settings.WatchedAppInsightsAppIds.Count;
        AppInsightsStatus.Set(
            watched > 0 ? ConnectionState.Connected : ConnectionState.NotConfigured,
            watched > 0
                ? $"{AppInsightsAccount}, watching {watched} resource{(watched == 1 ? "" : "s")}"
                : $"{AppInsightsAccount}, no resources selected");
    }

    [RelayCommand]
    private void SignOutOfAzure()
    {
        CreateAzureSignIn().SignOut();
        AppInsightsAccount = "";
        AvailableComponents.Clear();
        FilteredComponents.Clear();
        ComponentPickerStatus = "";
        ReportAppInsightsState();
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
            // The user pressed a button, so falling through to the browser when the cached
            // sign-in has lapsed is better than telling them interaction is required. Bounded,
            // because a browser that is closed rather than completed never comes back.
            using var cts = new CancellationTokenSource(SignInTimeout);
            var (credential, account) = await signIn.GetCredentialAsync(allowPrompt: true, cts.Token);
            AppInsightsAccount = account;
            using var client = new AppInsightsApiClient(credential);
            var found = await client.GetComponentsAsync(cts.Token);

            var selected = new HashSet<string>(
                AvailableComponents.Where(c => c.IsSelected).Select(c => c.AppId));
            foreach (var id in _settings.WatchedAppInsightsAppIds)
                selected.Add(id);

            AvailableComponents.Clear();
            foreach (var c in found
                .OrderByDescending(c => selected.Contains(c.AppId))
                .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase))
            {
                var component = new SelectableComponent
                {
                    AppId = c.AppId,
                    DisplayName = c.Name,
                    Qualifier = c.Qualifier,
                    IsSelected = selected.Contains(c.AppId),
                };

                component.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(SelectableComponent.IsSelected))
                        PersistWatchedComponents();
                };

                AvailableComponents.Add(component);
            }

            ComponentPickerStatus = $"{found.Count} resource{(found.Count != 1 ? "s" : "")} visible to you";
            ComponentFilter = "";
            ApplyComponentFilter();
        }
        catch (OperationCanceledException)
        {
            ComponentPickerStatus = "Sign-in cancelled or timed out";
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

    private void PersistWatchedComponents()
    {
        _settings.WatchedAppInsightsAppIds = AvailableComponents
            .Where(c => c.IsSelected).Select(c => c.AppId).ToList();
        _settings.Save();
        ReportAppInsightsState();
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
            var (credential, account) = await signIn.GetCredentialAsync(allowPrompt: true);
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
        _poller.NewSentryIssueDetected += OnNewSentryIssueDetected;
        _poller.SentryRegressionDetected += OnSentryRegressionDetected;
        _poller.SentryEscalationDetected += OnSentryEscalationDetected;
        _poller.SentryFailed += OnSentryFailed;
        _poller.SentryPolled += OnSentryPolled;
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

    private void OnNewSentryIssueDetected(SentryIssueItem issue)
        => Dispatcher.UIThread.Post(() => _notifications.ShowSentryIssue(
            issue.Title, issue.ProjectSlug, Detail(issue), issue.Permalink, isRegression: false));

    private void OnSentryRegressionDetected(SentryIssueItem issue)
        => Dispatcher.UIThread.Post(() => _notifications.ShowSentryIssue(
            issue.Title, issue.ProjectSlug, Detail(issue), issue.Permalink, isRegression: true));

    /// <summary>Reach plus the version it happened in, which is where triage starts.</summary>
    private static string Detail(SentryIssueItem issue) =>
        issue.HasRelease ? $"{issue.Impact} · {issue.ReleaseSummary}" : issue.Impact;

    private void OnSentryEscalationDetected(SentryIssueItem issue)
        => Dispatcher.UIThread.Post(() => _notifications.ShowSentryIssue(
            issue.Title, issue.ProjectSlug, $"grown to {Detail(issue)}", issue.Permalink, isRegression: true));

    private void OnSentryFailed(string msg)
        => Dispatcher.UIThread.Post(() => SentryStatus.Set(
            msg.Contains("authentication", StringComparison.OrdinalIgnoreCase)
                ? ConnectionState.AuthFailed
                : ConnectionState.Error,
            msg));

    private void OnSentryPolled(List<SentryIssueItem> issues)
        => Dispatcher.UIThread.Post(() =>
        {
            _lastSentryIssues = issues;
            RebuildSentryList();

            var muted = issues.Count(i => _settings.IsSentryIssueMuted(i.Id));
            var active = issues.Count - muted;
            SentryStatus.Set(
                ConnectionState.Connected,
                active == 0 && muted == 0 ? "Connected, nothing unresolved"
                : muted == 0 ? $"Connected, {active} unresolved"
                : $"Connected, {active} unresolved, {muted} muted");
        });

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
        _settingsOnOpen = ConnectionFingerprint();
        ExpandConnectionsNeedingAttention();
        _ = LoadPipelinesAsync();
        _ = LoadSentryProjectsAsync();
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
        _poller.NewSentryIssueDetected -= OnNewSentryIssueDetected;
        _poller.SentryRegressionDetected -= OnSentryRegressionDetected;
        _poller.SentryEscalationDetected -= OnSentryEscalationDetected;
        _poller.SentryFailed -= OnSentryFailed;
        _poller.SentryPolled -= OnSentryPolled;
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
