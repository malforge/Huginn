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
    /// Drops failures a later success on their branch has fixed, keeps only the newest failure
    /// per branch, and marks a failure as retrying when a run is under way and the last
    /// completed run on its branch failed.
    /// </summary>
    public static List<BuildItem> Apply(IReadOnlyList<BuildItem> failures, IReadOnlyCollection<BuildRun> runs)
    {
        var current = OnePerBranch(failures).ToHashSet();
        var result = new List<BuildItem>();
        foreach (var build in failures.Where(current.Contains))
        {
            var later = runs
                .Where(r => r.Id > build.Id
                            && r.DefinitionId == build.DefinitionId
                            && r.SourceBranch == build.SourceBranch)
                .ToList();

            var lastCompleted = later.Where(r => r.IsCompleted).MaxBy(r => r.Id);
            if (lastCompleted?.Result == BuildResult.Succeeded)
                continue;

            // A run is a retry only when the run before it failed. One that follows a canceled
            // or partially successful run is just the next run.
            var lastResult = lastCompleted?.Result ?? build.Result;
            var retry = later.Where(r => r.IsPending).MaxBy(r => r.Id);
            if (retry is not null && lastResult == BuildResult.Failed)
            {
                build.RetryInProgress = true;
                build.RetryBuildUrl = retry.WebUrl;
            }

            result.Add(build);
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
