using Ta.Core.Capture;

namespace Ta.Pinning.Tests;

/// <summary>
/// 裁剪映射测试 —— 把 Mac 的 Y 翻转公式**逐值断言**，
/// 并证明 Windows 实现（无翻转）与它在同一物理选区上给出完全相同的结果。
/// </summary>
public class PinnedImageCropTests
{
    /// <summary>视图（钉图内容区）尺寸：400×250 pt。</summary>
    private static readonly RectD Bounds = new(0, 0, 400, 250);

    /// <summary>源图：800×500 px。</summary>
    private const int SourceWidth = 800;
    private const int SourceHeight = 500;

    [Fact]
    public void Mac的Y翻转公式逐值正确()
    {
        // Mac: PinnedImageWindowController.swift:439-443
        // 这里按 Mac 的约定喂 **Y 向上**的视图坐标：选区顶部 y=75、高 125
        // （等价于 Windows 坐标里的 y=50、高 125）。
        var selectedYUp = new RectD(100, 75, 200, 125);
        var current = new RectD(0, 0, SourceWidth, SourceHeight);

        var mapped = PinnedImageCrop.MapMacFormula(selectedYUp, Bounds, current);

        // x = 0 + 100/400*800 = 200
        Assert.Equal(200, mapped.X, 9);
        // y = 0 + (1 - (75+125)/250) * 500 = (1 - 0.8) * 500 = 100   ← 这就是那次 Y 翻转
        Assert.Equal(100, mapped.Y, 9);
        // w = 200/400*800 = 400 ; h = 125/250*500 = 250
        Assert.Equal(400, mapped.Width, 9);
        Assert.Equal(250, mapped.Height, 9);
    }

    [Fact]
    public void Windows实现与Mac公式在同一物理选区上结果一致()
    {
        // 同一物理选区：Mac 用 Y 向上 (100, 75, 200, 125)，Windows 用 Y 向下 (100, 50, 200, 125)
        var selectedYDown = new RectD(100, 50, 200, 125);
        var selectedYUp = new RectD(
            selectedYDown.X,
            Bounds.Height - selectedYDown.MaxY,
            selectedYDown.Width,
            selectedYDown.Height);

        var mac = PinnedImageCrop.MapMacFormula(
            selectedYUp, Bounds, new RectD(0, 0, SourceWidth, SourceHeight));

        Assert.True(PinnedImageCrop.TryMapViewRectToImage(
            selectedYDown, Bounds, null, SourceWidth, SourceHeight, out var windows));

        // 四舍五入后比对：Mac 的 (1 - maxY/h) 形式在边界值上有亚像素误差
        // （1 - 0.8 = 0.19999999999999996），实现侧已换成数值更稳的等价形式。
        Assert.Equal((int)Math.Round(mac.X), windows.Left);
        Assert.Equal((int)Math.Round(mac.Y), windows.Top);
        Assert.Equal((int)Math.Round(mac.Width), windows.Width);
        Assert.Equal((int)Math.Round(mac.Height), windows.Height);
        Assert.Equal(200, windows.Left);
        Assert.Equal(100, windows.Top);
        Assert.Equal(400, windows.Width);
        Assert.Equal(250, windows.Height);
    }

    [Fact]
    public void 已有裁剪矩形时按当前矩形换算()
    {
        // Mac: let current = cropRect ?? CGRect(x: 0, y: 0, width: sourceCGImage.width, …)（:438）
        // 先裁到源图左上 1/4：0,0,400,250
        Assert.True(PinnedImageCrop.TryMapViewRectToImage(
            new RectD(0, 0, 200, 125), Bounds, null, SourceWidth, SourceHeight, out var first));
        Assert.Equal(new RectI(0, 0, 400, 250), first);

        // 再在钉图右半部拖一个选区（视图 x 200..400, y 125..250）→ 落在 current 的右下半
        Assert.True(PinnedImageCrop.TryMapViewRectToImage(
            new RectD(200, 125, 200, 125), Bounds, first, SourceWidth, SourceHeight, out var second));

        Assert.Equal(200, second.Left);    // 0 + 200/400*400
        Assert.Equal(125, second.Top);     // 0 + 125/250*250
        Assert.Equal(200, second.Width);   // 200/400*400
        Assert.Equal(125, second.Height);  // 125/250*250
    }

