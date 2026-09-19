using System.Text.RegularExpressions;
using Ta.OCR.Models;

namespace Ta.OCR.Text;

/// <summary>
/// 文档版面分析：把扁平的文本行还原成「块」与「表格」。
///
/// 逐字对应 Mac 版 <c>OCRDocumentLayoutAnalyzer</c>
/// （<c>AIScreenshotCore/OCR/OCRDocumentLayoutAnalyzer.swift:1-92</c>）。
///
/// <b>三组阈值，全部以归一化坐标（0..1、原点左下）为基准，不可改动</b>：
/// <list type="bullet">
///   <item>分块：<c>verticalGap &gt; typicalHeight * 2.4</c> 或 <c>|ΔminX| &gt; 0.28</c>（`:20-24`）</item>
///   <item>分行容差：<c>max(0.012, max(h1,h2) * 0.65)</c>（`:55`、`:67`）</item>
///   <item>表格：需 ≥2 个多格行、取众数列数、保留列数差 ≤1 的行、仍需 ≥2 行（`:42-47`）</item>
/// </list>
/// </summary>
public sealed class OCRDocumentLayoutAnalyzer
{
    // :38 —— 单格行按两个以上空白拆列。
    private static readonly Regex CellSeparator = new(@"\s{2,}", RegexOptions.Compiled);

    public OCRDocumentLayout Analyze(IReadOnlyList<OCRTextLine> lines)
    {
        // :8 —— 先按阅读顺序排序。LINQ OrderBy 是**稳定**排序；
        // Swift 的 sorted(by:) 官方不保证稳定，但比较器对 midY/minX 都不同的行是全序，
        // 只有「midY 与 minX 同时相等」才可能分歧 —— 那种行在真实截图里不存在。
        var ordered = lines.OrderBy(line => line, ReadingOrderComparer.Instance).ToList();

        return new OCRDocumentLayout(MakeBlocks(ordered), MakeTables(ordered));
    }

    /// <summary>对应 <c>makeBlocks</c>（`:15-31`）。注意入参是**已排序**的行。</summary>
    private static IReadOnlyList<OCRDocumentBlock> MakeBlocks(IReadOnlyList<OCRTextLine> lines)
    {
        if (lines.Count == 0)
        {
            return [];
        }

        var blocks = new List<List<OCRTextLine>> { new() { lines[0] } };

        for (var i = 1; i < lines.Count; i++)
        {
            var line = lines[i];
            // :19 —— 永远与**当前块的最后一行**比，而不是与上一行比。
            var previous = blocks[^1][^1];

            // :20-21 —— Mac 的 Y 轴向上，所以「上一个的下边界减下一个的上边界」就是垂直间距。
            var verticalGap = previous.BoundingBox.MinY - line.BoundingBox.MaxY;
            var typicalHeight = Math.Max(0.01, (previous.BoundingBox.Height + line.BoundingBox.Height) / 2);
            var largeGap = verticalGap > typicalHeight * 2.4;

            var majorIndentChange = Math.Abs(previous.BoundingBox.MinX - line.BoundingBox.MinX) > 0.28;

            if (largeGap || majorIndentChange)
            {
                blocks.Add([line]);
            }
            else
            {
                blocks[^1].Add(line);
            }
        }

        // :30
        return blocks.Select(block => new OCRDocumentBlock(block)).ToList();
    }

