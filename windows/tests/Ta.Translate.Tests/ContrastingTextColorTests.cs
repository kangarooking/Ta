using Ta.Core.Capture;
using Ta.Core.Imaging;
using Ta.Translate;
using Ta.Translate.Rendering;

namespace Ta.Translate.Tests;

/// <summary>
/// 验收 1：对比度选择 —— 亮度 0.2126R + 0.7152G + 0.0722B 在 0.54 两侧分别选黑/白。
/// 对应 Mac: TranslatedImageRenderer.contrastingTextColor（:187-191）。
/// </summary>
public class ContrastingTextColorTests
{
    [Theory]
    [InlineData(0.60, 0, 0, 0)]       // 亮 → 黑
    [InlineData(0.40, 255, 255, 255)] // 暗 → 白
    public void 亮度两侧选择相反的文字颜色(double luminance, byte expectedR, byte expectedG, byte expectedB)
    {
        // 用纯灰构造精确亮度（R=G=B=x 时亮度即 x/255）。
        var gray = (byte)Math.Round(luminance * 255);
        var background = new RgbaColor(gray, gray, gray);
        var textColor = RgbaColor.ContrastingTextColor(background);

        Assert.Equal(expectedR, textColor.R);
        Assert.Equal(expectedG, textColor.G);
        Assert.Equal(expectedB, textColor.B);
    }

    [Fact]
    public void 阈值两侧使用严格大于判定()
    {
        // Mac 的判定是严格大于：luminance > 0.54 → 黑，否则白。
        // 找一个亮度略高于 0.54 和一个略低于 0.54 的灰色，断言两侧选择相反。
        var above = new RgbaColor(138, 138, 138); // 138/255 ≈ 0.5412 > 0.54
        var below = new RgbaColor(137, 137, 137); // 137/255 ≈ 0.5373 < 0.54

        Assert.True(above.Luminance > 0.54);
        Assert.True(below.Luminance <= 0.54);

        Assert.Equal(RgbaColor.Black, RgbaColor.ContrastingTextColor(above));
        Assert.Equal(RgbaColor.White, RgbaColor.ContrastingTextColor(below));
    }

    [Fact]
    public void 非灰背景按Rec709系数计算亮度()
    {
        // 纯绿亮度 = 0.7152 > 0.54 → 黑字；纯蓝亮度 = 0.0722 < 0.54 → 白字。
        Assert.Equal(RgbaColor.Black, RgbaColor.ContrastingTextColor(new RgbaColor(0, 255, 0)));
        Assert.Equal(RgbaColor.White, RgbaColor.ContrastingTextColor(new RgbaColor(0, 0, 255)));
    }

    [Fact]
    public void 纯红亮度低于阈值选白字()
    {
        // 0.2126 < 0.54 —— 纯红背景上用白字。
        Assert.Equal(RgbaColor.White, RgbaColor.ContrastingTextColor(new RgbaColor(255, 0, 0)));
    }

    [Fact]
    public void 渲染fullImage时按区域均值选文字颜色()
    {
        // 左半黑右半白的图 → 均值约 128（亮度 ≈ 0.5 < 0.54）→ 白字。
        var image = HalfBlackHalfWhite(64, 32);
        var line = TestBitmaps.TranslatedLine("Source", "译文测试", 0.0, 0.0, 1.0, 1.0);

        var output = new TranslatedImageRenderer().Render(
            image,
            new[] { line },
            ScreenshotTranslationMode.FullImage,
            "简体中文");

        // 尺寸不变（与 Mac 测试 testFullTranslationPreservesOriginalDimensions 对齐）。
        Assert.Equal(64, output.Width);
        Assert.Equal(32, output.Height);
    }

    private static RgbaBitmap HalfBlackHalfWhite(int width, int height)
    {
        var bitmap = new RgbaBitmap(width, height);
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var i = (y * bitmap.Stride) + (x * 4);
                var isLeft = x < width / 2;
                bitmap.Pixels[i] = isLeft ? (byte)0 : (byte)255;
                bitmap.Pixels[i + 1] = bitmap.Pixels[i];
                bitmap.Pixels[i + 2] = bitmap.Pixels[i];
                bitmap.Pixels[i + 3] = 255;
            }
        }

        return bitmap;
    }
}
