using System.Collections.Generic;
using System.Globalization;

namespace Huginn.Models;

/// <summary>Which services report to a resource, for choosing the ones to judge.</summary>
public static class CloudRoles
{
    /// <summary>How far back a role counts as reporting, as the query says it.</summary>
    public const string LookBack = "1d";

    /// <summary>
    /// The roles that reported lately, busiest first. Dependencies and exceptions count as well as
    /// requests, since a worker serving no requests is still judged by those.
    /// </summary>
    public const string Query = $"""
        union requests, dependencies, exceptions
        | where timestamp > ago({LookBack})
        | summarize requests = sumif(itemCount, itemType == "request"), other = sumif(itemCount, itemType != "request") by cloud_RoleName
        | order by requests desc, other desc
        | take 100
        """;

    /// <summary>The roles in a result of <see cref="Query"/>, leaving out telemetry sent with no role.</summary>
    public static List<CloudRole> From(QueryTable table)
    {
        int nameAt = table.IndexOf("cloud_RoleName");
        int requestsAt = table.IndexOf("requests");
        int otherAt = table.IndexOf("other");
        List<CloudRole> roles = [];
        if (nameAt < 0) return roles;

        foreach (IReadOnlyList<string> row in table.Rows)
        {
            string name = row[nameAt].Trim();
            if (name.Length == 0) continue;
            roles.Add(new CloudRole(name, Count(row, requestsAt), Count(row, otherAt)));
        }

        return roles;
    }

    private static long Count(IReadOnlyList<string> row, int at) =>
        at >= 0 && long.TryParse(row[at], NumberStyles.Integer, CultureInfo.InvariantCulture, out long n) ? n : 0;
}
