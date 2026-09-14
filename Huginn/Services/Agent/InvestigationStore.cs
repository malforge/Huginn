using System;
using System.IO;
using System.Text.Json.Nodes;

namespace Huginn.Services.Agent;

/// <summary>
/// The channel an agent uses to ask a running Huginn to look closer at one finding.
/// </summary>
/// <remarks>
/// Two files beside the snapshot, for the same reason it is a file: the MCP process holds no
/// credentials and opens no port. It asks; the running Huginn, which is signed in, answers.
/// </remarks>
public static class InvestigationStore
{
    /// <summary>Written by the asker, watched by the running Huginn.</summary>
    public static string RequestPath =>
        Path.Combine(AppSettings.SettingsDir, "investigate.request");

    /// <summary>Written by the running Huginn, polled by the asker.</summary>
    public static string ResponsePath =>
        Path.Combine(AppSettings.SettingsDir, "investigate.response");

    /// <summary>Asks for a drill-down. Does nothing usable if Huginn is not running.</summary>
    public static void Ask(string id, string subject, string resource, string kind)
    {
        Write(RequestPath, new JsonObject
        {
            ["id"] = id,
            ["subject"] = subject,
            ["resource"] = resource,
            ["kind"] = kind,
            ["askedAt"] = DateTimeOffset.UtcNow.ToString("O"),
        });
    }

    /// <summary>Answers the request with that id, replacing any older answer.</summary>
    public static void Answer(string id, string text)
    {
        Write(ResponsePath, new JsonObject
        {
            ["id"] = id,
            ["text"] = text,
            ["answeredAt"] = DateTimeOffset.UtcNow.ToString("O"),
        });
    }

    /// <summary>Reads a request or an answer, or null when there is none to read.</summary>
    public static JsonObject? Read(string path)
    {
        try
        {
            return File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path)) as JsonObject : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Removes a file, ignoring a failure. A leftover is re-read on the next start.</summary>
    public static void Discard(string path)
    {
        try { File.Delete(path); } catch { }
    }

    private static void Write(string path, JsonObject payload)
    {
        try
        {
            Directory.CreateDirectory(AppSettings.SettingsDir);

            // Through a temporary file, so the other side never reads a half-written one.
            string temp = path + ".tmp";
            File.WriteAllText(temp, payload.ToJsonString());
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not write {Path.GetFileName(path)}: {ex.Message}");
        }
    }
}
