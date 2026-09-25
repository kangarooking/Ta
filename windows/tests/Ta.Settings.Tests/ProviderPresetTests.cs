using Ta.Settings.Core;

namespace Ta.Settings.Tests;

/// <summary>
/// 验收 2：9 个 Provider 预设的 baseURL / 模型 / providerKind 逐值断言。
///
/// 期望值逐行抄自参考文档 §9.9「Provider 预设（ModelProviderPreset，ModelSettingsView.swift:876-1024）」，
/// 以及 macOS 源码 ModelSettingsView.swift:881-1023 / TranslationConfiguration.swift（§9.5）。
/// 这些是跨平台硬契约，改动必须同步 Mac 版。
/// </summary>
public sealed class ProviderPresetTests
{
    /// <summary>引导顺序必须是 zhipuAPI, zhipuCodingPlan, deepSeek, openAI, gemini, anthropic, openRouter, azure, custom。</summary>
    [Fact]
    public void GuidedCases_HaveTheDocumentedOrder()
    {
        var actual = ProviderPresets.GuidedCases.Select(ProviderPresets.Raw).ToArray();
        Assert.Equal(
            new[]
            {
                "zhipuAPI",
                "zhipuCodingPlan",
                "deepSeek",
                "openAI",
                "gemini",
                "anthropic",
                "openRouter",
                "azure",
                "custom",
            },
            actual);
    }

    /// <summary>刚好 9 个预设。</summary>
    [Fact]
    public void GuidedCases_HasNinePresets() => Assert.Equal(9, ProviderPresets.GuidedCases.Count);

    /// <summary>标题逐值断言（ModelSettingsView.swift:895-907）。</summary>
    [Theory]
    [InlineData("zhipuAPI", "智谱 API")]
    [InlineData("zhipuCodingPlan", "智谱 Coding Plan")]
    [InlineData("deepSeek", "DeepSeek")]
    [InlineData("openAI", "OpenAI")]
    [InlineData("gemini", "Gemini")]
    [InlineData("anthropic", "Claude")]
    [InlineData("openRouter", "OpenRouter")]
    [InlineData("azure", "Azure")]
    [InlineData("custom", "自定义")]
    public void Title_MatchesDocument(string raw, string expected)
        => Assert.Equal(expected, ProviderPresets.Title(Parse(raw)));

    /// <summary>baseURL 逐值断言（ModelSettingsView.swift:971-982；azure/custom 需自填为空）。</summary>
    [Theory]
    [InlineData("zhipuAPI", "https://open.bigmodel.cn/api/paas/v4")]
    [InlineData("zhipuCodingPlan", "https://open.bigmodel.cn/api/coding/paas/v4")]
    [InlineData("deepSeek", "https://api.deepseek.com")]
    [InlineData("openAI", "https://api.openai.com/v1")]
    [InlineData("gemini", "https://generativelanguage.googleapis.com")]
    [InlineData("anthropic", "https://api.anthropic.com")]
    [InlineData("openRouter", "https://openrouter.ai/api/v1")]
    [InlineData("azure", "")]
    [InlineData("custom", "")]
    public void BaseUrl_MatchesDocument(string raw, string expected)
        => Assert.Equal(expected, ProviderPresets.BaseUrl(Parse(raw)));

    /// <summary>providerKind 逐值断言（ModelSettingsView.swift:962-969）。</summary>
    [Theory]
    [InlineData("zhipuAPI", "openAICompatible")]
    [InlineData("zhipuCodingPlan", "openAICompatible")]
    [InlineData("deepSeek", "openAICompatible")]
    [InlineData("openAI", "openAICompatible")]
    [InlineData("gemini", "googleGemini")]
    [InlineData("anthropic", "anthropic")]
    [InlineData("openRouter", "openAICompatible")]
    [InlineData("azure", "azureOpenAI")]
    [InlineData("custom", "openAICompatible")]
    public void ProviderKind_MatchesDocument(string raw, string expected)
        => Assert.Equal(expected, ProviderKinds.Raw(ProviderPresets.ProviderKind(Parse(raw))));

