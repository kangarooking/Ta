using System.Buffers.Binary;
using System.IO.Pipes;
using Ta.CLI.Agent;
using Ta.Core.Agent;

namespace Ta.CLI.Transport;

/// <summary>
/// 命名管道桥客户端（Windows 传输实现）。
///
/// 逐项对应 Mac: TaBridgeClient + TaUnixSocketIO
/// （Sources/TaAgentClient/TaBridgeClient.swift:1-217）：
///   - 帧格式复用 Ta.Core.Agent.AgentFrameCodec（4 字节大端长度 + JSON 负载，16 MiB 上限）；
///   - 连接失败重试一次（间隔 50ms），仅对「桥不可用」类错误码；
///   - 读写携带 CancellationToken —— SIGINT（Console.CancelKeyPress）取消挂起的管道操作，
///     对应 Mac 的 operation.cancel() → shutdown(fd, SHUT_RDWR)；
///   - 请求超时对应 Mac 的 SO_RCVTIMEO/SO_SNDTIMEO（此处对连接/读写整体生效）。
///
/// ⚠️ 这是 Ta.AgentBridge 就绪前的本地实现，保持最小可用；接口对齐后即可整体替换。
/// 管道名约定（《Windows移植参考文档》§11.1）：Mac 的
/// ~/Library/Application Support/Ta/agent-v1.sock → Windows 的 \\.\pipe\Ta\agent-v1。
/// </summary>
public sealed class NamedPipeBridgeClient : ITaBridgeClient
{
    private readonly string _pipeName;
    private readonly TimeSpan _timeout;

    /// <param name="socket">
    /// 管道名或完整管道路径。接受 "\\.\pipe\Ta\agent-v1"、"Ta\agent-v1" 或 "agent-v1"。
    /// </param>
    public NamedPipeBridgeClient(string socket, double timeoutSeconds)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(socket);
        _pipeName = NormalizePipeName(socket);
        _timeout = TimeSpan.FromSeconds(timeoutSeconds > 0 ? timeoutSeconds : 10);
    }

    /// <summary>完整的 Windows 管道路径（用于错误信息展示）。</summary>
    public string PipePath => $@"\\.\pipe\{_pipeName}";

    /// <summary>
    /// 管道名规范化：去掉 "\\.\pipe\" 前缀。对应 Mac 侧 URL 文件路径到 Unix socket 路径的映射。
    /// </summary>
    public static string NormalizePipeName(string socket)
    {
        var trimmed = socket.Trim();
        const string prefix = @"\\.\pipe\";
        if (trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[prefix.Length..];
        }
        return trimmed.TrimStart('\\', '/');
    }

    public async Task<AgentResponseEnvelope> SendAsync(
        AgentRequestEnvelope request,
        CancellationToken cancellationToken)
    {
        var payload = TaJson.WriteRequest(request);
        var frame = AgentFrameCodec.Frame(payload);

        // 对应 Mac: connectWithSingleRetry（TaBridgeClient.swift:134-161）。
        var stream = await ConnectWithSingleRetryAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(_timeout);
            var ct = timeoutCts.Token;

            await WriteAllAsync(stream, frame, ct).ConfigureAwait(false);
            var responseBytes = await ReadFrameAsync(stream, ct).ConfigureAwait(false);
            return AgentEnvelopeJson.Decode(responseBytes);
        }
        finally
        {
            await stream.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>对应 Mac: connectWithSingleRetry —— 最多两次尝试，仅「桥不可用」码重试。</summary>
    private async Task<NamedPipeClientStream> ConnectWithSingleRetryAsync(CancellationToken cancellationToken)
    {
        var lastCode = TaBridgeClientException.UnknownCode;
        for (var attempt = 0; attempt <= 1; attempt++)
        {
            var stream = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            try
            {
                await stream.ConnectAsync(_timeout, cancellationToken).ConfigureAwait(false);
                return stream;
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                lastCode = TaBridgeClientException.Win32CodeOf(exception);
                await stream.DisposeAsync().ConfigureAwait(false);

                var unavailable = lastCode is 2 /* ERROR_FILE_NOT_FOUND ≈ ENOENT */
                    or 233 /* ERROR_PIPE_NOT_CONNECTED */
                    or 10061 /* WSAECONNREFUSED ≈ ECONNREFUSED */
                    or TaBridgeClientException.UnknownCode;
                if (attempt == 0 && unavailable)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(50), cancellationToken).ConfigureAwait(false);
                    continue;
                }

                throw new TaBridgeClientException(
                    TaBridgeClientError.SocketFailure,
                    ConnectFailureMessage(lastCode),
                    operation: "connect",
                    code: lastCode);
            }
        }
        throw new TaBridgeClientException(
            TaBridgeClientError.SocketFailure,
            ConnectFailureMessage(lastCode),
            operation: "connect",
            code: lastCode);
    }

    /// <summary>连接失败信息。对应 Mac: "Bridge connect 失败（errno \(code)：\(strerror(code))）"。</summary>
    private string ConnectFailureMessage(int code) => code switch
    {
        TaBridgeClientException.UnknownCode => $"Bridge connect 失败（无法连接到命名管道 {PipePath}）",
        _ => $"Bridge connect 失败（Win32 错误 {code}：{Describe(code)}）管道 {PipePath}",
    };

    /// <summary>对应 Mac: TaUnixSocketIO.writeAll（TaBridgeClient.swift:69-84）。</summary>
    /// <remarks>
    /// Stream.WriteAsync 的契约是「写完整个缓冲区」，因此一次调用即可覆盖整帧
    /// （macOS 侧的 send 循环在 Windows 侧由 PipeStream 内部完成）。
    /// </remarks>
    private static async Task WriteAllAsync(PipeStream stream, byte[] data, CancellationToken ct)
    {
        await stream.WriteAsync(data, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    /// <summary>对应 Mac: TaUnixSocketIO.readPayload + readExactly（TaBridgeClient.swift:38-67）。</summary>
    private static async Task<byte[]> ReadFrameAsync(PipeStream stream, CancellationToken ct)
    {
        var header = new byte[AgentFrameCodec.HeaderBytes];
        await ReadExactlyAsync(stream, header, ct).ConfigureAwait(false);
        var length = AgentFrameCodec.PayloadLength(header);

        var payload = new byte[length];
        if (length > 0)
        {
            await ReadExactlyAsync(stream, payload, ct).ConfigureAwait(false);
        }
        return payload;
    }

    private static async Task ReadExactlyAsync(PipeStream stream, byte[] buffer, CancellationToken ct)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset), ct).ConfigureAwait(false);
            if (read == 0)
            {
                // 对应 Mac: TaBridgeClientError.connectionClosed —— "Bridge 在返回完整响应前关闭了连接。"
                throw new TaBridgeClientException(
                    TaBridgeClientError.ConnectionClosed,
                    "Bridge 在返回完整响应前关闭了连接。");
            }
            offset += read;
        }
    }

    /// <summary>常见 Win32 错误码的中文描述（尽力而为，取不到就用格式化消息）。</summary>
    private static string Describe(int code) => code switch
    {
        0 => "未知错误",
        2 => "系统找不到指定的文件。",
        53 => "找不到网络路径。",
        233 => "管道尚未连接。",
        10061 => "远程主机拒绝连接。",
        _ => new System.ComponentModel.Win32Exception(code).Message,
    };
}
