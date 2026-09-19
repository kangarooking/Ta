using Ta.Shell.Contracts;
using Ta.Shell.Models;

namespace Ta.Shell.Orchestration;

/// <summary>
/// 设置 key 常量。逐字对齐移植参考文档 §10.6（macOS 版 <c>UserDefaults</c> key）。
///
/// ⚠️ key 名必须与 Mac 版完全一致，否则 Windows 版读不到（也写不回）同一份配置 ——
/// 虽然两平台默认不共享存储，但升级/迁移路径依赖 key 相同。
/// </summary>
public static class SettingKeys
{
    public const string PostCaptureAction = "postCaptureAction";
    public const string LastPostCaptureQuickAction = "lastPostCaptureQuickAction";
    public const string RecognitionRoute = "recognitionRoute";
    public const string OcrEngine = "ocrEngine";
    public const string RecognitionLanguages = "recognitionLanguages";
    public const string MergeWrappedLines = "mergeWrappedLines";
    public const string ResultBarDuration = "resultBarDuration";
    public const string MultimodalTaskTemplate = "multimodalTaskTemplate";
    public const string TranslationSourceLanguage = "translationSourceLanguage";
    public const string TranslationTargetLanguage = "translationTargetLanguage";
    public const string TranslationDefaultMode = "translationDefaultMode";
    public const string TranslationUsesVisionFallback = "translationUsesVisionFallback";
}

/// <summary>
/// 影响捕获链路的全部设置。对应 Mac 版在 <c>CaptureCoordinator.action(for:)</c>
/// （:630-664）里就地读的那几个 UserDefaults key。
///
/// 集中成一个不可变快照有两个好处：
///   1. 一次捕获流程内设置不会中途变化（否则动作选择会前后不一致）；
///   2. 编排逻辑不直接依赖 <see cref="ISettingsStore"/>，可被单测构造。
/// </summary>
public sealed record CaptureSettings
{
    /// <summary>「开始拓取」框选后的默认行为。默认 <see cref="PostCaptureAction.Choose"/>。</summary>
    public PostCaptureAction PostCaptureAction { get; init; } = PostCaptureAction.Choose;

    /// <summary>「记住上次动作」读取的上次动作。默认 null（回退为显示操作栏）。</summary>
    public CaptureQuickAction? LastPostCaptureQuickAction { get; init; }

    /// <summary>识别路由。默认 <see cref="RecognitionRoute.LocalOcr"/>。</summary>
    public RecognitionRoute RecognitionRoute { get; init; } = RecognitionRoute.LocalOcr;

    /// <summary>OCR 引擎标识。默认 <c>appleVision</c>。</summary>
    public string OcrEngine { get; init; } = "appleVision";

    /// <summary>识别语言。默认 <c>zh-Hans,en-US</c>。</summary>
    public IReadOnlyList<string> RecognitionLanguages { get; init; } = OcrRequestOptions.DefaultLanguages;

    /// <summary>是否合并换行。默认 false。</summary>
    public bool MergeWrappedLines { get; init; }

    /// <summary>结果条自动隐藏秒数。默认 3。</summary>
    public double ResultBarDuration { get; init; } = Shell.UI.ResultBarLayout.DefaultAutoHideSeconds;

    /// <summary>多模态任务模板。默认 <c>general</c>。</summary>
    public string MultimodalTaskTemplate { get; init; } = "general";

    /// <summary>翻译模式。默认 <c>textOnly</c>。</summary>
    public TranslationMode TranslationDefaultMode { get; init; } = TranslationMode.TextOnly;

    /// <summary>本地识别不可用时是否回退视觉模型。默认 true。</summary>
    public bool TranslationUsesVisionFallback { get; init; } = true;

    /// <summary>
    /// 从设置存储读取一份快照。
    /// 每个 key 的默认值都逐一对齐 Mac 版（CaptureCoordinator.swift:494-512、:633-634）。
    /// </summary>
    public static CaptureSettings Load(ISettingsStore store)
    {
        ArgumentNullException.ThrowIfNull(store);

        var languagesRaw = store.ReadString(SettingKeys.RecognitionLanguages, "zh-Hans,en-US");
        var languages = languagesRaw
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(x => x.Length > 0)
            .ToArray();

        return new CaptureSettings
        {
            PostCaptureAction = PostCaptureActionExtensions.TryParseRawValue(
                store.ReadString(SettingKeys.PostCaptureAction, PostCaptureAction.Choose.RawValue()))
                ?? PostCaptureAction.Choose,

            LastPostCaptureQuickAction = CaptureQuickActionExtensions.TryParseRawValue(
                store.Read(SettingKeys.LastPostCaptureQuickAction)),

            RecognitionRoute = RecognitionRouteExtensions.TryParseRawValue(
                store.ReadString(SettingKeys.RecognitionRoute, RecognitionRoute.LocalOcr.RawValue()))
                ?? RecognitionRoute.LocalOcr,

            OcrEngine = store.ReadString(SettingKeys.OcrEngine, "appleVision"),

            // 解析后为空（比如只写了个逗号）时回退默认列表，而不是留空列表让 OCR 拿到 0 个语言。
            RecognitionLanguages = languages.Length > 0 ? languages : OcrRequestOptions.DefaultLanguages,

            MergeWrappedLines = store.ReadBool(SettingKeys.MergeWrappedLines, false),

            ResultBarDuration = Shell.UI.ResultBarLayout.NormalizeDuration(
                store.ReadDouble(SettingKeys.ResultBarDuration, Shell.UI.ResultBarLayout.DefaultAutoHideSeconds)),

            MultimodalTaskTemplate = store.ReadString(SettingKeys.MultimodalTaskTemplate, "general"),

            TranslationDefaultMode = ParseTranslationMode(
                store.ReadString(SettingKeys.TranslationDefaultMode, "textOnly")),

            TranslationUsesVisionFallback = store.ReadBool(SettingKeys.TranslationUsesVisionFallback, true),
        };
    }

    private static TranslationMode ParseTranslationMode(string raw) => raw switch
    {
        "textOnly" => TranslationMode.TextOnly,
        "fullImage" => TranslationMode.FullImage,
        "bilingual" => TranslationMode.Bilingual,
        _ => TranslationMode.TextOnly,
    };
}
