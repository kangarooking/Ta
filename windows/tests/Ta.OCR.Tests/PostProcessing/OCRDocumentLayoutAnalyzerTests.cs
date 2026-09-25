using Ta.OCR.Models;
using Ta.OCR.Text;
using Xunit;

namespace Ta.OCR.Tests.PostProcessing;

/// <summary>
/// <see cref="OCRDocumentLayoutAnalyzer"/> 测试。
///
/// 前 3 条断言**逐字对应** Mac 版
/// <c>Tests/AIScreenshotCoreTests/OCRDocumentLayoutAnalyzerTests.swift:6-45</c>，
/// 输入行（文本 + x + y）与期望结果完全一致，包括
/// <c>line(_:x:y:)</c> 那个固定的 0.25 × 0.04 归一化框。
/// </summary>
public class OCRDocumentLayoutAnalyzerTests
{
    private readonly OCRDocumentLayoutAnalyzer _analyzer = new();

    // ── Mac 逐字对应 ────────────────────────────────────────────────

    [Fact]
    public void Mac_检出表格的行与单元格()
    {
        // OCRDocumentLayoutAnalyzerTests.swift:6-17
        var lines = new[]
        {
            Line("Name", 0.1, 0.8), Line("Score", 0.55, 0.8),
            Line("Alice", 0.1, 0.7), Line("98", 0.55, 0.7),
            Line("Bob", 0.1, 0.6), Line("95", 0.55, 0.6),
        };

        var document = _analyzer.Analyze(lines);

        var table = Assert.Single(document.Tables);
        Assert.Equal(
            new[] { new[] { "Name", "Score" }, new[] { "Alice", "98" }, new[] { "Bob", "95" } },
            table.Rows);
        Assert.Contains("| Name | Score |", table.Markdown);
    }

    [Fact]
    public void Mac_按大垂直间距切分段落()
    {
        // :19-30
        var lines = new[]
        {
            Line("Heading", 0.1, 0.85),
            Line("First paragraph", 0.1, 0.78),
            Line("Second section", 0.1, 0.42),
        };

        var document = _analyzer.Analyze(lines);

        Assert.Equal(2, document.Blocks.Count);
        Assert.Equal("Second section", document.Blocks[^1].Text);
    }

    [Fact]
    public void Mac_按空白拆分单元格()
    {
        // :32-41
        var lines = new[]
        {
            Line("Product   Price   Qty", 0.1, 0.8),
            Line("Tea       12      2", 0.1, 0.7),
        };

        var document = _analyzer.Analyze(lines);

        Assert.Equal(new[] { "Product", "Price", "Qty" }, Assert.Single(document.Tables).Rows[0]);
    }

    // ── 分块阈值 ───────────────────────────────────────────────────

    [Fact]
    public void 空输入得到空版面()
    {
        var document = _analyzer.Analyze([]);

        Assert.Empty(document.Blocks);
        Assert.Empty(document.Tables);
    }

    [Fact]
    public void 单行只产生一个块()
    {
        var document = _analyzer.Analyze([Line("only", 0.1, 0.5)]);

        var block = Assert.Single(document.Blocks);
        Assert.Equal("only", block.Text);
    }

    [Fact]
    public void 垂直间距恰好等于阈值时不切块()
    {
        // verticalGap > typicalHeight * 2.4 是**严格大于**。
        // 两行都是 height = 0.04 → typicalHeight = 0.04，阈值 = 0.096。
        // 行1 minY = 0.7，行2 maxY = 0.604 → gap = 0.096，不切。
        var document = _analyzer.Analyze([Line("a", 0.1, 0.74), Line("b", 0.1, 0.7)]);

        Assert.Single(document.Blocks);
    }

    [Fact]
    public void 垂直间距超过阈值时切块()
    {
        // 行1 minY = 0.7，行2 maxY = 0.5 → gap = 0.2 > 0.096，切。
        var document = _analyzer.Analyze([Line("a", 0.1, 0.74), Line("b", 0.1, 0.5)]);

        Assert.Equal(2, document.Blocks.Count);
    }

