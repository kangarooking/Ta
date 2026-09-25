using Ta.AI;

namespace Ta.Translate;

/// <summary>
/// 翻译 Provider 客户端的凭据。
///
/// 对应 Mac 版 <c>AIProviderProfileStore</c> 中「选中的翻译 Profile + API Key」这一组信息
/// （<c>ScreenshotTranslationService.swift:134-153</c> 的 requiredProfile 逻辑）。
/// 宿主（Ta.Settings）负责实现 <see cref="ITranslationCredentialsProvider"/>，
/// 翻译模块只消费凭据对象，不直接触碰存储。
/// </summary>
public readonly record struct TranslationCredentials
{
    public string BaseUrl { get; init; }
    public string TextModel { get; init; }
    public string VisionModel { get; init; }
    public string ApiKey { get; init; }

    /// <summary>协议种类。对应 Mac: AIProviderProfile.providerKind（VisionProviderKind）。</summary>
    public VisionProviderKind ProviderKind { get; init; }
}

/// <summary>
/// 翻译凭据的提供者。返回 null 表示「没有一套带文字模型、视觉模型和 API Key 的可用 Profile」，
/// 编排层会抛出 <see cref="ScreenshotTranslationServiceError.Code.NotConfigured"/>
/// （对应 Mac 的 translationIneligible）。
/// </summary>
public interface ITranslationCredentialsProvider
{
    TranslationCredentials? GetCredentials();
}

/// <summary>纯文字翻译请求。对应 Mac: TranslationProviderClient.translateText 的参数（:39-47）。</summary>
public sealed record TranslationTextRequest
{
    public required TranslationCredentials Credentials { get; init; }

    /// <summary>待翻译正文。</summary>
    public required string Text { get; init; }

    /// <summary>源语言（中文自由文本，直接插值 prompt）。</summary>
    public required string SourceLanguage { get; init; }

    /// <summary>目标语言。</summary>
    public required string TargetLanguage { get; init; }
}

/// <summary>
/// 分段翻译请求。对应 Mac: TranslationProviderClient.translateSegments（:69-77）。
/// ⚠️ 分段策略（§9.4b）：<b>一次请求携带全部文字行</b>，无分块、无并发、无大小限制 ——
/// 编排层会把全部段落放进同一个请求，客户端实现也不要自行分块。
/// </summary>
public sealed record TranslationSegmentsRequest
{
    public required TranslationCredentials Credentials { get; init; }

    /// <summary>分段列表（id 从 0 连续编号，顺序即阅读顺序）。</summary>
    public required IReadOnlyList<TranslationSourceSegment> Segments { get; init; }

    public required string SourceLanguage { get; init; }

    public required string TargetLanguage { get; init; }
}

/// <summary>图片翻译（视觉模型）请求。对应 Mac: TranslationProviderClient.translateImage（:105-114）。</summary>
public sealed record TranslationImageRequest
{
    public required TranslationCredentials Credentials { get; init; }

    /// <summary>PNG 字节（Mac 端已把最长边压到 2560，见 prepareOCRImage）。</summary>
    public required byte[] PngData { get; init; }

    /// <summary>MIME 类型，固定 "image/png"。</summary>
    public string MimeType { get; init; } = "image/png";

    public required string SourceLanguage { get; init; }

    public required string TargetLanguage { get; init; }
}

/// <summary>
/// 翻译 Provider 客户端抽象 —— 本模块与 HTTP 层之间<b>唯一</b>的接触面。
///
/// 由 Ta.AI 的 Provider 客户端实现（OpenAI 兼容 / Azure / Anthropic / Gemini 四协议，
/// 见 <see cref="VisionProviderKind"/>）。prompt 原文、temperature 0、max_tokens、
/// json_mode、端点拼接等全部由实现负责 —— 本模块不直接调用 HTTP，也不关心协议细节。
///
/// 错误约定：实现可抛出其自有错误类型（对应 Mac 的
/// <c>TranslationProviderError.invalidEndpoint / server / invalidResponse …</c>），
/// 编排层原样向上传播；只有 <see cref="TranslationProviderError"/>（EmptyInput 等）
/// 由编排层自己抛出。
/// </summary>
public interface ITranslationClient
{
    /// <summary>纯文字翻译（jsonMode = false）。</summary>
    Task<string> TranslateTextAsync(TranslationTextRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// 分段翻译（jsonMode = true）。返回按 id 升序排列的结果；
    /// 对应 Mac: translated.sorted { $0.id &lt; $1.id }。缺少 id 时由实现抛出
    /// 其 invalidSegmentResponse（编排层会原样传播）。
    /// </summary>
    Task<IReadOnlyList<TranslationSegmentResult>> TranslateSegmentsAsync(
        TranslationSegmentsRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>视觉模型图片翻译（整图读字 + 翻译）。</summary>
    Task<string> TranslateImageAsync(TranslationImageRequest request, CancellationToken cancellationToken = default);
}
