using Ta.AI;

namespace Ta.AI.Tests;

/// <summary>
/// 端点拼接测试（参考文档 §9.3 的端点规则）。
///
/// Mac 实现位置：
///   · OpenAI 兼容识图：<c>OpenAICompatibleVisionClient.swift:97-117</c>
///   · Azure / Anthropic / Gemini 识图：<c>MultimodalProviderClient.swift:169-197</c>
///   · 翻译：<c>TranslationProviderClient.swift:269-309</c>
///   · DeepSeek-OCR-2：<c>DeepSeekOCR2Client.swift:109-132</c>
/// </summary>
public class EndpointCompositionTests
{
    private static async Task<Uri> VisionEndpointAsync(
        VisionProviderKind kind,
        string baseURL,
        string model = "m")
    {
        var handler = new RecordingHandler();
        // 用「三套解析器都能读」的响应体，这样本测试只关心端点 URL。
        handler.Enqueue(RecordingHandler.AllShapes("OK"));
        var client = new MultimodalProviderClient(new HttpClient(handler));
        await client.RecognizeAsync(kind, baseURL, model, "k", new byte[] { 1 }, "prompt");
        return handler.Single.Uri;
    }

    // ------------------------------------------------------------ OpenAI 兼容

    [Theory]
    [InlineData("https://api.example.com/v1", "https://api.example.com/v1/chat/completions")]
    [InlineData("https://api.example.com/v1/", "https://api.example.com/v1/chat/completions")]
    [InlineData("https://api.example.com", "https://api.example.com/chat/completions")]
    [InlineData("https://api.example.com/openai/v1", "https://api.example.com/openai/v1/chat/completions")]
    [InlineData("https://api.example.com/v1/chat/completions", "https://api.example.com/v1/chat/completions")]
    [InlineData("https://api.deepseek.com", "https://api.deepseek.com/chat/completions")]
    public async Task OpenAICompatibleAppendsChatCompletionsOnce(string baseURL, string expected)
    {
        var handler = new RecordingHandler();
        var client = new OpenAICompatibleVisionClient(new HttpClient(handler));
        await client.RecognizeAsync(baseURL, "m", "k", new byte[] { 1 }, "prompt");

        Assert.Equal(new Uri(expected), handler.Single.Uri);
        // 只出现一次 chat/completions（不重复追加）。
        Assert.Equal(1, handler.Single.Uri.AbsolutePath.Split("chat/completions").Length - 1);
    }

    [Fact]
    public async Task TranslationOpenAICompatibleAppendsChatCompletions()
    {
        var handler = new RecordingHandler();
        var client = new TranslationProviderClient(new HttpClient(handler));
        await client.TestConnectionAsync("https://api.deepseek.com/v1", "m", "k");

        Assert.Equal(new Uri("https://api.deepseek.com/v1/chat/completions"), handler.Single.Uri);
    }

    // ------------------------------------------------------------ Azure

    [Theory]
    [InlineData("https://myres.openai.azure.com", "https://myres.openai.azure.com/openai/v1/chat/completions")]
    [InlineData("https://myres.openai.azure.com/", "https://myres.openai.azure.com/openai/v1/chat/completions")]
    // ⚠️ Mac 是「整条 path 后面直接追加」，不识别已有的 openai 段（MultimodalProviderClient.swift:181-183）。
    [InlineData("https://myres.openai.azure.com/openai", "https://myres.openai.azure.com/openai/openai/v1/chat/completions")]
    [InlineData("https://myres.openai.azure.com/openai/v1/chat/completions",
        "https://myres.openai.azure.com/openai/v1/chat/completions")]
    public async Task AzureAppendsOpenAIV1ChatCompletionsOnce(string baseURL, string expected)
        => Assert.Equal(new Uri(expected), await VisionEndpointAsync(VisionProviderKind.AzureOpenAI, baseURL));

    [Fact]
    public async Task AzureTranslationEndpointMatchesVisionEndpoint()
    {
        var handler = new RecordingHandler();
        var client = new TranslationProviderClient(new HttpClient(handler));
        await client.TestConnectionAsync(
            "https://myres.openai.azure.com",
            "m",
            "k",
            VisionProviderKind.AzureOpenAI);

        Assert.Equal(
            new Uri("https://myres.openai.azure.com/openai/v1/chat/completions"),
            handler.Single.Uri);
    }

    // ------------------------------------------------------------ Anthropic

    [Theory]
    [InlineData("https://api.anthropic.com", "https://api.anthropic.com/v1/messages")]
    // ⚠️ Mac 同样是整条 path 后追加 v1/messages（MultimodalProviderClient.swift:185-187）。
    [InlineData("https://api.anthropic.com/v1", "https://api.anthropic.com/v1/v1/messages")]
    [InlineData("https://gw.example.com/anthropic/v1/messages", "https://gw.example.com/anthropic/v1/messages")]
    public async Task AnthropicAppendsV1MessagesOnce(string baseURL, string expected)
        => Assert.Equal(new Uri(expected), await VisionEndpointAsync(VisionProviderKind.Anthropic, baseURL));

