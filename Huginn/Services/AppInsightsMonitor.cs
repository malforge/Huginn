using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
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
    /// <summary>Window each rule looks back over.</summary>
    private const string Window = "1h";

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
                findings.AddRange(await ExamineAsync(client, component, ct));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // One unreadable resource must not hide the others.
                Log.Error($"Could not examine {component.Name}: {ex.Message}");
            }
        }

        List<ServiceFinding> raised = [];

        foreach (ServiceFinding finding in findings)
        {
            if (settings.HasFindingOutgrownMute(finding.Id, finding.Magnitude, EscalationFactor))
            {
                settings.UnmuteFinding(finding.Id);
                if (_hasBaseline) raised.Add(finding);
            }

            finding.IsMuted = settings.IsFindingMuted(finding.Id);
            finding.MutedAtMagnitude = settings.GetFindingMutedAt(finding.Id);
        }

        if (_hasBaseline)
        {
            foreach (ServiceFinding finding in findings.Where(f => !f.IsMuted && _known.Add(f.Id)))
                raised.Add(finding);
        }
        else
        {
            foreach (ServiceFinding finding in findings) _known.Add(finding.Id);
            _hasBaseline = true;
        }

        foreach (ServiceFinding finding in raised)
            settings.FlagFinding(finding.Id, finding.KindLabel);

        settings.PruneFindingFlags([.. findings.Select(f => f.Id)]);

        foreach (ServiceFinding finding in findings)
        {
            finding.IsFlagged = settings.IsFindingFlagged(finding.Id);
            finding.FlagReason = settings.GetFindingFlagReason(finding.Id);
        }

        // A finding that has gone away stops being known, so it can be raised again if it returns.
        _known.IntersectWith(findings.Select(f => f.Id));

        findings.Sort((a, b) =>
        {
            int flagged = (b.IsFlagged ? 1 : 0).CompareTo(a.IsFlagged ? 1 : 0);
            return flagged != 0 ? flagged : b.Magnitude.CompareTo(a.Magnitude);
        });

        return new Snapshot(findings, raised);
    }

    private static async Task<List<ServiceFinding>> ExamineAsync(
        AppInsightsApiClient client, AppInsightsComponent component, CancellationToken ct)
    {
        List<ServiceFinding> findings = [];

        // Requests that 404 are excluded wholesale. On an internet-facing resource they are
        // overwhelmingly bots probing for /wp-admin and friends, which would otherwise drown out
        // every real signal; a 404 from our own client is a client bug rather than an outage.
        QueryTable routes = await client.QueryAsync(component.AppId, $"""
            requests
            | where timestamp > ago({Window})
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

        foreach (List<JsonElement> row in routes.Rows)
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
                    Subject = name,
                    Detail = $"p95 {p95 / 1000:N1}s over {total:N0} calls",
                    Magnitude = p95,
                });
            }
        }

        QueryTable dependencies = await client.QueryAsync(component.AppId, $"""
            dependencies
            | where timestamp > ago({Window}) and success == false
            | summarize failed = sum(itemCount) by type, target, resultCode
            | where failed >= {DependencyFailureThreshold}
            | order by failed desc
            | take 10
            """, ct);

        int typeAt = dependencies.IndexOf("type");
        int targetAt = dependencies.IndexOf("target");
        int codeAt = dependencies.IndexOf("resultCode");
        int depFailedAt = dependencies.IndexOf("failed");

        foreach (List<JsonElement> row in dependencies.Rows)
        {
            long failed = Number(row, depFailedAt);
            findings.Add(new ServiceFinding
            {
                Kind = FindingKind.Dependency,
                ResourceName = component.Name,
                AppId = component.AppId,
                Subject = $"{Text(row, typeAt)} {Text(row, targetAt)}".Trim(),
                Detail = $"{failed:N0} failures, code {Text(row, codeAt)}",
                Magnitude = failed,
            });
        }

        // Silence is the failure a threshold never catches, so it gets its own rule.
        if (traffic == 0)
        {
            findings.Add(new ServiceFinding
            {
                Kind = FindingKind.NoTraffic,
                ResourceName = component.Name,
                AppId = component.AppId,
                Subject = component.Name,
                Detail = $"no requests in the last {Window}",
                Magnitude = 1,
            });
        }

        return findings;
    }

    private static string Text(List<JsonElement> row, int index) =>
        index < 0 || index >= row.Count ? "" : row[index].ValueKind switch
        {
            JsonValueKind.String => row[index].GetString() ?? "",
            JsonValueKind.Null or JsonValueKind.Undefined => "",
            _ => row[index].ToString(),
        };

    private static long Number(List<JsonElement> row, int index)
    {
        if (index < 0 || index >= row.Count) return 0;
        JsonElement value = row[index];
        return value.ValueKind switch
        {
            JsonValueKind.Number => (long)value.GetDouble(),
            JsonValueKind.String when double.TryParse(
                value.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out double d) => (long)d,
            _ => 0,
        };
    }

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
