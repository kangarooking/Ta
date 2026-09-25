using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace Ta.AgentBridge;

/// <summary>
/// Win32 命名管道互操作原语。对应 Mac 版的 Unix socket + Darwin 调用。
///
/// IO 模型：overlapped（异步可取消）句柄 + 同步 overlapped ReadFile/WriteFile +
/// 事件等待 + 超时/CancelIoEx。这是命名管道最可靠的一次性消息模式 ——
/// 既支持取消（CancelIoEx），又避免 FileStream 异步完成通知在管道上的不稳定性。
/// </summary>
internal static partial class PipeNative
{
    internal const uint PIPE_ACCESS_DUPLEX = 0x00000003;
    internal const uint FILE_FLAG_OVERLAPPED = 0x40000000;
    internal const uint PIPE_TYPE_BYTE = 0x00000000;
    internal const uint PIPE_READMODE_BYTE = 0x00000000;
    internal const uint PIPE_WAIT = 0x00000000;
    internal const uint PIPE_UNLIMITED_INSTANCES = 255;

    internal const uint GENERIC_READ = 0x80000000;
    internal const uint GENERIC_WRITE = 0x40000000;
    internal const uint FILE_SHARE_READ = 0x00000001;
    internal const uint FILE_SHARE_WRITE = 0x00000002;
    internal const uint OPEN_EXISTING = 3;

    internal const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    internal const uint TOKEN_QUERY = 0x0008;
    internal const int TokenUser = 1;

    internal const uint ERROR_IO_PENDING = 997;
    internal const uint ERROR_PIPE_CONNECTED = 535;
    internal const uint ERROR_OPERATION_ABORTED = 995;
    internal const uint WAIT_OBJECT_0 = 0;
    internal const uint WAIT_TIMEOUT = 0x00000102;

