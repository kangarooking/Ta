using System.Globalization;
using System.Text.RegularExpressions;

namespace Ta.OCR.Text;

/// <summary>
/// Swift/Foundation 字符串语义的兼容垫片。
///
/// Mac 版的三个后处理器重度依赖 <c>CharacterSet.whitespaces</c>、
/// <c>CharacterSet.whitespacesAndNewlines</c> 与 <c>NSRegularExpression</c>。
/// .NET 的 <c>char.IsWhiteSpace</c> / <c>Regex</c> 与它们**几乎但不完全**一致，
/// 这里把差异显式收拢，避免「看起来一样、跑出来不一样」。
/// </summary>
internal static class SwiftStringSemantics
{
    /// <summary>
    /// <c>CharacterSet.whitespaces</c> 的等价判定：
    /// Unicode 类别 Zs（含空格 U+0020、U+00A0、U+2000–U+200A 等）加制表符 U+0009。
    /// <para>
    /// 与 <c>char.IsWhiteSpace</c> 的差别：.NET 额外把 U+000A–U+000D、U+0085、U+2028、U+2029
    /// 也算作空白。在 <see cref="OCRTextNormalizer"/> 的「是否空行」判断里这会造成行为差异
    /// （一个只含垂直制表符或 U+0085 的行，Swift 视为非空、.NET 视为空），故必须分开实现。
    /// </para>
    /// </summary>
    public static bool IsSwiftWhitespace(char value)
    {
        if (value == '\t')
        {
            return true;
        }

        return char.GetUnicodeCategory(value) == UnicodeCategory.SpaceSeparator;
    }

    /// <summary>
    /// <c>CharacterSet.whitespacesAndNewlines</c> 的等价判定。
    /// <para>
    /// 实测集合与 <c>char.IsWhiteSpace</c> **完全相同**：
    /// Swift = Zs ∪ {U+0009..U+000D, U+0085, U+2028, U+2029}，
    /// .NET  = {U+0009..U+000D, U+0020, U+0085} ∪ Zs ∪ Zl ∪ Zp，二者等价。
    /// 所以用 <c>string.Trim()</c> 即可，这里保留名字是为了让「为什么可以放心用 Trim」有据可查。
    /// </para>
    /// </summary>
    public static bool IsSwiftWhitespaceOrNewline(char value) => char.IsWhiteSpace(value);

    /// <summary>两端裁剪 Swift 空白集（不含换行）。</summary>
    public static string TrimSwiftWhitespace(string value)
    {
        var start = 0;
        var end = value.Length - 1;
        while (start <= end && IsSwiftWhitespace(value[start]))
        {
            start++;
        }

        while (end >= start && IsSwiftWhitespace(value[end]))
        {
            end--;
        }

        return start > end ? string.Empty : value.Substring(start, end - start + 1);
    }

    /// <summary>两端裁剪 Swift 空白 + 换行集。等价于 <c>value.Trim()</c>。</summary>
    public static string TrimSwiftWhitespaceAndNewlines(string value) => value.Trim();

    /// <summary>
    /// 按正则切分字符串，语义对应
    /// <c>String.components(separatedBy: NSRegularExpression)</c>
    /// （`OCRDocumentLayoutAnalyzer.swift:75-91` 的私有扩展）。
    ///
    /// Mac 的实现逐字等价于：把每个匹配位置当分隔符，保留分隔符之间的片段，
    /// **首尾片段即使为空也保留**（所以 `"a  b"` 按 <c>\s{2,}</c> 切出
    /// `["a", "b"]`，而 `"  a"` 切出 `["", "", "a"]` —— 后面的 filter 会丢掉空串）。
    /// <para>
    /// .NET 的 <c>Regex.Split</c> 在这一点上一致，但为了不依赖框架的边界细节
    /// （以及 Swift 那份实现**不排序匹配**的特性），这里按原样显式复刻。
    /// </para>
    /// </summary>
    public static IReadOnlyList<string> ComponentsSplitByRegex(string value, Regex pattern)
    {
        ArgumentNullException.ThrowIfNull(pattern);

        var result = new List<string>();
        var last = 0;
        foreach (Match match in pattern.Matches(value))
        {
            result.Add(value.Substring(last, match.Index - last));
            last = match.Index + match.Length;
        }

        result.Add(value.Substring(last));
        return result;
    }
}
