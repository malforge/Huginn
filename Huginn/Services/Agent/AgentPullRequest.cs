namespace Huginn.Services.Agent;

/// <summary>A pull request, with the queue it is sitting in.</summary>
public sealed class AgentPullRequest
{
    public int Id { get; set; }

    public string Title { get; set; } = "";

    public string Repository { get; set; } = "";

    public string Author { get; set; } = "";

    public string Age { get; set; } = "";

    /// <summary>Why it is listed: awaiting-review, missing-reviewers, failed-validation,
    /// autocomplete-off, mine or acknowledged.</summary>
    public string Queue { get; set; } = "";

    public bool IsDraft { get; set; }

    public string Votes { get; set; } = "";

    public string Pipeline { get; set; } = "";

    public string Url { get; set; } = "";
}
