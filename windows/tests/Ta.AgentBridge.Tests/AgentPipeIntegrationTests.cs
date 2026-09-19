using Ta.AgentBridge.Protocol;
using Ta.Core.Capture;
using Ta.Core.Imaging;

namespace Ta.AgentBridge.Tests;

/// <summary>
/// 真机命名管道集成测试。起服务端，用客户端连上发请求，断言收到正确响应。
/// 对应对端认证 / 帧编解码 / 路由 / 审计豁免 / 方法全集的端到端行为。
/// </summary>
public class AgentPipeIntegrationTests
{
    private sealed class FakeBackend : IAgentCaptureBackend
    {
        public AgentCaptureSnapshot Snapshot(int? frontmostProcessId) => new()
        {
            Displays = new[]
            {
                new AgentDisplayTarget { Id = 1, Frame = new RectD(0, 0, 800, 600), PixelScale = 1, IsMain = true },
            },
            Windows = Array.Empty<AgentWindowTarget>(),
            FrontmostProcessId = frontmostProcessId,
        };

        public RgbaBitmap Capture(AgentResolvedCaptureRequest request) =>
            throw new AgentCaptureException(AgentCaptureFailure.TargetNotFound);
    }

    private static (AgentPipeServer Server, AgentPipeClient Client, TaAgentAuditLog Audit) CreateBridge()
    {
        var pipeName = $@"\\.\pipe\Ta\agent-test-{Guid.NewGuid():N}";
        var artifactRoot = Path.Combine(Path.GetTempPath(), $"ta-bridge-art-{Guid.NewGuid():N}");
        var auditPath = Path.Combine(Path.GetTempPath(), $"ta-bridge-audit-{Guid.NewGuid():N}.json");

        var captureService = new TaAgentCaptureService(new FakeBackend(), () => 420);
        var artifactStore = new TaAgentArtifactStore(artifactRoot);
        var audit = new TaAgentAuditLog(auditPath);
        var policy = new TaAgentPrivacyPolicy(true, true, AgentCloudPolicy.Allow, Array.Empty<string>(), allowCaptureTa: true);
        var dependencies = new AgentCapabilityDependencies { AppVersion = () => "1.0.0-test", ScreenPermission = () => true };

        var capability = new TaAgentCapabilityService(
            captureService, artifactStore, policy, auditLog: audit, dependencies: dependencies,
            preferenceStore: new InMemoryPreferenceStore());

        var router = new TaAgentRequestRouter(request => capability.HandleAsync(request));
        var server = new AgentPipeServer(router, pipeName);
        var client = new AgentPipeClient(pipeName, timeoutMs: 8000);
        return (server, client, audit);
    }

    private static AgentRequestEnvelope Request(string id, AgentMethod method, int protocolVersion = 1) =>
        new(protocolVersion, id, method, client: new AgentClientInfo("test", "1"));

    [Fact]
    public async Task 真机管道_systemStatus返回正确响应()
    {
        var (server, client, audit) = CreateBridge();
        server.Start();
        try
        {
            var response = await client.SendAsync(Request("status-1", AgentMethod.SystemStatus));

            Assert.True(response.Ok);
            Assert.Equal("status-1", response.RequestId);
            Assert.Equal("ready", ReadString(response.Data, "bridge"));
            Assert.Equal("Ta", ReadString(response.Data, "app"));
            Assert.Equal("1.0.0-test", ReadString(response.Data, "version"));
        }
        finally
        {
            server.Stop();
            audit.Clear();
        }
    }

    [Fact]
    public async Task 真机管道_handshake短路且不被审计()
    {
        var (server, client, audit) = CreateBridge();
        server.Start();
        try
        {
            // 先发一次 status（会被审计），建立基线。
            await client.SendAsync(Request("status-base", AgentMethod.SystemStatus));
            var before = audit.Recent(100).Count;

            var response = await client.SendAsync(Request("handshake-1", AgentMethod.SystemHandshake));

            Assert.True(response.Ok);
            Assert.Equal("Ta", ReadString(response.Data, "server"));
            Assert.Equal(1L, ReadInteger(response.Data, "protocolVersion"));
            // handshake 由 router 短路，不被审计 —— 条数不变。
            Assert.Equal(before, audit.Recent(100).Count);
        }
        finally
        {
            server.Stop();
            audit.Clear();
        }
    }

    [Fact]
    public async Task 真机管道_未宣告方法返回invalidRequest()
    {
        var (server, client, audit) = CreateBridge();
        server.Start();
        try
        {
            var response = await client.SendAsync(Request("x-1", AgentMethod.CaptureInteractive));

            Assert.False(response.Ok);
            Assert.Equal(AgentErrorCode.InvalidRequest, response.Error?.Code);
            Assert.Equal("该 Agent 方法尚未支持：capture.interactive。", response.Error?.Message);
        }
        finally
        {
            server.Stop();
            audit.Clear();
        }
    }

    [Fact]
    public async Task 真机管道_协议版本不兼容()
    {
        var (server, client, audit) = CreateBridge();
        server.Start();
        try
        {
            var response = await client.SendAsync(Request("future-1", AgentMethod.SystemStatus, protocolVersion: 99));

            Assert.False(response.Ok);
            Assert.Equal(AgentErrorCode.ProtocolVersionMismatch, response.Error?.Code);
        }
        finally
        {
            server.Stop();
            audit.Clear();
        }
    }

    [Fact]
    public async Task 真机管道_并发独立请求全部成功()
    {
        var (server, client, audit) = CreateBridge();
        server.Start();
        try
        {
            var tasks = Enumerable.Range(0, 8)
                .Select(i => client.SendAsync(Request($"req-{i}", AgentMethod.SystemStatus)))
                .ToArray();

            var responses = await Task.WhenAll(tasks);

            Assert.All(responses, r => Assert.True(r.Ok));
            Assert.Equal(8, responses.Select(r => r.RequestId).Distinct().Count());
        }
        finally
        {
            server.Stop();
            audit.Clear();
        }
    }

    private static string? ReadString(AgentJsonValue? value, string key) =>
        value is JsonObject obj && obj.TryGet(key, out var v) && v is JsonString s ? s.Value : null;

    private static long? ReadInteger(AgentJsonValue? value, string key) =>
        value is JsonObject obj && obj.TryGet(key, out var v) && v is JsonInteger i ? i.Value : null;
}
