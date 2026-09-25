using System.Collections.Concurrent;
using System.Runtime.InteropServices.WindowsRuntime;
using Ta.Core.Imaging;
using Ta.OCR.Imaging;
using Ta.OCR.Models;
using Ta.OCR.Text;
using Windows.Foundation;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

namespace Ta.OCR.Engines;

/// <summary>
/// 基于 <c>Windows.Media.Ocr</c>（WinRT <c>OcrEngine</c>）的内置本地引擎。
///
/// 对应 Mac 版 <c>VisionOCRService.recognizeLegacy</c>
/// （<c>AIScreenshotCore/OCR/VisionOCRService.swift:38-110</c>）。
/// macOS 26 的 <c>RecognizeDocumentsRequest</c> 分支在 Windows 上没有对应物，直接跳过。
///
/// <b>⚠️ 三个已确认的缺口（参考文档 §14 风险 #33、#34，均已实测验证）</b>
/// <list type="number">
///   <item>
///     <b>没有逐行置信度</b>。WinRT 的 <c>OcrResult</c> 只给文本与词级矩形，
///     没有任何 score 字段（本机实测 <c>OcrResult</c> / <c>OcrLine</c> / <c>OcrWord</c> 均无 confidence 成员）。
///     因此 <see cref="OCRResult.Confidence"/> 是<b>合成常量</b>
///     <see cref="SyntheticConfidence"/>，并会打上
///     <see cref="OCRResult.ConfidenceIsSynthetic"/> = true。
///     <b>该值不可与 Mac 的真实置信度直接比较</b>，也不要拿它做精度统计。
///     选 0.85 的理由：与 DeepSeek-OCR-2 云端路径的固定 confidence 一致
///     （<c>DeepSeekOCRRecognitionService.swift:41</c>），
///     于是「本地 vs 云端」在智能路由阈值上行为相同，不会因为换平台而改变
///     参考文档 §9.11 那个确认弹窗的触发频率。
///   </item>
///   <item>
///     <b>bbox 是图像像素坐标</b>，原点左上、Y 向下；Mac 的 <c>boundingBox</c> 是归一化
///     （0..1）、原点左下。统一经 <see cref="NormalizedRect.FromImagePixels"/> 换算，
///     该函数有专门的单测锁定。
///   </item>
///   <item>
///     <b>只有词级 bbox，没有行级 bbox</b>（<c>OcrLine</c> 无 <c>BoundingRect</c>，本机实测）。
///     行 bbox 由该行所有词矩形<b>求并集</b>得到，
///     这比 Mac 的 Vision 行框略紧/略松（取决于分词），
///     会让 <c>OCRDocumentLayoutAnalyzer</c> 的分块与分行结果与 Mac 略有差异。
///   </item>
/// </list>
///
/// <b>WinRT 没有对应开关的 Mac 参数</b>（说明即可，无法复现）：
/// <list type="bullet">
///   <item><c>recognitionLevel = .accurate</c>（`:45`）—— WinRT 只有系统 OCR 一档，无 fast/accurate 之分。</item>
///   <item><c>usesLanguageCorrection = true</c>（`:46`）—— WinRT 无语言纠正开关。</item>
///   <item><c>recognitionLanguages</c>（`:48-50`）—— WinRT 引擎是<b>单语言</b>的，
///     不能像 Mac 那样一次请求里给多个候选语言；这里退化为「按顺序挑第一个能匹配上的」。</item>
///   <item><c>VNDetectBarcodesRequest</c>（`:52-54`）—— Windows.Media.Ocr 完全没有条码 API，
///     见 <see cref="OCRResult.Barcodes"/> 的说明。</item>
/// </list>
///
/// <b>线程模型</b>：WinRT 运行时类可能不是 agile 的，跨线程创建/使用 <c>OcrEngine</c>
/// 会抛 <c>RPC_E_WRONG_THREAD</c>。因此所有 WinRT 调用都派发到一个**专用 MTA 线程**，
/// 引擎实例也只在那条线程上创建与使用。
/// </summary>
public sealed class WindowsMediaOcrEngine : IOCRTextEngine
{
    /// <summary>
    /// 合成置信度。见类注释缺口 #1 —— <b>不可与 Mac 的真实置信度比较</b>。
    /// </summary>
    public const double SyntheticConfidence = 0.85;

