using Ta.Core.Capture;
using Xunit;

namespace Ta.Core.Tests.Capture;

/// <summary>
/// FrozenDisplayCropper 测试。
///
/// 首个用例逐字复现 Mac 版 WindowSnapAndSelectionTests.swift:6-22 的输入与期望值，
/// 用于确认坐标换算链路在 Windows 上等价（Y 翻转已按 Windows 语义去除）。
/// </summary>
public class FrozenDisplayCropperTests
{
    private static CaptureSelection Selection(
        double x, double y, double width, double height,
        double screenX = 0, double screenY = 0,
        double screenWidth = 200, double screenHeight = 150,
        double scale = 2) =>
        new()
        {
            GlobalRect = new RectD(x, y, width, height),
            ScreenFrame = new RectD(screenX, screenY, screenWidth, screenHeight),
            BackingScaleFactor = scale,
        };

    [Fact]
    public void 选区在2倍缩放的屏幕上换算为正确像素矩形()
    {
        // Mac 版断言：(50, 25, 100×50) @ 屏 200×150 scale 2 → 像素 (100, 150, 200×100)
        // Mac 需要 Y 翻转（150 = 屏高 300 − 150），Windows 上下同向故为 (100, 50, 200×100)。
        var selection = Selection(50, 25, 100, 50);
        var ok = FrozenDisplayCropper.TryPixelRect(selection, 400, 300, out var result);

        Assert.True(ok);
        Assert.Equal(100, result.Left);
        Assert.Equal(50, result.Top);
        Assert.Equal(200, result.Width);
        Assert.Equal(100, result.Height);
    }

    [Fact]
    public void 选区按比例换算而非直接乘缩放系数()
    {
        // 图像尺寸与 screenFrame 的比例决定换算，即使图像尺寸与 scale 不一致也成立。
        var selection = Selection(0, 0, 100, 100, screenWidth: 100, screenHeight: 100, scale: 1);
        var ok = FrozenDisplayCropper.TryPixelRect(selection, 50, 50, out var result);

        Assert.True(ok);
        Assert.Equal(50, result.Width);
        Assert.Equal(50, result.Height);
    }

    [Fact]
    public void 完全位于显示器之外的选区被拒绝()
    {
        var selection = Selection(5000, 5000, 100, 100);
        var ok = FrozenDisplayCropper.TryPixelRect(selection, 400, 300, out _);

        Assert.False(ok);
    }

    [Fact]
    public void 部分越界的选区被裁剪到图像边界()
    {
        // 选区右边缘超出图像，结果必须夹取而非越界。
        var selection = Selection(180, 25, 100, 50);
        var ok = FrozenDisplayCropper.TryPixelRect(selection, 400, 300, out var result);

        Assert.True(ok);
        Assert.Equal(360, result.Left);
        Assert.Equal(40, result.Width);   // 400 − 360
    }

    [Fact]
    public void 尺寸为零的显示器边界被拒绝()
    {
        var selection = new CaptureSelection
        {
            GlobalRect = new RectD(0, 0, 10, 10),
            ScreenFrame = new RectD(0, 0, 0, 0),
        };
        var ok = FrozenDisplayCropper.TryPixelRect(selection, 400, 300, out _);

        Assert.False(ok);
    }

    [Fact]
    public void 非原点的显示器偏移被正确处理()
    {
        // 对应 Mac 版支持的负原点/副屏场景（screen.frame.minX 非 0）。
        var selection = Selection(110, 60, 50, 40, screenX: 100, screenY: 50, screenWidth: 200, screenHeight: 150);
        var ok = FrozenDisplayCropper.TryPixelRect(selection, 400, 300, out var result);

        Assert.True(ok);
        Assert.Equal(20, result.Left);    // (110 − 100) × 2
        Assert.Equal(20, result.Top);     // (60 − 50) × 2
        Assert.Equal(100, result.Width);  // 50 × 2
        Assert.Equal(80, result.Height);  // 40 × 2
    }
}
