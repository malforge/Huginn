using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace Huginn.Services.Agent;

/// <summary>
/// Registers this build as an MCP server with Claude Code, so an agent can ask Huginn what is
/// broken. Writes the user's Claude configuration, so it only ever runs when they ask it to.
/// </summary>
/// <remarks>
/// Claude Code is pointed at the running app over HTTP rather than told to start Huginn itself
/// over stdio. An update stops every copy of Huginn, and Claude Code reconnects to an HTTP server
/// that comes back but never restarts a stdio one, so only the HTTP registration survives it.
/// </remarks>
public static class ClaudeCodeRegistration
{
    /// <summary>The name the server is registered under.</summary>
    public const string ServerName = "huginn";

    /// <summary>
    /// Registered for the user rather than the folder Huginn happened to launch from. The default
    /// scope is the current project, which would leave it missing from every other one.
    /// </summary>
    private const string Scope = "user";

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    /// <summary>Where this copy of Huginn is running from, which is what a client must launch.</summary>
    public static string ExecutablePath => Environment.ProcessPath ?? "";

    /// <summary>The flag that makes Huginn serve MCP on stdio instead of opening a window.</summary>
    public const string ServeArgument = "--mcp";

    /// <summary>The header that admits a caller, carrying the token this copy hands out.</summary>
    public static string AuthorizationHeader(string token) => $"Authorization: Bearer {token}";

    /// <summary>The Claude Code one-liner, for someone who would rather run it themselves.</summary>
    public static string CliCommand(string token) =>
        $"claude mcp add --transport http -s {Scope} {ServerName} {McpEndpoint.Url} "
        + $"--header \"{AuthorizationHeader(token)}\"";

    /// <summary>
    /// The stdio form as configuration, for a client that is not Claude Code. Nearly every MCP
    /// client takes this shape, so it is more use than instructions for one of them, though such
    /// a client has to be reconnected after an update.
    /// </summary>
    public static string ConfigJson =>
        $$"""
        {
          "mcpServers": {
            "{{ServerName}}": {
              "command": "{{ExecutablePath.Replace("\\", "\\\\")}}",
              "args": ["{{ServeArgument}}"]
            }
          }
        }
        """;

    /// <summary>The Claude Code executable, or null when it is not installed.</summary>
    public static string? FindClaude()
    {
        string exe = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "claude.exe" : "claude";

        foreach (string directory in SearchPath())
        {
            try
            {
                string candidate = Path.Combine(directory, exe);
                if (File.Exists(candidate)) return candidate;
            }
            catch
            {
                // A malformed PATH entry is not worth failing over.
            }
        }

        return null;
    }