    /// <summary>
    /// 推荐视觉模型逐值断言（ModelSettingsView.swift:984-990）。
    /// deepSeek 用 <c>TranslationConfiguration.defaultVisionModel</c>，zhipuAPI 用 glm-5v-turbo，其余为空。
    /// </summary>
    [Theory]
    [InlineData("zhipuAPI", "glm-5v-turbo")]
    [InlineData("deepSeek", "deepseek-v4-flash-vision-exp")]
    [InlineData("zhipuCodingPlan", null)]
    [InlineData("openAI", null)]
    [InlineData("gemini", null)]
    [InlineData("anthropic", null)]
    [InlineData("openRouter", null)]
    [InlineData("azure", null)]
    [InlineData("custom", null)]
    public void SuggestedVisionModel_MatchesDocument(string raw, string? expected)
        => Assert.Equal(expected, ProviderPresets.SuggestedVisionModel(Parse(raw)));

    /// <summary>
    /// 推荐文字模型逐值断言（ModelSettingsView.swift:992-998）。
    /// deepSeek 用 <c>TranslationConfiguration.defaultTextModel</c>，
    /// zhipuAPI / zhipuCodingPlan 用 glm-5.2，其余为空。
    /// </summary>
    [Theory]
    [InlineData("zhipuAPI", "glm-5.2")]
    [InlineData("zhipuCodingPlan", "glm-5.2")]
    [InlineData("deepSeek", "deepseek-v4-flash")]
    [InlineData("openAI", null)]
    [InlineData("gemini", null)]
    [InlineData("anthropic", null)]
    [InlineData("openRouter", null)]
    [InlineData("azure", null)]
    [InlineData("custom", null)]
    public void SuggestedTextModel_MatchesDocument(string raw, string? expected)
        => Assert.Equal(expected, ProviderPresets.SuggestedTextModel(Parse(raw)));

    /// <summary>只有 zhipuCodingPlan 不支持视觉直连（ModelSettingsView.swift:1000）。</summary>
    [Theory]
    [InlineData("zhipuAPI", true)]
    [InlineData("zhipuCodingPlan", false)]
    [InlineData("deepSeek", true)]
    [InlineData("openAI", true)]
    [InlineData("gemini", true)]
    [InlineData("anthropic", true)]
    [InlineData("openRouter", true)]
    [InlineData("azure", true)]
    [InlineData("custom", true)]
    public void SupportsVisionDirectly_MatchesDocument(string raw, bool expected)
        => Assert.Equal(expected, ProviderPresets.SupportsVisionDirectly(Parse(raw)));

    /// <summary>推荐徽章只给智谱 API、智谱 Coding Plan 和 DeepSeek（ModelSettingsView.swift:1002-1004）。</summary>
    [Theory]
    [InlineData("zhipuAPI", true)]
    [InlineData("zhipuCodingPlan", true)]
    [InlineData("deepSeek", true)]
    [InlineData("openAI", false)]
    [InlineData("gemini", false)]
    [InlineData("anthropic", false)]
    [InlineData("openRouter", false)]
    [InlineData("azure", false)]
    [InlineData("custom", false)]
    public void IsRecommended_MatchesDocument(string raw, bool expected)
        => Assert.Equal(expected, ProviderPresets.IsRecommended(Parse(raw)));

    /// <summary>只有 azure 与 custom 需要自填服务地址并自动展开高级设置（ModelSettingsView.swift:1010）。</summary>
    [Theory]
    [InlineData("azure", true)]
    [InlineData("custom", true)]
    [InlineData("zhipuAPI", false)]
    [InlineData("zhipuCodingPlan", false)]
    [InlineData("deepSeek", false)]
    [InlineData("openAI", false)]
    [InlineData("gemini", false)]
    [InlineData("anthropic", false)]
    [InlineData("openRouter", false)]
    public void RequiresCustomEndpoint_MatchesDocument(string raw, bool expected)
        => Assert.Equal(expected, ProviderPresets.RequiresCustomEndpoint(Parse(raw)));

    /// <summary>建议配置名（ModelSettingsView.swift:1006-1008）。</summary>
    [Theory]
    [InlineData("custom", "自定义模型")]
    [InlineData("zhipuAPI", "智谱 API 日常")]
    [InlineData("zhipuCodingPlan", "智谱 Coding Plan 日常")]
    [InlineData("deepSeek", "DeepSeek 日常")]
    [InlineData("openAI", "OpenAI 日常")]
    [InlineData("gemini", "Gemini 日常")]
    [InlineData("anthropic", "Claude 日常")]
    [InlineData("openRouter", "OpenRouter 日常")]
    [InlineData("azure", "Azure 日常")]
    public void SuggestedConfigurationName_MatchesDocument(string raw, string expected)
        => Assert.Equal(expected, ProviderPresets.SuggestedConfigurationName(Parse(raw)));

