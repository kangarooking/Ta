using Ta.AI;
using Ta.Core.Imaging;
using Ta.Translate;
using Ta.Translate.Rendering;

namespace Ta.Translate.Tests;

/// <summary>
/// 编排流程测试（对应 Mac: CaptureCoordinator.performTranslation:666-791 与
/// ScreenshotTranslationService.translateLines:63-91）。
///
/// 覆盖：模式路由、视觉回退条件（含 0.55 与 0.72 两个阈值的区分）、
/// 批量单请求策略、译文缺失行的丢弃、bbox/confidence 保留。
/// </summary>
public class TranslationOrchestrationTests
{
    private static readonly TranslationCredentials Credentials = new()
    {
        BaseUrl = "https://api.deepseek.com/chat/completions",
        TextModel = "deepseek-v4-flash",
        VisionModel = "deepseek-v4-flash-vision-exp",
        ApiKey = "test-key",
        ProviderKind = VisionProviderKind.OpenAICompatible,
    };

    private static ScreenshotTranslationService CreateService(
        FakeTranslationClient client,
        FakeOcrEngine ocr,
        TranslationConfiguration? configuration = null,
        FakeClipboard? clipboard = null,
        FakeImageEncoder? encoder = null)
    {
        clipboard ??= new FakeClipboard();
        encoder ??= new FakeImageEncoder();
        return new ScreenshotTranslationService(
            client,
            ocr,
            encoder,
            clipboard,
            new FakeCredentials(Credentials),
            configuration ?? TranslationConfiguration.Default());
    }

    private static OcrResult OcrWithLines(
        string text,
        float confidence,
        params (string Text, double X, double Y, double W, double H)[] boxes)
    {
        var lines = boxes
            .Select(b => TestBitmaps.OcrLine(b.Text, confidence, b.X, b.Y, b.W, b.H))
            .ToList();
        return new OcrResult
        {
            Text = text,
            Confidence = confidence,
            Document = new OcrDocumentLayout
            {
                Blocks = new[] { new OcrDocumentBlock { Lines = lines } },
            },
        };
    }

    // ---------- 模式路由（Mac: TranslationModeRouting.swift:43-57） ----------

    [Fact]
    public void 工具栏翻译走配置的默认模式()
    {
        var mode = TranslationModeRouting.Mode(
            TranslationQuickAction.Translate,
            ScreenshotTranslationMode.FullImage);

        Assert.Equal(ScreenshotTranslationMode.FullImage, mode);
    }

    [Fact]
    public void 翻译文字快捷键强制textOnly()
    {
        // 即使配置的默认模式是 bilingualImage，快捷键也必须落到 textOnly。
        var mode = TranslationModeRouting.Mode(
            TranslationQuickAction.TranslateText,
            ScreenshotTranslationMode.BilingualImage);

        Assert.Equal(ScreenshotTranslationMode.TextOnly, mode);
    }

    // ---------- OCR 失败分支（Mac: :694-715） ----------

    [Fact]
    public async Task OCR失败_textOnly加视觉回退_切视觉模型并复制文字()
    {
        var client = new FakeTranslationClient
        {
            OnImage = _ => "视觉模型译文",
        };
        var ocr = new FakeOcrEngine { Failure = new InvalidOperationException("OCR 不可用") };
        var clipboard = new FakeClipboard();
        var service = CreateService(client, ocr, clipboard: clipboard);

        var outcome = await service.PerformTranslationAsync(
            TestBitmaps.Solid(64, 32, RgbaColor.White),
            ScreenshotTranslationMode.TextOnly);

        Assert.Single(client.ImageRequests);
        Assert.Empty(client.TextRequests);
        Assert.Equal("视觉模型译文", outcome.TranslatedText);
        Assert.True(outcome.CopiedToClipboard);
        Assert.Equal("翻译结果已复制", outcome.Message);
    }

    [Fact]
    public async Task OCR失败_图像模式直接报localOCRUnavailableForImage()
    {
        var client = new FakeTranslationClient();
        var ocr = new FakeOcrEngine { Failure = new InvalidOperationException("OCR 不可用") };
        var service = CreateService(client, ocr);

        var error = await Assert.ThrowsAsync<ScreenshotTranslationServiceError>(() =>
            service.PerformTranslationAsync(
                TestBitmaps.Solid(64, 32, RgbaColor.White),
                ScreenshotTranslationMode.FullImage));

        Assert.Equal(ScreenshotTranslationServiceError.Code.LocalOcrUnavailableForImage, error.ErrorCode);
        Assert.Empty(client.ImageRequests);
    }

