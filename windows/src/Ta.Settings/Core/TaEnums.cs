namespace Ta.Settings.Core;

/// <summary>
/// Provider 接口协议。对应 macOS <c>VisionProviderKind</c>（MultimodalProviderClient.swift:3-17）。
/// rawValue 字符串必须与 Mac 版一致，因为 <c>aiProviderProfileStateV1</c> 里存的就是这个字符串。
/// </summary>
public enum ProviderKind
{
    /// <summary>OpenAI-compatible（rawValue <c>openAICompatible</c>）。</summary>
    OpenAICompatible,

    /// <summary>Azure OpenAI（rawValue <c>azureOpenAI</c>）。</summary>
    AzureOpenAI,

    /// <summary>Anthropic Claude（rawValue <c>anthropic</c>）。</summary>
    Anthropic,

    /// <summary>Google Gemini（rawValue <c>googleGemini</c>）。</summary>
    GoogleGemini,
}

/// <summary><see cref="ProviderKind"/> 的 rawValue 与显示名（照抄 Mac 版）。</summary>
public static class ProviderKinds
{
    /// <summary>全部取值，顺序同 Mac 版 <c>CaseIterable</c>。</summary>
    public static IReadOnlyList<ProviderKind> All { get; } = new[]
    {
        ProviderKind.OpenAICompatible,
        ProviderKind.AzureOpenAI,
        ProviderKind.Anthropic,
        ProviderKind.GoogleGemini,
    };

    /// <summary>取 rawValue。</summary>
    public static string Raw(ProviderKind kind) => kind switch
    {
        ProviderKind.OpenAICompatible => "openAICompatible",
        ProviderKind.AzureOpenAI => "azureOpenAI",
        ProviderKind.Anthropic => "anthropic",
        ProviderKind.GoogleGemini => "googleGemini",
        _ => "openAICompatible",
    };

    /// <summary>由 rawValue 解析，无法识别时回落 OpenAI-compatible。</summary>
    public static ProviderKind FromRaw(string? raw) => raw switch
    {
        "azureOpenAI" => ProviderKind.AzureOpenAI,
        "anthropic" => ProviderKind.Anthropic,
        "googleGemini" => ProviderKind.GoogleGemini,
        _ => ProviderKind.OpenAICompatible,
    };

    /// <summary>显示名（MultimodalProviderClient.swift:10-16）。</summary>
    public static string DisplayName(ProviderKind kind) => kind switch
    {
        ProviderKind.OpenAICompatible => "OpenAI-compatible",
        ProviderKind.AzureOpenAI => "Azure OpenAI",
        ProviderKind.Anthropic => "Anthropic Claude",
        ProviderKind.GoogleGemini => "Google Gemini",
        _ => "OpenAI-compatible",
    };
}

/// <summary>
/// 默认识别路径。对应 macOS <c>RecognitionRoute</c>（CaptureModels.swift:34-38）。
/// </summary>
public enum RecognitionRoute
{
    /// <summary>OCR 引擎（推荐）。</summary>
    LocalOcr,

    /// <summary>多模态大模型。</summary>
    Multimodal,

    /// <summary>智能路由。</summary>
    Smart,
}

/// <summary><see cref="RecognitionRoute"/> 的 rawValue 与显示名。</summary>
public static class RecognitionRoutes
{
    /// <summary>全部取值，顺序同 Mac 版。</summary>
    public static IReadOnlyList<RecognitionRoute> All { get; } = new[]
    {
        RecognitionRoute.LocalOcr,
        RecognitionRoute.Multimodal,
        RecognitionRoute.Smart,
    };

    /// <summary>rawValue（Mac 版 camelCase 字符串）。</summary>
    public static string Raw(RecognitionRoute route) => route switch
    {
        RecognitionRoute.LocalOcr => "localOCR",
        RecognitionRoute.Multimodal => "multimodal",
        RecognitionRoute.Smart => "smart",
        _ => "localOCR",
    };

