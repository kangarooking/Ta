using Ta.OCR.Models;
using Ta.OCR.Text;
using Xunit;

namespace Ta.OCR.Tests.PostProcessing;

/// <summary>
/// <see cref="ContentClassifier"/> 测试。
///
/// 前 5 条断言**逐字对应** Mac 版
/// <c>Tests/AIScreenshotCoreTests/AIScreenshotCoreTests.swift:51-72</c>，
/// 输入与期望类型完全一致。其余覆盖判定顺序与每条判据的边界。
/// </summary>
public class ContentClassifierTests
{
    private readonly ContentClassifier _classifier = new();

    // ── Mac 逐字对应 ────────────────────────────────────────────────

    [Fact]
    public void Mac_识别Swift代码()
    {
        // AIScreenshotCoreTests.swift:52-57
        var text = """
            func greet(name: String) -> String {
                return "Hello, \(name)"
            }
            """;
        Assert.Equal(CaptureContentType.Code, _classifier.Classify(text));
    }

    [Fact]
    public void Mac_识别制表符分隔的表格()
    {
        // :60-63
        var text = "Name\tScore\nAlice\t98\nBob\t95";
        Assert.Equal(CaptureContentType.Table, _classifier.Classify(text));
    }

    [Fact]
    public void Mac_识别URL载荷()
    {
        // :65-67
        Assert.Equal(CaptureContentType.QrCode, _classifier.Classify("https://example.com/path"));
    }

    [Fact]
    public void Mac_识别公式()
    {
        // :69-72 —— 两条输入分别命中「数学符号」与「LaTeX 命令」两组特征。
        Assert.Equal(CaptureContentType.Formula, _classifier.Classify("∫₀¹ x² dx = 1/3"));
        Assert.Equal(CaptureContentType.Formula, _classifier.Classify(@"\frac{a+b}{c} = \sqrt{x}"));
    }

    // ── 空 → image ─────────────────────────────────────────────────

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\n\n")]
    [InlineData("\t \n \t")]
    public void 空白文本判为image(string text)
        => Assert.Equal(CaptureContentType.Image, _classifier.Classify(text));

    // ── 二维码载荷 ─────────────────────────────────────────────────

    [Theory]
    [InlineData("http://a.b")]
    [InlineData("https://a.b/c")]
    [InlineData("HTTPS://A.B")]           // 大小写不敏感（Mac: .caseInsensitive）
    [InlineData("MailTo:x@y.z")]
    [InlineData("tel:+8613800138000")]
    [InlineData("otpauth://totp/Example?secret=ABC")]
    [InlineData("WIFI:S:mynet;T:WPA;P:pass;;")]
    public void 单行匹配已知scheme判为qrCode(string text)
        => Assert.Equal(CaptureContentType.QrCode, _classifier.Classify(text));

    [Theory]
    [InlineData("www.example.com")]
    [InlineData("ftp://a.b")]
    [InlineData("文件:///x")]
    [InlineData("x https://a.b")]     // 锚定在行首
    public void 未知scheme或非行首不算qrCode(string text)
        => Assert.NotEqual(CaptureContentType.QrCode, _classifier.Classify(text));

    [Fact]
    public void 多行文本里的URL不算qrCode()
    {
        // looksLikeQRCodePayload 第一行 guard 就是「含 \n 直接 false」。
        var text = "https://example.com/a\nhttps://example.com/b";
        Assert.NotEqual(CaptureContentType.QrCode, _classifier.Classify(text));
    }

    // ── 表格 ───────────────────────────────────────────────────────

    [Fact]
    public void 竖线分隔且数量一致判为表格()
    {
        var text = "a | b | c\nd | e | f";
        Assert.Equal(CaptureContentType.Table, _classifier.Classify(text));
    }

    [Fact]
    public void 首行只有一个竖线不触发竖线判据()
    {
        // pipeCounts.first >= 2 是硬门槛。
        var text = "a | b\nc | d";
        Assert.NotEqual(CaptureContentType.Table, _classifier.Classify(text));
    }

    [Fact]
    public void 空白分隔的多列行判为表格()
    {
        var text = "Product   Price\nTea       12";
        Assert.Equal(CaptureContentType.Table, _classifier.Classify(text));
    }

    [Fact]
    public void 单行不可能是表格()
        => Assert.NotEqual(CaptureContentType.Table, _classifier.Classify("Product   Price"));

    [Fact]
    public void 制表符数量不一致时不触发制表符判据()
    {
        var text = "a\tb\tc\nd\te";
        Assert.NotEqual(CaptureContentType.Table, _classifier.Classify(text));
    }

    [Fact]
    public void 三行里有两行列数一致即可判为表格()
    {
        // max(2, rows-1) = 2，三行里两行一致就够。
        var text = "a | b | c\nd | e | f\nsingle line here";
        Assert.Equal(CaptureContentType.Table, _classifier.Classify(text));
    }

    [Fact]
    public void 空行被跳过后再数行数()
    {
        var text = "a\tb\n\nc\td";
        Assert.Equal(CaptureContentType.Table, _classifier.Classify(text));
    }

    // ── 公式 ───────────────────────────────────────────────────────

