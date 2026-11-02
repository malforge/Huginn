using System;
using System.IO;
using System.Text.Json.Nodes;

namespace Huginn.Services.Agent;

/// <summary>
/// The channel an agent uses to ask a running Huginn to look closer at one finding.
/// </summary>
/// <remarks>
/// Files beside the snapshot, for the same reason it is a file: the stdio MCP process holds no
/// credentials and opens no port. It asks; the running Huginn, which is signed in, answers. The
/// app's own HTTP endpoint goes through the same files, so both transports behave alike. Each
/// question has a pair of files of its own, named by its id, so agents asking at the same time
/// neither overwrite each other's question nor collect each other's answer.
/// </remarks>
public static class InvestigationStore
{
    private const string Prefix = "investigate-";

    private const string RequestExtension = ".request";

    private const string ResponseExtension = ".response";

    /// <summary>What the running Huginn watches for, as a file name pattern.</summary>
    public const string RequestFilter = Prefix + "*" + RequestExtension;

    /// <summary>Written by the asker, watched by the running Huginn.</summary>
    public static string RequestPath(string id) =>
        Path.Combine(AppSettings.SettingsDir, Prefix + id + RequestExtension);

    /// <summary>Written by the running Huginn, polled by the asker.</summary>
    public static string ResponsePath(string id) =>
        Path.Combine(AppSettings.SettingsDir, Prefix + id + ResponseExtension);

    /// <summary>Asks for a drill-down. Does nothing usable if Huginn is not running.</summary>
    public static void Ask(string id, string subject, string resource, string kind)
    {
        Write(RequestPath(id), new JsonObject
        {
            ["id"] = id,
            ["subject"] = subject,
            ["resource"] = resource,
            ["kind"] = kind,
            ["askedAt"] = DateTimeOffset.UtcNow.ToString("O"),
        });
    }

    /// <summary>Answers the request with that id.</summary>
    public static void Answer(string id, string text)
    {
        Write(ResponsePath(id), new JsonObject
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

    /// <summary>Removes a file, ignoring a failure.</summary>
    public static void Discard(string path)
    {
        try { File.Delete(path); } catch { }
    }

    /// <summary>
    /// Removes the questions and answers older than <paramref name="age"/>. An asker that gave up
    /// waiting never collects its answer, and a question asked while Huginn was closed is
    /// answered by nobody.
    /// </summary>
    public static void DiscardOlderThan(TimeSpan age)
    {
        try
        {
            DateTime cutoff = DateTime.UtcNow - age;
            foreach (string path in Directory.EnumerateFiles(AppSettings.SettingsDir, Prefix + "*"))
            {
                if (File.GetLastWriteTimeUtc(path) < cutoff) Discard(path);
            }
        }
        catch
        {
            // Tidying up is not worth failing a question over.
        }
    }

    private static void Write(string path, JsonObject payload)
    {
        try
        {
            Directory.CreateDirectory(AppSettings.SettingsDir);

            // Through a temporary file, so the other side never reads a half-written one. The
            // temporary name must not match the request filter, or the watcher fires on it.
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
