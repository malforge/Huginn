using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;

namespace Huginn.Services;

/// <summary>
/// Every secret Huginn keeps, held as one entry in an underlying store rather than one entry each.
/// </summary>
/// <remarks>
/// For the macOS keychain. It asks the person to allow access once per entry, and asks again for
/// every entry whenever the app's build changes, since an app without an Apple team identity is
/// trusted by the hash of that exact build. One entry makes that one question per update rather
/// than one per token. Secrets stored one entry each by earlier versions are read once, the first
/// time they are asked for, and kept here from then on.
/// <para>
/// An entry that could not be read, because the person refused or the keychain is locked, is never
/// taken for an empty one: nothing is moved in and nothing is written, so a refusal cannot wipe
/// what is stored.
/// </para>
/// </remarks>
public sealed class SecretVault : ICredentialStore
{
    /// <summary>The entry holding everything, beside the entries of earlier versions.</summary>
    public const string VaultEntry = "Huginn.vault";

    private readonly ICredentialStore _store;
    private readonly string? _lockPath;
    private readonly object _lock = new();
    private Dictionary<string, string>? _secrets;

    /// <param name="store">Where the one entry lives.</param>
    /// <param name="lockPath">
    /// A file every process sharing the entry agrees on, held while one of them rewrites it, so two
    /// writing at once cannot drop each other's change. None for a store only this process uses.
    /// </param>
    public SecretVault(ICredentialStore store, string? lockPath = null)
    {
        _store = store;
        _lockPath = lockPath;
    }

    public string? Get(string targetName) => TryGet(targetName, out string? secret) == CredentialRead.Found ? secret : null;

    public CredentialRead TryGet(string targetName, out string? secret)
    {
        secret = null;
        lock (_lock)
        {
            if (_secrets is null)
            {
                if (Read(out var read) == CredentialRead.Failed) return CredentialRead.Failed;
                _secrets = read;
            }

            if (_secrets.TryGetValue(targetName, out secret)) return CredentialRead.Found;

            // Kept on its own by an earlier version. Asking for one that was never stored costs
            // nothing: only an entry that exists needs the person's permission to read.
            CredentialRead earlier = _store.TryGet(targetName, out secret);
            if (earlier != CredentialRead.Found) return earlier;

            Log.Info($"Moved {targetName} into the single keychain entry.");
            string moved = secret!;
            Change(s => s[targetName] = moved);
            return CredentialRead.Found;
        }
    }

    public bool Set(string targetName, string secret, string? userName = null)
    {
        lock (_lock) return Change(s => s[targetName] = secret);
    }

    public bool Delete(string targetName)
    {
        lock (_lock)
        {
            // The earlier version's entry too, or the next read would move it back in.
            _store.Delete(targetName);
            return Change(s => s.Remove(targetName));
        }
    }

    /// <summary>
    /// Rewrites the entry from what it holds now, not from what this process last read, so a
    /// change another process made since is kept. Refuses when the entry cannot be read.
    /// </summary>
    private bool Change(Action<Dictionary<string, string>> change)
    {
        using FileStream? held = HoldLock();

        if (Read(out var secrets) == CredentialRead.Failed) return false;
        change(secrets);
        _secrets = secrets;

        return _store.Set(VaultEntry,
            JsonSerializer.Serialize(secrets, SecretVaultJsonContext.Default.DictionaryStringString),
            "Huginn");
    }

    private CredentialRead Read(out Dictionary<string, string> secrets)
    {
        secrets = new(StringComparer.Ordinal);

        CredentialRead read = _store.TryGet(VaultEntry, out string? json);
        if (read == CredentialRead.Failed) return read;
        if (read == CredentialRead.Absent || string.IsNullOrEmpty(json)) return CredentialRead.Absent;

        try
        {
            secrets = JsonSerializer.Deserialize(json, SecretVaultJsonContext.Default.DictionaryStringString)
                      ?? new(StringComparer.Ordinal);
        }
        catch (JsonException ex)
        {
            // Nothing can be recovered from it, so it is replaced on the next write rather than
            // blocking every write from now on.
            Log.Error($"The keychain entry {VaultEntry} is unreadable, so it is treated as empty: {ex.Message}");
        }

        return CredentialRead.Found;
    }

    /// <summary>
    /// The cross-process lock, waited for briefly. Writing without it is better than not writing,
    /// since the race it guards against is rare and a lost token only means signing in again.
    /// </summary>
    private FileStream? HoldLock()
    {
        if (_lockPath is null) return null;

        for (int attempt = 0; attempt < 40; attempt++)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_lockPath)!);
                return new FileStream(_lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException)
            {
                Thread.Sleep(50);
            }
            catch (UnauthorizedAccessException)
            {
                break;
            }
        }

        Log.Warn($"Could not hold {_lockPath}, so the keychain entry is written without it.");
        return null;
    }
}
