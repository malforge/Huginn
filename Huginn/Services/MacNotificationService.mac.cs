using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Huginn.Services;

/// <summary>
/// macOS notification center via NSUserNotificationCenter (ObjC runtime interop).
/// Uses the older NSUserNotification API for broad macOS version support.
/// </summary>
public class MacNotificationService : INotificationService
{
    public void RegisterActivation()
    {
        // On macOS, notification click handling is typically done via
        // NSUserNotificationCenterDelegate. For now we rely on the default
        // behavior — notifications show but clicks open the app.
    }

    public void ShowNewPullRequest(string title, string author, string repo, string url)
    {
        try
        {
            // Use stdin to avoid shell argument injection — osascript reads from stdin safely
            var script = $"display notification \"{EscapeAppleScript($"by {author}  ·  {repo}")}\" " +
                         $"with title \"🐦‍⬛ New PR awaiting review\" " +
                         $"subtitle \"{EscapeAppleScript(title)}\"";

            var psi = new ProcessStartInfo
            {
                FileName = "osascript",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
            };

            var proc = Process.Start(psi);
            if (proc != null)
            {
                proc.StandardInput.Write(script);
                proc.StandardInput.Close();
            }
        }
        catch
        {
            // Non-critical
        }
    }

    public void ShowSentryIssue(string title, string project, string impact, string url, bool isRegression)
    {
        Notify(
            isRegression ? "🔁 Sentry regression" : "🐛 New Sentry issue",
            title,
            $"{project} · {impact}");
    }

    private void Notify(string heading, string subtitle, string body)
    {
        try
        {
            var script = $"display notification \"{EscapeAppleScript(body)}\" " +
                         $"with title \"{heading}\" " +
                         $"subtitle \"{EscapeAppleScript(subtitle)}\"";

            var psi = new ProcessStartInfo
            {
                FileName = "osascript",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
            };

            var proc = Process.Start(psi);
            if (proc != null)
            {
                proc.StandardInput.Write(script);
                proc.StandardInput.Close();
            }
        }
        catch
        {
            // Non-critical
        }
    }

    public void ShowBuildFailed(string pipelineName, string branch, string url)
    {
        try
        {
            var script = $"display notification \"{EscapeAppleScript($"Branch: {branch}")}\" " +
                         $"with title \"🔴 Build Failed\" " +
                         $"subtitle \"{EscapeAppleScript(pipelineName)}\"";

            var psi = new ProcessStartInfo
            {
                FileName = "osascript",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
            };

            var proc = Process.Start(psi);
            if (proc != null)
            {
                proc.StandardInput.Write(script);
                proc.StandardInput.Close();
            }
        }
        catch
        {
            // Non-critical
        }
    }

    private static string EscapeAppleScript(string s) =>
        s.Replace("\\", "\\\\").Replace("\"", "\\\"");
}
