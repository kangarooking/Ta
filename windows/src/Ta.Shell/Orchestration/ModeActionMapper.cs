using Ta.Shell.Models;

namespace Ta.Shell.Orchestration;

/// <summary>
/// 截图模式 → 动作 / 操作栏 / 状态文本 的映射。
///
/// 逐行对齐 Mac 版两处：
///   · <c>CaptureCoordinator.action(for:)</c>（CaptureCoordinator.swift:630-664）—— 动作映射
///   · <c>AppModel.startCapture</c>（AppModel.swift:172-179）—— 状态文本
///
/// 纯函数，不碰 UI 不碰设置存储，因此六种模式的映射可被完整单测。
/// </summary>

/// <summary>
/// 一次模式的执行计划。
/// </summary>
/// <param name="Action">
/// 该模式预设的直达动作；<c>null</c> 表示<b>不预设</b> —— 等用户在覆盖层里选
/// （<c>interactive</c> 且 postCaptureAction == choose，或 <c>long</c>）。
/// </param>
/// <param name="ShowsActionToolbar">覆盖层是否显示操作栏。</param>
/// <param name="StatusText">开始截图后托盘/菜单栏显示的状态文本。</param>
public sealed record ModePlan(
    CaptureQuickAction? Action,
    bool ShowsActionToolbar,
    string StatusText)
{
    /// <summary>是否为长截图（走独立会话，不经动作分派）。</summary>
    public bool IsLongCapture => Action is null && StatusText == ModeActionMapper.StatusTextFor(CaptureMode.Long);
}

public static class ModeActionMapper
{
    /// <summary>
    /// 六种模式各自的状态文本。逐字对齐 Mac 版 AppModel.swift:172-179。
    /// </summary>
    public static string StatusTextFor(CaptureMode mode) => mode switch
    {
        CaptureMode.Interactive => "框选后选择操作",
        CaptureMode.Intelligent => "选择需要识别的区域",
        CaptureMode.Translation => "选择需要翻译的区域",
        CaptureMode.Image => "选择要复制的区域",
        CaptureMode.Pin => "选择要钉住的区域",
        CaptureMode.Long => "从起点框到滚动区域底部",
        _ => "本地识别就绪",
    };

    /// <summary>
    /// 算出某个模式的执行计划。
    ///
    /// 对照移植参考文档 §4.1：
    /// <code>
    /// | Mode         | 显示操作栏?                        | 直接动作                      |
    /// | interactive  | 是（当 postCaptureAction == choose）| 无 → 显示工具栏                |
    /// | intelligent  | 否                                 | recognitionRoute → OCR/多模态  |
    /// | translation  | 否                                 | translateText                 |
    /// | image        | 否                                 | copyImage                     |
    /// | pin          | 否                                 | pin                           |
    /// | long         | 否                                 | 走长截图会话                    |
    /// </code>
    /// </summary>
    public static ModePlan PlanFor(CaptureMode mode, CaptureSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var statusText = StatusTextFor(mode);
        var action = ConfiguredActionFor(mode, settings);

        // 操作栏只在「有动作可让用户挑」时才显示：
        //   Mac: selectionOverlay.begin(..., showsActionToolbar: configuredAction == nil)
        //       （CaptureCoordinator.swift:180）
        // 也就是说只有 interactive + choose（或 interactive + rememberLast 且无历史）时才有操作栏。
        return new ModePlan(action, action is null, statusText);
    }

    /// <summary>
    /// 模式预设的直达动作；null 表示等用户选。
    /// 对应 Mac 版 <c>action(for:)</c>（CaptureCoordinator.swift:630-664）。
    /// </summary>
    public static CaptureQuickAction? ConfiguredActionFor(
        CaptureMode mode,
        CaptureSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        return mode switch
        {
            CaptureMode.Interactive => settings.PostCaptureAction switch
            {
                // .choose → 不预设，显示操作栏让用户挑。
                PostCaptureAction.Choose => null,

                // .recognize → 由识别路由决定（recognitionAction()，:909-918）。
                PostCaptureAction.Recognize => RecognitionAction(settings.RecognitionRoute),

                PostCaptureAction.TranslateText => CaptureQuickAction.TranslateText,
                PostCaptureAction.CopyImage => CaptureQuickAction.CopyImage,
                PostCaptureAction.Pin => CaptureQuickAction.Pin,
                PostCaptureAction.Edit => CaptureQuickAction.Edit,

                // .rememberLast → 读 lastPostCaptureQuickAction；
                // 读不到（首次运行）时保持 null，即退回显示操作栏。
                PostCaptureAction.RememberLast => settings.LastPostCaptureQuickAction,

                _ => null,
            },

            // .intelligent 的 directAction 在 Mac 版也是 .localOCR，
            // 但实际用的是 recognitionAction()（:654）—— 智能模式下按路由分流。
            CaptureMode.Intelligent => RecognitionAction(settings.RecognitionRoute),

            CaptureMode.Translation => CaptureQuickAction.TranslateText,
            CaptureMode.Image => CaptureQuickAction.CopyImage,
            CaptureMode.Pin => CaptureQuickAction.Pin,

            // 长截图不经动作分派，走独立会话。
            CaptureMode.Long => null,

            _ => null,
        };
    }

    /// <summary>
    /// 识别路由 → 具体动作。逐行对齐 Mac 版 <c>recognitionAction()</c>
    /// （CaptureCoordinator.swift:909-918）：
    /// <c>.localOCR, .smart → .localOCR</c>；<c>.multimodal → .multimodal</c>。
    ///
    /// 注意 <c>smart</c> 在这里返回 localOCR —— 「智能」是<b>本地优先</b>，
    /// 低置信度时的云端增强是在识别结果出来之后才走的确认分支
    /// （:520-568），不在模式选择阶段。
    /// </summary>
    public static CaptureQuickAction RecognitionAction(RecognitionRoute route) => route switch
    {
        RecognitionRoute.Multimodal => CaptureQuickAction.Multimodal,
        _ => CaptureQuickAction.LocalOcr,
    };

    /// <summary>
    /// 最终生效的动作。对应 Mac 版 CaptureCoordinator.swift:188：
    /// <code>let action = selectedAction ?? configuredAction ?? .copyImage</code>
    ///
    /// 优先用户的选择，其次模式预设，最后兜底复制图片。
    /// </summary>
    public static CaptureQuickAction ResolveAction(
        CaptureQuickAction? selectedAction,
        CaptureQuickAction? configuredAction) =>
        selectedAction ?? configuredAction ?? CaptureQuickAction.CopyImage;
}
