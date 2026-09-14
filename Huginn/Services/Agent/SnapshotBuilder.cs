using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Huginn.Models;

namespace Huginn.Services.Agent;

/// <summary>Flattens what the dashboard is showing into the shape an agent reads.</summary>
public static class SnapshotBuilder
{
    /// <summary>One queue of pull requests, named by why they are in it.</summary>
    public record struct PrQueue(string Name, IEnumerable<PullRequestItem> Items);

    /// <summary>One group of builds, named by what is happening to them.</summary>
    public record struct BuildQueue(string Status, IEnumerable<BuildItem> Items);

    public static AgentSnapshot Build(
        IEnumerable<AgentSource> sources,
        IEnumerable<PrQueue> prQueues,
        IEnumerable<BuildQueue> buildQueues,
        IEnumerable<SentryIssueItem> crashes,
        IEnumerable<ServiceFinding> findings,
        string webBaseUrl) => new()
        {
            GeneratedAt = DateTimeOffset.UtcNow,
            HuginnVersion = Assembly.GetEntryAssembly()?
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
                .InformationalVersion?.Split('+')[0] ?? "",
            Sources = [.. sources],
            PullRequests =
            [
                .. prQueues.SelectMany(q => q.Items.Select(pr => new AgentPullRequest
                {
                    Id = pr.PullRequestId,
                    Title = pr.Title,
                    Repository = pr.RepositoryName,
                    Author = pr.CreatedByName,
                    Age = pr.Age,
                    Queue = q.Name,
                    IsDraft = pr.IsDraft,
                    Votes = pr.VoteSummary,
                    Pipeline = pr.PipelineLabel,
                    Url = pr.DevOpsUrl(webBaseUrl),
                })),
            ],
            Builds =
            [
                .. buildQueues.SelectMany(q => q.Items.Select(b => new AgentBuild
                {
                    Definition = b.DefinitionName,
                    Branch = b.BranchShortName,
                    RequestedBy = b.RequestedBy,
                    Age = b.Age,
                    Status = q.Status,
                    Url = b.WebUrl,
                })),
            ],
            Crashes =
            [
                .. crashes.Select(c => new AgentCrash
                {
                    ShortId = c.ShortId,
                    Title = c.Title,
                    Culprit = c.Culprit,
                    Project = c.ProjectSlug,
                    Level = c.Level,
                    Events = c.EventCount,
                    Users = c.UserCount,
                    FirstSeen = c.FirstSeen,
                    LastSeen = c.LastSeen,
                    Releases = c.ReleaseSummary,
                    Raised = c.IsFlagged,
                    RaisedReason = c.FlagReason,
                    Muted = c.IsMuted,
                    Series = c.EventSeries,
                    SeriesBucketMinutes = c.SeriesBucketMinutes,
                    Trend = c.TrendVerdict,
                    Url = c.Permalink,
                }),
            ],
            Services =
            [
                .. findings.Select(f => new AgentFinding
                {
                    Kind = f.Kind.ToString(),
                    Resource = f.ResourceName,
                    Subject = f.Subject,
                    Detail = f.Detail,
                    Magnitude = f.Magnitude,
                    Severity = f.Severity,
                    Muted = f.IsMuted,
                    Series = f.SeriesValues,
                    Trend = f.TrendVerdict,
                    PortalUrl = f.PortalUrl,
                }),
            ],
        };
}
