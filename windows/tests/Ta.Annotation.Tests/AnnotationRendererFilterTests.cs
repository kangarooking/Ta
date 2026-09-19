using Ta.Core.Agent;
using Ta.Core.Drawing;
using Ta.Core.Imaging;

namespace Ta.Annotation.Tests;

/// <summary>
/// 滤镜验证。
///
/// 覆盖参考文档 §14 风险 #8：本套滤镜是**配方版**算法
/// （两级缩放像素化 + 单遍盒式模糊），不是编辑器版的 CoreImage。
/// </summary>
public sealed class AnnotationRendererFilterTests
{
    private static readonly TaAgentAnnotationRenderer Renderer = new();

    // ── 马赛克 ──────────────────────────────────────────────────

    [Fact]
    public void 马赛克产生色块而非渐变()
    {
        // 输入是水平渐变（相邻像素差 1）—— 处理后必须出现等值色块。
        using var source = TestImages.HorizontalGradient(120, 80);

        const double scale = 10;
        var operation = AnnotationOperation.CreateMosaic(new AnnotationMosaicOperation(
            "mosaic", AnnotationMosaicMode.Rect, new AnnotationRect(10, 10, 100, 60), null, 24, scale));

        var rendered = Renderer.Render(source, null, [operation]);

        var blockSize = (int)Math.Round(scale);
        var row = 40;

        // 统计同一行内「相邻像素完全相同」的等值游程。
        var equalRuns = new List<int>();
        var run = 1;
        for (var x = 11; x < 110; x++)
        {
            var previous = TestImages.Pixel(rendered, x - 1, row).R;
            var current = TestImages.Pixel(rendered, x, row).R;
            if (previous == current)
            {
                run++;
                continue;
            }

            equalRuns.Add(run);
            run = 1;
        }

        equalRuns.Add(run);

        // 渐变输入里没有任何相邻等值像素；马赛克后必须出现长度 ≈ blockSize 的游程。
        Assert.True(equalRuns.Any(r => r >= blockSize - 1), $"最长等值游程 = {equalRuns.Max()}");

        // 块内应完全平坦（无渐变残留）。
        var blockStart = 10 + blockSize;
        Assert.Equal(
            TestImages.Pixel(rendered, blockStart, row).R,
            TestImages.Pixel(rendered, blockStart + blockSize - 1, row).R);

        // 块与块之间应有跳变。
        Assert.NotEqual(
            TestImages.Pixel(rendered, blockStart - 1, row).R,
            TestImages.Pixel(rendered, blockStart + blockSize - 1, row).R);
    }

    [Fact]
    public void 马赛克露出的是原图像素_不是已画上的标注()
    {
        using var source = TestImages.Solid(120, 80, 0, 0, 0);

        // 先画一个矩形，再用马赛克盖住同一区域 —— 马赛克块必须是纯黑（来自原图），
        // 不能把红色描边一起糊进去。
        var recipe = RecipeHelper.Of(
            AnnotationOperation.CreateRectangle(RecipeHelper.Rect(
                "rect", new AnnotationRect(10, 10, 60, 40), lineWidth: 20)),
            AnnotationOperation.CreateMosaic(new AnnotationMosaicOperation(
                "mosaic", AnnotationMosaicMode.Rect, new AnnotationRect(10, 10, 60, 40), null, 24, 8)));

        var rendered = Renderer.Render(source, recipe);

        // 区域中心在马赛克后应为纯黑。
        var (r, g, b) = TestImages.Pixel(rendered, 40, 30);
        Assert.Equal(0, r);
        Assert.Equal(0, g);
        Assert.Equal(0, b);
    }

    [Fact]
    public void 笔刷马赛克沿描边路径覆盖()
    {
        using var source = TestImages.HorizontalGradient(160, 80);

        var operation = AnnotationOperation.CreateMosaic(new AnnotationMosaicOperation(
            "mosaic", AnnotationMosaicMode.Brush, null, [new AnnotationPoint(20, 40), new AnnotationPoint(140, 40)], 20, 8));

        var rendered = Renderer.Render(source, null, [operation]);

        // 线上被马赛克成块（等值游程）。
        var run = 1;
        var longest = 1;
        for (var x = 21; x < 140; x++)
        {
            if (TestImages.Pixel(rendered, x, 40).R == TestImages.Pixel(rendered, x - 1, 40).R)
            {
                run++;
                longest = Math.Max(longest, run);
                continue;
            }

            run = 1;
        }

        Assert.True(longest >= 7, $"最长等值游程 = {longest}");

        // 线外（y = 10）仍是原始渐变。
        Assert.Equal(TestImages.Pixel(source, 60, 10).R, TestImages.Pixel(rendered, 60, 10).R);
    }

    // ── 模糊 ────────────────────────────────────────────────────

