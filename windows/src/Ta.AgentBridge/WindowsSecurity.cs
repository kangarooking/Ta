using System.Runtime.InteropServices;
using System.Security.Principal;

namespace Ta.AgentBridge;

/// <summary>
/// Windows ACL 助手。对应 Mac 版的 POSIX 权限 chmod 0700/0600。
/// 用受保护 DACL（禁继承）把目录/文件限制为仅当前用户可访问。
///
/// ⚠️ 通过底层 SetNamedSecurityInfoW **仅设置 DACL 段**（不含 SACL / Owner / Group）。
/// 这是刻意的：.NET 高层的 DirectoryInfo.SetAccessControl 默认 includeSections=All，
/// 会尝试写 SACL 从而需要 SeSecurityPrivilege 特权，在普通用户令牌下必抛
/// PrivilegeNotHeldException。仅设 DACL 对自有对象无需任何特权。
/// </summary>
internal static class WindowsSecurity
{
    private const int SE_FILE_OBJECT = 1;
    private const int DACL_SECURITY_INFORMATION = 0x00000004;
    private const int PROTECTED_DACL_SECURITY_INFORMATION = unchecked((int)0x80000000);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint SetNamedSecurityInfoW(
        string objectName,
        int objectType,
        int securityInfo,
        IntPtr sidOwner,
        IntPtr sidGroup,
        IntPtr dacl,
        IntPtr sacl);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptorW(
        string stringSecurityDescriptor,
        uint revision,
        out IntPtr securityDescriptor,
        out uint securityDescriptorSize);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetSecurityDescriptorDacl(
        IntPtr securityDescriptor,
        out bool daclPresent,
        out IntPtr dacl,
        out bool daclDefaulted);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr handle);

    private static string CurrentUserSidString() =>
        WindowsIdentity.GetCurrent().User?.Value
        ?? throw new InvalidOperationException("无法取得当前用户 SID。");

    /// <summary>把目录限制为仅当前用户可访问（对应 POSIX 0700，含子对象继承）。</summary>
    public static void RestrictDirectoryToCurrentUser(string path)
    {
        // OICI = 对象继承 + 容器继承，作用于子目录/文件。
        ApplyProtectedDacl(path, $"D:P(A;OICI;FA;;;{CurrentUserSidString()})");
    }

    /// <summary>把文件限制为仅当前用户可访问（对应 POSIX 0600）。</summary>
    public static void RestrictFileToCurrentUser(string path)
    {
        ApplyProtectedDacl(path, $"D:P(A;;FA;;;{CurrentUserSidString()})");
    }

    private static void ApplyProtectedDacl(string path, string sddl)
    {
        if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(sddl, 1, out var descriptor, out _))
        {
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "无法构建安全描述符。");
        }

        try
        {
            GetSecurityDescriptorDacl(descriptor, out _, out var dacl, out _);
            var result = SetNamedSecurityInfoW(
                path,
                SE_FILE_OBJECT,
                DACL_SECURITY_INFORMATION | PROTECTED_DACL_SECURITY_INFORMATION,
                IntPtr.Zero,
                IntPtr.Zero,
                dacl,
                IntPtr.Zero);
            if (result != 0)
            {
                throw new System.ComponentModel.Win32Exception((int)result, "无法设置文件 ACL。");
            }
        }
        finally
        {
            LocalFree(descriptor);
        }
    }
}
