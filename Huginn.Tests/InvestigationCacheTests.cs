using System;
using System.Threading.Tasks;
using Huginn.Services.Agent;
using Xunit;

namespace Huginn.Tests;

public sealed class InvestigationCacheTests
{
    private DateTimeOffset _now = new(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);

    private InvestigationCache Cache() => new(TimeSpan.FromMinutes(10), () => _now);

    [Fact]
    public async Task Agents_asking_about_one_finding_while_it_runs_share_the_run()
    {
        InvestigationCache cache = Cache();
        TaskCompletionSource<string> running = new();
        int runs = 0;

        Task<string> first = cache.AnswerAsync("a", () => { runs++; return running.Task; });
        Task<string> second = cache.AnswerAsync("a", () => { runs++; return Task.FromResult("again"); });
        running.SetResult("answer");

        Assert.Equal("answer", await first);
        Assert.Equal("answer", await second);
        Assert.Equal(1, runs);
    }

    [Fact]
    public async Task An_answer_is_handed_out_again_until_it_is_ten_minutes_old()
    {
        InvestigationCache cache = Cache();
        await cache.AnswerAsync("a", () => Task.FromResult("first"));

        _now = _now.AddMinutes(9);
        Assert.Equal("first", await cache.AnswerAsync("a", () => Task.FromResult("second")));

        _now = _now.AddMinutes(1);
        Assert.Equal("second", await cache.AnswerAsync("a", () => Task.FromResult("second")));
    }

    [Fact]
    public async Task A_run_that_failed_is_tried_again_rather_than_handed_out()
    {
        InvestigationCache cache = Cache();
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => cache.AnswerAsync("a", () => Task.FromException<string>(new InvalidOperationException())));

        Assert.Equal("worked", await cache.AnswerAsync("a", () => Task.FromResult("worked")));
    }

    [Fact]
    public async Task Each_finding_has_an_answer_of_its_own()
    {
        InvestigationCache cache = Cache();
        await cache.AnswerAsync("a", () => Task.FromResult("about a"));

        Assert.Equal("about b", await cache.AnswerAsync("b", () => Task.FromResult("about b")));
    }

    [Fact]
    public async Task Findings_differing_only_in_case_have_answers_of_their_own()
    {
        InvestigationCache cache = Cache();
        await cache.AnswerAsync("app|Dependency|HTTP Foo.com", () => Task.FromResult("upper"));

        Assert.Equal("lower",
            await cache.AnswerAsync("app|Dependency|HTTP foo.com", () => Task.FromResult("lower")));
    }
}
