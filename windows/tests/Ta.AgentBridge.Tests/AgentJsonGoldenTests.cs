using System.Text;
using Ta.AgentBridge.Protocol;

namespace Ta.AgentBridge.Tests;

/// <summary>
/// JSON 字节级 parity 测试。对应 Mac 版 CLIGoldenOutputTests（Tests/TaCLITests/CLIGoldenOutputTests.swift）。
/// 断言输出字节与 Mac golden 完全一致 —— 这是 §14 风险 #40 的守门测试。
/// </summary>
public class AgentJsonGoldenTests
{
    /// <summary>Mac CLIGoldenOutputTests 锁定的 golden 字符串。</summary>
    private const string Golden =
        "{\"artifacts\":[],\"data\":{\"bridge\":\"ready\"},\"meta\":{\"cloudUploaded\":false,\"durationMs\":7},\"ok\":true,\"protocolVersion\":1,\"requestId\":\"req-1\"}";

    [Fact]
    public void 成功信封与MacGolden字节一致()
    {
        var response = AgentResponseEnvelope.Success(
            "req-1",
            AgentJsonValue.Object(("bridge", new JsonString("ready"))),
            meta: new AgentResponseMetadata(7, false));

        var actual = Encoding.UTF8.GetString(AgentJson.Encode(response));

        Assert.Equal(Golden, actual);
    }

    [Fact]
    public void 对象键按字母序排序()
    {
        // 插入顺序刻意与字母序相反，验证 sortedKeys。
        var response = AgentResponseEnvelope.Success("req-sort", AgentJsonValue.Object(
            ("zeta", new JsonInteger(1)),
            ("alpha", new JsonInteger(2)),
            ("mike", new JsonInteger(3))));

        var actual = Encoding.UTF8.GetString(AgentJson.Encode(response));

        Assert.Contains("{\"alpha\":2,\"mike\":3,\"zeta\":1}", actual);
    }

    [Fact]
    public void 不转义正向斜杠()
    {
        var response = AgentResponseEnvelope.Success("req-slash",
            AgentJsonValue.Object(("path", new JsonString("https://ta.example/a/b"))));

        var actual = Encoding.UTF8.GetString(AgentJson.Encode(response));

        // .withoutEscapingSlashes —— 斜杠必须原样出现，而非 \/。
        Assert.Contains("\"path\":\"https://ta.example/a/b\"", actual);
        Assert.DoesNotContain("\\/", actual);
    }

    [Fact]
    public void 可选属性为null时省略且artifacts恒存在()
    {
        // data/meta 均为 null，但 artifacts 必须是 []。
        var response = AgentResponseEnvelope.Failure("req-min",
            new AgentErrorPayload(AgentErrorCode.InvalidRequest, "坏请求"));

        var actual = Encoding.UTF8.GetString(AgentJson.Encode(response));

        Assert.Contains("\"artifacts\":[]", actual);
        Assert.DoesNotContain("\"data\"", actual);
        Assert.DoesNotContain("\"meta\"", actual);
        Assert.Contains("{\"code\":\"INVALID_REQUEST\",\"message\":\"坏请求\",\"retryable\":false}", actual);
    }

    [Fact]
    public void 工件日期为无小数秒的ISO8601()
    {
        var artifact = new AgentArtifact(
            "artifact_1", @"C:\Ta\capture.png", "image/png",
            width: 4, height: 3, bytes: 12, sha256: new string('a', 64),
            expiresAtUtc: new DateTime(2026, 9, 17, 12, 34, 56, DateTimeKind.Utc));

        var response = AgentResponseEnvelope.Success("req-art", artifacts: new[] { artifact });
        var actual = Encoding.UTF8.GetString(AgentJson.Encode(response));

        Assert.Contains("\"expiresAt\":\"2026-09-17T12:34:56Z\"", actual);
        // width/height 存在时写出（按字母序分列）。
        Assert.Contains("\"height\":3", actual);
        Assert.Contains("\"width\":4", actual);
    }

    [Fact]
    public void 请求往返保持camelCase与params()
    {
        var request = new AgentRequestEnvelope(
            protocolVersion: 1,
            requestId: "req-enc",
            method: AgentMethod.CaptureRegion,
            @params: new Dictionary<string, AgentJsonValue>
            {
                ["x"] = new JsonNumber(1.5),
                ["y"] = new JsonInteger(2),
            },
            client: new AgentClientInfo("ta-cli", "1.0.1"));

        var bytes = AgentJson.EncodeRequest(request);
        var decoded = AgentJson.DecodeRequest(bytes);

        Assert.Equal("req-enc", decoded.RequestId);
        Assert.Equal(AgentMethod.CaptureRegion, decoded.Method);
        Assert.Equal(1.5, decoded.ParamNumber("x"));
        Assert.Equal(2, decoded.ParamNumber("y"));
        Assert.Equal("ta-cli", decoded.Client.Name);

        // 编码后 requestId 使用 camelCase。
        var text = Encoding.UTF8.GetString(bytes);
        Assert.Contains("\"requestId\":\"req-enc\"", text);
    }
}
