using System.Text.Json;
using Ta.AI;

namespace Ta.AI.Tests;

/// <summary>
/// 请求体形状测试：用 mock <see cref="HttpMessageHandler"/> 拦截真实请求后断言
/// <c>max_tokens</c> / <c>temperature</c> / <c>thinking</c> / 鉴权头 / content 形态。
///
/// Mac 实现位置：
///   · OpenAI 兼容识图请求体：<c>OpenAICompatibleVisionClient.swift:56-69</c>
///   · Azure / Anthropic / Gemini 识图请求体：<c>MultimodalProviderClient.swift:107-144</c>
///   · 翻译请求体：<c>TranslationProviderClient.swift:170-236</c>
///   · DeepSeek-OCR-2 请求体：<c>DeepSeekOCR2Client.swift:65-87</c>
///   · 思考抑制：<c>OpenAIChatResponseSupport.swift:11-21</c>
/// </summary>
public class RequestBodyShapeTests
{
    private static readonly byte[] PngImage = { 1, 2, 3 };

    private static async Task<CapturedRequest> CaptureOpenAIVisionAsync(string baseURL = "https://api.example.com/v1")
    {
        var handler = new RecordingHandler();
        var client = new OpenAICompatibleVisionClient(new HttpClient(handler));
        await client.RecognizeAsync(baseURL, "vision-model", "secret-key", PngImage, "PROMPT", "image/jpeg");
        return handler.Single;
    }

    private static async Task<CapturedRequest> CaptureVisionAsync(
        VisionProviderKind kind,
        string baseURL = "https://api.example.com")
    {
        var handler = new RecordingHandler();
        // 三套解析器都能读的响应体：本组测试只关心请求体形状。
        handler.Enqueue(RecordingHandler.AllShapes("OK"));
        var client = new MultimodalProviderClient(new HttpClient(handler));
        await client.RecognizeAsync(kind, baseURL, "vision-model", "secret-key", PngImage, "PROMPT", "image/png");
        return handler.Single;
    }

    private static async Task<CapturedRequest> CaptureTranslationTextAsync(
        VisionProviderKind kind = VisionProviderKind.OpenAICompatible,
        string baseURL = "https://api.example.com/v1")
    {
        var handler = new RecordingHandler();
        var client = new TranslationProviderClient(new HttpClient(handler));
        await client.TranslateTextAsync(baseURL, "text-model", "secret-key", "hello", "英文", "简体中文", kind);
        return handler.Single;
    }

    private static async Task<CapturedRequest> CaptureOcr2Async(
        string apiKey,
        string baseURL = "http://127.0.0.1:8000/v1")
    {
        var handler = new RecordingHandler();
        var client = new DeepSeekOCR2Client(new HttpClient(handler));
        await client.RecognizeAsync(baseURL, "deepseek-ai/DeepSeek-OCR-2", apiKey, PngImage, DeepSeekOCRPromptMode.PlainText);
        return handler.Single;
    }

    // ------------------------------------------------------------ 公共

    [Fact]
    public async Task AllRequestsAreSinglePostWithJsonContentType()
    {
        var captured = new[]
        {
            await CaptureOpenAIVisionAsync(),
            await CaptureVisionAsync(VisionProviderKind.AzureOpenAI, "https://myres.openai.azure.com"),
            await CaptureVisionAsync(VisionProviderKind.Anthropic, "https://api.anthropic.com"),
            await CaptureVisionAsync(VisionProviderKind.GoogleGemini, "https://generativelanguage.googleapis.com"),
            await CaptureTranslationTextAsync(),
        };

        foreach (var request in captured)
        {
            Assert.Equal("POST", request.Method);
            Assert.Equal("application/json", request.ContentType);
            Assert.False(string.IsNullOrWhiteSpace(request.Body));
        }
    }

    /// <summary>常量锁定，防回归：超时与 max_tokens 全部对齐 Mac。</summary>
    [Fact]
    public void TimeoutsAndMaxTokensMatchMac()
    {
        Assert.Equal(TimeSpan.FromSeconds(60), OpenAICompatibleVisionClient.VisionTimeout);
        Assert.Equal(TimeSpan.FromSeconds(60), MultimodalProviderClient.VisionTimeout);
        Assert.Equal(TimeSpan.FromSeconds(120), TranslationProviderClient.TranslationTimeout);
        Assert.Equal(TimeSpan.FromSeconds(120), DeepSeekOCR2Client.RecognitionTimeout);
        Assert.Equal(4096, OpenAICompatibleVisionClient.VisionMaxTokens);
        Assert.Equal(2048, MultimodalProviderClient.VisionMaxTokens);
        Assert.Equal(4096, TranslationProviderClient.DefaultMaxTokens);
        Assert.Equal(512, TranslationProviderClient.ConnectionTestMaxTokens);
        Assert.Equal(4096, DeepSeekOCR2Client.MaxTokens);
    }

