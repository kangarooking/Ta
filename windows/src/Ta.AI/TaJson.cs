using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Ta.AI;

/// <summary>
/// Provider 客户端共用的 JSON 约定。
///
/// Mac 版一律用 <c>JSONSerialization</c>（不转义非 ASCII）。这里用
/// <see cref="JavaScriptEncoder.UnsafeRelaxedJsonEscaping"/> 保持同样「中文直接出现在请求体里」
/// 的外观 —— 语义上等价，但对服务端日志与人工排查更友好。
/// </summary>
internal static class TaJson
{
    /// <summary>写请求体的序列化选项。</summary>
    internal static readonly JsonSerializerOptions WriteOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        WriteIndented = false,
    };

    /// <summary>读响应的反序列化选项：属性名大小写不敏感，放宽转义。</summary>
    internal static readonly JsonSerializerOptions ReadOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>Mac 对应 <c>try? JSONSerialization.jsonObject(with:)</c>：解析失败返回 null 而不抛。</summary>
    internal static JsonNode? TryParse(byte[] data)
    {
        if (data.Length == 0)
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(data, documentOptions: new JsonDocumentOptions { AllowTrailingCommas = true });
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>对象形式的响应根节点；非对象（数组 / 标量 / 解析失败）返回 null。</summary>
    internal static JsonObject? TryParseObject(byte[] data) => TryParse(data) as JsonObject;

    /// <summary>序列化为 UTF-8 字节。</summary>
    internal static byte[] Serialize(JsonNode node)
        => System.Text.Encoding.UTF8.GetBytes(node.ToJsonString(WriteOptions));
}