    /// <summary>
    /// Mac 把最长边超过 2560 的图等比缩小后再识别（<c>prepareOCRImage</c>）。
    /// 本地引擎路径在 Mac 上不过这一步（Vision 直接吃 CGImage），
    /// 但 WinRT 对超大图有明显上限，故同样先缩一次，行为更接近 Mac。
    /// </summary>
    private const int MaximumDimension = Ta.OCR.Imaging.OCRImagePreparation.DefaultMaximumDimension;

    /// <summary>
    /// 识别前默认加的白边像素数（实测最优 8px：贴边文字 0 字 → 可识别；
    /// 大于 20px 反而失效；整屏无副作用）。
    /// </summary>
    private const int RecognitionPadding = 8;

    private readonly BlockingCollection<WorkItem> _queue = new();
    private readonly Thread _winrtThread;
    private readonly OcrResultAssembler _assembler = new();

    private OcrEngine? _cachedEngine;
    private string? _cachedLanguageTag;
    private bool _disposed;

    public WindowsMediaOcrEngine()
    {
        // 专用 MTA 线程承载全部 WinRT 调用。
        _winrtThread = new Thread(Pump)
        {
            IsBackground = true,
            Name = "Ta.OCR.WinRT",
        };
        _winrtThread.SetApartmentState(ApartmentState.MTA);
        _winrtThread.Start();
    }

    /// <summary>
    /// 本机是否装有任何 OCR 语言包。
    /// 对应 Mac 侧 <c>VNRecognizeTextRequest.supportedRecognitionLanguages</c> 非空这一事实。
    /// </summary>
    public bool IsAvailable
    {
        get
        {
            try
            {
                return Invoke(() => OcrEngine.AvailableRecognizerLanguages.Count > 0);
            }
            catch (Exception)
            {
                return false;
            }
        }
    }

    public Task<OCRResult> RecognizeAsync(
        RgbaBitmap image,
        OCRRecognizeOptions options,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // 先缩图：Windows 侧同样要避免把 8K 截图直接塞进识别器。
        var prepared = OCRImagePreparation.Prepare(image, MaximumDimension);

        // 小字增强：Windows 媒体 OCR 对 ~13px 中文识别率很差（实测用户反馈
        // 「取不到正确的字」），识别前把短边 < 900 的图放大 2x —— 文字特征被拉到
        // 识别器擅长的尺度，命中率显著提升。放大后的归一化坐标基于新图尺寸，
        // 行框映射自动正确（见下方 NormalizedRect.FromImagePixels 用的是 image 尺寸）。
        // ⚠️ 默认「不放大」（2026-09-19 实测实验定案，真机四组对比）：
        //   · 3200x1800 整屏（缩到 2560）：识别 1246 字、质量良好 —— 引擎中文能力没问题；
        //   · 360x140 小图：原图 16 字 > 2x 双线性 13 > 3x 11 > 4x 9（放大越多越差）；
        //   · 182x74 极小图：原图识别 7 字，**2x 双线性直接 0 字**；最近邻放大无增益。
        // 结论：双线性平滑把笔画糊开，引擎对糊字检出率暴跌 —— 原图永远是最优输入。
        // 放大能力保留（环境变量 TA_OCR_UPSCALE=2/3/4/nn2/nn3/nn4 可覆盖），仅供后续实验。
        // ✅ 默认加 8px 白边（2026-09-19 真机实验定案，这是「取字识别不到」的真凶修复）：
        //   · 用户框选时文字常被切在图像边缘 → 引擎对贴边文字检出率极低
        //     （实测：605x119 含清晰中文，原图 0 行；加 8px 白边后识别出「时任务|料库|灵感」）；
        //   · 无副作用：整屏 2560x1440 加白边前后同为 1246 字；
        //   · 有增益：160x90 小图 15 字 → 25 字。
        // 白边不宜过多（20px 以上反而 0 行），8px 是实测最优。
        var padPixels = RecognitionPadding;
        if (int.TryParse(Environment.GetEnvironmentVariable("TA_OCR_PAD"), out var overridePad))
        {
            padPixels = overridePad;
        }

        prepared = Imaging.RgbaUpscaler.PadWhitespace(prepared, padPixels);
        Diag($"加白边 {padPixels}px -> {prepared.Width}x{prepared.Height}");

        // 反相开关（诊断实验用）：TA_OCR_INVERT=1 把深底浅字转成浅底深字再识别。
        if (Environment.GetEnvironmentVariable("TA_OCR_INVERT") == "1")
        {
            prepared = Imaging.RgbaUpscaler.Invert(prepared);
            Diag("已反相（实验）");
        }

        var beforeUpscale = prepared;
        var overrideFactor = Environment.GetEnvironmentVariable("TA_OCR_UPSCALE");
        if (!string.IsNullOrWhiteSpace(overrideFactor))
        {
            prepared = overrideFactor switch
            {
                "0" => prepared,
                "2" => Imaging.RgbaUpscaler.Upscale(prepared, 2),
                "3" => Imaging.RgbaUpscaler.Upscale(prepared, 3),
                "4" => Imaging.RgbaUpscaler.Upscale(prepared, 4),
                "nn2" => Imaging.RgbaUpscaler.UpscaleNearest(prepared, 2),
                "nn3" => Imaging.RgbaUpscaler.UpscaleNearest(prepared, 3),
                "nn4" => Imaging.RgbaUpscaler.UpscaleNearest(prepared, 4),
                _ => prepared,
            };
            Diag($"上采样（实验覆盖 {overrideFactor}）{beforeUpscale.Width}x{beforeUpscale.Height} "
                 + $"-> {prepared.Width}x{prepared.Height}");
        }

        // 引擎输入图 dump（%TEMP%/ta-ocr-input-original.png / -boosted.png，覆盖式）——
        // 用来排「图本身是坏的（全黑/错位）」还是「引擎能力不足」。
        DumpPng(ReferenceEquals(beforeUpscale, prepared) ? "input" : "input-original", beforeUpscale);
        DumpPng("input-boosted", prepared);

        var completion = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var item = new WorkItem(
            () => RecognizeOnWinrtThread(prepared, options, cancellationToken),
            completion);

        if (_queue.IsAddingCompleted)
        {
            throw new ObjectDisposedException(nameof(WindowsMediaOcrEngine));
        }

        _queue.Add(item);
        return completion.Task.ContinueWith(task => (OCRResult)task.Result!, TaskScheduler.Default);
    }

