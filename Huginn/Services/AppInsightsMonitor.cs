using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Huginn.Models;

namespace Huginn.Services;

/// <summary>
/// Watches Application Insights resources for routes that are failing or slow, dependencies that
/// are erroring, and resources that have gone quiet.
/// </summary>
/// <remarks>
/// Application Insights reports a stream rather than discrete problems, so the rules below define
/// what counts as wrong. They are deliberately fixed for now: thresholds chosen before seeing
/// which alerts actually fire are guesses, and a panel of uncalibrated numbers is worse than none.
/// </remarks>
public sealed class AppInsightsMonitor
{
    /// <summary>Calls a route needs in the window before its rates mean anything.</summary>
    private const int MinimumCalls = 10;

    /// <summary>Share of calls that must fail before a route is worth raising.</summary>
    private const double FailureRateThreshold = 0.10;

    /// <summary>95th percentile above which a route counts as slow.</summary>
    private const int LatencyThresholdMs = 5000;

    /// <summary>Failures a dependency needs in the window before it is worth raising.</summary>
    private const int DependencyFailureThreshold = 5;

    /// <summary>How much worse a muted finding must get before it is raised again.</summary>
    private const double EscalationFactor = 2;

    private readonly HashSet<string> _known = [];
    private bool _hasBaseline;

    public record Snapshot(List<ServiceFinding> Findings, List<ServiceFinding> NewFindings);

    public async Task<Snapshot> PollAsync(
        AppInsightsApiClient client,
        IReadOnlyList<AppInsightsComponent> components,
        AppSettings settings,
        CancellationToken ct)
    {
        List<ServiceFinding> findings = [];

        foreach (AppInsightsComponent component in components)
        {
            try
            {
                findings.AddRange(await ExamineAsync(
                    client, component, Math.Max(5, settings.AppInsightsWindowMinutes), ct));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // One unreadable resource must not hide the others.
                Log.Error($"Could not examine {component.Name}: {ex.GetType().Name}: {ex.Message}");
                Log.Error(ex.ToString());
            }
        }

        // Unlike a Sentry backlog, a finding describes what is wrong right now. There is no
        // such thing as an old one worth hiding: if the route is failing, it belongs on screen.
        // So everything unmuted is listed, and only the notification is held back to the ones
        // that are genuinely new to this session.
        List<ServiceFinding> raised = [];

        foreach (ServiceFinding finding in findings)
        {
            if (settings.HasFindingOutgrownMute(finding.Id, finding.Magnitude, EscalationFactor))
                settings.UnmuteFinding(finding.Id);

            finding.IsMuted = settings.IsFindingMuted(finding.Id);
            finding.MutedAtMagnitude = settings.GetFindingMutedAt(finding.Id);
            finding.FlagReason = finding.KindLabel;

            if (!finding.IsMuted && _known.Add(finding.Id) && _hasBaseline)
                raised.Add(finding);
        }

        _hasBaseline = true;

        // A finding that has gone away stops being known, so its return is news again.
        _known.IntersectWith(findings.Select(f => f.Id));

        // Severity first, then size within a kind. Magnitudes are not comparable across kinds.
        findings.Sort((a, b) =>
        {
            int severity = b.Severity.CompareTo(a.Severity);
            return severity != 0 ? severity : b.Magnitude.CompareTo(a.Magnitude);
        });

        return new Snapshot(findings, raised);
    }

