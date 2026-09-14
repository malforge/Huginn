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

    /// <summary>
    /// Calls a route answering mostly 404 needs before it becomes a finding, rather than the
    /// ordinary <see cref="MinimumCalls"/>.
    /// </summary>
    /// <remarks>
    /// An internet-facing resource is scanned continuously, and scanning is exactly the act of
    /// requesting paths that do not exist: one measured window held 2811 distinct 404 operations
    /// averaging four calls each. Probing is unbounded and endlessly novel, so no list of it can
    /// be kept current, but it is uniformly quiet per path. A route of our own that is broken is
    /// the opposite shape: one path, called as often as the app calls it.
    ///
    /// The cost is that a rarely-called route of ours answering 404 stays quiet. The settings
    /// view lists what this suppressed, so that is one glance away rather than invisible.
    /// </remarks>
    private const int MinimumCallsWhenMostly404 = 200;

    /// <summary>Share of a route's failures that must be 404 before it counts as probe-shaped.</summary>
    private const double Mostly404 = 0.9;

    /// <summary>Distinct failing result codes named on a card before it stops being readable.</summary>
    private const int MaxCodesPerRoute = 3;

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

    public record Snapshot(
        List<ServiceFinding> Findings,
        List<ServiceFinding> NewFindings,
        List<IgnoredSubject> Ignored);

    public async Task<Snapshot> PollAsync(
        AppInsightsApiClient client,
        IReadOnlyList<AppInsightsComponent> components,
        AppSettings settings,
        CancellationToken ct)
    {
        List<ServiceFinding> findings = [];

        // What the rules held back, so the settings view can show it. A filter that cannot be
        // audited is how a real failure stays hidden, which is the whole reason the blanket 404
        // exclusion went unnoticed for as long as it did.
        List<IgnoredSubject> suppressed = [];

        foreach (AppInsightsComponent component in components)
        {
            try
            {
                findings.AddRange(await ExamineAsync(
                    client, component, Math.Max(5, settings.AppInsightsWindowMinutes),
                    settings, suppressed, ct));
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

        // Traffic that is not ours at all never becomes a finding, so it cannot distort a
        // failure rate either. What was dropped is reported rather than silently discarded: a
        // filter nobody can audit is how a real failure stays hidden.
        List<IgnoredSubject> ignored = [.. suppressed];

        for (int i = findings.Count - 1; i >= 0; i--)
        {
            ServiceFinding finding = findings[i];
            if (settings.IsAlwaysShown(finding.Subject)) continue;

            string? pattern = settings.IgnoredSubjects
                .FirstOrDefault(p => Glob.Matches(p, finding.Subject));

            if (pattern == null) continue;

            ignored.Add(new IgnoredSubject(
                finding.Subject, finding.ResourceName, pattern, finding.Calls));
            findings.RemoveAt(i);
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

        return new Snapshot(findings, raised, ignored);
    }

    private static async Task<List<ServiceFinding>> ExamineAsync(
        AppInsightsApiClient client, AppInsightsComponent component, int windowMinutes,
        AppSettings settings, List<IgnoredSubject> suppressed, CancellationToken ct)
    {
        List<ServiceFinding> findings = [];
        string window = $"{windowMinutes}m";

        // 404s are counted like any other failure. They used to be excluded wholesale as probe
        // traffic, which hid a route of our own being called thousands of times an hour and
        // answering 404 every time. Probes are dealt with by the ignore list instead, which the
        // user can see and change.
        QueryTable routes = await client.QueryAsync(component.AppId, $"""
            requests
            | where timestamp > ago({window})
            | summarize total = sum(itemCount),
                        failed = sumif(itemCount, success == false),
                        notFound = sumif(itemCount, toint(resultCode) == 404),
                        p95 = round(percentile(duration, 95)),
                        codes = strcat_array(make_set_if(resultCode, success == false, {MaxCodesPerRoute}), ", ")
                      by name
            | where total >= {MinimumCalls}
            """, ct);

        int nameAt = routes.IndexOf("name");
        int totalAt = routes.IndexOf("total");
        int failedAt = routes.IndexOf("failed");
            int p95At = routes.IndexOf("p95");
        int codesAt = routes.IndexOf("codes");
        int notFoundAt = routes.IndexOf("notFound");

        long traffic = 0;

        foreach (IReadOnlyList<string> row in routes.Rows)
        {
            string name = Text(row, nameAt);
            long total = Number(row, totalAt);
            long failed = Number(row, failedAt);
            double p95 = Number(row, p95At);
            traffic += total;

            long notFound = Number(row, notFoundAt);

            // Scanner traffic is quiet per path and unbounded in variety, so it is held back by
            // volume rather than by a list of paths nobody could keep current.
            bool probeShaped = failed > 0
                               && (double)notFound / failed >= Mostly404
                               && total < MinimumCallsWhenMostly404
                               && !settings.IsAlwaysShown(name);

            double rate = total == 0 ? 0 : (double)failed / total;

            if (rate >= FailureRateThreshold && probeShaped)
            {
                suppressed.Add(new IgnoredSubject(
                    name, component.Name,
                    $"mostly 404 and under {MinimumCallsWhenMostly404} calls", total));
            }

            if (rate >= FailureRateThreshold && !probeShaped)
            {
                findings.Add(new ServiceFinding
                {
                    Kind = FindingKind.FailureRate,
                    ResourceName = component.Name,
                    AppId = component.AppId,
                    ResourceId = component.ResourceId,
                    Subject = name,
                    Detail = $"{rate:P0} of {total:N0} calls failed{Codes(Text(row, codesAt))}",
                    Magnitude = rate * 100,
                    Calls = total,
                    WindowMinutes = windowMinutes,
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
                    Calls = total,
                    WindowMinutes = windowMinutes,
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
                Detail = $"{failed:N0} failures{Codes(Text(row, codeAt))}",
                Magnitude = failed,
                Calls = failed,
                WindowMinutes = windowMinutes,
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
                WindowMinutes = windowMinutes,
            });
        }

        return findings;
    }

    /// <summary>
    /// The result codes behind a failing route, as a phrase. Which code it is decides what to do
    /// about it, so a bare failure rate sends you to the portal to find out.
    /// </summary>
    private static string Codes(string codes) =>
        codes.Length == 0 ? ""
        : codes.Contains(',') ? $", codes {codes}"
        : $", code {codes}";

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
            finding.SeriesValues = points;
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
