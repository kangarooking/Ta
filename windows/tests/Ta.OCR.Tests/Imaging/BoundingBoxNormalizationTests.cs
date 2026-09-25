using Ta.OCR.Models;
using Xunit;

namespace Ta.OCR.Tests.Imaging;

/// <summary>
/// <see cref="NormalizedRect.FromImagePixels"/> 测试 —— <b>B 部分必须测掉的纯函数</b>。
///
/// 这是参考文档 §14 风险 #33 的核心：WinRT 的 <c>OcrWord.BoundingRect</c> 是
/// <b>图像像素坐标、原点左上、Y 向下</b>，而 Mac 的 <c>boundingBox</c> 是
/// <b>归一化坐标（0..1）、原点左下、Y 向上</b>。
/// 换算错一个符号，<c>OCRDocumentLayoutAnalyzer</c> 的每个阈值都会反过来。
/// </summary>
public class BoundingBoxNormalizationTests
{
    private const int Width = 200;
    private const int Height = 100;

    [Fact]
    public void 左上角像素矩形换算到归一化左上角()
    {
        // WinRT: x=0, y=0, w=20, h=10（图像左上角的 20×10 块）
        var box = NormalizedRect.FromImagePixels(0, 0, 20, 10, Width, Height);

        Assert.Equal(0.0, box.MinX, 10);
        // Y 翻转后，下边界 = 1 - (0 + 10)/100 = 0.9
        Assert.Equal(0.9, box.MinY, 10);
        Assert.Equal(0.1, box.Width, 10);
        Assert.Equal(0.1, box.Height, 10);
        Assert.Equal(0.1, box.MaxX, 10);
        Assert.Equal(1.0, box.MaxY, 10);
    }

    [Fact]
    public void 右下角像素矩形换算到归一化右下角()
    {
        // WinRT: x=180, y=90, w=20, h=10
        var box = NormalizedRect.FromImagePixels(180, 90, 20, 10, Width, Height);

        Assert.Equal(0.9, box.MinX, 10);
        Assert.Equal(0.0, box.MinY, 10);
        Assert.Equal(1.0, box.MaxX, 10);
        Assert.Equal(0.1, box.MaxY, 10);
    }

    [Fact]
    public void 整个图像换算到满框()
    {
        var box = NormalizedRect.FromImagePixels(0, 0, Width, Height, Width, Height);

        Assert.Equal(0.0, box.MinX, 10);
        Assert.Equal(0.0, box.MinY, 10);
        Assert.Equal(1.0, box.MaxX, 10);
        Assert.Equal(1.0, box.MaxY, 10);
    }

    [Fact]
    public void Y轴方向与Mac一致_上方矩形有更大的minY()
    {
        // 图像里 y 小 = 位置靠上；归一化后 minY 应该更大（原点在下）。
        var top = NormalizedRect.FromImagePixels(10, 10, 20, 10, Width, Height);
        var bottom = NormalizedRect.FromImagePixels(10, 80, 20, 10, Width, Height);

        Assert.True(top.MinY > bottom.MinY);
        Assert.True(top.MidY > bottom.MidY);
    }

    [Fact]
    public void 对应本机实测的WinRT词框()
    {
        // 本机跑 Windows.Media.Ocr 的实测输出（600×120 图）：
        //   word='Hello' rect=(6,25,90,30) → union px=(6,20)-(257,55)
        //   normalized minX=0.0100 minY=0.5417 w=0.4183 h=0.2917
        var hello = NormalizedRect.FromImagePixels(6, 25, 90, 30, 600, 120);
        var box = hello.Union(NormalizedRect.FromImagePixels(118, 21, 49, 28, 600, 120));
        box = box.Union(NormalizedRect.FromImagePixels(188, 20, 69, 25, 600, 120));

        Assert.Equal(0.0100, box.MinX, 4);
        Assert.Equal(0.5417, box.MinY, 4);
        Assert.Equal(0.4183, box.Width, 4);
        Assert.Equal(0.2917, box.Height, 4);
    }

    [Fact]
    public void 图像尺寸非法时返回零矩形()
    {
        Assert.Equal(NormalizedRect.Zero, NormalizedRect.FromImagePixels(1, 1, 2, 2, 0, 100));
        Assert.Equal(NormalizedRect.Zero, NormalizedRect.FromImagePixels(1, 1, 2, 2, 100, 0));
        Assert.Equal(NormalizedRect.Zero, NormalizedRect.FromImagePixels(1, 1, 2, 2, -1, -1));
    }

    [Fact]
    public void 不夹取越界值()
    {
        // Vision 的归一化框本身可能轻微越界；换算不夹取，保持与 Mac 相同的数值行为。
        var box = NormalizedRect.FromImagePixels(-5, -5, 210, 110, Width, Height);

        Assert.True(box.MinX < 0);
        Assert.True(box.MaxY > 1);
    }

    [Fact]
    public void 并集取外接框()
    {
        var a = new NormalizedRect(0.1, 0.2, 0.2, 0.1);   // maxX=0.3 maxY=0.3
        var b = new NormalizedRect(0.4, 0.1, 0.2, 0.3);   // maxX=0.6 maxY=0.4

        var union = a.Union(b);

        Assert.Equal(0.1, union.MinX, 10);
        Assert.Equal(0.1, union.MinY, 10);
        Assert.Equal(0.6, union.MaxX, 10);
        Assert.Equal(0.4, union.MaxY, 10);
        Assert.Equal(0.5, union.Width, 10);
        Assert.Equal(0.3, union.Height, 10);
    }

    [Fact]
    public void 与自身求并集等于自身()
    {
        // ⚠️ 用逐字段比较而不是 record 相等：Union 是通过 min/max 重建矩形的，
        // 0.1 + 0.3 - 0.1 在二进制浮点下是 0.30000000000000004。
        // 这个误差量级远小于分析器的所有阈值（0.012 / 0.28 / 2.4×），
        // 对版面判断没有影响，所以不值得为它加「包含即短路」的特例分支。
        var a = new NormalizedRect(0.1, 0.2, 0.3, 0.4);

        var union = a.Union(a);

        Assert.Equal(a.MinX, union.MinX, 12);
        Assert.Equal(a.MinY, union.MinY, 12);
        Assert.Equal(a.MaxX, union.MaxX, 12);
        Assert.Equal(a.MaxY, union.MaxY, 12);
    }

    [Fact]
    public void 与零矩形求并集会张开到原点()
    {
        // ⚠️ 这不是 bug，是并集的定义。真实的调用路径（WindowsMediaOcrEngine）
        // 只在拿到**真实词框**之后才 Union，第一个词框直接赋值而不与 Zero 求并，
        // 所以 Zero 永远不会参与运算。这个测试把该前提钉住。
        var a = new NormalizedRect(0.1, 0.2, 0.3, 0.4);

        Assert.NotEqual(a, a.Union(NormalizedRect.Zero));
    }

    [Fact]
    public void midY是上下边界的中点()
    {
        var box = new NormalizedRect(0.1, 0.2, 0.3, 0.4);

        Assert.Equal(0.4, box.MidY, 10);
    }
}
