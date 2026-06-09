using System;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Huginn.Models;

public enum BuildResult { None, Succeeded, PartiallySucceeded, Failed, Canceled }

public sealed partial class BuildItem : ObservableObject
{
    public int Id { get; init; }
    public int DefinitionId { get; init; }
    public string DefinitionName { get; init; } = "";
    public BuildResult Result { get; init; }
    public string SourceBranch { get; init; } = "";
    public string RequestedBy { get; init; } = "";
    public DateTime FinishTime { get; init; }
    public string WebUrl { get; init; } = "";

    /// <summary>A newer build is currently running for this definition.</summary>
    public bool RetryInProgress { get; set; }

    /// <summary>URL of the superseding build (retry), if one exists.</summary>
    public string RetryBuildUrl { get; set; } = "";

    /// <summary>User has acknowledged this failure — kept in the list but excluded from the badge.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AcknowledgeTooltip))]
    private bool _isAcknowledged;

    public string AcknowledgeTooltip => IsAcknowledged
        ? "Unacknowledge — bring back into the badge"
        : "Acknowledge — hide from the badge until the next run";

    public string BranchShortName => SourceBranch.StartsWith("refs/heads/", StringComparison.Ordinal)
        ? SourceBranch["refs/heads/".Length..]
        : SourceBranch;

    public string Age
    {
        get
        {
            var span = DateTime.UtcNow - FinishTime;
            if (span.TotalDays >= 1) return $"{(int)span.TotalDays}d ago";
            if (span.TotalHours >= 1) return $"{(int)span.TotalHours}h ago";
            return $"{(int)span.TotalMinutes}m ago";
        }
    }

    public string ResultIcon => Result switch
    {
        BuildResult.Succeeded => "✅",
        BuildResult.PartiallySucceeded => "⚠️",
        BuildResult.Failed => "❌",
        BuildResult.Canceled => "🚫",
        _ => "❓",
    };
}

public sealed class PipelineDefinition
{
    public int Id { get; init; }
    public string Name { get; init; } = "";
    public string Path { get; init; } = "";

    /// <summary>Display name for the pipeline picker: "FolderPath \ Name" or just "Name".</summary>
    public string DisplayName => string.IsNullOrEmpty(Path) || Path == "\\"
        ? Name
        : $"{Path.TrimStart('\\')} \\ {Name}";
}
