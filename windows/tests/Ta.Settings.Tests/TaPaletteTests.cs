using System.Windows.Media;
using Ta.Settings.Brand;

namespace Ta.Settings.Tests;

/// <summary>
/// 验收 1：六个品牌色逐值断言。
///
/// 期望值来自参考文档 §12.2「调色板（TaPalette，:11-18）—— ⚠️ 精确 RGB」，
/// 也就是 macOS <c>TaPalette</c>（TaDesignSystem.swift:11-18）里写死的六个 rgb()。
/// </summary>
public sealed class TaPaletteTests
{
    /// <summary>把文档里的十六进制串转成期望的 Color。</summary>
    private static Color Expected(string hex)
    {
        var value = Convert.ToUInt32(hex.TrimStart('#'), 16);
        return Color.FromRgb((byte)(value >> 16), (byte)((value >> 8) & 0xFF), (byte)(value & 0xFF));
    }

    /// <summary>ink 必须精确等于 #1A1A1A。</summary>
    [Fact]
    public void Ink_IsExactDocumentValue() => Assert.Equal(Expected("#1A1A1A"), TaPalette.Ink);

    /// <summary>paper 必须精确等于 #FAF6EE。</summary>
    [Fact]
    public void Paper_IsExactDocumentValue() => Assert.Equal(Expected("#FAF6EE"), TaPalette.Paper);

    /// <summary>elevatedPaper 必须精确等于 #FFFDF8。</summary>
    [Fact]
    public void ElevatedPaper_IsExactDocumentValue()
        => Assert.Equal(Expected("#FFFDF8"), TaPalette.ElevatedPaper);

    /// <summary>cinnabar（品牌主色）必须精确等于 #D6402F。</summary>
    [Fact]
    public void Cinnabar_IsExactDocumentValue() => Assert.Equal(Expected("#D6402F"), TaPalette.Cinnabar);

    /// <summary>mutedInk 必须精确等于 #68645E。</summary>
    [Fact]
    public void MutedInk_IsExactDocumentValue() => Assert.Equal(Expected("#68645E"), TaPalette.MutedInk);

    /// <summary>
    /// hairline 是 <c>ink.opacity(0.10)</c>，因此 RGB 与 ink 相同、alpha = 26/255。
    /// </summary>
    [Fact]
    public void Hairline_IsInkAtTenPercentOpacity()
    {
        Assert.Equal(TaPalette.Ink.R, TaPalette.Hairline.R);
        Assert.Equal(TaPalette.Ink.G, TaPalette.Hairline.G);
        Assert.Equal(TaPalette.Ink.B, TaPalette.Hairline.B);
        Assert.Equal(26, TaPalette.Hairline.A);
    }

    /// <summary>hairline（ink @ 10%）叠加到 paper #FAF6EE 上应该得到 #E3E0D8（source-over 合成）。</summary>
    [Fact]
    public void Hairline_FlattenedOverPaper_IsE3E0D8()
    {
        var flattened = TaPalette.Flatten(TaPalette.Hairline, TaPalette.Paper);
        Assert.Equal(Expected("#E3E0D8"), flattened);
    }

    /// <summary>品牌名与 tagline 必须逐字一致（参考文档 §12.1）。</summary>
    [Fact]
    public void BrandStrings_AreExact()
    {
        Assert.Equal("拓", TaBrand.Name);
        Assert.Equal("Ta", TaBrand.EnglishName);
        Assert.Equal("把屏幕上的信息，拓下来。", TaBrand.Tagline);
        Assert.Equal("截图、识别、翻译与标注，一步完成", TaBrand.ProductDescription);
    }

    /// <summary>标题衬线体必须落到一个真的装在本机上的中文字体。</summary>
    [Fact]
    public void SerifFamily_ResolvesToAnInstalledChineseFont()
    {
        var installed = Fonts.SystemFontFamilies.Select(f => f.Source).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.Contains(TaTypography.SerifFamily.Source, installed);
        Assert.Contains(TaTypography.SerifFamily.Source, TaTypography.SerifCandidates);
    }
}
