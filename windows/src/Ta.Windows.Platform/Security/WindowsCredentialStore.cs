using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Security.Cryptography;

namespace Ta.Windows.Platform.Security;

public interface ISecretStore
{
    string? Read(string targetName);

    void Write(string targetName, string secret);

    void Delete(string targetName);
}

public sealed class WindowsCredentialStore : ISecretStore
{
    private const int CredentialTypeGeneric = 1;
    private const int CredentialPersistLocalMachine = 2;
    private const int ErrorNotFound = 1168;

    public string? Read(string targetName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetName);
        if (!CredentialNativeMethods.CredRead(
                targetName,
                CredentialTypeGeneric,
                0,
                out var credentialPointer))
        {
            var error = Marshal.GetLastWin32Error();
            if (error == ErrorNotFound)
            {
                return null;
            }

            throw new Win32Exception(error, "无法从 Windows 凭据管理器读取视觉 API Key。");
        }

        try
        {
            var credential = Marshal.PtrToStructure<NativeCredential>(credentialPointer);
            if (credential.CredentialBlob == nint.Zero || credential.CredentialBlobSize == 0)
            {
                return string.Empty;
            }

            var bytes = new byte[credential.CredentialBlobSize];
            Marshal.Copy(credential.CredentialBlob, bytes, 0, bytes.Length);
            try
            {
                return Encoding.UTF8.GetString(bytes);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(bytes);
            }
        }
        finally
        {
            CredentialNativeMethods.CredFree(credentialPointer);
        }
    }

    public void Write(string targetName, string secret)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetName);
        ArgumentException.ThrowIfNullOrWhiteSpace(secret);
        var bytes = Encoding.UTF8.GetBytes(secret);
        if (bytes.Length > 2560)
        {
            throw new ArgumentOutOfRangeException(nameof(secret), "API Key 超过 Windows 凭据管理器允许的长度。");
        }

        var blob = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, blob, bytes.Length);
            var credential = new NativeCredential
            {
                Type = CredentialTypeGeneric,
                TargetName = targetName,
                CredentialBlobSize = bytes.Length,
                CredentialBlob = blob,
                Persist = CredentialPersistLocalMachine,
                UserName = Environment.UserName,
            };
            if (!CredentialNativeMethods.CredWrite(ref credential, 0))
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "无法把视觉 API Key 保存到 Windows 凭据管理器。");
            }
        }
        finally
        {
            var zeros = new byte[bytes.Length];
            Marshal.Copy(zeros, 0, blob, zeros.Length);
            CryptographicOperations.ZeroMemory(bytes);
            Marshal.FreeHGlobal(blob);
        }
    }

    public void Delete(string targetName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetName);
        if (CredentialNativeMethods.CredDelete(targetName, CredentialTypeGeneric, 0))
        {
            return;
        }

        var error = Marshal.GetLastWin32Error();
        if (error != ErrorNotFound)
        {
            throw new Win32Exception(error, "无法从 Windows 凭据管理器删除旧的视觉 API Key。");
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeCredential
    {
        internal int Flags;
        internal int Type;
        internal string TargetName;
        internal string? Comment;
        internal System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        internal int CredentialBlobSize;
        internal nint CredentialBlob;
        internal int Persist;
        internal int AttributeCount;
        internal nint Attributes;
        internal string? TargetAlias;
        internal string UserName;
    }

    private static class CredentialNativeMethods
    {
        [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CredRead(
            string target,
            int type,
            int reservedFlag,
            out nint credentialPointer);

        [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CredWrite(ref NativeCredential credential, int flags);

        [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CredDelete(string target, int type, int flags);

        [DllImport("advapi32.dll")]
        internal static extern void CredFree(nint credentialPointer);
    }
}