    private OCRResult RecognizeOnWinrtThread(
        RgbaBitmap image,
        OCRRecognizeOptions options,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var engine = ResolveEngine(options.Languages)
            ?? throw new InvalidOperationException(
                "本机没有可用的 Windows OCR 语言包。请在「设置 → 时间和语言 → 语言和区域」里安装 OCR 支持。");

        // 词级矩形是 WinRT 唯一的位置信息；行框用并集合成（见类注释缺口 #3）。
        var lines = new List<OCRTextLine>();
        var winrtResult = RecognizeCoreAsync(engine, image, cancellationToken).GetAwaiter().GetResult();

        // 双路识别（深色界面适配）：OCR 模型对「深字浅底」明显更敏感，而深色 App /
        // IDE / 终端的浅色小字常被整片漏检（实测：同一屏整图 445 字，但截取深色区域
        // 的小条常 0 字或出乱字）。第一路结果过少时反相再识别一次，谁多取谁 ——
        // 浅底深字的正常图不受影响（第一路就识别足了，不会走第二路）。
        if (winrtResult.Lines.Count == 0)
        {
            // 深色界面（浅字深底）适配：反相后同样加白边再试。
            var inverted = Imaging.RgbaUpscaler.PadWhitespace(
                Imaging.RgbaUpscaler.Invert(image), RecognitionPadding);
            var invertedResult = RecognizeCoreAsync(engine, inverted, cancellationToken)
                .GetAwaiter().GetResult();
            if (invertedResult.Lines.Count > 0)
            {
                Diag($"反相后识别更优：{winrtResult.Lines.Count} -> {invertedResult.Lines.Count} 行");
                winrtResult = invertedResult;
                image = inverted;
            }
        }

        foreach (var winrtLine in winrtResult.Lines)
        {
            if (winrtLine.Text.Length == 0)
            {
                continue;
            }

            var box = NormalizedRect.Zero;
            var hasBox = false;
            foreach (var word in winrtLine.Words)
            {
                var wordBox = NormalizedRect.FromImagePixels(
                    word.BoundingRect.X,
                    word.BoundingRect.Y,
                    word.BoundingRect.Width,
                    word.BoundingRect.Height,
                    image.Width,
                    image.Height);

                box = hasBox ? box.Union(wordBox) : wordBox;
                hasBox = true;
            }

            lines.Add(new OCRTextLine(winrtLine.Text, SyntheticConfidence, box));
        }

        // 诊断留痕（非侵入）：引擎原始行数 vs 组装后长度 —— 一次分辨
        // 「引擎没识别到字」还是「组装环节把它丢了」。日志落 %TEMP%/ta-ocr-diag.log。
        var assembled = _assembler.Assemble(
            lines: RecognitionLineOrdering.Sort(lines),
            barcodes: [],
            mergeWrappedLines: options.MergeWrappedLines,
            languages: options.Languages,
            engine: OCREnginePreference.AppleVision,
            confidenceIsSynthetic: true);

        Diag($"图 {image.Width}x{image.Height} · 引擎行数 {winrtResult.Lines.Count} "
             + $"· 组装后 {assembled.Text.Length} 字 · 引擎={engine.RecognizerLanguage?.LanguageTag ?? "?"}");
        return assembled;
    }

