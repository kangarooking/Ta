using Ta.Core.Capture;

namespace Ta.Platform.Overlay;

/// <summary>
/// 覆盖层结果。
///
/// 对应 Mac 版 CaptureCoordinator 的 CaptureOutcome：
/// 取消**不产生文件、也不修改剪贴板** —— 这是产品承诺，必须保持。
/// </summary>
public readonly record struct OverlayOutcome
{
    public bool WasCancelled { get; private init; }

    /// <summary>屏幕像素坐标下的选区。取消时为零矩形。</summary>
    public RectD Selection { get; private init; }

    /// <summary>
    /// 触发方式。对应 Mac 版的几条等价路径：
    ///   · <see cref="OverlayTrigger.Drag"/> —— 拖拽框选
    ///   · <see cref="OverlayTrigger.EnterKey"/> —— Return / 小键盘 Enter（Mac 映射为 copyImage）
    ///   · <see cref="OverlayTrigger.Cancelled"/> —— Esc / 右键
    /// </summary>
    /// <summary>用户在操作栏点选的动作索引（null = 未用操作栏）。由宿主映射回动作枚举。</summary>
    public int? ToolbarActionIndex { get; private init; }

    public OverlayTrigger Trigger { get; private init; }

    public static OverlayOutcome Cancelled() => new()
    {
        WasCancelled = true,
        Trigger = OverlayTrigger.Cancelled,
    };

    public static OverlayOutcome Selected(
        RectD rect,
        OverlayTrigger trigger,
        int? toolbarActionIndex = null) => new()
    {
        Selection = rect,
        Trigger = trigger,
        ToolbarActionIndex = toolbarActionIndex,
    };

    public override string ToString() => WasCancelled
        ? "已取消"
        : $"({Selection.X:F0}, {Selection.Y:F0}) {Selection.Width:F0}×{Selection.Height:F0} [{Trigger}]";
}

public enum OverlayTrigger
{
    Drag,
    EnterKey,
    Cancelled,
}

/// <summary>覆盖层启动参数。</summary>
public sealed class OverlayOptions
{
    /// <summary>遮罩不透明度，0–255。对应 Mac 的黑色 0.42 遮罩。</summary>
    public byte DimAlpha { get; init; } = 190;

    /// <summary>
    /// 是否显示操作栏。对应 Mac: showsActionToolbar —— 只有通用截图模式为 true。
    /// 快捷键直达的模式为 false，此时 Enter 键也不被接受。
    /// </summary>
    public bool ShowsActionToolbar { get; init; }

    /// <summary>
    /// 是否排除自身出屏幕捕获。对应 Mac 的 excludedWindowIDs；
    /// Windows 用 WDA_EXCLUDEFROMCAPTURE 一行解决。
    /// </summary>
    public bool ExcludeSelfFromCapture { get; init; } = true;

    /// <summary>预设选区。仅供 UI 冒烟测试使用，正常流程为 null。</summary>
    public RectD? PresetSelection { get; init; }

    /// <summary>
    /// 操作栏按钮（标签, 动作索引）。ShowsActionToolbar 为 true 且此表非空时：
    /// 拖选完成不立即结束，而是在选区下方浮出操作栏等待点选。
    /// 动作索引由宿主（应用层）定义并负责映射回具体动作枚举。
    /// </summary>
    public IReadOnlyList<(string Label, int Action)>? ToolbarActions { get; init; }
}

/// <summary>
/// 覆盖层运行期间的可观测状态，供诊断与自动回归。
///
/// 用记录类型以便在生命周期各节点用 with 表达式派生新值，
/// 同时保留一个可变标记（钩子回调可能在任意时刻发生）。
/// </summary>
public sealed record OverlayDiagnostics
{
    /// <summary>显示覆盖层之前的前台窗口句柄。</summary>
    public IntPtr ForegroundBefore { get; init; }

    /// <summary>覆盖层显示后的前台窗口句柄。</summary>
    public IntPtr ForegroundDuring { get; init; }

    /// <summary>覆盖层关闭后的前台窗口句柄。</summary>
    public IntPtr ForegroundAfter { get; init; }

    /// <summary>低级键盘钩子是否安装成功。</summary>
    public bool KeyboardHookInstalled { get; init; }

    /// <summary>
    /// 是否曾通过钩子收到按键 —— 证明窗口未被激活也能收到输入。
    /// 由钩子回调设置，可能在任何线程发生，故用可变字段。
    /// </summary>
    public bool SawKeyViaHook { get; set; }

    /// <summary>
    /// 焦点是否被抢。true 表示全程未抢焦点 —— 这是整个覆盖层方案的核心前提，
    /// 也是唯一能自动回归验证的那一项。
    /// </summary>
    public bool FocusPreserved =>
        ForegroundDuring == ForegroundBefore && ForegroundAfter == ForegroundBefore;
}
