using System;

namespace Huginn.Services.Agent;

/// <summary>One monitored source and whether it is currently answering.</summary>
public sealed class AgentSource
{
    public string Name { get; set; } = "";

    /// <summary>Connected, AuthFailed, Error, Connecting or NotConfigured.</summary>
    public string State { get; set; } = "";

    public string Message { get; set; } = "";

    /// <summary>Null when this source has never completed a poll.</summary>
    public DateTimeOffset? LastPolled { get; set; }

    /// <summary>The source is not answering, so its empty list means nothing.</summary>
    public bool NeedsAttention { get; set; }
}
