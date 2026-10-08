using Huginn.Services.Agent;
using Xunit;

namespace Huginn.Tests;

/// <summary>Against what <c>claude mcp get</c> prints for each kind of entry.</summary>
public sealed class ClaudeCodeRegistrationTests
{
    private const string Stdio =
        """
        huginn:
          Scope: User config (available in all your projects)
          Status: ✔ Connected
          Type: stdio
          Command: C:\Users\you\AppData\Local\Huginn\current\Huginn.exe
          Args: --mcp
          Environment:

        To remove this server, run: claude mcp remove huginn -s user
        """;

    private static string Http(string url, string status) =>
        $"""
        huginn:
          Scope: User config (available in all your projects)
          Status: {status}
          Type: http
          URL: {url}
          Headers:
            Authorization: Bearer abc

        To remove this server, run: claude mcp remove huginn -s user
        """;

    [Fact]
    public void A_stdio_entry_is_the_old_way_even_when_connected()
    {
        var registration = ClaudeCodeRegistration.Parse(Stdio);

        Assert.True(registration.Registered);
        Assert.True(registration.IsStdio);
        Assert.True(registration.Connected);
        Assert.Equal(@"C:\Users\you\AppData\Local\Huginn\current\Huginn.exe", registration.Target);
        Assert.False(registration.PointsHere);
    }

    [Fact]
    public void A_stdio_entry_naming_this_executable_starts_this_copy()
    {
        var registration = ClaudeCodeRegistration.Parse(Stdio);

        Assert.True(registration.StartsCopyAt(@"C:\Users\you\AppData\Local\Huginn\current\Huginn.exe"));
        Assert.True(registration.StartsCopyAt(@"c:\users\YOU\appdata\local\huginn\current\huginn.exe"));
    }

    [Fact]
    public void A_stdio_entry_naming_another_executable_does_not_start_this_copy()
    {
        var registration = ClaudeCodeRegistration.Parse(Stdio);

        Assert.False(registration.StartsCopyAt(@"C:\Code\Huginn\bin\Debug\Huginn.exe"));
        Assert.False(registration.StartsCopyAt(null));
    }

    [Fact]
    public void An_http_entry_starts_no_copy()
    {
        var registration = ClaudeCodeRegistration.Parse(Http(McpEndpoint.Url, "✔ Connected"));

        Assert.False(registration.StartsCopyAt(McpEndpoint.Url));
    }

    [Fact]
    public void An_http_entry_at_this_address_that_connects_points_here()
    {
        var registration = ClaudeCodeRegistration.Parse(Http(McpEndpoint.Url, "✔ Connected"));

        Assert.False(registration.IsStdio);
        Assert.Equal(McpEndpoint.Url, registration.Target);
        Assert.True(registration.PointsHere);
    }

    [Fact]
    public void An_http_entry_here_that_cannot_connect_does_not_point_here()
    {
        var registration = ClaudeCodeRegistration.Parse(Http(McpEndpoint.Url, "! Needs authentication"));

        Assert.False(registration.Connected);
        Assert.False(registration.PointsHere);
    }

    [Fact]
    public void An_http_entry_at_another_address_does_not_point_here()
    {
        var registration = ClaudeCodeRegistration.Parse(Http("http://localhost:1/mcp", "✔ Connected"));

        Assert.Equal("http://localhost:1/mcp", registration.Target);
        Assert.False(registration.PointsHere);
    }
}
