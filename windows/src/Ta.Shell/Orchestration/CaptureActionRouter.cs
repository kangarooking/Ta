using Ta.Shell.Contracts;
using Ta.Shell.Models;
using Ta.Shell.UI;
using Ta.Core.Capture;
using Ta.Core.Imaging;

namespace Ta.Shell.Orchestration;

/// <summary>
/// 要写入剪贴板的内容。
///
/// 图片一律编码为 PNG 字节后再提交 —— 对齐 Mac 版 ClipboardService
/// 恒用 <c>public.png</c> 类型（移植参考文档 §6.7），
/// 也避免把 <see cref="RgbaBitmap"/> 这种大对象跨到平台层去解码。
/// </summary>
public readonly record struct ClipboardPayload
{
    public ClipboardPayload(string text) => (Kind, Text, ImagePng) = (ClipboardContentKind.Text, text, null);

    public ClipboardPayload(byte[] imagePng) => (Kind, Text, ImagePng) = (ClipboardContentKind.Image, null, imagePng);

    public ClipboardContentKind Kind { get; }
    public string? Text { get; }
    public byte[]? ImagePng { get; }
}

/// <summary>剪贴板内容种类。</summary>
public enum ClipboardContentKind
{
    Image,
    Text,
}

/// <summary>
/// 剪贴板后端。由平台层实现（Win32 <c>OpenClipboard/SetClipboardData</c>）。
///
/// 与 <see cref="ClipboardCommitPolicy"/> 的分工：
///   · 本接口负责「怎么写」；
///   · <see cref="ClipboardCommitPolicy"/> 负责「该不该写」。
/// 竞态判定<b>先于</b>写入发生，因此不存在「写了又撤回」的中间态。
/// </summary>
public interface IClipboardService
{
    /// <summary>当前剪贴板序号。对应 NSPasteboard.changeCount / GetClipboardSequenceNumber。</summary>
    int ChangeCount { get; }

    /// <summary>写入内容。对应 Mac 版 ClipboardService.copyImage/copyText。</summary>
    void Write(ClipboardPayload payload);
}

/// <summary>
/// 框选完成后把 8 个 <see cref="CaptureQuickAction"/> 分派到对应子系统。
///
/// 逐行对齐 Mac 版 <c>CaptureCoordinator.process(jobID:action:selection:initialChangeCount:)</c>
/// （CaptureCoordinator.swift:261-628）。
///
/// ## 为什么单独成类
///
/// 这是整条链路里最该被单测的部分：8 个动作各自该调哪个服务、成功后给什么文案、
/// 失败后给什么文案、剪贴板竞态怎么处理。这些<b>全是纯编排</b>，
/// 只依赖接口 —— 因此可以用内存假服务完整验证，不需要真截图、真剪贴板、真窗口。
///
/// ## 编排与 UI 的边界
///
/// 本类<b>不创建任何窗口</b>。结果反馈条通过 <see cref="IResultBarSink"/> 呈现，
/// 文件导出通过 <see cref="IScreenshotExporter"/>，
/// 标注编辑器通过 <see cref="IAnnotationEditor"/>。
/// </summary>
public sealed class CaptureActionRouter
{
    private readonly IOcrService _ocr;
    private readonly IMultimodalService _multimodal;
    private readonly ITranslationService _translation;
    private readonly IAnnotationEditor _annotation;
    private readonly IPinController _pins;
    private readonly IImageEncoder _encoder;
    private readonly IClipboardService _clipboard;
    private readonly IScreenshotExporter _exporter;
    private readonly IResultBarSink? _resultBar;
    private readonly UI.ResultTextWindow? _resultText;
    private readonly ISettingsStore? _settings;
    private readonly Func<bool>? _confirmCloudEnhancement;

