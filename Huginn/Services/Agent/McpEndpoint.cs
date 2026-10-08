using System.Text;

namespace Huginn.Services.Agent;

/// <summary>Where the running app answers MCP over HTTP.</summary>
public static class McpEndpoint
{
    public const string Path = "/mcp";

    /// <summary>
    /// Outside the range Windows and macOS hand out for outgoing connections, so nothing else on
    /// the machine is given it by chance.
    /// </summary>
    private const int BasePort = 38457;

    /// <summary>
    /// Fixed, so a registration stays valid across restarts. A named profile gets a port of its
    /// own, so a development build can run beside the installed one.
    /// </summary>
    public static int Port => PortFor(AppSettings.Profile);

    public static string Url => $"http://localhost:{Port}{Path}";

    public static int PortFor(string profile) =>
        profile.Length == 0 ? BasePort : BasePort + 1 + (int)(Fnv1a(profile) % 500);

    /// <summary>A hash that is the same on every run, which <see cref="string.GetHashCode()"/> is not.</summary>
    private static uint Fnv1a(string text)
    {
        uint hash = 2166136261;
        foreach (byte b in Encoding.UTF8.GetBytes(text))
        {
            hash ^= b;
            hash *= 16777619;
        }
        return hash;
    }
}
