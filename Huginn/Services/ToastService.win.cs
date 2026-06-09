using System;
using System.Diagnostics;
using Microsoft.Toolkit.Uwp.Notifications;

namespace Huginn.Services;

public class WinToastService : INotificationService
{
    private bool _activationRegistered;

    public void RegisterActivation()
    {
        if (_activationRegistered) return;
        _activationRegistered = true;

        ToastNotificationManagerCompat.OnActivated += args =>
        {
            var parsed = ToastArguments.Parse(args.Argument);
            if (parsed.TryGetValue("url", out var url) && !string.IsNullOrWhiteSpace(url))
            {
                try
                {
                    Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                }
                catch
                {
                    // Ignore browser launch failures
                }
            }
        };
    }

    public void ShowNewPullRequest(string title, string author, string repo, string url)
    {
        try
        {
            new ToastContentBuilder()
                .AddArgument("action", "openPr")
                .AddArgument("url", url)
                .AddText("🛡️ New PR awaiting your review")
                .AddText(title)
                .AddText($"by {author}  ·  {repo}")
                .Show();
        }
        catch
        {
            // Toast failures are non-critical
        }
    }

    public void ShowBuildFailed(string pipelineName, string branch, string url)
    {
        try
        {
            new ToastContentBuilder()
                .AddArgument("action", "openBuild")
                .AddArgument("url", url)
                .AddText("🔴 Build Failed")
                .AddText(pipelineName)
                .AddText($"Branch: {branch}")
                .Show();
        }
        catch
        {
            // Toast failures are non-critical
        }
    }
}
