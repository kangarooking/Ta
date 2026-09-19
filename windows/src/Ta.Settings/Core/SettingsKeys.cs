namespace Ta.Settings.Core;

/// <summary>
/// 全部设置键名与默认值。
///
/// 逐行对应 macOS <c>UserDefaults</c>（参考文档 §10.6「全部 UserDefaults Key」）。
/// ⚠️ 键名字符串必须与 §10.6 完全一致，否则读不到 Mac 版落盘的数据。
/// Windows 上的存储位置由 <see cref="ISettingsStore"/> 的实现决定
/// （默认 JSON 文件 <c>%LOCALAPPDATA%\Ta\settings.json</c>）。
/// </summary>
public static class SettingsKeys
{
    // ---- 常规 ----

    /// <summary>截图后行为，默认 <c>choose</c>。</summary>
    public const string PostCaptureAction = "postCaptureAction";

    /// <summary>「记住上次动作」。</summary>
    public const string LastPostCaptureQuickAction = "lastPostCaptureQuickAction";

    /// <summary>结果条时长，默认 3（滑块 1.5…8 步进 0.5）。</summary>
    public const string ResultBarDuration = "resultBarDuration";

    /// <summary>是否保存截图历史，默认 false。</summary>
    public const string SaveHistory = "saveHistory";

    // ---- 识别 ----

    /// <summary>识别路由，默认 <c>localOCR</c>。</summary>
    public const string RecognitionRoute = "recognitionRoute";

    /// <summary>OCR 引擎，默认 <c>appleVision</c>（Windows 上等价的本地引擎）。</summary>
    public const string OcrEngine = "ocrEngine";

    /// <summary>识别语言，默认 <c>zh-Hans,en-US</c>。</summary>
    public const string RecognitionLanguages = "recognitionLanguages";

    /// <summary>自动合并疑似断行，默认 **false**。</summary>
    public const string MergeWrappedLines = "mergeWrappedLines";

    /// <summary>多模态任务模板，默认 <c>general</c>。</summary>
    public const string MultimodalTaskTemplate = "multimodalTaskTemplate";

    /// <summary>DeepSeek-OCR-2 服务地址，默认空。</summary>
    public const string DeepSeekOcrBaseUrl = "deepSeekOCRBaseURL";

    /// <summary>DeepSeek-OCR-2 模型名，默认 <c>deepseek-ai/DeepSeek-OCR-2</c>。</summary>
    public const string DeepSeekOcrModel = "deepSeekOCRModel";

    /// <summary>DeepSeek-OCR-2 输出格式，默认 <c>plainText</c>。</summary>
    public const string DeepSeekOcrPromptMode = "deepSeekOCRPromptMode";

    /// <summary>OCR 增强包目录地址（设置时必须 https）。</summary>
    public const string OcrPackCatalogUrl = "ocrPackCatalogURL";

    // ---- 翻译 ----

    /// <summary>翻译源语言，默认「自动检测」。</summary>
    public const string TranslationSourceLanguage = "translationSourceLanguage";

    /// <summary>翻译目标语言，默认「简体中文」。</summary>
    public const string TranslationTargetLanguage = "translationTargetLanguage";

    /// <summary>翻译模式，默认 <c>textOnly</c>。</summary>
    public const string TranslationDefaultMode = "translationDefaultMode";

    /// <summary>本地 OCR 置信度较低时使用视觉模型，默认 **true**。</summary>
    public const string TranslationUsesVisionFallback = "translationUsesVisionFallback";

    // ---- AI 模型（Provider）----

    /// <summary>Provider 状态 JSON blob（<see cref="AiProviderProfileState"/>）。</summary>
    public const string AiProviderProfileState = "aiProviderProfileStateV1";

    /// <summary>遗留键，仅迁移读取：基础地址。</summary>
    public const string LegacyProviderBaseUrl = "providerBaseURL";

    /// <summary>遗留键，仅迁移读取：视觉模型。</summary>
    public const string LegacyProviderVisionModel = "providerVisionModel";

    /// <summary>遗留键，仅迁移读取：文字模型。</summary>
    public const string LegacyProviderTextModel = "providerTextModel";

    /// <summary>遗留键，仅迁移读取：接口协议。</summary>
    public const string LegacyProviderKind = "providerKind";

    /// <summary>遗留键，仅迁移读取：翻译服务地址。</summary>
    public const string LegacyTranslationBaseUrl = "translationBaseURL";

    /// <summary>遗留键，仅迁移读取：翻译文字模型。</summary>
    public const string LegacyTranslationTextModel = "translationTextModel";

    /// <summary>遗留键，仅迁移读取：翻译视觉模型。</summary>
    public const string LegacyTranslationVisionModel = "translationVisionModel";

    // ---- 快捷键（globalHotKey.&lt;rawValue&gt;）----

    /// <summary>生成 <c>globalHotKey.&lt;action&gt;</c> 键名（对应 §10.6 与 §4.2）。</summary>
    public static string GlobalHotKey(string actionRawValue) => $"globalHotKey.{actionRawValue}";

    // ---- Agent ----

    /// <summary>Agent 开关，默认 **true**。</summary>
    public const string AgentAccessEnabled = "agentAccessEnabled";

    /// <summary>Agent 无感自动截图，默认 **true**。</summary>
    public const string AgentAutomaticCaptureAllowed = "agentAutomaticCaptureAllowed";

    /// <summary>Agent 云策略，默认 <c>auto</c>。</summary>
    public const string AgentCloudPolicy = "agentCloudPolicy";

    /// <summary>隐私 App 黑名单，默认空（按 <c>,;\n</c> 分隔）。</summary>
    public const string AgentPrivacyDenylist = "agentPrivacyDenylist";

    /// <summary>允许 Agent 截取拓自身，默认 **true**。</summary>
    public const string AgentAllowCaptureTa = "agentAllowCaptureTa";
}

/// <summary>设置项默认值（键名见 <see cref="SettingsKeys"/>，数值见参考文档 §10.6）。</summary>
public static class SettingsDefaults
{
    /// <summary>识别语言默认 <c>zh-Hans,en-US</c>。</summary>
    public const string RecognitionLanguages = "zh-Hans,en-US";

    /// <summary>结果条时长默认 3 秒。</summary>
    public const double ResultBarDuration = 3.0;

    /// <summary>结果条时长范围与步进（参考文档 §10.6）。</summary>
    public const double ResultBarDurationMin = 1.5;

    /// <summary>结果条时长上界。</summary>
    public const double ResultBarDurationMax = 8.0;

    /// <summary>结果条时长步进。</summary>
    public const double ResultBarDurationStep = 0.5;

    /// <summary>自动合并疑似断行默认 **false**。</summary>
    public const bool MergeWrappedLines = false;

    /// <summary>视觉回退默认 **true**。</summary>
    public const bool TranslationUsesVisionFallback = true;

    /// <summary>Agent 开关默认 **true**。</summary>
    public const bool AgentAccessEnabled = true;

    /// <summary>Agent 自动截图默认 **true**。</summary>
    public const bool AgentAutomaticCaptureAllowed = true;

    /// <summary>允许截取拓自身默认 **true**。</summary>
    public const bool AgentAllowCaptureTa = true;
}
