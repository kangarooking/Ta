using Ta.AI;

namespace Ta.AI.Tests;

/// <summary>
/// 「关闭深度思考」检测：reasoning-only 响应的识别与两套中文文案。
///
/// Mac 实现：
///   · 检测：<c>OpenAIChatResponseSupport.swift:43-53</c>
///     （<c>reasoning_content</c> 或 <c>reasoning</c> 非空而 content 为空；
///     <c>finish_reason == "length"</c> 时 truncated）
///   · 识图文案：<c>OpenAICompatibleVisionClient.swift:19-25</c>（VisionClientError）
///   · 翻译文案：<c>TranslationProviderClient.swift:23-29</c>（TranslationProviderError）
/// </summary>
public class ReasoningOnlyTests
{
    private static async Task<VisionClientException> VisionAsync(string body, VisionProviderKind kind = VisionProviderKind.OpenAICompatible)
    {
        var handler = new RecordingHandler();
        handler.Enqueue(body);
        var client = new MultimodalProviderClient(new HttpClient(handler));

        return await Assert.ThrowsAsync<VisionClientException>(() => client.RecognizeAsync(
            kind,
            "https://api.example.com/v1",
            "m",
            "k",
            new byte[] { 1 },
            "PROMPT"));
    }

    private static async Task<TranslationProviderException> TranslationAsync(string body)
    {
        var handler = new RecordingHandler();
        handler.Enqueue(body);
        var client = new TranslationProviderClient(new HttpClient(handler));

        return await Assert.ThrowsAsync<TranslationProviderException>(() => client.TestConnectionAsync(
            "https://api.example.com/v1",
            "m",
            "k"));
    }

    [Fact]
    public async Task ReasoningContentOnlyIsDetected()
    {
        var error = await VisionAsync(RecordingHandler.ReasoningOnly("先想一会儿", finishReason: "stop"));

        var outcome = Assert.IsType<VisionClientError.ReasoningOnlyOutput>(error.Error);
        Assert.False(outcome.Truncated);
        Assert.Equal("模型只返回了思考过程，没有识别正文。请关闭深度思考后重试。", outcome.ErrorDescription);
    }

    [Fact]
    public async Task LengthFinishReasonMarksTruncated()
    {
        var error = await VisionAsync(RecordingHandler.ReasoningOnly("先想一会儿", finishReason: "length"));

        var outcome = Assert.IsType<VisionClientError.ReasoningOnlyOutput>(error.Error);
        Assert.True(outcome.Truncated);
        Assert.Equal(
            "识别失败：输出上限被思考过程耗尽，还没生成正文就被截断了。请关闭该模型的深度思考，或调大输出上限后重试。",
            outcome.ErrorDescription);
    }

    [Fact]
    public async Task ReasoningFieldIsAlsoAccepted()
    {
        var body = "{\"choices\":[{\"message\":{\"content\":\"\",\"reasoning\":\"思考\"},\"finish_reason\":\"length\"}]}";

        var error = await VisionAsync(body);

        var outcome = Assert.IsType<VisionClientError.ReasoningOnlyOutput>(error.Error);
        Assert.True(outcome.Truncated);
    }

    [Fact]
    public async Task WhitespaceOnlyReasoningIsNotReasoningOnly()
    {
        // reasoning_content 只有空白 → 视为没有思考内容 → 走 emptyResponse。
        var error = await VisionAsync(RecordingHandler.ReasoningOnly("   \n  ", finishReason: "stop"));

        Assert.IsType<VisionClientError.EmptyResponse>(error.Error);
        Assert.Equal("模型没有返回识别内容。", error.Error.ErrorDescription);
    }

    [Fact]
    public async Task EmptyContentWithoutReasoningIsEmptyResponse()
    {
        var error = await VisionAsync(RecordingHandler.ChatCompletion(string.Empty));

        Assert.IsType<VisionClientError.EmptyResponse>(error.Error);
    }

