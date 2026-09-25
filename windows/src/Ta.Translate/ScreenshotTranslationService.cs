using Ta.Core.Imaging;

namespace Ta.Translate;

/// <summary>
/// 截图翻译编排。
///
/// 逐行对应 Mac 版 <c>CaptureCoordinator.performTranslation</c>（:666-791）：
/// 1. 校验配置（:673）；
/// 2. 本地 OCR 取字（图像模式需要带 bbox 的行，因此翻译路径的 OCR 必须给定位信息）（:684-690）；
/// 3. OCR 失败时：仅当 mode == .textOnly &amp;&amp; usesVisionFallback 才切视觉模型，
///    否则报 localOCRUnavailableForImage（:694-715）；
/// 4. textOnly：shouldUseVision = usesVisionFallback &amp;&amp; (text.isEmpty || confidence &lt; 0.55)
///    —— ⚠️ <b>0.55 与 OCR 侧 isLowConfidence 的 0.72 是两个不同阈值，不要混</b>
///    （参考文档 §14 风险 #33）；
/// 5. 图像模式：收集非空文字行 → 空则 missingTextBoxes → 批量翻译 → 渲染 → 复制到剪贴板
///    （:755-790）；
/// 6. 批量策略：一次请求带全部文字行，无分块、无并发；译文缺失的行被丢弃；
///    保留原始 bbox 与 confidence（:63-91 的 translateLines，§9.4b）。
///
/// 本类不直接调用 HTTP —— 只依赖可注入的 <see cref="ITranslationClient"/>。
/// </summary>
public sealed class ScreenshotTranslationService
{
    private readonly ITranslationClient _client;
    private readonly ILocalOcrEngine _ocr;
    private readonly IImageEncoder _encoder;
    private readonly ITranslationClipboard _clipboard;
    private readonly ITranslationCredentialsProvider _credentials;
    private readonly TranslationConfiguration _configuration;
    private readonly TranslatedImageRenderer _renderer = new();

    /// <summary>视觉回退时把图片最长边压到的上限。对应 Mac: prepareOCRImage(maximumDimension: 2560)。</summary>
    public const int VisionMaximumDimension = 2560;

