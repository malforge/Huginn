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

    /// <summary>The Claude Code one-liner, for someone who would rather run it themselves.</summary>
    public static string CliCommand =>
        $"claude mcp add -s {Scope} {ServerName} -- \"{ExecutablePath}\" {ServeArgument}";

    /// <summary>
    /// The same thing as configuration, for a client that is not Claude Code. Nearly every MCP
    /// client takes this shape, so it is more use than instructions for one of them.
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
    /// <param name="Command">The executable it points at, empty when that was not reported.</param>
    /// <param name="PointsHere">The entry names the executable now running.</param>
    public readonly record struct Registration(bool Registered, string Command, bool PointsHere);

    /// <summary>
    /// Reads the existing registration, including which executable it names.
    /// </summary>
    /// <remarks>
    /// The command is only reported for some kinds of entry, so an empty
    /// <see cref="Registration.Command"/> means unknown rather than none, and
    /// <see cref="Registration.PointsHere"/> is false in that case rather than guessing.
    /// </remarks>
    public static async Task<Registration> ReadRegistrationAsync(CancellationToken ct = default)
    {
        if (FindClaude() is not { } claude) return new Registration(false, "", false);

        (int code, string output, _) = await RunAsync(claude, ["mcp", "get", ServerName], ct);
        if (code != 0) return new Registration(false, "", false);

        string command = "";
        foreach (string line in output.Split('\n'))
        {
            string trimmed = line.Trim();
            if (!trimmed.StartsWith("Command:", StringComparison.OrdinalIgnoreCase)) continue;

            command = trimmed["Command:".Length..].Trim();
            break;
        }

        bool here = command.Length > 0
                    && Environment.ProcessPath is { } running
                    && string.Equals(
                        Path.GetFullPath(command), Path.GetFullPath(running),
                        StringComparison.OrdinalIgnoreCase);

        return new Registration(true, command, here);
    }

    /// <summary>
    /// Points Claude Code at the executable currently running, replacing any existing entry.
    /// </summary>
    /// <remarks>
    /// Removes first, because <c>claude mcp get</c> reports only scope and status, never which
    /// executable is registered. Without that there is no way to tell a stale entry from a current
    /// one, so the only honest thing the button can offer is to overwrite it with this copy.
    /// </remarks>
    public static async Task<(bool Ok, string Message)> RegisterAsync(CancellationToken ct = default)
    {
        if (FindClaude() is not { } claude)
            return (false, "Claude Code was not found on this machine.");

        if (Environment.ProcessPath is not { } huginn)
            return (false, "Could not work out where this copy of Huginn is running from.");

        // A failure here just means there was nothing registered, which is the common case.
        await RunAsync(claude, ["mcp", "remove", ServerName], ct);

        // The running executable rather than a guessed install path, so a development build
        // registers itself and an installed one registers the installed one.
        (int code, string output, string error) = await RunAsync(
            claude,
            ["mcp", "add", "-s", Scope, ServerName, "--", huginn, ServeArgument],
            ct);

        return code == 0
            ? (true, $"Claude Code now points at this copy: {huginn}")
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
