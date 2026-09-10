namespace Huginn.Models;

/// <summary>One Application Insights resource that Huginn can watch.</summary>
public sealed class AppInsightsComponent
{
    /// <summary>Resource name, e.g. "test-go-insights-mobile-eurw".</summary>
    public string Name { get; init; } = "";

    /// <summary>The query API identifier, which is not the resource id.</summary>
    public string AppId { get; init; } = "";

    public string SubscriptionName { get; init; } = "";

    public string ResourceGroup { get; init; } = "";

    /// <summary>Resource group and subscription, for telling similarly named resources apart.</summary>
    public string Qualifier => $"{ResourceGroup} · {SubscriptionName}";
}
