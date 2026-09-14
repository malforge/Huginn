using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Huginn.Models;

namespace Huginn.Services;

/// <summary>
/// Orchestrates periodic polling of PRs and builds via dedicated monitors.
/// Owns the timer, API client lifecycle, and authentication.
/// </summary>
public sealed class PollingService : IDisposable
{
    private AdoApiClient? _client;
    private string _myUserId = "";
    private CancellationTokenSource? _cts;
    private Task? _adoLoop;
    private Task? _sentryLoop;
    private Task? _appInsightsLoop;
    private volatile bool _authFailed;
    private readonly AppSettings _settings;
    private readonly PrMonitor _prMonitor = new();
    private readonly BuildMonitor _buildMonitor = new();
    private readonly SentryMonitor _sentryMonitor = new();
    private SentryApiClient? _sentry;
    private readonly AppInsightsMonitor _appInsightsMonitor = new();
    private readonly SemaphoreSlim _pollLock = new(1, 1);
    private readonly SemaphoreSlim _sentryLock = new(1, 1);
    private readonly SemaphoreSlim _appInsightsLock = new(1, 1);

    // When each source last finished, so a refresh that was not asked for explicitly can leave
    // the expensive ones alone.
    private DateTimeOffset _sentryLastPolled = DateTimeOffset.MinValue;
    private DateTimeOffset _appInsightsLastPolled = DateTimeOffset.MinValue;

    public event Action<PollResult>? PollCompleted;
    // Batches rather than single items, so one poll produces one announcement per source
    // however much it found.
    public event Action<List<PullRequestItem>>? NewPullRequestsDetected;
    public event Action<List<BuildItem>>? NewBuildFailuresDetected;
    public event Action<SentryAlerts>? SentryAlertsDetected;
    public event Action<string>? StatusChanged;
    public event Action<string>? ErrorOccurred;

    public PollingService(AppSettings settings)
    {
        _settings = settings;
    }

    public async Task<bool> StartAsync()
    {
        Stop();
        _authFailed = false;

        var pat = _settings.GetPat();
        if (string.IsNullOrEmpty(pat))
        {
            ErrorOccurred?.Invoke("No PAT configured.");
            return false;
        }

        _client = new AdoApiClient(_settings.Organization, _settings.Project, pat);
        _client.AuthFailed += reason =>
        {
            _authFailed = true;
            ErrorOccurred?.Invoke($"Authentication failed: {reason}. Check your PAT.");
        };

        try
        {
            StatusChanged?.Invoke("Authenticating…");
            _myUserId = await _client.GetMyProfileIdAsync();
            if (string.IsNullOrEmpty(_myUserId))
            {
                ErrorOccurred?.Invoke("Failed to authenticate. Check your PAT and organization.");
                return false;
            }

            StatusChanged?.Invoke("Connected");
        }
        catch (Exception ex)
        {
            ErrorOccurred?.Invoke($"Connection failed: {ex.Message}");
            return false;
        }

        ConnectSentry();

        _buildMonitor.SetInitialLookback(TimeSpan.FromHours(2));
        _cts = new CancellationTokenSource();

        // Each source keeps its own cadence: they are independent, and forcing the slow one to
        // the fast one only re-reads the same data.
        _adoLoop = LoopAsync(PollAzureDevOpsOnceAsync, () => _settings.PollIntervalMinutes, _cts.Token);
        _sentryLoop = LoopAsync(PollSentryOnceAsync, () => _settings.SentryPollIntervalMinutes, _cts.Token);
        _appInsightsLoop = LoopAsync(
            PollAppInsightsOnceAsync, () => _settings.AppInsightsPollIntervalMinutes, _cts.Token);
        return true;
    }

