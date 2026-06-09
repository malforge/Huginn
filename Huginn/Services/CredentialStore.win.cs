using System;
using System.Runtime.InteropServices;
using System.Text;

namespace Huginn.Services;

/// <summary>
/// Read/write secrets via Windows Credential Manager (CredRead/CredWrite).
/// Secrets are stored encrypted by the OS, tied to the current user.
/// </summary>
public class WinCredentialStore : ICredentialStore
{
    private const int CredTypeGeneric = 1;
    private const int CredPersistLocalMachine = 2;

    public string? Get(string targetName)
    {
        if (!CredRead(targetName, CredTypeGeneric, 0, out var credPtr))
            return null;

        try
        {
            var cred = Marshal.PtrToStructure<CREDENTIAL>(credPtr);
            if (cred.CredentialBlobSize == 0 || cred.CredentialBlob == IntPtr.Zero)
                return null;
            return Marshal.PtrToStringUni(cred.CredentialBlob, cred.CredentialBlobSize / 2);
        }
        finally
        {
            CredFree(credPtr);
        }
    }

    public bool Set(string targetName, string secret, string? userName = null)
    {
        var secretBytes = Encoding.Unicode.GetBytes(secret);
        var cred = new CREDENTIAL
        {
            Type = CredTypeGeneric,
            TargetName = targetName,
            CredentialBlobSize = secretBytes.Length,
            CredentialBlob = Marshal.AllocHGlobal(secretBytes.Length),
            Persist = CredPersistLocalMachine,
            UserName = userName,
        };

        try
        {
            Marshal.Copy(secretBytes, 0, cred.CredentialBlob, secretBytes.Length);
            return CredWrite(ref cred, 0);
        }
        finally
        {
            Marshal.FreeHGlobal(cred.CredentialBlob);
        }
    }

    public bool Delete(string targetName)
        => CredDelete(targetName, CredTypeGeneric, 0);

    // ── P/Invoke ────────────────────────────────────────────────────────

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CredRead(string target, int type, int reserved, out IntPtr credential);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CredWrite(ref CREDENTIAL credential, int flags);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CredDelete(string target, int type, int flags);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern void CredFree(IntPtr buffer);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CREDENTIAL
    {
        public int Flags;
        public int Type;
        public string TargetName;
        public string Comment;
        public long LastWritten;
        public int CredentialBlobSize;
        public IntPtr CredentialBlob;
        public int Persist;
        public int AttributeCount;
        public IntPtr Attributes;
        public string TargetAlias;
        public string? UserName;
    }
}