    [Fact]
    public void 模糊把硬边摊开成过渡带()
    {
        // 左黑右白，硬边在 x = 60。
        using var source = TestImages.SplitVertical(120, 60, 60);

        const double radius = 8;
        var operation = AnnotationOperation.CreateBlur(new AnnotationBlurOperation(
            "blur", new AnnotationRect(0, 0, 120, 60), radius));

        var rendered = Renderer.Render(source, null, [operation]);

        var row = 30;

        // 边缘扩展夹取：远离硬边的左侧仍应是纯黑。
        Assert.Equal(0, TestImages.Pixel(rendered, 0, row).R);
        Assert.Equal(0, TestImages.Pixel(rendered, 20, row).R);

        // 过渡带宽度应接近核宽 max(3, round(2*8+1)) = 17。
        var transition = 0;
        for (var x = 1; x < 120; x++)
        {
            var value = TestImages.Pixel(rendered, x, row).R;
            if (value > 0 && value < 255)
            {
                transition++;
            }
        }

        Assert.InRange(transition, 13, 21);

        // 过渡带中点应接近中间灰。
        var middle = TestImages.Pixel(rendered, 60, row).R;
        Assert.InRange(middle, 100, 155);
    }

    [Fact]
    public void 可分离两遍盒式与单遍二维盒式一致_误差不超过一()
    {
        // 独立算一遍单遍二维盒式（双精度），再与渲染器输出比对，
        // 验证「可分离 2 遍等价于单遍」这个论断只带来 ≤1 的舍入差。
        using var source = TestImages.Checker(80, 60);

        const double radius = 6;
        var operation = AnnotationOperation.CreateBlur(new AnnotationBlurOperation(
            "blur", new AnnotationRect(0, 0, 80, 60), radius));

        var rendered = Renderer.Render(source, null, [operation]);

        var kernel = (int)Math.Round((radius * 2) + 1, MidpointRounding.AwayFromZero);
        var half = kernel / 2;
        var maxDelta = 0;

        for (var y = 0; y < source.Height; y++)
        {
            for (var x = 0; x < source.Width; x++)
            {
                for (var channel = 0; channel < 3; channel++)
                {
                    var sum = 0.0;
                    for (var ty = -half; ty <= half; ty++)
                    {
                        for (var tx = -half; tx <= half; tx++)
                        {
                            var sx = Math.Clamp(x + tx, 0, source.Width - 1);
                            var sy = Math.Clamp(y + ty, 0, source.Height - 1);
                            var index = (sy * source.Stride) + (sx * RgbaBitmap.BytesPerPixel);
                            sum += source.Pixels[index + channel];
                        }
                    }

                    var expected = (int)Math.Round(sum / (kernel * kernel));
                    var actual = channel switch
                    {
                        0 => TestImages.Pixel(rendered, x, y).R,
                        1 => TestImages.Pixel(rendered, x, y).G,
                        _ => TestImages.Pixel(rendered, x, y).B,
                    };

                    maxDelta = Math.Max(maxDelta, Math.Abs(expected - actual));
                }
            }
        }

        Assert.True(maxDelta <= 1, $"最大通道差 = {maxDelta}");
    }

    [Fact]
    public void 半径过小时核宽强制为三且为奇数()
    {
        using var source = TestImages.SplitVertical(60, 40, 30);

        // radius = 0.1 → max(3, round(1.2)) = 3。
        var operation = AnnotationOperation.CreateBlur(new AnnotationBlurOperation(
            "blur", new AnnotationRect(0, 0, 60, 40), 0.1));

        var rendered = Renderer.Render(source, null, [operation]);

        // 核宽 3 的过渡带：x = 29 应是 0 或 1/3 灰，x = 31 应接近 2/3 白。
        var left = TestImages.Pixel(rendered, 29, 20).R;
        var right = TestImages.Pixel(rendered, 31, 20).R;
        Assert.True(left < 100, $"left = {left}");
        Assert.True(right > 155, $"right = {right}");
    }

    [Fact]
    public void 模糊露出的是原图像素_不是已画上的标注()
    {
        using var source = TestImages.Solid(120, 80, 0, 0, 0);

        var recipe = RecipeHelper.Of(
            AnnotationOperation.CreateRectangle(RecipeHelper.Rect(
                "rect", new AnnotationRect(10, 10, 60, 40), lineWidth: 20)),
            AnnotationOperation.CreateBlur(new AnnotationBlurOperation(
                "blur", new AnnotationRect(10, 10, 60, 40), 8)));

        var rendered = Renderer.Render(source, recipe);

        var (r, g, b) = TestImages.Pixel(rendered, 40, 30);
        Assert.Equal(0, r);
        Assert.Equal(0, g);
        Assert.Equal(0, b);
    }
}
