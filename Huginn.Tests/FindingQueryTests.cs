using System.Linq;
using Huginn.Models;
using Xunit;

namespace Huginn.Tests;

public sealed class FindingQueryTests
{
    private static ServiceFinding FailingRoute() => new()
    {
        Kind = FindingKind.FailureRate,
        Subject = "GET /api/orders/load",
        WindowMinutes = 720,
    };

    [Fact]
    public void A_failing_route_opens_on_its_failed_requests_whether_or_not_anything_was_thrown()
    {
        string query = FindingQuery.Portal(FailingRoute());

        Assert.StartsWith("requests", query.TrimStart());
        Assert.Contains("name == \"GET /api/orders/load\"", query);
        Assert.Contains("success == false", query);
        Assert.Contains("kind=leftouter", query);
    }

    [Fact]
    public void Each_recorded_request_says_how_many_calls_it_stands_for_so_the_rows_add_up_to_the_card()
    {
        Assert.Contains("calls = itemCount", FindingQuery.Portal(FailingRoute()));
    }

    [Fact]
    public void An_agent_asking_about_a_failing_route_gets_its_failures_even_when_nothing_was_thrown()
    {
        string query = FindingQuery.Explanations(FailingRoute()).Last().Kql;

        Assert.Contains("kind=leftouter", query);
        Assert.DoesNotContain("kind=inner", query);
    }
}
