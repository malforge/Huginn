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
}