    /// <summary>由 rawValue 解析。</summary>
    public static RecognitionRoute FromRaw(string? raw) => raw switch
    {
        "multimodal" => RecognitionRoute.Multimodal,
        "smart" => RecognitionRoute.Smart,
        _ => RecognitionRoute.LocalOcr,
    };

    /// <summary>选择器里的显示名（SettingsView.swift:241-243）。</summary>
    public static string DisplayName(RecognitionRoute route) => route switch
    {
        RecognitionRoute.LocalOcr => "OCR 引擎（推荐）",
        RecognitionRoute.Multimodal => "多模态大模型",
        RecognitionRoute.Smart => "智能路由",
        _ => "OCR 引擎（推荐）",
    };
}

/// <summary>
/// OCR 引擎。对应 macOS <c>OCREnginePreference</c>（CaptureModels.swift:40-62）与参考文档 §9.1。
/// </summary>
public enum OcrEngine
{
    /// <summary>appleVision —— Apple Vision（内置）。</summary>
    AppleVision,

    /// <summary>rapidOCR —— RapidOCR 增强包。</summary>
    RapidOcr,

    /// <summary>paddleOCR —— PaddleOCR 增强包。</summary>
    PaddleOcr,

    /// <summary>deepSeekOCR2 —— DeepSeek-OCR-2（最新）。</summary>
    DeepSeekOcr2,
}

/// <summary><see cref="OcrEngine"/> 的 rawValue、显示名与派生属性。</summary>
public static class OcrEngines
{
    /// <summary>全部取值，顺序同 Mac 版。</summary>
    public static IReadOnlyList<OcrEngine> All { get; } = new[]
    {
        OcrEngine.AppleVision,
        OcrEngine.RapidOcr,
        OcrEngine.PaddleOcr,
        OcrEngine.DeepSeekOcr2,
    };

    /// <summary>rawValue。</summary>
    public static string Raw(OcrEngine engine) => engine switch
    {
        OcrEngine.AppleVision => "appleVision",
        OcrEngine.RapidOcr => "rapidOCR",
        OcrEngine.PaddleOcr => "paddleOCR",
        OcrEngine.DeepSeekOcr2 => "deepSeekOCR2",
        _ => "appleVision",
    };

    /// <summary>由 rawValue 解析，无法识别时回落 appleVision。</summary>
    public static OcrEngine FromRaw(string? raw) => raw switch
    {
        "rapidOCR" => OcrEngine.RapidOcr,
        "paddleOCR" => OcrEngine.PaddleOcr,
        "deepSeekOCR2" => OcrEngine.DeepSeekOcr2,
        _ => OcrEngine.AppleVision,
    };

    /// <summary>显示名（CaptureModels.swift:45-48）。</summary>
    public static string DisplayName(OcrEngine engine) => engine switch
    {
        OcrEngine.AppleVision => "Apple Vision（内置）",
        OcrEngine.RapidOcr => "RapidOCR 增强包",
        OcrEngine.PaddleOcr => "PaddleOCR 增强包",
        OcrEngine.DeepSeekOcr2 => "DeepSeek-OCR-2（最新）",
        _ => "Apple Vision（内置）",
    };

    /// <summary>是否本机引擎（除 DeepSeek-OCR-2 外都是本机）。</summary>
    public static bool IsLocalEngine(OcrEngine engine) => engine != OcrEngine.DeepSeekOcr2;

    /// <summary>是否使用可选增强包。</summary>
    public static bool UsesOptionalPack(OcrEngine engine) => engine is OcrEngine.RapidOcr or OcrEngine.PaddleOcr;
}

/// <summary>
/// 多模态识图任务模板。对应 macOS <c>MultimodalTaskTemplate</c>（MultimodalProviderClient.swift:19-27）。
/// </summary>
public enum MultimodalTask
{
    /// <summary>通用识图。</summary>
    General,

    /// <summary>精确取字。</summary>
    ExtractText,

    /// <summary>翻译成中文。</summary>
    TranslateChinese,

    /// <summary>翻译成英文。</summary>
    TranslateEnglish,

