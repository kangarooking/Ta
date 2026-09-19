using ProviderKindEnum = Ta.Settings.Core.ProviderKind;

namespace Ta.Settings.Core;

/// <summary>
/// AI 服务商预设。
///
/// 逐值对应 macOS <c>ModelProviderPreset</c>（ModelSettingsView.swift:876-1024）
/// 与参考文档 §9.9「Provider 预设」。⚠️ 这里的 baseURL / 模型名 / providerKind
/// 属于「必须 1:1」的硬契约，有单元测试逐值断言，改动前先看
/// <c>tests/Ta.Settings.Tests/ProviderPresetTests.cs</c>。
/// </summary>
public enum ProviderPreset
{
    /// <summary>智谱 API（rawValue <c>zhipuAPI</c>）。</summary>
    ZhipuApi,

    /// <summary>智谱 Coding Plan（rawValue <c>zhipuCodingPlan</c>）。</summary>
    ZhipuCodingPlan,

    /// <summary>DeepSeek（rawValue <c>deepSeek</c>）。</summary>
    DeepSeek,

    /// <summary>OpenAI（rawValue <c>openAI</c>）。</summary>
    OpenAi,

    /// <summary>Gemini（rawValue <c>gemini</c>）。</summary>
    Gemini,

    /// <summary>Claude（rawValue <c>anthropic</c>）。</summary>
    Anthropic,

    /// <summary>OpenRouter（rawValue <c>openRouter</c>）。</summary>
    OpenRouter,

    /// <summary>Azure（rawValue <c>azure</c>）。</summary>
    Azure,

    /// <summary>自定义（rawValue <c>custom</c>）。</summary>
    Custom,
}

/// <summary>
/// <see cref="ProviderPreset"/> 的全部派生数据。
/// 属性名与 macOS 版的 computed property 一一对应，便于对照 Review。
/// </summary>
public static class ProviderPresets
{
    /// <summary>
    /// 引导向导里服务商的展示顺序（ModelSettingsView.swift:881-891）：
    /// zhipuAPI, zhipuCodingPlan, deepSeek, openAI, gemini, anthropic, openRouter, azure, custom。
    /// ⚠️ 注意这不是 <c>enum</c> 声明顺序，Mac 版也单独维护了这个数组。
    /// </summary>
    public static IReadOnlyList<ProviderPreset> GuidedCases { get; } = new[]
    {
        ProviderPreset.ZhipuApi,
        ProviderPreset.ZhipuCodingPlan,
        ProviderPreset.DeepSeek,
        ProviderPreset.OpenAi,
        ProviderPreset.Gemini,
        ProviderPreset.Anthropic,
        ProviderPreset.OpenRouter,
        ProviderPreset.Azure,
        ProviderPreset.Custom,
    };

    /// <summary>rawValue（持久化到 JSON 里的字符串）。</summary>
    public static string Raw(ProviderPreset preset) => preset switch
    {
        ProviderPreset.ZhipuApi => "zhipuAPI",
        ProviderPreset.ZhipuCodingPlan => "zhipuCodingPlan",
        ProviderPreset.DeepSeek => "deepSeek",
        ProviderPreset.OpenAi => "openAI",
        ProviderPreset.Gemini => "gemini",
        ProviderPreset.Anthropic => "anthropic",
        ProviderPreset.OpenRouter => "openRouter",
        ProviderPreset.Azure => "azure",
        ProviderPreset.Custom => "custom",
        _ => "custom",
    };

    /// <summary>卡片标题（ModelSettingsView.swift:895-907）。</summary>
    public static string Title(ProviderPreset preset) => preset switch
    {
        ProviderPreset.DeepSeek => "DeepSeek",
        ProviderPreset.OpenAi => "OpenAI",
        ProviderPreset.OpenRouter => "OpenRouter",
        ProviderPreset.Anthropic => "Claude",
        ProviderPreset.Gemini => "Gemini",
        ProviderPreset.ZhipuApi => "智谱 API",
        ProviderPreset.ZhipuCodingPlan => "智谱 Coding Plan",
        ProviderPreset.Azure => "Azure",
        ProviderPreset.Custom => "自定义",
        _ => "自定义",
    };

