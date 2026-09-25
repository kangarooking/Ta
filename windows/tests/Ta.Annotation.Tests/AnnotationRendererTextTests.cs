using Ta.Core.Agent;
using Ta.Core.Drawing;
using Ta.Core.Imaging;

namespace Ta.Annotation.Tests;

/// <summary>
/// 文字渲染验证。
///
/// ⚠️ 保真度说明（参考文档 §14 风险 #12）：
/// Mac 用 CoreText + PingFang SC，这里用 Win32 GetGlyphOutlineW + 系统消息框字体。
/// 字形外形与度量**不可能**逐像素一致。本测试只锁定**能做到且必须对**的部分：
///   · 有墨迹（字形真的被画出来）
///   · 基线位置正确（origin.y + fontSize，Y 不翻转）
///   · 中文字形能取到轮廓（中文回退链有效）
///   · 编号数字居中且为白色
/// </summary>
public sealed class AnnotationRendererTextTests
{
    private static readonly TaAgentAnnotationRenderer Renderer = new();

    [Fact]
    public void 中文字形被画出来()
    {
        using var source = TestImages.Solid(200, 100, 255, 255, 255);

        var operation = AnnotationOperation.CreateText(new AnnotationTextOperation(
            "label", new AnnotationPoint(10, 10), "拓：重点功能", AnnotationColor.Red_, 28));

        var rendered = Renderer.Render(source, null, [operation]);

        var inked = 0;
        for (var y = 0; y < rendered.Height; y++)
        {
            for (var x = 0; x < rendered.Width; x++)
            {
                var (r, g, b) = TestImages.Pixel(rendered, x, y);
                if (r > 200 && g < 120 && b < 120)
                {
                    inked++;
                }
            }
        }

        // 6 个 28px 汉字 + 冒号，墨迹面积应在数百像素量级。
        Assert.True(inked > 300, $"中文字形墨迹像素 = {inked}");
    }

    [Fact]
    public void 基线在origin加字号处_不翻转()
    {
        using var source = TestImages.Solid(200, 200, 255, 255, 255);

        // 靠近画布顶部：基线 = 10 + 28 = 38，墨迹应落在上半部。
        var top = Renderer.Render(source, null, [AnnotationOperation.CreateText(
            new AnnotationTextOperation("t", new AnnotationPoint(10, 10), "拓", AnnotationColor.Red_, 28))]);

        var topBounds = TestImages.ChangedBounds(top, source);
        Assert.False(topBounds.Empty);

        // 基线 = origin.y + fontSize = 38，中文字形顶部约在基线上方 0.88em 处
        // → 墨迹起始行应在 8..30 之间。若 Y 被翻转，墨迹会跑到画布下半部甚至溢出。
        Assert.InRange(topBounds.MinY, 8, 30);
        Assert.True(topBounds.MaxY < 60, $"顶部文字的墨迹结束行 = {topBounds.MaxY}");

        // 靠近画布底部：基线 = 170 + 28 = 198，墨迹应落在下半部。
        var bottom = Renderer.Render(source, null, [AnnotationOperation.CreateText(
            new AnnotationTextOperation("t", new AnnotationPoint(10, 170), "拓", AnnotationColor.Red_, 28))]);

        var bottomBounds = TestImages.ChangedBounds(bottom, source);
        Assert.False(bottomBounds.Empty);
        Assert.True(bottomBounds.MinY > 120, $"底部文字的墨迹起始行 = {bottomBounds.MinY}");
        Assert.True(bottomBounds.MaxY <= 199, $"底部文字的墨迹结束行 = {bottomBounds.MaxY}");
    }

    [Fact]
    public void 文字从origin横向开始()
    {
        using var source = TestImages.Solid(200, 100, 255, 255, 255);

        var rendered = Renderer.Render(source, null, [AnnotationOperation.CreateText(
            new AnnotationTextOperation("t", new AnnotationPoint(40, 10), "ABC", AnnotationColor.Red_, 28))]);

        var bounds = TestImages.ChangedBounds(rendered, source);
        Assert.False(bounds.Empty);
        Assert.InRange(bounds.MinX, 36, 46);
    }

    [Fact]
    public void 拉丁文字也能取到字形()
    {
        using var source = TestImages.Solid(200, 100, 255, 255, 255);

        var rendered = Renderer.Render(source, null, [AnnotationOperation.CreateText(
            new AnnotationTextOperation("t", new AnnotationPoint(10, 10), "Hello Ta", AnnotationColor.Red_, 24))]);

        Assert.False(TestImages.ChangedBounds(rendered, source).Empty);
    }

    [Fact]
    public void 度量返回正宽度与高度()
    {
        var chinese = GlyphRasterizer.Measure("拓：重点功能", 28, bold: false);
        var latin = GlyphRasterizer.Measure("Hello Ta", 24, bold: false);
        var bold = GlyphRasterizer.Measure("7", 22, bold: true);

        Assert.True(chinese.Width > 0, $"中文宽度 = {chinese.Width}");
        Assert.True(chinese.Height > 0, $"中文高度 = {chinese.Height}");
        Assert.True(latin.Width > 0, $"拉丁宽度 = {latin.Width}");
        Assert.True(bold.Width > 0, $"粗体数字宽度 = {bold.Width}");

        // 6 个全角字符在 28px 下应明显宽于 8 个 24px 拉丁字符。
        Assert.True(chinese.Width > latin.Width, $"中文宽度 = {chinese.Width}，拉丁宽度 = {latin.Width}");

        // 高度应接近字号（ascent + descent）。
        Assert.InRange(chinese.Height, 28 * 0.9, 28 * 1.6);
    }

    [Fact]
    public void 空串度量为零()
    {
        var measurement = GlyphRasterizer.Measure(string.Empty, 24, bold: false);
        Assert.Equal(0, measurement.Width);
    }

    [Fact]
    public void 编号数字在气泡内居中且为白色()
    {
        using var source = TestImages.Solid(120, 90, 128, 128, 128);

        var rendered = Renderer.Render(source, null, [AnnotationOperation.CreateNumber(
            new AnnotationNumberOperation("n", new AnnotationPoint(60, 45), 3, AnnotationColor.Red_, 40))]);

        // 数字字号 = 直径 × 0.56 = 22.4。
        var bounds = FindWhiteBounds(rendered);
        Assert.False(bounds.Empty);

        // 白色墨迹应大致居中于 (60,45)。
        var centerX = (bounds.MinX + bounds.MaxX) / 2.0;
        var centerY = (bounds.MinY + bounds.MaxY) / 2.0;
        Assert.InRange(centerX, 55, 65);
        Assert.InRange(centerY, 40, 50);
    }

    private static (int MinX, int MinY, int MaxX, int MaxY, bool Empty) FindWhiteBounds(RgbaBitmap bitmap)
    {
        var minX = int.MaxValue;
        var minY = int.MaxValue;
        var maxX = int.MinValue;
        var maxY = int.MinValue;

        for (var y = 0; y < bitmap.Height; y++)
        {
            for (var x = 0; x < bitmap.Width; x++)
            {
                var (r, g, b) = TestImages.Pixel(bitmap, x, y);
                if (r < 240 || g < 240 || b < 240)
                {
                    continue;
                }

                if (x < minX) minX = x;
                if (x > maxX) maxX = x;
                if (y < minY) minY = y;
                if (y > maxY) maxY = y;
            }
        }

        return (minX, minY, maxX, maxY, minX == int.MaxValue);
    }
}