    [Fact]
    public async Task AzureReasoningOnlyIsDetected()
    {
        var error = await VisionAsync(
            RecordingHandler.ReasoningOnly("思考", finishReason: "length"),
            VisionProviderKind.AzureOpenAI);

        Assert.True(Assert.IsType<VisionClientError.ReasoningOnlyOutput>(error.Error).Truncated);
    }

    [Fact]
    public async Task TranslationReasoningOnlyUsesItsOwnWording()
    {
        var error = await TranslationAsync(RecordingHandler.ReasoningOnly("思考", finishReason: "stop"));

        var outcome = Assert.IsType<TranslationProviderError.ReasoningOnlyOutput>(error.Error);
        Assert.False(outcome.Truncated);
        Assert.Equal("模型只返回了思考过程，没有正文内容。请为该配置关闭深度思考后重试。", outcome.ErrorDescription);
    }

    [Fact]
    public async Task TranslationTruncatedReasoningOnlyUsesItsOwnWording()
    {
        var error = await TranslationAsync(RecordingHandler.ReasoningOnly("思考", finishReason: "length"));

        var outcome = Assert.IsType<TranslationProviderError.ReasoningOnlyOutput>(error.Error);
        Assert.True(outcome.Truncated);
        Assert.Equal(
            "模型把输出上限耗在思考过程里，还没生成正文就被截断了。请关闭该模型的深度思考，或调大输出上限后重试。",
            outcome.ErrorDescription);
    }

    [Fact]
    public async Task TranslationGeminiReasoningOnlyFallsBackToEmptyResponse()
    {
        // Mac: reasoningOutcome 只覆盖 OpenAI / Azure（:262）。
        var handler = new RecordingHandler();
        handler.Enqueue(RecordingHandler.ReasoningOnly("思考", finishReason: "length"));
        var client = new TranslationProviderClient(new HttpClient(handler));

        var error = await Assert.ThrowsAsync<TranslationProviderException>(() => client.TestConnectionAsync(
            "https://generativelanguage.googleapis.com",
            "gemini-2.0-flash",
            "k",
            VisionProviderKind.GoogleGemini));

        Assert.IsType<TranslationProviderError.EmptyResponse>(error.Error);
    }

    [Fact]
    public async Task DeepSeekOCROnlyReportsEmptyResponse()
    {
        var handler = new RecordingHandler();
        handler.Enqueue(RecordingHandler.ReasoningOnly("思考", finishReason: "length"));
        var client = new DeepSeekOCR2Client(new HttpClient(handler));

        var error = await Assert.ThrowsAsync<DeepSeekOCR2ClientException>(() => client.RecognizeAsync(
            "http://127.0.0.1:8000/v1",
            "m",
            "k",
            new byte[] { 1 },
            DeepSeekOCRPromptMode.PlainText));

        Assert.IsType<DeepSeekOCR2ClientError.EmptyResponse>(error.Error);
        Assert.Equal("DeepSeek OCR 没有返回识别内容。", error.Error.ErrorDescription);
    }

    // ------------------------------------------------------------ 服务端错误

    [Fact]
    public async Task ServerErrorParsesMessageField()
    {
        var handler = new RecordingHandler();
        handler.EnqueueStatus(
            System.Net.HttpStatusCode.TooManyRequests,
            "{\"error\":{\"message\":\"速率受限\",\"type\":\"rate_limit\"}}");
        var client = new MultimodalProviderClient(new HttpClient(handler));

        var error = await Assert.ThrowsAsync<VisionClientException>(() => client.RecognizeAsync(
            VisionProviderKind.OpenAICompatible,
            "https://api.example.com/v1",
            "m",
            "k",
            new byte[] { 1 },
            "PROMPT"));

        var server = Assert.IsType<VisionClientError.Server>(error.Error);
        Assert.Equal(429, server.StatusCode);
        Assert.Equal("模型服务错误（429）：速率受限", server.ErrorDescription);
    }

