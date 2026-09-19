using Ta.Core.Capture;
using Ta.Core.Imaging;
using Ta.Shell.Models;

namespace Ta.Shell.Contracts;

/// <summary>
/// 本文件集中声明「拓」Windows 应用层所依赖的全部服务接口。
///
/// 设计约束（来自移植任务书）：
///   · <b>只依赖接口</b>。各子系统（OCR / AI / 翻译 / 钉图 / 标注 / Agent 桥 / 捕获）
///     仍由其他模块并行开发，本应用层不能引用它们的具体类型。
///   · 每个接口都配一个内存假实现（见 <c>FakeServices.cs</c>），
///     使应用层可以在子系统尚未就绪时独立开发、独立跑通、独立测试。
///
/// 两个接口<b>不在此重复声明</b>，直接复用 <c>Ta.Core</c> 中已有的定义
/// （形状与语义都已就绪，重复声明只会制造两套类型）：
///   · <see cref="Ta.Core.Capture.IScreenCapture"/>  —— 屏幕捕获
///   · <see cref="Ta.Core.Imaging.IImageEncoder"/>    —— PNG / JPEG 编码
/// 同样，<c>ISettingsStore</c> 的字符串级版本已存在于 <c>Ta.HotKeys</c>；
/// 这里声明的是<b>应用层的富类型封装</b>，并提供一个适配器把两者接起来。
/// </summary>

// ─────────────────────────────────────────────────────────────────────────────
// 识别：本地 OCR
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>识别出的内容类型。对应 Mac 版 CaptureContentType。</summary>
public enum OcrContentType
{
    PlainText,
    Code,
    Table,
    QrCode,
    Formula,
    Image,
}

/// <summary>
/// 本地 OCR 识别结果。对应 Mac 版 <c>OCRResult</c>
/// （<c>AIScreenshotCore/Models/CaptureModels.swift:200-227</c>）。
/// </summary>
public sealed record OcrResult
{
    public OcrResult(
        string text,
        OcrContentType contentType = OcrContentType.PlainText,
        double confidence = 1,
        string engineDisplayName = "本地识别")
    {
        Text = text ?? string.Empty;
        ContentType = contentType;
        Confidence = confidence;
        EngineDisplayName = engineDisplayName;
    }

    public string Text { get; }
    public OcrContentType ContentType { get; }
    public double Confidence { get; }
    public string EngineDisplayName { get; }

    /// <summary>
    /// 低置信度判据：<c>confidence &lt; 0.72</c>。
    /// 逐字对齐 Mac 版 <c>OCRResult.isLowConfidence</c>（:224-226）。
    /// </summary>
    public bool IsLowConfidence => Confidence < LowConfidenceThreshold;

    /// <summary>0.72 —— Mac 版硬编码的阈值（CaptureModels.swift:225）。</summary>
    public const double LowConfidenceThreshold = 0.72;
}

/// <summary>识别请求参数。对应 Mac 版 recognize(image:languages:mergeWrappedLines:)。</summary>
public sealed record OcrRequestOptions
{
    public OcrRequestOptions(
        IReadOnlyList<string>? languages = null,
        bool mergeWrappedLines = false)
    {
        Languages = languages ?? DefaultLanguages;
        MergeWrappedLines = mergeWrappedLines;
    }

    public IReadOnlyList<string> Languages { get; }

    /// <summary>对应 UserDefaults "mergeWrappedLines"，用于合并 OCR 的换行。</summary>
    public bool MergeWrappedLines { get; }

    /// <summary>默认语言列表。对应 Mac 版 "zh-Hans,en-US"（CaptureCoordinator.swift:507）。</summary>
    public static IReadOnlyList<string> DefaultLanguages { get; } = new[] { "zh-Hans", "en-US" };
}

