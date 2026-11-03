using System;
using System.Runtime.InteropServices;
using System.Text;

namespace Huginn.Services;

/// <summary>
/// macOS Keychain credential storage via Security.framework P/Invoke, one entry per name.
/// </summary>
/// <remarks>
/// Reading an entry's secret is what the keychain asks the person about; finding the entry is not.
/// So only <see cref="TryGet"/> asks for the secret, and changing or deleting an entry asks for the
/// entry alone.
/// </remarks>
public class KeychainCredentialStore : ICredentialStore
{
    private const string ServiceName = "Huginn";
    private const string Security = "/System/Library/Frameworks/Security.framework/Security";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

    /// <summary>errSecItemNotFound: nothing is stored under that name.</summary>
    private const int ItemNotFound = -25300;

    public string? Get(string targetName) =>
        TryGet(targetName, out string? secret) == CredentialRead.Found ? secret : null;

    public CredentialRead TryGet(string targetName, out string? secret)
    {
        secret = null;
        var status = SecKeychainFindGenericPassword(
            IntPtr.Zero,
            Length(ServiceName), ServiceName,
            Length(targetName), targetName,
            out var passwordLength, out var passwordData,
            IntPtr.Zero);

        if (status == ItemNotFound) return CredentialRead.Absent;
        if (status != 0 || passwordData == IntPtr.Zero)
        {
            Log.Warn($"Could not read {targetName} from the keychain (status {status}).");
            return CredentialRead.Failed;
        }

        try
        {
            var bytes = new byte[passwordLength];
            Marshal.Copy(passwordData, bytes, 0, (int)passwordLength);
            secret = Encoding.UTF8.GetString(bytes);
            return CredentialRead.Found;
        }
        finally
        {
            SecKeychainItemFreeContent(IntPtr.Zero, passwordData);
        }
    }

    public bool Set(string targetName, string secret, string? userName = null)
    {
        var passwordBytes = Encoding.UTF8.GetBytes(secret);

        IntPtr existing = FindItem(targetName);
        if (existing != IntPtr.Zero)
        {
            try
            {
                return SecKeychainItemModifyAttributesAndData(
                    existing, IntPtr.Zero, (uint)passwordBytes.Length, passwordBytes) == 0;
            }
            finally
            {
                CFRelease(existing);
            }
        }

        return SecKeychainAddGenericPassword(
            IntPtr.Zero,
            Length(ServiceName), ServiceName,
            Length(targetName), targetName,
            (uint)passwordBytes.Length, passwordBytes,
            IntPtr.Zero) == 0;
    }

    public bool Delete(string targetName)
    {
        IntPtr item = FindItem(targetName);
        if (item == IntPtr.Zero) return false;

        try
        {
            return SecKeychainItemDelete(item) == 0;
        }
        finally
        {
            CFRelease(item);
        }
    }

    /// <summary>The entry itself, without its secret, or zero when there is none. Release it.</summary>
    private static IntPtr FindItem(string targetName)
    {
        var status = SecKeychainFindGenericPasswordItem(
            IntPtr.Zero,
            Length(ServiceName), ServiceName,
            Length(targetName), targetName,
            IntPtr.Zero, IntPtr.Zero,
            out var item);

        return status == 0 ? item : IntPtr.Zero;
    }

    /// <summary>The keychain counts the bytes it is passed, which differ from characters outside ASCII.</summary>
    private static uint Length(string text) => (uint)Encoding.UTF8.GetByteCount(text);

    [DllImport(Security)]
    private static extern int SecKeychainFindGenericPassword(
        IntPtr keychain, uint serviceNameLength, string serviceName,
        uint accountNameLength, string accountName,
        out uint passwordLength, out IntPtr passwordData, IntPtr itemRef);

    /// <summary>The same call asking for the entry and not its secret.</summary>
    [DllImport(Security, EntryPoint = "SecKeychainFindGenericPassword")]
    private static extern int SecKeychainFindGenericPasswordItem(
        IntPtr keychain, uint serviceNameLength, string serviceName,
        uint accountNameLength, string accountName,
        IntPtr passwordLength, IntPtr passwordData, out IntPtr itemRef);

    [DllImport(Security)]
    private static extern int SecKeychainAddGenericPassword(
        IntPtr keychain, uint serviceNameLength, string serviceName,
        uint accountNameLength, string accountName,
        uint passwordLength, byte[] passwordData, IntPtr itemRef);

    [DllImport(Security)]
    private static extern int SecKeychainItemModifyAttributesAndData(
        IntPtr itemRef, IntPtr attrList, uint length, byte[] data);

    [DllImport(Security)]
    private static extern int SecKeychainItemDelete(IntPtr itemRef);

    [DllImport(Security)]
    private static extern int SecKeychainItemFreeContent(IntPtr attrList, IntPtr data);

    [DllImport(CoreFoundation)]
    private static extern void CFRelease(IntPtr cf);
}
