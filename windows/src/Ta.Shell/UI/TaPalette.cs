using System.Drawing;

namespace Ta.Shell.UI;

/// <summary>
/// 品牌调色板。逐字对齐移植参考文档 §12.2（Mac 版 <c>TaPalette</c>，TaPalette.swift:11-18）。
///
/// ⚠️ RGB 值是精确契约，不能「看着差不多」就调整 —— 朱砂是品牌主色，
/// 出现在托盘徽章、结果条拓字、菜单强调项上，改动会被一眼看出。
/// </summary>
public static class TaPalette
{
    /// <summary>ink —— 主文字色 <c>rgb(26, 26, 26)</c> = #1A1A1A。</summary>
    public static readonly Color Ink = Color.FromArgb(255, 26, 26, 26);

    /// <summary>paper —— 面板底色 <c>rgb(250, 246, 238)</c> = #FAF6EE。</summary>
    public static readonly Color Paper = Color.FromArgb(255, 250, 246, 238);

    /// <summary>elevatedPaper —— 徽章底色 <c>rgb(255, 253, 248)</c> = #FFFDF8。</summary>
    public static readonly Color ElevatedPaper = Color.FromArgb(255, 255, 253, 248);

    /// <summary>cinnabar —— 品牌主色（朱砂）<c>rgb(214, 64, 47)</c> = #D6402F。</summary>
    public static readonly Color Cinnabar = Color.FromArgb(255, 214, 64, 47);

    /// <summary>mutedInk —— 次要文字 <c>rgb(104, 100, 94)</c> = #68645E。</summary>
    public static readonly Color MutedInk = Color.FromArgb(255, 104, 100, 94);

    /// <summary>
    /// hairline —— 发丝描边，ink @ 0.10 透明度。
    /// 对应 Mac 版 <c>TaPalette.hairline = ink @ 0.10</c>。
    /// </summary>
    public static readonly Color Hairline = Color.FromArgb(26, 26, 26, 26);

    /// <summary>悬停底色：朱砂 @ 0.07。对应 MenuBarContentView.swift:199。</summary>
    public static readonly Color CinnabarHover = Color.FromArgb(18, 214, 64, 47);

    /// <summary>强调项底色：ink 不透明。对应 MenuBarContentView.swift:198。</summary>
    public static readonly Color InkEmphasis = Color.FromArgb(255, 26, 26, 26);

    /// <summary>强调项描边：朱砂 @ 0.20。对应 MenuBarContentView.swift:205。</summary>
    public static readonly Color CinnabarBorder = Color.FromArgb(51, 214, 64, 47);

    /// <summary>关闭按钮底色：ink @ 0.055。对应 ResultBarView.swift:100。</summary>
    public static readonly Color InkCloseButton = Color.FromArgb(14, 26, 26, 26);

    // ─────────────────────────────────────────────────────────────────────────
    // 品牌
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>中文名（TaDesignSystem.swift）。</summary>
    public const string BrandName = "拓";

    /// <summary>英文名。</summary>
    public const string BrandEnglishName = "Ta";

    /// <summary>Tagline。</summary>
    public const string Tagline = "把屏幕上的信息，拓下来。";

    /// <summary>
    /// 标题字体。对应 Mac 版 <c>.system(.headline, design: .serif, weight: .bold)</c>
    /// （参考文档 §12.3）。
    ///
    /// Windows 上等价的中文衬线粗体是「宋体」族的 <c>SimSun</c>（加粗），
    /// 但对中文而言宋体加粗笔画极细、在 14pt 下可读性差；
    /// 因此这里选 <c>Microsoft YaHei</c> 的粗体作为视觉最接近的替代，
    /// 并在 <see cref="BrandSerifGlyph"/> 单独用宋体渲染「拓」这个单字徽章
    /// （单字、字号大，宋体的衬线感才能体现出来）。
    /// </summary>
    public static Font BrandTitleFont(double size) => new("Microsoft YaHei", (float)size, FontStyle.Bold, GraphicsUnit.Point);

    /// <summary>单字徽章用的衬线体。渲染 38pt 的「拓」与 22×22 的小徽章。</summary>
    public static Font BrandSerifFont(double size) => new("SimSun", (float)size, FontStyle.Bold, GraphicsUnit.Point);

    /// <summary>正文。</summary>
    public static Font BodyFont(double size, FontStyle style = FontStyle.Regular) =>
        new("Microsoft YaHei", (float)size, style, GraphicsUnit.Point);
}
