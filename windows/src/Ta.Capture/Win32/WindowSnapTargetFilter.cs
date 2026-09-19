using Ta.Core.Capture;

namespace Ta.Capture.Win32;

/// <summary>
/// 一个窗口的原始观测结果，由 Win32 枚举产生。
///
/// 存在意义：把「Win32 怎么取这些字段」和「哪些窗口算有效吸附目标」彻底分开。
/// <see cref="WindowSnapTargetFilter"/> 只接受本类型的实例，
/// 因此单测可以凭空构造一份假列表，不必真的开窗口 —— 这是验收条件 #1 的核心。
/// </summary>
/// <param name="WindowHandle">HWND。</param>
/// <param name="ProcessId">所属进程。</param>
/// <param name="ProcessImagePath">映像全路径。对应 Mac 的 bundleIdentifier，须保持稳定字面量。</param>
/// <param name="Title">窗口标题，可为 null。</param>
/// <param name="Frame">DWMWA_EXTENDED_FRAME_BOUNDS，全局屏坐标，不含阴影。对应 kCGWindowBounds。</param>
/// <param name="IsVisible">IsWindowVisible。对应 Mac 的 .optionOnScreenOnly。</param>
/// <param name="IsToolWindow">WS_EX_TOOLWINDOW。工具窗口不进吸附列表。</param>
/// <param name="IsCloaked">DWMWA_CLOAKED != 0。对应 Mac 的 kCGWindowLayer >= 0。</param>
/// <param name="Alpha">逐窗口透明度，范围 [0,1]。未分层窗口为 1。</param>
/// <param name="EnumerationOrder">EnumWindows 的返回序次（前→后）。对应 Mac 的 zOrder = 枚举索引。</param>
public sealed record RawWindowObservation(
    IntPtr WindowHandle,
    int ProcessId,
    string? ProcessImagePath,
    string? Title,
    RectD Frame,
    bool IsVisible,
    bool IsToolWindow,
    bool IsCloaked,
    double Alpha,
    int EnumerationOrder);

/// <summary>
/// 一个吸附目标。
/// 对应 Mac 版 WindowSnapTarget（WindowSnapService.swift:4-8）。
/// </summary>
public sealed record WindowSnapTarget(IntPtr WindowHandle, RectD Frame, int ZOrder);

/// <summary>
/// 「快照时前台进程」判定。
///
/// 逐行对应 Mac 版 WindowSnapSnapshotPolicy.includes（WindowSnapService.swift:21-26）。
/// </summary>
public static class WindowSnapSnapshotPolicy
{
    /// <summary>
    /// 只有前台进程拥有的窗口才进吸附列表。
    /// <para>
    /// ⚠️ 关键：Mac 版在 <c>frontmostProcessID</c> 为 nil 时<b>返回 true</b>（即放行全部）。
    /// 这是有意的 —— 拿不到前台进程时不能把所有窗口都过滤掉，宁可放宽。
    /// 参考文档 §14 风险 #27 提醒 Windows 上这个信号更弱（UWP/Electron 辅助进程/提权窗口
    /// 可能不可见），本实现保留同样的放宽语义。
    /// </para>
    /// </summary>
    public static bool Includes(int ownerProcessId, int? frontmostProcessId) =>
        frontmostProcessId is null || ownerProcessId == frontmostProcessId;
}

/// <summary>
/// 窗口枚举过滤。
///
/// 逐条复现 Mac 版 WindowSnapService.targets（WindowSnapService.swift:30-72）的每个条件，
/// 一一对应关系见 <see cref="WindowSnapTargetFilter.Targets"/> 内的注释与参考文档 §5.3。
///
/// 本类是**纯函数**：不吃任何 Win32，因此可完整单测（验收条件 #1）。
/// </summary>
public static class WindowSnapTargetFilter
{
    /// <summary>窗口最小宽度。对应 Mac: quartzFrame.width >= 24。</summary>
    public const int MinimumWidth = 24;

    /// <summary>窗口最小高度。对应 Mac: quartzFrame.height >= 16。</summary>
    public const int MinimumHeight = 16;

    /// <summary>最小不透明度。对应 Mac: alpha > 0.01。</summary>
    public const double MinimumAlpha = 0.01;

