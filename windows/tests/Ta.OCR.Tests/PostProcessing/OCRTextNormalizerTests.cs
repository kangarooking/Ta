using Ta.OCR.Text;
using Xunit;

namespace Ta.OCR.Tests.PostProcessing;

/// <summary>
/// <see cref="OCRTextNormalizer"/> 测试。
///
/// 前两条断言**逐字对应** Mac 版
/// <c>Tests/AIScreenshotCoreTests/AIScreenshotCoreTests.swift:74-85</c> 的两条
/// （<c>testNormalizerTrimsTrailingWhitespaceAndBlankRuns</c> 与
/// <c>testNormalizerCanMergeWrappedEnglishLines</c>），输入与期望输出都一样。
/// 其余条目覆盖合并规则的每个分支，防止「看起来一样」的回归。
/// </summary>
public class OCRTextNormalizerTests
{
    private readonly OCRTextNormalizer _normalizer = new();

    // ── Mac 逐字对应 ────────────────────────────────────────────────

    [Fact]
    public void Mac_裁剪尾部空白并折叠连续空行()
    {
        // AIScreenshotCoreTests.swift:75-76
        var input = "First line   \r\n\r\n\r\nSecond line\t\r\n";
        Assert.Equal("First line\n\nSecond line", _normalizer.Normalize(input));
    }

    [Fact]
    public void Mac_合并英文软换行时用空格连接()
    {
        // AIScreenshotCoreTests.swift:80-84
        var input = "A sentence that wraps\nonto another line";
        Assert.Equal(
            "A sentence that wraps onto another line",
            _normalizer.Normalize(input, mergeWrappedLines: true));
    }

    // ── 换行归一化 ──────────────────────────────────────────────────

    [Theory]
    [InlineData("a\r\nb", "a\nb")]
    [InlineData("a\rb", "a\nb")]
    [InlineData("a\r\n\r\nb", "a\n\nb")]
    [InlineData("a\n\rb", "a\n\nb")]
    public void CRLF与CR都归一化为LF(string input, string expected)
        => Assert.Equal(expected, _normalizer.Normalize(input));

    [Fact]
    public void 只有CRLF的输入不会产生双换行()
    {
        // 顺序敏感：必须先整段替换 \r\n → \n，再处理孤立 \r。反过来会得到两个 LF。
        Assert.Equal("a\nb", _normalizer.Normalize("a\r\nb"));
    }

    // ── 尾部空白与空行 ──────────────────────────────────────────────

    [Theory]
    [InlineData("a   ")]
    [InlineData("a\t")]
    [InlineData("a \t ")]
    public void 去掉每行尾部的空格与制表符(string input)
        => Assert.Equal("a", _normalizer.Normalize(input));

    [Fact]
    public void 不去掉行首空白()
    {
        // 缩进是代码的语义（ContentClassifier 的代码判据之一），必须保留。
        Assert.Equal("    return 1", _normalizer.Normalize("    return 1"));
    }

    [Fact]
    public void 连续空行折叠为一个()
        => Assert.Equal("a\n\nb", _normalizer.Normalize("a\n\n\n\n\nb"));

    [Fact]
    public void 开头的空行被丢弃()
        => Assert.Equal("a", _normalizer.Normalize("\n\n\na"));

    [Fact]
    public void 尾部的空行被丢弃()
        => Assert.Equal("a", _normalizer.Normalize("a\n\n\n"));

    [Fact]
    public void 只含空行的输入得到空串()
        => Assert.Equal(string.Empty, _normalizer.Normalize("\n\n\n"));

    [Fact]
    public void 空输入得到空串()
        => Assert.Equal(string.Empty, _normalizer.Normalize(string.Empty));

    [Fact]
    public void 默认不合并软换行()
    {
        // mergeWrappedLines 默认 false —— Mac 的默认值，翻译路径也依赖它。
        Assert.Equal("a\nb", _normalizer.Normalize("a\nb"));
    }

    // ── 合并规则的「不合并」分支 ────────────────────────────────────

    [Theory]
    [InlineData("句子一。", "下一句", "句子一。\n下一句")]
    [InlineData("真的吗？", "下一句", "真的吗？\n下一句")]
    [InlineData("太棒了！", "下一句", "太棒了！\n下一句")]
    [InlineData("End.", "Next", "End.\nNext")]
    [InlineData("Note:", "body", "Note:\nbody")]
    [InlineData("注意：", "正文", "注意：\n正文")]
    [InlineData("first;", "second", "first;\nsecond")]
    [InlineData("open {", "body", "open {\nbody")]
    [InlineData("close }", "tail", "close }\ntail")]
    public void 上一行是句末标点或代码边界时不合并(string previous, string next, string expected)
        => Assert.Equal(expected, _normalizer.Normalize($"{previous}\n{next}", mergeWrappedLines: true));

