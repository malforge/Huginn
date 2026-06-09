namespace Huginn.Services;

/// <summary>
/// Manages registering/unregistering the app to launch at login.
/// </summary>
public interface IAutoStartService
{
    bool IsEnabled { get; }
    void Enable(string executablePath);
    void Disable();
}
