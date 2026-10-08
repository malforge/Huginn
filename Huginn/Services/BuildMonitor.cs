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

        // What came after each failure is asked for per pipeline and branch, from the earliest
        // failure there, since another branch's runs say nothing about this one.
        var failingBranches = myFailed.Concat(watchedFailures)
            .Where(b => b.DefinitionId > 0)
            .GroupBy(b => (b.DefinitionId, b.SourceBranch))
            .ToList();

        if (failingBranches.Count > 0)
        {
            var fetched = await Task.WhenAll(failingBranches.Select(g =>
                client.GetBuildRunsOnBranchSinceAsync(
                    g.Key.DefinitionId, g.Key.SourceBranch, g.Min(b => b.QueueTime), ct)));
            var runs = fetched.SelectMany(r => r).ToList();

            myFailed = BuildSupersession.Apply(myFailed, runs);
            watchedFailures = BuildSupersession.Apply(watchedFailures, runs);
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

    public void Reset()
    {
        _knownFailedBuildIds.Clear();
        _hasBaseline = false;
    }

    public StatusParts GetStatusParts(BuildSnapshot snap)
    {
        var parts = new StatusParts();
        var total = BuildSupersession.OnePerBranch(
            snap.MyFailedBuilds.Concat(snap.WatchedPipelineFailures)).Count;
        if (total > 0)
            parts.Add($"{total} build failure{(total != 1 ? "s" : "")}");
        return parts;
    }
}