    /// <summary>把引擎输入图存成 PNG（诊断用）。失败绝不影响识别主流程。</summary>
    internal static void DumpPng(string tag, RgbaBitmap image)
    {
        try
        {
            // 同时保留「固定名（最新）」与「带时间戳（最近样本）」两份 ——
            // 固定名便于按需查看，时间戳份用于事后分析真实失败样本（曾经 dump
            // 被下一次识别覆盖，导致拿不到失败现场）。
            var bytes = Imaging.PngWriter.Encode(image);
            var dir = Path.GetTempPath();
            File.WriteAllBytes(Path.Combine(dir, $"ta-ocr-{tag}.png"), bytes);
            var stamped = Path.Combine(
                dir, "ta-ocr-samples", $"{DateTime.Now:HHmmss-fff}-{tag}-{image.Width}x{image.Height}.png");
            Directory.CreateDirectory(Path.GetDirectoryName(stamped)!);
            File.WriteAllBytes(stamped, bytes);

            // 只保留最近 20 份样本，避免无限增长。
            var samples = new DirectoryInfo(Path.GetDirectoryName(stamped)!)
                .GetFiles("*.png")
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .Skip(20);
            foreach (var stale in samples)
            {
                try
                {
                    stale.Delete();
                }
                catch
                {
                }
            }
        }
        catch
        {
        }
    }

    /// <summary>诊断留痕（%TEMP%/ta-ocr-diag.log）—— 失败绝不影响识别主流程。</summary>
    internal static void Diag(string message)
    {
        try
        {
            var path = Path.Combine(Path.GetTempPath(), "ta-ocr-diag.log");
            File.AppendAllText(path,
                $"{DateTime.Now:HH:mm:ss.fff} {message}{Environment.NewLine}");
        }
        catch
        {
        }
    }

