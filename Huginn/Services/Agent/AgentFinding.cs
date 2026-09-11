using System.Collections.Generic;

namespace Huginn.Services.Agent;

/// <summary>One problem on a monitored Azure resource.</summary>
public sealed class AgentFinding
{
    /// <summary>FailureRate, Latency, Dependency or NoTraffic.</summary>
    public string Kind { get; set; } = "";

    public string Resource { get; set; } = "";

    /// <summary>The route or dependency it concerns.</summary>
    public string Subject { get; set; } = "";

    public string Detail { get; set; } = "";

    /// <summary>Size in the unit of its kind: percent, milliseconds or a failure count.</summary>
    public double Magnitude { get; set; }

    /// <summary>Higher wants attention sooner. Kinds are not comparable by magnitude.</summary>
    public int Severity { get; set; }

    public bool Muted { get; set; }

    /// <summary>The measurement over the window, oldest first.</summary>
    public IReadOnlyList<double> Series { get; set; } = [];

    /// <summary>The series in a phrase: "steady all window", "stepped up 35m ago".</summary>
    public string Trend { get; set; } = "";

    public string PortalUrl { get; set; } = "";
}
