using System.Collections.Generic;
using System.Linq;
using Huginn.Models;

namespace Huginn.Services;

/// <summary>
/// Decides what a build failure means now, from the runs that came after it. A branch's runs
/// are its own: another branch running or passing on the same pipeline says nothing about
/// this one, so every comparison is within one pipeline and one branch.
/// </summary>
public static class BuildSupersession
{
    /// <summary>
    /// One card per failing pipeline and branch, showing where the branch stands now rather than
    /// the failure that drew attention to it. The card is the newest failed run there, whoever
    /// ran it; it is dropped when a later run passed, and marked as retrying when a run is under
    /// way and the run before it failed.
    /// </summary>
    public static List<BuildItem> Apply(IReadOnlyList<BuildItem> failures, IReadOnlyCollection<BuildItem> runs)
    {
        var result = new List<BuildItem>();
        foreach (var known in OnePerBranch(failures))
        {
            var onBranch = runs
                .Where(r => r.Id >= known.Id
                            && r.DefinitionId == known.DefinitionId
                            && r.SourceBranch == known.SourceBranch)
                .ToList();

            var card = onBranch
                .Where(r => r.IsCompleted && r.Result == BuildResult.Failed)
                .MaxBy(r => r.Id) ?? known;

            var after = onBranch.Where(r => r.Id > card.Id).ToList();
            if (after.Any(r => r.IsCompleted && r.Result == BuildResult.Succeeded))
                continue;

            // A run is a retry only when the run before it failed. One that follows a canceled
            // or partially successful run is just the next run.
            var retry = after.Where(r => r.IsPending).MaxBy(r => r.Id);
            if (retry is not null && !after.Any(r => r.IsCompleted))
            {
                card.RetryInProgress = true;
                card.RetryBuildUrl = retry.WebUrl;
            }

            result.Add(card);
        }
        return result;
    }

    /// <summary>The newest build per pipeline and branch, in the order they were given.</summary>
    public static List<BuildItem> OnePerBranch(IEnumerable<BuildItem> builds)
    {
        var list = builds.ToList();
        var newest = list
            .GroupBy(b => (b.DefinitionId, b.SourceBranch))
            .Select(g => g.MaxBy(b => b.Id)!)
            .ToHashSet();
        return list.Where(newest.Contains).ToList();
    }
}
