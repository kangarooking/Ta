namespace Ta.AI;

/// <summary>
/// 识图客户端错误分类。
///
/// 逐条对应 Mac 版 <c>OpenAICompatibleVisionClient.swift:3-26</c>（<c>VisionClientError</c>）。
/// 用 record 层次结构表达「枚举 + 关联值」，天然可比较（对应 Swift 的 <c>Equatable</c>）。
/// </summary>
public abstract record VisionClientError
{
    /// <summary>Mac: <c>errorDescription</c>。界面直接展示这段中文原文。</summary>
    public abstract string ErrorDescription { get; }

    /// <summary>Base URL 缺失 / 不合法；<paramref name="Message"/> 为具体原因。</summary>
    public sealed record InvalidEndpoint(string Message) : VisionClientError
    {
        public override string ErrorDescription => Message;
    }

    /// <summary>没填视觉模型。</summary>
    public sealed record MissingModel : VisionClientError
    {
        public override string ErrorDescription => "尚未配置视觉模型。";
    }

    /// <summary>没填 API Key。</summary>
    public sealed record MissingApiKey : VisionClientError
    {
        public override string ErrorDescription => "尚未配置 API Key。";
    }

    /// <summary>响应体解析不出来。</summary>
    public sealed record InvalidResponse : VisionClientError
    {
        public override string ErrorDescription => "模型服务返回了无法解析的响应。";
    }

    /// <summary>解析成功但正文为空。</summary>
    public sealed record EmptyResponse : VisionClientError
    {
        public override string ErrorDescription => "模型没有返回识别内容。";
    }

    /// <summary>
    /// 只返回了思考过程。
    /// Mac: <c>.reasoningOnlyOutput(truncated:)</c>；<see cref="Truncated"/> 为 true 表示
    /// <c>finish_reason == "length"</c>，即输出上限被思考过程耗尽。
    /// </summary>
    public sealed record ReasoningOnlyOutput(bool Truncated) : VisionClientError
    {
        public override string ErrorDescription => Truncated
            ? "识别失败：输出上限被思考过程耗尽，还没生成正文就被截断了。请关闭该模型的深度思考，或调大输出上限后重试。"
            : "模型只返回了思考过程，没有识别正文。请关闭深度思考后重试。";
    }

    /// <summary>HTTP 非 2xx。</summary>
    public sealed record Server(int StatusCode, string Message) : VisionClientError
    {
        public override string ErrorDescription => $"模型服务错误（{StatusCode}）：{Message}";
    }
}

/// <summary>识图客户端抛出的异常；<see cref="Error"/> 承载分类，<see cref="Exception.Message"/> 是界面文案。</summary>
public sealed class VisionClientException : Exception
{
    public VisionClientException(VisionClientError error)
        : base(error.ErrorDescription)
    {
        Error = error;
    }

    public VisionClientError Error { get; }
}

/// <summary>
/// 翻译客户端错误分类。
///
/// 对应 Mac 版 <c>TranslationProviderClient.swift:3-30</c>（<c>TranslationProviderError</c>）。
/// 比识图多 <see cref="EmptyInput"/> 与 <see cref="InvalidSegmentResponse"/>。
/// </summary>
public abstract record TranslationProviderError
{
    public abstract string ErrorDescription { get; }

    public sealed record InvalidEndpoint(string Message) : TranslationProviderError
    {
        public override string ErrorDescription => Message;
    }

    public sealed record MissingModel : TranslationProviderError
    {
        public override string ErrorDescription => "尚未配置翻译模型。";
    }

    public sealed record MissingApiKey : TranslationProviderError
    {
        public override string ErrorDescription => "尚未配置翻译 API Key。";
    }

    /// <summary>截图里没有可翻译的文字。</summary>
    public sealed record EmptyInput : TranslationProviderError
    {
        public override string ErrorDescription => "截图中没有可翻译的文字。";
    }

    public sealed record InvalidResponse : TranslationProviderError
    {
        public override string ErrorDescription => "翻译服务返回了无法解析的响应。";
    }

    public sealed record EmptyResponse : TranslationProviderError
    {
        public override string ErrorDescription => "翻译模型没有返回内容。";
    }

    /// <summary>分段翻译返回的 id 集合或数量与输入不一致（或 JSON 形态不对）。</summary>
    public sealed record InvalidSegmentResponse : TranslationProviderError
    {
        public override string ErrorDescription => "翻译模型没有返回完整的分段结果，请重试。";
    }

    public sealed record ReasoningOnlyOutput(bool Truncated) : TranslationProviderError
    {
        public override string ErrorDescription => Truncated
            ? "模型把输出上限耗在思考过程里，还没生成正文就被截断了。请关闭该模型的深度思考，或调大输出上限后重试。"
            : "模型只返回了思考过程，没有正文内容。请为该配置关闭深度思考后重试。";
    }

    public sealed record Server(int StatusCode, string Message) : TranslationProviderError
    {
        public override string ErrorDescription => $"翻译服务错误（{StatusCode}）：{Message}";
    }
}

public sealed class TranslationProviderException : Exception
{
    public TranslationProviderException(TranslationProviderError error)
        : base(error.ErrorDescription)
    {
        Error = error;
    }

    public TranslationProviderError Error { get; }
}

/// <summary>
/// DeepSeek-OCR-2 客户端错误分类。
///
/// 对应 Mac 版 <c>DeepSeekOCR2Client.swift:22-41</c>（<c>DeepSeekOCR2ClientError</c>）。
/// 独有 <see cref="OfficialApiUnsupported"/>：官方 API 没提供 OCR-2 端点。
/// </summary>
public abstract record DeepSeekOCR2ClientError
{
    public abstract string ErrorDescription { get; }

    public sealed record InvalidEndpoint(string Message) : DeepSeekOCR2ClientError
    {
        public override string ErrorDescription => Message;
    }

    public sealed record OfficialApiUnsupported : DeepSeekOCR2ClientError
    {
        public override string ErrorDescription =>
            "DeepSeek 官方 API 当前没有提供 DeepSeek-OCR-2 模型端点；请填写部署了该模型的 vLLM、SGLang 或兼容服务地址。";
    }

    public sealed record MissingModel : DeepSeekOCR2ClientError
    {
        public override string ErrorDescription => "尚未配置 DeepSeek OCR 模型名。";
    }

    public sealed record InvalidResponse : DeepSeekOCR2ClientError
    {
        public override string ErrorDescription => "DeepSeek OCR 服务返回了无法解析的响应。";
    }

    public sealed record EmptyResponse : DeepSeekOCR2ClientError
    {
        public override string ErrorDescription => "DeepSeek OCR 没有返回识别内容。";
    }

    public sealed record Server(int StatusCode, string Message) : DeepSeekOCR2ClientError
    {
        public override string ErrorDescription => $"DeepSeek OCR 服务错误（{StatusCode}）：{Message}";
    }
}

public sealed class DeepSeekOCR2ClientException : Exception
{
    public DeepSeekOCR2ClientException(DeepSeekOCR2ClientError error)
        : base(error.ErrorDescription)
    {
        Error = error;
    }

    public DeepSeekOCR2ClientError Error { get; }
}
