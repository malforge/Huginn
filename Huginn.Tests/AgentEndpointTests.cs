using System;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Huginn.Services.Agent;
using Xunit;

namespace Huginn.Tests;

public sealed class AgentEndpointTests
{
    private static int FreePort()
    {
        TcpListener probe = new(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private static async Task<bool> AnswersAsync(int port)
    {
        using HttpClient http = new() { Timeout = TimeSpan.FromSeconds(2) };
        try
        {
            using HttpResponseMessage response = await http.GetAsync(
                $"http://localhost:{port}{McpEndpoint.Path}", TestContext.Current.CancellationToken);
            return true;
        }
        catch (HttpRequestException)
        {
            return false;
        }
    }

    [Fact]
    public async Task Takes_over_the_port_once_another_copy_lets_go_of_it()
    {
        int port = FreePort();
        McpHttpHost other = new(port, "other", McpServer.Respond);
        other.Start();

        using AgentEndpoint endpoint = new(port, "token", McpServer.Respond, TimeSpan.FromMilliseconds(100));
        using ManualResetEventSlim changed = new();
        endpoint.Changed += changed.Set;
        endpoint.Start();

        Assert.NotNull(endpoint.Error);

        other.Dispose();

        Assert.True(changed.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        Assert.Null(endpoint.Error);
        Assert.True(await AnswersAsync(port));
    }

    [Fact]
    public async Task Listens_at_once_when_the_port_is_free()
    {
        int port = FreePort();
        using AgentEndpoint endpoint = new(port, "token", McpServer.Respond, TimeSpan.FromMinutes(1));
        endpoint.Start();

        Assert.Null(endpoint.Error);
        Assert.True(await AnswersAsync(port));
    }

    [Fact]
    public async Task Leaves_the_port_alone_once_disposed()
    {
        int port = FreePort();
        McpHttpHost other = new(port, "other", McpServer.Respond);
        other.Start();

        AgentEndpoint endpoint = new(port, "token", McpServer.Respond, TimeSpan.FromMilliseconds(100));
        endpoint.Start();
        endpoint.Dispose();
        other.Dispose();

        await Task.Delay(500, TestContext.Current.CancellationToken);

        using McpHttpHost next = new(port, "next", McpServer.Respond);
        next.Start();
    }
}