    // ------------------------------------------------------------ Gemini

    [Theory]
    [InlineData("https://generativelanguage.googleapis.com",
        "https://generativelanguage.googleapis.com/v1beta/models/gemini-2.0-flash:generateContent")]
    [InlineData("https://gw.example.com/v1beta",
        "https://gw.example.com/v1beta/v1beta/models/gemini-2.0-flash:generateContent")]
    [InlineData("https://gw.example.com/v1beta/models/gemini-2.0-flash:generateContent",
        "https://gw.example.com/v1beta/models/gemini-2.0-flash:generateContent")]
    public async Task GeminiAppendsGenerateContentOnce(string baseURL, string expected)
        => Assert.Equal(new Uri(expected), await VisionEndpointAsync(VisionProviderKind.GoogleGemini, baseURL, "gemini-2.0-flash"));

    [Fact]
    public async Task GeminiKeepsQueryString()
    {
        var handler = new RecordingHandler();
        handler.Enqueue(RecordingHandler.AllShapes("OK"));
        var client = new MultimodalProviderClient(new HttpClient(handler));
        await client.RecognizeAsync(
            VisionProviderKind.GoogleGemini,
            "https://gw.example.com?key=abc",
            "gemini-2.0-flash",
            "k",
            new byte[] { 1 },
            "prompt");

        Assert.Equal("https://gw.example.com/v1beta/models/gemini-2.0-flash:generateContent?key=abc", handler.Single.Uri.ToString());
    }

    // ------------------------------------------------------------ 翻译的特例

    [Fact]
    public async Task TranslationTrimsTrailingComma()
    {
        var handler = new RecordingHandler();
        var client = new TranslationProviderClient(new HttpClient(handler));
        await client.TestConnectionAsync("https://api.example.com/v1，", "m", "k");

        Assert.Equal(new Uri("https://api.example.com/v1/chat/completions"), handler.Single.Uri);
    }

    [Fact]
    public async Task TranslationAnthropicAndGeminiEndpoints()
    {
        var anthropic = new RecordingHandler();
        anthropic.Enqueue(RecordingHandler.AllShapes("OK"));
        await new TranslationProviderClient(new HttpClient(anthropic))
            .TestConnectionAsync("https://api.anthropic.com", "m", "k", VisionProviderKind.Anthropic);
        Assert.Equal(new Uri("https://api.anthropic.com/v1/messages"), anthropic.Single.Uri);

        var gemini = new RecordingHandler();
        gemini.Enqueue(RecordingHandler.AllShapes("OK"));
        await new TranslationProviderClient(new HttpClient(gemini))
            .TestConnectionAsync("https://generativelanguage.googleapis.com", "gemini-2.0-flash", "k", VisionProviderKind.GoogleGemini);
        Assert.Equal(
            new Uri("https://generativelanguage.googleapis.com/v1beta/models/gemini-2.0-flash:generateContent"),
            gemini.Single.Uri);
    }

    // ------------------------------------------------------------ DeepSeek-OCR-2

    [Fact]
    public async Task DeepSeekOCRAppendsChatCompletions()
    {
        var handler = new RecordingHandler();
        var client = new DeepSeekOCR2Client(new HttpClient(handler));
        await client.RecognizeAsync("http://127.0.0.1:8000/v1", "m", string.Empty, new byte[] { 1 }, DeepSeekOCRPromptMode.PlainText);

        Assert.Equal(new Uri("http://127.0.0.1:8000/v1/chat/completions"), handler.Single.Uri);
    }

    [Theory]
    [InlineData("https://api.deepseek.com")]
    [InlineData("https://api.deepseek.com/v1")]
    [InlineData("https://API.DEEPSEEK.COM")]
    public async Task DeepSeekOCRRejectsOfficialApi(string baseURL)
    {
        var client = new DeepSeekOCR2Client(new HttpClient(new RecordingHandler()));

        var error = await Assert.ThrowsAsync<DeepSeekOCR2ClientException>(() => client.RecognizeAsync(
            baseURL,
            "deepseek-ai/DeepSeek-OCR-2",
            "k",
            new byte[] { 1 },
            DeepSeekOCRPromptMode.PlainText));

        Assert.IsType<DeepSeekOCR2ClientError.OfficialApiUnsupported>(error.Error);
        Assert.Equal(
            "DeepSeek 官方 API 当前没有提供 DeepSeek-OCR-2 模型端点；请填写部署了该模型的 vLLM、SGLang 或兼容服务地址。",
            error.Error.ErrorDescription);
    }

