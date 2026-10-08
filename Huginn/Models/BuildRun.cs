namespace Huginn.Models;

/// <summary>One run of a pipeline, as far as deciding what an earlier failure on it means now.</summary>
public sealed record BuildRun(
    int Id,
    int DefinitionId,
    string SourceBranch,
    string Status,
    BuildResult Result,
    string WebUrl)
{
    /// <summary>Queued or running: it has no answer yet.</summary>
    public bool IsPending => Status is "inProgress" or "notStarted" or "postponed";

    public bool IsCompleted => Status == "completed";
}
