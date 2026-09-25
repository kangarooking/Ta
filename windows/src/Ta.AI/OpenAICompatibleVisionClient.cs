using System.Net.Http.Headers;
using System.Text.Json.Nodes;

namespace Ta.AI;

/// <summary>
/// OpenAI 兼容（chat/completions）识图客户端。
///
/// 逐条对应 Mac 版 <c>Recognition/OpenAICompatibleVisionClient.swift:28-141</c>。
/// 这是 <see cref="VisionProviderKind.OpenAICompatible"/> 的实际实现，
/// <see cref="MultimodalProviderClient"/> 会把该 kind 直接委托到这里。
///
/// 关键差异（与 Azure/Anthropic/Gemini 分支相比）：<c>max_tokens</c> 是 **4096**（不是 2048）。
/// </summary>
public sealed class OpenAICompatibleVisionClient
{
    /// <summary>Mac: OpenAICompatibleVisionClient.swift:67（识图 max_tokens）。</summary>
    public const int VisionMaxTokens = 4096;

    /// <summary>Mac: OpenAICompatibleVisionClient.swift:51（识图超时 60s）。</summary>
    public static readonly TimeSpan VisionTimeout = TimeSpan.FromSeconds(60);

    private readonly HttpClient _httpClient;

    /// <param name="httpClient">不传则新建；测试用 mock <see cref="HttpMessageHandler"/> 注入。</param>
    public OpenAICompatibleVisionClient(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? new HttpClient();
    }

    /// <summary>
    /// 发送一次识图请求，返回模型正文（已裁剪首尾空白）。
    ///
    /// Mac: <c>OpenAICompatibleVisionClient.recognize(baseURL:model:apiKey:imageData:mimeType:prompt:)</c>。
    /// </summary>
    public async Task<string> RecognizeAsync(
        string baseURL,
        string model,
        string apiKey,
        byte[] imageData,
        string prompt,
        string mimeType = "image/jpeg",
        CancellationToken cancellationToken = default)
    {
        var trimmedModel = TaText.Trim(model);
        if (trimmedModel.Length == 0)
        {
            throw new VisionClientException(new VisionClientError.MissingModel());
        }

        var trimmedKey = TaText.Trim(apiKey);
        if (trimmedKey.Length == 0)
        {
            throw new VisionClientException(new VisionClientError.MissingApiKey());
        }

        var endpoint = ProviderEndpointSupport.TryBuild(
            ProviderEndpointRoute.ChatCompletions,
            VisionProviderKind.OpenAICompatible,
            baseURL,
            trimmedModel,
            "Base URL 格式无效。",
            out var endpointError);
        if (endpoint is null)
        {
            // Mac: :98-107 —— 空 Base URL 与校验失败用不同的文案。
            throw new VisionClientException(new VisionClientError.InvalidEndpoint(
                TaText.Trim(baseURL).Length == 0 ? "请先配置 Base URL。" : endpointError!));
        }

        var dataUrl = $"data:{mimeType};base64,{Convert.ToBase64String(imageData)}";

        // Mac: :56-67 —— 文字在前，图片在后（image_url 用 data URL）。
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
                        ["text"] = prompt,
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
            ["max_tokens"] = VisionMaxTokens,
        };

        // Mac: :68 —— deepseek.com / bigmodel.cn / zhipuai.cn 才注入 thinking。
        OpenAIChatResponseSupport.ApplyThinkingPreference(body, endpoint.Host);

        async Task<ProviderHttpResult> SendAsync() => await ProviderHttp.PostJsonAsync(
                _httpClient,
                endpoint,
                VisionTimeout,
                body,
                request => request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", trimmedKey),
                cancellationToken)
            .ConfigureAwait(false);

        var result = await SendAsync();

        if (result.StatusCode is < 200 or >= 300)
        {
            // 常思考模型（如 glm-5.3-flash）拒绝 "thinking": {"type":"disabled"} ——
            // 400「不支持关闭思考；请使用 low、high 或 max」时自动降级 low 重试一次。
            var errorText = ProviderHttp.ErrorMessage(
                result.Body, includeStatusField: false, includeDetailFallback: false) ?? string.Empty;
            if (result.StatusCode == 400
                && errorText.Contains("始终思考", StringComparison.Ordinal)
                && body["thinking"]?["type"]?.GetValue<string>() == "disabled")
            {
                // 智谱 API 实测：disabled/low 都被 400，省略字段才 200 —— 删除字段重试。
                body.Remove("thinking");
                result = await SendAsync();

                if (result.StatusCode is < 200 or >= 300)
                {
                    var retryText = ProviderHttp.ErrorMessage(
                        result.Body, includeStatusField: false, includeDetailFallback: false);
                    throw new VisionClientException(new VisionClientError.Server(
                        result.StatusCode,
                        retryText ?? ProviderHttp.StatusPhrase(result.StatusCode)));
                }
            }
            else
            {
                throw new VisionClientException(new VisionClientError.Server(
                    result.StatusCode,
                    errorText.Length > 0 ? errorText : ProviderHttp.StatusPhrase(result.StatusCode)));
            }
        }

        var root = TaJson.TryParseObject(result.Body);
        if (root is not null)
        {
            var text = OpenAIChatResponseSupport.ContentText(root);
            if (!string.IsNullOrEmpty(text))
            {
                return text;
            }

            // Mac: :86-91 —— 正文为空但有思考内容时，报「只返回了思考过程」而不是「空响应」。
            var reasoning = OpenAIChatResponseSupport.ReasoningText(root);
            if (reasoning is not null)
            {
                throw new VisionClientException(new VisionClientError.ReasoningOnlyOutput(
                    OpenAIChatResponseSupport.IsLengthTruncated(root)));
            }
        }

        throw new VisionClientException(new VisionClientError.EmptyResponse());
    }
}
