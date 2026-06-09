namespace Huginn.Services;

/// <summary>
/// Secure credential storage (Windows Credential Manager / macOS Keychain).
/// </summary>
public interface ICredentialStore
{
    string? Get(string targetName);
    bool Set(string targetName, string secret, string? userName = null);
    bool Delete(string targetName);
}
