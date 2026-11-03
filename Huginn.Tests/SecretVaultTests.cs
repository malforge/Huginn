using System.Collections.Generic;
using System.Linq;
using Huginn.Services;
using Xunit;

namespace Huginn.Tests;

public sealed class SecretVaultTests
{
    /// <summary>
    /// A store of separate entries, like the keychain, counting each read of an entry that exists:
    /// on macOS each such read by a new build is one question to the person.
    /// </summary>
    private sealed class Keychain : ICredentialStore
    {
        public Dictionary<string, string> Entries { get; } = [];
        public Dictionary<string, int> Reads { get; } = [];

        /// <summary>Entries whose read is refused, as when the person clicks Deny.</summary>
        public HashSet<string> Refused { get; } = [];

        public string? Get(string targetName) =>
            TryGet(targetName, out string? value) == CredentialRead.Found ? value : null;

        public CredentialRead TryGet(string targetName, out string? secret)
        {
            secret = null;
            if (!Entries.TryGetValue(targetName, out string? value)) return CredentialRead.Absent;
            Reads[targetName] = Reads.GetValueOrDefault(targetName) + 1;
            if (Refused.Contains(targetName)) return CredentialRead.Failed;
            secret = value;
            return CredentialRead.Found;
        }

        public bool Set(string targetName, string secret, string? userName = null)
        {
            Entries[targetName] = secret;
            return true;
        }

        public bool Delete(string targetName) => Entries.Remove(targetName);
    }

    [Fact]
    public void Every_secret_lives_in_one_entry()
    {
        Keychain keychain = new();
        SecretVault vault = new(keychain);

        vault.Set("Huginn:ADO:PAT", "pat");
        vault.Set("Huginn:Sentry:Token", "sentry");
        vault.Set("MSAL:Huginn.appinsights", "cache");

        Assert.Equal([SecretVault.VaultEntry], keychain.Entries.Keys);
    }

    [Fact]
    public void A_new_build_reads_one_entry_however_many_secrets_there_are()
    {
        Keychain keychain = new();
        SecretVault earlier = new(keychain);
        earlier.Set("Huginn:ADO:PAT", "pat");
        earlier.Set("Huginn:Sentry:Token", "sentry");
        earlier.Set("MSAL:Huginn.appinsights", "cache");
        keychain.Reads.Clear();

        SecretVault next = new(keychain);
        Assert.Equal("pat", next.Get("Huginn:ADO:PAT"));
        Assert.Equal("sentry", next.Get("Huginn:Sentry:Token"));
        Assert.Equal("cache", next.Get("MSAL:Huginn.appinsights"));

        Assert.Equal(1, keychain.Reads.Values.Sum());
    }

    [Fact]
    public void A_secret_an_earlier_version_kept_on_its_own_is_moved_in_once()
    {
        Keychain keychain = new();
        keychain.Entries["Huginn:ADO:PAT"] = "old-pat";

        Assert.Equal("old-pat", new SecretVault(keychain).Get("Huginn:ADO:PAT"));
        keychain.Reads.Clear();

        Assert.Equal("old-pat", new SecretVault(keychain).Get("Huginn:ADO:PAT"));
        Assert.False(keychain.Reads.ContainsKey("Huginn:ADO:PAT"));
    }

    [Fact]
    public void A_secret_never_stored_costs_no_read()
    {
        Keychain keychain = new();
        SecretVault vault = new(keychain);

        Assert.Null(vault.Get("Huginn-verify:ADO:PAT"));
        Assert.Empty(keychain.Reads);
    }

    [Fact]
    public void A_write_keeps_what_another_process_wrote_since()
    {
        Keychain keychain = new();
        SecretVault app = new(keychain);
        SecretVault other = new(keychain);
        Assert.Null(app.Get("Huginn:ADO:PAT"));

        other.Set("Huginn:Sentry:Token", "sentry");
        app.Set("Huginn:ADO:PAT", "pat");

        SecretVault later = new(keychain);
        Assert.Equal("pat", later.Get("Huginn:ADO:PAT"));
        Assert.Equal("sentry", later.Get("Huginn:Sentry:Token"));
    }

    [Fact]
    public void An_unreadable_entry_is_treated_as_empty_rather_than_failing()
    {
        Keychain keychain = new();
        keychain.Entries[SecretVault.VaultEntry] = "{ not json";
        SecretVault vault = new(keychain);

        Assert.Null(vault.Get("Huginn:ADO:PAT"));
        Assert.True(vault.Set("Huginn:ADO:PAT", "pat"));
        Assert.Equal("pat", new SecretVault(keychain).Get("Huginn:ADO:PAT"));
    }

    [Fact]
    public void A_refused_read_neither_falls_back_to_the_old_entries_nor_overwrites_the_vault()
    {
        Keychain keychain = new();
        new SecretVault(keychain).Set("Huginn:ADO:PAT", "pat");
        keychain.Entries["Huginn:Sentry:Token"] = "old-sentry";
        string before = keychain.Entries[SecretVault.VaultEntry];
        keychain.Refused.Add(SecretVault.VaultEntry);
        keychain.Reads.Clear();

        SecretVault vault = new(keychain);

        Assert.Null(vault.Get("Huginn:ADO:PAT"));
        Assert.Null(vault.Get("Huginn:Sentry:Token"));
        Assert.False(keychain.Reads.ContainsKey("Huginn:Sentry:Token"));
        Assert.False(vault.Set("MSAL:Huginn.appinsights", "cache"));
        Assert.Equal(before, keychain.Entries[SecretVault.VaultEntry]);
    }

    [Fact]
    public void Once_allowed_after_a_refusal_the_vault_is_read_again()
    {
        Keychain keychain = new();
        new SecretVault(keychain).Set("Huginn:ADO:PAT", "pat");
        keychain.Refused.Add(SecretVault.VaultEntry);
        SecretVault vault = new(keychain);
        Assert.Null(vault.Get("Huginn:ADO:PAT"));

        keychain.Refused.Clear();

        Assert.Equal("pat", vault.Get("Huginn:ADO:PAT"));
    }

    [Fact]
    public void Writers_sharing_a_lock_file_keep_each_others_changes()
    {
        Keychain keychain = new();
        string lockPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"huginn-vault-{System.Guid.NewGuid():N}.lock");
        SecretVault first = new(keychain, lockPath);
        SecretVault second = new(keychain, lockPath);

        System.Threading.Tasks.Parallel.For(0, 40, i =>
            (i % 2 == 0 ? first : second).Set($"key{i}", $"value{i}"));

        SecretVault later = new(keychain);
        for (int i = 0; i < 40; i++) Assert.Equal($"value{i}", later.Get($"key{i}"));
        System.IO.File.Delete(lockPath);
    }

    [Fact]
    public void A_deleted_secret_an_earlier_version_kept_does_not_come_back()
    {
        Keychain keychain = new();
        keychain.Entries["Huginn:ADO:PAT"] = "old-pat";
        SecretVault vault = new(keychain);
        Assert.Equal("old-pat", vault.Get("Huginn:ADO:PAT"));

        Assert.True(vault.Delete("Huginn:ADO:PAT"));

        Assert.Null(new SecretVault(keychain).Get("Huginn:ADO:PAT"));
    }

    [Fact]
    public void A_deleted_secret_stays_deleted()
    {
        Keychain keychain = new();
        SecretVault vault = new(keychain);
        vault.Set("Huginn:ADO:PAT", "pat");

        Assert.True(vault.Delete("Huginn:ADO:PAT"));

        Assert.Null(new SecretVault(keychain).Get("Huginn:ADO:PAT"));
    }
}
