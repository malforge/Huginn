using System.Collections.Generic;

namespace Huginn.Models;

/// <summary>
/// What one Sentry poll found worth telling the user about, carried together so the whole poll
/// can be announced once rather than once per issue.
/// </summary>
public sealed record SentryAlerts(
    List<SentryIssueItem> New,
    List<SentryIssueItem> Regressions,
    List<SentryIssueItem> Escalations)
{
    public int Total => New.Count + Regressions.Count + Escalations.Count;

    /// <summary>Every alert in one sequence, for when only the count and the worst one matter.</summary>
    public IEnumerable<SentryIssueItem> All
    {
        get
        {
            foreach (SentryIssueItem issue in New) yield return issue;
            foreach (SentryIssueItem issue in Regressions) yield return issue;
            foreach (SentryIssueItem issue in Escalations) yield return issue;
        }
    }
}
