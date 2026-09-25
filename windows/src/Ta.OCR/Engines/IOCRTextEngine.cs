using Ta.Core.Imaging;
using Ta.OCR.Models;

namespace Ta.OCR.Engines;

/// <summary>识别参数。</summary>
public readonly record struct OCRRecognizeOptions
{
    public OCRRecognizeOptions(
        IReadOnlyList<string>? languages = null,
        bool mergeWrappedLines = false)
    {
        Languages = languages ?? [];
        MergeWrappedLines = mergeWrappedLines;
    }

    /// <summary>
    /// BCP-47 语言标签。对应 Mac 的 <c>recognitionLanguages</c>。
    /// 空数组 = 交给引擎自动检测（Mac 翻译路径就传空数组）。
    /// </summary>
    public IReadOnlyList<string> Languages { get; init; }

    /// <summary>是否合并软换行。默认 false，与 Mac 一致。</summary>
    public bool MergeWrappedLines { get; init; }

    public static OCRRecognizeOptions Default { get; } = new();
}

/// <summary>
/// OCR 引擎抽象。
///
/// 内置本地引擎（<see cref="WindowsMediaOcrEngine"/>）与 PaddleOCR 增强包都实现它，
/// 让 <see cref="ConfiguredOCRService"/> 的分发链只依赖接口，不关心背后是 WinRT 还是子进程。
/// </summary>
public interface IOCRTextEngine
{
    /// <summary>引擎当前是否可用（语言包缺失、未安装等情况下为 false）。</summary>
    bool IsAvailable { get; }

    Task<OCRResult> RecognizeAsync(
        RgbaBitmap image,
        OCRRecognizeOptions options,
        CancellationToken cancellationToken);
}
