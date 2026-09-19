using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Ta.AgentBridge.Protocol;

/// <summary>
/// 请求解码失败。对应 Mac 版连接层捕获的 DecodingError。
/// </summary>
public sealed class AgentDecodeException : Exception
{
    public AgentDecodeException(string message) : base(message)
    {
    }
}

/// <summary>
/// Bridge JSON 编解码。对应 Mac 版 TaAgentContracts/AgentEnvelope.swift:123-136 的 AgentJSONCoding。
///
/// ⚠️ §14 风险 #40 的四条字节级 parity 要求在此集中实现：
///   1. sortedKeys —— 所有对象键按 Ordinal 显式排序（System.Text.Json 默认按声明/插入顺序）
///   2. 不转义 / —— 用 JavaScriptEncoder.UnsafeRelaxedJsonEscaping（默认会输出 \/）
///   3. ISO8601 无小数秒 —— yyyy-MM-ddTHH:mm:ssZ（Swift 的 .iso8601 无小数秒）
///   4. 可选属性省略 —— data/meta/error/hint/width/height 为 null 时省略；artifacts 恒为 []
/// </summary>
public static class AgentJson
{
    private static readonly JsonWriterOptions WriterOptions = new()
    {
        Indented = false,
        // 不转义 /、+ 等非 ASCII 安全字符 —— 对应 Swift 的 .withoutEscapingSlashes。
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    // ── 响应信封序列化 ──────────────────────────────────────────────

    /// <summary>序列化响应信封为 UTF-8 字节。字段顺序严格按字母序。</summary>
    public static byte[] Encode(AgentResponseEnvelope response)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
        {
            WriteResponse(writer, response);
        }

        return buffer.ToArray();
    }

    private static void WriteResponse(Utf8JsonWriter writer, AgentResponseEnvelope response)
    {
        writer.WriteStartObject();

        // 字母序：artifacts, data, error, meta, ok, protocolVersion, requestId
        writer.WritePropertyName("artifacts");
        writer.WriteStartArray();
        foreach (var artifact in response.Artifacts)
        {
            WriteArtifact(writer, artifact);
        }

        writer.WriteEndArray();

        if (response.Data is { } data)
        {
            writer.WritePropertyName("data");
            WriteValue(writer, data);
        }

        if (response.Error is { } error)
        {
            writer.WritePropertyName("error");
            WriteErrorPayload(writer, error);
        }

        if (response.Meta is { } meta)
        {
            writer.WritePropertyName("meta");
            WriteMetadata(writer, meta);
        }

        writer.WriteBoolean("ok", response.Ok);
        writer.WriteNumber("protocolVersion", response.ProtocolVersion);
        writer.WriteString("requestId", response.RequestId);

        writer.WriteEndObject();
    }

    private static void WriteErrorPayload(Utf8JsonWriter writer, AgentErrorPayload error)
    {
        writer.WriteStartObject();
        // 字母序：code, hint, message, retryable
        writer.WriteString("code", error.Code.Raw());
        if (error.Hint is { } hint)
        {
            writer.WriteString("hint", hint);
        }

        writer.WriteString("message", error.Message);
        writer.WriteBoolean("retryable", error.Retryable);
        writer.WriteEndObject();
    }

    private static void WriteMetadata(Utf8JsonWriter writer, AgentResponseMetadata meta)
    {
        writer.WriteStartObject();
        // 字母序：cloudUploaded, durationMs
        writer.WriteBoolean("cloudUploaded", meta.CloudUploaded);
        writer.WriteNumber("durationMs", meta.DurationMs);
        writer.WriteEndObject();
    }

    private static void WriteArtifact(Utf8JsonWriter writer, AgentArtifact artifact)
    {
        writer.WriteStartObject();
        // 字母序：bytes, expiresAt, height?, id, mimeType, path, sha256, width?
        writer.WriteNumber("bytes", artifact.Bytes);
        writer.WriteString("expiresAt", artifact.ExpiresAtIso8601);
        if (artifact.Height is { } height)
        {
            writer.WriteNumber("height", height);
        }

        writer.WriteString("id", artifact.Id);
        writer.WriteString("mimeType", artifact.MimeType);
        writer.WriteString("path", artifact.Path);
        writer.WriteString("sha256", artifact.Sha256);
        if (artifact.Width is { } width)
        {
            writer.WriteNumber("width", width);
        }

        writer.WriteEndObject();
    }

    // ── 任意 JSONValue 序列化（供审计日志等复用） ─────────────────────

    public static byte[] EncodeValue(AgentJsonValue value)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
        {
            WriteValue(writer, value);
        }