    public ScreenshotTranslationService(
        ITranslationClient client,
        ILocalOcrEngine ocr,
        IImageEncoder encoder,
        ITranslationClipboard clipboard,
        ITranslationCredentialsProvider credentials,
        TranslationConfiguration? configuration = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _ocr = ocr ?? throw new ArgumentNullException(nameof(ocr));
        _encoder = encoder ?? throw new ArgumentNullException(nameof(encoder));
        _clipboard = clipboard ?? throw new ArgumentNullException(nameof(clipboard));
        _credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));
        _configuration = configuration ?? TranslationConfiguration.Default();
    }

    /// <summary>当前配置（只读视图）。</summary>
    public TranslationConfiguration Configuration => _configuration;

    /// <summary>
    /// 校验配置。对应 Mac: validateConfiguration（:44-46）—— 实际校验的是
    /// 「有没有一套带文字模型、视觉模型和 API Key 的可用 Profile」。
    /// </summary>
    /// <exception cref="ScreenshotTranslationServiceError">未配置可用 Profile。</exception>
    public void ValidateConfiguration()
    {
        var message = _configuration.ValidationMessage;
        if (message != null)
        {
            throw ScreenshotTranslationServiceError.NotConfigured(message);
        }

        if (_credentials.GetCredentials() is null)
        {
            throw ScreenshotTranslationServiceError.NotConfigured(
                "请先到“AI 模型”配置一套带文字模型和视觉模型的服务，然后在翻译设置中选择它。");
        }
    }

    /// <summary>
    /// 纯文字翻译。对应 Mac: translateText（:48-61）。
    /// </summary>
    public async Task<string> TranslateTextAsync(string text, CancellationToken cancellationToken = default)
    {
        var credentials = RequireCredentials();
        var trimmed = text?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
        {
            throw TranslationProviderError.EmptyInput();
        }

        return await _client.TranslateTextAsync(new TranslationTextRequest
        {
            Credentials = credentials,
            Text = trimmed,
            SourceLanguage = _configuration.SourceLanguage,
            TargetLanguage = _configuration.TargetLanguage,
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 批量翻译带 bbox 的文字行。
    ///
    /// 对应 Mac: translateLines（:63-91）。策略（§9.4b）：
    /// · <b>一次请求携带全部文字行</b>，无分块、无并发、无大小限制；
    /// · 丢弃译文缺失或为空的文字行；
    /// · 保留原始 bbox 与 confidence。
    /// </summary>
    public async Task<IReadOnlyList<TranslatedOcrLine>> TranslateLinesAsync(
        IReadOnlyList<OcrTextLine> lines,
        CancellationToken cancellationToken = default)
    {
        var usable = lines
            .Where(l => !string.IsNullOrWhiteSpace(l.Text))
            .ToList();

        if (usable.Count == 0)
        {
            throw ScreenshotTranslationServiceError.MissingTextBoxes();
        }

        var credentials = RequireCredentials();
        var segments = usable
            .Select((line, index) => new TranslationSourceSegment(index, line.Text))
            .ToList();

        var translated = await _client.TranslateSegmentsAsync(new TranslationSegmentsRequest
        {
            Credentials = credentials,
            Segments = segments,
            SourceLanguage = _configuration.SourceLanguage,
            TargetLanguage = _configuration.TargetLanguage,
        }, cancellationToken).ConfigureAwait(false);

        var byId = new Dictionary<int, string>();
        foreach (var segment in translated)
        {
            byId[segment.Id] = segment.Text;
        }

        var result = new List<TranslatedOcrLine>(usable.Count);
        for (var index = 0; index < usable.Count; index++)
        {
            // 译文缺失或为空 → 丢弃该行（Mac: :82-83）。
            if (!byId.TryGetValue(index, out var translatedText) || string.IsNullOrEmpty(translatedText))
            {
                continue;
            }

            var line = usable[index];
            result.Add(new TranslatedOcrLine(line.Text, translatedText, line.BoundingBox, line.Confidence));
        }

        return result;
    }

    /// <summary>
    /// 视觉模型翻译（整图读字）。对应 Mac: translateWithVision（:93-107）——
    /// 先把最长边压到 2560 再 PNG 编码。
    /// </summary>
    public async Task<string> TranslateWithVisionAsync(RgbaBitmap image, CancellationToken cancellationToken = default)
    {
        var credentials = RequireCredentials();
        var prepared = PrepareForVision(image);
        var png = _encoder.EncodePng(prepared);

        return await _client.TranslateImageAsync(new TranslationImageRequest
        {
            Credentials = credentials,
            PngData = png,
            SourceLanguage = _configuration.SourceLanguage,
            TargetLanguage = _configuration.TargetLanguage,
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 完整编排流程。对应 Mac: CaptureCoordinator.performTranslation（:666-791）。
    /// </summary>
    /// <param name="image">本次主动框选的截图（左上原点、Y 向下）。</param>
    /// <param name="mode">由 <see cref="TranslationModeRouting"/> 决定（快捷键强制 textOnly）。</param>
    /// <param name="progress">进度提示回调（对应 Mac 的 ResultBar 过程态）。</param>
    /// <param name="jobIsLatest">
    /// 对应 Mac 的 <c>latestJobID == jobID</c>：false 表示期间有更新的作业，
    /// 剪贴板将不会被覆盖（宿主传入；本类无法自行判断）。
    /// </param>
    public async Task<TranslationOutcome> PerformTranslationAsync(
        RgbaBitmap image,
        ScreenshotTranslationMode mode,
        IProgress<TranslationProgress>? progress = null,
        CancellationToken cancellationToken = default,
        bool jobIsLatest = true)
    {
        ValidateConfiguration();

        progress?.Report(new TranslationProgress
        {
            Title = "正在本机识别待翻译文字…",
            Detail = $"目标语言：{_configuration.TargetLanguage}",
        });

        var initialChangeCount = _clipboard.ChangeCount;

        // 2. 本地 OCR 取字（图像模式需要 bbox）（Mac: :684-690）。
        OcrResult ocrResult;
        try
        {
            ocrResult = await _ocr.RecognizeAsync(image, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // 3. OCR 失败：仅 textOnly + usesVisionFallback 可切视觉模型（Mac: :694-715）。
            if (mode != ScreenshotTranslationMode.TextOnly || !_configuration.UsesVisionFallback)
            {
                throw ScreenshotTranslationServiceError.LocalOcrUnavailableForImage();
            }

            progress?.Report(new TranslationProgress
            {
                Title = "本地识别不可用，正在切换视觉模型…",
                Detail = "仅上传本次主动框选的图片",
            });

            var visionTranslated = await TranslateWithVisionAsync(image, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return await FinishTextTranslationAsync(visionTranslated, initialChangeCount, jobIsLatest, cancellationToken)
                .ConfigureAwait(false);
        }

        cancellationToken.ThrowIfCancellationRequested();

        // 4. textOnly 分支（Mac: :718-753）。
        if (mode == ScreenshotTranslationMode.TextOnly)
        {
            // ⚠️ 阈值 0.55 —— 与 OCR 侧 isLowConfidence 的 0.72 是两个不同的数。
            var shouldUseVision = _configuration.UsesVisionFallback
                && (ocrResult.Text.Length == 0 || ocrResult.Confidence < 0.55f);

            string translated;
            if (shouldUseVision)
            {
                progress?.Report(new TranslationProgress
                {
                    Title = "正在使用视觉模型识别并翻译…",
                    Detail = "仅上传本次主动框选的图片",
                });

                translated = await TranslateWithVisionAsync(image, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                if (ocrResult.Text.Length == 0)
                {
                    throw TranslationProviderError.EmptyInput();
                }

                progress?.Report(new TranslationProgress
                {
                    Title = "正在翻译文字…",
                    Detail = $"{_configuration.SourceLanguage} → {_configuration.TargetLanguage}",
                });

                translated = await TranslateTextAsync(ocrResult.Text, cancellationToken).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            return await FinishTextTranslationAsync(translated, initialChangeCount, jobIsLatest, cancellationToken)
                .ConfigureAwait(false);
        }

        // 5. 图像模式（Mac: :755-790）。
        var lines = ocrResult.Document.Lines
            .Where(l => !string.IsNullOrWhiteSpace(l.Text))
            .ToList();

        if (lines.Count == 0)
        {
            throw ScreenshotTranslationServiceError.MissingTextBoxes();
        }

        progress?.Report(new TranslationProgress
        {
            Title = $"正在批量翻译 {lines.Count} 个文字区域…",
            Detail = "原图不会发送给文字模型",
        });

        var translatedLines = await TranslateLinesAsync(lines, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        var rendered = _renderer.Render(image, translatedLines, mode, _configuration.TargetLanguage);
        var copied = _clipboard.TryCopyImage(rendered, initialChangeCount, jobIsLatest);

        var modeName = mode == ScreenshotTranslationMode.FullImage ? "全文翻译图片" : "双语翻译图片";

        return new TranslationOutcome
        {
            Mode = mode,
            RenderedImage = rendered,
            CopiedToClipboard = copied,
            Message = copied
                ? $"已生成{modeName}"
                : "已打开预览；识别期间剪贴板有变化，未自动覆盖",
        };
    }

    /// <summary>finishTextTranslation（Mac: :793-818）—— 复制译文。</summary>
    private Task<TranslationOutcome> FinishTextTranslationAsync(
        string translated,
        int initialChangeCount,
        bool jobIsLatest,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var committed = _clipboard.TryCopyText(translated, initialChangeCount, jobIsLatest);

        return Task.FromResult(new TranslationOutcome
        {
            Mode = ScreenshotTranslationMode.TextOnly,
            TranslatedText = translated,
            CopiedToClipboard = committed,
            Message = committed ? "翻译结果已复制" : "识别期间剪贴板有变化，未自动覆盖",
        });
    }

    /// <summary>
    /// requiredProfile（Mac: :134-153）—— 取不到可用凭据即抛 NotConfigured。
    /// 凭据的完整校验（文字模型/视觉模型/API Key 齐备、翻译资格）由
    /// <see cref="ITranslationCredentialsProvider"/> 实现负责（对应 Mac 的 ProfileStore）。
    /// </summary>
    private TranslationCredentials RequireCredentials()
    {
        if (_credentials.GetCredentials() is not { } credentials)
        {
            throw ScreenshotTranslationServiceError.NotConfigured(
                "请先到“AI 模型”配置一套带文字模型和视觉模型的服务，然后在翻译设置中选择它。");
        }

        return credentials;
    }

    /// <summary>
    /// 压图到视觉模型可用的尺寸。对应 Mac: prepareOCRImage（OptionalOCRPackManager.swift:716-736）。
    /// Mac 用 .high 插值（≈ 盒式平均）；这里用面积平均降采样，行为确定、可测。
    /// 长边 ≤ 2560 时原样返回。
    /// </summary>
    private static RgbaBitmap PrepareForVision(RgbaBitmap image)
    {
        var largest = Math.Max(image.Width, image.Height);
        if (VisionMaximumDimension <= 0 || largest <= VisionMaximumDimension)
        {
            return image;
        }

        var scale = (double)VisionMaximumDimension / largest;
        var width = Math.Max(1, (int)Math.Round(image.Width * scale));
        var height = Math.Max(1, (int)Math.Round(image.Height * scale));

        var result = new RgbaBitmap(width, height);
        var scaleX = (double)image.Width / width;
        var scaleY = (double)image.Height / height;

        for (var ty = 0; ty < height; ty++)
        {
            var yStart = (int)(ty * scaleY);
            var yEnd = Math.Max(yStart + 1, (int)((ty + 1) * scaleY));

            for (var tx = 0; tx < width; tx++)
            {
                var xStart = (int)(tx * scaleX);
                var xEnd = Math.Max(xStart + 1, (int)((tx + 1) * scaleX));

                long sumR = 0;
                long sumG = 0;
                long sumB = 0;
                var count = 0L;

                for (var sy = yStart; sy < yEnd && sy < image.Height; sy++)
                {
                    var row = sy * image.Stride;
                    for (var sx = xStart; sx < xEnd && sx < image.Width; sx++)
                    {
                        var i = row + (sx * RgbaBitmap.BytesPerPixel);
                        sumR += image.Pixels[i];
                        sumG += image.Pixels[i + 1];
                        sumB += image.Pixels[i + 2];
                        count++;
                    }
                }

                var di = (ty * result.Stride) + (tx * RgbaBitmap.BytesPerPixel);
                if (count == 0)
                {
                    result.Pixels[di] = 0;
                    result.Pixels[di + 1] = 0;
                    result.Pixels[di + 2] = 0;
                }
                else
                {
                    result.Pixels[di] = (byte)(sumR / count);
                    result.Pixels[di + 1] = (byte)(sumG / count);
                    result.Pixels[di + 2] = (byte)(sumB / count);
                }

                result.Pixels[di + 3] = 255;
            }
        }

        return result;
    }
}
