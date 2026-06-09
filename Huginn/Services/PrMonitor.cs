using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Huginn.Models;

namespace Huginn.Services;

/// <summary>
/// Monitors pull requests: fetches review/created PRs, enriches with pipeline status,
/// and detects newly-assigned reviews via baseline tracking.
/// </summary>
public sealed class PrMonitor
{
    private readonly HashSet<int> _knownPrIds = [];
    private bool _hasBaseline;

    public record PrSnapshot(
        List<PullRequestItem> ReviewPrs,
        List<PullRequestItem> UnstaffedPrs,
        List<PullRequestItem> StaffedMyPrs,
        List<PullRequestItem> NewReviewPrs);

    public async Task<PrSnapshot> PollAsync(AdoApiClient client, string userId, CancellationToken ct)
    {
        var reviewPrs = await client.GetPullRequestsByReviewerAsync(userId, ct);
        var myPrs = await client.GetPullRequestsByCreatorAsync(userId, ct);

        var unstaffed = myPrs.Where(p => p.Reviewers.Count == 0).ToList();
        var staffedMyPrs = myPrs.Where(p => p.Reviewers.Count > 0).ToList();

        var allPrs = reviewPrs.Concat(myPrs).DistinctBy(p => p.PullRequestId).ToList();
        if (allPrs.Count > 0)
            await client.EnrichWithPipelineStatusAsync(allPrs, ct);

        var newReviewPrs = new List<PullRequestItem>();
        if (_hasBaseline)
        {
            foreach (var pr in reviewPrs)
            {
                if (_knownPrIds.Add(pr.PullRequestId))
                    newReviewPrs.Add(pr);
            }
        }
        else
        {
            foreach (var pr in reviewPrs)
                _knownPrIds.Add(pr.PullRequestId);
            _hasBaseline = true;
        }

        _knownPrIds.IntersectWith(reviewPrs.Select(p => p.PullRequestId));

        return new PrSnapshot(reviewPrs, unstaffed, staffedMyPrs, newReviewPrs);
    }

    public void Reset()
    {
        _knownPrIds.Clear();
        _hasBaseline = false;
    }

    public StatusParts GetStatusParts(PrSnapshot snap)
    {
        var allPrs = snap.ReviewPrs.Concat(snap.StaffedMyPrs).Concat(snap.UnstaffedPrs)
            .DistinctBy(p => p.PullRequestId).ToList();
        var parts = new StatusParts();

        var failed = allPrs.Count(p => p.PipelineStatus == PipelineState.Failed);
        var running = allPrs.Count(p => p.PipelineStatus == PipelineState.Running);
        if (failed > 0) parts.Add($"{failed} failing");
        if (snap.UnstaffedPrs.Count > 0) parts.Add($"{snap.UnstaffedPrs.Count} unstaffed");
        if (running > 0) parts.Add($"{running} building");
        if (snap.ReviewPrs.Count > 0) parts.Add($"{snap.ReviewPrs.Count} to review");

        return parts;
    }
}

/// <summary>Accumulates summary parts for the status bar.</summary>
public sealed class StatusParts
{
    private readonly List<string> _parts = [];
    public void Add(string part) => _parts.Add(part);
    public override string ToString() => _parts.Count > 0 ? string.Join(", ", _parts) : "all clear";
}
