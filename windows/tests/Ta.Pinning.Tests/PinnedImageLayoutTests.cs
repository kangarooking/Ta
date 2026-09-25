using Ta.Core.Capture;

namespace Ta.Pinning.Tests;

/// <summary>
/// 钉图初始尺寸 / 原点夹取 / 窗口框架约束 / 双击判定。
///
/// 前四个用例**逐字复刻** Mac 版 Tests/AIScreenshotAppTests/PinnedImageInteractionTests.swift
/// 的输入与期望值，用来证明 Windows 上的布局算术与 Mac 完全一致。
/// </summary>
public class PinnedImageLayoutTests
{
    /// <summary>Mac 测试里的 2× Retina 屏（:18）。</summary>
    private static readonly RectD RetinaScreen = new(0, 0, 1512, 950);

    [Fact]
    public void 截图钉图在2倍屏上保留选区的点尺寸()
    {
        // Mac: testScreenshotPinPreservesSelectionPointSizeOnRetina（:14-22）
        var size = PinnedImageLayout.InitialSize(
            (Width: 800, Height: 500),
            new PinSizeD(400, 250),
            RetinaScreen);

        Assert.Equal(400, size.Width, 6);
        Assert.Equal(250, size.Height, 6);
    }

    [Fact]
    public void 小尺寸截图钉图不会被人工放大()
    {
        // Mac: testSmallScreenshotPinIsNotArtificiallyEnlarged（:24-32）
        // scale 上限 1 —— 80×50 的选区钉出来还是 80×50，不撑到 640×480。
        var size = PinnedImageLayout.InitialSize(
            (Width: 160, Height: 100),
            new PinSizeD(80, 50),
            RetinaScreen);

        Assert.Equal(80, size.Width, 6);
        Assert.Equal(50, size.Height, 6);
    }

    [Fact]
    public void 超大钉图按可见屏等比缩小到876x584()
    {
        // Mac: testOversizedPinScalesDownProportionallyToVisibleScreen（:34-43）
        // limit = 900−24 × 700−24 = 876 × 676；scale = min(1, 876/1200, 676/800) = 0.73
        var size = PinnedImageLayout.InitialSize(
            (Width: 2400, Height: 1600),
            new PinSizeD(1200, 800),
            new RectD(0, 0, 900, 700));

        Assert.Equal(876, size.Width, 3);
        Assert.Equal(584, size.Height, 3);
    }

    [Fact]
    public void 剪贴板钉图走640x480上限且不放大()
    {
        // Mac: preferredLogicalSize == nil 时 limit = min(640, 屏−24) × min(480, 屏−24)（:22-24）
        var size = PinnedImageLayout.InitialSize(
            (Width: 2000, Height: 1500),
            preferredLogicalSize: null,
            RetinaScreen);

        // 2000×1500 按 640 上限等比 → 640×480
        Assert.Equal(640, size.Width, 6);
        Assert.Equal(480, size.Height, 6);

        var small = PinnedImageLayout.InitialSize(
            (Width: 100, Height: 80),
            preferredLogicalSize: null,
            RetinaScreen);

        // 100×80 小于上限，保持原样（scale 上限 1）
        Assert.Equal(100, small.Width, 6);
        Assert.Equal(80, small.Height, 6);
    }

    [Fact]
    public void 剪贴板钉图在窄屏上受屏高限制()
    {
        // 屏只有 300 高 → screenLimit 高 = 276 < 480，故按 276 夹。
        var size = PinnedImageLayout.InitialSize(
            (Width: 2000, Height: 2000),
            preferredLogicalSize: null,
            new RectD(0, 0, 1512, 300));

        Assert.Equal(276, size.Width, 6);
        Assert.Equal(276, size.Height, 6);
    }

    [Fact]
    public void 原点被夹到可见屏内()
    {
        // Mac: min(max(visibleFrame.minX + 12, anchor.x), visibleFrame.maxX - width - 12)（:148-149）
        var size = new PinSizeD(400, 250);
        var visible = RetinaScreen;

        // 锚点在左上越界 → 夹到 minX+12 / minY+12
        var topLeft = PinnedImageLayout.ClampOrigin(new PinPointD(5, 5), size, visible);
        Assert.Equal(12, topLeft.X, 6);
        Assert.Equal(12, topLeft.Y, 6);

        // 锚点在右下越界 → 夹到 maxX−w−12 / maxY−h−12 = 1100 / 688
        var bottomRight = PinnedImageLayout.ClampOrigin(new PinPointD(1400, 940), size, visible);
        Assert.Equal(1100, bottomRight.X, 6);
        Assert.Equal(688, bottomRight.Y, 6);

        // 锚点在范围内 → 原样保留
        var inside = PinnedImageLayout.ClampOrigin(new PinPointD(300, 200), size, visible);
        Assert.Equal(300, inside.X, 6);
        Assert.Equal(200, inside.Y, 6);
    }

