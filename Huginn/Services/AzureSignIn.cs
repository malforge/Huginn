using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Azure.Core;
using Azure.Identity;

namespace Huginn.Services;

/// <summary>
/// Builds the interactive Entra credential used for Azure telemetry, and remembers the signed-in
/// account so later launches refresh silently.
/// </summary>
/// <remarks>
/// Each environment is a separate slot with its own tenant, because production and development are
/// different logins. Without <see cref="TokenCachePersistenceOptions"/> the token cache is memory
/// only and every restart would prompt again.
/// </remarks>
public sealed class AzureSignIn
{
    /// <summary>Scope for ARM, used to enumerate subscriptions and components.</summary>
    public static readonly string[] ArmScope = ["https://management.azure.com/.default"];

    /// <summary>Scope for the Application Insights query API.</summary>
    public static readonly string[] QueryScope = ["https://api.applicationinsights.io/.default"];

    private static readonly string RecordDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Huginn", "auth");

    private readonly string _slot;
    private readonly string? _tenantId;
    private readonly string? _clientId;

    /// <param name="slot">Names the environment, so two sign-ins do not overwrite each other.</param>
    /// <param name="tenantId">Tenant to authenticate against; the home tenant when empty.</param>
    /// <param name="clientId">
    /// Application registration to present. When empty the Azure SDK development application is
    /// used, which needs no setup but is not the recommended posture for production tenants.
    /// </param>
    public AzureSignIn(string slot, string? tenantId, string? clientId)
    {
        _slot = string.IsNullOrWhiteSpace(slot) ? "default" : slot;
        _tenantId = string.IsNullOrWhiteSpace(tenantId) ? null : tenantId;
        _clientId = string.IsNullOrWhiteSpace(clientId) ? null : clientId;
    }

    private string RecordPath => Path.Combine(RecordDir, $"{_slot}.json");

    /// <summary>True when a previous sign-in for this slot can be resumed without prompting.</summary>
    public bool HasStoredAccount => File.Exists(RecordPath);

    /// <summary>
    /// Returns a credential for this slot. Resumes the stored account when there is one, and only
    /// prompts when <paramref name="allowPrompt"/> is set.
    /// </summary>
    /// <remarks>
    /// An interactive sign-in waits on a redirect to a local listener. If the browser is closed
    /// first that redirect never arrives, so the caller must supply a token it can cancel.
    /// </remarks>
    public async Task<(TokenCredential Credential, string Account)> GetCredentialAsync(
        bool allowPrompt, CancellationToken ct = default)
    {
        InteractiveBrowserCredentialOptions options = new()
        {
            TenantId = _tenantId,
            ClientId = _clientId,
            TokenCachePersistenceOptions = new TokenCachePersistenceOptions { Name = $"Huginn.{_slot}" },
            DisableAutomaticAuthentication = !allowPrompt,
            AdditionallyAllowedTenants = { "*" },
        };

        AuthenticationRecord? stored = await TryLoadRecordAsync(ct);
        if (stored is not null)
            options.AuthenticationRecord = stored;

        InteractiveBrowserCredential credential = new(options);

        if (stored is not null)
            return (credential, stored.Username);

        if (!allowPrompt)
            throw new InvalidOperationException("Not signed in.");

        // MSAL builds its client and opens the persistent token cache synchronously before the
        // first await, which freezes the window if that happens on the UI thread.
        AuthenticationRecord record = await Task.Run(
            () => credential.AuthenticateAsync(new TokenRequestContext(ArmScope), ct), ct);
        await SaveRecordAsync(record, ct);
        return (credential, record.Username);
    }

    /// <summary>
    /// Silently acquires a token to confirm the stored sign-in still works. A stored record only
    /// proves a file exists; it says nothing about whether the account is still usable.
    /// </summary>
    public async Task<bool> IsStillSignedInAsync(TokenCredential credential, CancellationToken ct = default)
    {
        try
        {
            await Task.Run(
                () => credential.GetTokenAsync(new TokenRequestContext(ArmScope), ct).AsTask(), ct);
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Info($"Stored Azure sign-in for {_slot} is no longer usable: {ex.Message}");
            return false;
        }
    }

    /// <summary>Forgets the stored account, so the next sign-in prompts again.</summary>
    public void SignOut()
    {
        try
        {
            if (File.Exists(RecordPath)) File.Delete(RecordPath);
        }
        catch (Exception ex)
        {
            Log.Error($"Could not clear the stored account for {_slot}: {ex.Message}");
        }
    }

    private async Task<AuthenticationRecord?> TryLoadRecordAsync(CancellationToken ct)
    {
        if (!File.Exists(RecordPath)) return null;
        try
        {
            await using FileStream stream = File.OpenRead(RecordPath);
            return await AuthenticationRecord.DeserializeAsync(stream, ct);
        }
        catch (Exception ex)
        {
            Log.Error($"Stored account for {_slot} is unreadable, ignoring it: {ex.Message}");
            return null;
        }
    }

    private async Task SaveRecordAsync(AuthenticationRecord record, CancellationToken ct)
    {
        try
        {
            Directory.CreateDirectory(RecordDir);
            await using FileStream stream = File.Create(RecordPath);
            await record.SerializeAsync(stream, ct);
        }
        catch (Exception ex)
        {
            Log.Error($"Could not store the account for {_slot}: {ex.Message}");
        }
    }
}