    [Fact]
    public void typicalHeight下限为001()
    {
        // 两行 height = 0 → typicalHeight = max(0.01, 0) = 0.01，阈值 = 0.024。
        // gap = 0.02 < 0.024 → 不切。若没有这个下限就会切。
        var document = _analyzer.Analyze([
            new OCRTextLine("a", 1, new NormalizedRect(0.1, 0.52, 0.2, 0)),
            new OCRTextLine("b", 1, new NormalizedRect(0.1, 0.5, 0.2, 0)),
        ]);

        Assert.Single(document.Blocks);
    }

    [Fact]
    public void 缩进变化超过028时切块()
    {
        // |ΔminX| > 0.28。0.1 → 0.39，差 0.29 > 0.28，切。
        var document = _analyzer.Analyze([Line("a", 0.1, 0.5), Line("b", 0.39, 0.46)]);

        Assert.Equal(2, document.Blocks.Count);
    }

    [Fact]
    public void 缩进变化恰好028时不切块()
    {
        // 0.1 → 0.38，差 0.28，不满足严格大于。
        var document = _analyzer.Analyze([Line("a", 0.1, 0.5), Line("b", 0.38, 0.46)]);

        Assert.Single(document.Blocks);
    }

    // ── 分行容差 ───────────────────────────────────────────────────

    [Fact]
    public void midY差在容差内的两行归入同一视觉行()
    {
        // height = 0.04 → 容差 = max(0.012, 0.026) = 0.026。
        // "left" midY = 0.76，"right" midY = 0.745，差 0.015 < 0.026 → 同一视觉行。
        // 但**只有一个**多格行，仍不构成表格（需 ≥2 个多格行）。
        var document = _analyzer.Analyze([Line("left", 0.1, 0.74), Line("right", 0.55, 0.725)]);

        Assert.Empty(document.Tables);
    }

    [Fact]
    public void 同一视觉行的左右两块拆成两个单元格()
    {
        // 两行都是「左右两块」→ 两个多格行 → 构成 2×2 表格。
        var document = _analyzer.Analyze([
            Line("left", 0.1, 0.74), Line("right", 0.55, 0.725),
            Line("a", 0.1, 0.54), Line("b", 0.55, 0.525),
        ]);

        var table = Assert.Single(document.Tables);
        Assert.Equal(
            new[] { new[] { "left", "right" }, new[] { "a", "b" } },
            table.Rows);
    }

    [Fact]
    public void midY差超过容差时分成两个视觉行()
    {
        var document = _analyzer.Analyze([Line("top", 0.1, 0.8), Line("bottom", 0.1, 0.5)]);

        Assert.Empty(document.Tables);
    }

    // ── 表格判定 ───────────────────────────────────────────────────

    [Fact]
    public void 只有一个多格行不构成表格()
    {
        var document = _analyzer.Analyze([Line("a   b", 0.1, 0.5)]);

        Assert.Empty(document.Tables);
    }

    [Fact]
    public void 列数差超过1的行被剔除()
    {
        // 众数列数 = 2（三行），"g   h   i   j   k" 是 5 列 → 差 3 > 1，被剔除。
        // 剔除后仍剩 3 行（≥2），所以**仍然输出表格**，只是不含那一行。
        var document = _analyzer.Analyze([
            Line("a   b", 0.1, 0.8),
            Line("c   d", 0.1, 0.7),
            Line("e   f", 0.1, 0.6),
            Line("g   h   i   j   k", 0.1, 0.5),
        ]);

        var table = Assert.Single(document.Tables);
        Assert.Equal(3, table.Rows.Count);
        Assert.DoesNotContain(table.Rows, row => row.Contains("g"));
    }

    [Fact]
    public void 列数差恰好为1的行被保留()
    {
        var document = _analyzer.Analyze([
            Line("a   b", 0.1, 0.8),
            Line("c   d", 0.1, 0.7),
            Line("e   f   g", 0.1, 0.6),
        ]);

        var table = Assert.Single(document.Tables);
        Assert.Equal(3, table.Rows.Count);
    }

    [Fact]
    public void 剔除后不足两行则不输出表格()
    {
        // 众数列数 = 2（只有一行），唯一的另一行是 5 列 → 被剔除后只剩 1 行 < 2 → 无表格。
        var document = _analyzer.Analyze([
            Line("a   b", 0.1, 0.8),
            Line("g   h   i   j   k", 0.1, 0.5),
        ]);

        Assert.Empty(document.Tables);
    }

