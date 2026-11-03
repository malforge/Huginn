using System;
using System.Threading;
using System.Threading.Tasks;
using Azure.Identity;

namespace Huginn.Services;

/// <summary>
/// Keeps an Azure sign-in's token cache in <see cref="SecretVault"/> rather than in a keychain
/// entry of its own, so it adds no question of its own after an update.
/// </summary>
/// <remarks>
/// The cache is stored as the library hands it over, which is why the type is called unsafe: it is
/// protected by the store it is put in, here the keychain, and by nothing of its own.
/// </remarks>
public sealed class VaultTokenCacheOptions : UnsafeTokenCacheOptions
{
    private readonly ICredentialStore _vault;
    private readonly string _name;

    public VaultTokenCacheOptions(ICredentialStore vault, string name)
    {
        _vault = vault;
        _name = name;
        Name = name;
    }

    /// <summary>One entry per cache, and per kind of token, since the library keeps two.</summary>
    private string Key(bool cae) => $"MSAL:{_name}{(cae ? ":cae" : "")}";

    protected override Task<ReadOnlyMemory<byte>> RefreshCacheAsync() => Task.FromResult(Load(false));

    protected override Task<TokenCacheData> RefreshCacheAsync(TokenCacheRefreshArgs args, CancellationToken cancellationToken = default) =>
        Task.FromResult(new TokenCacheData(Load(args.IsCaeEnabled)));

    protected override Task TokenCacheUpdatedAsync(TokenCacheUpdatedArgs args)
    {
        _vault.Set(Key(args.IsCaeEnabled), Convert.ToBase64String(args.UnsafeCacheData.Span));
        return Task.CompletedTask;
    }

    private ReadOnlyMemory<byte> Load(bool cae)
    {
        string? stored = _vault.Get(Key(cae));
        if (string.IsNullOrEmpty(stored)) return ReadOnlyMemory<byte>.Empty;

        try
        {
            return Convert.FromBase64String(stored);
        }
        catch (FormatException)
        {
            return ReadOnlyMemory<byte>.Empty;
        }
    }
}
