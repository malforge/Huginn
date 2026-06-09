using System.Collections.Generic;

namespace Huginn.Models;

/// <summary>
/// Result of a single polling cycle, containing categorized PR lists, build failures, and the authenticated user's ID.
/// </summary>
public sealed class PollResult
{
    public required List<PullRequestItem> ReviewPrs { get; init; }
    public required List<PullRequestItem> UnstaffedPrs { get; init; }
    public required List<PullRequestItem> StaffedMyPrs { get; init; }
    public required string UserId { get; init; }

    /// <summary>My triggered builds that recently failed.</summary>
    public List<BuildItem> MyFailedBuilds { get; init; } = [];

    /// <summary>Latest build per watched pipeline (only those that failed).</summary>
    public List<BuildItem> WatchedPipelineFailures { get; init; } = [];
}
