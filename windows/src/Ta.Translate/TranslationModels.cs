using Ta.Core.Capture;

namespace Ta.Translate;

/// <summary>
/// 截图翻译的三种模式。
///
/// 逐条对应 Mac 版 <c>TranslationModels.swift:3-15</c>（<c>ScreenshotTranslationMode</c>）。
/// rawValue 沿用 Mac 的 camelCase 字符串（会写入配置，不得改动）。
/// </summary>
public enum ScreenshotTranslationMode
{
    /// <summary>翻译文字并复制（快捷键直达此模式，永远只产出文字）。</summary>
    TextOnly,

    /// <summary>全文翻译图片（同尺寸覆盖，文字就地替换）。</summary>
    FullImage,

    /// <summary>双语翻译图片（在原图下方追加面板，源文 + 译文对照）。</summary>
    BilingualImage,
}

public static class ScreenshotTranslationModeExtensions
{
    /// <summary>对应 Mac 的 rawValue（配置落盘字符串，不得改动）。</summary>
    public static string RawValue(this ScreenshotTranslationMode mode) => mode switch
    {
        ScreenshotTranslationMode.TextOnly => "textOnly",
        ScreenshotTranslationMode.FullImage => "fullImage",
        ScreenshotTranslationMode.BilingualImage => "bilingualImage",
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null),
    };

    /// <summary>按 Mac 的 rawValue 反解析；无法识别时返回 null（与 Swift <c>init?(rawValue:)</c> 一致）。</summary>
    public static ScreenshotTranslationMode? ParseRawValue(string? rawValue)
    {
        if (string.IsNullOrWhiteSpace(rawValue))
        {
            return null;
        }

        return rawValue.Trim() switch
        {
            "textOnly" => ScreenshotTranslationMode.TextOnly,
            "fullImage" => ScreenshotTranslationMode.FullImage,
            "bilingualImage" => ScreenshotTranslationMode.BilingualImage,
            _ => null,
        };
    }

    /// <summary>Mac: TranslationModels.swift:8-14（displayName）。界面直接展示。</summary>
    public static string DisplayName(this ScreenshotTranslationMode mode) => mode switch
    {
        ScreenshotTranslationMode.TextOnly => "翻译文字并复制",
        ScreenshotTranslationMode.FullImage => "全文翻译图片",
        ScreenshotTranslationMode.BilingualImage => "双语翻译图片",
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null),
    };

    /// <summary>全部模式，声明顺序与 Mac 的 <c>CaseIterable</c> 一致。</summary>
    public static IReadOnlyList<ScreenshotTranslationMode> AllCases { get; } = new[]
    {
        ScreenshotTranslationMode.TextOnly,
        ScreenshotTranslationMode.FullImage,
        ScreenshotTranslationMode.BilingualImage,
    };
}

/// <summary>
/// 一条带定位的 OCR 文字行 —— 翻译链路（图像模式）必须携带 bbox 才能回写原图。
///
/// 对应 Mac 版 <c>CaptureModels.swift:90-100</c>（<c>OCRTextLine</c>）。
/// ⚠️ 坐标系差异（Windows移植参考文档 §5.1）：Mac 的 <c>boundingBox</c> 是
/// <b>归一化、原点在左下</b> 的 CGRect；Windows 侧统一为 <b>归一化、原点在左上</b>
///（与像素空间同向），因为 WinRT OCR 的 bbox 本就是图像像素坐标、Y 向下，
/// 归一化时不需要任何翻转。
/// </summary>
public readonly record struct OcrTextLine
{
    public string Text { get; init; }
    public float Confidence { get; init; }

    /// <summary>归一化包围盒（0…1，左上原点，Y 向下）。</summary>
    public RectD BoundingBox { get; init; }

    public OcrTextLine(string text, float confidence, RectD boundingBox)
    {
        Text = text;
        Confidence = confidence;
        BoundingBox = boundingBox;
    }
}

