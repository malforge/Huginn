using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
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
    [ObservableProperty] private string _testConnectionResult = "";
    [ObservableProperty] private bool _isTesting;
    [ObservableProperty] private bool _monitorMyBuilds;
    [ObservableProperty] private bool _autoStartEnabled;

    // Pipeline picker
    public ObservableCollection<SelectablePipeline> AvailablePipelines { get; } = [];
    public ObservableCollection<SelectablePipeline> FilteredPipelines { get; } = [];
    [ObservableProperty] private bool _isLoadingPipelines;
    [ObservableProperty] private string _pipelinePickerStatus = "";
    [ObservableProperty] private string _pipelinePickerTooltip = "";
    [ObservableProperty] private string _pipelineFilter = "";

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
        MonitorMyBuilds = _settings.MonitorMyBuilds;
        AutoStartEnabled = _autoStart.IsEnabled;

        UpdateService.Instance.PropertyChanged += OnUpdateServicePropertyChanged;
        UpdateService.Instance.StartPolling();

        if (_settings.IsConfigured)
            _ = ConnectAsync();
        else
            IsSettingsVisible = true;
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
        if (IsSettingsVisible)
            _ = LoadPipelinesAsync();
    }

    partial void OnPipelineFilterChanged(string value) => ApplyPipelineFilter();

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
    private async Task TestConnectionAsync()
    {
        if (string.IsNullOrWhiteSpace(Organization) || string.IsNullOrWhiteSpace(Pat))
        {
            TestConnectionResult = "⚠ Organization and PAT are required.";
            return;
        }

        IsTesting = true;
        TestConnectionResult = "Testing…";

        try
        {
            using var client = new AdoApiClient(Organization.Trim(), Project.Trim(), Pat.Trim());
            var userId = await client.GetMyProfileIdAsync();
            TestConnectionResult = string.IsNullOrEmpty(userId)
                ? "❌ Auth failed — check your PAT."
                : $"✅ Connected (user ID: {userId[..Math.Min(8, userId.Length)]}…)";
        }
        catch (Exception ex)
        {
            TestConnectionResult = $"❌ {ex.Message}";
        }
        finally
        {
            IsTesting = false;
        }
    }

    [RelayCommand]
    private async Task SaveAndConnectAsync()
    {
        _settings.Organization = Organization.Trim();
        _settings.Project = Project.Trim();
        _settings.MonitorMyBuilds = MonitorMyBuilds;
        _settings.WatchedPipelineIds = AvailablePipelines
            .Where(p => p.IsSelected).Select(p => p.Id).ToList();
        _settings.Save();

        if (!string.IsNullOrWhiteSpace(Pat))
            _settings.SetPat(Pat.Trim());

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
        if (string.IsNullOrWhiteSpace(org)) return;
        try
        {
            var url = $"https://dev.azure.com/{Uri.EscapeDataString(org)}/_usersSettings/tokens";
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch { }
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

        var ok = await _poller.StartAsync();
        IsConnected = ok;
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
            ConnectionError = msg;
            IsConnected = false;
        });

    [RelayCommand]
    private void OpenSettings()
    {
        IsSettingsVisible = true;
        _ = LoadPipelinesAsync();
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
