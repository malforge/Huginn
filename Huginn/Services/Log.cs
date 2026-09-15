using System;
using System.Globalization;
using System.IO;

namespace Huginn.Services;

/// <summary>
/// The application log. One file per day, kept for <see cref="KeepDays"/> days.
/// </summary>
/// <remarks>
/// The date in the file name is the whole rotation scheme: nothing is renamed, so a second process
/// sharing the folder, the MCP server included, never has a file moved out from under its open
/// handle. Left unrotated this reached 58 MB over five months.
/// </remarks>
public static class Log
{
    // Beside the settings, so a named HUGINN_PROFILE keeps its own log rather than appending to,
    // and pruning, the installed copy's.
    private static readonly string LogDir = AppSettings.SettingsDir;

    /// <summary>Days of history kept. A day costs a few hundred kilobytes at current volumes.</summary>
    private const int KeepDays = 14;

    private static readonly object Lock = new();
    private static StreamWriter? _writer;
    private static DateOnly _writerDay;

    /// <summary>Today's log file.</summary>
    public static string FilePath => PathFor(DateOnly.FromDateTime(DateTime.Now));

    private static string PathFor(DateOnly day) =>
        Path.Combine(LogDir, $"Huginn-{day:yyyy-MM-dd}.log");

    private static void EnsureWriter(DateOnly day)
    {
        if (_writer != null && _writerDay == day) return;

        _writer?.Dispose();
        _writer = null;
        Directory.CreateDirectory(LogDir);

        // FileShare.ReadWrite lets a second instance share the file, but only when the first one
        // opened it that way too. An older build holding it exclusively would otherwise leave this
        // process with no log at all, so fall back to a file of its own.
        _writer = TryOpen(PathFor(day)) ?? TryOpen(FallbackPath(day));
        _writerDay = day;

        Prune();
    }

    private static StreamWriter? TryOpen(string path)
    {
        try
        {
            return new StreamWriter(
                new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
            {
                AutoFlush = true,
            };
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static string FallbackPath(DateOnly day) =>
        Path.Combine(LogDir, $"Huginn-{day:yyyy-MM-dd}-{Environment.ProcessId}.log");

    /// <summary>
    /// Drops logs older than <see cref="KeepDays"/>. Only files this class names are considered, so
    /// nothing else in the folder can be caught by it, the pre-rotation Huginn.log included.
    /// </summary>
    private static void Prune()
    {
        try
        {
            DateOnly cutoff = DateOnly.FromDateTime(DateTime.Now.AddDays(-KeepDays));

            foreach (string file in Directory.EnumerateFiles(LogDir, "Huginn-*.log"))
            {
                string name = Path.GetFileNameWithoutExtension(file);
                if (name.Length < 17) continue;

                if (DateOnly.TryParseExact(
                        name.Substring(7, 10), "yyyy-MM-dd", CultureInfo.InvariantCulture,
                        DateTimeStyles.None, out DateOnly day)
                    && day < cutoff)
                {
                    File.Delete(file);
                }
            }
        }
        catch
        {
            // A log that cannot be tidied is not worth failing over.
        }
    }

    private static void Write(string level, string message)
    {
        try
        {
            DateTime now = DateTime.Now;

            lock (Lock)
            {
                EnsureWriter(DateOnly.FromDateTime(now));
                _writer?.WriteLine($"{now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}");
            }
        }
        catch
        {
            // Logging must never crash the app
        }
    }

    public static void Info(string message) => Write("INF", message);
    public static void Warn(string message) => Write("WRN", message);
    public static void Error(string message) => Write("ERR", message);
}