    [Fact]
    public void 多格行不足两行时不输出表格()
    {
        var document = _analyzer.Analyze([Line("a   b", 0.1, 0.8), Line("plain text line", 0.1, 0.7)]);

        Assert.Empty(document.Tables);
    }

    [Fact]
    public void 频次并列时取列数较大者()
    {
        // Mac 的 Dictionary 迭代顺序无保证，`max(by:)` 取最后一个并列最大 —— 行为不确定。
        // Windows 侧固定为「列数较大者」，这里把该决定钉死：
        // 两行 2 列 + 两行 3 列 → 众数列数取 3，于是两行 2 列（差 1）也被保留。
        var document = _analyzer.Analyze([
            Line("a   b", 0.1, 0.85),
            Line("c   d", 0.1, 0.75),
            Line("e   f   g", 0.1, 0.65),
            Line("h   i   j", 0.1, 0.55),
        ]);

        var table = Assert.Single(document.Tables);
        Assert.Equal(4, table.Rows.Count);
    }

    // ── 阅读顺序 ───────────────────────────────────────────────────

    [Fact]
    public void 同一行按minX从左到右()
    {
        // 阅读顺序用的是固定 0.015 阈值（VisionOCRService.swift:65-71），
        // 与分析器的 max(0.012, h*0.65) 不同 —— 两处刻意保留差异。
        var lines = RecognitionLineOrdering.Sort([
            Line("right", 0.6, 0.5),
            Line("left", 0.1, 0.5),
        ]);

        Assert.Equal("left", lines[0].Text);
        Assert.Equal("right", lines[1].Text);
    }

    [Fact]
    public void 垂直距离小于0015视为同一行()
    {
        var lines = RecognitionLineOrdering.Sort([
            Line("b", 0.1, 0.5000),
            Line("a", 0.5, 0.5050),   // midY 差 0.005 < 0.015 → 同行，按 minX
        ]);

        Assert.Equal("b", lines[0].Text);
        Assert.Equal("a", lines[1].Text);
    }

    [Fact]
    public void 垂直距离超过0015时midY大的在前()
    {
        var lines = RecognitionLineOrdering.Sort([
            Line("lower", 0.1, 0.50),
            Line("upper", 0.9, 0.80),
        ]);

        Assert.Equal("upper", lines[0].Text);
        Assert.Equal("lower", lines[1].Text);
    }

    // ── TSV / Markdown ─────────────────────────────────────────────

    [Fact]
    public void TSV按制表符连接()
    {
        var document = _analyzer.Analyze([
            Line("Name", 0.1, 0.8), Line("Score", 0.55, 0.8),
            Line("Alice", 0.1, 0.7), Line("98", 0.55, 0.7),
        ]);

        Assert.Equal("Name\tScore\nAlice\t98", Assert.Single(document.Tables).Tsv);
    }

    [Fact]
    public void Markdown表头与分隔行()
    {
        var document = _analyzer.Analyze([
            Line("A", 0.1, 0.8), Line("B", 0.55, 0.8),
            Line("1", 0.1, 0.7), Line("2", 0.55, 0.7),
        ]);

        Assert.Equal("| A | B |\n| --- | --- |\n| 1 | 2 |", Assert.Single(document.Tables).Markdown);
    }

    [Fact]
    public void Markdown短行右侧补空()
    {
        var table = new OCRTable([new[] { "a", "b" }, new[] { "c" }]);

        Assert.Equal("| a | b |\n| --- | --- |\n| c |  |", table.Markdown);
    }

    [Fact]
    public void 空表格的Markdown为空串()
        => Assert.Equal(string.Empty, new OCRTable([new string[0]]).Markdown);

    // ── 辅助 ───────────────────────────────────────────────────────

    /// <summary>
    /// 与 Mac 测试夹具 <c>line(_:x:y:)</c>（<c>OCRDocumentLayoutAnalyzerTests.swift:43-45</c>）
    /// 完全一致：<c>CGRect(x: x, y: y, width: 0.25, height: 0.04)</c>。
    /// </summary>
    private static OCRTextLine Line(string text, double x, double y)
        => new(text, 0.95, new NormalizedRect(x, y, 0.25, 0.04));
}
