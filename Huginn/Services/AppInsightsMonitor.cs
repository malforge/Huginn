using System;
using System.Collections.Generic;
using Azure.Identity;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Huginn.Models;

namespace Huginn.Services;

/// <summary>
/// Watches Application Insights resources for routes that are failing or slow, dependencies that
/// are erroring, exceptions thrown outside any failed request, and resources that have gone quiet.
/// </summary>
/// <remarks>
/// Application Insights reports a stream rather than discrete problems, so the rules below define
/// what counts as wrong. They are deliberately fixed for now: thresholds chosen before seeing
/// which alerts actually fire are guesses, and a panel of uncalibrated numbers is worse than none.
/// </remarks>
public sealed class AppInsightsMonitor
{
    /// <summary>
    /// Requests a route needs actually recorded in the window before its rates mean anything.
    /// </summary>
    /// <remarks>
    /// Recorded rather than estimated. Under sampling each recorded request stands for many, so
    /// the estimated count can be large on the strength of a single observation, and a single
    /// failed request would read as a route failing every call.
    /// </remarks>
    private const int MinimumRecorded = 10;

    /// <summary>
    /// Estimated calls a route answering mostly 404 needs before it becomes a finding, on top of
    /// <see cref="MinimumRecorded"/>.
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

    /// <summary>Recorded failures a dependency needs in the window before it is worth raising.</summary>
    private const int DependencyFailureThreshold = 5;

    /// <summary>How much worse a muted finding must get before it is raised again.</summary>
    private const double EscalationFactor = 2;

    private readonly HashSet<string> _known = [];
    private bool _hasBaseline;

    public record Snapshot(
        List<ServiceFinding> Findings,
        List<ServiceFinding> NewFindings,
        List<IgnoredSubject> Ignored,
        List<UnreadableResource> Unreadable);

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

        // A resource that could not be read contributes no findings, so without this the pane
        // shows whatever was readable as if it were the whole picture, and a dead sign-in reads
        // as good news.
        List<UnreadableResource> unreadable = [];

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
                // One unreadable resource must not hide the others, but it must not hide itself
                // either: it is reported back rather than only logged.
                Log.Error($"Could not examine {component.DisplayName}: {ex.GetType().Name}: {ex.Message}");
                Log.Error(ex.ToString());
                unreadable.Add(new UnreadableResource(component.DisplayName, Describe(ex), NeedsSignIn(ex)));
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
            // The section heading already names the kind, so the pill says only what changed.
            bool isNew = !finding.IsMuted && _known.Add(finding.Id) && _hasBaseline;
            finding.IsFlagged = isNew;
            finding.FlagReason = isNew ? "new" : "";

