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

    private static BuildItem Failure(int id, string branch = Main) =>
        Completed(id, BuildResult.Failed, branch);

    private static BuildItem Completed(int id, BuildResult result, string branch = Main) => new()
    {
        Id = id,
        DefinitionId = Pipeline,
        SourceBranch = branch,
        Status = "completed",
        Result = result,
        WebUrl = $"run/{id}",
    };

    private static BuildItem Running(int id, string branch = Main) => new()
    {
        Id = id,
        DefinitionId = Pipeline,
        SourceBranch = branch,
        Status = "inProgress",
        WebUrl = $"run/{id}",
    };

    [Fact]
    public void A_failure_fixed_by_a_later_success_is_gone_while_another_run_is_under_way()
    {
        var shown = BuildSupersession.Apply(
            [Failure(10)],
            [Failure(10), Completed(11, BuildResult.Succeeded), Running(12)]);

        Assert.Empty(shown);
    }

    [Fact]
    public void A_failure_fixed_on_its_own_branch_is_gone_while_another_branch_runs()
    {
        var shown = BuildSupersession.Apply(
            [Failure(10, PrOne)],
            [Failure(10, PrOne), Completed(11, BuildResult.Succeeded, PrOne), Running(12, PrTwo)]);

        Assert.Empty(shown);
    }

    [Fact]
    public void A_failure_after_a_fix_takes_the_card_whoever_ran_it()
    {
        var shown = BuildSupersession.Apply(
            [Failure(10)],
            [Failure(10), Completed(11, BuildResult.Succeeded), Failure(12), Failure(13)]);

        var card = Assert.Single(shown);
        Assert.Equal(13, card.Id);
        Assert.False(card.RetryInProgress);
    }

    [Fact]
    public void A_newer_failure_on_the_branch_takes_the_card()
    {
        var shown = BuildSupersession.Apply(
            [Failure(10)],
            [Failure(10), Failure(11)]);

        Assert.Equal([11], shown.Select(b => b.Id));
    }

    [Fact]
    public void Another_branch_running_is_not_a_retry()
    {
        var shown = BuildSupersession.Apply(
            [Failure(10, PrOne)],
            [Failure(10, PrOne), Running(11, PrTwo)]);

        var card = Assert.Single(shown);
        Assert.False(card.RetryInProgress);
    }

    [Fact]
    public void Another_branch_passing_does_not_hide_a_failure()
    {
        var shown = BuildSupersession.Apply(
            [Failure(10, PrOne)],
            [Failure(10, PrOne), Completed(11, BuildResult.Succeeded, PrTwo)]);

        var card = Assert.Single(shown);
        Assert.Equal(10, card.Id);
        Assert.False(card.RetryInProgress);
    }

    [Fact]
    public void A_run_after_the_failure_is_a_retry()
    {
        var shown = BuildSupersession.Apply(
            [Failure(10)],
            [Failure(10), Running(11)]);

        var card = Assert.Single(shown);
        Assert.True(card.RetryInProgress);
        Assert.Equal("run/11", card.RetryBuildUrl);
    }

    [Fact]
    public void A_run_after_a_later_failure_is_a_retry_of_that_failure()
    {
        var shown = BuildSupersession.Apply(
            [Failure(10)],
            [Failure(10), Failure(11), Running(12)]);

        var card = Assert.Single(shown);
        Assert.Equal(11, card.Id);
        Assert.True(card.RetryInProgress);
        Assert.Equal("run/12", card.RetryBuildUrl);
    }

    [Fact]
    public void A_run_after_a_canceled_build_is_not_a_retry()
    {
        var shown = BuildSupersession.Apply(
            [Failure(10)],
            [Failure(10), Completed(11, BuildResult.Canceled), Running(12)]);

        var card = Assert.Single(shown);
        Assert.Equal(10, card.Id);
        Assert.False(card.RetryInProgress);
    }

    [Fact]
    public void A_failure_with_nothing_known_after_it_stays_failed()
    {
        var shown = BuildSupersession.Apply([Failure(10)], []);

        var card = Assert.Single(shown);
        Assert.Equal(10, card.Id);
        Assert.False(card.RetryInProgress);
    }

    [Fact]
    public void Two_failures_of_yours_on_one_branch_make_one_card()
    {
        var shown = BuildSupersession.Apply(
            [Failure(11), Failure(10)],
            [Failure(10), Failure(11)]);

        Assert.Equal([11], shown.Select(b => b.Id));
    }

    [Fact]
    public void One_card_per_branch_keeps_each_failing_branch_of_a_pipeline()
    {
        var cards = BuildSupersession.OnePerBranch([Failure(11, PrTwo), Failure(10, PrOne), Failure(9, PrOne)]);

        Assert.Equal([11, 10], cards.Select(b => b.Id));
    }
}
