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
    /// <summary>
    /// Crashes and service findings are unbounded, so only the raised ones are listed. The rest
    /// is one summary line per project or resource, which is a fixed height whatever the volume.
    /// </summary>
    public ObservableCollection<SentryIssueItem> CrashActions { get; } = [];
    public ObservableCollection<SourceSummary> CrashSummaries { get; } = [];

    public ObservableCollection<ServiceFinding> ServiceActions { get; } = [];
    public ObservableCollection<SourceSummary> ServiceSummaries { get; } = [];

    [ObservableProperty] private bool _showMutedCrashes;
    [ObservableProperty] private bool _showMutedServices;
    [ObservableProperty] private int _mutedCrashCount;
    [ObservableProperty] private int _mutedServiceCount;
    [ObservableProperty] private bool _hasMutedCrashes;
    [ObservableProperty] private bool _hasMutedServices;

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

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasReviewSection))]
    private bool _hasReadyToReview;

    /// <summary>
    /// The review heading earns its space when there is something to review, or something hidden
    /// behind the approved toggle. Otherwise it is a title over nothing.
    /// </summary>
    public bool HasReviewSection => HasReadyToReview || HasHiddenApproved;
    [ObservableProperty] private bool _hasFailedBuilds;
    [ObservableProperty] private bool _hasRetryingBuilds;
    [ObservableProperty] private bool _hasAcknowledged;
    [ObservableProperty] private bool _isSettingsVisible;

    /// <summary>
    /// How many pane columns the window is wide enough for. Driven by the window rather than
    /// fixed, so the same layout works maximised and at the minimum width.
    /// </summary>
    [ObservableProperty] private int _layoutColumns = 1;

    // A pane with nothing in it is ambiguous: quiet, never run, or quietly broken. Each one
    // therefore says when it last managed to look.
    [ObservableProperty] private string _adoPaneStatus = "not checked yet";
    [ObservableProperty] private string _sentryPaneStatus = "not checked yet";
    [ObservableProperty] private string _servicePaneStatus = "not checked yet";

    private DateTimeOffset? _adoLastPolled;
    private DateTimeOffset? _sentryLastPolled;
    private DateTimeOffset? _serviceLastPolled;
    [ObservableProperty] private bool _isConnected;
    [ObservableProperty] private bool _isRefreshing;
    [ObservableProperty] private bool _showApproved;
    [ObservableProperty] private bool _updateAvailable;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasHiddenApproved))]
    [NotifyPropertyChangedFor(nameof(HasReviewSection))]
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
    /// <summary>How far back the service rules look, as a handful of familiar spans.</summary>
    public IReadOnlyList<WindowChoice> WindowChoices { get; } =
    [
        new("Last hour", 60),
        new("Last 3 hours", 180),
        new("Last 6 hours", 360),
        new("Last 12 hours", 720),
        new("Last day", 1440),
        new("Last 3 days", 4320),
        new("Last week", 10080),
    ];

    [ObservableProperty] private WindowChoice? _selectedWindow;
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

    /// <summary>Application Insights publishes on its own cadence, so it keeps its own result.</summary>
    private List<ServiceFinding> _lastFindings = [];

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
        SelectedWindow = WindowChoices.FirstOrDefault(w => w.Minutes == _settings.AppInsightsWindowMinutes)
                         ?? WindowChoices[0];
        SentryToken = _settings.GetSentryToken() ?? "";
        MonitorMyBuilds = _settings.MonitorMyBuilds;
        AutoStartEnabled = _autoStart.IsEnabled;

        UpdateService.Instance.PropertyChanged += OnUpdateServicePropertyChanged;
        UpdateService.Instance.StartPolling();

        Connections.Add(AdoStatus);
        Connections.Add(SentryStatus);
        Connections.Add(AppInsightsStatus);
        foreach (var connection in Connections)
        {
            connection.PropertyChanged += (_, _) =>
            {
                RefreshConnectionBanner();
                RefreshPaneStatuses();
            };
        }

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

        // Only when the picker actually holds the list. Deriving from an empty collection would
        // silently erase the selection every time the list failed to load.
        if (AvailablePipelines.Count > 0)
        {
            _settings.WatchedPipelineIds = AvailablePipelines
                .Where(p => p.IsSelected).Select(p => p.Id).ToList();
        }

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

        MutedCrashCount = _lastSentryIssues.Count(i => i.IsMuted);
        HasMutedCrashes = MutedCrashCount > 0;
        if (ShowMutedCrashes && MutedCrashCount == 0) ShowMutedCrashes = false;

        Sync(CrashActions, _lastSentryIssues
            .Where(i => ShowMutedCrashes ? i.IsMuted : i.IsFlagged && !i.IsMuted));

        Summarise(CrashSummaries, _lastSentryIssues
            .GroupBy(i => i.ProjectSlug)
            .OrderBy(g => g.Key)
            .Select(g => (g.Key, SentryProjectUrl(g.Key),
                          g.Count(), g.Count(i => i.IsFlagged), g.Count(i => i.IsMuted),
                          $"{g.Count()} unresolved")));
    }

    private string SentryProjectUrl(string slug)
    {
        var region = string.IsNullOrWhiteSpace(_settings.SentryRegionUrl)
            ? "https://sentry.io"
            : _settings.SentryRegionUrl.TrimEnd('/');
        var org = Uri.EscapeDataString(_settings.SentryOrganization);
        return $"{region}/organizations/{org}/issues/?query={Uri.EscapeDataString("is:unresolved")}"
             + $"&project={Uri.EscapeDataString(slug)}";
    }

    /// <summary>
    /// Replaces a list in place. Clearing and refilling leaves the scroll viewer holding an
    /// offset into content that no longer exists, which shows up as a half-drawn first row.
    /// </summary>
    private static void Sync<T>(ObservableCollection<T> target, IEnumerable<T> source)
    {
        var wanted = source.ToList();

        for (var i = target.Count - 1; i >= 0; i--)
        {
            if (!wanted.Contains(target[i])) target.RemoveAt(i);
        }

        for (var i = 0; i < wanted.Count; i++)
        {
            var existing = target.IndexOf(wanted[i]);
            if (existing < 0) target.Insert(i, wanted[i]);
            else if (existing != i) target.Move(existing, i);
        }
    }

    /// <summary>
    /// Updates the summary rows in place, so a row does not flicker or lose its position each
    /// time a poll lands.
    /// </summary>
    private static void Summarise(
        ObservableCollection<SourceSummary> rows,
        IEnumerable<(string Name, string Url, int Total, int Flagged, int Muted, string Breakdown)> source)
    {
        var seen = new HashSet<string>();

        foreach (var (name, url, total, flagged, muted, breakdown) in source)
        {
            seen.Add(name);
            var row = rows.FirstOrDefault(r => r.Name == name);
            if (row is null)
            {
                row = new SourceSummary { Name = name, Url = url };
                rows.Add(row);
            }

            row.Total = total;
            row.Flagged = flagged;
            row.Muted = muted;
            row.Breakdown = breakdown;
        }

        for (var i = rows.Count - 1; i >= 0; i--)
        {
            if (!seen.Contains(rows[i].Name)) rows.RemoveAt(i);
        }
    }

    /// <summary>Portal link for a watched resource, empty when its ARM id was never stored.</summary>
    private string PortalResourceUrl(string resourceName)
    {
        var entry = _settings.WatchedAppInsights.Values
            .FirstOrDefault(v => AppSettings.NameOf(v) == resourceName);

        var id = entry is null ? "" : AppSettings.ResourceIdOf(entry);
        return id.Length == 0 ? "" : $"https://portal.azure.com/#resource{id}/overview";
    }

    /// <summary>Names the kinds of trouble on a resource, loudest first.</summary>
    private static string DescribeKinds(IEnumerable<ServiceFinding> findings)
    {
        var parts = findings
            .GroupBy(f => f.Kind)
            .OrderByDescending(g => g.Count())
            .Select(g => $"{g.Count()} {Describe(g.Key)}")
            .ToList();

        return string.Join(" · ", parts);

        static string Describe(FindingKind kind) => kind switch
        {
            FindingKind.FailureRate => "failing",
            FindingKind.Latency => "slow",
            FindingKind.Dependency => "dependency",
            FindingKind.NoTraffic => "silent",
            _ => "other",
        };
    }

    [RelayCommand]
    private void OpenSummary(SourceSummary? summary)
    {
        if (summary is null || !summary.HasUrl) return;

        // Report a launch failure against whichever connection the row belongs to.
        var status = summary.Url.Contains("portal.azure.com", StringComparison.OrdinalIgnoreCase)
            ? AppInsightsStatus
            : SentryStatus;

        OpenUrl(summary.Url, status);
    }

    [RelayCommand]
    private void OpenServiceFinding(ServiceFinding? finding)
    {
        if (finding is null || !finding.HasPortalUrl) return;
        OpenUrl(finding.PortalUrl, AppInsightsStatus);
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
    /// <summary>Rebuilds the per-resource sections from the last Application Insights poll.</summary>
    /// <summary>
    /// One line per pane saying whether it is clear, stale or broken, and when it last looked.
    /// Without it an empty pane cannot be told apart from one that never ran.
    /// </summary>
    private static string PaneLine(ConnectionStatus status, DateTimeOffset? last, bool anything)
    {
        var checkedAt = last is null ? "never checked" : "checked " + last.Value.ToString("HH:mm");

        if (status.State == ConnectionState.Connecting) return "checking…";
        if (status.NeedsAttention) return $"{status.Glyph} {status.StateText}  ·  {checkedAt}";

        return anything ? checkedAt : $"all clear  ·  {checkedAt}";
    }

    private void RefreshPaneStatuses()
    {
        AdoPaneStatus = PaneLine(AdoStatus, _adoLastPolled, true);
        SentryPaneStatus = PaneLine(SentryStatus, _sentryLastPolled, CrashActions.Count > 0);
        ServicePaneStatus = PaneLine(AppInsightsStatus, _serviceLastPolled, ServiceActions.Count > 0);
    }

    private void RebuildServiceGroups()
    {
        foreach (var finding in _lastFindings)
        {
            finding.IsMuted = _settings.IsFindingMuted(finding.Id);
            finding.MutedAtMagnitude = _settings.GetFindingMutedAt(finding.Id);
        }

        MutedServiceCount = _lastFindings.Count(f => f.IsMuted);
        HasMutedServices = MutedServiceCount > 0;
        if (ShowMutedServices && MutedServiceCount == 0) ShowMutedServices = false;

        Sync(ServiceActions, _lastFindings.Where(f => f.IsMuted == ShowMutedServices));

        Summarise(ServiceSummaries, _lastFindings
            .GroupBy(f => f.ResourceName)
            .OrderBy(g => g.Key)
            .Select(g => (g.Key, PortalResourceUrl(g.Key),
                          g.Count(), 0, g.Count(f => f.IsMuted), DescribeKinds(g))));
    }

    partial void OnSelectedWindowChanged(WindowChoice? value)
    {
        if (value is null) return;
        _settings.AppInsightsWindowMinutes = value.Minutes;
        _settings.Save();
    }

    partial void OnShowMutedCrashesChanged(bool value) => RebuildSentryList();

    partial void OnShowMutedServicesChanged(bool value) => RebuildServiceGroups();

    [RelayCommand]
    private void ToggleServiceFindingMuted(ServiceFinding? finding)
    {
        if (finding is null) return;

        if (finding.IsMuted)
        {
            _settings.UnmuteFinding(finding.Id);
            finding.IsMuted = false;
        }
        else
        {
            // Muting settles the alert too, as it does for Sentry.
            _settings.MuteFinding(finding.Id, finding.Magnitude);
            _settings.DismissFinding(finding.Id);
            finding.IsMuted = true;
            finding.IsFlagged = false;
            finding.FlagReason = "";
        }

        RebuildServiceGroups();
    }

    [RelayCommand]
    private void DismissServiceFinding(ServiceFinding? finding)
    {
        if (finding is null) return;
        _settings.DismissFinding(finding.Id);
        finding.IsFlagged = false;
        finding.FlagReason = "";
        RebuildServiceGroups();
    }

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

        var watched = _settings.WatchedAppInsights.Count;
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
            foreach (var id in _settings.WatchedAppInsights.Keys)
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
                    ResourceId = c.ResourceId,
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
        _settings.WatchedAppInsights = AvailableComponents
            .Where(c => c.IsSelected)
            .ToDictionary(c => c.AppId, c => c.ResourceId.Length > 0 ? c.ResourceId : c.DisplayName);
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
        if (watched.Count == 0) watched = [.. _settings.WatchedAppInsights.Keys];
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
        if (SelectedWindow is not null) _settings.AppInsightsWindowMinutes = SelectedWindow.Minutes;
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
    private Task RefreshAsync() => RefreshAsync(force: true);

    /// <summary>
    /// Refresh on regaining focus, which must not re-query the expensive sources just because
    /// the user alt-tabbed back.
    /// </summary>
    public Task RefreshOnFocusAsync() => RefreshAsync(force: false);

    private async Task RefreshAsync(bool force)
    {
        if (_poller == null || !IsConnected) return;
        IsRefreshing = true;
        try
        {
            await _poller.PollNowAsync(force);
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
        _adoLastPolled = DateTimeOffset.Now;

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
        HasReadyToReview = ReadyToReviewPrs.Count > 0;
        HasFailedBuilds = FailedBuilds.Count > 0;
        HasRetryingBuilds = RetryingBuilds.Count > 0;
        HasAcknowledged = AcknowledgedPrs.Count > 0 || AcknowledgedBuilds.Count > 0;
        HiddenApprovedCount = cats.ApprovedByMeCount;

        UpdateBadge();
        RefreshPaneStatuses();

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
        _poller.AppInsightsPolled += OnAppInsightsPolled;
        _poller.AppInsightsFailed += OnAppInsightsFailed;
        _poller.NewServiceFindingDetected += OnNewServiceFindingDetected;
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
            _sentryLastPolled = DateTimeOffset.Now;
            RebuildSentryList();
            RefreshPaneStatuses();

            var muted = issues.Count(i => _settings.IsSentryIssueMuted(i.Id));
            var active = issues.Count - muted;
            SentryStatus.Set(
                ConnectionState.Connected,
                active == 0 && muted == 0 ? "Connected, nothing unresolved"
                : muted == 0 ? $"Connected, {active} unresolved"
                : $"Connected, {active} unresolved, {muted} muted");
        });

    private void OnAppInsightsPolled(List<ServiceFinding> findings)
        => Dispatcher.UIThread.Post(() =>
        {
            _lastFindings = findings;
            _serviceLastPolled = DateTimeOffset.Now;
            RebuildServiceGroups();
            RefreshPaneStatuses();

            var active = findings.Count(f => !f.IsMuted);
            AppInsightsStatus.Set(
                ConnectionState.Connected,
                active == 0
                    ? $"{AppInsightsAccount}, nothing wrong"
                    : $"{AppInsightsAccount}, {active} finding{(active == 1 ? "" : "s")}");
        });

    private void OnAppInsightsFailed(string msg)
        => Dispatcher.UIThread.Post(() => AppInsightsStatus.Set(
            msg.Contains("signed in", StringComparison.OrdinalIgnoreCase)
                ? ConnectionState.AuthFailed
                : ConnectionState.Error,
            msg));

    private void OnNewServiceFindingDetected(ServiceFinding finding)
        => Dispatcher.UIThread.Post(() => _notifications.ShowServiceFinding(
            finding.Subject, finding.ResourceName, finding.Detail));

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
        _poller.AppInsightsPolled -= OnAppInsightsPolled;
        _poller.AppInsightsFailed -= OnAppInsightsFailed;
        _poller.NewServiceFindingDetected -= OnNewServiceFindingDetected;
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
