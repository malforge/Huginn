namespace Huginn.Services;

/// <summary>
/// Secure credential storage (Windows Credential Manager / macOS Keychain).
/// </summary>
public interface ICredentialStore
{
    string? Get(string targetName);
    bool Set(string targetName, string secret, string? userName = null);
    bool Delete(string targetName);

    /// <summary>
    /// Reads an entry, telling one that is not there from one that could not be read, such as a
    /// keychain the person refused or that is locked. Only the first means there is nothing.
    /// </summary>
    CredentialRead TryGet(string targetName, out string? secret)
    {
        secret = Get(targetName);
        return secret is null ? CredentialRead.Absent : CredentialRead.Found;
    }
}
