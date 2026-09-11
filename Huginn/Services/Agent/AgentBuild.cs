namespace Huginn.Services.Agent;

/// <summary>A build Huginn is reporting on, which is to say a failing or retrying one.</summary>
public sealed class AgentBuild
{
    public string Definition { get; set; } = "";

    public string Branch { get; set; } = "";

    public string RequestedBy { get; set; } = "";

    public string Age { get; set; } = "";

    /// <summary>failed, retrying or acknowledged.</summary>
    public string Status { get; set; } = "";

    public string Url { get; set; } = "";
}
