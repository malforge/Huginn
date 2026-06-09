using System;
using System.Runtime.InteropServices;
using System.Text;

namespace Huginn.Services;

/// <summary>
/// macOS Keychain credential storage via Security.framework P/Invoke.
/// </summary>
public class KeychainCredentialStore : ICredentialStore
{
    private const string ServiceName = "Huginn";

    public string? Get(string targetName)
    {
        var status = SecKeychainFindGenericPassword(
            IntPtr.Zero,
            (uint)ServiceName.Length, ServiceName,
            (uint)targetName.Length, targetName,
            out var passwordLength, out var passwordData,
            out _);

        if (status != 0 || passwordData == IntPtr.Zero)
            return null;

        try
        {
            var bytes = new byte[passwordLength];
            Marshal.Copy(passwordData, bytes, 0, (int)passwordLength);
            return Encoding.UTF8.GetString(bytes);
        }
        finally
        {
            SecKeychainItemFreeContent(IntPtr.Zero, passwordData);
        }
    }

    public bool Set(string targetName, string secret, string? userName = null)
    {
        var passwordBytes = Encoding.UTF8.GetBytes(secret);

        // Try to update existing first
        var findStatus = SecKeychainFindGenericPassword(
            IntPtr.Zero,
            (uint)ServiceName.Length, ServiceName,
            (uint)targetName.Length, targetName,
            out _, out _, out var existingItem);

        if (findStatus == 0 && existingItem != IntPtr.Zero)
        {
            var modifyStatus = SecKeychainItemModifyAttributesAndData(
                existingItem, IntPtr.Zero, (uint)passwordBytes.Length, passwordBytes);
            return modifyStatus == 0;
        }

        // Add new
        var status = SecKeychainAddGenericPassword(
            IntPtr.Zero,
            (uint)ServiceName.Length, ServiceName,
            (uint)targetName.Length, targetName,
            (uint)passwordBytes.Length, passwordBytes,
            out _);

        return status == 0;
    }

    public bool Delete(string targetName)
    {
        var status = SecKeychainFindGenericPassword(
            IntPtr.Zero,
            (uint)ServiceName.Length, ServiceName,
            (uint)targetName.Length, targetName,
            out _, out _, out var itemRef);

        if (status != 0 || itemRef == IntPtr.Zero)
            return false;

        return SecKeychainItemDelete(itemRef) == 0;
    }

    // Security.framework P/Invoke
    [DllImport("/System/Library/Frameworks/Security.framework/Security")]
    private static extern int SecKeychainFindGenericPassword(
        IntPtr keychain, uint serviceNameLength, string serviceName,
        uint accountNameLength, string accountName,
        out uint passwordLength, out IntPtr passwordData, out IntPtr itemRef);

    [DllImport("/System/Library/Frameworks/Security.framework/Security")]
    private static extern int SecKeychainAddGenericPassword(
        IntPtr keychain, uint serviceNameLength, string serviceName,
        uint accountNameLength, string accountName,
        uint passwordLength, byte[] passwordData, out IntPtr itemRef);

    [DllImport("/System/Library/Frameworks/Security.framework/Security")]
    private static extern int SecKeychainItemModifyAttributesAndData(
        IntPtr itemRef, IntPtr attrList, uint length, byte[] data);

    [DllImport("/System/Library/Frameworks/Security.framework/Security")]
    private static extern int SecKeychainItemDelete(IntPtr itemRef);

    [DllImport("/System/Library/Frameworks/Security.framework/Security")]
    private static extern int SecKeychainItemFreeContent(IntPtr attrList, IntPtr data);
}
