using System.ComponentModel;
using Ta.CLI.Agent;

namespace Ta.CLI.Transport;

/// <summary>
/// 桥传输层错误。对应 Mac: TaBridgeClientError
/// （Sources/TaAgentClient/TaBridgeClient.swift:5-20）。
/// </summary>
public enum TaBridgeClientError
{
    /// <summary>路径/管道名过长。</summary>
    SocketPathTooLong,

    /// <summary>某个操作失败（携带操作名与平台错误码）。</summary>
    SocketFailure,

    /// <summary>对端在返回完整响应前关闭了连接。</summary>
    ConnectionClosed,
}

/// <summary>
/// 桥客户端异常。CLI 入口（Program.cs）将其映射为退出码 4（BRIDGE_UNAVAILABLE）。
/// 对应 Mac: TaBridgeClientError 经 TaCLI.swift:40-42 映射。
/// </summary>
public sealed class TaBridgeClientException : Exception
{
    public TaBridgeClientException(
        TaBridgeClientError error,
        string message,
        string? operation = null,
        int code = 0)
        : base(message)
    {
        Error = error;
        Operation = operation;
        Code = code;
    }

    public TaBridgeClientError Error { get; }

    /// <summary>失败操作名（如 "connect"），对应 Mac: socketFailure(operation:code:)。</summary>
    public string? Operation { get; }

    /// <summary>
    /// 平台错误码。Unix 侧为 errno（ENOENT=2 / ECONNREFUSED=111）；
    /// Windows 侧为 Win32 错误码：ERROR_FILE_NOT_FOUND=2、ERROR_PIPE_NOT_CONNECTED=233、WSAECONNREFUSED=10061。
    /// 对应《Windows移植参考文档》§11.1 的错误码映射约定。
    /// </summary>
    public int Code { get; }

    /// <summary>无法识别的平台错误码（连接失败但异常链里提取不到 Win32 码）。</summary>
    public const int UnknownCode = -1;

    /// <summary>
    /// 是否属于「桥不可用」（等价于 macOS 的 ENOENT / ECONNREFUSED 分支）。
    /// 只有 connect 阶段的这些错误才触发自动拉起 + 重试 ——
    /// 对应 Mac: CLIRunner.isUnavailable（Sources/TaCLI/CLICommands.swift:150-155）。
    ///
    /// ⚠️ Windows 侧额外把 <see cref="UnknownCode"/>（识别不到错误码的连接失败）也归入不可用：
    /// .NET 的命名管道异常链不一定携带可提取的 Win32 码，而「连不上桥」的处置方式
    /// （拉起拓 + 轮询重试，最终 BRIDGE_UNAVAILABLE）在两种情形下是相同的。
    /// </summary>
    public bool IsBridgeUnavailable =>
        Error == TaBridgeClientError.SocketFailure
        && Operation == "connect"
        && (Code is 2 /* ERROR_FILE_NOT_FOUND ≈ ENOENT */
            or 233 /* ERROR_PIPE_NOT_CONNECTED */
            or 10061 /* WSAECONNREFUSED ≈ ECONNREFUSED */
            or UnknownCode);

    /// <summary>
    /// 从底层异常提取 Win32 错误码，提取不到返回 <see cref="UnknownCode"/>。
    /// 覆盖 .NET 包装管道错误的两种形态：
    ///   1. 异常链中的 Win32Exception（NativeErrorCode）；
    ///   2. IOException 系异常把 Win32 码编进 HRESULT（FACILITY_WIN32=7）。
    /// </summary>
    internal static int Win32CodeOf(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is Win32Exception { NativeErrorCode: > 0 } win32)
            {
                return win32.NativeErrorCode;
            }
        }
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is IOException && (current.HResult & 0x7FFF0000) == 0x00070000)
            {
                return current.HResult & 0xFFFF;
            }
        }
        return UnknownCode;
    }
}

/// <summary>
/// 桥客户端抽象。解析/渲染/编排逻辑只依赖本接口，与传输实现完全分离。
///
/// 期望 Ta.AgentBridge 暴露的等价签名（届时把 <see cref="NamedPipeBridgeClient"/>
/// 换成 Ta.AgentBridge 的客户端即可，CLI 其余部分零改动）：
///
///   public interface ITaBridgeClient {
///       Task&lt;AgentResponseEnvelope&gt; SendAsync(AgentRequestEnvelope request, CancellationToken ct);
///   }
///   // 命名空间建议：Ta.AgentBridge（或 Ta.AgentBridge.Transport），
///   // 信封/枚举类型届时统一到 Ta.AgentBridge.Agent 命名空间后做映射适配器。
/// </summary>
public interface ITaBridgeClient
{
    Task<AgentResponseEnvelope> SendAsync(AgentRequestEnvelope request, CancellationToken cancellationToken);
}

/// <summary>客户端工厂：每次请求新建一个连接（与 Mac 版 TaBridgeClient 每次新建 socket 一致）。</summary>
public interface ITaBridgeClientFactory
{
    ITaBridgeClient Create(string socket, double timeoutSeconds);
}
