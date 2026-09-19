using Ta.Core.Imaging;
using Ta.Shell.Contracts;

namespace Ta.Shell.Composition;

/// <summary>
/// <see cref="IAgentBridge"/> 的真实实现：命名管道桥（<c>\\.\pipe\Ta\agent-v1</c>）。
///
/// 对应 Mac 版 <c>TaAgentBridgeServer.start()</c>：把能力服务挂到桥上并开始监听。
/// Start 失败抛异常由 AppModel 转状态文本（与 Mac 一致）。
/// </summary>
public sealed class AgentBridgeService : IAgentBridge, IDisposable
{
    private readonly Ta.AgentBridge.AgentPipeServer _server;

    public AgentBridgeService(Ta.AgentBridge.TaAgentRequestRouter router)
    {
        _server = new Ta.AgentBridge.AgentPipeServer(router);
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        _server.Start();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync()
    {
        _server.Stop();
        return Task.CompletedTask;
    }

    public void Dispose() => _server.Dispose();
}