/// <summary>
/// 本地 OCR 服务。
///
/// 对应 Mac 版 <c>ConfiguredOCRService</c>（OCR 引擎路由 + 增强包管理）与
/// <c>VisionOCRService</c>。翻译链路的本地识别也走这个接口
/// （<c>CaptureCoordinator.swift:686</c>）。
/// </summary>
public interface IOcrService
{
    Task<OcrResult> RecognizeAsync(
        RgbaBitmap image,
        OcrRequestOptions options,
        CancellationToken cancellationToken = default);

    /// <summary>预热引擎。对应 Mac 版 prewarmIfNeeded()（AppModel.swift:73）。</summary>
    void Prewarm();

    /// <summary>引擎显示名，用于结果反馈条的 detail。</summary>
    string EngineDisplayName { get; }
}

/// <summary>多模态任务模板。对应 Mac 版 MultimodalTaskTemplate。</summary>
public enum MultimodalTask
{
    ExtractText,
    ExplainCode,
    TableMarkdown,
    FormulaLaTeX,
}

/// <summary>
/// AI 多模态识图服务。
/// 对应 Mac 版 <c>MultimodalRecognitionService</c>。
/// </summary>
public interface IMultimodalService
{
    /// <summary>识图并返回文本。<paramref name="task"/> 为空表示默认取文字。</summary>
    Task<string> RecognizeAsync(
        RgbaBitmap image,
        MultimodalTask? task = null,
        CancellationToken cancellationToken = default);

    /// <summary>是否已配置 Provider。决定「智能路由」能否走云端增强。</summary>
    bool IsConfigured { get; }

    /// <summary>当前生效的视觉模型名，用于结果反馈条 detail。</summary>
    string ActiveModelName { get; }
}

// ─────────────────────────────────────────────────────────────────────────────
// 识别：翻译
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>翻译模式。对应 Mac 版 ScreenshotTranslationMode。</summary>
public enum TranslationMode
{
    /// <summary>只翻译文字，复制纯文本结果。</summary>
    TextOnly,

    /// <summary>整图替换为译文。</summary>
    FullImage,

    /// <summary>原文 + 译文双语叠图。</summary>
    Bilingual,
}

/// <summary>
/// 截图翻译服务。
/// 对应 Mac 版 <c>ScreenshotTranslationService</c> + <c>VisionOCRService</c> 的组合职责：
/// 本地 OCR 取字 → 文字模型翻译；本地识别不可用时按设置回退视觉模型。
/// </summary>
public interface ITranslationService
{
    /// <summary>配置校验。未配置时抛异常 —— 与 Mac 版 validateConfiguration() 一致。</summary>
    void ValidateConfiguration();

    /// <summary>纯文本翻译。</summary>
    Task<string> TranslateTextAsync(
        string text,
        CancellationToken cancellationToken = default);

    /// <summary>把图片直接交给视觉模型识别并翻译（本地 OCR 不可用时的回退）。</summary>
    Task<string> TranslateImageAsync(
        RgbaBitmap image,
        CancellationToken cancellationToken = default);

    /// <summary>本地识别失败时是否允许回退到视觉模型。对应 configuration.usesVisionFallback。</summary>
    bool UsesVisionFallback { get; }

    /// <summary>目标语言显示名，如「英文」。</summary>
    string TargetLanguage { get; }

    /// <summary>当前生效的文字模型名，用于结果反馈条 detail。</summary>
    string SelectedTextModelName { get; }
}

// ─────────────────────────────────────────────────────────────────────────────
// 输入：全局快捷键
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>快捷键注册失败信息。对应 Mac 版 registrationFailedNotification 的 userInfo。</summary>
public sealed record HotKeyRegistrationFailure(string Message, bool IsConflict);

/// <summary>
/// 全局快捷键服务。
///
/// 对应 Mac 版 <c>GlobalHotKeyManager.registerDefaults(...)</c>
/// （GlobalHotKeyManager.swift:28-101）与
/// <c>HotKeyPreferences.registrationFailedNotification</c>（:100）。
///
/// 由平台层的 <c>Ta.HotKeys.GlobalHotKeyManager</c> 适配实现；
/// 这里只保留应用层真正用到的三个成员。
/// </summary>
public interface IHotKeyService
{
    /// <summary>注册六个默认快捷键；参数为「截图模式」的启动回调。</summary>
    void Register(Action<CaptureMode> handler);

