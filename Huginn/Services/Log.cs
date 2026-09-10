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
        // FileShare.ReadWrite so a second instance still logs instead of silently
        // losing every line to a sharing violation.
        _writer = new StreamWriter(
            new FileStream(LogPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
        {
            AutoFlush = true,
        };
    }

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
