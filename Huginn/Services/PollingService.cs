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
    private Task? _pollLoop;
    private volatile bool _authFailed;
    private readonly AppSettings _settings;
    private readonly PrMonitor _prMonitor = new();
    private readonly BuildMonitor _buildMonitor = new();
    private readonly SentryMonitor _sentryMonitor = new();
    private SentryApiClient? _sentry;
    private readonly SemaphoreSlim _pollLock = new(1, 1);

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
        _pollLoop = PollLoopAsync(_cts.Token);
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

    public async Task PollNowAsync(CancellationToken ct = default)
    {
        if (_client == null || string.IsNullOrEmpty(_myUserId)) return;
        if (!await _pollLock.WaitAsync(0, ct)) return;

        try
        {
            StatusChanged?.Invoke("Refreshing…");

            // Sentry throttles the per-issue release lookups, so a poll there can take tens of
            // seconds. Run it alongside Azure DevOps rather than holding PR and build results
            // behind it. The two Azure DevOps passes stay sequential because they share one
            // client, whose project id is resolved lazily.
            var sentryTask = PollSentryAsync(ct);
            var adoTask = PollAzureDevOpsAsync(ct);

            // Azure DevOps is published the moment it lands rather than waiting on Sentry, which
            // can spend tens of seconds on throttled release lookups.
            var (prSnap, buildSnap) = await adoTask;

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

            // Let Sentry finish in its own time; it has already published its own list.
            await sentryTask;
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
    private async Task<SentryMonitor.SentrySnapshot?> PollSentryAsync(CancellationToken ct)
    {
        if (_sentry is null) return null;

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
    }

    private async Task PollLoopAsync(CancellationToken ct)
    {
        await PollNowAsync(ct);

        while (!ct.IsCancellationRequested && !_authFailed)
        {
            try
            {
                await Task.Delay(TimeSpan.FromMinutes(_settings.PollIntervalMinutes), ct);
                await PollNowAsync(ct);
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
    }
}