    /// <summary>注册失败（含冲突后回滚）。对应 Mac 版 registrationFailedNotification。</summary>
    event EventHandler<HotKeyRegistrationFailure>? RegistrationFailed;

    /// <summary>当前生效的快捷键，供菜单栏 popover 显示键面。</summary>
    IReadOnlyDictionary<Ta.HotKeys.GlobalHotKeyAction, Ta.HotKeys.HotKeyShortcut> ActiveShortcuts { get; }

    /// <summary>快捷键被用户改绑后重新注册。对应 reloadFromPreferences()。</summary>
    void Reload();
}

// ─────────────────────────────────────────────────────────────────────────────
// 输出：标注编辑 / 钉图
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>标注编辑器内可选的三个动作。对应 Mac 版 InlineAnnotationAction。</summary>
public enum AnnotationEditAction
{
    Copy,
    Save,
    Pin,
}

/// <summary>
/// 标注编辑器（原位标注）。
/// 对应 Mac 版 <c>InlineAnnotationController</c>（CaptureCoordinator.swift:410-477）。
///
/// ⚠️ 关键时序：编辑器打开后调用方<b>必须</b>把覆盖层保留存活
/// （<c>SelectionOverlay.Dismiss()</c> 推迟到编辑器关闭之后），
/// 这样遮罩持续存在且 Ta 从不激活 —— 见移植参考文档 §5.6。
/// </summary>
public interface IAnnotationEditor
{
    Task<AnnotationEditAction?> OpenAsync(
        RgbaBitmap image,
        CaptureSelection selection,
        Func<AnnotationEditAction, RgbaBitmap, bool> actionHandler,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// 钉图控制器。
/// 对应 Mac 版 <c>PinnedImageWindowController</c>。
/// </summary>
public interface IPinController
{
    /// <summary>把图片钉在屏幕上，位置靠近原选区。</summary>
    void Pin(RgbaBitmap image, CaptureSelection near);

    /// <summary>从剪贴板生成钉图；剪贴板没有可钉内容时返回 false。</summary>
    bool PinFromClipboard();

    void HideAll();
    void ShowAll();
    void EnableInteractionForAll();

    /// <summary>恢复最近关闭的那张钉图。</summary>
    bool RestoreLastClosed();
}

// ─────────────────────────────────────────────────────────────────────────────
// Agent 桥
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Ta Agent 桥服务端。
/// 对应 Mac 版 <c>TaAgentBridgeServer.start()</c>（AppModel.swift:129-144）。
///
/// 启动失败时 Mac 版把状态文本置为「Agent Bridge 启动失败」，因此
/// <see cref="StartAsync"/> 允许抛异常，由应用层决定如何呈现。
/// </summary>
public interface IAgentBridge
{
    Task StartAsync(CancellationToken cancellationToken = default);

    Task StopAsync();
}

// ─────────────────────────────────────────────────────────────────────────────
// 设置
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// 应用层设置存储。对应 Mac 版的 <c>UserDefaults</c>。
///
/// 为什么不用 <c>Ta.HotKeys.ISettingsStore</c>：那个只有字符串级的
/// Read/Write/Delete，而应用层需要读 double（resultBarDuration）与
/// bool（mergeWrappedLines）。这里声明富类型读法，并提供
/// <c>HotKeysSettingsStoreAdapter</c> 反向适配，使两个模块可共用同一份存储。
/// </summary>
public interface ISettingsStore
{
    string? Read(string key);
    void Write(string key, string value);
    void Delete(string key);

    bool ReadBool(string key, bool fallback);
    double ReadDouble(string key, double fallback);
    int ReadInt(string key, int fallback);
    string ReadString(string key, string fallback);
}

// ─────────────────────────────────────────────────────────────────────────────
// 文件导出
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// 截图导出（落盘）。对应 Mac 版 <c>ImageExportService</c>。
///
/// 抽成接口是为了让 <c>save</c> 动作可在单测中验证而不真的写盘；
/// 编码仍走 <see cref="Ta.Core.Imaging.IImageEncoder"/>。
/// </summary>
public interface IScreenshotExporter
{
    /// <summary>保存单张；返回文件路径。用户取消时返回 null。</summary>
    Task<string?> SaveAsync(
        RgbaBitmap image,
        string suggestedBaseName,
        CancellationToken cancellationToken = default);

    /// <summary>保存长截图分段。用户取消时返回 null。</summary>
    Task<IReadOnlyList<string>?> SaveManyAsync(
        IReadOnlyList<RgbaBitmap> images,
        string suggestedBaseName,
        CancellationToken cancellationToken = default);
}

// ─────────────────────────────────────────────────────────────────────────────
// 长截图会话
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>长截图会话结果。对应 Mac 版 scrollingCaptureSession.start 的 completion。</summary>
public sealed record LongCaptureOutcome
{
    private LongCaptureOutcome(
        bool wasCancelled,
        string? failureMessage,
        IReadOnlyList<RgbaBitmap>? images,
        int acceptedFrames,
        int skippedFrames,
        int reviewedSeams)
    {
        WasCancelled = wasCancelled;
        FailureMessage = failureMessage;
        Images = images ?? Array.Empty<RgbaBitmap>();
        AcceptedFrames = acceptedFrames;
        SkippedFrames = skippedFrames;
        ReviewedSeams = reviewedSeams;
    }

    public static LongCaptureOutcome Cancelled() => new(true, null, null, 0, 0, 0);

    public static LongCaptureOutcome Failed(string message) =>
        new(false, message ?? "长截图失败", null, 0, 0, 0);

    public static LongCaptureOutcome Completed(
        IReadOnlyList<RgbaBitmap> images,
        int acceptedFrames = 0,
        int skippedFrames = 0,
        int reviewedSeams = 0) =>
        new(false, null, images, acceptedFrames, skippedFrames, reviewedSeams);

    public bool WasCancelled { get; }
    public string? FailureMessage { get; }
    public IReadOnlyList<RgbaBitmap> Images { get; }
    public int AcceptedFrames { get; }
    public int SkippedFrames { get; }
    public int ReviewedSeams { get; }
}

/// <summary>
/// 滚动长截图会话。对应 Mac 版 <c>ScrollingCaptureSessionController</c>。
/// </summary>
public interface ILongCaptureSession
{
    Task<LongCaptureOutcome> RunAsync(
        CaptureSelection selection,
        CancellationToken cancellationToken = default);
}

// ─────────────────────────────────────────────────────────────────────────────
// 应用层自己需要的两个额外接缝（非子系统）
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>鼠标指针形态。对应 Mac 版 NSCursor.crosshair.set() / .arrow.set()。</summary>
public interface IPointerCursor
{
    /// <summary>立即切十字光标。必须在覆盖层出现<b>之前</b>调用（§3.1 行为契约）。</summary>
    void ShowCrosshair();

    /// <summary>恢复默认箭头（取消或失败时）。</summary>
    void RestoreArrow();
}

/// <summary>
/// Ta 自身窗口的可见性。对应任务书「E. 主界面隐藏」：
/// 截图时自动隐藏 Ta 主界面，不抢焦点、也不把自身截进图。
/// </summary>
public interface IAppWindowVisibility
{
    /// <summary>冻结整屏<b>之前</b>调用：隐藏菜单栏 popover 与结果反馈条。</summary>
    void HideForCapture();

    /// <summary>流程结束后调用：恢复可见。</summary>
    void RestoreAfterCapture();
}
