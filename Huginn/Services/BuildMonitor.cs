using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Huginn.Models;

namespace Huginn.Services;

/// <summary>
/// Monitors pipeline builds: tracks personal build failures and watched pipeline health.
/// Uses baseline tracking to avoid spamming notifications on first poll.
/// </summary>
public sealed class BuildMonitor
{
    private readonly HashSet<int> _knownFailedBuildIds = [];
    private bool _hasBaseline;

    /// <summary>
    /// How far back one of your own build failures is asked for. A fixed window rather than the
    /// time of the last poll: asking only for what has happened since then returns each failure
    /// exactly once, so it appears for one interval and then disappears on its own.
    /// </summary>
    private static readonly TimeSpan MyFailureWindow = TimeSpan.FromHours(24);

    public record BuildSnapshot(
        List<BuildItem> MyFailedBuilds,
        List<BuildItem> WatchedPipelineFailures,
        List<BuildItem> NewFailures);

    public async Task<BuildSnapshot> PollAsync(
        AdoApiClient client, string userId, AppSettings settings, CancellationToken ct)
    {
        var myFailed = new List<BuildItem>();
        var watchedFailures = new List<BuildItem>();

        if (settings.MonitorMyBuilds)
        {
            myFailed = await client.GetMyRecentFailedBuildsAsync(
                userId, DateTime.UtcNow - MyFailureWindow, ct);
        }

        if (settings.WatchedPipelineIds.Count > 0)
        {
            var latest = await client.GetLatestBuildsForDefinitionsAsync(settings.WatchedPipelineIds, ct);
            watchedFailures = latest.Where(b => b.Result == BuildResult.Failed).ToList();
        }

        // Collect all failed definition IDs so we can check for superseding builds
        var allFailed = myFailed.Concat(watchedFailures).ToList();
        var failedDefIds = allFailed.Select(b => b.DefinitionId).Where(id => id > 0).Distinct().ToList();

        if (failedDefIds.Count > 0)
        {
            var latestStatuses = await client.GetLatestBuildStatusPerDefinitionAsync(failedDefIds, ct);
            var statusByDef = latestStatuses.ToDictionary(x => x.DefinitionId, x => x);

            // Filter out failures where a newer build has already succeeded
            myFailed = FilterAndAnnotate(myFailed, statusByDef);
            watchedFailures = FilterAndAnnotate(watchedFailures, statusByDef);
        }

        var newFailures = new List<BuildItem>();
        if (_hasBaseline)
        {
            foreach (var build in myFailed.Concat(watchedFailures))
            {
                if (_knownFailedBuildIds.Add(build.Id))
                    newFailures.Add(build);
            }
        }
        else
        {
            foreach (var build in myFailed.Concat(watchedFailures))
                _knownFailedBuildIds.Add(build.Id);
            _hasBaseline = true;
        }

        return new BuildSnapshot(myFailed, watchedFailures, newFailures);
    }

    /// <summary>
    /// Removes failures that have been superseded by a newer successful build,
    /// and marks failures where a newer build is in progress.
    /// </summary>
    private static List<BuildItem> FilterAndAnnotate(
        List<BuildItem> failures,
        Dictionary<int, (int DefinitionId, int BuildId, string Status, BuildResult Result, string WebUrl)> latestByDef)
    {
        var result = new List<BuildItem>();
        foreach (var build in failures)
        {
            if (!latestByDef.TryGetValue(build.DefinitionId, out var latest))
            {
                result.Add(build);
                continue;
            }

            // A newer build (higher ID) has completed successfully — drop this failure
            if (latest.BuildId > build.Id && latest.Result == BuildResult.Succeeded)
                continue;

            // A newer build is queued or running — mark as retry in progress
            if (latest.BuildId > build.Id &&
                latest.Status is "inProgress" or "notStarted" or "postponed")
            {
                build.RetryInProgress = true;
                build.RetryBuildUrl = latest.WebUrl;
            }

            result.Add(build);
        }
        return result;
    }

    public void Reset()
    {
        _knownFailedBuildIds.Clear();
        _hasBaseline = false;
    }

    public StatusParts GetStatusParts(BuildSnapshot snap)
    {
        var parts = new StatusParts();
        var total = snap.MyFailedBuilds.Count + snap.WatchedPipelineFailures.Count;
        if (total > 0)
            parts.Add($"{total} build failure{(total != 1 ? "s" : "")}");
        return parts;
    }
}