    [Fact]
    public void 副屏负原点也能正确夹取()
    {
        // 参考文档 §14 风险 #16：Windows 多屏可能存在负原点显示器。
        var visible = new RectD(-1920, -200, 1920, 1080);
        var size = new PinSizeD(400, 250);

        var clamped = PinnedImageLayout.ClampOrigin(new PinPointD(-5000, -5000), size, visible);
        Assert.Equal(-1908, clamped.X, 6);   // minX + 12
        Assert.Equal(-188, clamped.Y, 6);    // minY + 12

        var far = PinnedImageLayout.ClampOrigin(new PinPointD(5000, 5000), size, visible);
        Assert.Equal(-1920 + 1920 - 400 - 12, far.X, 6);   // maxX − w − 12 = -412
        Assert.Equal(-200 + 1080 - 250 - 12, far.Y, 6);    // maxY − h − 12 = 618
    }

    [Fact]
    public void 窗口框架约束原样返回()
    {
        // Mac: testPinnedPanelCanMoveAboveTheMenuBarWithoutSystemClamping（:64-75）
        // 钉图允许伸到任务栏下方 —— 这条契约必须钉死，否则将来有人「顺手」加夹取就破了。
        var proposed = new RectD(100, 920, 320, 180);
        var result = PinnedImageLayout.ConstrainFrame(proposed);

        Assert.Equal(proposed, result);
        Assert.Equal(920, result.Y, 6);
    }

    [Fact]
    public void 初始尺寸与原点一次算对()
    {
        var (size, origin) = PinnedImageLayout.InitialFrame(
            (Width: 800, Height: 500),
            new PinSizeD(400, 250),
            RetinaScreen,
            new PinPointD(2000, 2000));

        Assert.Equal(400, size.Width, 6);
        Assert.Equal(1100, origin.X, 6);   // 被夹到 maxX − w − 12
        Assert.Equal(688, origin.Y, 6);    // 被夹到 maxY − h − 12
    }

    [Fact]
    public void 零尺寸的首选逻辑尺寸被忽略()
    {
        // Mac: guard size.width >= 1, size.height >= 1 else { return nil }（:10-13）
        var size = PinnedImageLayout.InitialSize(
            (Width: 300, Height: 200),
            new PinSizeD(0, 0),
            RetinaScreen);

        // 退化为按图像像素走「剪贴板分支」：min(640, 1488) × min(480, 926) → 300×200 不放大
        Assert.Equal(300, size.Width, 6);
        Assert.Equal(200, size.Height, 6);
    }

    // ── 双击关闭（Mac: PinnedImageInteractionTests:5-12） ────────────

    [Fact]
    public void 单击不会关闭钉图()
    {
        Assert.False(PinnedImageInteraction.ShouldClose(1));
    }

    [Fact]
    public void 双击与更高点击数都会关闭钉图()
    {
        Assert.True(PinnedImageInteraction.ShouldClose(2));
        Assert.True(PinnedImageInteraction.ShouldClose(3));
    }

    [Fact]
    public void 零次点击不会关闭钉图()
    {
        Assert.False(PinnedImageInteraction.ShouldClose(0));
    }

    // ── 装饰默认态（Mac: PinnedImageInteractionTests:45-62） ─────────

    [Fact]
    public void 边框与阴影默认可见()
    {
        var state = new PinDecorationState();
        Assert.True(state.ShowsBorder);
        Assert.True(state.ShowsShadow);
    }

    [Fact]
    public void 边框与阴影可以独立切换()
    {
        var state = new PinDecorationState();

        state = state.Toggled(showsBorder: false, state.ShowsShadow);
        Assert.False(state.ShowsBorder);
        Assert.True(state.ShowsShadow);

        state = state.Toggled(state.ShowsBorder, showsShadow: false);
        Assert.False(state.ShowsBorder);
        Assert.False(state.ShowsShadow);
    }
}
