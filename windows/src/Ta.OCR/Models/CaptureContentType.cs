namespace Ta.OCR.Models;

/// <summary>
/// OCR 识别出的内容类型。
///
/// 对应 Mac 版 `CaptureContentType`（`AIScreenshotCore/Models/CaptureModels.swift:81-88`）。
/// rawValue 逐字保留，跨平台设置与 Agent 契约依赖这些字符串。
/// </summary>
public enum CaptureContentType
{
    PlainText,
    Code,
    Table,
    QrCode,
    Formula,
    Image,
}

/// <summary>
/// 内容类型的 rawValue 编解码。字符串值与 Mac 版完全一致。
/// </summary>
public static class CaptureContentTypeNames
{
    public const string PlainText = "plainText";
    public const string Code = "code";
    public const string Table = "table";
    public const string QrCode = "qrCode";
    public const string Formula = "formula";
    public const string Image = "image";

    public static string RawValue(this CaptureContentType value) => value switch
    {
        CaptureContentType.PlainText => PlainText,
        CaptureContentType.Code => Code,
        CaptureContentType.Table => Table,
        CaptureContentType.QrCode => QrCode,
        CaptureContentType.Formula => Formula,
        CaptureContentType.Image => Image,
        _ => PlainText,
    };

    /// <summary>无法识别的字符串回退为 <see cref="CaptureContentType.PlainText"/>（与 Mac 的 `?? ` 默认值语义一致）。</summary>
    public static CaptureContentType Parse(string? rawValue) => rawValue switch
    {
        PlainText => CaptureContentType.PlainText,
        Code => CaptureContentType.Code,
        Table => CaptureContentType.Table,
        QrCode => CaptureContentType.QrCode,
        Formula => CaptureContentType.Formula,
        Image => CaptureContentType.Image,
        _ => CaptureContentType.PlainText,
    };
}