    /// <summary>提取并解释代码。</summary>
    ExplainCode,

    /// <summary>表格转 Markdown。</summary>
    TableMarkdown,

    /// <summary>表格转 CSV。</summary>
    TableCsv,

    /// <summary>公式转 LaTeX。</summary>
    FormulaLaTeX,
}

/// <summary><see cref="MultimodalTask"/> 的 rawValue 与显示名。</summary>
public static class MultimodalTasks
{
    /// <summary>全部取值，顺序同 Mac 版。</summary>
    public static IReadOnlyList<MultimodalTask> All { get; } = new[]
    {
        MultimodalTask.General,
        MultimodalTask.ExtractText,
        MultimodalTask.TranslateChinese,
        MultimodalTask.TranslateEnglish,
        MultimodalTask.ExplainCode,
        MultimodalTask.TableMarkdown,
        MultimodalTask.TableCsv,
        MultimodalTask.FormulaLaTeX,
    };

    /// <summary>rawValue。</summary>
    public static string Raw(MultimodalTask task) => task switch
    {
        MultimodalTask.ExtractText => "extractText",
        MultimodalTask.TranslateChinese => "translateChinese",
        MultimodalTask.TranslateEnglish => "translateEnglish",
        MultimodalTask.ExplainCode => "explainCode",
        MultimodalTask.TableMarkdown => "tableMarkdown",
        MultimodalTask.TableCsv => "tableCSV",
        MultimodalTask.FormulaLaTeX => "formulaLaTeX",
        _ => "general",
    };

    /// <summary>由 rawValue 解析。</summary>
    public static MultimodalTask FromRaw(string? raw) => raw switch
    {
        "extractText" => MultimodalTask.ExtractText,
        "translateChinese" => MultimodalTask.TranslateChinese,
        "translateEnglish" => MultimodalTask.TranslateEnglish,
        "explainCode" => MultimodalTask.ExplainCode,
        "tableMarkdown" => MultimodalTask.TableMarkdown,
        "tableCSV" => MultimodalTask.TableCsv,
        "formulaLaTeX" => MultimodalTask.FormulaLaTeX,
        _ => MultimodalTask.General,
    };

    /// <summary>显示名（MultimodalProviderClient.swift:23-31）。</summary>
    public static string DisplayName(MultimodalTask task) => task switch
    {
        MultimodalTask.General => "通用识图",
        MultimodalTask.ExtractText => "精确取字",
        MultimodalTask.TranslateChinese => "翻译成中文",
        MultimodalTask.TranslateEnglish => "翻译成英文",
        MultimodalTask.ExplainCode => "提取并解释代码",
        MultimodalTask.TableMarkdown => "表格转 Markdown",
        MultimodalTask.TableCsv => "表格转 CSV",
        MultimodalTask.FormulaLaTeX => "公式转 LaTeX",
        _ => "通用识图",
    };
}

/// <summary>
/// 截图翻译模式。对应 macOS <c>ScreenshotTranslationMode</c>（TranslationModels.swift:3-11）。
/// </summary>
public enum TranslationMode
{
    /// <summary>翻译文字并复制。</summary>
    TextOnly,

    /// <summary>全文翻译图片。</summary>
    FullImage,

    /// <summary>双语翻译图片。</summary>
    BilingualImage,
}

/// <summary><see cref="TranslationMode"/> 的 rawValue 与显示名。</summary>
public static class TranslationModes
{
    /// <summary>全部取值，顺序同 Mac 版。</summary>
    public static IReadOnlyList<TranslationMode> All { get; } = new[]
    {
        TranslationMode.TextOnly,
        TranslationMode.FullImage,
        TranslationMode.BilingualImage,
    };

    /// <summary>rawValue。</summary>
    public static string Raw(TranslationMode mode) => mode switch
    {
        TranslationMode.FullImage => "fullImage",
        TranslationMode.BilingualImage => "bilingualImage",
        _ => "textOnly",
    };

