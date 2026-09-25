using Ta.Core.Capture;
using Ta.Core.Imaging;
using Ta.Shell.Models;
using Ta.Shell.UI;

namespace Ta.Shell.Contracts;

/// <summary>
/// 选择覆盖层的抽象。
///
/// 为什么需要它：真身在 <c>Ta.Platform/Overlay/SelectionOverlay</c>（直接创建 Win32 窗口、
/// 跑自己的消息循环）。<c>CaptureCoordinator</c> 是任务书要求的「可单测纯编排」，
/// 不能依赖真实窗口 —— 于是这里抽一层，测试注入脚本化假覆盖层。
/// </summary>

/// <summary>
/// 覆盖层启动上下文。对应 Mac 版 <c>SelectionOverlayController.prepareStartContext()</c>
/// （CaptureCoordinator.swift:157）：
/// 鼠标所在屏 + 显示 ID + 源应用进程 + 吸附目标。
/// </summary>
public sealed record OverlayStartContext
{
    public OverlayStartContext(
        DisplayInfo display,
        string? sourceAppProcessName = null,
        string? sourceWindowTitle = null,
        IReadOnlyList<WindowInfo>? snapTargets = null)
    {
        Display = display;
        SourceAppProcessName = sourceAppProcessName;
        SourceWindowTitle = sourceWindowTitle;
        SnapTargets = snapTargets ?? Array.Empty<WindowInfo>();
    }

    /// <summary>鼠标当前所在的那块显示器。</summary>
    public DisplayInfo Display { get; }

    public string? SourceAppProcessName { get; }

    public string? SourceWindowTitle { get; }

    /// <summary>可吸附的窗口列表，序次即层级序。对应 Mac 版 WindowSnapService。</summary>
    public IReadOnlyList<WindowInfo> SnapTargets { get; }
}

/// <summary>一次覆盖层会话的请求参数。</summary>
public sealed record SelectionRequest
{
    public SelectionRequest(
        OverlayStartContext context,
        RgbaBitmap? frozenDisplayImage = null,
        bool showsActionToolbar = false,
        RectD? presetSelection = null)
    {
        Context = context;
        FrozenDisplayImage = frozenDisplayImage;
        ShowsActionToolbar = showsActionToolbar;
        PresetSelection = presetSelection;
    }

    public OverlayStartContext Context { get; }

    /// <summary>
    /// 冻结的整屏图。<b>null 表示只开操作栏冒烟</b>（无冻结帧），
    /// 对应 Mac 版 <c>openCaptureToolbarSmokeFixture</c>（CaptureCoordinator.swift:88-103）。
    /// </summary>
    public RgbaBitmap? FrozenDisplayImage { get; }

    /// <summary>是否显示操作栏。对应 showsActionToolbar —— 仅「开始拓取」且配置为「每次询问」时为 true。</summary>
    public bool ShowsActionToolbar { get; }

    /// <summary>预设选区，仅供 UI 冒烟测试。</summary>
    public RectD? PresetSelection { get; }
}

/// <summary>覆盖层返回结果。对应 Mac 版 begin 的 completion(selection, selectedAction)。</summary>
public readonly record struct SelectionResult
{
    /// <summary>是否取消（Esc / 右键）。取消时不得产生文件、也不得修改剪贴板。</summary>
    public bool WasCancelled { get; private init; }

    /// <summary>全局屏像素坐标下的选区。取消时为零矩形。</summary>
    public RectD Selection { get; private init; }

    /// <summary>用户在操作栏里选的动作；无操作栏时为 null。</summary>
    public CaptureQuickAction? SelectedAction { get; private init; }

    public OverlayTrigger Trigger { get; private init; }

    public static SelectionResult Cancelled() => new()
    {
        WasCancelled = true,
        Trigger = OverlayTrigger.Cancelled,
    };

    public static SelectionResult Selected(
        RectD selection,
        CaptureQuickAction? selectedAction = null,
        OverlayTrigger trigger = OverlayTrigger.Drag) => new()
    {
        Selection = selection,
        SelectedAction = selectedAction,
        Trigger = trigger,
    };
}

/// <summary>触发方式。与 Ta.Platform.Overlay.OverlayTrigger 取值一致。</summary>
public enum OverlayTrigger
{
    Drag,
    EnterKey,
    Cancelled,
}

/// <summary>
/// 选择覆盖层。
///
/// 对应 Mac 版 <c>SelectionOverlayController</c> 对外暴露的三个成员：
/// <c>prepareStartContext()</c>、<c>begin(context:frozenDisplayImage:showsActionToolbar:)</c>、
/// <c>dismiss()</c>。
/// </summary>
public interface ISelectionOverlay
{
    /// <summary>取样鼠标所在屏、前台应用与吸附目标。</summary>
    OverlayStartContext? PrepareStartContext();

    /// <summary>显示覆盖层并等待用户操作。</summary>
    Task<SelectionResult> BeginAsync(
        SelectionRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 关闭覆盖层。
    /// ⚠️ <c>edit</c> 动作下调用方必须<b>推迟</b>调用本方法，直到标注编辑器关闭
    /// （移植参考文档 §5.6：遮罩持续存在且 Ta 从不激活）。
    /// </summary>
    void Dismiss();
}

/// <summary>
/// 结果反馈条的呈现端。
///
/// 抽成接口使 <c>CaptureActionRouter</c> 不必依赖真实窗口即可被单测；
/// 真实现是 <c>ResultBarView</c>（一个非激活的分层面板）。
/// </summary>
public interface IResultBarSink
{
    void Show(ResultBarState state, ResultBarDisplayOptions options);
    void Hide();
}

/// <summary>结果反馈条的显示参数。</summary>
public readonly record struct ResultBarDisplayOptions
{
    /// <summary>是否允许自动隐藏。<c>.processing</c> 状态永远不自动隐藏。</summary>
    public bool AutoHide { get; init; }

    /// <summary>覆盖自动隐藏时长（秒）。null 表示用设置里的 resultBarDuration。</summary>
    public double? DismissalOverrideSeconds { get; init; }

    public static ResultBarDisplayOptions Auto(double? overrideSeconds = null) =>
        new() { AutoHide = true, DismissalOverrideSeconds = overrideSeconds };

    public static ResultBarDisplayOptions Sticky(double? overrideSeconds = null) =>
        new() { AutoHide = false, DismissalOverrideSeconds = overrideSeconds };
}
