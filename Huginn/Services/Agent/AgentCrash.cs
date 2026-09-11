using System.Collections.Generic;
using System;

namespace Huginn.Services.Agent;

/// <summary>A Sentry issue, with the reach and shape that decide whether it matters.</summary>
public sealed class AgentCrash
{
    public string ShortId { get; set; } = "";

    public string Title { get; set; } = "";

    public string Culprit { get; set; } = "";

    public string Project { get; set; } = "";

    public string Level { get; set; } = "";

    public int Events { get; set; }

    public int Users { get; set; }

    public DateTimeOffset FirstSeen { get; set; }

    public DateTimeOffset LastSeen { get; set; }

    /// <summary>App versions it has been seen in, e.g. "26.26.0 → 26.37.1".</summary>
    public string Releases { get; set; } = "";

    /// <summary>Huginn is pointing at this one.</summary>
    public bool Raised { get; set; }

    /// <summary>Why it was raised: new, regressed, worse or widespread. Empty when not raised.</summary>
    public string RaisedReason { get; set; } = "";

    public bool Muted { get; set; }

    /// <summary>Event counts per bucket over the stats window, oldest first.</summary>
    public IReadOnlyList<double> Series { get; set; } = [];

    public int SeriesBucketMinutes { get; set; }

    /// <summary>The series in a phrase: "constant", "one burst 3h ago", "easing off".</summary>
    public string Trend { get; set; } = "";

    public string Url { get; set; } = "";
}