    [Theory]
    [InlineData("first", "• bullet", "first\n• bullet")]
    [InlineData("first", "- item", "first\n- item")]
    public void 下一行是列表项时不合并(string previous, string next, string expected)
        => Assert.Equal(expected, _normalizer.Normalize($"{previous}\n{next}", mergeWrappedLines: true));

    // ── 合并规则的「合并」分支与连接符 ──────────────────────────────

    [Fact]
    public void 两侧都是ASCII时用空格连接()
        => Assert.Equal("hello world", _normalizer.Normalize("hello\nworld", mergeWrappedLines: true));

    [Fact]
    public void 上一行是中文时不留空格()
        => Assert.Equal("你好世界", _normalizer.Normalize("你好\n世界", mergeWrappedLines: true));

    [Fact]
    public void 下一行是中文时不留空格()
        => Assert.Equal("hello你好", _normalizer.Normalize("hello\n你好", mergeWrappedLines: true));

    [Fact]
    public void 上一行中文下一行英文也不留空格()
        => Assert.Equal("你好world", _normalizer.Normalize("你好\nworld", mergeWrappedLines: true));

    [Fact]
    public void 上一行英文下一行中文同样不留空格()
        => Assert.Equal("world你好", _normalizer.Normalize("world\n你好", mergeWrappedLines: true));

    [Fact]
    public void 超过127的BMP码位不算ASCII()
    {
        // ½（U+00BD）> 127，两侧都不算 ASCII → 不留空格。
        Assert.Equal("½½", _normalizer.Normalize("½\n½", mergeWrappedLines: true));
    }

    [Fact]
    public void 代理对不算ASCII()
    {
        // 😀（U+1F600）由两个 UTF-16 单元组成，两个都 ≥ 0xDC00。
        Assert.Equal("😀😀", _normalizer.Normalize("😀\n😀", mergeWrappedLines: true));
    }

    [Fact]
    public void 合并时不会跨过空行()
        => Assert.Equal("a\n\nb", _normalizer.Normalize("a\n\nb", mergeWrappedLines: true));

    [Fact]
    public void 连续多行被逐步合并()
        => Assert.Equal(
            "one two three",
            _normalizer.Normalize("one\ntwo\nthree", mergeWrappedLines: true));

    [Fact]
    public void 中英混排多行合并()
    {
        // 逐行推演（连接符只在**两侧都是 ASCII** 时才用空格）：
        //   "第一段" + "second" → 上段末是「段」非 ASCII → 无空格 → "第一段second"
        //   "第一段second" + "段" → 上段末是 'd'（ASCII）但下段首是「段」非 ASCII → 无空格
        Assert.Equal(
            "第一段second段",
            _normalizer.Normalize("第一段\nsecond\n段", mergeWrappedLines: true));
    }

    // ── Swift 空白集语义 ────────────────────────────────────────────
    // 下面三处刻意用 C# 的 \uXXXX 转义写不可见字符：若在源码里放字面量，
    // 就会出现「看不见但影响断言」的内容，读代码时无从判断。

    [Fact]
    public void 只含垂直制表符的行不算空行()
    {
        // U+000B：CharacterSet.whitespaces 不含它，.NET 的 char.IsWhiteSpace 含。
        // 若改用 .NET 的 Trim() 判空行，这一行会被折叠掉 —— 本移植最隐蔽的一处差异。
        Assert.Equal("\u000B", _normalizer.Normalize("\u000B"));
    }

    [Fact]
    public void 只含制表符的行算空行()
        => Assert.Equal(string.Empty, _normalizer.Normalize("\t"));

    [Fact]
    public void 只含不换行空格的行算空行()
    {
        // U+00A0 属于 Unicode Zs，在 CharacterSet.whitespaces 内。
        Assert.Equal(string.Empty, _normalizer.Normalize("\u00A0"));
    }

    [Fact]
    public void 只含C1控制符的行不算空行()
    {
        // U+0085 不在 CharacterSet.whitespaces 内（但在 whitespacesAndNewlines 内）。
        Assert.Equal("\u0085", _normalizer.Normalize("\u0085"));
    }
}
