using System;

namespace Huginn.Services.Agent;

/// <summary>A build Huginn is reporting on, which is to say a failing or retrying one.</summary>
public sealed class AgentBuild
{
    public string Definition { get; set; } = "";

    public string Branch { get; set; } = "";

    public string RequestedBy { get; set; } = "";

    public string Age { get; set; } = "";

    /// <summary>When the failed run finished, so a reader of an older snapshot can work out the age itself.</summary>
    public DateTime FinishedAt { get; set; }

    /// <summary>failed, retrying or acknowledged.</summary>
    public string Status { get; set; } = "";

    /// <summary>
    /// A newer run is under way after this failure. Kept apart from <see cref="Status"/>, since an
    /// acknowledged failure can be retrying too.
    /// </summary>
    public bool Retrying { get; set; }

    /// <summary>The failed run.</summary>
    public string Url { get; set; } = "";

    /// <summary>The run under way, when <see cref="Retrying"/>.</summary>
    public string RetryUrl { get; set; } = "";
}