    /// <summary>对应 <c>makeTables</c>（`:33-48`）。</summary>
    private static IReadOnlyList<OCRTable> MakeTables(IReadOnlyList<OCRTextLine> lines)
    {
        var visualRows = GroupIntoRows(lines);

        var multiCellRows = visualRows.Select(row =>
        {
            // :36 —— 视觉行里有多个文本行 → 按 minX 从左到右排，直接当单元格。
            if (row.Count > 1)
            {
                return row.OrderBy(item => item.BoundingBox.MinX).Select(item => item.Text).ToList();
            }

            // :37-40 —— 单行 → 按空白拆列，丢掉空片段。
            var text = row[0].Text;
            return SwiftStringSemantics
                .ComponentsSplitByRegex(text, CellSeparator)
                .Select(part => SwiftStringSemantics.TrimSwiftWhitespaceAndNewlines(part))
                .Where(part => part.Length > 0)
                .ToList();
        }).ToList();

        var candidates = multiCellRows.Where(row => row.Count >= 2).ToList();

        // :42-43 —— 至少两行多格才可能是表格。
        if (candidates.Count < 2)
        {
            return [];
        }

        // :44 —— 取众数列数。
        //
        // ⚠️ Mac 这里写的是
        // `Dictionary(grouping: candidates, by: \.count).max { $0.value.count < $1.value.count }?.key`：
        // ① Dictionary 的迭代顺序在 Swift 里无保证，
        // ② `max(by:)` 在多个并列最大时返回**最后一个**。
        // 合起来意味着 Mac 在「频次并列」时列数选择是不确定的。
        // 这里改为确定性的「频次并列时取列数较大者」，并在测试里钉住该行为。
        var commonCount = candidates
            .GroupBy(row => row.Count)
            .OrderByDescending(group => group.Count())
            .ThenByDescending(group => group.Key)
            .First()
            .Key;

        // :45 —— 只保留列数与众数差 ≤1 的行。
        var consistent = candidates.Where(row => Math.Abs(row.Count - commonCount) <= 1).ToList();

        // :46 —— 过滤后仍需 ≥2 行。
        if (consistent.Count < 2)
        {
            return [];
        }

        // :47
        return [new OCRTable(consistent)];
    }

    /// <summary>对应 <c>groupIntoRows</c>（`:50-64`）。</summary>
    private static IReadOnlyList<List<OCRTextLine>> GroupIntoRows(IReadOnlyList<OCRTextLine> lines)
    {
        var rows = new List<List<OCRTextLine>>();

        foreach (var line in lines)
        {
            // :53 —— 找**第一个**首行 midY 相符的视觉行（不是最后一个），
            // 这个「第一个」决定了同一行文字被归到哪一组，改成就成了不同的版面。
            var index = rows.FindIndex(row =>
            {
                var first = row[0];
                var tolerance = Math.Max(0.012, Math.Max(first.BoundingBox.Height, line.BoundingBox.Height) * 0.65);
                return Math.Abs(first.BoundingBox.MidY - line.BoundingBox.MidY) <= tolerance;
            });

            if (index >= 0)
            {
                rows[index].Add(line);
            }
            else
            {
                rows.Add([line]);
            }
        }

        // :63 —— 按 midY 降序（Y 轴向上，越靠上越先）。
        // 入参已是阅读顺序（基本上就是 midY 降序），此处仍显式排序以保持与 Mac 一致。
        return rows
            .OrderByDescending(row => row[0].BoundingBox.MidY)
            .ToList();
    }

    /// <summary>对应 <c>readingOrder</c>（`:66-72`）。</summary>
    private sealed class ReadingOrderComparer : IComparer<OCRTextLine>
    {
        public static readonly ReadingOrderComparer Instance = new();

        public int Compare(OCRTextLine lhs, OCRTextLine rhs)
        {
            var tolerance = Math.Max(0.012, Math.Max(lhs.BoundingBox.Height, rhs.BoundingBox.Height) * 0.65);

            // :68-70 —— 同一行（midY 差在容差内）按 minX 升序。
            if (Math.Abs(lhs.BoundingBox.MidY - rhs.BoundingBox.MidY) <= tolerance)
            {
                return lhs.BoundingBox.MinX.CompareTo(rhs.BoundingBox.MinX);
            }

            // :71 —— 否则 midY 大的在前（Y 轴向上）。
            return rhs.BoundingBox.MidY.CompareTo(lhs.BoundingBox.MidY);
        }
    }
}
