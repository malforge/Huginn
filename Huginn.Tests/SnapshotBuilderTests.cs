using System;
using Huginn.Models;
using Huginn.Services.Agent;
using Xunit;

namespace Huginn.Tests;

public sealed class SnapshotBuilderTests
{
    private static AgentBuild Only(string queue, BuildItem build)
    {
        var snapshot = SnapshotBuilder.Build([], [], [new(queue, [build])], [], [], [], [], "");
        return Assert.Single(snapshot.Builds);
    }

    [Fact]
    public void An_acknowledged_failure_that_is_being_retried_says_so()
    {
        var agent = Only("acknowledged", new BuildItem
        {
            Id = 10,
            WebUrl = "run/10",
            RetryInProgress = true,
            RetryBuildUrl = "run/11",
            IsAcknowledged = true,
        });

        Assert.Equal("acknowledged", agent.Status);
        Assert.True(agent.Retrying);
        Assert.Equal("run/11", agent.RetryUrl);
        Assert.Equal("run/10", agent.Url);
    }

    [Fact]
    public void A_failure_carries_when_it_finished()
    {
        var finished = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);

        var agent = Only("failed", new BuildItem { Id = 10, FinishTime = finished });

        Assert.Equal(finished, agent.FinishedAt);
        Assert.False(agent.Retrying);
        Assert.Equal("", agent.RetryUrl);
    }
}