    // ------------------------------------------------------------ OpenAI 兼容识图

    [Fact]
    public async Task OpenAIVisionBodyShape()
    {
        var captured = await CaptureOpenAIVisionAsync();

        Assert.Equal("Bearer secret-key", captured.Header("Authorization"));
        Assert.Equal("vision-model", captured.Text("model"));
        Assert.Equal(0, captured.Int("temperature"));
        Assert.Equal(4096, captured.Int("max_tokens"));      // ⚠️ 4096，不是 2048
        Assert.False(captured.Has("stream"));                // 识图路径不发 stream
        Assert.Equal("user", captured.Text("messages.0.role"));

        // content 是 parts 数组：文字在前、图片在后。
        var content = captured.Json()["messages"]![0]!["content"]!.AsArray();
        Assert.Equal(2, content.Count);
        Assert.Equal("text", captured.Text("messages.0.content.0.type"));
        Assert.Equal("PROMPT", captured.Text("messages.0.content.0.text"));
        Assert.Equal("image_url", captured.Text("messages.0.content.1.type"));
        var dataUrl = captured.Text("messages.0.content.1.image_url.url");
        Assert.StartsWith("data:image/jpeg;base64,", dataUrl);
        Assert.Equal("AQID", dataUrl["data:image/jpeg;base64,".Length..]);

        Assert.False(captured.Has("thinking"));              // 未知网关不注入 thinking
    }

    [Theory]
    [InlineData("https://api.deepseek.com/v1")]
    [InlineData("https://open.bigmodel.cn/api/paas/v4")]
    [InlineData("https://zhipuai.cn/v4")]
    [InlineData("https://gateway.deepseek.com/v1")]
    public async Task OpenAIVisionInjectsThinkingDisabledOnKnownHosts(string baseURL)
    {
        var captured = await CaptureOpenAIVisionAsync(baseURL);

        Assert.True(captured.Has("thinking"));
        Assert.Equal("disabled", captured.Text("thinking.type"));
    }

    [Theory]
    [InlineData("https://api.openai.com/v1")]
    [InlineData("https://notdeepseek.example.com/v1")]
    [InlineData("https://api.moonshot.cn/v1")]
    public async Task OpenAIVisionDoesNotInjectThinkingOnOtherHosts(string baseURL)
        => Assert.False((await CaptureOpenAIVisionAsync(baseURL)).Has("thinking"));

    // ------------------------------------------------------------ Azure 识图

    [Fact]
    public async Task AzureVisionBodyShape()
    {
        var captured = await CaptureVisionAsync(VisionProviderKind.AzureOpenAI, "https://myres.openai.azure.com");

        Assert.Equal("secret-key", captured.Header("api-key"));
        Assert.Null(captured.Header("Authorization"));
        Assert.Null(captured.Header("x-api-key"));
        Assert.Null(captured.Header("anthropic-version"));
        Assert.Equal("vision-model", captured.Text("model"));
        Assert.Equal(0, captured.Int("temperature"));
        Assert.Equal(2048, captured.Int("max_tokens"));      // ⚠️ Azure 识图 2048
        Assert.Equal("text", captured.Text("messages.0.content.0.type"));
        Assert.Equal("image_url", captured.Text("messages.0.content.1.type"));
    }

    [Fact]
    public async Task AzureVisionInjectsThinkingOnKnownHost()
    {
        var handler = new RecordingHandler();
        handler.Enqueue(RecordingHandler.AllShapes("OK"));
        var client = new MultimodalProviderClient(new HttpClient(handler));
        await client.RecognizeAsync(
            VisionProviderKind.AzureOpenAI,
            "https://open.bigmodel.cn/api/paas/v4",
            "m",
            "k",
            PngImage,
            "PROMPT");

        Assert.Equal("disabled", handler.Single.Text("thinking.type"));
    }

    // ------------------------------------------------------------ Anthropic 识图

    [Fact]
    public async Task AnthropicVisionBodyShape()
    {
        var captured = await CaptureVisionAsync(VisionProviderKind.Anthropic, "https://api.anthropic.com");

        Assert.Equal("secret-key", captured.Header("x-api-key"));
        Assert.Equal("2023-06-01", captured.Header("anthropic-version"));
        Assert.Null(captured.Header("Authorization"));
        Assert.Equal("vision-model", captured.Text("model"));
        Assert.Equal(2048, captured.Int("max_tokens"));      // ⚠️ Anthropic 识图 2048
        Assert.False(captured.Has("temperature"));           // ⚠️ 识图路径不带 temperature
        Assert.False(captured.Has("stream"));

        // 图片在文字之前。
        Assert.Equal("image", captured.Text("messages.0.content.0.type"));
        Assert.Equal("base64", captured.Text("messages.0.content.0.source.type"));
        Assert.Equal("image/png", captured.Text("messages.0.content.0.source.media_type"));
        Assert.Equal("AQID", captured.Text("messages.0.content.0.source.data"));
        Assert.Equal("text", captured.Text("messages.0.content.1.type"));
        Assert.Equal("PROMPT", captured.Text("messages.0.content.1.text"));
    }

