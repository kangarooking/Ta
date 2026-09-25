using System.Text.RegularExpressions;
using Ta.OCR.Models;

namespace Ta.OCR.Text;

/// <summary>
/// 内容类型分类器。
///
/// 逐字对应 Mac 版 <c>ContentClassifier</c>
/// （<c>AIScreenshotCore/OCR/ContentClassifier.swift:1-81</c>）。
///
/// ⚠️ <b>判定顺序不可调整</b>（`:6-27`）：空 → qrCode → table → formula → code → plainText。
/// 顺序是语义的一部分：一段含 URL 的表格必须先判成 qrCode，
/// 一段 LaTeX 源码必须先判成 formula 而不是 code。交换任意两步都会改变
/// 智能路由挑到的任务模板（参考文档 §9.11），进而改变提示词。
/// </summary>
public sealed class ContentClassifier
{
    // :30-31 —— 单行且以这些 scheme 开头 → 二维码载荷。
    private static readonly Regex QrCodePrefix = new(
        @"^(https?://|mailto:|tel:|otpauth://|WIFI:)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // :51 —— 表格的第三种判据：非空白 + 两个以上空白 + 非空白。
    private static readonly Regex WhitespaceSeparatedCells = new(
        @"\S\s{2,}\S",
        RegexOptions.Compiled);

    // :57-63 —— 代码特征。**需要命中 ≥2 个**才算代码（`:70`）。
    private static readonly Regex[] CodePatterns =
    [
        new(@"\b(func|class|struct|enum|protocol|import|let|var|return|if|else|for|while|async|await)\b", RegexOptions.Compiled),
        new(@"\b(const|function|interface|type|export|from|def|lambda|public|private|void|static)\b", RegexOptions.Compiled),
        new(@"[{};]", RegexOptions.Compiled),
        new(@"(^|\n)\s{2,}\S", RegexOptions.Compiled),
        new(@"\w+\s*\([^\n]*\)\s*(\{|->|:)", RegexOptions.Compiled),
    ];

    // :74-78 —— 公式特征，命中任意一个即可。
    private static readonly Regex[] FormulaPatterns =
    [
        new(@"\\(frac|sqrt|sum|int|begin|alpha|beta|theta)\b", RegexOptions.Compiled),
        new(@"[∫∑√≈≠≤≥∞∂]", RegexOptions.Compiled),
        new(@"[₀₁₂₃₄₅₆₇₈₉⁰¹²³⁴⁵⁶⁷⁸⁹]", RegexOptions.Compiled),
    ];

    public CaptureContentType Classify(string text)
    {
        // :7-8 —— 先按「空白+换行」裁剪，裁完为空 → image。
        var trimmed = SwiftStringSemantics.TrimSwiftWhitespaceAndNewlines(text);
        if (trimmed.Length == 0)
        {
            return CaptureContentType.Image;
        }

        // :10-12
        if (LooksLikeQrCodePayload(trimmed))
        {
            return CaptureContentType.QrCode;
        }

        // :14-16
        if (LooksLikeTable(trimmed))
        {
            return CaptureContentType.Table;
        }

        // :18-20
        if (LooksLikeFormula(trimmed))
        {
            return CaptureContentType.Formula;
        }

        // :22-24
        if (LooksLikeCode(trimmed))
        {
            return CaptureContentType.Code;
        }

        // :26
        return CaptureContentType.PlainText;
    }

    /// <summary>对应 <c>looksLikeQRCodePayload</c>（`:29-32`）。</summary>
    private static bool LooksLikeQrCodePayload(string text)
    {
        // :30 —— 含换行的一律不算，即使第一行确实是 URL。
        if (text.Contains('\n'))
        {
            return false;
        }

        return QrCodePrefix.IsMatch(text);
    }

    /// <summary>对应 <c>looksLikeTable</c>（`:34-54`）。</summary>
    private static bool LooksLikeTable(string text)
    {
        // :35 —— omittingEmptySubsequences: true，即跳过空行。
        var rows = text.Split('\n').Where(row => row.Length > 0).ToList();

        // :36 —— 至少两行才可能是表格。
        if (rows.Count < 2)
        {
            return false;
        }

        // 判据一：制表符数量一致（:38-42）。要求首行 tab 数 > 0。
        var tabCounts = rows.Select(row => row.Count(c => c == '\t')).ToList();
        if (tabCounts[0] > 0 && tabCounts.Count(count => count == tabCounts[0]) >= Math.Max(2, rows.Count - 1))
        {
            return true;
        }

        // 判据二：竖线数量一致（:44-48）。要求首行竖线数 >= 2。
        var pipeCounts = rows.Select(row => row.Count(c => c == '|')).ToList();
        if (pipeCounts[0] >= 2 && pipeCounts.Count(count => count == pipeCounts[0]) >= Math.Max(2, rows.Count - 1))
        {
            return true;
        }

        // 判据三：多列行（靠空白分隔）足够多（:50-53）。
        var multiColumnRows = rows.Count(row => WhitespaceSeparatedCells.IsMatch(row));
        return multiColumnRows >= Math.Max(2, rows.Count - 1);
    }

    /// <summary>对应 <c>looksLikeCode</c>（`:56-71`）。</summary>
    private static bool LooksLikeCode(string text)
    {
        var matches = CodePatterns.Count(pattern => pattern.IsMatch(text));

        // :70 —— 5 个特征里命中至少 2 个。
        return matches >= 2;
    }

    /// <summary>对应 <c>looksLikeFormula</c>（`:73-80`）。</summary>
    private static bool LooksLikeFormula(string text)
    {
        foreach (var pattern in FormulaPatterns)
        {
            if (pattern.IsMatch(text))
            {
                return true;
            }
        }

        return false;
    }
}