    private static async Task<List<ServiceFinding>> ExamineAsync(
        AppInsightsApiClient client, AppInsightsComponent component, int windowMinutes,
        CancellationToken ct)
    {
        List<ServiceFinding> findings = [];
        string window = $"{windowMinutes}m";

        // Requests that 404 are excluded wholesale. On an internet-facing resource they are
        // overwhelmingly bots probing for /wp-admin and friends, which would otherwise drown out
        // every real signal; a 404 from our own client is a client bug rather than an outage.
        QueryTable routes = await client.QueryAsync(component.AppId, $"""
            requests
            | where timestamp > ago({window})
            | where toint(resultCode) != 404
            | summarize total = sum(itemCount),
                        failed = sumif(itemCount, success == false),
                        p95 = round(percentile(duration, 95))
                      by name
            | where total >= {MinimumCalls}
            """, ct);

        int nameAt = routes.IndexOf("name");
        int totalAt = routes.IndexOf("total");
        int failedAt = routes.IndexOf("failed");
        int p95At = routes.IndexOf("p95");

        long traffic = 0;

        foreach (IReadOnlyList<string> row in routes.Rows)
        {
            string name = Text(row, nameAt);
            long total = Number(row, totalAt);
            long failed = Number(row, failedAt);
            double p95 = Number(row, p95At);
            traffic += total;

            double rate = total == 0 ? 0 : (double)failed / total;
            if (rate >= FailureRateThreshold)
            {
                findings.Add(new ServiceFinding
                {
                    Kind = FindingKind.FailureRate,
                    ResourceName = component.Name,
                    AppId = component.AppId,
                    ResourceId = component.ResourceId,
                    Subject = name,
                    Detail = $"{rate:P0} of {total:N0} calls failed",
                    Magnitude = rate * 100,
                });
            }

            if (p95 >= LatencyThresholdMs)
            {
                findings.Add(new ServiceFinding
                {
                    Kind = FindingKind.Latency,
                    ResourceName = component.Name,
                    AppId = component.AppId,
                    ResourceId = component.ResourceId,
                    Subject = name,
                    Detail = $"p95 {p95 / 1000:N1}s over {total:N0} calls",
                    Magnitude = p95,
                });
            }
        }

        QueryTable dependencies = await client.QueryAsync(component.AppId, $"""
            dependencies
            | where timestamp > ago({window}) and success == false
            | summarize failed = sum(itemCount) by type, target, resultCode
            | where failed >= {DependencyFailureThreshold}
            | order by failed desc
            | take 10
            """, ct);

        int typeAt = dependencies.IndexOf("type");
        int targetAt = dependencies.IndexOf("target");
        int codeAt = dependencies.IndexOf("resultCode");
        int depFailedAt = dependencies.IndexOf("failed");

        foreach (IReadOnlyList<string> row in dependencies.Rows)
        {
            long failed = Number(row, depFailedAt);
            string target = Text(row, targetAt);

            findings.Add(new ServiceFinding
            {
                Kind = FindingKind.Dependency,
                ResourceName = component.Name,
                AppId = component.AppId,
                ResourceId = component.ResourceId,
                Subject = $"{Text(row, typeAt)} {target}".Trim(),
                Detail = $"{failed:N0} failures, code {Text(row, codeAt)}",
                Magnitude = failed,
            });
        }

        await AddTrendsAsync(client, component, findings, windowMinutes, ct);

        // Silence is the failure a threshold never catches, so it gets its own rule.
        if (traffic == 0)
        {
            findings.Add(new ServiceFinding
            {
                Kind = FindingKind.NoTraffic,
                ResourceName = component.Name,
                AppId = component.AppId,
                ResourceId = component.ResourceId,
                Subject = component.Name,
                Detail = $"no requests in the last {Describe(windowMinutes)}",
                Magnitude = 1,
            });
        }

        return findings;
    }

    /// <summary>Buckets the window into roughly twenty points, never finer than a minute.</summary>
    private static int BucketMinutes(int windowMinutes) => Math.Max(1, windowMinutes / 20);

