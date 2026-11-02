using System;

namespace Huginn.Services.Agent;

/// <summary>
/// A drill-down in which at least one query could not be run. It fails rather than returning,
/// so it is not kept and handed out again, while what it did find still reaches the asker.
/// </summary>
public sealed class InvestigationIncompleteException(string report)
    : Exception("Not every query of the drill-down could be run.")
{
    /// <summary>The report as far as it got, each query that failed saying why.</summary>
    public string Report { get; } = report;
}
