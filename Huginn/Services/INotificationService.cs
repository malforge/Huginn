namespace Huginn.Services;

/// <summary>
/// Shows OS-native notifications (toasts on Windows, notification center on macOS).
/// </summary>
public interface INotificationService
{
    /// <summary>
    /// Register platform-specific activation handler (call once at startup).
    /// </summary>
    void RegisterActivation();

    void ShowNewPullRequest(string title, string author, string repo, string url);

    void ShowBuildFailed(string pipelineName, string branch, string url);

    /// <summary>A Sentry issue that is new, or has returned after being resolved.</summary>
    void ShowSentryIssue(string title, string project, string impact, string url, bool isRegression);

    /// <summary>A newly detected problem on a monitored service.</summary>
    void ShowServiceFinding(string subject, string resource, string detail);

    /// <summary>
    /// Everything one poll found, as a single notification. A poll that turns up ten problems
    /// must not fire ten of these: a storm reads exactly like a real emergency, so neither
    /// gets read.
    /// </summary>
    void ShowSummary(string heading, string subject, string detail, string url);
}