        return buffer.ToArray();
    }

    /// <summary>写任意 JSONValue。对象键按 Ordinal 排序输出（sortedKeys）。</summary>
    public static void WriteValue(Utf8JsonWriter writer, AgentJsonValue value)
    {
        switch (value)
        {
            case JsonNull:
                writer.WriteNullValue();
                break;
            case JsonBool b:
                writer.WriteBooleanValue(b.Value);
                break;
            case JsonInteger i:
                writer.WriteNumberValue(i.Value);
                break;
            case JsonNumber n:
                writer.WriteNumberValue(n.Value);
                break;
            case JsonString s:
                writer.WriteStringValue(s.Value);
                break;
            case JsonArray a:
                writer.WriteStartArray();
                foreach (var item in a.Items)
                {
                    WriteValue(writer, item);
                }

                writer.WriteEndArray();
                break;
            case JsonObject o:
                writer.WriteStartObject();
                foreach (var pair in o.Properties.OrderBy(p => p.Key, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(pair.Key);
                    WriteValue(writer, pair.Value);
                }

                writer.WriteEndObject();
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(value));
        }
    }

    // ── 请求信封解码 ────────────────────────────────────────────────

    /// <summary>从 UTF-8 字节解码请求信封。结构错误或未知方法抛 AgentDecodeException。</summary>
    public static AgentRequestEnvelope DecodeRequest(byte[] data)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(data);
        }
        catch (JsonException error)
        {
            throw new AgentDecodeException($"无法解析 Bridge 请求：{error.Message}");
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new AgentDecodeException("无法解析 Bridge 请求：根节点不是对象。");
            }

            if (!root.TryGetProperty("protocolVersion", out var versionElement)
                || versionElement.ValueKind != JsonValueKind.Number
                || !versionElement.TryGetInt32(out var protocolVersion))
            {
                throw new AgentDecodeException("无法解析 Bridge 请求：缺少 protocolVersion。");
            }

            if (!root.TryGetProperty("requestId", out var requestIdElement)
                || requestIdElement.ValueKind != JsonValueKind.String
                || requestIdElement.GetString() is not { } requestId)
            {
                throw new AgentDecodeException("无法解析 Bridge 请求：缺少 requestId。");
            }

            if (!root.TryGetProperty("method", out var methodElement)
                || methodElement.ValueKind != JsonValueKind.String
                || methodElement.GetString() is not { } methodRaw)
            {
                throw new AgentDecodeException("无法解析 Bridge 请求：缺少 method。");
            }

            if (!AgentMethodExtensions.TryParse(methodRaw, out var method))
            {
                throw new AgentDecodeException($"无法解析 Bridge 请求：未知方法 {methodRaw}。");
            }

            var client = new AgentClientInfo("ta", "dev");
            if (root.TryGetProperty("client", out var clientElement) && clientElement.ValueKind == JsonValueKind.Object)
            {
                var name = clientElement.TryGetProperty("name", out var nameElement) && nameElement.ValueKind == JsonValueKind.String
                    ? nameElement.GetString() ?? "ta"
                    : "ta";
                var version = clientElement.TryGetProperty("version", out var versionElem) && versionElem.ValueKind == JsonValueKind.String
                    ? versionElem.GetString() ?? "dev"
                    : "dev";
                client = new AgentClientInfo(name, version);
            }

            var @params = new Dictionary<string, AgentJsonValue>(StringComparer.Ordinal);
            if (root.TryGetProperty("params", out var paramsElement) && paramsElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in paramsElement.EnumerateObject())
                {
                    @params[property.Name] = ReadValue(property.Value);
                }
            }

            return new AgentRequestEnvelope(protocolVersion, requestId, method, @params, client);
        }
    }

    /// <summary>把任意 UTF-8 JSON 解码为 AgentJsonValue。供审计日志等复用同一读取路径。</summary>
    public static AgentJsonValue DecodeValue(byte[] data)
    {
        var document = JsonDocument.Parse(data);
        using (document)
        {
            return ReadValue(document.RootElement);
        }
    }

    /// <summary>序列化请求信封为 UTF-8 字节（供客户端发送）。字段按字母序。</summary>
    public static byte[] EncodeRequest(AgentRequestEnvelope request)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
        {
            writer.WriteStartObject();

            // 字母序：client, method, params, protocolVersion, requestId
            writer.WritePropertyName("client");
            writer.WriteStartObject();
            writer.WriteString("name", request.Client.Name);
            writer.WriteString("version", request.Client.Version);
            writer.WriteEndObject();

            writer.WriteString("method", request.Method.Raw());
            writer.WritePropertyName("params");
            WriteValue(writer, new JsonObject(request.Params));
            writer.WriteNumber("protocolVersion", request.ProtocolVersion);
            writer.WriteString("requestId", request.RequestId);

            writer.WriteEndObject();
        }

        return buffer.ToArray();
    }

    /// <summary>从 UTF-8 字节解码响应信封（供客户端读取）。</summary>
    public static AgentResponseEnvelope DecodeResponse(byte[] data)
    {
        var document = JsonDocument.Parse(data);
        using (document)
        {
            var root = document.RootElement;
            var ok = root.TryGetProperty("ok", out var okElement) && okElement.ValueKind == JsonValueKind.True;
            var requestId = root.TryGetProperty("requestId", out var idElement) && idElement.ValueKind == JsonValueKind.String
                ? idElement.GetString() ?? ""
                : "";
            var protocolVersion = root.TryGetProperty("protocolVersion", out var vElement) && vElement.TryGetInt32(out var v)
                ? v
                : AgentProtocol.CurrentVersion;

            AgentJsonValue? dataValue = root.TryGetProperty("data", out var dataElement)
                ? ReadValue(dataElement)
                : null;

            var artifacts = new List<AgentArtifact>();
            if (root.TryGetProperty("artifacts", out var artifactsElement) && artifactsElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in artifactsElement.EnumerateArray())
                {
                    artifacts.Add(ReadArtifact(item));
                }
            }

            AgentResponseMetadata? meta = null;
            if (root.TryGetProperty("meta", out var metaElement) && metaElement.ValueKind == JsonValueKind.Object)
            {
                var durationMs = metaElement.TryGetProperty("durationMs", out var d) && d.TryGetInt32(out var dv) ? dv : 0;
                var cloud = metaElement.TryGetProperty("cloudUploaded", out var c) && c.ValueKind == JsonValueKind.True;
                meta = new AgentResponseMetadata(durationMs, cloud);
            }

            AgentErrorPayload? error = null;
            if (root.TryGetProperty("error", out var errorElement) && errorElement.ValueKind == JsonValueKind.Object)
            {
                var code = errorElement.TryGetProperty("code", out var ce) && ce.ValueKind == JsonValueKind.String
                    ? ParseErrorCode(ce.GetString() ?? "")
                    : AgentErrorCode.InternalError;
                var message = errorElement.TryGetProperty("message", out var me) && me.ValueKind == JsonValueKind.String
                    ? me.GetString() ?? ""
                    : "";
                var hint = errorElement.TryGetProperty("hint", out var he) && he.ValueKind == JsonValueKind.String
                    ? he.GetString()
                    : null;
                var retryable = errorElement.TryGetProperty("retryable", out var re) && re.ValueKind == JsonValueKind.True;
                error = new AgentErrorPayload(code, message, hint, retryable);
            }

            return new AgentResponseEnvelope(requestId, ok, protocolVersion, dataValue, artifacts, meta, error);
        }
    }

    private static AgentErrorCode ParseErrorCode(string raw)
    {
        foreach (var code in Enum.GetValues<AgentErrorCode>())
        {
            if (code.Raw() == raw)
            {
                return code;
            }
        }

        return AgentErrorCode.InternalError;
    }

    private static AgentArtifact ReadArtifact(JsonElement element)
    {
        string ReadString(string key) => element.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
        int ReadInt(string key) => element.TryGetProperty(key, out var v) && v.TryGetInt32(out var i) ? i : 0;
        int? ReadOptInt(string key) => element.TryGetProperty(key, out var v) && v.TryGetInt32(out var i) ? i : null;

        var expiresAt = element.TryGetProperty("expiresAt", out var ea) && ea.ValueKind == JsonValueKind.String
            ? DateTime.Parse(ea.GetString() ?? "", CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal)
            : DateTime.MinValue;

        return new AgentArtifact(
            ReadString("id"),
            ReadString("path"),
            ReadString("mimeType"),
            ReadOptInt("width"),
            ReadOptInt("height"),
            ReadInt("bytes"),
            ReadString("sha256"),
            expiresAt);
    }

    private static AgentJsonValue ReadValue(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Null => AgentJsonValue.Null,
        JsonValueKind.True => new JsonBool(true),
        JsonValueKind.False => new JsonBool(false),
        JsonValueKind.Number => element.TryGetInt64(out var integer)
            ? new JsonInteger(integer)
            : new JsonNumber(element.GetDouble()),
        JsonValueKind.String => new JsonString(element.GetString() ?? ""),
        JsonValueKind.Array => new JsonArray(element.EnumerateArray().Select(ReadValue).ToArray()),
        JsonValueKind.Object => new JsonObject(
            element.EnumerateObject()
                .ToDictionary(p => p.Name, p => ReadValue(p.Value), StringComparer.Ordinal)),
        _ => AgentJsonValue.Null,
    };
}
