using System;
using System.IO;

namespace Huginn.Services;

public static class Log
{
    private static readonly string LogDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Huginn");

    private static readonly string LogPath = Path.Combine(LogDir, "Huginn.log");

    private static readonly object Lock = new();
    private static StreamWriter? _writer;

    private static void EnsureWriter()
    {
        if (_writer != null) return;
        Directory.CreateDirectory(LogDir);

        // FileShare.ReadWrite lets a second instance share the file, but only when the first one
        // opened it that way too. An older build holding it exclusively would otherwise leave this
        // process with no log at all, so fall back to a file of its own.
        _writer = TryOpen(LogPath) ?? TryOpen(FallbackLogPath());
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

    private static string FallbackLogPath() =>
        Path.Combine(LogDir, $"Huginn.{Environment.ProcessId}.log");

    private static void Write(string level, string message)
    {
        try
        {
            lock (Lock)
            {
                EnsureWriter();
                _writer!.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}");
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

    public static string FilePath => LogPath;
}
