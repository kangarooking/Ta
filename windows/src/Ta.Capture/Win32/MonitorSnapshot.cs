using Ta.Core.Capture;

namespace Ta.Capture.Win32;

/// <summary>
/// 一台显示器的原始观测结果，由 Win32 枚举产生。
///
/// 独立成记录类型是为了把「怎么拿到数据」和「怎么解释数据」分开：
/// <see cref="DisplaySnapshot"/> 只吃 <see cref="MonitorRecord"/>，因此可以完全用假数据单测。
/// </summary>
/// <param name="Handle">HMONITOR。指针值，重启即变，不能当稳定 ID（参考文档 §14 风险 #39）。</param>
/// <param name="DeviceName">设备名，如 <c>\\.\DISPLAY1</c>。跨会话唯一稳定，用于排序兜底。</param>
/// <param name="Bounds">rcMonitor。**本进程坐标空间下**的物理矩形，左上原点、Y 向下。</param>
/// <param name="WorkArea">rcWork，已扣除任务栏。</param>
/// <param name="IsPrimary">来自 MONITORINFOF_PRIMARY，绝不靠枚举顺序推断。</param>
/// <param name="EffectiveDpiX">GetDpiForMonitor 的 dpiX。96 = 100%。</param>
public sealed record MonitorRecord(
    IntPtr Handle,
    string DeviceName,
    RectD Bounds,
    RectD WorkArea,
    bool IsPrimary,
    uint EffectiveDpiX,
    uint EffectiveDpiY,
    uint PhysicalWidth = 0,
    uint PhysicalHeight = 0)
{
    /// <summary>
    /// 「枚举单位 → 物理像素」的倍率。对应 Mac 的 <c>NSScreen.backingScaleFactor</c>。
    ///
    /// ⚠️ **不能直接拿 dpi/96 当倍率**（曾经的实现，全屏截图被放大 1.25 倍的根因）：
    /// rcMonitor 的单位取决于**本进程的 DPI 感知模式** —— 感知进程拿到物理像素
    /// （2560x1440），不感知进程拿到逻辑像素（2048x1152）。而倍率 = 物理 / rcMonitor：
    ///
    ///   | 进程 DPI 感知 | rcMonitor | 倍率  |
    ///   |---|---|---|
    ///   | 感知（本工程 Ta.Shell 的实际情况） | 2560x1440 | 1.0  |
    ///   | 不感知 | 2048x1152 | 1.25 |
    ///
    /// 用 dpi/96（=1.25）顶替第一行，就会让 WGC 按 3200x1800 建帧池 —— 内容被 D3D
    /// 放大 1.25 倍：**全屏截图又糊又大**（用户实测 3199x1799，屏幕其实只有 2560x1440），
    /// 下游的裁剪、长截图滚动量换算跟着一起偏。
    ///
    /// 物理尺寸由 <see cref="DisplayEnumerator"/> 从显示模式读出（不受虚拟化影响）；
    /// 取不到时退回 DPI 倍率（唯一的不确定场景）。
    /// </summary>
    public double PixelScale =>
        DisplaySnapshot.ScaleFromFrame(Bounds, PhysicalWidth, EffectiveDpiX);
}

/// <summary>
/// 显示器快照的纯逻辑换算。
///
/// 对应 Mac 版 TaAgentCaptureService.swift:264-288 的 displays 映射，
/// 但 Windows 侧必须自行解决两件 Mac 不用管的事：
/// 1. <b>枚举顺序不保证</b> —— Mac 靠 <c>CGMainDisplayID()</c> 判主屏，
///    Windows 必须读 MONITORINFOF_PRIMARY（参考文档 §14 风险 #25）。
/// 2. <b>ID 是临时值</b> —— HMONITOR 是指针，按快照分配合法索引（风险 #39）。
/// </summary>
public static class DisplaySnapshot
{
    /// <summary>参考 DPI。Windows 的 100% 缩放。</summary>
    public const double ReferenceDpi = 96;

    /// <summary>
    /// DPI → 缩放倍率。夹到 >= 1：Mac 的 backingScaleFactor 至少 1，
    /// Windows 上 DPI 低于 96 时（极端配置）不应产生 0.x 的 scale。
    /// </summary>
    public static double ScaleFromDpi(uint dpi) => Math.Max(1, dpi / ReferenceDpi);

    /// <summary>
    /// 由「rcMonitor 矩形 + 该屏真实物理像素宽」求倍率：物理 / 矩形 = 枚举单位到物理像素的比。
    ///
    /// 这是 <see cref="MonitorRecord.PixelScale"/> 的纯函数版，刻意独立出来以便单测 ——
    /// 纯逻辑层不该依赖 Win32 才能验证。物理宽取不到（0）时退回 DPI 倍率。
    /// </summary>
    public static double ScaleFromFrame(RectD frame, uint physicalWidth, uint effectiveDpi)
    {
        if (physicalWidth > 0 && frame.Width > 0)
        {
            return physicalWidth / frame.Width;
        }

        return ScaleFromDpi(effectiveDpi);
    }

    /// <summary>
    /// 把原始观测列表换算为 <see cref="DisplayInfo"/> 列表。
    ///
    /// 排序规则：先按 Bounds.X、再按 Bounds.Y、最后按 DeviceName。
    /// 目的不是美观，而是让 ID 在「显示器集合不变」时**稳定**——
    /// EnumDisplayMonitors 的顺序在不同会话间可能变，直接用它当下标会让
    /// 已保存的 displayId 指向另一块屏。
    /// </summary>
    public static IReadOnlyList<DisplayInfo> ToDisplayInfos(IReadOnlyList<MonitorRecord> monitors)
    {
        ArgumentNullException.ThrowIfNull(monitors);

        var ordered = monitors
            .OrderBy(m => m.Bounds.MinX)
            .ThenBy(m => m.Bounds.MinY)
            .ThenBy(m => m.DeviceName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var result = new List<DisplayInfo>(ordered.Count);
        for (var i = 0; i < ordered.Count; i++)
        {
            var m = ordered[i];
            result.Add(new DisplayInfo
            {
                Id = i,
                Frame = m.Bounds,
                PixelScale = m.PixelScale,
                IsPrimary = m.IsPrimary,
            });
        }

        return result;
    }

    /// <summary>按 ID 取显示器。找不到时返回 null（对应 Mac 的 <c>guard let display else throw</c>）。</summary>
    public static DisplayInfo? Find(IReadOnlyList<DisplayInfo> displays, int displayId) =>
        displays.FirstOrDefault(d => d.Id == displayId);

    /// <summary>取主屏。没有主屏标记时退回第一块（正常情况下总有主屏）。</summary>
    public static DisplayInfo? Primary(IReadOnlyList<DisplayInfo> displays) =>
        displays.FirstOrDefault(d => d.IsPrimary) ?? displays.FirstOrDefault();

    /// <summary>
    /// 与窗口相交面积最大的显示器。对应 Mac 版 windowResolution 里
    /// 「与 window.frame 相交的所有屏取最大 pixelScale」的语义（TaAgentCaptureService.swift:233-236）。
    /// </summary>
    public static double PixelScaleForRect(IReadOnlyList<DisplayInfo> displays, RectD rect)
    {
        var scale = displays
            .Where(d => !ScreenGeometry.Intersect(d.Frame, rect).IsEmpty)
            .Select(d => d.PixelScale)
            .DefaultIfEmpty(1)
            .Max();

        return Math.Max(1, scale);
    }
}
