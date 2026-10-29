using System.Collections.Generic;
using System.Linq;

namespace Huginn.Models;

/// <summary>One Application Insights resource that Huginn can watch.</summary>
public sealed class AppInsightsComponent
{
    /// <summary>Resource name, as it appears in the Azure portal.</summary>
    public string Name { get; init; } = "";

    /// <summary>The query API identifier, which is not the resource id.</summary>
    public string AppId { get; init; } = "";

    public string SubscriptionName { get; init; } = "";

    public string ResourceGroup { get; init; } = "";

    /// <summary>Full ARM id. The portal addresses resources by this, not by the query app id.</summary>
    public string ResourceId { get; init; } = "";

    /// <summary>Resource group and subscription, for telling similarly named resources apart.</summary>
    public string Qualifier => $"{ResourceGroup} · {SubscriptionName}";

    /// <summary>
    /// The cloud roles to judge, when the resource is shared by several services. Empty judges
    /// everything in it.
    /// </summary>
    public IReadOnlyList<string> Roles { get; init; } = [];

    /// <summary>
    /// The filter that keeps a query to <see cref="Roles"/>, empty when every role counts. Every
    /// query against the resource carries it, so a shared resource reads as the service watched.
    /// </summary>
    public string RoleClause => Roles.Count == 0
        ? ""
        : "| where cloud_RoleName in (" + string.Join(", ", Roles.Select(Quote)) + ")";

    /// <summary>The resource as findings name it, with the roles when only some are watched.</summary>
    public string DisplayName => Roles.Count == 0 ? Name : $"{Name} › {string.Join(", ", Roles)}";

    /// <summary>Role names come from settings, so they are not assumed to be free of quotes.</summary>
    private static string Quote(string value) =>
        "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
}
