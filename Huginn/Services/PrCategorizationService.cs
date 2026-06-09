using System;
using System.Collections.Generic;
using System.Linq;
using Huginn.Models;

namespace Huginn.Services;

/// <summary>
/// Categorized view of pull requests after filtering and priority classification.
/// </summary>
public sealed class CategorizedPrs
{
    public required List<PullRequestItem> FailedValidation { get; init; }
    public required List<PullRequestItem> Unstaffed { get; init; }
    public required List<PullRequestItem> AutoCompleteOff { get; init; }
    public required List<PullRequestItem> ReadyToReview { get; init; }
    public required List<PullRequestItem> MyActive { get; init; }
    public required int ApprovedByMeCount { get; init; }

    /// <summary>
    /// Badge count: actionable items only — excludes acknowledged items, approved-by-me PRs, and own PRs.
    /// </summary>
    public int BadgeCount =>
        FailedValidation.Count(p => !p.IsAcknowledged && !p.IsApprovedByMe)
        + Unstaffed.Count(p => !p.IsAcknowledged && !p.IsApprovedByMe)
        + AutoCompleteOff.Count(p => !p.IsAcknowledged)
        + ReadyToReview.Count(p => !p.IsAcknowledged && !p.IsApprovedByMe);
}

/// <summary>
/// Pure function that categorizes poll results into priority-ordered display groups.
/// </summary>
public static class PrCategorizationService
{
    public static CategorizedPrs Categorize(PollResult result, bool showApproved)
    {
        var approvedByMe = new HashSet<int>();
        foreach (var pr in result.ReviewPrs)
        {
            var myVote = pr.Reviewers.FirstOrDefault(r =>
                r.Id.Equals(result.UserId, StringComparison.OrdinalIgnoreCase))?.Vote ?? 0;
            pr.IsApprovedByMe = myVote is 10 or 5;
            if (pr.IsApprovedByMe)
                approvedByMe.Add(pr.PullRequestId);
        }

        var activeReviewPrs = result.ReviewPrs
            .Where(pr => !approvedByMe.Contains(pr.PullRequestId))
            .ToList();

        var classified = new HashSet<int>();
        var failedValidation = new List<PullRequestItem>();
        var unstaffed = new List<PullRequestItem>();
        var autoCompleteOff = new List<PullRequestItem>();
        var readyToReview = new List<PullRequestItem>();
        var myActive = new List<PullRequestItem>();

        // Drafts skip every warning/review category — the PR is explicitly not ready,
        // so "missing reviewers", "no autocomplete", and "awaiting review" don't apply.
        // My drafts fall through to MyActive below; others' drafts get dropped.

        // 1. Failed validation — only from PRs still my responsibility
        foreach (var pr in activeReviewPrs.Concat(result.UnstaffedPrs).Concat(result.StaffedMyPrs))
        {
            if (!pr.IsDraft && pr.PipelineStatus == PipelineState.Failed && classified.Add(pr.PullRequestId))
                failedValidation.Add(pr);
        }

        // 2. Unstaffed — my PRs with no reviewers (not already in failed)
        foreach (var pr in result.UnstaffedPrs)
        {
            if (!pr.IsDraft && classified.Add(pr.PullRequestId))
                unstaffed.Add(pr);
        }

        // 3. Autocomplete off — my staffed PRs that aren't set to autocomplete (risk of being forgotten)
        foreach (var pr in result.StaffedMyPrs)
        {
            if (!pr.IsDraft && !pr.IsAutoCompleteSet && classified.Add(pr.PullRequestId))
                autoCompleteOff.Add(pr);
        }

        // 4. Ready to review — remaining active review PRs
        foreach (var pr in activeReviewPrs)
        {
            if (!pr.IsDraft && classified.Add(pr.PullRequestId))
                readyToReview.Add(pr);
        }

        // 3b. If ShowApproved, add approved review PRs back into the list
        if (showApproved)
        {
            foreach (var pr in result.ReviewPrs.Where(pr => approvedByMe.Contains(pr.PullRequestId)))
            {
                if (!pr.IsDraft && classified.Add(pr.PullRequestId))
                    readyToReview.Add(pr);
            }
        }

        // 5. My active PRs — own PRs (incl. my drafts) not already shown above
        foreach (var pr in result.UnstaffedPrs.Concat(result.StaffedMyPrs))
        {
            if (classified.Add(pr.PullRequestId))
                myActive.Add(pr);
        }

        return new CategorizedPrs
        {
            FailedValidation = failedValidation,
            Unstaffed = unstaffed,
            AutoCompleteOff = autoCompleteOff,
            ReadyToReview = readyToReview,
            MyActive = myActive,
            ApprovedByMeCount = approvedByMe.Count,
        };
    }
}