    public CaptureActionRouter(
        IOcrService ocr,
        IMultimodalService multimodal,
        ITranslationService translation,
        IAnnotationEditor annotation,
        IPinController pins,
        IImageEncoder encoder,
        IClipboardService clipboard,
        IScreenshotExporter exporter,
        IResultBarSink? resultBar = null,
        ISettingsStore? settings = null,
        Func<bool>? confirmCloudEnhancement = null,
        UI.ResultTextWindow? resultText = null)
    {
        _ocr = ocr ?? throw new ArgumentNullException(nameof(ocr));
        _multimodal = multimodal ?? throw new ArgumentNullException(nameof(multimodal));
        _translation = translation ?? throw new ArgumentNullException(nameof(translation));
        _annotation = annotation ?? throw new ArgumentNullException(nameof(annotation));
        _pins = pins ?? throw new ArgumentNullException(nameof(pins));
        _encoder = encoder ?? throw new ArgumentNullException(nameof(encoder));
        _clipboard = clipboard ?? throw new ArgumentNullException(nameof(clipboard));
        _exporter = exporter ?? throw new ArgumentNullException(nameof(exporter));
        _resultBar = resultBar;
        _resultText = resultText;
        _settings = settings;
        _confirmCloudEnhancement = confirmCloudEnhancement;
    }

    /// <summary>
    /// 截图开始前调用：收起结果窗 —— 置顶的结果窗会遮挡用户要截的内容，
    /// 导致截到的是窗口自己的空白（OCR 识别不出 + 截图内容错）。
    /// </summary>
    public void PrepareForCapture()
    {
        Ta.Shell.Program.Log("[resulttext] 截图前收起结果窗");
        _resultText?.Hide();
    }

    /// <summary>一次动作执行的上下文。</summary>
    public sealed record ActionRequest
    {
        public ActionRequest(
            CaptureQuickAction action,
            RgbaBitmap image,
            CaptureSelection selection,
            ClipboardCommitPolicy.ClipboardCommitTicket ticket,
            bool jobIsLatest,
            int jobId = 0)
        {
            Action = action;
            Image = image;
            Selection = selection;
            Ticket = ticket;
            JobIsLatest = jobIsLatest;
            JobId = jobId;
        }

        public CaptureQuickAction Action { get; }

        /// <summary>从冻结帧裁出的选区图。</summary>
        public RgbaBitmap Image { get; }

        public CaptureSelection Selection { get; }

        /// <summary>处理开始前记下的剪贴板序号凭据。</summary>
        public ClipboardCommitPolicy.ClipboardCommitTicket Ticket { get; }

        /// <summary>本次任务是否仍是当前任务。</summary>
        public bool JobIsLatest { get; }

        public int JobId { get; }
    }

    /// <summary>最近一次提交被拦下的原因，供测试与诊断断言。</summary>
    public ClipboardCommitPolicy.BlockReason LastBlockReason { get; private set; }
        = ClipboardCommitPolicy.BlockReason.None;

