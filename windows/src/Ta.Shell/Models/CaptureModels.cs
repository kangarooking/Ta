namespace Ta.Shell.Models;

/// <summary>
/// 六种截图模式。对应 Mac 版 <c>CaptureMode</c>
/// （<c>AIScreenshotCore/Models/CaptureModels.swift:3-10</c>）。
///
/// rawValue 沿用 Mac 的 camelCase，因此持久化数据与设置 key 在两平台一致。
/// </summary>
public enum CaptureMode
{
    /// <summary>开始拓取 —— 框选后再由用户选择操作。</summary>
    Interactive,

    /// <summary>极速识别内容 —— 按识别设置直接走 OCR 或多模态。</summary>
    Intelligent,

    /// <summary>截图翻译。</summary>
    Translation,

    /// <summary>截图图片 —— 始终复制原图。</summary>
    Image,

    /// <summary>截图并钉住。</summary>
    Pin,

    /// <summary>滚动长截图。</summary>
    Long,
}

public static class CaptureModeExtensions
{
    /// <summary>Mac 版 rawValue。用于设置持久化与跨平台一致性。</summary>
    public static string RawValue(this CaptureMode mode) => mode switch
    {
        CaptureMode.Interactive => "interactive",
        CaptureMode.Intelligent => "intelligent",
        CaptureMode.Translation => "translation",
        CaptureMode.Image => "image",
        CaptureMode.Pin => "pin",
        CaptureMode.Long => "long",
        _ => mode.ToString().ToLowerInvariant(),
    };

    /// <summary>从 Mac 版 rawValue 反解析；无法识别返回 null。</summary>
    public static CaptureMode? TryParseRawValue(string? rawValue) => rawValue switch
    {
        "interactive" => CaptureMode.Interactive,
        "intelligent" => CaptureMode.Intelligent,
        "translation" => CaptureMode.Translation,
        "image" => CaptureMode.Image,
        "pin" => CaptureMode.Pin,
        "long" => CaptureMode.Long,
        _ => null,
    };
}

/// <summary>
/// 框选后要执行的动作。对应 Mac 版 <c>CaptureQuickAction</c>
/// （<c>CaptureModels.swift:20-30</c>）。
///
/// Mac 版有 9 个 case（多了 <c>translate</c> 这个早期遗留别名），
/// 本移植只保留 <b>8 个真正被分派</b>的动作 —— 见任务书「动作分派」清单。
/// </summary>
public enum CaptureQuickAction
{
    /// <summary>复制图片（原图，不做任何识别）。</summary>
    CopyImage,

    /// <summary>钉图。</summary>
    Pin,

    /// <summary>本地 OCR 取字。</summary>
    LocalOcr,

    /// <summary>多模态识图。</summary>
    Multimodal,

    /// <summary>识别 + 翻译 + 复制文字。</summary>
    TranslateText,

    /// <summary>原位标注编辑。</summary>
    Edit,

    /// <summary>保存到磁盘。</summary>
    Save,

    /// <summary>AI 美化。⚠️ macOS 版也未实现，入口可见但必须如实标注「开发中」。</summary>
    Beautify,
}

public static class CaptureQuickActionExtensions
{
    /// <summary>Mac 版 rawValue，用于 lastPostCaptureQuickAction 持久化（CaptureCoordinator.swift:189）。</summary>
    public static string RawValue(this CaptureQuickAction action) => action switch
    {
        CaptureQuickAction.CopyImage => "copyImage",
        CaptureQuickAction.Pin => "pin",
        CaptureQuickAction.LocalOcr => "localOCR",
        CaptureQuickAction.Multimodal => "multimodal",
        CaptureQuickAction.TranslateText => "translateText",
        CaptureQuickAction.Edit => "edit",
        CaptureQuickAction.Save => "save",
        CaptureQuickAction.Beautify => "beautify",
        _ => action.ToString(),
    };

    /// <summary>从 lastPostCaptureQuickAction 的存储值反解析。</summary>
    public static CaptureQuickAction? TryParseRawValue(string? rawValue) => rawValue switch
    {
        "copyImage" => CaptureQuickAction.CopyImage,
        "pin" => CaptureQuickAction.Pin,
        "localOCR" => CaptureQuickAction.LocalOcr,
        "multimodal" => CaptureQuickAction.Multimodal,
        "translateText" => CaptureQuickAction.TranslateText,
        "edit" => CaptureQuickAction.Edit,
        "save" => CaptureQuickAction.Save,
        "beautify" => CaptureQuickAction.Beautify,
        _ => null,
    };
}

