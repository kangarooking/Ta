using System.Net.Http.Headers;
using System.Text.Json.Nodes;

namespace Ta.AI;

/// <summary>
/// 多模态识图客户端（四套 Provider 协议的统一入口）。
///
/// 逐条对应 Mac 版 <c>Recognition/MultimodalProviderClient.swift:66-235</c>。
/// <see cref="VisionProviderKind.OpenAICompatible"/> 直接委托给
/// <see cref="OpenAICompatibleVisionClient"/>（Mac: :82-91）。
///
/// ⚠️ <c>max_tokens</c> 与 OpenAI 兼容路径不同：Azure / Anthropic / Gemini 一律 **2048**
/// （Mac: :127、:139、:206、:208）。
/// ⚠️ 图片与文字的相对顺序按协议不同：
///   · Azure：文字在前、图片在后
///   · Anthropic：图片在前、文字在后
///   · Gemini：图片在前、文字在后
/// </summary>
public sealed class MultimodalProviderClient
{
    /// <summary>Mac: MultimodalProviderClient.swift:127,139（Azure / Anthropic / Gemini 识图 max_tokens）。</summary>
    public const int VisionMaxTokens = 2048;

    /// <summary>Mac: MultimodalProviderClient.swift:101（识图超时 60s）。</summary>
    public static readonly TimeSpan VisionTimeout = TimeSpan.FromSeconds(60);

    private readonly HttpClient _httpClient;

    /// <param name="httpClient">不传则新建；测试用 mock <see cref="HttpMessageHandler"/> 注入。</param>
    public MultimodalProviderClient(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? new HttpClient();
    }

    /// <summary>
    /// 发送一次识图请求，返回模型正文（已裁剪首尾空白）。
    ///
    /// Mac: <c>MultimodalProviderClient.recognize(provider:baseURL:model:apiKey:imageData:mimeType:prompt:)</c>。
    /// </summary>
    public async Task<string> RecognizeAsync(
        VisionProviderKind provider,
        string baseURL,
        string model,
        string apiKey,
        byte[] imageData,
        string prompt,
        string mimeType = "image/jpeg",
        CancellationToken cancellationToken = default)
    {
        if (provider == VisionProviderKind.OpenAICompatible)
        {
            return await new OpenAICompatibleVisionClient(_httpClient)
                .RecognizeAsync(baseURL, model, apiKey, imageData, prompt, mimeType, cancellationToken)
                .ConfigureAwait(false);
        }

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
            ProviderEndpointRoute.PerProvider,
            provider,
            baseURL,
            trimmedModel,
            "Base URL 格式无效。",
            out var endpointError);
        if (endpoint is null)
        {
            throw new VisionClientException(new VisionClientError.InvalidEndpoint(
                TaText.Trim(baseURL).Length == 0 ? "请先配置 Base URL。" : endpointError!));
        }

        var base64 = Convert.ToBase64String(imageData);

        // 三个分支各自组装请求体（Mac: :107-140）。
        var body = new JsonObject();
        Action<HttpRequestMessage>? configureHeaders = null;

        switch (provider)
        {
            case VisionProviderKind.AzureOpenAI:
                configureHeaders = request => request.Headers.TryAddWithoutValidation("api-key", trimmedKey);
                body["model"] = trimmedModel;
                body["messages"] = OpenAIMessagesWithImage(prompt, $"data:{mimeType};base64,{base64}", imageFirst: false);
                body["temperature"] = 0;
                body["max_tokens"] = VisionMaxTokens;
                OpenAIChatResponseSupport.ApplyThinkingPreference(body, endpoint.Host);
                break;

            case VisionProviderKind.Anthropic:
                configureHeaders = request =>
                {
                    request.Headers.TryAddWithoutValidation("x-api-key", trimmedKey);
                    request.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
                };
                // Mac: :119-129 —— 无 temperature。
                body["model"] = trimmedModel;
                body["max_tokens"] = VisionMaxTokens;
                body["messages"] = new JsonArray(new JsonObject
                {
                    ["role"] = "user",
                    ["content"] = new JsonArray(
                        new JsonObject
                        {
                            ["type"] = "image",
                            ["source"] = new JsonObject
                            {
                                ["type"] = "base64",
                                ["media_type"] = mimeType,
                                ["data"] = base64,
                            },
                        },
                        new JsonObject
                        {
                            ["type"] = "text",
                            ["text"] = prompt,
                        }),
                });
                break;

            case VisionProviderKind.GoogleGemini:
                configureHeaders = request => request.Headers.TryAddWithoutValidation("x-goog-api-key", trimmedKey);
                body["contents"] = new JsonArray(new JsonObject
                {
                    ["parts"] = new JsonArray(
                        new JsonObject
                        {
                            ["inlineData"] = new JsonObject
                            {
                                ["mimeType"] = mimeType,
                                ["data"] = base64,
                            },
                        },
                        new JsonObject
                        {
                            ["text"] = prompt,
                        }),
                });
                body["generationConfig"] = new JsonObject
                {
                    ["temperature"] = 0,
                    ["maxOutputTokens"] = VisionMaxTokens,
                };
                break;

            case VisionProviderKind.OpenAICompatible:
                throw new InvalidOperationException("Handled above.");
        }

