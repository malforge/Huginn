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

    /// <summary>Reach below this never raises an issue on its own, however quiet the day is.</summary>
    private const int MinUsersToRaise = 5;

    /// <summary>
    /// Share of everyone hit in the window that makes a single issue worth raising by itself.
    /// A share rather than a fixed count so the bar rises with the day: a busy morning raises
    /// only what dominates it, and a quiet one raises nothing rather than promoting a stray crash.
    /// </summary>
    private const double ImpactShare = 0.05;

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
                if (settings.SentryBaselineTaken) escalations.Add(issue);
            }

            if (settings.MutedSentryIssues.TryGetValue(issue.Id, out var muted))
                issue.MutedAtUserCount = muted.UserCount;

        }

        HashSet<string> known = [.. settings.KnownSentryIssues];

        if (settings.SentryBaselineTaken)
        {
            foreach (SentryIssueItem issue in issues)
            {
                if (!known.Contains(issue.Id))
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
                if (issue.IsRegression) _knownRegressionIds.Add(issue.Id);
        }

        settings.SetSentryBaseline(issues.Select(i => i.Id));

        // Anything raised stays raised until the user dismisses it, so the flag is persisted
        // rather than held for the lifetime of the process.
        foreach (SentryIssueItem issue in newIssues) settings.FlagSentryIssue(issue.Id, "new");
        foreach (SentryIssueItem issue in regressions) settings.FlagSentryIssue(issue.Id, "regressed");
        foreach (SentryIssueItem issue in escalations) settings.FlagSentryIssue(issue.Id, "worse");

        settings.PruneSentryFlags([.. issues.Select(i => i.Id)]);

        int threshold = ImpactThreshold(issues, settings);

        foreach (SentryIssueItem issue in issues)
        {
            // Reach is current state, so unlike the change-driven reasons it is recomputed every
            // poll rather than pinned until dismissed. Pinning would let the list accrete issues
            // that went quiet weeks ago. Muting is how the user silences one of these.
            issue.IsWidespread = issue.UserCount >= threshold
                                 && !settings.IsSentryIssueMuted(issue.Id);

            issue.ApplyRaise(
                settings.IsSentryIssueFlagged(issue.Id),
                settings.GetSentryFlagReason(issue.Id));
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

    /// <summary>
    /// How many users an issue must reach before it is raised on reach alone. Muted issues are
    /// left out of the total, so silencing the loudest crash does not drag the bar down with it.
    /// </summary>
    private static int ImpactThreshold(List<SentryIssueItem> issues, AppSettings settings)
    {
        int totalUsers = issues
            .Where(i => !settings.IsSentryIssueMuted(i.Id))
            .Sum(i => i.UserCount);

        return Math.Max(MinUsersToRaise, (int)Math.Ceiling(totalUsers * ImpactShare));
    }

    /// <summary>Pins an outstanding alert above anything that is merely large.</summary>
    private static int Rank(SentryIssueItem issue) => issue.IsFlagged ? 1 : 0;

    public void Reset()
    {
        _knownRegressionIds.Clear();
        _releases.Clear();
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