    [Theory]
    [InlineData(@"\frac{1}{2}")]
    [InlineData(@"\sqrt{2}")]
    [InlineData(@"\sum x")]
    [InlineData(@"\int x")]
    [InlineData(@"\begin{matrix}")]
    [InlineData(@"\alpha + \beta")]
    [InlineData(@"\theta = 45")]
    public void LaTeX命令判为公式(string text)
        => Assert.Equal(CaptureContentType.Formula, _classifier.Classify(text));

    [Theory]
    [InlineData(@"\sum_{i=1}^{n}")]
    [InlineData(@"\int_0^1")]
    [InlineData(@"\alpha_1")]
    [InlineData(@"\beta2")]
    public void 命令紧跟单词字符时不算词边界(string text)
    {
        // ⚠️ 这是 Mac 的真实行为，不是移植偏差：正则是
        // \\(frac|sqrt|sum|int|begin|alpha|beta|theta)\b，而 `_` 与数字都是单词字符，
        // ICU（Swift 侧）与 .NET 的 \b 判定一致 —— 所以 `\sum_` 不匹配。
        // 这条断言的意义在于**钉住 parity**：将来若有人「顺手修好」它，就会与 Mac 分叉。
        Assert.NotEqual(CaptureContentType.Formula, _classifier.Classify(text));
    }

    [Theory]
    [InlineData("∫")]
    [InlineData("∑")]
    [InlineData("√")]
    [InlineData("≈")]
    [InlineData("≠")]
    [InlineData("≤")]
    [InlineData("≥")]
    [InlineData("∞")]
    [InlineData("∂")]
    public void 数学符号判为公式(string text)
        => Assert.Equal(CaptureContentType.Formula, _classifier.Classify(text));

    [Theory]
    [InlineData("H₂O")]
    [InlineData("x²")]
    [InlineData("10³")]
    public void 上下标数字判为公式(string text)
        => Assert.Equal(CaptureContentType.Formula, _classifier.Classify(text));

    [Fact]
    public void 命令后面必须跟词边界()
    {
        // \b：`\fraction` 里的 `frac` 前面是 `i`（单词字符），不算词边界。
        Assert.NotEqual(CaptureContentType.Formula, _classifier.Classify(@"\fractional"));
    }

    // ── 代码 ───────────────────────────────────────────────────────

    [Fact]
    public void TypeScript类型与函数签名判为代码()
    {
        var text = "const x: number = 1;\nexport function f(a) { return a; }";
        Assert.Equal(CaptureContentType.Code, _classifier.Classify(text));
    }

    [Fact]
    public void Python缩进与def判为代码()
    {
        var text = "def add(a, b):\n    return a + b";
        Assert.Equal(CaptureContentType.Code, _classifier.Classify(text));
    }

    [Fact]
    public void 只命中一个代码特征时不算代码()
    {
        // 只命中 `[{};]` 一条 → score = 1 < 2。
        Assert.NotEqual(CaptureContentType.Code, _classifier.Classify("just a { brace"));
    }

    [Fact]
    public void 花括号加缩进两条命中算代码()
    {
        Assert.Equal(CaptureContentType.Code, _classifier.Classify("{\n    body"));
    }

    [Fact]
    public void 箭头函数签名算代码()
    {
        // 第 5 个特征是 \w+\s*\([^\n]*\)\s*(\{|->|:)。
        // "function" 命中第 2 组，"->" 命中第 5 组 → 2 条。
        Assert.Equal(CaptureContentType.Code, _classifier.Classify("function compute(a) -> b"));
    }

    [Fact]
    public void 只有函数名加箭头不算代码()
    {
        // 缺少第二个特征 → 只有第 5 组命中，score = 1 < 2。
        Assert.NotEqual(CaptureContentType.Code, _classifier.Classify("compute(a) -> b"));
    }

    // ── 判定顺序 ───────────────────────────────────────────────────

    [Fact]
    public void URL优先于表格()
    {
        // 单行 URL 同时也能被表格判据（空白分隔多列）命中，但 qrCode 在前。
        var text = "https://example.com/a  b";
        Assert.Equal(CaptureContentType.QrCode, _classifier.Classify(text));
    }

    [Fact]
    public void 表格优先于公式()
    {
        // 含 ≈ 的表格：table 判据在前。
        var text = "a\t≈\tb\t≈\nc\t≈\td\t≈";
        Assert.Equal(CaptureContentType.Table, _classifier.Classify(text));
    }

    [Fact]
    public void 公式优先于代码()
    {
        // 同时含 LaTeX 命令与花括号/缩进 → formula 在前。
        var text = "\\begin{aligned}\n  \\alpha\n\\end{aligned}";
        Assert.Equal(CaptureContentType.Formula, _classifier.Classify(text));
    }

    [Fact]
    public void 纯中文正文判为plainText()
        => Assert.Equal(CaptureContentType.PlainText, _classifier.Classify("这是一段普通的中文说明文字。"));

    [Fact]
    public void 纯英文散文判为plainText()
        => Assert.Equal(
            CaptureContentType.PlainText,
            _classifier.Classify("This is a plain English paragraph without any special markers."));

    [Fact]
    public void 中文截图正文判为plainText()
    {
        var text = "会议纪要\n\n下周一起开始新的迭代，请注意更新各自的任务看板。";
        Assert.Equal(CaptureContentType.PlainText, _classifier.Classify(text));
    }
}
