using System.Collections.Generic;
using System.Linq;
using Huginn.Models;
using Huginn.Services;
using Xunit;

namespace Huginn.Tests;

public sealed class BuildSupersessionTests
{
    private const int Pipeline = 1;
    private const string Main = "refs/heads/main";
    private const string PrOne = "refs/pull/1/merge";
    private const string PrTwo = "refs/pull/2/merge";

    private static BuildItem Failure(int id, string branch = Main) => new()
    {
        Id = id,
        DefinitionId = Pipeline,
        SourceBranch = branch,
        Result = BuildResult.Failed,
    };

    private static BuildRun Completed(int id, BuildResult result, string branch = Main) =>
        new(id, Pipeline, branch, "completed", result, $"run/{id}");

    private static BuildRun Running(int id, string branch = Main) =>
        new(id, Pipeline, branch, "inProgress", BuildResult.None, $"run/{id}");

    [Fact]
    public void A_failure_fixed_by_a_later_success_is_gone_while_another_run_is_under_way()
    {
        var shown = BuildSupersession.Apply(
            [Failure(10)],
            [Completed(10, BuildResult.Failed), Completed(11, BuildResult.Succeeded), Running(12)]);

        Assert.Empty(shown);
    }

    [Fact]
    public void A_failure_fixed_on_its_own_branch_is_gone_while_another_branch_runs()
    {
        var shown = BuildSupersession.Apply(
            [Failure(10, PrOne)],
            [Completed(10, BuildResult.Failed, PrOne), Completed(11, BuildResult.Succeeded, PrOne), Running(12, PrTwo)]);

        Assert.Empty(shown);
    }

    [Fact]
    public void Another_branch_running_is_not_a_retry()
    {
        var shown = BuildSupersession.Apply(
            [Failure(10, PrOne)],
            [Completed(10, BuildResult.Failed, PrOne), Running(11, PrTwo)]);

        var failure = Assert.Single(shown);
        Assert.False(failure.RetryInProgress);
    }

    [Fact]
    public void Another_branch_passing_does_not_hide_a_failure()
    {
        var shown = BuildSupersession.Apply(
            [Failure(10, PrOne)],
            [Completed(10, BuildResult.Failed, PrOne), Completed(11, BuildResult.Succeeded, PrTwo)]);

        var failure = Assert.Single(shown);
        Assert.False(failure.RetryInProgress);
    }

    [Fact]
    public void A_run_after_the_failure_is_a_retry()
    {
        var shown = BuildSupersession.Apply(
            [Failure(10)],
            [Completed(10, BuildResult.Failed), Running(11)]);

        var failure = Assert.Single(shown);
        Assert.True(failure.RetryInProgress);
        Assert.Equal("run/11", failure.RetryBuildUrl);
    }

    [Fact]
    public void A_run_after_a_later_failure_on_the_branch_is_a_retry()
    {
        var shown = BuildSupersession.Apply(
            [Failure(10)],
            [Completed(10, BuildResult.Failed), Completed(11, BuildResult.Failed), Running(12)]);

        var failure = Assert.Single(shown);
        Assert.True(failure.RetryInProgress);
        Assert.Equal("run/12", failure.RetryBuildUrl);
    }

    [Fact]
    public void A_run_after_a_canceled_build_is_not_a_retry()
    {
        var shown = BuildSupersession.Apply(
            [Failure(10)],
            [Completed(10, BuildResult.Failed), Completed(11, BuildResult.Canceled), Running(12)]);

        var failure = Assert.Single(shown);
        Assert.False(failure.RetryInProgress);
    }

    [Fact]
    public void A_failure_with_nothing_known_after_it_stays_failed()
    {
        var shown = BuildSupersession.Apply([Failure(10)], []);

        var failure = Assert.Single(shown);
        Assert.False(failure.RetryInProgress);
    }

    [Fact]
    public void A_newer_failure_on_the_same_branch_replaces_the_older_one()
    {
        var shown = BuildSupersession.Apply(
            [Failure(11), Failure(10)],
            [Completed(10, BuildResult.Failed), Completed(11, BuildResult.Failed)]);

        Assert.Equal([11], shown.Select(b => b.Id));
    }

    [Fact]
    public void One_card_per_branch_keeps_each_failing_branch_of_a_pipeline()
    {
        var cards = BuildSupersession.OnePerBranch([Failure(11, PrTwo), Failure(10, PrOne), Failure(9, PrOne)]);

        Assert.Equal([11, 10], cards.Select(b => b.Id));
    }
}
