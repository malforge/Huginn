namespace Huginn.Services.Agent;

/// <summary>Something the rules kept off the board, and why.</summary>
public sealed class AgentSuppressed
{
    public string Subject { get; set; } = "";

    public string Resource { get; set; } = "";

    /// <summary>The rule or pattern that held it back.</summary>
    public string Reason { get; set; } = "";

    public long Calls { get; set; }
}