    // ------------------------------------------------------------ Gemini 识图

    [Fact]
    public async Task GeminiVisionBodyShape()
    {
        var captured = await CaptureVisionAsync(VisionProviderKind.GoogleGemini, "https://generativelanguage.googleapis.com");

        Assert.Equal("secret-key", captured.Header("x-goog-api-key"));
        Assert.Null(captured.Header("Authorization"));

        // 图片在文字之前。
        Assert.Equal("image/png", captured.Text("contents.0.parts.0.inlineData.mimeType"));
        Assert.Equal("AQID", captured.Text("contents.0.parts.0.inlineData.data"));
        Assert.Equal("PROMPT", captured.Text("contents.0.parts.1.text"));

        Assert.Equal(0, captured.Int("generationConfig.temperature"));
        Assert.Equal(2048, captured.Int("generationConfig.maxOutputTokens")); // ⚠️ Gemini 识图 2048
        Assert.False(captured.Has("max_tokens"));
    }

    // ------------------------------------------------------------ 翻译：纯文字

    [Fact]
    public async Task TranslationTextOnlyUsesStringContent()
    {
        var captured = await CaptureTranslationTextAsync();

        Assert.Equal("Bearer secret-key", captured.Header("Authorization"));
        Assert.Equal("text-model", captured.Text("model"));
        Assert.Equal(0, captured.Int("temperature"));
        Assert.Equal(4096, captured.Int("max_tokens"));      // ⚠️ 翻译默认 4096
        Assert.False(captured.Bool("stream"));               // 显式 stream:false
        Assert.False(captured.Has("response_format"));       // 非 JSON 模式不追加

        // ⚠️ content 是字符串，不是 parts 数组（GLM 文字模型拒绝多模态数组）。
        Assert.Equal(JsonValueKind.String, captured.Node("messages.0.content")!.GetValueKind());
        Assert.Contains("Translate the following content from 英文 to 简体中文.", captured.Text("messages.0.content"));
        Assert.False(captured.Has("thinking"));
    }

    [Fact]
    public async Task TranslationAzureTextOnlyUsesApiKeyAndStringContent()
    {
        var captured = await CaptureTranslationTextAsync(VisionProviderKind.AzureOpenAI, "https://myres.openai.azure.com");

        Assert.Equal("secret-key", captured.Header("api-key"));
        Assert.Null(captured.Header("Authorization"));
        Assert.Equal(JsonValueKind.String, captured.Node("messages.0.content")!.GetValueKind());
        Assert.Equal(4096, captured.Int("max_tokens"));
    }

    [Fact]
    public async Task TranslationConnectionTestUses512MaxTokens()
    {
        var handler = new RecordingHandler();
        var client = new TranslationProviderClient(new HttpClient(handler));
        await client.TestConnectionAsync("https://api.example.com/v1", "m", "k");

        Assert.Equal(512, handler.Single.Int("max_tokens"));
    }

    [Fact]
    public async Task TranslationSegmentModeAddsJsonResponseFormat()
    {
        var handler = new RecordingHandler();
        handler.Enqueue(RecordingHandler.ChatCompletion("{\"translations\":[{\"id\":0,\"text\":\"x\"}]}"));
        var client = new TranslationProviderClient(new HttpClient(handler));
        await client.TranslateSegmentsAsync(
            "https://api.example.com/v1",
            "m",
            "k",
            new[] { TranslationSourceSegment.Create(0, "hello") },
            "英文",
            "简体中文");

        Assert.Equal("json_object", handler.Single.Text("response_format.type"));
        Assert.Equal(4096, handler.Single.Int("max_tokens"));
        Assert.Equal(JsonValueKind.String, handler.Single.Node("messages.0.content")!.GetValueKind());
    }

    [Fact]
    public async Task TranslationWithImageUsesPartsArrayImageAfterText()
    {
        var handler = new RecordingHandler();
        var client = new TranslationProviderClient(new HttpClient(handler));
        await client.TranslateImageAsync("https://api.example.com/v1", "m", "k", PngImage, "英文", "简体中文");

        Assert.Equal(JsonValueKind.Array, handler.Single.Node("messages.0.content")!.GetValueKind());
        Assert.Equal("text", handler.Single.Text("messages.0.content.0.type"));
        Assert.Equal("image_url", handler.Single.Text("messages.0.content.1.type"));
        Assert.StartsWith("data:image/png;base64,", handler.Single.Text("messages.0.content.1.image_url.url"));
    }

