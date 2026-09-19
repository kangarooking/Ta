using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Ta.AI;

/// <summary>
/// 翻译 Provider 客户端（四套协议，全部一次性非流式）。
///
/// 逐条对应 Mac 版 <c>Recognition/TranslationProviderClient.swift:32-369</c>。
///
/// 关键行为：
///   · 默认 <c>max_tokens: 4096</c>；连接测试 <c>512</c>（Mac: :158、:145）
///   · 超时 120s（Mac: :168）
///   · OpenAI / Azure 的纯文字请求 <c>content</c> 是**字符串**（GLM 文字模型拒绝多模态数组）；
///     带图时才用 parts 数组，且图片在文字**之后**（Mac: :178-189）
///   · Anthropic 反过来：图片在文字**之前**，且额外带 <c>temperature: 0</c>（Mac: :200-220）
///   · <c>jsonMode</c> 时追加 <c>"response_format":{"type":"json_object"}</c>（Mac: :198）
///   · 显式发送 <c>"stream": false</c>（Mac: :194）
/// </summary>
public sealed class TranslationProviderClient
{
    /// <summary>Mac: TranslationProviderClient.swift:158（翻译默认 max_tokens）。</summary>
    public const int DefaultMaxTokens = 4096;

    /// <summary>Mac: TranslationProviderClient.swift:145（连接测试 max_tokens）。</summary>
    public const int ConnectionTestMaxTokens = 512;

    /// <summary>Mac: TranslationProviderClient.swift:168（翻译超时 120s）。</summary>
    public static readonly TimeSpan TranslationTimeout = TimeSpan.FromSeconds(120);

    private readonly HttpClient _httpClient;

    /// <param name="httpClient">不传则新建；测试用 mock <see cref="HttpMessageHandler"/> 注入。</param>
    public TranslationProviderClient(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? new HttpClient();
    }

