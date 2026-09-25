using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Ta.CLI.Agent;

/// <summary>
/// JSON 值模型。逐项对应 Mac: JSONValue（Sources/TaAgentContracts/AgentEnvelope.swift:138-187）。
/// 结构相等（用于测试断言），与 Swift 的 Equatable 语义一致。
/// </summary>
public abstract class TaJsonValue : IEquatable<TaJsonValue>
{
    public static TaJsonValue NullValue { get; } = new TaJsonNull();
    public static TaJsonValue Bool(bool value) => new TaJsonBool(value);
    public static TaJsonValue Integer(long value) => new TaJsonInteger(value);
    public static TaJsonValue Number(double value) => new TaJsonNumber(value);
    public static TaJsonValue String(string value) => new TaJsonString(value);
    public static TaJsonValue Array(IReadOnlyList<TaJsonValue> items) => new TaJsonArray(items);
    public static TaJsonValue Object(IReadOnlyDictionary<string, TaJsonValue> members) => new TaJsonObject(members);

    public abstract bool Equals(TaJsonValue? other);

    public override bool Equals(object? obj) => obj is TaJsonValue other && Equals(other);

    public override int GetHashCode() => 0;

    public override string ToString() => TaJson.WriteValue(this);
}

/// <summary>对应 Mac: JSONValue.null</summary>
public sealed class TaJsonNull : TaJsonValue
{
    public override bool Equals(TaJsonValue? other) => other is TaJsonNull;
}

/// <summary>对应 Mac: JSONValue.bool</summary>
public sealed class TaJsonBool(bool value) : TaJsonValue
{
    public bool Value { get; } = value;
    public override bool Equals(TaJsonValue? other) => other is TaJsonBool b && b.Value == Value;
    public override int GetHashCode() => Value.GetHashCode();
}

/// <summary>对应 Mac: JSONValue.integer</summary>
public sealed class TaJsonInteger(long value) : TaJsonValue
{
    public long Value { get; } = value;
    public override bool Equals(TaJsonValue? other) => other is TaJsonInteger i && i.Value == Value;
    public override int GetHashCode() => Value.GetHashCode();
}

/// <summary>对应 Mac: JSONValue.number</summary>
public sealed class TaJsonNumber(double value) : TaJsonValue
{
    public double Value { get; } = value;
    public override bool Equals(TaJsonValue? other) => other is TaJsonNumber n && n.Value.Equals(Value);
    public override int GetHashCode() => Value.GetHashCode();
}

/// <summary>对应 Mac: JSONValue.string</summary>
public sealed class TaJsonString(string value) : TaJsonValue
{
    public string Value { get; } = value;
    public override bool Equals(TaJsonValue? other) => other is TaJsonString s && s.Value == Value;
    public override int GetHashCode() => Value.GetHashCode();
}

/// <summary>对应 Mac: JSONValue.array</summary>
public sealed class TaJsonArray(IReadOnlyList<TaJsonValue> items) : TaJsonValue
{
    public IReadOnlyList<TaJsonValue> Items { get; } = items;
    public override bool Equals(TaJsonValue? other) => other is TaJsonArray a && Items.SequenceEqual(a.Items);
    public override int GetHashCode() => Items.Count;
}

/// <summary>对应 Mac: JSONValue.object</summary>
public sealed class TaJsonObject(IReadOnlyDictionary<string, TaJsonValue> members) : TaJsonValue
{
    public IReadOnlyDictionary<string, TaJsonValue> Members { get; } = members;

    public override bool Equals(TaJsonValue? other) =>
        other is TaJsonObject o
        && Members.Count == o.Members.Count
        && Members.All(pair => o.Members.TryGetValue(pair.Key, out var value) && pair.Value.Equals(value));

    public override int GetHashCode() => Members.Count;
}

