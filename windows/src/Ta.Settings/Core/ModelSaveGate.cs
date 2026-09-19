namespace Ta.Settings.Core;

/// <summary>
/// AI 模型页的保存门禁与文案拼装。从 UI（<c>ModelsPage</c>）里抽出来的纯逻辑，
/// 这样才能被单元测试逐条断言（参考文档 §9.9 的 ⚠️ 硬契约）。
///
/// 全部对应 macOS <c>ModelSettingsView</c>：
/// - 门禁 → <c>persistDraft()</c>（ModelSettingsView.swift:809-824）
/// - 成功文案 → <c>saveAndTest()</c>（:790-796）
/// - 失败文案 → <c>saveAndTest()</c> 的 catch 分支（:802）
/// </summary>
public static class ModelSaveGate
{
    /// <summary>缺 API Key 时的提示。</summary>
    public const string MissingApiKeyMessage = "请粘贴 API Key。";

    /// <summary>测试失败提示的前缀。</summary>
    public const string TestFailurePrefix = "配置已保存，但连接测试失败：";

    /// <summary>两种模型都通了的成功文案前缀。</summary>
    public const string BothModelsSuccessPrefix = "两种模型连接成功。";

    /// <summary>Coding Plan 文字模型通了的成功文案前缀。</summary>
    public const string CodingPlanSuccessPrefix = "Coding Plan 文字模型连接成功：";

    /// <summary>成功文案里响应文本保留的字符数（ModelSettingsView.swift:793）。</summary>
    public const int VisionSuccessPrefixLength = 24;

    /// <summary>Coding Plan 成功文案里响应文本保留的字符数（ModelSettingsView.swift:795）。</summary>
    public const int CodingPlanSuccessPrefixLength = 36;

    /// <summary>
    /// 字段校验。按预设决定是否要求视觉模型，文字模型始终必填。
    /// 合法时返回 null。
    /// </summary>
    public static string? ValidationFailure(AiProviderProfile profile, ProviderPreset preset)
        => profile.ValidationMessage(
            requiresVisionModel: ProviderPresets.SupportsVisionDirectly(preset),
            requiresTextModel: true);

    /// <summary>
    /// API Key 门禁：Key 非空 **或** 已有存储的 Key，否则 <see cref="MissingApiKeyMessage"/>。
    /// 合法时返回 null。
    /// </summary>
    public static string? ApiKeyFailure(string? apiKey, bool hasStoredKey)
        => string.IsNullOrWhiteSpace(apiKey) && !hasStoredKey ? MissingApiKeyMessage : null;

    /// <summary>综合门禁：先校验字段，再校验 Key。合法时返回 null。</summary>
    public static string? PersistFailure(AiProviderProfile profile, ProviderPreset preset, string? apiKey, bool hasStoredKey)
        => ValidationFailure(profile, preset) ?? ApiKeyFailure(apiKey, hasStoredKey);

    /// <summary>截断到前 <paramref name="count"/> 个字符（对应 Swift 的 <c>prefix(_:)</c>）。</summary>
    public static string Truncate(string value, int count)
        => value.Length <= count ? value : value.Substring(0, count);

    /// <summary>
    /// 测试成功的文案。支持视觉时是
    /// <c>两种模型连接成功。文字：&lt;前24&gt; · 视觉：&lt;前24&gt;</c>，
    /// 否则是 <c>Coding Plan 文字模型连接成功：&lt;前36&gt;</c>。
    /// </summary>
    public static string SuccessMessage(bool supportsVision, string textResponse, string visionResponse)
        => supportsVision
            ? $"{BothModelsSuccessPrefix}文字：{Truncate(textResponse, VisionSuccessPrefixLength)} · 视觉：{Truncate(visionResponse, VisionSuccessPrefixLength)}"
            : $"{CodingPlanSuccessPrefix}{Truncate(textResponse, CodingPlanSuccessPrefixLength)}";

    /// <summary>测试失败的文案（配置仍然保留，所以要说清楚）。</summary>
    public static string TestFailureMessage(string error) => TestFailurePrefix + error;
}