    /// <summary>Mac: <c>translateText(...)</c>（:39-67）。<paramref name="jsonMode"/> 恒为 false。</summary>
    public Task<string> TranslateTextAsync(
        string baseURL,
        string model,
        string apiKey,
        string text,
        string sourceLanguage,
        string targetLanguage,
        VisionProviderKind provider = VisionProviderKind.OpenAICompatible,
        CancellationToken cancellationToken = default)
    {
        var trimmed = TaText.Trim(text);
        if (trimmed.Length == 0)
        {
            throw new TranslationProviderException(new TranslationProviderError.EmptyInput());
        }

        // ⚠️ 逐字对应 Mac: :50-58。sourceLanguage / targetLanguage 是中文自由文本，直接插值。
        var prompt = TaText.JoinLines(new[]
        {
            $"Translate the following content from {sourceLanguage} to {targetLanguage}.",
            "Preserve paragraphs, lists, code, numbers, names, and Markdown structure.",
            "Do not summarize, explain, or add labels. Output only the translation.",
            string.Empty,
            "<content>",
            trimmed,
            "</content>",
        });

        return CompleteAsync(
            provider: provider,
            baseURL: baseURL,
            model: model,
            apiKey: apiKey,
            prompt: prompt,
            jsonMode: false,
            cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Mac: <c>translateSegments(...)</c>（:69-103）。
    /// 一次请求带全部文字行；返回前校验 id 完整性（缺 id / 多 id / 数量不符都算失败）。
    /// </summary>
    public async Task<IReadOnlyList<TranslationSegmentResult>> TranslateSegmentsAsync(
        string baseURL,
        string model,
        string apiKey,
        IReadOnlyList<TranslationSourceSegment> segments,
        string sourceLanguage,
        string targetLanguage,
        VisionProviderKind provider = VisionProviderKind.OpenAICompatible,
        CancellationToken cancellationToken = default)
    {
        if (segments.Count == 0)
        {
            throw new TranslationProviderException(new TranslationProviderError.EmptyInput());
        }

        // Mac: :79-80 —— JSONEncoder 的紧凑输出。
        var sourceJson = JsonSerializer.Serialize(
            segments,
            TaJson.WriteOptions);

        // ⚠️ 逐字对应 Mac: :81-88。
        var prompt = TaText.JoinLines(new[]
        {
            $"Translate every JSON item's text from {sourceLanguage} to {targetLanguage}.",
            "Return valid JSON only in this exact shape: {\"translations\":[{\"id\":0,\"text\":\"...\"}]}.",
            "Preserve every id exactly once and keep short UI labels concise. Do not add explanations.",
            string.Empty,
            "Input JSON:",
            sourceJson,
        });

        var response = await CompleteAsync(
            provider: provider,
            baseURL: baseURL,
            model: model,
            apiKey: apiKey,
            prompt: prompt,
            jsonMode: true,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        var decoded = DecodeSegments(response);
        var expectedIds = new HashSet<int>(segments.Select(segment => segment.Id));
        var decodedIds = new HashSet<int>(decoded.Select(segment => segment.Id));

        // Mac: :99-101 —— 集合相等**且**数量相等，否则 invalidSegmentResponse。
        if (!decodedIds.SetEquals(expectedIds) || decoded.Count != expectedIds.Count)
        {
            throw new TranslationProviderException(new TranslationProviderError.InvalidSegmentResponse());
        }

        // Mac: :102 —— 按 id 升序返回。
        return decoded.OrderBy(segment => segment.Id).ToArray();
    }

    /// <summary>Mac: <c>translateImage(...)</c>（:105-130），默认 mimeType 为 <c>image/png</c>。</summary>
    public Task<string> TranslateImageAsync(
        string baseURL,
        string model,
        string apiKey,
        byte[] imageData,
        string sourceLanguage,
        string targetLanguage,
        VisionProviderKind provider = VisionProviderKind.OpenAICompatible,
        string mimeType = "image/png",
        CancellationToken cancellationToken = default)
    {
        // ⚠️ 逐字对应 Mac: :115-119。
        var prompt = TaText.JoinLines(new[]
        {
            $"Read all visible text in this screenshot and translate it from {sourceLanguage} to {targetLanguage}.",
            "Preserve reading order, paragraphs, lists, code, numbers, and names.",
            "Do not describe the image or explain. Output only the translated text.",
        });

        return CompleteAsync(
            provider: provider,
            baseURL: baseURL,
            model: model,
            apiKey: apiKey,
            prompt: prompt,
            imageData: imageData,
            mimeType: mimeType,
            jsonMode: false,
            cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Mac: <c>testConnection(...)</c>（:132-147）。
    /// Prompt 是 <c>Reply with exactly: OK</c>，<c>maxTokens: 512</c>，不带图。
    /// </summary>
    public Task<string> TestConnectionAsync(
        string baseURL,
        string model,
        string apiKey,
        VisionProviderKind provider = VisionProviderKind.OpenAICompatible,
        CancellationToken cancellationToken = default)
    {
        return CompleteAsync(
            provider: provider,
            baseURL: baseURL,
            model: model,
            apiKey: apiKey,
            prompt: "Reply with exactly: OK",
            jsonMode: false,
            maxTokens: ConnectionTestMaxTokens,
            cancellationToken: cancellationToken);
    }

    /// <summary>
    /// 四套协议的统一发送入口。Mac: TranslationProviderClient.swift:149-256。
    /// </summary>
    private async Task<string> CompleteAsync(
        VisionProviderKind provider,
        string baseURL,
        string model,
        string apiKey,
        string prompt,
        bool jsonMode,
        byte[]? imageData = null,
        string mimeType = "image/png",
        int maxTokens = DefaultMaxTokens,
        CancellationToken cancellationToken = default)
    {
        var trimmedModel = TaText.Trim(model);
        if (trimmedModel.Length == 0)
        {
            throw new TranslationProviderException(new TranslationProviderError.MissingModel());
        }

        var trimmedKey = TaText.Trim(apiKey);
        if (trimmedKey.Length == 0)
        {
            throw new TranslationProviderException(new TranslationProviderError.MissingApiKey());
        }

        var endpoint = ProviderEndpointSupport.TryBuild(
            ProviderEndpointRoute.PerProviderWithOpenAIChatCompletions,
            provider,
            baseURL,
            trimmedModel,
            "翻译 API 地址格式无效。",
            trimFullWidthComma: true,
            out var endpointError);
        if (endpoint is null)
        {
            // Mac: :277-279 —— 空地址专属文案。
            throw new TranslationProviderException(new TranslationProviderError.InvalidEndpoint(
                TaText.Trim(baseURL).Trim('，', ',').Length == 0
                    ? "请先配置翻译 API 地址。"
                    : endpointError!));
        }

        var body = new JsonObject();
        Action<HttpRequestMessage>? configureHeaders = null;

        switch (provider)
        {
            case VisionProviderKind.OpenAICompatible:
            case VisionProviderKind.AzureOpenAI:
                configureHeaders = provider == VisionProviderKind.AzureOpenAI
                    ? request => request.Headers.TryAddWithoutValidation("api-key", trimmedKey)
                    : request => request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", trimmedKey);

                JsonNode messageContent;
                if (imageData is not null)
                {
                    var dataUrl = $"data:{mimeType};base64,{Convert.ToBase64String(imageData)}";
                    // Mac: :180-183 —— 文字在前，图片追加在后。
                    messageContent = new JsonArray(
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
                        });
                }
                else
                {
                    // Mac: :184-189 —— 纯文字模型要求字符串形态的 content。
                    messageContent = prompt;
                }

                body["model"] = trimmedModel;
                body["messages"] = new JsonArray(new JsonObject
                {
                    ["role"] = "user",
                    ["content"] = messageContent,
                });
                body["temperature"] = 0;
                body["stream"] = false;
                body["max_tokens"] = maxTokens;
                OpenAIChatResponseSupport.ApplyThinkingPreference(body, endpoint.Host);
                if (jsonMode)
                {
                    body["response_format"] = new JsonObject
                    {
                        ["type"] = "json_object",
                    };
                }

                break;

            case VisionProviderKind.Anthropic:
                configureHeaders = request =>
                {
                    request.Headers.TryAddWithoutValidation("x-api-key", trimmedKey);
                    request.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
                };
                var content = new JsonArray();
                if (imageData is not null)
                {
                    // Mac: :204-213 —— 图片在文字之前。
                    content.Add(new JsonObject
                    {
                        ["type"] = "image",
                        ["source"] = new JsonObject
                        {
                            ["type"] = "base64",
                            ["media_type"] = mimeType,
                            ["data"] = Convert.ToBase64String(imageData),
                        },
                    });
                }

                content.Add(new JsonObject
                {
                    ["type"] = "text",
                    ["text"] = prompt,
                });
                body["model"] = trimmedModel;
                body["max_tokens"] = maxTokens;
                // Mac: :218 —— 翻译路径额外带 temperature: 0（识图路径不带）。
                body["temperature"] = 0;
                body["messages"] = new JsonArray(new JsonObject
                {
                    ["role"] = "user",
                    ["content"] = content,
                });
                break;

            case VisionProviderKind.GoogleGemini:
                configureHeaders = request => request.Headers.TryAddWithoutValidation("x-goog-api-key", trimmedKey);
                var parts = new JsonArray();
                if (imageData is not null)
                {
                    parts.Add(new JsonObject
                    {
                        ["inlineData"] = new JsonObject
                        {
                            ["mimeType"] = mimeType,
                            ["data"] = Convert.ToBase64String(imageData),
                        },
                    });
                }

                parts.Add(new JsonObject
                {
                    ["text"] = prompt,
                });
                body["contents"] = new JsonArray(new JsonObject
                {
                    ["parts"] = parts,
                });
                body["generationConfig"] = new JsonObject
                {
                    ["temperature"] = 0,
                    ["maxOutputTokens"] = maxTokens,
                };
                break;
        }

        var result = await ProviderHttp.PostJsonAsync(
                _httpClient,
                endpoint,
                TranslationTimeout,
                body,
                configureHeaders,
                cancellationToken)
            .ConfigureAwait(false);

        if (result.StatusCode is < 200 or >= 300)
        {
            // 常思考模型（如 glm-5.3-flash）拒绝 "thinking": {"type":"disabled"}，
            // 400 明说「不支持关闭思考；请使用 low、high 或 max」—— 自动降级 low 重试一次。
            // 普通模型不受影响（disabled 一次成功，不会走到这里）。
            var errorText = ProviderHttp.ErrorMessage(
                result.Body, includeStatusField: false, includeDetailFallback: true) ?? string.Empty;
            if (result.StatusCode == 400
                && errorText.Contains("始终思考", StringComparison.Ordinal)
                && body["thinking"]?["type"]?.GetValue<string>() == "disabled")
            {
                // 智谱 API 实测（2026-09-19，key 实测三种姿势）：disabled → 400、
                // low → 400（报错文案「请使用 low、high 或 max」有误导性，low 同样被拒）、
                // 省略字段 → 200 成功。结论：常思考模型不能传任何 thinking 控制。
                body.Remove("thinking");
                result = await ProviderHttp.PostJsonAsync(
                        _httpClient,
                        endpoint,
                        TranslationTimeout,
                        body,
                        configureHeaders,
                        cancellationToken)
                    .ConfigureAwait(false);

                if (result.StatusCode is < 200 or >= 300)
                {
                    var retryText = ProviderHttp.ErrorMessage(
                        result.Body, includeStatusField: false, includeDetailFallback: true);
                    throw new TranslationProviderException(new TranslationProviderError.Server(
                        result.StatusCode,
                        retryText ?? ProviderHttp.StatusPhrase(result.StatusCode)));
                }
            }
            else
            {
                throw new TranslationProviderException(new TranslationProviderError.Server(
                    result.StatusCode,
                    errorText.Length > 0 ? errorText : ProviderHttp.StatusPhrase(result.StatusCode)));
            }
        }

        // Mac: :248-255
        var root = TaJson.TryParseObject(result.Body);
        if (root is not null)
        {
            var text = ResponseText(provider, root);
            if (!string.IsNullOrEmpty(text))
            {
                return text;
            }

            var detailedError = ReasoningOutcome(provider, root);
            if (detailedError is not null)
            {
                throw new TranslationProviderException(detailedError);
            }
        }

        throw new TranslationProviderException(new TranslationProviderError.EmptyResponse());
    }

    /// <summary>
    /// Mac: :258-267 —— 只有 OpenAI / Azure 路径才可能「只有思考内容」。
    /// </summary>
    private static TranslationProviderError? ReasoningOutcome(VisionProviderKind provider, JsonObject root)
    {
        if (provider != VisionProviderKind.OpenAICompatible && provider != VisionProviderKind.AzureOpenAI)
        {
            return null;
        }

        if (OpenAIChatResponseSupport.ReasoningText(root) is null)
        {
            return null;
        }

        return new TranslationProviderError.ReasoningOnlyOutput(OpenAIChatResponseSupport.IsLengthTruncated(root));
    }

    /// <summary>Mac: :311-330（按协议取正文）。</summary>
    private static string? ResponseText(VisionProviderKind provider, JsonObject root)
    {
        switch (provider)
        {
            case VisionProviderKind.OpenAICompatible:
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
                return content is null ? null : TaText.TrimOrNull(CollectTextParts(content["parts"]));
            default:
                return null;
        }
    }

    private static string CollectTextParts(JsonNode? node)
    {
        if (node is not JsonArray parts)
        {
            return string.Empty;
        }

        var collected = new List<string>();
        foreach (var part in parts)
        {
            if (part is JsonObject partObject)
            {
                if (partObject["text"] is JsonValue text && text.TryGetValue<string>(out var textValue))
                {
                    collected.Add(textValue);
                }
                else if (partObject["content"] is JsonValue content &&
                         content.TryGetValue<string>(out var contentValue))
                {
                    collected.Add(contentValue);
                }
            }
        }

        return TaText.JoinLines(collected);
    }

    /// <summary>
    /// Mac: :344-360 —— 先剥 ```json / ``` 代码围栏，再解 <c>translations</c> 数组。
    /// 任何一步失败都算 <c>invalidSegmentResponse</c>。
    /// </summary>
    private static List<TranslationSegmentResult> DecodeSegments(string response)
    {
        var candidate = TaText.Trim(response);
        if (candidate.StartsWith("```", StringComparison.Ordinal))
        {
            candidate = candidate
                .Replace("```json", string.Empty, StringComparison.Ordinal)
                .Replace("```", string.Empty, StringComparison.Ordinal);
            candidate = TaText.Trim(candidate);
        }

        var root = TaJson.TryParseObject(System.Text.Encoding.UTF8.GetBytes(candidate));
        if (root is null)
        {
            throw new TranslationProviderException(new TranslationProviderError.InvalidSegmentResponse());
        }

        var translations = root["translations"];
        if (translations is null)
        {
            throw new TranslationProviderException(new TranslationProviderError.InvalidSegmentResponse());
        }

        try
        {
            var decoded = JsonSerializer.Deserialize<List<TranslationSegmentResult>>(
                translations.ToJsonString(),
                TaJson.ReadOptions);
            return decoded is null
                ? throw new TranslationProviderException(new TranslationProviderError.InvalidSegmentResponse())
                : decoded;
        }
        catch (JsonException)
        {
            throw new TranslationProviderException(new TranslationProviderError.InvalidSegmentResponse());
        }
    }
}
