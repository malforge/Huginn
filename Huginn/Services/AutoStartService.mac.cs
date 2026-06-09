using System;
using System.IO;

namespace Huginn.Services;

/// <summary>
/// macOS auto-start via LaunchAgent plist in ~/Library/LaunchAgents/.
/// </summary>
public sealed class MacAutoStartService : IAutoStartService
{
    private const string Label = "com.huginn.monitor";

    private static string PlistPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Library", "LaunchAgents", $"{Label}.plist");

    public bool IsEnabled => File.Exists(PlistPath);

    public void Enable(string executablePath)
    {
        try
        {
            var dir = Path.GetDirectoryName(PlistPath)!;
            Directory.CreateDirectory(dir);

            var plist = $"""
                <?xml version="1.0" encoding="UTF-8"?>
                <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
                <plist version="1.0">
                <dict>
                    <key>Label</key>
                    <string>{Label}</string>
                    <key>ProgramArguments</key>
                    <array>
                        <string>{executablePath}</string>
                    </array>
                    <key>RunAtLoad</key>
                    <true/>
                    <key>KeepAlive</key>
                    <false/>
                </dict>
                </plist>
                """;

            File.WriteAllText(PlistPath, plist);
            Log.Info($"Auto-start enabled: {PlistPath}");
        }
        catch (Exception ex)
        {
            Log.Warn($"Failed to enable auto-start: {ex.Message}");
        }
    }

    public void Disable()
    {
        try
        {
            if (File.Exists(PlistPath))
            {
                File.Delete(PlistPath);
                Log.Info("Auto-start disabled");
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Failed to disable auto-start: {ex.Message}");
        }
    }
}
