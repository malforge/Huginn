using System;

namespace Huginn.Models;

/// <summary>
/// The queries behind a finding: the one that found it, and the one that explains it.
/// </summary>
/// <remarks>
/// Kept in one place because both readers need the same thing. A person pastes
/// <see cref="Evidence"/> into the portal to see exactly what Huginn saw; an agent asks Huginn to
/// run <see cref="Explanation"/> and gets the answer without needing the schema or the access.
/// </remarks>
public static class FindingQuery
{
    /// <summary>
    /// Reproduces the finding. The portal's own blades answer over a different window and a
    /// different population, so they disagree with Huginn and there is no way to tell who is
    /// right; this settles it.
    /// </summary>
    public static string Evidence(ServiceFinding finding)
    {
        string window = Window(finding);
        string subject = Literal(finding.Subject);

        return finding.Kind switch
        {
            FindingKind.Dependency => $"""
                dependencies
                | where timestamp > ago({window})
                | where strcat(type, " ", target) == {subject}
                | summarize calls = sum(itemCount),
                            failures = sumif(itemCount, success == false)
                          by resultCode
                | order by failures desc
                """,

            FindingKind.NoTraffic => $"""
                requests
                | where timestamp > ago({window})
                | summarize calls = sum(itemCount) by bin(timestamp, 5m)
                | order by timestamp asc
                """,

            _ => $"""
                requests
                | where timestamp > ago({window})
                | where name == {subject}
                | summarize calls = sum(itemCount),
                            failures = sumif(itemCount, success == false),
                            p95 = round(percentile(duration, 95))
                          by resultCode
                | order by calls desc
                """,
        };
    }

    /// <summary>
    /// Says why it is failing rather than that it is. Deliberately selects nothing that
    /// identifies a person: no user ids, no query strings, no custom dimensions. Telemetry read
    /// this way ends up in an agent's context and from there in a transcript.
    /// </summary>
    public static string Explanation(ServiceFinding finding)
    {
        string window = Window(finding);
        string subject = Literal(finding.Subject);

        return finding.Kind switch
        {
            FindingKind.Latency => $"""
                requests
                | where timestamp > ago({window})
                | where name == {subject}
                | summarize calls = sum(itemCount),
                            p50 = round(percentile(duration, 50)),
                            p95 = round(percentile(duration, 95)),
                            p99 = round(percentile(duration, 99))
                          by bin(timestamp, 15m)
                | order by timestamp asc
                """,

            FindingKind.Dependency => $"""
                dependencies
                | where timestamp > ago({window}) and success == false
                | where strcat(type, " ", target) == {subject}
                | summarize failures = sum(itemCount),
                            slowest = round(max(duration))
                          by resultCode, bin(timestamp, 30m)
                | order by timestamp asc
                """,

            FindingKind.NoTraffic => $"""
                requests
                | where timestamp > ago(7d)
                | summarize calls = sum(itemCount) by bin(timestamp, 1h)
                | order by timestamp asc
                """,

            // A failing route: the codes, then whatever the server threw behind them.
            _ => $"""
                let window = {window};
                let route = {subject};
                requests
                | where timestamp > ago(window) and name == route
                | summarize calls = sum(itemCount) by resultCode
                | order by calls desc
                | take 10;
                requests
                | where timestamp > ago(window) and name == route and success == false
                | join kind=inner (exceptions | where timestamp > ago(window)) on operation_Id
                | summarize occurrences = count() by type, outerMessage, method
                | order by occurrences desc
                | take 10
                """,
        };
    }

    private static string Window(ServiceFinding finding) =>
        $"{Math.Max(5, finding.WindowMinutes)}m";

    /// <summary>
    /// Wraps a value as a KQL string. Operation names come back from Azure rather than from us,
    /// so they are not assumed to be free of quotes.
    /// </summary>
    private static string Literal(string value) =>
        "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
}
