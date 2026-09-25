using Ta.Translate;

namespace Ta.Translate.Tests;

/// <summary>
/// 验收 3：字号拟合 —— 给定窄矩形，字号从初值递减到能装下且不低于 7。
/// 对应 Mac: TranslatedImageRenderer.drawFittedText（:193-211）。
///
/// ⚠️ 具体拟合到的字号取决于字体度量（GDI/Segoe UI/微软雅黑），因此断言用
/// 「不变量」而非具体数值：≤ 初值、≥ 7、装得下、且 +1pt 就装不下。
/// </summary>
public class FontFittingTests
{
    private const string Text = "这是一段比较长的中文翻译文本用来测试拟合";

    [Fact]
    public void 窄矩形内字号从初值递减到适配()
    {
        // 初值 = min(72, max(8, rect.height * 0.72)) = min(72, 21.6) = 21.6
        var rectWidth = 200d;
        var rectHeight = 30d;
        var initial = Math.Min(72, Math.Max(8, rectHeight * 0.72));

        var fitted = TranslatedImageRenderer.FitFontSize(Text, rectWidth, rectHeight);

        // 从初值递减（不高于初值）……
        Assert.True(fitted <= initial, $"拟合字号 {fitted} 不应高于初值 {initial}。");

        // ……且不低于 7。
        Assert.True(fitted >= 7, $"拟合字号 {fitted} 不应低于 7。");

        // 在该字号下装得下（容差 ±0.5，Mac: :202）。
        var (width, height) = TranslatedImageRenderer.MeasureText(Text, rectWidth, fitted);
        Assert.True(width <= rectWidth + 0.5, $"宽度 {width} 超出 {rectWidth}。");
        Assert.True(height <= rectHeight + 0.5, $"高度 {height} 超出 {rectHeight}。");
    }

    [Fact]
    public void 拟合是刚好适配_大1pt就装不下()
    {
        // 构造一个能停在 7 以上某处的矩形，验证循环停在「首个适配字号」而不是撞到下限。
        var rectWidth = 200d;
        var rectHeight = 40d;
        var initial = Math.Min(72, Math.Max(8, rectHeight * 0.72));

        var fitted = TranslatedImageRenderer.FitFontSize(Text, rectWidth, rectHeight);
        Assert.True(fitted < initial, "该矩形下初值应当装不下，字号必须递减。");
        Assert.True(fitted > 7, "构造的矩形应当能停在 7 以上，以验证「刚好适配」语义。");

        // 大 1pt 就装不下 —— 证明循环是「首个适配即停」。
        var (widthPlus, heightPlus) = TranslatedImageRenderer.MeasureText(Text, rectWidth, fitted + 1);
        Assert.True(
            widthPlus > rectWidth + 0.5 || heightPlus > rectHeight + 0.5,
            "适配字号 +1 时应当装不下。");
    }

    [Fact]
    public void 始终装不下时字号停在7()
    {
        // 极窄矩形（宽 20）：无论如何都装不下 → 循环下界 7。
        // 用整数高度（25 → 初值 18 为整数）保证递减序列恰好落在 7 上
        // （初值为小数时最后一步会略低于 7，与 Mac 的 while 循环语义一致）。
        var fitted = TranslatedImageRenderer.FitFontSize(Text, 20, 25);

        Assert.True(fitted >= 7);
        Assert.Equal(7, fitted, 6);
    }

    [Fact]
    public void 足够宽时保持初值不再缩小()
    {
        // 宽高都充裕：初值即适配，字号不应被缩小。
        var rectWidth = 800d;
        var rectHeight = 400d;
        var initial = Math.Min(72, Math.Max(8, rectHeight * 0.72));

        var fitted = TranslatedImageRenderer.FitFontSize(Text, rectWidth, rectHeight);

        Assert.Equal(initial, fitted, 6);
    }

    [Fact]
    public void 拉丁文字同样走拟合循环()
    {
        var fitted = TranslatedImageRenderer.FitFontSize(
            "A longer English sentence that must shrink to fit the narrow box",
            160,
            40);

        var initial = Math.Min(72, Math.Max(8, 40 * 0.72));
        Assert.True(fitted <= initial);
        Assert.True(fitted >= 7);

        var (width, height) = TranslatedImageRenderer.MeasureText(
            "A longer English sentence that must shrink to fit the narrow box",
            160,
            fitted);
        Assert.True(width <= 160 + 0.5);
        Assert.True(height <= 40 + 0.5);
    }
}
