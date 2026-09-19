using System.Text.RegularExpressions;

namespace Ta.OCR.Text;

/// <summary>
/// OCR 原始文本规范化。
///
/// 逐字对应 Mac 版 <c>OCRTextNormalizer</c>
/// （<c>AIScreenshotCore/OCR/OCRTextNormalizer.swift:1-66</c>）。
///
/// <b>行为契约（不可改动，参考文档 §9.12「必须逐条复现」）</b>
/// <list type="number">
///   <item>CRLF / CR 一律转成 LF（`:7-9`）</item>
///   <item>每行去掉尾部 <c>[ \t]+</c>（`:11-13`）</item>
///   <item>连续空行折叠为一个空行；开头的空行直接丢弃（`:18-26`）</item>
///   <item>去掉尾部空行（`:39-41`）</item>
///   <item>仅当 <paramref name="mergeWrappedLines"/> 为 true 时合并软换行（默认 false，`:28-35`）</item>
/// </list>
///
/// ⚠️ <c>mergeWrappedLines</c> **默认 false**。翻译路径显式传 false，因为要拿 bbox 做图像渲染。
/// </summary>
public sealed class OCRTextNormalizer
{
    // 尾部空白：与 Swift 的 #"[ \t]+$"# 一致。行已被切开，$ 即串尾。
    private static readonly Regex TrailingSpaces = new(@"[ \t]+$", RegexOptions.Compiled);

    public string Normalize(string rawText, bool mergeWrappedLines = false)
    {
        if (rawText.Length == 0)
        {
            return string.Empty;
        }

        // :7-9 —— 顺序很重要：必须先换 CRLF 再换孤立 CR，否则 CRLF 会被拆成两个 LF。
        var normalizedNewlines = rawText
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace("\r", "\n", StringComparison.Ordinal);

        var output = new List<string>();
        var previousWasBlank = false;

        // :11-13 —— Swift 的 components(separatedBy: "\n") 对空串返回 [""]，.NET 的 Split 同样，
        // 因此 "abc" -> ["abc"]、"" -> [""]，行为一致（空串已在上面短路，这里无需特例）。
        foreach (var line in normalizedNewlines.Split('\n'))
        {
            var trimmed = TrailingSpaces.Replace(line, string.Empty);

            // :19 —— 用的是 CharacterSet.whitespaces（**不含**换行符），
            // 所以含 U+000B / U+0085 / U+2028 的行算「非空」。
            var isBlank = SwiftStringSemantics.TrimSwiftWhitespace(trimmed).Length == 0;

            if (isBlank)
            {
                // :21 —— !output.isEmpty 让**开头**的空行彻底消失，而不是留一个空行。
                if (!previousWasBlank && output.Count > 0)
                {
                    output.Add(string.Empty);
                }

                previousWasBlank = true;
                continue;
            }

            // :28-31 —— previous.isEmpty 保证不会跨空行合并。
            if (mergeWrappedLines
                && output.Count > 0
                && output[^1].Length > 0
                && ShouldMerge(output[^1], trimmed))
            {
                output[^1] = output[^1] + Joiner(output[^1], trimmed) + trimmed;
            }
            else
            {
                output.Add(trimmed);
            }

            previousWasBlank = false;
        }

        // :39-41 —— 去掉尾部空行。
        while (output.Count > 0 && output[^1].Length == 0)
        {
            output.RemoveAt(output.Count - 1);
        }

        return string.Join("\n", output);
    }

    /// <summary>
    /// 是否把下一行并到上一行。对应 <c>shouldMerge</c>（`:46-59`）。
    /// </summary>
    private static bool ShouldMerge(string previous, string next)
    {
        // :47-54 —— 上一行是句末标点，或下一行是列表项标记时**不**合并。
        if (EndsWithAny(previous, "。", "！", "？", ".", ":", "：")
            || StartsWithAny(next, "•", "-"))
        {
            return false;
        }

        // :58 —— 分号与花括号视为代码边界，同样不合并。
        return !EndsWithAny(previous, ";", "{", "}");
    }

    /// <summary>
    /// 连接符。对应 <c>joiner</c>（`:61-65`）：
    /// **仅当**上一行末字符与下一行首字符**都是 ASCII（&lt; 128）**时才用空格，否则用空串。
    /// 这条规则是中文不断词、英文按词拼接的关键，改一个字符就会让中英混排结果跑偏。
    /// </summary>
    private static string Joiner(string previous, string next)
    {
        var previousEndsAscii = previous.Length > 0 && previous[^1] < 128;
        var nextStartsAscii = next.Length > 0 && next[0] < 128;
        return previousEndsAscii && nextStartsAscii ? " " : string.Empty;
    }

    // 显式用 Ordinal：.NET 的默认 EndsWith 是区域性比较，会把 U+00AD（软连字符）之类
    // 的可忽略字符吃进去，导致 "-" 意外匹配 —— Swift 的 hasSuffix 不会。
    private static bool EndsWithAny(string value, params string[] suffixes)
        => Array.Exists(suffixes, suffix => value.EndsWith(suffix, StringComparison.Ordinal));

    private static bool StartsWithAny(string value, params string[] prefixes)
        => Array.Exists(prefixes, prefix => value.StartsWith(prefix, StringComparison.Ordinal));
}