    [Fact]
    public async Task DeepSeekOCRLocalHttpIsAllowed()
    {
        var handler = new RecordingHandler();
        var client = new DeepSeekOCR2Client(new HttpClient(handler));
        await client.RecognizeAsync(
            DeepSeekOCR2Client.RecommendedLocalBaseUrl,
            DeepSeekOCR2Client.LatestOfficialModel,
            string.Empty,
            new byte[] { 1 },
            DeepSeekOCRPromptMode.PlainText);

        Assert.Equal(new Uri("http://127.0.0.1:8000/v1/chat/completions"), handler.Single.Uri);
    }

    // ------------------------------------------------------------ 端点校验

    [Theory]
    [InlineData("https://api.example.com/v1", null)]
    [InlineData("http://localhost:8000/v1", null)]
    [InlineData("http://127.0.0.1:8000/v1", null)]
    [InlineData("http://[::1]:8000/v1", null)]
    [InlineData("http://api.example.com/v1", "远程服务必须使用 HTTPS")]
    [InlineData("ftp://api.example.com/v1", "远程服务必须使用 HTTPS")]
    [InlineData("api.example.com/v1", "Base URL 格式无效")]
    [InlineData("not a url", "Base URL 格式无效")]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void EndpointValidatorMessages(string? baseURL, string? expected)
        => Assert.Equal(expected, new ProviderEndpointValidator().ValidationMessage(baseURL!));

    [Theory]
    [InlineData("http://api.example.com/v1", "远程服务必须使用 HTTPS")]
    [InlineData("example.com/v1", "Base URL 格式无效")]
    public async Task VisionClientSurfacesValidatorMessage(string baseURL, string expected)
    {
        var client = new MultimodalProviderClient(new HttpClient(new RecordingHandler()));

        var error = await Assert.ThrowsAsync<VisionClientException>(() => client.RecognizeAsync(
            VisionProviderKind.AzureOpenAI,
            baseURL,
            "m",
            "k",
            new byte[] { 1 },
            "prompt"));

        Assert.Equal(expected, Assert.IsType<VisionClientError.InvalidEndpoint>(error.Error).Message);
    }

    [Theory]
    [InlineData("", "请先配置 Base URL。")]
    [InlineData("   ", "请先配置 Base URL。")]
    public async Task VisionClientEmptyBaseUrlMessage(string baseURL, string expected)
    {
        var client = new OpenAICompatibleVisionClient(new HttpClient(new RecordingHandler()));

        var error = await Assert.ThrowsAsync<VisionClientException>(() => client.RecognizeAsync(
            baseURL,
            "m",
            "k",
            new byte[] { 1 },
            "prompt"));

        Assert.Equal(expected, Assert.IsType<VisionClientError.InvalidEndpoint>(error.Error).Message);
    }

    [Fact]
    public async Task TranslationAndDeepSeekEmptyBaseUrlMessages()
    {
        var translation = new TranslationProviderClient(new HttpClient(new RecordingHandler()));
        var translationError = await Assert.ThrowsAsync<TranslationProviderException>(() =>
            translation.TestConnectionAsync(string.Empty, "m", "k"));
        Assert.Equal("请先配置翻译 API 地址。", Assert.IsType<TranslationProviderError.InvalidEndpoint>(translationError.Error).Message);

        var ocr = new DeepSeekOCR2Client(new HttpClient(new RecordingHandler()));
        var ocrError = await Assert.ThrowsAsync<DeepSeekOCR2ClientException>(() => ocr.RecognizeAsync(
            string.Empty,
            "m",
            "k",
            new byte[] { 1 },
            DeepSeekOCRPromptMode.PlainText));
        Assert.Equal("请先配置 DeepSeek OCR 服务地址。", Assert.IsType<DeepSeekOCR2ClientError.InvalidEndpoint>(ocrError.Error).Message);
    }

    [Theory]
    [InlineData("", "尚未配置视觉模型。")]
    [InlineData("   ", "尚未配置视觉模型。")]
    public async Task MissingModelAndApiKey(string model, string expected)
    {
        var client = new MultimodalProviderClient(new HttpClient(new RecordingHandler()));

        var modelError = await Assert.ThrowsAsync<VisionClientException>(() => client.RecognizeAsync(
            VisionProviderKind.Anthropic,
            "https://api.anthropic.com",
            model,
            "k",
            new byte[] { 1 },
            "prompt"));
        Assert.Equal(expected, Assert.IsType<VisionClientError.MissingModel>(modelError.Error).ErrorDescription);

        var keyError = await Assert.ThrowsAsync<VisionClientException>(() => client.RecognizeAsync(
            VisionProviderKind.Anthropic,
            "https://api.anthropic.com",
            "m",
            "  ",
            new byte[] { 1 },
            "prompt"));
        Assert.Equal("尚未配置 API Key。", Assert.IsType<VisionClientError.MissingApiKey>(keyError.Error).ErrorDescription);
    }
}
