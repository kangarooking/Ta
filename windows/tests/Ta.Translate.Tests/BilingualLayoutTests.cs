using Ta.Core.Imaging;
using Ta.Translate;
using Ta.Translate.Rendering;

namespace Ta.Translate.Tests;

/// <summary>
/// 验收 4：bilingual 布局 —— 输出高度 = 原图高 + 面板高；宽度不变。
/// 验收 5：Y 方向实测 —— 断言面板落在哪一侧。
///
/// 对应 Mac 测试 <c>TranslatedImageRendererTests.swift:24-40</c>
/// （testBilingualTranslationAppendsPanelBelowOriginal）：
/// Mac 断言输出 (4,4) 像素 == 原图 (4,4) 像素，即原图在上、面板在下。
/// Windows 像素空间同为左上原点、Y 向下（§5.1），因此该排布原样成立 ——
/// 但这里<b>显式实测</b>而不是照搬假设：源图用纯绿色填充（与白色面板完全区分），
/// 逐行检查哪一侧是源图颜色。
/// </summary>
public class BilingualLayoutTests
{
    private static readonly RgbaColor SourceColor = new(0, 200, 60);

    private static TranslatedOcrLine[] SampleLines() => new[]
    {
        TestBitmaps.TranslatedLine("Screenshot translation", "截图翻译", 0.08, 0.35, 0.72, 0.2, confidence: 0.95f),
    };

    [Fact]
    public void 输出高度等于原图高加面板高_宽度不变()
    {
        var image = TestBitmaps.Solid(360, 180, SourceColor);
        var lines = SampleLines();

        var panelHeight = TranslatedImageRenderer.MeasurePanelHeight(image.Width, lines);
        Assert.True(panelHeight > 0, "面板高度应为正。");

        var output = new TranslatedImageRenderer().Render(
            image,
            lines,
            ScreenshotTranslationMode.BilingualImage,
            "简体中文");

        Assert.Equal(image.Width, output.Width);
        Assert.Equal(image.Height + panelHeight, output.Height);
        Assert.True(output.Height > image.Height);
    }

    [Fact]
    public void Y方向实测_面板落在原图下方()
    {
        var image = TestBitmaps.Solid(360, 180, SourceColor);
        var lines = SampleLines();

        var panelHeight = TranslatedImageRenderer.MeasurePanelHeight(image.Width, lines);
        var output = new TranslatedImageRenderer().Render(
            image,
            lines,
            ScreenshotTranslationMode.BilingualImage,
            "简体中文");

        var seamRow = image.Height;

        // (a) 上侧 [0, H) 整块都是源图颜色 —— 原图完整地贴在上半。
        for (var y = 0; y < seamRow; y++)
        {
            for (var x = 0; x < output.Width; x++)
            {
                var pixel = TestBitmaps.Pixel(output, x, y);
                Assert.True(
                    pixel == SourceColor,
                    $"输出第 {y} 行第 {x} 列应为源图颜色（原图在上），实际 {pixel}。");
            }
        }

        // (b) Mac 测试的同款断言：输出 (4,4) == 原图 (4,4)。
        Assert.Equal(TestBitmaps.Pixel(image, 4, 4), TestBitmaps.Pixel(output, 4, 4));

        // (c) 接缝行是 separator（≈198 灰），不是源图色也不是纯面板底色。
        var seam = TestBitmaps.Pixel(output, 4, seamRow);
        Assert.NotEqual(SourceColor, seam);
        Assert.InRange(seam.R, 180, 220);

        // (d) 接缝下方是面板底色（247 白）—— 面板在下半。
        var panelPixel = TestBitmaps.Pixel(output, 4, seamRow + 8);
        Assert.InRange(panelPixel.R, 240, 255);
        Assert.InRange(panelPixel.G, 240, 255);
        Assert.InRange(panelPixel.B, 240, 255);

        // (e) 面板内画了文字：面板区域至少存在非底色像素。
        var hasPanelInk = false;
        for (var y = seamRow + 1; y < output.Height && !hasPanelInk; y++)
        {
            for (var x = 0; x < output.Width; x++)
            {
                var pixel = TestBitmaps.Pixel(output, x, y);
                if (Math.Abs(pixel.R - RenderColors.PanelBackground.R) > 12
                    || Math.Abs(pixel.G - RenderColors.PanelBackground.G) > 12
                    || Math.Abs(pixel.B - RenderColors.PanelBackground.B) > 12)
                {
                    hasPanelInk = true;
                    break;
                }
            }
        }

        Assert.True(hasPanelInk, "面板区域应包含标题或行文字。");
    }

    [Fact]
    public void 多行时面板高度随行数增加()
    {
        var one = new[]
        {
            TestBitmaps.TranslatedLine("One", "一", 0.1, 0.2, 0.5, 0.2),
        };
        var three = new[]
        {
            TestBitmaps.TranslatedLine("One", "一", 0.1, 0.2, 0.5, 0.1),
            TestBitmaps.TranslatedLine("Two", "二", 0.1, 0.4, 0.5, 0.1),
            TestBitmaps.TranslatedLine("Three", "三", 0.1, 0.6, 0.5, 0.1),
        };

        var heightOne = TranslatedImageRenderer.MeasurePanelHeight(360, one);
        var heightThree = TranslatedImageRenderer.MeasurePanelHeight(360, three);

        Assert.True(heightThree > heightOne, "行数越多面板越高。");
    }

    [Fact]
    public void 面板高度与渲染输出严格一致()
    {
        // 多条 + 中英混排：验证 MeasurePanelHeight 与渲染内部用的是同一套计算。
        var lines = new[]
        {
            TestBitmaps.TranslatedLine("Screenshot translation tool for macOS", "面向 macOS 的截图翻译工具", 0.05, 0.2, 0.9, 0.15),
            TestBitmaps.TranslatedLine("Bilingual panel rendering", "双语面板渲染", 0.05, 0.4, 0.9, 0.15),
            TestBitmaps.TranslatedLine("Keep original text and translation", "保留原文与译文对照", 0.05, 0.6, 0.9, 0.15),
        };
        var image = TestBitmaps.Solid(720, 200, SourceColor);

        var expected = TranslatedImageRenderer.MeasurePanelHeight(image.Width, lines);
        var output = new TranslatedImageRenderer().Render(
            image,
            lines,
            ScreenshotTranslationMode.BilingualImage,
            "简体中文");

        Assert.Equal(image.Height + expected, output.Height);
    }
}
