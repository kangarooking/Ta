namespace Ta.Translate;

/// <summary>
/// 翻译配置。
///
/// 逐字段对应 Mac 版 <c>TranslationConfiguration.swift:4-41</c>。
/// 默认值（<c>:5-9</c>）必须逐字一致 —— 见参考文档 §9.5。
///
/// 持久化由宿主（Ta.Settings）负责：键名见 <see cref="TranslationConfigurationKeys"/>，
/// 读取后构造本类型即可；本类型本身不碰存储，便于测试。
/// </summary>
public sealed record TranslationConfiguration
{
    // Mac: TranslationConfiguration.swift:5-9 —— 默认值不得改动。
    public const string DefaultBaseUrl = "https://api.deepseek.com/chat/completions";
    public const string DefaultTextModel = "deepseek-v4-flash";
    public const string DefaultVisionModel = "deepseek-v4-flash-vision-exp";
    public const string DefaultSourceLanguage = "自动检测";
    public const string DefaultTargetLanguage = "简体中文";

    /// <summary>翻译 API 基地址（Provider 侧实际字段；遗留配置键仅在迁移时读取，见 §10.6）。</summary>
    public string BaseUrl { get; init; } = DefaultBaseUrl;

    /// <summary>文字模型。</summary>
    public string TextModel { get; init; } = DefaultTextModel;

    /// <summary>视觉模型。</summary>
    public string VisionModel { get; init; } = DefaultVisionModel;

    /// <summary>源语言。⚠️ 中文自由文本，直接插值进英文 prompt（§9.4b）。</summary>
    public string SourceLanguage { get; init; } = DefaultSourceLanguage;

    /// <summary>目标语言。⚠️ 同上。</summary>
    public string TargetLanguage { get; init; } = DefaultTargetLanguage;

    /// <summary>工具栏「翻译」使用的默认模式。快捷键「翻译文字」强制 textOnly，绕过本字段。</summary>
    public ScreenshotTranslationMode DefaultMode { get; init; } = ScreenshotTranslationMode.TextOnly;

    /// <summary>是否允许在本地 OCR 不可用 / 低置信时切视觉模型。默认 true（与 Mac 一致）。</summary>
    public bool UsesVisionFallback { get; init; } = true;

    /// <summary>Mac: TranslationConfiguration.swift:35-40 —— 目标语言必填。</summary>
    public string? ValidationMessage =>
        string.IsNullOrWhiteSpace(TargetLanguage) ? "请填写目标语言。" : null;

    public static TranslationConfiguration Default() => new();
}

/// <summary>
/// 配置键名。逐条对应 Mac 的 UserDefaults key（参考文档 §10.6）。
/// 宿主（Ta.Settings）用这些键读写；翻译模块只消费构造好的配置对象。
/// </summary>
public static class TranslationConfigurationKeys
{
    public const string BaseUrl = "translationBaseURL";
    public const string TextModel = "translationTextModel";
    public const string VisionModel = "translationVisionModel";
    public const string SourceLanguage = "translationSourceLanguage";
    public const string TargetLanguage = "translationTargetLanguage";
    public const string DefaultMode = "translationDefaultMode";
    public const string UsesVisionFallback = "translationUsesVisionFallback";
}

/// <summary>语言预设。对应 Mac 设置界面里的两个下拉列表（§9.4b / §9.5）。</summary>
public static class TranslationLanguagePresets
{
    /// <summary>源语言：自动检测、英文、简体中文、日文、韩文。</summary>
    public static IReadOnlyList<string> Source { get; } = new[]
    {
        "自动检测", "英文", "简体中文", "日文", "韩文",
    };

    /// <summary>目标语言：简体中文、英文、繁体中文、日文、韩文、西班牙文。</summary>
    public static IReadOnlyList<string> Target { get; } = new[]
    {
        "简体中文", "英文", "繁体中文", "日文", "韩文", "西班牙文",
    };
}

/// <summary>
/// 触发翻译的操作。宿主把截图操作栏的 <c>CaptureQuickAction</c> 映射到这两个值之一。
/// 对应 Mac 版 <c>CaptureQuickAction</c> 中与翻译相关的两个 case。
/// </summary>
public enum TranslationQuickAction
{
    /// <summary>工具栏「翻译」—— 走配置的默认模式。</summary>
    Translate,

    /// <summary>「翻译文字」（快捷键直达）—— 强制 textOnly。</summary>
    TranslateText,
}

/// <summary>
/// 模式路由。
///
/// 逐条对应 Mac 版 <c>TranslationModeRouting.swift:43-57</c>：
/// · <c>.translate</c>（工具栏）→ 配置的默认值；
/// · <c>.translateText</c>（快捷键）→ <b>强制</b> <see cref="ScreenshotTranslationMode.TextOnly"/>。
/// 所以翻译快捷键永远只产出文字，不可能产出图片。
/// </summary>
public static class TranslationModeRouting
{
    public static ScreenshotTranslationMode? Mode(
        TranslationQuickAction action,
        ScreenshotTranslationMode configuredDefault) => action switch
    {
        TranslationQuickAction.Translate => configuredDefault,
        TranslationQuickAction.TranslateText => ScreenshotTranslationMode.TextOnly,
        _ => null,
    };
}
