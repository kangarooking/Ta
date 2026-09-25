namespace Ta.AI;

/// <summary>
/// 端点拼接：三种客户端（识图 / 翻译 / DeepSeek-OCR-2）共用的 Base URL → 请求 URL 逻辑。
///
/// 逐条对应 Mac：
///   · <c>OpenAICompatibleVisionClient.swift:97-117</c>（<see cref="ChatCompletions"/>）
///   · <c>MultimodalProviderClient.swift:169-197</c>（<see cref="PerProvider"/>）
///   · <c>TranslationProviderClient.swift:269-309</c>（<see cref="ChatCompletions"/> / <see cref="PerProvider"/>）
///   · <c>DeepSeekOCR2Client.swift:109-132</c>（<see cref="ChatCompletions"/> + 官方域名拦截）
///
/// 规则（「已带后缀就不重复追加」）：
///   · chat/completions：path 去首尾 <c>/</c> 后以 <c>chat/completions</c> 结尾 → 原样
///   · Azure OpenAI：不以 <c>chat/completions</c> 结尾 → 追加 <c>openai/v1/chat/completions</c>
///   · Anthropic：不以 <c>v1/messages</c> 结尾 → 追加 <c>v1/messages</c>
///   · Gemini：不含 <c>:generateContent</c> → 追加 <c>v1beta/models/&lt;model&gt;:generateContent</c>
///   · OpenAI 兼容（识图主路径）：原样使用
/// </summary>
internal enum ProviderEndpointRoute
{
    /// <summary>结尾不是 <c>chat/completions</c> 就追加 <c>chat/completions</c>
    /// （OpenAI 兼容识图、DeepSeek-OCR-2）。</summary>
    ChatCompletions,

    /// <summary>
    /// 按 <see cref="VisionProviderKind"/> 分派，但 OpenAI 兼容**原样使用 Base URL**
    /// （识图主路径，Mac: MultimodalProviderClient.swift:179-194 —— 该 kind 已被提前委托出去）。
    /// </summary>
    PerProvider,

    /// <summary>
    /// 按 <see cref="VisionProviderKind"/> 分派，OpenAI 兼容也追加 <c>chat/completions</c>
    /// （翻译客户端，Mac: TranslationProviderClient.swift:287-304）。
    /// </summary>
    PerProviderWithOpenAIChatCompletions,
}

internal static class ProviderEndpointSupport
{
    /// <summary>
    /// 拼接端点。失败时返回 null，并把要包进各客户端 <c>invalidEndpoint</c> 的文案写入
    /// <paramref name="errorMessage"/>。
    ///
    /// <paramref name="invalidFormatMessage"/> 是「解析不出 URL」时的兜底文案，各客户端不同：
    /// <c>"Base URL 格式无效。"</c> / <c>"翻译 API 地址格式无效。"</c> / <c>"DeepSeek OCR 服务地址格式无效。"</c>。
    /// </summary>
    internal static Uri? TryBuild(
        ProviderEndpointRoute route,
        VisionProviderKind provider,
        string baseURL,
        string model,
        string invalidFormatMessage,
        out string? errorMessage)
    {
        return TryBuild(
            route,
            provider,
            baseURL,
            model,
            invalidFormatMessage,
            trimFullWidthComma: false,
            errorMessage: out errorMessage);
    }

    /// <inheritdoc cref="TryBuild(ProviderEndpointRoute, VisionProviderKind, string, string, string, out string?)"/>
    /// <paramref name="trimFullWidthComma"/>：翻译客户端会额外裁掉首尾的中英文逗号
    /// （<c>TranslationProviderClient.swift:274-276</c>）。</param>
    internal static Uri? TryBuild(
        ProviderEndpointRoute route,
        VisionProviderKind provider,
        string baseURL,
        string model,
        string invalidFormatMessage,
        bool trimFullWidthComma,
        out string? errorMessage)
    {
        errorMessage = null;

        var trimmed = TaText.Trim(baseURL);
        if (trimFullWidthComma)
        {
            trimmed = trimmed.Trim('，', ',');
        }

        if (trimmed.Length == 0)
        {
            // 空 Base URL 的专属文案由各客户端自己决定，这里给一个通用兜底。
            errorMessage = invalidFormatMessage;
            return null;
        }

        var validation = new ProviderEndpointValidator().ValidationMessage(trimmed);
        if (validation is not null)
        {
            errorMessage = validation;
            return null;
        }

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var parsed))
        {
            errorMessage = invalidFormatMessage;
            return null;
        }

        // Mac: components.path.trimmingCharacters(in: CharacterSet(charactersIn: "/"))
        var path = parsed.AbsolutePath.Trim('/');
        var suffix = string.Empty;

        if (route == ProviderEndpointRoute.ChatCompletions)
        {
            if (!path.EndsWith("chat/completions", StringComparison.Ordinal))
            {
                suffix = "chat/completions";
            }
        }
        else
        {
            switch (provider)
            {
                case VisionProviderKind.AzureOpenAI:
                    if (!path.EndsWith("chat/completions", StringComparison.Ordinal))
                    {
                        suffix = "openai/v1/chat/completions";
                    }

                    break;
                case VisionProviderKind.Anthropic:
                    if (!path.EndsWith("v1/messages", StringComparison.Ordinal))
                    {
                        suffix = "v1/messages";
                    }

                    break;
                case VisionProviderKind.GoogleGemini:
                    if (!path.Contains(":generateContent", StringComparison.Ordinal))
                    {
                        suffix = $"v1beta/models/{model}:generateContent";
                    }

                    break;
                case VisionProviderKind.OpenAICompatible:
                    // 识图主路径原样使用；翻译路径追加 chat/completions。
                    if (route == ProviderEndpointRoute.PerProviderWithOpenAIChatCompletions &&
                        !path.EndsWith("chat/completions", StringComparison.Ordinal))
                    {
                        suffix = "chat/completions";
                    }

                    break;
            }
        }

        if (suffix.Length > 0)
        {
            // Mac: "/" + ([path, suffix].filter { !$0.isEmpty }.joined(separator: "/"))
            var joined = string.Join("/", new[] { path, suffix }.Where(part => part.Length > 0));
            path = joined;
        }

        // ⚠️ 与 Mac 的唯一可观察差异：Mac 在「已带后缀、无需追加」时会保留原始 path
        // （含结尾斜杠），这里统一用裁剪后的 path 重建，因此结尾斜杠会被规范化掉。
        // 服务端对 /v1/chat/completions 与 /v1/chat/completions/ 一视同仁，不影响行为。
        var builder = new UriBuilder(parsed)
        {
            // 保留 query / fragment（Mac 的 URLComponents 同样保留）。
            Path = "/" + path,
        };

        return builder.Uri;
    }
}
