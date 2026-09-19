using System.Net.Http.Headers;
using System.Text.Json.Nodes;

namespace Ta.AI;

/// <summary>一次非流式 POST 的结果。</summary>
internal readonly record struct ProviderHttpResult(int StatusCode, byte[] Body);

/// <summary>
/// Provider 客户端的公共 HTTP 发送逻辑。
///
/// Mac 全程 <c>URLSession.data(for:)</c>：**一次性、无重试、无流式**。这里保持同样语义，
/// 每个请求独立 CancellationTokenSource 控制超时（识图 60s，翻译 / OCR-2 120s）。
/// </summary>
internal static class ProviderHttp
{
    /// <summary>Content-Type 固定为 <c>application/json</c>（不带 charset，与 Mac 的 setValue 一致）。</summary>
    internal const string JsonContentType = "application/json";

    /// <summary>
    /// POST JSON 请求体。<paramref name="configure"/> 用于按协议设置鉴权头。
    /// </summary>
    internal static async Task<ProviderHttpResult> PostJsonAsync(
        HttpClient httpClient,
        Uri endpoint,
        TimeSpan timeout,
        JsonObject body,
        Action<HttpRequestMessage>? configure,
        CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);

        var payload = TaJson.Serialize(body);
        var content = new ByteArrayContent(payload);
        content.Headers.ContentType = new MediaTypeHeaderValue(JsonContentType);
        request.Content = content;

        configure?.Invoke(request);

        using var timeoutScope = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutScope.CancelAfter(timeout);

        using var response = await httpClient
            .SendAsync(request, HttpCompletionOption.ResponseContentRead, timeoutScope.Token)
            .ConfigureAwait(false);

        var bytes = await response.Content.ReadAsByteArrayAsync(timeoutScope.Token).ConfigureAwait(false);
        return new ProviderHttpResult((int)response.StatusCode, bytes);
    }

    /// <summary>
    /// 从错误响应体里抽取服务端消息。对应 Mac 各客户端的 <c>errorMessage(from:)</c>。
    /// </summary>
    /// <param name="includeStatusField">识图主客户端额外认 <c>error.status</c>
    /// （<c>MultimodalProviderClient.swift:230-232</c>）。</param>
    /// <param name="includeDetailFallback">翻译与 DeepSeek 客户端会退化读 <c>detail</c>
    /// （错误对象内与顶层，<c>TranslationProviderClient.swift:362-368</c>、<c>DeepSeekOCR2Client.swift:148-154</c>）。</param>
    internal static string? ErrorMessage(byte[] body, bool includeStatusField, bool includeDetailFallback)
    {
        var root = TaJson.TryParseObject(body);
        if (root is null)
        {
            return null;
        }

        if (root["error"] is JsonObject error)
        {
            if (error["message"] is JsonValue message && message.TryGetValue<string>(out var messageText))
            {
                return messageText;
            }

            if (includeStatusField &&
                error["status"] is JsonValue status &&
                status.TryGetValue<string>(out var statusText))
            {
                return statusText;
            }

            if (includeDetailFallback &&
                error["detail"] is JsonValue errorDetail &&
                errorDetail.TryGetValue<string>(out var errorDetailText))
            {
                return errorDetailText;
            }
        }

        if (root["message"] is JsonValue topMessage && topMessage.TryGetValue<string>(out var topMessageText))
        {
            return topMessageText;
        }

        if (includeDetailFallback &&
            root["detail"] is JsonValue detail &&
            detail.TryGetValue<string>(out var detailText))
        {
            return detailText;
        }

        return null;
    }

    /// <summary>
    /// 兜底状态文案。对应 Mac 的 <c>HTTPURLResponse.localizedString(forStatusCode:)</c>
    /// （Foundation 返回的是小写英文短语，如 "internal server error"）。
    /// </summary>
    internal static string StatusPhrase(int statusCode) => statusCode switch
    {
        400 => "bad request",
        401 => "unauthorized",
        403 => "forbidden",
        404 => "not found",
        405 => "method not allowed",
        408 => "request timeout",
        409 => "conflict",
        413 => "payload too large",
        415 => "unsupported media type",
        422 => "unprocessable entity",
        429 => "too many requests",
        500 => "internal server error",
        501 => "not implemented",
        502 => "bad gateway",
        503 => "service unavailable",
        504 => "gateway timeout",
        _ => $"HTTP Error {statusCode}",
    };
}
