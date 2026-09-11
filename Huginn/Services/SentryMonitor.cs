using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Huginn.Models;

namespace Huginn.Services;

/// <summary>
/// Monitors Sentry for unresolved issues, raising the ones that are new or have come back.
/// Uses baseline tracking so the first poll does not announce the whole existing backlog.
/// </summary>
public sealed class SentryMonitor
{
    private readonly HashSet<string> _knownIssueIds = [];
    private readonly HashSet<string> _knownRegressionIds = [];

    /// <summary>Cached releases per issue, with the time they were read.</summary>
    private readonly Dictionary<string, (DateTimeOffset Read, string First, string Last)> _releases = [];

    /// <summary>
    /// Ceiling on release lookups per poll. Each one is its own request, so a large backlog fills
    /// in over several polls rather than firing a burst at the API.
    /// </summary>
    private const int MaxReleaseLookupsPerPoll = 25;

    /// <summary>
    /// How long a release reading stays good. Keying this on the issue last-seen time instead
    /// would re-read the busiest issues on every single poll, which is both the most wasteful and
    /// the most rate-limited thing to do. The set of versions an issue appears in barely moves.
    /// </summary>
    private static readonly TimeSpan ReleaseCacheLifetime = TimeSpan.FromHours(1);
    private bool _hasBaseline;

    public record SentrySnapshot(
        List<SentryIssueItem> Issues,
        List<SentryIssueItem> NewIssues,
        List<SentryIssueItem> Regressions,
        List<SentryIssueItem> Escalations);

    public async Task<SentrySnapshot> PollAsync(
        SentryApiClient client, AppSettings settings, CancellationToken ct)
    {
        List<SentryIssueItem> issues = await client.GetIssuesAsync(
            settings.SentryOrganization, settings.WatchedSentryProjects, ct: ct);

        List<SentryIssueItem> newIssues = [];
        List<SentryIssueItem> regressions = [];
        List<SentryIssueItem> escalations = [];

        foreach (SentryIssueItem issue in issues)
        {
            // A muted issue that has returned or roughly doubled stops being muted, so an
            // unfixable crash goes quiet without going silent for good.
            if (settings.HasOutgrownMute(issue.Id, issue.UserCount, issue.EventCount, issue.IsRegression))
            {
                settings.UnmuteSentryIssue(issue.Id);
                if (_hasBaseline) escalations.Add(issue);
            }

            if (settings.MutedSentryIssues.TryGetValue(issue.Id, out var muted))
                issue.MutedAtUserCount = muted.UserCount;

        }

        if (_hasBaseline)
        {
            foreach (SentryIssueItem issue in issues)
            {
                if (_knownIssueIds.Add(issue.Id))
                {
                    // A muted issue cannot also be new, so no mute check is needed here.
                    newIssues.Add(issue);
                }
                else if (issue.IsRegression && _knownRegressionIds.Add(issue.Id))
                {
                    // Already known, but Sentry has since marked it as returned.
                    regressions.Add(issue);
                }
            }
        }
        else
        {
            foreach (SentryIssueItem issue in issues)
            {
                _knownIssueIds.Add(issue.Id);
                if (issue.IsRegression) _knownRegressionIds.Add(issue.Id);
            }
            _hasBaseline = true;
        }

        // Anything raised stays raised until the user dismisses it, so the flag is persisted
        // rather than held for the lifetime of the process.
        foreach (SentryIssueItem issue in newIssues) settings.FlagSentryIssue(issue.Id, "new");
        foreach (SentryIssueItem issue in regressions) settings.FlagSentryIssue(issue.Id, "regressed");
        foreach (SentryIssueItem issue in escalations) settings.FlagSentryIssue(issue.Id, "worse");

        settings.PruneSentryFlags([.. issues.Select(i => i.Id)]);

        foreach (SentryIssueItem issue in issues)
        {
            issue.IsFlagged = settings.IsSentryIssueFlagged(issue.Id);
            issue.FlagReason = settings.GetSentryFlagReason(issue.Id);
        }

        // Outstanding alerts first, then whatever affects most people.
        issues.Sort((a, b) =>
        {
            int rank = Rank(b).CompareTo(Rank(a));
            if (rank != 0) return rank;
            int users = b.UserCount.CompareTo(a.UserCount);
            return users != 0 ? users : b.EventCount.CompareTo(a.EventCount);
        });

        // An issue that stops being a regression can regress again later.
        _knownRegressionIds.IntersectWith(issues.Where(i => i.IsRegression).Select(i => i.Id));

        // An issue worth alerting on is worth an up-to-date version, since that is the first
        // thing asked of the alert.
        foreach (SentryIssueItem issue in newIssues.Concat(regressions).Concat(escalations))
            _releases.Remove(issue.Id);

        return new SentrySnapshot(issues, newIssues, regressions, escalations);
    }

    /// <summary>
    /// Fills in the app versions each issue has been seen in, one request per issue against a
    /// throttled endpoint. Kept out of the poll itself so the list can be shown first.
    /// </summary>
    public async Task EnrichReleasesAsync(
        SentryApiClient client, string organizationSlug, List<SentryIssueItem> issues, CancellationToken ct)
    {
        int looked = 0;

        DateTimeOffset now = DateTimeOffset.UtcNow;

        foreach (SentryIssueItem issue in issues)
        {
            if (_releases.TryGetValue(issue.Id, out var cached)
                && now - cached.Read < ReleaseCacheLifetime)
            {
                issue.FirstReleaseVersion = cached.First;
                issue.LastReleaseVersion = cached.Last;
                continue;
            }

            if (looked >= MaxReleaseLookupsPerPoll) continue;
            looked++;

            try
            {
                (string first, string last) = await client.GetIssueReleasesAsync(
                    organizationSlug, issue.Id, ct);
                issue.FirstReleaseVersion = first;
                issue.LastReleaseVersion = last;
                _releases[issue.Id] = (now, first, last);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // A missing version is worth carrying on without; the issue itself still matters.
                Log.Error($"Could not read releases for Sentry issue {issue.Id}: {ex.Message}");
            }
        }
    }

    /// <summary>Pins an outstanding alert above anything that is merely large.</summary>
    private static int Rank(SentryIssueItem issue) => issue.IsFlagged ? 1 : 0;

    public void Reset()
    {
        _knownIssueIds.Clear();
        _knownRegressionIds.Clear();
        _releases.Clear();
        _hasBaseline = false;
    }

    public StatusParts GetStatusParts(SentrySnapshot snapshot)
    {
        StatusParts parts = new();
        int count = snapshot.Issues.Count;
        if (count > 0)
            parts.Add($"{count} Sentry issue{(count != 1 ? "s" : "")}");
        return parts;
    }
}
