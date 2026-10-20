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

    /// <summary>"crash" for fatal and error, "report" for warning and info.</summary>
    public string Kind { get; set; } = "";

    /// <summary>Every event since Sentry first saw it, as Sentry itself counts them.</summary>
    public int Events { get; set; }

    /// <summary>Everyone it has ever reached, which is what the list is ranked by.</summary>
    public int Users { get; set; }

    /// <summary>Events within <see cref="RecentWindow"/>.</summary>
    public int RecentEvents { get; set; }

    /// <summary>People reached within <see cref="RecentWindow"/>: whether it is still going.</summary>
    public int RecentUsers { get; set; }

    /// <summary>The window the recent counts cover, e.g. "14d". Only issues seen in it are listed.</summary>
    public string RecentWindow { get; set; } = "";

    /// <summary>When Sentry first saw it.</summary>
    public DateTimeOffset FirstSeen { get; set; }

    public DateTimeOffset LastSeen { get; set; }

    /// <summary>App versions it has been seen in, e.g. "26.26.0 → 26.37.1".</summary>
    public string Releases { get; set; } = "";

    /// <summary>Huginn is pointing at this one.</summary>
    public bool Raised { get; set; }

    /// <summary>Why it was raised: new, regressed or worse. Empty when not raised.</summary>
    public string RaisedReason { get; set; } = "";

    public bool Muted { get; set; }

    /// <summary>Event counts per bucket over the last 24 hours, oldest first.</summary>
    public IReadOnlyList<double> Series { get; set; } = [];

    public int SeriesBucketMinutes { get; set; }

    /// <summary>The series in a phrase: "constant", "one burst 3h ago", "easing off".</summary>
    public string Trend { get; set; } = "";

    public string Url { get; set; } = "";
}
