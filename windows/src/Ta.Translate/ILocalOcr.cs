using Ta.Core.Imaging;

namespace Ta.Translate;

/// <summary>
/// 本地 OCR 引擎抽象 —— 翻译链路第一步（本机取字）的接口。
///
/// 由 Ta.OCR 实现（Windows 侧对应 Windows.Media.Ocr）。
/// ⚠️ 翻译路径的 OCR <b>必须返回带定位的行</b>（图像模式要按 bbox 回写译文），
/// 因此 <see cref="OcrResult.Document"/> 是必需输出而非可选装饰。
///
/// ⚠️ bbox 约定（参考文档 §14 风险 #33）：WinRT OCR 的 bbox 是<b>图像像素坐标</b>，
/// 实现必须归一化为 0…1；Windows 侧统一<b>左上原点、Y 向下</b>（与像素空间同向），
/// 归一化即 x/W、y/H，不需要任何 Y 翻转。行置信度 WinRT 不逐行提供 ——
/// 实现需合成 <see cref="OcrResult.Confidence"/>（例如统一 1.0，或按行宽/字号启发式），
/// 翻译流程的 0.55 阈值依赖该值，实现需在文档里说明取值方式。
/// </summary>
public interface ILocalOcrEngine
{
    /// <summary>识别整图。失败时抛异常 —— 编排层据此决定是否切视觉模型。</summary>
    Task<OcrResult> RecognizeAsync(RgbaBitmap image, CancellationToken cancellationToken = default);
}

/// <summary>
/// 剪贴板写入抽象。由宿主（Ta.Platform / Ta.Shell）实现。
///
/// 对应 Mac 版 <c>ClipboardService</c>（<c>copyText</c> / <c>copyImage</c> +
/// <c>ClipboardCommitPolicy</c>）。提交策略（initialChangeCount + jobIsLatest）由宿主实现，
/// 本模块只把捕获时刻的 changeCount 与作业新鲜度原样传下去；
/// 返回 false 表示「识别期间剪贴板有变化，未自动覆盖」，对应 Mac 的 showClipboardChanged 分支。
/// </summary>
public interface ITranslationClipboard
{
    /// <summary>当前剪贴板序列号。对应 Mac: NSPasteboard.changeCount。</summary>
    int ChangeCount { get; }

    /// <summary>复制文本。返回是否真正写入。</summary>
    bool TryCopyText(string text, int initialChangeCount, bool jobIsLatest);

    /// <summary>复制图像（PNG）。返回是否真正写入。</summary>
    bool TryCopyImage(RgbaBitmap image, int initialChangeCount, bool jobIsLatest);
}

/// <summary>
/// 进度提示。对应 Mac 的 <c>ResultBarState(kind: .processing, title:, detail:)</c>。
/// </summary>
public sealed record TranslationProgress
{
    public required string Title { get; init; }

    /// <summary>副标题（可为空）。</summary>
    public string? Detail { get; init; }
}

/// <summary>
/// 一次翻译编排的最终结果。合并了 Mac 版 textOnly 与图像模式两条完成路径
/// （<c>finishTextTranslation</c> / performTranslation 尾部）。
/// </summary>
public sealed record TranslationOutcome
{
    public required ScreenshotTranslationMode Mode { get; init; }

    /// <summary>textOnly 模式的译文；图像模式为 null。</summary>
    public string? TranslatedText { get; init; }

    /// <summary>图像模式渲染出的成品图；textOnly 为 null。</summary>
    public RgbaBitmap? RenderedImage { get; init; }

    /// <summary>是否已写入剪贴板（false = 识别期间剪贴板被占用，未覆盖）。</summary>
    public bool CopiedToClipboard { get; init; }

    /// <summary>结果条标题。对应 Mac: "翻译结果已复制" / "已生成全文翻译图片" 等。</summary>
    public required string Message { get; init; }
}
