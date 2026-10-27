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

    /// <summary>
    /// How far before the last check an issue may have been first seen and still count as new.
    /// Sentry can list an issue a little after its first event, so one that was not yet listed
    /// when Huginn last looked must not be missed for having been first seen just before it.
    /// </summary>
    private static readonly TimeSpan NewSlack = TimeSpan.FromHours(1);

    /// <summary>
    /// Age past which an issue is no longer called new, however long the alert has gone unread.
    /// The listing window, so the label never contradicts the date on the card.
    /// </summary>
    private static readonly TimeSpan NewFor = TimeSpan.FromDays(14);

    /// <param name="IsComplete">
    /// False when there were more issues than one poll reads, so the list is not everything.
    /// </param>
    public record SentrySnapshot(
        List<SentryIssueItem> Issues,
        List<SentryIssueItem> NewIssues,
        List<SentryIssueItem> Regressions,
        List<SentryIssueItem> Escalations,
        bool IsComplete);

    public async Task<SentrySnapshot> PollAsync(
        SentryApiClient client, AppSettings settings, CancellationToken ct)
    {
        (List<SentryIssueItem> issues, bool complete) = await client.GetIssuesAsync(
            settings.SentryOrganization, settings.WatchedSentryProjects, ct);

        return Evaluate(issues, settings, DateTimeOffset.UtcNow, complete);
    }

    /// <summary>Decides what a poll's issues raise, and in what order they are listed.</summary>
    public SentrySnapshot Evaluate(
        List<SentryIssueItem> issues, AppSettings settings, DateTimeOffset now, bool isComplete = true)
    {
        List<SentryIssueItem> newIssues = [];
        List<SentryIssueItem> regressions = [];
        List<SentryIssueItem> escalations = [];

        // Null on the very first poll, and on the first one after the check time was kept, so
        // both take a baseline rather than announcing a backlog.
        DateTimeOffset? lastChecked = settings.SentryCheckedAt;

        foreach (SentryIssueItem issue in issues)
        {
            // A mute measured over another window cannot be compared with these counts, so it is
            // measured again at its current size rather than lifted for a change of units.
            if (settings.IsSentryMuteMeasuredElsewhere(issue.Id) && !issue.IsRegression)
                settings.MuteSentryIssue(issue.Id, issue.RecentUserCount, issue.RecentEventCount);

            // A muted issue that has returned or whose recent reach roughly doubled stops being
            // muted, so an unfixable crash goes quiet without going silent for good. Recent
            // rather than lifetime reach, which only ever grows and would double eventually
            // without anything getting worse.
            if (settings.HasOutgrownMute(
                    issue.Id, issue.RecentUserCount, issue.RecentEventCount, issue.IsRegression))
            {
                settings.UnmuteSentryIssue(issue.Id);
                if (lastChecked is not null) escalations.Add(issue);
            }

            if (settings.MutedSentryIssues.TryGetValue(issue.Id, out var muted))
                issue.MutedAtUserCount = muted.UserCount;

            // A crash someone has taken leaves the lists of what to pick up, the way a mute does,
            // and comes back the same way. Whoever has it, the user included.
            issue.IsSetAside = issue.IsAssigned;

            if (issue.IsSetAside && !settings.IsSentryIssueMuted(issue.Id)
                && settings.RecordAssigned(issue.Id, issue.RecentUserCount, issue.RecentEventCount)
                && lastChecked is not null)
            {
                escalations.Add(issue);
            }
        }

        if (isComplete)
            settings.PruneAssigned([.. issues.Where(i => i.IsSetAside).Select(i => i.Id)]);

        HashSet<string> known = [.. settings.KnownSentryIssues];
        HashSet<string> knownRegressions = [.. settings.KnownSentryRegressions];

        foreach (SentryIssueItem issue in issues)
        {
            if (lastChecked is null)
            {
                if (issue.IsRegression) knownRegressions.Add(issue.Id);
                continue;
            }

            // New means Sentry first saw it since Huginn last looked. Being absent from the last
            // poll is not enough: an issue that went quiet for a while and came back is still as
            // old as it was.
            if (!known.Contains(issue.Id) && issue.FirstSeen >= lastChecked.Value - NewSlack)
            {
                // A muted issue cannot also be new, so no mute check is needed here.
                newIssues.Add(issue);
            }
            else if (issue.IsRegression && knownRegressions.Add(issue.Id))
            {
                // Sentry has marked it as returned since it was last raised as such.
                regressions.Add(issue);
            }
        }

        // An issue that stops being a regression can regress again later. Only a complete list
        // can say it has stopped, though: one cut short is missing issues, not their state.
        if (isComplete)
            knownRegressions.IntersectWith(issues.Where(i => i.IsRegression).Select(i => i.Id));

        settings.SetSentryBaseline(issues.Select(i => i.Id), knownRegressions, now);

        // Anything raised stays raised until the user dismisses it, so the flag is persisted
        // rather than held for the lifetime of the process.
        foreach (SentryIssueItem issue in newIssues) settings.FlagSentryIssue(issue.Id, "new");
        foreach (SentryIssueItem issue in regressions) settings.FlagSentryIssue(issue.Id, "regressed");
        foreach (SentryIssueItem issue in escalations) settings.FlagSentryIssue(issue.Id, "worse");

        // The new label says how old an issue is, so it comes down once that stops being true,
        // read or not. The issue itself stays listed by its reach.
        foreach (SentryIssueItem issue in issues)
        {
            if (settings.GetSentryFlagReason(issue.Id) == "new" && now - issue.FirstSeen > NewFor)
                settings.DismissSentryIssue(issue.Id);
        }

        if (isComplete) settings.PruneSentryFlags([.. issues.Select(i => i.Id)]);

        // Only a change raises an issue. Size is a ranking, shown as the top of the list, so it
        // cannot be swamped by one huge issue setting a bar nothing else reaches.
        foreach (SentryIssueItem issue in issues)
            issue.ApplyRaise(settings.IsSentryIssueFlagged(issue.Id), settings.GetSentryFlagReason(issue.Id));

        List<SentryIssueItem> ranked = [.. Ranked(issues, allTime: false)];
        issues.Clear();
        issues.AddRange(ranked);

        // An issue worth alerting on is worth an up-to-date version, since that is the first
        // thing asked of the alert.
        foreach (SentryIssueItem issue in newIssues.Concat(regressions).Concat(escalations))
            _releases.Remove(issue.Id);

        return new SentrySnapshot(issues, newIssues, regressions, escalations, isComplete);
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

    /// <summary>Most people reached first, over the last fortnight or over all time.</summary>
    /// <remarks>
    /// The fortnight says what is hurting people now, and lets a crash that was mostly fixed long
    /// ago drop down. All time says how much a long-running crash has cost. The other count breaks
    /// a tie, then events.
    /// </remarks>
    public static IEnumerable<SentryIssueItem> Ranked(IEnumerable<SentryIssueItem> issues, bool allTime) =>
        allTime
            ? issues.OrderByDescending(i => i.UserCount)
                .ThenByDescending(i => i.RecentUserCount)
                .ThenByDescending(i => i.EventCount)
            : issues.OrderByDescending(i => i.RecentUserCount)
                .ThenByDescending(i => i.UserCount)
                .ThenByDescending(i => i.RecentEventCount);

    public void Reset()
    {
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