    [Fact]
    public async Task ServerErrorFallsBackToDetailAndStatusPhrase()
    {
        var handler = new RecordingHandler();
        handler.EnqueueStatus(System.Net.HttpStatusCode.InternalServerError, "{\"detail\":\"后端炸了\"}");
        var client = new TranslationProviderClient(new HttpClient(handler));

        var error = await Assert.ThrowsAsync<TranslationProviderException>(() =>
            client.TestConnectionAsync("https://api.example.com/v1", "m", "k"));

        Assert.Equal("翻译服务错误（500）：后端炸了", Assert.IsType<TranslationProviderError.Server>(error.Error).ErrorDescription);

        var empty = new RecordingHandler();
        empty.EnqueueStatus(System.Net.HttpStatusCode.BadGateway, "not json");
        var other = new TranslationProviderClient(new HttpClient(empty));
        var fallback = await Assert.ThrowsAsync<TranslationProviderException>(() =>
            other.TestConnectionAsync("https://api.example.com/v1", "m", "k"));
        Assert.Equal("翻译服务错误（502）：bad gateway", Assert.IsType<TranslationProviderError.Server>(fallback.Error).ErrorDescription);
    }

    [Fact]
    public async Task UnparsableSuccessBodyIsInvalidResponse()
    {
        var handler = new RecordingHandler();
        handler.EnqueueStatus(System.Net.HttpStatusCode.OK, "<html>nope</html>");
        var client = new MultimodalProviderClient(new HttpClient(handler));

        var error = await Assert.ThrowsAsync<VisionClientException>(() => client.RecognizeAsync(
            VisionProviderKind.OpenAICompatible,
            "https://api.example.com/v1",
            "m",
            "k",
            new byte[] { 1 },
            "PROMPT"));

        Assert.IsType<VisionClientError.EmptyResponse>(error.Error);
    }

    // ------------------------------------------------------------ 响应解析

    [Theory]
    [InlineData("{\"choices\":[{\"message\":{\"content\":\"plain string\"}}]}", "plain string")]
    [InlineData("{\"choices\":[{\"message\":{\"content\":[{\"text\":\"a\"},{\"content\":\"b\"}]}}]}", "a\nb")]
    public async Task OpenAIContentShapes(string body, string expected)
    {
        var handler = new RecordingHandler();
        handler.Enqueue(body);
        var client = new MultimodalProviderClient(new HttpClient(handler));

        var text = await client.RecognizeAsync(
            VisionProviderKind.OpenAICompatible,
            "https://api.example.com/v1",
            "m",
            "k",
            new byte[] { 1 },
            "PROMPT");

        Assert.Equal(expected, text);
    }

    [Fact]
    public async Task AnthropicAndGeminiResponseShapes()
    {
        var anthropic = new RecordingHandler();
        anthropic.Enqueue("{\"content\":[{\"text\":\"第一段\"},{\"text\":\"第二段\"}]}");
        var anthropicText = await new MultimodalProviderClient(new HttpClient(anthropic)).RecognizeAsync(
            VisionProviderKind.Anthropic,
            "https://api.anthropic.com",
            "m",
            "k",
            new byte[] { 1 },
            "PROMPT");
        Assert.Equal("第一段\n第二段", anthropicText);

        var gemini = new RecordingHandler();
        gemini.Enqueue("{\"candidates\":[{\"content\":{\"parts\":[{\"text\":\"甲\"},{\"text\":\"乙\"}]}}]}");
        var geminiText = await new MultimodalProviderClient(new HttpClient(gemini)).RecognizeAsync(
            VisionProviderKind.GoogleGemini,
            "https://generativelanguage.googleapis.com",
            "gemini-2.0-flash",
            "k",
            new byte[] { 1 },
            "PROMPT");
        Assert.Equal("甲\n乙", geminiText);
    }

    [Fact]
    public async Task ResponseTextIsTrimmed()
    {
        var handler = new RecordingHandler();
        handler.Enqueue("{\"choices\":[{\"message\":{\"content\":\"  结果  \"}}]}");
        var client = new MultimodalProviderClient(new HttpClient(handler));

        Assert.Equal("结果", await client.RecognizeAsync(
            VisionProviderKind.OpenAICompatible,
            "https://api.example.com/v1",
            "m",
            "k",
            new byte[] { 1 },
            "PROMPT"));
    }
}
