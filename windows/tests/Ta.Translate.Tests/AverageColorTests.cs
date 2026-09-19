using Ta.Core.Capture;
using Ta.Core.Imaging;
using Ta.Translate;
using Ta.Translate.Rendering;

namespace Ta.Translate.Tests;

/// <summary>
/// 验收 2：averageColor —— 左右黑白各半的区域，均值约 128。
/// 对应 Mac: TranslatedImageRenderer.averageColor（:157-185）—— 1×1 降采样（盒式平均）。
/// </summary>
public class AverageColorTests
{
    [Fact]
    public void 左右黑白各半的区域均值为128()
    {
        var image = CreateHalfBlackHalfWhite(100, 50);

        var average = TranslatedImageRenderer.AverageColor(image, new RectD(0, 0, 100, 50));

        // 5000 个黑 + 5000 个白：和 = 5000*255 = 1,275,000；/10000 = 127.5 → 127（整数除法截断）。
        Assert.InRange(average.R, 126, 129);
        Assert.InRange(average.G, 126, 129);
        Assert.InRange(average.B, 126, 129);
    }

    [Fact]
    public void 纯色区域均值即该色()
    {
        var image = TestBitmaps.Solid(40, 20, new RgbaColor(10, 200, 90));

        var average = TranslatedImageRenderer.AverageColor(image, new RectD(0, 0, 40, 20));

        Assert.Equal(10, average.R);
        Assert.Equal(200, average.G);
        Assert.Equal(90, average.B);
    }

    [Fact]
    public void 越界区域被夹取到图像内()
    {
        var image = CreateHalfBlackHalfWhite(100, 50);

        // 请求 (-10, -10, 120, 70)：应被夹取到整图，均值仍约 128。
        var average = TranslatedImageRenderer.AverageColor(image, new RectD(-10, -10, 120, 70));

        Assert.InRange(average.R, 126, 129);
    }

    [Fact]
    public void 完全在图像外的区域回退为白色()
    {
        var image = CreateHalfBlackHalfWhite(100, 50);

        // 对应 Mac 裁剪失败时的兜底 .white（:174）。
        var average = TranslatedImageRenderer.AverageColor(image, new RectD(200, 200, 50, 50));

        Assert.Equal(255, average.R);
        Assert.Equal(255, average.G);
        Assert.Equal(255, average.B);
    }

    [Fact]
    public void 子区域只统计框内像素()
    {
        var image = CreateHalfBlackHalfWhite(100, 50);

        // 只取左半（纯黑）。
        var left = TranslatedImageRenderer.AverageColor(image, new RectD(0, 0, 50, 50));
        Assert.Equal(0, left.R);

        // 只取右半（纯白）。
        var right = TranslatedImageRenderer.AverageColor(image, new RectD(50, 0, 50, 50));
        Assert.Equal(255, right.R);
    }

    private static RgbaBitmap CreateHalfBlackHalfWhite(int width, int height)
    {
        var bitmap = new RgbaBitmap(width, height);
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var i = (y * bitmap.Stride) + (x * 4);
                var value = x < width / 2 ? (byte)0 : (byte)255;
                bitmap.Pixels[i] = value;
                bitmap.Pixels[i + 1] = value;
                bitmap.Pixels[i + 2] = value;
                bitmap.Pixels[i + 3] = 255;
            }
        }

        return bitmap;
    }
}

/// <summary>
/// fullImage 渲染行为（与 Mac 测试 TranslatedImageRendererTests 对齐）。
/// </summary>
public class FullImageRenderTests
{
    [Fact]
    public void 全文翻译保持原图尺寸()
    {
        // Mac: testFullTranslationPreservesOriginalDimensions（:7-22）。
        var image = TestBitmaps.Solid(320, 160, new RgbaColor(0, 90, 200));
        var lines = new[]
        {
            TestBitmaps.TranslatedLine("Hello", "你好", 0.1, 0.35, 0.4, 0.24, confidence: 0.95f),
        };

        var output = new TranslatedImageRenderer().Render(
            image,
            lines,
            ScreenshotTranslationMode.FullImage,
            "简体中文");

        Assert.Equal(image.Width, output.Width);
        Assert.Equal(image.Height, output.Height);
    }

    [Fact]
    public void 空文字行时报missingTextBoxes()
    {
        // Mac: render 的 guard !lines.isEmpty（:18）。
        var image = TestBitmaps.Solid(64, 32, new RgbaColor(255, 255, 255));

        var error = Assert.Throws<ScreenshotTranslationServiceError>(() =>
            new TranslatedImageRenderer().Render(
                image,
                Array.Empty<TranslatedOcrLine>(),
                ScreenshotTranslationMode.FullImage,
                "简体中文"));

        Assert.Equal(ScreenshotTranslationServiceError.Code.MissingTextBoxes, error.ErrorCode);
    }

    [Fact]
    public void 文字盒小于4x4时跳过不崩溃()
    {
        // 归一化 bbox 极小 → 扩张+求交后仍 < 4×4 → 跳过（Mac: :43）。
        var image = TestBitmaps.Solid(64, 32, new RgbaColor(255, 255, 255));
        var lines = new[]
        {
            TestBitmaps.TranslatedLine("a", "甲", 0.0, 0.0, 0.005, 0.01),
        };

        var output = new TranslatedImageRenderer().Render(
            image,
            lines,
            ScreenshotTranslationMode.FullImage,
            "简体中文");

        Assert.Equal(64, output.Width);
        Assert.Equal(32, output.Height);
    }

    [Fact]
    public void textOnly模式原样返回图像()
    {
        // Mac: .textOnly → return image（:20-21）。
        var image = TestBitmaps.Solid(64, 32, new RgbaColor(12, 34, 56));
        var lines = new[]
        {
            TestBitmaps.TranslatedLine("a", "甲", 0.1, 0.1, 0.5, 0.5),
        };

        var output = new TranslatedImageRenderer().Render(
            image,
            lines,
            ScreenshotTranslationMode.TextOnly,
            "简体中文");

        Assert.Same(image, output);
    }

    [Fact]
    public void 译文覆盖在文字盒位置写入像素()
    {
        // 亮背景（白）+ 中部文字盒 → 覆盖后该区域应出现文字像素（不再是纯白）。
        var image = TestBitmaps.Solid(200, 100, new RgbaColor(255, 255, 255));
        var lines = new[]
        {
            TestBitmaps.TranslatedLine("Hello world screenshot", "你好世界截图翻译", 0.2, 0.35, 0.6, 0.3),
        };

        var output = new TranslatedImageRenderer().Render(
            image,
            lines,
            ScreenshotTranslationMode.FullImage,
            "简体中文");

        // 盒内（扩张后）至少存在一个非白像素 —— 证明底色/文字真的画上去了。
        var hasInk = false;
        for (var y = 40; y < 70; y++)
        {
            for (var x = 40; x < 160; x++)
            {
                var pixel = TestBitmaps.Pixel(output, x, y);
                if (pixel.R != 255 || pixel.G != 255 || pixel.B != 255)
                {
                    hasInk = true;
                    break;
                }
            }

            if (hasInk)
            {
                break;
            }
        }

        Assert.True(hasInk, "文字盒区域内应当出现覆盖后的像素。");
    }
}
