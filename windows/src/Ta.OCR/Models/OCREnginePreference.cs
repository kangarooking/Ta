namespace Ta.OCR.Models;

/// <summary>
/// OCR 引擎偏好。
///
/// 对应 Mac 版 `OCREnginePreference`（`AIScreenshotCore/Models/CaptureModels.swift:40-62`）。
///
/// ⚠️ rawValue 逐字保留（`appleVision` / `rapidOCR` / `paddleOCR` / `deepSeekOCR2`），
/// 因为设置项 `ocrEngine` 的字符串在跨平台间必须一致（参考文档 §10.6）。
///
/// Windows 差异：
/// · `appleVision` 的 rawValue 在 Windows 上语义是「内置本地引擎」，实现走
///   `Windows.Media.Ocr`。保留原名是为了设置兼容，**不要**改名。
/// · `rapidOCR` 在 macOS 上就是死引擎（无构建脚本、无 catalog、无安装按钮，见参考文档 §9.7 末尾）。
///   Windows 版同样保留枚举以便协议对齐，但默认没有任何可用包。
/// </summary>
public enum OCREnginePreference
{
    AppleVision,
    RapidOCR,
    PaddleOCR,
    DeepSeekOCR2,
}

public static class OCREnginePreferenceExtensions
{
    public const string AppleVisionRaw = "appleVision";
    public const string RapidOCRRaw = "rapidOCR";
    public const string PaddleOCRRaw = "paddleOCR";
    public const string DeepSeekOCR2Raw = "deepSeekOCR2";

    public static string RawValue(this OCREnginePreference engine) => engine switch
    {
        OCREnginePreference.AppleVision => AppleVisionRaw,
        OCREnginePreference.RapidOCR => RapidOCRRaw,
        OCREnginePreference.PaddleOCR => PaddleOCRRaw,
        OCREnginePreference.DeepSeekOCR2 => DeepSeekOCR2Raw,
        _ => AppleVisionRaw,
    };

    /// <summary>显示名。与 Mac 版 `displayName` 逐字一致（`CaptureModels.swift:46-53`）。</summary>
    public static string DisplayName(this OCREnginePreference engine) => engine switch
    {
        OCREnginePreference.AppleVision => "Windows 媒体 OCR（内置）",
        OCREnginePreference.RapidOCR => "RapidOCR 增强包",
        OCREnginePreference.PaddleOCR => "PaddleOCR 增强包",
        OCREnginePreference.DeepSeekOCR2 => "DeepSeek-OCR-2（最新）",
        _ => "Windows 媒体 OCR（内置）",
    };

    /// <summary>本地引擎（非云端）。对应 Mac `isLocalEngine`（`:55-57`）。</summary>
    public static bool IsLocalEngine(this OCREnginePreference engine) => engine != OCREnginePreference.DeepSeekOCR2;

    /// <summary>是否依赖可选增强包。对应 Mac `usesOptionalPack`（`:59-61`）。</summary>
    public static bool UsesOptionalPack(this OCREnginePreference engine)
        => engine == OCREnginePreference.RapidOCR || engine == OCREnginePreference.PaddleOCR;

    /// <summary>
    /// 解析设置项里的 rawValue。空值/未知值回退到 <see cref="OCREnginePreference.AppleVision"/>，
    /// 与 Mac 的 `?? .appleVision` / `?? .appleVision` 一致（`OptionalOCRPackManager.swift:689`、`:698`）。
    /// </summary>
    public static OCREnginePreference ParseEngine(string? rawValue) => rawValue switch
    {
        AppleVisionRaw => OCREnginePreference.AppleVision,
        RapidOCRRaw => OCREnginePreference.RapidOCR,
        PaddleOCRRaw => OCREnginePreference.PaddleOCR,
        DeepSeekOCR2Raw => OCREnginePreference.DeepSeekOCR2,
        _ => OCREnginePreference.AppleVision,
    };
}
