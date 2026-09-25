using System.Text.Json;

namespace Ta.CLI.Agent;

/// <summary>
/// 响应信封解码。对应 Mac: TaBridgeClient.sendSynchronously 中的
/// AgentJSONCoding.decoder().decode(AgentResponseEnvelope.self, …)
/// （Sources/TaAgentClient/TaBridgeClient.swift:127）。
///
/// 读取侧不需要字节级控制，因此直接使用 System.Text.Json 的宽松解析；
/// 解析失败抛出的 JsonException 在入口处映射为 INTERNAL_ERROR（与 Mac 一致）。
/// </summary>
public static class AgentEnvelopeJson
{
    public static AgentResponseEnvelope Decode(ReadOnlyMemory<byte> utf8Payload)
    {
        using var document = JsonDocument.Parse(utf8Payload);
        var root = document.RootElement;

        var protocolVersion = root.TryGetProperty("protocolVersion", out var pv)
            ? pv.GetInt32()
            : AgentProtocol.CurrentVersion;
        var requestID = root.TryGetProperty("requestId", out var rid)
            ? rid.GetString() ?? string.Empty
            : string.Empty;
        var ok = root.TryGetProperty("ok", out var okElement) && okElement.GetBoolean();

        TaJsonValue? data = null;
        if (root.TryGetProperty("data", out var dataElement) && dataElement.ValueKind != JsonValueKind.Null)
        {
            data = Convert(dataElement);
        }

        var artifacts = new List<AgentArtifact>();
        if (root.TryGetProperty("artifacts", out var artifactsElement) && artifactsElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in artifactsElement.EnumerateArray())
            {
                artifacts.Add(new AgentArtifact(
                    Id: item.TryGetProperty("id", out var id) ? id.GetString() ?? string.Empty : string.Empty,
                    Path: item.TryGetProperty("path", out var path) ? path.GetString() ?? string.Empty : string.Empty,
                    MimeType: item.TryGetProperty("mimeType", out var mime) ? mime.GetString() ?? string.Empty : string.Empty,
                    Bytes: item.TryGetProperty("bytes", out var bytes) ? bytes.GetInt32() : 0,
                    Sha256: item.TryGetProperty("sha256", out var sha) ? sha.GetString() ?? string.Empty : string.Empty,
                    ExpiresAt: item.TryGetProperty("expiresAt", out var expires)
                        ? expires.GetDateTimeOffset()
                        : DateTimeOffset.UnixEpoch)
                {
                    Width = item.TryGetProperty("width", out var width) && width.ValueKind == JsonValueKind.Number
                        ? width.GetInt32()
                        : null,
                    Height = item.TryGetProperty("height", out var height) && height.ValueKind == JsonValueKind.Number
                        ? height.GetInt32()
                        : null,
                });
            }
        }

        AgentResponseMetadata? meta = null;
        if (root.TryGetProperty("meta", out var metaElement) && metaElement.ValueKind == JsonValueKind.Object)
        {
            meta = new AgentResponseMetadata(
                DurationMs: metaElement.TryGetProperty("durationMs", out var duration) ? duration.GetInt32() : 0,
                CloudUploaded: metaElement.TryGetProperty("cloudUploaded", out var cloud) && cloud.GetBoolean());
        }

        AgentErrorPayload? error = null;
        if (root.TryGetProperty("error", out var errorElement) && errorElement.ValueKind == JsonValueKind.Object)
        {
            var code = errorElement.TryGetProperty("code", out var codeElement)
                ? AgentErrorCodeExtensions.Parse(codeElement.GetString() ?? string.Empty)
                : AgentErrorCode.InternalError;
            error = new AgentErrorPayload(
                Code: code,
                Message: errorElement.TryGetProperty("message", out var message) ? message.GetString() ?? string.Empty : string.Empty,
                Retryable: errorElement.TryGetProperty("retryable", out var retryable) && retryable.GetBoolean())
            {
                Hint = errorElement.TryGetProperty("hint", out var hint) && hint.ValueKind == JsonValueKind.String
                    ? hint.GetString()
                    : null,
            };
        }

        return new AgentResponseEnvelope
        {
            ProtocolVersion = protocolVersion,
            RequestID = requestID,
            Ok = ok,
            Data = data,
            Artifacts = artifacts,
            Meta = meta,
            Error = error,
        };
    }

    /// <summary>JSON 元素 → TaJsonValue。整数优先落到 .integer，与 Mac 的解码优先级一致（AgentEnvelope.swift:151-153）。</summary>
    private static TaJsonValue Convert(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Null => TaJsonValue.NullValue,
        JsonValueKind.True => TaJsonValue.Bool(true),
        JsonValueKind.False => TaJsonValue.Bool(false),
        JsonValueKind.Number => element.TryGetInt64(out var integer)
            ? TaJsonValue.Integer(integer)
            : TaJsonValue.Number(element.GetDouble()),
        JsonValueKind.String => TaJsonValue.String(element.GetString() ?? string.Empty),
        JsonValueKind.Array => TaJsonValue.Array(element.EnumerateArray().Select(Convert).ToArray()),
        JsonValueKind.Object => TaJsonValue.Object(element.EnumerateObject()
            .ToDictionary(property => property.Name, property => Convert(property.Value))),
        _ => TaJsonValue.NullValue,
    };
}
