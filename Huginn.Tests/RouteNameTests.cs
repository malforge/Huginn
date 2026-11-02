using Huginn.Models;
using Xunit;

namespace Huginn.Tests;

public sealed class RouteNameTests
{
    [Theory]
    [InlineData("POST /v2/Customers", "post /v2/customers")]
    [InlineData("POST /v2/Customers/", "post /v2/customers")]
    [InlineData("GET /v2/Orders/{id}/Lines/", "get /v2/orders/{id}/lines")]
    [InlineData("GET /", "get /")]
    public void Spellings_of_one_route_share_a_key(string subject, string key)
    {
        Assert.Equal(key, RouteName.KeyOfTemplate(subject));
    }
}
