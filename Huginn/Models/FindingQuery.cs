using System;
using System.Collections.Generic;

namespace Huginn.Models;

/// <summary>
/// The queries behind a finding: the one that found it, and the one that explains it.
/// </summary>
/// <remarks>
/// Kept in one place because both readers need the same thing. A person pastes
/// <see cref="Evidence"/> into the portal to see exactly what Huginn saw; an agent asks Huginn to
/// run <see cref="Explanations"/> and gets the answer without needing the schema or the access.
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

            FindingKind.Exception => $"""
                exceptions
                | where timestamp > ago({window})
                | where problemId == {subject}
                | join kind=leftanti (
                      requests
                      | where timestamp > ago({window}) and success == false
                      | project operation_Id
                  ) on operation_Id
                | summarize thrown = sum(itemCount), recorded = count() by operation_Name
                | order by thrown desc
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
    /// What to show a person who opens the finding in the portal: why it is happening, rather
    /// than the measurement that <see cref="Evidence"/> reproduces.
    /// </summary>
    /// <remarks>
    /// This one may select the stack and the message, which <see cref="Explanations"/> may not.
    /// Its rows land in a browser the person is already signed into, not in an agent transcript.
    /// </remarks>
    public static string Portal(ServiceFinding finding)
    {
        string window = Window(finding);
        string subject = Literal(finding.Subject);

        return finding.Kind switch
        {
            // The failed requests themselves, with whatever they threw beside them. Read from
            // exceptions alone, a failure that threw nothing, such as a 404 or a handled error,
            // opens on no results at all. leftouter keeps those. calls is how many requests
            // each recorded one stands for under sampling, so the rows add up to the card.
            FindingKind.FailureRate => $"""
                requests
                | where timestamp > ago({window}) and success == false
                | where name == {subject}
                | project timestamp, resultCode, calls = itemCount, url, duration, operation_Id
                | join kind=leftouter (
                      exceptions
                      | where timestamp > ago({window})
                      | project operation_Id, type, outerMessage, method, details
                  ) on operation_Id
                | project timestamp, resultCode, calls, url, duration, type, outerMessage, method, details
                | order by timestamp desc
                """,

            // A dependency failure carries no stack of its own, so reach the caller’s through
            // the operation they share. leftouter keeps the failures that raised nothing.
            FindingKind.Dependency => $"""
                dependencies
                | where timestamp > ago({window}) and success == false
                | where strcat(type, " ", target) == {subject}
                | project operation_Id, timestamp, resultCode, operation_Name
                | join kind=leftouter (
                      exceptions
                      | where timestamp > ago({window})
                      | project operation_Id, type, outerMessage, details
                  ) on operation_Id
                | project timestamp, operation_Name, resultCode, type, outerMessage, details
                | order by timestamp desc
                """,

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

            FindingKind.Exception => $"""
                exceptions
                | where timestamp > ago({window})
                | where problemId == {subject}
                | project timestamp, operation_Name, type, outerMessage, method, assembly, details
                | order by timestamp desc
                """,

            // Nothing arriving: the shape of the silence is the whole answer.
            _ => Evidence(finding),
        };
    }

    /// <summary>
    /// Says why it is failing rather than that it is. Deliberately selects nothing that
    /// identifies a person: no user ids, no query strings, no custom dimensions. Telemetry read
    /// this way ends up in an agent's context and from there in a transcript.
    /// </summary>
    public static IReadOnlyList<Drilldown> Explanations(ServiceFinding finding)
    {
        string window = Window(finding);
        string subject = Literal(finding.Subject);

        return finding.Kind switch
        {
            FindingKind.Latency =>
            [
                new Drilldown("How the latency moved through the window", $"""
                    requests
                    | where timestamp > ago({window})
                    | where name == {subject}
                    | summarize calls = sum(itemCount),
                                p50 = round(percentile(duration, 50)),
                                p95 = round(percentile(duration, 95)),
                                p99 = round(percentile(duration, 99))
                              by bin(timestamp, 15m)
                    | order by timestamp asc
                    """),
            ],

            FindingKind.Dependency =>
            [
                new Drilldown("Failures by result code, over time", $"""
                    dependencies
                    | where timestamp > ago({window}) and success == false
                    | where strcat(type, " ", target) == {subject}
                    | summarize failures = sum(itemCount),
                                slowest = round(max(duration))
                              by resultCode, bin(timestamp, 30m)
                    | order by timestamp asc
                    """),
            ],

            FindingKind.Exception =>
            [
                new Drilldown("Where it is thrown and what it says, over time", $"""
                    exceptions
                    | where timestamp > ago({window})
                    | where problemId == {subject}
                    | summarize occurrences = sum(itemCount)
                              by operation_Name, outerMessage, method, bin(timestamp, 1h)
                    | order by timestamp asc
                    | take 50
                    """),
            ],

            FindingKind.NoTraffic =>
            [
                new Drilldown("Traffic over the last week", """
                    requests
                    | where timestamp > ago(7d)
                    | summarize calls = sum(itemCount) by bin(timestamp, 1h)
                    | order by timestamp asc
                    """),
            ],

            // A failing route: the codes, then whatever the server threw behind them. Two
            // queries rather than one of two statements, so each answer arrives labelled.
            _ =>
            [
                new Drilldown("Result codes", $"""
                    requests
                    | where timestamp > ago({window}) and name == {subject}
                    | summarize calls = sum(itemCount) by resultCode
                    | order by calls desc
                    | take 10
                    """),
                new Drilldown("The failures, and what they threw where they threw anything", $"""
                    requests
                    | where timestamp > ago({window}) and name == {subject} and success == false
                    | project operation_Id, resultCode, itemCount
                    | join kind=leftouter (
                          exceptions
                          | where timestamp > ago({window})
                          | project operation_Id, type, outerMessage, method
                      ) on operation_Id
                    | summarize failures = sum(itemCount) by resultCode, type, outerMessage, method
                    | order by failures desc
                    | take 10
                    """),
            ],
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