    /// <summary>
    /// 品牌图标文件名（ModelSettingsView.swift:950-960）。已有 6 个 PNG，
    /// azure 与 custom 没有。
    /// </summary>
    [Theory]
    [InlineData("deepSeek", "deepseek")]
    [InlineData("openAI", "openai")]
    [InlineData("openRouter", "openrouter")]
    [InlineData("anthropic", "claude")]
    [InlineData("gemini", "gemini")]
    [InlineData("zhipuAPI", "zhipu")]
    [InlineData("zhipuCodingPlan", "zhipu")]
    [InlineData("azure", null)]
    [InlineData("custom", null)]
    public void BrandAssetName_MatchesDocument(string raw, string? expected)
        => Assert.Equal(expected, ProviderPresets.BrandAssetName(Parse(raw)));

    /// <summary>Coding Plan 的特殊提示必须逐字一致（ModelSettingsView.swift:334-337）。</summary>
    [Fact]
    public void CodingPlanNotice_IsVerbatim()
        => Assert.Equal(
            "Coding Plan 官方直连不支持图片输入；这套配置不会出现在 AI 识图或截图翻译列表中。",
            ProviderPresets.CodingPlanNotice);

    /// <summary>
    /// 由已存配置反推预设（ModelSettingsView.swift:1012-1023）。
    /// </summary>
    [Theory]
    [InlineData("openAICompatible", "https://api.deepseek.com", "deepSeek")]
    [InlineData("openAICompatible", "https://api.deepseek.com/", "deepSeek")]
    [InlineData("openAICompatible", "https://api.openai.com/v1", "openAI")]
    [InlineData("openAICompatible", "https://openrouter.ai/api/v1", "openRouter")]
    [InlineData("anthropic", "https://api.anthropic.com", "anthropic")]
    [InlineData("googleGemini", "https://generativelanguage.googleapis.com", "gemini")]
    [InlineData("openAICompatible", "https://open.bigmodel.cn/api/coding/paas/v4", "zhipuCodingPlan")]
    [InlineData("openAICompatible", "https://open.bigmodel.cn/api/paas/v4", "zhipuAPI")]
    [InlineData("azureOpenAI", "https://my-resource.openai.azure.com", "azure")]
    [InlineData("openAICompatible", "http://127.0.0.1:8000/v1", "custom")]
    [InlineData("openAICompatible", "", "custom")]
    public void Matching_ReverseMapsProviderKindAndBaseUrl(string providerKind, string baseUrl, string expected)
        => Assert.Equal(expected, ProviderPresets.Raw(ProviderPresets.Matching(providerKind, baseUrl)));

    /// <summary>cooking 路径：coding 必须在 paas 之前判定，否则会被误判成 zhipuAPI。</summary>
    [Fact]
    public void Matching_PrefersCodingPlanOverPlainApi()
        => Assert.Equal(
            ProviderPreset.ZhipuCodingPlan,
            ProviderPresets.Matching("openAICompatible", "https://open.bigmodel.cn/api/coding/paas/v4"));

    /// <summary>rawValue 必须与枚举名一一对应且无重复。</summary>
    [Fact]
    public void RawValues_AreUniqueAndMatchDocument()
    {
        var values = ProviderPresets.GuidedCases.Select(ProviderPresets.Raw).ToArray();
        Assert.Equal(values.Length, values.Distinct(StringComparer.Ordinal).Count());
    }

    /// <summary>翻译默认配置（参考文档 §9.5，TranslationConfiguration.swift:5-9）。</summary>
    [Fact]
    public void TranslationConfiguration_MatchesDocument()
    {
        Assert.Equal("https://api.deepseek.com/chat/completions", TranslationConfiguration.DefaultBaseUrl);
        Assert.Equal("deepseek-v4-flash", TranslationConfiguration.DefaultTextModel);
        Assert.Equal("deepseek-v4-flash-vision-exp", TranslationConfiguration.DefaultVisionModel);
        Assert.Equal("自动检测", TranslationConfiguration.DefaultSourceLanguage);
        Assert.Equal("简体中文", TranslationConfiguration.DefaultTargetLanguage);
    }

    private static ProviderPreset Parse(string raw)
        => ProviderPresets.GuidedCases.First(p => string.Equals(ProviderPresets.Raw(p), raw, StringComparison.Ordinal));
}