/// <summary>
/// 本地 OCR 的整图识别结果。
///
/// 对应 Mac 版 <c>CaptureModels.swift:200-229</c>（<c>OCRResult</c>）。
/// 翻译链路只用 <see cref="Text"/>、<see cref="Confidence"/> 与 <see cref="Document"/>；
/// 其余字段保留以便与 Mac 版逐字段对照（本条目的 <c>contentType/barcodes/engine</c>
/// 由宿主按需填充，翻译流程不读取）。
/// </summary>
public sealed record OcrResult
{
    /// <summary>全文纯文本（各块按阅读顺序拼接）。</summary>
    public required string Text { get; init; }

    /// <summary>行置信度均值。Mac 用 Apple Vision 逐行置信度求均值；Windows 侧由宿主实现填充。</summary>
    public float Confidence { get; init; }

    /// <summary>
    /// 低置信判定阈值。⚠️ <b>0.72 只用于 OCR 侧的 <c>isLowConfidence</c> 提示</b>，
    /// 与翻译流程的 0.55 视觉回退阈值是两个完全不同的数（见参考文档 §14 风险 #33，
    /// <c>ScreenshotTranslationService.PerformTranslationAsync</c> 里的注释）。
    /// </summary>
    public bool IsLowConfidence => Confidence < 0.72f;

    /// <summary>文档版面（块 → 行），翻译图像模式从这里取带 bbox 的文字行。</summary>
    public OcrDocumentLayout Document { get; init; } = OcrDocumentLayout.Empty;
}

/// <summary>文档版面：块列表。对应 Mac: OCRDocumentLayout / OCRDocumentBlock。</summary>
public sealed record OcrDocumentLayout
{
    public static OcrDocumentLayout Empty { get; } = new();

    public IReadOnlyList<OcrDocumentBlock> Blocks { get; init; } = Array.Empty<OcrDocumentBlock>();

    /// <summary>扁平化全部文字行（保留 bbox 与 confidence）。对应 Mac: blocks.flatMap(\.lines)。</summary>
    public IEnumerable<OcrTextLine> Lines => Blocks.SelectMany(b => b.Lines);
}

public sealed record OcrDocumentBlock
{
    public IReadOnlyList<OcrTextLine> Lines { get; init; } = Array.Empty<OcrTextLine>();

    /// <summary>块内文字按行拼接。对应 Mac: OCRDocumentBlock.text。</summary>
    public string Text => string.Join("\n", Lines.Select(l => l.Text));
}

/// <summary>
/// 翻译后的文字行 —— 保留原始 bbox 与 confidence，供渲染器回写原图。
///
/// 对应 Mac 版 <c>TranslatedImageRenderer.swift:4-9</c>（<c>TranslatedOCRLine</c>）。
/// </summary>
public readonly record struct TranslatedOcrLine
{
    public string SourceText { get; init; }
    public string TranslatedText { get; init; }

    /// <summary>归一化包围盒（0…1，左上原点，Y 向下）—— 沿用源 OCR 行的原值，不重算。</summary>
    public RectD BoundingBox { get; init; }

    /// <summary>源行置信度，原值透传。</summary>
    public float Confidence { get; init; }

    public TranslatedOcrLine(string sourceText, string translatedText, RectD boundingBox, float confidence)
    {
        SourceText = sourceText;
        TranslatedText = translatedText;
        BoundingBox = boundingBox;
        Confidence = confidence;
    }
}

/// <summary>
/// 分段翻译的输入项。对应 Mac 版 <c>TranslationModels.swift:17-25</c>（<c>TranslationSourceSegment</c>）。
/// JSON 序列化形状（<c>{"id":…,"text":"…"}</c>）由 Ta.AI 的 HTTP 客户端保证。
/// </summary>
public readonly record struct TranslationSourceSegment(int Id, string Text);

/// <summary>分段翻译的输出项。对应 Mac: TranslationModels.swift:27-35（TranslationSegmentResult）。</summary>
public readonly record struct TranslationSegmentResult(int Id, string Text);
