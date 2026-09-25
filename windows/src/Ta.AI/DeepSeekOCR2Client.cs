using System.Net.Http.Headers;
using System.Text.Json.Nodes;

namespace Ta.AI;

/// <summary>
/// DeepSeek-OCR-2 prompt 模式。
///
/// 对应 Mac 版 <c>DeepSeekOCR2Client.swift:3-20</c>（<c>DeepSeekOCRPromptMode</c>）。
/// ⚠️ <see cref="Prompt"/> 逐字一致：<c>&lt;image&gt;\nFree OCR.</c> 这类字面量带真实换行。
/// </summary>
public enum DeepSeekOCRPromptMode
{
    /// <summary>纯文本（推荐截图取字）。</summary>
    PlainText,

    /// <summary>Markdown（保留文档结构）。</summary>
    DocumentMarkdown,
}

public static class DeepSeekOCRPromptModeExtensions
{
    /// <summary>对应 Mac 的 rawValue（配置落盘字符串）。</summary>
    public static string RawValue(this DeepSeekOCRPromptMode mode) => mode switch
    {
        DeepSeekOCRPromptMode.PlainText => "plainText",
        DeepSeekOCRPromptMode.DocumentMarkdown => "documentMarkdown",
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null),
    };

    /// <summary>Mac: DeepSeekOCR2Client.swift:7-12（displayName）。</summary>
    public static string DisplayName(this DeepSeekOCRPromptMode mode) => mode switch
    {
        DeepSeekOCRPromptMode.PlainText => "纯文本（推荐截图取字）",
        DeepSeekOCRPromptMode.DocumentMarkdown => "Markdown（保留文档结构）",
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null),
    };

    /// <summary>Mac: DeepSeekOCR2Client.swift:14-19（prompt，逐字一致）。</summary>
    public static string Prompt(this DeepSeekOCRPromptMode mode) => mode switch
    {
        DeepSeekOCRPromptMode.PlainText => "<image>\nFree OCR.",
        DeepSeekOCRPromptMode.DocumentMarkdown => "<image>\n<|grounding|>Convert the document to markdown.",
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null),
    };

    /// <summary>按 Mac 的 rawValue 反解析；无法识别时返回 null。</summary>
    public static DeepSeekOCRPromptMode? ParseRawValue(string? rawValue)
    {
        if (string.IsNullOrWhiteSpace(rawValue))
        {
            return null;
        }

        return rawValue.Trim() switch
        {
            "plainText" => DeepSeekOCRPromptMode.PlainText,
            "documentMarkdown" => DeepSeekOCRPromptMode.DocumentMarkdown,
            _ => null,
        };
    }

    /// <summary>全部模式，顺序与 Mac 的 <c>CaseIterable</c> 一致。</summary>
    public static IReadOnlyList<DeepSeekOCRPromptMode> AllCases { get; } = new[]
    {
        DeepSeekOCRPromptMode.PlainText,
        DeepSeekOCRPromptMode.DocumentMarkdown,
    };
}

/// <summary>
/// DeepSeek-OCR-2 客户端（vLLM / SGLang / 兼容 OpenAI 的本地部署）。
///
/// 逐条对应 Mac 版 <c>DeepSeekOCR2Client.swift:43-155</c>：
///   · host 为 <c>api.deepseek.com</c> → <see cref="DeepSeekOCR2ClientError.OfficialApiUnsupported"/>
///   · API Key 为空时**完全不发送** Authorization 头（本地服务不需要）
///   · 默认 <c>image/png</c>、<c>max_tokens: 4096</c>、超时 120s、<c>temperature: 0</c>
///   · **不注入** <c>thinking</c>（Mac 的 OCR-2 客户端没有调用 applyThinkingPreference）
/// </summary>
public sealed class DeepSeekOCR2Client
{
    /// <summary>Mac: DeepSeekOCR2Client.swift:44。</summary>
    public const string LatestOfficialModel = "deepseek-ai/DeepSeek-OCR-2";

    /// <summary>Mac: DeepSeekOCR2Client.swift:45。</summary>
    public const string RecommendedLocalBaseUrl = "http://127.0.0.1:8000/v1";

    /// <summary>Mac: DeepSeekOCR2Client.swift:85（OCR-2 max_tokens）。</summary>
    public const int MaxTokens = 4096;

    /// <summary>Mac: DeepSeekOCR2Client.swift:67（OCR-2 超时 120s）。</summary>
    public static readonly TimeSpan RecognitionTimeout = TimeSpan.FromSeconds(120);