/// <summary>
/// 字节级 JSON 序列化。对应 Mac: AgentJSONCoding（Sources/TaAgentContracts/AgentEnvelope.swift:123-136）：
///   dateEncodingStrategy = .iso8601
///   outputFormatting = [.sortedKeys, .withoutEscapingSlashes]
///
/// Windows 端用 Utf8JsonWriter 手工保证：
///   1. 所有对象 key 按序号（序数）排序 —— 与 Swift sortedKeys 对 ASCII key 的行为一致；
///   2. 不转义 '/' —— UnsafeRelaxedJsonEscaping 行为（与 .withoutEscapingSlashes 等价）；
///   3. 非 ASCII 原样输出 UTF-8 —— 与 Swift 一致（Swift JSONEncoder 不转义非 ASCII）；
///   4. 日期 ISO8601 无小数秒 —— 与 .iso8601 策略一致；
///   5. Double 用最短往返表示 —— 100.0 → "100"，与 Swift 一致。
/// </summary>
public static class TaJson
{
    private static readonly JsonWriterOptions WriterOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Indented = false,
        SkipValidation = true,
    };

    /// <summary>任意 JSON 值的紧凑序列化（key 排序、不转义斜杠）。</summary>
    public static string WriteValue(TaJsonValue value)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
        {
            WriteValue(writer, value);
        }
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    /// <summary>
    /// 请求信封序列化。对应 Mac: AgentRequestEnvelope 编码（AgentEnvelope.swift:17-51）。
    /// key 排序后：client, method, params, protocolVersion, requestId。
    /// </summary>
    public static byte[] WriteRequest(AgentRequestEnvelope request)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
        {
            writer.WriteStartObject();
            writer.WritePropertyName("client");
            writer.WriteStartObject();
            writer.WriteString("name", request.Client.Name);
            writer.WriteString("version", request.Client.Version);
            writer.WriteEndObject();
            writer.WriteString("method", request.Method.RawValue());
            writer.WritePropertyName("params");
            WriteValue(writer, TaJsonValue.Object(request.Params));
            writer.WriteNumber("protocolVersion", request.ProtocolVersion);
            writer.WriteString("requestId", request.RequestID);
            writer.WriteEndObject();
        }
        return buffer.WrittenSpan.ToArray();
    }

    /// <summary>
    /// 响应信封序列化 —— golden 测试要求字节级一致。
    /// 对应 Mac: AgentResponseEnvelope 编码（AgentEnvelope.swift:63-121）。
    ///
    /// 关键差异：Swift 对 nil 的可选字段直接省略，非可选的 artifacts 始终输出（空数组也输出）。
    /// key 排序后：artifacts, data, error, meta, ok, protocolVersion, requestId。
    /// </summary>
    public static string WriteResponse(AgentResponseEnvelope response)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
        {
            writer.WriteStartObject();
            writer.WritePropertyName("artifacts");
            writer.WriteStartArray();
            foreach (var artifact in response.Artifacts)
            {
                writer.WriteStartObject();
                writer.WriteNumber("bytes", artifact.Bytes);
                writer.WriteString("expiresAt", FormatIso8601(artifact.ExpiresAt));
                if (artifact.Height is { } height) { writer.WriteNumber("height", height); }
                writer.WriteString("id", artifact.Id);
                writer.WriteString("mimeType", artifact.MimeType);
                writer.WriteString("path", artifact.Path);
                writer.WriteString("sha256", artifact.Sha256);
                if (artifact.Width is { } width) { writer.WriteNumber("width", width); }
                writer.WriteEndObject();
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
                writer.WriteStartObject();
                writer.WriteString("code", error.Code.RawValue());
                if (error.Hint is { } hint) { writer.WriteString("hint", hint); }
                writer.WriteString("message", error.Message);
                writer.WriteBoolean("retryable", error.Retryable);
                writer.WriteEndObject();
            }
            if (response.Meta is { } meta)
            {
                writer.WritePropertyName("meta");
                writer.WriteStartObject();
                writer.WriteBoolean("cloudUploaded", meta.CloudUploaded);
                writer.WriteNumber("durationMs", meta.DurationMs);
                writer.WriteEndObject();
            }
            writer.WriteBoolean("ok", response.Ok);
            writer.WriteNumber("protocolVersion", response.ProtocolVersion);
            writer.WriteString("requestId", response.RequestID);
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static void WriteValue(Utf8JsonWriter writer, TaJsonValue value)
    {
        switch (value)
        {
            case TaJsonNull:
                writer.WriteNullValue();
                break;
            case TaJsonBool b:
                writer.WriteBooleanValue(b.Value);
                break;
            case TaJsonInteger i:
                writer.WriteNumberValue(i.Value);
                break;
            case TaJsonNumber n:
                if (!double.IsFinite(n.Value))
                {
                    // Swift 同样无法编码 Infinity/NaN（编码会抛错）。
                    throw new JsonException($"无法编码非有限数字：{n.Value}");
                }
                writer.WriteNumberValue(n.Value);
                break;
            case TaJsonString s:
                writer.WriteStringValue(s.Value);
                break;
            case TaJsonArray array:
                writer.WriteStartArray();
                foreach (var item in array.Items) { WriteValue(writer, item); }
                writer.WriteEndArray();
                break;
            case TaJsonObject obj:
                writer.WriteStartObject();
                // 对应 Mac: outputFormatting = [.sortedKeys] —— 所有对象 key 排序后输出。
                foreach (var key in obj.Members.Keys.OrderBy(k => k, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(key);
                    WriteValue(writer, obj.Members[key]);
                }
                writer.WriteEndObject();
                break;
            default:
                throw new JsonException($"未知 JSON 值类型：{value.GetType().Name}");
        }
    }

    /// <summary>对应 Mac: dateEncodingStrategy = .iso8601 —— "yyyy-MM-dd'T'HH:mm:ssZ"，无小数秒。</summary>
    internal static string FormatIso8601(DateTimeOffset value) =>
        value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    /// <summary>
    /// human 模式下 Double 的显示文本。对应 Mac: CLIOutput.display 的 .number 分支
    /// （Sources/TaCLI/CLIOutput.swift:64）—— Swift 的 String(Double) 会给整数补 ".0"
    /// （100.0 → "100.0"、100.5 → "100.5"）。
    /// </summary>
    internal static string DisplayNumber(double value)
    {
        var text = value.ToString("R", CultureInfo.InvariantCulture);
        if (text.IndexOf('.') < 0 && text.IndexOfAny(['e', 'E']) < 0)
        {
            text += ".0";
        }
        return text;
    }
}