    /// <summary>由 rawValue 解析。</summary>
    public static TranslationMode FromRaw(string? raw) => raw switch
    {
        "fullImage" => TranslationMode.FullImage,
        "bilingualImage" => TranslationMode.BilingualImage,
        _ => TranslationMode.TextOnly,
    };

    /// <summary>显示名（TranslationModels.swift:7-9）。</summary>
    public static string DisplayName(TranslationMode mode) => mode switch
    {
        TranslationMode.TextOnly => "翻译文字并复制",
        TranslationMode.FullImage => "全文翻译图片",
        TranslationMode.BilingualImage => "双语翻译图片",
        _ => "翻译文字并复制",
    };
}

/// <summary>
/// 截图完成后行为。对应 macOS <c>PostCaptureAction</c>（CaptureModels.swift:12-21）。
/// </summary>
public enum PostCaptureAction
{
    /// <summary>每次让我选择（推荐）。</summary>
    Choose,

    /// <summary>识别内容并复制。</summary>
    Recognize,

    /// <summary>翻译文字并复制。</summary>
    TranslateText,

    /// <summary>复制图片。</summary>
    CopyImage,

    /// <summary>钉在屏幕上。</summary>
    Pin,

    /// <summary>打开标注。</summary>
    Edit,

    /// <summary>记住上一次操作。</summary>
    RememberLast,
}

/// <summary><see cref="PostCaptureAction"/> 的 rawValue 与显示名。</summary>
public static class PostCaptureActions
{
    /// <summary>全部取值，顺序同 Mac 版。</summary>
    public static IReadOnlyList<PostCaptureAction> All { get; } = new[]
    {
        PostCaptureAction.Choose,
        PostCaptureAction.Recognize,
        PostCaptureAction.TranslateText,
        PostCaptureAction.CopyImage,
        PostCaptureAction.Pin,
        PostCaptureAction.Edit,
        PostCaptureAction.RememberLast,
    };

    /// <summary>rawValue。</summary>
    public static string Raw(PostCaptureAction action) => action switch
    {
        PostCaptureAction.Recognize => "recognize",
        PostCaptureAction.TranslateText => "translateText",
        PostCaptureAction.CopyImage => "copyImage",
        PostCaptureAction.Pin => "pin",
        PostCaptureAction.Edit => "edit",
        PostCaptureAction.RememberLast => "rememberLast",
        _ => "choose",
    };

    /// <summary>由 rawValue 解析。</summary>
    public static PostCaptureAction FromRaw(string? raw) => raw switch
    {
        "recognize" => PostCaptureAction.Recognize,
        "translateText" => PostCaptureAction.TranslateText,
        "copyImage" => PostCaptureAction.CopyImage,
        "pin" => PostCaptureAction.Pin,
        "edit" => PostCaptureAction.Edit,
        "rememberLast" => PostCaptureAction.RememberLast,
        _ => PostCaptureAction.Choose,
    };

    /// <summary>显示名（SettingsView.swift:191-199）。</summary>
    public static string DisplayName(PostCaptureAction action) => action switch
    {
        PostCaptureAction.Choose => "每次让我选择（推荐）",
        PostCaptureAction.Recognize => "识别内容并复制",
        PostCaptureAction.TranslateText => "翻译文字并复制",
        PostCaptureAction.CopyImage => "复制图片",
        PostCaptureAction.Pin => "钉在屏幕上",
        PostCaptureAction.Edit => "打开标注",
        PostCaptureAction.RememberLast => "记住上一次操作",
        _ => "每次让我选择（推荐）",
    };
}

/// <summary>
/// DeepSeek-OCR-2 输出格式。对应 macOS <c>DeepSeekOCRPromptMode</c>（DeepSeekOCR2Client.swift:3-10）。
/// </summary>
public enum DeepSeekOcrPromptMode
{
    /// <summary>纯文本（推荐截图取字）。</summary>
    PlainText,

    /// <summary>Markdown（保留文档结构）。</summary>
    DocumentMarkdown,
}