    [Fact]
    public async Task OCR失败_textOnly但关闭视觉回退_同样报错()
    {
        var client = new FakeTranslationClient();
        var ocr = new FakeOcrEngine { Failure = new InvalidOperationException("OCR 不可用") };
        var service = CreateService(
            client,
            ocr,
            new TranslationConfiguration { UsesVisionFallback = false });

        var error = await Assert.ThrowsAsync<ScreenshotTranslationServiceError>(() =>
            service.PerformTranslationAsync(
                TestBitmaps.Solid(64, 32, RgbaColor.White),
                ScreenshotTranslationMode.TextOnly));

        Assert.Equal(ScreenshotTranslationServiceError.Code.LocalOcrUnavailableForImage, error.ErrorCode);
    }

    // ---------- textOnly 的 0.55 阈值（Mac: :719-720） ----------

    [Fact]
    public async Task 置信度高于055走文字模型()
    {
        var client = new FakeTranslationClient { OnText = _ => "文字模型译文" };
        var ocr = new FakeOcrEngine
        {
            Result = OcrWithLines("hello world", 0.9f, ("hello world", 0.1, 0.1, 0.5, 0.2)),
        };
        var service = CreateService(client, ocr);

        var outcome = await service.PerformTranslationAsync(
            TestBitmaps.Solid(64, 32, RgbaColor.White),
            ScreenshotTranslationMode.TextOnly);

        Assert.Single(client.TextRequests);
        Assert.Empty(client.ImageRequests);
        Assert.Equal("文字模型译文", outcome.TranslatedText);
    }

    [Fact]
    public async Task 置信度低于055走视觉模型()
    {
        var client = new FakeTranslationClient { OnImage = _ => "视觉模型译文" };
        var ocr = new FakeOcrEngine
        {
            Result = OcrWithLines("blurry text", 0.5f, ("blurry text", 0.1, 0.1, 0.5, 0.2)),
        };
        var service = CreateService(client, ocr);

        var outcome = await service.PerformTranslationAsync(
            TestBitmaps.Solid(64, 32, RgbaColor.White),
            ScreenshotTranslationMode.TextOnly);

        Assert.Single(client.ImageRequests);
        Assert.Empty(client.TextRequests);
        Assert.Equal("视觉模型译文", outcome.TranslatedText);
    }

    [Fact]
    public async Task 置信度介于055与072之间仍走文字模型()
    {
        // ⚠️ 关键区分：0.72 是 OCR 侧 isLowConfidence 的阈值，与翻译的 0.55 无关。
        // 0.65 < 0.72 但 ≥ 0.55 → 翻译流程仍用文字模型，不得切视觉。
        var client = new FakeTranslationClient { OnText = _ => "文字模型译文" };
        var ocr = new FakeOcrEngine
        {
            Result = OcrWithLines("some text", 0.65f, ("some text", 0.1, 0.1, 0.5, 0.2)),
        };
        var service = CreateService(client, ocr);

        var outcome = await service.PerformTranslationAsync(
            TestBitmaps.Solid(64, 32, RgbaColor.White),
            ScreenshotTranslationMode.TextOnly);

        Assert.Single(client.TextRequests);
        Assert.Empty(client.ImageRequests);
        Assert.Equal("文字模型译文", outcome.TranslatedText);
    }

    [Fact]
    public async Task 文字为空时走视觉模型即使置信度很高()
    {
        var client = new FakeTranslationClient { OnImage = _ => "视觉模型译文" };
        var ocr = new FakeOcrEngine { Result = OcrWithLines(string.Empty, 1.0f) };
        var service = CreateService(client, ocr);

        var outcome = await service.PerformTranslationAsync(
            TestBitmaps.Solid(64, 32, RgbaColor.White),
            ScreenshotTranslationMode.TextOnly);

        Assert.Single(client.ImageRequests);
        Assert.Equal("视觉模型译文", outcome.TranslatedText);
    }

    [Fact]
    public async Task 文字为空且关闭回退时报emptyInput()
    {
        var client = new FakeTranslationClient();
        var ocr = new FakeOcrEngine { Result = OcrWithLines(string.Empty, 1.0f) };
        var service = CreateService(
            client,
            ocr,
            new TranslationConfiguration { UsesVisionFallback = false });

        var error = await Assert.ThrowsAsync<TranslationProviderError>(() =>
            service.PerformTranslationAsync(
                TestBitmaps.Solid(64, 32, RgbaColor.White),
                ScreenshotTranslationMode.TextOnly));

        Assert.Equal(TranslationProviderError.Code.EmptyInput, error.ErrorCode);
    }

