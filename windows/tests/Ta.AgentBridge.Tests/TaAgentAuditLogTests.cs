using Ta.AgentBridge.Protocol;

namespace Ta.AgentBridge.Tests;

/// <summary>
/// 审计日志测试。对应 Mac 版 TaAgentAuditLog。
/// ⚠️ 产品承诺守门：写出的 JSON 不含 requestID 明文、不含 params / data 任何内容。
/// </summary>
public class TaAgentAuditLogTests
{
    private static (TaAgentAuditLog Log, string Path) CreateLog(int? maximumEntries = null)
    {
        var path = Path.Combine(Path.GetTempPath(), $"ta-audit-{Guid.NewGuid():N}.json");
        var log = maximumEntries is { } max ? new TaAgentAuditLog(path, max) : new TaAgentAuditLog(path);
        return (log, path);
    }

    private static AgentRequestEnvelope Request(string id) => new(
        protocolVersion: 1,
        requestId: id,
        method: AgentMethod.CaptureDisplay,
        @params: new Dictionary<string, AgentJsonValue> { ["secret"] = new JsonString("top-secret-payload") },
        client: new AgentClientInfo("ta-cli", "1.0.1"));

    private static AgentResponseEnvelope Response(string id, bool ok = true) =>
        ok
            ? AgentResponseEnvelope.Success(id, meta: new AgentResponseMetadata(12, cloudUploaded: false))
            : AgentResponseEnvelope.Failure(id, new AgentErrorPayload(AgentErrorCode.TargetNotFound, "找不到目标。"));

    [Fact]
    public void 记录不含requestID明文且不含params()
    {
        var (log, path) = CreateLog();
        try
        {
            log.Record(Request("req-secret-abc"), Response("req-secret-abc"));

            var text = File.ReadAllText(path);

            // requestID 只存指纹，绝不存明文。
            Assert.DoesNotContain("req-secret-abc", text);
            Assert.Contains(TaAgentAuditLog.RequestFingerprint("req-secret-abc"), text);

            // params 内容绝不写入。
            Assert.DoesNotContain("top-secret-payload", text);
            Assert.DoesNotContain("secret", text);
        }
        finally
        {
            log.Clear();
        }
    }

    [Fact]
    public void 指纹为sha256前8字节十六进制()
    {
        var fingerprint = TaAgentAuditLog.RequestFingerprint("req-1");

        Assert.StartsWith("sha256:", fingerprint);
        Assert.Equal("sha256:".Length + 16, fingerprint.Length);
        Assert.All(fingerprint["sha256:".Length..], c => Uri.IsHexDigit(c));
    }

    [Fact]
    public void 客户端字段剥离控制字符并截断到80()
    {
        var (log, path) = CreateLog();
        try
        {
            var request = new AgentRequestEnvelope(
                1, "req-1", AgentMethod.SystemStatus,
                client: new AgentClientInfo("abcd" + new string('x', 100), "v1"));
            log.Record(request, Response("req-1"));

            var entry = Assert.Single(log.Recent(1));
            // 控制字符被剥离，长度截断到 80。
            // 控制字符在区域性敏感比较中是可忽略字符，必须用序数比较才能真实断言。
            Assert.DoesNotContain("", entry.ClientName, StringComparison.Ordinal);
            Assert.Equal(80, entry.ClientName.Length);
        }
        finally
        {
            log.Clear();
        }
    }

    [Fact]
    public void 上限FIFO裁剪且recent倒序()
    {
        var (log, path) = CreateLog(maximumEntries: 3);
        try
        {
            for (var i = 0; i < 5; i++)
            {
                log.Record(Request($"req-{i}"), Response($"req-{i}"));
            }

            var recent = log.Recent(3);

            // 仅保留最新 3 条，且倒序（最新在前）。
            Assert.Equal(3, recent.Count);
            Assert.Equal(TaAgentAuditLog.RequestFingerprint("req-4"), recent[0].RequestFingerprint);
            Assert.Equal(TaAgentAuditLog.RequestFingerprint("req-3"), recent[1].RequestFingerprint);
            Assert.Equal(TaAgentAuditLog.RequestFingerprint("req-2"), recent[2].RequestFingerprint);
        }
        finally
        {
            log.Clear();
        }
    }

    [Fact]
    public void 错误码与云上传标记被记录()
    {
        var (log, path) = CreateLog();
        try
        {
            var response = AgentResponseEnvelope.Failure("req-1",
                new AgentErrorPayload(AgentErrorCode.ScreenPermissionRequired, "无权限。"));
            log.Record(Request("req-1"), response);

            var entry = Assert.Single(log.Recent(1));
            Assert.False(entry.Succeeded);
            Assert.Equal(AgentErrorCode.ScreenPermissionRequired, entry.ErrorCode);
        }
        finally
        {
            log.Clear();
        }
    }
}