    internal static readonly IntPtr INVALID_HANDLE_VALUE = new(-1);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateNamedPipeW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    internal static partial IntPtr CreateNamedPipe(
        string pipeName, uint openMode, uint pipeMode, uint maxInstances,
        uint outBufferSize, uint inBufferSize, uint defaultTimeout, IntPtr securityAttributes);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool ConnectNamedPipe(IntPtr handle, IntPtr overlapped);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DisconnectNamedPipe(IntPtr handle);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    internal static partial IntPtr CreateFile(
        string pipeName, uint desiredAccess, uint shareMode, IntPtr securityAttributes,
        uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [LibraryImport("kernel32.dll", EntryPoint = "WaitNamedPipeW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool WaitNamedPipe(string pipeName, uint timeOut);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetNamedPipeClientProcessId(IntPtr pipe, out uint clientProcessId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial IntPtr OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetTokenInformation(IntPtr tokenHandle, int tokenInformationClass, IntPtr tokenInformation, uint tokenInformationLength, out uint returnLength);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool EqualSid(IntPtr sid1, IntPtr sid2);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial IntPtr LocalFree(IntPtr handle);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CloseHandle(IntPtr handle);

    // ── overlapped IO ───────────────────────────────────────────────

    [LibraryImport("kernel32.dll", EntryPoint = "CreateEventW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    internal static partial IntPtr CreateEvent(IntPtr eventAttributes, [MarshalAs(UnmanagedType.Bool)] bool manualReset, [MarshalAs(UnmanagedType.Bool)] bool initialState, string? name);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    [LibraryImport("kernel32.dll", EntryPoint = "ConnectNamedPipe", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static unsafe partial bool ConnectNamedPipeOverlapped(IntPtr handle, NativeOverlapped* overlapped);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static unsafe partial bool CancelIoEx(IntPtr handle, NativeOverlapped* overlapped);

    /// <summary>
    /// 取消句柄上**所有**挂起的 IO。
    ///
    /// 提供一个非 unsafe 入口：lpOverlapped 传 NULL 时 CancelIoEx 取消该句柄的
    /// 全部挂起 IO。调用方（如 AgentPipeServer.Stop）本身没有 unsafe 上下文，
    /// 直接写 <c>CancelIoEx(handle, null)</c> 会触发 CS0214 —— 那把 unsafe
    /// 收敛到这一行里，比给每个调用方法都标 unsafe 更干净。
    /// </summary>
    internal static bool CancelAllIo(IntPtr handle)
    {
        unsafe
        {
            return CancelIoEx(handle, null);
        }
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static unsafe partial bool GetOverlappedResult(IntPtr handle, NativeOverlapped* overlapped, out uint bytesTransferred, [MarshalAs(UnmanagedType.Bool)] bool wait);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static unsafe partial bool ReadFile(IntPtr handle, byte* buffer, uint numberOfBytesToRead, out uint numberOfBytesRead, NativeOverlapped* overlapped);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static unsafe partial bool WriteFile(IntPtr handle, byte* buffer, uint numberOfBytesToWrite, out uint numberOfBytesWritten, NativeOverlapped* overlapped);

    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct NativeOverlapped
    {
        public IntPtr Internal;
        public IntPtr InternalHigh;
        public int Offset;
        public int OffsetHigh;
        public IntPtr EventHandle;
    }

    /// <summary>分配带事件句柄的 OVERLAPPED。调用方负责 FreeOverlapped。</summary>
    internal static unsafe NativeOverlapped* AllocOverlapped(IntPtr eventHandle)
    {
        var overlapped = (NativeOverlapped*)Marshal.AllocHGlobal(sizeof(NativeOverlapped));
        overlapped->Internal = IntPtr.Zero;
        overlapped->InternalHigh = IntPtr.Zero;
        overlapped->Offset = 0;
        overlapped->OffsetHigh = 0;
        overlapped->EventHandle = eventHandle;
        return overlapped;
    }

    internal static unsafe void FreeOverlapped(NativeOverlapped* overlapped)
    {
        Marshal.FreeHGlobal((IntPtr)overlapped);
    }

    // ── 非 unsafe 的 IntPtr 封装 ──────────────────────────────────
    //
    // 为什么需要：C# **不允许给 async 方法标 unsafe**，因此像
    // AgentPipeServer.ConnectAsync 那样「跨越 await 持有 OVERLAPPED」的写法，
    // 无法在自身方法体里直接使用 NativeOverlapped* —— 光开 AllowUnsafeBlocks
    // 也救不了，报错仍是 CS0214。
    //
    // 解法是把指针收敛成 IntPtr：unsafe 只出现在下面这几个一行封装里，
    // 调用方（含 async 方法）全程只见 IntPtr，与句柄的处理方式一致。

    /// <summary>分配 OVERLAPPED，以 IntPtr 返回。调用方负责 <see cref="FreeOverlappedPtr"/>。</summary>
    internal static IntPtr AllocOverlappedPtr(IntPtr eventHandle)
    {
        unsafe
        {
            return (IntPtr)AllocOverlapped(eventHandle);
        }
    }

    internal static bool ConnectNamedPipeOverlappedPtr(IntPtr handle, IntPtr overlapped)
    {
        unsafe
        {
            return ConnectNamedPipeOverlapped(handle, (NativeOverlapped*)overlapped);
        }
    }

    internal static bool GetOverlappedResultPtr(IntPtr handle, IntPtr overlapped, out uint bytesTransferred, bool wait)
    {
        unsafe
        {
            return GetOverlappedResult(handle, (NativeOverlapped*)overlapped, out bytesTransferred, wait);
        }
    }

    internal static void FreeOverlappedPtr(IntPtr overlapped)
    {
        if (overlapped == IntPtr.Zero)
        {
            return;
        }

        unsafe
        {
            FreeOverlapped((NativeOverlapped*)overlapped);
        }
    }

    // ── SDDL / 安全 ─────────────────────────────────────────────────

    private const uint SDDL_REVISION_1 = 1;

    [LibraryImport("advapi32.dll", EntryPoint = "ConvertStringSecurityDescriptorToSecurityDescriptorW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ConvertStringSecurityDescriptorToSecurityDescriptor(string stringSecurityDescriptor, uint revision, out IntPtr securityDescriptor, out uint securityDescriptorSize);

    [LibraryImport("advapi32.dll", EntryPoint = "ConvertStringSidToSidW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ConvertStringSidToSid(string stringSid, out IntPtr sid);

    internal static string CurrentUserSidString() =>
        WindowsIdentity.GetCurrent().User?.Value
        ?? throw new InvalidOperationException("无法取得当前用户 SID。");

    /// <summary>
    /// 生成「仅当前用户可访问」的 SECURITY_ATTRIBUTES。对应参考文档 §11.1 的
    /// <c>D:P(A;;FA;;;&lt;user SID&gt;)</c> 纵深防御。调用方负责 FreeSecurityAttributes。
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributesNative
    {
        public uint nLength;
        public IntPtr lpSecurityDescriptor;
        public int bInheritHandle;
    }

    internal static IntPtr BuildCurrentUserSecurityAttributes()
    {
        var sddl = $"D:P(A;;FA;;;{CurrentUserSidString()})";
        if (!ConvertStringSecurityDescriptorToSecurityDescriptor(sddl, SDDL_REVISION_1, out var descriptor, out _))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法构建管道安全描述符。");
        }

        var attributes = Marshal.AllocHGlobal(Marshal.SizeOf<SecurityAttributesNative>());
        try
        {
            var value = new SecurityAttributesNative
            {
                nLength = (uint)Marshal.SizeOf<SecurityAttributesNative>(),
                lpSecurityDescriptor = descriptor,
                bInheritHandle = 0,
            };
            Marshal.StructureToPtr(value, attributes, fDeleteOld: false);
        }
        catch
        {
            LocalFree(descriptor);
            Marshal.FreeHGlobal(attributes);
            throw;
        }

        return attributes;
    }

    internal static void FreeSecurityAttributes(IntPtr attributes)
    {
        if (attributes == IntPtr.Zero)
        {
            return;
        }

        var descriptor = Marshal.ReadIntPtr(attributes, IntPtr.Size);
        Marshal.FreeHGlobal(attributes);
        if (descriptor != IntPtr.Zero)
        {
            LocalFree(descriptor);
        }
    }

    internal static IntPtr CurrentUserSidPointer()
    {
        if (!ConvertStringSidToSid(CurrentUserSidString(), out var pointer))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法转换当前用户 SID。");
        }

        return pointer;
    }
}