    // ---------- 图像模式：批量翻译（Mac: :755-790 + translateLines:63-91） ----------

    [Fact]
    public async Task 图像模式一次请求携带全部文字行()
    {
        var client = new FakeTranslationClient
        {
            OnSegments = request => request.Segments
                .Select(s => new TranslationSegmentResult(s.Id, $"译文{s.Id}"))
                .ToList(),
        };
        var ocr = new FakeOcrEngine
        {
            Result = OcrWithLines(
                "line1 line2 line3",
                0.9f,
                ("line1", 0.1, 0.1, 0.4, 0.1),
                ("   ", 0.1, 0.3, 0.4, 0.1), // 纯空白行应在入参前被过滤
                ("line2", 0.1, 0.5, 0.4, 0.1),
                ("line3", 0.1, 0.7, 0.4, 0.1)),
        };
        var clipboard = new FakeClipboard();
        var service = CreateService(client, ocr, clipboard: clipboard);

        var outcome = await service.PerformTranslationAsync(
            TestBitmaps.Solid(200, 200, RgbaColor.White),
            ScreenshotTranslationMode.FullImage);

        // 一次请求、三个非空段落（§9.4b：无分块、无并发）。
        Assert.Single(client.SegmentRequests);
        Assert.Equal(3, client.SegmentRequests[0].Segments.Count);
        Assert.Equal(new[] { 0, 1, 2 }, client.SegmentRequests[0].Segments.Select(s => s.Id));

        // 渲染 + 复制到剪贴板。
        Assert.NotNull(outcome.RenderedImage);
        Assert.Single(clipboard.CopiedImages);
        Assert.Equal("已生成全文翻译图片", outcome.Message);
    }

    [Fact]
    public async Task 双语模式生成双语图片()
    {
        var client = new FakeTranslationClient
        {
            OnSegments = request => request.Segments
                .Select(s => new TranslationSegmentResult(s.Id, $"译文{s.Id}"))
                .ToList(),
        };
        var ocr = new FakeOcrEngine
        {
            Result = OcrWithLines("hello", 0.9f, ("hello", 0.1, 0.2, 0.5, 0.2)),
        };
        var clipboard = new FakeClipboard();
        var service = CreateService(client, ocr, clipboard: clipboard);

        var outcome = await service.PerformTranslationAsync(
            TestBitmaps.Solid(200, 100, RgbaColor.White),
            ScreenshotTranslationMode.BilingualImage);

        Assert.Equal(ScreenshotTranslationMode.BilingualImage, outcome.Mode);
        Assert.NotNull(outcome.RenderedImage);
        Assert.True(outcome.RenderedImage.Height > 100, "双语模式输出应比原图高。");
        Assert.Equal("已生成双语翻译图片", outcome.Message);
    }

    [Fact]
    public async Task 图像模式没有非空文字行时报missingTextBoxes()
    {
        var client = new FakeTranslationClient();
        var ocr = new FakeOcrEngine
        {
            Result = OcrWithLines("", 0.9f, ("  ", 0.1, 0.1, 0.4, 0.1)),
        };
        var service = CreateService(client, ocr);

        var error = await Assert.ThrowsAsync<ScreenshotTranslationServiceError>(() =>
            service.PerformTranslationAsync(
                TestBitmaps.Solid(200, 100, RgbaColor.White),
                ScreenshotTranslationMode.FullImage));

        Assert.Equal(ScreenshotTranslationServiceError.Code.MissingTextBoxes, error.ErrorCode);
        Assert.Empty(client.SegmentRequests);
    }

    [Fact]
    public async Task 译文缺失或为空的行被丢弃()
    {
        var client = new FakeTranslationClient
        {
            // 只返回 id 0 与 id 2；id 1 缺失、id 2 译文为空 —— 两行都应被丢弃。
            OnSegments = _ => new List<TranslationSegmentResult>
            {
                new(0, "译文零"),
                new(2, string.Empty),
            },
        };
        var ocr = new FakeOcrEngine
        {
            Result = OcrWithLines(
                "a b c",
                0.9f,
                ("a", 0.1, 0.1, 0.2, 0.1),
                ("b", 0.1, 0.3, 0.2, 0.1),
                ("c", 0.1, 0.5, 0.2, 0.1)),
        };
        var service = CreateService(client, ocr);

        var lines = await service.TranslateLinesAsync(
            ocr.Result!.Document.Lines.ToList(),
            CancellationToken.None);

        Assert.Single(lines);
        Assert.Equal("a", lines[0].SourceText);
        Assert.Equal("译文零", lines[0].TranslatedText);

        // bbox 与 confidence 原样保留。
        Assert.Equal(0.1, lines[0].BoundingBox.MinX);
        Assert.Equal(0.9f, lines[0].Confidence);
    }

