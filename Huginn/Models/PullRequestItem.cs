using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Huginn.Models;

public enum PipelineState { None, Running, Succeeded, Failed }

public sealed partial class PullRequestItem : ObservableObject
{
    public int PullRequestId { get; init; }
    public string Title { get; init; } = "";
    public string Status { get; init; } = "";
    public string CreatedByName { get; init; } = "";
    public string RepositoryName { get; init; } = "";
    public string RepositoryId { get; init; } = "";
    public DateTime CreationDate { get; init; }
    public List<ReviewerInfo> Reviewers { get; init; } = [];
    public List<PipelineCheck> Checks { get; set; } = [];
    public PipelineState PipelineStatus { get; set; } = PipelineState.None;
    public bool IsAutoCompleteSet { get; init; }
    public bool IsDraft { get; init; }

    /// <summary>User has acknowledged this PR — kept in the list but excluded from the badge.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AcknowledgeTooltip))]
    private bool _isAcknowledged;

    public string AcknowledgeTooltip => IsAcknowledged
        ? "Unacknowledge — bring back into the badge"
        : "Acknowledge — hide from the badge until something changes";

    /// <summary>Computed during categorization — set when the current user has voted approved/approved-with-suggestions.</summary>
    public bool IsApprovedByMe { get; set; }

    public string PipelineIcon => PipelineStatus switch
    {
        PipelineState.Running => "⏳",
        PipelineState.Succeeded => "✅",
        PipelineState.Failed => "❌",
        _ => "",
    };

    public string PipelineLabel => PipelineStatus switch
    {
        PipelineState.Running => Checks.Count(c => c.State is "running" or "queued") is var n && n > 0
            ? $"{n} gate{(n > 1 ? "s" : "")} running"
            : "Gates running",
        PipelineState.Succeeded => "All gates passed",
        PipelineState.Failed => Checks.Count(c => c.State is "rejected" or "broken") is var n && n > 0
            ? $"{n} gate{(n > 1 ? "s" : "")} failed"
            : "Gates failed",
        _ => "",
    };

    public bool HasPipelineStatus => PipelineStatus != PipelineState.None;

    public string Age
    {
        get
        {
            var span = DateTime.UtcNow - CreationDate;
            if (span.TotalDays >= 1) return $"{(int)span.TotalDays}d ago";
            if (span.TotalHours >= 1) return $"{(int)span.TotalHours}h ago";
            return $"{(int)span.TotalMinutes}m ago";
        }
    }

    public string VoteSummary
    {
        get
        {
            var votes = Reviewers.Where(r => r.Vote != 0).ToList();
            if (votes.Count == 0) return "No votes yet";
            return string.Join("  ", votes.Select(v =>
                $"{v.ShortName} {v.VoteIcon}"));
        }
    }

    public string DevOpsUrl(string webBaseUrl) =>
        $"{webBaseUrl}/_git/{Uri.EscapeDataString(RepositoryName)}/pullrequest/{PullRequestId}";
}

public sealed class PipelineCheck
{
    public string Name { get; init; } = "";
    public string State { get; init; } = "";
    public string Description { get; init; } = "";
}

public sealed class ReviewerInfo
{
    public string Id { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public string UniqueName { get; init; } = "";
    public int Vote { get; init; }

    public string ShortName => DisplayName.Split(' ')[0];

    public string VoteIcon => Vote switch
    {
        10 => "✅",
        5 => "✅~",
        -5 => "⏳",
        -10 => "❌",
        _ => "○",
    };

    public string VoteLabel => Vote switch
    {
        10 => "Approved",
        5 => "Approved with suggestions",
        0 => "No vote",
        -5 => "Waiting for author",
        -10 => "Rejected",
        _ => "Unknown",
    };
}
