namespace Ta.OCR.Models;

/// <summary>
/// 一行 OCR 文本及其位置。
///
/// 对应 Mac 版 `OCRTextLine`（`AIScreenshotCore/Models/CaptureModels.swift:90-100`）。
/// Mac 用 `Float` 存 confidence，这里用 `double`：Windows 侧的 confidence 要么是合成常量，
/// 要么来自增强包 JSON（双精度），统一到 double 可避免来回转换。
/// </summary>
public readonly record struct OCRTextLine
{
    public OCRTextLine(string text, double confidence, NormalizedRect boundingBox)
    {
        Text = text;
        Confidence = confidence;
        BoundingBox = boundingBox;
    }

    public string Text { get; }

    public double Confidence { get; }

    public NormalizedRect BoundingBox { get; }
}

/// <summary>
/// 文档块（段落）。
///
/// 对应 Mac 版 `OCRDocumentBlock`（`CaptureModels.swift:102-106`）。
/// </summary>
public sealed record OCRDocumentBlock
{
    public OCRDocumentBlock(IReadOnlyList<OCRTextLine> lines)
    {
        Lines = lines;
    }

    public IReadOnlyList<OCRTextLine> Lines { get; }

    /// <summary>块内文本，按 \n 连接。对应 Mac `text`（`:104`）。</summary>
    public string Text => string.Join("\n", Lines.Select(line => line.Text));
}

/// <summary>
/// 表格。
///
/// 对应 Mac 版 `OCRTable`（`CaptureModels.swift:108-122`）。
/// <see cref="Markdown"/> 与 <see cref="Tsv"/> 的逐字输出被 macOS 版测试锁定
/// （`OCRDocumentLayoutAnalyzerTests.swift:16`），必须与 Mac 完全一致。
/// </summary>
public sealed record OCRTable
{
    public OCRTable(IReadOnlyList<IReadOnlyList<string>> rows)
    {
        Rows = rows;
    }

    public IReadOnlyList<IReadOnlyList<string>> Rows { get; }

    /// <summary>
    /// Markdown 表格。逐字对应 Mac `markdown`（`CaptureModels.swift:112-119`）：
    /// 列数取首行列数，短行右侧补空串，多余列截断。
    /// </summary>
    public string Markdown
    {
        get
        {
            if (Rows.Count == 0 || Rows[0].Count == 0)
            {
                return string.Empty;
            }

            var columnCount = Rows[0].Count;
            var normalized = Rows
                .Select(row => row.Concat(Enumerable.Repeat(string.Empty, Math.Max(0, columnCount - row.Count))).Take(columnCount))
                .ToList();

            var header = "| " + string.Join(" | ", normalized[0]) + " |";
            var divider = "| " + string.Join(" | ", Enumerable.Repeat("---", columnCount)) + " |";
            var body = normalized.Skip(1).Select(row => "| " + string.Join(" | ", row) + " |");

            return string.Join("\n", new[] { header, divider }.Concat(body));
        }
    }

    /// <summary>TSV。对应 Mac `tsv`（`:121`）。</summary>
    public string Tsv => string.Join("\n", Rows.Select(row => string.Join("\t", row)));
}

/// <summary>
/// 文档版面（块 + 表格）。
///
/// 对应 Mac 版 `OCRDocumentLayout`（`CaptureModels.swift:124-131`）。
/// </summary>
public sealed record OCRDocumentLayout
{
    public OCRDocumentLayout(
        IReadOnlyList<OCRDocumentBlock> blocks,
        IReadOnlyList<OCRTable> tables)
    {
        Blocks = blocks;
        Tables = tables;
    }

    /// <summary>空版面。</summary>
    public static OCRDocumentLayout Empty { get; } = new([], []);

    public IReadOnlyList<OCRDocumentBlock> Blocks { get; }

    public IReadOnlyList<OCRTable> Tables { get; }
}

/// <summary>
/// 检测到的条码/二维码。
///
/// 对应 Mac 版 `DetectedBarcode`（`CaptureModels.swift:133-141`），由
/// `VNDetectBarcodesRequest` 产出。
///
/// ⚠️ Windows 侧目前**始终为空**：`Windows.Media.Ocr` 没有任何条码 API，
/// 这是参考文档 §14 风险 #34。后果是本地引擎路径上
/// 「文字为空但有条码 → 用 payload 顶替」这条分支不会触发，
/// 单行 URL 文本仍会被 <see cref="ContentClassifier"/> 判成
/// <see cref="CaptureContentType.QrCode"/>，所以内容类型只丢「真条码」这一路。
/// 补齐需要引入 ZXing.Net（见交付报告）。
/// </summary>
public readonly record struct DetectedBarcode
{
    public DetectedBarcode(string payload, string symbology, NormalizedRect boundingBox)
    {
        Payload = payload;
        Symbology = symbology;
        BoundingBox = boundingBox;
    }

    public string Payload { get; }

    public string Symbology { get; }

    public NormalizedRect BoundingBox { get; }
}

/// <summary>
/// 一次识别的完整结果。
///
/// 对应 Mac 版 `OCRResult`（`CaptureModels.swift:200-229`）。
/// </summary>
public sealed record OCRResult
{
    public OCRResult(
        string text,
        CaptureContentType contentType,
        double confidence,
        IReadOnlyList<string>? languages = null,
        OCRDocumentLayout? document = null,
        IReadOnlyList<DetectedBarcode>? barcodes = null,
        OCREnginePreference engine = OCREnginePreference.AppleVision,
        bool confidenceIsSynthetic = false)
    {
        Text = text;
        ContentType = contentType;
        Confidence = confidence;
        Languages = languages ?? [];
        Document = document ?? OCRDocumentLayout.Empty;
        Barcodes = barcodes ?? [];
        Engine = engine;
        ConfidenceIsSynthetic = confidenceIsSynthetic;
    }

    public string Text { get; }

    public CaptureContentType ContentType { get; }

    public double Confidence { get; }

    public IReadOnlyList<string> Languages { get; }

    public OCRDocumentLayout Document { get; }

    public IReadOnlyList<DetectedBarcode> Barcodes { get; }

    public OCREnginePreference Engine { get; }

    /// <summary>
    /// 置信度是否为**合成值**。
    ///
    /// ⚠️ WinRT OCR 不返回逐行/逐词置信度（参考文档 §14 #33），所以内置本地引擎产出的
    /// confidence 是一个人为指定的常量。该值**不可与 Mac 的真实置信度直接比较**，
    /// 凡是跨平台比较、统计、上报 confidence 的地方都应先看这个标志。
    /// </summary>
    public bool ConfidenceIsSynthetic { get; }

    /// <summary>低置信度。阈值 0.72，逐字对应 Mac `isLowConfidence`（`CaptureModels.swift:227-229`）。</summary>
    public bool IsLowConfidence => Confidence < 0.72;
}