    [Fact]
    public async Task 全部分段都缺失时渲染报missingTextBoxes()
    {
        var client = new FakeTranslationClient
        {
            OnSegments = _ => Array.Empty<TranslationSegmentResult>(),
        };
        var ocr = new FakeOcrEngine
        {
            Result = OcrWithLines("hello", 0.9f, ("hello", 0.1, 0.1, 0.4, 0.2)),
        };
        var service = CreateService(client, ocr);

        var error = await Assert.ThrowsAsync<ScreenshotTranslationServiceError>(() =>
            service.PerformTranslationAsync(
                TestBitmaps.Solid(200, 100, RgbaColor.White),
                ScreenshotTranslationMode.FullImage));

        Assert.Equal(ScreenshotTranslationServiceError.Code.MissingTextBoxes, error.ErrorCode);
    }

    // ---------- 配置校验与剪贴板策略 ----------

    [Fact]
    public async Task 未配置凭据时报NotConfigured()
    {
        var client = new FakeTranslationClient();
        var ocr = new FakeOcrEngine { Result = OcrWithLines("hi", 0.9f, ("hi", 0.1, 0.1, 0.4, 0.2)) };
        var service = new ScreenshotTranslationService(
            client,
            ocr,
            new FakeImageEncoder(),
            new FakeClipboard(),
            new FakeCredentials(null));

        var error = await Assert.ThrowsAsync<ScreenshotTranslationServiceError>(() =>
            service.PerformTranslationAsync(
                TestBitmaps.Solid(64, 32, RgbaColor.White),
                ScreenshotTranslationMode.TextOnly));

        Assert.Equal(ScreenshotTranslationServiceError.Code.NotConfigured, error.ErrorCode);
    }

    [Fact]
    public async Task 目标语言为空时校验失败()
    {
        var client = new FakeTranslationClient();
        var ocr = new FakeOcrEngine { Result = OcrWithLines("hi", 0.9f, ("hi", 0.1, 0.1, 0.4, 0.2)) };
        var service = CreateService(
            client,
            ocr,
            new TranslationConfiguration { TargetLanguage = "   " });

        var error = await Assert.ThrowsAsync<ScreenshotTranslationServiceError>(() =>
            service.PerformTranslationAsync(
                TestBitmaps.Solid(64, 32, RgbaColor.White),
                ScreenshotTranslationMode.TextOnly));

        Assert.Equal(ScreenshotTranslationServiceError.Code.NotConfigured, error.ErrorCode);
        Assert.Contains("目标语言", error.Message);
    }

    [Fact]
    public async Task 剪贴板被占用时不复制但保留译文()
    {
        var client = new FakeTranslationClient { OnText = _ => "文字模型译文" };
        var ocr = new FakeOcrEngine
        {
            Result = OcrWithLines("hello", 0.9f, ("hello", 0.1, 0.1, 0.4, 0.2)),
        };
        var clipboard = new FakeClipboard { Commit = false };
        var service = CreateService(client, ocr, clipboard: clipboard);

        var outcome = await service.PerformTranslationAsync(
            TestBitmaps.Solid(64, 32, RgbaColor.White),
            ScreenshotTranslationMode.TextOnly);

        Assert.False(outcome.CopiedToClipboard);
        Assert.Equal("识别期间剪贴板有变化，未自动覆盖", outcome.Message);
        Assert.Equal("文字模型译文", outcome.TranslatedText);
    }

    [Fact]
    public async Task 视觉回退把图片压到最长边2560后编码PNG()
    {
        var client = new FakeTranslationClient { OnImage = _ => "译文" };
        var ocr = new FakeOcrEngine { Result = OcrWithLines(string.Empty, 1.0f) };
        var encoder = new FakeImageEncoder();
        var service = CreateService(client, ocr, encoder: encoder);

        await service.PerformTranslationAsync(
            TestBitmaps.Solid(3000, 1000, RgbaColor.White),
            ScreenshotTranslationMode.TextOnly);

        Assert.Single(encoder.EncodedPngs);
        Assert.Equal(2560, encoder.EncodedPngs[0].Width);
        Assert.Equal(853, encoder.EncodedPngs[0].Height); // 1000 * 2560/3000 = 853.33 → 853（四舍五入）
    }
}