    public void Stop()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
        _client?.Dispose();
        _client = null;
        _sentry?.Dispose();
        _sentry = null;
        _prMonitor.Reset();
        _buildMonitor.Reset();
        _sentryMonitor.Reset();
        _appInsightsMonitor.Reset();
    }

    /// <summary>
    /// Builds the Sentry client when that connection is configured. It is deliberately separate
    /// from the Azure DevOps one, so a missing or broken Sentry token cannot stop PR and build
    /// monitoring, and vice versa.
    /// </summary>
    private void ConnectSentry()
    {
        _sentry?.Dispose();
        _sentry = null;

        if (!_settings.IsSentryConfigured) return;

        _sentry = new SentryApiClient(_settings.GetSentryApiBaseUrl(), _settings.GetSentryToken()!);
        _sentry.AuthFailed += reason =>
            SentryFailed?.Invoke($"Sentry authentication failed: {reason}. Check the token.");
    }

    public event Action<string>? SentryFailed;
    public event Action<List<SentryIssueItem>>? SentryPolled;
    public event Action<List<ServiceFinding>>? NewServiceFindingsDetected;
    public event Action<string>? AppInsightsFailed;
    public event Action<List<ServiceFinding>>? AppInsightsPolled;

    /// <summary>What the rules held back on the last poll, for the settings view to show.</summary>
    public event Action<List<IgnoredSubject>>? AppInsightsSuppressed;

    /// <summary>Refreshes every source, for the Refresh button.</summary>
    public Task PollNowAsync(CancellationToken ct = default) => PollNowAsync(force: true, ct);

    /// <summary>
    /// Refreshes what is worth refreshing. Azure DevOps always, because it is cheap and pull
    /// requests genuinely move minute to minute. Sentry and Application Insights only when their
    /// own interval has elapsed: their windows are measured in hours, so re-querying them because
    /// a window regained focus costs a lot and tells you nothing new.
    /// </summary>
    public Task PollNowAsync(bool force, CancellationToken ct = default)
    {
        List<Task> polls = [PollAzureDevOpsOnceAsync(ct)];

        if (force || Due(_sentryLastPolled, _settings.SentryPollIntervalMinutes))
            polls.Add(PollSentryOnceAsync(ct));

        if (force || Due(_appInsightsLastPolled, _settings.AppInsightsPollIntervalMinutes))
            polls.Add(PollAppInsightsOnceAsync(ct));

        return Task.WhenAll(polls);
    }

    private static bool Due(DateTimeOffset last, int intervalMinutes) =>
        DateTimeOffset.UtcNow - last >= TimeSpan.FromMinutes(Math.Max(1, intervalMinutes));

    /// <summary>
    /// Examines the watched Application Insights resources. Like Sentry, it reports its own
    /// failures rather than throwing, so it cannot take another source down with it.
    /// </summary>
    private async Task PollAppInsightsOnceAsync(CancellationToken ct = default)
    {
        if (_settings.WatchedAppInsights.Count == 0) return;
        if (!await _appInsightsLock.WaitAsync(0, ct)) return;

        try
        {
            var signIn = new AzureSignIn(
                "appinsights", _settings.AppInsightsTenantId, _settings.AppInsightsClientId);

            if (!signIn.HasStoredAccount)
            {
                AppInsightsFailed?.Invoke("Not signed in");
                return;
            }

            // Background polling never prompts: a browser window appearing unbidden every
            // quarter of an hour would be worse than going quiet and saying so.
            var (credential, _) = await signIn.GetCredentialAsync(allowPrompt: false, ct);
            using var client = new AppInsightsApiClient(credential);

            // Entries saved before the ARM id was kept hold only a name, which leaves the
            // portal links dead. Fill them in once rather than making the user re-pick.
            if (_settings.WatchedAppInsights.Values.Any(v => AppSettings.ResourceIdOf(v).Length == 0))
                await UpgradeWatchedResourcesAsync(client, ct);

            var components = _settings.WatchedAppInsights
                .Select(w => new AppInsightsComponent
                {
                    AppId = w.Key,
                    Name = AppSettings.NameOf(w.Value),
                    ResourceId = AppSettings.ResourceIdOf(w.Value),
                })
                .ToList();

            var snapshot = await _appInsightsMonitor.PollAsync(client, components, _settings, ct);

            AppInsightsPolled?.Invoke(snapshot.Findings);
            AppInsightsSuppressed?.Invoke(snapshot.Ignored);
            _appInsightsLastPolled = DateTimeOffset.UtcNow;

            if (snapshot.NewFindings.Count > 0)
                NewServiceFindingsDetected?.Invoke(snapshot.NewFindings);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            AppInsightsFailed?.Invoke(ex.Message);
        }
        finally
        {
            _appInsightsLock.Release();
        }
    }

    private async Task PollAzureDevOpsOnceAsync(CancellationToken ct = default)
    {
        if (_client == null || string.IsNullOrEmpty(_myUserId)) return;
        if (!await _pollLock.WaitAsync(0, ct)) return;

        try
        {
            StatusChanged?.Invoke("Refreshing…");

            var (prSnap, buildSnap) = await PollAzureDevOpsAsync(ct);

            // Auth failure during this poll: snapshots are empty stubs from 401 responses.
            // Skip PollCompleted (would wipe the last-known UI state) and the "all clear"
            // status line (would overwrite the error banner). The loop terminates after.
            if (_authFailed) return;

            // Raise new-item events, one per source rather than one per item
            if (prSnap.NewReviewPrs.Count > 0)
                NewPullRequestsDetected?.Invoke(prSnap.NewReviewPrs);
            if (buildSnap.NewFailures.Count > 0)
                NewBuildFailuresDetected?.Invoke(buildSnap.NewFailures);

            PollCompleted?.Invoke(new PollResult
            {
                ReviewPrs = prSnap.ReviewPrs,
                UnstaffedPrs = prSnap.UnstaffedPrs,
                StaffedMyPrs = prSnap.StaffedMyPrs,
                UserId = _myUserId,
                MyFailedBuilds = buildSnap.MyFailedBuilds,
                WatchedPipelineFailures = buildSnap.WatchedPipelineFailures,
            });

            // Combined status summary
            var status = _prMonitor.GetStatusParts(prSnap);
            var buildStatus = _buildMonitor.GetStatusParts(buildSnap);
            var combined = status.ToString();
            var buildPart = buildStatus.ToString();
            if (buildPart != "all clear")
                combined = combined == "all clear" ? buildPart : $"{combined}, {buildPart}";
            StatusChanged?.Invoke($"Last updated: {DateTime.Now:HH:mm}  ·  {combined}");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            ErrorOccurred?.Invoke($"Poll error: {ex.Message}");
        }
        finally
        {
            _pollLock.Release();
        }
    }

    private async Task<(PrMonitor.PrSnapshot Prs, BuildMonitor.BuildSnapshot Builds)>
        PollAzureDevOpsAsync(CancellationToken ct)
    {
        var prs = await _prMonitor.PollAsync(_client!, _myUserId, ct);
        var builds = await _buildMonitor.PollAsync(_client!, _myUserId, _settings, ct);
        return (prs, builds);
    }

    /// <summary>
    /// Polls Sentry in isolation. A failure here reports itself and returns null rather than
    /// throwing, so it cannot take the Azure DevOps half of the poll down with it.
    /// </summary>
    private async Task<SentryMonitor.SentrySnapshot?> PollSentryOnceAsync(CancellationToken ct = default)
    {
        if (_sentry is null) return null;
        if (!await _sentryLock.WaitAsync(0, ct)) return null;

        try
        {
            var snapshot = await _sentryMonitor.PollAsync(_sentry, _settings, ct);

            // Show the issues straight away. Versions arrive in a second pass because each one
            // costs a throttled request, and an issue without its version is still worth seeing.
            SentryPolled?.Invoke(snapshot.Issues);

            SentryAlerts alerts = new(snapshot.NewIssues, snapshot.Regressions, snapshot.Escalations);
            if (alerts.Total > 0) SentryAlertsDetected?.Invoke(alerts);

            await _sentryMonitor.EnrichReleasesAsync(
                _sentry, _settings.SentryOrganization, snapshot.Issues, ct);

            SentryPolled?.Invoke(snapshot.Issues);
            _sentryLastPolled = DateTimeOffset.UtcNow;
            return snapshot;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            SentryFailed?.Invoke($"Sentry poll failed: {ex.Message}");
            return null;
        }
        finally
        {
            _sentryLock.Release();
        }
    }

    /// <summary>
    /// Replaces stored resource names with their full ARM ids, so portal links work without the
    /// user having to reselect anything. Failure here is not worth reporting: the watch still
    /// works, only the link is missing.
    /// </summary>
    private async Task UpgradeWatchedResourcesAsync(AppInsightsApiClient client, CancellationToken ct)
    {
        try
        {
            var known = await client.GetComponentsAsync(ct);
            var byAppId = known.ToDictionary(c => c.AppId, c => c.ResourceId);
            var changed = false;

            foreach (var appId in _settings.WatchedAppInsights.Keys.ToList())
            {
                if (AppSettings.ResourceIdOf(_settings.WatchedAppInsights[appId]).Length > 0) continue;
                if (!byAppId.TryGetValue(appId, out var resourceId) || resourceId.Length == 0) continue;

                _settings.WatchedAppInsights[appId] = resourceId;
                changed = true;
            }

            if (changed)
            {
                _settings.Save();
                Log.Info("Filled in Azure resource ids for the watched Application Insights resources.");
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Error($"Could not resolve resource ids: {ex.Message}");
        }
    }

    /// <summary>Runs one source on its own schedule until the service stops.</summary>
    private async Task LoopAsync(
        Func<CancellationToken, Task> poll, Func<int> intervalMinutes, CancellationToken ct)
    {
        await poll(ct);

        while (!ct.IsCancellationRequested && !_authFailed)
        {
            try
            {
                await Task.Delay(TimeSpan.FromMinutes(Math.Max(1, intervalMinutes())), ct);
                await poll(ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                ErrorOccurred?.Invoke($"Poll error: {ex.Message}");
            }
        }
    }

    public void Dispose()
    {
        Stop();
        _pollLock.Dispose();
        _sentryLock.Dispose();
        _appInsightsLock.Dispose();
    }
}