/// <summary>
/// 「开始拓取」模式下框选后的默认行为。对应 Mac 版 <c>PostCaptureAction</c>
/// （<c>CaptureModels.swift:12-19</c>）。
/// </summary>
public enum PostCaptureAction
{
    /// <summary>每次询问 —— 显示操作栏。</summary>
    Choose,

    /// <summary>直接识别 —— 由识别路由决定 OCR 或多模态。</summary>
    Recognize,

    TranslateText,
    CopyImage,
    Pin,
    Edit,

    /// <summary>沿用上次选择（lastPostCaptureQuickAction）。</summary>
    RememberLast,
}

public static class PostCaptureActionExtensions
{
    public static string RawValue(this PostCaptureAction action) => action switch
    {
        PostCaptureAction.Choose => "choose",
        PostCaptureAction.Recognize => "recognize",
        PostCaptureAction.TranslateText => "translateText",
        PostCaptureAction.CopyImage => "copyImage",
        PostCaptureAction.Pin => "pin",
        PostCaptureAction.Edit => "edit",
        PostCaptureAction.RememberLast => "rememberLast",
        _ => action.ToString(),
    };

    public static PostCaptureAction? TryParseRawValue(string? rawValue) => rawValue switch
    {
        "choose" => PostCaptureAction.Choose,
        "recognize" => PostCaptureAction.Recognize,
        "translateText" => PostCaptureAction.TranslateText,
        "copyImage" => PostCaptureAction.CopyImage,
        "pin" => PostCaptureAction.Pin,
        "edit" => PostCaptureAction.Edit,
        "rememberLast" => PostCaptureAction.RememberLast,
        _ => null,
    };
}

/// <summary>识别路由。对应 Mac 版 <c>RecognitionRoute</c>（<c>CaptureModels.swift:34-38</c>）。</summary>
public enum RecognitionRoute
{
    LocalOcr,
    Multimodal,

    /// <summary>智能 —— 本地优先，低置信度时按用户确认走云端增强。</summary>
    Smart,
}

public static class RecognitionRouteExtensions
{
    public static string RawValue(this RecognitionRoute route) => route switch
    {
        RecognitionRoute.LocalOcr => "localOCR",
        RecognitionRoute.Multimodal => "multimodal",
        RecognitionRoute.Smart => "smart",
        _ => route.ToString(),
    };

    public static RecognitionRoute? TryParseRawValue(string? rawValue) => rawValue switch
    {
        "localOCR" => RecognitionRoute.LocalOcr,
        "multimodal" => RecognitionRoute.Multimodal,
        "smart" => RecognitionRoute.Smart,
        _ => null,
    };
}

/// <summary>
/// 一次捕获流程的结果。对应 Mac 版 <c>CaptureOutcome</c>
/// （CaptureCoordinator.swift:4-8）。
///
/// 相比 Mac 版<b>多出一个 <see cref="NotAvailable"/></b>：用于「AI 美化」这类
/// 入口可见但功能未实现的动作。Mac 版把它报成 <c>.completed("美化入口已预留")</c>，
/// 状态栏会显示成正向结果 —— 这属于谎报成功。移植要求如实标注「开发中」，
/// 因此单独设一类，既不冒充成功也不冒充失败。
/// </summary>
public enum CaptureOutcomeKind
{
    Cancelled,
    Completed,
    Failed,

    /// <summary>功能未实现 / 当前不可用。如实标注「开发中」。</summary>
    NotAvailable,
}

public readonly record struct CaptureOutcome
{
    public CaptureOutcomeKind Kind { get; private init; }
    public string Message { get; private init; }

    private CaptureOutcome(CaptureOutcomeKind kind, string message)
    {
        Kind = kind;
        Message = message;
    }

    /// <summary>取消。对应 Mac 版 .cancelled；取消后状态文本回到「本地识别就绪」。</summary>
    public static CaptureOutcome Cancelled() =>
        new(CaptureOutcomeKind.Cancelled, "已取消");

    public static CaptureOutcome Completed(string message) =>
        new(CaptureOutcomeKind.Completed, message);

    public static CaptureOutcome Failed(string message) =>
        new(CaptureOutcomeKind.Failed, message);

    /// <summary>功能未实现。文案必须以「开发中」如实表述。</summary>
    public static CaptureOutcome NotAvailable(string message) =>
        new(CaptureOutcomeKind.NotAvailable, message);

    public bool WasCancelled => Kind == CaptureOutcomeKind.Cancelled;

    public override string ToString() => $"{Kind}: {Message}";
}
