namespace Ta.AI;

/// <summary>
/// 视觉 / 翻译 Provider 的协议种类。
///
/// 逐字段对应 Mac 版 <c>MultimodalProviderClient.swift:3-17</c>（<c>VisionProviderKind</c>）：
/// rawValue 字符串沿用 Mac 的 camelCase（会写入配置），<see cref="DisplayName"/> 是界面用名。
/// </summary>
public enum VisionProviderKind
{
    /// <summary>OpenAI-compatible（任意兼容 chat/completions 的网关）。</summary>
    OpenAICompatible,

    /// <summary>Azure OpenAI。</summary>
    AzureOpenAI,

    /// <summary>Anthropic Claude。</summary>
    Anthropic,

    /// <summary>Google Gemini。</summary>
    GoogleGemini,
}

public static class VisionProviderKindExtensions
{
    /// <summary>对应 Mac 的 rawValue（配置落盘字符串，不得改动）。</summary>
    public static string RawValue(this VisionProviderKind kind) => kind switch
    {
        VisionProviderKind.OpenAICompatible => "openAICompatible",
        VisionProviderKind.AzureOpenAI => "azureOpenAI",
        VisionProviderKind.Anthropic => "anthropic",
        VisionProviderKind.GoogleGemini => "googleGemini",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    /// <summary>Mac: MultimodalProviderClient.swift:9-16（displayName）。</summary>
    public static string DisplayName(this VisionProviderKind kind) => kind switch
    {
        VisionProviderKind.OpenAICompatible => "OpenAI-compatible",
        VisionProviderKind.AzureOpenAI => "Azure OpenAI",
        VisionProviderKind.Anthropic => "Anthropic Claude",
        VisionProviderKind.GoogleGemini => "Google Gemini",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    /// <summary>按 Mac 的 rawValue 反解析；无法识别时返回 null（与 Swift 的 <c>init?(rawValue:)</c> 一致）。</summary>
    public static VisionProviderKind? ParseRawValue(string? rawValue)
    {
        if (string.IsNullOrWhiteSpace(rawValue))
        {
            return null;
        }

        var normalized = rawValue.Trim();
        return normalized switch
        {
            "openAICompatible" => VisionProviderKind.OpenAICompatible,
            "azureOpenAI" => VisionProviderKind.AzureOpenAI,
            "anthropic" => VisionProviderKind.Anthropic,
            "googleGemini" => VisionProviderKind.GoogleGemini,
            _ => null,
        };
    }

    /// <summary>全部 kind，顺序与 Mac 的 <c>CaseIterable</c>（声明顺序）一致。</summary>
    public static IReadOnlyList<VisionProviderKind> AllCases { get; } = new[]
    {
        VisionProviderKind.OpenAICompatible,
        VisionProviderKind.AzureOpenAI,
        VisionProviderKind.Anthropic,
        VisionProviderKind.GoogleGemini,
    };
}
