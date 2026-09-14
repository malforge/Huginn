using System;
using System.Collections.Generic;

namespace Huginn.Services.Agent;

/// <summary>
/// Everything Huginn knows, flattened for a reader that is not Huginn. Written after each poll so
/// an agent can answer "what needs attention" without credentials, a port, or a running UI.
/// </summary>
public sealed class AgentSnapshot
{
    /// <summary>When this was written. A reader decides for itself whether that is recent enough.</summary>
    public DateTimeOffset GeneratedAt { get; set; }

    public string HuginnVersion { get; set; } = "";

    /// <summary>Health of each source, so silence can be told apart from nothing being wrong.</summary>
    public List<AgentSource> Sources { get; set; } = [];

    public List<AgentPullRequest> PullRequests { get; set; } = [];

    public List<AgentBuild> Builds { get; set; } = [];

    public List<AgentCrash> Crashes { get; set; } = [];

    public List<AgentFinding> Services { get; set; } = [];

    /// <summary>
    /// What the rules held back. Published so a reader can tell "nothing is wrong" apart from
    /// "something was filtered", which is the distinction a monitor must never blur.
    /// </summary>
    public List<AgentSuppressed> Suppressed { get; set; } = [];
}
