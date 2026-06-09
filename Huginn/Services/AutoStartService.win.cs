using Microsoft.Win32;

namespace Huginn.Services;

/// <summary>
/// Windows auto-start via HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\Run registry key.
/// </summary>
public sealed class WinAutoStartService : IAutoStartService
{
    private const string RunKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Huginn";

    public bool IsEnabled
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKey, false);
                return key?.GetValue(ValueName) is string;
            }
            catch
            {
                return false;
            }
        }
    }

    public void Enable(string executablePath)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, true);
            key?.SetValue(ValueName, $"\"{executablePath}\"");
            Log.Info($"Auto-start enabled: {executablePath}");
        }
        catch (System.Exception ex)
        {
            Log.Warn($"Failed to enable auto-start: {ex.Message}");
        }
    }

    public void Disable()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, true);
            key?.DeleteValue(ValueName, false);
            Log.Info("Auto-start disabled");
        }
        catch (System.Exception ex)
        {
            Log.Warn($"Failed to disable auto-start: {ex.Message}");
        }
    }
}
