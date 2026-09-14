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

    /// <summary>Whether this machine already has a Huginn server registered.</summary>
    public static async Task<bool> IsRegisteredAsync(CancellationToken ct = default)
    {
        if (FindClaude() is not { } claude) return false;

        (int code, _, _) = await RunAsync(claude, ["mcp", "get", ServerName], ct);
        return code == 0;
    }

    /// <summary>Points Claude Code at the executable currently running.</summary>
    public static async Task<(bool Ok, string Message)> RegisterAsync(CancellationToken ct = default)
    {
        if (FindClaude() is not { } claude)
            return (false, "Claude Code was not found on this machine.");

        if (Environment.ProcessPath is not { } huginn)
            return (false, "Could not work out where this copy of Huginn is running from.");

        // The running executable rather than a guessed install path, so a development build
        // registers itself and an installed one registers the installed one.
        (int code, string output, string error) = await RunAsync(
            claude,
            ["mcp", "add", "-s", Scope, ServerName, "--", huginn, "--mcp"],
            ct);

        return code == 0
            ? (true, $"Registered with Claude Code as \"{ServerName}\".")
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