            if (isNew) raised.Add(finding);
        }

        _hasBaseline = true;

        // A finding that has gone away stops being known, so its return is news again.
        _known.IntersectWith(findings.Select(f => f.Id));

        // Severity first, then size within a kind. Magnitudes are not comparable across kinds.
        findings.Sort((a, b) =>
        {
            int severity = b.Severity.CompareTo(a.Severity);
            return severity != 0 ? severity : Size(b).CompareTo(Size(a));
        });

        return new Snapshot(findings, raised, ignored, unreadable);
    }

    /// <summary>
    /// How big a finding is within its kind. A failing route is sized by the calls that failed
    /// rather than by its rate, so every call of a route hardly anyone uses failing does not
    /// outrank a busy route losing a tenth of thousands.
    /// </summary>
    private static double Size(ServiceFinding finding) =>
        finding.Kind == FindingKind.FailureRate ? finding.Magnitude / 100 * finding.Calls : finding.Magnitude;

    private static async Task<List<ServiceFinding>> ExamineAsync(
        AppInsightsApiClient client, AppInsightsComponent component, int windowMinutes,
        AppSettings settings, List<IgnoredSubject> suppressed, CancellationToken ct)
    {
        List<ServiceFinding> findings = [];
        string window = $"{windowMinutes}m";
        string role = component.RoleClause.Length == 0 ? "" : " " + component.RoleClause;

        // 404s are counted like any other failure. They used to be excluded wholesale as probe
        // traffic, which hid a route of our own being called thousands of times an hour and
        // answering 404 every time. Probes are dealt with by the ignore list instead, which the
        // user can see and change.
        QueryTable routes = await client.QueryAsync(component.AppId, $"""
            requests
            | where timestamp > ago({window}){role}
            | summarize total = sum(itemCount),
                        failed = sumif(itemCount, success == false),
                        notFound = sumif(itemCount, toint(resultCode) == 404),
                        p95 = round(percentile(duration, 95)),
                        codes = strcat_array(make_set_if(resultCode, success == false and isnotempty(resultCode), {MaxCodesPerRoute}), ", "),
                        lastFailed = maxif(timestamp, success == false),
                        recorded = count(),
                        users = dcountif(user_AuthenticatedId, success == false and isnotempty(user_AuthenticatedId))
                      by name
            | where recorded >= {MinimumRecorded} or failed > 0 or p95 >= {LatencyThresholdMs}
            """, ct);

        DateTimeOffset now = DateTimeOffset.UtcNow;
        findings.AddRange(FromRoutes(routes, component, windowMinutes, settings, suppressed, now));

        // One finding per dependency, whatever codes it failed with, so its threshold, its share
        // of calls and its sparkline all describe the same thing.
        QueryTable dependencies = await client.QueryAsync(component.AppId, $"""
            dependencies
            | where timestamp > ago({window}){role}
            | summarize calls = sum(itemCount),
                        failed = sumif(itemCount, success == false),
                        codes = strcat_array(make_set_if(resultCode, success == false and isnotempty(resultCode), {MaxCodesPerRoute}), ", "),
                        lastFailed = maxif(timestamp, success == false),
                        recorded = count(),
                        failedRecorded = countif(success == false),
                        users = dcountif(user_AuthenticatedId, success == false and isnotempty(user_AuthenticatedId))
                      by type, target
            | where failed > 0
            | order by failed desc
            """, ct);

        findings.AddRange(FromDependencies(dependencies, component, windowMinutes, settings, suppressed, now));

        // Exceptions behind a failed request are already on that route's card, so only the rest
        // are counted here. problemId groups by type and throwing method, as the portal does.
        QueryTable exceptions = await client.QueryAsync(component.AppId, $"""
            exceptions
            | where timestamp > ago({window}){role}
            | join kind=leftanti (
                  requests
                  | where timestamp > ago({window}) and success == false{role}
                  | project operation_Id
              ) on operation_Id
            | summarize thrown = sum(itemCount),
                        recorded = count(),
                        users = dcountif(user_AuthenticatedId, isnotempty(user_AuthenticatedId)),
                        operations = dcountif(operation_Name, isnotempty(operation_Name)),
                        operation = take_anyif(operation_Name, isnotempty(operation_Name)),
                        lastThrown = max(timestamp)
                      by problemId
            | order by thrown desc
            """, ct);

        findings.AddRange(FromExceptions(exceptions, component, windowMinutes, settings, suppressed, now));

        await AddTrendsAsync(client, component, findings, windowMinutes, ct);

        // Every request counts here, not only routes busy enough to be judged, or a resource
        // whose traffic is spread thinly would read as dead.
        QueryTable traffic = await client.QueryAsync(component.AppId, $"""
            requests
            | where timestamp > ago({window}){role}
            | summarize total = sum(itemCount),
                        lastHour = sumif(itemCount, timestamp > ago({SilenceMinutes}m)),
                        lastSeen = max(timestamp)
            """, ct);

        IReadOnlyList<string> row = traffic.Rows.Count == 0 ? [] : traffic.Rows[0];
        ServiceFinding? silent = NoTrafficFinding(
            component, windowMinutes,
            Number(row, traffic.IndexOf("total")), Number(row, traffic.IndexOf("lastHour")),
            Text(row, traffic.IndexOf("lastSeen")), now);

        if (silent is not null) findings.Add(silent);

        return findings;
    }

    public static List<ServiceFinding> FromRoutes(
        QueryTable routes, AppInsightsComponent component, int windowMinutes,
        AppSettings settings, List<IgnoredSubject> suppressed, DateTimeOffset now)
    {
        int nameAt = routes.IndexOf("name");
        int totalAt = routes.IndexOf("total");
        int failedAt = routes.IndexOf("failed");
        int p95At = routes.IndexOf("p95");
        int codesAt = routes.IndexOf("codes");
        int notFoundAt = routes.IndexOf("notFound");
        int lastFailedAt = routes.IndexOf("lastFailed");
        int recordedAt = routes.IndexOf("recorded");
        int usersAt = routes.IndexOf("users");

        List<ServiceFinding> findings = [];

        foreach (IReadOnlyList<string> row in routes.Rows)
        {
            string name = Text(row, nameAt);
            long total = Number(row, totalAt);
            long recorded = Number(row, recordedAt);
            long failed = Number(row, failedAt);
            double p95 = Number(row, p95At);

            // Too little recorded to judge is held back where it can be seen, never dropped:
            // a broken route of ours that is only lightly sampled must not vanish. Asking for a
            // route to be shown overrides it.
            bool thin = recorded < MinimumRecorded && !settings.IsAlwaysShown(name);

            bool sampled = recorded != total;

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
                    name, component.DisplayName,
                    $"mostly 404 and under {MinimumCallsWhenMostly404} calls", total));
            }
            else if (rate >= FailureRateThreshold && thin)
            {
                suppressed.Add(TooFew(name, component, FindingKind.FailureRate, $"{Count(recorded)} recorded", total));
            }

            if (rate >= FailureRateThreshold && !probeShaped && !thin)
            {
                findings.Add(new ServiceFinding
                {
                    Kind = FindingKind.FailureRate,
                    ResourceName = component.DisplayName,
                    AppId = component.AppId,
                    ResourceId = component.ResourceId,
                    RoleClause = component.RoleClause,
                    Subject = name,
                    Detail = $"{Share(rate)} of {Estimate(total, sampled)} calls failed{Codes(Text(row, codesAt))}",
                    Context = Evidence(
                        sampled ? $"{Count(recorded)} recorded" : "",
                        Users(Number(row, usersAt), sampled),
                        LastFailed(Text(row, lastFailedAt), now)),
                    Magnitude = rate * 100,
                    Calls = total,
                    WindowMinutes = windowMinutes,
                });
            }

            if (p95 >= LatencyThresholdMs && thin)
            {
                suppressed.Add(TooFew(name, component, FindingKind.Latency, $"{Count(recorded)} recorded", total));
            }
            else if (p95 >= LatencyThresholdMs)
            {
                findings.Add(new ServiceFinding
                {
                    Kind = FindingKind.Latency,
                    ResourceName = component.DisplayName,
                    AppId = component.AppId,
                    ResourceId = component.ResourceId,
                    RoleClause = component.RoleClause,
                    Subject = name,
                    Detail = $"p95 {(p95 / 1000).ToString("N1", English)}s over {Estimate(total, sampled)} calls",
                    Context = sampled ? $"{Count(recorded)} recorded" : "",
                    Magnitude = p95,
                    Calls = total,
                    WindowMinutes = windowMinutes,
                });
            }
        }

        return findings;
    }

    public static List<ServiceFinding> FromDependencies(
        QueryTable dependencies, AppInsightsComponent component, int windowMinutes,
        AppSettings settings, List<IgnoredSubject> heldBack, DateTimeOffset now)
    {
        List<ServiceFinding> findings = [];

        int typeAt = dependencies.IndexOf("type");
        int targetAt = dependencies.IndexOf("target");
        int callsAt = dependencies.IndexOf("calls");
        int failedAt = dependencies.IndexOf("failed");
        int codesAt = dependencies.IndexOf("codes");
        int lastFailedAt = dependencies.IndexOf("lastFailed");
        int recordedAt = dependencies.IndexOf("recorded");
        int failedRecordedAt = dependencies.IndexOf("failedRecorded");
        int usersAt = dependencies.IndexOf("users");

        foreach (IReadOnlyList<string> row in dependencies.Rows)
        {
            long calls = Number(row, callsAt);
            long failed = Number(row, failedAt);
            long failedRecorded = Number(row, failedRecordedAt);
            string target = Text(row, targetAt);

            string subject = $"{Text(row, typeAt)} {target}".Trim();

            if (failedRecorded < DependencyFailureThreshold && !settings.IsAlwaysShown(subject))
            {
                heldBack.Add(TooFew(
                    subject, component, FindingKind.Dependency,
                    $"{Count(failedRecorded)} failure{(failedRecorded == 1 ? "" : "s")} recorded", calls));
                continue;
            }

            bool callsSampled = Number(row, recordedAt) != calls;
            bool failuresSampled = failedRecorded != failed;

            // The share says whether this is a dead integration or a few bad calls out of many,
            // which a bare failure count cannot.
            double share = calls == 0 ? 0 : (double)failed / calls;

            findings.Add(new ServiceFinding
            {
                Kind = FindingKind.Dependency,
                ResourceName = component.DisplayName,
                AppId = component.AppId,
                ResourceId = component.ResourceId,
                RoleClause = component.RoleClause,
                Subject = $"{Text(row, typeAt)} {target}".Trim(),
                Detail = $"{Estimate(failed, failuresSampled)} of {Estimate(calls, callsSampled)} calls failed "
                         + $"({Share(share)}){Codes(Text(row, codesAt))}",
                Context = Evidence(
                    failuresSampled ? $"{Count(failedRecorded)} failures recorded" : "",
                    Users(Number(row, usersAt), failuresSampled),
                    LastFailed(Text(row, lastFailedAt), now)),
                Magnitude = failed,
                Calls = calls,
                WindowMinutes = windowMinutes,
            });
        }

        return findings;
    }

    /// <summary>
    /// Exceptions with no failed request behind them, one finding per problem. A failing route
    /// already reports what it threw, so these are the ones nothing else would show: background
    /// work, and failures that were caught and logged.
    /// </summary>
    public static List<ServiceFinding> FromExceptions(
        QueryTable exceptions, AppInsightsComponent component, int windowMinutes,
        AppSettings settings, List<IgnoredSubject> heldBack, DateTimeOffset now)
    {
        List<ServiceFinding> findings = [];

        int problemAt = exceptions.IndexOf("problemId");
        int thrownAt = exceptions.IndexOf("thrown");
        int recordedAt = exceptions.IndexOf("recorded");
        int usersAt = exceptions.IndexOf("users");
        int operationsAt = exceptions.IndexOf("operations");
        int operationAt = exceptions.IndexOf("operation");
        int lastThrownAt = exceptions.IndexOf("lastThrown");

        foreach (IReadOnlyList<string> row in exceptions.Rows)
        {
            long thrown = Number(row, thrownAt);
            long recorded = Number(row, recordedAt);

            string problem = Text(row, problemAt);

            if (recorded < MinimumRecorded && !settings.IsAlwaysShown(problem))
            {
                heldBack.Add(TooFew(problem, component, FindingKind.Exception, $"{Count(recorded)} recorded", thrown));
                continue;
            }

            bool sampled = recorded != thrown;
            long operations = Number(row, operationsAt);
            string operation = Text(row, operationAt);

            string where = operations > 1 ? $" across {Count(operations)} operations"
                : operation.Length > 0 ? $" in {operation}"
                : "";

            findings.Add(new ServiceFinding
            {
                Kind = FindingKind.Exception,
                ResourceName = component.DisplayName,
                AppId = component.AppId,
                ResourceId = component.ResourceId,
                RoleClause = component.RoleClause,
                Subject = Text(row, problemAt),
                Detail = $"{Estimate(thrown, sampled)} thrown{where}",
                Context = Evidence(
                    sampled ? $"{Count(recorded)} recorded" : "",
                    Users(Number(row, usersAt), sampled),
                    Last("thrown", Text(row, lastThrownAt), now)),
                Magnitude = thrown,
                Calls = thrown,
                WindowMinutes = windowMinutes,
            });
        }

        return findings;
    }

    /// <summary>The recent stretch silence is judged on.</summary>
    private const int SilenceMinutes = 60;

    /// <summary>
    /// Requests an hour a resource must average over the window before an empty last hour means
    /// it has gone quiet rather than that it is a quiet resource.
    /// </summary>
    private const int BusyPerHour = 10;

    /// <summary>
    /// Silence is the failure a threshold never catches, so it gets its own rule. Judged on the
    /// last hour as well as the whole window: a busy resource that stops partway through would
    /// otherwise not be called silent until the window has passed, and on a week that is a week.
    /// </summary>
    public static ServiceFinding? NoTrafficFinding(
        AppInsightsComponent component, int windowMinutes, long total, long lastHour, string lastSeen,
        DateTimeOffset now)
    {
        string detail;

        if (total == 0)
        {
            detail = $"no requests in the last {Describe(windowMinutes)}";
        }
        else
        {
            double perHour = total / (windowMinutes / 60.0);
            if (windowMinutes <= SilenceMinutes || lastHour > 0 || perHour < BusyPerHour) return null;

            string since = DateTimeOffset.TryParse(lastSeen, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal, out DateTimeOffset last)
                ? Ago(now - last)
                : $"{SilenceMinutes}m";

            detail = $"no requests for {since}, after about {Count((long)Math.Round(perHour))} an hour";
        }

        return new ServiceFinding
        {
            Kind = FindingKind.NoTraffic,
            ResourceName = component.DisplayName,
            AppId = component.AppId,
            ResourceId = component.ResourceId,
            RoleClause = component.RoleClause,
            Subject = component.DisplayName,
            Detail = detail,
            Magnitude = 1,
            WindowMinutes = windowMinutes,
        };
    }

    /// <summary>Something held back for having too little recorded to judge, saying how little.</summary>
    private static IgnoredSubject TooFew(
        string subject, AppInsightsComponent component, FindingKind kind, string recorded, long calls) =>
        new(subject, component.DisplayName, $"only {recorded}, too few to judge", calls, kind, TooFewRecorded: true);

    /// <summary>Copy is en-US whatever the machine is set to, so a finding reads the same everywhere.</summary>
    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en-US");

    private static string Count(long value) => value.ToString("N0", English);

    /// <summary>A count, marked as an estimate when sampling scaled it up from fewer recorded.</summary>
    private static string Estimate(long value, bool sampled) => sampled ? $"~{Count(value)}" : Count(value);

    /// <summary>
    /// People who hit a failure, a floor rather than a count when sampled, since anyone whose
    /// requests went unrecorded is not in it. Empty when the telemetry names nobody.
    /// </summary>
    private static string Users(long users, bool sampled) =>
        users == 0 ? ""
        : sampled ? $"at least {Count(users)} user{(users == 1 ? "" : "s")}"
        : $"{Count(users)} user{(users == 1 ? "" : "s")}";

    /// <summary>The parts of a finding's evidence that apply, joined as one line.</summary>
    private static string Evidence(params string[] parts) =>
        string.Join(" · ", parts.Where(p => p.Length > 0));

    /// <summary>A share as a whole percentage, saying under 1% rather than rounding to none.</summary>
    private static string Share(double share) =>
        share > 0 && share < 0.005 ? "<1%" : share.ToString("P0", English);

    /// <summary>
    /// When the last failure was, e.g. "last failed 3h ago". The window can be hours long, so a
    /// rate alone does not say whether the failing stopped long ago or is still going on.
    /// </summary>
    private static string LastFailed(string at, DateTimeOffset now) => Last("failed", at, now);

    /// <summary>A span as the cards write it, e.g. "6d", "3h" or "5m".</summary>
    private static string Ago(TimeSpan span) =>
        span.TotalDays >= 1 ? $"{(int)span.TotalDays}d"
        : span.TotalHours >= 1 ? $"{(int)span.TotalHours}h"
        : $"{Math.Max(1, (int)span.TotalMinutes)}m";

    /// <summary>When something last happened, e.g. "last thrown 5m ago". Empty when unknown.</summary>
    private static string Last(string verb, string at, DateTimeOffset now)
    {
        if (!DateTimeOffset.TryParse(at, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset when))
            return "";

        TimeSpan ago = now - when;
        return ago.TotalMinutes >= 1 ? $"last {verb} {Ago(ago)} ago" : $"last {verb} just now";
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
        string role = component.RoleClause.Length == 0 ? "" : " " + component.RoleClause;

        Dictionary<string, List<double>> latency = [];
        Dictionary<string, List<double>> failureRate = [];
        Dictionary<string, List<double>> dependency = [];
        Dictionary<string, List<double>> exception = [];

        if (findings.Any(f => f.Kind is FindingKind.Latency or FindingKind.FailureRate))
        {
            QueryTable series = await client.QueryAsync(component.AppId, $"""
                requests
                | where timestamp > ago({window}){role}
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

        if (findings.Any(f => f.Kind == FindingKind.Exception))
        {
            QueryTable series = await client.QueryAsync(component.AppId, $"""
                exceptions
                | where timestamp > ago({window}){role}
                | join kind=leftanti (
                      requests
                      | where timestamp > ago({window}) and success == false{role}
                      | project operation_Id
                  ) on operation_Id
                | summarize thrown = sum(itemCount) by problemId, bin(timestamp, {bucket}m)
                | order by timestamp asc
                """, ct);

            int problemAt = series.IndexOf("problemId");
            int thrownAt = series.IndexOf("thrown");

            foreach (IReadOnlyList<string> row in series.Rows)
                Append(exception, Text(row, problemAt), Number(row, thrownAt));
        }

        if (findings.Any(f => f.Kind == FindingKind.Dependency))
        {
            QueryTable series = await client.QueryAsync(component.AppId, $"""
                dependencies
                | where timestamp > ago({window}) and success == false{role}
                | summarize failed = sum(itemCount)
                          by subject = trim(" ", strcat(type, " ", target)), bin(timestamp, {bucket}m)
                | order by timestamp asc
                """, ct);

            int subjectAt = series.IndexOf("subject");
            int failedAt = series.IndexOf("failed");

            foreach (IReadOnlyList<string> row in series.Rows)
                Append(dependency, Text(row, subjectAt), Number(row, failedAt));
        }

        foreach (ServiceFinding finding in findings)
        {
            List<double>? points = finding.Kind switch
            {
                FindingKind.Latency => Lookup(latency, finding.Subject),
                FindingKind.FailureRate => Lookup(failureRate, finding.Subject),
                FindingKind.Dependency => Lookup(dependency, finding.Subject),
                FindingKind.Exception => Lookup(exception, finding.Subject),
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

    /// <summary>
    /// Whether the failure is a lapsed sign-in. Worth telling apart: it is the one cause the user
    /// can clear themselves, and here it is a daily event rather than an exception.
    /// </summary>
    private static bool NeedsSignIn(Exception ex) =>
        ex is AuthenticationRequiredException or AuthenticationFailedException
        || ex.Message.Contains("AADSTS", StringComparison.Ordinal);

    private static string Describe(Exception ex) =>
        NeedsSignIn(ex) ? "the Azure sign-in has expired" : $"{ex.GetType().Name}: {ex.Message}";

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
