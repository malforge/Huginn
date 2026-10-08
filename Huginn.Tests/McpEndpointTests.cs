using Huginn.Services.Agent;
using Xunit;

namespace Huginn.Tests;

public sealed class McpEndpointTests
{
    [Fact]
    public void A_named_profile_gets_a_port_of_its_own_that_stays_the_same()
    {
        int installed = McpEndpoint.PortFor("");
        int dev = McpEndpoint.PortFor("dev");

        Assert.NotEqual(installed, dev);
        Assert.Equal(dev, McpEndpoint.PortFor("dev"));
    }
}
