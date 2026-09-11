namespace Huginn.Models;

/// <summary>What kind of problem a service finding describes.</summary>
public enum FindingKind
{
    /// <summary>A route is returning errors for a meaningful share of its calls.</summary>
    FailureRate,

    /// <summary>A route is slow at the 95th percentile.</summary>
    Latency,

    /// <summary>Something the service depends on is failing.</summary>
    Dependency,

    /// <summary>The resource has stopped receiving traffic entirely.</summary>
    NoTraffic,
}