    /// <summary>
    /// 单窗口的「属性级」过滤：与具体屏幕无关的那几条。
    ///
    /// 对应 Mac 版 <c>guard</c> 序列（WindowSnapService.swift:40-56）：
    /// - <see cref="RawWindowObservation.IsVisible"/> ← Mac <c>.optionOnScreenOnly</c>
    /// - <see cref="RawWindowObservation.IsToolWindow"/> ← 工具窗口，等价于 Mac 的 layer >= 0 之外
    ///   的「非桌面元素」剔除（Mac 没有直接的 toolwindow 概念，但 CGWindowList 默认就排除了
    ///   面板类/桌面类图层，Windows 上必须显式剔除，否则托盘弹窗会污染吸附列表）
    /// - <see cref="RawWindowObservation.IsCloaked"/> ← Mac <c>layer >= 0</c>
    /// - <see cref="WindowSnapSnapshotPolicy.Includes"/> ← owner PID == frontmost PID
    /// - alpha / 尺寸
    /// </summary>
    public static bool PassesFilters(RawWindowObservation window, int? frontmostProcessId)
    {
        ArgumentNullException.ThrowIfNull(window);

        if (!window.IsVisible)
        {
            return false;
        }

        if (window.IsToolWindow)
        {
            return false;
        }

        if (window.IsCloaked)
        {
            return false;
        }

        if (!WindowSnapSnapshotPolicy.Includes(window.ProcessId, frontmostProcessId))
        {
            return false;
        }

        if (!(window.Alpha > MinimumAlpha))
        {
            return false;
        }

        // 尺寸检查做两遍：先查原始矩形，再查与屏幕相交后 —— 与 Mac 完全一致。
        if (window.Frame.Width < MinimumWidth || window.Frame.Height < MinimumHeight)
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// 在指定屏上生成吸附目标列表。
    ///
    /// 逐行对应 Mac 版 WindowSnapService.targets(on:frontmostProcessID:)（:30-72）：
    /// <code>
    /// guard layer >= 0                       → IsCloaked == false
    /// guard WindowSnapSnapshotPolicy.includes → PassesFilters
    /// guard alpha > 0.01                     → PassesFilters
    /// guard quartzFrame.width >= 24 && .height >= 16 → PassesFilters
    /// let appKitFrame = …intersection(screen.frame)
    /// guard appKitFrame.width >= 24 && .height >= 16 → 下面再做一次
    /// return WindowSnapTarget(frame: appKitFrame.integral, zOrder: index)
    /// </code>
    /// 唯一删除的是 Quartz→AppKit 的那次 Y 翻转（:60），Windows 上两边同为左上原点、Y 向下。
    /// </summary>
    public static IReadOnlyList<WindowSnapTarget> Targets(
        IReadOnlyList<RawWindowObservation> windows,
        int? frontmostProcessId,
        RectD screenFrame)
    {
        ArgumentNullException.ThrowIfNull(windows);

        var result = new List<WindowSnapTarget>();

        foreach (var window in windows)
        {
            if (!PassesFilters(window, frontmostProcessId))
            {
                continue;
            }

            var clipped = ScreenGeometry.Intersect(window.Frame, screenFrame);

            // Mac: guard appKitFrame.width >= 24, appKitFrame.height >= 16。
            // 注意这里判定的是**相交后**的尺寸，所以被屏幕边缘切掉一大半的窗口会被剔除。
            if (clipped.Width < MinimumWidth || clipped.Height < MinimumHeight)
            {
                continue;
            }

            result.Add(new WindowSnapTarget(
                window.WindowHandle,
                ScreenGeometry.Integral(clipped),
                window.EnumerationOrder));
        }

        return result;
    }

    /// <summary>
    /// 命中规则：包含该点 → zOrder 最小者 → 并列时面积最小者。
    ///
    /// 逐行对应 Mac 版 WindowSnapTargetSelector.target(at:from:)（WindowSnapService.swift:10-19）。
    /// </summary>
    public static WindowSnapTarget? TargetAt(PointD point, IReadOnlyList<WindowSnapTarget> targets)
    {
        ArgumentNullException.ThrowIfNull(targets);

        WindowSnapTarget? best = null;

        foreach (var candidate in targets)
        {
            if (!ScreenGeometry.Contains(candidate.Frame, point))
            {
                continue;
            }

            if (best is null
                || candidate.ZOrder < best.ZOrder
                || (candidate.ZOrder == best.ZOrder
                    && ScreenGeometry.Area(candidate.Frame) < ScreenGeometry.Area(best.Frame)))
            {
                best = candidate;
            }
        }

        return best;
    }
}