    /// <summary>
    /// 执行一个动作。永不抛异常 —— 所有失败都转成 <see cref="CaptureOutcome.Failed"/>
    /// 并呈现结果条，与 Mac 版 <c>process</c> 的 do/catch 结构一致。
    /// </summary>
    public async Task<CaptureOutcome> ExecuteAsync(
        ActionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        LastBlockReason = ClipboardCommitPolicy.BlockReason.None;

        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            return request.Action switch
            {
                CaptureQuickAction.CopyImage => await CopyImageAsync(request, cancellationToken),
                CaptureQuickAction.Pin => Pin(request),
                CaptureQuickAction.Multimodal => await MultimodalAsync(request, cancellationToken),
                CaptureQuickAction.TranslateText => await TranslateTextAsync(request, cancellationToken),
                CaptureQuickAction.Save => await SaveAsync(request, cancellationToken),
                CaptureQuickAction.Edit => await EditAsync(request, cancellationToken),
                CaptureQuickAction.Beautify => Beautify(),
                // localOCR 是兜底分支：Mac 版把它写在 if 链的最后（:494-616）。
                _ => await LocalOcrAsync(request, cancellationToken),
            };
        }
        catch (OperationCanceledException)
        {
            // 对应 Mac 版 :617-619 —— 取消时收起结果条/结果窗，不做任何副作用。
            _resultBar?.Hide();
            _resultText?.Close();
            return CaptureOutcome.Cancelled();
        }
        catch (Exception error)
        {
            // 兜底。对应 Mac 版 :620-626。
            Ta.Shell.Program.Log($"动作失败: {error.GetType().Name}: {error.Message}");
            _resultText?.Show("处理失败", "未预期的错误", error.Message);
            Show(ResultBarKind.Failure, "截图失败", error.Message, autoHide: false);
            return CaptureOutcome.Failed("截图失败");
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // copyImage —— 始终复制原图。对应 Mac 版 :279-295
    // ─────────────────────────────────────────────────────────────────────────
    private async Task<CaptureOutcome> CopyImageAsync(
        ActionRequest request,
        CancellationToken cancellationToken)
    {
        if (!await CommitImageAsync(request, cancellationToken))
        {
            return ClipboardChanged();
        }

        Show(ResultBarKind.Success, "已复制图片",
            $"{request.Image.Width} × {request.Image.Height}", autoHide: true);
        return CaptureOutcome.Completed("已复制图片");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // pin —— 钉图。对应 Mac 版 :297-309
    // ─────────────────────────────────────────────────────────────────────────
    private CaptureOutcome Pin(ActionRequest request)
    {
        _pins.Pin(request.Image, request.Selection);

        Show(ResultBarKind.Success, "已钉在屏幕上",
            "拖动移动 · 滚轮缩放 · 双击关闭", autoHide: true);
        return CaptureOutcome.Completed("已钉图");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // multimodal —— AI 识图。对应 Mac 版 :311-350
    // ─────────────────────────────────────────────────────────────────────────
    private async Task<CaptureOutcome> MultimodalAsync(
        ActionRequest request,
        CancellationToken cancellationToken)
    {
        Show(ResultBarKind.Processing, "正在调用多模态模型…",
            "仅上传本次主动框选的图片", autoHide: false);

        // 动作一开始就弹结果窗（等待态），结果出来后更新 —— 用户实测要求
        // 「结束框选后等待框立马出来」。
        _resultText?.ShowProcessing(
            "AI 识图", "正在调用多模态模型…");

        string text;
        try
        {
            text = await _multimodal.RecognizeAsync(request.Image, null, cancellationToken);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            Ta.Shell.Program.Log($"AI 识图失败: {error.GetType().Name}: {error.Message}");
            _resultText?.Show("AI 识图失败", _multimodal.ActiveModelName, error.Message);
            Show(ResultBarKind.Failure, "AI 识图失败", error.Message, autoHide: false);
            return CaptureOutcome.Failed("AI 识图失败");
        }

        cancellationToken.ThrowIfCancellationRequested();

        if (!await CommitTextAsync(text, request, cancellationToken))
        {
            return ClipboardChanged();
        }

        // 结果窗：把识别文字直接展示给用户（此前只有剪贴板 + 一闪而过的通知条，
        // 实测反馈「结果不返回给我」）。剪贴板行为保留。
        _resultText?.Show(
            "AI 识图结果",
            $"{_multimodal.ActiveModelName} · {text.Length} 字 · 已复制到剪贴板",
            text);
        Show(ResultBarKind.Success, "AI 识图结果已复制",
            $"{text.Length} 个字符 · {_multimodal.ActiveModelName}", autoHide: true);
        return CaptureOutcome.Completed("AI 识图结果已复制");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // translateText —— 识别 + 翻译 + 复制文字
    // 对应 Mac 版 :352-380 + performTranslation(:666-791) + finishTextTranslation(:793-818)
    // ─────────────────────────────────────────────────────────────────────────
    private async Task<CaptureOutcome> TranslateTextAsync(
        ActionRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            _translation.ValidateConfiguration();
        }
        catch (Exception error)
        {
            Ta.Shell.Program.Log($"截图翻译失败: {error.GetType().Name}: {error.Message}");
            _resultText?.Show("截图翻译失败", _translation.TargetLanguage, error.Message);
            Show(ResultBarKind.Failure, "截图翻译失败", error.Message, autoHide: false);
            return CaptureOutcome.Failed("截图翻译失败");
        }

        Show(ResultBarKind.Processing, "正在本机识别待翻译文字…",
            $"目标语言：{_translation.TargetLanguage}", autoHide: false);

        _resultText?.ShowProcessing(
            "截图翻译", $"目标语言：{_translation.TargetLanguage} · 正在识别文字…");

        string translated;
        string? sourceText;
        try
        {
            (sourceText, translated) = await TranslateTextPipelineAsync(request, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception error)
        {
            Ta.Shell.Program.Log($"截图翻译失败: {error.GetType().Name}: {error.Message}");
            Show(ResultBarKind.Failure, "截图翻译失败", error.Message, autoHide: false);
            return CaptureOutcome.Failed("截图翻译失败");
        }

        cancellationToken.ThrowIfCancellationRequested();

        if (!await CommitTextAsync(translated, request, cancellationToken))
        {
            return ClipboardChanged();
        }

        // 对应 Mac 版 finishTextTranslation(:809-817)。
        // 结果窗：原文 + 译文一起展示（视觉回退路径无原文，只展示译文）。
        var body = sourceText is null
            ? translated
            : sourceText + "\n\n" + new string('─', 28) + "\n\n" + translated;
        _resultText?.Show(
            "翻译结果 → " + _translation.TargetLanguage,
            $"{translated.Length} 字 · 已复制到剪贴板",
            body);
        Show(ResultBarKind.Success, "翻译结果已复制",
            $"{translated.Length} 个字符 · {_translation.TargetLanguage}", autoHide: true);
        return CaptureOutcome.Completed("翻译结果已复制");
    }

    /// <summary>
    /// 文字翻译链路：本地 OCR 取字 → 文字模型翻译；本地不可用或置信度低时回退视觉模型。
    /// 对应 Mac 版 <c>performTranslation</c> 的 textOnly 分支（:684-752）。
    /// </summary>
    private async Task<(string? Source, string Translated)> TranslateTextPipelineAsync(
        ActionRequest request,
        CancellationToken cancellationToken)
    {
        OcrResult ocrResult;
        try
        {
            // 翻译链路用「空语言列表 + 不合并换行」—— 逐字对齐 Mac 版 :686-690。
            ocrResult = await _ocr.RecognizeAsync(
                request.Image,
                new OcrRequestOptions(Array.Empty<string>(), mergeWrappedLines: false),
                cancellationToken);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            // 本地识别不可用：仅当允许视觉回退时切换，否则如实抛错（:694-696）。
            if (!_translation.UsesVisionFallback)
            {
                throw;
            }

            Show(ResultBarKind.Processing, "本地识别不可用，正在切换视觉模型…",
                "仅上传本次主动框选的图片", autoHide: false);
            _resultText?.ShowProcessing("截图翻译", "本地识别不可用，正在切换视觉模型…");
            return (null, await _translation.TranslateImageAsync(request.Image, cancellationToken));
        }

        cancellationToken.ThrowIfCancellationRequested();

        // 空结果或置信度 < 0.55 时走视觉模型（:718-731）。0.55 是 Mac 版硬编码值。
        const double visionFallbackConfidence = 0.55;
        var shouldUseVision = _translation.UsesVisionFallback
            && (ocrResult.Text.Length == 0 || ocrResult.Confidence < visionFallbackConfidence);

        if (shouldUseVision)
        {
            Show(ResultBarKind.Processing, "正在使用视觉模型识别并翻译…",
                "仅上传本次主动框选的图片", autoHide: false);
            _resultText?.ShowProcessing("截图翻译", "正在使用视觉模型识别并翻译…");
            return (null, await _translation.TranslateImageAsync(request.Image, cancellationToken));
        }

        if (ocrResult.Text.Length == 0)
        {
            throw new InvalidOperationException("没有识别到可翻译的文字。");
        }

        Show(ResultBarKind.Processing, "正在翻译文字…",
            $"使用 {_translation.SelectedTextModelName}", autoHide: false);
        _resultText?.ShowProcessing("截图翻译", $"正在翻译…（{_translation.SelectedTextModelName}）");
        var translated = await _translation.TranslateTextAsync(ocrResult.Text, cancellationToken);
        return (ocrResult.Text, translated);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // save —— 保存到磁盘。对应 Mac 版 :382-406
    // ─────────────────────────────────────────────────────────────────────────
    private async Task<CaptureOutcome> SaveAsync(
        ActionRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var path = await _exporter.SaveAsync(
                request.Image,
                "AI-Screenshot.png",
                cancellationToken);

            if (path is null)
            {
                // 用户取消保存对话框 —— 这不是错误。
                return CaptureOutcome.Cancelled();
            }

            Show(ResultBarKind.Success, "截图已保存",
                Path.GetFileName(path), autoHide: true);
            return CaptureOutcome.Completed("截图已保存");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception error)
        {
            Ta.Shell.Program.Log($"保存失败: {error.GetType().FullName}: {error.Message}");
            Show(ResultBarKind.Failure, "保存失败", error.Message, autoHide: false);
            return CaptureOutcome.Failed("保存失败");
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // edit —— 原位标注。对应 Mac 版 :408-479
    //
    // ⚠️ 覆盖层的存活由调用方（CaptureCoordinator）负责：edit 路径下
    //    ISelectionOverlay.Dismiss() 必须推迟到本方法返回之后（参考文档 §5.6）。
    // ─────────────────────────────────────────────────────────────────────────
    private async Task<CaptureOutcome> EditAsync(
        ActionRequest request,
        CancellationToken cancellationToken)
    {
        string? savedFilename = null;

        var outcome = await _annotation.OpenAsync(
            request.Image,
            request.Selection,
            (inlineAction, rendered) =>
            {
                switch (inlineAction)
                {
                    case AnnotationEditAction.Copy:
                        // 对齐 Mac 版 :413-421 —— 用**提交时刻**的剪贴板序号，
                        // 因为用户在编辑器里可能已经花了几十秒。
                        return Commit(
                            new ClipboardPayload(_encoder.EncodePng(rendered)),
                            new ClipboardCommitPolicy.ClipboardCommitTicket(_clipboard.ChangeCount),
                            request.JobIsLatest);

                    case AnnotationEditAction.Save:
                        try
                        {
                            var saved = _exporter
                                .SaveAsync(rendered, "AI-Annotated.png", cancellationToken)
                                .GetAwaiter().GetResult();

                            if (saved is null)
                            {
                                return false;
                            }

                            savedFilename = Path.GetFileName(saved);
                            return true;
                        }
                        catch (Exception error)
                        {
                            Show(ResultBarKind.Failure, "保存失败", error.Message, autoHide: false);
                            return false;
                        }

                    case AnnotationEditAction.Pin:
                        _pins.Pin(rendered, request.Selection);
                        return true;

                    default:
                        return false;
                }
            },
            cancellationToken);

        switch (outcome)
        {
            case AnnotationEditAction.Copy:
                Show(ResultBarKind.Success, "标注图片已复制",
                    "可直接粘贴到微信或文档", autoHide: true);
                return CaptureOutcome.Completed("标注图片已复制");

            case AnnotationEditAction.Save:
                Show(ResultBarKind.Success, "标注图片已保存",
                    savedFilename ?? "已保存到所选位置", autoHide: true);
                return CaptureOutcome.Completed("标注图片已保存");

            case AnnotationEditAction.Pin:
                Show(ResultBarKind.Success, "标注图片已钉住",
                    "拖动移动 · 滚轮缩放 · 双击关闭", autoHide: true);
                return CaptureOutcome.Completed("标注图片已钉住");

            default:
                // 用户直接关掉编辑器（Mac 版 :473-474 的 case nil）。
                return CaptureOutcome.Cancelled();
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // beautify —— AI 美化：macOS 版**未实现**，必须如实标注
    // 对应 Mac 版 :481-492
    //
    // ⚠️ 与 Mac 版的一处**刻意分歧**：
    //    Mac 版返回 .completed("美化入口已预留")，托盘状态会显示成正向结果。
    //    本移植返回 CaptureOutcome.NotAvailable，并把结果条标为 warning「开发中」。
    //    任务书明确要求「绝不谎报成功」—— 一个未实现的功能不能报成成功。
    // ─────────────────────────────────────────────────────────────────────────
    private CaptureOutcome Beautify()
    {
        Show(ResultBarKind.Warning,
            "AI 美化 · 开发中",
            "该功能尚未实现，当前版本无法美化图片",
            autoHide: true);

        // 不写剪贴板、不调任何识别服务 —— 未实现就是未实现。
        return CaptureOutcome.NotAvailable("AI 美化 · 开发中（尚未实现）");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // localOCR —— 本地 OCR（Mac 版 if 链的兜底分支）。对应 Mac 版 :494-616
    // ─────────────────────────────────────────────────────────────────────────
    private async Task<CaptureOutcome> LocalOcrAsync(
        ActionRequest request,
        CancellationToken cancellationToken)
    {
        var settings = CaptureSettings.Load(RequireSettings());

        // deepSeekOCR2 是「上传到你配置的服务」，其余都是纯本地 —— 文案必须区分。
        var isDeepSeek = string.Equals(settings.OcrEngine, "deepSeekOCR2", StringComparison.Ordinal);

        // 对应 Mac 版 :497-504。
        Show(ResultBarKind.Processing,
            isDeepSeek ? "正在使用 DeepSeek-OCR-2 识别…" : "正在本地识别文字…",
            isDeepSeek ? "截图将发送到你配置的 OCR 服务" : "图片不会上传",
            autoHide: false);

        _resultText?.ShowProcessing(
            "取字", isDeepSeek ? "正在使用 DeepSeek-OCR-2 识别…" : "正在本地识别文字…");

        OcrResult result;
        try
        {
            result = await _ocr.RecognizeAsync(
                request.Image,
                new OcrRequestOptions(settings.RecognitionLanguages, settings.MergeWrappedLines),
                cancellationToken);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            _resultText?.Show("取字失败", "本地识别", error.Message);
            Show(ResultBarKind.Failure, "本地识别失败", error.Message, autoHide: false);
            return CaptureOutcome.Failed("本地识别失败");
        }

        cancellationToken.ThrowIfCancellationRequested();

        // ── 智能路由的云端增强分支（:520-568）────────────────────────────────
        // 四个条件同时满足才走：路由为 smart、本地置信度低、多模态已配置、**用户确认过**。
        if (settings.RecognitionRoute == RecognitionRoute.Smart
            && result.IsLowConfidence
            && _multimodal.IsConfigured
            && (_confirmCloudEnhancement?.Invoke() ?? false))
        {
            Show(ResultBarKind.Processing, "正在进行云端增强…",
                "已按你的确认上传本次选区", autoHide: false);

            try
            {
                var enhanced = await _multimodal.RecognizeAsync(
                    request.Image,
                    SuggestedTaskFor(result.ContentType),
                    cancellationToken);

                cancellationToken.ThrowIfCancellationRequested();

                if (!await CommitTextAsync(enhanced, request, cancellationToken))
                {
                    return ClipboardChanged();
                }

                _resultText?.Show(
                    "取字结果（AI 增强）",
                    $"本地置信度 {Percent(result.Confidence)}% · 已复制到剪贴板",
                    enhanced);
                Show(ResultBarKind.Success, "AI 增强结果已复制",
                    $"本地置信度 {Percent(result.Confidence)}% · 已经你确认后上传", autoHide: true);
                return CaptureOutcome.Completed("AI 增强结果已复制");
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                // 云端失败不致命 —— 继续用本地结果，并如实说明（:559-568）。
                Show(ResultBarKind.Warning, "云端增强失败，继续使用本地结果",
                    error.Message, autoHide: true);
            }
        }

        // ── 没识别到文字 → 安全回退为复制 PNG（:571-587）────────────────────
        if (result.Text.Length == 0)
        {
            if (!await CommitImageAsync(request, cancellationToken))
            {
                return ClipboardChanged();
            }

            // ⚠️ 结果窗必须同步给终点反馈 —— 漏了这一步窗口会永远停在「正在处理」
            // 等待态（实测反馈：「一直没取到字」，日志 0.2s 就返回空但窗无变化）。
            _resultText?.Show(
                "未发现文字",
                $"{result.EngineDisplayName} · 已改为复制图片",
                "这张图里没有识别到文字。\n\n可能的原因：\n"
                + "· 选区内文字太小或被缩放模糊\n"
                + "· 选中了纯图形/空白区域\n\n"
                + "已自动改为复制图片，可直接粘贴使用。");
            Show(ResultBarKind.Warning, "未发现文字，已复制图片", null, autoHide: true);
            return CaptureOutcome.Completed("未发现文字，已复制图片");
        }

        if (!await CommitTextAsync(result.Text, request, cancellationToken))
        {
            // 提交被拦（剪贴板已被外部改动）—— 结果窗也要收口，不能停在等待态。
            _resultText?.Show("取字结果", "剪贴板已被外部改动，未写入", result.Text);
            return ClipboardChanged();
        }

        // ── 正常成功（:599-616）────────────────────────────────────────────
        var kind = result.IsLowConfidence
            ? ResultBarKind.Warning
            : ResultBarKind.Success;

        var title = result.IsLowConfidence
            ? "已复制，部分文字可能有误"
            : $"已复制{TypeLabelFor(result.ContentType)}";

        _resultText?.Show(
            "取字结果",
            $"{result.Text.Length} 字 · {result.EngineDisplayName} · 已复制到剪贴板",
            result.Text);
        Show(kind, title,
            $"{result.Text.Length} 个字符 · {result.EngineDisplayName}", autoHide: true);
        return CaptureOutcome.Completed(title);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 剪贴板提交：判定在写入之前，判定不通过就一次都不写
    // ─────────────────────────────────────────────────────────────────────────

    private Task<bool> CommitImageAsync(
        ActionRequest request,
        CancellationToken cancellationToken) =>
        CommitAsync(
            new ClipboardPayload(_encoder.EncodePng(request.Image)),
            request.Ticket,
            request.JobIsLatest,
            cancellationToken);

    private Task<bool> CommitTextAsync(
        string text,
        ActionRequest request,
        CancellationToken cancellationToken) =>
        CommitAsync(
            new ClipboardPayload(text),
            request.Ticket,
            request.JobIsLatest,
            cancellationToken);

    /// <summary>
    /// 竞态判定的唯一入口。
    /// </summary>
    /// <returns>true = 已写入剪贴板；false = 被拦下，调用方必须转成提示。</returns>
    private Task<bool> CommitAsync(
        ClipboardPayload payload,
        ClipboardCommitPolicy.ClipboardCommitTicket ticket,
        bool jobIsLatest,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var decision = ClipboardCommitPolicy.Evaluate(
            _clipboard.ChangeCount,
            ticket,
            jobIsLatest);

        if (!decision.ShouldCommit)
        {
            // 不覆盖用户的剪贴板 —— 这里刻意**不写**。
            LastBlockReason = decision.Reason;
            return Task.FromResult(false);
        }

        LastBlockReason = ClipboardCommitPolicy.BlockReason.None;
        _clipboard.Write(payload);
        return Task.FromResult(true);
    }

    /// <summary>标注编辑器内的同步提交（无取消令牌参与）。</summary>
    private bool Commit(
        ClipboardPayload payload,
        ClipboardCommitPolicy.ClipboardCommitTicket ticket,
        bool jobIsLatest)
    {
        var decision = ClipboardCommitPolicy.Evaluate(
            _clipboard.ChangeCount,
            ticket,
            jobIsLatest);

        if (!decision.ShouldCommit)
        {
            LastBlockReason = decision.Reason;
            return false;
        }

        _clipboard.Write(payload);
        return true;
    }

    /// <summary>剪贴板在处理期间被改动的标准提示。对应 Mac 版 showClipboardChanged(:939-949)。</summary>
    private CaptureOutcome ClipboardChanged()
    {
        Show(ResultBarKind.Warning,
            ClipboardCommitPolicy.ClipboardChangedTitle,
            ClipboardCommitPolicy.ClipboardChangedDetail,
            autoHide: false);
        return CaptureOutcome.Completed(ClipboardCommitPolicy.ClipboardChangedStatus);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 辅助
    // ─────────────────────────────────────────────────────────────────────────

    private ISettingsStore RequireSettings() =>
        _settings ?? throw new InvalidOperationException(
            "本地 OCR 动作需要 ISettingsStore（读取识别语言、OCR 引擎与识别路由）。");

    /// <summary>内容类型 → 中文标签。对应 Mac 版 :600-607。</summary>
    private static string TypeLabelFor(OcrContentType type) => type switch
    {
        OcrContentType.PlainText => "文本",
        OcrContentType.Code => "代码",
        OcrContentType.Table => "表格",
        OcrContentType.QrCode => "链接",
        OcrContentType.Formula => "公式",
        _ => "图片",
    };

    /// <summary>内容类型 → 建议的多模态任务。对应 Mac 版 suggestedTask(for:)（:930-937）。</summary>
    private static MultimodalTask SuggestedTaskFor(OcrContentType type) => type switch
    {
        OcrContentType.Code => MultimodalTask.ExplainCode,
        OcrContentType.Table => MultimodalTask.TableMarkdown,
        OcrContentType.Formula => MultimodalTask.FormulaLaTeX,
        _ => MultimodalTask.ExtractText,
    };

    private static int Percent(double confidence) => (int)Math.Round(confidence * 100);

    private void Show(
        ResultBarKind kind,
        string title,
        string? detail,
        bool autoHide,
        double? overrideSeconds = null)
    {
        _resultBar?.Show(
            new ResultBarState(kind, title, detail),
            autoHide
                ? ResultBarDisplayOptions.Auto(overrideSeconds)
                : ResultBarDisplayOptions.Sticky(overrideSeconds));
    }
}