        var result = await ProviderHttp.PostJsonAsync(
                _httpClient,
                endpoint,
                VisionTimeout,
                body,
                configureHeaders,
                cancellationToken)
            .ConfigureAwait(false);

        if (result.StatusCode is < 200 or >= 300)
        {
            throw new VisionClientException(new VisionClientError.Server(
                result.StatusCode,
                ProviderHttp.ErrorMessage(result.Body, includeStatusField: true, includeDetailFallback: false)
                ?? ProviderHttp.StatusPhrase(result.StatusCode)));
        }

        // Mac: :154-161 —— 先按协议取正文，取不到再判断是否「只有思考过程」。
        var root = TaJson.TryParseObject(result.Body);
        if (root is not null)
        {
            var text = ResponseText(provider, root);
            if (!string.IsNullOrEmpty(text))
            {
                return text;
            }

            var reasoning = OpenAIChatResponseSupport.ReasoningText(root);
            if (reasoning is not null)
            {
                throw new VisionClientException(new VisionClientError.ReasoningOnlyOutput(
                    OpenAIChatResponseSupport.IsLengthTruncated(root)));
            }
        }

        throw new VisionClientException(new VisionClientError.EmptyResponse());
    }

    /// <summary>
    /// OpenAI 兼容的 messages 数组：文字 + 图片（Azure 用 data URL）。
    /// Mac: MultimodalProviderClient.swift:199-209。
    /// </summary>
    private static JsonArray OpenAIMessagesWithImage(string prompt, string dataUrl, bool imageFirst)
    {
        var textPart = new JsonObject
        {
            ["type"] = "text",
            ["text"] = prompt,
        };
        var imagePart = new JsonObject
        {
            ["type"] = "image_url",
            ["image_url"] = new JsonObject
            {
                ["url"] = dataUrl,
            },
        };

        return new JsonArray(new JsonObject
        {
            ["role"] = "user",
            ["content"] = imageFirst ? new JsonArray(imagePart, textPart) : new JsonArray(textPart, imagePart),
        });
    }

    /// <summary>Mac: MultimodalProviderClient.swift:211-226（responseText(provider:object:)）。</summary>
    private static string? ResponseText(VisionProviderKind provider, JsonObject root)
    {
        switch (provider)
        {
            case VisionProviderKind.AzureOpenAI:
                return OpenAIChatResponseSupport.ContentText(root);
            case VisionProviderKind.Anthropic:
                return TaText.TrimOrNull(CollectTextParts(root["content"]));
            case VisionProviderKind.GoogleGemini:
                var candidates = root["candidates"] as JsonArray;
                if (candidates is null || candidates.Count == 0)
                {
                    return null;
                }

                var content = candidates[0]?["content"] as JsonObject;
                if (content is null)
                {
                    return null;
                }

                return TaText.TrimOrNull(CollectTextParts(content["parts"]));
            default:
                return null;
        }
    }

    /// <summary>把 parts 数组里的 <c>text</c> 用换行拼起来（Mac: :216-222、:326-328）。</summary>
    private static string CollectTextParts(JsonNode? node)
    {
        if (node is not JsonArray parts)
        {
            return string.Empty;
        }

        var collected = new List<string>();
        foreach (var part in parts)
        {
            if (part is JsonObject partObject &&
                partObject["text"] is JsonValue value &&
                value.TryGetValue<string>(out var text))
            {
                collected.Add(text);
            }
        }

        return TaText.JoinLines(collected);
    }
}
