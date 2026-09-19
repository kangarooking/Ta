using Ta.Capture.Win32;
using Ta.Core.Capture;

namespace Ta.Capture.Tests.Win32;

/// <summary>
/// 显示器快照换算的单元测试。
///
/// 重点锁定参考文档 §14 风险 #25：<b>绝不假设枚举序第一块是主屏</b>。
/// </summary>
public class DisplaySnapshotTests
{
    private static MonitorRecord Monitor(
        string name, RectD bounds, bool isPrimary, uint dpi = 96) =>
        new(IntPtr.Zero, name, bounds, bounds, isPrimary, dpi, dpi);

    /// <summary>96 DPI = 100% 缩放。</summary>
    [Fact]
    public void DPI换算为缩放倍率()
    {
        Assert.Equal(1.0, DisplaySnapshot.ScaleFromDpi(96));
        Assert.Equal(1.5, DisplaySnapshot.ScaleFromDpi(144));
        Assert.Equal(2.0, DisplaySnapshot.ScaleFromDpi(192));
    }

    /// <summary>DPI 低于 96 时夹到 1 —— 对应 Mac 的 backingScaleFactor 至少为 1。</summary>
    [Fact]
    public void 低DPI被夹到1()
    {
        Assert.Equal(1.0, DisplaySnapshot.ScaleFromDpi(72));
        Assert.Equal(1.0, DisplaySnapshot.ScaleFromDpi(0));
    }

    /// <summary>
    /// 主屏在枚举序第 3 位时也必须被正确识别。
    /// 这是风险 #25 的回归护栏：若实现改成 <c>monitors[0]</c>，本用例必失败。
    /// </summary>
    [Fact]
    public void 主屏由标志位判定而非枚举顺序()
    {
        var monitors = new[]
        {
            Monitor(@"\\.\DISPLAY2", new RectD(1920, 0, 1920, 1080), isPrimary: false),
            Monitor(@"\\.\DISPLAY3", new RectD(3840, 0, 1920, 1080), isPrimary: false),
            Monitor(@"\\.\DISPLAY1", new RectD(0, 0, 1920, 1080), isPrimary: true),
        };

        var displays = DisplaySnapshot.ToDisplayInfos(monitors);

        Assert.Equal(3, displays.Count);
        var primary = DisplaySnapshot.Primary(displays);
        Assert.NotNull(primary);
        Assert.True(primary.IsPrimary);
        Assert.Equal(new RectD(0, 0, 1920, 1080), primary.Frame);

        // 只有一块被标记为主屏。
        Assert.Equal(1, displays.Count(d => d.IsPrimary));
    }

    /// <summary>ID 按位置排序分配，因此在显示器集合不变时保持稳定。</summary>
    [Fact]
    public void ID按位置稳定分配()
    {
        // 故意给一个乱序输入。
        var monitors = new[]
        {
            Monitor(@"\\.\DISPLAY2", new RectD(1920, 0, 1920, 1080), isPrimary: false),
            Monitor(@"\\.\DISPLAY1", new RectD(0, 0, 1920, 1080), isPrimary: true),
        };

        var displays = DisplaySnapshot.ToDisplayInfos(monitors);

        Assert.Equal(0, displays[0].Id);
        Assert.Equal(0, displays[0].Frame.MinX);
        Assert.Equal(1, displays[1].Id);
        Assert.Equal(1920, displays[1].Frame.MinX);
    }

    /// <summary>相同 X 时按 Y 排序（上下堆叠的显示器）。</summary>
    [Fact]
    public void 相同X时按Y排序()
    {
        var monitors = new[]
        {
            Monitor(@"\\.\DISPLAY1", new RectD(0, 1080, 1920, 1080), isPrimary: true),
            Monitor(@"\\.\DISPLAY2", new RectD(0, 0, 1920, 1080), isPrimary: false),
        };

        var displays = DisplaySnapshot.ToDisplayInfos(monitors);

        Assert.Equal(0, displays[0].Frame.MinY);
        Assert.Equal(1080, displays[1].Frame.MinY);
    }

    [Fact]
    public void 按ID查找显示器()
    {
        var monitors = new[] { Monitor(@"\\.\DISPLAY1", new RectD(0, 0, 800, 600), isPrimary: true) };
        var displays = DisplaySnapshot.ToDisplayInfos(monitors);

        Assert.NotNull(DisplaySnapshot.Find(displays, 0));
        Assert.Null(DisplaySnapshot.Find(displays, 42));
    }

    /// <summary>
    /// 窗口缩放系数取「相交屏中最大的 pixelScale」。
    /// 对应 Mac 版 windowResolution（TaAgentCaptureService.swift:233-236）。
    /// </summary>
    [Fact]
    public void 窗口缩放系数取相交屏中的最大倍率()
    {
        var displays = new[]
        {
            new DisplayInfo { Id = 0, Frame = new RectD(0, 0, 1920, 1080), PixelScale = 1, IsPrimary = true },
            new DisplayInfo { Id = 1, Frame = new RectD(1920, 0, 1920, 1080), PixelScale = 2, IsPrimary = false },
        };

        // 只跨 100% 屏。
        Assert.Equal(1.0, DisplaySnapshot.PixelScaleForRect(displays, new RectD(100, 100, 200, 200)));
        // 跨两块屏 → 取 2。
        Assert.Equal(2.0, DisplaySnapshot.PixelScaleForRect(displays, new RectD(1800, 100, 400, 200)));
        // 完全不相交 → 兜底 1。
        Assert.Equal(1.0, DisplaySnapshot.PixelScaleForRect(displays, new RectD(9000, 9000, 100, 100)));
    }

    /// <summary>
    /// 倍率必须由「物理像素 ÷ rcMonitor 宽度」得出，**不是** dpi/96。
    ///
    /// 这是「全屏截图被放大 1.25 倍」的回归护栏：2560x1440 屏 @125% 缩放，
    /// DPI 感知进程的 rcMonitor 已经是物理像素，倍率必须是 1.0 —— 旧实现给 1.25，
    /// WGC 帧池就被建成 3200x1800，桌面内容被 D3D 放大：又糊又大（实测全屏截图 3199x1799）。
    /// </summary>
    [Fact]
    public void 倍率由物理像素除以显示器矩形得出()
    {
        // DPI 感知进程：rcMonitor = 物理像素 → 恒为 1.0，与 DPI 无关。
        Assert.Equal(1.0, DisplaySnapshot.ScaleFromFrame(new RectD(0, 0, 2560, 1440), 2560, 120));
        Assert.Equal(1.0, DisplaySnapshot.ScaleFromFrame(new RectD(0, 0, 1920, 1080), 1920, 96));

        // DPI 不感知进程：rcMonitor 是逻辑像素 → 需要 1.25 才能还原物理像素。
        Assert.Equal(1.25, DisplaySnapshot.ScaleFromFrame(new RectD(0, 0, 2048, 1152), 2560, 120));

        // 取不到显示模式时退回 DPI 倍率（唯一的不确定场景）。
        Assert.Equal(1.5, DisplaySnapshot.ScaleFromFrame(new RectD(0, 0, 1920, 1080), 0, 144));
    }

    /// <summary>空显示器列表不应抛异常。</summary>
    [Fact]
    public void 空列表返回空结果()
    {
        Assert.Empty(DisplaySnapshot.ToDisplayInfos(Array.Empty<MonitorRecord>()));
        Assert.Null(DisplaySnapshot.Primary(Array.Empty<DisplayInfo>()));
    }
}