    private static IEnumerable<string> SearchPath()
    {
        // Its own install location first: Claude Code puts itself here and does not always end up
        // on the PATH a windowed process inherits.
        yield return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin");

        foreach (string entry in (Environment.GetEnvironmentVariable("PATH") ?? "")
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            yield return entry.Trim();
    }

    /// <summary>What Claude Code currently has registered under this name.</summary>
    /// <param name="Registered">Whether an entry exists at all.</param>
    /// <param name="Type">The transport, http or stdio, empty when that was not reported.</param>
    /// <param name="Target">The address or executable it points at, empty when not reported.</param>
    /// <param name="Connected">Claude Code could reach it when asked just now.</param>
    public readonly record struct Registration(bool Registered, string Type, string Target, bool Connected)
    {
        /// <summary>Reaches this copy over HTTP, so there is nothing left to do.</summary>
        public bool PointsHere =>
            Connected
            && string.Equals(Type, "http", StringComparison.OrdinalIgnoreCase)
            && string.Equals(Target, McpEndpoint.Url, StringComparison.OrdinalIgnoreCase);

        public bool IsStdio => string.Equals(Type, "stdio", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Reads the existing registration: its transport, what it points at, and whether Claude Code
    /// could reach it. Asking makes Claude Code connect, so the answer reflects the token too.
    /// </summary>
    public static async Task<Registration> ReadRegistrationAsync(CancellationToken ct = default)
    {
        if (FindClaude() is not { } claude) return default;

        (int code, string output, _) = await RunAsync(claude, ["mcp", "get", ServerName], ct);
        return code == 0 ? Parse(output) : default;
    }

    /// <summary>The registration an existing entry describes, from what <c>claude mcp get</c> printed.</summary>
    public static Registration Parse(string output)
    {
        string type = Field(output, "Type");
        string target = string.Equals(type, "stdio", StringComparison.OrdinalIgnoreCase)
            ? Field(output, "Command")
            : Field(output, "URL");
        bool connected = Field(output, "Status").Contains("Connected", StringComparison.OrdinalIgnoreCase);

        return new Registration(true, type, target, connected);
    }

    /// <summary>The value of a "Name: value" line in what <c>claude mcp get</c> printed.</summary>
    private static string Field(string output, string name)
    {
        foreach (string line in output.Split('\n'))
        {
            string trimmed = line.Trim();
            if (trimmed.StartsWith(name + ":", StringComparison.OrdinalIgnoreCase))
                return trimmed[(name.Length + 1)..].Trim();
        }
        return "";
    }

    /// <summary>
    /// Points Claude Code at this copy's HTTP endpoint, replacing any existing entry.
    /// </summary>
    /// <remarks>
    /// Removes first, since <c>claude mcp add</c> refuses a name that is already taken. The
    /// endpoint is this copy's own, so a development build under a profile registers its own port.
    /// </remarks>
    public static async Task<(bool Ok, string Message)> RegisterAsync(string token, CancellationToken ct = default)
    {
        if (FindClaude() is not { } claude)
            return (false, "Claude Code was not found on this machine.");

        // A failure here just means there was nothing registered, which is the common case.
        await RunAsync(claude, ["mcp", "remove", ServerName], ct);

        (int code, string output, string error) = await RunAsync(
            claude,
            ["mcp", "add", "--transport", "http", "-s", Scope, ServerName, McpEndpoint.Url,
             "--header", AuthorizationHeader(token)],
            ct);

        return code == 0
            ? (true, $"Claude Code now reaches this copy at {McpEndpoint.Url}. "
                     + "New sessions connect at once; open ones after /mcp.")
            : (false, Explain(output, error));
    }

    /// <summary>Removes the registration, wherever it was made.</summary>
    public static async Task<(bool Ok, string Message)> UnregisterAsync(CancellationToken ct = default)
    {
        if (FindClaude() is not { } claude)
            return (false, "Claude Code was not found on this machine.");

        (int code, string output, string error) = await RunAsync(
            claude, ["mcp", "remove", ServerName], ct);

        return code == 0
            ? (true, "Removed from Claude Code.")
            : (false, Explain(output, error));
    }

    /// <summary>Whatever the CLI said, trimmed to one line the settings panel can show.</summary>
    private static string Explain(string output, string error)
    {
        string said = (error.Trim().Length > 0 ? error : output).Trim();
        if (said.Length == 0) return "Claude Code refused, without saying why.";

        string first = said.Split('\n')[0].Trim();
        return first.Length > 200 ? first[..200] + "…" : first;
    }

    private static async Task<(int Code, string Output, string Error)> RunAsync(
        string file, string[] arguments, CancellationToken ct)
    {
        ProcessStartInfo start = new()
        {
            FileName = file,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        foreach (string argument in arguments) start.ArgumentList.Add(argument);

        try
        {
            using Process? process = Process.Start(start);
            if (process == null) return (-1, "", "Could not start Claude Code.");

            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(Timeout);

            Task<string> output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            Task<string> error = process.StandardError.ReadToEndAsync(timeout.Token);

            await process.WaitForExitAsync(timeout.Token);

            return (process.ExitCode, await output, await error);
        }
        catch (OperationCanceledException)
        {
            return (-1, "", "Claude Code did not answer in time.");
        }
        catch (Exception ex)
        {
            return (-1, "", ex.Message);
        }
    }
}