    [Fact]
    public void 小于12pt的选区被拒绝()
    {
        // Mac: guard selected.width >= 12, selected.height >= 12 else { return }（:437）
        Assert.False(PinnedImageCrop.TryMapViewRectToImage(
            new RectD(10, 10, 11.9, 200), Bounds, null, SourceWidth, SourceHeight, out _));

        Assert.False(PinnedImageCrop.TryMapViewRectToImage(
            new RectD(10, 10, 200, 11.9), Bounds, null, SourceWidth, SourceHeight, out _));

        Assert.True(PinnedImageCrop.TryMapViewRectToImage(
            new RectD(10, 10, 12, 12), Bounds, null, SourceWidth, SourceHeight, out _));
    }

    [Fact]
    public void 结果小于2像素被拒绝()
    {
        // Mac: guard newCrop.width >= 2, newCrop.height >= 2（:445）
        // 视图 400pt ↔ 源图 800px（2:1），12pt 选区 → 24px，够；
        // 把 current 设成 1px 宽，比例换算后就不到 2px。
        Assert.False(PinnedImageCrop.TryMapViewRectToImage(
            new RectD(0, 0, 12, 12), Bounds, new RectI(0, 0, 1, 500), SourceWidth, SourceHeight, out _));
    }

    [Fact]
    public void 结果被夹到源图边界内()
    {
        // Mac: .integral.intersection(CGRect(x: 0, y: 0, width: sourceCGImage.width, …))（:444）
        // 选区几乎覆盖整个视图，但 current 故意越界（模拟浮点/进位误差）。
        Assert.True(PinnedImageCrop.TryMapViewRectToImage(
            new RectD(0, 0, 400, 250), Bounds, new RectI(0, 0, SourceWidth + 100, SourceHeight + 100),
            SourceWidth, SourceHeight, out var result));

        Assert.Equal(0, result.Left);
        Assert.Equal(0, result.Top);
        Assert.Equal(SourceWidth, result.Right);
        Assert.Equal(SourceHeight, result.Bottom);
    }

    [Fact]
    public void 原点向下取整远边向上取整()
    {
        // Mac: CGRect.integral —— 取最小的整数包含矩形。
        // 视图 300pt ↔ 源图 100px：选区 x 10..25 → 源图 3.33..8.33 → [3, 9)
        Assert.True(PinnedImageCrop.TryMapViewRectToImage(
            new RectD(10, 10, 15, 15), new RectD(0, 0, 300, 300), null, 100, 100, out var result));

        Assert.Equal(3, result.Left);
        Assert.Equal(3, result.Top);
        Assert.Equal(9, result.Right);
        Assert.Equal(9, result.Bottom);
    }

    [Fact]
    public void 归一化拖拽矩形与求交()
    {
        // Mac: normalizedRect（:630-632）+ .intersection(bounds)（:388）
        var normalized = PinnedImageCrop.Normalize(new PinPointD(300, 200), new PinPointD(100, 50));
        Assert.Equal(100, normalized.X, 9);
        Assert.Equal(50, normalized.Y, 9);
        Assert.Equal(200, normalized.Width, 9);
        Assert.Equal(150, normalized.Height, 9);

        var clipped = PinnedImageCrop.IntersectWithBounds(normalized, Bounds);
        Assert.Equal(100, clipped.X, 9);
        Assert.Equal(50, clipped.Y, 9);
        Assert.Equal(200, clipped.Width, 9);
        Assert.Equal(150, clipped.Height, 9);

        // 完全在外 → 空矩形
        var outside = PinnedImageCrop.IntersectWithBounds(new RectD(500, 500, 10, 10), Bounds);
        Assert.True(outside.IsEmpty);
    }
}
