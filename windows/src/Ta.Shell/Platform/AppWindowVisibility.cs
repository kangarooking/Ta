using Ta.Shell.Contracts;

namespace Ta.Shell.Platform;

/// <summary>
/// Ta 自身窗口在截图期间的可见性控制。
///
/// 对应任务书 E 项：<b>截图时自动隐藏 Ta 主界面，不抢焦点、不把自身截进图。</b>
///
/// # 三道防线，缺一不可
///
///   1. <b>收起面板</b> —— 对应 Mac 版 <c>startCapture</c> 里的
///      <c>NotificationCenter.post(.taMenuBarShouldClose)</c>（AppModel.swift:170）。
///      托盘 popover 是用户点击入口时开着的，不收起必然被截进去。
///   2. <b>显示亲和性排除</b> —— <c>SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE)</c>。
///      这一条是<b>机制级</b>保证：即使某条代码路径忘了隐藏，WGC 也抓不到这些窗口。
///      对应 Mac 版的 <c>excludedWindowIDs</c>（参考文档 §5.4）。
///   3. <b>不抢焦点</b> —— 所有窗口都带 <c>WS_EX_NOACTIVATE</c>（见
///      <c>LayeredSurfaceWindow</c>），因此隐藏/显示过程都不会把前台切到 Ta。
///
/// # 为什么用引用计数
///
/// 隐藏/恢复必须严格成对，否则用户截图完 Ta 的面板会莫名消失。
/// 捕获流程有多条嵌套路径（主链路 → 覆盖层 → 编辑器的覆盖层延迟关闭），
/// 用计数保证「最后离开的那个人负责恢复」。
/// </summary>
public sealed class AppWindowVisibility : IAppWindowVisibility
{
    private readonly List<Action> _hideActions = new();
    private readonly List<Action> _restoreActions = new();
    private int _hiddenDepth;

    /// <summary>当前是否处于隐藏态。</summary>
    public bool IsHidden => _hiddenDepth > 0;

    /// <summary>注册一个窗口（面板），让它参与「截图时隐藏」。</summary>
    /// <param name="hide">隐藏该窗口的动作。</param>
    /// <param name="restore">恢复该窗口可见的动作。</param>
    public void Register(Action hide, Action restore)
    {
        ArgumentNullException.ThrowIfNull(hide);
        ArgumentNullException.ThrowIfNull(restore);

        _hideActions.Add(hide);
        _restoreActions.Add(restore);
    }

    /// <summary>
    /// 截图前调用：隐藏全部注册窗口。
    /// </summary>
    public void HideForCapture()
    {
        _hiddenDepth++;

        if (_hiddenDepth > 1)
        {
            return;
        }

        foreach (var hide in _hideActions)
        {
            TryRun(hide);
        }
    }

    /// <summary>截图后调用：恢复可见。</summary>
    public void RestoreAfterCapture()
    {
        if (_hiddenDepth == 0)
        {
            return;
        }

        _hiddenDepth--;

        if (_hiddenDepth > 0)
        {
            return;
        }

        foreach (var restore in _restoreActions)
        {
            TryRun(restore);
        }
    }

    /// <summary>
    /// 让所有注册窗口<b>永久</b>排除出屏幕捕获（应用启动时调用一次）。
    ///
    /// 与 <see cref="HideForCapture"/> 的区别：这一条不随截图流程开关，
    /// 而是持续生效 —— 即使用户手动打开主界面，它也不会被截进任何图里。
    /// 这是第二道防线，见类注释。
    /// </summary>
    public void EnableCaptureExclusion(Action exclude)
    {
        ArgumentNullException.ThrowIfNull(exclude);
        TryRun(exclude);
    }

    private static void TryRun(Action action)
    {
        try
        {
            action();
        }
        catch (Exception)
        {
            // 单个窗口的隐藏失败不该中断整个截图流程 ——
            // 截图仍然能完成，只是那一块区域可能出现 Ta 的窗口。
        }
    }
}
