using Ta.OCR.Models;
using Ta.OCR.Text;
using Xunit;

namespace Ta.OCR.Tests.PostProcessing;

/// <summary>
/// <see cref="OcrResultAssembler"/> 测试。
///
/// 锁定 Mac 版 <c>VisionOCRService.recognizeLegacy</c> 收尾那段管线的**产出优先级**
/// （<c>VisionOCRService.swift:73-108</c>）：规范化 → 置信度均值 → 条码顶替 → 表格覆盖 → 内容类型。
/// </summary>
public class OcrResultAssemblerTests
{
    private readonly OcrResultAssembler _assembler = new();

    private static OCRTextLine Line(string text, double confidence, double x, double y)
        => new(text, confidence, new NormalizedRect(x, y, 0.2, 0.04));

    [Fact]
    public void 无行时置信度为0()
    {
        var result = _assembler.Assemble([], [], mergeWrappedLines: false);

        Assert.Equal(0, result.Confidence);
        Assert.Equal(string.Empty, result.Text);
        // 空文本 → classify 得到 image。
        Assert.Equal(CaptureContentType.Image, result.ContentType);
        Assert.True(result.IsLowConfidence);
    }

    [Fact]
    public void 置信度是行置信度的算术平均()
    {
        var result = _assembler.Assemble(
            [Line("a", 0.9, 0.1, 0.8), Line("b", 0.7, 0.1, 0.7)],
            [],
            mergeWrappedLines: false);

        Assert.Equal(0.8, result.Confidence, 10);
        Assert.False(result.IsLowConfidence);
    }

    [Fact]
    public void 低于072判为低置信度()
    {
        var result = _assembler.Assemble([Line("a", 0.71, 0.1, 0.8)], [], mergeWrappedLines: false);

        Assert.True(result.IsLowConfidence);
    }

    [Fact]
    public void 等于072不算低置信度()
    {
        // Mac: confidence < 0.72，严格小于。
        var result = _assembler.Assemble([Line("a", 0.72, 0.1, 0.8)], [], mergeWrappedLines: false);

        Assert.False(result.IsLowConfidence);
    }

    [Fact]
    public void 正文经过规范化()
    {
        var result = _assembler.Assemble(
            [Line("a   ", 1, 0.1, 0.8), Line("", 1, 0.1, 0.7), Line("b", 1, 0.1, 0.6)],
            [],
            mergeWrappedLines: false);

        Assert.Equal("a\n\nb", result.Text);
    }

    [Fact]
    public void mergeWrappedLines透传给规范化器()
    {
        var result = _assembler.Assemble(
            [Line("hello", 1, 0.1, 0.8), Line("world", 1, 0.1, 0.7)],
            [],
            mergeWrappedLines: true);

        Assert.Equal("hello world", result.Text);
    }

    [Fact]
    public void 文字为空但有条码时用payload顶替()
    {
        var barcodes = new[] { new DetectedBarcode("https://a.b", "QR", NormalizedRect.Zero) };

        var result = _assembler.Assemble([], barcodes, mergeWrappedLines: false);

        Assert.Equal("https://a.b", result.Text);
        Assert.Equal(CaptureContentType.QrCode, result.ContentType);
    }

    [Fact]
    public void 有条码但同时有文字时不算qrCode()
    {
        var barcodes = new[] { new DetectedBarcode("https://a.b", "QR", NormalizedRect.Zero) };

        var result = _assembler.Assemble([Line("正文", 1, 0.1, 0.8)], barcodes, mergeWrappedLines: false);

        Assert.NotEqual(CaptureContentType.QrCode, result.ContentType);
        Assert.Equal("正文", result.Text);
    }

    [Fact]
    public void 多个条码payload按换行连接()
    {
        var barcodes = new[]
        {
            new DetectedBarcode("one", "QR", NormalizedRect.Zero),
            new DetectedBarcode("two", "EAN13", NormalizedRect.Zero),
        };

        var result = _assembler.Assemble([], barcodes, mergeWrappedLines: false);

        Assert.Equal("one\ntwo", result.Text);
    }

    [Fact]
    public void 检出表格时用TSV覆盖正文()
    {
        // 两行各含两个左右分布的小块 → 分析器判为表格。
        var lines = new[]
        {
            Line("Name", 1, 0.10, 0.80),
            Line("Score", 1, 0.55, 0.80),
            Line("Alice", 1, 0.10, 0.70),
            Line("98", 1, 0.55, 0.70),
        };

        var result = _assembler.Assemble(lines, [], mergeWrappedLines: false);

        Assert.Equal("Name\tScore\nAlice\t98", result.Text);
        Assert.Equal(CaptureContentType.Table, result.ContentType);
        Assert.Single(result.Document.Tables);
    }

    [Fact]
    public void 有表格时内容类型为table即使文本像代码()
    {
        var lines = new[]
        {
            Line("func a()", 1, 0.10, 0.80),
            Line("func b()", 1, 0.55, 0.80),
            Line("let x", 1, 0.10, 0.70),
            Line("let y", 1, 0.55, 0.70),
        };

        var result = _assembler.Assemble(lines, [], mergeWrappedLines: false);

        Assert.Equal(CaptureContentType.Table, result.ContentType);
    }

    [Fact]
    public void 普通文本交给分类器()
    {
        var result = _assembler.Assemble(
            [Line("def add(a, b):", 1, 0.1, 0.8), Line("    return a + b", 1, 0.1, 0.7)],
            [],
            mergeWrappedLines: false);

        Assert.Equal(CaptureContentType.Code, result.ContentType);
    }

    [Fact]
    public void 引擎与语言回填到结果()
    {
        var result = _assembler.Assemble(
            [Line("a", 1, 0.1, 0.8)],
            [],
            mergeWrappedLines: false,
            languages: ["zh-Hans", "en-US"],
            engine: OCREnginePreference.PaddleOCR);

        Assert.Equal(OCREnginePreference.PaddleOCR, result.Engine);
        Assert.Equal(["zh-Hans", "en-US"], result.Languages);
    }

    [Fact]
    public void 语言缺省为空数组()
    {
        var result = _assembler.Assemble([Line("a", 1, 0.1, 0.8)], [], mergeWrappedLines: false);

        Assert.Empty(result.Languages);
    }

    [Fact]
    public void 合成置信度标记会被带上()
    {
        var real = _assembler.Assemble([Line("a", 0.9, 0.1, 0.8)], [], mergeWrappedLines: false);
        var synthetic = _assembler.Assemble(
            [Line("a", 0.9, 0.1, 0.8)], [], mergeWrappedLines: false, confidenceIsSynthetic: true);

        Assert.False(real.ConfidenceIsSynthetic);
        Assert.True(synthetic.ConfidenceIsSynthetic);
    }
}
