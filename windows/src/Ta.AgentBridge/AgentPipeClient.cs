using System.Runtime.InteropServices;
using Ta.AgentBridge.Protocol;
using Ta.Core.Agent;

namespace Ta.AgentBridge;

/// <summary>客户端错误。对应 Mac: TaBridgeClientError。</summary>
public sealed class AgentBridgeClientException : Exception
{
    public AgentBridgeClientException(string message, Exception? inner = null) : base(message, inner)
    {
    }
}

/// <summary>
/// Agent 命名管道客户端。对应 Mac 版 TaBridgeClient.swift。
///
/// - 默认超时 5 秒
/// - 连接重试一次（间隔 50 ms），仅对桥不可用等价错误（ERROR_FILE_NOT_FOUND /
///   ERROR_PIPE_BUSY / ERROR_PIPE_NOT_CONNECTED，对应 Mac 的 ENOENT/ECONNREFUSED）
/// - 取消 / 超时 → 关闭句柄（对应 Mac 的 shutdown(SHUT_RDWR)）
///
/// IO 用同步 overlapped ReadFile/WriteFile + 超时（见 PipeFrame 注释）。
/// </summary>
public sealed class AgentPipeClient
{
    private const int ERROR_FILE_NOT_FOUND = 2;
    private const int ERROR_PIPE_BUSY = 231;
    private const int ERROR_PIPE_NOT_CONNECTED = 233;

    public const string DefaultPipeName = @"\\.\pipe\Ta\agent-v1";

    private readonly string _pipeName;
    private readonly int _timeoutMs;

    public AgentPipeClient(string? pipeName = null, int timeoutMs = 5000)
    {
        _pipeName = pipeName ?? DefaultPipeName;
        _timeoutMs = timeoutMs > 0 ? timeoutMs : 5000;
    }

    public Task<AgentResponseEnvelope> SendAsync(AgentRequestEnvelope request, CancellationToken cancellationToken = default)
    {
        // 同步 overlapped IO 放线程池执行，异常经 Task 传播，不阻塞调用线程。
        return Task.Run(() =>
        {
            var handle = OpenWithRetry(cancellationToken);
            try
            {
                var requestFrame = AgentFrameCodec.Frame(AgentJson.EncodeRequest(request));
                if (!PipeFrame.WriteFrame(handle, requestFrame, _timeoutMs))
                {
                    throw new AgentBridgeClientException("写入桥请求失败。");
                }

                var payload = PipeFrame.ReadFrame(handle, _timeoutMs);
                if (payload is null)
                {
                    throw new AgentBridgeClientException("桥在处理完成前关闭了连接。");
                }

                return AgentJson.DecodeResponse(payload);
            }
            finally
            {
                PipeNative.CloseHandle(handle);
            }
        }, cancellationToken);
    }

    private IntPtr OpenWithRetry(CancellationToken ct)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var handle = PipeNative.CreateFile(
                _pipeName,
                PipeNative.GENERIC_READ | PipeNative.GENERIC_WRITE,
                PipeNative.FILE_SHARE_READ | PipeNative.FILE_SHARE_WRITE,
                IntPtr.Zero,
                PipeNative.OPEN_EXISTING,
                PipeNative.FILE_FLAG_OVERLAPPED,
                IntPtr.Zero);

            if (handle != PipeNative.INVALID_HANDLE_VALUE)
            {
                return handle;
            }

            var error = Marshal.GetLastWin32Error();
            if (!IsRetryable(error))
            {
                throw new AgentBridgeClientException($"无法连接 Agent 桥（错误 {error}）。");
            }

            if (attempt == 0)
            {
                if (error == ERROR_PIPE_BUSY)
                {
                    PipeNative.WaitNamedPipe(_pipeName, (uint)_timeoutMs);
                }

                if (ct.IsCancellationRequested)
                {
                    throw new OperationCanceledException();
                }

                Thread.Sleep(50);
            }
        }

        throw new AgentBridgeClientException("Agent 桥不可用。");
    }

    private static bool IsRetryable(int error) =>
        error is ERROR_FILE_NOT_FOUND or ERROR_PIPE_BUSY or ERROR_PIPE_NOT_CONNECTED;
}
