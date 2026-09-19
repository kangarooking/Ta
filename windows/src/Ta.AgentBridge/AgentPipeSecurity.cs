using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Ta.AgentBridge;

/// <summary>
/// 命名管道对端认证。对应 Mac 版 TaAgentBridgeConnection.swift:14-19 的 getpeereid 校验。
///
/// Mac 用 getpeereid(fileDescriptor) 比对 geteuid()，失败即静默关闭、不返回任何帧。
/// Windows 等价：用 GetNamedPipeClientProcessId 取得客户端 PID，打开其进程 token，
/// 比对 TokenUser 的 SID 是否等于当前用户 —— 即「同一用户」的语义（UID 对等物）。
/// 叠加 SDDL（仅当前用户可打开管道）作为纵深防御（见 PipeNative.BuildCurrentUserSecurityAttributes）。
///
/// ⚠️ 失败即静默关闭连接，**不返回任何帧**（连错误信封都没有）—— 返回 INVALID_REQUEST
/// 会泄露信息并破坏客户端预期（客户端把裸关闭视为 connectionClosed）。
/// </summary>
internal static class AgentPipeSecurity
{
    /// <summary>校验对端是否属于当前用户。返回 false 时应静默关闭连接。</summary>
    public static bool IsPeerAuthorized(IntPtr pipeHandle)
    {
        if (!PipeNative.GetNamedPipeClientProcessId(pipeHandle, out var clientProcessId) || clientProcessId == 0)
        {
            return false;
        }

        var processHandle = PipeNative.OpenProcess(PipeNative.PROCESS_QUERY_LIMITED_INFORMATION, false, clientProcessId);
        if (processHandle == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            if (!PipeNative.OpenProcessToken(processHandle, PipeNative.TOKEN_QUERY, out var tokenHandle))
            {
                return false;
            }

            try
            {
                return TokenUserMatchesCurrent(tokenHandle);
            }
            finally
            {
                PipeNative.CloseHandle(tokenHandle);
            }
        }
        finally
        {
            PipeNative.CloseHandle(processHandle);
        }
    }

    private static bool TokenUserMatchesCurrent(IntPtr tokenHandle)
    {
        // 首次调用取所需长度。
        PipeNative.GetTokenInformation(tokenHandle, PipeNative.TokenUser, IntPtr.Zero, 0, out var needed);
        if (needed == 0)
        {
            return false;
        }

        var buffer = Marshal.AllocHGlobal((int)needed);
        try
        {
            if (!PipeNative.GetTokenInformation(tokenHandle, PipeNative.TokenUser, buffer, needed, out _))
            {
                return false;
            }

            // TOKEN_USER 结构首字段是 SID_AND_ATTRIBUTES.User.Sid（指针）。
            var clientSid = Marshal.ReadIntPtr(buffer);
            if (clientSid == IntPtr.Zero)
            {
                return false;
            }

            var currentSid = PipeNative.CurrentUserSidPointer();
            try
            {
                return PipeNative.EqualSid(clientSid, currentSid);
            }
            finally
            {
                PipeNative.LocalFree(currentSid);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }
}
