using System.Text.Json.Nodes;

namespace Ta.AI;

/// <summary>
/// OpenAI 兼容 chat/completions 响应的公共解析与「关闭深度思考」注入。
///
/// 对应 Mac 版 <c>Recognition/OpenAIChatResponseSupport.swift:7-59</c>。
/// 这段逻辑是「关闭深度思考」功能的实现，Windows 必须同时复现检测逻辑与文案：
/// 推理型模型（GLM-4.x、DeepSeek-R1 …）可能把 token 预算全部耗在 <c>reasoning_content</c> 上，
/// 正文还没生成就被截断，以前会被误报成「空响应」。
/// </summary>
internal static class OpenAIChatResponseSupport
{
    /// <summary>
    /// Mac: OpenAIChatResponseSupport.swift:11-15。
    /// 只有这些域名会注入 <c>thinking</c>，未知网关一律不动（避免服务端因未知字段整体 400）。
    /// </summary>
    private static readonly string[] ThinkingControlHostSuffixes =
    {
        "deepseek.com",
        "bigmodel.cn",
        "zhipuai.cn",
    };

    /// <summary>
    /// Mac: <c>applyThinkingPreference(to:host:)</c> —— host 后缀命中时注入顶层
    /// <c>"thinking": {"type":"disabled"}</c>。
    /// </summary>
    internal static void ApplyThinkingPreference(JsonObject body, string? host)
    {
        var normalized = host?.ToLowerInvariant();
        if (normalized is null || normalized.Length == 0)
        {
            return;
        }

        if (!ThinkingControlHostSuffixes.Any(suffix => normalized.EndsWith(suffix, StringComparison.Ordinal)))
        {
            return;
        }

        body["thinking"] = new JsonObject
        {
            ["type"] = "disabled",
        };
    }

    /// <summary>Mac: <c>firstMessage(in:)</c> —— <c>choices[0].message</c>。</summary>
    internal static JsonObject? FirstMessage(JsonObject root)
    {
        var choices = root["choices"] as JsonArray;
        if (choices is null || choices.Count == 0)
        {
            return null;
        }

        return choices[0]?["message"] as JsonObject;
    }

    /// <summary>
    /// Mac: <c>text(fromContent:)</c> —— content 既可能是字符串，也可能是 parts 数组；
    /// 数组取 <c>text</c>（退化取 <c>content</c>）后用换行拼接，并裁剪首尾空白。
    /// </summary>
    internal static string? TextFromContent(JsonNode? content)
    {
        if (content is JsonValue value)
        {
            if (value.TryGetValue<string>(out var text))
            {
                return TaText.TrimOrNull(text);
            }

            return null;
        }

        if (content is JsonArray parts)
        {
            var collected = new List<string>();
            foreach (var part in parts)
            {
                if (part is not JsonObject partObject)
                {
                    continue;
                }

                if (partObject["text"] is JsonValue textValue && textValue.TryGetValue<string>(out var text))
                {
                    collected.Add(text);
                }
                else if (partObject["content"] is JsonValue contentValue &&
                         contentValue.TryGetValue<string>(out var contentText))
                {
                    collected.Add(contentText);
                }
            }

            return TaText.TrimOrNull(TaText.JoinLines(collected));
        }

        return null;
    }

    /// <summary>Mac: <c>contentText(in:)</c> —— 取首条 message 的 content 文本。</summary>
    internal static string? ContentText(JsonObject root) => TextFromContent(FirstMessage(root)?["content"]);

    /// <summary>
    /// Mac: <c>reasoningText(in:)</c> —— 只认 <c>reasoning_content</c> 与 <c>reasoning</c> 两个字段；
    /// 非空才返回（空串视为「没有思考内容」）。
    /// </summary>
    internal static string? ReasoningText(JsonObject root)
    {
        var message = FirstMessage(root);
        if (message is null)
        {
            return null;
        }

        if (message["reasoning_content"] is JsonValue reasoningContent &&
            reasoningContent.TryGetValue<string>(out var reasoningContentText))
        {
            return TaText.TrimOrNull(reasoningContentText);
        }

        if (message["reasoning"] is JsonValue reasoning && reasoning.TryGetValue<string>(out var reasoningText))
        {
            return TaText.TrimOrNull(reasoningText);
        }

        return null;
    }

    /// <summary>Mac: <c>isLengthTruncated(_:)</c> —— <c>choices[0].finish_reason == "length"</c>（大小写不敏感）。</summary>
    internal static bool IsLengthTruncated(JsonObject root)
    {
        var choices = root["choices"] as JsonArray;
        if (choices is null || choices.Count == 0)
        {
            return false;
        }

        if (choices[0]?["finish_reason"] is JsonValue reason && reason.TryGetValue<string>(out var text))
        {
            return text.ToLowerInvariant() == "length";
        }

        return false;
    }
}
