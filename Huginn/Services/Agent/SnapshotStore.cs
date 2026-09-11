using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Huginn.Services.Agent;

/// <summary>
/// Reads and writes the snapshot agents consume. A file rather than a socket: nothing to listen
/// on, nothing to authenticate, and a reader still gets an answer when Huginn is not running.
/// </summary>
public static class SnapshotStore
{
    /// <summary>Beside the settings, so a named profile keeps its own.</summary>
    public static string Path => System.IO.Path.Combine(AppSettings.SettingsDir, "state.json");

    /// <summary>
    /// A file the running Huginn watches. Its presence asks for a poll now, which is the only
    /// way a reader can do anything about a snapshot that has gone stale.
    /// </summary>
    public static string RefreshRequestPath =>
        System.IO.Path.Combine(AppSettings.SettingsDir, "refresh.request");

    /// <summary>Asks the running Huginn to poll. Does nothing if it is not running.</summary>
    public static void RequestRefresh()
    {
        try
        {
            Directory.CreateDirectory(AppSettings.SettingsDir);
            File.WriteAllText(RefreshRequestPath, DateTimeOffset.UtcNow.ToString("O"));
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not write the refresh request: {ex.Message}");
        }
    }

    /// <summary>
    /// Writes the snapshot, via a temporary file so a reader never sees a half-written one.
    /// Failures are swallowed: an export that cannot be written must not disturb monitoring.
    /// </summary>
    public static void Write(AgentSnapshot snapshot)
    {
        try
        {
            Directory.CreateDirectory(AppSettings.SettingsDir);
            string temp = Path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(snapshot, AgentJson.Default.AgentSnapshot));
            File.Move(temp, Path, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not write the agent snapshot: {ex.Message}");
        }
    }

    /// <summary>The last snapshot written, or null when there is none to read.</summary>
    public static AgentSnapshot? Read()
    {
        try
        {
            return File.Exists(Path)
                ? JsonSerializer.Deserialize(File.ReadAllText(Path), AgentJson.Default.AgentSnapshot)
                : null;
        }
        catch
        {
            return null;
        }
    }
}

[JsonSerializable(typeof(AgentSnapshot))]
[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
internal sealed partial class AgentJson : JsonSerializerContext;
