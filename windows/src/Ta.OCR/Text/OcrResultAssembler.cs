using Ta.OCR.Models;

namespace Ta.OCR.Text;

/// <summary>
/// 把「引擎产出的行」组装成 <see cref="OCRResult"/>。
///
/// 这条管线**内置本地引擎与增强包共用**，逐字对应 Mac 版
/// <c>VisionOCRService.recognizeLegacy</c> 的收尾部分
/// （<c>AIScreenshotCore/OCR/VisionOCRService.swift:73-108</c>），
/// macOS 26 分支用的是同一套逻辑（`:159-194`）。
///
/// <b>产出优先级（顺序即契约）</b>
/// <list type="number">
///   <item>行文本按 \n 连接 → <see cref="OCRTextNormalizer"/> 规范化（`:74-75`）</item>
///   <item>confidence = 行置信度算术平均，无行时为 0（`:76-78`）</item>
///   <item>文字为空但有条码 → 用条码 payload 拼接顶替（`:87-89`）</item>
///   <item>首个表格 rows ≥ 2 → 用 <c>table.tsv</c> **覆盖**正文（`:90-92`）</item>
///   <item>内容类型：有码无字 → qrCode；有表格 → table；否则 <see cref="ContentClassifier"/>（`:93-99`）</item>
/// </list>
/// </summary>
public sealed class OcrResultAssembler
{
    private readonly OCRTextNormalizer _normalizer = new();
    private readonly OCRDocumentLayoutAnalyzer _analyzer = new();
    private readonly ContentClassifier _classifier = new();

    /// <param name="confidenceIsSynthetic">
    /// 传入 true 时结果会打上合成置信度标记（Windows 内置引擎必须传 true，见
    /// <see cref="OCRResult.ConfidenceIsSynthetic"/> 与参考文档 §14 #33）。
    /// </param>
    public OCRResult Assemble(
        IReadOnlyList<OCRTextLine> lines,
        IReadOnlyList<DetectedBarcode> barcodes,
        bool mergeWrappedLines,
        IReadOnlyList<string>? languages = null,
        OCREnginePreference engine = OCREnginePreference.AppleVision,
        bool confidenceIsSynthetic = false)
    {
        // :73 —— 版面分析用的是**已排序**的行。
        var document = _analyzer.Analyze(lines);

        // :74-75
        var rawText = string.Join("\n", lines.Select(line => line.Text));
        var text = _normalizer.Normalize(rawText, mergeWrappedLines);

        // :76-78 —— 行置信度均值；无行时为 0（而不是 NaN）。
        var confidence = lines.Count == 0
            ? 0
            : lines.Select(line => line.Confidence).Sum() / lines.Count;

        // :87-89 —— 文字空、有条码：用 payload 顶替。
        if (text.Length == 0 && barcodes.Count > 0)
        {
            text = string.Join("\n", barcodes.Select(barcode => barcode.Payload));
        }

        // :90-92 —— 表格优先于正文。
        var firstTable = document.Tables.Count > 0 ? document.Tables[0] : null;
        if (firstTable is not null && firstTable.Rows.Count >= 2)
        {
            text = firstTable.Tsv;
        }

        // :93-99 —— 内容类型判定顺序。
        var contentType = barcodes.Count > 0 && lines.Count == 0
            ? CaptureContentType.QrCode
            : document.Tables.Count > 0
                ? CaptureContentType.Table
                : _classifier.Classify(text);

        // :101-108
        return new OCRResult(
            text: text,
            contentType: contentType,
            confidence: confidence,
            languages: languages,
            document: document,
            barcodes: barcodes,
            engine: engine,
            confidenceIsSynthetic: confidenceIsSynthetic);
    }
}