    /// <summary>副标题（未在向导里展示，保留以便对齐 Mac 版模型）。</summary>
    public static string Subtitle(ProviderPreset preset) => preset switch
    {
        ProviderPreset.DeepSeek => "自动填写常用模型",
        ProviderPreset.OpenAi => "OpenAI 官方接口",
        ProviderPreset.OpenRouter => "统一接入多家模型",
        ProviderPreset.Anthropic => "Anthropic Messages",
        ProviderPreset.Gemini => "Google AI 接口",
        ProviderPreset.ZhipuApi => "通用开放平台 API",
        ProviderPreset.ZhipuCodingPlan => "编码套餐专属接口",
        ProviderPreset.Azure => "填写专属资源地址",
        ProviderPreset.Custom => "本地或兼容服务",
        _ => "本地或兼容服务",
    };

    /// <summary>向导卡片副标题（ModelSettingsView.swift:923-935）。</summary>
    public static string GuidedSubtitle(ProviderPreset preset) => preset switch
    {
        ProviderPreset.DeepSeek => "中文理解自然，适合识图和翻译",
        ProviderPreset.OpenAi => "综合能力强，适合复杂截图",
        ProviderPreset.Gemini => "Google 视觉与文字模型",
        ProviderPreset.Anthropic => "Anthropic 视觉与文字模型",
        ProviderPreset.OpenRouter => "一个接口使用多家模型",
        ProviderPreset.ZhipuApi => "通用额度，支持识图和翻译",
        ProviderPreset.ZhipuCodingPlan => "订阅套餐，仅支持文字模型直连",
        ProviderPreset.Azure => "企业 Azure OpenAI 服务",
        ProviderPreset.Custom => "本地模型或兼容接口",
        _ => "本地模型或兼容接口",
    };

    /// <summary>接口协议（ModelSettingsView.swift:962-969）。</summary>
    public static ProviderKind ProviderKind(ProviderPreset preset) => preset switch
    {
        ProviderPreset.Anthropic => Core.ProviderKind.Anthropic,
        ProviderPreset.Gemini => Core.ProviderKind.GoogleGemini,
        ProviderPreset.Azure => Core.ProviderKind.AzureOpenAI,
        _ => Core.ProviderKind.OpenAICompatible,
    };

    /// <summary>服务地址（ModelSettingsView.swift:971-982）。Azure 与 custom 需自填，为空串。</summary>
    public static string BaseUrl(ProviderPreset preset) => preset switch
    {
        ProviderPreset.DeepSeek => "https://api.deepseek.com",
        ProviderPreset.OpenAi => "https://api.openai.com/v1",
        ProviderPreset.OpenRouter => "https://openrouter.ai/api/v1",
        ProviderPreset.Anthropic => "https://api.anthropic.com",
        ProviderPreset.Gemini => "https://generativelanguage.googleapis.com",
        ProviderPreset.ZhipuApi => "https://open.bigmodel.cn/api/paas/v4",
        ProviderPreset.ZhipuCodingPlan => "https://open.bigmodel.cn/api/coding/paas/v4",
        _ => string.Empty,
    };

    /// <summary>
    /// 推荐视觉模型（ModelSettingsView.swift:984-990）。
    /// deepSeek 用 <c>TranslationConfiguration.defaultVisionModel</c> = deepseek-v4-flash-vision-exp；
    /// zhipuAPI 用 glm-5v-turbo；其余为 null。
    /// </summary>
    public static string? SuggestedVisionModel(ProviderPreset preset) => preset switch
    {
        ProviderPreset.DeepSeek => TranslationConfiguration.DefaultVisionModel,
        ProviderPreset.ZhipuApi => "glm-5v-turbo",
        _ => null,
    };

    /// <summary>
    /// 推荐文字模型（ModelSettingsView.swift:992-998）。
    /// deepSeek 用 <c>TranslationConfiguration.defaultTextModel</c> = deepseek-v4-flash；
    /// zhipuAPI / zhipuCodingPlan 用 glm-5.2；其余为 null。
    /// </summary>
    public static string? SuggestedTextModel(ProviderPreset preset) => preset switch
    {
        ProviderPreset.DeepSeek => TranslationConfiguration.DefaultTextModel,
        ProviderPreset.ZhipuApi or ProviderPreset.ZhipuCodingPlan => "glm-5.2",
        _ => null,
    };

    /// <summary>是否支持视觉直连（ModelSettingsView.swift:1000）—— 除 Coding Plan 外都支持。</summary>
    public static bool SupportsVisionDirectly(ProviderPreset preset) => preset != ProviderPreset.ZhipuCodingPlan;

