using System;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Huginn.Services.Agent;
using Xunit;

namespace Huginn.Tests;

public sealed class McpHttpHostTests : IDisposable
{
    private const string Token = "test-token";

    private const string Initialize =
        """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"test","version":"1"}}}""";

    private readonly McpHttpHost _host;
    private readonly HttpClient _http = new();
    private readonly string _url;

    public McpHttpHostTests()
    {
        int port = FreePort();
        _url = $"http://localhost:{port}{McpEndpoint.Path}";
        _host = new McpHttpHost(port, Token, McpServer.Respond);
        _host.Start();
    }

    public void Dispose()
    {
        _host.Dispose();
        _http.Dispose();
    }

    private static int FreePort()
    {
        TcpListener probe = new(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private Task<HttpResponseMessage> Post(string body, string? token = Token, string? origin = null)
    {
        HttpRequestMessage request = new(HttpMethod.Post, _url)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Accept.ParseAdd("text/event-stream");
        if (token != null) request.Headers.Authorization = new("Bearer", token);
        if (origin != null) request.Headers.Add("Origin", origin);
        return _http.SendAsync(request, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Answers_a_request_as_json()
    {
        using HttpResponseMessage response = await Post(Initialize);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        JsonNode? answer = JsonNode.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal(1, answer?["id"]?.GetValue<int>());
        Assert.Equal("huginn", answer?["result"]?["serverInfo"]?["name"]?.GetValue<string>());
    }

    [Fact]
    public async Task Accepts_a_notification_without_a_body()
    {
        using HttpResponseMessage response = await Post("""{"jsonrpc":"2.0","method":"notifications/initialized"}""");

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal("", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Refuses_a_request_without_the_token()
    {
        using HttpResponseMessage response = await Post(Initialize, token: null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Refuses_a_request_with_the_wrong_token()
    {
        using HttpResponseMessage response = await Post(Initialize, token: "not-it");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Refuses_a_web_page_from_elsewhere()
    {
        using HttpResponseMessage response = await Post(Initialize, origin: "https://example.com");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Offers_no_event_stream()
    {
        HttpRequestMessage request = new(HttpMethod.Get, _url);
        request.Headers.Authorization = new("Bearer", Token);
        using HttpResponseMessage response = await _http.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
    }

    [Fact]
    public async Task Answers_only_at_its_path()
    {
        HttpRequestMessage request = new(HttpMethod.Post, _url.Replace(McpEndpoint.Path, "/other"))
        {
            Content = new StringContent(Initialize, Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new("Bearer", Token);
        using HttpResponseMessage response = await _http.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
