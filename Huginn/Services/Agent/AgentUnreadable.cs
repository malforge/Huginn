namespace Huginn.Services.Agent;

/// <summary>A watched resource the last poll could not read.</summary>
public sealed class AgentUnreadable
{
    public string Name { get; set; } = "";

    /// <summary>What went wrong, in words the reader can act on.</summary>
    public string Reason { get; set; } = "";

    /// <summary>The sign-in has lapsed, so a person re-authenticating is the fix.</summary>
    public bool NeedsSignIn { get; set; }
}
