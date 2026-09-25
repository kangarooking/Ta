using System.Diagnostics;
using Ta.AgentBridge.Protocol;

namespace Ta.AgentBridge;

/// <summary>
/// 请求路由。对应 Mac 版 TaAgentRequestRouter.swift:4-44。
///
/// ⚠️ system.handshake 由本路由**直接应答**，不走 capability service，
/// 因此不被审计（对应 Mac 的 router 短路）。协议版本不兼容也在此拦截。
/// </summary>
public sealed class TaAgentRequestRouter
{
    private readonly Func<AgentRequestEnvelope, Task<AgentResponseEnvelope>> _handler;

    public TaAgentRequestRouter(Func<AgentRequestEnvelope, Task<AgentResponseEnvelope>> handler)
    {
        _handler = handler;
    }

    public async Task<AgentResponseEnvelope> RouteAsync(AgentRequestEnvelope request)
    {
        try
        {
            request.ValidateProtocolVersion();
        }
        catch (AgentProtocolException error)
        {
            return AgentResponseEnvelope.Failure(request.RequestId, new AgentErrorPayload(
                AgentErrorCode.ProtocolVersionMismatch,
                $"Bridge 协议版本不兼容：收到 {error.Received}，当前支持 {error.Supported}。",
                "请升级拓 App 或调用端。"));
        }
        catch
        {
            return AgentResponseEnvelope.Failure(request.RequestId, new AgentErrorPayload(
                AgentErrorCode.InvalidRequest, "请求协议无效。", retryable: false));
        }

        if (request.Method == AgentMethod.SystemHandshake)
        {
            return Handshake(request.RequestId);
        }

        return await _handler(request).ConfigureAwait(false);
    }

    /// <summary>
    /// handshake 应答（router 短路）。对应 Mac 版 router 的 handshake 分支。
    /// 任务契约要求此应答带 meta（cloudUploaded:false, durationMs:&lt;n&gt;）。
    /// </summary>
    private static AgentResponseEnvelope Handshake(string requestId)
    {
        var stopwatch = Stopwatch.StartNew();
        var response = AgentResponseEnvelope.Success(
            requestId,
            AgentJsonValue.Object(
                ("protocolVersion", new JsonInteger(AgentProtocol.CurrentVersion)),
                ("server", new JsonString("Ta"))));
        stopwatch.Stop();

        return response with { Meta = new AgentResponseMetadata((int)stopwatch.ElapsedMilliseconds, false) };
    }
}
