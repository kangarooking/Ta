using Ta.AI;

namespace Ta.AI.Tests;

/// <summary>
/// ⚠️ **最重要的一组测试**：prompt 必须与 Mac 版逐字一致（含标点、空格、换行、弯引号）。
/// 这是产品行为，改一个字符就会改变模型输出。
///
/// 期望值来源（macOS 仓库）：
///   · 8 个任务模板：<c>Sources/AIScreenshotCore/Recognition/MultimodalProviderClient.swift:42-63</c>
///   · 纯文字翻译：<c>TranslationProviderClient.swift:50-58</c>
///   · 分段翻译：<c>TranslationProviderClient.swift:81-88</c>
///   · 图片翻译：<c>TranslationProviderClient.swift:115-119</c>
///   · 翻译连接测试：<c>TranslationProviderClient.swift:143</c>
///   · 识图连接测试 / recovery：<c>AIScreenshotApp/Recognition/MultimodalRecognitionService.swift:97, 15-17</c>
///   · DeepSeek-OCR-2：<c>DeepSeekOCR2Client.swift:14-19</c>
/// </summary>
public class PromptFidelityTests
{
    // ------------------------------------------------------------ 8 个任务模板

    [Theory]
    [InlineData(MultimodalTaskTemplate.General,
        "这是视觉理解任务，不是单纯的文字识别。请先描述图片中实际可见的主体、人物或动物、物体、场景、动作、颜色与构图；即使图片完全没有文字，也必须说明画面内容，不能只回答“没有文字”。如果图片包含文字，再准确整理文字，并保留原语言、段落、列表、代码和表格结构。直接输出结果，不要猜测看不清的内容。")]
    [InlineData(MultimodalTaskTemplate.ExtractText,
        "逐字提取截图中的全部可见文字，保持阅读顺序、段落和换行；不要总结、翻译或补写。")]
    [InlineData(MultimodalTaskTemplate.TranslateChinese,
        "识别截图中的内容并翻译成自然、准确的中文；代码、专有名词和数字保持原意。只输出译文。")]
    [InlineData(MultimodalTaskTemplate.TranslateEnglish,
        "识别截图中的内容并翻译成自然、准确的英文；代码、专有名词和数字保持原意。只输出译文。")]
    [InlineData(MultimodalTaskTemplate.ExplainCode,
        "提取截图中的代码，先输出可复制的完整代码块，再用简洁中文说明语言、用途和明显问题；不要虚构被遮挡的代码。")]
    [InlineData(MultimodalTaskTemplate.TableMarkdown,
        "识别截图中的表格，严格按行列输出为 Markdown 表格。合并单元格用最接近的重复值表达，不要输出额外说明。")]
    [InlineData(MultimodalTaskTemplate.TableCSV,
        "识别截图中的表格并输出合法 CSV。正确转义逗号、引号和换行，只输出 CSV 内容。")]
    [InlineData(MultimodalTaskTemplate.FormulaLaTeX,
        "识别截图中的数学公式并输出可复制的 LaTeX；多行公式使用 aligned 环境。只输出 LaTeX，不要解释或猜测模糊符号。")]
    public void TaskTemplatePromptMatchesMacVerbatim(MultimodalTaskTemplate template, string expected)
    {
        Assert.Equal(expected, template.Prompt());
    }

    [Fact]
    public void TaskTemplateCountAndRawValuesMatchMac()
    {
        Assert.Equal(8, MultimodalTaskTemplateExtensions.AllCases.Count);
        Assert.Equal(
            new[]
            {
                "general", "extractText", "translateChinese", "translateEnglish",
                "explainCode", "tableMarkdown", "tableCSV", "formulaLaTeX",
            },
            MultimodalTaskTemplateExtensions.AllCases.Select(t => t.RawValue()));
    }

    [Theory]
    [InlineData(MultimodalTaskTemplate.General, "通用识图")]
    [InlineData(MultimodalTaskTemplate.ExtractText, "精确取字")]
    [InlineData(MultimodalTaskTemplate.TranslateChinese, "翻译成中文")]
    [InlineData(MultimodalTaskTemplate.TranslateEnglish, "翻译成英文")]
    [InlineData(MultimodalTaskTemplate.ExplainCode, "提取并解释代码")]
    [InlineData(MultimodalTaskTemplate.TableMarkdown, "表格转 Markdown")]
    [InlineData(MultimodalTaskTemplate.TableCSV, "表格转 CSV")]
    [InlineData(MultimodalTaskTemplate.FormulaLaTeX, "公式转 LaTeX")]
    public void TaskTemplateDisplayNameMatchesMac(MultimodalTaskTemplate template, string expected)
        => Assert.Equal(expected, template.DisplayName());

    // ------------------------------------------------------------ 翻译 prompt

    [Fact]
    public async Task TranslateTextPromptMatchesMacVerbatim()
    {
        var handler = new RecordingHandler();
        var client = new TranslationProviderClient(new HttpClient(handler));

        await client.TranslateTextAsync(
            baseURL: "https://api.example.com/v1",
            model: "m",
            apiKey: "k",
            text: "  Hello  ",
            sourceLanguage: "自动检测",
            targetLanguage: "简体中文");

        var expected = string.Join(
            "\n",
            "Translate the following content from 自动检测 to 简体中文.",
            "Preserve paragraphs, lists, code, numbers, names, and Markdown structure.",
            "Do not summarize, explain, or add labels. Output only the translation.",
            string.Empty,
            "<content>",
            "Hello",
            "</content>");

        Assert.Equal(expected, handler.Single.Text("messages.0.content"));
    }

