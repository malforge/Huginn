using Huginn.Services;

namespace Huginn.Tests;

/// <summary>A credential store that holds nothing, for settings that never need a token.</summary>
internal sealed class NoCredentials : ICredentialStore
{
    public string? Get(string targetName) => null;

    public bool Set(string targetName, string secret, string? userName = null) => false;

    public bool Delete(string targetName) => false;
}