/// <summary><see cref="DeepSeekOcrPromptMode"/> 的 rawValue 与显示名。</summary>
public static class DeepSeekOcrPromptModes
{
    /// <summary>全部取值，顺序同 Mac 版。</summary>
    public static IReadOnlyList<DeepSeekOcrPromptMode> All { get; } = new[]
    {
        DeepSeekOcrPromptMode.PlainText,
        DeepSeekOcrPromptMode.DocumentMarkdown,
    };

    /// <summary>rawValue。</summary>
    public static string Raw(DeepSeekOcrPromptMode mode)
        => mode == DeepSeekOcrPromptMode.DocumentMarkdown ? "documentMarkdown" : "plainText";

    /// <summary>由 rawValue 解析。</summary>
    public static DeepSeekOcrPromptMode FromRaw(string? raw)
        => raw == "documentMarkdown" ? DeepSeekOcrPromptMode.DocumentMarkdown : DeepSeekOcrPromptMode.PlainText;

    /// <summary>显示名（DeepSeekOCR2Client.swift:7-9）。</summary>
    public static string DisplayName(DeepSeekOcrPromptMode mode) => mode switch
    {
        DeepSeekOcrPromptMode.PlainText => "纯文本（推荐截图取字）",
        DeepSeekOcrPromptMode.DocumentMarkdown => "Markdown（保留文档结构）",
        _ => "纯文本（推荐截图取字）",
    };
}

/// <summary>
/// Agent 云端策略。对应 macOS <c>AgentCloudPolicy</c>（AgentMethods.swift:34-38）。
/// </summary>
public enum AgentCloudPolicy
{
    /// <summary>按能力决定（推荐）。</summary>
    Auto,

    /// <summary>允许已配置的云端模型。</summary>
    Allow,

    /// <summary>始终禁止上传。</summary>
    Deny,
}

/// <summary><see cref="AgentCloudPolicy"/> 的 rawValue 与显示名。</summary>
public static class AgentCloudPolicies
{
    /// <summary>全部取值，顺序同 Mac 版。</summary>
    public static IReadOnlyList<AgentCloudPolicy> All { get; } = new[]
    {
        AgentCloudPolicy.Auto,
        AgentCloudPolicy.Allow,
        AgentCloudPolicy.Deny,
    };

    /// <summary>rawValue。</summary>
    public static string Raw(AgentCloudPolicy policy) => policy switch
    {
        AgentCloudPolicy.Allow => "allow",
        AgentCloudPolicy.Deny => "deny",
        _ => "auto",
    };

    /// <summary>由 rawValue 解析。</summary>
    public static AgentCloudPolicy FromRaw(string? raw) => raw switch
    {
        "allow" => AgentCloudPolicy.Allow,
        "deny" => AgentCloudPolicy.Deny,
        _ => AgentCloudPolicy.Auto,
    };

    /// <summary>选择器显示名（AgentSettingsView.swift:87-89）。</summary>
    public static string DisplayName(AgentCloudPolicy policy) => policy switch
    {
        AgentCloudPolicy.Auto => "按能力决定（推荐）",
        AgentCloudPolicy.Allow => "允许已配置的云端模型",
        AgentCloudPolicy.Deny => "始终禁止上传",
        _ => "按能力决定（推荐）",
    };

    /// <summary>策略说明（AgentSettingsView.swift:230-238）。</summary>
    public static string Description(AgentCloudPolicy policy) => policy switch
    {
        AgentCloudPolicy.Auto => "本地截图和 OCR 留在设备上；识图、翻译或 DeepSeek OCR 等模型能力按任务使用云端。调用方仍可用 cloud=deny 强制本地。",
        AgentCloudPolicy.Allow => "允许 Agent 使用你已经在拓中配置的云端模型。每次调用仍会写入是否上云的审计标记。",
        AgentCloudPolicy.Deny => "所有需要上传图片或文字的 Agent 调用都会被拒绝，只保留本地截图和本地 OCR。",
        _ => string.Empty,
    };
}
