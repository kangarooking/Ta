using Ta.Core.Imaging;
using Ta.Pinning.Imaging;

namespace Ta.Pinning.Tests;

/// <summary>
/// 灰度 / 反色像素变换测试。
/// 对应 Mac 版 PinnedImageView.displayedImage 的两个滤镜分支（:595-618）。
/// </summary>
public class PinPixelFilterTests
{
    /// <summary>构造一个 2×1 位图：左 (10,20,30,255)、右 (200,100,50,128)。</summary>
    private static RgbaBitmap CreateSample()
    {
        var bitmap = new RgbaBitmap(2, 1);
        bitmap.Pixels[0] = 10;
        bitmap.Pixels[1] = 20;
        bitmap.Pixels[2] = 30;
        bitmap.Pixels[3] = 255;
        bitmap.Pixels[4] = 200;
        bitmap.Pixels[5] = 100;
        bitmap.Pixels[6] = 50;
        bitmap.Pixels[7] = 128;
        return bitmap;
    }

    private static (byte R, byte G, byte B, byte A) Pixel(RgbaBitmap bitmap, int x) =>
        (bitmap.Pixels[x * 4], bitmap.Pixels[(x * 4) + 1], bitmap.Pixels[(x * 4) + 2], bitmap.Pixels[(x * 4) + 3]);

    [Fact]
    public void 灰度按Rec709亮度加权()
    {
        var source = CreateSample();
        var gray = PinPixelFilters.Apply(source, PinFilterMode.Grayscale);

        // 左像素：(2126*10 + 7152*20 + 722*30) / 10000 = 185960 / 10000 = 18
        var left = Pixel(gray, 0);
        Assert.Equal(18, left.R);
        Assert.Equal(18, left.G);
        Assert.Equal(18, left.B);
        Assert.Equal(255, left.A);   // Alpha 不动

        // 右像素：(2126*200 + 7152*100 + 722*50) / 10000 = 1176500 / 10000 = 117
        var right = Pixel(gray, 1);
        Assert.Equal(117, right.R);
        Assert.Equal(117, right.G);
        Assert.Equal(117, right.B);
        Assert.Equal(128, right.A);  // Alpha 仍然不动

        // 原图不被修改
        Assert.Equal(10, Pixel(source, 0).R);
    }

    [Fact]
    public void 反色逐通道取反且不动Alpha()
    {
        var source = CreateSample();
        var inverted = PinPixelFilters.Apply(source, PinFilterMode.Inverted);

        var left = Pixel(inverted, 0);
        Assert.Equal(245, left.R);
        Assert.Equal(235, left.G);
        Assert.Equal(225, left.B);
        Assert.Equal(255, left.A);

        var right = Pixel(inverted, 1);
        Assert.Equal(55, right.R);
        Assert.Equal(155, right.G);
        Assert.Equal(205, right.B);
        Assert.Equal(128, right.A);
    }

    [Fact]
    public void 不过滤模式原样返回()
    {
        var source = CreateSample();
        var same = PinPixelFilters.Apply(source, PinFilterMode.None);

        Assert.Same(source, same);
    }

    [Fact]
    public void 两次反色回到原图()
    {
        var source = CreateSample();
        var twice = PinPixelFilters.Apply(source, PinFilterMode.Inverted);
        twice = PinPixelFilters.Apply(twice, PinFilterMode.Inverted);

        Assert.Equal(source.Pixels, twice.Pixels);
    }

    [Fact]
    public void 灰度与反色互斥且可由状态机保证()
    {
        // 关键不是「滤镜本身互斥」，而是**状态**上同一时刻只有一个生效。
        var state = new PinViewState(new RgbaBitmapSource(400, 250), new PinSizeD(400, 250), new PinPointD(0, 0));

        state.ToggleGrayscale();
        Assert.Equal(PinFilterMode.Grayscale, state.FilterMode);

        // 再开反色 → 灰度必须自动关掉
        state.ToggleInversion();
        Assert.Equal(PinFilterMode.Inverted, state.FilterMode);
        Assert.NotEqual(PinFilterMode.Grayscale, state.FilterMode);

        // 关掉反色 → 两个都没开
        state.ToggleInversion();
        Assert.Equal(PinFilterMode.None, state.FilterMode);

        // 反过来也一样
        state.ToggleInversion();
        state.ToggleGrayscale();
        Assert.Equal(PinFilterMode.Grayscale, state.FilterMode);
    }

    [Fact]
    public void 恢复显示会清掉滤镜()
    {
        var state = new PinViewState(new RgbaBitmapSource(400, 250), new PinSizeD(400, 250), new PinPointD(0, 0));
        state.ToggleGrayscale();
        state.ToggleMirrorHorizontally();

        state.ResetAppearance();

        Assert.Equal(PinFilterMode.None, state.FilterMode);
        Assert.False(state.MirroredHorizontally);
        Assert.Equal(0, state.RotationQuarterTurns);
        Assert.True(state.Decoration.ShowsBorder);
        Assert.True(state.Decoration.ShowsShadow);
    }

    [Fact]
    public void 灰度对全黑与全白是恒等()
    {
        var bitmap = new RgbaBitmap(2, 1);
        bitmap.Pixels[0] = 0; bitmap.Pixels[1] = 0; bitmap.Pixels[2] = 0; bitmap.Pixels[3] = 255;
        bitmap.Pixels[4] = 255; bitmap.Pixels[5] = 255; bitmap.Pixels[6] = 255; bitmap.Pixels[7] = 255;

        var gray = PinPixelFilters.Apply(bitmap, PinFilterMode.Grayscale);

        Assert.Equal(0, gray.Pixels[0]);
        Assert.Equal(255, gray.Pixels[4]);
    }
}
