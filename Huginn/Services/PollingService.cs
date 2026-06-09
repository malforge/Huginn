using System;
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
    private readonly SemaphoreSlim _pollLock = new(1, 1);

    public event Action<PollResult>? PollCompleted;
    public event Action<PullRequestItem>? NewPullRequestDetected;
    public event Action<BuildItem>? NewBuildFailureDetected;
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
        _prMonitor.Reset();
        _buildMonitor.Reset();
    }

    public async Task PollNowAsync(CancellationToken ct = default)
    {
        if (_client == null || string.IsNullOrEmpty(_myUserId)) return;
        if (!await _pollLock.WaitAsync(0, ct)) return;

        try
        {
            StatusChanged?.Invoke("Refreshing…");

            var prSnap = await _prMonitor.PollAsync(_client, _myUserId, ct);
            var buildSnap = await _buildMonitor.PollAsync(_client, _myUserId, _settings, ct);

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
            // Merge build parts into the PR status
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