    private static async Task<OcrResult> RecognizeCoreAsync(
        OcrEngine engine,
        RgbaBitmap image,
        CancellationToken cancellationToken)
    {
        using var bitmap = CreateSoftwareBitmap(image);
        try
        {
            return await engine.RecognizeAsync(bitmap).AsTask(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
    }

    /// <summary>
    /// 把 RGBA 字节缓冲转成 WinRT 的 <c>SoftwareBitmap</c>。
    ///
    /// <c>BitmapPixelFormat.Bgra8</c> 要求 **B,G,R,A** 顺序，而 <see cref="RgbaBitmap"/>
    /// 是 R,G,B,A（对应 Mac 的 premultipliedLast），所以要交换 R/B。
    /// 位图本身不透明（截图输入 alpha 恒为 255），预乘与直通等价，无需换算。
    /// </summary>
    private static SoftwareBitmap CreateSoftwareBitmap(RgbaBitmap image)
    {
        var width = image.Width;
        var height = image.Height;

        // 交换 R/B：新建一份，避免改动调用方的位图。
        var bgra = new byte[image.Pixels.Length];
        for (var i = 0; i < image.Pixels.Length; i += RgbaBitmap.BytesPerPixel)
        {
            bgra[i] = image.Pixels[i + 2];     // B
            bgra[i + 1] = image.Pixels[i + 1]; // G
            bgra[i + 2] = image.Pixels[i];     // R
            bgra[i + 3] = image.Pixels[i + 3]; // A
        }

        var bitmap = new SoftwareBitmap(BitmapPixelFormat.Bgra8, width, height);
        bitmap.CopyFromBuffer(bgra.AsBuffer());
        return bitmap;
    }

    /// <summary>
    /// 解析引擎。对应 Mac 的 <c>automaticallyDetectsLanguage</c> + <c>recognitionLanguages</c> 组合。
    ///
    /// WinRT 只能单语言，所以：
    /// ① 语言列表为空 → <c>TryCreateFromUserProfileLanguages()</c>（近似「自动检测」）
    /// ② 否则按顺序挑第一个能匹配上的：先精确标签，再前缀（Mac 的 <c>zh-Hans</c> 在
    ///    Windows 上的实际标签是 <c>zh-Hans-CN</c>，精确匹配会失败）
    /// ③ 全都失败 → 退回 <c>TryCreateFromUserProfileLanguages()</c>，仍失败则抛异常
    /// </summary>
    private OcrEngine? ResolveEngine(IReadOnlyList<string> languages)
    {
        if (languages.Count == 0)
        {
            return GetOrCreateEngine(null);
        }

        var available = OcrEngine.AvailableRecognizerLanguages;

        foreach (var requested in languages)
        {
            if (string.IsNullOrWhiteSpace(requested))
            {
                continue;
            }

            var exact = OcrEngine.TryCreateFromLanguage(new Language(requested));
            if (exact is not null)
            {
                return CacheEngine(exact, requested);
            }

            // 前缀匹配：请求 "zh-Hans" 命中 "zh-Hans-CN"；也容忍反过来请求 "zh-Hans-CN" 命中 "zh-Hans"。
            var prefix = available.FirstOrDefault(candidate =>
                candidate.LanguageTag.StartsWith(requested, StringComparison.OrdinalIgnoreCase)
                || requested.StartsWith(candidate.LanguageTag, StringComparison.OrdinalIgnoreCase));

            if (prefix is not null)
            {
                var created = OcrEngine.TryCreateFromLanguage(prefix);
                if (created is not null)
                {
                    return CacheEngine(created, prefix.LanguageTag);
                }
            }
        }

        return GetOrCreateEngine(null);
    }

    private OcrEngine GetOrCreateEngine(string? languageTag)
    {
        if (_cachedEngine is not null && _cachedLanguageTag == languageTag)
        {
            return _cachedEngine;
        }

        var engine = languageTag is null
            ? OcrEngine.TryCreateFromUserProfileLanguages()
            : OcrEngine.TryCreateFromLanguage(new Language(languageTag));

        if (engine is null)
        {
            throw new InvalidOperationException(
                $"无法为语言 {languageTag ?? "用户默认语言"} 创建 Windows OCR 引擎。");
        }

        _cachedEngine = engine;
        _cachedLanguageTag = languageTag;
        return engine;
    }

    private OcrEngine CacheEngine(OcrEngine engine, string languageTag)
    {
        _cachedEngine = engine;
        _cachedLanguageTag = languageTag;
        return engine;
    }

    // ── 专用线程调度 ────────────────────────────────────────────────

    /// <summary>
    /// 一个待执行的工作项。<see cref="Body"/> 返回 <see cref="object"/> 以便同一队列
    /// 同时承载「识别」和「探测可用性」两种调用。
    /// </summary>
    private sealed class WorkItem
    {
        public WorkItem(Func<object?> body, TaskCompletionSource<object?> completion)
        {
            Body = body;
            Completion = completion;
        }

        public Func<object?> Body { get; }

        public TaskCompletionSource<object?> Completion { get; }
    }

    private void Pump()
    {
        foreach (var item in _queue.GetConsumingEnumerable())
        {
            try
            {
                item.Completion.TrySetResult(item.Body());
            }
            catch (OperationCanceledException)
            {
                item.Completion.TrySetCanceled();
            }
            catch (Exception error)
            {
                item.Completion.TrySetException(error);
            }
        }
    }

    /// <summary>
    /// 同步派发到 WinRT 线程并等待结果。
    /// 用 <c>task.Result</c> 取异常：TPL 会经 <c>ExceptionDispatchInfo</c> 原样重抛，
    /// 不会像 <c>GetAwaiter().GetResult()</c> 那样在 <c>ContinueWith</c> 里多包一层。
    /// </summary>
    private T Invoke<T>(Func<T> body)
    {
        var completion = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _queue.Add(new WorkItem(() => body(), completion));
        return (T)completion.Task.Result!;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _queue.CompleteAdding();
    }
}