    /// <summary>
    /// Attaches a series to each finding so the card can say whether it is new. Two queries per
    /// resource rather than one per finding: the series come back grouped, and a resource with
    /// twenty findings would otherwise cost twenty round trips.
    /// </summary>
    private static async Task AddTrendsAsync(
        AppInsightsApiClient client,
        AppInsightsComponent component,
        List<ServiceFinding> findings,
        int windowMinutes,
        CancellationToken ct)
    {
        if (findings.Count == 0) return;

        int bucket = BucketMinutes(windowMinutes);
        string window = $"{windowMinutes}m";

        Dictionary<string, List<double>> latency = [];
        Dictionary<string, List<double>> failureRate = [];
        Dictionary<string, List<double>> dependency = [];

        if (findings.Any(f => f.Kind is FindingKind.Latency or FindingKind.FailureRate))
        {
            QueryTable series = await client.QueryAsync(component.AppId, $"""
                requests
                | where timestamp > ago({window})
                | where toint(resultCode) != 404
                | summarize p95 = round(percentile(duration, 95)),
                            failed = sumif(itemCount, success == false),
                            total = sum(itemCount)
                          by name, bin(timestamp, {bucket}m)
                | order by timestamp asc
                """, ct);

            int nameAt = series.IndexOf("name");
            int p95At = series.IndexOf("p95");
            int failedAt = series.IndexOf("failed");
            int totalAt = series.IndexOf("total");

            foreach (IReadOnlyList<string> row in series.Rows)
            {
                string name = Text(row, nameAt);
                long total = Number(row, totalAt);

                Append(latency, name, Number(row, p95At));
                Append(failureRate, name, total == 0 ? 0 : (double)Number(row, failedAt) / total * 100);
            }
        }

        if (findings.Any(f => f.Kind == FindingKind.Dependency))
        {
            QueryTable series = await client.QueryAsync(component.AppId, $"""
                dependencies
                | where timestamp > ago({window}) and success == false
                | summarize failed = sum(itemCount) by target, bin(timestamp, {bucket}m)
                | order by timestamp asc
                """, ct);

            int targetAt = series.IndexOf("target");
            int failedAt = series.IndexOf("failed");

            foreach (IReadOnlyList<string> row in series.Rows)
                Append(dependency, Text(row, targetAt), Number(row, failedAt));
        }

        foreach (ServiceFinding finding in findings)
        {
            List<double>? points = finding.Kind switch
            {
                FindingKind.Latency => Lookup(latency, finding.Subject),
                FindingKind.FailureRate => Lookup(failureRate, finding.Subject),
                FindingKind.Dependency => dependency.FirstOrDefault(
                    d => finding.Subject.EndsWith(d.Key, StringComparison.OrdinalIgnoreCase)).Value,
                _ => null,
            };

            if (points is null || points.Count < 2) continue;

            finding.Spark = Trend.Spark(points);
            finding.TrendVerdict = Trend.Describe(points, bucket);
        }

        static void Append(Dictionary<string, List<double>> into, string key, double value)
        {
            if (key.Length == 0) return;
            if (!into.TryGetValue(key, out List<double>? list)) into[key] = list = [];
            list.Add(value);
        }

        static List<double>? Lookup(Dictionary<string, List<double>> from, string key) =>
            from.TryGetValue(key, out List<double>? list) ? list : null;
    }

    /// <summary>Reads the window back as a person would say it.</summary>
    private static string Describe(int minutes) =>
        minutes % 60 == 0 ? $"{minutes / 60}h" : $"{minutes}m";

    private static string Text(IReadOnlyList<string> row, int index) =>
        index < 0 || index >= row.Count ? "" : row[index];

    private static long Number(IReadOnlyList<string> row, int index) =>
        index >= 0 && index < row.Count
        && double.TryParse(row[index], NumberStyles.Any, CultureInfo.InvariantCulture, out double d)
            ? (long)d
            : 0;

    public void Reset()
    {
        _known.Clear();
        _hasBaseline = false;
    }

    public StatusParts GetStatusParts(Snapshot snapshot)
    {
        StatusParts parts = new();
        int count = snapshot.Findings.Count(f => !f.IsMuted);
        if (count > 0)
            parts.Add($"{count} service finding{(count != 1 ? "s" : "")}");
        return parts;
    }
}
