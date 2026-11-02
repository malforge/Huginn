using Huginn.Models;
using Xunit;

namespace Huginn.Tests;

public sealed class GlobTests
{
    [Theory]
    [InlineData("GET /api/orders", "GET /api/orders/")]
    [InlineData("GET /api/orders/", "GET /api/Orders")]
    [InlineData("GET /api/*", "GET /api/orders/")]
    public void A_rule_keeps_matching_a_route_whichever_spelling_of_it_is_shown(string pattern, string subject)
    {
        Assert.True(Glob.Matches(pattern, subject));
    }

    [Theory]
    [InlineData("GET /api/orders", "GET /api/orders/lines")]
    [InlineData("GET /api/orders/", "GET /api/order")]
    public void Folding_the_slash_does_not_widen_a_rule_to_other_routes(string pattern, string subject)
    {
        Assert.False(Glob.Matches(pattern, subject));
    }
}
