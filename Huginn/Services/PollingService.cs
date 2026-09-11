using System;
using System.Collections.Generic;
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
    private volatile bool _authFailed;
    private readonly AppSettings _settings;
    private readonly PrMonitor _prMonitor = new();
    private readonly BuildMonitor _buildMonitor = new();
    private readonly SentryMonitor _sentryMonitor = new();
    private SentryApiClient? _sentry;
    private readonly SemaphoreSlim _pollLock = new(1, 1);
    private readonly SemaphoreSlim _sentryLock = new(1, 1);

    public event Action<PollResult>? PollCompleted;
    public event Action<PullRequestItem>? NewPullRequestDetected;
    public event Action<BuildItem>? NewBuildFailureDetected;
    public event Action<SentryIssueItem>? NewSentryIssueDetected;
    public event Action<SentryIssueItem>? SentryRegressionDetected;
    public event Action<SentryIssueItem>? SentryEscalationDetected;
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

    /// <summary>Refreshes every source at once, for the Refresh button.</summary>
    public Task PollNowAsync(CancellationToken ct = default) =>
        Task.WhenAll(PollAzureDevOpsOnceAsync(ct), PollSentryOnceAsync(ct));

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

            // Raise new-item events
            foreach (var pr in prSnap.NewReviewPrs)
                NewPullRequestDetected?.Invoke(pr);
            foreach (var build in buildSnap.NewFailures)
                NewBuildFailureDetected?.Invoke(build);

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

            foreach (var issue in snapshot.NewIssues)
                NewSentryIssueDetected?.Invoke(issue);
            foreach (var issue in snapshot.Regressions)
                SentryRegressionDetected?.Invoke(issue);
            foreach (var issue in snapshot.Escalations)
                SentryEscalationDetected?.Invoke(issue);

            await _sentryMonitor.EnrichReleasesAsync(
                _sentry, _settings.SentryOrganization, snapshot.Issues, ct);

            SentryPolled?.Invoke(snapshot.Issues);
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
    }
}
