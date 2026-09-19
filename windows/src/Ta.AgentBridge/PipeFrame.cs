using System.Runtime.InteropServices;
using Ta.Core.Agent;

namespace Ta.AgentBridge;

/// <summary>
/// 同步 overlapped 帧读写（4 字节大端长度前缀 + JSON 负载，16 MiB 上限）。
///
/// ⚠️ 刻意用原生 overlapped ReadFile/WriteFile + 事件等待 + 超时/CancelIoEx，
/// 而非 FileStream 异步 IO：FILE_FLAG_OVERLAPPED 句柄上 FileStream 的异步完成通知
/// 在命名管道上不稳定（表现为挂起或被取消）。同步 overlapped 由我们自己控制
/// 完成事件与取消，最可靠。buffer 在整个 overlapped 操作期间保持固定。
/// </summary>
internal static unsafe class PipeFrame
{
    /// <summary>读一整帧。流结束返回 null。</summary>
    public static byte[]? ReadFrame(IntPtr handle, int timeoutMs)
    {
        var header = new byte[AgentFrameCodec.HeaderBytes];
        if (!ReadExact(handle, header, timeoutMs))
        {
            return null;
        }

        var length = AgentFrameCodec.PayloadLength(header);
        var payload = new byte[length];
        if (!ReadExact(handle, payload, timeoutMs))
        {
            return null;
        }

        return payload;
    }

    /// <summary>写一整帧。失败返回 false。</summary>
    public static bool WriteFrame(IntPtr handle, byte[] frame, int timeoutMs)
    {
        var offset = 0;
        while (offset < frame.Length)
        {
            if (!WriteChunk(handle, frame, offset, frame.Length - offset, timeoutMs, out var written))
            {
                return false;
            }

            offset += (int)written;
        }

        return true;
    }

    private static bool ReadExact(IntPtr handle, byte[] buffer, int timeoutMs)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            if (!ReadChunk(handle, buffer, offset, buffer.Length - offset, timeoutMs, out var read))
            {
                return false;
            }

            if (read == 0)
            {
                return false; // 流结束（对端关闭）
            }

            offset += (int)read;
        }

        return true;
    }

    private static unsafe bool ReadChunk(IntPtr handle, byte[] buffer, int offset, int count, int timeoutMs, out uint read)
    {
        read = 0;
        var evt = PipeNative.CreateEvent(IntPtr.Zero, true, false, null);
        if (evt == IntPtr.Zero)
        {
            return false;
        }

        var overlapped = PipeNative.AllocOverlapped(evt);
        try
        {
            fixed (byte* pointer = &buffer[offset])
            {
                if (PipeNative.ReadFile(handle, pointer, (uint)count, out read, overlapped))
                {
                    return true;
                }

                if (Marshal.GetLastWin32Error() != PipeNative.ERROR_IO_PENDING)
                {
                    return false;
                }

                return WaitAndComplete(handle, evt, overlapped, timeoutMs, out read);
            }
        }
        finally
        {
            PipeNative.FreeOverlapped(overlapped);
            PipeNative.CloseHandle(evt);
        }
    }

    private static unsafe bool WriteChunk(IntPtr handle, byte[] buffer, int offset, int count, int timeoutMs, out uint written)
    {
        written = 0;
        var evt = PipeNative.CreateEvent(IntPtr.Zero, true, false, null);
        if (evt == IntPtr.Zero)
        {
            return false;
        }

        var overlapped = PipeNative.AllocOverlapped(evt);
        try
        {
            fixed (byte* pointer = &buffer[offset])
            {
                if (PipeNative.WriteFile(handle, pointer, (uint)count, out written, overlapped))
                {
                    return true;
                }

                if (Marshal.GetLastWin32Error() != PipeNative.ERROR_IO_PENDING)
                {
                    return false;
                }

                return WaitAndComplete(handle, evt, overlapped, timeoutMs, out written);
            }
        }
        finally
        {
            PipeNative.FreeOverlapped(overlapped);
            PipeNative.CloseHandle(evt);
        }
    }

    private static unsafe bool WaitAndComplete(IntPtr handle, IntPtr evt, PipeNative.NativeOverlapped* overlapped, int timeoutMs, out uint transferred)
    {
        transferred = 0;
        var wait = PipeNative.WaitForSingleObject(evt, (uint)timeoutMs);
        if (wait == PipeNative.WAIT_OBJECT_0)
        {
            return PipeNative.GetOverlappedResult(handle, overlapped, out transferred, false);
        }

        // 超时：取消挂起的 IO。
        PipeNative.CancelIoEx(handle, overlapped);
        PipeNative.WaitForSingleObject(evt, PipeNative.WAIT_TIMEOUT);
        PipeNative.GetOverlappedResult(handle, overlapped, out transferred, false);
        return false;
    }
}