    /// <summary>Mac: DeepSeekOCR2Client.swift:120（官方 API 硬性拦截的 host）。</summary>
    private const string OfficialApiHost = "api.deepseek.com";

    private readonly HttpClient _httpClient;

    /// <param name="httpClient">不传则新建；测试用 mock <see cref="HttpMessageHandler"/> 注入。</param>
    public DeepSeekOCR2Client(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? new HttpClient();
    }

    /// <summary>
    /// 发送一次 OCR 请求，返回模型正文（已裁剪首尾空白）。
    ///
    /// Mac: <c>DeepSeekOCR2Client.recognize(baseURL:model:apiKey:imageData:mimeType:mode:)</c>。
    /// </summary>
    public async Task<string> RecognizeAsync(
        string baseURL,
        string model,
        string apiKey,
        byte[] imageData,
        DeepSeekOCRPromptMode mode,
        string mimeType = "image/png",
        CancellationToken cancellationToken = default)
    {
        var trimmedModel = TaText.Trim(model);
        if (trimmedModel.Length == 0)
        {
            throw new DeepSeekOCR2ClientException(new DeepSeekOCR2ClientError.MissingModel());
        }

        var trimmedKey = TaText.Trim(apiKey);

        var endpoint = ProviderEndpointSupport.TryBuild(
            ProviderEndpointRoute.ChatCompletions,
            VisionProviderKind.OpenAICompatible,
            baseURL,
            trimmedModel,
            "DeepSeek OCR 服务地址格式无效。",
            out var endpointError);
        if (endpoint is null)
        {
            throw new DeepSeekOCR2ClientException(new DeepSeekOCR2ClientError.InvalidEndpoint(
                TaText.Trim(baseURL).Length == 0 ? "请先配置 DeepSeek OCR 服务地址。" : endpointError!));
        }

        // Mac: :120-122 —— 官方 API 没有 OCR-2 端点，硬性拦截。
        if (NormalizeHost(endpoint).Equals(OfficialApiHost, StringComparison.Ordinal))
        {
            throw new DeepSeekOCR2ClientException(new DeepSeekOCR2ClientError.OfficialApiUnsupported());
        }

        var dataUrl = $"data:{mimeType};base64,{Convert.ToBase64String(imageData)}";

        // Mac: :75-86 —— prompt 在前、图片在后。
        var body = new JsonObject
        {
            ["model"] = trimmedModel,
            ["messages"] = new JsonArray(new JsonObject
            {
                ["role"] = "user",
                ["content"] = new JsonArray(
                    new JsonObject
                    {
                        ["type"] = "text",
                        ["text"] = mode.Prompt(),
                    },
                    new JsonObject
                    {
                        ["type"] = "image_url",
                        ["image_url"] = new JsonObject
                        {
                            ["url"] = dataUrl,
                        },
                    }),
            }),
            ["temperature"] = 0,
            ["max_tokens"] = MaxTokens,
        };

        // Mac: :69-72 —— Key 为空时连 Authorization 头都不发。
        var hasKey = trimmedKey.Length > 0;

        var result = await ProviderHttp.PostJsonAsync(
                _httpClient,
                endpoint,
                RecognitionTimeout,
                body,
                request =>
                {
                    if (hasKey)
                    {
                        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", trimmedKey);
                    }
                },
                cancellationToken)
            .ConfigureAwait(false);

        if (result.StatusCode is < 200 or >= 300)
        {
            throw new DeepSeekOCR2ClientException(new DeepSeekOCR2ClientError.Server(
                result.StatusCode,
                ProviderHttp.ErrorMessage(result.Body, includeStatusField: false, includeDetailFallback: true)
                ?? ProviderHttp.StatusPhrase(result.StatusCode)));
        }

        // Mac: :99-106 —— 只有「解析成功 + 正文非空」才算成功；不做 reasoning 检测。
        var root = TaJson.TryParseObject(result.Body);
        if (root is not null)
        {
            var text = OpenAIChatResponseSupport.ContentText(root);
            if (!string.IsNullOrEmpty(text))
            {
                return text;
            }
        }

        throw new DeepSeekOCR2ClientException(new DeepSeekOCR2ClientError.EmptyResponse());
    }

    /// <summary>小写 host，IPv6 去掉方括号（对齐 <see cref="ProviderEndpointValidator"/> 的处理）。</summary>
    private static string NormalizeHost(Uri uri)
    {
        var host = (uri.IdnHost ?? uri.Host ?? string.Empty).ToLowerInvariant();
        if (host.Length >= 2 && host[0] == '[' && host[^1] == ']')
        {
            host = host[1..^1];
        }

        return host;
    }
}