    [Fact]
    public async Task TranslationAnthropicTextOnlyHasTemperatureAndSingleTextPart()
    {
        var handler = new RecordingHandler();
        handler.Enqueue(RecordingHandler.AllShapes("OK"));
        var client = new TranslationProviderClient(new HttpClient(handler));
        await client.TranslateTextAsync(
            "https://api.anthropic.com",
            "m",
            "k",
            "hello",
            "英文",
            "简体中文",
            VisionProviderKind.Anthropic);

        Assert.Equal("k", handler.Single.Header("x-api-key"));
        Assert.Equal("2023-06-01", handler.Single.Header("anthropic-version"));
        Assert.Equal(4096, handler.Single.Int("max_tokens"));
        Assert.Equal(0, handler.Single.Int("temperature"));  // ⚠️ 翻译路径额外带 temperature:0
        Assert.False(handler.Single.Has("stream"));

        // 纯文字翻译：只有文字一个 part。
        Assert.Single(handler.Single.Json()["messages"]![0]!["content"]!.AsArray());
        Assert.Equal("text", handler.Single.Text("messages.0.content.0.type"));
    }

    [Fact]
    public async Task TranslationAnthropicWithImagePutsImageBeforeText()
    {
        var handler = new RecordingHandler();
        handler.Enqueue(RecordingHandler.AllShapes("OK"));
        var client = new TranslationProviderClient(new HttpClient(handler));
        await client.TranslateImageAsync(
            "https://api.anthropic.com",
            "m",
            "k",
            PngImage,
            "英文",
            "简体中文",
            provider: VisionProviderKind.Anthropic);

        Assert.Equal("image", handler.Single.Text("messages.0.content.0.type"));
        Assert.Equal("image/png", handler.Single.Text("messages.0.content.0.source.media_type"));
        Assert.Equal("text", handler.Single.Text("messages.0.content.1.type"));
    }

    [Fact]
    public async Task TranslationGeminiUsesMaxOutputTokens()
    {
        var handler = new RecordingHandler();
        handler.Enqueue(RecordingHandler.AllShapes("OK"));
        var client = new TranslationProviderClient(new HttpClient(handler));
        await client.TranslateTextAsync(
            "https://generativelanguage.googleapis.com",
            "gemini-2.0-flash",
            "k",
            "hello",
            "英文",
            "简体中文",
            VisionProviderKind.GoogleGemini);

        Assert.Equal(4096, handler.Single.Int("generationConfig.maxOutputTokens"));
        Assert.Equal(0, handler.Single.Int("generationConfig.temperature"));
        Assert.False(handler.Single.Has("max_tokens"));
        Assert.False(handler.Single.Has("stream"));
    }

    [Fact]
    public async Task TranslationInjectsThinkingOnKnownHost()
    {
        var handler = new RecordingHandler();
        var client = new TranslationProviderClient(new HttpClient(handler));
        await client.TestConnectionAsync("https://api.deepseek.com/v1", "m", "k");

        Assert.Equal("disabled", handler.Single.Text("thinking.type"));
    }

    // ------------------------------------------------------------ DeepSeek-OCR-2

    [Fact]
    public async Task DeepSeekOCRBodyShape()
    {
        var captured = await CaptureOcr2Async("local-key");

        Assert.Equal("Bearer local-key", captured.Header("Authorization"));
        Assert.Equal("deepseek-ai/DeepSeek-OCR-2", captured.Text("model"));
        Assert.Equal(0, captured.Int("temperature"));
        Assert.Equal(4096, captured.Int("max_tokens"));
        Assert.False(captured.Has("stream"));
        Assert.False(captured.Has("thinking"));      // OCR-2 客户端不注入 thinking

        // prompt 在前、图片在后。
        Assert.Equal("<image>\nFree OCR.", captured.Text("messages.0.content.0.text"));
        Assert.Equal("image_url", captured.Text("messages.0.content.1.type"));
        Assert.StartsWith("data:image/png;base64,", captured.Text("messages.0.content.1.image_url.url"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task DeepSeekOCROmitsAuthorizationWhenKeyEmpty(string apiKey)
        => Assert.Null((await CaptureOcr2Async(apiKey)).Header("Authorization"));

    [Fact]
    public async Task DeepSeekOCRDoesNotInjectThinkingEvenOnDeepSeekLikeHost()
    {
        var handler = new RecordingHandler();
        var client = new DeepSeekOCR2Client(new HttpClient(handler));
        await client.RecognizeAsync(
            "https://deepseek-ocr.example.com/v1",
            "m",
            "k",
            PngImage,
            DeepSeekOCRPromptMode.PlainText);

        Assert.False(handler.Single.Has("thinking"));
    }
}