    /// <summary>是否打「推荐」徽章（ModelSettingsView.swift:1002-1004）。</summary>
    public static bool IsRecommended(ProviderPreset preset)
        => preset is ProviderPreset.ZhipuApi or ProviderPreset.ZhipuCodingPlan or ProviderPreset.DeepSeek;

    /// <summary>建议配置名（ModelSettingsView.swift:1006-1008）。</summary>
    public static string SuggestedConfigurationName(ProviderPreset preset)
        => preset == ProviderPreset.Custom ? "自定义模型" : $"{Title(preset)} 日常";

    /// <summary>是否需要自填服务地址 → 自动展开高级设置（ModelSettingsView.swift:1010）。</summary>
    public static bool RequiresCustomEndpoint(ProviderPreset preset)
        => preset is ProviderPreset.Azure or ProviderPreset.Custom;

    /// <summary>
    /// 品牌图标文件名（不含扩展名）。已有 6 个 PNG：
    /// claude / deepseek / gemini / openai / openrouter / zhipu（Resources/Brand/Providers）。
    /// azure 与 custom 没有（ModelSettingsView.swift:950-960）。
    /// </summary>
    public static string? BrandAssetName(ProviderPreset preset) => preset switch
    {
        ProviderPreset.DeepSeek => "deepseek",
        ProviderPreset.OpenAi => "openai",
        ProviderPreset.OpenRouter => "openrouter",
        ProviderPreset.Anthropic => "claude",
        ProviderPreset.Gemini => "gemini",
        ProviderPreset.ZhipuApi or ProviderPreset.ZhipuCodingPlan => "zhipu",
        _ => null,
    };

    /// <summary>
    /// Coding Plan 的特殊提示（ModelSettingsView.swift:334-337）。
    /// </summary>
    public const string CodingPlanNotice =
        "Coding Plan 官方直连不支持图片输入；这套配置不会出现在 AI 识图或截图翻译列表中。";

    /// <summary>
    /// 第 3 步里 Coding Plan 的说明（ModelSettingsView.swift:550）。
    /// </summary>
    public const string CodingPlanCompletionNotice =
        "智谱 Coding Plan 直连仅支持文字模型，因此不会被设为 AI 识图默认，也不会出现在截图翻译模型列表。";

    /// <summary>
    /// 由已存配置反推预设。对应 macOS <c>ModelProviderPreset.matching(providerKind:baseURL:)</c>
    /// （ModelSettingsView.swift:1012-1023）。
    /// </summary>
    public static ProviderPreset Matching(string? providerKindRaw, string? baseUrl)
    {
        var url = (baseUrl ?? string.Empty).ToLowerInvariant().Trim().Trim('/', '\\', ' ');
        if (url.Contains("api.deepseek.com"))
        {
            return ProviderPreset.DeepSeek;
        }

        if (url.Contains("api.openai.com"))
        {
            return ProviderPreset.OpenAi;
        }

        if (url.Contains("openrouter.ai"))
        {
            return ProviderPreset.OpenRouter;
        }

        if (url.Contains("api.anthropic.com"))
        {
            return ProviderPreset.Anthropic;
        }

        if (url.Contains("generativelanguage.googleapis.com"))
        {
            return ProviderPreset.Gemini;
        }

        if (url.Contains("open.bigmodel.cn/api/coding"))
        {
            return ProviderPreset.ZhipuCodingPlan;
        }

        if (url.Contains("open.bigmodel.cn/api/paas"))
        {
            return ProviderPreset.ZhipuApi;
        }

        if (string.Equals(providerKindRaw, ProviderKinds.Raw(ProviderKindEnum.AzureOpenAI), StringComparison.Ordinal))
        {
            return ProviderPreset.Azure;
        }

        return ProviderPreset.Custom;
    }
}

/// <summary>
/// 翻译默认配置。对应 macOS <c>TranslationConfiguration</c>（参考文档 §9.5）。
/// </summary>
public static class TranslationConfiguration
{
    /// <summary>默认服务地址。</summary>
    public const string DefaultBaseUrl = "https://api.deepseek.com/chat/completions";

    /// <summary>默认文字模型。</summary>
    public const string DefaultTextModel = "deepseek-v4-flash";

    /// <summary>默认视觉模型。</summary>
    public const string DefaultVisionModel = "deepseek-v4-flash-vision-exp";

    /// <summary>默认源语言。</summary>
    public const string DefaultSourceLanguage = "自动检测";

    /// <summary>默认目标语言。</summary>
    public const string DefaultTargetLanguage = "简体中文";
}
