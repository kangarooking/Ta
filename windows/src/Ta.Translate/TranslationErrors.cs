namespace Ta.Translate;

/// <summary>
/// 翻译服务的错误。消息字符串逐条对应 Mac 版
/// <c>ScreenshotTranslationService.swift:6-19</c>（<c>ScreenshotTranslationServiceError</c>），
/// 界面直接展示 <see cref="Message"/> 里的中文原文。
///
/// Mac 版是 <c>LocalizedError</c> 枚举；C# 侧用带 <see cref="Code"/> 的异常表达，
/// 以便 <c>catch</c>时仍可精确区分（对应 Swift 的 case 匹配）。
/// </summary>
public sealed class ScreenshotTranslationServiceError : Exception
{
    public enum Code
    {
        /// <summary>无法为翻译模型编码截图。</summary>
        ImageEncodingFailed,

        /// <summary>没有找到可靠的文字位置，无法生成翻译图片。</summary>
        MissingTextBoxes,

        /// <summary>本地识别无法定位文字，且当前模式不允许视觉回退。</summary>
        LocalOcrUnavailableForImage,

        /// <summary>未配置可用的翻译 Provider（对应 Mac 的 translationIneligible）。</summary>
        NotConfigured,
    }

    public Code ErrorCode { get; }

    private ScreenshotTranslationServiceError(Code code, string message) : base(message)
    {
        ErrorCode = code;
    }

    public static ScreenshotTranslationServiceError ImageEncodingFailed() =>
        new(Code.ImageEncodingFailed, "无法为翻译模型编码截图。");

    /// <summary>Mac: "没有找到可靠的文字位置，无法生成翻译图片；可以改用“翻译文字并复制”。"</summary>
    public static ScreenshotTranslationServiceError MissingTextBoxes() =>
        new(Code.MissingTextBoxes, "没有找到可靠的文字位置，无法生成翻译图片；可以改用“翻译文字并复制”。");

    /// <summary>Mac: "本地文字识别暂时无法定位图片中的文字，因此不能生成全文翻译图片。请重试，或在翻译设置中改用“翻译文字并复制”。"</summary>
    public static ScreenshotTranslationServiceError LocalOcrUnavailableForImage() =>
        new(Code.LocalOcrUnavailableForImage,
            "本地文字识别暂时无法定位图片中的文字，因此不能生成全文翻译图片。请重试，或在翻译设置中改用“翻译文字并复制”。");

    /// <summary>Mac: AIProviderProfileStoreError.translationIneligible 的两条消息合并。</summary>
    public static ScreenshotTranslationServiceError NotConfigured(string message) =>
        new(Code.NotConfigured, message);
}

/// <summary>
/// 翻译 Provider（文字/视觉模型调用）的错误。
///
/// 逐条对应 Mac 版 <c>TranslationProviderClient.swift:3-29</c>（<c>TranslationProviderError</c>）。
/// 本类只定义<b>编排层自己会抛出</b>的用例；HTTP 层的其余用例
/// （invalidEndpoint / server / invalidResponse …）由 Ta.AI 的 <c>ITranslationClient</c>
/// 实现按其自有错误类型抛出 —— 见接口 XML 注释里的说明。
/// </summary>
public sealed class TranslationProviderError : Exception
{
    public enum Code
    {
        /// <summary>输入为空：截图中没有可翻译的文字 / 分段列表为空。</summary>
        EmptyInput,

        /// <summary>模型没有返回完整的分段结果。</summary>
        InvalidSegmentResponse,
    }

    public Code ErrorCode { get; }

    private TranslationProviderError(Code code, string message) : base(message)
    {
        ErrorCode = code;
    }

    /// <summary>Mac: "截图中没有可翻译的文字。"</summary>
    public static TranslationProviderError EmptyInput() =>
        new(Code.EmptyInput, "截图中没有可翻译的文字。");

    /// <summary>Mac: "翻译模型没有返回完整的分段结果，请重试。"</summary>
    public static TranslationProviderError InvalidSegmentResponse() =>
        new(Code.InvalidSegmentResponse, "翻译模型没有返回完整的分段结果，请重试。");
}