    [Fact]
    public async Task TranslateSegmentsPromptMatchesMacVerbatim()
    {
        var handler = new RecordingHandler();
        handler.Enqueue(RecordingHandler.ChatCompletion("{\"translations\":[{\"id\":0,\"text\":\"a\"},{\"id\":1,\"text\":\"b\"}]}"));
        var client = new TranslationProviderClient(new HttpClient(handler));

        await client.TranslateSegmentsAsync(
            baseURL: "https://api.example.com/v1",
            model: "m",
            apiKey: "k",
            segments: new[] { TranslationSourceSegment.Create(0, "Hello"), TranslationSourceSegment.Create(1, "World") },
            sourceLanguage: "英文",
            targetLanguage: "简体中文");

        var expected = string.Join(
            "\n",
            "Translate every JSON item's text from 英文 to 简体中文.",
            "Return valid JSON only in this exact shape: {\"translations\":[{\"id\":0,\"text\":\"...\"}]}.",
            "Preserve every id exactly once and keep short UI labels concise. Do not add explanations.",
            string.Empty,
            "Input JSON:",
            "[{\"id\":0,\"text\":\"Hello\"},{\"id\":1,\"text\":\"World\"}]");

        Assert.Equal(expected, handler.Single.Text("messages.0.content"));
    }

    [Fact]
    public async Task TranslateImagePromptMatchesMacVerbatim()
    {
        var handler = new RecordingHandler();
        var client = new TranslationProviderClient(new HttpClient(handler));

        await client.TranslateImageAsync(
            baseURL: "https://api.example.com/v1",
            model: "m",
            apiKey: "k",
            imageData: new byte[] { 1, 2, 3 },
            sourceLanguage: "英文",
            targetLanguage: "简体中文");

        var expected = string.Join(
            "\n",
            "Read all visible text in this screenshot and translate it from 英文 to 简体中文.",
            "Preserve reading order, paragraphs, lists, code, numbers, and names.",
            "Do not describe the image or explain. Output only the translated text.");

        // 带图时 content 是 parts 数组，文字在图片之后。
        Assert.Equal(expected, handler.Single.Text("messages.0.content.0.text"));
    }

    [Fact]
    public async Task TranslationConnectionTestPromptMatchesMac()
    {
        var handler = new RecordingHandler();
        var client = new TranslationProviderClient(new HttpClient(handler));

        await client.TestConnectionAsync("https://api.example.com/v1", "m", "k");

        Assert.Equal("Reply with exactly: OK", handler.Single.Text("messages.0.content"));
    }

    // ------------------------------------------------------------ 识图 / OCR-2 prompt

    [Fact]
    public void VisionConnectionTestPromptMatchesMac()
        => Assert.Equal(
            "这是拓的连接测试图片。请只回复你在图片中看到的英文单词和数字，不要添加解释。",
            MultimodalConnectionTest.Prompt);

    [Fact]
    public void GeneralVisionRecoveryPromptMatchesMac()
        => Assert.Equal(
            "请重新查看图片本身。这是视觉理解任务，不是 OCR。请直接描述图中可见的主体、动物或人物、物体、场景、动作、颜色和构图。即使没有任何文字，也必须描述画面；不要只回答“没有文字”。",
            GeneralVisionResponsePolicy.RecoveryPrompt);

    [Theory]
    [InlineData(DeepSeekOCRPromptMode.PlainText, "<image>\nFree OCR.")]
    [InlineData(DeepSeekOCRPromptMode.DocumentMarkdown, "<image>\n<|grounding|>Convert the document to markdown.")]
    public void DeepSeekOCRPromptMatchesMac(DeepSeekOCRPromptMode mode, string expected)
        => Assert.Equal(expected, mode.Prompt());

    [Fact]
    public async Task DeepSeekOCRRequestSendsPromptVerbatim()
    {
        var handler = new RecordingHandler();
        var client = new DeepSeekOCR2Client(new HttpClient(handler));

        await client.RecognizeAsync(
            baseURL: "http://127.0.0.1:8000/v1",
            model: "deepseek-ai/DeepSeek-OCR-2",
            apiKey: string.Empty,
            imageData: new byte[] { 9 },
            mode: DeepSeekOCRPromptMode.DocumentMarkdown);

        Assert.Equal("<image>\n<|grounding|>Convert the document to markdown.", handler.Single.Text("messages.0.content.0.text"));
    }

    /// <summary>任务模板 prompt 真的被塞进请求体（端到端确认 prompt 链路）。</summary>
    [Fact]
    public async Task RecognizeSendsSelectedTemplatePrompt()
    {
        var handler = new RecordingHandler();
        var client = new MultimodalProviderClient(new HttpClient(handler));

        await client.RecognizeAsync(
            VisionProviderKind.OpenAICompatible,
            "https://api.example.com/v1",
            "m",
            "k",
            new byte[] { 1 },
            MultimodalTaskTemplate.FormulaLaTeX.Prompt());

        Assert.Equal(MultimodalTaskTemplate.FormulaLaTeX.Prompt(), handler.Single.Text("messages.0.content.0.text"));
    }
}
